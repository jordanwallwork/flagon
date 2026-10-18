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
    /// Failures that leave the process in no state to carry on: these propagate rather than becoming a broken rule or a
    /// PARSE_ERROR, whether a rule is being compiled or run.
    /// </summary>
    public static bool IsFatal(Exception ex) => ex is OutOfMemoryException or StackOverflowException or AccessViolationException;

    /// <summary>
    /// Validates <paramref name="targeting"/> for a write with <paramref name="compiler"/> and returns what to store:
    /// null for absent or JSON <c>null</c> targeting, otherwise a detached copy of the rule.
    /// </summary>
    /// <exception cref="FlagTargetingException">The rule does not compile.</exception>
    public static JsonElement? Validate(ITargetingCompiler compiler, string key, JsonElement? targeting)
    {
        if (!IsPresent(targeting, out var rule))
        {
            return null;
        }

        rule = rule.Clone();
        if (IsRule(rule) && Compile(compiler, key, rule).Error is { } error)
        {
            throw new FlagTargetingException(key, error);
        }

        return rule;
    }

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
