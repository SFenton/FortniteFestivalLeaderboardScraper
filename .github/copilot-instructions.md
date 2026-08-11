# Workspace Instructions - FortniteFestivalLeaderboardScraper

## Operating mode

- Continue through safe approved work; do not stop at a probe, report, commit,
  or rejected hypothesis while in-scope work remains.
- Stop only for operator input, credentials, privileged access,
  provider/budget decisions, ambiguous user changes, or an uncleared
  live-safety/parity gate.
- Prefer read-only evidence before mutation and never fabricate measurements.
- Preserve unrelated worktree changes and keep secrets out of output,
  artifacts, history, and commits.
- Commit and push accepted/project-required changes unless instructed
  otherwise.

## Production safety

- Live Compose ownership:
  `/home/sfenton/Docker/FestivalServiceTracker`.
- Repository Compose files are templates.
- Before broad live work, check Docker health, PostgreSQL readiness,
  freeze/publication state, locks/long queries, disk headroom, CPU, and memory.
- All FST storage/scratch/export/repack work stays on the 4 TB FST drive unless
  explicitly overridden.
- Preserve historical correctness, Epic provenance, publication, recovery, and
  parity evidence.

Canonical details: `docs/operations/live-safety.md`.

## Documentation

Apply `.github/instructions/documentation.instructions.md` to every change.
Update canonical docs in the same change, create/index new pages for new
documentable areas, and report documentation impact explicitly.

Run `node tools/check-docs.mjs`.

## Key routing

- Documentation index: `docs/README.md`
- System architecture: `docs/architecture/`
- Web/service/worker/shared code: `docs/components/`
- API/configuration/flags/CLI: `docs/reference/`
- Deployment/VPN/live safety: `docs/operations/`
- Database/storage/query/retention work: `database-management` and focused DB
  skills
- PostgreSQL operations: `postgres-database-expert`
- Artifact-only DuckDB/Parquet analytics: `duckdb-analytics-expert`
- Autonomous plans/reports: `.github/skills/autonomous-plan-executor/SKILL.md`

API changes must review `FSTService/Api/ApiEndpoints.cs`, affected
`FSTService/Api/*Endpoints.cs`, publication contracts/tests,
`packages/core/src/api/serverTypes.ts`,
`FortniteFestivalWeb/src/api/client.ts`, and the API documentation.
