using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Jorrk.Flagon.Internal;

/// <summary>
/// Flagon's metrics and traces (see <see cref="FlagonDiagnostics"/> for the published names). One per container:
/// the meter comes from the host's <see cref="IMeterFactory"/> so hosts sharing a process keep separate instruments.
/// </summary>
/// <remarks>
/// New store operations (archive, purge, migrations, auto-create, seeding...) should go through
/// <see cref="TraceStoreOperationAsync{T}"/> so they get an activity, error status and the store-error counter for free.
/// </remarks>
internal sealed class FlagonInstrumentation : IDisposable
{
    /// <summary>Process-wide, as activity sources are global by nature.</summary>
    public static readonly ActivitySource ActivitySource = new(FlagonDiagnostics.ActivitySourceName);

    private readonly Meter? _ownedMeter;
    private readonly Counter<long> _evaluations;
    private readonly Histogram<double> _refreshDuration;
    private readonly Counter<long> _storeErrors;
    private readonly Counter<long> _pushNotifications;

    public FlagonInstrumentation(IMeterFactory? meterFactory = null)
    {
        Meter meter;
        if (meterFactory is null)
        {
            // Bare ServiceCollection without AddMetrics(): own a plain meter.
            meter = _ownedMeter = new Meter(FlagonDiagnostics.MeterName);
        }
        else
        {
            meter = meterFactory.Create(FlagonDiagnostics.MeterName);
        }

        _evaluations = meter.CreateCounter<long>("flagon.evaluations", "{evaluation}", "Flag evaluations.");
        _refreshDuration = meter.CreateHistogram<double>("flagon.refresh.duration", "s", "Duration of snapshot refreshes against the flag store.");
        _storeErrors = meter.CreateCounter<long>("flagon.store.errors", "{error}", "Flag store operations that failed.");
        _pushNotifications = meter.CreateCounter<long>("flagon.push.notifications", "{notification}", "Change signals pushed by the flag store, tagged by outcome (received or coalesced).");
    }

    /// <summary>Counts one evaluation. Allocation-free when nobody listens.</summary>
    public void RecordEvaluation<T>(FlagEvaluationDetails<T> details)
    {
        if (!_evaluations.Enabled)
        {
            return;
        }

        var tags = new TagList
        {
            { "feature_flag.key", details.FlagKey },
            { "feature_flag.result.reason", ReasonTag(details.Reason) },
        };
        if (details.Variant is not null)
        {
            tags.Add("feature_flag.result.variant", details.Variant);
        }

        if (details.ErrorCode != ErrorCode.None)
        {
            tags.Add("error.type", ErrorTag(details.ErrorCode));
        }

        _evaluations.Add(1, tags);
    }

    /// <summary>Records one snapshot refresh; a failed one is also counted as a store error.</summary>
    public void RecordRefresh(TimeSpan duration, RefreshOutcome outcome, Exception? error = null)
    {
        _refreshDuration.Record(duration.TotalSeconds, new KeyValuePair<string, object?>("flagon.refresh.outcome", OutcomeTag(outcome)));
        if (error is not null)
        {
            RecordStoreError("refresh", error);
        }
    }

    /// <summary>
    /// Counts one push signal from the store: <c>received</c> when it was queued for a refresh, <c>coalesced</c> when it
    /// was absorbed into a refresh already queued.
    /// </summary>
    public void RecordPushNotification(bool coalesced) =>
        _pushNotifications.Add(1, new KeyValuePair<string, object?>("flagon.push.outcome", coalesced ? "coalesced" : "received"));

    /// <summary>Counts a failed store operation (an infrastructure failure, not a domain outcome such as a version conflict).</summary>
    public void RecordStoreError(string operation, Exception error) =>
        _storeErrors.Add(1,
            new KeyValuePair<string, object?>("flagon.store.operation", operation),
            new KeyValuePair<string, object?>("error.type", error.GetType().FullName));

    /// <summary>Starts an activity named <c>flagon.{operation}</c>, or returns null when nobody listens.</summary>
    public static Activity? StartActivity(string operation, string? flagKey = null)
    {
        var activity = ActivitySource.StartActivity("flagon." + operation, ActivityKind.Internal);
        if (activity is not null && flagKey is not null)
        {
            activity.SetTag("feature_flag.key", flagKey);
        }

        return activity;
    }

    /// <summary>Marks <paramref name="activity"/> as failed with <paramref name="error"/>.</summary>
    public static void SetError(Activity? activity, Exception error)
    {
        if (activity is null)
        {
            return;
        }

        activity.SetStatus(ActivityStatusCode.Error, error.Message);
        activity.SetTag("error.type", error.GetType().FullName);
    }

    /// <summary>
    /// Runs a store-backed operation inside a <c>flagon.{operation}</c> activity. Any failure marks the activity as an
    /// error; failures that are not Flagon domain outcomes (<see cref="FlagonException"/>) or cancellation are also
    /// counted on <c>flagon.store.errors</c>.
    /// </summary>
    public async Task<T> TraceStoreOperationAsync<T>(string operation, string? flagKey, Func<Activity?, Task<T>> body)
    {
        using var activity = StartActivity(operation, flagKey);
        try
        {
            return await body(activity).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            SetError(activity, ex);
            if (ex is not (FlagonException or OperationCanceledException))
            {
                RecordStoreError(operation, ex);
            }

            throw;
        }
    }

    public void Dispose() => _ownedMeter?.Dispose();

    // OpenTelemetry's feature_flag.result.reason / error.type values are the OpenFeature names in lower snake case.
    // Precomputed from the shared wire names so the hot path does not allocate.
    private static readonly string[] ReasonTags = LowerWireNames<EvaluationReason>(r => r.ToWireName());
    private static readonly string[] ErrorTags = LowerWireNames<ErrorCode>(c => c.ToWireName());

    private static string ReasonTag(EvaluationReason reason) =>
        (uint)reason < (uint)ReasonTags.Length ? ReasonTags[(int)reason] : "unknown";

    private static string ErrorTag(ErrorCode code) =>
        (uint)code < (uint)ErrorTags.Length ? ErrorTags[(int)code] : "general";

    private static string[] LowerWireNames<TEnum>(Func<TEnum, string> wireName)
        where TEnum : struct, Enum
    {
        var values = Enum.GetValues<TEnum>();
        var names = new string[values.Max(v => Convert.ToInt32(v, System.Globalization.CultureInfo.InvariantCulture)) + 1];
        foreach (var value in values)
        {
            names[Convert.ToInt32(value, System.Globalization.CultureInfo.InvariantCulture)] = wireName(value).ToLowerInvariant();
        }

        return names;
    }

    public static string OutcomeTag(RefreshOutcome outcome) => outcome switch
    {
        RefreshOutcome.Unchanged => "unchanged",
        RefreshOutcome.Reloaded => "reloaded",
        _ => "failed",
    };
}

/// <summary>What a snapshot refresh did.</summary>
internal enum RefreshOutcome
{
    Unchanged,
    Reloaded,
    Failed,
}
