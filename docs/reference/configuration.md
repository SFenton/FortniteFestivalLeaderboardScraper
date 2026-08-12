---
status: canonical
owner: operations
last_verified: 2026-08-11
last_verified_commit: 453fd9b6
sources:
  - FSTService/appsettings.json
  - FSTService/Program.cs
  - docker-compose.yml
  - .env.example
  - deploy/docker-compose.yml
  - deploy/config/fstservice-role.env
  - deploy/config/fstworker-role.env
  - deploy/.env.example
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

## Change checklist

- Update this page and `deploy/.env.example` when an operator must supply a new
  nonsecret key. Update the root `.env.example` when the root Compose template
  consumes it.
- Update [Feature flags](feature-flags.md) for `FeatureOptions`.
- Update [Deployment](../operations/deployment.md) for role/container changes.
- Update [VPN proxy pool](../operations/vpn-proxy-pool.md) for proxy arrays,
  provider behavior, pacing, or self-heal.
