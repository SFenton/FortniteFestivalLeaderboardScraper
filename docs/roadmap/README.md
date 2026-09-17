---
status: roadmap
owner: repository
last_verified: 2026-08-16
last_verified_commit: 937868e0
sources:
  - docs/roadmap/data.md
  - docs/roadmap/post-scrape-processing.md
update_triggers:
  - A roadmap item is added, accepted, rejected, completed, blocked, or removed.
---

# Roadmap

Only unresolved, revalidated work belongs here. Obsolete audit, execution, and
refactor journals are removed from the current tree.

## Active areas

- [Data and publication readiness](data.md)
- [Post-scrape processing](post-scrape-processing.md)

Deleted historical roadmaps are not silently carried forward. Promote an item
from Git history only after checking current code, tests, runtime evidence,
dependencies, safety gates, and whether later work already solved or rejected
it.
