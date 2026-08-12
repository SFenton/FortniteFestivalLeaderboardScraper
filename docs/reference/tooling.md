---
status: canonical
owner: repository
last_verified: 2026-08-11
last_verified_commit: 2bdf7287
sources:
  - tools/
  - tools/fst-worker-compose-guard.sh
  - tools/fst-worker-compose-guard.test.mjs
  - tools/fst-worker-no-progress-watchdog.mjs
  - deploy/fst-compose.sh
  - FortniteFestivalWeb/package.json
  - .github/skills/
update_triggers:
  - A repository tool, wrapper, generated-artifact command, MCP surface, or agent skill is added, removed, or changes purpose.
---

# Tooling

Use repository tools through their documented wrapper instead of copying
implementation fragments into ad hoc commands.

## Main categories

| Area | Entry points |
|---|---|
| Compose/deployment | `deploy/fst-compose.sh`, `tools/fst-worker-compose-guard.sh`, `tools/scripts/gluetun-manage.sh` |
| PostgreSQL maintenance/evidence | `tools/postgres-*.sh`, `tools/sql/`, living database runbooks |
| Documentation | `tools/check-docs.mjs` |
| Secret/encoding/license checks | `tools/secret-scan.mjs`, `tools/check-encoding.mjs`, `tools/generate-license-manifest.mjs` |
| Autonomous reports | `tools/agent-report-email.mjs` |
| Production MCP adapters | `tools/mcp/` |
| Pak extraction | `tools/FortnitePakExtractor/` |

## Web scripts

`FortniteFestivalWeb/package.json` is the command inventory for unit/shared
tests, coverage, Playwright, linting, manual screenshots/images, embedded
bundle checks, performance capture, icons, licenses, and encoding.

Yarn 4 is authoritative for the web app. Standalone tools such as `tools/mcp/`
may use their own npm lockfile and commands.

## Database and deployment tools

Database scripts are not generic production authorization. Use the matching
runbook and live-safety gates. The worker Compose guard validates the standard
PIA overlay, role flags, aligned proxy arrays, dependencies, and supported data
profiles before a guarded recreate.

### Worker Compose guard

`tools/fst-worker-compose-guard.sh` retains the purposes of `--check`,
`--check-runonce`, `--recreate`, and `--recreate-runonce`, but deliberately
tightens every action to require canonical effective-service membership and
reject effective static PIA endpoint-IP pins. Every action also requires the
guard-only `worker` Compose profile. Continuous actions require
`restart: on-failure:5`; run-once actions require `restart: no`. Its
`--recover-start` action is the continuous production startup handoff after the
production-owned boot orchestrator has started core services and effective
proxies.

Every worker-start/recreate action shares one nonblocking host lock; checks do
not take it. By default the lock is derived as
`<resolved-compose-dir>/.fst-worker-compose-guard.lock`; an explicit absolute
override remains available. All invokers must share the resolved directory or
override and Unix owner.

Recovery is effective-set-only, capped by stage windows and a 1,800-second
default total deadline, and fail-closed. It does not accept `--config-only`,
run-once/data profiles, or any `candidate-*` throughput profile. It does not
recreate core services or non-effective proxies. The guard passes
`--profile worker` for every worker-dependent config resolution and
worker-targeted `up`; proxy-only recreates remain effective-name-only. Output
reports stages and counts without resolved endpoints, IPs, credentials, or
environment values.

If post-start readiness fails, cleanup stops the worker only while
`currentUpdate` remains idle and public reads remain unfrozen. Otherwise it
leaves the worker running and directs the operator to
`tools/fst-worker-no-progress-watchdog.mjs` and the canonical
[live-safety procedure](../operations/live-safety.md).

The action's host-side controls are documented in
[Configuration](configuration.md). Validate changes with:

```bash
bash -n tools/fst-worker-compose-guard.sh
node --test tools/fst-worker-compose-guard.test.mjs
```

## Pak extractor

[`tools/FortnitePakExtractor/README.md`](../../tools/FortnitePakExtractor/README.md)
documents a standalone, Windows-oriented utility that is not part of the main
solution. Treat its paths and external-data prerequisites as tool-specific.

## Agent skills

`.github/skills/` contains repository-specific execution and database advisor
contracts. Skills are operational instructions, not human architecture
documents. When a skill cites a schema, command, path, or deployment topology,
update it with the same source change and keep it linked to the canonical docs.
