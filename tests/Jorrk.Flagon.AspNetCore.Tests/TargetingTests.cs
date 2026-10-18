using System.Net.Http.Json;
using System.Text.Json;
using Jorrk.Flagon.Tests.Support;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Jorrk.Flagon.AspNetCore.Tests;

/// <summary>Targeting driven over HTTP: the OFREP request context, and the ambient context of an ASP.NET Core request.</summary>
public class TargetingTests
{
    private const string BulkPath = "/ofrep/v1/evaluate/flags";

    private static readonly JsonElement GbRule = JsonDocument.Parse(
        """{ "if": [ { "==": [ { "var": "country" }, "GB" ] }, "on", null ] }""").RootElement.Clone();

    private static async Task<FlagonTestHost> StartAsync(Action<IServiceCollection>? services = null)
    {
        var host = await FlagonTestHost.StartAsync(o =>
        {
            o.Services = services;
            o.Endpoints = app =>
            {
                app.MapFlagonOfrep();
                app.MapGet("/ambient", (IFlagon flagon) => Results.Ok(Reason(flagon.GetBooleanDetails("gb-only"))));
                app.MapGet("/explicit", (IFlagon flagon) => Results.Ok(Reason(flagon.GetBooleanDetails("gb-only", context:
                    new EvaluationContext("explicit", new Dictionary<string, object?> { ["country"] = "FR" })))));
            };
        });
        await host.Admin.CreateAsync(new CreateFlagRequest("gb-only", Enabled: false, Targeting: GbRule));
        return host;
    }

    private static string Reason(FlagEvaluationDetails<bool> details) => $"{details.Reason}:{details.Variant}";

    private static async Task<JsonElement> EvaluateAsync(HttpClient client, string country) =>
        await (await client.PostAsJsonAsync("/ofrep/v1/evaluate/flags/gb-only", new { context = new { targetingKey = "u1", country } }))
            .Content.ReadFromJsonAsync<JsonElement>();

    [Fact]
    public async Task The_context_sent_over_ofrep_drives_targeting_for_a_single_flag()
    {
        await using var host = await StartAsync();
        var client = host.CreateClient();

        var gb = await EvaluateAsync(client, "GB");
        var fr = await EvaluateAsync(client, "FR");

        Assert.True(gb.GetProperty("value").GetBoolean());
        Assert.Equal("on", gb.GetProperty("variant").GetString());
        Assert.Equal("TARGETING_MATCH", gb.GetProperty("reason").GetString());
        Assert.False(fr.GetProperty("value").GetBoolean());
        Assert.Equal("off", fr.GetProperty("variant").GetString());
        Assert.Equal("DEFAULT", fr.GetProperty("reason").GetString());
    }

    [Fact]
    public async Task The_context_sent_over_ofrep_drives_targeting_in_bulk_evaluation()
    {
        await using var host = await StartAsync();
        await host.Admin.CreateAsync(new CreateFlagRequest("plain", Enabled: true));

        var response = await host.CreateClient().PostAsJsonAsync(BulkPath, new { context = new { targetingKey = "u1", country = "GB" } });

        var flags = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("flags")
            .EnumerateArray().ToDictionary(f => f.GetProperty("key").GetString()!);
        Assert.Equal("TARGETING_MATCH", flags["gb-only"].GetProperty("reason").GetString());
        Assert.Equal("on", flags["gb-only"].GetProperty("variant").GetString());
        Assert.True(flags["gb-only"].GetProperty("value").GetBoolean());
        Assert.Equal("STATIC", flags["plain"].GetProperty("reason").GetString());
    }

    [Fact]
    public async Task A_rule_that_fails_while_evaluating_is_a_parse_error_over_ofrep()
    {
        await using var host = await StartAsync();
        await host.Admin.CreateAsync(new CreateFlagRequest("cat-bool",
            Targeting: JsonDocument.Parse("""{ "cat": [ { "var": "beta" } ] }""").RootElement));

        var response = await host.CreateClient().PostAsJsonAsync("/ofrep/v1/evaluate/flags/cat-bool", new { context = new { beta = true } });

        Assert.Equal(System.Net.HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("PARSE_ERROR", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errorCode").GetString());
    }

    [Fact]
    public async Task The_ambient_request_context_drives_targeting_when_no_context_is_passed()
    {
        await using var host = await StartAsync(s => s.AddFlagonAmbientContext(http => new EvaluationContext(
            http.User.Identity?.Name,
            new Dictionary<string, object?> { ["country"] = http.Request.Headers["X-Country"].ToString() })));
        var gbClient = host.CreateClient("alice");
        gbClient.DefaultRequestHeaders.Add("X-Country", "GB");
        var frClient = host.CreateClient("bob");
        frClient.DefaultRequestHeaders.Add("X-Country", "FR");

        Assert.Equal("TargetingMatch:on", await gbClient.GetFromJsonAsync<string>("/ambient"));
        Assert.Equal("Default:off", await frClient.GetFromJsonAsync<string>("/ambient"));
    }

    [Fact]
    public async Task An_explicit_context_wins_over_the_ambient_request_context()
    {
        await using var host = await StartAsync(s => s.AddFlagonAmbientContext(_ => new EvaluationContext(
            "ambient", new Dictionary<string, object?> { ["country"] = "GB" })));

        Assert.Equal("TargetingMatch:on", await host.CreateClient("alice").GetFromJsonAsync<string>("/ambient"));
        Assert.Equal("Default:off", await host.CreateClient("alice").GetFromJsonAsync<string>("/explicit"));
    }

    [Fact]
    public async Task Ofrep_targets_on_the_request_body_context_never_the_ambient_one()
    {
        await using var host = await StartAsync(s => s.AddFlagonAmbientContext(_ => new EvaluationContext(
            "ambient", new Dictionary<string, object?> { ["country"] = "GB" })));

        var body = await EvaluateAsync(host.CreateClient("alice"), "FR");

        Assert.Equal("DEFAULT", body.GetProperty("reason").GetString());
    }
}
