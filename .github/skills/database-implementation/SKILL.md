---
name: database-implementation
description: Database implementation advisor for schema, migrations, repository persistence, indexes, import/export, rollback, tests, and documentation.
---

# Database Implementation Skill

Use this advisor when adding or changing schema, migrations, repository methods, SQL scripts, import/export jobs, indexes, data movement, or runtime database configuration.

Required workflow:

1. Read the relevant `.github/instructions/*` file and component docs before editing DB-adjacent files.
2. Reproduce the issue or write the smallest failing test/invariant when feasible.
3. Keep changes idempotent, backward-compatible, rollback-aware, and aligned with existing repository helpers.
4. Use short lock and statement timeouts for migration/startup DDL; keep optional heavy indexes out of default startup paths unless required.
5. Put destructive changes, table rewrites, pruning, and `VACUUM FULL` behind the FST live-scrape A/B data-parity gate; once the new path is proven to have the same data as the old path, the destructive action is auto-approved with rollback and post-action validation recorded.
6. Preserve scrape IDs, Epic/API timestamps, publication state, and provider provenance when moving, compacting, or rehydrating scrape/replay data.
7. Follow `.github/instructions/documentation.instructions.md` and update
   `docs/architecture/data-storage.md`, the applicable living runbook, and any
   affected publication/configuration/operations page in the same patch.
8. Validate with targeted tests/builds plus DB smoke or parity gates appropriate to the changed surface.

Implementation report template:

| Change | Files | Invariant/test | Migration safety | Runtime impact | Rollback | Docs | Decision |
|---|---|---|---|---|---|---|---|
| `<change>` | `<paths>` | `<test>` | `<locks/timeouts/idempotent>` | `<none/low/high>` | `<path>` | `<updated>` | `<tier>` |
