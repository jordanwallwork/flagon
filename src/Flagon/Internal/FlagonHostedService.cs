using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Flagon.Internal;

/// <summary>
/// Loads the initial snapshot when the host starts, then polls the store every <see cref="FlagonOptions.RefreshInterval"/>.
/// A store outage at startup does not block the host unless <see cref="FlagonOptions.FailFastOnStartup"/> is set.
/// </summary>
internal sealed partial class FlagonHostedService(
    FlagSnapshotManager snapshots,
    FlagonOptions options,
    TimeProvider time,
    ILogger<FlagonHostedService> logger) : IHostedService, IAsyncDisposable
{
    private readonly CancellationTokenSource _stopping = new();
    private PeriodicTimer? _timer;
    private Task? _polling;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        // Create the timer before the first await so its schedule starts at host start, not after the initial load.
        _timer = new PeriodicTimer(options.RefreshInterval, time);

        try
        {
            await snapshots.RefreshAsync(cancellationToken).ConfigureAwait(false);
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
                    await snapshots.RefreshAsync(stoppingToken).ConfigureAwait(false);
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
}
