using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Jorrk.Flagon.Tests.Support;
using Microsoft.AspNetCore.Builder;

namespace Jorrk.Flagon.AspNetCore.Tests;

/// <summary>OFREP single and bulk evaluation of string, number and object flags.</summary>
public class OfrepTypedEvaluationTests
{
    private const string BulkPath = "/ofrep/v1/evaluate/flags";
    private static readonly object EmptyContext = new { context = new { } };

    private static Task<FlagonTestHost> StartAsync() =>
        FlagonTestHost.StartAsync(o => o.Endpoints = app => app.MapFlagonOfrep());

    private static Dictionary<string, JsonElement> Variants(string json) =>
        JsonDocument.Parse(json).RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone(), StringComparer.Ordinal);

    private static Task CreateAsync(FlagonTestHost host, string key, string variants, string? defaultVariant) =>
        host.Admin.CreateAsync(new CreateFlagRequest(key, Variants: Variants(variants), DefaultVariant: defaultVariant));

    private static async Task SeedTypedFlagsAsync(FlagonTestHost host)
    {
        await CreateAsync(host, "theme", """{ "light": "light", "dark": "dark" }""", "dark");
        await CreateAsync(host, "page-size", """{ "small": 10, "large": 50 }""", "large");
        await CreateAsync(host, "ratio", """{ "half": 0.5 }""", "half");
        await CreateAsync(host, "layout", """{ "roomy": { "rows": 1, "big": true, "name": "r", "nested": { "a": [1, 2] } } }""", "roomy");
    }

    private static async Task<JsonElement> EvaluateAsync(FlagonTestHost host, string key)
    {
        var response = await host.CreateClient().PostAsJsonAsync($"{BulkPath}/{key}", EmptyContext);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static async Task<Dictionary<string, JsonElement>> EvaluateAllAsync(FlagonTestHost host)
    {
        var response = await host.CreateClient().PostAsJsonAsync(BulkPath, EmptyContext);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("flags")
            .EnumerateArray().ToDictionary(f => f.GetProperty("key").GetString()!);
    }

    private static void AssertTypedValues(Func<string, JsonElement> flag)
    {
        Assert.Equal("dark", flag("theme").GetProperty("value").GetString());
        Assert.Equal("dark", flag("theme").GetProperty("variant").GetString());
        Assert.Equal("STATIC", flag("theme").GetProperty("reason").GetString());
        Assert.Equal(50, flag("page-size").GetProperty("value").GetInt32());
        Assert.Equal(0.5, flag("ratio").GetProperty("value").GetDouble());
        Assert.True(JsonNode.DeepEquals(
            JsonNode.Parse("""{ "rows": 1, "big": true, "name": "r", "nested": { "a": [1, 2] } }"""),
            JsonNode.Parse(flag("layout").GetProperty("value").GetRawText())));
    }

    [Fact]
    public async Task Single_evaluation_returns_the_served_variants_value_of_any_type()
    {
        await using var host = await StartAsync();
        await SeedTypedFlagsAsync(host);

        var flags = new Dictionary<string, JsonElement>();
        foreach (var key in new[] { "theme", "page-size", "ratio", "layout" })
        {
            flags[key] = await EvaluateAsync(host, key);
        }

        AssertTypedValues(key => flags[key]);
    }

    [Fact]
    public async Task Bulk_evaluation_returns_the_served_variants_value_of_any_type()
    {
        await using var host = await StartAsync();
        await SeedTypedFlagsAsync(host);

        var flags = await EvaluateAllAsync(host);

        AssertTypedValues(key => flags[key]);
    }

    [Fact]
    public async Task A_disabled_typed_flag_tells_the_client_to_use_its_code_default()
    {
        await using var host = await StartAsync();
        await host.Store.TryCreateAsync("theme", FlagdJson.Deserialize(
            """{ "state": "DISABLED", "variants": { "light": "light", "dark": "dark" }, "defaultVariant": "dark" }"""));
        await FlagonTestHost.AdvanceToNextRefreshAsync(host);

        var single = await EvaluateAsync(host, "theme");
        var bulk = (await EvaluateAllAsync(host))["theme"];

        foreach (var body in new[] { single, bulk })
        {
            Assert.Equal("DISABLED", body.GetProperty("reason").GetString());
            Assert.False(body.TryGetProperty("value", out _));
            Assert.False(body.TryGetProperty("variant", out _));
        }
    }

    [Fact]
    public async Task A_typed_flag_with_no_default_variant_tells_the_client_to_use_its_code_default()
    {
        await using var host = await StartAsync();
        await CreateAsync(host, "page-size", """{ "small": 10, "large": 50 }""", defaultVariant: null);

        var single = await EvaluateAsync(host, "page-size");
        var bulk = (await EvaluateAllAsync(host))["page-size"];

        foreach (var body in new[] { single, bulk })
        {
            Assert.Equal("DEFAULT", body.GetProperty("reason").GetString());
            Assert.False(body.TryGetProperty("value", out _));
        }
    }

    [Fact]
    public async Task Bulk_evaluation_of_typed_flags_keeps_the_snapshot_version_etag()
    {
        await using var host = await StartAsync();
        await SeedTypedFlagsAsync(host);
        var client = host.CreateClient();
        var first = await client.PostAsJsonAsync(BulkPath, EmptyContext);
        var etag = first.Headers.ETag!;

        var request = new HttpRequestMessage(HttpMethod.Post, BulkPath) { Content = JsonContent.Create(EmptyContext) };
        request.Headers.IfNoneMatch.Add(new EntityTagHeaderValue(etag.Tag));
        var second = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.NotModified, second.StatusCode);
    }
}
