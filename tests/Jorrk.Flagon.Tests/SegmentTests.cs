using System.Text.Json;
using Jorrk.Flagon.Internal.Targeting;
using Jorrk.Flagon.Internal.Targeting.JsonLogic;
using Jorrk.Flagon.Storage;
using Jorrk.Flagon.Tests.Support;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Jorrk.Flagon.Tests;

/// <summary>
/// Segments through <see cref="IFlagonAdmin"/>, <c>DefineSegment</c> and the snapshot: references resolve against the
/// store on write, segment edits reach dependent flags on refresh, and archive/purge refuse to break flags.
/// </summary>
public class SegmentTests
{
    private const string StaffRule = """{"ends_with":[{"var":"email"},"@example.com"]}""";
    private const string UsesStaff = """{"if":[{"$ref":"staff"},"on",null]}""";

    private static readonly EvaluationContext Ada = new("ada", new Dictionary<string, object?> { ["email"] = "ada@example.com" });
    private static readonly EvaluationContext Bob = new("bob", new Dictionary<string, object?> { ["email"] = "bob@elsewhere.test" });

    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static async Task<(FlagonTestHost Host, SegmentRecord Staff)> HostWithStaffAsync(Action<FlagonTestHostOptions>? configure = null)
    {
        var host = await FlagonTestHost.StartAsync(configure);
        var staff = await host.Admin.CreateSegmentAsync(new CreateSegmentRequest("staff", Json(StaffRule), "Employees", ["internal"]));
        return (host, staff);
    }

    [Fact]
    public async Task A_flag_referencing_a_segment_serves_the_segments_audience()
    {
        var (host, staff) = await HostWithStaffAsync();
        await using var _ = host;

        await host.Admin.CreateAsync(new CreateFlagRequest("beta", Targeting: Json(UsesStaff)));

        Assert.Equal(new FlagEvaluationDetails<bool>("beta", true, "on", EvaluationReason.TargetingMatch), host.Flagon.GetBooleanDetails("beta", false, Ada));
        Assert.Equal(new FlagEvaluationDetails<bool>("beta", false, "off", EvaluationReason.Default), host.Flagon.GetBooleanDetails("beta", true, Bob));
        Assert.Equal("Employees", staff.Document.Metadata.Description);
        Assert.Equal(["internal"], staff.Document.Metadata.Tags);
        Assert.Equal(FlagSource.Manual, staff.Document.Metadata.Source);
        Assert.Equal(FlagonTestHost.Epoch, staff.Document.Metadata.CreatedAt);
    }

    [Fact]
    public async Task Editing_a_segment_changes_who_every_dependent_flag_serves_in_every_app_after_its_next_refresh()
    {
        var store = new InMemoryFlagStore();
        var (appA, staff) = await HostWithStaffAsync(o => o.Store = store);
        await using var a = appA;
        await appA.Admin.CreateAsync(new CreateFlagRequest("beta", Targeting: Json(UsesStaff)));
        await appA.Admin.CreateAsync(new CreateFlagRequest("gamma", Targeting: Json("""{"if":[{"!":[{"$ref":"staff"}]},"on",null]}""")));
        await using var appB = await FlagonTestHost.StartAsync(o => o.Store = store);
        Assert.True(appB.Flagon.IsEnabled("beta", context: Ada));

        await appA.Admin.SetSegmentRuleAsync("staff", Json("""{"ends_with":[{"var":"email"},"@elsewhere.test"]}"""), staff.Version);

        Assert.True(appA.Flagon.IsEnabled("beta", context: Bob));
        Assert.True(appB.Flagon.IsEnabled("beta", context: Ada));
        await appB.AdvanceToNextRefreshAsync();
        Assert.False(appB.Flagon.IsEnabled("beta", context: Ada));
        Assert.True(appB.Flagon.IsEnabled("beta", context: Bob));
        Assert.True(appB.Flagon.IsEnabled("gamma", context: Ada));
        Assert.False(appB.Flagon.IsEnabled("gamma", context: Bob));
    }

    [Fact]
    public async Task A_flag_referencing_a_segment_that_does_not_exist_is_rejected()
    {
        await using var host = await FlagonTestHost.StartAsync();
        var flag = await host.Admin.CreateAsync(new CreateFlagRequest("beta"));

        var create = await Assert.ThrowsAsync<FlagTargetingException>(() => host.Admin.CreateAsync(new CreateFlagRequest("other", Targeting: Json(UsesStaff))));
        var set = await Assert.ThrowsAsync<FlagTargetingException>(() => host.Admin.SetTargetingAsync("beta", Json(UsesStaff), flag.Version));

        Assert.All([create, set], ex =>
        {
            Assert.Equal("$ref", ex.Operator);
            Assert.Equal("/if/0", ex.Path);
            Assert.Equal("segment 'staff' does not exist", ex.Error);
        });
        Assert.Null(await host.Admin.GetAsync("other"));
        Assert.Null((await host.Admin.GetAsync("beta"))!.Document.Targeting);
    }

    [Fact]
    public async Task A_flag_cannot_newly_reference_an_archived_segment()
    {
        var (host, staff) = await HostWithStaffAsync();
        await using var _ = host;
        var flag = await host.Admin.CreateAsync(new CreateFlagRequest("beta"));
        await host.Admin.ArchiveSegmentAsync("staff", staff.Version);

        var create = await Assert.ThrowsAsync<SegmentArchivedException>(() => host.Admin.CreateAsync(new CreateFlagRequest("other", Targeting: Json(UsesStaff))));
        var set = await Assert.ThrowsAsync<SegmentArchivedException>(() => host.Admin.SetTargetingAsync("beta", Json(UsesStaff), flag.Version));

        Assert.Equal("other", create.FlagKey);
        Assert.Equal(["staff"], create.SegmentKeys);
        Assert.Equal("beta", set.FlagKey);
    }

    [Fact]
    public async Task A_rule_that_only_fails_once_its_segments_are_inlined_is_rejected()
    {
        var (host, _) = await HostWithStaffAsync(o => o.Services = s => s.AddSingleton<ITargetingCompiler>(new LimitedCompiler(maxLength: 120)));
        await using var __ = host;

        var ex = await Assert.ThrowsAsync<FlagTargetingException>(() => host.Admin.CreateAsync(new CreateFlagRequest(
            "beta", Targeting: Json("""{"if":[{"and":[{"$ref":"staff"},{"$ref":"staff"}]},"on",null]}"""))));

        Assert.Equal("beta", ex.Key);
        Assert.Contains("rule too long", ex.Error);
    }

    [Theory]
    [InlineData("null", "", null)]
    [InlineData("{}", "", null)]
    [InlineData("""{"if":[{"$ref":"other"},true,false]}""", "/if/0", "$ref")]
    [InlineData("""{"and":[true,{"unknown_op":[1]}]}""", "/and/1", "unknown_op")]
    public async Task A_segment_rule_must_be_a_rule_without_references_that_compiles_on_its_own(string rule, string? path, string? op)
    {
        await using var host = await FlagonTestHost.StartAsync();
        await host.Admin.CreateSegmentAsync(new CreateSegmentRequest("other", Json(StaffRule)));

        var ex = await Assert.ThrowsAsync<SegmentRuleException>(() => host.Admin.CreateSegmentAsync(new CreateSegmentRequest("staff", Json(rule))));

        Assert.Equal("staff", ex.Key);
        Assert.Equal(op, ex.Operator);
        if (op is not null)
        {
            Assert.Equal(path, ex.Path);
        }

        Assert.Null(await host.Admin.GetSegmentAsync("staff"));
    }

    [Fact]
    public async Task Segment_keys_follow_the_flag_key_rules_and_must_be_unique()
    {
        var (host, _) = await HostWithStaffAsync();
        await using var __ = host;

        await Assert.ThrowsAsync<FlagValidationException>(() => host.Admin.CreateSegmentAsync(new CreateSegmentRequest("-bad key", Json(StaffRule))));
        await Assert.ThrowsAsync<FlagValidationException>(() => host.Admin.CreateSegmentAsync(new CreateSegmentRequest("ok", Json(StaffRule), Tags: ["a,b"])));
        Assert.Equal("staff", (await Assert.ThrowsAsync<SegmentAlreadyExistsException>(() => host.Admin.CreateSegmentAsync(new CreateSegmentRequest("staff", Json(StaffRule))))).Key);

        // A flag may share a segment's key.
        await host.Admin.CreateAsync(new CreateFlagRequest("staff"));
    }

    [Fact]
    public async Task A_segment_edit_that_would_stop_a_dependent_flag_compiling_is_rejected()
    {
        var (host, staff) = await HostWithStaffAsync(o => o.Services = s => s.AddSingleton<ITargetingCompiler>(new LimitedCompiler(maxLength: 120)));
        await using var _ = host;
        await host.Admin.CreateAsync(new CreateFlagRequest("beta", Targeting: Json("""{"if":[{"and":[{"$ref":"staff"},true]},"on",null]}""")));
        var longer = Json("""{"ends_with":[{"var":"email"},"@a-much-much-much-much-much-much-much-much-longer-domain.example.com"]}""");
        Assert.True(TargetingRules.Compile(new LimitedCompiler(120), "staff", longer).Succeeded);

        var ex = await Assert.ThrowsAsync<SegmentRuleException>(() => host.Admin.SetSegmentRuleAsync("staff", longer, staff.Version));

        Assert.Contains("flag 'beta'", ex.Message);
        Assert.Equal(staff.Version, (await host.Admin.GetSegmentAsync("staff"))!.Version);
    }

    [Fact]
    public async Task An_archived_dependent_does_not_block_a_segment_edit()
    {
        var (host, staff) = await HostWithStaffAsync(o => o.Services = s => s.AddSingleton<ITargetingCompiler>(new LimitedCompiler(maxLength: 120)));
        await using var _ = host;
        var beta = await host.Admin.CreateAsync(new CreateFlagRequest("beta", Targeting: Json("""{"if":[{"and":[{"$ref":"staff"},true]},"on",null]}""")));
        await host.Admin.ArchiveAsync("beta", beta.Version);
        var longer = Json("""{"ends_with":[{"var":"email"},"@a-much-much-much-much-much-much-much-much-longer-domain.example.com"]}""");

        var updated = await host.Admin.SetSegmentRuleAsync("staff", longer, staff.Version);

        Assert.Equal(longer.GetRawText(), updated.Document.Rule.GetRawText());
    }

    [Fact]
    public async Task A_segment_rule_cannot_be_edited_while_archived_or_from_a_stale_version()
    {
        var (host, staff) = await HostWithStaffAsync();
        await using var _ = host;
        var archived = await host.Admin.ArchiveSegmentAsync("staff", staff.Version);

        var conflict = await Assert.ThrowsAsync<SegmentVersionConflictException>(() => host.Admin.SetSegmentRuleAsync("staff", Json(StaffRule), staff.Version));
        await Assert.ThrowsAsync<FlagValidationException>(() => host.Admin.SetSegmentRuleAsync("staff", Json(StaffRule), archived.Version));
        await Assert.ThrowsAsync<SegmentNotFoundException>(() => host.Admin.SetSegmentRuleAsync("missing", Json(StaffRule), 1));

        Assert.Equal(archived.Version, conflict.CurrentVersion);
        Assert.Contains(host.Logs.Flagon(LogLevel.Information), l => l.EventId.Id == 1202);

        // Metadata may still be edited, as for flags.
        var described = await host.Admin.UpdateSegmentMetadataAsync("staff", new UpdateSegmentMetadataRequest("Former staff", []), archived.Version);
        Assert.Equal("Former staff", described.Document.Metadata.Description);
        Assert.Empty(described.Document.Metadata.Tags);
        Assert.True(described.Archived);
    }

    [Fact]
    public async Task Archiving_a_segment_is_refused_while_a_live_flag_references_it_but_not_for_archived_flags()
    {
        var (host, staff) = await HostWithStaffAsync();
        await using var _ = host;
        var beta = await host.Admin.CreateAsync(new CreateFlagRequest("beta", Targeting: Json(UsesStaff)));
        await host.Admin.CreateAsync(new CreateFlagRequest("alpha", Targeting: Json(UsesStaff)));

        var inUse = await Assert.ThrowsAsync<SegmentInUseException>(() => host.Admin.ArchiveSegmentAsync("staff", staff.Version));
        Assert.Equal(["alpha", "beta"], inUse.FlagKeys);

        await host.Admin.ArchiveAsync("beta", beta.Version);
        var alpha = await host.Admin.GetAsync("alpha");
        await host.Admin.SetTargetingAsync("alpha", null, alpha!.Version);
        var archived = await host.Admin.ArchiveSegmentAsync("staff", staff.Version);

        Assert.True(archived.Archived);
    }

    [Fact]
    public async Task Purging_a_segment_is_refused_while_any_flag_references_it_including_archived_ones()
    {
        var (host, staff) = await HostWithStaffAsync();
        await using var _ = host;
        var beta = await host.Admin.CreateAsync(new CreateFlagRequest("beta", Targeting: Json(UsesStaff)));
        beta = await host.Admin.ArchiveAsync("beta", beta.Version);
        var archived = await host.Admin.ArchiveSegmentAsync("staff", staff.Version);

        var inUse = await Assert.ThrowsAsync<SegmentInUseException>(() => host.Admin.PurgeSegmentAsync("staff", archived.Version));
        Assert.Equal(["beta"], inUse.FlagKeys);
        Assert.Contains("purged", inUse.Message);

        await host.Admin.PurgeAsync("beta", beta.Version);
        await host.Admin.PurgeSegmentAsync("staff", archived.Version);

        Assert.Null(await host.Admin.GetSegmentAsync("staff"));
        Assert.Equal(FlagAuditAction.Purged, (await host.Admin.GetSegmentAuditHistoryAsync("staff"))[0].Action);
    }

    [Fact]
    public async Task A_flag_referencing_an_archived_segment_can_only_be_restored_once_the_segment_is()
    {
        var (host, staff) = await HostWithStaffAsync();
        await using var _ = host;
        var beta = await host.Admin.CreateAsync(new CreateFlagRequest("beta", Targeting: Json(UsesStaff)));
        beta = await host.Admin.ArchiveAsync("beta", beta.Version);
        var archived = await host.Admin.ArchiveSegmentAsync("staff", staff.Version);

        var ex = await Assert.ThrowsAsync<SegmentArchivedException>(() => host.Admin.RestoreAsync("beta", beta.Version));
        Assert.Equal(["staff"], ex.SegmentKeys);
        Assert.True((await host.Admin.GetAsync("beta"))!.Archived);

        await host.Admin.RestoreSegmentAsync("staff", archived.Version);
        await host.Admin.RestoreAsync("beta", beta.Version);
        Assert.True(host.Flagon.IsEnabled("beta", context: Ada));
    }

    [Fact]
    public async Task A_flag_that_no_longer_compiles_with_its_segments_cannot_be_restored()
    {
        var (host, staff) = await HostWithStaffAsync(o => o.Services = s => s.AddSingleton<ITargetingCompiler>(new LimitedCompiler(maxLength: 120)));
        await using var _ = host;
        var beta = await host.Admin.CreateAsync(new CreateFlagRequest("beta", Targeting: Json("""{"if":[{"and":[{"$ref":"staff"},true]},"on",null]}""")));
        beta = await host.Admin.ArchiveAsync("beta", beta.Version);
        // Archived dependents are not checked by a segment edit.
        await host.Admin.SetSegmentRuleAsync("staff",
            Json("""{"ends_with":[{"var":"email"},"@a-much-much-much-much-much-much-much-much-longer-domain.example.com"]}"""), staff.Version);

        var ex = await Assert.ThrowsAsync<FlagTargetingException>(() => host.Admin.RestoreAsync("beta", beta.Version));

        Assert.Contains("rule too long", ex.Error);
        Assert.True((await host.Admin.GetAsync("beta"))!.Archived);
    }

    [Fact]
    public async Task A_segment_preview_says_so_when_the_engine_refuses_the_truthiness_check()
    {
        // The segment (47 characters) fits the limit; the "!!" wrapper around it (56) does not.
        await using var host = await FlagonTestHost.StartAsync(o => o.Services = s => s.AddSingleton<ITargetingCompiler>(new LimitedCompiler(maxLength: 50)));
        var evaluator = host.Services.GetRequiredService<Internal.FlagonEvaluator>();

        var preview = evaluator.PreviewSegment("staff", Json(StaffRule), Ada);

        Assert.False(preview.Matches);
        Assert.Equal("true", preview.Result);
        Assert.Contains("rule too long", preview.Error);
    }

    [Fact]
    public async Task A_flag_referencing_a_purged_segment_cannot_be_restored()
    {
        var store = new InMemoryFlagStore();
        var (host, staff) = await HostWithStaffAsync(o => o.Store = store);
        await using var _ = host;
        var beta = await host.Admin.CreateAsync(new CreateFlagRequest("beta", Targeting: Json(UsesStaff)));
        beta = await host.Admin.ArchiveAsync("beta", beta.Version);
        // Behind the admin's back, as a purge racing a flag write could leave it.
        var archived = await store.ArchiveSegmentAsync("staff", staff.Version);
        await store.PurgeSegmentAsync("staff", archived.Version);

        var ex = await Assert.ThrowsAsync<FlagTargetingException>(() => host.Admin.RestoreAsync("beta", beta.Version));

        Assert.Equal("$ref", ex.Operator);
        Assert.Equal("segment 'staff' does not exist", ex.Error);
    }

    [Fact]
    public async Task An_archived_segment_keeps_resolving_for_the_snapshot()
    {
        var (host, staff) = await HostWithStaffAsync();
        await using var _ = host;
        var beta = await host.Admin.CreateAsync(new CreateFlagRequest("beta", Targeting: Json(UsesStaff)));
        await host.Admin.ArchiveAsync("beta", beta.Version);
        await host.Admin.ArchiveSegmentAsync("staff", staff.Version);

        var snapshot = host.Services.GetRequiredService<Internal.FlagSnapshotManager>().Current;

        Assert.True(snapshot.Flags["beta"].Targeting!.Succeeded);
        Assert.True(snapshot.Segments["staff"].Archived);
        Assert.Equal(["beta"], snapshot.SegmentUsage["staff"]);
    }

    [Fact]
    public async Task A_flag_with_a_dangling_reference_is_a_parse_error_logged_once_and_other_flags_are_unaffected()
    {
        var store = new InMemoryFlagStore();
        await store.TryCreateAsync("dangling", FlagDocument.Boolean(false, new FlagMetadata()) with { Targeting = Json(UsesStaff) });
        await store.TryCreateAsync("plain", FlagDocument.Boolean(true, new FlagMetadata()));

        await using var host = await FlagonTestHost.StartAsync(o => o.Store = store);

        var details = host.Flagon.GetBooleanDetails("dangling", false, Ada);
        Assert.Equal(ErrorCode.ParseError, details.ErrorCode);
        Assert.Contains("segment 'staff' does not exist", details.ErrorMessage);
        Assert.True(host.Flagon.IsEnabled("plain"));
        var warning = Assert.Single(host.Logs.Flagon(LogLevel.Warning), l => l.EventId.Id == 1103);
        Assert.Contains("dangling", warning.Message);
    }

    [Fact]
    public async Task Segment_usage_lists_the_flags_that_reference_it_from_the_store()
    {
        var (host, _) = await HostWithStaffAsync();
        await using var __ = host;
        await host.Admin.CreateAsync(new CreateFlagRequest("zeta", Targeting: Json(UsesStaff)));
        var alpha = await host.Admin.CreateAsync(new CreateFlagRequest("alpha", Targeting: Json("""{"if":[{"or":[{"$ref":"staff"},false]},"on",null]}""")));
        await host.Admin.CreateAsync(new CreateFlagRequest("unrelated"));
        await host.Admin.ArchiveAsync("alpha", alpha.Version);

        var usage = await host.Admin.GetSegmentUsageAsync("staff");

        Assert.Equal([new SegmentReference("alpha", true), new SegmentReference("zeta", false)], usage);
    }

    [Fact]
    public async Task Every_segment_write_is_audited_newest_first_with_its_actor()
    {
        var (host, staff) = await HostWithStaffAsync();
        await using var _ = host;
        using (FlagonActor.Use("bob"))
        {
            staff = await host.Admin.SetSegmentRuleAsync("staff", Json("""{"==":[1,1]}"""), staff.Version);
        }

        var history = await host.Admin.GetSegmentAuditHistoryAsync("staff");

        Assert.Equal([FlagAuditAction.Updated, FlagAuditAction.Created], history.Select(e => e.Action));
        Assert.Equal("bob", history[0].Actor);
        Assert.Equal("TestApp", history[0].ApplicationName);
        Assert.Equal(StaffRule, history[0].Before!.Rule.GetRawText());
        Assert.Equal("""{"==":[1,1]}""", history[0].After!.Rule.GetRawText());
        Assert.Equal("bob", staff.Document.Metadata.UpdatedBy);
    }

    [Fact]
    public async Task Without_segment_support_segment_calls_are_not_supported_and_a_ref_says_why()
    {
        var store = OutageFlagStore.Wrap();
        await using var host = await FlagonTestHost.StartAsync(o => o.Store = store.Store);

        Assert.False(host.Admin.SupportsSegments);
        await Assert.ThrowsAsync<NotSupportedException>(() => host.Admin.ListSegmentsAsync());
        await Assert.ThrowsAsync<NotSupportedException>(() => host.Admin.CreateSegmentAsync(new CreateSegmentRequest("staff", Json(StaffRule))));
        var ex = await Assert.ThrowsAsync<FlagTargetingException>(() => host.Admin.CreateAsync(new CreateFlagRequest("beta", Targeting: Json(UsesStaff))));
        Assert.Equal("$ref", ex.Operator);
        Assert.Contains("supports segments", ex.Error);
    }

    [Fact]
    public async Task A_segment_defined_in_code_is_seeded_before_flags_so_a_defined_flag_can_reference_it()
    {
        await using var host = await FlagonTestHost.StartAsync(o => o.Flagon = f => f
            .Define("beta", targeting: UsesStaff)
            .DefineSegment("staff", StaffRule, "Employees", ["internal"]));

        var staff = await host.Admin.GetSegmentAsync("staff");
        Assert.Equal(FlagSource.Code, staff!.Document.Metadata.Source);
        Assert.Equal("Employees", staff.Document.Metadata.Description);
        Assert.True(host.Flagon.IsEnabled("beta", context: Ada));
        Assert.False(host.Flagon.IsEnabled("beta", context: Bob));
    }

    [Fact]
    public async Task A_segment_defined_in_code_syncs_its_metadata_but_never_overwrites_an_existing_rule()
    {
        var store = new InMemoryFlagStore();
        await using (var first = await FlagonTestHost.StartAsync(o =>
        {
            o.Store = store;
            o.Flagon = f => f.DefineSegment("staff", StaffRule, "v1");
        }))
        {
            var staff = (await first.Admin.GetSegmentAsync("staff"))!;
            await first.Admin.SetSegmentRuleAsync("staff", Json("""{"==":[{"var":"team"},"staff"]}"""), staff.Version);
        }

        await using var second = await FlagonTestHost.StartAsync(o =>
        {
            o.Store = store;
            o.Flagon = f => f.DefineSegment("staff", StaffRule, "v2", ["internal"]);
        });

        var synced = (await second.Admin.GetSegmentAsync("staff"))!;
        Assert.Equal("v2", synced.Document.Metadata.Description);
        Assert.Equal(["internal"], synced.Document.Metadata.Tags);
        Assert.Equal("""{"==":[{"var":"team"},"staff"]}""", synced.Document.Rule.GetRawText());
    }

    [Theory]
    [InlineData("""{"if":[{"$ref":"other"},true,false]}""", "$ref")]
    [InlineData("""{"unknown_op":[1]}""", "unknown_op")]
    public async Task A_segment_defined_in_code_with_an_invalid_rule_fails_startup_before_anything_is_written(string rule, string op)
    {
        var store = new InMemoryFlagStore();

        var ex = await Assert.ThrowsAsync<SegmentRuleException>(() => FlagonTestHost.StartAsync(o =>
        {
            o.Store = store;
            o.Flagon = f => f.DefineSegment("staff", rule);
        }));

        Assert.Equal(op, ex.Operator);
        Assert.Null(await store.GetSegmentAsync("staff"));
    }

    [Fact]
    public async Task A_flag_defined_in_code_referencing_an_unknown_segment_fails_startup()
    {
        var store = new InMemoryFlagStore();

        var ex = await Assert.ThrowsAsync<FlagTargetingException>(() => FlagonTestHost.StartAsync(o =>
        {
            o.Store = store;
            o.Flagon = f => f.Define("beta", targeting: UsesStaff);
        }));

        Assert.Equal("segment 'staff' does not exist", ex.Error);
        Assert.Null(await store.GetAsync("beta"));
    }

    [Fact]
    public async Task A_flag_defined_in_code_may_reference_a_segment_an_admin_created()
    {
        var store = new InMemoryFlagStore();
        await store.TryCreateSegmentAsync("staff", new SegmentDocument(Json(StaffRule), new FlagMetadata()));

        await using var host = await FlagonTestHost.StartAsync(o =>
        {
            o.Store = store;
            o.Flagon = f => f.Define("beta", targeting: UsesStaff);
        });

        Assert.True(host.Flagon.IsEnabled("beta", context: Ada));
    }

    [Fact]
    public void Defining_a_segment_checks_its_key_tags_and_json_and_rejects_duplicates()
    {
        var options = new FlagonOptions().DefineSegment("staff", StaffRule);

        Assert.Throws<FlagValidationException>(() => options.DefineSegment("bad key", StaffRule));
        Assert.Throws<FlagValidationException>(() => options.DefineSegment("ok", StaffRule, tags: [" padded"]));
        Assert.Throws<FlagValidationException>(() => options.DefineSegment("ok", "{ not json"));
        Assert.Throws<ArgumentException>(() => options.DefineSegment("staff", StaffRule));
    }

    [Fact]
    public async Task Defining_a_segment_needs_a_store_that_supports_segments()
    {
        var store = OutageFlagStore.Wrap();

        await Assert.ThrowsAsync<NotSupportedException>(() => FlagonTestHost.StartAsync(o =>
        {
            o.Store = store.Store;
            o.Flagon = f => f.DefineSegment("staff", StaffRule);
        }));
    }

    /// <summary>The default engine with a cap on the rule's JSON length, standing in for a compiler limit an inlined rule can exceed.</summary>
    private sealed class LimitedCompiler(int maxLength) : ITargetingCompiler
    {
        private readonly JsonLogicTargetingCompiler _inner = new();

        public TargetingCompilation Compile(string flagKey, JsonElement targeting) =>
            targeting.GetRawText().Length > maxLength
                ? TargetingCompilation.Failure($"rule too long ({targeting.GetRawText().Length} > {maxLength} characters)", "")
                : _inner.Compile(flagKey, targeting);
    }
}
