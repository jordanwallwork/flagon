using System.Collections.Concurrent;
using Jorrk.Flagon.Tests.Support;
using Microsoft.Extensions.Time.Testing;

namespace Jorrk.Flagon.Storage.SqlServer.Tests;

[Collection(SqlServerCollection.Name)]
public class SqlServerPushTests(SqlServerFixture db)
{
    private static readonly FlagDocument Document = FlagDocument.Boolean(true, new FlagMetadata());
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromMinutes(1);

    [Fact]
    public async Task Without_push_the_store_is_polled_only_and_reports_no_push_state()
    {
        var schema = SqlServerFixture.NewSchema();
        await using var app = await FlagonTestHost.StartAsync(o => o.Flagon = f => f.UseSqlServerStorage(db.PushConnectionString, schema));

        Assert.IsNotType<SqlServerPushFlagStore>(app.Services.GetService(typeof(IFlagStore)));
        Assert.Null(app.Status.IsPushConnected);
    }

    [Fact]
    public async Task A_write_in_one_host_reaches_another_host_on_the_same_database_long_before_the_refresh_interval()
    {
        var schema = SqlServerFixture.NewSchema();
        var time = new FakeTimeProvider(FlagonTestHost.Epoch);
        await using var appA = await StartHostAsync(db.PushConnectionString, schema, time, "AppA");
        await using var appB = await StartHostAsync(db.PushConnectionString, schema, time, "AppB");
        Assert.True(appB.Status.IsPushConnected);

        var created = await appA.Admin.CreateAsync(new CreateFlagRequest("search-v2"));
        await appA.Admin.SetEnabledAsync("search-v2", true, created.Version);
        Assert.False(appB.Flagon.IsEnabled("search-v2"));

        // The poll tick is a minute away on the fake clock, so only a push can deliver the change.
        var started = time.GetUtcNow();
        await WaitUntilAsync(() =>
        {
            time.Advance(TimeSpan.FromMilliseconds(100));
            return appB.Flagon.IsEnabled("search-v2");
        });

        Assert.True(time.GetUtcNow() - started < RefreshInterval);
    }

    [Fact]
    public async Task Killing_the_sqldependency_listener_session_does_not_lose_later_changes()
    {
        var connectionString = await db.CreateDatabaseAsync();
        var schema = SqlServerFixture.NewSchema();
        var writer = new SqlServerFlagStore(connectionString, schema);
        await writer.ApplyMigrationsAsync();
        using var store = new SqlServerPushFlagStore(connectionString, schema);
        var signals = new ConcurrentQueue<FlagChangeSignal>();
        using var stop = new CancellationTokenSource();
        await store.StartAsync(signals.Enqueue, stop.Token);

        await KillListenerSessionsAsync(connectionString);
        await writer.TryCreateAsync("checkout", Document);

        await WaitUntilAsync(() => signals.Any(s => !s.Resubscribed));
        Assert.True(store.IsConnected);
    }

    [Fact]
    public async Task Taking_the_database_offline_disconnects_then_reconnects_and_refreshes_a_write_made_in_the_gap()
    {
        var connectionString = await db.CreateDatabaseAsync();
        var database = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(connectionString).InitialCatalog;
        var schema = SqlServerFixture.NewSchema();
        var writer = new SqlServerFlagStore(connectionString, schema);
        await writer.ApplyMigrationsAsync();
        var time = new FakeTimeProvider(FlagonTestHost.Epoch);
        await using var app = await StartHostAsync(connectionString, schema, time, "AppA");
        Assert.True(app.Status.IsPushConnected);

        await db.ExecuteAsync($"ALTER DATABASE [{database}] SET OFFLINE WITH ROLLBACK IMMEDIATE");
        await WaitUntilAsync(() => app.Status.IsPushConnected == false);
        await db.ExecuteAsync($"ALTER DATABASE [{database}] SET ONLINE");

        // Written before the listener is back: no notification covers it, only the resubscribe refresh.
        await RetryAsync(() => writer.TryCreateAsync("late", Document));

        var started = time.GetUtcNow();
        await WaitUntilAsync(() =>
        {
            time.Advance(TimeSpan.FromMilliseconds(100));
            return app.Flagon.GetBooleanDetails("late").ErrorCode == ErrorCode.None;
        }, TimeSpan.FromSeconds(60));

        Assert.True(app.Status.IsPushConnected);
        Assert.True(time.GetUtcNow() - started < RefreshInterval);
    }

    [Fact]
    public async Task Without_service_broker_push_stays_disconnected_without_throwing_and_connects_once_it_is_enabled()
    {
        var connectionString = await db.CreateDatabaseAsync();
        var database = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(connectionString).InitialCatalog;
        var schema = SqlServerFixture.NewSchema();
        var writer = new SqlServerFlagStore(connectionString, schema);
        await writer.ApplyMigrationsAsync();
        await db.ExecuteAsync($"ALTER DATABASE [{database}] SET DISABLE_BROKER WITH ROLLBACK IMMEDIATE");
        using var store = new SqlServerPushFlagStore(connectionString, schema);
        var signals = new ConcurrentQueue<FlagChangeSignal>();
        using var stop = new CancellationTokenSource();

        await store.StartAsync(signals.Enqueue, stop.Token);
        Assert.False(store.IsConnected);

        await db.ExecuteAsync($"ALTER DATABASE [{database}] SET ENABLE_BROKER WITH ROLLBACK IMMEDIATE");
        await WaitUntilAsync(() => store.IsConnected, TimeSpan.FromSeconds(60));
        Assert.Contains(signals, s => s.Resubscribed);

        await writer.TryCreateAsync("checkout", Document);
        await WaitUntilAsync(() => signals.Any(s => !s.Resubscribed));
    }

    [Fact]
    public async Task Subscriptions_are_renewed_when_they_time_out()
    {
        var schema = SqlServerFixture.NewSchema();
        var writer = new SqlServerFlagStore(db.PushConnectionString, schema);
        await writer.ApplyMigrationsAsync();
        using var store = new SqlServerPushFlagStore(db.PushConnectionString, schema) { SubscriptionTimeout = TimeSpan.FromSeconds(1) };
        var signals = new ConcurrentQueue<FlagChangeSignal>();
        using var stop = new CancellationTokenSource();
        await store.StartAsync(signals.Enqueue, stop.Token);

        await Task.Delay(TimeSpan.FromSeconds(3.5));
        Assert.True(store.IsConnected);
        Assert.Empty(signals);

        await writer.TryCreateAsync("checkout", Document);
        await WaitUntilAsync(() => signals.Any(s => !s.Resubscribed));
    }

    [Fact]
    public async Task Stopping_releases_the_process_queue_and_no_callbacks_run_afterwards()
    {
        var connectionString = await db.CreateDatabaseAsync();
        var schema = SqlServerFixture.NewSchema();
        var writer = new SqlServerFlagStore(connectionString, schema);
        await writer.ApplyMigrationsAsync();
        using var store = new SqlServerPushFlagStore(connectionString, schema);
        var signals = new ConcurrentQueue<FlagChangeSignal>();
        using var stop = new CancellationTokenSource();
        await store.StartAsync(signals.Enqueue, stop.Token);
        Assert.Equal(1, await CountProcessQueuesAsync(connectionString));

        await stop.CancelAsync();
        await store.ListenersStopped;
        Assert.False(store.IsConnected);
        Assert.Equal(0, await CountProcessQueuesAsync(connectionString));

        await writer.TryCreateAsync("checkout", Document);
        await Task.Delay(TimeSpan.FromSeconds(1));
        Assert.Empty(signals);
    }

    private static Task<FlagonTestHost> StartHostAsync(string connectionString, string schema, FakeTimeProvider time, string application) =>
        FlagonTestHost.StartAsync(o =>
        {
            o.Time = time;
            o.ApplicationName = application;
            o.Flagon = f => { f.UseSqlServerStorage(connectionString, schema, push: true); f.RefreshInterval = RefreshInterval; };
        });

    /// <summary>Kills the sessions blocked in <c>WAITFOR (RECEIVE ...)</c> on the database: SqlDependency's listener.</summary>
    private async Task KillListenerSessionsAsync(string connectionString)
    {
        var database = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(connectionString).InitialCatalog;
        await db.ExecuteAsync($"""
            DECLARE @kill nvarchar(max) = N'';
            SELECT @kill += N'KILL ' + CAST(session_id AS nvarchar(10)) + N';'
            FROM sys.dm_exec_requests WHERE wait_type = 'BROKER_RECEIVE_WAITFOR' AND database_id = DB_ID('{database}');
            EXEC (@kill);
            """);
    }

    /// <summary>SqlDependency's per-process queues in the database.</summary>
    private static async Task<int> CountProcessQueuesAsync(string connectionString)
    {
        await using var connection = new Microsoft.Data.SqlClient.SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new Microsoft.Data.SqlClient.SqlCommand(
            "SELECT COUNT(*) FROM sys.service_queues WHERE name LIKE 'SqlQueryNotificationService-%'", connection);
        return (int)(await command.ExecuteScalarAsync())!;
    }

    private static async Task RetryAsync(Func<Task> action)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (true)
        {
            try
            {
                await action();
                return;
            }
            catch (Microsoft.Data.SqlClient.SqlException) when (DateTime.UtcNow < deadline)
            {
                // The database is still coming back online.
                await Task.Delay(100);
            }
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(10));
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "The condition did not become true in time.");
            await Task.Delay(10);
        }
    }
}
