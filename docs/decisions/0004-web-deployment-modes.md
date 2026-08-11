---
status: decision
owner: web
last_verified: 2026-08-11
last_verified_commit: 453fd9b6
sources:
  - FortniteFestivalWeb/Dockerfile
  - FortniteFestivalWeb/nginx.conf
  - FSTService/Program.cs
  - docker-compose.yml
update_triggers:
  - Static hosting, embedded assets, reverse proxy, or web/API container boundaries change.
---

# ADR 0004: Prefer standalone Nginx, retain embedded fallback

## Decision

Deploy the public React application as a standalone Nginx container that
reverse-proxies API and health traffic to `fstservice`. Keep FSTService's
ability to serve an embedded `wwwroot` bundle when one is packaged.

## Rationale

- Static web availability and maintenance UI are decoupled from API startup.
- Nginx provides immutable asset caching, SPA fallback, compression, and
  container-DNS re-resolution.
- The embedded path remains useful for compact or fallback deployments without
  forcing it on production.

## Consequences

- Both hosting paths must preserve API 404 behavior and SPA fallback.
- Web image and service image releases can be validated independently.
- API contract changes still require synchronized client/types regardless of
  hosting mode.
