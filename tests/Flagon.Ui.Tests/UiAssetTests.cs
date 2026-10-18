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

        var created = await client.PostAsJsonAsync("/flags/_api/flags", new { key = "new-checkout", description = "From the console" });
        var request = new HttpRequestMessage(HttpMethod.Put, "/flags/_api/flags/new-checkout/enabled")
        {
            Content = JsonContent.Create(new { enabled = true }),
        };
        request.Headers.IfMatch.Add(created.Headers.ETag!);
        var toggled = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.Equal(HttpStatusCode.OK, toggled.StatusCode);
        Assert.True(host.Flagon.IsEnabled("new-checkout"));
        Assert.All(await host.Admin.GetAuditHistoryAsync("new-checkout"), e => Assert.Equal("alice", e.Actor));

        using var list = JsonDocument.Parse(await client.GetStringAsync("/flags/_api/flags"));
        Assert.Equal("new-checkout", Assert.Single(list.RootElement.EnumerateArray()).GetProperty("key").GetString());
    }

    [Fact]
    public async Task The_console_and_the_management_api_can_both_be_mapped_with_their_default_paths()
    {
        await using var host = await FlagonTestHost.StartAsync(o => o.Endpoints = app =>
        {
            app.MapFlagonUi();
            app.MapFlagonApi();
        });
        var client = host.CreateClient("alice");
        await host.Admin.CreateAsync(new CreateFlagRequest("new-checkout"));

        var page = await client.GetAsync("/flagon/");
        var viaApi = await client.GetAsync("/flagon/api/flags/new-checkout");
        var viaConsole = await client.GetAsync("/flagon/_api/flags/new-checkout");

        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        Assert.Equal("text/html", page.Content.Headers.ContentType?.MediaType);
        Assert.Equal(HttpStatusCode.OK, viaApi.StatusCode);
        Assert.Equal(HttpStatusCode.OK, viaConsole.StatusCode);
        Assert.Equal("new-checkout", (await viaApi.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("key").GetString());
    }

    [Fact]
    public async Task The_console_script_calls_the_api_mounted_under_the_console()
    {
        await using var host = await StartAsync();

        var script = await host.CreateClient().GetStringAsync("/flags/api.js");

        Assert.Contains("new URL(\"_api/\", document.baseURI)", script, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Every_dialog_is_labelled_by_its_heading()
    {
        await using var host = await StartAsync();

        var page = await host.CreateClient().GetStringAsync("/flags/");

        var dialogs = System.Text.RegularExpressions.Regex.Matches(page, "<dialog\\b[^>]*>");
        Assert.NotEmpty(dialogs);
        Assert.All(dialogs, dialog =>
        {
            var labelledBy = System.Text.RegularExpressions.Regex.Match(dialog.Value, "aria-labelledby=\"([^\"]+)\"");
            Assert.True(labelledBy.Success, $"{dialog.Value} has no aria-labelledby");
            Assert.Matches($"<h2 id=\"{labelledBy.Groups[1].Value}\"", page);
        });
    }

    [Theory]
    [InlineData("setTargeting", "request(\"PUT\", `${flag(key)}/targeting`")]
    [InlineData("clearTargeting", "request(\"DELETE\", `${flag(key)}/targeting`")]
    [InlineData("evaluate", "request(\"POST\", `${flag(key)}/evaluate`")]
    public async Task The_console_client_exposes_the_targeting_calls(string name, string call)
    {
        await using var host = await StartAsync();

        var script = await host.CreateClient().GetStringAsync("/flags/api.js");

        Assert.Contains($"{name}: (", script, StringComparison.Ordinal);
        Assert.Contains(call, script, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_flag_page_has_a_targeting_card_that_edits_validates_saves_and_clears_the_rule()
    {
        await using var host = await StartAsync();

        var app = await ImportsOfAppAsync(host);

        Assert.Contains("No targeting: everyone gets the default variant", app, StringComparison.Ordinal);
        foreach (var call in new[] { "api.setTargeting(", "api.clearTargeting(", "api.evaluate(" })
        {
            Assert.Contains(call, app, StringComparison.Ordinal);
        }
        foreach (var label in new[] { "\"Format\"", "\"Validate\"", "\"Save\"", "\"Clear…\"" })
        {
            Assert.Contains(label, app, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task The_flag_page_has_a_try_it_panel_that_only_evaluates()
    {
        await using var host = await StartAsync();

        var app = await ImportsOfAppAsync(host);

        var panel = System.Text.RegularExpressions.Regex.Match(app, @"\nfunction tryItCard\(.*?\n}\n", System.Text.RegularExpressions.RegexOptions.Singleline);
        Assert.True(panel.Success, "app.js has no tryItCard function");
        foreach (var text in new[] { "\"Try it\"", "\"Evaluate saved\"", "\"Evaluate draft\"", "\"Add attribute\"", "api.evaluate(", "localStorage", "flagon.tryit." })
        {
            Assert.Contains(text, panel.Value, StringComparison.Ordinal);
        }
        // The panel never writes: the only API it calls is evaluate.
        Assert.All(System.Text.RegularExpressions.Regex.Matches(panel.Value, @"api\.(\w+)\("), call => Assert.Equal("evaluate", call.Groups[1].Value));
        Assert.Contains("tryItCard(flag, targeting.draft)", app, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_console_api_evaluates_saved_and_draft_rules_for_a_context_without_changing_the_flag()
    {
        await using var host = await StartAsync();
        var client = host.CreateClient();
        var created = await host.Admin.CreateAsync(new CreateFlagRequest("checkout", Enabled: true));
        var rule = JsonDocument.Parse("""{"if":[{"==":[{"var":"plan"},"pro"]},"on","off"]}""").RootElement;
        await host.Admin.SetTargetingAsync("checkout", rule, created.Version);
        var before = await host.Admin.GetAsync("checkout");
        var context = new { targetingKey = "user-1", plan = "pro" };
        var draft = JsonDocument.Parse("""
            {"state":"ENABLED","variants":{"on":true,"off":false},"defaultVariant":"off","targeting":{"if":[{"==":[{"var":"plan"},"free"]},"on","off"]}}
            """).RootElement;

        var saved = await (await client.PostAsJsonAsync("/flags/_api/flags/checkout/evaluate", new { context })).Content.ReadFromJsonAsync<JsonElement>();
        var drafted = await (await client.PostAsJsonAsync("/flags/_api/flags/checkout/evaluate", new { context, document = draft })).Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(("on", true, "TARGETING_MATCH"), (saved.GetProperty("variant").GetString(), saved.GetProperty("value").GetBoolean(), saved.GetProperty("reason").GetString()));
        Assert.Equal(("off", false, "TARGETING_MATCH"), (drafted.GetProperty("variant").GetString(), drafted.GetProperty("value").GetBoolean(), drafted.GetProperty("reason").GetString()));
        Assert.Equal(before!.Version, (await host.Admin.GetAsync("checkout"))!.Version);
        Assert.Equal(2, (await host.Admin.GetAuditHistoryAsync("checkout")).Count);
    }

    [Fact]
    public async Task The_console_loads_nothing_from_other_origins()
    {
        await using var host = await StartAsync();
        var client = host.CreateClient();

        foreach (var file in new[] { "", "app.js", "api.js", "keys.js", "styles.css" })
        {
            var text = await client.GetStringAsync($"/flags/{file}");
            Assert.DoesNotMatch("(?i)https?://|//cdn\\.|@import", text);
        }
    }

    [Fact]
    public async Task The_console_api_sets_evaluates_and_clears_targeting()
    {
        await using var host = await StartAsync();
        var client = host.CreateClient("alice");
        var created = await host.Admin.CreateAsync(new CreateFlagRequest("checkout", Enabled: false));
        var rule = JsonDocument.Parse("""{"if":[{"==":[{"var":"plan"},"pro"]},"on","off"]}""").RootElement;

        var set = await client.SendAsync(WithIfMatch(HttpMethod.Put, "/flags/_api/flags/checkout/targeting", created.Version, new { targeting = rule }));
        var setFlag = await set.Content.ReadFromJsonAsync<JsonElement>();
        var evaluated = await client.PostAsJsonAsync("/flags/_api/flags/checkout/evaluate", new { context = new { plan = "pro" } });
        var cleared = await client.SendAsync(WithIfMatch(HttpMethod.Delete, "/flags/_api/flags/checkout/targeting", setFlag.GetProperty("version").GetInt64()));

        Assert.Equal(HttpStatusCode.OK, set.StatusCode);
        Assert.Equal(rule.GetRawText(), setFlag.GetProperty("targeting").GetRawText());
        Assert.Equal("on", (await evaluated.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("variant").GetString());
        Assert.Equal(HttpStatusCode.OK, cleared.StatusCode);
        Assert.Equal(JsonValueKind.Null, (await cleared.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("targeting").ValueKind);
    }

    [Fact]
    public async Task The_console_api_reports_an_invalid_draft_rule_with_its_location()
    {
        await using var host = await StartAsync();
        await host.Admin.CreateAsync(new CreateFlagRequest("checkout"));
        var document = JsonDocument.Parse("""
            {"state":"ENABLED","variants":{"on":true,"off":false},"defaultVariant":"off","targeting":{"if":[{"nope":[]},"on","off"]}}
            """).RootElement;

        var response = await host.CreateClient().PostAsJsonAsync("/flags/_api/flags/checkout/evaluate", new { context = new { }, document });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("/if/0", problem.GetProperty("path").GetString());
        Assert.Equal("nope", problem.GetProperty("operator").GetString());
    }

    [Fact]
    public async Task A_stale_targeting_save_through_the_console_api_is_a_version_conflict()
    {
        await using var host = await StartAsync();
        var created = await host.Admin.CreateAsync(new CreateFlagRequest("checkout"));
        await host.Admin.SetEnabledAsync("checkout", true, created.Version);

        var response = await host.CreateClient().SendAsync(
            WithIfMatch(HttpMethod.Put, "/flags/_api/flags/checkout/targeting", created.Version, new { targeting = (object?)null }));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.True((await response.Content.ReadFromJsonAsync<JsonElement>()).TryGetProperty("currentVersion", out _));
    }

    private static HttpRequestMessage WithIfMatch(HttpMethod method, string uri, long version, object? body = null)
    {
        var request = new HttpRequestMessage(method, uri) { Content = body is null ? null : JsonContent.Create(body) };
        request.Headers.IfMatch.Add(new EntityTagHeaderValue($"\"{version}\""));
        return request;
    }

    private static async Task<string> ImportsOfAppAsync(FlagonTestHost host) =>
        await host.CreateClient().GetStringAsync("/flags/app.js");
}
