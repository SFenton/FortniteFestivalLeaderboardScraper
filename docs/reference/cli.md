---
status: canonical
owner: service
last_verified: 2026-08-15
last_verified_commit: 354f87eb
sources:
  - FSTService/Program.cs
  - FSTService/ScraperOptions.cs
  - FSTService/ScrapePhase.cs
  - FSTService/Scraping/PostScrapeOrchestrator.cs
  - FSTService/Persistence/PublishedScrapeIdArgument.cs
  - FSTService/Persistence/MaxScoreMaintenanceCommand.cs
  - FSTService/Persistence/MaxScoreMaintenanceModels.cs
  - FSTService/Persistence/MaxScoreMaintenanceFileStore.cs
  - FSTService/Persistence/MetaDatabase.cs
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
pass of a continuous worker. `--band-post-scrape` alone is the supported
direct legacy band-fetch mode. `--band-scrape` includes the legacy phase flag
for compatibility but the modern `BandScrape` result suppresses the duplicate
legacy fetch.

## Schema, recovery, and maintenance

| Command | Default behavior | Additional flags |
|---|---|---|
| `--initialize-schema-only` | Apply idempotent schema and exit | Cannot combine with maintenance/recovery commands |
| `--recover-improvement-notifications` | Execute recovery for one exact published scrape | Required `--published-scrape-id`; optional `--notification-dry-run`, `--notification-baseline-only`, `--notification-skip-projection-refresh`, `--notification-force` |
| `--score-history-dedup-maintenance` | Read-only deterministic report | Execute also requires `--score-history-dedup-execute` and `--expected-score-history-dedup-digest` `<sha256>` |
| `--solo-family-ranking-backfill` | Dry-run report | `--solo-family-ranking-backfill-execute` |
| `--leaderboard-rivals-recompute-account` `<id>` | Recompute one account and exit | Accepts `--flag=value` form |

Maintenance commands are mutually exclusive where enforced by `Program.cs`.
Use the matching living runbook; CLI availability is not authorization to run
against production.

`--published-scrape-id` is parsed once for improvement-notification recovery
and max-score maintenance. Both `--published-scrape-id 1296` and
`--published-scrape-id=1296` are accepted. The owning command requires exactly
one positive value; duplicates, blank/malformed values, and an orphaned scrape
ID without either owning command are startup errors. The shared option does not
activate max-score parsing by itself.

### Max-score correction

All max-score files must be `.json` paths below `Scraper:DataDirectory`.
Stage requests and manifests use canonical strict JSON; unknown properties,
noncanonical encoding, unsupported versions, duplicate/unsorted song IDs, and
more than 32 songs are rejected. Request version 2 binds a discovery or
promotion purpose, exact runtime, exact generated/changed instrument sets, and
per-song maximum constraints. Unscoped repeated song IDs are rejected.

| Action | Required flags | Behavior |
|---|---|---|
| `--max-score-maintenance-stage` | `--published-scrape-id`, `--max-score-maintenance-stage-request`, `--max-score-maintenance-manifest-output`, `--max-score-maintenance-report-output` | Serially stage complete immutable generations without pointer mutation; discovery permits explicit partial maximum constraints, while promotion requires complete old/new eight-field maxima |
| `--max-score-maintenance-plan` | `--published-scrape-id`, promotion-purpose `--max-score-maintenance-manifest`, `--expected-max-score-manifest-digest`, `--max-score-maintenance-report-output` | Read-only fail-closed preflight; rejects discovery/v3 plastic manifests, validates current rollback and staged artifact trees/hashes plus mapped observed scores against the exact integer ranking cutoff, records publication-population and complete consumed score-history count/range/hash evidence, and emits the deterministic `planDigest` |
| `--max-score-maintenance-apply` | plan flags plus `--expected-max-score-plan-digest` and `--max-score-maintenance-rollback-output` | Freeze, persist rollback evidence, atomically promote all songs, rebuild derived state, quarantine notifications, stage/publish caches, validate, and unfreeze |
| `--max-score-maintenance-resume` | apply manifest/scrape/digest flags and a new report output; rollback output is required only before it has been durably captured | Resume only the same digest/phase identities; any failure after freeze remains frozen |

Every action writes a versioned report. Apply/resume exit `2` with
`resumable=true` after a post-freeze failure. Do not manually clear the freeze;
rerun `--max-score-maintenance-resume` with the same manifest and digests. The
rollback snapshot timestamp comes from the persisted maintenance run, so a
crash after file creation but before its database checkpoint reproduces and
validates the same canonical bytes.

Plan report version 5 includes `populationEvidence`, `scoreHistoryEvidence`,
and `validCutoff` on every `observedScoreChecks` row. The cutoff is exactly
`RankingsCalculator.ComputeMaxScoreThreshold(newMaximum)`, currently
`floor(newMaximum × 1.05)`: scores above the CHOpt denominator remain valid
when they do not exceed this cutoff. Missing source mappings and scores above
the cutoff fail closed. Plan-digest contract version 5 binds the same evidence.
Apply rebuilds the plan before freeze; apply and resume then reload the
observed-score rows and reconstruct the approved digest before mutation.

Apply/resume report version 3 includes
`cacheEvidence` after cache staging, including the exact
publication-scope key count/fingerprint. Exact per-entry key/ETag/JSON hashes
remain durable database evidence rather than expanding the report. Plan may scan all registered-account
history plus affected-instrument fallback candidates; its aggregates are
constant-memory, but operators must allow the documented maintenance-window
cost. The strict apply-report parser rejects legacy version 2, unknown
properties, and version 3 reports missing `cacheEvidence` at
`caches_staged` or any later phase. Version 3 failures before cache staging
retain `cacheEvidence=null`.

Plan/apply/resume evidence and revalidation use
`Scraper:MaxScoreMaintenanceCommandTimeoutSeconds` uniformly. The default is
`600`; production may pass
`Scraper__MaxScoreMaintenanceCommandTimeoutSeconds=1800`, and startup rejects
values outside `1`-`86400`. This does not alter normal scrape timeouts. A
final completion transaction uses the configured server timeout only for
immutable cache validation, keeps its `5s` lock timeout, and restores the
`120s` mutation timeout before swap/checkpoint/unfreeze. Any failure remains
frozen. A failed plan's `plan` check identifies the sanitized evidence stage
and the base exception message without serializing SQL or connection data.

The retired `--path-repair-*` and
`--notification-maintenance-pro-lead-max-score-repair` families remain startup
errors in every supported prefix/value form.

See
[Max-score correction maintenance](../database/MaxScoreCorrectionMaintenanceRunbook.md).
