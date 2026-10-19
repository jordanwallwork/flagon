using System.Globalization;

namespace Jorrk.Flagon.Storage.Postgres;

/// <summary>
/// The payload of a change notification: <c>{version}:{commit time in epoch milliseconds}</c>. The time is the database
/// clock (<c>clock_timestamp()</c>) when the write notified, so it is not affected by clock skew between app hosts.
/// </summary>
internal static class PostgresChangePayload
{
    /// <summary>Parses a payload, returning false (never throwing) for anything malformed.</summary>
    public static bool TryParse(string? payload, out long version, out DateTimeOffset commitTime)
    {
        version = 0;
        commitTime = default;
        if (payload is null)
        {
            return false;
        }

        var parts = payload.Split(':');
        if (parts.Length != 2
            || !long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var parsedVersion)
            || !long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var epochMilliseconds)
            || epochMilliseconds > DateTimeOffset.MaxValue.ToUnixTimeMilliseconds())
        {
            return false;
        }

        version = parsedVersion;
        commitTime = DateTimeOffset.FromUnixTimeMilliseconds(epochMilliseconds);
        return true;
    }
}
