---
description: "Visual workflow enforcement for FortniteFestivalWeb. Loaded when editing visual files (TSX, CSS, CSS modules). Enforces the Plan→Confirm→Act workflow with designer validation for all visual/layout changes."
applyTo: "FortniteFestivalWeb/src/**/*.{tsx,css,module.css}"
---

# Visual Change Workflow

All visual, layout, and responsive changes in FortniteFestivalWeb follow the **Plan→Confirm→Act** workflow with mandatory designer validation.

## Plan Phase (no Playwright)

1. **Developer** (web-feat-*) researches the issue, proposes a fix (no implementation)
2. **Designer** (web-design-*) reviews via **code analysis only** — checks viewport coverage, responsive breakpoints, CSS property choices, edge cases
3. Dev↔design iterate until agreement (max 2 round-trips)
4. **Test team** proposes test cases
5. Full negotiation presented to user for approval

## Act Phase (with Playwright)

1. **Developer** implements the approved fix
2. **Designer** validates via **Playwright DOM inspection** using `web-playwright-runner` + `web-state/*` MCP tools to bootstrap browser state
3. Designer inspects across all 6 viewports → BLOCK/ADVISORY/PASS with measurements
4. If BLOCK → developer fixes → designer re-validates (max 3 iterations)
5. **Test team** writes and runs tests

## For Feature Agents (web-feat-*)

After implementing any visual/layout/CSS change:
1. Write what changed to `/memories/session/task-context.md`
2. Report as **"implemented, pending design review"** — never declare complete
3. Include: files changed, CSS/layout values modified, what to verify visually

## For Design Agents (web-design-*)

- **Plan mode**: Review proposals via code analysis only, no Playwright
- **Act mode**: Use `web-state/*` to bootstrap state → inspect DOM with Playwright via runner at all 6 viewports
- Output CSS measurements and property values, NOT code diffs or JSX
- Classify: BLOCK (regression/violation), ADVISORY (improvement), PASS (all clear)

## Key Rules

- **Feature agents cannot self-certify visual changes.** Designer validation is mandatory.
- **Design reviews in Act phase without Playwright measurements are INVALID.**
- **Plan phase has NO Playwright.** Design review is code-analysis-only.
- **Chain depth limit: 3 per phase.** Escalate to user after 3 failed iterations.
