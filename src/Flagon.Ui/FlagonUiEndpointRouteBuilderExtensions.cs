using Flagon.Ui.Internal;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

// ReSharper disable once CheckNamespace
namespace Microsoft.AspNetCore.Builder;

/// <summary>Mounts Flagon's management console.</summary>
public static class FlagonUiEndpointRouteBuilderExtensions
{
    /// <summary>The path <see cref="MapFlagonUi"/> mounts the console at when none is given.</summary>
    public const string DefaultPath = "/flagon";

    /// <summary>
    /// Mounts the management console at <paramref name="path"/>: a plain HTML/JavaScript page (embedded in this package,
    /// no Node build) for finding, toggling, editing, archiving and auditing flags. The console talks only to the
    /// management API, which this call maps for it at <c>{path}/api</c> (see
    /// <see cref="FlagonManagementEndpointRouteBuilderExtensions.MapFlagonApi"/>); don't map the API yourself at that same path.
    /// <para>
    /// Security: conventions applied to the returned builder cover both the page and its API, so
    /// <c>app.MapFlagonUi("/flags").RequireAuthorization("flag-admins")</c> protects the whole console. Without
    /// authorization the console (page and API) is only served in the <c>Development</c> environment and answers
    /// <c>404</c> elsewhere, the same rule as the management API.
    /// </para>
    /// </summary>
    /// <param name="endpoints">The app's endpoint route builder.</param>
    /// <param name="path">The path to serve the console at, e.g. <c>"/flags"</c>. Defaults to <see cref="DefaultPath"/>.</param>
    /// <returns>A builder that applies conventions (authorization, rate limiting, ...) to the console page and its API.</returns>
    public static IEndpointConventionBuilder MapFlagonUi(this IEndpointRouteBuilder endpoints, string? path = null)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var group = endpoints.MapGroup(NormalizePath(path));
        group.MapFlagonApi("/api");
        group.MapGet("/{**asset}", UiAssets.ServeAsync).AddEndpointFilter(UiAssets.GateAsync).ExcludeFromDescription();
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
