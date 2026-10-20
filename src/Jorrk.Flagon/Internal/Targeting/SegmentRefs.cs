using System.Buffers;
using System.Text.Json;

namespace Jorrk.Flagon.Internal.Targeting;

/// <summary>
/// Segment references in targeting rules, with flagd's <c>$ref</c> semantics: an object that is exactly
/// <c>{"$ref": "&lt;segment-key&gt;"}</c>, at any depth, stands for the segment's rule and is replaced by it (structural
/// inlining) before the rule is compiled, as flagd substitutes a flag set's <c>$evaluators</c>. The compiler never sees a
/// resolved reference, so a flag using a segment compiles, evaluates and allocates exactly as its inlined equivalent.
/// </summary>
internal static class SegmentRefs
{
    /// <summary>The reference operator.</summary>
    public const string Operator = "$ref";

    /// <summary>
    /// <paramref name="rule"/> with every reference replaced by the referenced segment's rule from
    /// <paramref name="segments"/> (segment key to rule), or the first malformed or unresolved reference as an error
    /// located at the reference. A rule without references is returned as it is.
    /// </summary>
    public static InlinedRule Inline(JsonElement rule, IReadOnlyDictionary<string, JsonElement> segments)
    {
        ArgumentNullException.ThrowIfNull(segments);
        var sites = new List<RefSite>();
        if (Find(rule, "", sites, segments) is { } error)
        {
            return new InlinedRule(rule, [], error);
        }

        if (sites.Count == 0)
        {
            return new InlinedRule(rule, [], null);
        }

        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            Write(writer, rule, segments);
        }

        using var document = JsonDocument.Parse(buffer.WrittenMemory);
        return new InlinedRule(document.RootElement.Clone(), sites, null);
    }

    /// <summary>
    /// The distinct segment keys <paramref name="targeting"/> references, in the order they first appear. Malformed
    /// references (a non-string operand, or extra keys) are not counted. Empty when there is no rule.
    /// </summary>
    public static IReadOnlyList<string> References(JsonElement? targeting)
    {
        if (targeting is not { } rule)
        {
            return [];
        }

        List<string>? keys = null;
        Collect(rule, ref keys);
        return keys ?? (IReadOnlyList<string>)[];
    }

    /// <summary>A JSON Pointer to the first object in <paramref name="rule"/> with a <c>$ref</c> key, or null when there is none.</summary>
    public static string? FindAny(JsonElement rule) => FindAny(rule, "");

    /// <summary>
    /// <paramref name="error"/>, from compiling an inlined rule, reported against the original rule: an error inside an
    /// inlined segment is located at its reference and its message names the segment.
    /// </summary>
    public static TargetingError MapError(TargetingError error, IReadOnlyList<RefSite> sites)
    {
        if (error.Path is not { } path)
        {
            return error;
        }

        RefSite? best = null;
        foreach (var site in sites)
        {
            var within = site.Pointer.Length == 0 || path == site.Pointer || path.StartsWith(site.Pointer + "/", StringComparison.Ordinal);
            if (within && (best is null || site.Pointer.Length > best.Value.Pointer.Length))
            {
                best = site;
            }
        }

        return best is { } at
            ? new TargetingError($"in segment '{at.Key}': {error.Message}", at.Pointer, error.Operator)
            : error;
    }

    // First error, collecting the well-formed references found before it.
    private static TargetingError? Find(JsonElement e, string pointer, List<RefSite> sites, IReadOnlyDictionary<string, JsonElement> segments)
    {
        switch (e.ValueKind)
        {
            case JsonValueKind.Object:
                if (e.TryGetProperty(Operator, out var operand))
                {
                    if (Malformed(e, operand) is { } malformed)
                    {
                        return new TargetingError(malformed, pointer, Operator);
                    }

                    var key = operand.GetString()!;
                    if (!segments.ContainsKey(key))
                    {
                        return new TargetingError($"segment '{key}' does not exist", pointer, Operator);
                    }

                    sites.Add(new RefSite(pointer, key));
                    return null;
                }

                foreach (var property in e.EnumerateObject())
                {
                    if (Find(property.Value, pointer + "/" + Escape(property.Name), sites, segments) is { } error)
                    {
                        return error;
                    }
                }

                return null;

            case JsonValueKind.Array:
                var index = 0;
                foreach (var item in e.EnumerateArray())
                {
                    if (Find(item, pointer + "/" + index.ToString(System.Globalization.CultureInfo.InvariantCulture), sites, segments) is { } error)
                    {
                        return error;
                    }

                    index++;
                }

                return null;

            default:
                return null;
        }
    }

    private static string? Malformed(JsonElement reference, JsonElement operand)
    {
        using var properties = reference.EnumerateObject();
        properties.MoveNext();
        if (properties.MoveNext())
        {
            return $"a $ref object must have no other keys, got {reference.GetRawText()}";
        }

        return operand.ValueKind == JsonValueKind.String
            ? null
            : $"the $ref operand must be a segment key (a string), got {operand.GetRawText()}";
    }

    private static void Write(Utf8JsonWriter writer, JsonElement e, IReadOnlyDictionary<string, JsonElement> segments)
    {
        switch (e.ValueKind)
        {
            case JsonValueKind.Object when e.TryGetProperty(Operator, out var operand):
                // Validated by Find: a well-formed reference to a segment that exists.
                segments[operand.GetString()!].WriteTo(writer);
                break;
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in e.EnumerateObject())
                {
                    writer.WritePropertyName(property.Name);
                    Write(writer, property.Value, segments);
                }

                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in e.EnumerateArray())
                {
                    Write(writer, item, segments);
                }

                writer.WriteEndArray();
                break;
            default:
                e.WriteTo(writer);
                break;
        }
    }

    private static void Collect(JsonElement e, ref List<string>? keys)
    {
        switch (e.ValueKind)
        {
            case JsonValueKind.Object:
                if (e.TryGetProperty(Operator, out var operand))
                {
                    if (Malformed(e, operand) is null && operand.GetString() is { } key && !(keys?.Contains(key, StringComparer.Ordinal) ?? false))
                    {
                        (keys ??= []).Add(key);
                    }

                    return;
                }

                foreach (var property in e.EnumerateObject())
                {
                    Collect(property.Value, ref keys);
                }

                return;
            case JsonValueKind.Array:
                foreach (var item in e.EnumerateArray())
                {
                    Collect(item, ref keys);
                }

                return;
        }
    }

    private static string? FindAny(JsonElement e, string pointer)
    {
        switch (e.ValueKind)
        {
            case JsonValueKind.Object:
                if (e.TryGetProperty(Operator, out _))
                {
                    return pointer;
                }

                foreach (var property in e.EnumerateObject())
                {
                    if (FindAny(property.Value, pointer + "/" + Escape(property.Name)) is { } found)
                    {
                        return found;
                    }
                }

                return null;
            case JsonValueKind.Array:
                var index = 0;
                foreach (var item in e.EnumerateArray())
                {
                    if (FindAny(item, pointer + "/" + index.ToString(System.Globalization.CultureInfo.InvariantCulture)) is { } found)
                    {
                        return found;
                    }

                    index++;
                }

                return null;
            default:
                return null;
        }
    }

    private static string Escape(string segment) =>
        segment.Replace("~", "~0", StringComparison.Ordinal).Replace("/", "~1", StringComparison.Ordinal);
}

/// <summary>
/// The result of <see cref="SegmentRefs.Inline"/>: the rule with references replaced and where each was, or the error
/// that stopped inlining (then <see cref="Rule"/> is the original rule).
/// </summary>
internal sealed record InlinedRule(JsonElement Rule, IReadOnlyList<RefSite> Sites, TargetingError? Error);

/// <summary>A resolved reference: a JSON Pointer to the <c>$ref</c> object in the original rule, and the segment key.</summary>
internal readonly record struct RefSite(string Pointer, string Key);
