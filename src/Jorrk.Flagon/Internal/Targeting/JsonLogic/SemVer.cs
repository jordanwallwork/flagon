namespace Jorrk.Flagon.Internal.Targeting.JsonLogic;

/// <summary>
/// flagd <c>sem_ver</c> (<c>core/pkg/evaluator/semver.go</c>) over a port of <c>golang.org/x/mod/semver</c>: optional
/// <c>v</c>/<c>V</c>, <c>vMAJOR[.MINOR[.PATCH[-PRE][+BUILD]]]</c>, build ignored; <c>^</c> = same major, <c>~</c> = same
/// major.minor. Invalid versions, unknown operators and non-string operators yield null.
/// </summary>
internal static class SemVer
{
    public static Value Evaluate(Value actual, Value op, Value target)
    {
        if (!TryParse(Normalize(actual), out var a) || op.Kind != Kind.String || !TryParse(Normalize(target), out var b))
        {
            return Value.Null;
        }

        var c = Compare(a, b);
        return op.AsString switch
        {
            "=" => Value.Of(c == 0),
            "!=" => Value.Of(c != 0),
            "<" => Value.Of(c < 0),
            "<=" => Value.Of(c <= 0),
            ">=" => Value.Of(c >= 0),
            ">" => Value.Of(c > 0),
            "^" => Value.Of(CompareInt(a.Major, b.Major) == 0),
            "~" => Value.Of(CompareInt(a.Major, b.Major) == 0 && CompareInt(a.Minor, b.Minor) == 0),
            _ => Value.Null,
        };
    }

    // ensureString (fmt %v for non-strings) then the v prefix.
    private static string Normalize(Value v)
    {
        var s = v.Kind switch
        {
            Kind.String => v.AsString,
            Kind.Number => Values.FormatGo(v.Number),
            Kind.Bool => v.AsBool ? "true" : "false",
            Kind.Null => "<nil>",
            _ => "<object>",
        };
        if (!s.StartsWith('v'))
        {
            s = "v" + (s.StartsWith('V') ? s[1..] : s);
        }

        return s;
    }

    private readonly record struct Version(string Major, string Minor, string Patch, string Prerelease);

    private static bool TryParse(string v, out Version version)
    {
        version = default;
        var i = 1;
        if (v.Length == 0 || v[0] != 'v' || !Int(v, ref i, out var major))
        {
            return false;
        }

        string minor = "0", patch = "0", pre = string.Empty;
        if (i < v.Length)
        {
            if (v[i++] != '.' || !Int(v, ref i, out minor))
            {
                return false;
            }

            if (i < v.Length)
            {
                if (v[i++] != '.' || !Int(v, ref i, out patch))
                {
                    return false;
                }

                if (i < v.Length && v[i] == '-')
                {
                    var start = i;
                    if (!Identifiers(v, ref i, '+', numericRule: true))
                    {
                        return false;
                    }

                    pre = v[start..i];
                }

                if (i < v.Length && v[i] == '+' && !Identifiers(v, ref i, '\0', numericRule: false))
                {
                    return false;
                }

                if (i < v.Length)
                {
                    return false;
                }
            }
        }

        version = new Version(major, minor, patch, pre);
        return true;
    }

    private static bool Int(string v, ref int i, out string value)
    {
        var start = i;
        while (i < v.Length && char.IsAsciiDigit(v[i]))
        {
            i++;
        }

        value = v[start..i];
        return value.Length > 0 && (value[0] != '0' || value.Length == 1);
    }

    // Dot-separated non-empty [0-9A-Za-z-] identifiers after the leading '-' or '+', up to stop (or the end).
    private static bool Identifiers(string v, ref int i, char stop, bool numericRule)
    {
        var start = ++i;
        for (; i < v.Length && v[i] != stop; i++)
        {
            if (v[i] == '.')
            {
                if (start == i || (numericRule && IsBadNum(v[start..i])))
                {
                    return false;
                }

                start = i + 1;
            }
            else if (!char.IsAsciiLetterOrDigit(v[i]) && v[i] != '-')
            {
                return false;
            }
        }

        return start != i && !(numericRule && IsBadNum(v[start..i]));
    }

    private static bool IsBadNum(string s) => s.Length > 1 && s[0] == '0' && s.All(char.IsAsciiDigit);

    private static bool IsNum(string s) => s.Length > 0 && s.All(char.IsAsciiDigit);

    private static int Compare(Version a, Version b)
    {
        var c = CompareInt(a.Major, b.Major);
        if (c == 0)
        {
            c = CompareInt(a.Minor, b.Minor);
        }

        if (c == 0)
        {
            c = CompareInt(a.Patch, b.Patch);
        }

        return c != 0 ? c : ComparePrerelease(a.Prerelease, b.Prerelease);
    }

    private static int CompareInt(string x, string y) =>
        x.Length != y.Length ? x.Length.CompareTo(y.Length) : Math.Sign(string.CompareOrdinal(x, y));

    private static int ComparePrerelease(string x, string y)
    {
        if (x == y)
        {
            return 0;
        }

        if (x.Length == 0)
        {
            return 1;
        }

        if (y.Length == 0)
        {
            return -1;
        }

        var xs = x[1..].Split('.');
        var ys = y[1..].Split('.');
        for (var i = 0; i < Math.Min(xs.Length, ys.Length); i++)
        {
            string dx = xs[i], dy = ys[i];
            if (dx == dy)
            {
                continue;
            }

            bool ix = IsNum(dx), iy = IsNum(dy);
            if (ix != iy)
            {
                return ix ? -1 : 1;
            }

            if (ix && dx.Length != dy.Length)
            {
                return dx.Length < dy.Length ? -1 : 1;
            }

            return string.CompareOrdinal(dx, dy) < 0 ? -1 : 1;
        }

        return xs.Length < ys.Length ? -1 : 1;
    }
}
