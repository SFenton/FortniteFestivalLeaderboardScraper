using FSTService.Persistence;
using Microsoft.Extensions.Logging;

namespace FSTService.Scraping;

public sealed class SoloFamilyRankingBackfillService
{
    private readonly GlobalLeaderboardPersistence _persistence;
    private readonly IMetaDatabase _metaDb;
    private readonly ILogger<SoloFamilyRankingBackfillService> _log;

    public SoloFamilyRankingBackfillService(
        GlobalLeaderboardPersistence persistence,
        IMetaDatabase metaDb,
        ILogger<SoloFamilyRankingBackfillService> log)
    {
        _persistence = persistence;
        _metaDb = metaDb;
        _log = log;
    }

    public SoloFamilyRankingBackfillResult Rebuild(bool execute)
    {
        var perInstrument = new Dictionary<string, Dictionary<string, RankingsCalculator.AccountMetrics>>(StringComparer.OrdinalIgnoreCase);
        var totalChartedByInstrument = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var sourceRowsByInstrument = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var instrument in GlobalLeaderboardScraper.AllInstruments)
        {
            var db = _persistence.GetOrCreateInstrumentDb(instrument);
            var rows = db.GetAllRankingSummariesDetailed();
            var instrumentRows = new Dictionary<string, RankingsCalculator.AccountMetrics>(StringComparer.OrdinalIgnoreCase);
            var totalChartedSongs = 0;

            foreach (var summary in rows)
            {
                instrumentRows[summary.AccountId] = new RankingsCalculator.AccountMetrics(
                    summary.AdjustedSkillRating,
                    summary.WeightedRating,
                    summary.FcRate,
                    summary.TotalScore,
                    summary.MaxScorePercent,
                    summary.SongsPlayed,
                    summary.FullComboCount,
                    summary.TotalChartedSongs,
                    summary.RawSkillRating,
                    summary.RawWeightedRating,
                    summary.RawMaxScorePercent);
                totalChartedSongs = Math.Max(totalChartedSongs, summary.TotalChartedSongs);
            }

            perInstrument[instrument] = instrumentRows;
            totalChartedByInstrument[instrument] = totalChartedSongs;
            sourceRowsByInstrument[instrument] = instrumentRows.Count;
        }

        var rankings = SoloFamilyRankingBuilder.BuildRankings(
            SoloFamilyRankingScopes.All,
            perInstrument,
            totalChartedByInstrument,
            RankingsCalculator.CredibilityThreshold,
            RankingsCalculator.PopulationMedian);

        var scopeRows = rankings
            .GroupBy(row => row.ScopeId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);

        if (execute)
        {
            _metaDb.ReplaceSoloFamilyRankings(rankings);
            _log.LogInformation("Rebuilt {Count:N0} solo family ranking rows across {Scopes:N0} scope(s).", rankings.Count, scopeRows.Count);
        }

        return new SoloFamilyRankingBackfillResult(
            rankings.Count,
            scopeRows,
            sourceRowsByInstrument,
            totalChartedByInstrument,
            execute);
    }
}

public sealed record SoloFamilyRankingBackfillResult(
    int TotalRows,
    IReadOnlyDictionary<string, int> ScopeRows,
    IReadOnlyDictionary<string, int> SourceRowsByInstrument,
    IReadOnlyDictionary<string, int> TotalChartedByInstrument,
    bool Executed);