using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Flagon.Internal;

/// <summary>
/// A canonical JSON form for comparing values by content: object properties sorted by name (ordinal), numbers in one
/// format chosen from their exact decimal value (never through <see cref="double"/>, so <c>1</c>, <c>1.0</c> and
/// <c>1e0</c> agree while <c>9007199254740993</c> and <c>9007199254740992</c> stay distinct), and no insignificant
/// whitespace.
/// </summary>
internal static class CanonicalJson
{
    /// <summary>Returns <paramref name="value"/> in canonical form, as a standalone element.</summary>
    public static JsonElement Canonicalize(JsonElement value)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            Write(writer, value);
        }

        using var document = JsonDocument.Parse(buffer.WrittenMemory);
        return document.RootElement.Clone();
    }

    /// <summary>The canonical text of <paramref name="value"/>, for ordinal comparison.</summary>
    public static string ToText(JsonElement value) => Canonicalize(value).GetRawText();

    private static void Write(Utf8JsonWriter writer, JsonElement value)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in value.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    Write(writer, property.Value);
                }

                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in value.EnumerateArray())
                {
                    Write(writer, item);
                }

                writer.WriteEndArray();
                break;
            case JsonValueKind.Number:
                writer.WriteRawValue(CanonicalNumber(value.GetRawText()), skipInputValidation: true);
                break;
            default:
                value.WriteTo(writer);
                break;
        }
    }

    /// <summary>
    /// The canonical text of the JSON number <paramref name="raw"/>, exactly its value: with significand <c>S</c> (its
    /// significant figures, no leading or trailing zeros) and value <c>S × 10^E</c>, an integer is written in full
    /// unless that needs more than 21 trailing zeros, a fraction as a plain decimal unless that needs 6 or more leading
    /// zeros, and anything else in exponent form (<c>1.5e+30</c>, <c>1e-7</c>). Zero, including <c>-0</c>, is <c>0</c>.
    /// The output is at most about 21 characters longer than the input, whatever the exponent.
    /// </summary>
    internal static string CanonicalNumber(string raw)
    {
        var text = raw.AsSpan();
        var negative = text[0] == '-';
        if (negative)
        {
            text = text[1..];
        }

        var exponentAt = text.IndexOfAny('e', 'E');
        var mantissa = exponentAt < 0 ? text : text[..exponentAt];
        long exponent = 0;
        if (exponentAt >= 0 && (!long.TryParse(text[(exponentAt + 1)..], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out exponent)
            || Math.Abs(exponent) > int.MaxValue))
        {
            // An absurd exponent: left as written rather than risk overflow (no real default has one).
            return raw;
        }

        var pointAt = mantissa.IndexOf('.');
        if (pointAt >= 0)
        {
            // Each figure after the point lowers the exponent of the significand by one.
            exponent -= mantissa.Length - pointAt - 1;
        }

        var significand = new StringBuilder(mantissa.Length);
        foreach (var c in mantissa)
        {
            if (c != '.' && (significand.Length > 0 || c != '0'))
            {
                significand.Append(c);
            }
        }

        if (significand.Length == 0)
        {
            return "0";
        }

        var trailing = 0;
        while (significand[significand.Length - 1 - trailing] == '0')
        {
            trailing++;
        }

        significand.Length -= trailing;
        exponent += trailing;

        var s = significand.ToString();
        var point = s.Length + exponent; // figures before the decimal point
        var sign = negative ? "-" : string.Empty;
        if (exponent is >= 0 and <= 21)
        {
            return sign + s + new string('0', (int)exponent);
        }

        if (exponent < 0 && point > 0)
        {
            return sign + s[..(int)point] + "." + s[(int)point..];
        }

        if (exponent < 0 && point > -6)
        {
            return sign + "0." + new string('0', (int)-point) + s;
        }

        var scientific = point - 1;
        return sign + s[0] + (s.Length > 1 ? "." + s[1..] : string.Empty)
            + "e" + (scientific < 0 ? "-" : "+") + Math.Abs(scientific).ToString(CultureInfo.InvariantCulture);
    }
}
