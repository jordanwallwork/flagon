using System.Collections.Concurrent;
using System.Text.Json;
using Flagon.Storage;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Flagon.Internal;

/// <summary>
/// Aggregates evaluations in memory — per key, the latest evaluation time and the code default supplied — and writes
/// them to the store in batches. Recording an already-seen key is lock- and allocation-free.
/// Disabled when <see cref="FlagonOptions.UsageTracking"/> is off or the store is read-only.
/// </summary>
internal sealed partial class UsageTracker(
    IFlagStore store,
    FlagonOptions options,
    TimeProvider time,
    FlagChangeStamper changes,
    FlagonInstrumentation instrumentation,
    ILogger<UsageTracker> logger)
{
    private static readonly JsonElement True = JsonSerializer.SerializeToElement(true);
    private static readonly JsonElement False = JsonSerializer.SerializeToElement(false);

    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _flushGate = new(1, 1);

    public bool IsEnabled { get; } = options.UsageTracking && !store.IsReadOnly;

    /// <summary>Records that <paramref name="key"/> was evaluated now with <paramref name="defaultValue"/> as the code default.</summary>
    public void Record(string? key, bool defaultValue)
    {
        if (!IsEnabled || key is null)
        {
            return;
        }

        if (!_entries.TryGetValue(key, out var entry))
        {
            // Only track keys that could be flags, so arbitrary strings can't grow the aggregate or reach the store.
            if (!FlagKey.IsValid(key))
            {
                return;
            }

            entry = _entries.GetOrAdd(key, static _ => new Entry());
        }

        // Fields are written independently: a flush racing this call may pair a new time with an old default (or vice
        // versa), but Dirty is set last, so the latest values are always written by the following flush.
        Volatile.Write(ref entry.UtcTicks, time.GetUtcNow().UtcTicks);
        Volatile.Write(ref entry.Default, defaultValue ? 1 : 0);
        Volatile.Write(ref entry.Dirty, 1);
    }

    /// <summary>Writes everything recorded since the last successful flush. On failure the entries stay pending and the error is rethrown.</summary>
    public async Task FlushAsync(CancellationToken cancellationToken)
    {
        if (!IsEnabled)
        {
            return;
        }

        await _flushGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var batch = new List<FlagUsage>();
            var taken = new List<Entry>();
            foreach (var (key, entry) in _entries)
            {
                if (Interlocked.Exchange(ref entry.Dirty, 0) == 1)
                {
                    var at = new DateTimeOffset(Volatile.Read(ref entry.UtcTicks), TimeSpan.Zero);
                    var observedDefault = Volatile.Read(ref entry.Default) == 1 ? True : False;
                    batch.Add(new FlagUsage(key, changes.ApplicationName, at, observedDefault));
                    taken.Add(entry);
                }
            }

            if (batch.Count == 0)
            {
                return;
            }

            try
            {
                await instrumentation.TraceStoreOperationAsync("usage_flush", flagKey: null, async activity =>
                {
                    activity?.SetTag("flagon.usage.flags", batch.Count);
                    await store.UpsertUsageAsync(batch, cancellationToken).ConfigureAwait(false);
                    return true;
                }).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                foreach (var entry in taken)
                {
                    Volatile.Write(ref entry.Dirty, 1);
                }

                LogFlushFailed(logger, ex, batch.Count);
                throw;
            }
        }
        finally
        {
            _flushGate.Release();
        }
    }

    [LoggerMessage(EventId = 1501, Level = LogLevel.Warning, Message = "Flagon could not write usage for {Count} flag(s) to the store; it will be retried at the next flush.")]
    private static partial void LogFlushFailed(ILogger logger, Exception exception, int count);

    private sealed class Entry
    {
        public long UtcTicks;
        public int Default;
        public int Dirty;
    }
}

/// <summary>Flushes <see cref="UsageTracker"/> every <see cref="FlagonOptions.UsageFlushInterval"/> and once more on graceful shutdown.</summary>
internal sealed class UsageFlushService(UsageTracker tracker, FlagonOptions options, TimeProvider time) : IHostedService, IAsyncDisposable
{
    private readonly CancellationTokenSource _stopping = new();
    private PeriodicTimer? _timer;
    private Task? _flushing;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (tracker.IsEnabled)
        {
            _timer = new PeriodicTimer(options.UsageFlushInterval, time);
            _flushing = FlushPeriodicallyAsync(_timer, _stopping.Token);
        }

        return Task.CompletedTask;
    }

    private async Task FlushPeriodicallyAsync(PeriodicTimer timer, CancellationToken stoppingToken)
    {
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                await FlushQuietlyAsync(stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_flushing is null)
        {
            return;
        }

        await _stopping.CancelAsync().ConfigureAwait(false);
        await _flushing.WaitAsync(cancellationToken).ConfigureAwait(false);
        await FlushQuietlyAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task FlushQuietlyAsync(CancellationToken cancellationToken)
    {
        try
        {
            await tracker.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Logged by the tracker; entries stay pending for the next flush.
        }
    }

    public ValueTask DisposeAsync()
    {
        _stopping.Cancel();
        _timer?.Dispose();
        _stopping.Dispose();
        return ValueTask.CompletedTask;
    }
}
