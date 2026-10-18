using System.Text.RegularExpressions;

namespace Jorrk.Flagon;

/// <summary>Flag key rules: letters, digits, <c>.</c>, <c>_</c>, <c>-</c>; starts with a letter or digit; 1–128 chars; case-sensitive.</summary>
public static partial class FlagKey
{
    /// <summary>The documented key pattern.</summary>
    public const string Pattern = "^[a-zA-Z0-9][a-zA-Z0-9._-]{0,127}$";

    /// <summary>Maximum key length.</summary>
    public const int MaxLength = 128;

    /// <summary>Whether <paramref name="key"/> is a valid flag key.</summary>
    public static bool IsValid(string? key) => key is not null && KeyRegex().IsMatch(key);

    /// <summary>Throws <see cref="FlagValidationException"/> if <paramref name="key"/> is invalid.</summary>
    public static void Validate(string? key)
    {
        if (!IsValid(key))
        {
            throw new FlagValidationException(
                $"'{key}' is not a valid flag key. Keys are 1-{MaxLength} characters of letters, digits, '.', '_' or '-', starting with a letter or digit.");
        }
    }

    // \z rather than $ so a trailing newline is rejected.
    [GeneratedRegex(@"^[a-zA-Z0-9][a-zA-Z0-9._-]{0,127}\z", RegexOptions.CultureInvariant)]
    private static partial Regex KeyRegex();
}
