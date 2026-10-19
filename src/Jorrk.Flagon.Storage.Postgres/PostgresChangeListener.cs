using Microsoft.Extensions.Logging;
using Npgsql;

namespace Jorrk.Flagon.Storage.Postgres;

/// <summary>
/// Holds one dedicated, unpooled <c>LISTEN</c> connection for a store and keeps it alive. A failure of the connection
/// (including a failed keepalive) reports it as disconnected, then reconnects with <see cref="ReconnectBackoff"/>,
/// re-issues <c>LISTEN</c> and signals a resubscribe so the app refreshes the changes it may have missed.
/// </summary>
internal sealed partial class PostgresChangeListener
{
    private readonly string _connectionString;
    private readonly string _channel;
    private readonly TimeSpan _keepaliveInterval;
    private readonly ILogger _logger;
    private readonly Action<bool> _connectionChanged;

    /// <param name="connectionString">An unpooled connection string: a pooled connection would keep its LISTEN after reuse.</param>
    /// <param name="channel">The notification channel, already validated.</param>
    /// <param name="keepaliveInterval">How long the connection may stay silent before a <c>SELECT 1</c> checks it.</param>
    /// <param name="logger">Receives connect, disconnect, backoff and notification logs.</param>
    /// <param name="connectionChanged">Called with true on each connect and false on each disconnect (or stop).</param>
    public PostgresChangeListener(string connectionString, string channel, TimeSpan keepaliveInterval, ILogger logger, Action<bool> connectionChanged)
    {
        _connectionString = connectionString;
        _channel = channel;
        _keepaliveInterval = keepaliveInterval;
        _logger = logger;
        _connectionChanged = connectionChanged;
    }

    /// <summary>
    /// Connects and listens before returning, so a write that commits after this returns is delivered. A failure here is
    /// thrown to the caller; after this, failures are retried in the background until <paramref name="stop"/> is cancelled.
    /// </summary>
    /// <remarks>Takes ownership of <paramref name="stop"/>: it is disposed when the listener ends.</remarks>
    public async Task StartAsync(Action<FlagChangeSignal> onChanged, CancellationTokenSource stop)
    {
        NpgsqlConnection connection;
        try
        {
            connection = await ConnectAsync(onChanged, stop.Token).ConfigureAwait(false);
        }
        catch
        {
            stop.Dispose();
            throw;
        }

        _connectionChanged(true);
        LogConnected(_logger, _channel);
        _ = RunAsync(connection, onChanged, stop);
    }

    private async Task<NpgsqlConnection> ConnectAsync(Action<FlagChangeSignal> onChanged, CancellationToken stop)
    {
        var connection = new NpgsqlConnection(_connectionString);
        try
        {
            connection.Notification += (_, e) =>
            {
                if (stop.IsCancellationRequested)
                {
                    return;
                }

                LogNotification(_logger, e.Payload);
                // A malformed payload still means a change: signal without a commit time rather than drop it.
                var committedAt = PostgresChangePayload.TryParse(e.Payload, out long _, out var commitTime) ? commitTime : (DateTimeOffset?)null;
                onChanged(new FlagChangeSignal(committedAt));
            };
            await connection.OpenAsync(stop).ConfigureAwait(false);
            await new NpgsqlCommand($"LISTEN \"{_channel}\"", connection).ExecuteNonQueryAsync(stop).ConfigureAwait(false);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>Pumps the connection, reconnecting after any failure, until stopped. Never throws.</summary>
    private async Task RunAsync(NpgsqlConnection? connection, Action<FlagChangeSignal> onChanged, CancellationTokenSource stop)
    {
        var connected = connection is not null;
        try
        {
            while (true)
            {
                if (connection is null)
                {
                    connection = await ReconnectAsync(onChanged, stop.Token).ConfigureAwait(false);
                    if (connection is null)
                    {
                        return;
                    }

                    connected = true;
                    _connectionChanged(true);
                    LogConnected(_logger, _channel);
                    // Notifications committed while disconnected were not delivered: refresh once now.
                    onChanged(new FlagChangeSignal(Resubscribed: true));
                }

                try
                {
                    await PumpAsync(connection, stop.Token).ConfigureAwait(false);
                }
                catch (Exception ex) when (!stop.IsCancellationRequested)
                {
                    connected = false;
                    _connectionChanged(false);
                    LogDisconnected(_logger, ex, _channel);
                    await connection.DisposeAsync().ConfigureAwait(false);
                    connection = null;
                }
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
            // Stopped: the listener ends here and the connection is released below.
        }
        finally
        {
            if (connection is not null)
            {
                await connection.DisposeAsync().ConfigureAwait(false);
            }

            if (connected)
            {
                _connectionChanged(false);
            }

            stop.Dispose();
        }
    }

    /// <summary>Waits for notifications. Each quiet keepalive interval is answered with <c>SELECT 1</c>, which fails on a half-open connection.</summary>
    private async Task PumpAsync(NpgsqlConnection connection, CancellationToken stop)
    {
        while (true)
        {
            using var idle = CancellationTokenSource.CreateLinkedTokenSource(stop);
            idle.CancelAfter(_keepaliveInterval);
            try
            {
                await connection.WaitAsync(idle.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!stop.IsCancellationRequested)
            {
                await using var keepalive = new NpgsqlCommand("SELECT 1", connection);
                await keepalive.ExecuteScalarAsync(stop).ConfigureAwait(false);
            }
        }
    }

    /// <summary>Reconnects with backoff until connected (returns the connection) or stopped (returns null).</summary>
    private async Task<NpgsqlConnection?> ReconnectAsync(Action<FlagChangeSignal> onChanged, CancellationToken stop)
    {
        Exception? lastError = null;
        for (var attempt = 0; ; attempt++)
        {
            var delay = ReconnectBackoff.Delay(attempt, Random.Shared);
            LogBackingOff(_logger, lastError, attempt + 1, delay);
            try
            {
                await Task.Delay(delay, stop).ConfigureAwait(false);
                return await ConnectAsync(onChanged, stop).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested)
            {
                return null;
            }
            catch (Exception ex)
            {
                lastError = ex;
            }
        }
    }

    [LoggerMessage(EventId = 1107, Level = LogLevel.Information, Message = "Flag change listener connected and listening on channel {Channel}.")]
    private static partial void LogConnected(ILogger logger, string channel);

    [LoggerMessage(EventId = 1108, Level = LogLevel.Information, Message = "Flag change listener on channel {Channel} lost its connection; it will reconnect.")]
    private static partial void LogDisconnected(ILogger logger, Exception exception, string channel);

    [LoggerMessage(EventId = 1109, Level = LogLevel.Warning, Message = "Flag change listener is not connected; reconnect attempt {Attempt} in {Delay}.")]
    private static partial void LogBackingOff(ILogger logger, Exception? exception, int attempt, TimeSpan delay);

    [LoggerMessage(EventId = 1110, Level = LogLevel.Debug, Message = "Flag change notification received: {Payload}.")]
    private static partial void LogNotification(ILogger logger, string payload);
}
