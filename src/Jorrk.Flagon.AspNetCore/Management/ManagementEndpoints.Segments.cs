using System.Text.Json;
using Jorrk.Flagon.AspNetCore.Internal;
using Jorrk.Flagon.Internal;
using Jorrk.Flagon.Internal.Targeting;
using Microsoft.AspNetCore.Http;

namespace Jorrk.Flagon.AspNetCore.Management;

/// <summary>Segment endpoints: thin translations between HTTP and the segment methods of <see cref="IFlagonAdmin"/>.</summary>
internal static partial class ManagementEndpoints
{
    /// <summary>Answers <c>501</c> for every segment route when the store does not support segments.</summary>
    public static async ValueTask<object?> SegmentsSupportedFilterAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var admin = context.HttpContext.RequestServices.GetService(typeof(IFlagonAdmin)) as IFlagonAdmin;
        if (admin is not { SupportsSegments: true })
        {
            return Results.Problem(
                detail: "The flag store does not support segments (it does not implement ISegmentStore).",
                statusCode: StatusCodes.Status501NotImplemented,
                title: "Segments not supported");
        }

        return await next(context).ConfigureAwait(false);
    }

    public static async Task<IResult> ListSegmentsAsync(HttpContext http, IFlagonAdmin admin)
    {
        var query = http.Request.Query;
        var text = NonEmpty(query["q"]);
        var tag = NonEmpty(query["tag"]);
        var archived = ParseBool(query, "archived");

        var segments = await admin.ListSegmentsAsync(http.RequestAborted).ConfigureAwait(false);
        var flags = await admin.ListAsync(http.RequestAborted).ConfigureAwait(false);
        var usedBy = flags
            .SelectMany(f => SegmentRefs.References(f.Document.Targeting))
            .GroupBy(k => k, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);

        var response = segments
            .Where(s => text is null
                || s.Key.Contains(text, StringComparison.OrdinalIgnoreCase)
                || (s.Document.Metadata.Description?.Contains(text, StringComparison.OrdinalIgnoreCase) ?? false))
            .Where(s => tag is null || s.Document.Metadata.Tags.Contains(tag, StringComparer.Ordinal))
            .Where(s => archived is null || s.Archived == archived)
            .OrderBy(s => s.Key, StringComparer.Ordinal)
            .Select(s => SegmentResponse.From(s, usedBy.GetValueOrDefault(s.Key)))
            .ToArray();

        return Results.Ok(response);
    }

    public static async Task<IResult> GetSegmentAsync(string key, HttpContext http, IFlagonAdmin admin)
    {
        var record = await admin.GetSegmentAsync(key, http.RequestAborted).ConfigureAwait(false)
            ?? throw new SegmentNotFoundException(key);
        return Results.Ok(await SegmentResponseAsync(http, admin, record).ConfigureAwait(false));
    }

    public static async Task<IResult> CreateSegmentAsync(HttpContext http, IFlagonAdmin admin)
    {
        var body = await ReadBodyAsync<CreateSegmentBody>(http, required: true).ConfigureAwait(false);
        if (string.IsNullOrEmpty(body!.Key))
        {
            throw new FlagValidationException("'key' is required.");
        }

        if (body.Rule.ValueKind == JsonValueKind.Undefined)
        {
            throw new FlagValidationException("'rule' is required: the segment's targeting condition.");
        }

        var record = await admin.CreateSegmentAsync(new CreateSegmentRequest(body.Key, body.Rule, body.Description, body.Tags), http.RequestAborted)
            .ConfigureAwait(false);
        var location = $"{http.Request.PathBase}{http.Request.Path.Value?.TrimEnd('/')}/{Uri.EscapeDataString(record.Key)}";
        return Results.Created(location, await SegmentResponseAsync(http, admin, record).ConfigureAwait(false));
    }

    public static async Task<IResult> UpdateSegmentMetadataAsync(string key, HttpContext http, IFlagonAdmin admin)
    {
        var body = await ReadBodyAsync<UpdateMetadataBody>(http, required: true).ConfigureAwait(false);
        var version = ExpectedVersion(http, body!.Version);
        var record = await admin.UpdateSegmentMetadataAsync(key, new UpdateSegmentMetadataRequest(body.Description, body.Tags), version, http.RequestAborted)
            .ConfigureAwait(false);
        return Results.Ok(await SegmentResponseAsync(http, admin, record).ConfigureAwait(false));
    }

    public static async Task<IResult> SetSegmentRuleAsync(string key, HttpContext http, IFlagonAdmin admin)
    {
        var body = await ReadBodyAsync<SetSegmentRuleBody>(http, required: true).ConfigureAwait(false);
        if (body!.Rule.ValueKind == JsonValueKind.Undefined)
        {
            throw new FlagValidationException("'rule' is required: the segment's targeting condition.");
        }

        var version = ExpectedVersion(http, body.Version);
        var record = await admin.SetSegmentRuleAsync(key, body.Rule, version, http.RequestAborted).ConfigureAwait(false);
        return Results.Ok(await SegmentResponseAsync(http, admin, record).ConfigureAwait(false));
    }

    public static async Task<IResult> ArchiveSegmentAsync(string key, HttpContext http, IFlagonAdmin admin)
    {
        var version = await VersionFromRequestAsync(http).ConfigureAwait(false);
        var record = await admin.ArchiveSegmentAsync(key, version, http.RequestAborted).ConfigureAwait(false);
        return Results.Ok(await SegmentResponseAsync(http, admin, record).ConfigureAwait(false));
    }

    public static async Task<IResult> RestoreSegmentAsync(string key, HttpContext http, IFlagonAdmin admin)
    {
        var version = await VersionFromRequestAsync(http).ConfigureAwait(false);
        var record = await admin.RestoreSegmentAsync(key, version, http.RequestAborted).ConfigureAwait(false);
        return Results.Ok(await SegmentResponseAsync(http, admin, record).ConfigureAwait(false));
    }

    public static async Task<IResult> PurgeSegmentAsync(string key, HttpContext http, IFlagonAdmin admin)
    {
        var version = await VersionFromRequestAsync(http).ConfigureAwait(false);
        await admin.PurgeSegmentAsync(key, version, http.RequestAborted).ConfigureAwait(false);
        return Results.NoContent();
    }

    public static async Task<IResult> GetSegmentAuditHistoryAsync(string key, HttpContext http, IFlagonAdmin admin)
    {
        var entries = await admin.GetSegmentAuditHistoryAsync(key, http.RequestAborted).ConfigureAwait(false);
        return Results.Ok(entries.Select(SegmentAuditEntryResponse.From).ToArray());
    }

    /// <summary>
    /// Tests a segment's rule against a context: the stored rule, or an unsaved <c>rule</c>. No side effects. A stored
    /// segment must exist; a draft rule may be tested for a key that does not exist yet (as when creating one).
    /// </summary>
    public static async Task<IResult> EvaluateSegmentAsync(string key, HttpContext http, IFlagonAdmin admin, FlagonEvaluator evaluator)
    {
        var body = await ReadBodyAsync<EvaluateSegmentBody>(http, required: false).ConfigureAwait(false) ?? new EvaluateSegmentBody();
        var (context, contextError) = EvaluationContextJson.Parse(body.Context);
        if (contextError is not null)
        {
            throw new FlagValidationException(contextError);
        }

        var rule = body.Rule.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null
            ? (await admin.GetSegmentAsync(key, http.RequestAborted).ConfigureAwait(false) ?? throw new SegmentNotFoundException(key)).Document.Rule
            : body.Rule;
        var preview = evaluator.PreviewSegment(key, rule, context);
        return Results.Ok(new EvaluateSegmentResponse(preview.Matches, preview.Result, preview.Error));
    }

    private static async Task<SegmentResponse> SegmentResponseAsync(HttpContext http, IFlagonAdmin admin, SegmentRecord record)
    {
        var usage = await admin.GetSegmentUsageAsync(record.Key, http.RequestAborted).ConfigureAwait(false);
        SetETag(http, record.Version);
        return SegmentResponse.From(record, usage.Select(u => new SegmentUserResponse(u.FlagKey, u.Archived)).ToArray());
    }
}
