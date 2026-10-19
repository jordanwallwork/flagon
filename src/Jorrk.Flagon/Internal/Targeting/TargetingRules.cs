using System.Text.Json;

namespace Jorrk.Flagon.Internal.Targeting;

/// <summary>
/// What every path that compiles a flag's <c>targeting</c> shares, always with the registered
/// <see cref="ITargetingCompiler"/>: the snapshot load (compile, never throw) and the write paths, including flags
/// defined in code when they are seeded (validate, throw <see cref="FlagTargetingException"/>), so a rule is judged the
/// same way on write as on load.
/// </summary>
internal static class TargetingRules
{
    /// <summary>
    /// Whether <paramref name="targeting"/> is a rule to run. Absent, JSON <c>null</c> and, as in flagd, an empty
    /// object (<c>{}</c>) are no targeting: the flag serves its default variant with reason STATIC. Compilers are only
    /// ever given rules.
    /// </summary>
    public static bool IsRule(JsonElement? targeting) =>
        IsPresent(targeting, out var rule) && !(rule.ValueKind == JsonValueKind.Object && !rule.EnumerateObject().Any());

    /// <summary>Compiles <paramref name="targeting"/>; a compiler that throws or returns nothing is reported as a failure.</summary>
    public static TargetingCompilation Compile(ITargetingCompiler compiler, string key, JsonElement targeting)
    {
        try
        {
            return compiler.Compile(key, targeting) ?? TargetingCompilation.Failure("the targeting compiler returned no result");
        }
        catch (Exception ex) when (!IsFatal(ex))
        {
            return TargetingCompilation.Failure(ex.Message);
        }
    }

    /// <summary>
    /// Compiles <paramref name="targeting"/> after inlining its segment references from <paramref name="segments"/>
    /// (segment key to rule). Null <paramref name="segments"/> means the store has no segments: a <c>$ref</c> then reaches
    /// the compiler, which rejects it. An unresolved or malformed reference fails at the reference; a failure inside an
    /// inlined segment is reported at its reference, naming the segment.
    /// </summary>
    public static TargetingCompilation Compile(
        ITargetingCompiler compiler, string key, JsonElement targeting, IReadOnlyDictionary<string, JsonElement>? segments)
    {
        if (segments is null)
        {
            return Compile(compiler, key, targeting);
        }

        var inlined = SegmentRefs.Inline(targeting, segments);
        if (inlined.Error is { } refError)
        {
            return TargetingCompilation.Failure(refError.Message, refError.Path, refError.Operator);
        }

        var compiled = Compile(compiler, key, inlined.Rule);
        if (compiled.Error is not { } error || inlined.Sites.Count == 0)
        {
            return compiled;
        }

        var mapped = SegmentRefs.MapError(error, inlined.Sites);
        return TargetingCompilation.Failure(mapped.Message, mapped.Path, mapped.Operator);
    }

    /// <summary>
    /// Failures that leave the process in no state to carry on: these propagate rather than becoming a broken rule or a
    /// PARSE_ERROR, whether a rule is being compiled or run.
    /// </summary>
    public static bool IsFatal(Exception ex) => ex is OutOfMemoryException or StackOverflowException or AccessViolationException;

    /// <summary>
    /// Validates <paramref name="targeting"/> for a write with <paramref name="compiler"/> and returns what to store:
    /// null for absent or JSON <c>null</c> targeting, otherwise a detached copy of the rule.
    /// </summary>
    /// <exception cref="FlagTargetingException">The rule does not compile.</exception>
    public static JsonElement? Validate(ITargetingCompiler compiler, string key, JsonElement? targeting) =>
        Validate(compiler, key, targeting, segments: null);

    /// <summary>
    /// <see cref="Validate(ITargetingCompiler, string, JsonElement?)"/> with segment references inlined from
    /// <paramref name="segments"/> first (see <see cref="Compile(ITargetingCompiler, string, JsonElement, IReadOnlyDictionary{string, JsonElement}?)"/>).
    /// What is stored keeps its references.
    /// </summary>
    /// <exception cref="FlagTargetingException">The rule does not compile, or references a segment that does not exist.</exception>
    public static JsonElement? Validate(
        ITargetingCompiler compiler, string key, JsonElement? targeting, IReadOnlyDictionary<string, JsonElement>? segments)
    {
        if (!IsPresent(targeting, out var rule))
        {
            return null;
        }

        rule = rule.Clone();
        if (IsRule(rule) && Compile(compiler, key, rule, segments).Error is { } error)
        {
            throw new FlagTargetingException(key, error);
        }

        return rule;
    }

    /// <summary>
    /// Validates a segment's rule for a write and returns a detached copy: it must be a rule (not absent, <c>null</c> or
    /// <c>{}</c>), contain no <c>$ref</c> (segments do not nest) and compile on its own with <paramref name="compiler"/>.
    /// </summary>
    /// <exception cref="SegmentRuleException">The rule is rejected.</exception>
    public static JsonElement ValidateSegmentRule(ITargetingCompiler compiler, string key, JsonElement? rule)
    {
        if (!IsRule(rule))
        {
            throw new SegmentRuleException(key, "a segment needs a rule (it may not be absent, null or {})");
        }

        var copy = rule!.Value.Clone();
        if (SegmentRefs.FindAny(copy) is { } pointer)
        {
            throw new SegmentRuleException(
                key, "a segment's rule may not reference another segment; compose segments in the flag's rule instead", pointer, SegmentRefs.Operator);
        }

        if (Compile(compiler, key, copy).Error is { } error)
        {
            throw new SegmentRuleException(key, error);
        }

        return copy;
    }

    /// <summary>The rules of <paramref name="segments"/>, archived ones included, keyed by segment key, to inline from.</summary>
    public static IReadOnlyDictionary<string, JsonElement> RulesOf(IEnumerable<SegmentRecord> segments) =>
        segments.ToDictionary(s => s.Key, s => s.Document.Rule, StringComparer.Ordinal);

    /// <summary>
    /// Parses targeting given as JSON text (as <c>Define</c> takes it) without compiling it: the rule is validated with
    /// the registered compiler when the flag is seeded. Returns null for JSON <c>null</c>.
    /// </summary>
    /// <exception cref="FlagValidationException">The text is not JSON.</exception>
    public static JsonElement? ParseJson(string key, string? targetingJson)
    {
        if (targetingJson is null)
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(targetingJson);
            return IsPresent(document.RootElement, out var rule) ? rule.Clone() : null;
        }
        catch (JsonException ex)
        {
            throw new FlagValidationException($"Flag '{key}' targeting is not valid JSON: {ex.Message}");
        }
    }

    private static bool IsPresent(JsonElement? targeting, out JsonElement rule)
    {
        rule = targeting.GetValueOrDefault();
        return targeting is { ValueKind: not (JsonValueKind.Undefined or JsonValueKind.Null) };
    }
}
