using System.Text.Json;
using Flagon.Internal.Targeting;
using Flagon.Storage;
using Flagon.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace Flagon.Tests;

/// <summary>
/// How the evaluator handles what a targeting compiler produces, exercised through the host with a fake compiler
/// registered in place of the default one.
/// </summary>
public class TargetingSeamTests
{
    private const string TargetedFlag = """
        {
          "state": "ENABLED", "variants": { "on": true, "off": false }, "defaultVariant": "off",
          "targeting": { "fake": "rule" }
        }
        """;

    private static async Task<FlagonTestHost> HostAsync(ITargetingCompiler compiler, params (string Key, string Json)[] flags)
    {
        var store = new InMemoryFlagStore();
        foreach (var (key, json) in flags)
        {
            await store.TryCreateAsync(key, FlagdJson.Deserialize(json));
        }

        return await FlagonTestHost.StartAsync(o =>
        {
            o.Store = store;
            o.Services = s => s.AddSingleton(compiler);
        });
    }

    [Fact]
    public async Task A_rule_resolving_to_a_variant_serves_it_with_reason_targeting_match()
    {
        var compiler = new FakeTargetingCompiler(_ => (in TargetingContext context) => "on");
        await using var host = await HostAsync(compiler, ("targeted", TargetedFlag));

        var details = host.Flagon.GetBooleanDetails("targeted", false);

        Assert.Equal(new FlagEvaluationDetails<bool>("targeted", true, "on", EvaluationReason.TargetingMatch), details);
    }

    [Fact]
    public async Task A_rule_resolving_to_null_serves_the_default_variant_with_reason_default()
    {
        var compiler = new FakeTargetingCompiler(_ => (in TargetingContext context) => null);
        await using var host = await HostAsync(compiler, ("targeted", TargetedFlag));

        var details = host.Flagon.GetBooleanDetails("targeted", true);

        Assert.Equal(new FlagEvaluationDetails<bool>("targeted", false, "off", EvaluationReason.Default), details);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_rule_resolving_to_a_variant_the_flag_does_not_define_returns_the_callers_default_with_an_error(bool callerDefault)
    {
        var compiler = new FakeTargetingCompiler(_ => (in TargetingContext context) => "maybe");
        await using var host = await HostAsync(compiler, ("targeted", TargetedFlag));

        var details = host.Flagon.GetBooleanDetails("targeted", callerDefault);

        Assert.Equal(callerDefault, details.Value);
        Assert.Null(details.Variant);
        Assert.Equal(EvaluationReason.Error, details.Reason);
        Assert.Equal(ErrorCode.General, details.ErrorCode);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_rule_that_fails_to_compile_returns_the_callers_default_with_a_parse_error(bool callerDefault)
    {
        var compiler = new FakeTargetingCompiler(_ => TargetingCompilation.Failure("unknown operator 'fake'"));
        await using var host = await HostAsync(compiler, ("targeted", TargetedFlag));

        var details = host.Flagon.GetBooleanDetails("targeted", callerDefault);

        Assert.Equal(callerDefault, details.Value);
        Assert.Null(details.Variant);
        Assert.Equal(EvaluationReason.Error, details.Reason);
        Assert.Equal(ErrorCode.ParseError, details.ErrorCode);
        Assert.Contains("unknown operator 'fake'", details.ErrorMessage);
    }

    [Fact]
    public async Task A_compiler_that_throws_is_treated_as_a_compile_failure()
    {
        var compiler = new FakeTargetingCompiler(new Func<JsonElement, TargetingCompilation>(_ => throw new InvalidOperationException("boom")));
        await using var host = await HostAsync(compiler, ("targeted", TargetedFlag));

        var details = host.Flagon.GetBooleanDetails("targeted", true);

        Assert.Equal(new FlagEvaluationDetails<bool>("targeted", true, null, EvaluationReason.Error, ErrorCode.ParseError, details.ErrorMessage), details);
        Assert.True(host.Status.IsLoaded);
    }

    [Fact]
    public async Task A_broken_rule_on_one_flag_does_not_affect_other_flags()
    {
        var compiler = new FakeTargetingCompiler(t => t.TryGetProperty("broken", out _)
            ? TargetingCompilation.Failure("bad rule")
            : TargetingCompilation.Success((in TargetingContext context) => "on"));
        await using var host = await HostAsync(compiler,
            ("broken", """{ "state": "ENABLED", "variants": { "on": true, "off": false }, "defaultVariant": "off", "targeting": { "broken": true } }"""),
            ("healthy", TargetedFlag),
            ("plain", """{ "state": "ENABLED", "variants": { "on": true, "off": false }, "defaultVariant": "on" }"""));

        Assert.Equal(ErrorCode.ParseError, host.Flagon.GetBooleanDetails("broken").ErrorCode);
        Assert.Equal(new FlagEvaluationDetails<bool>("healthy", true, "on", EvaluationReason.TargetingMatch), host.Flagon.GetBooleanDetails("healthy"));
        Assert.Equal(new FlagEvaluationDetails<bool>("plain", true, "on", EvaluationReason.Static), host.Flagon.GetBooleanDetails("plain"));
    }

    [Fact]
    public async Task A_broken_rule_is_warned_about_once_per_snapshot_version()
    {
        var compiler = new FakeTargetingCompiler(_ => TargetingCompilation.Failure("bad rule"));
        await using var host = await HostAsync(compiler, ("targeted", TargetedFlag));

        for (var i = 0; i < 5; i++)
        {
            host.Flagon.GetBooleanDetails("targeted");
        }

        await host.AdvanceToNextRefreshAsync(); // unchanged store: same snapshot version, no new warning
        Assert.Single(CompileWarnings(host), w => w.Message.Contains("'targeted'") && w.Message.Contains("bad rule"));

        await host.Admin.CreateAsync(new CreateFlagRequest("another")); // new snapshot version
        host.Flagon.GetBooleanDetails("targeted");

        Assert.Equal(2, CompileWarnings(host).Count);
    }

    [Fact]
    public async Task Rules_are_compiled_once_per_snapshot_load_not_per_evaluation()
    {
        var compiler = new FakeTargetingCompiler(_ => (in TargetingContext context) => "on");
        await using var host = await HostAsync(compiler,
            ("targeted", TargetedFlag),
            ("plain", """{ "state": "ENABLED", "variants": { "on": true, "off": false }, "defaultVariant": "on" }"""));

        for (var i = 0; i < 10; i++)
        {
            host.Flagon.GetBooleanDetails("targeted");
            host.Flagon.GetAllBooleanDetails();
        }

        await host.AdvanceToNextRefreshAsync(); // unchanged store: the snapshot is kept, nothing recompiled

        Assert.Equal(1, compiler.Compilations); // only the flag with targeting is compiled
    }

    [Fact]
    public async Task A_disabled_flag_returns_the_callers_default_without_running_its_rule()
    {
        var compiler = new FakeTargetingCompiler(_ => (in TargetingContext context) => throw new InvalidOperationException("rule must not run"));
        await using var host = await HostAsync(compiler, ("targeted",
            """{ "state": "DISABLED", "variants": { "on": true, "off": false }, "defaultVariant": "off", "targeting": { "fake": "rule" } }"""));

        Assert.Equal(new FlagEvaluationDetails<bool>("targeted", true, null, EvaluationReason.Disabled), host.Flagon.GetBooleanDetails("targeted", true));
    }

    [Fact]
    public async Task A_rule_that_throws_while_evaluating_returns_the_callers_default_with_a_parse_error()
    {
        // flagd reports a JsonLogic failure at evaluation time (e.g. cat on a bool) as PARSE_ERROR.
        var compiler = new FakeTargetingCompiler(_ => (in TargetingContext context) => throw new InvalidOperationException("boom"));
        await using var host = await HostAsync(compiler, ("targeted", TargetedFlag));

        var details = host.Flagon.GetBooleanDetails("targeted", true);

        Assert.True(details.Value);
        Assert.Null(details.Variant);
        Assert.Equal(EvaluationReason.Error, details.Reason);
        Assert.Equal(ErrorCode.ParseError, details.ErrorCode);
        Assert.Contains("boom", details.ErrorMessage);
    }

    [Fact]
    public async Task A_rule_that_throws_while_evaluating_is_warned_about_once_per_snapshot_version()
    {
        var compiler = new FakeTargetingCompiler(_ => (in TargetingContext context) => throw new InvalidOperationException("boom"));
        await using var host = await HostAsync(compiler, ("targeted", TargetedFlag));

        for (var i = 0; i < 5; i++)
        {
            host.Flagon.GetBooleanDetails("targeted");
        }

        host.Flagon.GetAllBooleanDetails();
        var warning = Assert.Single(RuleFailureWarnings(host));
        Assert.Contains("'targeted'", warning.Message);
        Assert.IsType<InvalidOperationException>(warning.Exception);

        await host.Admin.CreateAsync(new CreateFlagRequest("another")); // new snapshot version
        host.Flagon.GetBooleanDetails("targeted");

        Assert.Equal(2, RuleFailureWarnings(host).Count);
    }

    [Fact]
    public async Task A_rule_that_runs_out_of_memory_is_not_swallowed_as_a_parse_error()
    {
        var compiler = new FakeTargetingCompiler(_ => (in TargetingContext context) => throw new OutOfMemoryException());
        await using var host = await HostAsync(compiler, ("targeted", TargetedFlag));

        Assert.Throws<OutOfMemoryException>(() => host.Flagon.GetBooleanDetails("targeted"));
    }

    [Fact]
    public async Task A_compiler_that_runs_out_of_memory_is_not_swallowed_as_a_compile_failure()
    {
        var compiler = new FakeTargetingCompiler(new Func<JsonElement, TargetingCompilation>(_ => throw new OutOfMemoryException()));
        await using var host = await HostAsync(compiler);

        await Assert.ThrowsAsync<OutOfMemoryException>(() => host.Admin.CreateAsync(new CreateFlagRequest(
            "targeted", Targeting: JsonDocument.Parse("""{ "fake": "rule" }""").RootElement.Clone())));
    }

    [Fact]
    public async Task A_rule_failure_on_a_superseded_snapshot_is_not_warned_about_again()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        CompiledRule rule = (in TargetingContext context) =>
        {
            if (context.TryGetValue("block", out _))
            {
                entered.Set();
                release.Wait();
            }

            throw new InvalidOperationException("boom");
        };
        var compiler = new FakeTargetingCompiler(_ => rule);
        await using var host = await HostAsync(compiler, ("targeted", TargetedFlag));
        var late = Task.Run(() => host.Flagon.GetBooleanDetails("targeted", false,
            new EvaluationContext(null, new Dictionary<string, object?> { ["block"] = true })));
        entered.Wait();

        await host.Admin.CreateAsync(new CreateFlagRequest("another")); // new snapshot version
        host.Flagon.GetBooleanDetails("targeted"); // warns for the new version
        release.Set();
        Assert.Equal(ErrorCode.ParseError, (await late).ErrorCode); // evaluated on the old snapshot, finished late
        host.Flagon.GetBooleanDetails("targeted");

        Assert.Single(RuleFailureWarnings(host));
    }

    [Fact]
    public async Task Empty_targeting_is_no_targeting_and_serves_the_default_variant_with_reason_static()
    {
        var compiler = new FakeTargetingCompiler(_ => (in TargetingContext context) => "on");
        await using var host = await HostAsync(compiler, ("empty",
            """{ "state": "ENABLED", "variants": { "on": true, "off": false }, "defaultVariant": "off", "targeting": {} }"""));

        Assert.Equal(new FlagEvaluationDetails<bool>("empty", false, "off", EvaluationReason.Static), host.Flagon.GetBooleanDetails("empty", true));
        Assert.Equal(0, compiler.Compilations);
    }

    [Fact]
    public async Task An_archived_flag_returns_the_callers_default_even_when_its_rule_is_broken()
    {
        var compiler = new FakeTargetingCompiler(_ => TargetingCompilation.Failure("bad rule"));
        await using var host = await HostAsync(compiler, ("targeted", TargetedFlag));
        var record = await host.Admin.GetAsync("targeted");
        await host.Admin.ArchiveAsync("targeted", record!.Version);

        Assert.Equal(new FlagEvaluationDetails<bool>("targeted", true, null, EvaluationReason.Archived), host.Flagon.GetBooleanDetails("targeted", true));
    }

    [Fact]
    public async Task A_rule_sees_the_targeting_key_attributes_flag_key_and_timestamp()
    {
        var compiler = new FakeTargetingCompiler(_ => (in TargetingContext context) =>
            context.TryGetValue("targetingKey", out var user) && (string?)user == "alice"
            && context.TryGetValue("country", out var country) && (string?)country == "GB"
            && context.TryGetValue("$flagd.flagKey", out var flagKey) && (string?)flagKey == "targeted"
            && context.TryGetValue("$flagd.timestamp", out var timestamp) && (long?)timestamp == 1767225600 // 2026-01-01T00:00:00Z
                ? "on"
                : null);
        await using var host = await HostAsync(compiler, ("targeted", TargetedFlag));

        var matching = new EvaluationContext("alice", new Dictionary<string, object?> { ["country"] = "GB" });
        var other = new EvaluationContext("bob", new Dictionary<string, object?> { ["country"] = "GB" });

        Assert.Equal(EvaluationReason.TargetingMatch, host.Flagon.GetBooleanDetails("targeted", context: matching).Reason);
        Assert.Equal(EvaluationReason.Default, host.Flagon.GetBooleanDetails("targeted", context: other).Reason);
        Assert.Equal(EvaluationReason.TargetingMatch, host.Flagon.GetAllBooleanDetails(matching).Flags.Single().Reason);
    }

    [Fact]
    public async Task The_injected_targeting_key_wins_over_an_attribute_of_the_same_name()
    {
        var compiler = new FakeTargetingCompiler(_ => (in TargetingContext context) =>
            context.TryGetValue("targetingKey", out var user) && (string?)user == "alice" ? "on" : null);
        await using var host = await HostAsync(compiler, ("targeted", TargetedFlag));

        var context = new EvaluationContext("alice", new Dictionary<string, object?> { ["targetingKey"] = "mallory" });

        Assert.Equal(EvaluationReason.TargetingMatch, host.Flagon.GetBooleanDetails("targeted", context: context).Reason);
    }

    [Fact]
    public async Task The_default_compiler_evaluates_flagd_targeting_rules()
    {
        var store = new InMemoryFlagStore();
        await store.TryCreateAsync("targeted", FlagdJson.Deserialize("""
            {
              "state": "ENABLED", "variants": { "on": true, "off": false }, "defaultVariant": "off",
              "targeting": { "if": [ { "==": [ { "var": "country" }, "GB" ] }, "on", null ] }
            }
            """));
        await using var host = await FlagonTestHost.StartAsync(o => o.Store = store);

        var gb = new EvaluationContext("alice", new Dictionary<string, object?> { ["country"] = "GB" });
        var fr = new EvaluationContext("bob", new Dictionary<string, object?> { ["country"] = "FR" });

        Assert.Equal(new FlagEvaluationDetails<bool>("targeted", true, "on", EvaluationReason.TargetingMatch), host.Flagon.GetBooleanDetails("targeted", false, gb));
        Assert.Equal(new FlagEvaluationDetails<bool>("targeted", false, "off", EvaluationReason.Default), host.Flagon.GetBooleanDetails("targeted", true, fr));
    }

    [Fact]
    public async Task The_default_compiler_rejects_an_operator_outside_flagds_set_with_a_parse_error()
    {
        var store = new InMemoryFlagStore();
        await store.TryCreateAsync("targeted", FlagdJson.Deserialize(TargetedFlag));
        await using var host = await FlagonTestHost.StartAsync(o => o.Store = store);

        var details = host.Flagon.GetBooleanDetails("targeted", true);

        Assert.Equal(EvaluationReason.Error, details.Reason);
        Assert.Equal(ErrorCode.ParseError, details.ErrorCode);
        Assert.True(details.Value);
    }

    [Fact]
    public async Task Targeting_defined_in_code_is_validated_by_the_registered_compiler_and_fails_startup_when_rejected()
    {
        var compiler = new FakeTargetingCompiler(_ => TargetingCompilation.Failure("rejected by the replacement", "/if/0", "in"));
        var store = new InMemoryFlagStore();

        var ex = await Assert.ThrowsAsync<FlagTargetingException>(() => FlagonTestHost.StartAsync(o =>
        {
            o.Store = store;
            o.Flagon = f => f.Define("gb-only", false, targeting: """{ "if": [ { "in": [ { "var": "country" }, ["GB"] ] }, "on", null ] }""");
            o.Services = s => s.AddSingleton<ITargetingCompiler>(compiler);
        }));

        Assert.Equal("gb-only", ex.Key);
        Assert.Equal("/if/0", ex.Path);
        Assert.Equal("in", ex.Operator);
        Assert.Contains("rejected by the replacement", ex.Message);
        Assert.Null(await store.GetAsync("gb-only"));
    }

    [Fact]
    public async Task Targeting_defined_in_code_that_only_the_registered_compiler_understands_is_seeded_and_evaluated()
    {
        var compiler = new FakeTargetingCompiler(_ => (in TargetingContext context) => "on");
        await using var host = await FlagonTestHost.StartAsync(o =>
        {
            o.Flagon = f => f.Define("custom", false, targeting: """{ "fake": "rule" }""");
            o.Services = s => s.AddSingleton<ITargetingCompiler>(compiler);
        });

        Assert.Equal(new FlagEvaluationDetails<bool>("custom", true, "on", EvaluationReason.TargetingMatch), host.Flagon.GetBooleanDetails("custom"));
    }

    private static IReadOnlyList<Support.CapturedLog> CompileWarnings(FlagonTestHost host) =>
        host.Logs.Flagon(Microsoft.Extensions.Logging.LogLevel.Warning).Where(e => e.EventId.Id == 1103).ToArray();

    private static IReadOnlyList<Support.CapturedLog> RuleFailureWarnings(FlagonTestHost host) =>
        host.Logs.Flagon(Microsoft.Extensions.Logging.LogLevel.Warning).Where(e => e.EventId.Id == 1002).ToArray();
}

/// <summary>A compiler whose output each test chooses: a function from the targeting JSON to a compilation.</summary>
internal sealed class FakeTargetingCompiler(Func<JsonElement, TargetingCompilation> compile) : ITargetingCompiler
{
    public FakeTargetingCompiler(Func<JsonElement, CompiledRule> rule) : this(t => TargetingCompilation.Success(rule(t))) { }

    private int _compilations;

    public int Compilations => _compilations;

    public TargetingCompilation Compile(string flagKey, JsonElement targeting)
    {
        Interlocked.Increment(ref _compilations);
        return compile(targeting);
    }
}
