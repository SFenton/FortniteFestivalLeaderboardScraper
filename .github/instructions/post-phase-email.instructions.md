---
applyTo: ".github/skills/autonomous-plan-executor/SKILL.md,tools/agent-report-email.mjs,tools/agentReportEmail.mjs,tools/email.mjs,tools/agent-report-email.test.mjs,README.md"
---

# Autonomous post-phase e-mail instructions

Use these rules for autonomous plan executor phase e-mails and their Markdown inputs.

## Subject and title

- Use this subject format: `FST Autonomous Agent: Phase {n} - {Phase Title} · {Accepted|Rejected|Blocked|Mixed|Needs Attention}`.
- Use one clear body header: `Phase {n} - {Phase Title}`.
- The e-mail subject keeps `FST Autonomous Agent:`, but the body header must omit that prefix.
- Do not use an extra subtitle under the header. Open with bullets that explain the phase goal, what was done, and the practical outcome.

## Body structure

- Do not make the whole e-mail a single table. Use prose bullets as the primary explanation.
- Do not render Markdown tables as visual e-mail tables for post-phase reports. Render each table row as a human-friendly subject sub-header, followed by bullets in the form `<bold>{column}</bold>: {row value}` for every column.
- Write for a human operator first. Use task names such as `Band Read Projection Ownership Probe · Accepted`, not internal ids such as `p1-probe-band-read`.
- For every task, use this section order:
  - task sub-header with a human-legible name and outcome after a middle dot;
  - purpose bullets explaining why the task exists and what success means;
  - optional baseline/desired-outcome row sections;
  - `Outcome` bullets explaining why the task was accepted, rejected, blocked, or mixed;
  - `Files/Artifacts` bullets for every file and artifact path;
  - `Validation` bullets explaining why each validation is good evidence;
  - `Performance` bullets explaining runtime, memory, DB load, storage, or resource-cap observations;
  - `Commit` bullets listing the commit SHA, `none`, or `not committed`, plus the human reason.

## Explanation requirements

- Include enough concrete data for a deeper read without forcing the operator to open artifacts immediately: scrape IDs, row counts, sizes, pass/fail status, coverage, runtime, memory, DB-safety caveats, and artifact paths.
- Explain why rejected or blocked outcomes are useful when they narrow a failure or safely prevent unsafe promotion.
- Keep storage, publication safety, Epic/API constraints, public-read freeze state, and rollback implications visible when relevant.

## Rendering requirements

- Markdown e-mail rendering must preserve nested bullets well enough for file/artifact explanations.
- Sub-headers must visually report up to their parent header: phase title largest, task sections smaller, outcome/files/validation/performance/commit smaller again, and row-section titles smaller than task sections.
- Bullet text must stay large enough for quick mobile reading.
- Tables in post-phase Markdown may remain convenient source syntax, but e-mail HTML should convert them into row sections instead of visual tables.
- Phase e-mails are sent only for phases where new work executed or a decision changed. Skipped completed phases/tasks should be cited in console output but should not create catch-up e-mails.
