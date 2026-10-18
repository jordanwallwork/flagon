namespace Flagon.Testing.Tests;

public class TargetingTests
{
    private const string StaffOnly = """{"if":[{"ends_with":[{"var":"email"},"@example.com"]},"on","off"]}""";

    private static EvaluationContext Email(string email) =>
        new(attributes: new Dictionary<string, object?> { ["email"] = email });

    [Fact]
    public void A_boolean_flag_with_a_rule_serves_the_variant_the_rule_names()
    {
        using var flags = TestFlagon.Create();

        flags.Flags.SetFlag("staff-tools", false, targeting: StaffOnly);

        var staff = flags.GetBooleanDetails("staff-tools", context: Email("ann@example.com"));
        Assert.True(staff.Value);
        Assert.Equal(EvaluationReason.TargetingMatch, staff.Reason);
        Assert.False(flags.IsEnabled("staff-tools", true, Email("bob@elsewhere.org")));
    }

    [Fact]
    public void Every_typed_overload_takes_a_rule()
    {
        using var flags = TestFlagon.Create();
        const string Rule = """{"if":[{"==":[{"var":"tier"},"gold"]},"default",null]}""";
        var gold = new EvaluationContext(attributes: new Dictionary<string, object?> { ["tier"] = "gold" });

        flags.Flags
            .SetFlag("greeting", "hello", Rule)
            .SetFlag("limit", 5, Rule)
            .SetFlag("ratio", 0.5, Rule)
            .SetObjectFlag("layout", """{"Columns":2}""", Rule)
            .SetFlag("theme", new Theme("dark"), options: null, targeting: Rule)
            .SetFlag("size", new Dictionary<string, string> { ["s"] = "small", ["l"] = "large" }, "s",
                """{"if":[{"==":[{"var":"tier"},"gold"]},"l",null]}""");

        Assert.Equal(EvaluationReason.TargetingMatch, flags.GetStringDetails("greeting", "", gold).Reason);
        Assert.Equal(EvaluationReason.TargetingMatch, flags.GetIntDetails("limit", 0, gold).Reason);
        Assert.Equal(EvaluationReason.TargetingMatch, flags.GetDoubleDetails("ratio", 0, gold).Reason);
        Assert.Equal(EvaluationReason.TargetingMatch, flags.GetObjectDetails("layout", new Layout(0), gold).Reason);
        Assert.Equal(EvaluationReason.TargetingMatch, flags.GetObjectDetails("theme", new Theme(""), gold).Reason);
        Assert.Equal("large", flags.GetString("size", "", gold));
        Assert.Equal("small", flags.GetString("size", ""));
    }

    [Fact]
    public void Targeting_that_is_not_JSON_throws_FlagValidationException_at_SetFlag()
    {
        using var flags = TestFlagon.Create();

        Assert.Throws<FlagValidationException>(() => flags.Flags.SetFlag("staff-tools", true, targeting: "{not json"));
    }

    [Fact]
    public void A_rule_the_targeting_compiler_rejects_throws_FlagTargetingException_at_SetFlag()
    {
        using var flags = TestFlagon.Create();

        Assert.Throws<FlagTargetingException>(() =>
            flags.Flags.SetFlag("staff-tools", true, targeting: """{"if":[{"unknown_op":[1]},"on",null]}"""));
        Assert.Equal(ErrorCode.FlagNotFound, flags.GetBooleanDetails("staff-tools").ErrorCode);
    }

    [Fact]
    public void SetTargeting_replaces_and_clears_the_rule_on_an_existing_flag()
    {
        using var flags = TestFlagon.Create();
        flags.Flags.SetFlag("staff-tools", false);

        flags.Flags.SetTargeting("staff-tools", StaffOnly);
        Assert.True(flags.IsEnabled("staff-tools", context: Email("ann@example.com")));

        flags.Flags.SetTargeting("staff-tools", null);
        var details = flags.GetBooleanDetails("staff-tools", context: Email("ann@example.com"));
        Assert.False(details.Value);
        Assert.Equal(EvaluationReason.Static, details.Reason);
    }

    [Fact]
    public void SetTargeting_on_a_missing_flag_throws_FlagNotFoundException()
    {
        using var flags = TestFlagon.Create();

        Assert.Throws<FlagNotFoundException>(() => flags.Flags.SetTargeting("staff-tools", StaffOnly));
    }

    [Fact]
    public void SetTargeting_with_a_rule_the_compiler_rejects_throws_and_keeps_the_old_rule()
    {
        using var flags = TestFlagon.Create();
        flags.Flags.SetFlag("staff-tools", false, StaffOnly);

        Assert.Throws<FlagTargetingException>(() =>
            flags.Flags.SetTargeting("staff-tools", """{"if":[{"unknown_op":[1]},"on",null]}"""));
        Assert.True(flags.IsEnabled("staff-tools", context: Email("ann@example.com")));
    }

    // Expected buckets are flagd's own (testbed targeting.feature, "Fractional operator", @fractional-v3 examples).
    [Theory]
    [InlineData("jack", "diamonds")]
    [InlineData("queen", "diamonds")]
    [InlineData("ten", "clubs")]
    [InlineData("nine", "clubs")]
    [InlineData("3", "spades")]
    public void A_fractional_rollout_assigns_the_same_bucket_as_flagd(string name, string expected)
    {
        using var flags = TestFlagon.Create();
        var suits = new Dictionary<string, string>
        {
            ["clubs"] = "clubs", ["diamonds"] = "diamonds", ["hearts"] = "hearts", ["spades"] = "spades", ["wild"] = "wild",
        };
        flags.Flags.SetFlag("fractional-flag", suits, "wild", """
            {"fractional":[{"cat":[{"var":"$flagd.flagKey"},{"var":"user.name"}]},
              ["clubs",25],["diamonds",25],["hearts",25],["spades",25]]}
            """);

        var context = new EvaluationContext(attributes: new Dictionary<string, object?>
        {
            ["user"] = new Dictionary<string, object?> { ["name"] = name },
        });
        Assert.Equal(expected, flags.GetString("fractional-flag", "fallback", context));
    }

    [Fact]
    public void Reading_a_flag_as_another_type_is_a_type_mismatch_as_in_production()
    {
        using var flags = TestFlagon.Create();
        flags.Flags.SetFlag("greeting", "hello");

        var details = flags.GetIntDetails("greeting", 7);

        Assert.Equal(7, details.Value);
        Assert.Equal(EvaluationReason.Error, details.Reason);
        Assert.Equal(ErrorCode.TypeMismatch, details.ErrorCode);
    }

    public sealed record Layout(int Columns);

    public sealed record Theme(string Name);
}
