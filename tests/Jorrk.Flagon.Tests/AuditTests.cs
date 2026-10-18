using Jorrk.Flagon.Storage;
using Jorrk.Flagon.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace Jorrk.Flagon.Tests;

public class AuditTests
{
    [Fact]
    public async Task Creating_a_flag_records_who_created_it_from_which_app_and_when()
    {
        await using var host = await FlagonTestHost.StartAsync(o => o.ApplicationName = "Checkout.Api");

        FlagRecord created;
        using (FlagonActor.Use("alice"))
        {
            created = await host.Admin.CreateAsync(new CreateFlagRequest("new-checkout", Enabled: true, "New checkout"));
        }

        var entry = Assert.Single(await host.Admin.GetAuditHistoryAsync("new-checkout"));
        Assert.Equal(FlagAuditAction.Created, entry.Action);
        Assert.Equal("alice", entry.Actor);
        Assert.Equal("Checkout.Api", entry.ApplicationName);
        Assert.Equal(FlagonTestHost.Epoch, entry.Timestamp);
        Assert.Equal(created.Version, entry.Version);
        Assert.Null(entry.Before);
        Assert.Equal(created.Document, entry.After);
    }

    [Fact]
    public async Task Created_flags_are_stamped_with_created_and_updated_by()
    {
        await using var host = await FlagonTestHost.StartAsync();

        using var _ = FlagonActor.Use("alice");
        var created = await host.Admin.CreateAsync(new CreateFlagRequest("beta"));

        Assert.Equal("alice", created.Document.Metadata.CreatedBy);
        Assert.Equal(FlagonTestHost.Epoch, created.Document.Metadata.CreatedAt);
        Assert.Equal("alice", created.Document.Metadata.UpdatedBy);
        Assert.Equal(FlagonTestHost.Epoch, created.Document.Metadata.UpdatedAt);
    }

    [Fact]
    public async Task Toggling_records_before_and_after_and_stamps_updated_by_without_touching_created_by()
    {
        await using var host = await FlagonTestHost.StartAsync(o => o.ApplicationName = "Admin.Portal");
        FlagRecord created;
        using (FlagonActor.Use("alice"))
        {
            created = await host.Admin.CreateAsync(new CreateFlagRequest("beta", Enabled: false));
        }

        host.Time.Advance(TimeSpan.FromMinutes(5));
        FlagRecord toggled;
        using (FlagonActor.Use("bob"))
        {
            toggled = await host.Admin.SetEnabledAsync("beta", true, created.Version);
        }

        Assert.Equal("alice", toggled.Document.Metadata.CreatedBy);
        Assert.Equal(FlagonTestHost.Epoch, toggled.Document.Metadata.CreatedAt);
        Assert.Equal("bob", toggled.Document.Metadata.UpdatedBy);
        Assert.Equal(FlagonTestHost.Epoch.AddMinutes(5), toggled.Document.Metadata.UpdatedAt);

        var latest = (await host.Admin.GetAuditHistoryAsync("beta"))[0];
        Assert.Equal(FlagAuditAction.Updated, latest.Action);
        Assert.Equal("bob", latest.Actor);
        Assert.Equal("Admin.Portal", latest.ApplicationName);
        Assert.Equal(FlagonTestHost.Epoch.AddMinutes(5), latest.Timestamp);
        Assert.Equal("off", latest.Before!.DefaultVariant);
        Assert.Equal(created.Document, latest.Before);
        Assert.Equal("on", latest.After!.DefaultVariant);
        Assert.Equal(toggled.Document, latest.After);
    }

    [Fact]
    public async Task Audit_history_is_returned_newest_first()
    {
        await using var host = await FlagonTestHost.StartAsync();
        var flag = await host.Admin.CreateAsync(new CreateFlagRequest("beta"));
        flag = await host.Admin.SetEnabledAsync("beta", true, flag.Version);
        flag = await host.Admin.SetEnabledAsync("beta", false, flag.Version);

        var history = await host.Admin.GetAuditHistoryAsync("beta");

        Assert.Equal([FlagAuditAction.Updated, FlagAuditAction.Updated, FlagAuditAction.Created], history.Select(e => e.Action));
        Assert.Equal(["off", "on", "off"], history.Select(e => e.After!.DefaultVariant));
        Assert.Equal(flag.Version, history[0].Version);
    }

    [Fact]
    public async Task A_flag_with_no_changes_has_empty_audit_history()
    {
        await using var host = await FlagonTestHost.StartAsync();

        Assert.Empty(await host.Admin.GetAuditHistoryAsync("never-created"));
    }

    [Fact]
    public async Task A_conflicting_write_produces_no_audit_entry()
    {
        var store = new InMemoryFlagStore();
        await using var appA = await FlagonTestHost.StartAsync(o => { o.Store = store; o.ApplicationName = "AppA"; });
        await using var appB = await FlagonTestHost.StartAsync(o => { o.Store = store; o.ApplicationName = "AppB"; });
        var loadedByBoth = await appA.Admin.CreateAsync(new CreateFlagRequest("shared"));
        await appA.Admin.SetEnabledAsync("shared", true, loadedByBoth.Version);

        await Assert.ThrowsAsync<FlagVersionConflictException>(() => appB.Admin.SetEnabledAsync("shared", false, loadedByBoth.Version));

        var history = await appB.Admin.GetAuditHistoryAsync("shared");
        Assert.Equal(2, history.Count);
        Assert.All(history, e => Assert.Equal("AppA", e.ApplicationName));
    }

    [Fact]
    public async Task A_duplicate_create_produces_no_audit_entry()
    {
        await using var host = await FlagonTestHost.StartAsync();
        await host.Admin.CreateAsync(new CreateFlagRequest("dup"));

        await Assert.ThrowsAsync<FlagAlreadyExistsException>(() => host.Admin.CreateAsync(new CreateFlagRequest("dup")));

        Assert.Single(await host.Admin.GetAuditHistoryAsync("dup"));
    }

    [Fact]
    public async Task Changes_made_outside_an_actor_scope_have_no_actor()
    {
        await using var host = await FlagonTestHost.StartAsync();

        var created = await host.Admin.CreateAsync(new CreateFlagRequest("beta"));

        Assert.Null(created.Document.Metadata.CreatedBy);
        Assert.Null(Assert.Single(await host.Admin.GetAuditHistoryAsync("beta")).Actor);
    }

    [Fact]
    public async Task Actor_scopes_nest_and_restore_the_outer_actor()
    {
        await using var host = await FlagonTestHost.StartAsync();

        using (FlagonActor.Use("outer"))
        {
            using (FlagonActor.Use("inner"))
            {
                await host.Admin.CreateAsync(new CreateFlagRequest("a"));
            }

            await host.Admin.CreateAsync(new CreateFlagRequest("b"));
        }

        Assert.Equal("inner", (await host.Admin.GetAsync("a"))!.Document.Metadata.CreatedBy);
        Assert.Equal("outer", (await host.Admin.GetAsync("b"))!.Document.Metadata.CreatedBy);
        Assert.Null(FlagonActor.Current);
    }

    [Fact]
    public async Task A_custom_actor_accessor_supplies_the_actor()
    {
        await using var host = await FlagonTestHost.StartAsync(o =>
            o.Services = s => s.AddSingleton<IFlagonActorAccessor>(new FixedActor("svc-account")));

        var created = await host.Admin.CreateAsync(new CreateFlagRequest("beta"));

        Assert.Equal("svc-account", created.Document.Metadata.CreatedBy);
        Assert.Equal("svc-account", Assert.Single(await host.Admin.GetAuditHistoryAsync("beta")).Actor);
    }

    [Fact]
    public async Task The_application_name_defaults_to_the_hosts_application_name()
    {
        await using var host = await FlagonTestHost.StartAsync(o => o.ApplicationName = "Orders.Worker");

        await host.Admin.CreateAsync(new CreateFlagRequest("beta"));

        Assert.Equal("Orders.Worker", Assert.Single(await host.Admin.GetAuditHistoryAsync("beta")).ApplicationName);
    }

    [Fact]
    public async Task An_explicit_application_name_overrides_the_hosts()
    {
        await using var host = await FlagonTestHost.StartAsync(o =>
        {
            o.ApplicationName = "Orders.Worker";
            o.Flagon = f => f.ApplicationName = "orders";
        });

        await host.Admin.CreateAsync(new CreateFlagRequest("beta"));

        Assert.Equal("orders", Assert.Single(await host.Admin.GetAuditHistoryAsync("beta")).ApplicationName);
    }

    private sealed class FixedActor(string actor) : IFlagonActorAccessor
    {
        public string? Actor => actor;
    }
}
