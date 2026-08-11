---
status: decision
owner: data
last_verified: 2026-08-11
last_verified_commit: 453fd9b6
sources:
  - FSTService/ScraperWorker.cs
  - FSTService/Scraping/ScrapeLifecycleNotifier.cs
  - FSTService/Api/PublicationRouteSurfaceContract.cs
update_triggers:
  - Publication, read-freeze, generation, source binding, or failure-isolation semantics change.
---

# ADR 0002: Publish atomically from isolated candidates

## Decision

Treat scrape/post-process output as a candidate until validation and an atomic
publication commit advance the public generation. Keep readers on the previous
published generation while candidate work runs.

## Rationale

Epic collection and derived ranking work can fail independently. Exposing
partially refreshed tables would mix seasons, scrape IDs, rank inputs, caches,
and histories. A persisted freeze, route-surface contract, read leases, and
generation pointer provide a fail-closed boundary.

## Consequences

- Committed candidate rows are not automatically public.
- Incomplete or rejected candidates remain replay/diagnostic evidence.
- Publication readiness must cover every bound route surface before request
  pinning can be enabled.
- Browser caches and WebSockets reset only after the new generation is
  available.
