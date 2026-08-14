---
status: decision
owner: worker
last_verified: 2026-08-12
last_verified_commit: cb295b7e
sources:
  - FSTService/FSTService.csproj
  - FSTService/Program.cs
  - FSTService/HostedWorkerMode.cs
  - FSTService/ScraperWorker.cs
  - FSTService/Scraping/PostScrapeOrchestrator.cs
  - FSTService/Scraping/ScrapeProgressTracker.cs
  - docs/decisions/0001-split-service-worker-roles.md
  - docs/architecture/data-publication-flow.md
  - docs/roadmap/post-scrape-processing.md
  - FSTService/Scraping/Replay/ReplayEntryPoint.cs
  - FSTService/Scraping/Replay/TierOneReplayRunner.cs
update_triggers:
  - Post-scrape phase contracts, replay hosting, process boundaries, deployment ownership, or data ownership changes.
---

# ADR 0005: Keep post-scrape processing in a modular monolith

## Decision

Keep live post-scrape processing in the existing FSTService modular monolith and
one-image API/worker role model.

Introduce stable in-process phase IDs, descriptors, dependencies, resource
classes, progress contracts, and input/output results before changing process
boundaries.

Implement artifact replay first as a guarded one-shot mode of the existing
FSTService binary running against isolated same-drive PostgreSQL. Do not create
a new runner project until a second consumer or a measured host-isolation
problem justifies it.

Do not use runtime-loaded phase plugins or extract post-scrape microservices at
the current scale.

The first implementation slice is deliberately narrower than full
BandMaintenance: stable phase `post.band_maintenance`, adapter
`current_projection_refresh`, one band type, bounded overall scopes, and fresh
isolated PostgreSQL only. This is sufficient to iterate the dominant
current-projection SQL/algorithm while prune, search projection, provider
capture, publication, and notification coupling remain unsupported.
Replay forces unchanged-scope skipping off, one band-type worker, synchronous
commit, and candidate cleanup off. Its timing is explicitly non-comparable to
production; option-parity replay or a separate bounded probe is required for
production optimization claims.

## Context

Post-scrape work shares:

- one candidate scrape and exact catalog;
- one PostgreSQL source of truth;
- global snapshot, overlay, band, ranking, rival, cache, notification, and
  publication state;
- a strict publication-critical dependency graph;
- provider, CPU, memory, WAL, temp, I/O, and disk budgets;
- one final atomic publication decision.

The public API and mutation worker are already separate processes from one
image. The web application is independently deployable. This gives the useful
availability and permission boundary without introducing distributed
post-scrape consistency.

Current phase implementations are testable but not yet stable modules:
`PostScrapeOrchestrator` has a large dependency graph, progress has one
process-local owner, several phase boundaries are implicit, and replay cannot
run arbitrary phase slices from a sealed parent state.

## Rationale

### Modular monolith

- Preserves the current atomic candidate/publication model.
- Keeps one schema and migration owner.
- Avoids network calls between tightly coupled phases.
- Avoids duplicating connection pools, caches, runtime configuration, logging,
  authentication, and resource baselines.
- Allows phase interfaces and an explicit DAG to be proven without changing
  deployed topology.
- Keeps worker/API binaries compatible while preserving their independent
  container lifecycles.

### Existing-binary replay

- A one-shot FSTService process already provides process/container isolation.
- It reuses the same implementations, DI graph, configuration validation, and
  image digest as production.
- A guarded replay mode can reject production targets and disable publication.
- It avoids a second executable/project before another real consumer exists.
- It provides a migration path to a dedicated runner later if startup/resource
  evidence proves the host is unsuitable.

The replay command branches before `.env` loading and `WebApplication`
construction. This preserves one binary/image without inheriting the normal
host's DI graph, production credentials, provider network, hosted workers,
Docker access, HTTP serving, caches, notifications, or publication authority.

Tier-1 import is an allowlisted data contract, not a database restore. The
isolated target must be a marker-owned fresh database on a different
PostgreSQL system identifier than the captured source. Baseline and candidate
outputs are immutable child packages compared by an independently pinned
baseline image.

## Alternatives

### Runtime-loaded DLL plugins

Rejected.

- Assembly loading is a dependency/versioning mechanism, not a process or
  security boundary.
- Shared static/process state and PostgreSQL ownership remain.
- Type identity, unload, dependency probing, and version skew add failure modes
  without improving publication consistency.
- Hot replacement would make evidence and rollback harder to bind to one image
  digest.

Static assemblies may be extracted later for dependency hygiene, but they will
remain compile-time referenced and image-versioned.

### New phase-runner project now

Rejected for the initial implementation.

- There is no second consumer yet.
- Current replay gaps are contracts, manifests, phase selection, isolated
  inputs, and publication guards, not the absence of another `csproj`.
- The existing binary already supports multiple one-shot/hosted modes.

Reconsider when:

- the existing host has measured startup or resource overhead that harms replay;
- a second independently versioned consumer exists;
- phase implementations have a clean dependency direction;
- one image can still bind implementation and schema compatibility.

### Microservices

Rejected at the current scale.

- True independent deployment would require independent data ownership rather
  than several services mutating one shared schema.
- Cross-phase transactions would become queues, events, retries, sagas, and
  eventual consistency.
- Publication would need versioned distributed contracts and stronger
  reconciliation.
- Connection pools, runtimes, telemetry, health checks, resource reservations,
  and failure recovery would be duplicated.
- No observed workload requires independent horizontal scaling strongly enough
  to offset those costs.

Reconsider only after a phase has:

1. stable versioned input/output contracts;
2. generation-scoped or independently owned data;
3. independent scaling/deployment pressure;
4. idempotent queue/retry/recovery semantics;
5. distributed tracing and compatibility enforcement;
6. measured benefit greater than the simpler process or in-process option.

## Migration sequence

1. Restore complete timing bootstrap and instrument dominant phases.
2. Define stable phase IDs, descriptors, criticality, dependencies, resource
   classes, progress units, and results in the existing assembly.
3. Replace implicit orchestration with an explicit graph while retaining
   current serial order.
4. Add a normalized phase-attempt ledger and backward-compatible current
   summary.
5. Define sealed artifact and per-phase input/output manifests.
6. Add guarded one-shot replay to FSTService against isolated PostgreSQL.
7. Prove deterministic baseline/candidate execution from identical parent
   inputs.
8. Extract static assemblies only when dependency direction or reuse justifies
   them.
9. Introduce a separate runner project/process only after a measured need.
10. Reconsider service extraction only after the microservice gates above pass.

## Consequences

- The worker remains the only live mutation owner.
- Phase-level independent production deployment is not yet supported.
- API/web/worker containers retain independent deployment and rollback.
- Replay is independently invokable but image-locked to production code.
- Phase contracts, manifests, and schema compatibility become explicit
  architecture rather than test-only conventions.
- A replay process has no production publication authority and must fail closed
  on a production target.
- PostgreSQL remains authoritative; DuckDB/Parquet may query or transport
  bounded artifacts only.
- Full N+1 overlap remains rejected until candidate/publication/storage
  ownership is redesigned.
