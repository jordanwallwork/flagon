using System.Collections.Concurrent;
using System.Text.Json;
using Jorrk.Flagon.Internal.Targeting;
using Microsoft.Extensions.Logging;

namespace Jorrk.Flagon.Internal;

/// <summary>Evaluates flags against the current snapshot. No I/O.</summary>
internal sealed partial class FlagonEvaluator(
    FlagSnapshotManager snapshots,
    UsageTracker usage,
    FlagonInstrumentation instrumentation,
    ILogger<FlagonEvaluator> logger,
    FlagAutoCreator autoCreator,
    IEvaluationContextAccessor ambientContext,
    ITargetingCompiler compiler,
    TimeProvider time,
    IFlagonEvaluationObserver? observer = null) : IFlagon
{
    private readonly ConcurrentDictionary<string, byte> _warnedMissingKeys = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, long> _warnedRuleFailures = new(StringComparer.Ordinal);

    public bool IsEnabled(string key, bool defaultValue = false, EvaluationContext? context = null) =>
        GetBooleanDetails(key, defaultValue, context).Value;

    public ValueTask<bool> IsEnabledAsync(string key, bool defaultValue = false, EvaluationContext? context = null, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(IsEnabled(key, defaultValue, context));

    public FlagEvaluationDetails<bool> GetBooleanDetails(string key, bool defaultValue = false, EvaluationContext? context = null) =>
        Typed<bool, BooleanView>(key, defaultValue, context);

    public string GetString(string key, string defaultValue, EvaluationContext? context = null) =>
        GetStringDetails(key, defaultValue, context).Value;

    public ValueTask<string> GetStringAsync(string key, string defaultValue, EvaluationContext? context = null, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(GetString(key, defaultValue, context));

    public FlagEvaluationDetails<string> GetStringDetails(string key, string defaultValue, EvaluationContext? context = null) =>
        Typed<string, StringView>(key, defaultValue, context);

    public ValueTask<FlagEvaluationDetails<string>> GetStringDetailsAsync(string key, string defaultValue, EvaluationContext? context = null, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(GetStringDetails(key, defaultValue, context));

    public int GetInt(string key, int defaultValue, EvaluationContext? context = null) =>
        GetIntDetails(key, defaultValue, context).Value;

    public ValueTask<int> GetIntAsync(string key, int defaultValue, EvaluationContext? context = null, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(GetInt(key, defaultValue, context));

    public FlagEvaluationDetails<int> GetIntDetails(string key, int defaultValue, EvaluationContext? context = null) =>
        Typed<int, IntView>(key, defaultValue, context);

    public ValueTask<FlagEvaluationDetails<int>> GetIntDetailsAsync(string key, int defaultValue, EvaluationContext? context = null, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(GetIntDetails(key, defaultValue, context));

    public double GetDouble(string key, double defaultValue, EvaluationContext? context = null) =>
        GetDoubleDetails(key, defaultValue, context).Value;

    public ValueTask<double> GetDoubleAsync(string key, double defaultValue, EvaluationContext? context = null, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(GetDouble(key, defaultValue, context));

    public FlagEvaluationDetails<double> GetDoubleDetails(string key, double defaultValue, EvaluationContext? context = null) =>
        Typed<double, DoubleView>(key, defaultValue, context);

    public ValueTask<FlagEvaluationDetails<double>> GetDoubleDetailsAsync(string key, double defaultValue, EvaluationContext? context = null, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(GetDoubleDetails(key, defaultValue, context));

    public T GetObject<T>(string key, T defaultValue, EvaluationContext? context = null, JsonSerializerOptions? options = null) =>
        GetObjectDetails(key, defaultValue, context, options).Value;

    public ValueTask<T> GetObjectAsync<T>(string key, T defaultValue, EvaluationContext? context = null, JsonSerializerOptions? options = null, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(GetObject(key, defaultValue, context, options));

    public FlagEvaluationDetails<T> GetObjectDetails<T>(string key, T defaultValue, EvaluationContext? context = null, JsonSerializerOptions? options = null) =>
        Typed<T, ObjectView<T>>(key, defaultValue, context, options);

    public ValueTask<FlagEvaluationDetails<T>> GetObjectDetailsAsync<T>(string key, T defaultValue, EvaluationContext? context = null, JsonSerializerOptions? options = null, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(GetObjectDetails(key, defaultValue, context, options));

    /// <summary>
    /// A typed getter's view of an untyped result: its flag type, its caller's default, and the typed details. A struct
    /// with static members, so each <see cref="Typed{T, TView}"/> instantiation is specialised and inlined by the JIT (no
    /// delegate call on the hot path).
    /// </summary>
    private interface ITypedView<T>
    {
        static abstract FlagType Type { get; }

        static abstract CallerDefault Default(T defaultValue, JsonSerializerOptions? options);

        static abstract FlagEvaluationDetails<T> View(in Resolution resolution, T defaultValue, JsonSerializerOptions? options);
    }

    private readonly struct BooleanView : ITypedView<bool>
    {
        public static FlagType Type => FlagType.Boolean;

        public static CallerDefault Default(bool defaultValue, JsonSerializerOptions? options) => CallerDefault.Of(defaultValue);

        public static FlagEvaluationDetails<bool> View(in Resolution resolution, bool defaultValue, JsonSerializerOptions? options) =>
            resolution.AsBoolean(defaultValue);
    }

    private readonly struct StringView : ITypedView<string>
    {
        public static FlagType Type => FlagType.String;

        public static CallerDefault Default(string defaultValue, JsonSerializerOptions? options) => CallerDefault.Of(defaultValue);

        public static FlagEvaluationDetails<string> View(in Resolution resolution, string defaultValue, JsonSerializerOptions? options) =>
            resolution.AsString(defaultValue);
    }

    private readonly struct IntView : ITypedView<int>
    {
        public static FlagType Type => FlagType.Number;

        public static CallerDefault Default(int defaultValue, JsonSerializerOptions? options) => CallerDefault.Of(defaultValue);

        public static FlagEvaluationDetails<int> View(in Resolution resolution, int defaultValue, JsonSerializerOptions? options) =>
            resolution.AsInt(defaultValue);
    }

    private readonly struct DoubleView : ITypedView<double>
    {
        public static FlagType Type => FlagType.Number;

        public static CallerDefault Default(double defaultValue, JsonSerializerOptions? options) => CallerDefault.Of(defaultValue);

        public static FlagEvaluationDetails<double> View(in Resolution resolution, double defaultValue, JsonSerializerOptions? options) =>
            resolution.AsDouble(defaultValue);
    }

    private readonly struct ObjectView<T> : ITypedView<T>
    {
        public static FlagType Type => FlagType.Object;

        public static CallerDefault Default(T defaultValue, JsonSerializerOptions? options) => CallerDefault.Of(defaultValue, options);

        public static FlagEvaluationDetails<T> View(in Resolution resolution, T defaultValue, JsonSerializerOptions? options) =>
            resolution.AsObject(defaultValue, options);
    }

    /// <summary>
    /// Every typed getter: evaluates <paramref name="key"/> with the caller's <paramref name="context"/> (or the ambient
    /// one), views the result through <typeparamref name="TView"/>, then applies the side effects of this app's code using a
    /// flag: usage is recorded with the caller's default (also on <see cref="ErrorCode.TypeMismatch"/>), and a missing key
    /// is warned about once and queued for auto-create as a flag of the default's type. Then the evaluation metric and the
    /// observer, if any.
    /// </summary>
    private FlagEvaluationDetails<T> Typed<T, TView>(
        string key, T defaultValue, EvaluationContext? context, JsonSerializerOptions? options = null)
        where TView : struct, ITypedView<T>
    {
        context ??= ambientContext.Current;
        var resolution = Evaluate(key, context);
        var details = TView.View(resolution, defaultValue, options);
        var callerDefault = TView.Default(defaultValue, options);

        usage.Record(key, callerDefault);
        if (details.ErrorCode == ErrorCode.FlagNotFound)
        {
            WarnMissingOnce(details.FlagKey);
            autoCreator.Enqueue(details.FlagKey, callerDefault);
        }

        instrumentation.RecordEvaluation(details);
        if (observer is { IsEnabled: true })
        {
            // A typed view serves the variant's JSON or, when it served none (including on TYPE_MISMATCH), the caller's default.
            var value = details.Variant is not null ? resolution.Value : callerDefault.ToJson() ?? default;
            Observe(details, TView.Type, value, context, EvaluationOrigin.InProcess);
        }

        return details;
    }

    /// <summary>Tells the observer, if any, about one result; <paramref name="value"/> is the JSON it was served as.</summary>
    private void Observe<T>(
        FlagEvaluationDetails<T> details, FlagType? requestedType, JsonElement value, EvaluationContext? context, EvaluationOrigin origin)
    {
        if (observer is { IsEnabled: true } enabled)
        {
            enabled.OnEvaluated(new ObservedEvaluation(details.FlagKey, requestedType, value, details.Variant,
                details.Reason, details.ErrorCode, details.ErrorMessage, context, origin));
        }
    }

    /// <summary>
    /// Evaluates <paramref name="key"/> untyped for a remote caller (OFREP): the served variant's JSON of any type, or
    /// Undefined when no variant was served. The protocol carries no requested type, so this never reports
    /// TYPE_MISMATCH (client SDKs type-check). Unlike the typed getters, a remote evaluation is not this app's code using
    /// a flag, so it records no usage, never auto-creates or logs a missing key, and uses only the
    /// <paramref name="context"/> the caller sent (never the ambient one). The evaluation metric is recorded with its
    /// usual tags (key, variant, reason), except for missing keys, so arbitrary keys sent by clients cannot inflate its
    /// cardinality. The observer, if any, sees every result, missing keys included.
    /// </summary>
    internal FlagEvaluationDetails<JsonElement> GetRemoteDetails(string key, EvaluationContext context)
    {
        var details = Evaluate(key, context).ToDetails();
        if (details.ErrorCode != ErrorCode.FlagNotFound)
        {
            instrumentation.RecordEvaluation(details);
        }

        Observe(details, requestedType: null, details.Value, context, EvaluationOrigin.Remote);
        return details;
    }

    /// <summary>
    /// Bulk counterpart of <see cref="GetRemoteDetails"/>: uses only <paramref name="context"/>. Like
    /// <see cref="GetAllDetails"/> it records no evaluation metric. The observer, if any, sees every result.
    /// </summary>
    internal FlagEvaluationSet<JsonElement> GetAllRemoteDetails(EvaluationContext context) => EvaluateAll(context, EvaluationOrigin.Remote);

    /// <summary>
    /// Previews <paramref name="key"/> for the management API: the stored flag as of the current snapshot, evaluated
    /// untyped (the served variant's JSON, or Undefined when none was served) and with no side effects at all (no usage,
    /// auto-create, missing-key warning, ambient context or evaluation metric): previewing is not using the flag.
    /// </summary>
    internal FlagEvaluationDetails<JsonElement> PreviewDetails(string key, EvaluationContext context) =>
        Evaluate(key, context).ToDetails();

    /// <summary>
    /// Previews an unsaved <paramref name="draft"/> of <paramref name="key"/> as if it were stored (and not archived),
    /// with the same untyped, side-effect-free evaluation as <see cref="PreviewDetails"/>.
    /// </summary>
    /// <exception cref="FlagTargetingException">The draft's targeting rule does not compile.</exception>
    internal FlagEvaluationDetails<JsonElement> PreviewDraftDetails(string key, FlagDocument draft, EvaluationContext context)
    {
        // Compiled once, as a snapshot would (segment references inlined from the current snapshot's segments); a rule
        // that fails to compile is rejected as on write.
        var flag = SnapshotFlag.Create(new FlagRecord(key, draft, Version: 0), compiler, snapshots.Current.SegmentRules);
        if (flag.Targeting?.Error is { } error)
        {
            throw new FlagTargetingException(key, error);
        }

        // A version no snapshot has suppresses the once-per-snapshot rule-failure warning: a draft is not live.
        return Evaluate(key, flag, snapshotVersion: -1, context).ToDetails();
    }

    /// <summary>
    /// Runs a segment's <paramref name="rule"/> against <paramref name="context"/> for the management API, with no side
    /// effects. The rule must be valid for a segment. <c>$flagd.flagKey</c> (and fractional bucketing) sees the segment key.
    /// </summary>
    /// <exception cref="SegmentRuleException">The rule is not a valid segment rule.</exception>
    internal SegmentPreview PreviewSegment(string key, JsonElement rule, EvaluationContext context)
    {
        var validated = TargetingRules.ValidateSegmentRule(compiler, key, rule);
        var result = TargetingRules.Compile(compiler, key, validated).Rule!;
        // JsonLogic truthiness of the result, through the registered engine: "!!" is part of flagd's operator set.
        using var truthiness = JsonDocument.Parse($"{{\"!!\":[{validated.GetRawText()}]}}");
        var matches = TargetingRules.Compile(compiler, key, truthiness.RootElement.Clone()).Rule;

        var target = new TargetingContext(key, context, time.GetUtcNow().ToUnixTimeSeconds());
        try
        {
            return new SegmentPreview(matches?.Invoke(target) == "true", result(target), Error: null);
        }
        catch (Exception ex) when (!TargetingRules.IsFatal(ex))
        {
            return new SegmentPreview(false, null, ex.Message);
        }
    }

    /// <summary>
    /// Untyped evaluation against the current snapshot: no usage, auto-create or metrics. The only logging is a warning,
    /// once per snapshot version, for a rule that throws.
    /// </summary>
    private Resolution Evaluate(string key, EvaluationContext? context)
    {
        var snapshot = snapshots.Current;
        if (key is null || !snapshot.Flags.TryGetValue(key, out var flag))
        {
            return new(key ?? string.Empty, default, null, EvaluationReason.Error, ErrorCode.FlagNotFound, $"Flag '{key}' was not found.");
        }

        return Evaluate(key, flag, snapshot.Version, context);
    }

    public FlagEvaluationSet<JsonElement> GetAllDetails(EvaluationContext? context = null) => EvaluateAll(context ?? ambientContext.Current, EvaluationOrigin.InProcess);

    /// <summary>Evaluates every flag in the current snapshot, telling the observer (if any) about each result.</summary>
    private FlagEvaluationSet<JsonElement> EvaluateAll(EvaluationContext? context, EvaluationOrigin origin)
    {
        // Read the snapshot once so the version and the flags agree.
        var snapshot = snapshots.Current;
        var results = snapshot.Flags.Values
            .OrderBy(f => f.Record.Key, StringComparer.Ordinal)
            .Select(f => Evaluate(f.Record.Key, f, snapshot.Version, context).ToDetails())
            .ToList();
        if (observer is { IsEnabled: true })
        {
            foreach (var details in results)
            {
                Observe(details, requestedType: null, details.Value, context, origin);
            }
        }

        return new FlagEvaluationSet<JsonElement>(snapshot.Version, results);
    }

    /// <summary>
    /// The untyped evaluation core. Checks run in order: archived, disabled, targeting, variant lookup. The value is the
    /// served variant's JSON, or Undefined (<c>default</c>) when no variant was served. A typed caller (e.g.
    /// <see cref="FlagEvaluationDetailsExtensions.AsBoolean"/>) type-checks a served variant only, and otherwise
    /// substitutes its own default.
    /// </summary>
    private Resolution Evaluate(string key, SnapshotFlag flag, long snapshotVersion, EvaluationContext? context)
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
    private Resolution? ResolveTargeting(
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

/// <summary>
/// A segment rule run against a context: whether it matches (its result is JsonLogic-truthy), its result as flagd would
/// name a variant (null when the rule returns null), or why the rule failed while running.
/// </summary>
internal sealed record SegmentPreview(bool Matches, string? Result, string? Error);
