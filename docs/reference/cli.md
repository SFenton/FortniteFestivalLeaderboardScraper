---
status: canonical
owner: service
last_verified: 2026-08-13
last_verified_commit: 9d11111e
sources:
  - FSTService/Program.cs
  - FSTService/ScrapePhase.cs
  - FSTService/Persistence/MaxScoreMaintenanceCommand.cs
  - FSTService/Persistence/ScoreHistoryDedupMaintenanceCommand.cs
  - FSTService/Scraping/SoloFamilyRankingBackfillCommand.cs
  - FSTService/Scraping/LeaderboardRivalsRecomputeCommand.cs
update_triggers:
  - A command-line flag, combination rule, one-shot mode, or phase expansion changes.
---

# FSTService CLI

Use `dotnet FSTService.dll <flags>` in a built image or the equivalent
`dotnet run --project FSTService/FSTService.csproj -- <flags>` locally.

## Hosting and setup

| Flag | Behavior |
|---|---|
| `--setup` | Device-code authentication setup, then exit |
| `--api-only` | API-only mode; no scraper pipeline |
| `--no-scraper-worker` | Frontend/API role without scheduled scraper mutation |
| `--registration-sync-worker` | Registration refresh/backfill worker without scheduled scrape/band history |
| `--once` | One scrape/publication pass, then exit |
| `--rollout-read-only-startup` | Register only persisted-state loading/HTTP serving |
| `--rollout-postgres-read-only` | Enforce the paired PostgreSQL read-only rollout mode |

The two rollout read-only flags must be enabled together.

## Focused execution

| Flag | Behavior |
|---|---|
| `--test` `<song>` | Fetch a single-song diagnostic and exit |
| `--resolve-only` | Resolve unresolved account names |
| `--backfill-only` | Run existing-entry rank/percentile enrichment |
| `--precompute` | Rebuild API response precomputation from a complete current projection |

## Selectable scrape phases

Primary groups:

- `--solo-scrape`
- `--solo-leaderboards`
- `--band-scrape`

Individual flags:

- `--solo-enrichment`
- `--solo-refresh-users`
- `--solo-rivals`
- `--solo-player-stats`
- `--solo-precompute`
- `--solo-finalize`
- `--band-post-scrape`
- `--band-extraction`

`ScrapePhaseResolver` expands groups and fills intermediate solo phases. No
phase flags means the full pipeline. Launch selections apply only to the first
pass of a continuous worker.

## Schema, recovery, and maintenance

| Command | Default behavior | Additional flags |
|---|---|---|
| `--initialize-schema-only` | Apply idempotent schema and exit | Cannot combine with maintenance/recovery commands |
| `--recover-improvement-notifications` | Execute recovery for the current published scrape | `--published-scrape-id`, `--notification-dry-run`, `--notification-baseline-only`, `--notification-skip-projection-refresh`, `--notification-force` |
| `--score-history-dedup-maintenance` | Read-only deterministic report | Execute also requires `--score-history-dedup-execute` and `--expected-score-history-dedup-digest` `<sha256>` |
| `--solo-family-ranking-backfill` | Dry-run report | `--solo-family-ranking-backfill-execute` |
| `--leaderboard-rivals-recompute-account` `<id>` | Recompute one account and exit | Accepts `--flag=value` form |

Maintenance commands are mutually exclusive where enforced by `Program.cs`.
Use the matching living runbook; CLI availability is not authorization to run
against production.

### Max-score correction

All max-score files must be `.json` paths below `Scraper:DataDirectory`.
Manifests use canonical strict JSON; unknown properties, noncanonical encoding,
unsupported versions, duplicate/unsorted song IDs, and more than 32 songs are
rejected.

| Action | Required flags | Behavior |
|---|---|---|
| `--max-score-maintenance-stage` | `--published-scrape-id`, exactly one of `--max-score-maintenance-stage-request` or repeated `--max-score-maintenance-song-id`, `--max-score-maintenance-manifest-output`, `--max-score-maintenance-report-output` | Serially stage complete inferred immutable generations; never mutate a song pointer |
| `--max-score-maintenance-plan` | `--published-scrape-id`, `--max-score-maintenance-manifest`, `--expected-max-score-manifest-digest`, `--max-score-maintenance-report-output` | Read-only fail-closed preflight; emits the deterministic `planDigest` |
| `--max-score-maintenance-apply` | plan flags plus `--expected-max-score-plan-digest` and `--max-score-maintenance-rollback-output` | Freeze, persist rollback evidence, atomically promote all songs, rebuild derived state, quarantine notifications, stage/publish caches, validate, and unfreeze |
| `--max-score-maintenance-resume` | apply manifest/scrape/digest flags and a new report output; rollback output is required only before it has been durably captured | Resume only the same digest/phase identities; any failure after freeze remains frozen |

Every action writes a versioned report. Apply/resume exit `2` with
`resumable=true` after a post-freeze failure. Do not manually clear the freeze;
rerun `--max-score-maintenance-resume` with the same manifest and digests.

The retired `--path-repair-*` and
`--notification-maintenance-pro-lead-max-score-repair` families remain startup
errors in every supported prefix/value form.

See
[Max-score correction maintenance](../database/MaxScoreCorrectionMaintenanceRunbook.md).
