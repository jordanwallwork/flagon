using Flagon.Storage;
using Flagon.Tests.Support;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;

namespace Flagon.Tests;

public class AutoCreateTests
{
    /// <summary>Starts a host with auto-create on (the harness turns it off by default); <paramref name="configure"/> may still override it.</summary>
    private static Task<FlagonTestHost> StartAsync(Action<FlagonTestHostOptions>? configure = null) =>
        FlagonTestHost.StartAsync(o =>
        {
            configure?.Invoke(o);
            var flagon = o.Flagon;
            o.Flagon = f =>
            {
                f.AutoCreate = true;
                flagon?.Invoke(f);
            };
        });

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Evaluating_an_unknown_key_creates_the_flag_with_the_callers_default_and_source_auto(bool callerDefault)
    {
        await using var host = await StartAsync();

        var details = host.Flagon.GetBooleanDetails("new-thing", callerDefault);
        Assert.Equal(callerDefault, details.Value);
        Assert.Equal(ErrorCode.FlagNotFound, details.ErrorCode);

        await FlagonTestHost.EventuallyAsync(
            () => Task.FromResult(host.Flagon.GetBooleanDetails("new-thing", !callerDefault).ErrorCode == ErrorCode.None),
            "the auto-created flag is visible to this app");

        Assert.Equal(callerDefault, host.Flagon.IsEnabled("new-thing", !callerDefault));
        var flag = await host.Admin.GetAsync("new-thing");
        Assert.NotNull(flag);
        Assert.Equal(FlagState.Enabled, flag.Document.State);
        Assert.Equal(FlagSource.Auto, flag.Document.Metadata.Source);
        Assert.Equal(FlagonTestHost.Epoch, flag.Document.Metadata.CreatedAt);
        Assert.False(flag.Archived);
    }

    [Fact]
    public async Task Auto_create_is_audited_with_the_application_name()
    {
        await using var host = await StartAsync(o => o.ApplicationName = "Checkout.Api");

        host.Flagon.IsEnabled("new-thing", true);
        await host.StopAsync();

        var entry = Assert.Single(await host.Store.GetAuditAsync("new-thing"));
        Assert.Equal(FlagAuditAction.Created, entry.Action);
        Assert.Equal("Checkout.Api", entry.ApplicationName);
        Assert.Equal(FlagonTestHost.Epoch, entry.Timestamp);
        Assert.Null(entry.Before);
        Assert.Equal(FlagSource.Auto, entry.After!.Metadata.Source);
        Assert.Contains(host.Logs.Flagon(LogLevel.Information), l => l.EventId.Id == 1401 && l.Message.Contains("'new-thing'", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Repeated_evaluations_of_an_unknown_key_are_deduplicated_into_one_create()
    {
        var store = OutageFlagStore.Wrap();
        await using var host = await StartAsync(o => o.Store = store.Store);

        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
        {
            for (var i = 0; i < 500; i++)
            {
                host.Flagon.IsEnabled("hot-key");
            }
        })));
        await host.StopAsync();

        Assert.Equal(1, store.CallCount(nameof(IFlagStore.TryCreateAsync)));
        Assert.NotNull(await store.Inner.GetAsync("hot-key"));
    }

    [Fact]
    public async Task Many_apps_hitting_the_same_unknown_key_create_exactly_one_flag_without_errors()
    {
        var store = new InMemoryFlagStore();
        var time = new FakeTimeProvider(FlagonTestHost.Epoch);
        var hosts = new List<FlagonTestHost>();
        for (var i = 0; i < 6; i++)
        {
            var name = $"App{i}";
            hosts.Add(await StartAsync(o => { o.Store = store; o.Time = time; o.ApplicationName = name; }));
        }

        try
        {
            using var go = new Barrier(hosts.Count);
            await Task.WhenAll(hosts.Select(h => Task.Run(() =>
            {
                go.SignalAndWait();
                for (var i = 0; i < 200; i++)
                {
                    Assert.True(h.Flagon.IsEnabled("racy", true));
                }
            })));
            foreach (var host in hosts)
            {
                await host.StopAsync();
            }

            var flag = Assert.Single(await store.LoadAllAsync());
            Assert.Equal("racy", flag.Key);
            Assert.Equal(FlagSource.Auto, flag.Document.Metadata.Source);
            Assert.Single(await store.GetAuditAsync("racy"));
            foreach (var host in hosts)
            {
                Assert.DoesNotContain(host.Logs.Entries, l => l.Level >= LogLevel.Error || l.EventId.Id == 1402);
            }
        }
        finally
        {
            foreach (var host in hosts)
            {
                await host.DisposeAsync();
            }
        }
    }

    [Fact]
    public async Task An_app_whose_snapshot_predates_an_archive_never_recreates_the_archived_key()
    {
        var store = OutageFlagStore.Wrap();
        await using var host = await StartAsync(o => o.Store = store.Store);
        // Another app creates and archives the key; this app has not refreshed yet, so it looks missing here.
        var created = await store.Inner.TryCreateAsync("retired", FlagDocument.Boolean(true, new FlagMetadata()));
        await store.Inner.ArchiveAsync("retired", created!.Version);

        Assert.False(host.Flagon.IsEnabled("retired", false));
        await host.StopAsync();

        Assert.Equal(1, store.CallCount(nameof(IFlagStore.TryCreateAsync)));
        var flag = await store.Inner.GetAsync("retired");
        Assert.True(flag!.Archived);
        Assert.Empty(await store.Inner.GetAuditAsync("retired"));
    }

    [Fact]
    public async Task An_archived_flag_is_not_auto_created_again()
    {
        var store = OutageFlagStore.Wrap();
        await using var host = await StartAsync(o => o.Store = store.Store);
        var flag = await host.Admin.CreateAsync(new CreateFlagRequest("retired", Enabled: true));
        await host.Admin.ArchiveAsync("retired", flag.Version);

        Assert.Equal(EvaluationReason.Archived, host.Flagon.GetBooleanDetails("retired", true).Reason);
        await host.StopAsync();

        Assert.Equal(1, store.CallCount(nameof(IFlagStore.TryCreateAsync)));
        Assert.True((await store.Inner.GetAsync("retired"))!.Archived);
    }

    [Fact]
    public async Task Nothing_is_created_when_auto_create_is_switched_off()
    {
        var store = OutageFlagStore.Wrap();
        await using var host = await StartAsync(o =>
        {
            o.Store = store.Store;
            o.Flagon = f => f.AutoCreate = false;
        });

        Assert.True(host.Flagon.IsEnabled("unknown", true));
        await host.StopAsync();

        Assert.Equal(0, store.CallCount(nameof(IFlagStore.TryCreateAsync)));
        Assert.Empty(await store.Inner.LoadAllAsync());
        Assert.DoesNotContain(host.Logs.Entries, l => l.Level >= LogLevel.Error || l.EventId.Id == 1402);
    }

    [Fact]
    public async Task Nothing_is_created_and_nothing_fails_against_a_read_only_store()
    {
        var store = OutageFlagStore.Wrap();
        store.ReportsReadOnly = true;
        await using var host = await StartAsync(o => o.Store = store.Store);

        Assert.True(host.Flagon.IsEnabled("unknown", true));
        await host.StopAsync();

        Assert.Equal(0, store.CallCount(nameof(IFlagStore.TryCreateAsync)));
        Assert.Empty(await store.Inner.LoadAllAsync());
        Assert.DoesNotContain(host.Logs.Entries, l => l.Level >= LogLevel.Error || l.EventId.Id == 1402);
    }

    [Fact]
    public async Task Invalid_keys_are_never_auto_created()
    {
        var store = OutageFlagStore.Wrap();
        await using var host = await StartAsync(o => o.Store = store.Store);

        host.Flagon.IsEnabled("has space", true);
        await host.StopAsync();

        Assert.Equal(0, store.CallCount(nameof(IFlagStore.TryCreateAsync)));
    }

    [Fact]
    public async Task A_failed_auto_create_is_logged_and_retried_on_a_later_evaluation()
    {
        var store = OutageFlagStore.Wrap();
        await using var host = await StartAsync(o => o.Store = store.Store);
        store.IsDown = true;

        Assert.True(host.Flagon.IsEnabled("flaky", true));
        await FlagonTestHost.EventuallyAsync(
            () => Task.FromResult(host.Logs.Flagon(LogLevel.Warning).Any(l => l.EventId.Id == 1402)),
            "the failed create is logged");

        store.IsDown = false;
        await FlagonTestHost.EventuallyAsync(async () =>
        {
            host.Flagon.IsEnabled("flaky", true);
            return await store.Inner.GetAsync("flaky") is not null;
        }, "a later evaluation retries the create");
    }

    [Fact]
    public async Task A_boolean_default_auto_creates_an_on_off_flag()
    {
        await using var host = await StartAsync();

        host.Flagon.IsEnabled("new-thing", true);
        await host.StopAsync();

        var flag = (await host.Store.GetAsync("new-thing"))!;
        Assert.Equal(["off", "on"], flag.Document.Variants.Keys.Order(StringComparer.Ordinal));
        Assert.Equal("on", flag.Document.DefaultVariant);
    }

    private sealed record Layout(int Columns, string Title);

    public static TheoryData<string, FlagType, string> TypedDefaults => new()
    {
        { "string", FlagType.String, "\"dark\"" },
        { "int", FlagType.Number, "25" },
        { "double", FlagType.Number, "0.5" },
        { "object", FlagType.Object, """{"columns":3,"title":"Pics"}""" },
    };

    private static void EvaluateTyped(IFlagon flagon, string getter, string key)
    {
        _ = getter switch
        {
            "string" => (object)flagon.GetString(key, "dark"),
            "int" => flagon.GetInt(key, 25),
            "double" => flagon.GetDouble(key, 0.5),
            _ => flagon.GetObject(key, new Layout(3, "Pics"), options: new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)),
        };
    }

    [Theory]
    [MemberData(nameof(TypedDefaults))]
    public async Task A_typed_default_auto_creates_a_flag_of_its_type_serving_it(string getter, FlagType type, string json)
    {
        await using var host = await StartAsync();

        EvaluateTyped(host.Flagon, getter, "new-thing");
        await host.StopAsync();

        var flag = (await host.Store.GetAsync("new-thing"))!;
        Assert.Equal(type, flag.Document.Type);
        Assert.Equal(FlagState.Enabled, flag.Document.State);
        Assert.Equal("default", flag.Document.DefaultVariant);
        Assert.Equal(json, Assert.Single(flag.Document.Variants, v => v.Key == "default").Value.GetRawText());
        Assert.Single(flag.Document.Variants);
        Assert.Null(flag.Document.Targeting);
        Assert.Equal(FlagSource.Auto, flag.Document.Metadata.Source);
    }

    private sealed class MutableLayout
    {
        public int Columns { get; set; }
    }

    [Fact]
    public async Task An_object_default_is_auto_created_as_it_was_when_evaluated_even_if_the_caller_changes_it_later()
    {
        var store = OutageFlagStore.Wrap();
        var release = new TaskCompletionSource();
        store.HangCreate = release.Task;
        await using var host = await StartAsync(o => o.Store = store.Store);

        // The first create hangs, so the second key waits in the queue behind it.
        host.Flagon.IsEnabled("first");
        await FlagonTestHost.EventuallyAsync(
            () => Task.FromResult(store.CallCount(nameof(IFlagStore.TryCreateAsync)) == 1), "the first create has started");
        var layout = new MutableLayout { Columns = 3 };
        host.Flagon.GetObject("layout", layout);
        layout.Columns = 99;

        release.SetResult();
        await host.StopAsync();

        var flag = (await store.Inner.GetAsync("layout"))!;
        Assert.Equal("""{"Columns":3}""", flag.Document.Variants["default"].GetRawText());
    }

    [Fact]
    public async Task An_auto_created_typed_flag_is_then_served_to_this_app()
    {
        await using var host = await StartAsync();

        Assert.Equal("dark", host.Flagon.GetString("theme", "dark"));

        await FlagonTestHost.EventuallyAsync(
            () => Task.FromResult(host.Flagon.GetStringDetails("theme", "light") is { Value: "dark", Variant: "default", Reason: EvaluationReason.Static }),
            "the auto-created flag serves the first caller's default");
    }

    [Theory]
    [InlineData("null string")]
    [InlineData("array")]
    public async Task A_default_with_no_flag_type_auto_creates_nothing(string kind)
    {
        var store = OutageFlagStore.Wrap();
        await using var host = await StartAsync(o => o.Store = store.Store);

        if (kind == "array")
        {
            host.Flagon.GetObject("listy", new[] { 1, 2 });
        }
        else
        {
            host.Flagon.GetString("nully", null!);
        }

        await host.StopAsync();

        Assert.Equal(0, store.CallCount(nameof(IFlagStore.TryCreateAsync)));
        Assert.DoesNotContain(host.Logs.Entries, l => l.Level >= LogLevel.Warning && l.EventId.Id != 1001);
    }

    [Fact]
    public async Task A_type_mismatch_never_auto_creates()
    {
        var store = OutageFlagStore.Wrap();
        await store.Inner.TryCreateAsync("checkout", FlagDocument.Boolean(true, new FlagMetadata()));
        await using var host = await StartAsync(o => o.Store = store.Store);

        Assert.Equal(ErrorCode.TypeMismatch, host.Flagon.GetIntDetails("checkout", 3).ErrorCode);
        await host.StopAsync();

        Assert.Equal(0, store.CallCount(nameof(IFlagStore.TryCreateAsync)));
    }

    [Fact]
    public async Task An_archived_key_is_not_auto_created_again_by_a_typed_getter()
    {
        var store = OutageFlagStore.Wrap();
        await using var host = await StartAsync(o => o.Store = store.Store);
        // Another app creates and archives the key; this app has not refreshed yet, so it looks missing here.
        var created = await store.Inner.TryCreateAsync("retired", FlagDocument.Boolean(true, new FlagMetadata()));
        await store.Inner.ArchiveAsync("retired", created!.Version);

        Assert.Equal("fallback", host.Flagon.GetString("retired", "fallback"));
        await host.StopAsync();

        var flag = await store.Inner.GetAsync("retired");
        Assert.True(flag!.Archived);
        Assert.Equal(FlagType.Boolean, flag.Document.Type);
        Assert.Empty(await store.Inner.GetAuditAsync("retired"));
    }

    [Fact]
    public async Task Two_apps_racing_with_different_types_create_one_flag_and_the_loser_gets_a_type_mismatch()
    {
        var store = new InMemoryFlagStore();
        var time = new FakeTimeProvider(FlagonTestHost.Epoch);
        await using var web = await StartAsync(o => { o.Store = store; o.Time = time; o.ApplicationName = "Web"; });
        await using var worker = await StartAsync(o => { o.Store = store; o.Time = time; o.ApplicationName = "Worker"; });

        using (var go = new Barrier(2))
        {
            await Task.WhenAll(
                Task.Run(() => { go.SignalAndWait(); web.Flagon.GetString("page-size", "25"); }),
                Task.Run(() => { go.SignalAndWait(); worker.Flagon.GetInt("page-size", 25); }));
        }

        await FlagonTestHost.EventuallyAsync(async () => await store.GetAsync("page-size") is not null, "one app creates the flag");
        await FlagonTestHost.AdvanceToNextRefreshAsync(web, worker);
        var flag = Assert.Single(await store.LoadAllAsync());
        Assert.Single(await store.GetAuditAsync("page-size"));

        var asString = web.Flagon.GetStringDetails("page-size", "25");
        var asInt = worker.Flagon.GetIntDetails("page-size", 25);
        if (flag.Document.Type == FlagType.String)
        {
            Assert.Equal(ErrorCode.None, asString.ErrorCode);
            Assert.Equal(ErrorCode.TypeMismatch, asInt.ErrorCode);
        }
        else
        {
            Assert.Equal(FlagType.Number, flag.Document.Type);
            Assert.Equal(ErrorCode.None, asInt.ErrorCode);
            Assert.Equal(ErrorCode.TypeMismatch, asString.ErrorCode);
        }

        await web.StopAsync();
        await worker.StopAsync();
        var usage = await web.Admin.GetUsageAsync("page-size");
        Assert.Equal(["Web", "Worker"], usage.Applications.Select(a => a.ApplicationName));
        Assert.True(usage.HasDefaultMismatch);
    }
}
