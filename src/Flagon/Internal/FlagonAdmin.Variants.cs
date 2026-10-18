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
                && document.Variants.Keys.FirstOrDefault(name => !variants.ContainsKey(name) && split.Contains(name)) is { } removed)
            {
                throw new FlagValidationException(
                    $"Flag '{key}' cannot remove variant '{removed}' while it is a bucket in the percentage split; change the split first.");
            }

            var validated = FlagVariants.Validate(key, variants, defaultVariant, document.Type);
            return document with { Variants = validated.Variants, DefaultVariant = defaultVariant };
        }, cancellationToken));
    }

    public Task<FlagRecord> SetDefaultVariantAsync(string key, string? variant, long expectedVersion, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        return WriteAsync("set_default_variant", key, () => UpdateDocumentAsync(key, expectedVersion, current =>
        {
            var document = Unarchived(current, expectedVersion);
            if (document.Type == FlagType.Boolean && variant is not (FlagDocument.OnVariant or FlagDocument.OffVariant))
            {
                throw new FlagValidationException(
                    $"Flag '{key}' is a boolean flag; its default variant must be 'on' or 'off' (got {(variant is null ? "none" : $"'{variant}'")}).");
            }

            if (variant is not null && !document.Variants.ContainsKey(variant))
            {
                throw new FlagValidationException(
                    $"Flag '{key}' has no variant '{variant}' ({FlagVariants.QuotedNames(document.Variants.Keys)}); the default variant must be one of its variants, or none.");
            }

            return document with { DefaultVariant = variant };
        }, cancellationToken));
    }
}
