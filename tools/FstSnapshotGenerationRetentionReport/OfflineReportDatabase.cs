using System.Data;
using System.Text.Json;
using FSTService.Persistence.Maintenance;
using Npgsql;

namespace FstSnapshotGenerationRetentionReport;

public sealed class OfflineReportDatabase : IAsyncDisposable
{
    public NpgsqlDataSource DataSource { get; }
    private readonly string _connectionString;
    private int _disposeAttempted;
    internal Func<Task>? DisposeTestHook { get; set; }

    public OfflineReportDatabase(string connectionString)
    {
        var builder = new NpgsqlConnectionStringBuilder(connectionString)
        {
            ApplicationName = "fst-snapshot-retention-offline-report",
            Timeout = 10,
            CommandTimeout = SnapshotGenerationRetentionPlanner.OfflineCommandTimeoutSeconds,
            Pooling = false,
            IncludeErrorDetail = false,
            SearchPath = "pg_catalog,public",
            Options = "-c statement_timeout=15s -c lock_timeout=2s -c row_security=off "
                + "-c idle_session_timeout=20s -c idle_in_transaction_session_timeout=20s "
                + "-c transaction_timeout=120s",
        };
        _connectionString = builder.ConnectionString;
        DataSource = NpgsqlDataSource.Create(builder.ConnectionString);
    }

    public static OfflineReportDatabase FromEnvironment()
    {
        var connection = Environment.GetEnvironmentVariable(OfflineReportContract.ConnectionEnvironment);
        if (string.IsNullOrWhiteSpace(connection))
            throw new OfflineReportRefusal("connection_environment_missing");
        return new(connection);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposeAttempted, 1) != 0)
            return;
        await DataSource.DisposeAsync();
        if (DisposeTestHook is not null)
            await DisposeTestHook();
    }

    public async Task<SnapshotGenerationRetentionOfflineResult> CompleteAndDisposeAsync(
        SnapshotGenerationRetentionOfflineResult result)
    {
        try
        {
            await DisposeAsync();
            return result;
        }
        catch
        {
            var verified = await SnapshotGenerationRetentionPlanner.ConfirmCommittedAfterCleanupAsync(
                _connectionString, result);
            return verified ?? throw new SnapshotGenerationRetentionOfflineRefusal("commit_outcome_uncertain")
            {
                PossibleCommittedCycleId = result.Cycle.CycleId,
            };
        }
    }

    public async Task<OfflineReportRuntimeIdentity> InspectAsync(
        IOfflineReportCodeIdentityProvider codeProvider,
        CancellationToken ct)
    {
        await using var connection = await DataSource.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(
            IsolationLevel.RepeatableRead, ct);
        await using var settings = connection.CreateCommand();
        settings.Transaction = transaction;
        settings.CommandTimeout = 15;
        settings.CommandText = "SET TRANSACTION READ ONLY";
        await settings.ExecuteNonQueryAsync(ct);
        settings.CommandText = """
            SELECT pg_catalog.pg_try_advisory_xact_lock_shared(
                pg_catalog.hashtextextended(
                    'fst.snapshot-generation-retention-schema', 0));
            """;
        if (await settings.ExecuteScalarAsync(ct) is not true)
            throw new OfflineReportRefusal("retention_schema_lock_busy");
        var identity = await CaptureAsync(connection, transaction, codeProvider, ct);
        await transaction.CommitAsync(ct);
        return identity;
    }

    public static async Task<OfflineReportRuntimeIdentity> CaptureAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        IOfflineReportCodeIdentityProvider codeProvider,
        CancellationToken ct)
    {
        var code = await codeProvider.CaptureAsync(ct);
        await using var database = connection.CreateCommand();
        database.Transaction = transaction;
        database.CommandTimeout = 15;
        database.CommandText = """
            SELECT current_database(), db.oid::BIGINT,
                control.system_identifier::TEXT,
                current_setting('server_version_num')::INTEGER,
                current_setting('data_directory'),
                pg_catalog.pg_postmaster_start_time(),
                session_user::TEXT, current_user::TEXT,
                (SELECT role_row.rolsuper OR role_row.rolbypassrls
                 FROM pg_catalog.pg_roles role_row WHERE role_row.rolname = current_user)
            FROM pg_catalog.pg_database db
            CROSS JOIN pg_catalog.pg_control_system() control
            WHERE db.datname = current_database()
            """;
        OfflineReportDatabaseIdentity source;
        await using (var reader = await database.ExecuteReaderAsync(ct))
        {
            if (!await reader.ReadAsync(ct))
                throw new OfflineReportRefusal("database_identity_unavailable");
            source = new(reader.GetString(0), reader.GetInt64(1), reader.GetString(2),
                reader.GetInt32(3), reader.GetString(4), reader.GetDateTime(5),
                reader.GetString(6), reader.GetString(7), reader.GetBoolean(8));
        }
        if (source.ServerVersionNum is < 170000 or >= 180000)
            throw new OfflineReportRefusal("postgresql_17_required");

        await using var shape = connection.CreateCommand();
        shape.Transaction = transaction;
        shape.CommandTimeout = 15;
        shape.CommandText = RequiredShapeSql;
        var accepted = await shape.ExecuteScalarAsync(ct) is true;
        await using var schema = connection.CreateCommand();
        schema.Transaction = transaction;
        schema.CommandTimeout = 15;
        schema.CommandText = SchemaFingerprintSql;
        var encoded = (string?)await schema.ExecuteScalarAsync(ct)
            ?? throw new OfflineReportRefusal("schema_identity_unavailable");
        using var json = JsonDocument.Parse(encoded);
        var configuration = accepted
            ? await SnapshotGenerationRetentionWorkerConfigurationStore.ReadCurrentAsync(
                connection, transaction, ct)
            : null;
        return new(code, source, OfflineReportContract.Digest(source),
            OfflineReportContract.Digest(json.RootElement), accepted, configuration);
    }

    private const string RequiredShapeSql = """
        SELECT
            NOT EXISTS (
                SELECT 1
                FROM unnest(ARRAY[
                    'snapshot_generation_retention_cycles',
                    'snapshot_generation_retention_observations',
                    'snapshot_generation_retention_deferrals',
                    'snapshot_generation_retention_evidence',
                    'snapshot_generation_retention_holds',
                    'snapshot_generation_retention_worker_configuration',
                    'scrape_log', 'scrape_publication_state',
                    'publication_generations', 'service_worker_status',
                    'scrape_writer_failures', 'leaderboard_entries_snapshot',
                    'leaderboard_published_scope_source'
                ]) required(name)
                WHERE pg_catalog.to_regclass('public.' || required.name) IS NULL)
            AND (
                SELECT count(*)
                FROM pg_catalog.pg_trigger trigger_row
                JOIN pg_catalog.pg_class relation ON relation.oid = trigger_row.tgrelid
                JOIN pg_catalog.pg_namespace namespace ON namespace.oid = relation.relnamespace
                JOIN pg_catalog.pg_proc function ON function.oid = trigger_row.tgfoid
                JOIN pg_catalog.pg_namespace function_namespace
                    ON function_namespace.oid = function.pronamespace
                WHERE namespace.nspname = 'public'
                  AND function_namespace.nspname = 'public'
                  AND relation.relname IN (
                    'snapshot_generation_retention_cycles',
                    'snapshot_generation_retention_observations',
                    'snapshot_generation_retention_deferrals',
                    'snapshot_generation_retention_evidence',
                    'snapshot_generation_retention_worker_configuration')
                  AND trigger_row.tgname = CASE
                    WHEN relation.relname='snapshot_generation_retention_worker_configuration'
                    THEN 'trg_reject_retention_worker_configuration_mutation'
                    ELSE 'trg_reject_' || relation.relname || '_mutation'
                  END
                  AND NOT trigger_row.tgisinternal
                  AND trigger_row.tgenabled IN ('O', 'A')
                  AND function.proname =
                    'fst_reject_snapshot_generation_retention_evidence_mutation'
            ) = 5
            AND (
                SELECT count(*)
                FROM pg_catalog.pg_constraint constraint_row
                JOIN pg_catalog.pg_class relation ON relation.oid = constraint_row.conrelid
                JOIN pg_catalog.pg_namespace namespace ON namespace.oid = relation.relnamespace
                WHERE namespace.nspname = 'public'
                  AND relation.relname IN (
                    'snapshot_generation_retention_cycles',
                    'snapshot_generation_retention_observations',
                    'snapshot_generation_retention_deferrals')
                  AND constraint_row.contype = 'c' AND constraint_row.convalidated
                  AND pg_catalog.pg_get_expr(
                    constraint_row.conbin, constraint_row.conrelid) = 'report_only'
            ) = 3
            AND (
                SELECT count(*)
                FROM pg_catalog.pg_constraint constraint_row
                JOIN pg_catalog.pg_class relation ON relation.oid=constraint_row.conrelid
                JOIN pg_catalog.pg_namespace namespace ON namespace.oid=relation.relnamespace
                WHERE namespace.nspname='public'
                  AND ((relation.relname='snapshot_generation_retention_cycles'
                    AND constraint_row.conname='ck_snapshot_generation_retention_cycle_safe_point')
                    OR (relation.relname='snapshot_generation_retention_deferrals'
                    AND constraint_row.conname='ck_snapshot_generation_retention_deferral_safe_point'))
                  AND constraint_row.contype='c' AND constraint_row.convalidated
                  AND pg_catalog.pg_get_expr(
                    constraint_row.conbin,constraint_row.conrelid,false) =
                    '(safe_point_kind = ANY (ARRAY[''terminal_worker_post_publication''::text, ''operator_offline_post_publication''::text]))'
            ) = 2
            AND EXISTS (
                SELECT 1
                FROM pg_catalog.pg_constraint constraint_row
                WHERE constraint_row.conrelid = pg_catalog.to_regclass(
                    'public.snapshot_generation_retention_cycles')
                  AND constraint_row.conname =
                    'ux_snapshot_generation_retention_cycle_trigger'
                  AND constraint_row.contype='u'
                  AND constraint_row.convalidated
                  AND pg_catalog.pg_get_constraintdef(constraint_row.oid,false) =
                    'UNIQUE (trigger_scrape_id, trigger_publication_id)')
        """;

    private const string SchemaFingerprintSql = """
        WITH relations AS (
            SELECT relation.*, namespace.nspname
            FROM pg_catalog.pg_class relation
            JOIN pg_catalog.pg_namespace namespace ON namespace.oid = relation.relnamespace
            WHERE namespace.nspname = 'public'
              AND relation.relkind IN ('r', 'p', 'v', 'm', 'f', 'S')
              AND NOT (relation.relispartition AND relation.relname ~
                '^leaderboard_entries_snapshot_[a-z0-9_]+_s[1-9][0-9]*$')
        ), objects AS (
            SELECT 'relation' AS kind, relation.relname::TEXT AS identity,
                jsonb_build_object(
                    'kind', relation.relkind, 'persistence', relation.relpersistence,
                    'options', relation.reloptions,
                    'owner', pg_catalog.pg_get_userbyid(relation.relowner),
                    'rowSecurity', relation.relrowsecurity,
                    'forceRowSecurity', relation.relforcerowsecurity,
                    'tablespace', coalesce(tablespace.spcname, 'pg_default'),
                    'partitionKey', pg_catalog.pg_get_partkeydef(relation.oid),
                    'partitionBound', pg_catalog.pg_get_expr(
                        relation.relpartbound, relation.oid),
                    'view', CASE WHEN relation.relkind IN ('v', 'm')
                        THEN pg_catalog.pg_get_viewdef(relation.oid, false) END
                ) AS definition
            FROM relations relation
            LEFT JOIN pg_catalog.pg_tablespace tablespace
                ON tablespace.oid = relation.reltablespace
            UNION ALL
            SELECT 'column', relation.relname || '.' || attribute.attname,
                jsonb_build_object(
                    'position', attribute.attnum,
                    'type', pg_catalog.format_type(attribute.atttypid, attribute.atttypmod),
                    'notNull', attribute.attnotnull, 'identity', attribute.attidentity,
                    'generated', attribute.attgenerated,
                    'default', pg_catalog.pg_get_expr(default_row.adbin, default_row.adrelid),
                    'collation', collation_row.collname)
            FROM relations relation
            JOIN pg_catalog.pg_attribute attribute ON attribute.attrelid = relation.oid
            LEFT JOIN pg_catalog.pg_attrdef default_row
                ON default_row.adrelid = attribute.attrelid AND default_row.adnum = attribute.attnum
            LEFT JOIN pg_catalog.pg_collation collation_row ON collation_row.oid = attribute.attcollation
            WHERE attribute.attnum > 0 AND NOT attribute.attisdropped
            UNION ALL
            SELECT 'constraint', relation.relname || '.' || constraint_row.conname,
                jsonb_build_object(
                    'definition', pg_catalog.pg_get_constraintdef(constraint_row.oid, false),
                    'validated', constraint_row.convalidated,
                    'deferrable', constraint_row.condeferrable,
                    'deferred', constraint_row.condeferred)
            FROM relations relation
            JOIN pg_catalog.pg_constraint constraint_row ON constraint_row.conrelid = relation.oid
            UNION ALL
            SELECT 'index', relation.relname || '.' || index_relation.relname,
                jsonb_build_object(
                    'definition', pg_catalog.pg_get_indexdef(index_row.indexrelid),
                    'valid', index_row.indisvalid, 'ready', index_row.indisready,
                    'live', index_row.indislive, 'unique', index_row.indisunique,
                    'primary', index_row.indisprimary)
            FROM relations relation
            JOIN pg_catalog.pg_index index_row ON index_row.indrelid = relation.oid
            JOIN pg_catalog.pg_class index_relation ON index_relation.oid = index_row.indexrelid
            UNION ALL
            SELECT 'trigger', relation.relname || '.' || trigger_row.tgname,
                jsonb_build_object(
                    'definition', pg_catalog.pg_get_triggerdef(trigger_row.oid, false),
                    'enabled', trigger_row.tgenabled)
            FROM relations relation
            JOIN pg_catalog.pg_trigger trigger_row ON trigger_row.tgrelid = relation.oid
            WHERE NOT trigger_row.tgisinternal
            UNION ALL
            SELECT 'policy', relation.relname || '.' || policy.polname,
                jsonb_build_object(
                    'command', policy.polcmd, 'permissive', policy.polpermissive,
                    'roles', (SELECT jsonb_agg(
                        CASE WHEN role_oid = 0 THEN 'PUBLIC'
                            ELSE pg_catalog.pg_get_userbyid(role_oid) END ORDER BY role_oid)
                        FROM unnest(policy.polroles) role_oid),
                    'using', pg_catalog.pg_get_expr(policy.polqual, policy.polrelid),
                    'check', pg_catalog.pg_get_expr(policy.polwithcheck, policy.polrelid))
            FROM relations relation
            JOIN pg_catalog.pg_policy policy ON policy.polrelid = relation.oid
            UNION ALL
            SELECT 'function',
                function.proname || '(' || pg_catalog.pg_get_function_identity_arguments(function.oid) || ')',
                jsonb_build_object(
                    'definition', pg_catalog.pg_get_functiondef(function.oid),
                    'owner', pg_catalog.pg_get_userbyid(function.proowner))
            FROM pg_catalog.pg_proc function
            JOIN pg_catalog.pg_namespace namespace ON namespace.oid = function.pronamespace
            WHERE namespace.nspname = 'public' AND function.prokind IN ('f', 'p')
        )
        SELECT coalesce(jsonb_agg(
            jsonb_build_object('kind', kind, 'identity', identity, 'definition', definition)
            ORDER BY kind COLLATE "C", identity COLLATE "C"), '[]'::jsonb)::TEXT
        FROM objects
        """;
}

public sealed class OfflineReportAttestation(
    IOfflineReportCodeIdentityProvider codeProvider,
    OfflineReportAssertions assertions)
    : ISnapshotGenerationRetentionOfflineAttestation
{
    public OfflineReportRuntimeIdentity? ObservedIdentity { get; private set; }

    public async Task VerifyAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken ct)
    {
        var identity = await OfflineReportDatabase.CaptureAsync(
            connection, transaction, codeProvider, ct);
        assertions.Require(identity);
        ObservedIdentity = identity;
    }
}
