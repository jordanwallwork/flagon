using Flagon.Storage;
using Microsoft.Extensions.Logging;

namespace Flagon.Internal;

/// <summary>Management operations. Writes go to the store, then the local snapshot is refreshed so this app sees them at once.</summary>
internal sealed partial class FlagonAdmin(
    IFlagStore store,
    FlagSnapshotManager snapshots,
    TimeProvider time,
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
        return instrumentation.TraceStoreOperationAsync("create", request.Key, _ => CreateCoreAsync(request, cancellationToken));
    }

    private async Task<FlagRecord> CreateCoreAsync(CreateFlagRequest request, CancellationToken cancellationToken)
    {
        FlagKey.Validate(request.Key);
        ValidateTags(request.Tags);

        var now = time.GetUtcNow();
        var metadata = new FlagMetadata(
            Description: request.Description,
            Tags: request.Tags,
            Source: FlagSource.Manual,
            CreatedAt: now,
            UpdatedAt: now);

        var record = await store.TryCreateAsync(request.Key, FlagDocument.Boolean(request.Enabled, metadata), cancellationToken).ConfigureAwait(false)
            ?? throw new FlagAlreadyExistsException(request.Key);

        await snapshots.RefreshAsync(cancellationToken).ConfigureAwait(false);
        return record;
    }

    public Task<FlagRecord> SetEnabledAsync(string key, bool enabled, long expectedVersion, CancellationToken cancellationToken = default) =>
        instrumentation.TraceStoreOperationAsync("set_enabled", key, _ => SetEnabledCoreAsync(key, enabled, expectedVersion, cancellationToken));

    private async Task<FlagRecord> SetEnabledCoreAsync(string key, bool enabled, long expectedVersion, CancellationToken cancellationToken)
    {
        var current = await store.GetAsync(key, cancellationToken).ConfigureAwait(false)
            ?? throw new FlagNotFoundException(key);

        var variant = enabled ? FlagDocument.OnVariant : FlagDocument.OffVariant;
        if (!current.Document.Variants.ContainsKey(variant))
        {
            throw new FlagValidationException($"Flag '{key}' is not a boolean flag (it has no '{variant}' variant).");
        }

        var document = current.Document with
        {
            DefaultVariant = variant,
            Metadata = current.Document.Metadata with { UpdatedAt = time.GetUtcNow() },
        };

        // The store enforces expectedVersion atomically; the read above only builds the new document.
        FlagRecord record;
        try
        {
            record = await store.UpdateAsync(key, document, expectedVersion, cancellationToken).ConfigureAwait(false);
        }
        catch (FlagVersionConflictException conflict)
        {
            LogVersionConflict(logger, conflict.Key, conflict.ExpectedVersion, conflict.CurrentVersion);
            throw;
        }

        await snapshots.RefreshAsync(cancellationToken).ConfigureAwait(false);
        return record;
    }

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
        Message = "Update to flag '{FlagKey}' was rejected: expected version {ExpectedVersion} but the flag is at version {CurrentVersion} (it was changed concurrently).")]
    private static partial void LogVersionConflict(ILogger logger, string flagKey, long expectedVersion, long currentVersion);
}
