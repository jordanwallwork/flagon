using System.Text.Json;
using Jorrk.Flagon.Storage;
using Jorrk.Flagon.Tests.Support;
using Microsoft.Extensions.DependencyInjection;
using OpenFeature;
using OpenFeature.Constant;
using OpenFeature.Hosting;
using OpenFeature.Model;

namespace Jorrk.Flagon.OpenFeature.Tests;

/// <summary>A flag of any type disabled with <see cref="IFlagonAdmin.SetStateAsync"/> resolves to the caller's default through OpenFeature.</summary>
public class OpenFeatureDisabledFlagTests
{
    private static Dictionary<string, JsonElement> Variants(string json) =>
        JsonDocument.Parse(json).RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone(), StringComparer.Ordinal);

    private static async Task<FlagonTestHost> StartWithDisabledFlagAsync(string key, string variants, string defaultVariant)
    {
        var host = await FlagonTestHost.StartAsync(o => o.Services = services => services.AddOpenFeature(of => of.AddFlagon()));
        var flag = await host.Admin.CreateAsync(new CreateFlagRequest(key, Variants: Variants(variants), DefaultVariant: defaultVariant));
        await host.Admin.SetStateAsync(key, FlagState.Disabled, flag.Version);
        return host;
    }

    private static IFeatureClient Client(FlagonTestHost host) => host.Services.GetRequiredService<IFeatureClient>();

    private static void AssertDisabled<T>(global::OpenFeature.Model.FlagEvaluationDetails<T> details)
    {
        Assert.Equal(Reason.Disabled, details.Reason);
        Assert.Null(details.Variant);
        Assert.Equal(ErrorType.None, details.ErrorType);
    }

    [Fact]
    public async Task A_disabled_string_flag_resolves_to_the_callers_default()
    {
        await using var host = await StartWithDisabledFlagAsync("theme", """{ "dark": "dark" }""", "dark");

        var details = await Client(host).GetStringDetailsAsync("theme", "code-default");

        Assert.Equal("code-default", details.Value);
        AssertDisabled(details);
    }

    [Fact]
    public async Task A_disabled_number_flag_resolves_to_the_callers_integer_and_double_defaults()
    {
        await using var host = await StartWithDisabledFlagAsync("page-size", """{ "large": 50 }""", "large");

        var integer = await Client(host).GetIntegerDetailsAsync("page-size", 7);
        var real = await Client(host).GetDoubleDetailsAsync("page-size", 0.5);

        Assert.Equal(7, integer.Value);
        AssertDisabled(integer);
        Assert.Equal(0.5, real.Value);
        AssertDisabled(real);
    }

    [Fact]
    public async Task A_disabled_object_flag_resolves_to_the_callers_structure_default()
    {
        await using var host = await StartWithDisabledFlagAsync("layout", """{ "roomy": { "rows": 1 } }""", "roomy");

        var details = await Client(host).GetObjectDetailsAsync("layout", new Value(Structure.Builder().Set("rows", 2).Build()));

        Assert.Equal(2, details.Value.AsStructure!.GetValue("rows").AsInteger);
        AssertDisabled(details);
    }
}
