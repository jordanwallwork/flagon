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
    /// <item><c>GET {path}/flags/{key}</c>: one flag (with an <c>ETag</c> of its version).</item>
    /// <item><c>POST {path}/flags</c>: creates a flag from <c>{ key, enabled?, description?, tags? }</c>.</item>
    /// <item><c>PATCH {path}/flags/{key}</c>: updates <c>{ description?, tags? }</c> (omitted fields are unchanged).</item>
    /// <item><c>PUT {path}/flags/{key}/enabled</c>: toggles the flag from <c>{ enabled }</c>.</item>
    /// <item><c>POST {path}/flags/{key}/archive</c>, <c>POST {path}/flags/{key}/restore</c>, <c>DELETE {path}/flags/{key}</c> (purge).</item>
    /// <item><c>GET {path}/flags/{key}/audit</c>: audit history, newest first.</item>
    /// </list>
    /// Writes to an existing flag need the version the caller last saw, as an <c>If-Match</c> header (the flag's <c>ETag</c>)
    /// or a <c>version</c> body field; a stale version is answered with <c>409</c> problem details carrying <c>currentVersion</c>.
    /// Invalid requests get <c>400</c> problem details.
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
