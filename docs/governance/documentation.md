---
status: canonical
owner: repository
last_verified: 2026-08-11
last_verified_commit: 453fd9b6
sources:
  - docs/README.md
  - .github/instructions/documentation.instructions.md
  - tools/check-docs.mjs
update_triggers:
  - Documentation lifecycle, metadata, indexing, or validation rules change.
---

# Documentation governance

Documentation is part of the implementation. A change is incomplete when a
documented behavior differs from code, configuration, tests, or the production
contract.

## Sources of truth

Use this precedence when evidence conflicts:

1. Current code, configuration, tests, and measured runtime evidence.
2. Canonical documents listed in [`docs/README.md`](../README.md).
3. Living runbooks whose prerequisites have been revalidated.
4. Roadmaps, decisions, and archives for intent or history only.

A roadmap is never proof that a feature exists. An archive is never current
operator guidance.

## Required metadata

Canonical documents, living runbooks, roadmaps, decisions, and supersession
stubs must begin with YAML front matter containing:

- `status`
- functional `owner`
- `last_verified`
- `last_verified_commit`
- `sources`
- `update_triggers`

Owners are functional areas such as `web`, `service`, `worker`, `data`,
`operations`, or `repository`; do not invent a person. Archived original
documents retain their historical body and use an archive banner instead.

## Same-change update triggers

Review and update the matching canonical documentation whenever a change
affects:

| Changed surface | Required review |
|---|---|
| Component boundaries or cross-component flow | `architecture/` and the affected `components/` page |
| API routes, auth, rate limits, middleware, DTOs, or errors | `reference/api-contract.md` and `components/service-api.md` |
| Web routes, providers, state ownership, caching, or styling conventions | `components/web-app.md` |
| Worker modes, phases, scrape gates, recovery, or publication | `components/worker.md` and `architecture/data-publication-flow.md` |
| Schema, persistence, retention, source ownership, or restore | `architecture/data-storage.md` and the applicable runbook |
| Compose services, images, networks, volumes, ports, or role boundaries | `operations/deployment.md` |
| Proxies, VPN providers, Gluetun, proxy arrays, pacing, or self-heal | `operations/vpn-proxy-pool.md` |
| Configuration keys, environment variables, role files, or feature flags | `reference/configuration.md` and/or `reference/feature-flags.md` |
| CLI flags or one-shot commands | `reference/cli.md` |
| Test commands, CI, generated artifacts, or coverage gates | `testing/README.md` |
| Shared package exports or cross-language types | `components/shared-code.md` |

## When a new document is required

Create a new canonical document when a change introduces a top-level component,
container, hosted worker, run mode, public API family, durable data family,
external provider, operator procedure, or cross-cutting architectural decision
that no current page owns. Add it to `docs/README.md` in the same change.

Use an ADR under `docs/decisions/` when the rationale, alternatives, or
consequences would not remain clear from code alone.

## Roadmaps and archives

- Roadmaps contain unresolved work only.
- When work is accepted, rejected, completed, or superseded, update the roadmap
  in the same change.
- Move terminal evidence to `docs/archive/` instead of appending it to
  `README.md` or a living design.
- Completed destructive procedures must say `COMPLETED - DO NOT RE-EXECUTE`
  at both the old compatibility path and the archived original.
- Archived originals are changed only for a correction notice, redaction, or
  supersession link.

## Completion evidence

Every agent or pull request that changes the repository must state one of:

```text
Documentation impact: updated <paths>
```

```text
Documentation impact: none - <specific reason>
```

Run:

```bash
node tools/check-docs.mjs
```

The checker validates the canonical document set, metadata, index coverage,
relative links outside the archive, supersession paths, and the root README
size boundary.
