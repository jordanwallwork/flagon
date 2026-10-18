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
        var details = Evaluate(key, defaultValue, context ?? ambientContext.Current);
        if (details.ErrorCode == ErrorCode.FlagNotFound)
        {
            WarnMissingOnce(details.FlagKey);
            autoCreator.Enqueue(details.FlagKey, defaultValue);
        }

        instrumentation.RecordEvaluation(details);
        return details;
    }

    /// <summary>
    /// Evaluates <paramref name="key"/> for a remote caller (OFREP). Unlike <see cref="GetBooleanDetails"/>, a remote
    /// evaluation is not this app's code using a flag, so it records no usage, never auto-creates or logs a missing key,
    /// and uses only the <paramref name="context"/> the caller sent (never the ambient one). Missing keys are not counted
    /// on the evaluation metric either, so arbitrary keys sent by clients cannot inflate its cardinality.
    /// </summary>
    internal FlagEvaluationDetails<bool> GetRemoteBooleanDetails(string key, EvaluationContext context)
    {
        var details = Evaluate(key, defaultValue: false, context);
        if (details.ErrorCode != ErrorCode.FlagNotFound)
        {
            instrumentation.RecordEvaluation(details);
        }

        return details;
    }

    /// <summary>Bulk counterpart of <see cref="GetRemoteBooleanDetails"/>: uses only <paramref name="context"/>.</summary>
    internal FlagEvaluationSet<bool> GetAllRemoteBooleanDetails(EvaluationContext context) => EvaluateAll(context);

    /// <summary>
    /// Previews <paramref name="key"/> for the management API: the stored flag as of the current snapshot, evaluated
    /// exactly as <see cref="GetBooleanDetails"/> would with a <c>false</c> default, but with no side effects at all (no
    /// usage, auto-create, missing-key warning, ambient context or evaluation metric): previewing is not using the flag.
    /// </summary>
    internal FlagEvaluationDetails<bool> PreviewBooleanDetails(string key, EvaluationContext context) =>
        Evaluate(key, defaultValue: false, context);

    /// <summary>
    /// Previews an unsaved <paramref name="draft"/> of <paramref name="key"/> as if it were stored (and not archived),
    /// with the same side-effect-free evaluation as <see cref="PreviewBooleanDetails"/>.
    /// </summary>
    /// <exception cref="FlagTargetingException">The draft's targeting rule does not compile.</exception>
    internal FlagEvaluationDetails<bool> PreviewDraftBooleanDetails(string key, FlagDocument draft, EvaluationContext context)
    {
        var targeting = TargetingRules.Validate(compiler, key, draft.Targeting);
        var flag = new SnapshotFlag(
            new FlagRecord(key, draft, Version: 0),
            TargetingRules.IsRule(targeting) && targeting is { } rule ? TargetingRules.Compile(compiler, key, rule) : null);

        // A version no snapshot has suppresses the once-per-snapshot rule-failure warning: a draft is not live.
        return Evaluate(key, flag, snapshotVersion: -1, defaultValue: false, context);
    }

    /// <summary>
    /// Evaluation against the current snapshot: no usage, auto-create or metrics. The only logging is a warning, once per
    /// snapshot version, for a rule that throws.
    /// </summary>
    private FlagEvaluationDetails<bool> Evaluate(string key, bool defaultValue, EvaluationContext? context)
    {
        var snapshot = snapshots.Current;
        if (key is null || !snapshot.Flags.TryGetValue(key, out var flag))
        {
            return new(key ?? string.Empty, defaultValue, null, EvaluationReason.Error, ErrorCode.FlagNotFound, $"Flag '{key}' was not found.");
        }

        return Evaluate(key, flag, snapshot.Version, defaultValue, context);
    }

    public FlagEvaluationSet<bool> GetAllBooleanDetails(EvaluationContext? context = null) => EvaluateAll(context ?? ambientContext.Current);

    private FlagEvaluationSet<bool> EvaluateAll(EvaluationContext? context)
    {
        // Read the snapshot once so the version and the flags agree.
        var snapshot = snapshots.Current;
        var results = snapshot.Flags.Values
            .OrderBy(f => f.Record.Key, StringComparer.Ordinal)
            .Select(f => Evaluate(f.Record.Key, f, snapshot.Version, defaultValue: false, context))
            .ToList();
        return new FlagEvaluationSet<bool>(snapshot.Version, results);
    }

    private FlagEvaluationDetails<bool> Evaluate(string key, SnapshotFlag flag, long snapshotVersion, bool defaultValue, EvaluationContext? context)
    {
        // An archived flag fails safe to the caller's default whatever its document says.
        if (flag.Record.Archived)
        {
            return new(key, defaultValue, null, EvaluationReason.Archived);
        }

        var document = flag.Record.Document;

        // flagd semantics: a DISABLED flag yields the caller's default with reason DISABLED (not an error).
        if (document.State == FlagState.Disabled)
        {
            return new(key, defaultValue, null, EvaluationReason.Disabled);
        }

        if (ResolveTargeting(key, flag, snapshotVersion, defaultValue, context, out var variant, out var reason) is { } failed)
        {
            return failed;
        }

        if (variant is null)
        {
            // flagd: no defaultVariant and no rule result serves the caller's default (the testbed asserts DEFAULT).
            return new(key, defaultValue, null, EvaluationReason.Default);
        }

        if (!document.Variants.TryGetValue(variant, out var value))
        {
            return new(key, defaultValue, null, EvaluationReason.Error, ErrorCode.ParseError,
                $"Flag '{key}' has defaultVariant '{variant}', which is not one of its variants.");
        }

        if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            return new(key, defaultValue, null, EvaluationReason.Error, ErrorCode.TypeMismatch,
                $"Flag '{key}' variant '{variant}' is not a boolean.");
        }

        return new(key, value.GetBoolean(), variant, reason);
    }

    /// <summary>
    /// Picks the variant to serve and why: with no targeting the default variant (STATIC); with a rule, the variant it
    /// names (TARGETING_MATCH) or, when it matches nothing, the default variant (DEFAULT). Returns the error result when
    /// the rule is broken, throws, or names a variant the flag does not define; otherwise null.
    /// </summary>
    private FlagEvaluationDetails<bool>? ResolveTargeting(
        string key, SnapshotFlag flag, long snapshotVersion, bool defaultValue, EvaluationContext? context,
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
            return new(key, defaultValue, null, EvaluationReason.Error, ErrorCode.ParseError,
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
            return new(key, defaultValue, null, EvaluationReason.Error, ErrorCode.ParseError,
                $"Flag '{key}' targeting rule failed: {ex.Message}");
        }

        if (matched is null)
        {
            reason = EvaluationReason.Default;
            return null;
        }

        if (!document.Variants.ContainsKey(matched))
        {
            return new(key, defaultValue, null, EvaluationReason.Error, ErrorCode.General,
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
