---
description: "Session memory bridge protocol for agent communication. Ensures context transfers between agents via session memory files. Covers triage, plan negotiation, act logging, and completion."
applyTo: ".github/agents/**"
---

# Session Memory Bridge Protocol

Agents communicate via session memory files. All `runSubagent` calls are stateless — session memory is the only bridge.

## Writing Session Memory — ENCODING RULE

**ALWAYS use the `memory` tool** (command: `create`, `str_replace`, or `insert`) to write to `/memories/session/` files. NEVER use terminal commands (`Set-Content`, `echo`, `cat >`, etc.) — these cause encoding corruption.

If the `memory` tool is unavailable, use `create_file` as a fallback. Never use `run_in_terminal` to write session memory.

## Session Memory Files

| File | Written by | Read by | Purpose |
|---|---|---|---|
| `task-context.md` | Coordinator (triage) | All agents | Issue description, user context, prod data |
| `plan-negotiation.md` | All agents (plan phase) | Coordinator, all agents | Full dev↔design↔test negotiation log |
| `plan-proposal.md` | Coordinator | All agents (act phase) | Approved plan after user confirmation |
| `act-log.md` | All agents (act phase) | Coordinator | Implementation outcomes, test results |

## Triage Protocol (Coordinator writes BEFORE delegating)

Write to `/memories/session/task-context.md`:

```markdown
## Triage: {Brief Title} ({date})
**Symptom**: {What the user reported, verbatim or closely paraphrased}
**Verified data**: {Prod API evidence — what endpoint was checked, what was returned}
**User context**: {Player, instrument, sort mode, FRE state, settings, page}
**Classification**: {frontend | backend | data-flow | cross-repo | visual}
**Owning agent**: {target agent name}
**Relevant files**: {file paths if identified, otherwise "TBD"}
**Attachments**: {Textual description of screenshots — describe values, layout, visual state}
```

## Plan Negotiation Protocol (All agents write during plan phase)

Append to `/memories/session/plan-negotiation.md`:

```markdown
## {Agent Name} — {Plan/Review/Counter-Proposal} ({date})
{Content of the agent's contribution}
```

Each agent appends their research, proposals, reviews, counter-proposals. The coordinator reads the full log and presents it to the user transparently.

## Plan Approval Protocol (Coordinator writes AFTER user approves)

Write to `/memories/session/plan-proposal.md`:

```markdown
## Approved Plan ({date})
{The agreed plan, incorporating user modifications if any}
```

## Act Logging Protocol (All agents write during act phase)

Append to `/memories/session/act-log.md`:

```markdown
## {Agent Name} — {Implementation/Validation/Test} ({date})
{What was done, results, measurements}
```

## Completion Protocol

When work is done:
1. **Update** `/memories/session/act-log.md` with final outcome
2. **Write** persistent findings to `/memories/repo/` using the research persistence protocol
3. If the issue crosses ownership boundaries, note it for the coordinator
