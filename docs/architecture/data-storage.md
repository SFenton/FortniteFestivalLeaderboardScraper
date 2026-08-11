---
status: canonical
owner: data
last_verified: 2026-08-11
last_verified_commit: 453fd9b6
sources:
  - FSTService/Persistence/DatabaseInitializer.cs
  - FSTService/Persistence/MetaDatabase.cs
  - FSTService/Persistence/InstrumentDatabase.cs
  - FSTService/Persistence/GlobalLeaderboardPersistence.cs
  - FSTService/FeatureOptions.cs
  - deploy/postgres.Dockerfile
update_triggers:
  - Schema, persistence ownership, publication storage, retention, restore, or source-of-truth behavior changes.
---

# Data storage

## Authority

PostgreSQL 17, accessed through Npgsql and parameterized SQL, is the service
source of truth. The service does not use an ORM.

`MetaDatabase`, `InstrumentDatabase`, and other repository-style classes are
logical ownership boundaries over the same PostgreSQL database. In particular,
an `InstrumentDatabase` applies an instrument predicate to shared relations; it
is not a separate per-instrument SQLite database.

`FortniteFestival.Core` still contains legacy file/SQLite compatibility code
because it targets both .NET Framework 4.7.2 and .NET 9. That compatibility
surface is not the production service persistence model.

## Data families

| Family | Purpose |
|---|---|
| Catalog/provider capture | Exact Epic song payloads, catalog versions, images, item shop, path-generation inputs |
| Scrape/candidate state | Scrape runs, page coverage, writer outcomes, manifests, replay/failure evidence |
| Solo leaderboard state | Physical snapshots, overlays, current projections, ranks, history, population, first-seen data |
| Band state | Membership/context, rankings, histories, extraction state, tracked bands |
| Account state | Display names, registrations, selected profiles, refresh/backfill progress |
| Derived products | Rankings, rivals, statistics, precomputed responses, improvement notifications |
| Publication state | Published scrape/generation, source bindings, read freeze, commit intent, leases, cache generations |
| Operations/audit | Worker heartbeat, maintenance evidence, dedup/recovery audit state |

The exact relation inventory is intentionally source-driven because it changes
frequently. `DatabaseInitializer` and its tests are the schema inventory;
canonical documentation describes ownership and invariants instead of copying
volatile table counts.

## Publication ownership

Candidate writes do not become public merely because they were committed to a
table. Publication validates the candidate, prepares generation-bound state,
drains readers, and atomically advances the published pointer.

Feature flags support staged migration among legacy mutable rows, snapshot and
overlay readers, per-scope published sources, and generation-aware reads. Role
files intentionally use different read/write settings for `fstservice` and
`fstworker`.

## Storage and maintenance rules

- Production data, scratch, exports, repacks, and migration artifacts stay on
  the 4 TB FST drive unless the operator explicitly overrides the rule.
- Destructive maintenance requires exact affected objects, parity evidence,
  rollback, live preflight, and a bounded maintenance window.
- Schema initialization is idempotent but is not a substitute for a bounded
  maintenance command.
- Preserve Epic/provider provenance, historical leaderboard correctness,
  publication state, and replay evidence.

## Current procedures and history

Use the [living runbook index](../operations/runbooks/README.md). Completed
physical cleanup, retirement, compaction, and rejected rollout evidence is
preserved under
[`docs/archive/legacy/database/`](../archive/legacy/database/).
