using FSTService.Persistence;
using FSTService.Scraping;
using FSTService.Tests.Helpers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace FSTService.Tests.Unit;

public sealed class SoloCurrentProjectionBuilderTests : IDisposable
{
    private readonly TempInstrumentDatabase _fixture = new();

    public void Dispose() => _fixture.Dispose();

    [Fact]
    public async Task LoadStaleScopesAsync_returns_missing_failed_and_snapshot_mismatched_scopes()
    {
        var builder = new SoloCurrentProjectionBuilder(
            _fixture.DataSource,
            Substitute.For<ILogger<SoloCurrentProjectionBuilder>>());
        await builder.EnsureSchemaAsync();

        InsertSnapshotState("song_fresh", 42);
        InsertSnapshotState("song_stale", 42);
        InsertSnapshotState("song_missing", 42);
        InsertSnapshotState("song_failed", 42);
        InsertProjectionScope("song_fresh", sourceSnapshotId: 42, status: "ready");
        InsertProjectionScope("song_stale", sourceSnapshotId: 41, status: "ready");
        InsertProjectionScope("song_failed", sourceSnapshotId: 42, status: "failed");

        var scopes = await builder.LoadStaleScopesAsync();

        var scopeIds = scopes.Select(static scope => scope.SongId).Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(["song_failed", "song_missing", "song_stale"], scopeIds);
    }

    [Fact]
    public async Task AreActiveScopesFreshForInstruments_requires_ready_matching_active_snapshots()
    {
        var builder = new SoloCurrentProjectionBuilder(
            _fixture.DataSource,
            Substitute.For<ILogger<SoloCurrentProjectionBuilder>>());
        await builder.EnsureSchemaAsync();

        InsertSnapshotState("song_fresh", 42);
        InsertProjectionScope("song_fresh", sourceSnapshotId: 42, status: "ready");

        Assert.True(builder.AreActiveScopesFreshForInstruments([_fixture.Db.Instrument]));

        InsertSnapshotState("song_stale", 43);
        InsertProjectionScope("song_stale", sourceSnapshotId: 42, status: "ready");

        Assert.False(builder.AreActiveScopesFreshForInstruments([_fixture.Db.Instrument]));
    }

    [Fact]
    public async Task AreActiveScopesFreshForInstruments_uses_published_snapshot_during_public_read_freeze()
    {
        var builder = new SoloCurrentProjectionBuilder(
            _fixture.DataSource,
            Substitute.For<ILogger<SoloCurrentProjectionBuilder>>());
        await builder.EnsureSchemaAsync();

        InsertSnapshotState("song_frozen", 816);
        InsertProjectionScope("song_frozen", sourceSnapshotId: 815, status: "ready");
        SetPublicationState(publishedScrapeId: 815, publicReadsFrozen: true);

        Assert.True(builder.AreActiveScopesFreshForInstruments([_fixture.Db.Instrument]));
    }

    [Fact]
    public async Task SnapshotOverlayWorkerReaders_ExcludeLegacyOnlyScopesAndRebuildOverlayOnlyScopes()
    {
        _fixture.Db.UpsertEntries("song-legacy-only",
        [
            new LeaderboardEntry
            {
                AccountId = "acct-legacy",
                Score = 999_000,
                Accuracy = 100,
                Stars = 6,
                Season = 3,
                Source = "scrape",
                EndTime = "2025-01-15T12:00:00Z",
            },
        ]);
        var legacyBuilder = new SoloCurrentProjectionBuilder(
            _fixture.DataSource,
            Substitute.For<ILogger<SoloCurrentProjectionBuilder>>());
        await legacyBuilder.EnsureSchemaAsync();
        await legacyBuilder.RebuildScopeAsync(new SoloCurrentProjectionScopeKey(
            "song-legacy-only",
            _fixture.Db.Instrument));
        _fixture.Db.WriteLegacyLiveLeaderboardSupplementalRows = false;
        _fixture.Db.UpsertEntries("song-overlay-only",
        [
            new LeaderboardEntry
            {
                AccountId = "acct-overlay",
                Score = 100_000,
                Accuracy = 95,
                Stars = 5,
                Season = 3,
                Source = "backfill",
                EndTime = "2025-01-15T12:00:00Z",
            },
        ]);

        var builder = new SoloCurrentProjectionBuilder(
            _fixture.DataSource,
            Substitute.For<ILogger<SoloCurrentProjectionBuilder>>(),
            Options.Create(new FeatureOptions { UseSnapshotOverlayWorkerReaders = true }));
        await builder.EnsureSchemaAsync();

        var scopes = await builder.LoadCurrentScopesAsync();
        var prunedRows = await builder.PruneOrphanedScopesAsync();
        await builder.RebuildScopeAsync(new SoloCurrentProjectionScopeKey(
            "song-overlay-only",
            _fixture.Db.Instrument));

        Assert.Equal(1, prunedRows);
        Assert.Contains(scopes, scope => scope.SongId == "song-overlay-only");
        Assert.DoesNotContain(scopes, scope => scope.SongId == "song-legacy-only");
        using var connection = _fixture.DataSource.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT song_id, account_id, score
            FROM current_leaderboard_entries
            ORDER BY song_id, account_id
            """;
        using var reader = command.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal("song-overlay-only", reader.GetString(0));
        Assert.Equal("acct-overlay", reader.GetString(1));
        Assert.Equal(100_000, reader.GetInt32(2));
        Assert.False(reader.Read());
    }

    [Fact]
    public async Task SnapshotOverlayWorkerReaders_RebuildMixedScopesCreatedFromLegacyRows()
    {
        const string songId = "song-mixed-source";
        _fixture.Db.UpsertEntries(songId,
        [
            new LeaderboardEntry
            {
                AccountId = "acct-legacy",
                Score = 999_000,
                Accuracy = 100,
                Stars = 6,
                Season = 3,
                Source = "scrape",
                EndTime = "2025-01-15T12:00:00Z",
            },
        ]);
        var legacyBuilder = new SoloCurrentProjectionBuilder(
            _fixture.DataSource,
            Substitute.For<ILogger<SoloCurrentProjectionBuilder>>());
        await legacyBuilder.EnsureSchemaAsync();
        await legacyBuilder.RebuildScopeAsync(new SoloCurrentProjectionScopeKey(
            songId,
            _fixture.Db.Instrument));

        _fixture.Db.WriteLegacyLiveLeaderboardSupplementalRows = false;
        _fixture.Db.UpsertEntries(songId,
        [
            new LeaderboardEntry
            {
                AccountId = "acct-overlay",
                Score = 100_000,
                Accuracy = 95,
                Stars = 5,
                Season = 3,
                Source = "backfill",
                EndTime = "2025-01-15T12:00:00Z",
            },
        ]);
        await legacyBuilder.RebuildScopeAsync(new SoloCurrentProjectionScopeKey(
            songId,
            _fixture.Db.Instrument));
        var candidateBuilder = new SoloCurrentProjectionBuilder(
            _fixture.DataSource,
            Substitute.For<ILogger<SoloCurrentProjectionBuilder>>(),
            Options.Create(new FeatureOptions { UseSnapshotOverlayWorkerReaders = true }));
        await candidateBuilder.EnsureSchemaAsync();

        var staleScopes = await candidateBuilder.LoadStaleScopesAsync();
        var result = await candidateBuilder.RefreshScopesAsync(staleScopes);

        Assert.Contains(staleScopes, scope => scope.SongId == songId);
        Assert.Equal(1, result.SucceededScopeCount);
        using var connection = _fixture.DataSource.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT projection.account_id, scope.source_kind
            FROM current_leaderboard_entries projection
            JOIN solo_current_projection_scope scope
              ON scope.song_id = projection.song_id
             AND scope.instrument = projection.instrument
             AND scope.projection_generation = projection.projection_generation
            WHERE projection.song_id = @songId
              AND projection.instrument = @instrument
            """;
        command.Parameters.AddWithValue("songId", songId);
        command.Parameters.AddWithValue("instrument", _fixture.Db.Instrument);
        using var reader = command.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal("acct-overlay", reader.GetString(0));
        Assert.Equal("overlay", reader.GetString(1));
        Assert.False(reader.Read());
    }

    [Fact]
    public async Task RebuildScopeAsync_records_observe_only_diff_metrics()
    {
        var builder = new SoloCurrentProjectionBuilder(
            _fixture.DataSource,
            Substitute.For<ILogger<SoloCurrentProjectionBuilder>>());
        await builder.EnsureSchemaAsync();
        SeedLiveLeaderboard("song_metrics",
            ("user1", 1000),
            ("user2", 900));

        await builder.RebuildScopeAsync(new SoloCurrentProjectionScopeKey("song_metrics", _fixture.Db.Instrument));
        var first = ReadProjectionScopeMetrics("song_metrics");
        Assert.Equal(0, first.ExistingRows);
        Assert.Equal(2, first.DesiredRows);
        Assert.Equal(0, first.UnchangedRows);
        Assert.Equal(2, first.WouldInsertRows);
        Assert.Equal(0, first.WouldUpdateRows);
        Assert.Equal(0, first.WouldDeleteRows);

        await builder.RebuildScopeAsync(new SoloCurrentProjectionScopeKey("song_metrics", _fixture.Db.Instrument));
        var unchanged = ReadProjectionScopeMetrics("song_metrics");
        Assert.Equal(2, unchanged.ExistingRows);
        Assert.Equal(2, unchanged.DesiredRows);
        Assert.Equal(2, unchanged.UnchangedRows);
        Assert.Equal(0, unchanged.WouldInsertRows);
        Assert.Equal(0, unchanged.WouldUpdateRows);
        Assert.Equal(0, unchanged.WouldDeleteRows);

        SeedLiveLeaderboard("song_metrics",
            ("user1", 1000),
            ("user2", 950),
            ("user3", 925));

        await builder.RebuildScopeAsync(new SoloCurrentProjectionScopeKey("song_metrics", _fixture.Db.Instrument));
        var changed = ReadProjectionScopeMetrics("song_metrics");
        Assert.Equal(2, changed.ExistingRows);
        Assert.Equal(3, changed.DesiredRows);
        Assert.Equal(1, changed.UnchangedRows);
        Assert.Equal(1, changed.WouldInsertRows);
        Assert.Equal(1, changed.WouldUpdateRows);
        Assert.Equal(0, changed.WouldDeleteRows);
    }

    [Fact]
    public async Task RebuildScopeAsync_orders_exact_score_and_timestamp_ties_by_account_id()
    {
        var builder = new SoloCurrentProjectionBuilder(
            _fixture.DataSource,
            Substitute.For<ILogger<SoloCurrentProjectionBuilder>>());
        await builder.EnsureSchemaAsync();
        SeedLiveLeaderboard("song_exact_tie",
            ("acct-z", 1000),
            ("acct-a", 1000),
            ("acct-m", 1000));

        await builder.RebuildScopeAsync(new SoloCurrentProjectionScopeKey("song_exact_tie", _fixture.Db.Instrument));

        using var conn = _fixture.DataSource.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT account_id, rank, end_time
            FROM current_leaderboard_entries
            WHERE song_id = @songId
              AND instrument = @instrument
            ORDER BY rank
            """;
        cmd.Parameters.AddWithValue("songId", "song_exact_tie");
        cmd.Parameters.AddWithValue("instrument", _fixture.Db.Instrument);
        using var reader = cmd.ExecuteReader();
        var rows = new List<(string AccountId, int Rank, string EndTime)>();
        while (reader.Read())
            rows.Add((reader.GetString(0), reader.GetInt32(1), reader.GetString(2)));

        Assert.Equal(["acct-a", "acct-m", "acct-z"], rows.Select(static row => row.AccountId));
        Assert.Equal([1, 2, 3], rows.Select(static row => row.Rank));
        Assert.All(rows, static row => Assert.Equal("2025-01-15T12:00:00Z", row.EndTime));
    }

    [Fact]
    public void Stored_rank_offset_precedence_matches_the_total_rank_order()
    {
        using var conn = _fixture.DataSource.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            WITH rows(account_id, score, end_time, first_seen_at) AS (
                VALUES
                    ('acct-high'::TEXT, 1100, '2025-01-15T12:00:00Z'::TEXT, '2025-01-15T12:00:00Z'::TIMESTAMPTZ),
                    ('acct-early', 1000, '2025-01-15T11:59:59Z', '2025-01-15T12:00:00Z'::TIMESTAMPTZ),
                    ('acct-z', 1000, '2025-01-15T12:00:00Z', '2025-01-15T12:00:00Z'::TIMESTAMPTZ),
                    ('acct-a', 1000, '2025-01-15T12:00:00Z', '2025-01-15T12:00:00Z'::TIMESTAMPTZ),
                    ('acct-m', 1000, '2025-01-15T12:00:00Z', '2025-01-15T12:00:00Z'::TIMESTAMPTZ),
                    ('acct-late', 1000, '2025-01-15T12:00:01Z', '2025-01-15T12:00:00Z'::TIMESTAMPTZ),
                    ('acct-low', 900, '2025-01-15T12:00:00Z', '2025-01-15T12:00:00Z'::TIMESTAMPTZ)
            ),
            ranked AS (
                SELECT rows.*,
                       ROW_NUMBER() OVER (ORDER BY {SoloLeaderboardOrderingSql.OrderBy()}) AS expected_rank
                FROM rows
            )
            SELECT target.account_id,
                   target.expected_rank,
                   1 + COUNT(*) FILTER (
                       WHERE {SoloLeaderboardOrderingSql.Precedes("candidate", "target")}
                   ) AS classified_rank
            FROM ranked target
            CROSS JOIN rows candidate
            GROUP BY target.account_id, target.expected_rank
            ORDER BY target.expected_rank
            """;
        using var reader = cmd.ExecuteReader();
        var rows = new List<(string AccountId, long ExpectedRank, long ClassifiedRank)>();
        while (reader.Read())
            rows.Add((reader.GetString(0), reader.GetInt64(1), reader.GetInt64(2)));

        Assert.Equal(rows.Select(static row => row.ExpectedRank), rows.Select(static row => row.ClassifiedRank));
        Assert.Equal(
            ["acct-high", "acct-early", "acct-a", "acct-m", "acct-z", "acct-late", "acct-low"],
            rows.Select(static row => row.AccountId));
    }

    private void InsertSnapshotState(string songId, long activeSnapshotId)
    {
        using var conn = _fixture.DataSource.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO leaderboard_snapshot_state
            (song_id, instrument, active_snapshot_id, scrape_id, is_finalized, updated_at)
            VALUES (@songId, @instrument, @activeSnapshotId, @activeSnapshotId, TRUE, @now)
            ON CONFLICT (song_id, instrument) DO UPDATE SET
                active_snapshot_id = EXCLUDED.active_snapshot_id,
                scrape_id = EXCLUDED.scrape_id,
                is_finalized = EXCLUDED.is_finalized,
                updated_at = EXCLUDED.updated_at
            """;
        cmd.Parameters.AddWithValue("songId", songId);
        cmd.Parameters.AddWithValue("instrument", _fixture.Db.Instrument);
        cmd.Parameters.AddWithValue("activeSnapshotId", activeSnapshotId);
        cmd.Parameters.AddWithValue("now", DateTime.UtcNow);
        cmd.ExecuteNonQuery();
    }

    private void SeedLiveLeaderboard(string songId, params (string AccountId, int Score)[] entries)
    {
        _fixture.Db.UpsertEntries(songId, entries.Select(entry => new LeaderboardEntry
        {
            AccountId = entry.AccountId,
            Score = entry.Score,
            Accuracy = 95,
            Stars = 5,
            Season = 1,
            Source = "test",
            EndTime = "2025-01-15T12:00:00Z",
        }).ToList());
        _fixture.Db.RecomputeRanksForSongs([songId]);
    }

    private ProjectionScopeMetrics ReadProjectionScopeMetrics(string songId)
    {
        using var conn = _fixture.DataSource.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT existing_row_count,
                   desired_row_count,
                   unchanged_row_count,
                   would_insert_count,
                   would_update_count,
                   would_delete_count
            FROM solo_current_projection_scope
            WHERE song_id = @songId
              AND instrument = @instrument
            """;
        cmd.Parameters.AddWithValue("songId", songId);
        cmd.Parameters.AddWithValue("instrument", _fixture.Db.Instrument);
        using var reader = cmd.ExecuteReader();
        Assert.True(reader.Read());
        return new ProjectionScopeMetrics(
            reader.GetInt64(0),
            reader.GetInt64(1),
            reader.GetInt64(2),
            reader.GetInt64(3),
            reader.GetInt64(4),
            reader.GetInt64(5));
    }

    private void InsertProjectionScope(string songId, long? sourceSnapshotId, string status)
    {
        using var conn = _fixture.DataSource.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO solo_current_projection_scope
            (song_id, instrument, projection_generation, row_count, source_snapshot_id, source_kind,
             status, error_message, last_rebuilt_at, updated_at)
            VALUES (@songId, @instrument, 1, 1, @sourceSnapshotId, @sourceKind, @status, NULL, @now, @now)
            """;
        cmd.Parameters.AddWithValue("songId", songId);
        cmd.Parameters.AddWithValue("instrument", _fixture.Db.Instrument);
        cmd.Parameters.AddWithValue("sourceSnapshotId", sourceSnapshotId.HasValue ? sourceSnapshotId.Value : DBNull.Value);
        cmd.Parameters.AddWithValue("sourceKind", sourceSnapshotId.HasValue ? "snapshot" : "legacy-compatible");
        cmd.Parameters.AddWithValue("status", status);
        cmd.Parameters.AddWithValue("now", DateTime.UtcNow);
        cmd.ExecuteNonQuery();
    }

    private void SetPublicationState(int publishedScrapeId, bool publicReadsFrozen)
    {
        ScrapeRunTestHelper.EnsureAllocated(
            _fixture.DataSource,
            publishedScrapeId,
            completed: true);
        using var conn = _fixture.DataSource.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO scrape_publication_state
            (id, published_scrape_id, published_at, public_reads_frozen, public_reads_frozen_at, public_reads_frozen_reason, updated_at)
            VALUES (TRUE, @publishedScrapeId, @now, @publicReadsFrozen, CASE WHEN @publicReadsFrozen THEN @now ELSE NULL END, CASE WHEN @publicReadsFrozen THEN 'publish' ELSE NULL END, @now)
            ON CONFLICT (id) DO UPDATE SET
                published_scrape_id = EXCLUDED.published_scrape_id,
                published_at = EXCLUDED.published_at,
                public_reads_frozen = EXCLUDED.public_reads_frozen,
                public_reads_frozen_at = EXCLUDED.public_reads_frozen_at,
                public_reads_frozen_reason = EXCLUDED.public_reads_frozen_reason,
                updated_at = EXCLUDED.updated_at
            """;
        cmd.Parameters.AddWithValue("publishedScrapeId", publishedScrapeId);
        cmd.Parameters.AddWithValue("publicReadsFrozen", publicReadsFrozen);
        cmd.Parameters.AddWithValue("now", DateTime.UtcNow);
        cmd.ExecuteNonQuery();
    }

    private sealed record ProjectionScopeMetrics(
        long ExistingRows,
        long DesiredRows,
        long UnchangedRows,
        long WouldInsertRows,
        long WouldUpdateRows,
        long WouldDeleteRows);
}
