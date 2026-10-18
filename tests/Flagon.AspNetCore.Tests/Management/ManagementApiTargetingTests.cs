using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Flagon.Storage;
using Flagon.Tests.Support;
using Microsoft.AspNetCore.Builder;

namespace Flagon.AspNetCore.Tests.Management;

/// <summary>Targeting through the management API: written with a flag or on its own, rejected with 400 when it does not compile.</summary>
public class ManagementApiTargetingTests
{
    private static readonly EvaluationContext Gb = new("alice", new Dictionary<string, object?> { ["country"] = "GB" });

    private static readonly EvaluationContext Fr = new("bob", new Dictionary<string, object?> { ["country"] = "FR" });

    private static readonly JsonElement GbRule = Json("""{ "if": [ { "==": [ { "var": "country" }, "GB" ] }, "on", null ] }""");

    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static Task<FlagonTestHost> StartAsync() => FlagonTestHost.StartAsync(o => o.Endpoints = app => app.MapFlagonApi());

    private static async Task<JsonElement> ProblemAsync(HttpResponseMessage response, HttpStatusCode status)
    {
        Assert.Equal(status, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static HttpRequestMessage PutTargeting(string key, long version, object body)
    {
        var request = new HttpRequestMessage(HttpMethod.Put, $"/flagon/api/flags/{key}/targeting") { Content = JsonContent.Create(body) };
        request.Headers.IfMatch.Add(new EntityTagHeaderValue($"\"{version}\""));
        return request;
    }

    [Fact]
    public async Task A_flag_created_with_targeting_is_evaluated_with_it_and_returns_it()
    {
        await using var host = await StartAsync();

        var response = await host.CreateClient().PostAsJsonAsync("/flagon/api/flags", new { key = "gb-only", enabled = false, targeting = GbRule });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var flag = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(GbRule.GetRawText()), JsonNode.Parse(flag.GetProperty("targeting").GetRawText())));
        Assert.Equal(EvaluationReason.TargetingMatch, host.Flagon.GetBooleanDetails("gb-only", false, Gb).Reason);
    }

    [Fact]
    public async Task A_flag_without_targeting_returns_null_targeting()
    {
        await using var host = await StartAsync();
        await host.Admin.CreateAsync(new CreateFlagRequest("plain"));

        var flag = await host.CreateClient().GetFromJsonAsync<JsonElement>("/flagon/api/flags/plain");

        Assert.Equal(JsonValueKind.Null, flag.GetProperty("targeting").ValueKind);
    }

    [Fact]
    public async Task Creating_a_flag_with_a_rule_that_does_not_compile_is_a_bad_request_naming_the_operator_and_path()
    {
        await using var host = await StartAsync();

        var response = await host.CreateClient().PostAsJsonAsync("/flagon/api/flags", new
        {
            key = "broken",
            targeting = Json("""{ "if": [ { "regex": [ { "var": "email" }, ".*" ] }, "on", null ] }"""),
        });

        var problem = await ProblemAsync(response, HttpStatusCode.BadRequest);
        Assert.Equal("Invalid targeting rule", problem.GetProperty("title").GetString());
        Assert.Contains("regex", problem.GetProperty("detail").GetString());
        Assert.Equal("broken", problem.GetProperty("key").GetString());
        Assert.Equal("regex", problem.GetProperty("operator").GetString());
        Assert.Equal("/if/0", problem.GetProperty("path").GetString());
        Assert.Null(await host.Admin.GetAsync("broken"));
    }

    [Fact]
    public async Task A_rule_with_an_unresolved_ref_is_a_bad_request_explaining_that_evaluators_must_be_inlined()
    {
        await using var host = await StartAsync();

        var response = await host.CreateClient().PostAsJsonAsync("/flagon/api/flags", new
        {
            key = "ref",
            targeting = Json("""{ "if": [ { "$ref": "is_staff" }, "on", null ] }"""),
        });

        var problem = await ProblemAsync(response, HttpStatusCode.BadRequest);
        Assert.Equal("$ref", problem.GetProperty("operator").GetString());
        Assert.Contains("$evaluators", problem.GetProperty("detail").GetString());
        Assert.Contains("is_staff", problem.GetProperty("detail").GetString());
    }

    [Fact]
    public async Task Targeting_can_be_set_and_cleared_on_an_existing_flag()
    {
        await using var host = await StartAsync();
        var created = await host.Admin.CreateAsync(new CreateFlagRequest("rollout"));
        var client = host.CreateClient();

        var set = await client.SendAsync(PutTargeting("rollout", created.Version, new { targeting = GbRule }));
        Assert.Equal(HttpStatusCode.OK, set.StatusCode);
        Assert.Equal(EvaluationReason.TargetingMatch, host.Flagon.GetBooleanDetails("rollout", false, Gb).Reason);

        var version = long.Parse(set.Headers.ETag!.Tag.Trim('"'), System.Globalization.CultureInfo.InvariantCulture);
        var cleared = await client.SendAsync(PutTargeting("rollout", version, new { targeting = (object?)null }));
        Assert.Equal(HttpStatusCode.OK, cleared.StatusCode);
        Assert.Equal(JsonValueKind.Null, (await cleared.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("targeting").ValueKind);
        Assert.Equal(EvaluationReason.Static, host.Flagon.GetBooleanDetails("rollout", false, Gb).Reason);
    }

    [Fact]
    public async Task Setting_a_rule_that_does_not_compile_is_a_bad_request_and_leaves_the_flag_unchanged()
    {
        await using var host = await StartAsync();
        var created = await host.Admin.CreateAsync(new CreateFlagRequest("rollout", Targeting: GbRule));

        var response = await host.CreateClient().SendAsync(PutTargeting("rollout", created.Version, new { targeting = Json("""{ "nope": [] }""") }));

        var problem = await ProblemAsync(response, HttpStatusCode.BadRequest);
        Assert.Equal("nope", problem.GetProperty("operator").GetString());
        Assert.Equal("", problem.GetProperty("path").GetString());
        Assert.Equal(created, await host.Admin.GetAsync("rollout"));
    }

    [Fact]
    public async Task Setting_targeting_without_the_targeting_field_is_a_bad_request()
    {
        await using var host = await StartAsync();
        var created = await host.Admin.CreateAsync(new CreateFlagRequest("rollout", Targeting: GbRule));

        var response = await host.CreateClient().SendAsync(PutTargeting("rollout", created.Version, new { }));

        await ProblemAsync(response, HttpStatusCode.BadRequest);
        Assert.Equal(created, await host.Admin.GetAsync("rollout"));
    }

    private static HttpRequestMessage DeleteTargeting(string key, long version)
    {
        var request = new HttpRequestMessage(HttpMethod.Delete, $"/flagon/api/flags/{key}/targeting");
        request.Headers.IfMatch.Add(new EntityTagHeaderValue($"\"{version}\""));
        return request;
    }

    [Fact]
    public async Task Targeting_can_be_replaced_and_then_deleted()
    {
        await using var host = await StartAsync();
        var created = await host.Admin.CreateAsync(new CreateFlagRequest("rollout", Targeting: GbRule));
        var client = host.CreateClient();

        var replaced = await client.SendAsync(PutTargeting("rollout", created.Version, new { targeting = Json("""{ "if": [ true, "on", null ] }""") }));
        Assert.Equal(HttpStatusCode.OK, replaced.StatusCode);
        Assert.Equal(EvaluationReason.TargetingMatch, host.Flagon.GetBooleanDetails("rollout", false, Fr).Reason);

        var deleted = await client.SendAsync(DeleteTargeting("rollout", created.Version + 1));
        Assert.Equal(HttpStatusCode.OK, deleted.StatusCode);
        Assert.Equal($"\"{created.Version + 2}\"", deleted.Headers.ETag!.Tag);
        Assert.Equal(JsonValueKind.Null, (await deleted.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("targeting").ValueKind);
        Assert.Equal(EvaluationReason.Static, host.Flagon.GetBooleanDetails("rollout", false, Gb).Reason);
        Assert.Null((await host.Admin.GetAuditHistoryAsync("rollout"))[0].After!.Targeting);
    }

    [Fact]
    public async Task Deleting_targeting_accepts_the_version_in_the_body()
    {
        await using var host = await StartAsync();
        var created = await host.Admin.CreateAsync(new CreateFlagRequest("rollout", Targeting: GbRule));

        var response = await host.CreateClient().SendAsync(new HttpRequestMessage(HttpMethod.Delete, "/flagon/api/flags/rollout/targeting")
        {
            Content = JsonContent.Create(new { version = created.Version }),
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Null((await host.Admin.GetAsync("rollout"))!.Document.Targeting);
    }

    [Fact]
    public async Task Setting_or_deleting_targeting_on_a_stale_version_is_a_conflict_with_the_current_version()
    {
        await using var host = await StartAsync();
        var created = await host.Admin.CreateAsync(new CreateFlagRequest("rollout", Targeting: GbRule));
        var current = await host.Admin.SetEnabledAsync("rollout", true, created.Version);
        var client = host.CreateClient();

        var put = await ProblemAsync(await client.SendAsync(PutTargeting("rollout", created.Version, new { targeting = (object?)null })), HttpStatusCode.Conflict);
        var delete = await ProblemAsync(await client.SendAsync(DeleteTargeting("rollout", created.Version)), HttpStatusCode.Conflict);

        Assert.Equal(current.Version, put.GetProperty("currentVersion").GetInt64());
        Assert.Equal(current.Version, delete.GetProperty("currentVersion").GetInt64());
        Assert.Equal(current, await host.Admin.GetAsync("rollout"));
    }

    [Fact]
    public async Task Setting_or_deleting_targeting_on_an_archived_flag_is_a_bad_request()
    {
        await using var host = await StartAsync();
        var created = await host.Admin.CreateAsync(new CreateFlagRequest("retired", Targeting: GbRule));
        var archived = await host.Admin.ArchiveAsync("retired", created.Version);
        var client = host.CreateClient();

        var put = await ProblemAsync(await client.SendAsync(PutTargeting("retired", archived.Version, new { targeting = (object?)null })), HttpStatusCode.BadRequest);
        var delete = await ProblemAsync(await client.SendAsync(DeleteTargeting("retired", archived.Version)), HttpStatusCode.BadRequest);

        Assert.Contains("archived", put.GetProperty("detail").GetString());
        Assert.Contains("archived", delete.GetProperty("detail").GetString());
        Assert.Equal(archived, await host.Admin.GetAsync("retired"));
    }

    private static string[] Buckets(JsonElement rollout) => rollout.GetProperty("buckets").EnumerateArray()
        .Select(b => $"{b.GetProperty("variant").GetString()}={b.GetProperty("percentage").GetInt32()}").ToArray();

    private static Task<FlagRecord> CreateAsync(FlagonTestHost host, string key, string? variants, string targeting) =>
        host.Admin.CreateAsync(new CreateFlagRequest(key, Targeting: Json(targeting),
            Variants: variants is null ? null : Json(variants).EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone(), StringComparer.Ordinal),
            DefaultVariant: variants is null ? null : Json(variants).EnumerateObject().First().Name));

    [Theory]
    [MemberData(nameof(RolloutShapes.Rollouts), MemberType = typeof(RolloutShapes))]
    public async Task A_simple_percentage_rollout_is_summarised_as_its_percentage_and_on_off_buckets(string targeting, int percentage)
    {
        await using var host = await StartAsync();
        await host.Admin.CreateAsync(new CreateFlagRequest("rollout", Targeting: Json(targeting)));

        var flag = await host.CreateClient().GetFromJsonAsync<JsonElement>("/flagon/api/flags/rollout");
        var listed = Assert.Single(await host.CreateClient().GetFromJsonAsync<JsonElement[]>("/flagon/api/flags") ?? []);

        Assert.Equal(percentage, flag.GetProperty("rollout").GetProperty("percentage").GetInt32());
        Assert.Equal(percentage, listed.GetProperty("rollout").GetProperty("percentage").GetInt32());
        Assert.Equal([$"on={percentage}", $"off={100 - percentage}"], Buckets(flag.GetProperty("rollout")));
    }

    [Theory]
    [MemberData(nameof(RolloutShapes.Splits), MemberType = typeof(RolloutShapes))]
    public async Task A_canonical_split_is_summarised_as_its_buckets_without_a_percentage(string? variants, string targeting, string[] buckets)
    {
        await using var host = await StartAsync();
        await CreateAsync(host, "split", variants, targeting);

        var flag = await host.CreateClient().GetFromJsonAsync<JsonElement>("/flagon/api/flags/split");
        var listed = Assert.Single(await host.CreateClient().GetFromJsonAsync<JsonElement[]>("/flagon/api/flags") ?? []);

        Assert.Equal(buckets, Buckets(flag.GetProperty("rollout")));
        Assert.Equal(buckets, Buckets(listed.GetProperty("rollout")));
        Assert.False(flag.GetProperty("rollout").TryGetProperty("percentage", out _));
    }

    [Theory]
    [MemberData(nameof(RolloutShapes.NotSplits), MemberType = typeof(RolloutShapes))]
    public async Task Targeting_that_is_not_exactly_a_canonical_split_has_no_rollout_summary(string? variants, string targeting)
    {
        await using var host = await StartAsync();
        await CreateAsync(host, "targeted", variants, targeting);

        var flag = await host.CreateClient().GetFromJsonAsync<JsonElement>("/flagon/api/flags/targeted");

        Assert.Equal(JsonValueKind.Null, flag.GetProperty("rollout").ValueKind);
    }

    [Fact]
    public async Task A_flag_without_targeting_has_no_rollout_summary()
    {
        await using var host = await StartAsync();
        await host.Admin.CreateAsync(new CreateFlagRequest("plain"));

        var flag = await host.CreateClient().GetFromJsonAsync<JsonElement>("/flagon/api/flags/plain");

        Assert.Equal(JsonValueKind.Null, flag.GetProperty("rollout").ValueKind);
    }
}
