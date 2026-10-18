using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Flagon.Internal;
using Microsoft.AspNetCore.Http;
using Microsoft.Net.Http.Headers;

namespace Flagon.AspNetCore.Internal;

/// <summary>OFREP request handling: parses the evaluation context and maps Flagon evaluation details onto OFREP responses.</summary>
internal static class OfrepEndpoints
{
    public static async Task<IResult> EvaluateAsync(string key, HttpContext http, FlagonEvaluator flagon)
    {
        var (context, contextError) = await ReadContextAsync(http).ConfigureAwait(false);
        if (contextError is not null)
        {
            return Json(Failure(key, ErrorCode.InvalidContext.ToWireName(), contextError), StatusCodes.Status400BadRequest);
        }

        // OFREP has no caller default: "false" never reaches the client because every outcome that would return the
        // caller's default (missing, disabled, error) is reported without a value.
        var details = flagon.GetRemoteBooleanDetails(key, context);
        return details.ErrorCode switch
        {
            ErrorCode.None => Json(Success(details), StatusCodes.Status200OK),
            ErrorCode.FlagNotFound => Json(Failure(key, ErrorCode.FlagNotFound.ToWireName(), details.ErrorMessage), StatusCodes.Status404NotFound),
            _ => Json(Failure(key, ToOfrepErrorCode(details.ErrorCode), details.ErrorMessage), StatusCodes.Status400BadRequest),
        };
    }

    public static async Task<IResult> EvaluateAllAsync(HttpContext http, FlagonEvaluator flagon)
    {
        var (context, contextError) = await ReadContextAsync(http).ConfigureAwait(false);
        if (contextError is not null)
        {
            return Json(new JsonObject { ["errorCode"] = ErrorCode.InvalidContext.ToWireName(), ["errorDetails"] = contextError }, StatusCodes.Status400BadRequest);
        }

        var evaluation = flagon.GetAllRemoteBooleanDetails(context);

        // The ETag is the snapshot version: any flag change (in this app or, after a refresh, another app) moves it.
        // The context is not part of the ETag: an OFREP client caches the response for the context it sends, and a client
        // that changes its context must not send If-None-Match from the old one (context-aware ETags are out of scope).
        var etag = new EntityTagHeaderValue($"\"{evaluation.Version.ToString(CultureInfo.InvariantCulture)}\"");
        var headers = http.Response.GetTypedHeaders();
        headers.ETag = etag;

        // If-None-Match uses weak comparison (RFC 9110 13.1.2): only the opaque tags are compared.
        var ifNoneMatch = http.Request.GetTypedHeaders().IfNoneMatch;
        if (ifNoneMatch.Any(candidate => candidate.Equals(EntityTagHeaderValue.Any) || candidate.Tag == etag.Tag))
        {
            return Results.StatusCode(StatusCodes.Status304NotModified);
        }

        var flags = new JsonArray();
        foreach (var details in evaluation.Flags)
        {
            flags.Add(details.ErrorCode == ErrorCode.None
                ? Success(details)
                : Failure(details.FlagKey, ToOfrepErrorCode(details.ErrorCode), details.ErrorMessage));
        }

        return Json(new JsonObject { ["flags"] = flags }, StatusCodes.Status200OK);
    }

    /// <summary>
    /// Reads the OFREP request body <c>{ "context": { "targetingKey": "...", ...attributes } }</c>.
    /// An empty body is treated as an empty context; anything else malformed is an error message.
    /// </summary>
    private static async Task<(EvaluationContext Context, string? Error)> ReadContextAsync(HttpContext http)
    {
        JsonDocument document;
        try
        {
            using var reader = new StreamReader(http.Request.Body, Encoding.UTF8);
            var text = await reader.ReadToEndAsync(http.RequestAborted).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(text))
            {
                return (EvaluationContext.Empty, null);
            }

            document = JsonDocument.Parse(text);
        }
        catch (JsonException ex)
        {
            return (EvaluationContext.Empty, $"The request body is not valid JSON: {ex.Message}");
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return (EvaluationContext.Empty, "The request body must be a JSON object with a 'context' property.");
            }

            return root.TryGetProperty("context", out var contextElement)
                ? EvaluationContextJson.Parse(contextElement)
                : (EvaluationContext.Empty, null);
        }
    }

    private static JsonObject Success(FlagEvaluationDetails<bool> details)
    {
        var result = new JsonObject
        {
            ["key"] = details.FlagKey,
            ["reason"] = details.Reason.ToWireName(),
        };

        // No variant means the caller's default applies (disabled flags, and any future "use the code default" outcome):
        // OFREP expresses that by omitting the value: the spec's `codeDefaultFlag` success shape (protocol ADR 0006),
        // which tells the provider to use the code default.
        if (details.Variant is not null)
        {
            result["variant"] = details.Variant;
            result["value"] = details.Value;
        }

        return result;
    }

    private static JsonObject Failure(string key, string errorCode, string? errorDetails)
    {
        var result = new JsonObject { ["key"] = key, ["errorCode"] = errorCode };
        if (errorDetails is not null)
        {
            result["errorDetails"] = errorDetails;
        }

        return result;
    }

    /// <summary>
    /// Maps to OFREP's evaluation-failure error codes, which are a subset of the OpenFeature names
    /// (PARSE_ERROR, TARGETING_KEY_MISSING, INVALID_CONTEXT, GENERAL). Anything else (e.g. TYPE_MISMATCH) is GENERAL.
    /// </summary>
    private static string ToOfrepErrorCode(ErrorCode code) =>
        code is ErrorCode.ParseError or ErrorCode.TargetingKeyMissing or ErrorCode.InvalidContext
            ? code.ToWireName()
            : ErrorCode.General.ToWireName();

    private static IResult Json(JsonNode body, int statusCode) =>
        Results.Text(body.ToJsonString(), "application/json", Encoding.UTF8, statusCode);
}
