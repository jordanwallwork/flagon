using System.Net.Http.Json;
using Jorrk.Flagon.Tests.Support;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Jorrk.Flagon.AspNetCore.Tests;

public class AmbientContextTests
{
    /// <summary>What a request handler sees through the ambient accessor.</summary>
    private sealed record Seen(string? TargetingKey, Dictionary<string, string?> Attributes);

    private static Task<FlagonTestHost> StartAsync(Action<IServiceCollection> services) =>
        FlagonTestHost.StartAsync(o =>
        {
            o.Services = services;
            o.Endpoints = app => app.MapGet("/ambient", (IEvaluationContextAccessor accessor) =>
            {
                var context = accessor.Current;
                return context is null
                    ? Results.NoContent()
                    : Results.Ok(new Seen(context.TargetingKey, context.Attributes.ToDictionary(a => a.Key, a => a.Value?.ToString())));
            });
        });

    [Fact]
    public async Task The_ambient_context_targets_the_signed_in_user_by_default()
    {
        await using var host = await StartAsync(s => s.AddFlagonAmbientContext());

        var seen = await host.CreateClient("alice").GetFromJsonAsync<Seen>("/ambient");

        Assert.Equal("alice", seen!.TargetingKey);
    }

    [Fact]
    public async Task The_ambient_context_of_an_anonymous_request_has_no_targeting_key()
    {
        await using var host = await StartAsync(s => s.AddFlagonAmbientContext());

        var seen = await host.CreateClient().GetFromJsonAsync<Seen>("/ambient");

        Assert.Null(seen!.TargetingKey);
    }

    [Fact]
    public async Task A_custom_resolver_builds_the_ambient_context_from_the_request()
    {
        await using var host = await StartAsync(s => s.AddFlagonAmbientContext(http => new EvaluationContext(
            http.User.Identity?.Name,
            new Dictionary<string, object?> { ["tenant"] = http.Request.Headers["X-Tenant"].ToString() })));
        var client = host.CreateClient("alice");
        client.DefaultRequestHeaders.Add("X-Tenant", "contoso");

        var seen = await client.GetFromJsonAsync<Seen>("/ambient");

        Assert.Equal("alice", seen!.TargetingKey);
        Assert.Equal("contoso", seen.Attributes["tenant"]);
    }

    [Fact]
    public async Task A_resolver_class_can_be_plugged_in()
    {
        await using var host = await StartAsync(s => s.AddFlagonAmbientContext<FixedResolver>());

        var seen = await host.CreateClient("alice").GetFromJsonAsync<Seen>("/ambient");

        Assert.Equal("fixed", seen!.TargetingKey);
    }

    [Fact]
    public async Task There_is_no_ambient_context_outside_a_request()
    {
        await using var host = await StartAsync(s => s.AddFlagonAmbientContext());

        Assert.Null(host.Services.GetRequiredService<IEvaluationContextAccessor>().Current);
    }

    [Fact]
    public async Task Without_the_aspnetcore_registration_there_is_no_ambient_context()
    {
        await using var host = await StartAsync(_ => { });

        var response = await host.CreateClient("alice").GetAsync("/ambient");

        Assert.Equal(System.Net.HttpStatusCode.NoContent, response.StatusCode);
    }

    private sealed class FixedResolver : IEvaluationContextResolver
    {
        public EvaluationContext? Resolve(HttpContext httpContext) => new("fixed");
    }
}
