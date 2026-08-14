---
status: canonical
owner: operations
last_verified: 2026-08-13
last_verified_commit: 96ed9680
sources:
  - FSTService/appsettings.json
  - FSTService/Program.cs
  - docker-compose.yml
  - .env.example
  - deploy/docker-compose.yml
  - deploy/config/fstservice-role.env
  - deploy/config/fstworker-role.env
  - deploy/.env.example
  - tools/fst-worker-compose-guard.sh
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

## Path generation

| Key | Default | Purpose |
|---|---|---|
| `Scraper:CHOptPath` | `tools/CHOpt` | Bundled CHOpt launcher or binary |
| `Scraper:EnablePathGeneration` | `true` | Allows explicit path generation |
| `Scraper:EnableAutomaticPathGeneration` | `false` | Processes only pending songs from background catalog refresh when enabled |
| `Scraper:PathGenerationParallelism` | `4` | Maximum concurrent CHOpt processes |
| `Scraper:PathGenerationProfile` | `chopt-fnf-ew0-s20-json-png-prodrums-v4` | Semantic identity for the dedicated plastic-drums MIDI variant, activation model, eight-instrument scope, and artifact schema |

The MIDI decryption key is operator-supplied and must not appear in logs,
documentation, artifacts, or commands. Profile changes invalidate selected
older generations but do not select the full catalogue; use the guarded
sequential procedure in [Path generation](../components/path-generation.md).

The option classes are authoritative when a property exists but is omitted from
`appsettings.json`.

## Role differences

`deploy/config/fstservice-role.env` enables published-source reads while
disabling published-source writes, stored-rank rollout, unchanged-snapshot
reuse, automatic path generation, and publication read context.

`deploy/config/fstworker-role.env` skips startup schema initialization, enables
the three publication correctness gates, writes published scope sources, keeps
public-read ownership off the worker, leaves unchanged-snapshot reuse and
publication read context disabled, and sets
`WriteLegacyLiveLeaderboardDuringScrape=false`.

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

## Environment naming

Use the .NET key in prose (`Features:AppManual`) and the Compose form in
examples (`Features__AppManual`). Shell-friendly aliases such as
`FEATURE_APP_MANUAL` are template inputs, not service option names.

## Worker guard environment

These variables configure host-side
`tools/fst-worker-compose-guard.sh` mutation/recovery behavior. They are not
FSTService options and do not belong in container environment arrays.

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
