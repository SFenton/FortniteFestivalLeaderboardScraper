namespace FstStoredRankRollout;

public static class BenchmarkAnalyzer
{
    public static BenchmarkAnalysisReport Analyze(
        RolloutManifest manifest,
        ParityReport parity,
        ApiComparisonReport apiComparison,
        IReadOnlyList<BenchmarkBlockReport> blocks)
    {
        var failures = new List<string>();
        var correctnessPassed = parity.Passed && apiComparison.Passed;
        var sampleCountsPassed = true;
        var expectedSchedule = DeterministicRollout.BuildSchedule(manifest, manifest.Seed);
        var blocksBySequence = blocks
            .GroupBy(static block => block.Sequence)
            .ToDictionary(static group => group.Key, static group => group.ToArray());
        foreach (var expected in expectedSchedule)
        {
            if (!blocksBySequence.TryGetValue(expected.Sequence, out var matching)
                || matching.Length != 1)
            {
                sampleCountsPassed = false;
                failures.Add($"schedule-sequence:{expected.Sequence}:missing-or-duplicate");
                continue;
            }
            var actual = matching[0];
            if (!string.Equals(actual.WorkloadId, expected.WorkloadId, StringComparison.Ordinal)
                || !string.Equals(actual.Mode, expected.Mode, StringComparison.Ordinal)
                || actual.Concurrency != expected.Concurrency
                || !string.Equals(actual.Variant, expected.Variant, StringComparison.Ordinal)
                || actual.RequestedCount != expected.RequestCount)
            {
                sampleCountsPassed = false;
                failures.Add($"schedule-sequence:{expected.Sequence}:metadata-mismatch");
            }
            if (actual.CompletedCount != actual.RequestedCount
                || actual.LatencyMilliseconds.Count != actual.CompletedCount
                || actual.StatusCounts.Values.Sum() != actual.CompletedCount)
            {
                sampleCountsPassed = false;
                failures.Add($"schedule-sequence:{expected.Sequence}:incomplete-measurements");
            }
        }
        if (blocks.Count != expectedSchedule.Count)
        {
            sampleCountsPassed = false;
            failures.Add($"schedule-block-count:{blocks.Count}:expected:{expectedSchedule.Count}");
        }
        if (!parity.Passed)
            failures.Add($"row-parity-differences:{parity.DifferenceCount}");
        if (!apiComparison.Passed)
            failures.Add($"api-differences:{apiComparison.DifferenceCount}");

        foreach (var block in blocks.Where(static block => block.ErrorCount > 0))
            failures.Add($"benchmark-http-errors:{block.Sequence}:{block.ErrorCount}");
        if (manifest.PostgresNetworkBindings.Count > 0)
        {
            foreach (var block in blocks)
            {
                if (block.DatabaseAttestation is not { Passed: true } attestation
                    || !ReadOnlyPostgres.CompareDatabaseIdentity(
                            manifest,
                            attestation.Observed)
                        .Passed)
                {
                    correctnessPassed = false;
                    failures.Add(
                        $"benchmark-database-attestation:{block.Sequence}");
                }
            }
        }

        var workloadsById = manifest.ApiWorkloads.ToDictionary(
            static workload => workload.Id,
            StringComparer.Ordinal);
        foreach (var workloadGroup in blocks.GroupBy(static block => block.WorkloadId, StringComparer.Ordinal))
        {
            var baselineFingerprints = workloadGroup
                .Where(static block => block.Variant == "baseline")
                .SelectMany(static block => block.BodyFingerprints)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            var candidateFingerprints = workloadGroup
                .Where(static block => block.Variant == "candidate")
                .SelectMany(static block => block.BodyFingerprints)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            if (baselineFingerprints.Length != 1
                || candidateFingerprints.Length != 1
                || !string.Equals(
                    baselineFingerprints.FirstOrDefault(),
                    candidateFingerprints.FirstOrDefault(),
                    StringComparison.Ordinal))
            {
                correctnessPassed = false;
                failures.Add($"benchmark-body-fingerprint:{workloadGroup.Key}");
            }

            var statusCodes = workloadGroup
                .SelectMany(static block => block.StatusCounts.Keys)
                .Distinct()
                .Order()
                .ToArray();
            if (!workloadsById.TryGetValue(workloadGroup.Key, out var workloadDefinition)
                || statusCodes.Length != 1
                || workloadDefinition.ExpectedStatusCode is < 200 or >= 300
                || statusCodes[0] != workloadDefinition.ExpectedStatusCode)
            {
                correctnessPassed = false;
                failures.Add(
                    $"benchmark-http-status:{workloadGroup.Key}:" +
                    $"{string.Join(',', statusCodes)}:expected:" +
                    $"{workloadDefinition?.ExpectedStatusCode.ToString() ?? "<missing>"}");
            }

            var warmRates = workloadGroup
                .Where(static block => block.Mode == "warm")
                .Select(static block => block.WarmRequestStartsPerSecond)
                .Distinct()
                .ToArray();
            if (warmRates.Length != 1 || warmRates[0] is <= 0 or > 90)
            {
                sampleCountsPassed = false;
                failures.Add($"benchmark-warm-request-rate:{workloadGroup.Key}");
            }
        }

        var workloadAnalyses = new List<BenchmarkWorkloadAnalysis>();
        var performancePassed = true;
        foreach (var workload in manifest.ApiWorkloads.Where(static workload => workload.Benchmark))
        {
            foreach (var mode in new[] { "cold", "warm" })
            {
                foreach (var concurrency in new[] { 1, 8 })
                {
                    var matching = blocks.Where(block =>
                            string.Equals(block.WorkloadId, workload.Id, StringComparison.Ordinal)
                            && string.Equals(block.Mode, mode, StringComparison.Ordinal)
                            && block.Concurrency == concurrency)
                        .ToArray();
                    var baseline = matching
                        .Where(static block => block.Variant == "baseline")
                        .SelectMany(static block => block.LatencyMilliseconds)
                        .ToArray();
                    var candidate = matching
                        .Where(static block => block.Variant == "candidate")
                        .SelectMany(static block => block.LatencyMilliseconds)
                        .ToArray();
                    var minimum = workload.Core
                        ? mode == "cold" ? 30 : 200
                        : mode == "cold" ? concurrency == 1 ? 6 : 16 : 50;
                    var samplePass = baseline.Length >= minimum && candidate.Length >= minimum;
                    if (!samplePass)
                    {
                        sampleCountsPassed = false;
                        failures.Add(
                            $"sample-count:{workload.Id}:{mode}:c{concurrency}:" +
                            $"{baseline.Length}/{candidate.Length}:minimum:{minimum}");
                    }

                    var baselineP95 = RolloutStatistics.Percentile(baseline, 0.95);
                    var candidateP95 = RolloutStatistics.Percentile(candidate, 0.95);
                    var change = RolloutStatistics.ChangePercent(baselineP95, candidateP95);
                    var metricPassed = change.HasValue
                                       && (workload.Core && mode == "warm"
                                           ? change.Value <= -10.0
                                           : change.Value <= 10.0);
                    if (!metricPassed)
                    {
                        performancePassed = false;
                        failures.Add(
                            $"p95:{workload.Id}:{mode}:c{concurrency}:" +
                            (change.HasValue
                                ? $"{change.Value:0.###}%"
                                : "baseline-zero-increase"));
                    }
                    workloadAnalyses.Add(new BenchmarkWorkloadAnalysis
                    {
                        WorkloadId = workload.Id,
                        Mode = mode,
                        Concurrency = concurrency,
                        Core = workload.Core,
                        BaselineSamples = baseline.Length,
                        CandidateSamples = candidate.Length,
                        BaselineP95Milliseconds = baselineP95,
                        CandidateP95Milliseconds = candidateP95,
                        ChangePercent = change,
                        Passed = samplePass && metricPassed,
                    });
                }
            }
        }

        var resourceAnalyses = new[] { "cold", "warm" }
            .Select(mode => AnalyzeResourcesForMode(mode, blocks, failures))
            .ToArray();
        var resourcesPassed = resourceAnalyses.All(static analysis => analysis.Passed);

        var passed = correctnessPassed
                     && sampleCountsPassed
                     && performancePassed
                     && resourcesPassed
                     && failures.Count == 0;
        return new BenchmarkAnalysisReport
        {
            AnalyzedAtUtc = DateTimeOffset.UtcNow,
            CorrectnessPassed = correctnessPassed,
            SampleCountsPassed = sampleCountsPassed,
            PerformancePassed = performancePassed,
            ResourcesPassed = resourcesPassed,
            Passed = passed,
            Workloads = workloadAnalyses,
            Resources = resourceAnalyses,
            Failures = failures,
        };
    }

    private static ResourceAnalysis AnalyzeResourcesForMode(
        string mode,
        IReadOnlyList<BenchmarkBlockReport> blocks,
        ICollection<string> failures)
    {
        var modeBlocks = blocks
            .Where(block => string.Equals(block.Mode, mode, StringComparison.Ordinal))
            .OrderBy(static block => block.Sequence)
            .ToArray();
        var baseline = modeBlocks
            .Where(static block => block.Variant == "baseline")
            .ToArray();
        var candidate = modeBlocks
            .Where(static block => block.Variant == "candidate")
            .ToArray();
        var passed = true;
        var blockSamples = modeBlocks
            .Select(block => new
            {
                Block = block,
                Samples = GetOverlappingSamples(block),
            })
            .ToArray();

        if (baseline.Length == 0 || candidate.Length == 0)
        {
            passed = false;
            failures.Add($"postgres-container-resource-samples-missing:{mode}");
        }

        foreach (var item in blockSamples)
        {
            var block = item.Block;
            if (block.SamplerArmedAtUtc == default
                || block.SamplerArmedAtUtc > block.RequestsStartedAtUtc)
            {
                passed = false;
                failures.Add($"postgres-sampler-not-armed:{mode}:{block.Sequence}");
            }
            if (block.RequestsStartedAtUtc == default
                || block.RequestsCompletedAtUtc < block.RequestsStartedAtUtc)
            {
                passed = false;
                failures.Add($"postgres-request-window-invalid:{mode}:{block.Sequence}");
            }
            if (block.HttpRequestsCompletedAtUtc < block.RequestsStartedAtUtc
                || block.HttpRequestsCompletedAtUtc > block.RequestsCompletedAtUtc)
            {
                passed = false;
                failures.Add($"http-request-window-invalid:{mode}:{block.Sequence}");
            }
            if (item.Samples.Length == 0)
            {
                passed = false;
                failures.Add(
                    $"postgres-container-resource-samples-nonoverlapping:{mode}:{block.Sequence}");
            }
            if (block.DatabaseStart.StatsResetAtUtc != block.DatabaseEnd.StatsResetAtUtc)
            {
                passed = false;
                failures.Add($"postgres-stats-reset:{mode}:{block.Sequence}");
            }
            if (block.DatabaseEnd.BlocksRead < block.DatabaseStart.BlocksRead
                || block.DatabaseEnd.TempBytes < block.DatabaseStart.TempBytes
                || block.DatabaseEnd.TempFiles < block.DatabaseStart.TempFiles)
            {
                passed = false;
                failures.Add($"postgres-counter-regressed:{mode}:{block.Sequence}");
            }
        }

        var baselineCpu = RolloutStatistics.Percentile(
            blockSamples
                .Where(static item => item.Block.Variant == "baseline")
                .SelectMany(static item => item.Samples)
                .Select(static sample => sample.CpuPercent),
            0.95);
        var candidateCpu = RolloutStatistics.Percentile(
            blockSamples
                .Where(static item => item.Block.Variant == "candidate")
                .SelectMany(static item => item.Samples)
                .Select(static sample => sample.CpuPercent),
            0.95);
        var baselineMemory = RolloutStatistics.Percentile(
            blockSamples
                .Where(static item => item.Block.Variant == "baseline")
                .SelectMany(static item => item.Samples)
                .Select(static sample => (double)sample.MemoryCurrentBytes),
            0.95);
        var candidateMemory = RolloutStatistics.Percentile(
            blockSamples
                .Where(static item => item.Block.Variant == "candidate")
                .SelectMany(static item => item.Samples)
                .Select(static sample => (double)sample.MemoryCurrentBytes),
            0.95);
        var baselineBlocksRead = PerRequest(
            baseline,
            static block => Math.Max(
                0,
                block.DatabaseEnd.BlocksRead - block.DatabaseStart.BlocksRead));
        var candidateBlocksRead = PerRequest(
            candidate,
            static block => Math.Max(
                0,
                block.DatabaseEnd.BlocksRead - block.DatabaseStart.BlocksRead));
        var baselineTempBytes = PerRequest(
            baseline,
            static block => Math.Max(
                0,
                block.DatabaseEnd.TempBytes - block.DatabaseStart.TempBytes));
        var candidateTempBytes = PerRequest(
            candidate,
            static block => Math.Max(
                0,
                block.DatabaseEnd.TempBytes - block.DatabaseStart.TempBytes));
        var baselineTempFiles = PerRequest(
            baseline,
            static block => Math.Max(
                0,
                block.DatabaseEnd.TempFiles - block.DatabaseStart.TempFiles));
        var candidateTempFiles = PerRequest(
            candidate,
            static block => Math.Max(
                0,
                block.DatabaseEnd.TempFiles - block.DatabaseStart.TempFiles));

        var cpuChange = CalculateResourceChange(baselineCpu, candidateCpu);
        var memoryChange = CalculateResourceChange(baselineMemory, candidateMemory);
        var blocksReadChange = CalculateResourceChange(
            baselineBlocksRead,
            candidateBlocksRead);
        var tempBytesChange = CalculateResourceChange(
            baselineTempBytes,
            candidateTempBytes);
        var tempFilesChange = CalculateResourceChange(
            baselineTempFiles,
            candidateTempFiles);
        foreach (var (name, change) in new[]
                 {
                     ("postgres-cpu-p95", cpuChange),
                     ("postgres-memory-current-p95", memoryChange),
                     ("postgres-blocks-read-per-request", blocksReadChange),
                     ("postgres-temp-bytes-per-request", tempBytesChange),
                     ("postgres-temp-files-per-request", tempFilesChange),
                 })
        {
            if (change.Passed)
                continue;
            passed = false;
            failures.Add(
                $"{name}:{mode}:" +
                (change.BaselineZero
                    ? "baseline-zero-increase"
                    : $"{change.Percent!.Value:0.###}%"));
        }

        return new ResourceAnalysis
        {
            Mode = mode,
            Passed = passed,
            BlockCount = modeBlocks.Length,
            BlocksWithOverlappingSamples = blockSamples.Count(static item =>
                item.Samples.Length > 0),
            BaselineCpuP95 = baselineCpu,
            CandidateCpuP95 = candidateCpu,
            CpuBaselineZero = cpuChange.BaselineZero,
            CpuChangePercent = cpuChange.Percent,
            BaselineMemoryP95Bytes = baselineMemory,
            CandidateMemoryP95Bytes = candidateMemory,
            MemoryBaselineZero = memoryChange.BaselineZero,
            MemoryChangePercent = memoryChange.Percent,
            BaselineBlocksReadPerRequest = baselineBlocksRead,
            CandidateBlocksReadPerRequest = candidateBlocksRead,
            BlocksReadBaselineZero = blocksReadChange.BaselineZero,
            BlocksReadChangePercent = blocksReadChange.Percent,
            BaselineTempBytesPerRequest = baselineTempBytes,
            CandidateTempBytesPerRequest = candidateTempBytes,
            TempBytesBaselineZero = tempBytesChange.BaselineZero,
            TempBytesChangePercent = tempBytesChange.Percent,
            BaselineTempFilesPerRequest = baselineTempFiles,
            CandidateTempFilesPerRequest = candidateTempFiles,
            TempFilesBaselineZero = tempFilesChange.BaselineZero,
            TempFilesChangePercent = tempFilesChange.Percent,
        };
    }

    private static ResourceChange CalculateResourceChange(
        double baseline,
        double candidate)
    {
        var baselineZero = baseline == 0;
        var percent = RolloutStatistics.ChangePercent(baseline, candidate);
        return new ResourceChange(
            baselineZero,
            percent,
            baselineZero ? candidate == 0 : percent is <= 10.0);
    }

    private static ContainerResourceSample[] GetOverlappingSamples(
        BenchmarkBlockReport block) =>
        block.PostgresContainerSamples
            .Where(sample =>
                sample.ObservedAtUtc >= block.RequestsStartedAtUtc
                && sample.ObservedAtUtc <= block.RequestsCompletedAtUtc)
            .OrderBy(static sample => sample.ObservedAtUtc)
            .ToArray();

    private static double PerRequest(
        IReadOnlyCollection<BenchmarkBlockReport> blocks,
        Func<BenchmarkBlockReport, long> selector)
    {
        var requests = blocks.Sum(static block => block.CompletedCount);
        if (requests == 0)
            return 0;
        return blocks.Sum(selector) / (double)requests;
    }

    private readonly record struct ResourceChange(
        bool BaselineZero,
        double? Percent,
        bool Passed);
}
