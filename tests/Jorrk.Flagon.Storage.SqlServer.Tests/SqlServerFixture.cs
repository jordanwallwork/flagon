using Microsoft.Data.SqlClient;
using Testcontainers.MsSql;

namespace Jorrk.Flagon.Storage.SqlServer.Tests;

/// <summary>One SQL Server container shared by every test in the <see cref="SqlServerCollection"/>.</summary>
public sealed class SqlServerFixture : IAsyncLifetime
{
    private readonly MsSqlContainer _container = new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-CU14-ubuntu-22.04").Build();

    public string ConnectionString => _container.GetConnectionString();

    /// <summary>A database with Service Broker on, shared by push tests that only need their own schema.</summary>
    public string PushConnectionString { get; private set; } = "";

    /// <summary>A schema name no other test uses.</summary>
    public static string NewSchema() => $"t_{Guid.NewGuid():N}";

    public async Task InitializeAsync()
    {
        await _container.StartAsync();
        PushConnectionString = await CreateDatabaseAsync();
    }

    /// <summary>
    /// Creates a database no other test uses and returns its connection string. <c>CREATE DATABASE</c> leaves Service
    /// Broker on. Push tests that change database-wide state (offline, broker off) use their own.
    /// </summary>
    public async Task<string> CreateDatabaseAsync()
    {
        var name = $"d_{Guid.NewGuid():N}";
        await ExecuteAsync($"CREATE DATABASE [{name}]");
        return new SqlConnectionStringBuilder(ConnectionString) { InitialCatalog = name }.ConnectionString;
    }

    /// <summary>Runs a statement against the server (database-level changes such as taking a database offline).</summary>
    public async Task ExecuteAsync(string sql)
    {
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(sql, connection) { CommandTimeout = 120 };
        await command.ExecuteNonQueryAsync();
    }

    public Task DisposeAsync() => _container.DisposeAsync().AsTask();

    /// <summary>Runs a scalar query (assertions about the database itself, e.g. the installed schema version).</summary>
    public async Task<T?> ScalarAsync<T>(string sql)
    {
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(sql, connection);
        var result = await command.ExecuteScalarAsync();
        return result is null or DBNull ? default : (T)result;
    }
}

[CollectionDefinition(Name)]
public sealed class SqlServerCollection : ICollectionFixture<SqlServerFixture>
{
    public const string Name = "SQL Server";
}
