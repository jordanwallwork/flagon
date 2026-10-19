using System.Collections.Frozen;
using System.Text.Json;
using Jorrk.Flagon.Internal.Targeting;
using Jorrk.Flagon.Storage;
using Microsoft.Extensions.Logging;

namespace Jorrk.Flagon.Internal;

/// <summary>
/// An immutable view of every flag (and segment) at one change version. Swapped atomically; read lock-free by evaluation.
/// </summary>
internal sealed class FlagSnapshot
{
    public static FlagSnapshot Empty { get; } = new(
        0,
        FrozenDictionary<string, SnapshotFlag>.Empty,
        FrozenDictionary<string, SegmentRecord>.Empty,
        FrozenDictionary<string, IReadOnlyList<string>>.Empty,
        segmentRules: null);

    private FlagSnapshot(
        long version,
        FrozenDictionary<string, SnapshotFlag> flags,
        FrozenDictionary<string, SegmentRecord> segments,
        FrozenDictionary<string, IReadOnlyList<string>> segmentUsage,
        IReadOnlyDictionary<string, JsonElement>? segmentRules)
    {
        Version = version;
        Flags = flags;
        Segments = segments;
        SegmentUsage = segmentUsage;
        SegmentRules = segmentRules;
    }

    /// <summary>The store change version this snapshot reflects.</summary>
    public long Version { get; }

    public FrozenDictionary<string, SnapshotFlag> Flags { get; }

    /// <summary>Every segment, archived ones included, by key (empty when the store has no segments).</summary>
    public FrozenDictionary<string, SegmentRecord> Segments { get; }

    /// <summary>Segment key to the keys of the flags whose targeting references it (archived flags included), ordered by key.</summary>
    public FrozenDictionary<string, IReadOnlyList<string>> SegmentUsage { get; }

    /// <summary>
    /// The rules flags' <c>$ref</c>s are inlined from (every segment, archived ones included), or null when the store has
    /// no segments (a <c>$ref</c> then fails to compile).
    /// </summary>
    public IReadOnlyDictionary<string, JsonElement>? SegmentRules { get; }

    /// <summary>
    /// Builds a snapshot from <paramref name="records"/>, compiling each flag's targeting once with
    /// <paramref name="compiler"/> after inlining its segment references from <paramref name="segments"/> (null when the
    /// store has no segments). A rule that fails to compile (or whose compiler throws), including one referencing a
    /// segment that is not loaded, marks only that flag broken. Empty targeting (<c>{}</c>) is no targeting, as in flagd.
    /// </summary>
    public static FlagSnapshot Create(long version, IEnumerable<FlagRecord> records, IEnumerable<SegmentRecord>? segments, ITargetingCompiler compiler)
    {
        var segmentRecords = segments?.ToFrozenDictionary(s => s.Key, StringComparer.Ordinal);
        var rules = segmentRecords?.ToFrozenDictionary(s => s.Key, s => s.Value.Document.Rule, StringComparer.Ordinal);
        var flags = records.ToFrozenDictionary(
            r => r.Key,
            r => SnapshotFlag.Create(r, compiler, rules),
            StringComparer.Ordinal);
        var usage = flags.Values
            .SelectMany(f => SegmentRefs.References(f.Record.Document.Targeting).Select(segment => (Segment: segment, Flag: f.Record.Key)))
            .GroupBy(u => u.Segment, StringComparer.Ordinal)
            .ToFrozenDictionary(
                g => g.Key,
                g => (IReadOnlyList<string>)g.Select(u => u.Flag).Order(StringComparer.Ordinal).ToArray(),
                StringComparer.Ordinal);
        return new FlagSnapshot(version, flags, segmentRecords ?? FrozenDictionary<string, SegmentRecord>.Empty, usage, rules);
    }

    /// <summary>A snapshot of a store without segments.</summary>
    public static FlagSnapshot Create(long version, IEnumerable<FlagRecord> records, ITargetingCompiler compiler) =>
        Create(version, records, segments: null, compiler);
}

/// <summary>A flag in a <see cref="FlagSnapshot"/>: its record plus its compiled targeting (null when it has none).</summary>
internal sealed record SnapshotFlag(FlagRecord Record, TargetingCompilation? Targeting)
{
    /// <summary>
    /// <paramref name="record"/> with its targeting compiled by <paramref name="compiler"/>, segment references inlined
    /// from <paramref name="segments"/> (never throws: a rule that fails to compile is a failed
    /// <see cref="TargetingCompilation"/>). No rule, including <c>{}</c>, compiles to null.
    /// </summary>
    public static SnapshotFlag Create(FlagRecord record, ITargetingCompiler compiler, IReadOnlyDictionary<string, JsonElement>? segments = null) =>
        new(record, TargetingRules.IsRule(record.Document.Targeting)
            ? TargetingRules.Compile(compiler, record.Key, record.Document.Targeting!.Value, segments)
            : null);
}

/// <summary>
/// Owns the current <see cref="FlagSnapshot"/>, reloads it from the store and tracks its freshness.
/// A failed refresh leaves the last-known-good snapshot in place.
/// </summary>
internal sealed partial class FlagSnapshotManager(
    IFlagStore store,
    ITargetingCompiler compiler,
    FlagonOptions options,
    TimeProvider time,
    FlagonInstrumentation instrumentation,
    ILogger<FlagSnapshotManager> logger) : IFlagonSnapshotStatus
{
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    // Push state is kept apart from _status so a push signal (any thread) never races the refresh's read-modify-write of it.
    private readonly object _pushGate = new();
    private DateTimeOffset? _lastPushAt;
    private volatile FlagSnapshot _current = FlagSnapshot.Empty;
    private volatile FlagonSnapshotStatus _status = new(
        Version: 0, IsLoaded: false, IsStoreReachable: false, LastRefreshedAt: null, LastAttemptAt: null,
        ConsecutiveFailures: 0, LastError: null, RefreshInterval: options.RefreshInterval);

    public FlagSnapshot Current => _current;

    FlagonSnapshotStatus IFlagonSnapshotStatus.Current
    {
        get
        {
            DateTimeOffset? lastPushAt;
            lock (_pushGate)
            {
                lastPushAt = _lastPushAt;
            }

            // IsConnected is documented as I/O-free, so reading it here keeps the status cheap.
            var pushConnected = options.PushRefresh && store is IFlagChangeNotifier notifier ? notifier.IsConnected : (bool?)null;
            return _status with { IsPushConnected = pushConnected, LastPushAt = lastPushAt };
        }
    }

    /// <summary>Records that the store's notifier signalled a possible change now. Called from the notifier's callback.</summary>
    public void RecordPush()
    {
        lock (_pushGate)
        {
            _lastPushAt = time.GetUtcNow();
        }
    }

    /// <summary>
    /// Reloads all flags if the store's change version moved past the current snapshot's.
    /// On failure the snapshot is kept, the failure is recorded and logged, and the exception is rethrown.
    /// </summary>
    public async Task<RefreshOutcome> RefreshAsync(CancellationToken cancellationToken = default)
    {
        await _refreshGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var activity = FlagonInstrumentation.StartActivity("refresh");
            var started = time.GetTimestamp();
            RefreshOutcome outcome;
            try
            {
                outcome = await ReloadIfChangedAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (!(ex is OperationCanceledException && cancellationToken.IsCancellationRequested))
            {
                instrumentation.RecordRefresh(time.GetElapsedTime(started), RefreshOutcome.Failed, ex);
                FlagonInstrumentation.SetError(activity, ex);
                var failures = _status.ConsecutiveFailures + 1;
                _status = _status with
                {
                    IsStoreReachable = false,
                    LastAttemptAt = time.GetUtcNow(),
                    ConsecutiveFailures = failures,
                    LastError = ex,
                };
                LogRefreshFailed(logger, ex, failures, _current.Version);
                throw;
            }

            instrumentation.RecordRefresh(time.GetElapsedTime(started), outcome);
            activity?.SetTag("flagon.refresh.outcome", FlagonInstrumentation.OutcomeTag(outcome));
            activity?.SetTag("flagon.snapshot.version", _current.Version);

            var previous = _status;
            _status = previous with
            {
                Version = _current.Version,
                IsLoaded = true,
                IsStoreReachable = true,
                LastRefreshedAt = time.GetUtcNow(),
                LastAttemptAt = time.GetUtcNow(),
                ConsecutiveFailures = 0,
                LastError = null,
            };
            if (previous.ConsecutiveFailures > 0)
            {
                LogRefreshRecovered(logger, previous.ConsecutiveFailures, _current.Version);
            }

            return outcome;
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    private async Task<RefreshOutcome> ReloadIfChangedAsync(CancellationToken cancellationToken)
    {
        var version = await store.GetChangeVersionAsync(cancellationToken).ConfigureAwait(false);
        if (version == _current.Version && _status.IsLoaded)
        {
            return RefreshOutcome.Unchanged;
        }

        FlagSnapshot snapshot;
        if (store is ISegmentStore segmentStore)
        {
            // One consistent read of flags and segments, stamped with the version it reflects: separate reads could tear
            // (a segment purged between them would break a flag that had already stopped referencing it).
            var contents = await segmentStore.LoadAllWithSegmentsAsync(cancellationToken).ConfigureAwait(false);
            snapshot = FlagSnapshot.Create(contents.Version, contents.Flags, contents.Segments, compiler);
        }
        else
        {
            var flags = await store.LoadAllAsync(cancellationToken).ConfigureAwait(false);
            // Stamp with the version read *before* loading: a write racing the load then just causes one extra reload,
            // whereas stamping with the highest loaded version could skip a lower-numbered write that commits late.
            snapshot = FlagSnapshot.Create(version, flags, compiler);
        }

        foreach (var flag in snapshot.Flags.Values)
        {
            // Once per snapshot version: a snapshot is only rebuilt when the store's change version moves.
            if (flag.Targeting?.Error is { } error)
            {
                LogTargetingCompileFailed(logger, flag.Record.Key, snapshot.Version, error.Message);
            }
        }

        _current = snapshot;
        return RefreshOutcome.Reloaded;
    }

    [LoggerMessage(EventId = 1101, Level = LogLevel.Warning,
        Message = "Flagon could not refresh flags from the store ({ConsecutiveFailures} consecutive failure(s)); still serving the last-known-good snapshot (version {SnapshotVersion}).")]
    private static partial void LogRefreshFailed(ILogger logger, Exception exception, int consecutiveFailures, long snapshotVersion);

    [LoggerMessage(EventId = 1102, Level = LogLevel.Information,
        Message = "Flagon reached the flag store again after {ConsecutiveFailures} failed refresh(es); snapshot is at version {SnapshotVersion}.")]
    private static partial void LogRefreshRecovered(ILogger logger, int consecutiveFailures, long snapshotVersion);

    [LoggerMessage(EventId = 1103, Level = LogLevel.Warning,
        Message = "Flag '{FlagKey}' has a targeting rule that could not be compiled (snapshot version {SnapshotVersion}); it evaluates to the caller's default with PARSE_ERROR. {CompileError}")]
    private static partial void LogTargetingCompileFailed(ILogger logger, string flagKey, long snapshotVersion, string compileError);
}
