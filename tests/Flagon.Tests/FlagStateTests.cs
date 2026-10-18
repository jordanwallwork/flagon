using System.Text.Json;
using Flagon.Storage;
using Flagon.Tests.Support;

namespace Flagon.Tests;

/// <summary>Switching any flag's state (ENABLED/DISABLED) through <see cref="IFlagonAdmin.SetStateAsync"/>.</summary>
public class FlagStateTests
{
    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static Dictionary<string, JsonElement> Variants(string json) =>
        Json(json).EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone(), StringComparer.Ordinal);

    private static Task<FlagRecord> CreateStringFlagAsync(FlagonTestHost host, string key = "theme") =>
        host.Admin.CreateAsync(new CreateFlagRequest(key, Variants: Variants("""{ "light": "light", "dark": "dark" }"""), DefaultVariant: "dark"));

    [Fact]
    public async Task Disabling_a_flag_of_any_type_serves_each_callers_default_with_reason_disabled()
    {
        await using var host = await FlagonTestHost.StartAsync();
        var flags = new[]
        {
            await host.Admin.CreateAsync(new CreateFlagRequest("bool-flag", Enabled: true)),
            await CreateStringFlagAsync(host, "string-flag"),
            await host.Admin.CreateAsync(new CreateFlagRequest("number-flag", Variants: Variants("""{ "big": 50 }"""), DefaultVariant: "big")),
            await host.Admin.CreateAsync(new CreateFlagRequest("object-flag", Variants: Variants("""{ "a": { "rows": 3 } }"""), DefaultVariant: "a")),
        };

        foreach (var flag in flags)
        {
            var disabled = await host.Admin.SetStateAsync(flag.Key, FlagState.Disabled, flag.Version);
            Assert.Equal(FlagState.Disabled, disabled.Document.State);
            Assert.True(disabled.Version > flag.Version);
        }

        var boolean = host.Flagon.GetBooleanDetails("bool-flag", false);
        var text = host.Flagon.GetStringDetails("string-flag", "code-default");
        var number = host.Flagon.GetIntDetails("number-flag", 7);
        var obj = host.Flagon.GetObjectDetails("object-flag", Json("""{ "rows": 1 }"""));

        Assert.False(boolean.Value);
        Assert.Equal("code-default", text.Value);
        Assert.Equal(7, number.Value);
        Assert.Equal(1, obj.Value.GetProperty("rows").GetInt32());
        Assert.All(
            new[] { boolean.Reason, text.Reason, number.Reason, obj.Reason },
            reason => Assert.Equal(EvaluationReason.Disabled, reason));
        Assert.All(new[] { boolean.Variant, text.Variant, number.Variant, obj.Variant }, Assert.Null);
    }

    [Fact]
    public async Task The_boolean_toggle_rejects_a_non_boolean_flag_even_one_with_on_and_off_variants()
    {
        await using var host = await FlagonTestHost.StartAsync();
        var flag = await host.Admin.CreateAsync(new CreateFlagRequest(
            "mode", Variants: Variants("""{ "on": "loud", "off": "quiet" }"""), DefaultVariant: "off"));

        var ex = await Assert.ThrowsAsync<FlagValidationException>(() => host.Admin.SetEnabledAsync("mode", true, flag.Version));

        Assert.Contains("boolean", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(flag, await host.Admin.GetAsync("mode"));
    }

    [Fact]
    public async Task Enabling_a_disabled_flag_serves_its_default_variant_again()
    {
        await using var host = await FlagonTestHost.StartAsync();
        var flag = await CreateStringFlagAsync(host);
        flag = await host.Admin.SetStateAsync("theme", FlagState.Disabled, flag.Version);

        var enabled = await host.Admin.SetStateAsync("theme", FlagState.Enabled, flag.Version);

        Assert.Equal(FlagState.Enabled, enabled.Document.State);
        var details = host.Flagon.GetStringDetails("theme", "code-default");
        Assert.Equal("dark", details.Value);
        Assert.Equal(EvaluationReason.Static, details.Reason);
    }

    [Fact]
    public async Task Switching_state_is_audited_as_an_update_with_before_and_after_documents()
    {
        await using var host = await FlagonTestHost.StartAsync();
        var flag = await CreateStringFlagAsync(host);

        var disabled = await host.Admin.SetStateAsync("theme", FlagState.Disabled, flag.Version);

        var entry = (await host.Admin.GetAuditHistoryAsync("theme"))[0];
        Assert.Equal(FlagAuditAction.Updated, entry.Action);
        Assert.Equal(disabled.Version, entry.Version);
        Assert.Equal(FlagState.Enabled, entry.Before!.State);
        Assert.Equal(FlagState.Disabled, entry.After!.State);
        Assert.Equal(entry.Before.Variants.Keys.Order(), entry.After.Variants.Keys.Order());
        Assert.Equal("dark", entry.After.DefaultVariant);
    }

    [Fact]
    public async Task Switching_state_with_a_stale_version_is_a_conflict_and_changes_nothing()
    {
        await using var host = await FlagonTestHost.StartAsync();
        var flag = await CreateStringFlagAsync(host);
        var current = await host.Admin.UpdateMetadataAsync("theme", new UpdateFlagMetadataRequest(Description: "changed"), flag.Version);

        var ex = await Assert.ThrowsAsync<FlagVersionConflictException>(() => host.Admin.SetStateAsync("theme", FlagState.Disabled, flag.Version));

        Assert.Equal(flag.Version, ex.ExpectedVersion);
        Assert.Equal(current, await host.Admin.GetAsync("theme"));
        Assert.Equal(EvaluationReason.Static, host.Flagon.GetStringDetails("theme", "x").Reason);
    }

    [Fact]
    public async Task An_archived_flags_state_cannot_be_switched()
    {
        await using var host = await FlagonTestHost.StartAsync();
        var flag = await CreateStringFlagAsync(host);
        var archived = await host.Admin.ArchiveAsync("theme", flag.Version);

        var ex = await Assert.ThrowsAsync<FlagValidationException>(() => host.Admin.SetStateAsync("theme", FlagState.Disabled, archived.Version));

        Assert.Contains("archived", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(archived, await host.Admin.GetAsync("theme"));
    }

    [Fact]
    public async Task Switching_the_state_of_a_missing_flag_is_not_found()
    {
        await using var host = await FlagonTestHost.StartAsync();

        await Assert.ThrowsAsync<FlagNotFoundException>(() => host.Admin.SetStateAsync("nope", FlagState.Disabled, 1));
    }
}
