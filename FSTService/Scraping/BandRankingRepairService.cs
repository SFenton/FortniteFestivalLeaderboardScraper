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

    public IReadOnlyList<BandRankingRepairResult> Rebuild(IReadOnlyList<string>? bandTypes = null, int? totalChartedSongs = null, BandTeamRankingRebuildOptions? options = null)
    {
        var resolved = ResolveBandTypes(bandTypes);
        int chartedSongs = totalChartedSongs ?? GetTotalChartedSongs();
        var results = new List<BandRankingRepairResult>(resolved.Count);

        foreach (var bandType in resolved)
        {
            var before = GetCounts(bandType);
            var sw = Stopwatch.StartNew();
            BandTeamRankingRebuildMetrics? metrics = null;

            if (chartedSongs > 0)
            {
                metrics = _metaDb.RebuildBandTeamRankingsMeasured(bandType, chartedSongs, CredibilityThreshold, PopulationMedian, options);
            }
            else
            {
                _log.LogWarning("No songs are loaded, skipping band ranking rebuild for {BandType}.", bandType);
            }

            sw.Stop();
            var after = GetCounts(bandType);
            results.Add(new BandRankingRepairResult(bandType, chartedSongs, before, after, sw.Elapsed, metrics));
        }

        return results;
    }

    public int RecomputeOverThresholdFlags(IReadOnlyList<string>? bandTypes = null, double overThresholdMultiplier = 1.05)
    {
        var resolved = ResolveBandTypes(bandTypes);

        using var conn = _dataSource.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 0;
        cmd.CommandText = """
            WITH member_thresholds AS (
                SELECT
                    bms.song_id,
                    bms.band_type,
                    bms.team_key,
                    bms.instrument_combo,
                    BOOL_OR(bms.score > FLOOR(max_scores.max_score * @overThresholdMultiplier)::INT) AS is_over_threshold
                FROM band_member_stats bms
                JOIN songs s ON s.song_id = bms.song_id
                CROSS JOIN LATERAL (
                    SELECT CASE bms.instrument_id
                        WHEN 0 THEN s.max_lead_score
                        WHEN 1 THEN s.max_bass_score
                        WHEN 2 THEN s.max_vocals_score
                        WHEN 3 THEN s.max_drums_score
                        WHEN 4 THEN s.max_pro_lead_score
                        WHEN 5 THEN s.max_pro_bass_score
                        ELSE NULL
                    END AS max_score
                ) max_scores
                WHERE bms.band_type = ANY(@bandTypes)
                  AND bms.score IS NOT NULL
                  AND max_scores.max_score IS NOT NULL
                  AND max_scores.max_score > 0
                GROUP BY bms.song_id, bms.band_type, bms.team_key, bms.instrument_combo
            ),
            recalculated AS (
                SELECT
                    be.song_id,
                    be.band_type,
                    be.team_key,
                    be.instrument_combo,
                    COALESCE(mt.is_over_threshold, FALSE) AS is_over_threshold
                FROM band_entries be
                LEFT JOIN member_thresholds mt
                  ON mt.song_id = be.song_id
                 AND mt.band_type = be.band_type
                 AND mt.team_key = be.team_key
                 AND mt.instrument_combo = be.instrument_combo
                WHERE be.band_type = ANY(@bandTypes)
            )
            UPDATE band_entries be
            SET is_over_threshold = r.is_over_threshold,
                last_updated_at = NOW()
            FROM recalculated r
            WHERE be.song_id = r.song_id
              AND be.band_type = r.band_type
              AND be.team_key = r.team_key
              AND be.instrument_combo = r.instrument_combo
              AND be.is_over_threshold IS DISTINCT FROM r.is_over_threshold
            """;
        cmd.Parameters.AddWithValue("bandTypes", resolved.ToArray());
        cmd.Parameters.AddWithValue("overThresholdMultiplier", overThresholdMultiplier);

        var updated = cmd.ExecuteNonQuery();
        _log.LogInformation(
            "Recomputed band over-threshold flags for {BandTypes} at multiplier {Multiplier:F3}: {Updated:N0} rows changed.",
            string.Join(", ", resolved),
            overThresholdMultiplier,
            updated);

        return updated;
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
        var rankingsTable = ResolveBandRankingTable(conn, bandType);
        var statsTable = ResolveBandRankingStatsTable(conn, bandType);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $@"
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
                FROM {BandRankingStorageNames.QuoteIdentifier(rankingsTable)}
                WHERE band_type = @bandType
            ) ranked
            CROSS JOIN (
                SELECT
                    COALESCE(MAX(total_teams) FILTER (WHERE ranking_scope = 'overall' AND combo_id = ''), 0)::INT AS overall_teams,
                    COUNT(*) FILTER (WHERE ranking_scope = 'combo')::INT AS combo_catalog_entries
                FROM {BandRankingStorageNames.QuoteIdentifier(statsTable)}
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

    private static string ResolveBandRankingTable(NpgsqlConnection conn, string bandType)
        => BandRankingStorageNames.GetCurrentRankingTable(bandType);

    private static string ResolveBandRankingStatsTable(NpgsqlConnection conn, string bandType)
        => BandRankingStorageNames.GetCurrentStatsTable(bandType);

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
    TimeSpan Elapsed,
    BandTeamRankingRebuildMetrics? Metrics = null);

public sealed record BandRankingCounts(
    string BandType,
    int SourceRows,
    int RankableRows,
    int RankingRows,
    int OverallTeams,
    int ComboCatalogEntries);