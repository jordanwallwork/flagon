using Flagon;
using Flagon.Internal;
using Flagon.Storage;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

// ReSharper disable once CheckNamespace
namespace Microsoft.Extensions.DependencyInjection;

/// <summary>Registers Flagon.</summary>
public static class FlagonServiceCollectionExtensions
{
    /// <summary>
    /// Adds Flagon: <see cref="IFlagon"/> for evaluation, <see cref="IFlagonAdmin"/> for management and the configured
    /// <see cref="IFlagStore"/> (an <see cref="InMemoryFlagStore"/> when no storage is configured).
    /// </summary>
    public static IServiceCollection AddFlagon(this IServiceCollection services, Action<FlagonOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        // Run the delegate exactly once, here, so storage choices and (later) Define() calls are captured once.
        var options = new FlagonOptions();
        configure?.Invoke(options);

        services.AddSingleton(options);
        services.AddSingleton<IOptions<FlagonOptions>>(Options.Options.Create(options));
        services.TryAddSingleton(TimeProvider.System);
        // Hosts normally register logging; fall back to no-op logging when used with a bare ServiceCollection.
        services.TryAddSingleton<ILoggerFactory, NullLoggerFactory>();
        services.TryAdd(ServiceDescriptor.Singleton(typeof(ILogger<>), typeof(NullLogger<>)));

        var storeFactory = options.StoreFactory ?? (_ => new InMemoryFlagStore());
        services.AddSingleton(storeFactory);

        services.AddSingleton<FlagonInstrumentation>();
        services.AddSingleton<FlagSnapshotManager>();
        services.AddSingleton<IFlagonSnapshotStatus>(sp => sp.GetRequiredService<FlagSnapshotManager>());
        services.AddSingleton<IFlagon, FlagonEvaluator>();
        services.AddSingleton<IFlagonAdmin, FlagonAdmin>();
        services.AddHostedService<FlagonHostedService>();

        return services;
    }
}
