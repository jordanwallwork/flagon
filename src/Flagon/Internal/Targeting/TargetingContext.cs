namespace Flagon.Internal.Targeting;

/// <summary>
/// What a <see cref="CompiledRule"/> sees, as flagd builds it: the <see cref="EvaluationContext"/>'s attributes with
/// <c>targetingKey</c> injected, plus <c>$flagd.flagKey</c> and <c>$flagd.timestamp</c>. A struct passed by
/// reference, so building one per evaluation allocates nothing.
/// </summary>
internal readonly struct TargetingContext
{
    /// <summary>The <c>var</c> name of the injected targeting key.</summary>
    public const string TargetingKeyName = "targetingKey";

    /// <summary>The <c>var</c> path of the evaluated flag's key.</summary>
    public const string FlagKeyPath = "$flagd.flagKey";

    /// <summary>The <c>var</c> path of the evaluation time, in Unix seconds.</summary>
    public const string TimestampPath = "$flagd.timestamp";

    private static readonly IReadOnlyDictionary<string, object?> NoAttributes = new Dictionary<string, object?>(StringComparer.Ordinal);

    public TargetingContext(string flagKey, EvaluationContext? context, long timestamp)
    {
        FlagKey = flagKey;
        TargetingKey = context?.TargetingKey;
        Attributes = context?.Attributes ?? NoAttributes;
        Timestamp = timestamp;
    }

    /// <summary>The key of the flag being evaluated (<c>$flagd.flagKey</c>).</summary>
    public string FlagKey { get; }

    /// <summary>The evaluation context's targeting key, if any (<c>targetingKey</c>).</summary>
    public string? TargetingKey { get; }

    /// <summary>The evaluation context's attributes, as passed (never null).</summary>
    public IReadOnlyDictionary<string, object?> Attributes { get; }

    /// <summary>The evaluation time in Unix seconds (<c>$flagd.timestamp</c>).</summary>
    public long Timestamp { get; }

    /// <summary>
    /// Resolves a top-level <c>var</c> name: <see cref="TargetingKeyName"/> (the injected targeting key wins over an
    /// attribute of that name), <see cref="FlagKeyPath"/>, <see cref="TimestampPath"/>, else an attribute.
    /// Traversing into nested values (dotted paths below an attribute) is left to the compiler.
    /// </summary>
    public bool TryGetValue(string name, out object? value)
    {
        switch (name)
        {
            case TargetingKeyName when TargetingKey is not null:
                value = TargetingKey;
                return true;
            case FlagKeyPath:
                value = FlagKey;
                return true;
            case TimestampPath:
                value = Timestamp;
                return true;
            default:
                return Attributes.TryGetValue(name, out value);
        }
    }
}
