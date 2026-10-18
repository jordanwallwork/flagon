using System.Text.Json;

namespace Jorrk.Flagon.Internal.Targeting;

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
    private TargetingCompilation(CompiledRule? rule, TargetingError? error)
    {
        Rule = rule;
        Error = error;
    }

    /// <summary>The compiled rule; null when compilation failed.</summary>
    public CompiledRule? Rule { get; }

    /// <summary>Why and where compilation failed; null on success.</summary>
    public TargetingError? Error { get; }

    /// <summary>Whether the rule compiled.</summary>
    public bool Succeeded => Rule is not null;

    /// <summary>A successful compilation.</summary>
    public static TargetingCompilation Success(CompiledRule rule) => new(rule ?? throw new ArgumentNullException(nameof(rule)), null);

    /// <summary>
    /// A failed compilation, explained by <paramref name="message"/>, optionally located by <paramref name="path"/>
    /// and <paramref name="operator"/> (see <see cref="TargetingError"/>).
    /// </summary>
    public static TargetingCompilation Failure(string message, string? path = null, string? @operator = null) =>
        new(null, new TargetingError(message ?? throw new ArgumentNullException(nameof(message)), path, @operator));
}

/// <summary>Why a targeting rule was rejected, and where: the shape <see cref="FlagTargetingException"/> exposes.</summary>
/// <param name="Message">Why the rule was rejected (<see cref="FlagTargetingException.Error"/>).</param>
/// <param name="Path">
/// A JSON Pointer (RFC 6901) to the failing operation within the rule (empty for the rule itself); null when the
/// compiler cannot tell.
/// </param>
/// <param name="Operator">The operator that failed; null when the compiler cannot tell.</param>
internal sealed record TargetingError(string Message, string? Path, string? Operator);
