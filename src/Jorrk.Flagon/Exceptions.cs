namespace Jorrk.Flagon;

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
/// flagd's set, a <c>$ref</c> to a segment that does not exist, or a rule that fails for any context).
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

    internal FlagTargetingException(string key, Internal.Targeting.TargetingError error)
        : this(key, error.Message, error.Path, error.Operator)
    {
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

/// <summary>No segment exists with the given key.</summary>
public class SegmentNotFoundException : FlagonException
{
    /// <summary>Creates the exception.</summary>
    public SegmentNotFoundException(string key) : base($"Segment '{key}' does not exist.") => Key = key;

    /// <summary>The missing key.</summary>
    public string Key { get; }
}

/// <summary>A segment with the given key already exists.</summary>
public class SegmentAlreadyExistsException : FlagonException
{
    /// <summary>Creates the exception.</summary>
    public SegmentAlreadyExistsException(string key) : base($"Segment '{key}' already exists.") => Key = key;

    /// <summary>The conflicting key.</summary>
    public string Key { get; }
}

/// <summary>A segment write was based on a stale version: someone else changed the segment first.</summary>
public class SegmentVersionConflictException : FlagonException
{
    /// <summary>Creates the exception.</summary>
    public SegmentVersionConflictException(string key, long expectedVersion, long currentVersion)
        : base($"Segment '{key}' was changed by someone else (expected version {expectedVersion}, current version {currentVersion}). Reload and try again.")
    {
        Key = key;
        ExpectedVersion = expectedVersion;
        CurrentVersion = currentVersion;
    }

    /// <summary>The segment key.</summary>
    public string Key { get; }

    /// <summary>The version the caller expected.</summary>
    public long ExpectedVersion { get; }

    /// <summary>The segment's current version in the store.</summary>
    public long CurrentVersion { get; }
}

/// <summary>
/// A segment cannot be archived or purged because flags reference it: archiving is blocked by non-archived flags,
/// purging by any flag (an archived flag can be restored).
/// </summary>
public class SegmentInUseException : FlagonException
{
    /// <summary>Creates the exception.</summary>
    /// <param name="key">The segment key.</param>
    /// <param name="flagKeys">The flags that reference it.</param>
    /// <param name="action">What was refused, such as <c>archived</c> or <c>purged</c>; used in the message.</param>
    public SegmentInUseException(string key, IReadOnlyList<string> flagKeys, string action = "changed")
        : base($"Segment '{key}' cannot be {action}: it is referenced by {Describe(flagKeys)}. Remove the references first.")
    {
        Key = key;
        FlagKeys = flagKeys;
    }

    /// <summary>The segment key.</summary>
    public string Key { get; }

    /// <summary>The flags that reference the segment.</summary>
    public IReadOnlyList<string> FlagKeys { get; }

    private static string Describe(IReadOnlyList<string> flagKeys) =>
        (flagKeys.Count == 1 ? "flag " : "flags ") + string.Join(", ", flagKeys.Select(k => $"'{k}'"));
}

/// <summary>
/// A flag's targeting references archived segments, which cannot be newly referenced: on create, a targeting change or
/// a restore of the flag. Restore the segments first.
/// </summary>
public class SegmentArchivedException : FlagValidationException
{
    /// <summary>Creates the exception.</summary>
    /// <param name="flagKey">The flag key.</param>
    /// <param name="segmentKeys">The archived segments it references.</param>
    public SegmentArchivedException(string flagKey, IReadOnlyList<string> segmentKeys)
        : base($"Flag '{flagKey}' references archived {(segmentKeys.Count == 1 ? "segment" : "segments")} {string.Join(", ", segmentKeys.Select(k => $"'{k}'"))}; restore {(segmentKeys.Count == 1 ? "it" : "them")} first.")
    {
        FlagKey = flagKey;
        SegmentKeys = segmentKeys;
    }

    /// <summary>The flag key.</summary>
    public string FlagKey { get; }

    /// <summary>The archived segments the flag references.</summary>
    public IReadOnlyList<string> SegmentKeys { get; }
}

/// <summary>
/// A segment's rule was rejected on write: it is not a rule, contains <c>$ref</c>, does not compile on its own, or would
/// stop a flag that references the segment from compiling.
/// </summary>
public class SegmentRuleException : FlagValidationException
{
    /// <summary>Creates the exception.</summary>
    /// <param name="key">The segment key.</param>
    /// <param name="error">Why the rule was rejected.</param>
    /// <param name="path">A JSON Pointer to the failing operation within the segment's rule (empty for the rule itself), if known.</param>
    /// <param name="operator">The failing operator, if known.</param>
    public SegmentRuleException(string key, string error, string? path = null, string? @operator = null)
        : base(FormatMessage(key, error, path))
    {
        Key = key;
        Error = error;
        Path = path;
        Operator = @operator;
    }

    internal SegmentRuleException(string key, Internal.Targeting.TargetingError error)
        : this(key, error.Message, error.Path, error.Operator)
    {
    }

    /// <summary>The segment key.</summary>
    public string Key { get; }

    /// <summary>Why the rule was rejected.</summary>
    public string Error { get; }

    /// <summary>
    /// A JSON Pointer (RFC 6901) to the failing operation within the segment's rule; empty when it is the rule itself;
    /// null if unknown or the failure is in a flag that references the segment.
    /// </summary>
    public string? Path { get; }

    /// <summary>The operator that failed, if known.</summary>
    public string? Operator { get; }

    private static string FormatMessage(string key, string error, string? path) => path switch
    {
        null => $"Segment '{key}' has an invalid rule: {error}.",
        "" => $"Segment '{key}' has an invalid rule at its root: {error}.",
        _ => $"Segment '{key}' has an invalid rule at {path}: {error}.",
    };
}
