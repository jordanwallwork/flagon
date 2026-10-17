using System.Text.Json;
using System.Text.Json.Nodes;
using Json.Logic;

namespace Flagon.Targeting.JsonEverything;

/// <summary>
/// flagd's custom operators (<c>fractional</c>, <c>sem_ver</c>, <c>starts_with</c>, <c>ends_with</c>) as JsonLogic
/// <see cref="IRule"/> handlers.
/// </summary>
/// <remarks>
/// json-everything's <see cref="RuleRegistry"/> is process-global and <c>AddRule</c> overwrites. Registering the public
/// flagd names would clash with any other user of the library in the process (notably the OpenFeature flagd provider,
/// which registers the same names). So the compiler renames each flagd operator in the rule to a private name that
/// user rules cannot spell (the validator only admits flagd's own operator names), and only those are registered.
/// Like flagd (diegoholiveira <c>AddOperator</c>), every argument is evaluated before the operator runs, and a bad
/// argument yields null (no match), never an error.
/// </remarks>
internal static class FlagdOperators
{
    private const string Prefix = "flagon/";

    private static readonly Dictionary<string, IRule> Rules = new(StringComparer.Ordinal)
    {
        ["fractional"] = new FractionalRule(),
        ["sem_ver"] = new SemVerRule(),
        ["starts_with"] = new StringComparisonRule(static (value, target) => value.StartsWith(target, StringComparison.Ordinal)),
        ["ends_with"] = new StringComparisonRule(static (value, target) => value.EndsWith(target, StringComparison.Ordinal)),
    };

    private static int _registered;

    /// <summary>The private name a flagd operator is registered under, or null when <paramref name="op"/> is not one.</summary>
    public static string? PrivateName(string op) => Rules.ContainsKey(op) ? Prefix + op : null;

    /// <summary>Registers the operators under their private names (once per process).</summary>
    public static void Register()
    {
        if (Interlocked.Exchange(ref _registered, 1) == 1)
        {
            return;
        }

        foreach (var (name, rule) in Rules)
        {
            RuleRegistry.AddRule(Prefix + name, rule);
        }
    }

    /// <summary>Evaluates every argument, as flagd does before calling a custom operator; null when args is not an array.</summary>
    internal static JsonArray? EvaluateArguments(JsonNode? args, Json.Logic.EvaluationContext context) =>
        args is JsonArray ? JsonLogic.Apply(args, context) as JsonArray : null;

    internal static string? AsString(JsonNode? node) =>
        node is JsonValue value && value.GetValueKind() == JsonValueKind.String ? value.GetValue<string>() : null;

    private sealed class StringComparisonRule(Func<string, string, bool> compare) : IRule
    {
        public JsonNode? Apply(JsonNode? args, Json.Logic.EvaluationContext context)
        {
            if (EvaluateArguments(args, context) is not { Count: 2 } values
                || AsString(values[0]) is not { } value
                || AsString(values[1]) is not { } target)
            {
                return null;
            }

            return compare(value, target);
        }
    }
}
