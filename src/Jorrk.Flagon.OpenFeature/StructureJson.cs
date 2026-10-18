using System.Buffers;
using System.Globalization;
using System.Text.Json;
using OpenFeature.Model;

namespace Jorrk.Flagon.OpenFeature;

/// <summary>Maps between OpenFeature <see cref="Value"/>s (structures, lists, primitives) and JSON.</summary>
internal static class StructureJson
{
    /// <summary>
    /// Writes <paramref name="value"/> as JSON: structures become objects, lists arrays, numbers JSON numbers, and a
    /// <see cref="DateTime"/> its round-trip (ISO 8601) string.
    /// </summary>
    public static JsonElement ToJson(Value value)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            Write(writer, value);
        }

        using var document = JsonDocument.Parse(buffer.WrittenMemory);
        return document.RootElement.Clone();
    }

    /// <summary>Reads JSON as a <see cref="Value"/>: objects become structures, arrays lists, numbers doubles.</summary>
    public static Value ToValue(JsonElement json) => json.ValueKind switch
    {
        JsonValueKind.Object => new Value(ToStructure(json)),
        JsonValueKind.Array => new Value(json.EnumerateArray().Select(ToValue).ToList()),
        JsonValueKind.String => new Value(json.GetString()!),
        JsonValueKind.Number => new Value(json.GetDouble()),
        JsonValueKind.True => new Value(true),
        JsonValueKind.False => new Value(false),
        _ => new Value(),
    };

    private static Structure ToStructure(JsonElement json)
    {
        var builder = Structure.Builder();
        foreach (var property in json.EnumerateObject())
        {
            builder.Set(property.Name, ToValue(property.Value));
        }

        return builder.Build();
    }

    private static void Write(Utf8JsonWriter writer, Value? value)
    {
        if (value is null || value.IsNull)
        {
            writer.WriteNullValue();
        }
        else if (value.IsBoolean)
        {
            writer.WriteBooleanValue(value.AsBoolean!.Value);
        }
        else if (value.IsNumber)
        {
            writer.WriteNumberValue(value.AsDouble!.Value);
        }
        else if (value.IsString)
        {
            writer.WriteStringValue(value.AsString);
        }
        else if (value.IsDateTime)
        {
            writer.WriteStringValue(value.AsDateTime!.Value.ToString("O", CultureInfo.InvariantCulture));
        }
        else if (value.IsStructure)
        {
            writer.WriteStartObject();
            foreach (var (key, item) in value.AsStructure!)
            {
                writer.WritePropertyName(key);
                Write(writer, item);
            }

            writer.WriteEndObject();
        }
        else if (value.IsList)
        {
            writer.WriteStartArray();
            foreach (var item in value.AsList!)
            {
                Write(writer, item);
            }

            writer.WriteEndArray();
        }
        else
        {
            writer.WriteNullValue();
        }
    }
}
