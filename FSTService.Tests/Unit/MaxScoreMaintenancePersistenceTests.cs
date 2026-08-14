using FSTService.Persistence;
using FSTService.Scraping;
using FSTService.Tests.Helpers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace FSTService.Tests.Unit;

public sealed class MaxScoreMaintenancePersistenceTests
{
    [Fact]
    public void Notification_classification_quarantines_only_affected_rank_lanes()
    {
        var affected = Candidate("Solo_Guitar");
        var unrelated = Candidate("Solo_Bass");

        var classified =
            MaxScoreMaintenanceNotificationService
                .ClassifyPlayerRankCandidates(
                    [affected, unrelated],
                    ["Solo_Guitar"]);

        Assert.True(classified[0].MaintenanceInduced);
        Assert.False(classified[0].BlocksMaintenance);
        Assert.Equal(
            "max_score_derived_rank_change",
            classified[0].Classification);
        Assert.False(classified[1].MaintenanceInduced);
        Assert.True(classified[1].BlocksMaintenance);
    }

    [Fact]
    public void Notification_classification_quarantines_target_band_candidates()
    {
        var targetSong = Candidate("Solo_Guitar") with
        {
            SubjectType = "band",
            Instrument = null,
            SongId = "target-song",
            Lane = "band_song",
        };
        var bandRank = targetSong with
        {
            SongId = null,
            Lane = "band_rank",
        };
        var unrelatedSong = targetSong with
        {
            SongId = "other-song",
        };

        var classified =
            MaxScoreMaintenanceNotificationService
                .ClassifyMaintenanceCandidates(
                    [targetSong, bandRank, unrelatedSong],
                    ["Solo_Guitar"],
                    new HashSet<string>(
                        ["target-song"],
                        StringComparer.Ordinal));

        Assert.False(classified[0].BlocksMaintenance);
        Assert.False(classified[1].BlocksMaintenance);
        Assert.True(classified[2].BlocksMaintenance);
    }

    [Fact]
    public void Generic_notification_audit_is_quarantined_with_zero_visible_delivery()
    {
        using var dataSource = SharedPostgresContainer.CreateDatabase();
        using var conn = dataSource.OpenConnection();
        using (var run = conn.CreateCommand())
        {
            run.CommandText = """
                INSERT INTO improvement_notification_maintenance_runs (
                    notification_purpose,
                    notification_cause,
                    delivery_state,
                    published_scrape_id,
                    dry_run_digest,
                    canonical_candidate_data,
                    repair_manifest,
                    total_charted_songs,
                    status,
                    candidate_count,
                    allowed_candidate_count,
                    external_routine_candidate_count,
                    rejected_candidate_count,
                    quarantined_candidate_count,
                    visible_delivery_cap,
                    visible_delivery_count)
                VALUES (
                    @purpose,
                    'max_score_recompute',
                    'quarantined',
                    1296,
                    @digest,
                    '{}',
                    '{}'::jsonb,
                    700,
                    'completed',
                    1,
                    1,
                    0,
                    0,
                    1,
                    0,
                    0)
                RETURNING maintenance_run_id
                """;
            run.Parameters.AddWithValue(
                "purpose",
                MaxScoreMaintenanceSchema.Purpose);
            run.Parameters.AddWithValue(
                "digest",
                new string('a', 64));
            var runId = Convert.ToInt64(run.ExecuteScalar());

            using var candidate = conn.CreateCommand();
            candidate.CommandText = """
                INSERT INTO improvement_notification_maintenance_candidates (
                    maintenance_run_id,
                    candidate_key,
                    notification_purpose,
                    notification_cause,
                    delivery_state,
                    subject_type,
                    subject_key,
                    instrument,
                    candidate_kind,
                    metric,
                    classification,
                    allowed,
                    payload)
                VALUES (
                    @runId,
                    @candidateKey,
                    @purpose,
                    'max_score_recompute',
                    'quarantined',
                    'player',
                    'account-1',
                    'Solo_Guitar',
                    'player_max_score_percent_rank_changed',
                    'max_score_percent_rank',
                    'max_score_derived_rank_change',
                    TRUE,
                    '{}'::jsonb)
                """;
            candidate.Parameters.AddWithValue("runId", runId);
            candidate.Parameters.AddWithValue(
                "candidateKey",
                new string('b', 64));
            candidate.Parameters.AddWithValue(
                "purpose",
                MaxScoreMaintenanceSchema.Purpose);
            candidate.ExecuteNonQuery();
        }

        using var verify = conn.CreateCommand();
        verify.CommandText = """
            SELECT
                (
                    SELECT COUNT(*)
                    FROM improvement_notification_maintenance_candidates
                    WHERE notification_purpose = @purpose
                      AND delivery_state = 'quarantined'
                ),
                (
                    SELECT COALESCE(SUM(visible_delivery_count), 0)
                    FROM improvement_notification_maintenance_runs
                    WHERE notification_purpose = @purpose
                ),
                (
                    SELECT COUNT(*)
                    FROM player_improvement_events
                    WHERE notification_purpose = @purpose
                      AND delivery_state = 'visible'
                )
            """;
        verify.Parameters.AddWithValue(
            "purpose",
            MaxScoreMaintenanceSchema.Purpose);
        using var reader = verify.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal(1, reader.GetInt64(0));
        Assert.Equal(0, reader.GetInt64(1));
        Assert.Equal(0, reader.GetInt64(2));
    }

    [Fact]
    public void Failed_checkpoint_resumes_without_changing_digest_or_phase()
    {
        using var dataSource = SharedPostgresContainer.CreateDatabase();
        using var conn = dataSource.OpenConnection();
        var manifestDigest = new string('c', 64);
        var planDigest = new string('d', 64);
        using (var insert = conn.CreateCommand())
        {
            insert.CommandText = """
                INSERT INTO max_score_maintenance_runs (
                    manifest_sha256,
                    manifest_version,
                    plan_digest,
                    expected_published_scrape_id,
                    expected_publication_id,
                    expected_catalog_hash,
                    expected_catalog_song_count,
                    published_score_source_fingerprint,
                    notification_state_fingerprint,
                    rank_history_fingerprint,
                    manifest_json,
                    freeze_reason,
                    phase,
                    status,
                    failure_stage,
                    failure_detail)
                VALUES (
                    @manifestDigest,
                    1,
                    @planDigest,
                    1296,
                    500,
                    @catalogHash,
                    700,
                    @scoreFingerprint,
                    @notificationFingerprint,
                    @historyFingerprint,
                    '{}'::jsonb,
                    @freezeReason,
                    'paths_promoted',
                    'failed',
                    'paths_promoted',
                    'injected crash')
                """;
            insert.Parameters.AddWithValue(
                "manifestDigest",
                manifestDigest);
            insert.Parameters.AddWithValue("planDigest", planDigest);
            insert.Parameters.AddWithValue(
                "catalogHash",
                new string('e', 64));
            insert.Parameters.AddWithValue(
                "scoreFingerprint",
                new string('f', 64));
            insert.Parameters.AddWithValue(
                "notificationFingerprint",
                new string('1', 64));
            insert.Parameters.AddWithValue(
                "historyFingerprint",
                new string('2', 64));
            insert.Parameters.AddWithValue(
                "freezeReason",
                PublicReadFreezeState.MaxScoreMaintenanceReasonPrefix
                + manifestDigest);
            insert.ExecuteNonQuery();
        }

        using (var resume = conn.CreateCommand())
        {
            resume.CommandText = """
                UPDATE max_score_maintenance_runs
                SET status = 'running',
                    failure_stage = NULL,
                    failure_detail = NULL,
                    updated_at = now()
                WHERE manifest_sha256 = @manifestDigest
                  AND plan_digest = @planDigest
                  AND phase = 'paths_promoted'
                  AND status = 'failed'
                """;
            resume.Parameters.AddWithValue(
                "manifestDigest",
                manifestDigest);
            resume.Parameters.AddWithValue("planDigest", planDigest);
            Assert.Equal(1, resume.ExecuteNonQuery());
        }

        using var verify = conn.CreateCommand();
        verify.CommandText = """
            SELECT manifest_sha256, plan_digest, phase, status,
                   failure_stage, failure_detail
            FROM max_score_maintenance_runs
            WHERE manifest_sha256 = @manifestDigest
            """;
        verify.Parameters.AddWithValue(
            "manifestDigest",
            manifestDigest);
        using var reader = verify.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal(manifestDigest, reader.GetString(0));
        Assert.Equal(planDigest, reader.GetString(1));
        Assert.Equal("paths_promoted", reader.GetString(2));
        Assert.Equal("running", reader.GetString(3));
        Assert.True(reader.IsDBNull(4));
        Assert.True(reader.IsDBNull(5));
    }

    [Fact]
    public void Checkpoint_identity_and_rollback_rows_are_immutable()
    {
        using var dataSource = SharedPostgresContainer.CreateDatabase();
        using var conn = dataSource.OpenConnection();
        var manifestDigest = new string('4', 64);
        using (var seed = conn.CreateCommand())
        {
            seed.CommandText = """
                INSERT INTO max_score_maintenance_runs (
                    manifest_sha256,
                    manifest_version,
                    plan_digest,
                    expected_published_scrape_id,
                    expected_publication_id,
                    expected_catalog_hash,
                    expected_catalog_song_count,
                    published_score_source_fingerprint,
                    notification_state_fingerprint,
                    rank_history_fingerprint,
                    manifest_json,
                    freeze_reason,
                    phase,
                    status)
                VALUES (
                    @manifestDigest,
                    1,
                    @planDigest,
                    1296,
                    500,
                    @catalogHash,
                    700,
                    @scoreFingerprint,
                    @notificationFingerprint,
                    @historyFingerprint,
                    '{}'::jsonb,
                    @freezeReason,
                    'rollback_captured',
                    'running');
                INSERT INTO max_score_maintenance_rollback_songs (
                    manifest_sha256,
                    song_id,
                    expected_catalog_last_modified,
                    path_generation_revision,
                    path_expected_instruments,
                    path_generation_pending)
                VALUES (
                    @manifestDigest,
                    'song-a',
                    '2026-08-01T00:00:00.0000000Z',
                    1,
                    ARRAY[]::TEXT[],
                    FALSE);
                """;
            seed.Parameters.AddWithValue(
                "manifestDigest",
                manifestDigest);
            seed.Parameters.AddWithValue(
                "planDigest",
                new string('5', 64));
            seed.Parameters.AddWithValue(
                "catalogHash",
                new string('6', 64));
            seed.Parameters.AddWithValue(
                "scoreFingerprint",
                new string('7', 64));
            seed.Parameters.AddWithValue(
                "notificationFingerprint",
                new string('8', 64));
            seed.Parameters.AddWithValue(
                "historyFingerprint",
                new string('9', 64));
            seed.Parameters.AddWithValue(
                "freezeReason",
                PublicReadFreezeState.MaxScoreMaintenanceReasonPrefix
                + manifestDigest);
            seed.ExecuteNonQuery();
        }

        using var mutateRun = conn.CreateCommand();
        mutateRun.CommandText = """
            UPDATE max_score_maintenance_runs
            SET plan_digest = @replacement
            WHERE manifest_sha256 = @manifestDigest
            """;
        mutateRun.Parameters.AddWithValue(
            "replacement",
            new string('a', 64));
        mutateRun.Parameters.AddWithValue(
            "manifestDigest",
            manifestDigest);
        var runError = Assert.Throws<Npgsql.PostgresException>(
            () => mutateRun.ExecuteNonQuery());
        Assert.Equal("55000", runError.SqlState);

        using var deleteRollback = conn.CreateCommand();
        deleteRollback.CommandText = """
            DELETE FROM max_score_maintenance_rollback_songs
            WHERE manifest_sha256 = @manifestDigest
            """;
        deleteRollback.Parameters.AddWithValue(
            "manifestDigest",
            manifestDigest);
        var rollbackError = Assert.Throws<Npgsql.PostgresException>(
            () => deleteRollback.ExecuteNonQuery());
        Assert.Equal("55000", rollbackError.SqlState);
    }

    [Fact]
    public void Cache_swap_and_owned_unfreeze_commit_together()
    {
        using var dataSource = SharedPostgresContainer.CreateDatabase();
        const long scrapeId = 1296;
        const long publicationId = 500;
        var manifestDigest = new string('a', 64);
        var freezeReason =
            PublicReadFreezeState.MaxScoreMaintenanceReasonPrefix
            + manifestDigest;
        using (var conn = dataSource.OpenConnection())
        using (var seed = conn.CreateCommand())
        {
            seed.CommandText = """
                INSERT INTO scrape_log (
                    id, started_at, completed_at, status)
                VALUES (
                    @scrapeId, now() - interval '1 hour', now(),
                    'completed')
                ON CONFLICT (id) DO UPDATE SET
                    completed_at = EXCLUDED.completed_at,
                    status = EXCLUDED.status;

                INSERT INTO publication_generations (
                    publication_id, scrape_id, status, created_at)
                VALUES (
                    @publicationId, @scrapeId, 'current', now())
                ON CONFLICT (publication_id) DO UPDATE SET
                    scrape_id = EXCLUDED.scrape_id,
                    status = EXCLUDED.status;

                INSERT INTO scrape_publication_state (
                    id,
                    current_publication_id,
                    published_scrape_id,
                    public_reads_frozen,
                    public_reads_frozen_at,
                    public_reads_frozen_scrape_id,
                    public_reads_frozen_reason,
                    updated_at)
                VALUES (
                    TRUE,
                    @publicationId,
                    @scrapeId,
                    TRUE,
                    now(),
                    @scrapeId,
                    @freezeReason,
                    now())
                ON CONFLICT (id) DO UPDATE SET
                    current_publication_id =
                        EXCLUDED.current_publication_id,
                    working_publication_id = NULL,
                    published_scrape_id =
                        EXCLUDED.published_scrape_id,
                    public_reads_frozen = TRUE,
                    public_reads_frozen_at = now(),
                    public_reads_frozen_scrape_id =
                        EXCLUDED.public_reads_frozen_scrape_id,
                    public_reads_frozen_reason =
                        EXCLUDED.public_reads_frozen_reason,
                    updated_at = now();

                INSERT INTO max_score_maintenance_runs (
                    manifest_sha256,
                    manifest_version,
                    plan_digest,
                    expected_published_scrape_id,
                    expected_publication_id,
                    expected_catalog_hash,
                    expected_catalog_song_count,
                    published_score_source_fingerprint,
                    notification_state_fingerprint,
                    rank_history_fingerprint,
                    manifest_json,
                    freeze_reason,
                    phase,
                    status,
                    staged_cache_entry_count)
                VALUES (
                    @manifestDigest,
                    1,
                    @planDigest,
                    @scrapeId,
                    @publicationId,
                    @catalogHash,
                    700,
                    @scoreFingerprint,
                    @notificationFingerprint,
                    @historyFingerprint,
                    '{}'::jsonb,
                    @freezeReason,
                    'validated',
                    'running',
                    1);

                INSERT INTO api_response_cache_staging (
                    cache_key, json_data, etag, cached_at)
                VALUES ('route', decode('7b7d', 'hex'), 'etag', now());
                INSERT INTO publication_api_response_cache_staging (
                    publication_id, cache_key, json_data, etag, cached_at)
                VALUES (
                    @publicationId,
                    'route',
                    decode('7b7d', 'hex'),
                    'etag',
                    now());
                """;
            seed.Parameters.AddWithValue("scrapeId", scrapeId);
            seed.Parameters.AddWithValue(
                "publicationId",
                publicationId);
            seed.Parameters.AddWithValue(
                "manifestDigest",
                manifestDigest);
            seed.Parameters.AddWithValue(
                "planDigest",
                new string('b', 64));
            seed.Parameters.AddWithValue(
                "catalogHash",
                new string('c', 64));
            seed.Parameters.AddWithValue(
                "scoreFingerprint",
                new string('d', 64));
            seed.Parameters.AddWithValue(
                "notificationFingerprint",
                new string('e', 64));
            seed.Parameters.AddWithValue(
                "historyFingerprint",
                new string('f', 64));
            seed.Parameters.AddWithValue(
                "freezeReason",
                freezeReason);
            seed.ExecuteNonQuery();
        }

        using var meta = new MetaDatabase(
            dataSource,
            Microsoft.Extensions.Logging.Abstractions
                .NullLogger<MetaDatabase>.Instance);
        using (meta.AcquireMaxScoreMaintenanceLease(publicationId))
        {
            meta.SwapCachedResponsesFromStagingAndReleaseMaxScoreMaintenance(
                publicationId,
                scrapeId,
                manifestDigest);
        }

        using var verifyConnection = dataSource.OpenConnection();
        using var verify = verifyConnection.CreateCommand();
        verify.CommandText = """
            SELECT
                (
                    SELECT NOT public_reads_frozen
                    FROM scrape_publication_state
                    WHERE id = TRUE
                ),
                (
                    SELECT phase = 'completed'
                       AND status = 'completed'
                    FROM max_score_maintenance_runs
                    WHERE manifest_sha256 = @manifestDigest
                ),
                (
                    SELECT COUNT(*) = 1
                    FROM publication_api_response_cache
                    WHERE publication_id = @publicationId
                      AND cache_key = 'route'
                )
            """;
        verify.Parameters.AddWithValue(
            "manifestDigest",
            manifestDigest);
        verify.Parameters.AddWithValue(
            "publicationId",
            publicationId);
        using var reader = verify.ExecuteReader();
        Assert.True(reader.Read());
        Assert.True(reader.GetBoolean(0));
        Assert.True(reader.GetBoolean(1));
        Assert.True(reader.GetBoolean(2));
    }

    [Fact]
    public async Task Notification_quarantine_preserves_completed_marker_and_zero_visible_delivery()
    {
        using var dataSource = SharedPostgresContainer.CreateDatabase();
        var manifest = CreateManifest();
        var manifestDigest = manifest.ComputeDigest();
        var planDigest = new string('9', 64);
        var freezeReason =
            PublicReadFreezeState.MaxScoreMaintenanceReasonPrefix
            + manifestDigest;
        using (var conn = dataSource.OpenConnection())
        using (var seed = conn.CreateCommand())
        {
            seed.CommandText = """
                INSERT INTO scrape_log (
                    id, started_at, completed_at, status)
                VALUES (
                    @scrapeId, now() - interval '1 hour', now(),
                    'completed');
                INSERT INTO publication_generations (
                    publication_id, scrape_id, status, created_at)
                VALUES (
                    @publicationId, @scrapeId, 'current', now());
                INSERT INTO scrape_publication_state (
                    id,
                    current_publication_id,
                    working_publication_id,
                    published_scrape_id,
                    public_reads_frozen,
                    public_reads_frozen_at,
                    public_reads_frozen_scrape_id,
                    public_reads_frozen_reason,
                    improvement_notifications_scrape_id,
                    improvement_notifications_status,
                    improvement_notifications_projection_ready,
                    improvement_notifications_projection_scrape_id,
                    updated_at)
                VALUES (
                    TRUE,
                    @publicationId,
                    NULL,
                    @scrapeId,
                    TRUE,
                    now(),
                    @scrapeId,
                    @freezeReason,
                    @scrapeId,
                    'completed',
                    TRUE,
                    @scrapeId,
                    now())
                ON CONFLICT (id) DO UPDATE SET
                    current_publication_id =
                        EXCLUDED.current_publication_id,
                    working_publication_id = NULL,
                    published_scrape_id =
                        EXCLUDED.published_scrape_id,
                    public_reads_frozen = TRUE,
                    public_reads_frozen_at = now(),
                    public_reads_frozen_scrape_id =
                        EXCLUDED.public_reads_frozen_scrape_id,
                    public_reads_frozen_reason =
                        EXCLUDED.public_reads_frozen_reason,
                    improvement_notifications_scrape_id =
                        EXCLUDED.improvement_notifications_scrape_id,
                    improvement_notifications_status =
                        EXCLUDED.improvement_notifications_status,
                    improvement_notifications_projection_ready =
                        TRUE,
                    improvement_notifications_projection_scrape_id =
                        EXCLUDED.improvement_notifications_projection_scrape_id,
                    updated_at = now();
                INSERT INTO improvement_detection_runs (
                    published_scrape_id,
                    completed_at,
                    status,
                    mode,
                    baseline_only,
                    include_players,
                    include_bands,
                    include_song_events,
                    include_rankings,
                    notification_purpose,
                    delivery_state)
                VALUES (
                    @scrapeId,
                    now(),
                    'completed',
                    'execute',
                    FALSE,
                    TRUE,
                    TRUE,
                    TRUE,
                    TRUE,
                    'routine_score_observation_v1',
                    'visible');
                INSERT INTO max_score_maintenance_runs (
                    manifest_sha256,
                    manifest_version,
                    plan_digest,
                    expected_published_scrape_id,
                    expected_publication_id,
                    expected_catalog_hash,
                    expected_catalog_song_count,
                    published_score_source_fingerprint,
                    notification_state_fingerprint,
                    rank_history_fingerprint,
                    manifest_json,
                    freeze_reason,
                    phase,
                    status)
                VALUES (
                    @manifestDigest,
                    1,
                    @planDigest,
                    @scrapeId,
                    @publicationId,
                    @catalogHash,
                    @catalogSongCount,
                    @placeholderFingerprint,
                    @placeholderFingerprint,
                    @placeholderFingerprint,
                    @manifestJson,
                    @freezeReason,
                    'derived_state_rebuilt',
                    'running');
                """;
            seed.Parameters.AddWithValue(
                "scrapeId",
                manifest.ExpectedPublishedScrapeId);
            seed.Parameters.AddWithValue(
                "publicationId",
                manifest.ExpectedPublicationId);
            seed.Parameters.AddWithValue(
                "freezeReason",
                freezeReason);
            seed.Parameters.AddWithValue(
                "manifestDigest",
                manifestDigest);
            seed.Parameters.AddWithValue(
                "planDigest",
                planDigest);
            seed.Parameters.AddWithValue(
                "catalogHash",
                manifest.CatalogContentHash);
            seed.Parameters.AddWithValue(
                "catalogSongCount",
                manifest.CatalogSongCount);
            seed.Parameters.AddWithValue(
                "placeholderFingerprint",
                new string('8', 64));
            seed.Parameters.Add(
                "manifestJson",
                NpgsqlTypes.NpgsqlDbType.Jsonb).Value =
                System.Text.Encoding.UTF8.GetString(
                    manifest.SerializeCanonical());
            seed.ExecuteNonQuery();
        }

        var routine = new ImprovementNotificationService(
            dataSource,
            NullLogger<ImprovementNotificationService>.Instance);
        var service = new MaxScoreMaintenanceNotificationService(
            dataSource,
            routine,
            Options.Create(new ImprovementNotificationOptions
            {
                Scope = "registered",
            }),
            NullLogger<
                MaxScoreMaintenanceNotificationService>.Instance);
        var inspection = await service.InspectRoutineStateAsync(
            manifest,
            manifestDigest,
            requireOwnedFreeze: true,
            CancellationToken.None);

        var result = await service.QuarantineAndAlignAsync(
            manifest,
            manifestDigest,
            planDigest,
            inspection.PublishedScoreSourceFingerprint,
            CancellationToken.None);

        Assert.Equal(0, result.CandidateCount);
        Assert.Equal(0, result.VisibleDeliveryCount);
        using var verifyConnection = dataSource.OpenConnection();
        using var verify = verifyConnection.CreateCommand();
        verify.CommandText = """
            SELECT
                (
                    SELECT improvement_notifications_status =
                               'completed'
                       AND improvement_notifications_scrape_id =
                               @scrapeId
                    FROM scrape_publication_state
                    WHERE id = TRUE
                ),
                (
                    SELECT phase = 'notifications_quarantined'
                       AND visible_delivery_count = 0
                    FROM max_score_maintenance_runs
                    WHERE manifest_sha256 = @manifestDigest
                ),
                (
                    SELECT visible_delivery_count = 0
                       AND delivery_state = 'quarantined'
                    FROM improvement_notification_maintenance_runs
                    WHERE maintenance_run_id = @maintenanceRunId
                )
            """;
        verify.Parameters.AddWithValue(
            "scrapeId",
            manifest.ExpectedPublishedScrapeId);
        verify.Parameters.AddWithValue(
            "manifestDigest",
            manifestDigest);
        verify.Parameters.AddWithValue(
            "maintenanceRunId",
            result.MaintenanceRunId);
        using var reader = verify.ExecuteReader();
        Assert.True(reader.Read());
        Assert.True(reader.GetBoolean(0));
        Assert.True(reader.GetBoolean(1));
        Assert.True(reader.GetBoolean(2));
    }

    private static MaxScoreMaintenanceCandidate Candidate(
        string instrument)
        => new(
            SubjectType: "player",
            SubjectKey: "account-1",
            Instrument: instrument,
            SongId: null,
            ScopeKey: null,
            CandidateKind:
                "player_max_score_percent_rank_changed",
            Metric: "max_score_percent_rank",
            OldNumeric: null,
            NewNumeric: null,
            OldRank: 2,
            NewRank: 1,
            Lane: "player_rank",
            Classification: "routine_candidate",
            MaintenanceInduced: false,
            BlocksMaintenance: true);

    private static MaxScoreMaintenanceManifest CreateManifest()
    {
        var runtime = new PathGenerationRuntimeIdentity(
            "1.16.3",
            new string('a', 64),
            "profile-v3");
        var current = new MaxScoreMaintenancePathIdentity(
            Revision: 0,
            DatFileHash: null,
            SongLastModified: null,
            GeneratedAtUtc: null,
            ChoptVersion: null,
            ChoptBinarySha256: null,
            GenerationProfile: null,
            ArtifactGenerationId: null,
            ExpectedInstruments: [],
            Maxima: new MaxScoreMaintenanceMaxima(
                null, null, null, null, null, null, null, null),
            PathGenerationPending: false);
        var staged = new MaxScoreMaintenancePathIdentity(
            Revision: 0,
            DatFileHash: new string('b', 64),
            SongLastModified: "2026-08-01T00:00:00Z",
            GeneratedAtUtc: new DateTime(
                2026,
                8,
                13,
                0,
                0,
                0,
                DateTimeKind.Utc),
            ChoptVersion: runtime.Version,
            ChoptBinarySha256: runtime.BinarySha256,
            GenerationProfile: runtime.Profile,
            ArtifactGenerationId: "generation-a",
            ExpectedInstruments: ["Solo_Guitar"],
            Maxima: current.Maxima with { Lead = 51_573 },
            PathGenerationPending: false);
        return new MaxScoreMaintenanceManifest(
            MaxScoreMaintenanceManifest.CurrentManifestVersion,
            ExpectedPublishedScrapeId: 1296,
            ExpectedPublicationId: 500,
            CatalogVersion: 1,
            CatalogSchemaVersion:
                SongCatalogSnapshotBuilder.SchemaVersion,
            CatalogContentHash: new string('c', 64),
            CatalogSongCount: 1,
            CatalogSourceCapturedAtUtc: new DateTime(
                2026,
                8,
                13,
                0,
                0,
                0,
                DateTimeKind.Utc),
            CreatedAtUtc: new DateTime(
                2026,
                8,
                13,
                0,
                0,
                0,
                DateTimeKind.Utc),
            Runtime: runtime,
            Songs:
            [
                new MaxScoreMaintenanceManifestSong(
                    "song-a",
                    "2026-08-01T00:00:00Z",
                    current,
                    staged,
                    ["Solo_Guitar"]),
            ])
            .ValidateAndNormalize();
    }
}
