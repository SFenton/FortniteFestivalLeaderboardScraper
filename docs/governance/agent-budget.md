---
status: canonical
owner: repository
last_verified: 2026-09-08
last_verified_commit: 2dfb035e
sources:
  - AGENTS.md
  - .github/agent-budget.json
  - .github/agent-tools.json
  - .github/release-machine.json
  - .github/destructive-maintenance-machine.json
  - .github/evals/capability-qualification.json
  - .github/skills/fst-budget-workflow/SKILL.md
  - .github/skills/database-management/SKILL.md
update_triggers:
  - Agent routing, model policy, project gates or shared tooling changes.
---

# Budget-aware agents

The project adapter in `.github/agent-budget.json` selects evidence and
validation, not permissions. Generic routing/evidence/audit/evaluation tooling
belongs to `SFenton/copilot-config`; project safety remains here.

Use `fst-budget-workflow`. The shared installer adds a personal default
instruction that activates `budget-workflow` automatically for substantive
engineering and research; users need not name the skill. It prefers deterministic
commands, exact evidence ranges and one execution owner. HydraFusion may coordinate a well-scoped task;
do not nest a second HydraFusion workflow or implicit tandem underneath it.
Unknown or novel data/performance decisions retain frontier research.
The overall interactive model may be Sol, HydraFusion, or another model.
Identity never bypasses deterministic routing or exact project pins. A matching
qualified current model may fill its resolved role; otherwise it can
orchestrate/read but gains no semantic, repository-apply, database, production,
publication, release, or destructive authority.

`.github/agent-learning.json` adds a default fail-closed completion observer.
It retains only sanitized trace, operation, receipt, validator and all-leg
accounting metadata, mines repeated prompt-scoped cross-session subgraphs, and
may request one repository-local medium-owned incubation because
`automaticBuild` is enabled. `automaticPromotion` remains disabled. It silently
no-ops for one-offs, trivial operations and unstable work.
Public-truth/provenance adjudication, live database access, destructive
execution, restore decisions and release evidence may produce skills or
fixtures but never automatically promoted executors.

`.github/agent-opportunities.json` uses hierarchical version 3 definitions and
keeps every model capability provisional. Deterministic routing, evidence,
registered tools, tests, and receipts run without a model launch. Known-pattern
work uses the opportunity's exact project-qualified medium coordinator:
Claude Sonnet 5 medium/default for routine work and Sol medium/default for
database, concurrency, storage, publication, provenance, debugging, live, and
release-sensitive coordination. External research and binding specification
are conditional. Sol max/long-context appears only behind a named trigger
receipt for unresolved concurrency corruption, publication/provenance conflict,
live data-loss/restore conflict, or release public-health/rollback conflict.
No phase has standing max residency.

The adapter advertises only the implemented focused-test worker class. MAI Code
1.1 Flash may produce one provisional staged formatter-test artifact and one
reviewer-directed revision maximum. The worker gets no semantic, operational,
repository-apply, or live authority. `reviewed-application` requires isolated
pre-validation, medium acceptance, separate operator apply authorization,
identical post-validation, and rollback binding; it has no 30-case floor.
Only `unattended-application` requires >=30 matched held-out cases, >=10
families where used, independent review, confidence, matching terminal
outcomes, zero critical failures, fault-tested rollback, reconciled usage, and
positive complete all-leg savings.
Research, debugging, database/concurrency/performance work, publication,
provenance, live state, release, and destructive operations are never delegated.

Database work still uses `database-management` and relevant focused advisors.
Live authorization gates are unchanged. Cheap-model summaries cannot establish
current production state, historical correctness, publication parity, restore
readiness, or a performance win.

Registered zero-model validation commands live in `.github/agent-tools.json`.
The version 3 application release machine and destructive-maintenance machine
use separate operator authorization, evidence, rollback and cleanup contracts. Both
remain disabled while any required external or production tool is disabled.
Medium review cannot authorize side effects. Failed rollback can open max/long
review only through the machine's project-specific trigger receipt.

Run affected existing tests and matched benchmarks. Every changed documented
area is synchronized and `node tools/check-docs.mjs` remains required.
Instruction deduplication makes `AGENTS.md` canonical; the Copilot root points
to it rather than repeating its safety and command lists.

Shared tools can validate this adapter without running application code:

```bash
node "$HOME/.copilot/skills/budget-workflow/scripts/budget.mjs" validate .
node "$HOME/.copilot/skills/budget-workflow/scripts/budget.mjs" audit .
```

The shared read hook also has a repository registration under `.github/hooks/`.
When invoked it blocks unbounded large source reads, permits targeted reads and
exempts instruction contracts. Runtime discovery/enforcement is not certified
across all CLI launch contexts; it is not a security boundary or hard billing
cap. The deterministic packet command enforces its own byte/range limits.

Unattended model promotion requires at least 30 capability-matched held-out
paired task outcomes, independent review, confidence, matching terminal
outcomes, zero critical safety failures, fault-tested rollback, positive
complete all-leg savings and a noninferiority uncertainty bound. Three-case
studies are provisional and staging-only. Preserve failed/missing cases rather
than dropping them.

The current `fst-web-formatter-tests` capability has three cases and remains
27 cases short of the minimum. Its network-off Docker dependency sandbox is
qualified with read-only Yarn-lock-bound dependencies and isolated Vite caches,
but automatic application remains disabled and no savings are published. The
hierarchical deterministic study budget evaluates complete direct-frontier,
research/spec-to-medium/cheap, medium/cheap/review, and medium-owner-only
topologies. Expected savings remain null because mandatory production legs
lack complete matched evidence, so it authorizes zero model calls.

Rollback: remove this adapter/skill and the short routing pointers. Shared
personal skill/hook removal is independent of the application and never
requires a service restart.

## Evidence-mode research

The shared `evidence/research.mjs plan` is the research entry point; the older
`budget.mjs route` remains conservative broad-task advice. Do not chain both as
successive model gates. `evidencePolicy.always` plus the current phase selects
research context without loading unrelated implementation/release rules.
Applicable mandatory project contracts remain authoritative.

| Mode | Evidence flow |
|---|---|
| Repository | Search the allowed source tree, open complete units/small files, follow only decisive dependencies. |
| External | Neutral context with no repository root; approved public documentation/query IDs only. |
| Hybrid | Bounded local orientation, opened-source contract, external gaps, then local applicability. |

Use `init` and `evidence` with the current owner; they launch no additional
model. Optional `run` launches one isolated read-only research owner and records
actual usage. No automatic range-selector, draft worker, nested HydraFusion or
implicit tandem. High/unknown risk and novel decisions retain frontier reasoning.

The broker exposes full allowed-tree lexical/syntax discovery, not unrestricted
filesystem access. Content hashes invalidate changed evidence. Web discovery
supports approved documentation roots, public GitHub repositories and Crossref
metadata, not unrestricted search, browsers or PDFs. Public query text is
preapproved independently of private source. Missing capability is a reported
gap, not permission to bypass project policy.

Hybrid orientation is capped at eight repository operations or one-third of a
smaller session budget. A contract must cite opened local evidence; external
evidence unlocks further applicability reads. This reserves external headroom,
but the owner must still distinguish a documentation index from evidence for a
technical claim. Runtime/performance claims require the existing matched
benchmarks and live-safety workflow, not source confidence.
