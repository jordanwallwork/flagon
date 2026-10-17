using System.Diagnostics.Metrics;
using Flagon.Storage;
using Flagon.Tests.Support;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.Metrics.Testing;

namespace Flagon.Tests;

public class TelemetryTests
{
    [Fact]
    public async Task Each_evaluation_is_counted_on_the_Flagon_meter_tagged_by_flag_variant_and_reason()
    {
        await using var host = await FlagonTestHost.StartAsync();
        await host.Admin.CreateAsync(new CreateFlagRequest("checkout", Enabled: true));
        using var evaluations = Collect<long>(host, "flagon.evaluations");

        host.Flagon.IsEnabled("checkout");
        host.Flagon.GetBooleanDetails("checkout");

        var measurements = evaluations.GetMeasurementSnapshot();
        Assert.Equal(2, measurements.Count);
        Assert.All(measurements, m =>
        {
            Assert.Equal(1, m.Value);
            Assert.Equal("checkout", m.Tags["feature_flag.key"]);
            Assert.Equal("on", m.Tags["feature_flag.result.variant"]);
            Assert.Equal("static", m.Tags["feature_flag.result.reason"]);
        });
    }

    [Fact]
    public async Task A_missing_flag_is_counted_with_reason_error_and_error_type_flag_not_found_and_no_variant()
    {
        await using var host = await FlagonTestHost.StartAsync();
        using var evaluations = Collect<long>(host, "flagon.evaluations");

        host.Flagon.IsEnabled("nope", defaultValue: true);

        var m = Assert.Single(evaluations.GetMeasurementSnapshot());
        Assert.Equal("nope", m.Tags["feature_flag.key"]);
        Assert.Equal("error", m.Tags["feature_flag.result.reason"]);
        Assert.Equal("flag_not_found", m.Tags["error.type"]);
        Assert.False(m.Tags.ContainsKey("feature_flag.result.variant"));
    }

    [Fact]
    public async Task An_archived_flag_is_counted_with_reason_archived()
    {
        await using var host = await FlagonTestHost.StartAsync();
        var flag = await host.Admin.CreateAsync(new CreateFlagRequest("old", Enabled: true));
        await host.Admin.ArchiveAsync("old", flag.Version);
        using var evaluations = Collect<long>(host, "flagon.evaluations");

        host.Flagon.IsEnabled("old");

        Assert.Equal("archived", Assert.Single(evaluations.GetMeasurementSnapshot()).Tags["feature_flag.result.reason"]);
    }

    [Fact]
    public async Task Each_refresh_records_its_duration_tagged_with_whether_flags_were_reloaded()
    {
        var store = new InMemoryFlagStore();
        await using var host = await FlagonTestHost.StartAsync(o => o.Store = store);
        using var durations = Collect<double>(host, "flagon.refresh.duration");

        await host.AdvanceToNextRefreshAsync();
        await store.TryCreateAsync("from-elsewhere", FlagDocument.Boolean(true, new FlagMetadata()));
        await host.AdvanceToNextRefreshAsync();

        Assert.Equal(
            ["unchanged", "reloaded"],
            durations.GetMeasurementSnapshot().Select(m => (string?)m.Tags["flagon.refresh.outcome"]));
        Assert.All(durations.GetMeasurementSnapshot(), m => Assert.True(m.Value >= 0));
    }

    [Fact]
    public async Task A_refresh_that_cannot_reach_the_store_counts_a_store_error()
    {
        var store = OutageFlagStore.Wrap();
        await using var host = await FlagonTestHost.StartAsync(o => o.Store = store.Store);
        using var durations = Collect<double>(host, "flagon.refresh.duration");
        using var errors = Collect<long>(host, "flagon.store.errors");

        store.IsDown = true;
        await host.AdvanceToNextRefreshAsync();

        Assert.Equal("failed", Assert.Single(durations.GetMeasurementSnapshot()).Tags["flagon.refresh.outcome"]);
        var error = Assert.Single(errors.GetMeasurementSnapshot());
        Assert.Equal(1, error.Value);
        Assert.Equal("refresh", error.Tags["flagon.store.operation"]);
        Assert.Equal(typeof(StoreOutageException).FullName, error.Tags["error.type"]);
    }

    private static MetricCollector<T> Collect<T>(FlagonTestHost host, string instrument) where T : struct =>
        new(host.Services.GetRequiredService<IMeterFactory>(), FlagonDiagnostics.MeterName, instrument);
}
