using System.Text.Json;
using Flagon.Storage;
using Flagon.Tests.Support;

namespace Flagon.Tests;

/// <summary>Typed getters (string, int, double, object) on <see cref="IFlagon"/>, through the host seam.</summary>
public class TypedEvaluationTests
{
    private static async Task<FlagonTestHost> HostAsync(params (string Key, string Json)[] flags)
    {
        var store = new InMemoryFlagStore();
        foreach (var (key, json) in flags)
        {
            await store.TryCreateAsync(key, FlagdJson.Deserialize(json));
        }

        return await FlagonTestHost.StartAsync(o => o.Store = store);
    }

    private static string Static(string variantsJson, string defaultVariant) =>
        $$"""{ "state": "ENABLED", "variants": {{variantsJson}}, "defaultVariant": "{{defaultVariant}}" }""";

    [Fact]
    public async Task A_string_flag_serves_its_default_variant_statically()
    {
        await using var host = await HostAsync(("theme", Static("""{ "light": "light", "dark": "dark" }""", "dark")));

        Assert.Equal("dark", host.Flagon.GetString("theme", "fallback"));
        Assert.Equal(new FlagEvaluationDetails<string>("theme", "dark", "dark", EvaluationReason.Static),
            host.Flagon.GetStringDetails("theme", "fallback"));
    }

    [Fact]
    public async Task A_number_flag_serves_its_default_variant_as_an_int()
    {
        await using var host = await HostAsync(("page-size", Static("""{ "small": 10, "large": 100 }""", "large")));

        Assert.Equal(100, host.Flagon.GetInt("page-size", 1));
        Assert.Equal(new FlagEvaluationDetails<int>("page-size", 100, "large", EvaluationReason.Static),
            host.Flagon.GetIntDetails("page-size", 1));
    }

    [Fact]
    public async Task A_number_flag_serves_its_default_variant_as_a_double()
    {
        await using var host = await HostAsync(("ratio", Static("""{ "half": 0.5, "whole": 1 }""", "half")));

        Assert.Equal(0.5, host.Flagon.GetDouble("ratio", 0.1));
        Assert.Equal(new FlagEvaluationDetails<double>("ratio", 0.5, "half", EvaluationReason.Static),
            host.Flagon.GetDoubleDetails("ratio", 0.1));
    }

    [Fact]
    public async Task An_object_flag_serves_its_default_variant_as_json()
    {
        await using var host = await HostAsync(("layout", Static("""{ "grid": { "columns": 3, "title": "Pics" }, "list": {} }""", "grid")));

        var details = host.Flagon.GetObjectDetails("layout", JsonDocument.Parse("{}").RootElement);

        Assert.Equal("grid", details.Variant);
        Assert.Equal(EvaluationReason.Static, details.Reason);
        Assert.Equal(3, details.Value.GetProperty("columns").GetInt32());
        Assert.Equal("Pics", host.Flagon.GetObject("layout", default(JsonElement)).GetProperty("title").GetString());
    }

    private sealed record Layout(int Columns, string Title);

    [Fact]
    public async Task An_object_flag_deserialises_into_the_callers_type()
    {
        await using var host = await HostAsync(("layout", Static("""{ "grid": { "columns": 3, "title": "Pics" } }""", "grid")));

        var fallback = new Layout(1, "fallback");
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);

        Assert.Equal(new Layout(3, "Pics"), host.Flagon.GetObject("layout", fallback, options: options));
        Assert.Equal(new FlagEvaluationDetails<Layout>("layout", new Layout(3, "Pics"), "grid", EvaluationReason.Static),
            host.Flagon.GetObjectDetails("layout", fallback, options: options));
    }
}
