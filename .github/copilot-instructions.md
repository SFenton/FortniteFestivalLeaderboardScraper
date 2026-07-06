# Workspace Instructions — FortniteFestivalLeaderboardScraper

## Operating mode

- Work autonomously through approved repository work. Do not stop at status reports, completed probes, rejected hypotheses, commits, or priority boundaries when safe follow-up work remains in scope.
- Treat missing diagnostics, stale docs, failed non-destructive validation, slow but safe queries, and incomplete evidence as repairable work. Insert the smallest safe probe/fix and continue.
- Stop only when the remaining action requires operator input, credentials/secrets, sudo or privileged host access, provider/API terms or budget decisions, ambiguous ownership of user changes, or a live-safety/parity gate that cannot be cleared non-interactively.
- Keep the active plan/todo state current. Mark completed work complete, blocked work blocked with the exact hard gate, and newly discovered safe work as a task before reporting it as a next step.
- Commit and push accepted/project-required changes before moving to a new autonomous phase unless the operator explicitly asks not to commit.

## FST live-safety rules

- Production compose ownership is `/home/sfenton/Docker/FestivalServiceTracker`; repo compose files are templates unless the operator says otherwise.
- Scrapes should proceed normally. `fstworker`, `fstservice`, and `festivalweb` may be restarted or taken down for maintenance when useful, but redeploy/recover them as soon as possible to preserve the public user experience.
- Destructive data/reclaim actions are auto-approved after live-scrape A/B testing proves the new path has the same data as the old path. Record the parity evidence, rollback path, and exact affected objects before executing.
- Before broad DB probes, scrapes, deploys, or maintenance, check Docker health, Postgres readiness, public-read freeze state, published scrape, locks/long queries, disk headroom, CPU, and memory.
- All FST database/storage/reclaim work must remain on the 4 TB FST drive. Do not use alternate drives for data, scratch, migration, export, or repack workspace unless SFenton explicitly overrides this rule later.
- Preserve historical leaderboard correctness, Epic/API provenance, publication state, freeze/unfreeze behavior, and replay/parity evidence.

## Tool and evidence rules

- Never claim Playwright or any configured MCP tool is unreliable, unavailable, or cannot run from agents/subprocesses. If a tool call fails, report the actual error.
- Never fabricate measurements. DOM/UI measurements must come from real browser tooling; DB/runtime measurements must come from commands, logs, or database probes.
- Prefer read-only probes before mutations. Use bounded queries and avoid broad scans during live-sensitive windows unless they are part of the maintenance/evaluation work being actively executed.
- Keep secrets out of logs, docs, artifacts, shell history, e-mail reports, and commits.

## Dependency license maintenance

- When adding, removing, or changing any third-party npm, NuGet, or manually bundled package, update the license manifest workflow in code as part of the same change.
- Run `cd FortniteFestivalWeb && npm run licenses:generate` after dependency changes, then run `npm run licenses:check` to verify `FortniteFestivalWeb/src/generated/licenseManifest.ts` is current.
- If package license metadata cannot be inferred from installed metadata, lockfiles, or NuGet cache, add an explicit entry to `tools/license-overrides.json`.

## Autonomous reports

- Use `.github/skills/autonomous-plan-executor/SKILL.md` when the operator asks for autonomous execution or explicitly invokes the skill.
- Phase and recap e-mails use `node tools/agent-report-email.mjs`.
- If SMTP is configured and explicitly enabled, send the report. Otherwise render to `.outbox/fst-autonomous-agent/` and continue; missing e-mail infrastructure is a reporting degradation, not a workflow blocker.

## Key routing

- Database/storage/query/retention/platform work: use `database-management` plus focused database advisor skills.
- Postgres-specific operations: use `postgres-database-expert`.
- DuckDB/Parquet artifact analytics: use `duckdb-analytics-expert`.
- Web/UI work: validate through the smallest relevant web tests and real browser measurements when visual behavior matters.
- API contract changes must keep `FSTService/Api/ApiEndpoints.cs` and `FortniteFestivalWeb/src/api/client.ts` aligned.
