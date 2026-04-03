# FortniteFestivalWeb — Complete Domain Overview

## App Overview

**Purpose**: React SPA for tracking Fortnite Festival leaderboard scores across all seasons, instruments, and songs. Provides player profiles, score history, rivals comparisons, song browsing, rankings, item shop display, and score improvement suggestions.

**Stack**: React 19 + TypeScript + Vite + React Router (HashRouter) + React Query (@tanstack/react-query) + CSS modules + i18next

**Deployment**: Multi-stage Docker build → Node 20 (Vite build) → Nginx (static files + reverse proxy to FSTService). Nginx proxies `/api/` and health endpoints to `${API_BACKEND_URL}`. SPA fallback via `try_files → /index.html`.

**Build output**: `FSTService/wwwroot/` (Vite outputs directly into the backend's static files directory)

**Dev server**: Vite with proxy to `VITE_API_BASE` (default `http://localhost:8080`)

**Package dependencies**: `@festival/core` (API types, wire format expansion), `@festival/theme` (layout constants, breakpoints), `@festival/ui-utils` (platform detection: IS_IOS, IS_ANDROID, IS_PWA)

---

## Page Registry

| Route | Page Component | Feature Area | Lazy? | Feature Gate | Requires Player? |
|---|---|---|---|---|---|
| `/` | → Redirect to `/songs` | — | — | — | No |
| `/songs` | `SongsPage` | Songs | No (eager) | — | No |
| `/songs/:songId` | `SongDetailPage` | Songs | Yes | — | No |
| `/songs/:songId/:instrument` | `LeaderboardPage` | Leaderboard | Yes | — | No |
| `/songs/:songId/:instrument/history` | `PlayerHistoryPage` | Leaderboard | Yes | — | No |
| `/player/:accountId` | `PlayerPage` | Player | Yes | — | No |
| `/statistics` | `PlayerPage` (own profile) | Player | — | — | Yes |
| `/rivals` | `RivalsPage` | Rivals | Yes | `rivals` | Yes |
| `/rivals/all` | `AllRivalsPage` | Rivals | Yes | `rivals` | Yes |
| `/rivals/:rivalId` | `RivalDetailPage` | Rivals | Yes | `rivals` | Yes |
| `/rivals/:rivalId/rivalry` | `RivalryPage` | Rivals | Yes | `rivals` | Yes |
| `/suggestions` | `SuggestionsPage` | Suggestions | Yes | — | Yes |
| `/compete` | `CompetePage` | Compete | Yes | `compete` | Yes |
| `/shop` | `ShopPage` | Shop | Yes | `shop` | No |
| `/leaderboards` | `LeaderboardsOverviewPage` | Leaderboards | Yes | `leaderboards` | No |
| `/leaderboards/all` | `FullRankingsPage` | Leaderboards | Yes | `leaderboards` | No |
| `/settings` | `SettingsPage` | Settings | Yes | — | No |

Route constants defined in `src/routes.ts` with both path builders and regex patterns for matching.

---

## Component Library

### common/ — Shared primitives
| Component | Purpose |
|---|---|
| `Accordion` | Expandable/collapsible content section |
| `ActionPill` | Tappable action chip |
| `ArcSpinner` | Animated loading arc indicator |
| `DirectionSelector` | Toggle direction control |
| `EmptyState` | Empty data placeholder with icon + message |
| `FrostedCard` | Glass-morphism card with proximity glow effect |
| `InstrumentSelector` | Multi-instrument toggle picker |
| `MarqueeText` | Scrolling text for overflow (CSS module paired) |
| `Math` | Math/number display component |
| `PageHeader` | Standard page heading with portal support |
| `Paginator` | Page navigation control |
| `RadioRow` | Radio button row for settings |
| `SearchBar` | Reusable search input |
| `SectionHeader` | Section heading |
| `SuspenseFallback` | Suspense boundary loading state |
| `ToggleRow` | Toggle switch row for settings |

### display/ — Instrument display
| Component | Purpose |
|---|---|
| `InstrumentChip` | Compact instrument badge |
| `InstrumentHeader` | Instrument name + icon header |
| `InstrumentIcons` | Instrument icon set |

### page/ — Page-level wrappers
| Component | Purpose |
|---|---|
| `BackgroundImage` | Blurred background image component |
| `ErrorBoundary` | React error boundary |
| `FadeIn` | Fade-in animation wrapper |
| `LoadGate` | Loading/error/data gate (loading → error → content) |
| `RouteErrorFallback` | Error fallback for route-level errors |
| `SyncBanner` | Data sync progress banner |

### shell/ — App chrome
| Component | Path | Purpose |
|---|---|---|
| `AnimatedBackground` | shell/ | Particle/visual background effect |
| `HamburgerButton` | shell/ | Sidebar toggle button |
| `DesktopNav` | shell/desktop/ | Top navigation bar (desktop) |
| `HeaderProfileButton` | shell/desktop/ | Profile button in header |
| `HeaderSearch` | shell/desktop/ | Search in desktop header |
| `PinnedSidebar` | shell/desktop/ | Wide desktop pinned sidebar |
| `Sidebar` | shell/desktop/ | Slide-out sidebar |
| `BackLink` | shell/mobile/ | Back navigation link |
| `BottomNav` | shell/mobile/ | Bottom tab bar |
| `MobileHeader` | shell/mobile/ | Mobile top header |
| `MobilePlayerSearchModal` | shell/mobile/ | Player search modal |
| `FloatingActionButton` | shell/fab/ | Multi-action FAB |
| `FABMenu` | shell/fab/ | FAB expanded menu |

### modals/ — Modal system
| Component | Purpose |
|---|---|
| `Modal` | Base modal container |
| `ModalShell` | Standard modal shell layout |
| `ModalSection` | Section within a modal |
| `BulkActions` | Bulk action controls in modals |
| `ChangelogModal` | Version changelog display |
| `ConfirmAlert` | Yes/No confirmation dialog |

### routing/
| Component | Purpose |
|---|---|
| `FeatureGate` | Feature flag gate — hides routes when flag is off |

### songs/ — Song display components
| Component | Path | Purpose |
|---|---|---|
| `SongInfoHeader` | headers/ | Song info header with album art |
| `AlbumArt` | metadata/ | Album art thumbnail |
| `AccuracyDisplay` | metadata/ | Score accuracy display |
| `DifficultyBars` | metadata/ | Difficulty indicator bars |
| `DifficultyPill` | metadata/ | Difficulty badge |
| `GoldStars` | metadata/ | Gold star rating display |
| `MiniStars` | metadata/ | Compact star display |
| `PercentilePill` | metadata/ | Percentile badge |
| `ScorePill` | metadata/ | Score badge |
| `SeasonPill` | metadata/ | Season badge |
| `SongInfo` | metadata/ | Song metadata composite |

### player/ — Player components
| Component | Purpose |
|---|---|
| `PlayerSearchBar` | Player account search |
| `PlayerPercentileTable` | Percentile breakdown table |
| `SelectProfilePill` | Profile selection pill |
| `StatBox` | Statistics display box |

### leaderboard/
| Component | Purpose |
|---|---|
| `PaginatedLeaderboard` | Paginated leaderboard table |

### sort/ — Sort/reorder system
| Component | Purpose |
|---|---|
| `ReorderList` | Drag-and-drop reorder list |
| `SortableRow` | Sortable row within ReorderList |

### firstRun/
| Component | Purpose |
|---|---|
| `FirstRunCarousel` | Onboarding carousel overlay |

---

## Feature Areas

### 1. Songs (`src/pages/songs/`)
**Pages**: `SongsPage`
**Components**: `SongRow`, `SongsToolbar`, `InvalidScoreIcon`
**Modals**: `FilterModal`, `SortModal` (with filter sub-components)
**Purpose**: Browse all tracked songs with search, sort, filter by instrument/difficulty/score. Eager-loaded as the default page.

### 2. Song Detail (`src/pages/songinfo/`)
**Pages**: `SongDetailPage`
**Components**: `SongDetailHeader`, `SongHeader`, `InstrumentCard`, `ScoreHistoryChart`, `ScoreCardList`, `PathImage`, `PathsModal`, `ZoomableImage`
**Purpose**: View a single song's leaderboard previews across all instruments, score history chart, and optimal paths (CHOpt).

### 3. Leaderboard (`src/pages/leaderboard/`)
**Subfolders**: `global/` (public leaderboard) + `player/` (personal history)
**Pages**: `LeaderboardPage` (global), `PlayerHistoryPage` (personal)
**Components**: `LeaderboardEntry`, `PlayerContent`, `PlayerHistoryEntry`
**Modals**: `PlayerScoreSortModal`
**Purpose**: View per-song per-instrument leaderboards with pagination, and personal score history over time.

### 4. Player (`src/pages/player/`)
**Pages**: `PlayerPage`
**Sections**: `OverallSummarySection`, `InstrumentStatsSection`, `PlayerSectionHeading`
**Components**: `PlayerSongRow`, `TopSongsSection`
**Helpers**: `playerFilterHelpers`, `playerStats`, `playerPageTypes`
**Purpose**: Player profile showing overall stats, per-instrument breakdowns, top songs, percentile rankings.

### 5. Rivals (`src/pages/rivals/`)
**Pages**: `RivalsPage`, `RivalDetailPage`, `RivalryPage`, `AllRivalsPage`, `LeaderboardRivalsTab`
**Components**: `RivalRow`, `RivalSongRow`, `LeaderboardNeighborRow`
**Helpers**: `comboUtils`, `rivalCategories`
**Purpose**: View rivals (players near your skill level) per instrument combo, compare song-by-song, see rivalry details. Supports both song-based rivals and leaderboard-based rivals.

### 6. Suggestions (`src/pages/suggestions/`)
**Pages**: `SuggestionsPage`
**Components**: `CategoryCard`
**Modals**: `SuggestionsFilterModal`
**Helpers**: `suggestionsHelpers`
**Purpose**: Score improvement suggestions — songs where the player can gain the most leaderboard positions.

### 7. Compete (`src/pages/compete/`)
**Pages**: `CompetePage`
**Purpose**: Competitive comparison features (feature-gated).

### 8. Shop (`src/pages/shop/`)
**Pages**: `ShopPage`
**Components**: `ShopCard`
**Purpose**: Current Fortnite Festival item shop display with grid/list views. Uses WebSocket for real-time updates.

### 9. Leaderboards/Rankings (`src/pages/leaderboards/`)
**Pages**: `LeaderboardsOverviewPage`, `FullRankingsPage`
**Components**: `RankingCard`, `RankingEntry`
**Modals**: `InstrumentPickerModal`
**Helpers**: `rankingHelpers`
**Purpose**: Overall rankings across all tracked players per instrument, composite rankings, and combo rankings with paginated display.

### 10. Settings (`src/pages/settings/`)
**Pages**: `SettingsPage`
**Purpose**: User preferences — instrument visibility, metadata display order, score filtering, experimental features, first-run replays, light trail toggle.

### Shell (spread across `App.tsx` + `components/shell/`)
**Purpose**: Navigation (mobile bottom tabs + desktop top nav + wide pinned sidebar), FAB with context-sensitive actions per page, animated background, player selection modals, changelog modal.

---

## State Management

### Context Providers (9 — nested in App.tsx)
Provider tree order (outer → inner):
1. `QueryClientProvider` — React Query client
2. `FeatureFlagsProvider` — Feature flags from `/api/features` (all-on in dev)
3. `SettingsProvider` — App settings persisted to localStorage
4. `FestivalProvider` — Song list + current season from `/api/songs` (ETag caching + localStorage)
5. `ShopProvider` — Shop state from `/api/shop` + WebSocket real-time updates
6. `FirstRunProvider` — First-run experience carousel system
7. `FabSearchProvider` — FAB action registration (page-specific actions)
8. `SearchQueryProvider` — Search query text (separated to avoid keystroke re-renders)
9. `ScrollContainerProvider` — Scroll container ref + header portal target
10. `PlayerDataProvider` — Player data + sync status (inside AppShell, wraps routes)

### React Query Configuration
- `staleTime`: 5 min
- `gcTime`: 10 min
- `retry`: 1
- `refetchOnWindowFocus`: false
- Query key factory: `src/api/queryKeys.ts` (typed, hierarchical)

### Query Keys (18 groups)
`songs`, `player`, `playerHistory`, `syncStatus`, `leaderboard`, `allLeaderboards`, `playerStats`, `version`, `rivalsOverview`, `rivalsList`, `rivalDetail`, `rankings`, `playerRanking`, `compositeRankings`, `playerCompositeRanking`, `comboRankings`, `playerComboRanking`, `leaderboardNeighborhood`, `compositeNeighborhood`

### Page Caches (scroll/animation state — not data)
- `songDetailCache` — instrument data, scroll position per song
- `leaderboardCache` — entries, page, scroll per leaderboard
- `rankingsCache` — page, scroll per rankings view

### localStorage Usage
- `fst_songs_cache` — Songs response + ETag (instant render on load)
- `fst:trackedPlayer` — Currently selected player profile
- `fst:settings` — All AppSettings
- `fst:changelog` — Last-seen changelog version + hash
- `fst:featureFlagOverrides` — Dev-mode feature flag overrides
- `fst:firstRunSeen` — First-run experience seen state
- `fst:songSettings` — Per-song instrument selection

---

## Custom Hooks

### Data Hooks (`hooks/data/`) — 15 hooks
| Hook | Purpose |
|---|---|
| `useTrackedPlayer` | Tracked player profile (localStorage + state) |
| `useFilteredSongs` | Song list filtering by search/instrument/difficulty |
| `useSuggestions` | Score improvement suggestions |
| `useShopState` | Shop visibility + URL lookup |
| `useShopWebSocket` | WebSocket connection to shop updates |
| `useSyncStatus` | Player sync/backfill progress polling |
| `useAccountSearch` | Account search API with debounce |
| `useAvailableSeasons` | Available seasons from song data |
| `useDemoSongs` | Demo song data for first-run |
| `useItemShopDemoSongs` | Demo shop data for first-run |
| `useLoadPhase` | Loading phase state machine |
| `useScoreFilter` | Score validity filtering (CHOpt max) |
| `useSongLookups` | Song lookup maps |
| `useSortedScoreHistory` | Sorted score history entries |
| `useVersions` | App + API version check |

### UI Hooks (`hooks/ui/`) — 20 hooks
| Hook | Purpose |
|---|---|
| `useIsMobile` | Mobile/desktop/wide-desktop breakpoint detection |
| `useMediaQuery` | Generic CSS media query hook |
| `useStagger` | Staggered animation sequencing |
| `useStaggerRush` | Fast stagger variant |
| `useStaggerStyle` | Stagger CSS style generation |
| `useScrollFade` | Scroll-based fade effect |
| `useScrollMask` | Scroll mask gradient |
| `useScrollRestore` | Scroll position save/restore |
| `usePageTransition` | Page transition animation |
| `useViewTransition` | View Transition API wrapper |
| `useProximityGlow` | Proximity-based glow effect on cards |
| `useVisualViewport` | Visual viewport dimensions (iOS safe area) |
| `useFadeSpinner` | Fade-in spinner delay |
| `useFirstRun` | First-run slide registration |
| `useRegisterFirstRun` | Register page's first-run slides |
| `useGridColumnCount` | Responsive grid column calculation |
| `useLeaderboardColumns` | Leaderboard column configuration |
| `useModalDraft` | Draft state for modal forms |
| `useModalState` | Modal open/close state |
| `useTabNavigation` | Bottom tab navigation state |

### Chart Hooks (`hooks/chart/`) — 5 hooks
| Hook | Purpose |
|---|---|
| `useChartData` | Chart data transformation |
| `useChartDimensions` | Responsive chart sizing |
| `useChartPagination` | Chart page navigation |
| `useCardAnimation` | Card animation timing |
| `useListAnimation` | List animation sequencing |

### Navigation Hooks (`hooks/navigation/`) — 1 hook
| Hook | Purpose |
|---|---|
| `useNavigateToSongDetail` | Song detail navigation helper |

---

## API Integration

### Client (`src/api/client.ts`)
- Base URL: empty string (same-origin, proxied via Vite dev server or Nginx)
- Generic `get<T>()` and `post<T>()` helpers
- ETag-based caching: in-memory `etagCache` Map + localStorage for songs
- `expandAlbumArt()` — prepends CDN prefix to relative album art URLs
- `expandWire*()` — expands compressed wire format responses from `@festival/core`
- `normalizeDisplayName()` — defaults empty names to "Unknown User"

### API Methods (24)
`getSongs`, `getShop`, `getLeaderboard`, `getPlayer`, `searchAccounts`, `trackPlayer`, `getSyncStatus`, `getPlayerHistory`, `getAllLeaderboards`, `getPlayerStats`, `getVersion`, `getRivalsOverview`, `getRivalsList`, `getRivalDetail`, `getRankings`, `getPlayerRanking`, `getCompositeRankings`, `getPlayerCompositeRanking`, `getComboRankings`, `getPlayerComboRanking`, `getLeaderboardNeighborhood`, `getCompositeNeighborhood`, `getLeaderboardRivals`, `getLeaderboardRivalDetail`, `getRivalSuggestions`, `getRivalsAll`

### Caching Strategy
- ETag + 304 Not Modified (server-side) for songs, shop, player, neighborhoods
- React Query client-side: 5 min stale, 10 min GC
- localStorage for songs (instant render while ETag revalidation runs)
- Page-level UI caches (scroll positions, animation flags) in module-scope Maps

---

## Test Coverage

### Unit Tests (Vitest) — `__test__/`
Coverage thresholds: **95% per-file** (lines, branches, statements, functions)

| Area | Test Files | Key Tests |
|---|---|---|
| `app/` | 5 files | App rendering, shell layout, mobile/desktop, routing |
| `api/` | 3 files | Client methods, page cache, query keys |
| `contexts/` | 9 files | All 9 context providers |
| `hooks/data/` | 12 files | All data hooks |
| `hooks/ui/` | 17 files | All UI hooks |
| `hooks/chart/` | 5 files | All chart hooks |
| `components/common/` | 13 files | All common components |
| `components/shell/` | Shell subfolders | Desktop nav, mobile nav, FAB, sidebar |
| `components/modals/` | Modal tests | Modal system |
| `components/display/` | Instrument display | Chips, icons |
| `components/page/` | Page wrappers | ErrorBoundary, LoadGate, etc. |
| `components/routing/` | FeatureGate | Feature flag gating |
| `components/songs/` | Song metadata | All song display components |
| `components/sort/` | Sort system | Reorder list |
| `pages/songs/` | Songs page + modals | Song filtering, sort modal, filter modal |
| `pages/player/` | Player page + sections | Player stats, auto-reload |
| `pages/rivals/` | Rivals pages | Rival categories, combo utils |
| `pages/leaderboard/` | Leaderboard tests | (in pages/leaderboard/) |
| `pages/leaderboards/` | Rankings helpers | Ranking calculation |
| `pages/songinfo/` | Song detail | (in pages/songinfo/) |
| `pages/shop/` | Shop page | (in pages/shop/) |
| `pages/settings/` | Settings page | (in pages/settings/) |
| `pages/suggestions/` | Suggestions page | (in pages/suggestions/) |
| `utils/` | 9 files | Formatters, settings, filters, adapters |
| `firstRun/` | 1 file | First-run type tests |

### E2E Tests (Playwright) — `e2e/`
**4 viewport matrix**: desktop (1280×800), desktop-narrow (800×800), mobile (375×812), mobile-narrow (320×568)

| Spec | Purpose |
|---|---|
| `scroll.spec.ts` | Scroll behavior |
| `fre/carousel-interaction.fre.spec.ts` | First-run carousel interactions |
| `fre/compete.fre.spec.ts` | Compete page FRE |
| `fre/cross-page-flow.fre.spec.ts` | Cross-page FRE flows |
| `fre/feature-flags.fre.spec.ts` | Feature flag gating |
| `fre/layout.fre.spec.ts` | Layout FRE |
| `fre/leaderboards.fre.spec.ts` | Leaderboards FRE |
| `fre/player-history.fre.spec.ts` | Player history FRE |
| `fre/rivals.fre.spec.ts` | Rivals FRE |
| `fre/settings-direct.fre.spec.ts` | Settings FRE |
| `fre/shop.fre.spec.ts` | Shop FRE |
| `fre/song-detail.fre.spec.ts` | Song detail FRE |
| `fre/songs.fre.spec.ts` | Songs FRE |
| `fre/statistics.fre.spec.ts` | Statistics FRE |
| `fre/suggestions.fre.spec.ts` | Suggestions FRE |

Fixtures: `fre.ts` (first-run helpers), `navigation.ts` (navigation helpers)

---

## Build & Deploy

### Vite Configuration
- React plugin, path aliases for `@festival/*` packages
- Version injection: `__APP_VERSION__`, `__CORE_VERSION__`, `__THEME_VERSION__`
- Build output: `FSTService/wwwroot/` (backend serves directly)
- Test: Vitest with jsdom, v8 coverage provider
- Stubs: `react-native` and `react-native-app-auth` (shared code compat)

### Docker
- **Stage 1**: Node 20 → Yarn install → Vite build
- **Stage 2**: Nginx Alpine → serve static + reverse proxy
- Environment: `API_BACKEND_URL` via envsubst in nginx config template

### Nginx
- Gzip compression (text, JS, CSS, JSON, SVG)
- Hashed static assets: 1 year cache, `public, immutable`
- `/api/` → reverse proxy to backend (with WebSocket upgrade support)
- `/healthz`, `/readyz` → proxied health checks
- SPA fallback: `try_files $uri $uri/ /index.html`

---

## Key User Flows

### 1. First Visit (No Player Selected)
`/` → redirect to `/songs` → Browse song list → Tap song → Song detail → View leaderboard → Search for player → Select profile

### 2. Player Profile Selection
FAB → "Select Player Profile" → Search modal → Enter username → API search → Tap result → `trackPlayer` POST → Stored in localStorage → Navigate to `/statistics`

### 3. Song Exploration
`/songs` → Search/filter/sort → Tap song → `/songs/:songId` (instrument cards, score history chart) → Tap instrument → `/songs/:songId/:instrument` (full leaderboard) → Tap "History" → `/songs/:songId/:instrument/history`

### 4. Rivals Comparison
`/rivals` → Song-based or Leaderboard-based tab → Rival list per instrument combo → Tap rival → `/rivals/:rivalId` (rival detail, song-by-song comparison) → `/rivals/:rivalId/rivalry` (deep rivalry view)

### 5. Score Improvement
`/suggestions` → Category cards showing improvement opportunities → Filter by instrument/difficulty → Tap suggestion → Navigate to song detail

### 6. Rankings
`/leaderboards` → Overview cards per instrument → `/leaderboards/all?instrument=X` → Paginated rankings → Player ranking highlight → Neighborhood view

### 7. Item Shop
`/shop` → Grid/list of current shop items → Real-time WebSocket updates → Tap song → Navigate to song detail → Shop URL link to Epic store

### 8. Data Sync
Select player → Background sync status polling (`useSyncStatus`) → `SyncBanner` shows progress → Backfill + history phases → Auto-reload player data when complete

---

## Feature Flags

| Flag | Controls |
|---|---|
| `shop` | Item shop page + shop highlighting |
| `rivals` | Rivals pages (all 4 routes) |
| `compete` | Compete page |
| `leaderboards` | Rankings pages |
| `firstRun` | First-run experience carousel |
| `difficulty` | Difficulty display features |

Fetched from `/api/features` in production. All-on in dev mode with localStorage overrides (`fst:featureFlagOverrides`).

---

## Styles

### Global styles
- `src/styles/theme.css` — CSS custom properties (colors, spacing, breakpoints)
- `src/styles/animations.css` / `animations.module.css` — Keyframe animations
- `src/styles/effects.module.css` — Visual effects (glass, glow, fade)
- `src/styles/rivals.module.css` — Rivals-specific styles
- `src/index.css` — Base reset + global styles

### Pattern
- Component-level CSS modules (`.module.css`) paired with components
- Inline style objects (`*Styles.ts`) for dynamic/JS-driven styles
- `@festival/theme` package for shared constants (Size, Layout, breakpoints)

---

## Internationalization
- `i18next` with `en.json` translation file
- `useTranslation()` hook used throughout
- Setup in `src/i18n/index.ts`

---

## First-Run Experience (FRE)
- Per-page slide registration via `useRegisterFirstRun`
- Slides stored in `src/pages/*/firstRun/` directories
- `FirstRunCarousel` overlay component
- Seen state persisted to localStorage
- Replayable from Settings page
- Comprehensive E2E test coverage (14 FRE specs × 4 viewports)
