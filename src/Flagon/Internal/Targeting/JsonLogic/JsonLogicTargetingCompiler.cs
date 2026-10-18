using System.Text;
using System.Text.Json;

namespace Flagon.Internal.Targeting.JsonLogic;

/// <summary>
/// Flagon's targeting engine (the default <see cref="ITargetingCompiler"/>; ADR 0001): compiles flagd targeting into a tree of <see cref="Node"/>s once, evaluated per call without parsing
/// and, for common rules, without allocating. Semantics follow flagd 0.18: diegoholiveira/jsonlogic v3.9.1 restricted to
/// the operators flagd's targeting schema allows, plus <c>fractional</c> (v3), <c>sem_ver</c>, <c>starts_with</c> and
/// <c>ends_with</c>. An unknown operator (including an unresolved <c>$ref</c>) or a rule the Go engine would reject
/// for any data fails compilation.
/// </summary>
internal sealed class JsonLogicTargetingCompiler : ITargetingCompiler
{
    // Flagon only compiles rules (TargetingRules.IsRule): "{}" is no targeting, as in flagd, and never reaches here.
    public TargetingCompilation Compile(string flagKey, JsonElement targeting)
    {
        try
        {
            return TargetingCompilation.Success(new Rule(CompileValue(targeting)).Run);
        }
        catch (CompileException ex)
        {
            return TargetingCompilation.Failure(ex.Message, ex.Pointer, ex.Operator);
        }
    }

    private sealed class Rule(Node root)
    {
        public string? Run(in TargetingContext context) => ToVariant(root.Eval(new Env(context)));
    }

    /// <summary>
    /// flagd's result-to-variant mapping: JSON-encode the result, then strip every <c>"</c>. Null means no match;
    /// <c>true</c> names the variant "true"; numbers use Go's JSON format.
    /// </summary>
    internal static string? ToVariant(Value v) => v.Kind switch
    {
        Kind.Null => null,
        Kind.String => NeedsEscaping(v.AsString) ? Strip(Escape(v.AsString)) : v.AsString,
        Kind.Bool => v.AsBool ? "true" : "false",
        Kind.Number => Values.FormatJson(v.Number),
        _ => Values.ToJson(v).Replace("\"", string.Empty, StringComparison.Ordinal),
    };

    private static bool NeedsEscaping(string s)
    {
        foreach (var c in s)
        {
            if (c is < ' ' or '"' or '\\' or '<' or '>' or '&' or '\u2028' or '\u2029')
            {
                return true;
            }
        }

        return false;
    }

    private static string Escape(string s)
    {
        var sb = new StringBuilder();
        Values.WriteJsonString(sb, s);
        return sb.ToString();
    }

    private static string Strip(string json) => json.Replace("\"", string.Empty, StringComparison.Ordinal);

    // --- compilation ---

    /// <summary>
    /// A compile error. While it unwinds, the innermost operation names itself as <see cref="Operator"/> and every
    /// enclosing operation and array index adds a path segment, so <see cref="Pointer"/> locates the failing operation.
    /// </summary>
    private sealed class CompileException(string message) : Exception(message)
    {
        private readonly List<string> _reversedSegments = [];

        /// <summary>The operator of the innermost operation that failed.</summary>
        public string? Operator { get; private set; }

        /// <summary>A JSON Pointer (RFC 6901) to the failing operation within the rule; empty for the rule itself.</summary>
        public string Pointer => string.Concat(Enumerable.Reverse(_reversedSegments).Select(s => "/" + s.Replace("~", "~0", StringComparison.Ordinal).Replace("/", "~1", StringComparison.Ordinal)));

        public CompileException InOperation(string name)
        {
            if (Operator is null)
            {
                Operator = name;
            }
            else
            {
                _reversedSegments.Add(name);
            }

            return this;
        }

        public CompileException AtIndex(int index)
        {
            _reversedSegments.Add(index.ToString(System.Globalization.CultureInfo.InvariantCulture));
            return this;
        }
    }

    /// <summary><see cref="CompileValue"/> for the item at <paramref name="index"/> of an array, recording the index on failure.</summary>
    private static Node CompileItem(JsonElement item, int index)
    {
        try
        {
            return CompileValue(item);
        }
        catch (CompileException ex)
        {
            throw ex.AtIndex(index);
        }
    }

    private static Node[] CompileItems(IEnumerable<JsonElement> items) => items.Select(CompileItem).ToArray();

    /// <summary>A value position (Go <c>parseValues</c>): a one-key object is an operation; anything else a literal.</summary>
    private static Node CompileValue(JsonElement e)
    {
        switch (e.ValueKind)
        {
            case JsonValueKind.Object:
                using (var members = e.EnumerateObject())
                {
                    if (!members.MoveNext())
                    {
                        return new ConstNode(Value.ObjectOf(new Dictionary<string, Value>(StringComparer.Ordinal)));
                    }

                    var first = members.Current;
                    return members.MoveNext() ? new ConstNode(Values.Constant(e)) : CompileOperation(first.Name, first.Value);
                }

            case JsonValueKind.Array:
                var items = CompileItems(e.EnumerateArray());
                return items.All(i => i is ConstNode)
                    ? new ConstNode(Value.ArrayOf(items.Select(i => ((ConstNode)i).Value).ToArray()))
                    : new ArrayNode(items);
            default:
                return new ConstNode(Values.Constant(e));
        }
    }

    private static Node CompileOperation(string name, JsonElement values)
    {
        try
        {
            return CompileOperationCore(name, values);
        }
        catch (CompileException ex)
        {
            throw ex.InOperation(name);
        }
    }

    private static Node CompileOperationCore(string name, JsonElement values) => name switch
    {
        "var" => CompileVar(values),
        "if" => new IfNode(CompileItems(RawItems(name, values))),
        "and" or "or" => new AndOrNode(CompileItems(RawItems(name, values)), name == "and"),
        "map" => CompileIteration(IterationOp.Map, name, values),
        "filter" => CompileIteration(IterationOp.Filter, name, values),
        "all" => CompileIteration(IterationOp.All, name, values),
        "none" => CompileIteration(IterationOp.None, name, values),
        "some" => CompileIteration(IterationOp.Some, name, values),
        "reduce" => CompileIteration(IterationOp.Reduce, name, values),
        "fractional" => new FractionalNode(CompileArgs(values)),
        "starts_with" => new OpNode(Op.StartsWith, CompileArgs(values)),
        "ends_with" => new OpNode(Op.EndsWith, CompileArgs(values)),
        "sem_ver" => new OpNode(Op.SemVer, CompileArgs(values)),
        "===" => new OpNode(Op.StrictEq, CompileArgs(values)),
        "!==" => new OpNode(Op.StrictNeq, CompileArgs(values)),
        "!" => new OpNode(Op.Not, CompileArgs(values)),
        "!!" => new OpNode(Op.NotNot, CompileArgs(values)),
        "+" => new OpNode(Op.Plus, CompileArgs(values)),
        "==" => Checked(Op.Eq, name, values),
        "!=" => Checked(Op.Neq, name, values),
        "<" => Checked(Op.Lt, name, values),
        "<=" => Checked(Op.Le, name, values),
        ">" => Checked(Op.Gt, name, values),
        ">=" => Checked(Op.Ge, name, values),
        "in" => Checked(Op.In, name, values, minItems: 1),
        "substr" => Checked(Op.Substr, name, values, minItems: 2),
        "%" => Checked(Op.Mod, name, values, minItems: 2),
        "missing_some" => Checked(Op.MissingSome, name, values, minItems: 2),
        "-" => Checked(Op.Minus, name, values),
        "*" => Checked(Op.Mul, name, values),
        "/" => Checked(Op.Div, name, values),
        "max" => Checked(Op.Max, name, values),
        "min" => Checked(Op.Min, name, values),
        "cat" => Checked(Op.Cat, name, values, scalar: JsonValueKind.String),
        "missing" => Checked(Op.Missing, name, values, scalar: JsonValueKind.String),
        "merge" => Checked(Op.Merge, name, values, scalar: JsonValueKind.Number),
        "$ref" => throw new CompileException(
            $"unresolved $ref {values.GetRawText()}: Flagon stores flags individually and has no flag-set $evaluators, so inline the evaluator's rule in place of the reference"),
        _ => throw new CompileException($"unsupported operator '{name}'"),
    };

    private static Args CompileArgs(JsonElement values) => values.ValueKind == JsonValueKind.Array
        ? new Args(CompileItems(values.EnumerateArray()), null)
        : new Args(null, CompileValue(values));

    // An operator the Go engine panics on unless its evaluated arguments are an array: a literal non-array (other than
    // an allowed scalar) or a too-short literal array can never work, so it is a compile error.
    private static OpNode Checked(Op op, string name, JsonElement values, int minItems = 0, JsonValueKind scalar = JsonValueKind.Undefined)
    {
        var args = CompileArgs(values);
        var literalScalar = args.Single is ConstNode c
                            && !(scalar == JsonValueKind.String && c.Value.Kind == Kind.String)
                            && !(scalar == JsonValueKind.Number && c.Value.Kind is Kind.Bool or Kind.Number or Kind.String);
        if (literalScalar || (args.Items is { } items && items.Length < minItems))
        {
            throw new CompileException($"'{name}' needs an array of at least {Math.Max(minItems, 1)} argument(s), got {values.GetRawText()}");
        }

        return new OpNode(op, args);
    }

    // Operators the Go engine reads as a raw array (values.([]any)) before evaluating anything.
    private static IEnumerable<JsonElement> RawItems(string name, JsonElement values, int minItems = 0)
    {
        if (values.ValueKind != JsonValueKind.Array || values.GetArrayLength() < minItems)
        {
            throw new CompileException($"'{name}' needs an array of at least {minItems} argument(s), got {values.GetRawText()}");
        }

        return values.EnumerateArray();
    }

    private static Node CompileVar(JsonElement values)
    {
        switch (values.ValueKind)
        {
            case JsonValueKind.String:
                return VarNode.Path(values.GetString()!, null, rawString: true);
            case JsonValueKind.Number:
                return VarNode.Path(Values.FormatPlain(values.GetDouble()), null, rawString: false);
            case JsonValueKind.Null:
                return VarNode.NullPath();
            case JsonValueKind.Array:
                var n = values.GetArrayLength();
                if (n == 0)
                {
                    return VarNode.Path(string.Empty, null, rawString: false);
                }

                var path = values[0];
                var defaultNode = n == 2 ? CompileItem(values[1], 1) : null;
                if (path.ValueKind == JsonValueKind.String)
                {
                    return VarNode.Path(path.GetString()!, defaultNode, rawString: false);
                }

                if (path.ValueKind == JsonValueKind.Object)
                {
                    return VarNode.Dynamic(CompileValue(values));
                }

                throw new CompileException($"'var' path must be a string, got {path.GetRawText()}");
            case JsonValueKind.Object:
                return VarNode.Dynamic(CompileValue(values));
            default:
                throw new CompileException($"'var' path must be a string, got {values.GetRawText()}");
        }
    }

    private static Node CompileIteration(IterationOp op, string name, JsonElement values)
    {
        var items = RawItems(name, values, op is IterationOp.Map or IterationOp.Filter ? 0 : op == IterationOp.Reduce ? 0 : 2).ToArray();
        if (op is IterationOp.Map or IterationOp.Filter && items.Length < 2)
        {
            return new ConstNode(Value.EmptyArray);
        }

        if (op == IterationOp.Reduce && items.Length < 3)
        {
            return new ConstNode(Value.Of(0));
        }

        // apply(logic, element) requires an object for all/none/some/reduce.
        if (op is IterationOp.All or IterationOp.None or IterationOp.Some or IterationOp.Reduce && items[1].ValueKind != JsonValueKind.Object)
        {
            throw new CompileException($"'{name}' needs a rule object as its second argument, got {items[1].GetRawText()}");
        }

        // The subject is a raw literal array (elements unevaluated) or a rule; anything else is no subject.
        Node? subject = items[0].ValueKind switch
        {
            JsonValueKind.Array => new ConstNode(Values.Constant(items[0])),
            JsonValueKind.Object => CompileItem(items[0], 0),
            _ => null,
        };
        Node? initial = op == IterationOp.Reduce
            ? items[2].ValueKind == JsonValueKind.Object ? CompileItem(items[2], 2) : new ConstNode(Values.Constant(items[2]))
            : null;
        return new IterationNode(op, subject, CompileItem(items[1], 1), initial);
    }
}
