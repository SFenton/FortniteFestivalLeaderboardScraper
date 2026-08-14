# Playwright browser testing

Playwright owns behavior that requires a real browser: routing, focus, touch,
layout, responsive breakpoints, SVG/chart interaction, observers, storage,
publication changes, WebSockets, and browser-engine differences. Vitest keeps
fast logic and rendering assertions that do not require those capabilities.

Accessibility route specs block moderate, serious, and critical axe findings
and own the shell main/skip/title/announcement/focus contract plus
reduced-motion, Save-Data, and image-label behavior. These specs run in the
focused WebKit project as well as Chromium; Firefox and desktop WebKit retain
the same surface in the nightly matrix.

Route contracts also own selected-player/band guard behavior, replace-history
semantics, malformed deep-link resilience, and the intentional Not Found
surface. Ownership specs verify that full-song history and Suggestions
rivals-all requests remain account-scoped and reusable across route remounts.

## Layout

```text
e2e/
  fixtures/
    scenarios.ts        Typed empty and populated application worlds
    apiRouter.ts        Strict API, request recorder, overrides, WebSockets
    appState.ts         Local/session storage and selected-profile ownership
    test.ts             Shared Playwright fixtures
    fre.ts              First-run carousel driver layered on shared fixtures
  support/
    drivers/            Reusable user actions, not assertions or mock data
    projects.ts         Engine/project classification
  specs/
    accessibility/      Real-route axe, focus, reduced motion
    architecture/       Lazy boundaries and bundle loading contracts
    browser/            Critical cross-engine smoke
    diagnostics/        Tap and hit-test diagnostics
    flows/              Notifications and cross-page behavior
    ownership/          Request, cache, and storage ownership
    pages/              Page and domain interactions
    performance/        Deterministic runtime and growth benchmarks
    platform/           Publication, recovery, WebSocket behavior
    responsive/         Breakpoint and constrained-height geometry
    routes/             Route content and guard contracts
    shell/              Shell scrolling and navigation
  fre/                  First-run gate and progression contracts
```

Component browser tests are separate:

```text
component-tests/        Playwright component specifications
playwright/gallery/     Stories-and-gallery mount page
src/**/*.story.tsx      Production component scenarios
```

## Scenario rules

- Specs select a named `AppScenario`; they do not hand-write API payloads.
- Builders and scenarios are typed against `@festival/core/api`.
- Unknown API paths fail closed with the request method, path, and query.
- Every successful publication-bound response includes
  `X-FST-Publication-Id`.
- `ApiScenarioController` records requests, supports one-shot failures and
  delays, and can send or disconnect mocked WebSockets.
- `AppState` owns local and session storage. Specs should not clear only a
  prefix or write raw profile state unless they are testing the storage
  contract itself.
- Use `createEmptyScenario()` for absence/empty-state behavior and
  `createPopulatedScenario()` for rows, graphs, scores, bands, pagination, and
  selected-profile presentation.

## Locator and driver rules

- Prefer roles, labels, and visible names.
- Use test IDs for geometry, virtualization, chart internals, and generated
  rows that do not have a stable accessible identity.
- Drivers perform stable user actions. Assertions remain in specs.
- Do not centralize every production test ID into one registry.
- Avoid `waitForTimeout`; use web-first assertions, request records, the
  Playwright clock, or transition completion signals.

## Projects

| Project | Ownership |
|---|---|
| `chromium-desktop` | Full desktop functional suite |
| `chromium-mobile` | Full touch/mobile functional suite |
| `chromium-wide` | Manual asset and responsive boundary suites |
| `webkit-mobile` | iPhone-class critical and accessibility coverage |
| `webkit-desktop` | Scheduled Safari-engine desktop coverage |
| `firefox-desktop` | Scheduled Gecko coverage |

Component projects use `ct-chromium`, `ct-webkit`, and `ct-firefox`.
Publication uses a dedicated `publication-chromium` config and a second Vite
server with the normal e2e publication stub disabled.

Breakpoint boundaries are parameterized inside responsive/component specs
instead of multiplying the complete suite across viewport-only projects.

## Commands

```bash
corepack yarn e2e:typecheck
corepack yarn e2e
corepack yarn e2e:ci
corepack yarn e2e:component
corepack yarn e2e:publication
corepack yarn e2e:nightly
```

Targeted examples:

```bash
corepack yarn playwright test --project=chromium-desktop e2e/specs/routes
corepack yarn playwright test --project=webkit-mobile e2e/specs/browser
corepack yarn playwright test --config=playwright.component.config.ts --project=ct-chromium
corepack yarn performance:suggestions
```

The Suggestions benchmark fixes the generator clock, drives at least 100
accepted load triggers, attaches JSON metrics for generated/rendered
categories, DOM nodes, frosted markers, scroll height, heap, long tasks,
mousemove geometry reads, and back/forward restoration. The PR 4 baseline is
540 rendered categories, about 22.7k DOM nodes, 1,471 frosted markers, roughly
50.6 MB post-GC heap growth, a 1.28 s worst long task, and 1,471 geometry
reads. The PR 5 candidate is 12 mounted categories, 519 DOM nodes, 38 markers,
about 8 MB heap growth, zero list-growth long tasks, one geometry read, and
exact deep restoration. Those final ceilings are enforced. A second tagged
test reaches the 1,000-category limit under fully hidden filters and verifies
the explicit fresh-mix reset. The PR runner executes both in a dedicated
one-worker pass after the normal Chromium desktop project. Set
`SUGGESTIONS_CPU_THROTTLE` to a Chromium CPU multiplier for a local
slow-device run.

`scripts/run-e2e-project.mjs` accepts:

- `E2E_WORKERS` for per-run worker count;
- `E2E_SHARD=1/2` style external CI sharding;
- `PLAYWRIGHT_PORT` for isolated parallel servers.

## Failure artifacts

- One CI retry runs with the isolated retry strategy.
- A flaky pass fails CI through `failOnFlakyTests`.
- Traces are captured on the first retry.
- Screenshots are captured only on failure.
- Video is reserved for targeted gesture diagnostics.
- A quarantine must retain execution/reporting and have an issue, owner, and
  expiry.
