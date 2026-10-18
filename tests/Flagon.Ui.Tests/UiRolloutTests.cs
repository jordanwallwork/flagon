using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Flagon.Tests.Support;
using Microsoft.AspNetCore.Builder;

namespace Flagon.Ui.Tests;

/// <summary>The targeting card's percentage rollout control (#31).</summary>
public class UiRolloutTests
{
    private static Task<FlagonTestHost> StartAsync() =>
        FlagonTestHost.StartAsync(o => o.Endpoints = app => app.MapFlagonUi("/flags"));

    [Theory]
    [InlineData(0)]
    [InlineData(25)]
    [InlineData(100)]
    public async Task A_rollout_saved_in_the_canonical_shape_is_reported_back_as_its_percentage(int percentage)
    {
        await using var host = await StartAsync();
        var client = host.CreateClient("alice");
        var created = await host.Admin.CreateAsync(new CreateFlagRequest("checkout"));
        // Exactly what the console's slider writes.
        var rule = JsonDocument.Parse($$"""{"fractional":[["on",{{percentage}}],["off",{{100 - percentage}}]]}""").RootElement;

        var set = await client.SendAsync(WithIfMatch(HttpMethod.Put, "/flags/_api/flags/checkout/targeting", created.Version, new { targeting = rule }));
        var reread = await client.GetFromJsonAsync<JsonElement>("/flags/_api/flags/checkout");

        Assert.Equal(HttpStatusCode.OK, set.StatusCode);
        Assert.Equal(percentage, (await set.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("rollout").GetProperty("percentage").GetInt32());
        Assert.Equal(percentage, reread.GetProperty("rollout").GetProperty("percentage").GetInt32());
    }

    [Fact]
    public async Task Adjusting_then_removing_a_rollout_through_the_console_api_round_trips()
    {
        await using var host = await StartAsync();
        var client = host.CreateClient("alice");
        var created = await host.Admin.CreateAsync(new CreateFlagRequest("checkout", Enabled: true));

        var started = await PutRolloutAsync(client, created.Version, 10);
        var adjusted = await PutRolloutAsync(client, started.GetProperty("version").GetInt64(), 60);
        var removed = await client.SendAsync(WithIfMatch(HttpMethod.Delete, "/flags/_api/flags/checkout/targeting", adjusted.GetProperty("version").GetInt64()));
        var removedFlag = await removed.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(10, started.GetProperty("rollout").GetProperty("percentage").GetInt32());
        Assert.Equal(60, adjusted.GetProperty("rollout").GetProperty("percentage").GetInt32());
        Assert.Equal(HttpStatusCode.OK, removed.StatusCode);
        Assert.Equal(JsonValueKind.Null, removedFlag.GetProperty("rollout").ValueKind);
        Assert.Equal(JsonValueKind.Null, removedFlag.GetProperty("targeting").ValueKind);
    }

    [Fact]
    public async Task A_stale_rollout_save_is_a_version_conflict()
    {
        await using var host = await StartAsync();
        var client = host.CreateClient("alice");
        var created = await host.Admin.CreateAsync(new CreateFlagRequest("checkout"));
        await PutRolloutAsync(client, created.Version, 10);

        var stale = await client.SendAsync(WithIfMatch(HttpMethod.Put, "/flags/_api/flags/checkout/targeting", created.Version,
            new { targeting = JsonDocument.Parse("""{"fractional":[["on",20],["off",80]]}""").RootElement }));

        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.True((await stale.Content.ReadFromJsonAsync<JsonElement>()).TryGetProperty("currentVersion", out _));
    }

    [Fact]
    public async Task The_targeting_card_offers_a_rollout_slider_that_saves_the_canonical_shape()
    {
        await using var host = await StartAsync();

        var app = await host.CreateClient().GetStringAsync("/flags/app.js");

        // Rollout state comes from the server's recogniser, not a client-side one.
        Assert.Contains("flag.rollout", app, StringComparison.Ordinal);
        Assert.Contains("type: \"range\"", app, StringComparison.Ordinal);
        Assert.Contains("type: \"number\"", app, StringComparison.Ordinal);
        Assert.Contains("{ fractional: [[\"on\", n], [\"off\", 100 - n]] }", app, StringComparison.Ordinal);
        Assert.Contains("of users get", app, StringComparison.Ordinal);
        Assert.Contains("\"Remove rollout…\"", app, StringComparison.Ordinal);
        Assert.Contains("Custom rule: edit as JSON", app, StringComparison.Ordinal);
        Assert.Contains("targeting key", app, StringComparison.Ordinal);
    }

    private static async Task<JsonElement> PutRolloutAsync(HttpClient client, long version, int n)
    {
        var rule = JsonDocument.Parse($$"""{"fractional":[["on",{{n}}],["off",{{100 - n}}]]}""").RootElement;
        var response = await client.SendAsync(WithIfMatch(HttpMethod.Put, "/flags/_api/flags/checkout/targeting", version, new { targeting = rule }));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static HttpRequestMessage WithIfMatch(HttpMethod method, string uri, long version, object? body = null)
    {
        var request = new HttpRequestMessage(method, uri) { Content = body is null ? null : JsonContent.Create(body) };
        request.Headers.IfMatch.Add(new EntityTagHeaderValue($"\"{version}\""));
        return request;
    }
}
