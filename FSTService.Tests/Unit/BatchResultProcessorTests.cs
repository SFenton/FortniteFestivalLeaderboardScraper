using FSTService.Persistence;
using FSTService.Scraping;
using FSTService.Tests.Helpers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace FSTService.Tests.Unit;

public class BatchResultProcessorTests : IDisposable
{
    private readonly InMemoryMetaDatabase _metaDb = new();
    private readonly string _dataDir;
    private readonly GlobalLeaderboardPersistence _persistence;
    private readonly BatchResultProcessor _processor;

    public BatchResultProcessorTests()
    {
        _dataDir = Path.Combine(Path.GetTempPath(), $"fst_brp_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dataDir);
        var loggerFactory = Substitute.For<ILoggerFactory>();
        loggerFactory.CreateLogger(Arg.Any<string>()).Returns(Substitute.For<ILogger>());
        loggerFactory.CreateLogger<InstrumentDatabase>().Returns(Substitute.For<ILogger<InstrumentDatabase>>());
        var persLog = Substitute.For<ILogger<GlobalLeaderboardPersistence>>();
        _persistence = new GlobalLeaderboardPersistence(_metaDb.Db, loggerFactory, persLog, _metaDb.DataSource, Options.Create(new FeatureOptions()));
        _persistence.Initialize();
        _processor = new BatchResultProcessor(
            _persistence, Substitute.For<ILogger<BatchResultProcessor>>());
    }

    public void Dispose()
    {
        _persistence.Dispose();
        _metaDb.Dispose();
        try { Directory.Delete(_dataDir, true); } catch { }
    }

    [Fact]
    public void ProcessAlltimeResults_EmptyEntries_ReturnsZero()
    {
        var result = _processor.ProcessAlltimeResults(
            "song1", "Solo_Guitar",
            Array.Empty<LeaderboardEntry>(),
            new HashSet<string>());
        Assert.Equal(0, result);
    }

    [Fact]
    public void ProcessAlltimeResults_NewEntry_InsertsScoreChange()
    {
        var entries = new[]
        {
            new LeaderboardEntry { AccountId = "acct1", Score = 50000, Rank = 5, Accuracy = 95, Stars = 6 }
        };
        var result = _processor.ProcessAlltimeResults(
            "song1", "Solo_Guitar", entries, new HashSet<string>());

        Assert.Equal(1, result);

        // Verify the entry was written to instrument DB
        var db = _persistence.GetOrCreateInstrumentDb("Solo_Guitar");
        var entry = db.GetEntry("song1", "acct1");
        Assert.NotNull(entry);
        Assert.Equal(50000, entry!.Score);
    }

    [Fact]
    public void ProcessAlltimeResults_StaleEntry_WithScoreChange_RecordsHistory()
    {
        var db = _persistence.GetOrCreateInstrumentDb("Solo_Guitar");
        db.UpsertEntries("song1", [new LeaderboardEntry { AccountId = "acct1", Score = 40000, Rank = 10 }]);

        var existing = new HashSet<string> { "acct1" };
        var entries = new[]
        {
            new LeaderboardEntry { AccountId = "acct1", Score = 50000, Rank = 5, Accuracy = 95, Stars = 6 }
        };
        var result = _processor.ProcessAlltimeResults(
            "song1", "Solo_Guitar", entries, existing);

        Assert.Equal(1, result);

        // Verify updated in instrument DB
        var updated = db.GetEntry("song1", "acct1");
        Assert.Equal(50000, updated!.Score);
    }

    [Fact]
    public void ProcessAlltimeResults_StaleEntry_SameScore_NoHistoryInsert()
    {
        var db = _persistence.GetOrCreateInstrumentDb("Solo_Guitar");
        db.UpsertEntries("song1", [new LeaderboardEntry { AccountId = "acct1", Score = 50000, Rank = 5 }]);

        var existing = new HashSet<string> { "acct1" };
        var entries = new[]
        {
            new LeaderboardEntry { AccountId = "acct1", Score = 50000, Rank = 5, Accuracy = 95, Stars = 6 }
        };
        var result = _processor.ProcessAlltimeResults(
            "song1", "Solo_Guitar", entries, existing);

        Assert.Equal(1, result);
    }

    [Fact]
    public void ProcessAlltimeResults_RaisesPopulationFloor()
    {
        var entries = new[]
        {
            new LeaderboardEntry { AccountId = "acct1", Score = 50000, Rank = 100, Accuracy = 95, Stars = 6 }
        };
        var result = _processor.ProcessAlltimeResults("song1", "Solo_Guitar", entries, new HashSet<string>());
        Assert.Equal(1, result);
    }

    [Fact]
    public void ProcessSeasonalSessions_EmptySessions_ReturnsZero()
    {
        var result = _processor.ProcessSeasonalSessions(
            "song1", "Solo_Guitar", 1, Array.Empty<SessionHistoryEntry>());
        Assert.Equal(0, result);
    }

    [Fact]
    public void ProcessSeasonalSessions_InsertsHistory()
    {
        var sessions = new[]
        {
            new SessionHistoryEntry
            {
                AccountId = "acct1", Score = 45000, Rank = 8,
                Accuracy = 92, IsFullCombo = false, Stars = 5,
                EndTime = "2025-06-01T00:00:00Z",
            }
        };
        var result = _processor.ProcessSeasonalSessions(
            "song1", "Solo_Guitar", 5, sessions);

        Assert.Equal(1, result);
    }

    [Fact]
    public void ProcessSeasonalSessions_WithExistingEntry_RecordsOldScore()
    {
        var db = _persistence.GetOrCreateInstrumentDb("Solo_Guitar");
        db.UpsertEntries("song1", [new LeaderboardEntry { AccountId = "acct1", Score = 40000, Rank = 10 }]);

        var sessions = new[]
        {
            new SessionHistoryEntry
            {
                AccountId = "acct1", Score = 50000, Rank = 5,
                Accuracy = 95, IsFullCombo = true, Stars = 6,
                EndTime = "2025-06-01T00:00:00Z",
            }
        };
        var result = _processor.ProcessSeasonalSessions(
            "song1", "Solo_Guitar", 5, sessions);

        Assert.Equal(1, result);
    }
}
