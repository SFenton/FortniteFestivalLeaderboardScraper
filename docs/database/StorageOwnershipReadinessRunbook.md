# Storage Ownership Readiness Runbook

## Current decision

**Tier:** continuous-safe ownership/code readiness accepted; all destructive
actions remain blocked.

This phase owns storage-planner queue items P6, P8, and P9 while Epic device
authentication blocks the next live scrape. Runtime was `gpt-5.6-sol`,
reasoning `max`, context `long_context`. No worker was started, no production
row/index/schema/config was mutated, and no full export or alternate drive was
used.

Evidence:
`/mnt/docker-storage/Docker/FestivalServiceTracker/fst-data/evidence/storage-ownership-20260726T013551Z`.

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

The three surfaces total `61,217,292,288` bytes (`57.01 GiB`) gated for
possible future reclaim.

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

P6, P8, and P9 remain governed by their unchanged gates below. Evidence:
`/mnt/docker-storage/Docker/FestivalServiceTracker/fst-data/evidence/band-song-projection-retirement-20260726T103231Z`.

## Owner-card summary

| Planner | Surface | Bytes | Owner decision | Growth posture | Execution class | Priority |
|---|---|---:|---|---|---|---|
| P6 | `player_score_observations` | `11,686,199,296` | Non-authoritative duplicate/audit surface; no production reader | Solo and band-member dual writers are default-off in candidate code/config; deploy only for full-scrape A/B | `full-scrape-ab`, then `parity-gated-maintenance` | 1 |
| P8 | `scrape_dirty_*` | `8,706,752,512` | Abandoned work/audit state from scrapes `926`-`1146`; no current repo/runtime owner found | No write statement since `pg_stat_statements` reset on 2026-07-07 | `parity-gated-maintenance` | 2 |
| P9 | `leaderboard_entries_*` | `40,824,340,480` | Legacy mutable rollback/fallback surface with active supplemental writers and a publication-critical worker reader | Main scrape writer is off; supplemental writer switch remains on until reader migration | `full-scrape-ab`, then `parity-gated-maintenance` | 3 |

## P6: `player_score_observations`

### Owner card

| Requirement | Evidence / decision |
|---|---|
| Physical shape | `9,480,671` rows; `6,203,817,984` heap bytes; `5,480,636,416` index bytes |
| Source distribution | `9,251,686` `band-member` rows (`97.58%`); `228,985` `solo-history` rows (`2.42%`) |
| Solo writer | `MetaDatabase.InsertScoreChange(s)` dual-writes durable `score_history` and observations |
| Band writer | `BandLeaderboardPersistence` and `BandSpoolWriterFactory` dual-write durable `band_entries`/`band_member_stats` and observations |
| Reader/view | Only `player_score_observation_union`; repository consumers are tests. No production API/export reader was found |
| Database dependencies | No trigger, function, materialized view, publication, subscription, replication slot, RLS policy, or role other than `fst` |
| Runtime stats | Before this phase's probes: `seq_scan=0`, `idx_scan=9,938,697`, `1,904,653` inserts and `8,034,032` updates. The index activity matches conflict/idempotency writes |
| External tools | No production-compose/tool reference found; `pg_stat_statements` contained writers and explicit ownership probes, not an application reader |
| Solo overlap | All `228,985/228,985` observation rows have a semantic `score_history` match; zero duplicate semantic groups |
| Band overlap | Writer SQL proves transactional derivation from band staging. A deterministic 1% live sample covered `93,423` rows across all 27 instrument/band-type combinations; `49,899` still matched current fact keys, showing historical rows are not fully reconstructable from mutable current band facts |
| Export/API gate | Player export reads mapped physical snapshots plus overlays, not observations. Route/export parity must still be repeated on a full candidate scrape |
| Rollback/rebuild | Re-enable either writer flag; retain exact schema DDL. Solo rows rebuild from `score_history`; band rows rebuild only as a current baseline from `band_entries` + `band_member_stats`, not as restoration of discarded historical observations |
| Decision | Accept default-off writer readiness. Do not deploy, truncate, or drop before one complete live scrape publishes with flags off and parity passes |

### Candidate flags

- `Features:WriteSoloScoreObservations=false`
- `Features:WriteBandMemberScoreObservations=false`

Both flags leave the durable owners unchanged. They are independently
reversible. Candidate deployment requires count/fingerprint parity for
`score_history`, band facts, player/history APIs, exports, rankings,
notifications, publication, and public health.

### Future maintenance

Exact packages:

- `tools/sql/postgres-storage-readiness/player-score-observations-truncate.sql`
- `tools/sql/postgres-storage-readiness/player-score-observations-drop.sql`
- `tools/sql/postgres-storage-readiness/player-score-observations-rehydrate.sql`

Truncate is preferred before drop because it preserves the schema, unique
write-idempotency index, and union view for immediate rollback. No package uses
`CASCADE`.

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

The future action is one transaction truncating the four tables without
`CASCADE`. Before execution, rerun the manifest tool and one successful live
scrape with current code. If any row newer than scrape `1146` appears, stop and
reopen ownership instead of updating the guard mechanically.

After commit, rollback restores schema, not abandoned work rows. The rows are
not a source of truth and cannot be used for publication or replay.

## P9: legacy `leaderboard_entries_*`

### Owner card

| Requirement | Evidence / decision |
|---|---|
| Physical shape | Nine partitions, `36,768,081` rows, `40,824,340,480` bytes |
| Current main scrape writer | `Features:WriteLegacyLiveLeaderboardDuringScrape=false` in tracked and active production config |
| Current supplemental writers | `InstrumentDatabase.UpsertEntries` writes backfill, refresh, and neighbor rows to both legacy and `leaderboard_entries_overlay` |
| New rollback switch | `Features:WriteLegacyLiveLeaderboardSupplementalRows`; remains `true` until legacy readers migrate |
| Current public read | Active `fstservice` has `UsePublishedScopeSources=true`; a mapped leaderboard HTTP 200 probe changed zero legacy partition scan counters |
| Current worker read | `PostScrapeBandExtractor` reads legacy rows directly and `BandExtraction` is publication-critical; production `EnabledPhases=All` |
| Other code ownership | Direct legacy helper reads/rank updates/prunes remain. Scrape rank/index/prune work is gated by the main legacy-writer flag, but caller removal is not complete |
| Export | Player export reads snapshots plus overlays, not legacy rows |
| Published comparison | Published mappings own `39,588,650` rows, `2,820,569` more than legacy. All 27 bounded small/medium/large scope samples differed in count and checksum |
| Correct rollback owner | Published `scrape_publication_state` + complete `leaderboard_published_scope_source` + `leaderboard_entries_snapshot`, with overlays retained separately |
| Full comparison | Rejected: an all-row semantic join spilled temp and hit `No space left on device`. Health recovered immediately; bounded indexed proof replaced it |
| Decision | Do not disable supplemental writes, truncate, or drop yet. First migrate `PostScrapeBandExtractor` and every direct reader to active/published physical sources and overlays |

The current legacy rows are neither byte-exact published state nor a complete
published rollback copy. They are still operationally owned because a
publication-critical worker phase reads them.

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
9. With all exact gates passed, execute P6, P8, then P9 maintenance one surface
   at a time, rerunning health, manifests, capacity, and public fingerprints
   after each action.

The logical-shadow truncate remains a separate prerequisite and is not cleared
by this readiness phase. Capacity-ready SNAPSHOT-REUSE scrape `1265` also
failed before publication when ranking snapshots crossed its declared
same-drive safety floor, so steps 7-9 remain blocked and the worker is held.
