using System.Reflection;
using System.Text.Json;
using FSTService.Persistence;
using FSTService.Scraping;
using NSubstitute;

namespace FSTService.Tests.Unit;

/// <summary>
/// Tests for <see cref="ScraperWorker"/> — focuses on static/internal helpers
/// and mode-switching logic that can be tested without full HTTP orchestration.
/// </summary>
public class ScraperWorkerTests : IDisposable
{
    private readonly string _tempDir;

    public ScraperWorkerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"fst_worker_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, true); } catch { }
    }

    // Use reflection to invoke private static methods
    private static IReadOnlyList<string> InvokeGetEnabledInstruments(ScraperOptions opts)
    {
        var method = typeof(ScraperWorker).GetMethod(
            "GetEnabledInstruments",
            BindingFlags.NonPublic | BindingFlags.Static)!;
        return (IReadOnlyList<string>)method.Invoke(null, [opts])!;
    }

    private static int InvokeLoadCachedPageEstimate(ScraperOptions opts)
    {
        var method = typeof(ScrapeOrchestrator).GetMethod(
            "LoadCachedPageEstimate",
            BindingFlags.NonPublic | BindingFlags.Static)!;
        return (int)method.Invoke(null, [opts])!;
    }

    private static void InvokeSaveCachedPageEstimate(ScraperOptions opts, int totalPages)
    {
        var method = typeof(ScrapeOrchestrator).GetMethod(
            "SaveCachedPageEstimate",
            BindingFlags.NonPublic | BindingFlags.Static)!;
        method.Invoke(null, [opts, totalPages]);
    }

    private static ScraperOptions CreateValidResumeOptions() =>
        new()
        {
            RunOnce = true,
            ResumeScrapeId = 1263,
            ResumeSongsScraped = -1,
            ResumeTotalEntries = -1,
            ResumeTotalRequests = -1,
            ResumeTotalBytes = -1,
            ResumeEpicReportedOver100Pages = true,
        };

    private static ScrapeResumeState CreateValidResumeState()
    {
        var startedAt = DateTime.UtcNow.AddHours(-12);
        return new ScrapeResumeState(
            1263,
            startedAt,
            "running",
            1236,
            8208,
            8208,
            0,
            0,
            [])
        {
            AcquisitionCompletedAtUtc = startedAt.AddHours(4),
            SongsScraped = 684,
            TotalEntries = 39_696_674,
            TotalRequests = 398_376,
            TotalBytes = 57_563_653_024,
            EpicReportedOver100Pages = false,
            PublicationSongCount = 700,
            PublicationSongCatalogIsExact = true,
            ExpectedSoloScopeCount = 6_300,
            ExpectedSoloScopeFingerprintVersion =
                SoloAcquisitionScopeFingerprint.Version,
            ExpectedSoloScopeFingerprint = new string('a', 64),
            ActualCompleteSoloScopeCount = 6_300,
            ActualCompleteSoloScopeFingerprint = new string('a', 64),
            ActualCompleteSoloScopeOwnedByCatalog = true,
        };
    }

    // ─── GetEnabledInstruments ──────────────────────────────────

    [Fact]
    public void GetEnabledInstruments_AllEnabled_ReturnsAllSoloTypes()
    {
        var opts = new ScraperOptions(); // All default to true
        var result = InvokeGetEnabledInstruments(opts);
        Assert.Equal(
        [
            "Solo_Guitar",
            "Solo_Bass",
            "Solo_Vocals",
            "Solo_Drums",
            "Solo_PeripheralGuitar",
            "Solo_PeripheralBass",
            "Solo_PeripheralVocals",
            "Solo_PeripheralCymbals",
            "Solo_PeripheralDrums",
        ], result);
    }

    [Fact]
    public void GetEnabledInstruments_NoneEnabled_ReturnsEmpty()
    {
        var opts = new ScraperOptions
        {
            QueryLead = false,
            QueryBass = false,
            QueryVocals = false,
            QueryDrums = false,
            QueryProLead = false,
            QueryProBass = false,
            QueryProVocals = false,
            QueryProCymbals = false,
            QueryProDrums = false,
        };
        var result = InvokeGetEnabledInstruments(opts);
        Assert.Empty(result);
    }

    [Fact]
    public void GetEnabledInstruments_Partial_ReturnsSubset()
    {
        var opts = new ScraperOptions
        {
            QueryLead = true,
            QueryDrums = true,
            QueryBass = false,
            QueryVocals = false,
            QueryProLead = false,
            QueryProBass = false,
            QueryProVocals = false,
            QueryProCymbals = false,
            QueryProDrums = false,
        };
        var result = InvokeGetEnabledInstruments(opts);
        Assert.Equal(2, result.Count);
        Assert.Contains("Solo_Guitar", result);
        Assert.Contains("Solo_Drums", result);
    }

    // ─── Page estimate persistence ──────────────────────────────

    [Fact]
    public void LoadCachedPageEstimate_NoFile_Returns0()
    {
        var opts = new ScraperOptions { DataDirectory = _tempDir };
        Assert.Equal(0, InvokeLoadCachedPageEstimate(opts));
    }

    [Fact]
    public void SaveAndLoad_PageEstimate_RoundTrips()
    {
        var opts = new ScraperOptions { DataDirectory = _tempDir };

        InvokeSaveCachedPageEstimate(opts, 42);

        var loaded = InvokeLoadCachedPageEstimate(opts);
        Assert.Equal(42, loaded);
    }

    [Fact]
    public void SaveCachedPageEstimate_OverwritesPrevious()
    {
        var opts = new ScraperOptions { DataDirectory = _tempDir };

        InvokeSaveCachedPageEstimate(opts, 100);
        InvokeSaveCachedPageEstimate(opts, 200);

        Assert.Equal(200, InvokeLoadCachedPageEstimate(opts));
    }

    [Fact]
    public void LoadCachedPageEstimate_MalformedJson_Returns0()
    {
        var opts = new ScraperOptions { DataDirectory = _tempDir };
        var path = Path.Combine(_tempDir, "page-estimate.json");
        File.WriteAllText(path, "not json at all");

        Assert.Equal(0, InvokeLoadCachedPageEstimate(opts));
    }

    [Fact]
    public void LoadCachedPageEstimate_MissingProperty_Returns0()
    {
        var opts = new ScraperOptions { DataDirectory = _tempDir };
        var path = Path.Combine(_tempDir, "page-estimate.json");
        File.WriteAllText(path, """{"something":"else"}""");

        Assert.Equal(0, InvokeLoadCachedPageEstimate(opts));
    }

    [Fact]
    public void SaveCachedPageEstimate_WritesValidJson()
    {
        var opts = new ScraperOptions { DataDirectory = _tempDir };
        InvokeSaveCachedPageEstimate(opts, 999);

        var path = Path.Combine(_tempDir, "page-estimate.json");
        Assert.True(File.Exists(path));

        var json = File.ReadAllText(path);
        var doc = JsonDocument.Parse(json);
        Assert.Equal(999, doc.RootElement.GetProperty("totalPages").GetInt32());
        Assert.True(doc.RootElement.TryGetProperty("savedAt", out _));
    }

    // ─── ScraperOptions defaults ────────────────────────────────

    [Fact]
    public void ScraperOptions_Defaults_Reasonable()
    {
        var opts = new ScraperOptions();
        Assert.Equal(TimeSpan.FromHours(4), opts.ScrapeInterval);
        Assert.Equal(16, opts.DegreeOfParallelism);
        Assert.True(opts.QueryLead);
        Assert.True(opts.QueryDrums);
        Assert.True(opts.QueryVocals);
        Assert.True(opts.QueryBass);
        Assert.True(opts.QueryProLead);
        Assert.True(opts.QueryProBass);
        Assert.False(opts.ApiOnly);
        Assert.False(opts.SetupOnly);
        Assert.False(opts.RunOnce);
        Assert.Equal(0, opts.ResumeScrapeId);
        Assert.Equal(2, opts.RivalsMaxDegreeOfParallelism);
        Assert.False(opts.ResolveOnly);
        Assert.Null(opts.TestSongQuery);
    }

    [Fact]
    public void ValidateResumeScrape_AcceptsCompleteRunningCandidate()
    {
        var options = CreateValidResumeOptions();
        var state = CreateValidResumeState();

        ScraperWorker.ValidateResumeScrape(
            options,
            ScrapePhase.SoloRankings
            | ScrapePhase.SoloRivals
            | ScrapePhase.SoloPlayerStats
            | ScrapePhase.SoloPrecompute
            | ScrapePhase.SoloFinalize,
            state);
    }

    [Theory]
    [InlineData(nameof(ScraperOptions.QueryLead), "Solo_Guitar")]
    [InlineData(nameof(ScraperOptions.QueryBass), "Solo_Bass")]
    [InlineData(nameof(ScraperOptions.QueryVocals), "Solo_Vocals")]
    [InlineData(nameof(ScraperOptions.QueryDrums), "Solo_Drums")]
    [InlineData(nameof(ScraperOptions.QueryProLead), "Solo_PeripheralGuitar")]
    [InlineData(nameof(ScraperOptions.QueryProBass), "Solo_PeripheralBass")]
    [InlineData(nameof(ScraperOptions.QueryProVocals), "Solo_PeripheralVocals")]
    [InlineData(nameof(ScraperOptions.QueryProCymbals), "Solo_PeripheralCymbals")]
    [InlineData(nameof(ScraperOptions.QueryProDrums), "Solo_PeripheralDrums")]
    public void ValidateResumeScrape_RejectsMissingCanonicalSoloScopeFlag(
        string optionPropertyName,
        string missingInstrument)
    {
        var options = CreateValidResumeOptions();
        typeof(ScraperOptions)
            .GetProperty(optionPropertyName)!
            .SetValue(options, false);

        var error = Assert.Throws<InvalidOperationException>(() =>
            ScraperWorker.ValidateResumeScrape(
                options,
                ScrapePhase.SoloRankings
                | ScrapePhase.SoloRivals
                | ScrapePhase.SoloPlayerStats
                | ScrapePhase.SoloPrecompute
                | ScrapePhase.SoloFinalize,
                CreateValidResumeState()));

        Assert.Contains(
            "requires every canonical solo query flag enabled",
            error.Message);
        Assert.Contains(
            $"missing={missingInstrument}",
            error.Message);
    }

    [Fact]
    public void ValidateResumeScrape_RejectsIncompleteCandidate()
    {
        var options = new ScraperOptions
        {
            RunOnce = true,
            ResumeScrapeId = 1263,
        };
        var startedAt = DateTime.UtcNow.AddHours(-12);
        var state = new ScrapeResumeState(
            1263,
            startedAt,
            "running",
            1236,
            8208,
            8207,
            0,
            0,
            [])
        {
            AcquisitionCompletedAtUtc = startedAt.AddHours(4),
            SongsScraped = 684,
            TotalEntries = 39_696_674,
            TotalRequests = 398_376,
            TotalBytes = 57_563_653_024,
            EpicReportedOver100Pages = false,
            PublicationSongCount = 700,
        };

        var error = Assert.Throws<InvalidOperationException>(() =>
            ScraperWorker.ValidateResumeScrape(
                options,
                ScrapePhase.SoloRankings
                | ScrapePhase.SoloRivals
                | ScrapePhase.SoloPlayerStats
                | ScrapePhase.SoloPrecompute
                | ScrapePhase.SoloFinalize,
                state));

        Assert.Contains("manifests=8207/8208", error.Message);
    }

    [Fact]
    public void ValidateResumeScrape_RejectsMissingAcquisitionMetrics()
    {
        var options = new ScraperOptions
        {
            RunOnce = true,
            ResumeScrapeId = 1263,
        };
        var state = new ScrapeResumeState(
            1263,
            DateTime.UtcNow.AddHours(-12),
            "running",
            1236,
            8208,
            8208,
            0,
            0,
            []);

        var error = Assert.Throws<InvalidOperationException>(() =>
            ScraperWorker.ValidateResumeScrape(
                options,
                ScrapePhase.SoloRankings
                | ScrapePhase.SoloRivals
                | ScrapePhase.SoloPlayerStats
                | ScrapePhase.SoloPrecompute
                | ScrapePhase.SoloFinalize,
                state));

        Assert.Contains(
            "acquisition checkpoint is missing",
            error.Message);
    }

    [Fact]
    public void ValidateResumeScrape_RejectsPartialOrInvalidAcquisitionMetrics()
    {
        var options = new ScraperOptions
        {
            RunOnce = true,
            ResumeScrapeId = 1263,
        };
        var startedAt = DateTime.UtcNow.AddHours(-12);
        var state = new ScrapeResumeState(
            1263,
            startedAt,
            "running",
            1236,
            8208,
            8208,
            0,
            0,
            [])
        {
            AcquisitionCompletedAtUtc = startedAt.AddHours(4),
            SongsScraped = 684,
            TotalEntries = 39_696_674,
            TotalRequests = null,
            TotalBytes = 57_563_653_024,
            EpicReportedOver100Pages = false,
            PublicationSongCount = 700,
        };

        var error = Assert.Throws<InvalidOperationException>(() =>
            ScraperWorker.ValidateResumeScrape(
                options,
                ScrapePhase.SoloRankings
                | ScrapePhase.SoloRivals
                | ScrapePhase.SoloPlayerStats
                | ScrapePhase.SoloPrecompute
                | ScrapePhase.SoloFinalize,
                state));

        Assert.Contains(
            "acquisition metrics are incomplete",
            error.Message);
    }

    [Fact]
    public void ValidateResumeScrape_RejectsNegativeAcquisitionMetrics()
    {
        var options = new ScraperOptions
        {
            RunOnce = true,
            ResumeScrapeId = 1263,
        };
        var startedAt = DateTime.UtcNow.AddHours(-12);
        var state = new ScrapeResumeState(
            1263,
            startedAt,
            "running",
            1236,
            8208,
            8208,
            0,
            0,
            [])
        {
            AcquisitionCompletedAtUtc = startedAt.AddHours(4),
            SongsScraped = -1,
            TotalEntries = 39_696_674,
            TotalRequests = 398_376,
            TotalBytes = 57_563_653_024,
            EpicReportedOver100Pages = false,
            PublicationSongCount = 700,
        };

        var error = Assert.Throws<InvalidOperationException>(() =>
            ScraperWorker.ValidateResumeScrape(
                options,
                ScrapePhase.SoloRankings
                | ScrapePhase.SoloRivals
                | ScrapePhase.SoloPlayerStats
                | ScrapePhase.SoloPrecompute
                | ScrapePhase.SoloFinalize,
                state));

        Assert.Contains(
            "negative value",
            error.Message);
    }

    [Fact]
    public void CreateResumeScrapeResult_UsesPersistedMetricsNotLegacyOptions()
    {
        var startedAt = DateTime.UtcNow.AddHours(-12);
        var state = new ScrapeResumeState(
            1263,
            startedAt,
            "running",
            1236,
            8208,
            8208,
            0,
            0,
            [])
        {
            AcquisitionCompletedAtUtc = startedAt.AddHours(4),
            SongsScraped = 684,
            TotalEntries = 39_696_674,
            TotalRequests = 398_376,
            TotalBytes = 57_563_653_024,
            EpicReportedOver100Pages = true,
            PublicationSongCount = 700,
            PublicationSongCatalogIsExact = true,
            ExpectedSoloScopeCount = 6_300,
            ExpectedSoloScopeFingerprintVersion =
                SoloAcquisitionScopeFingerprint.Version,
            ExpectedSoloScopeFingerprint = new string('a', 64),
            ActualCompleteSoloScopeCount = 6_300,
            ActualCompleteSoloScopeFingerprint = new string('a', 64),
            ActualCompleteSoloScopeOwnedByCatalog = true,
        };
        var context = new ScrapePassContext
        {
            ScrapeId = state.ScrapeId,
            AccessToken = "token",
            CallerAccountId = "account",
            RegisteredIds = [],
            Aggregates =
                new Persistence.GlobalLeaderboardPersistence
                    .PipelineAggregates(),
            ScrapeRequests = [],
            PublicationCatalogSongs = [],
            DegreeOfParallelism = 1,
            LeaderboardScrapeCompleted = true,
        };

        var result = ScraperWorker.CreateResumeScrapeResult(
            state,
            context,
            startedAt.AddHours(12));

        Assert.Equal(684, result.SongsScraped);
        Assert.Equal(39_696_674, result.TotalEntries);
        Assert.Equal(398_376, result.TotalRequests);
        Assert.Equal(57_563_653_024, result.TotalBytes);
        Assert.True(result.EpicReportedOver100Pages);
    }

    [Theory]
    [InlineData("completed", PostScrapePhaseCriticality.PublicationCritical, true)]
    [InlineData("skipped", PostScrapePhaseCriticality.PublicationCritical, false)]
    [InlineData("skipped", PostScrapePhaseCriticality.BestEffort, true)]
    [InlineData("failed", PostScrapePhaseCriticality.BestEffort, false)]
    [InlineData("cancelled", PostScrapePhaseCriticality.PublicationCritical, false)]
    public void Resume_phase_status_is_criticality_aware(
        string status,
        PostScrapePhaseCriticality criticality,
        bool expectedSuccess)
    {
        Assert.Equal(
            expectedSuccess,
            ScraperWorker.IsSuccessfulPhaseOutcomeStatus(
                status,
                criticality));
    }

    [Fact]
    public void Resume_phase_outcome_rejects_unknown_criticality()
    {
        var now = DateTime.UtcNow;
        var outcome = new ScrapePhaseOutcomeRecord(
            42,
            "RankRecompute",
            "unknown",
            "completed",
            now,
            now,
            0,
            null);

        var error = Assert.Throws<InvalidOperationException>(() =>
            ScraperWorker.RehydratePhaseOutcome(outcome));

        Assert.Contains("unknown criticality 'unknown'", error.Message);
    }

    [Theory]
    [InlineData(false, 9, true, true, true)]
    [InlineData(true, 0, true, true, true)]
    [InlineData(true, 9, false, true, true)]
    [InlineData(true, 9, true, false, true)]
    [InlineData(true, 9, true, true, false)]
    public void AcquisitionCheckpoint_requires_solo_scope_and_all_gates(
        bool doSoloScrape,
        int expectedSoloScopeCount,
        bool soloCoverageComplete,
        bool bandManifestGatePassed,
        bool writerGatePassed)
    {
        var metaDatabase = Substitute.For<IMetaDatabase>();
        IReadOnlyCollection<(string SongId, string Instrument)>
            expectedPairs = expectedSoloScopeCount == 0
                ? []
                : GlobalLeaderboardScraper.AllInstruments
                    .Select(instrument => ("song-a", instrument))
                    .ToArray();

        Assert.False(
            ScrapeOrchestrator.RecordAcquisitionCheckpointIfEligible(
                metaDatabase,
                scrapeId: 42,
                songsScraped: 1,
                totalEntries: 10,
                totalRequests: 2,
                totalBytes: 100,
                epicReportedOver100Pages: false,
                expectedSoloLeaderboardPairs: expectedPairs,
                publicationCatalogSongIds: ["song-a"],
                doSoloScrape: doSoloScrape,
                soloCoverageComplete: soloCoverageComplete,
                bandManifestGatePassed: bandManifestGatePassed,
                writerGatePassed: writerGatePassed));
        metaDatabase.DidNotReceiveWithAnyArgs()
            .RecordScrapeAcquisitionCheckpoint(
                default,
                default,
                default,
                default,
                default,
                default!,
                default);
    }

    [Fact]
    public void AcquisitionCheckpoint_rejects_reduced_instrument_set()
    {
        var metaDatabase = Substitute.For<IMetaDatabase>();
        IReadOnlyCollection<(string SongId, string Instrument)>
            expectedPairs =
            [("song-a", "Solo_Guitar")];

        Assert.False(
            ScrapeOrchestrator.RecordAcquisitionCheckpointIfEligible(
                metaDatabase,
                scrapeId: 42,
                songsScraped: 1,
                totalEntries: 10,
                totalRequests: 2,
                totalBytes: 100,
                epicReportedOver100Pages: true,
                expectedSoloLeaderboardPairs: expectedPairs,
                publicationCatalogSongIds: ["song-a"],
                doSoloScrape: true,
                soloCoverageComplete: true,
                bandManifestGatePassed: true,
                writerGatePassed: true));
        metaDatabase.DidNotReceiveWithAnyArgs()
            .RecordScrapeAcquisitionCheckpoint(
                default,
                default,
                default,
                default,
                default,
                default!,
                default);
    }

    [Fact]
    public void AcquisitionCheckpoint_is_allowed_for_complete_canonical_product()
    {
        var metaDatabase = Substitute.For<IMetaDatabase>();
        IReadOnlyCollection<(string SongId, string Instrument)>
            expectedPairs =
            GlobalLeaderboardScraper.AllInstruments
                .Select(instrument => ("song-a", instrument))
                .ToArray();

        Assert.True(
            ScrapeOrchestrator.RecordAcquisitionCheckpointIfEligible(
                metaDatabase,
                scrapeId: 42,
                songsScraped: 1,
                totalEntries: 10,
                totalRequests: 2,
                totalBytes: 100,
                epicReportedOver100Pages: true,
                expectedSoloLeaderboardPairs: expectedPairs,
                publicationCatalogSongIds: ["song-a"],
                doSoloScrape: true,
                soloCoverageComplete: true,
                bandManifestGatePassed: true,
                writerGatePassed: true));
        metaDatabase.Received(1)
            .RecordScrapeAcquisitionCheckpoint(
                42,
                1,
                10,
                2,
                100,
                expectedPairs,
                true);
    }
}
