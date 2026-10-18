using System.Text.Json;
using Flagon.Internal.Targeting;
using Flagon.Storage;
using Microsoft.Extensions.Logging;

namespace Flagon.Internal;

/// <summary>Management operations. Writes go to the store, then the local snapshot is refreshed so this app sees them at once.</summary>
internal sealed partial class FlagonAdmin(
    IFlagStore store,
    FlagSnapshotManager snapshots,
    FlagChangeStamper changes,
    ITargetingCompiler compiler,
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
        FlagTags.Validate(request.Tags);
        var variants = ValidateVariants(request);
        var targeting = TargetingRules.Validate(compiler, request.Key, request.Targeting);

        var change = changes.Begin();
        var metadata = new FlagMetadata(
            Description: request.Description,
            Tags: request.Tags,
            Source: FlagSource.Manual).StampCreated(change);

        var document = variants is null
            ? FlagDocument.Boolean(request.Enabled, metadata) with { Targeting = targeting }
            : new FlagDocument(FlagState.Enabled, variants, request.DefaultVariant, metadata, targeting);
        var record = await store.TryCreateAsync(request.Key, document, change, cancellationToken).ConfigureAwait(false)
            ?? throw new FlagAlreadyExistsException(request.Key);

        await snapshots.RefreshAsync(cancellationToken).ConfigureAwait(false);
        return record;
    }

    /// <summary>The request's validated variants, or null for a boolean flag created from <see cref="CreateFlagRequest.Enabled"/>.</summary>
    private static IReadOnlyDictionary<string, JsonElement>? ValidateVariants(CreateFlagRequest request)
    {
        if (request.Variants is null)
        {
            return request.DefaultVariant is null
                ? null
                : throw new FlagValidationException($"Flag '{request.Key}' has a default variant but no variants; send variants with it, or use enabled for a boolean flag.");
        }

        if (request.Enabled)
        {
            throw new FlagValidationException(
                $"Flag '{request.Key}' sets both variants and enabled; a flag with variants serves its default variant, so leave enabled unset.");
        }

        return FlagVariants.Validate(request.Key, request.Variants, request.DefaultVariant).Variants;
    }

    public Task<FlagRecord> SetEnabledAsync(string key, bool enabled, long expectedVersion, CancellationToken cancellationToken = default) =>
        WriteAsync("set_enabled", key, () => SetEnabledCoreAsync(key, enabled, expectedVersion, cancellationToken));

    private Task<FlagRecord> SetEnabledCoreAsync(string key, bool enabled, long expectedVersion, CancellationToken cancellationToken) =>
        UpdateDocumentAsync(key, expectedVersion, current =>
        {
            var document = Unarchived(current, expectedVersion);
            if (document.Type != FlagType.Boolean)
            {
                throw new FlagValidationException(
                    $"Flag '{key}' is not a boolean flag; set its state (enabled/disabled) or default variant instead.");
            }

            var variant = enabled ? FlagDocument.OnVariant : FlagDocument.OffVariant;
            if (!document.Variants.ContainsKey(variant))
            {
                throw new FlagValidationException($"Flag '{key}' is not a boolean flag (it has no '{variant}' variant).");
            }

            return document with { DefaultVariant = variant };
        }, cancellationToken);

    public Task<FlagRecord> SetStateAsync(string key, FlagState state, long expectedVersion, CancellationToken cancellationToken = default) =>
        WriteAsync("set_state", key, () => SetStateCoreAsync(key, state, expectedVersion, cancellationToken));

    private Task<FlagRecord> SetStateCoreAsync(string key, FlagState state, long expectedVersion, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (!Enum.IsDefined(state))
        {
            throw new FlagValidationException($"'{state}' is not a flag state; use enabled or disabled.");
        }

        return UpdateDocumentAsync(
            key, expectedVersion, current => Unarchived(current, expectedVersion) with { State = state }, cancellationToken);
    }

    public Task<FlagRecord> SetTargetingAsync(string key, JsonElement? targeting, long expectedVersion, CancellationToken cancellationToken = default) =>
        WriteAsync("set_targeting", key, () => SetTargetingCoreAsync(key, targeting, expectedVersion, cancellationToken));

    private Task<FlagRecord> SetTargetingCoreAsync(string key, JsonElement? targeting, long expectedVersion, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(key);
        var validated = TargetingRules.Validate(compiler, key, targeting);
        return UpdateDocumentAsync(
            key, expectedVersion, current => Unarchived(current, expectedVersion) with { Targeting = validated }, cancellationToken);
    }

    /// <summary>
    /// The document of <paramref name="current"/>, refusing an archived flag. Like the store, a stale
    /// <paramref name="expectedVersion"/> is left to be reported as a conflict before the archived state; archiving bumps
    /// the version, so an archive racing the write still surfaces as a conflict.
    /// </summary>
    /// <exception cref="FlagValidationException">The flag is archived (and <paramref name="expectedVersion"/> is current).</exception>
    private static FlagDocument Unarchived(FlagRecord current, long expectedVersion) =>
        current.Archived && current.Version == expectedVersion
            ? throw new FlagValidationException($"Flag '{current.Key}' is archived; restore it to change it.")
            : current.Document;

    public Task<FlagRecord> UpdateMetadataAsync(string key, UpdateFlagMetadataRequest request, long expectedVersion, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return WriteAsync("update_metadata", key, () => UpdateMetadataCoreAsync(key, request, expectedVersion, cancellationToken));
    }

    private Task<FlagRecord> UpdateMetadataCoreAsync(string key, UpdateFlagMetadataRequest request, long expectedVersion, CancellationToken cancellationToken)
    {
        FlagTags.Validate(request.Tags);
        return UpdateDocumentAsync(key, expectedVersion, current =>
        {
            var document = current.Document;
            var metadata = document.Metadata;
            if (request.Description is not null)
            {
                metadata = metadata with { Description = request.Description.Length == 0 ? null : request.Description };
            }

            if (request.Tags is not null)
            {
                metadata = metadata with { Tags = request.Tags };
            }

            return document with { Metadata = metadata };
        }, cancellationToken);
    }

    /// <summary>
    /// Reads <paramref name="key"/>, builds its new document from the current record with <paramref name="update"/>
    /// (which may refuse the write by throwing), stamps and stores it at <paramref name="expectedVersion"/>, then
    /// refreshes the snapshot.
    /// </summary>
    private async Task<FlagRecord> UpdateDocumentAsync(
        string key, long expectedVersion, Func<FlagRecord, FlagDocument> update, CancellationToken cancellationToken)
    {
        var current = await store.GetAsync(key, cancellationToken).ConfigureAwait(false)
            ?? throw new FlagNotFoundException(key);

        var change = changes.Begin();
        var document = update(current).StampUpdated(change);

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

    public async Task<FlagUsageReport> GetUsageAsync(string key, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        var usage = await store.GetUsageAsync(key, cancellationToken).ConfigureAwait(false);
        return FlagUsageReport.For(key, usage);
    }

    public async Task<IReadOnlyList<FlagUsageReport>> ListUsageAsync(CancellationToken cancellationToken = default)
    {
        var usage = await store.LoadAllUsageAsync(cancellationToken).ConfigureAwait(false);
        return usage
            .GroupBy(u => u.Key, StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => FlagUsageReport.For(g.Key, g))
            .ToArray();
    }

    [LoggerMessage(EventId = 1201, Level = LogLevel.Information,
        Message = "Flagon rejected {Operation} of flag '{FlagKey}': expected version {ExpectedVersion} but the flag is at version {CurrentVersion} (it was changed concurrently).")]
    private static partial void LogVersionConflict(ILogger logger, string operation, string flagKey, long expectedVersion, long currentVersion);
}
