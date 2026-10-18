using System.Text.Json;
using Flagon.Internal.Targeting;
using Flagon.Storage;

namespace Flagon.Internal;

/// <summary>Variant and default-variant edits.</summary>
internal sealed partial class FlagonAdmin
{
    public Task<FlagRecord> SetVariantsAsync(
        string key, IReadOnlyDictionary<string, JsonElement> variants, string? defaultVariant, long expectedVersion, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(variants);
        return WriteAsync("set_variants", key, () => UpdateDocumentAsync(key, expectedVersion, current =>
        {
            var document = Unarchived(current, expectedVersion);
            if (document.Type == FlagType.Boolean)
            {
                throw new FlagValidationException(
                    $"Flag '{key}' is a boolean flag; its on/off variants are fixed (toggle it with enabled, or set its default variant).");
            }

            if (defaultVariant is not null && defaultVariant == document.DefaultVariant && !variants.ContainsKey(defaultVariant))
            {
                throw new FlagValidationException(
                    $"Flag '{key}' cannot remove variant '{defaultVariant}' while it is the default variant; choose another default variant (or none) in the same edit.");
            }

            if (RolloutShape.Split(document.Targeting, document.Variants) is { } split
                && split.Buckets.FirstOrDefault(b => !variants.ContainsKey(b.Variant)) is { } removed)
            {
                throw new FlagValidationException(
                    $"Flag '{key}' cannot remove variant '{removed.Variant}' while it is a bucket in the percentage split; change the split first.");
            }

            var validated = FlagVariants.Validate(key, variants, defaultVariant);
            if (document.Type is { } type && validated.Type != type)
            {
                throw new FlagValidationException(
                    $"Flag '{key}' is a {FlagVariants.Name(type)} flag; its variants must stay {FlagVariants.Name(type)} values (a flag's type never changes; got {FlagVariants.Name(validated.Type)}).");
            }

            return document with { Variants = validated.Variants, DefaultVariant = defaultVariant };
        }, cancellationToken));
    }

    public Task<FlagRecord> SetDefaultVariantAsync(string key, string? variant, long expectedVersion, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        return WriteAsync("set_default_variant", key, () => UpdateDocumentAsync(key, expectedVersion, current =>
        {
            var document = Unarchived(current, expectedVersion);
            if (variant is not null && !document.Variants.ContainsKey(variant))
            {
                throw new FlagValidationException(
                    $"Flag '{key}' has no variant '{variant}' ({string.Join(", ", document.Variants.Keys.Select(k => $"'{k}'"))}); the default variant must be one of its variants, or none.");
            }

            return document with { DefaultVariant = variant };
        }, cancellationToken));
    }
}
