using Jorrk.Flagon.Tests.Support;
using Microsoft.Extensions.DependencyInjection;
using OpenFeature;
using OfEvaluationContext = OpenFeature.Model.EvaluationContext;

namespace Jorrk.Flagon.OpenFeature.Tests;

public class OpenFeatureAmbientContextTests
{
    private static async Task<FlagonTestHost> StartAsync(RecordingEvaluationContextAccessor accessor)
    {
        var host = await FlagonTestHost.StartAsync(o => o.Services = services =>
        {
            services.AddSingleton<IEvaluationContextAccessor>(accessor);
            services.AddOpenFeature(of => of.AddFlagon());
        });
        await host.Admin.CreateAsync(new CreateFlagRequest("new-checkout", Enabled: true));
        return host;
    }

    private static IFeatureClient Client(FlagonTestHost host) => host.Services.GetRequiredService<IFeatureClient>();

    [Fact]
    public async Task An_OpenFeature_evaluation_without_context_uses_flagons_ambient_context()
    {
        var accessor = new RecordingEvaluationContextAccessor(new Jorrk.Flagon.EvaluationContext("alice"));
        await using var host = await StartAsync(accessor);

        Assert.True(await Client(host).GetBooleanValueAsync("new-checkout", false));

        Assert.Equal(1, accessor.Reads);
    }

    [Fact]
    public async Task An_OpenFeature_evaluation_context_takes_precedence_over_the_ambient_one()
    {
        var accessor = new RecordingEvaluationContextAccessor(new Jorrk.Flagon.EvaluationContext("alice"));
        await using var host = await StartAsync(accessor);

        await Client(host).GetBooleanValueAsync("new-checkout", false, OfEvaluationContext.Builder().SetTargetingKey("bob").Build());

        Assert.Equal(0, accessor.Reads);
    }
}
