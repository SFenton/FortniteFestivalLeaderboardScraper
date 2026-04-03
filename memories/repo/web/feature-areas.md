# FortniteFestivalWeb Feature Areas

> Last updated: 2026-04-03

---

## Songs

**Routes**: `/songs`
**Page**: `pages/songs/SongsPage.tsx` (eagerly loaded — only non-lazy page)
**Feature flag**: None (always available)
**Player required**: No (scores overlay when player is tracked)

### Page Structure
- `Page` shell with `scrollRestoreKey="songs"`
- `PageHeader` with title
- `SongsToolbar` — instrument selector + search + sort/filter pills
- Virtualized song list via `@tanstack/react-virtual` (`useVirtualizer`)
- `SongRow` — individual song row with metadata columns
- `SyncBanner` — shows when player data is syncing
- `EmptyState` — when no songs match filters
- `LoadGate` — guards rendering until data is loaded

### Sub-components (`pages/songs/components/`)
- `SongRow.tsx` — primary song list item
- `SongsToolbar.tsx` — instrument picker, search, sort/filter action pills
- `InvalidScoreIcon.tsx` — score validity indicator

### Modals (`pages/songs/modals/`)
- `SortModal.tsx` — sort by title, score, year, difficulty, etc. with ascending/descending
- `FilterModal.tsx` — filter by instrument, difficulty, genre, played status, etc.
  - Sub-components in `modals/components/`

### Contexts/Hooks Used
- `useFestival()` — songs list, loading state, error
- `usePlayerData()` — player scores overlay
- `useSettings()` — instrument visibility, metadata visibility
- `useFabSearch()` — FAB search registration
- `useSearchQuery()` — shared search query state
- `useFilteredSongs()` — search + sort + filter pipeline
- `useScoreFilter()` — invalid score filtering
- `useShopState()` — shop highlight integration
- `useShop()` — shop context
- `useModalState()` — sort/filter modal state
- `useStaggerStyle()` — entry animation stagger

### API Calls
- None directly (songs come from `FestivalContext` which calls `api.getSongs()`)

### User Interactions
- Search songs (debounced text input)
- Sort songs (modal with multiple sort modes)
- Filter songs (modal with instrument, difficulty, genre, played filters)
- Switch instrument (toolbar selector)
- Tap song → navigates to `/songs/:songId` (SongDetailPage)

### First-Run
- `pages/songs/firstRun/` — onboarding slides for songs page

---

## Song Detail (part of Songs feature area)

**Route**: `/songs/:songId`
**Page**: `pages/songinfo/SongDetailPage.tsx` (lazy loaded)

### Page Structure
- `Page` with `PageBackground` (album art background)
- `SongInfoHeader` — song title, artist, album art, shop link
- `ScoreHistoryChart` — line chart of player score history per instrument
- `InstrumentCard` (×N) — one card per active instrument showing top leaderboard entries + player score
- `PathsModal` — CHOpt optimal paths modal
- `EmptyState` / `ArcSpinner` — loading/error states

### Sub-components (`pages/songinfo/components/`)
- `InstrumentCard.tsx` — leaderboard preview + player score for one instrument
- `SongDetailHeader.tsx`, `SongHeader.tsx` — header variants
- `chart/` — `ScoreHistoryChart` and chart utilities
- `path/` — `PathsModal` for optimal paths

### API Calls
- `api.getPlayerHistory(accountId, songId)` — score history for chart
- `api.getAllLeaderboards(songId, 10, leeway)` — top-10 per instrument

### Contexts/Hooks Used
- `useFestival()` — song metadata
- `useTrackedPlayer()` — player identity
- `usePlayerData()` — precomputed player scores
- `useScoreFilter()` — leeway filtering for leaderboard entries
- `useShopState()` — shop URL for song
- `useLoadPhase()` — loading animation phase
- `useFabSearch()` — register openPaths for FAB

---

## Song Leaderboard (part of Songs feature area)

**Route**: `/songs/:songId/:instrument`
**Page**: `pages/leaderboard/global/LeaderboardPage.tsx` (lazy loaded)

### Page Structure
- `Page` with `PageBackground` (album art)
- `SongInfoHeader` — collapsible on mobile
- `PaginatedLeaderboard` — shared paginated list component
- `LeaderboardEntry` — individual leaderboard row
- Player footer row (when tracked player has a score)

### Sub-components (`pages/leaderboard/global/components/`)
- `LeaderboardEntry.tsx` — rank, name, score, accuracy, season, stars

### API Calls
- `api.getLeaderboard(songId, instrument, PAGE_SIZE, offset, leeway)` — paginated leaderboard

### Contexts/Hooks Used
- `useFestival()` — song list
- `usePlayerData()` — player's score for this song/instrument
- `useScoreFilter()` — invalid score detection + valid fallback resolution
- `useScrollContainer()` — scroll management
- Keyboard pagination (ArrowLeft/ArrowRight)

---

## Player History (part of Songs feature area)

**Route**: `/songs/:songId/:instrument/history`
**Page**: `pages/leaderboard/player/PlayerHistoryPage.tsx` (lazy loaded)

### Page Structure
- `Page` with `PageBackground`
- `SongInfoHeader` — collapsible
- Virtualized score history list via `@tanstack/react-virtual`
- `PlayerHistoryEntry` — individual history row
- `PlayerScoreSortModal` — sort by score, date, accuracy, etc.
- `ActionPill` for sort button

### Sub-components (`pages/leaderboard/player/components/`)
- `PlayerContent.tsx` — player data display (shared with PlayerPage)
- `PlayerHistoryEntry.tsx` — history row

### Modals (`pages/leaderboard/player/modals/`)
- `PlayerScoreSortModal.tsx` — sort modes for history entries

### API Calls
- `api.getPlayerHistory(accountId, songId, instrument)` — full history

### Contexts/Hooks Used
- `useFestival()` — song metadata
- `useTrackedPlayer()` — player identity
- `useFabSearch()` — register sort for FAB
- `useScoreFilter()` — filter invalid history entries
- `useSortedScoreHistory()` — sort pipeline
- `useLoadPhase()` — loading phase

---

## Rivals

**Routes**: `/rivals`, `/rivals/all`, `/rivals/:rivalId`, `/rivals/:rivalId/rivalry`
**Pages**: `RivalsPage.tsx`, `AllRivalsPage.tsx`, `RivalDetailPage.tsx`, `RivalryPage.tsx` (all lazy loaded)
**Feature flag**: `rivals`
**Player required**: Yes (redirects to `/songs` without player)

### RivalsPage (`/rivals`)
- Two tabs: **Song Rivals** and **Leaderboard Rivals** (tab via `?tab=leaderboard`)
- Song tab: per-instrument rival lists showing above/below neighbors + combo rivals
- Leaderboard tab: `LeaderboardRivalsTab` — per-instrument leaderboard-based rivals
- `RivalRow` — clickable rival entry showing rank delta, score delta
- `ActionPill` pills for "View All" per instrument category
- `InstrumentHeader` — instrument section headers

### AllRivalsPage (`/rivals/all?category=...`)
- Full rival list for a category: `common` (cross-instrument intersection), `combo`, or single instrument
- Fetches per-instrument rivals and computes intersection for "common"
- Uses `RivalRow` for display

### RivalDetailPage (`/rivals/:rivalId`)
- Song-by-song comparison between tracked player and rival
- Categories: closest battles, almost passed, slipping away, barely winning, pulling forward, dominating them
- `RivalSongRow` — per-song comparison row with score delta
- Preview of 5 songs per category, "View All" navigates to RivalryPage

### RivalryPage (`/rivals/:rivalId/rivalry?mode=...`)
- Full list of songs in one rivalry category
- `RivalSongRow` for each song

### Sub-components (`pages/rivals/components/`)
- `RivalRow.tsx` — rival summary row (name, rank delta, song count)
- `RivalSongRow.tsx` — per-song comparison (album art, scores, delta)
- `LeaderboardNeighborRow.tsx` — leaderboard neighbor display

### Helpers (`pages/rivals/helpers/`)
- `comboUtils.ts` — derive combo ID from settings, get enabled instruments
- `rivalCategories.ts` — categorize rival songs into battle types

### API Calls
- `api.getRivalsList(accountId, combo)` — per-instrument or combo rival list
- `api.getRivalsOverview(accountId)` — computedAt timestamp
- `api.getRivalDetail(accountId, combo, rivalId)` — song-by-song comparison
- `api.getLeaderboardRivals(instrument, accountId, rankBy)` — leaderboard-based rivals
- `api.getLeaderboardRivalDetail(instrument, accountId, rivalId, rankBy)` — leaderboard rival detail
- `api.getPlayer(accountId)` — resolve display name

### Contexts/Hooks Used
- `useTrackedPlayer()` — player identity (required)
- `useSettings()` — visible instruments
- `useFabSearch()` — register tab toggle for FAB
- `usePageTransition()` — loading/stagger animation
- `useStagger()` — entry animation
- `useSongLookups()` — album art + year maps (detail/rivalry pages)
- `useRivalsSharedStyles()` — shared style hook

### Caching
- Module-level caches (`_cachedInstrumentRivals`, `_cachedDetailSongs`, etc.) for instant back-navigation

---

## Shop

**Route**: `/shop`
**Page**: `pages/shop/ShopPage.tsx` (lazy loaded)
**Feature flag**: `shop`
**Player required**: No

### Page Structure
- `Page` with `scrollRestoreKey="shop"`
- `PageHeader` with title
- Grid/List toggle via `ActionPill`
- `ShopCard` (grid mode) or `SongRow` (list mode) for each shop song
- `EmptyState` when shop is empty

### Sub-components (`pages/shop/components/`)
- `ShopCard.tsx` — grid card with album art, title, "leaving tomorrow" badge

### API Calls
- None directly (shop data from `useShopState()` which uses `ShopContext` → `api.getShop()`)
- Shop data also received via WebSocket (`useShopWebSocket`)

### Contexts/Hooks Used
- `useShopState()` — shop songs, leaving tomorrow flags, shop URLs
- `useSettings()` — instrument visibility
- `useFabSearch()` — register grid/list toggle for FAB
- `useViewTransition()` — grid↔list transition animation
- `useStaggerStyle()` — entry stagger
- `useMediaQuery(QUERY_NARROW_GRID)` — force list on narrow screens

### Caching
- View mode persisted to `localStorage` key `fst:shopView`

---

## Player

**Route**: `/player/:accountId` (external player), `/statistics` (tracked player)
**Page**: `pages/player/PlayerPage.tsx` (lazy loaded)
**Player required**: `/statistics` requires tracked player; `/player/:accountId` works for any account

### Page Structure
- `ArcSpinner` → `PlayerContent` on load
- `PlayerContent` (from `pages/leaderboard/player/components/PlayerContent.tsx`) — large reusable component:
  - **OverallSummarySection** — total scores, percentiles, star counts
  - **InstrumentStatsSection** (×N) — per-instrument breakdown
  - **TopSongsSection** — highest scoring songs
  - `PlayerSongRow` — individual song in player profile
  - `PlayerSectionHeading` — section headers
- `SyncBanner` integration — shows backfill/history sync progress
- `EmptyState` / error handling

### Sub-components (`pages/player/components/`)
- `PlayerSongRow.tsx` — song row in player context (score, rank, percentile)
- `TopSongsSection.tsx` — top songs section

### Sections (`pages/player/sections/`)
- `OverallSummarySection.tsx` — aggregate statistics
- `InstrumentStatsSection.tsx` — per-instrument stats
- `PlayerSectionHeading.tsx` — styled section dividers

### Helpers (`pages/player/helpers/`)
- `playerFilterHelpers.ts` — song row filtering
- `playerPageTypes.ts` — shared type definitions
- `playerStats.ts` — statistics computation

### API Calls
- `api.getPlayer(accountId)` — full player data (React Query for non-tracked)
- `api.getSyncStatus(accountId)` — sync status polling (via `useSyncStatus`)

### Contexts/Hooks Used
- `useFestival()` — song metadata
- `usePlayerData()` — precomputed data for tracked player
- `useSyncStatus()` — backfill/history progress tracking
- `useLoadPhase()` — spinner → content transition
- React Query (`useQuery`, `useQueryClient`) — data fetching for non-tracked players

### Caching
- Module-level `_renderedPlayerAccount` / `_renderedTrackedAccount` for skip-stagger on revisit
- React Query cache for external players

---

## Leaderboards

**Routes**: `/leaderboards`, `/leaderboards/all`
**Pages**: `LeaderboardsOverviewPage.tsx`, `FullRankingsPage.tsx` (lazy loaded)
**Feature flag**: `leaderboards`
**Player required**: No (player ranking shown when tracked)

### LeaderboardsOverviewPage (`/leaderboards`)
- Grid of `RankingCard` per visible instrument — top-10 preview + player rank
- `ActionPill` for metric picker (when experimental ranks enabled)
- Metric modal — `totalscore`, `adjusted`, `percentage`, `songcount`, etc.
- Metric info slides — first-run-style tooltips for each metric

### FullRankingsPage (`/leaderboards/all?instrument=...&rankBy=...&page=...`)
- Full paginated leaderboard for one instrument/metric
- `PaginatedLeaderboard` — shared pagination component
- `RankingEntry` — individual ranking row
- Keyboard pagination (arrow keys)
- `InstrumentPickerModal` — switch instrument
- Metric picker modal

### Sub-components (`pages/leaderboards/components/`)
- `RankingCard.tsx` — preview card with top entries + player position
- `RankingEntry.tsx` — ranking row (rank, name, rating)

### Modals (`pages/leaderboards/modals/`)
- `InstrumentPickerModal.tsx` — instrument selector for full rankings

### Helpers (`pages/leaderboards/helpers/`)
- `rankingHelpers.ts` — `formatRating`, `getRankForMetric`, `RANKING_METRICS`, `EXPERIMENTAL_METRICS`, `computeRankWidth`

### API Calls (React Query)
- `api.getRankings(instrument, metric, page, pageSize)` — ranked entries
- `api.getPlayerRanking(instrument, accountId)` — player's ranking position
- `api.getComboRankings(comboId, rankBy, page, pageSize)` — combo rankings
- `api.getPlayerComboRanking(accountId, comboId, rankBy)` — player combo rank

### Contexts/Hooks Used
- `useTrackedPlayer()` — player identity for rank display
- `useSettings()` — visible instruments, experimental ranks flag
- `useFabSearch()` — register metric/instrument modals for FAB
- `useModalState()` — metric picker state
- `useGridColumnCount()` — responsive grid columns
- `useScrollContainer()` — scroll management for pagination
- React Query (`useQueries`, `useQuery`)

### Caching
- `rankingsCache` (pageCache) — page number + scroll position per instrument/metric

---

## Suggestions

**Route**: `/suggestions`
**Page**: `pages/suggestions/SuggestionsPage.tsx` (lazy loaded)
**Player required**: Yes (receives `accountId` prop)

### Page Structure
- `Page` with infinite scroll via `react-infinite-scroll-component`
- `CategoryCard` per suggestion category — expandable cards with song suggestions
- `SuggestionsFilterModal` — filter by instrument type, category type
- `PageHeader` with filter pill
- `EmptyState` when no suggestions
- Anti-stall logic: tracks consecutive empty batches when filters hide everything

### Sub-components (`pages/suggestions/components/`)
- `CategoryCard.tsx` — suggestion category display
- `firstRun/` — first-run slides for suggestions

### Modals (`pages/suggestions/modals/`)
- `SuggestionsFilterModal.tsx` — filter by instrument + category types

### Helpers
- `suggestionsHelpers.ts` — extensive utilities: `buildEffectiveInstrumentSettings`, `shouldShowCategoryType`, `filterCategoryForInstrumentTypes`, `computeEffectiveSeason`, `getCardDelay`, `buildAlbumArtMap`

### API Calls
- None directly (uses `useSuggestions` hook which computes client-side from songs + player scores)

### Contexts/Hooks Used
- `useFestival()` — songs, current season
- `usePlayerData()` — player scores for suggestion computation
- `useSuggestions()` — core suggestion engine (client-side computation)
- `useSettings()` — instrument visibility
- `useFabSearch()` — register filter for FAB
- `useModalState()` — filter modal state
- `useScrollContainer()` — scroll management
- `useScrollFade()` — scroll edge fade effect

### Caching
- Filter settings persisted via `loadSuggestionsFilter` / `saveSuggestionsFilter`

---

## Settings

**Route**: `/settings`
**Page**: `pages/settings/SettingsPage.tsx` (lazy loaded)
**Player required**: No

### Page Structure
- `Page` with `scrollRestoreKey="settings"`
- `PageHeader` with title
- **App Settings** section: instrument icons toggle, visual order toggle + `ReorderList`
- **Instruments** section: 6 instrument toggles (lead, bass, drums, vocals, pro lead, pro bass) — minimum 1 required
- **Song Metadata** section: toggles for score, percentage, percentile, season, difficulty, stars, max distance
- **Metadata Display Order** section: `ReorderList` for metadata column ordering
- **Score Filtering** section: invalid score filter toggle + leeway slider (`LeewaySlider`)
- **Feature Flags** section: experimental ranks toggle, light trails toggle
- **First-Run Tutorials** section: replay buttons for all 9 page tutorials
- **Version Info**: app version, core version, theme version, service version
- **Reset** button: full settings reset with confirmation dialog

### API Calls
- `api.getVersion()` — service version display

### Contexts/Hooks Used
- `useSettings()` — read + write all settings
- `useFeatureFlags()` — feature flag display
- `useRegisterFirstRun()` — register all 9 tutorial slide sets
- `useFirstRunReplay()` — replay individual tutorials

### Components Used
- `ToggleRow` — setting toggle with label and description
- `SectionHeader` — section title with description
- `ReorderList` — drag-to-reorder list
- `FrostedCard` — frosted glass card wrapper
- `ConfirmAlert` — reset confirmation dialog
- `FirstRunCarousel` — tutorial replay overlay
- `InstrumentIcon` — instrument icons in toggle rows
- `LeewaySlider` — custom range slider for score filtering leeway

---

## Shell

**Routes**: All (wraps every page)
**Files**: `App.tsx`, `components/shell/`
**Player required**: No

### Layout Architecture
Two layout modes determined by `useIsWideDesktop()`:

**Mobile / Standard Desktop** (< wide breakpoint):
- `MobileHeader` — title, hamburger, back link
- `BottomNav` — tab navigation (songs, suggestions, statistics, compete, settings)
- `FloatingActionButton` (FAB) — context-aware actions per route
- `Sidebar` — slide-out drawer with player profile, navigation, deselect

**Wide Desktop** (≥ wide breakpoint):
- `DesktopNav` — top navigation bar with profile button
- `PinnedSidebar` — always-visible sidebar with player profile, navigation
- `WideDesktopLayout` — three-column layout (sidebar gutter, center content, right gutter)
- Header portal overlay — per-page headers rendered into shell header zone

### Shell Components (`components/shell/`)

**Desktop** (`shell/desktop/`):
- `DesktopNav.tsx` — top nav bar
- `PinnedSidebar.tsx` — persistent sidebar for wide desktop
- `Sidebar.tsx` — slide-out sidebar
- `HeaderSearch.tsx` — search bar in header
- `HeaderProfileButton.tsx` — profile avatar/button
- `sidebarStyles.ts` — shared sidebar styles

**Mobile** (`shell/mobile/`):
- `MobileHeader.tsx` — mobile header with title + navigation
- `BottomNav.tsx` — bottom tab navigation
- `BackLink.tsx` — hierarchical back navigation
- `MobilePlayerSearchModal.tsx` — player search/select modal

**FAB** (`shell/fab/`):
- `FloatingActionButton.tsx` — floating action button with context-aware menu
- `FABMenu.tsx` — expandable action menu

**Other Shell Components**:
- `AnimatedBackground.tsx` — animated particle/album art background
- `HamburgerButton.tsx` — sidebar toggle button

### Contexts/Hooks Used
- `useTrackedPlayer()` — player state for navigation
- `useIsMobileChrome()` / `useIsMobile()` / `useIsWideDesktop()` — responsive breakpoints
- `useFabSearch()` — FAB action registration and sync
- `useShopState()` — shop visibility for FAB actions
- `useTabNavigation()` — per-tab navigation stack (mobile)
- `useProximityGlow()` — frosted card light trails
- `useFirstRunContext()` — suppress changelog during first-run
- `ScrollContainerProvider` — shared scroll container ref
- `FabSearchProvider` — FAB search context
- `SearchQueryProvider` — shared search query
- `FeatureFlagsProvider` — feature flags
- `SettingsProvider` — user settings
- `FestivalProvider` — song data
- `ShopProvider` — shop data
- `FirstRunProvider` — first-run tutorial state
- `PlayerDataProvider` — player data context

### Global Modals
- `ChangelogModal` — shown on new version
- `ConfirmAlert` — deselect player confirmation
- `MobilePlayerSearchModal` — player search with tracking/navigation

### Navigation
- `HashRouter` (react-router-dom) — hash-based routing
- Mobile: `BottomNav` 5-tab system with per-tab stacks
- Desktop: `DesktopNav` + sidebar
- Hierarchical back-nav: `/songs/X/Y/history` → `/songs/X/Y` → `/songs/X` → `/songs`
- Route-aware FAB: different action groups per route (sort/filter for songs, toggle for rivals, etc.)

---

## Compete

**Route**: `/compete`
**Page**: `pages/compete/CompetePage.tsx` (lazy loaded)
**Feature flag**: `compete`
**Player required**: Yes

### Page Structure
- `Page` with `scrollRestoreKey="compete"`
- `PageHeader` with title
- **Leaderboard Preview**: top-10 leaderboard entries (combo if 2+ instruments, per-instrument otherwise)
- Player's own ranking position highlighted
- **"View Full Rankings"** link → navigates to `/leaderboards/all`
- **Rivals Preview**: 3 above + 3 below from first visible instrument
- `RivalRow` per rival entry
- Link to full rivals page

### API Calls (React Query)
- `api.getRankings(instrument, 'totalscore', 1, 10)` or `api.getComboRankings(comboId, ...)` — top-10
- `api.getPlayerRanking(instrument, accountId)` or `api.getPlayerComboRanking(...)` — player rank
- `api.getRivalsList(accountId, previewInstrument)` — closest rivals

### Contexts/Hooks Used
- `useTrackedPlayer()` — player identity (required)
- `useSettings()` — visible instruments, experimental ranks
- `usePageTransition()` — loading phase
- `useStagger()` — entry animation
- React Query (`useQuery`)

### Components Used
- `RankingEntry` (from leaderboards)
- `RivalRow` (from rivals)

---

## Route Map

| Route | Feature Area | Page Component | Lazy | Feature Gate | Player Required |
|---|---|---|---|---|---|
| `/` | — | `Navigate` → `/songs` | — | — | — |
| `/songs` | Songs | `SongsPage` | No | — | No |
| `/songs/:songId` | Songs | `SongDetailPage` | Yes | — | No |
| `/songs/:songId/:instrument` | Songs | `LeaderboardPage` | Yes | — | No |
| `/songs/:songId/:instrument/history` | Songs | `PlayerHistoryPage` | Yes | — | No* |
| `/player/:accountId` | Player | `PlayerPage` | Yes | — | No |
| `/statistics` | Player | `PlayerPage` (tracked) | Yes | — | Yes |
| `/rivals` | Rivals | `RivalsPage` | Yes | `rivals` | Yes |
| `/rivals/all` | Rivals | `AllRivalsPage` | Yes | `rivals` | Yes |
| `/rivals/:rivalId` | Rivals | `RivalDetailPage` | Yes | `rivals` | Yes |
| `/rivals/:rivalId/rivalry` | Rivals | `RivalryPage` | Yes | `rivals` | Yes |
| `/suggestions` | Suggestions | `SuggestionsPage` | Yes | — | Yes |
| `/shop` | Shop | `ShopPage` | Yes | `shop` | No |
| `/leaderboards` | Leaderboards | `LeaderboardsOverviewPage` | Yes | `leaderboards` | No |
| `/leaderboards/all` | Leaderboards | `FullRankingsPage` | Yes | `leaderboards` | No |
| `/compete` | Compete | `CompetePage` | Yes | `compete` | Yes |
| `/settings` | Settings | `SettingsPage` | Yes | — | No |

\* PlayerHistoryPage is accessible without a tracked player but shows no data.

---

## Shared Infrastructure

### Contexts (9)
| Context | Purpose |
|---|---|
| `FestivalContext` | Song list, loading state, current season |
| `SettingsContext` | User preferences (instruments, metadata, filters) |
| `ShopContext` | Shop data provider (WebSocket + polling) |
| `PlayerDataContext` | Precomputed player data for tracked player |
| `ScrollContainerContext` | Shared scroll container ref + header portal |
| `FabSearchContext` | FAB action registration and global search state |
| `SearchQueryContext` | Songs page search query (shared for FAB) |
| `FeatureFlagsContext` | Feature flag values from server |
| `FirstRunContext` | First-run tutorial carousel state |

### Hook Categories
| Category | Path | Examples |
|---|---|---|
| Data hooks | `hooks/data/` | `useTrackedPlayer`, `useShopState`, `useFilteredSongs`, `useScoreFilter`, `useSuggestions`, `useSyncStatus`, `useLoadPhase`, `useSortedScoreHistory`, `useSongLookups`, `useAccountSearch`, `useVersions`, `useShopWebSocket`, `useAvailableSeasons`, `useDemoSongs` |
| UI hooks | `hooks/ui/` | `useIsMobile`, `useMediaQuery`, `useModalState`, `useStagger`, `useStaggerStyle`, `useStaggerRush`, `useScrollRestore`, `useScrollMask`, `useScrollFade`, `usePageTransition`, `useViewTransition`, `useProximityGlow`, `useGridColumnCount`, `useFirstRun`, `useRegisterFirstRun`, `useTabNavigation`, `useLeaderboardColumns`, `useVisualViewport` |
| Navigation hooks | `hooks/navigation/` | `useNavigateToSongDetail` |
| Chart hooks | `hooks/chart/` | `useChartData`, `useChartDimensions`, `useChartPagination`, `useCardAnimation`, `useListAnimation` |

### React Query
- `queryClient.ts` — configured QueryClient
- `queryKeys.ts` — typed key factory (songs, player, leaderboard, rankings, rivals, etc.)
- `pageCache.ts` — module-level caches for instant back-navigation (songDetail, leaderboard, rankings)

### Shared Components
| Path | Purpose |
|---|---|
| `components/common/` | PageHeader, EmptyState, ActionPill, FrostedCard, ArcSpinner, RadioRow, ToggleRow, SectionHeader, SuspenseFallback |
| `components/display/` | InstrumentHeader, InstrumentIcons |
| `components/page/` | Page shell, LoadGate, SyncBanner, FadeIn, ErrorBoundary, RouteErrorFallback |
| `components/leaderboard/` | PaginatedLeaderboard (shared pagination) |
| `components/songs/` | SongInfoHeader and related headers |
| `components/modals/` | Modal, ChangelogModal, ConfirmAlert, ModalSection |
| `components/sort/` | ReorderList |
| `components/routing/` | FeatureGate |
| `components/firstRun/` | FirstRunCarousel |
| `components/player/` | Player-related display components |
