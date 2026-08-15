---
status: canonical
owner: operations
last_verified: 2026-08-15
last_verified_commit: ba2907a8
sources:
  - AGENTS.md
  - .github/copilot-instructions.md
  - .github/instructions/fst-postgres.instructions.md
  - FSTService/Scraping/ScrapeLifecycleNotifier.cs
  - FSTService/ScraperOptions.cs
  - FSTService/Persistence/MaxScoreMaintenanceService.cs
  - docs/operations/deployment.md
  - tools/fst-worker-compose-guard.sh
  - tools/fst-worker-no-progress-watchdog.mjs
update_triggers:
  - Production ownership, preflight, maintenance, parity, publication, storage, or recovery rules change.
---

# Live safety

## Production boundaries

- The live Compose project is
  `/home/sfenton/Docker/FestivalServiceTracker`.
- Repository Compose files are templates.
- All database data, scratch, exports, migration artifacts, repacks, and
  retention work stay on the 4 TB FST drive unless the operator explicitly
  overrides the rule.
- Keep secrets out of commands, logs, documentation, artifacts, e-mail, and
  commits.

## Before broad probes, deploys, scrapes, or maintenance

Check:

1. Docker service health;
2. PostgreSQL readiness and cluster identity;
3. public-read freeze and publication state;
4. the current published scrape/generation;
5. locks and long-running queries;
6. disk headroom on the FST drive;
7. CPU and memory pressure.

Use bounded read-only probes first.

## Startup auto-heal

The repository entry point is
`tools/fst-worker-compose-guard.sh --recover-start`; the live copy and boot-unit
wiring remain production-owned. The action assumes the production orchestrator
has already started core services and effective proxies without dependencies.
It must not be used as a general Compose reconciler.

Before proxy mutation it verifies:

- the merged continuous configuration and exact effective arrays;
- the guard-only `worker` profile and continuous `on-failure:5` policy;
- the shared nonblocking worker start/recreate lock;
- PostgreSQL health and `fstservice` readiness;
- a stopped/absent worker container;
- `currentUpdate.status=idle`;
- unfrozen public reads.

The initial and post-recreate proxy windows are finite, proxy recreates are
effective-set-only and capped, and worker startup happens only after all runtime
probes pass. A 1,800-second total deadline also caps core readiness, proxy
convergence, runtime DNS/control/egress qualification, and worker readiness.
It never clears a freeze, rewrites publication state, restarts core services,
changes provider selectors, promotes spares, or installs static endpoint IPs.

Size the production unit timeout above the total deadline plus cleanup margin.
The shared lock defaults to `.fst-worker-compose-guard.lock` under the resolved
Compose directory. Every unit and operator must use that same resolved
directory and Unix owner, or configure the same explicit absolute lock path.

`on-failure:5` covers only bounded nonzero worker-process exits while the Docker
daemon remains running. It does not authorize daemon-boot restart; the profiled
worker remains excluded from generic Compose startup and the guarded host
handoff owns restart after reboot or daemon recovery. The guard explicitly
passes `--profile worker` for merged config inspection and worker-targeted
starts; proxy-only recovery never activates that profile.

### Post-start non-convergence

Before stopping an unaccepted worker, the guard re-reads `/api/service-info`.
It stops only when the update remains idle and public reads remain unfrozen. If
work has begun, reads are frozen, or the state cannot be verified, it leaves the
worker running rather than strand a candidate or freeze.

The canonical follow-up is the guarded no-progress watchdog:

```bash
node tools/fst-worker-no-progress-watchdog.mjs \
  --evidence-dir <FST-drive-evidence-path> \
  --dry-run
```

Keep evidence on the 4 TB FST drive. Remove `--dry-run` only after the
watchdog's own observation proves its timeout, database-activity,
candidate-mapping, publication-pointer, and rollback gates. Do not manually
stop the worker or clear the freeze first.

Do not bypass a failed gate by relaxing `service_healthy`, enabling a candidate
continuous profile, or broad-recreating the canonical pool. Investigate the
reported sanitized stage while keeping API/web/PostgreSQL available.

## Public-read and publication safety

During a scrape the worker freezes public reads on the prior published
generation. Failed or incomplete candidates do not replace it. If durable
failure isolation is uncertain, the system remains fail-closed.

Preserve:

- historical leaderboard correctness;
- Epic/provider provenance;
- publication pointer and generation bindings;
- freeze/unfreeze behavior;
- replay and parity evidence;
- notification completion requirements.

## Destructive work

Destructive data/reclaim work is allowed only after a current live-scrape A/B
proves the new path has the same data as the old path. Record:

- exact affected objects;
- accepted parity evidence;
- rollback procedure and boundaries;
- maintenance window and monitoring;
- validation that the command cannot target a different cluster/project.

Removed completed runbooks and Git history are forensic evidence, not reusable
authorization.

## Current-publication max-score correction

Use the
[max-score correction runbook](../database/MaxScoreCorrectionMaintenanceRunbook.md)
after the recurring path rule is fixed. Stage is pointer-free. Plan/apply
require a promotion-purpose v4 manifest (discovery is never promotable), the
worker offline, exact publication/catalog/path and notification state,
validated current rollback and staged artifact trees/hashes, the
path-generation/publication lock order, same-drive evidence, and a reviewed
manifest plus plan digest.

Apply owns a `max-score-maintenance:v1:<manifest-sha256>` freeze. Generic
scrape/publication freeze writers cannot overwrite or clear it. A failure
leaves public reads fail-closed and must be continued with the matching resume
command; do not manually unfreeze. Cache publication and freeze release commit
together only after derived, notification, rollback, and rank-history
validation. A `validated` checkpoint also protects its cache staging
generation from ordinary builders/writers; resume and the final locked
transaction require exact key/ETag/JSON-hash parity with immutable database
evidence. Never repair this by clearing the freeze or publishing a different
staging generation.

For a reviewed long-running evidence scan, set the bounded per-command override
documented in the runbook; production currently uses
`Scraper__MaxScoreMaintenanceCommandTimeoutSeconds=1800`. The override keeps
Npgsql cancellation and transaction-local PostgreSQL statement timeouts; it
does not authorize running with an active worker, weakening source locks, or
clearing a post-freeze failure. Plan failures identify the evidence stage so
operators can distinguish publication-population, complete score-history, and
other evidence without exposing SQL or credentials.

## Service availability

`fstworker`, `fstservice`, and `festivalweb` may be restarted or briefly stopped
for useful maintenance, but recover the public experience promptly. Avoid
leaving the API or web role with worker-only flags, Docker access, or candidate
read ownership.
