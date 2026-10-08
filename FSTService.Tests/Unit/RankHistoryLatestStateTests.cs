using FSTService.Scraping;
using FSTService.Persistence;
using FSTService.Tests.Helpers;
using Npgsql;

namespace FSTService.Tests.Unit;

public sealed class RankHistoryLatestStateTests : IDisposable
{
    private readonly TempInstrumentDatabase _latest = new();
    private readonly TempInstrumentDatabase _legacy = new();
    private readonly InMemoryMetaDatabase _latestMeta = new();
    private readonly InMemoryMetaDatabase _legacyMeta = new();

    public void Dispose()
    {
        _latest.Dispose();
        _legacy.Dispose();
        _latestMeta.Dispose();
        _legacyMeta.Dispose();
    }

    [Fact]
    public void Instrument_snapshots_from_latest_state_match_full_history_scans()
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        foreach (var db in new[] { _latest, _legacy })
        {
            for (var i = 0; i < 6; i++)
                db.Db.UpsertEntries($"song{i % 3}", [Entry($"p{i}", 1_000 - (i * 37))]);
            db.Db.RecomputeAllRanks();
            db.Db.ComputeSongStats();
            db.Db.ComputeAccountRankings(totalChartedSongs: 3);
            AlignComputedAt(db.DataSource);

            // Older history: an unchanged account, a changed account, one that is no
            // longer ranked, and a newer row that is not the oldest.
            CopyRankingToHistory(db.DataSource, "p0", today.AddDays(-5));
            CopyRankingToHistory(db.DataSource, "p1", today.AddDays(-9), adjust: true);
            CopyRankingToHistory(db.DataSource, "p1", today.AddDays(-3), adjust: true);
            CopyRankingToHistory(db.DataSource, "p2", today.AddDays(-2));
            InsertHistoryRow(db.DataSource, "gone", today.AddDays(-4));
        }

        var latestRows = _latest.Db.SnapshotRankHistory(cleanupRetention: false, useLatestState: true);
        var legacyRows = _legacy.Db.SnapshotRankHistory(cleanupRetention: false);
        Assert.Equal(legacyRows, latestRows);
        AssertSameHistory();
        Assert.True(IsReady(_latest.DataSource, "Solo_Guitar"));
        AssertLatestMatchesHistory(_latest.DataSource);

        // Later the same day: some ranks move, one account disappears, one is new.
        foreach (var db in new[] { _latest, _legacy })
        {
            Execute(db.DataSource, "UPDATE account_rankings SET adjusted_skill_rank = adjusted_skill_rank + 10 WHERE account_id IN ('p3', 'p4')");
            Execute(db.DataSource, "DELETE FROM account_rankings WHERE account_id = 'p5'");
            Execute(db.DataSource, """
                INSERT INTO account_rankings (account_id, instrument, songs_played, total_charted_songs, coverage, raw_skill_rating,
                    adjusted_skill_rating, adjusted_skill_rank, weighted_rating, weighted_rank, fc_rate, fc_rate_rank, total_score,
                    total_score_rank, max_score_percent, max_score_percent_rank, avg_accuracy, full_combo_count, avg_stars,
                    best_rank, avg_rank, computed_at)
                SELECT 'p9', instrument, songs_played, total_charted_songs, coverage, raw_skill_rating,
                    adjusted_skill_rating, 99, weighted_rating, 99, fc_rate, 99, total_score,
                    99, max_score_percent, 99, avg_accuracy, full_combo_count, avg_stars, best_rank, avg_rank, computed_at
                FROM account_rankings WHERE account_id = 'p0'
                """);
        }

        latestRows = _latest.Db.SnapshotRankHistory(cleanupRetention: false, useLatestState: true);
        legacyRows = _legacy.Db.SnapshotRankHistory(cleanupRetention: false);
        Assert.Equal(legacyRows, latestRows);
        AssertSameHistory();
        AssertLatestMatchesHistory(_latest.DataSource);

        // An identical rerun writes nothing new.
        _latest.Db.SnapshotRankHistory(cleanupRetention: false, useLatestState: true);
        _legacy.Db.SnapshotRankHistory(cleanupRetention: false);
        AssertSameHistory();
    }

    [Fact]
    public void Snapshot_without_latest_state_drops_readiness_and_the_next_one_rebuilds()
    {
        _latest.Db.UpsertEntries("song0", [Entry("p0", 1_000), Entry("p1", 900)]);
        _latest.Db.RecomputeAllRanks();
        _latest.Db.ComputeSongStats();
        _latest.Db.ComputeAccountRankings(totalChartedSongs: 1);

        _latest.Db.SnapshotRankHistory(cleanupRetention: false, useLatestState: true);
        Assert.True(IsReady(_latest.DataSource, "Solo_Guitar"));

        Execute(_latest.DataSource, "UPDATE account_rankings SET adjusted_skill_rank = adjusted_skill_rank + 5 WHERE account_id = 'p0'");
        _latest.Db.SnapshotRankHistory(cleanupRetention: false);
        Assert.False(IsReady(_latest.DataSource, "Solo_Guitar"));

        Execute(_latest.DataSource, "UPDATE account_rankings SET adjusted_skill_rank = adjusted_skill_rank + 5 WHERE account_id = 'p1'");
        _latest.Db.SnapshotRankHistory(cleanupRetention: false, useLatestState: true);
        Assert.True(IsReady(_latest.DataSource, "Solo_Guitar"));
        AssertLatestMatchesHistory(_latest.DataSource);
    }

    [Fact]
    public void Composite_snapshots_from_latest_state_match_full_history_scans()
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        foreach (var fixture in new[] { _latestMeta, _legacyMeta })
        {
            fixture.Db.ReplaceCompositeRankings([
                Composite("c0", 1, 0.10),
                Composite("c1", 2, 0.20),
                Composite("c2", 3, 0.30),
                Composite("c3", 4, 0.40),
            ]);
            InsertCompositeHistory(fixture.DataSource, "c0", today.AddDays(-4), 1, 0.10);
            InsertCompositeHistory(fixture.DataSource, "c1", today.AddDays(-6), 9, 0.90);
            InsertCompositeHistory(fixture.DataSource, "c1", today.AddDays(-2), 5, 0.50);
            InsertCompositeHistory(fixture.DataSource, "gone", today.AddDays(-3), 7, 0.70);
        }

        _latestMeta.Db.SnapshotCompositeRankHistory(cleanupRetention: false, useLatestState: true);
        _legacyMeta.Db.SnapshotCompositeRankHistory(cleanupRetention: false);
        Assert.Equal(CompositeHistory(_legacyMeta.DataSource), CompositeHistory(_latestMeta.DataSource));
        Assert.True(IsReady(_latestMeta.DataSource, RankHistoryLatestStateSchema.CompositeScope));

        foreach (var fixture in new[] { _latestMeta, _legacyMeta })
        {
            fixture.Db.ReplaceCompositeRankings([
                Composite("c0", 1, 0.10),
                Composite("c1", 3, 0.25),
                Composite("c3", 2, 0.40),
                Composite("c4", 4, 0.45),
            ]);
        }

        _latestMeta.Db.SnapshotCompositeRankHistory(cleanupRetention: false, useLatestState: true);
        _legacyMeta.Db.SnapshotCompositeRankHistory(cleanupRetention: false);
        Assert.Equal(CompositeHistory(_legacyMeta.DataSource), CompositeHistory(_latestMeta.DataSource));
        Assert.Equal(
            Query(_latestMeta.DataSource, """
                SELECT string_agg(format('%s|%s|%s|%s', account_id, snapshot_date, composite_rank, composite_rating), ',' ORDER BY account_id)
                FROM (SELECT DISTINCT ON (account_id) * FROM composite_rank_history ORDER BY account_id, snapshot_date DESC) h
                """),
            Query(_latestMeta.DataSource, """
                SELECT string_agg(format('%s|%s|%s|%s', account_id, snapshot_date, composite_rank, composite_rating), ',' ORDER BY account_id)
                FROM composite_rank_history_latest
                """));
    }

    private void AssertSameHistory() =>
        Assert.Equal(History(_legacy.DataSource), History(_latest.DataSource));

    private static void AssertLatestMatchesHistory(NpgsqlDataSource ds) =>
        Assert.Equal(
            Query(ds, $"""
                SELECT string_agg({RowFormat}, E'\n' ORDER BY account_id)
                FROM (SELECT DISTINCT ON (account_id) * FROM rank_history
                      WHERE instrument = 'Solo_Guitar' ORDER BY account_id, snapshot_date DESC) h
                """),
            Query(ds, $"""
                SELECT string_agg({RowFormat}, E'\n' ORDER BY account_id)
                FROM rank_history_latest h WHERE instrument = 'Solo_Guitar'
                """));

    private const string RowFormat = """
        format('%s|%s|%s|%s|%s|%s|%s|%s|%s|%s|%s|%s|%s|%s|%s|%s|%s|%s|%s', h.account_id, h.snapshot_date, h.snapshot_taken_at,
            h.adjusted_skill_rank, h.weighted_rank, h.fc_rate_rank, h.total_score_rank, h.max_score_percent_rank,
            h.adjusted_skill_rating, h.weighted_rating, h.fc_rate, h.total_score, h.max_score_percent, h.songs_played,
            h.coverage, h.full_combo_count, h.raw_max_score_percent, h.raw_weighted_rating, h.schema_version)
        """;

    private static string History(NpgsqlDataSource ds) =>
        Query(ds, $"SELECT string_agg({RowFormat}, E'\\n' ORDER BY h.account_id, h.snapshot_date) FROM rank_history h");

    private static string CompositeHistory(NpgsqlDataSource ds) =>
        Query(ds, """
            SELECT string_agg(format('%s|%s|%s|%s|%s|%s', account_id, snapshot_date, composite_rank, composite_rating,
                instruments_played, total_songs_played), E'\n' ORDER BY account_id, snapshot_date)
            FROM composite_rank_history
            """);

    private static bool IsReady(NpgsqlDataSource ds, string scope) =>
        Query(ds, $"SELECT COALESCE((SELECT ready::text FROM rank_history_latest_state WHERE scope = '{scope}'), 'false')") == "true";

    private static void AlignComputedAt(NpgsqlDataSource ds) =>
        Execute(ds, "UPDATE account_rankings SET computed_at = TIMESTAMPTZ '2026-10-08 00:00:00+00'");

    private static void CopyRankingToHistory(NpgsqlDataSource ds, string accountId, DateOnly date, bool adjust = false) =>
        Execute(ds, $"""
            INSERT INTO rank_history (account_id, instrument, snapshot_date, snapshot_taken_at,
                adjusted_skill_rank, weighted_rank, fc_rate_rank, total_score_rank, max_score_percent_rank,
                adjusted_skill_rating, weighted_rating, fc_rate, total_score, max_score_percent,
                songs_played, coverage, full_combo_count, raw_max_score_percent, raw_weighted_rating, raw_skill_rating, schema_version)
            SELECT account_id, instrument, DATE '{date:yyyy-MM-dd}', computed_at,
                adjusted_skill_rank + {(adjust ? date.DayNumber % 7 + 1 : 0)}, weighted_rank, fc_rate_rank, total_score_rank, max_score_percent_rank,
                adjusted_skill_rating, weighted_rating, fc_rate, total_score, max_score_percent,
                songs_played, coverage, full_combo_count, raw_max_score_percent, raw_weighted_rating, raw_skill_rating, 2
            FROM account_rankings WHERE account_id = '{accountId}'
            """);

    private static void InsertHistoryRow(NpgsqlDataSource ds, string accountId, DateOnly date) =>
        Execute(ds, $"""
            INSERT INTO rank_history (account_id, instrument, snapshot_date, adjusted_skill_rank, weighted_rank,
                fc_rate_rank, total_score_rank, max_score_percent_rank)
            VALUES ('{accountId}', 'Solo_Guitar', DATE '{date:yyyy-MM-dd}', 50, 50, 50, 50, 50)
            """);

    private static void InsertCompositeHistory(NpgsqlDataSource ds, string accountId, DateOnly date, int rank, double rating) =>
        Execute(ds, $"""
            INSERT INTO composite_rank_history (account_id, snapshot_date, composite_rank, composite_rating, instruments_played, total_songs_played)
            VALUES ('{accountId}', DATE '{date:yyyy-MM-dd}', {rank}, {rating.ToString(System.Globalization.CultureInfo.InvariantCulture)}, 1, 10)
            """);

    private static CompositeRankingDto Composite(string accountId, int rank, double rating) => new()
    {
        AccountId = accountId,
        InstrumentsPlayed = 1,
        TotalSongsPlayed = 10,
        CompositeRating = rating,
        CompositeRank = rank,
    };

    private static LeaderboardEntry Entry(string accountId, int score) => new()
    {
        AccountId = accountId,
        Score = score,
        Accuracy = 95,
        IsFullCombo = false,
        Stars = 5,
    };

    private static void Execute(NpgsqlDataSource ds, string sql)
    {
        using var conn = ds.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static string Query(NpgsqlDataSource ds, string sql)
    {
        using var conn = ds.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return cmd.ExecuteScalar() as string ?? string.Empty;
    }
}
