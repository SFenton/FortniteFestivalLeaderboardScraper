using System.Buffers;
using System.Data;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Runtime.ExceptionServices;
using FstSnapshotGenerationEvidence;
using FstSnapshotGenerationQuarantine;
using Npgsql;
using NpgsqlTypes;

namespace FstSnapshotGenerationDrop;

public sealed class DropDatabase : IAsyncDisposable
{
    public const string ConnectionEnvironment =
        "FST_SNAPSHOT_DROP_CONNECTION_STRING";

    private readonly NpgsqlDataSource _dataSource;

    public static Action<string>? DropTestHook { get; set; }

    private DropDatabase(NpgsqlDataSource dataSource)
    {
        _dataSource = dataSource;
    }

    public static DropDatabase FromEnvironment()
    {
        var value = Environment.GetEnvironmentVariable(
            ConnectionEnvironment);
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException(
                $"{ConnectionEnvironment} is required.");
        }
        return FromConnectionString(value);
    }

    public static DropDatabase FromConnectionString(
        string connectionString)
    {
        var builder = new NpgsqlConnectionStringBuilder(
            connectionString)
        {
            ApplicationName =
                "fst-snapshot-generation-drop",
            Timeout = 15,
            CommandTimeout = 180,
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
                "The drop connection must specify one PostgreSQL host, database, and username.");
        }
        builder.Options =
            "-c statement_timeout=180000 "
            + "-c lock_timeout=5000 "
            + "-c idle_in_transaction_session_timeout=240000 "
            + "-c transaction_timeout=240000";
        return new DropDatabase(
            NpgsqlDataSource.Create(
                builder.ConnectionString));
    }

    public async ValueTask DisposeAsync() =>
        await _dataSource.DisposeAsync();

    public async Task<SnapshotGenerationDropCandidate>
        SelectCanaryAsync(
            CancellationToken ct = default)
    {
        await using var connection =
            await _dataSource.OpenConnectionAsync(ct);
        await using var transaction =
            await connection.BeginTransactionAsync(
                IsolationLevel.RepeatableRead,
                ct);
        await using var setup = connection.CreateCommand();
        setup.Transaction = transaction;
        setup.CommandText = """
            SET TRANSACTION READ ONLY;
            SET LOCAL lock_timeout = '2s';
            SET LOCAL statement_timeout = '30s';
            SET LOCAL idle_in_transaction_session_timeout = '45s';
            """;
        await setup.ExecuteNonQueryAsync(ct);

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            WITH latest AS (
                SELECT *
                FROM snapshot_generation_retention_cycles
                ORDER BY created_at DESC, cycle_id DESC
                LIMIT 1
            )
            SELECT
                cycle.cycle_id,
                observation.observation_id,
                cycle.trigger_scrape_id,
                cycle.trigger_publication_id,
                observation.instrument,
                observation.snapshot_id,
                observation.root_relation,
                observation.root_oid,
                observation.child_relation,
                child.oid::BIGINT,
                child.relfilenode::BIGINT,
                (
                    SELECT COUNT(*)::BIGINT
                    FROM leaderboard_entries_snapshot row_value
                    WHERE row_value.snapshot_id =
                            observation.snapshot_id
                      AND row_value.instrument =
                            observation.instrument),
                pg_total_relation_size(child.oid)::BIGINT,
                observation.stable_child_identity_hash,
                observation.stable_config_schema_hash
            FROM latest cycle
            JOIN snapshot_generation_retention_observations
                observation
              ON observation.cycle_id = cycle.cycle_id
            JOIN pg_namespace namespace
              ON namespace.nspname = observation.child_schema
            JOIN pg_class child
              ON child.relnamespace = namespace.oid
             AND child.relname = observation.child_relation
             AND child.oid::BIGINT = observation.child_oid
             AND child.relfilenode::BIGINT =
                    observation.child_relfilenode
            WHERE cycle.status = 'observed'
              AND cycle.report_only
              AND cycle.oracle_agreement
              AND cycle.blocked_count = 0
              AND cycle.global_blockers = '[]'::jsonb
              AND cycle.planner_child_set =
                    cycle.oracle_child_set
              AND cycle.planner_live_set =
                    cycle.oracle_live_set
              AND cycle.planner_candidate_set =
                    cycle.oracle_candidate_set
              AND observation.classification = 'candidate'
              AND NOT observation.planner_live
              AND NOT observation.oracle_live
              AND cardinality(observation.blocker_codes) = 0
              AND NOT (
                    observation.instrument = 'Solo_Bass'
                    AND observation.snapshot_id = 1308)
              AND EXISTS (
                    SELECT 1
                    FROM leaderboard_entries_snapshot row_value
                    WHERE row_value.snapshot_id =
                            observation.snapshot_id
                      AND row_value.instrument =
                            observation.instrument)
              AND pg_total_relation_size(child.oid) > 0
            ORDER BY
                pg_total_relation_size(child.oid),
                observation.snapshot_id,
                observation.instrument,
                observation.child_oid
            LIMIT 1
            """;
        await using var reader =
            await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
        {
            throw new InvalidDataException(
                "The newest accepted cycle has no nonempty drop canary candidate.");
        }
        var result = new SnapshotGenerationDropCandidate(
            reader.GetInt64(0),
            reader.GetInt64(1),
            reader.GetInt64(2),
            reader.GetInt64(3),
            reader.GetString(4),
            reader.GetInt64(5),
            reader.GetString(6),
            reader.GetInt64(7),
            reader.GetString(8),
            reader.GetInt64(9),
            reader.GetInt64(10),
            reader.GetInt64(11),
            reader.GetInt64(12),
            reader.GetString(13),
            reader.GetString(14));
        await reader.CloseAsync();
        await transaction.CommitAsync(ct);
        return result;
    }

    public async Task ValidateQuarantineChainAsync(
        SnapshotGenerationDropPlan plan,
        CancellationToken ct = default)
    {
        await using var connection =
            await _dataSource.OpenConnectionAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                rehearsal.plan_digest,
                active.plan_digest,
                rehearsal.instrument,
                rehearsal.snapshot_id,
                rehearsal.root_oid,
                rehearsal.child_oid,
                rehearsal.child_relfilenode,
                rehearsal.row_count,
                rehearsal.row_fingerprint_sha256,
                rehearsal.logical_catalog_sha256,
                rehearsal.archive_manifest_sha256,
                rehearsal.archive_proof_manifest_sha256,
                rehearsal.source_evidence_manifest_sha256,
                rehearsal.total_bytes,
                active.instrument,
                active.snapshot_id,
                active.root_oid,
                active.child_oid,
                active.child_relfilenode,
                active.row_count,
                active.row_fingerprint_sha256,
                active.logical_catalog_sha256,
                active.archive_manifest_sha256,
                active.archive_proof_manifest_sha256,
                active.source_evidence_manifest_sha256,
                active.total_bytes,
                rehearsal_reattach.reattached_at,
                active.quarantined_at,
                EXISTS (
                    SELECT 1
                    FROM snapshot_generation_quarantine_reattachments
                        active_reattach
                    WHERE active_reattach.operation_id =
                            active.operation_id),
                q1q.attestation_id,
                q1s.attestation_id,
                q1r.attestation_id,
                q2q.attestation_id,
                q2s.attestation_id,
                q1q.attested_at,
                q1s.attested_at,
                q1r.attested_at,
                q2q.attested_at,
                q2s.attested_at
            FROM snapshot_generation_quarantine_operations
                rehearsal
            JOIN snapshot_generation_quarantine_operations
                active
              ON active.operation_id =
                    @activeOperationId
            JOIN snapshot_generation_quarantine_reattachments
                rehearsal_reattach
              ON rehearsal_reattach.operation_id =
                    rehearsal.operation_id
            JOIN snapshot_generation_quarantine_attestations q1q
              ON q1q.attestation_id =
                    @q1QuarantinedAttestationId
             AND q1q.operation_id = rehearsal.operation_id
             AND q1q.stage = 'quarantined'
            JOIN snapshot_generation_quarantine_attestations q1s
              ON q1s.attestation_id =
                    @q1SoakAttestationId
             AND q1s.operation_id = rehearsal.operation_id
             AND q1s.stage = 'soak'
            JOIN snapshot_generation_quarantine_attestations q1r
              ON q1r.attestation_id =
                    @q1ReattachedAttestationId
             AND q1r.operation_id = rehearsal.operation_id
             AND q1r.stage = 'reattached'
            JOIN snapshot_generation_quarantine_attestations q2q
              ON q2q.attestation_id =
                    @q2QuarantinedAttestationId
             AND q2q.operation_id = active.operation_id
             AND q2q.stage = 'quarantined'
            JOIN snapshot_generation_quarantine_attestations q2s
              ON q2s.attestation_id =
                    @q2SoakAttestationId
             AND q2s.operation_id = active.operation_id
             AND q2s.stage = 'soak'
            WHERE rehearsal.operation_id =
                    @rehearsalOperationId
            """;
        AddChainParameters(command, plan);
        await using var reader =
            await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
        {
            throw new InvalidDataException(
                "Durable Q1/Q2 evidence is incomplete.");
        }
        var rehearsalArchive =
            plan.RehearsalPlan.Archive;
        var activeArchive = plan.ActivePlan.Archive;
        if (reader.GetString(0) !=
                plan.RehearsalPlan.PlanDigest
            || reader.GetString(1) !=
                plan.ActivePlan.PlanDigest
            || reader.GetString(2) !=
                rehearsalArchive.Instrument
            || reader.GetInt64(3) !=
                rehearsalArchive.SnapshotId
            || reader.GetInt64(4) !=
                rehearsalArchive.RootOid
            || reader.GetInt64(5) !=
                rehearsalArchive.ChildOid
            || reader.GetInt64(6) !=
                rehearsalArchive.ChildRelfilenode
            || reader.GetInt64(7) !=
                rehearsalArchive.RowCount
            || reader.GetString(8) !=
                rehearsalArchive.RowFingerprintSha256
            || reader.GetString(9) !=
                rehearsalArchive.LogicalCatalogSha256
            || reader.GetString(10) !=
                rehearsalArchive.PackageManifestSha256
            || reader.GetString(11) !=
                rehearsalArchive.ProofManifestSha256
            || reader.GetString(12) !=
                plan.RehearsalPlan.SourceScrape
                    .ManifestSha256
            || reader.GetInt64(13) !=
                rehearsalArchive.TotalBytes
            || reader.GetString(14) !=
                activeArchive.Instrument
            || reader.GetInt64(15) !=
                activeArchive.SnapshotId
            || reader.GetInt64(16) !=
                activeArchive.RootOid
            || reader.GetInt64(17) !=
                activeArchive.ChildOid
            || reader.GetInt64(18) !=
                activeArchive.ChildRelfilenode
            || reader.GetInt64(19) !=
                activeArchive.RowCount
            || reader.GetString(20) !=
                activeArchive.RowFingerprintSha256
            || reader.GetString(21) !=
                activeArchive.LogicalCatalogSha256
            || reader.GetString(22) !=
                activeArchive.PackageManifestSha256
            || reader.GetString(23) !=
                activeArchive.ProofManifestSha256
            || reader.GetString(24) !=
                plan.ActivePlan.SourceScrape
                    .ManifestSha256
            || reader.GetInt64(25) !=
                activeArchive.TotalBytes
            || reader.GetFieldValue<DateTimeOffset>(26) >=
                reader.GetFieldValue<DateTimeOffset>(27)
            || reader.GetBoolean(28)
            || reader.GetInt64(29) !=
                plan.RehearsalQuarantinedAttestation
                    .AttestationId
            || reader.GetInt64(30) !=
                plan.RehearsalSoakAttestation
                    .AttestationId
            || reader.GetInt64(31) !=
                plan.RehearsalReattachedAttestation
                    .AttestationId
            || reader.GetInt64(32) !=
                plan.ActiveQuarantinedAttestation
                    .AttestationId
            || reader.GetInt64(33) !=
                plan.ActiveSoakAttestation
                    .AttestationId
            || reader.GetFieldValue<DateTimeOffset>(34) >=
                reader.GetFieldValue<DateTimeOffset>(35)
            || reader.GetFieldValue<DateTimeOffset>(35) >=
                reader.GetFieldValue<DateTimeOffset>(26)
            || reader.GetFieldValue<DateTimeOffset>(26) >=
                reader.GetFieldValue<DateTimeOffset>(36)
            || reader.GetFieldValue<DateTimeOffset>(36) >=
                reader.GetFieldValue<DateTimeOffset>(27)
            || reader.GetFieldValue<DateTimeOffset>(27) >
                reader.GetFieldValue<DateTimeOffset>(37)
            || reader.GetFieldValue<DateTimeOffset>(37) >=
                reader.GetFieldValue<DateTimeOffset>(38)
            || reader.GetFieldValue<DateTimeOffset>(38)
                    - reader.GetFieldValue<DateTimeOffset>(27)
                < TimeSpan.FromSeconds(
                    SnapshotGenerationDropToolContract
                        .MinimumSoakSeconds)
            || plan.ProofCompletedAtUtc <
                reader.GetFieldValue<DateTimeOffset>(38))
        {
            throw new InvalidDataException(
                "Durable Q1/Q2 evidence differs from the sealed drop plan.");
        }
        await reader.CloseAsync();
        await ValidateIndexRenameEvidenceAsync(
            connection,
            plan.RehearsalPlan.OperationId!,
            plan.RehearsalSemantic,
            ct);
        await ValidateIndexRenameEvidenceAsync(
            connection,
            plan.ActivePlan.OperationId!,
            plan.ActiveSemantic,
            ct);
        foreach (var attestation in new[]
                 {
                     plan.RehearsalQuarantinedAttestation,
                     plan.RehearsalSoakAttestation,
                     plan.RehearsalReattachedAttestation,
                     plan.ActiveQuarantinedAttestation,
                     plan.ActiveSoakAttestation,
                 })
        {
            await ValidateAttestationAsync(
                connection,
                attestation,
                ct);
        }
    }

    private static async Task
        ValidateIndexRenameEvidenceAsync(
            NpgsqlConnection connection,
            string operationId,
            SnapshotGenerationArchiveSemanticEvidence
                semantic,
            CancellationToken ct)
    {
        await using var command =
            connection.CreateCommand();
        command.CommandText = """
            SELECT
                index_role,
                index_oid,
                index_relfilenode,
                (
                    semantic_before #>>
                        '{expectedParentIndexOid}'
                    )::BIGINT,
                (
                    semantic_before #>>
                        '{expectedTopIndexOid}'
                    )::BIGINT,
                semantic_before #>>
                    '{expectedTopIndexName}',
                semantic_before = semantic_after,
                semantic_before_sha256 =
                    semantic_after_sha256
            FROM snapshot_generation_quarantine_index_renames
            WHERE operation_id = @operationId
            ORDER BY index_role
            """;
        command.Parameters.AddWithValue(
            "operationId",
            operationId);
        await using var reader =
            await command.ExecuteReaderAsync(ct);
        var rows = new List<
            (string Role,
             long Oid,
             long Relfilenode,
             long ParentRootOid,
             long ParentTopOid,
             string ParentTopRole)>();
        while (await reader.ReadAsync(ct))
        {
            if (!reader.GetBoolean(6)
                || !reader.GetBoolean(7))
            {
                throw new InvalidDataException(
                    "Quarantine index rename semantic evidence changed.");
            }
            rows.Add(
                (reader.GetString(0),
                 reader.GetInt64(1),
                 reader.GetInt64(2),
                 reader.GetInt64(3),
                 reader.GetInt64(4),
                 reader.GetString(5) ==
                    "leaderboard_entries_snapshot_pkey"
                    ? "pk"
                    : reader.GetString(5) ==
                        "ix_les_snapshot_song_score"
                        ? "score"
                        : "invalid"));
        }
        var expected = semantic.Indexes
            .OrderBy(index => index.Role,
                StringComparer.Ordinal)
            .Select(index =>
                (index.Role,
                 index.IndexOid,
                index.IndexRelfilenode,
                index.ParentRootIndexOid,
                index.ParentTopIndexOid,
                index.ParentTopRole))
            .ToArray();
        if (!rows.SequenceEqual(expected))
        {
            throw new InvalidDataException(
                "Quarantine index rename physical inventory differs from the authenticated archive.");
        }
    }

    public async Task<SnapshotGenerationDropDatabaseSnapshot>
        ReadSnapshotAsync(
            SnapshotGenerationQuarantinePlan activePlan,
            NpgsqlConnection? existingConnection = null,
            NpgsqlTransaction? transaction = null,
            CancellationToken ct = default)
    {
        if (existingConnection is null)
        {
            await using var standalone =
                await _dataSource.OpenConnectionAsync(ct);
            await using var snapshotTransaction =
                await standalone.BeginTransactionAsync(
                    IsolationLevel.RepeatableRead,
                    ct);
            await using (var setup =
                         standalone.CreateCommand())
            {
                setup.Transaction = snapshotTransaction;
                setup.CommandText = """
                    SET TRANSACTION READ ONLY;
                    SET LOCAL lock_timeout = '2s';
                    SET LOCAL statement_timeout = '180s';
                    SET LOCAL idle_in_transaction_session_timeout =
                        '240s';
                    SET LOCAL transaction_timeout = '240s';
                    """;
                await setup.ExecuteNonQueryAsync(ct);
            }
            var result = await ReadSnapshotAsync(
                activePlan,
                standalone,
                snapshotTransaction,
                ct);
            await snapshotTransaction.CommitAsync(ct);
            return result;
        }
        var connection = existingConnection;
        {
            var operationId = activePlan.OperationId!;
            var operation = await ReadOperationJsonAsync(
                connection,
                transaction,
                operationId,
                ct);
            var archive = activePlan.Archive;
            var privateSchema =
                operation.GetProperty("quarantineSchema")
                    .GetString()!;
            var privateRelation =
                operation.GetProperty("quarantineRelation")
                    .GetString()!;
            var defaultSchema =
                operation.GetProperty("defaultSchema")
                    .GetString()!;
            var defaultRelation =
                operation.GetProperty("defaultRelation")
                    .GetString()!;
            var exactCheck =
                operation.GetProperty("snapshotCheck")
                    .GetString()!;
            var mutationGuard =
                operation.GetProperty("mutationGuard")
                    .GetString()!;
            var defaultConstraint =
                operation.GetProperty("defaultConstraint")
                    .GetString()!;

            await using var command =
                connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                WITH latest_cycle AS (
                    SELECT cycle_id
                    FROM snapshot_generation_retention_cycles
                    ORDER BY created_at DESC, cycle_id DESC
                    LIMIT 1
                ),
                named_scrapes AS (
                    SELECT generation.scrape_id
                    FROM scrape_publication_state state
                    CROSS JOIN LATERAL unnest(ARRAY[
                        state.current_publication_id,
                        state.previous_publication_id,
                        state.working_publication_id
                    ]::BIGINT[]) pointer(publication_id)
                    JOIN publication_generations generation
                      ON generation.publication_id =
                            pointer.publication_id
                    WHERE state.id = TRUE
                      AND pointer.publication_id IS NOT NULL
                      AND generation.scrape_id IS NOT NULL
                ),
                target_refs AS (
                    SELECT 'active_snapshot'::TEXT AS kind
                    FROM leaderboard_snapshot_state snapshot_state
                    WHERE snapshot_state.instrument = @instrument
                      AND snapshot_state.active_snapshot_id =
                            @snapshotId
                    UNION ALL
                    SELECT 'projection'
                    FROM solo_current_projection_scope projection
                    WHERE projection.instrument = @instrument
                      AND projection.source_snapshot_id =
                            @snapshotId
                    UNION ALL
                    SELECT 'publication'
                    FROM leaderboard_published_scope_source source
                    WHERE source.instrument = @instrument
                      AND source.source_snapshot_id =
                            @snapshotId
                      AND source.published_scrape_id IN (
                            SELECT scrape_id FROM named_scrapes)
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
                    pg_backend_pid(),
                    (SELECT cycle_id FROM latest_cycle),
                    state.current_publication_id::BIGINT,
                    state.published_scrape_id::BIGINT,
                    state.public_reads_frozen,
                    state.working_publication_id::BIGINT,
                    state.publication_commit_intent_started_at
                        IS NOT NULL,
                    state.max_score_mutation_gate_token IS NOT NULL,
                    (
                        state.improvement_notifications_scrape_id =
                            state.published_scrape_id
                        AND state.improvement_notifications_status =
                            'completed'
                        AND
                            state.improvement_notifications_completed_at
                                IS NOT NULL
                        AND
                            state.improvement_notifications_projection_ready
                        AND
                            state.improvement_notifications_projection_scrape_id =
                                state.published_scrape_id),
                    (
                        SELECT COUNT(*)::INTEGER
                        FROM scrape_log scrape
                        WHERE scrape.status = 'running'),
                    (SELECT COUNT(*)::INTEGER FROM target_refs),
                    (
                        SELECT COUNT(*)::INTEGER
                        FROM scrape_writer_failures failure
                        WHERE failure.instrument = @instrument
                          AND failure.scrape_id = @snapshotId
                          AND failure.replayed_at IS NULL),
                    (
                        SELECT COUNT(*)::INTEGER
                        FROM snapshot_generation_retention_holds hold_row
                        WHERE hold_row.instrument = @instrument
                          AND hold_row.snapshot_id = @snapshotId
                          AND hold_row.released_at IS NULL
                          AND hold_row.hold_id <> @holdId),
                    EXISTS (
                        SELECT 1
                        FROM snapshot_generation_retention_holds hold_row
                        WHERE hold_row.hold_id = @holdId
                          AND hold_row.instrument = @instrument
                          AND hold_row.snapshot_id = @snapshotId
                          AND hold_row.hold_kind =
                                'retention_in_flight'
                          AND hold_row.released_at IS NULL),
                    private_relation.oid IS NOT NULL,
                    original_relation.oid IS NULL,
                    COALESCE(private_relation.oid, 0)::BIGINT,
                    COALESCE(
                        private_relation.relfilenode,
                        0)::BIGINT,
                    CASE
                        WHEN private_relation.oid IS NULL
                            THEN 0::BIGINT
                        ELSE pg_total_relation_size(
                            private_relation.oid)::BIGINT
                    END,
                    NOT EXISTS (
                        SELECT 1
                        FROM pg_inherits inheritance
                        WHERE inheritance.inhrelid =
                                private_relation.oid),
                    EXISTS (
                        SELECT 1
                        FROM pg_constraint constraint_row
                        WHERE constraint_row.conrelid =
                                private_relation.oid
                          AND constraint_row.conname = @exactCheck
                          AND constraint_row.contype = 'c'
                          AND constraint_row.convalidated),
                    EXISTS (
                        SELECT 1
                        FROM pg_trigger trigger_row
                        WHERE trigger_row.tgrelid =
                                private_relation.oid
                          AND trigger_row.tgname =
                                @mutationGuard
                          AND NOT trigger_row.tgisinternal
                          AND trigger_row.tgenabled = 'O'),
                    (
                        default_child.oid::BIGINT = @defaultOid
                        AND default_inheritance.inhparent::BIGINT =
                            @rootOid
                        AND pg_get_expr(
                            default_child.relpartbound,
                            default_child.oid,
                            TRUE) = 'DEFAULT'),
                    EXISTS (
                        SELECT 1
                        FROM pg_constraint constraint_row
                        WHERE constraint_row.conrelid =
                                default_child.oid
                          AND constraint_row.conname =
                                @defaultConstraint
                          AND constraint_row.contype = 'c'
                          AND constraint_row.convalidated),
                    (
                        SELECT COUNT(*)::INTEGER
                        FROM pg_index index_row
                        WHERE index_row.indrelid =
                                private_relation.oid
                          AND index_row.indisvalid
                          AND index_row.indisready),
                    EXISTS (
                        SELECT 1
                        FROM service_worker_status worker
                        WHERE worker.worker_key = 'scraper'
                          AND worker.status = 'offline'
                          AND worker.current_operation_json IS NULL)
                FROM scrape_publication_state state
                LEFT JOIN pg_namespace private_namespace
                  ON private_namespace.nspname =
                        @privateSchema
                LEFT JOIN pg_class private_relation
                  ON private_relation.relnamespace =
                        private_namespace.oid
                 AND private_relation.relname =
                        @privateRelation
                LEFT JOIN pg_namespace original_namespace
                  ON original_namespace.nspname = 'public'
                LEFT JOIN pg_class original_relation
                  ON original_relation.relnamespace =
                        original_namespace.oid
                 AND original_relation.relname =
                        @originalRelation
                JOIN pg_namespace default_namespace
                  ON default_namespace.nspname =
                        @defaultSchema
                JOIN pg_class default_child
                  ON default_child.relnamespace =
                        default_namespace.oid
                 AND default_child.relname =
                        @defaultRelation
                JOIN pg_inherits default_inheritance
                  ON default_inheritance.inhrelid =
                        default_child.oid
                WHERE state.id = TRUE
                """;
            command.Parameters.AddWithValue(
                "instrument",
                archive.Instrument);
            command.Parameters.AddWithValue(
                "snapshotId",
                archive.SnapshotId);
            command.Parameters.AddWithValue(
                "holdId",
                operation.GetProperty("holdId")
                    .GetInt64());
            command.Parameters.AddWithValue(
                "privateSchema",
                privateSchema);
            command.Parameters.AddWithValue(
                "privateRelation",
                privateRelation);
            command.Parameters.AddWithValue(
                "originalRelation",
                archive.ChildRelation);
            command.Parameters.AddWithValue(
                "exactCheck",
                exactCheck);
            command.Parameters.AddWithValue(
                "mutationGuard",
                mutationGuard);
            command.Parameters.AddWithValue(
                "defaultSchema",
                defaultSchema);
            command.Parameters.AddWithValue(
                "defaultRelation",
                defaultRelation);
            command.Parameters.AddWithValue(
                "defaultOid",
                operation.GetProperty("defaultOid")
                    .GetInt64());
            command.Parameters.AddWithValue(
                "rootOid",
                archive.RootOid);
            command.Parameters.AddWithValue(
                "defaultConstraint",
                defaultConstraint);
            await using var reader =
                await command.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct))
            {
                throw new InvalidDataException(
                    "Snapshot-generation drop database state is incomplete.");
            }
            var baseValues = new
            {
                LatestCycleId = reader.GetInt64(5),
                CurrentPublicationId = reader.GetInt64(6),
                PublishedScrapeId = reader.GetInt64(7),
                PublicReadsFrozen = reader.GetBoolean(8),
                WorkingPublicationId = reader.IsDBNull(9)
                    ? (long?)null
                    : reader.GetInt64(9),
                PublicationCommitIntentActive =
                    reader.GetBoolean(10),
                MaxScoreMutationGateActive =
                    reader.GetBoolean(11),
                NotificationsComplete =
                    reader.GetBoolean(12),
                RunningScrapeCount = reader.GetInt32(13),
                ActiveReferenceCount = reader.GetInt32(14),
                UnreplayedWriterFailureCount =
                    reader.GetInt32(15),
                OtherActiveHoldCount = reader.GetInt32(16),
                ExactHoldActive = reader.GetBoolean(17),
            };
            var databaseName = reader.GetString(0);
            var databaseOid = reader.GetInt64(1);
            var systemIdentifier = reader.GetString(2);
            var serverVersionNum = reader.GetInt32(3);
            var backendPid = reader.GetInt32(4);
            var privateRelationExists = reader.GetBoolean(18);
            var originalRelationAbsent = reader.GetBoolean(19);
            var currentChildOid = reader.GetInt64(20);
            var currentChildRelfilenode =
                reader.GetInt64(21);
            var currentTotalBytes = reader.GetInt64(22);
            var detached = reader.GetBoolean(23);
            var exactCheckPresent = reader.GetBoolean(24);
            var mutationGuardPresent = reader.GetBoolean(25);
            var defaultIdentityValid = reader.GetBoolean(26);
            var defaultExclusionPresent =
                reader.GetBoolean(27);
            var childIndexCount = reader.GetInt32(28);
            var workerOffline = reader.GetBoolean(29);
            var currentRowCount = 0L;
            if (privateRelationExists)
            {
                await reader.CloseAsync();
                await using var count =
                    connection.CreateCommand();
                count.Transaction = transaction;
                count.CommandText =
                    $"SELECT COUNT(*)::BIGINT FROM ONLY "
                    + $"{Quote(privateSchema)}."
                    + $"{Quote(privateRelation)}";
                currentRowCount = Convert.ToInt64(
                    await count.ExecuteScalarAsync(ct));
            }
            else
            {
                await reader.CloseAsync();
            }
            await using var defaultCount =
                connection.CreateCommand();
            defaultCount.Transaction = transaction;
            defaultCount.CommandText =
                $"SELECT COUNT(*)::BIGINT FROM ONLY "
                + $"{Quote(defaultSchema)}."
                + $"{Quote(defaultRelation)}";
            var defaultRowCount = Convert.ToInt64(
                await defaultCount.ExecuteScalarAsync(ct));

            var dependency = await QueryJsonAsync(
                connection,
                transaction,
                """
                SELECT COALESCE(
                    jsonb_agg(
                        jsonb_build_object(
                            'classId', dependency.classid::BIGINT,
                            'objectId', dependency.objid::BIGINT,
                            'objectSubId', dependency.objsubid,
                            'referencedClassId',
                                dependency.refclassid::BIGINT,
                            'referencedObjectId',
                                dependency.refobjid::BIGINT,
                            'referencedObjectSubId',
                                dependency.refobjsubid,
                            'dependencyType',
                                dependency.deptype::TEXT,
                            'object',
                                pg_describe_object(
                                    dependency.classid,
                                    dependency.objid,
                                    dependency.objsubid))
                        ORDER BY
                            dependency.classid,
                            dependency.objid,
                            dependency.objsubid,
                            dependency.refclassid,
                            dependency.refobjid,
                            dependency.refobjsubid,
                            dependency.deptype),
                    '[]'::jsonb)
                FROM pg_depend dependency
                WHERE dependency.refclassid =
                        'pg_class'::regclass
                  AND dependency.refobjid = @childOid
                """,
                ("childOid", archive.ChildOid),
                ct);
            var topology = await QueryJsonAsync(
                connection,
                transaction,
                """
                SELECT jsonb_build_object(
                    'rootOid', root.oid::BIGINT,
                    'rootKind', root.relkind::TEXT,
                    'rootPartitionKey',
                        pg_get_partkeydef(root.oid),
                    'defaultOid', default_child.oid::BIGINT,
                    'defaultBound',
                        pg_get_expr(
                            default_child.relpartbound,
                            default_child.oid,
                            TRUE),
                    'privateOid',
                        private_child.oid::BIGINT,
                    'privateRelfilenode',
                        private_child.relfilenode::BIGINT,
                    'privateIndexCount', (
                        SELECT COUNT(*)::INTEGER
                        FROM pg_index index_row
                        WHERE index_row.indrelid =
                                private_child.oid
                          AND index_row.indisvalid
                          AND index_row.indisready),
                    'privateColumns', (
                        SELECT COALESCE(
                            jsonb_agg(
                                jsonb_build_object(
                                    'ordinal', attribute.attnum,
                                    'name', attribute.attname,
                                    'type',
                                        format_type(
                                            attribute.atttypid,
                                            attribute.atttypmod),
                                    'notNull',
                                        attribute.attnotnull,
                                    'defaultExpression',
                                        pg_get_expr(
                                            default_value.adbin,
                                            default_value.adrelid))
                                ORDER BY attribute.attnum),
                            '[]'::jsonb)
                        FROM pg_attribute attribute
                        LEFT JOIN pg_attrdef default_value
                          ON default_value.adrelid =
                                attribute.attrelid
                         AND default_value.adnum =
                                attribute.attnum
                        WHERE attribute.attrelid =
                                private_child.oid
                          AND attribute.attnum > 0
                          AND NOT attribute.attisdropped),
                    'privateConstraints', (
                        SELECT COALESCE(
                            jsonb_agg(
                                jsonb_build_object(
                                    'name',
                                        constraint_row.conname,
                                    'type',
                                        constraint_row.contype::TEXT,
                                    'definition',
                                        pg_get_constraintdef(
                                            constraint_row.oid,
                                            TRUE),
                                    'validated',
                                        constraint_row.convalidated)
                                ORDER BY constraint_row.conname),
                            '[]'::jsonb)
                        FROM pg_constraint constraint_row
                        WHERE constraint_row.conrelid =
                                private_child.oid),
                    'privateIndexes', (
                        SELECT COALESCE(
                            jsonb_agg(
                                jsonb_build_object(
                                    'oid',
                                        index_relation.oid::BIGINT,
                                    'relfilenode',
                                        index_relation.relfilenode::BIGINT,
                                    'name',
                                        index_relation.relname,
                                    'definition',
                                        pg_get_indexdef(
                                            index_relation.oid),
                                    'valid',
                                        index_row.indisvalid,
                                    'ready',
                                        index_row.indisready,
                                    'primary',
                                        index_row.indisprimary,
                                    'unique',
                                        index_row.indisunique,
                                    'parentOid',
                                        index_parent.inhparent::BIGINT)
                                ORDER BY index_relation.relname),
                            '[]'::jsonb)
                        FROM pg_index index_row
                        JOIN pg_class index_relation
                          ON index_relation.oid =
                                index_row.indexrelid
                        LEFT JOIN pg_inherits index_parent
                          ON index_parent.inhrelid =
                                index_relation.oid
                        WHERE index_row.indrelid =
                                private_child.oid),
                    'privateTriggers', (
                        SELECT COALESCE(
                            jsonb_agg(
                                jsonb_build_object(
                                    'name', trigger_row.tgname,
                                    'enabled',
                                        trigger_row.tgenabled::TEXT,
                                    'functionOid',
                                        trigger_row.tgfoid::BIGINT)
                                ORDER BY trigger_row.tgname),
                            '[]'::jsonb)
                        FROM pg_trigger trigger_row
                        WHERE trigger_row.tgrelid =
                                private_child.oid
                          AND NOT trigger_row.tgisinternal),
                    'inheritsChildren', (
                        SELECT COALESCE(
                            jsonb_agg(
                                jsonb_build_object(
                                    'oid', child.oid::BIGINT,
                                    'name', child.relname,
                                    'relfilenode',
                                        child.relfilenode::BIGINT,
                                    'bound',
                                        pg_get_expr(
                                            child.relpartbound,
                                            child.oid,
                                            TRUE))
                                ORDER BY child.relname),
                            '[]'::jsonb)
                        FROM pg_inherits child_link
                        JOIN pg_class child
                          ON child.oid =
                                child_link.inhrelid
                        WHERE child_link.inhparent =
                                root.oid),
                    'partitionTreeChildren', (
                        SELECT COALESCE(
                            jsonb_agg(
                                jsonb_build_object(
                                    'oid',
                                        tree.relid::BIGINT,
                                    'name',
                                        tree.relid::regclass::TEXT,
                                    'parentOid',
                                        tree.parentrelid::BIGINT)
                                ORDER BY tree.relid),
                            '[]'::jsonb)
                        FROM pg_partition_tree(root.oid)
                            tree
                        WHERE tree.level = 1),
                    'rootIndexes', (
                        SELECT COALESCE(
                            jsonb_agg(
                                jsonb_build_object(
                                    'oid',
                                        index_relation.oid::BIGINT,
                                    'name',
                                        index_relation.relname,
                                    'valid',
                                        index_row.indisvalid,
                                    'ready',
                                        index_row.indisready)
                                ORDER BY index_relation.relname),
                            '[]'::jsonb)
                        FROM pg_index index_row
                        JOIN pg_class index_relation
                          ON index_relation.oid =
                                index_row.indexrelid
                        WHERE index_row.indrelid = root.oid),
                    'defaultIndexes', (
                        SELECT COALESCE(
                            jsonb_agg(
                                jsonb_build_object(
                                    'oid',
                                        index_relation.oid::BIGINT,
                                    'name',
                                        index_relation.relname,
                                    'valid',
                                        index_row.indisvalid,
                                    'ready',
                                        index_row.indisready)
                                ORDER BY index_relation.relname),
                            '[]'::jsonb)
                        FROM pg_index index_row
                        JOIN pg_class index_relation
                          ON index_relation.oid =
                                index_row.indexrelid
                        WHERE index_row.indrelid =
                                default_child.oid))
                FROM pg_namespace root_namespace
                JOIN pg_class root
                  ON root.relnamespace =
                        root_namespace.oid
                 AND root.relname = @rootRelation
                JOIN pg_namespace default_namespace
                  ON default_namespace.nspname =
                        @defaultSchema
                JOIN pg_class default_child
                  ON default_child.relnamespace =
                        default_namespace.oid
                 AND default_child.relname =
                        @defaultRelation
                JOIN pg_namespace private_namespace
                  ON private_namespace.nspname =
                        @privateSchema
                JOIN pg_class private_child
                  ON private_child.relnamespace =
                        private_namespace.oid
                 AND private_child.relname =
                        @privateRelation
                WHERE root_namespace.nspname = 'public'
                """,
                [
                    ("rootRelation", archive.RootRelation),
                    ("defaultSchema", defaultSchema),
                    ("defaultRelation", defaultRelation),
                    ("privateSchema", privateSchema),
                    ("privateRelation", privateRelation),
                ],
                ct);
            var liveness = await QueryJsonAsync(
                connection,
                transaction,
                """
                SELECT jsonb_build_object(
                    'activeSnapshots', (
                        SELECT COALESCE(
                            jsonb_agg(
                                jsonb_build_object(
                                    'songId', song_id,
                                    'snapshotId',
                                        active_snapshot_id)
                                ORDER BY song_id),
                            '[]'::jsonb)
                        FROM leaderboard_snapshot_state
                        WHERE instrument = @instrument
                          AND active_snapshot_id =
                                @snapshotId),
                    'projectionSources', (
                        SELECT COALESCE(
                            jsonb_agg(
                                jsonb_build_object(
                                    'songId', song_id,
                                    'snapshotId',
                                        source_snapshot_id,
                                    'status', status)
                                ORDER BY song_id),
                            '[]'::jsonb)
                        FROM solo_current_projection_scope
                        WHERE instrument = @instrument
                          AND source_snapshot_id =
                                @snapshotId),
                    'publicationSources', (
                        SELECT COALESCE(
                            jsonb_agg(
                                jsonb_build_object(
                                    'publishedScrapeId',
                                        source.published_scrape_id,
                                    'songId', source.song_id,
                                    'sourceSnapshotId',
                                        source.source_snapshot_id)
                                ORDER BY
                                    source.published_scrape_id,
                                    source.song_id),
                            '[]'::jsonb)
                        FROM leaderboard_published_scope_source
                            source
                        WHERE source.instrument = @instrument
                          AND source.source_snapshot_id =
                                @snapshotId
                          AND source.published_scrape_id IN (
                                SELECT generation.scrape_id
                                FROM scrape_publication_state state
                                CROSS JOIN LATERAL unnest(ARRAY[
                                    state.current_publication_id,
                                    state.previous_publication_id,
                                    state.working_publication_id
                                ]::BIGINT[]) pointer(publication_id)
                                JOIN publication_generations
                                    generation
                                  ON generation.publication_id =
                                        pointer.publication_id
                                WHERE state.id = TRUE
                                  AND pointer.publication_id
                                        IS NOT NULL)),
                    'writerFailures', (
                        SELECT COALESCE(
                            jsonb_agg(
                                jsonb_build_object(
                                    'writerKind', writer_kind,
                                    'songId', song_id)
                                ORDER BY writer_kind, song_id),
                            '[]'::jsonb)
                        FROM scrape_writer_failures
                        WHERE instrument = @instrument
                          AND scrape_id = @snapshotId
                          AND replayed_at IS NULL),
                    'holds', (
                        SELECT COALESCE(
                            jsonb_agg(
                                jsonb_build_object(
                                    'holdId', hold_id,
                                    'holdKind', hold_kind,
                                    'reason', reason)
                                ORDER BY hold_id),
                            '[]'::jsonb)
                        FROM snapshot_generation_retention_holds
                        WHERE instrument = @instrument
                          AND snapshot_id = @snapshotId
                          AND released_at IS NULL),
                    'publicationId', (
                        SELECT current_publication_id
                        FROM scrape_publication_state
                        WHERE id = TRUE),
                    'publishedScrapeId', (
                        SELECT published_scrape_id
                        FROM scrape_publication_state
                        WHERE id = TRUE))
                """,
                [
                    ("instrument", archive.Instrument),
                    ("snapshotId", archive.SnapshotId),
                ],
                ct);
            return new SnapshotGenerationDropDatabaseSnapshot(
                DateTimeOffset.UtcNow,
                databaseName,
                databaseOid,
                systemIdentifier,
                serverVersionNum,
                backendPid,
                baseValues.LatestCycleId,
                baseValues.CurrentPublicationId,
                baseValues.PublishedScrapeId,
                baseValues.PublicReadsFrozen,
                baseValues.WorkingPublicationId,
                baseValues.PublicationCommitIntentActive,
                baseValues.MaxScoreMutationGateActive,
                baseValues.NotificationsComplete,
                baseValues.RunningScrapeCount,
                baseValues.ActiveReferenceCount,
                baseValues.UnreplayedWriterFailureCount,
                baseValues.OtherActiveHoldCount,
                baseValues.ExactHoldActive,
                privateRelationExists,
                originalRelationAbsent,
                currentChildOid,
                currentChildRelfilenode,
                currentRowCount,
                currentTotalBytes,
                detached,
                exactCheckPresent,
                mutationGuardPresent,
                defaultIdentityValid,
                defaultExclusionPresent,
                defaultRowCount,
                childIndexCount,
                workerOffline,
                dependency,
                DropJson.Sha256(dependency),
                topology,
                DropJson.Sha256(topology),
                liveness,
                DropJson.Sha256(liveness));
        }
    }

    public async Task<FingerprintEvidence>
        ComputePrivateFingerprintAsync(
            SnapshotGenerationQuarantinePlan activePlan,
            NpgsqlConnection? existingConnection = null,
            NpgsqlTransaction? transaction = null,
            CancellationToken ct = default)
    {
        if (existingConnection is null)
        {
            await using var standalone =
                await _dataSource.OpenConnectionAsync(ct);
            await using var snapshotTransaction =
                await standalone.BeginTransactionAsync(
                    IsolationLevel.RepeatableRead,
                    ct);
            await using (var setup =
                         standalone.CreateCommand())
            {
                setup.Transaction = snapshotTransaction;
                setup.CommandText = """
                    SET TRANSACTION READ ONLY;
                    SET LOCAL lock_timeout = '2s';
                    SET LOCAL statement_timeout = '180s';
                    SET LOCAL idle_in_transaction_session_timeout =
                        '240s';
                    SET LOCAL transaction_timeout = '240s';
                    """;
                await setup.ExecuteNonQueryAsync(ct);
            }
            var result =
                await ComputePrivateFingerprintAsync(
                    activePlan,
                    standalone,
                    snapshotTransaction,
                    ct);
            await snapshotTransaction.CommitAsync(ct);
            return result;
        }
        var state = await ReadOperationJsonAsync(
                existingConnection,
                transaction,
                activePlan.OperationId!,
                ct);
        return await ComputePrivateFingerprintAsync(
            existingConnection,
            transaction,
            state.GetProperty("quarantineSchema")
                .GetString()!,
            state.GetProperty("quarantineRelation")
                .GetString()!,
            activePlan.Archive.SnapshotId,
            activePlan.Archive.Instrument,
            ct);
    }

    public async Task<SnapshotGenerationDropExecutionReport>
        DropAsync(
            SnapshotGenerationDropPlan plan,
            string approvedBy,
            string approvalReference,
            CancellationToken ct = default)
    {
        plan.Validate();
        ValidateActor(approvedBy, nameof(approvedBy));
        ValidateActor(
            approvalReference,
            nameof(approvalReference));
        if ((approvedBy ==
                plan.RehearsalQuarantineReport.Actor
             || approvedBy ==
                plan.ActiveQuarantineReport.Actor)
            || approvalReference ==
                plan.RehearsalQuarantineReport.Reference
            || approvalReference ==
                plan.RehearsalReattachReport.Reference
            || approvalReference ==
                plan.ActiveQuarantineReport.Reference)
        {
            throw new InvalidDataException(
                "Drop approval must be distinct from Q1 and Q2 approvals.");
        }

        var existing = await ReadDropStateAsync(
            plan,
            ct);
        if (existing.OperationExists)
            return BuildReport(
                plan,
                existing,
                approvedBy,
                approvalReference,
                "already-committed");
        ValidatePendingState(existing, plan);

        await using var connection =
            await _dataSource.OpenConnectionAsync(ct);
        await using var locks =
            await AcquireSessionLockChainAsync(
                connection,
                ct);
        await using var transaction =
            await connection.BeginTransactionAsync(
                IsolationLevel.Serializable,
                ct);
        var dropInvoked = false;
        try
        {
            await LockTargetAsync(
                connection,
                transaction,
                plan,
                ct);
            var snapshot = await ReadSnapshotAsync(
                plan.ActivePlan,
                connection,
                transaction,
                ct);
            ValidateBoundarySnapshot(plan, snapshot);
            var fingerprint =
                await ComputePrivateFingerprintAsync(
                    plan.ActivePlan,
                    connection,
                    transaction,
                    ct);
            if (fingerprint.RowCount !=
                    plan.ActivePlan.Archive.RowCount
                || fingerprint.Sha256 !=
                    plan.ActivePlan.Archive
                        .RowFingerprintSha256)
            {
                throw new InvalidDataException(
                    "Locked private row fingerprint differs from the archive.");
            }

            dropInvoked = true;
            await ExecuteDropFunctionAsync(
                connection,
                transaction,
                plan,
                snapshot,
                approvedBy,
                approvalReference,
                fingerprint,
                CancellationToken.None);
            DropTestHook?.Invoke(
                "after-drop-before-commit");
            await transaction.CommitAsync(
                CancellationToken.None);
            DropTestHook?.Invoke("after-commit");
        }
        catch when (!dropInvoked)
        {
            await transaction.RollbackAsync(
                CancellationToken.None);
            throw;
        }
        catch (Exception exception) when (
            exception is NpgsqlException
                or IOException
                or OperationCanceledException
                or InvalidDataException)
        {
            try
            {
                await transaction.DisposeAsync();
                var reconciled =
                    await ReadDropStateAsync(
                        plan,
                        CancellationToken.None);
                if (reconciled.OperationExists
                    && !reconciled.QuarantineRelationExists
                    && !reconciled.OriginalRelationExists
                    && !reconciled.OriginalOidExists)
                {
                    return BuildReport(
                        plan,
                        reconciled,
                        approvedBy,
                        approvalReference,
                        "reconciled-committed");
                }
                if (!reconciled.OperationExists
                    && reconciled.QuarantineRelationExists
                    && reconciled.ChildOid ==
                        plan.ActivePlan.Archive.ChildOid)
                {
                    ExceptionDispatchInfo
                        .Capture(exception)
                        .Throw();
                }
                throw new InvalidDataException(
                    "Drop commit outcome is inconsistent; no retry is permitted.");
            }
            catch (InvalidDataException)
            {
                throw;
            }
        }

        var state = await ReadDropStateAsync(
            plan,
            CancellationToken.None);
        ValidateCommittedState(state, plan);
        return BuildReport(
            plan,
            state,
            approvedBy,
            approvalReference,
            "committed");
    }

    public async Task<SnapshotGenerationDropState>
        ReadDropStateAsync(
            SnapshotGenerationDropPlan plan,
            CancellationToken ct = default)
    {
        await using var connection =
            await _dataSource.OpenConnectionAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                drop_row.drop_operation_id,
                drop_row.plan_digest,
                drop_row.instrument,
                drop_row.snapshot_id,
                drop_row.child_schema,
                drop_row.child_relation,
                drop_row.child_oid,
                drop_row.child_relfilenode,
                drop_row.quarantine_schema,
                drop_row.quarantine_relation,
                to_regclass(
                    format(
                        '%I.%I',
                        drop_row.child_schema,
                        drop_row.child_relation)) IS NOT NULL,
                to_regclass(
                    format(
                        '%I.%I',
                        drop_row.quarantine_schema,
                        drop_row.quarantine_relation)) IS NOT NULL,
                EXISTS (
                    SELECT 1
                    FROM pg_class relation
                    WHERE relation.oid =
                            drop_row.child_oid),
                EXISTS (
                    SELECT 1
                    FROM pg_constraint constraint_row
                    WHERE constraint_row.conrelid =
                            drop_row.default_partition_oid
                      AND constraint_row.conname =
                            drop_row.durable_default_exclusion_constraint
                      AND constraint_row.contype = 'c'
                      AND constraint_row.convalidated),
                EXISTS (
                    SELECT 1
                    FROM snapshot_generation_retention_holds hold_row
                    WHERE hold_row.hold_id =
                            drop_row.hold_id
                      AND hold_row.released_at IS NULL),
                restore_row.restore_operation_id IS NOT NULL,
                drop_row.approved_by,
                drop_row.approval_reference
            FROM snapshot_generation_drop_operations drop_row
            LEFT JOIN snapshot_generation_restore_operations
                restore_row
              ON restore_row.drop_operation_id =
                    drop_row.drop_operation_id
            WHERE drop_row.drop_operation_id =
                    @dropOperationId
              AND drop_row.plan_digest = @planDigest
            """;
        command.Parameters.AddWithValue(
            "dropOperationId",
            plan.DropOperationId!);
        command.Parameters.AddWithValue(
            "planDigest",
            plan.PlanDigest!);
        await using var reader =
            await command.ExecuteReaderAsync(ct);
        if (await reader.ReadAsync(ct))
        {
            return new SnapshotGenerationDropState(
                true,
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetInt64(3),
                reader.GetString(4),
                reader.GetString(5),
                reader.GetInt64(6),
                reader.GetInt64(7),
                reader.GetString(8),
                reader.GetString(9),
                reader.GetBoolean(10),
                reader.GetBoolean(11),
                reader.GetBoolean(12),
                reader.GetBoolean(13),
                reader.GetBoolean(14),
                reader.GetBoolean(15),
                reader.GetString(16),
                reader.GetString(17));
        }
        await reader.CloseAsync();

        var q = await ReadOperationJsonAsync(
            connection,
            null,
            plan.ActivePlan.OperationId!,
            ct);
        var childSchema =
            q.GetProperty("childSchema").GetString()!;
        var childRelation =
            q.GetProperty("childRelation").GetString()!;
        var quarantineSchema =
            q.GetProperty("quarantineSchema").GetString()!;
        var quarantineRelation =
            q.GetProperty("quarantineRelation").GetString()!;
        await using var pending = connection.CreateCommand();
        pending.CommandText = """
            SELECT
                to_regclass(
                    format('%I.%I', @childSchema, @childRelation))
                    IS NOT NULL,
                to_regclass(
                    format(
                        '%I.%I',
                        @quarantineSchema,
                        @quarantineRelation)) IS NOT NULL,
                EXISTS (
                    SELECT 1
                    FROM pg_class relation
                    WHERE relation.oid = @childOid)
            """;
        pending.Parameters.AddWithValue(
            "childSchema",
            childSchema);
        pending.Parameters.AddWithValue(
            "childRelation",
            childRelation);
        pending.Parameters.AddWithValue(
            "quarantineSchema",
            quarantineSchema);
        pending.Parameters.AddWithValue(
            "quarantineRelation",
            quarantineRelation);
        pending.Parameters.AddWithValue(
            "childOid",
            plan.ActivePlan.Archive.ChildOid);
        await using var pendingReader =
            await pending.ExecuteReaderAsync(ct);
        await pendingReader.ReadAsync(ct);
        return new SnapshotGenerationDropState(
            false,
            null,
            null,
            plan.ActivePlan.Archive.Instrument,
            plan.ActivePlan.Archive.SnapshotId,
            childSchema,
            childRelation,
            plan.ActivePlan.Archive.ChildOid,
            plan.ActivePlan.Archive.ChildRelfilenode,
            quarantineSchema,
            quarantineRelation,
            pendingReader.GetBoolean(0),
            pendingReader.GetBoolean(1),
            pendingReader.GetBoolean(2),
            false,
            true,
            false,
            null,
            null);
    }

    public async Task<SnapshotGenerationDropAttestationReport>
        RecordAttestationAsync(
            SnapshotGenerationDropPlan plan,
            string stage,
            string actor,
            RouteParityEvidence parity,
            CancellationToken ct = default)
    {
        if (stage is not (
                "pre_drop"
                or "dropped"
                or "post_publication"))
        {
            throw new ArgumentException(
                "Drop attestation stage is invalid.",
                nameof(stage));
        }
        ValidateActor(actor, nameof(actor));
        if (parity.RouteCount != 55
            || parity.DifferenceCount != 0
            || !parity.StatusParity
            || !parity.SemanticJsonParity)
        {
            throw new InvalidDataException(
                "Drop attestation route parity is not exact.");
        }
        var state = await ReadDropStateAsync(plan, ct);
        ValidateCommittedState(state, plan);
        var databaseEvidence =
            JsonSerializer.SerializeToElement(
                state,
                DropJson.Strict);
        var evidenceHash = DropJson.Sha256(
            new
            {
                Stage = stage,
                Parity = parity,
                Database = state,
            });
        await using var connection =
            await _dataSource.OpenConnectionAsync(ct);
        await using var locks =
            await AcquireSessionLockChainAsync(
                connection,
                ct);
        await using var transaction =
            await connection.BeginTransactionAsync(
                IsolationLevel.Serializable,
                ct);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT
                fst_record_snapshot_generation_drop_attestation(
                    @dropOperationId,
                    @stage,
                    @publicationId,
                    @publishedScrapeId,
                    @routeCount,
                    @baselineSha256,
                    @candidateSha256,
                    @databaseEvidence,
                    @evidenceSha256,
                    @actor)
            """;
        command.Parameters.AddWithValue(
            "dropOperationId",
            plan.DropOperationId!);
        command.Parameters.AddWithValue("stage", stage);
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
        command.Parameters.AddWithValue("actor", actor);
        var id = Convert.ToInt64(
            await command.ExecuteScalarAsync(ct));
        await transaction.CommitAsync(
            CancellationToken.None);
        return new SnapshotGenerationDropAttestationReport(
            1,
            SnapshotGenerationDropToolContract.ToolId,
            plan.DropOperationId!,
            stage,
            id,
            DateTimeOffset.UtcNow,
            actor,
            parity,
            databaseEvidence,
            evidenceHash).Seal();
    }

    private static async Task LockTargetAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        SnapshotGenerationDropPlan plan,
        CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT fst_lock_snapshot_generation_for_drop(
                @operationId,
                @childOid,
                @childRelfilenode)
            """;
        command.Parameters.AddWithValue(
            "operationId",
            plan.ActivePlan.OperationId!);
        command.Parameters.AddWithValue(
            "childOid",
            plan.ActivePlan.Archive.ChildOid);
        command.Parameters.AddWithValue(
            "childRelfilenode",
            plan.ActivePlan.Archive.ChildRelfilenode);
        var relation = (string)(
            await command.ExecuteScalarAsync(ct)
            ?? throw new InvalidOperationException(
                "Drop lock function returned no relation."));
        if (!string.Equals(
                relation,
                plan.ActiveQuarantineReport
                    .QuarantineRelation?
                    .Split('.', 2)
                    .Last(),
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "Drop lock function returned another relation.");
        }
    }

    private static async Task ExecuteDropFunctionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        SnapshotGenerationDropPlan plan,
        SnapshotGenerationDropDatabaseSnapshot snapshot,
        string approvedBy,
        string approvalReference,
        FingerprintEvidence fingerprint,
        CancellationToken ct)
    {
        var preflight =
            JsonSerializer.SerializeToElement(
                new
                {
                    Snapshot = snapshot,
                    Fingerprint = fingerprint,
                    plan.RecoveryBundlePath,
                    plan.RequiredCapacityBytes,
                    plan.CapacityReserveBytes,
                },
                DropJson.Strict);
        var dropEvidence =
            JsonSerializer.SerializeToElement(
                new
                {
                    Statement =
                        "DROP TABLE <derived-private-child> RESTRICT",
                    ExpectedRelationAbsent = true,
                    RetainDefaultExclusion = true,
                    RetainHold = true,
                },
                DropJson.Strict);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT fst_drop_quarantined_snapshot_generation(
                @dropOperationId,
                @planDigest,
                @rehearsalOperationId,
                @activeOperationId,
                @q1QuarantinedAttestationId,
                @q1SoakAttestationId,
                @q1ReattachedAttestationId,
                @q2QuarantinedAttestationId,
                @q2SoakAttestationId,
                @archiveSha256,
                @freshArchiveProofManifestSha256,
                @recoveryBundleManifestSha256,
                @semanticProjectionVersion,
                @rehearsalCatalogSha256,
                @catalogSha256,
                @rehearsalSemanticCatalogSha256,
                @semanticCatalogSha256,
                @rehearsalLogicalIndexShapeSha256,
                @logicalIndexShapeSha256,
                @rehearsalPhysicalIndexInventorySha256,
                @physicalIndexInventorySha256,
                @preDropBaselineRouteManifestSha256,
                @preDropCandidateRouteManifestSha256,
                @preDropRouteCount,
                @preDropStatusParity,
                @preDropSemanticJsonParity,
                @preDropDifferenceCount,
                @preDropAttestationSha256,
                @healthEvidenceSha256,
                @binarySha256,
                @restoreToolSha256,
                @restoreImageIdSha256,
                @repositoryCommit,
                @dependencyInventory,
                @dependencyInventorySha256,
                @topologyEvidence,
                @topologySha256,
                @livenessEvidence,
                @livenessSha256,
                @databaseName,
                @databaseOid,
                @systemIdentifier,
                @serverVersionNum,
                @healthStartedAt,
                @healthCompletedAt,
                @healthSampleCount,
                @healthSampleIntervalSeconds,
                @proofCompletedAt,
                @approvedBy,
                @approvalReference,
                @preflightEvidence,
                @dropEvidenceSha256,
                @dropEvidence)
            """;
        var q1 = plan.RehearsalPlan;
        var q2 = plan.ActivePlan;
        command.Parameters.AddWithValue(
            "dropOperationId",
            plan.DropOperationId!);
        command.Parameters.AddWithValue(
            "planDigest",
            plan.PlanDigest!);
        command.Parameters.AddWithValue(
            "rehearsalOperationId",
            q1.OperationId!);
        command.Parameters.AddWithValue(
            "activeOperationId",
            q2.OperationId!);
        command.Parameters.AddWithValue(
            "q1QuarantinedAttestationId",
            plan.RehearsalQuarantinedAttestation
                .AttestationId);
        command.Parameters.AddWithValue(
            "q1SoakAttestationId",
            plan.RehearsalSoakAttestation
                .AttestationId);
        command.Parameters.AddWithValue(
            "q1ReattachedAttestationId",
            plan.RehearsalReattachedAttestation
                .AttestationId);
        command.Parameters.AddWithValue(
            "q2QuarantinedAttestationId",
            plan.ActiveQuarantinedAttestation
                .AttestationId);
        command.Parameters.AddWithValue(
            "q2SoakAttestationId",
            plan.ActiveSoakAttestation
                .AttestationId);
        command.Parameters.AddWithValue(
            "archiveSha256",
            q2.Archive.ArchiveSha256);
        command.Parameters.AddWithValue(
            "freshArchiveProofManifestSha256",
            plan.FreshProofManifestSha256);
        command.Parameters.AddWithValue(
            "recoveryBundleManifestSha256",
            plan.RecoveryBundleManifestSha256);
        command.Parameters.AddWithValue(
            "semanticProjectionVersion",
            plan.ActiveSemantic.ProjectionVersion);
        command.Parameters.AddWithValue(
            "rehearsalCatalogSha256",
            plan.RehearsalSemantic.CatalogSha256);
        command.Parameters.AddWithValue(
            "catalogSha256",
            plan.ActiveSemantic.CatalogSha256);
        command.Parameters.AddWithValue(
            "rehearsalSemanticCatalogSha256",
            plan.RehearsalSemantic
                .SemanticCatalogSha256);
        command.Parameters.AddWithValue(
            "semanticCatalogSha256",
            plan.ActiveSemantic
                .SemanticCatalogSha256);
        command.Parameters.AddWithValue(
            "rehearsalLogicalIndexShapeSha256",
            plan.RehearsalSemantic
                .LogicalIndexShapeSha256);
        command.Parameters.AddWithValue(
            "logicalIndexShapeSha256",
            plan.ActiveSemantic
                .LogicalIndexShapeSha256);
        command.Parameters.AddWithValue(
            "rehearsalPhysicalIndexInventorySha256",
            plan.RehearsalSemantic
                .PhysicalIndexInventorySha256);
        command.Parameters.AddWithValue(
            "physicalIndexInventorySha256",
            plan.ActiveSemantic
                .PhysicalIndexInventorySha256);
        command.Parameters.AddWithValue(
            "preDropBaselineRouteManifestSha256",
            plan.PreDropParity.BaselineManifestSha256);
        command.Parameters.AddWithValue(
            "preDropCandidateRouteManifestSha256",
            plan.PreDropParity.CandidateManifestSha256);
        command.Parameters.AddWithValue(
            "preDropRouteCount",
            plan.PreDropParity.RouteCount);
        command.Parameters.AddWithValue(
            "preDropStatusParity",
            plan.PreDropParity.StatusParity);
        command.Parameters.AddWithValue(
            "preDropSemanticJsonParity",
            plan.PreDropParity.SemanticJsonParity);
        command.Parameters.AddWithValue(
            "preDropDifferenceCount",
            plan.PreDropParity.DifferenceCount);
        command.Parameters.AddWithValue(
            "preDropAttestationSha256",
            DropJson.Sha256(
                new
                {
                    Stage = "pre_drop",
                    Parity = plan.PreDropParity,
                    Database = snapshot,
                }));
        command.Parameters.AddWithValue(
            "healthEvidenceSha256",
            plan.Health.EvidenceSha256!);
        command.Parameters.AddWithValue(
            "binarySha256",
            plan.BinarySha256);
        command.Parameters.AddWithValue(
            "restoreToolSha256",
            plan.RestoreToolSha256);
        command.Parameters.AddWithValue(
            "restoreImageIdSha256",
            plan.RestoreImageIdSha256);
        command.Parameters.AddWithValue(
            "repositoryCommit",
            plan.RepositoryCommit);
        AddJson(
            command,
            "dependencyInventory",
            snapshot.DependencyInventory);
        command.Parameters.AddWithValue(
            "dependencyInventorySha256",
            snapshot.DependencyInventorySha256);
        AddJson(
            command,
            "topologyEvidence",
            snapshot.TopologyEvidence);
        command.Parameters.AddWithValue(
            "topologySha256",
            snapshot.TopologySha256);
        AddJson(
            command,
            "livenessEvidence",
            snapshot.LivenessEvidence);
        command.Parameters.AddWithValue(
            "livenessSha256",
            snapshot.LivenessSha256);
        command.Parameters.AddWithValue(
            "databaseName",
            snapshot.DatabaseName);
        command.Parameters.AddWithValue(
            "databaseOid",
            snapshot.DatabaseOid);
        command.Parameters.AddWithValue(
            "systemIdentifier",
            snapshot.SystemIdentifier);
        command.Parameters.AddWithValue(
            "serverVersionNum",
            snapshot.ServerVersionNum);
        command.Parameters.AddWithValue(
            "healthStartedAt",
            plan.Health.StartedAtUtc);
        command.Parameters.AddWithValue(
            "healthCompletedAt",
            plan.Health.CompletedAtUtc);
        command.Parameters.AddWithValue(
            "healthSampleCount",
            plan.Health.SuccessfulSampleCount);
        command.Parameters.AddWithValue(
            "healthSampleIntervalSeconds",
            plan.Health.SampleIntervalSeconds);
        command.Parameters.AddWithValue(
            "proofCompletedAt",
            plan.ProofCompletedAtUtc);
        command.Parameters.AddWithValue(
            "approvedBy",
            approvedBy);
        command.Parameters.AddWithValue(
            "approvalReference",
            approvalReference);
        AddJson(command, "preflightEvidence", preflight);
        command.Parameters.AddWithValue(
            "dropEvidenceSha256",
            DropJson.Sha256(
                new
                {
                    PlanDigest = plan.PlanDigest,
                    Preflight = preflight,
                    Drop = dropEvidence,
                }));
        AddJson(command, "dropEvidence", dropEvidence);
        var result = (string)(
            await command.ExecuteScalarAsync(ct)
            ?? throw new InvalidOperationException(
                "Drop function returned no operation ID."));
        if (result != plan.DropOperationId)
        {
            throw new InvalidDataException(
                "Drop function returned another operation ID.");
        }
    }

    public static void ValidateBoundarySnapshot(
        SnapshotGenerationDropPlan plan,
        SnapshotGenerationDropDatabaseSnapshot current)
    {
        var expected = plan.Database;
        var archive = plan.ActivePlan.Archive;
        var failures = new List<string>();
        Add(current.DatabaseName != expected.DatabaseName,
            "database-name");
        Add(current.DatabaseOid != expected.DatabaseOid,
            "database-oid");
        Add(current.SystemIdentifier !=
            expected.SystemIdentifier, "system-identifier");
        Add(current.ServerVersionNum !=
            expected.ServerVersionNum, "server-version");
        Add(current.LatestCycleId != archive.CycleId,
            "latest-cycle");
        Add(current.CurrentPublicationId !=
            archive.TriggerPublicationId,
            "current-publication");
        Add(current.PublishedScrapeId !=
            archive.TriggerScrapeId,
            "published-scrape");
        Add(current.PublicReadsFrozen, "public-reads-frozen");
        Add(current.WorkingPublicationId is not null,
            "working-publication");
        Add(current.PublicationCommitIntentActive,
            "publication-commit-intent");
        Add(current.MaxScoreMutationGateActive,
            "max-score-mutation-gate");
        Add(!current.NotificationsComplete,
            "notifications");
        Add(!current.WorkerOffline, "worker-online");
        Add(current.RunningScrapeCount != 0,
            "running-scrape");
        Add(current.ActiveReferenceCount != 0,
            "target-reference");
        Add(current.UnreplayedWriterFailureCount != 0,
            "writer-failure");
        Add(current.OtherActiveHoldCount != 0,
            "other-hold");
        Add(!current.ExactHoldActive, "exact-hold");
        Add(!current.PrivateRelationExists,
            "private-relation");
        Add(!current.OriginalRelationAbsent,
            "original-relation");
        Add(current.CurrentChildOid != archive.ChildOid,
            "child-oid");
        Add(current.CurrentChildRelfilenode !=
            archive.ChildRelfilenode, "child-relfilenode");
        Add(current.CurrentRowCount != archive.RowCount,
            "row-count");
        Add(current.CurrentTotalBytes != archive.TotalBytes,
            "total-bytes");
        Add(!current.Detached, "detached");
        Add(!current.ExactCheckPresent, "exact-check");
        Add(!current.MutationGuardPresent,
            "mutation-guard");
        Add(!current.DefaultIdentityValid,
            "default-identity");
        Add(!current.DefaultExclusionPresent,
            "default-exclusion");
        Add(current.DefaultRowCount != 0,
            "default-rows");
        Add(current.ChildIndexCount != 2,
            "child-indexes");
        Add(current.DependencyInventorySha256 !=
            expected.DependencyInventorySha256,
            "dependency-inventory");
        Add(current.TopologySha256 !=
            expected.TopologySha256,
            "topology");
        Add(current.LivenessSha256 !=
            expected.LivenessSha256,
            "liveness");
        if (failures.Count > 0)
        {
            throw new InvalidDataException(
                "Drop boundary validation failed: "
                + string.Join(", ", failures));
        }
        return;

        void Add(bool condition, string code)
        {
            if (condition)
                failures.Add(code);
        }
    }

    private static void ValidatePendingState(
        SnapshotGenerationDropState state,
        SnapshotGenerationDropPlan plan)
    {
        if (state.OperationExists
            || state.OriginalRelationExists
            || !state.QuarantineRelationExists
            || !state.OriginalOidExists
            || state.ChildOid !=
                plan.ActivePlan.Archive.ChildOid)
        {
            throw new InvalidDataException(
                "Drop state is neither a clean pending operation nor a committed operation.");
        }
    }

    private static void ValidateCommittedState(
        SnapshotGenerationDropState state,
        SnapshotGenerationDropPlan plan)
    {
        if (!state.OperationExists
            || state.DropOperationId !=
                plan.DropOperationId
            || state.PlanDigest != plan.PlanDigest
            || state.Instrument !=
                plan.ActivePlan.Archive.Instrument
            || state.SnapshotId !=
                plan.ActivePlan.Archive.SnapshotId
            || state.OriginalRelationExists
            || state.QuarantineRelationExists
            || state.OriginalOidExists
            || !state.DurableDefaultExclusionPresent
            || !state.HoldActive)
        {
            throw new InvalidDataException(
                "Committed drop state is incomplete or inconsistent.");
        }
    }

    private static SnapshotGenerationDropExecutionReport
        BuildReport(
            SnapshotGenerationDropPlan plan,
            SnapshotGenerationDropState state,
            string actor,
            string reference,
            string commitOutcome)
    {
        ValidateCommittedState(state, plan);
        return new SnapshotGenerationDropExecutionReport(
            1,
            SnapshotGenerationDropToolContract.ToolId,
            "drop",
            plan.DropOperationId!,
            plan.PlanDigest!,
            "dropped",
            commitOutcome,
            DateTimeOffset.UtcNow,
            state.ApprovedBy ?? actor,
            state.ApprovalReference ?? reference,
            plan.ActivePlan.Archive.Instrument,
            plan.ActivePlan.Archive.SnapshotId,
            plan.ActivePlan.Archive.ChildOid,
            plan.ActivePlan.Archive.ChildRelfilenode,
            plan.ActivePlan.Archive.RowCount,
            plan.ActivePlan.Archive
                .RowFingerprintSha256,
            JsonSerializer.SerializeToElement(
                state,
                DropJson.Strict)).Seal();
    }

    private static async Task<JsonElement>
        ReadOperationJsonAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction? transaction,
            string operationId,
            CancellationToken ct)
    {
        return await QueryJsonAsync(
            connection,
            transaction,
            """
            SELECT jsonb_build_object(
                'operationId', operation.operation_id,
                'planDigest', operation.plan_digest,
                'quarantineSchema',
                    operation.quarantine_schema,
                'quarantineRelation',
                    operation.quarantine_relation,
                'childSchema', operation.child_schema,
                'childRelation', operation.child_relation,
                'snapshotCheck',
                    operation.snapshot_check_constraint,
                'mutationGuard',
                    operation.mutation_guard_trigger,
                'defaultSchema',
                    operation.default_partition_schema,
                'defaultRelation',
                    operation.default_partition_relation,
                'defaultOid',
                    operation.default_partition_oid,
                'defaultConstraint',
                    operation.default_exclusion_constraint,
                'holdId', operation.hold_id)
            FROM snapshot_generation_quarantine_operations
                operation
            WHERE operation.operation_id = @operationId
            """,
            ("operationId", operationId),
            ct);
    }

    private static async Task ValidateAttestationAsync(
        NpgsqlConnection connection,
        SnapshotGenerationQuarantineAttestationReport
            report,
        CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(*)::INTEGER
            FROM snapshot_generation_quarantine_attestations
            WHERE attestation_id = @attestationId
              AND operation_id = @operationId
              AND stage = @stage
              AND publication_id = @publicationId
              AND published_scrape_id = @publishedScrapeId
              AND route_count = 55
              AND status_parity
              AND semantic_json_parity
              AND difference_count = 0
              AND baseline_route_manifest_sha256 =
                    @baselineSha256
              AND candidate_route_manifest_sha256 =
                    @candidateSha256
              AND evidence_sha256 = @evidenceSha256
            """;
        command.Parameters.AddWithValue(
            "attestationId",
            report.AttestationId);
        command.Parameters.AddWithValue(
            "operationId",
            report.OperationId);
        command.Parameters.AddWithValue(
            "stage",
            report.Stage);
        command.Parameters.AddWithValue(
            "publicationId",
            report.Parity.PublicationId);
        command.Parameters.AddWithValue(
            "publishedScrapeId",
            report.Parity.PublishedScrapeId);
        command.Parameters.AddWithValue(
            "baselineSha256",
            report.Parity.BaselineManifestSha256);
        command.Parameters.AddWithValue(
            "candidateSha256",
            report.Parity.CandidateManifestSha256);
        command.Parameters.AddWithValue(
            "evidenceSha256",
            report.EvidenceSha256);
        if (Convert.ToInt32(
                await command.ExecuteScalarAsync(ct)) != 1)
        {
            throw new InvalidDataException(
                $"Durable {report.Stage} attestation differs from its report.");
        }
    }

    private static async Task<JsonElement> QueryJsonAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        string sql,
        (string Name, object Value) parameter,
        CancellationToken ct) =>
        await QueryJsonAsync(
            connection,
            transaction,
            sql,
            [parameter],
            ct);

    private static async Task<JsonElement> QueryJsonAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        string sql,
        IReadOnlyList<(string Name, object Value)> parameters,
        CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var parameter in parameters)
        {
            command.Parameters.AddWithValue(
                parameter.Name,
                parameter.Value);
        }
        var text = (string)(
            await command.ExecuteScalarAsync(ct)
            ?? throw new InvalidDataException(
                "Database evidence query returned no value."));
        using var document = JsonDocument.Parse(text);
        return document.RootElement.Clone();
    }

    private static async Task<FingerprintEvidence>
        ComputePrivateFingerprintAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction? transaction,
            string schema,
            string relation,
            long snapshotId,
            string instrument,
            CancellationToken ct)
    {
        var sql = $"""
            COPY (
                SELECT to_jsonb(row_value)::text
                FROM ONLY {Quote(schema)}.{Quote(relation)}
                    AS row_value
                WHERE snapshot_id = {snapshotId}
                  AND instrument =
                        '{instrument.Replace("'", "''")}'
                ORDER BY
                    snapshot_id,
                    song_id,
                    instrument,
                    account_id
            ) TO STDOUT
            """;
        using var hash = IncrementalHash.CreateHash(
            HashAlgorithmName.SHA256);
        var chars = ArrayPool<char>.Shared.Rent(
            64 * 1024);
        var bytes = ArrayPool<byte>.Shared.Rent(
            Encoding.UTF8.GetMaxByteCount(chars.Length));
        long streamBytes = 0;
        try
        {
            using var reader =
                await connection.BeginTextExportAsync(
                    sql,
                    ct);
            var encoder = Encoding.UTF8.GetEncoder();
            while (true)
            {
                var read = await reader.ReadAsync(
                    chars.AsMemory(),
                    ct);
                if (read == 0)
                    break;
                encoder.Convert(
                    chars,
                    0,
                    read,
                    bytes,
                    0,
                    bytes.Length,
                    false,
                    out _,
                    out var used,
                    out _);
                hash.AppendData(bytes, 0, used);
                streamBytes += used;
            }
            encoder.Convert(
                [],
                0,
                0,
                bytes,
                0,
                bytes.Length,
                true,
                out _,
                out var finalBytes,
                out _);
            if (finalBytes > 0)
            {
                hash.AppendData(bytes, 0, finalBytes);
                streamBytes += finalBytes;
            }
        }
        finally
        {
            ArrayPool<char>.Shared.Return(chars);
            ArrayPool<byte>.Shared.Return(bytes);
        }
        await using var count = connection.CreateCommand();
        count.Transaction = transaction;
        count.CommandText =
            $"SELECT COUNT(*)::BIGINT FROM ONLY "
            + $"{Quote(schema)}.{Quote(relation)} "
            + "WHERE snapshot_id = @snapshotId "
            + "AND instrument = @instrument";
        count.Parameters.AddWithValue(
            "snapshotId",
            snapshotId);
        count.Parameters.AddWithValue(
            "instrument",
            instrument);
        var rowCount = Convert.ToInt64(
            await count.ExecuteScalarAsync(ct));
        return new FingerprintEvidence(
            "sha256-copy-to-jsonb-text-ordered-snapshot_id-song_id-instrument-account_id-v1",
            Convert.ToHexString(
                    hash.GetHashAndReset())
                .ToLowerInvariant(),
            rowCount,
            streamBytes);
    }

    private static async Task<SessionLockLease>
        AcquireSessionLockChainAsync(
            NpgsqlConnection connection,
            CancellationToken ct)
    {
        var locks = new List<(long? Numeric, string? Text)>();
        var ordered = new (long?, string?)[]
        {
            (
                SnapshotGenerationQuarantineEvidenceContract
                    .RegistrationAdvisoryLockKey,
                null),
            (
                SnapshotGenerationQuarantineEvidenceContract
                    .ServiceMaintenanceAdvisoryLockKey,
                null),
            (
                SnapshotGenerationQuarantineEvidenceContract
                    .PublicationAdvisoryLockKey,
                null),
            (
                SnapshotGenerationQuarantineEvidenceContract
                    .PlannerAdvisoryLockKey,
                null),
            (
                null,
                SnapshotGenerationQuarantineEvidenceContract
                    .SnapshotDdlLockName),
            (
                SnapshotGenerationQuarantineEvidenceContract
                    .ExecutorAdvisoryLockKey,
                null),
            (
                SnapshotGenerationDropToolContract
                    .DropAdvisoryLockKey,
                null),
        };
        var stopwatch = Stopwatch.StartNew();
        try
        {
            foreach (var item in ordered)
            {
                while (true)
                {
                    await using var command =
                        connection.CreateCommand();
                    if (item.Item2 is null)
                    {
                        command.CommandText =
                            "SELECT pg_try_advisory_lock(@key)";
                        command.Parameters.AddWithValue(
                            "key",
                            item.Item1!.Value);
                    }
                    else
                    {
                        command.CommandText = """
                            SELECT pg_try_advisory_lock(
                                hashtextextended(@key, 0))
                            """;
                        command.Parameters.AddWithValue(
                            "key",
                            item.Item2);
                    }
                    if (await command.ExecuteScalarAsync(ct)
                        is true)
                    {
                        locks.Add(item);
                        break;
                    }
                    if (stopwatch.Elapsed >=
                        TimeSpan.FromSeconds(5))
                    {
                        throw new TimeoutException(
                            "Snapshot-generation drop lock chain remained busy for five seconds.");
                    }
                    await Task.Delay(50, ct);
                }
            }
            return new SessionLockLease(
                connection,
                locks);
        }
        catch
        {
            await SessionLockLease.ReleaseAsync(
                connection,
                locks);
            throw;
        }
    }

    private static void AddChainParameters(
        NpgsqlCommand command,
        SnapshotGenerationDropPlan plan)
    {
        command.Parameters.AddWithValue(
            "rehearsalOperationId",
            plan.RehearsalPlan.OperationId!);
        command.Parameters.AddWithValue(
            "activeOperationId",
            plan.ActivePlan.OperationId!);
        command.Parameters.AddWithValue(
            "q1QuarantinedAttestationId",
            plan.RehearsalQuarantinedAttestation
                .AttestationId);
        command.Parameters.AddWithValue(
            "q1SoakAttestationId",
            plan.RehearsalSoakAttestation
                .AttestationId);
        command.Parameters.AddWithValue(
            "q1ReattachedAttestationId",
            plan.RehearsalReattachedAttestation
                .AttestationId);
        command.Parameters.AddWithValue(
            "q2QuarantinedAttestationId",
            plan.ActiveQuarantinedAttestation
                .AttestationId);
        command.Parameters.AddWithValue(
            "q2SoakAttestationId",
            plan.ActiveSoakAttestation
                .AttestationId);
    }

    private static void AddJson(
        NpgsqlCommand command,
        string name,
        JsonElement value) =>
        command.Parameters.Add(
                name,
                NpgsqlDbType.Jsonb)
            .Value = value.GetRawText();

    private static string Quote(string value)
    {
        if (string.IsNullOrWhiteSpace(value)
            || value.Any(character =>
                !(char.IsAsciiLetterOrDigit(character)
                  || character == '_'))
            || !(char.IsAsciiLetter(value[0])
                 || value[0] == '_'))
        {
            throw new InvalidDataException(
                $"Unsafe database identifier: {value}");
        }
        return $"\"{value.Replace("\"", "\"\"")}\"";
    }

    private static void ValidateActor(
        string value,
        string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value)
            || value.Length > 512
            || value.Any(char.IsControl))
        {
            throw new ArgumentException(
                $"{parameterName} is invalid.",
                parameterName);
        }
    }

    private sealed class SessionLockLease
        : IAsyncDisposable
    {
        private readonly NpgsqlConnection _connection;
        private readonly IReadOnlyList<(long? Numeric, string? Text)>
            _locks;
        private int _released;

        public SessionLockLease(
            NpgsqlConnection connection,
            IReadOnlyList<(long? Numeric, string? Text)> locks)
        {
            _connection = connection;
            _locks = locks.ToArray();
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _released, 1) != 0)
                return;
            await ReleaseAsync(_connection, _locks);
        }

        public static async Task ReleaseAsync(
            NpgsqlConnection connection,
            IReadOnlyList<(long? Numeric, string? Text)> locks)
        {
            if (connection.State != ConnectionState.Open)
                return;
            foreach (var item in locks.Reverse())
            {
                await using var command =
                    connection.CreateCommand();
                if (item.Text is null)
                {
                    command.CommandText =
                        "SELECT pg_advisory_unlock(@key)";
                    command.Parameters.AddWithValue(
                        "key",
                        item.Numeric!.Value);
                }
                else
                {
                    command.CommandText = """
                        SELECT pg_advisory_unlock(
                            hashtextextended(@key, 0))
                        """;
                    command.Parameters.AddWithValue(
                        "key",
                        item.Text);
                }
                await command.ExecuteScalarAsync(
                    CancellationToken.None);
            }
        }
    }
}
