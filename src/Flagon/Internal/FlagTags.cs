namespace Flagon.Internal;

/// <summary>The one tag rule, shared by <see cref="FlagonOptions.Define"/> and <see cref="FlagonAdmin"/>.</summary>
internal static class FlagTags
{
    /// <summary>
    /// Throws <see cref="FlagValidationException"/> unless every tag is non-blank, comma-free and has no leading or
    /// trailing whitespace (tags are stored comma-separated, because flagd metadata values must be primitives, and read
    /// back trimmed — so a padded tag would not survive a round trip).
    /// </summary>
    public static void Validate(IEnumerable<string>? tags)
    {
        foreach (var tag in tags ?? [])
        {
            if (string.IsNullOrWhiteSpace(tag) || tag.Contains(',', StringComparison.Ordinal) || tag.Trim().Length != tag.Length)
            {
                throw new FlagValidationException(
                    $"'{tag}' is not a valid tag. Tags must be non-blank, may not contain commas and may not start or end with whitespace.");
            }
        }
    }
}
