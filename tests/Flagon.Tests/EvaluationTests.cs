using Flagon.Tests.Support;
using Microsoft.Extensions.Logging;

namespace Flagon.Tests;

public class EvaluationTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task An_existing_flag_returns_its_stored_value_rather_than_the_callers_default(bool stored)
    {
        await using var host = await FlagonTestHost.StartAsync();
        await host.Admin.CreateAsync(new CreateFlagRequest("new-checkout", Enabled: stored));

        Assert.Equal(stored, host.Flagon.IsEnabled("new-checkout", defaultValue: !stored));
        Assert.Equal(stored, await host.Flagon.IsEnabledAsync("new-checkout", defaultValue: !stored));
    }

    [Fact]
    public async Task Details_explain_a_served_value_with_its_variant_and_a_static_reason()
    {
        await using var host = await FlagonTestHost.StartAsync();
        await host.Admin.CreateAsync(new CreateFlagRequest("new-checkout", Enabled: true));

        var details = await host.Flagon.GetBooleanDetailsAsync("new-checkout");

        Assert.Equal(new FlagEvaluationDetails<bool>("new-checkout", true, "on", EvaluationReason.Static), details);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_missing_flag_returns_the_callers_default_with_flag_not_found(bool callerDefault)
    {
        await using var host = await FlagonTestHost.StartAsync(o => o.Flagon = f => f.AutoCreate = false);

        Assert.Equal(callerDefault, host.Flagon.IsEnabled("does-not-exist", callerDefault));
        Assert.Equal(callerDefault, await host.Flagon.IsEnabledAsync("does-not-exist", callerDefault));
        var details = host.Flagon.GetBooleanDetails("does-not-exist", callerDefault);
        Assert.Equal(callerDefault, details.Value);
        Assert.Null(details.Variant);
        Assert.Equal(EvaluationReason.Error, details.Reason);
        Assert.Equal(ErrorCode.FlagNotFound, details.ErrorCode);
    }

    [Fact]
    public async Task A_missing_flag_is_warned_about_once_per_key_not_on_every_evaluation()
    {
        await using var host = await FlagonTestHost.StartAsync();

        for (var i = 0; i < 3; i++)
        {
            host.Flagon.IsEnabled("missing-a");
            await host.Flagon.IsEnabledAsync("missing-b");
        }

        var warnings = host.Logs.Flagon(LogLevel.Warning);
        Assert.Equal(2, warnings.Count);
        Assert.Single(warnings, w => w.Message.Contains("'missing-a'", StringComparison.Ordinal));
        Assert.Single(warnings, w => w.Message.Contains("'missing-b'", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("")]
    [InlineData("has space")]
    [InlineData("-leading-dash")]
    public async Task An_invalid_key_evaluates_like_a_missing_flag(string key)
    {
        await using var host = await FlagonTestHost.StartAsync();

        var details = host.Flagon.GetBooleanDetails(key, true);

        Assert.True(details.Value);
        Assert.Equal(ErrorCode.FlagNotFound, details.ErrorCode);
    }

    [Fact]
    public async Task Keys_are_case_sensitive()
    {
        await using var host = await FlagonTestHost.StartAsync();
        await host.Admin.CreateAsync(new CreateFlagRequest("Beta", Enabled: true));

        Assert.True(host.Flagon.IsEnabled("Beta"));
        Assert.Equal(ErrorCode.FlagNotFound, host.Flagon.GetBooleanDetails("beta").ErrorCode);
    }

    [Fact]
    public async Task An_evaluation_context_is_accepted_by_every_evaluation_api()
    {
        await using var host = await FlagonTestHost.StartAsync();
        await host.Admin.CreateAsync(new CreateFlagRequest("ctx", Enabled: true));
        var context = new EvaluationContext("user-42", new Dictionary<string, object?> { ["country"] = "GB" });

        Assert.True(host.Flagon.IsEnabled("ctx", false, context));
        Assert.True(await host.Flagon.IsEnabledAsync("ctx", false, context));
        Assert.True(host.Flagon.GetBooleanDetails("ctx", false, context).Value);
        Assert.True((await host.Flagon.GetBooleanDetailsAsync("ctx", false, context)).Value);
    }
}
