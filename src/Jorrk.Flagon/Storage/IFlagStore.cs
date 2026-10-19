namespace Jorrk.Flagon.Storage;

/// <summary>
/// The storage provider seam. A store holds one <see cref="FlagRecord"/> per key and stamps every write with a
/// version taken from a single store-wide, monotonically increasing change sequence, so
/// <see cref="GetChangeVersionAsync"/> detects any change (including purges) with one cheap call.
/// </summary>
/// <remarks>
/// <para>Keys are case-sensitive (ordinal). Implementations must be thread-safe and safe to share between several
/// apps and processes.</para>
/// <para>Write rules shared by every method that takes an <c>expectedVersion</c>: the write succeeds only if the flag
/// exists (<see cref="FlagNotFoundException"/> otherwise) and its current version equals the expected version
/// (<see cref="FlagVersionConflictException"/> otherwise); the version check happens before any state check. A
/// rejected write changes nothing and does not consume a version.</para>
/// <para>When a write is given a <see cref="FlagChangeContext"/>, the store appends a <see cref="FlagAuditEntry"/>
/// atomically with the change: either both are persisted or neither is. Without a context no audit entry is written.</para>
/// <para>Verify an implementation with the <c>Jorrk.Flagon.Storage.Conformance</c> package.</para>
/// </remarks>
public interface IFlagStore
{
    /// <summary>
    /// True for stores that cannot be written (auto-create and usage tracking then skip silently). Write methods on a
    /// read-only store throw <see cref="NotSupportedException"/>.
    /// </summary>
    bool IsReadOnly { get; }

    /// <summary>Creates or upgrades the store's schema. Idempotent and safe to call concurrently. A no-op for stores without a schema.</summary>
    Task ApplyMigrationsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// The highest change version assigned so far (0 for an empty, never-written store). Every create, update,
    /// archive, restore and purge moves it.
    /// </summary>
    ValueTask<long> GetChangeVersionAsync(CancellationToken cancellationToken = default);

    /// <summary>Loads every flag, including archived ones (<see cref="FlagRecord.Archived"/>). Purged flags are absent.</summary>
    Task<IReadOnlyList<FlagRecord>> LoadAllAsync(CancellationToken cancellationToken = default);

    /// <summary>Gets one flag (archived or not), or null if no flag has the key.</summary>
    Task<FlagRecord?> GetAsync(string key, CancellationToken cancellationToken = default);

    /// <summary>
    /// Inserts the flag if no flag with <paramref name="key"/> exists. Returns the created record, or null if the key was
    /// taken — including by an archived flag, which is never revived or changed by this call. Exactly one of several
    /// concurrent creates of the same key succeeds.
    /// </summary>
    /// <param name="key">The flag key.</param>
    /// <param name="document">The flag document.</param>
    /// <param name="change">Who/when, for the audit log (<see cref="FlagAuditAction.Created"/>); null writes no audit entry.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    Task<FlagRecord?> TryCreateAsync(string key, FlagDocument document, FlagChangeContext? change = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Replaces the flag's document if its version is still <paramref name="expectedVersion"/>; returns the new record.
    /// Updating an archived flag is allowed and leaves it archived.
    /// </summary>
    /// <param name="key">The flag key.</param>
    /// <param name="document">The new document.</param>
    /// <param name="expectedVersion">The version the caller last read.</param>
    /// <param name="change">Who/when, for the audit log (<see cref="FlagAuditAction.Updated"/>); null writes no audit entry.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <exception cref="FlagNotFoundException">No flag exists with the key.</exception>
    /// <exception cref="FlagVersionConflictException">The stored version differs from <paramref name="expectedVersion"/>.</exception>
    Task<FlagRecord> UpdateAsync(string key, FlagDocument document, long expectedVersion, FlagChangeContext? change = null, CancellationToken cancellationToken = default);

    /// <summary>Marks the flag archived (its document is kept); returns the new record.</summary>
    /// <param name="key">The flag key.</param>
    /// <param name="expectedVersion">The version the caller last read.</param>
    /// <param name="change">Who/when, for the audit log (<see cref="FlagAuditAction.Archived"/>); null writes no audit entry.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <exception cref="FlagNotFoundException">No flag exists with the key.</exception>
    /// <exception cref="FlagVersionConflictException">The stored version differs from <paramref name="expectedVersion"/>.</exception>
    /// <exception cref="FlagValidationException">The flag is already archived.</exception>
    Task<FlagRecord> ArchiveAsync(string key, long expectedVersion, FlagChangeContext? change = null, CancellationToken cancellationToken = default);

    /// <summary>Clears the flag's archived marker; returns the new record.</summary>
    /// <param name="key">The flag key.</param>
    /// <param name="expectedVersion">The version the caller last read.</param>
    /// <param name="change">Who/when, for the audit log (<see cref="FlagAuditAction.Restored"/>); null writes no audit entry.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <exception cref="FlagNotFoundException">No flag exists with the key.</exception>
    /// <exception cref="FlagVersionConflictException">The stored version differs from <paramref name="expectedVersion"/>.</exception>
    /// <exception cref="FlagValidationException">The flag is not archived.</exception>
    Task<FlagRecord> RestoreAsync(string key, long expectedVersion, FlagChangeContext? change = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Permanently deletes an archived flag and its usage records. Its audit history is kept. The purge consumes a
    /// version from the change sequence (returned) so other apps observe it; the key may then be created afresh.
    /// </summary>
    /// <param name="key">The flag key.</param>
    /// <param name="expectedVersion">The version the caller last read.</param>
    /// <param name="change">Who/when, for the audit log (<see cref="FlagAuditAction.Purged"/>); null writes no audit entry.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The change version assigned to the purge.</returns>
    /// <exception cref="FlagNotFoundException">No flag exists with the key.</exception>
    /// <exception cref="FlagVersionConflictException">The stored version differs from <paramref name="expectedVersion"/>.</exception>
    /// <exception cref="FlagValidationException">The flag is not archived.</exception>
    Task<long> PurgeAsync(string key, long expectedVersion, FlagChangeContext? change = null, CancellationToken cancellationToken = default);

    /// <summary>The audit history of <paramref name="key"/>, oldest first (empty if none). Survives purges.</summary>
    Task<IReadOnlyList<FlagAuditEntry>> GetAuditAsync(string key, CancellationToken cancellationToken = default);

    /// <summary>
    /// Upserts usage records keyed by (<see cref="FlagUsage.Key"/>, <see cref="FlagUsage.ApplicationName"/>). An
    /// existing record is replaced only by one with an equal or later <see cref="FlagUsage.LastEvaluatedAt"/>, so
    /// out-of-order batches never move it backwards. Keys need not exist as flags. Does not move the change version.
    /// </summary>
    Task UpsertUsageAsync(IReadOnlyCollection<FlagUsage> usage, CancellationToken cancellationToken = default);

    /// <summary>Usage of <paramref name="key"/>, one record per application.</summary>
    Task<IReadOnlyList<FlagUsage>> GetUsageAsync(string key, CancellationToken cancellationToken = default);

    /// <summary>Every usage record.</summary>
    Task<IReadOnlyList<FlagUsage>> LoadAllUsageAsync(CancellationToken cancellationToken = default);
}
