# FortniteFestivalWeb — State Management Documentation

> Last updated: 2026-04-03

---

## Context Catalog

### 1. FestivalContext (`contexts/FestivalContext.tsx`)

**Purpose**: Global song catalog — the primary data source for the entire app.

**State shape**:
```ts
type FestivalState = {
  songs: Song[];
  currentSeason: number;
  isLoading: boolean;
  error: string | null;
};
```

**Methods**:
- `refresh()` — Invalidates the `queryKeys.songs()` React Query cache, triggering a re-fetch.

**Data source**: React Query → `api.getSongs()` with `queryKeys.songs()`.
- `staleTime`: 5 min (revalidation is cheap via ETag 304).
- `initialData`: Instant render from localStorage `fst_songs_cache` while ETag revalidation runs.

**Hook**: `useFestival()` — throws if used outside provider.

---

### 2. SettingsContext (`contexts/SettingsContext.tsx`)

**Purpose**: User preferences — instrument visibility, metadata display, score filtering, UI toggles.

**State shape** (`AppSettings`):
```ts
{
  // Song display
  songsHideInstrumentIcons: boolean;
  songRowVisualOrderEnabled: boolean;
  songRowVisualOrder: string[];
  filterInvalidScores: boolean;
  filterInvalidScoresLeeway: number;
  enableExperimentalRanks: boolean;
  disableLightTrails: boolean;
  // Item shop
  hideItemShop: boolean;
  disableShopHighlighting: boolean;
  // Instrument visibility (6 booleans: Lead, Bass, Drums, Vocals, ProLead, ProBass)
  showLead / showBass / showDrums / showVocals / showProLead / showProBass: boolean;
  // Metadata visibility (7 booleans)
  metadataShowScore / metadataShowPercentage / metadataShowPercentile /
  metadataShowSeasonAchieved / metadataShowDifficulty / metadataShowStars /
  metadataShowMaxDistance: boolean;
}
```

**Methods**:
- `setSettings(s)` — Full replacement.
- `updateSettings(partial)` — Merge partial update.
- `resetSettings()` — Restore defaults.

**Persistence**: localStorage `fst:appSettings` — auto-saved on every change via `useEffect`.

**Helpers**:
- `isInstrumentVisible(settings, instrumentKey)` — Resolves instrument key → show flag.
- `visibleInstruments(settings)` — Returns array of visible instrument keys.

**Hook**: `useSettings()` — throws if used outside provider.

---

### 3. FeatureFlagsContext (`contexts/FeatureFlagsContext.tsx`)

**Purpose**: Feature gating — determines which features are enabled/disabled.

**State shape**:
```ts
type FeatureFlags = {
  shop: boolean;
  rivals: boolean;
  compete: boolean;
  leaderboards: boolean;
  firstRun: boolean;
  difficulty: boolean;
};
```

**Data source**:
- **Production**: React Query → `GET /api/features`, staleTime 30 min, gcTime 60 min, retry 1, no window refocus.
- **Dev mode**: All flags ON by default. Overridable via localStorage `fst:featureFlagOverrides`.

**Hook**: `useFeatureFlags()` — throws if used outside provider.

---

### 4. ShopContext (`contexts/ShopContext.tsx`)

**Purpose**: Item shop state — which songs are currently in the shop.

**State shape**:
```ts
type ShopContextValue = {
  shopSongIds: ReadonlySet<string> | null;      // All in-shop song IDs
  leavingTomorrowIds: ReadonlySet<string> | null; // IDs expiring tomorrow
  connected: boolean;                             // WebSocket status
  getShopUrl: (songId: string) => string | undefined;
  shopSongs: ShopSong[];                         // Enriched shop song objects
};
```

**Data sources** (layered):
1. **REST**: `api.getShop()` on mount (initial data).
2. **WebSocket**: `useShopWebSocket()` for live `shop_snapshot` and `shop_changed` deltas.
3. Enriches shop songs with `albumArt` from the full catalog (FestivalContext).

**Dependencies**: FestivalContext (for song catalog enrichment), FeatureFlagsContext (shop flag gate).

**Hook**: `useShop()` — throws if used outside provider.

---

### 5. PlayerDataContext (`contexts/PlayerDataContext.tsx`)

**Purpose**: Currently viewed player's data + sync status.

**State shape**:
```ts
type PlayerDataContextValue = {
  playerData: PlayerResponse | null;
  playerLoading: boolean;
  playerError: string | null;
  refreshPlayer: () => Promise<void>;
  isSyncing: boolean;
  syncPhase: SyncPhase;        // Idle | Backfill | History | Complete | Error
  backfillProgress: number;     // 0..1
  historyProgress: number;      // 0..1
};
```

**Data source**: React Query → `api.getPlayer(accountId)` with `queryKeys.player(accountId)`.
- Auto-invalidates when `useSyncStatus` detects sync completion.

**Note**: Provider receives `accountId` as a prop — mounted conditionally when a player is selected.

**Hook**: `usePlayerData()` — throws if used outside provider.

---

### 6. FirstRunContext (`contexts/FirstRunContext.tsx`)

**Purpose**: First-run experience (FRE) — manages which tutorial slides have been seen.

**State shape**:
```ts
type FirstRunContextValue = {
  enabled: boolean;            // From FeatureFlags.firstRun
  register / unregister: ...;  // Page slide registration
  getUnseenSlides: (pageKey, ctx) => FirstRunSlideDef[];
  getAllSlides: (pageKey) => FirstRunSlideDef[];
  markSeen: (slides) => void;
  resetPage / resetAll: ...;
  registeredPages: { pageKey, label }[];
  activeCarouselKey: string | null;
  setActiveCarousel: (key | null) => void;
};
```

**Storage mechanism**: Registry is in-memory (`useRef<Map>`). Seen state persists to localStorage `fst:firstRun` via `loadSeenSlides()` / `saveSeenSlides()`.

**Dependencies**: FeatureFlagsContext (firstRun flag).

**Hook**: `useFirstRunContext()` — throws if used outside provider.

---

### 7. FabSearchContext (`contexts/FabSearchContext.tsx`)

**Purpose**: Floating Action Button (FAB) action dispatch — decouples the FAB from page-specific actions.

**Pattern**: Pages register their action callbacks (sort, filter, toggle, etc.) on mount. The FAB reads and invokes them.

**State shape** — callback refs + small UI state:
```ts
{
  // Song page: openSort, openFilter
  // Suggestions page: openSuggestionsFilter
  // Player history: openPlayerHistorySort
  // Song detail: openPaths
  // Shop: shopToggleView, shopViewMode ('grid' | 'list')
  // Leaderboards: openLeaderboardMetric, openLeaderboardInstrument
  // Rivals: rivalsToggleTab, rivalsActiveTab ('song' | 'leaderboard')
  // Player page: playerPageSelect { displayName, onSelect }
}
```

**Design**: Uses `useRef` for action callbacks to avoid re-renders. Only `shopViewMode`, `rivalsActiveTab`, and `playerPageSelect` are React state.

**Hook**: `useFabSearch()` — no throw, returns defaults if used outside provider.

---

### 8. SearchQueryContext (`contexts/SearchQueryContext.tsx`)

**Purpose**: Global search text input — separated from FabSearchContext to avoid re-renders on every keystroke.

**State**: `{ query: string; setQuery: (q: string) => void }`

**Hook**: `useSearchQuery()`

---

### 9. ScrollContainerContext (`contexts/ScrollContainerContext.tsx`)

**Purpose**: Provides a ref to the app shell's scroll container + a header portal target.

**Subcontexts**:
1. **ScrollContainerContext** — `RefObject<HTMLDivElement>` for the scroll container.
2. **HeaderPortalContext** — Portal target node for page headers + CSS custom property `--header-portal-h` (set via ResizeObserver, bypasses React to avoid re-render cascades during scroll animations).

**Hooks**:
- `useScrollContainer()` — Returns the scroll container ref.
- `useHeaderPortal()` — Returns the portal target DOM node.
- `useHeaderPortalRef()` — Returns a ref callback to assign to the portal div.
- `useShellRefs()` — Convenience for App.tsx to get both refs.

---

## Provider Stack

Nesting order in `App.tsx`, outermost first:

```
QueryClientProvider          ← React Query client
  FeatureFlagsProvider       ← Feature flags (needs QueryClient for API fetch)
    SettingsProvider         ← User prefs (no dependencies)
      FestivalProvider       ← Song catalog (needs QueryClient; SettingsProvider is a sibling dep)
        ShopProvider         ← Item shop (needs Festival for enrichment, FeatureFlags for gate)
          FirstRunProvider   ← FRE (needs FeatureFlags)
            FabSearchProvider ← FAB actions (no upstream deps)
              SearchQueryProvider ← Search text (no deps)
                HashRouter         ← Routing (must be inside providers that pages consume)
                  ScrollContainerProvider ← Scroll refs (needs to be inside router for location awareness)
                    AppShell       ← Layout + routes
```

**Why this order matters**:
- `QueryClientProvider` must wrap everything that uses `useQuery`.
- `FeatureFlagsProvider` must be above `ShopProvider` and `FirstRunProvider` (they read flags).
- `FestivalProvider` must be above `ShopProvider` (shop enrichment reads songs).
- `HashRouter` must be inside all data providers so pages can access them.
- `ScrollContainerProvider` must be inside `HashRouter` so it can react to route changes.
- `PlayerDataProvider` is NOT in the global stack — it's rendered conditionally inside `AppShell` when a tracked player exists.

---

## React Query Patterns

### QueryClient Configuration (`api/queryClient.ts`)

```ts
{
  staleTime: 5 * 60 * 1000,   // 5 min default
  gcTime: 10 * 60 * 1000,     // 10 min garbage collection
  retry: 1,
  refetchOnWindowFocus: false,
}
```

### Query Key Factory (`api/queryKeys.ts`)

All query keys are produced by `queryKeys.*` functions. Full registry:

| Key | Factory | Used By |
|-----|---------|---------|
| `['songs']` | `queryKeys.songs()` | FestivalContext |
| `['features']` | `['features']` (inline) | FeatureFlagsContext |
| `['player', accountId, {songId, instruments, leeway}]` | `queryKeys.player(...)` | PlayerDataContext, PlayerPage |
| `['playerHistory', accountId, {songId, instrument}]` | `queryKeys.playerHistory(...)` | useChartData |
| `['syncStatus', accountId]` | `queryKeys.syncStatus(...)` | useSyncStatus |
| `['leaderboard', songId, instrument, {top, offset, leeway}]` | `queryKeys.leaderboard(...)` | LeaderboardPage |
| `['allLeaderboards', songId, {top, leeway}]` | `queryKeys.allLeaderboards(...)` | SongDetailPage |
| `['playerStats', accountId]` | `queryKeys.playerStats(...)` | PlayerContent |
| `['version']` | `queryKeys.version()` | useVersions |
| `['rivalsOverview', accountId]` | `queryKeys.rivalsOverview(...)` | RivalsPage |
| `['rivalsList', accountId, combo]` | `queryKeys.rivalsList(...)` | rivals pages |
| `['rivalDetail', accountId, combo, rivalId]` | `queryKeys.rivalDetail(...)` | RivalDetailPage |
| `['rankings', instrument, {rankBy, page, pageSize}]` | `queryKeys.rankings(...)` | FullRankingsPage |
| `['playerRanking', instrument, accountId]` | `queryKeys.playerRanking(...)` | FullRankingsPage |
| `['compositeRankings', {page, pageSize}]` | `queryKeys.compositeRankings(...)` | FullRankingsPage |
| `['playerCompositeRanking', accountId]` | `queryKeys.playerCompositeRanking(...)` | FullRankingsPage |
| `['comboRankings', comboId, {rankBy, page, pageSize}]` | `queryKeys.comboRankings(...)` | rankings |
| `['playerComboRanking', accountId, comboId, {rankBy}]` | `queryKeys.playerComboRanking(...)` | rankings |
| `['leaderboardNeighborhood', instrument, accountId]` | `queryKeys.leaderboardNeighborhood(...)` | leaderboards |
| `['compositeNeighborhood', accountId]` | `queryKeys.compositeNeighborhood(...)` | leaderboards |

### Per-Query Overrides

| Query | staleTime | gcTime | Other |
|-------|-----------|--------|-------|
| songs | 5 min | default (10 min) | `initialData` from localStorage |
| features | 30 min | 60 min | `retry: 1`, disabled in DEV |
| playerHistory | 5 min | default | — |
| player | default | default | `enabled: !!accountId` |

### Invalidation Pattern
Queries are invalidated via `queryClient.invalidateQueries({ queryKey: ... })`. Used when:
- User triggers manual refresh (FestivalContext.refresh, PlayerDataContext.refreshPlayer)
- Sync completion detected (PlayerDataContext auto-invalidates)
- Player deselection (App.tsx clears caches)

### No useMutation
The app is read-only (no client-initiated writes to the API). `useMutation` is not used anywhere.

---

## localStorage Usage

| Key | Location | Purpose | Shape |
|-----|----------|---------|-------|
| `fst_songs_cache` | `api/client.ts`, `FestivalContext.tsx` | Songs catalog + ETag for instant render | `{ data: SongsResponse, etag: string }` |
| `fst:appSettings` | `SettingsContext.tsx` | User preferences (instruments, metadata, UI toggles) | `AppSettings` object |
| `fst:trackedPlayer` | `hooks/data/useTrackedPlayer.ts` | Currently tracked player identity | `{ accountId, displayName }` |
| `fst:songSettings` | `utils/songSettings.ts` | Song list sort/filter/instrument state | `SongSettings` object |
| `fst:leaderboardSettings` | `utils/leaderboardSettings.ts` | Leaderboard ranking metric preference | `{ rankBy: RankingMetric }` |
| `fst:firstRun` | `firstRun/types.ts` | First-run seen slide records | `Record<slideId, { version, hash, seenAt }>` |
| `fst:featureFlagOverrides` | `FeatureFlagsContext.tsx` | Dev-only feature flag overrides | `Partial<FeatureFlags>` |
| `fst:changelog` | `App.tsx` | Changelog last-seen version/hash | `{ version, hash }` |
| `fst-suggestions-filter` | `suggestionsHelpers.ts` (×2 copies) | Suggestions page filter state | `SuggestionsFilterDraft` |

### Cross-Tab Sync
- `useTrackedPlayer` listens for both custom `fst:trackedPlayerChanged` events AND the native `storage` event for cross-tab synchronization.
- `songSettings` dispatches a custom `fst:songSettingsChanged` event but doesn't listen cross-tab.

---

## URL State

### Router Type
**HashRouter** — all routes are hash-based (`/#/songs`, `/#/rivals/all?category=common`).

### Route Definitions (`routes.ts`)

| Route | Pattern | Params |
|-------|---------|--------|
| Songs list | `/songs` | — |
| Song detail | `/songs/:songId` | songId (path) |
| Leaderboard | `/songs/:songId/:instrument` | songId, instrument (path) |
| Player history | `/songs/:songId/:instrument/history` | songId, instrument (path) |
| Player | `/player/:accountId` | accountId (path) |
| Rivals overview | `/rivals` | `?tab=`, `?rankBy=` |
| All rivals | `/rivals/all` | `?category=` |
| Rival detail | `/rivals/:rivalId` | rivalId (path), `?name=` |
| Rivalry | `/rivals/:rivalId/rivalry` | rivalId (path), `?mode=` |
| Statistics | `/statistics` | — (uses tracked player) |
| Suggestions | `/suggestions` | — (uses tracked player) |
| Compete | `/compete` | — |
| Shop | `/shop` | — |
| Leaderboards | `/leaderboards` | `?rankBy=` |
| Full rankings | `/leaderboards/all` | `?instrument=`, `?rankBy=` |
| Settings | `/settings` | — |

### useSearchParams Usage

| Page | Params Read | Params Written |
|------|-------------|----------------|
| RivalsPage | `tab`, `rankBy` | `tab`, `rankBy` (via setSearchParams) |
| RivalDetailPage | `name` | — |
| RivalryPage | `mode` | — |
| AllRivalsPage | `category` | — |
| LeaderboardsOverviewPage | `rankBy` | `rankBy` |
| FullRankingsPage | `instrument`, `rankBy`, `page`, `pageSize` | `instrument`, `rankBy`, `page` |
| LeaderboardPage | `page`, `navToPlayer` | `page` |

### Feature-Gated Routes
Routes for `shop`, `rivals`, `leaderboards`, and `compete` are wrapped in `<FeatureGate flag="...">`. Player-dependent routes (`rivals`, `statistics`, `suggestions`, `compete`) redirect to `/songs` when no tracked player exists.

---

## Page-Level Caches (`api/pageCache.ts`)

In-memory `Map` caches for UI state that survives back-navigation (NOT data caches):

| Cache | Key | Stores |
|-------|-----|--------|
| `songDetailCache` | songId | instrumentData, scoreHistory, accountId, scrollTop |
| `leaderboardCache` | composite key | entries, totalEntries, localEntries, page, scrollTop |
| `rankingsCache` | composite key | page, scrollTop |

These are cleared on player change/deselection in App.tsx.

---

## Custom Hooks

### Data Hooks (`hooks/data/`)

| Hook | Purpose | State Type |
|------|---------|------------|
| `useTrackedPlayer` | Tracked player identity with localStorage sync | `{ player, setPlayer, clearPlayer }` |
| `useFilteredSongs` | Song list filtering/sorting (pure computation) | `Song[]` (memoized) |
| `useScoreFilter` | CHOpt-based score validation at configurable leeway | `{ isScoreValid, getFilteredScore, ... }` |
| `useSongLookups` | Derived Maps from song catalog (albumArt, year) | `{ albumArtMap, yearMap }` |
| `useAvailableSeasons` | `1..currentSeason` array | `number[]` |
| `useShopState` | Combines ShopContext + Settings + FeatureFlags | `{ isShopHighlighted, isInShop, isLeavingTomorrow, ... }` |
| `useShopWebSocket` | WebSocket for live shop deltas/snapshots | `ShopState` |
| `useSyncStatus` | Polls `/api/sync-status/:id` for backfill/history progress | `SyncState` + `justCompleted` flag |
| `useAccountSearch` | Debounced account search with keyboard nav | `AccountSearchState` |
| `useSuggestions` | Suggestion generator with module-level cache | `{ categories, hasMore, loadMore }` |
| `useLoadPhase` | Loading → SpinnerOut → ContentIn state machine | `{ phase, shouldStagger }` |
| `useSortedScoreHistory` | Sort score history entries by mode + direction | `ScoreHistoryEntry[]` |
| `useDemoSongs` | First-run demo song rows with auto-rotation | `{ rows, fadingIdx, initialDone, pool }` |
| `useItemShopDemoSongs` | First-run shop demo songs (live or fallback) | `{ songs, isLive }` |
| `useVersions` | Build-time version constants | `APP_VERSION, CORE_VERSION, THEME_VERSION` |

### UI Hooks (`hooks/ui/`)

| Hook | Purpose |
|------|---------|
| `useIsMobile` / `useIsMobileChrome` / `useIsWideDesktop` | Device class detection |
| `useMediaQuery` | Generic CSS media query hook |
| `useTabNavigation` | Tab/nav active state management |
| `useFirstRun` | Per-page FRE carousel lifecycle |
| `useRegisterFirstRun` | Register page's first-run slides |
| `useLoadPhase` | Loading phase state machine |
| `useStagger` / `useStaggerRush` / `useStaggerStyle` | Stagger animation orchestration |
| `useScrollFade` / `useScrollMask` / `useScrollRestore` | Scroll behavior hooks |
| `useModalState` / `useModalDraft` | Modal open/close + draft state |
| `useProximityGlow` | Cursor proximity glow effect |
| `usePageTransition` / `useViewTransition` | Page transition animations |
| `useFadeSpinner` | Spinner fade-out timing |
| `useGridColumnCount` / `useLeaderboardColumns` | Responsive column calculation |
| `useVisualViewport` | Visual viewport tracking (mobile keyboard) |

### Chart Hooks (`hooks/chart/`)

| Hook | Purpose |
|------|---------|
| `useChartData` | Fetch + transform score history into chart points (uses React Query) |
| `useChartDimensions` | Responsive chart sizing |
| `useChartPagination` | Chart data pagination |
| `useCardAnimation` / `useListAnimation` | Chart entry animations |

### Navigation Hooks (`hooks/navigation/`)

| Hook | Purpose |
|------|---------|
| `useNavigateToSongDetail` | Navigate to song detail with transition |

---

## State Flow

### Data Loading Flow
```
App mount
  → QueryClientProvider creates queryClient
  → FeatureFlagsProvider fetches /api/features (or uses ALL_ON in dev)
  → FestivalProvider starts useQuery(songs) with localStorage initialData
  → ShopProvider fetches /api/shop + opens WebSocket
  → AppShell reads useTrackedPlayer from localStorage
    → If player exists: mounts PlayerDataProvider(accountId)
      → useQuery(player) + useSyncStatus polling
```

### Song Data Flow
```
FestivalContext.songs
  ├── SongsPage → useFilteredSongs(songs, search, sort, filters)
  ├── ShopContext → enriches shopSongs with albumArt
  ├── SongDetailPage → filters by songId
  ├── useSongLookups → derived albumArtMap, yearMap
  ├── useScoreFilter → builds threshold maps from maxScores
  └── useSuggestions → converts to CoreSong format
```

### Settings Flow
```
SettingsContext.settings
  ├── SongsPage → instrument visibility, metadata display order
  ├── useShopState → hideItemShop, disableShopHighlighting
  ├── useScoreFilter → filterInvalidScores, filterInvalidScoresLeeway
  ├── useSuggestions → instrument visibility for category filtering
  └── Various components → UI toggle flags
```

### Player Identity Flow
```
useTrackedPlayer (localStorage fst:trackedPlayer)
  → AppShell reads player
    → Conditionally renders PlayerDataProvider
    → Conditionally enables /rivals, /statistics, /suggestions, /compete routes
    → Player change → clears pageCache, songDetailCache, leaderboardCache
```

### FAB Action Flow
```
Page mounts → registers actions via useFabSearch().registerXxxActions(callbacks)
  → FAB reads current route → picks matching action set
  → User taps FAB → invokes registered callback (openSort, openFilter, etc.)
  → Page handles callback (opens modal, toggles view, etc.)
```
