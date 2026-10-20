using System.Text.Json;

namespace Jorrk.Flagon;

/// <summary>
/// A segment's definition: a reusable JsonLogic condition referenced from flag targeting with flagd's own syntax,
/// <c>{"$ref": "&lt;segment-key&gt;"}</c>, plus Flagon metadata. Like flagd's <c>$evaluators</c>, each reference is replaced
/// by <paramref name="Rule"/> before the flag's rule is compiled. Use <see cref="SegmentJson"/> to (de)serialise.
/// </summary>
/// <param name="Rule">
/// The condition: any targeting rule the registered targeting engine compiles on its own, conventionally a boolean one
/// (e.g. <c>{"in":[{"var":"targetingKey"},["k1","k2"]]}</c>). It may not contain <c>$ref</c>.
/// </param>
/// <param name="Metadata">
/// Flagon metadata: description, tags, source and the created/updated stamps, as for flags.
/// </param>
public sealed record SegmentDocument(JsonElement Rule, FlagMetadata Metadata);

/// <summary>A segment as held by a store: its key, document and the store-wide change version of its last write.</summary>
/// <param name="Key">The case-sensitive segment key (same rules as <see cref="FlagKey"/>, in a namespace of its own).</param>
/// <param name="Document">The segment's rule and metadata.</param>
/// <param name="Version">The change version assigned by the store's global sequence on the segment's last write. Used for optimistic concurrency.</param>
/// <param name="Archived">
/// True if the segment has been archived: it keeps resolving for the flags that reference it, but cannot be newly
/// referenced or edited until restored.
/// </param>
public sealed record SegmentRecord(string Key, SegmentDocument Document, long Version, bool Archived = false);

/// <summary>A request to create a segment.</summary>
/// <param name="Key">The segment key; must satisfy <see cref="FlagKey.Pattern"/>.</param>
/// <param name="Rule">
/// The segment's condition: a targeting rule (not absent, <c>null</c> or <c>{}</c>) that compiles on its own and contains
/// no <c>$ref</c>.
/// </param>
/// <param name="Description">Optional description.</param>
/// <param name="Tags">Optional tags (same rules as flag tags).</param>
public sealed record CreateSegmentRequest(string Key, JsonElement Rule, string? Description = null, IReadOnlyList<string>? Tags = null);

/// <summary>
/// A request to change a segment's descriptive metadata. Null fields are left unchanged; an empty description clears it
/// and an empty tag list removes all tags.
/// </summary>
/// <param name="Description">The new description, or null to keep the current one.</param>
/// <param name="Tags">The new tags, or null to keep the current ones.</param>
public sealed record UpdateSegmentMetadataRequest(string? Description = null, IReadOnlyList<string>? Tags = null);

/// <summary>A flag whose targeting references a segment.</summary>
/// <param name="FlagKey">The flag key.</param>
/// <param name="Archived">Whether the flag is archived (archived flags block a segment's purge, not its archive).</param>
public sealed record SegmentReference(string FlagKey, bool Archived);
