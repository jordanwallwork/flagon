namespace Flagon.Benchmarks;

/// <summary>The flag documents the benchmarks evaluate (flagd schema, boolean variants).</summary>
internal static class Rules
{
    public const string Static = """
        { "state": "ENABLED", "variants": { "on": true, "off": false }, "defaultVariant": "on" }
        """;

    /// <summary>Simple rule: <c>==</c> on an attribute.</summary>
    public const string EqualsAttribute = """
        {
          "state": "ENABLED", "variants": { "on": true, "off": false }, "defaultVariant": "off",
          "targeting": { "if": [ { "==": [ { "var": "country" }, "GB" ] }, "on", null ] }
        }
        """;

    /// <summary>Simple rule: <c>in</c> on a literal list.</summary>
    public const string InList = """
        {
          "state": "ENABLED", "variants": { "on": true, "off": false }, "defaultVariant": "off",
          "targeting": { "if": [ { "in": [ { "var": "country" }, [ "FR", "DE", "ES", "GB" ] ] }, "on", null ] }
        }
        """;

    /// <summary><c>fractional</c> 3-way split on the implicit targeting key.</summary>
    public const string Fractional = """
        {
          "state": "ENABLED", "variants": { "on": true, "off": false, "maybe": false }, "defaultVariant": "off",
          "targeting": { "fractional": [ [ "on", 34 ], [ "off", 33 ], [ "maybe", 33 ] ] }
        }
        """;

    /// <summary>
    /// Nested <c>if</c>/<c>and</c>/<c>or</c> over the body of a <c>$ref</c>'d evaluator. Flagon stores flags
    /// individually, so a flag-set <c>$evaluators</c> entry is inlined on import (as the parity harness does); the
    /// compiled rule is the same as flagd's after its own substitution.
    /// </summary>
    public const string Nested = """
        {
          "state": "ENABLED", "variants": { "on": true, "off": false }, "defaultVariant": "off",
          "targeting": {
            "if": [
              { "and": [
                  { "in": [ "@example.com", { "var": "email" } ] },
                  { "or": [ { "==": [ { "var": "plan" }, "pro" ] }, { ">=": [ { "var": "seats" }, 10 ] } ] },
                  { "starts_with": [ { "var": "region" }, "eu-" ] }
              ] },
              "on",
              { "if": [ { "==": [ { "var": "country" }, "GB" ] }, "on", "off" ] }
            ]
          }
        }
        """;
}
