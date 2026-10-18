using System.Text.Json;

namespace Flagon.Internal;

/// <summary>
/// Sees every evaluation result this app serves: once per result on the in-process path (typed getters and bulk) and
/// the remote (OFREP) path, next to the evaluation metric. Never told about management previews, which stay
/// side-effect free. Registered by <c>Flagon.Testing</c> for its evaluation log; with none registered the evaluator pays
/// one null check.
/// </summary>
internal interface IFlagonEvaluationObserver
{
    /// <summary>Called synchronously on the evaluating thread, possibly from many threads at once.</summary>
    void OnEvaluated(in ObservedEvaluation evaluation);
}

/// <summary>One evaluation result, as an <see cref="IFlagonEvaluationObserver"/> sees it.</summary>
/// <param name="FlagKey">The key evaluated.</param>
/// <param name="RequestedType">The typed getter's flag type; null for bulk and remote (untyped) evaluations.</param>
/// <param name="Value">The served variant's JSON, or the caller's default; Undefined when there is neither.</param>
/// <param name="Variant">The served variant, or null.</param>
/// <param name="Reason">Why the value was served.</param>
/// <param name="ErrorCode">The error, if any.</param>
/// <param name="ErrorMessage">The error's description, if any.</param>
/// <param name="Context">The effective context: the caller's, or the ambient one; null when there was neither.</param>
/// <param name="Origin">Whether this app's code or a remote (OFREP) caller evaluated it.</param>
internal readonly record struct ObservedEvaluation(
    string FlagKey,
    FlagType? RequestedType,
    JsonElement Value,
    string? Variant,
    EvaluationReason Reason,
    ErrorCode ErrorCode,
    string? ErrorMessage,
    EvaluationContext? Context,
    EvaluationOrigin Origin);

/// <summary>Where an evaluation came from; <c>Flagon.Testing</c> maps it to its public <c>EvaluationSource</c>.</summary>
internal enum EvaluationOrigin
{
    /// <summary>This app's code, through <see cref="IFlagon"/> (typed getters and bulk).</summary>
    InProcess,

    /// <summary>A remote caller, through the OFREP endpoints.</summary>
    Remote,
}
