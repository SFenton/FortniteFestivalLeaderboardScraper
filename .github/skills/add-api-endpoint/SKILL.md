---
name: add-api-endpoint
description: "Add a new REST API endpoint to FSTService. Use when creating a new route with proper caching, rate limiting, error handling, DTOs, tests, and API client method. Follows the canonical endpoint patterns from the API consistency registry."
argument-hint: "Description of endpoint (e.g., 'GET /api/player/{id}/achievements')"
---

# Add API Endpoint

## When to Use

- Adding a new REST endpoint to FSTService
- Creating a new query or mutation in the API layer

## Prerequisites

Read the API consistency registry: `/memories/repo/architecture/api-consistency-registry.md`

## Procedure

### 1. Design the Endpoint

Determine from the registry:
- **HTTP method**: GET (queries), POST (mutations), DELETE (removals)
- **Route**: `/api/{resource}/{id}/{subresource}` (kebab-case)
- **Response format**: `Results.Bytes()` + ETag (cached) or `Results.Ok()` (uncached)
- **Cache tier**: volatile (60s), standard (120s), stable (300s), static (1800s)
- **Rate limit**: public (60/min) or protected (30/min)
- **Auth**: `.RequireAuthorization()` if API key needed

### 2. Create/Update Endpoint Group

In the appropriate `FSTService/Api/{Group}Endpoints.cs`:

```csharp
// For cached endpoints:
app.MapGet("/api/{resource}/{id}", async (
    HttpContext httpContext,
    string id,
    ResponseCacheService cache,
    /* ... other deps */
) =>
{
    var cached = CacheHelper.ServeIfCached(httpContext, cache.Get(cacheKey));
    if (cached is not null) return cached;

    // Build response
    var data = /* query data */;
    if (data is null)
        return Results.NotFound(new { error = "{Resource} not found." });

    var jsonBytes = JsonSerializer.SerializeToUtf8Bytes(data, jsonOpts);
    var etag = cache.Set(cacheKey, jsonBytes);
    httpContext.Response.Headers.ETag = etag;
    httpContext.Response.Headers.CacheControl = "public, max-age={tier}, stale-while-revalidate={2x}";
    return Results.Bytes(jsonBytes, "application/json");
})
.WithTags("{Group}")
.RequireRateLimiting("public");
```

### 3. Register Route

In `FSTService/Api/ApiEndpoints.cs`, ensure the endpoint group is registered.

### 4. Update API Client

In `FortniteFestivalWeb/src/api/client.ts`:
```typescript
export async function fetch{ResourceName}(id: string): Promise<{ResponseType}> {
  const res = await fetch(`${BASE_URL}/api/{resource}/${id}`);
  if (!res.ok) throw new ApiError(res);
  return res.json();
}
```

Add query key in `FortniteFestivalWeb/src/api/queryKeys.ts`:
```typescript
export const queryKeys = {
  // ... existing keys
  {resource}: (id: string) => ['{resource}', id] as const,
};
```

### 5. Write Tests

FSTService test in `FSTService.Tests/Unit/{Group}EndpointsTests.cs`:
- Test 200 response with valid data
- Test 404 when resource not found
- Test 400 for invalid parameters
- Test rate limiting tier

### 6. Verify

```bash
dotnet test FSTService.Tests\FSTService.Tests.csproj
```

## Checklist

- [ ] Route follows `/api/{resource}/{id}/{subresource}` convention (kebab-case)
- [ ] Response format matches decision tree (Bytes+ETag for cached, Ok for uncached)
- [ ] Cache-Control tier from registry applied
- [ ] Rate limiting category applied
- [ ] Error responses: `{ error: string }` for 400/404
- [ ] `.WithTags()` for endpoint grouping
- [ ] API client method in `client.ts`
- [ ] Query key in `queryKeys.ts`
- [ ] Tests for 200, 404, 400 cases
- [ ] Coverage ≥ 94%
- [ ] Reviewed by fst-principal-api-designer
