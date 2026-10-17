using System.Text.Json;

namespace Flagon.Internal.Targeting;

/// <summary>
/// Compiles a flag's flagd <c>targeting</c> rule (JsonLogic plus flagd operators) into a <see cref="CompiledRule"/>.
/// Called once per flag per snapshot load, never per evaluation, so implementations may do all parsing and
/// allocation up front. Replaceable in DI: register an <see cref="ITargetingCompiler"/> to override the default.
/// </summary>
internal interface ITargetingCompiler
{
    /// <summary>
    /// Compiles <paramref name="targeting"/> for the flag <paramref name="flagKey"/>. A rule that cannot be compiled is
    /// reported as <see cref="TargetingCompilation.Failure"/>; implementations should not throw (a throw is treated as
    /// a failure).
    /// </summary>
    TargetingCompilation Compile(string flagKey, JsonElement targeting);
}

/// <summary>
/// A compiled targeting rule: returns the name of the variant to serve, or null when the rule does not match
/// (the flag's <c>defaultVariant</c> is then served). Must not perform I/O.
/// </summary>
internal delegate string? CompiledRule(in TargetingContext context);

/// <summary>The outcome of <see cref="ITargetingCompiler.Compile"/>: a <see cref="Rule"/>, or an <see cref="Error"/>.</summary>
internal sealed class TargetingCompilation
{
    private TargetingCompilation(CompiledRule? rule, string? error, string? errorPath, string? errorOperator)
    {
        Rule = rule;
        Error = error;
        ErrorPath = errorPath;
        ErrorOperator = errorOperator;
    }

    /// <summary>The compiled rule; null when compilation failed.</summary>
    public CompiledRule? Rule { get; }

    /// <summary>Why compilation failed; null on success.</summary>
    public string? Error { get; }

    /// <summary>
    /// Where in the rule compilation failed, as a JSON Pointer (RFC 6901) to the failing operation (empty for the rule
    /// itself); null on success or when the compiler cannot tell.
    /// </summary>
    public string? ErrorPath { get; }

    /// <summary>The operator that failed to compile; null on success or when the compiler cannot tell.</summary>
    public string? ErrorOperator { get; }

    /// <summary>Whether the rule compiled.</summary>
    public bool Succeeded => Rule is not null;

    /// <summary>A successful compilation.</summary>
    public static TargetingCompilation Success(CompiledRule rule) => new(rule ?? throw new ArgumentNullException(nameof(rule)), null, null, null);

    /// <summary>
    /// A failed compilation, explained by <paramref name="error"/>, optionally located by <paramref name="path"/>
    /// (see <see cref="ErrorPath"/>) and <paramref name="operator"/>.
    /// </summary>
    public static TargetingCompilation Failure(string error, string? path = null, string? @operator = null) =>
        new(null, error ?? throw new ArgumentNullException(nameof(error)), path, @operator);
}
