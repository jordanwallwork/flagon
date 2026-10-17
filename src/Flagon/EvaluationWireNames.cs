namespace Flagon;

/// <summary>
/// The single mapping of <see cref="EvaluationReason"/> and <see cref="ErrorCode"/> to their wire names: OpenFeature's
/// UPPER_SNAKE_CASE codes (<c>STATIC</c>, <c>TARGETING_MATCH</c>, <c>FLAG_NOT_FOUND</c>, ...). Used by the OpenFeature
/// provider, the OFREP endpoints and telemetry (which lower-cases them), so every surface spells them the same way.
/// </summary>
public static class EvaluationWireNames
{
    /// <summary>
    /// The OpenFeature reason code for <paramref name="reason"/>, e.g. <c>STATIC</c>. Flagon's own
    /// <see cref="EvaluationReason.Archived"/> is the custom reason <c>ARCHIVED</c>; an undefined value is <c>UNKNOWN</c>.
    /// </summary>
    public static string ToWireName(this EvaluationReason reason) => reason switch
    {
        EvaluationReason.Static => "STATIC",
        EvaluationReason.Default => "DEFAULT",
        EvaluationReason.TargetingMatch => "TARGETING_MATCH",
        EvaluationReason.Split => "SPLIT",
        EvaluationReason.Cached => "CACHED",
        EvaluationReason.Disabled => "DISABLED",
        EvaluationReason.Stale => "STALE",
        EvaluationReason.Error => "ERROR",
        EvaluationReason.Archived => "ARCHIVED",
        _ => "UNKNOWN",
    };

    /// <summary>
    /// The OpenFeature error code for <paramref name="code"/>, e.g. <c>FLAG_NOT_FOUND</c>; <see cref="ErrorCode.None"/>
    /// is <c>NONE</c> and an undefined value is <c>GENERAL</c>.
    /// </summary>
    public static string ToWireName(this ErrorCode code) => code switch
    {
        ErrorCode.None => "NONE",
        ErrorCode.ProviderNotReady => "PROVIDER_NOT_READY",
        ErrorCode.FlagNotFound => "FLAG_NOT_FOUND",
        ErrorCode.ParseError => "PARSE_ERROR",
        ErrorCode.TypeMismatch => "TYPE_MISMATCH",
        ErrorCode.TargetingKeyMissing => "TARGETING_KEY_MISSING",
        ErrorCode.InvalidContext => "INVALID_CONTEXT",
        ErrorCode.ProviderFatal => "PROVIDER_FATAL",
        _ => "GENERAL",
    };
}
