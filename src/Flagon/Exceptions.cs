namespace Flagon;

/// <summary>Base type for exceptions raised by Flagon.</summary>
public class FlagonException : Exception
{
    /// <summary>Creates the exception.</summary>
    public FlagonException(string message, Exception? innerException = null) : base(message, innerException) { }
}

/// <summary>A flag key, request or document failed validation.</summary>
public class FlagValidationException : FlagonException
{
    /// <summary>Creates the exception.</summary>
    public FlagValidationException(string message) : base(message) { }
}

/// <summary>
/// A flag's <c>targeting</c> rule was rejected on write: Flagon's targeting engine cannot compile it (an operator outside
/// flagd's set, an unresolved <c>$ref</c>, or a rule that fails for any context).
/// </summary>
public class FlagTargetingException : FlagValidationException
{
    /// <summary>Creates the exception.</summary>
    /// <param name="key">The flag key.</param>
    /// <param name="error">Why the rule was rejected.</param>
    /// <param name="path">A JSON Pointer to the failing operation within the rule (empty for the rule itself), if known.</param>
    /// <param name="operator">The failing operator, if known.</param>
    public FlagTargetingException(string key, string error, string? path = null, string? @operator = null)
        : base(FormatMessage(key, error, path))
    {
        Key = key;
        Error = error;
        Path = path;
        Operator = @operator;
    }

    /// <summary>The flag key.</summary>
    public string Key { get; }

    /// <summary>Why the rule was rejected (the targeting compiler's message).</summary>
    public string Error { get; }

    /// <summary>
    /// A JSON Pointer (RFC 6901) to the failing operation within the <c>targeting</c> rule, such as <c>/if/0</c>; empty
    /// when the failing operation is the rule itself; null if unknown.
    /// </summary>
    public string? Path { get; }

    /// <summary>The operator that failed, such as <c>regex</c> or <c>$ref</c>; null if unknown.</summary>
    public string? Operator { get; }

    private static string FormatMessage(string key, string error, string? path) => path switch
    {
        null => $"Flag '{key}' has an invalid targeting rule: {error}.",
        "" => $"Flag '{key}' has an invalid targeting rule at its root: {error}.",
        _ => $"Flag '{key}' has an invalid targeting rule at {path}: {error}.",
    };
}

/// <summary>No flag exists with the given key.</summary>
public class FlagNotFoundException : FlagonException
{
    /// <summary>Creates the exception.</summary>
    public FlagNotFoundException(string key) : base($"Flag '{key}' does not exist.") => Key = key;

    /// <summary>The missing key.</summary>
    public string Key { get; }
}

/// <summary>A flag with the given key already exists.</summary>
public class FlagAlreadyExistsException : FlagonException
{
    /// <summary>Creates the exception.</summary>
    public FlagAlreadyExistsException(string key) : base($"Flag '{key}' already exists.") => Key = key;

    /// <summary>The conflicting key.</summary>
    public string Key { get; }
}

/// <summary>An update was based on a stale version: someone else changed the flag first.</summary>
public class FlagVersionConflictException : FlagonException
{
    /// <summary>Creates the exception.</summary>
    public FlagVersionConflictException(string key, long expectedVersion, long currentVersion)
        : base($"Flag '{key}' was changed by someone else (expected version {expectedVersion}, current version {currentVersion}). Reload and try again.")
    {
        Key = key;
        ExpectedVersion = expectedVersion;
        CurrentVersion = currentVersion;
    }

    /// <summary>The flag key.</summary>
    public string Key { get; }

    /// <summary>The version the caller expected.</summary>
    public long ExpectedVersion { get; }

    /// <summary>The flag's current version in the store.</summary>
    public long CurrentVersion { get; }
}
