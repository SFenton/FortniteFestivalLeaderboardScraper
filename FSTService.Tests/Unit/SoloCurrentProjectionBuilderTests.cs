using FSTService.Persistence;
using FSTService.Tests.Helpers;
using Microsoft.Extensions.Logging;
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

    private void InsertProjectionScope(string songId, long? sourceSnapshotId, string status)
    {
        using var conn = _fixture.DataSource.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO solo_current_projection_scope
            (song_id, instrument, projection_generation, row_count, source_snapshot_id, status, error_message, last_rebuilt_at, updated_at)
            VALUES (@songId, @instrument, 1, 1, @sourceSnapshotId, @status, NULL, @now, @now)
            """;
        cmd.Parameters.AddWithValue("songId", songId);
        cmd.Parameters.AddWithValue("instrument", _fixture.Db.Instrument);
        cmd.Parameters.AddWithValue("sourceSnapshotId", sourceSnapshotId.HasValue ? sourceSnapshotId.Value : DBNull.Value);
        cmd.Parameters.AddWithValue("status", status);
        cmd.Parameters.AddWithValue("now", DateTime.UtcNow);
        cmd.ExecuteNonQuery();
    }

    private void SetPublicationState(int publishedScrapeId, bool publicReadsFrozen)
    {
        using var conn = _fixture.DataSource.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO scrape_log (id, started_at, completed_at)
            VALUES (@publishedScrapeId, @now, @now)
            ON CONFLICT (id) DO NOTHING;

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
}