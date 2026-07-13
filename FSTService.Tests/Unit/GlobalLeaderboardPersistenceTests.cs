using FSTService.Persistence;
using FSTService.Scraping;
using FSTService.Tests.Helpers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using System.Text.Json;

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

    private GlobalLeaderboardPersistence CreatePersistence(FeatureOptions? features = null)
    {
        var loggerFactory = new NullLoggerFactory();
        var glp = new GlobalLeaderboardPersistence(
            _metaFixture.Db,
            loggerFactory,
            NullLogger<GlobalLeaderboardPersistence>.Instance,
            _metaFixture.DataSource,
            Options.Create(features ?? new FeatureOptions()));
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

    private int GetSnapshotRowCount(long snapshotId, string songId, string instrument)
    {
        using var conn = _metaFixture.DataSource.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM leaderboard_entries_snapshot WHERE snapshot_id = @snapshotId AND song_id = @songId AND instrument = @instrument";
        cmd.Parameters.AddWithValue("snapshotId", snapshotId);
        cmd.Parameters.AddWithValue("songId", songId);
        cmd.Parameters.AddWithValue("instrument", instrument);
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    private int GetLiveRowCount(string songId, string instrument)
    {
        using var conn = _metaFixture.DataSource.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM leaderboard_entries WHERE song_id = @songId AND instrument = @instrument";
        cmd.Parameters.AddWithValue("songId", songId);
        cmd.Parameters.AddWithValue("instrument", instrument);
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    private (long FirstSeenScrapeId, long LastChangedScrapeId, long LastSeenScrapeId, int EntryCount)? GetScopeFingerprint(
        string songId,
        string instrument)
    {
        using var conn = _metaFixture.DataSource.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT first_seen_scrape_id, last_changed_scrape_id, last_seen_scrape_id, entry_count
            FROM leaderboard_scope_fingerprints
            WHERE song_id = @songId
              AND instrument = @instrument
              AND scope_kind = 'alltime'
            """;
        cmd.Parameters.AddWithValue("songId", songId);
        cmd.Parameters.AddWithValue("instrument", instrument);
        using var reader = cmd.ExecuteReader();
        if (!reader.Read())
            return null;

        return (reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2), reader.GetInt32(3));
    }

    private (long? PublishedScrapeId, long? ReportedTotalEntries, int? ReportedTotalPages, bool IsComplete)?
        GetScopeCoverage(string songId, string instrument)
    {
        using var conn = _metaFixture.DataSource.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT published_scrape_id, reported_total_entries, reported_total_pages, is_complete
            FROM leaderboard_scope_fingerprints
            WHERE song_id = @songId
              AND instrument = @instrument
              AND scope_kind = 'alltime'
            """;
        cmd.Parameters.AddWithValue("songId", songId);
        cmd.Parameters.AddWithValue("instrument", instrument);
        using var reader = cmd.ExecuteReader();
        if (!reader.Read())
            return null;

        return (
            reader.IsDBNull(0) ? null : reader.GetInt64(0),
            reader.IsDBNull(1) ? null : reader.GetInt64(1),
            reader.IsDBNull(2) ? null : reader.GetInt32(2),
            reader.GetBoolean(3));
    }

    private (int[] ReceivedPages, string ParseStatus, bool RetryExhausted, bool IsComplete, string? FailureReason)?
        GetScopeManifest(long scrapeId, string songId, string instrument)
    {
        using var conn = _metaFixture.DataSource.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT received_pages, parse_status, retry_exhausted, is_complete, failure_reason
            FROM leaderboard_scope_manifests
            WHERE scrape_id = @scrapeId
              AND song_id = @songId
              AND instrument = @instrument
              AND scope_kind = 'alltime'
            """;
        cmd.Parameters.AddWithValue("scrapeId", scrapeId);
        cmd.Parameters.AddWithValue("songId", songId);
        cmd.Parameters.AddWithValue("instrument", instrument);
        using var reader = cmd.ExecuteReader();
        if (!reader.Read())
            return null;
        return (
            reader.GetFieldValue<int[]>(0),
            reader.GetString(1),
            reader.GetBoolean(2),
            reader.GetBoolean(3),
            reader.IsDBNull(4) ? null : reader.GetString(4));
    }

    private void InsertScrapeLog(long scrapeId, bool completed)
    {
        using var conn = _metaFixture.DataSource.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO scrape_log (id, started_at, completed_at, status)
            VALUES (
                @scrapeId,
                now(),
                CASE WHEN @completed THEN now() ELSE NULL END,
                CASE WHEN @completed THEN 'completed' ELSE 'running' END)
            ON CONFLICT (id) DO UPDATE SET
                completed_at = EXCLUDED.completed_at,
                status = EXCLUDED.status
            """;
        cmd.Parameters.AddWithValue("scrapeId", (int)scrapeId);
        cmd.Parameters.AddWithValue("completed", completed);
        cmd.ExecuteNonQuery();
    }

    private void DeleteScrapeLog(long scrapeId)
    {
        using var conn = _metaFixture.DataSource.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM scrape_log WHERE id = @scrapeId";
        cmd.Parameters.AddWithValue("scrapeId", (int)scrapeId);
        cmd.ExecuteNonQuery();
    }

    private (int Score, long FirstSeenScrapeId, long LastChangedScrapeId)? GetLogicalCurrentRow(
        string songId,
        string instrument,
        string accountId)
    {
        using var conn = _metaFixture.DataSource.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT score, first_seen_scrape_id, last_changed_scrape_id
            FROM leaderboard_current_entries
            WHERE song_id = @songId
              AND instrument = @instrument
              AND account_id = @accountId
            """;
        cmd.Parameters.AddWithValue("songId", songId);
        cmd.Parameters.AddWithValue("instrument", instrument);
        cmd.Parameters.AddWithValue("accountId", accountId);
        using var reader = cmd.ExecuteReader();
        if (!reader.Read())
            return null;

        return (reader.GetInt32(0), reader.GetInt64(1), reader.GetInt64(2));
    }

    private (int Total, int Open, int Closed) GetLogicalVersionCounts(
        string songId,
        string instrument,
        string accountId)
    {
        using var conn = _metaFixture.DataSource.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT
                COUNT(*)::int,
                COUNT(*) FILTER (WHERE valid_to_scrape_id IS NULL)::int,
                COUNT(*) FILTER (WHERE valid_to_scrape_id IS NOT NULL)::int
            FROM leaderboard_entry_versions
            WHERE song_id = @songId
              AND instrument = @instrument
              AND account_id = @accountId
            """;
        cmd.Parameters.AddWithValue("songId", songId);
        cmd.Parameters.AddWithValue("instrument", instrument);
        cmd.Parameters.AddWithValue("accountId", accountId);
        using var reader = cmd.ExecuteReader();
        Assert.True(reader.Read());

        return (reader.GetInt32(0), reader.GetInt32(1), reader.GetInt32(2));
    }

    private sealed record LogicalWriteMetrics(
        int FlushCount,
        long ObservedRows,
        long NewRows,
        long ChangedRows,
        long UnchangedRows,
        long CurrentUpserts,
        long VersionsClosed,
        long VersionsOpened);

    private LogicalWriteMetrics? GetLogicalWriteMetrics(long scrapeId, string instrument)
    {
        using var conn = _metaFixture.DataSource.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT flush_count,
                   observed_rows,
                   new_rows,
                   changed_rows,
                   unchanged_rows,
                   current_upserts,
                   versions_closed,
                   versions_opened
            FROM leaderboard_logical_write_metrics
            WHERE scrape_id = @scrapeId
              AND instrument = @instrument
            """;
        cmd.Parameters.AddWithValue("scrapeId", scrapeId);
        cmd.Parameters.AddWithValue("instrument", instrument);
        using var reader = cmd.ExecuteReader();
        if (!reader.Read())
            return null;

        return new LogicalWriteMetrics(
            reader.GetInt32(0),
            reader.GetInt64(1),
            reader.GetInt64(2),
            reader.GetInt64(3),
            reader.GetInt64(4),
            reader.GetInt64(5),
            reader.GetInt64(6),
            reader.GetInt64(7));
    }

    [Fact]
    public async Task CleanupActiveScrapeWritersAsync_DisposesActiveDiskSpool()
    {
        using var glp = CreatePersistence();
        var spoolRoot = Path.Combine(_dataDir, "spool");
        var spool = glp.StartSpoolWriter(1001, spoolRoot);
        spool.Enqueue("song-a", "Solo_Guitar", new[]
        {
            new LeaderboardEntry
            {
                AccountId = "account-a",
                Score = 12345,
                Accuracy = 95,
                Stars = 5,
                Season = 34,
                Difficulty = 3,
                Percentile = 90,
                Rank = 1,
                Source = "scrape",
            },
        });
        var spoolDir = spool.SpoolDirectory;
        Assert.True(Directory.Exists(spoolDir));

        await glp.CleanupActiveScrapeWritersAsync();
        await glp.CleanupActiveScrapeWritersAsync();

        Assert.False(Directory.Exists(spoolDir));
    }

    private (long ActiveSnapshotId, long ScrapeId, bool IsFinalized)? GetSnapshotState(string songId, string instrument)
    {
        using var conn = _metaFixture.DataSource.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT active_snapshot_id, scrape_id, is_finalized FROM leaderboard_snapshot_state WHERE song_id = @songId AND instrument = @instrument";
        cmd.Parameters.AddWithValue("songId", songId);
        cmd.Parameters.AddWithValue("instrument", instrument);
        using var reader = cmd.ExecuteReader();
        if (!reader.Read())
            return null;

        return (reader.GetInt64(0), reader.GetInt64(1), reader.GetBoolean(2));
    }

    private List<LeaderboardEntryDto> GetCurrentState(GlobalLeaderboardPersistence glp, string songId, string instrument)
        => glp.GetCurrentStateLeaderboard(songId, instrument, top: 10) ?? [];

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
    public void InstrumentDatabase_bulk_upsert_skips_unchanged_conflicts()
    {
        using var glp = CreatePersistence();
        var db = glp.GetOrCreateInstrumentDb("Solo_Guitar");
        var entries = Enumerable.Range(1, InstrumentDatabase.BulkThreshold + 10)
            .Select(index => new LeaderboardEntry
            {
                AccountId = $"acct_{index:D3}",
                Score = 100_000 - index,
                Accuracy = 95,
                IsFullCombo = index % 2 == 0,
                Stars = 5,
                Season = 3,
                Difficulty = 3,
                Percentile = 90.0,
                Rank = index,
                ApiRank = index,
                Source = "scrape",
            })
            .ToList();

        var inserted = db.UpsertEntries("song_bulk", entries);
        var unchanged = db.UpsertEntries("song_bulk", entries);

        Assert.Equal(entries.Count, inserted);
        Assert.Equal(0, unchanged);
    }

    [Fact]
    public void PersistResult_inserts_account_ids_into_meta()
    {
        using var glp = CreatePersistence();
        glp.PersistResult(MakeResult("song_1", "Solo_Guitar", ("acct_new", 100_000)));

        var unresolved = _metaFixture.Db.GetUnresolvedAccountIds();
        Assert.Contains("acct_new", unresolved);
    }

    [Fact]
    public async Task FlushSpoolAsync_shadow_writes_snapshot_rows_for_scrape_run()
    {
        using var glp = CreatePersistence();

        glp.StartSpoolWriter(42, _dataDir);
        glp.EnqueueSpoolPage("song_1", "Solo_Guitar",
        [
            new LeaderboardEntry
            {
                AccountId = "acct_1",
                Score = 100_000,
                Accuracy = 95,
                Stars = 5,
                Season = 3,
                Difficulty = 3,
                Percentile = 99.0,
                Rank = 1,
                ApiRank = 1,
                Source = "scrape",
                EndTime = "2025-01-15T12:00:00Z",
            }
        ]);

        await glp.FlushSpoolAsync();

        Assert.Equal(1, GetSnapshotRowCount(42, "song_1", "Solo_Guitar"));
    }

    [Fact]
    public async Task FlushSpoolAsync_records_observe_only_scope_fingerprints()
    {
        using var glp = CreatePersistence();

        glp.StartSpoolWriter(42, _dataDir);
        glp.EnqueueSpoolPage("song_1", "Solo_Guitar",
        [
            new LeaderboardEntry
            {
                AccountId = "acct_1",
                Score = 100_000,
                Accuracy = 95,
                Stars = 5,
                Season = 3,
                Difficulty = 3,
                Percentile = 99.0,
                Rank = 1,
                ApiRank = 1,
                Source = "scrape",
            },
        ]);
        await glp.FlushSpoolAsync();

        glp.StartSpoolWriter(43, _dataDir);
        glp.EnqueueSpoolPage("song_1", "Solo_Guitar",
        [
            new LeaderboardEntry
            {
                AccountId = "acct_1",
                Score = 100_000,
                Accuracy = 95,
                Stars = 5,
                Season = 3,
                Difficulty = 3,
                Percentile = 99.0,
                Rank = 1,
                ApiRank = 1,
                Source = "scrape",
            },
        ]);
        await glp.FlushSpoolAsync();

        var unchanged = GetScopeFingerprint("song_1", "Solo_Guitar");
        Assert.NotNull(unchanged);
        Assert.Equal(42, unchanged.Value.FirstSeenScrapeId);
        Assert.Equal(42, unchanged.Value.LastChangedScrapeId);
        Assert.Equal(43, unchanged.Value.LastSeenScrapeId);
        Assert.Equal(1, unchanged.Value.EntryCount);

        glp.StartSpoolWriter(44, _dataDir);
        glp.EnqueueSpoolPage("song_1", "Solo_Guitar",
        [
            new LeaderboardEntry
            {
                AccountId = "acct_1",
                Score = 101_000,
                Accuracy = 95,
                Stars = 5,
                Season = 3,
                Difficulty = 3,
                Percentile = 99.0,
                Rank = 1,
                ApiRank = 1,
                Source = "scrape",
            },
        ]);
        await glp.FlushSpoolAsync();

        var changed = GetScopeFingerprint("song_1", "Solo_Guitar");
        Assert.NotNull(changed);
        Assert.Equal(42, changed.Value.FirstSeenScrapeId);
        Assert.Equal(44, changed.Value.LastChangedScrapeId);
        Assert.Equal(44, changed.Value.LastSeenScrapeId);
    }

    [Fact]
    public async Task FlushSpoolAsync_dual_writes_logical_current_and_versions()
    {
        using var glp = CreatePersistence();

        glp.StartSpoolWriter(42, _dataDir);
        glp.EnqueueSpoolPage("song_1", "Solo_Guitar",
        [
            new LeaderboardEntry
            {
                AccountId = "acct_1",
                Score = 100_000,
                Accuracy = 95,
                Stars = 5,
                Season = 3,
                Difficulty = 3,
                Percentile = 99.0,
                Rank = 1,
                ApiRank = 1,
                Source = "scrape",
            },
        ]);
        await glp.FlushSpoolAsync();

        var firstCurrent = GetLogicalCurrentRow("song_1", "Solo_Guitar", "acct_1");
        Assert.NotNull(firstCurrent);
        Assert.Equal(100_000, firstCurrent.Value.Score);
        Assert.Equal(42, firstCurrent.Value.FirstSeenScrapeId);
        Assert.Equal(42, firstCurrent.Value.LastChangedScrapeId);
        Assert.Equal((Total: 1, Open: 1, Closed: 0), GetLogicalVersionCounts("song_1", "Solo_Guitar", "acct_1"));
        Assert.Equal(new LogicalWriteMetrics(
            FlushCount: 1,
            ObservedRows: 1,
            NewRows: 1,
            ChangedRows: 0,
            UnchangedRows: 0,
            CurrentUpserts: 1,
            VersionsClosed: 0,
            VersionsOpened: 1), GetLogicalWriteMetrics(42, "Solo_Guitar"));

        glp.StartSpoolWriter(43, _dataDir);
        glp.EnqueueSpoolPage("song_1", "Solo_Guitar",
        [
            new LeaderboardEntry
            {
                AccountId = "acct_1",
                Score = 100_000,
                Accuracy = 95,
                Stars = 5,
                Season = 3,
                Difficulty = 3,
                Percentile = 99.0,
                Rank = 1,
                ApiRank = 1,
                Source = "scrape",
            },
        ]);
        await glp.FlushSpoolAsync();

        var unchangedCurrent = GetLogicalCurrentRow("song_1", "Solo_Guitar", "acct_1");
        Assert.NotNull(unchangedCurrent);
        Assert.Equal(100_000, unchangedCurrent.Value.Score);
        Assert.Equal(42, unchangedCurrent.Value.FirstSeenScrapeId);
        Assert.Equal(42, unchangedCurrent.Value.LastChangedScrapeId);
        Assert.Equal((Total: 1, Open: 1, Closed: 0), GetLogicalVersionCounts("song_1", "Solo_Guitar", "acct_1"));
        Assert.Equal(new LogicalWriteMetrics(
            FlushCount: 1,
            ObservedRows: 1,
            NewRows: 0,
            ChangedRows: 0,
            UnchangedRows: 1,
            CurrentUpserts: 0,
            VersionsClosed: 0,
            VersionsOpened: 0), GetLogicalWriteMetrics(43, "Solo_Guitar"));

        glp.StartSpoolWriter(44, _dataDir);
        glp.EnqueueSpoolPage("song_1", "Solo_Guitar",
        [
            new LeaderboardEntry
            {
                AccountId = "acct_1",
                Score = 101_000,
                Accuracy = 95,
                Stars = 5,
                Season = 3,
                Difficulty = 3,
                Percentile = 99.0,
                Rank = 1,
                ApiRank = 1,
                Source = "scrape",
            },
        ]);
        await glp.FlushSpoolAsync();

        var changedCurrent = GetLogicalCurrentRow("song_1", "Solo_Guitar", "acct_1");
        Assert.NotNull(changedCurrent);
        Assert.Equal(101_000, changedCurrent.Value.Score);
        Assert.Equal(42, changedCurrent.Value.FirstSeenScrapeId);
        Assert.Equal(44, changedCurrent.Value.LastChangedScrapeId);
        Assert.Equal((Total: 2, Open: 1, Closed: 1), GetLogicalVersionCounts("song_1", "Solo_Guitar", "acct_1"));
        Assert.Equal(new LogicalWriteMetrics(
            FlushCount: 1,
            ObservedRows: 1,
            NewRows: 0,
            ChangedRows: 1,
            UnchangedRows: 0,
            CurrentUpserts: 1,
            VersionsClosed: 1,
            VersionsOpened: 1), GetLogicalWriteMetrics(44, "Solo_Guitar"));
    }

    [Fact]
    public async Task FlushSpoolAsync_when_physical_snapshot_skip_enabled_pins_unchanged_scope_to_previous_snapshot()
    {
        using var glp = CreatePersistence(new FeatureOptions { SkipUnchangedPhysicalLeaderboardSnapshots = true });
        var expectedPairs = new[] { ("song_1", "Solo_Guitar") };

        glp.StartSpoolWriter(42, _dataDir);
        glp.EnqueueSpoolPage("song_1", "Solo_Guitar",
        [
            new LeaderboardEntry
            {
                AccountId = "acct_1",
                Score = 100_000,
                Accuracy = 95,
                Stars = 5,
                Season = 3,
                Difficulty = 3,
                Percentile = 99.0,
                Rank = 1,
                ApiRank = 1,
                Source = "scrape",
            },
        ]);
        await glp.FlushSpoolAsync();
        glp.FinalizeShadowSnapshots(42, expectedPairs: expectedPairs);

        Assert.Equal(1, GetSnapshotRowCount(42, "song_1", "Solo_Guitar"));
        Assert.Equal((42, 42, true), GetSnapshotState("song_1", "Solo_Guitar"));

        glp.StartSpoolWriter(43, _dataDir);
        glp.EnqueueSpoolPage("song_1", "Solo_Guitar",
        [
            new LeaderboardEntry
            {
                AccountId = "acct_1",
                Score = 100_000,
                Accuracy = 95,
                Stars = 5,
                Season = 3,
                Difficulty = 3,
                Percentile = 99.0,
                Rank = 1,
                ApiRank = 1,
                Source = "scrape",
            },
        ]);
        await glp.FlushSpoolAsync();
        glp.FinalizeShadowSnapshots(43, expectedPairs: expectedPairs);

        Assert.Equal(0, GetSnapshotRowCount(43, "song_1", "Solo_Guitar"));
        Assert.Equal((42, 42, true), GetSnapshotState("song_1", "Solo_Guitar"));
        var unchangedRows = GetCurrentState(glp, "song_1", "Solo_Guitar");
        var unchangedEntry = Assert.Single(unchangedRows);
        Assert.Equal(100_000, unchangedEntry.Score);

        glp.StartSpoolWriter(44, _dataDir);
        glp.EnqueueSpoolPage("song_1", "Solo_Guitar",
        [
            new LeaderboardEntry
            {
                AccountId = "acct_1",
                Score = 101_000,
                Accuracy = 95,
                Stars = 5,
                Season = 3,
                Difficulty = 3,
                Percentile = 99.0,
                Rank = 1,
                ApiRank = 1,
                Source = "scrape",
            },
        ]);
        await glp.FlushSpoolAsync();
        glp.FinalizeShadowSnapshots(44, expectedPairs: expectedPairs);

        Assert.Equal(1, GetSnapshotRowCount(44, "song_1", "Solo_Guitar"));
        Assert.Equal((44, 44, true), GetSnapshotState("song_1", "Solo_Guitar"));
        var changedRows = GetCurrentState(glp, "song_1", "Solo_Guitar");
        var changedEntry = Assert.Single(changedRows);
        Assert.Equal(101_000, changedEntry.Score);
    }

    [Fact]
    public async Task Published_scope_source_candidate_maps_changed_unchanged_and_empty_scopes()
    {
        using var glp = CreatePersistence(new FeatureOptions
        {
            SkipUnchangedPhysicalLeaderboardSnapshots = true,
            WritePublishedScopeSources = true,
        });
        InsertScrapeLog(42, completed: true);
        InsertScrapeLog(43, completed: false);
        var expectedPairs = new[]
        {
            ("song_changed", "Solo_Guitar"),
            ("song_unchanged", "Solo_Guitar"),
            ("song_empty", "Solo_Guitar"),
        };

        glp.StartSpoolWriter(42, _dataDir);
        glp.EnqueueSpoolPage("song_unchanged", "Solo_Guitar",
        [
            new LeaderboardEntry
            {
                AccountId = "acct_old",
                Score = 90_000,
                Accuracy = 95,
                Stars = 5,
                Season = 3,
                Difficulty = 3,
                Percentile = 99.0,
                Rank = 1,
                ApiRank = 1,
                Source = "scrape",
            },
        ]);
        await glp.FlushSpoolAsync();
        glp.FinalizeShadowSnapshots(42, expectedPairs: [("song_unchanged", "Solo_Guitar")]);

        glp.StartSpoolWriter(43, _dataDir);
        glp.EnqueueSpoolPage("song_changed", "Solo_Guitar",
        [
            new LeaderboardEntry
            {
                AccountId = "acct_new",
                Score = 110_000,
                Accuracy = 98,
                Stars = 6,
                Season = 3,
                Difficulty = 3,
                Percentile = 99.0,
                Rank = 1,
                ApiRank = 1,
                Source = "scrape",
            },
            new LeaderboardEntry
            {
                AccountId = "acct_new",
                Score = 100_000,
                Accuracy = 90,
                Stars = 5,
                Season = 3,
                Difficulty = 3,
                Percentile = 98.0,
                Rank = 2,
                ApiRank = 2,
                Source = "scrape",
            },
        ]);
        glp.EnqueueSpoolPage("song_unchanged", "Solo_Guitar",
        [
            new LeaderboardEntry
            {
                AccountId = "acct_old",
                Score = 90_000,
                Accuracy = 95,
                Stars = 5,
                Season = 3,
                Difficulty = 3,
                Percentile = 99.0,
                Rank = 1,
                ApiRank = 1,
                Source = "scrape",
            },
        ]);
        await glp.FlushSpoolAsync();
        glp.FinalizeShadowSnapshots(43, expectedPairs: expectedPairs);

        var coverage = glp.RecordLeaderboardScopeCoverage(
            43,
            [
                CoverageResult("song_changed", "Solo_Guitar", entries: 2),
                CoverageResult("song_unchanged", "Solo_Guitar", entries: 1),
                CoverageResult("song_empty", "Solo_Guitar", entries: 0, reportedPages: 0),
            ],
            expectedPairs);
        _metaFixture.Db.UpsertLeaderboardPopulation(
            [("song_changed", "Solo_Guitar", 500)]);
        var build = glp.BuildPublishedScopeSourceCandidate(43, expectedPairs);
        var sources = glp.GetPublishedScopeSources(43).ToDictionary(source => source.SongId);

        Assert.True(coverage.IsComplete);
        Assert.True(build.IsComplete);
        Assert.Equal(43, sources["song_changed"].SourceSnapshotId);
        Assert.Equal(1, sources["song_changed"].RowCount);
        Assert.Equal(500, sources["song_changed"].ReportedTotalEntries);
        Assert.Equal(42, sources["song_unchanged"].SourceSnapshotId);
        Assert.Equal("empty", sources["song_empty"].SourceKind);
        Assert.Null(sources["song_empty"].SourceSnapshotId);
        Assert.Equal(0, sources["song_empty"].RowCount);
        Assert.Equal((null, 0, 0, true), GetScopeCoverage("song_empty", "Solo_Guitar"));
    }

    [Fact]
    public async Task Published_scope_source_candidate_is_all_or_nothing_when_physical_rows_do_not_match()
    {
        using var glp = CreatePersistence(new FeatureOptions { WritePublishedScopeSources = true });
        InsertScrapeLog(42, completed: false);
        var expectedPairs = new[] { ("song_1", "Solo_Guitar") };

        glp.StartSpoolWriter(42, _dataDir);
        glp.EnqueueSpoolPage("song_1", "Solo_Guitar",
        [
            new LeaderboardEntry
            {
                AccountId = "acct_1",
                Score = 100_000,
                Accuracy = 95,
                Stars = 5,
                Season = 3,
                Difficulty = 3,
                Percentile = 99.0,
                Rank = 1,
                ApiRank = 1,
                Source = "scrape",
            },
        ]);
        await glp.FlushSpoolAsync();
        glp.FinalizeShadowSnapshots(42, expectedPairs: expectedPairs);
        Assert.True(glp.RecordLeaderboardScopeCoverage(
            42,
            [CoverageResult("song_1", "Solo_Guitar", entries: 1)],
            expectedPairs).IsComplete);

        using (var conn = _metaFixture.DataSource.OpenConnection())
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                DELETE FROM leaderboard_entries_snapshot
                WHERE snapshot_id = 42
                  AND song_id = 'song_1'
                  AND instrument = 'Solo_Guitar'
                """;
            cmd.ExecuteNonQuery();
        }

        var build = glp.BuildPublishedScopeSourceCandidate(42, expectedPairs);

        Assert.False(build.IsComplete);
        Assert.Equal(0, build.ValidatedScopeCount);
        Assert.Equal(0, build.MappedScopeCount);
        Assert.Equal(1, build.MissingScopeCount);
        Assert.Empty(glp.GetPublishedScopeSources(42));
    }

    [Fact]
    public void Scope_coverage_marks_failed_page_zero_as_incomplete()
    {
        using var glp = CreatePersistence(new FeatureOptions { WritePublishedScopeSources = true });
        var expectedPairs = new[] { ("song_1", "Solo_Guitar") };

        var coverage = glp.RecordLeaderboardScopeCoverage(
            42,
            [CoverageResult(
                "song_1",
                "Solo_Guitar",
                entries: 0,
                reportedPages: 0,
                pagesScraped: 0,
                requests: 1)],
            expectedPairs);

        Assert.False(coverage.IsComplete);
        Assert.Equal(1, coverage.IncompleteScopeCount);
        Assert.Equal((null, 0, 0, false), GetScopeCoverage("song_1", "Solo_Guitar"));
    }

    [Fact]
    public async Task ScopeManifestGatePersistsCompleteCoverageAndAllowsCandidate()
    {
        using var glp = CreatePersistence(new FeatureOptions
        {
            WritePublishedScopeSources = true,
            EnforceScopeCompletenessManifests = true,
        });
        InsertScrapeLog(42, completed: false);
        var expectedPairs = new[] { ("song_1", "Solo_Guitar") };
        var entries = new[]
        {
            new LeaderboardEntry
            {
                AccountId = "acct_1",
                Score = 100_000,
                Rank = 1,
                ApiRank = 1,
                Source = "scrape",
            },
        };

        glp.StartSpoolWriter(42, _dataDir);
        glp.EnqueueSpoolPage("song_1", "Solo_Guitar", entries);
        Assert.True((await glp.FlushSpoolAsync()).IsSuccess);
        glp.FinalizeShadowSnapshots(42, expectedPairs: expectedPairs);

        var result = new GlobalLeaderboardResult
        {
            SongId = "song_1",
            Instrument = "Solo_Guitar",
            EntriesCount = 1,
            TotalPages = 1,
            ReportedTotalPages = 1,
            PagesScraped = 1,
            Requests = 1,
            CompletenessManifest = ScopeCompletenessManifest.Create(
                0,
                0,
                new Dictionary<int, GlobalLeaderboardScraper.FetchStatus>
                {
                    [0] = GlobalLeaderboardScraper.FetchStatus.Success,
                },
                entries,
                reportedTotalPages: 1),
        };

        var coverage = glp.RecordLeaderboardScopeCoverage(42, [result], expectedPairs);
        var build = glp.BuildPublishedScopeSourceCandidate(42, expectedPairs);
        var manifest = GetScopeManifest(42, "song_1", "Solo_Guitar");

        Assert.True(coverage.IsComplete);
        Assert.True(build.IsComplete);
        Assert.NotNull(manifest);
        Assert.Equal([0], manifest.Value.ReceivedPages);
        Assert.Equal("complete", manifest.Value.ParseStatus);
        Assert.False(manifest.Value.RetryExhausted);
        Assert.True(manifest.Value.IsComplete);
    }

    [Fact]
    public async Task ScopeManifestGateRejectsMissingMalformedAndUnmanifestedScopes()
    {
        using var glp = CreatePersistence(new FeatureOptions
        {
            WritePublishedScopeSources = true,
            EnforceScopeCompletenessManifests = true,
        });
        InsertScrapeLog(42, completed: false);
        var expectedPairs = new[] { ("song_1", "Solo_Guitar") };
        var entries = new[]
        {
            new LeaderboardEntry
            {
                AccountId = "acct_1",
                Score = 100_000,
                Rank = 1,
                ApiRank = 1,
                Source = "scrape",
            },
        };

        glp.StartSpoolWriter(42, _dataDir);
        glp.EnqueueSpoolPage("song_1", "Solo_Guitar", entries);
        Assert.True((await glp.FlushSpoolAsync()).IsSuccess);
        glp.FinalizeShadowSnapshots(42, expectedPairs: expectedPairs);

        var gapResult = new GlobalLeaderboardResult
        {
            SongId = "song_1",
            Instrument = "Solo_Guitar",
            EntriesCount = 1,
            TotalPages = 3,
            ReportedTotalPages = 3,
            PagesScraped = 2,
            Requests = 3,
            CompletenessManifest = ScopeCompletenessManifest.Create(
                0,
                2,
                new Dictionary<int, GlobalLeaderboardScraper.FetchStatus>
                {
                    [0] = GlobalLeaderboardScraper.FetchStatus.Success,
                    [2] = GlobalLeaderboardScraper.FetchStatus.ParseFailure,
                },
                entries,
                reportedTotalPages: 3),
        };

        var gapCoverage = glp.RecordLeaderboardScopeCoverage(42, [gapResult], expectedPairs);
        var gapBuild = glp.BuildPublishedScopeSourceCandidate(42, expectedPairs);
        var manifest = GetScopeManifest(42, "song_1", "Solo_Guitar");

        Assert.False(gapCoverage.IsComplete);
        Assert.False(gapBuild.IsComplete);
        Assert.NotNull(manifest);
        Assert.False(manifest.Value.IsComplete);
        Assert.Equal("failed", manifest.Value.ParseStatus);

        var missingManifestCoverage = glp.RecordLeaderboardScopeCoverage(
            42,
            [CoverageResult("song_1", "Solo_Guitar", entries: 1)],
            expectedPairs);
        Assert.False(missingManifestCoverage.IsComplete);
        Assert.False(glp.BuildPublishedScopeSourceCandidate(42, expectedPairs).IsComplete);
        Assert.Null(GetScopeManifest(42, "song_1", "Solo_Guitar"));
    }

    [Fact]
    public async Task ScopeManifestGateAcceptsContiguousEpicForbiddenBoundary()
    {
        using var glp = CreatePersistence(new FeatureOptions
        {
            WritePublishedScopeSources = true,
            EnforceScopeCompletenessManifests = true,
        });
        InsertScrapeLog(42, completed: false);
        var expectedPairs = new[] { ("song_1", "Solo_Guitar") };
        var entries = new[]
        {
            new LeaderboardEntry
            {
                AccountId = "acct_1",
                Score = 100_000,
                Rank = 1,
                ApiRank = 1,
                Source = "scrape",
            },
        };

        glp.StartSpoolWriter(42, _dataDir);
        glp.EnqueueSpoolPage("song_1", "Solo_Guitar", entries);
        Assert.True((await glp.FlushSpoolAsync()).IsSuccess);
        glp.FinalizeShadowSnapshots(42, expectedPairs: expectedPairs);

        var result = new GlobalLeaderboardResult
        {
            SongId = "song_1",
            Instrument = "Solo_Guitar",
            EntriesCount = 1,
            TotalPages = 5,
            ReportedTotalPages = 5,
            PagesScraped = 2,
            Requests = 5,
            CompletenessManifest = ScopeCompletenessManifest.Create(
                0,
                4,
                new Dictionary<int, GlobalLeaderboardScraper.FetchStatus>
                {
                    [0] = GlobalLeaderboardScraper.FetchStatus.Success,
                    [1] = GlobalLeaderboardScraper.FetchStatus.Success,
                    [2] = GlobalLeaderboardScraper.FetchStatus.Forbidden,
                    [3] = GlobalLeaderboardScraper.FetchStatus.Forbidden,
                    [4] = GlobalLeaderboardScraper.FetchStatus.Forbidden,
                },
                entries,
                reportedTotalPages: 5,
                terminalBoundary: ScopeTerminalBoundaryKind.EpicForbidden,
                terminalBoundaryPage: 2),
        };

        var coverage = glp.RecordLeaderboardScopeCoverage(42, [result], expectedPairs);

        Assert.True(coverage.IsComplete);
        Assert.True(glp.BuildPublishedScopeSourceCandidate(42, expectedPairs).IsComplete);
        Assert.True(GetScopeManifest(42, "song_1", "Solo_Guitar")?.IsComplete);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CoordinatedDeepRowsReachSnapshotProjectionManifestAndPublication(
        bool useOnlineWriter)
    {
        using var glp = CreatePersistence(new FeatureOptions
        {
            WriteLegacyLiveLeaderboardDuringScrape = false,
            WritePublishedScopeSources = true,
            EnforceScopeCompletenessManifests = true,
        });
        var scrapeId = _metaFixture.Db.StartScrapeRun();
        var handler = new MockHttpMessageHandler();
        handler.EnqueueJsonOk(MakePage(0, 4, ("wave1-over", 1200), ("wave1-valid", 900)));
        handler.EnqueueJsonOk(MakePage(1, 4, ("wave1-valid-2", 800)));
        handler.EnqueueJsonOk(MakePage(2, 4, ("deep-valid-1", 700)));
        handler.EnqueueJsonOk(MakePage(3, 4, ("deep-valid-2", 600)));
        var scraper = new GlobalLeaderboardScraper(
            new HttpClient(handler),
            new ScrapeProgressTracker(),
            Substitute.For<ILogger<GlobalLeaderboardScraper>>(),
            maxLookupRetries: 0);
        var expectedPairs = new[] { ("song_deep", "Solo_Guitar") };

        if (useOnlineWriter)
        {
            glp.StartOnlineSoloWriter(
                scrapeId,
                channelCapacity: 8,
                maxBatchPages: 4,
                writerCount: 1,
                replayBaseDirectory: _dataDir);
        }
        else
        {
            glp.StartSpoolWriter(scrapeId, _dataDir);
        }
        var results = await scraper.ScrapeManySongsAsync(
            [
                new GlobalLeaderboardScraper.SongScrapeRequest
                {
                    SongId = "song_deep",
                    Instruments = ["Solo_Guitar"],
                    MaxScores = new SongMaxScores { MaxLeadScore = 1000 },
                },
            ],
            "token",
            "acct",
            maxConcurrency: 1,
            onSongComplete: async (songId, scopeResults) =>
            {
                foreach (var result in scopeResults)
                {
                    if (useOnlineWriter)
                    {
                        await glp.EnqueueOnlineSoloPageAsync(
                            songId,
                            result.Instrument,
                            result.Entries);
                    }
                    else
                    {
                        glp.EnqueueSpoolPage(songId, result.Instrument, result.Entries);
                    }
                }
            },
            maxPages: 2,
            overThresholdExtraPages: 2,
            validEntryTarget: 4,
            deferDeepScrape: true);

        var writerResult = useOnlineWriter
            ? await glp.DrainOnlineSoloWriterAsync()
            : await glp.FlushSpoolAsync();
        Assert.True(writerResult.IsSuccess);
        glp.FinalizeShadowSnapshots(scrapeId, expectedPairs: expectedPairs);
        var coverage = glp.RecordLeaderboardScopeCoverage(
            scrapeId,
            results.Values.SelectMany(static result => result),
            expectedPairs);
        var sourceBuild = glp.BuildPublishedScopeSourceCandidate(scrapeId, expectedPairs);

        var projectionBuilder = new SoloCurrentProjectionBuilder(
            _metaFixture.DataSource,
            Substitute.For<ILogger<SoloCurrentProjectionBuilder>>());
        await projectionBuilder.EnsureSchemaAsync();
        var projection = await projectionBuilder.RefreshScopesAsync(
            [new SoloCurrentProjectionScopeKey("song_deep", "Solo_Guitar")],
            new SoloCurrentProjectionRebuildOptions { MaxDegreeOfParallelism = 1 });

        _metaFixture.Db.CompleteScrapeRun(scrapeId, 1, 5, 4, 1000);
        _metaFixture.Db.PublishScrapeRun(
            scrapeId,
            promoteCachedResponses: false,
            expectedPublishedScopeCount: 1);

        Assert.True(coverage.IsComplete);
        Assert.True(sourceBuild.IsComplete);
        Assert.Equal(1, projection.SucceededScopeCount);
        Assert.Equal(5, projection.InsertedRows);
        Assert.Equal(
            ["wave1-over", "wave1-valid", "wave1-valid-2", "deep-valid-1", "deep-valid-2"],
            ReadAccounts(
                "leaderboard_entries_snapshot",
                "snapshot_id = @scrapeId",
                scrapeId));
        Assert.Equal(
            ["wave1-over", "wave1-valid", "wave1-valid-2", "deep-valid-1", "deep-valid-2"],
            ReadAccounts(
                "current_leaderboard_entries",
                "TRUE",
                scrapeId));
        var manifest = GetScopeManifest(scrapeId, "song_deep", "Solo_Guitar");
        Assert.NotNull(manifest);
        Assert.True(manifest.Value.IsComplete);
        Assert.Equal([0, 1, 2, 3], manifest.Value.ReceivedPages);
        Assert.Equal(scrapeId, _metaFixture.Db.GetPublishedScrapeRun()?.Id);
        Assert.Equal(5, Assert.Single(glp.GetPublishedScopeSources(scrapeId)).RowCount);
    }

    private static string MakePage(
        int page,
        int totalPages,
        params (string AccountId, int Score)[] entries)
    {
        var payload = new
        {
            page,
            totalPages,
            entries = entries.Select((entry, index) => new
            {
                teamId = entry.AccountId,
                rank = page * 100 + index + 1,
                percentile = 0.5,
                sessionHistory = new[]
                {
                    new
                    {
                        trackedStats = new Dictionary<string, int>
                        {
                            ["SCORE"] = entry.Score,
                        },
                    },
                },
            }),
        };
        return JsonSerializer.Serialize(payload);
    }

    private string[] ReadAccounts(string table, string extraPredicate, long scrapeId)
    {
        using var conn = _metaFixture.DataSource.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            SELECT account_id
            FROM {table}
            WHERE song_id = 'song_deep'
              AND instrument = 'Solo_Guitar'
              AND {extraPredicate}
            ORDER BY rank, account_id
            """;
        cmd.Parameters.AddWithValue("scrapeId", scrapeId);
        using var reader = cmd.ExecuteReader();
        var accounts = new List<string>();
        while (reader.Read())
            accounts.Add(reader.GetString(0));
        return accounts.ToArray();
    }

    [Fact]
    public async Task Publishing_scope_sources_promotes_mapping_and_fingerprint_atomically()
    {
        using var glp = CreatePersistence(new FeatureOptions { WritePublishedScopeSources = true });
        InsertScrapeLog(42, completed: false);
        var expectedPairs = new[] { ("song_1", "Solo_Guitar") };

        glp.StartSpoolWriter(42, _dataDir);
        glp.EnqueueSpoolPage("song_1", "Solo_Guitar",
        [
            new LeaderboardEntry
            {
                AccountId = "acct_1",
                Score = 100_000,
                Accuracy = 95,
                Stars = 5,
                Season = 3,
                Difficulty = 3,
                Percentile = 99.0,
                Rank = 1,
                ApiRank = 1,
                Source = "scrape",
            },
        ]);
        await glp.FlushSpoolAsync();
        glp.FinalizeShadowSnapshots(42, expectedPairs: expectedPairs);
        Assert.True(glp.RecordLeaderboardScopeCoverage(
            42,
            [CoverageResult("song_1", "Solo_Guitar", entries: 1)],
            expectedPairs).IsComplete);
        _metaFixture.Db.UpsertLeaderboardPopulation(
            [("song_1", "Solo_Guitar", 500)]);
        var build = glp.BuildPublishedScopeSourceCandidate(42, expectedPairs);
        Assert.True(build.IsComplete);

        _metaFixture.Db.CompleteScrapeRun(42, 1, 1, 1, 100);
        _metaFixture.Db.PublishScrapeRun(
            42,
            promoteCachedResponses: false,
            expectedPublishedScopeCount: build.ExpectedScopeCount);

        Assert.Equal(42, _metaFixture.Db.GetPublishedScrapeRun()?.Id);
        Assert.Equal(
            500,
            Assert.Single(glp.GetPublishedScopeSources(42)).ReportedTotalEntries);
        Assert.Equal((42, 1, 1, true), GetScopeCoverage("song_1", "Solo_Guitar"));
    }

    [Fact]
    public async Task Existing_published_mapping_repairs_population_floor_at_clean_boundary()
    {
        using var glp = CreatePersistence(new FeatureOptions { WritePublishedScopeSources = true });
        InsertScrapeLog(42, completed: false);
        var expectedPairs = new[] { ("song_1", "Solo_Guitar") };

        glp.StartSpoolWriter(42, _dataDir);
        glp.EnqueueSpoolPage("song_1", "Solo_Guitar",
        [
            new LeaderboardEntry
            {
                AccountId = "acct_1",
                Score = 100_000,
                Rank = 1,
                ApiRank = 1,
                Source = "scrape",
            },
        ]);
        await glp.FlushSpoolAsync();
        glp.FinalizeShadowSnapshots(42, expectedPairs: expectedPairs);
        Assert.True(glp.RecordLeaderboardScopeCoverage(
            42,
            [CoverageResult("song_1", "Solo_Guitar", entries: 1)],
            expectedPairs).IsComplete);
        Assert.True(glp.BuildPublishedScopeSourceCandidate(42, expectedPairs).IsComplete);
        _metaFixture.Db.CompleteScrapeRun(42, 1, 1, 1, 100);
        _metaFixture.Db.PublishScrapeRun(
            42,
            promoteCachedResponses: false,
            expectedPublishedScopeCount: 1);
        _metaFixture.Db.UpsertLeaderboardPopulation(
            [("song_1", "Solo_Guitar", 750)]);

        var repair = glp.BackfillCurrentPublishedScopeSources();

        Assert.True(repair.Applied);
        Assert.Contains("population-repaired:1", repair.Status);
        Assert.Equal(
            750,
            Assert.Single(glp.GetPublishedScopeSources(42)).ReportedTotalEntries);
    }

    [Fact]
    public async Task Existing_published_mapping_does_not_repair_population_during_newer_in_progress_scrape()
    {
        using var glp = CreatePersistence(new FeatureOptions { WritePublishedScopeSources = true });
        InsertScrapeLog(42, completed: false);
        var expectedPairs = new[] { ("song_1", "Solo_Guitar") };

        glp.StartSpoolWriter(42, _dataDir);
        glp.EnqueueSpoolPage("song_1", "Solo_Guitar",
        [
            new LeaderboardEntry
            {
                AccountId = "acct_1",
                Score = 100_000,
                Rank = 1,
                ApiRank = 1,
                Source = "scrape",
            },
        ]);
        await glp.FlushSpoolAsync();
        glp.FinalizeShadowSnapshots(42, expectedPairs: expectedPairs);
        Assert.True(glp.RecordLeaderboardScopeCoverage(
            42,
            [CoverageResult("song_1", "Solo_Guitar", entries: 1)],
            expectedPairs).IsComplete);
        Assert.True(glp.BuildPublishedScopeSourceCandidate(42, expectedPairs).IsComplete);
        _metaFixture.Db.CompleteScrapeRun(42, 1, 1, 1, 100);
        _metaFixture.Db.PublishScrapeRun(
            42,
            promoteCachedResponses: false,
            expectedPublishedScopeCount: 1);
        InsertScrapeLog(43, completed: false);
        glp.StartSpoolWriter(43, _dataDir);
        glp.EnqueueSpoolPage("song_new", "Solo_Guitar",
        [
            new LeaderboardEntry
            {
                AccountId = "acct_new",
                Score = 110_000,
                Rank = 1,
                ApiRank = 1,
                Source = "scrape",
            },
        ]);
        await glp.FlushSpoolAsync();
        glp.FinalizeShadowSnapshots(43, expectedPairs: [("song_new", "Solo_Guitar")]);
        _metaFixture.Db.UpsertLeaderboardPopulation(
            [("song_1", "Solo_Guitar", 750)]);

        var repair = glp.BackfillCurrentPublishedScopeSources();

        Assert.False(repair.Applied);
        Assert.Equal(
            1,
            Assert.Single(glp.GetPublishedScopeSources(42)).ReportedTotalEntries);
    }

    [Fact]
    public async Task Startup_backfill_maps_current_published_snapshot_and_empty_scope()
    {
        InsertScrapeLog(42, completed: false);
        var expectedPairs = new[]
        {
            ("song_1", "Solo_Guitar"),
            ("song_empty", "Solo_Guitar"),
        };
        using (var seed = CreatePersistence())
        {
            seed.StartSpoolWriter(42, _dataDir);
            seed.EnqueueSpoolPage("song_1", "Solo_Guitar",
            [
                new LeaderboardEntry
                {
                    AccountId = "acct_1",
                    Score = 100_000,
                    Accuracy = 95,
                    Stars = 5,
                    Season = 3,
                    Difficulty = 3,
                    Percentile = 99.0,
                    Rank = 1,
                    ApiRank = 1,
                    Source = "scrape",
                },
            ]);
            await seed.FlushSpoolAsync();
            seed.FinalizeShadowSnapshots(42, expectedPairs: expectedPairs);
        }
        _metaFixture.Db.UpsertLeaderboardPopulation([("song_1", "Solo_Guitar", 1)]);
        _metaFixture.Db.CompleteScrapeRun(42, 1, 1, 1, 100);
        _metaFixture.Db.PublishScrapeRun(42, promoteCachedResponses: false);

        using var rollout = CreatePersistence(new FeatureOptions { WritePublishedScopeSources = true });
        var sources = rollout.GetPublishedScopeSources(42).ToDictionary(source => source.SongId);

        Assert.Equal(2, sources.Count);
        Assert.Equal("snapshot", sources["song_1"].SourceKind);
        Assert.Equal(42, sources["song_1"].SourceSnapshotId);
        Assert.Equal("empty", sources["song_empty"].SourceKind);
        Assert.Equal((42, 0, 0, true), GetScopeCoverage("song_empty", "Solo_Guitar"));
    }

    private static GlobalLeaderboardResult CoverageResult(
        string songId,
        string instrument,
        int entries,
        int reportedPages = 1,
        int? pagesScraped = null,
        int requests = 1) =>
        new()
        {
            SongId = songId,
            Instrument = instrument,
            EntriesCount = entries,
            TotalPages = reportedPages,
            ReportedTotalPages = reportedPages,
            PagesScraped = pagesScraped ?? Math.Max(1, reportedPages),
            Requests = requests,
        };

    [Fact]
    public async Task RollbackIncompleteLogicalLeaderboardScrapes_removes_partial_or_orphaned_current_and_versions()
    {
        using var glp = CreatePersistence();
        InsertScrapeLog(42, completed: true);
        InsertScrapeLog(43, completed: false);
        InsertScrapeLog(44, completed: false);

        glp.StartSpoolWriter(42, _dataDir);
        glp.EnqueueSpoolPage("song_1", "Solo_Guitar",
        [
            new LeaderboardEntry
            {
                AccountId = "acct_1",
                Score = 100_000,
                Accuracy = 95,
                Stars = 5,
                Season = 3,
                Difficulty = 3,
                Percentile = 99.0,
                Rank = 1,
                ApiRank = 1,
                Source = "scrape",
            },
        ]);
        await glp.FlushSpoolAsync();

        glp.StartSpoolWriter(43, _dataDir);
        glp.EnqueueSpoolPage("song_1", "Solo_Guitar",
        [
            new LeaderboardEntry
            {
                AccountId = "acct_1",
                Score = 101_000,
                Accuracy = 95,
                Stars = 5,
                Season = 3,
                Difficulty = 3,
                Percentile = 99.0,
                Rank = 1,
                ApiRank = 1,
                Source = "scrape",
            },
            new LeaderboardEntry
            {
                AccountId = "acct_partial",
                Score = 50_000,
                Accuracy = 90,
                Stars = 4,
                Season = 3,
                Difficulty = 3,
                Percentile = 80.0,
                Rank = 2,
                ApiRank = 2,
                Source = "scrape",
            },
        ]);
        await glp.FlushSpoolAsync();

        var partialCurrent = GetLogicalCurrentRow("song_1", "Solo_Guitar", "acct_1");
        Assert.NotNull(partialCurrent);
        Assert.Equal(101_000, partialCurrent.Value.Score);
        Assert.Equal((Total: 2, Open: 1, Closed: 1), GetLogicalVersionCounts("song_1", "Solo_Guitar", "acct_1"));
        Assert.NotNull(GetLogicalCurrentRow("song_1", "Solo_Guitar", "acct_partial"));
        Assert.NotNull(GetLogicalWriteMetrics(43, "Solo_Guitar"));

        DeleteScrapeLog(43);
        glp.RollbackIncompleteLogicalLeaderboardScrapes(44);

        var restoredCurrent = GetLogicalCurrentRow("song_1", "Solo_Guitar", "acct_1");
        Assert.NotNull(restoredCurrent);
        Assert.Equal(100_000, restoredCurrent.Value.Score);
        Assert.Equal(42, restoredCurrent.Value.FirstSeenScrapeId);
        Assert.Equal(42, restoredCurrent.Value.LastChangedScrapeId);
        Assert.Equal((Total: 1, Open: 1, Closed: 0), GetLogicalVersionCounts("song_1", "Solo_Guitar", "acct_1"));
        Assert.Null(GetLogicalCurrentRow("song_1", "Solo_Guitar", "acct_partial"));
        Assert.Equal((Total: 0, Open: 0, Closed: 0), GetLogicalVersionCounts("song_1", "Solo_Guitar", "acct_partial"));
        Assert.Null(GetLogicalWriteMetrics(43, "Solo_Guitar"));
    }

    [Fact]
    public async Task RollbackIncompleteLogicalLeaderboardScrapes_truncates_when_all_logical_artifacts_are_invalid()
    {
        using var glp = CreatePersistence();
        InsertScrapeLog(43, completed: false);
        InsertScrapeLog(44, completed: false);

        glp.StartSpoolWriter(43, _dataDir);
        glp.EnqueueSpoolPage("song_1", "Solo_Guitar",
        [
            new LeaderboardEntry
            {
                AccountId = "acct_partial",
                Score = 50_000,
                Accuracy = 90,
                Stars = 4,
                Season = 3,
                Difficulty = 3,
                Percentile = 80.0,
                Rank = 1,
                ApiRank = 1,
                Source = "scrape",
            },
        ]);
        await glp.FlushSpoolAsync();
        DeleteScrapeLog(43);

        Assert.NotNull(GetLogicalCurrentRow("song_1", "Solo_Guitar", "acct_partial"));
        Assert.NotNull(GetLogicalWriteMetrics(43, "Solo_Guitar"));

        glp.RollbackIncompleteLogicalLeaderboardScrapes(44);

        Assert.Null(GetLogicalCurrentRow("song_1", "Solo_Guitar", "acct_partial"));
        Assert.Equal((Total: 0, Open: 0, Closed: 0), GetLogicalVersionCounts("song_1", "Solo_Guitar", "acct_partial"));
        Assert.Null(GetLogicalWriteMetrics(43, "Solo_Guitar"));
    }

    [Fact]
    public async Task FlushSpoolAsync_default_maintains_legacy_live_rows()
    {
        using var glp = CreatePersistence();

        glp.StartSpoolWriter(42, _dataDir);
        glp.EnqueueSpoolPage("song_1", "Solo_Guitar",
        [
            new LeaderboardEntry
            {
                AccountId = "acct_1",
                Score = 100_000,
                Accuracy = 95,
                Stars = 5,
                Season = 3,
                Difficulty = 3,
                Percentile = 99.0,
                Rank = 1,
                ApiRank = 1,
                Source = "scrape",
            }
        ]);

        await glp.FlushSpoolAsync();

        Assert.Equal(1, GetSnapshotRowCount(42, "song_1", "Solo_Guitar"));
        Assert.Equal(1, GetLiveRowCount("song_1", "Solo_Guitar"));
    }

    [Fact]
    public async Task FlushSpoolAsync_when_legacy_live_writes_disabled_writes_snapshot_only()
    {
        using var glp = CreatePersistence(new FeatureOptions { WriteLegacyLiveLeaderboardDuringScrape = false });

        glp.StartSpoolWriter(42, _dataDir);
        glp.EnqueueSpoolPage("song_1", "Solo_Guitar",
        [
            new LeaderboardEntry
            {
                AccountId = "acct_1",
                Score = 100_000,
                Accuracy = 95,
                Stars = 5,
                Season = 3,
                Difficulty = 3,
                Percentile = 99.0,
                Rank = 1,
                ApiRank = 1,
                Source = "scrape",
            }
        ]);

        await glp.FlushSpoolAsync();
        var activated = glp.FinalizeShadowSnapshots(42);
        var currentState = glp.GetCurrentStateLeaderboard("song_1", "Solo_Guitar", top: 10);

        Assert.Equal(1, GetSnapshotRowCount(42, "song_1", "Solo_Guitar"));
        Assert.Equal(0, GetLiveRowCount("song_1", "Solo_Guitar"));
        Assert.Equal(1, activated);
        var entry = Assert.Single(currentState!);
        Assert.Equal("acct_1", entry.AccountId);
        Assert.Equal(100_000, entry.Score);
    }

    [Fact]
    public async Task OnlineSoloWriter_when_legacy_live_writes_disabled_writes_snapshot_only()
    {
        using var glp = CreatePersistence(new FeatureOptions { WriteLegacyLiveLeaderboardDuringScrape = false });

        glp.StartOnlineSoloWriter(42, channelCapacity: 2, maxBatchPages: 2, writerCount: 1);
        await glp.EnqueueOnlineSoloPageAsync("song_1", "Solo_Guitar",
        [
            new LeaderboardEntry
            {
                AccountId = "acct_1",
                Score = 100_000,
                Accuracy = 95,
                Stars = 5,
                Season = 3,
                Difficulty = 3,
                Percentile = 99.0,
                Rank = 1,
                ApiRank = 1,
                Source = "scrape",
            }
        ]);
        await glp.DrainOnlineSoloWriterAsync();
        var activated = glp.FinalizeShadowSnapshots(42);
        var currentState = glp.GetCurrentStateLeaderboard("song_1", "Solo_Guitar", top: 10);

        Assert.Equal(1, GetSnapshotRowCount(42, "song_1", "Solo_Guitar"));
        Assert.Equal(0, GetLiveRowCount("song_1", "Solo_Guitar"));
        Assert.Equal(1, activated);
        var entry = Assert.Single(currentState!);
        Assert.Equal("acct_1", entry.AccountId);
        Assert.Equal(100_000, entry.Score);
    }

    [Fact]
    public async Task FinalizeShadowSnapshots_marks_snapshot_state_for_scrape_run()
    {
        using var glp = CreatePersistence();

        glp.StartSpoolWriter(42, _dataDir);
        glp.EnqueueSpoolPage("song_1", "Solo_Guitar",
        [
            new LeaderboardEntry
            {
                AccountId = "acct_1",
                Score = 100_000,
                Accuracy = 95,
                Stars = 5,
                Season = 3,
                Difficulty = 3,
                Percentile = 99.0,
                Rank = 1,
                ApiRank = 1,
                Source = "scrape",
                EndTime = "2025-01-15T12:00:00Z",
            }
        ]);

        await glp.FlushSpoolAsync();
        var activated = glp.FinalizeShadowSnapshots(42);

        Assert.Equal(1, activated);
        var state = GetSnapshotState("song_1", "Solo_Guitar");
        Assert.NotNull(state);
        Assert.Equal(42, state?.ActiveSnapshotId);
        Assert.Equal(42, state?.ScrapeId);
        Assert.True(state?.IsFinalized);
    }

    [Fact]
    public void FinalizeShadowSnapshots_activates_expected_pairs_without_snapshot_rows()
    {
        using var glp = CreatePersistence();

        var activated = glp.FinalizeShadowSnapshots(
            42,
            expectedPairs: [("song_empty", "Solo_Guitar")]);

        Assert.Equal(1, activated);
        Assert.Equal(0, GetSnapshotRowCount(42, "song_empty", "Solo_Guitar"));
        var state = GetSnapshotState("song_empty", "Solo_Guitar");
        Assert.NotNull(state);
        Assert.Equal(42, state?.ActiveSnapshotId);
        Assert.Equal(42, state?.ScrapeId);
        Assert.True(state?.IsFinalized);
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
        Assert.Single(pr.DirtyRivalSongs);
        Assert.Equal(RivalsDirtyReason.SelfScoreChange, pr.DirtyRivalSongs[0].DirtyReason);
        Assert.Equal("song_1", pr.DirtyRivalSongs[0].SongId);

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

    [Fact]
    public void PersistResult_marks_registered_user_self_rank_dirty_when_untracked_player_moves_nearby()
    {
        using var glp = CreatePersistence();
        var registered = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "acct_tracked" };

        var initial = new GlobalLeaderboardResult
        {
            SongId = "song_1",
            Instrument = "Solo_Guitar",
            Entries = new List<LeaderboardEntry>
            {
                new() { AccountId = "acct_1", Score = 120_000, Rank = 1, Accuracy = 99, IsFullCombo = true, Stars = 6, Season = 3, Percentile = 100.0 },
                new() { AccountId = "acct_tracked", Score = 110_000, Rank = 2, Accuracy = 98, IsFullCombo = true, Stars = 6, Season = 3, Percentile = 99.5 },
                new() { AccountId = "acct_3", Score = 100_000, Rank = 3, Accuracy = 97, IsFullCombo = false, Stars = 5, Season = 3, Percentile = 99.0 },
                new() { AccountId = "acct_untracked", Score = 70_000, Rank = 4, Accuracy = 95, IsFullCombo = false, Stars = 5, Season = 3, Percentile = 95.0 },
            },
        };

        var moved = new GlobalLeaderboardResult
        {
            SongId = "song_1",
            Instrument = "Solo_Guitar",
            Entries = new List<LeaderboardEntry>
            {
                new() { AccountId = "acct_1", Score = 120_000, Rank = 1, Accuracy = 99, IsFullCombo = true, Stars = 6, Season = 3, Percentile = 100.0 },
                new() { AccountId = "acct_untracked", Score = 115_000, Rank = 2, Accuracy = 98, IsFullCombo = true, Stars = 6, Season = 3, Percentile = 99.8 },
                new() { AccountId = "acct_tracked", Score = 110_000, Rank = 3, Accuracy = 98, IsFullCombo = true, Stars = 6, Season = 3, Percentile = 99.5 },
                new() { AccountId = "acct_3", Score = 100_000, Rank = 4, Accuracy = 97, IsFullCombo = false, Stars = 5, Season = 3, Percentile = 99.0 },
            },
        };

        glp.PersistResult(initial, registered);
        var pr = glp.PersistResult(moved, registered);

        Assert.Equal(0, pr.ScoreChangesDetected);
        Assert.Contains("acct_tracked", pr.ChangedAccountIds);
        Assert.Single(pr.DirtyRivalSongs);
        Assert.Equal("acct_tracked", pr.DirtyRivalSongs[0].AccountId);
        Assert.Equal(RivalsDirtyReason.SelfRankChange, pr.DirtyRivalSongs[0].DirtyReason);
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
            agg.IncrementSoloLeaderboardsWithData();
        });

        Assert.Equal(100, agg.TotalEntries);
        Assert.Equal(100, agg.TotalChanges);
        Assert.Equal(100, agg.SongsWithData);
        Assert.Equal(100, agg.SoloLeaderboardsWithData);
    }

    [Fact]
    public void PipelineAggregates_changed_account_ids_deduplicates()
    {
        var agg = new GlobalLeaderboardPersistence.PipelineAggregates();

        agg.AddChangedAccountIds(["acct_1", "acct_2"]);
        agg.AddChangedAccountIds(["acct_1", "acct_3"]);

        Assert.Equal(3, agg.ChangedAccountIds.Count);
    }

    [Fact]
    public void PipelineAggregates_dirty_rival_songs_deduplicates_by_song_key()
    {
        var agg = new GlobalLeaderboardPersistence.PipelineAggregates();

        agg.AddDirtyRivalSongs(
        [
            new RivalDirtySongRow
            {
                AccountId = "acct_1",
                Instrument = "Solo_Guitar",
                SongId = "song_1",
                DirtyReason = RivalsDirtyReason.SelfScoreChange,
                DetectedAt = "2026-01-01T00:00:00Z",
            },
        ]);
        agg.AddDirtyRivalSongs(
        [
            new RivalDirtySongRow
            {
                AccountId = "acct_1",
                Instrument = "Solo_Guitar",
                SongId = "song_1",
                DirtyReason = RivalsDirtyReason.NeighborWindowChange,
                DetectedAt = "2026-01-01T00:00:01Z",
            },
            new RivalDirtySongRow
            {
                AccountId = "acct_2",
                Instrument = "Solo_Bass",
                SongId = "song_2",
                DirtyReason = RivalsDirtyReason.SelfRankChange,
                DetectedAt = "2026-01-01T00:00:02Z",
            },
        ]);

        Assert.Equal(2, agg.DirtyRivalSongs.Count);
    }

    // ═══ Initialize ═════════════════════════════════════════════

    [Fact]
    public void Initialize_creates_all_instrument_dbs()
    {
        using var glp = CreatePersistence();
        var keys = glp.GetInstrumentKeys();
        Assert.True(keys.Count >= 6, "Should have at least 6 instrument databases");
        Assert.Contains("Solo_Guitar", keys);
        Assert.Contains("Solo_Bass", keys);
        Assert.Contains("Solo_Drums", keys);
        Assert.Contains("Solo_Vocals", keys);
    }

    // ═══ GetEntryCounts ═════════════════════════════════════════

    [Fact]
    public void GetEntryCounts_returns_zeros_when_empty()
    {
        using var glp = CreatePersistence();
        var counts = glp.GetEntryCounts();
        Assert.True(counts.Count >= 6);
        Assert.All(counts.Values, c => Assert.Equal(0, c));
    }

    [Fact]
    public void GetEntryCounts_reflects_persisted_data()
    {
        using var glp = CreatePersistence();
        glp.PersistResult(MakeResult("song_1", "Solo_Guitar", ("acct_1", 100_000)));
        glp.PersistResult(MakeResult("song_1", "Solo_Bass", ("acct_1", 90_000), ("acct_2", 80_000)));

        var counts = glp.GetEntryCounts();
        Assert.Equal(1, counts["Solo_Guitar"]);
        Assert.Equal(2, counts["Solo_Bass"]);
    }

    // ═══ GetInstrumentKeys ══════════════════════════════════════

    [Fact]
    public void GetInstrumentKeys_returns_all_known_instruments()
    {
        using var glp = CreatePersistence();
        var keys = glp.GetInstrumentKeys();
        Assert.Contains("Solo_Guitar", keys);
        Assert.Contains("Solo_Bass", keys);
        Assert.Contains("Solo_Drums", keys);
        Assert.Contains("Solo_Vocals", keys);
        Assert.Contains("Solo_PeripheralGuitar", keys);
        Assert.Contains("Solo_PeripheralBass", keys);
    }

    // ═══ GetMinSeasonAcrossInstruments ═══════════════════════════

    [Fact]
    public void GetMinSeasonAcrossInstruments_returns_null_when_empty()
    {
        using var glp = CreatePersistence();
        Assert.Null(glp.GetMinSeasonAcrossInstruments("song_1"));
    }

    [Fact]
    public void GetMinSeasonAcrossInstruments_finds_min_across_dbs()
    {
        using var glp = CreatePersistence();
        // Guitar has season 3, Bass has season 1
        var guitarResult = new GlobalLeaderboardResult
        {
            SongId = "song_1",
            Instrument = "Solo_Guitar",
            Entries = [new LeaderboardEntry { AccountId = "a", Score = 100, Season = 3 }],
        };
        var bassResult = new GlobalLeaderboardResult
        {
            SongId = "song_1",
            Instrument = "Solo_Bass",
            Entries = [new LeaderboardEntry { AccountId = "b", Score = 90, Season = 1 }],
        };
        glp.PersistResult(guitarResult);
        glp.PersistResult(bassResult);

        Assert.Equal(1, glp.GetMinSeasonAcrossInstruments("song_1"));
    }

    // ═══ GetMaxSeasonAcrossInstruments ═══════════════════════════

    [Fact]
    public void GetMaxSeasonAcrossInstruments_returns_null_when_empty()
    {
        using var glp = CreatePersistence();
        Assert.Null(glp.GetMaxSeasonAcrossInstruments());
    }

    [Fact]
    public void GetMaxSeasonAcrossInstruments_finds_max_across_dbs()
    {
        using var glp = CreatePersistence();
        var guitarResult = new GlobalLeaderboardResult
        {
            SongId = "song_1",
            Instrument = "Solo_Guitar",
            Entries = [new LeaderboardEntry { AccountId = "a", Score = 100, Season = 3 }],
        };
        var bassResult = new GlobalLeaderboardResult
        {
            SongId = "song_2",
            Instrument = "Solo_Bass",
            Entries = [new LeaderboardEntry { AccountId = "b", Score = 90, Season = 7 }],
        };
        glp.PersistResult(guitarResult);
        glp.PersistResult(bassResult);

        Assert.Equal(7, glp.GetMaxSeasonAcrossInstruments());
    }

    // ═══ Pipeline StartWriters + DrainWriters ═══════════════════

    [Fact]
    public async Task StartWriters_and_DrainWriters_process_items()
    {
        using var glp = CreatePersistence();
        var agg = glp.StartWriters();

        await glp.EnqueueResultAsync(
            MakeResult("song_1", "Solo_Guitar", ("acct_1", 100_000)),
            registeredAccountIds: null);

        await glp.DrainWritersAsync();

        Assert.True(agg.TotalEntries > 0);
        Assert.Equal(1, glp.GetEntryCounts()["Solo_Guitar"]);
    }

    [Fact]
    public async Task EnqueueResultAsync_throws_if_writers_not_started()
    {
        using var glp = CreatePersistence();
        // Don't call StartWriters
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            glp.EnqueueResultAsync(
                MakeResult("song_1", "Solo_Guitar", ("acct_1", 100_000)),
                registeredAccountIds: null).AsTask());
    }

    [Fact]
    public async Task EnqueueResultAsync_unknown_instrument_drops_result()
    {
        using var glp = CreatePersistence();
        glp.StartWriters();

        // Enqueue a result with an instrument that has no writer channel
        await glp.EnqueueResultAsync(
            MakeResult("song_1", "UnknownInstrument", ("acct_1", 100_000)),
            registeredAccountIds: null);

        await glp.DrainWritersAsync();

        // The result should be silently dropped — no entries persisted
        Assert.False(glp.GetEntryCounts().ContainsKey("UnknownInstrument"));
    }

    [Fact]
    public void Constructor_creates_persistence_without_directory()
    {
        var loggerFactory = new NullLoggerFactory();
        using var glp = new GlobalLeaderboardPersistence(
            _metaFixture.Db,
            loggerFactory,
            NullLogger<GlobalLeaderboardPersistence>.Instance,
            _metaFixture.DataSource,
            Options.Create(new FeatureOptions()));
    }

    // ═══ GetLeaderboardWithCount ════════════════════════════════

    [Fact]
    public void GetOrCreateInstrumentDb_Throws_ForUnknownInstrument()
    {
        using var glp = CreatePersistence();
        var ex = Assert.Throws<ArgumentException>(() => glp.GetOrCreateInstrumentDb("Solo_Keys"));
        Assert.Contains("Unknown instrument key", ex.Message);
    }

    [Fact]
    public void GetLeaderboardWithCount_returns_entries_and_count()
    {
        using var glp = CreatePersistence();
        var db = glp.GetOrCreateInstrumentDb("Solo_Guitar");
        db.UpsertEntries("song_1", new[]
        {
            new LeaderboardEntry { AccountId = "a1", Score = 300 },
            new LeaderboardEntry { AccountId = "a2", Score = 200 },
        });

        var result = glp.GetLeaderboardWithCount("song_1", "Solo_Guitar", top: 10);
        Assert.NotNull(result);
        var (entries, total) = result.Value;
        Assert.Equal(2, entries.Count);
        Assert.Equal(2, total);
    }

    [Fact]
    public void GetLeaderboardWithCount_unknown_instrument_returns_null()
    {
        using var glp = CreatePersistence();
        var result = glp.GetLeaderboardWithCount("song_1", "UnknownInst");
        Assert.Null(result);
    }

    [Fact]
    public void GetCurrentStatePlayerProfiles_reads_projected_current_rows_for_multiple_accounts()
    {
        using var glp = CreatePersistence();
        SeedCurrentLeaderboardEntry("acct_a", "song_1", "Solo_Guitar", 123_456, rank: 7);
        SeedCurrentLeaderboardEntry("acct_a", "song_2", "Solo_Bass", 234_567, rank: 11);
        SeedCurrentLeaderboardEntry("acct_b", "song_1", "Solo_Guitar", 345_678, rank: 3);

        var profiles = glp.GetCurrentStatePlayerProfiles(["acct_a", "acct_b", "missing"]);

        Assert.Equal(2, profiles.Count);
        Assert.Equal(2, profiles["acct_a"].Count);
        Assert.Single(profiles["acct_b"]);
        Assert.Contains(profiles["acct_a"], score => score is
        {
            SongId: "song_1",
            Instrument: "Solo_Guitar",
            Score: 123_456,
            Rank: 7,
            ApiRank: 7,
        });
        Assert.Contains(profiles["acct_a"], score => score is
        {
            SongId: "song_2",
            Instrument: "Solo_Bass",
            Score: 234_567,
            Rank: 11,
        });
    }

    [Fact]
    public void GetCurrentStatePlayerProfile_reads_projection_and_applies_filters()
    {
        using var glp = CreatePersistence();
        SeedCurrentLeaderboardEntry("acct_a", "song_1", "Solo_Guitar", 123_456, rank: 7);
        SeedCurrentLeaderboardEntry("acct_a", "song_1", "Solo_Bass", 234_567, rank: 11);
        SeedCurrentLeaderboardEntry("acct_a", "song_2", "Solo_Guitar", 345_678, rank: 3);
        SeedCurrentLeaderboardEntry("acct_b", "song_1", "Solo_Guitar", 456_789, rank: 2);

        var allScores = glp.GetCurrentStatePlayerProfile("acct_a");
        Assert.Equal(3, allScores.Count);

        var guitarScores = glp.GetCurrentStatePlayerProfile(
            "acct_a",
            instruments: new HashSet<string>(["Solo_Guitar"], StringComparer.OrdinalIgnoreCase));
        Assert.Equal(2, guitarScores.Count);
        Assert.All(guitarScores, score => Assert.Equal("Solo_Guitar", score.Instrument));

        var songAndInstrumentScores = glp.GetCurrentStatePlayerProfile(
            "acct_a",
            "song_1",
            new HashSet<string>(["Solo_Guitar"], StringComparer.OrdinalIgnoreCase));
        var score = Assert.Single(songAndInstrumentScores);
        Assert.Equal("song_1", score.SongId);
        Assert.Equal("Solo_Guitar", score.Instrument);
        Assert.Equal(123_456, score.Score);
        Assert.Equal(7, score.Rank);
        Assert.Equal(7, score.ApiRank);

        Assert.Empty(glp.GetCurrentStatePlayerProfile("missing"));
    }

    [Fact]
    public void GetCurrentStateSongIdsForMemberScoreFilter_applies_has_and_missing_conditions()
    {
        using var glp = CreatePersistence();
        SeedSong("song_1", leadDiff: 3);
        SeedSong("song_2", leadDiff: 3);
        SeedSong("song_3", leadDiff: 3);
        SeedSong("song_uncharted", leadDiff: 99);
        SeedCurrentLeaderboardEntry("acct_has", "song_1", "Solo_Guitar", 123_456, rank: 7);
        SeedCurrentLeaderboardEntry("acct_has", "song_2", "Solo_Guitar", 234_567, rank: 11);
        SeedCurrentLeaderboardEntry("acct_missing", "song_2", "Solo_Guitar", 345_678, rank: 3);

        var songIds = glp.GetCurrentStateSongIdsForMemberScoreFilter(
            hasScoreAccountIds: ["acct_has"],
            missingScoreAccountIds: ["acct_missing"],
            instruments: ["Solo_Guitar"]);

        Assert.Equal(["song_1"], songIds);
    }

    [Fact]
    public void GetCurrentStateSongIdsForMemberScoreFilter_counts_valid_history_fallback_when_leeway_filters_current_score()
    {
        using var glp = CreatePersistence();
        SeedSong("song_1", leadDiff: 3);
        SeedSongStats("song_1", "Solo_Guitar", maxScore: 100_000);
        SeedCurrentLeaderboardEntry("acct_has", "song_1", "Solo_Guitar", 150_000, rank: 7);

        var withoutFallback = glp.GetCurrentStateSongIdsForMemberScoreFilter(
            hasScoreAccountIds: ["acct_has"],
            missingScoreAccountIds: [],
            instruments: ["Solo_Guitar"],
            leeway: 0);
        Assert.Empty(withoutFallback);

        SeedScoreHistory("acct_has", "song_1", "Solo_Guitar", 99_000);

        var withFallback = glp.GetCurrentStateSongIdsForMemberScoreFilter(
            hasScoreAccountIds: ["acct_has"],
            missingScoreAccountIds: [],
            instruments: ["Solo_Guitar"],
            leeway: 0);
        Assert.Equal(["song_1"], withFallback);
    }

    private void SeedCurrentLeaderboardEntry(string accountId, string songId, string instrument, int score, int rank)
    {
        using var conn = _metaFixture.DataSource.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO current_leaderboard_entries (
                song_id, instrument, account_id, score, accuracy, is_full_combo, stars,
                season, percentile, rank, api_rank, source, difficulty, end_time,
                first_seen_at, last_updated_at, projection_generation, computed_at)
            VALUES (
                @songId, @instrument, @accountId, @score, 987000, TRUE, 5,
                9, 0.12, @rank, @rank, 'test', 3, '2026-05-05T00:00:00Z',
                @now, @now, 1, @now)
            ON CONFLICT (song_id, instrument, account_id) DO UPDATE SET
                score = EXCLUDED.score,
                rank = EXCLUDED.rank,
                api_rank = EXCLUDED.api_rank,
                last_updated_at = EXCLUDED.last_updated_at
            """;
        cmd.Parameters.AddWithValue("songId", songId);
        cmd.Parameters.AddWithValue("instrument", instrument);
        cmd.Parameters.AddWithValue("accountId", accountId);
        cmd.Parameters.AddWithValue("score", score);
        cmd.Parameters.AddWithValue("rank", rank);
        cmd.Parameters.AddWithValue("now", DateTime.UtcNow);
        cmd.ExecuteNonQuery();
    }

    private void SeedSong(string songId, int leadDiff = 0, int bassDiff = 0, int drumsDiff = 0, int vocalsDiff = 0)
    {
        using var conn = _metaFixture.DataSource.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO songs (
                song_id, title, artist, lead_diff, bass_diff, drums_diff, vocals_diff,
                pro_lead_diff, pro_bass_diff, plastic_guitar_diff, plastic_bass_diff,
                plastic_drums_diff, pro_vocals_diff)
            VALUES (
                @songId, @title, 'Artist', @lead, @bass, @drums, @vocals,
                @lead, @bass, @lead, @bass, @drums, @vocals)
            ON CONFLICT (song_id) DO UPDATE SET
                lead_diff = EXCLUDED.lead_diff,
                bass_diff = EXCLUDED.bass_diff,
                drums_diff = EXCLUDED.drums_diff,
                vocals_diff = EXCLUDED.vocals_diff,
                plastic_guitar_diff = EXCLUDED.plastic_guitar_diff,
                plastic_bass_diff = EXCLUDED.plastic_bass_diff,
                plastic_drums_diff = EXCLUDED.plastic_drums_diff,
                pro_vocals_diff = EXCLUDED.pro_vocals_diff
            """;
        cmd.Parameters.AddWithValue("songId", songId);
        cmd.Parameters.AddWithValue("title", songId);
        cmd.Parameters.AddWithValue("lead", leadDiff);
        cmd.Parameters.AddWithValue("bass", bassDiff);
        cmd.Parameters.AddWithValue("drums", drumsDiff);
        cmd.Parameters.AddWithValue("vocals", vocalsDiff);
        cmd.ExecuteNonQuery();
    }

    private void SeedSongStats(string songId, string instrument, int maxScore)
    {
        using var conn = _metaFixture.DataSource.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO song_stats (song_id, instrument, entry_count, log_weight, max_score, computed_at)
            VALUES (@songId, @instrument, 1, 1, @maxScore, @now)
            ON CONFLICT (song_id, instrument) DO UPDATE SET max_score = EXCLUDED.max_score
            """;
        cmd.Parameters.AddWithValue("songId", songId);
        cmd.Parameters.AddWithValue("instrument", instrument);
        cmd.Parameters.AddWithValue("maxScore", maxScore);
        cmd.Parameters.AddWithValue("now", DateTime.UtcNow);
        cmd.ExecuteNonQuery();
    }

    private void SeedScoreHistory(string accountId, string songId, string instrument, int score)
    {
        using var conn = _metaFixture.DataSource.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO score_history (
                song_id, instrument, account_id, new_score, accuracy, is_full_combo,
                stars, percentile, season, changed_at)
            VALUES (@songId, @instrument, @accountId, @score, 987000, TRUE, 5, 0.12, 9, @now)
            """;
        cmd.Parameters.AddWithValue("songId", songId);
        cmd.Parameters.AddWithValue("instrument", instrument);
        cmd.Parameters.AddWithValue("accountId", accountId);
        cmd.Parameters.AddWithValue("score", score);
        cmd.Parameters.AddWithValue("now", DateTime.UtcNow);
        cmd.ExecuteNonQuery();
    }

    // ═══ RecomputeAllRanks ══════════════════════════════════════

    [Fact]
    public void RecomputeAllRanks_updates_all_instruments()
    {
        using var glp = CreatePersistence();

        var guitarDb = glp.GetOrCreateInstrumentDb("Solo_Guitar");
        guitarDb.UpsertEntries("song_1", new[]
        {
            new LeaderboardEntry { AccountId = "a1", Score = 300 },
            new LeaderboardEntry { AccountId = "a2", Score = 200 },
        });

        var bassDb = glp.GetOrCreateInstrumentDb("Solo_Bass");
        bassDb.UpsertEntries("song_1", new[]
        {
            new LeaderboardEntry { AccountId = "a3", Score = 500 },
        });

        var total = glp.RecomputeAllRanks();
        Assert.Equal(3, total);

        Assert.Equal(1, guitarDb.GetEntry("song_1", "a1")!.Rank);
        Assert.Equal(2, guitarDb.GetEntry("song_1", "a2")!.Rank);
        Assert.Equal(1, bassDb.GetEntry("song_1", "a3")!.Rank);
    }

    [Fact]
    public void RecomputeAllRanks_skips_unchanged_ranks()
    {
        using var glp = CreatePersistence();
        var db = glp.GetOrCreateInstrumentDb("Solo_Guitar");
        db.UpsertEntries("song_1", new[]
        {
            new LeaderboardEntry { AccountId = "a1", Score = 300 },
            new LeaderboardEntry { AccountId = "a2", Score = 200 },
        });

        // First call assigns ranks from 0 → correct values
        var first = glp.RecomputeAllRanks();
        Assert.Equal(2, first);

        // Second call with no changes should skip all rows (ranks already correct)
        var second = glp.RecomputeAllRanks();
        Assert.Equal(0, second);
    }

    // ═══ GetSongCountsForInstruments ════════════════════════════

    [Fact]
    public void GetSongCountsForInstruments_returns_all_song_counts()
    {
        using var glp = CreatePersistence();
        var guitarDb = glp.GetOrCreateInstrumentDb("Solo_Guitar");
        guitarDb.UpsertEntries("song_1", new[]
        {
            new LeaderboardEntry { AccountId = "a1", Score = 300 },
            new LeaderboardEntry { AccountId = "a2", Score = 200 },
        });
        var bassDb = glp.GetOrCreateInstrumentDb("Solo_Bass");
        bassDb.UpsertEntries("song_1", new[]
        {
            new LeaderboardEntry { AccountId = "a3", Score = 500 },
        });

        var counts = glp.GetSongCountsForInstruments();
        Assert.Equal(2, counts[("song_1", "Solo_Guitar")]);
        Assert.Equal(1, counts[("song_1", "Solo_Bass")]);
    }

    // ═══ GetLeaderboardCount (GLP layer) ════════════════════════

    [Fact]
    public void GetLeaderboardCount_returns_count_for_known_instrument()
    {
        using var glp = CreatePersistence();
        var db = glp.GetOrCreateInstrumentDb("Solo_Guitar");
        db.UpsertEntries("song_1", new[]
        {
            new LeaderboardEntry { AccountId = "a1", Score = 100 },
            new LeaderboardEntry { AccountId = "a2", Score = 200 },
        });

        var count = glp.GetLeaderboardCount("song_1", "Solo_Guitar");
        Assert.Equal(2, count);
    }

    [Fact]
    public void GetLeaderboardCount_returns_null_for_unknown_instrument()
    {
        using var glp = CreatePersistence();
        var count = glp.GetLeaderboardCount("song_1", "Unknown_Instrument");
        Assert.Null(count);
    }

    // ═══ GetLeaderboard (GLP layer) ═════════════════════════════

    [Fact]
    public void GetLeaderboard_GlpLayer_returns_null_for_unknown_instrument()
    {
        using var glp = CreatePersistence();
        var entries = glp.GetLeaderboard("song_1", "Unknown_Instrument");
        Assert.Null(entries);
    }

    // ═══ PruneAllInstruments ════════════════════════════════

    [Fact]
    public void PruneAllInstruments_RemovesExcessEntries()
    {
        using var glp = CreatePersistence();
        glp.Initialize();

        var db = glp.GetOrCreateInstrumentDb("Solo_Guitar");
        var entries = Enumerable.Range(0, 50).Select(i =>
            new LeaderboardEntry
            {
                AccountId = $"p_{i}", Score = 5000 - i * 10,
                Accuracy = 95, Stars = 5, Season = 3,
            }).ToList();
        db.UpsertEntries("song1", entries);

        var preserve = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "p_40" };
        var deleted = glp.PruneAllInstruments(10, preserve);

        Assert.Equal(39, deleted); // 50 - 10 (top) - 1 (preserved) = 39
        Assert.Equal(11, db.GetLeaderboardCount("song1")); // 10 + 1 preserved
    }

    [Fact]
    public void PruneAllInstruments_ZeroMax_ReturnsZero()
    {
        using var glp = CreatePersistence();
        glp.Initialize();

        var deleted = glp.PruneAllInstruments(0, new HashSet<string>());
        Assert.Equal(0, deleted);
    }

    [Fact]
    public void PruneAllInstruments_WhenLegacyLiveWritesDisabled_ReturnsZeroAndLeavesRows()
    {
        using var glp = CreatePersistence(new FeatureOptions { WriteLegacyLiveLeaderboardDuringScrape = false });
        glp.Initialize();

        var db = glp.GetOrCreateInstrumentDb("Solo_Guitar");
        var entries = Enumerable.Range(0, 50).Select(i =>
            new LeaderboardEntry
            {
                AccountId = $"p_{i}", Score = 5000 - i * 10,
                Accuracy = 95, Stars = 5, Season = 3,
            }).ToList();
        db.UpsertEntries("song1", entries);

        var deleted = glp.PruneAllInstruments(10, new HashSet<string>());

        Assert.Equal(0, deleted);
        Assert.Equal(50, db.GetLeaderboardCount("song1"));
    }

    [Fact]
    public void IsReady_AfterInitialize_ReturnsTrue()
    {
        using var glp = CreatePersistence();
        glp.Initialize();

        Assert.True(glp.IsReady());
    }

    [Fact]
    public void IsReady_WithoutDbs_ReturnsFalse()
    {
        // Create but don't initialize — no instrument DBs exist
        var tempDir = Path.Combine(Path.GetTempPath(), $"fst_ready_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var metaFixture = new InMemoryMetaDatabase();
            var loggerFactory = new Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory();
            var glp = new GlobalLeaderboardPersistence(
                metaFixture.Db, loggerFactory,
                Microsoft.Extensions.Logging.Abstractions.NullLogger<GlobalLeaderboardPersistence>.Instance,
                metaFixture.DataSource,
                Options.Create(new FeatureOptions()));
            // Don't call Initialize — no DBs
            Assert.False(glp.IsReady());
            glp.Dispose();
            metaFixture.Dispose();
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    // ═══ CheckpointAll ══════════════════════════════════════════

    [Fact]
    public void CheckpointAll_succeeds_after_writes()
    {
        using var glp = CreatePersistence();

        glp.PersistResult(MakeResult("song_1", "Solo_Guitar",
            ("acct_1", 100_000), ("acct_2", 90_000)));

        // Should not throw — checkpoints all instrument DBs + meta DB
        glp.CheckpointAll();

        // Data should still be readable
        var db = glp.GetOrCreateInstrumentDb("Solo_Guitar");
        Assert.Equal(2, db.GetLeaderboardCount("song_1"));
    }

    [Fact]
    public void CheckpointAll_succeeds_on_empty_databases()
    {
        using var glp = CreatePersistence();

        // Should not throw even with no data written
        glp.CheckpointAll();
    }

    // ═══ Deferred Account IDs ═══════════════════════════════════

    [Fact]
    public void PipelineAggregates_deferred_account_ids_accumulates_and_deduplicates()
    {
        var agg = new GlobalLeaderboardPersistence.PipelineAggregates();

        agg.AddDeferredAccountIds(["acct_1", "acct_2", "acct_3"]);
        agg.AddDeferredAccountIds(["acct_2", "acct_3", "acct_4"]);

        Assert.Equal(4, agg.DeferredAccountIds.Count);
    }

    [Fact]
    public void PipelineAggregates_deferred_account_ids_thread_safe()
    {
        var agg = new GlobalLeaderboardPersistence.PipelineAggregates();

        Parallel.For(0, 1000, i =>
        {
            agg.AddDeferredAccountIds([$"acct_{i % 100}"]);
        });

        Assert.Equal(100, agg.DeferredAccountIds.Count);
    }

    [Fact]
    public async Task PersistResult_defers_account_ids_when_writers_active()
    {
        using var glp = CreatePersistence();
        var agg = glp.StartWriters();

        await glp.EnqueueResultAsync(
            MakeResult("song_1", "Solo_Guitar", ("acct_1", 100_000), ("acct_2", 90_000)),
            registeredAccountIds: null);

        await glp.DrainWritersAsync();

        // Account IDs should be accumulated in aggregates, not written yet
        Assert.True(agg.DeferredAccountIds.Count >= 2);
        Assert.Contains("acct_1", agg.DeferredAccountIds);
        Assert.Contains("acct_2", agg.DeferredAccountIds);
    }

    [Fact]
    public async Task FlushDeferredAccountIds_writes_to_meta_db()
    {
        using var glp = CreatePersistence();
        var agg = glp.StartWriters();

        await glp.EnqueueResultAsync(
            MakeResult("song_1", "Solo_Guitar", ("acct_flush_1", 100_000), ("acct_flush_2", 90_000)),
            registeredAccountIds: null);

        await glp.DrainWritersAsync();

        // Before flush — account IDs should be in deferred set
        Assert.True(agg.DeferredAccountIds.Count >= 2);

        // Flush
        var inserted = glp.FlushDeferredAccountIds();

        // After flush — account IDs should be in meta DB
        var unresolved = _metaFixture.Db.GetUnresolvedAccountIds();
        Assert.Contains("acct_flush_1", unresolved);
        Assert.Contains("acct_flush_2", unresolved);
        Assert.True(inserted >= 2);
    }

    [Fact]
    public void PersistResult_writes_account_ids_immediately_without_writers()
    {
        using var glp = CreatePersistence();

        // PersistResult without StartWriters — should write immediately
        glp.PersistResult(MakeResult("song_1", "Solo_Guitar", ("acct_direct", 100_000)));

        var unresolved = _metaFixture.Db.GetUnresolvedAccountIds();
        Assert.Contains("acct_direct", unresolved);
    }

    [Fact]
    public async Task FlushDeferredAccountIds_catches_database_exception_and_returns_zero()
    {
        // Arrange: mock IMetaDatabase that throws on InsertAccountIds
        var mockMeta = Substitute.For<IMetaDatabase>();
        mockMeta.When(m => m.InsertAccountIds(Arg.Any<IEnumerable<string>>()))
                .Do(_ => throw new InvalidOperationException("Simulated DB timeout"));

        using var glp = new GlobalLeaderboardPersistence(
            mockMeta, new NullLoggerFactory(),
            NullLogger<GlobalLeaderboardPersistence>.Instance,
            _metaFixture.DataSource,
            Options.Create(new FeatureOptions()));
        glp.Initialize();
        var agg = glp.StartWriters();

        await glp.EnqueueResultAsync(
            MakeResult("song_1", "Solo_Guitar", ("acct_err_1", 100_000)),
            registeredAccountIds: null);
        await glp.DrainWritersAsync();

        Assert.True(agg.DeferredAccountIds.Count >= 1);

        // Act: flush should catch the exception and return 0
        var inserted = glp.FlushDeferredAccountIds();

        // Assert: no exception thrown, returns 0
        Assert.Equal(0, inserted);
    }
}
