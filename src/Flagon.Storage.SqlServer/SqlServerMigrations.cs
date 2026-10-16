using System.Text;
using System.Text.RegularExpressions;

namespace Flagon.Storage.SqlServer;

/// <summary>One versioned schema migration for the SQL Server store.</summary>
/// <param name="Version">The schema version this migration installs (1, 2, ...).</param>
/// <param name="Name">The script name (e.g. <c>0001_initial</c>).</param>
/// <param name="Sql">The migration's T-SQL, with the schema name substituted.</param>
public sealed record SqlServerMigration(int Version, string Name, string Sql);

/// <summary>
/// The SQL Server store's embedded, ordered migration scripts. Use <see cref="GenerateScript"/> to obtain an
/// idempotent deployment script when auto-migration is turned off (<see cref="FlagonOptions.AutoMigrate"/>).
/// </summary>
public static partial class SqlServerMigrations
{
    /// <summary>The schema Flagon uses when none is configured.</summary>
    public const string DefaultSchema = "flagon";

    private const string Placeholder = "$schema$";
    private const string ResourcePrefix = "Flagon.Storage.SqlServer.Migrations.";

    private static readonly Lazy<IReadOnlyList<(int Version, string Name, string Template)>> Templates = new(LoadTemplates);

    /// <summary>The migrations, in order, for <paramref name="schema"/>.</summary>
    /// <exception cref="ArgumentException">The schema name is not a plain identifier.</exception>
    public static IReadOnlyList<SqlServerMigration> GetMigrations(string schema = DefaultSchema)
    {
        ValidateSchema(schema);
        return Templates.Value.Select(t => new SqlServerMigration(t.Version, t.Name, t.Template.Replace(Placeholder, schema, StringComparison.Ordinal))).ToArray();
    }

    /// <summary>
    /// A complete, idempotent T-SQL deployment script (batches separated by <c>GO</c>) that creates the schema and
    /// applies every migration not yet recorded in its <c>schema_version</c> table — the same statements the store runs
    /// when it migrates itself.
    /// </summary>
    /// <exception cref="ArgumentException">The schema name is not a plain identifier.</exception>
    public static string GenerateScript(string schema = DefaultSchema)
    {
        var script = new StringBuilder();
        foreach (var batch in GetBatches(schema))
        {
            script.AppendLine(batch.TrimEnd()).AppendLine("GO").AppendLine();
        }

        return script.ToString();
    }

    /// <summary>The batches to run in order: the bootstrap, then each migration wrapped in its version guard.</summary>
    internal static IEnumerable<string> GetBatches(string schema)
    {
        var migrations = GetMigrations(schema);
        yield return $"""
            -- Flagon bootstrap: schema and schema_version table.
            IF SCHEMA_ID(N'{schema}') IS NULL EXEC(N'CREATE SCHEMA [{schema}]');
            IF OBJECT_ID(N'[{schema}].[schema_version]', N'U') IS NULL
                CREATE TABLE [{schema}].[schema_version] (
                    [version] int NOT NULL PRIMARY KEY,
                    [name] nvarchar(256) NOT NULL,
                    [applied_at] datetimeoffset(7) NOT NULL
                );
            """;

        foreach (var migration in migrations)
        {
            yield return $"""
                -- Flagon migration {migration.Name}
                IF NOT EXISTS (SELECT 1 FROM [{schema}].[schema_version] WHERE [version] = {migration.Version})
                BEGIN
                {migration.Sql.Trim()}

                INSERT INTO [{schema}].[schema_version] ([version], [name], [applied_at]) VALUES ({migration.Version}, N'{migration.Name}', SYSDATETIMEOFFSET());
                END
                """;
        }
    }

    internal static void ValidateSchema(string schema)
    {
        ArgumentException.ThrowIfNullOrEmpty(schema);
        if (!SchemaPattern().IsMatch(schema))
        {
            throw new ArgumentException(
                $"'{schema}' is not a valid Flagon schema name: use letters, digits and underscores, starting with a letter or underscore (max 128).",
                nameof(schema));
        }
    }

    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_]{0,127}\z")]
    private static partial Regex SchemaPattern();

    private static IReadOnlyList<(int Version, string Name, string Template)> LoadTemplates()
    {
        var assembly = typeof(SqlServerMigrations).Assembly;
        return assembly.GetManifestResourceNames()
            .Where(n => n.StartsWith(ResourcePrefix, StringComparison.Ordinal) && n.EndsWith(".sql", StringComparison.Ordinal))
            .Select(n =>
            {
                var name = n[ResourcePrefix.Length..^".sql".Length];
                using var stream = assembly.GetManifestResourceStream(n)!;
                using var reader = new StreamReader(stream);
                return (Version: int.Parse(name[..name.IndexOf('_', StringComparison.Ordinal)], System.Globalization.CultureInfo.InvariantCulture), Name: name, Template: reader.ReadToEnd());
            })
            .OrderBy(t => t.Version)
            .ToArray();
    }
}
