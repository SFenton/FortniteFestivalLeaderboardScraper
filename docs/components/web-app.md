---
status: canonical
owner: web
last_verified: 2026-10-07
last_verified_commit: a4b8bf4d
sources:
  - FortniteFestivalWeb/src/pages/songs/modals/SortModal.tsx
  - FortniteFestivalWeb/src/pages/suggestions/components/SuggestionsMixLimit.tsx
  - FortniteFestivalWeb/src/pages/songs/components/SongsToolbar.tsx
  - FortniteFestivalWeb/src/pages/songs/modals/FilterModal.tsx
  - FortniteFestivalWeb/src/hooks/data/useFilteredSongs.ts
  - FortniteFestivalWeb/src/utils/songSettings.ts
  - FortniteFestivalWeb/package.json
  - FortniteFestivalWeb/.node-version
  - FortniteFestivalWeb/Dockerfile
  - FortniteFestivalWeb/src/main.tsx
  - FortniteFestivalWeb/src/index.css
  - FortniteFestivalWeb/src/components/notifications/MobileNotificationsModal.tsx
  - FortniteFestivalWeb/src/components/notifications/MobileNotificationsModal.story.tsx
  - FortniteFestivalWeb/playwright/gallery/main.tsx
  - FortniteFestivalWeb/src/utils/focusAppearance.ts
  - FortniteFestivalWeb/src/styles/focusAppearance.module.css
  - FortniteFestivalWeb/e2e/specs/accessibility/focus-appearance.spec.ts
  - FortniteFestivalWeb/e2e/specs/accessibility/view-all-headers.spec.ts
  - FortniteFestivalWeb/src/utils/viewAllCtaName.ts
  - FortniteFestivalWeb/src/App.tsx
  - FortniteFestivalWeb/src/App.module.css
  - FortniteFestivalWeb/src/appStyles.ts
  - FortniteFestivalWeb/src/components/common/StartupSplash.tsx
  - FortniteFestivalWeb/src/components/leaderboard/LeaderboardPaginationFooter.tsx
  - FortniteFestivalWeb/src/components/leaderboard/PaginatedLeaderboard.tsx
  - FortniteFestivalWeb/src/components/maintenance/BackendAvailabilityGate.tsx
  - FortniteFestivalWeb/src/components/maintenance/MaintenanceApp.tsx
  - FortniteFestivalWeb/src/components/shell/desktop/PinnedSidebar.tsx
  - FortniteFestivalWeb/src/components/shell/desktop/PinnedSidebar.module.css
  - FortniteFestivalWeb/src/components/page/PageQuickLinks.tsx
  - FortniteFestivalWeb/src/components/page/PageQuickLinks.module.css
  - FortniteFestivalWeb/src/utils/scrollViewport.ts
  - FortniteFestivalWeb/src/hooks/ui/useWheelHandoff.ts
  - FortniteFestivalWeb/src/hooks/ui/usePageQuickLinks.ts
  - FortniteFestivalWeb/src/hooks/ui/useScrollMask.ts
  - FortniteFestivalWeb/src/hooks/ui/useScrollFade.ts
  - FortniteFestivalWeb/src/components/leaderboard/LeaderboardPaginationFooter.tsx
  - FortniteFestivalWeb/src/pages/leaderboard/player/PlayerHistoryPage.tsx
  - FortniteFestivalWeb/src/pages/leaderboard/global/LeaderboardPage.tsx
  - FortniteFestivalWeb/src/pages/leaderboard/band/SongBandLeaderboardPage.tsx
  - FortniteFestivalWeb/src/pages/songinfo/components/InstrumentCard.tsx
  - FortniteFestivalWeb/src/pages/songinfo/components/SongBandLeaderboardPreview.tsx
  - FortniteFestivalWeb/src/utils/leaderboardPosition.ts
  - FortniteFestivalWeb/src/pages/suggestions/components/VirtualizedSuggestionsList.tsx
  - FortniteFestivalWeb/e2e/specs/responsive/desktop-scroll-panels.spec.ts
  - FortniteFestivalWeb/src/components/lazy/secondaryControls.ts
  - FortniteFestivalWeb/src/components/common/Accordion.tsx
  - FortniteFestivalWeb/src/components/shell/fab/MobileFloatingActionButton.tsx
  - FortniteFestivalWeb/src/components/shell/ShellScrollRestoration.tsx
  - FortniteFestivalWeb/src/components/shell/mobile/BottomNav.tsx
  - FortniteFestivalWeb/src/contexts/FabVisibilityContext.tsx
  - FortniteFestivalWeb/src/contexts/PageReadyContext.tsx
  - FortniteFestivalWeb/src/contexts/StartupEntranceContext.tsx
  - FortniteFestivalWeb/src/contexts/StartupSplashContext.tsx
  - FortniteFestivalWeb/src/hooks/ui/useInitialAppReveal.ts
  - FortniteFestivalWeb/src/pages/Page.tsx
  - FortniteFestivalWeb/src/pages/settings/SettingsPage.tsx
  - FortniteFestivalWeb/src/pages/settings/feedback/FeedbackModal.tsx
  - FortniteFestivalWeb/src/pages/settings/PrivacyPolicyModal.tsx
  - FortniteFestivalWeb/src/pages/settings/privacyPolicy.ts
  - FortniteFestivalWeb/src/pages/settings/SettingsServiceProgress.tsx
  - FortniteFestivalWeb/src/pages/settings/SettingsServiceProgress.module.css
  - FortniteFestivalWeb/src/pages/settings/serviceProgress.ts
  - FortniteFestivalWeb/src/pages/settings/serviceInfo.en.json
  - FortniteFestivalWeb/src/hooks/data/useServiceInfo.ts
  - FortniteFestivalWeb/src/hooks/data/useIncomingSongsRefresh.ts
  - FortniteFestivalWeb/src/pages/songs/SongsPage.tsx
  - FortniteFestivalWeb/src/hooks/ui/useScrollUpdateScheduler.ts
  - FortniteFestivalWeb/src/hooks/ui/useVirtualListScrollMargin.ts
  - FortniteFestivalWeb/e2e/specs/responsive/settings-progress.spec.ts
  - FortniteFestivalWeb/src/pages/shop/ShopPage.tsx
  - FortniteFestivalWeb/src/pages/shop/shopSort.ts
  - FortniteFestivalWeb/src/pages/shop/modals/ShopSortModal.tsx
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
  - FortniteFestivalWeb/src/contexts/
  - FortniteFestivalWeb/src/contexts/FeatureFlagsContext.tsx
  - FortniteFestivalWeb/playwright.config.ts
  - FortniteFestivalWeb/playwright.component.config.ts
  - FortniteFestivalWeb/playwright.publication.config.ts
  - FortniteFestivalWeb/e2e/specs/browser/startup-transition.spec.ts
  - FortniteFestivalWeb/e2e/support/startupSplash.ts
  - FortniteFestivalWeb/e2e/specs/browser/notification-rotation.spec.ts
  - FortniteFestivalWeb/e2e/specs/platform/publication.spec.ts
  - FortniteFestivalWeb/e2e/README.md
  - FortniteFestivalWeb/nginx.conf
  - FortniteFestivalWeb/scripts/verify-embedded-bundle.mjs
  - FortniteFestivalWeb/scripts/app-version.mjs
  - FortniteFestivalWeb/src/hooks/data/useVersions.ts
  - .github/workflows/publish-image.yml
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
2. `StartupSplashProvider`
3. `PublicationBoundary`
4. `BackendAvailabilityGate`
5. the application or a diagnostic fixture

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

`StartupSplashProvider` owns the only startup splash: a full-viewport,
solid-`--color-bg-app` surface with one centered `ArcSpinner`, mounted once at
boot and kept until application entry. Unresolved publication and
backend-availability stages render nothing of their own, and the application
drives the same element's reveal, so the spinner is never remounted between
stages. A remount restarts the spinner's rotation from a render-time phase;
on phone-class CPUs the slow application mount made the arc visibly snap back.
The splash does not expose the maintenance title, status copy, or logo
treatment. While the publication is unresolved, the same element is the one
visually hidden polite loading announcement; later stages leave it
accessibility-silent. Actual publication or availability failures still render
the full maintenance experience, which removes the splash while mounted;
recovery restores the covered splash.

After the application mounts, `PageReadyProvider` starts each route as not
ready. The shell remains `opacity: 0`, `inert`, and `aria-hidden` behind the
shared startup splash while the active page and lazy animated background
prepare; the application reports its reveal phase to `StartupSplashProvider`.
When the page publishes its terminal content-ready state, the shell
becomes opaque underneath the still-covered splash and only the splash fades
for the shared 300 ms transition. Completion accepts the splash's own opacity
`transitionend` and has a 100 ms safety fallback; reduced-motion users enter
without the fade. Entry is latched for the lifetime of the mounted app, so
normal route readiness resets never replay the startup surface. Unmounting the
application, such as the publication-change remount, restores the covered
splash for the next mount.

Songs publishes readiness at `LoadPhase.ContentIn`; route-error fallbacks
publish terminal readiness immediately, while unsupported URLs redirect to
Songs and complete through its normal readiness path.
`StartupEntranceContext` also prevents First Run, changelog, and fixed
leaderboard-footer body portals from mounting before entry. First Run may
reserve eligibility while hidden and retains priority over the changelog when
both are eligible.

## Routes

The route tree covers:

- songs, song detail, solo leaderboards, band leaderboards, and player history;
- player profiles, statistics, rivals, suggestions, and competition views;
- global, family, combo, and band rankings;
- band lookup, band detail, and player-band views;
- shop, optional manual, settings, licenses, and the privacy policy.

Use `src/routes.ts` for route construction and `src/App.tsx` for the rendered
tree. Static destinations and route-family matchers stay centralized in
`src/routes.ts`; route title/announcement metadata stays in
`src/routeMetadata.ts`. `RouteBoundary` gives every normal route, including
the eager Songs page, the standard recoverable error UI. `RequirePlayer` and
`RequireSelection` own access redirects with replace semantics, and the
wildcard route uses the same replacement redirect for unsupported URLs so they
land on `/songs`. Route and tab ownership normalize trailing slashes, and
Licenses remains owned by the Settings tab. `/settings/privacy` is a modal
route: it renders the Settings page with the privacy policy modal open.
`getRoutePageOwner` maps it to `/settings`, so `RouteBoundary`, shell scroll
reset, the mobile header, and the Settings FAB treat both URLs as one page and
Settings neither remounts nor scrolls to the top when the modal opens or
closes. In-app opens push the route and close with history Back; direct visits
close by replacing the URL with `/settings`. The policy text is owned by
[Privacy policy](../reference/privacy-policy.md). App Manual and in-app
feedback are the web features exposed through `/api/features`; feedback
controls are hidden unless the public `feedback` flag is true.

### Selected-row leaderboard navigation

Solo and band song leaderboards share one rule for the selected profile's own
row. When that row is not visible, tapping it jumps to its position: the extra
row under a Song Detail instrument or Duos/Trios/Quads preview, and the full
leaderboard footer when the row is on another page. The jump opens the full
board at the page containing the rank (`getLeaderboardPageForRank`, 25 rows per
page) with `navToPlayer=true` or `navToBand=true`, then scrolls the highlighted
row into view and clears the flag. A jump URL's `page` wins over the remembered
solo leaderboard page. When the row is already visible, tapping the footer
opens the profile: Statistics for a player and the band page for a band (which
resolves to Statistics when the band itself is selected). Rows for other
players and bands always open that player's or band's page. Accessibility
labels name the destination, for example "Jump to your band's position" versus
"Open band".

Card section headers on Rivals, Rival Detail, the Leaderboard Rivals tab and
Compete are single `role="button"` targets that read their title, then
"View All" (never "See All"). They are at least 44 pixels tall. An instrument
icon next to a visible instrument title is decorative (`InstrumentHeader`
`decorativeIcon`), so the name does not repeat the instrument. The full-width
"View all rivals" and "View full leaderboards" calls to action keep their
visible text but get an accessible name from `viewAllCtaName(label, card)`, for
example "View all rivals, Lead Rivals". This matches the native apps'
`ViewAllCta.Name` rule, so buttons in different cards stay distinct.

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

### Desktop panels and native scrolling

The wide shell is active at 1440px and above only when mobile chrome is not
active. Mobile/PWA chrome remains compact even on a wide viewport; Quick Links
producers use that same distinction rather than width alone.

Desktop navigation and the profile/Manual/Settings utility group are separate
content-height panels anchored to the top and bottom of the left rail.
Their full-height positioning frame is pointer-transparent. When vertical
space is constrained, both panels can scroll internally instead of shrinking
their controls or clipping the selected band's members and utility actions.
The existing exported navigation/control styles remain available to the
first-run navigation demo; new panel geometry is owned by CSS Modules.

The right portal retains the full available height for measurement but does
not intercept input. Its rendered Quick Links surface uses only the required
height, capped to that budget; long lists such as Songs A–Z scroll internally.
The reveal gate remains on the content-height root, and descendants inherit
its pointer state until reveal completes. Existing inline `--frosted-card`
markers stay on controls, not on rail frames or entire panels.

Actual panel surfaces use native overflow and scroll containment. Wheel input
over their controls or padding does not scroll main, including fitting lists
and scroll boundaries. Empty rail space reaches the browser's native main
scroller; there is no JavaScript wheel forwarding or artificial scroll range.
Activating a navigation or Quick Links control still performs its intentional
route or section navigation.

Main scrolling belongs to `[data-testid="app-scroll-container"]`, not the
document or `main#main-content`. In the wide shell, a transparent top border
equal to the page-header portal height extends that element's native hit area
into the empty header-side gutters. The visible client viewport and content
start remain below the header, while the center header remains independent.
The wide center header also uses native overflow containment. Firefox can
retain an already-started page wheel transaction after the pointer enters
overlay chrome. A Firefox-only handoff fence consumes the first wheel after
page-to-panel transfer; subsequent wheels scroll the panel natively. Its page
listener is passive, cancellation is attached only to actual panels/header,
and no wheel deltas are forwarded. Pointer/key actions reset that ownership,
and Ctrl-wheel zoom is left native. The intentional tradeoff is that the first
handoff wheel ends the prior transaction rather than moving the panel.
Its border box and client viewport therefore have different top/height values.
`src/utils/scrollViewport.ts` owns client-viewport bounds and size observation
for section offsets/visibility, masks, edge fades, virtual-list margins, and
fixed-footer clearance. Songs, Suggestions, and Player History virtualizers
observe client size, including header changes that leave the border box fixed.
Compact/mobile shells retain their existing borderless scroll layout.

### Shared interaction contracts

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
matching, titles, and mobile chrome labels; unknown locations use Songs
metadata while the wildcard replacement redirect settles. A visually hidden
fallback H1 covers lazy/mobile gaps and self-removes whenever a page-owned
visible H1 is present.

Focus ownership and focus appearance are separate. `installFocusAppearance`
runs before React in both the application and component gallery. Its
document-level capture listeners keep passive startup and pointer interaction
visually quiet, including body-portaled dialogs and lazy loading replacements.
The CSS override is limited to interactive controls and the main/dialog
structural focus targets; it does not blur elements, change initial targets,
alter modal traps/return focus, or remove keyboard and screen-reader semantics.
The document marker is not persisted and survives app-managed focus transfers
until the next relevant input, rather than disappearing when a launcher blurs.
Its CSS Module root class is explicitly bound to `documentElement`, so the
production build retains the stylesheet; a global-only side-effect import is
not sufficient. Disposal restores both the prior scope class and marker.

Keyboard navigation/activation restores native `:focus-visible` presentation
before component handlers run. Printable, composing/IME, and text-editing keys
do not masquerade as navigation; Tab and Escape can leave text-entry mode.
Trusted, pointer-free, zero-detail browser activation falls back to native
presentation rather than inheriting stale touch suppression or being assumed
to be a keyboard. Untrusted application-generated clicks do not change that
provenance: Export Data's temporary download anchor, for example, is not new
user input. This does not cancel the click or change download/focus behavior.
Forced-colors mode bypasses the quiet override. The rule uses no `!important`
and leaves text-field styling, caret/selection, selected/error colors, and
decorative shadows intact. Native accessibility overlays are outside this
application styling policy.

The blue keyboard outline remains intentional, including custom `role="link"`
controls. Native tap highlighting is independently made transparent on `html`
so wrappers, custom links, and text fields inherit the same policy. The neutral
press pulse remains unchanged. Do not replace this separation with blanket
outline removal, device-width heuristics, or delayed blur. Forcing native
`focusVisible: false` is also insufficient: engines differ when keyboard input
reaches that same focused element, and re-focusing it does not reliably restore
the indicator without a focus transition.

All semantic modal dialogs inherit `-webkit-text-size-adjust: 100%` and
`text-size-adjust: 100%` through `[aria-modal='true']` in `src/index.css`. This
holds authored modal text sizes through viewport rotation without changing
ordinary page typography or disabling viewport zoom. It applies to shared
`ModalShell` panels and independent first-run, Paths, confirmation, and
changelog dialogs. The rule does not remount open dialogs or change the
keyboard-stable mobile sheet position; the notification rotation browser
regression checks the retained panel and rendered text geometry.

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
The Item Shop uses `auto`; its FAB always carries the Sort action once the
page registers its actions, so handsets reserve FAB clearance there, and any
state without a renderable FAB keeps only the list's normal bottom padding.

`ShellScrollRestoration` owns route/layout scroll resets, preserve-scroll keys,
and the lazy Suggestions restoration coordinator outside `App.tsx`. Resets key
on the route's page owner, so modal routes such as `/settings/privacy` keep the
underlying page's scroll position.
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

The web feedback client exposes `submitFeedback` and `getFeedbackStatus`.
`submitFeedback` sends multipart `POST /api/feedback` requests through
`XMLHttpRequest` so upload progress is available to the UI, while status
polling remains a normal React/API-client JSON read against
`GET /api/feedback/{id}`.

### Song filters

The Songs sort modal offers Has FC only when a player or band profile is
selected, independent of whether score data has loaded. Without a selected
profile, a saved Has FC mode uses Title sorting, and Has FC plus its primary
instrument order controls are hidden.

The desktop Songs toolbar and mobile action dock always include Filter once
the page controls are revealed, including without a selected player or band
and before score data loads. Without a selected profile, the modal shows only
General and its filters. Selecting a player or band makes the other applicable
filter sections available, even while score data is loading.

The Songs filter modal keeps its draft in `SongsPage`, with applied filters
persisted in browser song settings. General applies across anonymous, solo,
and selected-band views and combines with search, score, and instrument
filters. Its dropdown order is Year, Duration, Item Shop, and Double Bass.
Dropdowns are inset on both sides; toggle rows use the shared accordion left
indent while switches stay aligned with the dropdown right edge.

Year derives its decade options from the full catalog's `ServerSong.year`
metadata, sorted chronologically, so newly represented decades appear without
a web change. The 1900s through 1960s remain hidden until songs from those
decades appear in the loaded catalog. Duration uses
`ServerSong.durationSeconds`: Under 1 Minute,
1-2 Minutes through 9-10 Minutes, and 10+ Minutes only when a catalog song is
at least 600 seconds long. Ranges include their lower bound and exclude the
upper bound. All decade and duration toggles default on, including newly
introduced buckets. Each dropdown includes Select All and Clear All. Select
All enables every bucket; Clear All disables every bucket. Each toggle works
independently, including the last enabled option. All-off choices persist
without automatic repair and can produce an empty song list. Turning a
bucket off excludes that bucket. Unknown or invalid metadata remains included
with all toggles on and is excluded when
that metadata filter is restricted.

Item Shop respects the hide-shop setting and uses the shared shop snapshot,
independent of profile selection. Available in Item Shop and Not Available in
Item Shop both default on and toggle independently. Both on includes all
songs; both off excludes all songs, even while shop data is pending.
Classification waits for a loaded shop snapshot rather than treating pending
data as unavailable.
This replaces the previous In the Shop and Leaving Tomorrow filter section;
older saved restrictions migrate to available-only, with no hidden legacy
restriction retained. Shop badges and leaving-tomorrow display remain intact.

Double Bass Support and No Double Bass Support both default on and toggle
independently. Both off excludes all songs. Both on applies no chart
restriction and includes unknown support metadata. With one category selected, only explicit `true`
or `false` in `ServerSong.doubleBassSupported` matches that category; absent
or null metadata matches neither. Existing saved single-category or
unrestricted choices migrate to independent boolean selections. Reset
restores all General choices in solo and band views.

### Item Shop sort

The Item Shop offers the general Songs sort modes — Title, Artist, Year, and
Duration — with an ascending/descending direction. Desktop shows a Sort pill
in the Shop header beside the song count and Grid/List toggle; mobile chrome
adds a Sort action to the Shop FAB at every width (the Grid/List action stays
hidden below the narrow-grid breakpoint). Both open `ShopSortModal`, which
uses the same radio rows, direction selector, Reset/Apply, and discard
confirmation as the Songs sort modal.

`pages/shop/shopSort.ts` owns the comparator, used by both grid and list. It
matches the Songs list: missing year or duration sorts as zero, and ties fall
back to title in the chosen direction. The shop feed (`ShopSong`) has no
duration, so Shop entries are joined to the published song catalogue by
`songId` for `durationSeconds` (and a missing year). List rows therefore show
artist, year, and duration like Songs rows. The choice persists in
`localStorage` under `fst:shopSort`, separately from Songs sort settings;
invalid stored values fall back to Title ascending. The Sort control is
highlighted when the choice differs from that default.

### Settings service progress

Settings keeps `useServiceInfo('settings')` as its sole request owner on the
shared React Query key. Visible Settings polling is five seconds; hidden-page
polling is throttled to 30 seconds. No WebSocket or page-owned duplicate fetch
is added, and publication-boundary cache/reset ownership is unchanged.

### Incoming songs

`FestivalContext` also loads `/api/songs/incoming` (query key
`['songs','incoming']`, one-minute stale time, five-minute refetch) and appends
songs missing from the published catalog with `awaitingPublication: true`, so a
song ingested mid-scrape or mid-post-process is listed and routable immediately
with empty leaderboards until publication. On the Songs page,
`useIncomingSongsRefresh` subscribes to the shared application WebSocket and
invalidates only that query after `songs_changed`.
The song detail page skips publication-bound leaderboard, band, member-score,
and score-history reads for such a song (during a scrape freeze those routes
would otherwise return `503` because the song is absent from the publication
cache) and renders empty cards without blocking page readiness.
Published entries always win, so metadata changes and removals still wait for
publication, and an incoming-list failure never blocks the published catalog.

Catalog publication lag (`serviceInfo.catalog`) is operational state only. The
Songs page does not show a catalog-update banner or any count of changes
awaiting publication; `e2e/specs/pages/songs/catalog-lag.spec.ts` keeps that
true with pending added, changed, and removed songs.

Unit-test setup replaces Node's native WebSocket with an inert implementation,
so page tests cannot make real `/api/ws` connections. Dedicated WebSocket
tests continue to install their own behavioral mocks.

The service area uses one flat `FrostedCard` with no tinted or bordered
subcards. Inside it, three ToggleRow-style entries reuse the same title,
subtitle, spacing, and trailing-value treatment as the rest of Settings:

1. `Leaderboard Service State`, with the friendly phase as its subtitle and
   `Updating` plus the shared spinner, `Idle`, or `Stopped` on the right.
2. The friendly `Phase · Subphase` label, followed only by the progress bar.
   Identical labels collapse to one. Registered-player band discovery adds one
   muted line below the bar with attempted-this-pass, temporarily unavailable,
   and durable completed lookup counts.
3. `Last Successful Publication`, with browser-local date/time and the local
   short timezone abbreviation as its subtitle.

The browser uses stable phase/subphase IDs for localization, including the two
dynamic rank-history cleanup families, and humanizes unknown IDs instead of
showing raw snake case. A named subphase consumes only
`currentUpdate.subphaseProgress`; it never inherits the parent
`phasePercent`. Exact progress requires `kind=exact`, a final denominator, and
a valid percentage. `indeterminate` keeps the animated bar, while
`not_applicable` omits the bar. Legacy named-subphase payloads remain
indeterminate.

The progress row intentionally has no visible percentage, generic unit count,
ETA, overall estimate, phase-state subtitle, or other status text. The sole
phase-specific exception is registered-player band discovery, whose concise
attempt summary explains why durable progress can remain at 0% after Epic
returns retryable unavailable responses. Exact counts remain available through
the progress bar's accessible value text. Display memory is keyed by operation,
scrape, plan, phase attempt, subphase ID, and subphase epoch; lower sequences
and older timestamps cannot regress the bar or attempt summary, while a new
identity can reset from a higher value to a lower truthful value.

Operational IDs, raw timestamps, attempts, model diagnostics, technical
disclosures, current-update timing, next-schedule timing, and selected-profile
rival/sync status are not rendered. Settings therefore does not add a
selected-profile sync polling loop; profile-name refresh and export controls
keep their existing selected-profile ownership.

When `/api/features` returns `feedback: true`, App Settings also shows
`Report an Issue` and `Request a Feature`. Each entry opens an accessible modal
that keeps the `[Bug] ` or `[Feature] ` title prefix editable, collects the
required description, and lets users attach up to four image/video files with a
90 MiB total request budget. Attachments render as local object-URL thumbnails
above the Attach Media button, open in a new browser tab, and revoke object
URLs on removal or unmount. Closing a dirty form uses the shared confirm alert;
successful submissions can close without confirmation. The modal submits
multipart data with upload progress, then polls feedback status until an issue
number is available, failure is reported, or the five-minute client timeout is
reached.

English shell/common/Songs resources remain eager in the i18next `translation`
namespace. App Manual, Settings, and First Run resources use named namespaces
registered synchronously by their lazy page/carousel owners. The Settings
namespace also owns the co-located service-progress vocabulary. Existing key
paths remain unchanged because route components use translation-first namespace
fallback; direct-route and replay browser tests reject any visible untranslated
key. The production graph requires all three JSON resources to stay outside the
entry and inside their declared lazy owner closures.

Focused unit and Playwright coverage owns v1/v2 rendering, exact and unknown
denominators, ETA suppression, failures/restarts, suppression of internal
best-effort warning counts, absence of technical and selected-profile sync
surfaces, shared-request concurrency, one-card overflow at 320, 375, 768, and
1440 pixels, and determinate desktop/mobile axe coverage.

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
evicts the user-visible backscroll range. The action's button is described by
the limit message, and because a successful fresh mix unmounts it, focus moves
to the route's labelled `main#main-content` (the same target as PUSH route
focus) instead of falling to `<body>`. Filtered-empty sessions continue
loading until a match, true generator exhaustion, or that ceiling.

Category cards are variable-height TanStack Virtual rows rooted in the
persistent application scroll container. Stable mix-and-source ordinals,
dynamic measurement, responsive remeasurement, and one retained focused row
preserve filtering, keyboard focus, route navigation, and deep pixel
restoration while bounding mounted DOM. Filter measurement changes and
fresh-mix identity changes temporarily suppress virtualizer scroll
compensation, enforce the intentional top reset, then restore normal
deep-scroll compensation. Rapid back-to-back identity commits and list unmount
keep compensation suppressed so stale late measurements cannot pull the list
away from the top. The shell loads the restoration
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
caching, and falls back to `index.html` for client routes. A dedicated
`^~ /api/feedback` location precedes the general API proxy so feedback uploads
can stream without request buffering and can use a 92 MiB Nginx body limit plus
300-second proxy send/read timeouts.

FSTService can also serve an embedded `wwwroot` bundle when one is present; see
[ADR 0004](../decisions/0004-web-deployment-modes.md).

### App version

Every published web image carries an automatic app version. The publish
workflow's `version-bump` job counts the first-parent commits on `master` at
the target SHA (`git rev-list --count --first-parent`). Every merge adds at
least one first-parent commit, so consecutive releases get distinct,
increasing counts. `build-and-push-web` passes that count and the target SHA to
the web Dockerfile as `FST_APP_BUILD_NUMBER` and `FST_APP_COMMIT`.
`scripts/app-version.mjs` then:

- replaces the `package.json` patch with the build number (`0.1.135` with build
  `1603` becomes `0.1.1603`);
- shortens the commit to seven characters.

Settings → App Version shows `<version> · <commit>`, and the What's New title
shows the same version. The Settings version card (`SettingsVersionList`) is a
description list: each label is a `dt` and each value its `dd`, in label →
value order. The `·` is hidden from assistive technology and a visually hidden
"commit" word is announced instead (`<version>, commit <sha>`). Rows wrap so
labels and values stay inside the card at 320 CSS px with doubled text.

Builds without a valid build number keep the plain `package.json` version and
omit the commit. That covers local development, tests, and the committed
embedded `wwwroot`. As a result, `embedded:check` stays deterministic and the
workflow never writes back to `master`. Only `package.json` major/minor changes
are meaningful; the patch is a local fallback. The web image is published only
when web-affecting paths change, so a service-only merge does not change the
deployed web version.

The component gallery is a Vite development/test input, not a production build
entry. Standalone production Nginx returns 404 for `/playwright/gallery` and
descendants instead of serving the SPA fallback. `embedded:check` verifies
that the committed `wwwroot` has no gallery and that both Nginx guards remain.

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

The local Vite gallery can open a story directly with
`?story=<component/path/export>`.
The Notifications `RotationPreview` story uses local media and no API, so a
developer can inspect the dialog in a browser without a service or worker.

Validation commands are in [Testing](../testing/README.md).
