namespace Flagon.Testing.Tests;

public class VariantSetFlagTests
{
    private static readonly Dictionary<string, int> PageSizes = new() { ["small"] = 10, ["medium"] = 25, ["large"] = 50 };

    [Fact]
    public void A_flag_with_named_variants_serves_its_default_variant()
    {
        using var flags = TestFlagon.Create();

        flags.Flags.SetFlag("page-size", PageSizes, "medium");

        var details = flags.GetIntDetails("page-size", 1);
        Assert.Equal(25, details.Value);
        Assert.Equal("medium", details.Variant);
        Assert.Equal(EvaluationReason.Static, details.Reason);
    }

    [Fact]
    public void A_flag_with_no_default_variant_serves_the_callers_default()
    {
        using var flags = TestFlagon.Create();

        flags.Flags.SetFlag<int>("page-size", PageSizes, defaultVariant: null);

        var details = flags.GetIntDetails("page-size", 1);
        Assert.Equal(1, details.Value);
        Assert.Null(details.Variant);
    }

    [Fact]
    public void A_default_variant_that_is_not_one_of_the_variants_throws_at_SetFlag()
    {
        using var flags = TestFlagon.Create();

        Assert.Throws<FlagValidationException>(() => flags.Flags.SetFlag("page-size", PageSizes, "huge"));
    }

    [Fact]
    public void Boolean_variants_other_than_on_and_off_throw_at_SetFlag()
    {
        using var flags = TestFlagon.Create();

        Assert.Throws<FlagValidationException>(() =>
            flags.Flags.SetFlag("beta", new Dictionary<string, bool> { ["yes"] = true, ["no"] = false }, "yes"));
    }

    [Fact]
    public void An_empty_variant_set_throws_at_SetFlag()
    {
        using var flags = TestFlagon.Create();

        Assert.Throws<FlagValidationException>(() => flags.Flags.SetFlag("page-size", new Dictionary<string, int>(), null));
    }

    [Fact]
    public void Object_variants_are_served_as_JSON_objects()
    {
        using var flags = TestFlagon.Create();
        var layouts = new Dictionary<string, System.Text.Json.JsonElement>
        {
            ["grid"] = System.Text.Json.JsonSerializer.SerializeToElement(new { Columns = 3 }),
            ["list"] = System.Text.Json.JsonSerializer.SerializeToElement(new { Columns = 1 }),
        };

        flags.Flags.SetFlag("layout", layouts, "grid");

        Assert.Equal(3, flags.GetObject("layout", new Layout(0)).Columns);
    }

    public sealed record Layout(int Columns);
}
