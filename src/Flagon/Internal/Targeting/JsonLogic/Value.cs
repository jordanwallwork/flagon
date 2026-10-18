using System.Collections;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Flagon.Internal.Targeting.JsonLogic;

internal enum Kind : byte
{
    Null,
    Bool,
    Number,
    String,
    Array,
    Object,
}

/// <summary>
/// A JsonLogic value as flagd's Go engine sees it after its JSON round trip: null, bool, IEEE double, string, array or
/// object. A 24-byte struct, so primitives never box. Arrays and objects reference their source without copying:
/// <see cref="T:Value[]"/>, a CLR list, a boxed <see cref="JsonElement"/>; a CLR or internal dictionary, a boxed
/// <see cref="JsonElement"/>, or one of the context markers (<see cref="Root"/>, <see cref="Flagd"/>).
/// </summary>
internal readonly struct Value
{
    private Value(Kind kind, double number, object? reference)
    {
        Kind = kind;
        Number = number;
        Ref = reference;
    }

    public Kind Kind { get; }

    /// <summary>The number, or 1/0 for a bool.</summary>
    public double Number { get; }

    public object? Ref { get; }

    public static Value Null => default;

    public static Value True { get; } = new(Kind.Bool, 1, null);

    public static Value False { get; } = new(Kind.Bool, 0, null);

    public static Value EmptyArray { get; } = new(Kind.Array, 0, Array.Empty<Value>());

    /// <summary>The whole evaluation data object (attributes, <c>targetingKey</c>, <c>$flagd</c>), resolved against the environment.</summary>
    public static Value Root { get; } = new(Kind.Object, 0, RootMarker.Instance);

    /// <summary>The <c>$flagd</c> object (<c>flagKey</c>, <c>timestamp</c>), resolved against the environment.</summary>
    public static Value Flagd { get; } = new(Kind.Object, 0, FlagdMarker.Instance);

    public bool IsNull => Kind == Kind.Null;

    public bool AsBool => Number != 0;

    public string AsString => (string)Ref!;

    public bool IsMarker => Ref is RootMarker or FlagdMarker;

    public static Value Of(bool b) => b ? True : False;

    public static Value Of(double d) => new(Kind.Number, d, null);

    public static Value Of(string s) => new(Kind.String, 0, s);

    public static Value ArrayOf(object items) => new(Kind.Array, 0, items);

    public static Value ObjectOf(object members) => new(Kind.Object, 0, members);

    public override string ToString() => Values.ToJson(this);

    internal sealed class RootMarker
    {
        public static readonly RootMarker Instance = new();
    }

    internal sealed class FlagdMarker
    {
        public static readonly FlagdMarker Instance = new();
    }
}

/// <summary>A Go-engine panic: the rule cannot be applied to this data (flagd reports PARSE_ERROR).</summary>
internal sealed class JsonLogicException(string message) : Exception(message);

/// <summary>Conversions, coercions and comparisons with diegoholiveira/jsonlogic v3.9.1 semantics.</summary>
internal static class Values
{
    // --- conversion in ---

    /// <summary>A context attribute as flagd's JSON round trip would deliver it (all numbers become doubles).</summary>
    public static Value FromClr(object? o) => o switch
    {
        null => Value.Null,
        string s => Value.Of(s),
        bool b => Value.Of(b),
        double d => Value.Of(d),
        long l => Value.Of(l),
        int i => Value.Of(i),
        float f => Value.Of(f),
        decimal m => Value.Of((double)m),
        short s => Value.Of(s),
        byte b => Value.Of(b),
        uint u => Value.Of(u),
        ulong u => Value.Of(u),
        sbyte s => Value.Of(s),
        ushort u => Value.Of(u),
        JsonElement e => FromJson(e),
        DateTimeOffset t => Value.Of(t.ToString("o", CultureInfo.InvariantCulture)),
        DateTime t => Value.Of(t.ToString("o", CultureInfo.InvariantCulture)),
        IReadOnlyDictionary<string, object?> or IDictionary<string, object?> or IDictionary => Value.ObjectOf(o),
        IList list => Value.ArrayOf(list),
        IEnumerable items => Value.ArrayOf(items.Cast<object?>().Select(FromClr).ToArray()),
        _ => Value.Of(Convert.ToString(o, CultureInfo.InvariantCulture) ?? string.Empty),
    };

    public static Value FromJson(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.String => Value.Of(e.GetString()!),
        JsonValueKind.Number => Value.Of(e.GetDouble()),
        JsonValueKind.True => Value.True,
        JsonValueKind.False => Value.False,
        JsonValueKind.Object => Value.ObjectOf(e),
        JsonValueKind.Array => Value.ArrayOf(e),
        _ => Value.Null,
    };

    /// <summary>A rule literal, deep-copied into internal arrays/objects (so it needs no <see cref="JsonDocument"/>).</summary>
    public static Value Constant(JsonElement e)
    {
        switch (e.ValueKind)
        {
            case JsonValueKind.Object:
                var members = new Dictionary<string, Value>(StringComparer.Ordinal);
                foreach (var p in e.EnumerateObject())
                {
                    members[p.Name] = Constant(p.Value);
                }

                return Value.ObjectOf(members);
            case JsonValueKind.Array:
                return Value.ArrayOf(e.EnumerateArray().Select(Constant).ToArray());
            default:
                return FromJson(e);
        }
    }

    // --- arrays and objects ---

    public static int Count(Value v) => v.Ref switch
    {
        Value[] a => a.Length,
        IList l => l.Count,
        JsonElement { ValueKind: JsonValueKind.Array } e => e.GetArrayLength(),
        JsonElement e => e.EnumerateObject().Count(),
        Dictionary<string, Value> d => d.Count,
        IReadOnlyCollection<KeyValuePair<string, object?>> c => c.Count,
        ICollection c => c.Count,
        string s => s.Length,
        _ => 0,
    };

    public static Value Item(Value array, int index) => array.Ref switch
    {
        Value[] a => a[index],
        IList l => FromClr(l[index]),
        JsonElement e => FromJson(e[index]),
        _ => throw new JsonLogicException("not an array"),
    };

    /// <summary>An object member; false when absent. Context markers are resolved against <paramref name="context"/>.</summary>
    public static bool TryGetMember(in TargetingContext context, Value obj, string key, out Value value)
    {
        object? raw;
        switch (obj.Ref)
        {
            case Value.RootMarker:
                if (key == "$flagd")
                {
                    value = Value.Flagd;
                    return true;
                }

                if (context.TryGetValue(key, out raw))
                {
                    value = FromClr(raw);
                    return true;
                }

                break;
            case Value.FlagdMarker:
                if (key == "flagKey")
                {
                    value = Value.Of(context.FlagKey);
                    return true;
                }

                if (key == "timestamp")
                {
                    value = Value.Of(context.Timestamp);
                    return true;
                }

                break;
            case Dictionary<string, Value> d:
                return d.TryGetValue(key, out value);
            case IReadOnlyDictionary<string, object?> d when d.TryGetValue(key, out raw):
                value = FromClr(raw);
                return true;
            case IDictionary<string, object?> d when d.TryGetValue(key, out raw):
                value = FromClr(raw);
                return true;
            case IDictionary d when d.Contains(key):
                value = FromClr(d[key]);
                return true;
            case JsonElement e when e.TryGetProperty(key, out var p):
                value = FromJson(p);
                return true;
        }

        value = Value.Null;
        return false;
    }

    /// <summary>Replaces a context marker with a real object (allocates; only for whole-object results).</summary>
    public static Value Materialize(in TargetingContext context, Value v)
    {
        if (v.Ref is Value.FlagdMarker)
        {
            return Value.ObjectOf(new Dictionary<string, Value>(StringComparer.Ordinal)
            {
                ["flagKey"] = Value.Of(context.FlagKey),
                ["timestamp"] = Value.Of(context.Timestamp),
            });
        }

        if (v.Ref is not Value.RootMarker)
        {
            return v;
        }

        var members = new Dictionary<string, Value>(StringComparer.Ordinal);
        foreach (var (k, raw) in context.Attributes)
        {
            members[k] = FromClr(raw);
        }

        if (context.TargetingKey is { } tk)
        {
            members[TargetingContext.TargetingKeyName] = Value.Of(tk);
        }

        members["$flagd"] = Materialize(context, Value.Flagd);
        return Value.ObjectOf(members);
    }

    public static IEnumerable<KeyValuePair<string, Value>> Members(Value obj) => obj.Ref switch
    {
        Dictionary<string, Value> d => d,
        IReadOnlyDictionary<string, object?> d => d.Select(kv => KeyValuePair.Create(kv.Key, FromClr(kv.Value))),
        IDictionary<string, object?> d => d.Select(kv => KeyValuePair.Create(kv.Key, FromClr(kv.Value))),
        IDictionary d => d.Cast<DictionaryEntry>().Select(kv => KeyValuePair.Create(Convert.ToString(kv.Key, CultureInfo.InvariantCulture)!, FromClr(kv.Value))),
        // Go decodes duplicate keys last-wins.
        JsonElement e => e.EnumerateObject().GroupBy(p => p.Name).Select(g => KeyValuePair.Create(g.Key, FromJson(g.Last().Value))),
        _ => [],
    };

    // --- coercions (internal/typing and internal/javascript) ---

    /// <summary>typing.IsTrue: non-zero numbers, non-empty strings/arrays/objects.</summary>
    public static bool IsTruthy(Value v) => v.Kind switch
    {
        Kind.Bool => v.AsBool,
        Kind.Number => v.Number != 0,
        Kind.String => v.AsString.Length > 0,
        Kind.Array or Kind.Object => v.IsMarker || Count(v) > 0,
        _ => false,
    };

    /// <summary>javascript.ToNumber: null 0, bool 0/1, strings parsed (blank 0, invalid NaN), arrays/objects NaN.</summary>
    public static double JsToNumber(Value v) => v.Kind switch
    {
        Kind.Null => 0,
        Kind.Bool or Kind.Number => v.Number,
        Kind.String => string.IsNullOrWhiteSpace(v.AsString) ? 0 : TryParseGoFloat(v.AsString, out var d) ? d : double.NaN,
        _ => double.NaN,
    };

    /// <summary>typing.ToNumber: numbers, strings parsed (invalid 0); anything else panics.</summary>
    public static double GoToNumber(Value v) => v.Kind switch
    {
        Kind.Number => v.Number,
        Kind.String => TryParseGoFloat(v.AsString, out var d) ? d : 0,
        _ => throw new JsonLogicException($"cannot convert {v} to a number"),
    };

    /// <summary>typing.ToString: numbers formatted ('f', -1), null empty, strings as is; anything else panics.</summary>
    public static string GoToString(Value v) => v.Kind switch
    {
        Kind.Number => FormatPlain(v.Number),
        Kind.Null => string.Empty,
        Kind.String => v.AsString,
        _ => throw new JsonLogicException($"cannot convert {v} to a string"),
    };

    // strconv.ParseFloat: no surrounding whitespace; out-of-range values are +-Inf.
    public static bool TryParseGoFloat(string s, out double d)
    {
        d = 0;
        if (s.Length == 0 || char.IsWhiteSpace(s[0]) || char.IsWhiteSpace(s[^1]))
        {
            return false;
        }

        return double.TryParse(s, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint | NumberStyles.AllowExponent,
                   CultureInfo.InvariantCulture, out d)
               || TryParseSpecial(s, out d);
    }

    private static bool TryParseSpecial(string s, out double d)
    {
        var body = s.TrimStart('+', '-');
        var negative = s.StartsWith('-');
        d = body.ToLowerInvariant() switch
        {
            "inf" or "infinity" => negative ? double.NegativeInfinity : double.PositiveInfinity,
            "nan" => double.NaN,
            _ => 0,
        };
        return body.ToLowerInvariant() is "inf" or "infinity" or "nan";
    }

    // --- comparisons (comp.go) ---

    public static bool LooseEquals(Value a, Value b)
    {
        if (a.IsNull || b.IsNull)
        {
            return a.IsNull && b.IsNull;
        }

        if (a.Kind == Kind.String && b.Kind == Kind.String)
        {
            return string.Equals(a.AsString, b.AsString, StringComparison.Ordinal);
        }

        return JsToNumber(a) == JsToNumber(b);
    }

    public static bool StrictEquals(Value a, Value b)
    {
        if (a.IsNull || b.IsNull)
        {
            return a.IsNull && b.IsNull;
        }

        return a.Kind == b.Kind && LooseEquals(a, b);
    }

    public static bool Less(Value a, Value b) =>
        a.Kind == Kind.String && b.Kind == Kind.String
            ? CompareCodePoints(a.AsString, b.AsString) < 0
            : JsToNumber(b) > JsToNumber(a);

    // Go compares strings bytewise (UTF-8), i.e. by code point; UTF-16 ordinal differs only around surrogates.
    public static int CompareCodePoints(string a, string b)
    {
        var n = Math.Min(a.Length, b.Length);
        for (var i = 0; i < n; i++)
        {
            char x = a[i], y = b[i];
            if (x == y)
            {
                continue;
            }

            if (char.IsSurrogate(x) != char.IsSurrogate(y) && (x >= 0xE000 || y >= 0xE000))
            {
                return char.IsSurrogate(x) ? 1 : -1;
            }

            return x < y ? -1 : 1;
        }

        return a.Length.CompareTo(b.Length);
    }

    // --- formatting ---

    /// <summary>strconv.FormatFloat(d, 'f', -1, 64): shortest round-trip digits, never an exponent.</summary>
    public static string FormatPlain(double d)
    {
        if (double.IsNaN(d))
        {
            return "NaN";
        }

        if (double.IsInfinity(d))
        {
            return d > 0 ? "+Inf" : "-Inf";
        }

        if (d == Math.Truncate(d) && Math.Abs(d) < 1e15)
        {
            return d == 0 && double.IsNegative(d) ? "-0" : ((long)d).ToString(CultureInfo.InvariantCulture);
        }

        var (negative, digits, exponent) = Decompose(d);
        var sb = new StringBuilder(negative ? "-" : string.Empty);
        // value = 0.digits * 10^exponent
        if (exponent <= 0)
        {
            sb.Append("0.").Append('0', -exponent).Append(digits);
        }
        else if (exponent >= digits.Length)
        {
            sb.Append(digits).Append('0', exponent - digits.Length);
        }
        else
        {
            sb.Append(digits, 0, exponent).Append('.').Append(digits, exponent, digits.Length - exponent);
        }

        return sb.ToString();
    }

    /// <summary>Go encoding/json's float format: plain, or 'e' notation below 1e-6 / from 1e21 (e.g. <c>1e+21</c>, <c>1e-7</c>).</summary>
    /// <summary>
    /// Go's <c>fmt</c> <c>%v</c> for a float64 (<c>%g</c>, shortest digits): an exponent when the decimal exponent is
    /// below -4 or at least 6 (so 1e6 is <c>1e+06</c>), at least two exponent digits; <c>+Inf</c>, <c>-Inf</c>, <c>NaN</c>.
    /// </summary>
    public static string FormatGo(double d)
    {
        if (!double.IsFinite(d) || d == 0)
        {
            return FormatPlain(d);
        }

        var (negative, digits, exponent) = Decompose(d);
        var e = exponent - 1;
        if (e >= -4 && e < 6)
        {
            return FormatPlain(d);
        }

        var mantissa = digits.Length == 1 ? digits : digits[..1] + "." + digits[1..];
        var exp = (e < 0 ? "-" : "+") + Math.Abs(e).ToString("00", CultureInfo.InvariantCulture);
        return (negative ? "-" : string.Empty) + mantissa + "e" + exp;
    }

    public static string FormatJson(double d)
    {
        var abs = Math.Abs(d);
        if (abs == 0 || (abs >= 1e-6 && abs < 1e21))
        {
            return FormatPlain(d);
        }

        var (negative, digits, exponent) = Decompose(d);
        var e = exponent - 1;
        var mantissa = digits.Length == 1 ? digits : digits[..1] + "." + digits[1..];
        var exp = e < 0 ? "-" + (-e).ToString(CultureInfo.InvariantCulture) : "+" + e.ToString("00", CultureInfo.InvariantCulture);
        return (negative ? "-" : string.Empty) + mantissa + "e" + exp;
    }

    // Shortest round-trip digits (no leading/trailing zeros) and the decimal exponent such that d = 0.digits * 10^exponent.
    private static (bool Negative, string Digits, int Exponent) Decompose(double d)
    {
        var shortest = Math.Abs(d).ToString("R", CultureInfo.InvariantCulture);
        var mant = shortest;
        var exp = 0;
        var ei = shortest.IndexOfAny(['E', 'e']);
        if (ei >= 0)
        {
            mant = shortest[..ei];
            exp = int.Parse(shortest[(ei + 1)..], CultureInfo.InvariantCulture);
        }

        var dot = mant.IndexOf('.');
        var intPart = dot >= 0 ? mant[..dot] : mant;
        var frac = dot >= 0 ? mant[(dot + 1)..] : string.Empty;
        var all = intPart + frac;
        var pointPos = intPart.Length + exp;
        var lead = all.Length - all.TrimStart('0').Length;
        all = all.TrimStart('0');
        pointPos -= lead;
        all = all.TrimEnd('0');
        if (all.Length == 0)
        {
            all = "0";
        }

        return (double.IsNegative(d), all, pointPos);
    }

    /// <summary>
    /// The JSON text Go's encoder writes for a value (sorted keys, HTML-escaped strings). Go's encoder fails on NaN and
    /// infinities: <paramref name="strict"/> throws for them, otherwise they are written as Go's <c>%v</c> spells them.
    /// </summary>
    public static string ToJson(Value v, bool strict = false)
    {
        var sb = new StringBuilder();
        WriteJson(sb, v, strict);
        return sb.ToString();
    }

    /// <summary>The error Go's JSON encoder reports for a NaN or infinite number.</summary>
    public static JsonLogicException UnsupportedJson(double d) => new($"json: unsupported value: {FormatPlain(d)}");

    private static void WriteJson(StringBuilder sb, Value v, bool strict)
    {
        switch (v.Kind)
        {
            case Kind.Null:
                sb.Append("null");
                break;
            case Kind.Bool:
                sb.Append(v.AsBool ? "true" : "false");
                break;
            case Kind.Number:
                sb.Append(double.IsFinite(v.Number) ? FormatJson(v.Number) : strict ? throw UnsupportedJson(v.Number) : FormatPlain(v.Number));
                break;
            case Kind.String:
                WriteJsonString(sb, v.AsString);
                break;
            case Kind.Array:
                sb.Append('[');
                for (var i = 0; i < Count(v); i++)
                {
                    if (i > 0)
                    {
                        sb.Append(',');
                    }

                    WriteJson(sb, Item(v, i), strict);
                }

                sb.Append(']');
                break;
            default:
                sb.Append('{');
                var first = true;
                foreach (var (k, item) in Members(v).OrderBy(m => m.Key, StringComparer.Ordinal))
                {
                    if (!first)
                    {
                        sb.Append(',');
                    }

                    first = false;
                    WriteJsonString(sb, k);
                    sb.Append(':');
                    WriteJson(sb, item, strict);
                }

                sb.Append('}');
                break;
        }
    }

    public static void WriteJsonString(StringBuilder sb, string s)
    {
        sb.Append('"');
        foreach (var c in s)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\b': sb.Append("\\b"); break;
                case '\f': sb.Append("\\f"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                case < ' ' or '<' or '>' or '&' or '\u2028' or '\u2029':
                    sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    break;
                default:
                    sb.Append(c);
                    break;
            }
        }

        sb.Append('"');
    }
}
