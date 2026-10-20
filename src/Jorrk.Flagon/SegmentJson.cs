using System.Text.Json;
using System.Text.Json.Nodes;

namespace Jorrk.Flagon;

/// <summary>
/// Reads and writes <see cref="SegmentDocument"/>s as stored: <c>{"rule": …, "metadata": {…}}</c>, where
/// <c>metadata</c> uses the same keys as a flag's (see <see cref="FlagdJson"/>). For store implementers.
/// </summary>
public static class SegmentJson
{
    /// <summary>Serialises a segment document.</summary>
    public static string Serialize(SegmentDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var root = new JsonObject
        {
            ["rule"] = JsonNode.Parse(document.Rule.GetRawText()),
            ["metadata"] = FlagdJson.WriteMetadata(document.Metadata),
        };
        return root.ToJsonString();
    }

    /// <summary>Parses a segment document.</summary>
    /// <exception cref="FlagValidationException">The JSON is not a segment document.</exception>
    public static SegmentDocument Deserialize(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        try
        {
            using var parsed = JsonDocument.Parse(json);
            var root = parsed.RootElement;
            return new SegmentDocument(root.GetProperty("rule").Clone(), FlagdJson.ReadMetadata(root));
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            throw new FlagValidationException($"Not a valid segment document: {ex.Message}");
        }
    }
}
