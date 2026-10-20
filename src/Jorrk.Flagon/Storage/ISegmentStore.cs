namespace Jorrk.Flagon.Storage;

/// <summary>
/// An optional store capability: segments, reusable targeting conditions that flags reference with
/// <c>{"$ref": "&lt;segment-key&gt;"}</c>. A store opts in by implementing this interface alongside <see cref="IFlagStore"/>;
/// for stores that do not, segments are unavailable (admin calls throw <see cref="NotSupportedException"/>) and a
/// <c>$ref</c> in a flag fails to compile.
/// </summary>
/// <remarks>
/// <para>Segments share the store's one change sequence with flags: every segment create, update, archive, restore and
/// purge takes its version from it and moves <see cref="IFlagStore.GetChangeVersionAsync"/> (and signals
/// <see cref="IFlagChangeNotifier"/> where implemented), so apps reload, and recompile the flags that reference a
/// segment, exactly as for a flag change.</para>
/// <para>Write rules mirror <see cref="IFlagStore"/>'s, with the segment exceptions: a write that takes an
/// <c>expectedVersion</c> succeeds only if the segment exists (<see cref="SegmentNotFoundException"/>) and is at that
/// version (<see cref="SegmentVersionConflictException"/>), checked before any state check (an archive state error is a
/// <see cref="FlagValidationException"/>). A rejected write changes nothing and consumes no version. With a
/// <see cref="FlagChangeContext"/> a <see cref="SegmentAuditEntry"/> is appended atomically with the change; audit
/// history survives purges. Segment keys are case-sensitive and independent of flag keys.</para>
/// <para>The store does not interpret rules or check references: whether a segment may be archived or purged while
/// flags reference it is decided by <see cref="IFlagonAdmin"/> before it writes.</para>
/// <para>Verify an implementation with the <c>Jorrk.Flagon.Storage.Conformance</c> package.</para>
/// </remarks>
public interface ISegmentStore
{
    /// <summary>
    /// Flags and segments from one consistent read: every write with a version up to the returned
    /// <see cref="FlagStoreContents.Version"/> is visible, none after it. Separate flag and segment reads could tear (a
    /// flag stops referencing a segment and the segment is purged between them), so the snapshot loads through this.
    /// </summary>
    Task<FlagStoreContents> LoadAllWithSegmentsAsync(CancellationToken cancellationToken = default);

    /// <summary>Loads every segment, including archived ones. Purged segments are absent.</summary>
    Task<IReadOnlyList<SegmentRecord>> LoadAllSegmentsAsync(CancellationToken cancellationToken = default);

    /// <summary>Gets one segment (archived or not), or null if no segment has the key.</summary>
    Task<SegmentRecord?> GetSegmentAsync(string key, CancellationToken cancellationToken = default);

    /// <summary>
    /// Inserts the segment if no segment with <paramref name="key"/> exists. Returns the created record, or null if the key
    /// was taken (including by an archived segment). Exactly one of several concurrent creates of the same key succeeds.
    /// </summary>
    /// <param name="key">The segment key.</param>
    /// <param name="document">The segment document.</param>
    /// <param name="change">Who/when, for the audit log (<see cref="FlagAuditAction.Created"/>); null writes no audit entry.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    Task<SegmentRecord?> TryCreateSegmentAsync(string key, SegmentDocument document, FlagChangeContext? change = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Replaces the segment's document if its version is still <paramref name="expectedVersion"/>; returns the new record.
    /// Updating an archived segment is allowed and leaves it archived.
    /// </summary>
    /// <param name="key">The segment key.</param>
    /// <param name="document">The new document.</param>
    /// <param name="expectedVersion">The version the caller last read.</param>
    /// <param name="change">Who/when, for the audit log (<see cref="FlagAuditAction.Updated"/>); null writes no audit entry.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <exception cref="SegmentNotFoundException">No segment exists with the key.</exception>
    /// <exception cref="SegmentVersionConflictException">The stored version differs from <paramref name="expectedVersion"/>.</exception>
    Task<SegmentRecord> UpdateSegmentAsync(string key, SegmentDocument document, long expectedVersion, FlagChangeContext? change = null, CancellationToken cancellationToken = default);

    /// <summary>Marks the segment archived (its document is kept); returns the new record.</summary>
    /// <param name="key">The segment key.</param>
    /// <param name="expectedVersion">The version the caller last read.</param>
    /// <param name="change">Who/when, for the audit log (<see cref="FlagAuditAction.Archived"/>); null writes no audit entry.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <exception cref="SegmentNotFoundException">No segment exists with the key.</exception>
    /// <exception cref="SegmentVersionConflictException">The stored version differs from <paramref name="expectedVersion"/>.</exception>
    /// <exception cref="FlagValidationException">The segment is already archived.</exception>
    Task<SegmentRecord> ArchiveSegmentAsync(string key, long expectedVersion, FlagChangeContext? change = null, CancellationToken cancellationToken = default);

    /// <summary>Clears the segment's archived marker; returns the new record.</summary>
    /// <param name="key">The segment key.</param>
    /// <param name="expectedVersion">The version the caller last read.</param>
    /// <param name="change">Who/when, for the audit log (<see cref="FlagAuditAction.Restored"/>); null writes no audit entry.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <exception cref="SegmentNotFoundException">No segment exists with the key.</exception>
    /// <exception cref="SegmentVersionConflictException">The stored version differs from <paramref name="expectedVersion"/>.</exception>
    /// <exception cref="FlagValidationException">The segment is not archived.</exception>
    Task<SegmentRecord> RestoreSegmentAsync(string key, long expectedVersion, FlagChangeContext? change = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Permanently deletes an archived segment. Its audit history is kept. The purge consumes a version from the change
    /// sequence (returned) so other apps observe it; the key may then be created afresh.
    /// </summary>
    /// <param name="key">The segment key.</param>
    /// <param name="expectedVersion">The version the caller last read.</param>
    /// <param name="change">Who/when, for the audit log (<see cref="FlagAuditAction.Purged"/>); null writes no audit entry.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The change version assigned to the purge.</returns>
    /// <exception cref="SegmentNotFoundException">No segment exists with the key.</exception>
    /// <exception cref="SegmentVersionConflictException">The stored version differs from <paramref name="expectedVersion"/>.</exception>
    /// <exception cref="FlagValidationException">The segment is not archived.</exception>
    Task<long> PurgeSegmentAsync(string key, long expectedVersion, FlagChangeContext? change = null, CancellationToken cancellationToken = default);

    /// <summary>The audit history of the segment <paramref name="key"/>, oldest first (empty if none). Survives purges.</summary>
    Task<IReadOnlyList<SegmentAuditEntry>> GetSegmentAuditAsync(string key, CancellationToken cancellationToken = default);
}
