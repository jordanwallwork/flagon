using System.Text.Json;
using Jorrk.Flagon.Internal.Targeting;
using Jorrk.Flagon.Storage;
using Microsoft.Extensions.Logging;

namespace Jorrk.Flagon.Internal;

/// <summary>
/// A flag declared in code with one of the <c>Define</c> overloads: its validated variants, default variant and their
/// flag type, plus the metadata to sync.
/// </summary>
internal sealed record FlagDefinition(
    string Key,
    IReadOnlyDictionary<string, JsonElement> Variants,
    string? DefaultVariant,
    FlagType Type,
    string? Description,
    IReadOnlyList<string> Tags,
    JsonElement? Targeting = null);

/// <summary>A segment declared in code with <c>DefineSegment</c>: its rule (JSON syntax checked, not yet compiled) and the metadata to sync.</summary>
internal sealed record SegmentDefinition(string Key, JsonElement Rule, string? Description, IReadOnlyList<string> Tags);

/// <summary>
/// Applies the segments and flags declared with <c>FlagonOptions.DefineSegment</c> and <c>Define</c> to the store, segments
/// first: creates missing ones (source code) and syncs metadata of existing ones without touching their rule, state or
/// targeting. Archived flags and segments are never revived or changed.
/// </summary>
internal sealed partial class FlagSeeder(
    IFlagStore store,
    FlagonOptions options,
    FlagChangeStamper changes,
    FlagonInstrumentation instrumentation,
    ITargetingCompiler compiler,
    ILogger<FlagSeeder> logger)
{
    private const int MaxConflictRetries = 5;

    // Keys already warned about a type clash, so a seeding retry (after a store failure) does not repeat the warning.
    private readonly HashSet<string> _typeClashesLogged = new(StringComparer.Ordinal);

    /// <summary>
    /// Compiles every segment definition's rule and every flag definition's targeting with the registered
    /// <see cref="ITargetingCompiler"/>, the one that will evaluate it. A definition is code, so a rule the compiler rejects
    /// is a configuration error, not an outage. A flag's segment references are inlined from the segments defined in code;
    /// a flag referencing a segment that is not defined in code is checked against the store when it is seeded.
    /// </summary>
    /// <exception cref="SegmentRuleException">A segment definition's rule is rejected.</exception>
    /// <exception cref="FlagTargetingException">A flag definition's targeting rule does not compile.</exception>
    /// <exception cref="NotSupportedException">Segments are defined but the store does not support them.</exception>
    public void ValidateDefinitions()
    {
        if (options.SegmentDefinitions.Count > 0 && store is not ISegmentStore)
        {
            throw new NotSupportedException(
                $"Segments are defined in code ('{options.SegmentDefinitions[0].Key}') but the flag store does not support segments (it does not implement ISegmentStore).");
        }

        var defined = options.SegmentDefinitions.ToDictionary(
            d => d.Key, d => TargetingRules.ValidateSegmentRule(compiler, d.Key, d.Rule), StringComparer.Ordinal);
        foreach (var definition in options.Definitions)
        {
            // Without segment support a $ref is left for the compiler to reject.
            if (store is not ISegmentStore || !TargetingRules.IsRule(definition.Targeting) || SegmentRefs.FindAny(definition.Targeting!.Value) is null)
            {
                TargetingRules.Validate(compiler, definition.Key, definition.Targeting);
            }
            else if (SegmentRefs.References(definition.Targeting).All(defined.ContainsKey))
            {
                TargetingRules.Validate(compiler, definition.Key, definition.Targeting, defined);
            }
        }
    }

    /// <summary>
    /// Seeds every definition. Throws on store failure; already-seeded definitions are idempotent on retry. Writes are
    /// audited and stamped with the actor unless <paramref name="audited"/> is false (test setup that must not leave a trace).
    /// </summary>
    public async Task SeedAsync(CancellationToken cancellationToken, bool audited = true)
    {
        if (store.IsReadOnly || !options.SeedDefinitions || options.Definitions.Count + options.SegmentDefinitions.Count == 0)
        {
            return;
        }

        await instrumentation.TraceStoreOperationAsync("seed", flagKey: null, async activity =>
        {
            activity?.SetTag("flagon.seed.definitions", options.Definitions.Count);
            if (options.SegmentDefinitions.Count > 0)
            {
                activity?.SetTag("flagon.seed.segment_definitions", options.SegmentDefinitions.Count);
            }

            // Segments first, so flags defined in code can reference them on an empty store.
            foreach (var definition in options.SegmentDefinitions)
            {
                var segments = store as ISegmentStore
                    ?? throw new NotSupportedException("Segments are defined in code but the flag store does not support segments (it does not implement ISegmentStore).");
                await SeedAsync(segments, definition, audited, cancellationToken).ConfigureAwait(false);
            }

            foreach (var definition in options.Definitions)
            {
                await SeedAsync(definition, audited, cancellationToken).ConfigureAwait(false);
            }

            return true;
        }).ConfigureAwait(false);
    }

    private async Task SeedAsync(ISegmentStore segments, SegmentDefinition definition, bool audited, CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            var current = await segments.GetSegmentAsync(definition.Key, cancellationToken).ConfigureAwait(false);
            var change = audited ? changes.Begin() : null;

            if (current is null)
            {
                var metadata = new FlagMetadata(Description: definition.Description, Tags: definition.Tags, Source: FlagSource.Code);
                if (change is not null)
                {
                    metadata = metadata.StampCreated(change);
                }

                var created = await segments.TryCreateSegmentAsync(definition.Key, new SegmentDocument(definition.Rule, metadata), change, cancellationToken)
                    .ConfigureAwait(false);
                if (created is not null || attempt >= MaxConflictRetries)
                {
                    return;
                }

                continue; // Created concurrently: re-read and sync instead.
            }

            // The rule is never overwritten: an admin may have changed it.
            if (current.Archived || IsInSync(current.Document.Metadata, definition.Description, definition.Tags))
            {
                return;
            }

            var updatedMetadata = current.Document.Metadata with { Description = definition.Description, Tags = definition.Tags, Source = FlagSource.Code };
            var document = current.Document with { Metadata = change is null ? updatedMetadata : updatedMetadata.StampUpdated(change) };
            try
            {
                await segments.UpdateSegmentAsync(definition.Key, document, current.Version, change, cancellationToken).ConfigureAwait(false);
                return;
            }
            catch (Exception ex) when (ex is SegmentVersionConflictException or SegmentNotFoundException && attempt < MaxConflictRetries)
            {
            }
        }
    }

    private async Task SeedAsync(FlagDefinition definition, bool audited, CancellationToken cancellationToken)
    {
        // Another app/instance may be seeding or editing the same flag; on a version conflict re-read and try again.
        for (var attempt = 0; ; attempt++)
        {
            var current = await store.GetAsync(definition.Key, cancellationToken).ConfigureAwait(false);
            var change = audited ? changes.Begin() : null;

            if (current is null)
            {
                await ValidateReferencesAsync(definition, cancellationToken).ConfigureAwait(false);
                var metadata = new FlagMetadata(
                    Description: definition.Description,
                    Tags: definition.Tags,
                    Source: FlagSource.Code);
                if (change is not null)
                {
                    metadata = metadata.StampCreated(change);
                }

                var created = await store.TryCreateAsync(
                    definition.Key,
                    new FlagDocument(FlagState.Enabled, definition.Variants, definition.DefaultVariant, metadata, definition.Targeting),
                    change,
                    cancellationToken).ConfigureAwait(false);
                if (created is not null || attempt >= MaxConflictRetries)
                {
                    return;
                }

                continue; // Created (or archived) concurrently: re-read and sync instead.
            }

            if (current.Archived)
            {
                return;
            }

            // A code change never rewrites an admin's flag: a flag of another type is left as it is, metadata included.
            if (current.Document.Type != definition.Type)
            {
                if (_typeClashesLogged.Add(definition.Key))
                {
                    LogTypeClash(logger, definition.Key, Describe(current.Document.Type), FlagVariants.Name(definition.Type));
                }

                return;
            }

            if (IsInSync(current.Document.Metadata, definition))
            {
                return;
            }

            var updated = current.Document with
            {
                Metadata = current.Document.Metadata with
                {
                    Description = definition.Description,
                    Tags = definition.Tags,
                    Source = FlagSource.Code,
                },
            };
            var document = change is null ? updated : updated.StampUpdated(change);

            try
            {
                await store.UpdateAsync(definition.Key, document, current.Version, change, cancellationToken).ConfigureAwait(false);
                return;
            }
            catch (Exception ex) when (ex is FlagVersionConflictException or FlagNotFoundException && attempt < MaxConflictRetries)
            {
            }
        }
    }

    /// <summary>
    /// Checks a definition's segment references against the store before the flag is created: each must exist and not be
    /// archived, and the rule must compile with them inlined.
    /// </summary>
    /// <exception cref="FlagTargetingException">The rule references a segment that does not exist, or does not compile.</exception>
    /// <exception cref="SegmentArchivedException">The rule references an archived segment.</exception>
    private async Task ValidateReferencesAsync(FlagDefinition definition, CancellationToken cancellationToken)
    {
        if (store is not ISegmentStore segmentStore || !TargetingRules.IsRule(definition.Targeting) || SegmentRefs.FindAny(definition.Targeting!.Value) is null)
        {
            return;
        }

        var segments = await segmentStore.LoadAllSegmentsAsync(cancellationToken).ConfigureAwait(false);
        TargetingRules.Validate(compiler, definition.Key, definition.Targeting, TargetingRules.RulesOf(segments));
        var archived = SegmentRefs.References(definition.Targeting)
            .Where(reference => segments.Any(s => s.Archived && string.Equals(s.Key, reference, StringComparison.Ordinal)))
            .ToArray();
        if (archived.Length > 0)
        {
            throw new SegmentArchivedException(definition.Key, archived);
        }
    }

    private static string Describe(FlagType? type) => type is { } known ? FlagVariants.Name(known) : "untyped";

    [LoggerMessage(EventId = 1106, Level = LogLevel.Warning,
        Message = "Flag '{FlagKey}' is defined in code as a {DefinedType} flag but the existing flag is {ExistingType}; it was left unchanged. Change the definition or the flag so their types match.")]
    private static partial void LogTypeClash(ILogger logger, string flagKey, string existingType, string definedType);

    private static bool IsInSync(FlagMetadata metadata, FlagDefinition definition) => IsInSync(metadata, definition.Description, definition.Tags);

    private static bool IsInSync(FlagMetadata metadata, string? description, IReadOnlyList<string> tags) =>
        metadata.Source == FlagSource.Code
        && string.Equals(metadata.Description, description, StringComparison.Ordinal)
        && metadata.Tags.SequenceEqual(tags, StringComparer.Ordinal);
}
