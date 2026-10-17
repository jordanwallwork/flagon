using Flagon.Storage;
using Flagon.Tests.Support;

namespace Flagon.Tests;

/// <summary>Documents written to the store by any tool are evaluated per flagd semantics.</summary>
public class FlagdSemanticsTests
{
    private static async Task<FlagonTestHost> HostWithStoredFlagAsync(string key, string flagdJson)
    {
        var store = new InMemoryFlagStore();
        await store.TryCreateAsync(key, FlagdJson.Deserialize(flagdJson));
        return await FlagonTestHost.StartAsync(o => o.Store = store);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_disabled_flag_returns_the_callers_default_with_reason_disabled(bool callerDefault)
    {
        await using var host = await HostWithStoredFlagAsync("kill-switch", """
            { "state": "DISABLED", "variants": { "on": true, "off": false }, "defaultVariant": "on" }
            """);

        var details = host.Flagon.GetBooleanDetails("kill-switch", callerDefault);

        Assert.Equal(new FlagEvaluationDetails<bool>("kill-switch", callerDefault, null, EvaluationReason.Disabled), details);
        Assert.Equal(callerDefault, host.Flagon.IsEnabled("kill-switch", callerDefault));
    }

    [Fact]
    public async Task A_flag_whose_rule_matches_serves_the_rules_variant_and_otherwise_its_default_variant()
    {
        await using var host = await HostWithStoredFlagAsync("targeted", """
            {
              "state": "ENABLED", "variants": { "on": true, "off": false }, "defaultVariant": "on",
              "targeting": { "if": [ { "==": [ { "var": "country" }, "GB" ] }, "off", null ] }
            }
            """);

        var gb = host.Flagon.GetBooleanDetails("targeted", true, new EvaluationContext("u1", new Dictionary<string, object?> { ["country"] = "GB" }));
        var fr = host.Flagon.GetBooleanDetails("targeted", false, new EvaluationContext("u2", new Dictionary<string, object?> { ["country"] = "FR" }));

        Assert.Equal(new FlagEvaluationDetails<bool>("targeted", false, "off", EvaluationReason.TargetingMatch), gb);
        Assert.Equal(new FlagEvaluationDetails<bool>("targeted", true, "on", EvaluationReason.Default), fr);
    }

    [Fact]
    public async Task A_flag_whose_value_is_not_boolean_returns_the_callers_default_with_type_mismatch()
    {
        await using var host = await HostWithStoredFlagAsync("colour", """
            { "state": "ENABLED", "variants": { "red": "#f00", "blue": "#00f" }, "defaultVariant": "red" }
            """);

        var details = host.Flagon.GetBooleanDetails("colour", true);

        Assert.True(details.Value);
        Assert.Equal(EvaluationReason.Error, details.Reason);
        Assert.Equal(ErrorCode.TypeMismatch, details.ErrorCode);
    }

    [Fact]
    public async Task A_flag_whose_default_variant_does_not_exist_returns_the_callers_default_with_a_parse_error()
    {
        await using var host = await HostWithStoredFlagAsync("broken", """
            { "state": "ENABLED", "variants": { "on": true, "off": false }, "defaultVariant": "maybe" }
            """);

        var details = host.Flagon.GetBooleanDetails("broken", true);

        Assert.True(details.Value);
        Assert.Equal(EvaluationReason.Error, details.Reason);
        Assert.Equal(ErrorCode.ParseError, details.ErrorCode);
    }
}
