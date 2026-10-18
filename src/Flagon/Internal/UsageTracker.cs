using System.Collections.Concurrent;
using Flagon.Storage;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Flagon.Internal;

/// <summary>
/// Aggregates evaluations in memory — per key, the latest evaluation time and the caller's default supplied — and
/// writes them to the store in batches, the default as canonical JSON (see <see cref="CanonicalJson"/>). Recording an
/// already-seen key with an already-seen default is lock-free, and allocation-free for boolean, number and string
/// defaults (a new default takes a per-key lock); object defaults are serialised at flush, never per evaluation. A flush
/// never writes one evaluation's time with another evaluation's default, nor drops a newer evaluation; of evaluations
/// within one clock tick it keeps the last recorded. Disabled when
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
            var taken = new List<(Entry Entry, Entry.Observation Latest)>();
            foreach (var (key, entry) in _entries)
            {
                if (entry.TryTakePending(out var latest))
                {
                    batch.Add(new FlagUsage(key, changes.ApplicationName, new DateTimeOffset(latest.UtcTicks, TimeSpan.Zero), latest.Slot.Default.ToJson()));
                    taken.Add((entry, latest));
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
                foreach (var (entry, latest) in taken)
                {
                    entry.MarkPending(latest.Slot);
                }

                LogFlushFailed(logger, ex, batch.Count);
                throw;
            }

            foreach (var (entry, latest) in taken)
            {
                entry.Written(latest);
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
    /// (immutable), its latest evaluation time and its place in the order evaluations were recorded. A flush writes the
    /// pending slot evaluated last (latest time, then latest recorded, so evaluations within one clock tick keep the
    /// last supplied default), so the time and the default it writes always belong to the same evaluation.
    /// <para>
    /// Recording a seen default is lock-free and allocation-free. A new default allocates its slot under a lock (the
    /// slot array is copy-on-write, replaced only under that lock). At most <see cref="MaxDefaults"/> are kept, dropping
    /// the least recently evaluated beyond that (an app passing ever-new object instances without value equality would
    /// otherwise grow the list forever).
    /// </para>
    /// <para>
    /// Eviction never loses an acknowledged evaluation. Under the lock it first retires every current slot, an atomic
    /// flag in the same word as the slot's time and pending marker, so each one's state is final: a recording that
    /// landed before the retire is in it, and one that would land after sees the flag and retries against the new
    /// array. The survivors are then copied, state and all, into the new array (each old slot pointing at its copy, so a
    /// failed flush re-marks the copy), and only the oldest by (time, order) is dropped. That one is never the key's
    /// latest evaluation (a survivor is later), so a flush would not have written it.
    /// </para>
    /// </summary>
    private sealed class Entry
    {
        private const int MaxDefaults = 4;

        private readonly object _gate = new();
        private Slot[] _slots = [];

        // The slot recorded most recently, so a run of evaluations with one default (the hot path) takes no new place in
        // the order; only a change of default does. Racing evaluations may order themselves either way.
        private Slot? _lastRecorded;
        private long _order;

        // Only touched by flushes, which the flush gate serialises.
        private long _writtenTicks = -1;
        private long _writtenOrder = -1;

        /// <summary>Records an evaluation at <paramref name="utcTicks"/> with <paramref name="defaultValue"/>.</summary>
        public void Observe(long utcTicks, in CallerDefault defaultValue)
        {
            while (true)
            {
                var slots = Volatile.Read(ref _slots);
                Slot? seen = null;
                foreach (var slot in slots)
                {
                    if (slot.Default.Matches(defaultValue))
                    {
                        seen = slot;
                        break;
                    }
                }

                if (seen is null)
                {
                    if (TryAdd(slots, utcTicks, defaultValue))
                    {
                        return;
                    }

                    continue;
                }

                if (!ReferenceEquals(Volatile.Read(ref _lastRecorded), seen))
                {
                    seen.RaiseOrder(Interlocked.Increment(ref _order));
                    Volatile.Write(ref _lastRecorded, seen);
                }

                if (seen.Observe(utcTicks))
                {
                    return;
                }

                // The slot was retired by an eviction: its copy (or none, if it was dropped) is in the new array.
            }
        }

        /// <summary>Adds a slot for a new default unless <paramref name="slots"/> is no longer current (the caller retries).</summary>
        private bool TryAdd(Slot[] slots, long utcTicks, in CallerDefault defaultValue)
        {
            lock (_gate)
            {
                if (!ReferenceEquals(_slots, slots))
                {
                    return false;
                }

                var added = new Slot(defaultValue.Retained(), Interlocked.Increment(ref _order), state: 0);
                added.Observe(utcTicks);
                Volatile.Write(ref _slots, slots.Length < MaxDefaults ? [.. slots, added] : Evict(slots, added));
                Volatile.Write(ref _lastRecorded, added);
                return true;
            }
        }

        /// <summary>Retires every slot of <paramref name="slots"/> and returns copies of all but the oldest, plus <paramref name="added"/>.</summary>
        private static Slot[] Evict(Slot[] slots, Slot added)
        {
            var retired = new (ulong State, long Order)[slots.Length];
            var oldest = 0;
            for (var i = 0; i < slots.Length; i++)
            {
                // The order is read after the retire: a recording raises it before landing in the state.
                var state = slots[i].Retire();
                retired[i] = (state, slots[i].Order);
                if (Slot.IsBefore(retired[i].State, retired[i].Order, retired[oldest].State, retired[oldest].Order))
                {
                    oldest = i;
                }
            }

            var next = new Slot[slots.Length];
            for (int i = 0, j = 0; i < slots.Length; i++)
            {
                if (i != oldest)
                {
                    next[j++] = slots[i].Successor = new Slot(slots[i].Default, retired[i].Order, retired[i].State);
                }
            }

            next[^1] = added;
            return next;
        }

        /// <summary>
        /// Takes the pending evaluations and returns the latest one; false when none is pending, or the latest is older
        /// than one already written (racing evaluations keep the latest).
        /// </summary>
        public bool TryTakePending(out Observation latest)
        {
            latest = default;
            var found = false;
            foreach (var slot in Volatile.Read(ref _slots))
            {
                if (slot.TryTakePending(out var ticks))
                {
                    var order = slot.Order;
                    if (!found || IsAfter(ticks, order, latest.UtcTicks, latest.Order))
                    {
                        latest = new Observation(slot, ticks, order);
                        found = true;
                    }
                }
            }

            return found && !IsAfter(_writtenTicks, _writtenOrder, latest.UtcTicks, latest.Order);
        }

        /// <summary>Marks <paramref name="slot"/>'s latest evaluation pending again after a failed write, following it into any eviction copy.</summary>
        public void MarkPending(Slot slot)
        {
            // Under the lock no eviction is half done, so a retired slot's successor is set (or it was dropped, being
            // older than a survivor: see the class remarks).
            lock (_gate)
            {
                for (Slot? current = slot; current is not null && !current.TryMarkPending(); current = current.Successor)
                {
                }
            }
        }

        /// <summary>Records that <paramref name="latest"/> has been written.</summary>
        public void Written(Observation latest)
        {
            if (IsAfter(latest.UtcTicks, latest.Order, _writtenTicks, _writtenOrder))
            {
                (_writtenTicks, _writtenOrder) = (latest.UtcTicks, latest.Order);
            }
        }

        private static bool IsAfter(long ticks, long order, long otherTicks, long otherOrder) =>
            ticks > otherTicks || (ticks == otherTicks && order > otherOrder);

        /// <summary>A slot's latest evaluation as a flush took it.</summary>
        public readonly record struct Observation(Slot Slot, long UtcTicks, long Order);

        /// <summary>
        /// One caller's default and its latest evaluation time, packed with the pending (not yet written) and retired
        /// markers into a single word so all are always read and written together: <c>ticks &lt;&lt; 2 | retired &lt;&lt; 1 |
        /// pending</c>. UTC ticks are below 2^62, so the packed value fits in 64 unsigned bits. Its place in the order
        /// evaluations were recorded is kept beside it. Recording never allocates.
        /// </summary>
        public sealed class Slot(CallerDefault defaultValue, long order, ulong state)
        {
            private const ulong Pending = 1;
            private const ulong Retired = 2;

            private ulong _state = state & ~Retired;
            private long _order = order;

            public CallerDefault Default { get; } = defaultValue;

            public long Order => Volatile.Read(ref _order);

            /// <summary>The copy an eviction made of this slot, once retired; null when it was dropped (or is live).</summary>
            public Slot? Successor { get; set; }

            /// <summary>True when (<paramref name="state"/>, <paramref name="order"/>) is an earlier evaluation than the other.</summary>
            public static bool IsBefore(ulong state, long order, ulong otherState, long otherOrder) =>
                IsAfter((long)(otherState >> 2), otherOrder, (long)(state >> 2), order);

            /// <summary>Moves this slot to at least <paramref name="order"/>.</summary>
            public void RaiseOrder(long order)
            {
                var current = Volatile.Read(ref _order);
                while (current < order)
                {
                    var seen = Interlocked.CompareExchange(ref _order, order, current);
                    if (seen == current)
                    {
                        return;
                    }

                    current = seen;
                }
            }

            /// <summary>
            /// Records an evaluation, keeping the later time if one is already recorded (racing evaluations keep the
            /// latest), and marks it pending. False when the slot is retired, so the caller records into the new array.
            /// </summary>
            public bool Observe(long utcTicks)
            {
                var current = Volatile.Read(ref _state);
                while (true)
                {
                    if ((current & Retired) != 0)
                    {
                        return false;
                    }

                    var next = ((ulong)Math.Max((long)(current >> 2), utcTicks) << 2) | Pending;
                    if (current == next)
                    {
                        return true;
                    }

                    var seen = Interlocked.CompareExchange(ref _state, next, current);
                    if (seen == current)
                    {
                        return true;
                    }

                    current = seen;
                }
            }

            /// <summary>Takes the latest evaluation if it has not been written yet, clearing its pending marker atomically.</summary>
            public bool TryTakePending(out long utcTicks)
            {
                var current = Volatile.Read(ref _state);
                while ((current & (Pending | Retired)) == Pending)
                {
                    var seen = Interlocked.CompareExchange(ref _state, current & ~Pending, current);
                    if (seen == current)
                    {
                        utcTicks = (long)(current >> 2);
                        return true;
                    }

                    current = seen;
                }

                utcTicks = 0;
                return false;
            }

            /// <summary>Marks the latest evaluation as pending again after a failed write; false when the slot is retired.</summary>
            public bool TryMarkPending()
            {
                var current = Volatile.Read(ref _state);
                while ((current & Retired) == 0)
                {
                    var seen = Interlocked.CompareExchange(ref _state, current | Pending, current);
                    if (seen == current)
                    {
                        return true;
                    }

                    current = seen;
                }

                return false;
            }

            /// <summary>Retires the slot, freezing its state, and returns that final state.</summary>
            public ulong Retire() => Interlocked.Or(ref _state, Retired);
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
