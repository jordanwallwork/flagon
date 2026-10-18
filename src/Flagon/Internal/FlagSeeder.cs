using System.Text.Json;
using Flagon.Internal.Targeting;
using Flagon.Storage;

namespace Flagon.Internal;

/// <summary>A flag declared in code with <see cref="FlagonOptions.Define"/>.</summary>
internal sealed record FlagDefinition(string Key, bool DefaultValue, string? Description, IReadOnlyList<string> Tags, JsonElement? Targeting = null);

/// <summary>
/// Applies the flags declared with <see cref="FlagonOptions.Define"/> to the store: creates missing ones (source code)
/// and syncs metadata of existing ones without touching their state or targeting. Archived flags are never revived or changed.
/// </summary>
internal sealed class FlagSeeder(
    IFlagStore store,
    FlagonOptions options,
    FlagChangeStamper changes,
    FlagonInstrumentation instrumentation,
    ITargetingCompiler compiler)
{
    private const int MaxConflictRetries = 5;

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

    /// <summary>Seeds every definition. Throws on store failure; already-seeded definitions are idempotent on retry.</summary>
    public async Task SeedAsync(CancellationToken cancellationToken)
    {
        if (store.IsReadOnly || options.Definitions.Count == 0)
        {
            return;
        }

        await instrumentation.TraceStoreOperationAsync("seed", flagKey: null, async activity =>
        {
            activity?.SetTag("flagon.seed.definitions", options.Definitions.Count);
            foreach (var definition in options.Definitions)
            {
                await SeedAsync(definition, cancellationToken).ConfigureAwait(false);
            }

            return true;
        }).ConfigureAwait(false);
    }

    private async Task SeedAsync(FlagDefinition definition, CancellationToken cancellationToken)
    {
        // Another app/instance may be seeding or editing the same flag; on a version conflict re-read and try again.
        for (var attempt = 0; ; attempt++)
        {
            var current = await store.GetAsync(definition.Key, cancellationToken).ConfigureAwait(false);
            var change = changes.Begin();

            if (current is null)
            {
                var metadata = new FlagMetadata(
                    Description: definition.Description,
                    Tags: definition.Tags,
                    Source: FlagSource.Code).StampCreated(change);

                var created = await store.TryCreateAsync(
                    definition.Key,
                    FlagDocument.Boolean(definition.DefaultValue, metadata) with { Targeting = definition.Targeting },
                    change,
                    cancellationToken).ConfigureAwait(false);
                if (created is not null || attempt >= MaxConflictRetries)
                {
                    return;
                }

                continue; // Created (or archived) concurrently: re-read and sync instead.
            }

            if (current.Archived || IsInSync(current.Document.Metadata, definition))
            {
                return;
            }

            var document = (current.Document with
            {
                Metadata = current.Document.Metadata with
                {
                    Description = definition.Description,
                    Tags = definition.Tags,
                    Source = FlagSource.Code,
                },
            }).StampUpdated(change);

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

    private static bool IsInSync(FlagMetadata metadata, FlagDefinition definition) =>
        metadata.Source == FlagSource.Code
        && string.Equals(metadata.Description, definition.Description, StringComparison.Ordinal)
        && metadata.Tags.SequenceEqual(definition.Tags, StringComparer.Ordinal);
}
