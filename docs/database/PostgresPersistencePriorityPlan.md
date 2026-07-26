# Postgres Persistence Priority Plan

This plan records the approved direction for improving FST Postgres persistence while allowing normal scrape/service operation to continue. Destructive database cleanup, irreversible migrations, and reclaim maintenance are auto-approved after live-scrape A/B testing proves the new path has the same data as the old path.

## Current production state

- Production compose ownership: `/home/sfenton/Docker/FestivalServiceTracker`.
- `fstservice`, `festivalweb`, and `fst-postgres` are healthy.
  `fstservice` runs
  `fstservice:band-song-retire-3ac2a7c9`. `fstworker` remains held
  on `fstservice:worker0a-recovery-21bd5f56` with run-once scheduling and
  restart disabled.
- Scrape `1236` published `6,138` complete scopes and `39,588,650` rows with
  the publication ledger unfrozen and remains authoritative. Scrape `1263` is
  failed at `capacity_watchdog_abandoned`, owns zero published-source rows,
  and scrape `1264` is failed at `capacity_postwriter_guard` after
  `8,232/8,232` complete manifests. Both candidates own zero published-source
  rows, and the worker ledger is offline.
- Failed-candidate derived-read isolation keeps mapped solo leaderboards
  available while unversioned ranking/history/export and band-song cache
  misses fail closed until the next successful publication. This is separate
  from `scrape_publication_state.public_reads_frozen`, which is `false`.
- `fstservice` and `festivalweb` may be restarted for maintenance and must be
  recovered promptly. `fstworker` remains held for this boundary; the next
  scrape must use commit `8db72081` or newer so retired indexes are not
  recreated.
- PG-3 dropped only `public.ix_crh_latest`, reclaiming exactly
  `20,890,148,864` database bytes. Free space rose from `78,549,483,520` to
  `99,439,702,016` bytes; the guard horizon improved from `2.61` to `3.31`
  days. Optional builds and rewrites remain blocked below seven-day headroom.
- The 2026-07-15 incident reclaimed another `3,277,996,032` database bytes by
  retiring partitioned `public.ix_btrhlv2_snapshot`, plus `751,341,568` bytes
  of reproducible non-database cache/scratch. A separate decision retired
  `public.ix_btrhpv2_snapshot` for another `8,864,440,320` database bytes.
  Free space initially reached about `39.83 GB`.
- The residual phase replaced the `23,526,973,440`-byte
  `ix_crh_retention_cutoff_account` btree with the 688,128-byte
  `ix_crh_retention_cutoff_brin`. Free space reached `63,339,065,344` bytes,
  and the measured `45,148,225,536`-byte scrape-completion guard passes with
  `18,190,839,808` bytes of margin.
- Final WORKER-0A candidate `1262` consumed that recovered headroom before
  rankings/publication. It left `31,264,702,464` bytes free and a measured
  `13,883,523,072`-byte shortfall. Published `1236` remains authoritative and
  unfrozen; `1262` owns zero published-source rows.
- The post-`1262` residual phase retired only the non-constraint
  `ix_rh_latest` partitioned family after replacing its ranking latest-row
  query with a primary-key group/max join. The drop reclaimed exactly
  `45,547,339,776` database bytes; final measured free space is
  `76,804,927,488` bytes, `31,656,701,952` bytes above the
  `45,148,225,536`-byte scrape boundary.
- Scrape `1263` consumed the restored headroom before publication. A separate
  residual recovery then retired `33` owner-proven non-constraint indexes in
  six families, reclaiming `17,174,200,320` database bytes. Final measured
  free space is `48,546,029,568` bytes, leaving `3,397,804,032` bytes above
  the same scrape boundary.
- LOGICAL-RETIRE readiness identified `141,462,937,600` bytes in the disabled
  `leaderboard_current_entries*` and `leaderboard_entry_versions*` shadow.
  Exact ownership, rebuild, and rollback evidence is complete, but no
  disabled-writer scrape has completed global publication. The tables remain
  intact and the destructive gate remains blocked.
- SOLO-DYNAMIC-AB measured the published solo projection at
  `46,633,459,712` bytes for `39,601,283` rows. Wholesale dynamic reads remain
  rejected. The accepted research candidate is a keyless, partitioned compact
  projection with the existing account/rank/score access paths, a bounded
  generation-hot tier, and a default-off stored-rank offset query flag. No
  production table, index, or read-source cutover occurred.
- SNAPSHOT-REUSE repaired the default-off physical write-skip candidate so it
  uses complete manifests and the current published source rather than mutable
  active/failed state. After device authentication was refreshed, scrape
  `1264` completed all network manifests/writers under the candidate, but only
  `281` scopes / `219,427` exact rows were reusable. Actual-run calibration
  estimates `78,765,704` physical bytes and about `166,448,926` WAL bytes
  avoided, while snapshot relations still grew `15,552,274,432` bytes.
  The post-writer guard blocked at `32,390,148,096` free bytes before
  rankings/publication. Production config was reverted; the flag remains
  default-off.
- STORAGE-OWNERSHIP completed continuous-safe P6/P8/P9 owner cards and exact
  manifests for `player_score_observations`, `scrape_dirty_*`, and legacy
  `leaderboard_entries_*`: `61,217,292,288` bytes total. Observation dual
  writers are default-off in candidate code/config; dirty tables have no
  current caller; legacy supplemental writes remain enabled because
  publication-critical `PostScrapeBandExtractor` still reads the legacy
  table. No production data/index/schema/config mutation or worker start ran.
  The final observation guard measured `52,103,634,944` free bytes after temp
  files/WAL aged out, with database size unchanged.
- BAND-SONG-PROJECTION retired only the stale optional
  `band_song_team_rankings*` data after proving successful scrape `1236`
  published with rebuilds skipped, live `/songs` versus `/song-rows` parity,
  failed-candidate isolation, exact archive restore, and two rolled-back
  production truncates. Four tables lost `36,747,099` rows and exactly
  `28,315,533,312` database bytes while their schemas, nine indexes, TOAST
  relations, and three `band_song_team_ranking_state` rows were retained.
  Final free space is about `58.97 GB`; both measured scrape guards now pass.
  The worker was not started.
- Public band-history team/date indexes remain because live plans proved their
  route ownership. Composite retention now uses BRIN cutoff rejection plus the
  primary key for account/date probes.
- All FST database/storage/reclaim work must remain on the 4 TB FST drive. Do not use alternate drives for data, scratch, migration, export, or repack workspace unless SFenton explicitly overrides this rule later.

## Capacity preflight guard

Run the read-only guard before every broad scrape, post-process, optional
shadow/history build, or maintenance/rewrite action:

```bash
tools/postgres-capacity-guard.sh \
  --action-class observation \
  --output /mnt/docker-storage/Docker/FestivalServiceTracker/fst-data/autonomous-artifacts/<session>/capacity-preflight.json
```

Use `--action-class scrape` or `post-process` for critical pipeline work,
`optional-build` for work that must defer below the seven-day threshold, and
`maintenance` or `rewrite` with `--required-scratch-bytes` for actions that
must fail closed without exact scratch. Use `reclaim` only for a proven
space-releasing action with zero transient and required scratch bytes. The
guard:

- verifies the active Postgres data mount is under `/mnt/docker-storage`;
- records filesystem free/used bytes, database size, WAL-directory size,
  transient/scratch estimates, scrape/publication/freeze state, locks, and
  active vacuum/index/rewrite work;
- defaults to measured roadmap assumptions of 14 GiB growth per full scrape,
  two full scrapes per day, and a seven-day alert/defer threshold;
- blocks optional builds or rewrites that cannot preserve that headroom and
  blocks critical scrape/post-process work below one full-scrape growth window;
- blocks `reclaim` below that emergency window or while vacuum, index build,
  rewrite, or ungranted-lock conflicts exist.

Override growth/rate thresholds only with measured evidence. A successful
observation with a capacity alert is evidence, not permission for an optional
build or rewrite; rerun the guard using the exact action class and scratch
estimate.

## Completed persistence phases

### [x] Phase 6: logical leaderboard version persistence

Phase 6 added shadow logical persistence while keeping physical snapshot tables authoritative for reads:

- `leaderboard_current_entries`
- `leaderboard_entry_versions`
- `WriteLogicalLeaderboardVersions` feature flag
- dual-write from `_le_staging`
- rollback for incomplete/orphaned logical artifacts
- fast truncate rollback for all-invalid artifacts
- OOM-safer curl fallback logging

Production eval scrape `1214` completed and was published after manual recovery. Commit: `02460b13 Add logical leaderboard version persistence`.

### [x] Phase 7: logical write metrics

Phase 7 added logical write classification metrics before attempting physical write skipping:

- `leaderboard_logical_write_metrics`
- metrics upsert from staging
- metrics rollback cleanup
- unit coverage for changed/new/unchanged classification and rollback cleanup

Commit: `2ac02445 Record logical leaderboard write metrics`.

Full-scale scrape `1218` produced useful metrics but failed before publish because Postgres ran out of space during ranking work:

| Metric | Rows | Share |
|---|---:|---:|
| Observed | 39,385,606 | 100.00% |
| Unchanged | 27,178,074 | 69.01% |
| Changed | 12,175,970 | 30.91% |
| New | 31,562 | 0.08% |
| Current upserts / versions opened | 12,207,532 | 31.00% |

Important finding: the logical model indicates most observed rows are unchanged,
so physical write skipping is likely valuable. The immediate measured scrape
headroom is now restored; promotion still requires its own complete live parity
window.

## Capacity boundary and remaining alert

`/mnt/docker-storage` hosts the active Postgres bind mount. The resumed
SNAPSHOT-REUSE run started with `52,199,215,104` free bytes. All `8,232`
manifests and writers completed, but the post-writer guard found only
`32,390,148,096` free bytes versus the `45,148,225,536` measured baseline
requirement and `44,394,828,933` candidate estimate. The guard rejected both
paths and stopped scrape `1264` before rankings/publication.

The BAND-SONG-PROJECTION retirement later reclaimed exactly
`28,315,533,312` database bytes. After retaining a checksummed
`2,184,507,134`-byte exact data archive on the FST drive, final measured free
space is `58,971,013,120` bytes. The baseline scrape guard now passes with
`13,822,787,584` bytes of margin and the SNAPSHOT-REUSE estimate passes with
`14,576,143,227` bytes of margin. This clears the start-capacity blocker but
does not promote the rejected candidate or restore scheduling.
`fstworker` remains held on `fstservice:worker0a-recovery-21bd5f56` with
restart `no`; a future candidate must use current source built with
`FSTService/Dockerfile` and rerun auth, `25/25` proxy, capacity, and full
public-health guards immediately before start. Optional builds and rewrites
remain blocked below seven-day headroom.

Destructive cleanup, irreversible migration, drop/truncate/repack/rewrite
work, or active Postgres data movement may proceed automatically after a
live-scrape A/B proves old-vs-new data parity and the exact objects, rollback
path, and post-action validation are documented.

## Task status

| Task | Status | Notes |
|---|---|---|
| Phase 6 logical current/version dual-write | Complete | Implemented, deployed, evaluated on scrape `1214`, committed as `02460b13`. |
| Phase 7 logical write metrics | Complete | Implemented, deployed, committed as `2ac02445`; production metrics captured from failed scrape `1218`. |
| LOGICAL-RETIRE ownership/rebuild package | Accepted readiness / truncate blocked | Exact `141,462,937,600`-byte object manifest, `39,820,273` current rows, `194,171,215` version rows, open-version integrity, stale-1237/public-1236 divergence proof, schema/rebuild SQL, and bounded nine-instrument regeneration are complete. No disabled-writer scrape has globally published, so no truncate ran. |
| SOLO-DYNAMIC-AB compact published solo read model | Accepted research/implementation candidate / migration blocked | Full owner/query matrix, service-cold and warm baseline, bounded unlogged samples, exact c1/c8 fingerprints, storage math, rollback DDL, and default-off rank-offset code are complete. Conservative compact-plus-hot projection is <=`20,215,010,912` bytes, reclaiming >=`26,418,448,800` bytes (`56.65%`). Final `3,812,061,184`-byte margin cannot safely build it. |
| Experimental logical shadow cleanup | Complete | Approved cleanup truncated experimental logical shadow tables and removed incomplete scrape `1218`. |
| Database architecture evaluation | Complete | Read-only code review and production probes completed on 2026-07-06. |
| History/index owner cards | Complete | Refreshed band v2, composite history, observation, dirty-work, and latest-state owner cards on 2026-07-13. Public team/date and retention indexes were retained from plan/caller proof. |
| PG-3 `ix_crh_latest` reclaim | Accepted | After scrape `1236` publish/unfreeze and worker hold, dropped one non-constraint index concurrently in 0.18 s. Reclaimed `20,890,148,864` database bytes with exact route/export/history/ranking/plan parity and tested recreate DDL. |
| PG-3 `ix_btrhlv2_snapshot` family reclaim | Accepted | During the scrape-`1261` capacity hold, dropped one partitioned non-constraint family in 68.378 ms. Reclaimed `3,277,996,032` database bytes; 12 route/ranking/history/export fingerprints and all three sampled band-history routes matched exactly. |
| PG-3 `ix_btrhpv2_snapshot` family reclaim | Accepted | As a separate decision, dropped the partitioned points-v2 snapshot lookup family in 129.457 ms. Reclaimed `8,864,440,320` database bytes; retained team/date plans and 12 route/ranking/history/export fingerprints matched exactly. |
| Residual incident index sweep | Superseded by deeper owner proof | Rejected the sufficiently large Trios current-projection index because the exact public selected-team plan uses it. A later retention-query redesign made the composite retention btree safely replaceable. |
| PG-3 composite retention btree reclaim | Accepted | Built a 688,128-byte BRIN replacement, removed the global retention sort, and dropped only `ix_crh_retention_cutoff_account` concurrently in 0.16 s. Reclaimed `23,526,973,440` database bytes; final free space is `63,339,065,344` bytes with `18,190,839,808` bytes above the measured scrape boundary. `12/12` fingerprints and `106` targeted tests passed. |
| PG-3 post-`1262` rank-history index reclaim | Accepted | Replaced latest-row distinct/sort with a primary-key group/max join, then dropped only non-constraint `ix_rh_latest` in 0.30 s. Reclaimed `45,547,339,776` database bytes; final measured free space is `76,804,927,488` with `31,656,701,952` bytes above the scrape boundary. `12/12` fingerprints and `68` targeted tests passed. |
| Scrape `1263` stale recovery and derived isolation | Accepted safety recovery / capacity blocked | Marked the watchdog-stopped scrape failed, preserved/unfroze published `1236`, reconciled the worker offline, deployed failed-candidate cache-only/fail-closed isolation, and restored the proxy guard to `25/25`. Free space is `31,385,374,720`, `13,762,850,816` bytes below the measured scrape boundary. |
| PG-3 post-`1263` residual index reclaim | Accepted | Dropped `33` non-constraint indexes across six exact owner-card families. Reclaimed `17,174,200,320` database bytes; measured free space is `48,546,029,568`, `3,397,804,032` bytes above the scrape boundary. Public route/fail-closed parity was exact, `120/120` tests passed, and commit `8db72081` prevents recreation. |
| Band rank-history retention policy draft | Complete | Semantics-first v2 retention options and parity gates documented on 2026-07-06. |
| `band_read_*` quarantine parity package | Complete | Reversible, live-scrape A/B parity-gated quarantine package documented on 2026-07-06; no DDL executed. |
| Phase 8 physical snapshot write skipping | Code/readiness accepted / live A/B capacity-blocked and reverted | Scrape `1264` completed `8,232/8,232` manifests and zero writer failures with the default-off candidate enabled. Exact live reuse was only `281` scopes / `219,427` rows; zero unchanged scope had candidate physical rows. Estimated benefit was `78,765,704` physical bytes and `166,448,926` WAL bytes, but the post-writer guard blocked at `32,390,148,096` free before rankings/publication. Production config was reverted, `1236` remains published, and the flag remains off. |
| P6 player-score observation ownership | Code/readiness accepted / truncate blocked | `11,686,199,296` bytes and `9,480,671` rows. No production reader; solo and band-member writers now have independent default-off flags. Full live writer-off publication parity is still required. |
| P8 stale dirty-work ownership | Readiness accepted / truncate blocked | Four tables total `8,706,752,512` bytes and `19,836,661` rows only from scrapes `926`-`1146`. No current repo/database/runtime writer; checksum-guarded exact truncate package is ready. |
| P9 legacy mutable leaderboard ownership | Mixed / reader migration blocked | Nine partitions total `40,824,340,480` bytes and `36,768,081` rows. Main scrape writer is off and mapped public reads bypass legacy, but supplemental dual writers and publication-critical band extraction remain owners. |
| Rank/temp spill write-mode reduction | Accepted config/code default | Switched band team ranking rebuild default from `Monolithic` to `ComboBatched` to reduce one-shot build-table insert pressure; all write modes already have parity coverage. |
| Optional band-song projection pressure gate | Complete | Defaulted optional rebuilds to disabled, made published reads reject stale/missing rows, and retired `36,747,099` stale rows for `28,315,533,312` database bytes while retaining schema/state and an exact restore archive. |
| BAND-SONG-PROJECTION retirement | Accepted | Truncated only `band_song_team_rankings` and the three `band_song_team_rankings_current_band_*` tables without `CASCADE`; 24/24 public route fingerprints remained exact, all nine indexes stayed valid, and both scrape guards now pass. Evidence: `/mnt/docker-storage/Docker/FestivalServiceTracker/fst-data/evidence/band-song-projection-retirement-20260726T103231Z`. |
| P7 bloat readiness probe | Complete | Repaired stale candidate table stats with bounded `ANALYZE` and refreshed read-only size/dead-tuple estimates; destructive/rewrite maintenance remains blocked by headroom and parity gates. |
| P8 public cache write-pressure reduction | Complete | Stopped request-time public API middleware writes to `api_response_cache`; cache persistence now stays in precompute/publish paths while frozen reads still serve published cache hits. |
| API service redeploy | Complete | Built `fstservice:sticky-rank-history-tracking`, recreated `fstservice` only, kept `fstworker` stopped, and verified `/readyz`, `festivalweb`, Postgres, and disk after recovery. |
| Worker validation start | Rejected / blocked | Starting `fstworker` with safer defaults caused `fstservice` and `/api/service-info` through `festivalweb` to time out; `fstworker` was stopped immediately and public API/web health recovered. |
| Worker scraping heal | Complete | Added worker-only startup schema-init skip, rebuilt the image, started `fstworker`, and held a 10-minute full-public-path watchdog while scrape `1219` began. |
| Emergency `band_read_*` reclaim | Complete | At 44 MB free / 100% disk, stopped `fstworker`, froze public reads to published `1214`, truncated rollback-safe logical shadow tables, quarantined/validated/dropped unused derived `band_read_*` tables, restored about 435 GB free, and restarted `fstworker` as scrape `1222`. |
| Autonomous scrape rollout | Rejected after scrape `1264`; capacity blocked | Candidate `1263` completed `8,208/8,208` manifests and was stopped during rank history. Residual reclaim restored enough start headroom for SNAPSHOT-REUSE candidate `1264`, which completed `8,232/8,232` manifests/writers but failed the strict post-writer guard at `32,390,148,096` free bytes before rankings/publication. Published `1236` remains safe; final free space is below both scrape requirements. |
| Destructive retention/reclaim | Parity-gated auto-approval | Deletes, drops, rewrites, repacks, and moves are auto-approved after live-scrape A/B proves the new path has the same data as the old path and rollback/post-action validation are documented. |
| Next implementation phase | Snapshot-reuse resume capacity-ready / worker held | Both measured scrape guards pass after BAND-SONG-PROJECTION reclaim. Do not start in this phase; rebuild a current-source worker image, rerun auth/proxy/public-health/capacity guards, and execute the parent-owned single-candidate SNAPSHOT-REUSE window. Logical truncate, optional builds, rewrites, repacks, broad movement, and owner-rejected drops remain blocked. |

## LOGICAL-RETIRE decision package (2026-07-25)

| Gate | Evidence | Decision |
|---|---|---|
| Ownership | No API/service reader; no FK/view/matview/routine/rule/trigger/policy/external runtime dependency; production writer disabled | Pass |
| Exact inventory | 18 leaf tables, 60 indexes, local primary keys only, negligible TOAST, `141,462,937,600` bytes total | Pass |
| Integrity | `39,820,273` current rows match `39,820,273` open versions with zero duplicate/missing/fingerprint errors | Pass |
| Current rebuild | Published snapshot sample rebuilt `139,264` rows across 27 scopes/all nine instruments with zero count mismatches | Pass |
| Version retention | Chronology is experimental/non-authoritative; preserve metadata/fingerprints and a deterministic sample, not a full duplicate | Pass |
| Live destructive parity | Disabled-write scrapes `1261`-`1263` completed manifests but failed before global publication | **Blocked** |

The exact future action is a short-timeout transaction truncating only the two
partitioned parents without `CASCADE`; schema and metrics remain. Do not run it
until a complete disabled-writer scrape publishes and the route/export/
ranking/history/publication fingerprints pass.

Evidence:
`/mnt/docker-storage/Docker/FestivalServiceTracker/fst-data/evidence/logical-retire-20260725T2306Z`.

## STORAGE-OWNERSHIP P6/P8/P9 decision package (2026-07-26)

| Surface | Rows | Bytes | Code/runtime ownership | Rebuild/rollback | Decision |
|---|---:|---:|---|---|---|
| `player_score_observations` | `9,480,671` | `11,686,199,296` | Two dual writers; no production reader/export owner | Solo semantics from `score_history`; current band baseline from band facts; exact schema retained | Default-off writer code accepted; truncate/drop blocked on one complete writer-off live publication |
| `scrape_dirty_*` | `19,836,661` | `8,706,752,512` | No current repository caller, DB dependency, external tool, or writer since 2026-07-07 stats reset | Work state is intentionally discardable; schema/checksum manifest retained | Exact truncate package accepted; execution blocked on successful current-code scrape/parity |
| `leaderboard_entries_*` | `36,768,081` | `40,824,340,480` | Main scrape writer off; supplemental writer active; mapped public reads bypass; band extraction still reads legacy | Rebuild correct published baseline from scope map + physical snapshots; overlays remain separate | Do not disable supplemental writer or truncate until reader migration and full live A/B |

The published map owns `39,588,650` rows, `2,820,569` more than the legacy
table. All 27 bounded legacy-vs-published scope samples differed in count and
checksum, so current legacy rows are not a byte-exact published rollback copy.
An attempted exact all-row join was rejected after its read-only plan spilled
temp and hit the capacity ceiling; public health recovered with no locks, and
bounded indexed proof replaced it.

Read-only recapture and exact future SQL:

- `tools/postgres-storage-ownership-readiness.sh`
- `tools/sql/postgres-storage-readiness/`
- `docs/database/StorageOwnershipReadinessRunbook.md`

Evidence:
`/mnt/docker-storage/Docker/FestivalServiceTracker/fst-data/evidence/storage-ownership-20260726T013551Z`.

## SOLO-DYNAMIC-AB decision package (2026-07-25)

`current_leaderboard_entries*` currently owns `17,821,523,968` heap bytes and
`28,806,701,056` index bytes. The primary-key, account, rank, and score
families occupy `9,189,982,208`, `8,910,036,992`, `6,352,797,696`, and
`4,347,568,128` bytes respectively.

Every runtime reader is classified in the phase evidence. Exports and
unfiltered published totals already bypass the projection. Deep pagination,
account/player reads, score bands, rivals, rankings, precompute, and
notifications force retention of a full current row set and the three
account/rank/score access paths. The primary key and per-row `computed_at` do
not own a read path.

| Candidate | Storage / performance decision |
|---|---|
| Fully dynamic mapped physical reads | Rejected. Even the improved frozen-overlay query was exact but regressed warm top p95 by `312.6%` at c1 and `84.7%` at c8; deep cold p95 regressed `72.5%`. Live overlay also produced a correctness difference against the frozen projection. |
| Top-N or registered-only projection | Rejected alone. Top/registered routes were exact and faster, but deep pages and worker full scans still require the full projection. |
| Hash-account compact projection | Rejected. It projected to `15,417,423,699` bytes, but selected-row p95 regressed `140.9%` at c1 and `45.2%` at c8. |
| Keyless compact btree projection | Accepted research candidate. A 501,284-row matched sample projects to `18,536,114,242` bytes while preserving the account/rank/score indexes and generation/source guards. |
| Compact btree plus hot generation | Accepted design. Exact rank-offset coverage, registered accounts, and frozen overlay keys add <=`1,678,896,670` bytes, for a conservative <=`20,215,010,912` bytes total and >=`26,418,448,800` bytes reclaim. |

The default-off
`Features__UseStoredSoloProjectionRanksForFilteredReads` candidate computes a
filtered rank by subtracting only rows above the threshold from the stored
published rank. Exact 240-pair warm A/Bs improved filtered-player p95 from
`94.678` to `17.858 ms` at c1 and `190.519` to `59.291 ms` at c8; filtered
top p95 improved from `8.654` to `3.585 ms` and `29.644` to `22.545 ms`.
Separate c8 resource runs reduced PostgreSQL CPU by `86.9%`, reads by `88.0%`,
created no temp files/bytes, and did not increase peak memory.

Migration remains blocked. A compact logged shadow needs about `18.54 GB`
plus roughly `4.58 GB` largest-index workspace and `18.54-37.1 GB` WAL, or
about `41.6-60.2 GB` practical headroom while retaining rollback. Current
margin above the measured scrape boundary is only `3,812,061,184` bytes. First
complete the disabled-writer publication gate and logical-shadow retirement,
then run one full dual-build scrape/publication A/B before any compact
read-source cutover or old projection drop.

Evidence:
`/mnt/docker-storage/Docker/FestivalServiceTracker/fst-data/evidence/solo-dynamic-ab-20260725T2346Z`.

## SNAPSHOT-REUSE decision package (2026-07-26)

| Gate | Evidence | Decision |
|---|---|---|
| Code semantics | Complete manifests arrive before bounded-online writes; reuse selects the current published source, not active/failed state | Pass |
| Changed/unchanged/empty/mixed | PostgreSQL fixtures cover source pinning, legacy coverage upgrade, changed coverage, missing physical rows, failed active sources, overlays, projection fallback, and exports | Pass |
| Published source integrity | `6,096/6,096` snapshot scopes have exact counts totaling `39,588,650` rows; `42` explicit empty scopes | Pass |
| Start capacity | `52,199,215,104` free; baseline `45,148,225,536`; candidate estimate `44,394,828,933` | Pass with severe alert |
| Post-writer capacity | `32,390,148,096` free after `8,232/8,232` manifests/writers | **Blocked** |
| Actual storage benefit | `281` scopes / `219,427` rows; estimated `78,765,704` physical bytes and `166,448,926` WAL bytes avoided | Too small to offset current growth |
| PIA guard | 30 canonical services, 25 healthy unique effective exits, 400 aggregate RPS | Pass |
| Worker authentication | Auth persistence and `25/25` paired authenticated direct/PIA JSON parity | Pass |
| Manifest/writer correctness | `8,232/8,232` complete; zero incomplete, parse, retry-exhausted, or writer failures | Pass before stop |
| Physical skip proof | Zero unchanged scope had scrape-`1264` physical rows | Pass |
| Live publication parity | Capacity stop before rankings/publication; `1264` owns zero mappings and `1236` remained authoritative | **Not cleared** |
| Rollback/public parity | Prior worker/config restored; all 13 public/export/history/ranking fingerprints exact | Pass |

The code/readiness change remains accepted default-off, but the live candidate
was reverted and not promoted. Production remains on the existing
service/worker images with the worker held. The exact capacity gate and future
build procedure are documented in `docs/database/SnapshotReuseRunbook.md`.

Evidence:
`/mnt/docker-storage/Docker/FestivalServiceTracker/fst-data/evidence/snapshot-reuse-live-ab-20260726T032124Z`.

## Architecture evaluation evidence (2026-07-06)

The current storage blocker is not a single table; it is the combined effect of physical snapshots, band history, band read projections, rank history, and wide indexes.

| Evidence | Current value | Interpretation |
|---|---:|---|
| `/mnt/docker-storage` free space | about 77 GB free, 98% used | High risk for scrape/post-process/publish headroom; monitor closely while normal scraping proceeds. |
| Solo physical snapshots | 1,579 GB total; 681 GB heap; 898 GB index/toast | Largest storage target, but highest correctness risk. |
| Band rank-history points v2 | 799 GB total; 324 GB heap; 475 GB index/toast | Major storage and write-maintenance surface. |
| Band read projections | 398 GB total; 254 GB heap; 144 GB index/toast | Large derived surface; repo code did not reference several `band_read_*` names during review. |
| Solo/composite rank history | 230 GB total; 95 GB heap; 136 GB index/toast | User-visible history, with notable dead tuples and low-scan indexes. |
| Current band leaderboard entries | 129 GB total | Hot derived current-state surface; update/index cost matters. |
| Band identity/member facts | 108 GB total | High dead tuple ratios suggest cleanup/repack candidates after proof. |
| `pg_stat_database.temp_bytes` | 3,354 GB | Ranking/rebuild queries spill heavily; not a disk-free fix, but major I/O/CPU work. |
| `api_response_cache` | 6,597 rows, 106 MB, about 54 KB average JSON | Not a storage blocker; still avoid live request-time cache churn. |

Large low-scan indexes observed during the read-only probe:

| Surface | Example observed indexes | Size / scans | Priority meaning |
|---|---|---:|---|
| Band history points | `band_team_rank_history_points_*_ranking_scope_combo*` | 114 GB / 1 scan, 89 GB / 4 scans, 41 GB / 15 scans | Prove access paths, then replace/drop if unused. |
| Band read projections | `band_read_subject_row_pkey`, `ix_brsr_generation_subject_scope`, `band_read_hot_window_pkey` | 45 GB / 0, 34 GB / 0, 31 GB / 0 | Verify ownership and reader usage first; likely high-value reclaim. |
| Composite history | `ix_crh_retention_cutoff_account`, `ix_crh_latest` | 19 GB / 2, 19 GB / 8 | Validate retention/latest query paths before changing. |
| Rank history | instrument-account-date indexes | 3.5-10 GB each, fewer than 100 scans | Review whether primary key and query shape already cover actual reads. |
| Build/current band ranking indexes | build-table-named indexes on current/published tables | multiple 1-3 GB indexes with 0 scans | Verify whether stale build-name indexes are expected after table swaps. |

High dead tuple candidates observed:

| Surface | Dead tuple signal | Decision |
|---|---:|---|
| `band_members`, `band_member_stats`, `band_search_*_projection`, `band_entries_duets` | Refreshed P7 stats show about 4.93-17.17% dead tuples after bounded `ANALYZE`. | Do not use the earlier stale 99% signal for rewrite/repack decisions; maintenance remains after safer reclaim/headroom. |
| `band_team_rank_history_points_v2_trios` | Refreshed P7 stats show about 7.68% dead tuples. | Not a rewrite/repack target before history retention/index plan and headroom. |
| `band_team_rank_history_points_v2_duets` | Refreshed P7 stats show about 10.59% dead tuples. | Not a rewrite/repack target before history retention/index plan and headroom. |
| Solo/composite rank history partitions | `composite_rank_history` refreshed at about 13.84% dead tuples; `rank_history` is empty. | Consider only after retention/index review and parity/headroom gates. |

## Autonomous roadmap execution log

### [x] Phase A: P0-P2 read-only proof package (2026-07-06T22:36:16Z)

Mode: Current-system probe / retention feasibility. No schema, data, runtime config, service, index, table, worker, or scrape mutations were performed.

| Priority | Decision | Evidence | Remaining gate |
|---|---|---|---|
| P0 live safety | Accepted | `fstservice` `/readyz` returned `Healthy`; `festivalweb` was healthy and serving the app shell; Postgres was healthy; public reads were unfrozen; published scrape was `1214`; `fstworker` remained intentionally stopped/stale; no ungranted locks. | Continue checking before every approved phase. |
| P1 `band_read_*` ownership proof | Accepted for proof; blocked for reclaim until parity | Source search found no active repo references outside this plan; `pg_views` found no view references; `pg_stat_statements` references were only diagnostic queries; representative `/api/songs` and `/api/leaderboard/{songId}/bands/Band_Duets?top=5` returned 200 without `band_read_*` usage. | Any quarantine/drop/repack/truncate requires live-scrape A/B data parity first. |
| P2 low-scan index proof | Accepted for proof; blocked for changes until parity | Read-only index inventory found large low-scan surfaces, including band rank-history points v2, rank/composite history, current/published band ranking projections, band search, and scrape-dirty indexes. Several primary keys have zero scans but are structural constraints and are not safe drop candidates without design review. | Any index drop/replacement requires live-scrape A/B data parity, exact object list, and rollback DDL. |

P1 `band_read_*` inventory:

| Table | Total size | Heap | Index/toast | Estimated rows | Stats scans | Interpretation |
|---|---:|---:|---:|---:|---:|---|
| `band_read_hot_window` | 191 GB | 160 GB | 31 GB | 174,369,920 | 0 seq / 0 idx | Derived read projection with no observed usage. |
| `band_read_subject_row` | 190 GB | 88 GB | 102 GB | 60,946,732 | 0 seq / 0 idx | Derived read projection with no observed usage. |
| `band_read_rank_anchor` | 12 GB | 4,974 MB | 7,713 MB | 12,570,308 | 0 seq / 0 idx | Derived read projection with no observed usage. |
| `band_read_scope_state` | 5,459 MB | 1,901 MB | 3,559 MB | 7,615,178 | 0 seq / 0 idx | Derived read projection metadata with no observed usage. |
| `band_read_generation` | 96 KB | 16 KB | 80 KB | 65 exact rows | 0 seq / 0 idx | Small metadata table. |
| `band_read_publication_state` | 24 KB | 8 KB | 16 KB | 1 exact row | 0 seq / 0 idx | Small metadata table. |

P1 highest-value `band_read_*` index candidates for parity-gated quarantine/drop:

| Index | Size | Scans | Notes |
|---|---:|---:|---|
| `band_read_subject_row_pkey` | 45 GB | 0 | Structural primary key; table-level quarantine is safer than isolated PK drop. |
| `ix_brsr_generation_subject_scope` | 34 GB | 0 | Non-primary covering index; potential drop candidate if table remains unused. |
| `band_read_hot_window_pkey` | 31 GB | 0 | Structural primary key; table-level quarantine is safer than isolated PK drop. |
| `ix_brsr_song_scope_team` | 21 GB | 0 | Non-primary read-path index; potential drop candidate if table remains unused. |
| `ix_brra_scope_sort` | 5,325 MB | 0 | Non-primary read-path index; potential drop candidate if table remains unused. |

Recommended P1 parity package:

1. Approve a non-destructive quarantine plan for `band_read_*` objects, not immediate deletion.
2. Rename tables/indexes or otherwise hide the surface in a reversible maintenance window while keeping `fstservice` and `festivalweb` live.
3. Monitor API routes and logs for failed references.
4. Drop only after observation, rollback proof, and live-scrape A/B data parity.

P2 low-scan giant index evidence:

| Group | Low-scan index count | Total size | Risk classification |
|---|---:|---:|---|
| Other indexes, including snapshot primary keys/current-state primary keys | 58 | 827 GB | Not a blanket drop pool; many are structural constraints despite low scans. |
| Band rank-history points v2 indexes | 9 | 474 GB | High-value design review target; history API parity required. |
| Rank/composite history indexes | 17 | 134 GB | Retention/latest-query ownership required before changes. |
| Band ranking projection indexes | 10 | 22 GB | Check generated build-table naming and current/published query plans. |
| Band search projection indexes | 4 | 19 GB | Validate search/profile routes before changes. |
| Band entries indexes | 5 | 6,304 MB | Must preserve scrape/write conflict checks and member lookups. |
| `scrape_dirty_band_team` indexes | 2 | 6,107 MB | Candidate for table-state proof; do not drop without dirty-workflow validation. |

P2 decision:

- Do not drop all low-scan indexes. Low `idx_scan` is useful evidence, but primary keys and unique constraints may be required for correctness, upserts, table swaps, or future writes.
- The highest safe next proof is to build per-index owner cards for non-primary, non-unique indexes first: query owner, source reference, endpoint/job dependency, replacement index if any, rollback DDL, and estimated reclaim.
- Primary-key/unique indexes should be handled through table/source-of-truth decisions, not isolated index drops.

### [x] Phase B: P3 snapshot publication correctness check (2026-07-06T22:41:14Z)

Mode: Current-system probe / live-safe non-destructive correction. No services were restarted, `fstworker` was not started, and no data was deleted.

Finding:

- `scrape_publication_state.published_scrape_id` was `1214`.
- `scrape_publication_state.public_reads_frozen` was `false`.
- `leaderboard_snapshot_state` had all 6,102 scopes finalized and active on snapshot `1218`.
- `scrape_log` contained `1214` but did not contain `1218`.
- Current-state read SQL uses `leaderboard_snapshot_state.active_snapshot_id` when public reads are unfrozen, and uses `scrape_publication_state.published_scrape_id` when public reads are frozen.

Live-safe correction applied:

```sql
UPDATE scrape_publication_state
SET public_reads_frozen = TRUE,
    public_reads_frozen_at = now(),
    public_reads_frozen_scrape_id = published_scrape_id,
    public_reads_frozen_reason = 'freeze-to-published-1214-after-1218-storage-failure',
    updated_at = now()
WHERE id = TRUE
  AND published_scrape_id = 1214;
```

Validation:

| Check | Result |
|---|---|
| `scrape_publication_state` | `published_scrape_id=1214`, `public_reads_frozen=true`, `public_reads_frozen_scrape_id=1214` |
| API health | `/readyz` returned `Healthy` after the correction |
| Web health | `festivalweb` remained healthy and served the app shell |
| Public solo route | `/api/leaderboard/{songId}/Solo_Guitar?top=3` returned 200 |
| Public band route | `/api/leaderboard/{songId}/bands/Band_Duets?top=3` returned 200 |
| Locks | No ungranted locks |

Decision:

- Accepted. Public/current-state reads are pinned back to the published scrape while storage/reclaim work continues.
- Keep `public_reads_frozen=true` until a later published scrape is safely promoted or a dedicated publication-state repair phase explicitly changes it.
- Treat any future `leaderboard_snapshot_state.active_snapshot_id` that points at a scrape missing from `scrape_log` as a correctness incident, not just a storage artifact.

### [x] Phase C: P4 band rank-history v2 proof (2026-07-06T22:43:00Z)

Mode: Current-system probe / history retention feasibility. No history rows, indexes, tables, workers, services, or configuration were changed.

Evidence:

| Surface | Total size | Heap | Index/toast | Estimated rows | Interpretation |
|---|---:|---:|---:|---:|---|
| `band_team_rank_history_points_v2_quad` | 365 GB | 139 GB | 226 GB | 329,537,184 | Largest band-history points partition. |
| `band_team_rank_history_points_v2_trios` | 288 GB | 118 GB | 170 GB | 323,710,688 | Large history surface with prior dead-tuple pressure. |
| `band_team_rank_history_points_v2_duets` | 146 GB | 67 GB | 78 GB | 197,216,976 | Smaller but still high-value surface. |
| `band_team_rank_history_latest_v2_*` | about 17.9 GB combined | about 8.2 GB | about 9.7 GB | about 21.5M combined | Latest-state delta detector; structural, not first reclaim target. |
| `band_team_rank_history_snapshot_v2` | 14 MB | 5.3 MB | 9.1 MB | 26,239 | Small metadata/freshness table. |

Metadata coverage:

| Band type | Completed snapshots | Date range | Source rows | Changed rows |
|---|---:|---|---:|---:|
| `Band_Duets` | 2,419 | 2026-04-26 to 2026-07-05 | 148,713,761 | 139,082,211 |
| `Band_Trios` | 7,713 | 2026-04-26 to 2026-07-06 | 227,017,957 | 203,548,764 |
| `Band_Quad` | 17,651 | 2026-04-26 to 2026-07-05 | 240,373,768 | 186,065,121 |

Representative public history route:

- `/api/rankings/bands/Band_Duets/{teamKey}/history` returned 200 with history data.
- `pg_stat_statements` then showed a `band_team_rank_history_points_v2` read returning 17 rows in about 9.35 ms with 28 shared blocks read.
- No ungranted locks were observed after the probe.

P4 decision:

- `band_team_rank_history_points_v2` is active user-facing history data, not an obsolete projection.
- Do not table-quarantine or broadly drop history points or latest-state indexes.
- Retention/index work must be history-semantics-first: define retention policy, prove endpoint parity, then target only redundant/non-public indexes or old date slices.
- Primary keys are structural. Low `idx_scan` on primary keys is not enough to drop them.
- Phase D completed the next safe read-only work: per-index owner cards for non-primary history indexes and a retention-policy draft for old history slices.

### [x] Phase D: P4.1 band rank-history v2 index owner cards and retention policy draft (2026-07-06T22:50:04Z)

Mode: Current-system probe / history retention feasibility. No history rows, indexes, tables, workers, services, runtime configuration, or scrape state were changed.

Live-safety preflight:

| Check | Result | Decision |
|---|---|---|
| Production compose | `fstservice`, `festivalweb`, and `fst-postgres` were healthy. | Safe for bounded read-only probes. |
| API readiness | `fstservice` `/readyz` returned `Healthy`; Postgres accepted connections. | Public API remained live. |
| Public reads | `published_scrape_id=1214`, `public_reads_frozen=true`, `public_reads_frozen_scrape_id=1214`. | Correctly pinned to the published scrape while storage work continues. |
| Disk | `/mnt/docker-storage` remained about 77 GB free and 98% used. | High scrape/eval/rewrite headroom risk; monitor closely and prioritize reclaim proof. |
| Locks/queries | No ungranted locks; only short idle/service and diagnostic sessions were observed. | No blocking database incident. |
| Worker/scrape | Recent `scrape_log` rows showed latest completed scrape `1214`; `scrape_log` has no `status` column. | Scrapes should proceed normally under the updated operating policy; scrape state probe adjusted to actual schema. |

Production band rank-history configuration observed from the live service:

| Setting | Live value | Interpretation |
|---|---|---|
| `BandRankHistory__Mode` | `Background` | History maintenance is best-effort/background, not scrape-critical. |
| `BandRankHistory__WriteMode` | `V2Only` | New band history writes target v2 tables. |
| `BandRankHistory__ApiReadSource` | `V2NarrowOnly` | Public band history reads use `band_team_rank_history_points_v2` directly. |
| `BandRankHistory__UseWideHistoryCompatibilityWrite` | `false` | Legacy wide compatibility writes are disabled in production config. |

P4.1 access evidence:

| Evidence | Result | Interpretation |
|---|---|---|
| Source owner | `FSTService/Api/RankingsEndpoints.cs` calls `MetaDatabase.GetBandRankHistory`; `MetaDatabase.GetBandRankHistoryFromPointsTable` reads `band_team_rank_history_points_v2` when v2 API reads are enabled. | `ix_btrhpv2_team_date` is a public history endpoint read-path index. |
| Writer owner | `BandRankHistoryWorker` calls `SnapshotBandRankHistoryChunked`; `SnapshotBandRankHistoryChunk` inserts v2 points and upserts latest-state rows. | Primary keys and latest-state indexes support write conflict checks and delta detection. |
| Retention owner | `CleanupBandRankHistoryRetention` currently deletes only legacy `band_team_rank_history_points`, `band_team_rank_history`, and `band_team_ranking_stats_history`; it does not delete v2 points/snapshot/latest tables. | v2 retention is not currently enforced by the existing cleanup path. |
| DB dependencies | No `pg_views` or regular `pg_proc` functions referenced `band_team_rank_history_points_v2`, `band_team_rank_history_snapshot_v2`, or `band_team_rank_history_latest_v2`. | Active dependencies are application SQL and indexes, not database views/functions. |
| Statement stats | `pg_stat_statements` showed v2 history endpoint reads, snapshot freshness reads, and diagnostic queries only. | Query ownership is narrow and auditable. |

Bounded query-plan evidence:

| Query | Plan/result | Decision |
|---|---|---|
| Public v2 band history read for one `Band_Duets` team over 30 days | `EXPLAIN (ANALYZE, BUFFERS)` returned 17 rows in 7.359 ms, using `band_team_rank_history_points_band_type_ranking_scope_combo_idx` on `band_team_rank_history_points_v2_duets`; 8 shared hits, 26 shared reads, no temp spill. | The large team/date index is active for the public read path. Do not drop without replacement/parity proof. |
| Snapshot freshness `max(snapshot_date)` for `Band_Duets` overall | `EXPLAIN (ANALYZE, BUFFERS)` returned in 0.073 ms using `band_team_rank_history_snapsh_band_type_ranking_scope_combo_key`; 4 shared hits. | Keep the small unique snapshot index; it supports freshness/status reads. |

P4.1 non-primary index owner cards:

| Index group | Size/scans | Owner and access path | Candidate decision | Rollback/proof gate |
|---|---:|---|---|---|
| `ix_btrhpv2_team_date` partition indexes (`band_team_rank_history_points_band_type_ranking_scope_combo_idx*`) | Duets 41 GB / 3 scans; Trios 89 GB / 0; Quad 114 GB / 0 | Public history endpoint filter: `band_type`, `ranking_scope`, `combo_id`, `team_key`, `snapshot_date >= cutoff`, ordered by `snapshot_date DESC`. | Keep for now. It is endpoint-owned even when `idx_scan` is low; Trios/Quad low scans likely reflect recent traffic, not proof of disuse. | Any replacement/drop needs sampled API parity for all band types, matched p50/p95/p99, and rollback DDL to recreate the partitioned indexes. |
| `ix_btrhpv2_snapshot` partition indexes (`*_snapshot_id_band_type_idx`) | Duets 2,004 MB / 4; Trios 3,164 MB / 4; Quad 3,284 MB / 4 | No current source read path with `WHERE snapshot_id`; v2 parity code reads by `band_type`/`snapshot_date`, and writers store `snapshot_id` from snapshot metadata. The observed scans were prior explicit diagnostic probes. | Accepted 2026-07-15; reclaimed `8,864,440,320` database bytes with exact public/history/plan parity. | Child indexes rebuild concurrently, then attach to a metadata-only parent; exact SQL is in the incident evidence. |
| `ix_btrhlv2_snapshot` partition indexes on latest v2 | Duets 714 MB / 0; Trios 1,140 MB / 0; Quad 1,271 MB / 0 | Latest-state table is a structural delta detector keyed by primary key; no current source read path with `WHERE snapshot_id`. | Accepted 2026-07-15; reclaimed `3,277,996,032` database bytes with exact parity. | Child indexes rebuild concurrently, then attach to a metadata-only parent; exact SQL is in the incident evidence. |
| `ix_btrhsv2_generation` on snapshot metadata | 4,392 kB / 0 | Small generation lookup index on 26,239-row metadata table. | Not a meaningful space target. Leave until a generation-owner review. | Recreate with `CREATE INDEX CONCURRENTLY` if ever dropped. |

P4.1 structural index decisions:

| Structural index | Size/scans | Decision |
|---|---:|---|
| `band_team_rank_history_points_v2_*_pkey` | Duets 35 GB / 0; Trios 78 GB / 0; Quad 110 GB / 0 | Keep. These enforce point uniqueness and `ON CONFLICT` behavior; low scan count does not make them safe reclaim targets. |
| `band_team_rank_history_latest_v2_*_pkey` | Duets 1,073 MB / 0; Trios 2,254 MB / 0; Quad 3,253 MB / 0 | Keep. These enforce latest-state uniqueness and writer conflict checks. |
| `band_team_rank_history_snapshot_v2_pkey` and unique snapshot key | 720 kB and 4,136 kB | Keep. The unique snapshot key is actively used by freshness reads; both are too small to matter for reclaim. |

P4.1 coverage evidence:

| Band type / scope | Completed snapshots | Date range | Source rows | Changed rows |
|---|---:|---|---:|---:|
| `Band_Duets` all scopes | 2,419 | 2026-04-26 to 2026-07-05 | 148,713,761 | 139,082,211 |
| `Band_Duets` combo | 2,365 | 2026-04-26 to 2026-07-05 | 115,965,807 | 107,095,590 |
| `Band_Duets` overall | 54 | 2026-04-26 to 2026-07-05 | 32,747,954 | 31,986,621 |
| `Band_Trios` all scopes | 7,713 | 2026-04-26 to 2026-07-06 | 227,017,957 | 203,548,764 |
| `Band_Trios` combo | 7,666 | 2026-04-26 to 2026-07-06 | 179,012,672 | 157,294,031 |
| `Band_Trios` overall | 47 | 2026-04-26 to 2026-07-05 | 48,005,285 | 46,254,733 |
| `Band_Quad` all scopes | 17,651 | 2026-04-26 to 2026-07-05 | 240,373,768 | 186,065,121 |
| `Band_Quad` combo | 17,609 | 2026-04-26 to 2026-07-05 | 207,168,338 | 155,859,795 |
| `Band_Quad` overall | 42 | 2026-04-27 to 2026-07-05 | 33,205,430 | 30,205,326 |

P4.1 retention policy draft:

| Option | Storage impact | Correctness risk | Decision |
|---|---|---|---|
| Keep all v2 history indefinitely | No immediate reclaim; continued growth in points and indexes. | Lowest semantic risk; preserves exact public history and audit trail. | Safe default until live-scrape A/B parity proves a destructive retention path. |
| Enforce 365-day v2 raw retention | Future old-slice reclaim once data ages past 365 days; no immediate win because current v2 range starts 2026-04-26. | Medium; must preserve endpoint semantics and restore/manifest coverage. | Candidate after adding v2-aware manifest/parity tooling and passing live-scrape A/B prune parity. |
| Cold archive old v2 slices on the 4 TB FST drive, then prune | Potential reclaim while preserving a restore path. | Medium/high; archive, checksum, and restore latency must be proven before delete. | Candidate after storage headroom, archive manifest, restore drill, exact object/date scope, and live-scrape A/B parity. |
| Coalesce old history to lower granularity | Potentially large future reclaim. | High; changes visible history density and may hide rank movements. | Rejected for now unless product semantics explicitly approve coarser old history. |
| Season-scoped raw retention plus summaries | Potentially useful after seasonal boundaries are codified. | High until season boundary, restore, and public-history behavior are specified. | Research-only; not an immediate reclaim action. |

P4.1 decision:

- Accepted as a proof/design phase.
- Do not drop the large `ix_btrhpv2_team_date` family from low scans alone; it is confirmed on the public read path.
- Both v2 `snapshot_id` lookup families passed separate owner/parity gates and
  were retired on 2026-07-15. Retain the public team/date and all
  primary/unique indexes.
- Keep all primary keys and unique constraints.
- Do not add v2 retention deletion until a manifest, endpoint parity suite, restore path, exact object/date scope, and live-scrape A/B data parity exist.
- Phase E completed the next safe non-destructive continuation by documenting a reversible `band_read_*` quarantine parity package. Actual quarantine/drop remains live-scrape A/B parity-gated.

### [x] Phase E: P1 `band_read_*` quarantine parity package (2026-07-06T22:51:18Z)

Mode: Current-system probe / parity-package design. No tables, indexes, rows, services, workers, runtime configuration, or scrape state were changed.

P1 refreshed object inventory:

| Object | Total size | Heap | Estimated rows | Role |
|---|---:|---:|---:|---|
| `band_read_hot_window` | 191 GB | 160 GB | 174,369,920 | Derived hot-window read projection. |
| `band_read_subject_row` | 190 GB | 88 GB | 60,946,732 | Derived subject/team read projection. |
| `band_read_rank_anchor` | 12 GB | 4,974 MB | 12,570,308 | Derived rank-anchor projection. |
| `band_read_scope_state` | 5,459 MB | 1,901 MB | 7,615,178 | Derived scope/generation state. |
| `band_read_generation` | 96 KB | 16 KB | not estimated | Small generation metadata. |
| `band_read_publication_state` | 24 KB | 8 KB | not estimated | Small publication metadata. |

P1 refreshed index inventory:

| Index | Size | Scans | Quarantine relevance |
|---|---:|---:|---|
| `band_read_subject_row_pkey` | 45 GB | 0 | Structural if table remains; table-level quarantine is safer than isolated PK changes. |
| `ix_brsr_generation_subject_scope` | 34 GB | 0 | Non-primary projection index; possible reclaim if table remains unused. |
| `band_read_hot_window_pkey` | 31 GB | 0 | Structural if table remains; table-level quarantine is safer than isolated PK changes. |
| `ix_brsr_song_scope_team` | 21 GB | 0 | Non-primary projection index; possible reclaim if table remains unused. |
| `ix_brra_scope_sort` | 5,325 MB | 0 | Non-primary projection index; possible reclaim if table remains unused. |
| `band_read_rank_anchor_pkey` | 2,387 MB | 0 | Structural if table remains. |
| `band_read_scope_state_pkey` | 1,761 MB | 0 | Structural if table remains. |
| `ix_brss_scope_generation` | 1,703 MB | 0 | Non-primary projection index; possible reclaim if table remains unused. |
| `ix_brsr_subject` | 1,622 MB | 0 | Non-primary projection index; possible reclaim if table remains unused. |
| `ix_brss_generation_scope` | 94 MB | 0 | Small non-primary projection index. |

P1 dependency evidence:

| Evidence source | Result | Interpretation |
|---|---|---|
| Repository source search | No `*.cs` references to `band_read_hot_window`, `band_read_subject_row`, `band_read_rank_anchor`, `band_read_scope_state`, `band_read_generation`, `band_read_publication_state`, or `BandReadIndex`. | Current repo code does not own active reads/writes for these objects. |
| `pg_views` | No view definitions referenced `band_read_*`. | No view dependency blocks quarantine. |
| `pg_proc` | No regular functions referenced `band_read_*`. | No stored function dependency blocks quarantine. |
| `pg_stat_statements` | Only one diagnostic count over the two tiny metadata tables was observed; no production read-path statements referenced large `band_read_*` objects. | Runtime evidence supports unused/obsolete projection classification. |
| Constraints | Only primary keys/check constraints were found; no foreign keys were observed. | Table-level quarantine remains reversible and lower-risk than partial index surgery. |

Approval-gated candidate action:

| Step | Action | Purpose | Live-safety gate | Rollback |
|---|---|---|---|---|
| 1 | Re-run live-safety preflight immediately before any DDL. | Confirm `fstservice`, `festivalweb`, Postgres, locks, disk, freeze state, and published scrape are still safe. | Must show healthy service/web/Postgres, no dangerous locks, public reads still frozen to `1214`, and no running scrape. | Abort with no mutation. |
| 2 | In a maintenance window, acquire short-lock-timeout DDL and rename `band_read_*` tables to a quarantine prefix/suffix after live-scrape A/B parity proves the active path no longer needs them. | Hide the projection from accidental readers without deleting data. | Keep downtime short if restart/redeploy is needed; normal scrapes may continue unless the maintenance step intentionally pauses them. Use short lock and statement timeouts. | Rename each object back to its original name. |
| 3 | Monitor representative public API routes and service logs. | Prove no active app path references quarantined objects. | `/readyz`, web app shell, `/api/songs`, representative solo leaderboard, representative band leaderboard, and band history route stay healthy. | Rename tables back immediately if any query fails because of quarantined names. |
| 4 | Hold quarantine through an explicit observation window. | Distinguish obsolete projection from rare path dependency. | Continue checking locks, errors, public reads, and disk. | Rename back if errors appear. |
| 5 | Only after successful observation, record final parity evidence and execute/drop under the parity gate. | Reclaim about 398 GB table/index space. | Drop remains destructive and requires the live-scrape A/B parity, rollback, and post-action validation gates. | Restore from backup/regenerate projection path must be documented before drop. |

Parity gate needed before Step 2:

> A live-scrape A/B must confirm the new active read/write path has the same data as the old `band_read_*` path for the affected routes/surfaces. Once parity passes, reversible table-rename quarantine is auto-approved for `band_read_hot_window`, `band_read_subject_row`, `band_read_rank_anchor`, `band_read_scope_state`, `band_read_generation`, and `band_read_publication_state`, with short lock/statement timeouts, prompt redeploy/recovery if needed, immediate API/log monitoring, and rename-back rollback.

P1 quarantine decision:

- Accepted as a parity-gated reclaim package.
- The package is the strongest near-term reclaim candidate because the group is about 398 GB and has no observed source/runtime dependencies.
- Quarantine/drop/truncate/repack/rewrite work is auto-approved after live-scrape A/B data parity passes and rollback/post-action validation are documented.
- If quarantine passes observation, a later drop may proceed after recording successful observation evidence, rollback/regeneration path, and exact objects/date.

### [x] Phase F: P5 default-off physical snapshot write-skip candidate (2026-07-06T23:27:00Z)

Mode: Implementation / capacity improvement. No production schema, data, runtime configuration, services, workers, indexes, tables, or scrape state were changed.

Live-safety preflight:

| Check | Result | Decision |
|---|---|---|
| Production compose | `fstservice`, `festivalweb`, and `fst-postgres` were healthy. | Safe for code/test work and bounded read-only probes. |
| API readiness | `fstservice` `/readyz` returned `Healthy`; Postgres accepted connections. | Public API remained live. |
| Public reads | `published_scrape_id=1214`, `public_reads_frozen=true`, `public_reads_frozen_scrape_id=1214`. | Public reads remain pinned to the last published scrape. |
| Locks/queries | No ungranted locks; only service idle sessions and the diagnostic query were observed. | No blocking database incident. |
| Disk | `/mnt/docker-storage` remained about 77 GB free and 98% used. | Too little headroom for another full scrape/post-process/publish eval; keep production rollout gated. |
| Worker | `fstworker` was stopped after exit 137 during band ranking rebuild with `53100: No space left on device`. | Do not restart a full worker scrape until code/deploy readiness and headroom/parity gates are satisfied. |

Implementation:

| Change | Files | Safety gate | Rollback |
|---|---|---|---|
| Added `Features:SkipUnchangedPhysicalLeaderboardSnapshots` defaulting to `false`. | `FSTService/FeatureOptions.cs` | Default-off; existing production behavior is unchanged until explicitly enabled. | Set the flag to `false` or remove the override. |
| Snapshot flush skips physical rows only when the current complete manifest, content, row count, coverage compatibility, and physical existence/count match the current published mapping. | `FSTService/Scraping/LeaderboardSpoolWriterFactory.cs` | Requires fingerprints, strict manifests, and published-source writes; incomplete or ambiguous scopes write normally. | Disable the flag; the old full physical snapshot insert path remains intact. |
| Snapshot finalization pins a skipped unchanged scope to its validated published physical source, never merely to the previous active/failed source. | `FSTService/Persistence/GlobalLeaderboardPersistence.cs` | Changed/new scopes with snapshot rows advance to the current scrape; empty scopes retain explicit-empty mapping behavior. | Disable the flag; finalization returns to one active snapshot per finalized scrape scope. |
| Added unit coverage for default flag behavior and the new/unchanged/changed snapshot-state contract. | `FSTService.Tests/Unit/ScraperOptionsAndModelsTests.cs`, `FSTService.Tests/Unit/GlobalLeaderboardPersistenceTests.cs` | Test proves unchanged scrape `43` writes zero duplicate snapshot rows, keeps active state at scrape `42`, and advances to scrape `44` when data changes. | Remove the flag and test if rejected before deployment. |

Validation:

| Command | Result | Evidence |
|---|---|---|
| `dotnet test FSTService.Tests/FSTService.Tests.csproj --filter "FullyQualifiedName~GlobalLeaderboardPersistenceTests|FullyQualifiedName~ScraperOptionsAndModelsTests"` | Passed: 88 tests, 0 failed, duration 27 s. | Covers snapshot write-skip behavior, logical metrics, existing snapshot writes, legacy-live disabled behavior, and feature default assertions. |

P5 decision:

- Accepted as a default-off implementation candidate; its source-selection and
  complete-manifest gates were repaired by SNAPSHOT-REUSE on 2026-07-26.
- This is not a production promotion yet: enabling it requires a controlled live-scrape A/B that proves API/current-state/publication parity, projection freshness behavior, and publish/freeze semantics under mixed active snapshot IDs.
- Expected storage/WAL benefit is bounded by unchanged solo scopes; Phase 7 measured 27,178,074 unchanged rows out of 39,385,606 observed rows (69.01%), but the exact physical snapshot reduction must be measured in a matched scrape after deployment.
- This implementation does not solve the current disk blocker alone; it reduces future duplicate physical snapshot writes after a safe rollout and leaves destructive reclaim work parity-gated.

### [x] Phase G: P6 combo-batched band ranking write-mode default (2026-07-06T23:35:00Z)

Mode: Performance / capacity improvement. No production services, workers, schema, data, indexes, tables, or scrape state were restarted or mutated.

Reason for the change:

| Evidence | Interpretation | Candidate |
|---|---|---|
| Failed `fstworker` logs showed `Band_Trios` failed at `insert_ranking_rows` in `Monolithic` mode with `53100: No space left on device`. | A single giant insert from `_band_rank_results` into the build table is a high-risk write burst under the current 77 GB headroom. | Use the existing `ComboBatched` write mode, which inserts overall rows and then combo rows by combo ID. |
| `Band_Duets` band-song projection also failed building/indexing an optional projection and falls back when rebuild fails. | Phase G reduced required band-team ranking write burst size first; Phase H later completed optional-projection gating. | Keep Phase H's default-disabled optional projection gate unless a measured deployment eval proves it should be re-enabled. |
| `RebuildBandTeamRankings_AllWriteModesMatch` already compares `Monolithic`, `ComboBatched`, and `Phased` outputs. | The candidate has existing correctness parity coverage across ranking scopes and combos. | Promote `ComboBatched` as the safer default while preserving rollback to `Monolithic`. |

Implementation:

| Change | Files/artifacts | Safety gate | Rollback |
|---|---|---|---|
| Changed `BandTeamRankingRebuildOptions.WriteMode` default from `Monolithic` to `ComboBatched`. | `FSTService/Persistence/BandTeamRankingRebuildOptions.cs` | No schema/data mutation; runtime behavior changes only when new code/config is used by a worker. | Set `BandTeamRankings:WriteMode=Monolithic`. |
| Changed repo appsettings and compose template defaults to `ComboBatched`. | `FSTService/appsettings.json`, `FSTService/appsettings.Development.json`, `docker-compose.yml`, `deploy/docker-compose.yml` | Existing `BAND_TEAM_RANKINGS_WRITE_MODE` env override remains the rollback switch. | Override `BAND_TEAM_RANKINGS_WRITE_MODE=Monolithic`. |
| Updated active production compose defaults without restarting services. | `/home/sfenton/Docker/FestivalServiceTracker/docker-compose.pia-30.yml`, `/home/sfenton/Docker/FestivalServiceTracker/.env` | No live restart; next worker recreate will use `ComboBatched`. | Revert the same non-secret override to `Monolithic`. |
| Added a default-options unit assertion. | `FSTService.Tests/Unit/ScraperOptionsAndModelsTests.cs` | Guards the intended safer default. | Update/remove assertion if rollback is chosen. |

Validation:

| Command | Result | Evidence |
|---|---|---|
| `dotnet test FSTService.Tests/FSTService.Tests.csproj --filter "FullyQualifiedName~ScraperOptionsAndModelsTests|FullyQualifiedName~MetaDatabaseRankingsTests.RebuildBandTeamRankings_AllWriteModesMatch|FullyQualifiedName~RankingsCalculatorTests.ComputeBandRankings_UsesConfiguredBandRankingWriteMode"` | Passed: 23 tests, 0 failed, duration 3 s. | Confirms default option shape, all write-mode parity, and configured write-mode propagation into `RebuildBandTeamRankings`. |
| `docker compose -f docker-compose.yml -f docker-compose.pia-30.yml config | grep -n "BandTeamRankings__WriteMode"` in `/home/sfenton/Docker/FestivalServiceTracker` | Reported `BandTeamRankings__WriteMode: ComboBatched` for service config entries. | Confirms the active production compose config resolves the safer mode for the next recreate. |

P6 decision:

- Accepted as a low-risk write-pressure reduction.
- This does not claim a storage-reclaim win: final ranking tables and indexes are the same size, but the required build-table insert is split into smaller statements.
- The worker remains stopped after the previous disk-exhaustion failure; no full scrape restart was performed in this phase.
- Phase H completed the safe follow-up for optional band-song projection pressure.

### [x] Phase H: P6 optional band-song projection pressure gate (2026-07-06T23:45:00Z)

Mode: Performance / capacity improvement. No production services, workers, schema, data, indexes, tables, or scrape state were restarted or mutated.

Reason for the change:

| Evidence | Interpretation | Candidate |
|---|---|---|
| Failed `fstworker` logs showed `BandSongRankings` for `Band_Duets` failed during build/index work with `53100: No space left on device`. | The band-song projection is a large optional derived surface that can consume disk during already tight post-scrape ranking work. | Do not rebuild it by default while disk headroom is constrained. |
| `RankingsCalculator` already treats band-song projection rebuild failure as non-fatal. | Band rankings and scrape continuation do not require this projection to succeed. | Preserve scrape/ranking progress by skipping the optional rebuild unless explicitly enabled. |
| Band-song endpoint reads have live/current-projection fallbacks, but stale current derived tables could otherwise be read after skipping a rebuild. | Correctness requires stale derived rows to be rejected once current band rankings advance. | Add a freshness check that falls back when `band_song_team_rankings_current_*` is older than `band_team_rankings_current_*`. |

Implementation:

| Change | Files/artifacts | Safety gate | Rollback |
|---|---|---|---|
| Added `BandTeamRankings:RebuildBandSongTeamRankings`, default `false`. | `FSTService/Persistence/BandTeamRankingRebuildOptions.cs`, `FSTService/appsettings.json`, `FSTService/appsettings.Development.json`, `docker-compose.yml`, `deploy/docker-compose.yml` | Optional projection rebuild is skipped by default; band team rankings, history queuing, and live fallbacks continue. | Set `BandTeamRankings:RebuildBandSongTeamRankings=true` or `BAND_TEAM_RANKINGS_REBUILD_BAND_SONG_TEAM_RANKINGS=true`. |
| Skipped optional band-song projection rebuilds in `RankingsCalculator` unless explicitly enabled. | `FSTService/Scraping/RankingsCalculator.cs` | Logs a skipped phase and preserves current band-ranking success flow. | Re-enable the option. |
| Added stale current band-song projection detection. | `FSTService/Persistence/MetaDatabase.cs` | Current band-song projection reads now require projection `computed_at >=` current band-team ranking `computed_at`; otherwise the existing legacy/current-projection/live fallbacks are used. | Re-enable rebuilds or remove the freshness gate if a dedicated generation model replaces it. |
| Updated active production compose defaults without restarting services. | `/home/sfenton/Docker/FestivalServiceTracker/docker-compose.pia-30.yml`, `/home/sfenton/Docker/FestivalServiceTracker/.env` | No live restart; next worker recreate will skip optional band-song projection rebuilds by default. | Revert the same non-secret override to `true` if the optional projection is needed. |

Validation:

| Command | Result | Evidence |
|---|---|---|
| `dotnet test FSTService.Tests/FSTService.Tests.csproj --filter "FullyQualifiedName~ScraperOptionsAndModelsTests|FullyQualifiedName~RankingsCalculatorTests.ComputeBandRankings_SkipsBandSongProjectionByDefault|FullyQualifiedName~RankingsCalculatorTests.ComputeBandRankings_RebuildsBandSongProjectionWhenEnabled|FullyQualifiedName~RankingsCalculatorTests.ComputeBandRankings_UsesConfiguredBandRankingWriteMode|FullyQualifiedName~MetaDatabaseRankingsTests.GetBandSongPerformances_|FullyQualifiedName~MetaDatabaseRankingsTests.GetBandSongPerformanceExtremes_|FullyQualifiedName~MetaDatabaseRankingsTests.RebuildBandSongTeamRankings_PopulatesOverallAndComboProjectionRows"` | Passed: 34 tests, 0 failed, duration 5 s. | Confirms default skip, explicit enable, stale current projection fallback for normal and extremes reads, existing derived projection reads, and rebuild metrics. |
| `docker compose -f docker-compose.yml -f docker-compose.pia-30.yml config | grep -n "BandTeamRankings__RebuildBandSongTeamRankings\\|BandTeamRankings__WriteMode"` in `/home/sfenton/Docker/FestivalServiceTracker` | Reported `BandTeamRankings__RebuildBandSongTeamRankings: "false"` and `BandTeamRankings__WriteMode: ComboBatched`. | Confirms the active production compose config resolves the safer optional-projection gate for the next recreate. |

P6.1 decision:

- Accepted as a low-risk optional-work reduction.
- This preserves ranking correctness by falling back instead of serving stale current band-song projection rows after current band rankings advance.
- This does not reclaim existing projection storage; it prevents future optional rebuild pressure during post-scrape ranking work.
- The worker remains stopped after the previous disk-exhaustion failure; no full scrape restart was performed in this phase.

### [x] Phase I: P7 bloat maintenance readiness probe (2026-07-06T23:53:00Z)

Mode: Current-system probe / maintenance readiness. No production services, workers, schema, data rows, indexes, tables, rewrites, repacks, `VACUUM FULL`, drops, deletes, or scrape state were mutated.

Live-safety preflight:

| Check | Result | Decision |
|---|---|---|
| Production compose | `fstservice`, `festivalweb`, and `fst-postgres` were healthy. | Safe for bounded read-only probes and statistics refresh. |
| API readiness | `fstservice` `/readyz` returned `Healthy`; Postgres accepted connections. | Public API remained live. |
| Public reads | `published_scrape_id=1214`, `public_reads_frozen=true`, `public_reads_frozen_scrape_id=1214`. | Public reads remain pinned to the last published scrape. |
| Locks | No ungranted locks. | No blocking database incident. |
| Disk | `/mnt/docker-storage` remained about 77 GB free and 98% used. | Insufficient headroom for rewrite/repack/`VACUUM FULL` maintenance. |

Probe sequence:

| Step | Command/evidence | Decision |
|---|---|---|
| Initial P7 stats read | Candidate `pg_stat_user_tables` rows all reported `n_live_tup=0` and `n_dead_tup=0`, despite large relation sizes. | Stats were stale/missing and could not support a bloat decision. |
| Bounded stats repair | `SET statement_timeout = '5min'; ANALYZE ...` over documented P7 candidates completed successfully. | Accepted as safe non-destructive evidence generation. |
| Refreshed P7 stats read | Read-only stats after `ANALYZE` produced live/dead estimates and current `last_analyze` timestamps. | Accepted as the current P7 readiness baseline. |

Refreshed P7 candidate evidence:

| Table | Total size | Heap | Indexes | Live rows | Dead rows | Dead % |
|---|---:|---:|---:|---:|---:|---:|
| `band_team_rank_history_points_v2_trios` | 288 GB | 118 GB | 170 GB | 343,402,243 | 28,547,988 | 7.68% |
| `band_team_rank_history_points_v2_duets` | 146 GB | 67 GB | 78 GB | 215,553,885 | 25,527,803 | 10.59% |
| `composite_rank_history` | 79 GB | 22 GB | 56 GB | 241,030,651 | 38,726,954 | 13.84% |
| `band_member_stats` | 56 GB | 17 GB | 39 GB | 55,325,103 | 11,467,093 | 17.17% |
| `band_members` | 41 GB | 15 GB | 27 GB | 55,431,207 | 10,807,064 | 16.32% |
| `band_search_member_projection` | 31 GB | 13 GB | 18 GB | 26,582,525 | 1,669,111 | 5.91% |
| `band_search_team_projection` | 13 GB | 9,242 MB | 4,370 MB | 8,491,681 | 440,349 | 4.93% |
| `band_entries_duets` | 6,918 MB | 3,548 MB | 3,369 MB | 6,503,259 | 381,715 | 5.54% |
| `rank_history` | 0 bytes | 0 bytes | 0 bytes | 0 | 0 | 0.00% |

P7 decision:

- Accepted as a readiness/evidence phase.
- The prior "about 99%" dead-tuple signal for several candidates was stale; refreshed estimates are materially lower, with the highest current candidate at 17.17%.
- Do not run `VACUUM FULL`, `CLUSTER`, `pg_repack`, table rewrites, drops, deletes, or prune maintenance while the FST drive has only about 77 GB free and live-scrape A/B parity/rollback evidence is incomplete.
- Plain `ANALYZE` is accepted as completed evidence repair; plain `VACUUM` remains a possible low-risk future maintenance action, but it does not return filesystem space and should not be treated as solving the storage blocker.
- Next storage-blocker work should prioritize parity-gated derived-surface reclaim over bloat rewrites because current P7 dead percentages do not justify scratch-heavy rewrite/repack work under 98% disk usage.

### [x] Phase J: P8 public API cache request-time write reduction (2026-07-07T00:02:00Z)

Mode: Performance / capacity improvement. No production services, workers, schema, data rows, indexes, tables, or scrape state were mutated.

Reason for the change:

| Evidence | Interpretation | Candidate |
|---|---|---|
| `PublicApiResponseCacheMiddleware` captured every cacheable live GET response and called `BulkSetCachedResponses` when public reads were not frozen. | Live public reads could write `api_response_cache` on cache misses even though publish/precompute paths already populate persisted public responses. | Stop request-time DB cache writes; keep persisted cache reads for frozen/publication mode. |
| `ScrapeTimePrecomputer`, `DiskStagingWriter`, and publish flow still write/stage/promote `api_response_cache`. | Removing middleware writes does not remove the publication-keyed cache population path. | Preserve precompute/publish cache writes as the primary persistent cache path. |
| Frozen-mode middleware reads already serve `api_response_cache` hits and continue through endpoint fallback on misses. | Frozen published reads remain available. | Keep frozen cache-hit behavior unchanged. |

Implementation:

| Change | Files/artifacts | Safety gate | Rollback |
|---|---|---|---|
| Removed live request-time response capture/store from public API response cache middleware. | `FSTService/Api/PublicApiResponseCacheMiddleware.cs` | Non-frozen requests pass through unchanged; frozen cache hits and misses retain existing behavior. | Reintroduce middleware `BulkSetCachedResponses` if request-time persistent cache warming is needed. |
| Updated middleware tests to assert no live store and preserved response body. | `FSTService.Tests/Unit/PublicReadGateTests.cs` | Confirms response parity for non-frozen requests and existing frozen cache behavior. | Restore prior store assertion if rollback is chosen. |

Validation:

| Command | Result | Evidence |
|---|---|---|
| `dotnet test FSTService.Tests/FSTService.Tests.csproj --filter "FullyQualifiedName~PublicReadGateTests"` | Passed: 48 tests, 0 failed, duration 2 s. | Confirms public-read freeze state behavior, cache-key policy, frozen persisted-cache hits, frozen misses, and non-frozen no-store pass-through. |

P8 decision:

- Accepted as a low-risk DB write-pressure reduction.
- This does not change endpoint JSON bodies or frozen cached-read behavior.
- Persistent public API cache entries should now come from precompute/staging/publish flows, not opportunistic live GETs.
- Additional P8 targets, such as `/api/status` counters, `/api/songs` split payloads, and member-score fan-out, are deferred until deploy/eval evidence identifies a measured bottleneck and matched response-parity baseline.

## Autonomous DB size-reduction execution plan

This plan is the next autonomous starting point for reducing Postgres storage while avoiding substantial processing, memory, WAL, temp, or API read-cost regressions. The autonomous executor should run it phase-by-phase using the live A/B execution contract in `.github/skills/autonomous-plan-executor/SKILL.md`.

Global execution rules for every phase:

- Keep the existing 60-second public-path monitor running and print visible CLI progress every 60 seconds during deploy, scrape, post-process, publication, and maintenance windows.
- Before each phase, capture public-path health, Docker caps, disk, Postgres locks/long queries, active scrape/publication state, relation/index sizes, WAL/temp counters, representative route responses, and rollback.
- Deploy one candidate at a time. Do not bundle unrelated storage optimizations into the same A/B scrape.
- Validate correctness before speed or space: old-vs-new row counts, ranges, fingerprints/checksums, active/public scrape selection, representative API JSON parity, and route health.
- Reject/revert any candidate that changes public output, weakens historical correctness, makes `fstservice` or `festivalweb` unhealthy, or adds material sustained CPU/memory/WAL/temp/IO/API latency cost without explicit acceptance.
- Stop `fstworker` after each scrape/post-process/publish decision point unless the current phase explicitly requires continued scraping and public path remains healthy.
- Commit and push accepted code/config/docs before moving to the next phase. Revert rejected experiments before continuing.

### Phase S0: storage baseline, monitor, and safety harness

Goal: establish a trustworthy baseline and make the executor ready to run later phases without guessing.

| Task | Action | Acceptance gate | Rejection / blocker |
|---|---|---|---|
| S0.1 Live-safety baseline | Capture `docker compose ps`, `/readyz`, `festivalweb` shell, `/api/service-info`, Postgres readiness, locks, long queries, disk, Docker stats, publication state, active scrape, and worker status. | Public path healthy, no ungranted locks, disk headroom known. | Block DB-heavy work if public path is degraded or disk headroom is too low to run the phase safely. |
| S0.2 Storage inventory | Capture top table/index sizes, top low-scan indexes, column widths, TOAST share, WAL/temp counters, and current largest relation owners. | Baseline artifact saved in session `files/` and summarized in the phase report. | Repair stale stats with bounded `ANALYZE` where safe; otherwise mark measurements caveated. |
| S0.3 A/B harness | Verify scripts/commands for representative route JSON capture, count/range/fingerprint SQL, and monitor logging. | Reusable commands exist before candidate phases start. | Insert harness repair work before any live A/B phase. |

Decision: accepted readiness only; no storage mutation.

### Phase S1: physical snapshot write-skip A/B and promotion

Goal: reduce future physical snapshot growth without changing public reads or historical correctness.

Starting candidate: `Features:SkipUnchangedPhysicalLeaderboardSnapshots`, currently implemented but default-off.

| Task | Action | Acceptance gate | Rollback |
|---|---|---|---|
| S1.1 Fixture parity | Run targeted tests proving unchanged scope rows are skipped and `leaderboard_snapshot_state` stays pinned to the prior physical snapshot. | Tests pass and docs list exact read/write semantics. | Keep flag off; revert code if fixture parity fails. |
| S1.2 Live A/B setup | Deploy flag off as baseline, capture a full scrape baseline for snapshot row counts, disk growth, WAL/temp, public routes, and active snapshot state. | Baseline complete and public path healthy. | Block until baseline scrape completes or safe equivalent exists. |
| S1.3 Candidate scrape | Enable the flag only for the candidate scrape, run `fstworker` under 60-second monitor, and compare changed/new/unchanged scope manifests, active snapshot mapping, API JSON, counts, disk growth, WAL/temp, CPU, memory, and phase timings. | Exact output parity for representative solo routes and count/fingerprint parity for changed scopes; unchanged scopes safely pinned; meaningful reduction in physical snapshot rows/disk growth; no material resource regression. | Disable flag, revert if needed, document failed predicate. |
| S1.4 Promote or reject | If accepted, commit/push docs/config and leave flag enabled; if rejected, flag off and revert candidate changes. | Decision and rollback evidence recorded. | N/A |

Decision tier: high-value future-growth reduction; requires full live A/B before production default.

### Phase S2: low-scan non-structural index reclaim

Goal: drop or replace indexes that burn storage/write cost without owning active read/write paths.

Initial candidate families:

- `band_team_rank_history_points_v2_*_snapshot_id_band_type_idx` (~8.4 GB, zero scans in probes).
- `band_team_rank_history_latest_v2_*_snapshot_id_band_type_idx` (~3.1 GB, zero scans in prior probes).
- Current/published build-name indexes with zero scans only after owner review.
- Never treat primary keys or unique constraints as drop candidates without table/source-of-truth design approval.

| Task | Action | Acceptance gate | Rollback |
|---|---|---|---|
| S2.1 Owner cards | For each candidate index: table, size, scans, source references, query texts, endpoint/job owner, replacement coverage, write cost, and exact `CREATE INDEX CONCURRENTLY` rollback DDL. | Owner card proves non-structural index has no active read/write path or has a better replacement. | Mark index rejected/keep. |
| S2.2 Read-path plans | Run bounded `EXPLAIN` or safe `EXPLAIN (ANALYZE, BUFFERS)` for representative history/status/admin queries. | Plans do not require candidate index, or replacement path is equal/better. | Keep index. |
| S2.3 One-index-at-a-time drop | Drop one accepted non-structural index, validate public/API routes, logs, locks, disk, and fallback/recreate DDL. | Disk decreases by expected size; no API/job regression; rollback DDL tested syntactically. | Recreate index concurrently, document rejection. |
| S2.4 Batch only after proof | Repeat for next index only after prior decision is accepted/rejected and committed. | No unprocessed index decisions. | Stop if public path or DB load degrades. |

Decision tier: low-to-medium storage win, low data-risk, but must be one-object-at-a-time.

Execution result (2026-07-13): accepted for `public.ix_crh_latest`.
The production latest-state job chose an identical parallel sequential
scan/sort plan before and transactionally without the index; a forced index
plan cost 16.75x more. The concurrent drop reclaimed `20,890,148,864` bytes.
Scrape `1236` totals, stable route fingerprints, normalized solo export, band
history, composite rankings, and representative plans matched exactly.
Startup schema no longer recreates the retired index, and exact concurrent
recreate/drop SQL is covered by targeted tests. The lower-value partitioned
band v2 snapshot lookup family remains a future one-family candidate rather
than a second destructive batch in this phase.

Execution result (2026-07-15): accepted for
`public.ix_btrhlv2_snapshot`. The latest-state delta/read/write plans used only
the partition primary keys before and transactionally without the family.
The family drop reclaimed `3,277,996,032` database bytes and
`3,278,016,512` filesystem bytes. Twelve public
leaderboard/ranking/history/export fingerprints, sampled history for all three
band types, composite routes, scrape `1236` totals, and representative plans
matched exactly. Exact rollback SQL builds the three children concurrently,
creates the parent `ON ONLY`, and attaches the children. The larger
`ix_btrhpv2_snapshot` points family was evaluated only as a later separate
decision.

Execution result (2026-07-15): accepted for
`public.ix_btrhpv2_snapshot`. Public history and points parity plans retained
the same team/date indexes before and after; only an unowned direct
`snapshot_id` diagnostic used the retired family. The drop reclaimed
`8,864,440,320` database bytes and `8,864,481,280` filesystem bytes. Twelve
public leaderboard/ranking/history/export fingerprints, sampled history for
all three band types, composite routes, and scrape `1236` totals matched
exactly. Exact rollback SQL builds the three children concurrently, creates
the parent `ON ONLY`, and attaches the children.

### Phase S3: physical snapshot retention/source-of-truth package

Goal: unlock the largest storage surface by proving what physical snapshots must retain.

Do not prune yet. Build the proof package first.

| Task | Action | Acceptance gate | Rollback |
|---|---|---|---|
| S3.1 Source-of-truth classification | Classify each `leaderboard_entries_snapshot_*` generation as canonical, reconstructable from logical/current/version rows, required safety window, or disposable after publication. | Written matrix covers rankings, rivals/opps, player stats, notifications, exports, public-read freeze, and restore. | Block pruning until all consumers are classified. |
| S3.2 Manifest tooling | Produce per-scrape/instrument row counts, song counts, account counts, min/max ranks, checksums/fingerprints, size estimates, and restore/regeneration path. | Manifest generated for published scrape and at least one candidate old scrape. | Build tooling before prune. |
| S3.3 Logical/read parity | A/B representative leaderboard reads from physical snapshot vs logical/current compact source. | API JSON parity and rank/count parity. | Keep physical authoritative. |
| S3.4 Retention A/B | On live scrape, prove keeping latest published + safety window and pruning/archive candidates preserves public reads and rollback. | Live-scrape A/B data parity, restore path, exact object list, post-action validation. | Reject/keep physical snapshots. |

Decision tier: highest potential storage win; destructive prune auto-approved only after live-scrape A/B parity.

### Phase S4: band rank-history v2 compact schema and retention

Goal: reduce large band-history tables without losing user-visible history.

Design candidates:

- Future compact table with normalized `team_id` / `team_key` dictionary instead of repeated 97-132 byte `team_key`.
- Store `row_fingerprint` as 16-byte binary/uuid or generated compact hash instead of 33-byte text.
- Review whether `snapshot_id` lookup indexes are redundant.
- Retention policy for raw v2 slices after manifest/restore proof.

| Task | Action | Acceptance gate | Rollback |
|---|---|---|---|
| S4.1 Schema design | Draft compact schema, dictionary ownership, indexes, and write/update semantics. | Design preserves endpoint history semantics and latest-state delta detection. | Keep current v2 tables. |
| S4.2 Artifact/fixture A/B | Populate compact candidate for bounded band type/date/team sample on the FST drive. | Count/range/fingerprint/API parity for sample; measured storage per row lower; query latency not materially worse. | Drop candidate tables. |
| S4.3 Live shadow write | Feature-flag dual-write compact history for one band type/scope while current v2 remains authoritative. | No public output change; writer overhead acceptable; monitor WAL/temp/CPU/mem. | Disable flag/drop shadow. |
| S4.4 Promotion/retention | Promote compact reads or prune old v2 only after live A/B parity, restore, and rollback docs. | History route parity, storage win, no material processing cost. | Keep old reads/tables. |

Decision tier: high future win; must be semantics-first.

### Phase S5: current band projection compact layout

Goal: reduce current band projection footprint while preserving hot band leaderboard routes.

Largest repeated data today includes `team_members`, `member_account_ids`, `team_key`, and per-member arrays.

| Task | Action | Acceptance gate | Rollback |
|---|---|---|---|
| S5.1 Access-path inventory | Identify API routes/jobs using `current_band_leaderboard_entries_*`, required columns, ordering, selected-band overlays, and fallback paths. | Owner card complete. | No schema change. |
| S5.2 Narrow projection candidate | Design a narrow current projection with normalized band/team identity and only route-critical columns. | Query shapes and indexes defined from observed access paths. | Keep current projection. |
| S5.3 Shadow build | Build candidate for one band type/scope, compare route JSON and timings. | JSON parity; p95/CPU/IO not materially worse; storage per row lower. | Drop shadow. |
| S5.4 Live A/B | Feature-flag read source to compact projection for representative routes. | Public route parity and lower storage/write pressure. | Disable flag. |

Decision tier: medium/high derived-cache win; good candidate after S1/S2.

### Phase S6: rank/composite history index and retention review

Goal: reduce history/index storage without losing visible rank-history behavior.

| Task | Action | Acceptance gate | Rollback |
|---|---|---|---|
| S6.1 Owner/index review | For `rank_history_*` and `composite_rank_history`, map latest/history/retention/API owners and index usage. | Keep/drop/replacement decision per index. | Recreate DDL. |
| S6.2 Retention semantics | Define how old rank history should behave: exact raw, season-scoped, summarized, or archived. | Product/history semantics documented. | Keep all raw. |
| S6.3 Bounded retention A/B | Manifest/checksum old slices and test route parity before any prune. | Live-scrape A/B parity and restore path. | Reject prune. |

Decision tier: medium storage win; semantics-gated.

### Phase S7: cache/precompute and optional derived projection policy

Goal: keep API pressure low without rebuilding huge optional projections when disk/headroom is unsafe.

| Task | Action | Acceptance gate | Rollback |
|---|---|---|---|
| S7.1 Band-song projection policy | Make optional band-song projection rebuild pressure-aware: rebuild only when disk/WAL/temp budget allows; otherwise serve stale-safe fallback. | Route parity, no stale current projection reads, measurable API impact understood. | Keep default-disabled rebuild. |
| S7.2 Public cache ownership | Keep `api_response_cache` writes in precompute/staging/publish paths, not live GET misses. | Public cache hits/freeze behavior intact. | Re-enable live writes only with measured need. |
| S7.3 Hot routes | Only after measured evidence, consider `/api/status` counters, `/api/songs` split payloads, and member-score batching. | Response parity and lower DB/API pressure. | Revert route optimization. |

Decision tier: operational/API pressure win, not primary storage reclaim.

### Phase S8: bloat maintenance only after safer reclaim

Goal: reclaim bloat without creating scratch-space or lock risk.

Current evidence after refreshed `ANALYZE`: candidate dead-tuple estimates are roughly 4.93-17.17%, not the stale 99% signal.

| Task | Action | Acceptance gate | Rollback |
|---|---|---|---|
| S8.1 Plain maintenance | Run bounded `ANALYZE` and consider plain `VACUUM` only when it helps stats/dead tuple reuse. | No public impact; no claim of filesystem reclaim. | Stop if load degrades. |
| S8.2 Repack/rewrite readiness | Only after S1/S2/S3 headroom and live parity, choose exact table, scratch needs, locks, and rollback. | 4 TB FST-drive scratch available; API health plan; parity. | Do not run. |
| S8.3 Repack/rewrite execution | Execute one object at a time in a maintenance window with 60-second monitor. | Relation size down, route parity, lock duration acceptable. | Restore/rebuild if needed. |

Decision tier: later maintenance; not current first-line storage strategy.

### Phase S9: promotion, reports, and next-loop insertion

Goal: ensure accepted changes are persistent and rejected work is not left dirty.

| Task | Action | Acceptance gate |
|---|---|---|
| S9.1 Commit/push accepted changes | Commit code/config/docs/DDL scripts for accepted candidates with evidence. | Push succeeds; working tree clean except unrelated preserved work. |
| S9.2 Render reports | Use `node tools/agent-report-email.mjs` for each phase and final recap. | Outbox/send artifact exists. |
| S9.3 Residual sweep | Classify every rejected/blocked/caveated item and insert safe derivative work. | No safe unprocessed follow-up remains. |

### [x] Phase K: API service redeploy and recovery check (2026-07-07T00:08:00Z)

Mode: Promotion readiness / live-safe deployment. No database schema, data rows, indexes, tables, scrapes, or worker state were mutated.

Deployment evidence:

| Step | Result | Decision |
|---|---|---|
| Build | `docker build -f FSTService/Dockerfile -t fstservice:sticky-rank-history-tracking .` completed successfully. | Accepted; image includes Phase F-J service changes. |
| Service recreate | `/home/sfenton/Docker/FestivalServiceTracker` `docker compose up -d --no-deps fstservice` recreated only `fstservice`. | Accepted; `fstworker` was not started. |
| Recovery | `fstservice` `/readyz` returned `Healthy`; `festivalweb` remained healthy and served the app shell; `fst-postgres` stayed healthy. | Accepted. |
| Disk | `/mnt/docker-storage` remained about 77 GB free and 98% used after build/redeploy. | Still insufficient for full scrape/rewrite/repack work. |

Deployment decision:

- Accepted for the API/service portion of the changes.
- `fstworker` remains stopped after the prior disk-exhaustion failure; a full worker scrape/restart remains gated by storage headroom and live-scrape A/B readiness.
- Production compose/.env defaults are staged so the next intentional worker recreate uses `ComboBatched` band ranking writes and skips optional band-song projection rebuilds by default.

### [x] Phase L: Worker validation start rejected by public API health gate (2026-07-07T00:40:00Z)

Mode: Promotion readiness / live-safe worker validation. No scrape completed, no schema/data/index/table mutation was accepted, and `fstworker` was stopped to restore public API health.

Validation attempt:

| Step | Result | Decision |
|---|---|---|
| Pre-start config check | Active compose resolved `RUN_ONCE=true`, `ENABLED_PHASES=All`, `BandTeamRankings__WriteMode=ComboBatched`, and `BandTeamRankings__RebuildBandSongTeamRankings=false`. | Safe enough to attempt worker validation with close monitoring. |
| Worker recreate | `docker compose -f docker-compose.yml -f docker-compose.pia-30.yml up -d --no-deps --force-recreate fstworker` started `fstworker`. | Validation started. |
| Public path monitor | `festivalweb` static shell returned 200, but `fstservice` `/readyz` and `/api/service-info` through `festivalweb` timed out; nginx logged upstream 504s for `/api/service-info`. | Rejected. Static web health alone is insufficient when API-backed routes are timing out. |
| Recovery action | `docker compose ... stop fstworker` stopped the worker. | Accepted rollback. |
| Recovery validation | `fstservice` `/readyz` returned `Healthy`; `festivalweb` static shell returned 200; `/api/service-info` through `festivalweb` returned 200; Postgres had no ungranted locks. | Public API/web health restored. |

Observed cause/evidence:

- `fstworker` startup resumed band rank-history background job `456` for `Band_Trios` scrape `1214`.
- The worker was stopped while `StartupInitializer`/`DatabaseInitializer.EnsureSchemaAsync` was still running, so worker logs ended with `57014: canceling statement due to user request`.
- During the attempt, `fstservice` became unhealthy/timeouts from the public path, so this worker start path is not acceptable without additional isolation or a safer startup/maintenance plan.

Worker validation decision:

- Rejected/blocked until a safer worker start path can prove `fstservice` and `festivalweb` API routes stay healthy after all expected containers return.
- The autonomous executor skill now explicitly requires post-restart/redeploy verification of Docker health, `fstservice` `/readyz`, `festivalweb` static shell, and a representative API route through `festivalweb`, and immediate rollback if `fstworker` breaks API/web health.

### [x] Phase M: Worker scraping healed with schema-init skip (2026-07-07T04:52:00Z)

Mode: Implementation / live-safe worker recovery. No destructive database action was performed.

Root cause:

| Finding | Evidence | Decision |
|---|---|---|
| `fstworker` startup ran global schema initialization even though `fstservice` had already initialized the database. | The rejected worker attempt made `fstservice` and `/api/service-info` through `festivalweb` time out while worker logs showed `StartupInitializer` / `DatabaseInitializer.EnsureSchemaAsync`. | Make schema initialization skippable for worker containers that start after the API service. |

Implementation:

| Change | Files/artifacts | Safety gate | Rollback |
|---|---|---|---|
| Added `Scraper:SkipStartupSchemaInitialization`, default `false`. | `FSTService/ScraperOptions.cs`, `FSTService/StartupInitializer.cs`, `FSTService.Tests/Unit/ScraperOptionsAndModelsTests.cs` | API/frontend defaults still run schema init; worker can opt out. | Set the option to `false`. |
| Added compose template env passthrough. | `docker-compose.yml`, `deploy/docker-compose.yml` | Defaults to `false` in repo templates. | Remove or leave unset. |
| Set active worker runtime override. | `/home/sfenton/Docker/FestivalServiceTracker/docker-compose.pia-30.yml`, `/home/sfenton/Docker/FestivalServiceTracker/.env` | Active worker uses `SKIP_STARTUP_SCHEMA_INITIALIZATION=true`; API service does not depend on this override. | Set `SKIP_STARTUP_SCHEMA_INITIALIZATION=false` after verifying worker/API behavior. |

Validation:

| Check | Result |
|---|---|
| Targeted test | `dotnet test FSTService.Tests/FSTService.Tests.csproj --filter "FullyQualifiedName~ScraperOptionsAndModelsTests"` passed 21 tests. |
| Build | `docker build -f FSTService/Dockerfile -t fstservice:sticky-rank-history-tracking .` completed successfully. |
| Worker start | `fstworker` started and became healthy. |
| Public-path watchdog | 60 ticks over about 10 minutes all returned `web_shell=200`, `web_api=200`, and `api_ready=Healthy`; no ungranted Postgres locks were observed. |
| Scrape state | `service_worker_status` showed `running/Worker ready` with current operation `scrape.leaderboards`; `scrape_log` created run `1219` at `2026-07-07 04:42:37Z`. |
| Disk | `/mnt/docker-storage` stayed about 83-87 GB free, 98% used during the watchdog window. |

Worker heal decision:

- Accepted. `fstworker` is running and scraping while `fstservice` and `festivalweb` remain healthy through the full public path.
- Continue the 60-second public-path monitor while scrape `1219` progresses.
- Storage remains tight; if the worker enters DB-heavy post-processing and public-path health degrades, stop/rollback the worker immediately and record the exact phase.

### [x] Phase N: Emergency `band_read_*` reclaim after scrape 1221 filled disk (2026-07-07T15:27:00Z)

Mode: Emergency storage reclaim / live-safe recovery. This phase executed a destructive drop only for the previously proven unused derived `band_read_*` projection group, after quarantine and public-route validation.

Incident trigger:

| Signal | Evidence | Immediate action |
|---|---|---|
| FST drive full | `/mnt/docker-storage` and Postgres data mount reached 44 MB free / 100% used while `fstworker` was running. | Stopped `fstworker` to prevent more DB writes. |
| Public path still recoverable | `festivalweb` shell, `/api/service-info` through `festivalweb`, and `fstservice` `/readyz` returned 200/Healthy after worker stop. | Kept API/web live. |
| Scrape state | Scrape `1221` was incomplete/unpublished; `published_scrape_id` remained `1214`. | Froze public reads back to published scrape `1214`. |
| Docker cache check | `docker builder prune` reclaimed root-Docker cache but did not change `/mnt/docker-storage`; FST pressure was Postgres data. | Proceeded to DB object inventory. |

Reclaim actions:

| Step | Result | Safety evidence |
|---|---|---|
| Logical shadow cleanup | `TRUNCATE leaderboard_current_entries, leaderboard_entry_versions, leaderboard_logical_write_metrics` recovered about 37 GB. | These are rollback/experimental logical artifacts, not authoritative public reads. |
| `band_read_*` inventory | `band_read_hot_window` 191 GB, `band_read_subject_row` 190 GB, `band_read_rank_anchor` 12 GB, `band_read_scope_state` 5,459 MB, plus tiny metadata tables. | Prior phases found no source/view/proc/runtime route owners; constraints were local PK/check only. |
| Quarantine | Renamed all six `band_read_*` tables to `quarantine_band_read_*_20260707`. | Public shell, `/api/service-info`, `/api/songs`, representative band leaderboard, and `/readyz` all returned healthy/200. |
| Drop | Dropped only the quarantined `band_read_*` tables. | Post-drop public shell, service-info, representative band route, and `/readyz` all returned healthy/200. |
| Headroom | `/mnt/docker-storage` recovered to about 435 GB free / 89% used. | Enough to restart worker under watchdog. |
| Worker recovery | Restarted `fstworker`; scrape `1222` began; 18-tick public-path watchdog passed with `web_shell=200`, `web_api=200`, `api_ready=Healthy`, no ungranted locks. | Public path stayed healthy and `band_read_remaining=0`. |

Emergency reclaim decision:

- Accepted. The `band_read_*` group was derived, previously unused by current code/routes, and validated through quarantine before drop.
- Rollback after drop would require regeneration/restore of obsolete derived projection data, but no active route or worker path referenced it before or after quarantine.
- Continue monitoring scrape `1222`; if disk again approaches critical levels during post-processing, stop worker and record the exact phase before further reclaim.

## Prioritization principles

1. Reclaim space first where the surface is likely derived and correctness risk is low.
2. Reduce write amplification before running another full scrape eval.
3. Do not trade permanent storage correctness for temporary free space.
4. Prefer read-only proof, manifests, parity checks, and reversible config/index changes before destructive work.
5. Separate "immediate free space" work from "future scrape cost" work; both matter, but the disk blocker must be cleared first.
6. All work must remain on the 4 TB FST drive. Alternate-drive data, scratch, migration, export, or repack workspace is prohibited unless SFenton explicitly overrides this rule later.

## Risk-adjusted priority order

### Priority 0: freeze the current safe operating posture

Goal: keep production stable while reclaim work is planned.

Status and rules:

- `fstservice` and `fst-postgres` remain healthy.
- `festivalweb` remains healthy and users can use the app against the last published scrape.
- Published scrape remains `1214`.
- Public reads remain in the current safe publication state and must be checked before maintenance.
- Scrapes proceed normally unless a specific maintenance step temporarily pauses worker/service/web.
- No destructive cleanup, `VACUUM FULL`, `pg_repack`, table rewrite, data move, or index drop happens from this plan alone.

Validation:

- Confirm service health, publication state, public-read freeze state, disk free, and absence of dangerous locks before parity-gated work.
- Confirm `fstservice` `/readyz`, `festivalweb` health, and at least one browser-visible app route after any service/web redeploy.

### Priority 1: prove and reclaim stale/derived band read projections

Goal: reclaim the best risk-adjusted space first.

Target surfaces:

- `band_read_hot_window`
- `band_read_subject_row`
- `band_read_rank_anchor`
- `band_read_scope_state`
- related `band_read_*` indexes

Why first:

- Observed group size is about 398 GB.
- Multiple large indexes showed zero scans.
- Repository search did not find active code references for several `band_read_*` table names.
- If these are obsolete derived projections, reclaim could be large with lower correctness risk than physical snapshot deletion.

Required proof before action:

- Confirm tables are not referenced by current deployed code, stored procedures, views, scheduled jobs, or API endpoints.
- Check `pg_depend`, view definitions, prepared jobs, and `pg_stat_statements` for table references.
- Capture row counts, size by table/index, min/max generation/scrape/date fields, and a manifest.
- Confirm published API responses do not depend on these tables.

Allowed candidate actions after live-scrape A/B parity:

- Rename quarantine first, then observe API/service behavior.
- Drop only after a rollback/restore path and observation window.
- Prefer dropping unused indexes before dropping tables if table ownership is uncertain.

Success metrics:

- Reclaimed bytes.
- No API/public-read regression.
- No failed queries referencing quarantined objects.

Decision tier: highest priority, proof-first.

### Priority 2: prove, replace, or drop low-scan giant indexes

Goal: reclaim index storage and reduce write/index-maintenance overhead without changing row ownership.

Target surfaces:

- Band rank-history points v2 low-scan indexes.
- Band read projection indexes.
- Composite/rank-history low-scan indexes.
- Current/published band ranking indexes with build-table-derived names and zero scans.
- `scrape_dirty_band_team` indexes if the table is empty/obsolete after current phase.

Why second:

- Potential reclaim is likely hundreds of GB.
- Dropping unused indexes can reduce future writes, WAL, vacuum, and checkpoints.
- Index drops are usually easier to roll back than data deletion, but rebuilds can be expensive and need disk.

Required proof before action:

- For each index: table, size, `idx_scan`, query texts, API endpoint owner, and whether the primary key or another index covers the path.
- Run plain `EXPLAIN` for representative reads; use `EXPLAIN ANALYZE` only in a safe bounded window.
- Confirm no maintenance or retention job needs the index.

Allowed candidate actions after live-scrape A/B parity:

- Drop clearly unused noncritical indexes one at a time.
- Replace broad btree indexes with narrower, partial, or differently ordered indexes only after matched query-plan proof.
- Use `CREATE INDEX CONCURRENTLY` for replacements on large live tables when required.

Success metrics:

- Reclaimed bytes.
- Lower WAL/index writes in the next scrape.
- No p95/p99 read regression for affected endpoints/jobs.

Decision tier: high priority, maintenance-window likely for some replacements.

### Priority 3: define source-of-truth and retention for physical leaderboard snapshots

Goal: unlock the largest storage win without breaking historical correctness.

Target surfaces:

- `leaderboard_entries_snapshot_*` partitions.
- Snapshot indexes, especially primary keys and score indexes.
- `leaderboard_snapshot_state`.
- Logical current/version tables and scope fingerprints used for parity proof.

Why third:

- Physical snapshots are the largest observed storage group at about 1.58 TB.
- They are wide full-row copies and duplicate much of `leaderboard_entries`, `leaderboard_current_entries`, and `leaderboard_entry_versions`.
- They are high correctness risk until logical/current reads and restore semantics are proven.

Required proof before action:

- Decide whether each snapshot generation is canonical, reconstructable, or disposable after publication.
- Prove logical current/version tables can reproduce representative leaderboard reads.
- Prove ranking, rivals/opps, player stats, improvement notifications, band history dependencies, API response cache, and exports remain correct.
- Build manifest coverage: scrape IDs, row counts, song/instrument counts, checksums/fingerprints, byte sizes, and restore path.

Allowed candidate actions after live-scrape A/B parity:

- Archive old snapshots only to validated locations on the 4 TB FST drive, verify manifest, then prune only after restore proof and live-scrape A/B data parity.
- Keep latest published and recent safety window on FST drive.
- Consider time/scrape-range partitioning for future snapshot retention if physical snapshots remain.

Success metrics:

- Reclaimed bytes, likely the largest possible win.
- Exact API parity for representative and sampled full-scope reads.
- Restore/rehydration time documented and tested.

Decision tier: high-impact but blocked until live-scrape A/B data parity.

### Priority 4: band rank-history points retention and index redesign

Goal: reduce the 799 GB band history surface while preserving user-visible history.

Target surfaces:

- `band_team_rank_history_points_v2_*`
- `band_team_rank_history_latest_v2_*`
- `band_team_rank_history_snapshot_v2`
- associated snapshot/team/date indexes

Why fourth:

- The surface is very large and has high index/toast overhead.
- Some large indexes have very low scan counts.
- Trios and Duets points show high dead tuple ratios.
- History is user-visible, so retention decisions are semantic, not purely technical.

Required proof before action:

- Define retention policy: all history forever, daily coalesced history, season-scoped history, or cold archive.
- Prove API history endpoints and status/freshness reads with representative teams/scopes.
- Verify whether low-scan indexes are unused or only used by rare admin/repair paths.
- Confirm whether row fingerprints and latest state can avoid same-day duplicate history writes.

Allowed candidate actions after live-scrape A/B parity:

- Drop/replace unused history indexes.
- Partition by time only for future layout or after a controlled migration.
- Archive old points by date/scope only with manifest and restore proof.
- Repack/vacuum only after sufficient scratch space exists on the 4 TB FST drive.

Success metrics:

- Reclaimed bytes and lower index write cost.
- Band history API parity.
- Reduced history snapshot wall clock, WAL, and temp reads.

Decision tier: high-impact, semantics-gated.

### Priority 5: Phase 8 unchanged row/scope physical write skipping

Goal: reduce future storage growth, WAL, CPU, and I/O for every scrape.

Starting evidence:

- Phase 7 observed 39,385,606 rows.
- 27,178,074 rows were unchanged, or 69.01%.
- Current upserts / versions opened were 12,207,532.
- Several instruments had more than 80% unchanged rows.

Target write paths:

- Solo snapshot insert from `_le_staging`.
- Legacy live `leaderboard_entries` merge when enabled.
- Logical current/version write path.
- Band writes and member stats when scope fingerprints prove unchanged.
- Projection refreshes that currently delete/reinsert unchanged scopes.

Candidate design:

- Add scope-level fingerprints before expensive staging/merge where possible.
- Skip full physical snapshot writes for unchanged scope generations while keeping `leaderboard_snapshot_state.active_snapshot_id` pinned to the previous physical snapshot for those scopes.
- Skip row writes when row fingerprint matches logical current state.
- Keep physical snapshots authoritative until parity is proven.
- Preserve rollback by feature flag and by retaining old read paths.

Validation:

- Unit tests for new/changed/unchanged classification.
- A/B fixture benchmark with matched data and resource caps.
- Production eval only after disk headroom.
- Measure wall clock, WAL bytes, rows inserted/updated/deleted, temp bytes, CPU, memory, locks, and API parity.

Decision tier: essential future-cost reduction after reclaim headroom.

Current implementation status:

- Default-off candidate implemented as `Features:SkipUnchangedPhysicalLeaderboardSnapshots`.
- Unit proof accepted for new/unchanged/changed scope behavior.
- Production enablement remains gated by live-scrape A/B parity, public-read/publish/freeze semantics, and sufficient disk headroom for the evaluation.

### Priority 6: rank/temp spill and ranking rebuild reduction

Goal: reduce database work even when storage is not being reclaimed.

Starting evidence:

- `pg_stat_database.temp_bytes` showed about 3,354 GB.
- Top temp writers include `_valid_entries`, `_latest_ranks`, `_band_rank_results`, `_band_song_rank_results`, and index builds on temp/build tables.

Target surfaces:

- Solo rank recomputation.
- Composite rank history.
- Band aggregate ranking rebuilds.
- Band song/team ranking build tables.
- Current/published ranking table index creation.

Candidate design:

- Reduce repeated temp-table materialization.
- Precompute or persist narrow current-state inputs.
- Avoid rebuilding rank outputs when source scope fingerprints are unchanged.
- Cap concurrent DB-heavy phases end-to-end, not just ranking internals.
- Evaluate whether sort keys/indexes can be narrower or partition-local.

Validation:

- Temp bytes per phase.
- Ranking wall clock by phase/instrument/band type.
- CPU, memory, disk I/O, and lock waits.
- Rank parity and API parity.

Decision tier: high database-work priority; not first for free-space blocker.

Current implementation status:

- `ComboBatched` is now the default band team ranking write mode in code, appsettings, repo compose templates, and the active production compose/.env default for the next worker recreate.
- Unit parity confirms `ComboBatched` produces the same band ranking output as `Monolithic` and `Phased` on the fixture.
- Optional band-song projection rebuilds are now disabled by default and stale current projection rows fall back to current projection/live computation when current band rankings are newer.

### Priority 7: dead tuple/bloat maintenance after reclaim headroom

Goal: reclaim table bloat only after enough free space exists and object ownership is proven.

Target candidates:

- `band_members`
- `band_member_stats`
- `band_search_member_projection`
- `band_search_team_projection`
- `band_entries_duets`
- `band_team_rank_history_points_v2_trios`
- `band_team_rank_history_points_v2_duets`
- selected rank-history partitions

Current readiness evidence:

- P7 repaired stale stats with bounded `ANALYZE` on 2026-07-06.
- Current dead-tuple estimates are about 16-17% for `band_members` / `band_member_stats`, 13.84% for `composite_rank_history`, 5-11% for `band_search_*`, `band_entries_duets`, and v2 band-history candidates, and 0 for empty `rank_history`.
- The old "about 99%" dead-tuple signal should not be used for maintenance approval without fresh corroborating evidence.

Why later:

- `VACUUM FULL`, repack, or table rewrites can need locks and scratch space; this scratch space must be on the 4 TB FST drive unless SFenton explicitly overrides this rule later.
- The system currently has too little headroom for risky rewrite work.
- Some surfaces may be better solved by dropping obsolete derived tables or indexes first.

Allowed candidate actions after live-scrape A/B parity:

- Plain vacuum/analyze where safe.
- `pg_repack` only with 4 TB FST-drive scratch-space, live-scrape A/B data parity, and maintenance-window validation.
- Rebuild derived projections from source if cheaper than repacking.

Validation:

- Before/after relation size.
- Dead tuple ratio.
- Query/runtime parity.
- Lock duration and API health.

Decision tier: important but after safer reclaim.

### Priority 8: hot read-path and cache pressure reduction

Goal: reduce steady-state DB CPU/I/O and cache churn.

Target surfaces:

- `/api/status` instrument counts.
- `/api/songs` JSON composition.
- `/api/leaderboard/{songId}/members/scores` fan-out.
- Public API response cache writes.
- Player profile fallback reads.

Candidate design:

- Replace `COUNT(*)` status paths with maintained per-instrument scrape counters.
- Split `/api/songs` base catalog from stats overlays or precompute publication-keyed payloads.
- Batch current-state fallback/profile reads.
- Make public cache publication-keyed and write primarily during publish/precompute, not on every live cacheable GET. (Phase J completed the live middleware no-store portion.)
- Keep selected-player/band overlays outside public static cache unless explicitly keyed.

Validation:

- Query count per request.
- p50/p95/p99 latency.
- Response byte size and serialization time.
- `api_response_cache` write rate and bytes.
- Exact response parity for representative requests.

Decision tier: lower immediate storage reclaim, good operational efficiency.

## Architecture evaluation backlog

| Track | Target | Baseline evidence | Candidate change | Success metric | Safety gate |
|---|---|---|---|---|---|
| Space reclaim | `band_read_*` projections | 398 GB group; many 0-scan indexes | Prove ownership; quarantine/drop stale derived surfaces | Reclaimed bytes | API/query reference proof and rollback |
| Space + write cost | Low-scan giant indexes | 100 GB+ indexes with near-zero scans | Drop/replace after plan proof | Reclaimed bytes and lower WAL/index writes | Representative `EXPLAIN` and endpoint parity |
| Largest storage | Physical snapshots | 1.58 TB group | Archive/compact/prune after logical parity | Reclaimed bytes | Manifest, restore, API/ranking parity |
| History storage | Band rank-history v2 | 799 GB group | Retention/index redesign | Storage and history job time down | History API parity |
| Future scrape cost | Unchanged row/scope writes | 69.01% unchanged in P7 | Scope/row write skipping | WAL/rows written down | Full scrape parity |
| Temp/CPU/I/O | Ranking temp tables | 3,354 GB temp bytes | Reduce temp materialization/rebuilds | Temp bytes and wall clock down | Rank/API parity |
| Bloat | High-dead derived/history tables | Refreshed P7 stats show about 4.93-17.17% dead tuples on candidates | Vacuum/repack/rebuild after headroom | Relation size down | Parity/maintenance gate |
| Hot reads | Status/songs/member-score/cache paths | Count scans, fan-out, live cache writes | Maintained counters/projections, batched reads | p95/query count down | Response parity |

## Required proof package for every reclaim action

Before any parity-gated reclaim action, produce a short proof package:

| Required item | Purpose |
|---|---|
| Object inventory | Tables/indexes, sizes, row counts, dead tuples, dependencies, and owners. |
| Access evidence | `pg_stat_user_indexes`, `pg_stat_statements`, source references, endpoint/job ownership, and representative query plans. |
| Correctness gate | API parity, row count/range/checksum/fingerprint parity, or manifest coverage depending on object type. |
| Rollback path | Rename-back, recreate index DDL, restore archive, regenerate projection, or read-source flag. |
| Maintenance risk | Expected locks, 4 TB FST-drive scratch need, WAL/temp impact, service health risk, and worker state. |
| Parity gate statement | Live-scrape A/B evidence proving the new path has the same data as the old path for the exact object/action. |

## Do-not-do list until live-scrape A/B parity passes

- Do not leave `fstworker`, `fstservice`, or `festivalweb` down after maintenance; redeploy/recover them as soon as possible.
- Do not intentionally leave normal scrapes disabled as a safety posture; scrapes should proceed unless an active maintenance step temporarily pauses them.
- Do not delete/prune historical data until live-scrape A/B parity proves the new path has the same data as the old path.
- Do not drop indexes or tables until live-scrape A/B parity proves the new path has the same data as the old path.
- Do not run `VACUUM FULL`, `CLUSTER`, `pg_repack`, or broad rewrites until live-scrape A/B parity, rollback, disk/resource, and post-action validation gates are documented.
- Do not move active Postgres data off the 4 TB FST drive; the 4 TB drive rule still applies to parity-gated destructive work.
- Do not use alternate-drive space for data, scratch, migration, export, or repack workspace unless SFenton explicitly overrides this rule later.

## Evaluation cadence for future phases

For eval phases:

1. Confirm `fstservice` and `fst-postgres` health, public-read freeze state, published scrape, disk headroom, and absence of dangerous long queries.
2. Start or deploy only the selected candidate.
3. Monitor every 60 seconds with visible status: scrape ID, phase/status, elapsed wall clock, DB locks/long queries, disk free, CPU, memory, and relevant write metrics.
4. When scrape/post-process/publish gates complete, stop `fstworker` before the next automatic scrape starts.
5. Wait for post-publish autovacuum or known cleanup to clear when relevant.
6. Evaluate against the target wall-clock, I/O, CPU, memory, correctness, and publication gates.
7. Commit and push passing phases; reject or revert failed phases; then continue to the next autonomous task or stop at the exact hard gate.

## Success criteria

Near-term success:

- Storage headroom is safe enough for a full eval without emergency cleanup.
- P7 metrics are preserved in docs and used to design P8.
- No public-read regression and no accidental worker scrape.

Medium-term success:

- Physical write volume falls materially for unchanged rows/scopes.
- Wall clock is maintained or improved.
- WAL, disk I/O, CPU, and memory pressure are significantly lower.
- Publication correctness and API parity remain proven.

Long-term success:

- FST stores leaderboard history in a compact, auditable source-of-truth model.
- Massive physical snapshots become either unnecessary, bounded, or safely retained as derived/rebuildable artifacts.
- Database operations have repeatable probes, manifests, rollback paths, and documented parity gates.
