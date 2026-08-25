using System.Text.Json;
using FortniteFestival.Core;
using FortniteFestival.Core.Services;
using FSTService.Api;
using FSTService.Persistence;
using FSTService.Scraping;
using FSTService.Tests.Helpers;
using Microsoft.Extensions.Options;
using Npgsql;

namespace FSTService.Tests.Unit;

/// <summary>
/// Phase A publication-bound path artifact snapshots: schema, bootstrap
/// backfill, candidate capture, binding lifecycle, and scoped effective reads.
/// </summary>
public sealed class PublicationPathArtifactTests : IDisposable
{
    private readonly InMemoryMetaDatabase _fixture = new();

    private MetaDatabase Db => _fixture.Db;
    private NpgsqlDataSource DataSource => _fixture.DataSource;

    public void Dispose() => _fixture.Dispose();

    [Fact]
    public void ContractVersionMatchesRouteSurfaceContract()
        => Assert.Equal(
            PublicationRouteSurfaceContractCatalog.ContractVersion,
            PublicationPathArtifactSchema.ContractVersion);

    [Fact]
    public async Task StartScrapeRun_captures_complete_candidate_snapshot()
    {
        await SeedCatalogAsync("song-a", "song-b", "song-c");
        SetGeneratedPaths("song-a");

        var scrapeId = Db.StartScrapeRun();
        var publicationId =
            Db.GetPublicationGenerationForScrape(scrapeId)!.PublicationId;

        var rows = ReadSnapshot(publicationId);
        Assert.Equal(
            new[] { "song-a", "song-b", "song-c" },
            rows.Keys.Order(StringComparer.Ordinal).ToArray());

        // Authoritative null-generation rows are stored explicitly.
        Assert.Null(rows["song-b"].GenerationId);
        Assert.Null(rows["song-b"].MaxLeadScore);
        Assert.Equal("gen-song-a", rows["song-a"].GenerationId);
        Assert.Equal(1_000, rows["song-a"].MaxLeadScore);

        var binding = ReadPathBinding(publicationId);
        Assert.NotNull(binding);
        Assert.Equal(
            "generation_path_artifact_manifest",
            binding!.BindingKind);
        Assert.Equal(PublicationGenerationStatus.Ready, binding.Status);
        Assert.Equal(3, binding.RowCount);
        Assert.Equal(64, binding.ContentHash!.Length);
        Assert.Equal(
            ComputeManifestHash(publicationId),
            binding.ContentHash);

        using var document = JsonDocument.Parse(binding.BindingJson);
        Assert.Equal(
            "publication_path_artifacts",
            document.RootElement.GetProperty("table").GetString());
        Assert.Equal(
            publicationId,
            document.RootElement.GetProperty("publicationId").GetInt64());
        Assert.Equal(
            scrapeId,
            document.RootElement.GetProperty("scrapeId").GetInt64());
        Assert.True(
            document.RootElement.GetProperty("authoritative").GetBoolean());
        Assert.Equal(
            PublicationPathArtifactSchema.ContractVersion,
            document.RootElement.GetProperty("contractVersion").GetInt32());
        Assert.Equal(
            "generation_candidate_snapshot",
            document.RootElement.GetProperty("source").GetString());
    }

    [Fact]
    public async Task Canonical_manifest_hash_is_deterministic_and_content_sensitive()
    {
        await SeedCatalogAsync("song-a", "song-b");
        SetGeneratedPaths("song-a");
        var scrapeId = Db.StartScrapeRun();
        var publicationId =
            Db.GetPublicationGenerationForScrape(scrapeId)!.PublicationId;

        var first = ComputeManifestHash(publicationId);
        var second = ComputeManifestHash(publicationId);
        Assert.Equal(first, second);

        ExecuteNonQuery(
            """
            UPDATE publication_path_artifacts
            SET max_lead_score = 4321
            WHERE publication_id = @publicationId
              AND song_id = 'song-a'
            """,
            command => command.Parameters.AddWithValue(
                "publicationId",
                publicationId));

        Assert.NotEqual(first, ComputeManifestHash(publicationId));
    }

    [Fact]
    public async Task Bootstrap_backfills_only_the_current_publication_and_is_idempotent()
    {
        await SeedCatalogAsync("song-a", "song-b");
        SetGeneratedPaths("song-a");
        var scrapeId = Db.StartScrapeRun();
        Db.CompleteScrapeRun(scrapeId, 2, 20, 2, 200);
        Db.PublishScrapeRun(scrapeId, promoteCachedResponses: false);
        var pointers = Db.GetPublicationPointerState();
        var publicationId = pointers.CurrentPublicationId!.Value;

        // Simulate a pre-Phase-A database: no snapshot, legacy binding.
        ExecuteNonQuery(
            """
            DELETE FROM publication_path_artifacts
            WHERE publication_id = @publicationId;

            UPDATE publication_surface_bindings
            SET binding_kind = 'legacy_live_unversioned',
                binding_json = jsonb_build_object('table', 'songs'),
                row_count = NULL,
                content_hash = NULL,
                status = 'building',
                built_at = now()
            WHERE publication_id = @publicationId
              AND surface_name = 'path_artifacts'
            """,
            command => command.Parameters.AddWithValue(
                "publicationId",
                publicationId));

        await DatabaseInitializer.EnsureSchemaAsync(DataSource);

        var rows = ReadSnapshot(publicationId);
        Assert.Equal(2, rows.Count);
        Assert.Equal("gen-song-a", rows["song-a"].GenerationId);
        Assert.Null(rows["song-b"].GenerationId);

        var binding = ReadPathBinding(publicationId)!;
        Assert.Equal(
            "generation_path_artifact_manifest",
            binding.BindingKind);
        Assert.Equal(PublicationGenerationStatus.Ready, binding.Status);
        Assert.Equal(2, binding.RowCount);
        using (var document = JsonDocument.Parse(binding.BindingJson))
        {
            Assert.Equal(
                "legacy_live_backfill",
                document.RootElement.GetProperty("source").GetString());
        }

        // A second migration must not duplicate or rewrite the snapshot.
        var capturedBefore = ReadCapturedAt(publicationId, "song-a");
        await DatabaseInitializer.EnsureSchemaAsync(DataSource);
        Assert.Equal(2, ReadSnapshot(publicationId).Count);
        Assert.Equal(capturedBefore, ReadCapturedAt(publicationId, "song-a"));
    }

    [Fact]
    public async Task Incomplete_snapshot_never_reports_a_ready_binding()
    {
        await SeedCatalogAsync("song-a", "song-b");
        var scrapeId = Db.StartScrapeRun();
        var publicationId =
            Db.GetPublicationGenerationForScrape(scrapeId)!.PublicationId;

        ExecuteNonQuery(
            """
            DELETE FROM publication_path_artifacts
            WHERE publication_id = @publicationId
              AND song_id = 'song-b'
            """,
            command => command.Parameters.AddWithValue(
                "publicationId",
                publicationId));

        using (var connection = DataSource.OpenConnection())
        {
            MetaDatabase.BindPublicationPathArtifacts(
                connection,
                null,
                publicationId,
                PublicationPathArtifactSchema.PreparedSnapshotSource,
                DateTime.UtcNow,
                requireReady: false);
        }

        var binding = ReadPathBinding(publicationId)!;
        Assert.Equal("legacy_live_unversioned", binding.BindingKind);
        Assert.Equal(
            PublicationGenerationStatus.Building,
            binding.Status);
        Assert.Null(binding.ContentHash);

        var store = CreateStore(usePublicationArtifacts: true);
        using var scope = store.BeginPublicationRead(publicationId);
        Assert.Throws<PublicationPathArtifactsUnavailableException>(
            () => store.GetPathGenerationStates());
    }

    [Fact]
    public async Task PrepareScrapePublication_preserves_the_generation_path_binding()
    {
        await SeedCatalogAsync("song-a", "song-b");
        SetGeneratedPaths("song-a");
        var scrapeId = Db.StartScrapeRun();
        Db.CompleteScrapeRun(scrapeId, 2, 20, 2, 200);
        Db.PublishScrapeRun(scrapeId, promoteCachedResponses: false);

        var publicationId =
            Db.GetPublicationGenerationForScrape(scrapeId)!.PublicationId;
        var binding = ReadPathBinding(publicationId)!;

        Assert.Equal(
            "generation_path_artifact_manifest",
            binding.BindingKind);
        Assert.Equal(PublicationGenerationStatus.Ready, binding.Status);
        Assert.Equal(2, binding.RowCount);
        using var document = JsonDocument.Parse(binding.BindingJson);
        Assert.Equal(
            "generation_prepared_snapshot",
            document.RootElement.GetProperty("source").GetString());
        Assert.Equal(
            scrapeId,
            document.RootElement.GetProperty("scrapeId").GetInt64());
    }

    [Fact]
    public async Task Snapshot_retention_keeps_current_previous_and_working_only()
    {
        await SeedCatalogAsync("song-a");
        var first = PublishScrape();
        var second = PublishScrape();
        var third = PublishScrape();

        Assert.Empty(ReadSnapshot(first));
        Assert.NotEmpty(ReadSnapshot(second));
        Assert.NotEmpty(ReadSnapshot(third));
        Assert.Equal(
            PublicationGenerationStatus.Retired,
            ReadPathBinding(first)!.Status);
    }

    [Fact]
    public async Task MaxScoreMaintenance_refresh_rebinds_the_current_snapshot()
    {
        await SeedCatalogAsync("song-a", "song-b");
        SetGeneratedPaths("song-a");
        var scrapeId = Db.StartScrapeRun();
        Db.CompleteScrapeRun(scrapeId, 2, 20, 2, 200);
        Db.PublishScrapeRun(scrapeId, promoteCachedResponses: false);
        var publicationId =
            Db.GetPublicationGenerationForScrape(scrapeId)!.PublicationId;
        var before = ReadPathBinding(publicationId)!;

        ExecuteNonQuery(
            """
            UPDATE songs
            SET max_lead_score = 9999,
                path_generation_revision = path_generation_revision + 1
            WHERE song_id = 'song-a'
            """,
            static _ => { });

        using (var connection = DataSource.OpenConnection())
        {
            using (var refresh = connection.CreateCommand())
            {
                refresh.CommandText = PublicationPathArtifactSchema
                    .RefreshSnapshotFromLiveSongsSql;
                refresh.Parameters.AddWithValue(
                    "publicationId",
                    publicationId);
                refresh.ExecuteNonQuery();
            }

            MetaDatabase.BindPublicationPathArtifacts(
                connection,
                null,
                publicationId,
                PublicationPathArtifactSchema.MaxScoreMaintenanceSource,
                DateTime.UtcNow,
                requireReady: true);
        }

        var after = ReadPathBinding(publicationId)!;
        Assert.Equal(9_999, ReadSnapshot(publicationId)["song-a"].MaxLeadScore);
        Assert.NotEqual(before.ContentHash, after.ContentHash);
        Assert.Equal(ComputeManifestHash(publicationId), after.ContentHash);
        Assert.Equal(PublicationGenerationStatus.Ready, after.Status);
        Assert.Equal(2, after.RowCount);
    }

    [Fact]
    public async Task Effective_reads_use_the_current_publication_snapshot()
    {
        await SeedCatalogAsync("song-a", "song-b");
        SetGeneratedPaths("song-a");
        var scrapeId = Db.StartScrapeRun();
        Db.CompleteScrapeRun(scrapeId, 2, 20, 2, 200);
        Db.PublishScrapeRun(scrapeId, promoteCachedResponses: false);

        // Live drift after the publication snapshot was captured.
        ExecuteNonQuery(
            """
            UPDATE songs
            SET max_lead_score = 5555,
                path_generation_revision = path_generation_revision + 1
            WHERE song_id = 'song-a'
            """,
            static _ => { });

        var store = CreateStore(usePublicationArtifacts: true);
        Assert.Equal(
            1_000,
            store.GetAllMaxScores()["song-a"].MaxLeadScore);
        Assert.Equal(
            1_000,
            store.GetPathGenerationState("song-a")!.MaxScores.MaxLeadScore);

        // Live reads are unaffected by the publication scope.
        Assert.Equal(
            5_555,
            store.GetLiveAllMaxScores()["song-a"].MaxLeadScore);
        Assert.Equal(
            5_555,
            store.GetLivePathGenerationState("song-a")!.MaxScores
                .MaxLeadScore);

        // Flag off keeps the legacy live behavior.
        var liveStore = CreateStore(usePublicationArtifacts: false);
        Assert.Equal(
            5_555,
            liveStore.GetAllMaxScores()["song-a"].MaxLeadScore);
    }

    [Fact]
    public async Task Explicit_scope_without_a_manifest_fails_closed()
    {
        await SeedCatalogAsync("song-a");
        var scrapeId = Db.StartScrapeRun();
        Db.CompleteScrapeRun(scrapeId, 1, 10, 1, 100);
        Db.PublishScrapeRun(scrapeId, promoteCachedResponses: false);
        var publicationId =
            Db.GetPublicationGenerationForScrape(scrapeId)!.PublicationId;
        ExecuteNonQuery(
            """
            DELETE FROM publication_path_artifacts
            WHERE publication_id = @publicationId
            """,
            command => command.Parameters.AddWithValue(
                "publicationId",
                publicationId));

        var store = CreateStore(usePublicationArtifacts: true);
        using var scope = store.BeginPublicationRead(publicationId);

        Assert.Throws<PublicationPathArtifactsUnavailableException>(
            () => store.GetAllMaxScores());
        Assert.Throws<PublicationPathArtifactsUnavailableException>(
            () => store.GetPathGenerationState("song-a"));
        Assert.Throws<PublicationPathArtifactsUnavailableException>(
            () => store.GetPathGenerationStates());
    }

    [Fact]
    public async Task Concurrent_publication_scopes_stay_isolated()
    {
        await SeedCatalogAsync("song-a");
        SetGeneratedPaths("song-a");
        var firstScrape = Db.StartScrapeRun();
        Db.CompleteScrapeRun(firstScrape, 1, 10, 1, 100);
        Db.PublishScrapeRun(firstScrape, promoteCachedResponses: false);
        var firstPublicationId =
            Db.GetPublicationGenerationForScrape(firstScrape)!.PublicationId;

        ExecuteNonQuery(
            """
            UPDATE songs
            SET max_lead_score = 2_000,
                path_generation_revision = path_generation_revision + 1
            WHERE song_id = 'song-a'
            """,
            static _ => { });

        var secondScrape = Db.StartScrapeRun();
        Db.CompleteScrapeRun(secondScrape, 1, 10, 1, 100);
        Db.PublishScrapeRun(secondScrape, promoteCachedResponses: false);
        var secondPublicationId =
            Db.GetPublicationGenerationForScrape(secondScrape)!.PublicationId;

        var store = CreateStore(usePublicationArtifacts: true);
        var barrier = new Barrier(2);

        async Task<int?> ReadAsync(long publicationId)
        {
            await Task.Yield();
            using var scope = store.BeginPublicationRead(publicationId);
            barrier.SignalAndWait();
            await Task.Yield();
            return store.GetAllMaxScores()["song-a"].MaxLeadScore;
        }

        var results = await Task.WhenAll(
            Task.Run(() => ReadAsync(firstPublicationId)),
            Task.Run(() => ReadAsync(secondPublicationId)));

        Assert.Equal(1_000, results[0]);
        Assert.Equal(2_000, results[1]);
        // The ambient scope is restored after both reads complete.
        Assert.Null(PathDataStorePublicationScope.CurrentPublicationId);
        Assert.Equal(
            2_000,
            store.GetAllMaxScores()["song-a"].MaxLeadScore);
    }

    [Fact]
    public async Task Bound_publication_songs_json_ignores_live_catalog_drift()
    {
        await SeedCatalogAsync("song-a");
        SetGeneratedPaths("song-a");
        var scrapeId = Db.StartScrapeRun();
        Db.CompleteScrapeRun(scrapeId, 1, 10, 1, 100);
        Db.PublishScrapeRun(scrapeId, promoteCachedResponses: false);
        var publicationId =
            Db.GetPublicationGenerationForScrape(scrapeId)!.PublicationId;

        // Live catalog gains a song and drifts after publication.
        await SeedCatalogAsync("song-a", "song-z");
        ExecuteNonQuery(
            """
            UPDATE songs
            SET max_lead_score = 7777,
                path_generation_revision = path_generation_revision + 1
            WHERE song_id = 'song-a'
            """,
            static _ => { });

        var store = CreateStore(usePublicationArtifacts: true);
        var json = SongsCacheService.BuildBoundPublicationSongsJson(
            publicationId,
            store,
            Db,
            CreateLeaderboardPersistence(),
            CreatePrecomputer(store),
            new JsonSerializerOptions());

        using var document = JsonDocument.Parse(json);
        var songs = document.RootElement.GetProperty("songs")
            .EnumerateArray()
            .ToArray();
        Assert.Equal(
            new[] { "song-a" },
            songs.Select(song => song.GetProperty("songId").GetString())
                .ToArray());
        Assert.Equal(
            1_000,
            songs[0].GetProperty("maxScores")
                .GetProperty("Solo_Guitar")
                .GetInt32());
    }

    [Fact]
    public async Task Path_artifact_resolution_uses_the_bound_publication_generation()
    {
        await SeedCatalogAsync("song-a");
        SetGeneratedPaths("song-a");
        var scrapeId = Db.StartScrapeRun();
        Db.CompleteScrapeRun(scrapeId, 1, 10, 1, 100);
        Db.PublishScrapeRun(scrapeId, promoteCachedResponses: false);
        var publicationId =
            Db.GetPublicationGenerationForScrape(scrapeId)!.PublicationId;

        // A newer live generation must not leak into published resolution.
        ExecuteNonQuery(
            """
            UPDATE songs
            SET path_artifact_generation_id = 'gen-song-a-v2',
                path_generation_revision = path_generation_revision + 1
            WHERE song_id = 'song-a'
            """,
            static _ => { });

        var store = CreateStore(usePublicationArtifacts: true);
        var resolver = new PathArtifactResolver(
            store,
            Options.Create(new ScraperOptions
            {
                DataDirectory = "data",
                UsePublicationPathArtifacts = true,
            }));

        using var scope = store.BeginPublicationRead(publicationId);
        var bound = resolver.Resolve(
            "song-a",
            "Solo_Guitar",
            "expert",
            "png",
            "gen-song-a");
        Assert.NotNull(bound);
        Assert.Equal("gen-song-a", bound!.GenerationId);

        Assert.Null(resolver.Resolve(
            "song-a",
            "Solo_Guitar",
            "expert",
            "png",
            "gen-song-a-v2"));
    }

    [Fact]
    public async Task Candidate_precompute_uses_the_working_publication_snapshot()
    {
        await SeedCatalogAsync("song-a");
        SetGeneratedPaths("song-a");
        var scrapeId = Db.StartScrapeRun();
        var publicationId =
            Db.GetPublicationGenerationForScrape(scrapeId)!.PublicationId;

        ExecuteNonQuery(
            """
            UPDATE songs
            SET max_lead_score = 7777,
                path_artifact_generation_id = 'gen-song-a-v2',
                path_generation_revision = path_generation_revision + 1
            WHERE song_id = 'song-a'
            """,
            static _ => { });

        var store = CreateStore(usePublicationArtifacts: true);
        var precomputer = CreatePrecomputer(
            store,
            FestivalService.CreateFromSongCatalogSnapshot(
                [CreateCatalogSong("song-a")]));
        await precomputer.PrecomputeAllAsync(
            showLeaderboardEntryTotals: false,
            CancellationToken.None,
            publishImmediately: false);

        using var connection = DataSource.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT json_data
            FROM publication_api_response_cache_staging
            WHERE publication_id = @publicationId
              AND cache_key = @cacheKey
            """;
        command.Parameters.AddWithValue(
            "publicationId",
            publicationId);
        command.Parameters.AddWithValue(
            "cacheKey",
            PublicationApiCacheKeys.Songs);
        var payload = Assert.IsType<byte[]>(command.ExecuteScalar());
        using var document = JsonDocument.Parse(payload);
        var song = Assert.Single(
            document.RootElement
                .GetProperty("songs")
                .EnumerateArray());

        Assert.Equal(
            1_000,
            song.GetProperty("maxScores")
                .GetProperty("Solo_Guitar")
                .GetInt32());
        Assert.Equal(
            "gen-song-a",
            song.GetProperty("pathArtifactGenerationId")
                .GetString());
    }

    private PathDataStore CreateStore(bool usePublicationArtifacts) =>        new(
            DataSource,
            null,
            Options.Create(new ScraperOptions
            {
                UsePublicationPathArtifacts = usePublicationArtifacts,
            }));

    private GlobalLeaderboardPersistence CreateLeaderboardPersistence() =>
        new(
            Db,
            Microsoft.Extensions.Logging.Abstractions
                .NullLoggerFactory.Instance,
            Microsoft.Extensions.Logging.Abstractions
                .NullLogger<GlobalLeaderboardPersistence>.Instance,
            DataSource,
            Options.Create(new FeatureOptions()));

    private ScrapeTimePrecomputer CreatePrecomputer(
        IPathDataStore store,
        FestivalService? festivalService = null) =>
        new(
            CreateLeaderboardPersistence(),
            Db,
            store,
            new ScrapeProgressTracker(),
            Microsoft.Extensions.Logging.Abstractions
                .NullLogger<ScrapeTimePrecomputer>.Instance,
            Microsoft.Extensions.Logging.Abstractions
                .NullLoggerFactory.Instance,
            new JsonSerializerOptions(),
            new FeatureOptions(),
            festivalService: festivalService);

    private long PublishScrape()
    {
        var scrapeId = Db.StartScrapeRun();
        Db.CompleteScrapeRun(scrapeId, 1, 10, 1, 100);
        Db.PublishScrapeRun(scrapeId, promoteCachedResponses: false);
        return Db.GetPublicationGenerationForScrape(scrapeId)!.PublicationId;
    }

    private async Task SeedCatalogAsync(params string[] songIds)
    {
        var persistence = new FestivalPersistence(DataSource);
        await persistence.SaveSongsVersionedAsync(
            songIds.Select(CreateCatalogSong).ToArray());
    }

    private void SetGeneratedPaths(string songId)
        => ExecuteNonQuery(
            """
            UPDATE songs
            SET max_lead_score = 1000,
                max_bass_score = 900,
                dat_file_hash = 'dat-hash',
                song_last_modified = '2026-07-31T12:00:00Z',
                paths_generated_at = TIMESTAMPTZ '2026-08-01 00:00:00Z',
                chopt_version = '1.16.4',
                chopt_binary_sha256 =
                    '4c3f9d55c50e8406080191a138580e377413ecc9b2edb60a877281f97018205f',
                path_generation_profile =
                    'chopt-fnf-ew0-s20-json-png-prodrums-v4',
                path_artifact_generation_id = @generationId,
                path_expected_instruments = ARRAY['Solo_Guitar', 'Solo_Bass'],
                path_generation_revision = path_generation_revision + 1
            WHERE song_id = @songId
            """,
            command =>
            {
                command.Parameters.AddWithValue("songId", songId);
                command.Parameters.AddWithValue(
                    "generationId",
                    $"gen-{songId}");
            });

    private void ExecuteNonQuery(
        string sql,
        Action<NpgsqlCommand> configure)
    {
        using var connection = DataSource.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        configure(command);
        command.ExecuteNonQuery();
    }

    private string ComputeManifestHash(long publicationId)
    {
        using var connection = DataSource.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT publication_path_artifact_manifest_sha256(@publicationId)";
        command.Parameters.AddWithValue("publicationId", publicationId);
        return (string)command.ExecuteScalar()!;
    }

    private DateTime ReadCapturedAt(long publicationId, string songId)
    {
        using var connection = DataSource.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT captured_at
            FROM publication_path_artifacts
            WHERE publication_id = @publicationId
              AND song_id = @songId
            """;
        command.Parameters.AddWithValue("publicationId", publicationId);
        command.Parameters.AddWithValue("songId", songId);
        return (DateTime)command.ExecuteScalar()!;
    }

    private Dictionary<string, SnapshotRow> ReadSnapshot(long publicationId)
    {
        var result = new Dictionary<string, SnapshotRow>(
            StringComparer.Ordinal);
        using var connection = DataSource.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT song_id, path_artifact_generation_id, max_lead_score,
                   path_generation_revision
            FROM publication_path_artifacts
            WHERE publication_id = @publicationId
            """;
        command.Parameters.AddWithValue("publicationId", publicationId);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            result[reader.GetString(0)] = new SnapshotRow(
                reader.IsDBNull(1) ? null : reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetInt32(2),
                reader.GetInt64(3));
        }

        return result;
    }

    private PublicationSurfaceBinding? ReadPathBinding(long publicationId)
        => Db.GetPublicationSurfaceBindings(publicationId)
            .SingleOrDefault(static binding =>
                binding.SurfaceName == PublicationSurfaceNames.PathArtifacts);

    private static Song CreateCatalogSong(string songId) =>
        new()
        {
            _title = songId,
            lastModified = new DateTime(
                2026, 7, 31, 12, 0, 0, DateTimeKind.Utc),
            track = new Track
            {
                su = songId,
                tt = songId,
                an = "Artist",
                ab = "Album",
                au = $"https://example.test/{songId}.jpg",
                mu = $"https://example.test/{songId}.dat",
                sig = "4/4",
                ge = ["rock"],
                ry = 2026,
                mt = 120,
                dn = 200,
                @in = new In
                {
                    gr = 1,
                    ba = 2,
                    vl = 3,
                    ds = 4,
                },
            },
        };

    private sealed record SnapshotRow(
        string? GenerationId,
        int? MaxLeadScore,
        long Revision);
}
