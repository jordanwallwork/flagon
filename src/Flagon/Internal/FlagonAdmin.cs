using Flagon.Storage;
using Microsoft.Extensions.Logging;

namespace Flagon.Internal;

/// <summary>Management operations. Writes go to the store, then the local snapshot is refreshed so this app sees them at once.</summary>
internal sealed partial class FlagonAdmin(
    IFlagStore store,
    FlagSnapshotManager snapshots,
    FlagChangeStamper changes,
    FlagonInstrumentation instrumentation,
    ILogger<FlagonAdmin> logger) : IFlagonAdmin
{
    public Task<IReadOnlyList<FlagRecord>> ListAsync(CancellationToken cancellationToken = default) =>
        store.LoadAllAsync(cancellationToken);

    public Task<FlagRecord?> GetAsync(string key, CancellationToken cancellationToken = default) =>
        store.GetAsync(key, cancellationToken);

    public Task<FlagRecord> CreateAsync(CreateFlagRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return WriteAsync("create", request.Key, () => CreateCoreAsync(request, cancellationToken));
    }

    private async Task<FlagRecord> CreateCoreAsync(CreateFlagRequest request, CancellationToken cancellationToken)
    {
        FlagKey.Validate(request.Key);
        ValidateTags(request.Tags);

        var change = changes.Begin();
        var metadata = new FlagMetadata(
            Description: request.Description,
            Tags: request.Tags,
            Source: FlagSource.Manual).StampCreated(change);

        var record = await store.TryCreateAsync(request.Key, FlagDocument.Boolean(request.Enabled, metadata), change, cancellationToken).ConfigureAwait(false)
            ?? throw new FlagAlreadyExistsException(request.Key);

        await snapshots.RefreshAsync(cancellationToken).ConfigureAwait(false);
        return record;
    }

    public Task<FlagRecord> SetEnabledAsync(string key, bool enabled, long expectedVersion, CancellationToken cancellationToken = default) =>
        WriteAsync("set_enabled", key, () => SetEnabledCoreAsync(key, enabled, expectedVersion, cancellationToken));

    private async Task<FlagRecord> SetEnabledCoreAsync(string key, bool enabled, long expectedVersion, CancellationToken cancellationToken)
    {
        var current = await store.GetAsync(key, cancellationToken).ConfigureAwait(false)
            ?? throw new FlagNotFoundException(key);

        var variant = enabled ? FlagDocument.OnVariant : FlagDocument.OffVariant;
        if (!current.Document.Variants.ContainsKey(variant))
        {
            throw new FlagValidationException($"Flag '{key}' is not a boolean flag (it has no '{variant}' variant).");
        }

        var change = changes.Begin();
        var document = (current.Document with { DefaultVariant = variant }).StampUpdated(change);

        // The store enforces expectedVersion atomically; the read above only builds the new document.
        var record = await store.UpdateAsync(key, document, expectedVersion, change, cancellationToken).ConfigureAwait(false);
        await snapshots.RefreshAsync(cancellationToken).ConfigureAwait(false);
        return record;
    }

    public async Task<IReadOnlyList<FlagAuditEntry>> GetAuditHistoryAsync(string key, CancellationToken cancellationToken = default)
    {
        var oldestFirst = await store.GetAuditAsync(key, cancellationToken).ConfigureAwait(false);
        return oldestFirst.Reverse().ToArray();
    }

    public Task<FlagRecord> ArchiveAsync(string key, long expectedVersion, CancellationToken cancellationToken = default) =>
        WriteAsync("archive", key, async () =>
        {
            var record = await store.ArchiveAsync(key, expectedVersion, changes.Begin(), cancellationToken).ConfigureAwait(false);
            await snapshots.RefreshAsync(cancellationToken).ConfigureAwait(false);
            return record;
        });

    public Task<FlagRecord> RestoreAsync(string key, long expectedVersion, CancellationToken cancellationToken = default) =>
        WriteAsync("restore", key, async () =>
        {
            var record = await store.RestoreAsync(key, expectedVersion, changes.Begin(), cancellationToken).ConfigureAwait(false);
            await snapshots.RefreshAsync(cancellationToken).ConfigureAwait(false);
            return record;
        });

    public Task PurgeAsync(string key, long expectedVersion, CancellationToken cancellationToken = default) =>
        WriteAsync("purge", key, async () =>
        {
            await store.PurgeAsync(key, expectedVersion, changes.Begin(), cancellationToken).ConfigureAwait(false);
            await snapshots.RefreshAsync(cancellationToken).ConfigureAwait(false);
            return true;
        });

    /// <summary>Traces a write as <c>flagon.{operation}</c> (counting store failures) and logs optimistic-concurrency conflicts.</summary>
    private Task<T> WriteAsync<T>(string operation, string key, Func<Task<T>> write) =>
        instrumentation.TraceStoreOperationAsync(operation, key, async _ =>
        {
            try
            {
                return await write().ConfigureAwait(false);
            }
            catch (FlagVersionConflictException conflict)
            {
                LogVersionConflict(logger, operation, conflict.Key, conflict.ExpectedVersion, conflict.CurrentVersion);
                throw;
            }
        });

    private static void ValidateTags(IReadOnlyList<string>? tags)
    {
        foreach (var tag in tags ?? [])
        {
            if (string.IsNullOrWhiteSpace(tag) || tag.Contains(',', StringComparison.Ordinal))
            {
                throw new FlagValidationException($"'{tag}' is not a valid tag. Tags must be non-blank and may not contain commas.");
            }
        }
    }

    [LoggerMessage(EventId = 1201, Level = LogLevel.Information,
        Message = "Flagon rejected {Operation} of flag '{FlagKey}': expected version {ExpectedVersion} but the flag is at version {CurrentVersion} (it was changed concurrently).")]
    private static partial void LogVersionConflict(ILogger logger, string operation, string flagKey, long expectedVersion, long currentVersion);
}
