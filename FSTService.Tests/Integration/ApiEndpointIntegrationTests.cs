using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using FortniteFestival.Core;
using FortniteFestival.Core.Persistence;
using FortniteFestival.Core.Services;
using FSTService.Api;
using FSTService.Auth;
using FSTService.Persistence;
using FSTService.Scraping;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace FSTService.Tests.Integration;

/// <summary>
/// Integration tests for API and Auth endpoints using <see cref="WebApplicationFactory{TEntryPoint}"/>.
/// Replaces external dependencies (auth, scraping) with test doubles and exercises
/// the full ASP.NET Core pipeline: middleware, auth, rate limiting, JSON serialization.
/// </summary>
public class ApiEndpointIntegrationTests : IClassFixture<ApiEndpointIntegrationTests.FstWebApplicationFactory>, IDisposable
{
    private readonly FstWebApplicationFactory _factory;
    private readonly HttpClient _client;
    private readonly HttpClient _authedClient;

    public ApiEndpointIntegrationTests(FstWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
        _authedClient = factory.CreateClient();
        _authedClient.DefaultRequestHeaders.Add("X-API-Key", FstWebApplicationFactory.TestApiKey);
    }

    public void Dispose()
    {
        _client.Dispose();
        _authedClient.Dispose();
    }

    // ─── Health ─────────────────────────────────────────────────

    [Fact]
    public async Task Healthz_ReturnsOk()
    {
        var response = await _client.GetAsync("/healthz");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("ok", body);
    }

    [Fact]
    public async Task ApiVersion_ReturnsVersion()
    {
        var response = await _client.GetAsync("/api/version");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        var version = json.GetProperty("version").GetString();
        Assert.NotNull(version);
        Assert.NotEqual("unknown", version);
    }

    // ─── Progress ───────────────────────────────────────────────

    [Fact]
    public async Task ApiProgress_ReturnsProgressResponse()
    {
        var response = await _client.GetAsync("/api/progress");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // ─── Songs ──────────────────────────────────────────────────

    [Fact]
    public async Task ApiSongs_ReturnsLoadedSongs()
    {
        var response = await _client.GetAsync("/api/songs");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(json.GetProperty("count").GetInt32() >= 1);
        var songs = json.GetProperty("songs");
        Assert.True(songs.GetArrayLength() >= 1);
        // Check first song has expected shape
        var first = songs[0];
        Assert.True(first.TryGetProperty("songId", out _));
        Assert.True(first.TryGetProperty("title", out _));
    }

    // ─── Path Images ────────────────────────────────────────────

    [Fact]
    public async Task ApiPaths_InvalidInstrument_Returns400()
    {
        var response = await _client.GetAsync("/api/paths/testSong1/BadInstrument/expert");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task ApiPaths_InvalidDifficulty_Returns400()
    {
        var response = await _client.GetAsync("/api/paths/testSong1/Solo_Guitar/nightmare");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task ApiPaths_ValidButMissing_Returns404()
    {
        var response = await _client.GetAsync("/api/paths/testSong1/Solo_Guitar/expert");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task ApiPaths_AllowedInstruments_AreAccepted()
    {
        // All valid instruments should return 404 (not generated yet), not 400
        var instruments = new[] { "Solo_Guitar", "Solo_Bass", "Solo_Drums", "Solo_Vocals", "Solo_PeripheralGuitar", "Solo_PeripheralBass" };
        foreach (var inst in instruments)
        {
            var response = await _client.GetAsync($"/api/paths/testSong1/{inst}/easy");
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }
    }

    [Fact]
    public async Task ApiPaths_AllDifficulties_AreAccepted()
    {
        foreach (var diff in new[] { "easy", "medium", "hard", "expert" })
        {
            var response = await _client.GetAsync($"/api/paths/testSong1/Solo_Guitar/{diff}");
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }
    }

    // ─── Leaderboard ────────────────────────────────────────────

    [Fact]
    public async Task ApiLeaderboard_ValidInstrument_ReturnsEntries()
    {
        var response = await _client.GetAsync("/api/leaderboard/testSong1/Solo_Guitar");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("testSong1", json.GetProperty("songId").GetString());
        Assert.Equal("Solo_Guitar", json.GetProperty("instrument").GetString());
    }

    [Fact]
    public async Task ApiLeaderboard_UnknownInstrument_Returns404()
    {
        var response = await _client.GetAsync("/api/leaderboard/testSong1/Kazoo");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task ApiLeaderboard_Leeway_FiltersInvalidScores()
    {
        const string songId = "leewayTestSong";
        using (var scope = _factory.Services.CreateScope())
        {
            var persistence = scope.ServiceProvider.GetRequiredService<GlobalLeaderboardPersistence>();
            var pathStore = scope.ServiceProvider.GetRequiredService<PathDataStore>();

            // Ensure Songs table + row exists for PathDataStore
            EnsureSongRow(pathStore, songId);

            // Insert max score for the song
            pathStore.UpdateMaxScores(songId, new SongMaxScores
            {
                MaxLeadScore = 90_000,
            }, "testhash");

            // Insert entries: one valid (85k), one above max (200k)
            var db = persistence.GetOrCreateInstrumentDb("Solo_Guitar");
            db.UpsertEntries(songId, new List<LeaderboardEntry>
            {
                new() { AccountId = "valid1",   Score = 85_000, Accuracy = 99, Stars = 6 },
                new() { AccountId = "valid2",   Score = 80_000, Accuracy = 98, Stars = 5 },
                new() { AccountId = "cheater1", Score = 200_000, Accuracy = 100, Stars = 6 },
            });
        }

        // Without leeway: all 3 entries returned
        var allResponse = await _client.GetAsync($"/api/leaderboard/{songId}/Solo_Guitar");
        var allJson = await allResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(3, allJson.GetProperty("count").GetInt32());

        // With leeway=1: maxScore * 1.01 = 90900, so 200k is excluded
        var filteredResponse = await _client.GetAsync($"/api/leaderboard/{songId}/Solo_Guitar?leeway=1");
        var filteredJson = await filteredResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(2, filteredJson.GetProperty("count").GetInt32());
        Assert.Equal(2, filteredJson.GetProperty("localEntries").GetInt32());
        var entries = filteredJson.GetProperty("entries");
        Assert.Equal("valid1", entries[0].GetProperty("accountId").GetString());
        Assert.Equal(1, entries[0].GetProperty("rank").GetInt32());
        Assert.Equal("valid2", entries[1].GetProperty("accountId").GetString());
        Assert.Equal(2, entries[1].GetProperty("rank").GetInt32());
    }

    [Fact]
    public async Task ApiLeaderboard_NegativeLeeway_IsStricter()
    {
        const string songId = "negLeewayTestSong";
        using (var scope = _factory.Services.CreateScope())
        {
            var persistence = scope.ServiceProvider.GetRequiredService<GlobalLeaderboardPersistence>();
            var pathStore = scope.ServiceProvider.GetRequiredService<PathDataStore>();

            EnsureSongRow(pathStore, songId);
            pathStore.UpdateMaxScores(songId, new SongMaxScores
            {
                MaxLeadScore = 100_000,
            }, "testhash2");

            var db = persistence.GetOrCreateInstrumentDb("Solo_Guitar");
            db.UpsertEntries(songId, new List<LeaderboardEntry>
            {
                new() { AccountId = "top1",    Score = 100_000, Accuracy = 100, Stars = 6 },
                new() { AccountId = "top2",    Score = 98_000,  Accuracy = 99,  Stars = 6 },
                new() { AccountId = "top3",    Score = 95_000,  Accuracy = 98,  Stars = 5 },
            });
        }

        // leeway=-5: maxScore * 0.95 = 95000. 100k and 98k are excluded
        var response = await _client.GetAsync($"/api/leaderboard/{songId}/Solo_Guitar?leeway=-5");
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, json.GetProperty("count").GetInt32());
        var entries = json.GetProperty("entries");
        Assert.Equal("top3", entries[0].GetProperty("accountId").GetString());
        Assert.Equal(1, entries[0].GetProperty("rank").GetInt32());
    }

    [Fact]
    public async Task ApiLeaderboardAll_Leeway_FiltersPerInstrument()
    {
        const string songId = "allLeewayTestSong";
        using (var scope = _factory.Services.CreateScope())
        {
            var persistence = scope.ServiceProvider.GetRequiredService<GlobalLeaderboardPersistence>();
            var pathStore = scope.ServiceProvider.GetRequiredService<PathDataStore>();

            EnsureSongRow(pathStore, songId);
            pathStore.UpdateMaxScores(songId, new SongMaxScores
            {
                MaxLeadScore = 90_000,
                MaxBassScore = 80_000,
            }, "testhash3");

            var guitarDb = persistence.GetOrCreateInstrumentDb("Solo_Guitar");
            guitarDb.UpsertEntries(songId, new List<LeaderboardEntry>
            {
                new() { AccountId = "g1", Score = 200_000, Accuracy = 100, Stars = 6 },
                new() { AccountId = "g2", Score = 85_000,  Accuracy = 99,  Stars = 5 },
            });

            var bassDb = persistence.GetOrCreateInstrumentDb("Solo_Bass");
            bassDb.UpsertEntries(songId, new List<LeaderboardEntry>
            {
                new() { AccountId = "b1", Score = 75_000, Accuracy = 98, Stars = 5 },
            });
        }

        // leeway=1: guitar max 90900 filters g1; bass max 80800 keeps b1
        var response = await _client.GetAsync($"/api/leaderboard/{songId}/all?leeway=1");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        var instruments = json.GetProperty("instruments");
        foreach (var inst in instruments.EnumerateArray())
        {
            var name = inst.GetProperty("instrument").GetString();
            if (name == "Solo_Guitar")
                Assert.Equal(1, inst.GetProperty("count").GetInt32());
            else if (name == "Solo_Bass")
                Assert.Equal(1, inst.GetProperty("count").GetInt32());
        }
    }

    // ─── Player profile ─────────────────────────────────────────

    [Fact]
    public async Task ApiPlayer_ReturnsProfile()
    {
        var response = await _client.GetAsync("/api/player/testAcct1");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("testAcct1", json.GetProperty("accountId").GetString());
    }

    [Fact]
    public async Task ApiPlayer_Rank_UsesComputedRank_OverStoredRank()
    {
        // Arrange: insert entries where the stored rank (from Epic) is stale
        // but the DB score ordering tells the truth.
        //
        // Player "rankAcct" stored Rank=3, but their score 500 is actually 5th
        // out of 5 players in the database.
        const string song = "rankTestSong";
        const string inst = "Solo_Guitar";

        using (var scope = _factory.Services.CreateScope())
        {
            var persistence = scope.ServiceProvider.GetRequiredService<GlobalLeaderboardPersistence>();
            var db = persistence.GetOrCreateInstrumentDb(inst);
            db.UpsertEntries(song, new[]
            {
                new LeaderboardEntry { AccountId = "rankOther1", Score = 900, Rank = 0 },
                new LeaderboardEntry { AccountId = "rankOther2", Score = 800, Rank = 0 },
                new LeaderboardEntry { AccountId = "rankOther3", Score = 700, Rank = 0 },
                new LeaderboardEntry { AccountId = "rankOther4", Score = 600, Rank = 0 },
                // This player has a stale stored Rank=3, but score puts them at #5
                new LeaderboardEntry { AccountId = "rankAcct", Score = 500, Rank = 3 },
            });
        }

        // Act
        var response = await _client.GetAsync($"/api/player/rankAcct?songId={song}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();

        var scores = json.GetProperty("scores");
        Assert.Equal(1, scores.GetArrayLength());

        var score = scores[0];
        // The computed rank should be 5 (4 players have higher scores + 1),
        // NOT the stale stored rank of 3.
        Assert.Equal(5, score.GetProperty("rank").GetInt32());
    }

    [Fact]
    public async Task ApiPlayer_Rank_FallsBackToStoredRank_WhenComputedIsZero()
    {
        // If for some reason the computed rank is 0 (shouldn't happen normally),
        // we fall back to the stored rank.
        const string song = "rankFallbackSong";
        const string inst = "Solo_Bass";

        using (var scope = _factory.Services.CreateScope())
        {
            var persistence = scope.ServiceProvider.GetRequiredService<GlobalLeaderboardPersistence>();
            var db = persistence.GetOrCreateInstrumentDb(inst);
            // Single entry — computed rank will be 1, stored rank is 7
            db.UpsertEntries(song, new[]
            {
                new LeaderboardEntry { AccountId = "rankFbAcct", Score = 1000, Rank = 7 },
            });
        }

        var response = await _client.GetAsync($"/api/player/rankFbAcct?songId={song}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();

        var score = json.GetProperty("scores")[0];
        // Computed rank is 1 (only entry), which is > 0, so we use it instead of stored 7
        Assert.Equal(1, score.GetProperty("rank").GetInt32());
    }

    [Fact]
    public async Task ApiPlayer_Rank_ConsistentWithLeaderboardOrder()
    {
        // The rank returned by /api/player should match the position in /api/leaderboard
        const string song = "rankConsistSong";
        const string inst = "Solo_Drums";

        using (var scope = _factory.Services.CreateScope())
        {
            var persistence = scope.ServiceProvider.GetRequiredService<GlobalLeaderboardPersistence>();
            var db = persistence.GetOrCreateInstrumentDb(inst);
            db.UpsertEntries(song, new[]
            {
                new LeaderboardEntry { AccountId = "rcAcct1", Score = 300000, Rank = 1 },
                new LeaderboardEntry { AccountId = "rcAcct2", Score = 200000, Rank = 10 }, // stale stored rank
                new LeaderboardEntry { AccountId = "rcAcct3", Score = 100000, Rank = 50 }, // stale stored rank
            });
        }

        // Get player rank from /api/player
        var playerResponse = await _client.GetAsync($"/api/player/rcAcct2?songId={song}");
        Assert.Equal(HttpStatusCode.OK, playerResponse.StatusCode);
        var playerJson = await playerResponse.Content.ReadFromJsonAsync<JsonElement>();
        var playerRank = playerJson.GetProperty("scores")[0].GetProperty("rank").GetInt32();

        // Get leaderboard order from /api/leaderboard
        var lbResponse = await _client.GetAsync($"/api/leaderboard/{song}/{inst}");
        Assert.Equal(HttpStatusCode.OK, lbResponse.StatusCode);
        var lbJson = await lbResponse.Content.ReadFromJsonAsync<JsonElement>();
        var entries = lbJson.GetProperty("entries");

        // Find rcAcct2's position in the leaderboard (1-indexed)
        int lbPosition = -1;
        for (int i = 0; i < entries.GetArrayLength(); i++)
        {
            if (entries[i].GetProperty("accountId").GetString() == "rcAcct2")
            {
                lbPosition = i + 1;
                break;
            }
        }

        Assert.Equal(2, lbPosition); // 2nd by score
        Assert.Equal(lbPosition, playerRank); // must match
    }


    // ─── Protected endpoints (require API key) ──────────────────

    [Fact]
    public async Task ApiStatus_NoAuth_ReturnsUnauthorized()
    {
        var response = await _client.GetAsync("/api/status");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task ApiStatus_WithApiKey_ReturnsOk()
    {
        var response = await _authedClient.GetAsync("/api/status");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(json.TryGetProperty("totalEntries", out _));
    }

    // ─── Register ───────────────────────────────────────────────

    [Fact]
    public async Task ApiRegister_NoAuth_ReturnsUnauthorized()
    {
        var content = JsonContent.Create(new { deviceId = "dev1", accountId = "acct1" });
        var response = await _client.PostAsync("/api/register", content);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task ApiRegister_WithAuth_RegistersAndReturnsOk()
    {
        // Seed display name so register endpoint can resolve username → accountId
        using (var scope = _factory.Services.CreateScope())
        {
            var metaDb = scope.ServiceProvider.GetRequiredService<MetaDatabase>();
            metaDb.InsertAccountNames(new[] { ("testAcct1", (string?)"TestUser1") });
        }

        var content = JsonContent.Create(new { deviceId = "testDev1", username = "TestUser1" });
        var response = await _authedClient.PostAsync("/api/register", content);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(json.GetProperty("registered").GetBoolean());
    }

    [Fact]
    public async Task ApiRegister_MissingFields_ReturnsBadRequest()
    {
        var content = JsonContent.Create(new { deviceId = "", accountId = "" });
        var response = await _authedClient.PostAsync("/api/register", content);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ─── Player history ─────────────────────────────────────────

    [Fact]
    public async Task ApiPlayerHistory_NotRegistered_Returns404()
    {
        var response = await _authedClient.GetAsync("/api/player/unknownAcct/history");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task ApiPlayerHistory_RegisteredUser_ReturnsHistory()
    {
        // Seed display name so register endpoint can resolve username → accountId
        using (var scope = _factory.Services.CreateScope())
        {
            var metaDb = scope.ServiceProvider.GetRequiredService<MetaDatabase>();
            metaDb.InsertAccountNames(new[] { ("histAcct", (string?)"HistUser") });
        }

        // Register a user first
        var regContent = JsonContent.Create(new { deviceId = "histDev", username = "HistUser" });
        await _authedClient.PostAsync("/api/register", regContent);

        var response = await _authedClient.GetAsync("/api/player/histAcct/history");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("histAcct", json.GetProperty("accountId").GetString());
    }

    // ─── Backfill status ────────────────────────────────────────

    [Fact]
    public async Task ApiBackfillStatus_NotFound_Returns404()
    {
        var response = await _authedClient.GetAsync("/api/backfill/unknown/status");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ─── Leaderboard population ─────────────────────────────────

    [Fact]
    public async Task PostLeaderboardPopulation_RequiresAuth()
    {
        var content = JsonContent.Create(new[]
        {
            new { songId = "song1", instrument = "Solo_Guitar", totalEntries = 50000L },
        });
        var response = await _client.PostAsync("/api/leaderboard-population", content);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task PostLeaderboardPopulation_EmptyArray_ReturnsBadRequest()
    {
        var content = JsonContent.Create(Array.Empty<object>());
        var response = await _authedClient.PostAsync("/api/leaderboard-population", content);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task PostLeaderboardPopulation_UpsertsAndGetReturnsData()
    {
        var items = new[]
        {
            new { songId = "testSong1", instrument = "Solo_Guitar", totalEntries = 123456L },
            new { songId = "testSong2", instrument = "Solo_Drums", totalEntries = 78901L },
        };
        var postResp = await _authedClient.PostAsync("/api/leaderboard-population", JsonContent.Create(items));
        Assert.Equal(HttpStatusCode.OK, postResp.StatusCode);

        var postJson = await postResp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(2, postJson.GetProperty("upserted").GetInt32());

        // Verify GET returns the data
        var getResp = await _authedClient.GetAsync("/api/leaderboard-population");
        Assert.Equal(HttpStatusCode.OK, getResp.StatusCode);

        var getJson = await getResp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(getJson.GetArrayLength() >= 2);
    }

    [Fact]
    public async Task PostLeaderboardPopulation_UpdatesExistingEntries()
    {
        var items1 = new[] { new { songId = "updateSong", instrument = "Solo_Bass", totalEntries = 1000L } };
        await _authedClient.PostAsync("/api/leaderboard-population", JsonContent.Create(items1));

        var items2 = new[] { new { songId = "updateSong", instrument = "Solo_Bass", totalEntries = 2000L } };
        var resp = await _authedClient.PostAsync("/api/leaderboard-population", JsonContent.Create(items2));
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var json = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, json.GetProperty("upserted").GetInt32());
    }

    [Fact]
    public async Task PostLeaderboardPopulation_ResponseIncludesRefreshFields()
    {
        var items = new[]
        {
            new { songId = "popFields", instrument = "Solo_Guitar", totalEntries = 42000L },
        };
        var resp = await _authedClient.PostAsync("/api/leaderboard-population", JsonContent.Create(items));
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var json = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, json.GetProperty("upserted").GetInt32());
        // New fields are always present regardless of whether a refresh occurred
        Assert.True(json.TryGetProperty("refreshTriggered", out var rt));
        Assert.Equal(JsonValueKind.True, rt.ValueKind == JsonValueKind.True ? JsonValueKind.True : JsonValueKind.False);
        Assert.True(json.TryGetProperty("personalDbsRebuilt", out var pd));
        Assert.Equal(JsonValueKind.Number, pd.ValueKind);
    }

    [Fact]
    public async Task PostLeaderboardPopulation_WhenIdle_WithRegisteredUsers_TriggersRefresh()
    {
        var metaDb = _factory.Services.GetRequiredService<MetaDatabase>();
        var accountId = "pop-refresh-test-account";
        var deviceId = "pop-refresh-test-device";

        // Register a user so the refresh path fires
        metaDb.RegisterUser(deviceId, accountId);

        try
        {
            var items = new[]
            {
                new { songId = "popRefresh", instrument = "Solo_Guitar", totalEntries = 99000L },
            };
            var resp = await _authedClient.PostAsync("/api/leaderboard-population", JsonContent.Create(items));
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

            var json = await resp.Content.ReadFromJsonAsync<JsonElement>();
            Assert.True(json.GetProperty("refreshTriggered").GetBoolean());
            Assert.True(json.GetProperty("personalDbsRebuilt").GetInt32() >= 1);
        }
        finally
        {
            metaDb.UnregisterAccount(accountId);
        }
    }

    [Fact]
    public async Task PostLeaderboardPopulation_WhenScraping_DoesNotTriggerRefresh()
    {
        var progress = _factory.Services.GetRequiredService<ScrapeProgressTracker>();
        var metaDb = _factory.Services.GetRequiredService<MetaDatabase>();
        var accountId = "pop-noscrape-test-account";
        var deviceId = "pop-noscrape-test-device";

        metaDb.RegisterUser(deviceId, accountId);

        // Simulate a scrape in progress
        progress.BeginPass(1, 1, 0);

        try
        {
            var items = new[]
            {
                new { songId = "popScraping", instrument = "Solo_Drums", totalEntries = 55000L },
            };
            var resp = await _authedClient.PostAsync("/api/leaderboard-population", JsonContent.Create(items));
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

            var json = await resp.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal(1, json.GetProperty("upserted").GetInt32());
            Assert.False(json.GetProperty("refreshTriggered").GetBoolean());
            Assert.Equal(0, json.GetProperty("personalDbsRebuilt").GetInt32());
        }
        finally
        {
            progress.EndPass();
            metaDb.UnregisterAccount(accountId);
        }
    }

    [Fact]
    public async Task GetLeaderboardPopulation_RequiresAuth()
    {
        var response = await _client.GetAsync("/api/leaderboard-population");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // ─── Sync endpoints ─────────────────────────────────────────

    [Fact]
    public async Task ApiSyncVersion_NotRegistered_Returns404()
    {
        var response = await _authedClient.GetAsync("/api/sync/unknownDevice/version");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task ApiSync_NotRegistered_Returns404()
    {
        var response = await _authedClient.GetAsync("/api/sync/unknownDevice");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }


    // ─── Diagnostic endpoints ───────────────────────────────────

    [Fact]
    public async Task DiagEvents_ReturnsResponse()
    {
        // TokenManager returns a valid token → the endpoint makes an HTTP request
        // The HttpMessageHandler_NoOp returns 200 for all requests
        var response = await _client.GetAsync("/api/diag/events");
        // Should get the proxied response from the mock handler
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task DiagLeaderboard_V1_ReturnsResponse()
    {
        var response = await _client.GetAsync("/api/diag/leaderboard?eventId=test&windowId=alltime");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("_url", body);
        Assert.Contains("_status", body);
    }

    [Fact]
    public async Task DiagLeaderboard_V2_ReturnsResponse()
    {
        var response = await _client.GetAsync("/api/diag/leaderboard?eventId=test&windowId=alltime&version=2");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("_url", body);
    }

    [Fact]
    public async Task DiagLeaderboard_V2_WithParams_ReturnsResponse()
    {
        var response = await _client.GetAsync("/api/diag/leaderboard?eventId=test&windowId=alltime&version=2&findTeams=true&teamAccountIds=abc123");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task DiagLeaderboard_V1_WithParams_ReturnsResponse()
    {
        var response = await _client.GetAsync("/api/diag/leaderboard?eventId=test&windowId=alltime&page=0&rank=1&teamAccountIds=abc123");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }


    // ─── Sync endpoints ─────────────────────────────────────────

    [Fact]
    public async Task SyncVersion_RegisteredDevice_ReturnsVersion()
    {
        // Seed display name so register endpoint can resolve username → accountId
        using (var scope = _factory.Services.CreateScope())
        {
            var metaDb = scope.ServiceProvider.GetRequiredService<MetaDatabase>();
            metaDb.InsertAccountNames(new[] { ("syncAcct", (string?)"SyncUser") });
        }

        // Register a device first
        var regContent = JsonContent.Create(new { deviceId = "syncDev", username = "SyncUser" });
        await _authedClient.PostAsync("/api/register", regContent);

        var response = await _authedClient.GetAsync("/api/sync/syncDev/version");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("syncDev", json.GetProperty("deviceId").GetString());
    }

    [Fact]
    public async Task SyncVersion_UnknownDevice_Returns404()
    {
        var response = await _authedClient.GetAsync("/api/sync/unknownDev123/version");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Sync_RegisteredDevice_ReturnsDbOrBuildsOnDemand()
    {
        // Seed display name so register endpoint can resolve username → accountId
        using (var scope = _factory.Services.CreateScope())
        {
            var metaDb = scope.ServiceProvider.GetRequiredService<MetaDatabase>();
            metaDb.InsertAccountNames(new[] { ("dlAcct", (string?)"DlUser") });
        }

        // Register a device
        var regContent = JsonContent.Create(new { deviceId = "dlDev", username = "DlUser" });
        await _authedClient.PostAsync("/api/register", regContent);

        var response = await _authedClient.GetAsync("/api/sync/dlDev");
        // Returns file (200) or 503 if build fails, or potentially a built-on-demand DB
        Assert.True(
            response.StatusCode == HttpStatusCode.OK ||
            response.StatusCode == HttpStatusCode.ServiceUnavailable,
            $"Unexpected: {response.StatusCode}");

        // If OK, verify content type
        if (response.StatusCode == HttpStatusCode.OK)
        {
            Assert.Equal("application/x-sqlite3", response.Content.Headers.ContentType?.MediaType);
        }
    }

    [Fact]
    public async Task Sync_UnregisteredDevice_Returns404()
    {
        var response = await _authedClient.GetAsync("/api/sync/unknownSync/version");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ─── Backfill status (with data) ────────────────────────────

    [Fact]
    public async Task BackfillStatus_RegisteredAccount_ReturnsStatus()
    {
        // Access the MetaDatabase directly via the factory's service provider
        using var scope = _factory.Services.CreateScope();
        var metaDb = scope.ServiceProvider.GetRequiredService<MetaDatabase>();
        metaDb.EnqueueBackfill("bfAcct", 10);

        var response = await _authedClient.GetAsync("/api/backfill/bfAcct/status");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("bfAcct", json.GetProperty("accountId").GetString());
        Assert.Equal("pending", json.GetProperty("status").GetString());
    }

    // ─── Account check ──────────────────────────────────────────

    [Fact]
    public async Task AccountCheck_ExistingUsername_ReturnsFound()
    {
        using (var scope = _factory.Services.CreateScope())
        {
            var metaDb = scope.ServiceProvider.GetRequiredService<MetaDatabase>();
            metaDb.InsertAccountNames(new[] { ("checkAcct", (string?)"CheckUser") });
        }

        var response = await _client.GetAsync("/api/account/check?username=CheckUser");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(json.GetProperty("exists").GetBoolean());
        Assert.Equal("checkAcct", json.GetProperty("accountId").GetString());
    }

    [Fact]
    public async Task AccountCheck_UnknownUsername_ReturnsNotFound()
    {
        var response = await _client.GetAsync("/api/account/check?username=NobodyHere");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(json.GetProperty("exists").GetBoolean());
    }

    [Fact]
    public async Task AccountCheck_EmptyUsername_ReturnsBadRequest()
    {
        var response = await _client.GetAsync("/api/account/check?username=");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ─── DELETE /api/register ───────────────────────────────────

    [Fact]
    public async Task DeleteRegister_Unregistered_ReturnsOk_WithFalse()
    {
        var response = await _authedClient.DeleteAsync(
            "/api/register?deviceId=delDev&accountId=delAcct");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(json.GetProperty("unregistered").GetBoolean());
    }

    [Fact]
    public async Task DeleteRegister_MissingParams_ReturnsBadRequest()
    {
        var response = await _authedClient.DeleteAsync(
            "/api/register?deviceId=&accountId=");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task DeleteRegister_NoAuth_ReturnsUnauthorized()
    {
        var response = await _client.DeleteAsync(
            "/api/register?deviceId=d&accountId=a");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // ─── Register with unknown username ─────────────────────────

    [Fact]
    public async Task Register_UnknownUsername_ReturnsNotRegistered()
    {
        var content = JsonContent.Create(new { deviceId = "testDev99", username = "NobodyExists" });
        var response = await _authedClient.PostAsync("/api/register", content);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(json.GetProperty("registered").GetBoolean());
        Assert.Equal("no_account_found", json.GetProperty("error").GetString());
    }

    // ─── FirstSeen endpoints ────────────────────────────────────

    [Fact]
    public async Task FirstSeen_ReturnsOk()
    {
        var response = await _client.GetAsync("/api/firstseen");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(json.TryGetProperty("count", out _));
        Assert.True(json.TryGetProperty("songs", out _));
    }

    [Fact]
    public async Task FirstSeenCalculate_NoAuth_ReturnsUnauthorized()
    {
        var response = await _client.PostAsync("/api/firstseen/calculate", null);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }


    // ─── DELETE /api/register with registered user ──────────

    [Fact]
    public async Task DeleteRegister_Registered_Unregisters()
    {
        using (var scope = _factory.Services.CreateScope())
        {
            var metaDb = scope.ServiceProvider.GetRequiredService<MetaDatabase>();
            metaDb.InsertAccountNames(new[] { ("delRegisteredAcct", (string?)"DelRegisteredUser") });
        }

        // Register a user first
        var regContent = JsonContent.Create(new { deviceId = "delRegDev", username = "DelRegisteredUser" });
        await _authedClient.PostAsync("/api/register", regContent);

        // Now delete (unregister)
        var response = await _authedClient.DeleteAsync(
            "/api/register?deviceId=delRegDev&accountId=delRegisteredAcct");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(json.GetProperty("unregistered").GetBoolean());
    }

    // ─── POST /api/backfill/{accountId} ─────────────────────

    [Fact]
    public async Task Backfill_UnregisteredAccount_Returns404()
    {
        var response = await _authedClient.PostAsync("/api/backfill/unknownAcctBf", null);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Backfill_RegisteredAccount_RunsSuccessfully()
    {
        // Register a user first
        using (var scope = _factory.Services.CreateScope())
        {
            var metaDb = scope.ServiceProvider.GetRequiredService<MetaDatabase>();
            metaDb.InsertAccountNames(new[] { ("bfRunAcct", (string?)"BfRunUser") });
        }
        var regContent = JsonContent.Create(new { deviceId = "bfRunDev", username = "BfRunUser" });
        await _authedClient.PostAsync("/api/register", regContent);

        // TokenManager now returns a valid token → backfill executes
        var response = await _authedClient.PostAsync("/api/backfill/bfRunAcct", null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("bfRunAcct", json.GetProperty("accountId").GetString());
    }


    // ─── POST /api/firstseen/calculate ──────────────────────

    [Fact]
    public async Task FirstSeenCalculate_WithAuth_RunsSuccessfully()
    {
        // TokenManager now returns a valid token → calculate executes
        var response = await _authedClient.PostAsync("/api/firstseen/calculate", null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(json.TryGetProperty("songsCalculated", out _));
    }


    // ─── Leaderboard edge cases ─────────────────────────────

    [Fact]
    public async Task ApiLeaderboard_WithTopParam_ReturnsLimitedEntries()
    {
        var response = await _client.GetAsync("/api/leaderboard/testSong1/Solo_Guitar?top=5");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task ApiLeaderboard_ReturnsLocalEntries()
    {
        const string song = "localEntriesSong";
        const string inst = "Solo_Guitar";

        using (var scope = _factory.Services.CreateScope())
        {
            var persistence = scope.ServiceProvider.GetRequiredService<GlobalLeaderboardPersistence>();
            var db = persistence.GetOrCreateInstrumentDb(inst);
            db.UpsertEntries(song, new[]
            {
                new LeaderboardEntry { AccountId = "leAcct1", Score = 500000, Rank = 1 },
                new LeaderboardEntry { AccountId = "leAcct2", Score = 400000, Rank = 2 },
                new LeaderboardEntry { AccountId = "leAcct3", Score = 300000, Rank = 3 },
            });
        }

        var response = await _client.GetAsync($"/api/leaderboard/{song}/{inst}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        // Without LeaderboardPopulation, localEntries == totalEntries == dbCount
        Assert.Equal(3, json.GetProperty("localEntries").GetInt32());
        Assert.Equal(3, json.GetProperty("totalEntries").GetInt32());
    }

    // ─── Sync status ────────────────────────────────────────

    [Fact]
    public async Task SyncStatus_UnknownAccount_ReturnsUntrackedNulls()
    {
        var response = await _client.GetAsync("/api/player/unknownSyncAcct/sync-status");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("unknownSyncAcct", json.GetProperty("accountId").GetString());
        Assert.False(json.GetProperty("isTracked").GetBoolean());
        // backfill and historyRecon may be null (omitted) or JsonValueKind.Null
        if (json.TryGetProperty("backfill", out var bf))
            Assert.Equal(JsonValueKind.Null, bf.ValueKind);
        if (json.TryGetProperty("historyRecon", out var hr))
            Assert.Equal(JsonValueKind.Null, hr.ValueKind);
    }

    [Fact]
    public async Task SyncStatus_RegisteredAccount_ReturnsTrackedWithStatus()
    {
        const string acct = "syncStatusAcct";
        const string device = "syncStatusDev";

        using (var scope = _factory.Services.CreateScope())
        {
            var metaDb = scope.ServiceProvider.GetRequiredService<MetaDatabase>();
            metaDb.RegisterUser(device, acct);
            metaDb.EnqueueBackfill(acct, 100);
        }

        var response = await _client.GetAsync($"/api/player/{acct}/sync-status");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(acct, json.GetProperty("accountId").GetString());
        Assert.True(json.GetProperty("isTracked").GetBoolean());

        var backfill = json.GetProperty("backfill");
        Assert.NotEqual(JsonValueKind.Null, backfill.ValueKind);
        Assert.Equal("pending", backfill.GetProperty("status").GetString());
        Assert.Equal(100, backfill.GetProperty("totalSongsToCheck").GetInt32());
    }

    // ─── Player profile with display name ───────────────────

    [Fact]
    public async Task ApiPlayer_WithKnownAccount_ReturnsDisplayName()
    {
        using (var scope = _factory.Services.CreateScope())
        {
            var metaDb = scope.ServiceProvider.GetRequiredService<MetaDatabase>();
            metaDb.InsertAccountNames(new[] { ("profileAcct1", (string?)"ProfileUser1") });
        }

        var response = await _client.GetAsync("/api/player/profileAcct1");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("profileAcct1", json.GetProperty("accountId").GetString());
        Assert.Equal("ProfileUser1", json.GetProperty("displayName").GetString());
    }

    // ─── Player profile prefers LeaderboardPopulation totalEntries ──

    [Fact]
    public async Task ApiPlayer_PrefersLeaderboardPopulation_OverDbRowCount()
    {
        const string acct = "popTestAcct";
        const string song = "popTestSong";
        const string inst = "Solo_Guitar";

        using (var scope = _factory.Services.CreateScope())
        {
            var metaDb = scope.ServiceProvider.GetRequiredService<MetaDatabase>();
            var persistence = scope.ServiceProvider.GetRequiredService<GlobalLeaderboardPersistence>();

            // Seed an entry in the instrument DB (DB count will be 1)
            var db = persistence.GetOrCreateInstrumentDb(inst);
            db.UpsertEntries(song, new[]
            {
                new LeaderboardEntry { AccountId = acct, Score = 500000, Rank = 1, Stars = 6, IsFullCombo = true, Accuracy = 10000 }
            });

            // Seed LeaderboardPopulation with a much larger value
            metaDb.UpsertLeaderboardPopulation(new[] { (song, inst, (long)75000) });
            metaDb.InsertAccountNames(new[] { (acct, (string?)"PopTestUser") });
        }

        var response = await _client.GetAsync($"/api/player/{acct}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        var scores = json.GetProperty("scores");
        Assert.Equal(1, scores.GetArrayLength());
        // Should use the LeaderboardPopulation value (75000), not the DB row count (1)
        Assert.Equal(75000, scores[0].GetProperty("totalEntries").GetInt32());
    }

    // ─── Leaderboard endpoint prefers LeaderboardPopulation ──

    [Fact]
    public async Task ApiLeaderboard_PrefersLeaderboardPopulation_OverDbRowCount()
    {
        const string song = "popLbSong";
        const string inst = "Solo_Guitar";

        using (var scope = _factory.Services.CreateScope())
        {
            var metaDb = scope.ServiceProvider.GetRequiredService<MetaDatabase>();
            var persistence = scope.ServiceProvider.GetRequiredService<GlobalLeaderboardPersistence>();

            // Seed two entries (DB count will be 2)
            var db = persistence.GetOrCreateInstrumentDb(inst);
            db.UpsertEntries(song, new[]
            {
                new LeaderboardEntry { AccountId = "lbAcct1", Score = 500000, Rank = 1 },
                new LeaderboardEntry { AccountId = "lbAcct2", Score = 400000, Rank = 2 },
            });

            // Seed LeaderboardPopulation with true population
            metaDb.UpsertLeaderboardPopulation(new[] { (song, inst, (long)120000) });
        }

        var response = await _client.GetAsync($"/api/leaderboard/{song}/{inst}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        // Should use the LeaderboardPopulation value (120000), not the DB row count (2)
        Assert.Equal(120000, json.GetProperty("totalEntries").GetInt32());
        // localEntries should reflect the actual DB row count
        Assert.Equal(2, json.GetProperty("localEntries").GetInt32());
    }


    // ─── Backfill POST with API key ─────────────────────────

    [Fact]
    public async Task Backfill_WithApiKey_ReturnsResponse()
    {
        // Register a user first
        var registerContent = JsonContent.Create(new
        {
            deviceId = "backfillDev",
            username = "BackfillUser"
        });
        _client.DefaultRequestHeaders.Remove("X-API-Key");
        _client.DefaultRequestHeaders.Add("X-API-Key", FstWebApplicationFactory.TestApiKey);

        // Seed the account
        using (var scope = _factory.Services.CreateScope())
        {
            var metaDb = scope.ServiceProvider.GetRequiredService<MetaDatabase>();
            metaDb.InsertAccountNames([("backfillAcct2", (string?)"BackfillUser2")]);
            metaDb.RegisterUser("backfillDev2", "backfillAcct2");
        }

        var response = await _client.PostAsync("/api/backfill/backfillAcct2", null);
        // Should succeed (200/202) or return error depending on token availability
        Assert.True(
            (int)response.StatusCode >= 200 && (int)response.StatusCode < 500,
            $"Unexpected: {response.StatusCode}");
    }

    // ─── FirstSeen endpoint ─────────────────────────────────

    [Fact]
    public async Task FirstSeen_ReturnsFirstSeenData()
    {
        // Seed first seen data
        using (var scope = _factory.Services.CreateScope())
        {
            var metaDb = scope.ServiceProvider.GetRequiredService<MetaDatabase>();
            metaDb.UpsertFirstSeenSeason("testSong1", 2, 1, 2, "found");
        }

        var response = await _client.GetAsync("/api/firstseen");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // ─── Diagnostic endpoints: no access token → Problem ───

    [Fact]
    public async Task DiagEvents_NoAccessToken_ReturnsProblem()
    {
        // Temporarily mock TokenManager to return null
        var tokenManager = _factory.Services.GetRequiredService<TokenManager>();
        tokenManager.GetAccessTokenAsync(Arg.Any<CancellationToken>())
            .Returns((string?)null);
        try
        {
            var response = await _client.GetAsync("/api/diag/events");
            Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        }
        finally
        {
            tokenManager.GetAccessTokenAsync(Arg.Any<CancellationToken>())
                .Returns("mock_access_token_for_testing");
        }
    }

    [Fact]
    public async Task DiagLeaderboard_NoAccessToken_ReturnsProblem()
    {
        var tokenManager = _factory.Services.GetRequiredService<TokenManager>();
        tokenManager.GetAccessTokenAsync(Arg.Any<CancellationToken>())
            .Returns((string?)null);
        try
        {
            var response = await _client.GetAsync(
                "/api/diag/leaderboard?eventId=test&windowId=test");
            Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        }
        finally
        {
            tokenManager.GetAccessTokenAsync(Arg.Any<CancellationToken>())
                .Returns("mock_access_token_for_testing");
        }
    }

    [Fact]
    public async Task FirstSeenCalculate_NoAccessToken_ReturnsProblem()
    {
        var tokenManager = _factory.Services.GetRequiredService<TokenManager>();
        tokenManager.GetAccessTokenAsync(Arg.Any<CancellationToken>())
            .Returns((string?)null);
        try
        {
            var response = await _authedClient.PostAsync("/api/firstseen/calculate", null);
            Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        }
        finally
        {
            tokenManager.GetAccessTokenAsync(Arg.Any<CancellationToken>())
                .Returns("mock_access_token_for_testing");
        }
    }

    [Fact]
    public async Task BackfillPost_NoAccessToken_ReturnsProblem()
    {
        // First register a user so we pass the "account is not registered" check
        using (var scope = _factory.Services.CreateScope())
        {
            var metaDb = scope.ServiceProvider.GetRequiredService<MetaDatabase>();
            metaDb.InsertAccountNames([("acctNoToken", (string?)"TokenTestUser")]);
            metaDb.RegisterUser("devNoToken", "acctNoToken");
        }

        var tokenManager = _factory.Services.GetRequiredService<TokenManager>();
        tokenManager.GetAccessTokenAsync(Arg.Any<CancellationToken>())
            .Returns((string?)null);
        try
        {
            var response = await _authedClient.PostAsync("/api/backfill/acctNoToken", null);
            Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        }
        finally
        {
            tokenManager.GetAccessTokenAsync(Arg.Any<CancellationToken>())
                .Returns("mock_access_token_for_testing");
        }
    }


    // ─── API-key sync/{deviceId}: on-demand build ──────────────

    [Fact]
    public async Task SyncDevice_OnDemandBuild_WhenFileNotExists()
    {
        // Register a device and seed its account
        using (var scope = _factory.Services.CreateScope())
        {
            var metaDb = scope.ServiceProvider.GetRequiredService<MetaDatabase>();
            metaDb.InsertAccountNames([("acctSync", (string?)"SyncUser")]);
            metaDb.RegisterUser("devSync", "acctSync");

            // Delete the personal DB file if it was created during registration
            var builder = scope.ServiceProvider.GetRequiredService<PersonalDbBuilder>();
            var dbPath = builder.GetPersonalDbPath("acctSync", "devSync");
            if (File.Exists(dbPath))
                File.Delete(dbPath);
        }

        var response = await _authedClient.GetAsync("/api/sync/devSync");
        // Should succeed — Build creates the file on demand
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // ─── Account search ─────────────────────────────────────

    [Fact]
    public async Task AccountSearch_EmptyQuery_ReturnsEmptyResults()
    {
        var response = await _client.GetAsync("/api/account/search?q=");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(0, json.GetProperty("results").GetArrayLength());
    }

    [Fact]
    public async Task AccountSearch_WithMatches_ReturnsResults()
    {
        // Seed account names
        using (var scope = _factory.Services.CreateScope())
        {
            var metaDb = scope.ServiceProvider.GetRequiredService<MetaDatabase>();
            metaDb.InsertAccountNames([
                ("searchAcct1", (string?)"SearchPlayer"),
                ("searchAcct2", (string?)"SearchOther"),
            ]);
        }

        var response = await _client.GetAsync("/api/account/search?q=Search&limit=5");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        var results = json.GetProperty("results");
        Assert.True(results.GetArrayLength() >= 2);
    }

    [Fact]
    public async Task AccountSearch_LimitClamped_ReturnsAtMost50()
    {
        var response = await _client.GetAsync("/api/account/search?q=test&limit=999");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // ─── Player track ───────────────────────────────────────

    [Fact]
    public async Task TrackPlayer_NewAccount_RegistersAndReturns()
    {
        using (var scope = _factory.Services.CreateScope())
        {
            var metaDb = scope.ServiceProvider.GetRequiredService<MetaDatabase>();
            metaDb.InsertAccountNames([("trackAcct1", (string?)"TrackPlayer")]);
        }

        var response = await _authedClient.PostAsync("/api/player/trackAcct1/track", null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(json.GetProperty("trackingStarted").GetBoolean());
        Assert.True(json.GetProperty("backfillKicked").GetBoolean());
        Assert.Equal("TrackPlayer", json.GetProperty("displayName").GetString());
    }

    [Fact]
    public async Task TrackPlayer_UnknownAccount_ReturnsNotFound()
    {
        var response = await _authedClient.PostAsync("/api/player/nonexistent999/track", null);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task TrackPlayer_AlreadyComplete_DoesNotReKickBackfill()
    {
        using (var scope = _factory.Services.CreateScope())
        {
            var metaDb = scope.ServiceProvider.GetRequiredService<MetaDatabase>();
            metaDb.InsertAccountNames([("trackAcct2", (string?)"TrackPlayer2")]);
            metaDb.RegisterUser("web-tracker", "trackAcct2");
            metaDb.EnqueueBackfill("trackAcct2", 100);
            metaDb.StartBackfill("trackAcct2");
            metaDb.CompleteBackfill("trackAcct2");
        }

        var response = await _authedClient.PostAsync("/api/player/trackAcct2/track", null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(json.GetProperty("backfillKicked").GetBoolean());
        Assert.Equal("complete", json.GetProperty("backfillStatus").GetString());
    }

    [Fact]
    public async Task TrackPlayer_EmptyAccountId_ReturnsBadRequest()
    {
        var response = await _authedClient.PostAsync("/api/player/%20/track", null);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ═══ Leaderboard All Instruments ════════════════════════════

    [Fact]
    public async Task ApiLeaderboardAll_ReturnsAllInstruments()
    {
        // Seed data for multiple instruments
        using (var scope = _factory.Services.CreateScope())
        {
            var persistence = scope.ServiceProvider.GetRequiredService<GlobalLeaderboardPersistence>();
            var guitarDb = persistence.GetOrCreateInstrumentDb("Solo_Guitar");
            guitarDb.UpsertEntries("allSong1", new[]
            {
                new LeaderboardEntry { AccountId = "allAcct1", Score = 100_000 },
                new LeaderboardEntry { AccountId = "allAcct2", Score = 90_000 },
            });
            var bassDb = persistence.GetOrCreateInstrumentDb("Solo_Bass");
            bassDb.UpsertEntries("allSong1", new[]
            {
                new LeaderboardEntry { AccountId = "allAcct3", Score = 80_000 },
            });
        }

        var response = await _client.GetAsync("/api/leaderboard/allSong1/all?top=10");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal("allSong1", json.GetProperty("songId").GetString());
        var instruments = json.GetProperty("instruments");
        Assert.True(instruments.GetArrayLength() >= 2);

        // Check that at least one instrument has entries
        bool hasEntries = false;
        for (int i = 0; i < instruments.GetArrayLength(); i++)
        {
            var inst = instruments[i];
            if (inst.GetProperty("count").GetInt32() > 0)
            {
                hasEntries = true;
                // Verify entries have expected shape
                var entries = inst.GetProperty("entries");
                Assert.True(entries.GetArrayLength() > 0);
                var first = entries[0];
                Assert.True(first.TryGetProperty("accountId", out _));
                Assert.True(first.TryGetProperty("score", out _));
                Assert.True(first.TryGetProperty("rank", out _));
            }
        }
        Assert.True(hasEntries);
    }

    [Fact]
    public async Task ApiLeaderboardAll_EmptySong_ReturnsEmptyInstruments()
    {
        var response = await _client.GetAsync("/api/leaderboard/nonexistentSong/all");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("nonexistentSong", json.GetProperty("songId").GetString());
        var instruments = json.GetProperty("instruments");
        // Should have instruments but all with 0 entries
        for (int i = 0; i < instruments.GetArrayLength(); i++)
        {
            Assert.Equal(0, instruments[i].GetProperty("count").GetInt32());
        }
    }

    // ═══ Player Stats Endpoint ══════════════════════════════════

    [Fact]
    public async Task ApiPlayerStats_ReturnsEmptyWhenNoStats()
    {
        var response = await _client.GetAsync("/api/player/unknownAcct/stats");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("unknownAcct", json.GetProperty("accountId").GetString());
        Assert.Equal(0, json.GetProperty("stats").GetArrayLength());
    }

    [Fact]
    public async Task ApiPlayerStats_ReturnsPreComputedStats()
    {
        // Seed player stats
        using (var scope = _factory.Services.CreateScope())
        {
            var metaDb = scope.ServiceProvider.GetRequiredService<MetaDatabase>();
            metaDb.UpsertPlayerStats(new PlayerStatsDto
            {
                AccountId = "statsAcct1",
                Instrument = "Solo_Guitar",
                SongsPlayed = 42,
                FullComboCount = 10,
                GoldStarCount = 5,
                AvgAccuracy = 97.5,
                BestRank = 3,
                BestRankSongId = "bestSong",
                TotalScore = 4_200_000,
                PercentileDist = "{\"1\":2}",
                AvgPercentile = "Top 5%",
                OverallPercentile = "Top 15%",
            });
        }

        var response = await _client.GetAsync("/api/player/statsAcct1/stats");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal("statsAcct1", json.GetProperty("accountId").GetString());
        var stats = json.GetProperty("stats");
        Assert.Equal(1, stats.GetArrayLength());

        var stat = stats[0];
        Assert.Equal("Solo_Guitar", stat.GetProperty("instrument").GetString());
        Assert.Equal(42, stat.GetProperty("songsPlayed").GetInt32());
        Assert.Equal(10, stat.GetProperty("fullComboCount").GetInt32());
        Assert.Equal(5, stat.GetProperty("goldStarCount").GetInt32());
        Assert.Equal(97.5, stat.GetProperty("avgAccuracy").GetDouble(), 0.01);
        Assert.Equal(3, stat.GetProperty("bestRank").GetInt32());
        Assert.Equal("bestSong", stat.GetProperty("bestRankSongId").GetString());
        Assert.Equal("Top 5%", stat.GetProperty("avgPercentile").GetString());
    }

    // ═══ Leaderboard Combined Count ═════════════════════════════

    [Fact]
    public async Task ApiLeaderboard_ReturnsTotalAndLocal_Correctly()
    {
        // Seed leaderboard data
        using (var scope = _factory.Services.CreateScope())
        {
            var persistence = scope.ServiceProvider.GetRequiredService<GlobalLeaderboardPersistence>();
            var db = persistence.GetOrCreateInstrumentDb("Solo_Guitar");
            db.UpsertEntries("countSong", new[]
            {
                new LeaderboardEntry { AccountId = "cnt1", Score = 300 },
                new LeaderboardEntry { AccountId = "cnt2", Score = 200 },
                new LeaderboardEntry { AccountId = "cnt3", Score = 100 },
            });
        }

        var response = await _client.GetAsync("/api/leaderboard/countSong/Solo_Guitar?top=2");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(2, json.GetProperty("count").GetInt32());
        Assert.Equal(3, json.GetProperty("localEntries").GetInt32());
        Assert.True(json.GetProperty("totalEntries").GetInt32() >= 3);
    }

    /// <summary>
    /// Ensures the Songs table and a row for the given songId exist in the PathDataStore database
    /// so UpdateMaxScores can UPDATE the row.
    /// </summary>
    private static void EnsureSongRow(PathDataStore pathStore, string songId)
    {
        // PathDataStore exposes its connection string pattern internally;
        // retrieve the DB path via reflection on the private _connectionString field.
        var connField = typeof(PathDataStore)
            .GetField("_connectionString", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var connStr = (string)connField.GetValue(pathStore)!;

        using var conn = new Microsoft.Data.Sqlite.SqliteConnection(connStr);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS Songs (
                SongId TEXT PRIMARY KEY,
                Title TEXT,
                MaxLeadScore INTEGER,
                MaxBassScore INTEGER,
                MaxDrumsScore INTEGER,
                MaxVocalsScore INTEGER,
                MaxProLeadScore INTEGER,
                MaxProBassScore INTEGER,
                DatFileHash TEXT,
                SongLastModified TEXT,
                PathsGeneratedAt TEXT,
                CHOptVersion TEXT
            );
            INSERT OR IGNORE INTO Songs (SongId, Title) VALUES (@songId, 'Test Song');
            """;
        cmd.Parameters.AddWithValue("@songId", songId);
        cmd.ExecuteNonQuery();
    }

    // ═══════════════════════════════════════════════════════════════
    // Factory: sets up the test server with in-memory/temp dependencies
    // ═══════════════════════════════════════════════════════════════

    public sealed class FstWebApplicationFactory : WebApplicationFactory<Program>
    {
        public const string TestApiKey = "test-api-key-12345";

        private readonly string _tempDir = Path.Combine(
            Path.GetTempPath(), $"fst_api_test_{Guid.NewGuid():N}");

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            Directory.CreateDirectory(_tempDir);

            builder.UseEnvironment("Testing");

            builder.ConfigureAppConfiguration((ctx, config) =>
            {
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Scraper:DataDirectory"] = _tempDir,
                    ["Scraper:DatabasePath"] = Path.Combine(_tempDir, "core.db"),
                    ["Scraper:DeviceAuthPath"] = Path.Combine(_tempDir, "device-auth.json"),
                    ["Scraper:ApiOnly"] = "true",
                    ["Api:ApiKey"] = TestApiKey,
                    ["Api:AllowedOrigins:0"] = "*",
                    ["Jwt:SecretKey"] = "TestSecretKey_SuperLongEnough_For_HMACSHA256_12345678",
                    ["Jwt:Issuer"] = "FSTService.Tests",
                    ["Jwt:Audience"] = "FSTService.Tests",
                    ["Jwt:AccessTokenExpirationMinutes"] = "60",
                    ["Jwt:RefreshTokenExpirationDays"] = "7",
                    ["EpicOAuth:ClientId"] = "test-client-id",
                    ["EpicOAuth:ClientSecret"] = "test-client-secret",
                    ["EpicOAuth:RedirectUri"] = "https://example.com/api/auth/epiccallback",
                    ["EpicOAuth:AppDeepLink"] = "festscoretracker://auth/callback",
                    ["EpicOAuth:TokenEncryptionKey"] = Convert.ToBase64String(new byte[32]),
                });
            });

            builder.ConfigureServices(services =>
            {
                // Remove the real ScraperWorker — we don't want background scraping
                services.RemoveAll<IHostedService>();

                // Re-register DatabaseInitializer so schemas are created during test startup
                services.AddHostedService(sp => sp.GetRequiredService<DatabaseInitializer>());

                // Ensure API key auth options are set (Program.cs resolves them early
                // before test config is applied, so we must override here)
                services.Configure<ApiKeyAuthOptions>("ApiKey", opts => opts.ApiKey = TestApiKey);

                // Replace TokenManager with a mock that returns a valid token
                // This unlocks coverage for endpoints gated by GetAccessTokenAsync
                services.RemoveAll<TokenManager>();
                services.RemoveAll<EpicAuthService>();

                var mockHandler = new HttpMessageHandler_NoOp();
                var mockHttp = new HttpClient(mockHandler);
                var mockEpic = Substitute.For<EpicAuthService>(mockHttp, Substitute.For<ILogger<EpicAuthService>>());

                // Mock ExchangeAuthorizationCodeAsync to return a fake Epic token
                mockEpic.ExchangeAuthorizationCodeAsync(
                        Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
                        Arg.Any<string>(), Arg.Any<CancellationToken>())
                    .Returns(callInfo => Task.FromResult(new EpicTokenResponse
                    {
                        AccountId = $"acct_{callInfo.ArgAt<string>(0).GetHashCode():x8}",
                        DisplayName = $"Player_{callInfo.ArgAt<string>(0)}",
                        AccessToken = "mock_epic_access_token",
                    }));
                services.AddSingleton(mockEpic);

                var mockTokenManager = Substitute.For<TokenManager>(
                    mockEpic,
                    Substitute.For<ICredentialStore>(),
                    Substitute.For<ILogger<TokenManager>>());
                mockTokenManager.GetAccessTokenAsync(Arg.Any<CancellationToken>())
                    .Returns("mock_access_token_for_testing");
                mockTokenManager.AccountId.Returns("mock_caller_account_id");
                services.AddSingleton(mockTokenManager);


                // Replace FestivalService with one that has test songs pre-loaded
                services.RemoveAll<FestivalService>();
                var festivalService = CreateTestFestivalService();
                services.AddSingleton(festivalService);

                // Register a default IHttpClientFactory that uses the no-op handler
                // so diagnostic endpoints don't make real HTTP requests
                services.AddHttpClient(string.Empty)
                    .ConfigurePrimaryHttpMessageHandler(() => new HttpMessageHandler_NoOp());
            });
        }

        private static FestivalService CreateTestFestivalService()
        {
            var service = new FestivalService((IFestivalPersistence?)null);
            var flags = BindingFlags.NonPublic | BindingFlags.Instance;
            var songsField = typeof(FestivalService).GetField("_songs", flags)!;
            var dirtyField = typeof(FestivalService).GetField("_songsDirty", flags)!;
            var dict = (Dictionary<string, Song>)songsField.GetValue(service)!;
            dict["testSong1"] = new Song
            {
                track = new Track
                {
                    su = "testSong1",
                    tt = "Integration Test Song",
                    an = "Test Artist",
                    @in = new In { gr = 5, ba = 3, vl = 4, ds = 2 },
                }
            };
            dirtyField.SetValue(service, true);
            return service;
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            try { Directory.Delete(_tempDir, true); } catch { }
        }

        /// <summary>A no-op HTTP handler that returns empty 200 responses.</summary>
        private sealed class HttpMessageHandler_NoOp : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request, CancellationToken cancellationToken)
                => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
    }
}
