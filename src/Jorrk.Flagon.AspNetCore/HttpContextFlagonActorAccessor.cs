using Microsoft.AspNetCore.Http;

namespace Jorrk.Flagon.AspNetCore;

/// <summary>
/// An <see cref="IFlagonActorAccessor"/> that attributes <see cref="IFlagonAdmin"/> writes to the current HTTP request's
/// authenticated user (<c>User.Identity.Name</c>). An explicit <see cref="FlagonActor.Use"/> scope still wins.
/// Register it with <c>services.AddFlagonHttpContextActor()</c>.
/// </summary>
public sealed class HttpContextFlagonActorAccessor(IHttpContextAccessor httpContextAccessor) : IFlagonActorAccessor
{
    /// <inheritdoc />
    public string? Actor => FlagonActor.Current ?? UserName(httpContextAccessor.HttpContext);

    internal static string? UserName(HttpContext? http) =>
        http?.User.Identity is { IsAuthenticated: true, Name: { Length: > 0 } name } ? name : null;
}
