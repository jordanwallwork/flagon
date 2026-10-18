using Jorrk.Flagon;
using Jorrk.Flagon.AspNetCore.Health;
using Microsoft.Extensions.Diagnostics.HealthChecks;

// ReSharper disable once CheckNamespace
namespace Microsoft.Extensions.DependencyInjection;

/// <summary>Registers Flagon's health check.</summary>
public static class FlagonHealthChecksBuilderExtensions
{
    /// <summary>
    /// Adds <see cref="FlagonHealthCheck"/>: Healthy while flags are loaded and fresh, Degraded when the flag store is
    /// unreachable, flags have never loaded, or the snapshot is older than <paramref name="staleAfter"/>.
    /// Requires <c>services.AddFlagon(...)</c>.
    /// </summary>
    /// <param name="builder">The health checks builder from <c>services.AddHealthChecks()</c>.</param>
    /// <param name="name">The check's name (default <c>flagon</c>).</param>
    /// <param name="staleAfter">
    /// Age beyond which the snapshot is stale; default <see cref="FlagonHealthCheck.DefaultStaleRefreshIntervals"/> ×
    /// <see cref="FlagonOptions.RefreshInterval"/>.
    /// </param>
    /// <param name="tags">Tags for filtering checks (e.g. <c>ready</c>).</param>
    public static IHealthChecksBuilder AddFlagon(
        this IHealthChecksBuilder builder,
        string name = "flagon",
        TimeSpan? staleAfter = null,
        IEnumerable<string>? tags = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        return builder.Add(new HealthCheckRegistration(
            name,
            sp => new FlagonHealthCheck(
                sp.GetRequiredService<IFlagonSnapshotStatus>(),
                sp.GetService<TimeProvider>() ?? TimeProvider.System,
                staleAfter),
            failureStatus: HealthStatus.Degraded,
            tags));
    }
}
