using System.Net;
using System.Net.Http.Json;
using Flagon.Tests.Support;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Flagon.Ui.Tests;

public class UiAuthorizationTests
{
    private static readonly string[] ConsolePaths = ["/flags/", "/flags/app.js", "/flags/_api/flags", "/flags/_api/flags/new-checkout"];

    private static async Task<FlagonTestHost> StartWithFlagAsync(Action<FlagonTestHostOptions> configure)
    {
        var host = await FlagonTestHost.StartAsync(configure);
        await host.Admin.CreateAsync(new CreateFlagRequest("new-checkout"));
        return host;
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    public async Task Without_an_authorization_policy_the_console_and_its_api_are_not_served_outside_development(string environment)
    {
        await using var host = await StartWithFlagAsync(o =>
        {
            o.EnvironmentName = environment;
            o.Endpoints = app => app.MapFlagonUi("/flags");
        });
        var client = host.CreateClient("alice");

        foreach (var path in ConsolePaths.Append("/flags"))
        {
            Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(path)).StatusCode);
        }

        var write = await client.PutAsJsonAsync("/flags/_api/flags/new-checkout/enabled", new { enabled = true, version = 1 });
        Assert.Equal(HttpStatusCode.NotFound, write.StatusCode);
        Assert.False(host.Flagon.IsEnabled("new-checkout"));
    }

    [Fact]
    public async Task Allowing_anonymous_access_does_not_unlock_the_console_in_production()
    {
        await using var host = await StartWithFlagAsync(o =>
        {
            o.EnvironmentName = Environments.Production;
            o.Endpoints = app => app.MapFlagonUi("/flags").AllowAnonymous();
        });

        foreach (var path in ConsolePaths)
        {
            Assert.Equal(HttpStatusCode.NotFound, (await host.CreateClient().GetAsync(path)).StatusCode);
        }
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Development")]
    public async Task An_authorization_policy_on_the_console_protects_the_page_and_its_api_in_any_environment(string environment)
    {
        await using var host = await StartWithFlagAsync(o =>
        {
            o.EnvironmentName = environment;
            o.Services = s => s.AddAuthorization(a => a.AddPolicy("flag-admins", p => p.RequireRole("flag-admin")));
            o.Endpoints = app => app.MapFlagonUi("/flags").RequireAuthorization("flag-admins");
        });

        foreach (var path in ConsolePaths)
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await host.CreateClient().GetAsync(path)).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await host.CreateClient("bob").GetAsync(path)).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await host.CreateClient("alice", "flag-admin").GetAsync(path)).StatusCode);
        }

        var bobToggle = await host.CreateClient("bob").PutAsJsonAsync("/flags/_api/flags/new-checkout/enabled", new { enabled = true, version = 1 });
        Assert.Equal(HttpStatusCode.Forbidden, bobToggle.StatusCode);
        Assert.False(host.Flagon.IsEnabled("new-checkout"));
    }
}
