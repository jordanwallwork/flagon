using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Jorrk.Flagon.Tests.Support;
using Microsoft.AspNetCore.Builder;

namespace Jorrk.Flagon.Ui.Tests;

/// <summary>
/// The console's segment editors (#39): the shapes it writes round-trip as <c>keys</c> (List mode) and
/// <c>segmentTargeting</c> (the targeting card's Segment mode); anything else falls back to the JSON editors.
/// </summary>
public class UiSegmentTests
{
    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static Task<FlagonTestHost> StartAsync(Action<FlagonTestHostOptions>? configure = null) => FlagonTestHost.StartAsync(o =>
    {
        o.Endpoints = app => app.MapFlagonUi("/flags");
        configure?.Invoke(o);
    });

    [Fact]
    public async Task A_key_list_saved_by_the_console_is_reported_back_as_its_keys()
    {
        await using var host = await StartAsync();
        var client = host.CreateClient("alice");
        // Exactly what the console's List mode writes.
        var rule = Json("""{"in":[{"var":"targetingKey"},["ada","grace"]]}""");

        var created = await client.PostAsJsonAsync("/flags/_api/segments", new { key = "staff", rule });
        var body = await created.Content.ReadFromJsonAsync<JsonElement>();
        var emptied = await client.SendAsync(WithIfMatch(HttpMethod.Put, "/flags/_api/segments/staff/rule", body.GetProperty("version").GetInt64(),
            new { rule = Json("""{"in":[{"var":"targetingKey"},[]]}""") }));

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.Equal(["ada", "grace"], body.GetProperty("keys").EnumerateArray().Select(k => k.GetString()));
        Assert.Empty((await emptied.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("keys").EnumerateArray());
    }

    [Theory]
    [InlineData("""{"in":[{"var":"email"},["ada"]]}""")]
    [InlineData("""{"in":[{"var":"targetingKey"},["ada",7]]}""")]
    [InlineData("""{"in":[{"var":"targetingKey"},["ada"],"extra"]}""")]
    [InlineData("""{"in":[{"var":["targetingKey"]},["ada"]]}""")]
    [InlineData("""{"in":[{"var":"targetingKey"},"ada"]}""")]
    [InlineData("""{"!":[{"in":[{"var":"targetingKey"},["ada"]]}]}""")]
    public async Task Any_other_rule_is_edited_as_a_rule(string rule)
    {
        await using var host = await StartAsync();

        var created = await host.CreateClient("alice").PostAsJsonAsync("/flags/_api/segments", new { key = "staff", rule = Json(rule) });

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.False((await created.Content.ReadFromJsonAsync<JsonElement>()).TryGetProperty("keys", out _));
    }

    [Theory]
    [InlineData("""{"if":[{"$ref":"staff"},"on",null]}""", new[] { "staff" })]
    [InlineData("""{"if":[{"or":[{"$ref":"staff"},{"$ref":"eu"}]},"on",null]}""", new[] { "staff", "eu" })]
    public async Task Segment_targeting_saved_by_the_console_is_reported_back_as_its_segments_and_variant(string rule, string[] segments)
    {
        await using var host = await StartAsync();
        await SegmentsAsync(host);
        var flag = await host.Admin.CreateAsync(new CreateFlagRequest("beta"));

        var set = await host.CreateClient("alice").SendAsync(WithIfMatch(HttpMethod.Put, "/flags/_api/flags/beta/targeting", flag.Version, new { targeting = Json(rule) }));
        var shape = (await set.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("segmentTargeting");

        Assert.Equal(HttpStatusCode.OK, set.StatusCode);
        Assert.Equal(segments, shape.GetProperty("segments").EnumerateArray().Select(s => s.GetString()));
        Assert.Equal("on", shape.GetProperty("variant").GetString());
    }

    [Theory]
    [InlineData("""{"if":[{"$ref":"staff"},"on","off"]}""")]
    [InlineData("""{"if":[{"$ref":"staff"},"nope",null]}""")]
    [InlineData("""{"if":[{"or":[{"$ref":"staff"}]},"on",null]}""")]
    [InlineData("""{"if":[{"or":[{"$ref":"staff"},{"$ref":"staff"}]},"on",null]}""")]
    [InlineData("""{"if":[{"and":[{"$ref":"staff"},{"$ref":"eu"}]},"on",null]}""")]
    [InlineData("""{"if":[{"!":[{"$ref":"staff"}]},"on",null]}""")]
    public async Task Any_other_rule_using_segments_is_a_custom_rule(string rule)
    {
        await using var host = await StartAsync();
        await SegmentsAsync(host);
        var flag = await host.Admin.CreateAsync(new CreateFlagRequest("beta"));

        var set = await host.CreateClient("alice").SendAsync(WithIfMatch(HttpMethod.Put, "/flags/_api/flags/beta/targeting", flag.Version, new { targeting = Json(rule) }));
        var body = await set.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.OK, set.StatusCode);
        Assert.Equal(JsonValueKind.Null, body.GetProperty("segmentTargeting").ValueKind);
        Assert.NotEmpty(body.GetProperty("segments").EnumerateArray());
    }

    [Fact]
    public async Task Without_segment_support_the_console_api_answers_501_and_the_page_hides_the_segments_link_by_default()
    {
        var store = OutageFlagStore.Wrap();
        await using var host = await StartAsync(o => o.Store = store.Store);
        var client = host.CreateClient("alice");

        var segments = await client.GetAsync("/flags/_api/segments");
        var page = await client.GetStringAsync("/flags/");

        Assert.Equal(HttpStatusCode.NotImplemented, segments.StatusCode);
        Assert.Contains("""<a href="#/segments" data-nav="segments" id="segments-nav" hidden>""", page, StringComparison.Ordinal);
    }

    private static async Task SegmentsAsync(FlagonTestHost host)
    {
        await host.Admin.CreateSegmentAsync(new CreateSegmentRequest("staff", Json("""{"in":[{"var":"targetingKey"},["ada"]]}""")));
        await host.Admin.CreateSegmentAsync(new CreateSegmentRequest("eu", Json("""{"in":[{"var":"country"},["FR"]]}""")));
    }

    private static HttpRequestMessage WithIfMatch(HttpMethod method, string uri, long version, object? body = null)
    {
        var request = new HttpRequestMessage(method, uri) { Content = body is null ? null : JsonContent.Create(body) };
        request.Headers.IfMatch.Add(new EntityTagHeaderValue($"\"{version}\""));
        return request;
    }
}
