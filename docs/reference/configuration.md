---
status: canonical
owner: operations
last_verified: 2026-08-25
last_verified_commit: 8c056d1d
sources:
  - FSTService/appsettings.json
  - FSTService/ScraperOptions.cs
  - FSTService/SongCatalogRefreshWorker.cs
  - FSTService/Scraping/PathGenerationModels.cs
  - FSTService/Scraping/PathDataStore.cs
  - FSTService/Persistence/PublicationPathArtifactSchema.cs
  - FSTService/Scraping/ScrapePassPathIngestion.cs
  - FSTService/Program.cs
  - FSTService/FeatureOptions.cs
  - FSTService/Scraping/PostScrapeOrchestrator.cs
  - FSTService/Persistence/MetaDatabase.cs
  - FSTService/Api/PublicationApiResponseCachePolicy.cs
  - docker-compose.yml
  - .env.example
  - deploy/docker-compose.yml
  - deploy/config/fstservice-role.env
  - deploy/config/fstworker-role.env
  - deploy/.env.example
  - tools/fst-worker-compose-guard.sh
  - FSTService/Scraping/Replay/ReplaySecurity.cs
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
| `Features` | persistence/publication rollout and App Manual |
| `ClientTelemetry` | bounded browser interaction diagnostics |
| `ImprovementNotifications` | notification scope, projection refresh, staleness |
| `PublicationCommit` | read drain, locks, retries, leases, deferred recovery |
| `BandRankHistory` | mode, storage/read source, compaction/retention behavior |
| `BandTeamRankings` | ranking writer strategy |
| `DatabaseMaintenance` | retention, pressure guards, cleanup and snapshot rewrite |
| `BackgroundJobs` | background scheduling |
| `Api` | API key and allowed origins |
| `ConnectionStrings` | PostgreSQL |
| `Kestrel` | HTTP listener |

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
| `Scraper:ScrapePassPathGenerationAllowChangedMaxima` | `false` | Applies a regenerated song whose existing maxima changed. Off records `max_score_change_requires_review` and leaves the song pending |
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

The Compose form is
`Scraper__BandCurrentProjectionUseBatchedMemberStatsAggregation`. The switch
changes only the current-projection query shape; scope filtering, concurrency,
transactions, generation/state writes, publication, cleanup, and ordering
remain unchanged. It is intentionally absent from production role overrides
and therefore remains off. Set it back to `false` for immediate code-path
rollback. Enabling it in production requires a capacity-safe matched full
scrape A/B and exact publication/data parity; isolated replay timing is not
promotion evidence.

## Player rivals

| Key | Default | Valid range | Purpose |
|---|---:|---:|---|
| `Scraper:RivalsMaxDegreeOfParallelism` | `2` | positive integer | Maximum registered accounts whose song-neighborhood rival scans may run concurrently |

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

## Role differences

`deploy/config/fstservice-role.env` enables published-source reads while
disabling published-source writes, stored-rank rollout, unchanged-snapshot
reuse, legacy automatic path generation, and publication read context. It sets
`Scraper__UsePublicationPathArtifacts=true`, so the service serves path state
and CHOpt maxima from the publication snapshot and rejects immediate admin path
regeneration. It intentionally does not set
`Scraper__EnableScrapePassPathGeneration`: staging is worker-only.

`deploy/config/fstworker-role.env` sets
`Scraper__UsePublicationPathArtifacts=true` and
`Scraper__EnableScrapePassPathGeneration=true` with the bounded
`ScrapePassPathGenerationMaxSongs=25`,
`ScrapePassPathGenerationTimeout=00:20:00`, and
`ScrapePassPathGenerationAllowChangedMaxima=false`. Legacy
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
context disabled, and sets
`WriteLegacyLiveLeaderboardDuringScrape=false`.
With that value, the post-scrape legacy stored-rank phase completes its
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
requires positive `Scraper:Resume*` metrics,
`Scraper:RegistrationSyncWorkerOnly=false`, the publication correctness and
snapshot-reuse gates, and
`Scraper:RivalsMaxDegreeOfParallelism=2`. This profile is for an existing
resume-eligible candidate only and does not authorize a new network scrape.
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
