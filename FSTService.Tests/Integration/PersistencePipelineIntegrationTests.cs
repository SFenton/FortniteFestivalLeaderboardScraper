using FSTService.Persistence;
using FSTService.Scraping;
using FSTService.Tests.Helpers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace FSTService.Tests.Integration;

/// <summary>
/// Integration tests that exercise the full GlobalLeaderboardPersistence pipeline
/// with real SQLite databases, including change detection, pipeline aggregates,
/// and the channel-based writer system.
/// </summary>
public sealed class PersistencePipelineIntegrationTests : IDisposable
{
    private readonly InMemoryMetaDatabase _metaFixture = new();
    private readonly string _dataDir;

    public PersistencePipelineIntegrationTests()
    {
        _dataDir = Path.Combine(Path.GetTempPath(), $"fst_pipe_test_{Guid.NewGuid():N}");
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
            _dataDir,
            _metaFixture.Db,
            loggerFactory,
            NullLogger<GlobalLeaderboardPersistence>.Instance);
        glp.Initialize();
        return glp;
    }

    private static GlobalLeaderboardResult MakeResult(
        string songId, string instrument, params (string AccountId, int Score)[] entries)
    {
        return new GlobalLeaderboardResult
        {
            SongId = songId,
            Instrument = instrument,
            Entries = entries.Select(e => new LeaderboardEntry
            {
                AccountId = e.AccountId,
                Score = e.Score,
                Accuracy = 95,
                IsFullCombo = false,
                Stars = 5,
                Season = 3,
                Percentile = 99.0,
            }).ToList(),
        };
    }

    /// <summary>
    /// End-to-end pipeline: start writers, enqueue results, drain, verify aggregates.
    /// </summary>
    [Fact]
    public async Task Pipeline_enqueue_and_drain_produces_correct_aggregates()
    {
        using var glp = CreatePersistence();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var registered = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "acct_tracked" };

        // Start pipeline
        var agg = glp.StartWriters(ct: cts.Token);

        // Enqueue results for different instruments
        await glp.EnqueueResultAsync(
            MakeResult("song_1", "Solo_Guitar",
                ("acct_tracked", 100_000), ("acct_other", 90_000)),
            registered, cts.Token);

        await glp.EnqueueResultAsync(
            MakeResult("song_1", "Solo_Bass",
                ("acct_tracked", 80_000)),
            registered, cts.Token);

        await glp.EnqueueResultAsync(
            MakeResult("song_2", "Solo_Guitar",
                ("acct_tracked", 70_000)),
            registered, cts.Token);

        // Drain and wait for all writers to finish
        await glp.DrainWritersAsync();

        // Verify aggregates
        Assert.Equal(4, agg.TotalEntries); // 2 + 1 + 1
        // SongsWithData is incremented by the ScraperWorker, not the pipeline writer

        // Registered entries should be tracked
        var seen = agg.SeenRegisteredEntries.ToList();
        Assert.Contains(("acct_tracked", "song_1", "Solo_Guitar"), seen);
        Assert.Contains(("acct_tracked", "song_1", "Solo_Bass"), seen);
        Assert.Contains(("acct_tracked", "song_2", "Solo_Guitar"), seen);

        // Verify data was persisted
        var board = glp.GetLeaderboard("song_1", "Solo_Guitar");
        Assert.NotNull(board);
        Assert.Equal(2, board.Count);
    }

    /// <summary>
    /// Pipeline detects score changes for registered users as data flows through.
    /// </summary>
    [Fact]
    public async Task Pipeline_detects_score_changes_for_registered_users()
    {
        using var glp = CreatePersistence();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var registered = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "acct_1" };

        // First pass — establish baseline
        glp.PersistResult(MakeResult("song_1", "Solo_Guitar", ("acct_1", 80_000)));

        // Second pass via pipeline — score changed
        var agg = glp.StartWriters(ct: cts.Token);

        await glp.EnqueueResultAsync(
            MakeResult("song_1", "Solo_Guitar", ("acct_1", 100_000)),
            registered, cts.Token);

        await glp.DrainWritersAsync();

        Assert.Equal(1, agg.TotalChanges);
        Assert.Contains("acct_1", agg.ChangedAccountIds);
    }

    /// <summary>
    /// Scrape run lifecycle: start → persist → complete, then query last completed.
    /// </summary>
    [Fact]
    public void Scrape_run_lifecycle_with_persistence()
    {
        using var glp = CreatePersistence();

        // Start scrape run
        var runId = _metaFixture.Db.StartScrapeRun();

        // Persist some results
        glp.PersistResult(MakeResult("song_1", "Solo_Guitar",
            ("acct_1", 100_000), ("acct_2", 90_000)));
        glp.PersistResult(MakeResult("song_1", "Solo_Bass",
            ("acct_1", 80_000)));

        var counts = glp.GetEntryCounts();
        var totalEntries = counts.Values.Sum();

        // Complete the scrape run
        _metaFixture.Db.CompleteScrapeRun(runId, 1, (int)totalEntries, 10, 1_000_000);

        // Verify last completed run
        var last = _metaFixture.Db.GetLastCompletedScrapeRun();
        Assert.NotNull(last);
        Assert.Equal(runId, last.Id);
        Assert.Equal(1, last.SongsScraped);
    }

    /// <summary>
    /// Account names are persisted to meta DB during result persistence.
    /// </summary>
    [Fact]
    public void Result_persistence_tracks_account_ids()
    {
        using var glp = CreatePersistence();

        glp.PersistResult(MakeResult("song_1", "Solo_Guitar",
            ("epic_acct_1", 100_000), ("epic_acct_2", 90_000)));

        var unresolved = _metaFixture.Db.GetUnresolvedAccountIds();
        Assert.Contains("epic_acct_1", unresolved);
        Assert.Contains("epic_acct_2", unresolved);

        // Resolve one
        _metaFixture.Db.InsertAccountNames([("epic_acct_1", "PlayerOne")]);

        var stillUnresolved = _metaFixture.Db.GetUnresolvedAccountIds();
        Assert.Contains("epic_acct_2", stillUnresolved);
        Assert.DoesNotContain("epic_acct_1", stillUnresolved);
    }
}
