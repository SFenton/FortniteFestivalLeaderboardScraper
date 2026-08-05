using FSTService.Persistence;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace FstStoredRankRollout;

public sealed class ParityRunner
{
    private readonly NpgsqlDataSource _dataSource;

    public ParityRunner(NpgsqlDataSource dataSource)
    {
        _dataSource = dataSource;
    }

    public async Task<ParityReport> RunAsync(
        RolloutManifest manifest,
        CancellationToken cancellationToken)
    {
        var startedAt = DateTimeOffset.UtcNow;
        var databaseAttestation = ReadOnlyPostgres.CompareDatabaseIdentity(
            manifest,
            await ReadOnlyPostgres.ReadDatabaseIdentityAsync(
                _dataSource,
                cancellationToken));
        if (!databaseAttestation.Passed)
        {
            throw new InvalidOperationException(
                $"Parity database identity failed: {string.Join(", ", databaseAttestation.Failures)}");
        }
        var preflight = await ReadOnlyPostgres.ReadPreflightAsync(
            _dataSource,
            manifest.PublishedScrapeId,
            manifest,
            cancellationToken);
        if (!preflight.Passed)
        {
            throw new InvalidOperationException(
                $"Parity preflight failed: {string.Join(", ", preflight.Failures)}");
        }
        var manifestGenerator = new ManifestGenerator(_dataSource);
        var initialGuard = await manifestGenerator.ValidateGuardAsync(
            manifest,
            cancellationToken);
        if (!initialGuard.Passed)
        {
            throw new InvalidOperationException(
                $"Parity manifest guard failed: {string.Join(", ", initialGuard.Failures)}");
        }

        var results = new List<ParityCaseResult>();
        foreach (var parityCase in manifest.RowCases)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var differences = new List<ParityDifference>();
            if (parityCase.RawMaxScore is > 0 && parityCase.LeewayTenths.HasValue)
            {
                var exactThreshold = DeterministicRollout.CalculateThreshold(
                    parityCase.RawMaxScore.Value,
                    parityCase.LeewayTenths.Value);
                if (exactThreshold != parityCase.MaxScore)
                {
                    differences.Add(new ParityDifference
                    {
                        Surface = "manifest",
                        Key = parityCase.Id,
                        Field = "csharpThreshold",
                        Baseline = exactThreshold.ToString(),
                        Candidate = parityCase.MaxScore.ToString(),
                    });
                }
            }

            using var baselineDatabase = CreateDatabase(parityCase.Instrument, useStoredRanks: false);
            using var candidateDatabase = CreateDatabase(parityCase.Instrument, useStoredRanks: true);
            var baseline = baselineDatabase.GetCurrentStateLeaderboardWithCount(
                parityCase.SongId,
                parityCase.Top,
                parityCase.Offset,
                parityCase.MaxScore);
            var candidate = candidateDatabase.GetCurrentStateLeaderboardWithCount(
                parityCase.SongId,
                parityCase.Top,
                parityCase.Offset,
                parityCase.MaxScore);
            var baselineRows = baseline.Entries.Select(ToComparable).ToArray();
            var candidateRows = candidate.Entries.Select(ToComparable).ToArray();
            differences.AddRange(ParityComparison.CompareLeaderboard(
                baseline.TotalCount,
                baselineRows,
                candidate.TotalCount,
                candidateRows));
            differences.AddRange(ParityComparison.EvaluateExpectedEvidence(
                parityCase,
                baseline.TotalCount,
                baselineRows,
                candidate.TotalCount,
                candidateRows));

            PageBoundaryExecutionEvidence? pageBoundary = null;
            if (parityCase.Tags.Contains("rank-page-boundary", StringComparer.Ordinal))
            {
                var evaluation = ParityComparison.EvaluatePageBoundary(
                    parityCase,
                    baseline.TotalCount,
                    baselineRows,
                    candidate.TotalCount,
                    candidateRows);
                pageBoundary = evaluation.Evidence;
                differences.AddRange(evaluation.Differences);
            }

            foreach (var accountId in parityCase.AccountIds
                         .Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var thresholds = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
                {
                    [parityCase.SongId] = parityCase.MaxScore,
                };
                var baselineRankings = baselineDatabase.GetCurrentStatePlayerRankingsFiltered(
                    accountId,
                    thresholds,
                    parityCase.SongId);
                var candidateRankings = candidateDatabase.GetCurrentStatePlayerRankingsFiltered(
                    accountId,
                    thresholds,
                    parityCase.SongId);
                differences.AddRange(ParityComparison.CompareRankings(
                    accountId,
                    baselineRankings,
                    candidateRankings));
            }

            results.Add(new ParityCaseResult
            {
                CaseId = parityCase.Id,
                SongId = parityCase.SongId,
                Instrument = parityCase.Instrument,
                BaselineTotalCount = baseline.TotalCount,
                CandidateTotalCount = candidate.TotalCount,
                BaselineRowCount = baseline.Entries.Count,
                CandidateRowCount = candidate.Entries.Count,
                AccountCount = parityCase.AccountIds.Count,
                PageBoundary = pageBoundary,
                Differences = differences,
            });
        }

        var endingGuard = await manifestGenerator.ValidateGuardAsync(
            manifest,
            cancellationToken);
        if (!endingGuard.Passed)
        {
            throw new InvalidOperationException(
                $"Parity ending manifest guard failed: {string.Join(", ", endingGuard.Failures)}");
        }

        var pageBoundaries = results
            .Select(static result => result.PageBoundary)
            .Where(static evidence => evidence is not null)
            .Cast<PageBoundaryExecutionEvidence>()
            .OrderBy(static evidence => evidence.Offset)
            .ThenBy(static evidence => evidence.CaseId, StringComparer.Ordinal)
            .ToArray();
        var pageBoundariesPassed = new[] { 99, 100 }.All(offset =>
            pageBoundaries.Any(evidence => evidence.Offset == offset && evidence.Passed));
        var report = new ParityReport
        {
            StartedAtUtc = startedAt,
            CompletedAtUtc = DateTimeOffset.UtcNow,
            PublishedScrapeId = manifest.PublishedScrapeId,
            ManifestFingerprint = manifest.SelectionFingerprint,
            InitialGuardFingerprint = initialGuard.ObservedGuardFingerprint,
            EndingGuardFingerprint = endingGuard.ObservedGuardFingerprint,
            CaseCount = results.Count,
            DifferenceCount = results.Sum(static result => result.Differences.Count),
            PageBoundariesPassed = pageBoundariesPassed,
            Cases = results,
            PageBoundaries = pageBoundaries,
        };
        report.Passed = report.DifferenceCount == 0
                        && manifest.Coverage.PromotionReady
                        && report.PageBoundariesPassed;
        return report;
    }

    private InstrumentDatabase CreateDatabase(string instrument, bool useStoredRanks) =>
        new(
            instrument,
            _dataSource,
            NullLogger<InstrumentDatabase>.Instance)
        {
            UsePublishedScopeSources = true,
            UseStoredProjectionRanksForFilteredReads = useStoredRanks,
        };

    private static ComparableLeaderboardRow ToComparable(LeaderboardEntryDto entry) =>
        new()
        {
            AccountId = entry.AccountId,
            Score = entry.Score,
            Rank = entry.Rank,
            Accuracy = entry.Accuracy,
            IsFullCombo = entry.IsFullCombo,
            Stars = entry.Stars,
            Season = entry.Season,
            Difficulty = entry.Difficulty,
            Percentile = entry.Percentile,
            EndTime = entry.EndTime,
            ApiRank = entry.ApiRank,
            Source = entry.Source,
        };
}
