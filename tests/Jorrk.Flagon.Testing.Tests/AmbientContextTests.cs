namespace Jorrk.Flagon.Testing.Tests;

public class AmbientContextTests
{
    [Fact]
    public void The_ambient_context_is_used_when_the_caller_passes_none()
    {
        using var flags = TestFlagon.Create();
        flags.Flags.EnableFor("beta", "user-1");

        flags.Context = new EvaluationContext("user-1");

        Assert.True(flags.IsEnabled("beta"));
        Assert.True(flags.GetAllDetails().Flags.Single(f => f.FlagKey == "beta").Value.GetBoolean());
    }

    [Fact]
    public void An_explicit_context_wins_over_the_ambient_one()
    {
        using var flags = TestFlagon.Create();
        flags.Flags.EnableFor("beta", "user-1");
        flags.Context = new EvaluationContext("user-1");

        Assert.False(flags.IsEnabled("beta", context: new EvaluationContext("user-2")));
    }

    [Fact]
    public void There_is_no_ambient_context_until_one_is_set_and_it_can_be_cleared()
    {
        using var flags = TestFlagon.Create();
        flags.Flags.EnableFor("beta", "user-1");
        Assert.Null(flags.Context);
        Assert.False(flags.IsEnabled("beta"));

        flags.Context = new EvaluationContext("user-1");
        flags.Context = null;

        Assert.False(flags.IsEnabled("beta"));
    }
}
