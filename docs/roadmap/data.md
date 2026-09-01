---
status: roadmap
owner: data
last_verified: 2026-08-30
last_verified_commit: 21d7193c
sources:
  - FSTService/FeatureOptions.cs
  - FSTService/appsettings.json
  - FSTService/Api/SongEndpoints.cs
  - FSTService/Scraping/ScrapeTimePrecomputer.cs
  - deploy/config/fstservice-role.env
  - deploy/config/fstworker-role.env
  - docs/database/SnapshotReuseRunbook.md
  - docs/database/StaleSoloRankIndexRetirementRunbook.md
  - docs/database/ProBassSnapshotRewritePilot.md
  - docs/database/SnapshotGenerationPartitionMigration.md
  - docs/database/SnapshotGenerationRetentionSafety.md
  - docs/database/SnapshotGenerationDropRunbook.md
  - FSTService/Persistence/Maintenance/SnapshotGenerationDropSchema.cs
  - docs/roadmap/post-scrape-processing.md
update_triggers:
  - Publication, snapshot ownership, retention, or analytics readiness changes.
---

# Data and publication readiness

These are verified gaps, not automatic implementation approvals.

| Item | Current evidence | Acceptance gate |
|---|---|---|
| Complete generation-addressable publication bindings | `EnablePublicationReadContext` remains false for both service and worker roles | Every publication-bound surface reports ready; stale/current generation behavior passes contract tests and live-safe validation |
| Finish snapshot/current-state ownership migration | Snapshot reuse is accepted and enabled. Scrape 1304 proved mixed legacy/generation writer routing and publication, but snapshot-overlay readers remain disabled. | Complete reader migration with replay/live parity, rollback, and storage/resource comparison |
| Bound physical snapshot generations | All nine instrument roots are generation-partitioned. The default-off report-only owner retains exact child/config identity, child-scoped roots, immutable cycles/deferrals/evidence, centralized TTL/planner locking, and an independent SQL liveness oracle. Archive-only and the original quarantine/reattach canary are live-accepted on Pro Cymbals `1314`. Q1 operation `1b44941dc5d5ea806dabc2187c3cffed` passed scrape `1335`, publication rotation `159` to `162`, exact cycle `15`, and publication-162 route soak; its initial reattach failed closed with `42P07` and no residue. A first approved DROP attempt then failed before DDL with `42703` on the empty pre-semantic operation schema. After explicit upgrade, DROP operation `333ba4b9fb69dbc098d127f0008ec709` committed under plan digest `fa45ca20c2c975e543b7d539d3b27cb05c5d80ff16345665205f2355eb67d5dc`. The first restore-plan attempt emitted no output and performed no mutation because Python reserialization did not reproduce the authoritative C# plan digest. H3 then failed read-only on a reserved SQL alias. H4 passed that lookup but failed read-only because canonical decimal-string opclass/collation OID arrays were compared directly with integer arrays. Both authorizations remain immutable and unused. H5 applies strict OID-array normalization and needs a third exact-DROP authorization without a schema migration. Solo Bass `1308` remains excluded by unreplayed writer-failure evidence with stable identity `4e3310328261704da558e6d83f99cbc77bc01cef10abbac0840df471d33809cc`. | Commit and review H5/authorizer/source/diff/test evidence; prepare and authorize a new tool-only H5 package while preserving H3/H4 evidence; execute mandatory logical restore under fresh approval; prove exact rows/topology/routes/health and later clean confirmation evidence before promotion. Automatic retirement and sparse-child compaction stay later and disabled |
| Evaluate bounded artifact analytics | DuckDB/Parquet is routed as an artifact-only option, not a production source of truth | Bounded export/replay benchmark that preserves PostgreSQL publication correctness and stays on the FST drive |
| Verify freeze-safe publication cache at a natural publication switch | Service-only promotion is complete. Scrape `1310` advanced publication `103`, preserved persisted reads through one deferred retry, and recorded zero HTTP failures across 309 monitor samples. The scrape evidence still did not attribute first-hit L1/L2 recovery or prove every invalidation-card observation. | At the next bounded cache-specific test, capture pre-publication leakage checks, atomic current/previous cache binding, L1 reset, first-hit L2 attribution, exact route parity, and public health without coupling that evidence to another data candidate |

Detailed post-scrape phase, progress, replay, deployment, A/B, and optimization
work is owned by the
[post-scrape processing roadmap](post-scrape-processing.md).

Completed physical cleanup, compaction, retirement, and rejected stored-rank
rollout documents were removed from the current tree and must not be
reintroduced as pending work.
