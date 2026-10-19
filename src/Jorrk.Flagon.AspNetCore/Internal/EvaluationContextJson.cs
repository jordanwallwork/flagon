using System.Text.Json;

namespace Jorrk.Flagon.AspNetCore.Internal;

/// <summary>
/// Reads an evaluation context sent over HTTP, <c>{ "targetingKey": "...", ...attributes }</c>. Shared by OFREP and the
/// management API's evaluate endpoint so a context means the same thing to both.
/// </summary>
internal static class EvaluationContextJson
{
    /// <summary>
    /// Parses <paramref name="context"/>: absent or JSON <c>null</c> is the empty context; anything other than an object,
    /// or a non-string <c>targetingKey</c>, is an error message.
    /// </summary>
    public static (EvaluationContext Context, string? Error) Parse(JsonElement context)
    {
        if (context.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
        {
            return (EvaluationContext.Empty, null);
        }

        if (context.ValueKind != JsonValueKind.Object)
        {
            return (EvaluationContext.Empty, "'context' must be a JSON object.");
        }

        string? targetingKey = null;
        var attributes = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var property in context.EnumerateObject())
        {
            if (property.NameEquals("targetingKey"))
            {
                if (property.Value.ValueKind is not (JsonValueKind.String or JsonValueKind.Null))
                {
                    return (EvaluationContext.Empty, "'context.targetingKey' must be a string.");
                }

                targetingKey = property.Value.GetString();
                continue;
            }

            attributes[property.Name] = ToAttributeValue(property.Value);
        }

        return (new EvaluationContext(targetingKey, attributes), null);
    }

    private static object? ToAttributeValue(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Number when value.TryGetInt64(out var integer) => integer,
        JsonValueKind.Number => value.GetDouble(),
        JsonValueKind.Null or JsonValueKind.Undefined => null,
        // Objects and arrays are passed through as JSON (detached from the request's document).
        _ => value.Clone(),
    };
}
