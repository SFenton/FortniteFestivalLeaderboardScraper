# Workspace Instructions — FortniteFestivalLeaderboardScraper

## Tool Availability Rules

- **NEVER** claim Playwright or any configured MCP tool is unreliable, unavailable, or cannot be run from subagents. If a tool call fails, report the actual error — do not fabricate a reason to skip it.
- **NEVER** skip the Plan→Confirm→Act workflow. All implementation requires user approval.
- **NEVER** fabricate measurement data. All DOM measurements must come from actual Playwright tool calls (via `web-playwright-runner` subagent).
- **NEVER** use Playwright in the Plan phase. Design reviews during planning are code-analysis only.

## Plan → Confirm → Act Workflow

ALL implementation requests follow a mandatory two-phase flow:

### Plan Phase (max 3 agent chains, no tools/Playwright)

1. Coordinator gathers user context (player, FRE, settings, filter/sort, page, behavior)
2. Developer researches → proposes fix → designer reviews via code analysis → test team proposes test cases
3. Full negotiation written to `/memories/session/plan-negotiation.md`
4. Coordinator presents ENTIRE negotiation to user transparently
5. **MANDATORY user approval** before proceeding to Act

### Act Phase (max 3 agent chains, tools + Playwright enabled)

1. Developer implements → designer validates with Playwright + `web-state/*` bootstrap → test team runs tests
2. Outcomes written to `/memories/session/act-log.md`
3. Coordinator presents summary to user

### Key Rules

- Feature agents report "implemented, pending design review" — never "complete"
- Design reviews in Act phase without Playwright measurements are INVALID
- Design agents delegate ALL Playwright to `web-playwright-runner`
- `web-state/*` MCP tools generate JS snippets for browser state bootstrap (player, sort, instrument, etc.)
- **Coordinator MUST invoke `runSubagent("web-design-{page}")` for Act chain 2** — math verification, code review, or terminal checks by the coordinator are NOT a substitute for Playwright DOM measurement
- Chain depth limit: 3 per phase. Escalate to user after limit.

## Session Memory

Always use the `memory` tool (create, str_replace, insert) to write to `/memories/session/` files. **NEVER** use terminal commands (`Set-Content`, `echo`, `cat >`) to write session memory — these cause encoding corruption.

### Session Memory Files
- `task-context.md` — Triage context + user context
- `plan-negotiation.md` — Full agent negotiation during plan phase
- `plan-proposal.md` — Approved plan after user confirmation
- `act-log.md` — Implementation outcomes during act phase

## Agent Hierarchy

See `AGENTS.md` for full organization. Key routing:
- Visual issues → Plan→Confirm→Act with designer validation in Act phase
- Known page bugs → direct to `web-feat-{page}` leaf agent via Plan→Act
- Cross-repo features → `fst-head` first (service-first dependency ordering)
- Architecture questions → relevant principal via `runSubagent`
