using System.Diagnostics;
using FSTService.Persistence;
using Npgsql;

namespace FSTService.Scraping;

public sealed class BandRankingRepairService
{
    private const int CredibilityThreshold = 50;
    private const double PopulationMedian = 0.5;

    private readonly IMetaDatabase _metaDb;
    private readonly NpgsqlDataSource _dataSource;
    private readonly ILogger<BandRankingRepairService> _log;

    public BandRankingRepairService(IMetaDatabase metaDb, NpgsqlDataSource dataSource, ILogger<BandRankingRepairService> log)
    {
        _metaDb = metaDb;
        _dataSource = dataSource;
        _log = log;
    }

    public BandRankingRepairOverview Inspect(IReadOnlyList<string>? bandTypes = null)
    {
        var resolved = ResolveBandTypes(bandTypes);
        var counts = resolved.Select(GetCounts).ToList();
        return new BandRankingRepairOverview(GetTotalChartedSongs(), counts);
    }

    public IReadOnlyList<BandRankingRepairResult> Rebuild(IReadOnlyList<string>? bandTypes = null, int? totalChartedSongs = null)
    {
        var resolved = ResolveBandTypes(bandTypes);
        int chartedSongs = totalChartedSongs ?? GetTotalChartedSongs();
        var results = new List<BandRankingRepairResult>(resolved.Count);

        foreach (var bandType in resolved)
        {
            var before = GetCounts(bandType);
            var sw = Stopwatch.StartNew();

            if (chartedSongs > 0)
            {
                _metaDb.RebuildBandTeamRankings(bandType, chartedSongs, CredibilityThreshold, PopulationMedian);
            }
            else
            {
                _log.LogWarning("No songs are loaded, skipping band ranking rebuild for {BandType}.", bandType);
            }

            sw.Stop();
            var after = GetCounts(bandType);
            results.Add(new BandRankingRepairResult(bandType, chartedSongs, before, after, sw.Elapsed));
        }

        return results;
    }

    public int GetTotalChartedSongs()
    {
        using var conn = _dataSource.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM songs";
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    private BandRankingCounts GetCounts(string bandType)
    {
        using var conn = _dataSource.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT
                src.source_rows,
                src.rankable_rows,
                ranked.ranking_rows,
                stats.overall_teams,
                stats.combo_catalog_entries
            FROM (
                SELECT
                    COUNT(*)::INT AS source_rows,
                    COUNT(*) FILTER (WHERE NOT is_over_threshold)::INT AS rankable_rows
                FROM band_entries
                WHERE band_type = @bandType
            ) src
            CROSS JOIN (
                SELECT COUNT(*)::INT AS ranking_rows
                FROM band_team_rankings
                WHERE band_type = @bandType
            ) ranked
            CROSS JOIN (
                SELECT
                    COALESCE(MAX(total_teams) FILTER (WHERE ranking_scope = 'overall' AND combo_id = ''), 0)::INT AS overall_teams,
                    COUNT(*) FILTER (WHERE ranking_scope = 'combo')::INT AS combo_catalog_entries
                FROM band_team_ranking_stats
                WHERE band_type = @bandType
            ) stats";
        cmd.Parameters.AddWithValue("bandType", bandType);

        using var reader = cmd.ExecuteReader();
        if (!reader.Read())
            return new BandRankingCounts(bandType, 0, 0, 0, 0, 0);

        return new BandRankingCounts(
            bandType,
            reader.GetInt32(0),
            reader.GetInt32(1),
            reader.GetInt32(2),
            reader.GetInt32(3),
            reader.GetInt32(4));
    }

    private static List<string> ResolveBandTypes(IReadOnlyList<string>? bandTypes)
    {
        if (bandTypes is null || bandTypes.Count == 0)
            return BandInstrumentMapping.AllBandTypes.ToList();

        var resolved = new List<string>(bandTypes.Count);
        foreach (var bandType in bandTypes)
        {
            if (!BandComboIds.IsValidBandType(bandType))
                throw new ArgumentException($"Unknown band type: {bandType}", nameof(bandTypes));

            if (!resolved.Contains(bandType, StringComparer.OrdinalIgnoreCase))
                resolved.Add(bandType);
        }

        return resolved;
    }
}

public sealed record BandRankingRepairOverview(int TotalChartedSongs, IReadOnlyList<BandRankingCounts> Bands);

public sealed record BandRankingRepairResult(
    string BandType,
    int TotalChartedSongs,
    BandRankingCounts Before,
    BandRankingCounts After,
    TimeSpan Elapsed);

public sealed record BandRankingCounts(
    string BandType,
    int SourceRows,
    int RankableRows,
    int RankingRows,
    int OverallTeams,
    int ComboCatalogEntries);