using System.Text.Json;
using Xunit;

namespace Flagon.Storage.Conformance;

public abstract partial class FlagStoreConformanceTests
{
    private static readonly JsonElement True = JsonSerializer.SerializeToElement(true);
    private static readonly JsonElement False = JsonSerializer.SerializeToElement(false);

    /// <summary>Conformance: usage is recorded per (key, application).</summary>
    [Fact]
    public Task Usage_is_recorded_per_flag_and_application() => WithStoreAsync(async store =>
    {
        await store.UpsertUsageAsync(
        [
            new FlagUsage("checkout", "AppA", Epoch.AddMinutes(1), True),
            new FlagUsage("checkout", "AppB", Epoch.AddMinutes(2), False),
            new FlagUsage("search", "AppA", Epoch.AddMinutes(3), True),
        ]);

        var checkout = (await store.GetUsageAsync("checkout")).OrderBy(u => u.ApplicationName, StringComparer.Ordinal).ToArray();
        Assert.Equal(2, checkout.Length);
        AssertUsage(checkout[0], "checkout", "AppA", Epoch.AddMinutes(1), true);
        AssertUsage(checkout[1], "checkout", "AppB", Epoch.AddMinutes(2), false);
        AssertUsage(Assert.Single(await store.GetUsageAsync("search")), "search", "AppA", Epoch.AddMinutes(3), true);
        Assert.Empty(await store.GetUsageAsync("missing"));
        Assert.Equal(3, (await store.LoadAllUsageAsync()).Count);
    });

    /// <summary>Conformance: a later batch replaces the record; an out-of-order older one does not.</summary>
    [Fact]
    public Task A_usage_upsert_never_moves_last_evaluated_backwards() => WithStoreAsync(async store =>
    {
        await store.UpsertUsageAsync([new FlagUsage("checkout", "AppA", Epoch.AddMinutes(1), False)]);
        await store.UpsertUsageAsync([new FlagUsage("checkout", "AppA", Epoch.AddMinutes(5), True)]);
        await store.UpsertUsageAsync([new FlagUsage("checkout", "AppA", Epoch.AddMinutes(3), False)]);

        AssertUsage(Assert.Single(await store.GetUsageAsync("checkout")), "checkout", "AppA", Epoch.AddMinutes(5), true);
    });

    /// <summary>Conformance: usage keys are case-sensitive, need not exist as flags, and do not move the change version.</summary>
    [Fact]
    public Task Usage_keys_are_case_sensitive_and_do_not_move_the_change_version() => WithStoreAsync(async store =>
    {
        await CreateAsync(store, "checkout");
        var before = await store.GetChangeVersionAsync();

        await store.UpsertUsageAsync(
        [
            new FlagUsage("checkout", "AppA", Epoch, True),
            new FlagUsage("Checkout", "AppA", Epoch.AddMinutes(1), False),
            new FlagUsage("never-created", "AppA", Epoch, null),
        ]);

        Assert.Equal(before, await store.GetChangeVersionAsync());
        AssertUsage(Assert.Single(await store.GetUsageAsync("checkout")), "checkout", "AppA", Epoch, true);
        AssertUsage(Assert.Single(await store.GetUsageAsync("Checkout")), "Checkout", "AppA", Epoch.AddMinutes(1), false);
        Assert.Null(Assert.Single(await store.GetUsageAsync("never-created")).ObservedDefault);
    });

    /// <summary>Conformance: an observed default of any flag type round-trips as the same JSON.</summary>
    [Theory]
    [InlineData("\"dark\"")]
    [InlineData("25")]
    [InlineData("0.5")]
    [InlineData("""{"columns":3,"tags":["a"],"title":"Pics"}""")]
    public Task Typed_observed_defaults_round_trip(string json) => WithStoreAsync(async store =>
    {
        using var observed = JsonDocument.Parse(json);
        await store.UpsertUsageAsync([new FlagUsage("typed", "AppA", Epoch, observed.RootElement)]);

        var usage = Assert.Single(await store.GetUsageAsync("typed"));
        Assert.Equal(json, usage.ObservedDefault!.Value.GetRawText());
    });

    /// <summary>Conformance: concurrent usage batches for the same keys converge on the latest record.</summary>
    [Fact]
    public Task Concurrent_usage_batches_converge_on_the_latest_record() => WithStoreAsync(async store =>
    {
        await Task.WhenAll(Enumerable.Range(1, 20).Select(i => Task.Run(() => store.UpsertUsageAsync(
        [
            new FlagUsage("a", "AppA", Epoch.AddMinutes(i), JsonSerializer.SerializeToElement(i % 2 == 0)),
            new FlagUsage("b", "AppA", Epoch.AddMinutes(i), null),
        ]))));

        AssertUsage(Assert.Single(await store.GetUsageAsync("a")), "a", "AppA", Epoch.AddMinutes(20), true);
        Assert.Equal(Epoch.AddMinutes(20), Assert.Single(await store.GetUsageAsync("b")).LastEvaluatedAt);
    });

    /// <summary>Conformance: purging a flag removes its usage records.</summary>
    [Fact]
    public Task Purging_a_flag_removes_its_usage() => WithStoreAsync(async store =>
    {
        var created = await CreateAsync(store, "old");
        await store.UpsertUsageAsync([new FlagUsage("old", "AppA", Epoch, True), new FlagUsage("keep", "AppA", Epoch, True)]);
        var archived = await store.ArchiveAsync("old", created.Version);

        Assert.Single(await store.GetUsageAsync("old"));
        await store.PurgeAsync("old", archived.Version);

        Assert.Empty(await store.GetUsageAsync("old"));
        Assert.Single(await store.GetUsageAsync("keep"));
    });

    private static void AssertUsage(FlagUsage usage, string key, string application, DateTimeOffset lastEvaluatedAt, bool observedDefault)
    {
        Assert.Equal(key, usage.Key);
        Assert.Equal(application, usage.ApplicationName);
        Assert.Equal(lastEvaluatedAt, usage.LastEvaluatedAt);
        Assert.NotNull(usage.ObservedDefault);
        Assert.Equal(observedDefault ? JsonValueKind.True : JsonValueKind.False, usage.ObservedDefault.Value.ValueKind);
    }
}
