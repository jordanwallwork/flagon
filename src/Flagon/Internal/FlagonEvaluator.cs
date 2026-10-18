using System.Collections.Concurrent;
using System.Text.Json;
using Flagon.Internal.Targeting;
using Microsoft.Extensions.Logging;

namespace Flagon.Internal;

/// <summary>Evaluates flags against the current snapshot. No I/O.</summary>
internal sealed partial class FlagonEvaluator(
    FlagSnapshotManager snapshots,
    UsageTracker usage,
    FlagonInstrumentation instrumentation,
    ILogger<FlagonEvaluator> logger,
    FlagAutoCreator autoCreator,
    IEvaluationContextAccessor ambientContext,
    ITargetingCompiler compiler,
    TimeProvider time) : IFlagon
{
    private readonly ConcurrentDictionary<string, byte> _warnedMissingKeys = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, long> _warnedRuleFailures = new(StringComparer.Ordinal);

    public bool IsEnabled(string key, bool defaultValue = false, EvaluationContext? context = null) =>
        GetBooleanDetails(key, defaultValue, context).Value;

    public ValueTask<bool> IsEnabledAsync(string key, bool defaultValue = false, EvaluationContext? context = null, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(IsEnabled(key, defaultValue, context));

    public FlagEvaluationDetails<bool> GetBooleanDetails(string key, bool defaultValue = false, EvaluationContext? context = null)
    {
        usage.Record(key, defaultValue);
        var details = Evaluate(key, context ?? ambientContext.Current).AsBoolean(defaultValue);
        if (details.ErrorCode == ErrorCode.FlagNotFound)
        {
            WarnMissingOnce(details.FlagKey);
            autoCreator.Enqueue(details.FlagKey, defaultValue);
        }

        instrumentation.RecordEvaluation(details);
        return details;
    }

    public string GetString(string key, string defaultValue, EvaluationContext? context = null) =>
        GetStringDetails(key, defaultValue, context).Value;

    public ValueTask<string> GetStringAsync(string key, string defaultValue, EvaluationContext? context = null, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(GetString(key, defaultValue, context));

    public FlagEvaluationDetails<string> GetStringDetails(string key, string defaultValue, EvaluationContext? context = null) =>
        Typed(Evaluate(key, context ?? ambientContext.Current).AsString(defaultValue));

    public ValueTask<FlagEvaluationDetails<string>> GetStringDetailsAsync(string key, string defaultValue, EvaluationContext? context = null, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(GetStringDetails(key, defaultValue, context));

    public int GetInt(string key, int defaultValue, EvaluationContext? context = null) =>
        GetIntDetails(key, defaultValue, context).Value;

    public ValueTask<int> GetIntAsync(string key, int defaultValue, EvaluationContext? context = null, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(GetInt(key, defaultValue, context));

    public FlagEvaluationDetails<int> GetIntDetails(string key, int defaultValue, EvaluationContext? context = null) =>
        Typed(Evaluate(key, context ?? ambientContext.Current).AsInt(defaultValue));

    public ValueTask<FlagEvaluationDetails<int>> GetIntDetailsAsync(string key, int defaultValue, EvaluationContext? context = null, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(GetIntDetails(key, defaultValue, context));

    public double GetDouble(string key, double defaultValue, EvaluationContext? context = null) =>
        GetDoubleDetails(key, defaultValue, context).Value;

    public ValueTask<double> GetDoubleAsync(string key, double defaultValue, EvaluationContext? context = null, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(GetDouble(key, defaultValue, context));

    public FlagEvaluationDetails<double> GetDoubleDetails(string key, double defaultValue, EvaluationContext? context = null) =>
        Typed(Evaluate(key, context ?? ambientContext.Current).AsDouble(defaultValue));

    public ValueTask<FlagEvaluationDetails<double>> GetDoubleDetailsAsync(string key, double defaultValue, EvaluationContext? context = null, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(GetDoubleDetails(key, defaultValue, context));

    public T GetObject<T>(string key, T defaultValue, EvaluationContext? context = null, JsonSerializerOptions? options = null) =>
        GetObjectDetails(key, defaultValue, context, options).Value;

    public ValueTask<T> GetObjectAsync<T>(string key, T defaultValue, EvaluationContext? context = null, JsonSerializerOptions? options = null, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(GetObject(key, defaultValue, context, options));

    public FlagEvaluationDetails<T> GetObjectDetails<T>(string key, T defaultValue, EvaluationContext? context = null, JsonSerializerOptions? options = null) =>
        Typed(Evaluate(key, context ?? ambientContext.Current).AsObject(defaultValue, options));

    public ValueTask<FlagEvaluationDetails<T>> GetObjectDetailsAsync<T>(string key, T defaultValue, EvaluationContext? context = null, JsonSerializerOptions? options = null, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(GetObjectDetails(key, defaultValue, context, options));

    /// <summary>
    /// The side effects of a typed (non-boolean) getter: the missing-key warning and the evaluation metric. Usage tracking
    /// and auto-create stay boolean-only for now.
    /// </summary>
    private FlagEvaluationDetails<T> Typed<T>(FlagEvaluationDetails<T> details)
    {
        if (details.ErrorCode == ErrorCode.FlagNotFound)
        {
            WarnMissingOnce(details.FlagKey);
        }

        instrumentation.RecordEvaluation(details);
        return details;
    }

    /// <summary>
    /// Evaluates <paramref name="key"/> untyped for a remote caller (OFREP): the served variant's JSON of any type, or
    /// Undefined when no variant was served. The protocol carries no requested type, so this never reports
    /// TYPE_MISMATCH (client SDKs type-check). Unlike the typed getters, a remote evaluation is not this app's code using
    /// a flag, so it records no usage, never auto-creates or logs a missing key, and uses only the
    /// <paramref name="context"/> the caller sent (never the ambient one). The evaluation metric is recorded with its
    /// usual tags (key, variant, reason), except for missing keys, so arbitrary keys sent by clients cannot inflate its
    /// cardinality.
    /// </summary>
    internal FlagEvaluationDetails<JsonElement> GetRemoteDetails(string key, EvaluationContext context)
    {
        var details = Evaluate(key, context);
        if (details.ErrorCode != ErrorCode.FlagNotFound)
        {
            instrumentation.RecordEvaluation(details);
        }

        return details;
    }

    /// <summary>
    /// Bulk counterpart of <see cref="GetRemoteDetails"/>: uses only <paramref name="context"/>. Like
    /// <see cref="GetAllDetails"/> it records no evaluation metric.
    /// </summary>
    internal FlagEvaluationSet<JsonElement> GetAllRemoteDetails(EvaluationContext context) => EvaluateAll(context);

    /// <summary>
    /// Previews <paramref name="key"/> for the management API: the stored flag as of the current snapshot, evaluated
    /// untyped (the served variant's JSON, or Undefined when none was served) and with no side effects at all (no usage,
    /// auto-create, missing-key warning, ambient context or evaluation metric): previewing is not using the flag.
    /// </summary>
    internal FlagEvaluationDetails<JsonElement> PreviewDetails(string key, EvaluationContext context) =>
        Evaluate(key, context);

    /// <summary>
    /// Previews an unsaved <paramref name="draft"/> of <paramref name="key"/> as if it were stored (and not archived),
    /// with the same untyped, side-effect-free evaluation as <see cref="PreviewDetails"/>.
    /// </summary>
    /// <exception cref="FlagTargetingException">The draft's targeting rule does not compile.</exception>
    internal FlagEvaluationDetails<JsonElement> PreviewDraftDetails(string key, FlagDocument draft, EvaluationContext context)
    {
        // Compiled once, as a snapshot would; a rule that fails to compile is rejected as on write.
        var flag = SnapshotFlag.Create(new FlagRecord(key, draft, Version: 0), compiler);
        if (flag.Targeting?.Error is { } error)
        {
            throw new FlagTargetingException(key, error);
        }

        // A version no snapshot has suppresses the once-per-snapshot rule-failure warning: a draft is not live.
        return Evaluate(key, flag, snapshotVersion: -1, context);
    }

    /// <summary>
    /// Untyped evaluation against the current snapshot: no usage, auto-create or metrics. The only logging is a warning,
    /// once per snapshot version, for a rule that throws.
    /// </summary>
    private FlagEvaluationDetails<JsonElement> Evaluate(string key, EvaluationContext? context)
    {
        var snapshot = snapshots.Current;
        if (key is null || !snapshot.Flags.TryGetValue(key, out var flag))
        {
            return new(key ?? string.Empty, default, null, EvaluationReason.Error, ErrorCode.FlagNotFound, $"Flag '{key}' was not found.");
        }

        return Evaluate(key, flag, snapshot.Version, context);
    }

    public FlagEvaluationSet<JsonElement> GetAllDetails(EvaluationContext? context = null) => EvaluateAll(context ?? ambientContext.Current);

    private FlagEvaluationSet<JsonElement> EvaluateAll(EvaluationContext? context)
    {
        // Read the snapshot once so the version and the flags agree.
        var snapshot = snapshots.Current;
        var results = snapshot.Flags.Values
            .OrderBy(f => f.Record.Key, StringComparer.Ordinal)
            .Select(f => Evaluate(f.Record.Key, f, snapshot.Version, context))
            .ToList();
        return new FlagEvaluationSet<JsonElement>(snapshot.Version, results);
    }

    /// <summary>
    /// The untyped evaluation core. Checks run in order: archived, disabled, targeting, variant lookup. The value is the
    /// served variant's JSON, or Undefined (<c>default</c>) when no variant was served. A typed caller (e.g.
    /// <see cref="FlagEvaluationDetailsExtensions.AsBoolean"/>) type-checks a served variant only, and otherwise
    /// substitutes its own default.
    /// </summary>
    private FlagEvaluationDetails<JsonElement> Evaluate(string key, SnapshotFlag flag, long snapshotVersion, EvaluationContext? context)
    {
        // An archived flag fails safe to the caller's default whatever its document says.
        if (flag.Record.Archived)
        {
            return new(key, default, null, EvaluationReason.Archived);
        }

        var document = flag.Record.Document;

        // flagd semantics: a DISABLED flag yields the caller's default with reason DISABLED (not an error).
        if (document.State == FlagState.Disabled)
        {
            return new(key, default, null, EvaluationReason.Disabled);
        }

        if (ResolveTargeting(key, flag, snapshotVersion, context, out var variant, out var reason) is { } failed)
        {
            return failed;
        }

        if (variant is null)
        {
            // flagd: no defaultVariant and no rule result serves the caller's default (the testbed asserts DEFAULT).
            return new(key, default, null, EvaluationReason.Default);
        }

        if (!document.Variants.TryGetValue(variant, out var value))
        {
            return new(key, default, null, EvaluationReason.Error, ErrorCode.ParseError,
                $"Flag '{key}' has defaultVariant '{variant}', which is not one of its variants.");
        }

        return new(key, value, variant, reason);
    }

    /// <summary>
    /// Picks the variant to serve and why: with no targeting the default variant (STATIC); with a rule, the variant it
    /// names (TARGETING_MATCH) or, when it matches nothing, the default variant (DEFAULT). Returns the error result when
    /// the rule is broken, throws, or names a variant the flag does not define; otherwise null.
    /// </summary>
    private FlagEvaluationDetails<JsonElement>? ResolveTargeting(
        string key, SnapshotFlag flag, long snapshotVersion, EvaluationContext? context,
        out string? variant, out EvaluationReason reason)
    {
        var document = flag.Record.Document;
        variant = document.DefaultVariant;
        reason = EvaluationReason.Static;
        if (flag.Targeting is not { } targeting)
        {
            return null;
        }

        if (targeting.Rule is not { } rule)
        {
            return new(key, default, null, EvaluationReason.Error, ErrorCode.ParseError,
                $"Flag '{key}' has a targeting rule that could not be compiled: {targeting.Error?.Message}");
        }

        string? matched;
        try
        {
            matched = rule(new TargetingContext(key, context, time.GetUtcNow().ToUnixTimeSeconds()));
        }
        catch (Exception ex) when (!TargetingRules.IsFatal(ex))
        {
            // flagd reports a JsonLogic failure while applying the rule (e.g. cat on a bool) as PARSE_ERROR.
            WarnRuleFailedOnce(key, snapshotVersion, ex);
            return new(key, default, null, EvaluationReason.Error, ErrorCode.ParseError,
                $"Flag '{key}' targeting rule failed: {ex.Message}");
        }

        if (matched is null)
        {
            reason = EvaluationReason.Default;
            return null;
        }

        if (!document.Variants.ContainsKey(matched))
        {
            return new(key, default, null, EvaluationReason.Error, ErrorCode.General,
                $"Flag '{key}' targeting resolved to variant '{matched}', which is not one of its variants.");
        }

        variant = matched;
        reason = EvaluationReason.TargetingMatch;
        return null;
    }

    /// <summary>
    /// Warns that <paramref name="key"/>'s rule threw, once per flag per snapshot version (as compile failures are). An
    /// evaluation that finishes after its snapshot was replaced is not warned about: the current snapshot's own
    /// evaluations report it, and a late one could otherwise move the claim back to an old version and re-log.
    /// </summary>
    private void WarnRuleFailedOnce(string key, long snapshotVersion, Exception exception)
    {
        if (snapshots.Current.Version != snapshotVersion)
        {
            return;
        }

        // Lock-free claim of (key, version): exactly one concurrent caller wins and logs.
        while (true)
        {
            if (!_warnedRuleFailures.TryGetValue(key, out var warnedAt))
            {
                if (_warnedRuleFailures.TryAdd(key, snapshotVersion))
                {
                    break;
                }
            }
            else if (warnedAt == snapshotVersion)
            {
                return;
            }
            else if (_warnedRuleFailures.TryUpdate(key, snapshotVersion, warnedAt))
            {
                break;
            }
        }

        LogRuleFailed(logger, exception, key, snapshotVersion);
    }

    public ValueTask<FlagEvaluationDetails<bool>> GetBooleanDetailsAsync(string key, bool defaultValue = false, EvaluationContext? context = null, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(GetBooleanDetails(key, defaultValue, context));

    private void WarnMissingOnce(string key)
    {
        if (_warnedMissingKeys.TryAdd(key, 0))
        {
            LogFlagNotFound(logger, key);
        }
    }

    [LoggerMessage(EventId = 1001, Level = LogLevel.Warning, Message = "Flag '{FlagKey}' was not found; returning the caller's default. This is logged once per key.")]
    private static partial void LogFlagNotFound(ILogger logger, string flagKey);

    [LoggerMessage(EventId = 1002, Level = LogLevel.Warning,
        Message = "Flag '{FlagKey}' targeting rule failed while evaluating (snapshot version {SnapshotVersion}); it evaluates to the caller's default with PARSE_ERROR. This is logged once per flag per snapshot version.")]
    private static partial void LogRuleFailed(ILogger logger, Exception exception, string flagKey, long snapshotVersion);
}
