namespace Flagon.Internal.Targeting.OwnJsonLogic;

/// <summary>
/// What a node evaluates against: the targeting context and the current data object (<see cref="Value.Root"/>, or an
/// element inside <c>map</c>/<c>filter</c>/...). <see cref="Outer"/> reproduces the Go engine's <c>solveVars</c>, which
/// resolves <c>var</c>s inside <c>filter</c>/<c>some</c>/<c>none</c> against the enclosing data first.
/// </summary>
internal readonly ref struct Env
{
    public Env(in TargetingContext context)
    {
        Context = context;
        Data = Value.Root;
    }

    public Env(in TargetingContext context, Value data, Value outer = default, bool hasOuter = false)
    {
        Context = context;
        Data = data;
        Outer = outer;
        HasOuter = hasOuter;
    }

    public readonly TargetingContext Context;
    public readonly Value Data;
    public readonly Value Outer;
    public readonly bool HasOuter;
}

/// <summary>A compiled JsonLogic expression. Stateless, so one tree serves concurrent evaluations.</summary>
internal abstract class Node
{
    public abstract Value Eval(in Env env);
}

internal sealed class ConstNode(Value value) : Node
{
    public Value Value { get; } = value;

    public override Value Eval(in Env env) => Value;
}

/// <summary>An array literal with rule elements, evaluated element-wise (Go <c>parseValues</c>). Allocates its result.</summary>
internal sealed class ArrayNode(Node[] items) : Node
{
    public Node[] Items { get; } = items;

    public override Value Eval(in Env env)
    {
        var result = new Value[Items.Length];
        for (var i = 0; i < result.Length; i++)
        {
            result[i] = Items[i].Eval(env);
        }

        return Value.ArrayOf(result);
    }
}

/// <summary>
/// An operator's arguments with Go <c>parseValues</c> semantics: a literal array (each element its own node, read
/// without building an array), or a single expression whose value is unpacked at evaluation.
/// </summary>
internal sealed class Args(Node[]? items, Node? single)
{
    public Node[]? Items { get; } = items;

    public Node? Single { get; } = single;

    /// <summary>True when the arguments are an array; otherwise <paramref name="list"/>.Scalar holds the single value.</summary>
    public bool Resolve(in Env env, out ArgList list)
    {
        if (Items is { } items)
        {
            list = new ArgList(items, default, items.Length);
            return true;
        }

        var value = Single!.Eval(env);
        list = new ArgList(null, value, value.Kind == Kind.Array ? Values.Count(value) : 0);
        return value.Kind == Kind.Array;
    }
}

internal readonly struct ArgList(Node[]? items, Value value, int count)
{
    public int Count { get; } = count;

    public Value Scalar => value;

    public Value Get(int i, in Env env) => items is not null ? items[i].Eval(env) : Values.Item(value, i);
}

/// <summary><c>var</c>: a dotted path (pre-split when constant) with an optional default, as diegoholiveira <c>getVar</c>.</summary>
internal sealed class VarNode : Node
{
    private readonly string[]? _parts;
    private readonly Node? _default;
    private readonly Node? _dynamic;
    private readonly bool _nullPath;
    private readonly bool _skipOuter;

    private VarNode(string[]? parts, Node? defaultNode, Node? dynamic, bool nullPath, bool skipOuter)
    {
        _parts = parts;
        _default = defaultNode;
        _dynamic = dynamic;
        _nullPath = nullPath;
        _skipOuter = skipOuter;
    }

    public static VarNode Path(string path, Node? defaultNode, bool rawString) =>
        new(Split(path), defaultNode, null, false, rawString && (path.Length == 0 || path.StartsWith('.')));

    public static VarNode NullPath() => new(null, null, null, true, false);

    public static VarNode Dynamic(Node spec) => new(null, null, spec, false, false);

    public static string[] Split(string path) => path.Split('.', StringSplitOptions.RemoveEmptyEntries);

    public override Value Eval(in Env env)
    {
        if (env.HasOuter && !_skipOuter)
        {
            var outer = new Env(env.Context, env.Outer);
            var resolved = EvalIn(outer);
            if (!resolved.IsNull)
            {
                return resolved;
            }
        }

        return EvalIn(env);
    }

    private Value EvalIn(in Env env)
    {
        if (_dynamic is not null)
        {
            return GetVar(env, _dynamic.Eval(env));
        }

        if (_nullPath)
        {
            return env.Data.Kind is Kind.Array or Kind.Object or Kind.Null ? Value.Null : env.Data;
        }

        return Resolve(env, env.Data, _parts!, _default?.Eval(env) ?? Value.Null);
    }

    /// <summary><c>getVar</c> on an already evaluated specification (dynamic <c>var</c>, <c>missing</c>).</summary>
    public static Value GetVar(in Env env, Value spec)
    {
        var dflt = Value.Null;
        switch (spec.Kind)
        {
            case Kind.Null:
                return env.Data.Kind is Kind.Array or Kind.Object ? Value.Null : env.Data;
            case Kind.Number:
                spec = Value.Of(Values.FormatPlain(spec.Number));
                break;
            case Kind.Array:
                var n = Values.Count(spec);
                if (n == 0)
                {
                    return Values.Materialize(env.Context, env.Data);
                }

                if (n == 2)
                {
                    dflt = Values.Item(spec, 1);
                }

                spec = Values.Item(spec, 0);
                if (spec.Kind != Kind.String)
                {
                    throw new JsonLogicException("var path must be a string");
                }

                break;
            case Kind.String:
                break;
            default:
                throw new JsonLogicException("var path must be a string");
        }

        return Resolve(env, env.Data, Split(spec.AsString), dflt);
    }

    public static Value Resolve(in Env env, Value data, string[] parts, Value dflt)
    {
        var current = data;
        foreach (var part in parts)
        {
            if (current.Kind == Kind.Object)
            {
                Values.TryGetMember(env.Context, current, part, out current);
            }
            else if (current.Kind == Kind.Array)
            {
                var position = Values.TryParseGoFloat(part, out var d) ? d : 0;
                var count = Values.Count(current);
                if (position < 0 || position >= count)
                {
                    return dflt;
                }

                current = Values.Item(current, (int)position);
            }
            else
            {
                return dflt;
            }

            if (current.IsNull)
            {
                return dflt;
            }
        }

        return Values.Materialize(env.Context, current);
    }
}

/// <summary><c>if</c>: (condition, then) pairs, optional else; a then/else yielding a one-key object is re-applied by Go (unsupported here).</summary>
internal sealed class IfNode(Node[] clauses) : Node
{
    public override Value Eval(in Env env)
    {
        var n = clauses.Length;
        for (var i = 0; i < n - 1; i += 2)
        {
            if (Values.IsTruthy(clauses[i].Eval(env)))
            {
                return Clause(clauses[i + 1], env);
            }
        }

        return n % 2 == 1 ? Clause(clauses[n - 1], env) : Value.Null;
    }

    private static Value Clause(Node node, in Env env)
    {
        var result = node.Eval(env);
        if (result.Kind == Kind.Object && Values.Count(result) == 1)
        {
            throw new JsonLogicException("if branch evaluated to a one-key object, which flagd re-applies as a rule");
        }

        return result;
    }
}

/// <summary><c>and</c> / <c>or</c>: short-circuit, returning the deciding value (or the last; null when empty).</summary>
internal sealed class AndOrNode(Node[] items, bool isAnd) : Node
{
    public override Value Eval(in Env env)
    {
        var last = Value.Null;
        foreach (var item in items)
        {
            last = item.Eval(env);
            if (Values.IsTruthy(last) != isAnd)
            {
                return last;
            }
        }

        return last;
    }
}

internal enum IterationOp
{
    Map,
    Filter,
    All,
    None,
    Some,
    Reduce,
}

/// <summary><c>map</c>, <c>filter</c>, <c>all</c>, <c>none</c>, <c>some</c>, <c>reduce</c>. Allocate (arrays, reduce scope).</summary>
internal sealed class IterationNode(IterationOp op, Node? subject, Node logic, Node? initial) : Node
{
    public override Value Eval(in Env env)
    {
        Value acc = default;
        var accKind = Kind.Null;
        if (op == IterationOp.Reduce)
        {
            acc = initial!.Eval(env);
            accKind = acc.Kind is Kind.Bool or Kind.Number or Kind.String
                ? acc.Kind
                : throw new JsonLogicException("reduce initial value must be a bool, number or string");
        }

        var items = subject?.Eval(env) ?? Value.Null;
        switch (op)
        {
            case IterationOp.Map or IterationOp.Filter when items.IsNull:
                return Value.EmptyArray;
            case IterationOp.Reduce when items.IsNull:
                return Value.Of(0);
            case IterationOp.All or IterationOp.Some when !Values.IsTruthy(items):
                return Value.False;
            case IterationOp.None when !Values.IsTruthy(items):
                return Value.True;
        }

        if (items.Kind != Kind.Array)
        {
            throw new JsonLogicException($"{op} subject is not an array");
        }

        var count = Values.Count(items);
        var outerFirst = op is IterationOp.Filter or IterationOp.None or IterationOp.Some;
        var results = op is IterationOp.Map or IterationOp.Filter ? new List<Value>(count) : null;
        Dictionary<string, Value>? scope = null;
        for (var i = 0; i < count; i++)
        {
            var element = Values.Item(items, i);
            if (op == IterationOp.Reduce)
            {
                if (element.IsNull)
                {
                    continue;
                }

                scope ??= new Dictionary<string, Value>(StringComparer.Ordinal)
                {
                    ["valueType"] = Value.Of(accKind == Kind.Bool ? "bool" : accKind == Kind.Number ? "number" : "string"),
                };
                scope["current"] = element;
                scope["accumulator"] = acc;
                var next = logic.Eval(new Env(env.Context, Value.ObjectOf(scope)));
                acc = accKind switch
                {
                    Kind.Bool => Value.Of(Values.IsTruthy(next)),
                    Kind.Number => Value.Of(Values.GoToNumber(next)),
                    _ => Value.Of(Values.GoToString(next)),
                };
                continue;
            }

            var v = logic.Eval(new Env(env.Context, element, env.Data, outerFirst));
            switch (op)
            {
                case IterationOp.Map:
                    results!.Add(v);
                    break;
                case IterationOp.Filter when Values.IsTruthy(v):
                    results!.Add(element);
                    break;
                case IterationOp.All when !Values.IsTruthy(v):
                    return Value.False;
                case IterationOp.None when Values.IsTruthy(v):
                    return Value.False;
                case IterationOp.Some when Values.IsTruthy(v):
                    return Value.True;
            }
        }

        return op switch
        {
            IterationOp.Map or IterationOp.Filter => Value.ArrayOf(results!.ToArray()),
            IterationOp.All or IterationOp.None => Value.True,
            IterationOp.Some => Value.False,
            _ => acc,
        };
    }
}

internal enum Op
{
    Eq,
    Neq,
    StrictEq,
    StrictNeq,
    Lt,
    Le,
    Gt,
    Ge,
    Not,
    NotNot,
    In,
    Cat,
    Substr,
    Plus,
    Minus,
    Mul,
    Div,
    Mod,
    Max,
    Min,
    Merge,
    Missing,
    MissingSome,
    StartsWith,
    EndsWith,
    SemVer,
}

/// <summary>Every operator whose arguments are evaluated up front (Go <c>parseValues</c>), dispatched on <see cref="Op"/>.</summary>
internal sealed class OpNode(Op op, Args args) : Node
{
    public override Value Eval(in Env env)
    {
        var isArray = args.Resolve(env, out var a);
        switch (op)
        {
            case Op.Eq or Op.Neq:
                Require(isArray);
                return Value.Of((a.Count >= 2 && Values.LooseEquals(a.Get(0, env), a.Get(1, env))) == (op == Op.Eq));
            case Op.StrictEq or Op.StrictNeq:
                return Value.Of((isArray && a.Count >= 2 && Values.StrictEquals(a.Get(0, env), a.Get(1, env))) == (op == Op.StrictEq));
            case Op.Lt or Op.Le or Op.Gt or Op.Ge:
                Require(isArray);
                return Value.Of(a.Count >= 2 && Compare(a, env));
            case Op.Not or Op.NotNot:
                var negated = isArray
                    ? a.Count == 0 || !Values.IsTruthy(a.Get(0, env))
                    : !Values.IsTruthy(a.Scalar);
                return Value.Of(negated == (op == Op.Not));
            case Op.In:
                Require(isArray && a.Count > 0);
                return Value.Of(In(a.Get(0, env), a.Count > 1 ? a.Get(1, env) : Value.Null));
            case Op.Cat:
                return Cat(isArray, a, env);
            case Op.Substr:
                Require(isArray && a.Count >= 2);
                return Value.Of(Substr(a, env));
            case Op.Plus or Op.Minus or Op.Mul or Op.Div or Op.Max or Op.Min or Op.Mod:
                return Arithmetic(isArray, a, env);
            case Op.Merge:
                return Merge(isArray, a, env);
            case Op.Missing or Op.MissingSome:
                return Missing(isArray, a, env);
            case Op.StartsWith or Op.EndsWith:
                if (!isArray || a.Count != 2)
                {
                    return Value.Null;
                }

                var subject = a.Get(0, env);
                var affix = a.Get(1, env);
                if (subject.Kind != Kind.String || affix.Kind != Kind.String)
                {
                    return Value.Null;
                }

                return Value.Of(op == Op.StartsWith
                    ? subject.AsString.StartsWith(affix.AsString, StringComparison.Ordinal)
                    : subject.AsString.EndsWith(affix.AsString, StringComparison.Ordinal));
            case Op.SemVer:
                return isArray && a.Count == 3 ? SemVer.Evaluate(a.Get(0, env), a.Get(1, env), a.Get(2, env)) : Value.Null;
            default:
                throw new InvalidOperationException(op.ToString());
        }
    }

    private static void Require(bool condition)
    {
        if (!condition)
        {
            throw new JsonLogicException("operator arguments are not an array of the required length");
        }
    }

    private bool Compare(ArgList a, in Env env)
    {
        var x = a.Get(0, env);
        var y = a.Get(1, env);
        var three = a.Count == 3;
        var z = three ? a.Get(2, env) : Value.Null;
        return op switch
        {
            Op.Lt => three ? Values.Less(x, y) && Values.Less(y, z) : Values.Less(x, y),
            Op.Le => three ? LessOrEqual(x, y) && LessOrEqual(y, z) : LessOrEqual(x, y),
            Op.Gt => three ? Values.Less(z, y) && Values.Less(y, x) : Values.Less(y, x),
            _ => three ? LessOrEqual(z, y) && LessOrEqual(y, x) : LessOrEqual(y, x),
        };
    }

    private static bool LessOrEqual(Value x, Value y) => Values.Less(x, y) || Values.LooseEquals(x, y);

    private static bool In(Value needle, Value haystack)
    {
        if (haystack.Kind == Kind.String)
        {
            return needle.Kind == Kind.String
                ? haystack.AsString.Contains(needle.AsString, StringComparison.Ordinal)
                : throw new JsonLogicException("in: the needle of a string search must be a string");
        }

        if (haystack.Kind != Kind.Array)
        {
            return false;
        }

        var count = Values.Count(haystack);
        for (var i = 0; i < count; i++)
        {
            var element = Values.Item(haystack, i);
            if (element.Kind == Kind.Array)
            {
                if (Values.Count(element) < 2)
                {
                    throw new JsonLogicException("in: range element needs two bounds");
                }

                var x = Values.GoToNumber(needle);
                if (x >= Values.GoToNumber(Values.Item(element, 0)) && Values.GoToNumber(Values.Item(element, 1)) >= x)
                {
                    return true;
                }
            }
            else if (needle.Kind == Kind.Number)
            {
                if (Values.GoToNumber(element) == needle.Number)
                {
                    return true;
                }
            }
            else if (element.Kind == needle.Kind && element.Kind switch
                     {
                         Kind.Null => true,
                         Kind.Bool => element.AsBool == needle.AsBool,
                         Kind.String => element.AsString == needle.AsString,
                         _ => false,
                     })
            {
                return true;
            }
        }

        return false;
    }

    private static Value Cat(bool isArray, ArgList a, in Env env)
    {
        if (!isArray)
        {
            return a.Scalar.Kind == Kind.String ? a.Scalar : throw new JsonLogicException("cat arguments are not an array");
        }

        switch (a.Count)
        {
            case 0:
                return Value.Of(string.Empty);
            case 1:
                return Value.Of(Values.GoToString(a.Get(0, env)));
        }

        var sb = new System.Text.StringBuilder();
        for (var i = 0; i < a.Count; i++)
        {
            sb.Append(Values.GoToString(a.Get(i, env)));
        }

        return Value.Of(sb.ToString().Trim());
    }

    private static string Substr(ArgList a, in Env env)
    {
        var runes = Values.GoToString(a.Get(0, env)).EnumerateRunes().ToArray();
        var from = (int)Values.GoToNumber(a.Get(1, env));
        var length = runes.Length;
        if (from < 0)
        {
            from = length + from;
        }

        if (from < 0 || from > length)
        {
            return string.Concat(runes.Select(r => r.ToString()));
        }

        if (a.Count == 3)
        {
            length = (int)Values.GoToNumber(a.Get(2, env));
        }

        var to = length < 0 ? runes.Length + length : from + length;
        to = Math.Min(to, runes.Length);
        return to <= from ? string.Empty : string.Concat(runes[from..to].Select(r => r.ToString()));
    }

    private Value Arithmetic(bool isArray, ArgList a, in Env env)
    {
        if (!isArray)
        {
            return op == Op.Plus ? Value.Of(Values.GoToNumber(a.Scalar)) : throw new JsonLogicException($"{op} arguments are not an array");
        }

        var n = a.Count;
        switch (op)
        {
            case Op.Plus:
                var sum = 0d;
                for (var i = 0; i < n; i++)
                {
                    sum += Values.GoToNumber(a.Get(i, env));
                }

                return Value.Of(sum);
            case Op.Mul:
                var product = 1d;
                for (var i = 0; i < n; i++)
                {
                    product *= Values.GoToNumber(a.Get(i, env));
                }

                return Value.Of(product);
            case Op.Mod:
                Require(n >= 2);
                return Value.Of(Values.GoToNumber(a.Get(0, env)) % Values.GoToNumber(a.Get(1, env)));
        }

        if (n == 0)
        {
            return op is Op.Max or Op.Min ? Value.Null : Value.Of(0);
        }

        var result = Values.GoToNumber(a.Get(0, env));
        if (op == Op.Minus && n == 1)
        {
            return Value.Of(-result);
        }

        for (var i = 1; i < n; i++)
        {
            var x = Values.GoToNumber(a.Get(i, env));
            result = op switch
            {
                Op.Minus => result - x,
                Op.Div => result / x,
                Op.Max => x > result ? x : result,
                _ => result > x ? x : result,
            };
        }

        return Value.Of(result);
    }

    private static Value Merge(bool isArray, ArgList a, in Env env)
    {
        if (!isArray)
        {
            return a.Scalar.Kind is Kind.Bool or Kind.Number or Kind.String
                ? Value.ArrayOf(new[] { a.Scalar })
                : throw new JsonLogicException("merge arguments are not an array");
        }

        var result = new List<Value>();
        for (var i = 0; i < a.Count; i++)
        {
            var item = a.Get(i, env);
            if (item.Kind == Kind.Array)
            {
                for (var j = 0; j < Values.Count(item); j++)
                {
                    result.Add(Values.Item(item, j));
                }
            }
            else
            {
                result.Add(item);
            }
        }

        return Value.ArrayOf(result.ToArray());
    }

    private Value Missing(bool isArray, ArgList a, in Env env)
    {
        Value specs;
        var needed = 0;
        if (op == Op.MissingSome)
        {
            Require(isArray && a.Count >= 2);
            needed = (int)Values.GoToNumber(a.Get(0, env));
            specs = a.Get(1, env);
            Require(specs.Kind == Kind.Array);
        }
        else if (!isArray)
        {
            Require(a.Scalar.Kind == Kind.String);
            specs = Value.ArrayOf(new[] { a.Scalar });
        }
        else
        {
            specs = a.Count == 0 ? Value.EmptyArray : default;
        }

        List<Value>? missing = null;
        var found = 0;
        var count = specs.Kind == Kind.Array ? Values.Count(specs) : a.Count;
        for (var i = 0; i < count; i++)
        {
            var spec = specs.Kind == Kind.Array ? Values.Item(specs, i) : a.Get(i, env);
            if (VarNode.GetVar(env, spec).IsNull)
            {
                (missing ??= []).Add(spec);
            }
            else
            {
                found++;
            }
        }

        if (op == Op.MissingSome && needed <= found)
        {
            return Value.EmptyArray;
        }

        return missing is null ? Value.EmptyArray : Value.ArrayOf(missing.ToArray());
    }
}
