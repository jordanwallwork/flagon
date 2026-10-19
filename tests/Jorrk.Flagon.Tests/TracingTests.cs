using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using Jorrk.Flagon.Tests.Support;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.Metrics.Testing;
using Microsoft.Extensions.Logging;

namespace Jorrk.Flagon.Tests;

/// <summary>Activity sources are process-wide, so these tests must not run alongside other hosts.</summary>
[CollectionDefinition(nameof(TracingTests), DisableParallelization = true)]
public class TracingCollection;

[Collection(nameof(TracingTests))]
public sealed class TracingTests : IDisposable
{
    private readonly ConcurrentQueue<Activity> _activities = new();
    private readonly ActivityListener _listener;

    public TracingTests()
    {
        _listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == FlagonDiagnostics.ActivitySourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = _activities.Enqueue,
        };
        ActivitySource.AddActivityListener(_listener);
    }

    public void Dispose() => _listener.Dispose();

    private Activity[] Named(string name) => _activities.Where(a => a.OperationName == name).ToArray();

    [Fact]
    public async Task Every_refresh_is_traced_as_a_flagon_refresh_activity()
    {
        await using var host = await FlagonTestHost.StartAsync();
        var before = Named("flagon.refresh").Length;

        await host.AdvanceToNextRefreshAsync();

        var refreshes = Named("flagon.refresh");
        Assert.Equal(before + 1, refreshes.Length);
        Assert.Equal(ActivityStatusCode.Unset, refreshes[^1].Status);
        Assert.Equal("unchanged", refreshes[^1].GetTagItem("flagon.refresh.outcome"));
    }

    [Fact]
    public async Task A_refresh_that_cannot_reach_the_store_is_traced_as_an_error()
    {
        var store = OutageFlagStore.Wrap();
        await using var host = await FlagonTestHost.StartAsync(o => o.Store = store.Store);

        store.IsDown = true;
        await host.AdvanceToNextRefreshAsync();

        var failed = Named("flagon.refresh")[^1];
        Assert.Equal(ActivityStatusCode.Error, failed.Status);
        Assert.Equal(typeof(StoreOutageException).FullName, failed.GetTagItem("error.type"));
    }

    [Fact]
    public async Task Admin_writes_are_traced_with_the_flag_key()
    {
        await using var host = await FlagonTestHost.StartAsync();

        var flag = await host.Admin.CreateAsync(new CreateFlagRequest("traced", Enabled: false));
        await host.Admin.SetEnabledAsync("traced", true, flag.Version);

        Assert.Equal("traced", Assert.Single(Named("flagon.create")).GetTagItem("feature_flag.key"));
        var toggle = Assert.Single(Named("flagon.set_enabled"));
        Assert.Equal("traced", toggle.GetTagItem("feature_flag.key"));
        Assert.Equal(ActivityStatusCode.Unset, toggle.Status);
    }

    [Fact]
    public async Task Editing_metadata_is_traced_and_a_stale_edit_is_logged_as_a_conflict()
    {
        await using var host = await FlagonTestHost.StartAsync();
        var flag = await host.Admin.CreateAsync(new CreateFlagRequest("documented", Enabled: false));
        await host.Admin.UpdateMetadataAsync("documented", new UpdateFlagMetadataRequest(Description: "first"), flag.Version);

        await Assert.ThrowsAsync<FlagVersionConflictException>(
            () => host.Admin.UpdateMetadataAsync("documented", new UpdateFlagMetadataRequest(Description: "second"), flag.Version));

        Assert.Equal(
            [ActivityStatusCode.Unset, ActivityStatusCode.Error],
            Named("flagon.update_metadata").Select(a => a.Status));
        Assert.Contains(host.Logs.Flagon(LogLevel.Information), l => l.EventId.Id == 1201 && l.Message.Contains("update_metadata", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_version_conflict_is_traced_as_an_error_and_logged_but_not_counted_as_a_store_error()
    {
        await using var host = await FlagonTestHost.StartAsync();
        using var storeErrors = new MetricCollector<long>(
            host.Services.GetRequiredService<IMeterFactory>(), FlagonDiagnostics.MeterName, "flagon.store.errors");
        var flag = await host.Admin.CreateAsync(new CreateFlagRequest("contested", Enabled: false));
        await host.Admin.SetEnabledAsync("contested", true, flag.Version);

        await Assert.ThrowsAsync<FlagVersionConflictException>(() => host.Admin.SetEnabledAsync("contested", false, flag.Version));

        var conflict = Named("flagon.set_enabled")[^1];
        Assert.Equal(ActivityStatusCode.Error, conflict.Status);
        Assert.Equal(typeof(FlagVersionConflictException).FullName, conflict.GetTagItem("error.type"));
        Assert.Empty(storeErrors.GetMeasurementSnapshot());
        var log = Assert.Single(host.Logs.Flagon(LogLevel.Information), l => l.EventId.Id == 1201);
        Assert.Contains("contested", log.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Archiving_is_traced_and_a_stale_archive_is_logged_as_a_conflict()
    {
        await using var host = await FlagonTestHost.StartAsync();
        var flag = await host.Admin.CreateAsync(new CreateFlagRequest("retired", Enabled: true));
        var archived = await host.Admin.ArchiveAsync("retired", flag.Version);

        await Assert.ThrowsAsync<FlagVersionConflictException>(() => host.Admin.RestoreAsync("retired", flag.Version));

        Assert.Equal("retired", Assert.Single(Named("flagon.archive")).GetTagItem("feature_flag.key"));
        Assert.Equal(ActivityStatusCode.Error, Assert.Single(Named("flagon.restore")).Status);
        var log = Assert.Single(host.Logs.Flagon(LogLevel.Information), l => l.EventId.Id == 1201);
        Assert.Contains("restore", log.Message, StringComparison.Ordinal);
        Assert.Contains(archived.Version.ToString(System.Globalization.CultureInfo.InvariantCulture), log.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Setting_targeting_is_traced_and_a_rejected_rule_marks_the_activity_as_an_error()
    {
        await using var host = await FlagonTestHost.StartAsync();
        var flag = await host.Admin.CreateAsync(new CreateFlagRequest("targeted"));
        var rule = System.Text.Json.JsonDocument.Parse("""{ "if": [ true, "on", null ] }""").RootElement.Clone();
        var invalid = System.Text.Json.JsonDocument.Parse("""{ "nope": [] }""").RootElement.Clone();

        await host.Admin.SetTargetingAsync("targeted", rule, flag.Version);
        await Assert.ThrowsAsync<FlagTargetingException>(() => host.Admin.SetTargetingAsync("targeted", invalid, flag.Version + 1));

        var activities = Named("flagon.set_targeting");
        Assert.Equal(2, activities.Length);
        Assert.All(activities, a => Assert.Equal("targeted", a.GetTagItem("feature_flag.key")));
        Assert.Equal([ActivityStatusCode.Unset, ActivityStatusCode.Error], activities.Select(a => a.Status));
    }

    [Fact]
    public async Task Seeding_flags_defined_in_code_is_traced()
    {
        await using var host = await FlagonTestHost.StartAsync(o => o.Flagon = f => f.Define("seeded", true));

        var seed = Assert.Single(Named("flagon.seed"));
        Assert.Equal(ActivityStatusCode.Unset, seed.Status);
        Assert.Equal(1, seed.GetTagItem("flagon.seed.definitions"));
    }

    [Fact]
    public async Task A_seeding_failure_is_traced_as_an_error_and_counted_as_a_store_error()
    {
        var store = OutageFlagStore.Wrap(isDown: true);
        await using var host = await FlagonTestHost.StartAsync(o =>
        {
            o.Store = store.Store;
            o.Flagon = f => f.Define("seeded", true);
        });
        using var storeErrors = new MetricCollector<long>(
            host.Services.GetRequiredService<IMeterFactory>(), FlagonDiagnostics.MeterName, "flagon.store.errors");

        await host.AdvanceToNextRefreshAsync();

        Assert.All(Named("flagon.seed"), a => Assert.Equal(ActivityStatusCode.Error, a.Status));
        Assert.Contains(storeErrors.GetMeasurementSnapshot(), m => (string?)m.Tags["flagon.store.operation"] == "seed");
    }

    [Fact]
    public async Task Flushing_usage_to_the_store_is_traced()
    {
        var host = await FlagonTestHost.StartAsync();
        await host.Admin.CreateAsync(new CreateFlagRequest("used", Enabled: true));
        host.Flagon.IsEnabled("used");

        await host.DisposeAsync(); // graceful shutdown flushes pending usage

        var flush = Assert.Single(Named("flagon.usage_flush"));
        Assert.Equal(1, flush.GetTagItem("flagon.usage.flags"));
    }

    [Fact]
    public async Task Applying_store_migrations_at_startup_is_traced_and_a_failure_counts_as_a_store_error()
    {
        var store = OutageFlagStore.Wrap(isDown: true);
        await using var host = await FlagonTestHost.StartAsync(o => o.Store = store.Store);
        using var storeErrors = new MetricCollector<long>(
            host.Services.GetRequiredService<IMeterFactory>(), FlagonDiagnostics.MeterName, "flagon.store.errors");

        await host.AdvanceToNextRefreshAsync(); // still down: migrations are retried and fail again
        store.IsDown = false;
        await host.AdvanceToNextRefreshAsync();

        Assert.Equal(
            [ActivityStatusCode.Error, ActivityStatusCode.Error, ActivityStatusCode.Unset],
            Named("flagon.migrate").Select(a => a.Status));
        Assert.Contains(storeErrors.GetMeasurementSnapshot(), m => (string?)m.Tags["flagon.store.operation"] == "migrate");
    }

    [Fact]
    public async Task Auto_creating_a_flag_is_traced_with_its_key()
    {
        await using var host = await FlagonTestHost.StartAsync(o => o.Flagon = f => f.AutoCreate = true);

        host.Flagon.IsEnabled("brand-new", true);
        await host.StopAsync(); // drains the auto-create queue

        var create = Assert.Single(Named("flagon.auto_create"));
        Assert.Equal("brand-new", create.GetTagItem("feature_flag.key"));
        Assert.Equal(true, create.GetTagItem("flagon.auto_create.created"));
    }

    [Fact]
    public async Task A_store_failure_during_an_admin_write_is_counted_as_a_store_error()
    {
        var store = OutageFlagStore.Wrap();
        await using var host = await FlagonTestHost.StartAsync(o => o.Store = store.Store);
        using var storeErrors = new MetricCollector<long>(
            host.Services.GetRequiredService<IMeterFactory>(), FlagonDiagnostics.MeterName, "flagon.store.errors");

        store.IsDown = true;
        await Assert.ThrowsAsync<StoreOutageException>(() => host.Admin.CreateAsync(new CreateFlagRequest("doomed", Enabled: true)));

        var error = Assert.Single(storeErrors.GetMeasurementSnapshot());
        Assert.Equal("create", error.Tags["flagon.store.operation"]);
        Assert.Equal(ActivityStatusCode.Error, Assert.Single(Named("flagon.create")).Status);
    }
}
