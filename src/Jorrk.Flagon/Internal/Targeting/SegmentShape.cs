using System.Text.Json;

namespace Jorrk.Flagon.Internal.Targeting;

/// <summary>
/// Recognises the targeting shape tooling can present as "serve a variant to segments": exactly
/// <c>{"if":[{"$ref":"a"},"&lt;variant&gt;",null]}</c>, or <c>{"if":[{"or":[{"$ref":"a"},{"$ref":"b"},…]},"&lt;variant&gt;",null]}</c>
/// for two or more distinct segments, where the variant is a literal name defined on the flag. Defined once, here, so
/// every consumer agrees; any other use of <c>$ref</c> is a custom rule and must be edited as one.
/// </summary>
internal static class SegmentShape
{
    /// <summary>The segments and variant if <paramref name="targeting"/> is exactly the canonical shape over <paramref name="variants"/>, otherwise null.</summary>
    public static SegmentTargeting? Match(JsonElement? targeting, IReadOnlyDictionary<string, JsonElement> variants)
    {
        if (Single(targeting, "if") is not { ValueKind: JsonValueKind.Array } args || args.GetArrayLength() != 3
            || args[1].ValueKind != JsonValueKind.String || !variants.ContainsKey(args[1].GetString()!)
            || args[2].ValueKind != JsonValueKind.Null)
        {
            return null;
        }

        var variant = args[1].GetString()!;
        if (Reference(args[0]) is { } only)
        {
            return new SegmentTargeting([only], variant);
        }

        if (Single(args[0], "or") is not { ValueKind: JsonValueKind.Array } any || any.GetArrayLength() < 2)
        {
            return null;
        }

        var segments = new List<string>();
        foreach (var item in any.EnumerateArray())
        {
            if (Reference(item) is not { } key || segments.Contains(key, StringComparer.Ordinal))
            {
                return null;
            }

            segments.Add(key);
        }

        return new SegmentTargeting(segments, variant);
    }

    // The segment key of exactly {"$ref": "<key>"}.
    private static string? Reference(JsonElement e) =>
        Single(e, SegmentRefs.Operator) is { ValueKind: JsonValueKind.String } key ? key.GetString() : null;

    // The operand of a one-key object with that key.
    private static JsonElement? Single(JsonElement? e, string name)
    {
        if (e is not { ValueKind: JsonValueKind.Object } obj)
        {
            return null;
        }

        using var properties = obj.EnumerateObject();
        if (!properties.MoveNext() || properties.Current.Name != name)
        {
            return null;
        }

        var operand = properties.Current.Value;
        return properties.MoveNext() ? null : operand;
    }
}

/// <summary>A canonical segment targeting rule: serve <see cref="Variant"/> to anyone in any of <see cref="Segments"/> (in rule order).</summary>
internal sealed record SegmentTargeting(IReadOnlyList<string> Segments, string Variant);

/// <summary>
/// Recognises the one segment rule shape tooling can present as a list of targeting keys: exactly
/// <c>{"in":[{"var":"targetingKey"},["k1","k2",…]]}</c> with string keys (possibly none). Anything else is edited as a rule.
/// </summary>
internal static class SegmentListShape
{
    /// <summary>The targeting keys if <paramref name="rule"/> is exactly the canonical list shape, otherwise null.</summary>
    public static IReadOnlyList<string>? Keys(JsonElement rule)
    {
        if (rule.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        using var properties = rule.EnumerateObject();
        if (!properties.MoveNext() || properties.Current.Name != "in")
        {
            return null;
        }

        var args = properties.Current.Value;
        if (properties.MoveNext() || args.ValueKind != JsonValueKind.Array || args.GetArrayLength() != 2 || !IsTargetingKey(args[0])
            || args[1].ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var keys = new List<string>(args[1].GetArrayLength());
        foreach (var key in args[1].EnumerateArray())
        {
            if (key.ValueKind != JsonValueKind.String)
            {
                return null;
            }

            keys.Add(key.GetString()!);
        }

        return keys;
    }

    // Exactly {"var": "targetingKey"}.
    private static bool IsTargetingKey(JsonElement e)
    {
        if (e.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        using var properties = e.EnumerateObject();
        return properties.MoveNext() && properties.Current.Name == "var"
            && properties.Current.Value.ValueKind == JsonValueKind.String && properties.Current.Value.GetString() == TargetingContext.TargetingKeyName
            && !properties.MoveNext();
    }
}
