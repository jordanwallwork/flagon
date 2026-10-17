using System.Text.Json;
using Flagon.Internal.Targeting.JsonLogic;

namespace Flagon.Internal.Targeting;

/// <summary>
/// What every path that compiles a flag's <c>targeting</c> shares: the snapshot load (compile, never throw) and the
/// write paths (validate, throw <see cref="FlagTargetingException"/>), so a rule is judged the same way on write as on load.
/// </summary>
internal static class TargetingRules
{
    /// <summary>Flagon's targeting engine, for validation where no service provider exists yet (<c>Define</c>).</summary>
    public static ITargetingCompiler Engine { get; } = new JsonLogicTargetingCompiler();

    /// <summary>
    /// Whether <paramref name="targeting"/> is a rule to run. Absent, JSON <c>null</c> and, as in flagd, an empty
    /// object (<c>{}</c>) are no targeting: the flag serves its default variant with reason STATIC.
    /// </summary>
    public static bool IsRule(JsonElement? targeting) =>
        targeting is { ValueKind: not (JsonValueKind.Undefined or JsonValueKind.Null) } rule
        && !(rule.ValueKind == JsonValueKind.Object && !rule.EnumerateObject().Any());

    /// <summary>Compiles <paramref name="targeting"/>; a compiler that throws or returns nothing is reported as a failure.</summary>
    public static TargetingCompilation Compile(ITargetingCompiler compiler, string key, JsonElement targeting)
    {
        try
        {
            return compiler.Compile(key, targeting) ?? TargetingCompilation.Failure("the targeting compiler returned no result");
        }
        catch (Exception ex)
        {
            return TargetingCompilation.Failure(ex.Message);
        }
    }

    /// <summary>
    /// Validates <paramref name="targeting"/> for a write with <paramref name="compiler"/> and returns what to store:
    /// null for absent or JSON <c>null</c> targeting, otherwise a detached copy of the rule.
    /// </summary>
    /// <exception cref="FlagTargetingException">The rule does not compile.</exception>
    public static JsonElement? Validate(ITargetingCompiler compiler, string key, JsonElement? targeting)
    {
        if (targeting is not { ValueKind: not (JsonValueKind.Undefined or JsonValueKind.Null) } rule)
        {
            return null;
        }

        rule = rule.Clone();
        if (IsRule(rule) && Compile(compiler, key, rule) is { Error: { } error } failure)
        {
            throw new FlagTargetingException(key, error, failure.ErrorPath, failure.ErrorOperator);
        }

        return rule;
    }

    /// <summary>Parses and validates targeting given as JSON text (as <c>Define</c> takes it).</summary>
    /// <exception cref="FlagValidationException">The text is not JSON, or the rule does not compile.</exception>
    public static JsonElement? Parse(ITargetingCompiler compiler, string key, string? targetingJson)
    {
        if (targetingJson is null)
        {
            return null;
        }

        JsonElement parsed;
        try
        {
            using var document = JsonDocument.Parse(targetingJson);
            parsed = document.RootElement.Clone();
        }
        catch (JsonException ex)
        {
            throw new FlagValidationException($"Flag '{key}' targeting is not valid JSON: {ex.Message}");
        }

        return Validate(compiler, key, parsed);
    }
}
