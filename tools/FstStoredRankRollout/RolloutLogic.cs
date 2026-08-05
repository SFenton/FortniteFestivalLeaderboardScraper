using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using FSTService.Persistence;
using FSTService.Scraping;

namespace FstStoredRankRollout;

public static class RolloutImagePin
{
    private static readonly Regex ImmutableReferencePattern = new(
        @"^[^@\s]+:[^@/\s]+@sha256:[0-9a-f]{64}$",
        RegexOptions.CultureInvariant);
    private static readonly Regex ImageIdPattern = new(
        @"^sha256:[0-9a-f]{64}$",
        RegexOptions.CultureInvariant);

    public static bool IsValid(string reference, string imageId) =>
        ImmutableReferencePattern.IsMatch(reference)
        && ImageIdPattern.IsMatch(imageId);

    public static bool IsValidImageId(string imageId) =>
        ImageIdPattern.IsMatch(imageId);

    public static void Validate(string reference, string imageId)
    {
        if (!IsValid(reference, imageId))
        {
            throw new InvalidDataException(
                "Service image pin must be tag@sha256:<64 lowercase hex> " +
                "with a resolved sha256 image ID.");
        }
    }
}

public static class RolloutEvidenceMount
{
    public const string RequiredTarget = "/mnt/docker-storage";

    public static void Validate(string target, string source, string fileSystem)
    {
        if (!string.Equals(target, RequiredTarget, StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(source)
            || string.IsNullOrWhiteSpace(fileSystem))
        {
            throw new InvalidDataException(
                "Evidence mount binding must include the exact 4 TB mount target, source, and filesystem.");
        }
    }
}

public static class DeterministicRollout
{
    private static readonly ScopeSourceClass[] RequiredSourceClasses =
    [
        ScopeSourceClass.Current,
        ScopeSourceClass.Reused,
        ScopeSourceClass.Empty,
        ScopeSourceClass.SourceMismatch,
    ];

    private static readonly string[] RequiredApiKinds = ["single", "list", "player", "member"];

    public static IReadOnlyList<ScopeEvidence> SelectScopes(
        IEnumerable<ScopeEvidence> candidates,
        IReadOnlyList<string> requiredInstruments,
        int seed)
    {
        var all = candidates.ToList();
        var selected = new Dictionary<string, ScopeEvidence>(StringComparer.Ordinal);

        foreach (var instrument in requiredInstruments)
        {
            AddFirst(
                all.Where(scope =>
                    string.Equals(scope.Instrument, instrument, StringComparison.Ordinal)
                    && scope.SourceClass is ScopeSourceClass.Current or ScopeSourceClass.Reused
                    && scope.PublishedRowCount > 0),
                selected,
                seed,
                $"instrument:{instrument}");
        }

        foreach (var sourceClass in RequiredSourceClasses)
        {
            AddFirst(
                all.Where(scope => scope.SourceClass == sourceClass),
                selected,
                seed,
                $"source:{sourceClass}");
        }

        AddFirst(
            all.Where(static scope =>
                scope.HasActiveOverlay
                && scope.SourceClass is ScopeSourceClass.Current or ScopeSourceClass.Reused
                && scope.PublishedRowCount > 0),
            selected,
            seed,
            "active-overlay");

        var largestCore = all
            .Where(scope =>
                scope.SourceClass is ScopeSourceClass.Current or ScopeSourceClass.Reused
                && scope.PublishedRowCount > 0
                && scope.RawMaxScore is > 0)
            .OrderByDescending(static scope => scope.PublishedRowCount)
            .ThenBy(
                scope => StableKey(seed, "largest-core", scope.Id),
                StringComparer.Ordinal)
            .FirstOrDefault();
        if (largestCore is not null)
            selected[largestCore.Id] = largestCore;

        return selected.Values
            .OrderBy(static scope => scope.Instrument, StringComparer.Ordinal)
            .ThenBy(static scope => scope.SongId, StringComparer.Ordinal)
            .ToArray();
    }

    public static ScopeEvidence[] StableOrder(
        IEnumerable<ScopeEvidence> scopes,
        int seed,
        string lane) =>
        scopes
            .OrderBy(scope => StableKey(seed, lane, scope.Id), StringComparer.Ordinal)
            .ThenBy(static scope => scope.Id, StringComparer.Ordinal)
            .ToArray();

    public static string StableKey(int seed, string lane, string key)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"{seed}\n{lane}\n{key}"));
        return Convert.ToHexString(bytes);
    }

    public static int CalculateThreshold(int maxScore, int leewayTenths) =>
        LeaderboardRankOffsetCalculator.CalculateThreshold(maxScore, leewayTenths);

    public static int SelectFractionalLeewayTenths(int maxScore, int seed, string key)
    {
        foreach (var leewayTenths in FractionalLeewayTenthsCandidates(maxScore, seed, key))
            return leewayTenths;

        return 1;
    }

    public static IReadOnlyList<int> FractionalLeewayTenthsCandidates(
        int maxScore,
        int seed,
        string key)
    {
        int[] candidates = [-49, -37, -13, -1, 1, 13, 37, 49];
        return candidates
            .OrderBy(value => StableKey(seed, key, value.ToString()), StringComparer.Ordinal)
            .Where(leewayTenths =>
        {
            var raw = maxScore * (1.0 + leewayTenths / 1000.0);
            return Math.Abs(raw - Math.Truncate(raw)) > 0.0000001;
        })
            .ToArray();
    }

    public static CoverageSummary BuildCoverage(
        IReadOnlyList<ScopeEvidence> scopes,
        IReadOnlyList<RowParityCase> rowCases,
        IReadOnlyList<ApiWorkload> apiWorkloads,
        IReadOnlyList<string> requiredInstruments)
    {
        var nonEmptyScopeIds = scopes
            .Where(static scope => scope.PublishedRowCount > 0 && scope.SampleAccounts.Count > 0)
            .Select(static scope => scope.Id)
            .ToHashSet(StringComparer.Ordinal);
        var coveredInstruments = rowCases
            .Where(item => nonEmptyScopeIds.Contains(item.ScopeId))
            .Select(static item => item.Instrument)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var missingInstruments = requiredInstruments
            .Except(coveredInstruments, StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var coveredSourceClasses = scopes
            .Select(static scope => scope.SourceClass.ToString())
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var missingSourceClasses = RequiredSourceClasses
            .Select(static value => value.ToString())
            .Except(coveredSourceClasses, StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var coveredApiKinds = apiWorkloads
            .Select(static item => item.Kind)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var missingApiKinds = RequiredApiKinds
            .Except(coveredApiKinds, StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var hasTie = scopes.Any(static scope => scope.ExactScoreTimeTies.Count > 0);
        var qualifiedOverlayScopes = scopes
            .Where(static scope =>
                scope.SourceClass is ScopeSourceClass.Current or ScopeSourceClass.Reused
                && scope.ProjectionGeneration.HasValue
                && string.Equals(scope.ProjectionStatus, "ready", StringComparison.Ordinal)
                && scope.ProjectionScopeSourceSnapshotId == scope.ProjectionSourceSnapshotId
                && scope.OverlayDerivedRows.Count > 0)
            .ToArray();
        var hasSourceMatchedOverlayRow = qualifiedOverlayScopes.Any(scope =>
            rowCases.Any(item =>
                string.Equals(item.ScopeId, scope.Id, StringComparison.Ordinal)
                && item.Tags.Contains("overlay-derived-row", StringComparer.Ordinal)
                && item.ExpectedRows.Any(expected => scope.OverlayDerivedRows.Any(overlay =>
                    string.Equals(
                        overlay.AccountId,
                        expected.AccountId,
                        StringComparison.OrdinalIgnoreCase)
                    && overlay.Score == expected.Score
                    && overlay.Rank == expected.Rank
                    && string.Equals(
                        overlay.Source,
                        expected.Source,
                        StringComparison.Ordinal)))));
        var hasOverlay = hasSourceMatchedOverlayRow;
        var hasRankPageBoundary99 = rowCases.Any(static item =>
            IsExpectedPageBoundaryCase(item, 99));
        var hasRankPageBoundary100 = rowCases.Any(static item =>
            IsExpectedPageBoundaryCase(item, 100));
        var hasRankPageBoundary = hasRankPageBoundary99 && hasRankPageBoundary100;
        var hasThresholdEdges = scopes.Any(scope =>
            HasActualThresholdBoundary(scope.ThresholdBoundary)
            && HasThresholdBoundaryCases(scope, rowCases));
        var hasFractionalTruncation = rowCases.Any(item =>
        {
            if (item.RawMaxScore is not > 0 || item.LeewayTenths is null)
                return false;
            var raw = item.RawMaxScore.Value * (1.0 + item.LeewayTenths.Value / 1000.0);
            return Math.Abs(raw - Math.Truncate(raw)) > 0.0000001
                   && item.MaxScore == Math.Truncate(raw);
        });

        var missing = new List<string>();
        missing.AddRange(missingInstruments.Select(static value => $"instrument:{value}"));
        missing.AddRange(missingSourceClasses.Select(static value => $"source:{value}"));
        missing.AddRange(missingApiKinds.Select(static value => $"api:{value}"));
        if (!hasTie) missing.Add("exact-score-time-tie");
        if (!hasOverlay) missing.Add("source-matched-active-overlay-row");
        if (!hasRankPageBoundary99) missing.Add("rank-page-boundary:offset-99");
        if (!hasRankPageBoundary100) missing.Add("rank-page-boundary:offset-100");
        if (!hasThresholdEdges) missing.Add("threshold-minus-one-exact-plus-one");
        if (!hasFractionalTruncation) missing.Add("fractional-csharp-threshold-truncation");
        if (!apiWorkloads.Any(static workload => workload.Core && workload.Kind == "single"))
            missing.Add("core-filtered-top-workload");
        if (!apiWorkloads.Any(static workload =>
                workload.Core
                && workload.Kind == "member"
                && workload.AccountIds.Count == 1
                && workload.Tags.Contains("single-account", StringComparer.Ordinal)))
            missing.Add("core-filtered-player-workload");

        return new CoverageSummary
        {
            CoveredInstruments = coveredInstruments,
            MissingInstruments = missingInstruments,
            CoveredSourceClasses = coveredSourceClasses,
            MissingSourceClasses = missingSourceClasses,
            CoveredApiKinds = coveredApiKinds,
            MissingApiKinds = missingApiKinds,
            HasExactScoreTimeTie = hasTie,
            HasActiveOverlay = hasOverlay,
            HasSourceMatchedOverlayRow = hasSourceMatchedOverlayRow,
            HasRankPageBoundary99 = hasRankPageBoundary99,
            HasRankPageBoundary100 = hasRankPageBoundary100,
            HasRankPageBoundary = hasRankPageBoundary,
            HasThresholdEdges = hasThresholdEdges,
            HasFractionalThresholdTruncation = hasFractionalTruncation,
            PromotionReady = missing.Count == 0,
            MissingRequirements = missing,
        };
    }

    public static bool IsExpectedPageBoundaryCase(RowParityCase item, int offset) =>
        item.Offset == offset
        && item.Top is > 0
        && item.ExpectedFirstRank == offset + 1
        && item.MinimumExpectedRows > 0
        && item.Tags.Contains("rank-page-boundary", StringComparer.Ordinal);

    private static bool HasActualThresholdBoundary(ThresholdBoundaryEvidence? boundary) =>
        boundary is not null
        && boundary.ExactTotalCount > boundary.BelowTotalCount
        && boundary.PlusTotalCount > boundary.ExactTotalCount
        && boundary.ExactAddedRows.Count > 0
        && boundary.PlusAddedRows.Count > 0
        && boundary.ExactAddedRows.All(row => row.Score == boundary.Threshold)
        && boundary.PlusAddedRows.All(row => row.Score == boundary.Threshold + 1);

    private static bool HasThresholdBoundaryCases(
        ScopeEvidence scope,
        IReadOnlyList<RowParityCase> rowCases)
    {
        var boundary = scope.ThresholdBoundary;
        if (!HasActualThresholdBoundary(boundary) || boundary is null)
            return false;
        var cases = rowCases
            .Where(item => string.Equals(item.ScopeId, scope.Id, StringComparison.Ordinal))
            .ToArray();
        var minus = cases.SingleOrDefault(item =>
            item.Tags.Contains("threshold-minus-one", StringComparer.Ordinal));
        var exact = cases.SingleOrDefault(item =>
            item.Tags.Contains("threshold-exact", StringComparer.Ordinal));
        var plus = cases.SingleOrDefault(item =>
            item.Tags.Contains("threshold-plus-one", StringComparer.Ordinal));
        return minus is not null
               && exact is not null
               && plus is not null
               && minus.MaxScore == boundary.Threshold - 1
               && exact.MaxScore == boundary.Threshold
               && plus.MaxScore == boundary.Threshold + 1
               && minus.ExpectedTotalCount == boundary.BelowTotalCount
               && exact.ExpectedTotalCount == boundary.ExactTotalCount
               && plus.ExpectedTotalCount == boundary.PlusTotalCount
               && boundary.ExactAddedRows.All(expected =>
                   minus.ExpectedAbsentAccountIds.Contains(
                       expected.AccountId,
                       StringComparer.OrdinalIgnoreCase))
               && boundary.PlusAddedRows.All(expected =>
                   minus.ExpectedAbsentAccountIds.Contains(
                       expected.AccountId,
                       StringComparer.OrdinalIgnoreCase)
                   && exact.ExpectedAbsentAccountIds.Contains(
                       expected.AccountId,
                       StringComparer.OrdinalIgnoreCase))
               && boundary.ExactAddedRows.All(expected =>
                   exact.ExpectedRows.Any(row => ExpectedRowsMatch(row, expected)))
               && boundary.PlusAddedRows.All(expected =>
                   plus.ExpectedRows.Any(row => ExpectedRowsMatch(row, expected)));
    }

    private static bool ExpectedRowsMatch(
        ExpectedLeaderboardRow left,
        ExpectedLeaderboardRow right) =>
        string.Equals(left.AccountId, right.AccountId, StringComparison.OrdinalIgnoreCase)
        && left.Score == right.Score
        && left.Rank == right.Rank
        && string.Equals(left.Source, right.Source, StringComparison.Ordinal);

    public static string ComputeManifestFingerprint(RolloutManifest manifest)
    {
        var canonical = new
        {
            manifest.SchemaVersion,
            manifest.Seed,
            manifest.PublishedScrapeId,
            manifest.PublicReadsFrozen,
            manifest.ServiceImageReference,
            manifest.ServiceImageId,
            manifest.WorkerContainerId,
            manifest.WorkerImageReference,
            manifest.WorkerImageId,
            manifest.WorkerContainerStatus,
            manifest.WorkerContainerState,
            manifest.DatabaseIdentity,
            manifest.ServiceDatabaseTarget,
            manifest.PostgresContainerId,
            manifest.PostgresImageReference,
            manifest.PostgresImageId,
            postgresNetworkNames = manifest.PostgresNetworkNames
                .Order(StringComparer.Ordinal),
            postgresNetworkAliases = manifest.PostgresNetworkAliases
                .Order(StringComparer.Ordinal),
            postgresServerAddresses = manifest.PostgresServerAddresses
                .Order(StringComparer.Ordinal),
            postgresNetworkBindings = manifest.PostgresNetworkBindings
                .OrderBy(static binding => binding.NetworkName, StringComparer.Ordinal)
                .Select(static binding => new
                {
                    binding.NetworkName,
                    binding.NetworkId,
                    binding.ServiceAlias,
                    binding.ExclusiveOwnerContainerId,
                    serverAddresses = binding.ServerAddresses
                        .Order(StringComparer.Ordinal),
                }),
            manifest.EvidenceMountTarget,
            manifest.EvidenceMountSource,
            manifest.EvidenceMountFileSystem,
            manifest.SelectionGuardFingerprint,
            requiredInstruments = manifest.RequiredInstruments.Order(StringComparer.Ordinal),
            scopes = manifest.Scopes
                .OrderBy(static scope => scope.Id, StringComparer.Ordinal)
                .Select(scope => new
                {
                    scope.Id,
                    scope.SongId,
                    scope.Instrument,
                    scope.PublishedScrapeId,
                    scope.SourceKind,
                    scope.SourceSnapshotId,
                    scope.SourceScrapeId,
                    scope.ProjectionSourceSnapshotId,
                    scope.PublishedRowCount,
                    scope.ContentFingerprint,
                    scope.CoverageFingerprint,
                    scope.ProjectionGeneration,
                    scope.ProjectionRowCount,
                    scope.ProjectionScopeSourceSnapshotId,
                    scope.ProjectionStatus,
                    scope.SourceClass,
                    scope.HasActiveOverlay,
                    scope.RawMaxScore,
                    overlayRows = scope.OverlayDerivedRows
                        .OrderBy(static row => row.Rank)
                        .ThenBy(static row => row.AccountId, StringComparer.Ordinal),
                    scope.ThresholdBoundary,
                    ties = scope.ExactScoreTimeTies
                        .OrderBy(static tie => tie.Score)
                        .ThenBy(static tie => tie.OrderTime, StringComparer.Ordinal),
                    accounts = scope.SampleAccounts
                        .OrderBy(static account => account.EvidenceKind, StringComparer.Ordinal)
                        .ThenBy(static account => account.Rank)
                        .ThenBy(static account => account.AccountId, StringComparer.Ordinal),
                }),
            rowCases = manifest.RowCases.OrderBy(static item => item.Id, StringComparer.Ordinal),
            apiWorkloads = manifest.ApiWorkloads.OrderBy(static item => item.Id, StringComparer.Ordinal),
            manifest.Coverage,
        };
        var json = JsonSerializer.SerializeToUtf8Bytes(canonical, RolloutJson.Options);
        return Convert.ToHexString(SHA256.HashData(json)).ToLowerInvariant();
    }

    public static IReadOnlyList<BenchmarkScheduleEntry> BuildSchedule(
        RolloutManifest manifest,
        int seed)
    {
        var entries = new List<BenchmarkScheduleEntry>();
        var sequence = 0;
        foreach (var workload in manifest.ApiWorkloads
                     .Where(static item => item.Benchmark)
                     .OrderBy(static item => item.Id, StringComparer.Ordinal))
        {
            foreach (var mode in new[] { "cold", "warm" })
            {
                foreach (var concurrency in new[] { 1, 8 })
                {
                    var blockCount = workload.Core
                        ? mode == "cold"
                            ? concurrency == 1 ? 15 : 2
                            : 2
                        : mode == "cold"
                            ? concurrency == 1 ? 3 : 1
                            : 1;
                    var requestCount = workload.Core
                        ? mode == "cold"
                            ? concurrency
                            : 50
                        : mode == "cold"
                            ? concurrency
                            : 25;

                    for (var block = 0; block < blockCount; block++)
                    {
                        var key = StableKey(seed, $"{workload.Id}:{mode}:{concurrency}", block.ToString());
                        var baselineFirst = (Convert.ToInt32(key[..2], 16) & 1) == 0;
                        var order = baselineFirst
                            ? new[] { "baseline", "candidate", "candidate", "baseline" }
                            : new[] { "candidate", "baseline", "baseline", "candidate" };
                        for (var position = 0; position < order.Length; position++)
                        {
                            entries.Add(new BenchmarkScheduleEntry
                            {
                                Sequence = ++sequence,
                                Mode = mode,
                                Concurrency = concurrency,
                                WorkloadId = workload.Id,
                                AbbaBlock = block + 1,
                                Position = position + 1,
                                Variant = order[position],
                                RequestCount = requestCount,
                            });
                        }
                    }
                }
            }
        }

        return entries;
    }

    private static void AddFirst(
        IEnumerable<ScopeEvidence> candidates,
        IDictionary<string, ScopeEvidence> selected,
        int seed,
        string lane)
    {
        var first = StableOrder(candidates, seed, lane).FirstOrDefault();
        if (first is not null)
            selected[first.Id] = first;
    }
}

public static class ParityComparison
{
    public static IReadOnlyList<ParityDifference> CompareLeaderboard(
        int baselineTotal,
        IReadOnlyList<ComparableLeaderboardRow> baseline,
        int candidateTotal,
        IReadOnlyList<ComparableLeaderboardRow> candidate)
    {
        var differences = new List<ParityDifference>();
        if (baselineTotal != candidateTotal)
        {
            differences.Add(new ParityDifference
            {
                Surface = "leaderboard",
                Key = "totalCount",
                Field = "totalCount",
                Baseline = baselineTotal.ToString(),
                Candidate = candidateTotal.ToString(),
            });
        }

        if (baseline.Count != candidate.Count)
        {
            differences.Add(new ParityDifference
            {
                Surface = "leaderboard",
                Key = "rowCount",
                Field = "rowCount",
                Baseline = baseline.Count.ToString(),
                Candidate = candidate.Count.ToString(),
            });
        }

        var count = Math.Max(baseline.Count, candidate.Count);
        for (var index = 0; index < count; index++)
        {
            if (index >= baseline.Count || index >= candidate.Count)
            {
                differences.Add(new ParityDifference
                {
                    Surface = "leaderboard",
                    Key = $"row:{index}",
                    Field = "presence",
                    Baseline = index < baseline.Count ? baseline[index].AccountId : null,
                    Candidate = index < candidate.Count ? candidate[index].AccountId : null,
                });
                continue;
            }

            CompareRow(index, baseline[index], candidate[index], differences);
        }

        return differences;
    }

    public static IReadOnlyList<ParityDifference> CompareRankings(
        string accountId,
        IReadOnlyDictionary<string, int> baseline,
        IReadOnlyDictionary<string, int> candidate)
    {
        var differences = new List<ParityDifference>();
        foreach (var songId in baseline.Keys
                     .Concat(candidate.Keys)
                     .Distinct(StringComparer.OrdinalIgnoreCase)
                     .Order(StringComparer.Ordinal))
        {
            var hasBaseline = baseline.TryGetValue(songId, out var baselineRank);
            var hasCandidate = candidate.TryGetValue(songId, out var candidateRank);
            if (hasBaseline == hasCandidate && (!hasBaseline || baselineRank == candidateRank))
                continue;
            differences.Add(new ParityDifference
            {
                Surface = "player",
                Key = $"{accountId}:{songId}",
                Field = "rank",
                Baseline = hasBaseline ? baselineRank.ToString() : null,
                Candidate = hasCandidate ? candidateRank.ToString() : null,
            });
        }

        return differences;
    }

    public static (
        PageBoundaryExecutionEvidence Evidence,
        IReadOnlyList<ParityDifference> Differences) EvaluatePageBoundary(
        RowParityCase parityCase,
        int baselineTotal,
        IReadOnlyList<ComparableLeaderboardRow> baseline,
        int candidateTotal,
        IReadOnlyList<ComparableLeaderboardRow> candidate)
    {
        var differences = new List<ParityDifference>();
        var expectedFirstRank = parityCase.ExpectedFirstRank;
        if (expectedFirstRank is null || parityCase.MinimumExpectedRows <= 0)
        {
            differences.Add(new ParityDifference
            {
                Surface = "page-boundary",
                Key = parityCase.Id,
                Field = "expectedEvidence",
                Baseline = $"{expectedFirstRank?.ToString() ?? "<null>"}/{parityCase.MinimumExpectedRows}",
                Candidate = "firstRank/minimumRows",
            });
        }

        CompareMinimumRows(
            "baselineRowCount",
            baseline.Count,
            parityCase.MinimumExpectedRows,
            parityCase,
            differences);
        CompareMinimumRows(
            "candidateRowCount",
            candidate.Count,
            parityCase.MinimumExpectedRows,
            parityCase,
            differences);
        ComparePageTotal(
            "baselineTotalCount",
            baselineTotal,
            parityCase,
            differences);
        ComparePageTotal(
            "candidateTotalCount",
            candidateTotal,
            parityCase,
            differences);
        CompareFirstRank(
            "baselineFirstRank",
            baseline.FirstOrDefault()?.Rank,
            expectedFirstRank,
            parityCase,
            differences);
        CompareFirstRank(
            "candidateFirstRank",
            candidate.FirstOrDefault()?.Rank,
            expectedFirstRank,
            parityCase,
            differences);

        return (
            new PageBoundaryExecutionEvidence
            {
                CaseId = parityCase.Id,
                Offset = parityCase.Offset,
                ExpectedFirstRank = expectedFirstRank ?? 0,
                MinimumExpectedRows = parityCase.MinimumExpectedRows,
                BaselineTotalCount = baselineTotal,
                CandidateTotalCount = candidateTotal,
                BaselineRowCount = baseline.Count,
                CandidateRowCount = candidate.Count,
                BaselineFirstRank = baseline.FirstOrDefault()?.Rank,
                CandidateFirstRank = candidate.FirstOrDefault()?.Rank,
                Passed = differences.Count == 0,
            },
            differences);
    }

    public static IReadOnlyList<ParityDifference> EvaluateExpectedEvidence(
        RowParityCase parityCase,
        int baselineTotal,
        IReadOnlyList<ComparableLeaderboardRow> baseline,
        int candidateTotal,
        IReadOnlyList<ComparableLeaderboardRow> candidate)
    {
        var differences = new List<ParityDifference>();
        EvaluateExpectedEvidenceForVariant(
            "baseline",
            parityCase,
            baselineTotal,
            baseline,
            differences);
        EvaluateExpectedEvidenceForVariant(
            "candidate",
            parityCase,
            candidateTotal,
            candidate,
            differences);
        return differences;
    }

    private static void EvaluateExpectedEvidenceForVariant(
        string variant,
        RowParityCase parityCase,
        int totalCount,
        IReadOnlyList<ComparableLeaderboardRow> rows,
        ICollection<ParityDifference> differences)
    {
        if (parityCase.ExpectedTotalCount.HasValue
            && totalCount != parityCase.ExpectedTotalCount.Value)
        {
            differences.Add(new ParityDifference
            {
                Surface = "expected-evidence",
                Key = parityCase.Id,
                Field = $"{variant}TotalCount",
                Baseline = parityCase.ExpectedTotalCount.Value.ToString(),
                Candidate = totalCount.ToString(),
            });
        }

        foreach (var expected in parityCase.ExpectedRows)
        {
            var actual = rows.FirstOrDefault(row => string.Equals(
                row.AccountId,
                expected.AccountId,
                StringComparison.OrdinalIgnoreCase));
            if (actual is null)
            {
                differences.Add(new ParityDifference
                {
                    Surface = "expected-evidence",
                    Key = parityCase.Id,
                    Field = $"{variant}ExpectedRowPresence",
                    Baseline = expected.AccountId,
                    Candidate = null,
                });
                continue;
            }
            CompareExpectedField(
                parityCase.Id,
                variant,
                expected.AccountId,
                "score",
                expected.Score,
                actual.Score,
                differences);
            CompareExpectedField(
                parityCase.Id,
                variant,
                expected.AccountId,
                "rank",
                expected.Rank,
                actual.Rank,
                differences);
            CompareExpectedField(
                parityCase.Id,
                variant,
                expected.AccountId,
                "source",
                expected.Source,
                actual.Source,
                differences);
        }

        foreach (var accountId in parityCase.ExpectedAbsentAccountIds)
        {
            if (!rows.Any(row => string.Equals(
                    row.AccountId,
                    accountId,
                    StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }
            differences.Add(new ParityDifference
            {
                Surface = "expected-evidence",
                Key = parityCase.Id,
                Field = $"{variant}ExpectedAbsence",
                Baseline = null,
                Candidate = accountId,
            });
        }
    }

    private static void CompareExpectedField<T>(
        string caseId,
        string variant,
        string accountId,
        string field,
        T expected,
        T actual,
        ICollection<ParityDifference> differences)
    {
        if (EqualityComparer<T>.Default.Equals(expected, actual))
            return;
        differences.Add(new ParityDifference
        {
            Surface = "expected-evidence",
            Key = $"{caseId}:{accountId}",
            Field = $"{variant}{char.ToUpperInvariant(field[0])}{field[1..]}",
            Baseline = expected?.ToString(),
            Candidate = actual?.ToString(),
        });
    }

    private static void CompareMinimumRows(
        string field,
        int actual,
        int minimum,
        RowParityCase parityCase,
        ICollection<ParityDifference> differences)
    {
        if (minimum > 0 && actual >= minimum)
            return;
        differences.Add(new ParityDifference
        {
            Surface = "page-boundary",
            Key = parityCase.Id,
            Field = field,
            Baseline = actual.ToString(),
            Candidate = $"minimum:{minimum}",
        });
    }

    private static void ComparePageTotal(
        string field,
        int actual,
        RowParityCase parityCase,
        ICollection<ParityDifference> differences)
    {
        if (actual > parityCase.Offset)
            return;
        differences.Add(new ParityDifference
        {
            Surface = "page-boundary",
            Key = parityCase.Id,
            Field = field,
            Baseline = actual.ToString(),
            Candidate = $"greater-than-offset:{parityCase.Offset}",
        });
    }

    private static void CompareFirstRank(
        string field,
        int? actual,
        int? expected,
        RowParityCase parityCase,
        ICollection<ParityDifference> differences)
    {
        if (expected.HasValue && actual == expected)
            return;
        differences.Add(new ParityDifference
        {
            Surface = "page-boundary",
            Key = parityCase.Id,
            Field = field,
            Baseline = actual?.ToString(),
            Candidate = expected?.ToString(),
        });
    }

    private static void CompareRow(
        int index,
        ComparableLeaderboardRow baseline,
        ComparableLeaderboardRow candidate,
        ICollection<ParityDifference> differences)
    {
        Compare(index, "accountId", baseline.AccountId, candidate.AccountId, differences);
        Compare(index, "score", baseline.Score, candidate.Score, differences);
        Compare(index, "rank", baseline.Rank, candidate.Rank, differences);
        Compare(index, "accuracy", baseline.Accuracy, candidate.Accuracy, differences);
        Compare(index, "isFullCombo", baseline.IsFullCombo, candidate.IsFullCombo, differences);
        Compare(index, "stars", baseline.Stars, candidate.Stars, differences);
        Compare(index, "season", baseline.Season, candidate.Season, differences);
        Compare(index, "difficulty", baseline.Difficulty, candidate.Difficulty, differences);
        Compare(index, "percentile", baseline.Percentile, candidate.Percentile, differences);
        Compare(index, "endTime", baseline.EndTime, candidate.EndTime, differences);
        Compare(index, "apiRank", baseline.ApiRank, candidate.ApiRank, differences);
        Compare(index, "source", baseline.Source, candidate.Source, differences);
    }

    private static void Compare<T>(
        int index,
        string field,
        T baseline,
        T candidate,
        ICollection<ParityDifference> differences)
    {
        if (EqualityComparer<T>.Default.Equals(baseline, candidate))
            return;
        differences.Add(new ParityDifference
        {
            Surface = "leaderboard",
            Key = $"row:{index}",
            Field = field,
            Baseline = baseline?.ToString(),
            Candidate = candidate?.ToString(),
        });
    }
}

public static class RolloutStatistics
{
    public static double Percentile(IEnumerable<double> values, double percentile)
    {
        var sorted = values.Order().ToArray();
        if (sorted.Length == 0)
            return 0;
        var index = Math.Clamp((int)Math.Ceiling(percentile * sorted.Length) - 1, 0, sorted.Length - 1);
        return sorted[index];
    }

    public static double? ChangePercent(double baseline, double candidate)
    {
        if (baseline == 0)
            return candidate == 0 ? 0 : null;
        return ((candidate / baseline) - 1.0) * 100.0;
    }
}

public static class RolloutAcceptance
{
    public static RolloutAcceptanceReport Finalize(
        RolloutManifest manifest,
        BenchmarkAnalysisReport analysis,
        RollbackVerificationEvidence rollback,
        RollbackVerificationEvidence recovery,
        RollbackVerificationEvidence finalRuntime,
        RolloutPreflightReport finalQuiescence,
        string finalQuiescenceSha256)
    {
        var failures = new List<string>();
        if (!analysis.Passed)
            failures.AddRange(analysis.Failures.Select(static failure => $"analysis:{failure}"));
        if (!string.Equals(rollback.Label, "rollback", StringComparison.Ordinal))
            failures.Add("rollback:label");
        if (!string.Equals(
                rollback.ManifestFingerprint,
                manifest.SelectionFingerprint,
                StringComparison.Ordinal))
        {
            failures.Add("rollback:manifest-fingerprint");
        }
        if (rollback.FstserviceStoredRankFlag || rollback.FstworkerStoredRankFlag)
            failures.Add("rollback:stored-rank-not-false");
        if (!rollback.FstservicePublishedSources || rollback.FstworkerPublishedSources)
            failures.Add("rollback:published-source-role-split");
        if (!rollback.FstserviceReadOnlyStartup || rollback.FstworkerReadOnlyStartup)
            failures.Add("rollback:read-only-startup-role-split");
        if (!rollback.FstservicePostgresReadOnly || rollback.FstworkerPostgresReadOnly)
            failures.Add("rollback:postgres-read-only-role-split");
        if (!rollback.HealthVerified)
            failures.Add("rollback:health");
        if (string.IsNullOrWhiteSpace(rollback.FstserviceContainerId)
            || string.IsNullOrWhiteSpace(rollback.FstworkerContainerId))
        {
            failures.Add("rollback:container-identity");
        }
        if (!string.Equals(
                rollback.FstserviceImageReference,
                manifest.ServiceImageReference,
                StringComparison.Ordinal)
            || !string.Equals(
                rollback.FstserviceImageId,
                manifest.ServiceImageId,
                StringComparison.Ordinal))
        {
            failures.Add("rollback:image-pin");
        }
        if (!string.Equals(
                rollback.FstworkerContainerId,
                manifest.WorkerContainerId,
                StringComparison.Ordinal)
            || !string.Equals(
                rollback.FstworkerImageReference,
                manifest.WorkerImageReference,
                StringComparison.Ordinal)
            || !string.Equals(
                rollback.FstworkerImageId,
                manifest.WorkerImageId,
                StringComparison.Ordinal)
            || !string.Equals(
                rollback.FstworkerContainerStatus,
                manifest.WorkerContainerStatus,
                StringComparison.Ordinal)
            || !string.Equals(
                rollback.FstworkerContainerState,
                manifest.WorkerContainerState,
                StringComparison.Ordinal))
        {
            failures.Add("rollback:worker-runtime-pin");
        }
        ValidateRuntimeDatabaseBinding(
            manifest,
            rollback,
            "rollback",
            expectedReadOnlyOption: true,
            failures);
        if (!string.Equals(recovery.Label, "recovery", StringComparison.Ordinal))
            failures.Add("recovery:label");
        if (!string.Equals(
                recovery.ManifestFingerprint,
                manifest.SelectionFingerprint,
                StringComparison.Ordinal))
        {
            failures.Add("recovery:manifest-fingerprint");
        }
        if (recovery.FstserviceStoredRankFlag || recovery.FstworkerStoredRankFlag)
            failures.Add("recovery:stored-rank-not-false");
        if (!recovery.FstservicePublishedSources || recovery.FstworkerPublishedSources)
            failures.Add("recovery:published-source-role-split");
        if (recovery.FstserviceReadOnlyStartup || recovery.FstworkerReadOnlyStartup)
            failures.Add("recovery:read-only-startup-not-false");
        if (recovery.FstservicePostgresReadOnly || recovery.FstworkerPostgresReadOnly)
            failures.Add("recovery:postgres-read-only-not-false");
        if (!recovery.HealthVerified)
            failures.Add("recovery:health");
        if (string.IsNullOrWhiteSpace(recovery.FstserviceContainerId)
            || string.IsNullOrWhiteSpace(recovery.FstworkerContainerId))
        {
            failures.Add("recovery:container-identity");
        }
        if (!string.Equals(
                recovery.FstserviceImageReference,
                manifest.ServiceImageReference,
                StringComparison.Ordinal)
            || !string.Equals(
                recovery.FstserviceImageId,
                manifest.ServiceImageId,
                StringComparison.Ordinal))
        {
            failures.Add("recovery:image-pin");
        }
        if (!string.Equals(
                recovery.FstworkerContainerId,
                manifest.WorkerContainerId,
                StringComparison.Ordinal)
            || !string.Equals(
                recovery.FstworkerImageReference,
                manifest.WorkerImageReference,
                StringComparison.Ordinal)
            || !string.Equals(
                recovery.FstworkerImageId,
                manifest.WorkerImageId,
                StringComparison.Ordinal)
            || !string.Equals(
                recovery.FstworkerContainerStatus,
                manifest.WorkerContainerStatus,
                StringComparison.Ordinal)
            || !string.Equals(
                recovery.FstworkerContainerState,
                manifest.WorkerContainerState,
                StringComparison.Ordinal))
        {
            failures.Add("recovery:worker-runtime-pin");
        }
        ValidateRuntimeDatabaseBinding(
            manifest,
            recovery,
            "recovery",
            expectedReadOnlyOption: false,
            failures);
        if (!string.Equals(finalRuntime.Label, "final", StringComparison.Ordinal))
            failures.Add("final:label");
        if (!string.Equals(
                finalRuntime.FstserviceContainerId,
                recovery.FstserviceContainerId,
                StringComparison.Ordinal)
            || !string.Equals(
                finalRuntime.FstserviceInstanceNonce,
                recovery.FstserviceInstanceNonce,
                StringComparison.Ordinal)
            || !string.Equals(
                finalRuntime.FstworkerContainerId,
                recovery.FstworkerContainerId,
                StringComparison.Ordinal))
        {
            failures.Add("final:recovery-container-identity");
        }
        if (!string.Equals(
                finalRuntime.ManifestFingerprint,
                manifest.SelectionFingerprint,
                StringComparison.Ordinal))
        {
            failures.Add("final:manifest-fingerprint");
        }
        if (finalRuntime.FstserviceStoredRankFlag
            || finalRuntime.FstworkerStoredRankFlag
            || finalRuntime.FstserviceReadOnlyStartup
            || finalRuntime.FstworkerReadOnlyStartup
            || finalRuntime.FstservicePostgresReadOnly
            || finalRuntime.FstworkerPostgresReadOnly)
        {
            failures.Add("final:normal-role-flags");
        }
        if (!finalRuntime.FstservicePublishedSources
            || finalRuntime.FstworkerPublishedSources
            || !finalRuntime.HealthVerified)
        {
            failures.Add("final:health-or-published-role");
        }
        if (!string.Equals(
                finalRuntime.FstserviceImageReference,
                manifest.ServiceImageReference,
                StringComparison.Ordinal)
            || !string.Equals(
                finalRuntime.FstserviceImageId,
                manifest.ServiceImageId,
                StringComparison.Ordinal))
        {
            failures.Add("final:image-pin");
        }
        if (!string.Equals(
                finalRuntime.FstworkerContainerId,
                manifest.WorkerContainerId,
                StringComparison.Ordinal)
            || !string.Equals(
                finalRuntime.FstworkerImageReference,
                manifest.WorkerImageReference,
                StringComparison.Ordinal)
            || !string.Equals(
                finalRuntime.FstworkerImageId,
                manifest.WorkerImageId,
                StringComparison.Ordinal)
            || !string.Equals(
                finalRuntime.FstworkerContainerStatus,
                manifest.WorkerContainerStatus,
                StringComparison.Ordinal)
            || !string.Equals(
                finalRuntime.FstworkerContainerState,
                manifest.WorkerContainerState,
                StringComparison.Ordinal))
        {
            failures.Add("final:worker-runtime-pin");
        }
        ValidateRuntimeDatabaseBinding(
            manifest,
            finalRuntime,
            "final",
            expectedReadOnlyOption: false,
            failures);
        if (!finalQuiescence.Passed
            || !finalQuiescence.MonitoringPrivilegeAttested
            || !finalQuiescence.CrossRoleVisibilityAttested
            || finalQuiescence.ActiveWorkerConnectionCount != 0
            || finalQuiescence.GrantedMutationLeaseCount != 0
            || finalQuiescence.ActiveDurableJobCount != 0)
        {
            failures.Add("final:db-quiescence");
        }
        if (finalQuiescence.DatabaseAttestation is not { Passed: true } finalDatabase
            || !ReadOnlyPostgres.CompareDatabaseIdentity(
                    manifest,
                    finalDatabase.Observed)
                .Passed)
        {
            failures.Add("final:database-target-attestation");
        }
        if (!Regex.IsMatch(
                finalQuiescenceSha256,
                "^[0-9a-f]{64}$",
                RegexOptions.CultureInvariant))
        {
            failures.Add("final:quiescence-sha256");
        }
        return new RolloutAcceptanceReport
        {
            FinalizedAtUtc = DateTimeOffset.UtcNow,
            ManifestFingerprint = manifest.SelectionFingerprint,
            Analysis = analysis,
            Rollback = rollback,
            Recovery = recovery,
            FinalRuntime = finalRuntime,
            FinalQuiescence = finalQuiescence,
            FinalQuiescenceSha256 = finalQuiescenceSha256,
            Passed = failures.Count == 0,
            Failures = failures,
        };
    }

    private static void ValidateRuntimeDatabaseBinding(
        RolloutManifest manifest,
        RollbackVerificationEvidence evidence,
        string prefix,
        bool expectedReadOnlyOption,
        ICollection<string> failures)
    {
        if (!DatabaseTargetsEqual(
                manifest.ServiceDatabaseTarget,
                evidence.FstserviceDatabaseTarget))
        {
            failures.Add($"{prefix}:service-database-target");
        }
        if (string.IsNullOrWhiteSpace(evidence.FstserviceContainerHostname)
            || !Regex.IsMatch(
                evidence.FstserviceInstanceNonce,
                "^[0-9a-f]{32}$",
                RegexOptions.CultureInvariant)
            || !Uri.TryCreate(
                evidence.FstserviceBaseUrl,
                UriKind.Absolute,
                out var serviceUri)
            || serviceUri.Scheme != Uri.UriSchemeHttp
            || !serviceUri.IsLoopback)
        {
            failures.Add($"{prefix}:service-instance-binding");
        }
        if (evidence.FstserviceDefaultTransactionReadOnlyOption
            != expectedReadOnlyOption)
        {
            failures.Add($"{prefix}:service-connection-read-only-option");
        }
        if (!string.Equals(
                manifest.PostgresContainerId,
                evidence.PostgresContainerId,
                StringComparison.Ordinal)
            || !string.Equals(
                manifest.PostgresImageReference,
                evidence.PostgresImageReference,
                StringComparison.Ordinal)
            || !string.Equals(
                manifest.PostgresImageId,
                evidence.PostgresImageId,
                StringComparison.Ordinal)
            || !SetEquals(
                manifest.PostgresNetworkNames,
                evidence.PostgresNetworkNames)
            || !SetEquals(
                manifest.PostgresNetworkAliases,
                evidence.PostgresNetworkAliases)
            || !SetEquals(
                manifest.PostgresServerAddresses,
                evidence.PostgresServerAddresses)
            || !NetworkBindingsEqual(
                manifest.PostgresNetworkBindings,
                evidence.PostgresNetworkBindings))
        {
            failures.Add($"{prefix}:postgres-runtime-binding");
        }
    }

    private static bool DatabaseTargetsEqual(
        ServiceDatabaseTarget left,
        ServiceDatabaseTarget right) =>
        string.Equals(left.Host, right.Host, StringComparison.Ordinal)
        && left.Port == right.Port
        && string.Equals(left.Database, right.Database, StringComparison.Ordinal)
        && string.Equals(left.Username, right.Username, StringComparison.Ordinal);

    private static bool SetEquals(
        IEnumerable<string> left,
        IEnumerable<string> right) =>
        left.Order(StringComparer.Ordinal)
            .SequenceEqual(right.Order(StringComparer.Ordinal), StringComparer.Ordinal);

    private static bool NetworkBindingsEqual(
        IEnumerable<PostgresNetworkBinding> left,
        IEnumerable<PostgresNetworkBinding> right)
    {
        static string Canonical(PostgresNetworkBinding binding) =>
            string.Join(
                "\u001f",
                binding.NetworkName,
                binding.NetworkId,
                binding.ServiceAlias,
                binding.ExclusiveOwnerContainerId,
                string.Join(
                    "\u001e",
                    binding.ServerAddresses.Order(StringComparer.Ordinal)));

        return left.Select(Canonical)
            .Order(StringComparer.Ordinal)
            .SequenceEqual(
                right.Select(Canonical).Order(StringComparer.Ordinal),
                StringComparer.Ordinal);
    }
}
