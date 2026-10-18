using System.Text.Json;
using Jorrk.Flagon.Storage;
using Jorrk.Flagon.Tests.Support;

namespace Jorrk.Flagon.Tests;

/// <summary>Editing a flag's variants and default variant through <see cref="IFlagonAdmin"/>.</summary>
public class VariantEditTests
{
    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static Dictionary<string, JsonElement> Variants(string json) =>
        Json(json).EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone(), StringComparer.Ordinal);

    private static IEnumerable<string> Raw(IReadOnlyDictionary<string, JsonElement> variants) =>
        variants.OrderBy(v => v.Key, StringComparer.Ordinal).Select(v => $"{v.Key}={v.Value.GetRawText()}");

    private static Task<FlagRecord> CreateThemeAsync(FlagonTestHost host) => host.Admin.CreateAsync(new CreateFlagRequest(
        "theme", Variants: Variants("""{ "light": "light", "dark": "dark" }"""), DefaultVariant: "light"));

    [Fact]
    public async Task Setting_variants_replaces_the_variants_and_default_variant_and_is_served_at_once()
    {
        await using var host = await FlagonTestHost.StartAsync();
        var created = await CreateThemeAsync(host);

        var updated = await host.Admin.SetVariantsAsync(
            "theme", Variants("""{ "light": "light", "solar": "solarized" }"""), "solar", created.Version);

        Assert.Equal(created.Version + 1, updated.Version);
        Assert.Equal("solar", updated.Document.DefaultVariant);
        Assert.Equal(["light=\"light\"", "solar=\"solarized\""], Raw(updated.Document.Variants));
        Assert.Equal(updated, await host.Admin.GetAsync("theme"));
        Assert.Equal("solarized", host.Flagon.GetString("theme", "caller-default"));
    }

    [Theory]
    [InlineData("""{ "light": 1, "dark": 2 }""")]
    [InlineData("""{ "light": { "bg": "white" } }""")]
    [InlineData("""{ "light": true }""")]
    public async Task Variant_edits_cannot_change_the_flags_type(string variants)
    {
        await using var host = await FlagonTestHost.StartAsync();
        var created = await CreateThemeAsync(host);

        var ex = await Assert.ThrowsAsync<FlagValidationException>(
            () => host.Admin.SetVariantsAsync("theme", Variants(variants), "light", created.Version));

        Assert.Contains("type", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("string", ex.Message, StringComparison.Ordinal);
        Assert.Equal(created, await host.Admin.GetAsync("theme"));
    }

    [Theory]
    [InlineData("""{ "on": true, "off": false }""", "on")]
    [InlineData("""{ "on": true, "off": false, "maybe": true }""", "off")]
    public async Task Boolean_flags_keep_their_fixed_variants(string variants, string defaultVariant)
    {
        await using var host = await FlagonTestHost.StartAsync();
        var created = await host.Admin.CreateAsync(new CreateFlagRequest("beta"));

        var ex = await Assert.ThrowsAsync<FlagValidationException>(
            () => host.Admin.SetVariantsAsync("beta", Variants(variants), defaultVariant, created.Version));

        Assert.Contains("boolean", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(created, await host.Admin.GetAsync("beta"));
    }

    [Fact]
    public async Task The_default_variant_cannot_be_removed_while_it_is_the_default()
    {
        await using var host = await FlagonTestHost.StartAsync();
        var created = await CreateThemeAsync(host);

        var ex = await Assert.ThrowsAsync<FlagValidationException>(
            () => host.Admin.SetVariantsAsync("theme", Variants("""{ "dark": "dark" }"""), "light", created.Version));

        Assert.Contains("remove", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("default variant", ex.Message, StringComparison.Ordinal);
        Assert.Contains("'light'", ex.Message, StringComparison.Ordinal);
        Assert.Equal(created, await host.Admin.GetAsync("theme"));
    }

    [Theory]
    [InlineData("dark")]
    [InlineData(null)]
    public async Task The_default_variant_can_be_removed_by_choosing_another_default_in_the_same_edit(string? defaultVariant)
    {
        await using var host = await FlagonTestHost.StartAsync();
        var created = await CreateThemeAsync(host);

        var updated = await host.Admin.SetVariantsAsync("theme", Variants("""{ "dark": "dark" }"""), defaultVariant, created.Version);

        Assert.Equal(defaultVariant, updated.Document.DefaultVariant);
        Assert.Equal(["dark"], updated.Document.Variants.Keys);
    }

    [Fact]
    public async Task A_variant_cannot_be_removed_while_it_is_a_bucket_in_the_percentage_split()
    {
        await using var host = await FlagonTestHost.StartAsync();
        var created = await host.Admin.CreateAsync(new CreateFlagRequest(
            "theme", Variants: Variants("""{ "light": "light", "dark": "dark", "solar": "solar" }"""), DefaultVariant: "light",
            Targeting: Json("""{ "fractional": [ [ "light", 50 ], [ "dark", 50 ] ] }""")));

        var ex = await Assert.ThrowsAsync<FlagValidationException>(
            () => host.Admin.SetVariantsAsync("theme", Variants("""{ "light": "light", "solar": "solar" }"""), "light", created.Version));

        Assert.Contains("remove", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("split", ex.Message, StringComparison.Ordinal);
        Assert.Contains("'dark'", ex.Message, StringComparison.Ordinal);
        Assert.Equal(created, await host.Admin.GetAsync("theme"));
    }

    [Fact]
    public async Task Variants_outside_the_split_and_custom_rule_names_can_still_be_removed()
    {
        await using var host = await FlagonTestHost.StartAsync();
        var split = await host.Admin.CreateAsync(new CreateFlagRequest(
            "theme", Variants: Variants("""{ "light": "light", "dark": "dark", "solar": "solar" }"""), DefaultVariant: "light",
            Targeting: Json("""{ "fractional": [ [ "light", 50 ], [ "dark", 50 ] ] }""")));
        var custom = await host.Admin.CreateAsync(new CreateFlagRequest(
            "custom", Variants: Variants("""{ "light": "light", "dark": "dark" }"""), DefaultVariant: "light",
            Targeting: Json("""{ "fractional": [ { "var": "email" }, [ "light", 50 ], [ "dark", 50 ] ] }""")));

        var trimmed = await host.Admin.SetVariantsAsync("theme", Variants("""{ "light": "light", "dark": "black" }"""), "light", split.Version);
        var customised = await host.Admin.SetVariantsAsync("custom", Variants("""{ "light": "light" }"""), "light", custom.Version);

        Assert.Equal(["dark=\"black\"", "light=\"light\""], Raw(trimmed.Document.Variants));
        Assert.Equal(["light"], customised.Document.Variants.Keys);
    }

    [Theory]
    [InlineData("""{}""", null, "at least one variant")]
    [InlineData("""{ "": "x" }""", null, "empty")]
    [InlineData("""{ "a": "x", "b": 1 }""", null, "one type")]
    [InlineData("""{ "nothing": null }""", null, "null")]
    [InlineData("""{ "list": ["x"] }""", null, "array")]
    [InlineData("""{ "a": "x" }""", "b", "'b'")]
    public async Task Variant_edits_follow_the_create_time_rules(string variants, string? defaultVariant, string message)
    {
        await using var host = await FlagonTestHost.StartAsync();
        var created = await CreateThemeAsync(host);

        var ex = await Assert.ThrowsAsync<FlagValidationException>(
            () => host.Admin.SetVariantsAsync("theme", Variants(variants), defaultVariant, created.Version));

        Assert.Contains(message, ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(created, await host.Admin.GetAsync("theme"));
    }

    [Fact]
    public async Task Variant_names_longer_than_128_characters_are_rejected()
    {
        await using var host = await FlagonTestHost.StartAsync();
        var created = await CreateThemeAsync(host);
        var tooLong = new Dictionary<string, JsonElement> { [new string('v', 129)] = Json("\"x\"") };

        var ex = await Assert.ThrowsAsync<FlagValidationException>(
            () => host.Admin.SetVariantsAsync("theme", tooLong, null, created.Version));

        Assert.Contains("128", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Archived_flags_reject_variant_and_default_variant_edits()
    {
        await using var host = await FlagonTestHost.StartAsync();
        await CreateThemeAsync(host);
        var archived = await host.Admin.ArchiveAsync("theme", 1);

        var variants = await Assert.ThrowsAsync<FlagValidationException>(
            () => host.Admin.SetVariantsAsync("theme", Variants("""{ "dark": "dark" }"""), "dark", archived.Version));
        var defaultVariant = await Assert.ThrowsAsync<FlagValidationException>(
            () => host.Admin.SetDefaultVariantAsync("theme", "dark", archived.Version));

        Assert.Contains("archived", variants.Message, StringComparison.Ordinal);
        Assert.Contains("archived", defaultVariant.Message, StringComparison.Ordinal);
        Assert.Equal(archived, await host.Admin.GetAsync("theme"));
    }

    [Fact]
    public async Task Archived_non_boolean_flags_reject_toggling_as_archived()
    {
        await using var host = await FlagonTestHost.StartAsync();
        await CreateThemeAsync(host);
        var archived = await host.Admin.ArchiveAsync("theme", 1);

        var ex = await Assert.ThrowsAsync<FlagValidationException>(
            () => host.Admin.SetEnabledAsync("theme", true, archived.Version));

        Assert.Contains("archived", ex.Message, StringComparison.Ordinal);
        Assert.Equal(archived, await host.Admin.GetAsync("theme"));
    }

    [Fact]
    public async Task Stale_versions_conflict_and_nothing_is_written()
    {
        await using var host = await FlagonTestHost.StartAsync();
        var created = await CreateThemeAsync(host);
        var current = await host.Admin.SetDefaultVariantAsync("theme", "dark", created.Version);

        var variants = await Assert.ThrowsAsync<FlagVersionConflictException>(
            () => host.Admin.SetVariantsAsync("theme", Variants("""{ "dark": "dark" }"""), "dark", created.Version));
        var defaultVariant = await Assert.ThrowsAsync<FlagVersionConflictException>(
            () => host.Admin.SetDefaultVariantAsync("theme", "light", created.Version));

        Assert.Equal(current.Version, variants.CurrentVersion);
        Assert.Equal(current.Version, defaultVariant.CurrentVersion);
        Assert.Equal(current, await host.Admin.GetAsync("theme"));
    }

    [Fact]
    public async Task A_stale_edit_that_the_current_document_would_reject_still_conflicts()
    {
        await using var host = await FlagonTestHost.StartAsync();
        var created = await host.Admin.CreateAsync(new CreateFlagRequest(
            "theme", Variants: Variants("""{ "light": "light", "dark": "dark", "solar": "solar" }"""), DefaultVariant: "light"));

        // Another admin removes 'dark' and splits light/solar.
        var trimmed = await host.Admin.SetVariantsAsync("theme", Variants("""{ "light": "light", "solar": "solar" }"""), "light", created.Version);
        var current = await host.Admin.SetTargetingAsync("theme", Json("""{ "fractional": [ [ "light", 50 ], [ "solar", 50 ] ] }"""), trimmed.Version);

        // Stale edits made against 'created': select the removed 'dark', and remove the now-split 'solar'.
        var defaultVariant = await Assert.ThrowsAsync<FlagVersionConflictException>(
            () => host.Admin.SetDefaultVariantAsync("theme", "dark", created.Version));
        var variants = await Assert.ThrowsAsync<FlagVersionConflictException>(
            () => host.Admin.SetVariantsAsync("theme", Variants("""{ "light": "light", "dark": "dark" }"""), "light", created.Version));

        Assert.Equal(current.Version, defaultVariant.CurrentVersion);
        Assert.Equal(current.Version, variants.CurrentVersion);
        Assert.Equal(current, await host.Admin.GetAsync("theme"));
    }

    [Fact]
    public async Task Unknown_flags_are_not_found()
    {
        await using var host = await FlagonTestHost.StartAsync();

        await Assert.ThrowsAsync<FlagNotFoundException>(
            () => host.Admin.SetVariantsAsync("missing", Variants("""{ "a": "x" }"""), "a", 1));
        await Assert.ThrowsAsync<FlagNotFoundException>(() => host.Admin.SetDefaultVariantAsync("missing", "a", 1));
    }

    [Fact]
    public async Task Variant_and_default_variant_edits_are_audited_as_updates_with_before_and_after()
    {
        await using var host = await FlagonTestHost.StartAsync(o => o.ApplicationName = "Admin.Portal");
        var created = await CreateThemeAsync(host);

        FlagRecord withVariants, withDefault;
        using (FlagonActor.Use("alice"))
        {
            withVariants = await host.Admin.SetVariantsAsync(
                "theme", Variants("""{ "light": "light", "dark": "black" }"""), "light", created.Version);
            withDefault = await host.Admin.SetDefaultVariantAsync("theme", "dark", withVariants.Version);
        }

        var history = await host.Admin.GetAuditHistoryAsync("theme");
        Assert.Equal(3, history.Count);
        var (defaultEntry, variantsEntry) = (history[0], history[1]);
        Assert.Equal(FlagAuditAction.Updated, variantsEntry.Action);
        Assert.Equal(created.Document, variantsEntry.Before);
        Assert.Equal(withVariants.Document, variantsEntry.After);
        Assert.Equal("alice", variantsEntry.Actor);
        Assert.Equal("Admin.Portal", variantsEntry.ApplicationName);
        Assert.Equal(FlagAuditAction.Updated, defaultEntry.Action);
        Assert.Equal(withVariants.Document, defaultEntry.Before);
        Assert.Equal(withDefault.Document, defaultEntry.After);
        Assert.Equal("light", defaultEntry.Before!.DefaultVariant);
        Assert.Equal("dark", defaultEntry.After!.DefaultVariant);
    }

    [Fact]
    public async Task Setting_the_default_variant_switches_the_served_value_and_keeps_the_variants()
    {
        await using var host = await FlagonTestHost.StartAsync();
        var created = await CreateThemeAsync(host);

        var updated = await host.Admin.SetDefaultVariantAsync("theme", "dark", created.Version);

        Assert.Equal("dark", updated.Document.DefaultVariant);
        Assert.Equal(Raw(created.Document.Variants), Raw(updated.Document.Variants));
        Assert.Equal("dark", host.Flagon.GetString("theme", "caller-default"));
    }

    [Fact]
    public async Task Clearing_the_default_variant_serves_each_callers_default()
    {
        await using var host = await FlagonTestHost.StartAsync();
        var created = await CreateThemeAsync(host);

        var updated = await host.Admin.SetDefaultVariantAsync("theme", null, created.Version);

        Assert.Null(updated.Document.DefaultVariant);
        Assert.Equal("caller-default", host.Flagon.GetString("theme", "caller-default"));
    }

    [Fact]
    public async Task The_default_variant_must_be_one_of_the_flags_variants()
    {
        await using var host = await FlagonTestHost.StartAsync();
        var created = await CreateThemeAsync(host);

        var ex = await Assert.ThrowsAsync<FlagValidationException>(() => host.Admin.SetDefaultVariantAsync("theme", "Dark", created.Version));

        Assert.Contains("'Dark'", ex.Message, StringComparison.Ordinal);
        Assert.Equal(created, await host.Admin.GetAsync("theme"));
    }

    [Theory]
    [InlineData("on", true)]
    [InlineData("off", false)]
    public async Task Setting_a_boolean_flags_default_variant_toggles_it(string variant, bool enabled)
    {
        await using var host = await FlagonTestHost.StartAsync();
        var created = await host.Admin.CreateAsync(new CreateFlagRequest("beta", Enabled: !enabled));

        var updated = await host.Admin.SetDefaultVariantAsync("beta", variant, created.Version);

        Assert.Equal(variant, updated.Document.DefaultVariant);
        Assert.Equal(enabled, host.Flagon.IsEnabled("beta"));
    }

    [Fact]
    public async Task A_boolean_flags_default_variant_cannot_be_cleared_so_it_stays_on_or_off()
    {
        await using var host = await FlagonTestHost.StartAsync();
        var created = await host.Admin.CreateAsync(new CreateFlagRequest("beta", Enabled: true));

        var ex = await Assert.ThrowsAsync<FlagValidationException>(() => host.Admin.SetDefaultVariantAsync("beta", null, created.Version));

        Assert.Contains("'on' or 'off'", ex.Message, StringComparison.Ordinal);
        Assert.Equal(created, await host.Admin.GetAsync("beta"));
        Assert.True(host.Flagon.IsEnabled("beta"));
    }
}
