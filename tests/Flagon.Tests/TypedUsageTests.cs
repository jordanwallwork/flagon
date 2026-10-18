using System.Text.Json;
using Flagon.Storage;
using Flagon.Tests.Support;

namespace Flagon.Tests;

/// <summary>Usage tracking and default-mismatch detection for the typed getters, through the host seam.</summary>
public class TypedUsageTests
{
    private sealed record Layout(int Columns, string Title);

    /// <summary>Evaluates through <paramref name="evaluate"/> in an app named <paramref name="application"/>, then stops it so its usage is flushed.</summary>
    private static async Task EvaluateInAsync(InMemoryFlagStore store, string application, Action<IFlagon> evaluate)
    {
        await using var host = await FlagonTestHost.StartAsync(o => { o.Store = store; o.ApplicationName = application; });
        evaluate(host.Flagon);
    }

    private static async Task<FlagUsageReport> UsageAsync(InMemoryFlagStore store, string key)
    {
        await using var admin = await FlagonTestHost.StartAsync(o => o.Store = store);
        return await admin.Admin.GetUsageAsync(key);
    }

    private static string Observed(FlagUsageReport usage, string application) =>
        usage.Applications.Single(a => a.ApplicationName == application).ObservedDefault!.Value.GetRawText();

    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement;

    [Fact]
    public async Task Each_typed_getter_records_the_callers_default_as_json()
    {
        var store = new InMemoryFlagStore();
        await EvaluateInAsync(store, "Web", f =>
        {
            f.GetString("theme", "dark");
            f.GetInt("page-size", 25);
            f.GetDouble("ratio", 0.5);
            f.GetObject("layout", new Layout(3, "Pics"), options: new JsonSerializerOptions(JsonSerializerDefaults.Web));
        });

        Assert.Equal("\"dark\"", Observed(await UsageAsync(store, "theme"), "Web"));
        Assert.Equal("25", Observed(await UsageAsync(store, "page-size"), "Web"));
        Assert.Equal("0.5", Observed(await UsageAsync(store, "ratio"), "Web"));
        Assert.Equal("""{"columns":3,"title":"Pics"}""", Observed(await UsageAsync(store, "layout"), "Web"));
    }

    [Fact]
    public async Task Object_defaults_are_recorded_with_their_keys_sorted()
    {
        var store = new InMemoryFlagStore();
        await EvaluateInAsync(store, "Web", f => f.GetObject("layout", Json("""{ "title": "Pics", "columns": 3.0, "tags": [ { "z": 1, "a": 2 } ] }""")));

        Assert.Equal("""{"columns":3,"tags":[{"a":2,"z":1}],"title":"Pics"}""", Observed(await UsageAsync(store, "layout"), "Web"));
    }

    [Fact]
    public async Task A_json_default_is_recorded_after_the_caller_disposes_its_document()
    {
        var store = new InMemoryFlagStore();
        await EvaluateInAsync(store, "Web", f =>
        {
            using var document = JsonDocument.Parse("""{ "columns": 3 }""");
            f.GetObject("layout", document.RootElement);
        });

        Assert.Equal("""{"columns":3}""", Observed(await UsageAsync(store, "layout"), "Web"));
    }

    [Fact]
    public async Task Apps_using_one_key_with_different_types_are_a_default_mismatch()
    {
        var store = new InMemoryFlagStore();
        await EvaluateInAsync(store, "Web", f => f.IsEnabled("checkout", true));
        await EvaluateInAsync(store, "Worker", f => f.GetString("checkout", "true"));

        Assert.True((await UsageAsync(store, "checkout")).HasDefaultMismatch);
    }

    [Fact]
    public async Task Equal_object_defaults_with_different_key_order_are_not_a_mismatch()
    {
        var store = new InMemoryFlagStore();
        await EvaluateInAsync(store, "Web", f => f.GetObject("layout", Json("""{ "columns": 3, "title": "Pics" }""")));
        await EvaluateInAsync(store, "Worker", f => f.GetObject("layout", Json("""{ "title": "Pics", "columns": 3 }""")));
        await EvaluateInAsync(store, "Jobs", f => f.GetObject("layout", new Layout(3, "Pics"), options: new JsonSerializerOptions(JsonSerializerDefaults.Web)));

        Assert.False((await UsageAsync(store, "layout")).HasDefaultMismatch);
    }

    [Fact]
    public async Task An_int_and_a_double_default_of_the_same_number_are_not_a_mismatch()
    {
        var store = new InMemoryFlagStore();
        await EvaluateInAsync(store, "Web", f => f.GetInt("page-size", 25));
        await EvaluateInAsync(store, "Worker", f => f.GetDouble("page-size", 25.0));

        Assert.False((await UsageAsync(store, "page-size")).HasDefaultMismatch);
    }

    [Fact]
    public void Stored_defaults_are_compared_by_content_whatever_their_key_order()
    {
        // A store (e.g. Postgres jsonb) may hand defaults back in its own key order and number format.
        var report = new FlagUsageReport("layout",
        [
            new FlagUsage("layout", "Web", FlagonTestHost.Epoch, Json("""{"columns":3,"title":"Pics"}""")),
            new FlagUsage("layout", "Worker", FlagonTestHost.Epoch, Json("""{ "title": "Pics", "columns": 3.0 }""")),
        ]);

        Assert.False(report.HasDefaultMismatch);
    }

    [Theory]
    [InlineData("9007199254740993", "9007199254740993.0", false)]
    [InlineData("9007199254740993", "90071992547409930e-1", false)]
    [InlineData("9007199254740993.0", "9007199254740992", true)]
    [InlineData("123456789012345678901234567890", "123456789012345678901234567891", true)]
    [InlineData("123456789012345678901234567890", "1.2345678901234567890123456789e29", false)]
    [InlineData("0.1", "0.10000000000000001", true)]
    [InlineData("-0.5", "-5e-1", false)]
    [InlineData("0", "-0.0", false)]
    [InlineData("1e30", "1000000000000000000000000000000.00", false)]
    [InlineData("1e-7", "0.00000010", false)]
    [InlineData("1e-7", "1e-8", true)]
    public void Stored_number_defaults_are_compared_exactly(string web, string worker, bool mismatch)
    {
        var report = new FlagUsageReport("ids",
        [
            new FlagUsage("ids", "Web", FlagonTestHost.Epoch, Json($$"""{"id":{{web}}}""")),
            new FlagUsage("ids", "Worker", FlagonTestHost.Epoch, Json($$"""{"id":{{worker}}}""")),
        ]);

        Assert.Equal(mismatch, report.HasDefaultMismatch);
    }

    [Fact]
    public async Task Recording_usage_of_a_seen_key_with_a_boolean_or_number_default_allocates_nothing()
    {
        await using var tracked = await FlagonTestHost.StartAsync();
        await using var untracked = await FlagonTestHost.StartAsync(o => o.Flagon = f => f.UsageTracking = false);

        Assert.Equal(AllocatedBy(untracked.Flagon), AllocatedBy(tracked.Flagon));

        static long AllocatedBy(IFlagon flagon)
        {
            Evaluate(flagon);
            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var i = 0; i < 100; i++)
            {
                Evaluate(flagon);
            }

            return GC.GetAllocatedBytesForCurrentThread() - before;
        }

        static void Evaluate(IFlagon flagon)
        {
            flagon.IsEnabled("checkout", true);
            flagon.GetInt("page-size", 25);
            flagon.GetDouble("ratio", 0.5);
        }
    }

    [Fact]
    public async Task A_type_mismatched_evaluation_still_records_usage()
    {
        var store = new InMemoryFlagStore();
        await store.TryCreateAsync("checkout", FlagDocument.Boolean(true, new FlagMetadata()));
        await EvaluateInAsync(store, "Worker", f => Assert.Equal(ErrorCode.TypeMismatch, f.GetStringDetails("checkout", "off").ErrorCode));

        Assert.Equal("\"off\"", Observed(await UsageAsync(store, "checkout"), "Worker"));
    }
}
