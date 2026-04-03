---
description: "Self-assessment protocol for agent evolution. Loaded whenever agents work in .github/agents/. Covers when agents should update themselves vs escalate to parents vs involve principals."
applyTo: ".github/agents/**"
---

# Agent Self-Assessment Protocol

After completing any plan or execute task, evaluate whether your own `.agent.md` file needs updating.

## Tiered Escalation

| Change scope | Who updates | Principal involved? |
|---|---|---|
| Bug fix, rename, small logic change in your owned files | You update yourself | No |
| New file added to your owned directory | You update yourself (add to ownership) | No |
| New pattern introduced (new hook shape, new component API, new error handling) | Your parent updates you after consulting principal | Yes |
| Cross-agent change (touches 2+ agents' territories) | Parent coordinates affected agents | Principal if new pattern |
| Architectural shift (new state pattern, new dependency, new caching strategy) | Principal updates registry → cascades to children | Yes (principal initiates) |

## Self-Update (you do this yourself)

After completing work, check:
1. **Ownership drift** — Did you create/rename/move files? Update your Ownership section.
2. **New tool needed** — Did you need a tool you don't have? Note it for your parent.
3. **New communication link** — Did you invoke or wish you could invoke an agent not in your `agents` array? Note it for your parent.
4. **Stale constraints** — Are any of your Constraints no longer accurate? Update them.

If any ownership or constraint changes are purely within your scope: edit your own `.agent.md` directly.

## Escalate to Parent (don't self-update)

If the change involves:
- A **new pattern** not in the consistency registry → escalate to parent, who consults principal
- A **new dependency** on another agent → escalate to parent (they update both `agents` arrays)
- **Splitting your responsibilities** → escalate to parent (they may create a sibling agent)

## Escalate to Principal (via parent)

If the change involves:
- An **architectural decision** (new state management approach, new caching strategy, new DB pattern)
- A **consistency violation** (you realize your code doesn't match the registry)
- A **proposed improvement** that would affect multiple sibling agents

Always route principal escalation through your parent head, not directly.
