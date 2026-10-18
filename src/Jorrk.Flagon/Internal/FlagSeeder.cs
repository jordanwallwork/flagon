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

/// <summary>
/// Applies the flags declared with <c>FlagonOptions.Define</c> to the store: creates missing ones (source code)
/// and syncs metadata of existing ones without touching their state or targeting. Archived flags are never revived or changed.
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
    /// Compiles every definition's targeting with the registered <see cref="ITargetingCompiler"/>, the one that will
    /// evaluate it. A definition is code, so a rule the compiler rejects is a configuration error, not an outage.
    /// </summary>
    /// <exception cref="FlagTargetingException">A definition's targeting rule does not compile.</exception>
    public void ValidateDefinitions()
    {
        foreach (var definition in options.Definitions)
        {
            TargetingRules.Validate(compiler, definition.Key, definition.Targeting);
        }
    }

    /// <summary>
    /// Seeds every definition. Throws on store failure; already-seeded definitions are idempotent on retry. Writes are
    /// audited and stamped with the actor unless <paramref name="audited"/> is false (test setup that must not leave a trace).
    /// </summary>
    public async Task SeedAsync(CancellationToken cancellationToken, bool audited = true)
    {
        if (store.IsReadOnly || !options.SeedDefinitions || options.Definitions.Count == 0)
        {
            return;
        }

        await instrumentation.TraceStoreOperationAsync("seed", flagKey: null, async activity =>
        {
            activity?.SetTag("flagon.seed.definitions", options.Definitions.Count);
            foreach (var definition in options.Definitions)
            {
                await SeedAsync(definition, audited, cancellationToken).ConfigureAwait(false);
            }

            return true;
        }).ConfigureAwait(false);
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

    private static string Describe(FlagType? type) => type is { } known ? FlagVariants.Name(known) : "untyped";

    [LoggerMessage(EventId = 1106, Level = LogLevel.Warning,
        Message = "Flag '{FlagKey}' is defined in code as a {DefinedType} flag but the existing flag is {ExistingType}; it was left unchanged. Change the definition or the flag so their types match.")]
    private static partial void LogTypeClash(ILogger logger, string flagKey, string existingType, string definedType);

    private static bool IsInSync(FlagMetadata metadata, FlagDefinition definition) =>
        metadata.Source == FlagSource.Code
        && string.Equals(metadata.Description, definition.Description, StringComparison.Ordinal)
        && metadata.Tags.SequenceEqual(definition.Tags, StringComparer.Ordinal);
}
