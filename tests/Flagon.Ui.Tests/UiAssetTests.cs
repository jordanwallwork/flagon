using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Flagon.Tests.Support;
using Microsoft.AspNetCore.Builder;

namespace Flagon.Ui.Tests;

public class UiAssetTests
{
    private static Task<FlagonTestHost> StartAsync(string? path = "/flags") =>
        FlagonTestHost.StartAsync(o => o.Endpoints = app => app.MapFlagonUi(path));

    [Fact]
    public async Task The_console_page_is_served_at_the_configured_path()
    {
        await using var host = await StartAsync();

        var response = await host.CreateClient().GetAsync("/flags/");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
        Assert.Contains("<title>Flagon</title>", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_mount_path_without_a_trailing_slash_redirects_to_the_console_page()
    {
        await using var host = await StartAsync();

        var response = await host.CreateClient().GetAsync("/flags");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/flags/", response.Headers.Location?.OriginalString);
    }

    [Fact]
    public async Task Without_a_path_the_console_is_mounted_at_the_default_path()
    {
        await using var host = await StartAsync(path: null);

        Assert.Equal(HttpStatusCode.OK, (await host.CreateClient().GetAsync("/flagon/")).StatusCode);
    }

    [Theory]
    [InlineData("app.js", "text/javascript")]
    [InlineData("api.js", "text/javascript")]
    [InlineData("keys.js", "text/javascript")]
    [InlineData("styles.css", "text/css")]
    public async Task The_pages_scripts_and_styles_are_served_next_to_it(string file, string mediaType)
    {
        await using var host = await StartAsync();
        var page = await (await host.CreateClient().GetAsync("/flags/")).Content.ReadAsStringAsync();

        var response = await host.CreateClient().GetAsync($"/flags/{file}");

        Assert.Contains($"./{file}", page + await ImportsOfAppAsync(host), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(mediaType, response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
    }

    [Fact]
    public async Task Unknown_files_under_the_console_path_are_not_found()
    {
        await using var host = await StartAsync();

        Assert.Equal(HttpStatusCode.NotFound, (await host.CreateClient().GetAsync("/flags/secrets.json")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await host.CreateClient().GetAsync("/flags/../appsettings.json")).StatusCode);
    }

    [Fact]
    public async Task The_page_forbids_framing_and_foreign_scripts()
    {
        await using var host = await StartAsync();

        var response = await host.CreateClient().GetAsync("/flags/");

        var csp = response.Headers.GetValues("Content-Security-Policy").Single();
        Assert.Contains("script-src 'self'", csp, StringComparison.Ordinal);
        Assert.Contains("frame-ancestors 'none'", csp, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Unchanged_files_are_revalidated_with_their_etag()
    {
        await using var host = await StartAsync();
        var client = host.CreateClient();
        var first = await client.GetAsync("/flags/app.js");

        var request = new HttpRequestMessage(HttpMethod.Get, "/flags/app.js");
        request.Headers.IfNoneMatch.Add(first.Headers.ETag!);
        var second = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.NotModified, second.StatusCode);
    }

    [Fact]
    public async Task The_console_talks_to_a_management_api_mounted_under_its_own_path()
    {
        await using var host = await StartAsync();
        var client = host.CreateClient("alice");

        var created = await client.PostAsJsonAsync("/flags/api/flags", new { key = "new-checkout", description = "From the console" });
        var request = new HttpRequestMessage(HttpMethod.Put, "/flags/api/flags/new-checkout/enabled")
        {
            Content = JsonContent.Create(new { enabled = true }),
        };
        request.Headers.IfMatch.Add(created.Headers.ETag!);
        var toggled = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.Equal(HttpStatusCode.OK, toggled.StatusCode);
        Assert.True(host.Flagon.IsEnabled("new-checkout"));
        Assert.All(await host.Admin.GetAuditHistoryAsync("new-checkout"), e => Assert.Equal("alice", e.Actor));

        using var list = JsonDocument.Parse(await client.GetStringAsync("/flags/api/flags"));
        Assert.Equal("new-checkout", Assert.Single(list.RootElement.EnumerateArray()).GetProperty("key").GetString());
    }

    private static async Task<string> ImportsOfAppAsync(FlagonTestHost host) =>
        await host.CreateClient().GetStringAsync("/flags/app.js");
}
