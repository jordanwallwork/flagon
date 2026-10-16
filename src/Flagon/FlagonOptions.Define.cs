using Flagon.Internal;

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
    /// <param name="key">The flag key (see <see cref="FlagKey"/>).</param>
    /// <param name="defaultValue">The value a newly created flag serves.</param>
    /// <param name="description">Human-readable description.</param>
    /// <param name="tags">Tags. Tags must be non-blank and may not contain commas.</param>
    /// <exception cref="FlagValidationException">The key or a tag is invalid.</exception>
    /// <exception cref="ArgumentException">The key has already been defined.</exception>
    public FlagonOptions Define(string key, bool defaultValue = false, string? description = null, IEnumerable<string>? tags = null)
    {
        FlagKey.Validate(key);
        var tagList = tags?.ToArray() ?? [];
        foreach (var tag in tagList)
        {
            if (string.IsNullOrWhiteSpace(tag) || tag.Contains(',', StringComparison.Ordinal))
            {
                throw new FlagValidationException($"'{tag}' is not a valid tag. Tags must be non-blank and may not contain commas.");
            }
        }

        if (_definitions.Exists(d => string.Equals(d.Key, key, StringComparison.Ordinal)))
        {
            throw new ArgumentException($"Flag '{key}' has already been defined.", nameof(key));
        }

        _definitions.Add(new FlagDefinition(key, defaultValue, description, tagList));
        return this;
    }
}
