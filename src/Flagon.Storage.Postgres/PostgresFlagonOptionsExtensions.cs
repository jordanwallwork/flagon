using Flagon.Storage.Internal;
using Flagon.Storage.Postgres;

// ReSharper disable once CheckNamespace
namespace Flagon;

/// <summary>Configures Flagon to store flags in PostgreSQL.</summary>
public static class PostgresFlagonOptionsExtensions
{
    /// <summary>
    /// Stores flags in PostgreSQL via <see cref="PostgresFlagStore"/>. Its schema is created and upgraded on startup
    /// unless <see cref="FlagonOptions.AutoMigrate"/> is turned off.
    /// </summary>
    /// <param name="options">The Flagon options.</param>
    /// <param name="connectionStringOrName">
    /// The name of a connection string in the app's configuration (<c>ConnectionStrings:{name}</c>), or an Npgsql
    /// connection string. A configured name wins; otherwise a value containing <c>=</c> is used as the connection string.
    /// Resolved when the store is first created; a value that is neither fails with <see cref="InvalidOperationException"/>.
    /// </param>
    /// <param name="schema">The schema holding Flagon's tables (default <c>flagon</c>).</param>
    /// <exception cref="ArgumentException"><paramref name="connectionStringOrName"/> is null or blank.</exception>
    public static FlagonOptions UsePostgresStorage(this FlagonOptions options, string connectionStringOrName, string schema = PostgresFlagStore.DefaultSchema)
    {
        ArgumentNullException.ThrowIfNull(options);
        ConnectionStringResolver.Guard(connectionStringOrName);
        ArgumentNullException.ThrowIfNull(schema);

        return options.UseStorage(services => new PostgresFlagStore(ConnectionStringResolver.Resolve(services, connectionStringOrName), schema));
    }
}
