using Flagon.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace Flagon.Tests;

public class AmbientContextFallbackTests
{
    private static Task<FlagonTestHost> StartAsync(RecordingEvaluationContextAccessor accessor) =>
        FlagonTestHost.StartAsync(o => o.Services = s => s.AddSingleton<IEvaluationContextAccessor>(accessor));

    [Fact]
    public async Task Evaluating_without_a_context_uses_the_ambient_context()
    {
        var accessor = new RecordingEvaluationContextAccessor(new EvaluationContext("alice"));
        await using var host = await StartAsync(accessor);
        await host.Admin.CreateAsync(new CreateFlagRequest("new-checkout", Enabled: true));

        Assert.True(host.Flagon.IsEnabled("new-checkout"));
        await host.Flagon.GetBooleanDetailsAsync("new-checkout");

        Assert.Equal(2, accessor.Reads);
    }

    [Fact]
    public async Task An_explicit_context_takes_precedence_over_the_ambient_one()
    {
        var accessor = new RecordingEvaluationContextAccessor(new EvaluationContext("alice"));
        await using var host = await StartAsync(accessor);
        await host.Admin.CreateAsync(new CreateFlagRequest("new-checkout", Enabled: true));

        host.Flagon.IsEnabled("new-checkout", context: new EvaluationContext("bob"));
        host.Flagon.GetAllBooleanDetails(new EvaluationContext("bob"));

        Assert.Equal(0, accessor.Reads);
    }

    [Fact]
    public async Task Bulk_evaluation_without_a_context_uses_the_ambient_context()
    {
        var accessor = new RecordingEvaluationContextAccessor(new EvaluationContext("alice"));
        await using var host = await StartAsync(accessor);

        host.Flagon.GetAllBooleanDetails();

        Assert.Equal(1, accessor.Reads);
    }
}
