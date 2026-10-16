using Xunit;

namespace Flagon.Storage.Conformance;

public abstract partial class FlagStoreConformanceTests
{
    /// <summary>Conformance: every audited write appends one entry with who/when/which app and before/after documents.</summary>
    [Fact]
    public Task Every_audited_write_appends_an_entry_describing_the_change() => WithStoreAsync(async store =>
    {
        var v1 = Flag(enabled: false, description: "v1");
        var v2 = Flag(enabled: true, description: "v2");
        var created = await store.TryCreateAsync("a", v1, Change("alice", "AppA", minutes: 1));
        var updated = await store.UpdateAsync("a", v2, created!.Version, Change("bob", "AppB", minutes: 2));
        var archived = await store.ArchiveAsync("a", updated.Version, Change("carol", "AppA", minutes: 3));
        var restored = await store.RestoreAsync("a", archived.Version, Change("dave", "AppB", minutes: 4));
        var rearchived = await store.ArchiveAsync("a", restored.Version, Change("erin", "AppA", minutes: 5));
        var purged = await store.PurgeAsync("a", rearchived.Version, Change("frank", "AppC", minutes: 6));

        var audit = await store.GetAuditAsync("a");

        Assert.Equal(
            [FlagAuditAction.Created, FlagAuditAction.Updated, FlagAuditAction.Archived, FlagAuditAction.Restored, FlagAuditAction.Archived, FlagAuditAction.Purged],
            audit.Select(e => e.Action));
        Assert.Equal([created.Version, updated.Version, archived.Version, restored.Version, rearchived.Version, purged], audit.Select(e => e.Version));
        Assert.Equal(["alice", "bob", "carol", "dave", "erin", "frank"], audit.Select(e => e.Actor));
        Assert.Equal(["AppA", "AppB", "AppA", "AppB", "AppA", "AppC"], audit.Select(e => e.ApplicationName));
        Assert.Equal(Enumerable.Range(1, 6).Select(m => Epoch.AddMinutes(m)), audit.Select(e => e.Timestamp));
        Assert.All(audit, e => Assert.Equal("a", e.Key));
        Assert.True(audit.Zip(audit.Skip(1)).All(p => p.Second.Id > p.First.Id), "Audit ids must increase.");

        Assert.Null(audit[0].Before);
        AssertSameDocument(v1, audit[0].After!);
        AssertSameDocument(v1, audit[1].Before!);
        AssertSameDocument(v2, audit[1].After!);
        AssertSameDocument(v2, audit[2].Before!);
        AssertSameDocument(v2, audit[2].After!);
        AssertSameDocument(v2, audit[5].Before!);
        Assert.Null(audit[5].After);
    });

    /// <summary>Conformance: audit entries are per key and keys are case-sensitive.</summary>
    [Fact]
    public Task Audit_history_is_read_per_key() => WithStoreAsync(async store =>
    {
        await store.TryCreateAsync("a", Flag(), Change());
        await store.TryCreateAsync("A", Flag(), Change());
        await store.TryCreateAsync("b", Flag(), Change());

        Assert.Equal("a", Assert.Single(await store.GetAuditAsync("a")).Key);
        Assert.Equal("A", Assert.Single(await store.GetAuditAsync("A")).Key);
        Assert.Empty(await store.GetAuditAsync("missing"));
    });

    /// <summary>Conformance: writes without a change context are not audited.</summary>
    [Fact]
    public Task Writes_without_a_change_context_are_not_audited() => WithStoreAsync(async store =>
    {
        var created = await CreateAsync(store, "a");
        await store.UpdateAsync("a", Flag(enabled: true), created.Version);

        Assert.Empty(await store.GetAuditAsync("a"));
    });

    /// <summary>Conformance: rejected writes leave no audit entry (the entry is atomic with the change).</summary>
    [Fact]
    public Task Rejected_writes_leave_no_audit_entry() => WithStoreAsync(async store =>
    {
        var created = await store.TryCreateAsync("a", Flag(), Change());
        await store.UpdateAsync("a", Flag(enabled: true), created!.Version, Change());

        Assert.Null(await store.TryCreateAsync("a", Flag(), Change("mallory")));
        await Assert.ThrowsAsync<FlagVersionConflictException>(() => store.UpdateAsync("a", Flag(), created.Version, Change("mallory")));
        await Assert.ThrowsAsync<FlagVersionConflictException>(() => store.ArchiveAsync("a", created.Version, Change("mallory")));
        await Assert.ThrowsAsync<FlagNotFoundException>(() => store.UpdateAsync("missing", Flag(), 1, Change("mallory")));

        var audit = await store.GetAuditAsync("a");
        Assert.Equal([FlagAuditAction.Created, FlagAuditAction.Updated], audit.Select(e => e.Action));
        Assert.DoesNotContain(audit, e => e.Actor == "mallory");
        Assert.Empty(await store.GetAuditAsync("missing"));
    });

    /// <summary>Conformance: under concurrent conflicting writes, the audit log records exactly the writes that won.</summary>
    [Fact]
    public Task Concurrent_conflicting_writes_audit_only_the_winner() => WithStoreAsync(async store =>
    {
        var created = await store.TryCreateAsync("a", Flag(), Change());

        var winners = await Task.WhenAll(Enumerable.Range(0, 10).Select(i => Task.Run(async () =>
        {
            try
            {
                return await store.UpdateAsync("a", Flag(description: $"writer {i}"), created!.Version, Change($"writer-{i}"));
            }
            catch (FlagVersionConflictException)
            {
                return null;
            }
        })));

        var winner = Assert.Single(winners, w => w is not null)!;
        var update = Assert.Single(await store.GetAuditAsync("a"), e => e.Action == FlagAuditAction.Updated);
        Assert.Equal(winner.Version, update.Version);
        Assert.Equal(winner.Document.Metadata.Description, update.After!.Metadata.Description);
    });

    /// <summary>Conformance: audit history outlives a purge, and continues if the key is re-created.</summary>
    [Fact]
    public Task Audit_history_survives_a_purge() => WithStoreAsync(async store =>
    {
        var created = await store.TryCreateAsync("a", Flag(), Change());
        var archived = await store.ArchiveAsync("a", created!.Version, Change());
        await store.PurgeAsync("a", archived.Version, Change());
        await store.TryCreateAsync("a", Flag(), Change());

        Assert.Equal(
            [FlagAuditAction.Created, FlagAuditAction.Archived, FlagAuditAction.Purged, FlagAuditAction.Created],
            (await store.GetAuditAsync("a")).Select(e => e.Action));
    });
}
