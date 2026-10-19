using Jorrk.Flagon.Tests.Support;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Npgsql;

namespace Jorrk.Flagon.Storage.Postgres.Tests;

public class PostgresHostTests(PostgresFixture db) : IClassFixture<PostgresFixture>
{
    [Fact]
    public async Task UsePostgresStorage_creates_the_schema_on_startup_and_serves_flags_from_postgres()
    {
        var schema = PostgresFixture.NewSchema();
        await using var host = await FlagonTestHost.StartAsync(o => o.Flagon = f => f.UsePostgresStorage(db.ConnectionString, schema));

        Assert.IsType<PostgresFlagStore>(host.Store);
        Assert.Equal(1, await db.ScalarAsync($"SELECT MAX(version) FROM \"{schema}\".schema_version"));

        var created = await host.Admin.CreateAsync(new CreateFlagRequest("new-checkout"));
        Assert.False(host.Flagon.IsEnabled("new-checkout", defaultValue: true));

        await host.Admin.SetEnabledAsync("new-checkout", true, created.Version);
        Assert.True(host.Flagon.IsEnabled("new-checkout"));
    }

    [Fact]
    public async Task A_toggle_made_by_one_app_reaches_another_app_sharing_the_database_after_one_refresh()
    {
        var schema = PostgresFixture.NewSchema();
        var time = new FakeTimeProvider(FlagonTestHost.Epoch);
        await using var appA = await FlagonTestHost.StartAsync(o => { o.Time = time; o.ApplicationName = "AppA"; o.Flagon = f => f.UsePostgresStorage(db.ConnectionString, schema); });
        await using var appB = await FlagonTestHost.StartAsync(o => { o.Time = time; o.ApplicationName = "AppB"; o.Flagon = f => f.UsePostgresStorage(db.ConnectionString, schema); });

        var created = await appA.Admin.CreateAsync(new CreateFlagRequest("search-v2"));
        await appA.Admin.SetEnabledAsync("search-v2", true, created.Version);
        Assert.False(appB.Flagon.IsEnabled("search-v2"));

        await FlagonTestHost.AdvanceToNextRefreshAsync(appA, appB);

        Assert.True(appB.Flagon.IsEnabled("search-v2"));
    }

    [Fact]
    public async Task UsePostgresStorage_accepts_a_connection_string_name_from_configuration()
    {
        var schema = PostgresFixture.NewSchema();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:Flags"] = db.ConnectionString })
            .Build();

        await using var host = await FlagonTestHost.StartAsync(o =>
        {
            o.Flagon = f => f.UsePostgresStorage("Flags", schema);
            o.Services = s => s.AddSingleton<IConfiguration>(configuration);
        });

        await host.Admin.CreateAsync(new CreateFlagRequest("named", Enabled: true));
        Assert.True(host.Flagon.IsEnabled("named"));
    }

    [Fact]
    public async Task An_unknown_connection_string_name_fails_with_a_clear_error()
    {
        var error = await Assert.ThrowsAnyAsync<Exception>(() =>
            FlagonTestHost.StartAsync(o => o.Flagon = f => f.UsePostgresStorage("Missing")));

        Assert.Contains("'Missing'", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void A_blank_connection_string_is_rejected_when_storage_is_configured(string value) =>
        Assert.Throws<ArgumentException>(() => new FlagonOptions().UsePostgresStorage(value));

    [Fact]
    public async Task A_configured_connection_string_name_takes_precedence_over_reading_the_value_as_a_connection_string()
    {
        var schema = PostgresFixture.NewSchema();
        const string name = "Flags=Primary";
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { [$"ConnectionStrings:{name}"] = db.ConnectionString })
            .Build();

        await using var host = await FlagonTestHost.StartAsync(o =>
        {
            o.Flagon = f => f.UsePostgresStorage(name, schema);
            o.Services = s => s.AddSingleton<IConfiguration>(configuration);
        });

        await host.Admin.CreateAsync(new CreateFlagRequest("named", Enabled: true));
        Assert.True(host.Flagon.IsEnabled("named"));
    }

    [Fact]
    public async Task With_auto_migrate_off_startup_leaves_the_database_untouched()
    {
        var schema = PostgresFixture.NewSchema();
        await using var host = await FlagonTestHost.StartAsync(o => o.Flagon = f =>
        {
            f.UsePostgresStorage(db.ConnectionString, schema);
            f.AutoMigrate = false;
        });

        Assert.Equal(0L, await db.ScalarAsync($"SELECT COUNT(*) FROM information_schema.schemata WHERE schema_name = '{schema}'"));
        Assert.False(host.Status.IsStoreReachable);
    }

    [Fact]
    public async Task Exported_migration_scripts_run_by_a_dba_give_a_working_store_that_needs_no_further_migration()
    {
        var schema = PostgresFixture.NewSchema();
        foreach (var script in PostgresFlagStore.GetMigrationScripts(schema))
        {
            await db.ExecuteAsync(script.Sql);
        }

        await using var host = await FlagonTestHost.StartAsync(o => o.Flagon = f =>
        {
            f.UsePostgresStorage(db.ConnectionString, schema);
            f.AutoMigrate = false;
        });

        Assert.True(host.Status.IsStoreReachable);
        await host.Admin.CreateAsync(new CreateFlagRequest("dba-managed", Enabled: true));
        Assert.True(host.Flagon.IsEnabled("dba-managed"));

        await host.Store.ApplyMigrationsAsync();
        Assert.Equal(1L, await db.ScalarAsync($"SELECT COUNT(*) FROM \"{schema}\".schema_version"));
    }

    [Fact]
    public async Task Exported_scripts_can_be_run_twice()
    {
        var schema = PostgresFixture.NewSchema();
        var scripts = PostgresFlagStore.GetMigrationScripts(schema);
        Assert.Equal("0001_initial", scripts[0].Name);

        foreach (var script in scripts.Concat(scripts))
        {
            await db.ExecuteAsync(script.Sql);
        }

        Assert.Equal((long)scripts.Count, await db.ScalarAsync($"SELECT COUNT(*) FROM \"{schema}\".schema_version"));
    }

    [Fact]
    public async Task An_exported_script_that_fails_part_way_leaves_nothing_behind_and_can_be_retried()
    {
        var schema = PostgresFixture.NewSchema();
        // A stray table without the expected columns makes the migration fail part way through.
        await db.ExecuteAsync($"CREATE SCHEMA \"{schema}\"; CREATE TABLE \"{schema}\".flags (key text)");
        var script = PostgresFlagStore.GetMigrationScripts(schema)[0].Sql;

        await db.PsqlAsync(script);

        Assert.Null(await db.ScalarAsync($"SELECT to_regclass('\"{schema}\".schema_version')::text"));
        Assert.Null(await db.ScalarAsync($"SELECT to_regclass('\"{schema}\".change_sequence')::text"));

        await db.ExecuteAsync($"DROP TABLE \"{schema}\".flags");
        await db.PsqlAsync(script);

        Assert.Equal(1L, await db.ScalarAsync($"SELECT COUNT(*) FROM \"{schema}\".schema_version"));
        await using var store = new PostgresFlagStore(db.ConnectionString, schema);
        Assert.NotNull(await store.TryCreateAsync("works", FlagDocument.Boolean(true, new FlagMetadata())));
    }

    [Theory]
    [InlineData("")]
    [InlineData("1flags")]
    [InlineData("flags; DROP TABLE x")]
    [InlineData("has\"quote")]
    public void Invalid_schema_names_are_rejected(string schema)
    {
        Assert.Throws<ArgumentException>(() => new PostgresFlagStore(db.ConnectionString, schema));
    }

    [Fact]
    public async Task Create_if_absent_never_touches_an_archived_flag()
    {
        await using var store = new PostgresFlagStore(db.ConnectionString, PostgresFixture.NewSchema());
        await store.ApplyMigrationsAsync();
        var created = await store.TryCreateAsync("legacy", FlagDocument.Boolean(true, new FlagMetadata(Description: "original")));
        var archived = await store.ArchiveAsync("legacy", created!.Version);

        Assert.Null(await store.TryCreateAsync("legacy", FlagDocument.Boolean(false, new FlagMetadata(Description: "revived"))));

        var read = await store.GetAsync("legacy");
        Assert.True(read!.Archived);
        Assert.Equal(archived.Version, read.Version);
        Assert.Equal("original", read.Document.Metadata.Description);
    }

    [Fact]
    public async Task The_store_uses_the_flagon_schema_by_default()
    {
        await using var store = new PostgresFlagStore(new NpgsqlConnectionStringBuilder(db.ConnectionString).ConnectionString);
        Assert.Equal("flagon", store.Schema);
    }
}
