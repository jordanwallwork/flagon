using System.Security.Claims;
using Microsoft.AspNetCore.Http;

namespace Flagon.AspNetCore;

/// <summary>
/// Builds the ambient <see cref="EvaluationContext"/> for a request. Register your own with
/// <c>services.AddFlagonAmbientContext&lt;TResolver&gt;()</c> or <c>services.AddFlagonAmbientContext(http =&gt; ...)</c>.
/// </summary>
public interface IEvaluationContextResolver
{
    /// <summary>Returns the context for <paramref name="httpContext"/>, or null for none. Called at most once per request.</summary>
    EvaluationContext? Resolve(HttpContext httpContext);
}

/// <summary>
/// The default resolver: the targeting key is the signed-in user's id, read from <see cref="HttpContext.User"/>'s
/// <see cref="ClaimTypes.NameIdentifier"/> claim, else its <c>sub</c> claim, else its identity name.
/// Anonymous requests get a context with no targeting key.
/// </summary>
public sealed class UserEvaluationContextResolver : IEvaluationContextResolver
{
    /// <inheritdoc />
    public EvaluationContext? Resolve(HttpContext httpContext)
    {
        ArgumentNullException.ThrowIfNull(httpContext);

        var user = httpContext.User;
        if (user.Identity?.IsAuthenticated != true)
        {
            return EvaluationContext.Empty;
        }

        var userId = user.FindFirst(ClaimTypes.NameIdentifier)?.Value
            ?? user.FindFirst("sub")?.Value
            ?? user.Identity.Name;
        return new EvaluationContext(userId);
    }
}
