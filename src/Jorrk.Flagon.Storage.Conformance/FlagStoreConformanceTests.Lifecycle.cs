using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace Jorrk.Flagon.Storage.Conformance;

public abstract partial class FlagStoreConformanceTests
{
    /// <summary>Conformance: a created flag can be read back by key and appears in LoadAll.</summary>
    [Fact]
    public Task A_created_flag_can_be_read_back() => WithStoreAsync(async store =>
    {
        var document = Flag(enabled: true, description: "New checkout");
        var created = await CreateAsync(store, "new-checkout", document);

        Assert.Equal("new-checkout", created.Key);
        Assert.False(created.Archived);
        AssertSameDocument(document, created.Document);

        var read = await store.GetAsync("new-checkout");
        Assert.NotNull(read);
        Assert.Equal(created.Version, read.Version);
        Assert.False(read.Archived);
        AssertSameDocument(document, read.Document);

        var all = Assert.Single(await store.LoadAllAsync());
        Assert.Equal("new-checkout", all.Key);
        Assert.Equal(created.Version, all.Version);
    });

    /// <summary>Conformance: create-if-absent does not overwrite an existing flag.</summary>
    [Fact]
    public Task Creating_an_existing_key_returns_null_and_leaves_the_flag_unchanged() => WithStoreAsync(async store =>
    {
        var original = await CreateAsync(store, "a", Flag(description: "original"));

        Assert.Null(await store.TryCreateAsync("a", Flag(enabled: true, description: "replacement")));

        var read = await store.GetAsync("a");
        Assert.Equal(original.Version, read!.Version);
        Assert.Equal("original", read.Document.Metadata.Description);
    });

    /// <summary>Conformance: an update replaces the document and returns the new version.</summary>
    [Fact]
    public Task An_update_with_the_current_version_replaces_the_document() => WithStoreAsync(async store =>
    {
        var created = await CreateAsync(store, "a", Flag(enabled: false));

        var updated = await store.UpdateAsync("a", Flag(enabled: true, description: "on now"), created.Version);

        Assert.True(updated.Version > created.Version);
        var read = await store.GetAsync("a");
        Assert.Equal(updated.Version, read!.Version);
        Assert.Equal(FlagDocument.OnVariant, read.Document.DefaultVariant);
        Assert.Equal("on now", read.Document.Metadata.Description);
    });

    /// <summary>Conformance: an update based on a stale version is rejected with the current version.</summary>
    [Fact]
    public Task An_update_with_a_stale_version_is_rejected_with_a_conflict() => WithStoreAsync(async store =>
    {
        var created = await CreateAsync(store, "a");
        var first = await store.UpdateAsync("a", Flag(enabled: true), created.Version);

        var conflict = await Assert.ThrowsAsync<FlagVersionConflictException>(() => store.UpdateAsync("a", Flag(description: "stale"), created.Version));

        Assert.Equal("a", conflict.Key);
        Assert.Equal(created.Version, conflict.ExpectedVersion);
        Assert.Equal(first.Version, conflict.CurrentVersion);
        var read = await store.GetAsync("a");
        Assert.Equal(first.Version, read!.Version);
        Assert.Equal(FlagDocument.OnVariant, read.Document.DefaultVariant);
    });

    /// <summary>Conformance: of two concurrent updates from the same version, exactly one wins.</summary>
    [Fact]
    public Task Of_concurrent_updates_from_the_same_version_exactly_one_wins() => WithStoreAsync(async store =>
    {
        var created = await CreateAsync(store, "a");

        var attempts = await Task.WhenAll(Enumerable.Range(0, 10).Select(i => Task.Run(async () =>
        {
            try
            {
                await store.UpdateAsync("a", Flag(description: $"writer {i}"), created.Version);
                return true;
            }
            catch (FlagVersionConflictException)
            {
                return false;
            }
        })));

        Assert.Equal(1, attempts.Count(won => won));
    });

    /// <summary>Conformance: writes to a missing key throw <see cref="FlagNotFoundException"/>.</summary>
    [Fact]
    public Task Writes_to_a_missing_key_throw_not_found() => WithStoreAsync(async store =>
    {
        Assert.Equal("missing", (await Assert.ThrowsAsync<FlagNotFoundException>(() => store.UpdateAsync("missing", Flag(), 1))).Key);
        await Assert.ThrowsAsync<FlagNotFoundException>(() => store.ArchiveAsync("missing", 1));
        await Assert.ThrowsAsync<FlagNotFoundException>(() => store.RestoreAsync("missing", 1));
        await Assert.ThrowsAsync<FlagNotFoundException>(() => store.PurgeAsync("missing", 1));
        Assert.Equal(0, await store.GetChangeVersionAsync());
    });

    /// <summary>Conformance: of many concurrent creates of one key, exactly one succeeds.</summary>
    [Fact]
    public Task Of_concurrent_creates_of_one_key_exactly_one_succeeds() => WithStoreAsync(async store =>
    {
        var results = await Task.WhenAll(Enumerable.Range(0, 20).Select(i =>
            Task.Run(() => store.TryCreateAsync("race", Flag(description: $"creator {i}"), Change(application: $"App{i}")))));

        var winner = Assert.Single(results, r => r is not null);
        var read = await store.GetAsync("race");
        Assert.Equal(winner!.Version, read!.Version);
        Assert.Equal(winner.Document.Metadata.Description, read.Document.Metadata.Description);
        Assert.Single(await store.LoadAllAsync());
        Assert.Single(await store.GetAuditAsync("race"));
    });

    /// <summary>Conformance: keys are case-sensitive.</summary>
    [Fact]
    public Task Keys_are_case_sensitive() => WithStoreAsync(async store =>
    {
        var lower = await CreateAsync(store, "checkout", Flag(description: "lower"));
        var upper = await CreateAsync(store, "Checkout", Flag(description: "upper"));

        Assert.Equal("lower", (await store.GetAsync("checkout"))!.Document.Metadata.Description);
        Assert.Equal("upper", (await store.GetAsync("Checkout"))!.Document.Metadata.Description);
        Assert.Null(await store.GetAsync("CHECKOUT"));
        Assert.Equal(["Checkout", "checkout"], (await store.LoadAllAsync()).Select(f => f.Key).Order(StringComparer.Ordinal));

        await store.UpdateAsync("Checkout", Flag(description: "upper 2"), upper.Version);
        Assert.Equal("lower", (await store.GetAsync("checkout"))!.Document.Metadata.Description);
        Assert.Equal(lower.Version, (await store.GetAsync("checkout"))!.Version);
    });

    /// <summary>Conformance: archiving keeps the flag, marked archived, in reads and LoadAll.</summary>
    [Fact]
    public Task An_archived_flag_is_still_loaded_with_its_archived_marker() => WithStoreAsync(async store =>
    {
        var created = await CreateAsync(store, "old", Flag(enabled: true, description: "old feature"));

        var archived = await store.ArchiveAsync("old", created.Version);

        Assert.True(archived.Archived);
        Assert.True(archived.Version > created.Version);
        Assert.Equal(archived.Version, await store.GetChangeVersionAsync());
        AssertSameDocument(created.Document, archived.Document);

        var read = await store.GetAsync("old");
        Assert.True(read!.Archived);
        Assert.Equal(archived.Version, read.Version);
        var loaded = Assert.Single(await store.LoadAllAsync());
        Assert.True(loaded.Archived);
        Assert.Equal(archived.Version, loaded.Version);
    });

    /// <summary>Conformance: create-if-absent never revives or changes an archived flag.</summary>
    [Fact]
    public Task Creating_an_archived_key_does_not_revive_it() => WithStoreAsync(async store =>
    {
        var created = await CreateAsync(store, "old", Flag(description: "original"));
        var archived = await store.ArchiveAsync("old", created.Version);

        Assert.Null(await store.TryCreateAsync("old", Flag(enabled: true, description: "auto")));

        var read = await store.GetAsync("old");
        Assert.True(read!.Archived);
        Assert.Equal(archived.Version, read.Version);
        Assert.Equal("original", read.Document.Metadata.Description);
        Assert.Equal(archived.Version, await store.GetChangeVersionAsync());
    });

    /// <summary>Conformance: a restored flag is no longer archived.</summary>
    [Fact]
    public Task A_restored_flag_is_no_longer_archived() => WithStoreAsync(async store =>
    {
        var created = await CreateAsync(store, "old", Flag(enabled: true));
        var archived = await store.ArchiveAsync("old", created.Version);

        var restored = await store.RestoreAsync("old", archived.Version);

        Assert.False(restored.Archived);
        Assert.True(restored.Version > archived.Version);
        Assert.Equal(restored.Version, await store.GetChangeVersionAsync());
        Assert.False((await store.GetAsync("old"))!.Archived);
        Assert.False(Assert.Single(await store.LoadAllAsync()).Archived);
        Assert.Equal(FlagDocument.OnVariant, restored.Document.DefaultVariant);
    });

    /// <summary>Conformance: an archived flag may be updated and stays archived.</summary>
    [Fact]
    public Task Updating_an_archived_flag_keeps_it_archived() => WithStoreAsync(async store =>
    {
        var created = await CreateAsync(store, "old");
        var archived = await store.ArchiveAsync("old", created.Version);

        var updated = await store.UpdateAsync("old", Flag(description: "why it was archived"), archived.Version);

        Assert.True(updated.Archived);
        Assert.True((await store.GetAsync("old"))!.Archived);
    });

    /// <summary>Conformance: archive, restore and purge are version-checked.</summary>
    [Fact]
    public Task Archive_restore_and_purge_with_a_stale_version_are_rejected_with_a_conflict() => WithStoreAsync(async store =>
    {
        var created = await CreateAsync(store, "a");
        var updated = await store.UpdateAsync("a", Flag(enabled: true), created.Version);

        var conflict = await Assert.ThrowsAsync<FlagVersionConflictException>(() => store.ArchiveAsync("a", created.Version));
        Assert.Equal(updated.Version, conflict.CurrentVersion);
        Assert.False((await store.GetAsync("a"))!.Archived);

        var archived = await store.ArchiveAsync("a", updated.Version);
        await Assert.ThrowsAsync<FlagVersionConflictException>(() => store.RestoreAsync("a", updated.Version));
        await Assert.ThrowsAsync<FlagVersionConflictException>(() => store.PurgeAsync("a", updated.Version));

        var read = await store.GetAsync("a");
        Assert.True(read!.Archived);
        Assert.Equal(archived.Version, read.Version);
    });

    /// <summary>Conformance: archive needs a live flag; restore and purge need an archived flag.</summary>
    [Fact]
    public Task Archive_restore_and_purge_reject_flags_in_the_wrong_state() => WithStoreAsync(async store =>
    {
        var live = await CreateAsync(store, "live");
        var old = await store.ArchiveAsync("old", (await CreateAsync(store, "old")).Version);
        var before = await store.GetChangeVersionAsync();

        await Assert.ThrowsAsync<FlagValidationException>(() => store.RestoreAsync("live", live.Version));
        await Assert.ThrowsAsync<FlagValidationException>(() => store.PurgeAsync("live", live.Version));
        await Assert.ThrowsAsync<FlagValidationException>(() => store.ArchiveAsync("old", old.Version));

        Assert.Equal(before, await store.GetChangeVersionAsync());
        Assert.NotNull(await store.GetAsync("live"));
        Assert.True((await store.GetAsync("old"))!.Archived);
    });

    /// <summary>Conformance: a purge removes the flag and is observable through the change version.</summary>
    [Fact]
    public Task A_purged_flag_is_gone_and_the_purge_moves_the_change_version() => WithStoreAsync(async store =>
    {
        var keep = await CreateAsync(store, "keep");
        var created = await CreateAsync(store, "old");
        var archived = await store.ArchiveAsync("old", created.Version);

        var purgeVersion = await store.PurgeAsync("old", archived.Version);

        Assert.True(purgeVersion > archived.Version);
        Assert.Equal(purgeVersion, await store.GetChangeVersionAsync());
        Assert.Null(await store.GetAsync("old"));
        var remaining = Assert.Single(await store.LoadAllAsync());
        Assert.Equal("keep", remaining.Key);
        Assert.Equal(keep.Version, remaining.Version);
    });

    /// <summary>Conformance: a purged key can be created again.</summary>
    [Fact]
    public Task A_purged_key_can_be_created_again() => WithStoreAsync(async store =>
    {
        var created = await CreateAsync(store, "old", Flag(description: "first life"));
        var archived = await store.ArchiveAsync("old", created.Version);
        var purgeVersion = await store.PurgeAsync("old", archived.Version);

        var recreated = await store.TryCreateAsync("old", Flag(description: "second life"));

        Assert.NotNull(recreated);
        Assert.False(recreated.Archived);
        Assert.True(recreated.Version > purgeVersion);
        Assert.Equal("second life", (await store.GetAsync("old"))!.Document.Metadata.Description);
    });

    /// <summary>Conformance: a flag document of any flag type round-trips with the same variants and targeting.</summary>
    [Theory]
    [InlineData("""{"light":"light","dark":"dark"}""", "dark")]
    [InlineData("""{"small":10,"large":250}""", "small")]
    [InlineData("""{"half":0.5,"tenth":0.1}""", "half")]
    [InlineData("""{"grid":{"columns":3,"title":"Pics","tags":["a","b"],"nested":{"z":1,"a":true}},"list":{"columns":1}}""", "grid")]
    public Task Typed_flag_documents_round_trip(string variants, string defaultVariant) => WithStoreAsync(async store =>
    {
        using var parsed = JsonDocument.Parse(variants);
        var document = new FlagDocument(
            FlagState.Disabled,
            parsed.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone(), StringComparer.Ordinal),
            defaultVariant,
            new FlagMetadata(Description: "typed", Tags: ["conformance"], Source: FlagSource.Code, CreatedAt: Epoch, UpdatedAt: Epoch));

        var created = await CreateAsync(store, "typed", document);
        AssertSameDocument(document, created.Document);

        var read = (await store.GetAsync("typed"))!.Document;
        AssertSameDocument(document, read);
        Assert.Equal(document.Type, read.Type);
        AssertSameDocument(document, Assert.Single(await store.LoadAllAsync()).Document);
    });

    /// <summary>
    /// Asserts two documents have the same flagd JSON, compared semantically: a store may reorder object keys (as
    /// Postgres <c>jsonb</c> does) or reformat whitespace.
    /// </summary>
    protected static void AssertSameDocument(FlagDocument expected, FlagDocument actual) =>
        AssertSameJson(FlagdJson.Serialize(expected), FlagdJson.Serialize(actual));

    /// <summary>Asserts two JSON texts are semantically equal (object key order and whitespace are ignored).</summary>
    protected static void AssertSameJson(string expected, string actual) =>
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(expected), JsonNode.Parse(actual)), $"Expected JSON {expected} but got {actual}.");
}
