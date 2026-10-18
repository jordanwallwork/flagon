using System.Diagnostics.Metrics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Flagon.Tests.Support;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.Metrics.Testing;

namespace Flagon.AspNetCore.Tests.Management;

/// <summary>Previewing a flag through <c>POST flags/{key}/evaluate</c> is not using it: nothing is recorded or written.</summary>
public class ManagementApiEvaluateSideEffectTests
{
    private static readonly string[] StoreWrites = ["TryCreateAsync", "UpdateAsync", "ArchiveAsync", "RestoreAsync", "PurgeAsync", "UpsertUsageAsync"];

    private static readonly object Context = new { targetingKey = "alice", country = "GB" };

    private static readonly JsonElement Draft = JsonDocument.Parse("""
        { "state": "ENABLED", "variants": { "on": true, "off": false }, "defaultVariant": "off",
          "targeting": { "if": [ { "==": [ { "var": "country" }, "GB" ] }, "on", null ] } }
        """).RootElement.Clone();

    /// <summary>Previews the stored flag, a draft of it, a draft of an unstored key and an unstored key.</summary>
    private static async Task PreviewAsync(HttpClient client)
    {
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/flagon/api/flags/new-checkout/evaluate", new { context = Context })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/flagon/api/flags/new-checkout/evaluate", new { context = Context, document = Draft })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/flagon/api/flags/draft-only/evaluate", new { context = Context, document = Draft })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsJsonAsync("/flagon/api/flags/not-a-flag/evaluate", new { context = Context })).StatusCode);
    }

    [Fact]
    public async Task Previews_record_no_usage_or_evaluation_metric_and_write_nothing_to_the_store()
    {
        var store = OutageFlagStore.Wrap();
        await using var host = await FlagonTestHost.StartAsync(o =>
        {
            o.Store = store.Store;
            o.Endpoints = app => app.MapFlagonApi();
        });
        await host.Admin.CreateAsync(new CreateFlagRequest("new-checkout", Enabled: true));
        var changeVersion = await host.Store.GetChangeVersionAsync();
        var writes = StoreWrites.ToDictionary(m => m, store.CallCount);
        using var evaluations = new MetricCollector<long>(
            host.Services.GetRequiredService<IMeterFactory>(), FlagonDiagnostics.MeterName, "flagon.evaluations");

        await PreviewAsync(host.CreateClient());
        await host.StopAsync(); // flushes usage

        Assert.Empty(evaluations.GetMeasurementSnapshot());
        Assert.Empty(await host.Admin.ListUsageAsync());
        Assert.Equal(changeVersion, await host.Store.GetChangeVersionAsync());
        Assert.Equal(writes, StoreWrites.ToDictionary(m => m, store.CallCount));
        Assert.Null(await host.Admin.GetAsync("draft-only"));
    }

    [Fact]
    public async Task Previewing_a_missing_key_does_not_auto_create_it()
    {
        await using var host = await FlagonTestHost.StartAsync(o =>
        {
            o.Endpoints = app => app.MapFlagonApi();
            o.Flagon = f => f.AutoCreate = true;
        });
        await host.Admin.CreateAsync(new CreateFlagRequest("new-checkout", Enabled: true));

        await PreviewAsync(host.CreateClient());

        // The app's own evaluation of an unknown key is auto-created; once that has happened, any queued preview key would have been too.
        host.Flagon.IsEnabled("used-in-code");
        await FlagonTestHost.EventuallyAsync(async () => await host.Admin.GetAsync("used-in-code") is not null, "the app's own flag is auto-created");
        await host.StopAsync();

        Assert.Null(await host.Admin.GetAsync("not-a-flag"));
        Assert.Null(await host.Admin.GetAsync("draft-only"));
    }
}
