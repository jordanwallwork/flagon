using System.Diagnostics.Metrics;
using Jorrk.Flagon.Storage;
using Jorrk.Flagon.Tests.Support;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.Metrics.Testing;

namespace Jorrk.Flagon.Tests;

public class PushReconnectTests
{
    private static readonly FlagDocument Document = FlagDocument.Boolean(true, new FlagMetadata());

    [Fact]
    public async Task A_resubscribe_signal_refreshes_the_snapshot_and_counts_a_reconnect_but_is_not_a_notification()
    {
        var inner = new InMemoryFlagStore();
        var store = new FakeNotifyingFlagStore(inner) { Connected = true };
        await using var host = await FlagonTestHost.StartAsync(o => o.Store = store);
        using var reconnects = Collect<long>(host, "flagon.push.reconnects");
        using var notifications = Collect<long>(host, "flagon.push.notifications");

        // A change that committed while the notifier was down: no signal for it, only the resubscribe.
        await inner.TryCreateAsync("late", Document);
        store.SignalResubscribed();
        await AdvanceUntilAsync(host, () => host.Flagon.GetBooleanDetails("late").ErrorCode == ErrorCode.None);

        Assert.Equal(1, reconnects.GetMeasurementSnapshot().Sum(m => m.Value));
        Assert.Empty(notifications.GetMeasurementSnapshot());
    }

    [Fact]
    public async Task The_push_lag_is_the_time_from_commit_to_the_refresh_that_applies_the_change()
    {
        var inner = new InMemoryFlagStore();
        var store = new FakeNotifyingFlagStore(inner) { Connected = true };
        await using var host = await FlagonTestHost.StartAsync(o => o.Store = store);
        using var lag = Collect<double>(host, "flagon.push.lag");

        host.Time.Advance(TimeSpan.FromSeconds(2));
        await inner.TryCreateAsync("late", Document);
        store.Signal(new FlagChangeSignal(CommittedAt: FlagonTestHost.Epoch));
        await AdvanceUntilAsync(host, () => host.Flagon.GetBooleanDetails("late").ErrorCode == ErrorCode.None);

        // Applied at least two seconds after the commit; the upper bound allows for the debounce and the stepping of the test clock.
        var measurement = Assert.Single(lag.GetMeasurementSnapshot());
        Assert.InRange(measurement.Value, 2.0, 5.0);
    }

    [Fact]
    public async Task A_signal_without_a_commit_time_records_no_lag()
    {
        var inner = new InMemoryFlagStore();
        var store = new FakeNotifyingFlagStore(inner) { Connected = true };
        await using var host = await FlagonTestHost.StartAsync(o => o.Store = store);
        using var lag = Collect<double>(host, "flagon.push.lag");

        await inner.TryCreateAsync("late", Document);
        store.Signal();
        await AdvanceUntilAsync(host, () => host.Flagon.GetBooleanDetails("late").ErrorCode == ErrorCode.None);

        Assert.Empty(lag.GetMeasurementSnapshot());
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

        Assert.True(condition(), "The change did not reach the snapshot before the next poll tick.");
    }

    private static MetricCollector<T> Collect<T>(FlagonTestHost host, string instrument) where T : struct =>
        new(host.Services.GetRequiredService<IMeterFactory>(), FlagonDiagnostics.MeterName, instrument);
}
