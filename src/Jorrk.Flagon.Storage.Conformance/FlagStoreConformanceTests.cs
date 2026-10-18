using Xunit;

namespace Jorrk.Flagon.Storage.Conformance;

/// <summary>
/// The <see cref="IFlagStore"/> conformance suite. Derive from this class in an xUnit test project and implement
/// <see cref="CreateStoreAsync"/>; every inherited <c>[Fact]</c> then runs against your provider.
/// </summary>
/// <remarks>
/// <para>Each test calls <see cref="CreateStoreAsync"/> for a store over fresh, empty, un-migrated storage (for a
/// database provider: a new database or schema), then calls <see cref="IFlagStore.ApplyMigrationsAsync"/> itself.</para>
/// <para>The suite never reads the wall clock; all timestamps it writes are fixed values.</para>
/// </remarks>
public abstract partial class FlagStoreConformanceTests
{
    /// <summary>A fixed instant used for audit and usage timestamps written by the suite.</summary>
    protected static readonly DateTimeOffset Epoch = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// Creates a store over fresh, empty storage that no other test uses. Migrations need not have been applied; the
    /// suite applies them. If the store implements <see cref="IAsyncDisposable"/> or <see cref="IDisposable"/> it is
    /// disposed at the end of the test (see <see cref="DisposeStoreAsync"/>).
    /// </summary>
    protected abstract Task<IFlagStore> CreateStoreAsync();

    /// <summary>Releases a store created by <see cref="CreateStoreAsync"/>. Override to also drop its storage.</summary>
    protected virtual async ValueTask DisposeStoreAsync(IFlagStore store)
    {
        switch (store)
        {
            case IAsyncDisposable asyncDisposable:
                await asyncDisposable.DisposeAsync().ConfigureAwait(false);
                break;
            case IDisposable disposable:
                disposable.Dispose();
                break;
        }
    }

    /// <summary>Creates a migrated store, runs <paramref name="test"/> against it, then disposes it.</summary>
    protected async Task WithStoreAsync(Func<IFlagStore, Task> test)
    {
        ArgumentNullException.ThrowIfNull(test);
        var store = await CreateStoreAsync().ConfigureAwait(false);
        try
        {
            await store.ApplyMigrationsAsync().ConfigureAwait(false);
            await test(store).ConfigureAwait(false);
        }
        finally
        {
            await DisposeStoreAsync(store).ConfigureAwait(false);
        }
    }

    /// <summary>A boolean flag document with the given description.</summary>
    protected static FlagDocument Flag(bool enabled = false, string? description = null) =>
        FlagDocument.Boolean(enabled, new FlagMetadata(Description: description, Tags: ["conformance"], Source: FlagSource.Manual, CreatedAt: Epoch, UpdatedAt: Epoch));

    /// <summary>A change context stamped with <see cref="Epoch"/> plus <paramref name="minutes"/>.</summary>
    protected static FlagChangeContext Change(string actor = "alice", string application = "AppA", int minutes = 0) =>
        new(Epoch.AddMinutes(minutes), actor, application);

    /// <summary>Creates a flag that must not already exist.</summary>
    protected static async Task<FlagRecord> CreateAsync(IFlagStore store, string key, FlagDocument? document = null, FlagChangeContext? change = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        var record = await store.TryCreateAsync(key, document ?? Flag(), change).ConfigureAwait(false);
        Assert.NotNull(record);
        return record;
    }
}
