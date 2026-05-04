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
}