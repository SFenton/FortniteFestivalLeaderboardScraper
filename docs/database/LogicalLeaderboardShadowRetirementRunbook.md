# Logical Leaderboard Shadow Retirement Runbook

## Current decision

**Tier:** destructive reclaim accepted and executed on 2026-07-28.

Scrape `1267` satisfied the repository's destructive live-scrape A/B gate with
logical writes disabled. It completed all `8,232/8,232` solo and band
manifests, zero writer failures, all 10 publication-critical phases, atomic
publication, and public-read unfreeze. The new publication owns `6,174`
complete solo scope mappings: `6,132` snapshot scopes owning `39,937,029`
physical rows from scrape/snapshot `1267`, plus 42 explicit-empty mappings
with no physical snapshot.

Two post-publish captures each returned HTTP `200` for all 13 normalized
leaderboard, export, player, ranking, history, composite, band, and band-song
fingerprints; the captures were `13/13` byte-exact. Full logical fingerprints
remained byte-identical for all `39,820,273` current rows and `194,171,215`
version rows. Scrape `1267` touched zero logical rows, emitted zero logical
write metrics, and produced no positive logical-table read counter delta.

LOGICAL-RETIRE independently reverified that gate, refreshed the exact live
manifest, completed a rollback-only rehearsal, and then transactionally
truncated only `public.leaderboard_current_entries` and
`public.leaderboard_entry_versions` without `CASCADE`. Their 18 leaf
partitions are empty; all 20 primary-key constraints and indexes remain valid.
`public.leaderboard_logical_write_metrics` remains intact at 108 rows.

The production transaction reclaimed `123,173,593,088` database bytes. Stable
filesystem free space increased from `41,158,270,976` to approximately
`164,328,067,072` bytes, a measured gain of `123,169,796,096` bytes after
evidence writes. Immediate and 60-second post-action captures were each
HTTP `200` and `13/13` byte-exact against the pre-action public fingerprints.
Published scrape `1267` remained unfrozen; Postgres, `fstservice`, and
`festivalweb` remained healthy; `fstworker` remained held/offline.

Execution evidence:
`/mnt/docker-storage/Docker/FestivalServiceTracker/fst-data/evidence/logical-retire-executed-20260728T092804Z`.

Hashed clearance evidence:
`/mnt/docker-storage/Docker/FestivalServiceTracker/fst-data/evidence/scrape-1267-guarded-publication-20260727T201218Z/parity/logical-shadow-retirement-live-gate.json`
(`95c55fb66bb33f07eccbfe01b45957ab6ad96439c2a96f41a16dd8a0519e2ae7`).

SCRAPE-1267 evidence root:
`/mnt/docker-storage/Docker/FestivalServiceTracker/fst-data/evidence/scrape-1267-guarded-publication-20260727T201218Z`.

Original readiness evidence:
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

POST-1265-LOW-SCRATCH did not weaken that table-data gate. It removed only the
four non-constraint secondary index trees from the already retired,
startup-rejected logical shadow. The 36 child indexes reclaimed
`18,289,049,600` database bytes; all `39,820,273` current rows,
`194,171,215` version rows, 20 primary-key constraints, table heaps, and exact
sample fingerprints remained intact. Immediate free space reached
`67,148,181,504` bytes, and the corrected `60,392,999,803`-byte start guard
passed with about `6.75 GB` of margin. Evidence:
`/mnt/docker-storage/Docker/FestivalServiceTracker/fst-data/evidence/post-scrape-1265-capacity-recovery-20260727T0011Z`.

The prior proxy-retuned disabled-writer baseline ran scrape `1266`. It completed
`8,232/8,232` manifests and every recorded publication-critical phase with zero
writer failures, but it exited during unbounded deferred registration/rivals
processing before global publication. Recovery marked `1266` failed and
confirmed that it owns zero published-source rows; published `1236` remains
unfrozen.

Logical current/version tables had zero scrape-`1266` touches or metric rows.
Full before/after fingerprints are byte-identical for all `39,820,273` current
rows and `194,171,215` version rows:

- current fingerprint file:
  `054b9bbeb52d6670b4adee9fc7afcc101977132a20cecaf14fcc30690a69f3f2`;
- version fingerprint file:
  `c9ab56adc1a983c62be0e3cc5dbe480ef6b6993a41de601638197cb394424313`.

That observation left the gate `NOT_CLEARED_NO_PUBLICATION`; no truncate ran.
Scrape `1267` subsequently cleared the gate as recorded above. Immediately
before LOGICAL-RETIRE execution, the retained leaf tables occupied
`123,173,888,000` bytes after the already accepted secondary-index retirement.
Hashed evidence:
`/mnt/docker-storage/Docker/FestivalServiceTracker/fst-data/evidence/proxy-retune-disabled-writer-baseline-20260727T004228Z/parity/logical-shadow-retirement-live-gate.json`.

## Exact scope

| Family | Parent | Leaf partitions | Pre-action rows | Pre-action bytes | Post-action bytes | Restore class |
|---|---|---:|---:|---:|---:|---|
| Logical current | `public.leaderboard_current_entries` | 9 | 39,820,273 | 26,674,814,976 | 147,456 | Rebuild semantic current from published physical snapshots |
| Logical versions | `public.leaderboard_entry_versions` | 9 | 194,171,215 | 96,499,073,024 | 147,456 | Experimental chronology; intentionally discardable |
| Metrics | `public.leaderboard_logical_write_metrics` | none | 108 | 106,496 | 106,496 | Retained |

Each target family has `bass`, `default`, `drums`, `guitar`, `solo_bass`,
`solo_drums`, `solo_guitar`, `solo_vocals`, and `vocals` leaf partitions. The
`default` partition owns the five peripheral instrument values. Schema,
constraints, partitions, and primary indexes remain after `TRUNCATE`.

## Retired secondary index family

The following parent trees are intentionally absent:

- `ix_lce_scope_rank`
- `ix_lce_last_changed`
- `ix_lev_open_versions`
- `ix_lev_from_scrape`

They owned 36 physical child indexes and zero constraints. Current production
has no logical-shadow reader, and `FeatureOptionsValidator` rejects enabling
the only writer. A transactional drop/rollback proof preserved exact bounded
current/version fingerprints; the production drop retained all 20 primary-key
constraints and `13/13` public route/export/history/ranking fingerprints.

Exact concurrent child rebuild, parent creation, and attach SQL is retained at:

`post-scrape-1265-capacity-recovery-20260727T0011Z/rollback/recreate-logical-shadow-secondary-indexes.sql`

Run that package before any future versioned migration re-enables a logical
writer or reader. `DatabaseInitializer` no longer recreates these indexes.

## Executed preconditions

All preconditions passed:

1. Runtime was `gpt-5.6-sol`, reasoning `max`, context `long_context`.
2. Evidence and scratch remained on `/mnt/docker-storage`.
3. `fstworker` was exited with restart `no`; its ledger was offline with no
   operation, active scrape, worker query, lock, vacuum, or index build.
4. Postgres, `fstservice`, `festivalweb`, `/readyz`, the web shell, and
   `/api/service-info` were healthy.
5. Published scrape `1267` was unfrozen.
6. The zero-scratch reclaim guard passed using the conservative
   `123,173,888,000`-byte estimate.
7. Fresh counts and canonical fingerprints exactly matched readiness:
   `39,820,273` current rows /
   `054b9bbeb52d6670b4adee9fc7afcc101977132a20cecaf14fcc30690a69f3f2`
   and `194,171,215` version rows /
   `c9ab56adc1a983c62be0e3cc5dbe480ef6b6993a41de601638197cb394424313`.
8. Production kept `Features__WriteLogicalLeaderboardVersions=false`.
   Code inspection found no API reader; the remaining maintenance/writer
   SELECTs are behind that startup-rejected flag. A controlled full public
   fingerprint window produced zero target table or statement-counter delta.
9. Database dependency recapture found zero inbound/outbound foreign keys,
   non-internal triggers, views, materialized views, routines, rules,
   publications, or prepared statements.

## Executed action

The rollback-only rehearsal and committed action used the same guarded
transaction:

```sql
BEGIN;
SET LOCAL lock_timeout = '5s';
SET LOCAL statement_timeout = '10min';
TRUNCATE TABLE
    public.leaderboard_current_entries,
    public.leaderboard_entry_versions;
COMMIT;
```

No statement used `CASCADE`; the metrics table was not included. Before
commit, the transaction verified the exact rows, bytes, relation/index/
constraint counts, publication, worker, scrape, query, and lock state. While
the truncate remained uncommitted and held its locks, the public suite stayed
HTTP `200` and `13/13` exact. The production transaction then committed.
The 60-second monitor covered the action and four post-commit ticks.

## Validation

- Both target parents and all 18 leaves contain zero rows.
- Metrics remain `108` rows / `106,496` bytes.
- All 20 constraints and 20 retained primary indexes are valid, ready, and
  live; the retired secondary family remains absent.
- No target query, ungranted lock, vacuum, index build, rewrite, or active
  scrape remains.
- Pre-action, pre-commit, immediate post-action, and 60-second post-action
  public suites are `13/13` exact HTTP `200`.
- The target family fell from `123,173,888,000` to `294,912` bytes. Database
  size fell from `3,823,878,641,331` to `3,700,705,048,243` bytes.
- The final reclaim and scrape guards both pass. Free space remains below the
  seven-day threshold, but one full scrape has `103,935,067,269` bytes of
  margin above the corrected `60,392,999,803`-byte requirement.

## Rollback and rebuild

The rollback-only rehearsal proved transaction rollback before commit. After
commit:

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

## Next storage phase

Keep `player_score_observations` separate from this completed phase. The next
lowest-risk storage phase is to independently verify that both observation
writers were disabled for published scrape `1267`, refresh its manifest and
rehydration package, and apply the same pre/post public parity gate before any
observation truncate. The compact solo projection remains an implementation
candidate, but optional-build/rewrite work is still below the seven-day
headroom threshold.
