using System.Text.Json;
using Jorrk.Flagon.Tests.Support;
using Microsoft.Data.SqlClient;

namespace Jorrk.Flagon.Storage.SqlServer.Tests;

[Collection(SqlServerCollection.Name)]
public class SqlServerSegmentTests(SqlServerFixture db)
{
    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

    [Fact]
    public async Task Upgrading_a_version_1_schema_with_data_keeps_it_and_adds_segments_on_the_same_change_counter()
    {
        var schema = SqlServerFixture.NewSchema();
        await using (var connection = new SqlConnection(db.ConnectionString))
        {
            // The bootstrap and migration 1 only, as an app on the previous release would have installed them.
            await connection.OpenAsync();
            foreach (var batch in SqlServerMigrations.GetBatches(schema).Take(2))
            {
                await using var command = new SqlCommand(batch, connection);
                await command.ExecuteNonQueryAsync();
            }
        }

        var store = new SqlServerFlagStore(db.ConnectionString, schema);
        var flag = await store.TryCreateAsync("checkout", FlagDocument.Boolean(true, new FlagMetadata()), new FlagChangeContext(DateTimeOffset.UnixEpoch, "alice"));
        Assert.Equal(1, await db.ScalarAsync<int>($"SELECT COUNT(*) FROM [{schema}].[schema_version]"));

        await store.ApplyMigrationsAsync();
        await store.ApplyMigrationsAsync();

        Assert.Equal(2, await db.ScalarAsync<int>($"SELECT COUNT(*) FROM [{schema}].[schema_version]"));
        Assert.Equal(flag!.Version, await store.GetChangeVersionAsync());
        Assert.Equal(flag.Version, (await store.GetAsync("checkout"))!.Version);
        Assert.Single(await store.GetAuditAsync("checkout"));

        var segment = await store.TryCreateSegmentAsync("staff", new SegmentDocument(Json("""{"==":[{"var":"team"},"staff"]}"""), new FlagMetadata()));
        Assert.True(segment!.Version > flag.Version);
        Assert.Equal(segment.Version, await store.GetChangeVersionAsync());
    }

    [Fact]
    public async Task A_segment_edit_in_one_app_changes_who_a_flag_serves_in_another_after_its_next_refresh()
    {
        var schema = SqlServerFixture.NewSchema();
        await using var appA = await FlagonTestHost.StartAsync(o => o.Flagon = f => f
            .UseSqlServerStorage(db.ConnectionString, schema: schema)
            .DefineSegment("staff", """{"in":[{"var":"targetingKey"},["alice"]]}""")
            .Define("beta", targeting: """{"if":[{"$ref":"staff"},"on",null]}"""));
        await using var appB = await FlagonTestHost.StartAsync(o => o.Flagon = f => f.UseSqlServerStorage(db.ConnectionString, schema: schema));
        Assert.True(appB.Flagon.IsEnabled("beta", context: new EvaluationContext("alice")));
        Assert.False(appB.Flagon.IsEnabled("beta", context: new EvaluationContext("bob")));

        var staff = (await appA.Admin.GetSegmentAsync("staff"))!;
        await appA.Admin.SetSegmentRuleAsync("staff", Json("""{"in":[{"var":"targetingKey"},["bob"]]}"""), staff.Version);
        await appB.AdvanceToNextRefreshAsync();

        Assert.False(appB.Flagon.IsEnabled("beta", context: new EvaluationContext("alice")));
        Assert.True(appB.Flagon.IsEnabled("beta", context: new EvaluationContext("bob")));
    }
}
