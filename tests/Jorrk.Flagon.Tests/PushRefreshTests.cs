using System.Diagnostics.Metrics;
using Jorrk.Flagon.Storage;
using Jorrk.Flagon.Tests.Support;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.Metrics.Testing;

namespace Jorrk.Flagon.Tests;

public class PushRefreshTests
{
    private static readonly FlagDocument Document = FlagDocument.Boolean(true, new FlagMetadata());

    [Fact]
    public async Task A_write_reaches_the_snapshot_before_the_next_poll_tick()
    {
        var store = new InMemoryFlagStore();
        await using var host = await FlagonTestHost.StartAsync(o => o.Store = store);

        host.Time.Advance(TimeSpan.FromSeconds(1));
        await store.TryCreateAsync("late", Document);
        await AdvanceUntilAsync(host, () => host.Flagon.GetBooleanDetails("late").ErrorCode == ErrorCode.None);

        Assert.True(host.Flagon.IsEnabled("late"));
        Assert.True(host.Time.GetUtcNow() - FlagonTestHost.Epoch < host.Status.RefreshInterval);
    }

    [Fact]
    public async Task A_push_refresh_does_not_reset_the_poll_timer()
    {
        var store = new InMemoryFlagStore();
        await using var host = await FlagonTestHost.StartAsync(o => o.Store = store);

        host.Time.Advance(TimeSpan.FromSeconds(5));
        await store.TryCreateAsync("late", Document);
        await AdvanceUntilAsync(host, () => host.Flagon.GetBooleanDetails("late").ErrorCode == ErrorCode.None);
        var pushedAt = host.Status.LastAttemptAt;
        Assert.True(pushedAt < FlagonTestHost.Epoch.AddSeconds(10));

        // The poll tick is still scheduled from host start, so it fires at exactly ten seconds, not ten seconds after the push.
        host.Time.Advance(FlagonTestHost.Epoch.AddSeconds(10) - host.Time.GetUtcNow());
        await host.WaitForRefreshAttemptAsync();

        Assert.Equal(FlagonTestHost.Epoch.AddSeconds(10), host.Status.LastAttemptAt);
    }

    [Fact]
    public async Task A_pending_push_debounce_does_not_hold_up_the_poll_tick()
    {
        var store = new FakeNotifyingFlagStore();
        await using var host = await FlagonTestHost.StartAsync(o =>
        {
            o.Store = store;
            o.Flagon = f => f.PushMinInterval = TimeSpan.FromMinutes(1);
        });

        // The push's debounce now runs for a minute; the write below does not signal, so only the tick can deliver it.
        store.Signal();
        await store.TryCreateAsync("late", Document);
        host.Time.Advance(host.Status.RefreshInterval);
        await host.WaitForRefreshAttemptAsync();

        Assert.Equal(ErrorCode.None, host.Flagon.GetBooleanDetails("late").ErrorCode);
    }

    [Fact]
    public async Task A_burst_of_writes_coalesces_into_one_push_refresh()
    {
        var store = new InMemoryFlagStore();
        await using var host = await FlagonTestHost.StartAsync(o => o.Store = store);
        using var notifications = Collect<long>(host, "flagon.push.notifications");

        for (var i = 0; i < 5; i++)
        {
            await store.TryCreateAsync($"burst-{i}", Document);
        }

        await AdvanceUntilAsync(host, () => host.Flagon.GetBooleanDetails("burst-4").ErrorCode == ErrorCode.None);

        var outcomes = notifications.GetMeasurementSnapshot()
            .GroupBy(m => (string)m.Tags["flagon.push.outcome"]!)
            .ToDictionary(g => g.Key, g => g.Sum(m => m.Value));
        Assert.Equal(1, outcomes["received"]);
        Assert.Equal(4, outcomes["coalesced"]);
    }

    [Fact]
    public async Task With_PushRefresh_off_a_write_waits_for_the_poll_tick_and_no_notifier_is_started()
    {
        var store = new InMemoryFlagStore();
        await using var host = await FlagonTestHost.StartAsync(o =>
        {
            o.Store = store;
            o.Flagon = f => f.PushRefresh = false;
        });
        using var notifications = Collect<long>(host, "flagon.push.notifications");

        await store.TryCreateAsync("late", Document);
        host.Time.Advance(TimeSpan.FromSeconds(1));

        Assert.Equal(ErrorCode.FlagNotFound, host.Flagon.GetBooleanDetails("late").ErrorCode);
        Assert.Empty(notifications.GetMeasurementSnapshot());

        await host.AdvanceToNextRefreshAsync();
        Assert.True(host.Flagon.IsEnabled("late"));
    }

    [Fact]
    public async Task Stores_without_the_capability_are_polled_only()
    {
        var store = OutageFlagStore.Wrap();
        await using var host = await FlagonTestHost.StartAsync(o => o.Store = store.Store);
        await store.Inner.TryCreateAsync("late", Document);

        host.Time.Advance(TimeSpan.FromSeconds(1));

        Assert.Equal(ErrorCode.FlagNotFound, host.Flagon.GetBooleanDetails("late").ErrorCode);
    }

    [Fact]
    public async Task Writes_after_the_host_stops_are_not_pushed()
    {
        var store = new InMemoryFlagStore();
        await using var host = await FlagonTestHost.StartAsync(o => o.Store = store);
        using var notifications = Collect<long>(host, "flagon.push.notifications");

        await host.StopAsync();
        await store.TryCreateAsync("after-stop", Document);
        host.Time.Advance(TimeSpan.FromSeconds(1));

        Assert.Empty(notifications.GetMeasurementSnapshot());
        Assert.Equal(ErrorCode.FlagNotFound, host.Flagon.GetBooleanDetails("after-stop").ErrorCode);
    }

    [Fact]
    public void Push_refresh_is_on_by_default_and_the_minimum_interval_defaults_to_100_milliseconds_and_must_be_positive()
    {
        var options = new FlagonOptions();

        Assert.True(options.PushRefresh);
        Assert.Equal(TimeSpan.FromMilliseconds(100), options.PushMinInterval);
        Assert.Throws<ArgumentOutOfRangeException>(() => options.PushMinInterval = TimeSpan.Zero);
        Assert.Throws<ArgumentOutOfRangeException>(() => options.PushMinInterval = TimeSpan.FromMilliseconds(-1));
    }

    /// <summary>
    /// Advances the fake clock in <see cref="FlagonOptions.PushMinInterval"/> steps (never reaching a poll tick) until
    /// <paramref name="condition"/> holds, giving the background loop a real moment to act after each step.
    /// </summary>
    private static async Task AdvanceUntilAsync(FlagonTestHost host, Func<bool> condition)
    {
        for (var step = 0; step < 50 && !condition(); step++)
        {
            host.Time.Advance(TimeSpan.FromMilliseconds(100));
            for (var wait = 0; wait < 50 && !condition(); wait++)
            {
                await Task.Delay(1);
            }
        }

        Assert.True(condition(), "The write did not reach the snapshot before the next poll tick.");
    }

    private static MetricCollector<T> Collect<T>(FlagonTestHost host, string instrument) where T : struct =>
        new(host.Services.GetRequiredService<IMeterFactory>(), FlagonDiagnostics.MeterName, instrument);
}
