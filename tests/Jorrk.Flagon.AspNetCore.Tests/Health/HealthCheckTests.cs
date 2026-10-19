using Jorrk.Flagon.Tests.Support;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Jorrk.Flagon.AspNetCore.Tests.Health;

public class HealthCheckTests
{
    private static Task<FlagonTestHost> StartAsync(OutageFlagStore store, Action<IHealthChecksBuilder>? health = null, TimeSpan? refreshInterval = null) =>
        FlagonTestHost.StartAsync(o =>
        {
            o.Store = store.Store;
            if (refreshInterval is { } interval)
            {
                o.Flagon = f => f.RefreshInterval = interval;
            }

            o.Services = s =>
            {
                var builder = s.AddHealthChecks();
                if (health is null)
                {
                    builder.AddFlagon();
                }
                else
                {
                    health(builder);
                }
            };
        });

    private static async Task<HealthReportEntry> CheckAsync(FlagonTestHost host, string name = "flagon")
    {
        var report = await host.Services.GetRequiredService<HealthCheckService>().CheckHealthAsync();
        return report.Entries[name];
    }

    [Fact]
    public async Task Flagon_is_healthy_once_flags_are_loaded_and_the_store_is_reachable()
    {
        await using var host = await StartAsync(OutageFlagStore.Wrap());

        var entry = await CheckAsync(host);

        Assert.Equal(HealthStatus.Healthy, entry.Status);
        Assert.Equal(true, entry.Data["storeReachable"]);
    }

    [Fact]
    public async Task Flagon_is_degraded_when_the_store_becomes_unreachable()
    {
        var store = OutageFlagStore.Wrap();
        await using var host = await StartAsync(store);

        store.IsDown = true;
        await host.AdvanceToNextRefreshAsync();
        var entry = await CheckAsync(host);

        Assert.Equal(HealthStatus.Degraded, entry.Status);
        Assert.IsType<StoreOutageException>(entry.Exception);
        Assert.Equal(1, entry.Data["consecutiveFailures"]);
    }

    [Fact]
    public async Task Flagon_is_degraded_when_it_starts_without_reaching_the_store()
    {
        await using var host = await StartAsync(OutageFlagStore.Wrap(isDown: true));

        Assert.Equal(HealthStatus.Degraded, (await CheckAsync(host)).Status);
    }

    [Fact]
    public async Task Flagon_is_healthy_again_once_the_store_recovers()
    {
        var store = OutageFlagStore.Wrap(isDown: true);
        await using var host = await StartAsync(store);

        store.IsDown = false;
        await host.AdvanceToNextRefreshAsync();

        Assert.Equal(HealthStatus.Healthy, (await CheckAsync(host)).Status);
    }

    [Fact]
    public async Task Flagon_is_degraded_when_the_snapshot_has_not_been_refreshed_for_longer_than_the_stale_threshold()
    {
        await using var host = await StartAsync(
            OutageFlagStore.Wrap(),
            health: h => h.AddFlagon(staleAfter: TimeSpan.FromSeconds(30)),
            refreshInterval: TimeSpan.FromMinutes(10));

        host.Time.Advance(TimeSpan.FromSeconds(30));
        Assert.Equal(HealthStatus.Healthy, (await CheckAsync(host)).Status);

        host.Time.Advance(TimeSpan.FromSeconds(1));
        var entry = await CheckAsync(host);
        Assert.Equal(HealthStatus.Degraded, entry.Status);
        Assert.Contains("stale", entry.Description, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task By_default_the_snapshot_is_stale_after_three_missed_refresh_intervals()
    {
        // A refresh that never completes (e.g. a hung store call) keeps the store "reachable" but lets the snapshot age.
        var store = OutageFlagStore.Wrap();
        await using var host = await StartAsync(store);
        var hang = new TaskCompletionSource();
        store.Hang = hang.Task;

        host.Time.Advance(TimeSpan.FromSeconds(30));
        Assert.Equal(HealthStatus.Healthy, (await CheckAsync(host)).Status);

        host.Time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(HealthStatus.Degraded, (await CheckAsync(host)).Status);
        hang.SetResult();
    }

    [Fact]
    public async Task The_check_name_and_tags_can_be_customised()
    {
        await using var host = await StartAsync(OutageFlagStore.Wrap(), health: h => h.AddFlagon(name: "flags", tags: ["ready"]));

        var entry = await CheckAsync(host, "flags");

        Assert.Equal(HealthStatus.Healthy, entry.Status);
        Assert.Contains("ready", entry.Tags);
    }

    [Fact]
    public async Task Push_state_is_absent_from_the_data_for_a_store_without_the_capability()
    {
        await using var host = await StartAsync(OutageFlagStore.Wrap());

        var entry = await CheckAsync(host);

        Assert.False(entry.Data.ContainsKey("pushConnected"));
        Assert.False(entry.Data.ContainsKey("lastPushAt"));
    }

    [Fact]
    public async Task A_connected_notifier_reports_pushConnected_and_lastPushAt_without_changing_the_status()
    {
        var store = new FakeNotifyingFlagStore { Connected = true };
        await using var host = await FlagonTestHost.StartAsync(o =>
        {
            o.Store = store;
            o.Services = s => s.AddHealthChecks().AddFlagon();
        });

        var before = await CheckAsync(host);
        Assert.Equal(HealthStatus.Healthy, before.Status);
        Assert.Equal(true, before.Data["pushConnected"]);
        Assert.False(before.Data.ContainsKey("lastPushAt"));

        host.Time.Advance(TimeSpan.FromSeconds(3));
        store.Signal();

        var after = await CheckAsync(host);
        Assert.Equal(HealthStatus.Healthy, after.Status);
        Assert.Equal(host.Time.GetUtcNow(), after.Data["lastPushAt"]);
    }

    [Fact]
    public async Task A_disconnected_notifier_is_still_healthy_with_pushConnected_false()
    {
        var store = new FakeNotifyingFlagStore { Connected = false };
        await using var host = await FlagonTestHost.StartAsync(o =>
        {
            o.Store = store;
            o.Services = s => s.AddHealthChecks().AddFlagon();
        });

        var entry = await CheckAsync(host);

        Assert.Equal(HealthStatus.Healthy, entry.Status);
        Assert.Equal(false, entry.Data["pushConnected"]);
    }
}
