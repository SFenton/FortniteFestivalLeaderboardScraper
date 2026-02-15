using FSTService.Persistence;
using FSTService.Scraping;
using FSTService.Tests.Helpers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace FSTService.Tests.Unit;

/// <summary>
/// Tests for <see cref="GlobalLeaderboardPersistence"/> focused on PersistResult,
/// score change detection, and pipeline aggregation.
/// </summary>
public sealed class GlobalLeaderboardPersistenceTests : IDisposable
{
    private readonly InMemoryMetaDatabase _metaFixture = new();
    private readonly string _dataDir;

    public GlobalLeaderboardPersistenceTests()
    {
        _dataDir = Path.Combine(Path.GetTempPath(), $"fst_glp_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dataDir);
    }

    public void Dispose()
    {
        _metaFixture.Dispose();
        try { Directory.Delete(_dataDir, recursive: true); } catch { }
    }

    private GlobalLeaderboardPersistence CreatePersistence()
    {
        var loggerFactory = new NullLoggerFactory();
        var glp = new GlobalLeaderboardPersistence(
            _dataDir,
            _metaFixture.Db,
            loggerFactory,
            NullLogger<GlobalLeaderboardPersistence>.Instance);
        glp.Initialize();
        return glp;
    }

    private static GlobalLeaderboardResult MakeResult(
        string songId, string instrument, params (string AccountId, int Score)[] entries)
    {
        return new GlobalLeaderboardResult
        {
            SongId = songId,
            Instrument = instrument,
            Entries = entries.Select(e => new LeaderboardEntry
            {
                AccountId = e.AccountId,
                Score = e.Score,
                Accuracy = 95,
                IsFullCombo = false,
                Stars = 5,
                Season = 3,
                Percentile = 99.0,
            }).ToList(),
        };
    }

    // ═══ Basic Persistence ══════════════════════════════════════

    [Fact]
    public void PersistResult_inserts_entries()
    {
        using var glp = CreatePersistence();
        var result = MakeResult("song_1", "Solo_Guitar",
            ("acct_1", 100_000), ("acct_2", 90_000));

        var pr = glp.PersistResult(result);
        Assert.Equal(2, pr.RowsAffected);
    }

    [Fact]
    public void PersistResult_upserts_on_score_change()
    {
        using var glp = CreatePersistence();

        glp.PersistResult(MakeResult("song_1", "Solo_Guitar", ("acct_1", 80_000)));
        var pr = glp.PersistResult(MakeResult("song_1", "Solo_Guitar", ("acct_1", 100_000)));

        Assert.Equal(1, pr.RowsAffected);
    }

    [Fact]
    public void PersistResult_inserts_account_ids_into_meta()
    {
        using var glp = CreatePersistence();
        glp.PersistResult(MakeResult("song_1", "Solo_Guitar", ("acct_new", 100_000)));

        var unresolved = _metaFixture.Db.GetUnresolvedAccountIds();
        Assert.Contains("acct_new", unresolved);
    }

    // ═══ Score Change Detection ═════════════════════════════════

    [Fact]
    public void PersistResult_detects_score_change_for_registered_user()
    {
        using var glp = CreatePersistence();
        var registered = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "acct_1" };

        // Insert initial scores
        glp.PersistResult(MakeResult("song_1", "Solo_Guitar", ("acct_1", 80_000)));

        // Update with new scores
        var pr = glp.PersistResult(
            MakeResult("song_1", "Solo_Guitar", ("acct_1", 100_000)),
            registered);

        Assert.Equal(1, pr.ScoreChangesDetected);
        Assert.Contains("acct_1", pr.ChangedAccountIds);

        // Verify change was recorded in meta DB
        var history = _metaFixture.Db.GetScoreHistory("acct_1");
        Assert.Single(history);
        Assert.Equal(80_000, history[0].OldScore);
        Assert.Equal(100_000, history[0].NewScore);
    }

    [Fact]
    public void PersistResult_detects_new_entry_for_registered_user()
    {
        using var glp = CreatePersistence();
        var registered = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "acct_1" };

        var pr = glp.PersistResult(
            MakeResult("song_1", "Solo_Guitar", ("acct_1", 100_000)),
            registered);

        Assert.Equal(1, pr.ScoreChangesDetected);
        Assert.Contains("acct_1", pr.ChangedAccountIds);

        var history = _metaFixture.Db.GetScoreHistory("acct_1");
        Assert.Single(history);
        Assert.Null(history[0].OldScore);
        Assert.Equal(100_000, history[0].NewScore);
    }

    [Fact]
    public void PersistResult_no_change_for_same_score()
    {
        using var glp = CreatePersistence();
        var registered = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "acct_1" };

        glp.PersistResult(MakeResult("song_1", "Solo_Guitar", ("acct_1", 100_000)));
        var pr = glp.PersistResult(
            MakeResult("song_1", "Solo_Guitar", ("acct_1", 100_000)),
            registered);

        Assert.Equal(0, pr.ScoreChangesDetected);
        Assert.Empty(pr.ChangedAccountIds);
    }

    [Fact]
    public void PersistResult_ignores_unregistered_users()
    {
        using var glp = CreatePersistence();
        var registered = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "acct_tracked" };

        // Only acct_untracked changes, but it's not registered
        glp.PersistResult(MakeResult("song_1", "Solo_Guitar",
            ("acct_untracked", 50_000), ("acct_tracked", 100_000)));

        var pr = glp.PersistResult(
            MakeResult("song_1", "Solo_Guitar",
                ("acct_untracked", 60_000), ("acct_tracked", 100_000)),
            registered);

        Assert.Equal(0, pr.ScoreChangesDetected);
    }

    // ═══ Multi-Instrument Support ═══════════════════════════════

    [Fact]
    public void PersistResult_works_across_instruments()
    {
        using var glp = CreatePersistence();

        glp.PersistResult(MakeResult("song_1", "Solo_Guitar", ("acct_1", 100_000)));
        glp.PersistResult(MakeResult("song_1", "Solo_Bass", ("acct_1", 90_000)));

        var counts = glp.GetEntryCounts();
        Assert.Equal(1, counts["Solo_Guitar"]);
        Assert.Equal(1, counts["Solo_Bass"]);
    }

    // ═══ GetPlayerProfile ═══════════════════════════════════════

    [Fact]
    public void GetPlayerProfile_aggregates_across_instruments()
    {
        using var glp = CreatePersistence();

        glp.PersistResult(MakeResult("song_1", "Solo_Guitar", ("acct_1", 100_000)));
        glp.PersistResult(MakeResult("song_1", "Solo_Bass", ("acct_1", 90_000)));
        glp.PersistResult(MakeResult("song_2", "Solo_Guitar", ("acct_1", 80_000)));

        var profile = glp.GetPlayerProfile("acct_1");
        Assert.Equal(3, profile.Count);
    }

    [Fact]
    public void GetPlayerProfile_returns_empty_for_unknown_player()
    {
        using var glp = CreatePersistence();
        var profile = glp.GetPlayerProfile("nobody");
        Assert.Empty(profile);
    }

    // ═══ GetLeaderboard ═════════════════════════════════════════

    [Fact]
    public void GetLeaderboard_returns_sorted_entries()
    {
        using var glp = CreatePersistence();

        glp.PersistResult(MakeResult("song_1", "Solo_Guitar",
            ("acct_low", 50_000), ("acct_high", 100_000)));

        var board = glp.GetLeaderboard("song_1", "Solo_Guitar");
        Assert.NotNull(board);
        Assert.Equal(2, board.Count);
        Assert.Equal("acct_high", board[0].AccountId);
        Assert.Equal("acct_low", board[1].AccountId);
    }

    [Fact]
    public void GetLeaderboard_returns_null_for_unknown_instrument()
    {
        using var glp = CreatePersistence();
        var board = glp.GetLeaderboard("song_1", "NonExistentInstrument");
        Assert.Null(board);
    }

    // ═══ Pipeline Aggregates ════════════════════════════════════

    [Fact]
    public void PipelineAggregates_tracks_seen_registered_entries()
    {
        var agg = new GlobalLeaderboardPersistence.PipelineAggregates();

        agg.AddSeenRegisteredEntries([
            ("acct_1", "song_1", "Solo_Guitar"),
            ("acct_1", "song_2", "Solo_Guitar"),
            ("acct_2", "song_1", "Solo_Bass"),
        ]);

        Assert.Equal(3, agg.SeenRegisteredEntries.Count);
    }

    [Fact]
    public void PipelineAggregates_thread_safe_counters()
    {
        var agg = new GlobalLeaderboardPersistence.PipelineAggregates();

        Parallel.For(0, 100, _ =>
        {
            agg.AddEntries(1);
            agg.AddChanges(1);
            agg.IncrementSongsWithData();
        });

        Assert.Equal(100, agg.TotalEntries);
        Assert.Equal(100, agg.TotalChanges);
        Assert.Equal(100, agg.SongsWithData);
    }

    [Fact]
    public void PipelineAggregates_changed_account_ids_deduplicates()
    {
        var agg = new GlobalLeaderboardPersistence.PipelineAggregates();

        agg.AddChangedAccountIds(["acct_1", "acct_2"]);
        agg.AddChangedAccountIds(["acct_1", "acct_3"]);

        Assert.Equal(3, agg.ChangedAccountIds.Count);
    }
}
