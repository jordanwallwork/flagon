using System.Text.Json;
using Flagon.Storage;

namespace Flagon.AspNetCore.Management;

/// <summary>A flag as returned by the management API.</summary>
internal sealed record FlagResponse(
    string Key,
    long Version,
    bool Enabled,
    string State,
    string? DefaultVariant,
    bool Archived,
    string? Description,
    IReadOnlyList<string> Tags,
    string Source,
    DateTimeOffset? CreatedAt,
    string? CreatedBy,
    DateTimeOffset? UpdatedAt,
    string? UpdatedBy,
    FlagUsageResponse Usage)
{
    public static FlagResponse From(FlagRecord record, FlagUsageReport? usage)
    {
        var document = record.Document;
        var metadata = document.Metadata;
        var enabled = document.DefaultVariant is { } defaultVariant
            && document.Variants.TryGetValue(defaultVariant, out var value) && value.ValueKind == JsonValueKind.True;
        return new FlagResponse(
            record.Key,
            record.Version,
            enabled,
            document.State == FlagState.Enabled ? "enabled" : "disabled",
            document.DefaultVariant,
            record.Archived,
            metadata.Description,
            metadata.Tags,
            ManagementEndpoints.SourceName(metadata.Source),
            metadata.CreatedAt,
            metadata.CreatedBy,
            metadata.UpdatedAt,
            metadata.UpdatedBy,
            FlagUsageResponse.From(usage));
    }
}

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

internal sealed record CreateFlagBody(string? Key, bool Enabled = false, string? Description = null, IReadOnlyList<string>? Tags = null);

internal sealed record UpdateMetadataBody(string? Description = null, IReadOnlyList<string>? Tags = null, long? Version = null);

internal sealed record SetEnabledBody(bool? Enabled = null, long? Version = null);

internal sealed record VersionBody(long? Version = null);
