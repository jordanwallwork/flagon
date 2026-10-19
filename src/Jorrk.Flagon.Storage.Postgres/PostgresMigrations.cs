using System.Reflection;
using System.Text.RegularExpressions;

namespace Jorrk.Flagon.Storage.Postgres;

/// <summary>One versioned PostgreSQL migration script, ready to run.</summary>
/// <param name="Version">The schema version the script installs (scripts run in ascending order).</param>
/// <param name="Name">The script name, e.g. <c>0001_initial</c>.</param>
/// <param name="Sql">
/// The complete script for one schema, in one transaction: it creates the schema and its <c>schema_version</c> table if
/// missing, applies the change idempotently and records <paramref name="Version"/> in <c>schema_version</c>.
/// </param>
public sealed record PostgresMigrationScript(int Version, string Name, string Sql);

/// <summary>The embedded migration scripts of <see cref="PostgresFlagStore"/>.</summary>
internal static partial class PostgresMigrations
{
    private const string SchemaToken = "__schema__";
    private const string ResourcePrefix = "Jorrk.Flagon.Storage.Postgres.Migrations.";

    private static readonly Lazy<IReadOnlyList<(int Version, string Name, string Body)>> Templates = new(LoadTemplates);

    public static void ValidateSchema(string schema)
    {
        ArgumentNullException.ThrowIfNull(schema);
        if (!SchemaPattern().IsMatch(schema))
        {
            throw new ArgumentException(
                $"'{schema}' is not a valid Flagon schema name: use letters, digits and underscores (max 63 characters), starting with a letter or underscore.",
                nameof(schema));
        }
    }

    /// <summary>Creates the schema and its version table (run before reading the installed versions).</summary>
    public static string Preamble(string schema) => $"""
        CREATE SCHEMA IF NOT EXISTS "{schema}";
        CREATE TABLE IF NOT EXISTS "{schema}".schema_version (
            version     integer      NOT NULL PRIMARY KEY,
            name        text         NOT NULL,
            applied_at  timestamptz  NOT NULL DEFAULT now()
        );
        """;

    /// <summary>
    /// The exported scripts: each runs in its own transaction (<c>BEGIN</c> ... <c>COMMIT</c>), so a script that fails part
    /// way leaves nothing behind (in <c>psql</c>, the failed transaction's <c>COMMIT</c> rolls back) and can be run again.
    /// </summary>
    public static IReadOnlyList<PostgresMigrationScript> For(string schema) =>
        ForRunner(schema)
            .Select(s => s with { Sql = $"BEGIN;\n\n{s.Sql}\n\nCOMMIT;\n" })
            .ToArray();

    /// <summary>The scripts without transaction control, for the store's own runner (which wraps each in a transaction).</summary>
    public static IReadOnlyList<PostgresMigrationScript> ForRunner(string schema)
    {
        ValidateSchema(schema);
        return Templates.Value
            .Select(t => new PostgresMigrationScript(t.Version, t.Name, $"""
                {Preamble(schema)}

                {t.Body.Replace(SchemaToken, schema, StringComparison.Ordinal)}

                INSERT INTO "{schema}".schema_version (version, name) VALUES ({t.Version}, '{t.Name}') ON CONFLICT (version) DO NOTHING;
                """))
            .ToArray();
    }

    private static IReadOnlyList<(int Version, string Name, string Body)> LoadTemplates()
    {
        var assembly = typeof(PostgresMigrations).Assembly;
        return assembly.GetManifestResourceNames()
            .Where(n => n.StartsWith(ResourcePrefix, StringComparison.Ordinal) && n.EndsWith(".sql", StringComparison.Ordinal))
            .Select(n =>
            {
                var name = n[ResourcePrefix.Length..^".sql".Length];
                var version = int.Parse(name.AsSpan(0, name.IndexOf('_', StringComparison.Ordinal)), System.Globalization.CultureInfo.InvariantCulture);
                return (version, name, Read(assembly, n));
            })
            .OrderBy(t => t.version)
            .ToArray();
    }

    private static string Read(Assembly assembly, string resource)
    {
        using var stream = assembly.GetManifestResourceStream(resource)
            ?? throw new InvalidOperationException($"Missing embedded migration '{resource}'.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_]{0,62}\z")]
    private static partial Regex SchemaPattern();
}
