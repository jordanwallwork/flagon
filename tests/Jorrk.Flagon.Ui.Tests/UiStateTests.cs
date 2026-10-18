using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Jorrk.Flagon.Storage;
using Jorrk.Flagon.Tests.Support;
using Microsoft.AspNetCore.Builder;

namespace Jorrk.Flagon.Ui.Tests;

/// <summary>The console's Enabled/Disabled state switch (smoke level: assets served, the API it calls covered over HTTP).</summary>
public class UiStateTests
{
    private static Task<FlagonTestHost> StartAsync() =>
        FlagonTestHost.StartAsync(o => o.Endpoints = app => app.MapFlagonUi("/flags"));

    [Fact]
    public async Task The_flag_page_has_a_state_switch_that_confirms_each_app_gets_its_code_default()
    {
        await using var host = await StartAsync();
        var client = host.CreateClient();

        var app = await client.GetStringAsync("/flags/app.js");
        var api = await client.GetStringAsync("/flags/api.js");

        Assert.Contains("setState: (key, version, state) => request(\"PUT\", `${flag(key)}/state`, { body: { state }, version })", api, StringComparison.Ordinal);
        var card = app[app.IndexOf("function stateCard(", StringComparison.Ordinal)..];
        foreach (var text in new[] { "\"State\"", "api.setState(", "confirmDialog(", "each app gets its code default" })
        {
            Assert.Contains(text, card, StringComparison.Ordinal);
        }

        Assert.Contains("stateCard(flag, reload)", app, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_list_marks_disabled_flags_and_filters_by_state()
    {
        await using var host = await StartAsync();

        var app = await host.CreateClient().GetStringAsync("/flags/app.js");

        Assert.Contains("flag.state === \"disabled\" && h(\"span\", { class: \"badge warn\"", app, StringComparison.Ordinal);
        Assert.Contains("[\"disabled\", \"Disabled\"]", app, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_audit_history_summarises_state_changes()
    {
        await using var host = await StartAsync();

        var app = await host.CreateClient().GetStringAsync("/flags/app.js");

        Assert.Contains("changes.push(`state ${stateName(before.state)} → ${stateName(after.state)}`)", app, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_console_api_disables_a_typed_flag()
    {
        await using var host = await StartAsync();
        var created = await host.Admin.CreateAsync(new CreateFlagRequest(
            "theme", Variants: new Dictionary<string, JsonElement> { ["dark"] = JsonDocument.Parse("\"dark\"").RootElement.Clone() }, DefaultVariant: "dark"));

        var response = await host.CreateClient().PutAsJsonAsync("/flags/_api/flags/theme/state", new { state = "disabled", version = created.Version });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("disabled", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("state").GetString());
        Assert.Equal(FlagState.Disabled, (await host.Admin.GetAsync("theme"))!.Document.State);
    }
}
