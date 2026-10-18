using Flagon.Internal;
using Flagon.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Flagon.Testing.Internal;

/// <summary>
/// Turns an <c>AddFlagon</c> registration into a test one: applies the testing switches to its options, replaces its
/// store with an in-memory one (the app's store factory is never invoked) and registers <see cref="TestFlags"/>.
/// Idempotent: applying it again only re-applies the switches.
/// </summary>
internal static class FlagonTestServices
{
    public static void Apply(IServiceCollection services, FlagonOptions options, FlagonTestingSwitches switches)
    {
        options.AutoCreate = switches.AutoCreate;
        options.UsageTracking = switches.UsageTracking;
        options.SeedDefinitions = switches.SeedDefinitions;
        options.FailFastOnStartup = true;

        if (services.Any(d => d.ServiceType == typeof(FlagonTestingSwitches)))
        {
            return;
        }

        services.RemoveAll<IFlagStore>();
        services.AddSingleton<IFlagStore>(new InMemoryFlagStore());
        services.AddSingleton(switches);
        services.AddSingleton(sp => new TestFlags(
            sp.GetRequiredService<IFlagStore>(),
            sp.GetRequiredService<FlagSnapshotManager>()));
    }
}
