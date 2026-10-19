using System.Text.Json;
using Jorrk.Flagon.Storage;
using Jorrk.Flagon.Tests.Support;

namespace Jorrk.Flagon.Tests;

/// <summary>Creating string, number and object flags with named variants through <see cref="IFlagonAdmin"/>.</summary>
public class TypedFlagAdminTests
{
    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static Dictionary<string, JsonElement> Variants(string json) =>
        Json(json).EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone(), StringComparer.Ordinal);

    private static IEnumerable<string> Raw(IReadOnlyDictionary<string, JsonElement> variants) =>
        variants.OrderBy(v => v.Key, StringComparer.Ordinal).Select(v => $"{v.Key}={v.Value.GetRawText().Replace(" ", "", StringComparison.Ordinal)}");

    [Fact]
    public async Task An_admin_can_create_a_string_flag_with_named_variants_and_a_default_variant()
    {
        await using var host = await FlagonTestHost.StartAsync();

        var created = await host.Admin.CreateAsync(new CreateFlagRequest(
            "theme", Description: "Site theme", Variants: Variants("""{ "light": "light", "dark": "dark" }"""), DefaultVariant: "dark"));

        Assert.Equal(FlagType.String, created.Document.Type);
        Assert.Equal(FlagState.Enabled, created.Document.State);
        Assert.Equal("dark", created.Document.DefaultVariant);
        Assert.Equal(["dark", "light"], created.Document.Variants.Keys.Order(StringComparer.Ordinal));
        Assert.Equal("Site theme", created.Document.Metadata.Description);
        Assert.Equal(FlagSource.Manual, created.Document.Metadata.Source);
        Assert.Equal(created, await host.Admin.GetAsync("theme"));
    }

    [Theory]
    [InlineData("""{ "small": 10, "large": 2.5 }""", FlagType.Number)]
    [InlineData("""{ "compact": { "rows": 3 }, "empty": {} }""", FlagType.Object)]
    [InlineData("""{ "blank": "" }""", FlagType.String)]
    public async Task A_flag_takes_the_type_of_its_variants(string variants, FlagType type)
    {
        await using var host = await FlagonTestHost.StartAsync();

        var created = await host.Admin.CreateAsync(new CreateFlagRequest("typed", Variants: Variants(variants), DefaultVariant: null));

        Assert.Equal(type, created.Document.Type);
        Assert.Null(created.Document.DefaultVariant);
        var stored = (await host.Admin.GetAsync("typed"))!.Document.Variants;
        Assert.Equal(Raw(Variants(variants)), Raw(stored));
    }

    [Theory]
    [InlineData("on", true)]
    [InlineData("off", false)]
    public async Task A_flag_created_with_the_on_off_variants_is_a_boolean_flag(string defaultVariant, bool value)
    {
        await using var host = await FlagonTestHost.StartAsync();

        var created = await host.Admin.CreateAsync(new CreateFlagRequest(
            "beta", Variants: Variants("""{ "on": true, "off": false }"""), DefaultVariant: defaultVariant));

        Assert.Equal(FlagType.Boolean, created.Document.Type);
        Assert.Equal(defaultVariant, created.Document.DefaultVariant);
        Assert.Equal(value, host.Flagon.IsEnabled("beta", !value));
    }

    [Fact]
    public async Task A_boolean_flag_may_be_created_with_no_default_variant_and_serves_the_callers_default()
    {
        await using var host = await FlagonTestHost.StartAsync();

        var created = await host.Admin.CreateAsync(new CreateFlagRequest(
            "beta", Variants: Variants("""{ "on": true, "off": false }"""), DefaultVariant: null));

        Assert.Equal(FlagType.Boolean, created.Document.Type);
        Assert.Null(created.Document.DefaultVariant);
        Assert.Equal(new FlagEvaluationDetails<bool>("beta", true, null, EvaluationReason.Default), host.Flagon.GetBooleanDetails("beta", true));
        Assert.Equal(new FlagEvaluationDetails<bool>("beta", false, null, EvaluationReason.Default), host.Flagon.GetBooleanDetails("beta", false));
    }

    [Fact]
    public async Task Creating_a_typed_flag_is_audited_with_the_created_document()
    {
        await using var host = await FlagonTestHost.StartAsync();

        var created = await host.Admin.CreateAsync(new CreateFlagRequest(
            "page-size", Variants: Variants("""{ "small": 10, "large": 50 }"""), DefaultVariant: "small"));

        var entry = Assert.Single(await host.Admin.GetAuditHistoryAsync("page-size"));
        Assert.Equal(FlagAuditAction.Created, entry.Action);
        Assert.Null(entry.Before);
        Assert.Equal(created.Document, entry.After);
        Assert.Equal(FlagType.Number, entry.After!.Type);
    }

    [Theory]
    [InlineData("""{}""", null, "at least one variant")]
    [InlineData("""{ "": "x" }""", null, "empty")]
    [InlineData("""{ "a": "x", "b": 1 }""", null, "one type")]
    [InlineData("""{ "a": true, "b": "true" }""", null, "one type")]
    [InlineData("""{ "list": [1, 2] }""", null, "array")]
    [InlineData("""{ "nothing": null }""", null, "null")]
    [InlineData("""{ "a": "x" }""", "b", "'b'")]
    [InlineData("""{ "yes": true, "no": false }""", "yes", "\"on\": true")]
    [InlineData("""{ "on": false, "off": true }""", "on", "\"on\": true")]
    [InlineData("""{ "on": true }""", "on", "\"on\": true")]
    [InlineData("""{ "on": true, "off": false, "maybe": true }""", "on", "\"on\": true")]
    public async Task Invalid_variants_are_rejected_and_nothing_is_written(string variants, string? defaultVariant, string message)
    {
        await using var host = await FlagonTestHost.StartAsync();

        var ex = await Assert.ThrowsAsync<FlagValidationException>(() => host.Admin.CreateAsync(
            new CreateFlagRequest("bad", Variants: Variants(variants), DefaultVariant: defaultVariant)));

        Assert.Contains(message, ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Null(await host.Admin.GetAsync("bad"));
    }

    [Fact]
    public async Task Variant_names_may_be_up_to_128_characters()
    {
        await using var host = await FlagonTestHost.StartAsync();
        var longest = new string('v', 128);

        await host.Admin.CreateAsync(new CreateFlagRequest("ok", Variants: new Dictionary<string, JsonElement> { [longest] = Json("1") }));
        var ex = await Assert.ThrowsAsync<FlagValidationException>(() => host.Admin.CreateAsync(
            new CreateFlagRequest("too-long", Variants: new Dictionary<string, JsonElement> { [longest + "v"] = Json("1") })));

        Assert.Contains("128", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Variant_names_differing_only_by_case_are_different_variants()
    {
        await using var host = await FlagonTestHost.StartAsync();
        var cased = new Dictionary<string, JsonElement>(StringComparer.Ordinal) { ["Dark"] = Json("1"), ["dark"] = Json("2") };

        var created = await host.Admin.CreateAsync(new CreateFlagRequest("cased", Variants: cased, DefaultVariant: "dark"));

        Assert.Equal(2, created.Document.Variants.Count);
        Assert.Equal("dark", created.Document.DefaultVariant);
    }

    [Fact]
    public async Task Variants_cannot_be_combined_with_enabled()
    {
        await using var host = await FlagonTestHost.StartAsync();

        var ex = await Assert.ThrowsAsync<FlagValidationException>(() => host.Admin.CreateAsync(
            new CreateFlagRequest("both", Enabled: true, Variants: Variants("""{ "a": "x" }"""), DefaultVariant: "a")));

        Assert.Contains("enabled", ex.Message, StringComparison.OrdinalIgnoreCase);
        await Assert.ThrowsAsync<FlagValidationException>(() => host.Admin.CreateAsync(new CreateFlagRequest("orphan", DefaultVariant: "a")));
        Assert.Empty(await host.Admin.ListAsync());
    }

    [Fact]
    public async Task A_typed_flag_keeps_its_variants_when_the_caller_mutates_the_dictionary_afterwards()
    {
        await using var host = await FlagonTestHost.StartAsync();
        var variants = Variants("""{ "a": "x" }""");

        var created = await host.Admin.CreateAsync(new CreateFlagRequest("copy", Variants: variants, DefaultVariant: "a"));
        variants["b"] = Json("\"y\"");

        Assert.Equal(["a"], created.Document.Variants.Keys);
    }
}
