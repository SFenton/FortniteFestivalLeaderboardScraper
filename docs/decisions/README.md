---
status: canonical
owner: repository
last_verified: 2026-08-18
last_verified_commit: 21d7193c
sources:
  - docs/decisions/
update_triggers:
  - An architectural decision is accepted, superseded, or reversed.
---

# Architecture decisions

| ADR | Decision |
|---|---|
| [0001](0001-split-service-worker-roles.md) | Run API and mutation worker roles from one .NET image |
| [0002](0002-publication-generation.md) | Publish atomically and keep public reads on a stable generation |
| [0003](0003-vpn-http-proxy-isolation.md) | Proxy only Epic scrape traffic through Gluetun HTTP endpoints |
| [0004](0004-web-deployment-modes.md) | Prefer a standalone Nginx web container while retaining an embedded fallback |
| [0005](0005-post-scrape-modular-monolith.md) | Keep post-scrape work in a modular monolith and add same-binary isolated replay before new processes or services |
| [0006](0006-snapshot-generation-subpartitions.md) | Subpartition physical leaderboard snapshots by retained generation |
| [0007](0007-snapshot-generation-drop-and-logical-restore.md) | Isolate exact-child DROP from quarantine and restore logically |

ADRs record rationale and consequences. Current behavior still belongs in the
canonical architecture and component documents.
