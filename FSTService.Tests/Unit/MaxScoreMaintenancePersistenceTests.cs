using FSTService.Persistence;
using FSTService.Scraping;
using FSTService.Tests.Helpers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;

namespace FSTService.Tests.Unit;

public sealed partial class MaxScoreMaintenancePersistenceTests
{
    public static TheoryData<
        string,
        int,
        int[],
        int?,
        int?,
        long> ObservedScoreEvidenceCases { get; } =
        new()
        {
            {
                "no rows",
                51_573,
                Array.Empty<int>(),
                null,
                null,
                0
            },
            {
                "all below maximum",
                51_573,
                [40_000, 50_000],
                50_000,
                50_000,
                0
            },
            {
                "slight above maximum below cutoff",
                51_573,
                [40_000, 52_809],
                52_809,
                52_809,
                0
            },
            {
                "exact cutoff",
                51_573,
                [54_151],
                54_151,
                54_151,
                0
            },
            {
                "multiple outliers with eligible row",
                51_573,
                [50_000, 60_000, 66_030],
                66_030,
                50_000,
                2
            },
            {
                "multiple outliers without eligible row",
                51_573,
                [60_000, 66_030],
                66_030,
                null,
                2
            },
            {
                "only invalid outlier",
                51_573,
                [66_030],
                66_030,
                null,
                1
            },
            {
                "show them who we are live outlier",
                63_750,
                [63_750, 145_947],
                145_947,
                63_750,
                1
            },
            {
                "run it live outlier",
                51_573,
                [51_588, 66_030],
                66_030,
                51_588,
                1
            },
        };

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

    [Theory]
    [MemberData(nameof(ObservedScoreEvidenceCases))]
    public async Task Observed_score_evidence_uses_resolved_ranking_eligibility(
        string _,
        int newMaximum,
        int[] resolvedScores,
        int? expectedHighestObservedScore,
        int? expectedHighestEligibleObservedScore,
        long expectedAboveValidCutoffCount)
    {
        using var dataSource =
            SharedPostgresContainer.CreateDatabase();
        var template = CreateManifest();
        var manifest = (template with
        {
            Songs = template.Songs
                .Select(song => song with
                {
                    StagedPath = song.StagedPath with
                    {
                        Maxima = song.StagedPath.Maxima with
                        {
                            Lead = newMaximum,
                        },
                    },
                })
                .ToArray(),
        }).ValidateAndNormalize();
        SeedPublishedSoloCurrentState(
            dataSource,
            manifest,
            overlayScore: 1);
        ReplacePublishedResolvedScores(
            dataSource,
            manifest,
            resolvedScores);

        var check = await LoadObservedScoreCheckAsync(
            dataSource,
            manifest);

        Assert.Equal(
            expectedHighestObservedScore,
            check.HighestObservedScore);
        Assert.Equal(
            expectedHighestEligibleObservedScore,
            check.HighestEligibleObservedScore);
        Assert.Equal(
            expectedAboveValidCutoffCount,
            check.AboveValidCutoffCount);
        Assert.Equal(newMaximum, check.NewMaximum);
        Assert.Equal(
            RankingsCalculator.ComputeMaxScoreThreshold(
                newMaximum),
            check.ValidCutoff);
        Assert.True(check.SourceMapped);
        Assert.True(check.Passed);
        check.ValidateContract();
    }

    [Fact]
    public async Task Observed_score_validation_handles_the_largest_int_cutoff_without_overflow()
    {
        using var dataSource =
            SharedPostgresContainer.CreateDatabase();
        var template = CreateManifest();
        var manifest = (template with
        {
            Songs = template.Songs
                .Select(song => song with
                {
                    StagedPath = song.StagedPath with
                    {
                        Maxima = song.StagedPath.Maxima with
                        {
                            Lead = RankingsCalculator
                                .MaximumScoreWithRepresentableRankingCutoff,
                        },
                    },
                })
                .ToArray(),
        }).ValidateAndNormalize();
        SeedPublishedSoloCurrentState(
            dataSource,
            manifest,
            overlayScore: int.MaxValue);

        await using (var cutoffCommand = dataSource.CreateCommand(
                         """
                         SELECT FLOOR(
                             @maximum::NUMERIC * 1.05)::INTEGER
                         """))
        {
            cutoffCommand.Parameters.AddWithValue(
                "maximum",
                RankingsCalculator
                    .MaximumScoreWithRepresentableRankingCutoff);
            Assert.Equal(
                int.MaxValue,
                (int)(await cutoffCommand.ExecuteScalarAsync())!);
        }

        await using var connection =
            await dataSource.OpenConnectionAsync();
        await using var transaction =
            await connection.BeginTransactionAsync();
        var check = Assert.Single(
            await MaxScoreMaintenanceService
                .LoadObservedScoreChecksAsync(
                    manifest,
                    connection,
                    transaction,
                    CancellationToken.None));

        Assert.Equal(
            RankingsCalculator
                .MaximumScoreWithRepresentableRankingCutoff,
            check.NewMaximum);
        Assert.Equal(int.MaxValue, check.ValidCutoff);
        Assert.Equal(int.MaxValue, check.HighestObservedScore);
        Assert.Equal(
            int.MaxValue,
            check.HighestEligibleObservedScore);
        Assert.Equal(0, check.AboveValidCutoffCount);
        Assert.True(check.Passed);
        check.ValidateContract();
    }

    [Fact]
    public async Task Observed_score_validation_fails_closed_without_authoritative_source()
    {
        using var dataSource =
            SharedPostgresContainer.CreateDatabase();
        var manifest = CreateManifest();
        SeedPublishedSoloCurrentState(
            dataSource,
            manifest,
            overlayScore: 50_000);
        await using (var removeSource =
                     dataSource.CreateCommand(
                         """
                         DELETE FROM leaderboard_published_scope_source
                         WHERE published_scrape_id = @scrapeId
                           AND song_id = 'song-a'
                           AND instrument = 'Solo_Guitar'
                         """))
        {
            removeSource.Parameters.AddWithValue(
                "scrapeId",
                manifest.ExpectedPublishedScrapeId);
            Assert.Equal(
                1,
                await removeSource.ExecuteNonQueryAsync());
        }

        await using var connection =
            await dataSource.OpenConnectionAsync();
        await using var transaction =
            await connection.BeginTransactionAsync();
        var check = Assert.Single(
            await MaxScoreMaintenanceService
                .LoadObservedScoreChecksAsync(
                    manifest,
                    connection,
                    transaction,
                    CancellationToken.None));

        Assert.False(check.SourceMapped);
        Assert.Null(check.HighestObservedScore);
        Assert.Null(check.HighestEligibleObservedScore);
        Assert.Equal(0, check.AboveValidCutoffCount);
        Assert.Equal(54_151, check.ValidCutoff);
        Assert.False(check.Passed);
        check.ValidateContract();
    }

    [Fact]
    public async Task Affected_player_stats_rebuild_and_validation_include_overlay_only_accounts()
    {
        using var dataSource =
            SharedPostgresContainer.CreateDatabase();
        var manifest = CreateManifest();
        SeedPublishedSoloCurrentState(
            dataSource,
            manifest,
            overlayScore: 50_000);

        await using var connection =
            await dataSource.OpenConnectionAsync();
        await using var transaction =
            await connection.BeginTransactionAsync();
        var affected =
            await MaxScoreMaintenanceDerivedStateService
                .LoadAffectedPlayerStatsAccountsAsync(
                    manifest,
                    connection,
                    transaction,
                    CancellationToken.None);
        var rebuildStartedAt =
            DateTime.UtcNow.AddSeconds(-1);

        Assert.Equal(
            ["account-base", "account-overlay"],
            affected);

        await using (var seedStats =
                     connection.CreateCommand())
        {
            seedStats.Transaction = transaction;
            seedStats.CommandText = """
                INSERT INTO player_stats_tiers (
                    account_id,
                    instrument,
                    tiers_json,
                    updated_at)
                VALUES (
                    'account-base',
                    'Overall',
                    '[]'::JSONB,
                    now())
                """;
            await seedStats.ExecuteNonQueryAsync();
        }
        Assert.Equal(
            1,
            await MaxScoreMaintenanceDerivedStateService
                .CountInvalidPlayerStatsAccountsAsync(
                    affected,
                    ["Solo_Guitar"],
                    rebuildStartedAt,
                    connection,
                    transaction,
                    CancellationToken.None));

        await using (var seedOverlayStats =
                     connection.CreateCommand())
        {
            seedOverlayStats.Transaction = transaction;
            seedOverlayStats.CommandText = """
                INSERT INTO player_stats_tiers (
                    account_id,
                    instrument,
                    tiers_json,
                    updated_at)
                VALUES
                    (
                        'account-overlay',
                        'Overall',
                        '[]'::JSONB,
                        now()
                    ),
                    (
                        'account-overlay',
                        'Solo_Drums',
                        '[]'::JSONB,
                        now()
                    )
                """;
            await seedOverlayStats.ExecuteNonQueryAsync();
        }
        Assert.Equal(
            1,
            await MaxScoreMaintenanceDerivedStateService
                .CountInvalidPlayerStatsAccountsAsync(
                    affected,
                    ["Solo_Guitar"],
                    rebuildStartedAt,
                    connection,
                    transaction,
                    CancellationToken.None));

        await using (var removeStaleStats =
                     connection.CreateCommand())
        {
            removeStaleStats.Transaction = transaction;
            removeStaleStats.CommandText = """
                DELETE FROM player_stats_tiers
                WHERE account_id = 'account-overlay'
                  AND instrument = 'Solo_Drums'
                """;
            await removeStaleStats.ExecuteNonQueryAsync();
        }
        Assert.Equal(
            0,
            await MaxScoreMaintenanceDerivedStateService
                .CountInvalidPlayerStatsAccountsAsync(
                    affected,
                    ["Solo_Guitar"],
                    rebuildStartedAt,
                    connection,
                    transaction,
                    CancellationToken.None));
    }

    [Fact]
    public void Maintenance_ranking_read_pass_uses_published_source_and_overlay_resolution()
    {
        using var dataSource =
            SharedPostgresContainer.CreateDatabase();
        var manifest = CreateManifest();
        SeedPublishedSoloCurrentState(
            dataSource,
            manifest,
            overlayScore: 50_000);
        ScrapeRunTestHelper.EnsureAllocated(
            dataSource,
            1297,
            completed: true);
        using (var connection =
               dataSource.OpenConnection())
        using (var seed = connection.CreateCommand())
        {
            seed.CommandText = """
                INSERT INTO leaderboard_entries_snapshot (
                    snapshot_id,
                    song_id,
                    instrument,
                    account_id,
                    score,
                    rank,
                    source,
                    first_seen_at,
                    last_updated_at)
                VALUES (
                    1297,
                    'song-a',
                    'Solo_Guitar',
                    'account-base',
                    90000,
                    1,
                    'scrape',
                    now(),
                    now());
                INSERT INTO leaderboard_snapshot_state (
                    song_id,
                    instrument,
                    active_snapshot_id,
                    scrape_id,
                    is_finalized,
                    updated_at)
                VALUES (
                    'song-a',
                    'Solo_Guitar',
                    1297,
                    1297,
                    TRUE,
                    now());

                INSERT INTO leaderboard_entries (
                    song_id,
                    instrument,
                    account_id,
                    score,
                    rank,
                    source,
                    first_seen_at,
                    last_updated_at)
                VALUES (
                    'unmapped-song',
                    'Solo_Guitar',
                    'account-unmapped',
                    88000,
                    1,
                    'scrape',
                    now(),
                    now());
                """;
            seed.ExecuteNonQuery();
        }

        using var meta = new MetaDatabase(
            dataSource,
            NullLogger<MetaDatabase>.Instance);
        using var loggerFactory =
            Microsoft.Extensions.Logging.LoggerFactory
                .Create(_ => { });
        using var persistence =
            new GlobalLeaderboardPersistence(
                meta,
                loggerFactory,
                NullLogger<
                    GlobalLeaderboardPersistence>.Instance,
                dataSource,
                Options.Create(new FeatureOptions()));
        persistence.InitializeReadOnly();
        var database =
            persistence.GetOrCreateInstrumentDb(
                "Solo_Guitar");

        Assert.Equal(
            90_000,
            Assert.Single(
                database.GetCurrentStatePlayerScores(
                    "account-base")).Score);
        Assert.Equal(
            88_000,
            Assert.Single(
                database.GetCurrentStatePlayerScores(
                    "account-unmapped")).Score);
        using (persistence
               .BeginMaxScoreMaintenancePublishedReadPass())
        {
            Assert.Equal(
                40_000,
                Assert.Single(
                    database.GetCurrentStatePlayerScores(
                        "account-base")).Score);
            Assert.Equal(
                50_000,
                Assert.Single(
                    database.GetCurrentStatePlayerScores(
                        "account-overlay")).Score);
            Assert.Equal(
                40_000,
                Assert.Single(
                    persistence
                        .GetCurrentStatePlayerProfile(
                            "account-base")).Score);
            Assert.Equal(
                50_000,
                Assert.Single(
                    persistence
                        .GetCurrentStatePlayerProfile(
                            "account-overlay")).Score);
            Assert.Empty(
                database.GetCurrentStatePlayerScores(
                    "account-unmapped"));
        }
        Assert.Equal(
            90_000,
            Assert.Single(
                database.GetCurrentStatePlayerScores(
                    "account-base")).Score);
        Assert.Equal(
            88_000,
            Assert.Single(
                database.GetCurrentStatePlayerScores(
                    "account-unmapped")).Score);
    }

    [Fact]
    public async Task Score_history_evidence_tracks_complete_consumed_inputs()
    {
        using var dataSource =
            SharedPostgresContainer.CreateDatabase();
        var manifest = CreateManifest();
        SeedPublishedSoloCurrentState(
            dataSource,
            manifest,
            overlayScore: 60_000);
        using (var connection =
               dataSource.OpenConnection())
        using (var seedUnrelated =
               connection.CreateCommand())
        {
            seedUnrelated.CommandText = """
                INSERT INTO leaderboard_entries_snapshot (
                    snapshot_id,
                    song_id,
                    instrument,
                    account_id,
                    score,
                    rank,
                    source,
                    first_seen_at,
                    last_updated_at)
                VALUES (
                    @scrapeId,
                    'other-song',
                    'Solo_Guitar',
                    'account-other',
                    70000,
                    1,
                    'scrape',
                    now(),
                    now());

                INSERT INTO leaderboard_published_scope_source (
                    published_scrape_id,
                    song_id,
                    instrument,
                    scope_kind,
                    source_kind,
                    source_snapshot_id,
                    source_scrape_id,
                    row_count,
                    content_fingerprint,
                    coverage_fingerprint,
                    reported_total_entries,
                    reported_total_pages,
                    is_complete,
                    created_at,
                    validated_at)
                VALUES (
                    @scrapeId,
                    'other-song',
                    'Solo_Guitar',
                    'alltime',
                    'snapshot',
                    @scrapeId,
                    @scrapeId,
                    1,
                    md5('other-song'),
                    md5('other-song:coverage'),
                    1,
                    1,
                    TRUE,
                    now(),
                    now());

                UPDATE publication_surface_bindings
                SET row_count = (
                    SELECT COUNT(*)
                    FROM leaderboard_published_scope_source
                    WHERE published_scrape_id = @scrapeId
                      AND scope_kind = 'alltime'
                )
                WHERE publication_id = @publicationId
                  AND surface_name = 'solo_scope_sources';
                """;
            seedUnrelated.Parameters.AddWithValue(
                "scrapeId",
                manifest.ExpectedPublishedScrapeId);
            seedUnrelated.Parameters.AddWithValue(
                "publicationId",
                manifest.ExpectedPublicationId);
            seedUnrelated.ExecuteNonQuery();
        }
        var postPromotionMaxScores =
            new Dictionary<string, SongMaxScores>(
                StringComparer.OrdinalIgnoreCase)
            {
                ["song-a"] = manifest.Songs[0]
                    .StagedPath.Maxima
                    .ToSongMaxScores(),
                ["other-song"] = new SongMaxScores
                {
                    MaxLeadScore = 50_000,
                    MaxBassScore = checked(
                        RankingsCalculator
                            .MaximumScoreWithRepresentableRankingCutoff
                        + 1),
                },
            };
        var originalId = InsertHistory(
            "song-a",
            "Solo_Guitar",
            "account-overlay",
            50_000,
            accuracy: 95);
        var baseline = await ComputeFingerprintAsync();

        InsertHistory(
            "other-song",
            "Solo_Guitar",
            "account-overlay",
            40_000,
            accuracy: 90);
        InsertHistory(
            "song-a",
            "Solo_Guitar",
            "account-overlay",
            70_000,
            accuracy: 100);
        Assert.Equal(
            baseline,
            await ComputeFingerprintAsync());

        var unrelatedAffectedId = InsertHistory(
            "other-song",
            "Solo_Guitar",
            "account-other",
            40_000,
            accuracy: 91);
        Assert.NotEqual(
            baseline,
            await ComputeFingerprintAsync());
        ExecuteHistoryMutation(
            "DELETE FROM score_history WHERE id = @id",
            unrelatedAffectedId);
        Assert.Equal(
            baseline,
            await ComputeFingerprintAsync());

        var insertedId = InsertHistory(
            "song-a",
            "Solo_Guitar",
            "account-overlay",
            51_000,
            accuracy: 96);
        Assert.NotEqual(
            baseline,
            await ComputeFingerprintAsync());
        ExecuteHistoryMutation(
            "DELETE FROM score_history WHERE id = @id",
            insertedId);
        Assert.Equal(
            baseline,
            await ComputeFingerprintAsync());

        ExecuteHistoryMutation(
            """
            UPDATE score_history
            SET accuracy = 97
            WHERE id = @id
            """,
            originalId);
        Assert.NotEqual(
            baseline,
            await ComputeFingerprintAsync());
        ExecuteHistoryMutation(
            "DELETE FROM score_history WHERE id = @id",
            originalId);
        Assert.NotEqual(
            baseline,
            await ComputeFingerprintAsync());

        long InsertHistory(
            string songId,
            string instrument,
            string accountId,
            int score,
            int accuracy)
        {
            using var connection =
                dataSource.OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO score_history (
                    song_id,
                    instrument,
                    account_id,
                    new_score,
                    new_rank,
                    accuracy,
                    changed_at)
                VALUES (
                    @songId,
                    @instrument,
                    @accountId,
                    @score,
                    1,
                    @accuracy,
                    now())
                RETURNING id
                """;
            command.Parameters.AddWithValue(
                "songId",
                songId);
            command.Parameters.AddWithValue(
                "instrument",
                instrument);
            command.Parameters.AddWithValue(
                "accountId",
                accountId);
            command.Parameters.AddWithValue(
                "score",
                score);
            command.Parameters.AddWithValue(
                "accuracy",
                accuracy);
            return Convert.ToInt64(
                command.ExecuteScalar());
        }

        void ExecuteHistoryMutation(
            string sql,
            long id)
        {
            using var connection =
                dataSource.OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            command.Parameters.AddWithValue("id", id);
            Assert.Equal(1, command.ExecuteNonQuery());
        }

        async Task<string> ComputeFingerprintAsync()
        {
            await using var connection =
                await dataSource.OpenConnectionAsync();
            await using var transaction =
                await connection.BeginTransactionAsync(
                    System.Data.IsolationLevel
                        .RepeatableRead);
            var evidence =
                await MaxScoreMaintenanceService
                .ComputeScoreHistoryEvidenceAsync(
                    manifest,
                    postPromotionMaxScores,
                    connection,
                    transaction,
                    CancellationToken.None);
            var reference =
                await MaxScoreMaintenanceService
                    .ComputeScoreHistoryEvidenceReferenceAsync(
                        manifest,
                        postPromotionMaxScores,
                        connection,
                        transaction,
                        CancellationToken.None);
            Assert.Equal(reference, evidence);
            return evidence.Fingerprint;
        }
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
                    score_history_fingerprint,
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
                    score_history_fingerprint,
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
    public async Task Persisted_rollback_evidence_accepts_valid_resume_snapshot()
    {
        var manifest = CreateManifest();
        var snapshot =
            MaxScoreMaintenanceService.CreateRollbackSnapshot(
                manifest,
                manifest.ComputeDigest(),
                new string('a', 64),
                new DateTime(
                    2026,
                    8,
                    14,
                    9,
                    0,
                    0,
                    DateTimeKind.Utc));
        var dataDirectory = CreateEvidenceDirectory();
        try
        {
            var written =
                await MaxScoreMaintenanceFileStore
                    .WriteCanonicalRollbackSnapshotAsync(
                        dataDirectory,
                        "rollback.json",
                        snapshot,
                        CancellationToken.None);

            var validated =
                await MaxScoreMaintenanceFileStore
                    .ValidateCanonicalRollbackSnapshotAsync(
                        dataDirectory,
                        written.FullPath,
                        written.Sha256,
                        snapshot,
                        CancellationToken.None);

            Assert.Equal(
                snapshot.SerializeCanonical(),
                validated.SerializeCanonical());
        }
        finally
        {
            Directory.Delete(dataDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task Persisted_rollback_evidence_rejects_deleted_resume_file()
    {
        var manifest = CreateManifest();
        var snapshot =
            MaxScoreMaintenanceService.CreateRollbackSnapshot(
                manifest,
                manifest.ComputeDigest(),
                new string('b', 64),
                DateTime.UtcNow);
        var dataDirectory = CreateEvidenceDirectory();
        try
        {
            var written =
                await MaxScoreMaintenanceFileStore
                    .WriteCanonicalRollbackSnapshotAsync(
                        dataDirectory,
                        "rollback.json",
                        snapshot,
                        CancellationToken.None);
            File.Delete(written.FullPath);

            await Assert.ThrowsAsync<ArgumentException>(
                () => MaxScoreMaintenanceFileStore
                    .ValidateCanonicalRollbackSnapshotAsync(
                        dataDirectory,
                        written.FullPath,
                        written.Sha256,
                        snapshot,
                        CancellationToken.None));
        }
        finally
        {
            Directory.Delete(dataDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task Persisted_rollback_evidence_rejects_corrupted_resume_file()
    {
        var manifest = CreateManifest();
        var snapshot =
            MaxScoreMaintenanceService.CreateRollbackSnapshot(
                manifest,
                manifest.ComputeDigest(),
                new string('c', 64),
                DateTime.UtcNow);
        var dataDirectory = CreateEvidenceDirectory();
        try
        {
            var written =
                await MaxScoreMaintenanceFileStore
                    .WriteCanonicalRollbackSnapshotAsync(
                        dataDirectory,
                        "rollback.json",
                        snapshot,
                        CancellationToken.None);
            await File.WriteAllTextAsync(
                written.FullPath,
                "{");

            await Assert.ThrowsAsync<ArgumentException>(
                () => MaxScoreMaintenanceFileStore
                    .ValidateCanonicalRollbackSnapshotAsync(
                        dataDirectory,
                        written.FullPath,
                        written.Sha256,
                        snapshot,
                        CancellationToken.None));
        }
        finally
        {
            Directory.Delete(dataDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task Persisted_rollback_evidence_rejects_wrong_canonical_snapshot()
    {
        var manifest = CreateManifest();
        var snapshot =
            MaxScoreMaintenanceService.CreateRollbackSnapshot(
                manifest,
                manifest.ComputeDigest(),
                new string('d', 64),
                DateTime.UtcNow);
        var swapped = snapshot with
        {
            PlanDigest = new string('e', 64),
        };
        var dataDirectory = CreateEvidenceDirectory();
        var path = Path.Combine(
            dataDirectory,
            "rollback.json");
        try
        {
            await File.WriteAllBytesAsync(
                path,
                swapped.SerializeCanonical());
            var swappedSha =
                await MaxScoreMaintenanceFileStore
                    .ComputeSha256Async(
                        path,
                        CancellationToken.None);

            await Assert.ThrowsAsync<InvalidOperationException>(
                () => MaxScoreMaintenanceFileStore
                    .ValidateCanonicalRollbackSnapshotAsync(
                        dataDirectory,
                        path,
                        swappedSha,
                        snapshot,
                        CancellationToken.None));
        }
        finally
        {
            Directory.Delete(dataDirectory, recursive: true);
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
                    score_history_fingerprint,
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

        using var mutateHistoryFingerprint =
            conn.CreateCommand();
        mutateHistoryFingerprint.CommandText = """
            UPDATE max_score_maintenance_runs
            SET score_history_fingerprint = @replacement
            WHERE manifest_sha256 = @manifestDigest
            """;
        mutateHistoryFingerprint.Parameters.AddWithValue(
            "replacement",
            new string('b', 64));
        mutateHistoryFingerprint.Parameters.AddWithValue(
            "manifestDigest",
            manifestDigest);
        var historyFingerprintError =
            Assert.Throws<Npgsql.PostgresException>(
                () => mutateHistoryFingerprint
                    .ExecuteNonQuery());
        Assert.Equal(
            "55000",
            historyFingerprintError.SqlState);

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
    public async Task Maintenance_source_lock_and_durable_gate_fence_score_history_writes()
    {
        using var dataSource =
            SharedPostgresContainer.CreateDatabase();
        using var meta = new MetaDatabase(
            dataSource,
            NullLogger<MetaDatabase>.Instance);
        var lease =
            await meta.AcquireMaxScoreMaintenanceLeaseAsync(
                500);
        try
        {
            await lease.ExecuteTransactionAsync(
                "score-history-lock-probe",
                requireSourceLocks: true,
                async (_, _, _) =>
                {
                    await using var competing =
                        await dataSource.OpenConnectionAsync();
                    await using var insert =
                        competing.CreateCommand();
                    insert.CommandText = """
                        SET lock_timeout = '100ms';
                        INSERT INTO score_history (
                            song_id,
                            instrument,
                            account_id,
                            new_score,
                            new_rank,
                            changed_at)
                        VALUES (
                            'song-lock',
                            'Solo_Guitar',
                            'account-lock',
                            100,
                            1,
                            now())
                        """;
                    var blocked =
                        await Assert.ThrowsAsync<
                            PostgresException>(
                            () => insert
                                .ExecuteNonQueryAsync());
                    Assert.Equal(
                        PostgresErrorCodes.LockNotAvailable,
                        blocked.SqlState);
                });

            await using var fencedConnection =
                await dataSource.OpenConnectionAsync();
            await using var fencedInsert =
                fencedConnection.CreateCommand();
            fencedInsert.CommandText = """
                INSERT INTO score_history (
                    song_id,
                    instrument,
                    account_id,
                    new_score,
                    new_rank,
                    changed_at)
                VALUES (
                    'song-fenced',
                    'Solo_Guitar',
                    'account-fenced',
                    100,
                    1,
                    now())
                """;
            var fenced =
                await Assert.ThrowsAsync<PostgresException>(
                    () => fencedInsert.ExecuteNonQueryAsync());
            Assert.Equal("55000", fenced.SqlState);
        }
        finally
        {
            await lease.DisposeAsync();
        }

        await using var releasedConnection =
            await dataSource.OpenConnectionAsync();
        await using var releasedInsert =
            releasedConnection.CreateCommand();
        releasedInsert.CommandText = """
            INSERT INTO score_history (
                song_id,
                instrument,
                account_id,
                new_score,
                new_rank,
                changed_at)
            VALUES (
                'song-released',
                'Solo_Guitar',
                'account-released',
                100,
                1,
                now())
            """;
        Assert.Equal(
            1,
            await releasedInsert.ExecuteNonQueryAsync());
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
                    score_history_fingerprint,
                    manifest_json,
                    freeze_reason,
                    phase,
                    status,
                    staged_cache_entry_count,
                    staged_cache_evidence)
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
                    @historyFingerprint,
                    '{}'::jsonb,
                    @freezeReason,
                    'notifications_quarantined',
                    'running',
                    0,
                    NULL);

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

                INSERT INTO max_score_maintenance_cache_entries (
                    manifest_sha256,
                    cache_key,
                    etag,
                    json_sha256)
                VALUES (
                    @manifestDigest,
                    'route',
                    'etag',
                    encode(
                        digest(
                            decode('7b7d', 'hex'),
                            'sha256'),
                        'hex'));

                UPDATE max_score_maintenance_runs
                SET phase = 'validated',
                    staged_cache_entry_count = 1,
                    staged_cache_evidence =
                        jsonb_build_object(
                            'entryCount', 1,
                            'contentFingerprint',
                                repeat('d', 64),
                            'publishedScopeCacheKeyCount', 0,
                            'publishedScopeCacheKeyFingerprint',
                                repeat('e', 64),
                            'targetScopeCount', 0,
                            'targetScopeFingerprint',
                                repeat('f', 64),
                            'affectedAccountCount', 0,
                            'affectedAccountFingerprint',
                                repeat('1', 64),
                            'overlayOnlyAccountCount', 0,
                            'overlayOnlyAccountFingerprint',
                                repeat('2', 64))
                WHERE manifest_sha256 = @manifestDigest;
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
            await maintenanceLease.VerifyHeldAsync(
                requireSourceLocks: true);
            queuedRegistration = Task.Run(async () =>
            {
                while (true)
                {
                    try
                    {
                        await using var registrationLease =
                            await meta
                                .AcquireRegistrationMutationLeaseAsync();
                        meta.RegisterUser(
                            "post-cache-device",
                            "post-cache-account");
                        return;
                    }
                    catch (RegistrationMutationBlockedException)
                    {
                        await Task.Delay(25);
                    }
                }
            });
            await Task.Delay(150);
            Assert.False(queuedRegistration.IsCompleted);

            await maintenanceLease.CompleteAsync(
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
    public async Task Final_cache_validation_uses_configured_server_timeout_and_restores_bounded_mutation_timeout()
    {
        using var dataSource =
            SharedPostgresContainer.CreateDatabase();
        const long scrapeId = 1297;
        const long publicationId = 501;
        const int configuredTimeoutSeconds = 1800;
        var manifestDigest = new string('5', 64);
        SeedValidatedCompletionState(
            dataSource,
            scrapeId,
            publicationId,
            manifestDigest);

        using var meta = new MetaDatabase(
            dataSource,
            NullLogger<MetaDatabase>.Instance,
            scraperOptions: new ScraperOptions
            {
                MaxScoreMaintenanceCommandTimeoutSeconds =
                    configuredTimeoutSeconds,
            });
        var observed =
            new List<MaxScoreMaintenanceServerTimeoutTestContext>();
        meta.MaxScoreMaintenanceServerTimeoutTestHook =
            observed.Add;

        await using var lease =
            await meta.AcquireMaxScoreMaintenanceLeaseAsync(
                publicationId);
        await lease.CompleteAsync(
            scrapeId,
            manifestDigest);

        Assert.Collection(
            observed,
            validation =>
            {
                Assert.Equal(
                    "final-cache-validation",
                    validation.Stage);
                Assert.Equal(
                    configuredTimeoutSeconds,
                    validation.StatementTimeoutSeconds);
                Assert.Equal(5, validation.LockTimeoutSeconds);
                Assert.Equal(
                    "serializable",
                    validation.TransactionIsolation);
            },
            mutations =>
            {
                Assert.Equal(
                    "final-bounded-mutations",
                    mutations.Stage);
                Assert.Equal(
                    120,
                    mutations.StatementTimeoutSeconds);
                Assert.Equal(5, mutations.LockTimeoutSeconds);
                Assert.Equal(
                    "serializable",
                    mutations.TransactionIsolation);
            });
    }

    [Fact]
    public async Task Final_cache_publication_rechecks_immutable_entry_evidence_before_unfreeze()
    {
        using var dataSource =
            SharedPostgresContainer.CreateDatabase();
        const long scrapeId = 1297;
        const long publicationId = 501;
        var manifestDigest = new string('6', 64);
        SeedValidatedCompletionState(
            dataSource,
            scrapeId,
            publicationId,
            manifestDigest);
        using (var connection =
               dataSource.OpenConnection())
        using (var mutateEvidence =
               connection.CreateCommand())
        {
            mutateEvidence.CommandText = """
                UPDATE max_score_maintenance_cache_entries
                SET etag = 'replacement'
                WHERE manifest_sha256 = @manifestDigest
                  AND cache_key = 'route'
                """;
            mutateEvidence.Parameters.AddWithValue(
                "manifestDigest",
                manifestDigest);
            var immutable =
                Assert.Throws<PostgresException>(
                    () => mutateEvidence.ExecuteNonQuery());
            Assert.Equal("55000", immutable.SqlState);
        }
        using (var connection =
               dataSource.OpenConnection())
        using (var tamper = connection.CreateCommand())
        {
            tamper.CommandText = """
                UPDATE api_response_cache_staging
                SET json_data =
                        decode(
                            '7b2274616d7065726564223a747275657d',
                            'hex'),
                    etag = 'tampered'
                WHERE cache_key = 'route';
                UPDATE publication_api_response_cache_staging
                SET json_data =
                        decode(
                            '7b2274616d7065726564223a747275657d',
                            'hex'),
                    etag = 'tampered'
                WHERE publication_id = @publicationId
                  AND cache_key = 'route';
                """;
            tamper.Parameters.AddWithValue(
                "publicationId",
                publicationId);
            var blocked =
                Assert.Throws<PostgresException>(
                    () => tamper.ExecuteNonQuery());
            Assert.Equal(
                PostgresErrorCodes.ObjectNotInPrerequisiteState,
                blocked.SqlState);
        }

        using var meta = new MetaDatabase(
            dataSource,
            NullLogger<MetaDatabase>.Instance);
        await using var lease =
            await meta.AcquireMaxScoreMaintenanceLeaseAsync(
                publicationId);
        await lease.ExecuteTransactionAsync(
            "owner-cache-tamper",
            requireSourceLocks: true,
            async (connection, transaction, ct) =>
            {
                await using var tamper =
                    connection.CreateCommand();
                tamper.Transaction = transaction;
                tamper.CommandText = """
                    UPDATE api_response_cache_staging
                    SET json_data =
                            decode(
                                '7b2274616d7065726564223a747275657d',
                                'hex'),
                        etag = 'tampered'
                    WHERE cache_key = 'route';
                    UPDATE publication_api_response_cache_staging
                    SET json_data =
                            decode(
                                '7b2274616d7065726564223a747275657d',
                                'hex'),
                        etag = 'tampered'
                    WHERE publication_id = @publicationId
                      AND cache_key = 'route';
                    """;
                tamper.Parameters.AddWithValue(
                    "publicationId",
                    publicationId);
                await tamper.ExecuteNonQueryAsync(ct);
            });
        var error =
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => lease.CompleteAsync(
                    scrapeId,
                    manifestDigest));
        Assert.Contains(
            "immutable entry evidence",
            error.Message,
            StringComparison.OrdinalIgnoreCase);

        using var verifyConnection =
            dataSource.OpenConnection();
        using var verify =
            verifyConnection.CreateCommand();
        verify.CommandText = """
            SELECT publication.public_reads_frozen,
                   run.phase,
                   run.status,
                   (
                       SELECT etag
                       FROM api_response_cache
                       WHERE cache_key = 'route'
                   )
            FROM scrape_publication_state publication
            JOIN max_score_maintenance_runs run
              ON run.manifest_sha256 =
                 @manifestDigest
            WHERE publication.id = TRUE
            """;
        verify.Parameters.AddWithValue(
            "manifestDigest",
            manifestDigest);
        using var reader = verify.ExecuteReader();
        Assert.True(reader.Read());
        Assert.True(reader.GetBoolean(0));
        Assert.Equal("validated", reader.GetString(1));
        Assert.Equal("running", reader.GetString(2));
        Assert.Equal("old-etag", reader.GetString(3));
    }

    [Fact]
    public async Task Completed_publication_retains_durable_gate_until_disposal_and_fences_stale_writer()
    {
        using var dataSource =
            SharedPostgresContainer.CreateDatabase();
        const long scrapeId = 1298;
        const long publicationId = 502;
        var manifestDigest = new string('8', 64);
        using var meta = new MetaDatabase(
            dataSource,
            NullLogger<MetaDatabase>.Instance);
        var staleLease =
            await meta.AcquireRegistrationMutationLeaseAsync();
        try
        {
            await staleLease.VerifyHeldAsync();
            using var terminator =
                dataSource.OpenConnection();
            using var terminate =
                terminator.CreateCommand();
            terminate.CommandText =
                "SELECT pg_terminate_backend(@backendProcessId)";
            terminate.Parameters.AddWithValue(
                "backendProcessId",
                staleLease.BackendProcessId);
            Assert.True(terminate.ExecuteScalar() is true);
            await Assert.ThrowsAsync<
                RegistrationMutationBlockedException>(
                () => staleLease.VerifyHeldAsync());
        }
        finally
        {
            await staleLease.DisposeAsync();
        }

        SeedValidatedCompletionState(
            dataSource,
            scrapeId,
            publicationId,
            manifestDigest);
        var maintenanceLease =
            await meta.AcquireMaxScoreMaintenanceLeaseAsync(
                publicationId);
        try
        {
            await maintenanceLease.CompleteAsync(
                scrapeId,
                manifestDigest);

            Assert.True(meta.AreRegistrationMutationsBlocked());
            using var staleConnection =
                dataSource.OpenConnection();
            using var staleWrite =
                staleConnection.CreateCommand();
            staleWrite.CommandText = """
                INSERT INTO leaderboard_entries (
                    song_id,
                    instrument,
                    account_id,
                    score,
                    rank,
                    source,
                    first_seen_at,
                    last_updated_at)
                VALUES (
                    'handoff-song',
                    'Solo_Guitar',
                    'stale-writer',
                    100000,
                    1,
                    'backfill',
                    now(),
                    now())
                """;
            var blocked =
                Assert.Throws<PostgresException>(
                    () => staleWrite.ExecuteNonQuery());
            Assert.Equal("55000", blocked.SqlState);

            using var verifyConnection =
                dataSource.OpenConnection();
            using var verify =
                verifyConnection.CreateCommand();
            verify.CommandText = """
                SELECT
                    NOT public_reads_frozen,
                    max_score_mutation_gate_token IS NOT NULL,
                    (
                        SELECT phase = 'completed'
                           AND status = 'completed'
                        FROM max_score_maintenance_runs
                        WHERE manifest_sha256 = @manifestDigest
                    ),
                    (
                        SELECT etag = 'new-etag'
                        FROM api_response_cache
                        WHERE cache_key = 'route'
                    ),
                    NOT EXISTS (
                        SELECT 1
                        FROM leaderboard_entries
                        WHERE song_id = 'handoff-song'
                          AND instrument = 'Solo_Guitar'
                          AND account_id = 'stale-writer'
                    )
                FROM scrape_publication_state
                WHERE id = TRUE
                """;
            verify.Parameters.AddWithValue(
                "manifestDigest",
                manifestDigest);
            using var reader = verify.ExecuteReader();
            Assert.True(reader.Read());
            Assert.True(reader.GetBoolean(0));
            Assert.True(reader.GetBoolean(1));
            Assert.True(reader.GetBoolean(2));
            Assert.True(reader.GetBoolean(3));
            Assert.True(reader.GetBoolean(4));
        }
        finally
        {
            await maintenanceLease.DisposeAsync();
        }

        Assert.False(meta.AreRegistrationMutationsBlocked());
    }

    [Fact]
    public async Task Backend_loss_after_advisory_release_leaves_completed_publication_fail_closed()
    {
        using var dataSource =
            SharedPostgresContainer.CreateDatabase();
        const long scrapeId = 1299;
        const long publicationId = 503;
        var manifestDigest = new string('9', 64);
        SeedValidatedCompletionState(
            dataSource,
            scrapeId,
            publicationId,
            manifestDigest);
        using var meta = new MetaDatabase(
            dataSource,
            NullLogger<MetaDatabase>.Instance);
        var maintenanceLease =
            await meta.AcquireMaxScoreMaintenanceLeaseAsync(
                publicationId);
        await maintenanceLease.CompleteAsync(
            scrapeId,
            manifestDigest);

        meta.MaxScoreMaintenanceAfterLocksReleasedTestHook =
            context =>
            {
                Assert.Equal(
                    "lease-disposal",
                    context.Operation);
                using var terminator =
                    dataSource.OpenConnection();
                using var terminate =
                    terminator.CreateCommand();
                terminate.CommandText =
                    "SELECT pg_terminate_backend(@backendProcessId)";
                terminate.Parameters.AddWithValue(
                    "backendProcessId",
                    context.BackendProcessId);
                Assert.True(terminate.ExecuteScalar() is true);
            };
        try
        {
            await maintenanceLease.DisposeAsync();
        }
        finally
        {
            meta.MaxScoreMaintenanceAfterLocksReleasedTestHook =
                null;
        }

        using (var failedConnection =
               dataSource.OpenConnection())
        using (var failed = failedConnection.CreateCommand())
        {
            failed.CommandText = """
                SELECT
                    NOT public_reads_frozen,
                    max_score_mutation_gate_token IS NOT NULL,
                    (
                        SELECT phase = 'completed'
                           AND status = 'completed'
                        FROM max_score_maintenance_runs
                        WHERE manifest_sha256 = @manifestDigest
                    ),
                    (
                        SELECT etag = 'new-etag'
                        FROM api_response_cache
                        WHERE cache_key = 'route'
                    )
                FROM scrape_publication_state
                WHERE id = TRUE
                """;
            failed.Parameters.AddWithValue(
                "manifestDigest",
                manifestDigest);
            using var reader = failed.ExecuteReader();
            Assert.True(reader.Read());
            Assert.True(reader.GetBoolean(0));
            Assert.True(reader.GetBoolean(1));
            Assert.True(reader.GetBoolean(2));
            Assert.True(reader.GetBoolean(3));
        }

        var blockedPopulation =
            Assert.Throws<PostgresException>(() =>
                meta.RaiseLeaderboardPopulationFloor(
                    "handoff-loss-song",
                    "Solo_Guitar",
                    25));
        Assert.Equal("55000", blockedPopulation.SqlState);

        await using (var recoveryLease =
                     await meta
                         .AcquireMaxScoreMaintenanceLeaseAsync(
                             publicationId)
                         .WaitAsync(TimeSpan.FromSeconds(5)))
        {
            await recoveryLease.VerifyHeldAsync(
                requireSourceLocks: true);
        }

        Assert.False(meta.AreRegistrationMutationsBlocked());
        meta.RaiseLeaderboardPopulationFloor(
            "handoff-loss-song",
            "Solo_Guitar",
            25);
        Assert.Equal(
            25,
            meta.GetLeaderboardPopulation(
                "handoff-loss-song",
                "Solo_Guitar"));
    }

    [Theory]
    [InlineData("path-batch-promotion")]
    [InlineData("rollback-checkpoint")]
    [InlineData("derived-ranking-stats")]
    [InlineData("notification-quarantine-alignment")]
    [InlineData("cache-staging")]
    public async Task Backend_loss_after_verification_rolls_back_representative_maintenance_commit(
        string operation)
    {
        using var dataSource =
            SharedPostgresContainer.CreateDatabase();
        const long scrapeId = 1397;
        const long publicationId = 597;
        var manifestDigest = new string('6', 64);
        var freezeReason =
            PublicReadFreezeState.MaxScoreMaintenanceReasonPrefix
            + manifestDigest;
        using (var connection = dataSource.OpenConnection())
        using (var seed = connection.CreateCommand())
        {
            seed.CommandText = """
                CREATE TABLE maintenance_fence_probe (
                    operation TEXT PRIMARY KEY,
                    created_at TIMESTAMPTZ NOT NULL DEFAULT now()
                );

                INSERT INTO scrape_log (
                    id, started_at, completed_at, status)
                VALUES (
                    @scrapeId,
                    now() - interval '1 hour',
                    now(),
                    'completed');
                INSERT INTO publication_generations (
                    publication_id, scrape_id, status, created_at)
                VALUES (
                    @publicationId,
                    @scrapeId,
                    'current',
                    now());
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
                    score_history_fingerprint,
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
                    1,
                    @scoreFingerprint,
                    @notificationFingerprint,
                    @rankFingerprint,
                    @rankFingerprint,
                    '{}'::jsonb,
                    @freezeReason,
                    'freeze_established',
                    'running');
                """;
            seed.Parameters.AddWithValue("scrapeId", scrapeId);
            seed.Parameters.AddWithValue(
                "publicationId",
                publicationId);
            seed.Parameters.AddWithValue(
                "freezeReason",
                freezeReason);
            seed.Parameters.AddWithValue(
                "manifestDigest",
                manifestDigest);
            seed.Parameters.AddWithValue(
                "planDigest",
                new string('7', 64));
            seed.Parameters.AddWithValue(
                "catalogHash",
                new string('8', 64));
            seed.Parameters.AddWithValue(
                "scoreFingerprint",
                new string('9', 64));
            seed.Parameters.AddWithValue(
                "notificationFingerprint",
                new string('a', 64));
            seed.Parameters.AddWithValue(
                "rankFingerprint",
                new string('b', 64));
            seed.ExecuteNonQuery();
        }

        using var meta = new MetaDatabase(
            dataSource,
            NullLogger<MetaDatabase>.Instance);
        var lostLease =
            await meta.AcquireMaxScoreMaintenanceLeaseAsync(
                publicationId);
        meta.MaxScoreMaintenanceBeforeCommitTestHook =
            context =>
            {
                Assert.Equal(operation, context.Operation);
                using var terminator =
                    dataSource.OpenConnection();
                using var terminate =
                    terminator.CreateCommand();
                terminate.CommandText =
                    "SELECT pg_terminate_backend(@backendProcessId)";
                terminate.Parameters.AddWithValue(
                    "backendProcessId",
                    context.BackendProcessId);
                Assert.True(terminate.ExecuteScalar() is true);
            };
        try
        {
            await Assert.ThrowsAsync<
                MaxScoreMaintenanceLeaseLostException>(
                () => lostLease.ExecuteTransactionAsync(
                    operation,
                    requireSourceLocks: true,
                    async (connection, transaction, token) =>
                    {
                        await using var mutation =
                            connection.CreateCommand();
                        mutation.Transaction = transaction;
                        mutation.CommandText = """
                            INSERT INTO maintenance_fence_probe (
                                operation)
                            VALUES (@operation);
                            UPDATE max_score_maintenance_runs
                            SET phase = 'rollback_captured',
                                promoted_song_count = 1,
                                updated_at = now()
                            WHERE manifest_sha256 =
                                @manifestDigest;
                            """;
                        mutation.Parameters.AddWithValue(
                            "operation",
                            operation);
                        mutation.Parameters.AddWithValue(
                            "manifestDigest",
                            manifestDigest);
                        await mutation.ExecuteNonQueryAsync(token);
                    }));
        }
        finally
        {
            meta.MaxScoreMaintenanceBeforeCommitTestHook =
                null;
        }
        await lostLease.DisposeAsync();

        using (var connection = dataSource.OpenConnection())
        using (var verify = connection.CreateCommand())
        {
            verify.CommandText = """
                SELECT
                    (
                        SELECT COUNT(*) = 0
                        FROM maintenance_fence_probe
                    ),
                    (
                        SELECT phase = 'freeze_established'
                           AND promoted_song_count = 0
                        FROM max_score_maintenance_runs
                        WHERE manifest_sha256 =
                            @manifestDigest
                    ),
                    public_reads_frozen,
                    max_score_mutation_gate_token IS NOT NULL
                FROM scrape_publication_state
                WHERE id = TRUE
                """;
            verify.Parameters.AddWithValue(
                "manifestDigest",
                manifestDigest);
            using var reader = verify.ExecuteReader();
            Assert.True(reader.Read());
            Assert.True(reader.GetBoolean(0));
            Assert.True(reader.GetBoolean(1));
            Assert.True(reader.GetBoolean(2));
            Assert.True(reader.GetBoolean(3));
        }

        await using (var resumedLease =
                     await meta
                         .AcquireMaxScoreMaintenanceLeaseAsync(
                             publicationId)
                         .WaitAsync(TimeSpan.FromSeconds(5)))
        {
            await resumedLease.ExecuteTransactionAsync(
                $"resume:{operation}",
                requireSourceLocks: true,
                async (connection, transaction, token) =>
                {
                    await using var resume =
                        connection.CreateCommand();
                    resume.Transaction = transaction;
                    resume.CommandText = """
                        INSERT INTO maintenance_fence_probe (
                            operation)
                        VALUES (@operation)
                        """;
                    resume.Parameters.AddWithValue(
                        "operation",
                        $"resume:{operation}");
                    await resume.ExecuteNonQueryAsync(token);
                });
        }

        using var resumedConnection =
            dataSource.OpenConnection();
        using var resumed = resumedConnection.CreateCommand();
        resumed.CommandText = """
            SELECT
                EXISTS (
                    SELECT 1
                    FROM maintenance_fence_probe
                    WHERE operation = @operation
                ),
                public_reads_frozen
            FROM scrape_publication_state
            WHERE id = TRUE
            """;
        resumed.Parameters.AddWithValue(
            "operation",
            $"resume:{operation}");
        using var resumedReader = resumed.ExecuteReader();
        Assert.True(resumedReader.Read());
        Assert.True(resumedReader.GetBoolean(0));
        Assert.True(resumedReader.GetBoolean(1));
    }

    [Fact]
    public async Task Lost_maintenance_backend_refuses_final_publish_and_unfreeze_until_new_lease_resumes()
    {
        using var dataSource =
            SharedPostgresContainer.CreateDatabase();
        const long scrapeId = 1297;
        const long publicationId = 501;
        var manifestDigest = new string('7', 64);
        SeedValidatedCompletionState(
            dataSource,
            scrapeId,
            publicationId,
            manifestDigest);
        using var meta = new MetaDatabase(
            dataSource,
            Microsoft.Extensions.Logging.Abstractions
                .NullLogger<MetaDatabase>.Instance);

        var lostLease =
            await meta.AcquireMaxScoreMaintenanceLeaseAsync(
                publicationId);
        meta.MaxScoreMaintenanceBeforeCommitTestHook =
            context =>
            {
                Assert.Equal(
                    "final-cache-publication-unfreeze",
                    context.Operation);
                using var terminator =
                    dataSource.OpenConnection();
                using var terminate =
                    terminator.CreateCommand();
                terminate.CommandText =
                    "SELECT pg_terminate_backend(@backendProcessId)";
                terminate.Parameters.AddWithValue(
                    "backendProcessId",
                    context.BackendProcessId);
                Assert.True(terminate.ExecuteScalar() is true);
            };
        try
        {
            await Assert.ThrowsAsync<
                MaxScoreMaintenanceLeaseLostException>(
                () => lostLease.CompleteAsync(
                    scrapeId,
                    manifestDigest));
        }
        finally
        {
            meta.MaxScoreMaintenanceBeforeCommitTestHook =
                null;
        }
        await lostLease.DisposeAsync();

        using (var failedConnection =
               dataSource.OpenConnection())
        using (var failed = failedConnection.CreateCommand())
        {
            failed.CommandText = """
                SELECT
                    public_reads_frozen,
                    max_score_mutation_gate_token IS NOT NULL,
                    (
                        SELECT phase = 'validated'
                           AND status = 'running'
                        FROM max_score_maintenance_runs
                        WHERE manifest_sha256 = @manifestDigest
                    ),
                    (
                        SELECT etag = 'old-etag'
                        FROM api_response_cache
                        WHERE cache_key = 'route'
                    )
                FROM scrape_publication_state
                WHERE id = TRUE
                """;
            failed.Parameters.AddWithValue(
                "manifestDigest",
                manifestDigest);
            using var reader = failed.ExecuteReader();
            Assert.True(reader.Read());
            Assert.True(reader.GetBoolean(0));
            Assert.True(reader.GetBoolean(1));
            Assert.True(reader.GetBoolean(2));
            Assert.True(reader.GetBoolean(3));
        }

        await using (var resumedLease =
                     await meta
                         .AcquireMaxScoreMaintenanceLeaseAsync(
                             publicationId)
                         .WaitAsync(TimeSpan.FromSeconds(5)))
        {
            await resumedLease.VerifyHeldAsync(
                requireSourceLocks: true);
            await resumedLease.CompleteAsync(
                scrapeId,
                manifestDigest);
        }

        using var verifyConnection =
            dataSource.OpenConnection();
        using var verify = verifyConnection.CreateCommand();
        verify.CommandText = """
            SELECT
                NOT public_reads_frozen,
                max_score_mutation_gate_token IS NULL,
                (
                    SELECT phase = 'completed'
                       AND status = 'completed'
                    FROM max_score_maintenance_runs
                    WHERE manifest_sha256 = @manifestDigest
                ),
                (
                    SELECT etag = 'new-etag'
                    FROM api_response_cache
                    WHERE cache_key = 'route'
                )
            FROM scrape_publication_state
            WHERE id = TRUE
            """;
        verify.Parameters.AddWithValue(
            "manifestDigest",
            manifestDigest);
        using var verifyReader = verify.ExecuteReader();
        Assert.True(verifyReader.Read());
        Assert.True(verifyReader.GetBoolean(0));
        Assert.True(verifyReader.GetBoolean(1));
        Assert.True(verifyReader.GetBoolean(2));
        Assert.True(verifyReader.GetBoolean(3));
    }

    [Fact]
    public async Task Four_instrument_notification_quarantine_preserves_marker_and_zero_visible_delivery()
    {
        using var dataSource = SharedPostgresContainer.CreateDatabase();
        var manifest = CreateFourInstrumentManifest();
        Assert.Equal(
            [
                "Solo_Guitar",
                "Solo_PeripheralGuitar",
                "Solo_PeripheralCymbals",
                "Solo_PeripheralDrums",
            ],
            manifest.Scope.ExpectedChangedInstruments);
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
                    score_history_fingerprint,
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

        await using var maintenanceLease =
            await AcquireMaintenanceLeaseAsync(
                dataSource,
                manifest.ExpectedPublicationId);
        var result = await service.QuarantineAndAlignAsync(
            manifest,
            manifestDigest,
            planDigest,
            inspection.PublishedScoreSourceFingerprint,
            maintenanceLease,
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

        await using var maintenanceLease =
            await AcquireMaintenanceLeaseAsync(
                dataSource,
                manifest.ExpectedPublicationId);
        var result = await service.QuarantineAndAlignAsync(
            manifest,
            manifestDigest,
            planDigest,
            inspection.PublishedScoreSourceFingerprint,
            maintenanceLease,
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

        await using var maintenanceLease =
            await AcquireMaintenanceLeaseAsync(
                dataSource,
                manifest.ExpectedPublicationId);
        var result = await service.QuarantineAndAlignAsync(
            manifest,
            manifestDigest,
            planDigest,
            inspection.PublishedScoreSourceFingerprint,
            maintenanceLease,
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

        await using var maintenanceLease =
            await AcquireMaintenanceLeaseAsync(
                dataSource,
                manifest.ExpectedPublicationId);
        var result = await service.QuarantineAndAlignAsync(
            manifest,
            manifestDigest,
            planDigest,
            inspection.PublishedScoreSourceFingerprint,
            maintenanceLease,
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

    private static async Task<IMaxScoreMaintenanceLease>
        AcquireMaintenanceLeaseAsync(
            NpgsqlDataSource dataSource,
            long publicationId)
    {
        var meta = new MetaDatabase(
            dataSource,
            NullLogger<MetaDatabase>.Instance);
        return await meta.AcquireMaxScoreMaintenanceLeaseAsync(
            publicationId);
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
                score_history_fingerprint,
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

    private static void SeedValidatedCompletionState(
        NpgsqlDataSource dataSource,
        long scrapeId,
        long publicationId,
        string manifestDigest)
    {
        var freezeReason =
            PublicReadFreezeState.MaxScoreMaintenanceReasonPrefix
            + manifestDigest;
        using var connection = dataSource.OpenConnection();
        using var seed = connection.CreateCommand();
        seed.CommandText = """
            INSERT INTO scrape_log (
                id, started_at, completed_at, status)
            VALUES (
                @scrapeId,
                now() - interval '1 hour',
                now(),
                'completed')
            ON CONFLICT (id) DO UPDATE SET
                completed_at = EXCLUDED.completed_at,
                status = EXCLUDED.status;

            INSERT INTO publication_generations (
                publication_id, scrape_id, status, created_at)
            VALUES (
                @publicationId,
                @scrapeId,
                'current',
                now())
            ON CONFLICT (publication_id) DO UPDATE SET
                scrape_id = EXCLUDED.scrape_id,
                status = EXCLUDED.status;

            INSERT INTO scrape_publication_state (
                id,
                current_publication_id,
                working_publication_id,
                published_scrape_id,
                public_reads_frozen,
                public_reads_frozen_at,
                public_reads_frozen_scrape_id,
                public_reads_frozen_reason,
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
                score_history_fingerprint,
                manifest_json,
                freeze_reason,
                phase,
                status,
                staged_cache_entry_count,
                staged_cache_evidence)
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
                @historyFingerprint,
                '{}'::jsonb,
                @freezeReason,
                'notifications_quarantined',
                'running',
                0,
                NULL);

            INSERT INTO api_response_cache (
                cache_key, json_data, etag, cached_at)
            VALUES (
                'route',
                decode('7b226f6c64223a747275657d', 'hex'),
                'old-etag',
                now())
            ON CONFLICT (cache_key) DO UPDATE SET
                json_data = EXCLUDED.json_data,
                etag = EXCLUDED.etag,
                cached_at = EXCLUDED.cached_at;

            INSERT INTO api_response_cache_staging (
                cache_key, json_data, etag, cached_at)
            VALUES (
                'route',
                decode('7b226e6577223a747275657d', 'hex'),
                'new-etag',
                now());

            INSERT INTO publication_api_response_cache_staging (
                publication_id,
                cache_key,
                json_data,
                etag,
                cached_at)
            VALUES (
                @publicationId,
                'route',
                decode('7b226e6577223a747275657d', 'hex'),
                'new-etag',
                now());

            INSERT INTO max_score_maintenance_cache_entries (
                manifest_sha256,
                cache_key,
                etag,
                json_sha256)
            VALUES (
                @manifestDigest,
                'route',
                'new-etag',
                encode(
                    digest(
                        decode(
                            '7b226e6577223a747275657d',
                            'hex'),
                        'sha256'),
                    'hex'));

            UPDATE max_score_maintenance_runs
            SET phase = 'validated',
                staged_cache_entry_count = 1,
                staged_cache_evidence =
                    jsonb_build_object(
                        'entryCount', 1,
                        'contentFingerprint',
                            repeat('d', 64),
                        'publishedScopeCacheKeyCount', 0,
                        'publishedScopeCacheKeyFingerprint',
                            repeat('e', 64),
                        'targetScopeCount', 0,
                        'targetScopeFingerprint',
                            repeat('f', 64),
                        'affectedAccountCount', 0,
                        'affectedAccountFingerprint',
                            repeat('1', 64),
                        'overlayOnlyAccountCount', 0,
                        'overlayOnlyAccountFingerprint',
                            repeat('2', 64))
            WHERE manifest_sha256 = @manifestDigest;
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
            new string('8', 64));
        seed.Parameters.AddWithValue(
            "catalogHash",
            new string('9', 64));
        seed.Parameters.AddWithValue(
            "scoreFingerprint",
            new string('a', 64));
        seed.Parameters.AddWithValue(
            "notificationFingerprint",
            new string('b', 64));
        seed.Parameters.AddWithValue(
            "historyFingerprint",
            new string('c', 64));
        seed.Parameters.AddWithValue(
            "freezeReason",
            freezeReason);
        seed.ExecuteNonQuery();
    }

    private static void SeedPublishedSoloCurrentState(
        NpgsqlDataSource dataSource,
        MaxScoreMaintenanceManifest manifest,
        int overlayScore)
    {
        ScrapeRunTestHelper.EnsureAllocated(
            dataSource,
            manifest.ExpectedPublishedScrapeId,
            completed: true);
        using var connection = dataSource.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE scrape_publication_state
            SET current_publication_id = NULL,
                previous_publication_id = NULL,
                working_publication_id = NULL
            WHERE id = TRUE;

            DELETE FROM publication_generations
            WHERE scrape_id = @scrapeId
              AND publication_id <> @publicationId;

            INSERT INTO publication_generations (
                publication_id,
                scrape_id,
                status,
                created_at,
                published_at)
            VALUES (
                @publicationId,
                @scrapeId,
                'current',
                now(),
                now())
            ON CONFLICT (publication_id) DO UPDATE SET
                scrape_id = EXCLUDED.scrape_id,
                status = EXCLUDED.status,
                published_at = EXCLUDED.published_at;

            INSERT INTO scrape_publication_state (
                id,
                current_publication_id,
                working_publication_id,
                published_scrape_id,
                published_at,
                updated_at)
            VALUES (
                TRUE,
                @publicationId,
                NULL,
                @scrapeId,
                now(),
                now())
            ON CONFLICT (id) DO UPDATE SET
                current_publication_id =
                    EXCLUDED.current_publication_id,
                working_publication_id = NULL,
                published_scrape_id =
                    EXCLUDED.published_scrape_id,
                published_at = EXCLUDED.published_at,
                updated_at = EXCLUDED.updated_at;

            INSERT INTO leaderboard_entries_snapshot (
                snapshot_id,
                song_id,
                instrument,
                account_id,
                score,
                rank,
                source,
                first_seen_at,
                last_updated_at)
            VALUES (
                @scrapeId,
                'song-a',
                'Solo_Guitar',
                'account-base',
                40000,
                1,
                'scrape',
                now(),
                now());

            INSERT INTO leaderboard_published_scope_source (
                published_scrape_id,
                song_id,
                instrument,
                scope_kind,
                source_kind,
                source_snapshot_id,
                source_scrape_id,
                row_count,
                content_fingerprint,
                coverage_fingerprint,
                reported_total_entries,
                reported_total_pages,
                is_complete,
                created_at,
                validated_at)
            VALUES (
                @scrapeId,
                'song-a',
                'Solo_Guitar',
                'alltime',
                'snapshot',
                @scrapeId,
                @scrapeId,
                1,
                md5('song-a'),
                md5('song-a:coverage'),
                1,
                1,
                TRUE,
                now(),
                now());

            INSERT INTO publication_surface_bindings (
                publication_id,
                surface_name,
                binding_kind,
                binding_json,
                row_count,
                content_hash,
                status,
                built_at)
            VALUES (
                @publicationId,
                'solo_scope_sources',
                'scrape_id',
                jsonb_build_object(
                    'publicationId', @publicationId,
                    'table',
                        'leaderboard_published_scope_source',
                    'publishedScrapeId', @scrapeId),
                (
                    SELECT COUNT(*)
                    FROM leaderboard_published_scope_source
                    WHERE published_scrape_id = @scrapeId
                      AND scope_kind = 'alltime'
                ),
                NULL,
                'ready',
                now())
            ON CONFLICT (
                publication_id,
                surface_name) DO UPDATE SET
                binding_kind = EXCLUDED.binding_kind,
                binding_json = EXCLUDED.binding_json,
                row_count = EXCLUDED.row_count,
                status = EXCLUDED.status,
                built_at = EXCLUDED.built_at;

            INSERT INTO leaderboard_entries_overlay (
                song_id,
                instrument,
                account_id,
                score,
                rank,
                source,
                first_seen_at,
                last_updated_at,
                source_priority,
                overlay_reason)
            VALUES (
                'song-a',
                'Solo_Guitar',
                'account-overlay',
                @overlayScore,
                1,
                'backfill',
                now(),
                now(),
                200,
                'max-score-test');

            INSERT INTO solo_current_projection_scope (
                song_id,
                instrument,
                projection_generation,
                row_count,
                source_snapshot_id,
                status,
                updated_at)
            VALUES (
                'song-a',
                'Solo_Guitar',
                1,
                1,
                @scrapeId,
                'ready',
                now());

            INSERT INTO current_leaderboard_entries (
                song_id,
                instrument,
                account_id,
                score,
                rank,
                source,
                first_seen_at,
                last_updated_at,
                projection_generation,
                computed_at)
            VALUES (
                'song-a',
                'Solo_Guitar',
                'account-base',
                40000,
                1,
                'projection',
                now(),
                now(),
                1,
                now());
            """;
        command.Parameters.AddWithValue(
            "scrapeId",
            manifest.ExpectedPublishedScrapeId);
        command.Parameters.AddWithValue(
            "publicationId",
            manifest.ExpectedPublicationId);
        command.Parameters.AddWithValue(
            "overlayScore",
            overlayScore);
        command.ExecuteNonQuery();
    }

    private static void ReplacePublishedResolvedScores(
        NpgsqlDataSource dataSource,
        MaxScoreMaintenanceManifest manifest,
        int[] scores)
    {
        using var connection = dataSource.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            DELETE FROM leaderboard_entries_snapshot
            WHERE snapshot_id = @scrapeId
              AND song_id = 'song-a'
              AND instrument = 'Solo_Guitar';

            DELETE FROM leaderboard_entries_overlay
            WHERE song_id = 'song-a'
              AND instrument = 'Solo_Guitar';

            INSERT INTO leaderboard_entries_snapshot (
                snapshot_id,
                song_id,
                instrument,
                account_id,
                score,
                rank,
                source,
                first_seen_at,
                last_updated_at)
            SELECT @scrapeId,
                   'song-a',
                   'Solo_Guitar',
                   'observed-account-' || score_row.ordinality,
                   score_row.score,
                   score_row.ordinality::INTEGER,
                   'scrape',
                   now(),
                   now()
            FROM unnest(@scores::INTEGER[])
                WITH ORDINALITY AS score_row(score, ordinality);

            UPDATE leaderboard_published_scope_source
            SET source_kind =
                    CASE
                        WHEN @scoreCount = 0 THEN 'empty'
                        ELSE 'snapshot'
                    END,
                source_snapshot_id =
                    CASE
                        WHEN @scoreCount = 0 THEN NULL
                        ELSE @scrapeId
                    END,
                row_count = @scoreCount,
                reported_total_entries = @scoreCount,
                reported_total_pages =
                    CASE
                        WHEN @scoreCount = 0 THEN 0
                        ELSE 1
                    END
            WHERE published_scrape_id = @scrapeId
              AND song_id = 'song-a'
              AND instrument = 'Solo_Guitar'
              AND scope_kind = 'alltime';
            """;
        command.Parameters.AddWithValue(
            "scrapeId",
            manifest.ExpectedPublishedScrapeId);
        command.Parameters.Add(
            "scores",
            NpgsqlTypes.NpgsqlDbType.Array
            | NpgsqlTypes.NpgsqlDbType.Integer).Value = scores;
        command.Parameters.AddWithValue(
            "scoreCount",
            scores.Length);
        command.ExecuteNonQuery();
    }

    private static async Task<
            MaxScoreMaintenanceObservedScoreCheck>
        LoadObservedScoreCheckAsync(
            NpgsqlDataSource dataSource,
            MaxScoreMaintenanceManifest manifest)
    {
        await using var connection =
            await dataSource.OpenConnectionAsync();
        await using var transaction =
            await connection.BeginTransactionAsync();
        return Assert.Single(
            await MaxScoreMaintenanceService
                .LoadObservedScoreChecksAsync(
                    manifest,
                    connection,
                    transaction,
                    CancellationToken.None));
    }

    private static string CreateEvidenceDirectory()
    {
        var path = Path.Combine(
            Directory.GetCurrentDirectory(),
            ".test-temp",
            $"max-score-evidence-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
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

    private static MaxScoreMaintenanceManifest
        CreateFourInstrumentManifest()
    {
        var template = CreateManifest();
        var runtime = new PathGenerationRuntimeIdentity(
            PathGenerationProfiles.PlasticDrumsV4ChoptVersion,
            PathGenerationProfiles.PlasticDrumsV4BinarySha256,
            PathGenerationProfiles.PlasticDrumsV4);
        var song = template.Songs[0];
        var current = song.CurrentPath with
        {
            GenerationProfile =
                "chopt-fnf-ew0-s20-json-png-v2",
        };
        var staged = song.StagedPath with
        {
            ChoptVersion = runtime.Version,
            ChoptBinarySha256 = runtime.BinarySha256,
            GenerationProfile = runtime.Profile,
            ExpectedInstruments =
                MaxScoreMaintenanceManifest.AllInstruments,
            Maxima = current.Maxima with
            {
                Lead = 51_573,
                ProLead = 51_573,
                ProCymbals = 60_000,
                ProDrums = 58_000,
            },
            ArtifactTreeSha256 = new string('7', 64),
            ArtifactFileCount = 65,
        };
        var changed = new[]
        {
            "Solo_Guitar",
            "Solo_PeripheralGuitar",
            "Solo_PeripheralCymbals",
            "Solo_PeripheralDrums",
        };
        return (template with
        {
            Scope = new MaxScoreMaintenanceScope(
                MaxScoreMaintenanceStagePurposes.Promotion,
                new string('6', 64),
                MaxScoreMaintenanceManifest.AllInstruments,
                changed),
            Runtime = runtime,
            Songs =
            [
                new MaxScoreMaintenanceManifestSong(
                    song.SongId,
                    song.ExpectedCatalogLastModified,
                    current,
                    staged,
                    changed,
                    new MaxScoreMaintenancePlasticDrumsEvidence(
                        2,
                        2,
                        new string('1', 64),
                        new string('2', 64),
                        new string('3', 64))),
            ],
        }).ValidateAndNormalize();
    }

    private static MaxScoreMaintenanceManifest CreateManifest()
    {
        var runtime = new PathGenerationRuntimeIdentity(
            "1.16.3",
            new string('a', 64),
            "profile-v3");
        var current = new MaxScoreMaintenancePathIdentity(
            Revision: 0,
            DatFileHash: new string('c', 64),
            SongLastModified: "2026-08-01T00:00:00Z",
            GeneratedAtUtc: new DateTime(
                2026,
                8,
                1,
                0,
                0,
                0,
                DateTimeKind.Utc),
            ChoptVersion: "1.16.2",
            ChoptBinarySha256: new string('d', 64),
            GenerationProfile: "profile-v2",
            ArtifactGenerationId: "generation-current",
            ExpectedInstruments:
            [
                "Solo_Bass",
                "Solo_Drums",
                "Solo_Vocals",
                "Solo_PeripheralBass",
            ],
            Maxima: new MaxScoreMaintenanceMaxima(
                null,
                25_000,
                30_000,
                40_000,
                null,
                60_000,
                null,
                null),
            PathGenerationPending: false,
            ArtifactTreeSha256: new string('e', 64),
            ArtifactFileCount: 33);
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
            ExpectedInstruments:
            [
                "Solo_Guitar",
                "Solo_Bass",
                "Solo_Drums",
                "Solo_Vocals",
                "Solo_PeripheralBass",
            ],
            Maxima: current.Maxima with { Lead = 51_573 },
            PathGenerationPending: false,
            ArtifactTreeSha256: new string('f', 64),
            ArtifactFileCount: 41);
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
            Scope: new MaxScoreMaintenanceScope(
                MaxScoreMaintenanceStagePurposes.Promotion,
                new string('8', 64),
                [
                    "Solo_Guitar",
                    "Solo_Bass",
                    "Solo_Drums",
                    "Solo_Vocals",
                    "Solo_PeripheralBass",
                ],
                ["Solo_Guitar"]),
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
