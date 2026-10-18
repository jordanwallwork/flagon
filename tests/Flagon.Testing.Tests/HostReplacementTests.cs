using System.Net.Http.Json;
using System.Text.Json;
using Flagon.Storage;
using Flagon.Tests.Support;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

namespace Flagon.Testing.Tests;

public class HostReplacementTests
{
    private static readonly object EmptyContext = new { context = new { } };

    /// <summary>
    /// An app that stores its flags in SQL Server (a server that doesn't exist), with <c>AddFlagonTesting()</c> applied
    /// after its own registrations, as <c>ConfigureTestServices</c> does.
    /// </summary>
    private static Task<FlagonTestHost> StartAsync(
        Action<FlagonOptions>? flagon = null,
        Action<IServiceCollection>? services = null,
        Action<FlagonTestingSwitches>? testing = null) =>
        FlagonTestHost.StartAsync(o =>
        {
            o.Flagon = f =>
            {
                f.UseSqlServerStorage("Server=nowhere;Connect Timeout=1");
                flagon?.Invoke(f);
            };
            o.Services = s =>
            {
                services?.Invoke(s);
                s.AddFlagonTesting(testing);
            };
            o.Endpoints = app =>
            {
                app.MapGet("/checkout", (IFlagon flagon) => flagon.IsEnabled("new-checkout") ? "new" : "old");
                app.MapFlagonOfrep();
                app.MapFlagonApi();
            };
        });

    [Fact]
    public async Task A_host_configured_for_SQL_Server_starts_and_serves_test_flags_with_no_database()
    {
        await using var host = await StartAsync();
        var client = host.CreateClient();

        host.Services.GetRequiredService<TestFlags>().SetFlag("new-checkout", true);

        Assert.Equal("new", await client.GetStringAsync("/checkout"));
    }

    [Fact]
    public async Task OFREP_single_evaluation_sees_a_test_write_on_the_next_request()
    {
        await using var host = await StartAsync();
        var client = host.CreateClient();
        var flags = host.Services.GetRequiredService<TestFlags>();

        flags.SetFlag("new-checkout", true);
        var on = await (await client.PostAsJsonAsync("/ofrep/v1/evaluate/flags/new-checkout", EmptyContext))
            .Content.ReadFromJsonAsync<JsonElement>();
        flags.SetFlag("new-checkout", false);
        var off = await (await client.PostAsJsonAsync("/ofrep/v1/evaluate/flags/new-checkout", EmptyContext))
            .Content.ReadFromJsonAsync<JsonElement>();

        Assert.True(on.GetProperty("value").GetBoolean());
        Assert.False(off.GetProperty("value").GetBoolean());
    }

    [Fact]
    public async Task OFREP_bulk_evaluation_sees_a_test_write_on_the_next_request()
    {
        await using var host = await StartAsync();
        var client = host.CreateClient();

        host.Services.GetRequiredService<TestFlags>().SetFlag("new-checkout", true).SetFlag("dark-mode", false);
        var response = await client.PostAsJsonAsync("/ofrep/v1/evaluate/flags", EmptyContext);

        var flags = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("flags").EnumerateArray()
            .ToDictionary(f => f.GetProperty("key").GetString()!, f => f.GetProperty("value").GetBoolean());
        Assert.Equal(new Dictionary<string, bool> { ["dark-mode"] = false, ["new-checkout"] = true }, flags);
    }

    [Fact]
    public async Task The_management_API_sees_a_test_write_on_the_next_request()
    {
        await using var host = await StartAsync();
        var client = host.CreateClient();

        host.Services.GetRequiredService<TestFlags>().SetFlag("new-checkout", true);
        var flag = await client.GetFromJsonAsync<JsonElement>("/flagon/api/flags/new-checkout");

        Assert.True(flag.GetProperty("enabled").GetBoolean());
    }

    [Fact]
    public async Task The_apps_ambient_context_drives_targeting_through_a_real_request()
    {
        await using var host = await StartAsync(
            flagon: f => f.Define("new-checkout", false, targeting: """{"if":[{"in":[{"var":"targetingKey"},["alice"]]},"on","off"]}"""),
            services: s => s.AddFlagonAmbientContext());

        Assert.Equal("new", await host.CreateClient("alice").GetStringAsync("/checkout"));
        Assert.Equal("old", await host.CreateClient("bob").GetStringAsync("/checkout"));
    }

    [Fact]
    public async Task Flags_defined_in_code_are_seeded()
    {
        await using var host = await StartAsync(flagon: f => f.Define("new-checkout", true));

        Assert.Equal("new", await host.CreateClient().GetStringAsync("/checkout"));
    }

    [Fact]
    public async Task Flags_defined_in_code_are_absent_when_seeding_is_off()
    {
        await using var host = await StartAsync(flagon: f => f.Define("new-checkout", true), testing: t => t.SeedDefinitions = false);

        Assert.Equal(ErrorCode.FlagNotFound, host.Flagon.GetBooleanDetails("new-checkout").ErrorCode);
        Assert.Null(await host.Admin.GetAsync("new-checkout"));
    }

    [Fact]
    public async Task The_apps_application_name_is_kept()
    {
        await using var host = await StartAsync(flagon: f => f.ApplicationName = "checkout-api");

        await host.Admin.CreateAsync(new CreateFlagRequest("new-checkout"));

        Assert.Equal("checkout-api", (await host.Admin.GetAuditHistoryAsync("new-checkout")).Single().ApplicationName);
    }

    [Fact]
    public async Task An_unknown_key_is_not_auto_created_by_default_even_when_the_app_turns_it_on()
    {
        await using var host = await StartAsync(flagon: f => f.AutoCreate = true);

        await host.CreateClient().GetStringAsync("/checkout");
        await host.StopAsync(); // Drains the auto-create queue.

        Assert.Null(await host.Admin.GetAsync("new-checkout"));
    }

    [Fact]
    public async Task An_unknown_key_is_auto_created_when_switched_on()
    {
        await using var host = await StartAsync(testing: t => t.AutoCreate = true);

        await host.CreateClient().GetStringAsync("/checkout");
        await host.StopAsync(); // Drains the auto-create queue.

        Assert.Equal(FlagSource.Auto, (await host.Admin.GetAsync("new-checkout"))!.Document.Metadata.Source);
    }

    [Fact]
    public async Task Usage_is_not_recorded_by_default_even_when_the_app_turns_it_on()
    {
        await using var host = await StartAsync(flagon: f => f.UsageTracking = true);
        host.Services.GetRequiredService<TestFlags>().SetFlag("new-checkout", true);

        await host.CreateClient().GetStringAsync("/checkout");
        await host.StopAsync(); // Flushes recorded usage.

        Assert.Empty((await host.Admin.GetUsageAsync("new-checkout")).Applications);
    }

    [Fact]
    public async Task Usage_is_recorded_and_flushed_on_stop_when_switched_on()
    {
        await using var host = await StartAsync(flagon: f => f.UsageTracking = false, testing: t => t.UsageTracking = true);
        host.Services.GetRequiredService<TestFlags>().SetFlag("new-checkout", true);

        await host.CreateClient().GetStringAsync("/checkout");
        await host.StopAsync(); // Flushes recorded usage.

        Assert.NotNull((await host.Admin.GetUsageAsync("new-checkout")).LastEvaluatedAt);
    }

    [Fact]
    public void Without_AddFlagon_it_throws_naming_the_fix()
    {
        var services = new ServiceCollection();

        var error = Assert.Throws<InvalidOperationException>(() => services.AddFlagonTesting());

        Assert.Contains("AddFlagon", error.Message);
    }

    [Fact]
    public async Task Calling_it_twice_keeps_one_store_and_one_TestFlags()
    {
        await using var host = await StartAsync(services: s => s.AddFlagonTesting());

        Assert.Single(host.Services.GetServices<IFlagStore>());
        Assert.Single(host.Services.GetServices<TestFlags>());
        host.Services.GetRequiredService<TestFlags>().SetFlag("new-checkout", true);
        Assert.Equal("new", await host.CreateClient().GetStringAsync("/checkout"));
    }

    [Fact]
    public void Calling_it_twice_applies_the_later_switches()
    {
        var services = new ServiceCollection().AddFlagon();

        services.AddFlagonTesting().AddFlagonTesting(t => t.AutoCreate = true);

        Assert.True(services.BuildServiceProvider().GetRequiredService<FlagonTestingSwitches>().AutoCreate);
    }

    [Fact]
    public async Task Reset_is_seen_on_the_next_request_and_clears_the_evaluation_log()
    {
        await using var host = await StartAsync(flagon: f => f.Define("new-checkout", false));
        var client = host.CreateClient();
        var flags = host.Services.GetRequiredService<TestFlags>();
        flags.SetFlag("new-checkout", true).SetFlag("dark-mode", true);
        Assert.Equal("new", await client.GetStringAsync("/checkout"));

        flags.Reset();

        Assert.Empty(flags.Evaluations);
        Assert.Equal("old", await client.GetStringAsync("/checkout"));
        var darkMode = await client.PostAsJsonAsync("/ofrep/v1/evaluate/flags/dark-mode", EmptyContext);
        Assert.Equal(System.Net.HttpStatusCode.NotFound, darkMode.StatusCode);
    }

    [Fact]
    public async Task An_OFREP_single_evaluation_is_recorded_as_remote_and_untyped_with_the_context_sent()
    {
        await using var host = await StartAsync();
        var flags = host.Services.GetRequiredService<TestFlags>();
        flags.SetFlag("new-checkout", true);

        await host.CreateClient().PostAsJsonAsync("/ofrep/v1/evaluate/flags/new-checkout",
            new { context = new { targetingKey = "user-42" } });

        var evaluation = Assert.Single(flags.Evaluations);
        Assert.Equal("new-checkout", evaluation.FlagKey);
        Assert.Null(evaluation.RequestedType);
        Assert.Equal(JsonValueKind.True, evaluation.Value.ValueKind);
        Assert.Equal("on", evaluation.Variant);
        Assert.Equal(EvaluationReason.Static, evaluation.Reason);
        Assert.Equal("user-42", evaluation.Context.TargetingKey);
        Assert.Equal(EvaluationSource.Remote, evaluation.Source);
        Assert.Equal(FlagonTestHost.Epoch, evaluation.At);
    }

    [Fact]
    public async Task An_OFREP_bulk_evaluation_records_each_flag_as_remote()
    {
        await using var host = await StartAsync();
        var flags = host.Services.GetRequiredService<TestFlags>();
        flags.SetFlag("dark-mode", false).SetFlag("new-checkout", true);

        await host.CreateClient().PostAsJsonAsync("/ofrep/v1/evaluate/flags", new { context = new { targetingKey = "user-42" } });

        Assert.Equal(["dark-mode", "new-checkout"], flags.Evaluations.Select(e => e.FlagKey));
        Assert.All(flags.Evaluations, e =>
        {
            Assert.Null(e.RequestedType);
            Assert.Equal(EvaluationSource.Remote, e.Source);
            Assert.Equal("user-42", e.Context.TargetingKey);
            Assert.Equal(EvaluationReason.Static, e.Reason);
        });
    }

    [Fact]
    public async Task An_in_process_evaluation_records_the_apps_ambient_context_and_targeting_reason()
    {
        await using var host = await StartAsync(
            flagon: f => f.Define("new-checkout", false, targeting: """{"if":[{"in":[{"var":"targetingKey"},["alice"]]},"on","off"]}"""),
            services: s => s.AddFlagonAmbientContext());
        var flags = host.Services.GetRequiredService<TestFlags>();

        await host.CreateClient("alice").GetStringAsync("/checkout");

        var evaluation = Assert.Single(flags.Evaluations.For("new-checkout"));
        Assert.Equal(FlagType.Boolean, evaluation.RequestedType);
        Assert.Equal(EvaluationSource.InProcess, evaluation.Source);
        Assert.Equal("alice", evaluation.Context.TargetingKey);
        Assert.Equal(EvaluationReason.TargetingMatch, evaluation.Reason);
        Assert.Equal("on", evaluation.Variant);
    }

    [Fact]
    public async Task A_management_draft_evaluation_records_nothing()
    {
        await using var host = await StartAsync();
        var flags = host.Services.GetRequiredService<TestFlags>();
        flags.SetFlag("new-checkout", true);

        var stored = await host.CreateClient().PostAsJsonAsync("/flagon/api/flags/new-checkout/evaluate", EmptyContext);
        var draft = await host.CreateClient().PostAsJsonAsync("/flagon/api/flags/new-checkout/evaluate", new
        {
            context = new { },
            document = new { state = "ENABLED", variants = new { on = true, off = false }, defaultVariant = "off" },
        });

        stored.EnsureSuccessStatusCode();
        draft.EnsureSuccessStatusCode();
        Assert.Empty(flags.Evaluations);
    }
}
