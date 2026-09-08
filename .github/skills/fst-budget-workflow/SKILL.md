---
name: fst-budget-workflow
description: Thin budget-aware FST research and implementation adapter retaining live-safety, publication/parity, matched benchmarks, targeted tests and documentation gates.
---

# FST budget workflow

Use global `budget-workflow` with `.github/agent-budget.json` when installed.
Its CLI lives at `$HOME/.copilot/skills/budget-workflow/scripts/`.
Otherwise follow this bounded workflow directly without external services.

Research uses `evidence/research.mjs plan` and phase-aware `evidencePolicy`.
Local feature/ownership questions are repository-only; computation/storage
alternatives with current architecture are hybrid; unrelated public questions
are neutral external-only. Prefer current-owner `init`/`evidence`, not a reader
agent. Hybrid work records a small opened-source contract before external gaps
and returns for local applicability. Research-phase context never waives live
safety or a specialist's explicit model/approval contract.

1. Read `AGENTS.md`, affected nested rules, and the relevant canonical docs.
2. Local code questions start with exact symbols/ranges. Performance/storage
   requests start with a measured bottleneck and current architecture, not an
   invented optimization. Novel alternatives and consequential changes need
   frontier research.
3. DB work invokes `database-management` and the relevant advisor only:
   probing, evaluation, implementation, improvement, PostgreSQL, or artifact
   analytics. Do not load every advisor or start autonomous-plan-executor.
4. Preserve live health, drive ownership, Epic provenance, freeze/publication,
   historical correctness and rollback. Source-only research does not authorize
   broad production scans or maintenance.
5. Run affected existing .NET/web/tool tests. Benchmark changed computation or
   storage with identical data, cache state, concurrency and resource caps;
   correctness/parity comes before speed. No production data in cheap-model
   prompts or scratch test containers.
6. Revise once on evidence, escalate an unresolved reasoning gap once.
   Update canonical documentation and run `node tools/check-docs.mjs`.
7. Deployment and destructive work retain existing approval, current live-scrape
   A/B, exact-object and restoration gates. Explicit specialist pins are not
   silently replaced.

Full workflow and quality-promotion rules:
[Budget-aware agents](../../../docs/governance/agent-budget.md).
