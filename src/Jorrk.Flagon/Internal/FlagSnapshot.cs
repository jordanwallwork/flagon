using System.Collections.Frozen;
using Jorrk.Flagon.Internal.Targeting;
using Jorrk.Flagon.Storage;
using Microsoft.Extensions.Logging;

namespace Jorrk.Flagon.Internal;

/// <summary>An immutable view of every flag at one change version. Swapped atomically; read lock-free by evaluation.</summary>
internal sealed class FlagSnapshot
{
    public static FlagSnapshot Empty { get; } = new(0, FrozenDictionary<string, SnapshotFlag>.Empty);

    private FlagSnapshot(long version, FrozenDictionary<string, SnapshotFlag> flags)
    {
        Version = version;
        Flags = flags;
    }

    /// <summary>The store change version this snapshot reflects.</summary>
    public long Version { get; }

    public FrozenDictionary<string, SnapshotFlag> Flags { get; }

    /// <summary>
    /// Builds a snapshot from <paramref name="records"/>, compiling each flag's targeting once with
    /// <paramref name="compiler"/>. A rule that fails to compile (or whose compiler throws) marks only that flag broken.
    /// Empty targeting (<c>{}</c>) is no targeting, as in flagd.
    /// </summary>
    public static FlagSnapshot Create(long version, IEnumerable<FlagRecord> records, ITargetingCompiler compiler)
    {
        var flags = records.ToFrozenDictionary(
            r => r.Key,
            r => SnapshotFlag.Create(r, compiler),
            StringComparer.Ordinal);
        return new FlagSnapshot(version, flags);
    }
}

/// <summary>A flag in a <see cref="FlagSnapshot"/>: its record plus its compiled targeting (null when it has none).</summary>
internal sealed record SnapshotFlag(FlagRecord Record, TargetingCompilation? Targeting)
{
    /// <summary>
    /// <paramref name="record"/> with its targeting compiled by <paramref name="compiler"/> (never throws: a rule that
    /// fails to compile is a failed <see cref="TargetingCompilation"/>). No rule, including <c>{}</c>, compiles to null.
    /// </summary>
    public static SnapshotFlag Create(FlagRecord record, ITargetingCompiler compiler) =>
        new(record, TargetingRules.IsRule(record.Document.Targeting)
            ? TargetingRules.Compile(compiler, record.Key, record.Document.Targeting!.Value)
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

        var flags = await store.LoadAllAsync(cancellationToken).ConfigureAwait(false);
        // Stamp with the version read *before* loading: a write racing the load then just causes one extra reload,
        // whereas stamping with the highest loaded version could skip a lower-numbered write that commits late.
        var snapshot = FlagSnapshot.Create(version, flags, compiler);
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
