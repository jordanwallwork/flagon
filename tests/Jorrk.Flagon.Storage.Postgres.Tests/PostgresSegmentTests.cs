using System.Text.Json;
using Jorrk.Flagon.Tests.Support;

namespace Jorrk.Flagon.Storage.Postgres.Tests;

public class PostgresSegmentTests(PostgresFixture db) : IClassFixture<PostgresFixture>
{
    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

    [Fact]
    public async Task Upgrading_a_version_1_schema_with_data_keeps_it_and_adds_segments_on_the_same_change_sequence()
    {
        var schema = PostgresFixture.NewSchema();
        try
        {
            await db.ExecuteAsync(PostgresFlagStore.GetMigrationScripts(schema)[0].Sql);
            await using var store = new PostgresFlagStore(db.ConnectionString, schema);
            var flag = await store.TryCreateAsync("checkout", FlagDocument.Boolean(true, new FlagMetadata()), new FlagChangeContext(DateTimeOffset.UnixEpoch, "alice"));

            await store.ApplyMigrationsAsync();
            await store.ApplyMigrationsAsync();

            Assert.Equal(2L, await db.ScalarAsync($"SELECT COUNT(*) FROM \"{schema}\".schema_version"));
            Assert.Equal(flag!.Version, await store.GetChangeVersionAsync());
            Assert.Equal(flag.Version, (await store.GetAsync("checkout"))!.Version);
            Assert.Single(await store.GetAuditAsync("checkout"));

            var segment = await store.TryCreateSegmentAsync("staff", new SegmentDocument(Json("""{"==":[{"var":"team"},"staff"]}"""), new FlagMetadata()));
            Assert.True(segment!.Version > flag.Version);
            Assert.Equal(segment.Version, await store.GetChangeVersionAsync());
        }
        finally
        {
            await db.DropSchemaAsync(schema);
        }
    }

    [Fact]
    public async Task A_segment_edit_in_one_app_changes_who_a_flag_serves_in_another_after_its_next_refresh()
    {
        var schema = PostgresFixture.NewSchema();
        try
        {
            await using var appA = await FlagonTestHost.StartAsync(o => o.Flagon = f => f
                .UsePostgresStorage(db.ConnectionString, schema)
                .DefineSegment("staff", """{"in":[{"var":"targetingKey"},["alice"]]}""")
                .Define("beta", targeting: """{"if":[{"$ref":"staff"},"on",null]}"""));
            await using var appB = await FlagonTestHost.StartAsync(o => o.Flagon = f => f.UsePostgresStorage(db.ConnectionString, schema));
            Assert.True(appB.Flagon.IsEnabled("beta", context: new EvaluationContext("alice")));
            Assert.False(appB.Flagon.IsEnabled("beta", context: new EvaluationContext("bob")));

            var staff = (await appA.Admin.GetSegmentAsync("staff"))!;
            await appA.Admin.SetSegmentRuleAsync("staff", Json("""{"in":[{"var":"targetingKey"},["bob"]]}"""), staff.Version);
            await appB.AdvanceToNextRefreshAsync();

            Assert.False(appB.Flagon.IsEnabled("beta", context: new EvaluationContext("alice")));
            Assert.True(appB.Flagon.IsEnabled("beta", context: new EvaluationContext("bob")));
        }
        finally
        {
            await db.DropSchemaAsync(schema);
        }
    }
}
