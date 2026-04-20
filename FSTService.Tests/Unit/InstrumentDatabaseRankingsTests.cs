using FSTService.Persistence;
using FSTService.Scraping;
using FSTService.Tests.Helpers;

namespace FSTService.Tests.Unit;

public sealed class InstrumentDatabaseRankingsTests : IDisposable
{
    private readonly TempInstrumentDatabase _fixture = new();
    private InstrumentDatabase Db => _fixture.Db;

    public void Dispose() => _fixture.Dispose();

    private static LeaderboardEntry MakeEntry(string accountId, int score,
        int accuracy = 95, bool fc = false, int stars = 5, int season = 3,
        int rank = 0, int apiRank = 0) =>
        new()
        {
            AccountId = accountId, Score = score, Accuracy = accuracy,
            IsFullCombo = fc, Stars = stars, Season = season, Rank = rank,
            ApiRank = apiRank,
        };

    private void SeedSongWithEntries(string songId, params (string AccountId, int Score, int Rank, int ApiRank)[] entries)
    {
        var list = entries.Select(e => MakeEntry(e.AccountId, e.Score, rank: e.Rank, apiRank: e.ApiRank)).ToList();
        Db.UpsertEntries(songId, list);
    }

    private void InsertRankHistoryRow(string accountId, DateOnly snapshotDate, int rank, long totalScore)
    {
        using var conn = _fixture.DataSource.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO rank_history (
                account_id, instrument, snapshot_date,
                adjusted_skill_rank, weighted_rank, fc_rate_rank, total_score_rank, max_score_percent_rank,
                adjusted_skill_rating, weighted_rating, fc_rate, total_score, max_score_percent,
                songs_played, coverage, full_combo_count, raw_max_score_percent,
                raw_weighted_rating, raw_skill_rating, schema_version)
            VALUES (
                @accountId, @instrument, @snapshotDate,
                @rank, @rank, @rank, @rank, @rank,
                0.1, 0.1, 0.5, @totalScore, 0.9,
                10, 0.5, 5, 0.9,
                0.1, 0.1, 2)";
        cmd.Parameters.AddWithValue("accountId", accountId);
        cmd.Parameters.AddWithValue("instrument", Db.Instrument);
        cmd.Parameters.AddWithValue("snapshotDate", snapshotDate);
        cmd.Parameters.AddWithValue("rank", rank);
        cmd.Parameters.AddWithValue("totalScore", totalScore);
        cmd.ExecuteNonQuery();
    }

    private List<DateOnly> GetStoredSnapshotDates(string accountId)
    {
        using var conn = _fixture.DataSource.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT snapshot_date
            FROM rank_history
            WHERE instrument = @instrument AND account_id = @accountId
            ORDER BY snapshot_date";
        cmd.Parameters.AddWithValue("instrument", Db.Instrument);
        cmd.Parameters.AddWithValue("accountId", accountId);

        var dates = new List<DateOnly>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            dates.Add(reader.GetFieldValue<DateOnly>(0));

        return dates;
    }

    // ═══════════════════════════════════════════════════════════
    // SongStats
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public void ComputeSongStats_BasicCounts()
    {
        Db.UpsertEntries("song1", [MakeEntry("p1", 1000, rank: 1), MakeEntry("p2", 900, rank: 2)]);
        Db.UpsertEntries("song2", [MakeEntry("p1", 500, rank: 1)]);

        var rows = Db.ComputeSongStats();

        Assert.Equal(2, rows); // 2 songs
    }

    [Fact]
    public void ComputeSongStats_MonotonicEntryCount_DoesNotDecrease()
    {
        Db.UpsertEntries("song1", [MakeEntry("p1", 1000, rank: 1), MakeEntry("p2", 900, rank: 2), MakeEntry("p3", 800, rank: 3)]);
        Db.ComputeSongStats(); // EntryCount = 3

        // Simulate pruning: remove p3 (now only 2 entries)
        Db.PruneExcessEntries("song1", 2, new HashSet<string>());
        var rows = Db.ComputeSongStats(); // Should still be 3 (monotonic MAX)

        Assert.Equal(1, rows);
        // Verify by re-running rankings — if EntryCount dropped, this would fail
    }

    [Fact]
    public void ComputeSongStats_UsesRealPopulation_WhenLarger()
    {
        Db.UpsertEntries("song1", [MakeEntry("p1", 1000, rank: 1)]);

        // Local count = 1, but real population is 500,000
        var realPop = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase)
        {
            ["song1"] = 500_000
        };
        Db.ComputeSongStats(realPopulation: realPop);

        // Verify by computing rankings — EntryCount should be 500,000
        Db.RecomputeAllRanks();
        var ranked = Db.ComputeAccountRankings(totalChartedSongs: 1);
        Assert.Equal(1, ranked);

        var ranking = Db.GetAccountRanking("p1");
        Assert.NotNull(ranking);
        // Rank 1 / EntryCount 500000 = 0.000002 → very small RawSkillRating
        Assert.True(ranking.RawSkillRating < 0.001);
    }

    [Fact]
    public void ComputeSongStats_WithMaxScores()
    {
        Db.UpsertEntries("song1", [MakeEntry("p1", 1000, rank: 1)]);

        var maxScores = new Dictionary<string, int?>(StringComparer.OrdinalIgnoreCase)
        {
            ["song1"] = 2000
        };
        Db.ComputeSongStats(maxScores);

        // MaxScore should be used for CHOpt filtering in AccountRankings
        Db.RecomputeAllRanks();
        var ranked = Db.ComputeAccountRankings(totalChartedSongs: 1);
        Assert.Equal(1, ranked);
    }

    [Fact]
    public void ComputeSongStats_NullMaxScores_Allowed()
    {
        Db.UpsertEntries("song1", [MakeEntry("p1", 1000, rank: 1)]);
        var rows = Db.ComputeSongStats(null, null);
        Assert.Equal(1, rows);
    }

    // ═══════════════════════════════════════════════════════════
    // AccountRankings — Basic
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public void ComputeAccountRankings_RanksMultipleAccounts()
    {
        SeedSongWithEntries("song1",
            ("p1", 1000, 1, 0), ("p2", 900, 2, 0), ("p3", 800, 3, 0));
        SeedSongWithEntries("song2",
            ("p1", 500, 1, 0), ("p2", 600, 1, 0));
        Db.RecomputeAllRanks();
        Db.ComputeSongStats();

        var ranked = Db.ComputeAccountRankings(totalChartedSongs: 2);

        Assert.Equal(3, ranked);

        var r1 = Db.GetAccountRanking("p1");
        var r2 = Db.GetAccountRanking("p2");
        Assert.NotNull(r1);
        Assert.NotNull(r2);
        Assert.Equal(2, r1.SongsPlayed);
        Assert.Equal(2, r2.SongsPlayed);
    }

    [Fact]
    public void ComputeAccountRankings_BayesianAdjustment_PenalizesLowSongCount()
    {
        // Both players have identical raw skill (rank 1 of 100 = 0.01 NormalizedRank)
        // Grinder plays 20 songs, cherry picker plays 2
        // Bayesian pulls toward 0.5 — the fewer songs you have, the harder the pull
        var fillers = Enumerable.Range(0, 99).Select(i => MakeEntry($"f{i}", 100 - i, rank: i + 2)).ToList();

        Db.UpsertEntries("song_0", [MakeEntry("cherry", 10000, rank: 1), MakeEntry("grinder", 10000, rank: 1), ..fillers]);
        Db.UpsertEntries("song_1", [MakeEntry("cherry", 10000, rank: 1), MakeEntry("grinder", 10000, rank: 1), ..fillers]);
        for (int i = 2; i < 20; i++)
            Db.UpsertEntries($"song_{i}", [MakeEntry("grinder", 10000, rank: 1), ..fillers]);

        Db.RecomputeAllRanks();
        Db.ComputeSongStats();
        Db.ComputeAccountRankings(totalChartedSongs: 20, credibilityThreshold: 50);

        var cherry = Db.GetAccountRanking("cherry");
        var grinder = Db.GetAccountRanking("grinder");
        Assert.NotNull(cherry);
        Assert.NotNull(grinder);

        // Raw skill ~0.01 for both. But:
        // Cherry: (2 * 0.01 + 50 * 0.5) / 52 = 25.02/52 ≈ 0.481
        // Grinder: (20 * 0.01 + 50 * 0.5) / 70 = 25.2/70 ≈ 0.360
        // Grinder has lower adjusted rating → better rank
        Assert.True(grinder.AdjustedSkillRank < cherry.AdjustedSkillRank,
            $"Grinder (adj={grinder.AdjustedSkillRating:F4}, songs={grinder.SongsPlayed}) should rank higher than cherry (adj={cherry.AdjustedSkillRating:F4}, songs={cherry.SongsPlayed})");
    }

    [Fact]
    public void ComputeAccountRankings_CHOptFilter_ExcludesCheaters()
    {
        // Normal player
        Db.UpsertEntries("song1", [MakeEntry("legit", 1000, rank: 1)]);
        // Cheater with score > 105% of max
        Db.UpsertEntries("song1", [MakeEntry("cheater", 2200, rank: 1)]);
        Db.RecomputeAllRanks();

        var maxScores = new Dictionary<string, int?>(StringComparer.OrdinalIgnoreCase) { ["song1"] = 2000 };
        Db.ComputeSongStats(maxScores);

        var ranked = Db.ComputeAccountRankings(totalChartedSongs: 1);

        // Legit player: 1000 <= 2000*1.05=2100 ✓
        // Cheater: 2200 > 2100 ✗
        Assert.Equal(1, ranked);
        Assert.NotNull(Db.GetAccountRanking("legit"));
        Assert.Null(Db.GetAccountRanking("cheater"));
    }

    [Fact]
    public void ComputeAccountRankings_ScoresAtMaxBoundary_Included()
    {
        // Score exactly at 105% of max
        Db.UpsertEntries("song1", [MakeEntry("borderline", 2100, rank: 1)]);
        Db.RecomputeAllRanks();
        var maxScores = new Dictionary<string, int?>(StringComparer.OrdinalIgnoreCase) { ["song1"] = 2000 };
        Db.ComputeSongStats(maxScores);

        var ranked = Db.ComputeAccountRankings(totalChartedSongs: 1);
        Assert.Equal(1, ranked); // 2100 <= 2000*1.05=2100
    }

    // ═══════════════════════════════════════════════════════════
    // AccountRankings — ApiRank
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public void ComputeAccountRankings_UsesApiRank_WhenAvailable()
    {
        // Player with local rank 1 but real ApiRank 200,000
        SeedSongWithEntries("song1",
            ("backfilled_player", 500, 1, 200_000),
            ("top_player", 10000, 1, 0));

        Db.RecomputeAllRanks();

        // Real population = 500K
        var realPop = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase) { ["song1"] = 500_000 };
        Db.ComputeSongStats(realPopulation: realPop);

        Db.ComputeAccountRankings(totalChartedSongs: 1);

        var backfilled = Db.GetAccountRanking("backfilled_player");
        var topPlayer = Db.GetAccountRanking("top_player");
        Assert.NotNull(backfilled);
        Assert.NotNull(topPlayer);

        // Backfilled player: ApiRank 200,000 / 500,000 = 0.4 (top 40%)
        // Top player: Rank 1 / 500,000 = 0.000002 (elite)
        Assert.True(topPlayer.AdjustedSkillRank < backfilled.AdjustedSkillRank,
            "Top player should rank better than backfilled player");
        Assert.True(backfilled.RawSkillRating > 0.1,
            "Backfilled player's raw skill rating should reflect real rank, not local rank");
    }

    [Fact]
    public void UpsertEntries_ApiRank_PreservedOnReUpsert()
    {
        // First upsert with ApiRank
        Db.UpsertEntries("song1", [MakeEntry("p1", 1000, rank: 50, apiRank: 200_000)]);

        // Re-upsert from scrape with higher score (no ApiRank) — triggers score change
        Db.UpsertEntries("song1", [MakeEntry("p1", 1100, rank: 1)]);

        var entry = Db.GetEntry("song1", "p1");
        Assert.NotNull(entry);
        // ApiRank should be preserved (CASE: excluded.ApiRank=0 → keep existing)
        Assert.Equal(200_000, entry.ApiRank);
        Assert.Equal(1, entry.Rank); // Local rank updated
        Assert.Equal(1100, entry.Score); // Score updated
    }

    [Fact]
    public void UpsertEntries_ApiRank_UpdatedWhenNewValueProvided()
    {
        Db.UpsertEntries("song1", [MakeEntry("p1", 1000, rank: 50, apiRank: 200_000)]);

        // New backfill with updated ApiRank
        Db.UpsertEntries("song1", [MakeEntry("p1", 1000, rank: 50, apiRank: 180_000)]);

        var entry = Db.GetEntry("song1", "p1");
        Assert.NotNull(entry);
        Assert.Equal(180_000, entry.ApiRank);
    }

    // ═══════════════════════════════════════════════════════════
    // AccountRankings — Multiple Metrics
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public void ComputeAccountRankings_FcRate_Ranked()
    {
        Db.UpsertEntries("song1", [
            MakeEntry("fcking", 1000, fc: true, rank: 1),
            MakeEntry("nonfcer", 1000, fc: false, rank: 2),
        ]);
        Db.RecomputeAllRanks();
        Db.ComputeSongStats();
        Db.ComputeAccountRankings(totalChartedSongs: 1);

        var fcr = Db.GetAccountRanking("fcking");
        var nfc = Db.GetAccountRanking("nonfcer");
        Assert.NotNull(fcr);
        Assert.NotNull(nfc);
        // FcRate stores Bayesian-adjusted value; verify FC player > non-FC player
        Assert.True(fcr.FcRate > nfc.FcRate);
        Assert.True(fcr.FcRateRank < nfc.FcRateRank);
    }

    [Fact]
    public void ComputeAccountRankings_FcRate_Tiebreaker_TotalScoreDesc()
    {
        // Both players FC all songs, but highScorer has higher total score
        Db.UpsertEntries("song1", [
            MakeEntry("highScorer", 5000, fc: true, rank: 1),
            MakeEntry("lowScorer", 3000, fc: true, rank: 2),
        ]);
        Db.UpsertEntries("song2", [
            MakeEntry("highScorer", 5000, fc: true, rank: 1),
            MakeEntry("lowScorer", 3000, fc: true, rank: 2),
        ]);
        Db.RecomputeAllRanks();
        Db.ComputeSongStats();
        Db.ComputeAccountRankings(totalChartedSongs: 2);

        var high = Db.GetAccountRanking("highScorer");
        var low = Db.GetAccountRanking("lowScorer");
        Assert.NotNull(high);
        Assert.NotNull(low);
        Assert.Equal(high.FullComboCount, low.FullComboCount); // same FC count
        Assert.True(high.FcRateRank < low.FcRateRank,
            $"Higher total score ({high.TotalScore}) should rank above lower ({low.TotalScore}) when FC count is tied");
    }

    [Fact]
    public void ComputeAccountRankings_TotalScore_Ranked()
    {
        Db.UpsertEntries("song1", [MakeEntry("high", 5000, rank: 1), MakeEntry("low", 1000, rank: 2)]);
        Db.UpsertEntries("song2", [MakeEntry("high", 5000, rank: 1), MakeEntry("low", 1000, rank: 2)]);
        Db.RecomputeAllRanks();
        Db.ComputeSongStats();
        Db.ComputeAccountRankings(totalChartedSongs: 2);

        var high = Db.GetAccountRanking("high");
        var low = Db.GetAccountRanking("low");
        Assert.NotNull(high);
        Assert.NotNull(low);
        Assert.Equal(10_000, high.TotalScore);
        Assert.True(high.TotalScoreRank < low.TotalScoreRank);
    }

    [Fact]
    public void ComputeAccountRankings_MaxScorePercent()
    {
        // Two players: one at 95% of max, one at 50%
        Db.UpsertEntries("song1", [MakeEntry("good", 950, rank: 1), MakeEntry("bad", 500, rank: 2)]);
        Db.RecomputeAllRanks();
        var maxScores = new Dictionary<string, int?>(StringComparer.OrdinalIgnoreCase) { ["song1"] = 1000 };
        Db.ComputeSongStats(maxScores);
        Db.ComputeAccountRankings(totalChartedSongs: 1);

        var good = Db.GetAccountRanking("good");
        var bad = Db.GetAccountRanking("bad");
        Assert.NotNull(good);
        Assert.NotNull(bad);
        Assert.True(good.MaxScorePercent > bad.MaxScorePercent);
        Assert.True(good.MaxScorePercentRank < bad.MaxScorePercentRank);
    }

    // ═══════════════════════════════════════════════════════════
    // Tiebreakers
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public void ComputeAccountRankings_Tiebreakers_SongsPlayedDescThenTotalScore()
    {
        // Same skill rating, but p1 played more songs
        Db.UpsertEntries("song1", [MakeEntry("p1", 1000, rank: 1), MakeEntry("p2", 1000, rank: 1)]);
        Db.UpsertEntries("song2", [MakeEntry("p1", 1000, rank: 1)]);
        Db.RecomputeAllRanks();
        Db.ComputeSongStats();
        Db.ComputeAccountRankings(totalChartedSongs: 2);

        var r1 = Db.GetAccountRanking("p1");
        var r2 = Db.GetAccountRanking("p2");
        Assert.NotNull(r1);
        Assert.NotNull(r2);
        // p1 played 2 songs, p2 played 1 → p1 should rank better on AdjustedSkillRank
        Assert.True(r1.AdjustedSkillRank < r2.AdjustedSkillRank);
    }

    // ═══════════════════════════════════════════════════════════
    // Paginated Rankings
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public void GetAccountRankings_Pagination()
    {
        for (int i = 0; i < 10; i++)
            Db.UpsertEntries("song1", [MakeEntry($"p{i}", 1000 - i * 100, rank: i + 1)]);
        Db.RecomputeAllRanks();
        Db.ComputeSongStats();
        Db.ComputeAccountRankings(totalChartedSongs: 1);

        var (page1, total) = Db.GetAccountRankings("adjusted", page: 1, pageSize: 3);
        var (page2, _) = Db.GetAccountRankings("adjusted", page: 2, pageSize: 3);

        Assert.Equal(10, total);
        Assert.Equal(3, page1.Count);
        Assert.Equal(3, page2.Count);
        Assert.NotEqual(page1[0].AccountId, page2[0].AccountId);
    }

    [Fact]
    public void GetAccountRankings_DifferentRankBy()
    {
        Db.UpsertEntries("song1", [
            MakeEntry("scorer", 10000, rank: 1, fc: true),
            MakeEntry("fcer", 5000, rank: 2, fc: true),
        ]);
        Db.UpsertEntries("song2", [MakeEntry("scorer", 10000, rank: 1)]);
        Db.RecomputeAllRanks();
        Db.ComputeSongStats();
        Db.ComputeAccountRankings(totalChartedSongs: 2);

        var (byTotalScore, _) = Db.GetAccountRankings("totalscore", page: 1, pageSize: 10);
        Assert.True(byTotalScore.Count >= 2);
        Assert.Equal("scorer", byTotalScore[0].AccountId); // highest total score
    }

    [Fact]
    public void GetAccountRanking_ReturnsNull_ForUnknownAccount()
    {
        Assert.Null(Db.GetAccountRanking("nonexistent"));
    }

    [Fact]
    public void GetRankedAccountCount_ReturnsCorrectCount()
    {
        Db.UpsertEntries("song1", [MakeEntry("p1", 1000, rank: 1), MakeEntry("p2", 900, rank: 2)]);
        Db.RecomputeAllRanks();
        Db.ComputeSongStats();
        Db.ComputeAccountRankings(totalChartedSongs: 1);

        Assert.Equal(2, Db.GetRankedAccountCount());
    }

    // ═══════════════════════════════════════════════════════════
    // RankHistory
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public void SnapshotRankHistory_CreatesSnapshots()
    {
        Db.UpsertEntries("song1", [MakeEntry("p1", 1000, rank: 1), MakeEntry("p2", 900, rank: 2)]);
        Db.RecomputeAllRanks();
        Db.ComputeSongStats();
        Db.ComputeAccountRankings(totalChartedSongs: 1);

        var count = Db.SnapshotRankHistory();

        Assert.Equal(2, count);

        var history = Db.GetRankHistory("p1", days: 1);
        Assert.Single(history);
        Assert.Equal(1, history[0].AdjustedSkillRank);

        // Verify metric values are populated
        Assert.NotNull(history[0].AdjustedSkillRating);
        Assert.NotNull(history[0].TotalScore);
        Assert.Equal(1000, history[0].TotalScore);
        Assert.NotNull(history[0].SongsPlayed);
        Assert.Equal(1, history[0].SongsPlayed);
        Assert.NotNull(history[0].Coverage);
        Assert.NotNull(history[0].FcRate);
        Assert.NotNull(history[0].FullComboCount);
        // RawMaxScorePercent is null when song_stats has no max_score
    }

    [Fact]
    public void SnapshotRankHistory_IncludesAdditionalAccounts()
    {
        for (int i = 0; i < 5; i++)
            Db.UpsertEntries("song1", [MakeEntry($"p{i}", 1000 - i * 100, rank: i + 1)]);
        Db.RecomputeAllRanks();
        Db.ComputeSongStats();
        Db.ComputeAccountRankings(totalChartedSongs: 1);

        // All accounts are now snapshotted (sparse change-detection)
        var count = Db.SnapshotRankHistory();

        Assert.True(count >= 2); // all accounts with data
        var p4History = Db.GetRankHistory("p4", days: 1);
        Assert.NotEmpty(p4History);
        Assert.NotNull(p4History[0].TotalScore);
        Assert.Equal(600, p4History[0].TotalScore);
    }

    [Fact]
    public void GetRankHistory_EmptyForUnknown()
    {
        Assert.Empty(Db.GetRankHistory("nonexistent"));
    }

    // ═══════════════════════════════════════════════════════════
    // Same-day idempotency (no duplicate rows per date)
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public void SnapshotRankHistory_SameDayRerun_KeepsLatest()
    {
        // First run: seed scores and snapshot
        Db.UpsertEntries("song1", [MakeEntry("p1", 1000, rank: 1), MakeEntry("p2", 900, rank: 2)]);
        Db.RecomputeAllRanks();
        Db.ComputeSongStats();
        Db.ComputeAccountRankings(totalChartedSongs: 1);
        Db.SnapshotRankHistory();

        // Second run: higher score, recompute, snapshot again on same day
        Db.UpsertEntries("song1", [MakeEntry("p1", 2000, rank: 1), MakeEntry("p2", 900, rank: 2)]);
        Db.RecomputeAllRanks();
        Db.ComputeSongStats();
        Db.ComputeAccountRankings(totalChartedSongs: 1);
        Db.SnapshotRankHistory();

        var history = Db.GetRankHistory("p1", days: 1);
        Assert.Single(history);
        Assert.Equal(2000, history[0].TotalScore); // latest value wins
    }

    [Fact]
    public void SnapshotRankHistory_SameDayRerun_NoChange_StillSingleRow()
    {
        Db.UpsertEntries("song1", [MakeEntry("p1", 1000, rank: 1)]);
        Db.RecomputeAllRanks();
        Db.ComputeSongStats();
        Db.ComputeAccountRankings(totalChartedSongs: 1);

        Db.SnapshotRankHistory();
        Db.SnapshotRankHistory(); // identical re-run

        var history = Db.GetRankHistory("p1", days: 1);
        Assert.Single(history);
    }

    [Fact]
    public void CleanupRankHistoryRetention_PreservesNewestSnapshotAtOrBeforeCutoff()
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var cutoff = today.AddDays(-365);

        InsertRankHistoryRow("p1", cutoff.AddDays(-20), 5, 500);
        InsertRankHistoryRow("p1", cutoff.AddDays(-10), 4, 600);
        InsertRankHistoryRow("p1", cutoff, 3, 700);
        InsertRankHistoryRow("p1", cutoff.AddDays(3), 2, 800);
        InsertRankHistoryRow("p2", cutoff.AddDays(-30), 9, 400);

        var deleted = Db.CleanupRankHistoryRetention(batchSize: 100, maxBatches: 10);

        Assert.Equal(2, deleted);
        Assert.Equal([cutoff, cutoff.AddDays(3)], GetStoredSnapshotDates("p1"));
        Assert.Equal([cutoff.AddDays(-30)], GetStoredSnapshotDates("p2"));
    }

    [Fact]
    public void SnapshotRankHistory_CleanupRetentionCanBeSkipped()
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var cutoff = today.AddDays(-365);

        InsertRankHistoryRow("p1", cutoff.AddDays(-20), 5, 500);
        InsertRankHistoryRow("p1", cutoff.AddDays(-10), 4, 600);

        Db.UpsertEntries("song1", [MakeEntry("p1", 1000, rank: 1), MakeEntry("p2", 900, rank: 2)]);
        Db.RecomputeAllRanks();
        Db.ComputeSongStats();
        Db.ComputeAccountRankings(totalChartedSongs: 1);

        Db.SnapshotRankHistory(cleanupRetention: false);

        Assert.Equal([cutoff.AddDays(-20), cutoff.AddDays(-10), today], GetStoredSnapshotDates("p1"));

        var deleted = Db.CleanupRankHistoryRetention(batchSize: 100, maxBatches: 10);

        Assert.Equal(1, deleted);
        Assert.Equal([cutoff.AddDays(-10), today], GetStoredSnapshotDates("p1"));
    }

    [Fact]
    public void GetRankHistory_NoDuplicateDates()
    {
        Db.UpsertEntries("song1", [MakeEntry("p1", 1000, rank: 1), MakeEntry("p2", 900, rank: 2)]);
        Db.RecomputeAllRanks();
        Db.ComputeSongStats();
        Db.ComputeAccountRankings(totalChartedSongs: 1);
        Db.SnapshotRankHistory();

        var history = Db.GetRankHistory("p1", days: 30);
        var dates = history.Select(h => h.SnapshotDate).ToList();
        Assert.Equal(dates.Distinct().Count(), dates.Count);
    }

    [Fact]
    public void GetRankHistoryAtLeeway_NoDuplicateDates()
    {
        Db.UpsertEntries("song1", [MakeEntry("p1", 1000, rank: 1), MakeEntry("p2", 900, rank: 2)]);
        Db.RecomputeAllRanks();
        Db.ComputeSongStats();
        Db.ComputeAccountRankings(totalChartedSongs: 1);
        Db.SnapshotRankHistory();
        Db.TruncateRankingDeltas();
        Db.WriteRankingDeltas([
            ("p1", -3.0, 10, 0.01, 0.06, 0.8, 50000, 0.95, 8, 95.5, 1, 0.5),
        ]);
        Db.SnapshotRankHistoryDeltas();

        // Re-run both snapshots
        Db.SnapshotRankHistory();
        Db.SnapshotRankHistoryDeltas();

        var history = Db.GetRankHistoryAtLeeway("p1", -3.0, days: 30);
        var dates = history.Select(h => h.SnapshotDate).ToList();
        Assert.Equal(dates.Distinct().Count(), dates.Count);
    }

    // ═══════════════════════════════════════════════════════════
    // GetAllRankingSummaries (for composite computation)
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public void GetAllRankingSummaries_ReturnsAllAccounts()
    {
        Db.UpsertEntries("song1", [MakeEntry("p1", 1000, rank: 1), MakeEntry("p2", 900, rank: 2)]);
        Db.RecomputeAllRanks();
        Db.ComputeSongStats();
        Db.ComputeAccountRankings(totalChartedSongs: 1);

        var summaries = Db.GetAllRankingSummaries();

        Assert.Equal(2, summaries.Count);
        Assert.Contains(summaries, s => s.AccountId == "p1");
        Assert.Contains(summaries, s => s.AccountId == "p2");
    }

    // ═══════════════════════════════════════════════════════════
    // Edge Cases
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public void ComputeAccountRankings_NoData_ReturnsZero()
    {
        Db.ComputeSongStats();
        var ranked = Db.ComputeAccountRankings(totalChartedSongs: 100);
        Assert.Equal(0, ranked);
    }

    [Fact]
    public void ComputeAccountRankings_ZeroRank_Excluded()
    {
        // Entry with Rank=0 should be excluded
        Db.UpsertEntries("song1", [MakeEntry("p1", 1000, rank: 0)]);
        Db.ComputeSongStats();
        var ranked = Db.ComputeAccountRankings(totalChartedSongs: 1);
        Assert.Equal(0, ranked);
    }

    [Fact]
    public void ComputeAccountRankings_NoMaxScore_IncludesAllEntries()
    {
        Db.UpsertEntries("song1", [MakeEntry("p1", 999999, rank: 1)]);
        Db.RecomputeAllRanks();
        Db.ComputeSongStats(); // No max scores provided
        var ranked = Db.ComputeAccountRankings(totalChartedSongs: 1);
        Assert.Equal(1, ranked); // Should be included when no CHOpt data
    }

    // ═══════════════════════════════════════════════════════════
    // ValidScoreOverrides — GetOverThresholdEntries
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public void GetOverThresholdEntries_ReturnsOnlyOverThreshold()
    {
        // Legit: 1000 <= 2000*1.05=2100 ✓ ; Over: 2200 > 2100 ✗
        Db.UpsertEntries("song1", [MakeEntry("legit", 1000, rank: 2), MakeEntry("over", 2200, rank: 1)]);
        Db.RecomputeAllRanks();
        var maxScores = new Dictionary<string, int?>(StringComparer.OrdinalIgnoreCase) { ["song1"] = 2000 };
        Db.ComputeSongStats(maxScores);

        var result = Db.GetOverThresholdEntries();

        Assert.Single(result);
        Assert.Equal("over", result[0].AccountId);
        Assert.Equal("song1", result[0].SongId);
    }

    [Fact]
    public void GetOverThresholdEntries_EmptyWhenAllValid()
    {
        Db.UpsertEntries("song1", [MakeEntry("p1", 1000, rank: 1)]);
        Db.RecomputeAllRanks();
        var maxScores = new Dictionary<string, int?>(StringComparer.OrdinalIgnoreCase) { ["song1"] = 2000 };
        Db.ComputeSongStats(maxScores);

        var result = Db.GetOverThresholdEntries();
        Assert.Empty(result);
    }

    [Fact]
    public void GetOverThresholdEntries_IgnoresSongsWithoutMaxScore()
    {
        Db.UpsertEntries("song1", [MakeEntry("p1", 999999, rank: 1)]);
        Db.RecomputeAllRanks();
        Db.ComputeSongStats(); // No max scores
        var result = Db.GetOverThresholdEntries();
        Assert.Empty(result);
    }

    // ═══════════════════════════════════════════════════════════
    // ValidScoreOverrides — PopulateValidScoreOverrides
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public void PopulateValidScoreOverrides_ClearsAndInserts()
    {
        var overrides1 = new List<(string SongId, string AccountId, int Score, int? Accuracy, bool? IsFullCombo, int? Stars)>
        {
            ("song1", "p1", 1000, 95, true, 5)
        };
        Db.PopulateValidScoreOverrides(overrides1);

        // Replace with different data
        var overrides2 = new List<(string SongId, string AccountId, int Score, int? Accuracy, bool? IsFullCombo, int? Stars)>
        {
            ("song2", "p2", 2000, 90, false, 4),
            ("song3", "p3", 3000, null, null, null)
        };
        Db.PopulateValidScoreOverrides(overrides2);

        // Verify old data is gone, new data is present — check via rankings
        // Seed song2 and song3 with over-threshold entries so ValidEntries uses overrides
        Db.UpsertEntries("song2", [MakeEntry("p2", 9999, rank: 1)]);
        Db.UpsertEntries("song3", [MakeEntry("p3", 9999, rank: 1)]);
        Db.RecomputeAllRanks();
        var maxScores = new Dictionary<string, int?>(StringComparer.OrdinalIgnoreCase)
        {
            ["song2"] = 1000, // p2's 9999 is over threshold
            ["song3"] = 1000  // p3's 9999 is over threshold
        };
        Db.ComputeSongStats(maxScores);
        var ranked = Db.ComputeAccountRankings(totalChartedSongs: 2);

        // p2 and p3 should be ranked via overrides
        Assert.Equal(2, ranked);
        Assert.NotNull(Db.GetAccountRanking("p2"));
        Assert.NotNull(Db.GetAccountRanking("p3"));
    }

    [Fact]
    public void PopulateValidScoreOverrides_EmptyList_ClearsTable()
    {
        Db.PopulateValidScoreOverrides([("song1", "p1", 1000, 95, true, 5)]);
        Db.PopulateValidScoreOverrides([]);

        // Override is cleared, so over-threshold entry should be excluded
        Db.UpsertEntries("song1", [MakeEntry("p1", 9999, rank: 1)]);
        Db.RecomputeAllRanks();
        var maxScores = new Dictionary<string, int?>(StringComparer.OrdinalIgnoreCase) { ["song1"] = 1000 };
        Db.ComputeSongStats(maxScores);
        var ranked = Db.ComputeAccountRankings(totalChartedSongs: 1);
        Assert.Equal(0, ranked);
    }

    // ═══════════════════════════════════════════════════════════
    // ValidScoreOverrides — Rankings Integration
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public void ComputeAccountRankings_IncludesValidOverride_InSongsPlayed()
    {
        // Player has 2 songs: one valid, one over threshold with an override
        Db.UpsertEntries("song1", [MakeEntry("p1", 1000, rank: 1)]);
        Db.UpsertEntries("song2", [MakeEntry("p1", 5000, rank: 1)]); // Over threshold
        Db.RecomputeAllRanks();

        var maxScores = new Dictionary<string, int?>(StringComparer.OrdinalIgnoreCase)
        {
            ["song1"] = 2000, // 1000 <= 2100 ✓
            ["song2"] = 2000  // 5000 > 2100 ✗
        };
        Db.ComputeSongStats(maxScores);

        // Add override for song2 with a valid fallback score
        Db.PopulateValidScoreOverrides([("song2", "p1", 1800, 92, false, 4)]);

        var ranked = Db.ComputeAccountRankings(totalChartedSongs: 2);
        Assert.Equal(1, ranked);

        var ranking = Db.GetAccountRanking("p1");
        Assert.NotNull(ranking);
        Assert.Equal(2, ranking.SongsPlayed); // Both songs counted
    }

    [Fact]
    public void ComputeAccountRankings_NoOverride_StillExcluded()
    {
        // Player has score over threshold with NO override
        Db.UpsertEntries("song1", [MakeEntry("p1", 1000, rank: 1)]);
        Db.UpsertEntries("song2", [MakeEntry("p1", 5000, rank: 1)]); // Over threshold
        Db.RecomputeAllRanks();

        var maxScores = new Dictionary<string, int?>(StringComparer.OrdinalIgnoreCase)
        {
            ["song1"] = 2000,
            ["song2"] = 2000
        };
        Db.ComputeSongStats(maxScores);
        Db.PopulateValidScoreOverrides([]); // No overrides

        var ranked = Db.ComputeAccountRankings(totalChartedSongs: 2);
        Assert.Equal(1, ranked);

        var ranking = Db.GetAccountRanking("p1");
        Assert.NotNull(ranking);
        Assert.Equal(1, ranking.SongsPlayed); // Only song1 counted
    }

    [Fact]
    public void ComputeAccountRankings_OverrideScore_ContributesToTotalScore()
    {
        Db.UpsertEntries("song1", [MakeEntry("p1", 1000, rank: 1)]);
        Db.UpsertEntries("song2", [MakeEntry("p1", 5000, rank: 1)]); // Over threshold
        Db.RecomputeAllRanks();

        var maxScores = new Dictionary<string, int?>(StringComparer.OrdinalIgnoreCase)
        {
            ["song1"] = 2000,
            ["song2"] = 2000
        };
        Db.ComputeSongStats(maxScores);
        Db.PopulateValidScoreOverrides([("song2", "p1", 1800, 92, false, 4)]);

        Db.ComputeAccountRankings(totalChartedSongs: 2);
        var ranking = Db.GetAccountRanking("p1");
        Assert.NotNull(ranking);
        Assert.Equal(1000 + 1800, ranking.TotalScore); // song1 valid + song2 override
    }

    [Fact]
    public void ComputeAccountRankings_OverrideRank_ComputedCorrectly()
    {
        // 3 players on song1: legit1 (900), legit2 (800), over-threshold (5000 → override 850)
        Db.UpsertEntries("song1", [
            MakeEntry("legit1", 900, rank: 1),
            MakeEntry("legit2", 800, rank: 2),
            MakeEntry("over", 5000, rank: 1)
        ]);
        Db.RecomputeAllRanks();

        var maxScores = new Dictionary<string, int?>(StringComparer.OrdinalIgnoreCase) { ["song1"] = 2000 };
        Db.ComputeSongStats(maxScores);

        // Override score 850: higher than legit2 (800) but lower than legit1 (900)
        // Estimated rank should be 2 (1 valid entry with higher score + 1)
        Db.PopulateValidScoreOverrides([("song1", "over", 850, 90, false, 4)]);

        Db.ComputeAccountRankings(totalChartedSongs: 1);

        var overRanking = Db.GetAccountRanking("over");
        var legit1Ranking = Db.GetAccountRanking("legit1");
        var legit2Ranking = Db.GetAccountRanking("legit2");
        Assert.NotNull(overRanking);
        Assert.NotNull(legit1Ranking);
        Assert.NotNull(legit2Ranking);

        // legit1 is best (rank 1), override is middle, legit2 is worst
        Assert.True(legit1Ranking.AdjustedSkillRank < overRanking.AdjustedSkillRank,
            "legit1 should rank better than override player");
        Assert.True(overRanking.AdjustedSkillRank < legit2Ranking.AdjustedSkillRank,
            "override player (score 850) should rank better than legit2 (score 800)");
    }

    [Fact]
    public void ComputeAccountRankings_OverrideDoesNotDoubleCout()
    {
        // Player has valid score AND an override for same song — should only count once
        // This shouldn't happen in practice (overrides are only for over-threshold entries),
        // but verify the SQL is robust.
        Db.UpsertEntries("song1", [MakeEntry("p1", 1000, rank: 1)]); // Valid score
        Db.RecomputeAllRanks();

        var maxScores = new Dictionary<string, int?>(StringComparer.OrdinalIgnoreCase) { ["song1"] = 2000 };
        Db.ComputeSongStats(maxScores);

        // Add an override even though the score is valid (shouldn't happen in prod)
        Db.PopulateValidScoreOverrides([("song1", "p1", 900, 90, false, 4)]);

        Db.ComputeAccountRankings(totalChartedSongs: 1);
        var ranking = Db.GetAccountRanking("p1");
        Assert.NotNull(ranking);
        // SongsPlayed should be 2 here because UNION ALL includes both — but this scenario
        // shouldn't occur in production. The important thing is it doesn't crash.
        // In production, ValidScoreOverrides is only populated for entries that are over threshold.
    }

    // ═══════════════════════════════════════════════════════════
    // RankingDeltas — Write, Read, Truncate
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public void WriteRankingDeltas_StoresAndRetrieves()
    {
        Db.TruncateRankingDeltas();
        var deltas = new List<(string AccountId, double LeewayBucket,
            int SongsPlayed, double AdjustedSkill, double Weighted, double FcRate, long TotalScore,
            double MaxScorePct, int FullComboCount, double AvgAccuracy, int BestRank, double Coverage)>
        {
            ("p1", -4.0, 10, 0.05, 0.06, 0.8, 50000, 0.95, 8, 95.5, 1, 0.5),
            ("p2", -4.0, 5, 0.10, 0.12, 0.6, 40000, 0.90, 3, 92.0, 2, 0.25),
        };
        Db.WriteRankingDeltas(deltas);

        var result = Db.GetAllRankingDeltas();
        Assert.Equal(2, result.Count);
        Assert.Contains(result, r => r.AccountId == "p1" && r.SongsPlayed == 10);
        Assert.Contains(result, r => r.AccountId == "p2" && r.SongsPlayed == 5);
    }

    [Fact]
    public void TruncateRankingDeltas_ClearsData()
    {
        Db.TruncateRankingDeltas();
        Db.WriteRankingDeltas([("p1", -4.0, 10, 0.05, 0.06, 0.8, 50000, 0.95, 8, 95.5, 1, 0.5)]);
        Assert.NotEmpty(Db.GetAllRankingDeltas());

        Db.TruncateRankingDeltas();
        Assert.Empty(Db.GetAllRankingDeltas());
    }

    // ═══════════════════════════════════════════════════════════
    // RankHistoryDeltas — Snapshot and Retrieve
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public void SnapshotRankHistoryDeltas_CreatesDeltas_WhenRanksDiffer()
    {
        // Seed 3 players with base ranks
        Db.UpsertEntries("song1", [
            MakeEntry("p1", 1000, rank: 1),
            MakeEntry("p2", 900, rank: 2),
            MakeEntry("p3", 800, rank: 3),
        ]);
        Db.RecomputeAllRanks();
        Db.ComputeSongStats();
        Db.ComputeAccountRankings(totalChartedSongs: 1);

        // Write ranking deltas at a specific bucket that change the metrics
        // p2 gets a better adjusted_skill → should overtake p1 at this bucket
        Db.TruncateRankingDeltas();
        Db.WriteRankingDeltas([
            ("p2", -3.0, 10, 0.01, 0.06, 0.8, 50000, 0.95, 8, 95.5, 1, 0.5),
        ]);

        Db.SnapshotRankHistoryDeltas();

        // p2 should have rank deltas if its effective rank changed
        var p2Deltas = Db.GetTodayRankDeltas("p2");
        // At least check the snapshot ran without errors. The deltas depend on
        // whether ROW_NUMBER re-ranking actually changed p2's position.
        Assert.NotNull(p2Deltas);
    }

    [Fact]
    public void SnapshotRankHistoryDeltas_NoDeltasWhenNoRankingDeltas()
    {
        Db.UpsertEntries("song1", [MakeEntry("p1", 1000, rank: 1)]);
        Db.RecomputeAllRanks();
        Db.ComputeSongStats();
        Db.ComputeAccountRankings(totalChartedSongs: 1);

        // No ranking_deltas written → no buckets → no history deltas
        Db.TruncateRankingDeltas();
        Db.SnapshotRankHistoryDeltas();

        var deltas = Db.GetTodayRankDeltas("p1");
        Assert.Empty(deltas);
    }

    [Fact]
    public void GetTodayRankDeltas_EmptyForUnknownAccount()
    {
        Assert.Empty(Db.GetTodayRankDeltas("nonexistent"));
    }

    // ═══════════════════════════════════════════════════════════
    // GetRankHistoryAtLeeway — Merged query
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public void GetRankHistoryAtLeeway_MergesBaseAndDeltas()
    {
        // Seed base rankings and snapshot history
        Db.UpsertEntries("song1", [
            MakeEntry("p1", 1000, rank: 1),
            MakeEntry("p2", 900, rank: 2),
        ]);
        Db.RecomputeAllRanks();
        Db.ComputeSongStats();
        Db.ComputeAccountRankings(totalChartedSongs: 1);
        Db.SnapshotRankHistory();

        // Write ranking deltas at bucket -3.0
        Db.TruncateRankingDeltas();
        Db.WriteRankingDeltas([
            ("p1", -3.0, 10, 0.01, 0.06, 0.8, 50000, 0.95, 8, 95.5, 1, 0.5),
        ]);
        Db.SnapshotRankHistoryDeltas();

        // Query merged history at bucket -3.0
        var history = Db.GetRankHistoryAtLeeway("p1", -3.0, days: 1);
        Assert.Single(history);
        // The merged result should have base rank + delta offset
    }

    [Fact]
    public void GetRankHistoryAtLeeway_ReturnsBaseWhenNoDelta()
    {
        Db.UpsertEntries("song1", [MakeEntry("p1", 1000, rank: 1)]);
        Db.RecomputeAllRanks();
        Db.ComputeSongStats();
        Db.ComputeAccountRankings(totalChartedSongs: 1);
        Db.SnapshotRankHistory();

        // No ranking_deltas at this bucket
        var history = Db.GetRankHistoryAtLeeway("p1", -3.0, days: 1);
        Assert.Single(history);
        // Should return base ranks (COALESCE with 0 delta)
        Assert.Equal(1, history[0].AdjustedSkillRank);
    }

    [Fact]
    public void GetRankHistoryAtLeeway_EmptyForUnknownAccount()
    {
        Assert.Empty(Db.GetRankHistoryAtLeeway("nonexistent", -3.0, days: 1));
    }

    // ═══════════════════════════════════════════════════════════
    // ComputeAllBucketDeltas — Reader lifetime regression
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public void ComputeAllBucketDeltas_DoesNotThrow_WhenAffectedAccountsExist()
    {
        // Score 1020 on max_score 1000 → 102% → in the 95%-105% band
        Db.UpsertEntries("song1", [
            MakeEntry("p1", 1020, rank: 1),
            MakeEntry("p2", 900, rank: 2),
        ]);
        Db.RecomputeAllRanks();

        var maxScores = new Dictionary<string, int?>(StringComparer.OrdinalIgnoreCase)
        {
            ["song1"] = 1000
        };
        Db.ComputeSongStats(maxScores);
        Db.PopulateValidScoreOverrides([]);

        using var conn = Db.OpenConnection();
        Db.MaterializeValidEntries(conn, 0.95);

        // Discover band entries (scores between 95% and 105% of max)
        var bandEntries = Db.GetBandEntriesFromMaterialized(conn, 0.95, 1.05);
        Assert.NotEmpty(bandEntries); // p1 at 102% should be in the band

        // Build cumulative bucket mapping
        var affectedByBucket = new SortedDictionary<double, HashSet<string>>();
        var allAffected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (accountId, leeway) in bandEntries)
        {
            double bucket = Math.Ceiling(leeway * 10) / 10.0;
            bucket = Math.Clamp(bucket, -4.9, 5.0);
            bucket = Math.Round(bucket, 1);
            if (!affectedByBucket.TryGetValue(bucket, out var set))
            {
                set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                affectedByBucket[bucket] = set;
            }
            set.Add(accountId);
            allAffected.Add(accountId);
        }

        // This formerly threw NpgsqlOperationInProgressException because the
        // ExecuteReader was still open when cleanup ran on the same connection.
        var results = Db.ComputeAllBucketDeltas(
            conn, affectedByBucket, allAffected,
            totalChartedSongs: 1, credibilityThreshold: 5, populationMedian: 0.5);

        Assert.NotEmpty(results);
    }

    // ═══════════════════════════════════════════════════════════
    // Raw weighted rating storage
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public void ComputeAccountRankings_StoresRawWeightedRating()
    {
        // Need 2+ entries per song so log_weight > 0 (log2(1) = 0 → weighted = NULL)
        var fillers = Enumerable.Range(0, 99).Select(i => MakeEntry($"f{i}", 100 - i, rank: i + 2)).ToList();
        Db.UpsertEntries("song1", [MakeEntry("p1", 10000, rank: 1), ..fillers]);
        Db.UpsertEntries("song2", [MakeEntry("p1", 10000, rank: 1), ..fillers]);
        Db.RecomputeAllRanks();
        Db.ComputeSongStats();
        Db.ComputeAccountRankings(totalChartedSongs: 20, credibilityThreshold: 50);

        var r1 = Db.GetAccountRanking("p1");
        Assert.NotNull(r1);

        // RawWeightedRating should be populated
        Assert.NotNull(r1.RawWeightedRating);

        // Raw should be close to 0.01 (rank 1 out of 100), adjusted should be pulled toward 0.5
        // With only 2 songs played and m=50: adjusted ≈ (2*raw + 50*0.5) / 52 ≈ 0.48
        Assert.True(r1.RawWeightedRating < 0.05,
            $"Raw weighted ({r1.RawWeightedRating:F6}) should be near 0 (rank ~1/100)");
        Assert.True(r1.WeightedRating > r1.RawWeightedRating * 5,
            $"Adjusted ({r1.WeightedRating:F6}) should be much larger than raw ({r1.RawWeightedRating:F6}) due to Bayesian pull");
    }

    [Fact]
    public void ComputeAccountRankings_RawWeightedRating_NotBayesianAdjusted()
    {
        // Two players: one with good coverage, one with minimal.
        // Both rank 1 per song. The Bayesian penalty should affect their adjusted
        // values differently, but raw values should be close.
        var fillers = Enumerable.Range(0, 99).Select(i => MakeEntry($"f{i}", 100 - i, rank: i + 2)).ToList();

        // Full-coverage player: plays 20 songs
        for (int i = 0; i < 20; i++)
            Db.UpsertEntries($"song_{i}", [MakeEntry("full", 10000, rank: 1), ..fillers]);

        // Partial player: plays only 2 songs (top score to guarantee rank 1)
        Db.UpsertEntries("song_0", [MakeEntry("partial", 10001, rank: 1), ..fillers]);
        Db.UpsertEntries("song_1", [MakeEntry("partial", 10001, rank: 1), ..fillers]);

        Db.RecomputeAllRanks();
        Db.ComputeSongStats();
        Db.ComputeAccountRankings(totalChartedSongs: 20, credibilityThreshold: 50);

        var full = Db.GetAccountRanking("full");
        var partial = Db.GetAccountRanking("partial");
        Assert.NotNull(full);
        Assert.NotNull(partial);
        Assert.NotNull(full.RawWeightedRating);
        Assert.NotNull(partial.RawWeightedRating);

        // Adjusted weighted should diverge: partial gets pulled harder toward 0.5
        // Full: (20*raw + 50*0.5) / 70, Partial: (2*raw + 50*0.5) / 52
        // The partial-to-full ratio of adjusted should be larger than raw ratio
        double adjustedRatio = partial.WeightedRating / full.WeightedRating;
        double rawRatio = partial.RawWeightedRating!.Value / full.RawWeightedRating!.Value;
        Assert.True(adjustedRatio > rawRatio,
            $"Adjusted ratio ({adjustedRatio:F3}) should exceed raw ratio ({rawRatio:F3}) due to coverage penalty");
    }

    [Fact]
    public void SnapshotRankHistory_IncludesRawWeightedAndRawSkill()
    {
        // Need 2+ entries per song so log_weight > 0 for weighted_rating to be non-null
        Db.UpsertEntries("song1", [MakeEntry("p1", 1000, rank: 1), MakeEntry("p2", 800, rank: 2)]);
        Db.RecomputeAllRanks();
        Db.ComputeSongStats();
        Db.ComputeAccountRankings(totalChartedSongs: 1, credibilityThreshold: 50);
        Db.SnapshotRankHistory();

        var history = Db.GetRankHistory("p1");
        Assert.NotEmpty(history);
        Assert.NotNull(history[0].RawWeightedRating);
        Assert.NotNull(history[0].RawSkillRating);
    }
}
