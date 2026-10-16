using Flagon.Storage;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Flagon.Internal;

/// <summary>
/// Loads the initial snapshot when the host starts, then polls the store every <see cref="FlagonOptions.RefreshInterval"/>.
/// A store outage at startup does not block the host unless <see cref="FlagonOptions.FailFastOnStartup"/> is set.
/// </summary>
internal sealed partial class FlagonHostedService(
    FlagSnapshotManager snapshots,
    IFlagStore store,
    FlagonOptions options,
    TimeProvider time,
    ILogger<FlagonHostedService> logger) : IHostedService, IAsyncDisposable
{
    private readonly CancellationTokenSource _stopping = new();
    private PeriodicTimer? _timer;
    private Task? _polling;
    private bool _migrated = !options.AutoMigrate;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        // Create the timer before the first await so its schedule starts at host start, not after the initial load.
        _timer = new PeriodicTimer(options.RefreshInterval, time);

        try
        {
            await MigrateAndRefreshAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (!(ex is OperationCanceledException && cancellationToken.IsCancellationRequested))
        {
            if (options.FailFastOnStartup)
            {
                _timer.Dispose();
                throw new FlagonException(
                    "Flagon could not load flags from the store at startup and FailFastOnStartup is enabled.", ex);
            }

            LogStartingWithoutFlags(logger, options.RefreshInterval);
        }

        _polling = PollAsync(_timer, _stopping.Token);
    }

    private async Task PollAsync(PeriodicTimer timer, CancellationToken stoppingToken)
    {
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                try
                {
                    await MigrateAndRefreshAsync(stoppingToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Already recorded and logged by the snapshot manager; keep serving last-known-good and retry next tick.
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    private async Task MigrateAndRefreshAsync(CancellationToken cancellationToken)
    {
        if (!_migrated)
        {
            try
            {
                await store.ApplyMigrationsAsync(cancellationToken).ConfigureAwait(false);
                _migrated = true;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogMigrationFailed(logger, ex);
                // Refresh anyway so the snapshot status reflects the store's reachability (and an already-installed
                // schema keeps serving); the migration is retried on the next tick.
                await snapshots.RefreshAsync(cancellationToken).ConfigureAwait(false);
                throw;
            }
        }

        await snapshots.RefreshAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_polling is null)
        {
            return;
        }

        await _stopping.CancelAsync().ConfigureAwait(false);
        await _polling.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    public ValueTask DisposeAsync()
    {
        _stopping.Cancel();
        _timer?.Dispose();
        _stopping.Dispose();
        return ValueTask.CompletedTask;
    }

    [LoggerMessage(EventId = 1103, Level = LogLevel.Warning,
        Message = "Flagon could not load flags from the store at startup; starting with code defaults and retrying every {RefreshInterval}.")]
    private static partial void LogStartingWithoutFlags(ILogger logger, TimeSpan refreshInterval);

    [LoggerMessage(EventId = 1104, Level = LogLevel.Warning, Message = "Flagon could not apply store migrations; they will be retried on the next refresh.")]
    private static partial void LogMigrationFailed(ILogger logger, Exception exception);
}
