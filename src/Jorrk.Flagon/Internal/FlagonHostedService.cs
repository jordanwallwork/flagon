using Microsoft.Extensions.Hosting;

namespace Jorrk.Flagon.Internal;

/// <summary>
/// Runs the <see cref="FlagonStartup"/> sequence when the host starts (validate, migrate, seed, load the initial
/// snapshot), then polls the store every <see cref="FlagonOptions.RefreshInterval"/>, retrying any startup step that
/// failed before each refresh. A store outage at startup does not block the host unless
/// <see cref="FlagonOptions.FailFastOnStartup"/> is set.
/// </summary>
internal sealed class FlagonHostedService(
    FlagonStartup startup,
    FlagSnapshotManager snapshots,
    FlagonOptions options,
    TimeProvider time) : IHostedService, IAsyncDisposable
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
            await startup.RunAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            _timer.Dispose();
            throw;
        }

        _polling = PollAsync(_timer, _stopping.Token);
    }

    private async Task PollAsync(PeriodicTimer timer, CancellationToken stoppingToken)
    {
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                await startup.RetryPendingAsync(stoppingToken).ConfigureAwait(false);

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
}
