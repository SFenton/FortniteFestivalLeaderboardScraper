using FSTService.Auth;

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
        Assert.True(opts.QueryLead);
        Assert.True(opts.QueryDrums);
        Assert.True(opts.QueryVocals);
        Assert.True(opts.QueryBass);
        Assert.True(opts.QueryProLead);
        Assert.True(opts.QueryProBass);
        Assert.Equal("data", opts.DataDirectory);
        Assert.Equal("data/device-auth.json", opts.DeviceAuthPath);
        Assert.False(opts.ApiOnly);
        Assert.False(opts.SetupOnly);
        Assert.False(opts.RunOnce);
        Assert.False(opts.ResolveOnly);
        Assert.Null(opts.TestSongQuery);
    }

    [Fact]
    public void ScraperOptions_Section_Constant()
    {
        Assert.Equal("Scraper", ScraperOptions.Section);
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
            TestSongQuery = "Test Song",
        };

        Assert.Equal(TimeSpan.FromMinutes(30), opts.ScrapeInterval);
        Assert.Equal(8, opts.DegreeOfParallelism);
        Assert.False(opts.QueryLead);
        Assert.False(opts.QueryBass);
        Assert.True(opts.ApiOnly);
        Assert.Equal("Test Song", opts.TestSongQuery);
    }

    // ─── FeatureOptions defaults ──────────────────────

    [Fact]
    public void FeatureOptions_DefaultValues()
    {
        var opts = new FeatureOptions();

        Assert.False(opts.Shop);
        Assert.False(opts.Rivals);
        Assert.False(opts.Leaderboards);
        Assert.False(opts.Compete);
        Assert.False(opts.FirstRun);
    }

    [Fact]
    public void FeatureOptions_Section_Constant()
    {
        Assert.Equal("Features", FeatureOptions.Section);
    }

    [Fact]
    public void FeatureOptions_Compete_IsDerived_BothOn()
    {
        var opts = new FeatureOptions { Rivals = true, Leaderboards = true };
        Assert.True(opts.Compete);
    }

    [Fact]
    public void FeatureOptions_Compete_IsDerived_RivalsOff()
    {
        var opts = new FeatureOptions { Rivals = false, Leaderboards = true };
        Assert.False(opts.Compete);
    }

    [Fact]
    public void FeatureOptions_Compete_IsDerived_LeaderboardsOff()
    {
        var opts = new FeatureOptions { Rivals = true, Leaderboards = false };
        Assert.False(opts.Compete);
    }

    [Fact]
    public void FeatureOptions_Compete_IsDerived_BothOff()
    {
        var opts = new FeatureOptions { Rivals = false, Leaderboards = false };
        Assert.False(opts.Compete);
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
    public void RegisterRequest_Properties()
    {
        var req = new FSTService.Api.RegisterRequest
        {
            DeviceId = "dev1",
            Username = "TestUser",
        };
        Assert.Equal("dev1", req.DeviceId);
        Assert.Equal("TestUser", req.Username);
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
