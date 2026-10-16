namespace Flagon.Storage;

/// <summary>
/// The storage provider seam. A store holds one <see cref="FlagRecord"/> per key and stamps every write with a
/// version taken from a single store-wide, monotonically increasing change sequence, so
/// <see cref="GetChangeVersionAsync"/> detects any change with one cheap call.
/// </summary>
/// <remarks>
/// Keys are case-sensitive. Implementations must be thread-safe and safe to share between several apps.
/// The contract is completed (archive/restore/purge, audit, usage, migrations) and verified by a conformance suite in a later version.
/// </remarks>
public interface IFlagStore
{
    /// <summary>True for stores that cannot be written (auto-create and usage tracking then skip silently).</summary>
    bool IsReadOnly { get; }

    /// <summary>The highest change version assigned so far (0 for an empty, never-written store).</summary>
    ValueTask<long> GetChangeVersionAsync(CancellationToken cancellationToken = default);

    /// <summary>Loads every flag.</summary>
    Task<IReadOnlyList<FlagRecord>> LoadAllAsync(CancellationToken cancellationToken = default);

    /// <summary>Gets one flag, or null.</summary>
    Task<FlagRecord?> GetAsync(string key, CancellationToken cancellationToken = default);

    /// <summary>Inserts the flag if no flag with <paramref name="key"/> exists. Returns the created record, or null if the key was taken.</summary>
    Task<FlagRecord?> TryCreateAsync(string key, FlagDocument document, CancellationToken cancellationToken = default);

    /// <summary>Replaces the flag's document if its version is still <paramref name="expectedVersion"/>; returns the new record.</summary>
    /// <exception cref="FlagNotFoundException">No flag exists with the key.</exception>
    /// <exception cref="FlagVersionConflictException">The stored version differs from <paramref name="expectedVersion"/>.</exception>
    Task<FlagRecord> UpdateAsync(string key, FlagDocument document, long expectedVersion, CancellationToken cancellationToken = default);
}
