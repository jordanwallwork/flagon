using System.Text.Json;

namespace Jorrk.Flagon.Internal.Targeting;

/// <summary>
/// Recognises the one targeting shape tooling can present as a percentage split: exactly
/// <c>{"fractional": [[v1, w1], …, [vn, wn]]}</c> with n ≥ 2, literal and distinct variant names that are all defined on
/// the flag, integral weights from 0 to 100 summing to 100, and default bucketing (no explicit bucketing value, so users
/// are bucketed by flag key and targeting key). Any JSON number with no fractional part counts as a weight (<c>25.0</c>
/// and <c>2.5e1</c> are 25), as the fractional engine weights them identically. A split of exactly
/// <c>[["on", N], ["off", 100 - N]]</c> of a boolean flag is also the boolean percentage rollout N. Defined once, here, so every consumer
/// agrees; anything else, however close, is a custom rule and must be edited as one.
/// </summary>
internal static class RolloutShape
{
    /// <summary>The split if <paramref name="targeting"/> is exactly the canonical shape over <paramref name="variants"/>, otherwise null.</summary>
    public static RolloutSplit? Split(JsonElement? targeting, IReadOnlyDictionary<string, JsonElement> variants)
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

        var rawBuckets = properties.Current.Value;
        if (properties.MoveNext() || rawBuckets.ValueKind != JsonValueKind.Array || rawBuckets.GetArrayLength() < 2)
        {
            return null;
        }

        var buckets = new List<RolloutBucket>(rawBuckets.GetArrayLength());
        var total = 0;
        foreach (var raw in rawBuckets.EnumerateArray())
        {
            if (Bucket(raw) is not { } bucket || !variants.ContainsKey(bucket.Variant) || buckets.Exists(b => b.Variant == bucket.Variant))
            {
                return null;
            }

            buckets.Add(bucket);
            total += bucket.Percentage;
        }

        return total == 100 ? new RolloutSplit(buckets, FlagVariants.CommonType(variants) == FlagType.Boolean) : null;
    }

    // A literal name and an integral weight from 0 to 100.
    private static RolloutBucket? Bucket(JsonElement bucket) =>
        bucket.ValueKind == JsonValueKind.Array && bucket.GetArrayLength() == 2
        && bucket[0].ValueKind == JsonValueKind.String
        && bucket[1].ValueKind == JsonValueKind.Number && bucket[1].TryGetDouble(out var weight)
        && weight == Math.Truncate(weight) && weight is >= 0 and <= 100
            ? new RolloutBucket(bucket[0].GetString()!, (int)weight)
            : null;
}

/// <summary>A canonical percentage split, its buckets in rule order, and whether it is over a boolean flag's variants.</summary>
internal sealed record RolloutSplit(IReadOnlyList<RolloutBucket> Buckets, bool OfBooleanFlag)
{
    /// <summary>
    /// The boolean rollout percentage N when the flag is a boolean flag and the split is exactly
    /// <c>[["on", N], ["off", 100 - N]]</c>, otherwise null (variants of another type named on/off are just a split).
    /// </summary>
    public int? Percentage => OfBooleanFlag && Buckets is [{ Variant: FlagDocument.OnVariant } on, { Variant: FlagDocument.OffVariant }] ? on.Percentage : null;

    /// <summary>True when <paramref name="variant"/> is one of the split's buckets.</summary>
    public bool Contains(string variant) => Buckets.Any(b => b.Variant == variant);
}

/// <summary>One bucket of a split: the share of users, in percent, who get <see cref="Variant"/>.</summary>
internal sealed record RolloutBucket(string Variant, int Percentage);
