using Flagon;
using Flagon.AspNetCore.Internal;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

// ReSharper disable once CheckNamespace
namespace Microsoft.AspNetCore.Builder;

/// <summary>Maps Flagon's OpenFeature Remote Evaluation Protocol (OFREP) endpoints.</summary>
public static class FlagonOfrepEndpointRouteBuilderExtensions
{
    /// <summary>
    /// Maps the OFREP evaluation endpoints, so OpenFeature SDKs in any language (using an OFREP provider whose base URL is
    /// this app plus <paramref name="prefix"/>) can evaluate this app's flags:
    /// <list type="bullet">
    /// <item><c>POST {prefix}/ofrep/v1/evaluate/flags/{key}</c>: evaluates one flag.</item>
    /// <item><c>POST {prefix}/ofrep/v1/evaluate/flags</c>: evaluates every flag; supports <c>ETag</c>/<c>If-None-Match</c>.</item>
    /// </list>
    /// The endpoints are public unless you add authorization to the returned builder, e.g. <c>.RequireAuthorization("flags")</c>.
    /// </summary>
    /// <param name="endpoints">The app's endpoint route builder.</param>
    /// <param name="prefix">An optional path the OFREP routes are mounted under, e.g. <c>"/flags"</c>. Defaults to the app root.</param>
    /// <returns>A builder that applies conventions (authorization, CORS, rate limiting, ...) to both endpoints.</returns>
    public static IEndpointConventionBuilder MapFlagonOfrep(this IEndpointRouteBuilder endpoints, string? prefix = null)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var group = endpoints.MapGroup(CombinePrefix(prefix) + "/ofrep/v1/evaluate/flags");
        group.MapPost("/{key}", (string key, HttpContext http, IFlagon flagon) => OfrepEndpoints.EvaluateAsync(key, http, flagon));
        group.MapPost("/", (HttpContext http, IFlagon flagon) => OfrepEndpoints.EvaluateAllAsync(http, flagon));
        return group;
    }

    private static string CombinePrefix(string? prefix)
    {
        if (string.IsNullOrWhiteSpace(prefix) || prefix == "/")
        {
            return string.Empty;
        }

        var trimmed = prefix.Trim().TrimEnd('/');
        return trimmed.StartsWith('/') ? trimmed : "/" + trimmed;
    }
}
