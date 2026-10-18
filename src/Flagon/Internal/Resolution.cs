using System.Text.Json;

namespace Flagon.Internal;

/// <summary>
/// The untyped evaluation core's result: the served variant's JSON (Undefined when none was served), its name, the
/// reason and any error. A struct, so a typed getter allocates only the <see cref="FlagEvaluationDetails{T}"/> it returns
/// (see <see cref="FlagEvaluationDetailsExtensions"/>); untyped callers allocate theirs with <see cref="ToDetails"/>.
/// </summary>
internal readonly struct Resolution(
    string flagKey,
    JsonElement value,
    string? variant,
    EvaluationReason reason,
    ErrorCode errorCode = ErrorCode.None,
    string? errorMessage = null)
{
    public string FlagKey { get; } = flagKey;

    public JsonElement Value { get; } = value;

    public string? Variant { get; } = variant;

    public EvaluationReason Reason { get; } = reason;

    public ErrorCode ErrorCode { get; } = errorCode;

    public string? ErrorMessage { get; } = errorMessage;

    /// <summary>This result as untyped public details.</summary>
    public FlagEvaluationDetails<JsonElement> ToDetails() => new(FlagKey, Value, Variant, Reason, ErrorCode, ErrorMessage);
}
