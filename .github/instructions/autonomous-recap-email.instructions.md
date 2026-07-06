---
applyTo: ".github/skills/autonomous-plan-executor/SKILL.md,tools/agent-report-email.mjs,tools/agentReportEmail.mjs,tools/email.mjs,tools/agent-report-email.test.mjs,README.md"
---

# Autonomous recap e-mail instructions

Use these rules for cumulative autonomous agent recap e-mails sent at workflow completion, hard approved-scope blocker, or operator-requested summary resend.

## Subject and title

- Use this subject format: `FST Autonomous Agent: Recap - {Workflow Title} · {Accepted|Mixed|Blocked|Needs Attention}`.
- Keep the branded prefix and status in the inbox subject.
- The body header must omit `FST Autonomous Agent:` and the trailing status, so it reads like `Recap - {Workflow Title}`.
- Open with bullets that explain the full workflow outcome in human terms before any detailed phase/task sections.

## Body structure

- Do not make the recap a single table. Use narrative bullets first and row sections only where compact comparison helps.
- Use these top-level sections when relevant:
  - `Outcome`
  - `Work Completed`
  - `Important Decisions`
  - `Remaining Gates`
  - `Files/Artifacts`
  - `Validation`
  - `Performance`
  - `Commit State`
  - `Next Autonomous Starting Point`
- For each completed/rejected/blocked work item, use human-legible section names and the outcome after a middle dot, for example `Band Read Projection Ownership Probe · Blocked by Approval Gate`.
- Explain why rejected or blocked work is useful if it narrows a failure, protects live safety, or prevents unsafe promotion.
- If Markdown tables are used as source syntax, the e-mail renderer should convert them into human-friendly row sections with a subject sub-header and bold-label bullets for every column.

## Required recap content

- Include accepted, rejected, blocked, skipped-with-evidence, and artifact-only decisions. Do not collapse them into a success-only summary.
- Include the current next autonomous starting point if any approved work remains.
- Include no-commit reasons when commits were intentionally skipped.
- Include the most important artifact paths with a plain-English purpose for each.
- Include validation and performance bullets that explain why the evidence is trustworthy from an operator perspective.
- Keep storage, publication safety, Epic/API constraints, public-read freeze state, and rollback implications visible when relevant.

## Rendering requirements

- Reuse the agent-report e-mail renderer so recap tables render as row sections, not visual tables.
- Recap bullets must remain large enough for quick mobile reading.
- Sub-headers should step down by hierarchy: recap header largest, top-level sections smaller, work-item sections and row-section titles smaller again.
- A recap e-mail may summarize skipped/completed phases, but it must not send separate catch-up phase e-mails for skipped work.
