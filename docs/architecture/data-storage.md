---
status: canonical
owner: data
last_verified: 2026-08-12
last_verified_commit: 042f9686
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
| Operations/audit | Worker heartbeat, terminal scrape-phase outcomes, detailed subphase timings, maintenance evidence, dedup/recovery audit state |

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

## Phase timing evidence

`scrape_phase_outcomes` is the terminal correctness ledger for named
post-scrape phases and their publication criticality.

`scrape_phase_timings` is append-only operational evidence for finer subphases.
Its bootstrap shape intentionally matches the surviving production relation:

- `BIGSERIAL` primary key;
- scrape, phase, optional subphase/item, timestamps, duration, optional
  row/scope counts, success, and optional error;
- no foreign key in this compatibility repair;
- indexes on `(scrape_id, phase, subphase, item_key)` and
  `started_at DESC`.

Timing persistence is best effort and cannot change phase failure,
cancellation, or publication behavior. Retention remains owned by the existing
service-level metadata cleanup.

For BandMaintenance timing rows, `success=false` means the subphase did not
complete successfully, including cancellation. Optional row/scope metrics are
null on failure because partial work may have occurred. Successful no-work
subphases record zero. For `current_projection_refresh`, `rows_read` stores the
already-known impacted scope count considered and `scope_count` stores scopes
selected for refresh, so `0`/`0` and `N`/`0` remain distinct without another
query or timing row.

Live scrape `1293` validated the compatibility shape and bounded write cost:
the two prior comparable scrapes contained `69` timing rows each, while `1293`
contained `72`, exactly the three new BandMaintenance rows. Their stored tuple
size was about `411` bytes in total. Whole-phase reconciliation left only
`257 ms` (`0.00324%`) outside the timed subphases, a conservative upper bound
for timing-persistence overhead and well below the `1%` acceptance gate.

## Storage and maintenance rules

- Production data, scratch, exports, repacks, and migration artifacts stay on
  the 4 TB FST drive unless the operator explicitly overrides the rule.
- After scrape `1293`, the capacity guard reported about `254.5 GB` free,
  `94%` filesystem use, and `2.11` projected headroom days. Storage recovery
  remains urgent. The report-only retention planner still requires repair and
  exact reclaim adjudication; no retention, rewrite, or gate reduction is
  authorized by the timing acceptance.
- Destructive maintenance requires exact affected objects, parity evidence,
  rollback, live preflight, and a bounded maintenance window.
- Schema initialization is idempotent but is not a substitute for a bounded
  maintenance command.
- Preserve Epic/provider provenance, historical leaderboard correctness,
  publication state, and replay evidence.

## Current procedures

Use the [living runbook index](../operations/runbooks/README.md). Completed
physical cleanup, retirement, compaction, and rejected rollout documents were
removed from the current tree after their current conclusions were captured.
They must not be reintroduced as pending procedures.
