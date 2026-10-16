using Xunit;

namespace Flagon.Storage.Conformance;

public abstract partial class FlagStoreConformanceTests
{
    /// <summary>Conformance: an empty store reports change version 0 and no flags.</summary>
    [Fact]
    public Task An_empty_store_has_change_version_zero_and_no_flags() => WithStoreAsync(async store =>
    {
        Assert.Equal(0, await store.GetChangeVersionAsync());
        Assert.Empty(await store.LoadAllAsync());
        Assert.Null(await store.GetAsync("missing"));
    });

    /// <summary>Conformance: every write takes a strictly greater version from one store-wide sequence.</summary>
    [Fact]
    public Task Every_write_takes_a_strictly_increasing_version_from_one_store_wide_sequence() => WithStoreAsync(async store =>
    {
        var versions = new List<long>();

        var a = await CreateAsync(store, "a");
        versions.Add(a.Version);
        var b = await CreateAsync(store, "b");
        versions.Add(b.Version);
        a = await store.UpdateAsync("a", Flag(enabled: true), a.Version);
        versions.Add(a.Version);
        a = await store.ArchiveAsync("a", a.Version);
        versions.Add(a.Version);
        a = await store.RestoreAsync("a", a.Version);
        versions.Add(a.Version);
        b = await store.ArchiveAsync("b", b.Version);
        versions.Add(b.Version);
        var purgeVersion = await store.PurgeAsync("b", b.Version);
        versions.Add(purgeVersion);

        Assert.True(versions[0] > 0);
        for (var i = 1; i < versions.Count; i++)
        {
            Assert.True(versions[i] > versions[i - 1], $"Write {i} got version {versions[i]}, not greater than {versions[i - 1]}.");
        }

        Assert.Equal(purgeVersion, await store.GetChangeVersionAsync());
    });

    /// <summary>Conformance: the change version equals the version of the latest write.</summary>
    [Fact]
    public Task The_change_version_is_the_version_of_the_latest_write() => WithStoreAsync(async store =>
    {
        var a = await CreateAsync(store, "a");
        Assert.Equal(a.Version, await store.GetChangeVersionAsync());

        var b = await CreateAsync(store, "b");
        Assert.Equal(b.Version, await store.GetChangeVersionAsync());

        a = await store.UpdateAsync("a", Flag(enabled: true), a.Version);
        Assert.Equal(a.Version, await store.GetChangeVersionAsync());
    });

    /// <summary>Conformance: concurrent writes to different keys all get distinct versions.</summary>
    [Fact]
    public Task Concurrent_writes_get_distinct_versions() => WithStoreAsync(async store =>
    {
        var created = await Task.WhenAll(Enumerable.Range(0, 20).Select(i => Task.Run(() => store.TryCreateAsync($"flag-{i}", Flag()))));

        var versions = created.Select(r => r!.Version).ToArray();
        Assert.Equal(versions.Length, versions.Distinct().Count());
        Assert.Equal(versions.Max(), await store.GetChangeVersionAsync());
    });

    /// <summary>Conformance: a failed write (conflict, existing key) does not move the change version.</summary>
    [Fact]
    public Task Rejected_writes_do_not_move_the_change_version() => WithStoreAsync(async store =>
    {
        var a = await CreateAsync(store, "a");
        await store.UpdateAsync("a", Flag(enabled: true), a.Version);
        var before = await store.GetChangeVersionAsync();

        Assert.Null(await store.TryCreateAsync("a", Flag()));
        await Assert.ThrowsAsync<FlagVersionConflictException>(() => store.UpdateAsync("a", Flag(), a.Version));

        Assert.Equal(before, await store.GetChangeVersionAsync());
    });
}
