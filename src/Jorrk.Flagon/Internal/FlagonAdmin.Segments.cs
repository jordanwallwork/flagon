using System.Text.Json;
using Jorrk.Flagon.Internal.Targeting;
using Jorrk.Flagon.Storage;
using Microsoft.Extensions.Logging;

namespace Jorrk.Flagon.Internal;

/// <summary>
/// Segment management. Reference checks (in use, would break a flag) run here, against the store, before the write; they
/// are not atomic with concurrent flag writes (see <see cref="IFlagonAdmin.PurgeSegmentAsync"/>).
/// </summary>
internal sealed partial class FlagonAdmin
{
    public bool SupportsSegments => store is ISegmentStore;

    /// <exception cref="NotSupportedException">The store does not implement <see cref="ISegmentStore"/>.</exception>
    private ISegmentStore SegmentStore => store as ISegmentStore
        ?? throw new NotSupportedException("The flag store does not support segments (it does not implement ISegmentStore).");

    public Task<IReadOnlyList<SegmentRecord>> ListSegmentsAsync(CancellationToken cancellationToken = default) =>
        SegmentStore.LoadAllSegmentsAsync(cancellationToken);

    public Task<SegmentRecord?> GetSegmentAsync(string key, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        return SegmentStore.GetSegmentAsync(key, cancellationToken);
    }

    public Task<SegmentRecord> CreateSegmentAsync(CreateSegmentRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var segments = SegmentStore;
        return SegmentWriteAsync("segment_create", request.Key, async () =>
        {
            ValidateSegmentKey(request.Key);
            FlagTags.Validate(request.Tags);
            var rule = TargetingRules.ValidateSegmentRule(compiler, request.Key, request.Rule);

            var change = changes.Begin();
            var metadata = new FlagMetadata(Description: request.Description, Tags: request.Tags, Source: FlagSource.Manual).StampCreated(change);
            var record = await segments.TryCreateSegmentAsync(request.Key, new SegmentDocument(rule, metadata), change, cancellationToken).ConfigureAwait(false)
                ?? throw new SegmentAlreadyExistsException(request.Key);

            await snapshots.RefreshAsync(cancellationToken).ConfigureAwait(false);
            return record;
        });
    }

    public Task<SegmentRecord> SetSegmentRuleAsync(string key, JsonElement rule, long expectedVersion, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        var segments = SegmentStore;
        return SegmentWriteAsync("segment_set_rule", key, async () =>
        {
            var validated = TargetingRules.ValidateSegmentRule(compiler, key, rule);
            return await UpdateSegmentDocumentAsync(segments, key, expectedVersion, async current =>
            {
                if (current.Archived)
                {
                    throw new FlagValidationException($"Segment '{key}' is archived; restore it to change it.");
                }

                await EnsureDependentsCompileAsync(segments, key, validated, cancellationToken).ConfigureAwait(false);
                return current.Document with { Rule = validated };
            }, cancellationToken).ConfigureAwait(false);
        });
    }

    public Task<SegmentRecord> UpdateSegmentMetadataAsync(string key, UpdateSegmentMetadataRequest request, long expectedVersion, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(request);
        var segments = SegmentStore;
        return SegmentWriteAsync("segment_update_metadata", key, () =>
        {
            FlagTags.Validate(request.Tags);
            return UpdateSegmentDocumentAsync(segments, key, expectedVersion, current =>
            {
                var metadata = current.Document.Metadata;
                if (request.Description is not null)
                {
                    metadata = metadata with { Description = request.Description.Length == 0 ? null : request.Description };
                }

                if (request.Tags is not null)
                {
                    metadata = metadata with { Tags = request.Tags };
                }

                return Task.FromResult(current.Document with { Metadata = metadata });
            }, cancellationToken);
        });
    }

    public Task<SegmentRecord> ArchiveSegmentAsync(string key, long expectedVersion, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        var segments = SegmentStore;
        return SegmentWriteAsync("segment_archive", key, async () =>
        {
            // Only non-archived flags block an archive: an archived segment keeps resolving for archived flags.
            await EnsureNotInUseAsync(segments, key, expectedVersion, includeArchivedFlags: false, "archived", cancellationToken).ConfigureAwait(false);
            var record = await segments.ArchiveSegmentAsync(key, expectedVersion, changes.Begin(), cancellationToken).ConfigureAwait(false);
            await snapshots.RefreshAsync(cancellationToken).ConfigureAwait(false);
            return record;
        });
    }

    public Task<SegmentRecord> RestoreSegmentAsync(string key, long expectedVersion, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        var segments = SegmentStore;
        return SegmentWriteAsync("segment_restore", key, async () =>
        {
            var record = await segments.RestoreSegmentAsync(key, expectedVersion, changes.Begin(), cancellationToken).ConfigureAwait(false);
            await snapshots.RefreshAsync(cancellationToken).ConfigureAwait(false);
            return record;
        });
    }

    public Task PurgeSegmentAsync(string key, long expectedVersion, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        var segments = SegmentStore;
        return SegmentWriteAsync("segment_purge", key, async () =>
        {
            // Archived flags block a purge too: restoring one would leave it referencing a segment that no longer exists.
            await EnsureNotInUseAsync(segments, key, expectedVersion, includeArchivedFlags: true, "purged", cancellationToken).ConfigureAwait(false);
            await segments.PurgeSegmentAsync(key, expectedVersion, changes.Begin(), cancellationToken).ConfigureAwait(false);
            await snapshots.RefreshAsync(cancellationToken).ConfigureAwait(false);
            return true;
        });
    }

    public async Task<IReadOnlyList<SegmentAuditEntry>> GetSegmentAuditHistoryAsync(string key, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        var oldestFirst = await SegmentStore.GetSegmentAuditAsync(key, cancellationToken).ConfigureAwait(false);
        return oldestFirst.Reverse().ToArray();
    }

    public async Task<IReadOnlyList<SegmentReference>> GetSegmentUsageAsync(string key, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        _ = SegmentStore;
        return References(await store.LoadAllAsync(cancellationToken).ConfigureAwait(false), key);
    }

    /// <summary>The flags of <paramref name="flags"/> whose targeting references <paramref name="segmentKey"/>, ordered by key.</summary>
    private static SegmentReference[] References(IEnumerable<FlagRecord> flags, string segmentKey) => flags
        .Where(f => SegmentRefs.References(f.Document.Targeting).Contains(segmentKey, StringComparer.Ordinal))
        .OrderBy(f => f.Key, StringComparer.Ordinal)
        .Select(f => new SegmentReference(f.Key, f.Archived))
        .ToArray();

    /// <summary>Segment keys follow the flag key rules, in a namespace of their own.</summary>
    internal static void ValidateSegmentKey(string? key)
    {
        if (!FlagKey.IsValid(key))
        {
            throw new FlagValidationException(
                $"'{key}' is not a valid segment key. Keys are 1-{FlagKey.MaxLength} characters of letters, digits, '.', '_' or '-', starting with a letter or digit.");
        }
    }

    /// <summary>
    /// The segment at <paramref name="expectedVersion"/> (a stale version is a conflict before anything else), refusing
    /// the write while flags reference it. Returns without checking when the store will refuse the write anyway (already
    /// archived for an archive, not archived for a purge), so its own error is reported.
    /// </summary>
    private async Task EnsureNotInUseAsync(
        ISegmentStore segments, string key, long expectedVersion, bool includeArchivedFlags, string action, CancellationToken cancellationToken)
    {
        var current = await segments.GetSegmentAsync(key, cancellationToken).ConfigureAwait(false)
            ?? throw new SegmentNotFoundException(key);
        if (current.Version != expectedVersion)
        {
            throw new SegmentVersionConflictException(key, expectedVersion, current.Version);
        }

        if (current.Archived != includeArchivedFlags)
        {
            return;
        }

        var users = References(await store.LoadAllAsync(cancellationToken).ConfigureAwait(false), key)
            .Where(r => includeArchivedFlags || !r.Archived)
            .Select(r => r.FlagKey)
            .ToArray();
        if (users.Length > 0)
        {
            throw new SegmentInUseException(key, users, action);
        }
    }

    /// <summary>
    /// Refuses a new rule for <paramref name="key"/> that would stop a non-archived flag referencing it from compiling
    /// (for example by taking the combined rule past a compiler limit), so a segment edit never breaks a flag.
    /// </summary>
    private async Task EnsureDependentsCompileAsync(ISegmentStore segments, string key, JsonElement rule, CancellationToken cancellationToken)
    {
        var flags = await store.LoadAllAsync(cancellationToken).ConfigureAwait(false);
        var dependents = flags
            .Where(f => !f.Archived && SegmentRefs.References(f.Document.Targeting).Contains(key, StringComparer.Ordinal))
            .OrderBy(f => f.Key, StringComparer.Ordinal)
            .ToArray();
        if (dependents.Length == 0)
        {
            return;
        }

        var rules = new Dictionary<string, JsonElement>(
            TargetingRules.RulesOf(await segments.LoadAllSegmentsAsync(cancellationToken).ConfigureAwait(false)), StringComparer.Ordinal)
        {
            [key] = rule,
        };
        foreach (var flag in dependents)
        {
            if (TargetingRules.Compile(compiler, flag.Key, flag.Document.Targeting!.Value, rules).Error is { } error)
            {
                throw new SegmentRuleException(key, $"flag '{flag.Key}' references the segment and would no longer compile: {error.Message}", @operator: error.Operator);
            }
        }
    }

    /// <summary>
    /// Reads <paramref name="key"/>, builds its new document with <paramref name="update"/> (which may refuse by throwing),
    /// stamps and stores it at <paramref name="expectedVersion"/>, then refreshes the snapshot.
    /// </summary>
    private async Task<SegmentRecord> UpdateSegmentDocumentAsync(
        ISegmentStore segments, string key, long expectedVersion, Func<SegmentRecord, Task<SegmentDocument>> update, CancellationToken cancellationToken)
    {
        var current = await segments.GetSegmentAsync(key, cancellationToken).ConfigureAwait(false)
            ?? throw new SegmentNotFoundException(key);
        if (current.Version != expectedVersion)
        {
            throw new SegmentVersionConflictException(key, expectedVersion, current.Version);
        }

        var document = await update(current).ConfigureAwait(false);
        var change = changes.Begin();
        document = document with { Metadata = document.Metadata.StampUpdated(change) };

        var record = await segments.UpdateSegmentAsync(key, document, expectedVersion, change, cancellationToken).ConfigureAwait(false);
        await snapshots.RefreshAsync(cancellationToken).ConfigureAwait(false);
        return record;
    }

    /// <summary>Traces a segment write as <c>flagon.{operation}</c> (counting store failures) and logs optimistic-concurrency conflicts.</summary>
    private Task<T> SegmentWriteAsync<T>(string operation, string key, Func<Task<T>> write) =>
        instrumentation.TraceStoreOperationAsync(operation, flagKey: null, async activity =>
        {
            activity?.SetTag("flagon.segment.key", key);
            try
            {
                return await write().ConfigureAwait(false);
            }
            catch (SegmentVersionConflictException conflict)
            {
                LogSegmentVersionConflict(logger, operation, conflict.Key, conflict.ExpectedVersion, conflict.CurrentVersion);
                throw;
            }
        });

    [LoggerMessage(EventId = 1202, Level = LogLevel.Information,
        Message = "Flagon rejected {Operation} of segment '{SegmentKey}': expected version {ExpectedVersion} but the segment is at version {CurrentVersion} (it was changed concurrently).")]
    private static partial void LogSegmentVersionConflict(ILogger logger, string operation, string segmentKey, long expectedVersion, long currentVersion);
}
