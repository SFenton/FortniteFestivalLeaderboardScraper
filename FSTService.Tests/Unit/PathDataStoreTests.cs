using FSTService.Persistence;
using FSTService.Scraping;
using FSTService.Tests.Helpers;

namespace FSTService.Tests.Unit;

public sealed class PathDataStoreTests : IDisposable
{
    private readonly Npgsql.NpgsqlDataSource _ds;
    private readonly PathDataStore _store;

    public PathDataStoreTests()
    {
        _ds = SharedPostgresContainer.CreateDatabase();
        _store = new PathDataStore(_ds);
    }

    public void Dispose()
    {
        _ds.Dispose();
    }

    private void EnsureSongRow(string songId)
    {
        using var conn = _ds.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"INSERT INTO songs (song_id) VALUES ('{songId}') ON CONFLICT DO NOTHING;";
        cmd.ExecuteNonQuery();
    }

    private void SetCatalogLastModified(
        string songId,
        string? lastModified)
    {
        using var conn = _ds.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE songs
            SET last_modified = @lastModified
            WHERE song_id = @songId
            """;
        cmd.Parameters.AddWithValue("songId", songId);
        cmd.Parameters.AddWithValue(
            "lastModified",
            (object?)lastModified ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    [Fact]
    public void GetPathGenerationState_returns_only_songs_with_hashes()
    {
        EnsureSongRow("song1");
        EnsureSongRow("song2");
        EnsureSongRow("song3");
        _store.UpdateMaxScores("song3", new SongMaxScores { MaxLeadScore = 50000 }, "abc123");

        var state = _store.GetPathGenerationStates();

        Assert.Single(state);
        Assert.True(state.ContainsKey("song3"));
        Assert.Equal("abc123", state["song3"].DatFileHash);
    }

    [Fact]
    public void GetAllMaxScores_returns_empty_when_no_scores()
    {
        var scores = _store.GetAllMaxScores();
        Assert.Empty(scores);
    }

    [Fact]
    public void Pending_path_generation_ids_are_durable()
    {
        EnsureSongRow("pending");
        using (var conn = _ds.OpenConnection())
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                UPDATE songs
                SET path_generation_pending = TRUE
                WHERE song_id = 'pending'
                """;
            cmd.ExecuteNonQuery();
        }

        var pending = _store.GetPendingPathGenerationSongIds();
        Assert.Single(pending);
        Assert.Contains("pending", pending);
    }

    [Fact]
    public void GetPathGenerationState_returns_hash_and_lastModified()
    {
        EnsureSongRow("song1");
        var scores = new SongMaxScores { MaxLeadScore = 50000 };
        _store.UpdateMaxScores("song1", scores, "hash1", "2026-01-01T00:00:00Z");

        var state = _store.GetPathGenerationStates();
        Assert.True(state.ContainsKey("song1"));
        Assert.Equal("hash1", state["song1"].DatFileHash);
        Assert.Equal("2026-01-01T00:00:00Z", state["song1"].SongLastModified);
    }

    [Fact]
    public void GetPathGenerationState_returns_null_lastModified_when_not_set()
    {
        EnsureSongRow("song1");
        var scores = new SongMaxScores { MaxLeadScore = 50000 };
        _store.UpdateMaxScores("song1", scores, "hash1");

        var state = _store.GetPathGenerationStates();
        Assert.True(state.ContainsKey("song1"));
        Assert.Equal("hash1", state["song1"].DatFileHash);
        Assert.Null(state["song1"].SongLastModified);
    }

    [Fact]
    public void UpdateMaxScores_then_GetAllMaxScores_returns_data()
    {
        EnsureSongRow("song1");
        var scores = new SongMaxScores
        {
            MaxLeadScore = 100000,
            MaxBassScore = 80000,
            MaxDrumsScore = 120000,
            MaxVocalsScore = 70000,
            MaxProLeadScore = 110000,
            MaxProBassScore = 90000,
            GeneratedAt = "2026-01-01T00:00:00Z",
            CHOptVersion = "1.10.3",
        };

        _store.UpdateMaxScores("song1", scores, "newhash");

        var all = _store.GetAllMaxScores();
        Assert.Single(all);
        Assert.True(all.ContainsKey("song1"));
        Assert.Equal(100000, all["song1"].MaxLeadScore);
        Assert.Equal(80000, all["song1"].MaxBassScore);
        Assert.Equal(120000, all["song1"].MaxDrumsScore);
        Assert.Equal(70000, all["song1"].MaxVocalsScore);
        Assert.Equal(110000, all["song1"].MaxProLeadScore);
        Assert.Equal(90000, all["song1"].MaxProBassScore);
    }

    [Fact]
    public void UpdateMaxScores_updates_dat_file_hash()
    {
        EnsureSongRow("song1");
        var scores = new SongMaxScores { MaxLeadScore = 50000 };
        _store.UpdateMaxScores("song1", scores, "hash_abc");

        var state = _store.GetPathGenerationStates();
        Assert.Equal("hash_abc", state["song1"].DatFileHash);
    }

    [Fact]
    public void UpdateMaxScores_partial_null_scores()
    {
        EnsureSongRow("song1");
        var scores = new SongMaxScores
        {
            MaxLeadScore = 100000,
            MaxBassScore = null,
            MaxDrumsScore = 120000,
        };

        _store.UpdateMaxScores("song1", scores, "hash");
        var all = _store.GetAllMaxScores();

        Assert.Equal(100000, all["song1"].MaxLeadScore);
        Assert.Null(all["song1"].MaxBassScore);
        Assert.Equal(120000, all["song1"].MaxDrumsScore);
    }

    [Fact]
    public async Task TryPromoteGenerationAsync_updates_all_fields_atomically_and_rejects_stale_revision()
    {
        EnsureSongRow("atomic");
        SetCatalogLastModified(
            "atomic",
            "2026-08-01T00:00:00.0000000Z");
        using (var conn = _ds.OpenConnection())
        using (var pending = conn.CreateCommand())
        {
            pending.CommandText = """
                UPDATE songs
                SET path_generation_pending = TRUE
                WHERE song_id = 'atomic'
                """;
            pending.ExecuteNonQuery();
        }
        var runtime = new PathGenerationRuntimeIdentity(
            "2.3.4",
            new string('a', 64),
            "profile-v2");
        var promotion = new PathGenerationPromotion(
            "attempt-1",
            "atomic",
            0,
            "generation-1",
            "dat-hash",
            "2026-08-01T00:00:00.0000000Z",
            DateTime.UtcNow,
            runtime,
            ["Solo_Guitar"],
            new SongMaxScores { MaxLeadScore = 123456 });

        var promoted = await _store.TryPromoteGenerationAsync(
            promotion,
            CancellationToken.None);
        var conflict = await _store.TryPromoteGenerationAsync(
            promotion with
            {
                AttemptId = "attempt-2",
                ArtifactGenerationId = "generation-2",
            },
            CancellationToken.None);

        Assert.Equal(PathGenerationPromotionOutcome.Promoted, promoted);
        Assert.Equal(PathGenerationPromotionOutcome.Conflict, conflict);
        var state = _store.GetPathGenerationState("atomic");
        Assert.NotNull(state);
        Assert.Equal(1, state!.Revision);
        Assert.Equal("generation-1", state.ArtifactGenerationId);
        Assert.Equal("dat-hash", state.DatFileHash);
        Assert.Equal(runtime.Version, state.ChoptVersion);
        Assert.Equal(runtime.BinarySha256, state.ChoptBinarySha256);
        Assert.Equal(runtime.Profile, state.GenerationProfile);
        Assert.Equal(["Solo_Guitar"], state.ExpectedInstruments);
        Assert.Equal(123456, state.MaxScores.MaxLeadScore);
        Assert.DoesNotContain(
            "atomic",
            _store.GetPendingPathGenerationSongIds());
    }

    [Fact]
    public async Task Promotion_accepts_equivalent_catalog_timestamp_precision()
    {
        EnsureSongRow("timestamp-precision");
        SetCatalogLastModified(
            "timestamp-precision",
            "2026-08-01T00:00:00.7170000Z");
        var promotion = new PathGenerationPromotion(
            "attempt-precision",
            "timestamp-precision",
            0,
            "generation-precision",
            "dat-hash",
            "2026-08-01T00:00:00.717Z",
            DateTime.UtcNow,
            new PathGenerationRuntimeIdentity(
                "2.3.4",
                new string('a', 64),
                "profile-v2"),
            ["Solo_Guitar"],
            new SongMaxScores { MaxLeadScore = 123456 });

        var outcome = await _store.TryPromoteGenerationAsync(
            promotion,
            CancellationToken.None);

        Assert.Equal(PathGenerationPromotionOutcome.Promoted, outcome);
        Assert.Equal(
            "generation-precision",
            _store.GetPathGenerationState("timestamp-precision")!
                .ArtifactGenerationId);
    }

    [Fact]
    public async Task Atomic_generation_rejects_legacy_writer_that_does_not_advance_revision()
    {
        EnsureSongRow("writer-fence");
        SetCatalogLastModified(
            "writer-fence",
            "2026-08-01T00:00:00.0000000Z");
        var runtime = new PathGenerationRuntimeIdentity(
            "2.3.4",
            new string('a', 64),
            "profile-v2");
        var promotion = new PathGenerationPromotion(
            "attempt-1",
            "writer-fence",
            0,
            "generation-1",
            "dat-hash",
            "2026-08-01T00:00:00.0000000Z",
            DateTime.UtcNow,
            runtime,
            ["Solo_Guitar"],
            new SongMaxScores { MaxLeadScore = 123456 });
        Assert.Equal(
            PathGenerationPromotionOutcome.Promoted,
            await _store.TryPromoteGenerationAsync(
                promotion,
                CancellationToken.None));

        using var conn = _ds.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE songs
            SET max_lead_score = 654321,
                dat_file_hash = 'legacy-overwrite',
                paths_generated_at = now(),
                chopt_version = '1.15.1'
            WHERE song_id = 'writer-fence'
            """;
        var error = Assert.Throws<Npgsql.PostgresException>(
            () => cmd.ExecuteNonQuery());
        Assert.Equal("55000", error.SqlState);

        var state = _store.GetPathGenerationState("writer-fence");
        Assert.NotNull(state);
        Assert.Equal(1, state!.Revision);
        Assert.Equal("generation-1", state.ArtifactGenerationId);
        Assert.Equal("dat-hash", state.DatFileHash);
        Assert.Equal(123456, state.MaxScores.MaxLeadScore);
    }

    [Fact]
    public async Task Promotion_rejects_stale_catalog_identity_and_preserves_pending_work()
    {
        EnsureSongRow("catalog-race");
        SetCatalogLastModified(
            "catalog-race",
            "2026-08-01T00:00:00.0000000Z");
        using (var conn = _ds.OpenConnection())
        using (var pending = conn.CreateCommand())
        {
            pending.CommandText = """
                UPDATE songs
                SET path_generation_pending = TRUE
                WHERE song_id = 'catalog-race'
                """;
            pending.ExecuteNonQuery();
        }
        var promotion = new PathGenerationPromotion(
            "attempt-stale",
            "catalog-race",
            0,
            "generation-stale",
            "dat-hash",
            "2026-08-01T00:00:00.0000000Z",
            DateTime.UtcNow,
            new PathGenerationRuntimeIdentity(
                "2.3.4",
                new string('a', 64),
                "profile-v2"),
            ["Solo_Guitar"],
            new SongMaxScores { MaxLeadScore = 123456 });

        SetCatalogLastModified(
            "catalog-race",
            "2026-08-02T00:00:00.0000000Z");
        var outcome = await _store.TryPromoteGenerationAsync(
            promotion,
            CancellationToken.None);

        Assert.Equal(PathGenerationPromotionOutcome.Conflict, outcome);
        var state = _store.GetPathGenerationState("catalog-race");
        Assert.NotNull(state);
        Assert.Equal(0, state!.Revision);
        Assert.Null(state.ArtifactGenerationId);
        Assert.Contains(
            "catalog-race",
            _store.GetPendingPathGenerationSongIds());
    }

    [Fact]
    public void Stale_max_score_query_cannot_reinstall_after_invalidation()
    {
        var flags =
            System.Reflection.BindingFlags.Instance |
            System.Reflection.BindingFlags.NonPublic;
        var revisionField = typeof(PathDataStore).GetField(
            "_maxScoresCacheRevision",
            flags)!;
        var invalidate = typeof(PathDataStore).GetMethod(
            "InvalidateMaxScoresCache",
            flags)!;
        var install = typeof(PathDataStore).GetMethod(
            "TryInstallMaxScoresCache",
            flags)!;
        var revision = (long)revisionField.GetValue(_store)!;
        invalidate.Invoke(_store, null);

        var installed = (bool)install.Invoke(
            _store,
            [
                new Dictionary<string, SongMaxScores>
                {
                    ["stale"] = new SongMaxScores
                    {
                        MaxLeadScore = 1,
                        ArtifactGenerationId = "stale-generation",
                    },
                },
                revision,
            ])!;

        Assert.False(installed);
        Assert.Empty(_store.GetAllMaxScores());
    }

    [Fact]
    public async Task AppendPathGenerationErrorAsync_is_append_only_and_bounds_detail()
    {
        var detail = new string('x', 3000);
        var error = new PathGenerationError(
            "attempt-1",
            "song-errors",
            "dat-hash",
            "1.2.3",
            new string('b', 64),
            "profile",
            ["Solo_Guitar"],
            "artifact_validation",
            "Solo_Guitar",
            "expert",
            detail,
            DateTime.UtcNow);

        await _store.AppendPathGenerationErrorAsync(error, CancellationToken.None);
        await _store.AppendPathGenerationErrorAsync(
            error with { AttemptId = "attempt-2" },
            CancellationToken.None);

        using var conn = _ds.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT COUNT(*), MAX(length(detail))
            FROM path_generation_errors
            WHERE song_id = @songId
            """;
        cmd.Parameters.AddWithValue("songId", "song-errors");
        using var reader = cmd.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal(2, reader.GetInt64(0));
        Assert.Equal(2048, reader.GetInt32(1));
    }
}

public sealed class SongMaxScoresTests
{
    [Theory]
    [InlineData("Solo_Guitar", 100)]
    [InlineData("Solo_Bass", 200)]
    [InlineData("Solo_Drums", 300)]
    [InlineData("Solo_Vocals", 400)]
    [InlineData("Solo_PeripheralGuitar", 500)]
    [InlineData("Solo_PeripheralBass", 600)]
    public void GetByInstrument_returns_correct_score(string instrument, int score)
    {
        var ms = new SongMaxScores();
        ms.SetByInstrument(instrument, score);
        Assert.Equal(score, ms.GetByInstrument(instrument));
    }

    [Fact]
    public void GetByInstrument_unknown_returns_null()
    {
        var ms = new SongMaxScores { MaxLeadScore = 100 };
        Assert.Null(ms.GetByInstrument("Unknown_Instrument"));
    }

    [Fact]
    public void SetByInstrument_unknown_does_nothing()
    {
        var ms = new SongMaxScores();
        ms.SetByInstrument("Unknown_Instrument", 999);
        // Should not throw and no field should be set
        Assert.Null(ms.MaxLeadScore);
        Assert.Null(ms.MaxBassScore);
    }
}
