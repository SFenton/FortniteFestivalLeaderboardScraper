# Logical Leaderboard Shadow Retirement Runbook

## Current decision

**Tier:** accepted readiness, blocked destructive action.

The logical leaderboard shadow is disabled and non-authoritative, but the
repository's destructive live-scrape A/B gate is not yet satisfied. Scrapes
`1261`, `1262`, and `1263` completed all `8,208` manifests with logical writes
disabled, zero writer failures, and zero publication-critical failures. Each
failed on capacity before global publication. SNAPSHOT-REUSE candidate `1264`
then completed `8,232/8,232` manifests with logical writes disabled and zero
writer failures, but its post-writer capacity guard failed before rankings or
publication. Do not truncate until one disabled-writer scrape completes
post-process, publishes, unfreezes, and passes the full public parity suite.

Evidence:
`/mnt/docker-storage/Docker/FestivalServiceTracker/fst-data/evidence/logical-retire-20260725T2306Z`.

SNAPSHOT-REUSE preflight on 2026-07-26 did not clear this prerequisite. Its
capacity, source-integrity, public-health, and proxy gates passed, but Epic
refresh returned `invalid_refresh_token` before any candidate deploy or worker
start. No scrape ID was allocated and no publication occurred. Evidence:
`/mnt/docker-storage/Docker/FestivalServiceTracker/fst-data/evidence/snapshot-reuse-20260726T010701Z`.

The resumed live A/B authenticated successfully and ran scrape `1264`, but the
strict post-process guard blocked at `32,390,148,096` free bytes. The scrape was
reconciled failed at `capacity_postwriter_guard`, published `1236` remained
unfrozen, and `1264` owns zero published-source rows. Logical current/version
tables had zero scrape-`1264` writes. The gate remains `NOT_CLEARED`; hashed
evidence is:
`/mnt/docker-storage/Docker/FestivalServiceTracker/fst-data/evidence/snapshot-reuse-live-ab-20260726T032124Z/parity/logical-shadow-retirement-live-gate.json`
(`75dc6c9ad8348199f447f9f4e549bb2b633c7e43f68338ea218fed3127e568b9`).

The capacity-ready retry ran scrape `1265` with logical writers still disabled.
It completed `8,232/8,232` manifests, zero writer failures, and four
publication-critical phases, but the safety monitor stopped ranking snapshots
at `13,144,125,440` free bytes before global publication. Exact before/after
fingerprints remained unchanged for all `39,820,273` current rows and
`194,171,215` version rows. The gate remains `NOT_CLEARED`; no truncate ran.
Latest hashed evidence:
`/mnt/docker-storage/Docker/FestivalServiceTracker/fst-data/evidence/snapshot-reuse-live-ab-20260726T110731Z/parity/logical-shadow-retirement-live-gate.json`
(`35723055c9439e2d75b4ba06e630d8c5bfc4a89aaa70c9ecced1e6fff3b4bc2f`).

## Exact scope

| Family | Parent | Leaf partitions | Rows | Bytes | Restore class |
|---|---|---:|---:|---:|---|
| Logical current | `public.leaderboard_current_entries` | 9 | 39,820,273 | 33,480,859,648 | Rebuild semantic current from published physical snapshots |
| Logical versions | `public.leaderboard_entry_versions` | 9 | 194,171,215 | 107,982,077,952 | Experimental chronology; intentionally discardable |
| Metrics | `public.leaderboard_logical_write_metrics` | none | 108 | 106,496 | Retain |

Each target family has `bass`, `default`, `drums`, `guitar`, `solo_bass`,
`solo_drums`, `solo_guitar`, `solo_vocals`, and `vocals` leaf partitions. The
`default` partition owns the five peripheral instrument values. Schema,
constraints, partitions, and indexes remain after `TRUNCATE`.

## Preconditions

1. Use runtime `gpt-5.6-sol`, reasoning `max`, context `long_context`.
2. Keep all evidence and scratch on `/mnt/docker-storage`.
3. Hold `fstworker`; verify no worker scrape/post-process/rank/publication query.
4. Require healthy Postgres, `fstservice`, `festivalweb`, `/readyz`, web shell,
   and `/api/service-info`.
5. Require published reads unfrozen and a named published scrape.
6. Run the measured reclaim capacity guard with zero transient/scratch bytes.
7. Capture locks, long queries, vacuum/index/rewrite progress, exact target
   sizes/counts/fingerprints, metrics count, and public fingerprints.
8. Confirm production
   `Features__WriteLogicalLeaderboardVersions=false`.
9. Require one complete disabled-writer scrape to have published with exact
   route/export/ranking/history/publication parity. Pre-publication manifest
   completion alone is insufficient.

## Future execution

Use a dedicated psql session and stop on any error:

```sql
BEGIN;
SET LOCAL lock_timeout = '5s';
SET LOCAL statement_timeout = '5min';
TRUNCATE TABLE
    public.leaderboard_current_entries,
    public.leaderboard_entry_versions;
COMMIT;
```

Do not add `CASCADE`. Do not include
`public.leaderboard_logical_write_metrics`. Monitor the full public path,
Postgres resources, locks, and free bytes every 60 seconds through the action
and at least 60 seconds afterward.

## Validation

- Both target parents and every leaf partition contain zero rows.
- Metrics remain `108` rows unless later normal metadata retention changes
  them explicitly.
- Constraints and indexes remain valid.
- No new target query, ungranted lock, vacuum, index build, or rewrite remains.
- Mapped leaderboard remains byte-exact HTTP `200`.
- Export, ranking, history, composite/band, and band-song routes retain their
  expected published or fail-closed fingerprints.
- Filesystem free-byte gain is reconciled against
  `141,462,937,600` candidate bytes; record database and filesystem deltas.
- Capacity guard and full service/web/database health pass afterward.

## Rollback and rebuild

A transaction rollback is the only exact rollback before commit. After commit:

- Rebuild `leaderboard_current_entries` from the current published physical map
  using `rebuild-current-from-published.sql` in the evidence root. The script
  fails closed unless the caller explicitly sets
  `fst.logical_shadow_rebuild=approved`, requires the target to be empty, and
  verifies the rebuilt count.
- Rebuilt current rows preserve semantic leaderboard fields and recompute the
  original writer fingerprint. Logical first/change/seen metadata restarts at
  the published baseline.
- Do not claim to restore the discarded `1223`-`1237` version chronology.
  `seed-versions-from-rebuilt-current.sql` may create one new open baseline per
  current row only as part of a future versioned migration/promotion.
- Preserve `logical-shadow-schema.sql`, exact manifests, canonical
  fingerprints, and deterministic current/version samples. Do not create a
  full same-drive duplicate.

## Fail-closed configuration

`WriteLogicalLeaderboardVersions` defaults false in code and tracked
configuration. `FeatureOptionsValidator` rejects true at startup. No
`UseLogicalLeaderboardVersions` read flag exists. Future writer or reader
enablement requires a code/config change, versioned migration, rebuild/restore
validation, tests, and a new full live-scrape promotion.
