using System.Text.Json;
using Flagon.Internal;
using Microsoft.AspNetCore.Http;

namespace Flagon.AspNetCore.Management;

/// <summary>Variant and default-variant edits.</summary>
internal static partial class ManagementEndpoints
{
    public static async Task<IResult> SetVariantsAsync(string key, HttpContext http, IFlagonAdmin admin)
    {
        var body = await ReadBodyAsync<SetVariantsBody>(http, required: true).ConfigureAwait(false);
        if (body!.Variants.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
        {
            throw new FlagValidationException("'variants' is required: a JSON object of variant names to values.");
        }

        var variants = FlagVariants.FromJson(key, body.Variants);
        var version = ExpectedVersion(http, body.Version);
        var record = await admin.SetVariantsAsync(key, variants, body.DefaultVariant, version, http.RequestAborted).ConfigureAwait(false);
        return await FlagAsync(http, admin, record).ConfigureAwait(false);
    }

    public static async Task<IResult> SetDefaultVariantAsync(string key, HttpContext http, IFlagonAdmin admin)
    {
        var body = await ReadBodyAsync<SetDefaultVariantBody>(http, required: true).ConfigureAwait(false);
        var variant = body!.Variant.ValueKind switch
        {
            JsonValueKind.String => body.Variant.GetString(),
            JsonValueKind.Null => null,
            JsonValueKind.Undefined => throw new FlagValidationException("'variant' is required: a variant name, or null for no default variant."),
            _ => throw new FlagValidationException("'variant' must be a variant name (a string), or null for no default variant."),
        };

        var version = ExpectedVersion(http, body.Version);
        var record = await admin.SetDefaultVariantAsync(key, variant, version, http.RequestAborted).ConfigureAwait(false);
        return await FlagAsync(http, admin, record).ConfigureAwait(false);
    }
}

/// <summary>
/// <c>variants</c> is required and read as JSON so repeated names can be rejected; an absent or <c>null</c>
/// <c>defaultVariant</c> leaves the flag with no default variant.
/// </summary>
internal sealed record SetVariantsBody(JsonElement Variants = default, string? DefaultVariant = null, long? Version = null);

/// <summary><c>variant</c> is required: a variant name, or JSON <c>null</c> for none (an absent field is Undefined).</summary>
internal sealed record SetDefaultVariantBody(JsonElement Variant = default, long? Version = null);
