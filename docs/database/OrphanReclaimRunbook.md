# ORPHAN-RECLAIM Runbook

## Current decision

**Tier:** Tier 1 and derived latest-state reclaim accepted; player observations
deferred on an exact live-publication gate.

ORPHAN-RECLAIM ran on 2026-07-27 with runtime `gpt-5.6-sol`, reasoning `max`,
and context `long_context`. Production stayed on published scrape `1236` with
public reads unfrozen. `fstworker` remained created/offline with restart `no`;
no worker or scrape was started.

Evidence:

`/mnt/docker-storage/Docker/FestivalServiceTracker/fst-data/evidence/orphan-reclaim-20260727T193224Z`

## Accepted scope

### Tier 1

| Action | Objects | Pre-action bytes | Result |
|---|---|---:|---|
| `TRUNCATE`, no `CASCADE` | `scrape_dirty_account`, `scrape_dirty_song_instrument`, `scrape_dirty_band_scope`, `scrape_dirty_band_team` | `8,706,752,512` | Empty schemas and primary keys retained |
| `TRUNCATE`, no `CASCADE` | `composite_rank_history_latest`, `band_current_projection_source_state` | `1,307,746,304` | Empty derived-state schemas retained |
| `TRUNCATE`, no `CASCADE` | `post_scrape_shadow_run`, `post_scrape_shadow_metric`, `invalid_leaderboard_shadow_observation` | `2,088,960` | Empty schema, sequence, indexes, and metric-to-run FK retained |
| `DROP TABLE`, no `CASCADE` | `notification_cleanup_audit_20260509` | `11,264,000` | Dated one-off audit table and owned sequence removed; exact recreate DDL retained |

Tier 1 reclaimed `10,027,671,552` database bytes. Filesystem free space rose
by `10,027,778,048` bytes.

### Tier 2 latest state

| Action | Objects | Rows | Pre-action bytes | Result |
|---|---|---:|---:|---|
| `TRUNCATE`, no `CASCADE` | `band_team_rank_history_latest_v2` and its three partitions | `21,403,363` | `15,520,841,728` | Parent/partitions and four primary-key constraints retained |
| `TRUNCATE`, no `CASCADE` | `rank_history_latest` | `6,800,990` | `3,032,678,400` | Empty schema and primary key retained |

Tier 2 reclaimed `18,553,454,592` database bytes. Filesystem free space rose
by `18,553,565,184` bytes.

Combined database reclaim was `28,581,126,144` bytes; measured filesystem gain
was `28,581,343,232` bytes.

## Ownership and correctness proof

- Every Tier 1 exact name was searched in current service/test/core code,
  `DatabaseInitializer`, dynamic table-name patterns, tools, production compose
  tooling, git history, database routines/views/triggers, prepared statements,
  publications, replication, and the deployed service binary.
- The only current `scrape_dirty_*` references are the read-only manifest and
  gated truncate package. No runtime creator, reader, or writer exists.
- Dirty and shadow state ended at scrape `1146`. Twenty-seven later scrapes
  completed before or at published scrape `1236`; published `1236` has `6,138`
  complete scopes, `39,588,650` rows, and zero failed publication-critical
  phases.
- `band_team_rank_history_latest_v2` is a writer-side change detector and
  parity helper, not an API read source. Production
  `BandRankHistory__Mode=Disabled`; a bounded `3,000`-row edge sample matched
  retained `band_team_rank_history_points_v2` exactly.
- `rank_history_latest` has no exact current code or statement owner. It was
  stale: `4,386/4,500` bounded rows differed from the newer retained
  `rank_history`, proving it was neither current nor authoritative.
- Exact schema DDL, counts, ranges, order-independent fingerprints, bounded
  samples, recreate SQL, and regeneration limitations were captured before
  mutation. Both production actions first passed rolled-back rehearsals.

## Public and live-safety validation

- The pre-action, post-Tier-1, and final suites each captured `13` normalized
  route/export/history/ranking fingerprints. Final comparison was `13/13`
  byte-exact.
- The mapped solo leaderboard remained HTTP `200`. Derived routes already
  isolated from failed scrape `1266` remained the same fail-closed HTTP `503`;
  the reclaim introduced no new failure shape.
- Postgres, `fstservice`, and `festivalweb` stayed healthy. `/readyz`, the web
  shell, and `/api/service-info` remained HTTP `200`.
- At each preflight and the immediate/60-second post-action ticks there were
  zero running scrapes, active worker queries, ungranted locks, vacuums, or
  index builds.
- Published scrape `1236` remained unfrozen and the worker ledger remained
  offline.

## Capacity outcome

Final free space was `64,001,667,072` bytes. The corrected full-run requirement
is `60,392,999,803` bytes, leaving `3,608,667,269` bytes of start margin.
Both `--action-class reclaim` and `--action-class scrape` pass with the
seven-day capacity alert still active.

The capacity guard now accepts an explicit conservative
`--expected-reclaim-bytes` only for zero-scratch reclaim. Below the emergency
window, the estimate must project enough post-action free space to restore the
full window; default reclaim behavior remains fail-closed. Always rerun the
guard without an estimate after the action.

## Deferred `player_score_observations`

The table remains intact at `10,167,937` rows and `12,682,354,688` bytes.
Current code and the deployed image contain independent default-off flags:

- `Features:WriteSoloScoreObservations=false`
- `Features:WriteBandMemberScoreObservations=false`

No production reader or export owner was found, and the union view is retained.
However, no complete scrape with both writers off has globally published and
unfrozen. The exact remaining gate is one complete writer-off scrape followed
by exact API, export, ranking, history, publication, and public-health parity.
Do not truncate the table before that gate passes.

## Rollback and rebuild

- A transaction rollback was the exact rollback before each commit.
- Empty retained schemas can be reused immediately if a future versioned owner
  is introduced.
- Exact Tier 1/Tier 2 DDL is in the evidence `manifests/` directory.
- Deterministic rebuild SQL for composite, band-v2 latest, and solo latest
  state is in `sql/rebuild-derived-state.sql`.
- Dirty/shadow work rows and the dated notification audit are non-authoritative
  and intentionally not reconstructed.
- Observation rehydration SQL remains at
  `tools/sql/postgres-storage-readiness/player-score-observations-rehydrate.sql`.

## Next storage phase

Scrape `1267` completed with the current recovery image and durable no-progress
watchdog. It published/unfroze and cleared the logical leaderboard shadow's
destructive parity gate. The logical truncate remains deferred to a separate
monitored phase and must not be combined with
`player_score_observations`.

Observation reclaim remains a distinct decision: verify both observation
writers were off for scrape `1267`, capture exact pre/post table evidence, and
apply its own public parity and rollback package before any truncate.
