using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Jorrk.Flagon.Tests.Support;
using Microsoft.AspNetCore.Builder;

namespace Jorrk.Flagon.AspNetCore.Tests.Management;

/// <summary>Segments through the management API: every route, and each segment error as problem details.</summary>
public class ManagementApiSegmentTests
{
    private const string Api = "/flagon/api";
    private static readonly JsonElement StaffRule = Json("""{"in":[{"var":"targetingKey"},["ada","grace"]]}""");
    private static readonly JsonElement UsesStaff = Json("""{"if":[{"$ref":"staff"},"on",null]}""");

    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static Task<FlagonTestHost> StartAsync(Action<FlagonTestHostOptions>? configure = null) => FlagonTestHost.StartAsync(o =>
    {
        o.Endpoints = app => app.MapFlagonApi();
        configure?.Invoke(o);
    });

    private static async Task<JsonElement> ProblemAsync(HttpResponseMessage response, HttpStatusCode status, string title)
    {
        Assert.Equal(status, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(title, problem.GetProperty("title").GetString());
        return problem;
    }

    private static HttpRequestMessage Request(HttpMethod method, string path, long? version = null, object? body = null)
    {
        var request = new HttpRequestMessage(method, Api + path) { Content = body is null ? null : JsonContent.Create(body) };
        if (version is { } v)
        {
            request.Headers.IfMatch.Add(new EntityTagHeaderValue($"\"{v}\""));
        }

        return request;
    }

    private static async Task<SegmentRecord> CreateStaffAsync(FlagonTestHost host) =>
        await host.Admin.CreateSegmentAsync(new CreateSegmentRequest("staff", StaffRule, "Employees", ["internal"]));

    [Fact]
    public async Task A_segment_can_be_created_read_and_listed_with_its_key_list_and_usage()
    {
        await using var host = await StartAsync();
        var client = host.CreateClient();

        var created = await client.PostAsJsonAsync($"{Api}/segments", new { key = "staff", rule = StaffRule, description = "Employees", tags = new[] { "internal" } });
        await host.Admin.CreateAsync(new CreateFlagRequest("beta", Targeting: UsesStaff));
        await host.Admin.CreateSegmentAsync(new CreateSegmentRequest("eu", Json("""{"in":[{"var":"country"},["FR","DE"]]}""")));

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.Equal($"{Api}/segments/staff", created.Headers.Location?.OriginalString);
        var body = await created.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("staff", body.GetProperty("key").GetString());
        Assert.Equal(["ada", "grace"], body.GetProperty("keys").EnumerateArray().Select(k => k.GetString()));
        Assert.Equal("manual", body.GetProperty("source").GetString());

        var get = await client.GetAsync($"{Api}/segments/staff");
        var staff = await get.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal($"\"{staff.GetProperty("version").GetInt64()}\"", get.Headers.ETag?.Tag);
        var user = Assert.Single(staff.GetProperty("usedBy").EnumerateArray());
        Assert.Equal("beta", user.GetProperty("key").GetString());
        Assert.False(user.GetProperty("archived").GetBoolean());

        var list = await client.GetFromJsonAsync<JsonElement>($"{Api}/segments");
        Assert.Equal(["eu", "staff"], list.EnumerateArray().Select(s => s.GetProperty("key").GetString()));
        Assert.Equal([0, 1], list.EnumerateArray().Select(s => s.GetProperty("usedBy").GetInt32()));
        Assert.False(list[0].TryGetProperty("keys", out _));

        var filtered = await client.GetFromJsonAsync<JsonElement>($"{Api}/segments?tag=internal&q=employ&archived=false");
        Assert.Equal("staff", Assert.Single(filtered.EnumerateArray()).GetProperty("key").GetString());
    }

    [Fact]
    public async Task A_segments_rule_and_metadata_can_be_edited_with_its_version()
    {
        await using var host = await StartAsync();
        var staff = await CreateStaffAsync(host);
        var client = host.CreateClient();

        var rule = await client.SendAsync(Request(HttpMethod.Put, "/segments/staff/rule", body: new { rule = Json("""{"==":[{"var":"team"},"staff"]}"""), version = staff.Version }));
        var ruled = await rule.Content.ReadFromJsonAsync<JsonElement>();
        var patch = await client.SendAsync(Request(new HttpMethod("PATCH"), "/segments/staff", ruled.GetProperty("version").GetInt64(), new { description = "Staff team" }));
        var stale = await client.SendAsync(Request(new HttpMethod("PATCH"), "/segments/staff", staff.Version, new { description = "lost" }));

        Assert.Equal(HttpStatusCode.OK, rule.StatusCode);
        Assert.False(ruled.TryGetProperty("keys", out _));
        Assert.Equal(HttpStatusCode.OK, patch.StatusCode);
        Assert.Equal("Staff team", (await host.Admin.GetSegmentAsync("staff"))!.Document.Metadata.Description);
        var conflict = await ProblemAsync(stale, HttpStatusCode.Conflict, "Version conflict");
        Assert.Equal((await host.Admin.GetSegmentAsync("staff"))!.Version, conflict.GetProperty("currentVersion").GetInt64());
    }

    [Fact]
    public async Task Archive_restore_purge_and_audit_go_through_the_api()
    {
        await using var host = await StartAsync();
        var staff = await CreateStaffAsync(host);
        var client = host.CreateClient();

        var archived = await (await client.SendAsync(Request(HttpMethod.Post, "/segments/staff/archive", staff.Version))).Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(archived.GetProperty("archived").GetBoolean());
        var restored = await (await client.SendAsync(Request(HttpMethod.Post, "/segments/staff/restore", archived.GetProperty("version").GetInt64()))).Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(restored.GetProperty("archived").GetBoolean());
        var rearchived = await (await client.SendAsync(Request(HttpMethod.Post, "/segments/staff/archive", restored.GetProperty("version").GetInt64()))).Content.ReadFromJsonAsync<JsonElement>();
        var purge = await client.SendAsync(Request(HttpMethod.Delete, "/segments/staff", rearchived.GetProperty("version").GetInt64()));

        Assert.Equal(HttpStatusCode.NoContent, purge.StatusCode);
        Assert.Null(await host.Admin.GetSegmentAsync("staff"));
        var audit = await client.GetFromJsonAsync<JsonElement>($"{Api}/segments/staff/audit");
        Assert.Equal(["purged", "archived", "restored", "archived", "created"], audit.EnumerateArray().Select(e => e.GetProperty("action").GetString()));
        Assert.Equal(StaffRule.GetRawText(), audit[4].GetProperty("after").GetProperty("rule").GetRawText());
        await ProblemAsync(await client.GetAsync($"{Api}/segments/staff"), HttpStatusCode.NotFound, "Segment not found");
    }

    [Fact]
    public async Task A_segment_can_be_tested_against_a_context_stored_or_as_a_draft()
    {
        await using var host = await StartAsync();
        await CreateStaffAsync(host);
        var client = host.CreateClient();

        var ada = await (await client.PostAsJsonAsync($"{Api}/segments/staff/evaluate", new { context = new { targetingKey = "ada" } })).Content.ReadFromJsonAsync<JsonElement>();
        var bob = await (await client.PostAsJsonAsync($"{Api}/segments/staff/evaluate", new { context = new { targetingKey = "bob" } })).Content.ReadFromJsonAsync<JsonElement>();
        var draft = await (await client.PostAsJsonAsync($"{Api}/segments/new-one/evaluate", new
        {
            context = new { targetingKey = "bob", team = "staff" },
            rule = Json("""{"==":[{"var":"team"},"staff"]}"""),
        })).Content.ReadFromJsonAsync<JsonElement>();
        var broken = await client.PostAsJsonAsync($"{Api}/segments/staff/evaluate", new { rule = Json("""{"nope":[1]}""") });
        var missing = await client.PostAsJsonAsync($"{Api}/segments/missing/evaluate", new { context = new { targetingKey = "ada" } });

        Assert.True(ada.GetProperty("matches").GetBoolean());
        Assert.Equal("true", ada.GetProperty("result").GetString());
        Assert.False(bob.GetProperty("matches").GetBoolean());
        Assert.True(draft.GetProperty("matches").GetBoolean());
        var problem = await ProblemAsync(broken, HttpStatusCode.BadRequest, "Invalid segment rule");
        Assert.Equal("nope", problem.GetProperty("operator").GetString());
        await ProblemAsync(missing, HttpStatusCode.NotFound, "Segment not found");
    }

    [Fact]
    public async Task Flags_report_the_segments_they_reference_and_can_be_filtered_by_segment()
    {
        await using var host = await StartAsync();
        await CreateStaffAsync(host);
        await host.Admin.CreateSegmentAsync(new CreateSegmentRequest("eu", Json("""{"in":[{"var":"country"},["FR"]]}""")));
        await host.Admin.CreateAsync(new CreateFlagRequest("beta", Targeting: UsesStaff));
        await host.Admin.CreateAsync(new CreateFlagRequest("both", Targeting: Json("""{"if":[{"or":[{"$ref":"staff"},{"$ref":"eu"}]},"on",null]}""")));
        await host.Admin.CreateAsync(new CreateFlagRequest("custom", Targeting: Json("""{"if":[{"and":[{"$ref":"eu"},true]},"on",null]}""")));
        var client = host.CreateClient();

        var beta = await client.GetFromJsonAsync<JsonElement>($"{Api}/flags/beta");
        var both = await client.GetFromJsonAsync<JsonElement>($"{Api}/flags/both");
        var custom = await client.GetFromJsonAsync<JsonElement>($"{Api}/flags/custom");
        var usingStaff = await client.GetFromJsonAsync<JsonElement>($"{Api}/flags?segment=staff");

        Assert.Equal(["staff"], beta.GetProperty("segments").EnumerateArray().Select(s => s.GetString()));
        Assert.Equal(["staff"], beta.GetProperty("segmentTargeting").GetProperty("segments").EnumerateArray().Select(s => s.GetString()));
        Assert.Equal("on", beta.GetProperty("segmentTargeting").GetProperty("variant").GetString());
        Assert.Equal(["staff", "eu"], both.GetProperty("segmentTargeting").GetProperty("segments").EnumerateArray().Select(s => s.GetString()));
        Assert.Equal(["eu"], custom.GetProperty("segments").EnumerateArray().Select(s => s.GetString()));
        Assert.Equal(JsonValueKind.Null, custom.GetProperty("segmentTargeting").ValueKind);
        Assert.Equal(["beta", "both"], usingStaff.EnumerateArray().Select(f => f.GetProperty("key").GetString()));
    }

    [Fact]
    public async Task Segment_errors_are_problem_details()
    {
        await using var host = await StartAsync();
        var staff = await CreateStaffAsync(host);
        await host.Admin.CreateAsync(new CreateFlagRequest("beta", Targeting: UsesStaff));
        var client = host.CreateClient();

        await ProblemAsync(await client.PostAsJsonAsync($"{Api}/segments", new { key = "staff", rule = StaffRule }), HttpStatusCode.Conflict, "Segment already exists");
        await ProblemAsync(await client.PostAsJsonAsync($"{Api}/segments", new { key = "nameless" }), HttpStatusCode.BadRequest, "Invalid request");
        var rule = await ProblemAsync(
            await client.PostAsJsonAsync($"{Api}/segments", new { key = "nested", rule = Json("""{"or":[true,{"$ref":"staff"}]}""") }),
            HttpStatusCode.BadRequest, "Invalid segment rule");
        Assert.Equal("nested", rule.GetProperty("key").GetString());
        Assert.Equal("/or/1", rule.GetProperty("path").GetString());
        Assert.Equal("$ref", rule.GetProperty("operator").GetString());

        var inUse = await ProblemAsync(await client.SendAsync(Request(HttpMethod.Post, "/segments/staff/archive", staff.Version)), HttpStatusCode.Conflict, "Segment in use");
        Assert.Equal(["beta"], inUse.GetProperty("flags").EnumerateArray().Select(f => f.GetString()));

        var missingRef = await ProblemAsync(
            await client.PostAsJsonAsync($"{Api}/flags", new { key = "gamma", targeting = Json("""{"if":[{"$ref":"nobody"},"on",null]}""") }),
            HttpStatusCode.BadRequest, "Invalid targeting rule");
        Assert.Equal("$ref", missingRef.GetProperty("operator").GetString());
        Assert.Equal("/if/0", missingRef.GetProperty("path").GetString());

        await host.Admin.CreateSegmentAsync(new CreateSegmentRequest("old", StaffRule));
        var old = await host.Admin.GetSegmentAsync("old");
        await host.Admin.ArchiveSegmentAsync("old", old!.Version);
        var archived = await ProblemAsync(
            await client.PostAsJsonAsync($"{Api}/flags", new { key = "delta", targeting = Json("""{"if":[{"$ref":"old"},"on",null]}""") }),
            HttpStatusCode.BadRequest, "Segment archived");
        Assert.Equal(["old"], archived.GetProperty("segments").EnumerateArray().Select(s => s.GetString()));
    }

    [Theory]
    [InlineData("GET", "/segments")]
    [InlineData("POST", "/segments")]
    [InlineData("GET", "/segments/staff")]
    [InlineData("PATCH", "/segments/staff")]
    [InlineData("PUT", "/segments/staff/rule")]
    [InlineData("POST", "/segments/staff/archive")]
    [InlineData("POST", "/segments/staff/restore")]
    [InlineData("DELETE", "/segments/staff")]
    [InlineData("GET", "/segments/staff/audit")]
    [InlineData("POST", "/segments/staff/evaluate")]
    public async Task Every_segment_route_is_not_implemented_when_the_store_has_no_segments(string method, string path)
    {
        var store = OutageFlagStore.Wrap();
        await using var host = await StartAsync(o => o.Store = store.Store);

        var response = await host.CreateClient().SendAsync(Request(new HttpMethod(method), path, version: 1, body: new { }));

        await ProblemAsync(response, HttpStatusCode.NotImplemented, "Segments not supported");
    }
}
