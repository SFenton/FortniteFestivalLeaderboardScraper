using System.Diagnostics;
using FSTService.Persistence;
using Npgsql;

namespace FSTService.Scraping;

public sealed class ComboRankingRepairService
{
    private readonly GlobalLeaderboardPersistence _persistence;
    private readonly IMetaDatabase _metaDb;
    private readonly NpgsqlDataSource _dataSource;
    private readonly ILogger<ComboRankingRepairService> _log;

    public ComboRankingRepairService(
        GlobalLeaderboardPersistence persistence,
        IMetaDatabase metaDb,
        NpgsqlDataSource dataSource,
        ILogger<ComboRankingRepairService> log)
    {
        _persistence = persistence;
        _metaDb = metaDb;
        _dataSource = dataSource;
        _log = log;
    }

    public ComboRankingRepairOverview Inspect(IReadOnlyList<string>? comboIds = null)
    {
        var resolved = ComboLeaderboardBuilder.ResolveComboIds(comboIds);
        var expected = BuildExpectedLeaderboards(resolved);
        var combos = resolved.Select(comboId =>
        {
            expected.TryGetValue(comboId, out var computed);
            return GetCounts(comboId, computed);
        }).ToList();
        return new ComboRankingRepairOverview(combos);
    }

    public IReadOnlyList<ComboRankingRepairResult> Rebuild(IReadOnlyList<string>? comboIds = null)
    {
        var resolved = ComboLeaderboardBuilder.ResolveComboIds(comboIds);
        var expected = BuildExpectedLeaderboards(resolved);
        var results = new List<ComboRankingRepairResult>(resolved.Count);

        foreach (var comboId in resolved)
        {
            if (!expected.TryGetValue(comboId, out var computed))
                computed = new ComboLeaderboardBuilder.ComputedComboLeaderboard(comboId, ComboIds.ToInstruments(comboId), []);

            var before = GetCounts(comboId, computed);
            var sw = Stopwatch.StartNew();
            _metaDb.ReplaceComboLeaderboard(comboId, computed.Entries, computed.Entries.Count);
            sw.Stop();

            _log.LogInformation(
                "Rebuilt combo leaderboard {ComboId} with {AccountCount} ranked accounts in {ElapsedMs}ms.",
                comboId,
                computed.Entries.Count,
                sw.ElapsedMilliseconds);

            var after = GetCounts(comboId, computed);
            results.Add(new ComboRankingRepairResult(comboId, computed.Instruments, before, after, sw.Elapsed));
        }

        return results;
    }

    private Dictionary<string, ComboLeaderboardBuilder.ComputedComboLeaderboard> BuildExpectedLeaderboards(IReadOnlyList<string> comboIds)
    {
        var instruments = comboIds
            .SelectMany(ComboIds.ToInstruments)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var perInstrument = new Dictionary<string, Dictionary<string, RankingsCalculator.AccountMetrics>>(StringComparer.OrdinalIgnoreCase);
        foreach (var instrument in instruments)
        {
            var db = _persistence.GetOrCreateInstrumentDb(instrument);
            var accountMetrics = new Dictionary<string, RankingsCalculator.AccountMetrics>(StringComparer.OrdinalIgnoreCase);
            foreach (var (accountId, adj, wgt, fc, ts, ms, songs, fcc) in db.GetAllRankingSummariesFull())
                accountMetrics[accountId] = new RankingsCalculator.AccountMetrics(adj, wgt, fc, ts, ms, songs, fcc);

            perInstrument[instrument] = accountMetrics;
        }

        return ComboLeaderboardBuilder.BuildLeaderboards(comboIds, perInstrument)
            .ToDictionary(leaderboard => leaderboard.ComboId, StringComparer.OrdinalIgnoreCase);
    }

    private ComboRankingCounts GetCounts(string comboId, ComboLeaderboardBuilder.ComputedComboLeaderboard? expected)
    {
        using var conn = _dataSource.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT
                (SELECT COUNT(*)::INT FROM combo_leaderboard WHERE combo_id = @comboId),
                COALESCE((SELECT total_accounts FROM combo_stats WHERE combo_id = @comboId), 0),
                (SELECT computed_at FROM combo_stats WHERE combo_id = @comboId)";
        cmd.Parameters.AddWithValue("comboId", comboId);

        using var reader = cmd.ExecuteReader();
        reader.Read();

        return new ComboRankingCounts(
            comboId,
            expected?.Instruments ?? ComboIds.ToInstruments(comboId),
            expected?.Entries.Count ?? 0,
            reader.GetInt32(0),
            reader.GetInt32(1),
            reader.IsDBNull(2) ? null : reader.GetDateTime(2).ToString("o"));
    }
}

public sealed record ComboRankingRepairOverview(IReadOnlyList<ComboRankingCounts> Combos);

public sealed record ComboRankingRepairResult(
    string ComboId,
    IReadOnlyList<string> Instruments,
    ComboRankingCounts Before,
    ComboRankingCounts After,
    TimeSpan Elapsed);

public sealed record ComboRankingCounts(
    string ComboId,
    IReadOnlyList<string> Instruments,
    int ExpectedAccounts,
    int LeaderboardRows,
    int StatsTotalAccounts,
    string? ComputedAt);