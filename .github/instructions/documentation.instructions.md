---
applyTo: "**"
---

# Documentation synchronization policy

Documentation is part of every repository change.

## Required review

Before completing a change, determine whether it affects a documented area:

- contributor setup, commands, dependencies, generated artifacts, or CI;
- component boundaries, responsibilities, data flow, or security boundaries;
- API routes, DTOs, auth, rate limits, middleware, errors, or publication
  classification;
- web routes, providers, state ownership, caching, styling, or deployment;
- worker modes, hosted services, scrape phases, recovery, freeze, publication,
  or notification behavior;
- schema, persistence, data ownership, retention, restore, or maintenance;
- Compose services, images, ports, networks, volumes, roles, or health checks;
- VPN/proxy providers, Gluetun topology, aligned endpoint arrays, pacing,
  cooldown, transport, or self-heal;
- configuration, environment variables, CLI flags, or feature flags;
- shared .NET/TypeScript package exports and cross-language contracts.

If behavior in one of these areas changes, update its canonical document under
`docs/` in the same change. A change is incomplete while implementation and
documentation disagree.

## New documentable areas

Create a new canonical document when introducing a top-level component,
container, hosted worker, run mode, public API family, durable data family,
external provider, operator procedure, or cross-cutting decision that no
existing page owns.

Add every new canonical page to `docs/README.md`. Add an ADR under
`docs/decisions/` when rationale, alternatives, or consequences are not obvious
from code.

## Contract-specific rules

API changes must review:

- `FSTService/Api/ApiEndpoints.cs`;
- the affected `FSTService/Api/*Endpoints.cs`;
- publication classification/surface contracts and tests;
- `packages/core/src/api/serverTypes.ts`;
- `FortniteFestivalWeb/src/api/client.ts`;
- `docs/reference/api-contract.md`.

Public feature changes must review:

- `FSTService/FeatureOptions.cs`;
- `FSTService/Api/FeatureEndpoints.cs`;
- `packages/core/src/api/serverTypes.ts`;
- `FortniteFestivalWeb/src/contexts/FeatureFlagsContext.tsx`;
- `docs/reference/feature-flags.md`.

Backend-only feature flags do not need browser exposure, but they must be
documented and assigned to the correct service/worker role.

Deployment/VPN changes must distinguish repository templates from the
production-owned Compose project. Never copy credentials, resolved values,
private endpoints, or provider account data into repository docs.

## Roadmaps and history

- Roadmaps contain unresolved work only and are never proof that behavior
  exists.
- Update an item's status in the same change that accepts, rejects, completes,
  or removes it.
- After moving valid current conclusions into canonical docs, delete obsolete
  audits, plans, designs, progress journals, and completed/rejected one-shot
  runbooks. Do not leave compatibility stubs or an in-repository archive.
- Git history is forensic evidence only, not current guidance or authorization.
- Keep safety-critical terminal facts in canonical current-state docs so
  completed destructive work cannot be mistaken for pending work.
- Do not append operational journals to `README.md`.

## Completion requirement

Every agent or pull request must report:

```text
Documentation impact: updated <paths>
```

or:

```text
Documentation impact: none - <specific reason>
```

Run `node tools/check-docs.mjs` before completion. Follow
`docs/governance/documentation.md` for lifecycle and metadata rules.
