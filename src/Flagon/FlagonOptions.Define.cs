using Flagon.Internal;
using Flagon.Internal.Targeting;

namespace Flagon;

public sealed partial class FlagonOptions
{
    private readonly List<FlagDefinition> _definitions = [];

    internal IReadOnlyList<FlagDefinition> Definitions => _definitions;

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
    /// default. Validated here with Flagon's targeting engine; <c>$ref</c> is not supported (inline the evaluator).
    /// </param>
    /// <exception cref="FlagValidationException">The key, a tag or the targeting JSON is invalid.</exception>
    /// <exception cref="FlagTargetingException">The targeting rule does not compile.</exception>
    /// <exception cref="ArgumentException">The key has already been defined.</exception>
    public FlagonOptions Define(string key, bool defaultValue = false, string? description = null, IEnumerable<string>? tags = null, string? targeting = null)
    {
        FlagKey.Validate(key);
        var tagList = tags?.ToArray() ?? [];
        FlagTags.Validate(tagList);
        var rule = TargetingRules.Parse(TargetingRules.Engine, key, targeting);

        if (_definitions.Exists(d => string.Equals(d.Key, key, StringComparison.Ordinal)))
        {
            throw new ArgumentException($"Flag '{key}' has already been defined.", nameof(key));
        }

        _definitions.Add(new FlagDefinition(key, defaultValue, description, tagList, rule));
        return this;
    }
}
