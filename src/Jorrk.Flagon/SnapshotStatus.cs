namespace Jorrk.Flagon;

/// <summary>
/// Freshness and store reachability of the in-memory flag snapshot that evaluation is served from.
/// Intended for health checks and diagnostics; reading it performs no I/O.
/// </summary>
public interface IFlagonSnapshotStatus
{
    /// <summary>The status as of the most recent refresh attempt.</summary>
    FlagonSnapshotStatus Current { get; }
}

/// <summary>A point-in-time view of the snapshot's freshness.</summary>
/// <param name="Version">The store change version the snapshot reflects (0 before the first successful load).</param>
/// <param name="IsLoaded">True once the snapshot has been loaded from the store at least once. Until then code defaults apply.</param>
/// <param name="IsStoreReachable">False when the most recent refresh attempt failed (the last-known-good snapshot is still served).</param>
/// <param name="LastRefreshedAt">When the store was last successfully read (whether or not anything had changed), or null if never.</param>
/// <param name="LastAttemptAt">When the most recent refresh attempt completed, successfully or not, or null if none has completed.</param>
/// <param name="ConsecutiveFailures">Refresh attempts that have failed in a row since the last success.</param>
/// <param name="LastError">The error from the most recent attempt, or null if it succeeded.</param>
/// <param name="RefreshInterval">The configured polling interval (see <see cref="FlagonOptions.RefreshInterval"/>).</param>
public sealed record FlagonSnapshotStatus(
    long Version,
    bool IsLoaded,
    bool IsStoreReachable,
    DateTimeOffset? LastRefreshedAt,
    DateTimeOffset? LastAttemptAt,
    int ConsecutiveFailures,
    Exception? LastError,
    TimeSpan RefreshInterval)
{
    /// <summary>
    /// The time since the store was last successfully read, as of <paramref name="now"/>; null if it never was.
    /// A health check can compare this to a multiple of <see cref="RefreshInterval"/> to decide the snapshot is stale.
    /// </summary>
    public TimeSpan? Age(DateTimeOffset now) => LastRefreshedAt is { } at ? now - at : null;
}
