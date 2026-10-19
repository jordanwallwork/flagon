using Jorrk.Flagon.Storage;
using Jorrk.Flagon.Tests.Support;

namespace Jorrk.Flagon.Tests;

public class MigrationTests
{
    [Fact]
    public async Task The_store_schema_is_migrated_on_startup_by_default()
    {
        var store = OutageFlagStore.Wrap();

        await using var host = await FlagonTestHost.StartAsync(o => o.Store = store.Store);

        Assert.Equal(1, store.CallCount(nameof(IFlagStore.ApplyMigrationsAsync)));
    }

    [Fact]
    public async Task Auto_migration_can_be_turned_off()
    {
        var store = OutageFlagStore.Wrap();

        await using var host = await FlagonTestHost.StartAsync(o =>
        {
            o.Store = store.Store;
            o.Flagon = f => f.AutoMigrate = false;
        });

        Assert.Equal(0, store.CallCount(nameof(IFlagStore.ApplyMigrationsAsync)));
    }

    [Fact]
    public async Task A_migration_that_fails_at_startup_is_retried_until_it_succeeds()
    {
        var store = OutageFlagStore.Wrap(isDown: true);
        await using var host = await FlagonTestHost.StartAsync(o => o.Store = store.Store);
        Assert.False(host.Status.IsLoaded);

        store.IsDown = false;
        await host.AdvanceToNextRefreshAsync();
        await host.AdvanceToNextRefreshAsync();

        Assert.Equal(2, store.CallCount(nameof(IFlagStore.ApplyMigrationsAsync)));
        Assert.True(host.Status.IsLoaded);
    }

    [Fact]
    public async Task A_migration_failure_stops_startup_when_fail_fast_is_on()
    {
        var store = OutageFlagStore.Wrap(isDown: true);

        await Assert.ThrowsAsync<FlagonException>(() => FlagonTestHost.StartAsync(o =>
        {
            o.Store = store.Store;
            o.Flagon = f => f.FailFastOnStartup = true;
        }));
    }
}
