using System.Text.Json;
using FSTService.Auth;
using FSTService.Persistence;
using Microsoft.Extensions.Configuration;

namespace FSTService.Tests.Unit;

public class ScraperOptionsAndModelsTests
{
    // ─── ScraperOptions defaults ────────────────────────

    [Fact]
    public void ScraperOptions_DefaultValues()
    {
        var opts = new ScraperOptions();

        Assert.Equal(TimeSpan.FromHours(4), opts.ScrapeInterval);
        Assert.Equal(16, opts.DegreeOfParallelism);
        Assert.Equal(1, opts.RankHistorySnapshotMaxDegreeOfParallelism);
        Assert.Equal(4, opts.LeaderboardRivalsMaxDegreeOfParallelism);
        Assert.Equal(
            600,
            opts.MaxScoreMaintenanceCommandTimeoutSeconds);
        Assert.True(opts.QueryLead);
        Assert.True(opts.QueryDrums);
        Assert.True(opts.QueryVocals);
        Assert.True(opts.QueryBass);
        Assert.True(opts.QueryProLead);
        Assert.True(opts.QueryProBass);
        Assert.Equal("data", opts.DataDirectory);
        Assert.Equal("data/device-auth.json", opts.DeviceAuthPath);
        Assert.False(opts.ApiOnly);
        Assert.False(opts.SkipStartupSchemaInitialization);
        Assert.False(opts.RolloutReadOnlyStartup);
        Assert.False(opts.RolloutPostgresReadOnly);
        Assert.False(opts.DisableScraperWorker);
        Assert.False(opts.RegistrationSyncWorkerOnly);
        Assert.False(opts.SetupOnly);
        Assert.False(opts.RunOnce);
        Assert.False(opts.ResolveOnly);
        Assert.False(
            opts.BandCurrentProjectionUseBatchedMemberStatsAggregation);
        Assert.True(opts.EnablePathGeneration);
        Assert.False(opts.EnableAutomaticPathGeneration);
        Assert.Equal(4, opts.PathGenerationParallelism);
        Assert.Equal(
            "chopt-fnf-ew0-s20-json-png-prodrums-v4",
            opts.PathGenerationProfile);
        Assert.Null(opts.TestSongQuery);
        Assert.Equal(TimeSpan.Zero, opts.RegisteredUserRefreshTimeout);
        Assert.Equal(RegistrationBackfillMode.BackgroundLowPriority, opts.RegistrationBackfillMode);
    }

    [Fact]
    public void ScraperOptions_Section_Constant()
    {
        Assert.Equal("Scraper", ScraperOptions.Section);
    }

    [Fact]
    public void DatabaseMaintenanceOptions_DefaultRetentionPlannerIsOffAndReportOnly()
    {
        var options = new DatabaseMaintenanceOptions();

        Assert.False(
            options.SnapshotGenerationRetentionPlannerEnabled);
        Assert.True(
            options.SnapshotGenerationRetentionReportOnly);
        Assert.Equal(
            2,
            options
                .SnapshotGenerationRetentionNewestGenerationsToKeep);
        Assert.Equal(
            2,
            options
                .SnapshotGenerationRetentionMinimumLaterSuccessfulPublications);
        Assert.Equal(
            1,
            options
                .SnapshotGenerationRetentionMaxPlannedChildrenPerCycle);
        Assert.True(
            options
                .SnapshotGenerationRetentionBlockUnreplayedWriterFailures);
    }

    [Fact]
    public void TrackedAppsettingsKeepsRetentionPlannerOffAndReportOnly()
    {
        using var document = JsonDocument.Parse(
            File.ReadAllText(
                Path.Combine(
                    AppContext.BaseDirectory,
                    "appsettings.json")));
        var maintenance = document.RootElement
            .GetProperty(DatabaseMaintenanceOptions.Section);

        Assert.False(
            maintenance
                .GetProperty(
                    "SnapshotGenerationRetentionPlannerEnabled")
                .GetBoolean());
        Assert.True(
            maintenance
                .GetProperty(
                    "SnapshotGenerationRetentionReportOnly")
                .GetBoolean());
        Assert.Equal(
            2,
            maintenance
                .GetProperty(
                    "SnapshotGenerationRetentionNewestGenerationsToKeep")
                .GetInt32());
        Assert.Equal(
            2,
            maintenance
                .GetProperty(
                    "SnapshotGenerationRetentionMinimumLaterSuccessfulPublications")
                .GetInt32());
        Assert.Equal(
            1,
            maintenance
                .GetProperty(
                    "SnapshotGenerationRetentionMaxPlannedChildrenPerCycle")
                .GetInt32());
        Assert.True(
            maintenance
                .GetProperty(
                    "SnapshotGenerationRetentionBlockUnreplayedWriterFailures")
                .GetBoolean());
    }

    [Fact]
    public void TrackedAppsettingsKeepsCurrentProjectionCandidateDisabled()
    {
        using var document = JsonDocument.Parse(
            File.ReadAllText(
                Path.Combine(
                    AppContext.BaseDirectory,
                    "appsettings.json")));

        Assert.False(
            document.RootElement
                .GetProperty(ScraperOptions.Section)
                .GetProperty(
                    "BandCurrentProjectionUseBatchedMemberStatsAggregation")
                .GetBoolean());
    }

    [Fact]
    public void ScraperOptions_CanSetProperties()
    {
        var opts = new ScraperOptions
        {
            ScrapeInterval = TimeSpan.FromMinutes(30),
            DegreeOfParallelism = 8,
            QueryLead = false,
            QueryBass = false,
            ApiOnly = true,
            RolloutReadOnlyStartup = true,
            RolloutPostgresReadOnly = true,
            DisableScraperWorker = true,
            RegistrationSyncWorkerOnly = true,
            RegistrationBackfillMode = RegistrationBackfillMode.ForegroundEpicExclusive,
            BandCurrentProjectionUseBatchedMemberStatsAggregation = true,
            TestSongQuery = "Test Song",
        };

        Assert.Equal(TimeSpan.FromMinutes(30), opts.ScrapeInterval);
        Assert.Equal(8, opts.DegreeOfParallelism);
        Assert.False(opts.QueryLead);
        Assert.False(opts.QueryBass);
        Assert.True(opts.ApiOnly);
        Assert.True(opts.RolloutReadOnlyStartup);
        Assert.True(opts.RolloutPostgresReadOnly);
        Assert.True(opts.DisableScraperWorker);
        Assert.True(opts.RegistrationSyncWorkerOnly);
        Assert.True(
            opts.BandCurrentProjectionUseBatchedMemberStatsAggregation);
        Assert.Equal(RegistrationBackfillMode.ForegroundEpicExclusive, opts.RegistrationBackfillMode);
        Assert.Equal("Test Song", opts.TestSongQuery);
    }

    [Fact]
    public void ScraperOptions_BindsProductionMaxScoreMaintenanceTimeoutOverride()
    {
        var prefix =
            $"FST_MAX_SCORE_TIMEOUT_{Guid.NewGuid():N}_";
        var variable =
            prefix
            + "Scraper__MaxScoreMaintenanceCommandTimeoutSeconds";
        try
        {
            Environment.SetEnvironmentVariable(
                variable,
                "1800");
            var configuration =
                new ConfigurationBuilder()
                    .AddEnvironmentVariables(prefix)
                    .Build();
            var options = new ScraperOptions();

            configuration
                .GetSection(ScraperOptions.Section)
                .Bind(options);

            Assert.Equal(
                1800,
                options
                    .MaxScoreMaintenanceCommandTimeoutSeconds);
            Assert.False(
                new ScraperOptionsValidator()
                    .Validate(null, options)
                    .Failed);
        }
        finally
        {
            Environment.SetEnvironmentVariable(
                variable,
                null);
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(600)]
    [InlineData(1800)]
    [InlineData(86_400)]
    public void ScraperOptionsValidator_AcceptsBoundedMaxScoreMaintenanceTimeout(
        int value)
    {
        var result = new ScraperOptionsValidator().Validate(
            null,
            new ScraperOptions
            {
                MaxScoreMaintenanceCommandTimeoutSeconds =
                    value,
            });

        Assert.False(result.Failed);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(86_401)]
    public void ScraperOptionsValidator_RejectsInvalidMaxScoreMaintenanceTimeout(
        int value)
    {
        var result = new ScraperOptionsValidator().Validate(
            null,
            new ScraperOptions
            {
                MaxScoreMaintenanceCommandTimeoutSeconds =
                    value,
            });

        Assert.True(result.Failed);
        Assert.Contains(
            "must be between 1 and 86400 seconds",
            result.FailureMessage,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ScraperOptions_DefaultsKeepPublicationPathArtifactsOff()
    {
        var options = new ScraperOptions();

        Assert.False(options.UsePublicationPathArtifacts);
        Assert.False(options.EnableAutomaticPathGeneration);
        Assert.False(
            new ScraperOptionsValidator()
                .Validate(null, options)
                .Failed);
    }

    [Fact]
    public void ScraperOptionsValidator_RejectsLegacyAutomaticGeneration()
    {
        var result = new ScraperOptionsValidator().Validate(
            null,
            new ScraperOptions
            {
                EnableAutomaticPathGeneration = true,
                UsePublicationPathArtifacts = false,
            });

        Assert.True(result.Failed);
        Assert.Contains(
            "not supported",
            result.FailureMessage,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ScraperOptionsValidator_RejectsLegacyAutomaticGenerationWithPublicationArtifacts()
    {
        var result = new ScraperOptionsValidator().Validate(
            null,
            new ScraperOptions
            {
                EnableAutomaticPathGeneration = true,
                UsePublicationPathArtifacts = true,
            });

        Assert.True(result.Failed);
        Assert.Contains(
            "scrape-pass staging",
            result.FailureMessage,
            StringComparison.Ordinal);
    }

    [Fact]
    public void BandRankHistoryOptions_DefaultsKeepV2ShadowWritesDisabled()
    {
        var opts = new BandRankHistoryOptions();

        Assert.Equal(BandRankHistoryMode.Inline, opts.Mode);
        Assert.Equal(BandRankHistoryWriteMode.Legacy, opts.WriteMode);
        Assert.True(opts.UseLatestState);
        Assert.True(opts.UseNarrowHistory);
        Assert.True(opts.UseWideHistoryCompatibilityWrite);
        Assert.Equal(BandRankHistoryApiReadSource.NarrowWithWideFallback, opts.ApiReadSource);
        Assert.False(opts.CompactV3DuetsReadEnabled);
        Assert.False(opts.CompactV3TriosReadEnabled);
        Assert.False(opts.CompactV3QuadReadEnabled);
        Assert.Equal(250_000, opts.ChunkSize);
        Assert.True(opts.RangeChunkingEnabled);
    }

    [Fact]
    public void BandTeamRankingRebuildOptions_DefaultsUseComboBatchedWrites()
    {
        var opts = new BandTeamRankingRebuildOptions();

        Assert.Equal(BandTeamRankingWriteMode.ComboBatched, opts.WriteMode);
        Assert.Equal(0, opts.CommandTimeoutSeconds);
        Assert.True(opts.DisableSynchronousCommit);
        Assert.Equal(1, opts.MaxParallelBandTypes);
    }

    // ─── FeatureOptions defaults ──────────────────────

    [Fact]
    public void FeatureOptions_DefaultValues()
    {
        var opts = new FeatureOptions();

        Assert.False(opts.AppManual);
        Assert.True(opts.WriteLegacyLiveLeaderboardDuringScrape);
        Assert.True(opts.WriteLegacyLiveLeaderboardSupplementalRows);
        Assert.False(opts.UseSnapshotOverlayWorkerReaders);
        Assert.False(opts.SkipUnchangedPhysicalLeaderboardSnapshots);
        Assert.False(opts.WritePublishedScopeSources);
        Assert.False(opts.UsePublishedScopeSources);
        Assert.False(opts.UseStoredSoloProjectionRanksForFilteredReads);
        Assert.False(opts.EnforceScopeCompletenessManifests);
        Assert.False(opts.RequireSuccessfulScrapeWriters);
        Assert.False(opts.EnforcePublicationCriticalPhases);
        Assert.False(opts.EnablePublicationReadContext);
    }

    [Fact]
    public void FeatureOptions_DoesNotExposeRetiredLogicalLeaderboardShadowConfiguration()
    {
        Assert.Null(typeof(FeatureOptions).GetProperty("WriteLogicalLeaderboardVersions"));
        Assert.Null(typeof(FeatureOptions).GetProperty("UseLogicalLeaderboardVersions"));
        Assert.Null(typeof(FeatureOptions).Assembly.GetType("FSTService.FeatureOptionsValidator"));
    }

    [Fact]
    public void FeatureOptions_DoesNotExposeRetiredScoreObservationWriters()
    {
        Assert.Null(typeof(FeatureOptions).GetProperty("WriteSoloScoreObservations"));
        Assert.Null(typeof(FeatureOptions).GetProperty("WriteBandMemberScoreObservations"));
    }

    [Fact]
    public void OptionTypes_ExposeOnlyActiveConfiguration()
    {
        Assert.Null(typeof(ScraperOptions).GetProperty("ScrapePassTimeoutMinutes"));
        Assert.Null(typeof(ImprovementNotificationOptions).GetProperty("FailScrapeOnError"));
        Assert.All(new[]
            {
                "Shop", "Rivals", "FirstRun", "Leaderboards", "Difficulty",
                "PlayerBands", "ExperimentalRanks", "Compete"
            },
            name => Assert.Null(typeof(FeatureOptions).GetProperty(name)));
        var opts = new ImprovementNotificationOptions();
        Assert.False(opts.Enabled);
        Assert.Equal("registered", opts.Scope);
        Assert.True(opts.IncludePlayers && opts.IncludeBands && opts.IncludeSongEvents
            && opts.IncludeRankings && opts.RefreshSoloProjection);
        Assert.False(opts.RefreshAllSoloScopesWhenNoImpactedScopes);
    }

    [Theory]
    [InlineData("appsettings.json")]
    [InlineData("appsettings.Development.json")]
    public void Appsettings_ExposeOnlyActiveConfiguration(string fileName)
    {
        var contents = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, fileName));

        var retiredKeys = new[] { "WriteLogicalLeaderboardVersions", "UseLogicalLeaderboardVersions",
            "WriteSoloScoreObservations", "WriteBandMemberScoreObservations",
            "ScrapePassTimeoutMinutes", "FailScrapeOnError" };
        Assert.All(retiredKeys,
            key => Assert.DoesNotContain(key, contents, StringComparison.Ordinal));

        using var document = JsonDocument.Parse(contents);
        var features = document.RootElement.GetProperty(FeatureOptions.Section);
        Assert.All(new[] { "Leaderboards", "Difficulty", "PlayerBands", "ExperimentalRanks", "Compete" },
            key => Assert.False(features.TryGetProperty(key, out _), $"{key} is still present in {fileName}"));
        Assert.False(features.GetProperty("AppManual").GetBoolean());
        var notifications = document.RootElement.GetProperty(ImprovementNotificationOptions.Section);
        var activeKeys = new[] { "Enabled", "Scope", "IncludePlayers", "IncludeBands",
            "IncludeSongEvents", "IncludeRankings", "RefreshSoloProjection",
            "RefreshAllSoloScopesWhenNoImpactedScopes" };
        Assert.All(activeKeys,
            key => Assert.True(notifications.TryGetProperty(key, out _), $"{key} is missing from {fileName}"));
    }

    [Fact]
    public void FeatureOptions_Section_Constant()
    {
        Assert.Equal("Features", FeatureOptions.Section);
    }

    // ─── StoredCredentials ──────────────────────────────

    [Fact]
    public void StoredCredentials_RequiredAndDefaults()
    {
        var creds = new StoredCredentials
        {
            AccountId = "abc123",
            RefreshToken = "rt_xyz",
        };

        Assert.Equal("abc123", creds.AccountId);
        Assert.Equal("rt_xyz", creds.RefreshToken);
        Assert.Equal("", creds.DisplayName);
        // SavedAt should be close to now
        Assert.True((DateTimeOffset.UtcNow - creds.SavedAt).TotalSeconds < 5);
    }

    [Fact]
    public void StoredCredentials_AllProperties()
    {
        var savedAt = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var creds = new StoredCredentials
        {
            AccountId = "abc",
            RefreshToken = "rt",
            DisplayName = "Player1",
            SavedAt = savedAt,
        };

        Assert.Equal("Player1", creds.DisplayName);
        Assert.Equal(savedAt, creds.SavedAt);
    }

    // ─── DeviceAuthorizationResponse ────────────────────

    [Fact]
    public void DeviceAuthorizationResponse_Properties()
    {
        var resp = new DeviceAuthorizationResponse
        {
            UserCode = "ABC123",
            DeviceCode = "device_xyz",
            VerificationUri = "https://example.com/activate",
            VerificationUriComplete = "https://example.com/activate?code=ABC123",
            ExpiresIn = 600,
            Interval = 5,
        };

        Assert.Equal("ABC123", resp.UserCode);
        Assert.Equal("device_xyz", resp.DeviceCode);
        Assert.Equal("https://example.com/activate", resp.VerificationUri);
        Assert.Equal("https://example.com/activate?code=ABC123", resp.VerificationUriComplete);
        Assert.Equal(600, resp.ExpiresIn);
        Assert.Equal(5, resp.Interval);
    }

    // ─── EpicTokenResponse ──────────────────────────────

    [Fact]
    public void EpicTokenResponse_DefaultValues()
    {
        var token = new EpicTokenResponse();

        Assert.Equal("", token.AccessToken);
        Assert.Equal(0, token.ExpiresIn);
        Assert.Equal("", token.TokenType);
        Assert.Equal("", token.RefreshToken);
        Assert.Equal(0, token.RefreshExpires);
        Assert.Equal("", token.AccountId);
        Assert.Equal("", token.ClientId);
        Assert.Equal("", token.DisplayName);
    }

    [Fact]
    public void EpicTokenResponse_CanSetAllProperties()
    {
        var now = DateTimeOffset.UtcNow;
        var token = new EpicTokenResponse
        {
            AccessToken = "at_123",
            ExpiresIn = 7200,
            ExpiresAt = now.AddHours(2),
            TokenType = "bearer",
            RefreshToken = "rt_456",
            RefreshExpires = 28800,
            RefreshExpiresAt = now.AddHours(8),
            AccountId = "acct_789",
            ClientId = "client_abc",
            DisplayName = "TestUser",
        };

        Assert.Equal("at_123", token.AccessToken);
        Assert.Equal(7200, token.ExpiresIn);
        Assert.Equal("bearer", token.TokenType);
        Assert.Equal("rt_456", token.RefreshToken);
        Assert.Equal(28800, token.RefreshExpires);
        Assert.Equal("acct_789", token.AccountId);
        Assert.Equal("client_abc", token.ClientId);
        Assert.Equal("TestUser", token.DisplayName);
    }

    // ─── ApiSettings ────────────────────────────────────

    [Fact]
    public void ApiSettings_DefaultValues()
    {
        var api = new FSTService.Api.ApiSettings();

        Assert.Equal("", api.ApiKey);
        Assert.Single(api.AllowedOrigins);
        Assert.Contains("http://localhost:3000", api.AllowedOrigins);
    }

    [Fact]
    public void ApiSettings_Section_Constant()
    {
        Assert.Equal("Api", FSTService.Api.ApiSettings.Section);
    }

    // ─── ApiKeyAuthOptions ──────────────────────────────

    [Fact]
    public void ApiKeyAuthOptions_DefaultApiKey()
    {
        var opts = new FSTService.Api.ApiKeyAuthOptions();
        Assert.Equal("", opts.ApiKey);
    }

    // ─── LeaderboardEntry ───────────────────────────────

    [Fact]
    public void LeaderboardEntry_DefaultValues()
    {
        var entry = new FSTService.Scraping.LeaderboardEntry();
        Assert.Equal("", entry.AccountId);
        Assert.Equal(0, entry.Rank);
        Assert.Equal(0.0, entry.Percentile);
        Assert.Equal(0, entry.Score);
        Assert.Equal(0, entry.Accuracy);
        Assert.False(entry.IsFullCombo);
        Assert.Equal(0, entry.Stars);
        Assert.Equal(0, entry.Season);
        Assert.Null(entry.EndTime);
    }

    // ─── GlobalLeaderboardResult ────────────────────────

    [Fact]
    public void GlobalLeaderboardResult_DefaultValues()
    {
        var result = new FSTService.Scraping.GlobalLeaderboardResult();
        Assert.Equal("", result.SongId);
        Assert.Equal("", result.Instrument);
        Assert.Empty(result.Entries);
        Assert.Equal(0, result.TotalPages);
        Assert.Equal(0, result.PagesScraped);
        Assert.Equal(0, result.Requests);
        Assert.Equal(0L, result.BytesReceived);
    }

    [Fact]
    public void LeaderboardEntryDto_DisplayName_CanBeSet()
    {
        var dto = new FSTService.Persistence.LeaderboardEntryDto
        {
            AccountId = "acct1",
            DisplayName = "PlayerOne",
            Score = 100000,
        };
        Assert.Equal("PlayerOne", dto.DisplayName);
        Assert.Equal("acct1", dto.AccountId);
        Assert.Equal(100000, dto.Score);
    }
}
