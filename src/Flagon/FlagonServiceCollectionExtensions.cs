using Flagon;
using Flagon.Internal;
using Flagon.Storage;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
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

        services.AddSingleton<FlagSnapshotManager>();
        services.AddSingleton<IFlagonSnapshotStatus>(sp => sp.GetRequiredService<FlagSnapshotManager>());
        services.AddSingleton<IFlagon, FlagonEvaluator>();
        services.TryAddSingleton<IEvaluationContextAccessor>(NullEvaluationContextAccessor.Instance);
        services.TryAddSingleton<IFlagonActorAccessor, AmbientFlagonActorAccessor>();
        services.AddSingleton(sp => new FlagChangeStamper(
            sp.GetRequiredService<TimeProvider>(),
            sp.GetRequiredService<IFlagonActorAccessor>(),
            ResolveApplicationName(options, sp)));
        services.AddSingleton<IFlagonAdmin, FlagonAdmin>();
        services.AddSingleton<FlagSeeder>();
        services.AddHostedService<FlagonHostedService>();
        // Registered after the refresher so it stops (and drains its queue) first.
        services.AddSingleton<FlagAutoCreator>();
        services.AddHostedService(sp => sp.GetRequiredService<FlagAutoCreator>());

        return services;
    }

    private static string ResolveApplicationName(FlagonOptions options, IServiceProvider services)
    {
        if (!string.IsNullOrWhiteSpace(options.ApplicationName))
        {
            return options.ApplicationName;
        }

        var hostName = services.GetService<IHostEnvironment>()?.ApplicationName;
        return !string.IsNullOrWhiteSpace(hostName)
            ? hostName
            : Assembly.GetEntryAssembly()?.GetName().Name ?? "unknown";
    }
}
