using System.Data;
using Npgsql;

namespace Jorrk.Flagon.Storage.Postgres;

/// <summary>Segments (<see cref="ISegmentStore"/>): the same write lock, change sequence and notifications as flags.</summary>
public sealed partial class PostgresFlagStore
{
    /// <inheritdoc />
    /// <remarks>
    /// One <c>REPEATABLE READ READ ONLY</c> transaction reads the change version, flags and segments, so all three come from
    /// one snapshot. Writes take versions under the store-wide lock and commit in order, so the snapshot holds exactly the
    /// writes numbered up to the version it reads.
    /// </remarks>
    public async Task<FlagStoreContents> LoadAllWithSegmentsAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.RepeatableRead, cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(connection, transaction, "SET TRANSACTION READ ONLY", cancellationToken).ConfigureAwait(false);

        long version;
        await using (var command = Command(connection, transaction, ChangeVersionSql))
        {
            version = (long)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
        }

        IReadOnlyList<FlagRecord> flags;
        await using (var command = Command(connection, transaction, $"SELECT key, document, version, archived FROM {_flags}"))
        {
            flags = await ReadFlagsAsync(command, cancellationToken).ConfigureAwait(false);
        }

        IReadOnlyList<SegmentRecord> segments;
        await using (var command = Command(connection, transaction, $"SELECT key, document, version, archived FROM {_segments}"))
        {
            segments = await ReadSegmentsAsync(command, cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new FlagStoreContents(version, flags, segments);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<SegmentRecord>> LoadAllSegmentsAsync(CancellationToken cancellationToken = default)
    {
        await using var command = _dataSource.CreateCommand($"SELECT key, document, version, archived FROM {_segments}");
        return await ReadSegmentsAsync(command, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<SegmentRecord?> GetSegmentAsync(string key, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        await using var command = _dataSource.CreateCommand($"SELECT key, document, version, archived FROM {_segments} WHERE key = @key");
        AddText(command, "key", key);
        return (await ReadSegmentsAsync(command, cancellationToken).ConfigureAwait(false)).SingleOrDefault();
    }

    /// <inheritdoc />
    public Task<SegmentRecord?> TryCreateSegmentAsync(string key, SegmentDocument document, FlagChangeContext? change = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(document);
        return WriteAsync<SegmentRecord?>(async (connection, transaction) =>
        {
            await using var command = Command(connection, transaction,
                $"INSERT INTO {_segments} (key, document, version) VALUES (@key, @document, {_nextVersion}) ON CONFLICT (key) DO NOTHING RETURNING version");
            AddText(command, "key", key);
            AddText(command, "document", SegmentJson.Serialize(document));
            if (await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not long version)
            {
                return null;
            }

            await SegmentAuditAsync(connection, transaction, change, key, FlagAuditAction.Created, version, before: null, after: document, cancellationToken).ConfigureAwait(false);
            await NotifyChangeAsync(connection, transaction, version, cancellationToken).ConfigureAwait(false);
            return new SegmentRecord(key, document, version);
        }, cancellationToken);
    }

    /// <inheritdoc />
    public Task<SegmentRecord> UpdateSegmentAsync(string key, SegmentDocument document, long expectedVersion, FlagChangeContext? change = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(document);
        return WriteAsync(async (connection, transaction) =>
        {
            var current = await GetSegmentForWriteAsync(connection, transaction, key, expectedVersion, cancellationToken).ConfigureAwait(false);
            await using var command = Command(connection, transaction,
                $"UPDATE {_segments} SET document = @document, version = {_nextVersion}, updated_at = now() WHERE key = @key RETURNING version");
            AddText(command, "key", key);
            AddText(command, "document", SegmentJson.Serialize(document));
            var version = (long)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;

            await SegmentAuditAsync(connection, transaction, change, key, FlagAuditAction.Updated, version, current.Document, document, cancellationToken).ConfigureAwait(false);
            await NotifyChangeAsync(connection, transaction, version, cancellationToken).ConfigureAwait(false);
            return current with { Document = document, Version = version };
        }, cancellationToken);
    }

    /// <inheritdoc />
    public Task<SegmentRecord> ArchiveSegmentAsync(string key, long expectedVersion, FlagChangeContext? change = null, CancellationToken cancellationToken = default) =>
        SetSegmentArchivedAsync(key, expectedVersion, archived: true, change, cancellationToken);

    /// <inheritdoc />
    public Task<SegmentRecord> RestoreSegmentAsync(string key, long expectedVersion, FlagChangeContext? change = null, CancellationToken cancellationToken = default) =>
        SetSegmentArchivedAsync(key, expectedVersion, archived: false, change, cancellationToken);

    /// <inheritdoc />
    public Task<long> PurgeSegmentAsync(string key, long expectedVersion, FlagChangeContext? change = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        return WriteAsync(async (connection, transaction) =>
        {
            var current = await GetSegmentForWriteAsync(connection, transaction, key, expectedVersion, cancellationToken).ConfigureAwait(false);
            if (!current.Archived)
            {
                throw new FlagValidationException($"Segment '{key}' must be archived before it can be purged.");
            }

            await using var command = Command(connection, transaction, $"""
                DELETE FROM {_segments} WHERE key = @key;
                INSERT INTO {_segmentPurges} (version, key) VALUES ({_nextVersion}, @key) RETURNING version;
                """);
            AddText(command, "key", key);
            var version = (long)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;

            await SegmentAuditAsync(connection, transaction, change, key, FlagAuditAction.Purged, version, current.Document, after: null, cancellationToken).ConfigureAwait(false);
            await NotifyChangeAsync(connection, transaction, version, cancellationToken).ConfigureAwait(false);
            return version;
        }, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<SegmentAuditEntry>> GetSegmentAuditAsync(string key, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        await using var command = _dataSource.CreateCommand(
            $"SELECT id, key, action, version, \"timestamp\", actor, application_name, before_document, after_document FROM {_segmentAudit} WHERE key = @key ORDER BY id");
        AddText(command, "key", key);

        var entries = new List<SegmentAuditEntry>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            entries.Add(new SegmentAuditEntry(
                reader.GetInt64(0),
                reader.GetString(1),
                Enum.Parse<FlagAuditAction>(reader.GetString(2)),
                reader.GetInt64(3),
                reader.GetFieldValue<DateTimeOffset>(4),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetString(6),
                reader.IsDBNull(7) ? null : SegmentJson.Deserialize(reader.GetString(7)),
                reader.IsDBNull(8) ? null : SegmentJson.Deserialize(reader.GetString(8))));
        }

        return entries;
    }

    private Task<SegmentRecord> SetSegmentArchivedAsync(string key, long expectedVersion, bool archived, FlagChangeContext? change, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(key);
        return WriteAsync(async (connection, transaction) =>
        {
            var current = await GetSegmentForWriteAsync(connection, transaction, key, expectedVersion, cancellationToken).ConfigureAwait(false);
            if (current.Archived == archived)
            {
                throw new FlagValidationException(archived ? $"Segment '{key}' is already archived." : $"Segment '{key}' is not archived.");
            }

            await using var command = Command(connection, transaction,
                $"UPDATE {_segments} SET archived = @archived, version = {_nextVersion}, updated_at = now() WHERE key = @key RETURNING version");
            AddText(command, "key", key);
            command.Parameters.Add(new NpgsqlParameter<bool>("archived", archived));
            var version = (long)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;

            var action = archived ? FlagAuditAction.Archived : FlagAuditAction.Restored;
            await SegmentAuditAsync(connection, transaction, change, key, action, version, current.Document, current.Document, cancellationToken).ConfigureAwait(false);
            await NotifyChangeAsync(connection, transaction, version, cancellationToken).ConfigureAwait(false);
            return current with { Archived = archived, Version = version };
        }, cancellationToken);
    }

    private async Task<SegmentRecord> GetSegmentForWriteAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string key, long expectedVersion, CancellationToken cancellationToken)
    {
        await using var command = Command(connection, transaction, $"SELECT key, document, version, archived FROM {_segments} WHERE key = @key FOR UPDATE");
        AddText(command, "key", key);
        var current = (await ReadSegmentsAsync(command, cancellationToken).ConfigureAwait(false)).SingleOrDefault()
            ?? throw new SegmentNotFoundException(key);
        if (current.Version != expectedVersion)
        {
            throw new SegmentVersionConflictException(key, expectedVersion, current.Version);
        }

        return current;
    }

    private async Task SegmentAuditAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, FlagChangeContext? change, string key, FlagAuditAction action,
        long version, SegmentDocument? before, SegmentDocument? after, CancellationToken cancellationToken)
    {
        if (change is null)
        {
            return;
        }

        await using var command = Command(connection, transaction, $"""
            INSERT INTO {_segmentAudit} (key, action, version, "timestamp", actor, application_name, before_document, after_document)
            VALUES (@key, @action, @version, @timestamp, @actor, @application, @before, @after)
            """);
        AddText(command, "key", key);
        AddText(command, "action", action.ToString());
        command.Parameters.Add(new NpgsqlParameter<long>("version", version));
        command.Parameters.Add(new NpgsqlParameter<DateTimeOffset>("timestamp", change.Timestamp.ToUniversalTime()));
        AddText(command, "actor", change.Actor);
        AddText(command, "application", change.ApplicationName);
        AddText(command, "before", before is null ? null : SegmentJson.Serialize(before));
        AddText(command, "after", after is null ? null : SegmentJson.Serialize(after));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<IReadOnlyList<SegmentRecord>> ReadSegmentsAsync(NpgsqlCommand command, CancellationToken cancellationToken)
    {
        var records = new List<SegmentRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            records.Add(new SegmentRecord(reader.GetString(0), SegmentJson.Deserialize(reader.GetString(1)), reader.GetInt64(2), reader.GetBoolean(3)));
        }

        return records;
    }
}
