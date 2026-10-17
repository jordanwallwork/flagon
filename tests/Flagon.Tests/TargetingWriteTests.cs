using System.Text.Json;
using Flagon.Storage;
using Flagon.Tests.Support;

namespace Flagon.Tests;

/// <summary>
/// Writing targeting through Flagon (<see cref="IFlagonAdmin"/> and <c>Define</c>): valid rules are stored and drive
/// evaluation at once; rules the targeting compiler rejects are refused before anything is written.
/// </summary>
public class TargetingWriteTests
{
    private const string GbRule = """{ "if": [ { "==": [ { "var": "country" }, "GB" ] }, "on", null ] }""";

    private static readonly EvaluationContext Gb = new("alice", new Dictionary<string, object?> { ["country"] = "GB" });
    private static readonly EvaluationContext Fr = new("bob", new Dictionary<string, object?> { ["country"] = "FR" });

    private static JsonElement Rule(string json) => JsonDocument.Parse(json).RootElement.Clone();

    [Fact]
    public async Task An_admin_can_create_a_flag_with_a_targeting_rule_that_drives_evaluation_immediately()
    {
        await using var host = await FlagonTestHost.StartAsync();

        var created = await host.Admin.CreateAsync(new CreateFlagRequest("gb-only", Enabled: false, Targeting: Rule(GbRule)));

        Assert.NotNull(created.Document.Targeting);
        Assert.Equal(new FlagEvaluationDetails<bool>("gb-only", true, "on", EvaluationReason.TargetingMatch), host.Flagon.GetBooleanDetails("gb-only", false, Gb));
        Assert.Equal(new FlagEvaluationDetails<bool>("gb-only", false, "off", EvaluationReason.Default), host.Flagon.GetBooleanDetails("gb-only", true, Fr));
    }

    [Fact]
    public async Task Creating_a_flag_with_an_invalid_rule_is_rejected_naming_the_operator_and_where_it_is()
    {
        await using var host = await FlagonTestHost.StartAsync();

        var ex = await Assert.ThrowsAsync<FlagTargetingException>(() => host.Admin.CreateAsync(new CreateFlagRequest(
            "broken", Targeting: Rule("""{ "if": [ { "and": [ true, { "regex": [ "a", "b" ] } ] }, "on", "off" ] }"""))));

        Assert.Equal("broken", ex.Key);
        Assert.Equal("regex", ex.Operator);
        Assert.Equal("/if/0/and/1", ex.Path);
        Assert.Contains("regex", ex.Message);
        Assert.Contains("/if/0/and/1", ex.Message);
        Assert.Null(await host.Admin.GetAsync("broken"));
    }

    [Fact]
    public async Task A_rule_with_an_unresolved_ref_is_rejected_because_flagon_has_no_flag_set_evaluators()
    {
        await using var host = await FlagonTestHost.StartAsync();

        var ex = await Assert.ThrowsAsync<FlagTargetingException>(() => host.Admin.CreateAsync(new CreateFlagRequest(
            "ref", Targeting: Rule("""{ "if": [ { "$ref": "is_staff" }, "on", null ] }"""))));

        Assert.Equal("$ref", ex.Operator);
        Assert.Equal("/if/0", ex.Path);
        Assert.Contains("is_staff", ex.Message);
        Assert.Contains("$evaluators", ex.Message);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("null")]
    public async Task Empty_or_null_targeting_is_accepted_as_no_targeting(string json)
    {
        await using var host = await FlagonTestHost.StartAsync();

        await host.Admin.CreateAsync(new CreateFlagRequest("plain", Enabled: true, Targeting: Rule(json)));

        Assert.Equal(new FlagEvaluationDetails<bool>("plain", true, "on", EvaluationReason.Static), host.Flagon.GetBooleanDetails("plain", false, Gb));
    }

    [Fact]
    public async Task An_admin_can_set_change_and_clear_a_flags_targeting()
    {
        await using var host = await FlagonTestHost.StartAsync();
        var flag = await host.Admin.CreateAsync(new CreateFlagRequest("rollout", Enabled: false, "Rollout", ["web"]));

        flag = await host.Admin.SetTargetingAsync("rollout", Rule(GbRule), flag.Version);
        Assert.Equal(EvaluationReason.TargetingMatch, host.Flagon.GetBooleanDetails("rollout", false, Gb).Reason);
        Assert.Equal("Rollout", flag.Document.Metadata.Description);
        Assert.Equal(["web"], flag.Document.Metadata.Tags);
        Assert.Equal(FlagDocument.OffVariant, flag.Document.DefaultVariant);

        flag = await host.Admin.SetTargetingAsync("rollout", null, flag.Version);
        Assert.Null(flag.Document.Targeting);
        Assert.Equal(new FlagEvaluationDetails<bool>("rollout", false, "off", EvaluationReason.Static), host.Flagon.GetBooleanDetails("rollout", true, Gb));
    }

    [Fact]
    public async Task Setting_an_invalid_rule_is_rejected_and_leaves_the_flag_unchanged()
    {
        await using var host = await FlagonTestHost.StartAsync();
        var flag = await host.Admin.CreateAsync(new CreateFlagRequest("rollout", Targeting: Rule(GbRule)));

        var ex = await Assert.ThrowsAsync<FlagTargetingException>(() =>
            host.Admin.SetTargetingAsync("rollout", Rule("""{ "nope": [] }"""), flag.Version));

        Assert.Equal("nope", ex.Operator);
        Assert.Equal("", ex.Path);
        Assert.Equal(flag, await host.Admin.GetAsync("rollout"));
    }

    [Fact]
    public async Task Setting_targeting_on_a_stale_version_is_rejected()
    {
        await using var host = await FlagonTestHost.StartAsync();
        var flag = await host.Admin.CreateAsync(new CreateFlagRequest("rollout"));
        await host.Admin.SetEnabledAsync("rollout", true, flag.Version);

        await Assert.ThrowsAsync<FlagVersionConflictException>(() => host.Admin.SetTargetingAsync("rollout", Rule(GbRule), flag.Version));
    }

    [Fact]
    public async Task Setting_targeting_is_audited()
    {
        await using var host = await FlagonTestHost.StartAsync();
        var flag = await host.Admin.CreateAsync(new CreateFlagRequest("rollout"));

        await host.Admin.SetTargetingAsync("rollout", Rule(GbRule), flag.Version);

        var latest = (await host.Admin.GetAuditHistoryAsync("rollout"))[0];
        Assert.Null(latest.Before!.Targeting);
        Assert.NotNull(latest.After!.Targeting);
    }

    [Fact]
    public async Task A_flag_defined_in_code_with_targeting_is_created_with_it()
    {
        await using var host = await FlagonTestHost.StartAsync(o => o.Flagon = f => f.Define("gb-only", false, targeting: GbRule));

        Assert.Equal(new FlagEvaluationDetails<bool>("gb-only", true, "on", EvaluationReason.TargetingMatch), host.Flagon.GetBooleanDetails("gb-only", false, Gb));
        Assert.Equal(new FlagEvaluationDetails<bool>("gb-only", false, "off", EvaluationReason.Default), host.Flagon.GetBooleanDetails("gb-only", true, Fr));
    }

    [Fact]
    public async Task Redeploying_a_definition_never_overwrites_targeting_an_admin_changed()
    {
        var store = new InMemoryFlagStore();
        await using (var firstDeploy = await FlagonTestHost.StartAsync(o =>
        {
            o.Store = store;
            o.Flagon = f => f.Define("gb-only", false, targeting: GbRule);
        }))
        {
            var flag = await firstDeploy.Admin.GetAsync("gb-only");
            await firstDeploy.Admin.SetTargetingAsync("gb-only", null, flag!.Version);
        }

        await using var secondDeploy = await FlagonTestHost.StartAsync(o =>
        {
            o.Store = store;
            o.Flagon = f => f.Define("gb-only", false, "now described", targeting: GbRule);
        });

        var updated = await secondDeploy.Admin.GetAsync("gb-only");
        Assert.Equal("now described", updated!.Document.Metadata.Description);
        Assert.Null(updated.Document.Targeting);
    }

    [Theory]
    [InlineData("""{ "if": [ { "unknown_op": [1] }, "on", null ] }""", "unknown_op")]
    [InlineData("""{ "if": [ { "$ref": "is_staff" }, "on", null ] }""", "$ref")]
    public void Defining_a_flag_with_an_invalid_rule_fails_at_configuration(string targeting, string op)
    {
        var options = new FlagonOptions();

        var ex = Assert.Throws<FlagTargetingException>(() => options.Define("broken", targeting: targeting));

        Assert.Equal("broken", ex.Key);
        Assert.Equal(op, ex.Operator);
        Assert.Equal("/if/0", ex.Path);
    }

    [Fact]
    public void Defining_a_flag_whose_targeting_is_not_json_fails_at_configuration()
    {
        var options = new FlagonOptions();

        Assert.Throws<FlagValidationException>(() => options.Define("broken", targeting: "{ if: "));
    }
}
