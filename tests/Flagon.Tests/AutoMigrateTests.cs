using Flagon.Storage;
using Flagon.Tests.Support;
using Microsoft.Extensions.Logging;

namespace Flagon.Tests;

public class AutoMigrateTests
{
    [Fact]
    public async Task Store_migrations_are_applied_once_on_startup_by_default()
    {
        var store = OutageFlagStore.Wrap();
        await using var host = await FlagonTestHost.StartAsync(o => o.Store = store.Store);

        await host.AdvanceToNextRefreshAsync();

        Assert.Equal(1, store.CallCount(nameof(IFlagStore.ApplyMigrationsAsync)));
    }

    [Fact]
    public async Task With_auto_migrate_off_store_migrations_are_never_applied()
    {
        var store = OutageFlagStore.Wrap();
        await using var host = await FlagonTestHost.StartAsync(o => { o.Store = store.Store; o.Flagon = f => f.AutoMigrate = false; });

        await host.AdvanceToNextRefreshAsync();

        Assert.Equal(0, store.CallCount(nameof(IFlagStore.ApplyMigrationsAsync)));
    }

    [Fact]
    public async Task Migrations_that_fail_at_startup_are_retried_on_the_next_refresh_and_flags_then_load()
    {
        var store = OutageFlagStore.Wrap(isDown: true);
        await store.Inner.TryCreateAsync("ready", FlagDocument.Boolean(true, new FlagMetadata()));
        await using var host = await FlagonTestHost.StartAsync(o => o.Store = store.Store);

        Assert.False(host.Flagon.IsEnabled("ready"));
        Assert.Contains(host.Logs.Flagon(LogLevel.Warning), e => e.EventId.Id == 1105);

        store.IsDown = false;
        await host.AdvanceToNextRefreshAsync();

        Assert.True(host.Flagon.IsEnabled("ready"));
        await host.AdvanceToNextRefreshAsync();
        Assert.Equal(2, store.CallCount(nameof(IFlagStore.ApplyMigrationsAsync)));
    }

    [Fact]
    public async Task With_fail_fast_a_migration_failure_stops_startup()
    {
        var store = OutageFlagStore.Wrap(isDown: true);

        var error = await Assert.ThrowsAsync<FlagonException>(() =>
            FlagonTestHost.StartAsync(o => { o.Store = store.Store; o.Flagon = f => f.FailFastOnStartup = true; }));

        Assert.IsType<StoreOutageException>(error.InnerException);
    }
}
