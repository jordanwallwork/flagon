// Shared source: compiled into each storage package that pushes change notifications (linked via <Compile Include=... Link=...>).
namespace Jorrk.Flagon.Storage.Internal;

/// <summary>The wait before a listener reconnect attempt: 1 second doubling per attempt, capped at 30 seconds, jittered.</summary>
internal static class ReconnectBackoff
{
    private static readonly double InitialMilliseconds = TimeSpan.FromSeconds(1).TotalMilliseconds;
    private static readonly double MaximumMilliseconds = TimeSpan.FromSeconds(30).TotalMilliseconds;

    /// <summary>
    /// The delay before attempt <paramref name="attempt"/> (zero-based). Equal jitter: a delay is drawn from the upper half of
    /// the capped exponential, so reconnects from many app instances spread out without ever waiting longer than the cap.
    /// </summary>
    public static TimeSpan Delay(int attempt, Random random)
    {
        var exponential = InitialMilliseconds * Math.Pow(2, Math.Min(Math.Max(attempt, 0), 30));
        var capped = Math.Min(MaximumMilliseconds, exponential);
        return TimeSpan.FromMilliseconds(capped * (0.5 + (random.NextDouble() / 2)));
    }
}
