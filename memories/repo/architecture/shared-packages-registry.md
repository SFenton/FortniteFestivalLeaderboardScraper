# Shared TypeScript Packages Registry

> Last updated: 2026-04-03

## Package Overview

| Package | npm Name | Version | Purpose | Consumers |
|---|---|---|---|---|
| `packages/core` | `@festival/core` | 0.0.7 | Domain models, enums, API types, formatters, filtering, suggestions engine, combo IDs, i18n | Web, RN, Native |
| `packages/theme` | `@festival/theme` | 0.0.2 | Design tokens (colors, spacing, typography, animation timing), CSS enum constants, style factories, breakpoints | Web, (RN indirectly) |
| `packages/ui-utils` | `@festival/ui-utils` | 0.0.1 | Shared UI utilities — stagger animation helpers, platform detection | Web |
| `packages/auth` | `@festival/auth` | 0.0.1 | Epic Games OAuth flow, FST auth/service HTTP clients, token parsing, session types | (RN planned, not yet imported) |
| `packages/native` | `@festival/native` | 0.0.1 | Native-only modules — MAUI/RN services, file persistence, calendar models, Epic content parsing | RN |

---

## @festival/core

**Entry point:** `src/index.ts` — barrel re-exports from all modules.

### Source Files

| File | Exports | Purpose |
|---|---|---|
| `instruments.ts` | `InstrumentKeys`, `InstrumentKey` (type) | 6 instrument keys: guitar, bass, drums, vocals, pro_guitar, pro_bass |
| `combos.ts` | `comboIdFromInstruments()`, `instrumentsFromComboId()`, `isMultiInstrumentCombo()`, `COMBO_INSTRUMENTS`, multi-instrument maps | Bitmask-based combo ID system for instrument combinations |
| `enums.ts` | `LoadPhase`, `PlayerScoreSortMode`, `Difficulty`, `CardPhase`, `ListPhase`, `ImagePhase`, `FabMode`, `AnimMode`, `TabKey`, `RowLayout`, `SyncPhase` | Shared UI state machine enums |
| `keys.ts` | `Keys` (ArrowDown, ArrowUp, Escape, Enter, Tab, Space) | Keyboard key constants |
| `stars.ts` | `MAX_DISPLAY_STARS`, `GOLD_STARS_THRESHOLD`, `displayStarCount()` | Star display logic |
| `settings.ts` | `Settings` type, default factory functions | Full user settings shape (instruments, sort, filters, suggestions) |
| `models.ts` | `Track`, `Song`, `ScoreTracker`, `LeaderboardData`, `ScoreHistoryEntry`, `V1LeaderboardEntry`, `GameDifficulty`, etc. | Core domain models — songs, scores, leaderboard entries |
| `httpErrorHelper.ts` | `extractError()`, `formatHttpError()`, `buildSummaryLine()` | HTTP error parsing and formatting |
| `concurrency.ts` | `createLimiter()` | Promise concurrency limiter (max-N in-flight) |
| `persistence.ts` | `FestivalPersistence` (interface), `InMemoryFestivalPersistence` | Persistence abstraction for scores/songs/history |
| `songListConfig.ts` | `SongSortMode`, `AdvancedMissingFilters`, `MetadataSortKey`, `InstrumentShowSettings`, `percentileBucket()`, `PERCENTILE_THRESHOLDS`, default factories, `normalizeMetadataSortPriority()`, `normalizeInstrumentOrder()` | Song list sorting, filtering, and metadata configuration |
| `instrumentFilters.ts` | `shouldShowCategory()`, `filterCategoryForInstruments()` | Instrument visibility filtering for suggestions/statistics |
| `api/serverTypes.ts` | `ServerInstrumentKey`, `ServerSong`, `ShopSong`, `SongsResponse`, `LeaderboardEntry`, `LeaderboardResponse`, `PlayerScore`, `ValidScoreVariant`, `RankTier`, `PopulationTierData`, `WsNotificationMessage`, etc. | FSTService HTTP API response types (mirrors C# DTOs) |
| `i18n/index.ts` | `setTranslationFunction()`, `t()` | i18n function registry — host app wires i18next at init |
| `suggestions/types.ts` | `RivalInfo`, `RivalSongMatch`, `RivalDataIndex`, `SuggestionCategory`, `SuggestionSongItem` | Rival and suggestion domain types |
| `suggestions/suggestionGenerator.ts` | `createSeededRng()`, suggestion generation engine | Pure suggestion generation with seeded RNG |
| `suggestions/suggestionFilterConfig.ts` | `SUGGESTION_TYPES`, `SuggestionTypeSettings`, default factories, key builders | Declarative suggestion type registry |

### App-Level Pure Logic (`app/`)

| File | Key Exports | Purpose |
|---|---|---|
| `formatters.ts` | `formatIntegerWithCommas()`, `formatScoreCompact()`, `formatPercentileTopExact()`, `formatPercentileBucket()`, `formatAccuracy()`, `accuracyColor()`, `ACCURACY_SCALE` | Number/score/percentile formatting (ported from MAUI) |
| `scoreRows.ts` | `buildScoreRows()`, `ScoreRow` type | Score row view-model builder |
| `songInfo.ts` | `SongInfoInstrumentRow`, `formatPercent()`, `formatSeason()`, `composeRankOutOf()` | Song detail instrument row builder |
| `songFiltering.ts` | `instrumentHasFC()`, `songHasAllFCsPriority()`, song sort/filter utilities | Song list filtering and sorting engine + re-exports from songListConfig |
| `statistics.ts` | `InstrumentDetailedStats`, `instrumentKeysForStats` | Per-instrument statistics computation |
| `logBuffer.ts` | `BatchedLogBuffer` | Bounded log buffer with flush (ported from MAUI ProcessViewModel) |
| `progress.ts` | `computeProgressState()` | Progress computation (ported from MAUI) |
| `findIndexBy.ts` | `findIndexBy()` | Generic array search helper |

### Constants

- `APP_VERSION = '0.0.3'`
- `CORE_VERSION = '0.0.7'`
- `THEME_VERSION = '0.0.2'`

### Tests

16 test files in `__tests__/` covering combos, concurrency, HTTP errors, models, persistence, settings, song list config, formatters, score rows, song filtering, statistics, log buffer, progress, suggestion generator/filter config, token parsing.

---

## @festival/theme

**Entry point:** `src/index.ts` — explicit named exports (no barrel `*`).

### Source Files

| File | Key Exports | Purpose |
|---|---|---|
| `colors.ts` | `Colors` object (~80 color tokens), `ColorKey` type | Full color palette — backgrounds, surfaces, glass, text, borders, accents, gold, status, chart, accuracy, difficulty |
| `spacing.ts` | `Radius`, `Font`, `Weight`, `ZIndex`, `LineHeight`, `Gap`, `Opacity`, `Border`, `Shadow`, `SpinnerSize`, `Spinner`, `IconSize`, `InstrumentSize`, `StarSize`, `AlbumArtSize`, `MetadataSize`, `ChartSize`, `GeneralSize`, `Size` (deprecated), `MaxWidth`, `Layout` | Comprehensive spacing/sizing token system |
| `breakpoints.ts` | `MOBILE_BREAKPOINT` (768), `NARROW_BREAKPOINT` (420), `MEDIUM_BREAKPOINT` (520), `WIDE_DESKTOP_BREAKPOINT` (1440), `QUERY_*` media strings | Responsive breakpoints + pre-built media queries |
| `animation.ts` | `STAGGER_INTERVAL`, `FADE_DURATION`, `SPINNER_FADE_MS`, `DEBOUNCE_MS`, `TRANSITION_MS`, `EASE_SMOOTH`, `EASE_OVERSHOOT`, ~30 total timing/easing constants | Animation timing constants |
| `frostedStyles.ts` | `frostedCard`, `frostedCardSurface`, `frostedCardLight`, `modalOverlay`, `modalCard`, `btnPrimary`, `btnDanger`, `purpleGlass` | Glass/frosted style mixins (spread into inline styles) |
| `goldStyles.ts` | `goldFill`, `goldOutline`, `goldOutlineSkew`, `GOLD_SKEW` | Gold/FC badge style mixins |
| `factories.ts` | `flexColumn`, `flexRow`, `flexCenter`, `flexBetween`, `textBold`, `textSemibold`, `truncate`, `absoluteFill`, `fixedFill`, `centerVertical` | Reusable CSS property objects |
| `cssEnums.ts` | `Display`, `Position`, `Align`, `Justify`, `TextAlign`, `FontStyle`, `TextTransform`, `FontVariant`, `WordBreak`, `WhiteSpace`, `Isolation`, `TransformOrigin`, `BoxSizing`, `BorderStyle`, `Overflow`, `ObjectFit`, `Cursor`, `PointerEvents`, `CssValue`, `CssProp`, `GridTemplate` | CSS keyword enums (eliminates string literals) |
| `cssHelpers.ts` | `border()`, `padding()`, `margin()`, `transition()`, `transitions()`, `scale()`, `translateY()`, `scaleTranslateY()` | CSS value builder functions |
| `pagination.ts` | `LEADERBOARD_PAGE_SIZE` (25), `SUGGESTIONS_BATCH_SIZE` (6), `SUGGESTIONS_INITIAL_BATCH` (10), `SCROLL_PREFETCH_PX` (600) | Pagination/batch constants |
| `polling.ts` | `SYNC_POLL_ACTIVE_MS` (3000), `SYNC_POLL_IDLE_MS` (60000) | Polling interval constants |

### Design Decisions

- **Framework-agnostic**: Uses a minimal `CSSProperties = Record<string, string | number | undefined>` type in factories.ts so it doesn't depend on React.
- **`Size` object is deprecated**: Consumers should use granular objects (`IconSize`, `InstrumentSize`, `StarSize`, etc.) instead.
- **Frosted card variants**: `frostedCard` (full, with noise SVG), `frostedCardSurface` (no marker), `frostedCardLight` (no noise, no shadow — for list items).

---

## @festival/ui-utils

**Entry point:** `src/index.ts` — exports from `stagger.ts` and `platform.ts`.

### Source Files

| File | Exports | Purpose |
|---|---|---|
| `stagger.ts` | `staggerDelay(index, interval, maxItems)`, `estimateVisibleCount(itemHeight)` | Stagger animation timing — returns delay or `undefined` for off-screen items |
| `platform.ts` | `IS_IOS`, `IS_ANDROID`, `IS_PWA`, `IS_MOBILE_DEVICE` | Platform detection via user-agent + display-mode; supports `?forceios` / `?forceandroid` / `?forcepwa` / `?forcedesktop` query params for testing |

### Tests

1 test file: `stagger.test.ts`.

---

## @festival/auth

**Entry point:** `src/index.ts` — barrel re-exports from all modules.

### Source Files

| File | Key Exports | Purpose |
|---|---|---|
| `authTypes.ts` | `AuthMode` enum (Local/Service), storage keys (`AUTH_MODE_STORAGE_KEY`, `SERVICE_ENDPOINT_KEY`, `AUTH_SESSION_KEY`), `AuthLoginResponse`, `AuthRefreshResponse`, `AuthSession` | Auth mode, session types, storage key constants |
| `tokenParsing.ts` | `parseExchangeCodeToken()`, `parseTokenVerify()` | Safe JSON parsing for Epic exchange code tokens and verify responses |
| `exchangeCode.types.ts` | `ExchangeCodeToken` type | Full Epic exchange code token shape (~20 fields) |
| `fstAuthClient.ts` | `FstAuthClient` class, `FstAuthError` | HTTP client for FST auth endpoints: login, refresh, logout |
| `fstServiceClient.ts` | `FstServiceClient` class, `FstServiceError`, `AccountCheckResult`, `ServiceVersionResult` | HTTP client for FST data endpoints: checkAccount, getServiceVersion, getWebSocketUrl |
| `epicOAuth.ts` | `EpicAuthResult`, `buildEpicAuthConfig()`, platform-specific OAuth flow | Epic Games OAuth (React Native only — imports from `react-native`) |

### Tests

1 test file: `fstAuthClient.test.ts`.

> **Note**: `epicOAuth.ts` imports from `react-native` and `react-native-app-auth`, making it RN-specific. The web app stubs these via Vite aliases.

---

## @festival/native

**Entry point:** `src/index.ts` — re-exports from services, IO, Epic, persistence, calendar modules.

### Source Directories

| Directory | Purpose |
|---|---|
| `services/` | `FestivalService` type + implementation (high-level scraping orchestration) |
| `io/` | `JsonSerializer` — JSON serialization utilities |
| `epic/` | `contentParsing` (Epic content/song metadata parsing), `leaderboardV1` (v1 leaderboard API client) |
| `persistence/file/` | `FileStore` types, `JsonSettingsPersistence`, `FileJsonFestivalPersistence` |
| `calendar/` | `CalendarModels` types |

### Dependencies

- `@festival/core` (portal link)
- `@festival/auth` (portal link)

> This package is excluded from the web app. It contains native-only code for MAUI and React Native.

---

## Dependency Graph

```
@festival/core          ← no @festival/* dependencies (leaf package)
@festival/theme         ← no @festival/* dependencies (leaf package)
@festival/ui-utils      ← no @festival/* dependencies (leaf package)
@festival/auth          ← no @festival/* dependencies (leaf package)
@festival/native        ← depends on @festival/core + @festival/auth

FortniteFestivalWeb     ← @festival/core, @festival/theme, @festival/ui-utils
FortniteFestivalRN      ← @festival/core (direct), @festival/native (via RN/packages)
```

- `core`, `theme`, `ui-utils`, and `auth` are all **leaf packages** with zero inter-package dependencies.
- `native` is the only package that depends on other `@festival/*` packages.
- The web app does NOT consume `@festival/auth` or `@festival/native`.

---

## Consumer Map

| Package | FortniteFestivalWeb | FortniteFestivalRN | @festival/native |
|---|---|---|---|
| `@festival/core` | **Heavy** (30+ import sites) — enums, types, server types, formatters, filtering, suggestions, combos | **Heavy** (20+ import sites) — types, filtering, formatters, suggestions | Dependency in package.json |
| `@festival/theme` | **Heavy** (30+ import sites) — Colors, spacing, animation timing, breakpoints, CSS enums, style factories | Not directly imported | — |
| `@festival/ui-utils` | **Moderate** (13 import sites) — stagger helpers, platform detection | Not directly imported | — |
| `@festival/auth` | Not imported | Not yet imported (planned) | Dependency in package.json |
| `@festival/native` | Not imported (excluded) | Consumed via RN packages | — |

### Most-Used Imports (Web)

**@festival/core**: `LoadPhase`, `InstrumentHeaderSize`, `TabKey`, `PlayerScoreSortMode`, `Keys`, `CardPhase`, `ListPhase`, `SyncPhase`, `BackfillStatus`, `ACCURACY_SCALE`, `accuracyColor`, `PercentileTier`

**@festival/theme**: `Colors`, `Size`, `Layout`, `Gap`, `Font`, `Weight`, `Radius`, `frostedCard`, `flexColumn`, `flexRow`, `Display`, `Align`, breakpoint queries, animation timing (`FADE_DURATION`, `STAGGER_INTERVAL`, `DEBOUNCE_MS`, `SPINNER_FADE_MS`)

**@festival/ui-utils**: `IS_PWA`, `IS_IOS`, `IS_ANDROID`, `staggerDelay`, `estimateVisibleCount`

---

## Build Configuration

### Package Resolution

All packages use **`portal:` links** in `package.json` dependencies — pointing to local source:

```json
"@festival/core": "portal:../packages/core"
"@festival/theme": "portal:../packages/theme"
"@festival/ui-utils": "portal:../packages/ui-utils"
```

### TypeScript Configuration

All packages share identical tsconfig patterns:

| Setting | Value |
|---|---|
| `target` | ES2020 |
| `module` | ESNext |
| `moduleResolution` | bundler |
| `strict` | true |
| `composite` | true (enables project references) |
| `declaration` + `declarationMap` | true |
| `outDir` | dist |
| `rootDir` | src |

Exception: `@festival/native` uses `noEmit: true` and `paths` for aliasing `@festival/core` and `@festival/auth`.

### Web App Resolution

FortniteFestivalWeb resolves packages in two layers:

1. **TypeScript** (`tsconfig.json` paths):
   ```json
   "@festival/core": ["../packages/core/src"],
   "@festival/theme": ["../packages/theme/src"],
   "@festival/ui-utils": ["../packages/ui-utils/src"]
   ```

2. **Vite** (`vite.config.ts` resolve.alias):
   ```js
   '@festival/core': path.resolve(__dirname, '../packages/core/src'),
   '@festival/theme': path.resolve(__dirname, '../packages/theme/src'),
   '@festival/ui-utils': path.resolve(__dirname, '../packages/ui-utils/src'),
   ```

Both resolve directly to **source** (no pre-build step needed). Vite compiles TS on-the-fly.

Vite also stubs RN-specific modules:
```js
'react-native': path.resolve(__dirname, 'src/stubs/react-native.ts'),
'react-native-app-auth': path.resolve(__dirname, 'src/stubs/react-native-app-auth.ts'),
```

### Version Injection

Vite defines build-time constants from package.json versions:
```js
__APP_VERSION__: pkg.version,        // FortniteFestivalWeb package.json
__CORE_VERSION__: corePkg.version,   // @festival/core package.json
__THEME_VERSION__: themePkg.version, // @festival/theme package.json
```

### Package Manager

Yarn 4.12.0 (Berry) — specified via `packageManager` field in core and theme package.json files.

---

## Patterns

### Framework-Agnostic Packages
- `core`, `theme`, `auth` contain **zero React imports** — pure TypeScript.
- `ui-utils` uses `window` and `navigator` (browser APIs) but no React.
- `native` imports `react-native` — excluded from web builds.
- `theme/factories.ts` uses a minimal `CSSProperties` type alias to avoid depending on `@types/react`.

### Barrel Exports
- `core` uses `export *` barrel pattern for all modules.
- `theme` uses explicit named exports (no `*`) for tree-shaking clarity.
- `ui-utils` uses explicit named exports.
- `auth` uses `export *` barrel pattern.

### Type-First Design
- Server API types in `core/api/serverTypes.ts` mirror C# DTOs from FSTService.
- Domain models are shared across web and native — changes must be backward-compatible.
- `InstrumentKey` (6-value union) vs `ServerInstrumentKey` (Solo_Guitar format) — both defined in core.

### Style Mixins (Theme)
- Frosted/gold styles are plain objects spread into inline styles: `style={{ ...frostedCard }}`.
- CSS enum objects eliminate string literals: `Display.flex` instead of `'flex'`.
- Helper functions build CSS shorthands: `border(2, '#CFA500')` → `'2px solid #CFA500'`.

### i18n Registry
- `@festival/core` provides `setTranslationFunction()` + `t()` — host app wires i18next.
- Translation keys in `i18n/en.json`, function references in `i18n/index.ts`.

### Concurrency
- `createLimiter(n)` — generic promise concurrency limiter used by both web and native.

### Seeded RNG
- `createSeededRng(seed)` — Mulberry32 PRNG for deterministic suggestion generation across platforms.

### MAUI Ports
- Several `app/` modules are direct ports from C# MAUI code: `formatters`, `logBuffer`, `progress`, `scoreRows`.
- Use `bankersRound()` for .NET parity (round-half-to-even).
