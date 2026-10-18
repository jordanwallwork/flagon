using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Jorrk.Flagon.Storage;
using Jorrk.Flagon.Tests.Support;
using Microsoft.AspNetCore.Builder;

namespace Jorrk.Flagon.AspNetCore.Tests;

/// <summary>A flag of any type disabled with <see cref="IFlagonAdmin.SetStateAsync"/> tells OFREP clients to use their code default.</summary>
public class OfrepDisabledFlagTests
{
    private static readonly object EmptyContext = new { context = new { } };

    private static readonly string[] Keys = ["bool-flag", "number-flag", "object-flag", "string-flag"];

    private static Dictionary<string, JsonElement> Variants(string json) =>
        JsonDocument.Parse(json).RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone(), StringComparer.Ordinal);

    private static async Task<FlagonTestHost> StartWithDisabledFlagsAsync()
    {
        var host = await FlagonTestHost.StartAsync(o => o.Endpoints = app => app.MapFlagonOfrep());
        FlagRecord[] flags =
        [
            await host.Admin.CreateAsync(new CreateFlagRequest("bool-flag", Enabled: true)),
            await host.Admin.CreateAsync(new CreateFlagRequest("string-flag", Variants: Variants("""{ "dark": "dark" }"""), DefaultVariant: "dark")),
            await host.Admin.CreateAsync(new CreateFlagRequest("number-flag", Variants: Variants("""{ "big": 50 }"""), DefaultVariant: "big")),
            await host.Admin.CreateAsync(new CreateFlagRequest("object-flag", Variants: Variants("""{ "a": { "rows": 3 } }"""), DefaultVariant: "a")),
        ];
        foreach (var flag in flags)
        {
            await host.Admin.SetStateAsync(flag.Key, FlagState.Disabled, flag.Version);
        }

        return host;
    }

    private static void AssertCodeDefault(JsonElement body)
    {
        Assert.Equal("DISABLED", body.GetProperty("reason").GetString());
        Assert.False(body.TryGetProperty("value", out _));
        Assert.False(body.TryGetProperty("errorCode", out _));
    }

    [Fact]
    public async Task A_disabled_flag_of_any_type_evaluates_to_the_code_default_shape()
    {
        await using var host = await StartWithDisabledFlagsAsync();
        var client = host.CreateClient();

        foreach (var key in Keys)
        {
            var response = await client.PostAsJsonAsync($"/ofrep/v1/evaluate/flags/{key}", EmptyContext);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            AssertCodeDefault(await response.Content.ReadFromJsonAsync<JsonElement>());
        }
    }

    [Fact]
    public async Task Bulk_evaluation_reports_disabled_flags_of_any_type_with_the_code_default_shape()
    {
        await using var host = await StartWithDisabledFlagsAsync();

        var response = await host.CreateClient().PostAsJsonAsync("/ofrep/v1/evaluate/flags", EmptyContext);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var flags = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("flags").EnumerateArray().ToArray();
        Assert.Equal(Keys, flags.Select(f => f.GetProperty("key").GetString()).Order(StringComparer.Ordinal));
        Assert.All(flags, AssertCodeDefault);
    }
}
