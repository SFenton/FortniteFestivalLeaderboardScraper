# Score-History NULL Dedup Maintenance Runbook

## Status and scope

This workflow repairs only duplicate `score_history` rows under:

```text
(account_id, song_id, instrument, new_score, score_achieved_at)
```

It is an explicit one-shot maintenance command. It is never invoked by normal
service or worker startup. Schema initialization adds only immutable audit
tables; row merging, deletion, and `ix_sh_dedup` replacement occur only in an
operator-requested execute transaction.

The 2026-08-02 read-only inventory established:

- `705,687` total rows / about `441 MB`;
- `1,631` rows with `score_achieved_at IS NULL`;
- `324` duplicate groups with `1,074` excess rows;
- every duplicate group has `new_score = 0`;
- only `id`, `new_rank`, `all_time_rank`, and `changed_at` vary.

This evidence is planning input, not execution authorization. The code was
implemented while scrape `1274` ran without querying or changing production,
containers, scrape state, or scrape evidence. Continuous-safe readiness repairs
were completed in repository code/tests while scrape `1277` was
post-processing, again without live database, container, or maintenance
activity.

## Safety contract

Dry run:

- opens a PostgreSQL `REPEATABLE READ`, `READ ONLY` transaction;
- uses `lock_timeout = 2s` and `statement_timeout = 120s`;
- reports exact total/null/duplicate/group/excess counts, affected accounts
  and songs, per-group rank/ID/time maxima, semantic variance, relation/index
  sizes, index definition, and selected merge values;
- exits with code `2` after emitting the JSON report when classification or
  index invariants block execute;
- computes canonical SHA-256 from sorted source rows, selected merge results,
  merge contract, and structured index state;
- excludes transaction/report clocks, planner row estimates, and relation
  sizes from the digest.

Execute:

- requires both `--score-history-dedup-execute` and
  `--expected-score-history-dedup-digest <sha256>`;
- configures the transaction with `SET LOCAL`, then acquires
  `LOCK TABLE score_history IN SHARE ROW EXCLUSIVE MODE` before any
  snapshot-establishing `SELECT`;
- takes the transaction advisory lock only after the table lock, so a writer
  that commits after transaction start but before lock acquisition is included
  in the locked analysis and must match the supplied digest;
- uses `lock_timeout = 3s` and `statement_timeout = 180s`;
- requires the release-owned audit tables, columns, constraints, functions,
  enabled immutable triggers, digest index, and run-ID sequence to match the
  exact catalog contract; it never creates or repairs them;
- re-reads and re-hashes under that lock before reserving an audit run ID;
- rejects any group whose `new_score` is not exactly zero or whose
  `old_score`, `old_rank`, `accuracy`, `is_full_combo`, `stars`, `percentile`,
  `season`, `season_rank`, or `difficulty` varies;
- audits every original affected row before updating or deleting anything;
- updates the lowest-ID survivor, preserving the earliest `changed_at` and the
  minimum positive rank, falling back to the minimum non-null rank;
- deletes only audited non-survivors;
- replaces the ordinary five-column unique `ix_sh_dedup` with PostgreSQL 17
  `UNIQUE ... NULLS NOT DISTINCT` in the same transaction.

The replacement index is built under a temporary name before the old index is
dropped. Reads remain available during the scan/build and pause only for the
final drop/rename cutover held until commit. `score_history` inserts, updates,
and deletes wait behind the maintenance transaction; `lock_timeout = 3s`
limits the maintenance command's lock acquisition, not other sessions' waits.
Run with the writer and API service stopped or otherwise quiesced so the final
read pause is bounded and no user request waits behind the cutover.

## Prerequisites

1. Use production compose ownership at
   `/home/sfenton/Docker/FestivalServiceTracker`.
2. Keep all reports and rollback files on the 4 TB FST filesystem.
3. Do **not** use `--initialize-schema-only` as maintenance preparation. That
   command owns the entire unbounded main/publication schema and advances
   sequences. Production already has the exact audit schema; run the bounded,
   read-only catalog preflight below. If it fails, stop. The normal release
   schema initialization path remains the sole owner of schema repair.
4. Before execute, capture Docker/Postgres health, public-read freeze state,
   published/active scrape, locks/long queries, disk, CPU, and memory.
5. Do not execute during an active scrape/write phase. Use a clean maintenance
   boundary with the required live-scrape parity and restore evidence.
6. Run two dry runs against unchanged data and require identical digests and
   accepted classifications.

## Commands

Bounded audit-schema catalog preflight (all returned values must be `true`):

```sql
WITH audit_tables(table_name) AS (
    VALUES
        ('score_history_dedup_maintenance_runs'::TEXT),
        ('score_history_dedup_original_rows'::TEXT)
),
column_state AS (
    SELECT
        relation.relname AS table_name,
        ARRAY_AGG(attribute.attname ORDER BY attribute.attnum) AS columns
    FROM pg_class relation
    JOIN pg_namespace relation_namespace
      ON relation_namespace.oid = relation.relnamespace
    JOIN pg_attribute attribute
      ON attribute.attrelid = relation.oid
     AND attribute.attnum > 0
     AND NOT attribute.attisdropped
    WHERE relation_namespace.nspname = 'public'
      AND relation.relname IN (SELECT table_name FROM audit_tables)
    GROUP BY relation.relname
),
constraint_state AS (
    SELECT
        relation.relname AS table_name,
        COUNT(*) AS constraint_count,
        BOOL_AND(constraint_row.convalidated) AS all_validated
    FROM pg_constraint constraint_row
    JOIN pg_class relation
      ON relation.oid = constraint_row.conrelid
    JOIN pg_namespace relation_namespace
      ON relation_namespace.oid = relation.relnamespace
    WHERE relation_namespace.nspname = 'public'
      AND relation.relname IN (SELECT table_name FROM audit_tables)
    GROUP BY relation.relname
),
trigger_state AS (
    SELECT
        COUNT(*) = 3
            AND BOOL_AND(
                trigger_row.tgenabled = 'O'
                AND trigger_row.tgqual IS NULL
                AND trigger_row.tgattr = ''::int2vector)
            AND ARRAY_AGG(trigger_row.tgname ORDER BY trigger_row.tgname) =
                ARRAY[
                    'trg_reject_score_history_dedup_original_append',
                    'trg_reject_score_history_dedup_original_mutation',
                    'trg_reject_score_history_dedup_run_mutation'
                ]::NAME[] AS exact
    FROM pg_trigger trigger_row
    JOIN pg_class relation
      ON relation.oid = trigger_row.tgrelid
    JOIN pg_namespace relation_namespace
      ON relation_namespace.oid = relation.relnamespace
    WHERE relation_namespace.nspname = 'public'
      AND relation.relname IN (SELECT table_name FROM audit_tables)
      AND NOT trigger_row.tgisinternal
),
function_state AS (
    SELECT
        COUNT(*) = 2
            AND ARRAY_AGG(procedure_row.proname ORDER BY procedure_row.proname) =
                ARRAY[
                    'reject_score_history_dedup_audit_mutation',
                    'reject_score_history_dedup_original_append'
                ]::NAME[] AS exact
    FROM pg_proc procedure_row
    JOIN pg_namespace procedure_namespace
      ON procedure_namespace.oid = procedure_row.pronamespace
    WHERE procedure_namespace.nspname = 'public'
      AND procedure_row.proname IN (
          'reject_score_history_dedup_audit_mutation',
          'reject_score_history_dedup_original_append')
),
index_state AS (
    SELECT
        COUNT(*) = 1
            AND BOOL_AND(
                NOT index_row.indisunique
                AND index_row.indisvalid
                AND index_row.indisready
                AND index_row.indnkeyatts = 2
                AND index_row.indnatts = 2
                AND index_row.indpred IS NULL
                AND index_row.indexprs IS NULL) AS exact
    FROM pg_class index_relation
    JOIN pg_namespace index_namespace
      ON index_namespace.oid = index_relation.relnamespace
    JOIN pg_index index_row
      ON index_row.indexrelid = index_relation.oid
    WHERE index_namespace.nspname = 'public'
      AND index_relation.relname = 'ix_score_history_dedup_runs_digest'
)
SELECT
    to_regclass('public.score_history_dedup_maintenance_runs')
        IS NOT NULL AS runs_table_present,
    to_regclass('public.score_history_dedup_original_rows')
        IS NOT NULL AS originals_table_present,
    COALESCE((
        SELECT CARDINALITY(columns) = 23
        FROM column_state
        WHERE table_name = 'score_history_dedup_maintenance_runs'
    ), FALSE) AS runs_columns_bounded,
    COALESCE((
        SELECT CARDINALITY(columns) = 19
        FROM column_state
        WHERE table_name = 'score_history_dedup_original_rows'
    ), FALSE) AS originals_columns_bounded,
    COALESCE((
        SELECT constraint_count = 18 AND all_validated
        FROM constraint_state
        WHERE table_name = 'score_history_dedup_maintenance_runs'
    ), FALSE) AS runs_constraints_bounded,
    COALESCE((
        SELECT constraint_count = 3 AND all_validated
        FROM constraint_state
        WHERE table_name = 'score_history_dedup_original_rows'
    ), FALSE) AS originals_constraints_bounded,
    COALESCE((SELECT exact FROM trigger_state), FALSE)
        AS immutable_triggers_bounded,
    COALESCE((SELECT exact FROM function_state), FALSE)
        AS trigger_functions_bounded,
    COALESCE((SELECT exact FROM index_state), FALSE)
        AS digest_index_bounded,
    CASE
        WHEN to_regclass(
            'public.score_history_dedup_maintenance_runs') IS NULL
        THEN FALSE
        ELSE pg_get_serial_sequence(
            'public.score_history_dedup_maintenance_runs',
            'maintenance_run_id') =
            'public.score_history_dedup_maintenance_runs_maintenance_run_id_seq'
    END AS run_sequence_bounded;
```

This query is an operator-visible early check. The maintenance binary performs
the deeper exact column/default, constraint-definition, function-body,
trigger-shape, index-opclass/order, and sequence contract validation and fails
closed on any difference.

Dry run (default):

```bash
docker compose run --rm --no-deps --entrypoint dotnet fstservice \
  FSTService.dll --score-history-dedup-maintenance
```

Execute only after two matching dry runs and the maintenance gate:

```bash
docker compose run --rm --no-deps --entrypoint dotnet fstservice \
  FSTService.dll \
  --score-history-dedup-maintenance \
  --score-history-dedup-execute \
  --expected-score-history-dedup-digest <sha256>
```

Supplying the digest without the execute flag, or execute without the digest,
is rejected.

## Expected lock and runtime

For the measured `705,687`-row / `441 MB` relation, plan for one short
write-blocking transaction:

| Step | Lock/load | Planning estimate |
|---|---|---:|
| Re-read and audit insert | `SHARE ROW EXCLUSIVE`; sequential/bounded scans | 1-20 seconds |
| Merge/delete `1,074` excess rows | Same transaction lock | under 10 seconds |
| Build replacement `ix_sh_dedup` | Temporary name; reads allowed, writes blocked | 10-120 seconds |
| Final drop/rename | Brief `ACCESS EXCLUSIVE` read/write pause until commit | seconds |
| Total | Three-second maintenance lock acquisition; each statement capped at 180 seconds | about 15-150 seconds |

These are size-based planning estimates, not measured production timings.
Abort and reschedule if lock acquisition times out, health degrades, or the
index build approaches its statement timeout.

## Audit and validation

`score_history_dedup_maintenance_runs` stores non-null purpose, contract
version, CLI source, digest, canonical candidate data, database/user/server
identity, exact counts, before/after index DDL, execution time, and executable
rollback SQL.

`score_history_dedup_original_rows` stores every original affected column and
row ID. Both tables reject `UPDATE`, `DELETE`, and `TRUNCATE`; the original-row
set also rejects inserts after its declared count is complete. They have no
scrape-log or retention coupling.

After execute, validate:

```sql
SELECT maintenance_run_id, dry_run_digest, original_rows_audited,
       duplicate_group_count, rows_deleted, index_replaced
FROM score_history_dedup_maintenance_runs
ORDER BY maintenance_run_id DESC
LIMIT 1;

SELECT i.indisunique, i.indisvalid, i.indnullsnotdistinct,
       pg_get_indexdef(i.indexrelid)
FROM pg_index i
WHERE i.indexrelid = 'public.ix_sh_dedup'::regclass;

SELECT COUNT(*)
FROM (
  SELECT 1
  FROM score_history
  GROUP BY account_id, song_id, instrument, new_score, score_achieved_at
  HAVING COUNT(*) > 1
) duplicate_groups;
```

The final duplicate-group count must be zero and the index must be unique,
valid, and `indnullsnotdistinct = true`. Re-running execute with the original
digest returns the prior immutable audit run without another write.

## Rollback

The exact rollback SQL is stored per run:

```sql
SELECT rollback_sql
FROM score_history_dedup_maintenance_runs
WHERE maintenance_run_id = <run-id>;
```

Save and execute that text only in a gated maintenance window. It:

1. takes the same short timeouts and write-blocking table lock;
2. verifies the immutable audit count, digest, target index, and current merged
   survivors;
3. proves every audited non-survivor ID is still absent, refusing rollback
   before any delete if a later explicit-ID row reused one;
4. drops the `NULLS NOT DISTINCT` index;
5. removes only the verified merged survivor rows;
6. restores every audited original row with its original ID and values;
7. verifies exact row equality;
8. recreates the legacy ordinary unique `ix_sh_dedup`;
9. advances `score_history_id_seq` without rewinding it.

Rollback fails closed if later writes changed a survivor, reused an audited
non-survivor ID, or made audit evidence incomplete. Unrelated later rows remain
untouched. The audit tables remain immutable after rollback. Run a new dry run
and require the original digest before considering the restore complete; a
subsequent execute creates a new immutable run rather than treating the rolled
back run as currently applied.
