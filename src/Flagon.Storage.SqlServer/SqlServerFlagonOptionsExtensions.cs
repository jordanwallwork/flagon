using Flagon.Storage.Internal;
using Flagon.Storage.SqlServer;

// ReSharper disable once CheckNamespace
namespace Flagon;

/// <summary>Configures Flagon to store flags in SQL Server.</summary>
public static class SqlServerFlagonOptionsExtensions
{
    /// <summary>
    /// Stores flags in SQL Server. Flagon's tables are created in <paramref name="schema"/> on startup unless
    /// <see cref="FlagonOptions.AutoMigrate"/> is off (see <see cref="SqlServerMigrations.GenerateScript"/>).
    /// </summary>
    /// <param name="options">The Flagon options.</param>
    /// <param name="connectionStringOrName">
    /// The name of a connection string in the app's configuration (<c>ConnectionStrings:{name}</c>), or a SQL Server
    /// connection string. A configured name wins; otherwise a value containing <c>=</c> is used as the connection string.
    /// Resolved when the store is first created; a value that is neither fails with <see cref="InvalidOperationException"/>.
    /// </param>
    /// <param name="schema">The schema holding Flagon's tables (letters, digits, underscores; default <c>flagon</c>).</param>
    /// <exception cref="ArgumentException"><paramref name="connectionStringOrName"/> is null or blank, or the schema name is invalid.</exception>
    public static FlagonOptions UseSqlServerStorage(this FlagonOptions options, string connectionStringOrName, string schema = SqlServerMigrations.DefaultSchema)
    {
        ArgumentNullException.ThrowIfNull(options);
        ConnectionStringResolver.Guard(connectionStringOrName);
        SqlServerMigrations.ValidateSchema(schema);
        return options.UseStorage(sp => new SqlServerFlagStore(ConnectionStringResolver.Resolve(sp, connectionStringOrName), schema));
    }
}
