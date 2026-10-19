namespace Jorrk.Flagon.Benchmarks;

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
    /// Nested <c>if</c>/<c>and</c>/<c>or</c>: the body of a <c>$ref</c>'d evaluator, already inlined, so the rule
    /// contains no <c>$ref</c>. Flagon rejects <c>$ref</c> on write, so a flag-set's <c>$evaluators</c> must be inlined
    /// before writing (as the parity harness does); this measures the inlined rule, the same one flagd runs after its
    /// own substitution, not <c>$ref</c> resolution.
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
