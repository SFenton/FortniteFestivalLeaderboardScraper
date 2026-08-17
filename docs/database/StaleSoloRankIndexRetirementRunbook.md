---
status: living-runbook
owner: operations
last_verified: 2026-08-17
last_verified_commit: bd11b749
sources:
  - tools/postgres-retire-ix-le-song-rank.sh
  - tools/postgres-retire-ix-le-song-rank.py
  - tools/postgres-retire-ix-le-song-rank.test.py
  - FSTService/Persistence/DatabaseInitializer.cs
  - FSTService/Persistence/GlobalLeaderboardPersistence.cs
  - FSTService/Persistence/Maintenance/DatabaseMaintenanceDryRunReporter.cs
  - https://www.postgresql.org/docs/17/sql-dropindex.html
  - https://www.postgresql.org/docs/17/sql-alterindex.html
  - https://www.postgresql.org/docs/17/sql-createindex.html
update_triggers:
  - The ix_le_song_rank catalog, production identity, worker/publication guards, drop mechanics, rollback, or capacity evidence changes.
---

# Stale solo rank index retirement

## Status and scope

This package owns only `public.ix_le_song_rank` and its nine former attached
partition indexes. It cannot accept another index name.

The family was retired successfully on 2026-08-17. The current production
catalog contains none of the ten exact names, and check mode returns
`already_absent` without DDL.

The accepted pre-execution package observed:

- one partitioned parent and nine attached leaves;
- `5,147,222,016` catalog-measured bytes;
- zero `idx_scan` and null `last_idx_scan` on every member;
- no primary, unique, exclusion, or other constraint ownership;
- only the expected automatic table-column and partition-attachment
  dependencies;
- publication `80` / scrape `1302`, idle and unfrozen;
- an offline worker, zero worker/maintenance backends, zero target locks, and
  zero waiting locks.

Zero scans are an observation, not proof of lifetime nonuse. The evidence
records `pg_stat_database.stats_reset`, `pg_postmaster_start_time()`, every
per-index counter, and the caveat that a reset, crash, or immediate shutdown
can shorten retained statistics history.

## Executed result

- PR `#53` merged as `bd11b74958191e26e2b06297d842a600cd4ed7bb`.
- Official service/held-worker image:
  `sha256:11e4735cc23a308ac7f7e050cb5716a7c283c0b0588e801ab2bcd56e7108bed3`.
- Guarded command wall time: `0.291500732` seconds.
- Catalog bytes removed: `5,147,222,016`.
- Immediate filesystem free-byte increase: `5,147,246,592`.
- PostgreSQL database-size decrease: `5,147,287,552`; the additional
  `65,536` bytes were table auxiliary-size movement, while every unrelated
  index, constraint, table heap identity, and representative score-index plan
  remained exact.
- WAL delta: `368,151` bytes; cumulative temp-byte delta: `0`.
- The 90-second public monitor recorded `324` samples with zero HTTP failures.
  Songs p95 was `8.402 ms`; the maximum sampled songs latency was `32.009 ms`.
- Publication `80` / scrape `1302` remained authoritative and unfrozen.
  Direct/web songs, rankings, path, leaderboard, and player payloads remained
  byte-identical where paired.
- Capacity check returned `accepted_with_capacity_alert`: free bytes
  `64,785,661,952`, one-scrape surplus `4,392,662,149`, preferred two-window
  shortfall `56,000,337,654`, and projected headroom `0.54` days.
- `fstworker` remains `Created`/offline under the reacquired capacity hold.
- Rollback was preserved with SHA-256
  `e588ea1c51355bdc3a5cf405276e9449060bac0bc4019417a5e96c0902537bd7`
  and was not executed.

Checksummed execution evidence:

```text
/mnt/docker-storage/Docker/FestivalServiceTracker/fst-data/evidence/ix-le-song-rank-execution-20260817T074541Z/
```

## Why current roles do not own the index

`DatabaseInitializer` does not create the family.
`GlobalLeaderboardPersistence.SoloIndexDefinitions` does not recreate it, and
the retirement candidate removes it from `SoloDroppableIndexes`.

The API role uses published-scope/current-projection reads rather than legacy
`leaderboard_entries` rank ordering. Production disables legacy live scrape
writes. Remaining legacy rank predicates are correctness-preserving fallback
or supplemental code paths and have no explicit index dependency; their
planner use remains governed by current indexes and ordinary sorting.

The exact check remains available for idempotent absence proof. Execute mode
still refuses any partial family, active query, or lock.

## Read-only package

Create a new evidence directory on the 4 TB FST drive:

```bash
tools/postgres-retire-ix-le-song-rank.sh \
  --check \
  --output /mnt/docker-storage/Docker/FestivalServiceTracker/fst-data/evidence/ix-le-song-rank-check-<UTC>
```

The command validates:

1. production Compose directory, observed shared worker start/recreate host
   lock state, project labels, PostgreSQL container/image, PGDATA bind mount,
   and PostgreSQL 17 system identifier;
2. healthy PostgreSQL, service, and web containers;
3. offline worker container and durable worker state;
4. idle/unfrozen publication with no working publication;
5. no running scrape/phase attempt, waiting lock, worker/maintenance backend,
   target relation lock, or matching active query;
6. exact names, OIDs, table OIDs, definitions, attachment map, validity flags,
   dependencies, constraints, bytes, and zero-use counters.

Check mode may report the shared worker guard lock as `externally_held` while
an approved capacity hold owns it. Execute mode must acquire that lock itself
and fails nonblockingly while another owner remains. Coordinate an explicit
lock handoff at the reviewed maintenance boundary; never terminate or bypass
an unknown holder.

It writes:

| Artifact | Purpose |
|---|---|
| `probe.json` | Sanitized project, cluster, publication, runtime, and catalog evidence |
| `zero-use-observation.json` | Dated per-index scan and statistics-window evidence |
| `manifest.json` | Immutable execution identity, exact OIDs/definitions/bytes, and artifact hashes |
| `drop-plan.sql` | Exact fail-closed transaction that execute mode would submit |
| `rollback.sql` | Exact parent/leaf recreation and attachment sequence |
| `report.json` | Outcome and byte totals |
| `SHA256SUMS` | Package integrity |

## Drop mechanics

PostgreSQL 17 explicitly rejects `DROP INDEX CONCURRENTLY` for a partitioned
index. An attached leaf also cannot be dropped independently. The supported
least-disruptive operation is therefore one normal:

```sql
DROP INDEX public.ix_le_song_rank;
```

Dropping the parent automatically drops its nine attached leaves. The package
uses no `CASCADE`.

Execute mode wraps that statement in one transaction with:

- the same nonblocking host lock used by guarded worker starts, acquired by
  execute mode and held across probe, drop, and validation;
- a `2s` lock timeout;
- a `30s` statement and idle-transaction timeout;
- a dedicated retirement advisory lock;
- a shared publication advisory lock, which allows ordinary shared public
  readers but excludes a publication commit;
- the exclusive registration mutation gate;
- a second exact catalog/publication/activity validation immediately before
  the drop.

A normal drop requires PostgreSQL's exclusive relation locks. If any reader,
writer, autovacuum, or other backend conflicts beyond two seconds, the
transaction fails and the complete family remains. Do not lengthen the timeout
to force progress; choose a quieter reviewed window.

## Reviewed execute command

Execute only from a reviewed check package and a new output directory:

```bash
tools/postgres-retire-ix-le-song-rank.sh \
  --execute \
  --output /mnt/docker-storage/Docker/FestivalServiceTracker/fst-data/evidence/ix-le-song-rank-execute-<UTC> \
  --manifest <check-package>/manifest.json \
  --zero-use-observation <check-package>/zero-use-observation.json \
  --rollback-file <check-package>/rollback.sql \
  --expected-manifest-sha256 <sha256> \
  --expected-zero-use-sha256 <sha256> \
  --expected-rollback-sha256 <sha256>
```

Execute mode rejects a changed Compose project/container/PGDATA mount, cluster
restart or system identifier, publication pointer, OID, name, definition,
attachment, dependency, constraint, byte count, scan counter, active query,
lock, worker, maintenance backend, or artifact digest.

An exact already-absent family is idempotent and performs no DDL. A partially
present family is never success-shaped.

## Post-drop validation

Success requires:

- all ten exact index relations absent;
- catalog bytes reduced from the manifest total to zero;
- publication and cluster identity unchanged;
- public reads still unfrozen and `currentUpdate` idle;
- worker still offline;
- zero waiting locks and worker/maintenance backends;
- healthy service, web, and PostgreSQL;
- representative direct and web API parity;
- measured FST filesystem free bytes before and after.

The catalog removal is exact. The host free-space delta is reported
independently because concurrent filesystem activity can obscure a byte-for-
byte `df` delta.

The completed reclaim crossed the single-scrape floor by about `4.39 GB` but
remains `56.00 GB` below the preferred `120,785,999,606`-byte two-window
headroom. The capacity guard allows a scrape with an alert, but the operator
keeps the worker held for the preferred-window policy.

## Rollback

Rollback is retained but was not needed. The generated script:

1. creates the empty partitioned parent with `ON ONLY`;
2. builds all nine exact leaf indexes with `CREATE INDEX CONCURRENTLY`;
3. attaches the equivalent leaves to the parent in canonical instrument
   order.

Leaf recreation is a heavy scan/build and can consume time, I/O, WAL, and
temporary headroom comparable to the retired bytes. Review capacity and public
load separately before rollback. Run the check command afterward and do not
start the worker until the complete 10-member attachment map is valid.

## Validation

Repository validation:

```bash
bash -n tools/postgres-retire-ix-le-song-rank.sh
PYTHONDONTWRITEBYTECODE=1 \
  python3 tools/postgres-retire-ix-le-song-rank.test.py
dotnet test FSTService.Tests/FSTService.Tests.csproj -c Release \
  --filter FullyQualifiedName~DatabaseMaintenanceDryRunReporterTests
node tools/check-docs.mjs
```

The isolated PostgreSQL 17 mechanics test must prove that concurrent parent
drop is rejected, normal parent drop removes all ten catalog objects, and the
generated rollback restores one parent plus nine attached leaves.
