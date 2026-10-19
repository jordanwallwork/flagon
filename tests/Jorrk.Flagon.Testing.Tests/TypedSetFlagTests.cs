namespace Jorrk.Flagon.Testing.Tests;

public class TypedSetFlagTests
{
    [Fact]
    public void A_string_flag_is_served_from_a_single_default_variant()
    {
        using var flags = TestFlagon.Create();

        flags.Flags.SetFlag("checkout-theme", "dark");

        var details = flags.GetStringDetails("checkout-theme", "light");
        Assert.Equal("dark", details.Value);
        Assert.Equal("default", details.Variant);
        Assert.Equal(EvaluationReason.Static, details.Reason);
    }

    [Fact]
    public void An_int_flag_round_trips_through_GetInt()
    {
        using var flags = TestFlagon.Create();

        flags.Flags.SetFlag("max-items", 25);

        var details = flags.GetIntDetails("max-items", 10);
        Assert.Equal(25, details.Value);
        Assert.Equal("default", details.Variant);
    }

    [Fact]
    public void A_double_flag_round_trips_through_GetDouble()
    {
        using var flags = TestFlagon.Create();

        flags.Flags.SetFlag("discount", 0.15);

        Assert.Equal(0.15, flags.GetDouble("discount", 0));
    }

    [Fact]
    public void A_double_that_JSON_cannot_hold_throws_at_SetFlag()
    {
        using var flags = TestFlagon.Create();

        Assert.Throws<FlagValidationException>(() => flags.Flags.SetFlag("discount", double.NaN));
    }

    [Fact]
    public void An_object_flag_set_from_JSON_round_trips_through_GetObject()
    {
        using var flags = TestFlagon.Create();

        flags.Flags.SetObjectFlag("layout", """{"Columns":3,"Compact":true}""");

        var details = flags.GetObjectDetails("layout", new Layout(1, false));
        Assert.Equal(new Layout(3, true), details.Value);
        Assert.Equal("default", details.Variant);
    }

    [Fact]
    public void An_object_flag_set_from_a_value_round_trips_through_GetObject()
    {
        using var flags = TestFlagon.Create();

        flags.Flags.SetFlag("layout", new Layout(4, true));

        Assert.Equal(new Layout(4, true), flags.GetObject("layout", new Layout(1, false)));
    }

    [Fact]
    public void Serializer_options_shape_an_object_value_as_GetObject_reads_it_with_them()
    {
        using var flags = TestFlagon.Create();
        var snakeCase = new System.Text.Json.JsonSerializerOptions { PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.SnakeCaseLower };

        flags.Flags.SetFlag("layout", new Layout(4, true), snakeCase);

        Assert.Equal(new Layout(4, true), flags.GetObject("layout", new Layout(1, false), options: snakeCase));
    }

    [Theory]
    [InlineData("[1,2]")]
    [InlineData("\"text\"")]
    [InlineData("{not json")]
    public void An_object_flag_that_is_not_a_JSON_object_throws_at_SetObjectFlag(string json)
    {
        using var flags = TestFlagon.Create();

        Assert.Throws<FlagValidationException>(() => flags.Flags.SetObjectFlag("layout", json));
    }

    [Fact]
    public void A_value_that_does_not_serialise_to_a_JSON_object_throws_at_SetFlag()
    {
        using var flags = TestFlagon.Create();

        Assert.Throws<FlagValidationException>(() => flags.Flags.SetFlag("layout", new[] { 1, 2 }));
    }

    public sealed record Layout(int Columns, bool Compact);
}
