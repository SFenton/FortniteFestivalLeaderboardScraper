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
containers, scrape state, or scrape evidence.

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
- takes a transaction advisory lock and
  `LOCK TABLE score_history IN SHARE ROW EXCLUSIVE MODE`;
- uses `lock_timeout = 3s` and `statement_timeout = 180s`;
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
3. Initialize the additive audit schema with the release's normal explicit
   schema-only step. The maintenance command never initializes schema.
4. Before execute, capture Docker/Postgres health, public-read freeze state,
   published/active scrape, locks/long queries, disk, CPU, and memory.
5. Do not execute during an active scrape/write phase. Use a clean maintenance
   boundary with the required live-scrape parity and restore evidence.
6. Run two dry runs against unchanged data and require identical digests and
   accepted classifications.

## Commands

Additive schema only:

```bash
docker compose run --rm --no-deps --entrypoint dotnet fstservice \
  FSTService.dll --initialize-schema-only
```

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
3. drops the `NULLS NOT DISTINCT` index;
4. removes the merged survivor rows;
5. restores every audited original row with its original ID and values;
6. verifies exact row equality;
7. recreates the legacy ordinary unique `ix_sh_dedup`;
8. advances `score_history_id_seq` without rewinding it.

Rollback fails closed if later writes changed a survivor or audit evidence is
incomplete. The audit tables remain immutable after rollback. Run a new dry
run and require the original digest before considering the restore complete.
