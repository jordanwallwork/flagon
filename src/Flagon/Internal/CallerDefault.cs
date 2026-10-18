using System.Text.Json;

namespace Flagon.Internal;

/// <summary>
/// A typed getter's caller's default, held as supplied so recording it costs nothing: booleans and numbers are stored
/// inline (no boxing), strings and objects by reference. It is turned into (canonical) JSON only when written, by
/// <see cref="ToJson"/>, so an object default is serialised once per flush or auto-create, not once per evaluation.
/// </summary>
internal readonly struct CallerDefault
{
    private enum Kind : byte
    {
        Boolean,
        Int,
        Double,
        String,
        Object,
    }

    private readonly Kind _kind;
    private readonly long _bits;
    private readonly object? _value;
    private readonly Type? _type;
    private readonly JsonSerializerOptions? _options;

    private CallerDefault(Kind kind, long bits, object? value = null, Type? type = null, JsonSerializerOptions? options = null)
    {
        _kind = kind;
        _bits = bits;
        _value = value;
        _type = type;
        _options = options;
    }

    /// <summary>True when this is a boolean default (auto-created as an on/off flag).</summary>
    public bool IsBoolean => _kind == Kind.Boolean;

    public static CallerDefault Of(bool value) => new(Kind.Boolean, value ? 1 : 0);

    public static CallerDefault Of(int value) => new(Kind.Int, value);

    public static CallerDefault Of(double value) => new(Kind.Double, BitConverter.DoubleToInt64Bits(value));

    public static CallerDefault Of(string? value) => new(Kind.String, 0, value);

    /// <summary>An object default of declared type <typeparamref name="T"/>, serialised later with <paramref name="options"/>.</summary>
    public static CallerDefault Of<T>(T value, JsonSerializerOptions? options) => new(Kind.Object, 0, value, typeof(T), options);

    /// <summary>
    /// True when <paramref name="other"/> is the same default: equal booleans and numbers, ordinal-equal strings, and
    /// objects that are <see cref="object.Equals(object, object)"/> of the same declared type serialised with the same
    /// options. Allocation-free.
    /// </summary>
    public bool Matches(in CallerDefault other) =>
        _kind == other._kind
        && _bits == other._bits
        && _kind switch
        {
            Kind.String => string.Equals((string?)_value, (string?)other._value, StringComparison.Ordinal),
            Kind.Object => _type == other._type && ReferenceEquals(_options, other._options) && Equals(_value, other._value),
            _ => true,
        };

    /// <summary>
    /// The default as canonical JSON (see <see cref="CanonicalJson"/>), or null when it has no JSON form (a non-finite
    /// double, or an object the serialiser rejects). A null string or object is JSON <c>null</c>.
    /// </summary>
    public JsonElement? ToJson()
    {
        try
        {
            var json = _kind switch
            {
                Kind.Boolean => JsonSerializer.SerializeToElement(_bits != 0),
                Kind.Int => JsonSerializer.SerializeToElement((int)_bits),
                Kind.Double => JsonSerializer.SerializeToElement(BitConverter.Int64BitsToDouble(_bits)),
                Kind.String => JsonSerializer.SerializeToElement((string?)_value),
                _ => JsonSerializer.SerializeToElement(_value, _type!, _options),
            };
            return CanonicalJson.Canonicalize(json);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or InvalidOperationException or ArgumentException)
        {
            return null;
        }
    }
}
