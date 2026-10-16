using Flagon;
using Flagon.AspNetCore;
using Flagon.AspNetCore.Internal;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection.Extensions;

// ReSharper disable once CheckNamespace
namespace Microsoft.Extensions.DependencyInjection;

/// <summary>Registers Flagon's ASP.NET Core services.</summary>
public static class FlagonAspNetCoreServiceCollectionExtensions
{
    /// <summary>
    /// Makes <see cref="IEvaluationContextAccessor"/> resolve the ambient evaluation context from the current request using
    /// the registered <see cref="IEvaluationContextResolver"/> (by default <see cref="UserEvaluationContextResolver"/>,
    /// which targets the signed-in user's id).
    /// </summary>
    public static IServiceCollection AddFlagonAmbientContext(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddHttpContextAccessor();
        services.TryAddSingleton<IEvaluationContextResolver, UserEvaluationContextResolver>();
        services.Replace(ServiceDescriptor.Singleton<IEvaluationContextAccessor, HttpContextEvaluationContextAccessor>());
        return services;
    }

    /// <summary>
    /// Like <see cref="AddFlagonAmbientContext(IServiceCollection)"/>, resolving the ambient context with
    /// <typeparamref name="TResolver"/> (a singleton).
    /// </summary>
    public static IServiceCollection AddFlagonAmbientContext<TResolver>(this IServiceCollection services)
        where TResolver : class, IEvaluationContextResolver
    {
        ArgumentNullException.ThrowIfNull(services);

        services.Replace(ServiceDescriptor.Singleton<IEvaluationContextResolver, TResolver>());
        return services.AddFlagonAmbientContext();
    }

    /// <summary>
    /// Like <see cref="AddFlagonAmbientContext(IServiceCollection)"/>, resolving the ambient context with
    /// <paramref name="resolve"/>.
    /// </summary>
    public static IServiceCollection AddFlagonAmbientContext(this IServiceCollection services, Func<HttpContext, EvaluationContext?> resolve)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(resolve);

        services.Replace(ServiceDescriptor.Singleton<IEvaluationContextResolver>(new DelegateEvaluationContextResolver(resolve)));
        return services.AddFlagonAmbientContext();
    }

    private sealed class DelegateEvaluationContextResolver(Func<HttpContext, EvaluationContext?> resolve) : IEvaluationContextResolver
    {
        public EvaluationContext? Resolve(HttpContext httpContext) => resolve(httpContext);
    }
}
