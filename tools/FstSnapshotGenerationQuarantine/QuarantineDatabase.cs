using System.Buffers;
using System.Data;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Npgsql;
using NpgsqlTypes;

namespace FstSnapshotGenerationQuarantine;

public sealed class QuarantineDatabase : IAsyncDisposable
{
    public const string DefaultConnectionEnvironment =
        "FST_SNAPSHOT_QUARANTINE_CONNECTION_STRING";

    private const string SnapshotFingerprintCopySql = """
        COPY (
            SELECT to_jsonb(row_value)::text
            FROM leaderboard_entries_snapshot AS row_value
            WHERE snapshot_id =
                    current_setting(
                        'fst.quarantine_snapshot_id')::BIGINT
              AND instrument =
                    current_setting(
                        'fst.quarantine_instrument')
            ORDER BY
                snapshot_id,
                song_id,
                instrument,
                account_id
        ) TO STDOUT
        """;

    private readonly NpgsqlDataSource _dataSource;

    public static Action<string>? ReattachTestHook { get; set; }

    private QuarantineDatabase(NpgsqlDataSource dataSource)
    {
        _dataSource = dataSource;
    }

    public static QuarantineDatabase FromEnvironment(
        string? connectionEnvironment = null,
        int statementTimeoutSeconds = 120)
    {
        var environmentName = string.IsNullOrWhiteSpace(
            connectionEnvironment)
            ? DefaultConnectionEnvironment
            : connectionEnvironment;
        var raw = Environment.GetEnvironmentVariable(
            environmentName);
        if (string.IsNullOrWhiteSpace(raw))
        {
            throw new InvalidOperationException(
                $"{environmentName} is required.");
        }
        return FromConnectionString(
            raw,
            statementTimeoutSeconds);
    }

    public static QuarantineDatabase FromConnectionString(
        string connectionString,
        int statementTimeoutSeconds = 120)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new ArgumentException(
                "Connection string is required.",
                nameof(connectionString));
        var builder = new NpgsqlConnectionStringBuilder(
            connectionString)
        {
            ApplicationName =
                "fst-snapshot-generation-quarantine",
            Timeout = Math.Clamp(
                statementTimeoutSeconds,
                1,
                15),
            CommandTimeout = Math.Clamp(
                statementTimeoutSeconds,
                5,
                600),
            MinPoolSize = 0,
            MaxPoolSize = 2,
        };
        if ((builder.Host ?? "").Split(
                    ',',
                    StringSplitOptions.RemoveEmptyEntries
                    | StringSplitOptions.TrimEntries)
                .Length != 1
            || string.IsNullOrWhiteSpace(builder.Database)
            || string.IsNullOrWhiteSpace(builder.Username))
        {
            throw new InvalidOperationException(
                "The quarantine connection string must specify one PostgreSQL host, database, and username.");
        }
        if (builder.Options?.Contains(
                "default_transaction_read_only=on",
                StringComparison.OrdinalIgnoreCase) is true)
        {
            throw new InvalidOperationException(
                "The quarantine connection string is read-only.");
        }

        var safeguards =
            $"-c statement_timeout={Math.Clamp(statementTimeoutSeconds, 5, 600) * 1000} "
            + "-c lock_timeout=5000 "
            + "-c idle_in_transaction_session_timeout=120000";
        builder.Options = string.IsNullOrWhiteSpace(
            builder.Options)
            ? safeguards
            : $"{builder.Options} {safeguards}";
        return new QuarantineDatabase(
            NpgsqlDataSource.Create(
                builder.ConnectionString));
    }

    public async ValueTask DisposeAsync() =>
        await _dataSource.DisposeAsync();

    public async Task<QuarantineDatabaseSnapshot>
        ReadSnapshotAsync(
            ArchivePackageEvidence archive,
            CancellationToken ct = default)
    {
        await using var connection =
            await _dataSource.OpenConnectionAsync(ct);
        return await ReadSnapshotAsync(
            connection,
            transaction: null,
            archive,
            ct);
    }

    public static void ValidateSnapshot(
        QuarantineDatabaseSnapshot snapshot,
        ArchivePackageEvidence archive,
        SourceScrapeEvidence source,
        RouteParityEvidence parity)
    {
        var failures = new List<string>();
        AddIf(
            !string.Equals(
                snapshot.DatabaseName,
                archive.DatabaseName,
                StringComparison.Ordinal),
            "database-name");
        AddIf(
            snapshot.DatabaseOid != archive.DatabaseOid,
            "database-oid");
        AddIf(
            !string.Equals(
                snapshot.SystemIdentifier,
                archive.SystemIdentifier,
                StringComparison.Ordinal),
            "system-identifier");
        AddIf(
            snapshot.ServerVersionNum !=
                archive.ServerVersionNum
            || snapshot.ServerVersionNum / 10_000 != 17,
            "server-version");
        AddIf(
            snapshot.CurrentPublicationId !=
                archive.TriggerPublicationId
            || snapshot.CurrentPublicationId !=
                parity.PublicationId,
            "current-publication");
        AddIf(
            snapshot.PublishedScrapeId !=
                archive.TriggerScrapeId
            || snapshot.PublishedScrapeId !=
                source.PublishedScrapeId
            || snapshot.PublishedScrapeId !=
                parity.PublishedScrapeId,
            "published-scrape");
        AddIf(
            snapshot.PublicReadsFrozen,
            "public-reads-frozen");
        AddIf(
            snapshot.WorkingPublicationId is not null,
            "working-publication");
        AddIf(
            snapshot.PublicationCommitIntentActive,
            "publication-commit-intent");
        AddIf(
            snapshot.MaxScoreMutationGateActive,
            "max-score-mutation-gate");
        AddIf(
            !snapshot.NotificationsComplete,
            "notifications-incomplete");
        AddIf(
            !snapshot.TriggerScrapeCompleted,
            "trigger-scrape-not-completed");
        AddIf(
            !snapshot.TriggerPublicationCurrent,
            "trigger-publication-not-current");
        AddIf(
            snapshot.LatestCycleId != archive.CycleId
            || snapshot.CycleTriggerScrapeId !=
                archive.TriggerScrapeId
            || snapshot.CycleTriggerPublicationId !=
                archive.TriggerPublicationId,
            "latest-cycle");
        AddIf(
            !string.Equals(
                snapshot.CycleStatus,
                "observed",
                StringComparison.Ordinal)
            || !snapshot.ReportOnly
            || !snapshot.OracleAgreement
            || snapshot.BlockedCount != 0
            || snapshot.GlobalBlockerCount != 0
            || snapshot.CandidateCount <= 0
            || !snapshot.PlannerOracleSetsEqual,
            "cycle-acceptance");
        AddIf(
            !string.Equals(
                snapshot.CandidateIdentityHash,
                archive.CandidateIdentityHash,
                StringComparison.Ordinal)
            || !string.Equals(
                snapshot.ObservationHash,
                archive.ObservationHash,
                StringComparison.Ordinal),
            "cycle-hashes");
        AddIf(
            snapshot.ObservationId !=
                archive.ObservationId
            || !string.Equals(
                snapshot.Instrument,
                archive.Instrument,
                StringComparison.Ordinal)
            || snapshot.SnapshotId != archive.SnapshotId,
            "observation-identity");
        AddIf(
            !string.Equals(
                snapshot.RootRelation,
                archive.RootRelation,
                StringComparison.Ordinal)
            || snapshot.RootOid != archive.RootOid
            || !string.Equals(
                snapshot.ChildRelation,
                archive.ChildRelation,
                StringComparison.Ordinal)
            || snapshot.ChildOid != archive.ChildOid
            || snapshot.ChildRelfilenode !=
                archive.ChildRelfilenode,
            "physical-identity");
        AddIf(
            !string.Equals(
                snapshot.StableChildIdentityHash,
                archive.StableChildIdentityHash,
                StringComparison.Ordinal)
            || !string.Equals(
                snapshot.StableConfigSchemaHash,
                archive.StableConfigSchemaHash,
                StringComparison.Ordinal),
            "stable-hashes");
        AddIf(
            !string.Equals(
                snapshot.Classification,
                "candidate",
                StringComparison.Ordinal)
            || snapshot.PlannerLive
            || snapshot.OracleLive
            || snapshot.BlockerCount != 0,
            "candidate-classification");
        AddIf(
            snapshot.CurrentRowCount != archive.RowCount,
            "row-count");
        AddIf(
            snapshot.CurrentTotalBytes !=
                archive.TotalBytes,
            "total-bytes");
        AddIf(
            snapshot.RunningScrapeCount != 0,
            "running-scrape");
        AddIf(
            snapshot.ActiveHoldCount != 0,
            "active-hold");
        AddIf(
            snapshot.UnreplayedWriterFailureCount != 0,
            "writer-failure");
        AddIf(
            snapshot.AcceptedRecentCycleCount != 5
            || snapshot.AcceptedRecentPublicationCount < 2
            || snapshot
                .AcceptedRecentCandidateIdentityCount < 2,
            "five-cycle-gate");
        AddIf(
            archive.Instrument == "Solo_Bass"
            && archive.SnapshotId == 1308,
            "protected-solo-bass-1308");
        if (failures.Count > 0)
        {
            throw new InvalidDataException(
                "Database preflight failed: "
                + string.Join(", ", failures));
        }
        return;

        void AddIf(bool condition, string code)
        {
            if (condition)
                failures.Add(code);
        }
    }

    public async Task<SnapshotGenerationQuarantineExecutionReport>
        QuarantineAsync(
            SnapshotGenerationQuarantinePlan plan,
            string approvedBy,
            string approvalReference,
            CancellationToken ct = default)
    {
        plan.Validate();
        ValidateActor(approvedBy, nameof(approvedBy));
        ValidateActor(
            approvalReference,
            nameof(approvalReference));

        await using var connection =
            await _dataSource.OpenConnectionAsync(ct);
        await using var lockChain =
            await AcquireSessionLockChainAsync(
                connection,
                ct);
        await using var transaction =
            await connection.BeginTransactionAsync(
                IsolationLevel.Serializable,
                ct);
        try
        {
            await LockCandidateAsync(
                connection,
                transaction,
                plan.Archive,
                ct);
            var snapshot = await ReadSnapshotAsync(
                connection,
                transaction,
                plan.Archive,
                ct);
            ValidateSnapshot(
                snapshot,
                plan.Archive,
                plan.SourceScrape,
                plan.PreQuarantineParity);
            var fingerprint =
                await ComputeFingerprintAsync(
                    connection,
                    transaction,
                    plan.Archive.SnapshotId,
                    plan.Archive.Instrument,
                    ct);
            ValidateFingerprint(
                fingerprint,
                plan.Archive);

            var preflight = JsonSerializer.SerializeToElement(
                new
                {
                    Snapshot = snapshot,
                    Fingerprint = fingerprint,
                    PlanDigest = plan.PlanDigest,
                    OperationId = plan.OperationId,
                },
                QuarantineJson.Strict);
            var operationId =
                await ExecuteQuarantineFunctionAsync(
                    connection,
                    transaction,
                    plan,
                    approvedBy,
                    approvalReference,
                    preflight,
                    ct);
            await transaction.CommitAsync(ct);

            var state = await ReadOperationStateAsync(
                operationId,
                ct);
            return BuildExecutionReport(
                plan,
                state,
                "quarantine",
                "quarantined",
                approvedBy,
                approvalReference,
                preflight);
        }
        catch
        {
            await transaction.RollbackAsync(
                CancellationToken.None);
            throw;
        }
    }

    public async Task<SnapshotGenerationQuarantineExecutionReport>
        ReattachAsync(
            SnapshotGenerationQuarantinePlan plan,
            string reattachedBy,
            string reattachReference,
            CancellationToken ct = default)
    {
        plan.Validate();
        ValidateActor(reattachedBy, nameof(reattachedBy));
        ValidateActor(
            reattachReference,
            nameof(reattachReference));

        var evidence = JsonSerializer.SerializeToElement(
            new
            {
                PlanDigest = plan.PlanDigest,
                OperationId = plan.OperationId,
                RequestedAtUtc = DateTimeOffset.UtcNow,
            },
            QuarantineJson.Strict);
        await using var connection =
            await _dataSource.OpenConnectionAsync(ct);
        await using var lockChain =
            await AcquireSessionLockChainAsync(
                connection,
                ct);
        await using var transaction =
            await connection.BeginTransactionAsync(
                IsolationLevel.Serializable,
                ct);
        try
        {
            await using var command =
                connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                SELECT fst_reattach_snapshot_generation(
                    @operationId,
                    @planDigest,
                    @reattachedBy,
                    @reattachReference,
                    @evidence)
                """;
            command.Parameters.AddWithValue(
                "operationId",
                plan.OperationId!);
            command.Parameters.AddWithValue(
                "planDigest",
                plan.PlanDigest!);
            command.Parameters.AddWithValue(
                "reattachedBy",
                reattachedBy);
            command.Parameters.AddWithValue(
                "reattachReference",
                reattachReference);
            command.Parameters.Add(
                    "evidence",
                    NpgsqlDbType.Jsonb)
                .Value = evidence.GetRawText();
            var operationId =
                (string)(await command.ExecuteScalarAsync(ct)
                    ?? throw new InvalidOperationException(
                        "Reattach function returned no operation ID."));
            ReattachTestHook?.Invoke(
                "after-reattach-before-commit");
            await transaction.CommitAsync(ct);

            var state = await ReadOperationStateAsync(
                operationId,
                ct);
            var committedEvidence =
                await ReadReattachEvidenceAsync(
                    operationId,
                    ct);
            return BuildExecutionReport(
                plan,
                state,
                "reattach",
                "reattached",
                reattachedBy,
                reattachReference,
                committedEvidence);
        }
        catch
        {
            await transaction.RollbackAsync(
                CancellationToken.None);
            throw;
        }
    }

    public async Task<SnapshotGenerationQuarantineAttestationReport>
        RecordAttestationAsync(
            SnapshotGenerationQuarantinePlan plan,
            string stage,
            string actor,
            RouteParityEvidence parity,
            CancellationToken ct = default)
    {
        plan.Validate();
        ValidateActor(actor, nameof(actor));
        if (stage is not (
                "quarantined"
                or "soak"
                or "reattached"))
        {
            throw new ArgumentException(
                "Attestation stage must be quarantined, soak, or reattached.",
                nameof(stage));
        }
        if (parity.DifferenceCount != 0
            || !parity.StatusParity
            || !parity.SemanticJsonParity
            || parity.RouteCount != 55)
        {
            throw new InvalidDataException(
                "Attestation route parity is not exact.");
        }
        await using var connection =
            await _dataSource.OpenConnectionAsync(ct);
        await using var lockChain =
            await AcquireSessionLockChainAsync(
                connection,
                ct);
        await using var transaction =
            await connection.BeginTransactionAsync(
                IsolationLevel.Serializable,
                ct);
        try
        {
            var state = await ReadOperationStateAsync(
                connection,
                transaction,
                plan.OperationId!,
                ct);
            ValidateOperationMatchesPlan(state, plan);
            if ((stage == "reattached") != state.Reattached)
            {
                throw new InvalidDataException(
                    "Attestation stage differs from durable operation state.");
            }
            if (parity.PublicationId !=
                    state.CurrentPublicationId
                || parity.PublishedScrapeId !=
                    state.CurrentPublishedScrapeId
                || state.CurrentPublicReadsFrozen
                || state.RunningScrapeCount != 0
                || state.TargetReferenceCount != 0)
            {
                throw new InvalidDataException(
                    "Attestation publication or database safety state is invalid.");
            }
            if (stage == "quarantined"
                && (state.CurrentPublicationId !=
                        state.TriggerPublicationId
                    || state.CurrentPublishedScrapeId !=
                        state.TriggerScrapeId
                    || !string.Equals(
                        parity.BaselineManifestSha256,
                        plan.PreQuarantineParity
                            .CandidateManifestSha256,
                        StringComparison.Ordinal)))
            {
                throw new InvalidDataException(
                    "Initial quarantine attestation is not bound to the sealed pre-quarantine capture.");
            }
            if (stage == "reattached"
                && !string.Equals(
                    parity.BaselineManifestSha256,
                    state
                        .LatestSuccessfulSoakCandidateManifestSha256,
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    "Reattached attestation baseline is not the latest successful soak capture.");
            }

            var databaseEvidence =
                JsonSerializer.SerializeToElement(
                    state,
                    QuarantineJson.Strict);
            var evidenceHash = QuarantineJson.Sha256(
                new
                {
                    Stage = stage,
                    Parity = parity,
                    Database = state,
                });

            await using var command =
                connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                SELECT
                    fst_record_snapshot_generation_quarantine_attestation(
                        @operationId,
                        @stage,
                        @publicationId,
                        @publishedScrapeId,
                        @routeCount,
                        @statusParity,
                        @semanticJsonParity,
                        @differenceCount,
                        @baselineSha256,
                        @candidateSha256,
                        @databaseEvidence,
                        @evidenceSha256,
                        @actor)
                """;
            command.Parameters.AddWithValue(
                "operationId",
                plan.OperationId!);
            command.Parameters.AddWithValue(
                "stage",
                stage);
            command.Parameters.AddWithValue(
                "publicationId",
                parity.PublicationId);
            command.Parameters.AddWithValue(
                "publishedScrapeId",
                parity.PublishedScrapeId);
            command.Parameters.AddWithValue(
                "routeCount",
                parity.RouteCount);
            command.Parameters.AddWithValue(
                "statusParity",
                parity.StatusParity);
            command.Parameters.AddWithValue(
                "semanticJsonParity",
                parity.SemanticJsonParity);
            command.Parameters.AddWithValue(
                "differenceCount",
                parity.DifferenceCount);
            command.Parameters.AddWithValue(
                "baselineSha256",
                parity.BaselineManifestSha256);
            command.Parameters.AddWithValue(
                "candidateSha256",
                parity.CandidateManifestSha256);
            command.Parameters.Add(
                    "databaseEvidence",
                    NpgsqlDbType.Jsonb)
                .Value = databaseEvidence.GetRawText();
            command.Parameters.AddWithValue(
                "evidenceSha256",
                evidenceHash);
            command.Parameters.AddWithValue(
                "actor",
                actor);
            var attestationId = Convert.ToInt64(
                await command.ExecuteScalarAsync(ct));
            await transaction.CommitAsync(ct);

            return new SnapshotGenerationQuarantineAttestationReport(
                SchemaVersion: 1,
                ToolId:
                    FSTService.Persistence.Maintenance
                        .SnapshotGenerationQuarantineContract.ToolId,
                OperationId: plan.OperationId!,
                PlanDigest: plan.PlanDigest!,
                Stage: stage,
                AttestationId: attestationId,
                CompletedAtUtc: DateTimeOffset.UtcNow,
                Actor: actor,
                Parity: parity,
                DatabaseEvidence: databaseEvidence,
                EvidenceSha256: evidenceHash).Seal();
        }
        catch
        {
            await transaction.RollbackAsync(
                CancellationToken.None);
            throw;
        }
    }

    public async Task<QuarantineOperationState>
        ReadOperationStateAsync(
            string operationId,
            CancellationToken ct = default)
    {
        await using var connection =
            await _dataSource.OpenConnectionAsync(ct);
        return await ReadOperationStateAsync(
            connection,
            transaction: null,
            operationId,
            ct);
    }

    private async Task<JsonElement>
        ReadReattachEvidenceAsync(
            string operationId,
            CancellationToken ct)
    {
        await using var connection =
            await _dataSource.OpenConnectionAsync(ct);
        await using var command =
            connection.CreateCommand();
        command.CommandText = """
            SELECT reattach_evidence::TEXT
            FROM snapshot_generation_quarantine_reattachments
            WHERE operation_id = @operationId
            """;
        command.Parameters.AddWithValue(
            "operationId",
            operationId);
        var value = (string?)(
            await command.ExecuteScalarAsync(ct));
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidDataException(
                "Committed reattach evidence is unavailable.");
        }
        using var document = JsonDocument.Parse(value);
        return document.RootElement.Clone();
    }

    private static async Task<QuarantineOperationState>
        ReadOperationStateAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction? transaction,
            string operationId,
            CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT
                operation.operation_id,
                operation.plan_digest,
                operation.trigger_publication_id,
                operation.trigger_scrape_id,
                operation.instrument,
                operation.snapshot_id,
                operation.root_schema,
                operation.root_relation,
                operation.root_oid,
                operation.child_schema,
                operation.child_relation,
                operation.child_oid,
                operation.child_relfilenode,
                operation.quarantine_schema,
                operation.quarantine_relation,
                operation.snapshot_check_constraint,
                operation.mutation_guard_trigger,
                operation.default_partition_schema,
                operation.default_partition_relation,
                operation.default_partition_oid,
                operation.default_exclusion_constraint,
                operation.row_count,
                operation.row_fingerprint_sha256,
                state.current_publication_id::BIGINT,
                state.published_scrape_id::BIGINT,
                state.public_reads_frozen,
                (
                    SELECT COUNT(*)::INTEGER
                    FROM scrape_log running_scrape
                    WHERE running_scrape.status = 'running'),
                (
                    SELECT COUNT(*)::BIGINT
                    FROM (
                        SELECT 1
                        FROM leaderboard_snapshot_state
                            snapshot_state
                        WHERE snapshot_state.instrument =
                                operation.instrument
                          AND snapshot_state.active_snapshot_id =
                                operation.snapshot_id

                        UNION ALL

                        SELECT 1
                        FROM solo_current_projection_scope
                            projection
                        WHERE projection.instrument =
                                operation.instrument
                          AND projection.source_snapshot_id =
                                operation.snapshot_id

                        UNION ALL

                        SELECT 1
                        FROM leaderboard_published_scope_source
                            source
                        WHERE source.instrument =
                                operation.instrument
                          AND source.source_snapshot_id =
                                operation.snapshot_id
                          AND source.published_scrape_id IN (
                                SELECT generation.scrape_id
                                FROM publication_generations
                                    generation
                                WHERE generation.publication_id IN (
                                    state.current_publication_id,
                                    state.previous_publication_id,
                                    state.working_publication_id)
                                  AND generation.scrape_id
                                        IS NOT NULL)

                        UNION ALL

                        SELECT 1
                        FROM scrape_writer_failures failure
                        WHERE failure.instrument =
                                operation.instrument
                          AND failure.scrape_id =
                                operation.snapshot_id
                          AND failure.replayed_at IS NULL

                        UNION ALL

                        SELECT 1
                        FROM snapshot_generation_retention_holds
                            hold_row
                        WHERE hold_row.instrument =
                                operation.instrument
                          AND hold_row.snapshot_id =
                                operation.snapshot_id
                          AND hold_row.released_at IS NULL
                          AND hold_row.hold_id <>
                                operation.hold_id
                    ) target_roots),
                (
                    SELECT
                        soak.candidate_route_manifest_sha256
                    FROM
                        snapshot_generation_quarantine_attestations
                            soak
                    WHERE soak.operation_id =
                            operation.operation_id
                      AND soak.stage = 'soak'
                      AND soak.difference_count = 0
                      AND soak.status_parity
                      AND soak.semantic_json_parity
                    ORDER BY soak.attestation_id DESC
                    LIMIT 1),
                reattach.operation_id IS NOT NULL AS reattached,
                current_namespace.nspname AS current_schema,
                current_relation.relname AS current_relation,
                current_relation.oid::BIGINT AS current_oid,
                current_relation.relfilenode::BIGINT
                    AS current_relfilenode,
                inheritance.inhparent::BIGINT AS current_parent_oid,
                pg_get_expr(
                    current_relation.relpartbound,
                    current_relation.oid,
                    TRUE) AS current_partition_bound,
                EXISTS (
                    SELECT 1
                    FROM pg_constraint constraint_row
                    WHERE constraint_row.conrelid =
                            current_relation.oid
                      AND constraint_row.conname =
                            operation.snapshot_check_constraint
                      AND constraint_row.contype = 'c'
                      AND constraint_row.convalidated)
                    AS exact_check_present,
                EXISTS (
                    SELECT 1
                    FROM pg_trigger trigger_row
                    WHERE trigger_row.tgrelid =
                            current_relation.oid
                      AND trigger_row.tgname =
                            operation.mutation_guard_trigger
                      AND NOT trigger_row.tgisinternal
                      AND trigger_row.tgenabled = 'O')
                    AS mutation_guard_present,
                EXISTS (
                    SELECT 1
                    FROM pg_constraint default_constraint
                    WHERE default_constraint.conrelid =
                            operation.default_partition_oid
                      AND default_constraint.conname =
                            operation.default_exclusion_constraint
                      AND default_constraint.contype = 'c'
                      AND default_constraint.convalidated)
                    AS default_exclusion_present,
                COUNT(*) FILTER (
                    WHERE attestation.stage = 'quarantined'
                      AND attestation.difference_count = 0
                      AND attestation.status_parity
                      AND attestation.semantic_json_parity)::INTEGER
                    AS quarantined_attestations,
                COUNT(*) FILTER (
                    WHERE attestation.stage = 'soak'
                      AND attestation.difference_count = 0
                      AND attestation.status_parity
                      AND attestation.semantic_json_parity)::INTEGER
                    AS soak_attestations,
                COUNT(*) FILTER (
                    WHERE attestation.stage = 'reattached'
                      AND attestation.difference_count = 0
                      AND attestation.status_parity
                      AND attestation.semantic_json_parity)::INTEGER
                    AS reattached_attestations
            FROM snapshot_generation_quarantine_operations operation
            LEFT JOIN
                snapshot_generation_quarantine_reattachments
                    reattach
              ON reattach.operation_id = operation.operation_id
            JOIN scrape_publication_state state
              ON state.id = TRUE
            LEFT JOIN pg_namespace current_namespace
              ON current_namespace.nspname = CASE
                    WHEN reattach.operation_id IS NULL
                        THEN operation.quarantine_schema
                    ELSE operation.child_schema
                END
            LEFT JOIN pg_class current_relation
              ON current_relation.relnamespace =
                    current_namespace.oid
             AND current_relation.relname = CASE
                    WHEN reattach.operation_id IS NULL
                        THEN operation.quarantine_relation
                    ELSE operation.child_relation
                END
            LEFT JOIN pg_inherits inheritance
              ON inheritance.inhrelid =
                    current_relation.oid
            LEFT JOIN
                snapshot_generation_quarantine_attestations
                    attestation
              ON attestation.operation_id =
                    operation.operation_id
            WHERE operation.operation_id = @operationId
            GROUP BY
                operation.operation_id,
                reattach.operation_id,
                state.current_publication_id,
                state.previous_publication_id,
                state.working_publication_id,
                state.published_scrape_id,
                state.public_reads_frozen,
                current_namespace.nspname,
                current_relation.relname,
                current_relation.oid,
                current_relation.relfilenode,
                current_relation.relpartbound,
                inheritance.inhparent
            """;
        command.Parameters.AddWithValue(
            "operationId",
            operationId);
        await using var reader =
            await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
        {
            throw new InvalidDataException(
                $"Quarantine operation was not found: {operationId}");
        }

        return new QuarantineOperationState(
            OperationId: reader.GetString(0),
            PlanDigest: reader.GetString(1),
            TriggerPublicationId: reader.GetInt64(2),
            TriggerScrapeId: reader.GetInt64(3),
            Instrument: reader.GetString(4),
            SnapshotId: reader.GetInt64(5),
            RootSchema: reader.GetString(6),
            RootRelation: reader.GetString(7),
            RootOid: reader.GetInt64(8),
            ChildSchema: reader.GetString(9),
            ChildRelation: reader.GetString(10),
            ChildOid: reader.GetInt64(11),
            ChildRelfilenode: reader.GetInt64(12),
            QuarantineSchema: reader.GetString(13),
            QuarantineRelation: reader.GetString(14),
            SnapshotCheckConstraint:
                reader.GetString(15),
            MutationGuardTrigger: reader.GetString(16),
            DefaultPartitionSchema: reader.GetString(17),
            DefaultPartitionRelation: reader.GetString(18),
            DefaultPartitionOid: reader.GetInt64(19),
            DefaultExclusionConstraint: reader.GetString(20),
            RowCount: reader.GetInt64(21),
            RowFingerprintSha256: reader.GetString(22),
            CurrentPublicationId: reader.GetInt64(23),
            CurrentPublishedScrapeId: reader.GetInt64(24),
            CurrentPublicReadsFrozen: reader.GetBoolean(25),
            RunningScrapeCount: reader.GetInt32(26),
            TargetReferenceCount: reader.GetInt64(27),
            LatestSuccessfulSoakCandidateManifestSha256:
                reader.IsDBNull(28)
                    ? null
                    : reader.GetString(28),
            Reattached: reader.GetBoolean(29),
            CurrentSchema:
                reader.IsDBNull(30)
                    ? null
                    : reader.GetString(30),
            CurrentRelation:
                reader.IsDBNull(31)
                    ? null
                    : reader.GetString(31),
            CurrentOid:
                reader.IsDBNull(32)
                    ? null
                    : reader.GetInt64(32),
            CurrentRelfilenode:
                reader.IsDBNull(33)
                    ? null
                    : reader.GetInt64(33),
            CurrentParentOid:
                reader.IsDBNull(34)
                    ? null
                    : reader.GetInt64(34),
            CurrentPartitionBound:
                reader.IsDBNull(35)
                    ? null
                    : reader.GetString(35),
            ExactCheckPresent: reader.GetBoolean(36),
            MutationGuardPresent: reader.GetBoolean(37),
            DefaultExclusionPresent: reader.GetBoolean(38),
            SuccessfulQuarantinedAttestations:
                reader.GetInt32(39),
            SuccessfulSoakAttestations:
                reader.GetInt32(40),
            SuccessfulReattachedAttestations:
                reader.GetInt32(41));
    }

    private static async Task<SessionAdvisoryLockChainLease>
        AcquireSessionLockChainAsync(
            NpgsqlConnection connection,
            CancellationToken ct)
    {
        var acquired = new List<SessionAdvisoryLock>();
        var deadline = Stopwatch.StartNew();
        try
        {
            foreach (var lockReference in SessionAdvisoryLock.Ordered)
            {
                while (true)
                {
                    ct.ThrowIfCancellationRequested();
                    await using var command =
                        connection.CreateCommand();
                    if (lockReference.TextKey is null)
                    {
                        command.CommandText =
                            "SELECT pg_try_advisory_lock(@lockKey)";
                        command.Parameters.AddWithValue(
                            "lockKey",
                            lockReference.NumericKey!.Value);
                    }
                    else
                    {
                        command.CommandText = """
                            SELECT pg_try_advisory_lock(
                                hashtextextended(@lockKey, 0))
                            """;
                        command.Parameters.AddWithValue(
                            "lockKey",
                            lockReference.TextKey);
                    }
                    if (await command.ExecuteScalarAsync(ct)
                        is true)
                    {
                        acquired.Add(lockReference);
                        break;
                    }
                    if (deadline.Elapsed >=
                        TimeSpan.FromSeconds(5))
                    {
                        throw new TimeoutException(
                            "Snapshot-generation quarantine lock chain remained busy for five seconds.");
                    }
                    await Task.Delay(
                        TimeSpan.FromMilliseconds(50),
                        ct);
                }
            }
            return new SessionAdvisoryLockChainLease(
                connection,
                acquired);
        }
        catch
        {
            await SessionAdvisoryLockChainLease.ReleaseAsync(
                connection,
                acquired);
            throw;
        }
    }

    public async Task<FingerprintEvidence>
        ComputeFingerprintAsync(
            ArchivePackageEvidence archive,
            CancellationToken ct = default)
    {
        await using var connection =
            await _dataSource.OpenConnectionAsync(ct);
        await using var transaction =
            await connection.BeginTransactionAsync(
                IsolationLevel.RepeatableRead,
                ct);
        var evidence = await ComputeFingerprintAsync(
            connection,
            transaction,
            archive.SnapshotId,
            archive.Instrument,
            ct);
        await transaction.CommitAsync(ct);
        return evidence;
    }

    private static async Task<QuarantineDatabaseSnapshot>
        ReadSnapshotAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction? transaction,
            ArchivePackageEvidence archive,
            CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            WITH latest_cycle AS (
                SELECT *
                FROM snapshot_generation_retention_cycles
                ORDER BY created_at DESC, cycle_id DESC
                LIMIT 1
            ),
            recent_cycles AS (
                SELECT *
                FROM snapshot_generation_retention_cycles
                ORDER BY created_at DESC, cycle_id DESC
                LIMIT 5
            ),
            recent_gate AS (
                SELECT
                    COUNT(*) FILTER (
                        WHERE status = 'observed'
                          AND report_only
                          AND oracle_agreement
                          AND blocked_count = 0
                          AND planner_version = 3
                          AND config_version = 1
                          AND global_blockers = '[]'::jsonb
                          AND planner_child_set =
                                oracle_child_set
                          AND planner_live_set =
                                oracle_live_set
                          AND planner_candidate_set =
                                oracle_candidate_set)::INTEGER
                        AS accepted_count,
                    COUNT(DISTINCT trigger_publication_id)
                        FILTER (
                            WHERE status = 'observed'
                              AND report_only
                              AND oracle_agreement
                              AND blocked_count = 0
                              AND planner_version = 3
                              AND config_version = 1
                              AND global_blockers =
                                    '[]'::jsonb
                              AND planner_child_set =
                                    oracle_child_set
                              AND planner_live_set =
                                    oracle_live_set
                              AND planner_candidate_set =
                                    oracle_candidate_set)::INTEGER
                        AS publication_count,
                    COUNT(DISTINCT candidate_identity_hash)
                        FILTER (
                            WHERE status = 'observed'
                              AND report_only
                              AND oracle_agreement
                              AND blocked_count = 0
                              AND planner_version = 3
                              AND config_version = 1
                              AND global_blockers =
                                    '[]'::jsonb
                              AND planner_child_set =
                                    oracle_child_set
                              AND planner_live_set =
                                    oracle_live_set
                              AND planner_candidate_set =
                                    oracle_candidate_set)::INTEGER
                        AS candidate_identity_count
                FROM recent_cycles
            )
            SELECT
                current_database()::TEXT,
                (
                    SELECT oid::BIGINT
                    FROM pg_database
                    WHERE datname = current_database()),
                (
                    SELECT system_identifier::TEXT
                    FROM pg_control_system()),
                current_setting(
                    'server_version_num')::INTEGER,
                current_user::TEXT,
                state.current_publication_id::BIGINT,
                state.published_scrape_id::BIGINT,
                state.public_reads_frozen,
                state.working_publication_id::BIGINT,
                state.publication_commit_intent_started_at
                    IS NOT NULL,
                state.max_score_mutation_gate_token IS NOT NULL,
                (
                    state.improvement_notifications_scrape_id =
                        cycle.trigger_scrape_id
                    AND state.improvement_notifications_status =
                        'completed'
                    AND
                        state.improvement_notifications_completed_at
                            IS NOT NULL
                    AND
                        state.improvement_notifications_projection_ready
                    AND
                        state.improvement_notifications_projection_scrape_id =
                            cycle.trigger_scrape_id),
                (
                    trigger_scrape.status = 'completed'
                    AND trigger_scrape.completed_at IS NOT NULL
                    AND trigger_scrape.failed_at IS NULL),
                (
                    trigger_publication.status = 'current'
                    AND trigger_publication.scrape_id =
                        cycle.trigger_scrape_id),
                cycle.cycle_id,
                cycle.trigger_scrape_id,
                cycle.trigger_publication_id,
                cycle.status,
                cycle.report_only,
                cycle.oracle_agreement,
                cycle.candidate_count,
                cycle.blocked_count,
                jsonb_array_length(cycle.global_blockers),
                (
                    cycle.planner_child_set =
                        cycle.oracle_child_set
                    AND cycle.planner_live_set =
                        cycle.oracle_live_set
                    AND cycle.planner_candidate_set =
                        cycle.oracle_candidate_set),
                cycle.candidate_identity_hash,
                cycle.observation_hash,
                observation.observation_id,
                observation.instrument,
                observation.snapshot_id,
                observation.root_relation,
                observation.root_oid,
                observation.child_relation,
                child.oid::BIGINT,
                child.relfilenode::BIGINT,
                pg_get_expr(
                    child.relpartbound,
                    child.oid,
                    TRUE),
                observation.stable_child_identity_hash,
                observation.stable_config_schema_hash,
                observation.classification,
                observation.planner_live,
                observation.oracle_live,
                cardinality(observation.blocker_codes),
                (
                    SELECT COUNT(*)::BIGINT
                    FROM leaderboard_entries_snapshot row_value
                    WHERE row_value.snapshot_id = @snapshotId
                      AND row_value.instrument = @instrument),
                pg_total_relation_size(child.oid)::BIGINT,
                (
                    SELECT COUNT(*)::INTEGER
                    FROM scrape_log scrape
                    WHERE scrape.status = 'running'),
                (
                    SELECT COUNT(*)::INTEGER
                    FROM snapshot_generation_retention_holds
                        hold_row
                    WHERE hold_row.instrument =
                            observation.instrument
                      AND hold_row.snapshot_id =
                            observation.snapshot_id
                      AND hold_row.released_at IS NULL),
                (
                    SELECT COUNT(*)::INTEGER
                    FROM scrape_writer_failures failure
                    WHERE failure.instrument =
                            observation.instrument
                      AND failure.scrape_id =
                            observation.snapshot_id
                      AND failure.replayed_at IS NULL),
                recent_gate.accepted_count,
                recent_gate.publication_count,
                recent_gate.candidate_identity_count
            FROM latest_cycle cycle
            JOIN snapshot_generation_retention_observations
                observation
              ON observation.cycle_id = cycle.cycle_id
             AND observation.observation_id =
                    @observationId
            JOIN scrape_publication_state state
              ON state.id = TRUE
            JOIN scrape_log trigger_scrape
              ON trigger_scrape.id =
                    cycle.trigger_scrape_id
            JOIN publication_generations trigger_publication
              ON trigger_publication.publication_id =
                    cycle.trigger_publication_id
            JOIN pg_namespace child_namespace
              ON child_namespace.nspname =
                    observation.child_schema
            JOIN pg_class child
              ON child.relnamespace =
                    child_namespace.oid
             AND child.relname =
                    observation.child_relation
            JOIN pg_inherits inheritance
              ON inheritance.inhrelid = child.oid
             AND inheritance.inhparent =
                    observation.root_oid
            CROSS JOIN recent_gate
            WHERE cycle.cycle_id = @cycleId
              AND observation.instrument = @instrument
              AND observation.snapshot_id = @snapshotId
            """;
        command.Parameters.AddWithValue(
            "cycleId",
            archive.CycleId);
        command.Parameters.AddWithValue(
            "observationId",
            archive.ObservationId);
        command.Parameters.AddWithValue(
            "instrument",
            archive.Instrument);
        command.Parameters.AddWithValue(
            "snapshotId",
            archive.SnapshotId);
        await using var reader =
            await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
        {
            throw new InvalidDataException(
                "Latest database planner evidence does not contain the archived candidate.");
        }

        return new QuarantineDatabaseSnapshot(
            CapturedAtUtc: DateTimeOffset.UtcNow,
            DatabaseName: reader.GetString(0),
            DatabaseOid: reader.GetInt64(1),
            SystemIdentifier: reader.GetString(2),
            ServerVersionNum: reader.GetInt32(3),
            CurrentUser: reader.GetString(4),
            CurrentPublicationId: reader.GetInt64(5),
            PublishedScrapeId: reader.GetInt64(6),
            PublicReadsFrozen: reader.GetBoolean(7),
            WorkingPublicationId:
                reader.IsDBNull(8)
                    ? null
                    : reader.GetInt64(8),
            PublicationCommitIntentActive:
                reader.GetBoolean(9),
            MaxScoreMutationGateActive:
                reader.GetBoolean(10),
            NotificationsComplete: reader.GetBoolean(11),
            TriggerScrapeCompleted: reader.GetBoolean(12),
            TriggerPublicationCurrent: reader.GetBoolean(13),
            LatestCycleId: reader.GetInt64(14),
            CycleTriggerScrapeId: reader.GetInt64(15),
            CycleTriggerPublicationId: reader.GetInt64(16),
            CycleStatus: reader.GetString(17),
            ReportOnly: reader.GetBoolean(18),
            OracleAgreement: reader.GetBoolean(19),
            CandidateCount: reader.GetInt32(20),
            BlockedCount: reader.GetInt32(21),
            GlobalBlockerCount: reader.GetInt32(22),
            PlannerOracleSetsEqual: reader.GetBoolean(23),
            CandidateIdentityHash: reader.GetString(24),
            ObservationHash: reader.GetString(25),
            ObservationId: reader.GetInt64(26),
            Instrument: reader.GetString(27),
            SnapshotId: reader.GetInt64(28),
            RootRelation: reader.GetString(29),
            RootOid: reader.GetInt64(30),
            ChildRelation: reader.GetString(31),
            ChildOid: reader.GetInt64(32),
            ChildRelfilenode: reader.GetInt64(33),
            PartitionBound: reader.GetString(34),
            StableChildIdentityHash: reader.GetString(35),
            StableConfigSchemaHash: reader.GetString(36),
            Classification: reader.GetString(37),
            PlannerLive: reader.GetBoolean(38),
            OracleLive: reader.GetBoolean(39),
            BlockerCount: reader.GetInt32(40),
            CurrentRowCount: reader.GetInt64(41),
            CurrentTotalBytes: reader.GetInt64(42),
            RunningScrapeCount: reader.GetInt32(43),
            ActiveHoldCount: reader.GetInt32(44),
            UnreplayedWriterFailureCount:
                reader.GetInt32(45),
            AcceptedRecentCycleCount:
                reader.GetInt32(46),
            AcceptedRecentPublicationCount:
                reader.GetInt32(47),
            AcceptedRecentCandidateIdentityCount:
                reader.GetInt32(48));
    }

    private static async Task LockCandidateAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        ArchivePackageEvidence archive,
        CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT fst_lock_snapshot_generation_for_quarantine(
                @cycleId,
                @observationId,
                @childOid,
                @childRelfilenode)
            """;
        command.Parameters.AddWithValue(
            "cycleId",
            archive.CycleId);
        command.Parameters.AddWithValue(
            "observationId",
            archive.ObservationId);
        command.Parameters.AddWithValue(
            "childOid",
            archive.ChildOid);
        command.Parameters.AddWithValue(
            "childRelfilenode",
            archive.ChildRelfilenode);
        var relation = (string)(
            await command.ExecuteScalarAsync(ct)
            ?? throw new InvalidOperationException(
                "Candidate lock function returned no relation."));
        if (!string.Equals(
                relation,
                archive.ChildRelation,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "Candidate lock function returned another relation.");
        }
    }

    private static async Task<FingerprintEvidence>
        ComputeFingerprintAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            long snapshotId,
            string instrument,
            CancellationToken ct)
    {
        await using (var settings =
                     connection.CreateCommand())
        {
            settings.Transaction = transaction;
            settings.CommandText = """
                SELECT
                    set_config(
                        'fst.quarantine_snapshot_id',
                        @snapshotId::TEXT,
                        TRUE),
                    set_config(
                        'fst.quarantine_instrument',
                        @instrument,
                        TRUE)
                """;
            settings.Parameters.AddWithValue(
                "snapshotId",
                snapshotId);
            settings.Parameters.AddWithValue(
                "instrument",
                instrument);
            await settings.ExecuteNonQueryAsync(ct);
        }

        using var hash = IncrementalHash.CreateHash(
            HashAlgorithmName.SHA256);
        var charBuffer = ArrayPool<char>.Shared.Rent(
            64 * 1024);
        var byteBuffer = ArrayPool<byte>.Shared.Rent(
            Encoding.UTF8.GetMaxByteCount(
                charBuffer.Length));
        long streamBytes = 0;
        try
        {
            using var reader =
                await connection.BeginTextExportAsync(
                    SnapshotFingerprintCopySql,
                    ct);
            var encoder = Encoding.UTF8.GetEncoder();
            while (true)
            {
                var read = await reader.ReadAsync(
                    charBuffer.AsMemory(),
                    ct);
                if (read == 0)
                    break;
                encoder.Convert(
                    charBuffer,
                    0,
                    read,
                    byteBuffer,
                    0,
                    byteBuffer.Length,
                    flush: false,
                    out _,
                    out var bytesUsed,
                    out _);
                hash.AppendData(
                    byteBuffer,
                    0,
                    bytesUsed);
                streamBytes += bytesUsed;
            }
            encoder.Convert(
                Array.Empty<char>(),
                0,
                0,
                byteBuffer,
                0,
                byteBuffer.Length,
                flush: true,
                out _,
                out var finalBytes,
                out _);
            if (finalBytes > 0)
            {
                hash.AppendData(
                    byteBuffer,
                    0,
                    finalBytes);
                streamBytes += finalBytes;
            }
        }
        finally
        {
            ArrayPool<char>.Shared.Return(charBuffer);
            ArrayPool<byte>.Shared.Return(byteBuffer);
        }

        await using var count = connection.CreateCommand();
        count.Transaction = transaction;
        count.CommandText = """
            SELECT COUNT(*)::BIGINT
            FROM leaderboard_entries_snapshot
            WHERE snapshot_id = @snapshotId
              AND instrument = @instrument
            """;
        count.Parameters.AddWithValue(
            "snapshotId",
            snapshotId);
        count.Parameters.AddWithValue(
            "instrument",
            instrument);
        var rowCount = Convert.ToInt64(
            await count.ExecuteScalarAsync(ct));
        return new FingerprintEvidence(
            Algorithm:
                "sha256-copy-to-jsonb-text-ordered-snapshot_id-song_id-instrument-account_id-v1",
            Sha256: Convert.ToHexString(
                    hash.GetHashAndReset())
                .ToLowerInvariant(),
            RowCount: rowCount,
            StreamBytes: streamBytes);
    }

    private static void ValidateFingerprint(
        FingerprintEvidence fingerprint,
        ArchivePackageEvidence archive)
    {
        if (!string.Equals(
                fingerprint.Sha256,
                archive.RowFingerprintSha256,
                StringComparison.Ordinal)
            || fingerprint.RowCount != archive.RowCount)
        {
            throw new InvalidDataException(
                "Current candidate row fingerprint differs from the accepted archive.");
        }
    }

    private static async Task<string>
        ExecuteQuarantineFunctionAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            SnapshotGenerationQuarantinePlan plan,
            string approvedBy,
            string approvalReference,
            JsonElement preflight,
            CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT fst_quarantine_snapshot_generation(
                @operationId,
                @planDigest,
                @archiveManifestSha256,
                @archiveProofManifestSha256,
                @sourceEvidenceManifestSha256,
                @baselineRouteManifestSha256,
                @candidateRouteManifestSha256,
                @cycleId,
                @observationId,
                @childOid,
                @childRelfilenode,
                @rowCount,
                @rowFingerprintSha256,
                @logicalCatalogSha256,
                @approvedBy,
                @approvalReference,
                @preflight)
            """;
        command.Parameters.AddWithValue(
            "operationId",
            plan.OperationId!);
        command.Parameters.AddWithValue(
            "planDigest",
            plan.PlanDigest!);
        command.Parameters.AddWithValue(
            "archiveManifestSha256",
            plan.Archive.PackageManifestSha256);
        command.Parameters.AddWithValue(
            "archiveProofManifestSha256",
            plan.Archive.ProofManifestSha256);
        command.Parameters.AddWithValue(
            "sourceEvidenceManifestSha256",
            plan.SourceScrape.ManifestSha256);
        command.Parameters.AddWithValue(
            "baselineRouteManifestSha256",
            plan.PreQuarantineParity
                .BaselineManifestSha256);
        command.Parameters.AddWithValue(
            "candidateRouteManifestSha256",
            plan.PreQuarantineParity
                .CandidateManifestSha256);
        command.Parameters.AddWithValue(
            "cycleId",
            plan.Archive.CycleId);
        command.Parameters.AddWithValue(
            "observationId",
            plan.Archive.ObservationId);
        command.Parameters.AddWithValue(
            "childOid",
            plan.Archive.ChildOid);
        command.Parameters.AddWithValue(
            "childRelfilenode",
            plan.Archive.ChildRelfilenode);
        command.Parameters.AddWithValue(
            "rowCount",
            plan.Archive.RowCount);
        command.Parameters.AddWithValue(
            "rowFingerprintSha256",
            plan.Archive.RowFingerprintSha256);
        command.Parameters.AddWithValue(
            "logicalCatalogSha256",
            plan.Archive.LogicalCatalogSha256);
        command.Parameters.AddWithValue(
            "approvedBy",
            approvedBy);
        command.Parameters.AddWithValue(
            "approvalReference",
            approvalReference);
        command.Parameters.Add(
                "preflight",
                NpgsqlDbType.Jsonb)
            .Value = preflight.GetRawText();
        return (string)(
            await command.ExecuteScalarAsync(ct)
            ?? throw new InvalidOperationException(
                "Quarantine function returned no operation ID."));
    }

    private static SnapshotGenerationQuarantineExecutionReport
        BuildExecutionReport(
            SnapshotGenerationQuarantinePlan plan,
            QuarantineOperationState state,
            string action,
            string status,
            string actor,
            string reference,
            JsonElement evidence)
    {
        ValidateOperationMatchesPlan(state, plan);
        return new SnapshotGenerationQuarantineExecutionReport(
            SchemaVersion: 1,
            ToolId:
                FSTService.Persistence.Maintenance
                    .SnapshotGenerationQuarantineContract.ToolId,
            Action: action,
            OperationId: state.OperationId,
            PlanDigest: state.PlanDigest,
            Status: status,
            CompletedAtUtc: DateTimeOffset.UtcNow,
            Actor: actor,
            Reference: reference,
            DatabaseName: plan.Database.DatabaseName,
            SystemIdentifier:
                plan.Database.SystemIdentifier,
            PublicationId:
                state.TriggerPublicationId,
            PublishedScrapeId:
                state.TriggerScrapeId,
            Instrument: state.Instrument,
            SnapshotId: state.SnapshotId,
            ChildRelation: state.ChildRelation,
            QuarantineRelation:
                state.Reattached
                    ? null
                    : $"{state.QuarantineSchema}.{state.QuarantineRelation}",
            ChildOid: state.ChildOid,
            ChildRelfilenode:
                state.ChildRelfilenode,
            RowCount: state.RowCount,
            RowFingerprintSha256:
                state.RowFingerprintSha256,
            Evidence: evidence).Seal();
    }

    private static void ValidateOperationMatchesPlan(
        QuarantineOperationState state,
        SnapshotGenerationQuarantinePlan plan)
    {
        if (!string.Equals(
                state.OperationId,
                plan.OperationId,
                StringComparison.Ordinal)
            || !string.Equals(
                state.PlanDigest,
                plan.PlanDigest,
                StringComparison.Ordinal)
            || state.TriggerPublicationId !=
                plan.Archive.TriggerPublicationId
            || state.TriggerScrapeId !=
                plan.Archive.TriggerScrapeId
            || state.Instrument != plan.Archive.Instrument
            || state.SnapshotId != plan.Archive.SnapshotId
            || state.RootOid != plan.Archive.RootOid
            || state.ChildOid != plan.Archive.ChildOid
            || state.ChildRelfilenode !=
                plan.Archive.ChildRelfilenode
            || state.RowCount != plan.Archive.RowCount
            || !string.Equals(
                state.RowFingerprintSha256,
                plan.Archive.RowFingerprintSha256,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "Durable quarantine operation differs from the plan.");
        }
        if (state.CurrentOid != state.ChildOid
            || state.CurrentRelfilenode !=
                state.ChildRelfilenode)
        {
            throw new InvalidDataException(
                "Current quarantine relation identity differs from the plan.");
        }
        if (state.Reattached)
        {
            if (!string.Equals(
                    state.CurrentSchema,
                    state.ChildSchema,
                    StringComparison.Ordinal)
                || !string.Equals(
                    state.CurrentRelation,
                    state.ChildRelation,
                    StringComparison.Ordinal)
                || state.CurrentParentOid != state.RootOid
                || state.ExactCheckPresent
                || state.MutationGuardPresent
                || state.DefaultExclusionPresent)
            {
                throw new InvalidDataException(
                    "Reattached operation state is invalid.");
            }
        }
        else if (!string.Equals(
                     state.CurrentSchema,
                     state.QuarantineSchema,
                     StringComparison.Ordinal)
                 || !string.Equals(
                     state.CurrentRelation,
                     state.QuarantineRelation,
                     StringComparison.Ordinal)
                 || state.CurrentParentOid is not null
                 || !state.ExactCheckPresent
                 || !state.MutationGuardPresent
                 || !state.DefaultExclusionPresent)
        {
            throw new InvalidDataException(
                "Quarantined operation state is invalid.");
        }
    }

    private static void ValidateActor(
        string value,
        string name)
    {
        if (string.IsNullOrWhiteSpace(value)
            || value.Length > 512
            || value.Any(char.IsControl))
        {
            throw new ArgumentException(
                $"{name} is invalid.",
                name);
        }
    }

    private sealed record SessionAdvisoryLock(
        long? NumericKey,
        string? TextKey)
    {
        public static readonly IReadOnlyList<
            SessionAdvisoryLock> Ordered =
        [
            new(
                FSTService.Persistence.Maintenance
                    .SnapshotGenerationQuarantineContract
                    .RegistrationAdvisoryLockKey,
                null),
            new(
                FSTService.Persistence.Maintenance
                    .SnapshotGenerationQuarantineContract
                    .ServiceMaintenanceAdvisoryLockKey,
                null),
            new(
                FSTService.Persistence.Maintenance
                    .SnapshotGenerationQuarantineContract
                    .PublicationAdvisoryLockKey,
                null),
            new(
                FSTService.Persistence.Maintenance
                    .SnapshotGenerationQuarantineContract
                    .PlannerAdvisoryLockKey,
                null),
            new(
                null,
                FSTService.Persistence.Maintenance
                    .SnapshotGenerationQuarantineContract
                    .SnapshotDdlLockName),
            new(
                FSTService.Persistence.Maintenance
                    .SnapshotGenerationQuarantineContract
                    .ExecutorAdvisoryLockKey,
                null),
        ];
    }

    private sealed class SessionAdvisoryLockChainLease
        : IAsyncDisposable
    {
        private readonly NpgsqlConnection _connection;
        private readonly IReadOnlyList<SessionAdvisoryLock>
            _locks;
        private int _released;

        public SessionAdvisoryLockChainLease(
            NpgsqlConnection connection,
            IReadOnlyList<SessionAdvisoryLock> locks)
        {
            _connection = connection;
            _locks = locks.ToArray();
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(
                    ref _released,
                    1) != 0)
            {
                return;
            }
            await ReleaseAsync(_connection, _locks);
        }

        public static async Task ReleaseAsync(
            NpgsqlConnection connection,
            IReadOnlyList<SessionAdvisoryLock> locks)
        {
            if (connection.State != ConnectionState.Open)
                return;
            foreach (var lockReference in locks.Reverse())
            {
                await using var command =
                    connection.CreateCommand();
                if (lockReference.TextKey is null)
                {
                    command.CommandText =
                        "SELECT pg_advisory_unlock(@lockKey)";
                    command.Parameters.AddWithValue(
                        "lockKey",
                        lockReference.NumericKey!.Value);
                }
                else
                {
                    command.CommandText = """
                        SELECT pg_advisory_unlock(
                            hashtextextended(@lockKey, 0))
                        """;
                    command.Parameters.AddWithValue(
                        "lockKey",
                        lockReference.TextKey);
                }
                if (await command.ExecuteScalarAsync(
                        CancellationToken.None)
                    is not true)
                {
                    throw new InvalidOperationException(
                        "Snapshot-generation quarantine session advisory lock was not held during release.");
                }
            }
        }
    }
}
