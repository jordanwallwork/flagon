using Flagon.AspNetCore.Internal;
using Microsoft.AspNetCore.Http;

namespace Flagon.AspNetCore.Management;

/// <summary>
/// Runs around every management endpoint: refuses to serve outside Development unless authorization is configured,
/// attributes writes to the request's user, and turns Flagon exceptions into problem details.
/// </summary>
internal static class ManagementEndpointFilter
{
    public static async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        if (!ManagementGate.IsOpen(http))
        {
            // Look like nothing is mapped here, rather than advertising an unprotected management API.
            return Results.NotFound();
        }

        using var actor = FlagonActor.Current is null && HttpContextFlagonActorAccessor.UserName(http) is { } name
            ? FlagonActor.Use(name)
            : null;

        try
        {
            return await next(context).ConfigureAwait(false);
        }
        catch (FlagValidationException ex)
        {
            return Problem(StatusCodes.Status400BadRequest, "Invalid request", ex.Message);
        }
        catch (FlagNotFoundException ex)
        {
            return Problem(StatusCodes.Status404NotFound, "Flag not found", ex.Message);
        }
        catch (FlagAlreadyExistsException ex)
        {
            return Problem(StatusCodes.Status409Conflict, "Flag already exists", ex.Message);
        }
        catch (FlagVersionConflictException ex)
        {
            http.Response.Headers.ETag = ManagementEndpoints.FormatETag(ex.CurrentVersion);
            return Problem(StatusCodes.Status409Conflict, "Version conflict", ex.Message, new Dictionary<string, object?>
            {
                ["key"] = ex.Key,
                ["expectedVersion"] = ex.ExpectedVersion,
                ["currentVersion"] = ex.CurrentVersion,
            });
        }
    }

    private static IResult Problem(int status, string title, string detail, IDictionary<string, object?>? extensions = null) =>
        Results.Problem(detail: detail, statusCode: status, title: title, extensions: extensions);
}
