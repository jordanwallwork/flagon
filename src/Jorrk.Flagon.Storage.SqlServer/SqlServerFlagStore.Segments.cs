using System.Data;
using Microsoft.Data.SqlClient;

namespace Jorrk.Flagon.Storage.SqlServer;

/// <summary>Segments (<see cref="ISegmentStore"/>): the same serialised writes and change counter as flags.</summary>
public partial class SqlServerFlagStore
{
    /// <inheritdoc />
    /// <remarks>
    /// One transaction first reads the change counter <c>WITH (HOLDLOCK)</c>: that waits for an in-flight write (which holds
    /// the counter exclusively from the moment it takes its version) and blocks later writes from taking a version until
    /// the load ends. Flags and segments are then read in the same transaction, so they hold exactly the writes up to the
    /// version read.
    /// </remarks>
    public async Task<FlagStoreContents> LoadAllWithSegmentsAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken).ConfigureAwait(false);

        long version;
        await using (var command = Command(connection, transaction, $"SELECT [value] FROM {T("change_sequence")} WITH (HOLDLOCK) WHERE [id] = 1"))
        {
            version = (long)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
        }

        IReadOnlyList<FlagRecord> flags;
        await using (var command = Command(connection, transaction, $"SELECT [flag_key], [document], [version], [archived] FROM {T("flags")}"))
        {
            flags = await ReadFlagsAsync(command, cancellationToken).ConfigureAwait(false);
        }

        IReadOnlyList<SegmentRecord> segments;
        await using (var command = Command(connection, transaction, $"SELECT [segment_key], [document], [version], [archived] FROM {T("segments")}"))
        {
            segments = await ReadSegmentsAsync(command, cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new FlagStoreContents(version, flags, segments);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<SegmentRecord>> LoadAllSegmentsAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = Command(connection, null, $"SELECT [segment_key], [document], [version], [archived] FROM {T("segments")}");
        return await ReadSegmentsAsync(command, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<SegmentRecord?> GetSegmentAsync(string key, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = Command(connection, null, $"SELECT [segment_key], [document], [version], [archived] FROM {T("segments")} WHERE [segment_key] = @key");
        AddKey(command, key);
        return (await ReadSegmentsAsync(command, cancellationToken).ConfigureAwait(false)).SingleOrDefault();
    }

    /// <inheritdoc />
    public Task<SegmentRecord?> TryCreateSegmentAsync(string key, SegmentDocument document, FlagChangeContext? change = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(document);
        return WriteAsync<SegmentRecord?>(async (connection, transaction) =>
        {
            // Insert-if-absent; archived rows count as present.
            await using (var exists = Command(connection, transaction, $"SELECT COUNT(*) FROM {T("segments")} WITH (UPDLOCK, HOLDLOCK) WHERE [segment_key] = @key"))
            {
                AddKey(exists, key);
                if ((int)(await exists.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))! > 0)
                {
                    return null;
                }
            }

            var version = await NextVersionAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
            var json = SegmentJson.Serialize(document);
            await using (var insert = Command(connection, transaction,
                $"INSERT INTO {T("segments")} ([segment_key], [document], [version], [archived], [updated_at]) VALUES (@key, @document, @version, 0, COALESCE(@timestamp, SYSDATETIMEOFFSET()))"))
            {
                AddKey(insert, key);
                insert.Parameters.Add("@document", SqlDbType.NVarChar, -1).Value = json;
                insert.Parameters.Add("@version", SqlDbType.BigInt).Value = version;
                insert.Parameters.Add("@timestamp", SqlDbType.DateTimeOffset).Value = (object?)change?.Timestamp ?? DBNull.Value;
                await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await SegmentAuditAsync(connection, transaction, change, key, FlagAuditAction.Created, version, before: null, after: json, cancellationToken).ConfigureAwait(false);
            return new SegmentRecord(key, document, version);
        }, cancellationToken);
    }

    /// <inheritdoc />
    public Task<SegmentRecord> UpdateSegmentAsync(string key, SegmentDocument document, long expectedVersion, FlagChangeContext? change = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        return WriteAsync(async (connection, transaction) =>
        {
            var current = await GetSegmentForWriteAsync(connection, transaction, key, expectedVersion, cancellationToken).ConfigureAwait(false);
            var json = SegmentJson.Serialize(document);
            var version = await SetSegmentRowAsync(connection, transaction, key, json, current.Archived, change, cancellationToken).ConfigureAwait(false);
            await SegmentAuditAsync(connection, transaction, change, key, FlagAuditAction.Updated, version, current.Json, json, cancellationToken).ConfigureAwait(false);
            return new SegmentRecord(key, document, version, current.Archived);
        }, cancellationToken);
    }

    /// <inheritdoc />
    public Task<SegmentRecord> ArchiveSegmentAsync(string key, long expectedVersion, FlagChangeContext? change = null, CancellationToken cancellationToken = default) =>
        SetSegmentArchivedAsync(key, expectedVersion, archived: true, change, cancellationToken);

    /// <inheritdoc />
    public Task<SegmentRecord> RestoreSegmentAsync(string key, long expectedVersion, FlagChangeContext? change = null, CancellationToken cancellationToken = default) =>
        SetSegmentArchivedAsync(key, expectedVersion, archived: false, change, cancellationToken);

    /// <inheritdoc />
    /// <remarks>The counter covers the purge, so no purge table is needed.</remarks>
    public Task<long> PurgeSegmentAsync(string key, long expectedVersion, FlagChangeContext? change = null, CancellationToken cancellationToken = default) =>
        WriteAsync(async (connection, transaction) =>
        {
            var current = await GetSegmentForWriteAsync(connection, transaction, key, expectedVersion, cancellationToken).ConfigureAwait(false);
            if (!current.Archived)
            {
                throw new FlagValidationException($"Segment '{key}' must be archived before it can be purged.");
            }

            var version = await NextVersionAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
            await using (var delete = Command(connection, transaction, $"DELETE FROM {T("segments")} WHERE [segment_key] = @key"))
            {
                AddKey(delete, key);
                await delete.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await SegmentAuditAsync(connection, transaction, change, key, FlagAuditAction.Purged, version, current.Json, after: null, cancellationToken).ConfigureAwait(false);
            return version;
        }, cancellationToken);

    /// <inheritdoc />
    public async Task<IReadOnlyList<SegmentAuditEntry>> GetSegmentAuditAsync(string key, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = Command(connection, null, $"""
            SELECT [id], [segment_key], [action], [version], [timestamp], [actor], [application_name], [before_document], [after_document]
            FROM {T("segment_audit")} WHERE [segment_key] = @key ORDER BY [id]
            """);
        AddKey(command, key);
        var entries = new List<SegmentAuditEntry>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            entries.Add(new SegmentAuditEntry(
                reader.GetInt64(0),
                reader.GetString(1),
                Enum.Parse<FlagAuditAction>(reader.GetString(2)),
                reader.GetInt64(3),
                reader.GetDateTimeOffset(4),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetString(6),
                reader.IsDBNull(7) ? null : SegmentJson.Deserialize(reader.GetString(7)),
                reader.IsDBNull(8) ? null : SegmentJson.Deserialize(reader.GetString(8))));
        }

        return entries;
    }

    private Task<SegmentRecord> SetSegmentArchivedAsync(string key, long expectedVersion, bool archived, FlagChangeContext? change, CancellationToken cancellationToken) =>
        WriteAsync(async (connection, transaction) =>
        {
            var current = await GetSegmentForWriteAsync(connection, transaction, key, expectedVersion, cancellationToken).ConfigureAwait(false);
            if (current.Archived == archived)
            {
                throw new FlagValidationException(archived ? $"Segment '{key}' is already archived." : $"Segment '{key}' is not archived.");
            }

            var version = await SetSegmentRowAsync(connection, transaction, key, current.Json, archived, change, cancellationToken).ConfigureAwait(false);
            await SegmentAuditAsync(connection, transaction, change, key, archived ? FlagAuditAction.Archived : FlagAuditAction.Restored, version, current.Json, current.Json, cancellationToken)
                .ConfigureAwait(false);
            return new SegmentRecord(key, SegmentJson.Deserialize(current.Json), version, archived);
        }, cancellationToken);

    private async Task<(string Json, bool Archived)> GetSegmentForWriteAsync(SqlConnection connection, SqlTransaction transaction, string key, long expectedVersion, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(key);
        await using var command = Command(connection, transaction, $"SELECT [document], [version], [archived] FROM {T("segments")} WITH (UPDLOCK, HOLDLOCK) WHERE [segment_key] = @key");
        AddKey(command, key);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new SegmentNotFoundException(key);
        }

        var version = reader.GetInt64(1);
        if (version != expectedVersion)
        {
            throw new SegmentVersionConflictException(key, expectedVersion, version);
        }

        return (reader.GetString(0), reader.GetBoolean(2));
    }

    private async Task<long> SetSegmentRowAsync(SqlConnection connection, SqlTransaction transaction, string key, string json, bool archived, FlagChangeContext? change, CancellationToken cancellationToken)
    {
        var version = await NextVersionAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        await using var command = Command(connection, transaction,
            $"UPDATE {T("segments")} SET [document] = @document, [version] = @version, [archived] = @archived, [updated_at] = COALESCE(@timestamp, SYSDATETIMEOFFSET()) WHERE [segment_key] = @key");
        AddKey(command, key);
        command.Parameters.Add("@document", SqlDbType.NVarChar, -1).Value = json;
        command.Parameters.Add("@version", SqlDbType.BigInt).Value = version;
        command.Parameters.Add("@archived", SqlDbType.Bit).Value = archived;
        command.Parameters.Add("@timestamp", SqlDbType.DateTimeOffset).Value = (object?)change?.Timestamp ?? DBNull.Value;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return version;
    }

    private async Task SegmentAuditAsync(
        SqlConnection connection, SqlTransaction transaction, FlagChangeContext? change, string key, FlagAuditAction action, long version, string? before, string? after,
        CancellationToken cancellationToken)
    {
        if (change is null)
        {
            return;
        }

        await using var command = Command(connection, transaction, $"""
            INSERT INTO {T("segment_audit")} ([segment_key], [action], [version], [timestamp], [actor], [application_name], [before_document], [after_document])
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

    private static async Task<IReadOnlyList<SegmentRecord>> ReadSegmentsAsync(SqlCommand command, CancellationToken cancellationToken)
    {
        var segments = new List<SegmentRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            segments.Add(new SegmentRecord(reader.GetString(0), SegmentJson.Deserialize(reader.GetString(1)), reader.GetInt64(2), reader.GetBoolean(3)));
        }

        return segments;
    }
}
