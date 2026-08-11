---
status: canonical
owner: operations
last_verified: 2026-08-11
last_verified_commit: 453fd9b6
sources:
  - AGENTS.md
  - .github/copilot-instructions.md
  - .github/instructions/fst-postgres.instructions.md
  - FSTService/Scraping/ScrapeLifecycleNotifier.cs
  - docs/operations/deployment.md
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

Completed destructive runbooks in the archive are evidence, not reusable
authorization.

## Service availability

`fstworker`, `fstservice`, and `festivalweb` may be restarted or briefly stopped
for useful maintenance, but recover the public experience promptly. Avoid
leaving the API or web role with worker-only flags, Docker access, or candidate
read ownership.
