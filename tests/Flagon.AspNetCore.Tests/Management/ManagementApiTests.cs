using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Flagon.Tests.Support;
using Microsoft.AspNetCore.Builder;

namespace Flagon.AspNetCore.Tests.Management;

public class ManagementApiTests
{
    private static Task<FlagonTestHost> StartAsync(Action<FlagonTestHostOptions>? configure = null) =>
        FlagonTestHost.StartAsync(o =>
        {
            o.Endpoints = app => app.MapFlagonApi();
            configure?.Invoke(o);
        });

    private static IEnumerable<string?> Keys(JsonElement flags) => flags.EnumerateArray().Select(f => f.GetProperty("key").GetString());

    private static async Task<JsonElement> ProblemAsync(HttpResponseMessage response, HttpStatusCode status)
    {
        Assert.Equal(status, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal((int)status, problem.GetProperty("status").GetInt32());
        return problem;
    }

    private static HttpRequestMessage WithIfMatch(HttpMethod method, string uri, long version, object? body = null)
    {
        var request = new HttpRequestMessage(method, uri) { Content = body is null ? null : JsonContent.Create(body) };
        request.Headers.IfMatch.Add(new EntityTagHeaderValue($"\"{version}\""));
        return request;
    }

    [Fact]
    public async Task Listing_flags_returns_every_flag_with_its_state_and_metadata()
    {
        await using var host = await StartAsync();
        await host.Admin.CreateAsync(new CreateFlagRequest("new-checkout", Enabled: true, "New checkout", ["checkout"]));
        await host.Admin.CreateAsync(new CreateFlagRequest("dark-mode"));

        var flags = await host.CreateClient().GetFromJsonAsync<JsonElement>("/flagon/api/flags");

        Assert.Equal(["dark-mode", "new-checkout"], Keys(flags));
        var checkout = flags.EnumerateArray().Single(f => f.GetProperty("key").GetString() == "new-checkout");
        Assert.True(checkout.GetProperty("enabled").GetBoolean());
        Assert.Equal("enabled", checkout.GetProperty("state").GetString());
        Assert.Equal("New checkout", checkout.GetProperty("description").GetString());
        Assert.Equal(["checkout"], checkout.GetProperty("tags").EnumerateArray().Select(t => t.GetString()));
        Assert.Equal("manual", checkout.GetProperty("source").GetString());
        Assert.False(checkout.GetProperty("archived").GetBoolean());
        Assert.True(checkout.GetProperty("version").GetInt64() > 0);
    }

    [Fact]
    public async Task The_flag_list_can_be_filtered_by_text_tag_source_state_and_archived()
    {
        await using var host = await StartAsync();
        await host.Admin.CreateAsync(new CreateFlagRequest("new-checkout", Enabled: true, "Faster payment page", ["checkout", "web"]));
        await host.Admin.CreateAsync(new CreateFlagRequest("checkout-banner", Enabled: false, Tags: ["web"]));
        var old = await host.Admin.CreateAsync(new CreateFlagRequest("old-search", Enabled: true));
        await host.Admin.ArchiveAsync("old-search", old.Version);
        var client = host.CreateClient();

        Assert.Equal(["checkout-banner", "new-checkout"], Keys(await client.GetFromJsonAsync<JsonElement>("/flagon/api/flags?q=CHECKOUT")));
        Assert.Equal(["new-checkout"], Keys(await client.GetFromJsonAsync<JsonElement>("/flagon/api/flags?q=payment")));
        Assert.Equal(["new-checkout"], Keys(await client.GetFromJsonAsync<JsonElement>("/flagon/api/flags?tag=checkout")));
        Assert.Equal(["checkout-banner"], Keys(await client.GetFromJsonAsync<JsonElement>("/flagon/api/flags?enabled=false")));
        Assert.Equal(["old-search"], Keys(await client.GetFromJsonAsync<JsonElement>("/flagon/api/flags?archived=true")));
        Assert.Equal(["new-checkout"], Keys(await client.GetFromJsonAsync<JsonElement>("/flagon/api/flags?archived=false&enabled=true")));
        Assert.Equal(3, (await client.GetFromJsonAsync<JsonElement>("/flagon/api/flags?source=manual")).GetArrayLength());
        Assert.Equal(0, (await client.GetFromJsonAsync<JsonElement>("/flagon/api/flags?source=auto")).GetArrayLength());
    }

    [Fact]
    public async Task An_unknown_filter_value_is_a_bad_request()
    {
        await using var host = await StartAsync();

        await ProblemAsync(await host.CreateClient().GetAsync("/flagon/api/flags?source=robot"), HttpStatusCode.BadRequest);
        await ProblemAsync(await host.CreateClient().GetAsync("/flagon/api/flags?enabled=maybe"), HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Getting_a_flag_returns_it_with_its_version_as_the_etag()
    {
        await using var host = await StartAsync();
        var created = await host.Admin.CreateAsync(new CreateFlagRequest("new-checkout", Enabled: true));

        var response = await host.CreateClient().GetAsync("/flagon/api/flags/new-checkout");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal($"\"{created.Version}\"", response.Headers.ETag?.Tag);
        var flag = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("new-checkout", flag.GetProperty("key").GetString());
        Assert.Equal(created.Version, flag.GetProperty("version").GetInt64());
    }

    [Fact]
    public async Task Getting_a_missing_flag_is_not_found()
    {
        await using var host = await StartAsync();

        await ProblemAsync(await host.CreateClient().GetAsync("/flagon/api/flags/nope"), HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Creating_a_flag_returns_201_with_its_location_and_the_app_sees_it_immediately()
    {
        await using var host = await StartAsync();

        var response = await host.CreateClient().PostAsJsonAsync("/flagon/api/flags",
            new { key = "new-checkout", enabled = true, description = "New checkout", tags = new[] { "checkout" } });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal("/flagon/api/flags/new-checkout", response.Headers.Location?.OriginalString);
        var flag = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("New checkout", flag.GetProperty("description").GetString());
        Assert.True(host.Flagon.IsEnabled("new-checkout"));
    }

    [Fact]
    public async Task Creating_a_flag_with_an_invalid_key_or_tag_is_a_bad_request_with_problem_details()
    {
        await using var host = await StartAsync();
        var client = host.CreateClient();

        var badKey = await ProblemAsync(await client.PostAsJsonAsync("/flagon/api/flags", new { key = "not valid!" }), HttpStatusCode.BadRequest);
        Assert.Contains("not valid!", badKey.GetProperty("detail").GetString());
        await ProblemAsync(await client.PostAsJsonAsync("/flagon/api/flags", new { key = "ok", tags = new[] { "a,b" } }), HttpStatusCode.BadRequest);
        await ProblemAsync(await client.PostAsJsonAsync("/flagon/api/flags", new { enabled = true }), HttpStatusCode.BadRequest);
        await ProblemAsync(await client.PostAsync("/flagon/api/flags", new StringContent("{oops", null, "application/json")), HttpStatusCode.BadRequest);
        Assert.Empty(await host.Admin.ListAsync());
    }

    [Fact]
    public async Task Creating_a_flag_whose_key_exists_is_a_conflict()
    {
        await using var host = await StartAsync();
        await host.Admin.CreateAsync(new CreateFlagRequest("new-checkout"));

        await ProblemAsync(await host.CreateClient().PostAsJsonAsync("/flagon/api/flags", new { key = "new-checkout" }), HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Toggling_a_flag_with_the_version_in_the_body_changes_what_the_app_evaluates()
    {
        await using var host = await StartAsync();
        var created = await host.Admin.CreateAsync(new CreateFlagRequest("new-checkout", Enabled: false));

        var response = await host.CreateClient().PutAsJsonAsync("/flagon/api/flags/new-checkout/enabled", new { enabled = true, version = created.Version });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var flag = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(flag.GetProperty("enabled").GetBoolean());
        Assert.True(flag.GetProperty("version").GetInt64() > created.Version);
        Assert.Equal($"\"{flag.GetProperty("version").GetInt64()}\"", response.Headers.ETag?.Tag);
        Assert.True(host.Flagon.IsEnabled("new-checkout"));
    }

    [Fact]
    public async Task Updating_metadata_with_if_match_changes_description_and_tags_but_not_state()
    {
        await using var host = await StartAsync();
        var created = await host.Admin.CreateAsync(new CreateFlagRequest("new-checkout", Enabled: true, "Old", ["a"]));

        var response = await host.CreateClient().SendAsync(WithIfMatch(HttpMethod.Patch, "/flagon/api/flags/new-checkout", created.Version,
            new { description = "New checkout flow", tags = new[] { "checkout", "web" } }));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var stored = await host.Admin.GetAsync("new-checkout");
        Assert.Equal("New checkout flow", stored!.Document.Metadata.Description);
        Assert.Equal(["checkout", "web"], stored.Document.Metadata.Tags);
        Assert.True(host.Flagon.IsEnabled("new-checkout"));
    }

    [Fact]
    public async Task Omitted_metadata_fields_are_left_unchanged()
    {
        await using var host = await StartAsync();
        var created = await host.Admin.CreateAsync(new CreateFlagRequest("new-checkout", Description: "Keep me", Tags: ["a"]));

        await host.CreateClient().PatchAsJsonAsync("/flagon/api/flags/new-checkout", new { tags = new[] { "b" }, version = created.Version });

        var stored = await host.Admin.GetAsync("new-checkout");
        Assert.Equal("Keep me", stored!.Document.Metadata.Description);
        Assert.Equal(["b"], stored.Document.Metadata.Tags);
    }

    [Fact]
    public async Task A_write_based_on_a_stale_version_is_a_conflict_carrying_the_current_version()
    {
        await using var host = await StartAsync();
        var created = await host.Admin.CreateAsync(new CreateFlagRequest("new-checkout"));
        var current = await host.Admin.SetEnabledAsync("new-checkout", true, created.Version);
        var client = host.CreateClient();

        var viaHeader = await client.SendAsync(WithIfMatch(HttpMethod.Put, "/flagon/api/flags/new-checkout/enabled", created.Version, new { enabled = false }));
        var problem = await ProblemAsync(viaHeader, HttpStatusCode.Conflict);
        Assert.Equal(current.Version, problem.GetProperty("currentVersion").GetInt64());
        Assert.Equal(created.Version, problem.GetProperty("expectedVersion").GetInt64());
        Assert.Equal($"\"{current.Version}\"", viaHeader.Headers.ETag?.Tag);

        var viaBody = await client.PatchAsJsonAsync("/flagon/api/flags/new-checkout", new { description = "x", version = created.Version });
        Assert.Equal(current.Version, (await ProblemAsync(viaBody, HttpStatusCode.Conflict)).GetProperty("currentVersion").GetInt64());

        Assert.True(host.Flagon.IsEnabled("new-checkout"));
    }

    [Fact]
    public async Task A_write_without_an_expected_version_is_a_bad_request()
    {
        await using var host = await StartAsync();
        await host.Admin.CreateAsync(new CreateFlagRequest("new-checkout"));
        var client = host.CreateClient();

        await ProblemAsync(await client.PutAsJsonAsync("/flagon/api/flags/new-checkout/enabled", new { enabled = true }), HttpStatusCode.BadRequest);
        await ProblemAsync(await client.PostAsync("/flagon/api/flags/new-checkout/archive", null), HttpStatusCode.BadRequest);
        Assert.False(host.Flagon.IsEnabled("new-checkout"));
    }

    [Fact]
    public async Task Writing_to_a_missing_flag_is_not_found()
    {
        await using var host = await StartAsync();

        await ProblemAsync(
            await host.CreateClient().PutAsJsonAsync("/flagon/api/flags/nope/enabled", new { enabled = true, version = 1 }),
            HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task A_flag_can_be_archived_restored_archived_again_and_purged()
    {
        await using var host = await StartAsync();
        var created = await host.Admin.CreateAsync(new CreateFlagRequest("old-search", Enabled: true));
        var client = host.CreateClient();

        var archived = await client.SendAsync(WithIfMatch(HttpMethod.Post, "/flagon/api/flags/old-search/archive", created.Version));
        Assert.Equal(HttpStatusCode.OK, archived.StatusCode);
        var archivedFlag = await archived.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(archivedFlag.GetProperty("archived").GetBoolean());
        Assert.False(host.Flagon.IsEnabled("old-search"));

        var restored = await client.PostAsJsonAsync("/flagon/api/flags/old-search/restore", new { version = archivedFlag.GetProperty("version").GetInt64() });
        var restoredFlag = await restored.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(restoredFlag.GetProperty("archived").GetBoolean());
        Assert.True(host.Flagon.IsEnabled("old-search"));

        var purgeLive = await client.SendAsync(WithIfMatch(HttpMethod.Delete, "/flagon/api/flags/old-search", restoredFlag.GetProperty("version").GetInt64()));
        await ProblemAsync(purgeLive, HttpStatusCode.BadRequest);

        var rearchived = await (await client.SendAsync(WithIfMatch(HttpMethod.Post, "/flagon/api/flags/old-search/archive",
            restoredFlag.GetProperty("version").GetInt64()))).Content.ReadFromJsonAsync<JsonElement>();
        var purged = await client.SendAsync(WithIfMatch(HttpMethod.Delete, "/flagon/api/flags/old-search", rearchived.GetProperty("version").GetInt64()));

        Assert.Equal(HttpStatusCode.NoContent, purged.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/flagon/api/flags/old-search")).StatusCode);
    }

    [Fact]
    public async Task Audit_history_is_returned_newest_first_and_attributes_api_writes_to_the_signed_in_user()
    {
        await using var host = await StartAsync();
        var alice = host.CreateClient("alice");

        var created = await (await alice.PostAsJsonAsync("/flagon/api/flags", new { key = "new-checkout" })).Content.ReadFromJsonAsync<JsonElement>();
        host.Time.Advance(TimeSpan.FromMinutes(1));
        await alice.PutAsJsonAsync("/flagon/api/flags/new-checkout/enabled", new { enabled = true, version = created.GetProperty("version").GetInt64() });

        var audit = await alice.GetFromJsonAsync<JsonElement>("/flagon/api/flags/new-checkout/audit");

        Assert.Equal(["updated", "created"], audit.EnumerateArray().Select(e => e.GetProperty("action").GetString()));
        var update = audit[0];
        Assert.Equal("alice", update.GetProperty("actor").GetString());
        Assert.Equal("TestApp", update.GetProperty("applicationName").GetString());
        Assert.Equal(FlagonTestHost.Epoch.AddMinutes(1), update.GetProperty("timestamp").GetDateTimeOffset());
        Assert.Equal("off", update.GetProperty("before").GetProperty("defaultVariant").GetString());
        Assert.Equal("on", update.GetProperty("after").GetProperty("defaultVariant").GetString());
        Assert.Equal(JsonValueKind.Null, audit[1].GetProperty("before").ValueKind);
        Assert.Equal("alice", (await host.Admin.GetAsync("new-checkout"))!.Document.Metadata.UpdatedBy);
    }

    [Fact]
    public async Task Anonymous_writes_have_no_actor()
    {
        await using var host = await StartAsync();

        await host.CreateClient().PostAsJsonAsync("/flagon/api/flags", new { key = "new-checkout" });

        Assert.Null(Assert.Single(await host.Admin.GetAuditHistoryAsync("new-checkout")).Actor);
    }

    [Fact]
    public async Task The_api_can_be_mounted_at_a_custom_path()
    {
        await using var host = await FlagonTestHost.StartAsync(o => o.Endpoints = app => app.MapFlagonApi("/admin/flags-api/"));
        await host.Admin.CreateAsync(new CreateFlagRequest("new-checkout"));
        var client = host.CreateClient();

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/admin/flags-api/flags/new-checkout")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/flagon/api/flags/new-checkout")).StatusCode);
    }
}
