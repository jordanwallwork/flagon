using System.Threading.Channels;
using Jorrk.Flagon.Storage;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jorrk.Flagon.Internal;

/// <summary>
/// Runs the <see cref="FlagonStartup"/> sequence when the host starts (validate, migrate, seed, load the initial
/// snapshot), then polls the store every <see cref="FlagonOptions.RefreshInterval"/>, retrying any startup step that
/// failed before each refresh. A store outage at startup does not block the host unless
/// <see cref="FlagonOptions.FailFastOnStartup"/> is set.
/// </summary>
/// <remarks>
/// When the store implements <see cref="IFlagChangeNotifier"/> and <see cref="FlagonOptions.PushRefresh"/> is on, the
/// notifier is started after the startup sequence and each signal it raises queues one refresh. Queued refreshes are
/// debounced by <see cref="FlagonOptions.PushMinInterval"/>, so a burst becomes one refresh. A push refresh runs the same
/// path as a tick and does not reset the poll timer; the timer remains the backstop.
/// </remarks>
internal sealed partial class FlagonHostedService(
    FlagonStartup startup,
    FlagSnapshotManager snapshots,
    FlagonOptions options,
    IFlagStore store,
    FlagonInstrumentation instrumentation,
    TimeProvider time,
    ILogger<FlagonHostedService> logger) : IHostedService, IAsyncDisposable
{
    private readonly CancellationTokenSource _stopping = new();
    private readonly CancellationTokenSource _pushStopping = new();
    // The earliest commit time among signals not yet covered by a refresh: the lag of the next applied refresh is measured from it.
    private readonly object _pendingCommitGate = new();
    private DateTimeOffset? _pendingCommit;
    private PeriodicTimer? _timer;
    private Task? _polling;
    private bool _pushStarted;

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

        var pushSignals = options.PushRefresh && store is IFlagChangeNotifier
            // A single pending slot: TryWrite on a full channel returns false, which is how a signal is counted as coalesced.
            ? Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = true,
            })
            : null;

        _polling = PollAsync(_timer, pushSignals?.Reader, _stopping.Token);

        if (pushSignals is not null)
        {
            _pushStarted = true;
            try
            {
                // The listener lives on _pushStopping; startup cancellation only abandons waiting for its first connect.
                await StartPushAsync((IFlagChangeNotifier)store, pushSignals.Writer).WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Startup was abandoned: stop the listener and the poll loop that is already running.
                await _pushStopping.CancelAsync().ConfigureAwait(false);
                await _stopping.CancelAsync().ConfigureAwait(false);
                _timer.Dispose();
                throw;
            }
        }
    }

    private async Task StartPushAsync(IFlagChangeNotifier notifier, ChannelWriter<bool> signals)
    {
        try
        {
            await notifier.StartAsync(signal =>
            {
                if (signal.Resubscribed)
                {
                    // Not a change: the listener came back, so refresh to pick up whatever it missed.
                    instrumentation.RecordPushReconnect();
                }
                else
                {
                    snapshots.RecordPush();
                }

                if (signal.CommittedAt is { } committedAt)
                {
                    RememberCommit(committedAt);
                }

                var queued = signals.TryWrite(true);
                if (!signal.Resubscribed)
                {
                    instrumentation.RecordPushNotification(coalesced: !queued);
                }
            }, _pushStopping.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogPushUnavailable(logger, ex, options.RefreshInterval);
        }
    }

    private async Task PollAsync(PeriodicTimer timer, ChannelReader<bool>? pushes, CancellationToken stoppingToken)
    {
        try
        {
            if (pushes is null)
            {
                while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
                {
                    await RefreshAsync(stoppingToken).ConfigureAwait(false);
                }

                return;
            }

            // The waits are kept across iterations: the timer must keep its own schedule when a push wins, and a
            // PeriodicTimer tick must not be waited on twice at once. A pending debounce is awaited alongside the tick, so
            // a long PushMinInterval never holds up polling.
            Task<bool>? tick = null;
            Task<bool>? push = null;
            Task? debounce = null;
            while (true)
            {
                tick ??= timer.WaitForNextTickAsync(stoppingToken).AsTask();
                // While a debounce is pending the slot stays full, so a wait to read would complete at once: skip it.
                push ??= debounce is null ? pushes.WaitToReadAsync(stoppingToken).AsTask() : null;
                await Task.WhenAny(tick, debounce ?? push!).ConfigureAwait(false);

                if (tick.IsCompleted)
                {
                    var ticked = await tick.ConfigureAwait(false);
                    tick = null;
                    if (!ticked)
                    {
                        return;
                    }

                    await RefreshAsync(stoppingToken).ConfigureAwait(false);
                }
                else if (debounce is not null)
                {
                    // The burst has had PushMinInterval to arrive (it coalesced into the slot): take the slot and refresh.
                    await debounce.ConfigureAwait(false);
                    debounce = null;
                    pushes.TryRead(out _);
                    await RefreshAsync(stoppingToken).ConfigureAwait(false);
                }
                else
                {
                    await push!.ConfigureAwait(false);
                    push = null;
                    debounce = Task.Delay(options.PushMinInterval, time, stoppingToken);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    /// <summary>The refresh shared by ticks and pushes: retry failed startup steps, then reload the snapshot if the version moved.</summary>
    private async Task RefreshAsync(CancellationToken stoppingToken)
    {
        await startup.RetryPendingAsync(stoppingToken).ConfigureAwait(false);

        // Taken before the refresh, so a commit that arrives while it runs stays pending for the next one.
        DateTimeOffset? committedAt;
        lock (_pendingCommitGate)
        {
            committedAt = _pendingCommit;
            _pendingCommit = null;
        }

        try
        {
            var outcome = await snapshots.RefreshAsync(stoppingToken).ConfigureAwait(false);
            if (outcome == RefreshOutcome.Reloaded && committedAt is { } commit)
            {
                instrumentation.RecordPushLag(time.GetUtcNow() - commit);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Already recorded and logged by the snapshot manager; keep serving last-known-good and retry next tick.
            // The change is still unapplied, so keep its commit time for the refresh that does apply it.
            if (committedAt is { } commit)
            {
                RememberCommit(commit);
            }
        }
    }

    /// <summary>Keeps the earliest commit time not yet covered by an applied refresh.</summary>
    private void RememberCommit(DateTimeOffset committedAt)
    {
        lock (_pendingCommitGate)
        {
            _pendingCommit = _pendingCommit is { } pending && pending <= committedAt ? pending : committedAt;
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_polling is null)
        {
            return;
        }

        // Stop the notifier first so no further signals are queued, wait for its connections to close, then end the poll loop.
        // Only this host's listener is waited on: another host may share the store.
        await _pushStopping.CancelAsync().ConfigureAwait(false);
        if (_pushStarted && store is IFlagChangeNotifierTeardown teardown)
        {
            await teardown.WhenStoppedAsync(_pushStopping.Token).WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        await _stopping.CancelAsync().ConfigureAwait(false);
        await _polling.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    public ValueTask DisposeAsync()
    {
        _stopping.Cancel();
        _pushStopping.Cancel();
        _timer?.Dispose();
        _stopping.Dispose();
        _pushStopping.Dispose();
        return ValueTask.CompletedTask;
    }

    [LoggerMessage(EventId = 1111, Level = LogLevel.Warning,
        Message = "Flagon could not start listening for flag changes in the store; relying on polling every {RefreshInterval}.")]
    private static partial void LogPushUnavailable(ILogger logger, Exception exception, TimeSpan refreshInterval);
}
