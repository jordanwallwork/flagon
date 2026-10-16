using Flagon.Storage;
using Flagon.Tests.Support;
using Microsoft.Extensions.Logging;

namespace Flagon.Tests;

public class SeedingTests
{
    [Fact]
    public async Task A_flag_defined_in_code_exists_at_startup_with_its_default_description_tags_and_source_code()
    {
        await using var host = await FlagonTestHost.StartAsync(o => o.Flagon = f =>
            f.Define("new-checkout", true, "The redesigned checkout", ["checkout", "web"]));

        var details = host.Flagon.GetBooleanDetails("new-checkout", defaultValue: false);
        Assert.True(details.Value);
        Assert.Equal(EvaluationReason.Static, details.Reason);

        var flag = await host.Admin.GetAsync("new-checkout");
        Assert.NotNull(flag);
        Assert.Equal("The redesigned checkout", flag.Document.Metadata.Description);
        Assert.Equal(["checkout", "web"], flag.Document.Metadata.Tags);
        Assert.Equal(FlagSource.Code, flag.Document.Metadata.Source);
        Assert.Equal(FlagonTestHost.Epoch, flag.Document.Metadata.CreatedAt);
    }

    [Fact]
    public async Task Redeploying_with_changed_metadata_updates_description_and_tags_but_never_the_state_an_admin_set()
    {
        var store = new InMemoryFlagStore();
        await using (var firstDeploy = await FlagonTestHost.StartAsync(o =>
        {
            o.Store = store;
            o.Flagon = f => f.Define("new-checkout", true, "Old description", ["checkout"]);
        }))
        {
            var flag = await firstDeploy.Admin.GetAsync("new-checkout");
            await firstDeploy.Admin.SetEnabledAsync("new-checkout", false, flag!.Version);
        }

        await using var secondDeploy = await FlagonTestHost.StartAsync(o =>
        {
            o.Store = store;
            o.Flagon = f => f.Define("new-checkout", true, "New description", ["checkout", "beta"]);
        });

        Assert.False(secondDeploy.Flagon.IsEnabled("new-checkout", defaultValue: true));
        var updated = await secondDeploy.Admin.GetAsync("new-checkout");
        Assert.Equal("New description", updated!.Document.Metadata.Description);
        Assert.Equal(["checkout", "beta"], updated.Document.Metadata.Tags);
        Assert.Equal(FlagSource.Code, updated.Document.Metadata.Source);
        Assert.Equal(FlagonTestHost.Epoch, updated.Document.Metadata.CreatedAt);
    }

    [Fact]
    public async Task A_flag_created_elsewhere_and_later_defined_in_code_becomes_a_code_flag_keeping_its_state()
    {
        var store = new InMemoryFlagStore();
        await store.TryCreateAsync("legacy", FlagDocument.Boolean(true, new FlagMetadata(Source: FlagSource.Auto)));

        await using var host = await FlagonTestHost.StartAsync(o =>
        {
            o.Store = store;
            o.Flagon = f => f.Define("legacy", false, "Now documented");
        });

        Assert.True(host.Flagon.IsEnabled("legacy"));
        var flag = await host.Admin.GetAsync("legacy");
        Assert.Equal(FlagSource.Code, flag!.Document.Metadata.Source);
        Assert.Equal("Now documented", flag.Document.Metadata.Description);
    }

    [Fact]
    public async Task Redeploying_an_unchanged_definition_does_not_write_to_the_store()
    {
        var store = new InMemoryFlagStore();
        void Configure(FlagonTestHostOptions o)
        {
            o.Store = store;
            o.Flagon = f => f.Define("stable", true, "Same", ["a"]);
        }

        await using (await FlagonTestHost.StartAsync(Configure))
        {
        }

        var versionAfterFirstDeploy = await store.GetChangeVersionAsync();
        await using var secondDeploy = await FlagonTestHost.StartAsync(Configure);

        Assert.Equal(versionAfterFirstDeploy, await store.GetChangeVersionAsync());
    }

    [Fact]
    public async Task An_archived_flag_still_defined_in_code_is_neither_revived_nor_changed()
    {
        var store = new InMemoryFlagStore();
        var created = await store.TryCreateAsync("retired", FlagDocument.Boolean(true, new FlagMetadata(Description: "Old")));
        await store.ArchiveAsync("retired", created!.Version);
        var versionBeforeDeploy = await store.GetChangeVersionAsync();

        await using var host = await FlagonTestHost.StartAsync(o =>
        {
            o.Store = store;
            o.Flagon = f => f.Define("retired", true, "New");
        });

        var flag = await host.Admin.GetAsync("retired");
        Assert.True(flag!.Archived);
        Assert.Equal("Old", flag.Document.Metadata.Description);
        Assert.Equal(versionBeforeDeploy, await store.GetChangeVersionAsync());
    }

    [Fact]
    public async Task When_the_store_is_unavailable_at_startup_seeding_is_retried_until_it_succeeds()
    {
        var store = OutageFlagStore.Wrap(isDown: true);

        await using var host = await FlagonTestHost.StartAsync(o =>
        {
            o.Store = store.Store;
            o.Flagon = f => f.Define("new-checkout", true);
        });

        Assert.False(host.Flagon.IsEnabled("new-checkout"));
        Assert.Contains(host.Logs.Flagon(LogLevel.Warning), l => l.EventId.Id == 1104);

        await host.AdvanceToNextRefreshAsync();
        Assert.Null(await store.Inner.GetAsync("new-checkout"));

        store.IsDown = false;
        await host.AdvanceToNextRefreshAsync();

        Assert.True(host.Flagon.IsEnabled("new-checkout"));
        Assert.Equal(FlagSource.Code, (await store.Inner.GetAsync("new-checkout"))!.Document.Metadata.Source);
    }

    [Fact]
    public async Task With_fail_fast_enabled_a_seeding_failure_stops_the_app_from_starting()
    {
        var store = OutageFlagStore.Wrap(isDown: true);

        var ex = await Assert.ThrowsAsync<FlagonException>(() => FlagonTestHost.StartAsync(o =>
        {
            o.Store = store.Store;
            o.Flagon = f =>
            {
                f.FailFastOnStartup = true;
                f.Define("new-checkout", true);
            };
        }));

        Assert.IsType<StoreOutageException>(ex.InnerException);
    }

    [Fact]
    public async Task Seeding_writes_are_audited_as_made_by_the_seeding_application()
    {
        var store = new InMemoryFlagStore();
        await using (await FlagonTestHost.StartAsync(o =>
        {
            o.Store = store;
            o.ApplicationName = "Checkout";
            o.Flagon = f => f.Define("new-checkout", true, "v1");
        }))
        {
        }

        await using var host = await FlagonTestHost.StartAsync(o =>
        {
            o.Store = store;
            o.ApplicationName = "Checkout";
            o.Flagon = f => f.Define("new-checkout", true, "v2");
        });

        var history = await host.Admin.GetAuditHistoryAsync("new-checkout");
        Assert.Equal([FlagAuditAction.Updated, FlagAuditAction.Created], history.Select(e => e.Action));
        Assert.All(history, e => Assert.Equal("Checkout", e.ApplicationName));
    }

    [Fact]
    public void Defining_an_invalid_key_a_tag_with_a_comma_or_the_same_key_twice_is_rejected()
    {
        var options = new FlagonOptions().Define("ok");

        Assert.Throws<FlagValidationException>(() => options.Define("not a key!"));
        Assert.Throws<FlagValidationException>(() => options.Define("tagged", tags: ["a,b"]));
        Assert.Throws<ArgumentException>(() => options.Define("ok"));
    }
}
