using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Flagon.Tests.Support;
using Microsoft.AspNetCore.Builder;

namespace Flagon.AspNetCore.Tests;

public class OfrepSingleEvaluationTests
{
    private static readonly object EmptyContext = new { context = new { } };

    private static Task<FlagonTestHost> StartAsync() =>
        FlagonTestHost.StartAsync(o => o.Endpoints = app => app.MapFlagonOfrep());

    [Fact]
    public async Task An_enabled_flag_evaluates_to_true_with_its_variant_and_a_static_reason()
    {
        await using var host = await StartAsync();
        await host.Admin.CreateAsync(new CreateFlagRequest("new-checkout", Enabled: true));

        var response = await host.CreateClient().PostAsJsonAsync("/ofrep/v1/evaluate/flags/new-checkout", EmptyContext);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("new-checkout", body.GetProperty("key").GetString());
        Assert.True(body.GetProperty("value").GetBoolean());
        Assert.Equal("on", body.GetProperty("variant").GetString());
        Assert.Equal("STATIC", body.GetProperty("reason").GetString());
    }

    [Fact]
    public async Task A_missing_flag_returns_404_with_the_ofrep_flag_not_found_error()
    {
        await using var host = await StartAsync();

        var response = await host.CreateClient().PostAsJsonAsync("/ofrep/v1/evaluate/flags/no-such-flag", EmptyContext);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("no-such-flag", body.GetProperty("key").GetString());
        Assert.Equal("FLAG_NOT_FOUND", body.GetProperty("errorCode").GetString());
        Assert.False(string.IsNullOrEmpty(body.GetProperty("errorDetails").GetString()));
    }

    [Fact]
    public async Task A_disabled_flag_tells_the_client_to_use_its_code_default()
    {
        await using var host = await StartAsync();
        await host.Store.TryCreateAsync("legacy", FlagdJson.Deserialize(
            """{ "state": "DISABLED", "variants": { "on": true, "off": false }, "defaultVariant": "on" }"""));
        await FlagonTestHost.AdvanceToNextRefreshAsync(host);

        var response = await host.CreateClient().PostAsJsonAsync("/ofrep/v1/evaluate/flags/legacy", EmptyContext);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("DISABLED", body.GetProperty("reason").GetString());
        // OFREP "code default" shape: no value, so the client SDK returns the caller's default.
        Assert.False(body.TryGetProperty("value", out _));
    }

    [Fact]
    public async Task A_flag_whose_document_cannot_be_interpreted_returns_400_with_a_parse_error()
    {
        await using var host = await StartAsync();
        await host.Store.TryCreateAsync("broken", FlagdJson.Deserialize(
            """{ "state": "ENABLED", "variants": { "on": true, "off": false }, "defaultVariant": "missing" }"""));
        await FlagonTestHost.AdvanceToNextRefreshAsync(host);

        var response = await host.CreateClient().PostAsJsonAsync("/ofrep/v1/evaluate/flags/broken", EmptyContext);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("broken", body.GetProperty("key").GetString());
        Assert.Equal("PARSE_ERROR", body.GetProperty("errorCode").GetString());
    }

    [Fact]
    public async Task A_context_with_a_targeting_key_and_attributes_is_accepted()
    {
        await using var host = await StartAsync();
        await host.Admin.CreateAsync(new CreateFlagRequest("new-checkout", Enabled: true));

        var response = await host.CreateClient().PostAsJsonAsync("/ofrep/v1/evaluate/flags/new-checkout",
            new { context = new { targetingKey = "user-123", email = "a@example.com", beta = true, age = 42 } });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("""{ "context": "a string" }""")]
    [InlineData("""{ "context": { "targetingKey": 42 } }""")]
    public async Task A_malformed_context_returns_400_invalid_context(string requestBody)
    {
        await using var host = await StartAsync();
        await host.Admin.CreateAsync(new CreateFlagRequest("new-checkout", Enabled: true));

        var response = await host.CreateClient().PostAsync("/ofrep/v1/evaluate/flags/new-checkout",
            new StringContent(requestBody, System.Text.Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("new-checkout", body.GetProperty("key").GetString());
        Assert.Equal("INVALID_CONTEXT", body.GetProperty("errorCode").GetString());
    }

    [Fact]
    public async Task The_endpoints_can_be_mounted_under_a_prefix()
    {
        await using var host = await FlagonTestHost.StartAsync(o => o.Endpoints = app => app.MapFlagonOfrep("/flags"));
        await host.Admin.CreateAsync(new CreateFlagRequest("new-checkout", Enabled: true));

        var client = host.CreateClient();
        var mounted = await client.PostAsJsonAsync("/flags/ofrep/v1/evaluate/flags/new-checkout", EmptyContext);
        var root = await client.PostAsJsonAsync("/ofrep/v1/evaluate/flags/new-checkout", EmptyContext);

        Assert.Equal(HttpStatusCode.OK, mounted.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, root.StatusCode);
    }
}
