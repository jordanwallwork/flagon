using Flagon.Storage;
using Flagon.Tests.Support;

namespace Flagon.Tests;

public class AdminTests
{
    [Fact]
    public async Task An_admin_can_create_a_boolean_flag_with_description_tags_and_initial_state()
    {
        await using var host = await FlagonTestHost.StartAsync();

        var created = await host.Admin.CreateAsync(new CreateFlagRequest("new-checkout", Enabled: true, "New checkout flow", ["checkout", "web"]));

        var flag = await host.Admin.GetAsync("new-checkout");
        Assert.Equal(created, flag);
        Assert.Equal(FlagState.Enabled, flag!.Document.State);
        Assert.Equal("on", flag.Document.DefaultVariant);
        Assert.Equal("New checkout flow", flag.Document.Metadata.Description);
        Assert.Equal(["checkout", "web"], flag.Document.Metadata.Tags);
        Assert.Equal(FlagSource.Manual, flag.Document.Metadata.Source);
        Assert.Equal(FlagonTestHost.Epoch, flag.Document.Metadata.CreatedAt);
        Assert.Equal(FlagonTestHost.Epoch, flag.Document.Metadata.UpdatedAt);
    }

    [Fact]
    public async Task Created_flags_are_listed()
    {
        await using var host = await FlagonTestHost.StartAsync();
        await host.Admin.CreateAsync(new CreateFlagRequest("a"));
        await host.Admin.CreateAsync(new CreateFlagRequest("b"));

        var keys = (await host.Admin.ListAsync()).Select(f => f.Key).Order(StringComparer.Ordinal);

        Assert.Equal(["a", "b"], keys);
    }

    [Fact]
    public async Task An_admin_can_toggle_a_flag_on_and_off_and_evaluation_follows_immediately()
    {
        await using var host = await FlagonTestHost.StartAsync();
        var flag = await host.Admin.CreateAsync(new CreateFlagRequest("beta", Enabled: false));
        Assert.False(host.Flagon.IsEnabled("beta", defaultValue: true));

        host.Time.Advance(TimeSpan.FromMinutes(5));
        flag = await host.Admin.SetEnabledAsync("beta", true, flag.Version);
        Assert.True(host.Flagon.IsEnabled("beta"));
        Assert.Equal("on", host.Flagon.GetBooleanDetails("beta").Variant);
        Assert.Equal(FlagonTestHost.Epoch, flag.Document.Metadata.CreatedAt);
        Assert.Equal(FlagonTestHost.Epoch.AddMinutes(5), flag.Document.Metadata.UpdatedAt);

        flag = await host.Admin.SetEnabledAsync("beta", false, flag.Version);
        Assert.False(host.Flagon.IsEnabled("beta", defaultValue: true));
        Assert.Equal("off", host.Flagon.GetBooleanDetails("beta").Variant);
    }

    [Fact]
    public async Task Toggling_preserves_description_and_tags()
    {
        await using var host = await FlagonTestHost.StartAsync();
        var flag = await host.Admin.CreateAsync(new CreateFlagRequest("beta", false, "Beta programme", ["growth"]));

        flag = await host.Admin.SetEnabledAsync("beta", true, flag.Version);

        Assert.Equal("Beta programme", flag.Document.Metadata.Description);
        Assert.Equal(["growth"], flag.Document.Metadata.Tags);
    }

    [Fact]
    public async Task A_toggle_based_on_a_stale_version_is_rejected_when_another_app_changed_the_flag_first()
    {
        var store = new InMemoryFlagStore();
        await using var appA = await FlagonTestHost.StartAsync(o => o.Store = store);
        await using var appB = await FlagonTestHost.StartAsync(o => o.Store = store);
        var loadedByBoth = await appA.Admin.CreateAsync(new CreateFlagRequest("shared", Enabled: false));

        var changedByA = await appA.Admin.SetEnabledAsync("shared", true, loadedByBoth.Version);
        var conflict = await Assert.ThrowsAsync<FlagVersionConflictException>(
            () => appB.Admin.SetEnabledAsync("shared", false, loadedByBoth.Version));

        Assert.Equal("shared", conflict.Key);
        Assert.Equal(loadedByBoth.Version, conflict.ExpectedVersion);
        Assert.Equal(changedByA.Version, conflict.CurrentVersion);
        Assert.True((await appB.Admin.GetAsync("shared"))!.Document.DefaultVariant == "on");
    }

    [Fact]
    public async Task Toggling_a_missing_flag_fails_with_not_found()
    {
        await using var host = await FlagonTestHost.StartAsync();

        var ex = await Assert.ThrowsAsync<FlagNotFoundException>(() => host.Admin.SetEnabledAsync("nope", true, 1));

        Assert.Equal("nope", ex.Key);
    }

    [Fact]
    public async Task Creating_a_flag_whose_key_already_exists_fails()
    {
        await using var host = await FlagonTestHost.StartAsync();
        await host.Admin.CreateAsync(new CreateFlagRequest("dup", Enabled: true));

        await Assert.ThrowsAsync<FlagAlreadyExistsException>(() => host.Admin.CreateAsync(new CreateFlagRequest("dup")));
        Assert.True(host.Flagon.IsEnabled("dup"));
    }

    [Fact]
    public async Task Keys_differing_only_by_case_are_different_flags()
    {
        await using var host = await FlagonTestHost.StartAsync();

        await host.Admin.CreateAsync(new CreateFlagRequest("Beta", Enabled: true));
        await host.Admin.CreateAsync(new CreateFlagRequest("beta", Enabled: false));

        Assert.True(host.Flagon.IsEnabled("Beta"));
        Assert.False(host.Flagon.IsEnabled("beta", defaultValue: true));
    }

    [Theory]
    [InlineData("a")]
    [InlineData("0")]
    [InlineData("checkout.v2_new-flow")]
    [InlineData("ABC-def.123_x")]
    public async Task Keys_matching_the_documented_pattern_are_accepted(string key)
    {
        await using var host = await FlagonTestHost.StartAsync();

        await host.Admin.CreateAsync(new CreateFlagRequest(key, Enabled: true));

        Assert.True(host.Flagon.IsEnabled(key));
    }

    [Fact]
    public async Task A_128_character_key_is_accepted_and_129_is_rejected()
    {
        await using var host = await FlagonTestHost.StartAsync();

        await host.Admin.CreateAsync(new CreateFlagRequest(new string('k', 128)));
        await Assert.ThrowsAsync<FlagValidationException>(() => host.Admin.CreateAsync(new CreateFlagRequest(new string('k', 129))));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("has space")]
    [InlineData(".leading-dot")]
    [InlineData("_leading-underscore")]
    [InlineData("-leading-dash")]
    [InlineData("slash/key")]
    [InlineData("colon:key")]
    [InlineData("trailing-newline\n")]
    [InlineData("ünïcode")]
    public async Task Keys_outside_the_documented_pattern_are_rejected(string key)
    {
        await using var host = await FlagonTestHost.StartAsync();

        await Assert.ThrowsAsync<FlagValidationException>(() => host.Admin.CreateAsync(new CreateFlagRequest(key)));
        Assert.Empty(await host.Admin.ListAsync());
    }

    [Theory]
    [InlineData("has,comma")]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData(" web")]
    [InlineData("web ")]
    [InlineData("web\t")]
    public async Task Invalid_tags_are_rejected(string tag)
    {
        await using var host = await FlagonTestHost.StartAsync();

        await Assert.ThrowsAsync<FlagValidationException>(() => host.Admin.CreateAsync(new CreateFlagRequest("tagged", Tags: [tag])));
    }
}
