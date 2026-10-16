using Microsoft.AspNetCore.Http;

namespace Flagon.AspNetCore.Internal;

/// <summary>Resolves the ambient context from the current request, once per request.</summary>
internal sealed class HttpContextEvaluationContextAccessor(
    IHttpContextAccessor httpContextAccessor,
    IEvaluationContextResolver resolver) : IEvaluationContextAccessor
{
    private static readonly object ItemKey = new();

    public EvaluationContext? Current
    {
        get
        {
            var httpContext = httpContextAccessor.HttpContext;
            if (httpContext is null)
            {
                return null;
            }

            if (httpContext.Items.TryGetValue(ItemKey, out var cached))
            {
                return (EvaluationContext?)cached;
            }

            var context = resolver.Resolve(httpContext);
            httpContext.Items[ItemKey] = context;
            return context;
        }
    }
}
