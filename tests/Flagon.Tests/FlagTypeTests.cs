namespace Flagon.Tests;

/// <summary>A flag's type is derived from its variant values, as flagd does (no stored field).</summary>
public class FlagTypeTests
{
    private static FlagDocument WithVariants(string variantsJson) =>
        FlagdJson.Deserialize($$"""{ "state": "ENABLED", "variants": {{variantsJson}}, "defaultVariant": null }""");

    [Theory]
    [InlineData("""{ "on": true, "off": false }""", FlagType.Boolean)]
    [InlineData("""{ "only": false }""", FlagType.Boolean)]
    [InlineData("""{ "light": "light", "dark": "dark" }""", FlagType.String)]
    [InlineData("""{ "empty": "" }""", FlagType.String)]
    [InlineData("""{ "small": 10, "large": 100 }""", FlagType.Number)]
    [InlineData("""{ "half": 0.5, "third": 0.333 }""", FlagType.Number)]
    [InlineData("""{ "whole": 1, "fraction": 1.5 }""", FlagType.Number)]
    [InlineData("""{ "a": { "columns": 2 }, "b": {} }""", FlagType.Object)]
    public void A_flag_whose_variants_share_one_type_has_that_type(string variantsJson, FlagType expected) =>
        Assert.Equal(expected, WithVariants(variantsJson).Type);

    [Fact]
    public void A_flag_with_no_variants_has_no_type() =>
        Assert.Null(WithVariants("{}").Type);

    [Theory]
    [InlineData("""{ "on": true, "name": "x" }""")]
    [InlineData("""{ "count": 1, "label": "1" }""")]
    [InlineData("""{ "config": {}, "flag": false }""")]
    [InlineData("""{ "list": [1, 2], "other": [3] }""")]
    [InlineData("""{ "nothing": null }""")]
    public void A_flag_whose_variants_are_mixed_or_not_a_flagd_type_has_no_type(string variantsJson) =>
        Assert.Null(WithVariants(variantsJson).Type);

    [Fact]
    public void A_boolean_flag_created_by_flagon_is_boolean() =>
        Assert.Equal(FlagType.Boolean, FlagDocument.Boolean(enabled: true).Type);
}
