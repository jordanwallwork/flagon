using System.Buffers;
using System.Text.Json;

namespace Flagon.Internal;

/// <summary>
/// A canonical JSON form for comparing values by content: object properties sorted by name (ordinal), numbers in one
/// invariant format (integers as integers, everything else as the shortest round-trippable double, so <c>1</c>,
/// <c>1.0</c> and <c>1e0</c> agree), and no insignificant whitespace.
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
            case JsonValueKind.Number when value.TryGetInt64(out var integer):
                writer.WriteNumberValue(integer);
                break;
            case JsonValueKind.Number when value.TryGetDouble(out var number) && double.IsFinite(number):
                // An integral value written in fractional or exponent form (1.0, 1e15) is the same number as the integer.
                if (Math.Floor(number) == number && Math.Abs(number) < 9.2e18)
                {
                    writer.WriteNumberValue((long)number);
                }
                else
                {
                    writer.WriteNumberValue(number);
                }

                break;
            default:
                value.WriteTo(writer);
                break;
        }
    }
}
