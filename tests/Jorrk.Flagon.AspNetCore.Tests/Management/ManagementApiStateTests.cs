using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Jorrk.Flagon.Storage;
using Jorrk.Flagon.Tests.Support;
using Microsoft.AspNetCore.Builder;

namespace Jorrk.Flagon.AspNetCore.Tests.Management;

/// <summary>Switching a flag's state with <c>PUT flags/{key}/state</c>, and the boolean-only <c>PUT flags/{key}/enabled</c>.</summary>
public class ManagementApiStateTests
{
    private static Task<FlagonTestHost> StartAsync() => FlagonTestHost.StartAsync(o => o.Endpoints = app => app.MapFlagonApi());

    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static Dictionary<string, JsonElement> Variants(string json) =>
        Json(json).EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone(), StringComparer.Ordinal);

    private static StringContent JsonBody(string json) => new(json, Encoding.UTF8, "application/json");

    private static Task<FlagRecord> CreateStringFlagAsync(FlagonTestHost host) =>
        host.Admin.CreateAsync(new CreateFlagRequest("theme", Variants: Variants("""{ "light": "light", "dark": "dark" }"""), DefaultVariant: "dark"));

    private static async Task<JsonElement> ProblemAsync(HttpResponseMessage response, HttpStatusCode status)
    {
        Assert.Equal(status, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    [Fact]
    public async Task Disabling_and_enabling_a_flag_returns_it_with_its_new_state()
    {
        await using var host = await StartAsync();
        var created = await CreateStringFlagAsync(host);
        var client = host.CreateClient();

        var disabled = await client.PutAsync("/flagon/api/flags/theme/state", JsonBody($$"""{ "state": "disabled", "version": {{created.Version}} }"""));

        Assert.Equal(HttpStatusCode.OK, disabled.StatusCode);
        var flag = await disabled.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("disabled", flag.GetProperty("state").GetString());
        Assert.Equal("dark", flag.GetProperty("defaultVariant").GetString());
        Assert.Equal(EvaluationReason.Disabled, host.Flagon.GetStringDetails("theme", "code-default").Reason);

        var request = new HttpRequestMessage(HttpMethod.Put, "/flagon/api/flags/theme/state") { Content = JsonBody("""{ "state": "enabled" }""") };
        request.Headers.IfMatch.Add(new EntityTagHeaderValue($"\"{flag.GetProperty("version").GetInt64()}\""));
        var enabled = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, enabled.StatusCode);
        Assert.Equal("enabled", (await enabled.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("state").GetString());
        Assert.Equal("dark", host.Flagon.GetString("theme", "code-default"));
    }

    [Theory]
    [InlineData("""{ "version": 1 }""", "'state'")]
    [InlineData("""{ "state": "off", "version": 1 }""", "'off'")]
    [InlineData("""{ "state": null, "version": 1 }""", "'state'")]
    public async Task A_missing_or_unknown_state_is_a_bad_request(string body, string message)
    {
        await using var host = await StartAsync();
        await CreateStringFlagAsync(host);

        var problem = await ProblemAsync(await host.CreateClient().PutAsync("/flagon/api/flags/theme/state", JsonBody(body)), HttpStatusCode.BadRequest);

        Assert.Contains(message, problem.GetProperty("detail").GetString(), StringComparison.Ordinal);
        Assert.Equal(FlagState.Enabled, (await host.Admin.GetAsync("theme"))!.Document.State);
    }

    [Fact]
    public async Task Switching_state_with_a_stale_version_is_a_conflict()
    {
        await using var host = await StartAsync();
        var created = await CreateStringFlagAsync(host);
        var current = await host.Admin.SetStateAsync("theme", FlagState.Disabled, created.Version);

        var response = await host.CreateClient().PutAsync(
            "/flagon/api/flags/theme/state", JsonBody($$"""{ "state": "enabled", "version": {{created.Version}} }"""));

        var problem = await ProblemAsync(response, HttpStatusCode.Conflict);
        Assert.Equal(current.Version, problem.GetProperty("currentVersion").GetInt64());
        Assert.Equal(FlagState.Disabled, (await host.Admin.GetAsync("theme"))!.Document.State);
    }

    [Fact]
    public async Task Switching_the_state_of_an_archived_flag_is_a_bad_request()
    {
        await using var host = await StartAsync();
        var created = await CreateStringFlagAsync(host);
        var archived = await host.Admin.ArchiveAsync("theme", created.Version);

        var response = await host.CreateClient().PutAsync(
            "/flagon/api/flags/theme/state", JsonBody($$"""{ "state": "disabled", "version": {{archived.Version}} }"""));

        var problem = await ProblemAsync(response, HttpStatusCode.BadRequest);
        Assert.Contains("archived", problem.GetProperty("detail").GetString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task The_enabled_toggle_rejects_a_non_boolean_flag()
    {
        await using var host = await StartAsync();
        var created = await CreateStringFlagAsync(host);

        var response = await host.CreateClient().PutAsync(
            "/flagon/api/flags/theme/enabled", JsonBody($$"""{ "enabled": true, "version": {{created.Version}} }"""));

        var problem = await ProblemAsync(response, HttpStatusCode.BadRequest);
        Assert.Contains("not a boolean flag", problem.GetProperty("detail").GetString(), StringComparison.Ordinal);
        Assert.Equal(created, await host.Admin.GetAsync("theme"));
    }
}
