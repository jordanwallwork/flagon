using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Flagon;

/// <summary>
/// Reads and writes <see cref="FlagDocument"/>s as flagd flag-definition JSON
/// (<c>{"state","variants","defaultVariant","targeting"?,"metadata"?}</c>).
/// Flagon metadata keys: <c>description</c>, <c>tags</c> (comma-separated string, as flagd metadata values must be
/// primitives), <c>source</c> (<c>manual</c>|<c>auto</c>|<c>code</c>), <c>createdAt</c>/<c>updatedAt</c> (ISO 8601),
/// <c>createdBy</c>/<c>updatedBy</c>.
/// </summary>
public static class FlagdJson
{
    /// <summary>Serialises a flag to flagd JSON.</summary>
    public static string Serialize(FlagDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        var variants = new JsonObject();
        foreach (var (name, value) in document.Variants)
        {
            variants[name] = JsonNode.Parse(value.GetRawText());
        }

        var root = new JsonObject
        {
            ["state"] = document.State == FlagState.Enabled ? "ENABLED" : "DISABLED",
            ["variants"] = variants,
            ["defaultVariant"] = document.DefaultVariant,
        };

        if (document.Targeting is { } targeting)
        {
            root["targeting"] = JsonNode.Parse(targeting.GetRawText());
        }

        var m = document.Metadata;
        var metadata = new JsonObject();
        if (m.Description is not null) metadata["description"] = m.Description;
        if (m.Tags.Count > 0) metadata["tags"] = string.Join(',', m.Tags);
        metadata["source"] = m.Source.ToString().ToLowerInvariant();
        if (m.CreatedAt is { } createdAt) metadata["createdAt"] = FormatDate(createdAt);
        if (m.CreatedBy is not null) metadata["createdBy"] = m.CreatedBy;
        if (m.UpdatedAt is { } updatedAt) metadata["updatedAt"] = FormatDate(updatedAt);
        if (m.UpdatedBy is not null) metadata["updatedBy"] = m.UpdatedBy;
        root["metadata"] = metadata;

        return root.ToJsonString();
    }

    /// <summary>Parses flagd JSON into a flag.</summary>
    /// <exception cref="FlagValidationException">The JSON is not a valid flagd flag definition.</exception>
    public static FlagDocument Deserialize(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        try
        {
            using var parsed = JsonDocument.Parse(json);
            var root = parsed.RootElement;

            var state = root.GetProperty("state").GetString() switch
            {
                "ENABLED" => FlagState.Enabled,
                "DISABLED" => FlagState.Disabled,
                var other => throw new FlagValidationException($"Unknown flagd state '{other}'."),
            };

            var variants = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            foreach (var variant in root.GetProperty("variants").EnumerateObject())
            {
                variants[variant.Name] = variant.Value.Clone();
            }

            var defaultVariant = root.GetProperty("defaultVariant").GetString()
                ?? throw new FlagValidationException("flagd 'defaultVariant' must be a string.");

            JsonElement? targeting = root.TryGetProperty("targeting", out var t) && t.ValueKind != JsonValueKind.Null
                ? t.Clone()
                : null;

            return new FlagDocument(state, variants, defaultVariant, ReadMetadata(root), targeting);
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            throw new FlagValidationException($"Not a valid flagd flag definition: {ex.Message}");
        }
    }

    // ISO 8601 round-trip format without trailing zero fractional seconds.
    private static string FormatDate(DateTimeOffset value) =>
        value.ToString("yyyy-MM-dd'T'HH:mm:ss.FFFFFFFzzz", CultureInfo.InvariantCulture);

    private static FlagMetadata ReadMetadata(JsonElement root)
    {
        if (!root.TryGetProperty("metadata", out var m) || m.ValueKind != JsonValueKind.Object)
        {
            return new FlagMetadata();
        }

        string? Str(string name) => m.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;
        DateTimeOffset? Date(string name) => Str(name) is { } s ? DateTimeOffset.Parse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind) : null;

        var tags = Str("tags") is { Length: > 0 } t
            ? t.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            : [];

        var source = Str("source") switch
        {
            "auto" => FlagSource.Auto,
            "code" => FlagSource.Code,
            _ => FlagSource.Manual,
        };

        return new FlagMetadata(Str("description"), tags, source, Date("createdAt"), Str("createdBy"), Date("updatedAt"), Str("updatedBy"));
    }
}
