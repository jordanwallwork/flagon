namespace Jorrk.Flagon.Testing.Tests;

public class SwitchesTests
{
    [Fact]
    public void Background_writes_are_off_and_seeding_and_recording_are_on_by_default()
    {
        var switches = new FlagonTestingSwitches();

        Assert.False(switches.AutoCreate);
        Assert.False(switches.UsageTracking);
        Assert.True(switches.SeedDefinitions);
        Assert.True(switches.RecordEvaluations);
    }

    [Fact]
    public void Flags_defined_in_code_are_seeded()
    {
        using var flags = TestFlagon.Create(f => f.Define("new-checkout", true));

        var details = flags.GetBooleanDetails("new-checkout", defaultValue: false);

        Assert.True(details.Value);
        Assert.Equal(EvaluationReason.Static, details.Reason);
    }

    [Fact]
    public void Flags_defined_in_code_are_absent_when_seeding_is_off()
    {
        using var flags = TestFlagon.Create(
            f => f.Define("new-checkout", true),
            t => t.SeedDefinitions = false);

        Assert.Equal(ErrorCode.FlagNotFound, flags.GetBooleanDetails("new-checkout").ErrorCode);
    }

    [Fact]
    public void A_defined_flag_whose_targeting_does_not_compile_fails_Create()
    {
        Assert.Throws<FlagTargetingException>(() => TestFlagon.Create(f =>
        {
            f.FailFastOnStartup = false;
            f.Define("new-checkout", true, targeting: """{"no-such-operator":[1]}""");
        }));
    }

    [Fact]
    public async Task An_unknown_key_is_not_auto_created_by_default_even_when_the_app_turns_it_on()
    {
        using var flags = TestFlagon.Create(f => f.AutoCreate = true);

        flags.IsEnabled("unknown", defaultValue: true);
        await Task.Delay(100);

        Assert.Null(await flags.Admin.GetAsync("unknown"));
        Assert.Equal(ErrorCode.FlagNotFound, flags.GetBooleanDetails("unknown").ErrorCode);
    }

    [Fact]
    public async Task An_unknown_key_is_auto_created_in_the_background_when_switched_on()
    {
        using var flags = TestFlagon.Create(testing: t => t.AutoCreate = true);

        flags.IsEnabled("unknown", defaultValue: true);

        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (flags.GetBooleanDetails("unknown").ErrorCode == ErrorCode.FlagNotFound && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
        }

        Assert.Equal(FlagSource.Auto, (await flags.Admin.GetAsync("unknown"))!.Document.Metadata.Source);
        Assert.True(flags.IsEnabled("unknown", defaultValue: false));
    }
}
