---
status: canonical
owner: web
last_verified: 2026-08-14
last_verified_commit: c0e0f775
sources:
  - FortniteFestivalWeb/package.json
  - FortniteFestivalWeb/.node-version
  - FortniteFestivalWeb/Dockerfile
  - FortniteFestivalWeb/src/main.tsx
  - FortniteFestivalWeb/src/App.tsx
  - FortniteFestivalWeb/src/components/lazy/secondaryControls.ts
  - FortniteFestivalWeb/src/components/common/Accordion.tsx
  - FortniteFestivalWeb/src/components/shell/fab/MobileFloatingActionButton.tsx
  - FortniteFestivalWeb/src/components/shell/ShellScrollRestoration.tsx
  - FortniteFestivalWeb/src/components/shell/mobile/BottomNav.tsx
  - FortniteFestivalWeb/src/contexts/FabVisibilityContext.tsx
  - FortniteFestivalWeb/src/pages/Page.tsx
  - FortniteFestivalWeb/src/pages/settings/SettingsPage.tsx
  - FortniteFestivalWeb/src/pages/settings/SettingsServiceProgress.tsx
  - FortniteFestivalWeb/src/pages/settings/SettingsServiceProgress.module.css
  - FortniteFestivalWeb/src/pages/settings/serviceProgress.ts
  - FortniteFestivalWeb/src/pages/settings/serviceInfo.en.json
  - FortniteFestivalWeb/src/hooks/data/useServiceInfo.ts
  - FortniteFestivalWeb/src/hooks/ui/useScrollUpdateScheduler.ts
  - FortniteFestivalWeb/src/hooks/ui/useVirtualListScrollMargin.ts
  - FortniteFestivalWeb/e2e/specs/responsive/settings-progress.spec.ts
  - FortniteFestivalWeb/src/pages/shop/ShopPage.tsx
  - FortniteFestivalWeb/src/pages/leaderboards/modals/RankByModal.tsx
  - FortniteFestivalWeb/src/pages/leaderboards/firstRun/metricInfo/
  - FortniteFestivalWeb/src/pages/suggestions/SuggestionsPage.tsx
  - FortniteFestivalWeb/src/pages/suggestions/components/SuggestionsLoadSentinel.tsx
  - FortniteFestivalWeb/src/pages/suggestions/suggestionsSessionCache.ts
  - FortniteFestivalWeb/src/pages/manual/ManualPage.tsx
  - FortniteFestivalWeb/src/pages/manual/manualScreenshotAssets.ts
  - FortniteFestivalWeb/manual-assets/generated/manifest.json
  - FortniteFestivalWeb/src/i18n/en.json
  - FortniteFestivalWeb/src/i18n/appManual.en.json
  - FortniteFestivalWeb/src/i18n/settings.en.json
  - FortniteFestivalWeb/src/i18n/firstRun.en.json
  - FortniteFestivalWeb/performance-budgets.json
  - FortniteFestivalWeb/scripts/check-performance-budgets.mjs
  - FortniteFestivalWeb/scripts/generate-manual-image-variants.mjs
  - FortniteFestivalWeb/scripts/generate-theme-css.mjs
  - .github/workflows/web-performance.yml
  - FortniteFestivalWeb/src/routes.ts
  - FortniteFestivalWeb/src/routeMetadata.ts
  - FortniteFestivalWeb/src/api/
  - FortniteFestivalWeb/src/components/page/RouteBoundary.tsx
  - FortniteFestivalWeb/src/components/page/RouteGuards.tsx
  - FortniteFestivalWeb/src/pages/NotFoundPage.tsx
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

The production build classifies every TypeScript source module. Application
modules must be reachable from the normal or lazy Vite graph; only component
stories and an explicit set of type-only modules may remain outside it.
Obsolete implementations and tests that import only those implementations are
removed together rather than retained to inflate coverage.

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
tree. Static destinations and route-family matchers stay centralized in
`src/routes.ts`; route title/announcement metadata stays in
`src/routeMetadata.ts`. `RouteBoundary` gives every normal route, including
the eager Songs page, the standard recoverable error UI. `RequirePlayer` and
`RequireSelection` own access redirects with replace semantics, while one
wildcard route retains malformed URLs and renders an intentional Not Found
page. Route and tab ownership normalize trailing slashes, and Licenses remains
owned by the Settings tab. The manual is the only feature currently exposed
through `/api/features`.

## State ownership

| State | Owner |
|---|---|
| Remote API data | React Query; shared option factories own keys, request functions, and policies |
| Publication identity | `src/api/publication.ts` and `PublicationBoundary` |
| User preferences | Settings context and browser storage |
| Navigation/shareable filters | Route paths and search parameters where implemented |
| Shell interactions | Focused contexts for search, page readiness/actions, visibility, selection, and feature state |

Do not add another global store without a cross-cutting need that the existing
query/context split cannot model.

Full-song player history uses one account/song query across Song Detail,
Player History, and chart consumers. Rival data used by Suggestions is also
React Query owned under the selected account. Profile sync, publication
changes, and name refreshes invalidate the same account scopes, while the
Suggestions module cache retains only locally generated mix/navigation state.
Page-visit animation state is committed only after content is ready; render
and abandoned/suspended work do not mutate session-level visit markers.

## UI structure and styling

The application has mobile and wide-desktop shells around one shared route
tree. Pages use shared shell, loading, empty, error, modal, card, navigation,
and action primitives, but not every route has an identical component shape.

Accordion triggers own stable panel relationships through `aria-expanded` and
`aria-controls`. Collapsed panels remain mounted for their grid-row transition
but are `inert` and `aria-hidden`, so hidden controls never enter the tab order.
Sort and Suggestions accordions expose named regions; dense instrument filter
groups avoid excessive landmarks. Mobile BottomNav uses pending state only for
visual feedback and assigns `aria-current="page"` solely to the committed route.

Both shell layouts render exactly one `main#main-content[tabindex="-1"]`.
`RouteAccessibility` owns the first-focusable HashRouter-safe skip link,
document titles, polite route announcements, and focus transfer for distinct
PUSH/REPLACE navigation. Initial navigation and POP do not move focus; modal
ownership delays route focus and preserves a different connected control that
the modal restores. `src/routes.ts` and `src/routeMetadata.ts` supply route
matching, titles, and mobile chrome labels, including Not Found metadata. A
visually hidden fallback H1 covers lazy/mobile gaps and self-removes whenever a
page-owned visible H1 is present.

Decorative visual policy is centralized through `useVisualPreferences`.
Reduced motion removes background crossfades, continuous pulse/breathe
animation, notification media cycling, and rotating selected-band members
without disabling functional spinners. Save-Data omits remote decorative
background and optional notification album art. Both policies pause timer work
while the document is hidden. Instrument images expose canonical display
labels while retaining wire keys only in `data-instrument`; repeated star PNGs
are decorative children of one labelled star group.

`Page` owns the standard bottom-clearance contract for fixed action surfaces.
Its default `end` spacer always reserves FAB clearance, while `fixed` adjusts
the shell viewport and `none` delegates spacing to the caller. The opt-in
`auto` mode preserves the existing desktop spacer but, in mobile chrome,
reserves clearance only while `MobileFloatingActionButton` has registered a
renderable surface. Empty warm-up mounts do not register, and the shared
registry tracks overlapping page-owned and shell-owned FABs independently.
The Item Shop uses `auto`, so narrow handsets without quick links, a selected
band filter, or the view-toggle action retain only the list's normal bottom
padding; handset states that do render a FAB remain protected from overlap.

`ShellScrollRestoration` owns route/layout scroll resets, preserve-scroll keys,
and the lazy Suggestions restoration coordinator outside `App.tsx`.
`useScrollUpdateScheduler` provides one-frame coalescing plus viewport settling
and cleanup for masks/fades, while each consumer retains its own observer and
geometry rules. Songs and Suggestions share virtual-list scroll-margin
measurement through `useVirtualListScrollMargin`; filter-specific virtualizer
compensation remains local to Suggestions.

Rank By keeps its normal radio controls in the shared secondary-control chunk.
Per-instrument player metric-help content is a nested interaction boundary: the
accessible info button loads the metric carousel, formulas, KaTeX JS/CSS, and
formula fonts only after activation. The parent modal becomes inert while help
owns focus; Escape closes only help and returns focus to the exact info
trigger. Band, combo, and solo-family Rank By controls use scope-specific
descriptions and intentionally expose no per-instrument metric-help action.

Current styling combines:

- co-located CSS Modules for selectors, pseudo states, media queries, and
  animations;
- typed values and factories from `@festival/theme`;
- local inline style objects when values are dynamic or do not justify a CSS
  class;
- shared UI utilities from `@festival/ui-utils`.

`src/styles/theme.css` is generated from an explicit 115-variable mapping to
`@festival/theme`; `theme:css:check` runs before every production build and
fails on drift. Existing CSS variable names remain stable. Deprecated `Size`
usage is allowed only within a checked no-growth baseline while migrations
move domain slices to `IconSize`, `InstrumentSize`, `StarSize`, `ChartSize`,
`MetadataSize`, `GeneralSize`, and `Layout`. The first accepted slice removes
all chart-category aliases and shares rank-history modeling, axis visuals, and
accuracy colors without merging the solo and band chart components.

Band type taxonomy and localized labels have one web owner. Labels are
translated at render time and are never persisted or used as route keys.
Action and band-filter pills share transition timing while retaining their
different active-style behavior.

The obsolete CSS migration checklist was removed and is not a current
file-count or completion source.

## API boundary

The request implementation lives in `src/api/client.ts`. Shared response and
domain types come from `@festival/core`; that package is not itself the HTTP
client. API changes must keep the service endpoint files, shared types, and
client aligned.

### Settings service progress

Settings keeps `useServiceInfo('settings')` as its sole request owner on the
shared React Query key. Visible Settings polling is five seconds; hidden-page
polling is throttled to 30 seconds. No WebSocket or page-owned duplicate fetch
is added, and publication-boundary cache/reset ownership is unchanged.

The service area uses one flat `FrostedCard` with no tinted or bordered
subcards. Its first live summary line combines update and worker state, then
puts the specific translated phase and any distinct subphase in the card's
primary visual position. Identical phase/subphase labels collapse to one line.
Idle state uses that same position to say that the service is waiting for the
next update.

The browser uses stable phase/subphase IDs for localization with safe label
fallbacks. A phase bar is determinate only when service-info v2 reports a final
denominator and exact `phasePercent`; v1 and unknown-total payloads remain
indeterminate and never reuse legacy `progressPercent` as exact progress.
The visible phase percentage stays on the progress-bar line. Units, exceptional
phase states, estimated overall progress, and ETA range/confidence wrap beneath
it without promoting estimated overall progress above the exact phase value.
ETA sample count remains part of the trust gate but is not user-facing.
Display memory rejects older payload regressions while allowing a new phase
attempt to reset and announce itself.

One concise availability sentence distinguishes an existing publication from a
first publication still in progress without exposing scrape IDs. A flat
definition-list footer shows last publication, current update start when
applicable, and next scheduled update behind one subtle divider. Operational
IDs, raw heartbeat/progress timestamps, attempts, model diagnostics, technical
disclosures, and selected-profile rival/sync status are not rendered. Settings
therefore does not add a selected-profile sync polling loop; profile-name
refresh and export controls keep their existing selected-profile ownership.

English shell/common/Songs resources remain eager in the i18next `translation`
namespace. App Manual, Settings, and First Run resources use named namespaces
registered synchronously by their lazy page/carousel owners. The Settings
namespace also owns the co-located service-progress vocabulary. Existing key
paths remain unchanged because route components use translation-first namespace
fallback; direct-route and replay browser tests reject any visible untranslated
key. The production graph requires all three JSON resources to stay outside the
entry and inside their declared lazy owner closures.

Focused unit and Playwright coverage owns v1/v2 rendering, exact and unknown
denominators, ETA suppression, warnings/failures/restarts, absence of technical
and selected-profile sync surfaces, shared-request concurrency, one-card
overflow at 320, 375, 768, and 1440 pixels, and determinate desktop/mobile axe
coverage.

The path modal can display the generated PNG or a text table. Text mode renders
one row per activation, not one row per optional start note. Schema-v2
artifacts supply authoritative trigger metadata for the fret cue, beat, time,
Overdrive, and score columns. Raw CHOpt instruction strings remain in the JSON
contract for parity validation but are not shown in the modal; legacy artifacts
show unavailable metrics explicitly. See
[Path generation](path-generation.md).

The selector exposes Lead, Bass, Drums, Tap Vocals, Pro Lead, Pro Bass,
Pro Drums, and Pro Drums + Cymbals when enabled in Settings. Karaoke remains
the only instrument without path visualization.

`src/changelog.ts` is current in-app announcement content. The eager shell reads
only the checked hash metadata in `src/changelogHash.ts`; a unit test requires
that metadata to match the lazy announcement content. The changelog is not a
durable release history or a source of implementation status.

## Suggestions generation and loading

Suggestions are locally generated from the current catalog and selected
player/band score source; they are not paginated remote data and therefore do
not use `useInfiniteQuery`. `useSuggestions` owns the generator, navigation
cache, and batch commit guard. Solo rival input is fetched once per account
through the shared React Query rivals-all key, then injected into the current
generator without replacing its mix. Cached rival data is installed before a
fresh mix generates its first category; data that resolves or refreshes later
requeues rivalry pipelines while retaining emitted categories and history.
The navigation cache records the applied query revision and combo, so an
unchanged remount does not duplicate pipelines; a distinct revision also
reactivates a previously exhausted mix. A lightweight per-identity scroll map
is shared with the persistent shell.
Because generated content caches one identity, committing a different player
or band source immediately discards the prior local mix, even before the new
source finishes loading. Creating its replacement generator then resets the
identity and invalidates snapshots for discarded identities before the shell
can restore them. Same-identity route and layout remounts preserve their
snapshot because they reuse the generator. Each generator has a distinct mix
identity, and the raw navigation cache stops at 1,000 categories. The page
then exposes an explicit **Start a new mix** action that creates a fresh seeded
generator while retaining the selected source and filters; it never silently
evicts the user-visible backscroll range. Filtered-empty sessions continue
loading until a match, true generator exhaustion, or that ceiling.

Category cards are variable-height TanStack Virtual rows rooted in the
persistent application scroll container. Stable mix-and-source ordinals,
dynamic measurement, responsive remeasurement, and one retained focused row
preserve filtering, keyboard focus, route navigation, and deep pixel
restoration while bounding mounted DOM. Filter measurement changes temporarily
suppress virtualizer scroll compensation, enforce the intentional top reset,
then restore normal deep-scroll compensation. The shell loads the restoration
controller through the existing lazy Suggestions module and restores on route
return, profile/layout ownership changes, and song-detail Back navigation. It
holds the target through late virtual measurements until the scroll position
is stable or user intent cancels. Canonical and trailing-slash Suggestions
paths share the same scroll owner. Pages without a restoration key no longer
share an anonymous fallback cache.

An internal `IntersectionObserver` sentinel observes against the application
scroll container with the shared prefetch distance. Each raw-category commit
re-arms the sentinel using the mix identity and category count, while page and
hook guards coalesce repeated observer notifications before React commits the
batch. Browsers without
`IntersectionObserver` receive a manual Load More control. The legacy
`react-infinite-scroll-component` and `throttle-debounce` dependency path is
removed. Per-card edge fading observes only mounted virtual wrappers, and the
global frosted-card hover path resolves and measures only the card under the
pointer instead of scanning the accumulated list.

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

Manual screenshots use PNG only as the authoring format under
`FortniteFestivalWeb/manual-assets/source/screenshots`. The source captures and
schema-v2 generation manifest are excluded from Docker and Vite deployment.
The public bundle contains 376 responsive WebP variants plus three canonical
PNGs retained only for the documented `song-detail-cards` legacy alias. All
supported Chromium, WebKit, and Firefox projects decode WebP, so each carousel
uses its full-resolution hashed WebP as the `<img>` fallback and retries it once
without the responsive `<source>` after a candidate decode failure.

`manual:images:check` verifies all 141 source hashes, PNG metadata, encoded WebP
dimensions/hashes, alias closure, the generated TypeScript map, and an exact
deploy file list. The generated public Manual directory is 17,080,853 bytes;
the embedded bundle, including physical legacy aliases, is capped at
18,000,000 bytes by the normal performance-budget check.

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
