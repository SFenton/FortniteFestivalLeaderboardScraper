---
status: canonical
owner: web
last_verified: 2026-08-12
last_verified_commit: 9f343376
sources:
  - FortniteFestivalWeb/package.json
  - FortniteFestivalWeb/.node-version
  - FortniteFestivalWeb/Dockerfile
  - FortniteFestivalWeb/src/main.tsx
  - FortniteFestivalWeb/src/App.tsx
  - FortniteFestivalWeb/src/components/lazy/secondaryControls.ts
  - FortniteFestivalWeb/performance-budgets.json
  - FortniteFestivalWeb/scripts/check-performance-budgets.mjs
  - .github/workflows/web-performance.yml
  - FortniteFestivalWeb/src/routes.ts
  - FortniteFestivalWeb/src/api/
  - FortniteFestivalWeb/src/contexts/
  - FortniteFestivalWeb/playwright.config.ts
  - FortniteFestivalWeb/playwright.component.config.ts
  - FortniteFestivalWeb/playwright.publication.config.ts
  - FortniteFestivalWeb/e2e/README.md
  - FortniteFestivalWeb/nginx.conf
update_triggers:
  - Routes, providers, state ownership, publication handling, styling conventions, package boundaries, or web deployment changes.
---

# Web app

`FortniteFestivalWeb` is a React 19 and TypeScript application built by Vite.
It uses React Router's `HashRouter`, TanStack React Query for remote state,
i18next for localization, and Yarn 4 as its package manager.

## Bootstrap

`src/main.tsx` installs direct-route migration and stale-chunk recovery, then
renders:

1. `QueryClientProvider`
2. `PublicationBoundary`
3. `BackendAvailabilityGate`
4. the application or a diagnostic fixture

Diagnostic fixtures, persisted scroll-fade test mode, tap-diagnostics runtime,
and notification sample data stay outside the normal entry graph. They load
only when their explicit query, validation, or stored diagnostic preference is
present. Root diagnostic fixtures render independently of publication and
backend-availability gates. Conditional shell dialogs and the first-run
carousel also load through interaction/visibility boundaries rather than the
normal returning-user entry.

`PublicationBoundary` blocks the normal application until `/api/publication`
resolves. A publication-change event clears query/song caches, resets the
WebSocket, and remounts the app with the new publication ID.

## Routes

The route tree covers:

- songs, song detail, solo leaderboards, band leaderboards, and player history;
- player profiles, statistics, rivals, suggestions, and competition views;
- global, family, combo, and band rankings;
- band lookup, band detail, and player-band views;
- shop, optional manual, settings, and licenses.

Use `src/routes.ts` for route construction and `src/App.tsx` for the rendered
tree and access-dependent redirects. The manual is the only feature currently
exposed through `/api/features`.

## State ownership

| State | Owner |
|---|---|
| Remote API data | React Query and API-specific caches |
| Publication identity | `src/api/publication.ts` and `PublicationBoundary` |
| User preferences | Settings context and browser storage |
| Navigation/shareable filters | Route paths and search parameters where implemented |
| Shell interactions | Focused contexts for search, page readiness/actions, visibility, selection, and feature state |

Do not add another global store without a cross-cutting need that the existing
query/context split cannot model.

## UI structure and styling

The application has mobile and wide-desktop shells around one shared route
tree. Pages use shared shell, loading, empty, error, modal, card, navigation,
and action primitives, but not every route has an identical component shape.

Current styling combines:

- co-located CSS Modules for selectors, pseudo states, media queries, and
  animations;
- typed values and factories from `@festival/theme`;
- local inline style objects when values are dynamic or do not justify a CSS
  class;
- shared UI utilities from `@festival/ui-utils`.

The obsolete CSS migration checklist was removed and is not a current
file-count or completion source.

## API boundary

The request implementation lives in `src/api/client.ts`. Shared response and
domain types come from `@festival/core`; that package is not itself the HTTP
client. API changes must keep the service endpoint files, shared types, and
client aligned.

`src/changelog.ts` is current in-app announcement content. The eager shell reads
only the checked hash metadata in `src/changelogHash.ts`; a unit test requires
that metadata to match the lazy announcement content. The changelog is not a
durable release history or a source of implementation status.

## Build and deployment

The web build runtime is pinned by `FortniteFestivalWeb/.node-version`; CI,
nightly browser runs, performance measurement, and the production web image use
that exact Node patch. The preferred production image builds the SPA with Node
and serves static files through Nginx. Nginx re-resolves the `fstservice`
container name, proxies
`/api`, `/healthz`, and `/readyz`, supports WebSockets, applies immutable asset
caching, and falls back to `index.html` for client routes.

FSTService can also serve an embedded `wwwroot` bundle when one is present; see
[ADR 0004](../decisions/0004-web-deployment-modes.md).

## Browser test ownership

Playwright uses typed named application scenarios rather than generic empty
payloads. The shared router models publication headers, request transitions,
storage, selected player/band profiles, populated and partial score states,
rankings, rivals, notifications, paths, and WebSockets. Route specs protect
every rendered and guarded route contract; page specs own rich rows,
pagination, graphs, filters, and persistence.

The project matrix separates actual browser/device regimes from responsive
boundaries. Chromium desktop/mobile run the full functional suite, WebKit and
Firefox own engine-sensitive coverage, and breakpoint widths are exercised in
focused responsive or component tests. Real production components use
Playwright's stories-and-gallery mount model for focus, overflow, touch,
geometry, and constrained width/height behavior.

Validation commands are in [Testing](../testing/README.md).
