using System.Text.Json;

namespace FSTService.Api;

/// <summary>
/// Shared helper for the ETag/cache check pattern used across API endpoints.
/// Checks a cached entry against the request's If-None-Match header and returns
/// either a 304 Not Modified or the cached JSON.
/// </summary>
internal static class CacheHelper
{
    /// <summary>
    /// If <paramref name="entry"/> is non-null, sets the ETag header and returns
    /// either 304 (if the client already has it) or the cached JSON bytes.
    /// Returns null when no cached entry is available.
    /// </summary>
    public static IResult? ServeIfCached(HttpContext httpContext, (byte[] Json, string ETag)? entry)
    {
        if (entry is null) return null;

        var (json, etag) = entry.Value;
        return ServeBytesWithETag(httpContext, json, etag);
    }

    /// <summary>
    /// Serves a cached first page while projecting its entries array down to a
    /// requested page window that is fully contained in the precomputed first 50 rows.
    /// This lets endpoints reuse a precomputed pageSize=50 response for overview
    /// and full-page requests that ask for fewer rows.
    /// </summary>
    public static IResult? ServeFirstPageSubsetIfCached(
        HttpContext httpContext,
        (byte[] Json, string ETag)? entry,
        int requestedPage,
        int requestedPageSize)
    {
        if (entry is null) return null;
        if (requestedPage <= 0 || requestedPageSize <= 0) return null;
        if (requestedPage == 1 && requestedPageSize == 50) return ServeIfCached(httpContext, entry);

        var json = ProjectFirstPageSubset(entry.Value.Json, requestedPage, requestedPageSize) ?? entry.Value.Json;
        return ServeBytesWithETag(httpContext, json, ResponseCacheService.ComputeETag(json));
    }

    private static IResult ServeBytesWithETag(HttpContext httpContext, byte[] json, string etag)
    {
        var requestETag = httpContext.Request.Headers.IfNoneMatch.ToString();
        if (!string.IsNullOrEmpty(requestETag) && requestETag == etag)
        {
            httpContext.Response.Headers.ETag = etag;
            return Results.StatusCode(304);
        }

        httpContext.Response.Headers.ETag = etag;
        return Results.Bytes(json, "application/json");
    }

    private static byte[]? ProjectFirstPageSubset(byte[] json, int requestedPage, int requestedPageSize)
    {
        if (requestedPage < 1 || requestedPageSize < 1) return null;
        var offset = (requestedPage - 1) * requestedPageSize;
        if (offset < 0 || offset + requestedPageSize > 50) return null;

        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                return null;

            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream))
            {
                writer.WriteStartObject();
                foreach (var property in document.RootElement.EnumerateObject())
                {
                    if (property.NameEquals("page"))
                    {
                        writer.WriteNumber(property.Name, requestedPage);
                        continue;
                    }

                    if (property.NameEquals("pageSize"))
                    {
                        writer.WriteNumber(property.Name, requestedPageSize);
                        continue;
                    }

                    if (property.NameEquals("entries") && property.Value.ValueKind == JsonValueKind.Array)
                    {
                        writer.WritePropertyName(property.Name);
                        writer.WriteStartArray();
                        var index = 0;
                        var written = 0;
                        foreach (var entry in property.Value.EnumerateArray())
                        {
                            if (index++ < offset) continue;
                            if (written >= requestedPageSize) break;
                            entry.WriteTo(writer);
                            written++;
                        }
                        writer.WriteEndArray();
                        continue;
                    }

                    property.WriteTo(writer);
                }
                writer.WriteEndObject();
            }

            return stream.ToArray();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static IResult? ServeUnavailableIfFrozen(HttpContext httpContext, ResponseCacheService cache)
    {
        if (!cache.RequiresCachedReads) return null;

        httpContext.Response.Headers.CacheControl = "no-store";
        httpContext.Response.Headers["Retry-After"] = "30";
        return Results.Problem(
            title: "Published data unavailable",
            detail: "A stable published response is not available for this request yet. Retry shortly.",
            statusCode: StatusCodes.Status503ServiceUnavailable);
    }
}
