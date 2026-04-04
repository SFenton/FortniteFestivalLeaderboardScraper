---
name: agent-onboarding
description: "Add a new agent to the organization. Use when the user requests a new specialized agent. Coordinates with heads and principals to determine placement, communication links, hierarchy, and then creates the agent file and updates affected agents."
argument-hint: "What the new agent should specialize in"
---

# Agent Onboarding

## When to Use

- User asks to add a new specialized agent
- A domain has grown enough to warrant splitting an existing agent
- A new cross-cutting concern needs dedicated ownership

## Procedure

### 1. Analyze Domain

Determine: Is this a service agent, web agent, or cross-cutting?

| Signal | Domain |
|---|---|
| Relates to C#, scraping, API endpoints, database | Service (fst-*) |
| Relates to React, pages, components, CSS, hooks | Web (web-*) |
| Relates to both repos or neither | Cross-cutting |

### 2. Consult Head + Principals

Delegate to the relevant head as a subagent:
- "Where does this agent fit in your team? Expand existing, new leaf, or new sub-tree?"

The head will consult its principals:
- Architect: "Does this overlap with existing responsibilities? What patterns should it follow?"
- API Designer / DB Principal / UX Designer (as relevant): "What domain-specific constraints apply?"

### 3. Determine Placement

Apply these rules (head + principals advise):

| Scenario | Action |
|---|---|
| New capability within existing agent's domain | **Expand** existing agent instead |
| Existing agent is too broad (>200 lines, >10 owned files) | **Split** into sibling peers |
| New feature area in web | New `web-feat-*` under web-features-coord |
| New cross-cutting concern | New agent at cross-cutting level |
| New pipeline phase | Expand fst-scrape-pipeline (use /add-scrape-phase skill) |
| Unclear domain | Ask user clarifying questions |

### 4. Determine Communication Links

Using the placement, determine:

| Question | Determines |
|---|---|
| Which principals review this agent's work? | Add to agent's `agents` array |
| Which sibling agents does it coordinate with? | Add bidirectional links |
| Which tools does it need? | Set `tools` array (minimal set) |
| Does it have children? | If yes, add `edit` to tools |

### 5. Create the Agent File

Create `.github/agents/{name}.agent.md`:

```yaml
---
name: "{name}"
description: "Use when {keyword-rich trigger phrases for subagent discovery}"
tools: [{minimal tools}]
agents: [{communication links}]
model: "Claude Haiku 4.5"
user-invocable: false
---
```

**Model tier selection:**
- **Haiku 4.5** (default) — Implementation agents: code gen, testing, mechanical tasks
- **Sonnet 4.6** — Design/analysis agents: vision analysis, design judgment, screenshot interpretation
- **Opus 4.6** — Coordination/research agents: principals, heads, coordinator. Only when deep reasoning is essential.

Body must include:
- Ownership section (files/directories)
- Plan Mode and Execute Mode
- Constraints (including principal consultation rules)
- Cascading Evolution Protocol (if it will have children)

### 6. Update Affected Agents

1. **Parent agent**: Add new agent to its `agents` array
2. **Relevant principals**: Add new agent to their `agents` array (so they can update it)
3. **Sibling agents**: Add bidirectional links if they need to coordinate
4. **Head agent**: Update routing table in body

### 7. Update Org Registry

Update `/memories/repo/architecture/org-registry.md` with:
- New agent in hierarchy tree
- Ownership boundaries
- Communication links

### 8. Trigger Cascade

Instruct the parent to evaluate whether existing children need to know about the new sibling:
- Ownership boundaries may have shifted
- Communication links may need updating

## Checklist

- [ ] Domain determined (service / web / cross-cutting)
- [ ] Head and principal(s) consulted for placement
- [ ] Expand-first evaluated (prefer expanding over splitting)
- [ ] `.agent.md` created with full body (ownership, modes, constraints, evolution)
- [ ] Parent's `agents` array updated
- [ ] Principal(s)' `agents` array updated
- [ ] Sibling bidirectional links added
- [ ] Head's routing table updated
- [ ] Org registry updated
- [ ] Cascade triggered for affected children
