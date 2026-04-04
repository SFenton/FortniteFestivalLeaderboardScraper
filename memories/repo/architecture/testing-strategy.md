# Testing Strategy — Cross-Repo Overview

> Living document maintained by the Testing V-Team. Last updated: 2026-04-03.

## Test Infrastructure Overview

| Aspect | FSTService | FortniteFestivalWeb |
|--------|-----------|-------------------|
| **Unit Framework** | xUnit 2.9.2 | Vitest 4.0.18 |
| **Mocking** | NSubstitute 5.3.0 | `vi.fn()` / `vi.mock()` / `vi.hoisted()` |
| **Component Testing** | N/A | @testing-library/react 16.3.2 |
| **Integration** | WebApplicationFactory + Testcontainers PostgreSQL 4.3.0 | N/A |
| **E2E** | N/A | Playwright 1.58.2 (4-viewport matrix) |
| **Coverage Tool** | coverlet.collector 6.0.2 (Cobertura XML) | @vitest/coverage-v8 (v8 provider) |
| **Coverage Threshold** | 94% line coverage (CI-enforced) | 95% per-file (lines/branches/statements/functions) |
| **Runtime** | .NET 9.0 | jsdom 28.1.0 |
| **Test Count** | 54 unit + 2 integration files | 181 unit/component + 17 E2E files |

## FSTService Tests

### Directory Structure
```
FSTService.Tests/
├── Helpers/
│   ├── InMemoryMetaDatabase.cs      — PostgreSQL-backed MetaDatabase fixture
│   ├── TempInstrumentDatabase.cs    — PostgreSQL-backed InstrumentDatabase fixture
│   ├── SharedPostgresContainer.cs   — Singleton Testcontainers container
│   └── MockHttpMessageHandler.cs    — Queue-based HTTP response mock
├── Unit/                            — 54 test classes
│   ├── ScraperWorkerTestBase.cs     — Abstract base class (3 subclasses)
│   └── ...                          — One test file per source file
└── Integration/
    ├── ApiEndpointIntegrationTests.cs
    └── PersistencePipelineIntegrationTests.cs
```

### xUnit Patterns
- **Sealed test classes** implementing `IDisposable` for cleanup
- **Field-initialized fixtures:** `private readonly InMemoryMetaDatabase _fixture = new();`
- **Property shorthand:** `private MetaDatabase Db => _fixture.Db;`
- **`[Fact]`** for parameterless tests (no `[Theory]` usage observed)
- **Assert methods:** `Assert.True/False/Equal/NotEqual/Null/NotNull/Single/Contains/DoesNotContain/Empty`
- **Floating-point:** `Assert.Equal(expected, actual, precision)` for decimal comparisons
- **Async tests:** `[Fact] public async Task` with `CancellationTokenSource` for timeout safety
- **xUnit parallelism:** Default parallel execution across classes (not within a class)

### NSubstitute Mocking
- **Interface mocking:** `Substitute.For<ILogger<T>>()`, `Substitute.For<ICredentialStore>()`
- **Return configuration:** `store.LoadAsync(Arg.Any<CancellationToken>()).Returns(value)`
- **Arg matchers:** `Arg.Any<T>()`, `Arg.Is<T>(predicate)`
- **Logging suppression:** All loggers mocked with NSubstitute (no log output in tests)

### Test Helpers

#### SharedPostgresContainer (singleton per test run)
- Uses `postgres:17-alpine` via Testcontainers
- `max_connections=500` to support parallel test execution
- `Lazy<PostgreSqlContainer>` — started on first access, reused across all tests
- `CreateDatabase()` creates a fresh GUID-named database per test, initializes schema via `DatabaseInitializer.EnsureSchemaAsync()`
- Connection pool: `MinPoolSize=0, MaxPoolSize=10, ConnectionIdleLifetime=10`

#### InMemoryMetaDatabase / TempInstrumentDatabase
- Sealed, `IDisposable`, wraps a real `MetaDatabase`/`InstrumentDatabase` backed by Testcontainers PostgreSQL
- Exposes `Db` (the database) and `DataSource` (raw NpgsqlDataSource)
- Used as field-initialized fixture in test class constructors

#### MockHttpMessageHandler
- Queue-based: `EnqueueJsonOk(json)`, `EnqueueError(status)`, `Enqueue429(retryAfter)`, `EnqueueHtml403()`, `EnqueueException(ex)`
- Request capture: `Requests` list records all outgoing `HttpRequestMessage`
- Fail-fast: throws `InvalidOperationException` if no responses queued

#### ScraperWorkerTestBase (abstract)
- Base class for `ScraperWorkerTests`, `ScraperWorkerStatefulTests`, `ScraperWorkerModeTests`
- Constructor initializes 12+ dependencies (mix of real and mocked)
- `CreateWorker(ScraperOptions?)` — factory for SUT with optional config
- `InvokePrivateAsync(worker, methodName, args)` — reflection for private method testing
- `CreateServiceWithSongs(params (string, string, string)[])` — reflection-based song injection
- Temp directory per instance, cleaned up in `Dispose()`

### Factory Method Pattern
Most test classes define a `Create*()` factory that returns the SUT (and sometimes a tuple with dependencies):
```csharp
private (AccountNameResolver resolver, MockHttpMessageHandler handler, InMemoryMetaDatabase metaDb) 
    CreateResolver(string? accessToken = "test_token") { ... }
```

### Reflection Usage
- Private field mutation via `BindingFlags.NonPublic | BindingFlags.Instance` to inject test data into `FestivalService._songs`
- Private method invocation for testing internal behavior (`ScraperWorkerTestBase.InvokePrivateAsync`)

## FSTService Integration Tests

### WebApplicationFactory Pattern (`ApiEndpointIntegrationTests`)
- **`IClassFixture<FstWebApplicationFactory>`** — shared factory across all tests in the class
- **`ConfigureWebHost()`**:
  - Uses `"Testing"` environment
  - In-memory config overrides: data directory, connection strings, API keys, JWT secrets
  - `RemoveAll<T>()` strips real services (NpgsqlDataSource, IHostedService, TokenManager)
  - Replaces with: test PostgreSQL data source, `TestDatabaseInitializer`, mocked `TokenManager`
  - All HttpClients replaced with no-op handlers (prevents external API calls)
- **Two clients:** unauthenticated `_client` and authenticated `_authedClient` (with API key header)
- **Tests endpoint behavior:** status codes, JSON response structure, auth requirements

### Persistence Pipeline Integration Tests
- `PersistencePipelineIntegrationTests` tests the full write pipeline:
  - `StartWriters()` → `EnqueueResultAsync()` → `DrainWritersAsync()` → assert aggregates
  - Tests score change detection, aggregate computation, data persistence
- Uses real `GlobalLeaderboardPersistence` with Testcontainers PostgreSQL

## FortniteFestivalWeb Unit Tests

### Directory Structure
```
FortniteFestivalWeb/__test__/
├── helpers/
│   ├── TestProviders.tsx       — Full provider stack for rendering
│   ├── apiMocks.ts             — Mock API data (songs, leaderboards, players)
│   ├── browserStubs.ts         — ResizeObserver, IntersectionObserver, matchMedia stubs
│   └── ...
├── setup.ts                    — Global jsdom stubs
├── barrels.test.ts             — Barrel export validation
├── api/                        — API client tests
├── app/                        — App shell/routing (5 files)
├── components/                 — UI components (shell/, fab/, songs/, common/, display/)
├── contexts/                   — React context tests
├── firstRun/                   — First-run logic tests
├── hooks/
│   ├── ui/                     — 13 files (useIsMobile, useStagger, useScrollFade, etc.)
│   ├── data/                   — useSyncStatus, useScoreFilter, etc.
│   └── chart/                  — useChartData
├── pages/                      — Full page tests
│   ├── suggestions/
│   ├── leaderboard/player/
│   ├── rivals/
│   ├── shop/
│   ├── compete/                — (gap: currently empty, see Known Gaps)
│   ├── settings/
│   ├── songs/
│   └── statistics/
└── utils/                      — 9 utility test files
```

### Setup File (`setup.ts`)
Global stubs configured before all tests:
- `ResizeObserver` — no-op (used by virtual lists, carousels)
- `IntersectionObserver` — no-op (used by scroll effects, infinite scroll)
- `window.matchMedia` — returns defaults (needed by `@festival/ui-utils/platform`)

### TestProviders Component
Full provider stack wrapping components under test:
```
QueryClientProvider → FeatureFlagsProvider → SettingsProvider → FestivalProvider →
ShopProvider → FabSearchProvider → SearchQueryProvider → PlayerDataProvider →
FirstRunProvider → ScrollContainerProvider → ShellRefInjector → MemoryRouter
```
- Accepts `route` (initial route) and `accountId` (tracked player)
- `createTestQueryClient()` — retry: false, gcTime: 0 (no retries, instant cleanup)
- `ShellRefInjector` — provides mock scroll container + header portal target

### Browser Stubs (`browserStubs.ts`)
Import in `beforeAll`:
- `stubScrollTo()` — mocks Element.prototype.scrollTo, scrollIntoView
- `stubElementDimensions(height?)` — mocks clientHeight, scrollHeight, getBoundingClientRect
- `stubResizeObserver(contentRect?)` — full mock with fire-on-observe
- `stubIntersectionObserver()` — minimal mock
- `stubMatchMedia(matches?)` — configurable matchMedia

### API Mocking Pattern
```typescript
const mockApi = vi.hoisted(() => ({
  getSongs: vi.fn().mockResolvedValue({ songs: [...], count: 8, currentSeason: 5 }),
  getPlayer: vi.fn().mockResolvedValue({ accountId: '...', displayName: '...' }),
  // ... all endpoints
}));
vi.mock('../../../src/api/client', () => ({ api: mockApi }));

beforeEach(() => {
  vi.clearAllMocks();
  localStorage.clear();
});
```
- `vi.hoisted()` ensures mocks are hoisted above imports
- `vi.mock()` replaces the real API client module
- Per-test overrides: `mockApi.getSongs.mockResolvedValueOnce(...)` 
- `localStorage` managed for tracked player, settings, feature flags

### Component Rendering Pattern
```typescript
function renderPage(route = '/songs', accountId = 'test-player') {
  return render(
    <TestProviders route={route} accountId={accountId}>
      <Routes>
        <Route path="/songs" element={<SongsPage />} />
      </Routes>
    </TestProviders>,
  );
}
```

## FortniteFestivalWeb E2E Tests

### Playwright Configuration
- **4 viewport projects:**
  - `desktop` — 1280×800
  - `desktop-narrow` — 800×800
  - `mobile` — 375×812, hasTouch: true
  - `mobile-narrow` — 320×568, hasTouch: true
- **baseURL:** `http://localhost:3000`
- **testDir:** `./e2e`
- **timeout:** 30s per test
- **webServer:** Auto-starts Vite in `e2e` mode on port 3000

### Directory Structure
```
FortniteFestivalWeb/e2e/
├── fixtures/
│   ├── fre.ts                     — FreCarousel + FreState page objects
│   └── navigation.ts              — goto(), gotoFresh(), getFirstSongId()
├── fre/                           — 14 spec files
│   ├── suggestions.fre.spec.ts
│   ├── songs.fre.spec.ts
│   ├── leaderboards.fre.spec.ts
│   ├── rivals.fre.spec.ts
│   ├── statistics.fre.spec.ts
│   ├── shop.fre.spec.ts
│   ├── player-history.fre.spec.ts
│   ├── song-detail.fre.spec.ts
│   ├── feature-flags.fre.spec.ts
│   ├── layout.fre.spec.ts
│   ├── cross-page-flow.fre.spec.ts
│   ├── carousel-interaction.fre.spec.ts
│   ├── compete.fre.spec.ts
│   └── settings-direct.fre.spec.ts
└── scroll.spec.ts                 — Desktop-only scroll behavior
```

### Page Object Pattern
**FreCarousel** — locators for overlay, card, close/next/prev buttons, dots, title, description. Methods: `waitForVisible()`, `dismiss()`, `slideCount()`, `collectAllTitles()`, `goToSlide(i)`.

**FreState** — localStorage management: `resetAppState()`, `clearFirstRunState()`, `setTrackedPlayer()`, `setSettings(key, value)`.

### Navigation Helpers
- `goto(page, route)` — navigates to `/#${route}`, waits for React hydration
- `gotoFresh(page, route)` — goto + reload for fresh localStorage
- `getFirstSongId(page)` — intercepts `/api/songs` response, extracts first song ID

### Viewport-Conditional Tests
```typescript
test.beforeEach(async ({ page }, testInfo) => {
  if (testInfo.project.name !== 'desktop') test.skip();
});
```

## Coverage Strategy

| Component | Tool | Threshold | Scope | Enforcement |
|-----------|------|-----------|-------|-------------|
| **FSTService** | coverlet.collector 6.0.2 | **94% line coverage** | `[FSTService]*` assembly only | CI: `publish-image.yml` job parses Cobertura XML, fails build |
| **FortniteFestivalWeb** | @vitest/coverage-v8 | **95% per-file** (lines, branches, statements, functions) | `src/**/*.{ts,tsx}` | Vitest config thresholds (local); **CI job currently disabled** |

### Coverage Exclusions
**FSTService:** Test/Core/Scraping projects excluded (only `[FSTService]*` assembly measured)

**FortniteFestivalWeb:**
- `__test__/**` — test files
- `src/vite-env.d.ts` — ambient declarations
- `src/main.tsx` — entry point
- `src/stubs/**` — React Native mock modules
- `src/utils/platform.ts` — platform detection
- `src/components/sort/reorderTypes.ts` — type-only file
- `src/pages/player/helpers/playerPageTypes.ts` — type-only file

### Coverage Reporting
- **FSTService:** Cobertura XML → CI bash script → pass/fail
- **FortniteFestivalWeb:** text (console), json, lcov (IDE gutters)

## Test Naming Conventions

### FSTService
- **File:** `{SourceClass}Tests.cs` (mirrors source file name)
- **Method:** `{Method}_{Condition}_{Expected}` 
  - Examples: `StartScrapeRun_returns_positive_id`, `InsertAccountIds_ignores_duplicates`, `CalculateAsync_AlreadyCalculated_Skips`
- **Sections:** Unicode box-drawing headers (`═══ ScrapeLog ═══`)

### FortniteFestivalWeb (Vitest)
- **File:** `{Feature}.test.ts` or `{Feature}.test.tsx` (mirrors src/ structure)
- **Suite:** `describe('{ComponentName}', () => { ... })`
- **Test:** `it('renders without crashing', ...)`, `it('shows error when no player data', ...)`
- **Style:** Sentence-case descriptions starting with verb

### FortniteFestivalWeb (Playwright)
- **File:** `{feature}.fre.spec.ts` (FRE = Festival Run E2E) or `{feature}.spec.ts`
- **Suite:** `test.describe('{Feature} FRE', () => { ... })`
- **Test:** `test('fresh, with player — shows all 4 slides', ...)`

## Mocking Patterns

### FSTService — External Dependencies
| Dependency | Mock Strategy |
|-----------|--------------|
| HTTP APIs (Epic, leaderboards) | `MockHttpMessageHandler` — queue-based response enqueuing |
| Databases | Real PostgreSQL via Testcontainers (not mocked) |
| Logging | `Substitute.For<ILogger<T>>()` (suppressed) |
| Auth (TokenManager) | NSubstitute mock returning fixed access tokens |
| File I/O (credential store) | NSubstitute mock with `Returns()` |
| Configuration | `Options.Create(new ScraperOptions { ... })` |
| Background services | `RemoveAll<IHostedService>()` in integration tests |

### FortniteFestivalWeb — External Dependencies
| Dependency | Mock Strategy |
|-----------|--------------|
| API calls | `vi.hoisted()` + `vi.mock('src/api/client')` — all endpoints mocked |
| Browser APIs | Global stubs in setup.ts (ResizeObserver, IntersectionObserver, matchMedia) |
| localStorage | Direct manipulation in beforeEach/tests |
| Routing | `MemoryRouter` with `initialEntries` via TestProviders |
| Feature flags | localStorage `fst:featureFlagOverrides` |
| User events | `@testing-library/user-event` (preferred over `fireEvent`) |

## Test Data

### Test Accounts (from AGENTS.md)
| Username | Account ID | Usage |
|----------|-----------|-------|
| SFentonX | `195e93ef108143b2975ee46662d4d0e1` | Primary test account |
| captainparticles | `cb8ebb19b32c40d1a736d7f8efec17ac` | Secondary test account |
| kahnyri | `4c2a1300df4c49a9b9d2b352d704bdf0` | Tertiary test account |

### FSTService Test Fixtures
- **Song data:** Created via reflection into `FestivalService._songs` dictionary
- **Leaderboard entries:** `MakeEntry()` / `MakeResult()` factory methods with tuple params and defaults
- **Database seeding:** `SeedEntries(db, songId, params (AccountId, Score)[])` helpers
- **HTTP responses:** Inline JSON strings in test methods

### FortniteFestivalWeb Test Fixtures
- **Mock songs:** `MOCK_SONGS` in `apiMocks.ts` (3 test songs with difficulty, maxScores, genres)
- **Mock leaderboard:** `MOCK_LEADERBOARD_ENTRIES` (4 entries with rank, percentile, isFullCombo)
- **localStorage:** `fst:trackedPlayer`, `fst:firstRun`, `fst:featureFlagOverrides`
- **API responses:** Inline mock objects in `vi.hoisted()` blocks

### Integration Test Config (WebApplicationFactory)
- API key: `test-api-key-12345`
- JWT secret: `TestSecretKey_SuperLongEnough_For_HMACSHA256_12345678`
- Environment: `"Testing"`
- Data directory: `Path.GetTempPath()` with GUID suffix

## Known Gaps

### FSTService
1. **API endpoint coverage:** Some endpoint files (AdminEndpoints, DiagEndpoints, FeatureEndpoints) may lack dedicated unit tests — covered indirectly via integration tests
2. **Auth flow:** `EpicAuthService` and full OAuth flow are unit-tested, but no integration test exercises the actual token refresh → API call chain
3. **No Theory/parameterized tests:** All tests use `[Fact]` — data-driven scenarios could benefit from `[Theory]` + `[InlineData]`

### FortniteFestivalWeb
1. **`compete/` page:** No unit tests exist for the compete page (directory empty in `__test__/pages/`)
2. **CI gap:** Web test job in GitHub Actions is **currently commented out** — coverage enforcement only works locally
3. **E2E scope:** Playwright tests focus heavily on first-run experience (FRE). Page-specific E2E tests for logged-in flows (rivals detail, player history, song detail) are minimal
4. **No visual regression:** No screenshot comparison or visual regression testing configured
5. **No API contract tests:** No automated verification that web API mocks match actual FSTService response shapes

### Cross-Repo
1. **No contract testing:** API shape changes in FSTService have no automated cross-check against `FortniteFestivalWeb/src/api/client.ts` types
2. **No shared test fixtures:** Test accounts are defined in AGENTS.md but not programmatically shared between repos
3. **Web CI disabled:** FSTService enforces 94% coverage in CI, but FortniteFestivalWeb's 95% threshold only enforces locally
