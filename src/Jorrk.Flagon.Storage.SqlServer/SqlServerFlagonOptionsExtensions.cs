using Jorrk.Flagon.Storage.Internal;
using Jorrk.Flagon.Storage.SqlServer;
using Microsoft.Extensions.Logging;

// ReSharper disable once CheckNamespace
namespace Jorrk.Flagon;

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
    /// <param name="push">
    /// Push changes to apps with query notifications (<see cref="SqlServerPushFlagStore"/>) instead of only polling. Off by
    /// default: it needs Service Broker and a <c>db_owner</c> login, and is unavailable on Azure SQL Database.
    /// </param>
    /// <exception cref="ArgumentException"><paramref name="connectionStringOrName"/> is null or blank, or the schema name is invalid.</exception>
    public static FlagonOptions UseSqlServerStorage(this FlagonOptions options, string connectionStringOrName, string schema = SqlServerMigrations.DefaultSchema, bool push = false)
    {
        ArgumentNullException.ThrowIfNull(options);
        ConnectionStringResolver.Guard(connectionStringOrName);
        SqlServerMigrations.ValidateSchema(schema);
        return options.UseStorage(sp =>
        {
            var connectionString = ConnectionStringResolver.Resolve(sp, connectionStringOrName);
            return push
                ? new SqlServerPushFlagStore(connectionString, schema, (sp.GetService(typeof(ILoggerFactory)) as ILoggerFactory)?.CreateLogger<SqlServerPushFlagStore>())
                : new SqlServerFlagStore(connectionString, schema);
        });
    }
}
