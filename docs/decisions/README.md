---
status: canonical
owner: repository
last_verified: 2026-08-11
last_verified_commit: 453fd9b6
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

ADRs record rationale and consequences. Current behavior still belongs in the
canonical architecture and component documents.
