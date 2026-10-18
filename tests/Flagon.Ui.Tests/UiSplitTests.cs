using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Flagon.Tests.Support;
using Microsoft.AspNetCore.Builder;

namespace Flagon.Ui.Tests;

/// <summary>The targeting card's N-variant percentage split editor, its list badge and its audit summary (#57).</summary>
public class UiSplitTests
{
    private static Task<FlagonTestHost> StartAsync() =>
        FlagonTestHost.StartAsync(o => o.Endpoints = app => app.MapFlagonUi("/flags"));

    private static Task<string> AppAsync(FlagonTestHost host) => host.CreateClient().GetStringAsync("/flags/app.js");

    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static Task<FlagRecord> CreateThemeAsync(FlagonTestHost host) => host.Admin.CreateAsync(new CreateFlagRequest(
        "theme",
        Variants: Json("""{ "light": "light", "dark": "dark", "solar": "solar" }""").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone(), StringComparer.Ordinal),
        DefaultVariant: "light"));

    private static HttpRequestMessage WithIfMatch(HttpMethod method, string uri, long version, object body)
    {
        var request = new HttpRequestMessage(method, uri) { Content = JsonContent.Create(body) };
        request.Headers.IfMatch.Add(new EntityTagHeaderValue($"\"{version}\""));
        return request;
    }

    [Fact]
    public async Task A_split_saved_through_the_console_api_is_reported_back_as_its_buckets_and_guards_its_variants()
    {
        await using var host = await StartAsync();
        var client = host.CreateClient("alice");
        var created = await CreateThemeAsync(host);
        // Exactly what the console's split editor writes.
        var rule = Json("""{"fractional":[["light",34],["dark",33],["solar",33]]}""");

        var set = await client.SendAsync(WithIfMatch(HttpMethod.Put, "/flags/_api/flags/theme/targeting", created.Version, new { targeting = rule }));
        var flag = await set.Content.ReadFromJsonAsync<JsonElement>();
        var removal = await client.SendAsync(WithIfMatch(HttpMethod.Put, "/flags/_api/flags/theme/variants", flag.GetProperty("version").GetInt64(),
            new { variants = new { light = "light", dark = "dark" }, defaultVariant = "light" }));

        Assert.Equal(HttpStatusCode.OK, set.StatusCode);
        Assert.Equal(["light=34", "dark=33", "solar=33"], flag.GetProperty("rollout").GetProperty("buckets").EnumerateArray()
            .Select(b => $"{b.GetProperty("variant").GetString()}={b.GetProperty("percentage").GetInt32()}"));
        Assert.Equal(HttpStatusCode.BadRequest, removal.StatusCode);
    }

    [Fact]
    public async Task The_targeting_card_offers_a_split_editor_that_saves_the_canonical_shape()
    {
        await using var host = await StartAsync();

        var app = await AppAsync(host);

        Assert.Contains("function splitControl", app, StringComparison.Ordinal);
        Assert.Contains("\"Even split\"", app, StringComparison.Ordinal);
        Assert.Contains("must add up to 100%", app, StringComparison.Ordinal);
        Assert.Contains("api.setTargeting(flag.key, flag.version, { fractional: buckets.map((b) => [b.variant, b.percentage]) })", app, StringComparison.Ordinal);
        Assert.Contains("\"Remove split…\"", app, StringComparison.Ordinal);
        // The boolean slider stays the on/off special case; any other rule stays in the JSON editor.
        Assert.Contains("flag.rollout?.percentage", app, StringComparison.Ordinal);
        Assert.Contains("Custom rule: edit as JSON", app, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Splits_show_their_weights_in_the_list_and_the_audit_history()
    {
        await using var host = await StartAsync();

        var app = await AppAsync(host);

        Assert.Contains("`split ${splitWeights(flag.rollout.buckets)}`", app, StringComparison.Ordinal);
        Assert.Contains("[\"rollout\", \"Rollout or split\"]", app, StringComparison.Ordinal);
        Assert.Contains("`split ${splitWeights(from)} → ${splitWeights(to)}`", app, StringComparison.Ordinal);
        Assert.Contains("function splitBuckets(rule, variants)", app, StringComparison.Ordinal);
    }
}
