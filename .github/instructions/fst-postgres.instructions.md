---
applyTo: "docker-compose.yml,deploy/docker-compose.yml,deploy/postgres.Dockerfile,FSTService/**/*.cs,FSTService.Tests/**/*.cs,tools/postgres-*.sh,docs/database/**/*.md"
---

# FST Postgres documentation instructions

When touching Postgres, schema, persistence, DB initialization, repository-style persistence, compose database files, or database operations tooling:

- Update `docs/database/FSTServiceDatabaseDesign.md` when schema, storage layout, retention, publication, or persistence behavior changes.
- Update `README.md` or relevant runbooks when operator commands, Docker volumes, image tags, environment variables, data ownership, or recovery procedures change.
- If the change affects API read contracts, also review `FSTService/Api/ApiEndpoints.cs` and `FortniteFestivalWeb/src/api/client.ts`.
- If the change affects scrape/post-process/rankings data shape, also review the relevant design documents under `docs/design/`.
- Keep docs explicit about historical leaderboard correctness, published-scrape read safety, public-read freeze/unfreeze behavior, storage retention, locks, long queries, and maintenance windows.
- The active production compose project is `/home/sfenton/Docker/FestivalServiceTracker`; repo compose files are templates unless the operator explicitly says otherwise.
- Temporary use of another drive is allowed only for scratch/migration/repack workspace when approved. Long-term FST data must remain on the FST drive.
