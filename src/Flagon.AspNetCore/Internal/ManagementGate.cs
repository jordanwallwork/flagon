using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Flagon.AspNetCore.Internal;

/// <summary>
/// The rule shared by the management API and the management UI (this file is compiled into both packages): an endpoint
/// is served when authorization is configured on it, or the app runs in the <c>Development</c> environment.
/// </summary>
internal static class ManagementGate
{
    /// <summary>True when the current endpoint may be served; otherwise the caller should answer 404.</summary>
    public static bool IsOpen(HttpContext http) =>
        IsAuthorizationConfigured(http) || http.RequestServices.GetRequiredService<IHostEnvironment>().IsDevelopment();

    /// <summary>
    /// True when the endpoint carries authorization requirements (e.g. from <c>RequireAuthorization(...)</c>) that are not
    /// cancelled by <c>AllowAnonymous()</c>.
    /// </summary>
    public static bool IsAuthorizationConfigured(HttpContext http)
    {
        var metadata = http.GetEndpoint()?.Metadata;
        if (metadata is null || metadata.GetMetadata<IAllowAnonymous>() is not null)
        {
            return false;
        }

        return metadata.GetMetadata<IAuthorizeData>() is not null || metadata.GetMetadata<AuthorizationPolicy>() is not null;
    }
}
