using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Jorrk.Flagon.Tests.Support;
using Microsoft.AspNetCore.Builder;

namespace Jorrk.Flagon.Ui.Tests;

/// <summary>The flag page's variants card and default-variant picker, and their audit summaries (#55).</summary>
public class UiVariantEditTests
{
    private static Task<FlagonTestHost> StartAsync() =>
        FlagonTestHost.StartAsync(o => o.Endpoints = app => app.MapFlagonUi("/flags"));

    private static Task<string> AppAsync(FlagonTestHost host) => host.CreateClient().GetStringAsync("/flags/app.js");

    private static string Function(string script, string name)
    {
        var match = System.Text.RegularExpressions.Regex.Match(
            script, $@"\n(async )?function {name}\(.*?\n}}\n", System.Text.RegularExpressions.RegexOptions.Singleline);
        Assert.True(match.Success, $"app.js has no {name} function");
        return match.Value;
    }

    [Theory]
    [InlineData("setVariants", "request(\"PUT\", `${flag(key)}/variants`, { body: { variants, defaultVariant }, version })")]
    [InlineData("setDefaultVariant", "request(\"PUT\", `${flag(key)}/default-variant`, { body: { variant }, version })")]
    public async Task The_console_client_exposes_the_variant_calls(string name, string call)
    {
        await using var host = await StartAsync();

        var script = await host.CreateClient().GetStringAsync("/flags/api.js");

        Assert.Contains($"{name}: (", script, StringComparison.Ordinal);
        Assert.Contains(call, script, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_variants_card_edits_variants_and_picks_the_default_variant()
    {
        await using var host = await StartAsync();

        var card = Function(await AppAsync(host), "variantsCard");

        foreach (var text in new[] { "api.setVariants(", "api.setDefaultVariant(", "\"Edit variants\"", "\"Add variant\"", "\"Default variant\"",
            "\"Save variants\"", "\"Set default\"", "disabled: flag.archived", "saveInline(", "write(" })
        {
            Assert.Contains(text, card, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task The_variants_editor_warns_when_the_targeting_rule_mentions_a_removed_name()
    {
        await using var host = await StartAsync();

        var app = await AppAsync(host);

        Assert.Contains("function stringLiterals(", app, StringComparison.Ordinal);
        Assert.Contains("stringLiterals(flag.targeting)", Function(app, "variantsCard"), StringComparison.Ordinal);
        Assert.Contains("Renaming a variant is a remove plus an add", Function(app, "variantsCard"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_audit_history_summarises_variant_and_default_variant_changes()
    {
        await using var host = await StartAsync();

        var app = await AppAsync(host);

        Assert.Contains("describeVariantChanges(before, after)", Function(app, "describeChange"), StringComparison.Ordinal);
        foreach (var summary in new[] { "`variant ${name} added`", "`variant ${name} removed`", "`default ${before.defaultVariant ?? \"none\"} → ${after.defaultVariant ?? \"none\"}`" })
        {
            Assert.Contains(summary, app, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task The_console_api_edits_variants_and_the_default_variant()
    {
        await using var host = await StartAsync();
        var client = host.CreateClient("alice");
        var created = await host.Admin.CreateAsync(new CreateFlagRequest("theme",
            Variants: new Dictionary<string, JsonElement> { ["light"] = JsonDocument.Parse("\"light\"").RootElement },
            DefaultVariant: "light"));

        var variants = await client.SendAsync(Put("/flags/_api/flags/theme/variants", created.Version,
            new { variants = new { light = "light", dark = "dark" }, defaultVariant = "light" }));
        var edited = await variants.Content.ReadFromJsonAsync<JsonElement>();
        var defaulted = await client.SendAsync(Put("/flags/_api/flags/theme/default-variant", edited.GetProperty("version").GetInt64(), new { variant = "dark" }));

        Assert.Equal(HttpStatusCode.OK, variants.StatusCode);
        Assert.Equal(HttpStatusCode.OK, defaulted.StatusCode);
        Assert.Equal("dark", (await defaulted.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("value").GetString());
        Assert.Equal("alice", (await host.Admin.GetAuditHistoryAsync("theme"))[0].Actor);
    }

    private static HttpRequestMessage Put(string uri, long version, object body)
    {
        var request = new HttpRequestMessage(HttpMethod.Put, uri) { Content = JsonContent.Create(body) };
        request.Headers.IfMatch.Add(new EntityTagHeaderValue($"\"{version}\""));
        return request;
    }
}
