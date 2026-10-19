using Jorrk.Flagon.Storage;
using Jorrk.Flagon.Tests.Support;

namespace Jorrk.Flagon.Tests;

public class InMemoryChangeNotifierTests
{
    private static readonly FlagDocument Document = FlagDocument.Boolean(true, new FlagMetadata());

    [Fact]
    public async Task Every_write_that_moves_the_change_version_signals_the_notifier()
    {
        var store = new InMemoryFlagStore();
        var signals = await ListenAsync(store);

        var created = await store.TryCreateAsync("checkout", Document);
        Assert.Equal(1, signals.Count);

        var updated = await store.UpdateAsync("checkout", Document, created!.Version);
        Assert.Equal(2, signals.Count);

        var archived = await store.ArchiveAsync("checkout", updated.Version);
        Assert.Equal(3, signals.Count);

        var restored = await store.RestoreAsync("checkout", archived.Version);
        Assert.Equal(4, signals.Count);

        var rearchived = await store.ArchiveAsync("checkout", restored.Version);
        Assert.Equal(5, signals.Count);

        await store.PurgeAsync("checkout", rearchived.Version);
        Assert.Equal(6, signals.Count);
    }

    [Fact]
    public async Task Usage_writes_do_not_signal_because_they_do_not_move_the_change_version()
    {
        var store = new InMemoryFlagStore();
        await store.TryCreateAsync("checkout", Document);
        var signals = await ListenAsync(store);

        await store.UpsertUsageAsync([new FlagUsage("checkout", "Web", FlagonTestHost.Epoch)]);

        Assert.Equal(0, signals.Count);
    }

    [Fact]
    public async Task Rejected_writes_do_not_signal()
    {
        var store = new InMemoryFlagStore();
        var created = await store.TryCreateAsync("checkout", Document);
        var signals = await ListenAsync(store);

        Assert.Null(await store.TryCreateAsync("checkout", Document));
        await Assert.ThrowsAsync<FlagVersionConflictException>(() => store.UpdateAsync("checkout", Document, created!.Version + 99));

        Assert.Equal(0, signals.Count);
    }

    [Fact]
    public async Task Cancelling_the_token_disconnects_the_notifier_and_no_callbacks_run_afterwards()
    {
        var store = new InMemoryFlagStore();
        using var cts = new CancellationTokenSource();
        var signals = await ListenAsync(store, cts.Token);
        Assert.True(((IFlagChangeNotifier)store).IsConnected);

        await cts.CancelAsync();
        Assert.False(((IFlagChangeNotifier)store).IsConnected);

        await store.TryCreateAsync("checkout", Document);

        Assert.Equal(0, signals.Count);
    }

    [Fact]
    public async Task Every_listener_on_a_shared_store_hears_the_writes()
    {
        var store = new InMemoryFlagStore();
        var first = await ListenAsync(store);
        var second = await ListenAsync(store);

        await store.TryCreateAsync("checkout", Document);

        Assert.Equal(1, first.Count);
        Assert.Equal(1, second.Count);
    }

    private static async Task<Signals> ListenAsync(InMemoryFlagStore store, CancellationToken cancellationToken = default)
    {
        var signals = new Signals();
        await ((IFlagChangeNotifier)store).StartAsync(_ => Interlocked.Increment(ref signals.Count), cancellationToken);
        return signals;
    }

    private sealed class Signals
    {
        public int Count;
    }
}
