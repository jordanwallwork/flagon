using System.Threading.Channels;
using Xunit;

namespace Jorrk.Flagon.Storage.Conformance;

public abstract partial class FlagStoreConformanceTests
{
    private static readonly TimeSpan SignalTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan QuietPeriod = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// Returns the <see cref="IFlagChangeNotifier"/> to exercise for <paramref name="store"/>, or null to skip the notification
    /// cases. Defaults to the store itself when it implements the capability, so stores without it skip automatically.
    /// </summary>
    protected virtual IFlagChangeNotifier? AsNotifier(IFlagStore store) => store as IFlagChangeNotifier;

    /// <summary>
    /// Creates a second store instance over the same storage as <paramref name="store"/> (for a database provider: the same
    /// database and schema). Returns null, the default, when instances cannot share storage, which skips the cross-instance case.
    /// The caller disposes the instance; this does not drop storage.
    /// </summary>
    protected virtual Task<IFlagStore?> CreateSecondInstanceAsync(IFlagStore store) => Task.FromResult<IFlagStore?>(null);

    /// <summary>Conformance: create, update, archive, restore and purge each signal the notifier within five seconds.</summary>
    [Fact]
    public Task Each_lifecycle_write_signals_the_notifier() => WithNotifierAsync(async (store, notifier) =>
    {
        await using var listener = await SignalListener.StartAsync(notifier);

        var created = await SignalAfter(listener, "create", () => CreateAsync(store, "checkout"));
        var updated = await SignalAfter(listener, "update", () => store.UpdateAsync("checkout", Flag(enabled: true), created.Version));
        var archived = await SignalAfter(listener, "archive", () => store.ArchiveAsync("checkout", updated.Version));
        var restored = await SignalAfter(listener, "restore", () => store.RestoreAsync("checkout", archived.Version));
        var rearchived = await SignalAfter(listener, "re-archive", () => store.ArchiveAsync("checkout", restored.Version));
        await SignalAfter(listener, "purge", () => store.PurgeAsync("checkout", rearchived.Version));
    });

    /// <summary>Conformance: a write made through a second store instance over the same storage signals the first instance.</summary>
    [Fact]
    public Task A_write_through_a_second_instance_signals_the_first() => WithNotifierAsync(async (store, notifier) =>
    {
        var other = await CreateSecondInstanceAsync(store);
        if (other is null)
        {
            return;
        }

        try
        {
            await using var listener = await SignalListener.StartAsync(notifier);
            var created = await SignalAfter(listener, "create via second instance", () => CreateAsync(other, "checkout"));
            await SignalAfter(listener, "update via second instance", () => other.UpdateAsync("checkout", Flag(enabled: true), created.Version));
        }
        finally
        {
            await DisposeInstanceAsync(other);
        }
    });

    /// <summary>Conformance: usage upserts do not signal the notifier.</summary>
    [Fact]
    public Task Usage_upserts_do_not_signal() => WithNotifierAsync(async (store, notifier) =>
    {
        await CreateAsync(store, "checkout");
        await using var listener = await SignalListener.StartAsync(notifier);

        await store.UpsertUsageAsync([new FlagUsage("checkout", "AppA", Epoch, True)]);

        await Task.Delay(QuietPeriod);
        Assert.Equal(0, listener.Count);
    });

    /// <summary>Conformance: once the token passed to StartAsync is cancelled, the notifier invokes no further callbacks.</summary>
    [Fact]
    public Task No_callbacks_run_after_the_notifier_is_stopped() => WithNotifierAsync(async (store, notifier) =>
    {
        var created = await CreateAsync(store, "checkout");
        var listener = new SignalListener();
        using var stop = new CancellationTokenSource();
        await notifier.StartAsync(listener.OnChanged, stop.Token);

        await stop.CancelAsync();
        await WaitUntilAsync(() => !notifier.IsConnected, "the notifier to report disconnected");
        var stoppedAt = listener.Count;

        await store.UpdateAsync("checkout", Flag(enabled: true), created.Version);

        await Task.Delay(QuietPeriod);
        Assert.Equal(stoppedAt, listener.Count);
    });

    /// <summary>
    /// Conformance: after N concurrent writes, the change version read after a signal converges to the final version. This
    /// asserts convergence, not the number of signals: signals may be coalesced or spurious.
    /// </summary>
    [Fact]
    public Task Under_concurrent_writes_the_change_version_converges_to_the_final_version() => WithNotifierAsync(async (store, notifier) =>
    {
        await using var listener = await SignalListener.StartAsync(notifier);

        await Task.WhenAll(Enumerable.Range(1, 10).Select(i => Task.Run(() => CreateAsync(store, $"flag-{i}"))));
        var final = await store.GetChangeVersionAsync();

        using var timeout = new CancellationTokenSource(SignalTimeout);
        while (true)
        {
            await listener.Signals.ReadAsync(timeout.Token).ConfigureAwait(false);
            if (await store.GetChangeVersionAsync() == final)
            {
                break;
            }
        }
    });

    /// <summary>
    /// Conformance: rejected writes leave the change version unchanged. Whether they signal is not required, so a commit-free
    /// period is only checked against the version, not against the signal count.
    /// </summary>
    [Fact]
    public Task Rejected_writes_leave_the_notifier_contract_intact() => WithNotifierAsync(async (store, notifier) =>
    {
        var created = await CreateAsync(store, "checkout");
        var version = await store.GetChangeVersionAsync();
        await using var listener = await SignalListener.StartAsync(notifier);

        Assert.Null(await store.TryCreateAsync("checkout", Flag()));
        await Assert.ThrowsAsync<FlagVersionConflictException>(() => store.UpdateAsync("checkout", Flag(), created.Version + 99));
        await Assert.ThrowsAsync<FlagValidationException>(() => store.PurgeAsync("checkout", created.Version));

        await Task.Delay(QuietPeriod);
        Assert.Equal(version, await store.GetChangeVersionAsync());
    });

    private async Task WithNotifierAsync(Func<IFlagStore, IFlagChangeNotifier, Task> test) => await WithStoreAsync(store =>
    {
        var notifier = AsNotifier(store);
        return notifier is null ? Task.CompletedTask : test(store, notifier);
    });

    private static async Task DisposeInstanceAsync(IFlagStore store)
    {
        switch (store)
        {
            case IAsyncDisposable asyncDisposable:
                await asyncDisposable.DisposeAsync().ConfigureAwait(false);
                break;
            case IDisposable disposable:
                disposable.Dispose();
                break;
        }
    }

    private static async Task<T> SignalAfter<T>(SignalListener listener, string step, Func<Task<T>> write)
    {
        var before = listener.Count;
        var result = await write().ConfigureAwait(false);
        await listener.AwaitAboveAsync(before, step).ConfigureAwait(false);
        return result;
    }

    private static async Task WaitUntilAsync(Func<bool> condition, string description)
    {
        var deadline = DateTimeOffset.UtcNow + SignalTimeout;
        while (!condition())
        {
            if (DateTimeOffset.UtcNow > deadline)
            {
                Assert.Fail($"Timed out after {SignalTimeout.TotalSeconds}s waiting for {description}.");
            }

            await Task.Delay(10).ConfigureAwait(false);
        }
    }

    /// <summary>Counts signals and queues them for the test to read. Stopped on dispose.</summary>
    private sealed class SignalListener : IAsyncDisposable
    {
        private readonly CancellationTokenSource _stop = new();
        private readonly Channel<bool> _signals = Channel.CreateUnbounded<bool>();
        private int _count;

        public int Count => Volatile.Read(ref _count);

        public ChannelReader<bool> Signals => _signals.Reader;

        public void OnChanged()
        {
            Interlocked.Increment(ref _count);
            _signals.Writer.TryWrite(true);
        }

        public static async Task<SignalListener> StartAsync(IFlagChangeNotifier notifier)
        {
            var listener = new SignalListener();
            await notifier.StartAsync(listener.OnChanged, listener._stop.Token).ConfigureAwait(false);
            return listener;
        }

        public Task AwaitAboveAsync(int previous, string step) =>
            WaitUntilAsync(() => Count > previous, $"a signal after {step}");

        public async ValueTask DisposeAsync()
        {
            await _stop.CancelAsync().ConfigureAwait(false);
            _stop.Dispose();
        }
    }
}
