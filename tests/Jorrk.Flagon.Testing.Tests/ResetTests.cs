namespace Jorrk.Flagon.Testing.Tests;

public class ResetTests
{
    private static void App(FlagonOptions flagon) => flagon
        .Define("new-checkout", true)
        .Define("page-size", 25);

    [Fact]
    public void Reset_removes_set_flags_and_restores_defined_flags()
    {
        using var flags = TestFlagon.Create(App);
        flags.Flags
            .SetFlag("new-checkout", false)
            .SetFlag("page-size", "not a number")
            .SetFlag("extra", true);

        flags.Flags.Reset();

        Assert.True(flags.IsEnabled("new-checkout"));
        Assert.Equal(25, flags.GetInt("page-size", 0));
        Assert.Equal(ErrorCode.FlagNotFound, flags.GetBooleanDetails("extra").ErrorCode);
    }

    [Fact]
    public async Task Reset_adds_no_audit_entries()
    {
        using var flags = TestFlagon.Create(App);
        var before = (await flags.Admin.GetAuditHistoryAsync("new-checkout")).Count;
        flags.Flags.SetFlag("new-checkout", false);

        flags.Flags.Reset().Reset();

        Assert.Equal(before, (await flags.Admin.GetAuditHistoryAsync("new-checkout")).Count);
        Assert.True(flags.IsEnabled("new-checkout"));
    }

    [Fact]
    public void Reset_restores_defined_flags_that_were_disabled_archived_or_removed()
    {
        using var flags = TestFlagon.Create(App);
        flags.Flags.Disable("new-checkout").Archive("page-size");

        flags.Flags.Reset();

        Assert.Equal(EvaluationReason.Static, flags.GetBooleanDetails("new-checkout").Reason);
        Assert.Equal(25, flags.GetInt("page-size", 0));

        flags.Flags.Remove("new-checkout").Reset();

        Assert.True(flags.IsEnabled("new-checkout"));
    }

    [Fact]
    public void Reset_leaves_the_flagon_empty_when_definitions_are_not_seeded()
    {
        using var flags = TestFlagon.Create(App, t => t.SeedDefinitions = false);
        flags.Flags.SetFlag("new-checkout", true);

        flags.Flags.Reset();

        Assert.Empty(flags.GetAllDetails().Flags);
    }

    [Fact]
    public async Task Store_versions_keep_rising_across_a_reset()
    {
        using var flags = TestFlagon.Create(App);
        flags.Flags.SetFlag("new-checkout", false);
        var before = (await flags.Admin.GetAsync("new-checkout"))!.Version;

        flags.Flags.Reset();

        Assert.True((await flags.Admin.GetAsync("new-checkout"))!.Version > before);
    }

    [Fact]
    public void Reset_chains()
    {
        using var flags = TestFlagon.Create(App);

        flags.Flags.Reset().SetFlag("extra", true);

        Assert.True(flags.IsEnabled("extra"));
    }

    [Fact]
    public void Reset_clears_the_evaluation_log()
    {
        using var flags = TestFlagon.Create(App);
        flags.IsEnabled("new-checkout");

        flags.Flags.Reset();
        Assert.Empty(flags.Evaluations);

        flags.IsEnabled("new-checkout");
        Assert.Single(flags.Evaluations);
    }
}
