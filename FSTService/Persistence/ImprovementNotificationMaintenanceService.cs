using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Npgsql;
using NpgsqlTypes;

namespace FSTService.Persistence;

public sealed class ImprovementNotificationMaintenanceService
{
    private const int CommandTimeoutSeconds = 600;
    private const string ProLeadInstrument = "Solo_PeripheralGuitar";
    private const string MaxScorePercentRankMetric = "max_score_percent_rank";

    private readonly NpgsqlDataSource _dataSource;
    private readonly ILogger<ImprovementNotificationMaintenanceService> _log;
    private readonly bool _registeredOnly;

    public ImprovementNotificationMaintenanceService(
        NpgsqlDataSource dataSource,
        ILogger<ImprovementNotificationMaintenanceService> log,
        IOptions<ImprovementNotificationOptions>? options = null)
    {
        _dataSource = dataSource;
        _log = log;
        _registeredOnly =
            options is not null &&
            !string.Equals(
                options.Value.Scope,
                "all",
                StringComparison.OrdinalIgnoreCase);
    }

    public async Task<ImprovementNotificationMaintenanceDryRunReport>
        DryRunProLeadMaxScoreRepairAsync(
            long expectedPublishedScrapeId,
            ImprovementNotificationMaintenanceManifest manifest,
            CancellationToken ct = default)
    {
        ValidateExpectedPublishedScrapeId(expectedPublishedScrapeId);
        var normalizedManifest = (manifest
            ?? throw new ArgumentNullException(nameof(manifest)))
            .ValidateAndNormalize();

        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(
            IsolationLevel.RepeatableRead,
            ct);
        await using (var readOnly = conn.CreateCommand())
        {
            readOnly.Transaction = tx;
            readOnly.CommandText = "SET TRANSACTION READ ONLY;";
            await readOnly.ExecuteNonQueryAsync(ct);
        }

        await ValidateInputsAsync(
            conn,
            tx,
            expectedPublishedScrapeId,
            ct);
        var totalChartedSongs = await ValidateManifestSongIdentitiesAsync(
            conn,
            tx,
            expectedPublishedScrapeId,
            normalizedManifest,
            ManifestIdentityPhase.PreRepair,
            ct);
        var report = await BuildDryRunReportAsync(
            conn,
            tx,
            expectedPublishedScrapeId,
            normalizedManifest,
            totalChartedSongs,
            useProjectedProLeadRankings: true,
            ct);
        await tx.CommitAsync(ct);
        return report;
    }

    public async Task<ImprovementNotificationMaintenanceExecuteReport>
        ExecuteProLeadMaxScoreRepairAsync(
            long expectedPublishedScrapeId,
            string expectedDryRunDigest,
            ImprovementNotificationMaintenanceManifest manifest,
            CancellationToken ct = default)
    {
        ValidateExpectedPublishedScrapeId(expectedPublishedScrapeId);
        var normalizedExpectedDigest = NormalizeDigest(expectedDryRunDigest);
        var normalizedManifest = (manifest
            ?? throw new ArgumentNullException(nameof(manifest)))
            .ValidateAndNormalize();

        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(
            IsolationLevel.RepeatableRead,
            ct);
        await LockExpectedPublicationAsync(
            conn,
            tx,
            expectedPublishedScrapeId,
            ct);
        await ValidateInputsAsync(
            conn,
            tx,
            expectedPublishedScrapeId,
            ct);
        var totalChartedSongs = await ValidateManifestSongIdentitiesAsync(
            conn,
            tx,
            expectedPublishedScrapeId,
            normalizedManifest,
            ManifestIdentityPhase.PostRepair,
            ct);

        var projectedDryRun = await BuildDryRunReportAsync(
            conn,
            tx,
            expectedPublishedScrapeId,
            normalizedManifest,
            totalChartedSongs,
            useProjectedProLeadRankings: true,
            ct);
        if (!projectedDryRun.DryRunDigest.Equals(
                normalizedExpectedDigest,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Maintenance dry-run digest changed from expected " +
                $"{normalizedExpectedDigest} to {projectedDryRun.DryRunDigest}; " +
                "no rows were written.");
        }

        var actualCandidates = await LoadCandidatesAsync(
            conn,
            tx,
            normalizedManifest,
            totalChartedSongs,
            useProjectedProLeadRankings: false,
            ct);
        if (!projectedDryRun.Candidates.SequenceEqual(actualCandidates))
        {
            throw new InvalidOperationException(
                "Actual account_rankings notification candidate set does not " +
                "exactly match the staged manifest projection; no rows were written.");
        }

        if (projectedDryRun.RejectedCandidateCount > 0)
        {
            throw new InvalidOperationException(
                $"Maintenance safety gate rejected " +
                $"{projectedDryRun.RejectedCandidateCount:N0} " +
                "candidate(s); no rows were written.");
        }

        var expectedBaselineRows = await ValidateBaselineTargetsAsync(
            conn,
            tx,
            projectedDryRun.Candidates,
            ct);
        var canonicalCandidateData = BuildCanonicalCandidateData(
            expectedPublishedScrapeId,
            normalizedManifest,
            totalChartedSongs,
            projectedDryRun.Candidates);
        var maintenanceRunId = await InsertMaintenanceRunAsync(
            conn,
            tx,
            projectedDryRun,
            canonicalCandidateData,
            ct);
        await InsertQuarantinedCandidatesAsync(
            conn,
            tx,
            maintenanceRunId,
            projectedDryRun.Candidates
                .Where(candidate => candidate.Allowed)
                .ToArray(),
            ct);
        var rankStateRowsUpdated = await BaselineAllowedProLeadRankStateAsync(
            conn,
            tx,
            projectedDryRun.Candidates,
            ct);
        if (rankStateRowsUpdated != expectedBaselineRows)
        {
            throw new InvalidOperationException(
                "Selective Pro Lead rank-state baseline changed while maintenance " +
                "was executing; all writes were rolled back.");
        }
        await UpdateMaintenanceRunStateCountAsync(
            conn,
            tx,
            maintenanceRunId,
            rankStateRowsUpdated,
            ct);

        await tx.CommitAsync(ct);

        _log.LogInformation(
            "Persisted notification maintenance quarantine for purpose {Purpose}, " +
            "published scrape {ScrapeId}, digest {Digest}: candidates={CandidateCount:N0}, " +
            "quarantined={QuarantinedCount:N0}, visible=0, " +
            "Pro Lead rank-state rows={StateRows:N0}.",
            ImprovementNotificationSafetyContract.ProLeadMaxScoreRepairPurpose,
            expectedPublishedScrapeId,
            projectedDryRun.DryRunDigest,
            projectedDryRun.CandidateCount,
            projectedDryRun.AllowedCandidateCount,
            rankStateRowsUpdated);

        return new ImprovementNotificationMaintenanceExecuteReport(
            Purpose: ImprovementNotificationSafetyContract.ProLeadMaxScoreRepairPurpose,
            Cause: ImprovementNotificationSafetyContract.MaxScoreRecomputeCause,
            DeliveryState: ImprovementNotificationSafetyContract.QuarantinedDeliveryState,
            PublishedScrapeId: expectedPublishedScrapeId,
            DryRunDigest: projectedDryRun.DryRunDigest,
            TotalChartedSongs: totalChartedSongs,
            CandidateCount: projectedDryRun.CandidateCount,
            ExternalRoutineCandidateCount:
                projectedDryRun.ExternalRoutineCandidateCount,
            QuarantinedCandidateCount: projectedDryRun.AllowedCandidateCount,
            SelectivePlayerRankStateRowsUpdated: rankStateRowsUpdated,
            VisibleDeliveryCap:
                ImprovementNotificationSafetyContract.ProLeadMaxScoreRepairVisibleDeliveryCap,
            VisibleDeliveryCount: 0,
            BroadcastRequested: false);
    }

    private static async Task LockExpectedPublicationAsync(
        NpgsqlConnection conn,
        NpgsqlTransaction tx,
        long expectedPublishedScrapeId,
        CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandTimeout = CommandTimeoutSeconds;
        cmd.CommandText = """
            SELECT published_scrape_id
            FROM scrape_publication_state
            WHERE id = TRUE
            FOR SHARE;
            """;
        var current = await cmd.ExecuteScalarAsync(ct);
        if (current is not int publishedScrapeId
            || publishedScrapeId != expectedPublishedScrapeId)
        {
            throw new InvalidOperationException(
                $"Published scrape changed from expected {expectedPublishedScrapeId} " +
                $"to {current?.ToString() ?? "null"}; no rows were written.");
        }

        await using var rankingLock = conn.CreateCommand();
        rankingLock.Transaction = tx;
        rankingLock.CommandTimeout = CommandTimeoutSeconds;
        rankingLock.CommandText = """
            LOCK TABLE account_rankings_pro_guitar IN SHARE MODE;
            LOCK TABLE song_stats_pro_guitar IN SHARE MODE;
            """;
        await rankingLock.ExecuteNonQueryAsync(ct);
    }

    private async Task<ImprovementNotificationMaintenanceDryRunReport>
        BuildDryRunReportAsync(
            NpgsqlConnection conn,
            NpgsqlTransaction tx,
            long expectedPublishedScrapeId,
            ImprovementNotificationMaintenanceManifest manifest,
            int totalChartedSongs,
            bool useProjectedProLeadRankings,
            CancellationToken ct)
    {
        var candidates = await LoadCandidatesAsync(
            conn,
            tx,
            manifest,
            totalChartedSongs,
            useProjectedProLeadRankings,
            ct);
        var canonicalCandidateData = BuildCanonicalCandidateData(
            expectedPublishedScrapeId,
            manifest,
            totalChartedSongs,
            candidates);
        var digest = Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes(canonicalCandidateData)));
        var classificationCounts = candidates
            .GroupBy(candidate => candidate.Classification, StringComparer.Ordinal)
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .Select(group => new ImprovementNotificationMaintenanceClassificationCount(
                group.Key,
                group.LongCount(),
                group.All(candidate => candidate.Allowed),
                group.Any(candidate => candidate.BlocksMaintenance)))
            .ToArray();
        var subjectMaxima = candidates
            .GroupBy(candidate => new
            {
                candidate.SubjectType,
                candidate.SubjectKey,
                candidate.Instrument,
            })
            .OrderBy(group => group.Key.SubjectType, StringComparer.Ordinal)
            .ThenBy(group => group.Key.SubjectKey, StringComparer.Ordinal)
            .ThenBy(group => group.Key.Instrument, StringComparer.Ordinal)
            .Select(group =>
            {
                var numericDeltas = group
                    .Where(candidate =>
                        candidate.OldNumeric.HasValue
                        && candidate.NewNumeric.HasValue)
                    .Select(candidate =>
                        Math.Abs(candidate.NewNumeric!.Value - candidate.OldNumeric!.Value))
                    .ToArray();
                var rankMovements = group
                    .Where(candidate =>
                        candidate.OldRank.HasValue
                        && candidate.NewRank.HasValue)
                    .Select(candidate =>
                        Math.Abs(candidate.NewRank!.Value - candidate.OldRank!.Value))
                    .ToArray();
                var rankImprovements = group
                    .Where(candidate =>
                        candidate.OldRank.HasValue
                        && candidate.NewRank.HasValue)
                    .Select(candidate =>
                        Math.Max(0, candidate.OldRank!.Value - candidate.NewRank!.Value))
                    .ToArray();
                return new ImprovementNotificationMaintenanceSubjectMaximum(
                    group.Key.SubjectType,
                    group.Key.SubjectKey,
                    group.Key.Instrument,
                    group.LongCount(),
                    numericDeltas.Length == 0 ? null : numericDeltas.Max(),
                    rankMovements.Length == 0 ? null : rankMovements.Max(),
                    rankImprovements.Length == 0 ? null : rankImprovements.Max());
            })
            .ToArray();
        var allowedCount = candidates.LongCount(candidate => candidate.Allowed);
        var externalRoutineCount = candidates.LongCount(candidate =>
            !candidate.Allowed && !candidate.BlocksMaintenance);
        var rejectedCount = candidates.LongCount(candidate => candidate.BlocksMaintenance);

        return new ImprovementNotificationMaintenanceDryRunReport(
            Purpose: ImprovementNotificationSafetyContract.ProLeadMaxScoreRepairPurpose,
            Cause: ImprovementNotificationSafetyContract.MaxScoreRecomputeCause,
            DeliveryState: ImprovementNotificationSafetyContract.QuarantinedDeliveryState,
            PublishedScrapeId: expectedPublishedScrapeId,
            Manifest: manifest,
            TotalChartedSongs: totalChartedSongs,
            DryRunDigest: digest,
            CandidateCount: candidates.LongCount(),
            AllowedCandidateCount: allowedCount,
            ExternalRoutineCandidateCount: externalRoutineCount,
            RejectedCandidateCount: rejectedCount,
            ClassificationCounts: classificationCounts,
            PerSubjectMaxima: subjectMaxima,
            MaxCandidatesForAnySubject:
                subjectMaxima.Length == 0
                    ? 0
                    : subjectMaxima.Max(subject => subject.CandidateCount),
            VisibleDeliveryCap:
                ImprovementNotificationSafetyContract.ProLeadMaxScoreRepairVisibleDeliveryCap,
            VisibleDeliveryCount: 0,
            QuarantineCandidateCount: rejectedCount == 0 ? allowedCount : 0,
            CapDecision: rejectedCount == 0
                ? "quarantine_only_zero_visible_delivery"
                : "blocked_disallowed_candidates",
            Candidates: candidates);
    }

    private async Task<IReadOnlyList<
        ImprovementNotificationMaintenanceCandidate>> LoadCandidatesAsync(
            NpgsqlConnection conn,
            NpgsqlTransaction tx,
            ImprovementNotificationMaintenanceManifest manifest,
            int totalChartedSongs,
            bool useProjectedProLeadRankings,
            CancellationToken ct)
    {
        var rawCandidates = new List<RawMaintenanceCandidate>();
        if (useProjectedProLeadRankings)
        {
            await AddProjectedProLeadRankCandidatesAsync(
                conn,
                tx,
                manifest,
                totalChartedSongs,
                rawCandidates,
                _registeredOnly,
                ct);
        }

        await AddCandidatesAsync(
            conn,
            tx,
            PlayerRankCandidatesSql,
            rawCandidates,
            ct,
            _registeredOnly,
            includeProLeadRankCandidates: !useProjectedProLeadRankings);
        await AddCandidatesAsync(
            conn,
            tx,
            PlayerSongCandidatesSql,
            rawCandidates,
            ct,
            _registeredOnly);
        await AddCandidatesAsync(
            conn,
            tx,
            BandSongCandidatesSql,
            rawCandidates,
            ct,
            _registeredOnly);
        await AddCandidatesAsync(
            conn,
            tx,
            BandRankCandidatesSql,
            rawCandidates,
            ct,
            _registeredOnly);

        return ClassifyCandidates(rawCandidates);
    }

    private static async Task ValidateInputsAsync(
        NpgsqlConnection conn,
        NpgsqlTransaction tx,
        long expectedPublishedScrapeId,
        CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandTimeout = CommandTimeoutSeconds;
        cmd.CommandText = """
            SELECT publication.published_scrape_id,
                   publication.public_reads_frozen,
                   publication.improvement_notifications_scrape_id,
                   publication.improvement_notifications_status,
                   scrape.status,
                   EXISTS (
                       SELECT 1
                       FROM improvement_detection_runs run
                       WHERE run.published_scrape_id = publication.published_scrape_id
                         AND run.status = 'completed'
                         AND run.mode = 'execute'
                         AND NOT run.baseline_only
                         AND run.include_players
                         AND run.include_song_events
                         AND run.include_rankings
                         AND run.notification_purpose = 'routine_score_observation_v1'
                         AND run.delivery_state = 'visible'
                   ) AS player_inputs_ready,
                   EXISTS (
                       SELECT 1
                       FROM improvement_detection_runs run
                       WHERE run.published_scrape_id = publication.published_scrape_id
                         AND run.status = 'completed'
                         AND run.mode = 'execute'
                         AND NOT run.baseline_only
                         AND run.include_bands
                         AND run.include_song_events
                         AND run.include_rankings
                         AND run.notification_purpose = 'routine_score_observation_v1'
                         AND run.delivery_state = 'visible'
                   ) AS band_inputs_ready
            FROM scrape_publication_state publication
            LEFT JOIN scrape_log scrape
              ON scrape.id = publication.published_scrape_id
            WHERE publication.id = TRUE;
            """;

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct) || reader.IsDBNull(0))
        {
            throw new InvalidOperationException(
                "Maintenance notification dry run requires a published scrape.");
        }

        var publishedScrapeId = reader.GetInt32(0);
        if (publishedScrapeId != expectedPublishedScrapeId)
        {
            throw new InvalidOperationException(
                $"Published scrape changed from expected {expectedPublishedScrapeId} " +
                $"to {publishedScrapeId}; no rows were written.");
        }

        if (reader.GetBoolean(1))
        {
            throw new InvalidOperationException(
                "Maintenance notification dry run is blocked while public reads are frozen.");
        }

        int? markerScrapeId = reader.IsDBNull(2) ? null : reader.GetInt32(2);
        var markerStatus = reader.IsDBNull(3) ? null : reader.GetString(3);
        if (markerScrapeId != publishedScrapeId || markerStatus != "completed")
        {
            throw new InvalidOperationException(
                $"Maintenance notification inputs are stale: notification marker " +
                $"{markerScrapeId?.ToString() ?? "null"}/{markerStatus ?? "null"} " +
                $"does not complete published scrape {publishedScrapeId}.");
        }

        if (reader.IsDBNull(4) || reader.GetString(4) != "completed")
        {
            throw new InvalidOperationException(
                $"Published scrape {publishedScrapeId} is missing a completed scrape ledger row.");
        }

        if (!reader.GetBoolean(5) || !reader.GetBoolean(6))
        {
            throw new InvalidOperationException(
                $"Maintenance notification inputs are stale for published scrape " +
                $"{publishedScrapeId}: completed routine player and band song/rank " +
                "detection runs are required.");
        }
    }

    private static async Task<int> ValidateManifestSongIdentitiesAsync(
        NpgsqlConnection conn,
        NpgsqlTransaction tx,
        long expectedPublishedScrapeId,
        ImprovementNotificationMaintenanceManifest manifest,
        ManifestIdentityPhase phase,
        CancellationToken ct)
    {
        var songIds = manifest.Songs
            .Select(static song => song.SongId)
            .ToArray();

        await using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandTimeout = CommandTimeoutSeconds;
            cmd.CommandText = $$"""
                WITH published_catalog_songs AS (
                    SELECT catalog_song.value AS song
                    FROM publication_generations generation
                    JOIN publication_song_catalog catalog
                      ON catalog.publication_id = generation.publication_id
                    JOIN publication_surface_bindings binding
                      ON binding.publication_id = generation.publication_id
                     AND binding.surface_name = 'song_catalog'
                    CROSS JOIN LATERAL jsonb_array_elements(
                        catalog.catalog_json -> 'songs'
                    ) catalog_song(value)
                    WHERE generation.scrape_id = @publishedScrapeId
                      AND catalog.is_exact
                      AND catalog.source_kind = 'provider_exact'
                      AND catalog.schema_version = @catalogSchemaVersion
                      AND binding.binding_kind =
                          'generation_catalog_snapshot'
                      AND binding.status = 'ready'
                      AND binding.row_count = catalog.song_count
                      AND binding.content_hash = catalog.content_hash
                )
                SELECT song.song_id,
                       song.path_generation_revision,
                       song.last_modified,
                       song.max_pro_lead_score,
                       stats.max_score,
                       song.path_artifact_generation_id,
                       song.dat_file_hash,
                       song.song_last_modified,
                       song.chopt_version,
                       song.chopt_binary_sha256,
                       song.path_generation_profile,
                       COALESCE(song.path_expected_instruments, ARRAY[]::TEXT[]),
                       song.path_generation_pending,
                       song.pro_lead_diff,
                       catalog.song ->> 'lastModified'
                FROM songs song
                JOIN published_catalog_songs catalog
                  ON catalog.song #>> '{track,su}' = song.song_id
                LEFT JOIN song_stats stats
                  ON stats.song_id = song.song_id
                 AND stats.instrument = 'Solo_PeripheralGuitar'
                WHERE song.song_id = ANY(@songIds)
                ORDER BY song.song_id
                {{(phase == ManifestIdentityPhase.PostRepair
                    ? "FOR SHARE OF song"
                    : string.Empty)}};
                """;
            cmd.Parameters.Add(
                "songIds",
                NpgsqlDbType.Array | NpgsqlDbType.Text).Value = songIds;
            cmd.Parameters.AddWithValue(
                "publishedScrapeId",
                expectedPublishedScrapeId);
            cmd.Parameters.AddWithValue(
                "catalogSchemaVersion",
                SongCatalogSnapshotBuilder.SchemaVersion);

            await using var reader = await cmd.ExecuteReaderAsync(ct);
            var index = 0;
            while (await reader.ReadAsync(ct))
            {
                if (index >= manifest.Songs.Count)
                {
                    throw new InvalidOperationException(
                        "Notification maintenance manifest resolved more than " +
                        "the required four database songs.");
                }

                var expected = manifest.Songs[index];
                var actualSongId = reader.GetString(0);
                if (!actualSongId.Equals(expected.SongId, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        "Notification maintenance manifest song set does not " +
                        "exactly match the database song set.");
                }

                var actualRevision = reader.GetInt64(1);
                var expectedRevision = phase == ManifestIdentityPhase.PreRepair
                    ? expected.ExpectedCurrentPathRevision
                    : checked(expected.ExpectedCurrentPathRevision + 1);
                var actualCatalogLastModified =
                    reader.IsDBNull(2) ? null : reader.GetString(2);
                int? actualSongMaximum =
                    reader.IsDBNull(3) ? null : reader.GetInt32(3);
                int? actualStatsMaximum =
                    reader.IsDBNull(4) ? null : reader.GetInt32(4);
                int? expectedMaximum = phase == ManifestIdentityPhase.PreRepair
                    ? expected.CurrentOldProLeadMaxScore
                    : expected.ProposedProLeadMaxScore;

                if (actualRevision != expectedRevision
                    || !ProviderTimestampIdentity.Equivalent(
                        actualCatalogLastModified,
                        expected.ExpectedCatalogLastModified)
                    || actualSongMaximum != expectedMaximum
                    || actualStatsMaximum != expectedMaximum
                    || reader.IsDBNull(13)
                    || reader.GetInt32(13) < 0
                    || reader.GetInt32(13) == 99
                    || reader.IsDBNull(14)
                    || !ProviderTimestampIdentity.Equivalent(
                        reader.GetString(14),
                        expected.ExpectedCatalogLastModified))
                {
                    throw new InvalidOperationException(
                        $"Notification maintenance database identity mismatch " +
                        $"for manifest song {expected.SongId}; no rows were written.");
                }

                if (phase == ManifestIdentityPhase.PostRepair)
                {
                    var actualGenerationId =
                        reader.IsDBNull(5) ? null : reader.GetString(5);
                    var actualDatHash =
                        reader.IsDBNull(6) ? null : reader.GetString(6);
                    var actualSongLastModified =
                        reader.IsDBNull(7) ? null : reader.GetString(7);
                    var actualChoptVersion =
                        reader.IsDBNull(8) ? null : reader.GetString(8);
                    var actualBinaryHash =
                        reader.IsDBNull(9) ? null : reader.GetString(9);
                    var actualProfile =
                        reader.IsDBNull(10) ? null : reader.GetString(10);
                    var expectedInstruments =
                        reader.GetFieldValue<string[]>(11);
                    var pending = reader.GetBoolean(12);

                    if (!string.Equals(
                            actualGenerationId,
                            expected.StagedArtifactGenerationId,
                            StringComparison.Ordinal)
                        || !string.Equals(
                            actualDatHash,
                            expected.StagedDatFileHash,
                            StringComparison.Ordinal)
                        || !ProviderTimestampIdentity.Equivalent(
                            actualSongLastModified,
                            expected.ExpectedCatalogLastModified)
                        || pending
                        || !expectedInstruments.Contains(
                            ProLeadInstrument,
                            StringComparer.Ordinal)
                        || !OptionalIdentityMatches(
                            actualChoptVersion,
                            expected.StagedChoptVersion)
                        || !OptionalIdentityMatches(
                            actualBinaryHash,
                            expected.StagedChoptBinarySha256)
                        || !OptionalIdentityMatches(
                            actualProfile,
                            expected.StagedGenerationProfile))
                    {
                        throw new InvalidOperationException(
                            $"Promoted path identity mismatch for manifest song " +
                            $"{expected.SongId}; no rows were written.");
                    }
                }

                index++;
            }

            if (index != ImprovementNotificationMaintenanceManifest.RequiredSongCount)
            {
                throw new InvalidOperationException(
                    "Notification maintenance manifest must resolve exactly four " +
                    "existing database songs.");
            }
        }

        if (phase == ManifestIdentityPhase.PreRepair)
        {
            await using var stagedIdentity = conn.CreateCommand();
            stagedIdentity.Transaction = tx;
            stagedIdentity.CommandTimeout = CommandTimeoutSeconds;
            stagedIdentity.CommandText = """
                SELECT COUNT(*)
                FROM songs
                WHERE path_artifact_generation_id = ANY(@generationIds);
                """;
            stagedIdentity.Parameters.Add(
                "generationIds",
                NpgsqlDbType.Array | NpgsqlDbType.Text).Value =
                manifest.Songs
                    .Select(static song => song.StagedArtifactGenerationId)
                    .ToArray();
            var activeGenerationCount = Convert.ToInt32(
                await stagedIdentity.ExecuteScalarAsync(ct));
            if (activeGenerationCount != 0)
            {
                throw new InvalidOperationException(
                    "One or more staged artifact generation IDs are already " +
                    "active in the database; no rows were written.");
            }
        }

        await using var totalCharted = conn.CreateCommand();
        totalCharted.Transaction = tx;
        totalCharted.CommandTimeout = CommandTimeoutSeconds;
        totalCharted.CommandText = """
            WITH published_catalog_songs AS (
                SELECT catalog_song.value AS song
                FROM publication_generations generation
                JOIN publication_song_catalog catalog
                  ON catalog.publication_id = generation.publication_id
                JOIN publication_surface_bindings binding
                  ON binding.publication_id = generation.publication_id
                 AND binding.surface_name = 'song_catalog'
                CROSS JOIN LATERAL jsonb_array_elements(
                    catalog.catalog_json -> 'songs'
                ) catalog_song(value)
                WHERE generation.scrape_id = @publishedScrapeId
                  AND catalog.is_exact
                  AND catalog.source_kind = 'provider_exact'
                  AND catalog.schema_version = @catalogSchemaVersion
                  AND binding.binding_kind = 'generation_catalog_snapshot'
                  AND binding.status = 'ready'
                  AND binding.row_count = catalog.song_count
                  AND binding.content_hash = catalog.content_hash
            )
            SELECT MIN(total_charted_songs),
                   MAX(total_charted_songs),
                   COUNT(*)::INTEGER,
                   (
                       SELECT COUNT(*)::INTEGER
                       FROM published_catalog_songs catalog
                       WHERE jsonb_typeof(
                           catalog.song #> '{track,in,pg}'
                       ) = 'number'
                         AND (
                             catalog.song #>> '{track,in,pg}'
                         )::INTEGER >= 0
                         AND (
                             catalog.song #>> '{track,in,pg}'
                         )::INTEGER <> 99
                   ) AS catalog_total
            FROM account_rankings
            WHERE instrument = 'Solo_PeripheralGuitar';
            """;
        totalCharted.Parameters.AddWithValue(
            "publishedScrapeId",
            expectedPublishedScrapeId);
        totalCharted.Parameters.AddWithValue(
            "catalogSchemaVersion",
            SongCatalogSnapshotBuilder.SchemaVersion);
        await using var totalReader = await totalCharted.ExecuteReaderAsync(ct);
        if (!await totalReader.ReadAsync(ct)
            || totalReader.GetInt32(2) <= 0
            || totalReader.IsDBNull(0)
            || totalReader.IsDBNull(1))
        {
            throw new InvalidOperationException(
                "Notification maintenance projection requires current Pro Lead " +
                "account rankings.");
        }

        var minimumRankingTotal = totalReader.GetInt32(0);
        var maximumRankingTotal = totalReader.GetInt32(1);
        var catalogTotal = totalReader.GetInt32(3);
        if (minimumRankingTotal <= 0
            || minimumRankingTotal != maximumRankingTotal
            || minimumRankingTotal != catalogTotal)
        {
            throw new InvalidOperationException(
                "Current Pro Lead account-ranking total-charted identity does not " +
                "match the song catalog; no rows were written.");
        }

        return minimumRankingTotal;
    }

    private static bool OptionalIdentityMatches(
        string? actual,
        string? expected)
        => expected is null
            || string.Equals(actual, expected, StringComparison.Ordinal);

    private static async Task AddProjectedProLeadRankCandidatesAsync(
        NpgsqlConnection conn,
        NpgsqlTransaction tx,
        ImprovementNotificationMaintenanceManifest manifest,
        int totalChartedSongs,
        List<RawMaintenanceCandidate> candidates,
        bool registeredOnly,
        CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandTimeout = CommandTimeoutSeconds;
        cmd.CommandText = ProjectedProLeadRankCandidatesSql;
        cmd.Parameters.Add(
            "songIds",
            NpgsqlDbType.Array | NpgsqlDbType.Text).Value =
            manifest.Songs.Select(static song => song.SongId).ToArray();
        cmd.Parameters.Add(
            "proposedMaxima",
            NpgsqlDbType.Array | NpgsqlDbType.Integer).Value =
            manifest.Songs
                .Select(static song => song.ProposedProLeadMaxScore)
                .ToArray();
        cmd.Parameters.AddWithValue("totalCharted", totalChartedSongs);
        cmd.Parameters.AddWithValue("instrument", ProLeadInstrument);
        cmd.Parameters.AddWithValue("threshold", 1.05d);
        cmd.Parameters.AddWithValue("m", 50);
        cmd.Parameters.AddWithValue("C", 0.5d);
        cmd.Parameters.AddWithValue(
            "registeredOnly",
            registeredOnly);
        await ReadCandidatesAsync(cmd, candidates, ct);
    }

    private static async Task AddCandidatesAsync(
        NpgsqlConnection conn,
        NpgsqlTransaction tx,
        string sql,
        List<RawMaintenanceCandidate> candidates,
        CancellationToken ct,
        bool registeredOnly,
        bool? includeProLeadRankCandidates = null)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandTimeout = CommandTimeoutSeconds;
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue(
            "registeredOnly",
            registeredOnly);
        if (includeProLeadRankCandidates.HasValue)
        {
            cmd.Parameters.AddWithValue(
                "includeProLeadRankCandidates",
                includeProLeadRankCandidates.Value);
        }
        await ReadCandidatesAsync(cmd, candidates, ct);
    }

    private static async Task ReadCandidatesAsync(
        NpgsqlCommand cmd,
        List<RawMaintenanceCandidate> candidates,
        CancellationToken ct)
    {
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            candidates.Add(new RawMaintenanceCandidate(
                SubjectType: reader.GetString(0),
                SubjectKey: reader.GetString(1),
                Instrument: reader.IsDBNull(2) ? null : reader.GetString(2),
                SongId: reader.IsDBNull(3) ? null : reader.GetString(3),
                ScopeKey: reader.IsDBNull(4) ? null : reader.GetString(4),
                CandidateKind: reader.GetString(5),
                Metric: reader.GetString(6),
                OldNumeric: reader.IsDBNull(7) ? null : reader.GetDecimal(7),
                NewNumeric: reader.IsDBNull(8) ? null : reader.GetDecimal(8),
                OldRank: reader.IsDBNull(9) ? null : reader.GetInt32(9),
                NewRank: reader.IsDBNull(10) ? null : reader.GetInt32(10),
                Lane: reader.GetString(11)));
        }
    }

    private static IReadOnlyList<ImprovementNotificationMaintenanceCandidate>
        ClassifyCandidates(IReadOnlyList<RawMaintenanceCandidate> rawCandidates)
    {
        var contaminatedProLeadSubjects = rawCandidates
            .Where(candidate =>
                candidate.SubjectType == "player"
                && candidate.Instrument == ProLeadInstrument)
            .GroupBy(candidate => (candidate.SubjectKey, candidate.Instrument))
            .Where(group => group.Any(candidate =>
                candidate.Lane != "player_rank"
                || candidate.Metric != MaxScorePercentRankMetric))
            .Select(group => group.Key)
            .ToHashSet();
        var ordinaryPlayerSubjects = rawCandidates
            .Where(candidate =>
                candidate.SubjectType == "player"
                && (
                    candidate.Lane == "player_song"
                    || candidate.CandidateKind is
                        "player_total_score_improved"
                        or "player_fc_count_improved"
                ))
            .Select(candidate => (candidate.SubjectKey, candidate.Instrument))
            .ToHashSet();

        return rawCandidates
            .Select(candidate =>
            {
                string classification;
                var allowed = false;
                var blocksMaintenance = true;

                if (candidate.SubjectType == "player"
                    && candidate.Instrument == ProLeadInstrument
                    && candidate.Metric == MaxScorePercentRankMetric)
                {
                    if (contaminatedProLeadSubjects.Contains(
                            (candidate.SubjectKey, candidate.Instrument)))
                    {
                        classification =
                            "pro_lead_denominator_attribution_ambiguous";
                    }
                    else
                    {
                        classification = "pro_lead_denominator_rank_movement";
                        allowed = true;
                        blocksMaintenance = false;
                    }
                }
                else if (IsIndependentlyOrdinaryScoreObservation(
                             candidate,
                             ordinaryPlayerSubjects))
                {
                    classification = "ordinary_score_observation_outside_maintenance";
                    blocksMaintenance = false;
                }
                else if (candidate.SubjectType == "band")
                {
                    classification = "band_candidate_not_allowed";
                }
                else if (candidate.Instrument != ProLeadInstrument)
                {
                    classification = "other_instrument_not_allowed";
                }
                else if (candidate.Metric != MaxScorePercentRankMetric)
                {
                    classification = candidate.CandidateKind == "player_rank_state_missing"
                        ? "required_player_rank_state_missing"
                        : "pro_lead_non_denominator_change";
                }
                else
                {
                    classification = "unclassified_maintenance_candidate";
                }

                return new ImprovementNotificationMaintenanceCandidate(
                    candidate.SubjectType,
                    candidate.SubjectKey,
                    candidate.Instrument,
                    candidate.SongId,
                    candidate.ScopeKey,
                    candidate.CandidateKind,
                    candidate.Metric,
                    candidate.OldNumeric,
                    candidate.NewNumeric,
                    candidate.OldRank,
                    candidate.NewRank,
                    classification,
                    allowed,
                    blocksMaintenance);
            })
            .OrderBy(candidate => candidate.SubjectType, StringComparer.Ordinal)
            .ThenBy(candidate => candidate.SubjectKey, StringComparer.Ordinal)
            .ThenBy(candidate => candidate.Instrument, StringComparer.Ordinal)
            .ThenBy(candidate => candidate.SongId, StringComparer.Ordinal)
            .ThenBy(candidate => candidate.ScopeKey, StringComparer.Ordinal)
            .ThenBy(candidate => candidate.CandidateKind, StringComparer.Ordinal)
            .ThenBy(candidate => candidate.Metric, StringComparer.Ordinal)
            .ThenBy(candidate => candidate.OldNumeric)
            .ThenBy(candidate => candidate.NewNumeric)
            .ThenBy(candidate => candidate.OldRank)
            .ThenBy(candidate => candidate.NewRank)
            .ToArray();
    }

    private static bool IsIndependentlyOrdinaryScoreObservation(
        RawMaintenanceCandidate candidate,
        IReadOnlySet<(string SubjectKey, string? Instrument)>
            ordinaryPlayerSubjects)
    {
        if (candidate.Lane is "player_song" or "band_song")
            return true;

        if (candidate.Lane == "band_rank")
            return candidate.CandidateKind != "band_rank_state_missing";

        if (candidate.Lane != "player_rank"
            || candidate.CandidateKind == "player_rank_state_missing")
        {
            return false;
        }

        return candidate.CandidateKind is
                "player_total_score_improved"
                or "player_fc_count_improved"
            || ordinaryPlayerSubjects.Contains(
                (candidate.SubjectKey, candidate.Instrument));
    }

    private static string BuildCanonicalCandidateData(
        long publishedScrapeId,
        ImprovementNotificationMaintenanceManifest manifest,
        int totalChartedSongs,
        IReadOnlyList<ImprovementNotificationMaintenanceCandidate> candidates)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString(
                "purpose",
                ImprovementNotificationSafetyContract.ProLeadMaxScoreRepairPurpose);
            writer.WriteString(
                "cause",
                ImprovementNotificationSafetyContract.MaxScoreRecomputeCause);
            writer.WriteString(
                "deliveryState",
                ImprovementNotificationSafetyContract.QuarantinedDeliveryState);
            writer.WriteNumber(
                "visibleDeliveryCap",
                ImprovementNotificationSafetyContract.ProLeadMaxScoreRepairVisibleDeliveryCap);
            writer.WriteNumber("publishedScrapeId", publishedScrapeId);
            writer.WriteNumber("totalChartedSongs", totalChartedSongs);
            WriteCanonicalManifest(writer, manifest);
            writer.WriteStartArray("projectedCandidates");
            foreach (var candidate in candidates)
                WriteCanonicalCandidate(writer, candidate);
            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void WriteCanonicalManifest(
        Utf8JsonWriter writer,
        ImprovementNotificationMaintenanceManifest manifest)
    {
        writer.WriteStartObject("manifest");
        writer.WriteNumber("manifestVersion", manifest.ManifestVersion);
        writer.WriteStartArray("songs");
        foreach (var song in manifest.Songs)
        {
            writer.WriteStartObject();
            writer.WriteString("songId", song.SongId);
            writer.WriteNumber(
                "expectedCurrentPathRevision",
                song.ExpectedCurrentPathRevision);
            writer.WriteString(
                "expectedCatalogLastModified",
                song.ExpectedCatalogLastModified);
            if (song.CurrentOldProLeadMaxScore.HasValue)
            {
                writer.WriteNumber(
                    "currentOldProLeadMaxScore",
                    song.CurrentOldProLeadMaxScore.Value);
            }
            else
            {
                writer.WriteNull("currentOldProLeadMaxScore");
            }
            writer.WriteNumber(
                "proposedProLeadMaxScore",
                song.ProposedProLeadMaxScore);
            writer.WriteString(
                "stagedArtifactGenerationId",
                song.StagedArtifactGenerationId);
            writer.WriteString("stagedDatFileHash", song.StagedDatFileHash);
            WriteNullableString(
                writer,
                "stagedChoptVersion",
                song.StagedChoptVersion);
            WriteNullableString(
                writer,
                "stagedChoptBinarySha256",
                song.StagedChoptBinarySha256);
            WriteNullableString(
                writer,
                "stagedGenerationProfile",
                song.StagedGenerationProfile);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    private static void WriteCanonicalCandidate(
        Utf8JsonWriter writer,
        ImprovementNotificationMaintenanceCandidate candidate)
    {
        writer.WriteStartObject();
        writer.WriteString("subjectType", candidate.SubjectType);
        writer.WriteString("subjectKey", candidate.SubjectKey);
        WriteNullableString(writer, "instrument", candidate.Instrument);
        WriteNullableString(writer, "songId", candidate.SongId);
        WriteNullableString(writer, "scopeKey", candidate.ScopeKey);
        writer.WriteString("candidateKind", candidate.CandidateKind);
        writer.WriteString("metric", candidate.Metric);
        WriteNullableDecimal(writer, "oldNumeric", candidate.OldNumeric);
        WriteNullableDecimal(writer, "newNumeric", candidate.NewNumeric);
        WriteNullableInt32(writer, "oldRank", candidate.OldRank);
        WriteNullableInt32(writer, "newRank", candidate.NewRank);
        writer.WriteString("classification", candidate.Classification);
        writer.WriteBoolean("allowed", candidate.Allowed);
        writer.WriteBoolean("blocksMaintenance", candidate.BlocksMaintenance);
        writer.WriteEndObject();
    }

    private static async Task<long> InsertMaintenanceRunAsync(
        NpgsqlConnection conn,
        NpgsqlTransaction tx,
        ImprovementNotificationMaintenanceDryRunReport dryRun,
        string canonicalCandidateData,
        CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
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
                visible_delivery_count,
                started_at,
                completed_at)
            VALUES (
                @purpose,
                @cause,
                @deliveryState,
                @publishedScrapeId,
                @digest,
                @canonicalCandidateData,
                @repairManifest,
                @totalChartedSongs,
                'completed',
                @candidateCount,
                @allowedCandidateCount,
                @externalRoutineCandidateCount,
                0,
                @allowedCandidateCount,
                0,
                0,
                now(),
                now())
            RETURNING maintenance_run_id;
            """;
        cmd.Parameters.AddWithValue(
            "purpose",
            ImprovementNotificationSafetyContract.ProLeadMaxScoreRepairPurpose);
        cmd.Parameters.AddWithValue(
            "cause",
            ImprovementNotificationSafetyContract.MaxScoreRecomputeCause);
        cmd.Parameters.AddWithValue(
            "deliveryState",
            ImprovementNotificationSafetyContract.QuarantinedDeliveryState);
        cmd.Parameters.AddWithValue(
            "publishedScrapeId",
            checked((int)dryRun.PublishedScrapeId));
        cmd.Parameters.AddWithValue("digest", dryRun.DryRunDigest);
        cmd.Parameters.AddWithValue("canonicalCandidateData", canonicalCandidateData);
        cmd.Parameters.Add("repairManifest", NpgsqlDbType.Jsonb).Value =
            dryRun.Manifest.SerializeCanonicalJson();
        cmd.Parameters.AddWithValue(
            "totalChartedSongs",
            dryRun.TotalChartedSongs);
        cmd.Parameters.AddWithValue("candidateCount", dryRun.CandidateCount);
        cmd.Parameters.AddWithValue("allowedCandidateCount", dryRun.AllowedCandidateCount);
        cmd.Parameters.AddWithValue(
            "externalRoutineCandidateCount",
            dryRun.ExternalRoutineCandidateCount);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync(ct));
    }

    private static async Task InsertQuarantinedCandidatesAsync(
        NpgsqlConnection conn,
        NpgsqlTransaction tx,
        long maintenanceRunId,
        IReadOnlyList<ImprovementNotificationMaintenanceCandidate> candidates,
        CancellationToken ct)
    {
        foreach (var candidate in candidates)
        {
            await using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO improvement_notification_maintenance_candidates (
                    maintenance_run_id,
                    candidate_key,
                    notification_purpose,
                    notification_cause,
                    delivery_state,
                    subject_type,
                    subject_key,
                    instrument,
                    song_id,
                    scope_key,
                    candidate_kind,
                    metric,
                    old_numeric,
                    new_numeric,
                    old_rank,
                    new_rank,
                    classification,
                    allowed,
                    payload)
                VALUES (
                    @maintenanceRunId,
                    @candidateKey,
                    @purpose,
                    @cause,
                    @deliveryState,
                    @subjectType,
                    @subjectKey,
                    @instrument,
                    @songId,
                    @scopeKey,
                    @candidateKind,
                    @metric,
                    @oldNumeric,
                    @newNumeric,
                    @oldRank,
                    @newRank,
                    @classification,
                    @allowed,
                    @payload);
                """;
            cmd.Parameters.AddWithValue("maintenanceRunId", maintenanceRunId);
            cmd.Parameters.AddWithValue("candidateKey", ComputeCandidateKey(candidate));
            cmd.Parameters.AddWithValue(
                "purpose",
                ImprovementNotificationSafetyContract.ProLeadMaxScoreRepairPurpose);
            cmd.Parameters.AddWithValue(
                "cause",
                ImprovementNotificationSafetyContract.MaxScoreRecomputeCause);
            cmd.Parameters.AddWithValue(
                "deliveryState",
                ImprovementNotificationSafetyContract.QuarantinedDeliveryState);
            cmd.Parameters.AddWithValue("subjectType", candidate.SubjectType);
            cmd.Parameters.AddWithValue("subjectKey", candidate.SubjectKey);
            cmd.Parameters.Add("instrument", NpgsqlDbType.Text).Value =
                NullableValue(candidate.Instrument);
            cmd.Parameters.Add("songId", NpgsqlDbType.Text).Value =
                NullableValue(candidate.SongId);
            cmd.Parameters.Add("scopeKey", NpgsqlDbType.Text).Value =
                NullableValue(candidate.ScopeKey);
            cmd.Parameters.AddWithValue("candidateKind", candidate.CandidateKind);
            cmd.Parameters.AddWithValue("metric", candidate.Metric);
            cmd.Parameters.Add("oldNumeric", NpgsqlDbType.Numeric).Value =
                NullableValue(candidate.OldNumeric);
            cmd.Parameters.Add("newNumeric", NpgsqlDbType.Numeric).Value =
                NullableValue(candidate.NewNumeric);
            cmd.Parameters.Add("oldRank", NpgsqlDbType.Integer).Value =
                NullableValue(candidate.OldRank);
            cmd.Parameters.Add("newRank", NpgsqlDbType.Integer).Value =
                NullableValue(candidate.NewRank);
            cmd.Parameters.AddWithValue("classification", candidate.Classification);
            cmd.Parameters.AddWithValue("allowed", candidate.Allowed);
            cmd.Parameters.Add("payload", NpgsqlDbType.Jsonb).Value =
                JsonSerializer.Serialize(candidate);
            await cmd.ExecuteNonQueryAsync(ct);
        }
    }

    private static async Task<long> BaselineAllowedProLeadRankStateAsync(
        NpgsqlConnection conn,
        NpgsqlTransaction tx,
        IReadOnlyList<ImprovementNotificationMaintenanceCandidate> candidates,
        CancellationToken ct)
    {
        var accountIds = candidates
            .Where(candidate =>
                candidate.Allowed
                && candidate.SubjectType == "player"
                && candidate.Instrument == ProLeadInstrument
                && candidate.Metric == MaxScorePercentRankMetric)
            .Select(candidate => candidate.SubjectKey)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(accountId => accountId, StringComparer.Ordinal)
            .ToArray();
        if (accountIds.Length == 0)
            return 0;

        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            UPDATE player_rank_improvement_state state
            SET max_score_percent_rank = current.max_score_percent_rank,
                computed_at = current.computed_at,
                observed_at = now(),
                updated_at = now()
            FROM account_rankings current
            WHERE state.account_id = current.account_id
              AND state.instrument = current.instrument
              AND state.instrument = 'Solo_PeripheralGuitar'
              AND state.account_id = ANY(@accountIds)
              AND state.total_score IS NOT DISTINCT FROM current.total_score
              AND state.full_combo_count IS NOT DISTINCT FROM current.full_combo_count
              AND state.max_score_percent_rank IS DISTINCT FROM current.max_score_percent_rank;
            """;
        cmd.Parameters.Add(
            "accountIds",
            NpgsqlDbType.Array | NpgsqlDbType.Text).Value = accountIds;
        return await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task<int> ValidateBaselineTargetsAsync(
        NpgsqlConnection conn,
        NpgsqlTransaction tx,
        IReadOnlyList<ImprovementNotificationMaintenanceCandidate> candidates,
        CancellationToken ct)
    {
        var accountIds = candidates
            .Where(candidate =>
                candidate.Allowed
                && candidate.SubjectType == "player"
                && candidate.Instrument == ProLeadInstrument
                && candidate.Metric == MaxScorePercentRankMetric)
            .Select(candidate => candidate.SubjectKey)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(accountId => accountId, StringComparer.Ordinal)
            .ToArray();
        if (accountIds.Length == 0)
            return 0;

        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandTimeout = CommandTimeoutSeconds;
        cmd.CommandText = """
            SELECT COUNT(*)::INTEGER
            FROM player_rank_improvement_state state
            JOIN account_rankings current
              ON current.account_id = state.account_id
             AND current.instrument = state.instrument
            WHERE state.instrument = 'Solo_PeripheralGuitar'
              AND state.account_id = ANY(@accountIds)
              AND state.total_score IS NOT DISTINCT FROM current.total_score
              AND state.full_combo_count IS NOT DISTINCT FROM current.full_combo_count
              AND state.max_score_percent_rank
                  IS DISTINCT FROM current.max_score_percent_rank;
            """;
        cmd.Parameters.Add(
            "accountIds",
            NpgsqlDbType.Array | NpgsqlDbType.Text).Value = accountIds;
        var eligibleCount = Convert.ToInt32(await cmd.ExecuteScalarAsync(ct));
        if (eligibleCount != accountIds.Length)
        {
            throw new InvalidOperationException(
                "Projected Pro Lead candidates do not map exactly to safe " +
                "rank-state baseline rows; no rows were written.");
        }

        return eligibleCount;
    }

    private static async Task UpdateMaintenanceRunStateCountAsync(
        NpgsqlConnection conn,
        NpgsqlTransaction tx,
        long maintenanceRunId,
        long rankStateRowsUpdated,
        CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            UPDATE improvement_notification_maintenance_runs
            SET player_rank_state_rows_updated = @rankStateRowsUpdated
            WHERE maintenance_run_id = @maintenanceRunId;
            """;
        cmd.Parameters.AddWithValue("rankStateRowsUpdated", rankStateRowsUpdated);
        cmd.Parameters.AddWithValue("maintenanceRunId", maintenanceRunId);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static string ComputeCandidateKey(
        ImprovementNotificationMaintenanceCandidate candidate)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
            WriteCanonicalCandidate(writer, candidate);
        return Convert.ToHexStringLower(SHA256.HashData(stream.ToArray()));
    }

    private static void ValidateExpectedPublishedScrapeId(long expectedPublishedScrapeId)
    {
        if (expectedPublishedScrapeId <= 0 || expectedPublishedScrapeId > int.MaxValue)
        {
            throw new ArgumentOutOfRangeException(
                nameof(expectedPublishedScrapeId),
                "Expected published scrape ID must be a positive PostgreSQL integer.");
        }
    }

    private static string NormalizeDigest(string digest)
    {
        var normalized = digest?.Trim().ToLowerInvariant()
            ?? throw new ArgumentNullException(nameof(digest));
        if (normalized.Length != 64
            || normalized.Any(character => !Uri.IsHexDigit(character)))
        {
            throw new ArgumentException(
                "Expected dry-run digest must be exactly 64 hexadecimal characters.",
                nameof(digest));
        }

        return normalized;
    }

    private static void WriteNullableString(
        Utf8JsonWriter writer,
        string propertyName,
        string? value)
    {
        if (value is null)
            writer.WriteNull(propertyName);
        else
            writer.WriteString(propertyName, value);
    }

    private static void WriteNullableDecimal(
        Utf8JsonWriter writer,
        string propertyName,
        decimal? value)
    {
        if (value.HasValue)
            writer.WriteNumber(propertyName, value.Value);
        else
            writer.WriteNull(propertyName);
    }

    private static void WriteNullableInt32(
        Utf8JsonWriter writer,
        string propertyName,
        int? value)
    {
        if (value.HasValue)
            writer.WriteNumber(propertyName, value.Value);
        else
            writer.WriteNull(propertyName);
    }

    private static object NullableValue(string? value) =>
        value is null ? DBNull.Value : value;

    private static object NullableValue(decimal? value) =>
        value.HasValue ? value.Value : DBNull.Value;

    private static object NullableValue(int? value) =>
        value.HasValue ? value.Value : DBNull.Value;

    private enum ManifestIdentityPhase
    {
        PreRepair,
        PostRepair,
    }

    private sealed record RawMaintenanceCandidate(
        string SubjectType,
        string SubjectKey,
        string? Instrument,
        string? SongId,
        string? ScopeKey,
        string CandidateKind,
        string Metric,
        decimal? OldNumeric,
        decimal? NewNumeric,
        int? OldRank,
        int? NewRank,
        string Lane);

    private static readonly string ProjectedProLeadRankCandidatesSql = $"""
        WITH {PublishedSoloScopeSql.CurrentResolvedEntriesCte},
        manifest AS (
            SELECT song_id, proposed_max_score
            FROM unnest(@songIds::TEXT[], @proposedMaxima::INTEGER[])
                AS item(song_id, proposed_max_score)
        ), projected_stats AS (
            SELECT stats.song_id,
                   stats.entry_count,
                   stats.log_weight,
                   COALESCE(manifest.proposed_max_score, stats.max_score) AS max_score
            FROM song_stats stats
            LEFT JOIN manifest
              ON manifest.song_id = stats.song_id
            WHERE stats.instrument = 'Solo_PeripheralGuitar'
        ), current_rows AS (
            SELECT resolved.song_id,
                   resolved.account_id,
                   resolved.score,
                   resolved.accuracy,
                   resolved.is_full_combo,
                   resolved.stars,
                   COALESCE(
                       NULLIF(resolved.api_rank, 0),
                       resolved.rank) AS effective_rank
            FROM resolved_rows resolved
        ), valid_current AS (
            SELECT current.song_id,
                   current.account_id,
                   current.score,
                   current.accuracy,
                   current.is_full_combo,
                   current.stars,
                   current.effective_rank,
                   stats.entry_count,
                   stats.log_weight,
                   stats.max_score
            FROM current_rows current
            JOIN projected_stats stats
              ON stats.song_id = current.song_id
            WHERE stats.entry_count > 0
              AND current.effective_rank > 0
              AND current.score <= COALESCE(
                  FLOOR(stats.max_score * @threshold)::INTEGER,
                  current.score + 1)
        ), fallback_entries AS (
            SELECT current.song_id,
                   current.account_id,
                   fallback.new_score AS score,
                   COALESCE(fallback.accuracy, 0) AS accuracy,
                   COALESCE(fallback.is_full_combo, FALSE) AS is_full_combo,
                   COALESCE(fallback.stars, 0) AS stars,
                   (
                       SELECT COUNT(*)::INTEGER + 1
                       FROM current_rows ranked_current
                       JOIN projected_stats ranked_stats
                         ON ranked_stats.song_id = ranked_current.song_id
                       WHERE ranked_current.song_id = current.song_id
                         AND ranked_current.score > fallback.new_score
                         AND ranked_current.score <= COALESCE(
                             FLOOR(ranked_stats.max_score * @threshold)::INTEGER,
                             ranked_current.score + 1)
                         AND ranked_current.account_id <> current.account_id
                   ) AS effective_rank,
                   stats.entry_count,
                   stats.log_weight,
                   stats.max_score
            FROM current_rows current
            JOIN projected_stats stats
              ON stats.song_id = current.song_id
             AND stats.entry_count > 0
            JOIN LATERAL (
                SELECT history.new_score,
                       history.accuracy,
                       history.is_full_combo,
                       history.stars
                FROM score_history history
                WHERE history.account_id = current.account_id
                  AND history.song_id = current.song_id
                  AND history.instrument = 'Solo_PeripheralGuitar'
                  AND history.new_score IS NOT NULL
                  AND history.new_score <= COALESCE(
                      FLOOR(stats.max_score * @threshold)::INTEGER,
                      history.new_score + 1)
                ORDER BY history.new_score DESC,
                         history.changed_at DESC,
                         history.id DESC
                LIMIT 1
            ) fallback ON TRUE
            WHERE current.score > COALESCE(
                FLOOR(stats.max_score * @threshold)::INTEGER,
                current.score + 1)
        ), valid_entries AS (
            SELECT * FROM valid_current
            UNION ALL
            SELECT * FROM fallback_entries
        ), aggregated AS (
            SELECT entries.account_id,
                   COUNT(*)::INTEGER AS songs_played,
                   @totalCharted::INTEGER AS total_charted_songs,
                   CAST(COUNT(*) AS DOUBLE PRECISION) / @totalCharted AS coverage,
                   AVG(
                       CAST(entries.effective_rank AS DOUBLE PRECISION)
                       / entries.entry_count) AS raw_skill_rating,
                   SUM(
                       (
                           CAST(entries.effective_rank AS DOUBLE PRECISION)
                           / entries.entry_count
                       ) * entries.log_weight
                   ) / NULLIF(SUM(entries.log_weight), 0) AS weighted_rating,
                   CAST(
                       SUM(CASE WHEN entries.is_full_combo THEN 1 ELSE 0 END)
                       AS DOUBLE PRECISION
                   ) / @totalCharted AS fc_rate,
                   SUM(entries.score)::BIGINT AS total_score,
                   AVG(
                       CASE
                           WHEN entries.max_score IS NOT NULL
                                AND entries.max_score > 0
                           THEN LEAST(
                               CAST(entries.score AS DOUBLE PRECISION)
                                   / entries.max_score,
                               @threshold)
                           ELSE NULL
                       END
                   ) AS max_score_percent,
                   AVG(entries.accuracy) AS avg_accuracy,
                   SUM(
                       CASE WHEN entries.is_full_combo THEN 1 ELSE 0 END
                   )::INTEGER AS full_combo_count,
                   AVG(entries.stars) AS avg_stars,
                   MIN(entries.effective_rank) AS best_rank,
                   AVG(
                       CAST(entries.effective_rank AS DOUBLE PRECISION)
                   ) AS avg_rank
            FROM valid_entries entries
            GROUP BY entries.account_id
        ), with_bayesian AS (
            SELECT aggregated.*,
                   (
                       songs_played * raw_skill_rating + @m * @C
                   ) / (songs_played + @m) AS adjusted_skill_rating,
                   (
                       songs_played * COALESCE(weighted_rating, 1.0) + @m * @C
                   ) / (songs_played + @m) AS adjusted_weighted_rating,
                   (
                       songs_played * COALESCE(max_score_percent, 0.5) + @m * @C
                   ) / (songs_played + @m) AS adjusted_max_score_percent
            FROM aggregated
        ), ranked AS (
            SELECT with_bayesian.*,
                   (
                       ROW_NUMBER() OVER (
                           ORDER BY adjusted_skill_rating ASC,
                                    songs_played DESC,
                                    total_score DESC,
                                    full_combo_count DESC,
                                    account_id ASC
                       )
                   )::INTEGER AS adjusted_skill_rank,
                   (
                       ROW_NUMBER() OVER (
                           ORDER BY adjusted_weighted_rating ASC,
                                    songs_played DESC,
                                    total_score DESC,
                                    full_combo_count DESC,
                                    account_id ASC
                       )
                   )::INTEGER AS weighted_rank,
                   (
                       ROW_NUMBER() OVER (
                           ORDER BY fc_rate DESC,
                                    total_score DESC,
                                    songs_played DESC,
                                    adjusted_skill_rating ASC,
                                    account_id ASC
                       )
                   )::INTEGER AS fc_rate_rank,
                   (
                       ROW_NUMBER() OVER (
                           ORDER BY total_score DESC,
                                    songs_played DESC,
                                    adjusted_skill_rating ASC,
                                    account_id ASC
                       )
                   )::INTEGER AS total_score_rank,
                   (
                       ROW_NUMBER() OVER (
                           ORDER BY adjusted_max_score_percent DESC,
                                    songs_played DESC,
                                    adjusted_skill_rating ASC,
                                    account_id ASC
                       )
                   )::INTEGER AS max_score_percent_rank
            FROM with_bayesian
        ), subjects AS (
            SELECT account_id
            FROM player_rank_improvement_state
            WHERE NOT @registeredOnly
               OR EXISTS (
                   SELECT 1
                   FROM registered_users registered
                   WHERE registered.account_id =
                       player_rank_improvement_state.account_id
               )
            UNION
            SELECT account_id
            FROM registered_users
        ), projected_rows AS (
            SELECT projected.*,
                   state.adjusted_skill_rank AS old_adjusted_skill_rank,
                   state.weighted_rank AS old_weighted_rank,
                   state.fc_rate_rank AS old_fc_rate_rank,
                   state.total_score_rank AS old_total_score_rank,
                   state.max_score_percent_rank AS old_max_score_percent_rank,
                   state.total_score AS old_total_score,
                   state.full_combo_count AS old_full_combo_count,
                   state.account_id AS state_account_id
            FROM subjects subject
            JOIN ranked projected
              ON projected.account_id = subject.account_id
            LEFT JOIN player_rank_improvement_state state
              ON state.account_id = projected.account_id
             AND state.instrument = 'Solo_PeripheralGuitar'
        )
        SELECT 'player'::TEXT AS subject_type,
               projected.account_id AS subject_key,
               'Solo_PeripheralGuitar'::TEXT AS instrument,
               NULL::TEXT AS song_id,
               NULL::TEXT AS scope_key,
               candidate.candidate_kind,
               candidate.metric,
               candidate.old_numeric,
               candidate.new_numeric,
               candidate.old_rank,
               candidate.new_rank,
               'player_rank'::TEXT AS lane
        FROM projected_rows projected
        CROSS JOIN LATERAL (VALUES
            (
                'player_rank_state_missing',
                'state',
                NULL::NUMERIC,
                NULL::NUMERIC,
                NULL::INTEGER,
                NULL::INTEGER,
                projected.state_account_id IS NULL
            ),
            (
                'player_adjusted_skill_rank_improved',
                'adjusted_skill_rank',
                NULL::NUMERIC,
                NULL::NUMERIC,
                projected.old_adjusted_skill_rank,
                projected.adjusted_skill_rank,
                projected.state_account_id IS NOT NULL
                    AND projected.old_adjusted_skill_rank IS NOT NULL
                    AND projected.adjusted_skill_rank > 0
                    AND projected.adjusted_skill_rank
                        < projected.old_adjusted_skill_rank
            ),
            (
                'player_weighted_rank_improved',
                'weighted_rank',
                NULL::NUMERIC,
                NULL::NUMERIC,
                projected.old_weighted_rank,
                projected.weighted_rank,
                projected.state_account_id IS NOT NULL
                    AND projected.old_weighted_rank IS NOT NULL
                    AND projected.weighted_rank > 0
                    AND projected.weighted_rank < projected.old_weighted_rank
            ),
            (
                'player_total_score_rank_improved',
                'total_score_rank',
                NULL::NUMERIC,
                NULL::NUMERIC,
                projected.old_total_score_rank,
                projected.total_score_rank,
                projected.state_account_id IS NOT NULL
                    AND projected.old_total_score_rank IS NOT NULL
                    AND projected.total_score_rank > 0
                    AND projected.total_score_rank
                        < projected.old_total_score_rank
            ),
            (
                'player_fc_rate_rank_improved',
                'fc_rate_rank',
                NULL::NUMERIC,
                NULL::NUMERIC,
                projected.old_fc_rate_rank,
                projected.fc_rate_rank,
                projected.state_account_id IS NOT NULL
                    AND projected.old_fc_rate_rank IS NOT NULL
                    AND projected.fc_rate_rank > 0
                    AND projected.fc_rate_rank < projected.old_fc_rate_rank
            ),
            (
                'player_total_score_improved',
                'total_score',
                projected.old_total_score::NUMERIC,
                projected.total_score::NUMERIC,
                NULL::INTEGER,
                NULL::INTEGER,
                projected.state_account_id IS NOT NULL
                    AND projected.old_total_score IS NOT NULL
                    AND projected.total_score > projected.old_total_score
            ),
            (
                'player_fc_count_improved',
                'full_combo_count',
                projected.old_full_combo_count::NUMERIC,
                projected.full_combo_count::NUMERIC,
                NULL::INTEGER,
                NULL::INTEGER,
                projected.state_account_id IS NOT NULL
                    AND projected.old_full_combo_count IS NOT NULL
                    AND projected.full_combo_count
                        > projected.old_full_combo_count
            ),
            (
                'player_max_score_percent_rank_changed',
                'max_score_percent_rank',
                NULL::NUMERIC,
                NULL::NUMERIC,
                projected.old_max_score_percent_rank,
                projected.max_score_percent_rank,
                projected.state_account_id IS NOT NULL
                    AND projected.old_max_score_percent_rank
                        IS DISTINCT FROM projected.max_score_percent_rank
            )
        ) candidate(
            candidate_kind,
            metric,
            old_numeric,
            new_numeric,
            old_rank,
            new_rank,
            should_emit)
        WHERE candidate.should_emit;
        """;

    private const string PlayerRankCandidatesSql = """
        WITH subjects AS (
            SELECT account_id
            FROM player_rank_improvement_state
            WHERE NOT @registeredOnly
               OR EXISTS (
                   SELECT 1
                   FROM registered_users registered
                   WHERE registered.account_id =
                       player_rank_improvement_state.account_id
               )
            UNION
            SELECT account_id
            FROM registered_users
        ), current_rows AS (
            SELECT current.*,
                   state.adjusted_skill_rank AS old_adjusted_skill_rank,
                   state.weighted_rank AS old_weighted_rank,
                   state.fc_rate_rank AS old_fc_rate_rank,
                   state.total_score_rank AS old_total_score_rank,
                   state.max_score_percent_rank AS old_max_score_percent_rank,
                   state.total_score AS old_total_score,
                   state.full_combo_count AS old_full_combo_count,
                   state.account_id AS state_account_id
            FROM subjects subject
            JOIN account_rankings current
              ON current.account_id = subject.account_id
            LEFT JOIN player_rank_improvement_state state
              ON state.account_id = current.account_id
             AND state.instrument = current.instrument
        )
        SELECT 'player'::TEXT AS subject_type,
               current.account_id AS subject_key,
               current.instrument,
               NULL::TEXT AS song_id,
               NULL::TEXT AS scope_key,
               candidate.candidate_kind,
               candidate.metric,
               candidate.old_numeric,
               candidate.new_numeric,
               candidate.old_rank,
               candidate.new_rank,
               'player_rank'::TEXT AS lane
        FROM current_rows current
        CROSS JOIN LATERAL (VALUES
            (
                'player_rank_state_missing',
                'state',
                NULL::NUMERIC,
                NULL::NUMERIC,
                NULL::INTEGER,
                NULL::INTEGER,
                current.state_account_id IS NULL
            ),
            (
                'player_adjusted_skill_rank_improved',
                'adjusted_skill_rank',
                NULL::NUMERIC,
                NULL::NUMERIC,
                current.old_adjusted_skill_rank,
                current.adjusted_skill_rank,
                current.state_account_id IS NOT NULL
                    AND current.old_adjusted_skill_rank IS NOT NULL
                    AND current.adjusted_skill_rank > 0
                    AND current.adjusted_skill_rank < current.old_adjusted_skill_rank
            ),
            (
                'player_weighted_rank_improved',
                'weighted_rank',
                NULL::NUMERIC,
                NULL::NUMERIC,
                current.old_weighted_rank,
                current.weighted_rank,
                current.state_account_id IS NOT NULL
                    AND current.old_weighted_rank IS NOT NULL
                    AND current.weighted_rank > 0
                    AND current.weighted_rank < current.old_weighted_rank
            ),
            (
                'player_total_score_rank_improved',
                'total_score_rank',
                NULL::NUMERIC,
                NULL::NUMERIC,
                current.old_total_score_rank,
                current.total_score_rank,
                current.state_account_id IS NOT NULL
                    AND current.old_total_score_rank IS NOT NULL
                    AND current.total_score_rank > 0
                    AND current.total_score_rank < current.old_total_score_rank
            ),
            (
                'player_fc_rate_rank_improved',
                'fc_rate_rank',
                NULL::NUMERIC,
                NULL::NUMERIC,
                current.old_fc_rate_rank,
                current.fc_rate_rank,
                current.state_account_id IS NOT NULL
                    AND current.old_fc_rate_rank IS NOT NULL
                    AND current.fc_rate_rank > 0
                    AND current.fc_rate_rank < current.old_fc_rate_rank
            ),
            (
                'player_total_score_improved',
                'total_score',
                current.old_total_score::NUMERIC,
                current.total_score::NUMERIC,
                NULL::INTEGER,
                NULL::INTEGER,
                current.state_account_id IS NOT NULL
                    AND current.old_total_score IS NOT NULL
                    AND current.total_score > current.old_total_score
            ),
            (
                'player_fc_count_improved',
                'full_combo_count',
                current.old_full_combo_count::NUMERIC,
                current.full_combo_count::NUMERIC,
                NULL::INTEGER,
                NULL::INTEGER,
                current.state_account_id IS NOT NULL
                    AND current.old_full_combo_count IS NOT NULL
                    AND current.full_combo_count > current.old_full_combo_count
            ),
            (
                'player_max_score_percent_rank_changed',
                'max_score_percent_rank',
                NULL::NUMERIC,
                NULL::NUMERIC,
                current.old_max_score_percent_rank,
                current.max_score_percent_rank,
                current.state_account_id IS NOT NULL
                    AND current.old_max_score_percent_rank
                        IS DISTINCT FROM current.max_score_percent_rank
            )
        ) candidate(
            candidate_kind,
            metric,
            old_numeric,
            new_numeric,
            old_rank,
            new_rank,
            should_emit)
        WHERE candidate.should_emit
          AND (
              @includeProLeadRankCandidates
              OR current.instrument <> 'Solo_PeripheralGuitar'
          );
        """;

    private const string PlayerSongCandidatesSql = """
        WITH subjects AS (
            SELECT account_id
            FROM player_improvement_state
            WHERE NOT @registeredOnly
               OR EXISTS (
                   SELECT 1
                   FROM registered_users registered
                   WHERE registered.account_id =
                       player_improvement_state.account_id
               )
            UNION
            SELECT account_id
            FROM registered_users
        ), current_rows AS (
            SELECT current.*,
                   state.account_id AS state_account_id,
                   state.score AS old_score,
                   state.rank AS old_rank,
                   state.stars AS old_stars,
                   state.is_full_combo AS old_full_combo,
                   state.difficulty AS old_difficulty
            FROM subjects subject
            JOIN current_leaderboard_entries current
              ON current.account_id = subject.account_id
            LEFT JOIN player_improvement_state state
              ON state.account_id = current.account_id
             AND state.song_id = current.song_id
             AND state.instrument = current.instrument
        )
        SELECT 'player'::TEXT AS subject_type,
               current.account_id AS subject_key,
               current.instrument,
               current.song_id,
               NULL::TEXT AS scope_key,
               candidate.candidate_kind,
               candidate.metric,
               candidate.old_numeric,
               candidate.new_numeric,
               candidate.old_rank,
               candidate.new_rank,
               'player_song'::TEXT AS lane
        FROM current_rows current
        CROSS JOIN LATERAL (VALUES
            (
                'player_first_score',
                'score',
                NULL::NUMERIC,
                current.score::NUMERIC,
                NULL::INTEGER,
                current.rank,
                current.state_account_id IS NULL
            ),
            (
                'player_score_pb',
                'score',
                current.old_score::NUMERIC,
                current.score::NUMERIC,
                NULL::INTEGER,
                NULL::INTEGER,
                current.state_account_id IS NOT NULL
                    AND current.score > COALESCE(current.old_score, -1)
            ),
            (
                'player_song_rank_improved',
                'song_rank',
                NULL::NUMERIC,
                NULL::NUMERIC,
                current.old_rank,
                current.rank,
                current.old_score IS NOT NULL
                    AND current.score IS NOT NULL
                    AND current.score > current.old_score
                    AND current.old_rank IS NOT NULL
                    AND current.rank IS NOT NULL
                    AND current.rank > 0
                    AND current.rank < current.old_rank
            ),
            (
                'player_stars_improved',
                'stars',
                current.old_stars::NUMERIC,
                current.stars::NUMERIC,
                NULL::INTEGER,
                NULL::INTEGER,
                current.state_account_id IS NOT NULL
                    AND current.stars IS NOT NULL
                    AND current.old_stars IS NOT NULL
                    AND current.stars > current.old_stars
            ),
            (
                'player_gold_stars_achieved',
                'stars',
                current.old_stars::NUMERIC,
                current.stars::NUMERIC,
                NULL::INTEGER,
                NULL::INTEGER,
                current.stars >= 6
                    AND (
                        current.state_account_id IS NULL
                        OR COALESCE(current.old_stars, 0) < 6
                    )
            ),
            (
                'player_fc_achieved',
                'full_combo',
                NULL::NUMERIC,
                NULL::NUMERIC,
                NULL::INTEGER,
                NULL::INTEGER,
                current.is_full_combo IS TRUE
                    AND (
                        current.state_account_id IS NULL
                        OR COALESCE(current.old_full_combo, FALSE) = FALSE
                    )
            ),
            (
                'player_difficulty_bumped',
                'difficulty',
                current.old_difficulty::NUMERIC,
                current.difficulty::NUMERIC,
                NULL::INTEGER,
                NULL::INTEGER,
                current.state_account_id IS NOT NULL
                    AND current.difficulty IS NOT NULL
                    AND current.old_difficulty IS NOT NULL
                    AND current.difficulty > current.old_difficulty
            )
        ) candidate(
            candidate_kind,
            metric,
            old_numeric,
            new_numeric,
            old_rank,
            new_rank,
            should_emit)
        WHERE candidate.should_emit;
        """;

    private const string BandSongCandidatesSql = """
        WITH current_rows AS (
            SELECT current.*,
                   subject.band_subject_id,
                   state.band_subject_id AS state_band_subject_id,
                   state.score AS old_score,
                   state.rank AS old_rank,
                   state.stars AS old_stars,
                   state.is_full_combo AS old_full_combo,
                   state.difficulty AS old_difficulty
            FROM current_band_leaderboard_entries current
            JOIN band_current_projection_scope published_scope
              ON published_scope.song_id = current.song_id
             AND published_scope.band_type = current.band_type
             AND published_scope.ranking_scope = current.ranking_scope
             AND published_scope.scope_combo_id = current.scope_combo_id
             AND published_scope.published_generation = current.projection_generation
            JOIN band_improvement_subjects subject
              ON subject.band_type = current.band_type
             AND subject.team_key = current.team_key
            LEFT JOIN band_improvement_state state
              ON state.band_subject_id = subject.band_subject_id
             AND state.song_id = current.song_id
             AND state.ranking_scope = current.ranking_scope
             AND state.scope_combo_id = COALESCE(current.scope_combo_id, '')
            WHERE NOT @registeredOnly
               OR EXISTS (
                   SELECT 1
                   FROM registered_bands registered
                   WHERE registered.band_type = current.band_type
                     AND registered.team_key = current.team_key
               )
        )
        SELECT 'band'::TEXT AS subject_type,
               current.band_type || ':' || current.team_key AS subject_key,
               NULL::TEXT AS instrument,
               current.song_id,
               current.ranking_scope || ':' || COALESCE(current.scope_combo_id, '')
                   AS scope_key,
               candidate.candidate_kind,
               candidate.metric,
               candidate.old_numeric,
               candidate.new_numeric,
               candidate.old_rank,
               candidate.new_rank,
               'band_song'::TEXT AS lane
        FROM current_rows current
        CROSS JOIN LATERAL (VALUES
            (
                'band_first_score',
                'score',
                NULL::NUMERIC,
                current.score::NUMERIC,
                NULL::INTEGER,
                current.rank,
                current.state_band_subject_id IS NULL
            ),
            (
                CASE
                    WHEN current.ranking_scope = 'combo'
                        THEN 'band_combo_score_pb'
                    ELSE 'band_score_pb'
                END,
                'score',
                current.old_score::NUMERIC,
                current.score::NUMERIC,
                NULL::INTEGER,
                NULL::INTEGER,
                current.state_band_subject_id IS NOT NULL
                    AND current.score > COALESCE(current.old_score, -1)
            ),
            (
                'band_song_rank_improved',
                'song_rank',
                NULL::NUMERIC,
                NULL::NUMERIC,
                current.old_rank,
                current.rank,
                current.old_score IS NOT NULL
                    AND current.score IS NOT NULL
                    AND current.score > current.old_score
                    AND current.old_rank IS NOT NULL
                    AND current.rank IS NOT NULL
                    AND current.rank > 0
                    AND current.rank < current.old_rank
            ),
            (
                'band_stars_improved',
                'stars',
                current.old_stars::NUMERIC,
                current.stars::NUMERIC,
                NULL::INTEGER,
                NULL::INTEGER,
                current.state_band_subject_id IS NOT NULL
                    AND current.stars IS NOT NULL
                    AND current.old_stars IS NOT NULL
                    AND current.stars > current.old_stars
            ),
            (
                'band_gold_stars_achieved',
                'stars',
                current.old_stars::NUMERIC,
                current.stars::NUMERIC,
                NULL::INTEGER,
                NULL::INTEGER,
                current.stars >= 6
                    AND (
                        current.state_band_subject_id IS NULL
                        OR COALESCE(current.old_stars, 0) < 6
                    )
            ),
            (
                'band_fc_achieved',
                'full_combo',
                NULL::NUMERIC,
                NULL::NUMERIC,
                NULL::INTEGER,
                NULL::INTEGER,
                current.is_full_combo IS TRUE
                    AND (
                        current.state_band_subject_id IS NULL
                        OR COALESCE(current.old_full_combo, FALSE) = FALSE
                    )
            ),
            (
                'band_member_difficulty_bumped',
                'difficulty',
                current.old_difficulty::NUMERIC,
                current.difficulty::NUMERIC,
                NULL::INTEGER,
                NULL::INTEGER,
                current.state_band_subject_id IS NOT NULL
                    AND current.difficulty IS NOT NULL
                    AND current.old_difficulty IS NOT NULL
                    AND current.difficulty > current.old_difficulty
            )
        ) candidate(
            candidate_kind,
            metric,
            old_numeric,
            new_numeric,
            old_rank,
            new_rank,
            should_emit)
        WHERE candidate.should_emit;
        """;

    private const string BandRankCandidatesSql = """
        WITH rankings AS (
            SELECT band_type, ranking_scope, combo_id, team_key,
                   weighted_rank, fc_rate_rank, total_score_rank,
                   total_score, full_combo_count
            FROM band_team_rankings_current_band_duets
            UNION ALL
            SELECT band_type, ranking_scope, combo_id, team_key,
                   weighted_rank, fc_rate_rank, total_score_rank,
                   total_score, full_combo_count
            FROM band_team_rankings_current_band_trios
            UNION ALL
            SELECT band_type, ranking_scope, combo_id, team_key,
                   weighted_rank, fc_rate_rank, total_score_rank,
                   total_score, full_combo_count
            FROM band_team_rankings_current_band_quad
        ), current_rows AS (
            SELECT current.*,
                   subject.band_subject_id,
                   state.band_subject_id AS state_band_subject_id,
                   state.weighted_rank AS old_weighted_rank,
                   state.fc_rate_rank AS old_fc_rate_rank,
                   state.total_score_rank AS old_total_score_rank,
                   state.total_score AS old_total_score,
                   state.full_combo_count AS old_full_combo_count
            FROM rankings current
            JOIN band_improvement_subjects subject
              ON subject.band_type = current.band_type
             AND subject.team_key = current.team_key
            LEFT JOIN band_rank_improvement_state state
              ON state.band_subject_id = subject.band_subject_id
             AND state.ranking_scope = current.ranking_scope
             AND state.combo_id = COALESCE(current.combo_id, '')
            WHERE NOT @registeredOnly
               OR EXISTS (
                   SELECT 1
                   FROM registered_bands registered
                   WHERE registered.band_type = current.band_type
                     AND registered.team_key = current.team_key
               )
        )
        SELECT 'band'::TEXT AS subject_type,
               current.band_type || ':' || current.team_key AS subject_key,
               NULL::TEXT AS instrument,
               NULL::TEXT AS song_id,
               current.ranking_scope || ':' || COALESCE(current.combo_id, '')
                   AS scope_key,
               candidate.candidate_kind,
               candidate.metric,
               candidate.old_numeric,
               candidate.new_numeric,
               candidate.old_rank,
               candidate.new_rank,
               'band_rank'::TEXT AS lane
        FROM current_rows current
        CROSS JOIN LATERAL (VALUES
            (
                'band_rank_state_missing',
                'state',
                NULL::NUMERIC,
                NULL::NUMERIC,
                NULL::INTEGER,
                NULL::INTEGER,
                current.state_band_subject_id IS NULL
            ),
            (
                'band_weighted_rank_improved',
                'weighted_rank',
                NULL::NUMERIC,
                NULL::NUMERIC,
                current.old_weighted_rank,
                current.weighted_rank,
                current.state_band_subject_id IS NOT NULL
                    AND current.old_weighted_rank IS NOT NULL
                    AND current.weighted_rank > 0
                    AND current.weighted_rank < current.old_weighted_rank
                    AND (
                        (
                            current.old_total_score IS NOT NULL
                            AND current.total_score IS NOT NULL
                            AND current.total_score > current.old_total_score
                        )
                        OR (
                            current.old_full_combo_count IS NOT NULL
                            AND current.full_combo_count IS NOT NULL
                            AND current.full_combo_count > current.old_full_combo_count
                        )
                    )
            ),
            (
                'band_total_score_rank_improved',
                'total_score_rank',
                NULL::NUMERIC,
                NULL::NUMERIC,
                current.old_total_score_rank,
                current.total_score_rank,
                current.state_band_subject_id IS NOT NULL
                    AND current.old_total_score_rank IS NOT NULL
                    AND current.total_score_rank > 0
                    AND current.total_score_rank < current.old_total_score_rank
                    AND current.old_total_score IS NOT NULL
                    AND current.total_score IS NOT NULL
                    AND current.total_score > current.old_total_score
            ),
            (
                'band_fc_rate_rank_improved',
                'fc_rate_rank',
                NULL::NUMERIC,
                NULL::NUMERIC,
                current.old_fc_rate_rank,
                current.fc_rate_rank,
                current.state_band_subject_id IS NOT NULL
                    AND current.old_fc_rate_rank IS NOT NULL
                    AND current.fc_rate_rank > 0
                    AND current.fc_rate_rank < current.old_fc_rate_rank
                    AND current.old_full_combo_count IS NOT NULL
                    AND current.full_combo_count IS NOT NULL
                    AND current.full_combo_count > current.old_full_combo_count
            ),
            (
                'band_total_score_improved',
                'total_score',
                current.old_total_score::NUMERIC,
                current.total_score::NUMERIC,
                NULL::INTEGER,
                NULL::INTEGER,
                current.state_band_subject_id IS NOT NULL
                    AND current.old_total_score IS NOT NULL
                    AND current.total_score > current.old_total_score
            ),
            (
                'band_fc_count_improved',
                'full_combo_count',
                current.old_full_combo_count::NUMERIC,
                current.full_combo_count::NUMERIC,
                NULL::INTEGER,
                NULL::INTEGER,
                current.state_band_subject_id IS NOT NULL
                    AND current.old_full_combo_count IS NOT NULL
                    AND current.full_combo_count > current.old_full_combo_count
            )
        ) candidate(
            candidate_kind,
            metric,
            old_numeric,
            new_numeric,
            old_rank,
            new_rank,
            should_emit)
        WHERE candidate.should_emit;
        """;
}

public sealed record ImprovementNotificationMaintenanceCandidate(
    string SubjectType,
    string SubjectKey,
    string? Instrument,
    string? SongId,
    string? ScopeKey,
    string CandidateKind,
    string Metric,
    decimal? OldNumeric,
    decimal? NewNumeric,
    int? OldRank,
    int? NewRank,
    string Classification,
    bool Allowed,
    bool BlocksMaintenance);

public sealed record ImprovementNotificationMaintenanceClassificationCount(
    string Classification,
    long CandidateCount,
    bool Allowed,
    bool BlocksMaintenance);

public sealed record ImprovementNotificationMaintenanceSubjectMaximum(
    string SubjectType,
    string SubjectKey,
    string? Instrument,
    long CandidateCount,
    decimal? MaxAbsoluteNumericDelta,
    int? MaxAbsoluteRankMovement,
    int? MaxRankImprovement);

public sealed record ImprovementNotificationMaintenanceDryRunReport(
    string Purpose,
    string Cause,
    string DeliveryState,
    long PublishedScrapeId,
    ImprovementNotificationMaintenanceManifest Manifest,
    int TotalChartedSongs,
    string DryRunDigest,
    long CandidateCount,
    long AllowedCandidateCount,
    long ExternalRoutineCandidateCount,
    long RejectedCandidateCount,
    IReadOnlyList<ImprovementNotificationMaintenanceClassificationCount>
        ClassificationCounts,
    IReadOnlyList<ImprovementNotificationMaintenanceSubjectMaximum>
        PerSubjectMaxima,
    long MaxCandidatesForAnySubject,
    int VisibleDeliveryCap,
    int VisibleDeliveryCount,
    long QuarantineCandidateCount,
    string CapDecision,
    IReadOnlyList<ImprovementNotificationMaintenanceCandidate> Candidates);

public sealed record ImprovementNotificationMaintenanceExecuteReport(
    string Purpose,
    string Cause,
    string DeliveryState,
    long PublishedScrapeId,
    string DryRunDigest,
    int TotalChartedSongs,
    long CandidateCount,
    long ExternalRoutineCandidateCount,
    long QuarantinedCandidateCount,
    long SelectivePlayerRankStateRowsUpdated,
    int VisibleDeliveryCap,
    int VisibleDeliveryCount,
    bool BroadcastRequested);
