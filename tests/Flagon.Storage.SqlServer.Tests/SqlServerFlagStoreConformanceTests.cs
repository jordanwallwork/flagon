using Flagon.Storage.Conformance;

namespace Flagon.Storage.SqlServer.Tests;

[Collection(SqlServerCollection.Name)]
public class SqlServerFlagStoreConformanceTests(SqlServerFixture db) : FlagStoreConformanceTests
{
    protected override Task<IFlagStore> CreateStoreAsync() =>
        Task.FromResult<IFlagStore>(new SqlServerFlagStore(db.ConnectionString, SqlServerFixture.NewSchema()));
}
