---
status: canonical
owner: repository
last_verified: 2026-08-11
last_verified_commit: 453fd9b6
sources:
  - docs/archive/legacy/
update_triggers:
  - Historical material is moved, corrected, redacted, or superseded.
---

# Documentation archive

The archive preserves evidence without letting old plans or completed
procedures compete with current guidance.

## Contents

| Path | Contents | Current authority |
|---|---|---|
| `legacy/audits/` | Point-in-time service, worker, web, and PostgreSQL audits | None; revalidate before promoting work |
| `legacy/refactor/` | The completed/abandoned multi-phase web refactor journal | [`components/web-app.md`](../components/web-app.md) |
| `legacy/design/` | Superseded and unadopted design documents | Current architecture and ADRs |
| `legacy/database/` | Legacy DB design, execution journal, completed/rejected runbooks | [`architecture/data-storage.md`](../architecture/data-storage.md) and living runbooks |

Archived documents may contain stale paths, counts, statuses, commands, and
relative links because those details are part of the historical record. Their
archive banners and directory placement are authoritative.

Do not copy an archived command into an operator session without re-deriving
its prerequisites, affected objects, rollback, and current implementation.
