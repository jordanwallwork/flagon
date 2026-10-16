using Flagon.Storage;
using Flagon.Tests.Support;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;

namespace Flagon.Tests;

public class RefreshTests
{
    [Fact]
    public async Task A_flag_toggled_by_another_app_is_seen_by_every_app_after_one_refresh_interval()
    {
        var store = new InMemoryFlagStore();
        var time = new FakeTimeProvider(FlagonTestHost.Epoch);
        await using var appA = await FlagonTestHost.StartAsync(o => { o.Store = store; o.Time = time; });
        await using var appB = await FlagonTestHost.StartAsync(o => { o.Store = store; o.Time = time; });

        var flag = await appA.Admin.CreateAsync(new CreateFlagRequest("shared", Enabled: false));
        Assert.True(appA.Flagon.GetBooleanDetails("shared").ErrorCode == ErrorCode.None);
        Assert.Equal(ErrorCode.FlagNotFound, appB.Flagon.GetBooleanDetails("shared").ErrorCode);

        await FlagonTestHost.AdvanceToNextRefreshAsync(appA, appB);
        Assert.False(appB.Flagon.IsEnabled("shared", defaultValue: true));

        await appA.Admin.SetEnabledAsync("shared", true, flag.Version);
        Assert.False(appB.Flagon.IsEnabled("shared"));

        await FlagonTestHost.AdvanceToNextRefreshAsync(appA, appB);
        Assert.True(appA.Flagon.IsEnabled("shared"));
        Assert.True(appB.Flagon.IsEnabled("shared"));
    }

    [Fact]
    public async Task Polling_checks_the_change_version_each_interval_and_reloads_only_when_it_changed()
    {
        var store = OutageFlagStore.Wrap();
        await using var host = await FlagonTestHost.StartAsync(o => o.Store = store.Store);
        var loadsAfterStartup = store.CallCount(nameof(IFlagStore.LoadAllAsync));
        var versionChecksAfterStartup = store.CallCount(nameof(IFlagStore.GetChangeVersionAsync));

        await host.AdvanceToNextRefreshAsync();
        await host.AdvanceToNextRefreshAsync();

        Assert.Equal(versionChecksAfterStartup + 2, store.CallCount(nameof(IFlagStore.GetChangeVersionAsync)));
        Assert.Equal(loadsAfterStartup, store.CallCount(nameof(IFlagStore.LoadAllAsync)));

        await store.Inner.TryCreateAsync("from-elsewhere", FlagDocument.Boolean(true, new FlagMetadata()));
        await host.AdvanceToNextRefreshAsync();

        Assert.Equal(loadsAfterStartup + 1, store.CallCount(nameof(IFlagStore.LoadAllAsync)));
        Assert.True(host.Flagon.IsEnabled("from-elsewhere"));
    }

    [Fact]
    public async Task Nothing_changes_before_the_configured_refresh_interval_elapses()
    {
        var store = new InMemoryFlagStore();
        await using var host = await FlagonTestHost.StartAsync(o =>
        {
            o.Store = store;
            o.Flagon = f => f.RefreshInterval = TimeSpan.FromMinutes(1);
        });
        await store.TryCreateAsync("late", FlagDocument.Boolean(true, new FlagMetadata()));

        host.Time.Advance(TimeSpan.FromSeconds(59));
        Assert.Equal(ErrorCode.FlagNotFound, host.Flagon.GetBooleanDetails("late").ErrorCode);

        host.Time.Advance(TimeSpan.FromSeconds(1));
        await host.WaitForRefreshAttemptAsync();
        Assert.True(host.Flagon.IsEnabled("late"));
    }

    [Fact]
    public void The_refresh_interval_defaults_to_ten_seconds_and_must_be_positive()
    {
        var options = new FlagonOptions();

        Assert.Equal(TimeSpan.FromSeconds(10), options.RefreshInterval);
        Assert.Throws<ArgumentOutOfRangeException>(() => options.RefreshInterval = TimeSpan.Zero);
    }

    [Fact]
    public async Task When_the_store_goes_down_the_last_known_good_flags_keep_being_served_and_the_failure_is_logged()
    {
        var store = OutageFlagStore.Wrap();
        await using var host = await FlagonTestHost.StartAsync(o => o.Store = store.Store);
        await host.Admin.CreateAsync(new CreateFlagRequest("checkout", Enabled: true));

        store.IsDown = true;
        await host.AdvanceToNextRefreshAsync();

        Assert.True(host.Flagon.IsEnabled("checkout"));
        Assert.Equal(EvaluationReason.Static, host.Flagon.GetBooleanDetails("checkout").Reason);
        var warning = Assert.Single(host.Logs.Flagon(LogLevel.Warning));
        Assert.Equal(1101, warning.EventId.Id);
        Assert.IsType<StoreOutageException>(warning.Exception);
    }

    [Fact]
    public async Task Refresh_status_reports_the_store_unreachable_during_an_outage_and_recovers_afterwards()
    {
        var store = OutageFlagStore.Wrap();
        await using var host = await FlagonTestHost.StartAsync(o => o.Store = store.Store);
        await host.Admin.CreateAsync(new CreateFlagRequest("checkout"));

        var healthy = host.Status;
        Assert.True(healthy.IsLoaded);
        Assert.True(healthy.IsStoreReachable);
        Assert.Equal(FlagonTestHost.Epoch, healthy.LastRefreshedAt);
        Assert.Equal(TimeSpan.FromSeconds(10), healthy.RefreshInterval);
        Assert.Equal(1, healthy.Version);

        store.IsDown = true;
        await host.AdvanceToNextRefreshAsync();
        await host.AdvanceToNextRefreshAsync();

        var down = host.Status;
        Assert.False(down.IsStoreReachable);
        Assert.True(down.IsLoaded);
        Assert.Equal(2, down.ConsecutiveFailures);
        Assert.IsType<StoreOutageException>(down.LastError);
        Assert.Equal(FlagonTestHost.Epoch, down.LastRefreshedAt);
        Assert.Equal(FlagonTestHost.Epoch.AddSeconds(20), down.LastAttemptAt);
        Assert.Equal(TimeSpan.FromSeconds(20), down.Age(host.Time.GetUtcNow()));

        store.IsDown = false;
        await host.AdvanceToNextRefreshAsync();

        var recovered = host.Status;
        Assert.True(recovered.IsStoreReachable);
        Assert.Equal(0, recovered.ConsecutiveFailures);
        Assert.Null(recovered.LastError);
        Assert.Equal(FlagonTestHost.Epoch.AddSeconds(30), recovered.LastRefreshedAt);
        Assert.Contains(host.Logs.Flagon(LogLevel.Information), l => l.EventId.Id == 1102);
    }

    [Fact]
    public async Task When_the_store_is_unavailable_at_startup_the_app_starts_with_code_defaults_and_keeps_retrying()
    {
        var store = OutageFlagStore.Wrap(isDown: true);
        await store.Inner.TryCreateAsync("checkout", FlagDocument.Boolean(true, new FlagMetadata()));

        await using var host = await FlagonTestHost.StartAsync(o => o.Store = store.Store);

        var details = host.Flagon.GetBooleanDetails("checkout", defaultValue: false);
        Assert.False(details.Value);
        Assert.Equal(ErrorCode.FlagNotFound, details.ErrorCode);
        Assert.False(host.Status.IsLoaded);
        Assert.False(host.Status.IsStoreReachable);
        Assert.Contains(host.Logs.Flagon(LogLevel.Warning), l => l.EventId.Id == 1103);

        await host.AdvanceToNextRefreshAsync();
        Assert.False(host.Flagon.IsEnabled("checkout"));

        store.IsDown = false;
        await host.AdvanceToNextRefreshAsync();
        Assert.True(host.Flagon.IsEnabled("checkout"));
        Assert.True(host.Status.IsLoaded);
    }

    [Fact]
    public async Task With_fail_fast_enabled_the_app_refuses_to_start_when_the_store_is_unavailable()
    {
        var store = OutageFlagStore.Wrap(isDown: true);

        var ex = await Assert.ThrowsAsync<FlagonException>(() => FlagonTestHost.StartAsync(o =>
        {
            o.Store = store.Store;
            o.Flagon = f => f.FailFastOnStartup = true;
        }));

        Assert.IsType<StoreOutageException>(ex.InnerException);
    }

    [Fact]
    public async Task With_fail_fast_enabled_the_app_starts_normally_when_the_store_is_available()
    {
        await using var host = await FlagonTestHost.StartAsync(o => o.Flagon = f => f.FailFastOnStartup = true);

        Assert.True(host.Status.IsLoaded);
    }
}
