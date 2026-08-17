using FSTService.Persistence;
using FSTService.Scraping;

namespace FSTService.Api;

public sealed class PublicApiResponseCacheMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<PublicApiResponseCacheMiddleware>
        _log;
    private readonly TimeSpan? _maxBuildDurationOverride;

    public PublicApiResponseCacheMiddleware(
        RequestDelegate next,
        ILogger<PublicApiResponseCacheMiddleware> log,
        TimeSpan? maxBuildDurationOverride = null)
    {
        _next = next;
        _log = log;
        _maxBuildDurationOverride =
            maxBuildDurationOverride;
    }

    public async Task InvokeAsync(
        HttpContext context,
        IMetaDatabase metaDb,
        PublicReadGateService gate,
        PublicApiCacheTelemetry telemetry,
        PublicationReadContextService? publicationService = null,
        PublicationApiResponseCacheService? cacheService = null)
    {
        if (context.WebSockets.IsWebSocketRequest)
        {
            await _next(context);
            return;
        }

        var publicationBound =
            PublicApiResponseCachePolicy
                .IsAuthoritativePublicationBound(context);
        var cacheable =
            PublicApiResponseCachePolicy.TryCreateRequestPlan(
                context,
                out var plan);

        if (!cacheable)
        {
            if (publicationBound && gate.IsFrozen)
            {
                telemetry.Record(context, PublicApiCacheOutcome.Bypassed);
            }
            await _next(context);
            return;
        }

        var safety = gate.GetCacheSafetySnapshot();
        if (FailedCandidateReadRoutingPolicy.EndpointHandlesRead(
                context,
                safety.FailedCandidateIsolationActive)
            && !plan.FreezeCritical)
        {
            if (publicationBound)
                telemetry.Record(context, PublicApiCacheOutcome.Bypassed);
            context.Response.Headers["X-FST-Public-Cache"] =
                "endpoint";
            await _next(context);
            return;
        }

        var shouldLookup =
            safety.IsFrozen
            || plan.FreezeCritical;
        if (shouldLookup)
        {
            var serviceHit = cacheService is null
                ? null
                : TryGetServiceHit(
                    context,
                    plan,
                    cacheService,
                    publicationService,
                    safety);
            if (serviceHit is not null)
            {
                await ServeHitAsync(
                    context,
                    metaDb,
                    gate,
                    telemetry,
                    plan,
                    serviceHit.Value);
                return;
            }

            if (cacheService is null && safety.IsFrozen)
            {
                var cacheKey = plan.RequestCacheKey;
                var publicationContext =
                    context.GetPublicationReadContext();
                PublicationCachedResponse? currentCached = null;
                (byte[] Json, string ETag)? cached;
                if (publicationContext is not null)
                {
                    cached = metaDb.GetCachedResponse(
                        publicationContext.PublicationId,
                        cacheKey);
                }
                else
                {
                var lookup =
                    metaDb.GetCurrentCacheLookup(cacheKey);
                currentCached = lookup?.CachedResponse;
                cached = currentCached is null
                    ? lookup?.HasCurrentPublication == true
                        || publicationService?.PinningConfigured
                            == true
                        ? null
                        : metaDb.GetCachedResponse(cacheKey)
                    : (currentCached.Json, currentCached.ETag);

                if (currentCached is not null
                    && publicationService?.PinningConfigured == true
                    && !CanServePinnedCacheHit(
                        context,
                        publicationService,
                        currentCached))
                {
                    await _next(context);
                    return;
                }
                }
                var cachedResult =
                    CacheHelper.ServeIfCached(
                        context,
                        cached);
                if (cachedResult is not null)
                {
                    if (currentCached is not null)
                    {
                        SetPublicationContext(
                            context,
                            currentCached);
                    }
                    if (publicationBound)
                    {
                        telemetry.Record(
                            context,
                            PublicApiCacheOutcome.Hit);
                    }
                    context.Response.Headers[
                        "X-FST-Public-Cache"] = "hit";
                    await cachedResult.ExecuteAsync(context);
                    await RecordSelectedActivityAsync(
                        context,
                        metaDb,
                        gate);
                    return;
                }
            }

            if (cacheService is not null)
            {
                telemetry.RecordOperation(
                    context,
                    plan.RequestCacheKey,
                    cacheService.CurrentPublicationId,
                    revision: null,
                    PublicationApiCacheOperation.Miss,
                    TimeSpan.Zero,
                    payloadBytes: 0);
            }

            if (safety.IsFrozen)
            {
                if (await BlockFrozenMissIfRequiredAsync(
                        context,
                        gate,
                        telemetry,
                        publicationBound,
                        plan))
                {
                    return;
                }
            }
        }

        if (!safety.IsFrozen
            && plan.FreezeCritical
            && plan.AllowWriteThrough
            && cacheService is not null
            && cacheService.CurrentPublicationId
                is long publicationId)
        {
            await ExecuteSingleFlightBuildAsync(
                context,
                metaDb,
                gate,
                telemetry,
                cacheService,
                plan,
                publicationId);
            return;
        }

        await _next(context);
    }

    private PublicationApiCacheHit? TryGetServiceHit(
        HttpContext context,
        PublicApiCacheRequestPlan plan,
        PublicationApiResponseCacheService cacheService,
        PublicationReadContextService? publicationService,
        PublicReadCacheSafetySnapshot safety)
    {
        if (PublicationReadContextMiddleware
                .TryReadRequestedPublicationId(
                    context.Request,
                    out var requestedPublicationId,
                    out _)
            && requestedPublicationId.HasValue)
        {
            if (publicationService?.PinningConfigured
                != true)
            {
                return cacheService.TryGetCurrent(
                    plan,
                    safety);
            }

            var hit = cacheService.TryGet(
                requestedPublicationId.Value,
                plan,
                safety);
            if (hit is null)
                return null;
            var cached = new PublicationCachedResponse(
                hit.Value.PublicationId,
                hit.Value.PublishedScrapeId,
                hit.Value.PublishedAtUtc,
                hit.Value.Json,
                hit.Value.ETag,
                hit.Value.CachedAtUtc,
                hit.Value.ContentType,
                hit.Value.ContentSha256,
                hit.Value.SourceCacheKey);
            return CanServePinnedCacheHit(
                context,
                publicationService,
                cached)
                    ? hit
                    : null;
        }

        return cacheService.TryGetCurrent(
            plan,
            safety);
    }

    private async Task ServeHitAsync(
        HttpContext context,
        IMetaDatabase metaDb,
        PublicReadGateService gate,
        PublicApiCacheTelemetry telemetry,
        PublicApiCacheRequestPlan plan,
        PublicationApiCacheHit hit)
    {
        context.Response.ContentType = hit.ContentType;
        if (!string.IsNullOrWhiteSpace(
                plan.ResponseCacheControl))
        {
            context.Response.Headers.CacheControl =
                plan.ResponseCacheControl;
        }
        SetPublicationContext(
            context,
            new PublicationCachedResponse(
                hit.PublicationId,
                hit.PublishedScrapeId,
                hit.PublishedAtUtc,
                hit.Json,
                hit.ETag,
                hit.CachedAtUtc,
                hit.ContentType,
                hit.ContentSha256,
                hit.SourceCacheKey));
        context.Response.Headers[
            "X-FST-Public-Cache"] = "hit";
        context.Response.Headers[
            "X-FST-Public-Cache-Tier"] =
            hit.Tier == PublicationApiCacheTier.L1
                ? "l1"
                : "l2";
        telemetry.Record(
            context,
            PublicApiCacheOutcome.Hit);
        telemetry.RecordOperation(
            context,
            plan.RequestCacheKey,
            hit.PublicationId,
            hit.ContentSha256,
            hit.Tier == PublicationApiCacheTier.L1
                ? PublicationApiCacheOperation.L1Hit
                : PublicationApiCacheOperation.L2Hit,
            TimeSpan.Zero,
            hit.Json.Length,
            cachedAtUtc: hit.CachedAtUtc);
        await CacheHelper.ServeIfCached(
                context,
                (hit.Json, hit.ETag))!
            .ExecuteAsync(context);
        await RecordSelectedActivityAsync(
            context,
            metaDb,
            gate);
    }

    private async Task ExecuteSingleFlightBuildAsync(
        HttpContext context,
        IMetaDatabase metaDb,
        PublicReadGateService gate,
        PublicApiCacheTelemetry telemetry,
        PublicationApiResponseCacheService cacheService,
        PublicApiCacheRequestPlan plan,
        long publicationId)
    {
        await using var buildLease =
            await cacheService.AcquireBuildLeaseAsync(
                publicationId,
                plan.RequestCacheKey,
                context.RequestAborted);
        if (buildLease.Waited)
        {
            telemetry.RecordOperation(
                context,
                plan.RequestCacheKey,
                publicationId,
                revision: null,
                PublicationApiCacheOperation
                    .SingleFlightWait,
                TimeSpan.Zero,
                payloadBytes: 0);
        }

        var retrySafety =
            gate.GetCacheSafetySnapshot();
        var existing = cacheService.TryGetCurrent(
            plan,
            retrySafety);
        if (existing is not null)
        {
            await ServeHitAsync(
                context,
                metaDb,
                gate,
                telemetry,
                plan,
                existing.Value);
            return;
        }

        if (gate.IsFrozen)
        {
            await BlockFrozenMissIfRequiredAsync(
                context,
                gate,
                telemetry,
                publicationBound: true,
                plan);
            return;
        }

        var originalBody = context.Response.Body;
        await using var buffer = new MemoryStream();
        context.Response.Body = buffer;
        var stopwatch = System.Diagnostics
            .Stopwatch.StartNew();
        Exception? buildError = null;
        try
        {
            await _next(context);
        }
        catch (Exception ex)
        {
            buildError = ex;
            throw;
        }
        finally
        {
            stopwatch.Stop();
            context.Response.Body = originalBody;
            if (buildError is not null)
            {
                _log.LogWarning(
                    buildError,
                    "Publication API cache build failed for key hash {KeyHash}.",
                    PublicApiCacheTelemetry.HashKey(
                        plan.RequestCacheKey));
                telemetry.RecordOperation(
                    context,
                    plan.RequestCacheKey,
                    publicationId,
                    revision: null,
                    PublicationApiCacheOperation.BuildError,
                    stopwatch.Elapsed,
                    payloadBytes: 0,
                    buildError);
            }
        }

        var json = buffer.ToArray();
        async Task FlushResponseAsync()
        {
            buffer.Position = 0;
            await buffer.CopyToAsync(
                originalBody,
                context.RequestAborted);
        }

        var contentType =
            context.Response.ContentType
            ?? string.Empty;
        if (context.Response.StatusCode
                != StatusCodes.Status200OK
            || !contentType.StartsWith(
                "application/json",
                StringComparison.OrdinalIgnoreCase)
            || json.Length == 0
            || json.Length > plan.MaxPayloadBytes)
        {
            telemetry.RecordOperation(
                context,
                plan.RequestCacheKey,
                publicationId,
                revision: null,
                PublicationApiCacheOperation
                    .BuildRejectedResponse,
                stopwatch.Elapsed,
                json.Length);
            await FlushResponseAsync();
            return;
        }

        if (stopwatch.Elapsed
            > (_maxBuildDurationOverride
               ?? plan.MaxBuildDuration))
        {
            telemetry.RecordOperation(
                context,
                plan.RequestCacheKey,
                publicationId,
                revision: null,
                PublicationApiCacheOperation
                    .BuildRejectedSlow,
                stopwatch.Elapsed,
                json.Length);
            await FlushResponseAsync();
            return;
        }

        var etag = context.Response.Headers.ETag
            .ToString();
        if (string.IsNullOrWhiteSpace(etag))
        {
            etag = ResponseCacheService
                .ComputeETag(json);
            context.Response.Headers.ETag = etag;
        }
        var canonicalStored =
            cacheService.TryGetCurrent(plan);
        if (canonicalStored is not null
            && canonicalStored.Value.Json
                .AsSpan()
                .SequenceEqual(json)
            && string.Equals(
                canonicalStored.Value.ETag,
                etag,
                StringComparison.Ordinal))
        {
            context.Response.Headers[
                "X-FST-Public-Cache"] = "build";
            telemetry.RecordOperation(
                context,
                plan.RequestCacheKey,
                publicationId,
                canonicalStored.Value.ContentSha256,
                PublicationApiCacheOperation.BuildStored,
                stopwatch.Elapsed,
                json.Length);
            await FlushResponseAsync();
            return;
        }
        var stored = cacheService.TryStoreCurrent(
            publicationId,
            plan.RequestCacheKey,
            json,
            etag);
        if (stored is null)
        {
            telemetry.RecordOperation(
                context,
                plan.RequestCacheKey,
                publicationId,
                revision: null,
                PublicationApiCacheOperation
                    .BuildRejectedResponse,
                stopwatch.Elapsed,
                json.Length);
            await FlushResponseAsync();
            return;
        }

        context.Response.Headers[
            "X-FST-Public-Cache"] = "build";
        telemetry.RecordOperation(
            context,
            plan.RequestCacheKey,
            publicationId,
            stored.ContentSha256,
            PublicationApiCacheOperation.BuildStored,
            stopwatch.Elapsed,
            json.Length);
        await FlushResponseAsync();
    }

    private static async Task<bool>
        BlockFrozenMissIfRequiredAsync(
            HttpContext context,
            PublicReadGateService gate,
            PublicApiCacheTelemetry telemetry,
            bool publicationBound,
            PublicApiCacheRequestPlan plan)
    {
        context.Response.Headers[
            "X-FST-Public-Cache"] = "miss";
        var state = gate.GetState();
        if (!publicationBound
            || !gate.RequiresCachedReads
            || state.PublicationCommitPending
            || !(plan.FreezeCritical
                 || PublicReadGateMiddleware
                     .RequiresPublishedData(context.Request)
                 || state.MaxScoreMaintenance
                 && PublicReadGateMiddleware
                     .RequiresMaxScoreMaintenanceData(
                         context.Request)))
        {
            if (publicationBound)
            {
                telemetry.Record(
                    context,
                    PublicApiCacheOutcome.MissContinued);
            }
            return false;
        }

        telemetry.Record(
            context,
            PublicApiCacheOutcome.MissBlocked);
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers["Retry-After"] = "30";
        await Results.Problem(
                title: "Published data unavailable",
                detail:
                    "Published data is under a fail-closed maintenance or recovery gate. This route is held until a stable published response is available.",
                statusCode:
                    StatusCodes.Status503ServiceUnavailable)
            .ExecuteAsync(context);
        return true;
    }

    private static void SetPublicationContext(
        HttpContext context,
        PublicationCachedResponse cached)
    {
        context.Response.Headers[
            PublicationReadContextMiddleware.PublicationHeader] =
            cached.PublicationId.ToString(
                System.Globalization.CultureInfo
                    .InvariantCulture);
        context.Response.Headers.Append(
            "Vary",
            PublicationReadContextMiddleware.PublicationHeader);
        context.SetPublicationReadContext(
            new PublicationReadContext(
                cached.PublicationId,
                cached.PublishedScrapeId,
                cached.PublishedAtUtc));
    }

    private static Task RecordSelectedActivityAsync(
        HttpContext context,
        IMetaDatabase metaDb,
        PublicReadGateService gate)
    {
        if (!SelectedProfileHeaders.TryParse(
                context.Request.Headers,
                out var selection)
            || selection is null)
        {
            return Task.CompletedTask;
        }

        return SelectedProfileActivityMiddleware
            .RecordActivityIfNeededAsync(
                context,
                metaDb,
                context.RequestServices
                    .GetService<
                        RegistrationMutationCoordinator>(),
                context.RequestServices
                    .GetService<
                        Microsoft.Extensions.Options
                            .IOptions<ScraperOptions>>()
                    ?.Value.RolloutReadOnlyStartup
                == true,
                gate.GetState().MaxScoreMaintenance,
                context.RequestAborted);
    }

    private static bool CanServePinnedCacheHit(
        HttpContext context,
        PublicationReadContextService publicationService,
        PublicationCachedResponse cached)
    {
        if (!PublicationReadContextMiddleware
                .TryReadRequestedPublicationId(
                    context.Request,
                    out var requestedPublicationId,
                    out _)
            || requestedPublicationId.HasValue
            && requestedPublicationId.Value
                != cached.PublicationId)
        {
            return false;
        }

        try
        {
            var readiness =
                publicationService.EvaluateReadiness(
                    new PublicationPointerState(
                        cached.PublicationId,
                        PreviousPublicationId: null,
                        WorkingPublicationId: null,
                        cached.PublishedScrapeId,
                        cached.PublishedAtUtc));
            return readiness.ReadyForPinning;
        }
        catch
        {
            return false;
        }
    }
}
