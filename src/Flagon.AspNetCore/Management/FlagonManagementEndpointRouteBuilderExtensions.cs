using Flagon.AspNetCore.Management;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

// ReSharper disable once CheckNamespace
namespace Microsoft.AspNetCore.Builder;

/// <summary>Maps Flagon's HTTP management API.</summary>
public static class FlagonManagementEndpointRouteBuilderExtensions
{
    /// <summary>The path <see cref="MapFlagonApi"/> mounts the API at when none is given.</summary>
    public const string DefaultPath = "/flagon/api";

    /// <summary>
    /// Maps the management API, a JSON wrapper over <see cref="Flagon.IFlagonAdmin"/> for building your own tooling:
    /// <list type="bullet">
    /// <item><c>GET {path}/flags</c>: lists flags; optional filters <c>q</c> (key/description contains, case-insensitive),
    /// <c>tag</c>, <c>source</c> (<c>manual</c>|<c>auto</c>|<c>code</c>), <c>enabled</c> and <c>archived</c> (<c>true</c>|<c>false</c>).</item>
    /// <item><c>GET {path}/flags/{key}</c>: one flag (with an <c>ETag</c> of its version). Flags carry their raw
    /// <c>targeting</c> (or <c>null</c>) and a <c>rollout</c> summary, <c>{ percentage }</c>, when the rule is exactly
    /// <c>{"fractional": [["on", N], ["off", 100 - N]]}</c> with default bucketing (otherwise <c>null</c>).</item>
    /// <item><c>POST {path}/flags</c>: creates a flag from <c>{ key, enabled?, description?, tags?, targeting? }</c>.</item>
    /// <item><c>PATCH {path}/flags/{key}</c>: updates <c>{ description?, tags? }</c> (omitted fields are unchanged).</item>
    /// <item><c>PUT {path}/flags/{key}/enabled</c>: toggles the flag from <c>{ enabled }</c>.</item>
    /// <item><c>PUT {path}/flags/{key}/targeting</c>: replaces the flag's flagd targeting rule from <c>{ targeting }</c>
    /// (<c>null</c> removes it); <c>DELETE {path}/flags/{key}/targeting</c> removes it. Archived flags are refused (<c>400</c>).</item>
    /// <item><c>POST {path}/flags/{key}/evaluate</c>: previews the flag against <c>{ context?, document? }</c> and returns
    /// <c>{ value, variant, reason, errorCode?, errorMessage? }</c>. Without <c>document</c> the stored flag is evaluated;
    /// with one, that unsaved flagd flag document is (as if stored and not archived). Previews have no side effects: no
    /// usage, auto-create, evaluation metric or store write.</item>
    /// <item><c>POST {path}/flags/{key}/archive</c>, <c>POST {path}/flags/{key}/restore</c>, <c>DELETE {path}/flags/{key}</c> (purge).</item>
    /// <item><c>GET {path}/flags/{key}/audit</c>: audit history, newest first.</item>
    /// </list>
    /// Writes to an existing flag need the version the caller last saw, as an <c>If-Match</c> header (the flag's <c>ETag</c>)
    /// or a <c>version</c> body field; a stale version is answered with <c>409</c> problem details carrying <c>currentVersion</c>.
    /// Invalid requests get <c>400</c> problem details; a targeting rule that does not compile is one, titled
    /// <c>Invalid targeting rule</c> and carrying the flag <c>key</c>, the failing <c>operator</c> and a JSON Pointer
    /// <c>path</c> to it within the rule.
    /// <para>
    /// Security: unless authorization is added to the returned builder (e.g. <c>.RequireAuthorization("flag-admins")</c>),
    /// the endpoints only serve in the <c>Development</c> environment and answer <c>404</c> elsewhere, so an unauthenticated
    /// management API cannot be exposed in production by accident. Writes are attributed (audit actor, <c>updatedBy</c>) to
    /// the request's <c>User.Identity.Name</c> unless an explicit <see cref="Flagon.FlagonActor"/> scope is active.
    /// </para>
    /// </summary>
    /// <param name="endpoints">The app's endpoint route builder.</param>
    /// <param name="path">The path to mount the API at. Defaults to <see cref="DefaultPath"/>.</param>
    /// <returns>A builder that applies conventions (authorization, CORS, rate limiting, ...) to every management endpoint.</returns>
    public static IEndpointConventionBuilder MapFlagonApi(this IEndpointRouteBuilder endpoints, string? path = null)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var group = endpoints.MapGroup(NormalizePath(path));
        group.AddEndpointFilter(ManagementEndpointFilter.InvokeAsync);

        group.MapGet("/flags", ManagementEndpoints.ListAsync);
        group.MapPost("/flags", ManagementEndpoints.CreateAsync);
        group.MapGet("/flags/{key}", ManagementEndpoints.GetAsync);
        group.MapPatch("/flags/{key}", ManagementEndpoints.UpdateMetadataAsync);
        group.MapPut("/flags/{key}/enabled", ManagementEndpoints.SetEnabledAsync);
        group.MapPut("/flags/{key}/targeting", ManagementEndpoints.SetTargetingAsync);
        group.MapDelete("/flags/{key}/targeting", ManagementEndpoints.ClearTargetingAsync);
        group.MapPost("/flags/{key}/evaluate", ManagementEndpoints.EvaluateAsync);
        group.MapPost("/flags/{key}/archive", ManagementEndpoints.ArchiveAsync);
        group.MapPost("/flags/{key}/restore", ManagementEndpoints.RestoreAsync);
        group.MapDelete("/flags/{key}", ManagementEndpoints.PurgeAsync);
        group.MapGet("/flags/{key}/audit", ManagementEndpoints.GetAuditHistoryAsync);

        return group;
    }

    private static string NormalizePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return DefaultPath;
        }

        var trimmed = path.Trim().TrimEnd('/');
        return trimmed.StartsWith('/') ? trimmed : "/" + trimmed;
    }
}
