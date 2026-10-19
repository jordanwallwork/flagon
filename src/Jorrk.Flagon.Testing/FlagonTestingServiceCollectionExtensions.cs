using Jorrk.Flagon;
using Jorrk.Flagon.Testing;
using Jorrk.Flagon.Testing.Internal;

// ReSharper disable once CheckNamespace
namespace Microsoft.Extensions.DependencyInjection;

/// <summary>Replaces an app's Flagon store for integration tests.</summary>
public static class FlagonTestingServiceCollectionExtensions
{
    /// <summary>
    /// Turns the app's <c>AddFlagon</c> registration into a test one, for integration tests of a running host (e.g. in
    /// <c>WebApplicationFactory</c>'s <c>ConfigureTestServices</c>). The app's store is replaced with an in-memory one
    /// and its store factory is never invoked, so no database is opened. Its <c>Define</c>s,
    /// <see cref="FlagonOptions.ApplicationName"/>, <see cref="FlagonOptions.RefreshInterval"/>, ambient context, actor
    /// accessor and targeting engine are kept. The testing switches are applied to its options, with
    /// <see cref="FlagonOptions.FailFastOnStartup"/> forced on.
    /// <para>
    /// Resolve <see cref="TestFlags"/> from the host's services to set flags: in-process evaluation, the OFREP endpoints
    /// and the management API see each write on the next request.
    /// </para>
    /// </summary>
    /// <param name="services">The app's services, after its <c>AddFlagon</c> call.</param>
    /// <param name="configure">Overrides of the test-safe defaults.</param>
    /// <returns>The same service collection, for chaining.</returns>
    /// <exception cref="InvalidOperationException"><c>AddFlagon</c> has not been registered yet.</exception>
    /// <remarks>Calling it again keeps one store and one <see cref="TestFlags"/>, and applies the new switches.</remarks>
    /// <example>
    /// <code>
    /// builder.ConfigureTestServices(s => s.AddFlagonTesting());
    /// var flags = factory.Services.GetRequiredService&lt;TestFlags&gt;();
    /// flags.SetFlag("new-checkout", true);
    /// </code>
    /// </example>
    public static IServiceCollection AddFlagonTesting(this IServiceCollection services, Action<FlagonTestingSwitches>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        var options = services.LastOrDefault(d => d.ServiceType == typeof(FlagonOptions))?.ImplementationInstance as FlagonOptions
            ?? throw new InvalidOperationException(
                "AddFlagonTesting() replaces the app's Flagon registration, but Flagon is not registered. Call " +
                "services.AddFlagon(...) first (in a WebApplicationFactory, call AddFlagonTesting() from ConfigureTestServices, " +
                "which runs after the app's own services).");

        var switches = services.LastOrDefault(d => d.ServiceType == typeof(FlagonTestingSwitches))?.ImplementationInstance as FlagonTestingSwitches
            ?? new FlagonTestingSwitches();
        configure?.Invoke(switches);
        FlagonTestServices.Apply(services, options, switches);
        return services;
    }
}
