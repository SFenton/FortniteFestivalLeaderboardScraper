---
name: autonomous-plan-executor
description: Explicit autonomous phase/task executor for FST roadmaps, safe self-unblocking, progress reporting, commits, and phase/recap e-mail reports.
---

# Autonomous Plan Executor Skill

Use this skill when the operator explicitly asks for autonomous execution, names `autonomous-plan-executor`, provides an approved multi-phase plan, or asks the agent to keep iterating until completion, a stop counter, or a hard blocker.

This skill is an execution orchestrator. It does not replace focused repository skills; it owns plan parsing, task ordering, stop counters, self-unblocking, progress cadence, commit/push boundaries, phase reports, and final recap reports while applying the focused skill for the current domain.

Once invoked, run with day-trader-style persistence: continue through every approved phase/task/priority in order, insert safe derivative work as it is discovered, commit and push accepted progress, and do not stop at reports, completed probes, rejected hypotheses, commits, maintenance restarts, deployments, or parity-gated destructive actions. FST live-safety gates block only the exact unsafe/destructive action until the live-scrape A/B data-parity gate is met; they do not end the autonomous queue while safe code, docs, tests, probes, manifests, parity checks, feasibility packages, maintenance, deploy, scrape, or readiness work remains.

## Input contract

Accept any combination of:

1. A freeform prompt with goals, constraints, artifacts, success criteria, and stop rules.
2. One or more Markdown files containing priorities, phases, tasks, checklists, commands, benchmarks, or acceptance gates.
3. Existing session todo state, checkpoint notes, eval artifacts, benchmark reports, issue/PR comments, or operator follow-up notes that refine the active plan.

When Markdown files are provided, read them before editing. Convert headings into phases, checkboxes or ordered steps into tasks, tables into acceptance/evidence requirements, and next-step sections into ordered task inserts. Preserve author order unless FST live-safety gates require a prerequisite probe.

## FST domain routing

Before executing each phase or task, classify the domain and apply the matching focused guidance:

| Work type | Required routing |
|---|---|
| Database/storage/query/retention/platform work | `database-management` plus focused DB advisor skills |
| Postgres-specific schema, query, index, vacuum, lock, backup/restore work | `postgres-database-expert` |
| DuckDB/Parquet/export artifact analysis | `duckdb-analytics-expert` |
| FSTService API/scraper/background worker work | `FSTService/AGENTS.md`, relevant `FSTService/**/*.cs`, and targeted service tests |
| Web/API client/UI work | `FortniteFestivalWeb` scripts and real browser measurements when visual behavior matters |
| Dependency changes | License-manifest instructions and `FortniteFestivalWeb` license scripts |
| Autonomous phase and recap reporting | `.github/instructions/post-phase-email.instructions.md` and `.github/instructions/autonomous-recap-email.instructions.md` |

Repository rules override general plan text. Preserve historical leaderboard correctness, Epic/API provenance, provider constraints, public-read publication state, Docker resource safety, README/docs accuracy, and dependency-license maintenance even if the input plan omits them.

## Maintenance and scrape-continuity requirement

- Scrapes should proceed normally while autonomous work continues. Do not keep `fstworker` stopped as a default safety posture when normal scraping is expected.
- `fstworker`, `fstservice`, and `festivalweb` may be restarted, redeployed, or temporarily taken down for maintenance when useful. Keep downtime as short as practical, redeploy/recover as soon as possible, and verify worker/service/web health immediately afterward.
- Backend/database work should preserve the user experience by using prompt redeploys, publication gates, rollback-safe changes, and clear monitoring rather than by avoiding all downtime.
- Destructive reclaim, index/table drop, table rewrite/repack, active data movement, or irreversible publication-state changes are auto-approved after live-scrape A/B testing proves the new path has the same data as the old path. Until that parity gate passes, continue all safe non-interactive work around it: code/test work, deploy prep, bounded probes, fixture or artifact benchmarks, parity tooling, manifests, rollback plans, parity packages, operational monitors, documentation, and commit/report updates.

## Required execution loop

1. **Parse and normalize the plan.** Build a working phase/task list with explicit acceptance gates, evidence requirements, validation commands, performance targets, and known blockers. If a task lacks measurable gates, infer them from repo docs and the prompt before starting.
2. **Run prerequisites first.** Check current branch/worktree, relevant instruction files, live-safety risk, Docker health/resource pressure when runtime is affected, and existing artifacts. Do not overwrite unrelated user changes.
3. **Skip already completed work.** When the plan/checkpoint/Markdown marks work complete, accepted, rejected with evidence, or hard-blocked with no safe alternative, treat it as processed and advance unless the operator explicitly asks for a refresh or fresh evidence proves the artifact stale/contradicted. Skipped completed work should not generate catch-up e-mails.
4. **Execute phases in order.** Do not start a later phase until every task in the current phase is accepted, rejected with evidence, skipped as already processed, or blocked by a hard safety/credential/approval gate with no safe alternative.
5. **Execute tasks in order.** A task is not complete until implementation, docs, tests/evals/benchmarks, performance checks, output validation, cleanup, and commit handling required by that task are complete.
6. **Diagnose before changing.** For failures, regressions, or missing evidence, identify the root cause and choose the next smallest safe hypothesis before editing again.
7. **Iterate autonomously.** A report, failed command, rejected A/B, completed smoke, commit/revert, parity package, or phase boundary is not a stopping point. Continue into the next safe repair, A/B, benchmark, proof package, implementation task, or readiness task until stop criteria are reached.
8. **Insert discovered work into the active plan.** When a task reveals prerequisite work, residual risk, missing data, performance bottlenecks, validation gaps, or a parity-gated action, insert new work at the earliest safe point: immediately before dependent work if it blocks correctness, later in the same phase if it strengthens evidence, or into a future phase if it is promotion/readiness work. Do not leave actionable safe work as narrative-only "next steps."
9. **Unblock repairable blockers autonomously.** Treat missing diagnostics, stale manifests, data-quality gaps, slow-but-safe query paths, absent coverage, and failed non-destructive validation as work items, not hard blockers.
10. **Run residual-blocker sweeps.** Before reports, phase completion, finalization, and whenever an inserted-work queue appears empty, classify rejected/blocked/caveated decisions, insert safe derivative work, and execute it before claiming no useful work remains.
11. **Convert next steps into tasks before reporting.** Any actionable safe next step named in a report must already exist in todo/plan state with an accepted, rejected, done, in-progress, or hard-blocked decision.
12. **Keep phase completion strict.** Do not mark a phase complete while it has open inserted tasks, unvalidated outputs, pending commits/reverts, unresolved next steps, or unclassified rejected work.
13. **Commit and push accepted work before starting new work.** Commit accepted/project-required file changes before moving to the next autonomous task/phase. Revert rejected experiments that should not remain. Do not commit artifacts, secrets, noisy logs, or unrelated user changes.
14. **Triage dirty files at every task boundary.** Classify dirty/untracked paths as accepted work to commit, project-required supporting work, rejected experiment to revert, generated artifact/log to leave out, or unrelated user work to preserve/ask about.
15. **Push before continuing.** After each accepted commit, push and verify success before starting the next risky autonomous task. If push fails, record the commit SHA and block continuation when persistence risk matters.
16. **Finish with no leftover approved work.** The final report must not hand back safe in-scope next steps for the operator. It may list rejected or hard-blocked scope only after exhausting safe non-interactive alternatives, inserting/processing every actionable follow-up within approved scope, and committing/pushing accepted changes. Fail the final report if any actionable safe follow-up lacks a processed task.

## Autonomous blocker triage

| Class | Meaning | Required autonomous action |
|---|---|---|
| Repairable now | Existing repo tooling can safely repair or narrow the issue without credentials, destructive changes, privileged access, or approval-only maintenance. | Insert and execute the smallest safe repair/probe/A/B immediately. |
| Diagnosable rejection | A benchmark, eval, or implementation attempt is rejected, but evidence isolates a divergence, regression, bottleneck, or weak slice. | Insert a root-cause repair task before dependent promotion/default work. |
| Evidence generation | Progress is blocked by missing manifests, parity evidence, coverage, fixtures, artifacts, or benchmark baselines. | Insert safe artifact generation, read-only probes, bounded backfills, or evaluation tasks. |
| Time/data accrual | The gate needs future live scrape observations, future Epic/API responses, or non-overlapping windows that cannot be fabricated now. | Build or verify safe accrual machinery, persistence, monitors, and readiness reports; block only the final claim. |
| Provider or approval gate | Remaining work requires credentials, provider/API terms, budget, privileged host access, or explicit operator approval outside the standing parity-gated destructive-action approval. | Do not perform the externally gated action. Insert and execute all safe feasibility/proof/readiness work around it, then mark only the exact gated action blocked. Destructive data/reclaim work is not externally gated after live-scrape A/B data parity passes. |
| Rejected with no safe hypothesis | All safe non-interactive repair paths have been attempted or rejected and no useful narrowing remains. | Record attempts, evidence, and why additional work would violate safety/approval boundaries or repeat exhausted hypotheses. |

End-of-queue rule: before declaring the queue complete, sweep all rejected, blocked, and caveated decisions. Prioritize new safe work in this order: correctness/parity, live/public-read safety, data coverage, storage/retention feasibility, performance/resource safety, operational monitoring, documentation/reporting. "Not approved for production mutation/scrape" does not mean "no more work"; continue with readiness work that is safe and useful.

Parity gate rule: if destructive reclaim, index/table drop, rewrite/repack, active data movement, or irreversible publication-state change is pending, build and run the live-scrape A/B proof first. When the new path is confirmed to have the same data as the old path, the destructive action is auto-approved; execute it with recorded rollback, object list, monitoring, and post-action parity checks. If parity fails, keep processing safe alternatives such as rollback SQL drafts, manifest/checksum tooling, endpoint parity tests, fixture benchmarks, bounded EXPLAIN/probe packages, monitoring scripts, docs/runbooks, deployments, and normal scrape readiness.

## Stop counters

Use the explicit stop rule from the prompt or plan when present. Otherwise:

| Work category | Consecutive stop counter |
|---|---:|
| Production database/storage or scrape-pipeline optimization | 15 no-improvement or regression iterations |
| General implementation/debug loops | 10 failed iterations |
| Code-only no-forward-momentum loops | 5 no-forward-momentum iterations |
| Focused skill with a stricter counter | The focused skill's stricter counter |

Accepted improvements reset the relevant counter. Rejected hypotheses do not stop the workflow if they produce a new diagnosis or safe next hypothesis. Count only consecutive iterations with no accepted improvement, no new evidence, no useful narrowing, and no validated plan progress.

## Performance and live-safety gates

1. Read Docker caps and runtime defaults before heavy work. Production runs under `/home/sfenton/Docker/FestivalServiceTracker`; repo compose files are templates unless the operator says otherwise.
2. Before broad evals, scrapes, backfills, DB scans, deploys, or service changes, run live-safety probes: `docker compose ps`, service `/readyz`, Postgres readiness, locks/long queries, `docker stats --no-stream`, disk headroom, public-read freeze state, and published scrape.
3. Scrapes may proceed normally, and `fstworker`, `fstservice`, and `festivalweb` may be restarted or temporarily taken down for maintenance. Redeploy/recover them as soon as possible, then verify worker state, `/readyz`, `festivalweb` health, and representative public routes.
4. Destructive data/reclaim actions are auto-approved after live-scrape A/B testing confirms the new path has the same data as the old path. Before execution, record exact objects/actions, old-vs-new parity evidence, rollback, disk/resource risk, and post-action validation.
5. When a destructive DDL/reclaim action has not yet met the live-scrape A/B parity gate, continue with smaller safe substitutes that answer the same question as far as possible: fixture tests, code review, generated rollback DDL, representative read-only query plans, manifest generation, API parity probes, storage math, and approval-package drafts.
6. Define the real-time or throughput target before accepting performance work. Include wall clock, p50/p95/p99 or phase latency, CPU, memory, WAL, temp bytes, disk read/write, lock waits, and artifact sizes where relevant.
7. Correctness and publication parity outrank speed. Do not accept a performance win that changes API output, breaks historical correctness, bypasses provider constraints, or weakens public-read safety.

## Progress, console, and e-mail reporting

1. Keep the terminal visibly updated every 60 seconds while autonomous workflow work is executing. For long commands, report current phase/task, elapsed time, command, artifact paths, resource readings, and blocker/ETA state.
2. After each newly executed task completes, print a status table covering what changed, validation, performance before/after, accepted/rejected/blocked decision, stop counter, commit/push or revert status, and inserted follow-up tasks.
3. At the end of each phase where new work executed or a decision changed, print a detailed phase report and send/render the same report by e-mail using `node tools/agent-report-email.mjs`. Follow `.github/instructions/post-phase-email.instructions.md`.
4. E-mail reports must include accepted tasks, rejected tasks, blockers, commits, artifacts, commands, validation results, performance tables, and the next task that will be executed or the hard gate that blocks continuation.
5. If SMTP is unavailable, render the phase e-mail to `.outbox/fst-autonomous-agent/` and continue. Missing e-mail infrastructure is a reporting degradation, not a workflow blocker.
6. At workflow completion or a hard approved-scope blocker, send/render a cumulative recap and follow `.github/instructions/autonomous-recap-email.instructions.md`.

## Tooling and non-interactive constraints

1. Prefer project scripts and user-space installs. Add dependencies only when necessary for the task and update license manifests when dependency manifests change.
2. Never prompt for `sudo`. Find a non-privileged path: existing Docker service, user-local install, `npx`/`npm exec` where available, session artifact, or reduced benchmark that answers the same question.
3. Do not wait for credentials or privileged host access. Do not wait for separate destructive DB approval once live-scrape A/B data parity has passed; execute the destructive action under the recorded parity/rollback/monitoring gate. If another hard boundary blocks a task, mark only that scope blocked with evidence, then process all safe alternatives and dependent plan updates. Missing diagnostics, parity coverage, manifests, benchmarks, docs, and non-destructive validation are not external-access boundaries; repair them autonomously with existing repo tooling.
4. Keep secrets out of commands, logs, reports, artifacts, commits, and e-mail bodies.

## Output templates

Task completion status:

| Phase | Task | Result | Change/evidence | Correctness gate | Performance gate | Tests/evals | Commit | Inserted work |
|---|---|---|---|---|---|---|---|---|
| `<phase>` | `<task>` | `<accepted/rejected/blocked/iterating>` | `<files/artifacts>` | `<passed/failed>` | `<before -> after>` | `<commands>` | `<sha pushed/reverted/blocked>` | `<none or task ids>` |

Phase report:

| Task | Decision | Reason | Files/artifacts | Validation | Performance | Commit |
|---|---|---|---|---|---|---|
| `<task>` | `<accepted/rejected/blocked>` | `<why>` | `<paths>` | `<tests/evals>` | `<metrics>` | `<sha pushed/reverted/blocked>` |

Final report:

| Phase | Accepted | Rejected | Blocked | Commits | Key evidence | Final state |
|---|---:|---:|---:|---|---|---|
| `<phase>` | `<n>` | `<n>` | `<n>` | `<shas>` | `<artifacts>` | `<complete/no approved work remains>` |

Residual-blocker sweep:

| Residual issue | Class | Safe derivative work | Inserted task id | Decision | Remaining hard gate |
|---|---|---|---|---|---|
| `<issue>` | `<triage class>` | `<repair/probe/accrual/feasibility>` | `<id or none>` | `<accepted/rejected/blocked/done>` | `<none or gate>` |
