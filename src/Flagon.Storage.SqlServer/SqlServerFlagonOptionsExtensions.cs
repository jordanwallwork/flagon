using Flagon;
using Flagon.Storage.SqlServer;
using Microsoft.Extensions.Configuration;

// ReSharper disable once CheckNamespace
namespace Microsoft.Extensions.DependencyInjection;

/// <summary>Configures Flagon to store flags in SQL Server.</summary>
public static class SqlServerFlagonOptionsExtensions
{
    /// <summary>
    /// Stores flags in SQL Server. Flagon's tables are created in <paramref name="schema"/> on startup unless
    /// <see cref="FlagonOptions.AutoMigrate"/> is off (see <see cref="SqlServerMigrations.GenerateScript"/>).
    /// </summary>
    /// <param name="options">The Flagon options.</param>
    /// <param name="connectionStringOrName">
    /// A connection string, or the name of one in configuration (<c>ConnectionStrings:{name}</c>). A value that names
    /// a configured connection string is used as a name; otherwise a value containing <c>=</c> is used as a connection string.
    /// </param>
    /// <param name="schema">The schema holding Flagon's tables (letters, digits, underscores; default <c>flagon</c>).</param>
    /// <exception cref="ArgumentException">The schema name is invalid.</exception>
    public static FlagonOptions UseSqlServerStorage(this FlagonOptions options, string connectionStringOrName, string schema = SqlServerMigrations.DefaultSchema)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrEmpty(connectionStringOrName);
        SqlServerMigrations.ValidateSchema(schema);
        return options.UseStorage(sp => new SqlServerFlagStore(ResolveConnectionString(sp, connectionStringOrName), schema));
    }

    private static string ResolveConnectionString(IServiceProvider services, string connectionStringOrName)
    {
        var named = services.GetService<IConfiguration>()?.GetConnectionString(connectionStringOrName);
        if (!string.IsNullOrEmpty(named))
        {
            return named;
        }

        if (connectionStringOrName.Contains('=', StringComparison.Ordinal))
        {
            return connectionStringOrName;
        }

        throw new InvalidOperationException(
            $"Flagon SQL Server storage: no connection string named '{connectionStringOrName}' was found in configuration (ConnectionStrings:{connectionStringOrName}).");
    }
}
