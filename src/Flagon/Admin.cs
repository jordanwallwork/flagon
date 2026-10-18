using System.Text.Json;
using Flagon.Storage;

namespace Flagon;

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
/// targeting engine before anything is written; <c>$ref</c> is not supported (inline the evaluator instead).
/// </param>
/// <param name="Variants">
/// Optional named variants (name to JSON value): at least one; names non-empty, at most 128 characters and unique
/// (ordinal); values all of one flag type (boolean, string, number or object; arrays and <c>null</c> are rejected).
/// The flag's type is derived from them and never changes. Null creates a boolean flag from <paramref name="Enabled"/>.
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
    /// <exception cref="FlagTargetingException">The request's targeting rule does not compile.</exception>
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
    /// targeting engine before anything is written; <c>$ref</c> is not supported (inline the evaluator instead).
    /// </summary>
    /// <exception cref="FlagTargetingException">The rule does not compile; the flag is unchanged.</exception>
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
    /// <paramref name="variant"/> is null (callers then get their own default). For a boolean flag, <c>on</c> or
    /// <c>off</c> is the same as <see cref="SetEnabledAsync"/>.
    /// </summary>
    /// <exception cref="FlagValidationException">The variant is not one of the flag's, or the flag is archived; the flag is unchanged.</exception>
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

    /// <summary>Restores an archived flag so it evaluates normally again.</summary>
    /// <exception cref="FlagNotFoundException">No flag exists with the key.</exception>
    /// <exception cref="FlagVersionConflictException">The flag changed since <paramref name="expectedVersion"/>.</exception>
    /// <exception cref="FlagValidationException">The flag is not archived.</exception>
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
}
