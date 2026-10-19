using System.Collections.Concurrent;
using System.Text.Json;
using Xunit;

namespace Jorrk.Flagon.Storage.Conformance;

public abstract partial class FlagStoreConformanceTests
{
    /// <summary>
    /// Returns the <see cref="ISegmentStore"/> to exercise for <paramref name="store"/>, or null when the store has none.
    /// Defaults to the store itself when it implements the capability. When this is null the segment cases are skipped,
    /// so stores without segments need no overrides.
    /// </summary>
    protected virtual ISegmentStore? AsSegmentStore(IFlagStore store) => store as ISegmentStore;

    /// <summary>A segment document with the given description, matching targeting keys in <paramref name="keys"/>.</summary>
    protected static SegmentDocument Segment(string? description = null, params string[] keys)
    {
        var rule = JsonSerializer.SerializeToElement(new Dictionary<string, object>
        {
            ["in"] = new object[] { new Dictionary<string, string> { ["var"] = "targetingKey" }, keys.Length == 0 ? new[] { "alice" } : keys },
        });
        return new SegmentDocument(rule, new FlagMetadata(Description: description, Tags: ["conformance"], Source: FlagSource.Manual, CreatedAt: Epoch, UpdatedAt: Epoch));
    }

    /// <summary>Asserts two segment documents are the same (rule compared as JSON, metadata as stored).</summary>
    protected static void AssertSameSegment(SegmentDocument expected, SegmentDocument actual) =>
        AssertSameJson(SegmentJson.Serialize(expected), SegmentJson.Serialize(actual));

    /// <summary>Conformance (segments): a created segment can be read back by key and in the full listing.</summary>
    [SkippableFact]
    public virtual Task A_created_segment_can_be_read_back() => WithSegmentStoreAsync(async (store, segments) =>
    {
        var document = Segment("beta testers", "alice", "bob");
        var created = await segments.TryCreateSegmentAsync("beta", document);

        Assert.NotNull(created);
        Assert.Equal("beta", created.Key);
        Assert.False(created.Archived);
        AssertSameSegment(document, created.Document);

        var read = await segments.GetSegmentAsync("beta");
        Assert.NotNull(read);
        Assert.Equal(created.Version, read.Version);
        AssertSameSegment(document, read.Document);
        Assert.Equal("beta", Assert.Single(await segments.LoadAllSegmentsAsync()).Key);
        Assert.Null(await segments.GetSegmentAsync("missing"));
    });

    /// <summary>Conformance (segments): each segment write moves the change version to the version it was assigned.</summary>
    [SkippableFact]
    public virtual Task Each_segment_write_moves_the_change_version() => WithSegmentStoreAsync(async (store, segments) =>
    {
        var created = (await segments.TryCreateSegmentAsync("beta", Segment()))!;
        Assert.Equal(created.Version, await store.GetChangeVersionAsync());

        var updated = await segments.UpdateSegmentAsync("beta", Segment("v2"), created.Version);
        Assert.True(updated.Version > created.Version);
        Assert.Equal(updated.Version, await store.GetChangeVersionAsync());

        var archived = await segments.ArchiveSegmentAsync("beta", updated.Version);
        Assert.True(archived.Archived);
        Assert.Equal(archived.Version, await store.GetChangeVersionAsync());

        var restored = await segments.RestoreSegmentAsync("beta", archived.Version);
        Assert.False(restored.Archived);
        Assert.Equal(restored.Version, await store.GetChangeVersionAsync());

        var rearchived = await segments.ArchiveSegmentAsync("beta", restored.Version);
        var purged = await segments.PurgeSegmentAsync("beta", rearchived.Version);
        Assert.True(purged > rearchived.Version);
        Assert.Equal(purged, await store.GetChangeVersionAsync());
        Assert.Null(await segments.GetSegmentAsync("beta"));
        Assert.Empty(await segments.LoadAllSegmentsAsync());
    });

    /// <summary>Conformance (segments): flag and segment writes take strictly increasing versions from one sequence.</summary>
    [SkippableFact]
    public virtual Task Flag_and_segment_writes_share_one_sequence() => WithSegmentStoreAsync(async (store, segments) =>
    {
        var versions = new List<long>();
        var flag = await CreateAsync(store, "checkout");
        versions.Add(flag.Version);
        var segment = (await segments.TryCreateSegmentAsync("beta", Segment()))!;
        versions.Add(segment.Version);
        flag = await store.UpdateAsync("checkout", Flag(enabled: true), flag.Version);
        versions.Add(flag.Version);
        segment = await segments.UpdateSegmentAsync("beta", Segment("v2"), segment.Version);
        versions.Add(segment.Version);
        segment = await segments.ArchiveSegmentAsync("beta", segment.Version);
        versions.Add(segment.Version);
        flag = await store.ArchiveAsync("checkout", flag.Version);
        versions.Add(flag.Version);
        versions.Add(await segments.PurgeSegmentAsync("beta", segment.Version));
        versions.Add(await store.PurgeAsync("checkout", flag.Version));

        for (var i = 1; i < versions.Count; i++)
        {
            Assert.True(versions[i] > versions[i - 1], $"Write {i} got version {versions[i]}, not greater than {versions[i - 1]}.");
        }

        Assert.Equal(versions[^1], await store.GetChangeVersionAsync());
    });

    /// <summary>
    /// Conformance (segments): not found, version conflict and already-exists behave as for flags, archive state is
    /// checked after the version, and no rejected write moves the change version.
    /// </summary>
    [SkippableFact]
    public virtual Task Rejected_segment_writes_change_nothing() => WithSegmentStoreAsync(async (store, segments) =>
    {
        var created = (await segments.TryCreateSegmentAsync("beta", Segment("kept")))!;
        var version = await store.GetChangeVersionAsync();

        Assert.Null(await segments.TryCreateSegmentAsync("beta", Segment("other")));
        var conflict = await Assert.ThrowsAsync<SegmentVersionConflictException>(() => segments.UpdateSegmentAsync("beta", Segment(), created.Version + 99));
        Assert.Equal("beta", conflict.Key);
        Assert.Equal(created.Version + 99, conflict.ExpectedVersion);
        Assert.Equal(created.Version, conflict.CurrentVersion);
        await Assert.ThrowsAsync<SegmentVersionConflictException>(() => segments.ArchiveSegmentAsync("beta", created.Version + 99));
        await Assert.ThrowsAsync<SegmentVersionConflictException>(() => segments.PurgeSegmentAsync("beta", created.Version + 99));
        Assert.Equal("missing", (await Assert.ThrowsAsync<SegmentNotFoundException>(() => segments.UpdateSegmentAsync("missing", Segment(), 1))).Key);
        await Assert.ThrowsAsync<SegmentNotFoundException>(() => segments.ArchiveSegmentAsync("missing", 1));
        await Assert.ThrowsAsync<SegmentNotFoundException>(() => segments.RestoreSegmentAsync("missing", 1));
        await Assert.ThrowsAsync<SegmentNotFoundException>(() => segments.PurgeSegmentAsync("missing", 1));
        await Assert.ThrowsAsync<FlagValidationException>(() => segments.RestoreSegmentAsync("beta", created.Version));
        await Assert.ThrowsAsync<FlagValidationException>(() => segments.PurgeSegmentAsync("beta", created.Version));

        Assert.Equal(version, await store.GetChangeVersionAsync());
        var read = await segments.GetSegmentAsync("beta");
        Assert.Equal(created.Version, read!.Version);
        Assert.Equal("kept", read.Document.Metadata.Description);

        var archived = await segments.ArchiveSegmentAsync("beta", created.Version);
        await Assert.ThrowsAsync<FlagValidationException>(() => segments.ArchiveSegmentAsync("beta", archived.Version));
        Assert.Equal(archived.Version, await store.GetChangeVersionAsync());
    });

    /// <summary>Conformance (segments): an archived segment can be updated and stays archived; a purged key can be created afresh.</summary>
    [SkippableFact]
    public virtual Task Archived_segments_can_be_updated_and_purged_keys_reused() => WithSegmentStoreAsync(async (store, segments) =>
    {
        var created = (await segments.TryCreateSegmentAsync("beta", Segment()))!;
        var archived = await segments.ArchiveSegmentAsync("beta", created.Version);

        Assert.Null(await segments.TryCreateSegmentAsync("beta", Segment()));
        var updated = await segments.UpdateSegmentAsync("beta", Segment("while archived"), archived.Version);
        Assert.True(updated.Archived);
        Assert.True((await segments.GetSegmentAsync("beta"))!.Archived);
        Assert.Contains(await segments.LoadAllSegmentsAsync(), s => s.Key == "beta" && s.Archived);

        await segments.PurgeSegmentAsync("beta", updated.Version);
        var recreated = await segments.TryCreateSegmentAsync("beta", Segment("again"));
        Assert.NotNull(recreated);
        Assert.False(recreated.Archived);
    });

    /// <summary>Conformance (segments): flag and segment keys are independent namespaces, and segment keys are case-sensitive.</summary>
    [SkippableFact]
    public virtual Task Flag_and_segment_keys_are_independent() => WithSegmentStoreAsync(async (store, segments) =>
    {
        await store.TryCreateAsync("shared", Flag(), Change());
        Assert.NotNull(await segments.TryCreateSegmentAsync("shared", Segment(), Change()));
        Assert.NotNull(await segments.TryCreateSegmentAsync("Shared", Segment(), Change()));

        Assert.NotNull(await store.GetAsync("shared"));
        Assert.Null(await store.GetAsync("Shared"));
        Assert.Equal(["Shared", "shared"], (await segments.LoadAllSegmentsAsync()).Select(s => s.Key).Order(StringComparer.Ordinal));
        Assert.Single(await store.LoadAllAsync());
        Assert.Equal(FlagAuditAction.Created, Assert.Single(await store.GetAuditAsync("shared")).Action);
        Assert.Equal(FlagAuditAction.Created, Assert.Single(await segments.GetSegmentAuditAsync("shared")).Action);
        Assert.Single(await segments.GetSegmentAuditAsync("Shared"));
    });

    /// <summary>Conformance (segments): every audited write appends an entry atomically with the change, kept after a purge.</summary>
    [SkippableFact]
    public virtual Task Every_audited_segment_write_appends_an_entry_that_survives_a_purge() => WithSegmentStoreAsync(async (store, segments) =>
    {
        var v1 = Segment("v1", "alice");
        var v2 = Segment("v2", "bob");
        var created = await segments.TryCreateSegmentAsync("beta", v1, Change("alice", "AppA", minutes: 1));
        var updated = await segments.UpdateSegmentAsync("beta", v2, created!.Version, Change("bob", "AppB", minutes: 2));
        var archived = await segments.ArchiveSegmentAsync("beta", updated.Version, Change("carol", "AppA", minutes: 3));
        var restored = await segments.RestoreSegmentAsync("beta", archived.Version, Change("dave", "AppB", minutes: 4));
        var rearchived = await segments.ArchiveSegmentAsync("beta", restored.Version, Change("erin", "AppA", minutes: 5));
        var purged = await segments.PurgeSegmentAsync("beta", rearchived.Version, Change("frank", "AppC", minutes: 6));

        var audit = await segments.GetSegmentAuditAsync("beta");

        Assert.Equal(
            [FlagAuditAction.Created, FlagAuditAction.Updated, FlagAuditAction.Archived, FlagAuditAction.Restored, FlagAuditAction.Archived, FlagAuditAction.Purged],
            audit.Select(e => e.Action));
        Assert.Equal([created.Version, updated.Version, archived.Version, restored.Version, rearchived.Version, purged], audit.Select(e => e.Version));
        Assert.Equal(["alice", "bob", "carol", "dave", "erin", "frank"], audit.Select(e => e.Actor));
        Assert.Equal(["AppA", "AppB", "AppA", "AppB", "AppA", "AppC"], audit.Select(e => e.ApplicationName));
        Assert.Equal(Enumerable.Range(1, 6).Select(m => Epoch.AddMinutes(m)), audit.Select(e => e.Timestamp));
        Assert.All(audit, e => Assert.Equal("beta", e.Key));
        Assert.True(audit.Zip(audit.Skip(1)).All(p => p.Second.Id > p.First.Id), "Audit ids must increase.");

        Assert.Null(audit[0].Before);
        AssertSameSegment(v1, audit[0].After!);
        AssertSameSegment(v1, audit[1].Before!);
        AssertSameSegment(v2, audit[1].After!);
        AssertSameSegment(v2, audit[2].Before!);
        AssertSameSegment(v2, audit[2].After!);
        AssertSameSegment(v2, audit[5].Before!);
        Assert.Null(audit[5].After);
        Assert.Empty(await store.GetAuditAsync("beta"));
    });

    /// <summary>Conformance (segments): writes without a change context, and rejected writes, are not audited.</summary>
    [SkippableFact]
    public virtual Task Unaudited_and_rejected_segment_writes_leave_no_audit_entry() => WithSegmentStoreAsync(async (store, segments) =>
    {
        var created = (await segments.TryCreateSegmentAsync("beta", Segment()))!;
        await segments.UpdateSegmentAsync("beta", Segment("v2"), created.Version);
        await Assert.ThrowsAsync<SegmentVersionConflictException>(() => segments.UpdateSegmentAsync("beta", Segment("v3"), created.Version, Change()));
        Assert.Null(await segments.TryCreateSegmentAsync("beta", Segment(), Change()));

        Assert.Empty(await segments.GetSegmentAuditAsync("beta"));
    });

    /// <summary>Conformance (segments): a consistent load returns every flag and segment with the version it reflects.</summary>
    [SkippableFact]
    public virtual Task A_consistent_load_returns_flags_and_segments_at_its_version() => WithSegmentStoreAsync(async (store, segments) =>
    {
        var empty = await segments.LoadAllWithSegmentsAsync();
        Assert.Equal(0, empty.Version);
        Assert.Empty(empty.Flags);
        Assert.Empty(empty.Segments);

        await CreateAsync(store, "checkout");
        var beta = (await segments.TryCreateSegmentAsync("beta", Segment()))!;
        await segments.ArchiveSegmentAsync("beta", beta.Version);
        await segments.TryCreateSegmentAsync("staff", Segment());
        var version = await store.GetChangeVersionAsync();

        var contents = await segments.LoadAllWithSegmentsAsync();

        Assert.Equal(version, contents.Version);
        Assert.Equal("checkout", Assert.Single(contents.Flags).Key);
        Assert.Equal(["beta", "staff"], contents.Segments.Select(s => s.Key).Order(StringComparer.Ordinal));
        Assert.True(contents.Segments.Single(s => s.Key == "beta").Archived);
    });

    /// <summary>
    /// Conformance (segments): under concurrent flag and segment writes, every consistent load holds exactly the state at
    /// its version (every write numbered up to it, none after) and its version covers every write completed before it began.
    /// Best effort: a store that tears loads may pass by luck, but never one that is consistent and fails.
    /// </summary>
    [SkippableFact]
    public virtual Task Consistent_loads_under_concurrent_writes_match_the_state_at_their_version() => WithSegmentStoreAsync(async (store, segments) =>
    {
        // Every write's (kind, key, version), recorded once it completes.
        var writes = new ConcurrentBag<(string Kind, string Key, long Version)>();
        var loads = new ConcurrentBag<(long CompletedBefore, FlagStoreContents Contents)>();
        using var stop = new CancellationTokenSource();

        var writers = Enumerable.Range(0, 4).Select(w => Task.Run(async () =>
        {
            for (var i = 0; i < 8; i++)
            {
                var flag = (await store.TryCreateAsync($"flag-{w}-{i}", Flag()))!;
                writes.Add(("flag", flag.Key, flag.Version));
                var segment = (await segments.TryCreateSegmentAsync($"segment-{w}-{i}", Segment()))!;
                writes.Add(("segment", segment.Key, segment.Version));
                var updated = await segments.UpdateSegmentAsync(segment.Key, Segment("v2"), segment.Version);
                writes.Add(("segment", updated.Key, updated.Version));
                if (i % 2 == 0)
                {
                    var archived = await segments.ArchiveSegmentAsync(segment.Key, updated.Version);
                    var purged = await segments.PurgeSegmentAsync(segment.Key, archived.Version);
                    writes.Add(("segment", segment.Key, archived.Version));
                    writes.Add(("segment-purge", segment.Key, purged));
                }
            }
        })).ToArray();

        var loaders = Enumerable.Range(0, 2).Select(_ => Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                var completed = writes.IsEmpty ? 0 : writes.Max(w => w.Version);
                loads.Add((completed, await segments.LoadAllWithSegmentsAsync()));
            }
        })).ToArray();

        await Task.WhenAll(writers);
        await stop.CancelAsync();
        await Task.WhenAll(loaders);
        loads.Add((writes.Max(w => w.Version), await segments.LoadAllWithSegmentsAsync()));

        var all = writes.ToArray();
        foreach (var (completedBefore, contents) in loads)
        {
            Assert.True(contents.Version >= completedBefore, $"A load at version {contents.Version} missed a write completed before it began ({completedBefore}).");
            Assert.Equal(StateAt(all, "flag", contents.Version), contents.Flags.ToDictionary(f => f.Key, f => f.Version));
            Assert.Equal(StateAt(all, "segment", contents.Version), contents.Segments.ToDictionary(s => s.Key, s => s.Version));
        }
    });

    /// <summary>Conformance (segments): re-applying migrations keeps existing segments and their audit.</summary>
    [SkippableFact]
    public virtual Task Applying_migrations_again_keeps_segments() => WithSegmentStoreAsync(async (store, segments) =>
    {
        var created = await segments.TryCreateSegmentAsync("beta", Segment("kept"), Change());
        var version = await store.GetChangeVersionAsync();

        await store.ApplyMigrationsAsync();
        await store.ApplyMigrationsAsync();

        Assert.Equal(version, await store.GetChangeVersionAsync());
        var read = await segments.GetSegmentAsync("beta");
        Assert.Equal(created!.Version, read!.Version);
        Assert.Equal("kept", read.Document.Metadata.Description);
        Assert.Single(await segments.GetSegmentAuditAsync("beta"));
    });

    /// <summary>Conformance (segments, notifier): each segment write signals the notifier, as flag writes do.</summary>
    [SkippableFact]
    public virtual Task Each_segment_write_signals_the_notifier() => WithSegmentStoreAsync(async (store, segments) =>
    {
        var notifier = AsNotifier(store);
        Skip.If(notifier is null, "The store does not implement IFlagChangeNotifier; it is polled only.");
        await using var listener = await SignalListener.StartAsync(notifier!);

        var created = await SignalAfter(listener, "segment create", async () => (await segments.TryCreateSegmentAsync("beta", Segment()))!);
        var updated = await SignalAfter(listener, "segment update", () => segments.UpdateSegmentAsync("beta", Segment("v2"), created.Version));
        var archived = await SignalAfter(listener, "segment archive", () => segments.ArchiveSegmentAsync("beta", updated.Version));
        var restored = await SignalAfter(listener, "segment restore", () => segments.RestoreSegmentAsync("beta", archived.Version));
        var rearchived = await SignalAfter(listener, "segment re-archive", () => segments.ArchiveSegmentAsync("beta", restored.Version));
        await SignalAfter(listener, "segment purge", () => segments.PurgeSegmentAsync("beta", rearchived.Version));
    });

    /// <summary>The live keys of <paramref name="kind"/> at <paramref name="version"/>, each with the version of its last write up to it.</summary>
    private static Dictionary<string, long> StateAt((string Kind, string Key, long Version)[] writes, string kind, long version)
    {
        var state = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var write in writes.Where(w => w.Version <= version).OrderBy(w => w.Version))
        {
            if (write.Kind == kind)
            {
                state[write.Key] = write.Version;
            }
            else if (write.Kind == kind + "-purge")
            {
                state.Remove(write.Key);
            }
        }

        return state;
    }

    /// <summary>Runs a segment case, or skips it (reported as skipped, never passed) when the store has no segments.</summary>
    private async Task WithSegmentStoreAsync(Func<IFlagStore, ISegmentStore, Task> test) => await WithStoreAsync(store =>
    {
        var segments = AsSegmentStore(store);
        Skip.If(segments is null, "The store does not implement ISegmentStore; it has no segments.");
        return test(store, segments!);
    });
}
