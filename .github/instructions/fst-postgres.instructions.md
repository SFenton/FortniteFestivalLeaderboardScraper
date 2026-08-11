---
applyTo: "docker-compose.yml,deploy/docker-compose.yml,deploy/postgres.Dockerfile,FSTService/**/*.cs,FSTService.Tests/**/*.cs,tools/postgres-*.sh,docs/database/**/*.md"
---

# FST Postgres documentation instructions

When touching Postgres, schema, persistence, DB initialization, repository-style persistence, compose database files, or database operations tooling:

- Update `docs/architecture/data-storage.md` when schema, storage layout,
  retention, publication, or persistence ownership changes.
- Update the applicable living runbook when operator commands, Docker volumes,
  image tags, environment variables, recovery, validation, or rollback changes.
- If the change affects API read contracts, review
  `FSTService/Api/ApiEndpoints.cs`, the affected
  `FSTService/Api/*Endpoints.cs`, publication contracts/tests,
  `packages/core/src/api/serverTypes.ts`,
  `FortniteFestivalWeb/src/api/client.ts`, and
  `docs/reference/api-contract.md`.
- If the change affects scrape/post-process/rankings data shape, also review
  `docs/architecture/data-publication-flow.md`,
  `docs/components/worker.md`, and the active data roadmap.
- Keep docs explicit about historical leaderboard correctness, published-scrape read safety, public-read freeze/unfreeze behavior, storage retention, locks, long queries, and maintenance windows.
- The active production compose project is `/home/sfenton/Docker/FestivalServiceTracker`; repo compose files are templates unless the operator explicitly says otherwise.
- All FST database/storage/reclaim work must remain on the 4 TB FST drive. Do not use alternate drives for data, scratch, migration, export, or repack workspace unless SFenton explicitly overrides this rule later.
- Follow `.github/instructions/documentation.instructions.md` and report the
  documentation impact before completion.
