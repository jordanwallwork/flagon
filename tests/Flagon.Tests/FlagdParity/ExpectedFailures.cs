using System.Collections.Concurrent;

namespace Flagon.Tests.FlagdParity;

/// <summary>
/// Per-compiler expected-failure lists, <c>FlagdParity/expected-failures/&lt;compiler&gt;.txt</c>: one case id per line
/// (<c>&lt;feature path&gt;:&lt;line&gt;</c>); text after <c>#</c> is a comment. A missing file means "no expected failures".
/// </summary>
internal static class ExpectedFailures
{
    private static readonly ConcurrentDictionary<string, IReadOnlySet<string>> Cache = new(StringComparer.Ordinal);

    public static string FileName(string compiler) => $"expected-failures/{compiler}.txt";

    public static IReadOnlySet<string> For(string compiler) => Cache.GetOrAdd(compiler, static name =>
    {
        var path = Path.Combine(AppContext.BaseDirectory, "FlagdParity", "expected-failures", $"{name}.txt");
        if (!File.Exists(path))
        {
            return new HashSet<string>(StringComparer.Ordinal);
        }

        return File.ReadAllLines(path)
            .Select(line => (line.IndexOf('#') is var hash and >= 0 ? line[..hash] : line).Trim())
            .Where(id => id.Length > 0)
            .ToHashSet(StringComparer.Ordinal);
    });
}
