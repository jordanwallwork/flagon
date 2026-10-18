namespace Flagon.Tests.Support;

/// <summary>
/// The one fixture of targeting shapes and whether each is a canonical split:
/// exactly <c>{"fractional": [[v1, w1], …, [vn, wn]]}</c> with n ≥ 2, literal distinct defined variant names, integral
/// weights (any JSON number with no fractional part, so <c>25.0</c> is 25: the fractional engine weights it identically)
/// from 0 to 100 summing to 100, and default bucketing. A boolean flag's split of exactly
/// <c>[["on", N], ["off", 100 - N]]</c> is also a simple percentage rollout N. Anything else, however close, is a custom rule. Every shape here is a valid rule. The
/// UI's <c>splitBuckets</c> (app.js) mirrors this recogniser; keep the two in step with this fixture.
/// </summary>
public static class RolloutShapes
{
    /// <summary>The variants of the typed flag the split cases use (null variants in a case means a boolean flag).</summary>
    public const string ColourVariants = """{ "red": "red", "green": "green", "blue": "blue" }""";

    /// <summary>Canonical boolean rollouts and their percentage.</summary>
    public static IEnumerable<object[]> Rollouts => new (string Targeting, int Percentage)[]
    {
        ("""{ "fractional": [ [ "on", 10 ], [ "off", 90 ] ] }""", 10),
        ("""{"fractional":[["on",0],["off",100]]}""", 0),
        ("""{ "fractional": [ [ "on", 100 ], [ "off", 0 ] ] }""", 100),
        ("""{ "fractional": [ [ "on", 50 ], [ "off", 50 ] ] }""", 50),
        ("""{ "fractional": [ [ "on", 25.0 ], [ "off", 75.0 ] ] }""", 25),
        ("""{ "fractional": [ [ "on", 2.5e1 ], [ "off", 75 ] ] }""", 25),
    }.Select(r => new object[] { r.Targeting, r.Percentage });

    /// <summary>
    /// Canonical splits that are not simple percentage rollouts: the flag's variants (null for a boolean flag), the
    /// targeting, and the buckets as <c>variant=percentage</c> in rule order.
    /// </summary>
    public static IEnumerable<object?[]> Splits => new (string? Variants, string Targeting, string[] Buckets)[]
    {
        (ColourVariants, """{ "fractional": [ [ "red", 34 ], [ "green", 33 ], [ "blue", 33 ] ] }""", ["red=34", "green=33", "blue=33"]),
        (ColourVariants, """{ "fractional": [ [ "blue", 50 ], [ "red", 50 ] ] }""", ["blue=50", "red=50"]),
        (ColourVariants, """{ "fractional": [ [ "red", 0 ], [ "green", 100.0 ], [ "blue", 0 ] ] }""", ["red=0", "green=100", "blue=0"]),
        (null, """{ "fractional": [ [ "off", 90 ], [ "on", 10 ] ] }""", ["off=90", "on=10"]),
        // Variants named on/off do not make a boolean flag: the rollout percentage is boolean-only.
        ("""{ "on": "loud", "off": "quiet" }""", """{ "fractional": [ [ "on", 10 ], [ "off", 90 ] ] }""", ["on=10", "off=90"]),
    }.Select(s => new object?[] { s.Variants, s.Targeting, s.Buckets });

    /// <summary>Near misses: the flag's variants (null for a boolean flag) and valid targeting that is not a canonical split.</summary>
    public static IEnumerable<object?[]> NotSplits => new (string? Variants, string Targeting)[]
    {
        (null, """{ "fractional": [ { "var": "email" }, [ "on", 10 ], [ "off", 90 ] ] }"""),
        (null, """{ "fractional": [ [ "on", 10 ], [ "off", 80 ] ] }"""),
        (null, """{ "fractional": [ [ "on", 10 ], [ "off", 90 ] ], "extra": 1 }"""),
        (null, """{ "if": [ { "fractional": [ [ "on", 10 ], [ "off", 90 ] ] }, "on", "off" ] }"""),
        (null, """{ "fractional": [ [ "on", 10 ], [ "off", 80 ], [ "on", 10 ] ] }"""),
        (null, """{ "fractional": [ [ "on", 10.5 ], [ "off", 89.5 ] ] }"""),
        (null, """{ "fractional": [ [ "on", 10 ] ] }"""),
        (null, """{ "fractional": [ [ "on", -10 ], [ "off", 110 ] ] }"""),
        (null, """{ "fractional": [ [ "on", 10 ], [ "off", 90 ], [ "x", 0 ] ] }"""),
        (null, """{ "if": [ { "==": [ { "var": "country" }, "GB" ] }, "on", null ] }"""),
        (null, """{}"""),
        (ColourVariants, """{ "fractional": [ [ "red", 34 ], [ "green", 33 ], [ "blue", 34 ] ] }"""),
        (ColourVariants, """{ "fractional": [ [ "red", 50 ], [ "purple", 50 ] ] }"""),
        (ColourVariants, """{ "fractional": [ [ "red", 50 ], [ "red", 50 ] ] }"""),
        (ColourVariants, """{ "fractional": [ { "cat": [ { "var": "$flagd.flagKey" }, { "var": "email" } ] }, [ "red", 50 ], [ "blue", 50 ] ] }"""),
        (ColourVariants, """{ "fractional": [ "user-1", [ "red", 50 ], [ "blue", 50 ] ] }"""),
        (ColourVariants, """{ "fractional": [ [ "red" ], [ "blue" ] ] }"""),
        (ColourVariants, """{ "fractional": [ [ { "var": "colour" }, 50 ], [ "blue", 50 ] ] }"""),
        (ColourVariants, """{ "fractional": [ [ "red", 100 ] ] }"""),
    }.Select(s => new object?[] { s.Variants, s.Targeting });
}
