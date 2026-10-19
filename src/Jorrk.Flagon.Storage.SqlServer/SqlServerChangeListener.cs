using Jorrk.Flagon.Storage.Internal;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;

namespace Jorrk.Flagon.Storage.SqlServer;

/// <summary>
/// Keeps one query-notification subscription on a store's change counter. <c>SqlDependency</c> receives the
/// notifications on a queue it creates for this process, so app instances never consume each other's messages.
/// </summary>
/// <remarks>
/// Notifications fire once, so the listener re-subscribes after each one and signals when the counter it reads has moved
/// since its last read. That catches anything committed between a notification and the re-subscribe. The subscription
/// timeout re-subscribes periodically, which also checks the database is reachable. Any other notification (database
/// offline, invalid subscription) or a failed subscribe reports the listener disconnected. It then reconnects with
/// <see cref="ReconnectBackoff"/> and signals a resubscribe, so the app refreshes what it may have missed.
/// </remarks>
internal sealed partial class SqlServerChangeListener
{
    private readonly string _connectionString;
    private readonly string _counterQuery;
    private readonly TimeSpan _subscriptionTimeout;
    private readonly ILogger _logger;
    private readonly Action<bool> _connectionChanged;
    private bool _dependencyStarted;
    private long _lastVersion;

    /// <param name="connectionString">The store's connection string; <c>SqlDependency</c> keys its listener on it.</param>
    /// <param name="counterQuery">The change-counter query to subscribe to (a valid query-notification shape).</param>
    /// <param name="subscriptionTimeout">How long a subscription lasts before it is renewed.</param>
    /// <param name="logger">Receives connect, disconnect, backoff and notification logs.</param>
    /// <param name="connectionChanged">Called with true on each connect and false on each disconnect (or stop).</param>
    public SqlServerChangeListener(string connectionString, string counterQuery, TimeSpan subscriptionTimeout, ILogger logger, Action<bool> connectionChanged)
    {
        _connectionString = connectionString;
        _counterQuery = counterQuery;
        _subscriptionTimeout = subscriptionTimeout;
        _logger = logger;
        _connectionChanged = connectionChanged;
    }

    /// <summary>
    /// Subscribes before returning, so a write that commits after this returns is delivered. If that first subscribe
    /// fails (no Service Broker, missing permissions, server unreachable), it is not thrown: the listener retries in the
    /// background like any later failure, until <paramref name="stop"/> is cancelled. The returned task completes once
    /// the listener has stopped and released its <c>SqlDependency</c> listener.
    /// </summary>
    /// <remarks>Takes ownership of <paramref name="stop"/>: it is disposed when the listener ends.</remarks>
    public async Task<Task> StartAsync(Action<FlagChangeSignal> onChanged, CancellationTokenSource stop)
    {
        Task<SqlNotificationEventArgs>? subscription = null;
        Exception? initialError = null;
        try
        {
            subscription = await ConnectAsync(stop.Token).ConfigureAwait(false);
            _connectionChanged(true);
            LogConnected(_logger);
        }
        catch (Exception ex) when (!stop.IsCancellationRequested)
        {
            initialError = ex;
        }
        catch
        {
            stop.Dispose();
            throw;
        }

        return RunAsync(subscription, initialError, onChanged, stop);
    }

    /// <summary>Starts the process's <c>SqlDependency</c> listener and subscribes. On failure nothing is left started.</summary>
    private async Task<Task<SqlNotificationEventArgs>> ConnectAsync(CancellationToken stop)
    {
        // Start is synchronous I/O: it creates this process's queue, service and procedure on first use.
        await Task.Run(() => SqlDependency.Start(_connectionString), stop).ConfigureAwait(false);
        _dependencyStarted = true;
        try
        {
            var (subscription, version) = await SubscribeAsync(stop).ConfigureAwait(false);
            _lastVersion = version;
            return subscription;
        }
        catch
        {
            await StopDependencyAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>Reads the counter with a subscription attached. The task completes on the subscription's one notification.</summary>
    private async Task<(Task<SqlNotificationEventArgs> Subscription, long Version)> SubscribeAsync(CancellationToken stop)
    {
        var fired = new TaskCompletionSource<SqlNotificationEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(stop).ConfigureAwait(false);
        await using var command = new SqlCommand(_counterQuery, connection);
        var dependency = new SqlDependency(command, options: null, timeout: (int)_subscriptionTimeout.TotalSeconds);
        dependency.OnChange += (_, e) => fired.TrySetResult(e);
        var version = (long)(await command.ExecuteScalarAsync(stop).ConfigureAwait(false))!;
        return (fired.Task, version);
    }

    /// <summary>Waits for notifications and re-subscribes, reconnecting after any failure, until stopped. Never throws.</summary>
    private async Task RunAsync(Task<SqlNotificationEventArgs>? subscription, Exception? initialError, Action<FlagChangeSignal> onChanged, CancellationTokenSource stop)
    {
        var connected = subscription is not null;
        try
        {
            while (true)
            {
                if (subscription is null)
                {
                    subscription = await ReconnectAsync(initialError, stop.Token).ConfigureAwait(false);
                    initialError = null;
                    if (subscription is null)
                    {
                        return;
                    }

                    connected = true;
                    _connectionChanged(true);
                    LogConnected(_logger);
                    // Changes committed while disconnected were not signalled: refresh once now.
                    Signal(onChanged, new FlagChangeSignal(Resubscribed: true), stop.Token);
                }

                try
                {
                    subscription = await PumpAsync(subscription, onChanged, stop.Token).ConfigureAwait(false);
                }
                catch (Exception ex) when (!stop.IsCancellationRequested)
                {
                    connected = false;
                    _connectionChanged(false);
                    LogDisconnected(_logger, ex);
                    await StopDependencyAsync().ConfigureAwait(false);
                    subscription = null;
                }
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
            // Stopped: the listener ends here and the SqlDependency listener is released below.
        }
        finally
        {
            await StopDependencyAsync().ConfigureAwait(false);
            if (connected)
            {
                _connectionChanged(false);
            }

            stop.Dispose();
        }
    }

    /// <summary>
    /// Handles one notification: re-subscribes and signals if the counter moved. Data changes and subscription timeouts
    /// are normal; anything else throws, which the caller treats as a disconnect.
    /// </summary>
    private async Task<Task<SqlNotificationEventArgs>> PumpAsync(Task<SqlNotificationEventArgs> subscription, Action<FlagChangeSignal> onChanged, CancellationToken stop)
    {
        var e = await subscription.WaitAsync(stop).ConfigureAwait(false);
        LogNotification(_logger, e.Type, e.Info, e.Source);
        if (e.Type != SqlNotificationType.Change || e.Source is not (SqlNotificationSource.Data or SqlNotificationSource.Timeout))
        {
            throw new SqlServerNotificationException(e);
        }

        var (next, version) = await SubscribeAsync(stop).ConfigureAwait(false);
        if (version != _lastVersion)
        {
            _lastVersion = version;
            Signal(onChanged, new FlagChangeSignal(), stop);
        }

        return next;
    }

    /// <summary>Reconnects with backoff until subscribed (returns the subscription) or stopped (returns null).</summary>
    private async Task<Task<SqlNotificationEventArgs>?> ReconnectAsync(Exception? lastError, CancellationToken stop)
    {
        for (var attempt = 0; ; attempt++)
        {
            var delay = ReconnectBackoff.Delay(attempt, Random.Shared);
            LogBackingOff(_logger, lastError, attempt + 1, delay);
            try
            {
                await Task.Delay(delay, stop).ConfigureAwait(false);
                return await ConnectAsync(stop).ConfigureAwait(false);
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

    /// <summary>Balances the <c>SqlDependency.Start</c> made by the last connect, if any.</summary>
    private async Task StopDependencyAsync()
    {
        if (!_dependencyStarted)
        {
            return;
        }

        _dependencyStarted = false;
        try
        {
            await Task.Run(() => SqlDependency.Stop(_connectionString)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // The server may already be gone; its per-process queue then cleans itself up.
            LogStopFailed(_logger, ex);
        }
    }

    private static void Signal(Action<FlagChangeSignal> onChanged, FlagChangeSignal signal, CancellationToken stop)
    {
        if (!stop.IsCancellationRequested)
        {
            onChanged(signal);
        }
    }

    /// <summary>A notification that means the subscription is no longer reliable (database offline, invalid query, ...).</summary>
    private sealed class SqlServerNotificationException(SqlNotificationEventArgs e)
        : Exception($"Query notification {e.Type}/{e.Info}/{e.Source} ended the subscription.");

    [LoggerMessage(EventId = 1112, Level = LogLevel.Information, Message = "SQL Server flag change listener subscribed.")]
    private static partial void LogConnected(ILogger logger);

    [LoggerMessage(EventId = 1113, Level = LogLevel.Information, Message = "SQL Server flag change listener lost its subscription; it will reconnect.")]
    private static partial void LogDisconnected(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 1114, Level = LogLevel.Warning, Message = "SQL Server flag change listener is not subscribed; reconnect attempt {Attempt} in {Delay}. Push needs Service Broker and a db_owner login; polling continues meanwhile.")]
    private static partial void LogBackingOff(ILogger logger, Exception? exception, int attempt, TimeSpan delay);

    [LoggerMessage(EventId = 1115, Level = LogLevel.Debug, Message = "SQL Server query notification received: {Type}/{Info}/{Source}.")]
    private static partial void LogNotification(ILogger logger, SqlNotificationType type, SqlNotificationInfo info, SqlNotificationSource source);

    [LoggerMessage(EventId = 1116, Level = LogLevel.Debug, Message = "SqlDependency.Stop failed while releasing the SQL Server flag change listener.")]
    private static partial void LogStopFailed(ILogger logger, Exception exception);
}
