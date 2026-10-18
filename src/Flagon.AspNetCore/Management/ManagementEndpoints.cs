using System.Globalization;
using System.Text.Json;
using Flagon.AspNetCore.Internal;
using Flagon.Internal;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;

namespace Flagon.AspNetCore.Management;

/// <summary>Management API handlers: thin translations between HTTP and <see cref="IFlagonAdmin"/>.</summary>
internal static partial class ManagementEndpoints
{
    public static async Task<IResult> ListAsync(HttpContext http, IFlagonAdmin admin)
    {
        var query = http.Request.Query;
        var text = NonEmpty(query["q"]);
        var tag = NonEmpty(query["tag"]);
        var source = ParseSource(NonEmpty(query["source"]));
        var type = ParseType(NonEmpty(query["type"]));
        var enabled = ParseBool(query, "enabled");
        var archived = ParseBool(query, "archived");

        var records = await admin.ListAsync(http.RequestAborted).ConfigureAwait(false);
        var usage = (await admin.ListUsageAsync(http.RequestAborted).ConfigureAwait(false))
            .ToDictionary(u => u.Key, StringComparer.Ordinal);

        var flags = records
            .Select(r => FlagResponse.From(r, usage.GetValueOrDefault(r.Key)))
            .Where(f => text is null
                || f.Key.Contains(text, StringComparison.OrdinalIgnoreCase)
                || (f.Description?.Contains(text, StringComparison.OrdinalIgnoreCase) ?? false))
            .Where(f => tag is null || f.Tags.Contains(tag, StringComparer.Ordinal))
            .Where(f => source is null || f.Source == source)
            .Where(f => type is null || f.Type == type)
            .Where(f => enabled is null || f.Enabled == enabled)
            .Where(f => archived is null || f.Archived == archived)
            .OrderBy(f => f.Key, StringComparer.Ordinal)
            .ToArray();

        return Results.Ok(flags);
    }

    public static async Task<IResult> GetAsync(string key, HttpContext http, IFlagonAdmin admin)
    {
        var record = await admin.GetAsync(key, http.RequestAborted).ConfigureAwait(false)
            ?? throw new FlagNotFoundException(key);
        return await FlagAsync(http, admin, record).ConfigureAwait(false);
    }

    public static async Task<IResult> CreateAsync(HttpContext http, IFlagonAdmin admin)
    {
        var body = await ReadBodyAsync<CreateFlagBody>(http, required: true).ConfigureAwait(false);
        if (string.IsNullOrEmpty(body!.Key))
        {
            throw new FlagValidationException("'key' is required.");
        }

        var variants = body.Variants.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null
            ? null
            : FlagVariants.FromJson(body.Key, body.Variants);
        var record = await admin.CreateAsync(
            new CreateFlagRequest(body.Key, body.Enabled, body.Description, body.Tags, body.Targeting, variants, body.DefaultVariant),
            http.RequestAborted).ConfigureAwait(false);

        var location = $"{http.Request.PathBase}{http.Request.Path.Value?.TrimEnd('/')}/{Uri.EscapeDataString(record.Key)}";
        return Results.Created(location, await ResponseAsync(http, admin, record).ConfigureAwait(false));
    }

    public static async Task<IResult> UpdateMetadataAsync(string key, HttpContext http, IFlagonAdmin admin)
    {
        var body = await ReadBodyAsync<UpdateMetadataBody>(http, required: true).ConfigureAwait(false);
        var version = ExpectedVersion(http, body!.Version);
        var record = await admin.UpdateMetadataAsync(
            key, new UpdateFlagMetadataRequest(body.Description, body.Tags), version, http.RequestAborted).ConfigureAwait(false);
        return await FlagAsync(http, admin, record).ConfigureAwait(false);
    }

    public static async Task<IResult> SetEnabledAsync(string key, HttpContext http, IFlagonAdmin admin)
    {
        var body = await ReadBodyAsync<SetEnabledBody>(http, required: true).ConfigureAwait(false);
        var enabled = body!.Enabled ?? throw new FlagValidationException("'enabled' is required.");
        var version = ExpectedVersion(http, body.Version);
        var record = await admin.SetEnabledAsync(key, enabled, version, http.RequestAborted).ConfigureAwait(false);
        return await FlagAsync(http, admin, record).ConfigureAwait(false);
    }

    public static async Task<IResult> SetStateAsync(string key, HttpContext http, IFlagonAdmin admin)
    {
        var body = await ReadBodyAsync<SetStateBody>(http, required: true).ConfigureAwait(false);
        var state = ParseState(body!.State);
        var version = ExpectedVersion(http, body.Version);
        var record = await admin.SetStateAsync(key, state, version, http.RequestAborted).ConfigureAwait(false);
        return await FlagAsync(http, admin, record).ConfigureAwait(false);
    }

    private static FlagState ParseState(string? state) => state?.ToLowerInvariant() switch
    {
        null => throw new FlagValidationException("'state' is required: enabled or disabled."),
        "enabled" => FlagState.Enabled,
        "disabled" => FlagState.Disabled,
        _ => throw new FlagValidationException($"'{state}' is not a flag state; use enabled or disabled."),
    };

    public static async Task<IResult> SetTargetingAsync(string key, HttpContext http, IFlagonAdmin admin)
    {
        var body = await ReadBodyAsync<SetTargetingBody>(http, required: true).ConfigureAwait(false);
        if (body!.Targeting.ValueKind == JsonValueKind.Undefined)
        {
            throw new FlagValidationException("'targeting' is required: a flagd targeting rule, or null to remove it.");
        }

        var version = ExpectedVersion(http, body.Version);
        var record = await admin.SetTargetingAsync(key, body.Targeting, version, http.RequestAborted).ConfigureAwait(false);
        return await FlagAsync(http, admin, record).ConfigureAwait(false);
    }

    public static async Task<IResult> ClearTargetingAsync(string key, HttpContext http, IFlagonAdmin admin)
    {
        var version = await VersionFromRequestAsync(http).ConfigureAwait(false);
        var record = await admin.SetTargetingAsync(key, null, version, http.RequestAborted).ConfigureAwait(false);
        return await FlagAsync(http, admin, record).ConfigureAwait(false);
    }

    /// <summary>
    /// Previews the flag against a context without using it: the stored flag from the current snapshot, or an unsaved
    /// <c>document</c>. Evaluation is side-effect free (no usage, auto-create, metric or store write).
    /// </summary>
    public static async Task<IResult> EvaluateAsync(string key, HttpContext http, FlagonEvaluator evaluator)
    {
        var body = await ReadBodyAsync<EvaluateBody>(http, required: false).ConfigureAwait(false) ?? new EvaluateBody();
        var (context, contextError) = EvaluationContextJson.Parse(body.Context);
        if (contextError is not null)
        {
            throw new FlagValidationException(contextError);
        }

        FlagEvaluationDetails<JsonElement> details;
        if (body.Document.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
        {
            details = evaluator.PreviewDetails(key, context);
            if (details.ErrorCode == ErrorCode.FlagNotFound)
            {
                throw new FlagNotFoundException(key);
            }
        }
        else
        {
            if (body.Document.ValueKind != JsonValueKind.Object)
            {
                throw new FlagValidationException("'document' must be a flagd flag definition (a JSON object).");
            }

            details = evaluator.PreviewDraftDetails(key, FlagdJson.Deserialize(body.Document.GetRawText()), context);
        }

        return Results.Ok(EvaluateResponse.From(details));
    }

    public static async Task<IResult> ArchiveAsync(string key, HttpContext http, IFlagonAdmin admin)
    {
        var version = await VersionFromRequestAsync(http).ConfigureAwait(false);
        var record = await admin.ArchiveAsync(key, version, http.RequestAborted).ConfigureAwait(false);
        return await FlagAsync(http, admin, record).ConfigureAwait(false);
    }

    public static async Task<IResult> RestoreAsync(string key, HttpContext http, IFlagonAdmin admin)
    {
        var version = await VersionFromRequestAsync(http).ConfigureAwait(false);
        var record = await admin.RestoreAsync(key, version, http.RequestAborted).ConfigureAwait(false);
        return await FlagAsync(http, admin, record).ConfigureAwait(false);
    }

    public static async Task<IResult> PurgeAsync(string key, HttpContext http, IFlagonAdmin admin)
    {
        var version = await VersionFromRequestAsync(http).ConfigureAwait(false);
        await admin.PurgeAsync(key, version, http.RequestAborted).ConfigureAwait(false);
        return Results.NoContent();
    }

    public static async Task<IResult> GetAuditHistoryAsync(string key, HttpContext http, IFlagonAdmin admin)
    {
        var entries = await admin.GetAuditHistoryAsync(key, http.RequestAborted).ConfigureAwait(false);
        return Results.Ok(entries.Select(FlagAuditEntryResponse.From).ToArray());
    }

    public static string SourceName(FlagSource source) => source.ToString().ToLowerInvariant();

    public static string FormatETag(long version) => "\"" + version.ToString(CultureInfo.InvariantCulture) + "\"";

    private static async Task<IResult> FlagAsync(HttpContext http, IFlagonAdmin admin, FlagRecord record) =>
        Results.Ok(await ResponseAsync(http, admin, record).ConfigureAwait(false));

    private static async Task<FlagResponse> ResponseAsync(HttpContext http, IFlagonAdmin admin, FlagRecord record)
    {
        var usage = await admin.GetUsageAsync(record.Key, http.RequestAborted).ConfigureAwait(false);
        SetETag(http, record.Version);
        return FlagResponse.From(record, usage);
    }

    private static void SetETag(HttpContext http, long version) => http.Response.Headers.ETag = FormatETag(version);

    private static async Task<long> VersionFromRequestAsync(HttpContext http)
    {
        var body = await ReadBodyAsync<VersionBody>(http, required: false).ConfigureAwait(false);
        return ExpectedVersion(http, body?.Version);
    }

    /// <summary>The expected version: the <c>If-Match</c> header (an ETag such as <c>"5"</c>) wins over the body's <c>version</c>.</summary>
    private static long ExpectedVersion(HttpContext http, long? bodyVersion)
    {
        var ifMatch = http.Request.Headers.IfMatch.ToString().Trim();
        if (ifMatch.Length > 0)
        {
            if (ifMatch.StartsWith("W/", StringComparison.Ordinal))
            {
                ifMatch = ifMatch[2..];
            }

            if (long.TryParse(ifMatch.Trim('"'), NumberStyles.None, CultureInfo.InvariantCulture, out var headerVersion))
            {
                return headerVersion;
            }

            throw new FlagValidationException($"If-Match '{http.Request.Headers.IfMatch}' is not a flag version ETag such as \"5\".");
        }

        return bodyVersion
            ?? throw new FlagValidationException("The expected version is required: send the flag's ETag as If-Match, or a 'version' field in the body.");
    }

    private static async Task<T?> ReadBodyAsync<T>(HttpContext http, bool required)
        where T : class
    {
        if (!http.Request.HasJsonContentType())
        {
            if (!required && http.Request.ContentLength is null or 0)
            {
                return null;
            }

            throw new FlagValidationException("The request body must be JSON (Content-Type: application/json).");
        }

        var options = http.RequestServices.GetService<IOptions<JsonOptions>>()?.Value.SerializerOptions
            ?? new JsonSerializerOptions(JsonSerializerDefaults.Web);
        try
        {
            var body = await JsonSerializer.DeserializeAsync<T>(http.Request.Body, options, http.RequestAborted).ConfigureAwait(false);
            return body ?? (required ? throw new FlagValidationException("A request body is required.") : null);
        }
        catch (JsonException ex)
        {
            throw new FlagValidationException($"The request body is not valid: {ex.Message}");
        }
    }

    private static string? NonEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    private static string? ParseSource(string? value)
    {
        if (value is null)
        {
            return null;
        }

        return Enum.TryParse<FlagSource>(value, ignoreCase: true, out var source) && Enum.IsDefined(source)
            ? SourceName(source)
            : throw new FlagValidationException($"'{value}' is not a flag source (expected manual, auto or code).");
    }

    private static string? ParseType(string? value)
    {
        if (value is null)
        {
            return null;
        }

        return Enum.TryParse<FlagType>(value, ignoreCase: true, out var type) && Enum.IsDefined(type)
            ? FlagVariants.Name(type)
            : throw new FlagValidationException($"'{value}' is not a flag type (expected boolean, string, number or object).");
    }

    private static bool? ParseBool(IQueryCollection query, string name)
    {
        var value = NonEmpty(query[name]);
        if (value is null)
        {
            return null;
        }

        return bool.TryParse(value, out var parsed)
            ? parsed
            : throw new FlagValidationException($"Query parameter '{name}' must be true or false.");
    }
}
