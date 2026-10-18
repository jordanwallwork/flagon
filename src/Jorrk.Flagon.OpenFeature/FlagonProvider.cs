using OpenFeature;
using OpenFeature.Constant;
using OpenFeature.Model;
using OfEvaluationContext = OpenFeature.Model.EvaluationContext;

namespace Jorrk.Flagon.OpenFeature;

/// <summary>
/// An OpenFeature provider backed by Flagon's in-process evaluation (<see cref="IFlagon"/>).
/// Resolution reads Flagon's in-memory snapshot and performs no I/O. Every OpenFeature type resolves through Flagon's
/// typed getters: a flag of another type returns the caller's default with <see cref="ErrorType.TypeMismatch"/>, and
/// integer resolution accepts only an integral number within <see cref="int"/> range (as <c>IFlagon.GetInt</c>).
/// Structures map to and from JSON objects.
/// </summary>
public sealed class FlagonProvider : FeatureProvider
{
    /// <summary>The provider name reported in OpenFeature metadata.</summary>
    public const string Name = "Flagon";

    private static readonly Metadata ProviderMetadata = new(Name);

    private readonly IFlagon _flagon;

    /// <summary>Creates a provider over <paramref name="flagon"/>.</summary>
    public FlagonProvider(IFlagon flagon)
    {
        ArgumentNullException.ThrowIfNull(flagon);
        _flagon = flagon;
    }

    /// <inheritdoc />
    public override Metadata GetMetadata() => ProviderMetadata;

    /// <inheritdoc />
    public override Task<ResolutionDetails<bool>> ResolveBooleanValueAsync(string flagKey, bool defaultValue, OfEvaluationContext? context = null, CancellationToken cancellationToken = default) =>
        Resolved(_flagon.GetBooleanDetails(flagKey, defaultValue, ToFlagonContext(context)));

    /// <inheritdoc />
    public override Task<ResolutionDetails<string>> ResolveStringValueAsync(string flagKey, string defaultValue, OfEvaluationContext? context = null, CancellationToken cancellationToken = default) =>
        Resolved(_flagon.GetStringDetails(flagKey, defaultValue, ToFlagonContext(context)));

    /// <inheritdoc />
    public override Task<ResolutionDetails<int>> ResolveIntegerValueAsync(string flagKey, int defaultValue, OfEvaluationContext? context = null, CancellationToken cancellationToken = default) =>
        Resolved(_flagon.GetIntDetails(flagKey, defaultValue, ToFlagonContext(context)));

    /// <inheritdoc />
    public override Task<ResolutionDetails<double>> ResolveDoubleValueAsync(string flagKey, double defaultValue, OfEvaluationContext? context = null, CancellationToken cancellationToken = default) =>
        Resolved(_flagon.GetDoubleDetails(flagKey, defaultValue, ToFlagonContext(context)));

    /// <inheritdoc />
    public override Task<ResolutionDetails<Value>> ResolveStructureValueAsync(string flagKey, Value defaultValue, OfEvaluationContext? context = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(defaultValue);

        // The default goes to Flagon as JSON (for usage and auto-create); when no variant is served the caller gets their
        // own Value back untouched, so nothing JSON cannot express (e.g. a DateTime) is lost.
        var details = _flagon.GetObjectDetails(flagKey, StructureJson.ToJson(defaultValue), ToFlagonContext(context));
        var value = details.Variant is not null && details.ErrorCode == ErrorCode.None
            ? StructureJson.ToValue(details.Value)
            : defaultValue;
        return Resolved(details, value);
    }

    private static Task<ResolutionDetails<T>> Resolved<T>(FlagEvaluationDetails<T> details) => Resolved(details, details.Value);

    private static Task<ResolutionDetails<TValue>> Resolved<T, TValue>(FlagEvaluationDetails<T> details, TValue value) =>
        Task.FromResult(new ResolutionDetails<TValue>(
            details.FlagKey,
            value,
            ToErrorType(details.ErrorCode),
            // OpenFeature reason codes are Flagon's shared wire names; Flagon-only reasons (ARCHIVED) are custom reasons.
            details.Reason.ToWireName(),
            details.Variant,
            details.ErrorMessage));

    private static Jorrk.Flagon.EvaluationContext? ToFlagonContext(OfEvaluationContext? context)
    {
        // OpenFeature always hands providers a merged context, empty when nothing was set anywhere. Treat empty as
        // "no context" so Flagon falls back to its ambient context (IEvaluationContextAccessor), e.g. the request's user.
        if (context is null || (context.Count == 0 && context.TargetingKey is null))
        {
            return null;
        }

        var attributes = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var (key, value) in context.AsDictionary())
        {
            attributes[key] = value?.AsObject;
        }

        return new Jorrk.Flagon.EvaluationContext(context.TargetingKey, attributes);
    }

    private static ErrorType ToErrorType(ErrorCode code) => code switch
    {
        ErrorCode.None => ErrorType.None,
        ErrorCode.ProviderNotReady => ErrorType.ProviderNotReady,
        ErrorCode.FlagNotFound => ErrorType.FlagNotFound,
        ErrorCode.ParseError => ErrorType.ParseError,
        ErrorCode.TypeMismatch => ErrorType.TypeMismatch,
        ErrorCode.TargetingKeyMissing => ErrorType.TargetingKeyMissing,
        ErrorCode.InvalidContext => ErrorType.InvalidContext,
        ErrorCode.ProviderFatal => ErrorType.ProviderFatal,
        _ => ErrorType.General,
    };
}
