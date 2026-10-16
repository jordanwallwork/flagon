using System.Text.Json;
using Flagon.Storage;

namespace Flagon;

/// <summary>
/// How a flag is being used across the applications that share the store: when each application last evaluated it and
/// the code default each one supplied. Usage is recorded per key, so a report can exist for a key that is not (or no
/// longer) a flag.
/// </summary>
/// <param name="Key">The flag key.</param>
/// <param name="Applications">One entry per application that has evaluated the key, ordered by application name (ordinal).</param>
public sealed record FlagUsageReport(string Key, IReadOnlyList<FlagUsage> Applications)
{
    /// <summary>The most recent evaluation by any application, or null if no application has evaluated the key.</summary>
    public DateTimeOffset? LastEvaluatedAt => Applications.Count == 0 ? null : Applications.Max(a => a.LastEvaluatedAt);

    /// <summary>
    /// True when applications have supplied different code defaults for this key, meaning they would behave differently
    /// if the flag were missing, archived or disabled. Applications with no observed default are ignored.
    /// </summary>
    public bool HasDefaultMismatch =>
        Applications
            .Where(a => a.ObservedDefault is { } d && d.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null))
            .Select(a => a.ObservedDefault!.Value.GetRawText())
            .Distinct(StringComparer.Ordinal)
            .Skip(1)
            .Any();

    internal static FlagUsageReport For(string key, IEnumerable<FlagUsage> usage) =>
        new(key, usage.OrderBy(u => u.ApplicationName, StringComparer.Ordinal).ToArray());
}
