using System.Net;
using System.Net.Http.Json;
using Flagon.Tests.Support;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Flagon.AspNetCore.Tests.Management;

public class ManagementApiAuthorizationTests
{
    private static readonly string[] ReadPaths =
    [
        "/flagon/api/flags",
        "/flagon/api/flags/new-checkout",
        "/flagon/api/flags/new-checkout/audit",
    ];

    private static async Task<FlagonTestHost> StartWithFlagAsync(Action<FlagonTestHostOptions> configure)
    {
        var host = await FlagonTestHost.StartAsync(configure);
        await host.Admin.CreateAsync(new CreateFlagRequest("new-checkout"));
        return host;
    }

    [Fact]
    public async Task Without_an_authorization_policy_the_api_serves_anonymous_callers_in_development()
    {
        await using var host = await StartWithFlagAsync(o =>
        {
            o.EnvironmentName = Environments.Development;
            o.Endpoints = app => app.MapFlagonApi();
        });

        foreach (var path in ReadPaths)
        {
            Assert.Equal(HttpStatusCode.OK, (await host.CreateClient().GetAsync(path)).StatusCode);
        }
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    public async Task Without_an_authorization_policy_the_api_is_not_served_outside_development(string environment)
    {
        await using var host = await StartWithFlagAsync(o =>
        {
            o.EnvironmentName = environment;
            o.Endpoints = app => app.MapFlagonApi();
        });
        var client = host.CreateClient("alice");

        foreach (var path in ReadPaths)
        {
            Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(path)).StatusCode);
        }

        var write = await client.PutAsJsonAsync("/flagon/api/flags/new-checkout/enabled", new { enabled = true, version = 1 });
        Assert.Equal(HttpStatusCode.NotFound, write.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsJsonAsync("/flagon/api/flags", new { key = "sneaky" })).StatusCode);
        Assert.False(host.Flagon.IsEnabled("new-checkout"));
        Assert.Null(await host.Admin.GetAsync("sneaky"));
    }

    [Fact]
    public async Task Allowing_anonymous_access_does_not_count_as_configuring_authorization()
    {
        await using var host = await StartWithFlagAsync(o =>
        {
            o.EnvironmentName = Environments.Production;
            o.Endpoints = app => app.MapFlagonApi().AllowAnonymous();
        });

        Assert.Equal(HttpStatusCode.NotFound, (await host.CreateClient().GetAsync("/flagon/api/flags")).StatusCode);
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Development")]
    public async Task With_an_authorization_policy_the_api_enforces_it_in_any_environment(string environment)
    {
        await using var host = await StartWithFlagAsync(o =>
        {
            o.EnvironmentName = environment;
            o.Services = s => s.AddAuthorization(a => a.AddPolicy("flag-admins", p => p.RequireRole("flag-admin")));
            o.Endpoints = app => app.MapFlagonApi().RequireAuthorization("flag-admins");
        });

        foreach (var path in ReadPaths)
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await host.CreateClient().GetAsync(path)).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await host.CreateClient("bob").GetAsync(path)).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await host.CreateClient("alice", "flag-admin").GetAsync(path)).StatusCode);
        }

        var bobToggle = await host.CreateClient("bob").PutAsJsonAsync("/flagon/api/flags/new-checkout/enabled", new { enabled = true, version = 1 });
        Assert.Equal(HttpStatusCode.Forbidden, bobToggle.StatusCode);
        Assert.False(host.Flagon.IsEnabled("new-checkout"));

        var version = (await host.Admin.GetAsync("new-checkout"))!.Version;
        var aliceToggle = await host.CreateClient("alice", "flag-admin").PutAsJsonAsync("/flagon/api/flags/new-checkout/enabled", new { enabled = true, version });
        Assert.Equal(HttpStatusCode.OK, aliceToggle.StatusCode);
        Assert.True(host.Flagon.IsEnabled("new-checkout"));
    }

    [Fact]
    public async Task Requiring_any_authenticated_user_also_unlocks_the_api_in_production()
    {
        await using var host = await StartWithFlagAsync(o =>
        {
            o.EnvironmentName = Environments.Production;
            o.Endpoints = app => app.MapFlagonApi().RequireAuthorization();
        });

        Assert.Equal(HttpStatusCode.Unauthorized, (await host.CreateClient().GetAsync("/flagon/api/flags")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await host.CreateClient("alice").GetAsync("/flagon/api/flags")).StatusCode);
    }

    [Fact]
    public async Task The_http_context_actor_accessor_attributes_admin_writes_in_your_own_endpoints_to_the_signed_in_user()
    {
        await using var host = await FlagonTestHost.StartAsync(o =>
        {
            o.Services = s => s.AddFlagonHttpContextActor();
            o.Endpoints = app => app.MapPost("/my-tooling/create", async (IFlagonAdmin admin) =>
            {
                await admin.CreateAsync(new CreateFlagRequest("from-my-tooling"));
                return Results.Ok();
            });
        });

        await host.CreateClient("carol").PostAsync("/my-tooling/create", null);

        Assert.Equal("carol", Assert.Single(await host.Admin.GetAuditHistoryAsync("from-my-tooling")).Actor);
    }

    [Fact]
    public async Task An_explicit_actor_scope_wins_over_the_http_user_and_no_request_means_no_actor()
    {
        await using var host = await FlagonTestHost.StartAsync(o =>
        {
            o.Services = s => s.AddFlagonHttpContextActor();
            o.Endpoints = app => app.MapPost("/my-tooling/create", async (IFlagonAdmin admin) =>
            {
                using (FlagonActor.Use("nightly-job"))
                {
                    await admin.CreateAsync(new CreateFlagRequest("scoped"));
                }

                return Results.Ok();
            });
        });

        await host.CreateClient("carol").PostAsync("/my-tooling/create", null);
        await host.Admin.CreateAsync(new CreateFlagRequest("outside-a-request"));

        Assert.Equal("nightly-job", Assert.Single(await host.Admin.GetAuditHistoryAsync("scoped")).Actor);
        Assert.Null(Assert.Single(await host.Admin.GetAuditHistoryAsync("outside-a-request")).Actor);
    }
}
