using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using Npgsql;

namespace FstStoredRankRollout;

public sealed class ApiRunner
{
    public const int DefaultWarmRequestStartsPerSecond = 80;

    public async Task<ApiCaptureReport> CaptureAsync(
        RolloutManifest manifest,
        Uri baseUri,
        string variant,
        string outputDirectory,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(outputDirectory);
        var bodyDirectory = Path.Combine(outputDirectory, "bodies");
        Directory.CreateDirectory(bodyDirectory);
        using var client = CreateHttpClient(baseUri, maxConnections: 8);
        var items = new List<ApiCaptureItem>();
        var index = 0;
        foreach (var workload in manifest.ApiWorkloads)
        {
            if (workload.ExpectedStatusCode is < 200 or >= 300)
            {
                throw new InvalidDataException(
                    $"Workload {workload.Id} has non-success expected status " +
                    $"{workload.ExpectedStatusCode}.");
            }
            using var request = new HttpRequestMessage(HttpMethod.Get, workload.Path);
            request.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true };
            using var response = await client.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            var body = await response.Content.ReadAsByteArrayAsync(cancellationToken);
            var fileName = $"{++index:D4}-{Sanitize(workload.Id)}.body";
            await File.WriteAllBytesAsync(
                Path.Combine(bodyDirectory, fileName),
                body,
                cancellationToken);
            items.Add(new ApiCaptureItem
            {
                WorkloadId = workload.Id,
                Kind = workload.Kind,
                Path = workload.Path,
                ExpectedStatusCode = workload.ExpectedStatusCode,
                StatusCode = (int)response.StatusCode,
                ContentType = response.Content.Headers.ContentType?.ToString() ?? "",
                ETag = response.Headers.ETag?.ToString(),
                BodySha256 = Sha256(body),
                BodyLength = body.LongLength,
                BodyFile = Path.Combine("bodies", fileName),
            });
        }

        var unexpectedStatusCount = items.Count(static item =>
            item.StatusCode != item.ExpectedStatusCode);
        return new ApiCaptureReport
        {
            Variant = variant,
            CapturedAtUtc = DateTimeOffset.UtcNow,
            ManifestFingerprint = manifest.SelectionFingerprint,
            UnexpectedStatusCount = unexpectedStatusCount,
            Passed = unexpectedStatusCount == 0,
            Items = items,
        };
    }

    public static async Task<ApiComparisonReport> CompareAsync(
        ApiCaptureReport baseline,
        string baselineDirectory,
        ApiCaptureReport candidate,
        string candidateDirectory,
        CancellationToken cancellationToken)
    {
        var differences = new List<ParityDifference>();
        if (!string.Equals(
                baseline.ManifestFingerprint,
                candidate.ManifestFingerprint,
                StringComparison.Ordinal))
        {
            differences.Add(new ParityDifference
            {
                Surface = "api",
                Key = "manifest",
                Field = "fingerprint",
                Baseline = baseline.ManifestFingerprint,
                Candidate = candidate.ManifestFingerprint,
            });
        }

        var baselineItems = baseline.Items.ToDictionary(
            static item => item.WorkloadId,
            StringComparer.Ordinal);
        var candidateItems = candidate.Items.ToDictionary(
            static item => item.WorkloadId,
            StringComparer.Ordinal);
        foreach (var workloadId in baselineItems.Keys
                     .Concat(candidateItems.Keys)
                     .Distinct(StringComparer.Ordinal)
                     .Order(StringComparer.Ordinal))
        {
            var hasBaseline = baselineItems.TryGetValue(workloadId, out var baselineItem);
            var hasCandidate = candidateItems.TryGetValue(workloadId, out var candidateItem);
            if (!hasBaseline || !hasCandidate)
            {
                differences.Add(new ParityDifference
                {
                    Surface = "api",
                    Key = workloadId,
                    Field = "presence",
                    Baseline = hasBaseline ? "present" : null,
                    Candidate = hasCandidate ? "present" : null,
                });
                continue;
            }

            var baselineValue = baselineItem
                                ?? throw new InvalidOperationException("Baseline capture lookup failed.");
            var candidateValue = candidateItem
                                 ?? throw new InvalidOperationException("Candidate capture lookup failed.");
            Compare(workloadId, "path", baselineValue.Path, candidateValue.Path, differences);
            Compare(
                workloadId,
                "expectedStatus",
                baselineValue.ExpectedStatusCode,
                candidateValue.ExpectedStatusCode,
                differences);
            RequireExpectedStatus(
                workloadId,
                "baselineExpectedStatus",
                baselineValue,
                differences);
            RequireExpectedStatus(
                workloadId,
                "candidateExpectedStatus",
                candidateValue,
                differences);
            Compare(workloadId, "status", baselineValue.StatusCode, candidateValue.StatusCode, differences);
            Compare(workloadId, "contentType", baselineValue.ContentType, candidateValue.ContentType, differences);
            Compare(workloadId, "etag", baselineValue.ETag, candidateValue.ETag, differences);
            Compare(workloadId, "bodyLength", baselineValue.BodyLength, candidateValue.BodyLength, differences);
            Compare(workloadId, "bodySha256", baselineValue.BodySha256, candidateValue.BodySha256, differences);
            if (!string.Equals(
                    baselineValue.BodySha256,
                    candidateValue.BodySha256,
                    StringComparison.Ordinal))
            {
                var baselineBody = await File.ReadAllBytesAsync(
                    Path.Combine(baselineDirectory, baselineValue.BodyFile),
                    cancellationToken);
                var candidateBody = await File.ReadAllBytesAsync(
                    Path.Combine(candidateDirectory, candidateValue.BodyFile),
                    cancellationToken);
                var firstDifference = FindFirstDifference(baselineBody, candidateBody);
                differences.Add(new ParityDifference
                {
                    Surface = "api",
                    Key = workloadId,
                    Field = "firstBodyByteDifference",
                    Baseline = firstDifference.Baseline,
                    Candidate = firstDifference.Candidate,
                });
            }
        }

        return new ApiComparisonReport
        {
            ComparedAtUtc = DateTimeOffset.UtcNow,
            BaselineVariant = baseline.Variant,
            CandidateVariant = candidate.Variant,
            WorkloadCount = baselineItems.Keys
                .Concat(candidateItems.Keys)
                .Distinct(StringComparer.Ordinal)
                .Count(),
            DifferenceCount = differences.Count,
            Passed = differences.Count == 0,
            Differences = differences,
        };
    }

    public async Task<BenchmarkBlockReport> BenchmarkAsync(
        NpgsqlDataSource dataSource,
        ApiWorkload workload,
        Uri baseUri,
        BenchmarkScheduleEntry schedule,
        string postgresContainer,
        int warmRequestStartsPerSecond,
        CancellationToken cancellationToken)
    {
        if (warmRequestStartsPerSecond is <= 0 or > 90)
        {
            throw new ArgumentOutOfRangeException(
                nameof(warmRequestStartsPerSecond),
                "Warm request starts must be between 1 and 90 per second.");
        }

        using var client = CreateHttpClient(baseUri, Math.Max(8, schedule.Concurrency));
        if (string.Equals(schedule.Mode, "warm", StringComparison.Ordinal))
        {
            var warmupCount = Math.Max(5, schedule.Concurrency);
            await RunRequestsAsync(
                client,
                workload.Path,
                warmupCount,
                schedule.Concurrency,
                collect: false,
                maxRequestStartsPerSecond: null,
                cancellationToken);
        }

        var startedAt = DateTimeOffset.UtcNow;
        var databaseStart = await ReadOnlyPostgres.ReadDatabaseResourcesAsync(
            dataSource,
            cancellationToken);
        var samples = new ConcurrentQueue<ContainerResourceSample>();
        using var samplerStop = new CancellationTokenSource();
        var samplerArmed = new TaskCompletionSource<DateTimeOffset>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var samplerTask = SampleContainerAsync(
            postgresContainer,
            samples,
            samplerArmed,
            samplerStop.Token,
            cancellationToken);
        var samplerArmedAt = await samplerArmed.Task.WaitAsync(cancellationToken);
        var requestsStartedAt = DateTimeOffset.UtcNow;
        var httpRequestsCompletedAt = requestsStartedAt;
        List<RequestMeasurement> measurements;
        try
        {
            measurements = await RunRequestsAsync(
                client,
                workload.Path,
                schedule.RequestCount,
                schedule.Concurrency,
                collect: true,
                maxRequestStartsPerSecond: string.Equals(
                    schedule.Mode,
                    "warm",
                    StringComparison.Ordinal)
                    ? warmRequestStartsPerSecond
                    : null,
                cancellationToken);
            httpRequestsCompletedAt = DateTimeOffset.UtcNow;
        }
        finally
        {
            samplerStop.Cancel();
            await samplerTask;
        }
        var requestsCompletedAt = DateTimeOffset.UtcNow;
        var databaseEnd = await ReadOnlyPostgres.ReadDatabaseResourcesAsync(
            dataSource,
            cancellationToken);
        var statuses = measurements
            .GroupBy(static item => item.StatusCode)
            .ToDictionary(static group => group.Key, static group => group.Count());
        var fingerprints = measurements
            .Select(static item => item.BodySha256)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();

        return new BenchmarkBlockReport
        {
            StartedAtUtc = startedAt,
            CompletedAtUtc = DateTimeOffset.UtcNow,
            Sequence = schedule.Sequence,
            Variant = schedule.Variant,
            Mode = schedule.Mode,
            Concurrency = schedule.Concurrency,
            WorkloadId = workload.Id,
            Path = workload.Path,
            RequestedCount = schedule.RequestCount,
            CompletedCount = measurements.Count,
            ErrorCount = measurements.Count(static item => item.StatusCode is < 200 or >= 300),
            WarmRequestStartsPerSecond = warmRequestStartsPerSecond,
            SamplerArmedAtUtc = samplerArmedAt,
            RequestsStartedAtUtc = requestsStartedAt,
            HttpRequestsCompletedAtUtc = httpRequestsCompletedAt,
            RequestsCompletedAtUtc = requestsCompletedAt,
            LatencyMilliseconds = measurements.Select(static item => item.ElapsedMilliseconds).ToArray(),
            StatusCounts = statuses,
            BodyFingerprints = fingerprints,
            DatabaseStart = databaseStart,
            DatabaseEnd = databaseEnd,
            PostgresContainerSamples = samples
                .OrderBy(static sample => sample.ObservedAtUtc)
                .ToArray(),
        };
    }

    private static HttpClient CreateHttpClient(Uri baseUri, int maxConnections)
    {
        var handler = new SocketsHttpHandler
        {
            MaxConnectionsPerServer = maxConnections,
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
            AutomaticDecompression = System.Net.DecompressionMethods.All,
        };
        return new HttpClient(handler)
        {
            BaseAddress = baseUri,
            Timeout = TimeSpan.FromSeconds(60),
        };
    }

    private static async Task<List<RequestMeasurement>> RunRequestsAsync(
        HttpClient client,
        string path,
        int requestCount,
        int concurrency,
        bool collect,
        int? maxRequestStartsPerSecond,
        CancellationToken cancellationToken)
    {
        var results = new ConcurrentQueue<RequestMeasurement>();
        var stopwatch = Stopwatch.StartNew();
        var wave = 0;
        for (var offset = 0; offset < requestCount; offset += concurrency)
        {
            if (maxRequestStartsPerSecond.HasValue && wave > 0)
            {
                var targetElapsed = TimeSpan.FromSeconds(
                    wave * concurrency / (double)maxRequestStartsPerSecond.Value);
                var delay = targetElapsed - stopwatch.Elapsed;
                if (delay > TimeSpan.Zero)
                    await Task.Delay(delay, cancellationToken);
            }

            var waveCount = Math.Min(concurrency, requestCount - offset);
            var requests = Enumerable.Range(0, waveCount)
                .Select(async _ =>
                {
                    var requestStopwatch = Stopwatch.StartNew();
                    using var response = await client.GetAsync(
                        path,
                        HttpCompletionOption.ResponseHeadersRead,
                        cancellationToken);
                    var body = await response.Content.ReadAsByteArrayAsync(cancellationToken);
                    requestStopwatch.Stop();
                    if (collect)
                    {
                        results.Enqueue(new RequestMeasurement(
                            requestStopwatch.Elapsed.TotalMilliseconds,
                            (int)response.StatusCode,
                            Sha256(body)));
                    }
                });
            await Task.WhenAll(requests);
            wave++;
        }

        return results.ToList();
    }

    private static async Task SampleContainerAsync(
        string postgresContainer,
        ConcurrentQueue<ContainerResourceSample> samples,
        TaskCompletionSource<DateTimeOffset> samplerArmed,
        CancellationToken stopToken,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(postgresContainer))
            throw new ArgumentException("PostgreSQL container is required.", nameof(postgresContainer));
        samplerArmed.TrySetResult(DateTimeOffset.UtcNow);
        while (true)
        {
            ContainerResourceSample? sample;
            sample = await DockerStats.ReadAsync(
                postgresContainer,
                cancellationToken);
            if (sample is not null)
                samples.Enqueue(sample);
            if (stopToken.IsCancellationRequested)
                return;
            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(500), stopToken);
            }
            catch (OperationCanceledException) when (stopToken.IsCancellationRequested)
            {
                return;
            }
        }
    }

    private static void Compare<T>(
        string key,
        string field,
        T baseline,
        T candidate,
        ICollection<ParityDifference> differences)
    {
        if (EqualityComparer<T>.Default.Equals(baseline, candidate))
            return;
        differences.Add(new ParityDifference
        {
            Surface = "api",
            Key = key,
            Field = field,
            Baseline = baseline?.ToString(),
            Candidate = candidate?.ToString(),
        });
    }

    private static void RequireExpectedStatus(
        string workloadId,
        string field,
        ApiCaptureItem item,
        ICollection<ParityDifference> differences)
    {
        if (item.ExpectedStatusCode is < 200 or >= 300)
        {
            differences.Add(new ParityDifference
            {
                Surface = "api",
                Key = workloadId,
                Field = $"{field}NotSuccessful",
                Baseline = "200-299",
                Candidate = item.ExpectedStatusCode.ToString(),
            });
            return;
        }
        if (item.StatusCode == item.ExpectedStatusCode)
            return;
        differences.Add(new ParityDifference
        {
            Surface = "api",
            Key = workloadId,
            Field = field,
            Baseline = item.ExpectedStatusCode.ToString(),
            Candidate = item.StatusCode.ToString(),
        });
    }

    private static (string Baseline, string Candidate) FindFirstDifference(
        IReadOnlyList<byte> baseline,
        IReadOnlyList<byte> candidate)
    {
        var count = Math.Min(baseline.Count, candidate.Count);
        for (var index = 0; index < count; index++)
        {
            if (baseline[index] != candidate[index])
                return ($"{index}:{baseline[index]}", $"{index}:{candidate[index]}");
        }
        return ($"length:{baseline.Count}", $"length:{candidate.Count}");
    }

    private static string Sanitize(string value)
    {
        var characters = value
            .Select(character => char.IsLetterOrDigit(character) || character is '-' or '_'
                ? character
                : '-')
            .ToArray();
        return new string(characters);
    }

    private static string Sha256(ReadOnlySpan<byte> bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private sealed record RequestMeasurement(
        double ElapsedMilliseconds,
        int StatusCode,
        string BodySha256);
}
