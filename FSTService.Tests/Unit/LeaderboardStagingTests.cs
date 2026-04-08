using FSTService.Persistence;
using FSTService.Scraping;
using FSTService.Tests.Helpers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace FSTService.Tests.Unit;

/// <summary>
/// Tests for the leaderboard staging infrastructure (Phase 1):
/// schema creation, StageChunk, UpsertStagingMeta, EnqueueDeepScrapeJob,
/// FinalizeInstrumentFromStaging, cleanup, and two-pass finalization.
/// </summary>
public class LeaderboardStagingTests : IDisposable
{
    private readonly InMemoryMetaDatabase _metaFixture;
    private readonly GlobalLeaderboardPersistence _persistence;
    private const string TestInstrument = "Solo_Guitar";

    public LeaderboardStagingTests()
    {
        _metaFixture = new InMemoryMetaDatabase();
        var loggerFactory = Substitute.For<ILoggerFactory>();
        loggerFactory.CreateLogger(Arg.Any<string>()).Returns(Substitute.For<ILogger>());
        _persistence = new GlobalLeaderboardPersistence(
            _metaFixture.Db, loggerFactory,
            Substitute.For<ILogger<GlobalLeaderboardPersistence>>(),
            _metaFixture.DataSource,
            Options.Create(new FeatureOptions()));
        _persistence.Initialize();
    }

    public void Dispose()
    {
        _persistence.Dispose();
        _metaFixture.Dispose();
    }

    // ── Schema idempotency ──────────────────────────────────────────

    [Fact]
    public void SchemaCreation_IsIdempotent()
    {
        // Schema was already created by InMemoryMetaDatabase constructor.
        // Creating it again should not throw.
        DatabaseInitializer.EnsureSchemaAsync(_metaFixture.DataSource)
            .GetAwaiter().GetResult();
    }

    // ── StageChunk ──────────────────────────────────────────────────

    [Fact]
    public void StageChunk_InsertsEntries()
    {
        var scrapeId = _metaFixture.Db.StartScrapeRun();
        var entries = MakeEntries(page: 0, count: 5);

        _metaFixture.Db.StageChunk(scrapeId, "song1", TestInstrument, entries);

        var count = _metaFixture.Db.GetStagedEntryCount(scrapeId, "song1", TestInstrument);
        Assert.Equal(5, count);
    }

    [Fact]
    public void StageChunk_EmptyList_DoesNothing()
    {
        var scrapeId = _metaFixture.Db.StartScrapeRun();

        _metaFixture.Db.StageChunk(scrapeId, "song1", TestInstrument,
            Array.Empty<(int, LeaderboardEntry)>());

        var count = _metaFixture.Db.GetStagedEntryCount(scrapeId, "song1", TestInstrument);
        Assert.Equal(0, count);
    }

    [Fact]
    public void StageChunk_MultipleChunks_Accumulate()
    {
        var scrapeId = _metaFixture.Db.StartScrapeRun();

        _metaFixture.Db.StageChunk(scrapeId, "song1", TestInstrument,
            MakeEntries(page: 0, count: 3));
        _metaFixture.Db.StageChunk(scrapeId, "song1", TestInstrument,
            MakeEntries(page: 1, count: 4, startIndex: 3));

        var count = _metaFixture.Db.GetStagedEntryCount(scrapeId, "song1", TestInstrument);
        Assert.Equal(7, count);
    }

    // ── UpsertStagingMeta ───────────────────────────────────────────

    [Fact]
    public void UpsertStagingMeta_InsertAndUpdate()
    {
        var scrapeId = _metaFixture.Db.StartScrapeRun();

        _metaFixture.Db.UpsertStagingMeta(scrapeId, "song1", TestInstrument, new StagingMetaUpdate
        {
            ReportedPages = 100, PagesScraped = 50, EntriesStaged = 5000,
            Requests = 50, BytesReceived = 1_000_000,
        });

        var rows = _metaFixture.Db.GetStagingMeta(scrapeId);
        Assert.Single(rows);
        Assert.Equal(100, rows[0].ReportedPages);
        Assert.Equal(50, rows[0].PagesScraped);

        // Update (additive semantics: pages_scraped, entries_staged, requests, bytes_received accumulate)
        _metaFixture.Db.UpsertStagingMeta(scrapeId, "song1", TestInstrument, new StagingMetaUpdate
        {
            ReportedPages = 100, PagesScraped = 100, EntriesStaged = 10000,
            Requests = 100, BytesReceived = 2_000_000, DeepScrapeStatus = "eligible",
        });

        rows = _metaFixture.Db.GetStagingMeta(scrapeId);
        Assert.Single(rows);
        Assert.Equal(150, rows[0].PagesScraped);      // 50 + 100
        Assert.Equal(15000, rows[0].EntriesStaged);   // 5000 + 10000
        Assert.Equal(150, rows[0].Requests);           // 50 + 100
        Assert.Equal(3_000_000, rows[0].BytesReceived); // 1M + 2M
        Assert.Equal("eligible", rows[0].DeepScrapeStatus);
    }

    // ── EnqueueDeepScrapeJob ────────────────────────────────────────

    [Fact]
    public void EnqueueDeepScrapeJob_InsertsAndQueries()
    {
        var scrapeId = _metaFixture.Db.StartScrapeRun();

        _metaFixture.Db.EnqueueDeepScrapeJob(new DeepScrapeJobInfo
        {
            ScrapeId = scrapeId, SongId = "song1", Instrument = TestInstrument,
            Label = "Test Song", ValidCutoff = 95000, ValidEntryTarget = 10000,
            Wave2StartPage = 100, ReportedPages = 500, InitialValidCount = 8000,
        });

        var jobs = _metaFixture.Db.GetDeepScrapeJobs(scrapeId);
        Assert.Single(jobs);
        Assert.Equal("pending", jobs[0].Status);
        Assert.Equal(100, jobs[0].Wave2StartPage);
        Assert.Equal(8000, jobs[0].InitialValidCount);
    }

    [Fact]
    public void DeepScrapeJob_CursorUpdate_And_Completion()
    {
        var scrapeId = _metaFixture.Db.StartScrapeRun();
        _metaFixture.Db.EnqueueDeepScrapeJob(new DeepScrapeJobInfo
        {
            ScrapeId = scrapeId, SongId = "song1", Instrument = TestInstrument,
            ValidCutoff = 95000, ValidEntryTarget = 10000,
            Wave2StartPage = 100, ReportedPages = 500, InitialValidCount = 8000,
        });

        _metaFixture.Db.UpdateDeepScrapeJobCursor(scrapeId, "song1", TestInstrument,
            cursorPage: 150, currentValidCount: 9500);

        var jobs = _metaFixture.Db.GetDeepScrapeJobs(scrapeId, status: "running");
        Assert.Single(jobs);
        Assert.Equal(150, jobs[0].CursorPage);
        Assert.Equal(9500, jobs[0].CurrentValidCount);

        _metaFixture.Db.CompleteDeepScrapeJob(scrapeId, "song1", TestInstrument, "complete");

        jobs = _metaFixture.Db.GetDeepScrapeJobs(scrapeId, status: "complete");
        Assert.Single(jobs);
        Assert.NotNull(jobs[0].CompletedAt);
    }

    // ── Cleanup ─────────────────────────────────────────────────────

    [Fact]
    public void CleanupAbandonedStaging_DeletesOldScrapeData()
    {
        var oldScrapeId = _metaFixture.Db.StartScrapeRun();
        _metaFixture.Db.StageChunk(oldScrapeId, "song1", TestInstrument,
            MakeEntries(page: 0, count: 3));
        _metaFixture.Db.UpsertStagingMeta(oldScrapeId, "song1", TestInstrument, new StagingMetaUpdate
        {
            ReportedPages = 100, PagesScraped = 1, EntriesStaged = 3,
            Requests = 1, BytesReceived = 1000,
        });
        _metaFixture.Db.EnqueueDeepScrapeJob(new DeepScrapeJobInfo
        {
            ScrapeId = oldScrapeId, SongId = "song1", Instrument = TestInstrument,
            ValidCutoff = 95000, ValidEntryTarget = 10000,
            Wave2StartPage = 100, ReportedPages = 500, InitialValidCount = 0,
        });

        var newScrapeId = _metaFixture.Db.StartScrapeRun();

        var deleted = _metaFixture.Db.CleanupAbandonedStaging(newScrapeId);
        Assert.True(deleted > 0);

        Assert.Equal(0, _metaFixture.Db.GetStagedEntryCount(oldScrapeId, "song1", TestInstrument));
        Assert.Empty(_metaFixture.Db.GetStagingMeta(oldScrapeId));
        Assert.Empty(_metaFixture.Db.GetDeepScrapeJobs(oldScrapeId));
    }

    // ── FinalizeInstrumentFromStaging ───────────────────────────────

    [Fact]
    public void FinalizeLeaderboard_MergesIntoLiveTable()
    {
        var scrapeId = _metaFixture.Db.StartScrapeRun();
        var entries = MakeEntries(page: 0, count: 10, baseScore: 50000);

        _metaFixture.Db.StageChunk(scrapeId, "song1", TestInstrument, entries);
        _metaFixture.Db.UpsertStagingMeta(scrapeId, "song1", TestInstrument, new StagingMetaUpdate
        {
            ReportedPages = 1, PagesScraped = 1, EntriesStaged = 10,
            Requests = 1, BytesReceived = 5000,
        });

        var (rowsMerged, scoreChanges, affectedSongs) = _persistence.FinalizeInstrumentFromStaging(
            scrapeId, TestInstrument);

        Assert.Equal(10, rowsMerged);
        Assert.Equal(0, scoreChanges);
        Assert.Equal(1, affectedSongs.Count); // one song had entries merged

        // Staged rows should be deleted
        Assert.Equal(0, _metaFixture.Db.GetStagedEntryCount(scrapeId, "song1", TestInstrument));

        // Wave 1 should be marked finalized
        var meta = _metaFixture.Db.GetStagingMeta(scrapeId);
        Assert.Single(meta);
        Assert.NotNull(meta[0].Wave1FinalizedAt);
    }

    [Fact]
    public void FinalizeLeaderboard_DetectsScoreChangesForRegisteredUsers()
    {
        var scrapeId = _metaFixture.Db.StartScrapeRun();
        var accountId = "registered_user_1";

        // Pre-populate with an existing entry
        var db = _persistence.GetOrCreateInstrumentDb(TestInstrument);
        db.UpsertEntries("song1", [new LeaderboardEntry
        {
            AccountId = accountId, Score = 40000, Accuracy = 90,
            IsFullCombo = false, Stars = 4, Season = 1,
        }]);

        // Stage a higher-score entry
        _metaFixture.Db.StageChunk(scrapeId, "song1", TestInstrument,
        [
            (0, new LeaderboardEntry
            {
                AccountId = accountId, Score = 50000, Accuracy = 95,
                IsFullCombo = true, Stars = 5, Season = 1,
            })
        ]);
        _metaFixture.Db.UpsertStagingMeta(scrapeId, "song1", TestInstrument, new StagingMetaUpdate
        {
            ReportedPages = 1, PagesScraped = 1, EntriesStaged = 1,
            Requests = 1, BytesReceived = 500,
        });

        var registeredIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { accountId };

        var (rowsMerged, scoreChanges, _) = _persistence.FinalizeInstrumentFromStaging(
            scrapeId, TestInstrument, registeredIds);

        Assert.Equal(1, rowsMerged);
        Assert.Equal(1, scoreChanges);

        // Verify score was updated in live table
        var entry = db.GetEntry("song1", accountId);
        Assert.NotNull(entry);
        Assert.Equal(50000, entry.Score);
    }

    [Fact]
    public void TwoPassFinalization_Wave1ThenWave2()
    {
        var scrapeId = _metaFixture.Db.StartScrapeRun();

        // Wave 1: pages 0-99 (normal entries)
        var wave1Entries = MakeEntries(page: 0, count: 5, baseScore: 50000);
        _metaFixture.Db.StageChunk(scrapeId, "song1", TestInstrument, wave1Entries);
        _metaFixture.Db.UpsertStagingMeta(scrapeId, "song1", TestInstrument, new StagingMetaUpdate
        {
            ReportedPages = 200, PagesScraped = 1, EntriesStaged = 5,
            Requests = 1, BytesReceived = 2500, DeepScrapeStatus = "eligible",
        });

        // Finalize wave 1
        var (w1Merged, _, _) = _persistence.FinalizeInstrumentFromStaging(
            scrapeId, TestInstrument, wave: 1);
        Assert.Equal(5, w1Merged);

        // Verify wave 1 finalized
        var meta = _metaFixture.Db.GetStagingMeta(scrapeId);
        Assert.Single(meta);
        Assert.NotNull(meta[0].Wave1FinalizedAt);
        Assert.Null(meta[0].Wave2FinalizedAt);

        // Wave 2: pages 100+ (deep scrape entries, different accounts)
        var wave2Entries = MakeEntries(page: 100, count: 3, startIndex: 5, baseScore: 30000);
        _metaFixture.Db.StageChunk(scrapeId, "song1", TestInstrument, wave2Entries);

        // Finalize wave 2
        var (w2Merged, _, _) = _persistence.FinalizeInstrumentFromStaging(
            scrapeId, TestInstrument, wave: 2);
        Assert.Equal(3, w2Merged);

        // Verify both waves finalized
        meta = _metaFixture.Db.GetStagingMeta(scrapeId);
        Assert.NotNull(meta[0].Wave2FinalizedAt);

        // Live table should have entries from both waves
        var db = _persistence.GetOrCreateInstrumentDb(TestInstrument);
        var leaderboard = db.GetLeaderboard("song1");
        Assert.Equal(8, leaderboard.Count);
    }

    [Fact]
    public void TwoPassFinalization_IsIdempotent_ForSameAccounts()
    {
        var scrapeId = _metaFixture.Db.StartScrapeRun();

        // Stage and finalize wave 1
        _metaFixture.Db.StageChunk(scrapeId, "song1", TestInstrument,
            MakeEntries(page: 0, count: 3, baseScore: 50000));
        _metaFixture.Db.UpsertStagingMeta(scrapeId, "song1", TestInstrument, new StagingMetaUpdate
        {
            ReportedPages = 100, PagesScraped = 1, EntriesStaged = 3,
            Requests = 1, BytesReceived = 1500,
        });
        _persistence.FinalizeInstrumentFromStaging(scrapeId, TestInstrument, wave: 1);

        // Stage wave 2 with overlapping account (same account, same score = no-op merge)
        _metaFixture.Db.StageChunk(scrapeId, "song1", TestInstrument,
        [
            (100, new LeaderboardEntry
            {
                AccountId = "acc_0", Score = 50000, Accuracy = 90,
                Stars = 4, Season = 1,
            })
        ]);
        var (w2Merged, _, _) = _persistence.FinalizeInstrumentFromStaging(
            scrapeId, TestInstrument, wave: 2);

        // Should merge without error (idempotent ON CONFLICT)
        Assert.True(w2Merged >= 0);

        var db = _persistence.GetOrCreateInstrumentDb(TestInstrument);
        var entry = db.GetEntry("song1", "acc_0");
        Assert.NotNull(entry);
        Assert.Equal(50000, entry.Score);
    }

    [Fact]
    public void DeleteStagedEntries_RemovesOnlyTargetCombo()
    {
        var scrapeId = _metaFixture.Db.StartScrapeRun();

        _metaFixture.Db.StageChunk(scrapeId, "song1", TestInstrument,
            MakeEntries(page: 0, count: 3));
        _metaFixture.Db.StageChunk(scrapeId, "song2", TestInstrument,
            MakeEntries(page: 0, count: 2, startIndex: 10));

        var deleted = _metaFixture.Db.DeleteStagedEntries(scrapeId, "song1", TestInstrument);
        Assert.Equal(3, deleted);

        Assert.Equal(0, _metaFixture.Db.GetStagedEntryCount(scrapeId, "song1", TestInstrument));
        Assert.Equal(2, _metaFixture.Db.GetStagedEntryCount(scrapeId, "song2", TestInstrument));
    }

    // ── Helpers ──────────────────────────────────────────────────────

    [Fact]
    public void StageChunk_DuplicateAccountId_Throws()
    {
        // Verify that staging a chunk with duplicate account IDs causes a PK violation.
        // This confirms the crash root cause — callers must dedupe before staging.
        var scrapeId = _metaFixture.Db.StartScrapeRun();
        var entries = new List<(int PageNum, LeaderboardEntry Entry)>
        {
            (0, new LeaderboardEntry { AccountId = "dup_acct", Score = 50000, Season = 1, Source = "scrape" }),
            (1, new LeaderboardEntry { AccountId = "dup_acct", Score = 49000, Season = 1, Source = "scrape" }),
        };

        Assert.ThrowsAny<Exception>(() =>
            _metaFixture.Db.StageChunk(scrapeId, "song1", TestInstrument, entries));
    }

    [Fact]
    public void StageChunk_DedupedEntries_Succeeds()
    {
        // After deduplication (keeping highest score per account), staging should succeed.
        var scrapeId = _metaFixture.Db.StartScrapeRun();
        var rawEntries = new List<LeaderboardEntry>
        {
            new() { AccountId = "dup_acct", Score = 50000, Season = 1, Source = "scrape" },
            new() { AccountId = "dup_acct", Score = 49000, Season = 1, Source = "scrape" },
            new() { AccountId = "other", Score = 48000, Season = 1, Source = "scrape" },
        };

        var deduped = rawEntries
            .GroupBy(e => e.AccountId, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderByDescending(e => e.Score).First())
            .Select((e, i) => (PageNum: i / 100, Entry: e))
            .ToList();

        _metaFixture.Db.StageChunk(scrapeId, "song1", TestInstrument, deduped);

        var count = _metaFixture.Db.GetStagedEntryCount(scrapeId, "song1", TestInstrument);
        Assert.Equal(2, count);
    }

    private static List<(int PageNum, LeaderboardEntry Entry)> MakeEntries(
        int page, int count, int startIndex = 0, int baseScore = 50000)
    {
        var entries = new List<(int, LeaderboardEntry)>(count);
        for (int i = 0; i < count; i++)
        {
            entries.Add((page, new LeaderboardEntry
            {
                AccountId = $"acc_{startIndex + i}",
                Score = baseScore - (startIndex + i) * 100,
                Accuracy = 90 + i % 10,
                IsFullCombo = i % 3 == 0,
                Stars = 4 + i % 2,
                Season = 1,
                Difficulty = 3,
                Source = "scrape",
            }));
        }
        return entries;
    }
}
