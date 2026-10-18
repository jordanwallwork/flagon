using System.Text.Json;

namespace Flagon.Internal.Targeting;

/// <summary>
/// Recognises the one targeting shape tooling can present as a simple percentage rollout:
/// exactly <c>{"fractional": [["on", N], ["off", 100 - N]]}</c>, with default bucketing (no explicit bucketing value,
/// so users are bucketed by flag key and targeting key) and an integral N from 0 to 100. Any JSON number with no
/// fractional part counts (<c>25.0</c> and <c>2.5e1</c> are 25), as the fractional engine weights them identically. Defined once, here, so every
/// consumer agrees; anything else, however close, is not a rollout and must be edited as a rule.
/// </summary>
internal static class RolloutShape
{
    /// <summary>The rollout percentage N if <paramref name="targeting"/> is exactly the canonical shape, otherwise null.</summary>
    public static int? Percentage(JsonElement? targeting)
    {
        if (targeting is not { ValueKind: JsonValueKind.Object } rule)
        {
            return null;
        }

        using var properties = rule.EnumerateObject();
        if (!properties.MoveNext() || properties.Current.Name != "fractional")
        {
            return null;
        }

        var buckets = properties.Current.Value;
        if (properties.MoveNext() || buckets.ValueKind != JsonValueKind.Array || buckets.GetArrayLength() != 2)
        {
            return null;
        }

        return Bucket(buckets[0], FlagDocument.OnVariant) is { } on && Bucket(buckets[1], FlagDocument.OffVariant) is { } off
            && on is >= 0 and <= 100 && on + off == 100
            ? on
            : null;
    }

    // Any integral weight; the bounds only keep the cast exact (a rollout's weights are within 0..100 anyway).
    private static int? Bucket(JsonElement bucket, string variant) =>
        bucket.ValueKind == JsonValueKind.Array && bucket.GetArrayLength() == 2
        && bucket[0].ValueKind == JsonValueKind.String && bucket[0].ValueEquals(variant)
        && bucket[1].ValueKind == JsonValueKind.Number && bucket[1].TryGetDouble(out var weight)
        && weight == Math.Truncate(weight) && weight is >= -100 and <= 200
            ? (int)weight
            : null;
}
