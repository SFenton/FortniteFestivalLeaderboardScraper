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
        var requestETag = httpContext.Request.Headers.IfNoneMatch.ToString();
        if (!string.IsNullOrEmpty(requestETag) && requestETag == etag)
        {
            httpContext.Response.Headers.ETag = etag;
            return Results.StatusCode(304);
        }

        httpContext.Response.Headers.ETag = etag;
        return Results.Bytes(json, "application/json");
    }
}
