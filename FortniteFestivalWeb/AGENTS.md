# FortniteFestivalWeb Development Guidelines

## Stack

React 19, TypeScript, Vite, React Router HashRouter, TanStack React Query,
i18next, CSS Modules, and `@festival/theme`. Yarn 4 is authoritative for this
application.

## Architecture

Current routes, bootstrap providers, publication behavior, state ownership,
styling, shared packages, and deployment are documented in:

- `docs/components/web-app.md`
- `docs/components/shared-code.md`
- `docs/reference/api-contract.md`
- `docs/testing/README.md`

Use `src/routes.ts` for route construction and `src/App.tsx` for the rendered
tree. React Query owns remote data; focused contexts own application/shell
state. Do not copy volatile context, test, route, or file counts into guidance.

## API and publication

The HTTP client lives in `src/api/client.ts`; shared response/domain types live
in `@festival/core`. `PublicationBoundary` must continue to bootstrap before
normal rendering and clear caches/reset WebSocket state on publication change.

API changes must stay aligned with service routes/contracts and
`packages/core/src/api/serverTypes.ts`.

## Styling

Use CSS Modules for selectors, pseudo states, media queries, and animations.
Use `@festival/theme` for shared tokens and typed style values. Inline styles
are appropriate for small dynamic values. The obsolete refactor/CSS migration
documents were removed and are not an active checklist.

## Testing

```bash
corepack yarn test:unit
corepack yarn test:shared
corepack yarn lint
corepack yarn lint:css
corepack yarn build
corepack yarn e2e
```

Use the smallest relevant Playwright project/spec.

## Dependencies and licenses

When a package manifest, lockfile, NuGet reference, or bundled dependency
changes:

```bash
corepack yarn licenses:generate
corepack yarn licenses:check
```

Add unresolved metadata to `../tools/license-overrides.json`.

## Documentation

Follow `.github/instructions/documentation.instructions.md`. Route, provider,
state, styling, API, feature, testing, build, or deployment changes must update
the canonical web documentation in the same change.
