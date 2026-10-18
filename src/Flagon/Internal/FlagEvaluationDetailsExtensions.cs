using System.Globalization;
using System.Text.Json;

namespace Flagon.Internal;

/// <summary>
/// Typed views of the untyped evaluation core's results. When no variant was served the caller's default is returned
/// with the core's reason and error; a served variant whose value is not of the requested type is a
/// <see cref="ErrorCode.TypeMismatch"/> (the type check applies only to a served variant, as in flagd).
/// </summary>
internal static class FlagEvaluationDetailsExtensions
{
    /// <summary>Views an untyped result as a boolean one.</summary>
    public static FlagEvaluationDetails<bool> AsBoolean(this in Resolution details, bool defaultValue) =>
        details.As(defaultValue, "a boolean", TryGetBoolean);

    /// <summary>Views an untyped result as a string one.</summary>
    public static FlagEvaluationDetails<string> AsString(this in Resolution details, string defaultValue) =>
        details.As(defaultValue, "a string", TryGetString);

    /// <summary>
    /// Views an untyped result as an int one. Only an integral number within <see cref="int"/> range converts (e.g.
    /// <c>10</c> or <c>1e2</c>); a fractional or out-of-range number is a type mismatch, never truncated.
    /// </summary>
    public static FlagEvaluationDetails<int> AsInt(this in Resolution details, int defaultValue) =>
        details.As(defaultValue, "an integer within Int32 range", TryGetInt);

    /// <summary>Views an untyped result as a double one. Any (finite) number converts.</summary>
    public static FlagEvaluationDetails<double> AsDouble(this in Resolution details, double defaultValue) =>
        details.As(defaultValue, "a number", TryGetDouble);

    /// <summary>
    /// Views an untyped result as a <typeparamref name="T"/>, deserialised from the served variant's JSON object with
    /// <paramref name="options"/> (a <see cref="JsonElement"/> is served as is). A variant that is not an object, or
    /// fails to deserialise, is a type mismatch.
    /// </summary>
    public static FlagEvaluationDetails<T> AsObject<T>(this in Resolution details, T defaultValue, JsonSerializerOptions? options)
    {
        if (typeof(T) == typeof(JsonElement))
        {
            // The JIT folds this branch per T, so the JSON view neither deserialises nor allocates a converter.
            var json = details.As((JsonElement)(object)defaultValue!, "an object", TryGetObject);
            return (FlagEvaluationDetails<T>)(object)json;
        }

        return details.As(defaultValue, $"an object that deserialises as {typeof(T).Name}",
            (JsonElement value, out T result) => TryDeserialize(value, options, out result));
    }

    /// <summary>Converts a served variant's JSON to the requested type; false is a type mismatch.</summary>
    private delegate bool TryConvert<T>(JsonElement value, out T result);

    private static FlagEvaluationDetails<T> As<T>(this in Resolution details, T defaultValue, string typeName, TryConvert<T> convert)
    {
        if (details.Variant is null)
        {
            return new(details.FlagKey, defaultValue, null, details.Reason, details.ErrorCode, details.ErrorMessage);
        }

        if (!convert(details.Value, out var value))
        {
            return new(details.FlagKey, defaultValue, null, EvaluationReason.Error, ErrorCode.TypeMismatch,
                $"Flag '{details.FlagKey}' variant '{details.Variant}' is not {typeName}.");
        }

        return new(details.FlagKey, value, details.Variant, details.Reason);
    }

    private static bool TryGetBoolean(JsonElement value, out bool result)
    {
        result = value.ValueKind == JsonValueKind.True;
        return value.ValueKind is JsonValueKind.True or JsonValueKind.False;
    }

    private static bool TryGetString(JsonElement value, out string result)
    {
        result = value.ValueKind == JsonValueKind.String ? value.GetString()! : string.Empty;
        return value.ValueKind == JsonValueKind.String;
    }

    private static bool TryGetInt(JsonElement value, out int result)
    {
        result = 0;
        if (value.ValueKind != JsonValueKind.Number)
        {
            return false;
        }

        if (value.TryGetInt32(out result))
        {
            return true;
        }

        // An integral value written with a fraction or exponent (10.0, 1e2) is still an integer. Integrality is decided on
        // the exact decimal value (no decimal or double rounding, which would turn 1e-29 into 0): its canonical text is an
        // integer literal only when it is integral and has at most 21 trailing zeros, far beyond Int32 range.
        return int.TryParse(CanonicalJson.CanonicalNumber(value.GetRawText()), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out result);
    }

    private static bool TryGetDouble(JsonElement value, out double result)
    {
        result = 0;
        return value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out result) && double.IsFinite(result);
    }

    private static bool TryGetObject(JsonElement value, out JsonElement result)
    {
        result = value;
        return value.ValueKind == JsonValueKind.Object;
    }

    private static bool TryDeserialize<T>(JsonElement value, JsonSerializerOptions? options, out T result)
    {
        result = default!;
        if (value.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        try
        {
            result = value.Deserialize<T>(options)!;
            return true;
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or InvalidOperationException)
        {
            return false;
        }
    }
}
