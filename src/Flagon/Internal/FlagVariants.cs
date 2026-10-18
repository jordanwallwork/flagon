using System.Collections.Frozen;
using System.Text.Json;

namespace Flagon.Internal;

/// <summary>
/// The write-time variant rules, shared by every write that sets a flag's variants (admin create and variant edits,
/// <c>Define</c>). Not applied on snapshot load: documents written by other tools still evaluate.
/// </summary>
internal static class FlagVariants
{
    /// <summary>The longest variant name accepted on write.</summary>
    public const int MaxNameLength = 128;

    /// <summary>
    /// Validates <paramref name="variants"/> and <paramref name="defaultVariant"/> for flag <paramref name="key"/> and
    /// returns an immutable, ordinal copy of the variants (values cloned, so they outlive the caller's
    /// <see cref="JsonDocument"/>) with their flag type.
    /// </summary>
    /// <exception cref="FlagValidationException">
    /// There are no variants; a name is empty, longer than <see cref="MaxNameLength"/> or repeated (ordinal); a value is
    /// an array or <c>null</c>; the values are of more than one flag type; or <paramref name="defaultVariant"/> is not
    /// one of the variants.
    /// </exception>
    public static (IReadOnlyDictionary<string, JsonElement> Variants, FlagType Type) Validate(
        string key, IEnumerable<KeyValuePair<string, JsonElement>> variants, string? defaultVariant)
    {
        ArgumentNullException.ThrowIfNull(variants);
        var copy = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        FlagType? type = null;
        foreach (var (name, value) in variants)
        {
            if (string.IsNullOrEmpty(name))
            {
                throw new FlagValidationException($"Flag '{key}' has a variant with an empty name; variant names must be non-empty.");
            }

            if (name.Length > MaxNameLength)
            {
                throw new FlagValidationException(
                    $"Flag '{key}' has a variant name longer than {MaxNameLength} characters ('{name[..32]}…').");
            }

            if (!copy.TryAdd(name, value.Clone()))
            {
                throw Repeated(key, name);
            }

            var valueType = TypeOf(key, name, value);
            if (type is not null && type != valueType)
            {
                throw new FlagValidationException(
                    $"Flag '{key}' mixes {Name(type.Value)} and {Name(valueType)} variants; all variant values must be of one type.");
            }

            type = valueType;
        }

        if (type is null)
        {
            throw new FlagValidationException($"Flag '{key}' must have at least one variant.");
        }

        if (defaultVariant is not null && !copy.ContainsKey(defaultVariant))
        {
            throw new FlagValidationException(
                $"Flag '{key}' has default variant '{defaultVariant}', which is not one of its variants ({string.Join(", ", copy.Keys.Select(k => $"'{k}'"))}).");
        }

        return (copy.ToFrozenDictionary(StringComparer.Ordinal), type.Value);
    }

    /// <summary>
    /// Reads a JSON object of variants (name to value), as sent over HTTP, rejecting a repeated name rather than letting
    /// the last one silently win. The values are not otherwise checked: pass the result to <see cref="Validate"/>.
    /// </summary>
    /// <exception cref="FlagValidationException"><paramref name="variants"/> is not a JSON object, or repeats a name.</exception>
    public static IReadOnlyDictionary<string, JsonElement> FromJson(string key, JsonElement variants)
    {
        if (variants.ValueKind != JsonValueKind.Object)
        {
            throw new FlagValidationException($"Flag '{key}' has invalid 'variants': expected a JSON object of variant names to values.");
        }

        var result = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var property in variants.EnumerateObject())
        {
            if (!result.TryAdd(property.Name, property.Value.Clone()))
            {
                throw Repeated(key, property.Name);
            }
        }

        return result;
    }

    /// <summary>The wire name of <paramref name="type"/>: <c>boolean</c>, <c>string</c>, <c>number</c> or <c>object</c>.</summary>
    public static string Name(FlagType type) => type switch
    {
        FlagType.Boolean => "boolean",
        FlagType.String => "string",
        FlagType.Number => "number",
        FlagType.Object => "object",
        _ => throw new ArgumentOutOfRangeException(nameof(type)),
    };

    private static FlagValidationException Repeated(string key, string name) =>
        new($"Flag '{key}' defines variant '{name}' more than once; variant names must be unique.");

    private static FlagType TypeOf(string key, string name, JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.True or JsonValueKind.False => FlagType.Boolean,
        JsonValueKind.String => FlagType.String,
        JsonValueKind.Number => FlagType.Number,
        JsonValueKind.Object => FlagType.Object,
        JsonValueKind.Array => throw new FlagValidationException(
            $"Flag '{key}' variant '{name}' is an array; variant values must be booleans, strings, numbers or JSON objects."),
        JsonValueKind.Null => throw new FlagValidationException(
            $"Flag '{key}' variant '{name}' is null; variant values must be booleans, strings, numbers or JSON objects."),
        _ => throw new FlagValidationException($"Flag '{key}' variant '{name}' has no value."),
    };
}
