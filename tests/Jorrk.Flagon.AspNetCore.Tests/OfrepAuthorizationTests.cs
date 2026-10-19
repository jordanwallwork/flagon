using System.Net;
using System.Net.Http.Json;
using Jorrk.Flagon.Tests.Support;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

namespace Jorrk.Flagon.AspNetCore.Tests;

public class OfrepAuthorizationTests
{
    private static readonly object EmptyContext = new { context = new { } };

    [Fact]
    public async Task The_ofrep_endpoints_are_public_by_default_even_in_production()
    {
        await using var host = await FlagonTestHost.StartAsync(o =>
        {
            o.EnvironmentName = "Production";
            o.Endpoints = app => app.MapFlagonOfrep();
        });
        await host.Admin.CreateAsync(new CreateFlagRequest("new-checkout", Enabled: true));
        var anonymous = host.CreateClient();

        Assert.Equal(HttpStatusCode.OK, (await anonymous.PostAsJsonAsync("/ofrep/v1/evaluate/flags/new-checkout", EmptyContext)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await anonymous.PostAsJsonAsync("/ofrep/v1/evaluate/flags", EmptyContext)).StatusCode);
    }

    [Fact]
    public async Task Requiring_authorization_rejects_anonymous_callers_and_admits_authorised_ones()
    {
        await using var host = await FlagonTestHost.StartAsync(o =>
        {
            o.Services = s => s.AddAuthorization(a => a.AddPolicy("flag-readers", p => p.RequireRole("reader")));
            o.Endpoints = app => app.MapFlagonOfrep().RequireAuthorization("flag-readers");
        });
        await host.Admin.CreateAsync(new CreateFlagRequest("new-checkout", Enabled: true));

        foreach (var path in new[] { "/ofrep/v1/evaluate/flags/new-checkout", "/ofrep/v1/evaluate/flags" })
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await host.CreateClient().PostAsJsonAsync(path, EmptyContext)).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await host.CreateClient("bob").PostAsJsonAsync(path, EmptyContext)).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await host.CreateClient("alice", "reader").PostAsJsonAsync(path, EmptyContext)).StatusCode);
        }
    }
}
