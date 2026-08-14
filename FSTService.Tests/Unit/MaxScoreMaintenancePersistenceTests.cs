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
    public void Routine_player_rank_count_excludes_alignment_only_percent_changes()
    {
        var routine = Candidate("Solo_Guitar") with
        {
            CandidateKind =
                "player_total_score_rank_improved",
            Metric = "total_score_rank",
        };
        var percentOnly = Candidate("Solo_Guitar");

        Assert.Equal(
            1,
            MaxScoreMaintenanceNotificationService
                .CountRoutinePlayerRankEvents(
                    [routine, percentOnly]));
        Assert.Equal(
            0,
            MaxScoreMaintenanceNotificationService
                .CountRoutinePlayerRankEvents(
                    [percentOnly]));
    }

    [Fact]
    public void Routine_band_song_count_coalesces_multi_metric_same_play_rows()
    {
        var score = Candidate("Solo_Guitar") with
        {
            SubjectType = "band",
            SubjectKey = "Band_Duets:account-1:account-2",
            Instrument = null,
            SongId = "song-a",
            ScopeKey = "overall:",
            CandidateKind = "band_score_pb",
            Metric = "score",
            Lane = "band_song",
            RoutineEventGroupKey = "201000\u001f0:1\u001fSolo_Guitar+Solo_Bass",
        };
        var rank = score with
        {
            ScopeKey = "combo:Solo_Guitar+Solo_Bass",
            CandidateKind = "band_song_rank_improved",
            Metric = "song_rank",
        };
        var stars = score with
        {
            CandidateKind = "band_stars_improved",
            Metric = "stars",
        };
        var otherPlay = score with
        {
            RoutineEventGroupKey =
                "202000\u001f0:1\u001fSolo_Guitar+Solo_Bass",
        };

        Assert.Equal(
            2,
            MaxScoreMaintenanceNotificationService
                .CountRoutineBandSongEvents(
                    [score, rank, stars, otherPlay]));
    }

    [Fact]
    public void Routine_band_rank_count_groups_rank_metrics_and_excludes_missing_state()
    {
        var weightedRank = Candidate("Solo_Guitar") with
        {
            SubjectType = "band",
            SubjectKey = "Band_Duets:account-1:account-2",
            Instrument = null,
            ScopeKey = "combo:Solo_Guitar+Solo_Bass",
            CandidateKind = "band_weighted_rank_improved",
            Metric = "weighted_rank",
            Lane = "band_rank",
            RoutineEventGroupKey =
                "combo\u001fSolo_Guitar+Solo_Bass",
        };
        var totalScoreRank = weightedRank with
        {
            CandidateKind = "band_total_score_rank_improved",
            Metric = "total_score_rank",
        };
        var fcRateRank = weightedRank with
        {
            CandidateKind = "band_fc_rate_rank_improved",
            Metric = "fc_rate_rank",
        };
        var totalScore = weightedRank with
        {
            CandidateKind = "band_total_score_improved",
            Metric = "total_score",
        };
        var fullCombos = weightedRank with
        {
            CandidateKind = "band_fc_count_improved",
            Metric = "full_combo_count",
        };
        var missing = weightedRank with
        {
            ScopeKey = "overall:",
            CandidateKind = "band_rank_state_missing",
            Metric = "state",
            RoutineEventGroupKey = "overall\u001f",
        };

        Assert.Equal(
            3,
            MaxScoreMaintenanceNotificationService
                .CountRoutineBandRankEvents(
                    [
                        weightedRank,
                        totalScoreRank,
                        fcRateRank,
                        totalScore,
                        fullCombos,
                        missing,
                    ]));
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
    public async Task Rollback_file_first_checkpoint_crash_reuses_persisted_run_timestamp()
    {
        using var dataSource = SharedPostgresContainer.CreateDatabase();
        var manifest = CreateManifest();
        var manifestDigest = manifest.ComputeDigest();
        var planDigest = new string('3', 64);
        var runCreatedAt = new DateTime(
            2026,
            8,
            14,
            8,
            30,
            0,
            DateTimeKind.Utc);
        using (var conn = dataSource.OpenConnection())
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
                    created_at,
                    updated_at)
                VALUES (
                    @manifestDigest,
                    @manifestVersion,
                    @planDigest,
                    @scrapeId,
                    @publicationId,
                    @catalogHash,
                    @catalogSongCount,
                    @fingerprint,
                    @fingerprint,
                    @fingerprint,
                    @manifestJson,
                    @freezeReason,
                    'freeze_established',
                    'failed',
                    @createdAt,
                    @createdAt)
                """;
            insert.Parameters.AddWithValue(
                "manifestDigest",
                manifestDigest);
            insert.Parameters.AddWithValue(
                "manifestVersion",
                manifest.ManifestVersion);
            insert.Parameters.AddWithValue(
                "planDigest",
                planDigest);
            insert.Parameters.AddWithValue(
                "scrapeId",
                manifest.ExpectedPublishedScrapeId);
            insert.Parameters.AddWithValue(
                "publicationId",
                manifest.ExpectedPublicationId);
            insert.Parameters.AddWithValue(
                "catalogHash",
                manifest.CatalogContentHash);
            insert.Parameters.AddWithValue(
                "catalogSongCount",
                manifest.CatalogSongCount);
            insert.Parameters.AddWithValue(
                "fingerprint",
                new string('4', 64));
            insert.Parameters.Add(
                "manifestJson",
                NpgsqlTypes.NpgsqlDbType.Jsonb).Value =
                System.Text.Encoding.UTF8.GetString(
                    manifest.SerializeCanonical());
            insert.Parameters.AddWithValue(
                "freezeReason",
                PublicReadFreezeState.MaxScoreMaintenanceReasonPrefix
                + manifestDigest);
            insert.Parameters.AddWithValue(
                "createdAt",
                runCreatedAt);
            insert.ExecuteNonQuery();
        }

        var dataDirectory = Path.Combine(
            Directory.GetCurrentDirectory(),
            ".test-temp",
            $"max-score-rollback-resume-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dataDirectory);
        try
        {
            var firstTimestamp = ReadCreatedAt();
            var firstSnapshot =
                MaxScoreMaintenanceService.CreateRollbackSnapshot(
                    manifest,
                    manifestDigest,
                    planDigest,
                    firstTimestamp);
            var firstWrite = await MaxScoreMaintenanceFileStore
                .WriteCanonicalRollbackSnapshotAsync(
                    dataDirectory,
                    "rollback.json",
                    firstSnapshot,
                    CancellationToken.None);

            var resumedTimestamp = ReadCreatedAt();
            var resumedSnapshot =
                MaxScoreMaintenanceService.CreateRollbackSnapshot(
                    manifest,
                    manifestDigest,
                    planDigest,
                    resumedTimestamp);
            var resumedWrite = await MaxScoreMaintenanceFileStore
                .WriteCanonicalRollbackSnapshotAsync(
                    dataDirectory,
                    "rollback.json",
                    resumedSnapshot,
                    CancellationToken.None);

            Assert.Equal(firstWrite.Sha256, resumedWrite.Sha256);
            Assert.Equal(
                firstSnapshot.CreatedAtUtc,
                resumedSnapshot.CreatedAtUtc);
            using var verifyConnection =
                dataSource.OpenConnection();
            using var verify = verifyConnection.CreateCommand();
            verify.CommandText = """
                SELECT phase, status
                FROM max_score_maintenance_runs
                WHERE manifest_sha256 = @manifestDigest
                """;
            verify.Parameters.AddWithValue(
                "manifestDigest",
                manifestDigest);
            using var reader = verify.ExecuteReader();
            Assert.True(reader.Read());
            Assert.Equal(
                "freeze_established",
                reader.GetString(0));
            Assert.Equal("failed", reader.GetString(1));
        }
        finally
        {
            if (Directory.Exists(dataDirectory))
                Directory.Delete(dataDirectory, recursive: true);
        }

        DateTime ReadCreatedAt()
        {
            using var conn = dataSource.OpenConnection();
            using var command = conn.CreateCommand();
            command.CommandText = """
                SELECT created_at
                FROM max_score_maintenance_runs
                WHERE manifest_sha256 = @manifestDigest
                """;
            command.Parameters.AddWithValue(
                "manifestDigest",
                manifestDigest);
            return Convert.ToDateTime(command.ExecuteScalar())
                .ToUniversalTime();
        }
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
    public async Task Cache_swap_and_owned_unfreeze_commit_before_registration_gate_release()
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
        Task queuedRegistration;
        await using (var maintenanceLease =
                     await meta
                         .AcquireMaxScoreMaintenanceLeaseAsync(
                             publicationId))
        {
            using var ambientLease =
                maintenanceLease.EnterAmbientScope();
            await maintenanceLease
                .AcquireSourceLocksAsync();
            queuedRegistration = Task.Run(async () =>
            {
                await using var registrationLease =
                    await meta
                        .AcquireRegistrationMutationLeaseAsync();
                meta.RegisterUser(
                    "post-cache-device",
                    "post-cache-account");
            });
            await Task.Delay(150);
            Assert.False(queuedRegistration.IsCompleted);

            meta.SwapCachedResponsesFromStagingAndReleaseMaxScoreMaintenance(
                publicationId,
                scrapeId,
                manifestDigest);
            Assert.False(queuedRegistration.IsCompleted);
            Assert.False(
                meta.IsAccountRegistered(
                    "post-cache-account"));
        }
        await queuedRegistration.WaitAsync(
            TimeSpan.FromSeconds(5));

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
        Assert.True(
            meta.IsAccountRegistered(
                "post-cache-account"));
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

    [Fact]
    public async Task Rank_change_only_quarantine_aligns_state_and_resume_inspection_is_clear()
    {
        using var dataSource = SharedPostgresContainer.CreateDatabase();
        var manifest = CreateManifest();
        var manifestDigest = manifest.ComputeDigest();
        var planDigest = new string('5', 64);
        using (var conn = dataSource.OpenConnection())
        using (var seed = conn.CreateCommand())
        {
            seed.CommandText = """
                INSERT INTO registered_users (
                    device_id,
                    account_id,
                    registered_at,
                    last_activity_at)
                VALUES (
                    'web-tracker',
                    'account-1',
                    now() - interval '1 day',
                    now());
                INSERT INTO account_rankings (
                    account_id,
                    instrument,
                    songs_played,
                    total_charted_songs,
                    coverage,
                    raw_skill_rating,
                    adjusted_skill_rating,
                    adjusted_skill_rank,
                    weighted_rating,
                    weighted_rank,
                    fc_rate,
                    fc_rate_rank,
                    total_score,
                    total_score_rank,
                    max_score_percent,
                    max_score_percent_rank,
                    avg_accuracy,
                    full_combo_count,
                    avg_stars,
                    best_rank,
                    avg_rank,
                    computed_at)
                VALUES (
                    'account-1',
                    'Solo_Guitar',
                    1,
                    1,
                    1,
                    100,
                    100,
                    1,
                    100,
                    1,
                    1,
                    1,
                    1000,
                    1,
                    100,
                    1,
                    100,
                    1,
                    6,
                    1,
                    1,
                    now());
                INSERT INTO player_rank_improvement_state (
                    account_id,
                    instrument,
                    adjusted_skill_rank,
                    weighted_rank,
                    fc_rate_rank,
                    total_score_rank,
                    max_score_percent_rank,
                    total_score,
                    full_combo_count,
                    computed_at,
                    observed_at,
                    updated_at)
                VALUES (
                    'account-1',
                    'Solo_Guitar',
                    1,
                    1,
                    1,
                    1,
                    2,
                    1000,
                    1,
                    now(),
                    now(),
                    now())
                """;
            seed.ExecuteNonQuery();
        }
        SeedNotificationMaintenanceState(
            dataSource,
            manifest,
            manifestDigest,
            planDigest);

        var (routine, service) =
            CreateNotificationMaintenanceServices(
                dataSource);
        var inspection = await service.InspectRoutineStateAsync(
            manifest,
            manifestDigest,
            requireOwnedFreeze: true,
            CancellationToken.None);
        var candidate = Assert.Single(inspection.Candidates);
        Assert.Equal(
            "player_max_score_percent_rank_changed",
            candidate.CandidateKind);
        Assert.Equal(0, routine.Precompute(
            CreatePlayerRankDryRunOptions(
                manifest.ExpectedPublishedScrapeId))
            .PlayerRankEventsInserted);

        using (var conn = dataSource.OpenConnection())
        using (var fail = conn.CreateCommand())
        {
            fail.CommandText = """
                UPDATE max_score_maintenance_runs
                SET status = 'failed',
                    failure_stage = 'derived_state_rebuilt',
                    failure_detail = 'injected resume boundary'
                WHERE manifest_sha256 = @manifestDigest
                """;
            fail.Parameters.AddWithValue(
                "manifestDigest",
                manifestDigest);
            Assert.Equal(1, fail.ExecuteNonQuery());
        }
        var resumeInspection =
            await service.InspectRoutineStateAsync(
                manifest,
                manifestDigest,
                requireOwnedFreeze: true,
                CancellationToken.None);
        Assert.Single(resumeInspection.Candidates);

        var result = await service.QuarantineAndAlignAsync(
            manifest,
            manifestDigest,
            planDigest,
            inspection.PublishedScoreSourceFingerprint,
            CancellationToken.None);

        Assert.Equal(1, result.CandidateCount);
        Assert.Equal(0, result.VisibleDeliveryCount);
        var aligned =
            await service.InspectRoutineStateAsync(
                manifest,
                manifestDigest,
                requireOwnedFreeze: true,
                CancellationToken.None);
        Assert.Empty(aligned.Candidates);
    }

    [Fact]
    public async Task Previously_over_threshold_band_with_missing_subject_is_baselined_and_never_emits_first_score()
    {
        using var dataSource = SharedPostgresContainer.CreateDatabase();
        var manifest = CreateManifest();
        var manifestDigest = manifest.ComputeDigest();
        var planDigest = new string('6', 64);
        using (var conn = dataSource.OpenConnection())
        using (var seed = conn.CreateCommand())
        {
            seed.CommandText = """
                INSERT INTO registered_bands (
                    source_id,
                    band_type,
                    team_key,
                    band_id,
                    registered_at,
                    last_activity_at,
                    last_member_sync_at)
                VALUES (
                    'web-band-tracker',
                    'Band_Duets',
                    'account-1:account-2',
                    'band-1',
                    now() - interval '1 day',
                    now(),
                    now());
                INSERT INTO current_band_leaderboard_entries (
                    song_id,
                    band_type,
                    ranking_scope,
                    scope_combo_id,
                    team_key,
                    entry_combo_id,
                    entry_instrument_combo,
                    team_members,
                    score,
                    accuracy,
                    is_full_combo,
                    stars,
                    difficulty,
                    season,
                    rank,
                    total_entries,
                    percentile,
                    first_seen_at,
                    last_updated_at,
                    computed_at,
                    projection_generation)
                VALUES (
                    'song-a',
                    'Band_Duets',
                    'overall',
                    '',
                    'account-1:account-2',
                    '0:1',
                    'Solo_Guitar+Solo_Bass',
                    ARRAY['account-1', 'account-2'],
                    1000,
                    100,
                    TRUE,
                    6,
                    3,
                    14,
                    1,
                    500,
                    1,
                    now() - interval '1 day',
                    now(),
                    now(),
                    7);
                INSERT INTO band_current_projection_scope (
                    song_id,
                    band_type,
                    ranking_scope,
                    scope_combo_id,
                    projection_generation,
                    published_generation,
                    row_count,
                    published_row_count,
                    status,
                    last_rebuilt_at,
                    updated_at)
                VALUES (
                    'song-a',
                    'Band_Duets',
                    'overall',
                    '',
                    7,
                    7,
                    1,
                    1,
                    'ready',
                    now(),
                    now())
                """;
            seed.ExecuteNonQuery();
        }
        SeedNotificationMaintenanceState(
            dataSource,
            manifest,
            manifestDigest,
            planDigest);

        var (routine, service) =
            CreateNotificationMaintenanceServices(
                dataSource);
        var inspection = await service.InspectRoutineStateAsync(
            manifest,
            manifestDigest,
            requireOwnedFreeze: true,
            CancellationToken.None);
        Assert.Empty(inspection.Candidates);

        var result = await service.QuarantineAndAlignAsync(
            manifest,
            manifestDigest,
            planDigest,
            inspection.PublishedScoreSourceFingerprint,
            CancellationToken.None);
        Assert.Equal(0, result.CandidateCount);

        var nextRoutine = routine.Precompute(
            new ImprovementNotificationPrecomputeOptions(
                Execute: true,
                BaselineOnly: false,
                Scope: "registered",
                IncludePlayers: false,
                IncludeBands: true,
                IncludeSongEvents: true,
                IncludeRankings: false,
                PruneExpired: false,
                PublishedScrapeId:
                    manifest.ExpectedPublishedScrapeId));
        Assert.Equal(0, nextRoutine.BandSongEventsInserted);
        using var verifyConnection = dataSource.OpenConnection();
        using var verify = verifyConnection.CreateCommand();
        verify.CommandText = """
            SELECT
                (
                    SELECT COUNT(*)
                    FROM band_improvement_subjects
                    WHERE band_type = 'Band_Duets'
                      AND team_key = 'account-1:account-2'
                ),
                (
                    SELECT COUNT(*)
                    FROM band_improvement_state state
                    JOIN band_improvement_subjects subject
                      ON subject.band_subject_id =
                           state.band_subject_id
                    WHERE subject.band_type = 'Band_Duets'
                      AND subject.team_key =
                           'account-1:account-2'
                      AND state.song_id = 'song-a'
                ),
                (
                    SELECT COUNT(*)
                    FROM band_improvement_events event
                    JOIN band_improvement_subjects subject
                      ON subject.band_subject_id =
                           event.band_subject_id
                    WHERE subject.band_type = 'Band_Duets'
                      AND subject.team_key =
                           'account-1:account-2'
                      AND event.event_kind =
                           'band_first_score'
                      AND event.delivery_state = 'visible'
                )
            """;
        using var reader = verify.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal(1, reader.GetInt64(0));
        Assert.Equal(1, reader.GetInt64(1));
        Assert.Equal(0, reader.GetInt64(2));
    }

    [Fact]
    public async Task Band_candidate_parity_uses_routine_coalescing_and_excludes_missing_state_on_resume()
    {
        using var dataSource = SharedPostgresContainer.CreateDatabase();
        var manifest = CreateManifest();
        var manifestDigest = manifest.ComputeDigest();
        var planDigest = new string('7', 64);
        using (var conn = dataSource.OpenConnection())
        using (var seed = conn.CreateCommand())
        {
            seed.CommandText = """
                INSERT INTO registered_bands (
                    source_id,
                    band_type,
                    team_key,
                    band_id,
                    registered_at,
                    last_activity_at,
                    last_member_sync_at)
                VALUES (
                    'web-band-tracker',
                    'Band_Duets',
                    'account-1:account-2',
                    'band-1',
                    now() - interval '1 day',
                    now(),
                    now());

                INSERT INTO band_improvement_subjects (
                    band_type,
                    team_key,
                    team_members,
                    first_seen_at,
                    last_seen_at)
                VALUES (
                    'Band_Duets',
                    'account-1:account-2',
                    ARRAY['account-1', 'account-2'],
                    now() - interval '1 day',
                    now());

                INSERT INTO current_band_leaderboard_entries (
                    song_id,
                    band_type,
                    ranking_scope,
                    scope_combo_id,
                    team_key,
                    entry_combo_id,
                    entry_instrument_combo,
                    team_members,
                    score,
                    accuracy,
                    is_full_combo,
                    stars,
                    difficulty,
                    season,
                    rank,
                    total_entries,
                    percentile,
                    first_seen_at,
                    last_updated_at,
                    computed_at,
                    projection_generation)
                VALUES
                    (
                        'song-a',
                        'Band_Duets',
                        'overall',
                        '',
                        'account-1:account-2',
                        '0:1',
                        'Solo_Guitar+Solo_Bass',
                        ARRAY['account-1', 'account-2'],
                        201000,
                        100,
                        TRUE,
                        6,
                        3,
                        14,
                        90,
                        500,
                        82,
                        now() - interval '1 day',
                        now(),
                        now(),
                        7),
                    (
                        'song-a',
                        'Band_Duets',
                        'combo',
                        'Solo_Guitar+Solo_Bass',
                        'account-1:account-2',
                        '0:1',
                        'Solo_Guitar+Solo_Bass',
                        ARRAY['account-1', 'account-2'],
                        201000,
                        100,
                        TRUE,
                        6,
                        3,
                        14,
                        40,
                        500,
                        92,
                        now() - interval '1 day',
                        now(),
                        now(),
                        7);

                INSERT INTO band_current_projection_scope (
                    song_id,
                    band_type,
                    ranking_scope,
                    scope_combo_id,
                    projection_generation,
                    published_generation,
                    row_count,
                    published_row_count,
                    status,
                    last_rebuilt_at,
                    updated_at)
                VALUES
                    (
                        'song-a',
                        'Band_Duets',
                        'overall',
                        '',
                        7,
                        7,
                        1,
                        1,
                        'ready',
                        now(),
                        now()),
                    (
                        'song-a',
                        'Band_Duets',
                        'combo',
                        'Solo_Guitar+Solo_Bass',
                        7,
                        7,
                        1,
                        1,
                        'ready',
                        now(),
                        now());

                INSERT INTO band_improvement_state (
                    band_subject_id,
                    song_id,
                    ranking_scope,
                    scope_combo_id,
                    entry_combo_id,
                    entry_instrument_combo,
                    score,
                    rank,
                    stars,
                    is_full_combo,
                    difficulty,
                    percentile,
                    season,
                    total_entries,
                    first_seen_at,
                    last_updated_at,
                    observed_at,
                    updated_at)
                SELECT subject.band_subject_id,
                       'song-a',
                       state.ranking_scope,
                       state.scope_combo_id,
                       '0:1',
                       'Solo_Guitar+Solo_Bass',
                       200000,
                       state.rank,
                       5,
                       FALSE,
                       2,
                       80,
                       14,
                       500,
                       now() - interval '1 day',
                       now() - interval '1 hour',
                       now() - interval '1 hour',
                       now() - interval '1 hour'
                FROM band_improvement_subjects subject
                CROSS JOIN (VALUES
                    ('overall', '', 100),
                    (
                        'combo',
                        'Solo_Guitar+Solo_Bass',
                        50)
                ) state(ranking_scope, scope_combo_id, rank)
                WHERE subject.band_type = 'Band_Duets'
                  AND subject.team_key =
                      'account-1:account-2';

                INSERT INTO band_team_rankings_current_band_duets (
                    band_type,
                    ranking_scope,
                    combo_id,
                    team_key,
                    team_members,
                    songs_played,
                    total_charted_songs,
                    coverage,
                    raw_skill_rating,
                    adjusted_skill_rating,
                    adjusted_skill_rank,
                    weighted_rating,
                    weighted_rank,
                    fc_rate,
                    fc_rate_rank,
                    total_score,
                    total_score_rank,
                    avg_accuracy,
                    full_combo_count,
                    avg_stars,
                    best_rank,
                    avg_rank,
                    raw_weighted_rating,
                    computed_at)
                VALUES
                    (
                        'Band_Duets',
                        'combo',
                        'Solo_Guitar+Solo_Bass',
                        'account-1:account-2',
                        ARRAY['account-1', 'account-2'],
                        1,
                        1,
                        1,
                        0,
                        0,
                        1,
                        0,
                        90,
                        1,
                        250,
                        201000,
                        180,
                        100,
                        2,
                        6,
                        1,
                        1,
                        NULL,
                        now()),
                    (
                        'Band_Duets',
                        'overall',
                        '',
                        'account-1:account-2',
                        ARRAY['account-1', 'account-2'],
                        1,
                        1,
                        1,
                        0,
                        0,
                        1,
                        0,
                        1,
                        1,
                        1,
                        201000,
                        1,
                        100,
                        2,
                        6,
                        1,
                        1,
                        NULL,
                        now());

                INSERT INTO band_rank_improvement_state (
                    band_subject_id,
                    ranking_scope,
                    combo_id,
                    adjusted_skill_rank,
                    weighted_rank,
                    fc_rate_rank,
                    total_score_rank,
                    total_score,
                    full_combo_count,
                    computed_at,
                    observed_at,
                    updated_at)
                SELECT subject.band_subject_id,
                       'combo',
                       'Solo_Guitar+Solo_Bass',
                       1,
                       100,
                       300,
                       200,
                       200000,
                       1,
                       now() - interval '1 hour',
                       now() - interval '1 hour',
                       now() - interval '1 hour'
                FROM band_improvement_subjects subject
                WHERE subject.band_type = 'Band_Duets'
                  AND subject.team_key =
                      'account-1:account-2';
                """;
            seed.ExecuteNonQuery();
        }
        SeedNotificationMaintenanceState(
            dataSource,
            manifest,
            manifestDigest,
            planDigest);

        var (routine, service) =
            CreateNotificationMaintenanceServices(dataSource);
        var inspection = await service.InspectRoutineStateAsync(
            manifest,
            manifestDigest,
            requireOwnedFreeze: true,
            CancellationToken.None);
        var routineReport = routine.Precompute(
            new ImprovementNotificationPrecomputeOptions(
                Execute: false,
                BaselineOnly: false,
                Scope: "registered",
                IncludePlayers: false,
                IncludeBands: true,
                IncludeSongEvents: true,
                IncludeRankings: true,
                PruneExpired: false,
                PublishedScrapeId:
                    manifest.ExpectedPublishedScrapeId));
        Assert.Equal(1, routineReport.BandSongEventsInserted);
        Assert.Equal(3, routineReport.BandRankEventsInserted);

        using (var conn = dataSource.OpenConnection())
        using (var fail = conn.CreateCommand())
        {
            fail.CommandText = """
                UPDATE max_score_maintenance_runs
                SET status = 'failed',
                    failure_stage = 'derived_state_rebuilt',
                    failure_detail = 'injected resume boundary'
                WHERE manifest_sha256 = @manifestDigest
                """;
            fail.Parameters.AddWithValue(
                "manifestDigest",
                manifestDigest);
            Assert.Equal(1, fail.ExecuteNonQuery());
        }
        var resumeInspection =
            await service.InspectRoutineStateAsync(
                manifest,
                manifestDigest,
                requireOwnedFreeze: true,
                CancellationToken.None);
        Assert.Equal(
            inspection.CandidateCount,
            resumeInspection.CandidateCount);

        var result = await service.QuarantineAndAlignAsync(
            manifest,
            manifestDigest,
            planDigest,
            inspection.PublishedScoreSourceFingerprint,
            CancellationToken.None);

        Assert.Equal(18, result.CandidateCount);
        Assert.Equal(0, result.VisibleDeliveryCount);
        var aligned = await service.InspectRoutineStateAsync(
            manifest,
            manifestDigest,
            requireOwnedFreeze: true,
            CancellationToken.None);
        Assert.Empty(aligned.Candidates);

        using var verifyConnection = dataSource.OpenConnection();
        using var verify = verifyConnection.CreateCommand();
        verify.CommandText = """
            SELECT
                (
                    SELECT COUNT(*)
                    FROM improvement_notification_maintenance_candidates
                    WHERE maintenance_run_id = @maintenanceRunId
                ),
                (
                    SELECT COUNT(*)
                    FROM improvement_notification_maintenance_candidates
                    WHERE maintenance_run_id = @maintenanceRunId
                      AND candidate_kind =
                          'band_rank_state_missing'
                ),
                (
                    SELECT COUNT(*)
                    FROM band_improvement_events
                    WHERE delivery_state = 'visible'
                )
            """;
        verify.Parameters.AddWithValue(
            "maintenanceRunId",
            result.MaintenanceRunId);
        using var reader = verify.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal(18, reader.GetInt64(0));
        Assert.Equal(1, reader.GetInt64(1));
        Assert.Equal(0, reader.GetInt64(2));
    }

    private static (
        ImprovementNotificationService Routine,
        MaxScoreMaintenanceNotificationService Maintenance)
        CreateNotificationMaintenanceServices(
            Npgsql.NpgsqlDataSource dataSource)
    {
        var routine = new ImprovementNotificationService(
            dataSource,
            NullLogger<ImprovementNotificationService>.Instance);
        return (
            routine,
            new MaxScoreMaintenanceNotificationService(
                dataSource,
                routine,
                Options.Create(
                    new ImprovementNotificationOptions
                    {
                        Scope = "registered",
                    }),
                NullLogger<
                    MaxScoreMaintenanceNotificationService>.Instance));
    }

    private static ImprovementNotificationPrecomputeOptions
        CreatePlayerRankDryRunOptions(long publishedScrapeId)
        => new(
            Execute: false,
            BaselineOnly: false,
            Scope: "registered",
            IncludePlayers: true,
            IncludeBands: false,
            IncludeSongEvents: false,
            IncludeRankings: true,
            PruneExpired: false,
            PublishedScrapeId: publishedScrapeId);

    private static void SeedNotificationMaintenanceState(
        Npgsql.NpgsqlDataSource dataSource,
        MaxScoreMaintenanceManifest manifest,
        string manifestDigest,
        string planDigest)
    {
        var freezeReason =
            PublicReadFreezeState.MaxScoreMaintenanceReasonPrefix
            + manifestDigest;
        using var conn = dataSource.OpenConnection();
        using var seed = conn.CreateCommand();
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
                @manifestVersion,
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
            "manifestVersion",
            manifest.ManifestVersion);
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
