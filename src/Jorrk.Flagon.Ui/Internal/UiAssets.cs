using System.Collections.Frozen;
using System.Security.Cryptography;
using Jorrk.Flagon.AspNetCore.Internal;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Net.Http.Headers;

namespace Jorrk.Flagon.Ui.Internal;

/// <summary>Serves the console's files, which are embedded in this assembly under the <c>FlagonUi/</c> resource prefix.</summary>
internal static class UiAssets
{
    private const string ResourcePrefix = "FlagonUi/";
    private const string IndexFile = "index.html";

    // The page loads only its own scripts and styles and calls only its own API.
    private const string ContentSecurityPolicy =
        "default-src 'none'; script-src 'self'; style-src 'self'; img-src 'self' data:; connect-src 'self'; " +
        "base-uri 'none'; form-action 'self'; frame-ancestors 'none'";

    private static readonly Lazy<FrozenDictionary<string, Asset>> Assets = new(Load);

    /// <summary>Answers 404 for the whole console unless the management gate is open (authorization configured, or Development).</summary>
    public static async ValueTask<object?> GateAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next) =>
        ManagementGate.IsOpen(context.HttpContext) ? await next(context).ConfigureAwait(false) : Results.NotFound();

    public static Task<IResult> ServeAsync(HttpContext http, string? asset)
    {
        if (string.IsNullOrEmpty(asset))
        {
            // Serve the page at "{path}/" so its relative URLs (./app.js, ./_api/flags) resolve under the mount path.
            if (!http.Request.Path.Value!.EndsWith('/'))
            {
                return Task.FromResult(Results.Redirect($"{http.Request.PathBase}{http.Request.Path}/{http.Request.QueryString}"));
            }

            asset = IndexFile;
        }

        if (!Assets.Value.TryGetValue(asset, out var file))
        {
            return Task.FromResult(Results.NotFound());
        }

        var headers = http.Response.Headers;
        headers.ETag = file.ETag;
        headers.CacheControl = "no-cache";
        headers.XContentTypeOptions = "nosniff";
        if (asset == IndexFile)
        {
            headers.ContentSecurityPolicy = ContentSecurityPolicy;
            headers.XFrameOptions = "DENY";
            headers["Referrer-Policy"] = "no-referrer";
        }

        if (http.Request.Headers.IfNoneMatch.Any(tag => tag?.Contains(file.ETag, StringComparison.Ordinal) == true))
        {
            return Task.FromResult(Results.StatusCode(StatusCodes.Status304NotModified));
        }

        return Task.FromResult(Results.Bytes(file.Content, file.ContentType));
    }

    private static FrozenDictionary<string, Asset> Load()
    {
        var assembly = typeof(UiAssets).Assembly;
        var contentTypes = new FileExtensionContentTypeProvider();
        var assets = new Dictionary<string, Asset>(StringComparer.Ordinal);
        foreach (var resource in assembly.GetManifestResourceNames())
        {
            if (!resource.StartsWith(ResourcePrefix, StringComparison.Ordinal))
            {
                continue;
            }

            var name = resource[ResourcePrefix.Length..].Replace('\\', '/');
            using var stream = assembly.GetManifestResourceStream(resource)!;
            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            var content = buffer.ToArray();

            var contentType = contentTypes.TryGetContentType(name, out var type) ? type : "application/octet-stream";
            if (contentType.StartsWith("text/", StringComparison.Ordinal) || contentType == "application/javascript"
                || contentType == "text/javascript")
            {
                contentType += "; charset=utf-8";
            }

            var etag = "\"" + Convert.ToHexString(SHA256.HashData(content), 0, 8).ToLowerInvariant() + "\"";
            assets[name] = new Asset(content, contentType, etag);
        }

        return assets.ToFrozenDictionary(StringComparer.Ordinal);
    }

    private sealed record Asset(byte[] Content, string ContentType, string ETag);
}
