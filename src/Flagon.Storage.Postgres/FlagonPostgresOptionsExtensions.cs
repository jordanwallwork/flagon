using Flagon.Storage.Postgres;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

// ReSharper disable once CheckNamespace
namespace Flagon;

/// <summary>Configures Flagon to store flags in PostgreSQL.</summary>
public static class FlagonPostgresOptionsExtensions
{
    /// <summary>
    /// Stores flags in PostgreSQL via <see cref="PostgresFlagStore"/>. Its schema is created and upgraded on startup
    /// unless <see cref="FlagonOptions.AutoMigrate"/> is turned off.
    /// </summary>
    /// <param name="options">The Flagon options.</param>
    /// <param name="connectionStringOrName">
    /// An Npgsql connection string, or (when it contains no <c>=</c>) the name of a connection string in the app's
    /// <see cref="IConfiguration"/> (<c>ConnectionStrings:{name}</c>), resolved when the store is first created.
    /// </param>
    /// <param name="schema">The schema holding Flagon's tables (default <c>flagon</c>).</param>
    public static FlagonOptions UsePostgresStorage(this FlagonOptions options, string connectionStringOrName, string schema = PostgresFlagStore.DefaultSchema)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionStringOrName);
        ArgumentNullException.ThrowIfNull(schema);

        return options.UseStorage(services => new PostgresFlagStore(ResolveConnectionString(services, connectionStringOrName), schema));
    }

    private static string ResolveConnectionString(IServiceProvider services, string connectionStringOrName)
    {
        if (connectionStringOrName.Contains('=', StringComparison.Ordinal))
        {
            return connectionStringOrName;
        }

        return services.GetService<IConfiguration>()?.GetConnectionString(connectionStringOrName)
            ?? throw new InvalidOperationException(
                $"Flagon could not find a connection string named '{connectionStringOrName}' in configuration (ConnectionStrings:{connectionStringOrName}).");
    }
}
