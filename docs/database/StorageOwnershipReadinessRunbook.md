# Storage Ownership Readiness Runbook

## Current decision

**Tier:** P6 observation and P8 dirty-work reclaim executed; P9 legacy rows
remain blocked on active readers, supplemental writers, and their own live
parity gate.

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

P9 remains governed by its unchanged gate below; P6 and P8 were later
executed. Evidence:
`/mnt/docker-storage/Docker/FestivalServiceTracker/fst-data/evidence/band-song-projection-retirement-20260726T103231Z`.

## Owner-card summary

| Planner | Surface | Bytes | Owner decision | Growth posture | Execution class | Priority |
|---|---|---:|---|---|---|---|
| P6 | `player_score_observations` | `24,576` after truncate | Non-authoritative duplicate/audit surface; no production reader | Scrape `1267` proved both writers off; schema/view/indexes retained for rollback | accepted maintenance | complete |
| P8 | `scrape_dirty_*` | `65,536` after truncate | Abandoned work/audit state from scrapes `926`-`1146`; no current repo/runtime owner found | ORPHAN-RECLAIM executed; family remains fully empty | accepted maintenance | complete |
| P9 | `leaderboard_entries_*` | `40,825,225,216` | Legacy mutable rollback/fallback surface with active supplemental writers and a publication-critical worker reader | Main scrape writer is off; supplemental writer switch remains on until reader migration | `full-scrape-ab`, then `parity-gated-maintenance` | 3 |

## P6: `player_score_observations`

### Owner card

| Requirement | Evidence / decision |
|---|---|
| Pre-action physical shape | `10,167,937` rows; `6,774,161,408` heap bytes; `5,906,309,120` index bytes |
| Source distribution | `9,938,912` `band-member` rows (`97.75%`); `229,025` `solo-history` rows (`2.25%`) |
| Solo writer | `MetaDatabase.InsertScoreChange(s)` dual-writes durable `score_history` and observations |
| Band writer | `BandLeaderboardPersistence` and `BandSpoolWriterFactory` dual-write durable `band_entries`/`band_member_stats` and observations |
| Reader/view | Only `player_score_observation_union`; repository consumers are tests. No production API/export reader was found |
| Database dependencies | No trigger, function, materialized view, publication, subscription, replication slot, RLS policy, or role other than `fst` |
| Runtime stats | Current cumulative table stats recorded `2,591,919` inserts and `9,593,492` updates; production writers are now default-off and the worker is held. The unique-index activity matches conflict/idempotency writes, not a reader |
| External tools | No production-compose/tool reference found; the original statement window contained writers and explicit ownership probes, not an application reader |
| Solo overlap | The original `228,985/228,985` owner-card set had a semantic `score_history` match. Refresh overlap proof for the current `229,025` rows in the required writer-off publication window |
| Band overlap | Writer SQL proves transactional derivation from band staging. A deterministic 1% live sample covered `93,423` rows across all 27 instrument/band-type combinations; `49,899` still matched current fact keys, showing historical rows are not fully reconstructable from mutable current band facts |
| Export/API gate | Player export reads mapped physical snapshots plus overlays, not observations. Route/export parity must still be repeated on a full candidate scrape |
| Rollback/rebuild | Re-enable either writer flag; retain exact schema DDL. Solo rows rebuild from `score_history`; band rows rebuild only as a current baseline from `band_entries` + `band_member_stats`, not as restoration of discarded historical observations |
| Executed gate | Published scrape `1267` had zero observation touches, two exact public suites, and zero true production reader statements |
| Decision | Truncate accepted and executed; table/view/index/sequence schema remains. Drop is a separate future code/schema-removal decision |

### Deployed flags

- `Features:WriteSoloScoreObservations=false`
- `Features:WriteBandMemberScoreObservations=false`

Both flags leave the durable owners unchanged. They are independently
reversible. Candidate deployment requires count/fingerprint parity for
`score_history`, band facts, player/history APIs, exports, rankings,
notifications, publication, and public health.

### Executed maintenance and future drop

Exact packages:

- `tools/sql/postgres-storage-readiness/player-score-observations-truncate.sql`
- `tools/sql/postgres-storage-readiness/player-score-observations-drop.sql`
- `tools/sql/postgres-storage-readiness/player-score-observations-rehydrate.sql`

The checked-in truncate package was executed without `CASCADE`. It preserved
the schema, unique write-idempotency index, primary key, sequence, and union
view for immediate rollback. The drop package remains future-only and requires
removing schema creation plus both writer paths in a versioned migration. No
package uses `CASCADE`.

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
2. Implement a default-off band-extraction/read migration to active candidate
   physical snapshots plus overlays.
3. Prove fixture parity for extracted bands, member facts, ranks, and affected
   scope tracking.
4. Run one complete live scrape with old-vs-new extraction parity.
5. Set `WriteLegacyLiveLeaderboardSupplementalRows=false`; prove backfill,
   refresh, and neighbor overlay parity in the same complete candidate window.
6. Hold the worker after publish/unfreeze; prove no legacy scan/write delta,
   exact API/export/ranking/history parity, and rebuild capacity.
7. Run `legacy-leaderboard-truncate.sql`.
8. Keep the empty schema for a rollback window. Rehydrate from the published
   map with `legacy-leaderboard-rebuild.sql` if rollback requires it.
9. Only after code/schema creation and every direct caller are removed, use
   `legacy-leaderboard-drop.sql`.

No package uses `CASCADE`. The rebuild intentionally reconstructs the
published physical baseline, not the divergent current legacy contents.

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
7. If snapshot reuse passes, run a separate observation-writer-off scrape A/B.
8. Run a separate legacy reader/supplemental-writer migration A/B.
9. P8 is complete. With their separate exact gates passed, execute P6 and then
   P9 maintenance one surface at a time, rerunning health, manifests, capacity,
   and public fingerprints after each action.

The logical-shadow truncate remains a separate prerequisite and is not cleared
by this readiness phase. ORPHAN-RECLAIM restored enough start capacity for
scrape `1267`, but P6, P9, and logical-shadow data still require their own
publication/parity gates. The worker remains held for the next parent-owned
scrape decision.
