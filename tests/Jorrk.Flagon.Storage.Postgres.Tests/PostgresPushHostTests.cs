using Jorrk.Flagon.Tests.Support;
using Microsoft.Extensions.Time.Testing;

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
}
