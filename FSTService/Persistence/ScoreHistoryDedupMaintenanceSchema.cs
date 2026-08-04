namespace FSTService.Persistence;

public static class ScoreHistoryDedupMaintenanceSchema
{
    public const int ContractVersion = 2;
    public const string Purpose = "score_history_null_timestamp_dedup_v1";
    public const string ExecutionSource = "explicit_cli";
    public const string NullSafeReplacementIndexName =
        "ix_sh_dedup_nulls_not_distinct_replacement";
    public const string LegacyReplacementIndexName =
        "ix_sh_dedup_legacy_replacement";
    public const string LegacyIndexDdl = """
        CREATE UNIQUE INDEX ix_sh_dedup
        ON public.score_history
        USING btree (account_id, song_id, instrument, new_score, score_achieved_at);
        """;
    public const string NullSafeIndexDdl = """
        CREATE UNIQUE INDEX ix_sh_dedup
        ON public.score_history
        USING btree (account_id, song_id, instrument, new_score, score_achieved_at)
        NULLS NOT DISTINCT;
        """;
    public const string NullSafeReplacementIndexDdl = """
        CREATE UNIQUE INDEX ix_sh_dedup_nulls_not_distinct_replacement
        ON public.score_history
        USING btree (account_id, song_id, instrument, new_score, score_achieved_at)
        NULLS NOT DISTINCT;
        """;
    public const string LegacyReplacementIndexDdl = """
        CREATE UNIQUE INDEX ix_sh_dedup_legacy_replacement
        ON public.score_history
        USING btree (account_id, song_id, instrument, new_score, score_achieved_at);
        """;

    public const string Sql = """

        -- =====================================================================
        -- SCORE HISTORY NULL-TIMESTAMP DEDUP MAINTENANCE AUDIT
        -- =====================================================================

        CREATE TABLE IF NOT EXISTS score_history_dedup_maintenance_runs (
            maintenance_run_id       BIGSERIAL   PRIMARY KEY,
            maintenance_purpose      TEXT        NOT NULL
                CHECK (maintenance_purpose = 'score_history_null_timestamp_dedup_v1'),
            maintenance_contract_version INTEGER NOT NULL
                CONSTRAINT ck_score_history_dedup_contract_version
                CHECK (maintenance_contract_version IN (1, 2)),
            execution_source         TEXT        NOT NULL
                CHECK (execution_source = 'explicit_cli'),
            dry_run_digest           TEXT        NOT NULL
                CHECK (dry_run_digest ~ '^[0-9a-f]{64}$'),
            canonical_candidate_data TEXT        NOT NULL,
            safety_classification    TEXT        NOT NULL
                CHECK (safety_classification = 'ready'),
            database_name            TEXT        NOT NULL,
            database_user            TEXT        NOT NULL,
            server_version_num       INTEGER     NOT NULL,
            duplicate_row_count      BIGINT      NOT NULL CHECK (duplicate_row_count >= 0),
            duplicate_group_count    BIGINT      NOT NULL CHECK (duplicate_group_count >= 0),
            excess_row_count         BIGINT      NOT NULL CHECK (excess_row_count >= 0),
            affected_account_count   BIGINT      NOT NULL CHECK (affected_account_count >= 0),
            affected_song_count      BIGINT      NOT NULL CHECK (affected_song_count >= 0),
            original_rows_audited    BIGINT      NOT NULL CHECK (original_rows_audited >= 0),
            survivor_rows_updated    BIGINT      NOT NULL CHECK (survivor_rows_updated >= 0),
            rows_deleted             BIGINT      NOT NULL CHECK (rows_deleted >= 0),
            index_replaced           BOOLEAN     NOT NULL,
            index_definition_before  TEXT        NOT NULL,
            index_definition_after   TEXT        NOT NULL,
            rollback_sql             TEXT        NOT NULL,
            executed_at              TIMESTAMPTZ NOT NULL DEFAULT clock_timestamp(),
            CHECK (
                duplicate_row_count =
                    duplicate_group_count + excess_row_count
            ),
            CHECK (original_rows_audited = duplicate_row_count),
            CHECK (survivor_rows_updated = duplicate_group_count),
            CHECK (rows_deleted = excess_row_count)
        );

        DO $contract_version$
        DECLARE
            old_constraint_name TEXT;
        BEGIN
            FOR old_constraint_name IN
                SELECT constraint_row.conname
                FROM pg_constraint constraint_row
                WHERE constraint_row.conrelid =
                        'public.score_history_dedup_maintenance_runs'::regclass
                  AND constraint_row.contype = 'c'
                  AND pg_get_constraintdef(constraint_row.oid, TRUE) =
                        'CHECK (maintenance_contract_version = 1)'
            LOOP
                EXECUTE format(
                    'ALTER TABLE public.score_history_dedup_maintenance_runs ' ||
                    'DROP CONSTRAINT %I',
                    old_constraint_name);
            END LOOP;

            IF NOT EXISTS (
                SELECT 1
                FROM pg_constraint constraint_row
                WHERE constraint_row.conrelid =
                        'public.score_history_dedup_maintenance_runs'::regclass
                  AND constraint_row.conname =
                        'ck_score_history_dedup_contract_version'
            ) THEN
                ALTER TABLE public.score_history_dedup_maintenance_runs
                    ADD CONSTRAINT
                        ck_score_history_dedup_contract_version
                    CHECK (maintenance_contract_version IN (1, 2));
            END IF;
        END
        $contract_version$;

        CREATE INDEX IF NOT EXISTS ix_score_history_dedup_runs_digest
            ON score_history_dedup_maintenance_runs
                (dry_run_digest, maintenance_run_id DESC);

        CREATE TABLE IF NOT EXISTS score_history_dedup_original_rows (
            maintenance_run_id BIGINT      NOT NULL
                REFERENCES score_history_dedup_maintenance_runs(maintenance_run_id)
                ON DELETE RESTRICT,
            original_id        INTEGER     NOT NULL,
            song_id            TEXT        NOT NULL,
            instrument         TEXT        NOT NULL,
            account_id         TEXT        NOT NULL,
            old_score          INTEGER,
            new_score          INTEGER     NOT NULL CHECK (new_score = 0),
            old_rank           INTEGER,
            new_rank           INTEGER,
            accuracy           INTEGER,
            is_full_combo      BOOLEAN,
            stars              INTEGER,
            percentile         REAL,
            season             INTEGER,
            score_achieved_at  TIMESTAMPTZ,
            season_rank        INTEGER,
            all_time_rank      INTEGER,
            difficulty         INTEGER,
            changed_at         TIMESTAMPTZ NOT NULL,
            PRIMARY KEY (maintenance_run_id, original_id)
        );

        CREATE OR REPLACE FUNCTION reject_score_history_dedup_audit_mutation()
        RETURNS trigger
        LANGUAGE plpgsql
        AS $$
        BEGIN
            RAISE EXCEPTION
                'Score-history dedup audit records are immutable.'
                USING ERRCODE = '55000';
        END
        $$;

        CREATE OR REPLACE FUNCTION reject_score_history_dedup_original_append()
        RETURNS trigger
        LANGUAGE plpgsql
        AS $$
        DECLARE
            expected_rows BIGINT;
            current_rows BIGINT;
        BEGIN
            SELECT original_rows_audited
            INTO expected_rows
            FROM score_history_dedup_maintenance_runs
            WHERE maintenance_run_id = NEW.maintenance_run_id;

            SELECT COUNT(*)::BIGINT
            INTO current_rows
            FROM score_history_dedup_original_rows
            WHERE maintenance_run_id = NEW.maintenance_run_id;

            IF expected_rows IS NULL OR current_rows >= expected_rows THEN
                RAISE EXCEPTION
                    'Score-history dedup original-row audit is sealed.'
                    USING ERRCODE = '55000';
            END IF;

            RETURN NEW;
        END
        $$;

        DROP TRIGGER IF EXISTS trg_reject_score_history_dedup_run_mutation
            ON score_history_dedup_maintenance_runs;
        CREATE TRIGGER trg_reject_score_history_dedup_run_mutation
        BEFORE UPDATE OR DELETE OR TRUNCATE
        ON score_history_dedup_maintenance_runs
        FOR EACH STATEMENT
        EXECUTE FUNCTION reject_score_history_dedup_audit_mutation();

        DROP TRIGGER IF EXISTS trg_reject_score_history_dedup_original_append
            ON score_history_dedup_original_rows;
        CREATE TRIGGER trg_reject_score_history_dedup_original_append
        BEFORE INSERT
        ON score_history_dedup_original_rows
        FOR EACH ROW
        EXECUTE FUNCTION reject_score_history_dedup_original_append();

        DROP TRIGGER IF EXISTS trg_reject_score_history_dedup_original_mutation
            ON score_history_dedup_original_rows;
        CREATE TRIGGER trg_reject_score_history_dedup_original_mutation
        BEFORE UPDATE OR DELETE OR TRUNCATE
        ON score_history_dedup_original_rows
        FOR EACH STATEMENT
        EXECUTE FUNCTION reject_score_history_dedup_audit_mutation();
        """;
}
