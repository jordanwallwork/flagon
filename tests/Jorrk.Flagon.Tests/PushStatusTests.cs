using Jorrk.Flagon.Storage;
using Jorrk.Flagon.Tests.Support;

namespace Jorrk.Flagon.Tests;

public class PushStatusTests
{
    private static readonly FlagDocument Document = FlagDocument.Boolean(true, new FlagMetadata());

    [Fact]
    public async Task A_store_without_the_notifier_capability_reports_no_push_state()
    {
        await using var host = await FlagonTestHost.StartAsync(o => o.Store = OutageFlagStore.Wrap().Store);

        Assert.Null(host.Status.IsPushConnected);
        Assert.Null(host.Status.LastPushAt);
    }

    [Fact]
    public async Task With_push_refresh_off_a_notifier_store_reports_no_push_state()
    {
        var store = new InMemoryFlagStore();
        await using var host = await FlagonTestHost.StartAsync(o =>
        {
            o.Store = store;
            o.Flagon = f => f.PushRefresh = false;
        });

        Assert.Null(host.Status.IsPushConnected);

        await store.TryCreateAsync("checkout", Document);
        Assert.Null(host.Status.LastPushAt);
    }

    [Fact]
    public async Task A_connected_in_memory_notifier_reports_connected_and_the_time_of_the_last_push()
    {
        var store = new InMemoryFlagStore();
        await using var host = await FlagonTestHost.StartAsync(o => o.Store = store);
        Assert.Equal(true, host.Status.IsPushConnected);
        Assert.Null(host.Status.LastPushAt);

        host.Time.Advance(TimeSpan.FromSeconds(5));
        await store.TryCreateAsync("checkout", Document);
        Assert.Equal(host.Time.GetUtcNow(), host.Status.LastPushAt);

        host.Time.Advance(TimeSpan.FromSeconds(5));
        await store.UpdateAsync("checkout", Document, expectedVersion: 1);
        Assert.Equal(host.Time.GetUtcNow(), host.Status.LastPushAt);
    }

    [Fact]
    public async Task Usage_writes_are_not_pushes_so_they_leave_the_last_push_time_alone()
    {
        var store = new InMemoryFlagStore();
        await using var host = await FlagonTestHost.StartAsync(o => o.Store = store);

        await store.UpsertUsageAsync([new FlagUsage("checkout", "Web", FlagonTestHost.Epoch)]);

        Assert.Null(host.Status.LastPushAt);
    }

    [Fact]
    public async Task A_disconnected_notifier_reports_disconnected_and_leaves_the_snapshot_healthy()
    {
        var store = new FakeNotifyingFlagStore { Connected = false };
        await using var host = await FlagonTestHost.StartAsync(o => o.Store = store);

        Assert.Equal(false, host.Status.IsPushConnected);
        Assert.Null(host.Status.LastPushAt);
        Assert.True(host.Status.IsLoaded);
        Assert.True(host.Status.IsStoreReachable);
    }

    [Fact]
    public async Task A_notifier_that_drops_its_connection_is_reported_as_disconnected()
    {
        var store = new FakeNotifyingFlagStore { Connected = true };
        await using var host = await FlagonTestHost.StartAsync(o => o.Store = store);
        Assert.Equal(true, host.Status.IsPushConnected);

        store.Connected = false;

        Assert.Equal(false, host.Status.IsPushConnected);
    }

    [Fact]
    public async Task A_signal_from_the_notifier_sets_the_last_push_time_even_when_it_is_coalesced()
    {
        var store = new FakeNotifyingFlagStore { Connected = true };
        await using var host = await FlagonTestHost.StartAsync(o => o.Store = store);

        host.Time.Advance(TimeSpan.FromSeconds(2));
        store.Signal();
        store.Signal();

        Assert.Equal(host.Time.GetUtcNow(), host.Status.LastPushAt);
    }
}
