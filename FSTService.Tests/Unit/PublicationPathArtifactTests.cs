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

        // Simulate a pre-Phase-A database: no snapshot or current binding.
        ExecuteNonQuery(
            """
            DELETE FROM publication_path_artifacts
            WHERE publication_id = @publicationId;

            DELETE FROM publication_surface_bindings
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
        var bindingBefore = ReadFullPathBinding(publicationId);
        await DatabaseInitializer.EnsureSchemaAsync(DataSource);
        Assert.Equal(2, ReadSnapshot(publicationId).Count);
        Assert.Equal(capturedBefore, ReadCapturedAt(publicationId, "song-a"));
        Assert.Equal(bindingBefore, ReadFullPathBinding(publicationId));
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

    [Theory]
    [InlineData(PublicationPathArtifactSchema.PreparedSnapshotSource)]
    [InlineData(PublicationPathArtifactSchema.ScrapePassStagingSource)]
    [InlineData(PublicationPathArtifactSchema.LegacyLiveBackfillSource)]
    public async Task Full_schema_preserves_current_binding_provenance_and_built_at(
        string source)
    {
        await SeedCatalogAsync("song-a", "song-b");
        SetGeneratedPaths("song-a");
        var publicationId = PublishScrape();
        ExecuteNonQuery(
            """
            UPDATE publication_surface_bindings
            SET binding_json =
                    jsonb_set(binding_json, '{source}', to_jsonb(@source::text))
                    || '{"operatorEvidence":"preserve-exactly"}'::jsonb,
                built_at = TIMESTAMPTZ '2026-08-02 03:04:05.123456Z'
            WHERE publication_id = @publicationId
              AND surface_name = 'path_artifacts'
            """,
            command =>
            {
                command.Parameters.AddWithValue("source", source);
                command.Parameters.AddWithValue("publicationId", publicationId);
            });
        var before = ReadFullPathBinding(publicationId);
        var capturedAt = ReadCapturedAt(publicationId, "song-a");

        for (var attempt = 0; attempt < 2; attempt++)
        {
            await DatabaseInitializer.EnsureSchemaAsync(DataSource);

            Assert.Equal(before, ReadFullPathBinding(publicationId));
            Assert.Equal(capturedAt, ReadCapturedAt(publicationId, "song-a"));
            Assert.Equal(ComputeManifestHash(publicationId),
                ReadPathBinding(publicationId)!.ContentHash);
        }
    }

    [Theory]
    [InlineData("3", "manifest_version_future")]
    [InlineData("2147483648", "manifest_version_future")]
    [InlineData("-1", "manifest_version_invalid")]
    [InlineData("0", "manifest_version_invalid")]
    [InlineData("1.5", "manifest_version_invalid")]
    [InlineData("\"unknown\"", "manifest_version_invalid")]
    [InlineData("\"2\"", "manifest_version_invalid")]
    [InlineData("null", "manifest_version_invalid")]
    public async Task Full_schema_refuses_future_or_invalid_versions_without_rewriting(
        string version, string code)
    {
        await SeedCatalogAsync("song-a");
        SetGeneratedPaths("song-a");
        var publicationId = PublishScrape();
        ExecuteNonQuery(
            """
            UPDATE publication_surface_bindings
            SET binding_json = jsonb_set(binding_json, '{manifestVersion}', @version::jsonb)
            WHERE publication_id = @publicationId AND surface_name = 'path_artifacts'
            """,
            command =>
            {
                command.Parameters.AddWithValue("version", version);
                command.Parameters.AddWithValue("publicationId", publicationId);
            });
        var before = ReadFullPathBinding(publicationId);

        var failure = await Assert.ThrowsAsync<PublicationPathArtifactInitializationException>(
            () => DatabaseInitializer.EnsureSchemaAsync(DataSource));

        Assert.Equal(before, ReadFullPathBinding(publicationId));
        Assert.Contains(failure.Failures, item => item.PublicationId == publicationId && item.Code == code);
        Assert.False((await PublicationPathArtifactReleaseGate.ReadAsync(DataSource)).IsReleased);
    }

    [Theory]
    [InlineData("kind", "binding_kind_invalid")]
    [InlineData("table", "binding_json_invalid")]
    [InlineData("authoritative", "binding_json_invalid")]
    [InlineData("source", "binding_json_invalid")]
    [InlineData("json-array", "manifest_version_invalid")]
    [InlineData("publication", "binding_publication_mismatch")]
    [InlineData("publication-type", "binding_publication_mismatch")]
    [InlineData("scrape", "binding_scrape_mismatch")]
    [InlineData("contract", "binding_contract_invalid")]
    [InlineData("contract-type", "binding_contract_invalid")]
    [InlineData("expected-count", "binding_expected_count_invalid")]
    [InlineData("catalog", "binding_expected_count_invalid")]
    [InlineData("actual-count", "binding_row_count_mismatch")]
    [InlineData("binding-count", "binding_row_count_mismatch")]
    [InlineData("hash", "binding_content_hash_mismatch")]
    public async Task Ready_binding_contract_failures_are_visible_and_source_preserving(string defect, string code)
    {
        await SeedCatalogAsync("song-a", "song-b");
        SetGeneratedPaths("song-a");
        var publicationId = PublishScrape();
        var patch = defect switch
        {
            "table" => """{"table":"wrong-source"}""",
            "authoritative" => """{"authoritative":false}""",
            "source" => """{"source":""}""",
            "publication" => """{"publicationId":999999}""",
            "publication-type" => """{"publicationId":"not-a-number"}""",
            "scrape" => """{"scrapeId":999999}""",
            "contract" => """{"contractVersion":99}""",
            "contract-type" => """{"contractVersion":"1"}""",
            "expected-count" => """{"expectedRowCount":999999}""",
            _ => "{}",
        };
        ExecuteNonQuery(
            """
            UPDATE publication_surface_bindings
            SET binding_json = CASE WHEN @defect='json-array' THEN '[]'::jsonb
                                    ELSE binding_json || @patch::jsonb END,
                binding_kind = CASE WHEN @defect='kind' THEN 'wrong-kind' ELSE binding_kind END,
                row_count = CASE WHEN @defect='binding-count' THEN row_count+1 ELSE row_count END,
                content_hash = CASE WHEN @defect='hash' THEN repeat('f',64) ELSE content_hash END
            WHERE publication_id=@publicationId AND surface_name='path_artifacts';
            DELETE FROM publication_path_artifacts
            WHERE publication_id=@publicationId AND song_id='song-b' AND @defect='actual-count';
            UPDATE publication_song_catalog SET is_exact=FALSE
            WHERE publication_id=@publicationId AND @defect='catalog';
            """,
            command =>
            {
                command.Parameters.AddWithValue("publicationId", publicationId);
                command.Parameters.AddWithValue("defect", defect);
                command.Parameters.AddWithValue("patch", patch);
            });
        var before = ReadFullPathBinding(publicationId);

        var failure = await Assert.ThrowsAsync<PublicationPathArtifactInitializationException>(
            () => DatabaseInitializer.EnsureSchemaAsync(DataSource));

        Assert.Equal(before, ReadFullPathBinding(publicationId));
        Assert.Contains(failure.Failures, item => item.PublicationId == publicationId && item.Code == code);
        var release = await PublicationPathArtifactReleaseGate.ReadAsync(DataSource);
        Assert.False(release.IsReleased);
        Assert.Equal(code, release.FailureCode);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Noncurrent_active_pointer_failures_cannot_be_silent(bool working)
    {
        await SeedCatalogAsync("song-a");
        SetGeneratedPaths("song-a");
        var first = PublishScrape();
        long target;
        if (working)
        {
            var scrape = Db.StartScrapeRun();
            target = Db.GetPublicationGenerationForScrape(scrape)!.PublicationId;
        }
        else
        {
            PublishScrape();
            target = first;
        }
        ExecuteNonQuery(
            """
            UPDATE publication_surface_bindings
            SET binding_json=jsonb_set(binding_json,'{manifestVersion}','3'::jsonb),
                status='building'
            WHERE publication_id=@target AND surface_name='path_artifacts'
            """, command => command.Parameters.AddWithValue("target", target));
        var before = ReadFullPathBinding(target);

        if (working)
        {
            var failure = await Assert.ThrowsAsync<PublicationPathArtifactInitializationException>(
                () => DatabaseInitializer.EnsureSchemaAsync(DataSource));
            Assert.Contains(failure.Failures, item => item.PublicationId == target && item.Code == "manifest_version_future");
        }
        else
        {
            var warnings = new List<PublicationPathArtifactInitializationFailure>();
            await DatabaseInitializer.EnsureSchemaAsync(DataSource, reportWarning: warnings.Add);
            Assert.Contains(warnings, item => item.PublicationId == target && item.Code == "manifest_version_future");
            Assert.True((await PublicationPathArtifactReleaseGate.ReadAsync(DataSource)).IsReleased);
        }
        Assert.Equal(before, ReadFullPathBinding(target));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"manifestVersion\":1}")]
    public async Task Full_schema_upgrades_only_legacy_bindings_through_the_upgrade_path(string legacy)
    {
        await SeedCatalogAsync("song-a");
        SetGeneratedPaths("song-a");
        var publicationId = PublishScrape();
        ExecuteNonQuery(
            """
            UPDATE publication_surface_bindings
            SET binding_json = @legacy::jsonb, content_hash = repeat('a',64),
                built_at = TIMESTAMPTZ '2026-08-02 03:04:05Z'
            WHERE publication_id = @publicationId AND surface_name = 'path_artifacts'
            """,
            command =>
            {
                command.Parameters.AddWithValue("legacy", legacy);
                command.Parameters.AddWithValue("publicationId", publicationId);
            });
        var capturedAt = ReadCapturedAt(publicationId, "song-a");

        await DatabaseInitializer.EnsureSchemaAsync(DataSource);

        var binding = ReadPathBinding(publicationId)!;
        using var json = JsonDocument.Parse(binding.BindingJson);
        Assert.Equal(PublicationPathArtifactSchema.SchemaUpgradeSource,
            json.RootElement.GetProperty("source").GetString());
        Assert.Equal(PublicationPathArtifactSchema.ManifestVersion,
            json.RootElement.GetProperty("manifestVersion").GetInt32());
        Assert.Equal(ComputeManifestHash(publicationId), binding.ContentHash);
        Assert.Equal(capturedAt, ReadCapturedAt(publicationId, "song-a"));
        var upgraded = ReadFullPathBinding(publicationId);
        await DatabaseInitializer.EnsureSchemaAsync(DataSource);
        Assert.Equal(upgraded, ReadFullPathBinding(publicationId));
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
    public async Task Version_upgrade_does_not_invent_a_missing_previous_binding()
    {
        await SeedCatalogAsync("song-a");
        SetGeneratedPaths("song-a");
        var previous = PublishScrape();
        var current = PublishScrape();
        ExecuteNonQuery(
            """
            DELETE FROM publication_surface_bindings
            WHERE publication_id=@previous AND surface_name='path_artifacts'
            """,
            command => command.Parameters.AddWithValue("previous", previous));
        var currentBefore = ReadFullPathBinding(current);

        await DatabaseInitializer.EnsureSchemaAsync(DataSource);

        Assert.Null(ReadPathBinding(previous));
        Assert.Equal(currentBefore, ReadFullPathBinding(current));
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
            publishImmediately: false,
            publicationCatalogSongs:
                [CreateCatalogSong("song-a")]);

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

    private (string Json, string Sha256) ReadFullPathBinding(long publicationId)
    {
        using var connection = DataSource.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT to_jsonb(binding)::text,
                encode(digest(to_jsonb(binding)::text, 'sha256'), 'hex')
            FROM publication_surface_bindings binding
            WHERE publication_id = @publicationId
              AND surface_name = 'path_artifacts'
            """;
        command.Parameters.AddWithValue("publicationId", publicationId);
        using var reader = command.ExecuteReader();
        Assert.True(reader.Read());
        return (reader.GetString(0), reader.GetString(1));
    }

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
