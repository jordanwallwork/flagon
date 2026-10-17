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
