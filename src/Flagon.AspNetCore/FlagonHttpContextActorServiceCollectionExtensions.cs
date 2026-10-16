using Flagon;
using Flagon.AspNetCore;
using Microsoft.Extensions.DependencyInjection.Extensions;

// ReSharper disable once CheckNamespace
namespace Microsoft.Extensions.DependencyInjection;

/// <summary>Registers <see cref="HttpContextFlagonActorAccessor"/>.</summary>
public static class FlagonHttpContextActorServiceCollectionExtensions
{
    /// <summary>
    /// Attributes every <see cref="IFlagonAdmin"/> write made during an HTTP request (audit actor, <c>createdBy</c>/<c>updatedBy</c>)
    /// to the request's authenticated user, unless a <see cref="FlagonActor.Use"/> scope is active.
    /// May be called before or after <c>AddFlagon</c>. (The management API attributes its own writes without this.)
    /// </summary>
    public static IServiceCollection AddFlagonHttpContextActor(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddHttpContextAccessor();
        services.Replace(ServiceDescriptor.Singleton<IFlagonActorAccessor, HttpContextFlagonActorAccessor>());
        return services;
    }
}
