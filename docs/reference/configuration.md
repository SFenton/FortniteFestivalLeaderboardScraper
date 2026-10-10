---
status: canonical
owner: operations
last_verified: 2026-09-30
last_verified_commit: b801fdf3
sources:
  - FSTService/appsettings.json
  - FSTService/ScraperOptions.cs
  - FSTService/Scraping/PiaRegionRotator.cs
  - FSTService/Scraping/ProxyPool.cs
  - FSTService/SongCatalogRefreshWorker.cs
  - FSTService/Scraping/ItemShopService.cs
  - FSTService/StartupInitializer.cs
  - FSTService/Scraping/PathGenerationModels.cs
  - FSTService/Scraping/PathDataStore.cs
  - FSTService/Persistence/PublicationPathArtifactSchema.cs
  - FSTService/Scraping/ScrapePassPathIngestion.cs
  - FSTService/Program.cs
  - FSTService/FeatureOptions.cs
  - FSTService/FeedbackOptions.cs
  - FSTService/Scraping/PostScrapeOrchestrator.cs
  - FSTService/Persistence/MetaDatabase.cs
  - FSTService/DatabaseMaintenanceOptions.cs
  - FSTService/Persistence/Maintenance/ServiceMaintenanceLock.cs
  - FSTService/Persistence/Maintenance/SnapshotGenerationRetentionPlanner.cs
  - FSTService/Api/PublicationApiResponseCachePolicy.cs
  - docker-compose.yml
  - .env.example
  - deploy/docker-compose.yml
  - deploy/config/fstservice-role.env
  - deploy/config/fstworker-role.env
  - deploy/.env.example
  - tools/fst-worker-compose-guard.sh
  - FSTService/Scraping/Replay/ReplaySecurity.cs
  - FSTService/Scraping/Capture/CaptureOnlyCommand.cs
  - FSTService/Scraping/Capture/CaptureOnlyComposition.cs
  - FSTService/Scraping/Capture/CapturePaginationMaximums.cs
  - FSTService/Scraping/Capture/CaptureOnlyStorage.cs
  - FSTService/Scraping/LeaderboardPaginationPlanner.cs
  - tools/FstSnapshotGenerationRetirement/
  - tools/postgres-snapshot-generation-retirement.sh
update_triggers:
  - An appsettings section, environment key, secret, role file, or configuration precedence rule changes.
---

# Configuration

## Precedence

FSTService uses normal .NET configuration precedence: tracked
`appsettings.json`, environment-specific files when present, environment
variables, and command-line-derived options. Compose uses double underscores to
map environment variables to nested sections.

Repository defaults are development/safe baselines. Role files and Compose
overrides intentionally diverge between the public service and mutation worker.

## Main sections

| Section | Scope |
|---|---|
| `Scraper` | hosting mode, schedule, concurrency, phases, catalog/path work, proxy pool |
| `Features` | persistence/publication rollout, App Manual, and in-app feedback |
| `ClientTelemetry` | bounded browser interaction diagnostics |
| `Feedback` | in-app bug report/feature request GitHub target, limits, and media conversion ([In-app feedback](../components/in-app-feedback.md)) |
| `ImprovementNotifications` | notification scope, projection refresh, staleness |
| `PublicationCommit` | read drain, locks, retries, leases, deferred recovery |
| `BandRankHistory` | mode, storage/read source, compaction/retention behavior |
| `BandTeamRankings` | ranking writer strategy |
| `DatabaseMaintenance` | retention, pressure guards, cleanup and snapshot rewrite |
| `BackgroundJobs` | background scheduling |
| `Api` | API key and allowed origins |
| `ConnectionStrings` | PostgreSQL |
| `Kestrel` | HTTP listener |

## Worker shutdown

| Key | Default | Accepted range / effect |
|---|---:|---|
| `Scraper:ProxyRequestTimeoutSeconds` | `0` | Per-attempt timeout for proxied curl sends, started after an exit lease is acquired; `0` keeps the executor's 30-second default |
| `Scraper:WorkerShutdownTimeoutSeconds` | `120` | Full-worker host shutdown budget, clamped to 30–600 seconds. It must exceed the scrape pass's bounded 30-second cleanup so a stopped worker records its interrupted phase attempt; Compose `stop_grace_period` must be longer still (template: 180s). |

## Worker-only PIA region rotation

Region rotation is separate from `Scraper:ProxyActiveStandby` and
`Scraper:ProxyActiveRotationSeconds`, which only select among already-running
proxy endpoints. All keys below apply **only** to the mutation worker;
API/frontend and capture-only roles cannot operate VPN regions.

| Key | Default | Accepted range / effect |
|---|---:|---|
| `Scraper:ProxyRegionRotationEnabled` | `false` | Opt-in actual PIA tunnel refreshes (reconnects or region changes) after per-exit HTTP 429s |
| `Scraper:ProxyRegionRotationRegions` | empty | 0–64 distinct, operator-qualified PIA region names; indexed environment entries such as `Scraper__ProxyRegionRotationRegions__0`. Empty is valid only with reconnect-in-place |
| `Scraper:ProxyRegionRotationReconnectInPlace` | `false` | First candidate reconnects the exit's current region so Gluetun selects another random server |
| `Scraper:ProxyRegionRotationRateLimitThreshold` | `3` | 1–100 consecutive 429s for one exit before scheduling a refresh |
| `Scraper:ProxyRegionRotationRequestBudget` | `0` | `0` (off) or 10–1,000,000 successful requests on one egress before a proactive refresh |
| `Scraper:ProxyRegionRotationMinIntervalSeconds` | `900` | 5–86,400 seconds between attempts for the same exit |
| `Scraper:ProxyRegionRotationGlobalIntervalSeconds` | `60` | 0–3,600 seconds between the starts of any two refreshes |
| `Scraper:ProxyRegionRotationMaxConcurrent` | `1` | 1 to the effective exit count; exits refreshing at the same time (never one exit twice) |
| `Scraper:ProxyRegionRotationMaxAttempts` | `2` | 1–16 candidate reconnects/region changes per refresh before restoration |
| `Scraper:ProxyRegionRotationAttemptTimeoutSeconds` | `30` | 5–360 seconds to verify one candidate; a dead PIA server fails its OpenVPN TLS handshake after about 20 seconds, while a healthy reconnect verifies in a few seconds |
| `Scraper:ProxyRegionRotationProbeTimeoutSeconds` | `240` | 10–360 seconds overall for all candidates of one refresh |
| `Scraper:ProxyRegionRotationBurnedEgressTtlSeconds` | `900` | 0–86,400 seconds an egress that returned 429 is rejected as a replacement |
| `Scraper:ProxyRegionRotationDrainSeconds` | `60` | 0–300 seconds to let in-flight leases finish before the tunnel changes; later reports from the old tunnel are ignored |
| `Scraper:ProxyRegionRotationQuarantineRetrySeconds` | `0` | 0–3,600 seconds before a quarantined exit gets a fully verified refresh retry (doubling per consecutive failure, capped at one hour); `0` keeps it quarantined until the worker restarts |
| `Scraper:ProxyRegionRotationTargetEndpoints` | `false` | Each refresh attempt first pins the exit (Gluetun `endpoint_ip`) to the least recently used known server address in a qualified region that no exit holds, is outside the rate-limited window, and is not backing off after a failed pin; random selection is the fallback. Requires rotation with at least one region |
| `Scraper:ProxyRegionRotationTargetMinRestSeconds` | `0` | 0–3,600 seconds since a known server was last used before it can be targeted; `0` relies only on the rate-limited window. Unrested servers leave the attempt to random selection, which also discovers new servers |
| `Scraper:ProxyRegionRotationSeedServerCatalog` | `false` | With targeting, read each exit container's Gluetun PIA server list (read-only Docker archive read of `/gluetun/servers/private internet access.json`) at startup and hourly and add its qualified-region UDP addresses to the target catalog; learned entries are never overwritten; it also limits each exit's targets and region-change candidates to qualified regions present in that exit's own list |

The enabled worker requires a nonzero `ExpectedProxyEndpointCount`, four
complete aligned proxy/control/provider/container arrays with every provider
labeled PIA, `ProxyUseCurlTransport=true`, and a fully qualified
`ProxyCurlTempDirectory` below its `DataDirectory` (on the FST data drive).
Invalid configurations fail startup rather than silently disabling healing.
Configure the production-owned PIA worker overlay, not the optional AirVPN
repository template. The static `SERVER_REGIONS` selectors in production
Compose remain the rollback and boot-recovery baseline. See
[VPN proxy pool](../operations/vpn-proxy-pool.md) for admission and cooldown
semantics and [Deployment](../operations/deployment.md) for release gates.

## Snapshot-generation report-only retention

| Key | Default | Effective bound | Purpose |
|---|---:|---:|---|
| `DatabaseMaintenance:SnapshotGenerationRetentionReportOnlyEnabled` | `false` | Boolean | Enables terminal worker-owned observation only; it cannot create executable work |
| `DatabaseMaintenance:SnapshotGenerationRetentionCommandTimeoutSeconds` | `30` | clamped to `5`-`120` | Npgsql and transaction-local statement/idle timeout for one repeatable-read observation and its short evidence write |
| `DatabaseMaintenance:ServiceMaintenanceLockWaitMilliseconds` | `500` | clamped to `0`-`5000` | Bounded wait used by metadata TTL and generation observation for the shared service-maintenance advisory lock |

The tracked configuration is safe-by-default. There is no option that enables
archive/detach/drop/delete behavior and no option that disables unreplayed
writer-failure protection. The legacy
`DatabaseMaintenance:SnapshotRetentionRewriteEnabled` remains `false` and is
not reused as the generation-child oracle.

Enabling report-only observation is a worker-role change and does not expose a
browser feature flag or public API. The enabled worker uses a code-bounded
128-item keyed FIFO. Planner deferrals remain at its head instead of being
overwritten. Runnable registration work first receives a code-bounded
30-second, non-cancelling adaptive drain window; if still incomplete, the FIFO
is retained and the scheduled scrape may proceed without recording a cycle.
It must be enabled only for a later coordinator-owned observation window. See
[Snapshot generation retention safety](../database/SnapshotGenerationRetentionSafety.md).

## Publication API cache safety bounds

Freeze-safe cache coverage is code-owned rather than a deployment feature flag.
This prevents a role override from widening request-time cache writes or
weakening fail-closed behavior.

| Bound | Value | Purpose |
|---|---:|---|
| Lazy route family | overview only | Excludes arbitrary/high-cardinality routes |
| Lazy page sizes | `25`, `50` | Ten finite metric/size variants |
| Maximum measured build | `< 1000 ms` | Hard admission limit; target is `< 500 ms` |
| Maximum lazy payload | `2 MiB` | Bounds memory, PostgreSQL row size, and response capture |
| Operation telemetry | last `256` | Bounded diagnostics without raw cache keys |
| L2 retention | current + previous publication | Existing publication cleanup contract |

Changing these bounds is an API/publication behavior change requiring matched
benchmarks, freeze tests, documentation, and a separate promotion decision.

## Song catalog refresh

| Key | Default | Purpose |
|---|---:|---|
| `Scraper:SongSyncInterval` | `00:05:00` | Boundary-aligned interval for fetching and persisting the exact Spark Tracks catalog |

The public service role owns this refresh. A successful exact change updates
`live_song_catalog` and live song metadata, invalidates process-local song
state, emits aggregate `songs_changed` telemetry, and retries later when the
publication lock is busy. It does not generate paths and, with publication
path artifacts enabled, does not mutate the canonical published
`/api/songs` row, maxima, rankings, or publication pointer. Those surfaces
remain tied to the catalog captured when the worker allocated its publication.

The repository Compose files contain only a commented 15-minute example.
Production keeps the code default unless the production-owned Compose project
explicitly overrides `Scraper__SongSyncInterval`.

## Item Shop reconciliation

| Key | Default | Purpose |
|---|---:|---|
| `Scraper:EnableItemShopRefresh` | `true` | Makes the process responsible for provider polling, reconciliation, service notifications, and shop timers |
| `Scraper:ItemShopRefreshInterval` | `00:15:00` | Daytime reconciliation cadence; must be positive when refresh ownership is enabled |

The Compose forms are `Scraper__EnableItemShopRefresh` and
`Scraper__ItemShopRefreshInterval`. Production role files explicitly enable
refresh on `fstservice` at 15 minutes and disable it on `fstworker`. A disabled
role still loads and serves the persisted `item_shop_tracks` projection but
performs no provider request, notification reconciliation, cleanup, or timer
registration. This is a backend role setting with no browser feature-flag
surface.

## Path generation

| Key | Default | Purpose |
|---|---|---|
| `Scraper:CHOptPath` | `tools/CHOpt` | Bundled CHOpt launcher or binary |
| `Scraper:EnablePathGeneration` | `true` | Allows explicit path generation |
| `Scraper:EnableAutomaticPathGeneration` | `false` | Legacy API-owned pending-song promotion. Rejected at startup until publication-safe scrape-pass staging replaces it |
| `Scraper:UsePublicationPathArtifacts` | `false` | Backend-only source flag. Serves effective published path state and CHOpt maxima from the publication-bound `publication_path_artifacts` snapshot instead of live `songs` rows |
| `Scraper:EnableScrapePassPathGeneration` | `false` | Worker-only publication-safe scrape-pass staging. Stages pending-song generations into the working publication snapshot; live rows change only at publication commit |
| `Scraper:ScrapePassPathGenerationMaxSongs` | `25` | Maximum pending songs staged per scrape pass (1–500) |
| `Scraper:ScrapePassPathGenerationTimeout` | `00:20:00` | Whole-batch staging budget (1 minute–6 hours) |
| `Scraper:PathGenerationParallelism` | `4` | Maximum concurrent CHOpt processes |
| `Scraper:PathGenerationProfile` | `chopt-fnf-ew0-s20-json-png-prodrums-v4` | Semantic identity for the dedicated plastic-drums MIDI variant, authored activation-window contract, eight-instrument scope, and artifact schema |

The MIDI decryption key is operator-supplied and must not appear in logs,
documentation, artifacts, or commands. Profile changes invalidate selected
older generations but do not select the full catalogue; use the guarded
sequential procedure in [Path generation](../components/path-generation.md).

The scrape-pass staging options have no browser exposure and are owned by the
`fstworker` role only; the `fstservice` role ignores them apart from the admin
regeneration gate. `Scraper:EnableScrapePassPathGeneration` requires both
`Scraper:EnablePathGeneration` and `Scraper:UsePublicationPathArtifacts`,
because staged generations are only readable through the publication-bound
snapshot. Out-of-range max-song or timeout values are rejected at startup, and
`Scraper:EnableAutomaticPathGeneration=true` is still rejected at startup.

Staging failures never abort a scrape pass, and songs deferred for review or
retry are excluded from automatic selection until an explicit successful
promotion, a provider catalog identity change, or
`POST /api/admin/path-generation/rearm` re-arms them.

Automatic staging runs only when the resolved phase set is `ScrapePhase.All`.
Phase-selective runs leave pending songs untouched because staged maxima may be
published only with rebuilt rankings, statistics, and the canonical
`public-api:songs:v1` payload.

When automatic staging is enabled, `Scraper:MidiEncryptionKey` is a startup
prerequisite and must be a 32- or 64-character hexadecimal AES key. The worker
fails option validation before readiness when the key is missing or invalid;
tracked role files never contain the secret.

A role that never runs schema DDL - `Scraper:ApiOnly=true`,
`Scraper:SkipStartupSchemaInitialization=true`, or
`Scraper:RolloutReadOnlyStartup=true` - and also sets
`Scraper:UsePublicationPathArtifacts=true` verifies the current publication's
path artifact release at startup. The rollout read-only mode performs this
check before its early return. Start the API/schema-initializing role first,
then no-DDL roles; see
[Deployment topology](../operations/deployment.md).

Enabling `Scraper:UsePublicationPathArtifacts` also disables immediate admin
path regeneration on the service role: `POST /api/admin/regenerate-paths`
returns `409 Conflict` because live promotion is no longer a supported path
state change in publication-bound mode.

`Scraper:UsePublicationPathArtifacts` has no browser exposure. It is owned by
both the `fstservice` (read) and `fstworker` (capture/maintenance) roles and
takes effect only after a restart. Mutation, generation, and maintenance code
paths always read live rows regardless of its value. Published API reads and
scrape-derived computation read the exact current or working publication
snapshot. Rollback is setting
`Scraper__UsePublicationPathArtifacts=false` and restarting. See
[Publication path artifact snapshots](../database/PublicationPathArtifactSnapshots.md).

The option classes are authoritative when a property exists but is omitted from
`appsettings.json`.

## Max-score maintenance

| Key | Default | Valid range | Purpose |
|---|---:|---:|---|
| `Scraper:MaxScoreMaintenanceCommandTimeoutSeconds` | `600` | `1`-`86400` | Npgsql command and transaction-local PostgreSQL statement timeout for live-scale max-score plan/apply/resume/rollback evidence and revalidation |

The production Compose-form override is
`Scraper__MaxScoreMaintenanceCommandTimeoutSeconds=1800`. The value applies
uniformly to publication population, complete consumed score-history,
notification, affected-account, cache, final validation, and apply/resume/rollback
revalidation commands. It does not change ordinary scrape or cleanup command
timeouts. During final completion, the transaction-local PostgreSQL
`statement_timeout` uses this value only for the immutable cache-entry
validation while `lock_timeout` remains `5s`; it is restored to `120s` before
the cache swap, completed checkpoint, verification, and unfreeze. A validation
or timeout-transition failure rolls back the transaction and leaves the freeze
and durable mutation gate intact. Cancellation still aborts fail-closed, and
invalid/non-positive values prevent startup.

## Band current-projection candidate

| Key | Default | Purpose |
|---|---:|---|
| `Scraper:BandCurrentProjectionUseBatchedMemberStatsAggregation` | `false` | Use one lateral `band_member_stats` aggregate per projected row instead of seven correlated aggregates |
| `Scraper:BandCurrentProjectionMaxParallelScopes` | `0` | Concurrent scope transactions across all band types; `0` keeps one sequential worker per band type with at most two band types at once; values above `16` are clamped |
| `Scraper:BandCurrentProjectionBatchScopesBySourcePair` | `false` | With `MaxParallelScopes` above `0`, rebuild all selected scopes of one song and band type in one transaction that reads the song's band entries and member stats once |
| `Scraper:BandCurrentProjectionPublishParallelism` | `0` | When positive, publish an incremental refresh one song per transaction with up to this many at once and clean only unsettled scopes; `0` keeps one publish transaction and a whole-projection candidate scan; values above `16` are clamped |
| `Scraper:BandCurrentProjectionStaleScopeSweepMaxScopes` | `0` | When positive, also rebuild up to this many stale scopes outside the scrape's impacted set |
| `Scraper:BandCurrentProjectionSinglePassStaleSweep` | `false` | Derive the stale sweep's candidates and its unchanged-scope selection from one scan of the band entries instead of two; same selection |
| `Scraper:BandSearchProjectionParallelBandTypes` | `false` | Refresh the band search projection one band type per concurrent transaction |
| `Scraper:BandSpoolFlushMaxParallelBandTypes` | `1` | How many band types the post-fetch band spool flush writes at once; clamped to the number of band types |
| `Scraper:BandRetentionFloorMode` | `Off` | Band retention floor for the post-fetch band flush. `Report` records staged new rows that rank below the floor band prune last recorded for their scope, and the next prune counts how many of them it would keep; `Enforce` also skips those rows; `Off` does neither. See [worker: band retention floor](../components/worker.md#band-retention-floor) |
| `Scraper:OverThresholdMultiplier` | `1.05` | Solo deep-scrape trigger (`CHOptMax × multiplier`) and the band over-threshold flag: a band entry is over threshold when any member's score exceeds that member's instrument CHOpt max times this multiplier. The band page fetch and band extraction both apply it |
| `Scraper:BandRetentionFloorMarginRows` | `100` | Rows between band prune's last kept window row and the recorded retention floor; absorbs over-threshold flips at the top of a leaderboard between the flush and prune |

The Compose form is
`Scraper__BandCurrentProjectionUseBatchedMemberStatsAggregation`. The switch
changes only the current-projection query shape; scope filtering, concurrency,
transactions, generation/state writes, publication, cleanup, and ordering
remain unchanged. It is intentionally absent from production role overrides
and therefore remains off. Set it back to `false` for immediate code-path
rollback. Enabling it in production requires a capacity-safe matched full
scrape A/B and exact publication/data parity; isolated replay timing is not
promotion evidence.

`Scraper__BandCurrentProjectionMaxParallelScopes` (template variable
`BAND_CURRENT_PROJECTION_MAX_PARALLEL_SCOPES`) changes only how many selected
scopes rebuild concurrently. Each scope keeps its own transaction and writes
disjoint projection and scope-state keys; filtering, query shape, publication,
cleanup, and failure accounting are unchanged. Both switches are part of the
durable phase configuration identity. Set it back to `0` for rollback.

`Scraper__BandCurrentProjectionBatchScopesBySourcePair` (template variable
`BAND_CURRENT_PROJECTION_BATCH_SCOPES_BY_SOURCE_PAIR`) applies only when
`MaxParallelScopes` is positive. The parallel slots then run one transaction
per (song, band type) instead of per scope. Each transaction reads the
song's non-over-threshold band entries, combo ids, and member-stat arrays
into a temporary table once, then rebuilds each selected scope from it. A
failure marks every scope of that pair failed. Projection rows, scope state,
and publication match the per-scope path. The switch is part of the durable
phase configuration identity; `false` is the rollback.

`Scraper__BandCurrentProjectionPublishParallelism` (template variable
`BAND_CURRENT_PROJECTION_PUBLISH_PARALLELISM`) changes only the incremental
refresh's publish and candidate cleanup: each song's scopes flip and lose
their older generations in their own transaction, and cleanup probes only
unsettled scopes. Rebuilds, filtering, and failure accounting are unchanged,
and the end state matches the single-transaction publish. It is part of the
durable phase configuration identity; `0` is the rollback.

`Scraper__BandCurrentProjectionStaleScopeSweepMaxScopes` (template variable
`BAND_CURRENT_PROJECTION_STALE_SCOPE_SWEEP_MAX_SCOPES`) adds a best-effort
sweep to BandMaintenance's current-projection subphase. It loads every source
scope and every existing projection scope key, runs the same unchanged-scope
filter once over those and the impacted scopes, and refreshes every impacted
scope that needs it plus up to the cap of the others (in the filter's
deterministic order) without filtering again. A sweep failure is logged and
the refresh continues with the impacted scopes. The switch is part of the durable
phase configuration identity; `0` disables it.

`Scraper__BandCurrentProjectionSinglePassStaleSweep` (template variable
`BAND_CURRENT_PROJECTION_SINGLE_PASS_STALE_SWEEP`) applies only when the sweep
is enabled. It reads the band entries once: the per-scope projected row
counts and latest source updates go into a temporary table, and both the
candidate keys and the unchanged-scope selection come from it. The selected
scopes and candidate count match the two-pass sweep. It is part of the durable
phase configuration identity; `false` is the rollback.

`Scraper__BandSearchProjectionParallelBandTypes` (template variable
`BAND_SEARCH_PROJECTION_PARALLEL_BAND_TYPES`) makes BandMaintenance's
`search_projection_refresh` subphase refresh each band type in its own
concurrent transaction under the existing rebuild lock. All band types use the
same incremental cutoff, and the next cutoff (`refreshed_at`) advances only
after every band type commits, so a failed band type is refreshed again in
full next time. Readers may briefly see one band type refreshed before
another. It is part of the durable phase configuration identity; `false` is
the rollback.

## Registered-band remaining-work grace

| Key | Default | Valid range | Purpose |
|---|---:|---:|---|
| `Scraper:EnableRegisteredPlayerBandDiscoveryRemainingWorkGrace` | `false` | Boolean | Enables one remaining-work-gated extension for discovery |
| `Scraper:EnableRegisteredBandTargetedProcessingRemainingWorkGrace` | `false` | Boolean | Enables one remaining-work-gated extension for targeted processing |
| `Scraper:RegisteredBandRemainingWorkGraceMaxDuration` | `00:02:00` | `00:00:01`-`00:02:00` | Immutable maximum extension beyond the base timeout |
| `Scraper:RegisteredBandRemainingWorkGraceRecentProgressWindow` | `00:01:30` | `00:00:01` through max duration | Maximum durable-checkpoint age and grace-idle interval |
| `Scraper:RegisteredBandRemainingWorkGraceMaxRemainingLookups` | `3` | `1`-`3` | Maximum exact `planned - durable completed` work at the base deadline |

Compose maps these to
`ENABLE_REGISTERED_PLAYER_BAND_DISCOVERY_REMAINING_WORK_GRACE`,
`ENABLE_REGISTERED_BAND_TARGETED_PROCESSING_REMAINING_WORK_GRACE`,
`REGISTERED_BAND_REMAINING_WORK_GRACE_MAX_DURATION`,
`REGISTERED_BAND_REMAINING_WORK_GRACE_RECENT_PROGRESS_WINDOW`, and
`REGISTERED_BAND_REMAINING_WORK_GRACE_MAX_REMAINING_LOOKUPS`.
All tracked enable defaults remain false. Enabling either phase requires its
resolved discovery/targeted base timeout to be positive; zero retains the
legacy unlimited wait only while grace is disabled. Invalid thresholds fail
startup. All five values participate in durable phase `config_id`.

With the bounded defaults, enabled maximum network/await budgets are eight
minutes for discovery (`6m + 2m`) and seven minutes for targeted processing
(`5m + 2m`). Production enablement requires a separate matched full-scrape A/B;
configuration rollback is independently setting each enable flag to `false`.

## Player rivals

| Key | Default | Valid range | Purpose |
|---|---:|---:|---|
| `Scraper:RivalsMaxDegreeOfParallelism` | `2` | positive integer | Maximum registered accounts whose song-neighborhood rival scans may run concurrently |
| `Scraper:PrepareSoloCurrentProjectionBeforeRivals` | `false` | boolean | With legacy worker readers, refresh stale solo current-projection scopes before rivals and player stats |
| `Scraper:UseValidatedSoloProjectionForLegacyDerivedReaders` | `false` | boolean | After that early refresh leaves no stale or orphaned scope, legacy rivals, leaderboard-rivals, and player-stats readers match ready projection scopes against the active snapshot during the freeze |
| `Scraper:UseValidatedSoloProjectionForLegacyPrecompute` | `false` | boolean | After publication cleanup's projection refresh leaves no stale or orphaned scope, legacy precompute readers match ready projection scopes against the active snapshot until precompute ends |
| `Scraper:SoloCurrentProjectionApplyDiff` | `false` | boolean | A solo current-projection scope refresh writes only the rows that differ from the stored projection, keeping the scope's generation, instead of deleting and re-inserting every row |

The Compose form is `Scraper__RivalsMaxDegreeOfParallelism`. Scheduled
post-scrape rivals first load all target users' current scores once per
instrument, sequentially across instruments, then reuse those immutable score
lists for combo counting, neighborhood scans, and selection-state persistence.
The account limit applies only after that shared preload. Direct single-user
and backfill calls retain their existing on-demand read path.

Lower the value to reduce PostgreSQL memory, temp-file, and parallel-query
pressure. Raising it requires a matched full-scrape capacity test because each
account can execute many neighborhood reads and fingerprint queries. The
setting changes scheduling only; rival eligibility, methods, directions,
samples, persistence, publication criticality, and result ordering are
unchanged.

Rival song counts and neighborhoods read the solo current projection only when
a scope is ready for its song's active source, and song counts use it only
when every scope of the instrument is ready. Otherwise each neighborhood
re-ranks live and snapshot rows for the song. Because the projection is
normally refreshed later in `Cleanup.SoloCurrentProjection`, songs that
received a new snapshot in the current scrape take that fallback (scrape
`1436`: 72-517 of 729 scopes ready per instrument; Rivals `4.6`-`38.5`
minutes across scrapes `1416`-`1424`). With
`Scraper__PrepareSoloCurrentProjectionBeforeRivals=true` and legacy worker
readers, the existing `PrepareSoloCurrentProjectionForDerived` phase refreshes
stale scopes first. It is best-effort and records nothing on the pass
context: publication cleanup still reloads stale scopes (including scopes
re-dirtied by later snapshot activation) and remains the publication-critical
refresh. Public reads are frozen for all of post-processing, so the earlier
refresh exposes nothing. Snapshot/overlay worker readers always prepare and
validate the projection instead. The switch is part of the durable phase
configuration identity; set it back to `false` for rollback.

The early refresh alone does not reach the projection during the public-read
freeze: legacy readers match each scope's source against the published scrape,
so every song whose active snapshot is newer fails readiness. The all-scope
check behind the shared rivals preload then always falls back to whole-instrument
live-plus-snapshot ranking (about 33 seconds per instrument in scrapes
`1453`-`1456`; 338 of 731 Solo Guitar scopes mismatched while every scope
matched its active snapshot). With
`Scraper__UseValidatedSoloProjectionForLegacyDerivedReaders=true` as well,
the phase reloads stale scopes and orphans after its refresh; when none remain,
legacy readers match ready scopes against the active snapshot, which is what
the fallback reads, until just before snapshot activation, and the
read pass always clears it. Readiness still requires an exact source match per
scope, so any scope that changes later falls back as before. Read-only
production parity on 25 rivals accounts found identical rows (all columns)
from both paths: Solo Guitar `8,055`, Solo Bass `4,764`, Pro Drums `141`.
It is part of the durable phase configuration identity; set it to `false` for
rollback.

`Scraper__SoloCurrentProjectionApplyDiff` changes how every solo
current-projection scope refresh writes (early refresh, cleanup, and
notification recovery). See
[worker: solo current projection writes](../components/worker.md#solo-current-projection-writes).
It is part of the durable phase configuration identity; `false` is the
rollback.

## Leaderboard rivals

| Key | Default | Valid range | Purpose |
|---|---:|---:|---|
| `Scraper:LeaderboardRivalsMaxDegreeOfParallelism` | `4` | positive integer | Registered accounts included in each per-instrument ranking/profile batch |

The Compose form is
`Scraper__LeaderboardRivalsMaxDegreeOfParallelism`. Despite the retained key
name, scheduled processing no longer fans out that many accounts concurrently.
It processes one instrument at a time and chunks registered accounts by this
value. Each chunk loads rankings and its deduplicated user/neighbor profiles
once, then persists every user/instrument independently.

Lower values reduce peak profile memory but repeat the full-instrument profile
query more often. Higher values reduce query count while retaining more
profiles and score DTOs in memory. The setting does not change rank methods,
neighbor radius, sample caps, persistence shape, or publication behavior.
Direct single-user calls and max-score maintenance keep their separate
on-demand and maintenance-lease paths.

## Rankings concurrency

| Key | Default | Purpose |
|---|---:|---|
| `BandTeamRankings:WriteMode` | `ComboBatched` | Insert overall rows, then each combo's rows in its own statement (`Monolithic` inserts all rows in one statement) |
| `BandTeamRankings:MaxParallelBandTypes` | `1` | Band types whose team rankings rebuild at once |
| `BandTeamRankings:OverlapRankHistorySnapshotsWithBandRankings` | `false` | Run rank-history snapshots concurrently with band team rankings; the rankings pass still waits for both |
| `Scraper:RankHistorySnapshotMaxDegreeOfParallelism` | `1` | Concurrent rank-history snapshot writers (one per solo instrument plus composite) |
| `Scraper:UseRankHistoryLatestState` | `false` | Compare rank-history snapshots against the maintained latest row per account instead of scanning each instrument's and the composite's whole history; see below |

Per-instrument solo rankings always run at most two instruments at once to
bound PostgreSQL memory. The production worker env sets
`BandTeamRankings__MaxParallelBandTypes=2` and
`BandTeamRankings__OverlapRankHistorySnapshotsWithBandRankings=true`. With the
band rank-history schema ensured once per instance, scrape `1459` rebuilt the
three band types in 16.0 minutes (39.0 in `1458`, when the schema lock
serialized them), which left the sequential rank-history snapshots (about 35
minutes) as the longest branch of ComputeRankings. Peak PostgreSQL anonymous
memory during that overlap was about 2.9 GiB on top of 4.2 GiB of shared
buffers in the 16 GiB container. Raising
`Scraper__RankHistorySnapshotMaxDegreeOfParallelism` (template variable
`RANK_HISTORY_SNAPSHOT_MAX_DOP`) runs that many snapshot writers at once and
adds WAL and data-file pressure; it is part of the durable phase configuration
identity, and `1` is the rollback. With `2` in scrape `1460` the snapshots took
23.5 minutes (34.3 in `1459`), but the concurrent band team rankings slowed
from 16.0 to 33.1 minutes, so ComputeRankings improved only from 51.7 to 49.6
minutes; peak anonymous memory rose to about 3.9 GiB. In scrape `1461` the
same setting left Band_Quad's combo inserts at about 8 seconds per combo
(450 combos, 66 minutes) after the snapshots had evicted cached pages, and
ComputeRankings took 103 minutes. With the combo index described below
(scrape `1462`, still `2`), band team rankings took 12.5 minutes (Band_Quad's
inserts 2.5 minutes), the snapshots 25.3, and ComputeRankings 41.3 minutes, so
production keeps `2`.

The band rank-history schema is ensured once per worker process. Until
2026-10-08, two band types that started together both ran that DDL, and the
second one's `CREATE INDEX IF NOT EXISTS` waited for the first band type's whole
rebuild transaction. In the first scrape after each worker start, Duets waited
for Trios (scrapes `1495` and `1497`: Duets 562 and 540 s, against 236 s in
`1496`). The ensure step is now serialized in-process, so the second caller
skips it.

Each snapshot writes a new history row only for accounts whose ranks or
metrics differ from their latest history row. Finding that latest row scanned
the whole history every scrape: about 104 seconds per instrument partition (14
to 38 GB each) and about 5.5 minutes for `composite_rank_history` (92 GB),
together about 260 GB of reads per scrape. `Scraper:UseRankHistoryLatestState`
instead reads the latest row per account from `rank_history_latest` and
`composite_rank_history_latest`. Each snapshot updates those tables in the same
transaction as its history insert.

The first enabled snapshot of each scope (instrument or `composite`) rebuilds
its latest rows from history with the original scan, so it costs one old-style
snapshot plus the insert. It then marks the scope ready in
`rank_history_latest_state`. A snapshot taken with the option off drops that
scope's readiness, and the next enabled snapshot rebuilds it. Retention cleanup
never deletes an account's newest history row, so it does not affect the
latest rows. The tables are created on first use, so no deploy hook is needed.
`false` is the rollback.

`ComboBatched` was adopted when disk headroom was tight (a single monolithic
insert once failed with `No space left on device`). Its per-combo statements
filter the materialized results temp table by `combo_id`; that table now gets
a partial `(combo_id)` index for combo rows and is analyzed before the combo
loop, so each statement reads only its combo instead of scanning every result
row (millions for Band_Quad).

## Role differences

`deploy/config/fstservice-role.env` enables published-source reads while
disabling published-source writes, stored-rank rollout, unchanged-snapshot
reuse, legacy automatic path generation, and publication read context. It sets
`Scraper__UsePublicationPathArtifacts=true`, so the service serves path state
and CHOpt maxima from the publication snapshot and rejects immediate admin path
regeneration. It intentionally does not set
`Scraper__EnableScrapePassPathGeneration`: staging is worker-only.
The service role also sets `Scraper__EnableItemShopRefresh=true` and
`Scraper__ItemShopRefreshInterval=00:15:00`, making it the sole supported
provider-refresh owner.

`deploy/config/fstworker-role.env` sets
`Scraper__UsePublicationPathArtifacts=true` and
`Scraper__EnableScrapePassPathGeneration=true` with the bounded
`ScrapePassPathGenerationMaxSongs=25`,
`ScrapePassPathGenerationTimeout=00:20:00`. Validated MIDI-driven maximum
changes apply automatically through candidate publication, without an approval
setting. The retired `ScrapePassPathGenerationAllowChangedMaxima` key is
ignored if it remains in older deployment configuration. Legacy
`Scraper__EnableAutomaticPathGeneration` stays `false` on both roles and is
rejected at startup, so the supported production configuration replaces the
legacy generator rather than leaving the catalog without one. The shipped
option defaults remain `false` for generic safety; enabling them is a role
configuration decision, and either flag can be reverted independently on
restart.

`deploy/config/fstworker-role.env` also skips startup schema initialization, enables
the three publication correctness gates, writes published scope sources, keeps
public-read ownership off the worker, enables scope fingerprints and
unchanged-snapshot reuse after accepted scrape 1303, leaves publication read
context disabled, sets `Scraper__EnableItemShopRefresh=false`, and sets
`WriteLegacyLiveLeaderboardDuringScrape=false`.
The worker therefore loads persisted Item Shop state without provider HTTP or
shop timers. With the legacy-write value, the post-scrape legacy stored-rank phase completes its
publication-critical contract without performing a rank update. It is never
persisted as skipped. Setting the rollback flag to `true` restores the existing
legacy recompute implementation and its publication-critical failure behavior.

Do not copy one role file onto the other.

## Secrets and operator values

Never commit real values. Depending on the selected template and enabled
features, operator-supplied values include:

- PostgreSQL password;
- API key;
- Epic client ID/secret;
- MIDI/path-generation key;
- in-app feedback GitHub token (`Feedback__GitHubToken`);
- VPN provider credentials, keys, addresses, and server selection;
- optional e-mail/reporting credentials.

The production Compose project is
`/home/sfenton/Docker/FestivalServiceTracker`; repository Compose files are
templates. Document key names and behavior, never resolved values, private
endpoints, or provider account data.

## Isolated replay environment

Replay mode does not load `.env` or normal appsettings. Its process receives a
small explicit environment:

| Variable | Requirement |
|---|---|
| `FST_REPLAY_APPROVED_ROOT` | Existing canonical child of the production FST evidence/replay roots |
| `FST_REPLAY_APPROVED_DEVICE` | Exact filesystem device identity (`major:minor` on Linux) for the 4 TB FST drive |
| `FST_REPLAY_ROLLBACK_RESERVE_BYTES` | Non-negative disk reserve; defaults to 1 GiB |
| `FST_REPLAY_POSTGRES_CONNECTION` | Secret isolated PostgreSQL connection; single loopback host and `fst_replay_*` database |
| `FST_REPLAY_GIT_COMMIT` | Exact implementation commit |
| `FST_REPLAY_IMAGE_DIGEST` | Exact OCI SHA-256 digest |
| `FST_REPLAY_IMAGE_REVISION` | Exact OCI revision |

The replay connection is never serialized or printed. Normal production
`ConnectionStrings__PostgreSQL`, when present, is used only as a rejection
reference; matching its host/port is forbidden regardless of database name.
The sealed Tier-1 input also carries the source PostgreSQL system identifier,
which the isolated target must not match.

Tests inject their root/target policy directly; there is no environment flag
that weakens production root, device, marker, cluster, or publication refusal.

## Manual capture-only environment

Capture-only mode loads the normal `.env`, appsettings, environment-specific
appsettings, and process environment solely to obtain Epic authentication,
enabled leaderboard types, pacing, and proxy-routing behavior. It never reads
or creates an Npgsql data source and does not require a PostgreSQL connection.
Resolved values, credentials, tokens, configured addresses, and authenticated
account configuration must not be copied into logs, packages, exception
artifacts, or documentation. Captured leaderboard participant account IDs are
data payload and require the same access and retention controls as leaderboard
history.

Every production invocation requires these explicit values:

| Variable | Requirement |
|---|---|
| `FST_CAPTURE_APPROVED_ROOT` | Existing capture root under the canonical 4 TB FST `fst-data/capture` or `fst-data/evidence/capture` tree |
| `FST_CAPTURE_APPROVED_DEVICE` | Exact filesystem device identity (`major:minor` on Linux) for that root |
| `FST_CAPTURE_MAX_PACKAGE_BYTES` | Maximum final sealed-package size; must exceed the bounded 24 MiB pre-metadata allowance and is used as the conservative pre-provider admission size |
| `FST_CAPTURE_MIN_FREE_SPACE_RESERVE_BYTES` | Non-negative free-space reserve that must remain after admission and sealing |
| `FST_CAPTURE_MAX_RETAINED_SEALED_PACKAGES` | Positive count ceiling; reaching it refuses capture and never deletes an older package |
| `FST_CAPTURE_GIT_COMMIT` | Exact 40- or 64-character implementation commit |
| `FST_CAPTURE_IMAGE_DIGEST` | Exact OCI SHA-256 image digest |
| `FST_CAPTURE_IMAGE_REVISION` | Exact 40- or 64-character OCI revision |
| `FST_CAPTURE_PAGINATION_MAX_SCORES_PATH` | Optional canonical `fst.capture-pagination-max-scores.v1` regular file beneath the approved root and on its filesystem device; required whenever a non-exhausted parallel solo scope needs CHOpt-aware pagination |

The approved root and every ancestor are rejected if they contain Tier-0
package marker files; a sealed package can never be reused as a capture
container.

`FST_CAPTURE_RESPONSE_SHARD_BYTES` is optional and defaults to 64 MiB. It must
be greater than the 8 MiB response-record ceiling and no larger than the
64 MiB contract ceiling. The default geometry provides 128 GiB of response
capacity, above the measured approximately 92.8 GB workload. Before provider
traffic, checked arithmetic verifies that the configured shard size multiplied
by the fixed 2,048-shard ceiling can contain the admitted package response
budget. The command rechecks current and projected final package bytes
independently from future metadata/workspace bytes. A no-follow approved-root
lock is acquired for preflight and held through the final free-space and
retained-count decision and sealing, including between different output
directories. It never performs retention deletion.

The normal `Scraper` keys that select full-scrape solo instruments,
`Scraper:EnableBandScraping`, `Scraper:MaxPagesPerLeaderboard`, concurrency,
the global request rate, and the existing aligned
proxy/pacing/cooldown/retry/self-heal settings are reused. Parallel solo mode
also reuses the active valid-entry/deep-scrape thresholds and batch size;
sequential solo and the active band fetcher stop at the initial common page
cap. The legacy direct band phase's separate page/valid-entry settings are not
used by capture-only mode.
Pages may complete concurrently, but package records are restored to canonical
scope/page order before sealing. A scope that could be truncated fails closed
unless catalog acquisition supplies the exact non-database maximum-score
snapshot needed by the ordinary pagination decision. The configured curl scratch directory is required for every live capture,
including .NET-HTTP fallback when curl is not the primary proxy transport. It
must be the exact absolute
`<FST_CAPTURE_APPROVED_ROOT>/.capture-curl-scratch` path, outside every output
package and any existing Tier-0 package ancestor. Capture revalidates that path
before every curl write and caps every curl response during transfer at the
capture response-record byte limit. Curl ignores ambient configuration, and
proxy concurrency ownership remains active until each response body is
consumed or disposed. Initial storage admission reserves one
maximum response per configured capture page-concurrency slot in addition to
the maximum package and sealing workspace.

The maximum-score file is strict canonical JSON bound to the exact provider
catalog SHA-256. It contains one ordinal song record per catalog song and one
entry, in canonical solo-instrument order, for every supported maximum; a
missing maximum omits the `maximumScore` member. Capture reads it as a regular
no-follow file on the approved device, records its SHA-256 in package lineage,
and revalidates it with the final catalog fetch. It is pagination input only
and grants no database or path-generation authority.

```json
{"formatId":"fst.capture-pagination-max-scores.v1","providerContentSha256":"<sha256>","songs":[{"maximums":[{"leaderboardType":"Solo_Guitar","maximumScore":123456}],"songId":"song-id"}],"version":1}
```

The abbreviated example shows one maximum; an admitted file must contain every
catalog song and every capture-contract solo instrument in canonical order.

## Snapshot-retirement plan environment

The separate host-run retirement plan tool does not load service appsettings or
Compose role files. It accepts only:

| Variable | Requirement |
|---|---|
| `FST_SNAPSHOT_RETIREMENT_CONNECTION_STRING` | Operator-supplied PostgreSQL connection string; treated as a secret and never emitted |
| `FST_SNAPSHOT_RETIREMENT_BINARY_SHA256` | Lowercase SHA-256 of the published self-contained single-file Release supervisor executable; the wrapper and process both verify it |

These variables do not enable worker behavior. A matching explicit immutable
policy epoch is still required before `plan-cycle` can write a plan. See
[Snapshot generation retirement plan control plane](../database/SnapshotGenerationRetirementControlPlane.md).

## Environment naming

Use the .NET key in prose (`Features:AppManual`) and the Compose form in
examples (`Features__AppManual`). Shell-friendly aliases such as
`FEATURE_APP_MANUAL` are template inputs, not service option names.

## Worker guard environment

These variables configure host-side
`tools/fst-worker-compose-guard.sh` mutation/recovery behavior. They are not
FSTService options and do not belong in container environment arrays.

Run-once guard actions require a named data profile. `scrape-resume` is the
only profile that permits `Scraper:EnabledPhases=SoloRankings`; it also
requires a positive `Scraper:ResumeScrapeId`, explicit full-worker hosting
(`Scraper:ApiOnly=false`, `Scraper:DisableScraperWorker=false`,
`Scraper:RegistrationSyncWorkerOnly=false`), `Scraper:RunOnce=true`, all nine
canonical `Scraper:Query*` solo flags enabled, the
publication correctness and snapshot-reuse gates, and
`Scraper:RivalsMaxDegreeOfParallelism=2`. This profile is for an existing
resume-eligible candidate only and does not authorize a new network scrape.
The worker ignores legacy `Scraper:ResumeSongsScraped`,
`Scraper:ResumeTotalEntries`, `Scraper:ResumeTotalRequests`,
`Scraper:ResumeTotalBytes`, and
`Scraper:ResumeEpicReportedOver100Pages` values and instead loads the exact
metrics from the candidate's PostgreSQL acquisition checkpoint. The legacy
keys remain bindable so older worker images and environment files can coexist
during a rolling deployment. Worker database validation also requires the
checkpoint's versioned solo-scope count/fingerprint to match the requested
scrape's complete all-time manifests for all nine canonical solo instruments;
band manifests are not considered, and both the guard and the in-worker resume
admission reject any reduced canonical `Scraper:Query*` solo scope before
post-processing begins.
Terminal completion does not create a missing checkpoint for legacy rows.
With runtime probes enabled, the guard also requires a stopped worker, an
`updating` or `stalled` service state for the exact configured resume scrape,
frozen public reads with reason `post-process`, and a different published
scrape ID before it may recreate the worker. The profile is rejected for
ordinary `--check`/`--recreate`; only `--check-runonce` and
`--recreate-runonce` may use it.

| Variable | Default | Purpose |
|---|---:|---|
| `FST_WORKER_COMPOSE_GUARD_LOCK_PATH` | `<resolved-compose-dir>/.fst-worker-compose-guard.lock` | Optional explicit shared absolute lock for `--recreate`, `--recreate-runonce`, and `--recover-start` |
| `FST_WORKER_RECOVERY_CORE_WAIT_SECONDS` | `60` | Bounded PostgreSQL/API readiness window |
| `FST_WORKER_RECOVERY_INITIAL_WAIT_SECONDS` | `360` | Initial effective-proxy convergence window |
| `FST_WORKER_RECOVERY_RECREATE_WAIT_SECONDS` | `360` | Post-recreate effective-proxy convergence window |
| `FST_WORKER_RECOVERY_WORKER_WAIT_SECONDS` | `180` | Worker health/new-heartbeat convergence window |
| `FST_WORKER_RECOVERY_TOTAL_DEADLINE_SECONDS` | `1800` | Positive overall deadline across core, proxies, runtime probes, and worker readiness |
| `FST_WORKER_RECOVERY_POLL_INTERVAL_SECONDS` | `5` | Positive health polling interval |
| `FST_WORKER_RECOVERY_MAX_PROXY_RECREATES` | `3` | Maximum effective services recreated in one invocation |
| `FST_WORKER_RECOVERY_HEARTBEAT_FRESH_SECONDS` | `30` | Maximum accepted age for the new worker heartbeat |
| `FST_WORKER_RECOVERY_WORKER_STOP_TIMEOUT_SECONDS` | `30` | Fail-closed worker stop grace after startup failure |

All numeric values are validated before Compose inspection or mutation. The
360-second proxy windows accommodate the observed startup class, while the
1,800-second overall deadline prevents their probe/retry composition from
becoming an open-ended boot.

The production owner may set these in the boot-unit environment; repository
Compose templates do not own them. Without an override, the lock is derived
after `COMPOSE_DIR` is resolved. Every production invoker must therefore use
the same resolved Compose directory and Unix owner. An explicit override must
be one shared absolute path with the same owner. Size `TimeoutStartSec` above
the total deadline plus signal-cleanup margin.

## Worker Compose startup contract

Repository templates and production-synchronized merged configurations use:

- `profiles: ["worker"]` so generic Compose startup excludes `fstworker`;
- `restart: on-failure:5` for continuous worker configurations, providing only
  bounded nonzero process-exit retries while Docker remains running;
- `restart: no` for run-once overlays.

The guard explicitly passes `--profile worker` for every merged config
resolution that needs `fstworker` and every worker-targeted start. Proxy-only
recreates do not enable the profile. The `on-failure:5` policy intentionally
does not restore the worker after Docker daemon or host restart; guarded host
startup owns that operation.

## Change checklist

- Update this page and `deploy/.env.example` when an operator must supply a new
  nonsecret key. Update the root `.env.example` when the root Compose template
  consumes it.
- Update [Feature flags](feature-flags.md) for `FeatureOptions`.
- Update [Deployment](../operations/deployment.md) for role/container changes.
- Update [VPN proxy pool](../operations/vpn-proxy-pool.md) for proxy arrays,
  provider behavior, pacing, or self-heal.
