using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Flagon.Internal.Targeting;
using Json.Logic;

namespace Flagon.Targeting.JsonEverything;

/// <summary>
/// Spike B (#23): compiles flagd targeting rules for json-everything's <c>JsonLogic</c> (model-less <see cref="JsonNode"/>
/// API). At snapshot load the rule is validated against flagd's operator set and converted once into a fully materialised
/// <see cref="JsonNode"/> tree, with the flagd operators renamed to private names (see <see cref="FlagdOperators"/>).
/// Per evaluation the <see cref="TargetingContext"/> is converted into a <see cref="JsonObject"/> holding only the
/// attributes the rule can read, and <see cref="JsonLogic.Apply(JsonNode?, JsonNode?)"/> walks the tree.
/// </summary>
internal sealed class JsonEverythingTargetingCompiler : ITargetingCompiler
{
    private static readonly TargetingCompilation NeverMatches = TargetingCompilation.Success(static (in TargetingContext _) => null);

    /// <summary>The operators flagd's targeting schema allows (flagd-schemas <c>targeting.json</c>), and no others.</summary>
    private static readonly HashSet<string> StandardOperators = new(StringComparer.Ordinal)
    {
        "var", "missing", "missing_some",
        "if", "==", "===", "!=", "!==", "!", "!!", "or", "and",
        "<", "<=", ">", ">=",
        "+", "-", "*", "/", "%", "max", "min",
        "map", "filter", "reduce", "all", "none", "some", "merge", "in",
        "cat", "substr",
    };

    static JsonEverythingTargetingCompiler() => FlagdOperators.Register();

    public TargetingCompilation Compile(string flagKey, JsonElement targeting)
    {
        // flagd treats an empty object as "no targeting" (reason STATIC); the seam has no STATIC, so it never matches.
        if (targeting.ValueKind == JsonValueKind.Object && !targeting.EnumerateObject().Any())
        {
            return NeverMatches;
        }

        var reads = new ContextReads();
        JsonNode? rule;
        try
        {
            rule = Convert(targeting, reads);
        }
        catch (UnsupportedRuleException ex)
        {
            return TargetingCompilation.Failure(ex.Message);
        }

        var plan = reads.ToPlan();
        return TargetingCompilation.Success((in TargetingContext context) =>
            ToVariant(JsonLogic.Apply(rule, ContextData.Build(context, plan))));
    }

    /// <summary>
    /// Converts a rule into a fully materialised <see cref="JsonNode"/> tree (safe to share across threads), rejecting
    /// operators outside flagd's set, renaming flagd's custom operators, and recording which context names it reads.
    /// </summary>
    private static JsonNode? Convert(JsonElement element, ContextReads reads)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
            {
                var properties = element.EnumerateObject().ToList();
                if (properties.Count != 1)
                {
                    // Not a rule: a literal object (JsonLogic and flagd's Go engine both treat it as data).
                    var literal = new JsonObject();
                    foreach (var p in properties)
                    {
                        literal[p.Name] = Literal(p.Value);
                    }

                    return literal;
                }

                var (op, args) = (properties[0].Name, properties[0].Value);
                string name;
                if (StandardOperators.Contains(op))
                {
                    name = op;
                }
                else if (FlagdOperators.PrivateName(op) is { } custom)
                {
                    name = custom;
                }
                else
                {
                    throw new UnsupportedRuleException(op == "$ref"
                        ? $"Unresolved evaluator reference {args.GetRawText()}."
                        : $"Operator '{op}' is not supported by flagd.");
                }

                reads.Observe(op, args);
                return new JsonObject { [name] = Convert(args, reads) };
            }
            case JsonValueKind.Array:
            {
                var array = new JsonArray();
                foreach (var item in element.EnumerateArray())
                {
                    array.Add(Convert(item, reads));
                }

                return array;
            }
            default:
                return Literal(element);
        }
    }

    private static JsonNode? Literal(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Object => new JsonObject(element.EnumerateObject().Select(p => KeyValuePair.Create(p.Name, Literal(p.Value)))),
        JsonValueKind.Array => new JsonArray(element.EnumerateArray().Select(Literal).ToArray()),
        JsonValueKind.String => JsonValue.Create(element.GetString()),
        JsonValueKind.Number => JsonValue.Create(element.GetDouble()),
        JsonValueKind.True => JsonValue.Create(true),
        JsonValueKind.False => JsonValue.Create(false),
        _ => null,
    };

    /// <summary>
    /// flagd's result-to-variant mapping: null means no match; a string is the variant; a boolean is "true"/"false";
    /// a number is its JSON text; anything else is its JSON text without quotes (never a variant, so an error).
    /// </summary>
    private static string? ToVariant(JsonNode? result)
    {
        switch (result)
        {
            case null:
                return null;
            case JsonValue value when value.GetValueKind() == JsonValueKind.String:
                return value.GetValue<string>();
            case JsonValue value when value.GetValueKind() is JsonValueKind.True or JsonValueKind.False:
                return value.GetValueKind() == JsonValueKind.True ? "true" : "false";
            case JsonValue value when JsonNumbers.TryGetDouble(value, out var number):
                return JsonNumbers.FormatLikeGoJson(number);
            default:
                return result.ToJsonString().Replace("\"", string.Empty, StringComparison.Ordinal);
        }
    }

    private sealed class UnsupportedRuleException(string message) : Exception(message);

    /// <summary>Collects the top-level context names a rule can read, or notes that it may read any of them.</summary>
    private sealed class ContextReads
    {
        private readonly HashSet<string> _names = new(StringComparer.Ordinal);
        private bool _any;

        public void Observe(string op, JsonElement args)
        {
            switch (op)
            {
                case "var":
                    ObservePath(args.ValueKind == JsonValueKind.Array ? args.EnumerateArray().FirstOrDefault() : args);
                    break;
                case "missing" when args.ValueKind == JsonValueKind.Array:
                    foreach (var path in args.EnumerateArray())
                    {
                        ObservePath(path);
                    }

                    break;
                case "missing":
                    ObservePath(args);
                    break;
                case "missing_some":
                    // Paths are the second argument; anything not a literal list of names means "could read anything".
                    if (args.ValueKind == JsonValueKind.Array && args.GetArrayLength() == 2 && args[1].ValueKind == JsonValueKind.Array)
                    {
                        foreach (var path in args[1].EnumerateArray())
                        {
                            ObservePath(path);
                        }
                    }
                    else
                    {
                        _any = true;
                    }

                    break;
            }
        }

        private void ObservePath(JsonElement path)
        {
            // "var" of "" / null / an expression may read the whole context; a literal path reads its first segment.
            var literal = path.ValueKind switch
            {
                JsonValueKind.String => path.GetString(),
                JsonValueKind.Number => path.GetRawText(),
                _ => null,
            };
            if (string.IsNullOrEmpty(literal))
            {
                _any = true;
                return;
            }

            var dot = literal.IndexOf('.', StringComparison.Ordinal);
            _names.Add(dot < 0 ? literal : literal[..dot]);
        }

        public ContextPlan ToPlan() => _any ? ContextPlan.All : new ContextPlan(_names.ToArray());
    }
}

/// <summary>Number helpers shared by the result mapping and the flagd operators.</summary>
internal static class JsonNumbers
{
    /// <summary>Reads a JSON number from a <see cref="JsonValue"/> as flagd's Go engine sees it (float64).</summary>
    public static bool TryGetDouble(JsonValue value, out double number)
    {
        if (value.TryGetValue(out double d)) { number = d; return true; }
        if (value.TryGetValue(out decimal m)) { number = (double)m; return true; }
        if (value.TryGetValue(out long l)) { number = l; return true; }
        if (value.TryGetValue(out int i)) { number = i; return true; }
        if (value.TryGetValue(out JsonElement e) && e.ValueKind == JsonValueKind.Number) { number = e.GetDouble(); return true; }

        number = 0;
        return false;
    }

    /// <summary>Formats a float64 as Go's <c>encoding/json</c> does: plain decimal in [1e-6, 1e21), exponent form outside.</summary>
    public static string FormatLikeGoJson(double number)
    {
        var abs = Math.Abs(number);
        if (abs == 0)
        {
            return "0";
        }

        if (abs is >= 1e-6 and < 1e21)
        {
            return abs < 7.9e28 && (double)(decimal)number == number
                ? ((decimal)number).ToString(CultureInfo.InvariantCulture)
                : number.ToString("R", CultureInfo.InvariantCulture);
        }

        return number.ToString("0.################e+00", CultureInfo.InvariantCulture);
    }
}
