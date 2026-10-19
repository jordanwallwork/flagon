// Shared source: compiled into each storage package (linked via <Compile Include=... Link=...>), so every provider
// resolves "connection string or name" the same way without the core package depending on configuration.
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Jorrk.Flagon.Storage.Internal;

/// <summary>Resolves the <c>connectionStringOrName</c> argument of the <c>Use*Storage</c> extensions.</summary>
internal static class ConnectionStringResolver
{
    /// <summary>The argument guard every <c>Use*Storage(connectionStringOrName, ...)</c> applies up front.</summary>
    public static void Guard(string connectionStringOrName, string paramName = "connectionStringOrName") =>
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionStringOrName, paramName);

    /// <summary>
    /// A connection string configured under <c>ConnectionStrings:{value}</c> wins; otherwise a value containing
    /// <c>=</c> is used as a literal connection string; otherwise there is nothing to connect to, which is an error.
    /// </summary>
    /// <exception cref="InvalidOperationException">The value is neither a configured name nor a connection string.</exception>
    public static string Resolve(IServiceProvider services, string connectionStringOrName)
    {
        var named = services.GetService<IConfiguration>()?.GetConnectionString(connectionStringOrName);
        if (!string.IsNullOrWhiteSpace(named))
        {
            return named;
        }

        if (connectionStringOrName.Contains('=', StringComparison.Ordinal))
        {
            return connectionStringOrName;
        }

        throw new InvalidOperationException(
            $"Flagon could not find a connection string named '{connectionStringOrName}' in configuration " +
            $"(ConnectionStrings:{connectionStringOrName}), and the value is not itself a connection string (it contains no '=').");
    }
}
