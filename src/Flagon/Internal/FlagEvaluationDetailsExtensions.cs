using System.Text.Json;

namespace Flagon.Internal;

/// <summary>Typed views of the untyped evaluation core's results.</summary>
internal static class FlagEvaluationDetailsExtensions
{
    /// <summary>
    /// Views an untyped result as a boolean one. When no variant was served the caller's <paramref name="defaultValue"/>
    /// is returned with the core's reason and error; a served variant that is not a boolean is a
    /// <see cref="ErrorCode.TypeMismatch"/> (the type check applies only to a served variant, as in flagd).
    /// </summary>
    public static FlagEvaluationDetails<bool> AsBoolean(this FlagEvaluationDetails<JsonElement> details, bool defaultValue)
    {
        if (details.Variant is null)
        {
            return new(details.FlagKey, defaultValue, null, details.Reason, details.ErrorCode, details.ErrorMessage);
        }

        if (details.Value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            return new(details.FlagKey, defaultValue, null, EvaluationReason.Error, ErrorCode.TypeMismatch,
                $"Flag '{details.FlagKey}' variant '{details.Variant}' is not a boolean.");
        }

        return new(details.FlagKey, details.Value.GetBoolean(), details.Variant, details.Reason);
    }
}
