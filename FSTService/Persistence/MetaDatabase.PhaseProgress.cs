using Npgsql;

namespace FSTService.Persistence;

public sealed partial class MetaDatabase
{
    public int InterruptOrphanedScrapePhaseAttempts(
        string workerInstanceId,
        DateTime interruptedAtUtc,
        string reason)
    {
        var now = NormalizeUtc(interruptedAtUtc);
        using var conn = _ds.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE scrape_phase_attempts
            SET status = 'interrupted',
                last_progress_at = GREATEST(last_progress_at, @now),
                heartbeat_at = GREATEST(heartbeat_at, @now),
                completed_at = @now,
                warning_message = COALESCE(warning_message, @reason)
            WHERE status = 'running'
              AND worker_instance_id IS DISTINCT FROM @workerInstanceId
            """;
        cmd.Parameters.AddWithValue("now", now);
        cmd.Parameters.AddWithValue("reason", reason);
        cmd.Parameters.AddWithValue("workerInstanceId", workerInstanceId);
        return cmd.ExecuteNonQuery();
    }

    public int StartScrapePhaseAttempt(ScrapePhaseAttemptStart attempt)
    {
        for (var retry = 0; retry < 3; retry++)
        {
            try
            {
                using var conn = _ds.OpenConnection();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = """
                    WITH next_attempt AS (
                        SELECT COALESCE(MAX(attempt), 0) + 1 AS attempt
                        FROM scrape_phase_attempts
                        WHERE scrape_id = @scrapeId
                          AND phase_id = @phaseId
                    )
                    INSERT INTO scrape_phase_attempts (
                        scrape_id, phase_id, attempt, operation_id,
                        phase_ordinal, plan_version, worker_instance_id,
                        current_subphase_id, status, units_kind,
                        units_completed, units_total, units_total_final,
                        phase_percent, overall_percent_kind, overall_percent,
                        overall_model_version, eta_lower_seconds,
                        eta_upper_seconds, eta_confidence, eta_sample_count,
                        started_at, last_progress_at, heartbeat_at,
                        build_id, config_id)
                    SELECT
                        @scrapeId, @phaseId, next_attempt.attempt, @operationId,
                        @phaseOrdinal, @planVersion, @workerInstanceId,
                        @currentSubphaseId, @status, @unitsKind,
                        @unitsCompleted, @unitsTotal, @unitsTotalFinal,
                        @phasePercent, @overallPercentKind, @overallPercent,
                        @overallModelVersion, @etaLowerSeconds,
                        @etaUpperSeconds, @etaConfidence, @etaSampleCount,
                        @startedAt, @lastProgressAt, @heartbeatAt,
                        @buildId, @configId
                    FROM next_attempt
                    RETURNING attempt
                    """;
                AddPhaseAttemptStartParameters(cmd, attempt);
                return Convert.ToInt32(cmd.ExecuteScalar());
            }
            catch (PostgresException ex) when (
                ex.SqlState == PostgresErrorCodes.UniqueViolation
                && retry < 2)
            {
            }
        }

        throw new InvalidOperationException(
            $"Unable to allocate a phase attempt for scrape {attempt.ScrapeId}, phase {attempt.PhaseId}.");
    }

    public bool UpdateScrapePhaseAttemptProgress(ScrapePhaseAttemptProgress progress)
    {
        using var conn = _ds.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE scrape_phase_attempts
            SET current_subphase_id = @currentSubphaseId,
                units_kind = @unitsKind,
                units_completed = @unitsCompleted,
                units_total = @unitsTotal,
                units_total_final = @unitsTotalFinal,
                phase_percent = @phasePercent,
                overall_percent_kind = @overallPercentKind,
                overall_percent = @overallPercent,
                overall_model_version = @overallModelVersion,
                eta_lower_seconds = @etaLowerSeconds,
                eta_upper_seconds = @etaUpperSeconds,
                eta_confidence = @etaConfidence,
                eta_sample_count = @etaSampleCount,
                last_progress_at = GREATEST(
                    last_progress_at,
                    @lastProgressAt),
                heartbeat_at = GREATEST(heartbeat_at, @heartbeatAt)
            WHERE scrape_id = @scrapeId
              AND phase_id = @phaseId
              AND attempt = @attempt
              AND status = 'running'
            """;
        cmd.Parameters.AddWithValue("scrapeId", progress.ScrapeId);
        cmd.Parameters.AddWithValue("phaseId", progress.PhaseId);
        cmd.Parameters.AddWithValue("attempt", progress.Attempt);
        cmd.Parameters.AddWithValue("currentSubphaseId", (object?)progress.CurrentSubphaseId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("unitsKind", (object?)progress.UnitsKind ?? DBNull.Value);
        cmd.Parameters.AddWithValue("unitsCompleted", (object?)progress.UnitsCompleted ?? DBNull.Value);
        cmd.Parameters.AddWithValue("unitsTotal", (object?)progress.UnitsTotal ?? DBNull.Value);
        cmd.Parameters.AddWithValue("unitsTotalFinal", progress.UnitsTotalFinal);
        cmd.Parameters.AddWithValue("phasePercent", (object?)progress.PhasePercent ?? DBNull.Value);
        cmd.Parameters.AddWithValue("overallPercentKind", progress.OverallPercentKind);
        cmd.Parameters.AddWithValue("overallPercent", (object?)progress.OverallPercent ?? DBNull.Value);
        cmd.Parameters.AddWithValue("overallModelVersion", (object?)progress.OverallModelVersion ?? DBNull.Value);
        cmd.Parameters.AddWithValue("etaLowerSeconds", (object?)progress.EtaLowerSeconds ?? DBNull.Value);
        cmd.Parameters.AddWithValue("etaUpperSeconds", (object?)progress.EtaUpperSeconds ?? DBNull.Value);
        cmd.Parameters.AddWithValue("etaConfidence", (object?)progress.EtaConfidence ?? DBNull.Value);
        cmd.Parameters.AddWithValue("etaSampleCount", (object?)progress.EtaSampleCount ?? DBNull.Value);
        cmd.Parameters.AddWithValue("lastProgressAt", NormalizeUtc(progress.LastProgressAtUtc));
        cmd.Parameters.AddWithValue("heartbeatAt", NormalizeUtc(progress.HeartbeatAtUtc));
        return cmd.ExecuteNonQuery() == 1;
    }

    public int HeartbeatScrapePhaseAttempts(
        long scrapeId,
        string workerInstanceId,
        DateTime heartbeatAtUtc)
    {
        using var conn = _ds.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE scrape_phase_attempts
            SET heartbeat_at = GREATEST(heartbeat_at, @heartbeatAt)
            WHERE scrape_id = @scrapeId
              AND worker_instance_id = @workerInstanceId
              AND status = 'running'
            """;
        cmd.Parameters.AddWithValue("scrapeId", scrapeId);
        cmd.Parameters.AddWithValue("workerInstanceId", workerInstanceId);
        cmd.Parameters.AddWithValue("heartbeatAt", NormalizeUtc(heartbeatAtUtc));
        return cmd.ExecuteNonQuery();
    }

    public bool CompleteScrapePhaseAttempt(ScrapePhaseAttemptCompletion completion)
    {
        using var conn = _ds.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE scrape_phase_attempts
            SET status = @status,
                last_progress_at = GREATEST(last_progress_at, @lastProgressAt),
                heartbeat_at = GREATEST(heartbeat_at, @heartbeatAt),
                completed_at = @completedAt,
                warning_message = @warningMessage,
                error_message = @errorMessage
            WHERE scrape_id = @scrapeId
              AND phase_id = @phaseId
              AND attempt = @attempt
              AND status = 'running'
            """;
        cmd.Parameters.AddWithValue("scrapeId", completion.ScrapeId);
        cmd.Parameters.AddWithValue("phaseId", completion.PhaseId);
        cmd.Parameters.AddWithValue("attempt", completion.Attempt);
        cmd.Parameters.AddWithValue("status", completion.Status);
        cmd.Parameters.AddWithValue("lastProgressAt", NormalizeUtc(completion.LastProgressAtUtc));
        cmd.Parameters.AddWithValue("heartbeatAt", NormalizeUtc(completion.HeartbeatAtUtc));
        cmd.Parameters.AddWithValue("completedAt", NormalizeUtc(completion.CompletedAtUtc));
        cmd.Parameters.AddWithValue("warningMessage", (object?)completion.WarningMessage ?? DBNull.Value);
        cmd.Parameters.AddWithValue("errorMessage", (object?)completion.ErrorMessage ?? DBNull.Value);
        return cmd.ExecuteNonQuery() == 1;
    }

    public IReadOnlyList<PhaseDurationSample> GetSuccessfulPhaseDurationSamples(
        string phaseId,
        string planVersion,
        string? configId,
        int limit)
    {
        using var conn = _ds.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT
                GREATEST(
                    0,
                    round(EXTRACT(EPOCH FROM (completed_at - started_at)) * 1000)
                )::BIGINT AS duration_ms,
                units_kind,
                units_total,
                units_total_final
            FROM scrape_phase_attempts
            WHERE phase_id = @phaseId
              AND plan_version = @planVersion
              AND config_id IS NOT DISTINCT FROM @configId
              AND status = 'completed'
              AND completed_at IS NOT NULL
            ORDER BY completed_at DESC
            LIMIT @limit
            """;
        cmd.Parameters.AddWithValue("phaseId", phaseId);
        cmd.Parameters.AddWithValue("planVersion", planVersion);
        cmd.Parameters.AddWithValue("configId", (object?)configId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("limit", Math.Clamp(limit, 1, 100));

        var samples = new List<PhaseDurationSample>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            samples.Add(new PhaseDurationSample(
                reader.GetInt64(0),
                reader.IsDBNull(1) ? null : reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetInt64(2),
                reader.GetBoolean(3)));
        }
        return samples;
    }

    private static void AddPhaseAttemptStartParameters(
        NpgsqlCommand cmd,
        ScrapePhaseAttemptStart attempt)
    {
        cmd.Parameters.AddWithValue("scrapeId", attempt.ScrapeId);
        cmd.Parameters.AddWithValue("phaseId", attempt.PhaseId);
        cmd.Parameters.AddWithValue("operationId", attempt.OperationId);
        cmd.Parameters.AddWithValue("phaseOrdinal", attempt.PhaseOrdinal);
        cmd.Parameters.AddWithValue("planVersion", attempt.PlanVersion);
        cmd.Parameters.AddWithValue("workerInstanceId", attempt.WorkerInstanceId);
        cmd.Parameters.AddWithValue("currentSubphaseId", (object?)attempt.CurrentSubphaseId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("status", attempt.Status);
        cmd.Parameters.AddWithValue("unitsKind", (object?)attempt.UnitsKind ?? DBNull.Value);
        cmd.Parameters.AddWithValue("unitsCompleted", (object?)attempt.UnitsCompleted ?? DBNull.Value);
        cmd.Parameters.AddWithValue("unitsTotal", (object?)attempt.UnitsTotal ?? DBNull.Value);
        cmd.Parameters.AddWithValue("unitsTotalFinal", attempt.UnitsTotalFinal);
        cmd.Parameters.AddWithValue("phasePercent", (object?)attempt.PhasePercent ?? DBNull.Value);
        cmd.Parameters.AddWithValue("overallPercentKind", attempt.OverallPercentKind);
        cmd.Parameters.AddWithValue("overallPercent", (object?)attempt.OverallPercent ?? DBNull.Value);
        cmd.Parameters.AddWithValue("overallModelVersion", (object?)attempt.OverallModelVersion ?? DBNull.Value);
        cmd.Parameters.AddWithValue("etaLowerSeconds", (object?)attempt.EtaLowerSeconds ?? DBNull.Value);
        cmd.Parameters.AddWithValue("etaUpperSeconds", (object?)attempt.EtaUpperSeconds ?? DBNull.Value);
        cmd.Parameters.AddWithValue("etaConfidence", (object?)attempt.EtaConfidence ?? DBNull.Value);
        cmd.Parameters.AddWithValue("etaSampleCount", (object?)attempt.EtaSampleCount ?? DBNull.Value);
        cmd.Parameters.AddWithValue("startedAt", NormalizeUtc(attempt.StartedAtUtc));
        cmd.Parameters.AddWithValue("lastProgressAt", NormalizeUtc(attempt.LastProgressAtUtc));
        cmd.Parameters.AddWithValue("heartbeatAt", NormalizeUtc(attempt.HeartbeatAtUtc));
        cmd.Parameters.AddWithValue("buildId", (object?)attempt.BuildId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("configId", (object?)attempt.ConfigId ?? DBNull.Value);
    }

    private static ScrapePhaseAttemptInfo? ReadScrapePhaseAttempt(
        NpgsqlDataReader reader,
        int ordinal)
    {
        if (reader.IsDBNull(ordinal))
            return null;

        return new ScrapePhaseAttemptInfo
        {
            ScrapeId = reader.GetInt64(ordinal),
            PhaseId = reader.GetString(ordinal + 1),
            Attempt = reader.GetInt32(ordinal + 2),
            OperationId = reader.GetString(ordinal + 3),
            PhaseOrdinal = reader.GetInt32(ordinal + 4),
            PlanVersion = reader.GetString(ordinal + 5),
            WorkerInstanceId = reader.GetString(ordinal + 6),
            CurrentSubphaseId = reader.IsDBNull(ordinal + 7) ? null : reader.GetString(ordinal + 7),
            Status = reader.GetString(ordinal + 8),
            UnitsKind = reader.IsDBNull(ordinal + 9) ? null : reader.GetString(ordinal + 9),
            UnitsCompleted = reader.IsDBNull(ordinal + 10) ? null : reader.GetInt64(ordinal + 10),
            UnitsTotal = reader.IsDBNull(ordinal + 11) ? null : reader.GetInt64(ordinal + 11),
            UnitsTotalFinal = reader.GetBoolean(ordinal + 12),
            PhasePercent = reader.IsDBNull(ordinal + 13) ? null : reader.GetDouble(ordinal + 13),
            OverallPercentKind = reader.GetString(ordinal + 14),
            OverallPercent = reader.IsDBNull(ordinal + 15) ? null : reader.GetDouble(ordinal + 15),
            OverallModelVersion = reader.IsDBNull(ordinal + 16) ? null : reader.GetString(ordinal + 16),
            EtaLowerSeconds = reader.IsDBNull(ordinal + 17) ? null : reader.GetDouble(ordinal + 17),
            EtaUpperSeconds = reader.IsDBNull(ordinal + 18) ? null : reader.GetDouble(ordinal + 18),
            EtaConfidence = reader.IsDBNull(ordinal + 19) ? null : reader.GetString(ordinal + 19),
            EtaSampleCount = reader.IsDBNull(ordinal + 20) ? null : reader.GetInt32(ordinal + 20),
            StartedAtUtc = GetUtc(reader, ordinal + 21),
            LastProgressAtUtc = GetUtc(reader, ordinal + 22),
            HeartbeatAtUtc = GetUtc(reader, ordinal + 23),
            CompletedAtUtc = GetNullableUtc(reader, ordinal + 24),
            BuildId = reader.IsDBNull(ordinal + 25) ? null : reader.GetString(ordinal + 25),
            ConfigId = reader.IsDBNull(ordinal + 26) ? null : reader.GetString(ordinal + 26),
            WarningMessage = reader.IsDBNull(ordinal + 27) ? null : reader.GetString(ordinal + 27),
            ErrorMessage = reader.IsDBNull(ordinal + 28) ? null : reader.GetString(ordinal + 28),
        };
    }
}
