---
status: canonical
owner: service
last_verified: 2026-08-14
last_verified_commit: faaa6d73
sources:
  - FSTService/Program.cs
  - FSTService/ScrapePhase.cs
  - FSTService/Persistence/ScoreHistoryDedupMaintenanceCommand.cs
  - FSTService/Scraping/SoloFamilyRankingBackfillCommand.cs
  - FSTService/Scraping/LeaderboardRivalsRecomputeCommand.cs
  - FSTService/Scraping/Replay/ReplayCommand.cs
  - FSTService/Scraping/Replay/ReplayEntryPoint.cs
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

## Isolated phase replay candidate

Replay dispatch occurs before `.env` loading, `WebApplication` construction,
HTTP hosting, hosted-worker registration, provider clients, notifications, and
publication services.

The protocol-v1 execution command requires all of:

```text
--replay-parent-package <sealed-tier0-root>
--replay-package <sealed-tier1-input-root>
--replay-phase post.band_maintenance
--replay-subphase current_projection_refresh
--replay-output <new-attempt-directory>
--replay-id <manifest-replay-id>
--replay-attempt <positive-integer>
--no-publication
```

Only the bounded BandMaintenance current-projection refresh kernel is
replayable. Unknown phase IDs, other stable phases, other BandMaintenance
subphases, provider/network phases, phase ranges, and publication are rejected.

Comparison is a separate no-database command. It requires baseline/candidate
package paths, report output, exact expected image digest, Git commit, OCI
revision, attempt number for both lanes, and `--no-publication`:

```text
--replay-compare-baseline <package>
--replay-compare-candidate <package>
--replay-comparison-output <new-json-file>
--replay-baseline-image-digest <sha256:...>
--replay-baseline-git-commit <commit>
--replay-baseline-revision <revision>
--replay-baseline-attempt <positive-integer>
--replay-candidate-image-digest <sha256:...>
--replay-candidate-git-commit <commit>
--replay-candidate-revision <revision>
--replay-candidate-attempt <positive-integer>
--no-publication
```

Exit codes distinguish usage, root, package, target, import, phase, output,
comparison, cancellation, and unexpected failures. Output/comparison format
version `2` always emits `productionComparableTiming=false` with the
deterministic-override reason. CLI availability does not authorize
production-derived capture, full BandMaintenance, provider access, live
replay, or deployment.

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
