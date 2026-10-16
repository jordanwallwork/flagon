using Flagon.Storage;
using Flagon.Tests.Support;
using Microsoft.Extensions.Time.Testing;

namespace Flagon.Tests;

public class ArchiveTests
{
    [Fact]
    public async Task An_archived_flag_returns_the_callers_default_with_reason_archived()
    {
        await using var host = await FlagonTestHost.StartAsync();
        var flag = await host.Admin.CreateAsync(new CreateFlagRequest("old-feature", Enabled: true));

        var archived = await host.Admin.ArchiveAsync("old-feature", flag.Version);

        Assert.True(archived.Archived);
        Assert.True(archived.Version > flag.Version);
        var details = host.Flagon.GetBooleanDetails("old-feature", defaultValue: false);
        Assert.False(details.Value);
        Assert.Null(details.Variant);
        Assert.Equal(EvaluationReason.Archived, details.Reason);
        Assert.Equal(ErrorCode.None, details.ErrorCode);
        Assert.True(host.Flagon.IsEnabled("old-feature", defaultValue: true));
    }

    [Fact]
    public async Task A_disabled_flag_that_is_archived_reports_archived_rather_than_disabled()
    {
        var store = new InMemoryFlagStore();
        var flag = await store.TryCreateAsync("legacy", FlagdJson.Deserialize("""{"state":"DISABLED","variants":{"on":true,"off":false},"defaultVariant":"on"}"""));
        await store.ArchiveAsync("legacy", flag!.Version);
        await using var host = await FlagonTestHost.StartAsync(o => o.Store = store);

        Assert.Equal(EvaluationReason.Archived, host.Flagon.GetBooleanDetails("legacy").Reason);
    }

    [Fact]
    public async Task Restoring_an_archived_flag_brings_back_its_previous_value()
    {
        await using var host = await FlagonTestHost.StartAsync();
        var flag = await host.Admin.CreateAsync(new CreateFlagRequest("comeback", Enabled: true, "Keep me", ["growth"]));
        flag = await host.Admin.ArchiveAsync("comeback", flag.Version);

        var restored = await host.Admin.RestoreAsync("comeback", flag.Version);

        Assert.False(restored.Archived);
        Assert.Equal("Keep me", restored.Document.Metadata.Description);
        var details = host.Flagon.GetBooleanDetails("comeback");
        Assert.True(details.Value);
        Assert.Equal(EvaluationReason.Static, details.Reason);
    }

    [Fact]
    public async Task Archived_flags_are_still_listed_and_readable_with_their_archived_marker()
    {
        await using var host = await FlagonTestHost.StartAsync();
        var flag = await host.Admin.CreateAsync(new CreateFlagRequest("shelved"));
        await host.Admin.ArchiveAsync("shelved", flag.Version);

        Assert.True((await host.Admin.GetAsync("shelved"))!.Archived);
        Assert.True(Assert.Single(await host.Admin.ListAsync()).Archived);
    }

    [Fact]
    public async Task Purging_an_archived_flag_removes_it_so_it_evaluates_as_not_found()
    {
        await using var host = await FlagonTestHost.StartAsync();
        var flag = await host.Admin.CreateAsync(new CreateFlagRequest("gone", Enabled: true));
        flag = await host.Admin.ArchiveAsync("gone", flag.Version);

        await host.Admin.PurgeAsync("gone", flag.Version);

        Assert.Null(await host.Admin.GetAsync("gone"));
        Assert.Empty(await host.Admin.ListAsync());
        var details = host.Flagon.GetBooleanDetails("gone", defaultValue: true);
        Assert.True(details.Value);
        Assert.Equal(ErrorCode.FlagNotFound, details.ErrorCode);
    }

    [Fact]
    public async Task A_purged_key_can_be_created_again()
    {
        await using var host = await FlagonTestHost.StartAsync();
        var flag = await host.Admin.CreateAsync(new CreateFlagRequest("reborn"));
        flag = await host.Admin.ArchiveAsync("reborn", flag.Version);
        await host.Admin.PurgeAsync("reborn", flag.Version);

        await host.Admin.CreateAsync(new CreateFlagRequest("reborn", Enabled: true));

        Assert.True(host.Flagon.IsEnabled("reborn"));
    }

    [Fact]
    public async Task Purging_a_flag_that_is_not_archived_is_rejected_and_the_flag_keeps_working()
    {
        await using var host = await FlagonTestHost.StartAsync();
        var flag = await host.Admin.CreateAsync(new CreateFlagRequest("live", Enabled: true));

        await Assert.ThrowsAsync<FlagValidationException>(() => host.Admin.PurgeAsync("live", flag.Version));

        Assert.Equal(flag, await host.Admin.GetAsync("live"));
        Assert.True(host.Flagon.IsEnabled("live"));
    }

    [Fact]
    public async Task Archiving_an_already_archived_flag_and_restoring_a_live_flag_are_rejected()
    {
        await using var host = await FlagonTestHost.StartAsync();
        var flag = await host.Admin.CreateAsync(new CreateFlagRequest("f"));

        await Assert.ThrowsAsync<FlagValidationException>(() => host.Admin.RestoreAsync("f", flag.Version));
        flag = await host.Admin.ArchiveAsync("f", flag.Version);
        await Assert.ThrowsAsync<FlagValidationException>(() => host.Admin.ArchiveAsync("f", flag.Version));
    }

    [Fact]
    public async Task Archive_restore_and_purge_of_a_missing_flag_fail_with_not_found()
    {
        await using var host = await FlagonTestHost.StartAsync();

        await Assert.ThrowsAsync<FlagNotFoundException>(() => host.Admin.ArchiveAsync("nope", 1));
        await Assert.ThrowsAsync<FlagNotFoundException>(() => host.Admin.RestoreAsync("nope", 1));
        await Assert.ThrowsAsync<FlagNotFoundException>(() => host.Admin.PurgeAsync("nope", 1));
    }

    [Fact]
    public async Task Archive_restore_and_purge_based_on_a_stale_version_are_rejected_with_a_conflict()
    {
        var store = new InMemoryFlagStore();
        await using var appA = await FlagonTestHost.StartAsync(o => o.Store = store);
        await using var appB = await FlagonTestHost.StartAsync(o => o.Store = store);
        var seenByB = await appA.Admin.CreateAsync(new CreateFlagRequest("contested", Enabled: true));

        var toggled = await appA.Admin.SetEnabledAsync("contested", false, seenByB.Version);
        var archiveConflict = await Assert.ThrowsAsync<FlagVersionConflictException>(() => appB.Admin.ArchiveAsync("contested", seenByB.Version));
        Assert.Equal(toggled.Version, archiveConflict.CurrentVersion);

        var archived = await appA.Admin.ArchiveAsync("contested", toggled.Version);
        var restoreConflict = await Assert.ThrowsAsync<FlagVersionConflictException>(() => appB.Admin.RestoreAsync("contested", toggled.Version));
        var purgeConflict = await Assert.ThrowsAsync<FlagVersionConflictException>(() => appB.Admin.PurgeAsync("contested", toggled.Version));
        Assert.Equal(archived.Version, restoreConflict.CurrentVersion);
        Assert.Equal(archived.Version, purgeConflict.CurrentVersion);
        Assert.True((await appB.Admin.GetAsync("contested"))!.Archived);
    }

    [Fact]
    public async Task A_flag_archived_restored_and_purged_by_another_app_is_observed_after_the_next_refresh()
    {
        var store = new InMemoryFlagStore();
        var time = new FakeTimeProvider(FlagonTestHost.Epoch);
        await using var appA = await FlagonTestHost.StartAsync(o => { o.Store = store; o.Time = time; });
        await using var appB = await FlagonTestHost.StartAsync(o => { o.Store = store; o.Time = time; });
        var flag = await appA.Admin.CreateAsync(new CreateFlagRequest("shared", Enabled: true));
        await FlagonTestHost.AdvanceToNextRefreshAsync(appA, appB);
        Assert.True(appB.Flagon.IsEnabled("shared"));

        flag = await appA.Admin.ArchiveAsync("shared", flag.Version);
        Assert.True(appB.Flagon.IsEnabled("shared"));
        await FlagonTestHost.AdvanceToNextRefreshAsync(appA, appB);
        Assert.Equal(EvaluationReason.Archived, appB.Flagon.GetBooleanDetails("shared").Reason);
        Assert.False(appB.Flagon.IsEnabled("shared"));

        flag = await appA.Admin.RestoreAsync("shared", flag.Version);
        await FlagonTestHost.AdvanceToNextRefreshAsync(appA, appB);
        Assert.True(appB.Flagon.IsEnabled("shared"));

        flag = await appA.Admin.ArchiveAsync("shared", flag.Version);
        await appA.Admin.PurgeAsync("shared", flag.Version);
        await FlagonTestHost.AdvanceToNextRefreshAsync(appA, appB);
        Assert.Equal(ErrorCode.FlagNotFound, appB.Flagon.GetBooleanDetails("shared").ErrorCode);
    }
}
