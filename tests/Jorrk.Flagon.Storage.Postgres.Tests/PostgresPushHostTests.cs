using Jorrk.Flagon.Tests.Support;
using Microsoft.Extensions.Time.Testing;
using Npgsql;

namespace Jorrk.Flagon.Storage.Postgres.Tests;

public class PostgresPushHostTests(PostgresFixture db) : IClassFixture<PostgresFixture>
{
    [Fact]
    public async Task A_write_in_one_host_reaches_another_host_on_the_same_database_long_before_the_refresh_interval()
    {
        var schema = PostgresFixture.NewSchema();
        var time = new FakeTimeProvider(FlagonTestHost.Epoch);
        var refreshInterval = TimeSpan.FromMinutes(1);
        await using var appA = await FlagonTestHost.StartAsync(o =>
        {
            o.Time = time;
            o.ApplicationName = "AppA";
            o.Flagon = f => { f.UsePostgresStorage(db.ConnectionString, schema); f.RefreshInterval = refreshInterval; };
        });
        await using var appB = await FlagonTestHost.StartAsync(o =>
        {
            o.Time = time;
            o.ApplicationName = "AppB";
            o.Flagon = f => { f.UsePostgresStorage(db.ConnectionString, schema); f.RefreshInterval = refreshInterval; };
        });

        var created = await appA.Admin.CreateAsync(new CreateFlagRequest("search-v2"));
        await appA.Admin.SetEnabledAsync("search-v2", true, created.Version);
        Assert.False(appB.Flagon.IsEnabled("search-v2"));

        // Advance the fake clock in small steps. The poll tick is a minute away, so only a push can deliver the change.
        var started = time.GetUtcNow();
        for (var step = 0; step < 100 && !appB.Flagon.IsEnabled("search-v2"); step++)
        {
            time.Advance(TimeSpan.FromMilliseconds(100));
            await Task.Delay(10);
        }

        Assert.True(appB.Flagon.IsEnabled("search-v2"));
        Assert.True(time.GetUtcNow() - started < refreshInterval);
    }

    [Fact]
    public async Task A_write_made_while_the_listener_is_down_reaches_the_snapshot_after_it_reconnects_long_before_the_refresh_interval()
    {
        var schema = PostgresFixture.NewSchema();
        await using var writer = new PostgresFlagStore(db.ConnectionString, schema);
        await writer.ApplyMigrationsAsync();
        var time = new FakeTimeProvider(FlagonTestHost.Epoch);
        var refreshInterval = TimeSpan.FromMinutes(1);
        await using var app = await FlagonTestHost.StartAsync(o =>
        {
            o.Time = time;
            o.Flagon = f => { f.UsePostgresStorage(db.ConnectionString, schema); f.RefreshInterval = refreshInterval; };
        });
        Assert.True(app.Status.IsPushConnected);

        var pid = Convert.ToInt32(await db.ScalarAsync(
            $"SELECT pid FROM pg_stat_activity WHERE query = 'LISTEN \"flagon_{schema}\"' AND pid <> pg_backend_pid()"));
        await db.ExecuteAsync($"SELECT pg_terminate_backend({pid})");
        await WaitUntilAsync(() => app.Status.IsPushConnected == false);

        // Written in the gap: no signal reaches the app for this one.
        await writer.TryCreateAsync("late", FlagDocument.Boolean(true, new FlagMetadata()));

        var started = time.GetUtcNow();
        await WaitUntilAsync(() =>
        {
            time.Advance(TimeSpan.FromMilliseconds(100));
            return app.Flagon.GetBooleanDetails("late").ErrorCode == ErrorCode.None;
        });

        Assert.True(app.Status.IsPushConnected);
        Assert.True(time.GetUtcNow() - started < refreshInterval);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "The condition did not become true in time.");
            await Task.Delay(10);
        }
    }
}
