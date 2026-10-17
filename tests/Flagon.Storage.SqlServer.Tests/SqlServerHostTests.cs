using Flagon.Tests.Support;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Flagon.Storage.SqlServer.Tests;

[Collection(SqlServerCollection.Name)]
public class SqlServerHostTests(SqlServerFixture db)
{
    [Fact]
    public async Task Flagon_creates_its_schema_on_startup_and_serves_flags_from_sql_server()
    {
        var schema = SqlServerFixture.NewSchema();
        await using var appA = await FlagonTestHost.StartAsync(o => o.Flagon = f => f.UseSqlServerStorage(db.ConnectionString, schema: schema));

        Assert.Equal(1, await db.ScalarAsync<int>($"SELECT MAX([version]) FROM [{schema}].[schema_version]"));
        Assert.True(appA.Status.IsLoaded);
        Assert.True(appA.Flagon.IsEnabled("checkout", defaultValue: true));

        var created = await appA.Admin.CreateAsync(new CreateFlagRequest("checkout", Enabled: false));
        Assert.False(appA.Flagon.IsEnabled("checkout", defaultValue: true));

        await appA.Admin.SetEnabledAsync("checkout", true, created.Version);
        Assert.True(appA.Flagon.IsEnabled("checkout"));

        await using var appB = await FlagonTestHost.StartAsync(o => o.Flagon = f => f.UseSqlServerStorage(db.ConnectionString, schema: schema));
        Assert.True(appB.Flagon.IsEnabled("checkout"));
        Assert.False(appB.Flagon.IsEnabled("Checkout"));
    }

    [Fact]
    public async Task Flags_defined_in_code_are_seeded_into_a_freshly_migrated_schema()
    {
        var schema = SqlServerFixture.NewSchema();

        await using var app = await FlagonTestHost.StartAsync(o => o.Flagon = f => f
            .UseSqlServerStorage(db.ConnectionString, schema: schema)
            .Define("new-search", defaultValue: true, description: "Search v2"));

        Assert.True(app.Flagon.IsEnabled("new-search"));
        Assert.Equal("Search v2", (await app.Store.GetAsync("new-search"))!.Document.Metadata.Description);
    }

    [Fact]
    public async Task The_connection_string_can_be_given_by_name_from_configuration()
    {
        var schema = SqlServerFixture.NewSchema();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:Flags"] = db.ConnectionString })
            .Build();

        await using var app = await FlagonTestHost.StartAsync(o =>
        {
            o.Services = s => s.AddSingleton<IConfiguration>(configuration);
            o.Flagon = f => f.UseSqlServerStorage("Flags", schema: schema);
        });

        await app.Admin.CreateAsync(new CreateFlagRequest("named", Enabled: true));
        Assert.True(app.Flagon.IsEnabled("named"));
        Assert.Equal(schema, Assert.IsType<SqlServerFlagStore>(app.Store).Schema);
    }

    [Fact]
    public async Task A_missing_connection_string_name_is_reported_clearly()
    {
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            FlagonTestHost.StartAsync(o => o.Flagon = f => f.UseSqlServerStorage("NoSuchConnection")));

        Assert.Contains("NoSuchConnection", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void A_blank_connection_string_is_rejected_when_storage_is_configured(string value) =>
        Assert.Throws<ArgumentException>(() => new FlagonOptions().UseSqlServerStorage(value));

    [Fact]
    public async Task With_auto_migration_off_a_dba_can_install_the_schema_from_the_exported_script()
    {
        var schema = SqlServerFixture.NewSchema();
        await using (var unmigrated = await FlagonTestHost.StartAsync(o => o.Flagon = f =>
        {
            f.AutoMigrate = false;
            f.UseSqlServerStorage(db.ConnectionString, schema: schema);
        }))
        {
            Assert.False(unmigrated.Status.IsLoaded);
        }

        var script = SqlServerMigrations.GenerateScript(schema);
        await RunScriptAsync(script);
        await RunScriptAsync(script);

        await using var app = await FlagonTestHost.StartAsync(o => o.Flagon = f =>
        {
            f.AutoMigrate = false;
            f.UseSqlServerStorage(db.ConnectionString, schema: schema);
        });
        Assert.True(app.Status.IsLoaded);
        await app.Admin.CreateAsync(new CreateFlagRequest("installed-by-dba", Enabled: true));
        Assert.True(app.Flagon.IsEnabled("installed-by-dba"));
        Assert.Equal(1, await db.ScalarAsync<int>($"SELECT COUNT(*) FROM [{schema}].[schema_version]"));
    }

    [Fact]
    public async Task An_exported_script_that_fails_part_way_leaves_nothing_behind_and_can_be_retried()
    {
        var schema = SqlServerFixture.NewSchema();
        // A stray table where the migration wants to create one makes it fail part way through.
        await RunScriptAsync($"CREATE SCHEMA [{schema}]\nGO\nCREATE TABLE [{schema}].[flags] ([x] int)");
        var script = SqlServerMigrations.GenerateScript(schema);

        await Assert.ThrowsAsync<SqlException>(() => RunScriptAsync(script));

        Assert.Null(await db.ScalarAsync<string>($"SELECT OBJECT_NAME(OBJECT_ID(N'[{schema}].[change_sequence]'))"));
        Assert.Equal(0, await db.ScalarAsync<int>($"SELECT COUNT(*) FROM [{schema}].[schema_version]"));

        await RunScriptAsync($"DROP TABLE [{schema}].[flags]");
        await RunScriptAsync(script);

        Assert.Equal(1, await db.ScalarAsync<int>($"SELECT MAX([version]) FROM [{schema}].[schema_version]"));
        await using var app = await FlagonTestHost.StartAsync(o => o.Flagon = f =>
        {
            f.AutoMigrate = false;
            f.UseSqlServerStorage(db.ConnectionString, schema: schema);
        });
        await app.Admin.CreateAsync(new CreateFlagRequest("installed-after-retry", Enabled: true));
        Assert.True(app.Flagon.IsEnabled("installed-after-retry"));
    }

    [Fact]
    public void Schema_names_that_are_not_plain_identifiers_are_rejected()
    {
        Assert.Throws<ArgumentException>(() => new SqlServerFlagStore(db.ConnectionString, "flags]; DROP TABLE x; --"));
        Assert.Throws<ArgumentException>(() => SqlServerMigrations.GenerateScript("bad-name"));
    }

    private async Task RunScriptAsync(string script)
    {
        await using var connection = new SqlConnection(db.ConnectionString);
        await connection.OpenAsync();
        foreach (var batch in script.Split("\nGO", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            await using var command = new SqlCommand(batch, connection);
            await command.ExecuteNonQueryAsync();
        }
    }
}
