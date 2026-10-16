namespace Flagon;

/// <summary>
/// Names of Flagon's telemetry sources, for subscribing with OpenTelemetry
/// (e.g. <c>metrics.AddMeter(FlagonDiagnostics.MeterName)</c>, <c>tracing.AddSource(FlagonDiagnostics.ActivitySourceName)</c>).
/// </summary>
/// <remarks>
/// <para>Metrics on the <see cref="MeterName"/> meter:</para>
/// <list type="bullet">
/// <item><description><c>flagon.evaluations</c> (counter, <c>{evaluation}</c>): one per evaluation, tagged
/// <c>feature_flag.key</c>, <c>feature_flag.result.reason</c> (OpenFeature reason in snake case, e.g. <c>static</c>),
/// <c>feature_flag.result.variant</c> (when a variant was served) and <c>error.type</c> (e.g. <c>flag_not_found</c>, on errors).</description></item>
/// <item><description><c>flagon.refresh.duration</c> (histogram, seconds): each snapshot refresh against the store, tagged
/// <c>flagon.refresh.outcome</c> (<c>unchanged</c>, <c>reloaded</c> or <c>failed</c>).</description></item>
/// <item><description><c>flagon.store.errors</c> (counter, <c>{error}</c>): store operations that failed, tagged
/// <c>flagon.store.operation</c> (e.g. <c>refresh</c>, <c>create</c>, <c>set_enabled</c>) and <c>error.type</c> (exception type).</description></item>
/// </list>
/// <para>Activities on the <see cref="ActivitySourceName"/> source are named <c>flagon.&lt;operation&gt;</c>
/// (<c>flagon.refresh</c>, <c>flagon.create</c>, <c>flagon.set_enabled</c>, <c>flagon.archive</c>, <c>flagon.restore</c>, <c>flagon.purge</c>, ...) and carry <c>feature_flag.key</c> when they concern one flag.
/// Failed operations set the activity status to <c>Error</c> and tag <c>error.type</c>.</para>
/// </remarks>
public static class FlagonDiagnostics
{
    /// <summary>The name of Flagon's <see cref="System.Diagnostics.Metrics.Meter"/>: <c>Flagon</c>.</summary>
    public const string MeterName = "Flagon";

    /// <summary>The name of Flagon's <see cref="System.Diagnostics.ActivitySource"/>: <c>Flagon</c>.</summary>
    public const string ActivitySourceName = "Flagon";
}
