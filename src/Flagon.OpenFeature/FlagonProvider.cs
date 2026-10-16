using OpenFeature;
using OpenFeature.Constant;
using OpenFeature.Model;
using OfEvaluationContext = OpenFeature.Model.EvaluationContext;

namespace Flagon.OpenFeature;

/// <summary>
/// An OpenFeature provider backed by Flagon's in-process evaluation (<see cref="IFlagon"/>).
/// Resolution reads Flagon's in-memory snapshot and performs no I/O. Only boolean flags are supported:
/// string, integer, double and structure resolutions return <see cref="ErrorType.TypeMismatch"/> with the caller's default.
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
    public override Task<ResolutionDetails<bool>> ResolveBooleanValueAsync(string flagKey, bool defaultValue, OfEvaluationContext? context = null, CancellationToken cancellationToken = default)
    {
        var details = _flagon.GetBooleanDetails(flagKey, defaultValue, ToFlagonContext(context));
        return Task.FromResult(new ResolutionDetails<bool>(
            details.FlagKey,
            details.Value,
            ToErrorType(details.ErrorCode),
            ToReason(details.Reason),
            details.Variant,
            details.ErrorMessage));
    }

    /// <inheritdoc />
    public override Task<ResolutionDetails<string>> ResolveStringValueAsync(string flagKey, string defaultValue, OfEvaluationContext? context = null, CancellationToken cancellationToken = default) =>
        Unsupported(flagKey, defaultValue, "string");

    /// <inheritdoc />
    public override Task<ResolutionDetails<int>> ResolveIntegerValueAsync(string flagKey, int defaultValue, OfEvaluationContext? context = null, CancellationToken cancellationToken = default) =>
        Unsupported(flagKey, defaultValue, "integer");

    /// <inheritdoc />
    public override Task<ResolutionDetails<double>> ResolveDoubleValueAsync(string flagKey, double defaultValue, OfEvaluationContext? context = null, CancellationToken cancellationToken = default) =>
        Unsupported(flagKey, defaultValue, "double");

    /// <inheritdoc />
    public override Task<ResolutionDetails<Value>> ResolveStructureValueAsync(string flagKey, Value defaultValue, OfEvaluationContext? context = null, CancellationToken cancellationToken = default) =>
        Unsupported(flagKey, defaultValue, "structure");

    private static Task<ResolutionDetails<T>> Unsupported<T>(string flagKey, T defaultValue, string type) =>
        Task.FromResult(new ResolutionDetails<T>(
            flagKey,
            defaultValue,
            ErrorType.TypeMismatch,
            Reason.Error,
            errorMessage: $"Flagon supports only boolean flags; '{flagKey}' cannot be resolved as a {type}."));

    private static Flagon.EvaluationContext? ToFlagonContext(OfEvaluationContext? context)
    {
        if (context is null)
        {
            return null;
        }

        var attributes = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var (key, value) in context.AsDictionary())
        {
            attributes[key] = value?.AsObject;
        }

        return new Flagon.EvaluationContext(context.TargetingKey, attributes);
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

    private static string ToReason(EvaluationReason reason) => reason switch
    {
        EvaluationReason.Static => Reason.Static,
        EvaluationReason.Default => Reason.Default,
        EvaluationReason.TargetingMatch => Reason.TargetingMatch,
        EvaluationReason.Split => Reason.Split,
        EvaluationReason.Cached => Reason.Cached,
        EvaluationReason.Disabled => Reason.Disabled,
        EvaluationReason.Unknown => Reason.Unknown,
        EvaluationReason.Stale => "STALE",
        EvaluationReason.Error => Reason.Error,
        // Reasons Flagon adds later (e.g. Archived) surface as OpenFeature custom reasons in SCREAMING_SNAKE_CASE.
        _ => ToScreamingSnakeCase(reason.ToString()),
    };

    private static string ToScreamingSnakeCase(string name)
    {
        var builder = new System.Text.StringBuilder(name.Length + 4);
        for (var i = 0; i < name.Length; i++)
        {
            if (i > 0 && char.IsUpper(name[i]))
            {
                builder.Append('_');
            }
            builder.Append(char.ToUpperInvariant(name[i]));
        }
        return builder.ToString();
    }
}
