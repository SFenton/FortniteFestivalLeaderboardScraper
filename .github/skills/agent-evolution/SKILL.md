---
name: agent-evolution
description: "Evolve agent files as code changes. Use when a parent agent needs to update its children's .agent.md files because ownership boundaries changed, new patterns were discovered, or dependencies shifted. Supports cascading updates down the hierarchy."
argument-hint: "Which agent to update and why"
---

# Agent Evolution

## When to Use

- A principal discovered a new canonical pattern that affects child agents
- Source files moved/renamed, changing ownership boundaries
- A consistency audit revealed stale agent instructions
- A new dependency or communication link is needed
- After creating a new agent that affects sibling relationships
- The auto-evolution hook flagged an agent as potentially stale
- An agent self-assessed and determined it needs parent/principal involvement

## Tiered Escalation Model

Not every change flows to principals. Updates are tiered:

| Scope | Who acts | Principal? | Examples |
|---|---|---|---|
| **Self-update** | Agent updates its own `.agent.md` | No | New file in owned dir, stale constraint, ownership drift |
| **Parent-coordinated** | Parent updates child + siblings | Only if new pattern | New cross-agent dependency, splitting responsibilities |
| **Principal-initiated** | Principal updates registry → cascades | Yes | New canonical pattern, architectural shift, consistency audit |

### Self-Update (agent acts alone)

Any agent can edit its own `.agent.md` when:
- It created/renamed/moved files in its owned directory (update Ownership section)
- A constraint is no longer accurate (update Constraints)
- Its description no longer matches what it does (update `description`)

Self-updates do NOT require parent approval. They appear in git diffs for human review.

### Parent-Coordinated Update

An agent escalates to its parent when:
- It needs a new communication link (`agents` array change on another agent)
- Its responsibilities should be split into a sibling
- A change touches multiple agents' territories

The parent decides whether to consult a principal before editing children.

### Principal-Initiated Cascade

A principal initiates when:
- New canonical pattern added to consistency registry
- Consistency audit reveals widespread drift
- Architecture research found a better approach
- New framework version requires pattern changes

The principal edits children → each child evaluates its own children → cascade down.

## Who Can Update Whom

| Parent | Can fully edit |
|---|---|
| Festival Score Tracker Agent | fst-head, web-head, all 5 principals, all cross-cutting |
| fst-head | fst-scrape-pipeline, fst-api, fst-persistence, fst-auth, fst-rivals, fst-performance, fst-testing |
| web-head | web-components, web-styling, web-state, web-performance, web-features-coord, web-testing |
| web-features-coord | web-feat-rivals, web-feat-shop, web-feat-songs, web-feat-player, web-feat-leaderboards, web-feat-suggestions, web-feat-settings, web-feat-shell |
| fst-principal-architect | All fst-* sub-agents |
| fst-principal-api-designer | fst-api |
| fst-principal-db | fst-persistence |
| web-principal-architect | web-components, web-styling, web-state, web-performance, web-features-coord, web-testing |
| web-principal-designer | web-components, web-styling, all web-feat-*, web-testing |
| **Any agent** | **Itself** (self-update for ownership/constraint drift) |

## Procedure

### 1. Identify What Changed

Read the source files that triggered the evolution. Determine:
- Did ownership boundaries shift? (files moved, renamed, new directories)
- Did canonical patterns change? (new consistency registry entries)
- Did dependencies change? (new imports, new API calls, new DB queries)

### 2. Read the Target Agent

Read `.github/agents/{agent-name}.agent.md` fully.

### 3. Determine Updates Needed

| Change Type | What to update in agent file |
|---|---|
| Files moved/renamed | Ownership section in body |
| New pattern discovered | Add to constraints or approach section |
| New dependency | Add to `agents` array in frontmatter |
| New tool needed | Add to `tools` array in frontmatter |
| Description stale | Update `description` in frontmatter |
| New constraint | Add to Constraints section in body |

### 4. Edit the Agent File

Edit `.github/agents/{agent-name}.agent.md` with the changes.

### 5. Evaluate Cascade

If the updated agent has children:
1. Instruct the agent to evaluate whether its children need corresponding updates
2. The child reads its own updated instructions and compares against each of its children
3. Updates those children that need alignment
4. Cascade continues until leaf nodes (no children)

If the updated agent is a leaf node: cascade stops.

### 6. Update Org Registry

Update `/memories/repo/architecture/org-registry.md` if hierarchy, ownership, or communication links changed.

## Cascade Example

```
fst-principal-architect discovers new error handling pattern
  → updates fst-scrape-pipeline.agent.md constraints
  → fst-scrape-pipeline has no children → STOP

web-principal-architect discovers new state persistence rule
  → updates web-features-coord.agent.md constraints
  → web-features-coord evaluates 8 feature agents
  → updates web-feat-songs (uses localStorage incorrectly)
  → web-feat-songs is a leaf → STOP
  → web-feat-shop already compliant → SKIP
  → ... (evaluate remaining 6)
```

## Anti-Patterns

- DO NOT create circular updates (agent A updates B which updates A)
- DO NOT update agents you don't parent (escalate to the correct parent)
- DO NOT modify agent files in `.github/agents/` that exclude your children
- DO NOT remove communication links without checking if the link is still used
