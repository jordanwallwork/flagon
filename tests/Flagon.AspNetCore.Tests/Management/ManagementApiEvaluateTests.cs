using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Flagon.Tests.Support;
using Microsoft.AspNetCore.Builder;

namespace Flagon.AspNetCore.Tests.Management;

/// <summary>
/// <c>POST flags/{key}/evaluate</c>: previews the stored flag, or an unsaved draft document, against a context, with the
/// same result real evaluation would give.
/// </summary>
public class ManagementApiEvaluateTests
{
    private static readonly EvaluationContext Gb = new("alice", new Dictionary<string, object?> { ["country"] = "GB" });

    private static readonly object GbContext = new { targetingKey = "alice", country = "GB" };

    private static readonly JsonElement GbRule = Json("""{ "if": [ { "==": [ { "var": "country" }, "GB" ] }, "on", null ] }""");

    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static Task<FlagonTestHost> StartAsync() => FlagonTestHost.StartAsync(o => o.Endpoints = app => app.MapFlagonApi());

    private static async Task<JsonElement> EvaluateAsync(FlagonTestHost host, string key, object body)
    {
        var response = await host.CreateClient().PostAsJsonAsync($"/flagon/api/flags/{key}/evaluate", body);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static async Task<JsonElement> ProblemAsync(HttpResponseMessage response, HttpStatusCode status)
    {
        Assert.Equal(status, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static void AssertMatches(FlagEvaluationDetails<bool> expected, JsonElement actual)
    {
        Assert.Equal(expected.Value, actual.GetProperty("value").GetBoolean());
        Assert.Equal(expected.Variant, actual.GetProperty("variant").GetString());
        Assert.Equal(expected.Reason.ToWireName(), actual.GetProperty("reason").GetString());
        if (expected.ErrorCode == ErrorCode.None)
        {
            Assert.False(actual.TryGetProperty("errorCode", out _));
        }
        else
        {
            Assert.Equal(expected.ErrorCode.ToWireName(), actual.GetProperty("errorCode").GetString());
            Assert.Equal(expected.ErrorMessage, actual.GetProperty("errorMessage").GetString());
        }
    }

    [Fact]
    public async Task The_stored_flag_evaluates_as_IFlagon_would_for_the_same_context()
    {
        await using var host = await StartAsync();
        await host.Admin.CreateAsync(new CreateFlagRequest("gb-only", Targeting: GbRule));

        var result = await EvaluateAsync(host, "gb-only", new { context = GbContext });

        Assert.Equal("on", result.GetProperty("variant").GetString());
        Assert.Equal("TARGETING_MATCH", result.GetProperty("reason").GetString());
        AssertMatches(host.Flagon.GetBooleanDetails("gb-only", false, Gb), result);
    }

    [Fact]
    public async Task A_draft_document_evaluates_as_IFlagon_would_once_that_document_is_stored()
    {
        await using var host = await StartAsync();
        var created = await host.Admin.CreateAsync(new CreateFlagRequest("gb-only"));
        var draft = Json("""
            { "state": "ENABLED", "variants": { "on": true, "off": false }, "defaultVariant": "off",
              "targeting": { "if": [ { "==": [ { "var": "country" }, "GB" ] }, "on", null ] } }
            """);

        var result = await EvaluateAsync(host, "gb-only", new { context = GbContext, document = draft });

        Assert.Equal("TARGETING_MATCH", result.GetProperty("reason").GetString());
        Assert.Equal(EvaluationReason.Static, host.Flagon.GetBooleanDetails("gb-only", false, Gb).Reason); // nothing saved
        await host.Admin.SetTargetingAsync("gb-only", GbRule, created.Version);
        AssertMatches(host.Flagon.GetBooleanDetails("gb-only", false, Gb), result);
    }

    [Fact]
    public async Task A_draft_whose_rule_fails_at_evaluation_reports_the_same_error_as_IFlagon()
    {
        await using var host = await StartAsync();
        var created = await host.Admin.CreateAsync(new CreateFlagRequest("names-a-ghost"));
        var ghostRule = Json("""{ "if": [ true, "ghost", null ] }""");
        var draft = Json("""
            { "state": "ENABLED", "variants": { "on": true, "off": false }, "defaultVariant": "off",
              "targeting": { "if": [ true, "ghost", null ] } }
            """);

        var result = await EvaluateAsync(host, "names-a-ghost", new { context = GbContext, document = draft });

        Assert.Equal("GENERAL", result.GetProperty("errorCode").GetString());
        await host.Admin.SetTargetingAsync("names-a-ghost", ghostRule, created.Version);
        AssertMatches(host.Flagon.GetBooleanDetails("names-a-ghost", false, Gb), result);
    }

    [Fact]
    public async Task A_draft_is_evaluated_even_for_a_key_that_is_not_stored_yet()
    {
        await using var host = await StartAsync();
        var draft = Json("""{ "state": "ENABLED", "variants": { "on": true, "off": false }, "defaultVariant": "on" }""");

        var result = await EvaluateAsync(host, "brand-new", new { document = draft });

        Assert.True(result.GetProperty("value").GetBoolean());
        Assert.Equal("STATIC", result.GetProperty("reason").GetString());
    }

    [Fact]
    public async Task A_draft_is_evaluated_as_given_even_when_the_stored_flag_is_archived()
    {
        await using var host = await StartAsync();
        var created = await host.Admin.CreateAsync(new CreateFlagRequest("retired", Enabled: true));
        await host.Admin.ArchiveAsync("retired", created.Version);
        var draft = Json("""{ "state": "ENABLED", "variants": { "on": true, "off": false }, "defaultVariant": "on" }""");

        var stored = await EvaluateAsync(host, "retired", new { context = GbContext });
        var drafted = await EvaluateAsync(host, "retired", new { context = GbContext, document = draft });

        Assert.Equal("ARCHIVED", stored.GetProperty("reason").GetString());
        Assert.Equal("STATIC", drafted.GetProperty("reason").GetString());
    }

    [Fact]
    public async Task A_draft_whose_rule_does_not_compile_is_a_bad_request_naming_the_operator_and_path()
    {
        await using var host = await StartAsync();
        var draft = Json("""
            { "state": "ENABLED", "variants": { "on": true, "off": false }, "defaultVariant": "off",
              "targeting": { "if": [ { "regex": [ { "var": "email" }, ".*" ] }, "on", null ] } }
            """);

        var response = await host.CreateClient().PostAsJsonAsync("/flagon/api/flags/broken/evaluate", new { context = GbContext, document = draft });

        var problem = await ProblemAsync(response, HttpStatusCode.BadRequest);
        Assert.Equal("Invalid targeting rule", problem.GetProperty("title").GetString());
        Assert.Equal("broken", problem.GetProperty("key").GetString());
        Assert.Equal("regex", problem.GetProperty("operator").GetString());
        Assert.Equal("/if/0", problem.GetProperty("path").GetString());
    }

    [Theory]
    [InlineData("""{ "context": {}, "document": { "variants": { "on": true }, "defaultVariant": "on" } }""")] // no state
    [InlineData("""{ "context": {}, "document": [] }""")]
    [InlineData("""{ "context": [] }""")]
    [InlineData("""{ "context": { "targetingKey": 42 } }""")]
    [InlineData("""not json""")]
    public async Task A_malformed_document_or_context_is_a_bad_request(string body)
    {
        await using var host = await StartAsync();
        await host.Admin.CreateAsync(new CreateFlagRequest("new-checkout"));

        var response = await host.CreateClient().PostAsync(
            "/flagon/api/flags/new-checkout/evaluate", new StringContent(body, System.Text.Encoding.UTF8, "application/json"));

        await ProblemAsync(response, HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Evaluating_a_stored_flag_that_does_not_exist_is_not_found()
    {
        await using var host = await StartAsync();

        var response = await host.CreateClient().PostAsJsonAsync("/flagon/api/flags/nope/evaluate", new { context = GbContext });

        var problem = await ProblemAsync(response, HttpStatusCode.NotFound);
        Assert.Equal("Flag not found", problem.GetProperty("title").GetString());
    }

    [Fact]
    public async Task An_empty_body_evaluates_the_stored_flag_with_an_empty_context()
    {
        await using var host = await StartAsync();
        await host.Admin.CreateAsync(new CreateFlagRequest("new-checkout", Enabled: true));

        var response = await host.CreateClient().PostAsync("/flagon/api/flags/new-checkout/evaluate", null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        AssertMatches(host.Flagon.GetBooleanDetails("new-checkout", false, EvaluationContext.Empty), await response.Content.ReadFromJsonAsync<JsonElement>());
    }
}
