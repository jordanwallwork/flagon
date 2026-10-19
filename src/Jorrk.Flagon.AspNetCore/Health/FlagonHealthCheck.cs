using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Jorrk.Flagon.AspNetCore.Health;

/// <summary>
/// Reports Flagon's flag snapshot health: <see cref="HealthStatus.Healthy"/> while flags are loaded, the store is
/// reachable and the snapshot is fresh; <see cref="HealthStatus.Degraded"/> otherwise. Flagon keeps serving the
/// last-known-good snapshot (or code defaults) while degraded, so the check never reports Unhealthy.
/// Reads <see cref="IFlagonSnapshotStatus"/> only; performs no I/O.
/// </summary>
public sealed class FlagonHealthCheck : IHealthCheck
{
    /// <summary>How many refresh intervals may pass without a successful refresh before the snapshot counts as stale.</summary>
    public const int DefaultStaleRefreshIntervals = 3;

    private readonly IFlagonSnapshotStatus _status;
    private readonly TimeProvider _time;
    private readonly TimeSpan? _staleAfter;

    /// <summary>Creates the check.</summary>
    /// <param name="status">Flagon's snapshot status.</param>
    /// <param name="time">The clock used to age the snapshot.</param>
    /// <param name="staleAfter">
    /// How long since the last successful refresh before the snapshot is stale; null for
    /// <see cref="DefaultStaleRefreshIntervals"/> × <see cref="FlagonOptions.RefreshInterval"/>.
    /// </param>
    public FlagonHealthCheck(IFlagonSnapshotStatus status, TimeProvider time, TimeSpan? staleAfter = null)
    {
        ArgumentNullException.ThrowIfNull(status);
        ArgumentNullException.ThrowIfNull(time);
        if (staleAfter is { } threshold)
        {
            ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(threshold, TimeSpan.Zero, nameof(staleAfter));
        }

        _status = status;
        _time = time;
        _staleAfter = staleAfter;
    }

    /// <inheritdoc />
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var status = _status.Current;
        var staleAfter = _staleAfter ?? status.RefreshInterval * DefaultStaleRefreshIntervals;
        var age = status.Age(_time.GetUtcNow());

        var data = new Dictionary<string, object>
        {
            ["snapshotVersion"] = status.Version,
            ["loaded"] = status.IsLoaded,
            ["storeReachable"] = status.IsStoreReachable,
            ["consecutiveFailures"] = status.ConsecutiveFailures,
            ["staleAfter"] = staleAfter,
        };
        if (status.LastRefreshedAt is { } refreshedAt)
        {
            data["lastRefreshedAt"] = refreshedAt;
        }

        if (age is { } snapshotAge)
        {
            data["snapshotAge"] = snapshotAge;
        }

        // Reported for operators only: push state never changes the status, because polling is the backstop.
        if (status.IsPushConnected is { } pushConnected)
        {
            data["pushConnected"] = pushConnected;
        }

        if (status.LastPushAt is { } pushedAt)
        {
            data["lastPushAt"] = pushedAt;
        }

        var result = (status, age) switch
        {
            ({ IsLoaded: false }, _) => HealthCheckResult.Degraded(
                "Flagon has not loaded flags from the store yet; code defaults apply.", status.LastError, data),
            ({ IsStoreReachable: false }, _) => HealthCheckResult.Degraded(
                $"Flagon cannot reach the flag store ({status.ConsecutiveFailures} consecutive failed refresh(es)); serving the last-known-good snapshot.",
                status.LastError, data),
            (_, { } a) when a > staleAfter => HealthCheckResult.Degraded(
                $"Flagon's flag snapshot is stale: last refreshed {a} ago (threshold {staleAfter}).", data: data),
            _ => HealthCheckResult.Healthy("Flagon's flag snapshot is fresh.", data),
        };
        return Task.FromResult(result);
    }
}
