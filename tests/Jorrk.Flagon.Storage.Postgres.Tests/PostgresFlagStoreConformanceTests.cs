using Jorrk.Flagon.Storage.Conformance;

namespace Jorrk.Flagon.Storage.Postgres.Tests;

public class PostgresFlagStoreConformanceTests(PostgresFixture db) : FlagStoreConformanceTests, IClassFixture<PostgresFixture>
{
    protected override Task<IFlagStore> CreateStoreAsync() =>
        Task.FromResult<IFlagStore>(new PostgresFlagStore(db.ConnectionString, PostgresFixture.NewSchema()));

    protected override Task<IFlagStore?> CreateSecondInstanceAsync(IFlagStore store) =>
        Task.FromResult<IFlagStore?>(new PostgresFlagStore(db.ConnectionString, ((PostgresFlagStore)store).Schema));

    protected override async ValueTask DisposeStoreAsync(IFlagStore store)
    {
        await base.DisposeStoreAsync(store);
        await db.DropSchemaAsync(((PostgresFlagStore)store).Schema);
    }
}
