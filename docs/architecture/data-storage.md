---
status: canonical
owner: data
last_verified: 2026-08-14
last_verified_commit: eb593898
sources:
  - FSTService/Persistence/DatabaseInitializer.cs
  - FSTService/Persistence/MetaDatabase.cs
  - FSTService/Persistence/InstrumentDatabase.cs
  - FSTService/Persistence/GlobalLeaderboardPersistence.cs
  - FSTService/Persistence/MaxScoreMaintenanceSchema.cs
  - FSTService/Persistence/MaxScoreMaintenanceService.cs
  - FSTService/Persistence/MaxScoreMaintenanceNotificationService.cs
  - FSTService/Persistence/RegistrationMutationGuard.cs
  - FSTService/Persistence/MetaDatabase.PhaseProgress.cs
  - FSTService/Persistence/Maintenance/DatabaseMaintenanceDryRunReporter.cs
  - FSTService/Persistence/Maintenance/DatabaseRetentionMaintenanceService.cs
  - FSTService/Scraping/BackfillOrchestrator.cs
  - FSTService/Scraping/RegistrationMutationCoordinator.cs
  - FSTService/Scraping/RegisteredBandProcessing.cs
  - FSTService/Scraping/RegisteredPlayerBandDiscovery.cs
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
| Operations/audit | Worker heartbeat, terminal scrape-phase outcomes, detailed subphase timings, max-score checkpoints/rollback evidence, maintenance notification quarantine, dedup/recovery audit state |

The `songs` path-generation state stores distinct theoretical maxima for all
eight path instruments. Plastic drums use separate
`max_pro_cymbals_score` and `max_pro_drums_score` columns because cymbal-mode
gems can score differently from the no-cymbal mode even though both originate
from Epic's single `pd` chart. Schema initialization adds these nullable
columns idempotently and includes them in the atomic path-metadata write guard.

The exact relation inventory is intentionally source-driven because it changes
frequently. `DatabaseInitializer` and its tests are the schema inventory;
canonical documentation describes ownership and invariants instead of copying
volatile table counts.

### Max-score maintenance evidence

`max_score_maintenance_runs` owns the digest-bound workflow checkpoint:
manifest/plan identities, exact publication/catalog, score-source,
notification-state and rank-history fingerprints, freeze owner, last durable
phase, rollback file digest, notification audit link, counters, and bounded
failure detail. A post-freeze failure changes status to `failed` but does not
clear its phase or freeze.

`max_score_maintenance_rollback_songs` stores every pre-promotion path field
and all eight maxima for every manifest song. It complements the canonical
same-drive rollback JSON. Database triggers reject workflow-identity changes
and rollback-row updates/deletes; neither surface deletes historical
generations. Rollback JSON timestamps use the immutable run `created_at`, so a
file-first/database-checkpoint retry validates identical canonical bytes.

`improvement_notification_maintenance_runs` and
`improvement_notification_maintenance_candidates` retain historical
`maintenance_pro_lead_max_score_repair_v1` rows and accept new
`maintenance_max_score_correction_v1` audit rows. Both purposes remain
quarantine-only with a compile/schema-enforced visible delivery count of zero.
Maintenance candidate parity uses routine visible cardinality rather than raw
audit-row cardinality: player ranks coalesce by player/instrument, band-song
metrics coalesce by play, band rank metrics group by subject/scope, progress
metrics remain individual, and `band_rank_state_missing` is audit/alignment
only. Max-score-percent rank changes likewise remain in quarantine and state
alignment. Missing current band subjects and their song/rank state are created
inside the same repeatable-read quarantine transaction before candidate
collection.

Registration, backfill, registered-user refresh, score-history reconstruction,
tracked-band discovery/processing, selected-profile activity, and stale
registration pruning share one PostgreSQL session advisory mutation gate.
Each complete external async mutation lifecycle holds the shared form on a
dedicated unpooled, non-multiplexed session before its first write; gate
holders and waiters therefore do not consume the normal 15-connection service
pool. The lease owns no transaction or publication-row lock, so
`idle_in_transaction_session_timeout` cannot expire it while Epic/history work
uses ordinary pooled connections. Background/manual workers may wait with
cancellation on the isolated session. HTTP tracking, manual backfill, and band
sync use a pool-capacity-bounded `pg_try_advisory_lock_shared` admission and
return `503`/`Retry-After: 30` instead of queueing behind maintenance.
Cancellation or disposal explicitly unlocks and physically closes the
isolated session.

Max-score plan/apply takes the exclusive form of the same gate first. It waits
for every admitted shared lifecycle to finish and prevents later registration
work from starting. The lock-owning unpooled session immediately records an
opaque random owner token, publication ID, backend PID/start, and acquisition
time in `scrape_publication_state`; this durable fence also blocks HTTP before
a public-read freeze exists. Apply then takes the path-generation and
publication advisory locks, establishes or revalidates its digest-owned
freeze, and only then opens the source-lock transaction. That transaction
takes `SHARE` locks in fixed order on `leaderboard_entries_overlay`,
`leaderboard_entries`, and `band_member_stats`. This removes
publication-row/source-table lock inversion while protecting solo score
identity and the member source used by band threshold/projection rebuilds.

Row triggers remain a fail-closed second line on registered users/bands,
registered-user refresh progress, registered-band status/progress,
registered-player band-discovery progress, backfill status/progress, and
history-reconstruction status/progress. Statement triggers apply the same
fence to `leaderboard_entries`, `leaderboard_entries_overlay`, and
`score_history`, plus the locked band source `band_member_stats`. Each trigger
locks the publication singleton only for its bounded write transaction and
rejects either the active durable mutation-gate token or a
`max-score-maintenance:v1:<digest>` freeze. This row lock orders a surviving
write from a lost shared session before a newly claimed exclusive gate,
without taking a second advisory lock on a pooled connection.

Both lease types set an independent random session token and capture the
backend PID. Every guarded phase verifies the token, backend, expected
advisory locks, and durable gate state; exclusive phases additionally verify
the owner token and, after acquisition, all three source table locks
immediately before mutation. `AsyncLocal` carries only the current lease
reference for cache routing; it is not accepted as lock proof. Shared-gate
acquisition synchronously invalidates cached path maxima and refreshes scraper
instrument support before lookup-bearing backfill/history/band work;
metadata-only HTTP activity and pruning take the same gate without that extra
cache churn. The maintenance owner has one transaction-local owner-token
bypass used only to remove stale negative backfill checks and matching successful
`history_recon_progress` rows for newly promoted path-backed pairs. Matching
history status rows move to `pending`, clear the completion timestamp,
recompute `songs_processed` from preserved rows, clear aggregate season/entry
counters, advance their admission revision, and fence preserved unrelated
progress to that revision.
Only affected accounts are requeued; positive backfill checks and unrelated
history pairs are preserved. Ordinary scrape/publication freezes retain their
existing registration behavior.

Final cache swap, completed checkpoint, durable-gate clear, and unfreeze run
inside the source-lock transaction on the live lock-owning session. If that
backend is terminated or the token/locks disappear, phase/final mutations
fail closed: the cache is not published, the durable freeze and gate owner
remain, and resume must acquire and validate a new lease before it can replace
the stale owner token and continue.

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

## Durable phase-attempt ledger

`scrape_phase_attempts` complements rather than replaces
`scrape_phase_outcomes`, `scrape_phase_timings`, and
`service_worker_status.current_operation_json`.

Its primary key is `(scrape_id, phase_id, attempt)`. Typed columns retain the
stable operation/phase IDs, ordinal and plan version, worker instance,
subphase, terminal/running status, units and denominator-final flag, exact
phase percent, conservative overall/ETA fields, start/progress/heartbeat/end
timestamps, safe build/config hashes, and warning/error text. It intentionally
has no foreign key so startup is additive and rollback does not couple scrape
history deletion to telemetry. An FK and explicit row-retention lifecycle are
an L3 follow-up requiring measured growth, scrape-log retention, delete-lock,
and rollback evidence; they are not part of PR #15.

Indexes follow the actual paths:

- active service-info/watchdog lookup by scrape, `last_progress_at`, ordinal,
  and attempt;
- orphan interruption by running worker instance;
- successful same-plan/config history for ETA sampling.

The current row is updated rather than appended for every progress tick.
Expected writes are one start and terminal update per phase, subphase
transitions, a maximum one meaningful progress update per five seconds, and
one heartbeat-only update per worker heartbeat interval. Progress updates use
the greater of the stored and observed progress timestamps, so a backwards
clock step cannot regress `last_progress_at` or violate its start-time check.

Accepted scrape `1296` produced 24 attempt rows across 22 phase IDs, 2,068
updates, and a 212,992-byte relation (106,496-byte heap and 65,536-byte
indexes). It ended with zero running, interrupted, cancelled, orphaned, or
null-completion rows. The matched wall-clock upper bound for all PR-2 overhead
was `0.0696%`; summed terminal phase outcomes differed by `0.736%`.

## Snapshot retention planning evidence

`DatabaseMaintenanceDryRunReporter` estimates partition-local keep-only
rewrites from bounded PostgreSQL catalogs and `pg_stats`; report-only planning
does not scan snapshot partitions.

The estimator is fail-closed:

- retained rows include both policy `Keep` and `Blocked` snapshot IDs;
- active, current-projection-source, and rollback-protected IDs are present in
  every partition plan even when absent from `most_common_vals`;
- positive `n_distinct`, negative fraction-of-row `n_distinct`, zero/unknown
  values, MCV/frequency length, frequency remainder, and the drift between
  `n_live_tup` and `reltuples` all contribute to statistics safety;
- MCV row/byte estimates plus an explicit unknown remainder reconcile to one
  conservative row total and the relation's total bytes. Floor-rounding
  residual is retained, never purged;
- complete statistics allow at most `max(1, MCV count)` residual rows and
  `max(4096 bytes, MCV count)` residual bytes from floor rounding, and require
  nonzero `n_live_tup` versus `reltuples` drift to stay within `10%`;
- if protected estimates are missing or statistics are partial, stale, or
  inconsistent, executable purge rows/bytes are zero, retained/workspace
  estimates become the full partition, and `CanExecute=false`;
- informational candidate-purge estimates remain separate from executable
  estimates.

A truly empty partition may report zero retained rows, but it is not a rewrite
candidate. Exact row scans remain confined to the separately guarded execution
preflight and are never introduced into report-only planning.

The live read-only candidate on publication `1293` completed in `94 ms`
without locks or public degradation. All nine partitions contained protected
IDs `1293` and `1291` that were absent from MCV statistics, so every plan
failed closed. Estimated rows and bytes reconciled, but conservative retained
workspace became the full `2,607,232,278,528` bytes; executable purge
rows/bytes were zero. The separately labeled informational candidate was about
`2.52` billion rows / `1.46 TB`, with about `392 GB` outside MCV coverage.
Those values are not reclaim proof.

## Storage and maintenance rules

- Production data, scratch, exports, repacks, and migration artifacts stay on
  the 4 TB FST drive unless the operator explicitly overrides the rule.
- The current read-only retention observation reported about `258.8 GB` free,
  `94%` filesystem use, and `2.14` projected headroom days. Storage recovery
  remains urgent. Trustworthy report-only planning does not authorize
  retention, rewrite, or reduction of the `500 GiB` free-space gate.
- Destructive maintenance requires exact affected objects, parity evidence,
  rollback, live preflight, and a bounded maintenance window.
- Current-publication max-score correction requires the canonical manifest and
  plan digests, the path-generation/publication lock order, a durable
  maintenance freeze, complete rollback coverage, and atomic cache
  swap/unfreeze. Use the
  [max-score correction runbook](../database/MaxScoreCorrectionMaintenanceRunbook.md).
- Schema initialization is idempotent but is not a substitute for a bounded
  maintenance command.
- Preserve Epic/provider provenance, historical leaderboard correctness,
  publication state, and replay evidence.

## Current procedures

Use the [living runbook index](../operations/runbooks/README.md). Completed
physical cleanup, retirement, compaction, and rejected rollout documents were
removed from the current tree after their current conclusions were captured.
They must not be reintroduced as pending procedures.
