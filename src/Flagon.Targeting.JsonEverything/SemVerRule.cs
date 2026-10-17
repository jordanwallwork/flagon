using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Json.Logic;

namespace Flagon.Targeting.JsonEverything;

/// <summary>
/// flagd's <c>sem_ver</c>: <c>[version, operator, version]</c>, compared with Go's <c>golang.org/x/mod/semver</c>
/// (an optional <c>v</c>/<c>V</c> prefix, <c>MAJOR</c> and <c>MAJOR.MINOR</c> shorthands, build metadata ignored).
/// <c>^</c> means same major and <c>~</c> same major.minor. Any invalid input yields null.
/// </summary>
internal sealed class SemVerRule : IRule
{
    public JsonNode? Apply(JsonNode? args, Json.Logic.EvaluationContext context)
    {
        if (FlagdOperators.EvaluateArguments(args, context) is not { Count: 3 } values
            || Normalize(values[0]) is not { } actual
            || FlagdOperators.AsString(values[1]) is not { } op
            || Normalize(values[2]) is not { } target)
        {
            return null;
        }

        return op switch
        {
            "=" => GoSemVer.Compare(actual, target) == 0,
            "!=" => GoSemVer.Compare(actual, target) != 0,
            "<" => GoSemVer.Compare(actual, target) < 0,
            "<=" => GoSemVer.Compare(actual, target) <= 0,
            ">" => GoSemVer.Compare(actual, target) > 0,
            ">=" => GoSemVer.Compare(actual, target) >= 0,
            "^" => GoSemVer.Compare(GoSemVer.Major(actual), GoSemVer.Major(target)) == 0,
            "~" => GoSemVer.Compare(GoSemVer.MajorMinor(actual), GoSemVer.MajorMinor(target)) == 0,
            _ => null,
        };
    }

    // flagd's normalizeVersion: fmt "%v" the value, prefix "v" (normalising "V"), and require a valid semver.
    private static string? Normalize(JsonNode? node)
    {
        var text = node switch
        {
            null => "<nil>",
            JsonValue v when v.GetValueKind() == JsonValueKind.String => v.GetValue<string>(),
            JsonValue v when v.GetValueKind() is JsonValueKind.True or JsonValueKind.False => v.GetValueKind() == JsonValueKind.True ? "true" : "false",
            JsonValue v when JsonNumbers.TryGetDouble(v, out var d) => FormatLikeGoPercentV(d),
            _ => null,
        };
        if (text is null)
        {
            return null;
        }

        if (!text.StartsWith('v'))
        {
            text = "v" + (text.StartsWith('V') ? text[1..] : text);
        }

        return GoSemVer.IsValid(text) ? text : null;
    }

    // Go's %v of a float64 is %g with the shortest precision: exponent form when the exponent is < -4 or >= 21.
    private static string? FormatLikeGoPercentV(double d)
    {
        var abs = Math.Abs(d);
        if (abs != 0 && (abs < 1e-4 || abs >= 1e21))
        {
            return null; // exponent form, never a valid version
        }

        return d.ToString("R", CultureInfo.InvariantCulture);
    }
}

/// <summary>A port of <c>golang.org/x/mod/semver</c> (v0.41.0): <c>IsValid</c>, <c>Compare</c>, <c>Major</c>, <c>MajorMinor</c>.</summary>
internal static class GoSemVer
{
    private readonly record struct Parsed(string Major, string Minor, string Patch, string Prerelease);

    public static bool IsValid(string v) => TryParse(v, out _);

    public static string Major(string v) => TryParse(v, out var p) ? v[..(1 + p.Major.Length)] : string.Empty;

    public static string MajorMinor(string v)
    {
        if (!TryParse(v, out var p))
        {
            return string.Empty;
        }

        var i = 1 + p.Major.Length;
        var j = i + 1 + p.Minor.Length;
        if (j <= v.Length && v[i] == '.' && v[(i + 1)..j] == p.Minor)
        {
            return v[..j];
        }

        return v[..i] + "." + p.Minor;
    }

    public static int Compare(string v, string w)
    {
        var okV = TryParse(v, out var pv);
        var okW = TryParse(w, out var pw);
        if (!okV && !okW) return 0;
        if (!okV) return -1;
        if (!okW) return 1;

        var c = CompareInt(pv.Major, pw.Major);
        if (c != 0) return c;
        c = CompareInt(pv.Minor, pw.Minor);
        if (c != 0) return c;
        c = CompareInt(pv.Patch, pw.Patch);
        if (c != 0) return c;
        return ComparePrerelease(pv.Prerelease, pw.Prerelease);
    }

    private static bool TryParse(string v, out Parsed parsed)
    {
        parsed = default;
        if (v.Length == 0 || v[0] != 'v') return false;
        if (!ParseInt(v[1..], out var major, out v)) return false;
        if (v.Length == 0)
        {
            parsed = new Parsed(major, "0", "0", string.Empty);
            return true;
        }

        if (v[0] != '.' || !ParseInt(v[1..], out var minor, out v)) return false;
        if (v.Length == 0)
        {
            parsed = new Parsed(major, minor, "0", string.Empty);
            return true;
        }

        if (v[0] != '.' || !ParseInt(v[1..], out var patch, out v)) return false;
        var prerelease = string.Empty;
        if (v.Length > 0 && v[0] == '-' && !ParsePrerelease(v, out prerelease, out v)) return false;
        if (v.Length > 0 && v[0] == '+' && !ParseBuild(v, out v)) return false;
        if (v.Length != 0) return false;

        parsed = new Parsed(major, minor, patch, prerelease);
        return true;
    }

    private static bool ParseInt(string v, out string number, out string rest)
    {
        number = rest = string.Empty;
        if (v.Length == 0 || !char.IsAsciiDigit(v[0])) return false;
        var i = 1;
        while (i < v.Length && char.IsAsciiDigit(v[i])) i++;
        if (v[0] == '0' && i != 1) return false;
        (number, rest) = (v[..i], v[i..]);
        return true;
    }

    private static bool ParsePrerelease(string v, out string prerelease, out string rest)
    {
        prerelease = rest = string.Empty;
        int i = 1, start = 1;
        while (i < v.Length && v[i] != '+')
        {
            if (!IsIdentChar(v[i]) && v[i] != '.') return false;
            if (v[i] == '.')
            {
                if (start == i || IsBadNum(v[start..i])) return false;
                start = i + 1;
            }

            i++;
        }

        if (start == i || IsBadNum(v[start..i])) return false;
        (prerelease, rest) = (v[..i], v[i..]);
        return true;
    }

    private static bool ParseBuild(string v, out string rest)
    {
        rest = string.Empty;
        int i = 1, start = 1;
        while (i < v.Length)
        {
            if (!IsIdentChar(v[i]) && v[i] != '.') return false;
            if (v[i] == '.')
            {
                if (start == i) return false;
                start = i + 1;
            }

            i++;
        }

        if (start == i) return false;
        rest = v[i..];
        return true;
    }

    private static bool IsIdentChar(char c) => char.IsAsciiLetterOrDigit(c) || c == '-';

    private static bool IsBadNum(string v)
    {
        var i = 0;
        while (i < v.Length && char.IsAsciiDigit(v[i])) i++;
        return i == v.Length && i > 1 && v[0] == '0';
    }

    private static bool IsNum(string v) => v.Length > 0 && v.All(char.IsAsciiDigit);

    private static int CompareInt(string x, string y)
    {
        if (x == y) return 0;
        if (x.Length != y.Length) return x.Length < y.Length ? -1 : 1;
        return string.CompareOrdinal(x, y) < 0 ? -1 : 1;
    }

    private static int ComparePrerelease(string x, string y)
    {
        if (x == y) return 0;
        if (x.Length == 0) return 1;
        if (y.Length == 0) return -1;
        while (x.Length != 0 && y.Length != 0)
        {
            x = x[1..];
            y = y[1..];
            string dx, dy;
            (dx, x) = NextIdent(x);
            (dy, y) = NextIdent(y);
            if (dx != dy)
            {
                var ix = IsNum(dx);
                var iy = IsNum(dy);
                if (ix != iy) return ix ? -1 : 1;
                if (ix && dx.Length != dy.Length) return dx.Length < dy.Length ? -1 : 1;
                return string.CompareOrdinal(dx, dy) < 0 ? -1 : 1;
            }
        }

        return x.Length == 0 ? -1 : 1;
    }

    private static (string Ident, string Remainder) NextIdent(string x)
    {
        var i = x.IndexOf('.', StringComparison.Ordinal);
        return i < 0 ? (x, string.Empty) : (x[..i], x[i..]);
    }
}
