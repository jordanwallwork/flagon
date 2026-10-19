using Jorrk.Flagon.Storage.Conformance;
using Xunit;

namespace Jorrk.Flagon.Storage.SqlServer.Tests;

/// <summary>The full suite against the pushing store, notification cases included.</summary>
[Collection(SqlServerCollection.Name)]
public class SqlServerPushFlagStoreConformanceTests(SqlServerFixture db) : FlagStoreConformanceTests
{
    protected override Task<IFlagStore> CreateStoreAsync() =>
        Task.FromResult<IFlagStore>(new SqlServerPushFlagStore(db.PushConnectionString, SqlServerFixture.NewSchema()));

    protected override Task<IFlagStore?> CreateSecondInstanceAsync(IFlagStore store) =>
        Task.FromResult<IFlagStore?>(new SqlServerPushFlagStore(db.PushConnectionString, ((SqlServerFlagStore)store).Schema));
}
