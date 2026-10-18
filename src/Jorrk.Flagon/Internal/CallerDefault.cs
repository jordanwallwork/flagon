using System.Text.Json;

namespace Jorrk.Flagon.Internal;

/// <summary>
/// A typed getter's caller's default, held as supplied so recording it costs nothing: booleans and numbers are stored
/// inline (no boxing), strings and objects by reference. It is turned into (canonical) JSON only when written, by
/// <see cref="ToJson"/>, so an object default is serialised once per flush or auto-create, not once per evaluation.
/// </summary>
internal readonly struct CallerDefault
{
    /// <summary>
    /// The CLR shape of the typed getter's default (<c>int</c> and <c>double</c> are distinct shapes of the one number
    /// flag type), not a <see cref="FlagType"/>.
    /// </summary>
    private enum Shape : byte
    {
        Boolean,
        Int,
        Double,
        String,
        Object,
    }

    private readonly Shape _shape;
    private readonly long _bits;
    private readonly object? _value;
    private readonly Type? _type;
    private readonly JsonSerializerOptions? _options;

    private CallerDefault(Shape shape, long bits, object? value = null, Type? type = null, JsonSerializerOptions? options = null)
    {
        _shape = shape;
        _bits = bits;
        _value = value;
        _type = type;
        _options = options;
    }

    public static CallerDefault Of(bool value) => new(Shape.Boolean, value ? 1 : 0);

    public static CallerDefault Of(int value) => new(Shape.Int, value);

    public static CallerDefault Of(double value) => new(Shape.Double, BitConverter.DoubleToInt64Bits(value));

    public static CallerDefault Of(string? value) => new(Shape.String, 0, value);

    /// <summary>An object default of declared type <typeparamref name="T"/>, serialised later with <paramref name="options"/>.</summary>
    public static CallerDefault Of<T>(T value, JsonSerializerOptions? options) => new(Shape.Object, 0, value, typeof(T), options);

    /// <summary>
    /// This default, safe to keep beyond the evaluation that supplied it: a defined <see cref="JsonElement"/> default is
    /// cloned, as the caller may dispose its <see cref="JsonDocument"/> once the getter returns. Every other default is
    /// returned as is (booleans and numbers are inline; other objects are the caller's to keep alive), so retaining one
    /// allocates nothing.
    /// </summary>
    public CallerDefault Retained() =>
        _shape == Shape.Object && _value is JsonElement { ValueKind: not JsonValueKind.Undefined } json
            ? new(Shape.Object, 0, json.Clone(), _type, _options)
            : this;

    /// <summary>
    /// True when <paramref name="other"/> is the same default: equal booleans and numbers, ordinal-equal strings,
    /// <see cref="JsonElement"/>s of equal content (so a <see cref="Retained"/> clone matches the element it was cloned
    /// from), and other objects that are <see cref="object.Equals(object, object)"/> of the same declared type serialised
    /// with the same options. Allocation-free for booleans, numbers and strings.
    /// </summary>
    public bool Matches(in CallerDefault other) =>
        _shape == other._shape
        && _bits == other._bits
        && _shape switch
        {
            Shape.String => string.Equals((string?)_value, (string?)other._value, StringComparison.Ordinal),
            Shape.Object => _type == other._type && ReferenceEquals(_options, other._options) && ObjectEquals(_value, other._value),
            _ => true,
        };

    private static bool ObjectEquals(object? value, object? other) =>
        value is JsonElement json && other is JsonElement otherJson
            ? JsonEquals(json, otherJson)
            : Equals(value, other);

    private static bool JsonEquals(JsonElement value, JsonElement other)
    {
        if (value.ValueKind == JsonValueKind.Undefined || other.ValueKind == JsonValueKind.Undefined)
        {
            return value.ValueKind == other.ValueKind;
        }

#if NET9_0_OR_GREATER
        return JsonElement.DeepEquals(value, other);
#else
        // A false negative only costs the tracker a slot, never a wrong default, so formatting differences are fine here.
        return string.Equals(value.GetRawText(), other.GetRawText(), StringComparison.Ordinal);
#endif
    }

    /// <summary>
    /// The default as canonical JSON (see <see cref="CanonicalJson"/>), or null when it has no JSON form (a non-finite
    /// double, or an object the serialiser or one of its converters rejects). A null string or object is JSON <c>null</c>.
    /// </summary>
    public JsonElement? ToJson()
    {
        try
        {
            var json = _shape switch
            {
                Shape.Boolean => JsonSerializer.SerializeToElement(_bits != 0),
                Shape.Int => JsonSerializer.SerializeToElement((int)_bits),
                Shape.Double => JsonSerializer.SerializeToElement(BitConverter.Int64BitsToDouble(_bits)),
                Shape.String => JsonSerializer.SerializeToElement((string?)_value),
                _ => JsonSerializer.SerializeToElement(_value, _type!, _options),
            };
            return CanonicalJson.Canonicalize(json);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return null;
        }
    }
}
