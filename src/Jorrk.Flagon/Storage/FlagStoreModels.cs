using System.Text.Json;

namespace Jorrk.Flagon.Storage;

/// <summary>Who made a change and when; passed to <see cref="IFlagStore"/> writes so the store can audit them.</summary>
/// <param name="Timestamp">When the change was made (from the caller's <see cref="TimeProvider"/>).</param>
/// <param name="Actor">Who made the change (a user name, or null for system changes).</param>
/// <param name="ApplicationName">Which application made the change.</param>
public sealed record FlagChangeContext(DateTimeOffset Timestamp, string? Actor = null, string? ApplicationName = null);

/// <summary>The kind of change an audit entry records.</summary>
public enum FlagAuditAction
{
    /// <summary>The flag was created.</summary>
    Created,

    /// <summary>The flag's document was replaced.</summary>
    Updated,

    /// <summary>The flag was archived.</summary>
    Archived,

    /// <summary>The flag was restored from the archive.</summary>
    Restored,

    /// <summary>The flag was permanently deleted.</summary>
    Purged,
}

/// <summary>One append-only audit log entry, written atomically with the change it describes.</summary>
/// <param name="Id">Store-assigned identifier; increases with each entry.</param>
/// <param name="Key">The flag key.</param>
/// <param name="Action">What happened.</param>
/// <param name="Version">The change version the write was assigned.</param>
/// <param name="Timestamp">When it happened (<see cref="FlagChangeContext.Timestamp"/>).</param>
/// <param name="Actor">Who did it.</param>
/// <param name="ApplicationName">Which application did it.</param>
/// <param name="Before">The document before the change (null for <see cref="FlagAuditAction.Created"/>).</param>
/// <param name="After">The document after the change (null for <see cref="FlagAuditAction.Purged"/>).</param>
public sealed record FlagAuditEntry(
    long Id,
    string Key,
    FlagAuditAction Action,
    long Version,
    DateTimeOffset Timestamp,
    string? Actor,
    string? ApplicationName,
    FlagDocument? Before,
    FlagDocument? After);

/// <summary>When an application last evaluated a flag, and the code default it supplied.</summary>
/// <param name="Key">The flag key (need not exist as a flag).</param>
/// <param name="ApplicationName">The evaluating application.</param>
/// <param name="LastEvaluatedAt">When the application last evaluated the flag.</param>
/// <param name="ObservedDefault">The default value the application's code supplied, as canonical JSON (object keys sorted, one number format); null when unknown.</param>
public sealed record FlagUsage(string Key, string ApplicationName, DateTimeOffset LastEvaluatedAt, JsonElement? ObservedDefault = null);

/// <summary>One append-only segment audit log entry, written atomically with the change it describes.</summary>
/// <param name="Id">Store-assigned identifier; increases with each entry.</param>
/// <param name="Key">The segment key.</param>
/// <param name="Action">What happened.</param>
/// <param name="Version">The change version the write was assigned.</param>
/// <param name="Timestamp">When it happened (<see cref="FlagChangeContext.Timestamp"/>).</param>
/// <param name="Actor">Who did it.</param>
/// <param name="ApplicationName">Which application did it.</param>
/// <param name="Before">The document before the change (null for <see cref="FlagAuditAction.Created"/>).</param>
/// <param name="After">The document after the change (null for <see cref="FlagAuditAction.Purged"/>).</param>
public sealed record SegmentAuditEntry(
    long Id,
    string Key,
    FlagAuditAction Action,
    long Version,
    DateTimeOffset Timestamp,
    string? Actor,
    string? ApplicationName,
    SegmentDocument? Before,
    SegmentDocument? After);

/// <summary>Every flag and segment from one consistent read (see <see cref="ISegmentStore.LoadAllWithSegmentsAsync"/>).</summary>
/// <param name="Version">The change version the contents reflect: every write numbered up to it is included, none after.</param>
/// <param name="Flags">Every flag, archived ones included.</param>
/// <param name="Segments">Every segment, archived ones included.</param>
public sealed record FlagStoreContents(long Version, IReadOnlyList<FlagRecord> Flags, IReadOnlyList<SegmentRecord> Segments);
