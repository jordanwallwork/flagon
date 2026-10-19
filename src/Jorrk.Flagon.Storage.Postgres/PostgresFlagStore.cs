using Jorrk.Flagon.Storage.Internal;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using NpgsqlTypes;

namespace Jorrk.Flagon.Storage.Postgres;

/// <summary>
/// An <see cref="IFlagStore"/> over PostgreSQL using raw ADO.NET (Npgsql). All tables live in one schema
/// (<see cref="DefaultSchema"/> unless configured), so apps that should not share flags can use different schemas in one database.
/// </summary>
/// <remarks>
/// <para>Every write takes its version from one sequence while holding a transaction-scoped advisory lock, so versions
/// commit in order and <see cref="GetChangeVersionAsync"/> (<c>MAX(version)</c> over flags and purges) never skips a
/// write that commits late.</para>
/// <para><see cref="ApplyMigrationsAsync"/> runs the embedded scripts not yet recorded in <c>schema_version</c>,
/// serialised across processes by <c>pg_advisory_lock</c>. Use <see cref="GetMigrationScripts"/> to export them instead.</para>
/// <para>Every write that moves the change version also runs <c>pg_notify</c> on channel <c>flagon_{schema}</c> inside its
/// transaction, so listeners hear about it at commit (see <see cref="StartAsync"/>). Usage writes and rejected writes do not notify.
/// Writes made directly in SQL, outside this store, are not notified and are picked up by polling only.</para>
/// </remarks>
public sealed class PostgresFlagStore : IFlagStore, IFlagChangeNotifier, IFlagChangeNotifierTeardown, IAsyncDisposable, IDisposable
{
    /// <summary>The schema used when none is configured.</summary>
    public const string DefaultSchema = "flagon";

    private const int MaxIdentifierBytes = 63;

    private readonly NpgsqlDataSource _dataSource;
    private readonly string _channel;
    private readonly string _listenerConnectionString;
    private readonly CancellationTokenSource _disposing = new();
    private readonly ListenerRuns _listenerRuns = new();
    private int _liveListeners;
    private readonly ILogger _logger;
    private readonly string _migrationLock;
    private readonly string _writeLock;
    private readonly string _flags;
    private readonly string _purges;
    private readonly string _audit;
    private readonly string _usage;
    private readonly string _nextVersion;

    /// <summary>Creates a store over the database at <paramref name="connectionString"/>.</summary>
    /// <param name="connectionString">An Npgsql connection string.</param>
    /// <param name="schema">The schema holding Flagon's tables (letters, digits and underscores).</param>
    /// <param name="logger">Receives the change listener's connect, disconnect, reconnect and notification logs. Optional.</param>
    /// <exception cref="ArgumentException">The schema name is invalid.</exception>
    public PostgresFlagStore(string connectionString, string schema = DefaultSchema, ILogger? logger = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        PostgresMigrations.ValidateSchema(schema);

        // PostgreSQL truncates identifiers longer than 63 bytes, so an over-long channel would silently be shared with another schema.
        _channel = $"flagon_{schema}";
        if (Encoding.UTF8.GetByteCount(_channel) > MaxIdentifierBytes)
        {
            throw new ArgumentException(
                $"Schema '{schema}' is too long: its notification channel '{_channel}' must stay within {MaxIdentifierBytes} bytes.",
                nameof(schema));
        }

        Schema = schema;
        _logger = logger ?? NullLogger.Instance;
        _dataSource = NpgsqlDataSource.Create(connectionString);
        // Listeners get their own unpooled connection: a pooled one would keep its LISTEN after being returned to the pool.
        _listenerConnectionString = new NpgsqlConnectionStringBuilder(connectionString) { Pooling = false }.ConnectionString;
        _migrationLock = $"flagon:migrate:{schema}";
        _writeLock = $"flagon:write:{schema}";
        _flags = $"\"{schema}\".flags";
        _purges = $"\"{schema}\".purges";
        _audit = $"\"{schema}\".audit";
        _usage = $"\"{schema}\".usage";
        _nextVersion = $"nextval('\"{schema}\".change_sequence')";
    }

    /// <summary>The schema holding Flagon's tables.</summary>
    public string Schema { get; }

    /// <summary>How long the listener connection may stay silent before a <c>SELECT 1</c> checks it (30 seconds).</summary>
    internal TimeSpan ListenerKeepaliveInterval { get; init; } = TimeSpan.FromSeconds(30);

    /// <inheritdoc />
    public bool IsReadOnly => false;

    /// <inheritdoc />
    public bool IsConnected => Volatile.Read(ref _liveListeners) > 0;

    /// <summary>
    /// The migration scripts for <paramref name="schema"/>, oldest first, for DBA-managed deployments (turn off
    /// <see cref="FlagonOptions.AutoMigrate"/> and run these through your own change process). Each script is idempotent
    /// and records its version in <c>schema_version</c>, so the store's own runner skips it afterwards.
    /// </summary>
    /// <exception cref="ArgumentException">The schema name is invalid.</exception>
    public static IReadOnlyList<PostgresMigrationScript> GetMigrationScripts(string schema = DefaultSchema) => PostgresMigrations.For(schema);

    /// <inheritdoc />
    public async Task ApplyMigrationsAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(connection, null, "SELECT pg_advisory_lock(hashtextextended(@lock, 0))", cancellationToken, ("lock", _migrationLock)).ConfigureAwait(false);
        try
        {
            await ExecuteAsync(connection, null, PostgresMigrations.Preamble(Schema), cancellationToken).ConfigureAwait(false);

            var applied = new HashSet<int>();
            await using (var command = Command(connection, null, $"SELECT version FROM \"{Schema}\".schema_version"))
            await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
            {
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    applied.Add(reader.GetInt32(0));
                }
            }

            foreach (var script in PostgresMigrations.ForRunner(Schema).Where(s => !applied.Contains(s.Version)))
            {
                await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
                await ExecuteAsync(connection, transaction, script.Sql, cancellationToken).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            await ExecuteAsync(connection, null, "SELECT pg_advisory_unlock(hashtextextended(@lock, 0))", CancellationToken.None, ("lock", _migrationLock)).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public async ValueTask<long> GetChangeVersionAsync(CancellationToken cancellationToken = default)
    {
        await using var command = _dataSource.CreateCommand(
            $"SELECT GREATEST((SELECT COALESCE(MAX(version), 0) FROM {_flags}), (SELECT COALESCE(MAX(version), 0) FROM {_purges}))");
        return (long)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<FlagRecord>> LoadAllAsync(CancellationToken cancellationToken = default)
    {
        await using var command = _dataSource.CreateCommand($"SELECT key, document, version, archived FROM {_flags}");
        return await ReadFlagsAsync(command, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<FlagRecord?> GetAsync(string key, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        await using var command = _dataSource.CreateCommand($"SELECT key, document, version, archived FROM {_flags} WHERE key = @key");
        AddText(command, "key", key);
        return (await ReadFlagsAsync(command, cancellationToken).ConfigureAwait(false)).SingleOrDefault();
    }

    /// <inheritdoc />
    public Task<FlagRecord?> TryCreateAsync(string key, FlagDocument document, FlagChangeContext? change = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(document);
        return WriteAsync<FlagRecord?>(async (connection, transaction) =>
        {
            // ON CONFLICT DO NOTHING: an existing key (archived or not) is left untouched.
            await using var command = Command(connection, transaction,
                $"INSERT INTO {_flags} (key, document, version) VALUES (@key, @document, {_nextVersion}) ON CONFLICT (key) DO NOTHING RETURNING version");
            AddText(command, "key", key);
            AddText(command, "document", FlagdJson.Serialize(document));
            if (await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not long version)
            {
                return null;
            }

            await AuditAsync(connection, transaction, change, key, FlagAuditAction.Created, version, before: null, after: document, cancellationToken).ConfigureAwait(false);
            await NotifyChangeAsync(connection, transaction, version, cancellationToken).ConfigureAwait(false);
            return new FlagRecord(key, document, version);
        }, cancellationToken);
    }

    /// <inheritdoc />
    public Task<FlagRecord> UpdateAsync(string key, FlagDocument document, long expectedVersion, FlagChangeContext? change = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(document);
        return WriteAsync(async (connection, transaction) =>
        {
            var current = await GetForWriteAsync(connection, transaction, key, expectedVersion, cancellationToken).ConfigureAwait(false);
            await using var command = Command(connection, transaction,
                $"UPDATE {_flags} SET document = @document, version = {_nextVersion}, updated_at = now() WHERE key = @key RETURNING version");
            AddText(command, "key", key);
            AddText(command, "document", FlagdJson.Serialize(document));
            var version = (long)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;

            await AuditAsync(connection, transaction, change, key, FlagAuditAction.Updated, version, current.Document, document, cancellationToken).ConfigureAwait(false);
            await NotifyChangeAsync(connection, transaction, version, cancellationToken).ConfigureAwait(false);
            return current with { Document = document, Version = version };
        }, cancellationToken);
    }

    /// <inheritdoc />
    public Task<FlagRecord> ArchiveAsync(string key, long expectedVersion, FlagChangeContext? change = null, CancellationToken cancellationToken = default) =>
        SetArchivedAsync(key, expectedVersion, archived: true, change, cancellationToken);

    /// <inheritdoc />
    public Task<FlagRecord> RestoreAsync(string key, long expectedVersion, FlagChangeContext? change = null, CancellationToken cancellationToken = default) =>
        SetArchivedAsync(key, expectedVersion, archived: false, change, cancellationToken);

    /// <inheritdoc />
    public Task<long> PurgeAsync(string key, long expectedVersion, FlagChangeContext? change = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        return WriteAsync(async (connection, transaction) =>
        {
            var current = await GetForWriteAsync(connection, transaction, key, expectedVersion, cancellationToken).ConfigureAwait(false);
            if (!current.Archived)
            {
                throw new FlagValidationException($"Flag '{key}' must be archived before it can be purged.");
            }

            await using var command = Command(connection, transaction, $"""
                DELETE FROM {_flags} WHERE key = @key;
                DELETE FROM {_usage} WHERE key = @key;
                INSERT INTO {_purges} (version, key) VALUES ({_nextVersion}, @key) RETURNING version;
                """);
            AddText(command, "key", key);
            var version = (long)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;

            await AuditAsync(connection, transaction, change, key, FlagAuditAction.Purged, version, current.Document, after: null, cancellationToken).ConfigureAwait(false);
            await NotifyChangeAsync(connection, transaction, version, cancellationToken).ConfigureAwait(false);
            return version;
        }, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<FlagAuditEntry>> GetAuditAsync(string key, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        await using var command = _dataSource.CreateCommand(
            $"SELECT id, key, action, version, \"timestamp\", actor, application_name, before_document, after_document FROM {_audit} WHERE key = @key ORDER BY id");
        AddText(command, "key", key);

        var entries = new List<FlagAuditEntry>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            entries.Add(new FlagAuditEntry(
                reader.GetInt64(0),
                reader.GetString(1),
                Enum.Parse<FlagAuditAction>(reader.GetString(2)),
                reader.GetInt64(3),
                reader.GetFieldValue<DateTimeOffset>(4),
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

        // One row per (key, application) — ON CONFLICT cannot touch a row twice in one statement — keeping the latest,
        // in a fixed order so concurrent batches lock rows in the same order.
        var rows = usage
            .GroupBy(u => (u.Key, u.ApplicationName))
            .Select(g => g.Aggregate((latest, next) => next.LastEvaluatedAt >= latest.LastEvaluatedAt ? next : latest))
            .OrderBy(u => u.Key, StringComparer.Ordinal)
            .ThenBy(u => u.ApplicationName, StringComparer.Ordinal)
            .ToArray();
        if (rows.Length == 0)
        {
            return;
        }

        await using var command = _dataSource.CreateCommand($"""
            INSERT INTO {_usage} AS u (key, application_name, last_evaluated_at, observed_default)
            SELECT * FROM unnest(@keys, @applications, @evaluated, @defaults)
            ON CONFLICT (key, application_name) DO UPDATE
                SET last_evaluated_at = EXCLUDED.last_evaluated_at, observed_default = EXCLUDED.observed_default
                WHERE u.last_evaluated_at <= EXCLUDED.last_evaluated_at
            """);
        command.Parameters.Add(new NpgsqlParameter<string[]>("keys", rows.Select(r => r.Key).ToArray()));
        command.Parameters.Add(new NpgsqlParameter<string[]>("applications", rows.Select(r => r.ApplicationName).ToArray()));
        command.Parameters.Add(new NpgsqlParameter<DateTime[]>("evaluated", rows.Select(r => r.LastEvaluatedAt.UtcDateTime).ToArray()) { NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.TimestampTz });
        command.Parameters.Add(new NpgsqlParameter<string?[]>("defaults", rows.Select(r => r.ObservedDefault?.GetRawText()).ToArray()) { NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Text });
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<FlagUsage>> GetUsageAsync(string key, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        await using var command = _dataSource.CreateCommand(
            $"SELECT key, application_name, last_evaluated_at, observed_default FROM {_usage} WHERE key = @key");
        AddText(command, "key", key);
        return await ReadUsageAsync(command, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<FlagUsage>> LoadAllUsageAsync(CancellationToken cancellationToken = default)
    {
        await using var command = _dataSource.CreateCommand($"SELECT key, application_name, last_evaluated_at, observed_default FROM {_usage}");
        return await ReadUsageAsync(command, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Opens a dedicated, unpooled connection and runs <c>LISTEN flagon_{schema}</c> before returning, so a write that commits
    /// after this returns is delivered. If the connection is later lost, it reconnects in the background with backoff,
    /// re-issues <c>LISTEN</c> and signals a resubscribe (see <see cref="FlagChangeSignal.Resubscribed"/>). Polling covers
    /// the gap. The listener stops when <paramref name="cancellationToken"/> is cancelled or the store is disposed.
    /// </remarks>
    public async Task StartAsync(Action<FlagChangeSignal> onChanged, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(onChanged);
        if (cancellationToken.IsCancellationRequested)
        {
            return;
        }

        var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _disposing.Token);
        var listener = new PostgresChangeListener(_listenerConnectionString, _channel, ListenerKeepaliveInterval, _logger,
            connected => Interlocked.Add(ref _liveListeners, connected ? 1 : -1));
        var run = await listener.StartAsync(onChanged, stop).ConfigureAwait(false);
        _listenerRuns.Add(cancellationToken, run);
    }

    /// <inheritdoc />
    public Task WhenStoppedAsync(CancellationToken startToken) => _listenerRuns.WhenStoppedAsync(startToken);

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        _disposing.Cancel();
        return _dataSource.DisposeAsync();
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _disposing.Cancel();
        _dataSource.Dispose();
    }

    /// <summary>
    /// Notifies listeners of a version-moving write, inside the write's transaction, so the notification is delivered at
    /// commit and never for a write that rolls back. The payload is <c>{version}:{clock_timestamp in epoch ms}</c>.
    /// </summary>
    private async Task NotifyChangeAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, long version, CancellationToken cancellationToken)
    {
        await using var command = Command(connection, transaction,
            "SELECT pg_notify(@channel, @version || ':' || floor(extract(epoch FROM clock_timestamp()) * 1000)::bigint::text)");
        AddText(command, "channel", _channel);
        AddText(command, "version", version.ToString(CultureInfo.InvariantCulture));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private Task<FlagRecord> SetArchivedAsync(string key, long expectedVersion, bool archived, FlagChangeContext? change, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(key);
        return WriteAsync(async (connection, transaction) =>
        {
            var current = await GetForWriteAsync(connection, transaction, key, expectedVersion, cancellationToken).ConfigureAwait(false);
            if (current.Archived == archived)
            {
                throw new FlagValidationException(archived ? $"Flag '{key}' is already archived." : $"Flag '{key}' is not archived.");
            }

            await using var command = Command(connection, transaction,
                $"UPDATE {_flags} SET archived = @archived, version = {_nextVersion}, updated_at = now() WHERE key = @key RETURNING version");
            AddText(command, "key", key);
            command.Parameters.Add(new NpgsqlParameter<bool>("archived", archived));
            var version = (long)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;

            var action = archived ? FlagAuditAction.Archived : FlagAuditAction.Restored;
            await AuditAsync(connection, transaction, change, key, action, version, current.Document, current.Document, cancellationToken).ConfigureAwait(false);
            await NotifyChangeAsync(connection, transaction, version, cancellationToken).ConfigureAwait(false);
            return current with { Archived = archived, Version = version };
        }, cancellationToken);
    }

    /// <summary>
    /// Runs a write in a transaction holding the store-wide write lock, so sequence values are taken and committed in
    /// order (a reader that sees version N has seen every write numbered below N).
    /// </summary>
    private async Task<T> WriteAsync<T>(Func<NpgsqlConnection, NpgsqlTransaction, Task<T>> write, CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(connection, transaction, "SELECT pg_advisory_xact_lock(hashtextextended(@lock, 0))", cancellationToken, ("lock", _writeLock)).ConfigureAwait(false);
        var result = await write(connection, transaction).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return result;
    }

    private async Task<FlagRecord> GetForWriteAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string key, long expectedVersion, CancellationToken cancellationToken)
    {
        await using var command = Command(connection, transaction, $"SELECT key, document, version, archived FROM {_flags} WHERE key = @key FOR UPDATE");
        AddText(command, "key", key);
        var current = (await ReadFlagsAsync(command, cancellationToken).ConfigureAwait(false)).SingleOrDefault()
            ?? throw new FlagNotFoundException(key);
        if (current.Version != expectedVersion)
        {
            throw new FlagVersionConflictException(key, expectedVersion, current.Version);
        }

        return current;
    }

    private async Task AuditAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, FlagChangeContext? change, string key, FlagAuditAction action,
        long version, FlagDocument? before, FlagDocument? after, CancellationToken cancellationToken)
    {
        if (change is null)
        {
            return;
        }

        await using var command = Command(connection, transaction, $"""
            INSERT INTO {_audit} (key, action, version, "timestamp", actor, application_name, before_document, after_document)
            VALUES (@key, @action, @version, @timestamp, @actor, @application, @before, @after)
            """);
        AddText(command, "key", key);
        AddText(command, "action", action.ToString());
        command.Parameters.Add(new NpgsqlParameter<long>("version", version));
        command.Parameters.Add(new NpgsqlParameter<DateTimeOffset>("timestamp", change.Timestamp.ToUniversalTime()));
        AddText(command, "actor", change.Actor);
        AddText(command, "application", change.ApplicationName);
        AddText(command, "before", before is null ? null : FlagdJson.Serialize(before));
        AddText(command, "after", after is null ? null : FlagdJson.Serialize(after));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<IReadOnlyList<FlagRecord>> ReadFlagsAsync(NpgsqlCommand command, CancellationToken cancellationToken)
    {
        var records = new List<FlagRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            records.Add(new FlagRecord(reader.GetString(0), FlagdJson.Deserialize(reader.GetString(1)), reader.GetInt64(2), reader.GetBoolean(3)));
        }

        return records;
    }

    private static async Task<IReadOnlyList<FlagUsage>> ReadUsageAsync(NpgsqlCommand command, CancellationToken cancellationToken)
    {
        var usage = new List<FlagUsage>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            JsonElement? observedDefault = null;
            if (!reader.IsDBNull(3))
            {
                using var json = JsonDocument.Parse(reader.GetString(3));
                observedDefault = json.RootElement.Clone();
            }

            usage.Add(new FlagUsage(reader.GetString(0), reader.GetString(1), reader.GetFieldValue<DateTimeOffset>(2), observedDefault));
        }

        return usage;
    }

    private static NpgsqlCommand Command(NpgsqlConnection connection, NpgsqlTransaction? transaction, string sql) => new(sql, connection, transaction);

    private static async Task ExecuteAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction, string sql, CancellationToken cancellationToken, params (string Name, string Value)[] parameters)
    {
        await using var command = Command(connection, transaction, sql);
        foreach (var (name, value) in parameters)
        {
            AddText(command, name, value);
        }

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static void AddText(NpgsqlCommand command, string name, string? value) =>
        command.Parameters.Add(new NpgsqlParameter(name, NpgsqlDbType.Text) { Value = (object?)value ?? DBNull.Value });
}
