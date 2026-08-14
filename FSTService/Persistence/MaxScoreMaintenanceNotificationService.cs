using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Npgsql;
using NpgsqlTypes;

namespace FSTService.Persistence;

public sealed record MaxScoreMaintenanceNotificationInspection(
    string PublishedScoreSourceFingerprint,
    string NotificationStateFingerprint,
    IReadOnlyList<MaxScoreMaintenanceCandidate> Candidates)
{
    public long CandidateCount => Candidates.Sum(candidate =>
        candidate.SubjectType == "aggregate"
        && candidate.NewNumeric.HasValue
            ? checked((long)candidate.NewNumeric.Value)
            : 1L);
}

public sealed record MaxScoreMaintenanceNotificationQuarantineResult(
    long MaintenanceRunId,
    string CandidateDigest,
    long CandidateCount,
    long StateRowsUpdated,
    int VisibleDeliveryCount);

public sealed class MaxScoreMaintenanceNotificationService
{
    private const int DefaultCommandTimeoutSeconds = 600;
    private const int MaximumMaintenanceCandidates = 100_000;
    private readonly NpgsqlDataSource _dataSource;
    private readonly ImprovementNotificationService _routine;
    private readonly ImprovementNotificationOptions _options;
    private readonly ILogger<MaxScoreMaintenanceNotificationService> _log;

    public MaxScoreMaintenanceNotificationService(
        NpgsqlDataSource dataSource,
        ImprovementNotificationService routine,
        IOptions<ImprovementNotificationOptions> options,
        ILogger<MaxScoreMaintenanceNotificationService> log)
    {
        _dataSource = dataSource;
        _routine = routine;
        _options = options.Value;
        _log = log;
    }

    public async Task<MaxScoreMaintenanceNotificationInspection>
        InspectRoutineStateAsync(
            MaxScoreMaintenanceManifest manifest,
            string manifestSha256,
            bool requireOwnedFreeze,
            CancellationToken ct)
    {
        var normalizedManifest = manifest.ValidateAndNormalize();
        var normalizedDigest =
            MaxScoreMaintenanceManifest.NormalizeSha256(
                manifestSha256,
                nameof(manifestSha256));
        await ValidatePublishedNotificationInputsAsync(
            normalizedManifest.ExpectedPublishedScrapeId,
            requireOwnedFreeze
                ? PublicReadFreezeState.MaxScoreMaintenanceReasonPrefix
                  + normalizedDigest
                : null,
            ct);

        var routineReport = _routine.Precompute(
            CreateDryRunOptions(
                normalizedManifest.ExpectedPublishedScrapeId));
        var candidates = await LoadPlayerRankCandidatesAsync(ct);
        if (CountRoutinePlayerRankEvents(candidates)
            != routineReport.PlayerRankEventsInserted)
        {
            throw new InvalidOperationException(
                "Routine player-rank candidate detail count changed during max-score inspection.");
        }
        if (routineReport.PlayerSongEventsInserted > 0
            || routineReport.BandSongEventsInserted > 0
            || routineReport.BandRankEventsInserted > 0)
        {
            var unexplained = new List<MaxScoreMaintenanceCandidate>();
            if (routineReport.PlayerSongEventsInserted > 0)
            {
                unexplained.Add(CreateAggregateCandidate(
                    "player_song",
                    routineReport.PlayerSongEventsInserted));
            }
            if (routineReport.BandSongEventsInserted > 0)
            {
                unexplained.Add(CreateAggregateCandidate(
                    "band_song",
                    routineReport.BandSongEventsInserted));
            }
            if (routineReport.BandRankEventsInserted > 0)
            {
                unexplained.Add(CreateAggregateCandidate(
                    "band_rank",
                    routineReport.BandRankEventsInserted));
            }
            candidates = candidates.Concat(unexplained).ToArray();
        }

        return new MaxScoreMaintenanceNotificationInspection(
            await ComputePublishedScoreSourceFingerprintAsync(
                normalizedManifest,
                ct),
            await ComputeNotificationStateFingerprintAsync(ct),
            candidates);
    }

    public async Task<MaxScoreMaintenanceNotificationQuarantineResult>
        QuarantineAndAlignAsync(
            MaxScoreMaintenanceManifest manifest,
            string manifestSha256,
            string planDigest,
            string expectedScoreSourceFingerprint,
            CancellationToken ct)
    {
        var normalizedManifest = manifest.ValidateAndNormalize();
        var normalizedManifestDigest =
            MaxScoreMaintenanceManifest.NormalizeSha256(
                manifestSha256,
                nameof(manifestSha256));
        var normalizedPlanDigest =
            MaxScoreMaintenanceManifest.NormalizeSha256(
                planDigest,
                nameof(planDigest));
        var freezeReason =
            PublicReadFreezeState.MaxScoreMaintenanceReasonPrefix
            + normalizedManifestDigest;
        await ValidatePublishedNotificationInputsAsync(
            normalizedManifest.ExpectedPublishedScrapeId,
            freezeReason,
            ct);
        var scoreFingerprint =
            await ComputePublishedScoreSourceFingerprintAsync(
                normalizedManifest,
                ct);
        if (!string.Equals(
                scoreFingerprint,
                expectedScoreSourceFingerprint,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Published score-source fingerprint changed after planning; notification attribution is unsafe.");
        }

        var routineReport = _routine.Precompute(
            CreateDryRunOptions(
                normalizedManifest.ExpectedPublishedScrapeId));
        if (routineReport.PlayerSongEventsInserted > 0)
        {
            throw new InvalidOperationException(
                "Maintenance overlapped routine player-song notification candidates; reads remain frozen.");
        }

        var affectedInstruments = normalizedManifest.Songs
            .SelectMany(song => song.ChangedInstruments)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(instrument => instrument, StringComparer.Ordinal)
            .ToArray();
        var targetSongIds = normalizedManifest.Songs
            .Select(song => song.SongId)
            .ToHashSet(StringComparer.Ordinal);

        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(
            IsolationLevel.RepeatableRead,
            ct);
        await ConfigureTransactionAsync(conn, tx, ct);
        await LockAndValidateOwnedPublicationAsync(
            conn,
            tx,
            normalizedManifest.ExpectedPublishedScrapeId,
            normalizedManifest.ExpectedPublicationId,
            freezeReason,
            ct);
        await BaselineMissingBandSubjectsAsync(
            conn,
            tx,
            ct);

        var playerRankCandidates =
            await LoadCandidatesAsync(
                conn,
                tx,
                PlayerRankCandidatesSql,
                ct);
        var bandSongCandidates =
            await LoadCandidatesAsync(
                conn,
                tx,
                BandSongCandidatesSql,
                ct);
        var bandRankCandidates =
            await LoadCandidatesAsync(
                conn,
                tx,
                BandRankCandidatesSql,
                ct);
        if (CountRoutinePlayerRankEvents(playerRankCandidates)
                != routineReport.PlayerRankEventsInserted
            || CountRoutineBandSongEvents(bandSongCandidates)
                != routineReport.BandSongEventsInserted
            || CountRoutineBandRankEvents(bandRankCandidates)
                != routineReport.BandRankEventsInserted)
        {
            throw new InvalidOperationException(
                "Routine notification candidate details changed during maintenance classification.");
        }
        var candidates = ClassifyMaintenanceCandidates(
            playerRankCandidates
                .Concat(bandSongCandidates)
                .Concat(bandRankCandidates)
                .ToArray(),
            affectedInstruments,
            targetSongIds);
        if (candidates.Length > MaximumMaintenanceCandidates)
        {
            throw new InvalidOperationException(
                $"Maintenance candidate count {candidates.Length:N0} exceeds the fail-closed limit {MaximumMaintenanceCandidates:N0}.");
        }
        if (candidates.Any(candidate => candidate.BlocksMaintenance))
        {
            throw new InvalidOperationException(
                "One or more notification candidates are outside the changed max-score instruments; reads remain frozen.");
        }

        var canonicalCandidateData = BuildCanonicalCandidateData(
            normalizedManifestDigest,
            normalizedPlanDigest,
            normalizedManifest.ExpectedPublishedScrapeId,
            candidates);
        var candidateDigest = Convert.ToHexStringLower(
            SHA256.HashData(
                Encoding.UTF8.GetBytes(canonicalCandidateData)));

        var existingRun = await FindExistingMaintenanceRunAsync(
            conn,
            tx,
            normalizedManifest.ExpectedPublishedScrapeId,
            candidateDigest,
            ct);
        long maintenanceRunId;
        long stateRowsUpdated;
        if (existingRun.HasValue)
        {
            maintenanceRunId = existingRun.Value;
            stateRowsUpdated = await ReadMaintenanceStateCountAsync(
                conn,
                tx,
                maintenanceRunId,
                ct);
        }
        else
        {
            maintenanceRunId = await InsertMaintenanceRunAsync(
                conn,
                tx,
                normalizedManifest,
                candidateDigest,
                canonicalCandidateData,
                candidates.Length,
                ct);
            await InsertCandidatesAsync(
                conn,
                tx,
                maintenanceRunId,
                candidates,
                ct);
            var playerStateRowsUpdated =
                await AlignPlayerRankStateAsync(
                conn,
                tx,
                affectedInstruments,
                ct);
            var bandSongStateRowsUpdated =
                await AlignBandSongStateAsync(
                    conn,
                    tx,
                    targetSongIds,
                    ct);
            var bandRankStateRowsUpdated =
                await AlignBandRankStateAsync(
                    conn,
                    tx,
                    ct);
            stateRowsUpdated =
                playerStateRowsUpdated
                + bandSongStateRowsUpdated
                + bandRankStateRowsUpdated;
            await UpdateMaintenanceStateCountAsync(
                conn,
                tx,
                maintenanceRunId,
                playerStateRowsUpdated,
                ct);
        }

        await using (var advance = conn.CreateCommand())
        {
            advance.Transaction = tx;
            advance.CommandText = """
                UPDATE max_score_maintenance_runs
                SET phase = 'notifications_quarantined',
                    status = 'running',
                    notification_maintenance_run_id =
                        @notificationMaintenanceRunId,
                    quarantined_candidate_count = @candidateCount,
                    visible_delivery_count = 0,
                    failure_stage = NULL,
                    failure_detail = NULL,
                    updated_at = now()
                WHERE manifest_sha256 = @manifestSha256
                  AND plan_digest = @planDigest
                  AND phase IN (
                      'derived_state_rebuilt',
                      'notifications_quarantined')
                """;
            advance.Parameters.AddWithValue(
                "notificationMaintenanceRunId",
                maintenanceRunId);
            advance.Parameters.AddWithValue(
                "candidateCount",
                candidates.LongLength);
            advance.Parameters.AddWithValue(
                "manifestSha256",
                normalizedManifestDigest);
            advance.Parameters.AddWithValue(
                "planDigest",
                normalizedPlanDigest);
            if (await advance.ExecuteNonQueryAsync(ct) != 1)
            {
                throw new InvalidOperationException(
                    "Max-score workflow phase changed before notification quarantine committed.");
            }
        }

        await ValidateMarkerStillCompletedAsync(
            conn,
            tx,
            normalizedManifest.ExpectedPublishedScrapeId,
            ct);
        await tx.CommitAsync(ct);

        _log.LogInformation(
            "Persisted max-score notification quarantine for scrape {ScrapeId}: candidates={CandidateCount:N0}, stateRows={StateRows:N0}, visible=0.",
            normalizedManifest.ExpectedPublishedScrapeId,
            candidates.Length,
            stateRowsUpdated);
        return new MaxScoreMaintenanceNotificationQuarantineResult(
            maintenanceRunId,
            candidateDigest,
            candidates.Length,
            stateRowsUpdated,
            VisibleDeliveryCount: 0);
    }

    internal static MaxScoreMaintenanceCandidate[]
        ClassifyPlayerRankCandidates(
            IReadOnlyList<MaxScoreMaintenanceCandidate> candidates,
            IReadOnlyCollection<string> affectedInstruments)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(affectedInstruments);
        return ClassifyMaintenanceCandidates(
            candidates,
            affectedInstruments,
            new HashSet<string>(StringComparer.Ordinal));
    }

    internal static int CountRoutinePlayerRankEvents(
        IReadOnlyList<MaxScoreMaintenanceCandidate> candidates)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        return candidates
            .Where(candidate => candidate.CandidateKind is
                "player_adjusted_skill_rank_improved"
                or "player_weighted_rank_improved"
                or "player_total_score_rank_improved"
                or "player_fc_rate_rank_improved"
                or "player_total_score_improved"
                or "player_fc_count_improved")
            .Select(candidate => (
                candidate.SubjectKey,
                candidate.Instrument))
            .Distinct()
            .Count();
    }

    internal static int CountRoutineBandSongEvents(
        IReadOnlyList<MaxScoreMaintenanceCandidate> candidates)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        var bandSongCandidates = candidates
            .Where(candidate => candidate.Lane == "band_song")
            .ToArray();
        if (bandSongCandidates.Any(candidate =>
                candidate.SongId is null
                || candidate.RoutineEventGroupKey is null))
        {
            throw new InvalidOperationException(
                "Band-song maintenance candidates are missing routine play-group identity.");
        }

        return bandSongCandidates
            .Select(candidate => (
                candidate.SubjectKey,
                candidate.SongId!,
                candidate.RoutineEventGroupKey!))
            .Distinct()
            .Count();
    }

    internal static int CountRoutineBandRankEvents(
        IReadOnlyList<MaxScoreMaintenanceCandidate> candidates)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        var bandRankCandidates = candidates
            .Where(candidate => candidate.Lane == "band_rank")
            .ToArray();
        if (bandRankCandidates.Any(candidate =>
                candidate.RoutineEventGroupKey is null))
        {
            throw new InvalidOperationException(
                "Band-rank maintenance candidates are missing routine rank-group identity.");
        }

        var groupedRankEvents = bandRankCandidates
            .Where(candidate => candidate.CandidateKind is
                "band_total_score_rank_improved"
                or "band_weighted_rank_improved"
                or "band_fc_rate_rank_improved")
            .Select(candidate => (
                candidate.SubjectKey,
                candidate.RoutineEventGroupKey!))
            .Distinct()
            .Count();
        var progressEvents = bandRankCandidates.Count(candidate =>
            candidate.CandidateKind is
                "band_total_score_improved"
                or "band_fc_count_improved");
        var recognizedRows = bandRankCandidates.Count(candidate =>
            candidate.CandidateKind is
                "band_total_score_rank_improved"
                or "band_weighted_rank_improved"
                or "band_fc_rate_rank_improved"
                or "band_total_score_improved"
                or "band_fc_count_improved"
                or "band_rank_state_missing");
        if (recognizedRows != bandRankCandidates.Length)
        {
            throw new InvalidOperationException(
                "Band-rank maintenance candidates contain an unknown routine grouping kind.");
        }

        return groupedRankEvents + progressEvents;
    }

    internal static MaxScoreMaintenanceCandidate[]
        ClassifyMaintenanceCandidates(
            IReadOnlyList<MaxScoreMaintenanceCandidate> candidates,
            IReadOnlyCollection<string> affectedInstruments,
            IReadOnlySet<string> targetSongIds)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(affectedInstruments);
        ArgumentNullException.ThrowIfNull(targetSongIds);
        return candidates
            .Select(candidate =>
            {
                var maintenanceInduced =
                    candidate.Lane switch
                    {
                        "player_rank" =>
                            candidate.Instrument is not null
                            && affectedInstruments.Contains(
                                candidate.Instrument,
                                StringComparer.Ordinal),
                        "band_song" =>
                            candidate.SongId is not null
                            && targetSongIds.Contains(
                                candidate.SongId),
                        "band_rank" => true,
                        _ => false,
                    };
                return candidate with
                {
                    Classification = maintenanceInduced
                        ? "max_score_derived_rank_change"
                        : "unexplained_routine_candidate",
                    MaintenanceInduced = maintenanceInduced,
                    BlocksMaintenance = !maintenanceInduced,
                };
            })
            .ToArray();
    }

    private ImprovementNotificationPrecomputeOptions CreateDryRunOptions(
        long publishedScrapeId)
        => new(
            Execute: false,
            BaselineOnly: false,
            Scope: _options.Scope,
            IncludePlayers: true,
            IncludeBands: true,
            IncludeSongEvents: true,
            IncludeRankings: true,
            PruneExpired: false,
            CommandTimeoutSeconds: _options.CommandTimeoutSeconds > 0
                ? _options.CommandTimeoutSeconds
                : DefaultCommandTimeoutSeconds,
            DetectedAtUtc: DateTime.UtcNow,
            Source: "max-score-maintenance-plan",
            PublishedScrapeId: publishedScrapeId);

    private async Task ValidatePublishedNotificationInputsAsync(
        long expectedPublishedScrapeId,
        string? requiredFreezeReason,
        CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = DefaultCommandTimeoutSeconds;
        cmd.CommandText = """
            SELECT publication.published_scrape_id,
                   publication.current_publication_id,
                   publication.working_publication_id,
                   publication.public_reads_frozen,
                   publication.public_reads_frozen_scrape_id,
                   publication.public_reads_frozen_reason,
                   publication.improvement_notifications_scrape_id,
                   publication.improvement_notifications_status,
                   scrape.status,
                   EXISTS (
                       SELECT 1
                       FROM improvement_detection_runs run
                       WHERE run.published_scrape_id =
                               publication.published_scrape_id
                         AND run.status = 'completed'
                         AND run.mode = 'execute'
                         AND NOT run.baseline_only
                         AND run.include_players
                         AND run.include_song_events
                         AND run.include_rankings
                         AND run.notification_purpose =
                               'routine_score_observation_v1'
                         AND run.delivery_state = 'visible'
                   ),
                   EXISTS (
                       SELECT 1
                       FROM improvement_detection_runs run
                       WHERE run.published_scrape_id =
                               publication.published_scrape_id
                         AND run.status = 'completed'
                         AND run.mode = 'execute'
                         AND NOT run.baseline_only
                         AND run.include_bands
                         AND run.include_song_events
                         AND run.include_rankings
                         AND run.notification_purpose =
                               'routine_score_observation_v1'
                         AND run.delivery_state = 'visible'
                   )
            FROM scrape_publication_state publication
            LEFT JOIN scrape_log scrape
              ON scrape.id = publication.published_scrape_id
            WHERE publication.id = TRUE
            """;
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)
            || reader.IsDBNull(0)
            || reader.GetInt64(0) != expectedPublishedScrapeId
            || reader.IsDBNull(1)
            || !reader.IsDBNull(2)
            || reader.IsDBNull(8)
            || !string.Equals(
                reader.GetString(8),
                "completed",
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Notification maintenance requires the exact completed current publication with no working publication.");
        }

        var frozen = reader.GetBoolean(3);
        if (requiredFreezeReason is null)
        {
            if (frozen)
            {
                throw new InvalidOperationException(
                    "Notification maintenance planning requires unfrozen public reads.");
            }
        }
        else if (!frozen
                 || reader.IsDBNull(4)
                 || reader.GetInt64(4) != expectedPublishedScrapeId
                 || reader.IsDBNull(5)
                 || !string.Equals(
                     reader.GetString(5),
                     requiredFreezeReason,
                     StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Notification maintenance lost its digest-owned freeze.");
        }

        if (reader.IsDBNull(6)
            || reader.GetInt64(6) != expectedPublishedScrapeId
            || reader.IsDBNull(7)
            || !string.Equals(
                reader.GetString(7),
                "completed",
                StringComparison.Ordinal)
            || !reader.GetBoolean(9)
            || !reader.GetBoolean(10))
        {
            throw new InvalidOperationException(
                "Notification maintenance requires the completed marker and completed visible routine player/band lanes.");
        }
    }

    private async Task<IReadOnlyList<MaxScoreMaintenanceCandidate>>
        LoadPlayerRankCandidatesAsync(CancellationToken ct)
        => await LoadCandidatesAsync(
            PlayerRankCandidatesSql,
            ct);

    private async Task<IReadOnlyList<MaxScoreMaintenanceCandidate>>
        LoadBandSongCandidatesAsync(CancellationToken ct)
        => await LoadCandidatesAsync(
            BandSongCandidatesSql,
            ct);

    private async Task<IReadOnlyList<MaxScoreMaintenanceCandidate>>
        LoadBandRankCandidatesAsync(CancellationToken ct)
        => await LoadCandidatesAsync(
            BandRankCandidatesSql,
            ct);

    private async Task<IReadOnlyList<MaxScoreMaintenanceCandidate>>
        LoadCandidatesAsync(
            string sql,
            CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        return await LoadCandidatesAsync(
            conn,
            transaction: null,
            sql,
            ct);
    }

    private async Task<IReadOnlyList<MaxScoreMaintenanceCandidate>>
        LoadCandidatesAsync(
            NpgsqlConnection conn,
            NpgsqlTransaction? transaction,
            string sql,
            CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandTimeout = _options.CommandTimeoutSeconds > 0
            ? _options.CommandTimeoutSeconds
            : DefaultCommandTimeoutSeconds;
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue(
            "registeredOnly",
            !string.Equals(
                _options.Scope,
                "all",
                StringComparison.OrdinalIgnoreCase));
        var candidates = new List<MaxScoreMaintenanceCandidate>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            candidates.Add(new MaxScoreMaintenanceCandidate(
                reader.GetString(0),
                reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.GetString(5),
                reader.GetString(6),
                reader.IsDBNull(7) ? null : reader.GetDecimal(7),
                reader.IsDBNull(8) ? null : reader.GetDecimal(8),
                reader.IsDBNull(9) ? null : reader.GetInt32(9),
                reader.IsDBNull(10) ? null : reader.GetInt32(10),
                reader.GetString(11),
                "routine_candidate",
                MaintenanceInduced: false,
                BlocksMaintenance: true,
                reader.IsDBNull(12) ? null : reader.GetString(12)));
        }

        return candidates
            .OrderBy(candidate => candidate.SubjectType, StringComparer.Ordinal)
            .ThenBy(candidate => candidate.SubjectKey, StringComparer.Ordinal)
            .ThenBy(candidate => candidate.Instrument, StringComparer.Ordinal)
            .ThenBy(candidate => candidate.SongId, StringComparer.Ordinal)
            .ThenBy(candidate => candidate.ScopeKey, StringComparer.Ordinal)
            .ThenBy(
                candidate => candidate.RoutineEventGroupKey,
                StringComparer.Ordinal)
            .ThenBy(candidate => candidate.CandidateKind, StringComparer.Ordinal)
            .ThenBy(candidate => candidate.Metric, StringComparer.Ordinal)
            .ToArray();
    }

    private async Task BaselineMissingBandSubjectsAsync(
        NpgsqlConnection conn,
        NpgsqlTransaction tx,
        CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandTimeout = _options.CommandTimeoutSeconds > 0
            ? _options.CommandTimeoutSeconds
            : DefaultCommandTimeoutSeconds;
        cmd.CommandText = BaselineMissingBandSubjectsSql;
        cmd.Parameters.AddWithValue(
            "registeredOnly",
            !string.Equals(
                _options.Scope,
                "all",
                StringComparison.OrdinalIgnoreCase));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private async Task<string> ComputePublishedScoreSourceFingerprintAsync(
        MaxScoreMaintenanceManifest manifest,
        CancellationToken ct)
    {
        var instruments = manifest.Songs
            .SelectMany(song => song.ChangedInstruments)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(instrument => instrument, StringComparer.Ordinal)
            .ToArray();
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = DefaultCommandTimeoutSeconds;
        cmd.CommandText = """
            WITH source_identity AS (
                SELECT COALESCE(
                    string_agg(
                        concat_ws(
                            ':',
                            song_id,
                            instrument,
                            source_kind,
                            COALESCE(source_snapshot_id::TEXT, ''),
                            COALESCE(source_scrape_id::TEXT, ''),
                            row_count,
                            COALESCE(reported_total_entries::TEXT, ''),
                            COALESCE(reported_total_pages::TEXT, '')),
                        '|' ORDER BY song_id, instrument),
                    '') AS identity
                FROM leaderboard_published_scope_source
                WHERE published_scrape_id = @publishedScrapeId
                  AND scope_kind = 'alltime'
                  AND instrument = ANY(@instruments)
            ), overlay_identity AS (
                SELECT COUNT(*)::BIGINT AS row_count,
                       COALESCE(
                           SUM(hashtextextended(
                               concat_ws(
                                   ':',
                                   song_id,
                                   instrument,
                                   account_id,
                                   score,
                                   COALESCE(accuracy::TEXT, ''),
                                   COALESCE(is_full_combo::TEXT, ''),
                                   COALESCE(stars::TEXT, ''),
                                   COALESCE(rank::TEXT, ''),
                                   COALESCE(api_rank::TEXT, ''),
                                   source_priority),
                               0)::NUMERIC),
                           0) AS hash_sum,
                       COALESCE(
                           bit_xor(hashtextextended(
                               concat_ws(
                                   ':',
                                   song_id,
                                   instrument,
                                   account_id,
                                   score,
                                   COALESCE(rank::TEXT, ''),
                                   COALESCE(api_rank::TEXT, ''),
                                   source_priority),
                               1)),
                           0) AS hash_xor,
                       MAX(last_updated_at) AS max_updated_at
                FROM leaderboard_entries_overlay
                WHERE instrument = ANY(@instruments)
            ), base_identity AS (
                SELECT COUNT(*)::BIGINT AS row_count,
                       COALESCE(
                           SUM(hashtextextended(
                               concat_ws(
                                   ':',
                                   song_id,
                                   instrument,
                                   account_id,
                                   score,
                                   COALESCE(accuracy::TEXT, ''),
                                   COALESCE(is_full_combo::TEXT, ''),
                                   COALESCE(stars::TEXT, ''),
                                   COALESCE(rank::TEXT, ''),
                                   COALESCE(api_rank::TEXT, '')),
                               0)::NUMERIC),
                           0) AS hash_sum,
                       COALESCE(
                           bit_xor(hashtextextended(
                               concat_ws(
                                   ':',
                                   song_id,
                                   instrument,
                                   account_id,
                                   score,
                                   COALESCE(rank::TEXT, ''),
                                   COALESCE(api_rank::TEXT, '')),
                               1)),
                           0) AS hash_xor,
                       MAX(last_updated_at) AS max_updated_at
                FROM leaderboard_entries
                WHERE instrument = ANY(@instruments)
            ), band_identity AS (
                SELECT COUNT(*)::BIGINT AS row_count,
                       COALESCE(
                           SUM(hashtextextended(
                               concat_ws(
                                   ':',
                                   song_id,
                                   band_type,
                                   team_key,
                                   instrument_combo,
                                   score,
                                   COALESCE(rank::TEXT, ''),
                                   COALESCE(is_full_combo::TEXT, ''),
                                   COALESCE(stars::TEXT, ''),
                                   COALESCE(difficulty::TEXT, '')),
                               0)::NUMERIC),
                           0) AS hash_sum,
                       COALESCE(
                           bit_xor(hashtextextended(
                               concat_ws(
                                   ':',
                                   song_id,
                                   band_type,
                                   team_key,
                                   instrument_combo,
                                   score,
                                   COALESCE(rank::TEXT, '')),
                               1)),
                           0) AS hash_xor
                FROM band_entries
            ), band_member_identity AS (
                SELECT COUNT(*)::BIGINT AS row_count,
                       COALESCE(
                           SUM(hashtextextended(
                               concat_ws(
                                   ':',
                                   song_id,
                                   band_type,
                                   team_key,
                                   instrument_combo,
                                   member_index,
                                   account_id,
                                   COALESCE(instrument_id::TEXT, ''),
                                   COALESCE(score::TEXT, ''),
                                   COALESCE(is_full_combo::TEXT, ''),
                                   COALESCE(difficulty::TEXT, '')),
                               0)::NUMERIC),
                           0) AS hash_sum,
                       COALESCE(
                           bit_xor(hashtextextended(
                               concat_ws(
                                   ':',
                                   song_id,
                                   band_type,
                                   team_key,
                                   instrument_combo,
                                   member_index,
                                   account_id,
                                   COALESCE(instrument_id::TEXT, ''),
                                   COALESCE(score::TEXT, '')),
                               1)),
                           0) AS hash_xor
                FROM band_member_stats
                WHERE song_id = ANY(@songIds)
            )
            SELECT encode(
                digest(
                    source_identity.identity
                    || ':'
                    || overlay_identity.row_count
                    || ':'
                    || overlay_identity.hash_sum
                    || ':'
                    || overlay_identity.hash_xor
                    || ':'
                    || COALESCE(
                        overlay_identity.max_updated_at::TEXT,
                        '')
                    || ':'
                    || base_identity.row_count
                    || ':'
                    || base_identity.hash_sum
                    || ':'
                    || base_identity.hash_xor
                    || ':'
                    || COALESCE(
                        base_identity.max_updated_at::TEXT,
                        '')
                    || ':'
                    || band_identity.row_count
                    || ':'
                    || band_identity.hash_sum
                    || ':'
                    || band_identity.hash_xor
                    || ':'
                    || band_member_identity.row_count
                    || ':'
                    || band_member_identity.hash_sum
                    || ':'
                    || band_member_identity.hash_xor,
                    'sha256'),
                'hex')
            FROM source_identity,
                 overlay_identity,
                 base_identity,
                 band_identity,
                 band_member_identity
            """;
        cmd.Parameters.AddWithValue(
            "publishedScrapeId",
            manifest.ExpectedPublishedScrapeId);
        cmd.Parameters.Add(
            "instruments",
            NpgsqlDbType.Array | NpgsqlDbType.Text).Value =
            instruments;
        cmd.Parameters.Add(
            "songIds",
            NpgsqlDbType.Array | NpgsqlDbType.Text).Value =
            manifest.Songs.Select(song => song.SongId).ToArray();
        return Convert.ToString(await cmd.ExecuteScalarAsync(ct))
            ?? throw new InvalidOperationException(
                "Published score-source fingerprint was unavailable.");
    }

    private async Task<string> ComputeNotificationStateFingerprintAsync(
        CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = DefaultCommandTimeoutSeconds;
        cmd.CommandText = """
            WITH state_rows AS (
                SELECT 'player_song' AS lane,
                       account_id || ':' || song_id || ':' || instrument AS key,
                       concat_ws(
                           ':',
                           score,
                           COALESCE(rank::TEXT, ''),
                           COALESCE(stars::TEXT, ''),
                           COALESCE(is_full_combo::TEXT, ''),
                           COALESCE(difficulty::TEXT, '')) AS value
                FROM player_improvement_state
                UNION ALL
                SELECT 'player_rank',
                       account_id || ':' || instrument,
                       concat_ws(
                           ':',
                           COALESCE(adjusted_skill_rank::TEXT, ''),
                           COALESCE(weighted_rank::TEXT, ''),
                           COALESCE(fc_rate_rank::TEXT, ''),
                           COALESCE(total_score_rank::TEXT, ''),
                           COALESCE(max_score_percent_rank::TEXT, ''),
                           COALESCE(total_score::TEXT, ''),
                           COALESCE(full_combo_count::TEXT, ''))
                FROM player_rank_improvement_state
                UNION ALL
                SELECT 'band_song',
                       band_subject_id || ':' || song_id || ':'
                           || ranking_scope || ':' || scope_combo_id,
                       concat_ws(
                           ':',
                           score,
                           COALESCE(rank::TEXT, ''),
                           COALESCE(stars::TEXT, ''),
                           COALESCE(is_full_combo::TEXT, ''),
                           COALESCE(difficulty::TEXT, ''))
                FROM band_improvement_state
                UNION ALL
                SELECT 'band_rank',
                       band_subject_id || ':' || ranking_scope || ':'
                           || combo_id,
                       concat_ws(
                           ':',
                           COALESCE(weighted_rank::TEXT, ''),
                           COALESCE(fc_rate_rank::TEXT, ''),
                           COALESCE(total_score_rank::TEXT, ''),
                           COALESCE(total_score::TEXT, ''),
                           COALESCE(full_combo_count::TEXT, ''))
                FROM band_rank_improvement_state
            ), aggregate AS (
                SELECT COUNT(*)::BIGINT AS row_count,
                       COALESCE(
                           SUM(hashtextextended(
                               lane || ':' || key || ':' || value,
                               0)::NUMERIC),
                           0) AS hash_sum,
                       COALESCE(
                           bit_xor(hashtextextended(
                               lane || ':' || key || ':' || value,
                               1)),
                           0) AS hash_xor
                FROM state_rows
            )
            SELECT encode(
                digest(
                    row_count || ':' || hash_sum || ':' || hash_xor,
                    'sha256'),
                'hex')
            FROM aggregate
            """;
        return Convert.ToString(await cmd.ExecuteScalarAsync(ct))
            ?? throw new InvalidOperationException(
                "Notification-state fingerprint was unavailable.");
    }

    private static MaxScoreMaintenanceCandidate CreateAggregateCandidate(
        string lane,
        long count)
        => new(
            SubjectType: "aggregate",
            SubjectKey: lane,
            Instrument: null,
            SongId: null,
            ScopeKey: null,
            CandidateKind: $"{lane}_candidate_count",
            Metric: "count",
            OldNumeric: 0,
            NewNumeric: count,
            OldRank: null,
            NewRank: null,
            Lane: lane,
            Classification: "unexplained_routine_candidate",
            MaintenanceInduced: false,
            BlocksMaintenance: true);

    private static string BuildCanonicalCandidateData(
        string manifestSha256,
        string planDigest,
        long publishedScrapeId,
        IReadOnlyList<MaxScoreMaintenanceCandidate> candidates)
        => JsonSerializer.Serialize(
            new
            {
                purpose = MaxScoreMaintenanceSchema.Purpose,
                cause = MaxScoreMaintenanceSchema.Cause,
                deliveryState = "quarantined",
                visibleDeliveryCap = 0,
                manifestSha256,
                planDigest,
                publishedScrapeId,
                candidates,
            },
            MaxScoreMaintenanceJson.Canonical);

    private static async Task ConfigureTransactionAsync(
        NpgsqlConnection conn,
        NpgsqlTransaction tx,
        CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            SET LOCAL lock_timeout = '5s';
            SET LOCAL statement_timeout = '600s';
            """;
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task LockAndValidateOwnedPublicationAsync(
        NpgsqlConnection conn,
        NpgsqlTransaction tx,
        long publishedScrapeId,
        long publicationId,
        string freezeReason,
        CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            SELECT current_publication_id,
                   working_publication_id,
                   published_scrape_id,
                   public_reads_frozen,
                   public_reads_frozen_scrape_id,
                   public_reads_frozen_reason
            FROM scrape_publication_state
            WHERE id = TRUE
            FOR SHARE
            """;
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)
            || reader.IsDBNull(0)
            || reader.GetInt64(0) != publicationId
            || !reader.IsDBNull(1)
            || reader.IsDBNull(2)
            || reader.GetInt64(2) != publishedScrapeId
            || !reader.GetBoolean(3)
            || reader.IsDBNull(4)
            || reader.GetInt64(4) != publishedScrapeId
            || reader.IsDBNull(5)
            || !string.Equals(
                reader.GetString(5),
                freezeReason,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Notification quarantine lost the maintenance publication/freeze identity.");
        }
    }

    private static async Task<long?> FindExistingMaintenanceRunAsync(
        NpgsqlConnection conn,
        NpgsqlTransaction tx,
        long publishedScrapeId,
        string candidateDigest,
        CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            SELECT maintenance_run_id
            FROM improvement_notification_maintenance_runs
            WHERE notification_purpose = @purpose
              AND published_scrape_id = @publishedScrapeId
              AND dry_run_digest = @digest
              AND status = 'completed'
              AND delivery_state = 'quarantined'
              AND visible_delivery_count = 0
            """;
        cmd.Parameters.AddWithValue(
            "purpose",
            MaxScoreMaintenanceSchema.Purpose);
        cmd.Parameters.AddWithValue(
            "publishedScrapeId",
            checked((int)publishedScrapeId));
        cmd.Parameters.AddWithValue("digest", candidateDigest);
        var value = await cmd.ExecuteScalarAsync(ct);
        return value is null or DBNull
            ? null
            : Convert.ToInt64(value);
    }

    private static async Task<long> InsertMaintenanceRunAsync(
        NpgsqlConnection conn,
        NpgsqlTransaction tx,
        MaxScoreMaintenanceManifest manifest,
        string candidateDigest,
        string canonicalCandidateData,
        int candidateCount,
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
                player_rank_state_rows_updated,
                visible_delivery_cap,
                visible_delivery_count,
                started_at,
                completed_at)
            VALUES (
                @purpose,
                @cause,
                'quarantined',
                @publishedScrapeId,
                @digest,
                @canonicalCandidateData,
                @manifest,
                @totalChartedSongs,
                'completed',
                @candidateCount,
                @candidateCount,
                0,
                0,
                @candidateCount,
                0,
                0,
                0,
                now(),
                now())
            RETURNING maintenance_run_id
            """;
        cmd.Parameters.AddWithValue(
            "purpose",
            MaxScoreMaintenanceSchema.Purpose);
        cmd.Parameters.AddWithValue(
            "cause",
            MaxScoreMaintenanceSchema.Cause);
        cmd.Parameters.AddWithValue(
            "publishedScrapeId",
            checked((int)manifest.ExpectedPublishedScrapeId));
        cmd.Parameters.AddWithValue("digest", candidateDigest);
        cmd.Parameters.AddWithValue(
            "canonicalCandidateData",
            canonicalCandidateData);
        cmd.Parameters.Add("manifest", NpgsqlDbType.Jsonb).Value =
            Encoding.UTF8.GetString(manifest.SerializeCanonical());
        cmd.Parameters.AddWithValue(
            "totalChartedSongs",
            manifest.CatalogSongCount);
        cmd.Parameters.AddWithValue(
            "candidateCount",
            candidateCount);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync(ct));
    }

    private static async Task InsertCandidatesAsync(
        NpgsqlConnection conn,
        NpgsqlTransaction tx,
        long maintenanceRunId,
        IReadOnlyList<MaxScoreMaintenanceCandidate> candidates,
        CancellationToken ct)
    {
        foreach (var candidate in candidates)
        {
            var payload = JsonSerializer.Serialize(
                candidate,
                MaxScoreMaintenanceJson.Canonical);
            var candidateKey = Convert.ToHexStringLower(
                SHA256.HashData(Encoding.UTF8.GetBytes(payload)));
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
                    'quarantined',
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
                    TRUE,
                    @payload)
                """;
            cmd.Parameters.AddWithValue(
                "maintenanceRunId",
                maintenanceRunId);
            cmd.Parameters.AddWithValue(
                "candidateKey",
                candidateKey);
            cmd.Parameters.AddWithValue(
                "purpose",
                MaxScoreMaintenanceSchema.Purpose);
            cmd.Parameters.AddWithValue(
                "cause",
                MaxScoreMaintenanceSchema.Cause);
            cmd.Parameters.AddWithValue(
                "subjectType",
                candidate.SubjectType);
            cmd.Parameters.AddWithValue(
                "subjectKey",
                candidate.SubjectKey);
            cmd.Parameters.Add("instrument", NpgsqlDbType.Text).Value =
                (object?)candidate.Instrument ?? DBNull.Value;
            cmd.Parameters.Add("songId", NpgsqlDbType.Text).Value =
                (object?)candidate.SongId ?? DBNull.Value;
            cmd.Parameters.Add("scopeKey", NpgsqlDbType.Text).Value =
                (object?)candidate.ScopeKey ?? DBNull.Value;
            cmd.Parameters.AddWithValue(
                "candidateKind",
                candidate.CandidateKind);
            cmd.Parameters.AddWithValue("metric", candidate.Metric);
            cmd.Parameters.Add("oldNumeric", NpgsqlDbType.Numeric).Value =
                (object?)candidate.OldNumeric ?? DBNull.Value;
            cmd.Parameters.Add("newNumeric", NpgsqlDbType.Numeric).Value =
                (object?)candidate.NewNumeric ?? DBNull.Value;
            cmd.Parameters.Add("oldRank", NpgsqlDbType.Integer).Value =
                (object?)candidate.OldRank ?? DBNull.Value;
            cmd.Parameters.Add("newRank", NpgsqlDbType.Integer).Value =
                (object?)candidate.NewRank ?? DBNull.Value;
            cmd.Parameters.AddWithValue(
                "classification",
                candidate.Classification);
            cmd.Parameters.Add("payload", NpgsqlDbType.Jsonb).Value =
                payload;
            await cmd.ExecuteNonQueryAsync(ct);
        }
    }

    private async Task<long> AlignPlayerRankStateAsync(
        NpgsqlConnection conn,
        NpgsqlTransaction tx,
        IReadOnlyList<string> instruments,
        CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            WITH current_rows AS (
                SELECT current.*
                FROM account_rankings current
                WHERE current.instrument = ANY(@instruments)
                  AND (
                      NOT @registeredOnly
                      OR EXISTS (
                          SELECT 1
                          FROM registered_users registered
                          WHERE registered.account_id =
                              current.account_id
                      )
                  )
            ), upserted AS (
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
                SELECT account_id,
                       instrument,
                       adjusted_skill_rank,
                       weighted_rank,
                       fc_rate_rank,
                       total_score_rank,
                       max_score_percent_rank,
                       total_score,
                       full_combo_count,
                       computed_at,
                       now(),
                       now()
                FROM current_rows
                ON CONFLICT (account_id, instrument) DO UPDATE SET
                    adjusted_skill_rank =
                        EXCLUDED.adjusted_skill_rank,
                    weighted_rank = EXCLUDED.weighted_rank,
                    fc_rate_rank = EXCLUDED.fc_rate_rank,
                    total_score_rank = EXCLUDED.total_score_rank,
                    max_score_percent_rank =
                        EXCLUDED.max_score_percent_rank,
                    total_score = EXCLUDED.total_score,
                    full_combo_count = EXCLUDED.full_combo_count,
                    computed_at = EXCLUDED.computed_at,
                    observed_at = EXCLUDED.observed_at,
                    updated_at = now()
                RETURNING 1
            )
            SELECT COUNT(*) FROM upserted
            """;
        cmd.Parameters.Add(
            "instruments",
            NpgsqlDbType.Array | NpgsqlDbType.Text).Value =
            instruments.ToArray();
        cmd.Parameters.AddWithValue(
            "registeredOnly",
            !string.Equals(
                _options.Scope,
                "all",
                StringComparison.OrdinalIgnoreCase));
        return Convert.ToInt64(await cmd.ExecuteScalarAsync(ct));
    }

    private async Task<long> AlignBandSongStateAsync(
        NpgsqlConnection conn,
        NpgsqlTransaction tx,
        IReadOnlySet<string> targetSongIds,
        CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            WITH current_rows AS (
                SELECT current.*,
                       subject.band_subject_id
                FROM current_band_leaderboard_entries current
                JOIN band_current_projection_scope published_scope
                  ON published_scope.song_id = current.song_id
                 AND published_scope.band_type = current.band_type
                 AND published_scope.ranking_scope =
                       current.ranking_scope
                 AND published_scope.scope_combo_id =
                       current.scope_combo_id
                 AND published_scope.published_generation =
                       current.projection_generation
                JOIN band_improvement_subjects subject
                  ON subject.band_type = current.band_type
                 AND subject.team_key = current.team_key
                WHERE current.song_id = ANY(@songIds)
                  AND (
                      NOT @registeredOnly
                      OR EXISTS (
                          SELECT 1
                          FROM registered_bands registered
                          WHERE registered.band_type =
                                  current.band_type
                            AND registered.team_key =
                                  current.team_key
                      )
                  )
            ), upserted AS (
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
                SELECT band_subject_id,
                       song_id,
                       ranking_scope,
                       COALESCE(scope_combo_id, ''),
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
                       now(),
                       now()
                FROM current_rows
                ON CONFLICT (
                    band_subject_id,
                    song_id,
                    ranking_scope,
                    scope_combo_id)
                DO UPDATE SET
                    entry_combo_id = EXCLUDED.entry_combo_id,
                    entry_instrument_combo =
                        EXCLUDED.entry_instrument_combo,
                    score = EXCLUDED.score,
                    rank = EXCLUDED.rank,
                    stars = EXCLUDED.stars,
                    is_full_combo = EXCLUDED.is_full_combo,
                    difficulty = EXCLUDED.difficulty,
                    percentile = EXCLUDED.percentile,
                    season = EXCLUDED.season,
                    total_entries = EXCLUDED.total_entries,
                    first_seen_at = EXCLUDED.first_seen_at,
                    last_updated_at = EXCLUDED.last_updated_at,
                    observed_at = EXCLUDED.observed_at,
                    updated_at = now()
                RETURNING 1
            )
            SELECT COUNT(*) FROM upserted
            """;
        cmd.Parameters.Add(
            "songIds",
            NpgsqlDbType.Array | NpgsqlDbType.Text).Value =
            targetSongIds.ToArray();
        cmd.Parameters.AddWithValue(
            "registeredOnly",
            !string.Equals(
                _options.Scope,
                "all",
                StringComparison.OrdinalIgnoreCase));
        return Convert.ToInt64(await cmd.ExecuteScalarAsync(ct));
    }

    private async Task<long> AlignBandRankStateAsync(
        NpgsqlConnection conn,
        NpgsqlTransaction tx,
        CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            WITH rankings AS (
                SELECT band_type,
                       ranking_scope,
                       combo_id,
                       team_key,
                       adjusted_skill_rank,
                       weighted_rank,
                       fc_rate_rank,
                       total_score_rank,
                       total_score,
                       full_combo_count,
                       computed_at
                FROM band_team_rankings_current_band_duets
                UNION ALL
                SELECT band_type,
                       ranking_scope,
                       combo_id,
                       team_key,
                       adjusted_skill_rank,
                       weighted_rank,
                       fc_rate_rank,
                       total_score_rank,
                       total_score,
                       full_combo_count,
                       computed_at
                FROM band_team_rankings_current_band_trios
                UNION ALL
                SELECT band_type,
                       ranking_scope,
                       combo_id,
                       team_key,
                       adjusted_skill_rank,
                       weighted_rank,
                       fc_rate_rank,
                       total_score_rank,
                       total_score,
                       full_combo_count,
                       computed_at
                FROM band_team_rankings_current_band_quad
            ), current_rows AS (
                SELECT ranking.*,
                       subject.band_subject_id
                FROM rankings ranking
                JOIN band_improvement_subjects subject
                  ON subject.band_type = ranking.band_type
                 AND subject.team_key = ranking.team_key
                WHERE NOT @registeredOnly
                   OR EXISTS (
                       SELECT 1
                       FROM registered_bands registered
                       WHERE registered.band_type =
                               ranking.band_type
                         AND registered.team_key =
                               ranking.team_key
                   )
            ), upserted AS (
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
                SELECT band_subject_id,
                       ranking_scope,
                       COALESCE(combo_id, ''),
                       adjusted_skill_rank,
                       weighted_rank,
                       fc_rate_rank,
                       total_score_rank,
                       total_score,
                       full_combo_count,
                       computed_at,
                       now(),
                       now()
                FROM current_rows
                ON CONFLICT (
                    band_subject_id,
                    ranking_scope,
                    combo_id)
                DO UPDATE SET
                    adjusted_skill_rank =
                        EXCLUDED.adjusted_skill_rank,
                    weighted_rank = EXCLUDED.weighted_rank,
                    fc_rate_rank = EXCLUDED.fc_rate_rank,
                    total_score_rank =
                        EXCLUDED.total_score_rank,
                    total_score = EXCLUDED.total_score,
                    full_combo_count =
                        EXCLUDED.full_combo_count,
                    computed_at = EXCLUDED.computed_at,
                    observed_at = EXCLUDED.observed_at,
                    updated_at = now()
                RETURNING 1
            )
            SELECT COUNT(*) FROM upserted
            """;
        cmd.Parameters.AddWithValue(
            "registeredOnly",
            !string.Equals(
                _options.Scope,
                "all",
                StringComparison.OrdinalIgnoreCase));
        return Convert.ToInt64(await cmd.ExecuteScalarAsync(ct));
    }

    private static async Task UpdateMaintenanceStateCountAsync(
        NpgsqlConnection conn,
        NpgsqlTransaction tx,
        long maintenanceRunId,
        long stateRowsUpdated,
        CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            UPDATE improvement_notification_maintenance_runs
            SET player_rank_state_rows_updated = @stateRowsUpdated
            WHERE maintenance_run_id = @maintenanceRunId
              AND notification_purpose = @purpose
              AND delivery_state = 'quarantined'
              AND visible_delivery_count = 0
            """;
        cmd.Parameters.AddWithValue(
            "stateRowsUpdated",
            stateRowsUpdated);
        cmd.Parameters.AddWithValue(
            "maintenanceRunId",
            maintenanceRunId);
        cmd.Parameters.AddWithValue(
            "purpose",
            MaxScoreMaintenanceSchema.Purpose);
        if (await cmd.ExecuteNonQueryAsync(ct) != 1)
        {
            throw new InvalidOperationException(
                "Notification maintenance audit state count was not updated.");
        }
    }

    private static async Task<long> ReadMaintenanceStateCountAsync(
        NpgsqlConnection conn,
        NpgsqlTransaction tx,
        long maintenanceRunId,
        CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            SELECT player_rank_state_rows_updated
            FROM improvement_notification_maintenance_runs
            WHERE maintenance_run_id = @maintenanceRunId
              AND visible_delivery_count = 0
            """;
        cmd.Parameters.AddWithValue(
            "maintenanceRunId",
            maintenanceRunId);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync(ct));
    }

    private static async Task ValidateMarkerStillCompletedAsync(
        NpgsqlConnection conn,
        NpgsqlTransaction tx,
        long publishedScrapeId,
        CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            SELECT improvement_notifications_scrape_id,
                   improvement_notifications_status
            FROM scrape_publication_state
            WHERE id = TRUE
            """;
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)
            || reader.IsDBNull(0)
            || reader.GetInt64(0) != publishedScrapeId
            || reader.IsDBNull(1)
            || !string.Equals(
                reader.GetString(1),
                "completed",
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Notification maintenance must not alter the completed publication marker.");
        }
    }

    private const string BaselineMissingBandSubjectsSql = """
        WITH band_rankings AS (
            SELECT band_type,
                   ranking_scope,
                   combo_id,
                   team_key,
                   team_members,
                   adjusted_skill_rank,
                   weighted_rank,
                   fc_rate_rank,
                   total_score_rank,
                   total_score,
                   full_combo_count,
                   computed_at
            FROM band_team_rankings_current_band_duets
            UNION ALL
            SELECT band_type,
                   ranking_scope,
                   combo_id,
                   team_key,
                   team_members,
                   adjusted_skill_rank,
                   weighted_rank,
                   fc_rate_rank,
                   total_score_rank,
                   total_score,
                   full_combo_count,
                   computed_at
            FROM band_team_rankings_current_band_trios
            UNION ALL
            SELECT band_type,
                   ranking_scope,
                   combo_id,
                   team_key,
                   team_members,
                   adjusted_skill_rank,
                   weighted_rank,
                   fc_rate_rank,
                   total_score_rank,
                   total_score,
                   full_combo_count,
                   computed_at
            FROM band_team_rankings_current_band_quad
        ), source_rows AS (
            SELECT current.band_type,
                   current.team_key,
                   current.team_members,
                   MIN(current.first_seen_at) AS first_seen_at,
                   MAX(current.last_updated_at) AS last_seen_at
            FROM current_band_leaderboard_entries current
            JOIN band_current_projection_scope published_scope
              ON published_scope.song_id = current.song_id
             AND published_scope.band_type = current.band_type
             AND published_scope.ranking_scope =
                   current.ranking_scope
             AND published_scope.scope_combo_id =
                   current.scope_combo_id
             AND published_scope.published_generation =
                   current.projection_generation
            WHERE NOT @registeredOnly
               OR EXISTS (
                   SELECT 1
                   FROM registered_bands registered
                   WHERE registered.band_type =
                           current.band_type
                     AND registered.team_key =
                           current.team_key
               )
            GROUP BY current.band_type,
                     current.team_key,
                     current.team_members
            UNION ALL
            SELECT ranking.band_type,
                   ranking.team_key,
                   ranking.team_members,
                   MIN(ranking.computed_at) AS first_seen_at,
                   MAX(ranking.computed_at) AS last_seen_at
            FROM band_rankings ranking
            WHERE NOT @registeredOnly
               OR EXISTS (
                   SELECT 1
                   FROM registered_bands registered
                   WHERE registered.band_type =
                           ranking.band_type
                     AND registered.team_key =
                           ranking.team_key
               )
            GROUP BY ranking.band_type,
                     ranking.team_key,
                     ranking.team_members
        ), collapsed AS (
            SELECT band_type,
                   team_key,
                   string_to_array(
                       MIN(COALESCE(
                           array_to_string(
                               team_members,
                               chr(31)),
                           '')),
                       chr(31)) AS team_members,
                   MIN(first_seen_at) AS first_seen_at,
                   MAX(last_seen_at) AS last_seen_at
            FROM source_rows
            GROUP BY band_type,
                     team_key
        ), inserted_subjects AS (
            INSERT INTO band_improvement_subjects (
                band_type,
                team_key,
                team_members,
                first_seen_at,
                last_seen_at,
                created_at,
                updated_at)
            SELECT collapsed.band_type,
                   collapsed.team_key,
                   collapsed.team_members,
                   collapsed.first_seen_at,
                   collapsed.last_seen_at,
                   now(),
                   now()
            FROM collapsed
            WHERE NOT EXISTS (
                SELECT 1
                FROM band_improvement_subjects existing
                WHERE existing.band_type =
                        collapsed.band_type
                  AND existing.team_key =
                        collapsed.team_key
            )
            ON CONFLICT (band_type, team_key) DO NOTHING
            RETURNING band_subject_id,
                      band_type,
                      team_key
        ), song_state_inserted AS (
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
                   current.song_id,
                   current.ranking_scope,
                   COALESCE(current.scope_combo_id, ''),
                   current.entry_combo_id,
                   current.entry_instrument_combo,
                   current.score,
                   current.rank,
                   current.stars,
                   current.is_full_combo,
                   current.difficulty,
                   current.percentile,
                   current.season,
                   current.total_entries,
                   current.first_seen_at,
                   current.last_updated_at,
                   now(),
                   now()
            FROM current_band_leaderboard_entries current
            JOIN band_current_projection_scope published_scope
              ON published_scope.song_id = current.song_id
             AND published_scope.band_type = current.band_type
             AND published_scope.ranking_scope =
                   current.ranking_scope
             AND published_scope.scope_combo_id =
                   current.scope_combo_id
             AND published_scope.published_generation =
                   current.projection_generation
            JOIN inserted_subjects subject
              ON subject.band_type = current.band_type
             AND subject.team_key = current.team_key
            ON CONFLICT (
                band_subject_id,
                song_id,
                ranking_scope,
                scope_combo_id)
            DO NOTHING
            RETURNING 1
        ), rank_state_inserted AS (
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
                   ranking.ranking_scope,
                   COALESCE(ranking.combo_id, ''),
                   ranking.adjusted_skill_rank,
                   ranking.weighted_rank,
                   ranking.fc_rate_rank,
                   ranking.total_score_rank,
                   ranking.total_score,
                   ranking.full_combo_count,
                   ranking.computed_at,
                   now(),
                   now()
            FROM band_rankings ranking
            JOIN inserted_subjects subject
              ON subject.band_type = ranking.band_type
             AND subject.team_key = ranking.team_key
            ON CONFLICT (
                band_subject_id,
                ranking_scope,
                combo_id)
            DO NOTHING
            RETURNING 1
        )
        SELECT (SELECT COUNT(*) FROM inserted_subjects),
               (SELECT COUNT(*) FROM song_state_inserted),
               (SELECT COUNT(*) FROM rank_state_inserted)
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
                   state.max_score_percent_rank AS
                       old_max_score_percent_rank,
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
               'player_rank'::TEXT AS lane,
               current.account_id || chr(31)
                   || current.instrument AS routine_event_group_key
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
                    AND current.adjusted_skill_rank
                        < current.old_adjusted_skill_rank
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
                    AND current.weighted_rank
                        < current.old_weighted_rank
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
                    AND current.total_score_rank
                        < current.old_total_score_rank
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
                    AND current.fc_rate_rank
                        < current.old_fc_rate_rank
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
                    AND current.full_combo_count
                        > current.old_full_combo_count
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
                        IS DISTINCT FROM
                        current.max_score_percent_rank
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
             AND published_scope.ranking_scope =
                   current.ranking_scope
             AND published_scope.scope_combo_id =
                   current.scope_combo_id
             AND published_scope.published_generation =
                   current.projection_generation
            JOIN band_improvement_subjects subject
              ON subject.band_type = current.band_type
             AND subject.team_key = current.team_key
            LEFT JOIN band_improvement_state state
              ON state.band_subject_id = subject.band_subject_id
             AND state.song_id = current.song_id
             AND state.ranking_scope = current.ranking_scope
             AND state.scope_combo_id =
                   COALESCE(current.scope_combo_id, '')
            WHERE NOT @registeredOnly
               OR EXISTS (
                   SELECT 1
                   FROM registered_bands registered
                   WHERE registered.band_type = current.band_type
                     AND registered.team_key = current.team_key
               )
        )
        SELECT 'band'::TEXT AS subject_type,
               current.band_type || ':' || current.team_key
                   AS subject_key,
               NULL::TEXT AS instrument,
               current.song_id,
               current.ranking_scope || ':'
                   || COALESCE(current.scope_combo_id, '')
                   AS scope_key,
               candidate.candidate_kind,
               candidate.metric,
               candidate.old_numeric,
               candidate.new_numeric,
               candidate.old_rank,
               candidate.new_rank,
               'band_song'::TEXT AS lane,
               concat_ws(
                   chr(31),
                   current.score::TEXT,
                   COALESCE(current.entry_combo_id, ''),
                   COALESCE(current.entry_instrument_combo, ''))
                   AS routine_event_group_key
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
                    AND current.score
                        > COALESCE(current.old_score, -1)
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
                        OR COALESCE(
                            current.old_full_combo,
                            FALSE) = FALSE
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
                    AND current.difficulty
                        > current.old_difficulty
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
        """;

    private const string BandRankCandidatesSql = """
        WITH rankings AS (
            SELECT band_type,
                   ranking_scope,
                   combo_id,
                   team_key,
                   weighted_rank,
                   fc_rate_rank,
                   total_score_rank,
                   total_score,
                   full_combo_count
            FROM band_team_rankings_current_band_duets
            UNION ALL
            SELECT band_type,
                   ranking_scope,
                   combo_id,
                   team_key,
                   weighted_rank,
                   fc_rate_rank,
                   total_score_rank,
                   total_score,
                   full_combo_count
            FROM band_team_rankings_current_band_trios
            UNION ALL
            SELECT band_type,
                   ranking_scope,
                   combo_id,
                   team_key,
                   weighted_rank,
                   fc_rate_rank,
                   total_score_rank,
                   total_score,
                   full_combo_count
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
               current.band_type || ':' || current.team_key
                   AS subject_key,
               NULL::TEXT AS instrument,
               NULL::TEXT AS song_id,
               current.ranking_scope || ':'
                   || COALESCE(current.combo_id, '')
                   AS scope_key,
               candidate.candidate_kind,
               candidate.metric,
               candidate.old_numeric,
               candidate.new_numeric,
               candidate.old_rank,
               candidate.new_rank,
               'band_rank'::TEXT AS lane,
               current.ranking_scope || chr(31)
                   || COALESCE(current.combo_id, '')
                   AS routine_event_group_key
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
                    AND current.weighted_rank
                        < current.old_weighted_rank
                    AND (
                        (
                            current.old_total_score IS NOT NULL
                            AND current.total_score IS NOT NULL
                            AND current.total_score
                                > current.old_total_score
                        )
                        OR (
                            current.old_full_combo_count IS NOT NULL
                            AND current.full_combo_count IS NOT NULL
                            AND current.full_combo_count
                                > current.old_full_combo_count
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
                    AND current.total_score_rank
                        < current.old_total_score_rank
                    AND current.old_total_score IS NOT NULL
                    AND current.total_score IS NOT NULL
                    AND current.total_score
                        > current.old_total_score
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
                    AND current.fc_rate_rank
                        < current.old_fc_rate_rank
                    AND current.old_full_combo_count IS NOT NULL
                    AND current.full_combo_count IS NOT NULL
                    AND current.full_combo_count
                        > current.old_full_combo_count
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
                    AND current.total_score
                        > current.old_total_score
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
                    AND current.full_combo_count
                        > current.old_full_combo_count
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
        """;
}
