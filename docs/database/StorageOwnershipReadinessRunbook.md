---
status: living-runbook
owner: data
last_verified: 2026-08-11
last_verified_commit: 453fd9b6
sources:
  - FSTService/Persistence/
  - docs/roadmap/data.md
update_triggers:
  - Storage ownership, active readers/writers, cleanup readiness, evidence, or promotion gates change.
---

# Storage Ownership Readiness Runbook

## Current decision

**Tier:** P6 observation data retirement, BAND-SONG-PROJECTION repository
writer/schema-creation retirement, and P8 dirty-work reclaim are complete.
P6 and exact band-song physical objects await cleanup-image full-scrape parity;
P9 legacy rows remain blocked on active readers, supplemental writers, and
their own live parity gate.

The original 2026-07-26 readiness phase owned storage-planner queue items P6,
P8, and P9 while Epic device authentication blocked the next live scrape.
Runtime was `gpt-5.6-sol`, reasoning `max`, context `long_context`. That
readiness phase started no worker and mutated no production row, index, schema,
or config.

Evidence:
`/mnt/docker-storage/Docker/FestivalServiceTracker/fst-data/evidence/storage-ownership-20260726T013551Z`.

ORPHAN-RECLAIM follow-through on 2026-07-27 independently recaptured the P8
manifest, proved 27 later successful scrapes culminating in published `1236`,
and truncated all four `scrape_dirty_*` tables without `CASCADE`. Empty schemas
and primary keys remain. The same phase left P6 and P9 intact. Evidence:
`/mnt/docker-storage/Docker/FestivalServiceTracker/fst-data/evidence/orphan-reclaim-20260727T193224Z`.

OBSERVATION-RETIRE follow-through on 2026-07-28 proved both observation writers
were disabled for published scrape `1267`, classified every current
`WITH`/`SELECT` statement as an ownership probe rather than a production
reader, and captured two stable `13/13` public suites with zero observation
read/write delta. A rollback-only rehearsal and a 1.23-second short-timeout
transaction then truncated only `public.player_score_observations` without
`CASCADE`. The empty table, union view, two indexes, primary key, and sequence
remain. Database size fell by `12,682,330,112` bytes; stable filesystem free
space rose by `12,680,921,088` bytes to about `212.04 GB`. Immediate and
60-second public captures remained `13/13` exact HTTP `200`, while published
`1267` stayed unfrozen. Evidence:
`/mnt/docker-storage/Docker/FestivalServiceTracker/fst-data/evidence/observation-retirement-20260728T184629Z`.

Repository follow-through removes both observation writer implementations,
their tracked config keys, and startup creation of the table, unique source
index, union view, primary key, and sequence. Fresh schemas exclude all
observation objects while retaining `score_history`, band facts/statistics,
notification behavior, and the checked-in rehydrate/drop evidence. This code
change performs no live DDL; existing physical objects remain until a cleanup
image completes one full scrape with publication and public-fingerprint
parity.

The combined exact physical cleanup package is now prepared at
`tools/postgres-retired-schema-cleanup.sh` and
`docs/database/RetiredPhysicalSchemaCleanupRunbook.md`. Its observation scope
is exactly the union view, owned sequence, and empty table; it does not select
`score_history`, band facts, notifications, or any other audit/history table.
Execution remains blocked on accepted scrape-`1278` publication/unfreeze and
fingerprint parity.

Live preflight at `2026-07-26T01:35:54Z`:

| Gate | Result |
|---|---|
| Public path | Postgres, `fstservice`, `festivalweb`, `/readyz`, shell, and `/api/service-info` healthy |
| Publication | Published `1236`, unfrozen |
| Worker | Offline; do not start until Epic device login succeeds |
| Database activity | No active scrape, ungranted lock, vacuum, index build, rewrite, or long query |
| Capacity | `48,951,795,712` bytes free at preflight; 99% used; observation accepted with capacity alert |
| Production mutation | None |

The final guard measured `52,103,634,944` free bytes after failed-query temp
files and WAL aged out. Database size was unchanged at `3,829,206,619,827`
bytes; this was not a reclaim action.

A later post-`1265` low-scratch phase retired only the four dormant
non-constraint secondary index trees on the disabled logical shadow.
Immediate free space reached `67,148,181,504` bytes and the corrected
`60,392,999,803`-byte full-run start requirement passed with about `6.75 GB`
of margin. At that point the separate P6/P8/P9 table-data gates remained
intact; ORPHAN-RECLAIM later cleared only P8. Evidence:
`/mnt/docker-storage/Docker/FestivalServiceTracker/fst-data/evidence/post-scrape-1265-capacity-recovery-20260727T0011Z`.

The remaining populated P9 surface is `40,825,225,216` bytes.

## Separate BAND-SONG-PROJECTION retirement

This P6/P8/P9 readiness decision did not authorize those three surfaces.
A later, independent owner card did clear and retire the stale optional
`band_song_team_rankings*` data:

- four standalone data tables, `36,747,099` rows,
  `28,315,639,808` pre-retirement bytes;
- rebuild disabled, no database/external dependency, successful scrape `1236`
  published with the optional rebuild skipped;
- exact published fallback/fail-closed route parity plus two rolled-back live
  truncates;
- exact schema and `2,184,507,134`-byte compressed data archive retained on
  the FST drive;
- `28,315,533,312` database bytes reclaimed, with schema, indexes, TOAST, and
  the three-row state ledger retained.

Repository follow-through removes the disabled rebuild option and writer,
legacy optional projection reader, rebuild-only metrics/tests, tracked
appsettings/Compose keys, maintenance watch ownership, and startup creation of
the exact retired relations and legacy indexes. Fresh schemas exclude
`band_song_team_rankings`, `band_song_team_ranking_state`,
`band_song_team_rankings_current_band_duets`,
`band_song_team_rankings_current_band_trios`, and
`band_song_team_rankings_current_band_quad`; the published
`current_band_leaderboard_entries` plus `band_current_projection_scope`
fallback and fail-closed generation gates remain. This code change performs no
live DDL. Existing physical copies, indexes, TOAST objects, and the state
ledger await one cleanup-image full scrape with publication and exact public
fingerprint parity before physical removal.

The prepared combined package names exactly these five relations and no active
band current/ranking table. It inventories their owned indexes/TOAST objects,
drops only the five named tables as part of the all-family atomic transaction,
and preserves the exact three-row state ledger as canonical, hashed rollback
data. See
`docs/database/RetiredPhysicalSchemaCleanupRunbook.md`.

P9 remains governed by its unchanged gate below; P6 and P8 were later
executed. Evidence:
`/mnt/docker-storage/Docker/FestivalServiceTracker/fst-data/evidence/band-song-projection-retirement-20260726T103231Z`.

## Owner-card summary

| Planner | Surface | Bytes | Owner decision | Growth posture | Execution class | Priority |
|---|---|---:|---|---|---|---|
| P6 | `player_score_observations` | `24,576` after truncate | Non-authoritative duplicate/audit surface; no production reader | Writers, tracked config, and startup schema creation removed; existing physical objects await cleanup-image full-scrape parity | accepted code retirement; parity-gated physical cleanup | code complete, physical cleanup pending |
| P8 | `scrape_dirty_*` | `65,536` after truncate | Abandoned work/audit state from scrapes `926`-`1146`; no current repo/runtime owner found | ORPHAN-RECLAIM executed; family remains fully empty | accepted maintenance | complete |
| P9 | `leaderboard_entries_*` | `40,825,225,216` | Legacy mutable rollback/fallback surface with active supplemental writers and a publication-critical worker reader | Main scrape writer is off; supplemental writer switch remains on until reader migration | `full-scrape-ab`, then `parity-gated-maintenance` | 3 |

## P6: `player_score_observations`

### Owner card

| Requirement | Evidence / decision |
|---|---|
| Pre-action physical shape | `10,167,937` rows; `6,774,161,408` heap bytes; `5,906,309,120` index bytes |
| Source distribution | `9,938,912` `band-member` rows (`97.75%`); `229,025` `solo-history` rows (`2.25%`) |
| Solo writer | Removed from `MetaDatabase`; durable `score_history` writes are unchanged |
| Band writer | Removed from `BandLeaderboardPersistence` and `BandSpoolWriterFactory`; durable `band_entries`/`band_member_stats` writes are unchanged |
| Reader/view | Existing `player_score_observation_union` is the only database dependency. No production API/export/code reader remains |
| Database dependencies | No trigger, function, materialized view, publication, subscription, replication slot, RLS policy, or role other than `fst` |
| Runtime stats | Pre-retirement cumulative table stats recorded `2,591,919` inserts and `9,593,492` updates. Both writers were off for scrape `1267` and are now removed from the repository; the historical unique-index activity matched conflict/idempotency writes, not a reader |
| External tools | No production-compose/tool reference found; the original statement window contained writers and explicit ownership probes, not an application reader |
| Solo overlap | The original `228,985/228,985` owner-card set had a semantic `score_history` match; the executed writer-off publication window refreshed the final `229,025`-row manifest before truncate |
| Band overlap | Writer SQL proves transactional derivation from band staging. A deterministic 1% live sample covered `93,423` rows across all 27 instrument/band-type combinations; `49,899` still matched current fact keys, showing historical rows are not fully reconstructable from mutable current band facts |
| Export/API gate | Player export reads mapped physical snapshots plus overlays, not observations. Route/export parity must still be repeated on a full candidate scrape |
| Rollback/rebuild | Exact schema/rehydration SQL remains retained. Solo rows rebuild from `score_history`; band rows rebuild only as a current baseline from `band_entries` + `band_member_stats`, not as restoration of discarded historical observations. Reintroducing a writer requires new versioned code/config and migration work |
| Executed gate | Published scrape `1267` had zero observation touches, two exact public suites, and zero true production reader statements |
| Decision | Truncate and repository writer/config/schema-creation retirement are complete. Existing table/view/index/primary-key/sequence objects await cleanup-image full-scrape parity before separate physical removal |

### Retired flags and durable-owner invariants

Published scrape `1267` proved both writer flags disabled before truncate:

- `Features:WriteSoloScoreObservations=false`
- `Features:WriteBandMemberScoreObservations=false`

The repository now removes both `FeatureOptions` properties and all tracked
appsettings/Compose keys. Role files expose neither key. The cleanup image must
still prove exact count/fingerprint parity for `score_history`, band facts,
player/history APIs, exports, rankings, notifications, publication, and public
health during one complete scrape before physical cleanup.

### Executed maintenance and future drop

Exact packages:

- `tools/sql/postgres-storage-readiness/player-score-observations-truncate.sql`
- `tools/sql/postgres-storage-readiness/player-score-observations-drop.sql`
- `tools/sql/postgres-storage-readiness/player-score-observations-rehydrate.sql`

The checked-in truncate package was executed without `CASCADE`. It preserved
the schema, unique write-idempotency index, primary key, sequence, and union
view for immediate rollback. Code/config/schema-creation removal is now
complete, but the drop package remains future-only until the cleanup image
passes full-scrape publication and public-fingerprint parity. No package uses
`CASCADE`, and this repository change does not execute any package.

## P8: `scrape_dirty_*`

### Owner card

| Table | Rows | Scrape IDs | Bytes | Dominant owner evidence |
|---|---:|---|---:|---|
| `scrape_dirty_account` | `2,728` | `926`-`1146` | `671,744` | rankings and registered-user work |
| `scrape_dirty_song_instrument` | `2,425,194` | `926`-`1146` | `729,743,360` | ranking, refresh, snapshot, and notification work |
| `scrape_dirty_band_scope` | `2,561,011` | `926`-`1146` | `1,127,137,280` | `2,470,759` `band_maintenance/read_index_publication` rows |
| `scrape_dirty_band_team` | `16,847,728` | `926`-`1146` | `6,849,200,128` | `16,750,000` `band_maintenance/read_index_publication` rows |

The rows were created from `2026-06-04` through `2026-06-22`. Current source,
schema, and production-compose searches found no caller. Database catalog
proof found no trigger, function, view, materialized view, FK, publication,
subscription, replication slot, RLS policy, or scheduler schema. Since the
`pg_stat_statements` reset at `2026-07-07T13:44:06Z`, there has been no dirty
table writer; recorded reads are bounded ownership probes.

### Integrity and future cleanup

`dirty-manifest.txt` records exact count/range/time/source/reason values and two
order-independent checksums per table. The checked-in truncate package embeds
the same manifest and fails closed on any drift, active scrape, or missing
approval GUC.

Exact package:

- `tools/sql/postgres-storage-readiness/scrape-dirty-truncate.sql`

ORPHAN-RECLAIM executed one transaction truncating the four tables without
`CASCADE`. The checked-in package is now idempotent only for the fully empty
retired state; any partial or newly populated state fails closed and must
reopen ownership.

After commit, rollback restores schema, not abandoned work rows. The rows were
not a source of truth and could not be used for publication or replay. Exact
pre-action counts, fingerprints, samples, and DDL remain in the ORPHAN-RECLAIM
evidence package.

## P9: legacy `leaderboard_entries_*`

### Owner card

| Requirement | Evidence / decision |
|---|---|
| Physical shape | Nine partitions, `36,769,051` rows, `40,825,225,216` bytes |
| Current main scrape writer | `Features:WriteLegacyLiveLeaderboardDuringScrape=false` in tracked and active production config |
| Current supplemental writers | `InstrumentDatabase.UpsertEntries` writes backfill, refresh, and neighbor rows to both legacy and `leaderboard_entries_overlay` |
| New rollback switch | `Features:WriteLegacyLiveLeaderboardSupplementalRows`; remains `true` until legacy readers migrate |
| Reader migration switch | `Features:UseSnapshotOverlayWorkerReaders`; default `false`, worker-only even when the shared compose environment also reaches `fstservice` |
| Guarded run-once card | `tools/fst-worker-dual-lane-runonce.sh --data-profile legacy-reader-migration`; requires supplemental rollback writes and scope fingerprints on, main legacy scrape writes off, snapshot reuse off, all publication gates on, and the exact candidate image |
| Current public read | Active `fstservice` has `UsePublishedScopeSources=true`; a mapped leaderboard HTTP 200 probe changed zero legacy partition scan counters |
| Current worker read | `PostScrapeBandExtractor` reads legacy rows directly and `BandExtraction` is publication-critical; production `EnabledPhases=All` |
| Other code ownership | Direct legacy helper reads/rank updates/prunes remain. Scrape rank/index/prune work is gated by the main legacy-writer flag, but caller removal is not complete |
| Export | Player export reads snapshots plus overlays, not legacy rows |
| Published comparison | Published `1267` mappings own `39,937,029` rows, `3,167,978` more than legacy. All 27 refreshed bounded small/median/large scope samples differed in count and checksum |
| Correct rollback owner | Published `scrape_publication_state` + complete `leaderboard_published_scope_source` + `leaderboard_entries_snapshot`, with overlays retained separately |
| Full comparison | Rejected: an all-row semantic join spilled temp and hit `No space left on device`. Health recovered immediately; bounded indexed proof replaced it |
| Decision | Do not disable supplemental writes, truncate, or drop yet. First migrate `PostScrapeBandExtractor` and every direct reader to active/published physical sources and overlays |

The current legacy rows are neither byte-exact published state nor a complete
published rollback copy. They are still operationally owned because a
publication-critical worker phase reads them.

The refreshed 2026-07-28 manifest also proves the supplemental writer is not
theoretical: the legacy table gained exactly `970` backfill rows since the
prior owner card while the `36,723,764` scrape-source row count stayed fixed.
The newest supplemental row timestamp is
`2026-07-28T07:25:24.572407Z`. Keep
`WriteLegacyLiveLeaderboardSupplementalRows=true` until reader migration and a
complete writer-off candidate scrape pass.

### Exact future sequence

1. Keep `WriteLegacyLiveLeaderboardDuringScrape=false`.
2. Enable the implemented default-off band-extraction/read migration only in a
   guarded worker run. It resolves current-state helpers from finalized active
   snapshots plus overlays, seeds a narrow accumulated band-context source
   once under an advisory lock, keeps that source current from both spool and
   supplemental writes, bypasses projection fast paths during candidate
   calculations, and prunes legacy-only projection scopes before notification
   processing.
3. Re-run fixture parity for extracted bands, member facts, ranks, affected
   scope tracking, overlay-only scopes, and legacy-only projection rejection.
4. Run one complete live scrape with old-vs-new extraction parity.
5. Set `WriteLegacyLiveLeaderboardSupplementalRows=false`; prove backfill,
   refresh, and neighbor overlay parity in the same complete candidate window.
6. Hold the worker after publish/unfreeze; prove no legacy scan/write delta,
   exact API/export/ranking/history parity, and rebuild capacity.
7. Run `legacy-leaderboard-truncate.sql`.
8. Keep the empty schema for a rollback window. Rehydrate from the published
   map, overlays, and accumulated band context with
   `legacy-leaderboard-rebuild.sql` if rollback requires it.
9. Remove the now-completed legacy seed SQL from the band-context migration;
   the durable seed state prevents re-execution, but final drop must remove the
   textual/runtime fallback dependency.
10. Only after code/schema creation and every direct caller are removed, use
    `legacy-leaderboard-drop.sql`.

No package uses `CASCADE`. The rebuild intentionally reconstructs the
published physical baseline plus current overlays and exact accumulated band
context, not the divergent pre-migration legacy heap.

### 2026-08-07 reader candidate

| Change | Files | Invariant/test | Migration safety | Runtime impact | Rollback | Decision |
|---|---|---|---|---|---|---|
| Snapshot/overlay worker readers | `FeatureOptions`, `InstrumentDatabase`, `GlobalLeaderboardPersistence` | Direct point/batch/profile/count/rank helpers reject legacy-only rows and include overlay-only rows | Default-off; published API role is forced onto mapped publication sources | Candidate helpers bypass potentially stale projection fast paths | Set `USE_SNAPSHOT_OVERLAY_WORKER_READERS=false` while legacy remains | Full-scrape A/B required |
| Band extraction source | `PostScrapeBandExtractor`, `LeaderboardSpoolWriterFactory`, `InstrumentDatabase` | Accumulated context preserves score-only updates, rejects same-score band-only changes, repairs concurrent snapshot ordering, and preserves later supplemental writes | Default-off; one-time advisory-lock seed has a 300-second command bound and durable completion state | Read-only seeded simulation matched `83,801/83,801` raw rows with zero missing/value mismatches and completed derived comparison in 2.44 s | Disable reader flag; retained legacy/context rows remain intact | Full-scrape A/B required |
| Projection/notification cleanup | `SoloCurrentProjectionBuilder`, `ImprovementNotificationService` | Legacy-only projection rows are pruned and excluded from notification scans | Metadata-only `source_kind` column; projection rows are rebuildable after rollback | Low bounded delete plus normal scope refresh | Disable flag and rebuild missing legacy fallback scopes if rollback needs them | Full-scrape A/B required |

The projection migration adds the metadata-only
`solo_current_projection_scope.source_kind` column. Existing scopes with a
non-null source snapshot are backfilled to `snapshot`; null-source scopes stay
`legacy-compatible` and are forced through candidate rebuild or orphan
pruning. Cleanup first probes the small scope table and issues projection
deletes only for named orphan keys, avoiding an unconditional full projection
scan. Startup notification recovery performs the same provenance repair and
notification precompute fails closed if any unresolved provenance remains.

Player stats calculate ranks from the fully resolved snapshot/overlay source
in bounded account batches. API-cache player precompute bulk-loads registered
profiles in 512-account chunks and uses the account-indexed projection only
after stale- and orphan-scope checks prove that projection current.

The worker seeds `leaderboard_band_context` before startup notification
recovery, authentication, or scrape allocation whenever the candidate flag is
enabled. This ordering guarantees that subsequent registered-user refreshes
update an existing accumulated row instead of being lost before extraction.
The first production candidate seeded `83,801` contexts in `3.69` seconds
during network scrape before registered refresh began; later runs use the
durable seed ledger and skip the source scans.

The candidate deliberately avoids a snapshot band-context index. Live probes
showed direct active-snapshot reconstruction took 134.96 seconds and omitted
32,403 derived entries because snapshots do not accumulate historical band
JSON. The narrow seeded context preserved all 78,789 derived keys, while its
raw merge matched all 83,801 legacy rows exactly. Promotion still requires
complete-scrape writer/context fingerprints, measured context-sync overhead,
zero publication-critical failures, and public band parity.

Every maintenance file fails closed unless its named session GUC is supplied,
for example:

```bash
docker exec \
  -e PGOPTIONS="-c fst.scrape_dirty_maintenance=approved" \
  -i fst-postgres \
  psql -X -v ON_ERROR_STOP=1 -U fst -d fstservice \
  < tools/sql/postgres-storage-readiness/scrape-dirty-truncate.sql
```

Run this only after the file's documented live-parity gate passes.

## Read-only readiness tooling

```bash
tools/postgres-storage-ownership-readiness.sh \
  --surface all \
  --output /mnt/docker-storage/Docker/FestivalServiceTracker/fst-data/evidence/<session>
```

The tool:

- requires an FST-drive output path;
- fails on an active scrape, ungranted lock, vacuum, or index build;
- runs the capacity observation guard;
- caps temp spill at `256MB`;
- emits bounded manifests and package checksums;
- copies the gated truncate/drop/rebuild/rehydration SQL;
- has no apply mode.

## Exact post-login sequence

1. Complete operator-owned Epic device login without logging codes, URLs, or
   credentials.
2. Rerun the auth-only refresh canary and require the rotated token to persist
   on the FST drive.
3. Rerun authenticated low-rate direct/PIA JSON parity and the `25/25` compose
   proxy guard.
4. Rerun public health, publication/unfreeze, lock/query, resource, and both
   measured capacity guards.
5. Deploy only the accepted snapshot-reuse candidate; do not combine these
   storage-owner flags with that A/B.
6. Run one guarded run-once scrape through post-process, publication,
   unfreeze, route/export/ranking/history parity, and hold before another
   scrape.
7. P6 observation-writer-off and BAND-SONG-PROJECTION data retirement are
   complete. Deploy the cleanup image separately and require one complete
   scrape with publication and public-fingerprint parity before removing the
   retained exact observation or band-song physical objects.
8. Run a separate legacy reader/supplemental-writer migration A/B.
9. P6 and BAND-SONG-PROJECTION data/code retirement and P8 reclaim are
   complete. Execute only P6 or exact band-song physical cleanup and P9
   maintenance after each surface's remaining exact gate, rerunning health,
   manifests, capacity, and public fingerprints after each action.

Logical-shadow, P6, and BAND-SONG-PROJECTION data retirement each cleared
independent scrape/public parity gates. Their repository cleanup layers are
complete, but retained physical objects still require cleanup-image full-scrape
parity before exact drop SQL may run. P9 remains separately blocked on
reader/writer migration and its own publication/parity gate. The worker
remains held for the next parent-owned scrape decision.
