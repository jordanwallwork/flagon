using System.Text.Json;

namespace Flagon;

/// <summary>Why an evaluation returned its value. Aligned with OpenFeature's resolution reasons.</summary>
public enum EvaluationReason
{
    /// <summary>The flag has no targeting; its default variant was served (OpenFeature <c>STATIC</c>).</summary>
    Static,

    /// <summary>
    /// The flag's targeting rule did not match (resolved to null), so its default variant was served (OpenFeature
    /// <c>DEFAULT</c>). A flag with no default variant returns the caller's default with this reason, as flagd does.
    /// </summary>
    Default,

    /// <summary>
    /// The flag's targeting rule resolved to one of its variants, which was served (OpenFeature <c>TARGETING_MATCH</c>).
    /// A <c>fractional</c> rollout reports this reason too, as flagd does.
    /// </summary>
    TargetingMatch,

    /// <summary>
    /// A pseudorandom split assigned the value (OpenFeature <c>SPLIT</c>). Not produced by Flagon: like flagd, a
    /// <c>fractional</c> rollout reports <see cref="TargetingMatch"/>.
    /// </summary>
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
/// A flag's targeting rule is evaluated against it (with <c>targetingKey</c> injected, as flagd does).
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
/// <remarks>
/// Every method takes an optional <see cref="EvaluationContext"/>. When none is passed, the ambient context from the
/// registered <see cref="IEvaluationContextAccessor"/> is used (e.g. the current request's user with
/// <c>Flagon.AspNetCore</c>'s <c>AddFlagonAmbientContext()</c>); an explicitly passed context always wins.
/// </remarks>
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
    /// Returns the string flag <paramref name="key"/>'s value, or <paramref name="defaultValue"/> if it cannot be evaluated
    /// (missing, archived, disabled, no variant served, an error, or a served value that is not a string).
    /// </summary>
    string GetString(string key, string defaultValue, EvaluationContext? context = null);

    /// <summary>Async counterpart of <see cref="GetString"/>. Completes synchronously.</summary>
    ValueTask<string> GetStringAsync(string key, string defaultValue, EvaluationContext? context = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Evaluates the string flag <paramref name="key"/> and explains the result. A served variant that is not a string
    /// returns <paramref name="defaultValue"/> with <see cref="EvaluationReason.Error"/> and <see cref="ErrorCode.TypeMismatch"/>.
    /// </summary>
    FlagEvaluationDetails<string> GetStringDetails(string key, string defaultValue, EvaluationContext? context = null);

    /// <summary>Async counterpart of <see cref="GetStringDetails"/>. Completes synchronously.</summary>
    ValueTask<FlagEvaluationDetails<string>> GetStringDetailsAsync(string key, string defaultValue, EvaluationContext? context = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the number flag <paramref name="key"/>'s value as an <see cref="int"/>, or <paramref name="defaultValue"/>
    /// if it cannot be evaluated. Only an integral value within <see cref="int"/> range is served: a fractional or
    /// out-of-range number is a type mismatch, never truncated.
    /// </summary>
    int GetInt(string key, int defaultValue, EvaluationContext? context = null);

    /// <summary>Async counterpart of <see cref="GetInt"/>. Completes synchronously.</summary>
    ValueTask<int> GetIntAsync(string key, int defaultValue, EvaluationContext? context = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Evaluates the number flag <paramref name="key"/> as an <see cref="int"/> and explains the result. A served variant
    /// that is not an integral number within <see cref="int"/> range returns <paramref name="defaultValue"/> with
    /// <see cref="EvaluationReason.Error"/> and <see cref="ErrorCode.TypeMismatch"/>.
    /// </summary>
    FlagEvaluationDetails<int> GetIntDetails(string key, int defaultValue, EvaluationContext? context = null);

    /// <summary>Async counterpart of <see cref="GetIntDetails"/>. Completes synchronously.</summary>
    ValueTask<FlagEvaluationDetails<int>> GetIntDetailsAsync(string key, int defaultValue, EvaluationContext? context = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the number flag <paramref name="key"/>'s value as a <see cref="double"/> (integral or fractional), or
    /// <paramref name="defaultValue"/> if it cannot be evaluated.
    /// </summary>
    double GetDouble(string key, double defaultValue, EvaluationContext? context = null);

    /// <summary>Async counterpart of <see cref="GetDouble"/>. Completes synchronously.</summary>
    ValueTask<double> GetDoubleAsync(string key, double defaultValue, EvaluationContext? context = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Evaluates the number flag <paramref name="key"/> as a <see cref="double"/> and explains the result. A served
    /// variant that is not a number returns <paramref name="defaultValue"/> with <see cref="EvaluationReason.Error"/> and
    /// <see cref="ErrorCode.TypeMismatch"/>.
    /// </summary>
    FlagEvaluationDetails<double> GetDoubleDetails(string key, double defaultValue, EvaluationContext? context = null);

    /// <summary>Async counterpart of <see cref="GetDoubleDetails"/>. Completes synchronously.</summary>
    ValueTask<FlagEvaluationDetails<double>> GetDoubleDetailsAsync(string key, double defaultValue, EvaluationContext? context = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the object flag <paramref name="key"/>'s value deserialised as <typeparamref name="T"/> with
    /// <paramref name="options"/> (<see cref="JsonSerializer"/>'s defaults when null), or <paramref name="defaultValue"/>
    /// if it cannot be evaluated or deserialised. With <typeparamref name="T"/> <see cref="JsonElement"/> (inferred from a
    /// <see cref="JsonElement"/> default, e.g. <c>GetObject("layout", fallbackJson)</c>) the served JSON object is
    /// returned as is.
    /// </summary>
    T GetObject<T>(string key, T defaultValue, EvaluationContext? context = null, JsonSerializerOptions? options = null);

    /// <summary>Async counterpart of <see cref="GetObject{T}"/>. Completes synchronously.</summary>
    ValueTask<T> GetObjectAsync<T>(string key, T defaultValue, EvaluationContext? context = null, JsonSerializerOptions? options = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Evaluates the object flag <paramref name="key"/>, deserialises it as <typeparamref name="T"/> (as
    /// <see cref="GetObject{T}"/> does) and explains the result. A served variant that is not a JSON object, or that fails
    /// to deserialise, returns
    /// <paramref name="defaultValue"/> with <see cref="EvaluationReason.Error"/> and <see cref="ErrorCode.TypeMismatch"/>.
    /// </summary>
    FlagEvaluationDetails<T> GetObjectDetails<T>(string key, T defaultValue, EvaluationContext? context = null, JsonSerializerOptions? options = null);

    /// <summary>Async counterpart of <see cref="GetObjectDetails{T}"/>. Completes synchronously.</summary>
    ValueTask<FlagEvaluationDetails<T>> GetObjectDetailsAsync<T>(string key, T defaultValue, EvaluationContext? context = null, JsonSerializerOptions? options = null, CancellationToken cancellationToken = default);

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
