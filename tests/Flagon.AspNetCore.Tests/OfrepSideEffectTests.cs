using System.Net;
using System.Net.Http.Json;
using Flagon.Tests.Support;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

namespace Flagon.AspNetCore.Tests;

/// <summary>
/// OFREP callers are remote (often anonymous) clients, not this app's code: their evaluations must not create flags
/// or count as this app's usage.
/// </summary>
public class OfrepSideEffectTests
{
    private static readonly object EmptyContext = new { context = new { } };

    private static Task<FlagonTestHost> StartAsync() =>
        FlagonTestHost.StartAsync(o => o.Endpoints = app => app.MapFlagonOfrep());

    [Fact]
    public async Task A_missing_flag_requested_over_ofrep_is_not_auto_created()
    {
        await using var host = await StartAsync();

        var response = await host.CreateClient().PostAsJsonAsync("/ofrep/v1/evaluate/flags/made-up-by-a-client", EmptyContext);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        // The app's own evaluation of an unknown key is auto-created; once that has happened, any queued OFREP key would have been too.
        host.Flagon.IsEnabled("used-in-code");
        await FlagonTestHost.EventuallyAsync(async () => await host.Admin.GetAsync("used-in-code") is not null, "the app's own flag is auto-created");
        await host.StopAsync();

        Assert.Null(await host.Admin.GetAsync("made-up-by-a-client"));
    }

    [Fact]
    public async Task Evaluations_over_ofrep_are_not_recorded_as_the_apps_usage()
    {
        await using var host = await StartAsync();
        await host.Admin.CreateAsync(new CreateFlagRequest("new-checkout", Enabled: true));

        await host.CreateClient().PostAsJsonAsync("/ofrep/v1/evaluate/flags/new-checkout", EmptyContext);
        await host.CreateClient().PostAsJsonAsync("/ofrep/v1/evaluate/flags/not-a-flag", EmptyContext);
        await host.StopAsync(); // flushes usage

        Assert.Empty((await host.Admin.GetUsageAsync("new-checkout")).Applications);
        Assert.Empty(await host.Admin.ListUsageAsync());
    }

    [Fact]
    public async Task Ofrep_evaluates_with_the_request_body_context_only_never_the_ambient_one()
    {
        var accessor = new RecordingEvaluationContextAccessor(new EvaluationContext("server-side-user"));
        await using var host = await FlagonTestHost.StartAsync(o =>
        {
            o.Endpoints = app => app.MapFlagonOfrep();
            o.Services = s => s.AddSingleton<IEvaluationContextAccessor>(accessor);
        });
        await host.Admin.CreateAsync(new CreateFlagRequest("new-checkout", Enabled: true));
        var client = host.CreateClient("alice");

        await client.PostAsJsonAsync("/ofrep/v1/evaluate/flags/new-checkout", EmptyContext);
        await client.PostAsync("/ofrep/v1/evaluate/flags/new-checkout", null);
        await client.PostAsJsonAsync("/ofrep/v1/evaluate/flags", EmptyContext);

        Assert.Equal(0, accessor.Reads);
    }
}
