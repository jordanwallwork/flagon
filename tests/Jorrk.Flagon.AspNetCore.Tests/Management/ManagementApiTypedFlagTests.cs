using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Jorrk.Flagon.Tests.Support;
using Microsoft.AspNetCore.Builder;

namespace Jorrk.Flagon.AspNetCore.Tests.Management;

/// <summary>Creating and viewing string, number and object flags over the management API.</summary>
public class ManagementApiTypedFlagTests
{
    private static Task<FlagonTestHost> StartAsync() => FlagonTestHost.StartAsync(o => o.Endpoints = app => app.MapFlagonApi());

    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static Dictionary<string, JsonElement> Variants(string json) =>
        Json(json).EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone(), StringComparer.Ordinal);

    private static StringContent JsonBody(string json) => new(json, Encoding.UTF8, "application/json");

    private static IEnumerable<string?> Keys(JsonElement flags) => flags.EnumerateArray().Select(f => f.GetProperty("key").GetString());

    private static async Task<JsonElement> ProblemAsync(HttpResponseMessage response, HttpStatusCode status)
    {
        Assert.Equal(status, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    [Fact]
    public async Task Creating_a_flag_with_variants_returns_its_type_variants_and_default_value()
    {
        await using var host = await StartAsync();

        var response = await host.CreateClient().PostAsync("/flagon/api/flags", JsonBody("""
            { "key": "layout", "variants": { "compact": { "rows": 3 }, "roomy": { "rows": 1, "big": true } }, "defaultVariant": "roomy" }
            """));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var flag = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("object", flag.GetProperty("type").GetString());
        Assert.Equal(JsonValueKind.Null, flag.GetProperty("enabled").ValueKind);
        Assert.Equal("roomy", flag.GetProperty("defaultVariant").GetString());
        Assert.Equal(3, flag.GetProperty("variants").GetProperty("compact").GetProperty("rows").GetInt32());
        Assert.True(flag.GetProperty("value").GetProperty("big").GetBoolean());
        Assert.Equal(FlagType.Object, (await host.Admin.GetAsync("layout"))!.Document.Type);
    }

    [Fact]
    public async Task A_flag_without_a_default_variant_has_a_null_value()
    {
        await using var host = await StartAsync();

        var response = await host.CreateClient().PostAsync("/flagon/api/flags", JsonBody("""
            { "key": "page-size", "variants": { "small": 10, "large": 50 } }
            """));

        var flag = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("number", flag.GetProperty("type").GetString());
        Assert.Equal(JsonValueKind.Null, flag.GetProperty("defaultVariant").ValueKind);
        Assert.Equal(JsonValueKind.Null, flag.GetProperty("value").ValueKind);
        Assert.Equal(50, flag.GetProperty("variants").GetProperty("large").GetInt32());
    }

    [Fact]
    public async Task A_boolean_flag_reports_its_type_variants_value_and_enabled()
    {
        await using var host = await StartAsync();
        await host.Admin.CreateAsync(new CreateFlagRequest("beta", Enabled: true));

        var flag = await host.CreateClient().GetFromJsonAsync<JsonElement>("/flagon/api/flags/beta");

        Assert.Equal("boolean", flag.GetProperty("type").GetString());
        Assert.True(flag.GetProperty("enabled").GetBoolean());
        Assert.True(flag.GetProperty("value").GetBoolean());
        Assert.False(flag.GetProperty("variants").GetProperty("off").GetBoolean());
    }

    [Theory]
    [InlineData("""{ "key": "k", "variants": {} }""", "at least one variant")]
    [InlineData("""{ "key": "k", "variants": { "": "x" } }""", "empty")]
    [InlineData("""{ "key": "k", "variants": { "a": "x", "a": "y" } }""", "more than once")]
    [InlineData("""{ "key": "k", "variants": { "a": "x", "b": 2 } }""", "one type")]
    [InlineData("""{ "key": "k", "variants": { "a": [1] } }""", "array")]
    [InlineData("""{ "key": "k", "variants": { "a": null } }""", "null")]
    [InlineData("""{ "key": "k", "variants": { "a": "x" }, "defaultVariant": "b" }""", "'b'")]
    [InlineData("""{ "key": "k", "variants": { "a": "x" }, "defaultVariant": "a", "enabled": true }""", "enabled")]
    [InlineData("""{ "key": "k", "variants": ["a"] }""", "variants")]
    [InlineData("""{ "key": "k", "defaultVariant": "a" }""", "no variants")]
    [InlineData("""{ "key": "k", "variants": { "yes": true, "no": false }, "defaultVariant": "yes" }""", "\"on\": true")]
    public async Task Invalid_variants_are_a_bad_request_with_problem_details(string body, string detail)
    {
        await using var host = await StartAsync();

        var problem = await ProblemAsync(await host.CreateClient().PostAsync("/flagon/api/flags", JsonBody(body)), HttpStatusCode.BadRequest);

        Assert.Contains(detail, problem.GetProperty("detail").GetString(), StringComparison.OrdinalIgnoreCase);
        Assert.Empty(await host.Admin.ListAsync());
    }

    [Fact]
    public async Task A_variant_name_longer_than_128_characters_is_a_bad_request()
    {
        await using var host = await StartAsync();
        var body = JsonSerializer.Serialize(new { key = "k", variants = new Dictionary<string, int> { [new string('v', 129)] = 1 } });

        var problem = await ProblemAsync(await host.CreateClient().PostAsync("/flagon/api/flags", JsonBody(body)), HttpStatusCode.BadRequest);

        Assert.Contains("128", problem.GetProperty("detail").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_flag_list_can_be_filtered_by_type()
    {
        await using var host = await StartAsync();
        await host.Admin.CreateAsync(new CreateFlagRequest("beta"));
        await host.Admin.CreateAsync(new CreateFlagRequest("theme", Variants: Variants("""{ "light": "light" }"""), DefaultVariant: "light"));
        await host.Admin.CreateAsync(new CreateFlagRequest("page-size", Variants: Variants("""{ "small": 10 }""")));
        await host.Admin.CreateAsync(new CreateFlagRequest("layout", Variants: Variants("""{ "compact": {} }""")));
        var client = host.CreateClient();

        Assert.Equal(["beta"], Keys(await client.GetFromJsonAsync<JsonElement>("/flagon/api/flags?type=boolean")));
        Assert.Equal(["theme"], Keys(await client.GetFromJsonAsync<JsonElement>("/flagon/api/flags?type=string")));
        Assert.Equal(["page-size"], Keys(await client.GetFromJsonAsync<JsonElement>("/flagon/api/flags?type=NUMBER")));
        Assert.Equal(["layout"], Keys(await client.GetFromJsonAsync<JsonElement>("/flagon/api/flags?type=object")));
        Assert.Equal(["beta"], Keys(await client.GetFromJsonAsync<JsonElement>("/flagon/api/flags?enabled=false")));
        await ProblemAsync(await client.GetAsync("/flagon/api/flags?type=list"), HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Evaluating_a_stored_typed_flag_returns_the_served_variants_value()
    {
        await using var host = await StartAsync();
        await host.Admin.CreateAsync(new CreateFlagRequest(
            "theme",
            Variants: Variants("""{ "light": "light", "dark": "dark" }"""),
            DefaultVariant: "light",
            Targeting: Json("""{ "if": [ { "==": [ { "var": "country" }, "GB" ] }, "dark", null ] }""")));

        var gb = await (await host.CreateClient().PostAsJsonAsync("/flagon/api/flags/theme/evaluate", new { context = new { country = "GB" } }))
            .Content.ReadFromJsonAsync<JsonElement>();
        var fr = await (await host.CreateClient().PostAsJsonAsync("/flagon/api/flags/theme/evaluate", new { context = new { country = "FR" } }))
            .Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal("dark", gb.GetProperty("value").GetString());
        Assert.Equal("TARGETING_MATCH", gb.GetProperty("reason").GetString());
        Assert.Equal("light", fr.GetProperty("value").GetString());
        Assert.Equal("light", fr.GetProperty("variant").GetString());
    }

    [Theory]
    [InlineData("""{ "small": 10, "large": 2.5 }""", "large", "2.5")]
    [InlineData("""{ "compact": { "rows": 3, "tags": ["a"] } }""", "compact", """{"rows":3,"tags":["a"]}""")]
    [InlineData("""{ "on": true, "off": false }""", "off", "false")]
    [InlineData("""{ "blank": "" }""", "blank", "\"\"")]
    public async Task Evaluating_an_unsaved_typed_document_returns_its_value_untyped(string variants, string defaultVariant, string value)
    {
        await using var host = await StartAsync();
        var document = $$"""{ "state": "ENABLED", "variants": {{variants}}, "defaultVariant": "{{defaultVariant}}" }""";

        var response = await host.CreateClient().PostAsync("/flagon/api/flags/draft/evaluate", JsonBody($$"""{ "document": {{document}} }"""));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(value, result.GetProperty("value").GetRawText().Replace(" ", "", StringComparison.Ordinal));
        Assert.Equal(defaultVariant, result.GetProperty("variant").GetString());
        Assert.Equal("STATIC", result.GetProperty("reason").GetString());
        Assert.False(result.TryGetProperty("errorCode", out _));
    }

    [Fact]
    public async Task Evaluating_a_flag_that_serves_no_variant_returns_a_null_value()
    {
        await using var host = await StartAsync();
        await host.Admin.CreateAsync(new CreateFlagRequest("page-size", Variants: Variants("""{ "small": 10 }""")));

        var result = await (await host.CreateClient().PostAsync("/flagon/api/flags/page-size/evaluate", null)).Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(JsonValueKind.Null, result.GetProperty("value").ValueKind);
        Assert.Equal(JsonValueKind.Null, result.GetProperty("variant").ValueKind);
        Assert.Equal("DEFAULT", result.GetProperty("reason").GetString());
    }

    [Fact]
    public async Task A_flags_usage_shows_each_apps_typed_code_default_as_json()
    {
        var store = new Jorrk.Flagon.Storage.InMemoryFlagStore();
        await using (var web = await FlagonTestHost.StartAsync(o => { o.Store = store; o.ApplicationName = "Web"; }))
        await using (var worker = await FlagonTestHost.StartAsync(o => { o.Store = store; o.ApplicationName = "Worker"; }))
        {
            web.Flagon.GetObject("layout", Json("""{ "title": "Pics", "columns": 3 }"""));
            worker.Flagon.GetString("layout", "grid");
        } // disposing flushes usage

        await using var host = await FlagonTestHost.StartAsync(o => { o.Store = store; o.Endpoints = app => app.MapFlagonApi(); });
        await host.Admin.CreateAsync(new CreateFlagRequest("layout", Variants: Variants("""{ "grid": { "columns": 3 } }"""), DefaultVariant: "grid"));

        var usage = (await host.CreateClient().GetFromJsonAsync<JsonElement>("/flagon/api/flags/layout")).GetProperty("usage");

        Assert.True(usage.GetProperty("hasDefaultMismatch").GetBoolean());
        var defaults = usage.GetProperty("applications").EnumerateArray().Select(a => a.GetProperty("observedDefault")).ToArray();
        Assert.Equal(JsonValueKind.Object, defaults[0].ValueKind);
        Assert.Equal("""{"columns":3,"title":"Pics"}""", defaults[0].GetRawText());
        Assert.Equal("grid", defaults[1].GetString());
    }
}
