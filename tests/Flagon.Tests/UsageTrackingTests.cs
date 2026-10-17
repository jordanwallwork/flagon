using Flagon.Storage;
using Flagon.Tests.Support;

namespace Flagon.Tests;

public class UsageTrackingTests
{
    private static readonly TimeSpan FlushInterval = TimeSpan.FromSeconds(60);

    [Fact]
    public async Task An_admin_sees_when_each_app_last_evaluated_a_flag_and_the_default_it_supplied()
    {
        await using var host = await FlagonTestHost.StartAsync(o => o.ApplicationName = "Checkout");
        await host.Admin.CreateAsync(new CreateFlagRequest("new-checkout", Enabled: true));
        host.Time.Advance(TimeSpan.FromSeconds(5));
        var evaluatedAt = host.Time.GetUtcNow();

        host.Flagon.IsEnabled("new-checkout", defaultValue: true);
        host.Time.Advance(FlushInterval - TimeSpan.FromSeconds(5));

        var usage = await EventuallyAsync(() => host.Admin.GetUsageAsync("new-checkout"), u => u.Applications.Count > 0);
        var app = Assert.Single(usage.Applications);
        Assert.Equal("Checkout", app.ApplicationName);
        Assert.Equal(evaluatedAt, app.LastEvaluatedAt);
        Assert.True(app.ObservedDefault!.Value.GetBoolean());
        Assert.Equal(evaluatedAt, usage.LastEvaluatedAt);
        Assert.False(usage.HasDefaultMismatch);
    }

    [Fact]
    public async Task An_admin_is_warned_when_two_apps_evaluate_a_flag_with_different_code_defaults()
    {
        var store = new InMemoryFlagStore();
        await using (var web = await FlagonTestHost.StartAsync(o => { o.Store = store; o.ApplicationName = "Web"; }))
        await using (var worker = await FlagonTestHost.StartAsync(o => { o.Store = store; o.ApplicationName = "Worker"; }))
        {
            web.Flagon.IsEnabled("dark-mode", defaultValue: true);
            worker.Flagon.IsEnabled("dark-mode", defaultValue: false);
            web.Flagon.IsEnabled("search-v2", defaultValue: true);
            worker.Flagon.IsEnabled("search-v2", defaultValue: true);
        }

        await using var admin = await FlagonTestHost.StartAsync(o => o.Store = store);
        var darkMode = await admin.Admin.GetUsageAsync("dark-mode");
        Assert.True(darkMode.HasDefaultMismatch);
        Assert.Equal(["Web", "Worker"], darkMode.Applications.Select(a => a.ApplicationName));
        var all = await admin.Admin.ListUsageAsync();
        Assert.Equal(["dark-mode", "search-v2"], all.Select(r => r.Key));
        Assert.False(all[1].HasDefaultMismatch);
    }

    [Fact]
    public async Task Evaluations_are_written_in_one_batch_per_interval_carrying_the_latest_evaluation()
    {
        var outage = OutageFlagStore.Wrap();
        await using var host = await FlagonTestHost.StartAsync(o => o.Store = outage.Store);

        for (var i = 0; i < 1000; i++)
        {
            host.Flagon.IsEnabled("hot-path", defaultValue: i % 2 == 0);
        }
        host.Time.Advance(TimeSpan.FromSeconds(30));
        var lastEvaluatedAt = host.Time.GetUtcNow();
        host.Flagon.IsEnabled("hot-path", defaultValue: false);
        host.Time.Advance(TimeSpan.FromSeconds(29));
        Assert.Equal(0, outage.CallCount(nameof(IFlagStore.UpsertUsageAsync)));

        host.Time.Advance(TimeSpan.FromSeconds(1));

        var usage = await EventuallyAsync(() => host.Admin.GetUsageAsync("hot-path"), u => u.Applications.Count > 0);
        Assert.Equal(1, outage.CallCount(nameof(IFlagStore.UpsertUsageAsync)));
        var app = Assert.Single(usage.Applications);
        Assert.Equal(lastEvaluatedAt, app.LastEvaluatedAt);
        Assert.False(app.ObservedDefault!.Value.GetBoolean());
    }

    [Fact]
    public async Task Nothing_is_written_when_no_flag_was_evaluated_during_an_interval()
    {
        var outage = OutageFlagStore.Wrap();
        await using var host = await FlagonTestHost.StartAsync(o => o.Store = outage.Store);
        host.Flagon.IsEnabled("once");
        host.Time.Advance(FlushInterval);
        await EventuallyAsync(() => host.Admin.GetUsageAsync("once"), u => u.Applications.Count > 0);

        host.Time.Advance(FlushInterval);
        await host.AdvanceToNextRefreshAsync();

        Assert.Equal(1, outage.CallCount(nameof(IFlagStore.UpsertUsageAsync)));
    }

    [Fact]
    public async Task Usage_recorded_since_the_last_flush_is_written_on_graceful_shutdown()
    {
        var store = new InMemoryFlagStore();
        var host = await FlagonTestHost.StartAsync(o => { o.Store = store; o.ApplicationName = "Api"; });
        host.Flagon.IsEnabled("shutdown-flag", defaultValue: true);

        await host.DisposeAsync();

        var usage = Assert.Single(await store.GetUsageAsync("shutdown-flag"));
        Assert.Equal("Api", usage.ApplicationName);
        Assert.Equal(FlagonTestHost.Epoch, usage.LastEvaluatedAt);
    }

    [Fact]
    public async Task Usage_tracking_can_be_turned_off()
    {
        var outage = OutageFlagStore.Wrap();
        var host = await FlagonTestHost.StartAsync(o => { o.Store = outage.Store; o.Flagon = f => f.UsageTracking = false; });
        host.Flagon.IsEnabled("private-flag");
        host.Time.Advance(FlushInterval);
        await host.AdvanceToNextRefreshAsync();

        await host.DisposeAsync();

        Assert.Equal(0, outage.CallCount(nameof(IFlagStore.UpsertUsageAsync)));
        Assert.Empty(await outage.Inner.LoadAllUsageAsync());
    }

    [Fact]
    public async Task Usage_tracking_is_skipped_silently_for_a_read_only_store()
    {
        var inner = new InMemoryFlagStore();
        await inner.TryCreateAsync("remote-flag", FlagDocument.Boolean(true, new FlagMetadata()));
        var readOnly = ReadOnlyFlagStore.Wrap(inner);
        var host = await FlagonTestHost.StartAsync(o => o.Store = readOnly.Store);
        host.Flagon.IsEnabled("remote-flag");
        host.Time.Advance(FlushInterval);
        await host.AdvanceToNextRefreshAsync();

        await host.DisposeAsync();

        Assert.Equal(0, readOnly.UsageWrites);
        Assert.Empty(host.Logs.Flagon(Microsoft.Extensions.Logging.LogLevel.Warning));
    }

    [Fact]
    public async Task Usage_that_could_not_be_written_is_retried_at_the_next_flush()
    {
        var outage = OutageFlagStore.Wrap();
        await using var host = await FlagonTestHost.StartAsync(o => o.Store = outage.Store);
        host.Flagon.IsEnabled("resilient", defaultValue: true);
        outage.IsDown = true;
        host.Time.Advance(FlushInterval);
        await EventuallyAsync(() => Task.FromResult(outage.CallCount(nameof(IFlagStore.UpsertUsageAsync))), n => n == 1);

        outage.IsDown = false;
        host.Time.Advance(FlushInterval);

        var usage = await EventuallyAsync(() => outage.Inner.GetUsageAsync("resilient"), u => u.Count > 0);
        Assert.True(Assert.Single(usage).ObservedDefault!.Value.GetBoolean());
    }

    [Fact]
    public async Task Keys_that_could_never_be_flags_are_not_tracked()
    {
        var store = new InMemoryFlagStore();
        var host = await FlagonTestHost.StartAsync(o => o.Store = store);
        host.Flagon.IsEnabled("not a valid key!");

        await host.DisposeAsync();

        Assert.Empty(await store.LoadAllUsageAsync());
    }

    [Fact]
    public async Task Each_written_usage_pairs_an_evaluation_time_with_the_default_supplied_at_that_time()
    {
        var store = UsageRecordingStore.Wrap(new InMemoryFlagStore());
        await using var host = await FlagonTestHost.StartAsync(o =>
        {
            o.Store = store.Store;
            o.Flagon = f => f.UsageFlushInterval = TimeSpan.FromSeconds(1);
        });

        // Every second the code default flips, and every second a flush runs concurrently with the next evaluation.
        // A written row must never pair one evaluation's time with another evaluation's default.
        for (var second = 1; second <= 5000; second++)
        {
            host.Time.Advance(TimeSpan.FromSeconds(1));
            host.Flagon.IsEnabled("flip-flop", defaultValue: second % 2 == 0);
        }

        await host.StopAsync();

        Assert.NotEmpty(store.Written);
        Assert.All(store.Written, u =>
        {
            var second = (long)(u.LastEvaluatedAt - FlagonTestHost.Epoch).TotalSeconds;
            Assert.Equal(second % 2 == 0, u.ObservedDefault!.Value.GetBoolean());
        });
        Assert.Equal(FlagonTestHost.Epoch.AddSeconds(5000), store.Written[^1].LastEvaluatedAt);
    }

    /// <summary>A store that forwards everything and keeps every usage row written, in order.</summary>
    public class UsageRecordingStore : System.Reflection.DispatchProxy
    {
        private readonly System.Collections.Concurrent.ConcurrentQueue<FlagUsage> _written = new();
        private IFlagStore _inner = null!;

        public IFlagStore Store { get; private set; } = null!;

        public IReadOnlyList<FlagUsage> Written => _written.ToArray();

        public static UsageRecordingStore Wrap(IFlagStore inner)
        {
            var proxy = Create<IFlagStore, UsageRecordingStore>();
            var store = (UsageRecordingStore)(object)proxy;
            store._inner = inner;
            store.Store = proxy;
            return store;
        }

        protected override object? Invoke(System.Reflection.MethodInfo? targetMethod, object?[]? args)
        {
            ArgumentNullException.ThrowIfNull(targetMethod);
            if (targetMethod.Name == nameof(IFlagStore.UpsertUsageAsync))
            {
                foreach (var usage in (IEnumerable<FlagUsage>)args![0]!)
                {
                    _written.Enqueue(usage);
                }
            }

            return targetMethod.Invoke(_inner, args);
        }
    }

    /// <summary>A store that reports itself read-only and counts (but forwards) usage writes, to prove none are attempted.</summary>
    public class ReadOnlyFlagStore : System.Reflection.DispatchProxy
    {
        private IFlagStore _inner = null!;
        private int _usageWrites;

        public IFlagStore Store { get; private set; } = null!;

        public int UsageWrites => _usageWrites;

        public static ReadOnlyFlagStore Wrap(IFlagStore inner)
        {
            var proxy = Create<IFlagStore, ReadOnlyFlagStore>();
            var store = (ReadOnlyFlagStore)(object)proxy;
            store._inner = inner;
            store.Store = proxy;
            return store;
        }

        protected override object? Invoke(System.Reflection.MethodInfo? targetMethod, object?[]? args)
        {
            ArgumentNullException.ThrowIfNull(targetMethod);
            if (targetMethod.Name == "get_" + nameof(IFlagStore.IsReadOnly))
            {
                return true;
            }

            if (targetMethod.Name == nameof(IFlagStore.UpsertUsageAsync))
            {
                Interlocked.Increment(ref _usageWrites);
            }

            return targetMethod.Invoke(_inner, args);
        }
    }

    /// <summary>Polls (bounded by a real-time safety timeout) until <paramref name="read"/> satisfies <paramref name="done"/>.</summary>
    private static async Task<T> EventuallyAsync<T>(Func<Task<T>> read, Func<T, bool> done)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (true)
        {
            var value = await read();
            if (done(value))
            {
                return value;
            }

            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException($"Condition not met; last value: {value}");
            }

            await Task.Delay(1);
        }
    }
}
