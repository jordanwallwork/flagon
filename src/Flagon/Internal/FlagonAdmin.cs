using Flagon.Storage;

namespace Flagon.Internal;

/// <summary>Management operations. Writes go to the store, then the local snapshot is refreshed so this app sees them at once.</summary>
internal sealed class FlagonAdmin(IFlagStore store, FlagSnapshotManager snapshots, TimeProvider time) : IFlagonAdmin
{
    public Task<IReadOnlyList<FlagRecord>> ListAsync(CancellationToken cancellationToken = default) =>
        store.LoadAllAsync(cancellationToken);

    public Task<FlagRecord?> GetAsync(string key, CancellationToken cancellationToken = default) =>
        store.GetAsync(key, cancellationToken);

    public async Task<FlagRecord> CreateAsync(CreateFlagRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        FlagKey.Validate(request.Key);
        ValidateTags(request.Tags);

        var now = time.GetUtcNow();
        var metadata = new FlagMetadata(
            Description: request.Description,
            Tags: request.Tags,
            Source: FlagSource.Manual,
            CreatedAt: now,
            UpdatedAt: now);

        var record = await store.TryCreateAsync(request.Key, FlagDocument.Boolean(request.Enabled, metadata), new FlagChangeContext(now), cancellationToken).ConfigureAwait(false)
            ?? throw new FlagAlreadyExistsException(request.Key);

        await snapshots.RefreshAsync(cancellationToken).ConfigureAwait(false);
        return record;
    }

    public async Task<FlagRecord> SetEnabledAsync(string key, bool enabled, long expectedVersion, CancellationToken cancellationToken = default)
    {
        var current = await store.GetAsync(key, cancellationToken).ConfigureAwait(false)
            ?? throw new FlagNotFoundException(key);

        var variant = enabled ? FlagDocument.OnVariant : FlagDocument.OffVariant;
        if (!current.Document.Variants.ContainsKey(variant))
        {
            throw new FlagValidationException($"Flag '{key}' is not a boolean flag (it has no '{variant}' variant).");
        }

        var now = time.GetUtcNow();
        var document = current.Document with
        {
            DefaultVariant = variant,
            Metadata = current.Document.Metadata with { UpdatedAt = now },
        };

        // The store enforces expectedVersion atomically; the read above only builds the new document.
        var record = await store.UpdateAsync(key, document, expectedVersion, new FlagChangeContext(now), cancellationToken).ConfigureAwait(false);
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
}
