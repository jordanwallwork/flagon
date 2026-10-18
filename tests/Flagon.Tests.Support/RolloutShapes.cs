namespace Flagon.Tests.Support;

/// <summary>
/// The one fixture of targeting shapes and whether each is a simple percentage rollout: exactly
/// <c>{"fractional": [["on", N], ["off", 100 - N]]}</c> with default bucketing and an integer N.
/// Anything else, however close, is not a rollout. Every shape here is a valid rule.
/// </summary>
public static class RolloutShapes
{
    /// <summary>Canonical rollouts and their percentage.</summary>
    public static IEnumerable<object[]> Rollouts => new (string Targeting, int Percentage)[]
    {
        ("""{ "fractional": [ [ "on", 10 ], [ "off", 90 ] ] }""", 10),
        ("""{"fractional":[["on",0],["off",100]]}""", 0),
        ("""{ "fractional": [ [ "on", 100 ], [ "off", 0 ] ] }""", 100),
        ("""{ "fractional": [ [ "on", 50 ], [ "off", 50 ] ] }""", 50),
    }.Select(r => new object[] { r.Targeting, r.Percentage });

    /// <summary>Near misses: valid targeting that is not a simple rollout.</summary>
    public static IEnumerable<object[]> NotRollouts => new[]
    {
        """{ "fractional": [ { "var": "email" }, [ "on", 10 ], [ "off", 90 ] ] }""",
        """{ "fractional": [ [ "off", 90 ], [ "on", 10 ] ] }""",
        """{ "fractional": [ [ "on", 10 ], [ "off", 80 ] ] }""",
        """{ "fractional": [ [ "on", 10 ], [ "off", 90 ] ], "extra": 1 }""",
        """{ "if": [ { "fractional": [ [ "on", 10 ], [ "off", 90 ] ] }, "on", "off" ] }""",
        """{ "fractional": [ [ "on", 10 ], [ "off", 80 ], [ "on", 10 ] ] }""",
        """{ "fractional": [ [ "on", 10.5 ], [ "off", 89.5 ] ] }""",
        """{ "fractional": [ [ "on", 10 ] ] }""",
        """{ "fractional": [ [ "on", 10 ], [ "off", 90 ], [ "x", 0 ] ] }""",
        """{ "if": [ { "==": [ { "var": "country" }, "GB" ] }, "on", null ] }""",
        """{}""",
    }.Select(t => new object[] { t });
}
