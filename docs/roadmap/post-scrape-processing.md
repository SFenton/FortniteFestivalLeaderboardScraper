---
status: roadmap
owner: worker
last_verified: 2026-08-14
last_verified_commit: 099fd6fa
sources:
  - FSTService/ScraperWorker.cs
  - FSTService/Scraping/PostScrapeOrchestrator.cs
  - FSTService/Scraping/PostScrapePhasePolicy.cs
  - FSTService/Scraping/RankingsCalculator.cs
  - FSTService/Scraping/ScrapeProgressTracker.cs
  - FSTService/Scraping/WorkerStatusPublisher.cs
  - FSTService/Scraping/PhaseProgressCatalog.cs
  - FSTService/Scraping/DurablePhaseProgressSink.cs
  - FSTService/Scraping/Replay/
  - FSTService/Scraping/OnlineBoundedPageWriter.cs
  - FSTService/Persistence/DatabaseInitializer.cs
  - FSTService/Persistence/MetaDatabase.cs
  - FSTService/Persistence/Maintenance/DatabaseRetentionMaintenanceService.cs
  - FSTService/Api/HealthEndpoints.cs
  - FortniteFestivalWeb/src/pages/settings/SettingsPage.tsx
  - FortniteFestivalWeb/src/pages/settings/SettingsServiceProgress.tsx
  - FortniteFestivalWeb/src/pages/settings/serviceProgress.ts
  - /mnt/docker-storage/Docker/FestivalServiceTracker/fst-data/evidence/pr27-settings-live-ab-20260814T062455Z
  - packages/core/src/api/serverTypes.ts
  - docs/architecture/data-publication-flow.md
  - docs/architecture/data-storage.md
  - docs/components/worker.md
  - docs/database/SnapshotReuseRunbook.md
  - docs/decisions/0005-post-scrape-modular-monolith.md
update_triggers:
  - A post-scrape phase, dependency, criticality, progress contract, replay path, deployment gate, overlap decision, or measured baseline changes.
---

# Post-scrape processing roadmap

## Outcome

- **Decision:** retain the modular monolith and the existing one-image
  API/worker role split.
- Add stable in-process phase contracts before attempting process separation.
- Add guarded replay to the existing FSTService binary before considering a
  new runner project.
- Keep PostgreSQL as the durable source of truth. DuckDB and Parquet remain
  bounded artifact/replay companions.
- Reject microservices, runtime-loaded plugins, full scrape N+1 overlap, and
  raw-HTTP capture as current implementation directions.
- Use the accepted timing foundation to optimize measured bottlenecks rather
  than inferred phase cost. The first BandMaintenance target is current
  projection refresh.
- Treat storage capacity as an urgent independent safety lane. The existing
  report-only snapshot-retention planner must fail closed on incomplete
  statistics, and exact reclaim/workspace evidence must pass the current gate
  before any rewrite proposal.

Implementation is approved after this plan is rendered to the local autonomous
agent outbox. All implementation, evaluation, deployment, and promotion
decisions remain GPT-5.6 Sol owned.

This approval is limited to the operator-approved roadmap. It never bypasses
the active live-safety, publication-parity, provider, storage, rollback, or
maintenance gates for a future action.

### Runtime contract

| Field | Required value |
|---|---|
| Model | `gpt-5.6-sol` |
| Reasoning effort | `max` |
| Context tier | `long_context` |

This table records the active operator-approved autonomous run's execution
metadata. It is not standing repository authorization for a future live,
destructive, or parity-gated action.

No implementation agent may silently substitute another model, lower effort,
or shorter context.

## Tandem decision quality

The independent GPT-5.6 Sol and Claude Opus 5 passes agreed on the main
architecture, progress defect, Settings direction, dominant phases, storage
pressure, microservice rejection, and current overlap rejection. Disputes were
resolved through repository and bounded runtime evidence.

| Tandem item | Result |
|---|---|
| Modular monolith versus microservices | Consensus: retain the modular monolith and reject microservices |
| New replay runner project | Adjudicated: add guarded replay to the existing FSTService binary first; create another project only after a second consumer or measured host problem |
| Raw HTTP versus parsed/database artifacts | Adjudicated: post-phase replay starts with phase-scoped canonical PostgreSQL inputs; raw HTTP is a separately funded parser/network tier |
| Another candidate's snapshot cost | Factual conflict resolved: fixed partitions append one generation in the tens-of-GB class, not another copy of the cumulative 2.381 TB family |
| BandMaintenance/rankings overlap | Interpretation narrowed: projection work may eventually overlap the solo-ranking subgraph; band prune must precede band rankings |
| Durable progress storage | Adjudicated: normalized phase attempts plus the existing JSON current summary |
| First implementation boundary | Adjudicated: timing DDL compatibility, fresh-schema/idempotency tests, and BandMaintenance telemetry only |
| Snapshot reclaim estimate | Adjudicated: major potential is verified, but exact reclaimable bytes remain unknown until the existing planner output is captured |

| Surface | Assessment | Confidence |
|---|---|---:|
| Historical correctness and publication safety | Great: candidate isolation, exact catalog binding, complete-scope manifests, critical-phase gates, atomic generation publication, and fail-closed reads are strong | High |
| Test posture | Good: extensive Postgres, worker, API, publication, web, and browser coverage; CI enforces 94% service line coverage | High |
| Modularity | Okay: phases are testable, but `PostScrapeOrchestrator.cs` is 2,748 lines and still contains dormant or PostgreSQL-no-op paths | High |
| Live progress observability | Good: normalized durable attempts, service-info v2, watchdog progress/liveness separation, and the responsive Settings progress experience are accepted | High |
| Performance | Poor: recent full-scrape p50 is about 8.58 hours and recorded post-processing consumes about 5.6 hours on scrape 1290 | High |
| Storage sustainability | Poor and urgent: the 3.6 TB drive is 96% used with roughly 170 GB free after scrape 1296 | High |
| Overall | Correctness-first and operationally dependable, with durable backend and browser progress accepted; performance, storage, and replay remain unresolved | High |

## Evidence rules

This roadmap uses four evidence labels:

- **Verified:** current code, configuration, tests, git history, or bounded
  runtime measurement directly proves the statement.
- **Inference:** primary evidence supports the conclusion, but the exact
  quantity or causal attribution is not yet measured.
- **Hypothesis:** a candidate worth falsifying; not an implementation fact.
- **Unknown:** evidence is absent or unsafe to collect during the current live
  state.

Roadmaps and removed historical harnesses are not proof that behavior exists.
Current canonical docs, code, tests, runtime state, and measured artifacts take
precedence.

## Verified baseline

The bounded runtime sample was captured while scrape `1291` was in network
collection and published scrape `1290` remained current behind the expected
public-read freeze.

| Surface | Verified state |
|---|---|
| Repository at research boundary | Clean `master` at `86b45d30` |
| Core health | PostgreSQL, API, worker, and web healthy; PostgreSQL and API ready |
| Database activity | No waiting locks or non-idle query older than five minutes at the sampled time |
| PostgreSQL cap | 16 GiB |
| Worker cap | 12 GiB |
| FST drive | 3.6 TB, about 299 GB free, 92% used |
| Database | About 3,304 GB |
| Snapshot family | About 2,381 GB and approximately 4.473 billion estimated rows |
| Snapshot state | 6,318 finalized scopes, one active snapshot ID: `1290` |
| Current plus previous publication sources | Scrapes `1290` and `1288`, about 80.8 million mapped snapshot rows |
| Live writer mode | `OnlineBounded`; successful page batches are not retained as replay files |
| Production correctness flags | Scope manifests, successful writers, publication-critical phases, and published-scope-source writes are enforced; legacy live scrape writes are disabled |
| Snapshot reuse | All supporting correctness prerequisites are present, but `SkipUnchangedPhysicalLeaderboardSnapshots` remains false |
| Snapshot retention | Report-only planning is enabled by the current code/appsettings default and is not explicitly set in the worker role environment; rewrite false; max one partition; free-space path `/app/data`; 500 GiB minimum free-space gate currently blocks rewrite |

### Workload and wall-clock baseline

The latest ten completed scrapes covered 697–699 songs, 40.23–40.47 million
entries, 596–599 thousand requests, and approximately 91 GB decimal received
per scrape.

| Metric | Observed value |
|---|---:|
| Full scrape p50 | 8 h 34 m 50 s |
| Full scrape range | 7 h 57 m 41 s to 9 h 47 m 36 s |
| Scrape `1290` wall clock | 9 h 35 m 03 s |
| Scrape `1290` recorded post-phase sum | 5 h 36 m 21 s |
| Publication completion-to-ready p50 | About 4.9 minutes |
| Ready-to-published commit interval | About 7–17 seconds on recent split-publication runs |

These ten scrapes are workload-comparable but span recent code/configuration
changes. Small-sample p95 values are maximum-like and must not be treated as a
long-run distribution.

### Current post-phase timing baseline

| Phase | Samples | p50 or representative duration | Tail/variance note |
|---|---:|---:|---|
| `BandMaintenance` | 10+ | About 171 minutes | Scrape `1293` took 132.3 minutes: current projection 100.8 minutes (76.20%), prune 19.1 minutes, search refresh 12.4 minutes |
| `ComputeRankings` | 10+ | About 68 minutes | Stable near 70 minutes |
| `RefreshRegisteredUsers` | 10 | About 24 minutes | About 44-minute tail |
| `Cleanup.PrecomputeAll` | 10 | About 13 minutes | Stable |
| `Cleanup.SoloCurrentProjection` | 10 | About 12 minutes | About 18-minute tail |
| `LeaderboardRivals` | 5 | About 5 minutes | One recent run about 20 minutes; feature is recent |
| `Rivals` | 10 | About 50 seconds | One recent run about 30 minutes |
| Each shadow activation | 10 | About 2.3–2.5 minutes | Broad snapshot-state query |
| `BandExtraction` | 10 | About 1.5 minutes | Already bounded-parallel |
| First-seen, names, stats, checkpoint, legacy prune | 10 | Usually seconds or no-op | Not optimization priorities |

### Rankings subphase baseline

For scrape `1290`:

| Rankings subphase | Duration |
|---|---:|
| Per-instrument rankings | 13 m 20 s |
| Load about 7.01 million ranking rows | 9 s |
| Composite rankings | 1 m 18 s |
| Solo-family rankings | 2 m 11 s |
| Combo rankings | 2 m 09 s |
| Rank-history snapshots | 33 m 44 s |
| Band rankings | 17 m 00 s |
| Total | 69 m 51 s |

Rank-history snapshots are the largest measured ranking subphase. Existing
rank-history/band-ranking overlap support remains disabled.

## Unique findings and corrections

### Missing `scrape_phase_timings` bootstrap

**Verified:** current bootstrap creates `scrape_phase_outcomes` but never
creates `scrape_phase_timings`. `MetaDatabase.RecordScrapePhaseTiming` catches
and debug-logs insertion failure, so a fresh database silently loses ranking
telemetry. Production retains the table as historical schema state.

The compatibility repair must exactly match the live shape before adding any
new constraint:

```sql
CREATE TABLE IF NOT EXISTS scrape_phase_timings (
    id            BIGSERIAL PRIMARY KEY,
    scrape_id     BIGINT NOT NULL,
    phase         TEXT NOT NULL,
    subphase      TEXT,
    item_key      TEXT,
    started_at    TIMESTAMPTZ NOT NULL,
    completed_at  TIMESTAMPTZ NOT NULL,
    duration_ms   BIGINT NOT NULL,
    rows_read     BIGINT,
    rows_written  BIGINT,
    rows_deleted  BIGINT,
    scope_count   BIGINT,
    success       BOOLEAN NOT NULL DEFAULT TRUE,
    error_message TEXT
);

CREATE INDEX IF NOT EXISTS ix_scrape_phase_timings_scrape
    ON scrape_phase_timings
       (scrape_id, phase, subphase, item_key);

CREATE INDEX IF NOT EXISTS ix_scrape_phase_timings_started
    ON scrape_phase_timings (started_at DESC);
```

The first repair must not add a foreign key because the surviving live table
has none and `CREATE TABLE IF NOT EXISTS` does not reconcile an existing
shape. A later lock-aware migration may add constraints after separate
evaluation.

### Snapshot capacity

**Verified:** snapshot generations share nine fixed instrument partitions.
Another candidate appends one generation; it does not allocate another 2.381
TB. Historical full-scrape evidence observed roughly 15–20 GB snapshot-family
growth per new generation before other WAL/derived costs.

**Inference:** most snapshot rows are older than the current and one rollback
generation. Current plus previous mapped rows are about 1.8% of the catalog
row estimate. This does not prove exact reclaimable bytes because row width,
index distribution, failed-candidate evidence, source bindings, and retention
protection vary.

**Unknown:** the exact planner candidate list, protected generations, retained
bytes, purge bytes, rewrite workspace, and rollback objects. The report-only
planner is already enabled, but its exact result is not currently persisted in
available evidence.

### Current progress defect

**Verified:**

- `/api/progress` is process-local and was empty in `fstservice` while the
  separate worker was actively scraping.
- `/api/service-info` had no progress percent or ETA after more than an hour of
  network work.
- worker liveness heartbeats intentionally preserve operation progress
  timestamps.
- post-process durable updates hard-code `PostScrapeEnrichment`.
- Settings assigns static weights that do not reflect measured duration and
  can move overall progress backward as durable ranking child operations
  replace the parent operation.

## Exact current dependency map

```text
Exact catalog selection
  -> public-read freeze
  -> network collection and bounded online persistence
  -> complete writer/scope-manifest gates
  -> enrichment branches:
       RankRecompute || FirstSeenSeason || AccountNameResolution
  -> registered-user recurring refresh
  -> early snapshot activation
  -> BandExtraction:
       derive band rows
       -> team membership/configuration summary rebuild
  -> registered-player band discovery
  -> registered-band targeted processing
  -> BandMaintenance:
       global prune
       -> search projection refresh
       -> current band projection refresh
  -> ComputeRankings:
       per-instrument solo ranks
       -> composite + family + combo rankings
       -> rank-history snapshots
       -> band rankings
  -> optional early solo projection preparation
  -> song rivals
  -> leaderboard rivals
  -> player statistics tiers
  -> finalization no-ops / final snapshot activation / projection seal
  -> publication-critical solo projection refresh
  -> publication-critical API response precompute
  -> best-effort cleanup
  -> published-scope-source build
  -> publication prepare
  -> reader drain and atomic commit
  -> unfreeze
  -> improvement notifications and client broadcast
  -> deferred retention admission
```

### Phase ownership and criticality

| Phase | Owner | Primary inputs | Primary outputs | Criticality |
|---|---|---|---|---|
| `RankRecompute` | `GlobalLeaderboardPersistence` | Changed songs and legacy live rows | Legacy stored ranks | Publication-critical, but production no-op while legacy writes are off |
| `FirstSeenSeason` | `FirstSeenSeasonCalculator` | Catalog, season windows, Epic history | First-seen season rows | Best effort |
| `AccountNameResolution` | `AccountNameResolver` | Unresolved account IDs | Display-name state | Best effort |
| `RefreshRegisteredUsers` | `CyclicalSongMachine` | Registered accounts, catalog, seasons, Epic lookups | Overlays, session history, scope checkpoints | Publication-critical |
| `ActivateShadowSnapshotsEarly` | `GlobalLeaderboardPersistence` | Candidate snapshot rows and expected scopes | Active snapshot state | Publication-critical |
| `BandExtraction` | `PostScrapeBandExtractor` | Band context, max scores | Band entries/member rows, team membership/configuration summaries, and impacted scopes | Publication-critical |
| `LegacyBandScrape` | `BandScrapePhase` | Legacy band network path | Band entries | Publication-critical, unreachable in a normal `BandAll` pass |
| `RegisteredPlayerBandDiscovery` | Discovery orchestrator | Registered accounts, season windows, Epic | Discovered teams/scopes | Best effort |
| `RegisteredBandTargetedProcessing` | Targeted orchestrator | Registered teams, Epic | Targeted team rows/scopes | Best effort |
| `BandMaintenance` | Band persistence/projection builders | Band entries/members and impacted teams/scopes | Pruned band state plus search and current projections | Publication-critical |
| `ComputeRankings` | `RankingsCalculator` | Current solo/band state, populations, max scores | Solo/composite/family/combo/band rankings and histories | Publication-critical |
| `PrepareSoloCurrentProjectionForDerived` | `SoloCurrentProjectionBuilder` | Snapshots and overlays | Validated current projection | Publication-critical, currently flag-dependent |
| `Rivals` | `RivalsOrchestrator` | Current scores/ranks and dirty fingerprints | Song-rival rows/samples | Publication-critical |
| `LeaderboardRivals` | `LeaderboardRivalsCalculator` | Rankings and player scores | Ranking-neighbor rival rows/samples | Publication-critical |
| `PlayerStatsTiers` | Post-scrape orchestrator | Player profiles, max scores, populations | Stats-tier rows | Publication-critical |
| `Checkpoint` | Persistence wrappers | None in PostgreSQL | None | Best effort, verified PostgreSQL no-op |
| `ActivateShadowSnapshots` | `GlobalLeaderboardPersistence` | Same candidate snapshot state | Wave-two finalization marker | Publication-critical |
| `SealSoloCurrentProjectionScopes` | Projection builder/context | Stale and notification scopes | Publication scope boundary | Publication-critical |
| `Cleanup.SoloCurrentProjection` | Projection builder | Snapshots, overlays, sealed scopes | Candidate current projection | Publication-critical |
| `Cleanup.PrecomputeAll` | `ScrapeTimePrecomputer` | All derived candidate state | Generation-staged API responses | Publication-critical |
| Retention/legacy cleanup | Post-scrape and retention services | History/snapshot metadata | Bounded deletion or report | Best effort |
| Publication | Worker and `MetaDatabase.Publication` | Complete candidate and required bindings | Current publication pointer | Publication-critical |
| `ImprovementNotifications` | Notification recovery service | Published generation and projection plan | Notification state/events | Best effort, post-publication |

## BandExtraction, BandMaintenance, and rankings table/resource map

BandMaintenance cannot overlap complete rankings because the band-ranking
branch reads `band_entries` while the prune branch writes it.

| Work | Reads | Writes/deletes | Important resources |
|---|---|---|---|
| BandExtraction membership/configuration summaries | `band_members`, `band_member_stats` | `band_team_membership`, `band_team_membership_state`, `band_team_configurations` | Runs under BandExtraction ownership; outside the BandMaintenance timing contract |
| Band prune | `band_entries`, `band_members` | `band_entries`, `band_member_stats`, `band_members` | Global window sort, one long transaction, WAL/temp |
| Search projection | `band_entries`, `band_member_stats`, `band_members` | `band_search_team_projection`, `band_search_member_projection`, `band_search_projection_state`, `band_identity` | Dedicated advisory rebuild lock |
| Current band projection | `band_entries`, `band_member_stats` | `current_band_leaderboard_entries`, `band_current_projection_scope/state` | Per-scope transactions, default max two band types |
| Per-instrument solo rankings | Current solo state, `song_stats`, population/max-score inputs | `song_stats`, overrides, temp valid entries, `account_rankings` | Two sessions with elevated per-session sort/index memory |
| Composite/family/combo | Loaded solo account metrics | `composite_rankings`, `solo_family_rankings`, `combo_leaderboard`, `combo_stats` | Truncate/COPY or delete/COPY |
| Rank snapshots | Current solo/composite rankings and prior histories | `rank_history`, `composite_rank_history` | Large history scans, WAL/data-file pressure |
| Band rankings | `band_entries` | Band ranking generations/build/current tables and stats | Long build/swap transactions |

The only data-correct future overlap candidates are BandMaintenance projection
work versus the **solo** ranking subgraph. Band prune and band rankings retain
an explicit dependency. Even table-disjoint work shares PostgreSQL memory,
WAL, buffer cache, disk queues, temp space, connection capacity, CPU, and
autovacuum. No parallelism claim may be promoted without matched resource
evidence.

## Per-phase optimization matrix

Every candidate below is unresolved work. Correctness/publication parity is
required before performance acceptance.

| Phase | Candidate | Parallel/reorder/remove assessment | Smallest falsifying probe | Acceptance metrics | Rollback | Execution class |
|---|---|---|---|---|---|---|
| `RankRecompute` | Omit or record `skipped` when legacy live writes are disabled | Remove from active PostgreSQL plan; retain rollback behavior behind the flag until separate audit | Unit/integration tests with legacy flag on/off plus full scrape parity | Zero output difference; no missing legacy rollback behavior | Restore conditional call | `scrape-boundary-deploy` |
| `FirstSeenSeason` | Add song/request counts only | Already parallel; current cost too small for algorithm work | Three comparable scrape observations | No telemetry overhead above 1%; optimize only if >2% wall | Remove counters | `continuous-safe` |
| `AccountNameResolution` | Add batch/account counts only | Already parallel; not a bottleneck | Three scrape observations | Same names and retries | Remove counters | `continuous-safe` |
| `RefreshRegisteredUsers` | Measure planned scopes, skipped/fetched/update counts, and per-account tails; then evaluate freshness-driven reduction | Do not raise concurrency before provider/load evidence | Identical captured response replay on isolated inputs | Exact overlay/history/scope parity; lower requests or wall; no retry amplification | Feature flag to full recurring path | `full-scrape-ab` |
| Early snapshot activation | Use expected/manifests rather than scanning candidate snapshot rows; evaluate one activation | Potential query rewrite; final activation removal requires separate proof | Bounded plan and isolated current-state checksums | At least 30% phase reduction; exact snapshot state and current reads | Restore existing SQL/calls | `full-scrape-ab` |
| `BandExtraction` | Make impacted teams/scopes reflect actual changed rows | Already parallel; do not increase DOP first | Same input rows, compare impacted-key sets and band outputs | Exact band rows; fewer downstream scopes | Keep broad-impact mode | `full-scrape-ab` |
| `LegacyBandScrape` | Retire unreachable normal-pass branch after mode audit | Remove only in a dedicated dead-path PR | CLI/config/reference search plus targeted tests | No supported mode loses band acquisition | Revert deletion | `scrape-boundary-deploy` |
| Band discovery/targeting | Add lookup budgets, results, retry, and checkpoint timings | Low priority; mutual parallelism must preserve provider budget | Bounded captured/provider canary | Same teams/scopes, no retry/error increase | Restore serial order | `full-scrape-ab` |
| Band prune | Restrict ranking/window work to changed `(song, band_type)` scopes | Secondary measured target: `1,144,264 ms` (`14.41%`) in scrape `1293` | Isolated changed-scope A/B after current projection analysis | Exact retained entries/members; ≥20% subphase reduction; no >10% WAL/temp/IO regression | Global-prune feature flag | `full-scrape-ab` |
| BandExtraction membership/configuration summaries | Measure changed-team batching and skip exact unchanged summaries in a later dedicated iteration | Remains owned by BandExtraction; separate from BandMaintenance timing and optimization | Same extraction inputs, membership/configuration checksums | Exact membership/configuration rows with fewer writes or lower extraction wall | Existing broad summary rebuild | `full-scrape-ab` |
| BandMaintenance current projection refresh | Analyze unchanged-scope selection and replace/delete volume before changing the algorithm | First measured target: `6,049,933 ms` (`76.20%`), `53,543` considered scopes, `8,020` refreshed | Bounded query/plan and same-input checksum probe, then one-variable full-scrape A/B | Exact current/public DTO hashes; materially fewer than `14,179,946` writes and `14,189,655` deletes; no >10% resource regression | Existing broad current refresh | `full-scrape-ab` |
| BandMaintenance search projection refresh | Skip exact unchanged teams/scopes and batch by measured resource class | Lower priority: `745,473 ms` (`9.39%`) in scrape `1293` | Changed-team/checksum comparison in isolated PostgreSQL | Exact search/public DTO hashes; fewer rows and wall | Existing broad search refresh | `full-scrape-ab` |
| Per-instrument rankings | Profile materialization/window queries under existing DOP 2 | Never blindly raise DOP; prior unbounded concurrency OOM-killed PostgreSQL | Isolated DOP 1/2/3 resource-capped A/B after query plans | Exact rankings; ≥10% wall benefit; no >10% memory/WAL/temp/API regression | DOP/config restore | `full-scrape-ab` |
| Composite/family/combo | Test bounded two-way execution and adaptive diff/replace | Data-disjoint but memory/WAL-sensitive | Same loaded metrics, independent output hashes | Exact rows/order; ≥10% combined wall reduction | Serial execution and full replace | `full-scrape-ab` |
| Rank-history snapshots | First optimize latest-state scan; then test existing overlap flag or DOP change separately | Largest ranking subphase; do not combine concurrency hypotheses | Isolated current-history query plan and one-variable A/B | Exact histories; meaningful wall reduction; no >10% resource regression | Overlap/DOP off | `full-scrape-ab` |
| Band rankings | Split from solo ranking stage and retain dependency on completed BandMaintenance | Can run only after prune/current band input is terminal | Phase contract tests and isolated ranking parity | Exact band rank/stat/history rows | Existing monolithic `ComputeAllAsync` | `full-scrape-ab` |
| Solo projection prepare | Keep dormant until snapshot-overlay reader migration is promoted | No current optimization work | Existing reader migration parity gate | Full public/worker parity | Flag off | `full-scrape-ab` |
| Song rivals | Bound account concurrency and persist skip/recompute/input counts | Potential overlap with player stats later; not before query/resource evidence | Registered-account slice replay | Exact rivals/samples; lower query count and p95 | Existing task fan-out | `full-scrape-ab` |
| Leaderboard rivals | Bulk-load neighborhoods and selected neighbor scores; add input fingerprints | Recompute only users whose ranking neighborhood changed | One-account, then full registered-slice replay | Exact rows/samples; ≥25% query/wall reduction | Existing per-user algorithm | `full-scrape-ab` |
| Player stats | Add per-chunk timing | Usually seconds; leave serial unless recurring tail appears | Three comparable scrapes | No output difference; optimize only if material | Remove counters | `continuous-safe` |
| Checkpoint/cache warm | Remove verified PostgreSQL no-ops in a dedicated PR | Do not combine with progress or performance work | PostgreSQL and rollback-mode tests | Zero output/cache difference | Revert deletion | `scrape-boundary-deploy` |
| Final snapshot activation | Prove whether wave-two marker has a current consumer; collapse only after parity | Candidate removal, not assumed redundancy | Source/reference tests and full current-state/public checksums | Exact resume, projection, publication, and API behavior | Restore second activation | `full-scrape-ab` |
| Projection seal | Add exact scope count | Retain ordering boundary | Unit/integration coverage | Same sealed/deferred scope behavior | Remove counter | `continuous-safe` |
| Solo projection refresh | Replace full delete/reinsert with adaptive merge at low diff ratio; batch by instrument | Must remain before precompute | 0%, 0.1%, 1%, 10%, and full-change isolated replay | Exact current rows/ranks; lower WAL/dead tuples and ≥10% wall on low-change data; no >10% full-change regression | Full-rebuild mode | `full-scrape-ab` |
| Precompute | Add subphase timings, reuse leeway/band-score inputs, then test selective two-lane execution | Preserve API latency priority; do not enable all-phase parallelism first | Same phase inputs, cache fingerprint comparison | Exact cache keys/bytes/ETags; ≥10% wall reduction; public API p95 within gate | Serial mode/input rebuild | `full-scrape-ab` |
| Best-effort cleanup | Move after publication under pressure admission | Correctness-of-intent fix; typical measured benefit near zero | Failure injection and API/resource observation | Publication unchanged; cleanup failure cannot block it; live-read p95 within gate | Restore pre-publication call | `full-scrape-ab` |
| Publication | Persist scope-source, notification-plan, preparation, drain, exclusive, and cleanup timings | Ordering remains strict | Additive timing-only observation | Exact pointer/freeze/route parity; <1% overhead | Disable timing writes | `continuous-safe` |
| Improvement notifications | Record skip reason and detection-stage/scope timing | Already post-publication | Inspect conditions and several published scrapes | No silent starvation; exact notification state | Remove counters | `continuous-safe` |
| `PostScrapeRefresher` / deferred sync | Audit supported modes and retire or explicitly re-home | Verified no current production caller; removal is separate from timing/progress work | Reference/config/tests plus best-effort skip audit | No supported mode loses refresh/backfill/recovery | Revert deletion | `scrape-boundary-deploy` |

Default rejection is any correctness/publication difference or a sustained
greater-than-10% regression in API p95, phase wall clock, CPU, memory, WAL,
temp bytes, or disk I/O. Observability-only work targets less than 1% overhead.

## Settings simplification

Settings will use three primary visual groups:

1. **Health**
   - worker online/stale/offline;
   - update running/failed/stalled/idle;
   - retain prior-publication availability in failure copy.
2. **Progress and ETA**
   - stable current phase/subphase label;
   - exact phase progress and units when determinate;
   - indeterminate treatment when no final denominator exists;
   - estimated overall progress and ETA range/confidence.
3. **Publication timing**
   - current update start;
   - last successful publication;
   - next scheduled update.

Move worker instance IDs, operation timestamps, raw heartbeat age, diagnostic
details, phase attempts, and branches under a collapsed technical-details
disclosure. Move selected-player/band synchronization into a separate
selected-profile card because it is user-specific state, not service health.

### Browser acceptance

- No horizontal overflow or marquee for core status at 320, 375, 768, and
  1,440 CSS pixels.
- Determinate progress exposes accessible min/max/current values and meaningful
  text.
- Indeterminate progress exposes no numeric percentage.
- State changes appear within two foreground polls.
- One shared service-info request and at most one in flight.
- Worker stale, failed, deferred, restarted, unknown-total, resumed, and
  completed-with-warning states have tests.
- Default view contains no wall of `N/A` rows.
- Technical details are keyboard accessible and collapsed by default.
- Profile sync remains independently testable.
- Existing publication bootstrap/cache-reset behavior is unchanged.

## Architecture decision

See
[ADR 0005](../decisions/0005-post-scrape-modular-monolith.md).

### Migration sequence

1. Convert orchestration to an explicit dependency/resource graph without
   changing execution order.
2. Add guarded phase replay to the existing FSTService one-shot host.
3. Prove identical phase inputs/outputs and restart behavior in isolated
   PostgreSQL.
4. Extract a static contracts/implementation assembly only after dependency
   direction is clean or a second consumer needs it.
5. Consider a separate runner project/process only after the existing host is
   a measured obstacle.
6. Reconsider service extraction only if one independently scalable lifecycle
   has measured value greater than versioning, operations, and distributed
   consistency cost.

## Tiered staging, replay, and lineage

### Separation of concerns

- **Parser/network replay** validates response parsing, pagination, retry,
  provider terminal boundaries, and request provenance.
- **Post-phase replay** validates SQL, algorithms, projections, rankings,
  rivals, precompute, cleanup, and publication preparation.

No raw HTTP capture will be implemented until a parser/network requirement and
storage budget exist.

### Artifact tiers

| Tier | Contents | Purpose | Default |
|---|---|---|---|
| 0 | Exact catalog hash/snapshot, scope content and coverage fingerprints, phase/subphase timings, build/config/schema fingerprints | Always-on operational evidence and workload matching | First |
| 1 | Phase-scoped canonical PostgreSQL input datasets restored into isolated PostgreSQL | Rankings/projections/rivals/precompute/query development | On demand after capacity gate |
| 2 | Retained parsed page/spool representation plus manifests | Writer/current-state output-shape development | Only when a specific experiment needs it |
| 3 | Sanitized raw response bodies and request metadata | Parser/provider/retry replay | Deferred |

Current `OnlineBounded` production success paths have no retained parsed spool.
Tier 2 therefore requires an explicit tee/retention option or a canonical
candidate-row export. Failure-only artifacts are not a complete scrape package.

### Manifest and lineage

Every replay package and phase output requires:

- format/schema version;
- capture/package ID;
- source scrape and publication IDs where applicable;
- exact catalog content hash;
- source cut and UTC timestamps;
- git commit and OCI image digest;
- database major/extension/schema digest;
- allowlisted configuration and phase-plan fingerprint;
- file/table name, logical owner, row count, min/max keys/timestamps;
- compressed and uncompressed bytes;
- SHA-256 for each artifact plus a package root hash;
- parent input root hashes;
- phase ID/version and implementation digest;
- status, attempt, timing, resource metrics, and terminal error;
- no bearer tokens, cookies, credentials, resolved proxy endpoints, or private
  provider/account configuration.

Artifacts are immutable after sealing. An interrupted attempt receives a new
attempt directory/row; it never overwrites the prior attempt. Resume requires
all parent hashes to match.

### Isolated PostgreSQL

- Use the existing FSTService binary in a one-shot replay mode.
- Restore only phase-declared inputs, not the 3.3 TB database.
- Use a separate PostgreSQL container/database, no published ports, and no
  production connection credentials.
- Keep its data, scratch, exports, and outputs on the FST drive.
- Apply explicit CPU, memory, PID, and later disk-I/O limits.
- Give the replay mode no Docker control and no production publication
  authority.
- Require a fail-closed connection-target guard before replay execution.
- Compare baseline and candidate from the same sealed parent state.
- Emit canonical row/order/content/API-cache fingerprints.

### Same-drive capacity gates

Before any Tier 1+ artifact:

1. current scrape/publication is at a safe boundary;
2. public/API/PostgreSQL health passes;
3. no waiting lock or conflicting heavy operation;
4. exact input/export/output size estimate exists;
5. free bytes exceed package, two matched output workspaces, rollback reserve,
   and live scrape growth reserve;
6. the current 500 GiB rewrite gate and other storage reservations are not
   weakened merely to admit replay;
7. artifact cleanup/rebuild ownership is documented;
8. the external unplugged HDD is neither assumed nor used.

The first replay evidence is Tier 0. A bounded Tier 1 slice follows only after
the exact capacity plan is accepted.

## Production continuity and iteration loop

### Branch, PR, candidate image, full scrape, merge, and official deploy

Every accepted deployed iteration has its own branch and PR.

1. Create the next branch from the currently deployed/accepted clean master.
2. Implement one coherent iteration with synchronized canonical docs and
   rollback switch.
3. Run targeted tests, affected full suites, documentation validation,
   coverage/workflow gates, and build the candidate image locally from
   `FSTService/Dockerfile`.
4. Record source commit, local image digest/tag, configuration diff, schema
   compatibility, exact rollback image/config, and the iteration's numeric
   hypothesis.
5. For worker/DB/publication work, do **not** merge yet. Wait for a complete
   current scrape/post-process/publish/unfreeze boundary and hold the next
   worker run.
6. Deploy the local candidate image/config while the worker is held. Keep API
   and web on the accepted publication and verify the full public path.
7. Start one guarded candidate worker window and monitor through complete
   network scrape, post-processing, publication/unfreeze, notification
   completion, public/API parity, and resource evaluation.
8. Hold the worker before an unwanted second scrape.
9. Accept only with exact data/publication parity, public health, rollback
   evidence, and the iteration's performance/resource gate.
10. If rejected, restore the previous image/config, validate public health,
    preserve evidence, and close or revise the PR without merging.
11. If accepted, commit any evidence-driven docs/config adjustment on the same
    branch, push, obtain review, and merge the PR.
12. Allow the master workflow to rebuild official immutable SHA images.
13. Redeploy the official master image/config in place of the local candidate,
    verify image revision and the full public path, and confirm behavior still
    matches the accepted candidate.
14. Only then create the next iteration branch.

Web-only and API-only additive work may use the same branch/PR discipline
without a full worker scrape when it cannot affect worker, database, or
publication behavior. It still requires independent image/revision and public
path validation.

### Quality and image gates

- `dotnet test FSTService.Tests/FSTService.Tests.csproj`
- `dotnet build FSTService/FSTService.csproj -c Release`
- applicable web unit/shared/coverage/build/browser tests
- `node tools/check-docs.mjs`
- `git diff --check`
- Docker image build from repository source
- source/image revision proof
- no secrets or generated noise
- exact rollback

### Full-scrape A/B metrics

| Family | Required metrics |
|---|---|
| Correctness | Scope/manifests, rows/ranges/order/hashes, rankings, rivals, histories, caches/ETags, publication bindings/pointer, freeze/unfreeze, notifications |
| Time | Network, writer, every post phase/subphase, preparation, drain, exclusive commit, notification completion |
| PostgreSQL | WAL, temp, buffers/IO, locks/waits, deadlocks, checkpoints, relation/index growth, dead tuples |
| Process | Worker/API/PostgreSQL CPU, peak RSS, PID/thread/connection counts |
| Public health | Readiness, web shell, representative API routes, cached/miss p50/p95/p99, WebSocket/publication rotation |
| Provider | Requests, useful/wire rate, retries, timeouts, blocks, 403/429/5xx, endpoint health |
| Artifact | Input/output bytes, import/replay/seal/checksum time |

## Overlap decision

### Current decision

Full scrape N+1 during post-processing N is rejected.

- `scrape_publication_state` and its working pointer are singleton.
- `StartScrapeRun` allocates and replaces the working generation.
- Rankings, rivals, projections, cache staging, band state, and publication
  preparation are global mutable owners.
- Capture and post-scrape provider work share the Epic/proxy/DOP budget.
- Current storage and I/O headroom are insufficient for an unmeasured second
  lane.
- Current progress and recovery models assume one foreground operation.

Incremental snapshot storage is tens of GB, not another 2.381 TB. That
correction does not make overlap safe.

### Future research-only scheduler gates

Do not implement overlap until all are true:

1. capture output is immutable and does not mutate production database state;
2. publication supports multiple named candidates without overwriting one
   working pointer;
3. every derived table has generation/candidate ownership or an isolated
   sandbox;
4. provider work has explicit lane budgets;
5. storage capacity and retention are stable;
6. DB/disk/CPU/memory resource A/B proves overlap safe;
7. publication remains strict FIFO;
8. maximum in-flight is one post-process plus one capture, with at most one
   sealed backlog package;
9. no N+2 starts while backlog exists;
10. any >10% public/resource regression or any correctness difference rejects
    overlap.

No wall-clock benefit is forecast until those gates are measured.

## Prioritized implementation roadmap

Each iteration below is a separate branch/PR.

### Parallel storage and reclaim evidence

**Establish an exact executable snapshot-retention plan**

- Planner-estimator correctness is `continuous-safe`: it changes only bounded
  read-only evidence and fail-closed eligibility, and the live harness can
  validate it without a scrape or deployment.
- Use only plans with complete protected-ID coverage, reconciled row/byte
  totals, and `CanExecute=true`.
- Current publication-`1293` catalog evidence reconciles but all nine plans are
  blocked: protected IDs `1293` and `1291` are absent from MCV statistics,
  `n_live_tup` and `reltuples` are stale/inconsistent, and unknown MCV
  remainder is material.
- Informational candidate purge estimates are about `2.52` billion rows /
  `1.46 TB`; executable purge estimates remain zero and full retained workspace
  is about `2.61 TB`.
- When catalog statistics are stale or partial, choose and validate a bounded
  evidence source: maintenance-window statistics refresh with adequate target,
  durable per-snapshot rollup metadata, or exact partition counts under a
  separately approved load window.
- Persist exact candidate partitions, protected snapshot IDs/publications,
  retained/purge rows and bytes, required rewrite workspace, rollback objects,
  query/runtime cost, and the current `500 GiB` free-space gate.
- Do not enable rewrite, lower the 500 GiB gate, delete rows/indexes, repack, or
  move data.
- Execution remains `parity-gated-maintenance` and blocked until statistics,
  exact-count, parity, and workspace evidence all agree.

### PR-5: same-binary isolated replay

**Class:** `full-scrape-ab` before any production-facing use.

**Mandatory starting and acceptance gate:** PR-4 confines operations within a
caller-supplied package root but deliberately does not select or authorize that
root. PR-5 must add a fail-closed root admission policy before any CLI/runtime
entry point can create a package:

- production-derived roots must resolve beneath an operator-approved location
  on the 4 TB FST drive;
- bounded tests may use only repository or explicitly assigned session-test
  roots;
- canonical roots and existing ancestors must reject symlinks/reparse points,
  traversal, normalization aliases, alternate drives, generic temporary
  directories, and PostgreSQL data directories; and
- rejection must occur before database, network, package, import, or phase
  execution.

- guarded FSTService replay mode;
- isolated connection-target refusal for production;
- phase-from/through or stable single-phase invocation;
- no-publication mode;
- phase-scoped import/output manifest;
- bounded Tier 1 dataset;
- baseline/candidate runner against the same parent.

### PR-6: verified dead/no-op path cleanup

**Class:** `scrape-boundary-deploy`

- audit best-effort skip reasons/starvation first;
- conditionally remove PostgreSQL checkpoint/cache warm and legacy rank work;
- retire unreachable LegacyBandScrape, unused `PostScrapeRefresher`, and dormant
  deferred sync only after supported-mode tests;
- preserve rollback/reference evidence.

### PR-7 and later: measured optimization iterations

Order is evidence-driven:

1. BandMaintenance current projection refresh, starting with the measured
   `53,543` considered / `8,020` refreshed scope and row-churn path;
2. solo current-projection write reduction;
3. rank-history query path and one-variable concurrency/overlap experiment;
4. leaderboard-rivals batching/fingerprints;
5. precompute input reuse/selective concurrency;
6. best-effort cleanup reorder;
7. snapshot activation consolidation;
8. storage-retention execution after parity/capacity gates;
9. capture-only overlap research after architecture/storage redesign.

## Testing strategy

### Schema and persistence

- fresh Testcontainers schema;
- idempotent repeated initialization;
- exact columns/defaults/indexes;
- timing insert/read/retention;
- old production-compatible shape;
- upgrade when table already exists;
- failure logging remains non-blocking.

### Phase contracts and progress

- DAG is acyclic and dependency-complete;
- stable IDs do not change accidentally;
- determinate progress is monotonic after denominator finalization;
- unknown totals remain indeterminate;
- liveness heartbeat cannot advance progress;
- concurrent branches do not overwrite each other;
- restart creates a new attempt and preserves old history;
- terminal failures retain criticality;
- API v1 fields remain compatible;
- ETA confidence/range backtests and hides on weak evidence.

### Replay

- manifest canonicalization and root checksum;
- corruption and parent mismatch rejection;
- isolated-target guard;
- deterministic same-image rerun;
- baseline/candidate from identical parent;
- interrupted resume;
- phase-scoped input sufficiency;
- no production publication authority;
- output row/order/hash/API-cache parity.

### Web

- shared request ownership;
- foreground/background polling;
- all lifecycle states;
- responsive widths;
- accessibility;
- selected-profile separation;
- stale worker and prior-publication messaging;
- no progress number for unknown totals.

### Full-scrape A/B

- incomplete scrape, missing page, provider boundary, writer failure;
- phase critical/best-effort failure;
- worker restart and no-progress watchdog;
- publication contention/defer/recovery;
- unchanged/low/high-change data;
- public API/cache/WebSocket parity;
- rollback to prior image/config and prior published generation;
- disk/WAL/temp/resource gates.

## Continuous improvement loop

For every accepted iteration:

1. classify the workload/config/image as comparable or exclude it;
2. compare with the immediately prior accepted baseline;
3. update the phase scorecard only from successful comparable runs;
4. record correctness, wall, resources, storage, provider, and public health;
5. accept/reject each hypothesis explicitly;
6. preserve rollback;
7. merge only after the local candidate completes its required observation;
8. redeploy and verify the official master image;
9. create the next branch only after official parity;
10. stop optimization when no measured bottleneck has a safe next hypothesis.

Default rejection remains greater than 10% sustained regression in protected
metrics. Correctness/publication differences reject regardless of speed.

## Rejected hypotheses

| Hypothesis | Decision | Reason |
|---|---|---|
| Microservices now | Rejected | No independently owned data/service boundary offsets distributed consistency, operations, tracing, resource duplication, and version skew |
| Hot-loaded DLL plugins | Rejected | Assembly loading is not process or binary isolation and adds version/type complexity |
| New runner project now | Rejected | Existing binary can provide guarded one-shot replay; no second consumer exists |
| DuckDB/Parquet live source of truth | Rejected | PostgreSQL owns publication and durable correctness; artifacts only |
| Hard-coded browser weights as exact progress | Rejected | Current order includes concurrent work and measured weights are materially wrong |
| Exact ETA | Rejected | ETA is necessarily an estimate and must expose range/confidence |
| Full raw-HTTP capture now | Rejected | No parser requirement and insufficient storage headroom |
| Successful parsed spool already exists | Rejected as fact | Live `OnlineBounded` success path does not retain one |
| Full N+1 overlap now | Rejected | Singleton publication/global mutable ownership/provider/resource constraints |
| Another candidate costs 2.381 TB | Rejected as fact | Fixed partitions append one generation; 2.381 TB is cumulative |
| Complete BandMaintenance and complete rankings are independent | Rejected | Band prune writes `band_entries`; band rankings read it |
| Blind DOP increase | Rejected | Prior PostgreSQL OOM and current WAL/IO risks |
| Immediate snapshot rewrite/reclaim | Blocked | Exact planner evidence and live-scrape parity are absent; current free space is below the configured rewrite gate |
| Restore removed ranking-delta/harness code unchanged | Rejected | Historical removal is evidence of obsolete complexity, not a current implementation template |

## Unresolved evidence gaps and next probes

| Gap | Evidence class | Exact next probe | Gate |
|---|---|---|---|
| Current band projection rewrite feasibility | Unknown | Bounded plan/same-input checksum probe for the `53,543` considered / `8,020` refreshed scope path | Separate one-variable A/B; exact projection/publication parity; no >10% resource regression |
| Exact snapshot reclaim plan | Unknown | Establish complete protected-ID row distribution and exact workspace evidence from a bounded validated source | No rewrite or gate reduction |
| Improvement-notification gaps | Unknown | Record skip reasons and inspect markers/coverage on recent publications | Do not call the phase starved or removable without evidence |
| Best-effort skip/starvation | Unknown | Compare requested phases, outcomes, skip reasons, pressure decisions, and feature conditions | Required before dead-path PR |
| Projection diff ratios | Unknown | Persist aggregate would-insert/update/delete metrics | Required before merge strategy |
| Rival tail cause | Unknown | Per-account decisions/query counts/timing | No concurrency change before attribution |
| Precompute subphase cost | Unknown | Stable subphase timing and cache counts | No parallel flag promotion |
| Replay dataset size | Unknown | Tier 0, then bounded Tier 1 export/import size/runtime | Must fit same-drive reserve |
| Raw compression/dedup | Deferred unknown | Only after parser requirement; bounded sample | No current work |
| Parallel resource behavior | Unknown | Isolated same-input resource-capped A/B | Any >10% regression rejects |
| ETA accuracy | Unknown | Backtest phase-boundary history, then collect within-phase checkpoints | Hide ETA until confidence gate |
| Phase-attempt lifecycle | Unknown | Measure row growth and scrape-log deletion/lock behavior before proposing an FK or explicit retention | Separate evidence; no locking FK or cleanup by default |
| Future overlap benefit | Deferred unknown | Only after isolated capture and publication redesign | Research-only |

## Implementation gate

This tandem plan is accepted for implementation after local outbox rendering.

- GPT-5.6 Sol owns every implementation, test, benchmark, deployment,
  production probe, A/B, rollback, commit, and promotion decision.
- Approval of this roadmap is not authorization to bypass the current
  live-safety, parity, publication, provider, storage, rollback, or maintenance
  gate for any later action.
- PR-5 same-binary isolated replay is the next implementation boundary and
  cannot pass acceptance without the approved FST-root admission gate.
- Current-projection optimization is a separate future full-scrape A/B; it
  cannot be combined with PR-3 Settings work.
- Snapshot-retention execution remains a separate parity- and capacity-gated
  maintenance task.
