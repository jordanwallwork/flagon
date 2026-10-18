namespace Jorrk.Flagon.Testing.Tests;

public class FlagStateTests
{
    private static EvaluationContext User(string targetingKey) => new(targetingKey);

    [Fact]
    public void EnableFor_turns_a_flag_on_only_for_the_listed_targeting_keys()
    {
        using var flags = TestFlagon.Create();

        flags.Flags.EnableFor("beta", "user-1", "user-2");

        Assert.True(flags.IsEnabled("beta", context: User("user-1")));
        Assert.True(flags.IsEnabled("beta", context: User("user-2")));
        Assert.False(flags.IsEnabled("beta", true, User("user-3")));
        Assert.False(flags.IsEnabled("beta", true));
        Assert.Equal(EvaluationReason.TargetingMatch, flags.GetBooleanDetails("beta", context: User("user-1")).Reason);
    }

    [Fact]
    public void EnableFor_with_no_targeting_keys_is_off_for_everyone()
    {
        using var flags = TestFlagon.Create();

        flags.Flags.EnableFor("beta");

        Assert.False(flags.IsEnabled("beta", true, User("user-1")));
    }

    [Fact]
    public void A_disabled_flag_of_any_type_serves_the_callers_default_until_enabled()
    {
        using var flags = TestFlagon.Create();
        flags.Flags.SetFlag("greeting", "hello", """{"if":[true,"default",null]}""");

        flags.Flags.Disable("greeting");

        var disabled = flags.GetStringDetails("greeting", "caller-default");
        Assert.Equal("caller-default", disabled.Value);
        Assert.Equal(EvaluationReason.Disabled, disabled.Reason);

        flags.Flags.Enable("greeting");

        var enabled = flags.GetStringDetails("greeting", "caller-default");
        Assert.Equal("hello", enabled.Value);
        Assert.Equal(EvaluationReason.TargetingMatch, enabled.Reason);
    }

    [Fact]
    public void An_archived_flag_evaluates_with_reason_archived()
    {
        using var flags = TestFlagon.Create();
        flags.Flags.SetFlag("old-checkout", true);

        flags.Flags.Archive("old-checkout").Archive("old-checkout");

        var details = flags.GetBooleanDetails("old-checkout", false);
        Assert.False(details.Value);
        Assert.Equal(EvaluationReason.Archived, details.Reason);
    }

    [Fact]
    public void A_removed_flag_evaluates_as_not_found_and_can_be_set_again()
    {
        using var flags = TestFlagon.Create();
        flags.Flags.SetFlag("old-checkout", true).Disable("old-checkout");

        flags.Flags.Remove("old-checkout").Remove("never-set");

        Assert.Equal(ErrorCode.FlagNotFound, flags.GetBooleanDetails("old-checkout").ErrorCode);
        flags.Flags.SetFlag("old-checkout", true);
        Assert.True(flags.IsEnabled("old-checkout"));
    }

    [Fact]
    public void An_archived_flag_can_be_removed()
    {
        using var flags = TestFlagon.Create();
        flags.Flags.SetFlag("old-checkout", true).Archive("old-checkout");

        flags.Flags.Remove("old-checkout");

        Assert.Equal(ErrorCode.FlagNotFound, flags.GetBooleanDetails("old-checkout").ErrorCode);
    }

    [Fact]
    public void Disable_Enable_and_Archive_on_a_missing_flag_throw_FlagNotFoundException()
    {
        using var flags = TestFlagon.Create();

        Assert.Throws<FlagNotFoundException>(() => flags.Flags.Disable("missing"));
        Assert.Throws<FlagNotFoundException>(() => flags.Flags.Enable("missing"));
        Assert.Throws<FlagNotFoundException>(() => flags.Flags.Archive("missing"));
    }

    [Fact]
    public void A_flag_can_be_replaced_by_one_of_another_type_and_state()
    {
        using var flags = TestFlagon.Create();
        flags.Flags.SetFlag("checkout", true).Disable("checkout");

        flags.Flags.SetFlag("checkout", "v2");

        var details = flags.GetStringDetails("checkout", "v1");
        Assert.Equal("v2", details.Value);
        Assert.Equal(EvaluationReason.Static, details.Reason);
        Assert.Equal(ErrorCode.TypeMismatch, flags.GetBooleanDetails("checkout").ErrorCode);
    }

    [Fact]
    public async Task State_changes_are_unaudited_test_writes()
    {
        using var flags = TestFlagon.Create();

        flags.Flags.SetFlag("checkout", true).Disable("checkout").Enable("checkout").Archive("checkout");

        Assert.Empty(await flags.Admin.GetAuditHistoryAsync("checkout"));
    }
}
