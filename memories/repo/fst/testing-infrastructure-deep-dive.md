# FSTService Testing Infrastructure Deep Dive

> Last updated: 2026-04-03

## Test Framework

### Dependencies (FSTService.Tests.csproj)
| Package | Version | Purpose |
|---|---|---|
| xunit | 2.9.2 | Test framework |
| xunit.runner.visualstudio | 2.8.2 | VS/VS Code test discovery |
| NSubstitute | 5.3.0 | Mocking framework |
| Microsoft.AspNetCore.Mvc.Testing | 9.0.0 | WebApplicationFactory for integration tests |
| Testcontainers.PostgreSql | 4.3.0 | Real PostgreSQL in Docker for tests |
| Npgsql | 9.0.3 | PostgreSQL .NET driver |
| coverlet.collector | 6.0.2 | Code coverage collection |
| Microsoft.NET.Test.Sdk | 17.12.0 | Test SDK |

### Global Usings
- `Xunit` — implicit via csproj
- `FortniteFestival.Core.Scraping` — implicit via csproj

### Target Framework
- `net9.0`, nullable enabled, implicit usings enabled

---

## Test Helpers

All helpers live in `FSTService.Tests/Helpers/`. Four files total:

### 1. SharedPostgresContainer (`SharedPostgresContainer.cs`)
**Purpose**: Shared PostgreSQL Testcontainer — one container per test run, lazily started.

- **Image**: `postgres:17-alpine`
- **Credentials**: user=`test`, password=`test`, database=`fst_tests`
- **Config**: `max_connections=500` (handles parallel test classes)
- **Singleton**: `Lazy<PostgreSqlContainer>`, started synchronously on first access
- **Key Method**: `CreateDatabase()` — creates a fresh database with a unique GUID name, initializes schema via `DatabaseInitializer.EnsureSchemaAsync()`, returns an `NpgsqlDataSource` with `MaxPoolSize=10`
- **Usage Pattern**: Every test class that needs a real database calls `SharedPostgresContainer.CreateDatabase()` to get an isolated database. The container is shared but each test gets its own database.

### 2. InMemoryMetaDatabase (`InMemoryMetaDatabase.cs`)
**Purpose**: Creates a `MetaDatabase` backed by a fresh PostgreSQL database. Drop-in replacement for the old SQLite fixture.

- Implements `IDisposable`
- Creates its own `NpgsqlDataSource` via `SharedPostgresContainer.CreateDatabase()`
- Exposes `Db` (MetaDatabase) and `DataSource` (NpgsqlDataSource)
- Logger is a NSubstitute mock (`Substitute.For<ILogger<MetaDatabase>>()`)
- Used by: `MetaDatabaseTests`, `GlobalLeaderboardPersistenceTests`, `RivalsCalculatorTests`, `PostScrapeOrchestratorTests`, `SongProcessingMachineTests`, `ScraperWorkerTestBase`, etc.

### 3. TempInstrumentDatabase (`TempInstrumentDatabase.cs`)
**Purpose**: Creates an `InstrumentDatabase` backed by a fresh PostgreSQL database.

- Constructor takes optional `instrument` parameter (default: `"Solo_Guitar"`)
- Same pattern as InMemoryMetaDatabase — creates own DataSource, exposes `Db` and `DataSource`
- Used by: `InstrumentDatabaseTests`, `InstrumentDatabaseRankingsTests`

### 4. MockHttpMessageHandler (`MockHttpMessageHandler.cs`)
**Purpose**: Queue-based HTTP mock that returns preconfigured responses and captures all requests.

- **Response queue**: `Queue<object>` — holds `HttpResponseMessage` or `Exception`
- **Request capture**: `List<HttpRequestMessage>` exposed as `Requests`
- **Enqueue methods**:
  - `EnqueueResponse(HttpResponseMessage)` — raw response
  - `EnqueueJsonResponse(HttpStatusCode, string)` — JSON content type
  - `EnqueueJsonOk(string)` — shorthand for 200 + JSON
  - `EnqueueError(HttpStatusCode, string)` — error with body
  - `EnqueueException(Exception)` — throws on next request
  - `Enqueue429(TimeSpan)` — rate limit with Retry-After header
  - `EnqueueHtml403()` — CDN-style HTML 403 response
- **Behavior**: Throws `InvalidOperationException` if no responses are queued
- Used by: `ResilientHttpExecutorTests`, `TokenManagerTests`, `GlobalLeaderboardScraperTests`, etc.

---

## Unit Test Patterns

### File Organization
- **56 unit test files** in `FSTService.Tests/Unit/`
- Naming convention: `{ClassName}Tests.cs` mirrors `{ClassName}.cs`
- Some classes split into multiple files: `ScraperWorkerTests.cs`, `ScraperWorkerStatefulTests.cs`, `ScraperWorkerModeTests.cs` all share `ScraperWorkerTestBase.cs`

### Common Patterns

#### Pattern 1: IDisposable with InMemoryMetaDatabase
```csharp
public sealed class MetaDatabaseTests : IDisposable
{
    private readonly InMemoryMetaDatabase _fixture = new();
    private MetaDatabase Db => _fixture.Db;
    public void Dispose() => _fixture.Dispose();

    [Fact]
    public void StartScrapeRun_returns_positive_id()
    {
        var id = Db.StartScrapeRun();
        Assert.True(id > 0);
    }
}
```

#### Pattern 2: NSubstitute for Dependencies
```csharp
// Interface mocking
var store = Substitute.For<ICredentialStore>();
store.LoadAsync(Arg.Any<CancellationToken>())
    .Returns(new StoredCredentials { AccountId = "acct1", RefreshToken = "rt_old" });

// Class mocking with constructor args (virtual methods only)
var tokenManager = Substitute.For<TokenManager>(
    epicAuth, Substitute.For<ICredentialStore>(), Substitute.For<ILogger<TokenManager>>());
tokenManager.GetAccessTokenAsync(Arg.Any<CancellationToken>())
    .Returns("mock_access_token");
```

#### Pattern 3: Sealed classes require real instances with mock HTTP
```csharp
// EpicAuthService is sealed — can't substitute directly
var handler = new MockHttpMessageHandler();
handler.EnqueueJsonOk(MakeTokenJson(...));
var http = new HttpClient(handler);
var auth = new EpicAuthService(http, Substitute.For<ILogger<EpicAuthService>>());
```

#### Pattern 4: Reflection for Private Methods
```csharp
private static IReadOnlyList<string> InvokeGetEnabledInstruments(ScraperOptions opts)
{
    var method = typeof(ScraperWorker).GetMethod(
        "GetEnabledInstruments", BindingFlags.NonPublic | BindingFlags.Static)!;
    return (IReadOnlyList<string>)method.Invoke(null, [opts])!;
}
```

#### Pattern 5: Temp Directory Lifecycle
```csharp
private readonly string _tempDir;

public MyTests()
{
    _tempDir = Path.Combine(Path.GetTempPath(), $"fst_test_{Guid.NewGuid():N}");
    Directory.CreateDirectory(_tempDir);
}

public void Dispose()
{
    try { Directory.Delete(_tempDir, true); } catch { }
}
```

#### Pattern 6: Pure Logic Tests (No Mocks)
```csharp
// AdaptiveConcurrencyLimiterTests, ComboIdsTests — test pure logic
[Fact]
public void Constructor_SetsInitialDop()
{
    _limiter = new AdaptiveConcurrencyLimiter(16, 4, 64, _log);
    Assert.Equal(16, _limiter.CurrentDop);
}
```

#### Pattern 7: ScraperWorkerTestBase (Shared Test Infrastructure)
- Abstract base class for ScraperWorker tests
- Creates real `MetaDatabase`, `GlobalLeaderboardPersistence` (backed by Testcontainers)
- Substitutes external dependencies: `TokenManager`, `GlobalLeaderboardScraper`, `AccountNameResolver`, `ScoreBackfiller`, etc.
- Provides `CreateWorker()` and `CreateWorkerWithHttp()` factory methods
- `InvokePrivateAsync()` helper for testing private instance methods
- `CreateServiceWithSongs()` — sets up FestivalService with test songs via reflection
- `NoOpHttpHandler` inner class — returns 200 OK for all requests
- Split into subclasses for **cross-class parallelism** (xUnit runs test classes in parallel by default)

### Middleware Tests
- `PathTraversalGuardMiddlewareTests` — uses `DefaultHttpContext` directly, `[Theory]` with `[InlineData]`
- `ApiKeyAuthHandlerTests` — creates `AuthenticationScheme` and handler directly, tests auth flow

### Test Naming
- Descriptive method names: `MethodName_Scenario_ExpectedResult`
- Section headers with Unicode box-drawing: `// ═══ ScrapeLog ══════════════════`
- Subsection headers: `// ─── 429 rate limit ─────────────`

---

## Integration Test Patterns

### ApiEndpointIntegrationTests
**Location**: `FSTService.Tests/Integration/ApiEndpointIntegrationTests.cs`

**Structure**: `IClassFixture<FstWebApplicationFactory>` — factory is shared across all tests in the class.

#### FstWebApplicationFactory
- Extends `WebApplicationFactory<Program>`
- **Test API key**: `"test-api-key-12345"` (static const)
- **Configuration overrides** via `AddInMemoryCollection`:
  - `Scraper:ApiOnly = true` (no scraping)
  - `ConnectionStrings:PostgreSQL` → SharedPostgresContainer
  - `Api:ApiKey` → test key
  - JWT settings (secret, issuer, audience)
  - EpicOAuth settings (client ID, secret, redirect URI)
  - Token encryption key
- **Service overrides**:
  - Removes all `IHostedService` (no background scraping)
  - Replaces `NpgsqlDataSource` with `SharedPostgresContainer.CreateDatabase()`
  - Adds `TestDatabaseInitializer` (initializes schema without HTTP calls to Epic CDN)
  - Replaces `TokenManager` with mock returning `"mock_access_token_for_testing"`
  - Replaces `FestivalService` with pre-loaded test songs
  - Registers no-op HTTP handlers for ALL typed HttpClients (prevents real HTTP)
  - Overrides `ApiKeyAuthOptions` (early resolution workaround)
  - Overrides `EpicAuthService` with mock for OAuth exchange

#### TestDatabaseInitializer
- Nested `IHostedService` inside the factory
- Calls `_persistence.Initialize()` directly
- Uses reflection to signal `StartupInitializer._readySignal` TCS as ready

#### HttpMessageHandler_NoOp
- Nested handler returning empty 200 responses
- Used for ALL typed HttpClients in integration tests

#### Test Client Setup
```csharp
_client = factory.CreateClient();            // unauthenticated
_authedClient = factory.CreateClient();      // with X-API-Key header
_authedClient.DefaultRequestHeaders.Add("X-API-Key", FstWebApplicationFactory.TestApiKey);
```

#### Test Pattern: Seed via DI Scope
```csharp
using (var scope = _factory.Services.CreateScope())
{
    var persistence = scope.ServiceProvider.GetRequiredService<GlobalLeaderboardPersistence>();
    var db = persistence.GetOrCreateInstrumentDb("Solo_Guitar");
    db.UpsertEntries(songId, entries);
}
```

### PersistencePipelineIntegrationTests
**Location**: `FSTService.Tests/Integration/PersistencePipelineIntegrationTests.cs`

- Tests the full `GlobalLeaderboardPersistence` pipeline: start writers, enqueue results, drain, verify
- Uses `InMemoryMetaDatabase` + real PostgreSQL (not WebApplicationFactory)
- Creates `GlobalLeaderboardPersistence` directly with `NullLoggerFactory`
- Factory helper: `MakeResult()` for creating `GlobalLeaderboardResult` test data
- Tests: pipeline aggregation, score change detection, scrape run lifecycle, account name persistence

---

## Mock Patterns

### 1. HTTP Mocking: MockHttpMessageHandler (Queue-Based)
- Preconfigure response sequence for deterministic testing
- Verify captured requests for assertion
- Used with `new HttpClient(mockHandler)`

### 2. Service Substitution: NSubstitute
- **Interface mocking**: `Substitute.For<IInterface>()`
- **Virtual class mocking**: `Substitute.For<ClassName>(constructorArgs...)`
  - Works because NSubstitute creates proxies for virtual members
  - Requires real constructor args (even if not used)
- **Return configuration**: `.Returns(value)`, `.Returns(callInfo => ...)`, `.ReturnsForAnyArgs()`
- **Exception configuration**: `.Throws(ex)`, `.ThrowsForAnyArgs(ex)`
- **Arg matchers**: `Arg.Any<T>()`, `Arg.Is<T>(predicate)`

### 3. Database Mocking: Real PostgreSQL via Testcontainers
- **NOT in-memory fakes** — uses real PostgreSQL for all persistence tests
- `SharedPostgresContainer` provides isolated databases per test class
- `InMemoryMetaDatabase` and `TempInstrumentDatabase` are thin wrappers
- Schema is initialized via `DatabaseInitializer.EnsureSchemaAsync()` — same as production
- **Rationale**: Tests exercise real SQL queries, actual PostgreSQL behavior, and production schema

### 4. No-Op HTTP Handlers
- `NoOpHttpHandler` / `HttpMessageHandler_NoOp` — returns 200 OK for any request
- Used when a dependency requires HttpClient but HTTP calls aren't the focus
- Each typed HttpClient in integration tests gets a no-op handler

### 5. FestivalService via Reflection
- `CreateTestFestivalService()` / `CreateServiceWithSongs()` — populates internal `_songs` dictionary via reflection
- Avoids HTTP calls to Epic CDN for song data
- Used in both unit and integration tests

### 6. IServiceProvider Mock (for DI resolution in tests)
```csharp
var serviceProvider = Substitute.For<IServiceProvider>();
serviceProvider.GetService(typeof(SongProcessingMachine)).Returns(_machine);
```

---

## Coverage Configuration

### CI Enforcement (`.github/workflows/publish-image.yml`)
- **Threshold**: **94% line coverage** for FSTService
- **Collection tool**: `coverlet.collector` via `--collect:"XPlat Code Coverage"`
- **Report format**: Cobertura XML
- **Include filter**: `[FSTService]*` — only FSTService assembly, not tests or Core
- **Enforcement logic**:
  1. Finds `coverage.cobertura.xml` in TestResults
  2. Extracts `line-rate` from `<package name="FSTService">` element
  3. Falls back to top-level `<coverage line-rate>` if no package-specific rate
  4. Converts to percentage, compares against threshold using `bc -l`
  5. Fails the build if below 94%

### Local Coverage
```bash
dotnet test FSTService.Tests\FSTService.Tests.csproj --collect:"XPlat Code Coverage" \
  -- DataCollectionRunSettings.DataCollectors.DataCollector.Configuration.Format=cobertura \
  DataCollectionRunSettings.DataCollectors.DataCollector.Configuration.Include="[FSTService]*"
```

### CI Test Command
```bash
dotnet test FSTService.Tests/FSTService.Tests.csproj \
  -c Release --no-build \
  --logger trx --results-directory TestResults \
  --collect:"XPlat Code Coverage" \
  -- DataCollectionRunSettings.DataCollectors.DataCollector.Configuration.Format=cobertura \
  DataCollectionRunSettings.DataCollectors.DataCollector.Configuration.Include="[FSTService]*"
```

---

## Test Data

### Test Song Factory
```csharp
// Via reflection into FestivalService internal dictionary
dict["testSong1"] = new Song
{
    track = new Track
    {
        su = "testSong1", tt = "Test Song", an = "Test Artist",
        @in = new In { gr = 5, ba = 3, vl = 4, ds = 2 }
    }
};
```

### Leaderboard Entry Factory
```csharp
private static LeaderboardEntry MakeEntry(string accountId, int score,
    int accuracy = 95, bool fc = false, int stars = 5, int season = 3, int difficulty = 3) =>
    new()
    {
        AccountId = accountId, Score = score, Accuracy = accuracy,
        IsFullCombo = fc, Stars = stars, Season = season, Difficulty = difficulty,
        Percentile = 99.0, EndTime = "2025-01-15T12:00:00Z",
    };
```

### GlobalLeaderboardResult Factory
```csharp
private static GlobalLeaderboardResult MakeResult(
    string songId, string instrument, params (string AccountId, int Score)[] entries)
{
    return new GlobalLeaderboardResult
    {
        SongId = songId, Instrument = instrument,
        Entries = entries.Select(e => new LeaderboardEntry
        {
            AccountId = e.AccountId, Score = e.Score,
            Accuracy = 95, IsFullCombo = false, Stars = 5,
            Season = 3, Percentile = 99.0,
        }).ToList(),
    };
}
```

### Test Accounts (from AGENTS.md)
| Username | Account ID |
|---|---|
| SFentonX | `195e93ef108143b2975ee46662d4d0e1` |
| captainparticles | `cb8ebb19b32c40d1a736d7f8efec17ac` |
| kahnyri | `4c2a1300df4c49a9b9d2b352d704bdf0` |

### Integration Test API Key
- `"test-api-key-12345"` — used via `X-API-Key` header

---

## Test File Inventory (56 Unit + 2 Integration = 58 Total)

### Unit Tests by Domain
**Scraping Pipeline**: `ScraperWorkerTests`, `ScraperWorkerStatefulTests`, `ScraperWorkerModeTests`, `SongProcessingMachineTests`, `DeepScrapeTests`, `DeepScrapeCoordinatorTests`, `ScrapeProgressTrackerTests`, `BatchResultProcessorTests`, `PostScrapeOrchestratorTests`, `PostScrapeRefresherTests`, `ScoreBackfillerTests`, `BackfillQueueTests`, `HistoryReconstructorTests`, `HistoryReconstructorInstanceTests`, `FirstSeenSeasonCalculatorTests`, `GlobalLeaderboardScraperTests`

**Persistence**: `MetaDatabaseTests`, `MetaDatabaseAdditionalTests`, `MetaDatabaseRankingsTests`, `MetaDatabaseRivalsTests`, `InstrumentDatabaseTests`, `InstrumentDatabaseRankingsTests`, `GlobalLeaderboardPersistenceTests`, `DatabaseInitializerTests`, `PathDataStoreTests`

**API**: `RankingsEndpointsTests`, `SongsCacheServiceTests`, `SongsCachePrimeTests`, `ShopCacheServiceTests`, `ShopUrlHelperTests`, `NotificationServiceTests`, `PrecomputeSubResourceTests`, `EndpointPrecomputerWiringTests`, `ScrapeTimePrecomputerTests`

**Auth/Security**: `TokenManagerTests`, `FileCredentialStoreTests`, `ApiKeyAuthHandlerTests`, `PathTraversalGuardMiddlewareTests`

**Rivals**: `RivalsCalculatorTests`, `RivalsOrchestratorTests`, `LeaderboardRivalsCalculatorTests`

**Rankings**: `RankingsCalculatorTests`, `PlayerStatsCalculatorTests`

**Performance/Concurrency**: `ResilientHttpExecutorTests`, `SharedDopPoolTests`, `AdaptiveConcurrencyLimiterTests`

**Config/Models**: `ScraperOptionsAndModelsTests`, `ComboIdsTests`

**Paths**: `PathGeneratorTests`, `PathGeneratorOrchestrationTests`, `MidiCryptorTests`, `MidiTrackRenamerTests`

**Shop**: `ItemShopServiceTests`

### Integration Tests
- `ApiEndpointIntegrationTests` — full ASP.NET Core pipeline via WebApplicationFactory
- `PersistencePipelineIntegrationTests` — GlobalLeaderboardPersistence writer pipeline

---

## Key Architecture Decisions

1. **Real PostgreSQL over in-memory fakes**: All persistence tests use Testcontainers with real PostgreSQL. This ensures SQL queries, indexes, and schema migrations are tested against production-equivalent behavior.

2. **Shared container, isolated databases**: One PostgreSQL container per test run, but each test class gets a unique database via `CREATE DATABASE "{guid}"`. This balances speed (container startup is expensive) with isolation (tests don't interfere).

3. **No FluentAssertions dependency**: Tests use xUnit's built-in `Assert.*` methods exclusively.

4. **Cross-class parallelism via base class splitting**: `ScraperWorkerTestBase` is subclassed into multiple test files so xUnit runs them in parallel (default behavior: classes run in parallel, tests within a class run sequentially).

5. **Reflection for private method testing**: Used sparingly for internal helpers and field access (FestivalService, ScraperWorker private methods).

6. **Queue-based HTTP mocking**: `MockHttpMessageHandler` provides deterministic HTTP response sequences without external mock libraries.
