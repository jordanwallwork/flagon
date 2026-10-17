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
    TimeProvider time) : IFlagon
{
    private readonly ConcurrentDictionary<string, byte> _warnedMissingKeys = new(StringComparer.Ordinal);

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

    /// <summary>Pure evaluation against the current snapshot: no usage, auto-create, logging or metrics.</summary>
    private FlagEvaluationDetails<bool> Evaluate(string key, bool defaultValue, EvaluationContext? context)
    {
        if (key is null || !snapshots.Current.Flags.TryGetValue(key, out var flag))
        {
            return new(key ?? string.Empty, defaultValue, null, EvaluationReason.Error, ErrorCode.FlagNotFound, $"Flag '{key}' was not found.");
        }

        return Evaluate(key, flag, defaultValue, context);
    }

    public FlagEvaluationSet<bool> GetAllBooleanDetails(EvaluationContext? context = null) => EvaluateAll(context ?? ambientContext.Current);

    private FlagEvaluationSet<bool> EvaluateAll(EvaluationContext? context)
    {
        // Read the snapshot once so the version and the flags agree.
        var snapshot = snapshots.Current;
        var results = snapshot.Flags.Values
            .OrderBy(f => f.Record.Key, StringComparer.Ordinal)
            .Select(f => Evaluate(f.Record.Key, f, defaultValue: false, context))
            .ToList();
        return new FlagEvaluationSet<bool>(snapshot.Version, results);
    }

    private FlagEvaluationDetails<bool> Evaluate(string key, SnapshotFlag flag, bool defaultValue, EvaluationContext? context)
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

        // No targeting: the default variant, STATIC. Targeting resolving to null (no match): the default variant, DEFAULT.
        var variant = document.DefaultVariant;
        var reason = EvaluationReason.Static;
        if (flag.Targeting is { } targeting)
        {
            if (targeting.Rule is not { } rule)
            {
                return new(key, defaultValue, null, EvaluationReason.Error, ErrorCode.ParseError,
                    $"Flag '{key}' has a targeting rule that could not be compiled: {targeting.Error}");
            }

            string? matched;
            try
            {
                matched = rule(new TargetingContext(key, context, time.GetUtcNow().ToUnixTimeSeconds()));
            }
            catch (Exception ex)
            {
                // flagd reports a JsonLogic failure while applying the rule (e.g. cat on a bool) as PARSE_ERROR.
                return new(key, defaultValue, null, EvaluationReason.Error, ErrorCode.ParseError,
                    $"Flag '{key}' targeting rule failed: {ex.Message}");
            }

            if (matched is null)
            {
                reason = EvaluationReason.Default;
            }
            else if (!document.Variants.ContainsKey(matched))
            {
                return new(key, defaultValue, null, EvaluationReason.Error, ErrorCode.General,
                    $"Flag '{key}' targeting resolved to variant '{matched}', which is not one of its variants.");
            }
            else
            {
                variant = matched;
                reason = EvaluationReason.TargetingMatch;
            }
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
}
