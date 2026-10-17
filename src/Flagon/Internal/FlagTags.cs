namespace Flagon.Internal;

/// <summary>The one tag rule, shared by <see cref="FlagonOptions.Define"/> and <see cref="FlagonAdmin"/>.</summary>
internal static class FlagTags
{
    /// <summary>
    /// Throws <see cref="FlagValidationException"/> unless every tag is non-blank and comma-free (tags are stored
    /// comma-separated, because flagd metadata values must be primitives).
    /// </summary>
    public static void Validate(IEnumerable<string>? tags)
    {
        foreach (var tag in tags ?? [])
        {
            if (string.IsNullOrWhiteSpace(tag) || tag.Contains(',', StringComparison.Ordinal))
            {
                throw new FlagValidationException($"'{tag}' is not a valid tag. Tags must be non-blank and may not contain commas.");
            }
        }
    }
}
