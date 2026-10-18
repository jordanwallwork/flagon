namespace Flagon.Testing;

/// <summary>
/// Test-safe overrides of <see cref="FlagonOptions"/>, applied after the app's own configuration. Background behaviour
/// that would race a test's assertions is off by default; <see cref="FlagonOptions.FailFastOnStartup"/> is always on, so
/// a broken setup fails the test instead of starting with code defaults.
/// </summary>
public sealed class FlagonTestingSwitches
{
    /// <summary>
    /// When true, evaluating a key that does not exist creates it in the background, as in production
    /// (<see cref="FlagonOptions.AutoCreate"/>). Off by default: the create races the test's assertions.
    /// </summary>
    public bool AutoCreate { get; set; }

    /// <summary>
    /// When true, usage is recorded as in production (<see cref="FlagonOptions.UsageTracking"/>) and written to the
    /// store when the test flagon is disposed. Off by default.
    /// </summary>
    public bool UsageTracking { get; set; }

    /// <summary>
    /// When true (the default), the flags declared with <c>Define</c> are seeded at startup, so tests start from the
    /// app's declared defaults. When false, the test flagon starts empty.
    /// </summary>
    public bool SeedDefinitions { get; set; } = true;
}
