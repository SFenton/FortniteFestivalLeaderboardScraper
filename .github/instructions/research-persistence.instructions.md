---
description: "Research persistence protocol for all agents. Mandates writing diagnostic findings and data flow discoveries to repo memory after investigation. Prevents knowledge loss across conversations."
applyTo: ".github/agents/**"
---

# Research Persistence Protocol

After completing any diagnostic investigation, you MUST persist your findings before finishing.

## What to Persist

### Diagnostic Findings → `/memories/repo/{area}-diagnostics.md`

Write a structured entry for every bug investigation:

```markdown
## {Bug Title} ({date})
- **Symptom**: {What the user saw}
- **Root cause**: {Why it happened}
- **Fix**: {What was changed and where}
- **Verified**: {How the fix was confirmed — test results, prod data, manual check}
> Lesson: {One-sentence takeaway for future investigations}
```

Area names: `web`, `fst`, `api-contract`, `scraping`, `persistence`, `auth`, `rivals`

### Session Context → `/memories/session/task-context.md`

Keep this file updated throughout investigation so:
- If the conversation continues, you don't re-discover what you already found
- If you escalate via handoff, the receiving agent has full context

## When to Persist

- **Mandatory** for all diagnostic/debugging work
- **Recommended** for implementation work that reveals non-obvious system behavior
- **Skip** for routine edits where no new knowledge was gained

## Before Returning Results

Check: "Did I write my findings to repo memory?" If not, do it now.
