# FortniteFestivalWeb — Architecture Patterns

> Last updated: 2026-04-03

## Project Structure

```
FortniteFestivalWeb/src/
├── api/              # API client, React Query setup, page-level UI caches
│   ├── client.ts         # fetch-based API client (all endpoints)
│   ├── queryClient.ts    # QueryClient singleton
│   ├── queryKeys.ts      # Typed query key factory
│   └── pageCache.ts      # Page-level UI state caches (scroll, animation)
├── components/       # Shared, reusable UI components
│   ├── common/           # Primitives: EmptyState, FrostedCard, ArcSpinner, PageHeader, SearchBar, Accordion, etc.
│   ├── display/          # Instrument display: InstrumentChip, InstrumentHeader, InstrumentIcons
│   ├── firstRun/         # FirstRunCarousel component
│   ├── leaderboard/      # Leaderboard table components
│   ├── modals/           # App-wide modals: ChangelogModal, ConfirmAlert, Modal, modalStyles
│   ├── page/             # Page infrastructure: ErrorBoundary, LoadGate, BackgroundImage, FadeIn, RouteErrorFallback, SyncBanner
│   ├── player/           # Player-specific components
│   ├── routing/          # FeatureGate (flag-gated routes)
│   ├── shell/            # App shell: desktop/, mobile/, fab/, AnimatedBackground, HamburgerButton
│   │   ├── desktop/      # DesktopNav, Sidebar, PinnedSidebar, HeaderSearch, HeaderProfileButton
│   │   ├── mobile/       # MobileHeader, BottomNav, BackLink, MobilePlayerSearchModal
│   │   └── fab/          # FloatingActionButton, FABMenu
│   └── songs/            # Song display components (SongRow, etc.)
├── contexts/         # React contexts (9 total)
├── firstRun/         # First-run experience types and demo data
├── hooks/            # Custom hooks organized by domain
│   ├── chart/            # Chart-specific: useChartData, useChartDimensions, useCardAnimation, useListAnimation
│   ├── data/             # Data fetching/state: useTrackedPlayer, useFilteredSongs, useShopState, useLoadPhase, etc.
│   ├── navigation/       # Route helpers: useNavigateToSongDetail
│   └── ui/               # UI behavior: useStaggerStyle, useModalState, useScrollMask, useIsMobile, useMediaQuery, etc.
├── i18n/             # i18next setup (single English locale)
├── pages/            # Feature pages (each owns components/, modals/, firstRun/, helpers/)
│   ├── Page.tsx          # Shared page shell (THE canonical pattern)
│   ├── PageMessage.tsx
│   ├── songs/            # SongsPage + components/, modals/, firstRun/
│   ├── songinfo/         # SongDetailPage
│   ├── leaderboard/      # global/LeaderboardPage, player/PlayerHistoryPage
│   ├── player/           # PlayerPage + components/, helpers/
│   ├── rivals/           # RivalsPage, RivalDetailPage, RivalryPage, AllRivalsPage + components/, helpers/
│   ├── suggestions/      # SuggestionsPage
│   ├── compete/          # CompetePage
│   ├── leaderboards/     # LeaderboardsOverviewPage, FullRankingsPage
│   ├── shop/             # ShopPage
│   └── settings/         # SettingsPage
├── routes.ts         # Centralized route path constants + regex patterns
├── styles/           # Global styles: theme.css, animations.css, CSS modules
├── utils/            # Pure utilities: apiError, formatters, songSettings, songSort, etc.
├── App.tsx           # Root component: provider stack, routing, shell layout
├── appStyles.ts      # Shell layout style objects
└── main.tsx          # Entry point: StrictMode + createRoot
```

### Feature Page Organization

Each feature page directory follows a consistent internal structure:
- `{Feature}Page.tsx` — Main page component (default export)
- `components/` — Page-specific child components
- `modals/` — Page-specific modal dialogs (SortModal, FilterModal, etc.)
- `firstRun/` — First-run slide definitions for that page
- `helpers/` — Page-specific utilities and types

## Routing

### Router: HashRouter (react-router-dom v7)
- Uses `HashRouter` (not BrowserRouter) — hash-based routing for static hosting compatibility
- Route tree defined inline in `App.tsx → RoutesContent()`, shared between mobile and wide-desktop layouts

### Route Constants: `src/routes.ts`
- `Routes` object: static paths and parameterized path builders (functions)
- `RoutePatterns` object: regex patterns for route matching (used by FAB, back-navigation, animated backgrounds)
- All route references use these constants — no hardcoded path strings in components

### Code Splitting: `React.lazy()` + `<Suspense>`
- **Eagerly loaded**: `SongsPage` (landing page, always needed)
- **Lazy loaded**: All other pages — `SongDetailPage`, `LeaderboardPage`, `PlayerHistoryPage`, `PlayerPage`, `SuggestionsPage`, `SettingsPage`, `ShopPage`, `RivalsPage`, `RivalDetailPage`, `RivalCategoryPage`, `AllRivalsPage`, `LeaderboardsOverviewPage`, `FullRankingsPage`, `CompetePage`
- `<Suspense fallback={<SuspenseFallback />}>` wraps the entire `<Routes>` block
- Each route wrapped in `<ErrorBoundary fallback={<RouteErrorFallback />}>`

### Feature-Gated Routes
- `<FeatureGate flag="...">` wraps routes requiring feature flags (shop, rivals, compete, leaderboards)
- Redirects to `/songs` when flag is disabled

### Player-Conditional Routes
- Routes requiring a tracked player (rivals, statistics, suggestions, compete) conditionally render `<Navigate>` when no player is set

## State Management

### React Query (TanStack Query v5)
- **QueryClient** (`api/queryClient.ts`): `staleTime: 5min`, `gcTime: 10min`, `retry: 1`, `refetchOnWindowFocus: false`
- **Query Key Factory** (`api/queryKeys.ts`): All keys produced by typed factory functions — `queryKeys.songs()`, `queryKeys.player(accountId, ...)`, `queryKeys.leaderboard(...)`, etc.
- **Invalidation**: Targeted invalidation via `qc.invalidateQueries({ queryKey: queryKeys.xxx() })`

### React Contexts (9 total, wrap order matters)

Provider stack in `App()`:
```
QueryClientProvider(queryClient)
  FeatureFlagsProvider          ← fetches /api/features, dev mode: all-on + localStorage overrides
    SettingsProvider             ← app preferences in localStorage
      FestivalProvider           ← songs data via React Query + localStorage ETag cache
        ShopProvider             ← shop data via REST + WebSocket
          FirstRunProvider       ← first-run experience state
            FabSearchProvider    ← FAB action registration (refs, no re-renders)
              SearchQueryProvider ← search input state (isolated from FAB)
                HashRouter
                  ScrollContainerProvider ← shell scroll ref + header portal ref
                    AppShell     ← the actual app
```

Inside `AppShell`:
```
PlayerDataProvider(accountId)   ← player data + sync status via React Query
  {shell + routes}
```

### Context Patterns

| Context | Purpose | State Source |
|---|---|---|
| `FeatureFlagsProvider` | Feature flag values | React Query (prod) / localStorage (dev) |
| `SettingsProvider` | User preferences (instruments, metadata visibility, filters) | localStorage |
| `FestivalProvider` | Song catalog + current season | React Query + localStorage ETag cache |
| `ShopProvider` | Item shop state | REST API + WebSocket |
| `FirstRunProvider` | First-run experience registration + seen state | localStorage |
| `FabSearchProvider` | FAB action registration (sort, filter, etc.) | Refs (no re-renders) |
| `SearchQueryProvider` | Search query input state | useState (isolated to prevent keystroke re-renders) |
| `ScrollContainerProvider` | Shell scroll container ref + header portal DOM node | Refs + ResizeObserver |
| `PlayerDataProvider` | Player data + backfill sync status | React Query + polling |

### Local State Conventions
- **Tracked player**: `useTrackedPlayer()` — localStorage + cross-tab sync via custom events
- **Song settings**: `songSettings.ts` — localStorage + custom `SONG_SETTINGS_CHANGED_EVENT` for cross-component sync
- **Page UI state**: `pageCache.ts` — in-memory Maps for scroll position, animation flags (NOT data caches)
- **Modal state**: `useModalState<T>(defaults)` — open/close + draft pattern

## API Layer

### Client: `api/client.ts`
- Pure `fetch`-based, no axios
- Base URL: empty string (`''`) — requests go to same origin, Vite dev proxy forwards `/api` to backend
- Two fetch helpers: `get<T>(path)`, `post<T>(path)`
- ETag support: `getWithETag<T>(path)` with in-memory `etagCache` Map
- Songs: special localStorage cache + ETag for instant render on reload
- Display name normalization: `normalizeDisplayName()` replaces empty names with 'Unknown User'
- Album art URL expansion: `expandAlbumArt()` prepends CDN prefix to relative paths
- All methods properly encode URI components

### Wire format expansion
- `expandWireSongsResponse()`, `expandWirePlayerResponse()`, `expandWireStatsResponse()` from `@festival/core/api/serverTypes`
- Expands compact wire format to full objects client-side

### Error Handling
- API errors: `throw new Error(\`API \${status}: \${statusText}\`)` — parsed by `parseApiError()` which extracts HTTP status and maps to i18n error categories
- Error display: `<EmptyState>` with `parseApiError()` — localized title + subtitle
- Route errors: `<ErrorBoundary fallback={<RouteErrorFallback />}>` wraps each route

## Component Patterns

### Page Shell: `Page.tsx` (THE canonical pattern)
Every page uses the shared `<Page>` component:
```tsx
<Page
  scrollRestoreKey="songs"       // auto scroll restore on back-nav
  scrollDeps={[items.length]}    // scroll mask dependencies
  loadPhase={phase}              // auto spinner during Loading/SpinnerOut
  firstRun={{ key, label, slides, gateContext }}  // auto first-run carousel
  before={<PageHeader ... />}    // content before scroll area (portaled to header)
  after={<SortModal ... />}      // content after scroll area (modals, footers)
  background={<BackgroundImage />} // background content
  headerCollapse={{ onCollapse }} // scroll-linked header collapse
  fabSpacer="end"                // bottom spacing for FAB
>
  {content}
</Page>
```

Page handles: scroll mask, stagger rush, scroll restoration, header collapse CSS variable, first-run experience, load phase spinner, FAB spacer.

### Loading Pattern: `useLoadPhase()`
State machine: `Loading → SpinnerOut → ContentIn`
- `Loading`: Show `ArcSpinner` (via `<Page loadPhase>` or `<LoadGate>`)
- `SpinnerOut`: Fade spinner out (SPINNER_FADE_MS)
- `ContentIn`: Show content, enable stagger animations

### Stagger Animation Pattern
- `useStaggerStyle(delayMs, { skip })` — hook for single items
- `buildStaggerStyle(delayMs)` — pure function for `.map()` loops
- `clearStaggerStyle` — onAnimationEnd handler to clean up inline styles
- `staggerMs(index, step, offset, base)` — delay calculator
- `useStaggerRush()` — attached to scroll container, allows resetting stagger on route changes

### Modal Pattern: `useModalState<T>(defaults)`
```tsx
const { visible, draft, setDraft, open, close, reset } = useModalState(defaultValues);
// open(currentValues) → copies to draft → edit draft → apply or cancel
```

### Styling Hierarchy
1. **CSS Modules** (`.module.css`) — for components with ≥3 style rules, animations, pseudo-elements
2. **Theme tokens** — `@festival/theme` exports JS constants (`Colors`, `Font`, `Gap`, `Layout`, `Size`, etc.)
3. **CSS custom properties** — `theme.css` defines `:root` variables consumed by CSS modules
4. **`useStyles()` pattern** — `useMemo`-based style object factories for inline styles
5. **`appStyles.ts`** — shell layout style objects

> Lesson: ≥3 rules → CSS module, <3 → inline with theme tokens

### Responsive Layout
- `useIsMobile()` — viewport ≤ MOBILE_BREAKPOINT (layout decisions)
- `useIsMobileChrome()` — mobile chrome (bottom nav, FAB, mobile header) — always on iOS/Android/PWA
- `useIsWideDesktop()` — viewport ≥ WIDE_DESKTOP_BREAKPOINT (pinned sidebar)
- Three layout modes: **mobile** (bottom nav + FAB), **desktop** (top nav + sidebar), **wide desktop** (pinned sidebar + overlay architecture)

### Shell Architecture
- **Mobile**: `MobileHeader` + `BottomNav` + `FloatingActionButton` (contextual actions per route)
- **Desktop**: `DesktopNav` + `Sidebar` (hamburger toggle)
- **Wide Desktop**: `PinnedSidebar` (always visible) + scroll container with overlay architecture (sidebar + header overlays with `pointer-events: none`)

### FAB (Floating Action Button) System
- **Context-driven**: `FabSearchContext` — pages register their actions via `registerXxxActions()`, FAB reads them
- **Route-aware**: `App.tsx` configures FAB `actionGroups` based on current route pattern
- **Isolated search**: `SearchQueryContext` separated from `FabSearchContext` to prevent keystroke re-renders

## Build & Tooling

### Vite (v6.2+)
- Build output: `../FSTService/wwwroot` (served as static files by ASP.NET Core)
- Dev server: port 3000, proxy `/api` → `VITE_API_BASE` (default `http://localhost:8080`)
- API key injection: `VITE_API_KEY` env var → `X-API-Key` header on proxied requests
- Define constants: `__APP_VERSION__`, `__CORE_VERSION__`, `__THEME_VERSION__` from package.json files

### TypeScript (v5.7, strict mode)
- Target: ES2020, module: ESNext, moduleResolution: bundler
- Strict settings: all enabled + `noUnusedLocals`, `noUnusedParameters`, `noUncheckedIndexedAccess`, `noFallthroughCasesInSwitch`, `forceConsistentCasingInFileNames`
- Path aliases: `@festival/core`, `@festival/theme`, `@festival/ui-utils` → monorepo packages
- Stubs: `react-native` and `react-native-app-auth` → empty stubs (shared code compatibility)

### ESLint (v10, flat config)
- TypeScript recommended + React hooks + React refresh
- `react-hooks/rules-of-hooks`: **warn** (not error — some pre-existing conditional hook calls need refactoring)
- `react-hooks/exhaustive-deps`: **warn**
- `react/forbid-dom-props: ['style']`: **warn** (CSS module migration in progress)
- `no-magic-numbers`: **warn** with extensive ignore list for common UI values
- `no-restricted-imports`: **error** — blocks deprecated `INSTRUMENT_SORT_MODES` / `METADATA_SORT_DISPLAY` imports

### Stylelint
- Standard config + `stylelint-declaration-strict-value` (enforces CSS variable usage)

### Testing
- **Unit**: Vitest v4 + jsdom + @testing-library/react
  - Coverage: v8 provider, 95% threshold per file (lines/branches/statements/functions)
  - Setup: `__test__/setup.ts`
- **E2E**: Playwright (config at `playwright.config.ts`)
- `/* v8 ignore start/stop */` comments extensively used to exclude shell/integration code from coverage

### Package Manager
- Yarn 4.12 (Berry) with PnP

## Provider Stack

Full provider wrap order (outermost → innermost):
```
<StrictMode>
  <QueryClientProvider client={queryClient}>
    <FeatureFlagsProvider>
      <SettingsProvider>
        <FestivalProvider>
          <ShopProvider>
            <FirstRunProvider>
              <FabSearchProvider>
                <SearchQueryProvider>
                  <HashRouter>
                    <ScrollContainerProvider>
                      <PlayerDataProvider accountId={player?.accountId}>
                        {app content}
                      </PlayerDataProvider>
                    </ScrollContainerProvider>
                  </HashRouter>
                </SearchQueryProvider>
              </FabSearchProvider>
            </FirstRunProvider>
          </ShopProvider>
        </FestivalProvider>
      </SettingsProvider>
    </FeatureFlagsProvider>
    <ReactQueryDevtools />
  </QueryClientProvider>
</StrictMode>
```

Note: `PlayerDataProvider` is inside `AppShell` (needs `player` state), not in `App()`.

## Code Splitting

- **Route-level splitting**: `React.lazy()` for all pages except `SongsPage` (landing page)
- **Bundle strategy**: Vite auto-splits vendor chunks; pages are separate chunks via dynamic imports
- **No manual chunk configuration** — relies on Vite's default code splitting

## Key Dependencies

| Package | Version | Purpose |
|---|---|---|
| react / react-dom | 19.x | UI framework |
| react-router-dom | 7.x | Client-side routing (HashRouter) |
| @tanstack/react-query | 5.x | Server state management |
| @tanstack/react-virtual | 3.x | Virtual scrolling (SongsPage, leaderboards) |
| react-i18next / i18next | 16.x / 25.x | Internationalization (English only currently) |
| react-icons/io5 | 5.x | Icons (Ionicons 5 set) |
| recharts | 3.x | Charts (score history, statistics) |
| @dnd-kit/core + sortable | 6.x / 10.x | Drag-and-drop (song row reordering) |
| react-infinite-scroll-component | 6.x | Infinite scroll (leaderboards) |
| katex | 0.16.x | Math equation rendering |
| @festival/core | portal | Shared types, API types, enums, utilities |
| @festival/theme | portal | Design tokens, colors, spacing, breakpoints, style factories |
| @festival/ui-utils | portal | Platform detection, stagger utilities |

## Shared Packages (portal: linked)

### @festival/core (`packages/core/src/`)
- API server types (`api/serverTypes`)
- Wire format expansion functions
- Instrument types, combo definitions
- Song models, settings, suggestions logic
- `LoadPhase` enum

### @festival/theme (`packages/theme/src/`)
- Color tokens (`Colors`, `frostedCard`, `purpleGlass`, `goldStyles`)
- Spacing/layout (`Gap`, `Layout`, `Size`, `MaxWidth`, `Radius`)
- Typography (`Font`, `Weight`)
- CSS enum constants (`Position`, `Display`, `Overflow`, `Cursor`, etc.)
- CSS helper functions (`padding()`, `flexColumn`, `flexRow`, `flexCenter`, `flexBetween`, `fixedFill`)
- Breakpoints (`MOBILE_BREAKPOINT`, `WIDE_DESKTOP_BREAKPOINT`)
- Animation constants (`FADE_DURATION`, `SPINNER_FADE_MS`, `FAB_DISMISS_MS`)

### @festival/ui-utils (`packages/ui-utils/src/`)
- Platform detection (`IS_IOS`, `IS_ANDROID`, `IS_PWA`)
- Stagger delay utilities (`staggerDelay`, `estimateVisibleCount`)

## Persistence Decision Tree

| Data Type | Storage | Mechanism |
|---|---|---|
| Server data | React Query cache | In-memory with configurable staleTime/gcTime |
| Songs catalog | React Query + localStorage ETag | Instant render from cache, revalidate via 304 |
| Tracked player | localStorage | `useTrackedPlayer()` + custom sync events |
| App settings/preferences | localStorage | `SettingsContext` |
| Song settings (sort, filter, instrument) | localStorage | `songSettings.ts` + custom events |
| Feature flag overrides (dev) | localStorage | `FeatureFlagsContext` |
| First-run seen state | localStorage | `FirstRunContext` |
| Page UI state (scroll, animation) | In-memory Maps | `pageCache.ts` |
| Search query | React state | `SearchQueryContext` |
| Navigation state | URL hash + searchParams | React Router |

## Known Architectural Notes

- **HashRouter vs BrowserRouter**: Hash routing used for compatibility with static file hosting (ASP.NET Core wwwroot)
- **v8 ignore extensively used**: Shell-level code, callbacks, and layout code excluded from unit test coverage — tested via Playwright E2E instead
- **`useStyles()` pattern**: Many components use `useMemo`-based inline style objects; CSS module migration is in progress per `react/forbid-dom-props: ['style']` lint rule
- **FabSearchContext uses refs**: Action registrations stored in refs (not state) to avoid re-renders when pages register/unregister
- **ScrollContainerProvider uses ResizeObserver + CSS custom properties**: Header portal height written directly to CSS `--header-portal-h` to avoid React re-render cascades during scroll-driven animations
- **SongsPage eagerly loaded**: Only page not behind `React.lazy()` — it's the landing page
- **Build output goes into FSTService/wwwroot**: SPA is served as static files by the .NET backend
