---
status: canonical
owner: repository
last_verified: 2026-09-08
last_verified_commit: 2dfb035e
sources:
  - AGENTS.md
  - .github/agent-budget.json
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

Database work still uses `database-management` and relevant focused advisors.
Existing autonomous-plan model pins and live authorization gates are unchanged.
Cheap-model summaries cannot establish current production state, historical
correctness, publication parity, restore readiness, or a performance win.

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

Model promotion requires held-out paired task outcomes, independently reviewed
research, zero critical safety failures, complete cost accounting and a
noninferiority uncertainty bound. A small smoke benchmark is not promotion
evidence. Preserve failed/missing cases rather than dropping them.

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
