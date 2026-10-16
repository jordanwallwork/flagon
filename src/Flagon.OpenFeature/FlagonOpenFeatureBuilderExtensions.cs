using Flagon;
using Flagon.OpenFeature;
using Microsoft.Extensions.DependencyInjection;
using OpenFeature.Hosting;

namespace OpenFeature;

/// <summary>Registers Flagon as an OpenFeature provider with the OpenFeature hosting builder.</summary>
public static class FlagonOpenFeatureBuilderExtensions
{
    /// <summary>
    /// Uses Flagon as the default OpenFeature provider:
    /// <c>services.AddFlagon(...); services.AddOpenFeature(of =&gt; of.AddFlagon());</c>
    /// Requires <c>AddFlagon</c> to have been called on the same service collection.
    /// </summary>
    public static OpenFeatureBuilder AddFlagon(this OpenFeatureBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.AddProvider(sp => new FlagonProvider(sp.GetRequiredService<IFlagon>()));
    }

    /// <summary>Uses Flagon as the OpenFeature provider for the named <paramref name="domain"/>.</summary>
    public static OpenFeatureBuilder AddFlagon(this OpenFeatureBuilder builder, string domain)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.AddProvider(domain, (sp, _) => new FlagonProvider(sp.GetRequiredService<IFlagon>()));
    }
}
