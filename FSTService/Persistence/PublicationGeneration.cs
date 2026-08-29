using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace FSTService.Persistence;

public sealed class PublicationCommitOptions
{
    public const string Section = "PublicationCommit";

    public int DrainTimeoutMilliseconds { get; set; } = 5_000;
    public int RetryDelayMilliseconds { get; set; } = 50;
    public int RelationLockTimeoutMilliseconds { get; set; } = 500;
    public int StatementTimeoutMilliseconds { get; set; } = 5_000;
    public int MaxExclusiveLockDurationMilliseconds { get; set; } = 5_000;
    public int StaleCommitIntentSeconds { get; set; } = 30;
    public int ContentionRetryAttempts { get; set; } = 24;
    public int ContentionRetryDelayMilliseconds { get; set; } = 250;
    public int DefaultReadLeaseSeconds { get; set; } = 30;
    public int ExportReadLeaseSeconds { get; set; } = 180;
    public int AbandonedReadyGraceSeconds { get; set; } = 30;
    public int WorkerHeartbeatFreshSeconds { get; set; } = 30;
    public int PreparationLockTimeoutMilliseconds { get; set; } = 5_000;
    public int PreparationStatementTimeoutMilliseconds { get; set; } =
        900_000;
    public int PreparationTransactionTimeoutMilliseconds { get; set; } =
        1_800_000;
    public int NotificationRecoveryRetrySeconds { get; set; } = 60;
}

public sealed record PublicationPreparationResult(
    long ScrapeId,
    long PublicationId,
    long? CurrentPublicationId,
    long? PreviousPublicationId,
    bool PromoteCachedResponses,
    int? ExpectedPublishedScopeCount,
    bool QueueImprovementNotifications,
    string ImprovementNotificationProjectionScopesJson,
    int ImprovementNotificationProjectionScopeCount,
    long? BandProjectionGeneration,
    DateTime PreparedAtUtc,
    TimeSpan PrepareDuration,
    bool AlreadyPublished = false)
{
    public DateTime? RankingsInputCutoffUtc { get; init; }
}

public sealed record PublicationCommitResult(
    long ScrapeId,
    long PublicationId,
    long? PreviousPublicationId,
    TimeSpan DrainDuration,
    TimeSpan ExclusiveLockDuration,
    int LockRejections,
    int RelationLockRetries,
    bool AlreadyPublished = false);

public sealed record PublicationCommitIntentHandle(
    long ScrapeId,
    string OwnerToken,
    DateTime StartedAtUtc);

public sealed record PublicationBandOrphanSweepResult(
    bool LockAcquired,
    bool Completed,
    int ExaminedTableCount,
    IReadOnlyList<string> DroppedTables);

public sealed class DeferredPublicationMetadataException
    : InvalidOperationException
{
    public DeferredPublicationMetadataException(
        string message,
        Exception? innerException = null)
        : base(message, innerException)
    {
    }
}

public sealed class PublicationCommitBusyException : InvalidOperationException
{
    public PublicationCommitBusyException(
        string message,
        TimeSpan drainDuration,
        int lockRejections,
        int relationLockRetries)
        : base(message)
    {
        DrainDuration = drainDuration;
        LockRejections = lockRejections;
        RelationLockRetries = relationLockRetries;
    }

    public TimeSpan DrainDuration { get; }
    public int LockRejections { get; }
    public int RelationLockRetries { get; }
}

public sealed class PublicationCommitDeadlineExceededException
    : TimeoutException
{
    public PublicationCommitDeadlineExceededException(
        string message,
        TimeSpan elapsed,
        TimeSpan budget,
        int relationLockRetries,
        Exception? innerException = null)
        : base(message, innerException)
    {
        Elapsed = elapsed;
        Budget = budget;
        RelationLockRetries = relationLockRetries;
    }

    public TimeSpan Elapsed { get; }
    public TimeSpan Budget { get; }
    public int RelationLockRetries { get; }
}

public enum PublicationCommitIntentReconciliationStatus
{
    NotPresent,
    Fresh,
    Active,
    Cleared,
    FailedCandidateIsolated,
    AbandonedWorkingIsolated,
}

public sealed record PublicationCommitIntentReconciliationResult(
    PublicationCommitIntentReconciliationStatus Status,
    long? ScrapeId,
    TimeSpan? Age);

public static class PublicationGenerationStatus
{
    public const string Building = "building";
    public const string Ready = "ready";
    public const string Current = "current";
    public const string Retained = "retained";
    public const string Failed = "failed";
    public const string Retired = "retired";
}

public sealed record PublicationGenerationInfo(
    long PublicationId,
    long? ScrapeId,
    string Status,
    long? PreviousPublicationId,
    DateTime CreatedAtUtc,
    DateTime? SourceCutAtUtc,
    DateTime? ReadyAtUtc,
    DateTime? PublishedAtUtc,
    DateTime? FailedAtUtc,
    string? FailurePhase,
    string? FailureMessage);

public sealed record PublicationPointerState(
    long? CurrentPublicationId,
    long? PreviousPublicationId,
    long? WorkingPublicationId,
    long? PublishedScrapeId,
    DateTime? PublishedAtUtc);

public sealed record PublicationSurfaceBinding(
    long PublicationId,
    string SurfaceName,
    string BindingKind,
    string BindingJson,
    long? RowCount,
    string? ContentHash,
    string Status,
    DateTime BuiltAtUtc);

public sealed record PublicationSurfaceSourceEvidence(
    string SurfaceName,
    bool Exists,
    long PublicationId,
    long? ScrapeId,
    long? RowCount,
    string? ContentHash,
    long? SourceGeneration = null);

public sealed record PublicationCachedResponse(
    long PublicationId,
    long PublishedScrapeId,
    DateTime? PublishedAtUtc,
    byte[] Json,
    string ETag,
    DateTime? CachedAtUtc = null,
    string ContentType = "application/json",
    string? ContentSha256 = null,
    string? CacheKey = null);

public sealed record PublicationCacheLookup(
    bool HasCurrentPublication,
    PublicationCachedResponse? CachedResponse);

public static class PublicationSurfaceNames
{
    public const string AccountNames = "account_names";
    public const string AccountOverlays = "account_overlays";
    public const string ApiResponseCache = "api_response_cache";
    public const string BandRankings = "band_rankings";
    public const string History = "history";
    public const string ImprovementNotifications = "improvement_notifications";
    public const string ItemShop = "item_shop";
    public const string PathArtifacts = "path_artifacts";
    public const string SoloScopeSources = "solo_scope_sources";
    public const string SongCatalog = "song_catalog";
}

public sealed record PublishedScopeSourceKey(
    string Instrument,
    string SongId,
    string ScopeKind);

public static class PublishedScopeSourceBindingContract
{
    public const int KeyHashVersion = 1;
    public const string BindingKind = "scrape_id";
    public const string TableName =
        "leaderboard_published_scope_source";

    public static string ComputeKeyHash(
        IEnumerable<PublishedScopeSourceKey> keys)
    {
        ArgumentNullException.ThrowIfNull(keys);
        using var hash =
            IncrementalHash.CreateHash(
                HashAlgorithmName.SHA256);
        foreach (var key in keys
                     .OrderBy(
                         static key => key.Instrument,
                         StringComparer.Ordinal)
                     .ThenBy(
                         static key => key.SongId,
                         StringComparer.Ordinal)
                     .ThenBy(
                         static key => key.ScopeKind,
                         StringComparer.Ordinal))
        {
            AppendValue(hash, key.Instrument);
            AppendValue(hash, key.SongId);
            AppendValue(hash, key.ScopeKind);
            hash.AppendData([(byte)'\n']);
        }

        return Convert.ToHexString(
                hash.GetHashAndReset())
            .ToLowerInvariant();
    }

    public static bool IsKeyHash(string? value) =>
        value is { Length: 64 }
        && value.All(static character =>
            character is >= '0' and <= '9'
            or >= 'a' and <= 'f');

    private static void AppendValue(
        IncrementalHash hash,
        string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var valueBytes = Encoding.UTF8.GetBytes(value);
        var lengthBytes = Encoding.ASCII.GetBytes(
            valueBytes.Length.ToString(
                CultureInfo.InvariantCulture));
        hash.AppendData(lengthBytes);
        hash.AppendData([(byte)':']);
        hash.AppendData(valueBytes);
    }
}

public sealed record PublicationSongCatalogInfo(
    long PublicationId,
    long ScrapeId,
    long CatalogVersion,
    int SchemaVersion,
    string CatalogJson,
    string ContentHash,
    int SongCount,
    DateTime SourceCapturedAtUtc);

public static class PublicationGenerationRetirementSchemaMigration
{
    public const long AdvisoryLockKey =
        5067481511116520100L;

    public const string ColumnsSql = """
        ALTER TABLE publication_generations
            ADD COLUMN IF NOT EXISTS retired_at TIMESTAMPTZ;
        ALTER TABLE publication_generations
            ADD COLUMN IF NOT EXISTS retired_scrape_id BIGINT;
        """;

    public const string CreateIndexSql = """
        CREATE INDEX CONCURRENTLY
            ix_publication_generations_retired_scrape
        ON public.publication_generations (retired_scrape_id)
        WHERE retired_scrape_id IS NOT NULL
        """;

    public const string DropIndexSql = """
        DROP INDEX CONCURRENTLY IF EXISTS
            public.ix_publication_generations_retired_scrape
        """;

    public const string IndexValidationSql = """
        SELECT EXISTS (
            SELECT 1
            FROM pg_class index_relation
            JOIN pg_namespace index_namespace
              ON index_namespace.oid =
                    index_relation.relnamespace
            JOIN pg_index index_row
              ON index_row.indexrelid =
                    index_relation.oid
            JOIN pg_class table_relation
              ON table_relation.oid =
                    index_row.indrelid
            JOIN pg_namespace table_namespace
              ON table_namespace.oid =
                    table_relation.relnamespace
            JOIN pg_am access_method
              ON access_method.oid =
                    index_relation.relam
            WHERE index_namespace.nspname = 'public'
              AND index_relation.relname =
                    'ix_publication_generations_retired_scrape'
              AND index_relation.relkind = 'i'
              AND table_namespace.nspname = 'public'
              AND table_relation.relname =
                    'publication_generations'
              AND access_method.amname = 'btree'
              AND index_row.indisvalid
              AND index_row.indisready
              AND NOT index_row.indisprimary
              AND NOT index_row.indisunique
              AND index_row.indnkeyatts = 1
              AND (
                    SELECT array_agg(
                        attribute.attname::TEXT
                        ORDER BY key.ordinality)
                    FROM unnest(index_row.indkey)
                        WITH ORDINALITY
                        AS key(attnum, ordinality)
                    JOIN pg_attribute attribute
                      ON attribute.attrelid =
                            index_row.indrelid
                     AND attribute.attnum =
                            key.attnum
                  ) = ARRAY['retired_scrape_id']::TEXT[]
              AND regexp_replace(
                    pg_get_expr(
                        index_row.indpred,
                        index_row.indrelid),
                    '[()[:space:]]',
                    '',
                    'g') =
                    'retired_scrape_idISNOTNULL'
        )
        """;
}

public static class PublicationGenerationForeignKeyMigration
{
    public const string Sql = """
        CREATE OR REPLACE FUNCTION
            public.fst_restrict_publication_generation_scrape_delete_v2()
        RETURNS trigger
        LANGUAGE plpgsql
        SET search_path = pg_catalog, public
        AS $publication_scrape_delete_guard$
        BEGIN
            IF EXISTS (
                SELECT 1
                FROM public.publication_generations generation
                WHERE generation.scrape_id = OLD.id
            ) THEN
                RAISE EXCEPTION
                    'scrape_log row % is retained by publication_generations',
                    OLD.id
                    USING
                        ERRCODE = '23503',
                        TABLE = 'scrape_log',
                        CONSTRAINT =
                            'publication_generations_scrape_id_restrict_fkey_v2';
            END IF;
            RETURN OLD;
        END
        $publication_scrape_delete_guard$;

        DO $publication_fk_migration$
        BEGIN
            IF EXISTS (
                SELECT 1
                FROM pg_constraint constraint_row
                WHERE constraint_row.conrelid =
                        'publication_generations'::regclass
                  AND constraint_row.conname =
                        'publication_generations_scrape_id_restrict_fkey_v2'
                  AND NOT (
                      constraint_row.contype = 'f'
                      AND constraint_row.conkey = ARRAY[(
                        SELECT attribute.attnum
                        FROM pg_attribute attribute
                        WHERE attribute.attrelid =
                                'publication_generations'::regclass
                          AND attribute.attname = 'scrape_id'
                    )]::SMALLINT[]
                      AND constraint_row.confrelid =
                        'scrape_log'::regclass
                      AND constraint_row.confkey = ARRAY[(
                        SELECT attribute.attnum
                        FROM pg_attribute attribute
                        WHERE attribute.attrelid =
                                'scrape_log'::regclass
                          AND attribute.attname = 'id'
                    )]::SMALLINT[]
                      AND constraint_row.confupdtype = 'a'
                      AND constraint_row.confdeltype = 'r'
                      AND constraint_row.confmatchtype = 's'
                      AND NOT constraint_row.condeferrable
                      AND NOT constraint_row.condeferred
                  )
            ) THEN
                ALTER TABLE publication_generations
                    DROP CONSTRAINT
                        publication_generations_scrape_id_restrict_fkey_v2;
            END IF;

            IF NOT EXISTS (
                SELECT 1
                FROM pg_constraint constraint_row
                WHERE constraint_row.conrelid =
                        'publication_generations'::regclass
                  AND constraint_row.conname =
                        'publication_generations_scrape_id_restrict_fkey_v2'
            ) THEN
                ALTER TABLE publication_generations
                    ADD CONSTRAINT
                        publication_generations_scrape_id_restrict_fkey_v2
                    FOREIGN KEY (scrape_id)
                    REFERENCES scrape_log(id)
                    ON DELETE RESTRICT
                    NOT VALID;
            END IF;

            IF EXISTS (
                SELECT 1
                FROM pg_constraint constraint_row
                WHERE constraint_row.conrelid =
                        'publication_generations'::regclass
                  AND constraint_row.conname =
                        'publication_generations_scrape_id_restrict_fkey_v2'
                  AND NOT constraint_row.convalidated
            ) THEN
                ALTER TABLE publication_generations
                    VALIDATE CONSTRAINT
                        publication_generations_scrape_id_restrict_fkey_v2;
            END IF;

            IF EXISTS (
                SELECT 1
                FROM pg_trigger trigger_row
                WHERE trigger_row.tgrelid =
                        'scrape_log'::regclass
                  AND trigger_row.tgname =
                        'trg_scrape_log_restrict_publication_generation_delete_v2'
                  AND (
                      trigger_row.tgisinternal
                      OR trigger_row.tgenabled <> 'O'
                      OR trigger_row.tgfoid <>
                            'public.fst_restrict_publication_generation_scrape_delete_v2()'
                                ::regprocedure
                      OR (trigger_row.tgtype & 1) = 0
                      OR (trigger_row.tgtype & 2) = 0
                      OR (trigger_row.tgtype & 8) = 0
                  )
            ) THEN
                DROP TRIGGER
                    trg_scrape_log_restrict_publication_generation_delete_v2
                    ON scrape_log;
            END IF;

            IF NOT EXISTS (
                SELECT 1
                FROM pg_trigger trigger_row
                WHERE trigger_row.tgrelid =
                        'scrape_log'::regclass
                  AND trigger_row.tgname =
                        'trg_scrape_log_restrict_publication_generation_delete_v2'
            ) THEN
                CREATE TRIGGER
                    trg_scrape_log_restrict_publication_generation_delete_v2
                BEFORE DELETE ON scrape_log
                FOR EACH ROW
                EXECUTE FUNCTION
                    public.fst_restrict_publication_generation_scrape_delete_v2();
            END IF;

            IF NOT EXISTS (
                SELECT 1
                FROM pg_constraint constraint_row
                WHERE constraint_row.conrelid =
                        'publication_generations'::regclass
                  AND constraint_row.conname =
                        'publication_generations_previous_publication_id_fkey'
                  AND constraint_row.contype = 'f'
                  AND constraint_row.conkey = ARRAY[(
                        SELECT attribute.attnum
                        FROM pg_attribute attribute
                        WHERE attribute.attrelid =
                                'publication_generations'::regclass
                          AND attribute.attname =
                                'previous_publication_id'
                    )]::SMALLINT[]
                  AND constraint_row.confrelid =
                        'publication_generations'::regclass
                  AND constraint_row.confkey = ARRAY[(
                        SELECT attribute.attnum
                        FROM pg_attribute attribute
                        WHERE attribute.attrelid =
                                'publication_generations'::regclass
                          AND attribute.attname = 'publication_id'
                    )]::SMALLINT[]
                  AND constraint_row.confupdtype = 'a'
                  AND constraint_row.confdeltype = 'n'
                  AND constraint_row.confmatchtype = 's'
                  AND NOT constraint_row.condeferrable
                  AND NOT constraint_row.condeferred
                  AND constraint_row.convalidated
            ) THEN
                IF EXISTS (
                    SELECT 1
                    FROM pg_constraint
                    WHERE conrelid =
                            'publication_generations'::regclass
                      AND conname =
                            'publication_generations_previous_publication_id_fkey'
                ) THEN
                    ALTER TABLE publication_generations
                        DROP CONSTRAINT
                            publication_generations_previous_publication_id_fkey;
                END IF;
                ALTER TABLE publication_generations
                    ADD CONSTRAINT
                        publication_generations_previous_publication_id_fkey
                    FOREIGN KEY (previous_publication_id)
                    REFERENCES publication_generations(publication_id)
                    ON DELETE SET NULL;
            END IF;

            IF NOT EXISTS (
                SELECT 1
                FROM pg_constraint constraint_row
                WHERE constraint_row.conrelid =
                        'scrape_publication_state'::regclass
                  AND constraint_row.conname =
                        'scrape_publication_state_previous_publication_id_fkey'
                  AND constraint_row.contype = 'f'
                  AND constraint_row.conkey = ARRAY[(
                        SELECT attribute.attnum
                        FROM pg_attribute attribute
                        WHERE attribute.attrelid =
                                'scrape_publication_state'::regclass
                          AND attribute.attname =
                                'previous_publication_id'
                    )]::SMALLINT[]
                  AND constraint_row.confrelid =
                        'publication_generations'::regclass
                  AND constraint_row.confkey = ARRAY[(
                        SELECT attribute.attnum
                        FROM pg_attribute attribute
                        WHERE attribute.attrelid =
                                'publication_generations'::regclass
                          AND attribute.attname = 'publication_id'
                    )]::SMALLINT[]
                  AND constraint_row.confupdtype = 'a'
                  AND constraint_row.confdeltype = 'n'
                  AND constraint_row.confmatchtype = 's'
                  AND NOT constraint_row.condeferrable
                  AND NOT constraint_row.condeferred
                  AND constraint_row.convalidated
            ) THEN
                IF EXISTS (
                    SELECT 1
                    FROM pg_constraint
                    WHERE conrelid =
                            'scrape_publication_state'::regclass
                      AND conname =
                            'scrape_publication_state_previous_publication_id_fkey'
                ) THEN
                    ALTER TABLE scrape_publication_state
                        DROP CONSTRAINT
                            scrape_publication_state_previous_publication_id_fkey;
                END IF;
                ALTER TABLE scrape_publication_state
                    ADD CONSTRAINT
                        scrape_publication_state_previous_publication_id_fkey
                    FOREIGN KEY (previous_publication_id)
                    REFERENCES publication_generations(publication_id)
                    ON DELETE SET NULL;
            END IF;

            IF (
                SELECT COUNT(*)
                FROM pg_constraint constraint_row
                WHERE constraint_row.conrelid =
                        'publication_generations'::regclass
                  AND constraint_row.conname =
                        'publication_generations_scrape_id_restrict_fkey_v2'
                  AND constraint_row.contype = 'f'
                  AND constraint_row.conkey = ARRAY[(
                        SELECT attribute.attnum
                        FROM pg_attribute attribute
                        WHERE attribute.attrelid =
                                'publication_generations'::regclass
                          AND attribute.attname = 'scrape_id'
                    )]::SMALLINT[]
                  AND constraint_row.confrelid =
                        'scrape_log'::regclass
                  AND constraint_row.confdeltype = 'r'
                  AND constraint_row.convalidated
            ) <> 1 THEN
                RAISE EXCEPTION
                    'publication_generations.scrape_id must own the exact rolling-safe restrictive foreign key';
            END IF;

            IF (
                SELECT COUNT(*)
                FROM pg_trigger trigger_row
                WHERE trigger_row.tgrelid =
                        'scrape_log'::regclass
                  AND trigger_row.tgname =
                        'trg_scrape_log_restrict_publication_generation_delete_v2'
                  AND NOT trigger_row.tgisinternal
                  AND trigger_row.tgenabled = 'O'
                  AND trigger_row.tgfoid =
                        'public.fst_restrict_publication_generation_scrape_delete_v2()'
                            ::regprocedure
                  AND (trigger_row.tgtype & 1) <> 0
                  AND (trigger_row.tgtype & 2) <> 0
                  AND (trigger_row.tgtype & 8) <> 0
            ) <> 1 THEN
                RAISE EXCEPTION
                    'scrape_log must own the rolling-safe publication generation delete guard';
            END IF;

            IF (
                SELECT COUNT(*)
                FROM pg_constraint constraint_row
                WHERE constraint_row.conrelid =
                        'publication_generations'::regclass
                  AND constraint_row.contype = 'f'
                  AND constraint_row.conkey = ARRAY[(
                        SELECT attribute.attnum
                        FROM pg_attribute attribute
                        WHERE attribute.attrelid =
                                'publication_generations'::regclass
                          AND attribute.attname =
                                'previous_publication_id'
                    )]::SMALLINT[]
            ) <> 1 THEN
                RAISE EXCEPTION
                    'publication_generations.previous_publication_id must own exactly one foreign key';
            END IF;

            IF (
                SELECT COUNT(*)
                FROM pg_constraint constraint_row
                WHERE constraint_row.conrelid =
                        'scrape_publication_state'::regclass
                  AND constraint_row.contype = 'f'
                  AND constraint_row.conkey = ARRAY[(
                        SELECT attribute.attnum
                        FROM pg_attribute attribute
                        WHERE attribute.attrelid =
                                'scrape_publication_state'::regclass
                          AND attribute.attname =
                                'previous_publication_id'
                    )]::SMALLINT[]
            ) <> 1 THEN
                RAISE EXCEPTION
                    'scrape_publication_state.previous_publication_id must own exactly one foreign key';
            END IF;
        END
        $publication_fk_migration$;
        """;
}

public static class PublicationGenerationSchema
{
    public const long AdvisoryLockKey = 5067481511116519500L;
    public const long CacheBuildAdvisoryLockBase = 5067481511116520000L;

    public const string Sql = """
        CREATE TABLE IF NOT EXISTS publication_generations (
            publication_id         BIGINT GENERATED BY DEFAULT AS IDENTITY PRIMARY KEY,
            scrape_id              BIGINT UNIQUE REFERENCES scrape_log(id) ON DELETE CASCADE,
            status                 TEXT NOT NULL,
            previous_publication_id BIGINT
                REFERENCES publication_generations(publication_id) ON DELETE SET NULL,
            created_at             TIMESTAMPTZ NOT NULL DEFAULT now(),
            source_cut_at          TIMESTAMPTZ,
            ready_at               TIMESTAMPTZ,
            published_at           TIMESTAMPTZ,
            failed_at              TIMESTAMPTZ,
            failure_phase          TEXT,
            failure_message        TEXT,
            retired_at             TIMESTAMPTZ,
            retired_scrape_id      BIGINT,
            metadata               JSONB NOT NULL DEFAULT '{}'::jsonb,
            CONSTRAINT ck_publication_generations_status
                CHECK (status IN ('building', 'ready', 'current', 'retained', 'failed', 'retired'))
        );

        CREATE INDEX IF NOT EXISTS ix_publication_generations_status
            ON publication_generations (status, publication_id DESC);

        CREATE TABLE IF NOT EXISTS publication_surface_bindings (
            publication_id BIGINT NOT NULL
                REFERENCES publication_generations(publication_id) ON DELETE CASCADE,
            surface_name   TEXT NOT NULL,
            binding_kind   TEXT NOT NULL,
            binding_json   JSONB NOT NULL,
            row_count      BIGINT,
            content_hash   TEXT,
            status         TEXT NOT NULL DEFAULT 'ready',
            built_at       TIMESTAMPTZ NOT NULL DEFAULT now(),
            PRIMARY KEY (publication_id, surface_name),
            CONSTRAINT ck_publication_surface_binding_status
                CHECK (status IN ('building', 'ready', 'failed', 'retired'))
        );

        CREATE INDEX IF NOT EXISTS ix_publication_surface_bindings_surface
            ON publication_surface_bindings (surface_name, publication_id DESC);

        CREATE OR REPLACE FUNCTION
            publication_scope_source_binding_validation(
                target_publication_id BIGINT)
        RETURNS TABLE (
            publication_id BIGINT,
            scrape_id BIGINT,
            generation_status TEXT,
            expected_row_count BIGINT,
            binding_row_count BIGINT,
            actual_row_count BIGINT,
            binding_content_hash TEXT,
            actual_key_hash TEXT,
            invalid_row_count INTEGER,
            duplicate_key_count INTEGER,
            binding_identity_valid BOOLEAN,
            is_valid BOOLEAN)
        LANGUAGE sql
        STABLE
        AS $scope_source_validation$
            WITH target AS (
                SELECT
                    generation.publication_id,
                    generation.scrape_id,
                    generation.status,
                    generation.metadata #>>
                        '{publicationPreparation,scrapeId}'
                        AS metadata_scrape_id,
                    generation.metadata #>>
                        '{publicationPreparation,publicationId}'
                        AS metadata_publication_id,
                    CASE
                        WHEN generation.metadata #>>
                                '{publicationPreparation,expectedPublishedScopeCount}'
                                ~ '^[1-9][0-9]*$'
                        THEN (
                            generation.metadata #>>
                                '{publicationPreparation,expectedPublishedScopeCount}'
                        )::BIGINT
                        ELSE NULL
                    END AS expected_row_count
                FROM publication_generations generation
                WHERE generation.publication_id =
                        target_publication_id
                  AND generation.scrape_id IS NOT NULL
            ), binding AS (
                SELECT
                    target.publication_id,
                    COUNT(binding.*)::INTEGER
                        AS binding_count,
                    MAX(binding.binding_kind)
                        AS binding_kind,
                    MAX(binding.binding_json::TEXT)::JSONB
                        AS binding_json,
                    MAX(binding.row_count)
                        AS row_count,
                    MAX(binding.content_hash)
                        AS content_hash,
                    MAX(binding.status)
                        AS status
                FROM target
                LEFT JOIN publication_surface_bindings binding
                  ON binding.publication_id =
                        target.publication_id
                 AND binding.surface_name =
                        'solo_scope_sources'
                GROUP BY target.publication_id
            ), source_stats AS (
                SELECT
                    target.publication_id,
                    COUNT(source.*)::BIGINT
                        AS actual_row_count,
                    COUNT(source.*) FILTER (
                        WHERE btrim(source.song_id) = ''
                           OR source.instrument NOT IN (
                                'Solo_Guitar',
                                'Solo_Bass',
                                'Solo_Vocals',
                                'Solo_Drums',
                                'Solo_PeripheralGuitar',
                                'Solo_PeripheralBass',
                                'Solo_PeripheralVocals',
                                'Solo_PeripheralCymbals',
                                'Solo_PeripheralDrums')
                           OR source.scope_kind <> 'alltime'
                           OR btrim(
                                source.content_fingerprint) = ''
                           OR btrim(
                                source.coverage_fingerprint) = ''
                           OR NOT source.is_complete
                           OR source.source_scrape_id <= 0
                           OR source.source_scrape_id >
                                source.published_scrape_id
                           OR source.reported_total_entries <
                                source.row_count
                           OR (
                                source.source_kind = 'snapshot'
                                AND (
                                    source.source_snapshot_id IS NULL
                                    OR source.source_snapshot_id <= 0
                                    OR source.source_snapshot_id <>
                                        source.source_scrape_id
                                    OR source.row_count <= 0
                                    OR source.reported_total_pages <= 0
                                )
                           )
                           OR (
                                source.source_kind = 'empty'
                                AND (
                                    source.source_snapshot_id IS NOT NULL
                                    OR source.row_count <> 0
                                    OR source.reported_total_entries <> 0
                                    OR source.reported_total_pages <> 0
                                )
                           )
                           OR source.source_kind NOT IN (
                                'snapshot',
                                'empty')
                    )::INTEGER AS invalid_row_count,
                    (
                        COUNT(source.*)
                        - COUNT(DISTINCT (
                            source.instrument,
                            source.song_id,
                            source.scope_kind))
                    )::INTEGER AS duplicate_key_count,
                    encode(
                        sha256(
                            convert_to(
                                COALESCE(
                                    string_agg(
                                        octet_length(
                                            source.instrument)::TEXT
                                            || ':'
                                            || source.instrument
                                            || octet_length(
                                                source.song_id)::TEXT
                                            || ':'
                                            || source.song_id
                                            || octet_length(
                                                source.scope_kind)::TEXT
                                            || ':'
                                            || source.scope_kind
                                            || chr(10),
                                        '' ORDER BY
                                            source.instrument
                                                COLLATE "C",
                                            source.song_id
                                                COLLATE "C",
                                            source.scope_kind
                                                COLLATE "C"),
                                    ''),
                                'UTF8')),
                        'hex') AS actual_key_hash
                FROM target
                LEFT JOIN leaderboard_published_scope_source source
                  ON source.published_scrape_id =
                        target.scrape_id
                GROUP BY target.publication_id
            ), validation AS (
                SELECT
                    target.publication_id,
                    target.scrape_id,
                    target.status AS generation_status,
                    target.expected_row_count,
                    binding.row_count
                        AS binding_row_count,
                    source.actual_row_count,
                    binding.content_hash
                        AS binding_content_hash,
                    source.actual_key_hash,
                    source.invalid_row_count,
                    source.duplicate_key_count,
                    COALESCE(
                        binding.binding_count = 1
                        AND target.metadata_scrape_id =
                            target.scrape_id::TEXT
                        AND target.metadata_publication_id =
                            target.publication_id::TEXT
                        AND binding.binding_kind = 'scrape_id'
                        AND binding.status = 'ready'
                        AND binding.binding_json ->> 'table' =
                            'leaderboard_published_scope_source'
                        AND binding.binding_json ->> 'publicationId' =
                            target.publication_id::TEXT
                        AND binding.binding_json ->> 'publishedScrapeId' =
                            target.scrape_id::TEXT
                        AND binding.binding_json ->> 'keyHashVersion' =
                            '1',
                        FALSE) AS binding_identity_valid
                FROM target
                JOIN binding
                  ON binding.publication_id =
                        target.publication_id
                JOIN source_stats source
                  ON source.publication_id =
                        target.publication_id
            )
            SELECT
                validation.publication_id,
                validation.scrape_id,
                validation.generation_status,
                validation.expected_row_count,
                validation.binding_row_count,
                validation.actual_row_count,
                validation.binding_content_hash,
                validation.actual_key_hash,
                validation.invalid_row_count,
                validation.duplicate_key_count,
                validation.binding_identity_valid,
                COALESCE(
                    validation.expected_row_count > 0
                    AND validation.binding_row_count =
                        validation.expected_row_count
                    AND validation.actual_row_count =
                        validation.expected_row_count
                    AND validation.invalid_row_count = 0
                    AND validation.duplicate_key_count = 0
                    AND validation.binding_identity_valid
                    AND validation.binding_content_hash ~
                        '^[0-9a-f]{64}$'
                    AND validation.binding_content_hash =
                        validation.actual_key_hash,
                    FALSE) AS is_valid
            FROM validation
        $scope_source_validation$;

        CREATE SEQUENCE IF NOT EXISTS song_catalog_version_seq;

        CREATE TABLE IF NOT EXISTS live_song_catalog (
            id              BOOLEAN     PRIMARY KEY DEFAULT TRUE CHECK (id),
            catalog_version BIGINT      NOT NULL
                DEFAULT nextval('song_catalog_version_seq'),
            schema_version  INTEGER     NOT NULL DEFAULT 1,
            catalog_json    JSONB       NOT NULL,
            content_hash    TEXT        NOT NULL,
            song_count      INTEGER     NOT NULL,
            source_kind     TEXT        NOT NULL
                DEFAULT 'legacy_columns_reconstructed',
            is_exact        BOOLEAN     NOT NULL DEFAULT FALSE,
            captured_at     TIMESTAMPTZ NOT NULL,
            CONSTRAINT ck_live_song_catalog_count
                CHECK (song_count >= 0),
            CONSTRAINT ck_live_song_catalog_hash
                CHECK (content_hash ~ '^[0-9a-f]{64}$'),
            CONSTRAINT ck_live_song_catalog_source_kind
                CHECK (source_kind IN (
                    'provider_exact',
                    'legacy_columns_reconstructed'))
        );

        CREATE TABLE IF NOT EXISTS publication_song_catalog (
            publication_id    BIGINT      PRIMARY KEY
                REFERENCES publication_generations(publication_id) ON DELETE CASCADE,
            catalog_version   BIGINT      NOT NULL
                DEFAULT nextval('song_catalog_version_seq'),
            schema_version    INTEGER     NOT NULL DEFAULT 1,
            catalog_json      JSONB       NOT NULL,
            content_hash      TEXT        NOT NULL,
            song_count        INTEGER     NOT NULL,
            source_kind       TEXT        NOT NULL
                DEFAULT 'legacy_publication_reconstructed',
            is_exact          BOOLEAN     NOT NULL DEFAULT FALSE,
            source_captured_at TIMESTAMPTZ NOT NULL,
            captured_at       TIMESTAMPTZ NOT NULL DEFAULT now(),
            CONSTRAINT ck_publication_song_catalog_count
                CHECK (song_count >= 0),
            CONSTRAINT ck_publication_song_catalog_hash
                CHECK (content_hash ~ '^[0-9a-f]{64}$'),
            CONSTRAINT ck_publication_song_catalog_source_kind
                CHECK (source_kind IN (
                    'provider_exact',
                    'legacy_publication_reconstructed'))
        );

        ALTER TABLE live_song_catalog
            ADD COLUMN IF NOT EXISTS catalog_version BIGINT;
        ALTER TABLE live_song_catalog
            ADD COLUMN IF NOT EXISTS schema_version INTEGER;
        ALTER TABLE live_song_catalog
            ADD COLUMN IF NOT EXISTS source_kind TEXT;
        ALTER TABLE live_song_catalog
            ADD COLUMN IF NOT EXISTS is_exact BOOLEAN;

        UPDATE live_song_catalog
        SET catalog_version = COALESCE(
                catalog_version,
                nextval('song_catalog_version_seq')),
            schema_version = COALESCE(schema_version, 1),
            source_kind = COALESCE(
                source_kind,
                'legacy_columns_reconstructed'),
            is_exact = COALESCE(is_exact, FALSE);

        ALTER TABLE live_song_catalog
            ALTER COLUMN catalog_version SET NOT NULL;
        ALTER TABLE live_song_catalog
            ALTER COLUMN catalog_version
            SET DEFAULT nextval('song_catalog_version_seq');
        ALTER TABLE live_song_catalog
            ALTER COLUMN schema_version SET NOT NULL;
        ALTER TABLE live_song_catalog
            ALTER COLUMN schema_version SET DEFAULT 1;
        ALTER TABLE live_song_catalog
            ALTER COLUMN source_kind SET NOT NULL;
        ALTER TABLE live_song_catalog
            ALTER COLUMN source_kind
            SET DEFAULT 'legacy_columns_reconstructed';
        ALTER TABLE live_song_catalog
            ALTER COLUMN is_exact SET NOT NULL;
        ALTER TABLE live_song_catalog
            ALTER COLUMN is_exact SET DEFAULT FALSE;

        ALTER TABLE publication_song_catalog
            ADD COLUMN IF NOT EXISTS catalog_version BIGINT;
        ALTER TABLE publication_song_catalog
            ADD COLUMN IF NOT EXISTS schema_version INTEGER;
        ALTER TABLE publication_song_catalog
            ADD COLUMN IF NOT EXISTS source_kind TEXT;
        ALTER TABLE publication_song_catalog
            ADD COLUMN IF NOT EXISTS is_exact BOOLEAN;

        UPDATE publication_song_catalog
        SET catalog_version = COALESCE(
                catalog_version,
                nextval('song_catalog_version_seq')),
            schema_version = COALESCE(schema_version, 1),
            source_kind = COALESCE(
                source_kind,
                'legacy_publication_reconstructed'),
            is_exact = COALESCE(is_exact, FALSE);

        ALTER TABLE publication_song_catalog
            ALTER COLUMN catalog_version SET NOT NULL;
        ALTER TABLE publication_song_catalog
            ALTER COLUMN catalog_version
            SET DEFAULT nextval('song_catalog_version_seq');
        ALTER TABLE publication_song_catalog
            ALTER COLUMN schema_version SET NOT NULL;
        ALTER TABLE publication_song_catalog
            ALTER COLUMN schema_version SET DEFAULT 1;
        ALTER TABLE publication_song_catalog
            ALTER COLUMN source_kind SET NOT NULL;
        ALTER TABLE publication_song_catalog
            ALTER COLUMN source_kind
            SET DEFAULT 'legacy_publication_reconstructed';
        ALTER TABLE publication_song_catalog
            ALTER COLUMN is_exact SET NOT NULL;
        ALTER TABLE publication_song_catalog
            ALTER COLUMN is_exact SET DEFAULT FALSE;

        DO $$
        BEGIN
            IF NOT EXISTS (
                SELECT 1
                FROM pg_constraint
                WHERE conrelid = 'live_song_catalog'::regclass
                  AND conname = 'ck_live_song_catalog_source_kind'
            ) THEN
                ALTER TABLE live_song_catalog
                    ADD CONSTRAINT ck_live_song_catalog_source_kind
                    CHECK (source_kind IN (
                        'provider_exact',
                        'legacy_columns_reconstructed'));
            END IF;

            IF NOT EXISTS (
                SELECT 1
                FROM pg_constraint
                WHERE conrelid = 'publication_song_catalog'::regclass
                  AND conname = 'ck_publication_song_catalog_source_kind'
            ) THEN
                ALTER TABLE publication_song_catalog
                    ADD CONSTRAINT ck_publication_song_catalog_source_kind
                    CHECK (source_kind IN (
                        'provider_exact',
                        'legacy_publication_reconstructed'));
            END IF;
        END $$;

        CREATE OR REPLACE FUNCTION normalize_legacy_live_song_catalog_write()
        RETURNS trigger
        LANGUAGE plpgsql
        AS $$
        BEGIN
            IF (
                NEW.catalog_json IS DISTINCT FROM OLD.catalog_json
                OR NEW.content_hash IS DISTINCT FROM OLD.content_hash
                OR NEW.song_count IS DISTINCT FROM OLD.song_count
            )
            AND NEW.catalog_version = OLD.catalog_version
            THEN
                NEW.catalog_version :=
                    nextval('song_catalog_version_seq');
                NEW.schema_version := 1;
                NEW.source_kind := 'legacy_columns_reconstructed';
                NEW.is_exact := FALSE;
            END IF;
            RETURN NEW;
        END $$;

        DROP TRIGGER IF EXISTS trg_normalize_legacy_live_song_catalog_write
            ON live_song_catalog;
        CREATE TRIGGER trg_normalize_legacy_live_song_catalog_write
        BEFORE UPDATE ON live_song_catalog
        FOR EACH ROW
        EXECUTE FUNCTION normalize_legacy_live_song_catalog_write();

        CREATE TABLE IF NOT EXISTS publication_api_response_cache (
            publication_id BIGINT NOT NULL
                REFERENCES publication_generations(publication_id) ON DELETE CASCADE,
            cache_key   TEXT        NOT NULL,
            json_data   BYTEA       NOT NULL,
            etag        TEXT        NOT NULL,
            cached_at   TIMESTAMPTZ NOT NULL DEFAULT now(),
            PRIMARY KEY (publication_id, cache_key)
        );

        CREATE TABLE IF NOT EXISTS publication_api_response_cache_staging (
            publication_id BIGINT NOT NULL
                REFERENCES publication_generations(publication_id) ON DELETE CASCADE,
            cache_key   TEXT        NOT NULL,
            json_data   BYTEA       NOT NULL,
            etag        TEXT        NOT NULL,
            cached_at   TIMESTAMPTZ NOT NULL DEFAULT now(),
            PRIMARY KEY (publication_id, cache_key)
        );

        ALTER TABLE scrape_publication_state
            ADD COLUMN IF NOT EXISTS current_publication_id BIGINT
                REFERENCES publication_generations(publication_id);
        ALTER TABLE scrape_publication_state
            ADD COLUMN IF NOT EXISTS previous_publication_id BIGINT
                REFERENCES publication_generations(publication_id) ON DELETE SET NULL;
        ALTER TABLE scrape_publication_state
            ADD COLUMN IF NOT EXISTS working_publication_id BIGINT
                REFERENCES publication_generations(publication_id);
        ALTER TABLE scrape_publication_state
            ADD COLUMN IF NOT EXISTS publication_commit_intent_started_at
                TIMESTAMPTZ;
        ALTER TABLE scrape_publication_state
            ADD COLUMN IF NOT EXISTS publication_commit_intent_heartbeat_at
                TIMESTAMPTZ;
        ALTER TABLE scrape_publication_state
            ADD COLUMN IF NOT EXISTS publication_commit_intent_owner
                TEXT;

        INSERT INTO publication_generations (
            scrape_id,
            status,
            created_at,
            source_cut_at,
            ready_at,
            published_at)
        SELECT
            publication.published_scrape_id,
            'current',
            COALESCE(scrape.started_at, publication.published_at, now()),
            scrape.completed_at,
            scrape.completed_at,
            publication.published_at
        FROM scrape_publication_state publication
        LEFT JOIN scrape_log scrape
          ON scrape.id = publication.published_scrape_id
        WHERE publication.id = TRUE
          AND publication.published_scrape_id IS NOT NULL
        ON CONFLICT (scrape_id) DO UPDATE SET
            status = CASE
                WHEN publication_generations.status = 'retired'
                    THEN publication_generations.status
                ELSE 'current'
            END,
            published_at = COALESCE(
                publication_generations.published_at,
                EXCLUDED.published_at),
            ready_at = COALESCE(
                publication_generations.ready_at,
                EXCLUDED.ready_at);

        UPDATE scrape_publication_state publication
        SET current_publication_id = generation.publication_id,
            updated_at = now()
        FROM publication_generations generation
        WHERE publication.id = TRUE
          AND publication.published_scrape_id = generation.scrape_id
          AND publication.current_publication_id IS NULL;

        WITH canonical_songs AS (
            SELECT
                song.song_id,
                jsonb_strip_nulls(jsonb_build_object(
                    '_title', song.title,
                    'track', jsonb_strip_nulls(jsonb_build_object(
                        'tt', song.title,
                        'ry', song.release_year,
                        'su', song.song_id,
                        'in', CASE
                            WHEN song.lead_diff IS NOT NULL
                              OR song.bass_diff IS NOT NULL
                              OR song.vocals_diff IS NOT NULL
                              OR song.drums_diff IS NOT NULL
                              OR song.pro_lead_diff IS NOT NULL
                              OR song.pro_bass_diff IS NOT NULL
                              OR song.plastic_guitar_diff IS NOT NULL
                              OR song.plastic_bass_diff IS NOT NULL
                              OR song.plastic_drums_diff IS NOT NULL
                              OR song.pro_vocals_diff IS NOT NULL
                            THEN jsonb_strip_nulls(jsonb_build_object(
                                'pb', COALESCE(
                                    song.plastic_bass_diff,
                                    song.pro_bass_diff),
                                'pd', song.plastic_drums_diff,
                                'vl', song.vocals_diff,
                                'pg', COALESCE(
                                    song.plastic_guitar_diff,
                                    song.pro_lead_diff),
                                'gr', song.lead_diff,
                                'ds', song.drums_diff,
                                'ba', song.bass_diff,
                                'bd', song.pro_vocals_diff))
                            ELSE NULL
                        END,
                        'mt', song.tempo,
                        'an', song.artist)),
                    '_activeDate', NULLIF(song.active_date, ''),
                    'lastModified', NULLIF(song.last_modified, '')))
                    AS song_json
            FROM songs song
            WHERE NULLIF(song.song_id, '') IS NOT NULL
        ),
        legacy_payload AS (
            SELECT
                jsonb_build_object(
                    'schemaVersion', 1,
                    'songs', COALESCE(
                        jsonb_agg(song_json ORDER BY song_id),
                        '[]'::jsonb)) AS catalog_json,
                COUNT(*)::integer AS song_count
            FROM canonical_songs
        )
        INSERT INTO live_song_catalog (
            id, catalog_version, schema_version, catalog_json,
            content_hash, song_count, source_kind, is_exact, captured_at)
        SELECT
            TRUE,
            nextval('song_catalog_version_seq'),
            1,
            catalog_json,
            encode(
                digest(convert_to(catalog_json::text, 'UTF8'), 'sha256'),
                'hex'),
            song_count,
            'legacy_columns_reconstructed',
            FALSE,
            now()
        FROM legacy_payload
        ON CONFLICT (id) DO NOTHING;

        WITH bootstrap_publications AS (
            SELECT current_publication_id AS publication_id
            FROM scrape_publication_state
            WHERE id = TRUE
              AND current_publication_id IS NOT NULL
            UNION
            SELECT working_publication_id
            FROM scrape_publication_state
            WHERE id = TRUE
              AND working_publication_id IS NOT NULL
        )
        INSERT INTO publication_song_catalog (
            publication_id, catalog_version, schema_version, catalog_json,
            content_hash, song_count, source_kind, is_exact,
            source_captured_at, captured_at)
        SELECT
            publication.publication_id,
            catalog.catalog_version,
            catalog.schema_version,
            catalog.catalog_json,
            catalog.content_hash,
            catalog.song_count,
            'legacy_publication_reconstructed',
            FALSE,
            catalog.captured_at,
            now()
        FROM bootstrap_publications publication
        CROSS JOIN live_song_catalog catalog
        WHERE catalog.id = TRUE
        ON CONFLICT (publication_id) DO NOTHING;

        INSERT INTO publication_surface_bindings (
            publication_id, surface_name, binding_kind, binding_json,
            row_count, content_hash, status, built_at)
        SELECT
            snapshot.publication_id,
            'song_catalog',
            'legacy_reconstructed_catalog',
            jsonb_build_object(
                'table', 'publication_song_catalog',
                'publicationId', snapshot.publication_id,
                'catalogVersion', snapshot.catalog_version,
                'schemaVersion', snapshot.schema_version,
                'sourceKind', snapshot.source_kind,
                'isExact', false,
                'sourceCapturedAt', snapshot.source_captured_at),
            snapshot.song_count,
            snapshot.content_hash,
            'building',
            snapshot.captured_at
        FROM publication_song_catalog snapshot
        JOIN scrape_publication_state publication
          ON publication.id = TRUE
         AND snapshot.publication_id IN (
             publication.current_publication_id,
             publication.working_publication_id)
        WHERE NOT snapshot.is_exact
        ON CONFLICT (publication_id, surface_name) DO UPDATE SET
            binding_kind = EXCLUDED.binding_kind,
            binding_json = EXCLUDED.binding_json,
            row_count = EXCLUDED.row_count,
            content_hash = EXCLUDED.content_hash,
            status = EXCLUDED.status,
            built_at = EXCLUDED.built_at
        WHERE publication_surface_bindings.status <> 'retired'
          AND (
              publication_surface_bindings.binding_kind IN (
                  'legacy_live_unversioned',
                  'generation_catalog_snapshot',
                  'legacy_reconstructed_catalog')
              OR publication_surface_bindings.status IN (
                  'building',
                  'failed'));

        UPDATE publication_surface_bindings binding
        SET binding_kind = 'legacy_reconstructed_catalog',
            binding_json = jsonb_build_object(
                'table', 'publication_song_catalog',
                'publicationId', snapshot.publication_id,
                'catalogVersion', snapshot.catalog_version,
                'schemaVersion', snapshot.schema_version,
                'sourceKind', snapshot.source_kind,
                'isExact', false,
                'sourceCapturedAt', snapshot.source_captured_at),
            row_count = snapshot.song_count,
            content_hash = snapshot.content_hash,
            status = 'building',
            built_at = snapshot.captured_at
        FROM publication_song_catalog snapshot
        WHERE binding.publication_id = snapshot.publication_id
          AND binding.surface_name = 'song_catalog'
          AND NOT snapshot.is_exact
          AND binding.status NOT IN ('failed', 'retired');

        DO $$
        DECLARE
            current_id BIGINT;
            binding_built_at TIMESTAMPTZ;
            existing_binding_kind TEXT;
            generation_authoritative BOOLEAN;
            legacy_cached_at TIMESTAMPTZ;
            legacy_row_count BIGINT;
            legacy_content_hash TEXT;
            generation_row_count BIGINT;
            generation_content_hash TEXT;
        BEGIN
            SELECT current_publication_id
            INTO current_id
            FROM scrape_publication_state
            WHERE id = TRUE;

            IF current_id IS NULL THEN
                RETURN;
            END IF;

            SELECT
                binding.built_at,
                binding.binding_kind,
                COALESCE(
                    (binding.binding_json ->> 'authoritative')::boolean,
                    FALSE)
            INTO
                binding_built_at,
                existing_binding_kind,
                generation_authoritative
            FROM publication_surface_bindings binding
            WHERE binding.publication_id = current_id
              AND binding.surface_name = 'api_response_cache';

            SELECT
                MAX(cached_at),
                COUNT(*),
                md5(COALESCE(
                    string_agg(
                        cache_key || ':' || etag,
                        '|' ORDER BY cache_key),
                    ''))
            INTO legacy_cached_at, legacy_row_count, legacy_content_hash
            FROM api_response_cache;

            SELECT
                COUNT(*),
                md5(COALESCE(
                    string_agg(
                        cache_key || ':' || etag,
                        '|' ORDER BY cache_key),
                    ''))
            INTO generation_row_count, generation_content_hash
            FROM publication_api_response_cache
            WHERE publication_id = current_id;

            IF (
                   NOT COALESCE(generation_authoritative, FALSE)
                   AND (
                       binding_built_at IS NULL
                       OR existing_binding_kind IN (
                            'inherited_previous_publication',
                            'inherited_generation_cache')
                       OR generation_row_count
                            IS DISTINCT FROM legacy_row_count
                       OR generation_content_hash
                            IS DISTINCT FROM legacy_content_hash
                       OR legacy_cached_at IS NOT NULL
                          AND legacy_cached_at > binding_built_at
                   )
               )
               OR (
                   COALESCE(generation_authoritative, FALSE)
                   AND legacy_cached_at IS NOT NULL
                   AND legacy_cached_at > binding_built_at
               )
            THEN
                DELETE FROM publication_api_response_cache
                WHERE publication_id = current_id;

                INSERT INTO publication_api_response_cache (
                    publication_id, cache_key, json_data, etag, cached_at)
                SELECT
                    current_id,
                    cache_key,
                    json_data,
                    etag,
                    cached_at
                FROM api_response_cache;

                INSERT INTO publication_surface_bindings (
                    publication_id, surface_name, binding_kind, binding_json,
                    row_count, content_hash, status, built_at)
                VALUES (
                    current_id,
                    'api_response_cache',
                    'legacy_current_table_reconciled',
                    jsonb_build_object(
                        'table', 'publication_api_response_cache',
                        'sourceTable', 'api_response_cache',
                        'publicationId', current_id),
                    legacy_row_count,
                    legacy_content_hash,
                    'ready',
                    now())
                ON CONFLICT (publication_id, surface_name) DO UPDATE SET
                    binding_kind = EXCLUDED.binding_kind,
                    binding_json = EXCLUDED.binding_json,
                    row_count = EXCLUDED.row_count,
                    content_hash = EXCLUDED.content_hash,
                    status = EXCLUDED.status,
                    built_at = EXCLUDED.built_at;
            END IF;
        END $$;

        DELETE FROM publication_api_response_cache cache
        USING scrape_publication_state publication
        WHERE publication.id = TRUE
          AND cache.publication_id IS DISTINCT FROM
              publication.current_publication_id
          AND cache.publication_id IS DISTINCT FROM
              publication.previous_publication_id
          AND cache.publication_id IS DISTINCT FROM
              publication.working_publication_id;

        DELETE FROM publication_api_response_cache_staging cache
        USING scrape_publication_state publication
        WHERE publication.id = TRUE
          AND cache.publication_id IS DISTINCT FROM
              publication.current_publication_id
          AND cache.publication_id IS DISTINCT FROM
              publication.previous_publication_id
          AND cache.publication_id IS DISTINCT FROM
              publication.working_publication_id;

        DELETE FROM publication_song_catalog catalog
        USING scrape_publication_state publication
        WHERE publication.id = TRUE
          AND catalog.publication_id IS DISTINCT FROM
              publication.current_publication_id
          AND catalog.publication_id IS DISTINCT FROM
              publication.previous_publication_id
          AND catalog.publication_id IS DISTINCT FROM
              publication.working_publication_id;

        UPDATE publication_surface_bindings binding
        SET binding_kind = 'retired_generation_catalog',
            binding_json = jsonb_build_object(
                'table', 'publication_song_catalog',
                'retired', true),
            row_count = 0,
            content_hash = NULL,
            status = 'retired',
            built_at = now()
        FROM scrape_publication_state publication
        WHERE publication.id = TRUE
          AND binding.surface_name = 'song_catalog'
          AND binding.publication_id IS DISTINCT FROM
              publication.current_publication_id
          AND binding.publication_id IS DISTINCT FROM
              publication.previous_publication_id
          AND binding.publication_id IS DISTINCT FROM
              publication.working_publication_id
          AND binding.status <> 'retired';

        UPDATE publication_surface_bindings binding
        SET binding_kind = 'retired_generation_cache',
            binding_json = jsonb_build_object(
                'table', 'publication_api_response_cache',
                'retired', true),
            row_count = 0,
            content_hash = NULL,
            status = 'retired',
            built_at = now()
        FROM scrape_publication_state publication
        WHERE publication.id = TRUE
          AND binding.surface_name = 'api_response_cache'
          AND binding.publication_id IS DISTINCT FROM
              publication.current_publication_id
          AND binding.publication_id IS DISTINCT FROM
              publication.previous_publication_id
          AND binding.publication_id IS DISTINCT FROM
              publication.working_publication_id
          AND binding.status <> 'retired';
        """;
}
