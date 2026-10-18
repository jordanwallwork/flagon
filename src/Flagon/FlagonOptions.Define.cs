using System.Text.Json;
using Flagon.Internal;
using Flagon.Internal.Targeting;

namespace Flagon;

public sealed partial class FlagonOptions
{
    private readonly List<FlagDefinition> _definitions = [];

    internal IReadOnlyList<FlagDefinition> Definitions => _definitions;

    /// <summary>The variant name a flag defined with a single value (string, number or object) is created with.</summary>
    private const string SingleVariant = "default";

    /// <summary>
    /// Declares a boolean flag in code. On startup (after migrations) a missing flag is created with source
    /// <see cref="FlagSource.Code"/> serving <paramref name="defaultValue"/>; an existing flag has its description, tags
    /// and source brought in line with this definition but its state is never changed. Archived flags are left alone.
    /// </summary>
    /// <remarks>
    /// Like <paramref name="defaultValue"/>, <paramref name="targeting"/> applies only when the flag is created: an
    /// existing flag keeps whatever targeting it has (an admin may have changed it).
    /// </remarks>
    /// <param name="key">The flag key (see <see cref="FlagKey"/>).</param>
    /// <param name="defaultValue">The value a newly created flag serves.</param>
    /// <param name="description">Human-readable description.</param>
    /// <param name="tags">Tags. Tags must be non-blank, may not contain commas and may not start or end with whitespace.</param>
    /// <param name="targeting">
    /// Optional flagd targeting rule as JSON text, e.g. <c>{"if":[{"in":[{"var":"email"},["a@example.com"]]},"on",null]}</c>.
    /// A rule that matches serves the variant it names (<c>"on"</c> or <c>"off"</c>); otherwise the flag serves its
    /// default. Only the JSON syntax is checked here; the rule is compiled with the registered targeting compiler when
    /// the host starts, and a rule it rejects fails startup with <see cref="FlagTargetingException"/>. <c>$ref</c> is not
    /// supported by Flagon's targeting engine (inline the evaluator).
    /// </param>
    /// <exception cref="FlagValidationException">The key, a tag or the targeting JSON is invalid.</exception>
    /// <exception cref="ArgumentException">The key has already been defined.</exception>
    public FlagonOptions Define(string key, bool defaultValue = false, string? description = null, IEnumerable<string>? tags = null, string? targeting = null)
    {
        var boolean = FlagDocument.Boolean(defaultValue);
        return Add(key, boolean.Variants, boolean.DefaultVariant, description, tags, targeting);
    }

    /// <summary>
    /// Declares a string flag in code: a missing flag is created with a single <c>default</c> variant serving
    /// <paramref name="defaultValue"/>. Otherwise as <see cref="Define(string, bool, string?, IEnumerable{string}?, string?)"/>.
    /// </summary>
    /// <exception cref="FlagValidationException">The key, a tag or the targeting JSON is invalid.</exception>
    /// <exception cref="ArgumentException">The key has already been defined.</exception>
    public FlagonOptions Define(string key, string defaultValue, string? description = null, IEnumerable<string>? tags = null, string? targeting = null)
    {
        ArgumentNullException.ThrowIfNull(defaultValue);
        return Single(key, JsonSerializer.SerializeToElement(defaultValue), description, tags, targeting);
    }

    /// <summary>
    /// Declares a number flag in code: a missing flag is created with a single <c>default</c> variant serving
    /// <paramref name="defaultValue"/>. Otherwise as <see cref="Define(string, bool, string?, IEnumerable{string}?, string?)"/>.
    /// </summary>
    /// <exception cref="FlagValidationException">The key, a tag or the targeting JSON is invalid.</exception>
    /// <exception cref="ArgumentException">The key has already been defined.</exception>
    public FlagonOptions Define(string key, int defaultValue, string? description = null, IEnumerable<string>? tags = null, string? targeting = null) =>
        Single(key, JsonSerializer.SerializeToElement(defaultValue), description, tags, targeting);

    /// <summary>
    /// Declares a number flag in code: a missing flag is created with a single <c>default</c> variant serving
    /// <paramref name="defaultValue"/>. Otherwise as <see cref="Define(string, bool, string?, IEnumerable{string}?, string?)"/>.
    /// </summary>
    /// <exception cref="FlagValidationException">
    /// The key, a tag or the targeting JSON is invalid, or <paramref name="defaultValue"/> is NaN or infinite (JSON has no such numbers).
    /// </exception>
    /// <exception cref="ArgumentException">The key has already been defined.</exception>
    public FlagonOptions Define(string key, double defaultValue, string? description = null, IEnumerable<string>? tags = null, string? targeting = null) =>
        Single(key, Number(key, SingleVariant, defaultValue), description, tags, targeting);

    /// <summary>
    /// Declares an object flag in code: a missing flag is created with a single <c>default</c> variant serving the JSON
    /// object <paramref name="json"/>. Otherwise as <see cref="Define(string, bool, string?, IEnumerable{string}?, string?)"/>.
    /// </summary>
    /// <param name="key">The flag key (see <see cref="FlagKey"/>).</param>
    /// <param name="json">The value a newly created flag serves: JSON text of an object, e.g. <c>{"columns":3}</c>.</param>
    /// <param name="description">Human-readable description.</param>
    /// <param name="tags">Tags, as for the boolean overload.</param>
    /// <param name="targeting">Optional flagd targeting rule as JSON text, as for the boolean overload.</param>
    /// <exception cref="FlagValidationException">
    /// The key, a tag or the targeting JSON is invalid, or <paramref name="json"/> is not valid JSON or not an object.
    /// </exception>
    /// <exception cref="ArgumentException">The key has already been defined.</exception>
    public FlagonOptions DefineObject(string key, string json, string? description = null, IEnumerable<string>? tags = null, string? targeting = null)
    {
        ArgumentNullException.ThrowIfNull(json);
        JsonElement value;
        try
        {
            using var document = JsonDocument.Parse(json);
            value = document.RootElement.Clone();
        }
        catch (JsonException ex)
        {
            throw new FlagValidationException($"Flag '{key}' object value is not valid JSON: {ex.Message}");
        }

        if (value.ValueKind != JsonValueKind.Object)
        {
            throw new FlagValidationException(
                $"Flag '{key}' object value is a JSON {value.ValueKind.ToString().ToLowerInvariant()}; DefineObject takes a JSON object.");
        }

        return Single(key, value, description, tags, targeting);
    }

    /// <summary>
    /// Declares a flag with named variants in code: a missing flag is created with <paramref name="variants"/>, serving
    /// <paramref name="defaultVariant"/> when no targeting rule applies. Otherwise as
    /// <see cref="Define(string, bool, string?, IEnumerable{string}?, string?)"/>: an existing flag keeps its variants,
    /// default variant and targeting, and only has its metadata synced.
    /// </summary>
    /// <typeparam name="T">
    /// The variant value type: <see cref="bool"/>, <see cref="string"/>, <see cref="int"/>, <see cref="double"/> or
    /// <see cref="JsonElement"/> (for objects).
    /// </typeparam>
    /// <param name="key">The flag key (see <see cref="FlagKey"/>).</param>
    /// <param name="variants">Variant name to value, e.g. <c>small</c>/<c>medium</c>/<c>large</c>.</param>
    /// <param name="defaultVariant">
    /// The variant served when no targeting rule applies, or null for none (the caller's default is served).
    /// </param>
    /// <param name="description">Human-readable description.</param>
    /// <param name="tags">Tags, as for the boolean overload.</param>
    /// <param name="targeting">Optional flagd targeting rule as JSON text; a rule names the variant it serves.</param>
    /// <exception cref="FlagValidationException">
    /// The key, a tag or the targeting JSON is invalid; there are no variants; a variant name is empty, longer than 128
    /// characters or repeated; a value is null, a non-finite number, an array, or of a different type from the others;
    /// or <paramref name="defaultVariant"/> is not one of the variants.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// <typeparamref name="T"/> is not a supported variant type, or the key has already been defined.
    /// </exception>
    public FlagonOptions Define<T>(
        string key,
        IReadOnlyDictionary<string, T> variants,
        string? defaultVariant,
        string? description = null,
        IEnumerable<string>? tags = null,
        string? targeting = null)
    {
        ArgumentNullException.ThrowIfNull(variants);
        if (typeof(T) != typeof(bool) && typeof(T) != typeof(string) && typeof(T) != typeof(int)
            && typeof(T) != typeof(double) && typeof(T) != typeof(JsonElement))
        {
            throw new ArgumentException(
                $"Flag '{key}' variants are of type {typeof(T).Name}; use bool, string, int, double or JsonElement.", nameof(variants));
        }

        var values = variants.Select(v => KeyValuePair.Create(v.Key, v.Value switch
        {
            double number => Number(key, v.Key, number),
            JsonElement json => json,
            var other => JsonSerializer.SerializeToElement(other),
        }));
        return Add(key, values, defaultVariant, description, tags, targeting);
    }

    private static JsonElement Number(string key, string variant, double value) => double.IsFinite(value)
        ? JsonSerializer.SerializeToElement(value)
        : throw new FlagValidationException($"Flag '{key}' variant '{variant}' is {value}; number variants must be finite.");

    private FlagonOptions Single(string key, JsonElement value, string? description, IEnumerable<string>? tags, string? targeting) =>
        Add(key, new Dictionary<string, JsonElement>(StringComparer.Ordinal) { [SingleVariant] = value }, SingleVariant, description, tags, targeting);

    private FlagonOptions Add(
        string key,
        IEnumerable<KeyValuePair<string, JsonElement>> variants,
        string? defaultVariant,
        string? description,
        IEnumerable<string>? tags,
        string? targeting)
    {
        FlagKey.Validate(key);
        var tagList = tags?.ToArray() ?? [];
        FlagTags.Validate(tagList);
        var rule = TargetingRules.ParseJson(key, targeting);
        var (validated, type) = FlagVariants.Validate(key, variants, defaultVariant);

        if (_definitions.Exists(d => string.Equals(d.Key, key, StringComparison.Ordinal)))
        {
            throw new ArgumentException($"Flag '{key}' has already been defined.", nameof(key));
        }

        _definitions.Add(new FlagDefinition(key, validated, defaultVariant, type, description, tagList, rule));
        return this;
    }
}
