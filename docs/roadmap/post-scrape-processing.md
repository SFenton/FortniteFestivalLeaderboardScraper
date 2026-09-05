---
status: roadmap
owner: worker
last_verified: 2026-09-04
last_verified_commit: f266ecb8
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
  - FSTService/Persistence/InstrumentDatabase.cs
  - FSTService/Persistence/MetaDatabase.cs
  - FSTService/Persistence/PublishedSoloScopeSql.cs
  - FSTService.Tests/Unit/InstrumentDatabaseTests.cs
  - FSTService/Persistence/Maintenance/DatabaseRetentionMaintenanceService.cs
  - FSTService/Persistence/Maintenance/SnapshotGenerationRetentionPlanner.cs
  - FSTService/Persistence/Maintenance/SnapshotGenerationRetentionOracle.cs
  - FSTService/Persistence/Maintenance/SnapshotGenerationQuarantineSchema.cs
  - FSTService/Persistence/Maintenance/SnapshotGenerationDropSchema.cs
  - tools/FstSnapshotGenerationQuarantine/
  - tools/FstSnapshotGenerationDrop/
  - tools/postgres-snapshot-generation-restore.py
  - tools/FstSnapshotGenerationRetirement/
  - docs/database/SnapshotGenerationRetirementControlPlane.md
  - FSTService/Api/HealthEndpoints.cs
  - packages/core/src/api/serverTypes.ts
  - docs/architecture/data-publication-flow.md
  - docs/architecture/data-storage.md
  - docs/components/worker.md
  - docs/database/SnapshotReuseRunbook.md
  - docs/database/StaleSoloRankIndexRetirementRunbook.md
  - docs/database/SnapshotGenerationRetentionSafety.md
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
- The one-instrument-at-a-time snapshot-generation conversion is accepted.
  Scrape `1310` proved all nine generation writer paths through publication,
  notifications, registration drain, and worker exit.
- Make recurring generation retention the active storage lane in evidence
  gates. The implemented first slice is default-off/report-only and has no
  executable work state. The separate archive-only CLI and isolated prover are
  implemented, synthetically validated, and live-accepted on unchanged Pro
  Cymbals snapshot `1314`. Live cycles `5/1325` through `9/1329` have exact
  agreement, zero blockers, publication rotation, and genuine candidate-set
  changes. The no-Docker-socket quarantine/reattach executor is implemented
  and live-accepted on Pro Cymbals snapshot `1314`. The separate
  non-cascading DROP/logical-restore implementation is repository-ready but
  is not live-accepted. Official scrape `1333`, publication `157`, and cycle
  `13` are accepted; its disposable PostgreSQL 17 archive/proof plus
  DROP/restore drill is also accepted locally. Q1 operation
  `1b44941dc5d5ea806dabc2187c3cffed` passed scrape `1335`, publication
  rotation `159` to `162`, cycle `15`, and the publication-162 soak. Its
  first reattach failed closed with `42P07` and no residue after an unrelated
  new child reused its leaf-index name. Later work reached an independently
  approved DROP call, which failed before DDL with `42703` because the empty
  initial operation table lacked semantic columns. After explicit upgrade,
  operation `333ba4b9fb69dbc098d127f0008ec709` committed under plan digest
  `fa45ca20c2c975e543b7d539d3b27cb05c5d80ff16345665205f2355eb67d5dc`.
  Restore planning then failed before output/mutation on non-authoritative
  Python reserialization. The corrective branch now implements immutable
  exact-DROP tool authorization and a tool-only repair package. Final
  H3 failed read-only on a reserved PostgreSQL alias. H4 passed that lookup
  but failed before output or mutation on canonical decimal-string
  opclass/collation OID arrays. H5 then committed the mandatory exact restore;
  its raw-ZIP route attestation failed before database write. H6-prime now
  keeps the strict post-restore parity validator and adds a separate
  exact-manifest-pinned midnight shop rollover attribution for the sole
  historical route difference. Authorization `0ed3cd71...` attested and
  finalized the restore, and candidate scrape `1337`, publication `171`,
  notifications, and cycle `17` completed with zero failures and exact
  planner/oracle agreement. Promote through PR/CI, deploy official images, and
  run the official-image confirmation scrape.
  Sparse compaction remains a separate unresolved iteration.
- Keep exact archive/restore, retained-source parity, rollback, capacity, and
  live API gates for every remaining instrument and for any future recurring
  generation-retention owner.
- After a safe capacity window exists, use the accepted timing foundation to
  optimize measured bottlenecks rather than inferred phase cost. The first
  BandMaintenance target remains current projection refresh.

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
| Modularity | Good: phases are testable and retired PostgreSQL no-op wrappers, unused refresher wiring, and deferred post-scrape sync are removed; the orchestrator remains large enough to justify stable internal phase contracts | High |
| Live progress observability | Good: normalized durable phase/subphase attempts, service-info v2, watchdog progress/liveness separation, and the responsive Settings bare-bar experience are accepted | High |
| Performance | Poor: recent full-scrape p50 is about 8.58 hours and recorded post-processing consumes about 5.6 hours on scrape 1290 | High |
| Storage sustainability | Improving but incomplete: all nine partitions and writer paths are accepted, scrape 1310 left about 2.702 TB free, and recurring whole-child retirement plus sparse-child compaction remain | High |
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
| Snapshot retention | The generation-child report-only planner did not exist at the research boundary. The legacy rewrite path was disabled and its 500 GiB free-space gate blocked rewrite |

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
| `BandMaintenance` | 10+ | About 171 minutes | Scrape `1306` took 197.7 minutes; current projection alone took 166.0 minutes for 13,118/54,301 scopes, reinforcing it as the first target |
| `ComputeRankings` | 10+ | About 68 minutes | Scrape `1306` took 72.7 minutes; rank-history snapshots were 37.7 minutes and band rankings 18.0 minutes |
| `RefreshRegisteredUsers` | 10+ | About 24 minutes | Scrape `1306` completed in 4.1 minutes; retain the historical 44-minute tail until more bounded samples exist |
| `Cleanup.PrecomputeAll` | 10+ | About 13 minutes | Scrape `1306` took 13.3 minutes; stable |
| `Cleanup.SoloCurrentProjection` | 10+ | About 12 minutes | Scrape `1306` took 16.0 minutes for 6,053 scopes |
| `LeaderboardRivals` | 9 | 8m28s current | Accepted scrape `1318` reduced the matched `1317` control from 4h26m44s to 8m28s; remaining work is selective recomputation through input fingerprints |
| `Rivals` | 13 | About 50 seconds | Scrape `1315` took 40m21s for eight accounts: preload took 4m36s, the full account then took 35m44s and produced 53,665 samples |
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
All nine instrument roots now use snapshot-ID children. Their accepted
generation migrations returned `2,699,337,936,896` filesystem bytes in total,
and measured FST free space after the Pro Drums final drop is
`2,728,956,956,672` bytes. Another scrape appends one generation child per
instrument rather than another cumulative copy.

**Verified:** a complete guarded scrape is required between instrument
migrations. Scrapes `1304`, `1305`, `1306`, and `1307` proved
generation-aware writes for all five migrated instruments. Scrape `1307`
completed publication `98`, notification recovery, registration drain, and
normal worker exit; the post-Solo Drums gate is accepted. Solo Bass was then
migrated. Scrape `1308` failed closed on one 13-row Solo Bass writer batch
when concurrent cross-instrument generation DDL collided on a truncated
inherited-index name. Publication `98` remained current and unfrozen. The
global generation-DDL lock fix required a clean full retry.

**Verified:** scrape `1309` accepted the retry. All `8,484` manifests and
`604,907` page statuses completed, all six physical generation children
matched published-source sums, every default child stayed empty, publication
`101` became current/unfrozen, notifications and registration drain completed,
and the worker exited `0`.

**Verified:** Pro Vocals retained `34,514,935` of `633,981,317` archived rows
across snapshots `1302-1307` and `1309`, returned `350,852,210,688`
filesystem bytes, left an empty default child, and preserved exact
publication/reference/API parity.

**Verified:** Pro Cymbals retained `400,455` of `8,661,068` archived rows
across snapshots `1302-1307` and `1309`, returned `4,757,069,824` filesystem
bytes, left an empty default child, and preserved exact
publication/reference/API parity.

**Verified:** Pro Drums retained `190,168` of `5,473,658` archived rows across
snapshots `1302-1307` and `1309`, returned `2,942,509,056` filesystem bytes,
left an empty default child, and preserved exact publication/reference/API
parity.

**Verified:** scrape `1310` accepted all nine generation-write paths. All
`8,484` manifests and `605,239` persisted page statuses succeeded, all nine
physical children exactly matched published-source sums, defaults remained
empty, publication `103` became current/unfrozen, notification runs emitted
`101` player and `47` band events, registration drain completed, and the
worker exited `0`.

**Verified:** the first physical-reference inventory contains six
failed-scrape `1308` children with no active/projection/named-publication
source, totaling `12,908,355,584` bytes. That is not accepted retention
eligibility while same-instrument unreplayed writer-failure evidence remains.
The nine `1310` children total `15,870,648,320` bytes, while older successful
generations remain sparsely pinned. Whole-child retirement is necessary but
does not by itself prove bounded steady-state storage.

**Unknown:** single-leaf archive/restore behavior, guarded detach/reattach
versus direct-drop lock duration, no-socket mailbox/prover crash recovery,
measured eligible-child arrival rate, archive runway, and sparse-compaction
cost until the gated drills and canaries pass.

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
| `RankRecompute` | `GlobalLeaderboardPersistence` | Changed songs and legacy live rows | Legacy stored ranks | Publication-critical rollback path; completes without update while legacy writes are off and may never be skipped |
| `FirstSeenSeason` | `FirstSeenSeasonCalculator` | Catalog, season windows, Epic history | First-seen season rows | Best effort |
| `AccountNameResolution` | `AccountNameResolver` | Unresolved account IDs | Display-name state | Best effort |
| `RefreshRegisteredUsers` | `CyclicalSongMachine` | Registered accounts, catalog, seasons, Epic lookups | Overlays, session history, scope checkpoints | Publication-critical |
| `ActivateShadowSnapshotsEarly` | `GlobalLeaderboardPersistence` | Candidate snapshot rows and expected scopes | Active snapshot state | Publication-critical |
| `BandExtraction` | `PostScrapeBandExtractor` | Band context, max scores | Band entries/member rows, team membership/configuration summaries, and impacted scopes | Publication-critical |
| `LegacyBandScrape` | `BandScrapePhase` | Legacy band network path | Band entries | Publication-critical; reachable only through direct `--band-post-scrape`, suppressed by normal `BandScrape` |
| `RegisteredPlayerBandDiscovery` | Discovery orchestrator | Registered accounts, season windows, Epic | Discovered teams/scopes | Best effort |
| `RegisteredBandTargetedProcessing` | Targeted orchestrator | Registered teams, Epic | Targeted team rows/scopes | Best effort |
| `BandMaintenance` | Band persistence/projection builders | Band entries/members and impacted teams/scopes | Pruned band state plus search and current projections | Publication-critical |
| `ComputeRankings` | `RankingsCalculator` | Current solo/band state, populations, max scores | Solo/composite/family/combo/band rankings and histories | Publication-critical |
| `PrepareSoloCurrentProjectionForDerived` | `SoloCurrentProjectionBuilder` | Snapshots and overlays | Validated current projection | Publication-critical, currently flag-dependent |
| `Rivals` | `RivalsOrchestrator` | Current scores/ranks and dirty fingerprints | Song-rival rows/samples | Publication-critical |
| `LeaderboardRivals` | `LeaderboardRivalsCalculator` | Rankings and player scores | Ranking-neighbor rival rows/samples | Publication-critical |
| `PlayerStatsTiers` | Post-scrape orchestrator | Player profiles, max scores, populations | Stats-tier rows | Publication-critical |
| `Checkpoint` | Reserved progress/history descriptor | None | None | No execution policy; stable ID retained for plan-v2 evidence compatibility |
| `ActivateShadowSnapshots` | `GlobalLeaderboardPersistence` | Same candidate snapshot state | Wave-two finalization marker | Publication-critical |
| `SealSoloCurrentProjectionScopes` | Projection builder/context | Stale and notification scopes | Publication scope boundary | Publication-critical |
| `Cleanup.SoloCurrentProjection` | Projection builder | Snapshots, overlays, sealed scopes | Candidate current projection | Publication-critical |
| `Cleanup.PrecomputeAll` | `ScrapeTimePrecomputer` | All derived candidate state | Generation-staged API responses | Publication-critical |
| Retention/legacy cleanup | Post-scrape and retention services | History/snapshot metadata | Bounded deletion or report | Best effort |
| Publication | Worker and `MetaDatabase.Publication` | Complete candidate and required bindings | Current publication pointer | Publication-critical |
| `ImprovementNotifications` | Notification recovery service | Published generation and projection plan | Notification state/events or explicit skip reason | Best effort, post-publication |

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
| `RankRecompute` | Candidate completes the critical contract without legacy work when writes are disabled | Retains rollback behavior and criticality when the flag is on; critical skips fail closed | Unit/integration tests with legacy flag on/off plus full scrape parity | Zero output difference; no missing legacy rollback behavior | Revert conditional no-work path | `full-scrape-ab` |
| `FirstSeenSeason` | Add song/request counts only | Already parallel; current cost too small for algorithm work | Three comparable scrape observations | No telemetry overhead above 1%; optimize only if >2% wall | Remove counters | `continuous-safe` |
| `AccountNameResolution` | Add batch/account counts only | Already parallel; not a bottleneck | Three scrape observations | Same names and retries | Remove counters | `continuous-safe` |
| `RefreshRegisteredUsers` | Measure planned scopes, skipped/fetched/update counts, and per-account tails; then evaluate freshness-driven reduction | Do not raise concurrency before provider/load evidence | Identical captured response replay on isolated inputs | Exact overlay/history/scope parity; lower requests or wall; no retry amplification | Feature flag to full recurring path | `full-scrape-ab` |
| Early snapshot activation | Use expected/manifests rather than scanning candidate snapshot rows; evaluate one activation | Potential query rewrite; final activation removal requires separate proof | Bounded plan and isolated current-state checksums | At least 30% phase reduction; exact snapshot state and current reads | Restore existing SQL/calls | `full-scrape-ab` |
| `BandExtraction` | Make impacted teams/scopes reflect actual changed rows | Already parallel; do not increase DOP first | Same input rows, compare impacted-key sets and band outputs | Exact band rows; fewer downstream scopes | Keep broad-impact mode | `full-scrape-ab` |
| `LegacyBandScrape` | Retain direct `--band-post-scrape`; remove only duplicate await | Mode audit proved the direct legacy launch remains supported | CLI/config matrix plus targeted tests | No supported mode loses band acquisition | Revert duplicate-await deletion | `full-scrape-ab` |
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
| Song rivals | Accepted: bulk-load target scores once per instrument, reuse them, and cap accounts at 2. Next batch selected-rival account/song profile reads per instrument and batch selection-state fingerprints | Potential overlap with player stats later; do not raise account concurrency before query/resource evidence | Production-shaped account/song-pair fixture, then one full registered-account slice | Exact rivals/samples/fingerprints; at least 25% query or phase-wall reduction; bounded profile rows/RSS; no greater than 10% protected resource regression | Retain the current per-rival reads and accepted preload/account cap | `full-scrape-ab` |
| Leaderboard rivals | Add input fingerprints and selectively recompute only accounts whose ranking neighborhood or relevant scores changed | Accepted instrument-first default-4 batching is the baseline; fingerprints must preserve atomic user/instrument replacement and publication semantics | Same-input full registered slice with unchanged, one-account, one-instrument, and broad-change cases | Exact rows/samples/state; unchanged inputs skip safely; changed inputs match forced recomputation; no greater than 10% protected resource regression | Force all accepted batches to recompute | `full-scrape-ab` |
| Player stats | Add per-chunk timing | Usually seconds; leave serial unless recurring tail appears | Three comparable scrapes | No output difference; optimize only if material | Remove counters | `continuous-safe` |
| Checkpoint/cache warm | Candidate removes verified PostgreSQL no-ops | Stable checkpoint ID remains reserved; no persistence contract remains | PostgreSQL API-absence and worker-flow tests plus full scrape parity | Zero output/cache difference | Revert deletion | `full-scrape-ab` |
| Final snapshot activation | Prove whether wave-two marker has a current consumer; collapse only after parity | Candidate removal, not assumed redundancy | Source/reference tests and full current-state/public checksums | Exact resume, projection, publication, and API behavior | Restore second activation | `full-scrape-ab` |
| Projection seal | Add exact scope count | Retain ordering boundary | Unit/integration coverage | Same sealed/deferred scope behavior | Remove counter | `continuous-safe` |
| Solo projection refresh | Replace full delete/reinsert with adaptive merge at low diff ratio; batch by instrument | Must remain before precompute | 0%, 0.1%, 1%, 10%, and full-change isolated replay | Exact current rows/ranks; lower WAL/dead tuples and ≥10% wall on low-change data; no >10% full-change regression | Full-rebuild mode | `full-scrape-ab` |
| Precompute | Add subphase timings, reuse leeway/band-score inputs, then test selective two-lane execution | Preserve API latency priority; do not enable all-phase parallelism first | Same phase inputs, cache fingerprint comparison | Exact cache keys/bytes/ETags; ≥10% wall reduction; public API p95 within gate | Serial mode/input rebuild | `full-scrape-ab` |
| Best-effort cleanup | Move after publication under pressure admission | Correctness-of-intent fix; typical measured benefit near zero | Failure injection and API/resource observation | Publication unchanged; cleanup failure cannot block it; live-read p95 within gate | Restore pre-publication call | `full-scrape-ab` |
| Publication | Persist scope-source, notification-plan, preparation, drain, exclusive, and cleanup timings | Ordering remains strict | Additive timing-only observation | Exact pointer/freeze/route parity; <1% overhead | Disable timing writes | `continuous-safe` |
| Improvement notifications | Candidate records skip reason; detection-stage/scope timing remains future work | Already post-publication | Inspect conditions and full candidate publication | No silent starvation; exact notification state | Revert skip telemetry | `full-scrape-ab` |
| `PostScrapeRefresher` / deferred sync | Candidate removes both dead surfaces | Caller history and current recurring/run-once owners are explicit | Reference/config/DI/tests plus full scrape parity | No supported mode loses refresh/backfill/recovery | Revert deletion | `full-scrape-ab` |

Default rejection is any correctness/publication difference or a sustained
greater-than-10% regression in API p95, phase wall clock, CPU, memory, WAL,
temp bytes, or disk I/O. Observability-only work targets less than 1% overhead.

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

The default-off generation-child report-only control plane, archive-only
package/proof tool, quarantine/reattach executor, and guarded DROP/logical
restore tier are implemented and live-accepted. Candidate scrape `1345` and
official scrape `1346` closed the DROP-tier promotion with zero failures,
clean 55-route captures, and exact cycles `25`/`26`.

Cycle `33` / scrape `1353` then passed the separate large-child recovery gate
on Solo Guitar snapshot `1311`: `3,518,955,520` source bytes,
`6,888,770` rows, a `272,084,869`-byte archive, exact network-none restore
fingerprint/catalog parity, complete cleanup, and no source mutation.

Current code still has no automatic-retirement execution path. The first
default-off host control-plane slice now covers bounded immutable
authorization, status, operator deactivation, reconciliation, and
largest-first plan persistence only. It has no archive/Docker process,
admission lease, source mutation, or worker/API integration. Scrape `1308`
remains protected wherever unreplayed writer-failure evidence exists.

Archive execution is the next separate implementation gate. It must reuse the
accepted immutable planner/archive/quarantine/DROP evidence while proving exact
container binding, cooperative cancellation and owned-resource cleanup,
full-duration admission, and interruption-safe provenance before any command
can invoke `pg_dump` or a proof container.

### Next iterations

Order is evidence-driven:

1. implement recurring generation retention in gated tranches:
   - merge and deploy the default-off plan-only control plane, then collect
     read-only largest-first plan/reconcile evidence across terminal cycles;
   - add archive execution only after exact source-container, admission-loss,
     process-start, cancellation-cleanup, expiry, and transition-failure gates
     pass independent review;
   - implement default-off automatic retirement of at most one archive-first
     eligible child per terminal cycle only after those execution gates;
   - validate it through isolated failure/recovery tests and one complete
     dual-lane scrape candidate before any enablement;
   - separately gated sparse-child compaction before claiming bounded
     steady-state storage;
2. review and qualify the freeze-safe publication API cache candidate. The
   repository implementation reuses canonical rows, eagerly adds songs plus
   bounded top-10 song/instrument rows, and lazily admits only overview sizes
   25/50 after sub-11 ms measured compute p95. Promotion still requires one
   full scrape/publication window, same-publication freeze injection, exact
   key/JSON/ETag parity, and no protected precompute/WAL/API regression;
3. BandMaintenance current projection refresh. PR #47 merges the
   implementation default-off: seven same-key `band_member_stats` aggregates
   become one lateral aggregate only when the candidate switch is enabled.
   Schema and fixture tests prove `member_index` uniqueness inside the query
   key and exact parity for missing rows and nullable stat columns.
   Option-parity replay and primed isolated PostgreSQL tests cover unchanged
   discovery and zero/all/one/mixed changed scopes. A 64-scope/2,048-row
   fixture preserves exact output/state hashes and successful transaction
   counts while deriving unchanged command/round-trip estimates and an
   aggregate-pass reduction from `14,336` to `2,048` (`-85.714%`).
   PostgreSQL `EXPLAIN` independently measures seven scans versus one.
   Production enablement remains pending, not accepted: capacity must first
   restore a full-scrape window, then a matched full-scrape A/B must pass exact
   publication/data parity and the protected `>10%` regression rule;
4. solo current-projection write reduction;
5. rank-history query path and one-variable concurrency/overlap experiment;
6. song-rivals selected-profile batching and fingerprint batching;
7. leaderboard-rivals input fingerprints/selective recomputation;
8. precompute input reuse/selective concurrency;
9. best-effort cleanup reorder;
10. snapshot activation consolidation;
11. storage-retention execution after parity/capacity gates;
12. capture-only overlap research after architecture/storage redesign.

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
- deterministic and production-option-parity execution profiles;
- all-unchanged and mixed changed-scope filtering;
- exact successful transaction and derived command/round-trip/member-stat
  aggregation-pass deltas;
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
| Current band projection rewrite feasibility | Unknown | Option-parity replay or a separate bounded probe for the `53,543` considered / `8,020` refreshed scope path; deterministic PR-5 timing is not production-comparable | Separate one-variable A/B; exact projection/publication parity; no >10% resource regression |
| Exact snapshot reclaim plan | Unknown | Establish complete protected-ID row distribution and exact workspace evidence from a bounded validated source | No rewrite or gate reduction |
| Projection diff ratios | Unknown | Persist aggregate would-insert/update/delete metrics | Required before merge strategy |
| Rival tail cause | Partially measured | Batch and benchmark per-score selection-state fingerprint neighborhoods; preserve exact fingerprints and skip decisions | No broader concurrency increase; accepted account cap remains `2` |
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
- Durable publication-keyed API caching is the next active implementation;
  broader snapshot-capacity recovery remains separately gated.
- Current-projection optimization remains a separate later full-scrape A/B.
- Snapshot-generation DROP/restore is implemented as a separate manual,
  default-inert maintenance surface. Scrape `1333`, cycle `13`, and its
  disposable drill are accepted. DROP operation
  `333ba4b9fb69dbc098d127f0008ec709` is committed; mandatory authenticated
  logical restore and confirmation remain. The corrective branch now carries
  separate immutable tool authorization and a nonduplicating repair package.
  H3 authorization failed read-only on a reserved SQL alias; H4 authorization
  passed that lookup but failed read-only on string-serialized OID arrays. H5
  restored the child exactly, but route attestation stopped on volatile ZIP
  bytes. H6-prime's hash-only midnight shop bridge, strict stabilized pair,
  live authorization/finalization, and candidate confirmation scrape are
  accepted. PR/official-image promotion remains.
  Automatic retention is still a later parity- and capacity-gated task.
