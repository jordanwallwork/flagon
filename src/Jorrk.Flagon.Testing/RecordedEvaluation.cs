using System.Text.Json;

namespace Jorrk.Flagon.Testing;

/// <summary>Where an evaluation came from.</summary>
public enum EvaluationSource
{
    /// <summary>This app's code, through <see cref="IFlagon"/> (typed getters and bulk).</summary>
    InProcess,

    /// <summary>A remote caller, through the OFREP endpoints.</summary>
    Remote,
}

/// <summary>One evaluation, as recorded in the <see cref="EvaluationLog"/>.</summary>
/// <param name="FlagKey">The key evaluated.</param>
/// <param name="RequestedType">
/// The flag type the typed getter asked for (<c>GetInt</c> and <c>GetDouble</c> both ask for
/// <see cref="FlagType.Number"/>); null for bulk and remote evaluations, which are untyped.
/// </param>
/// <param name="Value">
/// The served variant's value or, when none was served, the caller's default; <see cref="JsonValueKind.Undefined"/>
/// when there is neither (an untyped evaluation that served no variant).
/// </param>
/// <param name="Variant">The served variant, or null.</param>
/// <param name="Reason">Why the value was served.</param>
/// <param name="ErrorCode">The error, or <see cref="Jorrk.Flagon.ErrorCode.None"/>.</param>
/// <param name="ErrorMessage">The error's description, or null.</param>
/// <param name="Context">
/// The effective context: the one the caller passed or, when it passed none, the ambient one (an empty context when
/// there was neither).
/// </param>
/// <param name="Source">Whether the app's code or a remote caller evaluated it.</param>
/// <param name="At">When it was evaluated, by the host's <see cref="TimeProvider"/>.</param>
public sealed record RecordedEvaluation(
    string FlagKey,
    FlagType? RequestedType,
    JsonElement Value,
    string? Variant,
    EvaluationReason Reason,
    ErrorCode ErrorCode,
    string? ErrorMessage,
    EvaluationContext Context,
    EvaluationSource Source,
    DateTimeOffset At);
