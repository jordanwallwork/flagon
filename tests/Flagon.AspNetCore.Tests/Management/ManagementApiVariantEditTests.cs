using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Flagon.Storage;
using Flagon.Tests.Support;
using Microsoft.AspNetCore.Builder;

namespace Flagon.AspNetCore.Tests.Management;

/// <summary>Editing a flag's variants and default variant over the management API.</summary>
public class ManagementApiVariantEditTests
{
    private static Task<FlagonTestHost> StartAsync() => FlagonTestHost.StartAsync(o => o.Endpoints = app => app.MapFlagonApi());

    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static Dictionary<string, JsonElement> Variants(string json) =>
        Json(json).EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone(), StringComparer.Ordinal);

    private static StringContent JsonBody(string json) => new(json, Encoding.UTF8, "application/json");

    private static HttpRequestMessage Put(string uri, string json, long? ifMatch = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Put, uri) { Content = JsonBody(json) };
        if (ifMatch is { } version)
        {
            request.Headers.IfMatch.Add(new EntityTagHeaderValue($"\"{version}\""));
        }

        return request;
    }

    private static async Task<JsonElement> ProblemAsync(HttpResponseMessage response, HttpStatusCode status)
    {
        Assert.Equal(status, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static Task<FlagRecord> CreateThemeAsync(FlagonTestHost host) => host.Admin.CreateAsync(new CreateFlagRequest(
        "theme", Variants: Variants("""{ "light": "light", "dark": "dark" }"""), DefaultVariant: "light"));

    [Fact]
    public async Task Putting_variants_replaces_them_and_returns_the_flag_with_its_new_etag()
    {
        await using var host = await StartAsync();
        var created = await CreateThemeAsync(host);

        var response = await host.CreateClient().SendAsync(Put("/flagon/api/flags/theme/variants", $$"""
            { "variants": { "light": "light", "solar": "solarized" }, "defaultVariant": "solar", "version": {{created.Version}} }
            """));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var flag = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("solar", flag.GetProperty("defaultVariant").GetString());
        Assert.Equal("solarized", flag.GetProperty("value").GetString());
        Assert.Equal(["light", "solar"], flag.GetProperty("variants").EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
        Assert.Equal($"\"{created.Version + 1}\"", response.Headers.ETag?.Tag);
        Assert.Equal("solarized", host.Flagon.GetString("theme", "fallback"));
    }

    [Fact]
    public async Task Putting_variants_without_a_default_variant_clears_it()
    {
        await using var host = await StartAsync();
        var created = await CreateThemeAsync(host);

        var response = await host.CreateClient().SendAsync(
            Put("/flagon/api/flags/theme/variants", """{ "variants": { "dark": "dark" } }""", ifMatch: created.Version));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Null((await host.Admin.GetAsync("theme"))!.Document.DefaultVariant);
    }

    [Theory]
    [InlineData("""{ "variants": { "light": 1 }, "defaultVariant": "light" }""", "type")]
    [InlineData("""{ "variants": { "dark": "dark" }, "defaultVariant": "light" }""", "default variant")]
    [InlineData("""{ "variants": {} }""", "at least one variant")]
    [InlineData("""{ "variants": { "a": "x", "a": "y" } }""", "more than once")]
    [InlineData("""{ "variants": { "a": null } }""", "null")]
    [InlineData("""{ "variants": ["a"] }""", "variants")]
    [InlineData("""{ "defaultVariant": "light" }""", "'variants' is required")]
    [InlineData("""{ "variants": null }""", "'variants' is required")]
    public async Task Invalid_variant_edits_are_a_bad_request_and_nothing_is_written(string body, string detail)
    {
        await using var host = await StartAsync();
        var created = await CreateThemeAsync(host);

        var problem = await ProblemAsync(
            await host.CreateClient().SendAsync(Put("/flagon/api/flags/theme/variants", body, ifMatch: created.Version)), HttpStatusCode.BadRequest);

        Assert.Contains(detail, problem.GetProperty("detail").GetString(), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(created, await host.Admin.GetAsync("theme"));
    }

    [Fact]
    public async Task Putting_variants_on_a_boolean_flag_is_a_bad_request()
    {
        await using var host = await StartAsync();
        var created = await host.Admin.CreateAsync(new CreateFlagRequest("beta"));

        var problem = await ProblemAsync(await host.CreateClient().SendAsync(Put("/flagon/api/flags/beta/variants",
            """{ "variants": { "on": true, "off": false }, "defaultVariant": "on" }""", ifMatch: created.Version)), HttpStatusCode.BadRequest);

        Assert.Contains("boolean", problem.GetProperty("detail").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Putting_the_default_variant_switches_the_served_value()
    {
        await using var host = await StartAsync();
        var created = await CreateThemeAsync(host);

        var response = await host.CreateClient().SendAsync(
            Put("/flagon/api/flags/theme/default-variant", $$"""{ "variant": "dark", "version": {{created.Version}} }"""));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var flag = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("dark", flag.GetProperty("defaultVariant").GetString());
        Assert.Equal("dark", flag.GetProperty("value").GetString());
        Assert.Equal($"\"{created.Version + 1}\"", response.Headers.ETag?.Tag);
    }

    [Fact]
    public async Task Putting_a_null_default_variant_clears_it_and_an_absent_one_is_a_bad_request()
    {
        await using var host = await StartAsync();
        var created = await CreateThemeAsync(host);
        var client = host.CreateClient();

        var absent = await ProblemAsync(
            await client.SendAsync(Put("/flagon/api/flags/theme/default-variant", "{}", ifMatch: created.Version)), HttpStatusCode.BadRequest);
        var cleared = await client.SendAsync(Put("/flagon/api/flags/theme/default-variant", """{ "variant": null }""", ifMatch: created.Version));

        Assert.Contains("'variant' is required", absent.GetProperty("detail").GetString(), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.OK, cleared.StatusCode);
        var flag = await cleared.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(JsonValueKind.Null, flag.GetProperty("defaultVariant").ValueKind);
        Assert.Equal(JsonValueKind.Null, flag.GetProperty("value").ValueKind);
    }

    [Fact]
    public async Task Putting_an_unknown_default_variant_is_a_bad_request()
    {
        await using var host = await StartAsync();
        var created = await CreateThemeAsync(host);

        var problem = await ProblemAsync(await host.CreateClient().SendAsync(
            Put("/flagon/api/flags/theme/default-variant", """{ "variant": "solar" }""", ifMatch: created.Version)), HttpStatusCode.BadRequest);

        Assert.Contains("'solar'", problem.GetProperty("detail").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Setting_a_boolean_flags_default_variant_toggles_it()
    {
        await using var host = await StartAsync();
        var created = await host.Admin.CreateAsync(new CreateFlagRequest("beta"));

        var flag = await (await host.CreateClient().SendAsync(
            Put("/flagon/api/flags/beta/default-variant", """{ "variant": "on" }""", ifMatch: created.Version))).Content.ReadFromJsonAsync<JsonElement>();

        Assert.True(flag.GetProperty("enabled").GetBoolean());
        Assert.True(host.Flagon.IsEnabled("beta"));
    }

    [Theory]
    [InlineData("variants", """{ "variants": { "dark": "dark" }, "defaultVariant": "dark" }""")]
    [InlineData("default-variant", """{ "variant": "dark" }""")]
    public async Task Stale_versions_conflict_by_header_or_body(string path, string body)
    {
        await using var host = await StartAsync();
        var created = await CreateThemeAsync(host);
        var current = await host.Admin.UpdateMetadataAsync("theme", new UpdateFlagMetadataRequest("Theme"), created.Version);
        var client = host.CreateClient();
        var withBodyVersion = body.Insert(body.LastIndexOf('}'), $", \"version\": {created.Version} ");

        var viaHeader = await ProblemAsync(
            await client.SendAsync(Put($"/flagon/api/flags/theme/{path}", body, ifMatch: created.Version)), HttpStatusCode.Conflict);
        var viaBody = await ProblemAsync(
            await client.SendAsync(Put($"/flagon/api/flags/theme/{path}", withBodyVersion)), HttpStatusCode.Conflict);

        Assert.Equal(current.Version, viaHeader.GetProperty("currentVersion").GetInt64());
        Assert.Equal(current.Version, viaBody.GetProperty("currentVersion").GetInt64());
        Assert.Equal(current, await host.Admin.GetAsync("theme"));
    }

    [Theory]
    [InlineData("variants", """{ "variants": { "dark": "dark" }, "defaultVariant": "dark" }""")]
    [InlineData("default-variant", """{ "variant": "dark" }""")]
    public async Task Archived_and_missing_flags_are_rejected(string path, string body)
    {
        await using var host = await StartAsync();
        await CreateThemeAsync(host);
        var archived = await host.Admin.ArchiveAsync("theme", 1);
        var client = host.CreateClient();

        var archivedProblem = await ProblemAsync(
            await client.SendAsync(Put($"/flagon/api/flags/theme/{path}", body, ifMatch: archived.Version)), HttpStatusCode.BadRequest);
        await ProblemAsync(await client.SendAsync(Put($"/flagon/api/flags/missing/{path}", body, ifMatch: 1)), HttpStatusCode.NotFound);

        Assert.Contains("archived", archivedProblem.GetProperty("detail").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Variant_edits_are_audited_as_updates_with_before_and_after_documents()
    {
        await using var host = await StartAsync();
        var created = await CreateThemeAsync(host);
        await host.CreateClient().SendAsync(Put("/flagon/api/flags/theme/variants",
            """{ "variants": { "light": "white", "dark": "dark" }, "defaultVariant": "dark" }""", ifMatch: created.Version));

        var latest = (await host.CreateClient().GetFromJsonAsync<JsonElement>("/flagon/api/flags/theme/audit"))[0];

        Assert.Equal("updated", latest.GetProperty("action").GetString());
        Assert.Equal("light", latest.GetProperty("before").GetProperty("variants").GetProperty("light").GetString());
        Assert.Equal("light", latest.GetProperty("before").GetProperty("defaultVariant").GetString());
        Assert.Equal("white", latest.GetProperty("after").GetProperty("variants").GetProperty("light").GetString());
        Assert.Equal("dark", latest.GetProperty("after").GetProperty("defaultVariant").GetString());
    }
}
