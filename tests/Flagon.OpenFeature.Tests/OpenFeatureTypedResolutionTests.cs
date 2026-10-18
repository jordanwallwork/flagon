using Flagon.Storage;
using Flagon.Tests.Support;
using Microsoft.Extensions.DependencyInjection;
using OpenFeature;
using OpenFeature.Constant;
using OpenFeature.Hosting;
using OpenFeature.Model;

namespace Flagon.OpenFeature.Tests;

/// <summary>String, integer, double and structure resolution through the OpenFeature SDK.</summary>
public class OpenFeatureTypedResolutionTests
{
    private static async Task<FlagonTestHost> StartWithStoredFlagsAsync(params (string Key, string FlagdJson)[] flags)
    {
        var store = new InMemoryFlagStore();
        foreach (var (key, json) in flags)
        {
            await store.TryCreateAsync(key, FlagdJson.Deserialize(json));
        }

        return await FlagonTestHost.StartAsync(o =>
        {
            o.Store = store;
            o.Services = services => services.AddOpenFeature(of => of.AddFlagon());
        });
    }

    private static string Static(string variants, string defaultVariant) =>
        $$"""{ "state": "ENABLED", "variants": {{variants}}, "defaultVariant": "{{defaultVariant}}" }""";

    private static IFeatureClient Client(FlagonTestHost host) => host.Services.GetRequiredService<IFeatureClient>();

    [Fact]
    public async Task A_string_flag_resolves_to_its_served_variant()
    {
        await using var host = await StartWithStoredFlagsAsync(("theme", Static("""{ "light": "light", "dark": "dark" }""", "dark")));

        var details = await Client(host).GetStringDetailsAsync("theme", "caller-default");

        Assert.Equal("dark", details.Value);
        Assert.Equal("dark", details.Variant);
        Assert.Equal(Reason.Static, details.Reason);
        Assert.Equal(ErrorType.None, details.ErrorType);
    }

    [Fact]
    public async Task An_integer_flag_resolves_to_its_served_variant()
    {
        await using var host = await StartWithStoredFlagsAsync(("page-size", Static("""{ "small": 10, "large": 50 }""", "large")));

        var details = await Client(host).GetIntegerDetailsAsync("page-size", 7);

        Assert.Equal(50, details.Value);
        Assert.Equal("large", details.Variant);
        Assert.Equal(ErrorType.None, details.ErrorType);
    }

    [Theory]
    [InlineData("2.5")]
    [InlineData("3000000000")]
    public async Task Integer_resolution_of_a_fractional_or_out_of_range_number_is_a_type_mismatch(string number)
    {
        await using var host = await StartWithStoredFlagsAsync(("n", Static($$"""{ "v": {{number}} }""", "v")));

        var details = await Client(host).GetIntegerDetailsAsync("n", 7);

        Assert.Equal(7, details.Value);
        Assert.Equal(Reason.Error, details.Reason);
        Assert.Equal(ErrorType.TypeMismatch, details.ErrorType);
    }

    [Fact]
    public async Task A_double_flag_resolves_to_its_served_variant()
    {
        await using var host = await StartWithStoredFlagsAsync(("ratio", Static("""{ "half": 0.5 }""", "half")));

        var details = await Client(host).GetDoubleDetailsAsync("ratio", 1.5);

        Assert.Equal(0.5, details.Value);
        Assert.Equal("half", details.Variant);
        Assert.Equal(ErrorType.None, details.ErrorType);
    }

    [Fact]
    public async Task An_object_flag_resolves_to_a_structure_that_round_trips_its_json()
    {
        await using var host = await StartWithStoredFlagsAsync(("layout", Static("""
            { "roomy": { "rows": 3, "ratio": 0.25, "big": true, "name": "roomy", "none": null,
                         "nested": { "deep": { "flag": false } }, "list": [1, "two", { "three": 3 }] } }
            """, "roomy")));

        var details = await Client(host).GetObjectDetailsAsync("layout", new Value(Structure.Empty));

        Assert.Equal(ErrorType.None, details.ErrorType);
        Assert.Equal("roomy", details.Variant);
        var layout = details.Value.AsStructure!;
        Assert.Equal(3, layout.GetValue("rows").AsInteger);
        Assert.Equal(0.25, layout.GetValue("ratio").AsDouble);
        Assert.True(layout.GetValue("big").AsBoolean);
        Assert.Equal("roomy", layout.GetValue("name").AsString);
        Assert.True(layout.GetValue("none").IsNull);
        Assert.False(layout.GetValue("nested").AsStructure!.GetValue("deep").AsStructure!.GetValue("flag").AsBoolean);
        var list = layout.GetValue("list").AsList!;
        Assert.Equal(1, list[0].AsInteger);
        Assert.Equal("two", list[1].AsString);
        Assert.Equal(3, list[2].AsStructure!.GetValue("three").AsInteger);
    }

    [Fact]
    public async Task A_structure_default_is_returned_intact_when_no_variant_is_served()
    {
        await using var host = await StartWithStoredFlagsAsync(("layout", """
            { "state": "DISABLED", "variants": { "roomy": { "rows": 1 } }, "defaultVariant": "roomy" }
            """));
        var callerDefault = new Value(Structure.Builder()
            .Set("rows", 2)
            .Set("label", "caller-default")
            .Set("inner", new Value(Structure.Builder().Set("on", true).Build()))
            .Build());

        var details = await Client(host).GetObjectDetailsAsync("layout", callerDefault);

        Assert.Equal(Reason.Disabled, details.Reason);
        var layout = details.Value.AsStructure!;
        Assert.Equal(2, layout.GetValue("rows").AsInteger);
        Assert.Equal("caller-default", layout.GetValue("label").AsString);
        Assert.True(layout.GetValue("inner").AsStructure!.GetValue("on").AsBoolean);
    }

    [Fact]
    public async Task A_missing_typed_flag_returns_the_callers_default_with_flag_not_found()
    {
        await using var host = await StartWithStoredFlagsAsync();

        var details = await Client(host).GetStringDetailsAsync("no-such-flag", "caller-default");

        Assert.Equal("caller-default", details.Value);
        Assert.Equal(ErrorType.FlagNotFound, details.ErrorType);
    }

    [Fact]
    public async Task Every_resolver_reports_a_type_mismatch_with_the_callers_default_for_a_flag_of_another_type()
    {
        await using var host = await StartWithStoredFlagsAsync(
            ("theme", Static("""{ "dark": "dark" }""", "dark")),
            ("layout", Static("""{ "roomy": { "rows": 1 } }""", "roomy")));
        var client = Client(host);

        var asBoolean = await client.GetBooleanDetailsAsync("theme", true);
        var asInteger = await client.GetIntegerDetailsAsync("theme", 7);
        var asDouble = await client.GetDoubleDetailsAsync("theme", 1.5);
        var asObject = await client.GetObjectDetailsAsync("theme", new Value("caller-default"));
        var asString = await client.GetStringDetailsAsync("layout", "caller-default");

        Assert.True(asBoolean.Value);
        Assert.Equal(7, asInteger.Value);
        Assert.Equal(1.5, asDouble.Value);
        Assert.Equal("caller-default", asObject.Value.AsString);
        Assert.Equal("caller-default", asString.Value);
        Assert.All(
            [asBoolean.ErrorType, asInteger.ErrorType, asDouble.ErrorType, asObject.ErrorType, asString.ErrorType],
            e => Assert.Equal(ErrorType.TypeMismatch, e));
        Assert.All(
            [asBoolean.Reason, asInteger.Reason, asDouble.Reason, asObject.Reason, asString.Reason],
            r => Assert.Equal(Reason.Error, r));
    }
}
