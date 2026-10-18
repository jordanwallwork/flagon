using System.Net.Http.Json;
using System.Text.Json;
using Flagon.Tests.Support;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using OpenFeature;
using OfEvaluationContext = OpenFeature.Model.EvaluationContext;

namespace Flagon.OpenFeature.Tests;

/// <summary>
/// The same targeted flag evaluated for the same context in-process (<see cref="IFlagon"/>), over OFREP (single and
/// bulk) and through the OpenFeature provider gives the same value, variant and reason.
/// </summary>
public class TargetingAgreementTests
{
    // Enterprise plans or 100+ seats always get it; everyone else is bucketed 50/50 on their targeting key.
    private const string Rule = """
        {
          "if": [
            { "or": [ { "==": [ { "var": "plan" }, "enterprise" ] }, { ">=": [ { "var": "seats" }, 100 ] } ] },
            "on",
            { "fractional": [ [ "on", 50 ], [ "off", 50 ] ] }
          ]
        }
        """;

    private sealed record Outcome(bool Value, string? Variant, string Reason);

    private sealed record User(string Key, string Plan, int Seats);

    private static readonly User[] Users = Enumerable.Range(0, 40)
        .Select(i => new User($"user-{i}", i % 5 == 0 ? "enterprise" : "free", i % 7 == 0 ? 150 : 3))
        .ToArray();

    [Fact]
    public async Task In_process_ofrep_and_openfeature_evaluation_agree_for_the_same_context()
    {
        await using var host = await FlagonTestHost.StartAsync(o =>
        {
            o.Services = services => services.AddOpenFeature(of => of.AddFlagon());
            o.Endpoints = app => app.MapFlagonOfrep();
        });
        await host.Admin.CreateAsync(new CreateFlagRequest("rollout", Enabled: false, Targeting: JsonDocument.Parse(Rule).RootElement));
        var http = host.CreateClient();
        var openFeature = host.Services.GetRequiredService<IFeatureClient>();
        var reasons = new HashSet<string>(StringComparer.Ordinal);

        foreach (var user in Users)
        {
            var inProcess = host.Flagon.GetBooleanDetails("rollout", false,
                new EvaluationContext(user.Key, new Dictionary<string, object?> { ["plan"] = user.Plan, ["seats"] = user.Seats }));
            var expected = new Outcome(inProcess.Value, inProcess.Variant, inProcess.Reason.ToWireName());
            reasons.Add(expected.Reason);

            var body = new { context = new { targetingKey = user.Key, plan = user.Plan, seats = user.Seats } };
            var single = await (await http.PostAsJsonAsync("/ofrep/v1/evaluate/flags/rollout", body)).Content.ReadFromJsonAsync<JsonElement>();
            var bulk = (await (await http.PostAsJsonAsync("/ofrep/v1/evaluate/flags", body)).Content.ReadFromJsonAsync<JsonElement>())
                .GetProperty("flags").EnumerateArray().Single(f => f.GetProperty("key").GetString() == "rollout");
            var of = await openFeature.GetBooleanDetailsAsync("rollout", false,
                OfEvaluationContext.Builder().SetTargetingKey(user.Key).Set("plan", user.Plan).Set("seats", user.Seats).Build());

            Assert.Equal(expected, Ofrep(single));
            Assert.Equal(expected, Ofrep(bulk));
            Assert.Equal(expected, new Outcome(of.Value, of.Variant, of.Reason!));
        }

        // The rule is exercised both ways: users bucketed off as well as on (fractional reports TARGETING_MATCH, as flagd does).
        Assert.Equal(["TARGETING_MATCH"], reasons);
        Assert.Contains(Users, u => !host.Flagon.GetBooleanDetails("rollout", true,
            new EvaluationContext(u.Key, new Dictionary<string, object?> { ["plan"] = u.Plan, ["seats"] = u.Seats })).Value);
    }

    private static Outcome Ofrep(JsonElement result) =>
        new(result.GetProperty("value").GetBoolean(), result.GetProperty("variant").GetString(), result.GetProperty("reason").GetString()!);
}
