# Web Testing Infrastructure

## Vitest Setup

**Config location**: `FortniteFestivalWeb/vite.config.ts` → `test` block

| Setting | Value |
|---|---|
| Environment | `jsdom` |
| Globals | `true` (describe, it, expect available without import) |
| Setup file | `__test__/setup.ts` |
| Excludes | `e2e/**`, `node_modules/**` |
| Coverage provider | `v8` |
| Coverage reporters | `text`, `json`, `lcov` |

**Global setup (`__test__/setup.ts`)** provides:
- `@testing-library/jest-dom/vitest` matchers (toBeVisible, toHaveTextContent, etc.)
- `../src/i18n` initialization (i18next for translations in tests)
- `ResizeObserver` stub (for FirstRunCarousel, @tanstack/react-virtual)
- `IntersectionObserver` stub (for useScrollFade)
- `window.matchMedia` stub (for @festival/ui-utils/platform)

**NPM scripts**:
- `npm test` → `vitest run` (single run)
- `npm run test:watch` → `vitest` (watch mode)
- `npm run e2e` → `playwright test`
- `npm run e2e:headed` → `playwright test --headed`

## TestProviders

**File**: `__test__/helpers/TestProviders.tsx`

Wraps all 9 app contexts in the correct nesting order for component/page tests:

```
QueryClientProvider (test QueryClient: no retries, gcTime=0)
  └─ FeatureFlagsProvider
    └─ SettingsProvider
      └─ FestivalProvider
        └─ ShopProvider
          └─ FabSearchProvider
            └─ SearchQueryProvider
              └─ PlayerDataProvider (optional accountId prop)
                └─ FirstRunProvider
                  └─ ScrollContainerProvider
                    └─ ShellRefInjector (mock scroll container + portal target)
                      └─ MemoryRouter (initialEntries=[route])
                        └─ {children}
```

**Props**: `{ children, route?: string, accountId?: string }`

**`createTestQueryClient()`**: Exported separately; creates a QueryClient with `retry: false, gcTime: 0` for deterministic tests.

**`ShellRefInjector`**: Internal helper that:
- Renders a `data-testid="test-header-portal"` div for portaled content
- Creates a mock scroll container (`data-testid="test-scroll-container"`) with stubbed `scrollHeight: 5000`, `scrollTop: 0`, `clientHeight: 800`, and no-op `scrollTo`

## Unit Test Patterns

### API Mocking (vi.hoisted pattern)
Page and integration tests use `vi.hoisted()` to declare mock APIs before module loading:

```ts
const mockApi = vi.hoisted(() => ({
  getSongs: vi.fn().mockResolvedValue({ songs: [...], count: 3, currentSeason: 5 }),
  getVersion: vi.fn().mockResolvedValue({ version: '1.0.0' }),
  getPlayer: vi.fn().mockResolvedValue({ ... }),
  // ... all API methods
}));

vi.mock('../../../src/api/client', () => ({ api: mockApi }));
```

The mock MUST be declared in the test file (vi.mock hoists to file scope). Cannot be shared from helpers.

### Shared API Mock Factory
**File**: `__test__/helpers/apiMocks.ts`

`createApiMock(overrides?)` returns a full mock of the `api` object with all methods pre-configured with fixture data. Supports per-method overrides:
```ts
vi.mock('../../src/api/client', () => ({ api: createApiMock() }));
// Override per-test:
vi.mocked(api.getSongs).mockResolvedValueOnce({ songs: [], count: 0 });
```

### Rendering Pages
```ts
function renderSongsPage(route = '/songs', accountId?: string) {
  return render(
    <TestProviders route={route} accountId={accountId}>
      <Routes>
        <Route path="/songs" element={<SongsPage />} />
      </Routes>
    </TestProviders>,
  );
}
```

### Timer Pattern for Load Phases
Pages use Loading → SpinnerOut → ContentIn state machine with 500ms delay.

**File**: `__test__/helpers/timerHelpers.ts`

| Helper | Purpose |
|---|---|
| `advanceThroughLoadPhase()` | Advances 550ms (SPINNER_FADE_MS + 50) |
| `advanceThroughQuickFade()` | Advances 200ms (QUICK_FADE_MS + 50) |
| `flushPromisesAndTimers()` | `vi.runAllTimersAsync()` wrapped in `act()` |
| `advancePastDebounce()` | Advances 300ms for 250ms search debounce |

Usage:
```ts
beforeEach(() => { vi.useFakeTimers({ shouldAdvanceTime: true }); });
afterEach(() => { vi.useRealTimers(); });
```

### Browser Stubs
**File**: `__test__/helpers/browserStubs.ts`

| Stub | What it provides |
|---|---|
| `stubScrollTo()` | `Element.prototype.scrollTo` and `scrollIntoView` as vi.fn() |
| `stubElementDimensions(height=800)` | `clientHeight`, `scrollHeight`, `clientWidth`, `offsetHeight`, `getBoundingClientRect` |
| `stubResizeObserver(contentRect?)` | Mock `ResizeObserver` that fires callback synchronously on observe() |
| (in setup.ts) | `IntersectionObserver` no-op stub |

### Scroll Container Wrapper
**File**: `__test__/helpers/scrollContainerWrapper.tsx`

`createScrollContainerWrapper()` → returns `{ wrapper, mockEl }` for scroll hook tests (useScrollRestore, useHeaderCollapse, useScrollFade). The mock element has manipulable `scrollTop`, `scrollHeight`, `clientHeight` properties.

### Component Tests (no providers needed)
Simple components that don't need contexts use direct render:
```ts
const { container } = render(
  React.createElement(SearchBar, { value: '', onChange, placeholder: 'Search...' }),
);
```

### Hook Tests
```ts
const { result } = renderHook(() => useFilteredSongs({ songs, search: '', ... }));
expect(result.current).toHaveLength(3);
```

### Context Tests
```ts
function wrapper({ children }: { children: ReactNode }) {
  return <SettingsProvider>{children}</SettingsProvider>;
}
const { result } = renderHook(() => useSettings(), { wrapper });
```

## Test File Organization

**175 test files** across the following structure:

```
__test__/
├── setup.ts                    # Global setup (jest-dom, i18n, stubs)
├── barrels.test.ts             # Coverage-only: imports barrel re-exports
├── helpers/
│   ├── TestProviders.tsx       # Full provider stack wrapper
│   ├── apiMocks.ts             # Mock factory + fixture data
│   ├── browserStubs.ts         # jsdom stubs (scroll, resize, dimensions)
│   ├── scrollContainerWrapper.tsx  # Scroll context wrapper for hook tests
│   └── timerHelpers.ts         # Load phase + debounce timer helpers
├── api/
│   ├── client.test.ts          # API client tests
│   ├── pageCache.test.ts       # Page cache tests
│   └── queryKeys.test.ts       # Query key generation tests
├── app/
│   ├── App.test.tsx            # Full app rendering
│   ├── AppCoverage.test.tsx    # Coverage-only imports
│   ├── AppMobile.test.tsx      # Mobile viewport app tests
│   ├── AppShell.test.tsx       # Shell integration
│   └── routes.test.ts          # Route configuration
├── contexts/
│   ├── FabSearchContext.test.tsx
│   ├── FeatureFlagsContext.test.tsx
│   ├── FestivalContext.test.tsx
│   ├── FirstRunContext.test.tsx
│   ├── PlayerDataContext.test.tsx
│   ├── SearchQueryContext.test.tsx
│   ├── SettingsContext.test.tsx
│   └── ShopContext.test.tsx
├── hooks/
│   ├── chart/                  # useCardAnimation, useChartData, useChartDimensions, useChartPagination, useListAnimation
│   ├── data/                   # useAccountSearch, useAvailableSeasons, useDemoSongs, useFilteredSongs, useLoadPhase, useScoreFilter, useShopWebSocket, useSortedScoreHistory, useSuggestions, useSyncStatus, useTrackedPlayer, useVersions
│   ├── navigation/             # useNavigateToSongDetail
│   └── ui/                     # useFadeSpinner, useFirstRun, useIsMobile, useLeaderboardColumns, useMediaQuery, useModalDraft, useModalState, usePageTransition, useScrollFade, useScrollMask, useScrollRestore, useStagger, useStaggerRush, useStaggerStyle, useTabNavigation, useViewTransition, useVisualViewport
├── components/
│   ├── common/                 # Accordion, ActionPill, DirectionSelector, EmptyState, InstrumentSelector, LoadGate, LoadingSpinner, MarqueeText, Paginator, SearchBar, SectionHeader, SuspenseFallback, ToggleRow
│   ├── display/                # InstrumentChip, InstrumentHeader, InstrumentIcons, ScorePill
│   ├── firstRun/               # FirstRun component tests
│   ├── modals/                 # Modal component tests
│   ├── page/                   # BackgroundImage, ErrorBoundary, FadeIn, Page, PageHelpers, PageMessage, RouteErrorFallback
│   ├── player/                 # Player-specific components
│   ├── routing/                # Route component tests
│   ├── shell/                  # AnimatedBackground + desktop/, fab/, mobile/ subfolders
│   ├── songs/                  # Song components + headers/, metadata/
│   └── sort/                   # Sort component tests
├── pages/
│   ├── leaderboard/            # global/ (components/), player/ (components/, firstRun/, modals/)
│   ├── leaderboards/           # helpers/
│   ├── player/                 # components/, firstRun/, helpers/, sections/
│   ├── rivals/                 # components/
│   ├── settings/
│   ├── shop/
│   ├── songinfo/               # components/ (chart/, path/), firstRun/
│   ├── songs/                  # components/, firstRun/, modals/ (components/)
│   └── suggestions/            # components/, firstRun/, modals/
├── firstRun/
│   └── types.test.ts           # FirstRun type tests
└── utils/
    ├── buildRivalDataIndexFromRivalsAll.test.ts
    ├── coreFormatters.test.ts
    ├── coreHelpers.test.ts
    ├── formatPercentile.test.ts
    ├── leaderboardSettings.test.ts
    ├── platform.test.ts
    ├── songSettings.test.ts
    ├── suggestionAdapter.test.ts
    └── suggestionsFilter.test.ts
```

**Naming conventions**:
- Unit tests: `{Component|hook|util}.test.{ts|tsx}`
- Test directory mirrors source structure (`pages/songs/` → `__test__/pages/songs/`)
- Helpers in `__test__/helpers/` — never in source tree

## Playwright Setup

**Config file**: `FortniteFestivalWeb/playwright.config.ts`

| Setting | Value |
|---|---|
| Test directory | `./e2e` |
| Timeout | 30000ms |
| Base URL | `http://localhost:3000` |
| Headless | `true` |

**4 viewport projects**:

| Project | Width×Height | Touch |
|---|---|---|
| `desktop` | 1280×800 | No |
| `desktop-narrow` | 800×800 | No |
| `mobile` | 375×812 | Yes |
| `mobile-narrow` | 320×568 | Yes |

**Web server**: `npx vite --mode e2e --port 3000` with reuse, 30s startup timeout.

**15 E2E spec files**:
```
e2e/
├── fixtures/
│   ├── fre.ts          # FreCarousel page object + FreState localStorage helper + extended fixtures
│   └── navigation.ts   # goto(), gotoFresh(), getFirstSongId() helpers
├── scroll.spec.ts      # Scroll behavior tests (desktop only)
└── fre/                # FRE (First Run Experience) specs
    ├── carousel-interaction.fre.spec.ts
    ├── compete.fre.spec.ts
    ├── cross-page-flow.fre.spec.ts
    ├── feature-flags.fre.spec.ts
    ├── layout.fre.spec.ts
    ├── leaderboards.fre.spec.ts
    ├── player-history.fre.spec.ts
    ├── rivals.fre.spec.ts
    ├── settings-direct.fre.spec.ts
    ├── shop.fre.spec.ts
    ├── song-detail.fre.spec.ts
    ├── songs.fre.spec.ts
    ├── statistics.fre.spec.ts
    └── suggestions.fre.spec.ts
```

## E2E Patterns

### Fixtures (Playwright test.extend)
**`e2e/fixtures/fre.ts`** extends Playwright's `test` with two custom fixtures:

```ts
type FreFixtures = {
  fre: FreCarousel;    // Page object for carousel interaction
  freState: FreState;  // localStorage state management
};

export const test = base.extend<FreFixtures>({ ... });
```

### Page Objects
**`FreCarousel`**: Locates FRE elements via `data-testid` attributes:
- `fre-overlay`, `fre-card`, `fre-close`, `fre-next`, `fre-prev`, `fre-dots`, `fre-title`, `fre-description`, `fre-slide-area`
- Methods: `waitForVisible()`, `dismiss()`, `slideCount()`, `assertSlideCount(n)`, `collectAllTitles()`, `goToSlide(index)`

**`FreState`**: Manages app state via `localStorage` for E2E:
- `resetAppState()` → navigates to `/`, clears all `fst:*` keys
- `setTrackedPlayer(accountId?, displayName?)` → sets `fst:trackedPlayer`
- `setSettings(partial)` → merges into `fst:appSettings`
- `setFeatureFlags(overrides)` → sets `fst:featureFlagOverrides`
- `markSlidesSeen(slideIds)` → pre-marks slides as seen
- `getSeenSlides()` → reads current seen state

### Navigation Helpers
**`e2e/fixtures/navigation.ts`**:
- `goto(page, route)` → `page.goto('/#' + route)`, waits 2s for React mount
- `gotoFresh(page, route)` → goto + reload (forces localStorage re-read)
- `getFirstSongId(page)` → intercepts `/api/songs` to get a real song ID

### Test Structure Pattern
```ts
import { test, expect } from '../fixtures/fre';
import { goto } from '../fixtures/navigation';

test.describe('Songs FRE', () => {
  test.beforeEach(async ({ freState }) => {
    await freState.resetAppState();
  });

  test('shows N slides in state X', async ({ page, fre, freState }) => {
    await freState.setTrackedPlayer();
    await goto(page, '/songs');
    await fre.waitForVisible();
    await fre.assertSlideCount(8);
  });
});
```

### Live API
E2E tests run against the real running server (via `webServer` config in playwright.config.ts). No API mocking — tests must tolerate real data.

### Viewport-specific skipping
```ts
test.beforeEach(async ({ page }, testInfo) => {
  if (testInfo.project.name !== 'desktop') test.skip();
});
```

## Coverage

**Thresholds** (enforced per-file):

| Metric | Threshold |
|---|---|
| Lines | 95% |
| Branches | 95% |
| Statements | 95% |
| Functions | 95% |

**`perFile: true`** — every file must individually meet all thresholds.

**Included**: `src/**/*.{ts,tsx}`

**Excluded from coverage**:
- `__test__/**` (test files themselves)
- `src/vite-env.d.ts` (type declarations)
- `src/main.tsx` (entry point)
- `src/stubs/**` (React Native stubs)
- `src/utils/platform.ts` (environment detection)
- `src/components/sort/reorderTypes.ts` (type-only file)
- `src/pages/player/helpers/playerPageTypes.ts` (type-only file)

**Coverage-only test**: `barrels.test.ts` exists solely to import barrel re-exports and theme files to ensure they register as covered.

## Mocking Patterns

### API Client Mock (most common)
```ts
// 1. Hoisted mock (page/integration tests)
const mockApi = vi.hoisted(() => ({ ... }));
vi.mock('../../../src/api/client', () => ({ api: mockApi }));

// 2. Shared factory (simpler tests)
vi.mock('../../src/api/client', () => ({ api: createApiMock() }));

// 3. Per-test override
vi.mocked(api.getSongs).mockResolvedValueOnce({ songs: [], count: 0 });
```

### Browser API Mocks
- `window.matchMedia` → stubbed in setup.ts, overridable per-test with `vi.fn()`
- `localStorage` → real jsdom; cleared with `localStorage.clear()` in beforeEach
- `ResizeObserver` → stubbed in setup.ts + `stubResizeObserver()` for tests needing custom sizes
- `IntersectionObserver` → stubbed in setup.ts as no-op
- `Element.prototype.scrollTo/scrollIntoView` → `stubScrollTo()`
- `getBoundingClientRect` → `stubElementDimensions(height)`

### Module Mocking
CSS modules auto-resolve in jsdom (return empty objects). No explicit CSS mock needed.

### Timer Mocking
```ts
vi.useFakeTimers({ shouldAdvanceTime: true });
// shouldAdvanceTime: true allows Promises to resolve while timers are faked
```

### Fixture Data
All mock data lives in `__test__/helpers/apiMocks.ts`:
- `MOCK_SONGS` (3 songs with varying attributes)
- `MOCK_LEADERBOARD_ENTRIES` (5 entries)
- `MOCK_PLAYER_SCORES` (3 scores)
- `MOCK_PLAYER` (full player response)
- `MOCK_HISTORY_ENTRIES` (3 history entries)
- `MOCK_SONGS_RESPONSE`, `MOCK_LEADERBOARD_RESPONSE`, `MOCK_ALL_LEADERBOARDS_RESPONSE`, `MOCK_PLAYER_HISTORY_RESPONSE`, `MOCK_PLAYER_STATS_RESPONSE`, `MOCK_SYNC_STATUS`, `MOCK_TRACK_PLAYER_RESPONSE`, `MOCK_ACCOUNT_SEARCH_RESPONSE`

### E2E State Management (not mocking — real state)
E2E tests use `page.evaluate()` to set `localStorage` keys directly:
```ts
await page.evaluate(({ id, name }) =>
  localStorage.setItem('fst:trackedPlayer', JSON.stringify({ accountId: id, displayName: name })),
  { id: accountId, name: displayName },
);
```
