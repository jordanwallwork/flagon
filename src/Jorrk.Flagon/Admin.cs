using System.Text.Json;
using Jorrk.Flagon.Storage;

namespace Jorrk.Flagon;

/// <summary>
/// A request to create a flag: a boolean flag from <paramref name="Enabled"/>, or, when <paramref name="Variants"/> is
/// given, a flag of the variants' type (string, number or object) serving <paramref name="DefaultVariant"/>.
/// </summary>
/// <param name="Key">The flag key; must satisfy <see cref="FlagKey.Pattern"/>.</param>
/// <param name="Enabled">
/// Initial value served by a boolean flag (<c>{ "on": true, "off": false }</c>). Must be false when
/// <paramref name="Variants"/> is given.
/// </param>
/// <param name="Description">Optional description.</param>
/// <param name="Tags">Optional tags (no commas).</param>
/// <param name="Targeting">
/// Optional flagd targeting rule (JsonLogic plus flagd's operators). A rule that matches serves the variant it names
/// (<c>"on"</c> or <c>"off"</c> for a boolean flag); otherwise the flag serves <paramref name="Enabled"/> (or
/// <paramref name="DefaultVariant"/>). Validated with Flagon's
/// targeting engine before anything is written. It may reference segments with <c>{"$ref": "&lt;segment-key&gt;"}</c>
/// (each must exist and not be archived); they are inlined when the rule is compiled.
/// </param>
/// <param name="Variants">
/// Optional named variants (name to JSON value): at least one; names non-empty, at most 128 characters and unique
/// (ordinal); values all of one flag type (boolean, string, number or object; arrays and <c>null</c> are rejected).
/// The flag's type is derived from them and never changes. Boolean variants must be exactly
/// <c>{ "on": true, "off": false }</c>, with <paramref name="DefaultVariant"/> <c>on</c> or <c>off</c>. Null creates a
/// boolean flag from <paramref name="Enabled"/>.
/// </param>
/// <param name="DefaultVariant">
/// The variant served when no targeting rule applies: one of <paramref name="Variants"/>, or null for no default variant
/// (callers then get their own default). Only used with <paramref name="Variants"/>.
/// </param>
public sealed record CreateFlagRequest(
    string Key,
    bool Enabled = false,
    string? Description = null,
    IReadOnlyList<string>? Tags = null,
    JsonElement? Targeting = null,
    IReadOnlyDictionary<string, JsonElement>? Variants = null,
    string? DefaultVariant = null);

/// <summary>
/// A request to change a flag's descriptive metadata. Null fields are left unchanged; an empty description clears it
/// and an empty tag list removes all tags.
/// </summary>
/// <param name="Description">The new description, or null to keep the current one.</param>
/// <param name="Tags">The new tags (no commas), or null to keep the current ones.</param>
public sealed record UpdateFlagMetadataRequest(string? Description = null, IReadOnlyList<string>? Tags = null);

/// <summary>
/// In-process flag management. Reads go to the store (not the evaluation snapshot); writes are applied to the store
/// and reflected in this app's <see cref="IFlagon"/> immediately.
/// Updates take the version the caller last saw and throw <see cref="FlagVersionConflictException"/> if it is stale.
/// </summary>
public interface IFlagonAdmin
{
    /// <summary>Lists all flags.</summary>
    Task<IReadOnlyList<FlagRecord>> ListAsync(CancellationToken cancellationToken = default);

    /// <summary>Gets a flag, or null if none exists with <paramref name="key"/>.</summary>
    Task<FlagRecord?> GetAsync(string key, CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates a flag with source <see cref="FlagSource.Manual"/>: a boolean flag, or one with the request's variants
    /// (see <see cref="CreateFlagRequest"/>).
    /// </summary>
    /// <exception cref="FlagValidationException">The request is invalid.</exception>
    /// <exception cref="FlagTargetingException">The request's targeting rule does not compile, or references a segment that does not exist.</exception>
    /// <exception cref="SegmentArchivedException">The request's targeting rule references an archived segment.</exception>
    /// <exception cref="FlagAlreadyExistsException">A flag with the key already exists.</exception>
    Task<FlagRecord> CreateAsync(CreateFlagRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Toggles a boolean flag on or off (switches its default variant). For any type's kill switch, use
    /// <see cref="SetStateAsync"/>.
    /// </summary>
    /// <exception cref="FlagValidationException">The flag is not a boolean flag; the flag is unchanged.</exception>
    /// <exception cref="FlagNotFoundException">No flag exists with the key.</exception>
    /// <exception cref="FlagVersionConflictException">The flag changed since <paramref name="expectedVersion"/>.</exception>
    Task<FlagRecord> SetEnabledAsync(string key, bool enabled, long expectedVersion, CancellationToken cancellationToken = default);

    /// <summary>
    /// Switches a flag of any type between <see cref="FlagState.Enabled"/> and <see cref="FlagState.Disabled"/>. A
    /// disabled flag serves each caller's own code default with reason <see cref="EvaluationReason.Disabled"/>; its
    /// variants, default variant and targeting are kept for when it is enabled again.
    /// </summary>
    /// <exception cref="FlagValidationException">The flag is archived (restore it first); the flag is unchanged.</exception>
    /// <exception cref="FlagNotFoundException">No flag exists with the key.</exception>
    /// <exception cref="FlagVersionConflictException">The flag changed since <paramref name="expectedVersion"/>.</exception>
    Task<FlagRecord> SetStateAsync(string key, FlagState state, long expectedVersion, CancellationToken cancellationToken = default);

    /// <summary>
    /// Replaces a flag's flagd targeting rule, or removes it when <paramref name="targeting"/> is null (or JSON
    /// <c>null</c>). Its state, variants, default variant and metadata are untouched. The rule is validated with Flagon's
    /// targeting engine before anything is written. It may reference segments with <c>{"$ref": "&lt;segment-key&gt;"}</c>.
    /// </summary>
    /// <exception cref="FlagTargetingException">
    /// The rule does not compile, or references a segment that does not exist (<see cref="FlagTargetingException.Operator"/>
    /// <c>$ref</c>); the flag is unchanged.
    /// </exception>
    /// <exception cref="SegmentArchivedException">The rule references an archived segment; the flag is unchanged.</exception>
    /// <exception cref="FlagValidationException">The flag is archived (restore it first); the flag is unchanged.</exception>
    /// <exception cref="FlagNotFoundException">No flag exists with the key.</exception>
    /// <exception cref="FlagVersionConflictException">The flag changed since <paramref name="expectedVersion"/>.</exception>
    Task<FlagRecord> SetTargetingAsync(string key, JsonElement? targeting, long expectedVersion, CancellationToken cancellationToken = default);

    /// <summary>Changes a flag's description and/or tags; its state and variants are untouched.</summary>
    /// <exception cref="FlagValidationException">The request is invalid.</exception>
    /// <exception cref="FlagNotFoundException">No flag exists with the key.</exception>
    /// <exception cref="FlagVersionConflictException">The flag changed since <paramref name="expectedVersion"/>.</exception>
    Task<FlagRecord> UpdateMetadataAsync(string key, UpdateFlagMetadataRequest request, long expectedVersion, CancellationToken cancellationToken = default);

    /// <summary>
    /// Replaces the variants and default variant of a string, number or object flag in one write. Boolean flags keep
    /// their fixed <c>on</c>/<c>off</c> variants (use <see cref="SetEnabledAsync"/>). The variants follow the create-time
    /// rules (see <see cref="CreateFlagRequest.Variants"/>) and must keep the flag's type; the current default variant
    /// can only be removed by choosing another (or none) in the same edit, and a bucket of a canonical percentage split
    /// (<c>{"fractional": [[v1, w1], …]}</c>, see the management API's <c>rollout</c>) can't be removed until the split
    /// changes. Targeting and metadata are untouched, and custom targeting rules are not checked for the names removed.
    /// </summary>
    /// <param name="key">The flag key.</param>
    /// <param name="variants">The complete new set of variants (name to JSON value).</param>
    /// <param name="defaultVariant">The variant served when no targeting rule applies, or null for none.</param>
    /// <param name="expectedVersion">The version the caller last saw.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <exception cref="FlagValidationException">
    /// The variants are invalid, change the flag's type, or remove its default variant or a bucket of its percentage
    /// split; the flag is boolean; or it is archived. The flag is unchanged.
    /// </exception>
    /// <exception cref="FlagNotFoundException">No flag exists with the key.</exception>
    /// <exception cref="FlagVersionConflictException">The flag changed since <paramref name="expectedVersion"/>.</exception>
    Task<FlagRecord> SetVariantsAsync(
        string key, IReadOnlyDictionary<string, JsonElement> variants, string? defaultVariant, long expectedVersion, CancellationToken cancellationToken = default);

    /// <summary>
    /// Changes which variant a flag of any type serves when no targeting rule applies, or sets none when
    /// <paramref name="variant"/> is null (callers then get their own default). A boolean flag takes only <c>on</c> or
    /// <c>off</c> (not null), so this is the same as <see cref="SetEnabledAsync"/>.
    /// </summary>
    /// <exception cref="FlagValidationException">
    /// The variant is not one of the flag's, the flag is boolean and the variant is not <c>on</c> or <c>off</c>, or the
    /// flag is archived; the flag is unchanged.
    /// </exception>
    /// <exception cref="FlagNotFoundException">No flag exists with the key.</exception>
    /// <exception cref="FlagVersionConflictException">The flag changed since <paramref name="expectedVersion"/>.</exception>
    Task<FlagRecord> SetDefaultVariantAsync(string key, string? variant, long expectedVersion, CancellationToken cancellationToken = default);

    /// <summary>
    /// The audit history of <paramref name="key"/>, newest first (empty if none). Entries survive purges.
    /// Every write made through this service records its actor (from <see cref="IFlagonActorAccessor"/>),
    /// application (<see cref="FlagonOptions.ApplicationName"/>), timestamp, and before/after documents.
    /// </summary>
    Task<IReadOnlyList<FlagAuditEntry>> GetAuditHistoryAsync(string key, CancellationToken cancellationToken = default);

    /// <summary>
    /// Archives a flag: its document is kept, but evaluation returns the caller's default with reason
    /// <see cref="EvaluationReason.Archived"/> and the key is never re-created automatically.
    /// </summary>
    /// <exception cref="FlagNotFoundException">No flag exists with the key.</exception>
    /// <exception cref="FlagVersionConflictException">The flag changed since <paramref name="expectedVersion"/>.</exception>
    /// <exception cref="FlagValidationException">The flag is already archived.</exception>
    Task<FlagRecord> ArchiveAsync(string key, long expectedVersion, CancellationToken cancellationToken = default);

    /// <summary>
    /// Restores an archived flag so it evaluates normally again. A flag whose targeting references segments can only be
    /// restored while they all exist and are not archived (restore the segments first).
    /// </summary>
    /// <exception cref="FlagNotFoundException">No flag exists with the key.</exception>
    /// <exception cref="FlagVersionConflictException">The flag changed since <paramref name="expectedVersion"/>.</exception>
    /// <exception cref="FlagValidationException">The flag is not archived.</exception>
    /// <exception cref="SegmentArchivedException">The flag's targeting references an archived segment.</exception>
    /// <exception cref="FlagTargetingException">
    /// The flag's targeting references a segment that does not exist, or no longer compiles with its segments inlined.
    /// </exception>
    Task<FlagRecord> RestoreAsync(string key, long expectedVersion, CancellationToken cancellationToken = default);

    /// <summary>
    /// Permanently deletes an archived flag (its audit history is kept). The key then evaluates as not found and may be created again.
    /// </summary>
    /// <exception cref="FlagNotFoundException">No flag exists with the key.</exception>
    /// <exception cref="FlagVersionConflictException">The flag changed since <paramref name="expectedVersion"/>.</exception>
    /// <exception cref="FlagValidationException">The flag is not archived (only archived flags can be purged).</exception>
    Task PurgeAsync(string key, long expectedVersion, CancellationToken cancellationToken = default);

    /// <summary>
    /// Usage of <paramref name="key"/>: when each application last evaluated it, the code default each supplied, and
    /// whether those defaults disagree (<see cref="FlagUsageReport.HasDefaultMismatch"/>). Empty if nothing has been recorded.
    /// Usage is batched in each app and written every <see cref="FlagonOptions.UsageFlushInterval"/> (and on graceful
    /// shutdown), so it lags evaluation by up to one interval.
    /// </summary>
    Task<FlagUsageReport> GetUsageAsync(string key, CancellationToken cancellationToken = default);

    /// <summary>Usage of every key that has been evaluated (one report per key, ordered by key), for flag-list views.</summary>
    Task<IReadOnlyList<FlagUsageReport>> ListUsageAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Whether the store supports segments (implements <see cref="ISegmentStore"/>). When it does not, every segment
    /// method throws <see cref="NotSupportedException"/>.
    /// </summary>
    bool SupportsSegments { get; }

    /// <summary>Lists all segments, archived ones included.</summary>
    /// <exception cref="NotSupportedException">The store does not support segments.</exception>
    Task<IReadOnlyList<SegmentRecord>> ListSegmentsAsync(CancellationToken cancellationToken = default);

    /// <summary>Gets a segment, or null if none exists with <paramref name="key"/>.</summary>
    /// <exception cref="NotSupportedException">The store does not support segments.</exception>
    Task<SegmentRecord?> GetSegmentAsync(string key, CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates a segment with source <see cref="FlagSource.Manual"/>. Its rule must be a rule (not absent, <c>null</c> or
    /// <c>{}</c>), contain no <c>$ref</c> and compile on its own. It can be referenced at once.
    /// </summary>
    /// <exception cref="FlagValidationException">The key or a tag is invalid.</exception>
    /// <exception cref="SegmentRuleException">The rule is rejected.</exception>
    /// <exception cref="SegmentAlreadyExistsException">A segment with the key already exists.</exception>
    /// <exception cref="NotSupportedException">The store does not support segments.</exception>
    Task<SegmentRecord> CreateSegmentAsync(CreateSegmentRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Replaces a segment's rule. The rule follows the create-time rules, and every non-archived flag that references the
    /// segment must still compile with it inlined, so an edit can never break a flag. Flags referencing it serve the new
    /// audience from the next refresh (this app's at once).
    /// </summary>
    /// <exception cref="SegmentRuleException">
    /// The rule is rejected, or a flag referencing the segment would no longer compile (the message names it); the
    /// segment is unchanged.
    /// </exception>
    /// <exception cref="FlagValidationException">The segment is archived (restore it first).</exception>
    /// <exception cref="SegmentNotFoundException">No segment exists with the key.</exception>
    /// <exception cref="SegmentVersionConflictException">The segment changed since <paramref name="expectedVersion"/>.</exception>
    /// <exception cref="NotSupportedException">The store does not support segments.</exception>
    Task<SegmentRecord> SetSegmentRuleAsync(string key, JsonElement rule, long expectedVersion, CancellationToken cancellationToken = default);

    /// <summary>Changes a segment's description and/or tags; its rule is untouched.</summary>
    /// <exception cref="FlagValidationException">A tag is invalid.</exception>
    /// <exception cref="SegmentNotFoundException">No segment exists with the key.</exception>
    /// <exception cref="SegmentVersionConflictException">The segment changed since <paramref name="expectedVersion"/>.</exception>
    /// <exception cref="NotSupportedException">The store does not support segments.</exception>
    Task<SegmentRecord> UpdateSegmentMetadataAsync(string key, UpdateSegmentMetadataRequest request, long expectedVersion, CancellationToken cancellationToken = default);

    /// <summary>
    /// Archives a segment. It keeps resolving for the (archived) flags that reference it, so evaluation never changes, but
    /// it can no longer be newly referenced or edited. Refused while any non-archived flag references it.
    /// </summary>
    /// <exception cref="SegmentInUseException">A non-archived flag references the segment.</exception>
    /// <exception cref="FlagValidationException">The segment is already archived.</exception>
    /// <exception cref="SegmentNotFoundException">No segment exists with the key.</exception>
    /// <exception cref="SegmentVersionConflictException">The segment changed since <paramref name="expectedVersion"/>.</exception>
    /// <exception cref="NotSupportedException">The store does not support segments.</exception>
    Task<SegmentRecord> ArchiveSegmentAsync(string key, long expectedVersion, CancellationToken cancellationToken = default);

    /// <summary>Restores an archived segment so it can be referenced and edited again.</summary>
    /// <exception cref="FlagValidationException">The segment is not archived.</exception>
    /// <exception cref="SegmentNotFoundException">No segment exists with the key.</exception>
    /// <exception cref="SegmentVersionConflictException">The segment changed since <paramref name="expectedVersion"/>.</exception>
    /// <exception cref="NotSupportedException">The store does not support segments.</exception>
    Task<SegmentRecord> RestoreSegmentAsync(string key, long expectedVersion, CancellationToken cancellationToken = default);

    /// <summary>
    /// Permanently deletes an archived segment (its audit history is kept). Refused while any flag, archived or not,
    /// references it, since an archived flag can be restored.
    /// </summary>
    /// <remarks>
    /// The check runs before the write and is not atomic with concurrent flag writes: a flag that starts referencing the
    /// segment meanwhile serves PARSE_ERROR (logged once per snapshot) until it is edited.
    /// </remarks>
    /// <exception cref="SegmentInUseException">A flag references the segment.</exception>
    /// <exception cref="FlagValidationException">The segment is not archived.</exception>
    /// <exception cref="SegmentNotFoundException">No segment exists with the key.</exception>
    /// <exception cref="SegmentVersionConflictException">The segment changed since <paramref name="expectedVersion"/>.</exception>
    /// <exception cref="NotSupportedException">The store does not support segments.</exception>
    Task PurgeSegmentAsync(string key, long expectedVersion, CancellationToken cancellationToken = default);

    /// <summary>The audit history of the segment <paramref name="key"/>, newest first (empty if none). Survives purges.</summary>
    /// <exception cref="NotSupportedException">The store does not support segments.</exception>
    Task<IReadOnlyList<SegmentAuditEntry>> GetSegmentAuditHistoryAsync(string key, CancellationToken cancellationToken = default);

    /// <summary>The flags whose targeting references the segment <paramref name="key"/>, ordered by flag key, read from the store.</summary>
    /// <exception cref="NotSupportedException">The store does not support segments.</exception>
    Task<IReadOnlyList<SegmentReference>> GetSegmentUsageAsync(string key, CancellationToken cancellationToken = default);
}
