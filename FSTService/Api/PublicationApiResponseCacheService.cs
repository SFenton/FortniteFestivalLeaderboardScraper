using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using FSTService.Persistence;

namespace FSTService.Api;

public enum PublicationApiCacheTier
{
    L1,
    L2,
}

public readonly record struct PublicationApiCacheHit(
    long PublicationId,
    long PublishedScrapeId,
    DateTime? PublishedAtUtc,
    byte[] Json,
    string ETag,
    DateTime? CachedAtUtc,
    string ContentType,
    string ContentSha256,
    string SourceCacheKey,
    PublicationApiCacheTier Tier);

public sealed class PublicationApiResponseCacheService
{
    private readonly IMetaDatabase _metaDb;
    private readonly PublicReadGateService _gate;
    private readonly Func<long?> _publicationIdProvider;
    private readonly ILogger<PublicationApiResponseCacheService> _log;
    private readonly ConcurrentDictionary<L1Key, PublicationApiCacheHit>
        _l1 = new();
    private readonly ConcurrentDictionary<BuildKey, SemaphoreSlim>
        _buildGates = new();
    private readonly ConcurrentDictionary<string, byte>
        _staleCurrentKeys = new(StringComparer.Ordinal);
    private readonly object _revisionLock = new();
    private long _lastSafetyRevision = long.MinValue;

    public PublicationApiResponseCacheService(
        IMetaDatabase metaDb,
        PublicReadGateService gate,
        Func<long?> publicationIdProvider,
        ILogger<PublicationApiResponseCacheService> log)
    {
        _metaDb = metaDb;
        _gate = gate;
        _publicationIdProvider = publicationIdProvider;
        _log = log;
    }

    public long? CurrentPublicationId =>
        _publicationIdProvider();

    public PublicationApiCacheHit? TryGetCurrent(
        PublicApiCacheRequestPlan plan)
    {
        var publicationId = _publicationIdProvider();
        return publicationId.HasValue
            ? TryGetCore(
                publicationId.Value,
                plan,
                currentPublication: true)
            : null;
    }

    public PublicationApiCacheHit? TryGet(
        long publicationId,
        PublicApiCacheRequestPlan plan) =>
        TryGetCore(
            publicationId,
            plan,
            currentPublication: false);

    public PublicationCachedResponse? TryStoreCurrent(
        long expectedPublicationId,
        string cacheKey,
        byte[] json,
        string etag)
    {
        var before = _gate.GetCacheSafetySnapshot();
        if (before.IsFrozen
            || before.FailedCandidateIsolationActive
            || _publicationIdProvider() != expectedPublicationId)
        {
            return null;
        }

        PublicationCachedResponse? existing;
        try
        {
            existing = _metaDb
                .GetCurrentCacheLookup(cacheKey)
                ?.CachedResponse;
        }
        catch (Exception ex)
        {
            _log.LogWarning(
                ex,
                "Publication cache comparison failed for key hash {KeyHash}.",
                PublicApiCacheTelemetry.HashKey(cacheKey));
            return null;
        }
        if (existing is not null
            && existing.PublicationId
                == expectedPublicationId
            && string.Equals(
                existing.ETag,
                etag,
                StringComparison.Ordinal)
            && existing.Json.AsSpan()
                .SequenceEqual(json))
        {
            var unchangedAfter =
                _gate.GetCacheSafetySnapshot();
            if (!unchangedAfter.IsFrozen
                && !unchangedAfter
                    .FailedCandidateIsolationActive
                && unchangedAfter.Revision
                    == before.Revision
                && _publicationIdProvider()
                    == expectedPublicationId)
            {
                InvalidateAll();
                return existing;
            }
        }

        PublicationCachedResponse? stored;
        try
        {
            stored = _metaDb.TrySetCurrentCachedResponse(
                expectedPublicationId,
                cacheKey,
                json,
                etag);
        }
        catch (Exception ex)
        {
            _log.LogWarning(
                ex,
                "Publication cache write-through failed for key hash {KeyHash}.",
                PublicApiCacheTelemetry.HashKey(cacheKey));
            return null;
        }
        var after = _gate.GetCacheSafetySnapshot();
        if (stored is null
            || after.IsFrozen
            || after.FailedCandidateIsolationActive
            || after.Revision != before.Revision
            || _publicationIdProvider() != expectedPublicationId)
        {
            return null;
        }
        InvalidateAll();
        InvalidateAll();
        return stored;
    }

    public async ValueTask<PublicationApiCacheBuildLease>
        AcquireBuildLeaseAsync(
            long publicationId,
            string cacheKey,
            CancellationToken ct)
    {
        var gate = _buildGates.GetOrAdd(
            new BuildKey(publicationId, cacheKey),
            static _ => new SemaphoreSlim(1, 1));
        var stopwatch = Stopwatch.StartNew();
        await gate.WaitAsync(ct);
        stopwatch.Stop();
        return new PublicationApiCacheBuildLease(
            gate,
            stopwatch.Elapsed > TimeSpan.FromMilliseconds(1));
    }

    public void InvalidateAll()
    {
        _l1.Clear();
    }

    public void Reset()
    {
        _l1.Clear();
        _staleCurrentKeys.Clear();
    }

    public void MarkCurrentKeyStale(string cacheKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cacheKey);
        _staleCurrentKeys[cacheKey] = 0;
        _l1.Clear();
    }

    public void ClearCurrentKeyStale(string cacheKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cacheKey);
        _staleCurrentKeys.TryRemove(cacheKey, out _);
    }

    private PublicationApiCacheHit? TryGetCore(
        long publicationId,
        PublicApiCacheRequestPlan plan,
        bool currentPublication)
    {
        var safety = _gate.GetCacheSafetySnapshot();
        RefreshSafetyRevision(safety);

        var l1Key = new L1Key(
            publicationId,
            safety.Revision,
            plan.RequestCacheKey);
        if (!safety.FailedCandidateIsolationActive
            && _l1.TryGetValue(l1Key, out var l1))
            return l1 with { Tier = PublicationApiCacheTier.L1 };

        foreach (var candidate in plan.LookupCandidates)
        {
            if (currentPublication
                && !safety.IsFrozen
                && _staleCurrentKeys.ContainsKey(
                    candidate.CacheKey))
            {
                continue;
            }

            PublicationCachedResponse? cached;
            if (currentPublication)
            {
                var lookup =
                    _metaDb.GetCurrentCacheLookup(
                        candidate.CacheKey);
                cached = lookup?.CachedResponse;
            }
            else
            {
                cached = _metaDb.GetCachedResponseEntry(
                    publicationId,
                    candidate.CacheKey);
            }
            if (cached is null
                || cached.PublicationId != publicationId)
            {
                continue;
            }

            var transformed = Transform(
                cached,
                candidate);
            if (transformed is null)
                continue;

            if (!safety.FailedCandidateIsolationActive)
                _l1[l1Key] = transformed.Value;
            return transformed;
        }

        return null;
    }

    private PublicationApiCacheHit? Transform(
        PublicationCachedResponse cached,
        PublicApiCacheLookupCandidate candidate)
    {
        var json = candidate.Transform switch
        {
            PublicApiCacheTransform.None =>
                cached.Json,
            PublicApiCacheTransform.FirstPageSubset =>
                CacheHelper.ProjectFirstPageSubset(
                    cached.Json,
                    candidate.Page,
                    candidate.PageSize),
            PublicApiCacheTransform.OverviewSubset =>
                CacheHelper.ProjectOverviewSubset(
                    cached.Json,
                    candidate.PageSize),
            _ => throw new ArgumentOutOfRangeException(),
        };
        if (json is null)
        {
            _log.LogWarning(
                "Publication cache transform failed for key hash {KeyHash}.",
                PublicApiCacheTelemetry.HashKey(
                    candidate.CacheKey));
            return null;
        }

        var etag = ReferenceEquals(json, cached.Json)
            ? cached.ETag
            : ResponseCacheService.ComputeETag(json);
        var contentSha256 =
            ReferenceEquals(json, cached.Json)
            && !string.IsNullOrWhiteSpace(
                cached.ContentSha256)
                ? cached.ContentSha256
                : Convert.ToHexString(SHA256.HashData(json))
                    .ToLowerInvariant();
        return new PublicationApiCacheHit(
            cached.PublicationId,
            cached.PublishedScrapeId,
            cached.PublishedAtUtc,
            json,
            etag,
            cached.CachedAtUtc,
            cached.ContentType,
            contentSha256,
            candidate.CacheKey,
            PublicationApiCacheTier.L2);
    }

    private void RefreshSafetyRevision(
        PublicReadCacheSafetySnapshot safety)
    {
        if (Volatile.Read(ref _lastSafetyRevision)
            == safety.Revision)
        {
            return;
        }

        lock (_revisionLock)
        {
            if (_lastSafetyRevision == safety.Revision)
                return;

            _l1.Clear();
            Volatile.Write(
                ref _lastSafetyRevision,
                safety.Revision);
        }
    }

    private readonly record struct L1Key(
        long PublicationId,
        long SafetyRevision,
        string CacheKey);

    private readonly record struct BuildKey(
        long PublicationId,
        string CacheKey);
}

public sealed class PublicationApiCacheBuildLease
    : IAsyncDisposable
{
    private SemaphoreSlim? _gate;

    internal PublicationApiCacheBuildLease(
        SemaphoreSlim gate,
        bool waited)
    {
        _gate = gate;
        Waited = waited;
    }

    public bool Waited { get; }

    public ValueTask DisposeAsync()
    {
        Interlocked.Exchange(ref _gate, null)?.Release();
        return ValueTask.CompletedTask;
    }
}
