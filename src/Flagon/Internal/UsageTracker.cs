using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using Flagon.Storage;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Flagon.Internal;

/// <summary>
/// Aggregates evaluations in memory — per key, the latest evaluation time and the caller's default supplied — and
/// writes them to the store in batches, the default as canonical JSON (see <see cref="CanonicalJson"/>). Recording an
/// already-seen key with an already-seen default is lock-free, and allocation-free for boolean, number and string
/// defaults; object defaults are serialised at flush, never per evaluation. A flush never writes one evaluation's time
/// with another evaluation's default, nor drops a newer evaluation. Disabled when
/// <see cref="FlagonOptions.UsageTracking"/> is off or the store is read-only.
/// </summary>
internal sealed partial class UsageTracker(
    IFlagStore store,
    FlagonOptions options,
    TimeProvider time,
    FlagChangeStamper changes,
    FlagonInstrumentation instrumentation,
    ILogger<UsageTracker> logger)
{
    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _flushGate = new(1, 1);

    public bool IsEnabled { get; } = options.UsageTracking && !store.IsReadOnly;

    /// <summary>Records that <paramref name="key"/> was evaluated now with <paramref name="defaultValue"/> as the caller's default.</summary>
    public void Record(string? key, in CallerDefault defaultValue)
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

        entry.Observe(time.GetUtcNow().UtcTicks, defaultValue);
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
            var taken = new List<(Entry Entry, Entry.Slot Slot, long UtcTicks)>();
            foreach (var (key, entry) in _entries)
            {
                if (entry.TryTakePending(out var slot, out var utcTicks))
                {
                    batch.Add(new FlagUsage(key, changes.ApplicationName, new DateTimeOffset(utcTicks, TimeSpan.Zero), slot.Default.ToJson()));
                    taken.Add((entry, slot, utcTicks));
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
                foreach (var (_, slot, _) in taken)
                {
                    slot.MarkPending();
                }

                LogFlushFailed(logger, ex, batch.Count);
                throw;
            }

            foreach (var (entry, _, utcTicks) in taken)
            {
                entry.Written(utcTicks);
            }
        }
        finally
        {
            _flushGate.Release();
        }
    }

    [LoggerMessage(EventId = 1501, Level = LogLevel.Warning, Message = "Flagon could not write usage for {Count} flag(s) to the store; it will be retried at the next flush.")]
    private static partial void LogFlushFailed(ILogger logger, Exception exception, int count);

    /// <summary>
    /// The evaluations of one key: one <see cref="Slot"/> per distinct caller's default, each holding that default
    /// (immutable) and its latest evaluation time. A flush writes the pending slot evaluated last, so the time and the
    /// default it writes always belong to the same evaluation. Slots are copy-on-write: a new default allocates once, a
    /// seen one never does. At most <see cref="MaxDefaults"/> are kept, dropping the least recently evaluated beyond
    /// that (an app passing ever-new object instances without value equality would otherwise grow the list forever).
    /// </summary>
    private sealed class Entry
    {
        private const int MaxDefaults = 4;

        private Slot[] _slots = [];

        // Only touched by flushes, which the flush gate serialises.
        private long _writtenTicks = -1;

        /// <summary>Records an evaluation at <paramref name="utcTicks"/> with <paramref name="defaultValue"/>.</summary>
        public void Observe(long utcTicks, in CallerDefault defaultValue)
        {
            while (true)
            {
                var slots = Volatile.Read(ref _slots);
                foreach (var slot in slots)
                {
                    if (slot.Default.Matches(defaultValue))
                    {
                        slot.Observe(utcTicks);
                        return;
                    }
                }

                var added = new Slot(defaultValue.Retained());
                added.Observe(utcTicks);
                if (Interlocked.CompareExchange(ref _slots, With(slots, added), slots) == slots)
                {
                    return;
                }
            }
        }

        private static Slot[] With(Slot[] slots, Slot added)
        {
            if (slots.Length < MaxDefaults)
            {
                return [.. slots, added];
            }

            var oldest = slots.MinBy(s => s.LatestTicks);
            return [.. slots.Where(s => s != oldest), added];
        }

        /// <summary>
        /// Takes the pending evaluations and returns the latest one's slot and time; false when none is pending, or the
        /// latest is older than one already written (racing evaluations keep the latest).
        /// </summary>
        public bool TryTakePending([NotNullWhen(true)] out Slot? latest, out long utcTicks)
        {
            latest = null;
            utcTicks = -1;
            foreach (var slot in Volatile.Read(ref _slots))
            {
                if (slot.TryTakePending(out var ticks) && ticks > utcTicks)
                {
                    latest = slot;
                    utcTicks = ticks;
                }
            }

            return latest is not null && utcTicks >= _writtenTicks;
        }

        /// <summary>Records that the evaluation at <paramref name="utcTicks"/> has been written.</summary>
        public void Written(long utcTicks) => _writtenTicks = Math.Max(_writtenTicks, utcTicks);

        /// <summary>
        /// One caller's default and its latest evaluation time, packed with the pending (not yet written) marker into a
        /// single word so both are always read and written together: <c>ticks &lt;&lt; 1 | pending</c>. UTC ticks are
        /// below 2^62, so the packed value fits in 64 unsigned bits. Recording never allocates.
        /// </summary>
        public sealed class Slot(CallerDefault defaultValue)
        {
            private const ulong Pending = 1;

            private ulong _state;

            public CallerDefault Default { get; } = defaultValue;

            public long LatestTicks => (long)(Volatile.Read(ref _state) >> 1);

            /// <summary>Records an evaluation, unless a later one is already recorded (racing evaluations keep the latest).</summary>
            public void Observe(long utcTicks)
            {
                var next = ((ulong)utcTicks << 1) | Pending;
                var current = Volatile.Read(ref _state);
                while ((current >> 1) <= (ulong)utcTicks && current != next)
                {
                    var seen = Interlocked.CompareExchange(ref _state, next, current);
                    if (seen == current)
                    {
                        return;
                    }

                    current = seen;
                }
            }

            /// <summary>Takes the latest evaluation if it has not been written yet, clearing its pending marker atomically.</summary>
            public bool TryTakePending(out long utcTicks)
            {
                var current = Volatile.Read(ref _state);
                while ((current & Pending) != 0)
                {
                    var seen = Interlocked.CompareExchange(ref _state, current & ~Pending, current);
                    if (seen == current)
                    {
                        utcTicks = (long)(current >> 1);
                        return true;
                    }

                    current = seen;
                }

                utcTicks = 0;
                return false;
            }

            /// <summary>Marks the latest evaluation as pending again after a failed write, so the next flush retries it.</summary>
            public void MarkPending() => Interlocked.Or(ref _state, Pending);
        }
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
