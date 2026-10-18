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
    /// The variant name of a flag created from a single value: a non-boolean flag auto-created from a caller's default,
    /// or defined in code with one string, number or object value.
    /// </summary>
    public const string SingleVariant = "default";

    /// <summary>
    /// The flag type of a JSON value of kind <paramref name="kind"/>, as flagd derives it, or null when that kind has
    /// none (arrays, <c>null</c>, undefined).
    /// </summary>
    public static FlagType? TypeOf(JsonValueKind kind) => kind switch
    {
        JsonValueKind.True or JsonValueKind.False => FlagType.Boolean,
        JsonValueKind.String => FlagType.String,
        JsonValueKind.Number => FlagType.Number,
        JsonValueKind.Object => FlagType.Object,
        _ => null,
    };

    /// <summary>Variant names for a message: each quoted and comma-separated, e.g. <c>'a', 'b'</c>.</summary>
    public static string QuotedNames(IEnumerable<string> names) => string.Join(", ", names.Select(n => $"'{n}'"));

    /// <summary>
    /// Validates <paramref name="variants"/> and <paramref name="defaultVariant"/> for flag <paramref name="key"/> and
    /// returns an immutable, ordinal copy of the variants (values cloned, so they outlive the caller's
    /// <see cref="JsonDocument"/>) with their flag type.
    /// </summary>
    /// <exception cref="FlagValidationException">
    /// There are no variants; a name is empty, longer than <see cref="MaxNameLength"/> or repeated (ordinal); a value is
    /// an array or <c>null</c>; the values are of more than one flag type; or <paramref name="defaultVariant"/> is not
    /// one of the variants; or the values are booleans other than exactly <c>{ "on": true, "off": false }</c> with
    /// <c>on</c> or <c>off</c> as the default variant; or they are not of <paramref name="requiredType"/>.
    /// </exception>
    /// <param name="key">The flag key, for messages.</param>
    /// <param name="variants">The variants to validate.</param>
    /// <param name="defaultVariant">The default variant, or null for none.</param>
    /// <param name="requiredType">The type the variants must have (an existing flag's type, which never changes), or null for any.</param>
    public static (IReadOnlyDictionary<string, JsonElement> Variants, FlagType Type) Validate(
        string key, IEnumerable<KeyValuePair<string, JsonElement>> variants, string? defaultVariant, FlagType? requiredType = null)
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

        if (requiredType is { } required && type != required)
        {
            throw new FlagValidationException(
                $"Flag '{key}' is a {Name(required)} flag; its variants must stay {Name(required)} values (a flag's type never changes; got {Name(type.Value)}).");
        }

        if (defaultVariant is not null && !copy.ContainsKey(defaultVariant))
        {
            throw new FlagValidationException(
                $"Flag '{key}' has default variant '{defaultVariant}', which is not one of its variants ({QuotedNames(copy.Keys)}).");
        }

        if (type == FlagType.Boolean)
        {
            ValidateBoolean(key, copy, defaultVariant);
        }

        return (copy.ToFrozenDictionary(StringComparer.Ordinal), type.Value);
    }

    /// <summary>
    /// Boolean flags have fixed variants: exactly <c>{ "on": true, "off": false }</c>, with <c>on</c> or <c>off</c> as the
    /// default variant (so the on/off toggle always describes them).
    /// </summary>
    private static void ValidateBoolean(string key, Dictionary<string, JsonElement> variants, string? defaultVariant)
    {
        if (variants.Count != 2
            || !variants.TryGetValue(FlagDocument.OnVariant, out var on) || on.ValueKind != JsonValueKind.True
            || !variants.TryGetValue(FlagDocument.OffVariant, out var off) || off.ValueKind != JsonValueKind.False)
        {
            throw new FlagValidationException(
                $"Flag '{key}' is a boolean flag; its variants must be exactly {{ \"on\": true, \"off\": false }}.");
        }

        if (defaultVariant is null)
        {
            throw new FlagValidationException(
                $"Flag '{key}' is a boolean flag; its default variant must be 'on' or 'off'.");
        }
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

    private static FlagType TypeOf(string key, string name, JsonElement value) => TypeOf(value.ValueKind) ?? value.ValueKind switch
    {
        JsonValueKind.Array => throw new FlagValidationException(
            $"Flag '{key}' variant '{name}' is an array; variant values must be booleans, strings, numbers or JSON objects."),
        JsonValueKind.Null => throw new FlagValidationException(
            $"Flag '{key}' variant '{name}' is null; variant values must be booleans, strings, numbers or JSON objects."),
        _ => throw new FlagValidationException($"Flag '{key}' variant '{name}' has no value."),
    };
}
