using System.Collections;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Flagon.Internal.Targeting;

namespace Flagon.Targeting.JsonEverything;

/// <summary>Which context attributes a compiled rule needs converted: all of them, or only the named ones.</summary>
internal sealed class ContextPlan(string[]? names)
{
    public static ContextPlan All { get; } = new(null);

    /// <summary>The top-level names the rule reads; null when it may read any.</summary>
    public string[]? Names { get; } = names;
}

/// <summary>
/// Builds the JsonLogic data object flagd evaluates a rule against: the context's attributes, <c>targetingKey</c>
/// (the injected key wins over an attribute of that name), and a <c>$flagd</c> object with <c>flagKey</c> and
/// <c>timestamp</c> that replaces any attribute of that name. Numbers become doubles, as in flagd's JSON round trip.
/// </summary>
internal static class ContextData
{
    private const string FlagdKey = "$flagd";

    public static JsonObject Build(in TargetingContext context, ContextPlan plan)
    {
        var data = new JsonObject();
        if (plan.Names is null)
        {
            foreach (var (name, value) in context.Attributes)
            {
                data[name] = ToNode(value);
            }
        }
        else
        {
            foreach (var name in plan.Names)
            {
                if (context.Attributes.TryGetValue(name, out var value))
                {
                    data[name] = ToNode(value);
                }
            }

            // fractional's implicit bucketing reads targetingKey even when no var names it.
            if (context.TargetingKey is null && context.Attributes.TryGetValue(TargetingContext.TargetingKeyName, out var key))
            {
                data[TargetingContext.TargetingKeyName] = ToNode(key);
            }
        }

        if (context.TargetingKey is { } targetingKey)
        {
            data[TargetingContext.TargetingKeyName] = targetingKey;
        }

        data[FlagdKey] = new JsonObject
        {
            ["flagKey"] = context.FlagKey,
            ["timestamp"] = (double)context.Timestamp,
        };
        return data;
    }

    private static JsonNode? ToNode(object? value) => value switch
    {
        null => null,
        string s => JsonValue.Create(s),
        bool b => JsonValue.Create(b),
        double d => JsonValue.Create(d),
        float f => JsonValue.Create((double)f),
        decimal m => JsonValue.Create((double)m),
        long or int or short or sbyte or byte or ushort or uint or ulong => JsonValue.Create(System.Convert.ToDouble(value, CultureInfo.InvariantCulture)),
        DateTimeOffset dto => JsonValue.Create(dto.ToString("O", CultureInfo.InvariantCulture)),
        DateTime dt => JsonValue.Create(dt.ToString("O", CultureInfo.InvariantCulture)),
        JsonElement e => FromElement(e),
        JsonNode n => n.DeepClone(),
        IEnumerable<KeyValuePair<string, object?>> map => new JsonObject(map.Select(kv => KeyValuePair.Create(kv.Key, ToNode(kv.Value)))),
        IDictionary dictionary => new JsonObject(dictionary.Keys.Cast<object>()
            .Select(k => KeyValuePair.Create(System.Convert.ToString(k, CultureInfo.InvariantCulture)!, ToNode(dictionary[k])))),
        IEnumerable items => new JsonArray(items.Cast<object?>().Select(ToNode).ToArray()),
        _ => JsonValue.Create(System.Convert.ToString(value, CultureInfo.InvariantCulture)),
    };

    private static JsonNode? FromElement(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.Object => new JsonObject(e.EnumerateObject().Select(p => KeyValuePair.Create(p.Name, FromElement(p.Value)))),
        JsonValueKind.Array => new JsonArray(e.EnumerateArray().Select(FromElement).ToArray()),
        JsonValueKind.String => JsonValue.Create(e.GetString()),
        JsonValueKind.Number => JsonValue.Create(e.GetDouble()),
        JsonValueKind.True => JsonValue.Create(true),
        JsonValueKind.False => JsonValue.Create(false),
        _ => null,
    };
}
