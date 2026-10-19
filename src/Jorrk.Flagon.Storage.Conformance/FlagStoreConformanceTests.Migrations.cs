using Xunit;

namespace Jorrk.Flagon.Storage.Conformance;

public abstract partial class FlagStoreConformanceTests
{
    /// <summary>Conformance: re-applying migrations is a no-op that keeps existing data.</summary>
    [Fact]
    public Task Applying_migrations_again_keeps_existing_data() => WithStoreAsync(async store =>
    {
        var created = await store.TryCreateAsync("a", Flag(description: "kept"), Change());
        await store.UpsertUsageAsync([new FlagUsage("a", "AppA", Epoch)]);
        var version = await store.GetChangeVersionAsync();

        await store.ApplyMigrationsAsync();
        await store.ApplyMigrationsAsync();

        Assert.Equal(version, await store.GetChangeVersionAsync());
        var read = await store.GetAsync("a");
        Assert.Equal(created!.Version, read!.Version);
        Assert.Equal("kept", read.Document.Metadata.Description);
        Assert.Single(await store.GetAuditAsync("a"));
        Assert.Single(await store.GetUsageAsync("a"));
    });

    /// <summary>Conformance: concurrent migrations of fresh storage are serialised and leave a working store.</summary>
    [Fact]
    public async Task Concurrent_migrations_of_fresh_storage_leave_a_working_store()
    {
        var store = await CreateStoreAsync();
        try
        {
            await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => Task.Run(() => store.ApplyMigrationsAsync())));

            var created = await store.TryCreateAsync("a", Flag(), Change());
            Assert.NotNull(created);
            Assert.Equal(created.Version, await store.GetChangeVersionAsync());
        }
        finally
        {
            await DisposeStoreAsync(store);
        }
    }
}
