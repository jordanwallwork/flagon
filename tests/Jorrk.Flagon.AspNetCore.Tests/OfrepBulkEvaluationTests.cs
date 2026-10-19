using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Jorrk.Flagon.Storage;
using Jorrk.Flagon.Tests.Support;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Time.Testing;

namespace Jorrk.Flagon.AspNetCore.Tests;

public class OfrepBulkEvaluationTests
{
    private const string BulkPath = "/ofrep/v1/evaluate/flags";
    private static readonly object EmptyContext = new { context = new { } };

    private static Task<FlagonTestHost> StartAsync(InMemoryFlagStore? store = null, FakeTimeProvider? time = null) =>
        FlagonTestHost.StartAsync(o =>
        {
            o.Store = store;
            o.Time = time;
            o.Endpoints = app => app.MapFlagonOfrep();
        });

    [Fact]
    public async Task Bulk_evaluation_returns_every_flag()
    {
        await using var host = await StartAsync();
        await host.Admin.CreateAsync(new CreateFlagRequest("new-checkout", Enabled: true));
        await host.Admin.CreateAsync(new CreateFlagRequest("dark-mode", Enabled: false));
        await host.Store.TryCreateAsync("legacy", FlagdJson.Deserialize(
            """{ "state": "DISABLED", "variants": { "on": true, "off": false }, "defaultVariant": "on" }"""));
        await FlagonTestHost.AdvanceToNextRefreshAsync(host);

        var response = await host.CreateClient().PostAsJsonAsync(BulkPath, EmptyContext);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var flags = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("flags")
            .EnumerateArray().ToDictionary(f => f.GetProperty("key").GetString()!);
        Assert.Equal(["dark-mode", "legacy", "new-checkout"], flags.Keys.Order(StringComparer.Ordinal));
        Assert.True(flags["new-checkout"].GetProperty("value").GetBoolean());
        Assert.Equal("on", flags["new-checkout"].GetProperty("variant").GetString());
        Assert.False(flags["dark-mode"].GetProperty("value").GetBoolean());
        Assert.Equal("off", flags["dark-mode"].GetProperty("variant").GetString());
        Assert.Equal("DISABLED", flags["legacy"].GetProperty("reason").GetString());
        Assert.False(flags["legacy"].TryGetProperty("value", out _));
    }

    [Fact]
    public async Task Bulk_evaluation_reports_a_flag_that_cannot_be_evaluated_as_an_item_failure()
    {
        await using var host = await StartAsync();
        await host.Admin.CreateAsync(new CreateFlagRequest("new-checkout", Enabled: true));
        await host.Store.TryCreateAsync("broken", FlagdJson.Deserialize(
            """{ "state": "ENABLED", "variants": { "on": true, "off": false }, "defaultVariant": "missing" }"""));
        await FlagonTestHost.AdvanceToNextRefreshAsync(host);

        var response = await host.CreateClient().PostAsJsonAsync(BulkPath, EmptyContext);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var flags = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("flags")
            .EnumerateArray().ToDictionary(f => f.GetProperty("key").GetString()!);
        Assert.Equal("PARSE_ERROR", flags["broken"].GetProperty("errorCode").GetString());
        Assert.True(flags["new-checkout"].GetProperty("value").GetBoolean());
    }

    [Fact]
    public async Task Bulk_evaluation_with_a_malformed_context_returns_400_invalid_context()
    {
        await using var host = await StartAsync();

        var response = await host.CreateClient().PostAsync(BulkPath,
            new StringContent("""{ "context": [] }""", System.Text.Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("INVALID_CONTEXT", body.GetProperty("errorCode").GetString());
    }

    [Fact]
    public async Task Bulk_evaluation_returns_304_while_the_flags_are_unchanged_since_the_clients_etag()
    {
        await using var host = await StartAsync();
        await host.Admin.CreateAsync(new CreateFlagRequest("new-checkout", Enabled: true));
        var client = host.CreateClient();

        var first = await client.PostAsJsonAsync(BulkPath, EmptyContext);
        var etag = first.Headers.ETag;
        Assert.NotNull(etag);

        var second = await PostWithIfNoneMatchAsync(client, etag);

        Assert.Equal(HttpStatusCode.NotModified, second.StatusCode);
        Assert.Equal(etag, second.Headers.ETag);
        Assert.Empty(await second.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task Bulk_evaluation_returns_fresh_flags_and_a_new_etag_after_a_flag_changes()
    {
        await using var host = await StartAsync();
        var created = await host.Admin.CreateAsync(new CreateFlagRequest("new-checkout", Enabled: false));
        var client = host.CreateClient();
        var etag = (await client.PostAsJsonAsync(BulkPath, EmptyContext)).Headers.ETag;

        await host.Admin.SetEnabledAsync("new-checkout", true, created.Version);
        var response = await PostWithIfNoneMatchAsync(client, etag!);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(response.Headers.ETag);
        Assert.NotEqual(etag, response.Headers.ETag);
        var flag = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("flags")[0];
        Assert.True(flag.GetProperty("value").GetBoolean());
    }

    [Fact]
    public async Task A_change_made_by_another_app_changes_the_etag_after_the_next_refresh()
    {
        var store = new InMemoryFlagStore();
        var time = new FakeTimeProvider(FlagonTestHost.Epoch);
        await using var appA = await StartAsync(store, time);
        await using var appB = await StartAsync(store, time);
        var created = await appA.Admin.CreateAsync(new CreateFlagRequest("new-checkout", Enabled: false));
        await FlagonTestHost.AdvanceToNextRefreshAsync(appA, appB);
        var client = appB.CreateClient();
        var etag = (await client.PostAsJsonAsync(BulkPath, EmptyContext)).Headers.ETag!;

        await appA.Admin.SetEnabledAsync("new-checkout", true, created.Version);
        Assert.Equal(HttpStatusCode.NotModified, (await PostWithIfNoneMatchAsync(client, etag)).StatusCode);

        await FlagonTestHost.AdvanceToNextRefreshAsync(appA, appB);
        Assert.Equal(HttpStatusCode.OK, (await PostWithIfNoneMatchAsync(client, etag)).StatusCode);
    }

    private static Task<HttpResponseMessage> PostWithIfNoneMatchAsync(HttpClient client, EntityTagHeaderValue etag)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, BulkPath) { Content = JsonContent.Create(EmptyContext) };
        request.Headers.IfNoneMatch.Add(etag);
        return client.SendAsync(request);
    }
}
