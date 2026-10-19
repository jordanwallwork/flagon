using Npgsql;
using Testcontainers.PostgreSql;

namespace Jorrk.Flagon.Storage.Postgres.Tests;

/// <summary>
/// One PostgreSQL database per test class; tests isolate themselves with a unique schema each. By default a container is
/// started (Docker required). Set <c>FLAGON_TEST_POSTGRES</c> to a connection string to run against an existing server instead.
/// </summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer? _container = Environment.GetEnvironmentVariable("FLAGON_TEST_POSTGRES") is null
        ? new PostgreSqlBuilder("postgres:16-alpine").Build()
        : null;

    public string ConnectionString => Environment.GetEnvironmentVariable("FLAGON_TEST_POSTGRES") ?? _container!.GetConnectionString();

    public static string NewSchema() => $"t_{Guid.NewGuid():N}";

    public Task InitializeAsync() => _container?.StartAsync() ?? Task.CompletedTask;

    public Task DisposeAsync() => _container?.DisposeAsync().AsTask() ?? Task.CompletedTask;

    /// <summary>Runs <paramref name="script"/> through <c>psql</c> inside the container, the way a DBA would deploy it.</summary>
    public async Task PsqlAsync(string script)
    {
        if (_container is not null)
        {
            await _container.ExecScriptAsync(script);
            return;
        }

        // Same behaviour as the container: psql reports a failing statement and carries on with the rest of the script.
        var builder = new NpgsqlConnectionStringBuilder(ConnectionString);
        var psql = new System.Diagnostics.ProcessStartInfo("psql", ["-X", "-q", "-h", builder.Host ?? "localhost", "-p", builder.Port.ToString(), "-U", builder.Username ?? "postgres", "-d", builder.Database ?? "postgres", "-f", "-"])
        {
            RedirectStandardInput = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
        };
        psql.Environment["PGPASSWORD"] = builder.Password ?? string.Empty;
        using var process = System.Diagnostics.Process.Start(psql) ?? throw new InvalidOperationException("psql could not be started.");
        var errors = process.StandardError.ReadToEndAsync();
        await process.StandardInput.WriteAsync(script);
        process.StandardInput.Close();
        await process.WaitForExitAsync();
        await process.StandardOutput.ReadToEndAsync();
        await errors;
    }

    public async Task DropSchemaAsync(string schema)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand($"DROP SCHEMA IF EXISTS \"{schema}\" CASCADE", connection);
        await command.ExecuteNonQueryAsync();
    }

    public async Task ExecuteAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    public async Task<object?> ScalarAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        var result = await command.ExecuteScalarAsync();
        return result is DBNull ? null : result;
    }
}
