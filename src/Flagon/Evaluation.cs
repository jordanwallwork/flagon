namespace Flagon;

/// <summary>Why an evaluation returned its value. Aligned with OpenFeature's resolution reasons.</summary>
public enum EvaluationReason
{
    /// <summary>The flag has no targeting; its default variant was served (OpenFeature <c>STATIC</c>).</summary>
    Static,

    /// <summary>The flag's default variant was served without evaluating targeting (OpenFeature <c>DEFAULT</c>).</summary>
    Default,

    /// <summary>A targeting rule matched (OpenFeature <c>TARGETING_MATCH</c>). Reserved: not produced in the MVP.</summary>
    TargetingMatch,

    /// <summary>A pseudorandom split assigned the value (OpenFeature <c>SPLIT</c>). Reserved: not produced in the MVP.</summary>
    Split,

    /// <summary>The value came from a cache (OpenFeature <c>CACHED</c>).</summary>
    Cached,

    /// <summary>The flag is disabled (flagd <c>DISABLED</c>); the caller's default was returned (OpenFeature <c>DISABLED</c>).</summary>
    Disabled,

    /// <summary>The reason is unknown (OpenFeature <c>UNKNOWN</c>).</summary>
    Unknown,

    /// <summary>The value may be stale (OpenFeature <c>STALE</c>).</summary>
    Stale,

    /// <summary>An error occurred; the caller's default was returned. See <see cref="FlagEvaluationDetails{T}.ErrorCode"/> (OpenFeature <c>ERROR</c>).</summary>
    Error,

    /// <summary>
    /// The flag is archived; the caller's default was returned. Not an error. A Flagon-specific reason
    /// (OpenFeature allows custom reasons), surfaced as <c>ARCHIVED</c>.
    /// </summary>
    Archived,
}

/// <summary>Evaluation error codes. Aligned with OpenFeature's error codes.</summary>
public enum ErrorCode
{
    /// <summary>No error.</summary>
    None,

    /// <summary>The provider is not ready (OpenFeature <c>PROVIDER_NOT_READY</c>).</summary>
    ProviderNotReady,

    /// <summary>No flag exists with the key (OpenFeature <c>FLAG_NOT_FOUND</c>).</summary>
    FlagNotFound,

    /// <summary>The flag definition could not be interpreted (OpenFeature <c>PARSE_ERROR</c>).</summary>
    ParseError,

    /// <summary>The flag's value is not of the requested type (OpenFeature <c>TYPE_MISMATCH</c>).</summary>
    TypeMismatch,

    /// <summary>Targeting requires a targeting key that was not supplied (OpenFeature <c>TARGETING_KEY_MISSING</c>).</summary>
    TargetingKeyMissing,

    /// <summary>The evaluation context is invalid (OpenFeature <c>INVALID_CONTEXT</c>).</summary>
    InvalidContext,

    /// <summary>The provider is in an unrecoverable state (OpenFeature <c>PROVIDER_FATAL</c>).</summary>
    ProviderFatal,

    /// <summary>Any other error (OpenFeature <c>GENERAL</c>).</summary>
    General,
}

/// <summary>The detailed result of evaluating a flag, modelled on OpenFeature's evaluation details.</summary>
/// <typeparam name="T">The flag's value type.</typeparam>
/// <param name="FlagKey">The evaluated key.</param>
/// <param name="Value">The resolved value (the caller's default on error, disabled, or missing).</param>
/// <param name="Variant">The variant served, or null when the caller's default was returned.</param>
/// <param name="Reason">Why <paramref name="Value"/> was returned.</param>
/// <param name="ErrorCode">The error, if <paramref name="Reason"/> is <see cref="EvaluationReason.Error"/>.</param>
/// <param name="ErrorMessage">Optional human-readable error detail.</param>
public sealed record FlagEvaluationDetails<T>(
    string FlagKey,
    T Value,
    string? Variant,
    EvaluationReason Reason,
    ErrorCode ErrorCode = ErrorCode.None,
    string? ErrorMessage = null);

/// <summary>
/// The context a flag is evaluated in: a targeting key (usually a user id) plus string-keyed attributes.
/// Accepted by every evaluation API today; the MVP boolean engine does not use it.
/// </summary>
public sealed class EvaluationContext
{
    /// <summary>An empty context.</summary>
    public static EvaluationContext Empty { get; } = new();

    /// <summary>Creates a context.</summary>
    public EvaluationContext(string? targetingKey = null, IReadOnlyDictionary<string, object?>? attributes = null)
    {
        TargetingKey = targetingKey;
        Attributes = attributes ?? new Dictionary<string, object?>(StringComparer.Ordinal);
    }

    /// <summary>The subject being evaluated for (e.g. a user id).</summary>
    public string? TargetingKey { get; }

    /// <summary>Additional attributes, e.g. <c>email</c>, <c>country</c>.</summary>
    public IReadOnlyDictionary<string, object?> Attributes { get; }
}

/// <summary>
/// In-process flag evaluation. Evaluation never performs I/O: it reads an in-memory snapshot of the store.
/// Missing flags return the caller's default.
/// </summary>
public interface IFlagon
{
    /// <summary>Returns whether the boolean flag <paramref name="key"/> is on, or <paramref name="defaultValue"/> if it cannot be evaluated.</summary>
    bool IsEnabled(string key, bool defaultValue = false, EvaluationContext? context = null);

    /// <summary>Async counterpart of <see cref="IsEnabled"/>. Completes synchronously.</summary>
    ValueTask<bool> IsEnabledAsync(string key, bool defaultValue = false, EvaluationContext? context = null, CancellationToken cancellationToken = default);

    /// <summary>Evaluates the boolean flag <paramref name="key"/> and explains the result.</summary>
    FlagEvaluationDetails<bool> GetBooleanDetails(string key, bool defaultValue = false, EvaluationContext? context = null);

    /// <summary>Async counterpart of <see cref="GetBooleanDetails"/>. Completes synchronously.</summary>
    ValueTask<FlagEvaluationDetails<bool>> GetBooleanDetailsAsync(string key, bool defaultValue = false, EvaluationContext? context = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Evaluates every flag in the current snapshot (bulk evaluation, e.g. for OFREP clients), ordered by key.
    /// Each flag is evaluated with a caller default of <c>false</c>; an entry whose
    /// <see cref="FlagEvaluationDetails{T}.Variant"/> is null (e.g. a disabled flag, or an error) carries no flag value,
    /// so the consumer should apply its own default.
    /// </summary>
    FlagEvaluationSet<bool> GetAllBooleanDetails(EvaluationContext? context = null);
}

/// <summary>The result of evaluating every flag in one snapshot.</summary>
/// <typeparam name="T">The flags' value type.</typeparam>
/// <param name="Version">
/// The store change version of the snapshot the flags were evaluated from. It changes whenever any flag changes,
/// so it is a suitable cache validator (e.g. an HTTP ETag) for the set.
/// </param>
/// <param name="Flags">One result per flag, ordered by key (ordinal).</param>
public sealed record FlagEvaluationSet<T>(long Version, IReadOnlyList<FlagEvaluationDetails<T>> Flags);
