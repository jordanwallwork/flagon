using System.Data;
using System.Text.Json;
using Microsoft.Data.SqlClient;

namespace Jorrk.Flagon.Storage.SqlServer;

/// <summary>
/// An <see cref="IFlagStore"/> over SQL Server using raw ADO.NET. All tables live in one schema (default
/// <c>flagon</c>); apps that share a database and schema share flags, and apps that should not can use different schemas.
/// </summary>
/// <remarks>
/// Flag writes run in a transaction that first locks the single-row change sequence, so writes are serialised, versions
/// are committed in order (a refresher that reads version N never misses a later-committed lower version) and
/// create-if-absent is race-free. Flag writes are rare, so the serialisation costs nothing in practice.
/// <para>This store is polled. <see cref="SqlServerPushFlagStore"/> adds push notifications.</para>
/// </remarks>
public class SqlServerFlagStore : IFlagStore
{
    private readonly string _connectionString;
    private readonly string _schema;

    /// <summary>Creates a store over <paramref name="connectionString"/> using tables in <paramref name="schema"/>.</summary>
    /// <param name="connectionString">A SQL Server connection string.</param>
    /// <param name="schema">The schema holding Flagon's tables (letters, digits, underscores).</param>
    /// <exception cref="ArgumentException">The connection string is empty or the schema name is invalid.</exception>
    public SqlServerFlagStore(string connectionString, string schema = SqlServerMigrations.DefaultSchema)
    {
        ArgumentException.ThrowIfNullOrEmpty(connectionString);
        SqlServerMigrations.ValidateSchema(schema);
        _connectionString = connectionString;
        _schema = schema;
    }

    /// <summary>The schema holding Flagon's tables.</summary>
    public string Schema => _schema;

    /// <inheritdoc />
    public bool IsReadOnly => false;

    /// <inheritdoc />
    /// <remarks>Serialised across processes with <c>sp_getapplock</c>; applies only the scripts not yet recorded in <c>schema_version</c>.</remarks>
    public async Task ApplyMigrationsAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await GetAppLockAsync(connection, transaction, "migrate", cancellationToken).ConfigureAwait(false);
        foreach (var batch in SqlServerMigrations.GetBatches(_schema))
        {
            await using var command = Command(connection, transaction, batch);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask<long> GetChangeVersionAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = Command(connection, null, $"SELECT [value] FROM {T("change_sequence")} WHERE [id] = 1");
        return (long)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<FlagRecord>> LoadAllAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = Command(connection, null, $"SELECT [flag_key], [document], [version], [archived] FROM {T("flags")}");
        return await ReadFlagsAsync(command, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<FlagRecord?> GetAsync(string key, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = Command(connection, null, $"SELECT [flag_key], [document], [version], [archived] FROM {T("flags")} WHERE [flag_key] = @key");
        AddKey(command, key);
        return (await ReadFlagsAsync(command, cancellationToken).ConfigureAwait(false)).SingleOrDefault();
    }

    /// <inheritdoc />
    public Task<FlagRecord?> TryCreateAsync(string key, FlagDocument document, FlagChangeContext? change = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(document);
        return WriteAsync<FlagRecord?>(async (connection, transaction) =>
        {
            // Insert-if-absent; archived rows count as present, so an archived key is never revived or touched.
            await using (var exists = Command(connection, transaction, $"SELECT COUNT(*) FROM {T("flags")} WITH (UPDLOCK, HOLDLOCK) WHERE [flag_key] = @key"))
            {
                AddKey(exists, key);
                if ((int)(await exists.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))! > 0)
                {
                    return null;
                }
            }

            var version = await NextVersionAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
            var json = FlagdJson.Serialize(document);
            await using (var insert = Command(connection, transaction,
                $"INSERT INTO {T("flags")} ([flag_key], [document], [version], [archived], [updated_at]) VALUES (@key, @document, @version, 0, COALESCE(@timestamp, SYSDATETIMEOFFSET()))"))
            {
                AddKey(insert, key);
                insert.Parameters.Add("@document", SqlDbType.NVarChar, -1).Value = json;
                insert.Parameters.Add("@version", SqlDbType.BigInt).Value = version;
                insert.Parameters.Add("@timestamp", SqlDbType.DateTimeOffset).Value = (object?)change?.Timestamp ?? DBNull.Value;
                await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await AuditAsync(connection, transaction, change, key, FlagAuditAction.Created, version, before: null, after: json, cancellationToken).ConfigureAwait(false);
            return new FlagRecord(key, document, version);
        }, cancellationToken);
    }

    /// <inheritdoc />
    public Task<FlagRecord> UpdateAsync(string key, FlagDocument document, long expectedVersion, FlagChangeContext? change = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        return WriteAsync(async (connection, transaction) =>
        {
            var current = await GetForWriteAsync(connection, transaction, key, expectedVersion, cancellationToken).ConfigureAwait(false);
            var json = FlagdJson.Serialize(document);
            var version = await SetRowAsync(connection, transaction, key, json, current.Archived, change, cancellationToken).ConfigureAwait(false);
            await AuditAsync(connection, transaction, change, key, FlagAuditAction.Updated, version, current.Json, json, cancellationToken).ConfigureAwait(false);
            return new FlagRecord(key, document, version, current.Archived);
        }, cancellationToken);
    }

    /// <inheritdoc />
    public Task<FlagRecord> ArchiveAsync(string key, long expectedVersion, FlagChangeContext? change = null, CancellationToken cancellationToken = default) =>
        SetArchivedAsync(key, expectedVersion, archived: true, change, cancellationToken);

    /// <inheritdoc />
    public Task<FlagRecord> RestoreAsync(string key, long expectedVersion, FlagChangeContext? change = null, CancellationToken cancellationToken = default) =>
        SetArchivedAsync(key, expectedVersion, archived: false, change, cancellationToken);

    /// <inheritdoc />
    public Task<long> PurgeAsync(string key, long expectedVersion, FlagChangeContext? change = null, CancellationToken cancellationToken = default) =>
        WriteAsync(async (connection, transaction) =>
        {
            var current = await GetForWriteAsync(connection, transaction, key, expectedVersion, cancellationToken).ConfigureAwait(false);
            if (!current.Archived)
            {
                throw new FlagValidationException($"Flag '{key}' must be archived before it can be purged.");
            }

            await using (var delete = Command(connection, transaction,
                $"DELETE FROM {T("flags")} WHERE [flag_key] = @key; DELETE FROM {T("usage")} WHERE [flag_key] = @key;"))
            {
                AddKey(delete, key);
                await delete.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            var version = await NextVersionAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
            await AuditAsync(connection, transaction, change, key, FlagAuditAction.Purged, version, current.Json, after: null, cancellationToken).ConfigureAwait(false);
            return version;
        }, cancellationToken);

    /// <inheritdoc />
    public async Task<IReadOnlyList<FlagAuditEntry>> GetAuditAsync(string key, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = Command(connection, null, $"""
            SELECT [id], [flag_key], [action], [version], [timestamp], [actor], [application_name], [before_document], [after_document]
            FROM {T("audit")} WHERE [flag_key] = @key ORDER BY [id]
            """);
        AddKey(command, key);
        var entries = new List<FlagAuditEntry>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            entries.Add(new FlagAuditEntry(
                reader.GetInt64(0),
                reader.GetString(1),
                Enum.Parse<FlagAuditAction>(reader.GetString(2)),
                reader.GetInt64(3),
                reader.GetDateTimeOffset(4),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetString(6),
                reader.IsDBNull(7) ? null : FlagdJson.Deserialize(reader.GetString(7)),
                reader.IsDBNull(8) ? null : FlagdJson.Deserialize(reader.GetString(8))));
        }

        return entries;
    }

    /// <inheritdoc />
    public async Task UpsertUsageAsync(IReadOnlyCollection<FlagUsage> usage, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(usage);
        if (usage.Count == 0)
        {
            return;
        }

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        // Usage batches are infrequent (about once a minute per instance); serialising them avoids key-range deadlocks.
        await GetAppLockAsync(connection, transaction, "usage", cancellationToken).ConfigureAwait(false);
        await using var command = Command(connection, transaction, $"""
            UPDATE {T("usage")} SET [last_evaluated_at] = @at, [observed_default] = @default
            WHERE [flag_key] = @key AND [application_name] = @app AND [last_evaluated_at] <= @at;
            IF @@ROWCOUNT = 0 AND NOT EXISTS (SELECT 1 FROM {T("usage")} WHERE [flag_key] = @key AND [application_name] = @app)
                INSERT INTO {T("usage")} ([flag_key], [application_name], [last_evaluated_at], [observed_default]) VALUES (@key, @app, @at, @default);
            """);
        var key = AddKey(command, string.Empty);
        var app = command.Parameters.Add("@app", SqlDbType.NVarChar, 256);
        var at = command.Parameters.Add("@at", SqlDbType.DateTimeOffset);
        var observed = command.Parameters.Add("@default", SqlDbType.NVarChar, -1);
        foreach (var record in usage)
        {
            key.Value = record.Key;
            app.Value = record.ApplicationName;
            at.Value = record.LastEvaluatedAt;
            observed.Value = record.ObservedDefault is { } value ? value.GetRawText() : DBNull.Value;
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<FlagUsage>> GetUsageAsync(string key, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = Command(connection, null, $"SELECT [flag_key], [application_name], [last_evaluated_at], [observed_default] FROM {T("usage")} WHERE [flag_key] = @key");
        AddKey(command, key);
        return await ReadUsageAsync(command, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<FlagUsage>> LoadAllUsageAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = Command(connection, null, $"SELECT [flag_key], [application_name], [last_evaluated_at], [observed_default] FROM {T("usage")}");
        return await ReadUsageAsync(command, cancellationToken).ConfigureAwait(false);
    }

    private Task<FlagRecord> SetArchivedAsync(string key, long expectedVersion, bool archived, FlagChangeContext? change, CancellationToken cancellationToken) =>
        WriteAsync(async (connection, transaction) =>
        {
            var current = await GetForWriteAsync(connection, transaction, key, expectedVersion, cancellationToken).ConfigureAwait(false);
            if (current.Archived == archived)
            {
                throw new FlagValidationException(archived ? $"Flag '{key}' is already archived." : $"Flag '{key}' is not archived.");
            }

            var version = await SetRowAsync(connection, transaction, key, current.Json, archived, change, cancellationToken).ConfigureAwait(false);
            await AuditAsync(connection, transaction, change, key, archived ? FlagAuditAction.Archived : FlagAuditAction.Restored, version, current.Json, current.Json, cancellationToken)
                .ConfigureAwait(false);
            return new FlagRecord(key, FlagdJson.Deserialize(current.Json), version, archived);
        }, cancellationToken);

    /// <summary>Runs a flag write in a transaction that holds the change-sequence row lock (serialising flag writes).</summary>
    private async Task<TResult> WriteAsync<TResult>(Func<SqlConnection, SqlTransaction, Task<TResult>> write, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken).ConfigureAwait(false);
        await using (var lockSequence = Command(connection, transaction, $"SELECT [value] FROM {T("change_sequence")} WITH (UPDLOCK, HOLDLOCK) WHERE [id] = 1"))
        {
            await lockSequence.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        }

        var result = await write(connection, transaction).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return result;
    }

    private async Task<(string Json, bool Archived)> GetForWriteAsync(SqlConnection connection, SqlTransaction transaction, string key, long expectedVersion, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(key);
        await using var command = Command(connection, transaction, $"SELECT [document], [version], [archived] FROM {T("flags")} WITH (UPDLOCK, HOLDLOCK) WHERE [flag_key] = @key");
        AddKey(command, key);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new FlagNotFoundException(key);
        }

        var version = reader.GetInt64(1);
        if (version != expectedVersion)
        {
            throw new FlagVersionConflictException(key, expectedVersion, version);
        }

        return (reader.GetString(0), reader.GetBoolean(2));
    }

    private async Task<long> SetRowAsync(SqlConnection connection, SqlTransaction transaction, string key, string json, bool archived, FlagChangeContext? change, CancellationToken cancellationToken)
    {
        var version = await NextVersionAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        await using var command = Command(connection, transaction,
            $"UPDATE {T("flags")} SET [document] = @document, [version] = @version, [archived] = @archived, [updated_at] = COALESCE(@timestamp, SYSDATETIMEOFFSET()) WHERE [flag_key] = @key");
        AddKey(command, key);
        command.Parameters.Add("@document", SqlDbType.NVarChar, -1).Value = json;
        command.Parameters.Add("@version", SqlDbType.BigInt).Value = version;
        command.Parameters.Add("@archived", SqlDbType.Bit).Value = archived;
        command.Parameters.Add("@timestamp", SqlDbType.DateTimeOffset).Value = (object?)change?.Timestamp ?? DBNull.Value;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return version;
    }

    private async Task<long> NextVersionAsync(SqlConnection connection, SqlTransaction transaction, CancellationToken cancellationToken)
    {
        await using var command = Command(connection, transaction, $"UPDATE {T("change_sequence")} SET [value] = [value] + 1 OUTPUT inserted.[value] WHERE [id] = 1");
        return (long)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
    }

    private async Task AuditAsync(
        SqlConnection connection, SqlTransaction transaction, FlagChangeContext? change, string key, FlagAuditAction action, long version, string? before, string? after,
        CancellationToken cancellationToken)
    {
        if (change is null)
        {
            return;
        }

        await using var command = Command(connection, transaction, $"""
            INSERT INTO {T("audit")} ([flag_key], [action], [version], [timestamp], [actor], [application_name], [before_document], [after_document])
            VALUES (@key, @action, @version, @timestamp, @actor, @app, @before, @after)
            """);
        AddKey(command, key);
        command.Parameters.Add("@action", SqlDbType.NVarChar, 16).Value = action.ToString();
        command.Parameters.Add("@version", SqlDbType.BigInt).Value = version;
        command.Parameters.Add("@timestamp", SqlDbType.DateTimeOffset).Value = change.Timestamp;
        command.Parameters.Add("@actor", SqlDbType.NVarChar, 256).Value = (object?)change.Actor ?? DBNull.Value;
        command.Parameters.Add("@app", SqlDbType.NVarChar, 256).Value = (object?)change.ApplicationName ?? DBNull.Value;
        command.Parameters.Add("@before", SqlDbType.NVarChar, -1).Value = (object?)before ?? DBNull.Value;
        command.Parameters.Add("@after", SqlDbType.NVarChar, -1).Value = (object?)after ?? DBNull.Value;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task GetAppLockAsync(SqlConnection connection, SqlTransaction transaction, string purpose, CancellationToken cancellationToken)
    {
        await using var command = Command(connection, transaction, "sp_getapplock");
        command.CommandType = CommandType.StoredProcedure;
        command.CommandTimeout = 0;
        command.Parameters.Add("@Resource", SqlDbType.NVarChar, 255).Value = $"Flagon:{_schema}:{purpose}";
        command.Parameters.Add("@LockMode", SqlDbType.VarChar, 32).Value = "Exclusive";
        command.Parameters.Add("@LockOwner", SqlDbType.VarChar, 32).Value = "Transaction";
        command.Parameters.Add("@LockTimeout", SqlDbType.Int).Value = -1;
        var result = command.Parameters.Add("@Result", SqlDbType.Int);
        result.Direction = ParameterDirection.ReturnValue;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        if ((int)result.Value < 0)
        {
            throw new FlagonException($"Flagon could not acquire the SQL Server application lock '{purpose}' (sp_getapplock returned {result.Value}).");
        }
    }

    private static async Task<IReadOnlyList<FlagRecord>> ReadFlagsAsync(SqlCommand command, CancellationToken cancellationToken)
    {
        var flags = new List<FlagRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            flags.Add(new FlagRecord(reader.GetString(0), FlagdJson.Deserialize(reader.GetString(1)), reader.GetInt64(2), reader.GetBoolean(3)));
        }

        return flags;
    }

    private static async Task<IReadOnlyList<FlagUsage>> ReadUsageAsync(SqlCommand command, CancellationToken cancellationToken)
    {
        var usage = new List<FlagUsage>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            JsonElement? observed = reader.IsDBNull(3) ? null : JsonSerializer.Deserialize<JsonElement>(reader.GetString(3));
            usage.Add(new FlagUsage(reader.GetString(0), reader.GetString(1), reader.GetDateTimeOffset(2), observed));
        }

        return usage;
    }

    private async Task<SqlConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqlConnection(_connectionString);
        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static SqlCommand Command(SqlConnection connection, SqlTransaction? transaction, string sql) => new(sql, connection, transaction);

    private static SqlParameter AddKey(SqlCommand command, string key)
    {
        var parameter = command.Parameters.Add("@key", SqlDbType.NVarChar, 128);
        parameter.Value = key;
        return parameter;
    }

    private string T(string table) => $"[{_schema}].[{table}]";
}
