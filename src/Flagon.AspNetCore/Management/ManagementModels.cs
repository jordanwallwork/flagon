using System.Text.Json;
using System.Text.Json.Serialization;
using Flagon.Internal;
using Flagon.Internal.Targeting;
using Flagon.Storage;

namespace Flagon.AspNetCore.Management;

/// <summary>
/// A flag as returned by the management API. <c>type</c> is derived from the variants (null when they are empty or
/// mixed); <c>value</c> is the default variant's value (null when there is no default variant); <c>enabled</c> is the
/// boolean on/off toggle, null for non-boolean flags.
/// </summary>
internal sealed record FlagResponse(
    string Key,
    long Version,
    string? Type,
    bool? Enabled,
    string State,
    string? DefaultVariant,
    IReadOnlyDictionary<string, JsonElement> Variants,
    JsonElement? Value,
    bool Archived,
    string? Description,
    IReadOnlyList<string> Tags,
    string Source,
    DateTimeOffset? CreatedAt,
    string? CreatedBy,
    DateTimeOffset? UpdatedAt,
    string? UpdatedBy,
    JsonElement? Targeting,
    RolloutResponse? Rollout,
    FlagUsageResponse Usage)
{
    public static FlagResponse From(FlagRecord record, FlagUsageReport? usage)
    {
        var document = record.Document;
        var metadata = document.Metadata;
        var type = document.Type;
        JsonElement? value = document.DefaultVariant is { } defaultVariant && document.Variants.TryGetValue(defaultVariant, out var served)
            ? served
            : null;
        return new FlagResponse(
            record.Key,
            record.Version,
            type is { } t ? FlagVariants.Name(t) : null,
            type == FlagType.Boolean ? value?.ValueKind == JsonValueKind.True : null,
            document.State == FlagState.Enabled ? "enabled" : "disabled",
            document.DefaultVariant,
            document.Variants,
            value,
            record.Archived,
            metadata.Description,
            metadata.Tags,
            ManagementEndpoints.SourceName(metadata.Source),
            metadata.CreatedAt,
            metadata.CreatedBy,
            metadata.UpdatedAt,
            metadata.UpdatedBy,
            document.Targeting,
            RolloutShape.Percentage(document.Targeting) is { } percentage ? new RolloutResponse(percentage) : null,
            FlagUsageResponse.From(usage));
    }
}

/// <summary>
/// Present when the flag's targeting is exactly a simple percentage rollout (see <see cref="RolloutShape"/>), so tooling
/// can show and edit it as a percentage instead of a rule.
/// </summary>
internal sealed record RolloutResponse(int Percentage);

/// <summary>Where a flag is being evaluated: per application, the last evaluation and the code default supplied.</summary>
internal sealed record FlagUsageResponse(DateTimeOffset? LastEvaluatedAt, bool HasDefaultMismatch, IReadOnlyList<FlagApplicationUsageResponse> Applications)
{
    public static FlagUsageResponse From(FlagUsageReport? usage) => usage is null
        ? new FlagUsageResponse(null, false, [])
        : new FlagUsageResponse(
            usage.LastEvaluatedAt,
            usage.HasDefaultMismatch,
            usage.Applications.Select(a => new FlagApplicationUsageResponse(a.ApplicationName, a.LastEvaluatedAt, a.ObservedDefault)).ToArray());
}

internal sealed record FlagApplicationUsageResponse(string ApplicationName, DateTimeOffset LastEvaluatedAt, JsonElement? ObservedDefault);

/// <summary>An audit entry as returned by the management API. Documents are flagd JSON.</summary>
internal sealed record FlagAuditEntryResponse(
    long Id,
    string Key,
    string Action,
    long Version,
    DateTimeOffset Timestamp,
    string? Actor,
    string? ApplicationName,
    JsonElement? Before,
    JsonElement? After)
{
    public static FlagAuditEntryResponse From(FlagAuditEntry entry) => new(
        entry.Id,
        entry.Key,
        entry.Action.ToString().ToLowerInvariant(),
        entry.Version,
        entry.Timestamp,
        entry.Actor,
        entry.ApplicationName,
        ToJson(entry.Before),
        ToJson(entry.After));

    private static JsonElement? ToJson(FlagDocument? document) =>
        document is null ? null : JsonDocument.Parse(FlagdJson.Serialize(document)).RootElement.Clone();
}

/// <summary><c>variants</c> (absent or <c>null</c> for a boolean flag) is read as JSON so repeated names can be rejected.</summary>
internal sealed record CreateFlagBody(
    string? Key,
    bool Enabled = false,
    string? Description = null,
    IReadOnlyList<string>? Tags = null,
    JsonElement? Targeting = null,
    JsonElement Variants = default,
    string? DefaultVariant = null);

/// <summary><c>targeting</c> is required: a rule, or JSON <c>null</c> to remove it (an absent field is Undefined).</summary>
internal sealed record SetTargetingBody(JsonElement Targeting = default, long? Version = null);

internal sealed record UpdateMetadataBody(string? Description = null, IReadOnlyList<string>? Tags = null, long? Version = null);

internal sealed record SetEnabledBody(bool? Enabled = null, long? Version = null);

internal sealed record VersionBody(long? Version = null);

/// <summary>
/// <c>context</c> is the evaluation context (absent or <c>null</c> is empty); <c>document</c>, when present, is an unsaved
/// flagd flag document to evaluate instead of the stored flag.
/// </summary>
internal sealed record EvaluateBody(JsonElement Context = default, JsonElement Document = default);

/// <summary>
/// A preview evaluation result, using the same wire names as OFREP and the evaluation API. Untyped: <c>value</c> is the
/// served variant's JSON value of any type, or null when no variant was served (callers would get their own default),
/// so it never reports <c>TYPE_MISMATCH</c>.
/// </summary>
internal sealed record EvaluateResponse(
    JsonElement? Value,
    string? Variant,
    string Reason,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ErrorCode,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ErrorMessage)
{
    public static EvaluateResponse From(FlagEvaluationDetails<JsonElement> details) => new(
        details.Variant is null ? null : details.Value,
        details.Variant,
        details.Reason.ToWireName(),
        details.ErrorCode == Flagon.ErrorCode.None ? null : details.ErrorCode.ToWireName(),
        details.ErrorMessage);
}
