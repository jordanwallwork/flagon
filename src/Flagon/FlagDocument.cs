using System.Collections.Frozen;
using System.Text.Json;

namespace Flagon;

/// <summary>The flagd <c>state</c> of a flag.</summary>
public enum FlagState
{
    /// <summary>The flag is evaluated normally (flagd <c>ENABLED</c>).</summary>
    Enabled,

    /// <summary>
    /// The flag is switched off in the management system (flagd <c>DISABLED</c>): evaluation returns the
    /// caller's default value with reason <see cref="EvaluationReason.Disabled"/>.
    /// </summary>
    Disabled,
}

/// <summary>How a flag came to exist.</summary>
public enum FlagSource
{
    /// <summary>Created by an admin (UI, management API or <see cref="IFlagonAdmin"/>).</summary>
    Manual,

    /// <summary>Created automatically the first time code evaluated an unknown key.</summary>
    Auto,

    /// <summary>Declared in code with <c>Define(...)</c>.</summary>
    Code,
}

/// <summary>Flagon's own metadata, stored inside the flagd document's <c>metadata</c> object.</summary>
/// <param name="Description">Human-readable description.</param>
/// <param name="Tags">Free-form tags. Tags may not contain commas or start or end with whitespace (they are stored comma-separated to stay within flagd's metadata schema).</param>
/// <param name="Source">How the flag came to exist.</param>
/// <param name="CreatedAt">When the flag was created.</param>
/// <param name="CreatedBy">Who (or which app) created the flag.</param>
/// <param name="UpdatedAt">When the flag was last changed.</param>
/// <param name="UpdatedBy">Who (or which app) last changed the flag.</param>
public sealed record FlagMetadata(
    string? Description = null,
    IReadOnlyList<string>? Tags = null,
    FlagSource Source = FlagSource.Manual,
    DateTimeOffset? CreatedAt = null,
    string? CreatedBy = null,
    DateTimeOffset? UpdatedAt = null,
    string? UpdatedBy = null)
{
    /// <summary>Free-form tags (never null).</summary>
    public IReadOnlyList<string> Tags { get; init; } = Tags ?? [];
}

/// <summary>
/// A flag definition in the flagd flag-definition schema: <c>state</c>, <c>variants</c>, <c>defaultVariant</c>,
/// optional <c>targeting</c> (JsonLogic) and <c>metadata</c>. Use <see cref="FlagdJson"/> to (de)serialise.
/// </summary>
/// <param name="State">flagd state.</param>
/// <param name="Variants">Variant name to value. Boolean flags use <c>{ "on": true, "off": false }</c>.</param>
/// <param name="DefaultVariant">
/// The variant served when no targeting rule applies. Toggling a boolean flag changes this. Null (as flagd allows) means
/// "no default variant": when no rule applies, evaluation returns the caller's default with reason
/// <see cref="EvaluationReason.Default"/>.
/// </param>
/// <param name="Metadata">Flagon metadata.</param>
/// <param name="Targeting">
/// Optional flagd targeting rule (JsonLogic plus flagd's operators), compiled once per snapshot load. An empty object
/// (<c>{}</c>) is no targeting, as in flagd.
/// </param>
public sealed record FlagDocument(
    FlagState State,
    IReadOnlyDictionary<string, JsonElement> Variants,
    string? DefaultVariant,
    FlagMetadata Metadata,
    JsonElement? Targeting = null)
{
    /// <summary>Variant name a boolean flag uses for <c>true</c>.</summary>
    public const string OnVariant = "on";

    /// <summary>Variant name a boolean flag uses for <c>false</c>.</summary>
    public const string OffVariant = "off";

    // Shared by every boolean flag, so genuinely immutable: a cast to IDictionary cannot change other flags' variants.
    private static readonly IReadOnlyDictionary<string, JsonElement> BooleanVariants = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
    {
        [OnVariant] = JsonSerializer.SerializeToElement(true),
        [OffVariant] = JsonSerializer.SerializeToElement(false),
    }.ToFrozenDictionary(StringComparer.Ordinal);

    /// <summary>Creates an <see cref="FlagState.Enabled"/> boolean flag serving <paramref name="enabled"/>.</summary>
    public static FlagDocument Boolean(bool enabled, FlagMetadata? metadata = null) =>
        new(FlagState.Enabled, BooleanVariants, enabled ? OnVariant : OffVariant, metadata ?? new FlagMetadata());
}

/// <summary>A flag as held by a store: its key, document and the store-wide change version of its last write.</summary>
/// <param name="Key">The case-sensitive flag key.</param>
/// <param name="Document">The flagd document.</param>
/// <param name="Version">The change version assigned by the store's global sequence on the flag's last write. Used for optimistic concurrency.</param>
/// <param name="Archived">True if the flag has been archived: it is kept (and blocks re-creation of its key) but should evaluate to the caller's default.</param>
public sealed record FlagRecord(string Key, FlagDocument Document, long Version, bool Archived = false);
