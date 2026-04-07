using FSTService.Persistence;
using FSTService.Scraping;
using FSTService.Tests.Helpers;
using NSubstitute;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace FSTService.Tests.Unit;

/// <summary>
/// Tests for <see cref="RivalsOrchestrator"/> lifecycle, status management,
/// and parallel computation.
/// </summary>
public sealed class RivalsOrchestratorTests : IDisposable
{
    private readonly InMemoryMetaDatabase _metaFixture = new();
    private readonly string _dataDir;

    public RivalsOrchestratorTests()
    {
        _dataDir = Path.Combine(Path.GetTempPath(), $"fst_rivals_orch_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dataDir);
    }

    public void Dispose()
    {
        _metaFixture.Dispose();
        try { Directory.Delete(_dataDir, recursive: true); } catch { }
    }

    private GlobalLeaderboardPersistence CreatePersistence()
    {
        var loggerFactory = new NullLoggerFactory();
        var glp = new GlobalLeaderboardPersistence(
            _metaFixture.Db,
            loggerFactory,
            NullLogger<GlobalLeaderboardPersistence>.Instance,
            _metaFixture.DataSource,
            Options.Create(new FeatureOptions()));
        glp.Initialize();
        return glp;
    }

    private (RivalsOrchestrator Orch, ScrapeProgressTracker Progress) CreateOrchestrator(
        GlobalLeaderboardPersistence persistence)
    {
        var calculator = new RivalsCalculator(persistence, NullLogger<RivalsCalculator>.Instance);
        var progress = new ScrapeProgressTracker();
        var orch = new RivalsOrchestrator(calculator, persistence, new Api.NotificationService(Substitute.For<ILogger<Api.NotificationService>>()), progress, new UserSyncProgressTracker(new Api.NotificationService(Substitute.For<ILogger<Api.NotificationService>>()), Substitute.For<ILogger<UserSyncProgressTracker>>()), new Api.ResponseCacheService(TimeSpan.FromMinutes(5)), NullLogger<RivalsOrchestrator>.Instance);
        return (orch, progress);
    }

    [Fact]
    public async Task ComputeAllAsync_creates_status_rows_for_registered_users()
    {
        var persistence = CreatePersistence();
        var (orch, _) = CreateOrchestrator(persistence);

        var registeredIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "acct_1", "acct_2" };
        await orch.ComputeAllAsync(registeredIds, null, CancellationToken.None);

        // Both accounts should have RivalsStatus rows
        Assert.NotNull(_metaFixture.Db.GetRivalsStatus("acct_1"));
        Assert.NotNull(_metaFixture.Db.GetRivalsStatus("acct_2"));
    }

    [Fact]
    public async Task ComputeAllAsync_skips_when_no_registered_users()
    {
        var persistence = CreatePersistence();
        var (orch, progress) = CreateOrchestrator(persistence);

        await orch.ComputeAllAsync(
            new HashSet<string>(), null, CancellationToken.None);

        // Should not have set phase
        Assert.Empty(_metaFixture.Db.GetPendingRivalsAccounts());
    }

    [Fact]
    public async Task ComputeAllAsync_skips_when_all_users_already_complete()
    {
        var persistence = CreatePersistence();
        var (orch, progress) = CreateOrchestrator(persistence);

        // Register user but mark rivals as already complete
        _metaFixture.Db.EnsureRivalsStatus("acct-complete");
        _metaFixture.Db.CompleteRivals("acct-complete", 0, 0);

        var registeredIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "acct-complete" };
        await orch.ComputeAllAsync(registeredIds, null, CancellationToken.None);
        // toCompute should be empty → early return at line 63
    }

    [Fact]
    public async Task ComputeAllAsync_processes_pending_accounts()
    {
        var persistence = CreatePersistence();
        var (orch, _) = CreateOrchestrator(persistence);

        // Pre-create pending status
        _metaFixture.Db.EnsureRivalsStatus("acct_1");

        var registeredIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "acct_1" };
        await orch.ComputeAllAsync(registeredIds, null, CancellationToken.None);

        // Should be complete (even with no data, just 0 rivals)
        var status = _metaFixture.Db.GetRivalsStatus("acct_1");
        Assert.Equal("complete", status!.Status);
    }

    [Fact]
    public async Task ComputeAllAsync_includes_dirty_users()
    {
        var persistence = CreatePersistence();
        var (orch, _) = CreateOrchestrator(persistence);

        // acct_1 is complete, acct_2 is not pending but has dirty instruments
        _metaFixture.Db.EnsureRivalsStatus("acct_1");
        _metaFixture.Db.StartRivals("acct_1");
        _metaFixture.Db.CompleteRivals("acct_1", 0, 0);

        var registeredIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "acct_1", "acct_2" };
        var dirtyMap = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["acct_2"] = new(StringComparer.OrdinalIgnoreCase) { "Solo_Guitar" },
        };

        await orch.ComputeAllAsync(registeredIds, dirtyMap, CancellationToken.None);

        // acct_2 should have been computed via dirty path
        var status2 = _metaFixture.Db.GetRivalsStatus("acct_2");
        Assert.NotNull(status2);
        Assert.Equal("complete", status2.Status);
    }

    [Fact]
    public void ComputeForUser_completes_with_zero_rivals_when_no_data()
    {
        // User has a RivalsStatus row but no scores → should complete with 0 combos/rivals
        var persistence = CreatePersistence();
        var (orch, _) = CreateOrchestrator(persistence);

        _metaFixture.Db.EnsureRivalsStatus("empty_user");

        orch.ComputeForUser("empty_user");

        var status = _metaFixture.Db.GetRivalsStatus("empty_user");
        Assert.Equal("complete", status!.Status);
        Assert.Equal(0, status.CombosComputed);
        Assert.Equal(0, status.RivalsFound);
    }
}
