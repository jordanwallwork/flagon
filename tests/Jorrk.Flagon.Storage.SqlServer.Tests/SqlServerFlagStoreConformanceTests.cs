using Jorrk.Flagon.Storage.Conformance;
using Xunit;

namespace Jorrk.Flagon.Storage.SqlServer.Tests;

[Collection(SqlServerCollection.Name)]
public class SqlServerFlagStoreConformanceTests(SqlServerFixture db) : FlagStoreConformanceTests
{
    protected override Task<IFlagStore> CreateStoreAsync() =>
        Task.FromResult<IFlagStore>(new SqlServerFlagStore(db.ConnectionString, SqlServerFixture.NewSchema()));

    [Fact(Skip = "The polled SQL Server store has no IFlagChangeNotifier; SqlServerPushFlagStoreConformanceTests covers push.")]
    public override Task Each_lifecycle_write_signals_the_notifier() => Task.CompletedTask;

    [Fact(Skip = "The polled SQL Server store has no IFlagChangeNotifier; SqlServerPushFlagStoreConformanceTests covers push.")]
    public override Task A_write_through_a_second_instance_signals_the_first() => Task.CompletedTask;

    [Fact(Skip = "The polled SQL Server store has no IFlagChangeNotifier; SqlServerPushFlagStoreConformanceTests covers push.")]
    public override Task Usage_upserts_do_not_signal() => Task.CompletedTask;

    [Fact(Skip = "The polled SQL Server store has no IFlagChangeNotifier; SqlServerPushFlagStoreConformanceTests covers push.")]
    public override Task No_callbacks_run_after_the_notifier_is_stopped() => Task.CompletedTask;

    [Fact(Skip = "The polled SQL Server store has no IFlagChangeNotifier; SqlServerPushFlagStoreConformanceTests covers push.")]
    public override Task Under_concurrent_writes_the_change_version_converges_to_the_final_version() => Task.CompletedTask;

    [Fact(Skip = "The polled SQL Server store has no IFlagChangeNotifier; SqlServerPushFlagStoreConformanceTests covers push.")]
    public override Task Rejected_writes_leave_the_notifier_contract_intact() => Task.CompletedTask;
}
