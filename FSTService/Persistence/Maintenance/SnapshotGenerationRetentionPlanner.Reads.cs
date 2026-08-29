using System.Text.Json;
using System.Text.RegularExpressions;
using Npgsql;

namespace FSTService.Persistence.Maintenance;

public sealed partial class SnapshotGenerationRetentionPlanner
{
    private const string SnapshotParent =
        "leaderboard_entries_snapshot";
    private static readonly Regex GenerationChildPattern = new(
        "^(?<root>leaderboard_entries_snapshot_[a-z0-9_]+)_s(?<snapshot>[1-9][0-9]*)$",
        RegexOptions.CultureInvariant
        | RegexOptions.Compiled);

    private static async Task<SafePointState>
        LoadSafePointStateAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            SnapshotGenerationRetentionPlanRequest request,
            long configuredResumeScrapeId,
            int commandTimeoutSeconds,
            CancellationToken ct)
    {
        long? publishedScrapeId = null;
        long? currentPublicationId = null;
        long? previousPublicationId = null;
        long? workingPublicationId = null;
        var publicReadsFrozen = false;
        DateTime? commitIntentStartedAt = null;
        DateTime? commitIntentHeartbeatAt = null;
        string? commitIntentOwner = null;
        string? maxScoreGateToken = null;
        long? maxScoreGatePublicationId = null;
        int? maxScoreGateBackendPid = null;
        DateTime? maxScoreGateBackendStart = null;
        DateTime? maxScoreGateAcquiredAt = null;
        long? notificationScrapeId = null;
        string? notificationStatus = null;
        DateTime? notificationCompletedAt = null;
        var notificationProjectionReady = false;
        long? notificationProjectionScrapeId = null;
        DateTime? publicReadsFrozenAt = null;
        long? publicReadsFrozenScrapeId = null;
        string? publicReadsFrozenReason = null;
        var deferrals =
            new List<SnapshotGenerationRetentionBlocker>();
        var blockers =
            new List<SnapshotGenerationRetentionBlocker>();
        var anomalies =
            new List<SnapshotGenerationRetentionAnomaly>();

        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandTimeout = commandTimeoutSeconds;
            command.CommandText = """
                SELECT
                    published_scrape_id::BIGINT,
                    current_publication_id,
                    previous_publication_id,
                    working_publication_id,
                    public_reads_frozen,
                    publication_commit_intent_started_at,
                    publication_commit_intent_heartbeat_at,
                    publication_commit_intent_owner,
                    max_score_mutation_gate_token,
                    max_score_mutation_gate_publication_id,
                    max_score_mutation_gate_backend_pid,
                    max_score_mutation_gate_backend_start,
                    max_score_mutation_gate_acquired_at,
                    improvement_notifications_scrape_id::BIGINT,
                    improvement_notifications_status,
                    improvement_notifications_completed_at,
                    improvement_notifications_projection_ready,
                    improvement_notifications_projection_scrape_id::BIGINT,
                    public_reads_frozen_at,
                    public_reads_frozen_scrape_id::BIGINT,
                    public_reads_frozen_reason
                FROM scrape_publication_state
                WHERE id = TRUE
                """;
            await using var reader =
                await command.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct))
            {
                blockers.Add(
                    new SnapshotGenerationRetentionBlocker(
                        "publication_state_missing",
                        "The scrape publication singleton is missing."));
            }
            else
            {
                publishedScrapeId =
                    reader.IsDBNull(0)
                        ? null
                        : reader.GetInt64(0);
                currentPublicationId =
                    reader.IsDBNull(1)
                        ? null
                        : reader.GetInt64(1);
                previousPublicationId =
                    reader.IsDBNull(2)
                        ? null
                        : reader.GetInt64(2);
                workingPublicationId =
                    reader.IsDBNull(3)
                        ? null
                        : reader.GetInt64(3);
                publicReadsFrozen = reader.GetBoolean(4);
                commitIntentStartedAt =
                    reader.IsDBNull(5)
                        ? null
                        : reader.GetDateTime(5);
                commitIntentHeartbeatAt =
                    reader.IsDBNull(6)
                        ? null
                        : reader.GetDateTime(6);
                commitIntentOwner =
                    reader.IsDBNull(7)
                        ? null
                        : reader.GetString(7);
                maxScoreGateToken =
                    reader.IsDBNull(8)
                        ? null
                        : reader.GetString(8);
                maxScoreGatePublicationId =
                    reader.IsDBNull(9)
                        ? null
                        : reader.GetInt64(9);
                maxScoreGateBackendPid =
                    reader.IsDBNull(10)
                        ? null
                        : reader.GetInt32(10);
                maxScoreGateBackendStart =
                    reader.IsDBNull(11)
                        ? null
                        : reader.GetDateTime(11);
                maxScoreGateAcquiredAt =
                    reader.IsDBNull(12)
                        ? null
                        : reader.GetDateTime(12);
                notificationScrapeId =
                    reader.IsDBNull(13)
                        ? null
                        : reader.GetInt64(13);
                notificationStatus =
                    reader.IsDBNull(14)
                        ? null
                        : reader.GetString(14);
                notificationCompletedAt =
                    reader.IsDBNull(15)
                        ? null
                        : reader.GetDateTime(15);
                notificationProjectionReady =
                    reader.GetBoolean(16);
                notificationProjectionScrapeId =
                    reader.IsDBNull(17)
                        ? null
                        : reader.GetInt64(17);
                publicReadsFrozenAt =
                    reader.IsDBNull(18)
                        ? null
                        : reader.GetDateTime(18);
                publicReadsFrozenScrapeId =
                    reader.IsDBNull(19)
                        ? null
                        : reader.GetInt64(19);
                publicReadsFrozenReason =
                    reader.IsDBNull(20)
                        ? null
                        : reader.GetString(20);
            }
        }

        if (publishedScrapeId != request.TriggerScrapeId
            || currentPublicationId !=
                request.TriggerPublicationId)
        {
            blockers.Add(
                new SnapshotGenerationRetentionBlocker(
                    "safe_point_mismatch",
                    $"Requested scrape/publication {request.TriggerScrapeId}/{request.TriggerPublicationId} does not match current {publishedScrapeId?.ToString() ?? "null"}/{currentPublicationId?.ToString() ?? "null"}."));
        }
        if (publicReadsFrozen)
        {
            deferrals.Add(
                new SnapshotGenerationRetentionBlocker(
                    "public_reads_frozen",
                    "Public reads remain frozen."));
        }
        if (workingPublicationId.HasValue)
        {
            deferrals.Add(
                new SnapshotGenerationRetentionBlocker(
                    "working_publication_present",
                    $"Working publication {workingPublicationId.Value} remains unresolved."));
        }
        if (commitIntentStartedAt.HasValue
            || commitIntentHeartbeatAt.HasValue
            || !string.IsNullOrWhiteSpace(
                commitIntentOwner))
        {
            deferrals.Add(
                new SnapshotGenerationRetentionBlocker(
                    "publication_commit_intent_present",
                    "Publication commit-intent state remains active or unreconciled."));
        }
        if (!string.IsNullOrWhiteSpace(maxScoreGateToken)
            || maxScoreGatePublicationId.HasValue
            || maxScoreGateBackendPid.HasValue
            || maxScoreGateBackendStart.HasValue
            || maxScoreGateAcquiredAt.HasValue)
        {
            deferrals.Add(
                new SnapshotGenerationRetentionBlocker(
                    "max_score_mutation_gate_present",
                    "The durable max-score mutation gate is active or unreconciled."));
        }

        var notificationsCompleted =
            notificationScrapeId == publishedScrapeId
            && string.Equals(
                notificationStatus,
                "completed",
                StringComparison.Ordinal)
            && notificationCompletedAt.HasValue
            && notificationProjectionReady
            && notificationProjectionScrapeId ==
                publishedScrapeId;
        var notificationsDisabled =
            string.Equals(
                notificationStatus,
                "disabled",
                StringComparison.Ordinal)
            && !notificationScrapeId.HasValue
            && !notificationCompletedAt.HasValue
            && !notificationProjectionReady
            && !notificationProjectionScrapeId.HasValue;
        if (!notificationsCompleted && !notificationsDisabled)
        {
            var notificationsRetryable =
                notificationScrapeId == publishedScrapeId
                && notificationStatus is
                    "pending"
                    or "running"
                    or "failed"
                && notificationProjectionReady
                && notificationProjectionScrapeId ==
                    publishedScrapeId;
            var notificationBlocker =
                new SnapshotGenerationRetentionBlocker(
                    notificationsRetryable
                        ? "improvement_notifications_incomplete"
                        : "improvement_notifications_terminal_state_invalid",
                    notificationsRetryable
                        ? $"Improvement notifications remain in recoverable {notificationStatus} state for published scrape {publishedScrapeId?.ToString() ?? "null"}."
                        : $"Improvement notification state {notificationStatus ?? "null"} is terminal, missing, malformed, or not aligned to published scrape {publishedScrapeId?.ToString() ?? "null"}; automatic recovery cannot safely make this retention cycle clean.");
            if (notificationsRetryable)
                deferrals.Add(notificationBlocker);
            else
                blockers.Add(notificationBlocker);
        }

        var registrationDrain =
            await LoadRegistrationDrainAsync(
                connection,
                transaction,
                commandTimeoutSeconds,
                ct);
        var registrationTerminalBlockers =
            registrationDrain.TerminalBlockers;
        blockers.AddRange(registrationTerminalBlockers);
        if (registrationDrain.RunnableBackfills > 0
            || registrationDrain.RepairableHistory > 0)
        {
            var incomplete =
                new SnapshotGenerationRetentionBlocker(
                    "registration_drain_incomplete",
                    $"Registration drain has {registrationDrain.RunnableBackfills} runnable backfill and {registrationDrain.RepairableHistory} safely repairable history account(s) remaining.");
            if (registrationTerminalBlockers.Count == 0)
                deferrals.Add(incomplete);
            else
                blockers.Add(incomplete);
        }

        var generations = new List<PublicationGenerationRow>();
        await using (var bindingCommand =
                     connection.CreateCommand())
        {
            bindingCommand.Transaction = transaction;
            bindingCommand.CommandTimeout =
                commandTimeoutSeconds;
            bindingCommand.CommandText = """
                SELECT
                    publication_id,
                    scrape_id,
                    status,
                    previous_publication_id,
                    metadata #>>
                        '{publicationPreparation,expectedPublishedScopeCount}',
                    metadata #>>
                        '{publicationPreparation,scrapeId}',
                    metadata #>>
                        '{publicationPreparation,publicationId}'
                FROM publication_generations
                ORDER BY publication_id
                """;
            await using var reader =
                await bindingCommand.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                generations.Add(
                    new PublicationGenerationRow(
                        reader.GetInt64(0),
                        reader.IsDBNull(1)
                            ? null
                            : reader.GetInt64(1),
                        reader.GetString(2),
                        reader.IsDBNull(3)
                            ? null
                            : reader.GetInt64(3),
                        reader.IsDBNull(4)
                            ? null
                            : reader.GetString(4),
                        reader.IsDBNull(5)
                            ? null
                            : reader.GetString(5),
                        reader.IsDBNull(6)
                            ? null
                            : reader.GetString(6)));
            }
        }

        var pointerIds = new[]
            {
                currentPublicationId,
                previousPublicationId,
                workingPublicationId,
            }
            .Where(static id => id.HasValue)
            .Select(static id => id!.Value)
            .ToArray();
        if (pointerIds.Distinct().Count() != pointerIds.Length)
        {
            blockers.Add(
                new SnapshotGenerationRetentionBlocker(
                    "publication_pointer_duplicate",
                    "Current, previous, and working publication pointers are not distinct."));
        }

        var byId = generations.ToDictionary(
            static generation => generation.PublicationId);
        var namedPublications =
            new List<NamedPublicationDescriptor>();
        ValidateNamed(
            "current",
            currentPublicationId,
            required: true,
            [PublicationGenerationStatus.Current]);
        ValidateNamed(
            "previous",
            previousPublicationId,
            required: false,
            [PublicationGenerationStatus.Retained]);
        ValidateNamed(
            "working",
            workingPublicationId,
            required: false,
            [
                PublicationGenerationStatus.Building,
                PublicationGenerationStatus.Ready,
            ]);
        if (currentPublicationId.HasValue
            && byId.TryGetValue(
                currentPublicationId.Value,
                out var currentGeneration))
        {
            if (currentGeneration.ScrapeId !=
                publishedScrapeId)
            {
                blockers.Add(
                    new SnapshotGenerationRetentionBlocker(
                        "current_publication_scrape_mismatch",
                        $"Current publication {currentPublicationId.Value} belongs to scrape {currentGeneration.ScrapeId?.ToString() ?? "null"}, not published scrape {publishedScrapeId?.ToString() ?? "null"}."));
            }
            if (currentGeneration.PreviousPublicationId !=
                previousPublicationId)
            {
                blockers.Add(
                    new SnapshotGenerationRetentionBlocker(
                        "publication_predecessor_mismatch",
                        $"Current publication {currentPublicationId.Value} records predecessor {currentGeneration.PreviousPublicationId?.ToString() ?? "null"}, not the previous pointer {previousPublicationId?.ToString() ?? "null"}."));
            }
        }

        var namedPublicationIds =
            pointerIds.ToHashSet();
        var failedPublicationEvidence =
            await LoadFailedPublicationRecoveryEvidenceAsync(
                connection,
                transaction,
                generations
                    .Where(static generation =>
                        generation.Status ==
                            PublicationGenerationStatus.Failed)
                    .Select(static generation =>
                        generation.PublicationId)
                    .ToArray(),
                currentPublicationId,
                previousPublicationId,
                workingPublicationId,
                publishedScrapeId,
                publicReadsFrozen,
                publicReadsFrozenAt,
                publicReadsFrozenScrapeId,
                publicReadsFrozenReason,
                commitIntentStartedAt,
                commitIntentHeartbeatAt,
                commitIntentOwner,
                maxScoreGatePublicationId,
                notificationScrapeId,
                notificationProjectionScrapeId,
                configuredResumeScrapeId,
                commandTimeoutSeconds,
                ct);

        foreach (var generation in generations.Where(
                     generation =>
                         namedPublicationIds.Contains(
                             generation.PublicationId)
                         && generation.Status ==
                            PublicationGenerationStatus.Failed))
        {
            if (!failedPublicationEvidence.TryGetValue(
                    generation.PublicationId,
                    out var evidence))
            {
                throw new InvalidOperationException(
                    $"Failed publication {generation.PublicationId} was not returned by the recovery-evidence inventory.");
            }

            blockers.Add(
                new SnapshotGenerationRetentionBlocker(
                    "named_failed_publication",
                    $"Failed publication {generation.PublicationId}/{generation.ScrapeId?.ToString() ?? "null"} is still named by {string.Join(", ", evidence.NamedPointerSlots)} and cannot be classified as terminal orphan evidence.",
                    evidence));
        }

        foreach (var generation in generations.Where(
                     generation =>
                         !namedPublicationIds.Contains(
                             generation.PublicationId)))
        {
            if (generation.Status ==
                PublicationGenerationStatus.Retained)
            {
                anomalies.Add(
                    new SnapshotGenerationRetentionAnomaly(
                        "unpointed_retained_publication",
                        $"Retained publication {generation.PublicationId} is not named by current/previous/working; it is stale-bookkeeping evidence, not a liveness root or blocker.",
                        generation.PublicationId,
                        generation.ScrapeId,
                        generation.Status));
            }
            else if (generation.Status is
                     PublicationGenerationStatus.Building
                     or PublicationGenerationStatus.Ready
                     or PublicationGenerationStatus.Current)
            {
                blockers.Add(
                    new SnapshotGenerationRetentionBlocker(
                        "unpointed_nonterminal_publication",
                        $"Publication {generation.PublicationId} has nonterminal status {generation.Status} but is not named by current/previous/working."));
            }
            else if (generation.Status ==
                     PublicationGenerationStatus.Failed)
            {
                if (!failedPublicationEvidence.TryGetValue(
                        generation.PublicationId,
                        out var evidence))
                {
                    throw new InvalidOperationException(
                        $"Failed publication {generation.PublicationId} was not returned by the recovery-evidence inventory.");
                }

                if (evidence.TerminalFailureIdentityValid
                    && evidence.RecoveryReasons.Count == 0)
                {
                    anomalies.Add(
                        new SnapshotGenerationRetentionAnomaly(
                            "unpointed_terminal_failed_publication",
                            $"Terminal failed publication {generation.PublicationId}/{generation.ScrapeId?.ToString() ?? "null"} is unnamed and has no live recovery artifacts; {evidence.PublishedSourceRowCount} source-map row(s) and {evidence.UnreplayedWriterFailureCount} unreplayed writer failure row(s) remain immutable provenance rather than global liveness.",
                            generation.PublicationId,
                            generation.ScrapeId,
                            generation.Status,
                            evidence));
                }
                else
                {
                    blockers.Add(
                        new SnapshotGenerationRetentionBlocker(
                            "unpointed_failed_publication",
                            $"Failed publication {generation.PublicationId}/{generation.ScrapeId?.ToString() ?? "null"} has nonterminal or ambiguous recovery ownership: {string.Join(", ", evidence.RecoveryReasons)}.",
                            evidence));
                }
            }
            else if (generation.Status !=
                     PublicationGenerationStatus.Retired)
            {
                blockers.Add(
                    new SnapshotGenerationRetentionBlocker(
                        "unpointed_publication_status_unknown",
                        $"Publication {generation.PublicationId} has unknown status {generation.Status} and is not named by current/previous/working."));
            }
        }

        var scrapeIdsToValidate = namedPublications
            .Select(static publication => publication.ScrapeId)
            .Append(request.TriggerScrapeId)
            .Distinct()
            .ToArray();
        var scrapeIdentities =
            new Dictionary<
                long,
                (string Status, DateTime? CompletedAt, DateTime? FailedAt)>();
        if (scrapeIdsToValidate.Length > 0)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandTimeout = commandTimeoutSeconds;
            command.CommandText = """
                SELECT
                    id::BIGINT,
                    status,
                    completed_at,
                    failed_at
                FROM scrape_log
                WHERE id = ANY(@scrapeIds)
                """;
            command.Parameters.AddWithValue(
                "scrapeIds",
                scrapeIdsToValidate);
            await using var reader =
                await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                scrapeIdentities[reader.GetInt64(0)] = (
                    reader.GetString(1),
                    reader.IsDBNull(2)
                        ? null
                        : reader.GetDateTime(2),
                    reader.IsDBNull(3)
                        ? null
                        : reader.GetDateTime(3));
            }
        }

        ValidateTerminalScrape(
            request.TriggerScrapeId,
            "trigger_scrape_not_terminal",
            $"Trigger scrape {request.TriggerScrapeId}");
        foreach (var publication in namedPublications)
        {
            ValidateTerminalScrape(
                publication.ScrapeId,
                "named_publication_scrape_not_terminal",
                $"{publication.Slot} publication {publication.PublicationId} scrape {publication.ScrapeId}");
        }

        return new SafePointState(
            publishedScrapeId,
            currentPublicationId,
            previousPublicationId,
            workingPublicationId,
            namedPublications
                .OrderBy(
                    static publication =>
                        publication.Slot,
                    StringComparer.Ordinal)
                .ToArray(),
            deferrals
                .DistinctBy(static blocker =>
                    (blocker.Code, blocker.Detail))
                .OrderBy(
                    static blocker => blocker.Code,
                    StringComparer.Ordinal)
                .ToArray(),
            blockers
                .DistinctBy(static blocker =>
                    (blocker.Code, blocker.Detail))
                .OrderBy(
                    static blocker => blocker.Code,
                    StringComparer.Ordinal)
                .ThenBy(
                    static blocker => blocker.Detail,
                    StringComparer.Ordinal)
                .ToArray(),
            anomalies
                .DistinctBy(static anomaly =>
                    (
                        anomaly.Code,
                        anomaly.PublicationId,
                        anomaly.ScrapeId,
                        anomaly.PublicationStatus,
                        anomaly.Detail))
                .OrderBy(
                    static anomaly => anomaly.Code,
                    StringComparer.Ordinal)
                .ThenBy(
                    static anomaly => anomaly.PublicationId)
                .ThenBy(
                    static anomaly => anomaly.ScrapeId)
                .ThenBy(
                    static anomaly => anomaly.PublicationStatus,
                    StringComparer.Ordinal)
                .ThenBy(
                    static anomaly => anomaly.Detail,
                    StringComparer.Ordinal)
                .ToArray());

        void ValidateNamed(
            string slot,
            long? publicationId,
            bool required,
            IReadOnlyCollection<string> allowedStatuses)
        {
            if (!publicationId.HasValue)
            {
                if (required)
                {
                    blockers.Add(
                        new SnapshotGenerationRetentionBlocker(
                            "publication_pointer_missing",
                            $"The required {slot} publication pointer is null."));
                }
                return;
            }

            if (!byId.TryGetValue(
                    publicationId.Value,
                    out var generation)
                || !generation.ScrapeId.HasValue
                || generation.ScrapeId <= 0
                || !allowedStatuses.Contains(
                    generation.Status))
            {
                blockers.Add(
                    new SnapshotGenerationRetentionBlocker(
                        "named_publication_invalid",
                        $"The {slot} publication {publicationId.Value} is missing, has no positive scrape ID, or has an unexpected status."));
                return;
            }

            var metadataIdentityValid =
                long.TryParse(
                    generation.MetadataScrapeId,
                    out var metadataScrapeId)
                && metadataScrapeId ==
                    generation.ScrapeId.Value
                && long.TryParse(
                    generation.MetadataPublicationId,
                    out var metadataPublicationId)
                && metadataPublicationId ==
                    generation.PublicationId;
            var expectedCount =
                long.TryParse(
                    generation.ExpectedPublishedScopeCount,
                    out var parsedExpectedCount)
                && parsedExpectedCount > 0
                    ? parsedExpectedCount
                    : (long?)null;
            if (!metadataIdentityValid
                || !expectedCount.HasValue)
            {
                blockers.Add(
                    new SnapshotGenerationRetentionBlocker(
                        "named_publication_preparation_invalid",
                        $"The {slot} publication {publicationId.Value} does not have scrape-aligned preparation metadata with a positive expected scope-source count."));
            }

            namedPublications.Add(
                new NamedPublicationDescriptor(
                    slot,
                    generation.PublicationId,
                    generation.ScrapeId.Value,
                    expectedCount,
                    metadataIdentityValid));
        }

        void ValidateTerminalScrape(
            long scrapeId,
            string code,
            string label)
        {
            if (!scrapeIdentities.TryGetValue(
                    scrapeId,
                    out var scrape)
                || !string.Equals(
                    scrape.Status,
                    "completed",
                    StringComparison.Ordinal)
                || !scrape.CompletedAt.HasValue
                || scrape.FailedAt.HasValue)
            {
                blockers.Add(
                    new SnapshotGenerationRetentionBlocker(
                        code,
                        $"{label} is missing or does not have exact terminal completed scrape provenance."));
            }
        }
    }

    private static async Task<IReadOnlyDictionary<
        long,
        SnapshotGenerationRetentionPublicationFailureEvidence>>
        LoadFailedPublicationRecoveryEvidenceAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            IReadOnlyCollection<long> failedPublicationIds,
            long? currentPublicationId,
            long? previousPublicationId,
            long? workingPublicationId,
            long? publishedScrapeId,
            bool publicReadsFrozen,
            DateTime? publicReadsFrozenAt,
            long? publicReadsFrozenScrapeId,
            string? publicReadsFrozenReason,
            DateTime? commitIntentStartedAt,
            DateTime? commitIntentHeartbeatAt,
            string? commitIntentOwner,
            long? maxScoreGatePublicationId,
            long? notificationScrapeId,
            long? notificationProjectionScrapeId,
            long configuredResumeScrapeId,
            int commandTimeoutSeconds,
            CancellationToken ct)
    {
        if (failedPublicationIds.Count == 0)
        {
            return new Dictionary<
                long,
                SnapshotGenerationRetentionPublicationFailureEvidence>();
        }

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandTimeout = commandTimeoutSeconds;
        command.CommandText = """
            SELECT
                generation.publication_id,
                generation.scrape_id,
                generation.status,
                generation.failed_at,
                generation.failure_phase,
                scrape.status,
                scrape.completed_at,
                scrape.failed_at,
                COALESCE((
                    SELECT COUNT(*)::BIGINT
                    FROM publication_surface_bindings binding
                    WHERE binding.publication_id =
                            generation.publication_id
                ), 0),
                COALESCE((
                    SELECT COUNT(*)::BIGINT
                    FROM publication_surface_bindings binding
                    WHERE binding.publication_id =
                            generation.publication_id
                      AND binding.status IN (
                            'building',
                            'ready')
                      AND NOT (
                            binding.surface_name = 'item_shop'
                            AND binding.status = 'building'
                            AND binding.binding_kind =
                                'legacy_live_unversioned'
                            AND binding.binding_json ->> 'table' =
                                'item_shop_tracks')
                ), 0),
                COALESCE((
                    SELECT COUNT(*)::BIGINT
                    FROM publication_surface_bindings binding
                    WHERE binding.publication_id =
                            generation.publication_id
                      AND binding.status = 'building'
                ), 0),
                COALESCE((
                    SELECT COUNT(*)::BIGINT
                    FROM publication_surface_bindings binding
                    WHERE binding.publication_id =
                            generation.publication_id
                      AND binding.status = 'ready'
                ), 0),
                COALESCE((
                    SELECT COUNT(*)::BIGINT
                    FROM publication_surface_bindings binding
                    WHERE binding.publication_id =
                            generation.publication_id
                      AND binding.status = 'failed'
                ), 0),
                COALESCE((
                    SELECT COUNT(*)::BIGINT
                    FROM publication_surface_bindings binding
                    WHERE binding.publication_id =
                            generation.publication_id
                      AND binding.status = 'retired'
                ), 0),
                COALESCE((
                    SELECT COUNT(*)::BIGINT
                    FROM publication_surface_bindings binding
                    WHERE binding.publication_id =
                            generation.publication_id
                      AND binding.status NOT IN (
                            'building',
                            'ready',
                            'failed',
                            'retired')
                ), 0),
                COALESCE((
                    SELECT COUNT(*)::BIGINT
                    FROM leaderboard_published_scope_source source
                    WHERE source.published_scrape_id =
                            generation.scrape_id
                ), 0),
                COALESCE((
                    SELECT COUNT(*)::BIGINT
                    FROM publication_api_response_cache cache
                    WHERE cache.publication_id =
                            generation.publication_id
                ), 0),
                COALESCE((
                    SELECT COUNT(*)::BIGINT
                    FROM publication_api_response_cache_staging staging
                    WHERE staging.publication_id =
                            generation.publication_id
                ), 0),
                COALESCE((
                    SELECT COUNT(*)::BIGINT
                    FROM publication_song_catalog catalog
                    WHERE catalog.publication_id =
                            generation.publication_id
                ), 0),
                COALESCE((
                    SELECT COUNT(*)::BIGINT
                    FROM publication_path_artifacts artifact
                    WHERE artifact.publication_id =
                            generation.publication_id
                ), 0),
                COALESCE((
                    SELECT COUNT(*)::BIGINT
                    FROM pg_class relation
                    JOIN pg_namespace namespace
                      ON namespace.oid =
                            relation.relnamespace
                    WHERE namespace.nspname = 'public'
                      AND relation.relkind IN ('r', 'p')
                      AND (
                            relation.relname LIKE
                                'btr\_pubprep\_'
                                || generation.publication_id::TEXT
                                || '\_%' ESCAPE '\'
                            OR relation.relname LIKE
                                'btrs\_pubprep\_'
                                || generation.publication_id::TEXT
                                || '\_%' ESCAPE '\')
                ), 0),
                COALESCE((
                    SELECT COUNT(*)::BIGINT
                    FROM pg_class relation
                    JOIN pg_namespace namespace
                      ON namespace.oid =
                            relation.relnamespace
                    WHERE namespace.nspname = 'public'
                      AND relation.relkind IN ('r', 'p')
                      AND (
                            relation.relname LIKE
                                'btr\_retained\_'
                                || generation.publication_id::TEXT
                                || '\_%' ESCAPE '\'
                            OR relation.relname LIKE
                                'btrs\_retained\_'
                                || generation.publication_id::TEXT
                                || '\_%' ESCAPE '\')
                ), 0),
                COALESCE((
                    SELECT COUNT(*)::BIGINT
                    FROM leaderboard_staging staging
                    WHERE staging.scrape_id =
                            generation.scrape_id
                ), 0)
                + COALESCE((
                    SELECT COUNT(*)::BIGINT
                    FROM leaderboard_staging_v2 staging
                    WHERE staging.scrape_id =
                            generation.scrape_id
                ), 0),
                COALESCE((
                    SELECT COUNT(*)::BIGINT
                    FROM leaderboard_staging_meta staging
                    WHERE staging.scrape_id =
                            generation.scrape_id
                ), 0),
                COALESCE((
                    SELECT COUNT(*)::BIGINT
                    FROM deep_scrape_queue staging
                    WHERE staging.scrape_id =
                            generation.scrape_id
                ), 0),
                COALESCE((
                    SELECT COUNT(*)::BIGINT
                    FROM scrape_writer_failures failure
                    WHERE failure.scrape_id =
                            generation.scrape_id
                      AND failure.replayed_at IS NULL
                ), 0)
            FROM publication_generations generation
            LEFT JOIN scrape_log scrape
              ON scrape.id = generation.scrape_id
            WHERE generation.publication_id =
                    ANY(@failedPublicationIds)
              AND generation.status = 'failed'
            ORDER BY generation.publication_id
            """;
        command.Parameters.AddWithValue(
            "failedPublicationIds",
            failedPublicationIds.ToArray());

        var result =
            new Dictionary<
                long,
                SnapshotGenerationRetentionPublicationFailureEvidence>();
        await using var reader =
            await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var publicationId = reader.GetInt64(0);
            var scrapeId = reader.IsDBNull(1)
                ? (long?)null
                : reader.GetInt64(1);
            var publicationStatus = reader.GetString(2);
            var publicationFailedAt = reader.IsDBNull(3)
                ? (DateTime?)null
                : reader.GetDateTime(3);
            var publicationFailurePhase = reader.IsDBNull(4)
                ? null
                : reader.GetString(4);
            var scrapeStatus = reader.IsDBNull(5)
                ? null
                : reader.GetString(5);
            var scrapeCompletedAt = reader.IsDBNull(6)
                ? (DateTime?)null
                : reader.GetDateTime(6);
            var scrapeFailedAt = reader.IsDBNull(7)
                ? (DateTime?)null
                : reader.GetDateTime(7);
            var surfaceBindingRowCount =
                reader.GetInt64(8);
            var liveSurfaceBindingRowCount =
                reader.GetInt64(9);
            var buildingSurfaceBindingRowCount =
                reader.GetInt64(10);
            var readySurfaceBindingRowCount =
                reader.GetInt64(11);
            var failedSurfaceBindingRowCount =
                reader.GetInt64(12);
            var retiredSurfaceBindingRowCount =
                reader.GetInt64(13);
            var invalidSurfaceBindingRowCount =
                reader.GetInt64(14);
            var publishedSourceRowCount =
                reader.GetInt64(15);
            var apiResponseCacheRowCount =
                reader.GetInt64(16);
            var apiResponseCacheStagingRowCount =
                reader.GetInt64(17);
            var songCatalogRowCount =
                reader.GetInt64(18);
            var pathArtifactRowCount =
                reader.GetInt64(19);
            var preparedBandRelationCount =
                reader.GetInt64(20);
            var retainedBandRelationCount =
                reader.GetInt64(21);
            var leaderboardStagingRowCount =
                reader.GetInt64(22);
            var leaderboardStagingMetadataRowCount =
                reader.GetInt64(23);
            var deepScrapeQueueRowCount =
                reader.GetInt64(24);
            var unreplayedWriterFailureCount =
                reader.GetInt64(25);

            var namedPointerSlots = new List<string>();
            AddNamedPointer(
                currentPublicationId,
                "current");
            AddNamedPointer(
                previousPublicationId,
                "previous");
            AddNamedPointer(
                workingPublicationId,
                "working");

            var terminalFailureIdentityValid =
                scrapeId is > 0
                && string.Equals(
                    publicationStatus,
                    PublicationGenerationStatus.Failed,
                    StringComparison.Ordinal)
                && publicationFailedAt.HasValue
                && string.Equals(
                    scrapeStatus,
                    "failed",
                    StringComparison.Ordinal)
                && scrapeFailedAt.HasValue;
            var configuredResumeScrape =
                configuredResumeScrapeId > 0
                && scrapeId == configuredResumeScrapeId;
            var publishedScrapeReference =
                scrapeId.HasValue
                && scrapeId == publishedScrapeId;
            var publicationFreezeReference =
                scrapeId.HasValue
                && scrapeId ==
                    publicReadsFrozenScrapeId
                && (
                    publicReadsFrozen
                    || publicReadsFrozenAt.HasValue
                    || !string.IsNullOrWhiteSpace(
                        publicReadsFrozenReason));
            var publicationCommitIntentReference =
                workingPublicationId == publicationId
                && (
                    commitIntentStartedAt.HasValue
                    || commitIntentHeartbeatAt.HasValue
                    || !string.IsNullOrWhiteSpace(
                        commitIntentOwner));
            var maxScoreMutationGateReference =
                maxScoreGatePublicationId ==
                    publicationId;
            var notificationStateReference =
                scrapeId.HasValue
                && (
                    notificationScrapeId == scrapeId
                    || notificationProjectionScrapeId ==
                        scrapeId);

            var recoveryReasons = new List<string>();
            recoveryReasons.AddRange(
                namedPointerSlots.Select(
                    static slot =>
                        $"named_pointer:{slot}"));
            if (!terminalFailureIdentityValid)
            {
                recoveryReasons.Add(
                    "failed_publication_identity_invalid");
            }
            if (string.Equals(
                    scrapeStatus,
                    "running",
                    StringComparison.Ordinal))
            {
                recoveryReasons.Add("running_scrape");
            }
            if (configuredResumeScrape)
            {
                recoveryReasons.Add(
                    "configured_resume_scrape");
            }
            if (publishedScrapeReference)
            {
                recoveryReasons.Add(
                    "published_scrape_reference");
            }
            if (publicationFreezeReference)
            {
                recoveryReasons.Add(
                    "publication_freeze_reference");
            }
            if (publicationCommitIntentReference)
            {
                recoveryReasons.Add(
                    "publication_commit_intent_reference");
            }
            if (maxScoreMutationGateReference)
            {
                recoveryReasons.Add(
                    "max_score_mutation_gate_reference");
            }
            if (notificationStateReference)
            {
                recoveryReasons.Add(
                    "notification_state_reference");
            }
            AddArtifactReason(
                liveSurfaceBindingRowCount,
                "live_surface_binding");
            AddArtifactReason(
                invalidSurfaceBindingRowCount,
                "invalid_surface_binding_state");
            AddArtifactReason(
                apiResponseCacheRowCount,
                "publication_api_response_cache");
            AddArtifactReason(
                apiResponseCacheStagingRowCount,
                "publication_api_response_cache_staging");
            AddArtifactReason(
                songCatalogRowCount,
                "publication_song_catalog");
            AddArtifactReason(
                pathArtifactRowCount,
                "publication_path_artifact");
            AddArtifactReason(
                preparedBandRelationCount,
                "prepared_band_relation");
            AddArtifactReason(
                retainedBandRelationCount,
                "retained_band_relation");
            AddArtifactReason(
                leaderboardStagingRowCount,
                "leaderboard_staging");
            AddArtifactReason(
                leaderboardStagingMetadataRowCount,
                "leaderboard_staging_metadata");
            AddArtifactReason(
                deepScrapeQueueRowCount,
                "deep_scrape_queue");

            result.Add(
                publicationId,
                new SnapshotGenerationRetentionPublicationFailureEvidence(
                    publicationId,
                    scrapeId,
                    publicationStatus,
                    publicationFailedAt,
                    publicationFailurePhase,
                    scrapeStatus,
                    scrapeCompletedAt,
                    scrapeFailedAt,
                    terminalFailureIdentityValid,
                    namedPointerSlots
                        .Order(StringComparer.Ordinal)
                        .ToArray(),
                    configuredResumeScrape,
                    publishedScrapeReference,
                    publicationFreezeReference,
                    publicationCommitIntentReference,
                    maxScoreMutationGateReference,
                    notificationStateReference,
                    surfaceBindingRowCount,
                    liveSurfaceBindingRowCount,
                    buildingSurfaceBindingRowCount,
                    readySurfaceBindingRowCount,
                    failedSurfaceBindingRowCount,
                    retiredSurfaceBindingRowCount,
                    invalidSurfaceBindingRowCount,
                    publishedSourceRowCount,
                    apiResponseCacheRowCount,
                    apiResponseCacheStagingRowCount,
                    songCatalogRowCount,
                    pathArtifactRowCount,
                    preparedBandRelationCount,
                    retainedBandRelationCount,
                    leaderboardStagingRowCount,
                    leaderboardStagingMetadataRowCount,
                    deepScrapeQueueRowCount,
                    unreplayedWriterFailureCount,
                    recoveryReasons
                        .Distinct(StringComparer.Ordinal)
                        .Order(StringComparer.Ordinal)
                        .ToArray()));

            void AddNamedPointer(
                long? pointerPublicationId,
                string slot)
            {
                if (pointerPublicationId == publicationId)
                    namedPointerSlots.Add(slot);
            }

            void AddArtifactReason(
                long count,
                string reason)
            {
                if (count > 0)
                    recoveryReasons.Add(reason);
            }
        }

        if (result.Count != failedPublicationIds.Count)
        {
            throw new InvalidOperationException(
                $"Failed-publication recovery inventory expected {failedPublicationIds.Count} row(s) but returned {result.Count}.");
        }
        return result;
    }

    private static async Task<RegistrationDrainSnapshot>
        LoadRegistrationDrainAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            int commandTimeoutSeconds,
            CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandTimeout = commandTimeoutSeconds;
        command.CommandText = """
            WITH registered AS (
                SELECT DISTINCT account_id
                FROM registered_users
            ), backfill_inventory AS (
                SELECT
                    COALESCE(
                        registered.account_id,
                        backfill.account_id) AS account_id,
                    registered.account_id IS NOT NULL
                        AS is_registered,
                    backfill.status
                FROM registered
                FULL OUTER JOIN backfill_status backfill
                  ON backfill.account_id =
                        registered.account_id
            ), history_inventory AS (
                SELECT
                    registered.account_id,
                    history.status
                FROM registered
                JOIN backfill_status backfill
                  ON backfill.account_id =
                        registered.account_id
                 AND backfill.status = 'complete'
                LEFT JOIN history_recon_status history
                  ON history.account_id =
                        registered.account_id
            )
            SELECT
                COUNT(DISTINCT backfill.account_id)
                    FILTER (
                        WHERE backfill.status IN (
                            'pending',
                            'in_progress',
                            'deferred')
                    )::INTEGER
                        AS runnable_backfills,
                (
                    SELECT COUNT(DISTINCT history.account_id)
                    FROM history_inventory history
                    WHERE history.status IS NULL
                       OR history.status IN (
                            'pending',
                            'in_progress',
                            'error')
                )::INTEGER
                    AS repairable_history,
                COUNT(DISTINCT backfill.account_id)
                    FILTER (
                        WHERE backfill.is_registered
                          AND backfill.status IS NULL
                    )::INTEGER
                        AS missing_backfills,
                COUNT(DISTINCT backfill.account_id)
                    FILTER (
                        WHERE backfill.is_registered
                          AND backfill.status = 'error'
                    )::INTEGER
                        AS terminal_backfill_errors,
                COUNT(DISTINCT backfill.account_id)
                    FILTER (
                        WHERE backfill.is_registered
                          AND backfill.status IS NOT NULL
                          AND backfill.status NOT IN (
                              'pending',
                              'in_progress',
                              'deferred',
                              'complete',
                              'error')
                    )::INTEGER
                        AS invalid_backfills,
                (
                    SELECT COUNT(DISTINCT history.account_id)
                    FROM history_inventory history
                    WHERE history.status IS NOT NULL
                      AND history.status NOT IN (
                          'pending',
                          'in_progress',
                          'complete',
                          'error')
                )::INTEGER
                    AS invalid_history
            FROM backfill_inventory backfill
            """;
        await using var reader =
            await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
        {
            throw new InvalidOperationException(
                "Registration drain query returned no state.");
        }

        var runnableBackfills = reader.GetInt32(0);
        var repairableHistory = reader.GetInt32(1);
        var terminalBlockers =
            new List<SnapshotGenerationRetentionBlocker>();
        AddTerminalBlocker(
            reader.GetInt32(2),
            "registration_backfill_state_missing",
            "registered account(s) have no backfill state and cannot be claimed by the background registration worker");
        AddTerminalBlocker(
            reader.GetInt32(3),
            "registration_backfill_terminal_error",
            "backfill account(s) are in terminal error state and require explicit supported requeue");
        AddTerminalBlocker(
            reader.GetInt32(4),
            "registration_backfill_state_invalid",
            "backfill account(s) have an unknown non-runnable state");
        AddTerminalBlocker(
            reader.GetInt32(5),
            "registration_history_state_invalid",
            "history-reconstruction account(s) have an unknown state that automatic recovery cannot safely classify");

        return new RegistrationDrainSnapshot(
            runnableBackfills,
            repairableHistory,
            terminalBlockers);

        void AddTerminalBlocker(
            int count,
            string code,
            string detail)
        {
            if (count <= 0)
                return;
            terminalBlockers.Add(
                new SnapshotGenerationRetentionBlocker(
                    code,
                    $"Registration drain found {count} {detail}."));
        }
    }

    private static async Task<TopologyState> LoadTopologyAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int commandTimeoutSeconds,
        CancellationToken ct)
    {
        var rootNames =
            SnapshotGenerationRetentionContract.Instruments
                .Select(static instrument =>
                    instrument.RootRelation)
                .ToArray();
        var relations = new List<TopologyRelation>();
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandTimeout = commandTimeoutSeconds;
            command.CommandText = """
                SELECT
                    relation.oid::BIGINT,
                    namespace.nspname,
                    relation.relname,
                    relation.relkind::TEXT,
                    relation.relpersistence::TEXT,
                    relation.relfilenode::BIGINT,
                    COALESCE(
                        pg_get_partkeydef(
                            partitioned.partrelid),
                        ''),
                    COALESCE(
                        pg_get_expr(
                            relation.relpartbound,
                            relation.oid,
                            TRUE),
                        ''),
                    COALESCE(
                        tablespace.spcname,
                        database_tablespace.spcname),
                    COALESCE(
                        access_method.amname,
                        ''),
                    COALESCE(
                        relation.reloptions,
                        ARRAY[]::TEXT[]),
                    parent.oid::BIGINT,
                    parent_namespace.nspname,
                    parent.relname,
                    GREATEST(
                        COALESCE(relation.reltuples, 0),
                        0)::DOUBLE PRECISION,
                    CASE
                        WHEN relation.relkind IN ('r', 'm')
                        THEN pg_total_relation_size(
                            relation.oid)::BIGINT
                        ELSE 0::BIGINT
                    END
                FROM pg_class relation
                JOIN pg_namespace namespace
                  ON namespace.oid = relation.relnamespace
                LEFT JOIN pg_inherits inheritance
                  ON inheritance.inhrelid = relation.oid
                LEFT JOIN pg_class parent
                  ON parent.oid = inheritance.inhparent
                LEFT JOIN pg_namespace parent_namespace
                  ON parent_namespace.oid = parent.relnamespace
                LEFT JOIN pg_partitioned_table partitioned
                  ON partitioned.partrelid = relation.oid
                LEFT JOIN pg_tablespace tablespace
                  ON tablespace.oid = relation.reltablespace
                LEFT JOIN pg_am access_method
                  ON access_method.oid = relation.relam
                CROSS JOIN LATERAL (
                    SELECT default_tablespace.spcname
                    FROM pg_database database
                    JOIN pg_tablespace default_tablespace
                      ON default_tablespace.oid =
                            database.dattablespace
                    WHERE database.datname =
                            current_database()
                ) database_tablespace
                WHERE namespace.nspname = 'public'
                  AND (
                      relation.relname =
                            'leaderboard_entries_snapshot'
                      OR relation.relname = ANY(@rootNames)
                      OR parent.relname =
                            'leaderboard_entries_snapshot'
                      OR parent.relname = ANY(@rootNames)
                  )
                ORDER BY relation.relname
                """;
            command.Parameters.AddWithValue(
                "rootNames",
                rootNames);
            await using var reader =
                await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                relations.Add(
                    new TopologyRelation(
                        reader.GetInt64(0),
                        reader.GetString(1),
                        reader.GetString(2),
                        reader.GetString(3),
                        reader.GetString(4),
                        reader.GetInt64(5),
                        reader.GetString(6),
                        reader.GetString(7),
                        reader.GetString(8),
                        reader.GetString(9),
                        reader.GetFieldValue<string[]>(10),
                        reader.IsDBNull(11)
                            ? null
                            : reader.GetInt64(11),
                        reader.IsDBNull(12)
                            ? null
                            : reader.GetString(12),
                        reader.IsDBNull(13)
                            ? null
                            : reader.GetString(13),
                        Math.Max(
                            0,
                            checked((long)Math.Ceiling(
                                reader.GetDouble(14)))),
                        reader.GetInt64(15)));
            }
        }

        var relationOids = relations
            .Select(static relation => relation.Oid)
            .Distinct()
            .ToArray();
        var indexes =
            new List<SnapshotGenerationRetentionIndex>();
        if (relationOids.Length > 0)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandTimeout = commandTimeoutSeconds;
            command.CommandText = """
                SELECT
                    index_row.indrelid::BIGINT,
                    index_relation.oid::BIGINT,
                    index_relation.relfilenode::BIGINT,
                    index_relation.relname,
                    index_relation.relkind::TEXT,
                    index_row.indisvalid,
                    index_row.indisready,
                    index_row.indisprimary,
                    index_row.indisunique,
                    access_method.amname,
                    COALESCE(
                        tablespace.spcname,
                        database_tablespace.spcname),
                    parent_index.oid::BIGINT,
                    pg_get_indexdef(
                        index_relation.oid)
                FROM pg_index index_row
                JOIN pg_class index_relation
                  ON index_relation.oid =
                        index_row.indexrelid
                JOIN pg_am access_method
                  ON access_method.oid =
                        index_relation.relam
                LEFT JOIN pg_inherits inheritance
                  ON inheritance.inhrelid =
                        index_relation.oid
                LEFT JOIN pg_class parent_index
                  ON parent_index.oid =
                        inheritance.inhparent
                LEFT JOIN pg_tablespace tablespace
                  ON tablespace.oid =
                        index_relation.reltablespace
                CROSS JOIN LATERAL (
                    SELECT default_tablespace.spcname
                    FROM pg_database database
                    JOIN pg_tablespace default_tablespace
                      ON default_tablespace.oid =
                            database.dattablespace
                    WHERE database.datname =
                            current_database()
                ) database_tablespace
                WHERE index_row.indrelid::BIGINT =
                        ANY(@relationOids)
                ORDER BY
                    index_row.indrelid,
                    index_relation.relname
                """;
            command.Parameters.AddWithValue(
                "relationOids",
                relationOids);
            await using var reader =
                await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                indexes.Add(
                    new SnapshotGenerationRetentionIndex(
                        reader.GetInt64(0),
                        reader.GetInt64(1),
                        reader.GetInt64(2),
                        reader.GetString(3),
                        reader.GetString(4),
                        reader.GetBoolean(5),
                        reader.GetBoolean(6),
                        reader.GetBoolean(7),
                        reader.GetBoolean(8),
                        reader.GetString(9),
                        reader.GetString(10),
                        reader.IsDBNull(11)
                            ? null
                            : reader.GetInt64(11),
                        reader.GetString(12)));
            }
        }

        return await BuildTopologyStateAsync(
            connection,
            transaction,
            relations,
            indexes,
            commandTimeoutSeconds,
            ct);
    }

    private static async Task<TopologyState>
        BuildTopologyStateAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            IReadOnlyList<TopologyRelation> relations,
            IReadOnlyList<SnapshotGenerationRetentionIndex>
                indexes,
            int commandTimeoutSeconds,
            CancellationToken ct)
    {
        var globalBlockers =
            new List<SnapshotGenerationRetentionBlocker>();
        var byName = relations.ToDictionary(
            static relation => relation.RelationName,
            StringComparer.Ordinal);
        var top = byName.GetValueOrDefault(SnapshotParent);
        if (top is null
            || top.RelationKind != "p"
            || top.ParentOid.HasValue
            || top.PartitionKey != "LIST (instrument)")
        {
            globalBlockers.Add(
                new SnapshotGenerationRetentionBlocker(
                    "snapshot_parent_shape_invalid",
                    "The top snapshot relation is missing, attached, nonpartitioned, or does not use exact LIST (instrument)."));
        }
        var topIndexes = top is null
            ? []
            : indexes.Where(index =>
                    index.TableOid == top.Oid)
                .OrderBy(
                    static index => index.IndexName,
                    StringComparer.Ordinal)
                .ToArray();

        var expectedRoots =
            SnapshotGenerationRetentionContract.Instruments
                .Select(static instrument =>
                    instrument.RootRelation)
                .ToHashSet(StringComparer.Ordinal);
        foreach (var unexpected in relations.Where(
                     relation =>
                         relation.ParentRelationName ==
                             SnapshotParent
                         && !expectedRoots.Contains(
                             relation.RelationName)))
        {
            globalBlockers.Add(
                new SnapshotGenerationRetentionBlocker(
                    "unexpected_instrument_root",
                    $"Unexpected direct snapshot root {unexpected.RelationName}."));
        }

        var allExpectedDefaultsExist =
            SnapshotGenerationRetentionContract.Instruments
                .All(instrument =>
                    byName.TryGetValue(
                        instrument.DefaultRelation,
                        out var relation)
                    && relation.ParentRelationName ==
                        instrument.RootRelation);
        var defaultCounts = allExpectedDefaultsExist
            ? await LoadDefaultCountsAsync(
                connection,
                transaction,
                commandTimeoutSeconds,
                ct)
            : new Dictionary<string, long>(
                StringComparer.Ordinal);
        var children =
            new List<SnapshotGenerationRetentionChild>();
        var indexTopologyValidations =
            new List<
                SnapshotGenerationRetentionIndexTopologyValidation>();

        foreach (var instrument in
                 SnapshotGenerationRetentionContract.Instruments)
        {
            var instrumentBlockers =
                new List<SnapshotGenerationRetentionBlocker>();
            var root = byName.GetValueOrDefault(
                instrument.RootRelation);
            var expectedRootBound =
                $"FOR VALUES IN ('{instrument.Instrument}')";
            if (root is null
                || root.RelationKind != "p"
                || root.ParentRelationName != SnapshotParent
                || root.PartitionBound != expectedRootBound
                || root.PartitionKey != "LIST (snapshot_id)")
            {
                instrumentBlockers.Add(
                    new SnapshotGenerationRetentionBlocker(
                        "instrument_root_shape_invalid",
                        $"Root {instrument.RootRelation} is missing, detached, nonpartitioned, or has an unexpected bound/key."));
            }

            var defaultChild = byName.GetValueOrDefault(
                instrument.DefaultRelation);
            if (defaultChild is null
                || defaultChild.ParentRelationName !=
                    instrument.RootRelation
                || defaultChild.PartitionBound != "DEFAULT"
                || defaultChild.RelationKind != "r")
            {
                instrumentBlockers.Add(
                    new SnapshotGenerationRetentionBlocker(
                        "default_child_shape_invalid",
                        $"Root {instrument.RootRelation} does not own its exact regular default child."));
            }
            else if (!defaultCounts.TryGetValue(
                         instrument.RootRelation,
                         out var defaultRows))
            {
                instrumentBlockers.Add(
                    new SnapshotGenerationRetentionBlocker(
                        "default_child_count_unresolved",
                        $"Default child row count for {instrument.RootRelation} was not captured."));
            }
            else if (defaultRows != 0)
            {
                instrumentBlockers.Add(
                    new SnapshotGenerationRetentionBlocker(
                        "default_child_not_empty",
                        $"Default child {instrument.DefaultRelation} contains {defaultRows} row(s)."));
            }

            var rootIndexes = root is null
                ? []
                : indexes.Where(index =>
                        index.TableOid == root.Oid)
                    .OrderBy(
                        static index => index.IndexName,
                        StringComparer.Ordinal)
                    .ToArray();
            var defaultIndexes = defaultChild is null
                ? []
                : indexes.Where(index =>
                        index.TableOid == defaultChild.Oid)
                    .OrderBy(
                        static index => index.IndexName,
                        StringComparer.Ordinal)
                    .ToArray();
            globalBlockers.AddRange(instrumentBlockers);
            var generationRelations = relations
                .Where(relation =>
                    relation.ParentRelationName ==
                        instrument.RootRelation
                    && relation.PartitionBound !=
                        "DEFAULT")
                .OrderBy(
                    static relation =>
                        relation.RelationName,
                    StringComparer.Ordinal)
                .ToArray();
            if (generationRelations.Length == 0
                && instrumentBlockers.Count > 0)
            {
                globalBlockers.Add(
                    new SnapshotGenerationRetentionBlocker(
                        "instrument_generation_children_missing",
                        $"Broken root {instrument.RootRelation} has no numeric generation children on which to retain its topology blockers."));
            }

            var numericChildIndexValidations =
                new List<
                    SnapshotGenerationRetentionNumericChildIndexValidation>();
            foreach (var relation in generationRelations)
            {
                var childBlockers =
                    new List<SnapshotGenerationRetentionBlocker>(
                        instrumentBlockers);
                var match = GenerationChildPattern.Match(
                    relation.RelationName);
                if (!match.Success
                    || match.Groups["root"].Value !=
                        instrument.RootRelation
                    || !long.TryParse(
                        match.Groups["snapshot"].Value,
                        out var snapshotId)
                    || snapshotId <= 0)
                {
                    globalBlockers.Add(
                        new SnapshotGenerationRetentionBlocker(
                            "malformed_generation_child",
                            $"Attached child {relation.RelationName} is not an exact numeric child of {instrument.RootRelation}."));
                    continue;
                }

                if (relation.RelationKind != "r"
                    || relation.Relfilenode <= 0)
                {
                    globalBlockers.Add(
                        new SnapshotGenerationRetentionBlocker(
                            "nonphysical_generation_child",
                            $"Generation child {relation.RelationName} is not an exact regular physical relation."));
                    continue;
                }
                var expectedBound =
                    $"FOR VALUES IN ('{snapshotId}')";
                if (relation.PartitionBound != expectedBound)
                {
                    childBlockers.Add(
                        new SnapshotGenerationRetentionBlocker(
                            "generation_child_bound_invalid",
                            $"Child {relation.RelationName} has bound {relation.PartitionBound}, not {expectedBound}."));
                }

                var childIndexes = indexes
                    .Where(index =>
                        index.TableOid == relation.Oid)
                    .OrderBy(
                        static index => index.IndexName,
                        StringComparer.Ordinal)
                    .ToArray();
                var numericIndexValidation =
                    BuildPrimaryNumericChildIndexValidation(
                        instrument.Instrument,
                        snapshotId,
                        relation.RelationName,
                        childIndexes,
                        rootIndexes);
                numericChildIndexValidations.Add(
                    numericIndexValidation);
                if (!numericIndexValidation.IsValid)
                {
                    childBlockers.Add(
                        new SnapshotGenerationRetentionBlocker(
                            "generation_child_index_shape_invalid",
                            $"Child {relation.RelationName} does not own the exact valid attached root-index set."));
                }

                children.Add(
                    new SnapshotGenerationRetentionChild(
                        instrument,
                        root?.SchemaName ?? "public",
                        root?.ParentOid ?? 0,
                        root?.Oid ?? 0,
                        root?.PartitionKey ?? "unresolved",
                        root?.PartitionBound ?? "unresolved",
                        root?.TablespaceName ?? "unresolved",
                        root?.RelationOptions ?? [],
                        rootIndexes,
                        relation.SchemaName,
                        relation.RelationName,
                        snapshotId,
                        relation.Oid,
                        relation.Relfilenode,
                        relation.PartitionBound,
                        relation.TablespaceName,
                        relation.RelationKind,
                        relation.PersistenceKind,
                        relation.AccessMethod,
                        relation.RelationOptions,
                        childIndexes,
                        relation.RowEstimate,
                        relation.TotalBytes,
                        childBlockers
                            .DistinctBy(static blocker =>
                                (blocker.Code, blocker.Detail))
                            .OrderBy(
                                static blocker => blocker.Code,
                                StringComparer.Ordinal)
                            .ToArray()));
            }

            var indexTopology =
                BuildPrimaryIndexTopologyValidation(
                    instrument.Instrument,
                    topIndexes,
                    rootIndexes,
                    defaultIndexes,
                    numericChildIndexValidations);
            indexTopologyValidations.Add(indexTopology);
            globalBlockers.AddRange(
                BuildIndexTopologyBlockers(
                    indexTopology,
                    instrument.RootRelation,
                    instrument.DefaultRelation));
        }

        return new TopologyState(
            children,
            globalBlockers
                .DistinctBy(static blocker =>
                    (blocker.Code, blocker.Detail))
                .OrderBy(
                    static blocker => blocker.Code,
                    StringComparer.Ordinal)
                .ToArray(),
            indexTopologyValidations
                .OrderBy(
                    static validation =>
                        validation.Instrument,
                    StringComparer.Ordinal)
                .ToArray());
    }

    private static
        SnapshotGenerationRetentionIndexTopologyValidation
        BuildPrimaryIndexTopologyValidation(
            string instrument,
            IReadOnlyList<SnapshotGenerationRetentionIndex>
                topIndexes,
            IReadOnlyList<SnapshotGenerationRetentionIndex>
                rootIndexes,
            IReadOnlyList<SnapshotGenerationRetentionIndex>
                defaultIndexes,
            IReadOnlyList<
                SnapshotGenerationRetentionNumericChildIndexValidation>
                numericChildIndexValidations)
    {
        var topIndexOids = topIndexes
            .Select(static index => index.IndexOid)
            .ToHashSet();
        var attachedRootIndexes = rootIndexes
            .Where(index =>
                index.ParentIndexOid.HasValue
                && topIndexOids.Contains(
                    index.ParentIndexOid.Value))
            .ToArray();
        var rootIndexOids = attachedRootIndexes
            .Select(static index => index.IndexOid)
            .ToHashSet();

        static int MissingAttachments(
            IEnumerable<long> parentOids,
            IReadOnlyList<SnapshotGenerationRetentionIndex>
                children) =>
            parentOids.Count(parentOid =>
                children.Count(child =>
                    child.ParentIndexOid == parentOid) == 0);

        static int DuplicateAttachments(
            IEnumerable<long> parentOids,
            IReadOnlyList<SnapshotGenerationRetentionIndex>
                children) =>
            parentOids.Sum(parentOid =>
                Math.Max(
                    0,
                    children.Count(child =>
                        child.ParentIndexOid == parentOid) - 1));

        return new
            SnapshotGenerationRetentionIndexTopologyValidation(
                instrument,
                topIndexes
                    .Select(
                        SnapshotGenerationRetentionIndexTopologyValidation
                            .IndexKey)
                    .Order(StringComparer.Ordinal)
                    .ToArray(),
                rootIndexes
                    .Select(
                        SnapshotGenerationRetentionIndexTopologyValidation
                            .IndexKey)
                    .Order(StringComparer.Ordinal)
                    .ToArray(),
                defaultIndexes
                    .Select(
                        SnapshotGenerationRetentionIndexTopologyValidation
                            .IndexKey)
                    .Order(StringComparer.Ordinal)
                    .ToArray(),
                SnapshotGenerationRetentionContract
                    .RequiredSnapshotParentIndexNames
                    .Where(required =>
                        topIndexes.All(index =>
                            index.IndexName != required))
                    .Order(StringComparer.Ordinal)
                    .ToArray(),
                topIndexes.Count(index =>
                    !index.IsValid
                    || index.RelationKind != "I"),
                topIndexes.Count(index => !index.IsReady),
                topIndexes.Count(index =>
                    index.ParentIndexOid.HasValue),
                MissingAttachments(
                    topIndexOids,
                    attachedRootIndexes),
                DuplicateAttachments(
                    topIndexOids,
                    attachedRootIndexes),
                rootIndexes.Count(index =>
                    !index.ParentIndexOid.HasValue
                    || !topIndexOids.Contains(
                        index.ParentIndexOid.Value)),
                rootIndexes.Count(index =>
                    !index.IsValid
                    || index.RelationKind != "I"),
                rootIndexes.Count(index => !index.IsReady),
                MissingAttachments(
                    rootIndexOids,
                    defaultIndexes),
                DuplicateAttachments(
                    rootIndexOids,
                    defaultIndexes),
                defaultIndexes.Count(index =>
                    !index.ParentIndexOid.HasValue
                    || !rootIndexOids.Contains(
                        index.ParentIndexOid.Value)),
                defaultIndexes.Count(index =>
                    !index.IsValid
                    || index.RelationKind != "i"),
                defaultIndexes.Count(index =>
                    !index.IsReady),
                numericChildIndexValidations
                    .OrderBy(
                        static validation =>
                            validation.SnapshotId)
                    .ThenBy(
                        static validation =>
                            validation.ChildRelation,
                        StringComparer.Ordinal)
                    .ToArray());
    }

    private static IReadOnlyList<
        SnapshotGenerationRetentionBlocker>
        BuildIndexTopologyBlockers(
            SnapshotGenerationRetentionIndexTopologyValidation
                validation,
            string rootRelation,
            string defaultRelation)
    {
        var blockers =
            new List<SnapshotGenerationRetentionBlocker>();
        Add(
            validation.MissingRequiredTopIndexNames.Count > 0,
            "snapshot_parent_index_missing",
            $"Top snapshot indexes are missing: {string.Join(", ", validation.MissingRequiredTopIndexNames)}.");
        Add(
            validation.InvalidTopIndexCount > 0,
            "snapshot_parent_index_invalid",
            $"Top snapshot relation has {validation.InvalidTopIndexCount} invalid or nonpartitioned index(es).");
        Add(
            validation.UnreadyTopIndexCount > 0,
            "snapshot_parent_index_unready",
            $"Top snapshot relation has {validation.UnreadyTopIndexCount} unready index(es).");
        Add(
            validation.AttachedTopIndexCount > 0,
            "snapshot_parent_index_detached",
            $"Top snapshot relation unexpectedly has {validation.AttachedTopIndexCount} index parent attachment(s).");
        Add(
            validation.MissingRootIndexCount > 0,
            "instrument_root_index_missing",
            $"Root {rootRelation} is missing {validation.MissingRootIndexCount} top-index attachment(s).");
        Add(
            validation.DuplicateRootIndexCount > 0
            || validation.DetachedRootIndexCount > 0,
            "instrument_root_index_detached",
            $"Root {rootRelation} has duplicate or detached index attachments.");
        Add(
            validation.InvalidRootIndexCount > 0,
            "instrument_root_index_invalid",
            $"Root {rootRelation} has {validation.InvalidRootIndexCount} invalid or nonpartitioned index(es).");
        Add(
            validation.UnreadyRootIndexCount > 0,
            "instrument_root_index_unready",
            $"Root {rootRelation} has {validation.UnreadyRootIndexCount} unready index(es).");
        Add(
            validation.MissingDefaultIndexCount > 0,
            "default_child_index_missing",
            $"Default child {defaultRelation} is missing {validation.MissingDefaultIndexCount} root-index attachment(s).");
        Add(
            validation.DuplicateDefaultIndexCount > 0
            || validation.DetachedDefaultIndexCount > 0,
            "default_child_index_detached",
            $"Default child {defaultRelation} has duplicate or detached index attachments.");
        Add(
            validation.InvalidDefaultIndexCount > 0,
            "default_child_index_invalid",
            $"Default child {defaultRelation} has {validation.InvalidDefaultIndexCount} invalid or nonregular index(es).");
        Add(
            validation.UnreadyDefaultIndexCount > 0,
            "default_child_index_unready",
            $"Default child {defaultRelation} has {validation.UnreadyDefaultIndexCount} unready index(es).");
        return blockers;

        void Add(bool condition, string code, string detail)
        {
            if (condition)
            {
                blockers.Add(
                    new SnapshotGenerationRetentionBlocker(
                        code,
                        detail));
            }
        }
    }

    private static
        SnapshotGenerationRetentionNumericChildIndexValidation
        BuildPrimaryNumericChildIndexValidation(
        string instrument,
        long snapshotId,
        string childRelation,
        IReadOnlyList<SnapshotGenerationRetentionIndex>
            childIndexes,
        IReadOnlyList<SnapshotGenerationRetentionIndex>
            parentIndexes)
    {
        var parentByOid = parentIndexes.ToDictionary(
            static index => index.IndexOid);
        var missingParentIndexCount =
            parentIndexes.Count(parent =>
                childIndexes.All(child =>
                    child.ParentIndexOid !=
                        parent.IndexOid));
        var duplicateParentIndexCount =
            parentIndexes.Sum(parent =>
                Math.Max(
                    0,
                    childIndexes.Count(child =>
                        child.ParentIndexOid ==
                            parent.IndexOid) - 1));
        var detachedIndexCount =
            childIndexes.Count(index =>
                !index.ParentIndexOid.HasValue
                || !parentByOid.ContainsKey(
                    index.ParentIndexOid.Value));
        var attributeMismatchIndexCount =
            childIndexes.Count(index =>
                index.ParentIndexOid.HasValue
                && parentByOid.TryGetValue(
                    index.ParentIndexOid.Value,
                    out var parent)
                && (index.IsPrimary != parent.IsPrimary
                    || index.IsUnique != parent.IsUnique
                    || index.AccessMethod !=
                        parent.AccessMethod));
        return new
            SnapshotGenerationRetentionNumericChildIndexValidation(
                instrument,
                snapshotId,
                childRelation,
                childIndexes
                    .Select(
                        SnapshotGenerationRetentionIndexTopologyValidation
                            .IndexKey)
                    .Order(StringComparer.Ordinal)
                    .ToArray(),
                parentIndexes.Count,
                missingParentIndexCount,
                duplicateParentIndexCount,
                detachedIndexCount,
                childIndexes.Count(index =>
                    !index.IsValid
                    || index.RelationKind != "i"),
                childIndexes.Count(index =>
                    !index.IsReady),
                attributeMismatchIndexCount);
    }

    private static async Task<IReadOnlyDictionary<string, long>>
        LoadDefaultCountsAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            int commandTimeoutSeconds,
            CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandTimeout = commandTimeoutSeconds;
        command.CommandText = """
            SELECT root_relation, row_count
            FROM (
                SELECT
                    'leaderboard_entries_snapshot_solo_guitar'::TEXT
                        AS root_relation,
                    COUNT(*)::BIGINT AS row_count
                FROM ONLY
                    leaderboard_entries_snapshot_solo_guitar_default
                UNION ALL
                SELECT
                    'leaderboard_entries_snapshot_solo_bass',
                    COUNT(*)::BIGINT
                FROM ONLY
                    leaderboard_entries_snapshot_solo_bass_default
                UNION ALL
                SELECT
                    'leaderboard_entries_snapshot_solo_vocals',
                    COUNT(*)::BIGINT
                FROM ONLY
                    leaderboard_entries_snapshot_solo_vocals_default
                UNION ALL
                SELECT
                    'leaderboard_entries_snapshot_solo_drums',
                    COUNT(*)::BIGINT
                FROM ONLY
                    leaderboard_entries_snapshot_solo_drums_default
                UNION ALL
                SELECT
                    'leaderboard_entries_snapshot_pro_guitar',
                    COUNT(*)::BIGINT
                FROM ONLY
                    leaderboard_entries_snapshot_pro_guitar_default
                UNION ALL
                SELECT
                    'leaderboard_entries_snapshot_pro_bass',
                    COUNT(*)::BIGINT
                FROM ONLY
                    leaderboard_entries_snapshot_pro_bass_default
                UNION ALL
                SELECT
                    'leaderboard_entries_snapshot_pro_vocals',
                    COUNT(*)::BIGINT
                FROM ONLY
                    leaderboard_entries_snapshot_pro_vocals_default
                UNION ALL
                SELECT
                    'leaderboard_entries_snapshot_pro_cymbals',
                    COUNT(*)::BIGINT
                FROM ONLY
                    leaderboard_entries_snapshot_pro_cymbals_default
                UNION ALL
                SELECT
                    'leaderboard_entries_snapshot_pro_drums',
                    COUNT(*)::BIGINT
                FROM ONLY
                    leaderboard_entries_snapshot_pro_drums_default
            ) counts
            ORDER BY root_relation
            """;

        var counts = new Dictionary<string, long>(
            StringComparer.Ordinal);
        await using var reader =
            await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            counts[reader.GetString(0)] = reader.GetInt64(1);
        return counts;
    }

    private static async Task<PrimaryReferenceState>
        LoadPrimaryReferencesAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            IReadOnlyList<SnapshotGenerationRetentionChild>
                children,
            IReadOnlyList<NamedPublicationDescriptor>
                namedPublications,
            long configuredResumeScrapeId,
            int commandTimeoutSeconds,
            CancellationToken ct)
    {
        var rootReasons =
            new Dictionary<string, HashSet<string>>(
                StringComparer.Ordinal);
        var childBlockers =
            new Dictionary<
                string,
                List<SnapshotGenerationRetentionBlocker>>(
                StringComparer.Ordinal);
        var globalBlockers =
            new List<SnapshotGenerationRetentionBlocker>();
        var byLogicalIdentity = children
            .GroupBy(static child =>
                (
                    child.InstrumentDefinition.Instrument,
                    child.SnapshotId))
            .ToDictionary(
                static group => group.Key,
                static group => group.ToArray());
        var supportedInstruments =
            SnapshotGenerationRetentionContract.Instruments
                .Select(static instrument =>
                    instrument.Instrument)
                .ToHashSet(StringComparer.Ordinal);

        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandTimeout = commandTimeoutSeconds;
            command.CommandText = """
                SELECT
                    song_id,
                    instrument,
                    active_snapshot_id,
                    scrape_id,
                    is_finalized
                FROM leaderboard_snapshot_state
                ORDER BY instrument, song_id
                """;
            await using var reader =
                await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var songId = reader.GetString(0);
                var instrument = reader.GetString(1);
                var snapshotId = reader.IsDBNull(2)
                    ? (long?)null
                    : reader.GetInt64(2);
                var scrapeId = reader.IsDBNull(3)
                    ? (long?)null
                    : reader.GetInt64(3);
                var finalized = reader.GetBoolean(4);
                if (!supportedInstruments.Contains(instrument))
                {
                    globalBlockers.Add(
                        new SnapshotGenerationRetentionBlocker(
                            "active_snapshot_instrument_invalid",
                            $"Active snapshot row {songId} has unsupported instrument {instrument}."));
                    continue;
                }
                if (snapshotId is > 0)
                {
                    AddRoot(
                        instrument,
                        snapshotId.Value,
                        "active_snapshot",
                        requirePhysicalChild: true);
                    if (!finalized
                        || scrapeId != snapshotId)
                    {
                        AddChildBlocker(
                            instrument,
                            snapshotId.Value,
                            "active_snapshot_state_invalid",
                            $"Active snapshot row {songId}/{instrument} is not finalized or scrape-aligned.");
                    }
                }
                else if (snapshotId.HasValue)
                {
                    globalBlockers.Add(
                        new SnapshotGenerationRetentionBlocker(
                            "active_snapshot_id_invalid",
                            $"Active snapshot row {songId}/{instrument} has invalid snapshot ID {snapshotId.Value}."));
                }
            }
        }

        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandTimeout = commandTimeoutSeconds;
            command.CommandText = """
                SELECT
                    song_id,
                    instrument,
                    source_snapshot_id,
                    source_kind,
                    status,
                    row_count
                FROM solo_current_projection_scope
                ORDER BY instrument, song_id
                """;
            await using var reader =
                await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var songId = reader.GetString(0);
                var instrument = reader.GetString(1);
                var snapshotId = reader.IsDBNull(2)
                    ? (long?)null
                    : reader.GetInt64(2);
                var sourceKind = reader.GetString(3);
                var status = reader.GetString(4);
                var rowCount = reader.GetInt64(5);
                if (!supportedInstruments.Contains(instrument))
                {
                    globalBlockers.Add(
                        new SnapshotGenerationRetentionBlocker(
                            "projection_instrument_invalid",
                            $"Projection row {songId} has unsupported instrument {instrument}."));
                    continue;
                }
                if (snapshotId is > 0)
                {
                    AddRoot(
                        instrument,
                        snapshotId.Value,
                        "projection_source",
                        requirePhysicalChild: true);
                    if (!string.Equals(
                            sourceKind,
                            "snapshot",
                            StringComparison.Ordinal)
                        || !string.Equals(
                            status,
                            "ready",
                            StringComparison.Ordinal)
                        || rowCount < 0)
                    {
                        AddChildBlocker(
                            instrument,
                            snapshotId.Value,
                            "projection_source_invalid",
                            $"Projection row {songId}/{instrument} has inconsistent source kind, status, or row count.");
                    }
                }
                else if (string.Equals(
                             sourceKind,
                             "snapshot",
                             StringComparison.Ordinal))
                {
                    globalBlockers.Add(
                        new SnapshotGenerationRetentionBlocker(
                            "projection_source_missing",
                            $"Projection row {songId}/{instrument} declares a snapshot source without a positive snapshot ID."));
                }
            }
        }

        var publicationSourceValidations =
            new List<
                SnapshotGenerationRetentionPublicationSourceValidation>();
        if (namedPublications.Count > 0)
        {
            var publicationIds = namedPublications
                .Select(static publication =>
                    publication.PublicationId)
                .ToArray();
            var scrapeIds = namedPublications
                .Select(static publication =>
                    publication.ScrapeId)
                .ToArray();
            var bindings =
                new Dictionary<long, PublicationSourceBindingRow>();
            await using (var bindingCommand =
                         connection.CreateCommand())
            {
                bindingCommand.Transaction = transaction;
                bindingCommand.CommandTimeout =
                    commandTimeoutSeconds;
                bindingCommand.CommandText = """
                    SELECT
                        publication_id,
                        binding_kind,
                        binding_json::TEXT,
                        row_count,
                        content_hash,
                        status
                    FROM publication_surface_bindings
                    WHERE publication_id =
                            ANY(@publicationIds)
                      AND surface_name =
                            'solo_scope_sources'
                    ORDER BY publication_id
                    """;
                bindingCommand.Parameters.AddWithValue(
                    "publicationIds",
                    publicationIds);
                await using var bindingReader =
                    await bindingCommand
                        .ExecuteReaderAsync(ct);
                while (await bindingReader.ReadAsync(ct))
                {
                    bindings.Add(
                        bindingReader.GetInt64(0),
                        new PublicationSourceBindingRow(
                            bindingReader.GetString(1),
                            bindingReader.GetString(2),
                            bindingReader.IsDBNull(3)
                                ? null
                                : bindingReader.GetInt64(3),
                            bindingReader.IsDBNull(4)
                                ? null
                                : bindingReader.GetString(4),
                            bindingReader.GetString(5)));
                }
            }

            var sources = namedPublications.ToDictionary(
                static publication => publication.ScrapeId,
                static _ => new PublicationSourceAccumulator());
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandTimeout = commandTimeoutSeconds;
            command.CommandText = """
                SELECT
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
                    is_complete
                FROM leaderboard_published_scope_source
                WHERE published_scrape_id =
                        ANY(@namedPublicationScrapeIds)
                ORDER BY
                    published_scrape_id,
                    instrument,
                    song_id,
                    scope_kind
                """;
            command.Parameters.AddWithValue(
                "namedPublicationScrapeIds",
                scrapeIds);
            await using var reader =
                await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var publishedScrapeId = reader.GetInt64(0);
                var songId = reader.GetString(1);
                var instrument = reader.GetString(2);
                var scopeKind = reader.GetString(3);
                var sourceKind = reader.GetString(4);
                var snapshotId = reader.IsDBNull(5)
                    ? (long?)null
                    : reader.GetInt64(5);
                var sourceScrapeId = reader.GetInt64(6);
                var rowCount = reader.GetInt64(7);
                var contentFingerprint =
                    reader.GetString(8);
                var coverageFingerprint =
                    reader.GetString(9);
                var reportedTotalEntries =
                    reader.GetInt64(10);
                var reportedTotalPages =
                    reader.GetInt32(11);
                var complete = reader.GetBoolean(12);
                var source = sources[publishedScrapeId];
                source.ActualRowCount++;
                if (!source.Keys.Add(
                        (instrument, songId, scopeKind)))
                {
                    source.DuplicateKeyCount++;
                }
                source.OrderedKeys.Add(
                    new PublishedScopeSourceKey(
                        instrument,
                        songId,
                        scopeKind));
                if (string.IsNullOrWhiteSpace(songId)
                    || !supportedInstruments.Contains(instrument))
                {
                    source.InvalidRowCount++;
                    globalBlockers.Add(
                        new SnapshotGenerationRetentionBlocker(
                            "publication_source_instrument_invalid",
                            $"Named publication source {publishedScrapeId}/{songId} has unsupported instrument {instrument}."));
                    continue;
                }
                if (snapshotId is > 0)
                {
                    AddRoot(
                        instrument,
                        snapshotId.Value,
                        "named_publication_source",
                        requirePhysicalChild: true);
                    if (!string.Equals(
                            sourceKind,
                            "snapshot",
                            StringComparison.Ordinal)
                        || !string.Equals(
                            scopeKind,
                            "alltime",
                            StringComparison.Ordinal)
                        || sourceScrapeId != snapshotId
                        || sourceScrapeId >
                            publishedScrapeId
                        || rowCount <= 0
                        || reportedTotalEntries < rowCount
                        || reportedTotalPages <= 0
                        || string.IsNullOrWhiteSpace(
                            contentFingerprint)
                        || string.IsNullOrWhiteSpace(
                            coverageFingerprint)
                        || !complete)
                    {
                        source.InvalidRowCount++;
                        AddChildBlocker(
                            instrument,
                            snapshotId.Value,
                            "publication_source_invalid",
                            $"Named publication source {publishedScrapeId}/{songId}/{instrument} is incomplete or has inconsistent physical provenance.");
                    }
                }
                else if (!string.Equals(
                             sourceKind,
                             "empty",
                             StringComparison.Ordinal)
                         || !string.Equals(
                             scopeKind,
                             "alltime",
                             StringComparison.Ordinal)
                         || sourceScrapeId <= 0
                         || sourceScrapeId >
                            publishedScrapeId
                         || rowCount != 0
                         || reportedTotalEntries != 0
                         || reportedTotalPages != 0
                         || string.IsNullOrWhiteSpace(
                             contentFingerprint)
                         || string.IsNullOrWhiteSpace(
                             coverageFingerprint)
                         || !complete)
                {
                    source.InvalidRowCount++;
                    globalBlockers.Add(
                        new SnapshotGenerationRetentionBlocker(
                            "publication_source_invalid",
                            $"Named publication source {publishedScrapeId}/{songId}/{instrument} has invalid empty provenance."));
                }
            }

            foreach (var publication in namedPublications)
            {
                var source = sources[publication.ScrapeId];
                bindings.TryGetValue(
                    publication.PublicationId,
                    out var binding);
                var actualKeyHash =
                    PublishedScopeSourceBindingContract
                        .ComputeKeyHash(
                            source.OrderedKeys);
                var validation =
                    new SnapshotGenerationRetentionPublicationSourceValidation(
                        publication.Slot,
                        publication.PublicationId,
                        publication.ScrapeId,
                        publication
                            .ExpectedPublishedScopeCount,
                        binding?.RowCount,
                        source.ActualRowCount,
                        binding?.ContentHash,
                        actualKeyHash,
                        source.InvalidRowCount,
                        source.DuplicateKeyCount,
                        BindingIdentityMatches(
                            binding,
                            publication));
                publicationSourceValidations.Add(validation);
                if (!validation.IsValid)
                {
                    globalBlockers.Add(
                        new SnapshotGenerationRetentionBlocker(
                            "named_publication_source_set_invalid",
                            $"Named {publication.Slot} publication {publication.PublicationId}/{publication.ScrapeId} does not match its authoritative ready scope-source binding: {validation.ComparisonKey}."));
                }
            }
        }

        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandTimeout = commandTimeoutSeconds;
            command.CommandText = """
                SELECT id::BIGINT
                FROM scrape_log
                WHERE status = 'running'
                ORDER BY id
                """;
            await using var reader =
                await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var scrapeId = reader.GetInt64(0);
                foreach (var child in children.Where(
                             child =>
                                 child.SnapshotId == scrapeId))
                {
                    AddRootByKey(
                        child.PhysicalKey,
                        "running_scrape");
                }
            }
        }

        if (configuredResumeScrapeId > 0)
        {
            foreach (var child in children.Where(
                         child =>
                             child.SnapshotId ==
                             configuredResumeScrapeId))
            {
                AddRootByKey(
                    child.PhysicalKey,
                    "configured_resume_scrape");
            }
        }

        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandTimeout = commandTimeoutSeconds;
            command.CommandText = """
                SELECT
                    scrape_id,
                    instrument,
                    writer_kind,
                    song_id
                FROM scrape_writer_failures
                WHERE replayed_at IS NULL
                ORDER BY scrape_id, instrument, writer_kind, song_id
                """;
            await using var reader =
                await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var scrapeId = reader.GetInt64(0);
                var instrument = reader.GetString(1);
                var writerKind = reader.GetString(2);
                var songId = reader.GetString(3);
                if (!supportedInstruments.Contains(instrument))
                {
                    globalBlockers.Add(
                        new SnapshotGenerationRetentionBlocker(
                            "writer_failure_instrument_invalid",
                            $"Unreplayed writer failure {scrapeId}/{writerKind}/{songId} has unsupported instrument {instrument}."));
                    continue;
                }
                AddRoot(
                    instrument,
                    scrapeId,
                    "unreplayed_writer_failure",
                    requirePhysicalChild: false);
            }
        }

        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandTimeout = commandTimeoutSeconds;
            command.CommandText = """
                SELECT
                    instrument,
                    snapshot_id,
                    hold_kind,
                    reason
                FROM snapshot_generation_retention_holds
                WHERE released_at IS NULL
                ORDER BY instrument, snapshot_id, hold_kind
                """;
            await using var reader =
                await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var instrument = reader.GetString(0);
                var snapshotId = reader.GetInt64(1);
                var holdKind = reader.GetString(2);
                var reason = reader.GetString(3);
                AddRoot(
                    instrument,
                    snapshotId,
                    $"hold:{holdKind}:{reason}",
                    requirePhysicalChild: false);
            }
        }

        var snapshotIds = children
            .Select(static child => child.SnapshotId)
            .Distinct()
            .ToArray();
        var scrapes = new Dictionary<
            long,
            (string Status, DateTime? CompletedAt, DateTime? FailedAt)>();
        if (snapshotIds.Length > 0)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandTimeout = commandTimeoutSeconds;
            command.CommandText = """
                SELECT
                    id::BIGINT,
                    status,
                    completed_at,
                    failed_at
                FROM scrape_log
                WHERE id = ANY(@snapshotIds)
                """;
            command.Parameters.AddWithValue(
                "snapshotIds",
                snapshotIds);
            await using var reader =
                await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                scrapes[reader.GetInt64(0)] = (
                    reader.GetString(1),
                    reader.IsDBNull(2)
                        ? null
                        : reader.GetDateTime(2),
                    reader.IsDBNull(3)
                        ? null
                        : reader.GetDateTime(3));
            }
        }

        foreach (var child in children)
        {
            if (!scrapes.TryGetValue(
                    child.SnapshotId,
                    out var scrape))
            {
                AddChildBlockerByKey(
                    child.PhysicalKey,
                    "scrape_provenance_missing",
                    $"Child {child.ChildRelation} has no scrape_log provenance row.");
                continue;
            }

            var terminal = scrape.Status switch
            {
                "completed" => scrape.CompletedAt.HasValue,
                "failed" => scrape.FailedAt.HasValue,
                _ => false,
            };
            if (!terminal)
            {
                AddChildBlockerByKey(
                    child.PhysicalKey,
                    "scrape_not_terminal",
                    $"Child {child.ChildRelation} belongs to nonterminal scrape status {scrape.Status}.");
            }
        }

        return new PrimaryReferenceState(
            rootReasons.ToDictionary(
                static item => item.Key,
                static item =>
                    (IReadOnlyList<string>)item.Value
                        .OrderBy(
                            static reason => reason,
                            StringComparer.Ordinal)
                        .ToArray(),
                StringComparer.Ordinal),
            childBlockers.ToDictionary(
                static item => item.Key,
                static item =>
                    (IReadOnlyList<
                        SnapshotGenerationRetentionBlocker>)
                        item.Value
                            .DistinctBy(static blocker =>
                                (blocker.Code, blocker.Detail))
                            .OrderBy(
                                static blocker => blocker.Code,
                                StringComparer.Ordinal)
                            .ToArray(),
                StringComparer.Ordinal),
            globalBlockers
                .DistinctBy(static blocker =>
                    (blocker.Code, blocker.Detail))
                .OrderBy(
                    static blocker => blocker.Code,
                    StringComparer.Ordinal)
                .ToArray(),
            publicationSourceValidations
                .OrderBy(
                    static validation =>
                        validation.Slot,
                    StringComparer.Ordinal)
                .ToArray());

        static bool BindingIdentityMatches(
            PublicationSourceBindingRow? binding,
            NamedPublicationDescriptor publication)
        {
            if (binding is null
                || !publication.MetadataIdentityValid
                || !string.Equals(
                    binding.BindingKind,
                    PublishedScopeSourceBindingContract
                        .BindingKind,
                    StringComparison.Ordinal)
                || !string.Equals(
                    binding.Status,
                    "ready",
                    StringComparison.Ordinal))
            {
                return false;
            }

            using var document =
                JsonDocument.Parse(binding.BindingJson);
            var root = document.RootElement;
            return root.ValueKind ==
                    JsonValueKind.Object
                && root.TryGetProperty(
                    "publicationId",
                    out var publicationId)
                && publicationId.TryGetInt64(
                    out var parsedPublicationId)
                && parsedPublicationId ==
                    publication.PublicationId
                && root.TryGetProperty(
                    "publishedScrapeId",
                    out var scrapeId)
                && scrapeId.TryGetInt64(
                    out var parsedScrapeId)
                && parsedScrapeId ==
                    publication.ScrapeId
                && root.TryGetProperty(
                    "table",
                    out var table)
                && table.ValueKind ==
                    JsonValueKind.String
                && string.Equals(
                    table.GetString(),
                    PublishedScopeSourceBindingContract
                        .TableName,
                    StringComparison.Ordinal)
                && root.TryGetProperty(
                    "keyHashVersion",
                    out var hashVersion)
                && hashVersion.TryGetInt32(
                    out var parsedHashVersion)
                && parsedHashVersion ==
                    PublishedScopeSourceBindingContract
                        .KeyHashVersion;
        }

        void AddRoot(
            string instrument,
            long snapshotId,
            string reason,
            bool requirePhysicalChild)
        {
            if (snapshotId <= 0)
            {
                globalBlockers.Add(
                    new SnapshotGenerationRetentionBlocker(
                        "protected_snapshot_id_invalid",
                        $"Root {reason} has invalid snapshot ID {snapshotId}."));
                return;
            }

            if (!byLogicalIdentity.TryGetValue(
                    (instrument, snapshotId),
                    out var matches))
            {
                if (requirePhysicalChild)
                {
                    globalBlockers.Add(
                        new SnapshotGenerationRetentionBlocker(
                            "protected_child_missing",
                            $"Physical root {reason} requires missing child ({instrument}, {snapshotId})."));
                }
                return;
            }

            foreach (var child in matches)
                AddRootByKey(child.PhysicalKey, reason);
        }

        void AddRootByKey(
            string key,
            string reason)
        {
            if (!rootReasons.TryGetValue(
                    key,
                    out var reasons))
            {
                reasons = new HashSet<string>(
                    StringComparer.Ordinal);
                rootReasons[key] = reasons;
            }
            reasons.Add(reason);
        }

        void AddChildBlocker(
            string instrument,
            long snapshotId,
            string code,
            string detail)
        {
            if (!byLogicalIdentity.TryGetValue(
                    (instrument, snapshotId),
                    out var matches))
            {
                globalBlockers.Add(
                    new SnapshotGenerationRetentionBlocker(
                        code,
                        detail));
                return;
            }
            foreach (var child in matches)
                AddChildBlockerByKey(
                    child.PhysicalKey,
                    code,
                    detail);
        }

        void AddChildBlockerByKey(
            string key,
            string code,
            string detail)
        {
            if (!childBlockers.TryGetValue(
                    key,
                    out var blockers))
            {
                blockers =
                    new List<
                        SnapshotGenerationRetentionBlocker>();
                childBlockers[key] = blockers;
            }
            blockers.Add(
                new SnapshotGenerationRetentionBlocker(
                    code,
                    detail));
        }
    }

    private sealed record SafePointState(
        long? PublishedScrapeId,
        long? CurrentPublicationId,
        long? PreviousPublicationId,
        long? WorkingPublicationId,
        IReadOnlyList<NamedPublicationDescriptor>
            NamedPublications,
        IReadOnlyList<SnapshotGenerationRetentionBlocker>
            DeferralBlockers,
        IReadOnlyList<SnapshotGenerationRetentionBlocker>
            GlobalBlockers,
        IReadOnlyList<SnapshotGenerationRetentionAnomaly>
            Anomalies);

    private sealed record RegistrationDrainSnapshot(
        int RunnableBackfills,
        int RepairableHistory,
        IReadOnlyList<SnapshotGenerationRetentionBlocker>
            TerminalBlockers);

    private sealed record PublicationGenerationRow(
        long PublicationId,
        long? ScrapeId,
        string Status,
        long? PreviousPublicationId,
        string? ExpectedPublishedScopeCount,
        string? MetadataScrapeId,
        string? MetadataPublicationId);

    private sealed record NamedPublicationDescriptor(
        string Slot,
        long PublicationId,
        long ScrapeId,
        long? ExpectedPublishedScopeCount,
        bool MetadataIdentityValid);

    private sealed record TopologyRelation(
        long Oid,
        string SchemaName,
        string RelationName,
        string RelationKind,
        string PersistenceKind,
        long Relfilenode,
        string PartitionKey,
        string PartitionBound,
        string TablespaceName,
        string AccessMethod,
        IReadOnlyList<string> RelationOptions,
        long? ParentOid,
        string? ParentSchemaName,
        string? ParentRelationName,
        long RowEstimate,
        long TotalBytes);

    private sealed record TopologyState(
        IReadOnlyList<SnapshotGenerationRetentionChild>
            Children,
        IReadOnlyList<SnapshotGenerationRetentionBlocker>
            GlobalBlockers,
        IReadOnlyList<
            SnapshotGenerationRetentionIndexTopologyValidation>
            IndexTopologyValidations);

    private sealed record PrimaryReferenceState(
        IReadOnlyDictionary<string, IReadOnlyList<string>>
            RootReasons,
        IReadOnlyDictionary<
            string,
            IReadOnlyList<SnapshotGenerationRetentionBlocker>>
            ChildBlockers,
        IReadOnlyList<SnapshotGenerationRetentionBlocker>
            GlobalBlockers,
        IReadOnlyList<
            SnapshotGenerationRetentionPublicationSourceValidation>
            PublicationSourceValidations);

    private sealed record PublicationSourceBindingRow(
        string BindingKind,
        string BindingJson,
        long? RowCount,
        string? ContentHash,
        string Status);

    private sealed class PublicationSourceAccumulator
    {
        internal long ActualRowCount { get; set; }
        internal int InvalidRowCount { get; set; }
        internal int DuplicateKeyCount { get; set; }
        internal HashSet<(string Instrument, string SongId, string ScopeKind)>
            Keys { get; } = [];
        internal List<PublishedScopeSourceKey>
            OrderedKeys { get; } = [];
    }
}
