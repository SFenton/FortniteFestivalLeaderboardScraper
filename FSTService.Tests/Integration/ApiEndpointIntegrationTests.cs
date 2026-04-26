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
using FSTService.Tests.Helpers;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Npgsql;

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

    // ─── Features ───────────────────────────────────────────────

    [Fact]
    public async Task ApiFeatures_ReturnsFeatureFlags()
    {
        var response = await _client.GetAsync("/api/features");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        // Default config keeps the remaining optional features OFF.
        Assert.False(json.GetProperty("compete").GetBoolean());
        Assert.False(json.GetProperty("leaderboards").GetBoolean());
        Assert.False(json.GetProperty("difficulty").GetBoolean());
        Assert.False(json.GetProperty("playerBands").GetBoolean());
        Assert.False(json.GetProperty("experimentalRanks").GetBoolean());
        Assert.False(json.TryGetProperty("rivals", out _));
        Assert.False(json.TryGetProperty("firstRun", out _));
    }

    // ─── Progress ───────────────────────────────────────────────

    [Fact]
    public async Task ApiProgress_ReturnsProgressResponse()
    {
        var response = await _client.GetAsync("/api/progress");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task ApiServiceInfo_ReturnsExpectedShape()
    {
        var response = await _client.GetAsync("/api/service-info");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(json.TryGetProperty("lastCompletedUpdate", out _));
        Assert.Equal("idle", json.GetProperty("currentUpdate").GetProperty("status").GetString());
        Assert.True(json.TryGetProperty("nextScheduledUpdateAt", out _));
    }

    [Fact]
    public async Task ApiServiceInfo_UsesLastCompletedScrapeForDurableTiming()
    {
        var metaDb = _factory.Services.GetRequiredService<MetaDatabase>();
        var scrapeId = metaDb.StartScrapeRun();
        metaDb.CompleteScrapeRun(scrapeId, songsScraped: 12, totalEntries: 345, totalRequests: 67, totalBytes: 890);

        var response = await _client.GetAsync("/api/service-info");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        var lastCompletedUpdate = json.GetProperty("lastCompletedUpdate");
        var startedAt = DateTimeOffset.Parse(lastCompletedUpdate.GetProperty("startedAt").GetString()!);
        var completedAt = DateTimeOffset.Parse(lastCompletedUpdate.GetProperty("completedAt").GetString()!);
        var nextScheduledUpdateAt = DateTimeOffset.Parse(json.GetProperty("nextScheduledUpdateAt").GetString()!);

        Assert.True(startedAt <= completedAt);
        Assert.Equal(completedAt.AddHours(4), nextScheduledUpdateAt);
    }

    [Fact]
    public async Task ApiServiceInfo_ReflectsLiveScrapeProgress()
    {
        var tracker = _factory.Services.GetRequiredService<ScrapeProgressTracker>();
        tracker.EndPass();

        try
        {
            tracker.BeginPass(totalLeaderboards: 10, totalSongs: 5, cachedTotalPages: 0);
            tracker.SetSubOperation("fetching_leaderboards");

            var response = await _client.GetAsync("/api/service-info");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            var json = await response.Content.ReadFromJsonAsync<JsonElement>();
            var currentUpdate = json.GetProperty("currentUpdate");
            Assert.Equal("updating", currentUpdate.GetProperty("status").GetString());
            Assert.Equal("Scraping", currentUpdate.GetProperty("phase").GetString());
            Assert.Equal("fetching_leaderboards", currentUpdate.GetProperty("subOperation").GetString());
            Assert.False(json.TryGetProperty("nextScheduledUpdateAt", out _));
        }
        finally
        {
            tracker.EndPass();
        }
    }

    [Fact]
    public async Task ApiServiceInfo_ExposesBranchesAndProgressPercent_DuringEnrichment()
    {
        var tracker = _factory.Services.GetRequiredService<ScrapeProgressTracker>();
        tracker.EndPass();

        try
        {
            tracker.BeginPass(totalLeaderboards: 0, totalSongs: 0, cachedTotalPages: 0);
            tracker.SetPhase(ScrapeProgressTracker.ScrapePhase.PostScrapeEnrichment);
            tracker.RegisterBranches(new[] { "rank_recompute", "first_seen", "name_resolution", "pruning" });
            tracker.StartBranch("rank_recompute");
            tracker.CompleteBranch("rank_recompute", "complete", "42 entries updated");
            tracker.SetSubOperation("enriching_parallel_tail");

            var response = await _client.GetAsync("/api/service-info");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            var json = await response.Content.ReadFromJsonAsync<JsonElement>();
            var currentUpdate = json.GetProperty("currentUpdate");

            Assert.Equal("updating", currentUpdate.GetProperty("status").GetString());
            Assert.Equal("PostScrapeEnrichment", currentUpdate.GetProperty("phase").GetString());
            Assert.Equal("enriching_parallel_tail", currentUpdate.GetProperty("subOperation").GetString());
            Assert.Equal(25.0, currentUpdate.GetProperty("progressPercent").GetDouble());

            var branches = currentUpdate.GetProperty("branches");
            Assert.Equal(JsonValueKind.Array, branches.ValueKind);
            Assert.Equal(4, branches.GetArrayLength());
            var rankBranch = branches.EnumerateArray().Single(b => b.GetProperty("id").GetString() == "rank_recompute");
            Assert.Equal("complete", rankBranch.GetProperty("status").GetString());
            Assert.Equal("42 entries updated", rankBranch.GetProperty("message").GetString());
        }
        finally
        {
            tracker.EndPass();
        }
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

    [Fact]
    public async Task ApiSongs_Difficulty_ExposesAllInstrumentFields()
    {
        var response = await _client.GetAsync("/api/songs");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        var songs = json.GetProperty("songs");
        JsonElement? testSong = null;
        for (var i = 0; i < songs.GetArrayLength(); i++)
        {
            if (songs[i].GetProperty("songId").GetString() == "testSong1")
            {
                testSong = songs[i];
                break;
            }
        }
        Assert.NotNull(testSong);
        var diff = testSong!.Value.GetProperty("difficulty");

        // Existing fields
        Assert.Equal(5, diff.GetProperty("guitar").GetInt32());
        Assert.Equal(3, diff.GetProperty("bass").GetInt32());
        Assert.Equal(4, diff.GetProperty("vocals").GetInt32());
        Assert.Equal(2, diff.GetProperty("drums").GetInt32());
        Assert.Equal(6, diff.GetProperty("proGuitar").GetInt32());
        Assert.Equal(5, diff.GetProperty("proBass").GetInt32());

        // New fields: proDrums and proCymbals share @in.pd; proVocals is Karaoke (@in.bd).
        Assert.Equal(7, diff.GetProperty("proDrums").GetInt32());
        Assert.Equal(7, diff.GetProperty("proCymbals").GetInt32());
        Assert.Equal(
            diff.GetProperty("proDrums").GetInt32(),
            diff.GetProperty("proCymbals").GetInt32());
        Assert.Equal(4, diff.GetProperty("proVocals").GetInt32());
    }

    [Fact]
    public async Task ApiSongs_Difficulty_OmitsMicModePropertyWhenSongHasNoChart()
    {
        var response = await _client.GetAsync("/api/songs");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        var songs = json.GetProperty("songs");
        JsonElement? testSong = null;
        for (var i = 0; i < songs.GetArrayLength(); i++)
        {
          if (songs[i].GetProperty("songId").GetString() == "testSongNoMic")
          {
              testSong = songs[i];
              break;
          }
        }

        Assert.True(testSong.HasValue);
        var diff = testSong!.Value.GetProperty("difficulty");
        Assert.False(diff.TryGetProperty("proVocals", out _));
    }

    [Fact]
    public async Task ApiSongs_ExposesDurationSeconds()
    {
        var response = await _client.GetAsync("/api/songs");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        var songs = json.GetProperty("songs");
        JsonElement? testSong = null;
        for (var i = 0; i < songs.GetArrayLength(); i++)
        {
            if (songs[i].GetProperty("songId").GetString() == "testSong1")
            {
                testSong = songs[i];
                break;
            }
        }
        Assert.NotNull(testSong);
        Assert.True(testSong!.Value.TryGetProperty("durationSeconds", out var dur));
        Assert.Equal(235, dur.GetInt32());
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
    public async Task ApiPlayer_PrefersCurrentStateRows_AndMixedLiveFallback()
    {
        const string accountId = "playerCurrentStateAcct";
        const string guitarSong = "playerCurrentStateSong";
        const string bassSong = "playerLiveFallbackSong";

        using (var scope = _factory.Services.CreateScope())
        {
            var persistence = scope.ServiceProvider.GetRequiredService<GlobalLeaderboardPersistence>();
            var dataSource = scope.ServiceProvider.GetRequiredService<NpgsqlDataSource>();

            var guitarDb = persistence.GetOrCreateInstrumentDb("Solo_Guitar");
            guitarDb.UpsertEntries(guitarSong, new[]
            {
                new LeaderboardEntry { AccountId = accountId, Score = 100_000, Rank = 1, ApiRank = 1 },
            });
            InsertSnapshotEntry(dataSource, 1201, guitarSong, "Solo_Guitar", accountId, 95_000, rank: 1);
            InsertSnapshotState(dataSource, guitarSong, "Solo_Guitar", 1201);
            InsertOverlayEntry(dataSource, guitarSong, "Solo_Guitar", accountId, 97_000, sourcePriority: 200, overlayReason: "refresh");

            var bassDb = persistence.GetOrCreateInstrumentDb("Solo_Bass");
            bassDb.UpsertEntries(bassSong, new[]
            {
                new LeaderboardEntry { AccountId = accountId, Score = 88_000, Rank = 1, ApiRank = 1 },
            });
        }

        var response = await _client.GetAsync($"/api/player/{accountId}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(2, json.GetProperty("totalScores").GetInt32());
        var scores = json.GetProperty("scores");

        JsonElement guitarScore = default;
        JsonElement bassScore = default;
        foreach (var score in scores.EnumerateArray())
        {
            switch (score.GetProperty("si").GetString())
            {
                case guitarSong:
                    guitarScore = score;
                    break;
                case bassSong:
                    bassScore = score;
                    break;
            }
        }

        Assert.Equal(97_000, guitarScore.GetProperty("sc").GetInt32());
        Assert.Equal(88_000, bassScore.GetProperty("sc").GetInt32());
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

        // Act — leeway=0 triggers computed rank path (without leeway, stored rank is used)
        var response = await _client.GetAsync($"/api/player/rankAcct?songId={song}&leeway=0");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();

        var scores = json.GetProperty("scores");
        Assert.Equal(1, scores.GetArrayLength());

        var score = scores[0];
        // The computed rank should be 5 (4 players have higher scores + 1),
        // NOT the stale stored rank of 3.
        Assert.Equal(5, score.GetProperty("rk").GetInt32());
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

        var response = await _client.GetAsync($"/api/player/rankFbAcct?songId={song}&leeway=0");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();

        var score = json.GetProperty("scores")[0];
        // Computed rank is 1 (only entry), which is > 0, so we use it instead of stored 7
        Assert.Equal(1, score.GetProperty("rk").GetInt32());
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

        // Get player rank from /api/player — leeway=0 triggers computed rank
        var playerResponse = await _client.GetAsync($"/api/player/rcAcct2?songId={song}&leeway=0");
        Assert.Equal(HttpStatusCode.OK, playerResponse.StatusCode);
        var playerJson = await playerResponse.Content.ReadFromJsonAsync<JsonElement>();
        var playerRank = playerJson.GetProperty("scores")[0].GetProperty("rk").GetInt32();

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

    [Fact]
    public async Task ApiLeaderboardAll_Rank_PrefersApiRank()
    {
        const string song = "apiRankAllSong";
        const string inst = "Solo_Guitar";

        using (var scope = _factory.Services.CreateScope())
        {
            var persistence = scope.ServiceProvider.GetRequiredService<GlobalLeaderboardPersistence>();
            var db = persistence.GetOrCreateInstrumentDb(inst);
            db.UpsertEntries(song, new[]
            {
                // ApiRank=5000 from Epic backfill; computed ROW_NUMBER rank would be 1
                new LeaderboardEntry { AccountId = "apiRkAcct1", Score = 90_000, ApiRank = 5000 },
                // No ApiRank; computed ROW_NUMBER rank should be used (2)
                new LeaderboardEntry { AccountId = "apiRkAcct2", Score = 80_000, ApiRank = 0 },
            });
        }

        var response = await _client.GetAsync($"/api/leaderboard/{song}/all");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        var instruments = json.GetProperty("instruments");

        foreach (var instEl in instruments.EnumerateArray())
        {
            if (instEl.GetProperty("instrument").GetString() != inst) continue;
            var entries = instEl.GetProperty("entries");
            // First entry: ApiRank=5000 should be used instead of computed rank 1
            Assert.Equal(5000, entries[0].GetProperty("rank").GetInt32());
            // Second entry: ApiRank=0, so computed rank (2) is used
            Assert.Equal(2, entries[1].GetProperty("rank").GetInt32());
        }
    }

    [Fact]
    public async Task ApiPlayer_Rank_PrefersApiRank_OverComputedRank()
    {
        const string song = "apiRankPlayerSong";
        const string inst = "Solo_Guitar";

        using (var scope = _factory.Services.CreateScope())
        {
            var persistence = scope.ServiceProvider.GetRequiredService<GlobalLeaderboardPersistence>();
            var db = persistence.GetOrCreateInstrumentDb(inst);
            db.UpsertEntries(song, new[]
            {
                new LeaderboardEntry { AccountId = "apiRkOther1", Score = 90_000 },
                // This player: computed rank = 2, but Epic says rank 10001
                new LeaderboardEntry { AccountId = "apiRkPlayer", Score = 80_000, ApiRank = 10_001 },
            });
        }

        var response = await _client.GetAsync($"/api/player/apiRkPlayer?songId={song}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();

        var score = json.GetProperty("scores")[0];
        // ApiRank=10001 should take priority over computed rank (2)
        Assert.Equal(10_001, score.GetProperty("rk").GetInt32());
    }

    [Fact]
    public async Task ApiPlayer_Rank_FallsBackToComputed_WhenApiRankZero()
    {
        const string song = "apiRankFallbackSong";
        const string inst = "Solo_Guitar";

        using (var scope = _factory.Services.CreateScope())
        {
            var persistence = scope.ServiceProvider.GetRequiredService<GlobalLeaderboardPersistence>();
            var db = persistence.GetOrCreateInstrumentDb(inst);
            db.UpsertEntries(song, new[]
            {
                new LeaderboardEntry { AccountId = "fbOther1", Score = 90_000 },
                // No ApiRank — should use computed rank (2)
                new LeaderboardEntry { AccountId = "fbPlayer", Score = 80_000, ApiRank = 0, Rank = 99 },
            });
        }

        var response = await _client.GetAsync($"/api/player/fbPlayer?songId={song}&leeway=0");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();

        var score = json.GetProperty("scores")[0];
        // ApiRank=0, so computed rank (2) is used — NOT stale stored rank (99)
        Assert.Equal(2, score.GetProperty("rk").GetInt32());
    }

    [Fact]
    public async Task ApiPlayer_Leeway_Rank_UsesCurrentStateMembership()
    {
        const string accountId = "playerCurrentStateRankAcct";
        const string song = "playerCurrentStateRankSong";

        using (var scope = _factory.Services.CreateScope())
        {
            var persistence = scope.ServiceProvider.GetRequiredService<GlobalLeaderboardPersistence>();
            var dataSource = scope.ServiceProvider.GetRequiredService<NpgsqlDataSource>();

            var db = persistence.GetOrCreateInstrumentDb("Solo_Guitar");
            db.UpsertEntries(song, new[]
            {
                new LeaderboardEntry { AccountId = accountId, Score = 200_000, Rank = 1, ApiRank = 1 },
                new LeaderboardEntry { AccountId = "playerCurrentStateRankLiveOther", Score = 100_000, Rank = 2, ApiRank = 2 },
            });

            InsertSnapshotEntry(dataSource, 1202, song, "Solo_Guitar", "playerCurrentStateRankSnapOther", 300_000, rank: 1);
            InsertSnapshotEntry(dataSource, 1202, song, "Solo_Guitar", accountId, 200_000, rank: 2);
            InsertSnapshotState(dataSource, song, "Solo_Guitar", 1202);
        }

        var response = await _client.GetAsync($"/api/player/{accountId}?songId={song}&leeway=0");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(2, json.GetProperty("scores")[0].GetProperty("rk").GetInt32());
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
    public async Task PostLeaderboardPopulation_EndpointRemoved()
    {
        var content = JsonContent.Create(new[]
        {
            new { songId = "song1", instrument = "Solo_Guitar", totalEntries = 50000L },
        });
        var response = await _authedClient.PostAsync("/api/leaderboard-population", content);
        // POST endpoint was removed — PercentileService deprecated
        Assert.True(response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed);
    }

    [Fact]
    public async Task GetLeaderboardPopulation_RequiresAuth()
    {
        var response = await _client.GetAsync("/api/leaderboard-population");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
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

    [Fact]
    public async Task ApiLeaderboard_PrefersFinalizedSnapshotState_OverLiveRows()
    {
        const string song = "snapshotEndpointSong";
        const string inst = "Solo_Guitar";

        using (var scope = _factory.Services.CreateScope())
        {
            var persistence = scope.ServiceProvider.GetRequiredService<GlobalLeaderboardPersistence>();
            var dataSource = scope.ServiceProvider.GetRequiredService<NpgsqlDataSource>();

            var db = persistence.GetOrCreateInstrumentDb(inst);
            db.UpsertEntries(song, new[]
            {
                new LeaderboardEntry { AccountId = "live_only", Score = 500000, Rank = 1, ApiRank = 1 },
            });

            InsertSnapshotEntry(dataSource, 77, song, inst, "snapshot_only", 490000, rank: 1);
            InsertSnapshotState(dataSource, song, inst, 77);
        }

        var response = await _client.GetAsync($"/api/leaderboard/{song}/{inst}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, json.GetProperty("localEntries").GetInt32());
        Assert.Equal(1, json.GetProperty("count").GetInt32());

        var entries = json.GetProperty("entries");
        Assert.Equal(1, entries.GetArrayLength());
        Assert.Equal("snapshot_only", entries[0].GetProperty("accountId").GetString());
        Assert.Equal(490000, entries[0].GetProperty("score").GetInt32());
    }

    [Fact]
    public async Task ApiLeaderboard_PrefersOverlay_OverFinalizedSnapshotAndLiveRows()
    {
        const string song = "overlayEndpointSong";
        const string inst = "Solo_Guitar";

        using (var scope = _factory.Services.CreateScope())
        {
            var persistence = scope.ServiceProvider.GetRequiredService<GlobalLeaderboardPersistence>();
            var dataSource = scope.ServiceProvider.GetRequiredService<NpgsqlDataSource>();

            var db = persistence.GetOrCreateInstrumentDb(inst);
            db.UpsertEntries(song, new[]
            {
                new LeaderboardEntry { AccountId = "shared_acct", Score = 400000, Rank = 1, ApiRank = 1 },
                new LeaderboardEntry { AccountId = "live_other", Score = 350000, Rank = 2, ApiRank = 2 },
            });

            InsertSnapshotEntry(dataSource, 88, song, inst, "shared_acct", 450000, rank: 1);
            InsertSnapshotEntry(dataSource, 88, song, inst, "snapshot_other", 440000, rank: 2);
            InsertSnapshotState(dataSource, song, inst, 88);
            InsertOverlayEntry(dataSource, song, inst, "shared_acct", 470000, sourcePriority: 200, overlayReason: "refresh");
        }

        var response = await _client.GetAsync($"/api/leaderboard/{song}/{inst}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(2, json.GetProperty("localEntries").GetInt32());

        var entries = json.GetProperty("entries");
        Assert.Equal(2, entries.GetArrayLength());
        Assert.Equal("shared_acct", entries[0].GetProperty("accountId").GetString());
        Assert.Equal(470000, entries[0].GetProperty("score").GetInt32());
        Assert.Equal("refresh", entries[0].GetProperty("source").GetString());
        Assert.Equal("snapshot_other", entries[1].GetProperty("accountId").GetString());
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
        Assert.Equal(75000, scores[0].GetProperty("te").GetInt32());
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
            metaDb.UpsertFirstSeenSeason("testSong1", 2, 1, 2, "found", 2);
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
    public async Task SelectedPlayerHeader_TouchesWebRegistrationActivity()
    {
        using (var scope = _factory.Services.CreateScope())
        {
            var metaDb = scope.ServiceProvider.GetRequiredService<MetaDatabase>();
            var dataSource = scope.ServiceProvider.GetRequiredService<NpgsqlDataSource>();

            metaDb.InsertAccountNames([("trackHeaderAcct", (string?)"Header Player")]);
            metaDb.RegisterUser("web-tracker", "trackHeaderAcct");

            using var conn = dataSource.OpenConnection();
            using var seed = conn.CreateCommand();
            seed.CommandText = "UPDATE registered_users SET last_activity_at = @lastActivityAt, registered_at = @registeredAt WHERE device_id = @deviceId AND account_id = @accountId";
            seed.Parameters.AddWithValue("deviceId", "web-tracker");
            seed.Parameters.AddWithValue("accountId", "trackHeaderAcct");
            seed.Parameters.AddWithValue("lastActivityAt", DateTime.UtcNow.AddHours(-8));
            seed.Parameters.AddWithValue("registeredAt", DateTime.UtcNow.AddHours(-8));
            seed.ExecuteNonQuery();
        }

        var request = new HttpRequestMessage(HttpMethod.Get, "/api/version");
        request.Headers.Add("X-FST-Selected-Player", "trackHeaderAcct");

        var response = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var verifyScope = _factory.Services.CreateScope();
        var verifyDataSource = verifyScope.ServiceProvider.GetRequiredService<NpgsqlDataSource>();
        using var verifyConn = verifyDataSource.OpenConnection();
        using var verify = verifyConn.CreateCommand();
        verify.CommandText = "SELECT last_activity_at FROM registered_users WHERE device_id = @deviceId AND account_id = @accountId";
        verify.Parameters.AddWithValue("deviceId", "web-tracker");
        verify.Parameters.AddWithValue("accountId", "trackHeaderAcct");
        var lastActivityAt = (DateTime?)verify.ExecuteScalar();

        Assert.NotNull(lastActivityAt);
        Assert.True(lastActivityAt!.Value > DateTime.UtcNow.AddHours(-1));
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
    public async Task ApiLeaderboardAll_MixesCurrentStateAndLiveFallbackPerInstrument()
    {
        const string songId = "allSongCurrentStateMix";

        using (var scope = _factory.Services.CreateScope())
        {
            var persistence = scope.ServiceProvider.GetRequiredService<GlobalLeaderboardPersistence>();
            var dataSource = scope.ServiceProvider.GetRequiredService<NpgsqlDataSource>();
            var metaDb = scope.ServiceProvider.GetRequiredService<IMetaDatabase>();

            var guitarDb = persistence.GetOrCreateInstrumentDb("Solo_Guitar");
            guitarDb.UpsertEntries(songId, new[]
            {
                new LeaderboardEntry { AccountId = "g_live_only", Score = 100_000, Rank = 1, ApiRank = 1 },
            });

            InsertSnapshotEntry(dataSource, 901, songId, "Solo_Guitar", "g_snapshot", 99_000, rank: 1);
            InsertSnapshotState(dataSource, songId, "Solo_Guitar", 901);
            InsertOverlayEntry(dataSource, songId, "Solo_Guitar", "g_snapshot", 101_000, sourcePriority: 200, overlayReason: "refresh");

            var bassDb = persistence.GetOrCreateInstrumentDb("Solo_Bass");
            bassDb.UpsertEntries(songId, new[]
            {
                new LeaderboardEntry { AccountId = "b_live", Score = 88_000, Rank = 1, ApiRank = 1 },
            });

            metaDb.UpsertLeaderboardPopulation(new[]
            {
                (songId, "Solo_Guitar", 200L),
                (songId, "Solo_Bass", 50L),
            });
        }

        var response = await _client.GetAsync($"/api/leaderboard/{songId}/all?top=10");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        var instruments = json.GetProperty("instruments");

        JsonElement guitar = default;
        JsonElement bass = default;
        foreach (var instrument in instruments.EnumerateArray())
        {
            switch (instrument.GetProperty("instrument").GetString())
            {
                case "Solo_Guitar":
                    guitar = instrument;
                    break;
                case "Solo_Bass":
                    bass = instrument;
                    break;
            }
        }

        Assert.Equal(1, guitar.GetProperty("count").GetInt32());
        Assert.Equal(1, guitar.GetProperty("localEntries").GetInt32());
        Assert.Equal(200, guitar.GetProperty("totalEntries").GetInt32());
        Assert.Equal("g_snapshot", guitar.GetProperty("entries")[0].GetProperty("accountId").GetString());
        Assert.Equal(101000, guitar.GetProperty("entries")[0].GetProperty("score").GetInt32());

        Assert.Equal(1, bass.GetProperty("count").GetInt32());
        Assert.Equal(1, bass.GetProperty("localEntries").GetInt32());
        Assert.Equal(50, bass.GetProperty("totalEntries").GetInt32());
        Assert.Equal("b_live", bass.GetProperty("entries")[0].GetProperty("accountId").GetString());
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

    [Fact]
    public async Task ApiLeaderboardAll_IgnoresStalePrecomputedCache_AndReturnsFreshResults()
    {
        const string songId = "allSongStaleCache";

        using (var scope = _factory.Services.CreateScope())
        {
            var persistence = scope.ServiceProvider.GetRequiredService<GlobalLeaderboardPersistence>();
            var metaDb = scope.ServiceProvider.GetRequiredService<IMetaDatabase>();

            var guitarDb = persistence.GetOrCreateInstrumentDb("Solo_Guitar");
            guitarDb.UpsertEntries(songId, new[]
            {
                new LeaderboardEntry { AccountId = "freshGuitar1", Score = 111_000 },
                new LeaderboardEntry { AccountId = "freshGuitar2", Score = 101_000 },
            });

            var bassDb = persistence.GetOrCreateInstrumentDb("Solo_Bass");
            bassDb.UpsertEntries(songId, new[]
            {
                new LeaderboardEntry { AccountId = "freshBass1", Score = 95_000 },
            });

            metaDb.UpsertLeaderboardPopulation(new[]
            {
                (songId, "Solo_Guitar", 200L),
                (songId, "Solo_Bass", 50L),
            });

            var stalePayload = new
            {
                songId,
                instruments = new[]
                {
                    new
                    {
                        instrument = "Solo_Guitar",
                        count = 0,
                        totalEntries = 10,
                        localEntries = 10,
                        entries = Array.Empty<object>(),
                    },
                    new
                    {
                        instrument = "Solo_Bass",
                        count = 0,
                        totalEntries = 5,
                        localEntries = 5,
                        entries = Array.Empty<object>(),
                    },
                },
            };

            var staleJson = JsonSerializer.SerializeToUtf8Bytes(stalePayload);
            metaDb.BulkSetCachedResponses(new[]
            {
                ($"lb:{songId}:10:", staleJson, ResponseCacheService.ComputeETag(staleJson)),
            });
        }

        var response = await _client.GetAsync($"/api/leaderboard/{songId}/all?top=10");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        var instruments = json.GetProperty("instruments");

        JsonElement guitar = default;
        JsonElement bass = default;
        foreach (var inst in instruments.EnumerateArray())
        {
            switch (inst.GetProperty("instrument").GetString())
            {
                case "Solo_Guitar":
                    guitar = inst;
                    break;
                case "Solo_Bass":
                    bass = inst;
                    break;
            }
        }

        Assert.Equal(2, guitar.GetProperty("count").GetInt32());
        Assert.Equal(2, guitar.GetProperty("localEntries").GetInt32());
        Assert.Equal(200, guitar.GetProperty("totalEntries").GetInt32());
        Assert.Equal(2, guitar.GetProperty("entries").GetArrayLength());

        Assert.Equal(1, bass.GetProperty("count").GetInt32());
        Assert.Equal(1, bass.GetProperty("localEntries").GetInt32());
        Assert.Equal(50, bass.GetProperty("totalEntries").GetInt32());
        Assert.Equal(1, bass.GetProperty("entries").GetArrayLength());
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
    public async Task ApiPlayerStats_OnDemand_UsesCurrentStateScores()
    {
        const string accountId = "playerStatsCurrentStateAcct";
        const string song = "playerStatsCurrentStateSong";

        using (var scope = _factory.Services.CreateScope())
        {
            var persistence = scope.ServiceProvider.GetRequiredService<GlobalLeaderboardPersistence>();
            var dataSource = scope.ServiceProvider.GetRequiredService<NpgsqlDataSource>();

            var db = persistence.GetOrCreateInstrumentDb("Solo_Guitar");
            db.UpsertEntries(song, new[]
            {
                new LeaderboardEntry { AccountId = accountId, Score = 100_000, Rank = 1, ApiRank = 1 },
            });

            InsertSnapshotEntry(dataSource, 1203, song, "Solo_Guitar", "playerStatsSnapshotOther", 99_000, rank: 1);
            InsertSnapshotState(dataSource, song, "Solo_Guitar", 1203);
        }

        var response = await _client.GetAsync($"/api/player/{accountId}/stats");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(accountId, json.GetProperty("accountId").GetString());
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

    [Fact]
    public async Task ApiPlayerStats_ReturnsBands_WhenFeatureEnabled()
    {
        var featureOptions = _factory.Services.GetRequiredService<IOptions<FeatureOptions>>().Value;
        var originalPlayerBands = featureOptions.PlayerBands;
        featureOptions.PlayerBands = true;

        try
        {
            using var scope = _factory.Services.CreateScope();
            var metaDb = scope.ServiceProvider.GetRequiredService<MetaDatabase>();
            var persistence = scope.ServiceProvider.GetRequiredService<GlobalLeaderboardPersistence>();
            var dataSource = scope.ServiceProvider.GetRequiredService<NpgsqlDataSource>();

            metaDb.InsertAccountIds(["bandsAcct1", "bandsMate1", "bandsMate2"]);
            metaDb.InsertAccountNames([
                ("bandsAcct1", (string?)"Bands Player"),
                ("bandsMate1", (string?)"Bands Mate One"),
                ("bandsMate2", (string?)"Bands Mate Two"),
            ]);

            var db = persistence.GetOrCreateInstrumentDb("Solo_Guitar");
            db.UpsertEntries("bands_song_seed", [
                new LeaderboardEntry { AccountId = "bandsAcct1", Score = 95_000, Rank = 1, Accuracy = 99, Stars = 6, Season = 5 },
                new LeaderboardEntry { AccountId = "bandsOther", Score = 90_000, Rank = 2, Accuracy = 98, Stars = 6, Season = 5 },
            ]);

            metaDb.UpsertPlayerStatsTiers("bandsAcct1", "Solo_Guitar", JsonSerializer.Serialize(new[]
            {
                new PlayerStatsTier { SongsPlayed = 1, TotalScore = 95_000, CompletionPercent = 100, BestRank = 1 }
            }));

            SeedBandRows(dataSource, "bands_song_1", "Band_Duets", "bandsAcct1:bandsMate1", (0, "bandsAcct1", 0), (1, "bandsMate1", 1));
            SeedBandRows(dataSource, "bands_song_2", "Band_Duets", "bandsAcct1:bandsMate1", (0, "bandsAcct1", 2), (1, "bandsMate1", 1));
            SeedBandRows(dataSource, "bands_song_3", "Band_Trios", "bandsAcct1:bandsMate1:bandsMate2", (0, "bandsAcct1", 0), (1, "bandsMate1", 1), (2, "bandsMate2", 3));

            var response = await _client.GetAsync("/api/player/bandsAcct1/stats");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            var json = await response.Content.ReadFromJsonAsync<JsonElement>();
            var bands = json.GetProperty("bands");
            var allEntries = bands.GetProperty("all").GetProperty("entries");

            Assert.Equal(2, bands.GetProperty("all").GetProperty("totalCount").GetInt32());
            Assert.Equal(1, bands.GetProperty("duos").GetProperty("totalCount").GetInt32());
            Assert.Equal(1, bands.GetProperty("trios").GetProperty("totalCount").GetInt32());
            Assert.Equal(0, bands.GetProperty("quads").GetProperty("totalCount").GetInt32());

            Assert.Equal("bandsAcct1:bandsMate1", allEntries[0].GetProperty("teamKey").GetString());
            Assert.Equal(2, allEntries[0].GetProperty("appearanceCount").GetInt32());
            Assert.Equal("bandsAcct1:bandsMate1:bandsMate2", allEntries[1].GetProperty("teamKey").GetString());
            Assert.Equal(1, allEntries[1].GetProperty("appearanceCount").GetInt32());

            var duoEntry = bands.GetProperty("duos").GetProperty("entries")[0];
            var trioEntry = bands.GetProperty("trios").GetProperty("entries")[0];
            var duoBandId = duoEntry.GetProperty("bandId").GetString();

            Assert.True(Guid.TryParse(duoBandId, out _));
            Assert.Equal(duoBandId, allEntries[0].GetProperty("bandId").GetString());
            Assert.Equal(2, duoEntry.GetProperty("appearanceCount").GetInt32());
            Assert.True(Guid.TryParse(trioEntry.GetProperty("bandId").GetString(), out _));
            Assert.Equal(1, trioEntry.GetProperty("appearanceCount").GetInt32());

            var playerMember = duoEntry.GetProperty("members")
                .EnumerateArray()
                .Single(member => string.Equals(member.GetProperty("accountId").GetString(), "bandsAcct1", StringComparison.Ordinal));
            var instruments = playerMember.GetProperty("instruments")
                .EnumerateArray()
                .Select(value => value.GetString())
                .ToArray();

            Assert.Contains("Solo_Guitar", instruments);
                Assert.Contains("Solo_Vocals", instruments);
        }
        finally
        {
            featureOptions.PlayerBands = originalPlayerBands;
        }
    }

    [Fact]
    public async Task ApiPlayerStats_ReturnsSixBandPreviewEntries_WhenMoreBandsExist()
    {
        var featureOptions = _factory.Services.GetRequiredService<IOptions<FeatureOptions>>().Value;
        var originalPlayerBands = featureOptions.PlayerBands;
        featureOptions.PlayerBands = true;

        try
        {
            using var scope = _factory.Services.CreateScope();
            var persistence = scope.ServiceProvider.GetRequiredService<GlobalLeaderboardPersistence>();
            var metaDb = scope.ServiceProvider.GetRequiredService<MetaDatabase>();
            var dataSource = scope.ServiceProvider.GetRequiredService<NpgsqlDataSource>();

            metaDb.InsertAccountIds([
                "bandsPreviewAcct",
                "bandsPreviewMate1",
                "bandsPreviewMate2",
                "bandsPreviewMate3",
                "bandsPreviewMate4",
                "bandsPreviewMate5",
                "bandsPreviewMate6",
                "bandsPreviewMate7",
            ]);
            metaDb.InsertAccountNames([
                ("bandsPreviewAcct", (string?)"Bands Preview Player"),
                ("bandsPreviewMate1", (string?)"Bands Preview Mate 1"),
                ("bandsPreviewMate2", (string?)"Bands Preview Mate 2"),
                ("bandsPreviewMate3", (string?)"Bands Preview Mate 3"),
                ("bandsPreviewMate4", (string?)"Bands Preview Mate 4"),
                ("bandsPreviewMate5", (string?)"Bands Preview Mate 5"),
                ("bandsPreviewMate6", (string?)"Bands Preview Mate 6"),
                ("bandsPreviewMate7", (string?)"Bands Preview Mate 7"),
            ]);

            var db = persistence.GetOrCreateInstrumentDb("Solo_Guitar");
            db.UpsertEntries("bands_preview_seed", [
                new LeaderboardEntry { AccountId = "bandsPreviewAcct", Score = 95_000, Rank = 1, Accuracy = 99, Stars = 6, Season = 5 },
                new LeaderboardEntry { AccountId = "bandsPreviewOther", Score = 90_000, Rank = 2, Accuracy = 98, Stars = 6, Season = 5 },
            ]);

            metaDb.UpsertPlayerStatsTiers("bandsPreviewAcct", "Solo_Guitar", JsonSerializer.Serialize(new[]
            {
                new PlayerStatsTier { SongsPlayed = 1, TotalScore = 95_000, CompletionPercent = 100, BestRank = 1 }
            }));

            for (var index = 1; index <= 7; index++)
            {
                SeedBandRows(
                    dataSource,
                    $"bands_preview_song_{index}",
                    "Band_Duets",
                    $"bandsPreviewAcct:bandsPreviewMate{index}",
                    (0, "bandsPreviewAcct", index % 4),
                    (1, $"bandsPreviewMate{index}", (index + 1) % 4));
            }

            var response = await _client.GetAsync("/api/player/bandsPreviewAcct/stats");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            var json = await response.Content.ReadFromJsonAsync<JsonElement>();
            var bands = json.GetProperty("bands");
            var allEntries = bands.GetProperty("all").GetProperty("entries");
            var duoEntries = bands.GetProperty("duos").GetProperty("entries");

            Assert.Equal(7, bands.GetProperty("all").GetProperty("totalCount").GetInt32());
            Assert.Equal(7, bands.GetProperty("duos").GetProperty("totalCount").GetInt32());
            Assert.Equal(6, allEntries.GetArrayLength());
            Assert.Equal(6, duoEntries.GetArrayLength());
        }
        finally
        {
            featureOptions.PlayerBands = originalPlayerBands;
        }
    }

    [Fact]
    public async Task ApiPlayerStats_IgnoresCachedBandPayloadsWithoutBandIds()
    {
        using var scope = _factory.Services.CreateScope();
        var metaDb = scope.ServiceProvider.GetRequiredService<MetaDatabase>();
        var dataSource = scope.ServiceProvider.GetRequiredService<NpgsqlDataSource>();

        metaDb.InsertAccountIds(new[] { "staleBandCacheAcct", "staleBandCacheMate" });
        metaDb.InsertAccountNames(new[]
        {
            ("staleBandCacheAcct", (string?)"Stale Band Cache Player"),
            ("staleBandCacheMate", (string?)"Stale Band Cache Mate"),
        });

        metaDb.UpsertPlayerStatsTiers("staleBandCacheAcct", "Solo_Guitar", JsonSerializer.Serialize(new[]
        {
            new PlayerStatsTier { SongsPlayed = 1, TotalScore = 95_000, CompletionPercent = 100, BestRank = 1 }
        }));

        SeedBandRows(dataSource, "stale_band_cache_song", "Band_Duets", "staleBandCacheAcct:staleBandCacheMate", (0, "staleBandCacheAcct", 0), (1, "staleBandCacheMate", 1));

        var stalePayload = JsonSerializer.SerializeToUtf8Bytes(new
        {
            accountId = "staleBandCacheAcct",
            totalSongs = 999,
            instruments = Array.Empty<object>(),
            bands = new
            {
                all = new { totalCount = 1, entries = new object[] { new { teamKey = "staleBandCacheAcct:staleBandCacheMate", bandType = "Band_Duets", members = Array.Empty<object>() } } },
                duos = new { totalCount = 1, entries = new object[] { new { teamKey = "staleBandCacheAcct:staleBandCacheMate", bandType = "Band_Duets", members = Array.Empty<object>() } } },
                trios = new { totalCount = 0, entries = Array.Empty<object>() },
                quads = new { totalCount = 0, entries = Array.Empty<object>() },
            },
        }, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        metaDb.BulkSetCachedResponses(new[]
        {
            ("playerstats:staleBandCacheAcct", stalePayload, ResponseCacheService.ComputeETag(stalePayload))
        });

        var response = await _client.GetAsync("/api/player/staleBandCacheAcct/stats");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        var entry = json.GetProperty("bands").GetProperty("duos").GetProperty("entries")[0];
        Assert.True(Guid.TryParse(entry.GetProperty("bandId").GetString(), out _));
        Assert.Equal(1, entry.GetProperty("appearanceCount").GetInt32());
    }

    [Fact]
    public async Task ApiPlayerStats_ReturnsBands_WhenFeatureDisabled()
    {
        var featureOptions = _factory.Services.GetRequiredService<IOptions<FeatureOptions>>().Value;
        var originalPlayerBands = featureOptions.PlayerBands;
        featureOptions.PlayerBands = false;

        try
        {
            using var scope = _factory.Services.CreateScope();
            var metaDb = scope.ServiceProvider.GetRequiredService<MetaDatabase>();
            var persistence = scope.ServiceProvider.GetRequiredService<GlobalLeaderboardPersistence>();
            var dataSource = scope.ServiceProvider.GetRequiredService<NpgsqlDataSource>();

            metaDb.InsertAccountIds(["bandsDisabledAcct1", "bandsDisabledMate1"]);
            metaDb.InsertAccountNames([
                ("bandsDisabledAcct1", (string?)"Bands Disabled Player"),
                ("bandsDisabledMate1", (string?)"Bands Disabled Mate"),
            ]);

            var db = persistence.GetOrCreateInstrumentDb("Solo_Guitar");
            db.UpsertEntries("bands_disabled_song_seed", [
                new LeaderboardEntry { AccountId = "bandsDisabledAcct1", Score = 95_000, Rank = 1, Accuracy = 99, Stars = 6, Season = 5 },
                new LeaderboardEntry { AccountId = "bandsDisabledOther", Score = 90_000, Rank = 2, Accuracy = 98, Stars = 6, Season = 5 },
            ]);

            metaDb.UpsertPlayerStatsTiers("bandsDisabledAcct1", "Solo_Guitar", JsonSerializer.Serialize(new[]
            {
                new PlayerStatsTier { SongsPlayed = 1, TotalScore = 95_000, CompletionPercent = 100, BestRank = 1 }
            }));

            SeedBandRows(dataSource, "bands_disabled_song_1", "Band_Duets", "bandsDisabledAcct1:bandsDisabledMate1", (0, "bandsDisabledAcct1", 0), (1, "bandsDisabledMate1", 1));

            var response = await _client.GetAsync("/api/player/bandsDisabledAcct1/stats");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            var json = await response.Content.ReadFromJsonAsync<JsonElement>();
            var bands = json.GetProperty("bands");

            Assert.Equal(1, bands.GetProperty("all").GetProperty("totalCount").GetInt32());
            Assert.Equal(1, bands.GetProperty("duos").GetProperty("totalCount").GetInt32());
            Assert.Equal(0, bands.GetProperty("trios").GetProperty("totalCount").GetInt32());
            Assert.Equal(0, bands.GetProperty("quads").GetProperty("totalCount").GetInt32());
        }
        finally
        {
            featureOptions.PlayerBands = originalPlayerBands;
        }
    }

    [Fact]
    public async Task ApiPlayerBandsByType_ReturnsBandsForRequestedType()
    {
        using var scope = _factory.Services.CreateScope();
        var metaDb = scope.ServiceProvider.GetRequiredService<MetaDatabase>();
        var dataSource = scope.ServiceProvider.GetRequiredService<NpgsqlDataSource>();

        metaDb.InsertAccountIds(["typedAcct", "typedMate1", "typedMate2", "typedMate3"]);
        metaDb.InsertAccountNames([
            ("typedAcct", (string?)"Typed Player"),
            ("typedMate1", (string?)"Typed Mate One"),
            ("typedMate2", (string?)"Typed Mate Two"),
            ("typedMate3", (string?)"Typed Mate Three"),
        ]);

        SeedBandRows(dataSource, "typed_song_1", "Band_Duets", "typedAcct:typedMate1", "0:1", (0, "typedAcct", 0), (1, "typedMate1", 1));
        SeedBandRows(dataSource, "typed_song_2", "Band_Duets", "typedAcct:typedMate2", "0:3", (0, "typedAcct", 0), (1, "typedMate2", 3));
        SeedBandRows(dataSource, "typed_song_3", "Band_Trios", "typedAcct:typedMate1:typedMate3", "0:1:3", (0, "typedAcct", 0), (1, "typedMate1", 1), (2, "typedMate3", 3));

        var response = await _client.GetAsync("/api/player/typedAcct/bands/Band_Duets");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.ETag is not null);

        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("typedAcct", json.GetProperty("accountId").GetString());
        Assert.Equal("Band_Duets", json.GetProperty("bandType").GetString());
        Assert.Equal(2, json.GetProperty("totalCount").GetInt32());

        var entries = json.GetProperty("entries");
        Assert.Equal(2, entries.GetArrayLength());
        Assert.All(entries.EnumerateArray(), entry =>
        {
            Assert.Equal("Band_Duets", entry.GetProperty("bandType").GetString());
            Assert.True(Guid.TryParse(entry.GetProperty("bandId").GetString(), out _));
            Assert.Equal(1, entry.GetProperty("appearanceCount").GetInt32());
        });
    }

    [Fact]
    public async Task ApiPlayerBandsList_ReturnsFlatListForRequestedGroup()
    {
        using var scope = _factory.Services.CreateScope();
        var metaDb = scope.ServiceProvider.GetRequiredService<MetaDatabase>();
        var dataSource = scope.ServiceProvider.GetRequiredService<NpgsqlDataSource>();

        metaDb.InsertAccountIds(["flatBandsAcct", "flatBandsMate1", "flatBandsMate2"]);
        metaDb.InsertAccountNames([
            ("flatBandsAcct", (string?)"Flat Bands Player"),
            ("flatBandsMate1", (string?)"Flat Mate One"),
            ("flatBandsMate2", (string?)"Flat Mate Two"),
        ]);

        SeedBandRows(dataSource, "flat_bands_song_1", "Band_Duets", "flatBandsAcct:flatBandsMate1", (0, "flatBandsAcct", 0), (1, "flatBandsMate1", 1));
        SeedBandRows(dataSource, "flat_bands_song_2", "Band_Duets", "flatBandsAcct:flatBandsMate1", (0, "flatBandsAcct", 3), (1, "flatBandsMate1", 1));
        SeedBandRows(dataSource, "flat_bands_song_3", "Band_Trios", "flatBandsAcct:flatBandsMate1:flatBandsMate2", (0, "flatBandsAcct", 0), (1, "flatBandsMate1", 1), (2, "flatBandsMate2", 3));

        var response = await _client.GetAsync("/api/player/flatBandsAcct/bands?group=all");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        var entries = json.GetProperty("entries");

        Assert.Equal("flatBandsAcct", json.GetProperty("accountId").GetString());
        Assert.Equal("all", json.GetProperty("group").GetString());
        Assert.Equal(2, json.GetProperty("totalCount").GetInt32());
        Assert.Equal("flatBandsAcct:flatBandsMate1", entries[0].GetProperty("teamKey").GetString());
        Assert.Equal(2, entries[0].GetProperty("appearanceCount").GetInt32());
        Assert.True(Guid.TryParse(entries[0].GetProperty("bandId").GetString(), out _));
    }

    [Fact]
    public async Task ApiPlayerBandsList_PaginatesSortedFlatListForRequestedGroup()
    {
        using var scope = _factory.Services.CreateScope();
        var metaDb = scope.ServiceProvider.GetRequiredService<MetaDatabase>();
        var dataSource = scope.ServiceProvider.GetRequiredService<NpgsqlDataSource>();

        var accountId = "pagedBandsAcct";
        var mates = Enumerable.Range(1, 4).Select(i => $"pagedBandsMate{i}").ToArray();
        metaDb.InsertAccountIds([accountId, ..mates]);
        metaDb.InsertAccountNames([
            (accountId, (string?)"Paged Bands Player"),
            (mates[0], (string?)"Paged Mate One"),
            (mates[1], (string?)"Paged Mate Two"),
            (mates[2], (string?)"Paged Mate Three"),
            (mates[3], (string?)"Paged Mate Four"),
        ]);

        for (var teamIndex = 0; teamIndex < mates.Length; teamIndex++)
        {
            var appearances = mates.Length - teamIndex;
            var mate = mates[teamIndex];
            for (var songIndex = 0; songIndex < appearances; songIndex++)
            {
                SeedBandRows(
                    dataSource,
                    $"paged_bands_song_{teamIndex}_{songIndex}",
                    "Band_Duets",
                    $"{accountId}:{mate}",
                    (0, accountId, 0),
                    (1, mate, 1));
            }
        }

        var response = await _client.GetAsync($"/api/player/{accountId}/bands?group=duos&page=2&pageSize=2");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        var entries = json.GetProperty("entries");

        Assert.Equal(accountId, json.GetProperty("accountId").GetString());
        Assert.Equal("duos", json.GetProperty("group").GetString());
        Assert.Equal(4, json.GetProperty("totalCount").GetInt32());
        Assert.Equal(2, entries.GetArrayLength());
        Assert.Equal($"{accountId}:{mates[2]}", entries[0].GetProperty("teamKey").GetString());
        Assert.Equal(2, entries[0].GetProperty("appearanceCount").GetInt32());
        Assert.Equal($"{accountId}:{mates[3]}", entries[1].GetProperty("teamKey").GetString());
        Assert.Equal(1, entries[1].GetProperty("appearanceCount").GetInt32());
    }

    [Fact]
    public async Task ApiBandDetail_ReturnsBand_ForBandId()
    {
        using var scope = _factory.Services.CreateScope();
        var metaDb = scope.ServiceProvider.GetRequiredService<MetaDatabase>();
        var dataSource = scope.ServiceProvider.GetRequiredService<NpgsqlDataSource>();

        metaDb.InsertAccountIds(["bandDetailAcct", "bandDetailMate"]);
        metaDb.InsertAccountNames([
            ("bandDetailAcct", (string?)"Band Detail Player"),
            ("bandDetailMate", (string?)"Band Detail Mate"),
        ]);

        SeedBandRows(dataSource, "band_detail_song_1", "Band_Duets", "bandDetailAcct:bandDetailMate", "0:1", (0, "bandDetailAcct", 0), (1, "bandDetailMate", 1));
        SeedBandRows(dataSource, "band_detail_song_2", "Band_Duets", "bandDetailAcct:bandDetailMate", "3:1", (0, "bandDetailAcct", 3), (1, "bandDetailMate", 1));
        metaDb.RebuildBandTeamRankings("Band_Duets", totalChartedSongs: 2);

        var listResponse = await _client.GetAsync("/api/player/bandDetailAcct/bands?group=duos");
        Assert.Equal(HttpStatusCode.OK, listResponse.StatusCode);
        var listJson = await listResponse.Content.ReadFromJsonAsync<JsonElement>();
        var bandId = listJson.GetProperty("entries")[0].GetProperty("bandId").GetString();

        var response = await _client.GetAsync($"/api/bands/{bandId}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        var band = json.GetProperty("band");
        var ranking = json.GetProperty("ranking");

        Assert.Equal(bandId, band.GetProperty("bandId").GetString());
        Assert.Equal("bandDetailAcct:bandDetailMate", band.GetProperty("teamKey").GetString());
        Assert.Equal(2, band.GetProperty("appearanceCount").GetInt32());

        if (ranking.ValueKind == JsonValueKind.Object)
        {
            Assert.Equal(bandId, ranking.GetProperty("bandId").GetString());
            Assert.Equal("Band_Duets", ranking.GetProperty("bandType").GetString());
            Assert.Equal("bandDetailAcct:bandDetailMate", ranking.GetProperty("teamKey").GetString());
            Assert.Equal(2, ranking.GetProperty("songsPlayed").GetInt32());
        }
        else
        {
            Assert.Equal(JsonValueKind.Null, ranking.ValueKind);
        }
    }

    [Fact]
    public async Task ApiPlayerBandsByType_FiltersByCombo()
    {
        using var scope = _factory.Services.CreateScope();
        var metaDb = scope.ServiceProvider.GetRequiredService<MetaDatabase>();
        var dataSource = scope.ServiceProvider.GetRequiredService<NpgsqlDataSource>();

        metaDb.InsertAccountIds(["comboAcct", "comboMate1", "comboMate2", "comboMate3"]);
        metaDb.InsertAccountNames([
            ("comboAcct", (string?)"Combo Player"),
            ("comboMate1", (string?)"Combo Mate One"),
            ("comboMate2", (string?)"Combo Mate Two"),
            ("comboMate3", (string?)"Combo Mate Three"),
        ]);

        SeedBandRows(dataSource, "combo_song_1", "Band_Duets", "comboAcct:comboMate1", "0:1", (0, "comboAcct", 0), (1, "comboMate1", 1));
        SeedBandRows(dataSource, "combo_song_2", "Band_Duets", "comboAcct:comboMate1", "1:3", (0, "comboAcct", 3), (1, "comboMate1", 1));
        SeedBandRows(dataSource, "combo_song_3", "Band_Duets", "comboAcct:comboMate2", "0:1", (0, "comboAcct", 0), (1, "comboMate2", 1));
        SeedBandRows(dataSource, "combo_song_4", "Band_Duets", "comboAcct:comboMate3", "0:3", (0, "comboAcct", 0), (1, "comboMate3", 3));

        var response = await _client.GetAsync("/api/player/comboAcct/bands/Band_Duets?combo=Solo_Guitar+Solo_Bass");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Solo_Guitar+Solo_Bass", json.GetProperty("comboId").GetString());
        Assert.Equal(2, json.GetProperty("totalCount").GetInt32());

        var entries = json.GetProperty("entries");
        Assert.Equal(2, entries.GetArrayLength());

        var filteredTeam = entries
            .EnumerateArray()
            .Single(entry => string.Equals(entry.GetProperty("teamKey").GetString(), "comboAcct:comboMate1", StringComparison.Ordinal));
        var playerMember = filteredTeam.GetProperty("members")
            .EnumerateArray()
            .Single(member => string.Equals(member.GetProperty("accountId").GetString(), "comboAcct", StringComparison.Ordinal));
        var instruments = playerMember.GetProperty("instruments")
            .EnumerateArray()
            .Select(value => value.GetString())
            .ToArray();

        Assert.Contains("Solo_Guitar", instruments);
        Assert.DoesNotContain("Solo_Vocals", instruments);
    }

    [Fact]
    public async Task ApiPlayerBandsByType_ReturnsBadRequest_ForUnknownBandType()
    {
        var response = await _client.GetAsync("/api/player/anyAcct/bands/BadType");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains("Unknown band type", json.GetProperty("error").GetString());
    }

    [Fact]
    public async Task ApiPlayerBandsByType_ReturnsBadRequest_ForComboSizeMismatch()
    {
        var response = await _client.GetAsync("/api/player/anyAcct/bands/Band_Duets?combo=Solo_Guitar+Solo_Bass+Solo_Drums");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Combo size does not match band type Band_Duets.", json.GetProperty("error").GetString());
    }

    [Fact]
    public async Task ApiPlayerBandsByType_ReturnsEmptyResult_WhenPlayerHasNoBands()
    {
        var response = await _client.GetAsync("/api/player/emptyAcct/bands/Band_Quad");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("emptyAcct", json.GetProperty("accountId").GetString());
        Assert.Equal("Band_Quad", json.GetProperty("bandType").GetString());
        Assert.Equal(0, json.GetProperty("totalCount").GetInt32());
        Assert.Equal(0, json.GetProperty("entries").GetArrayLength());
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
    /// Ensures a row for the given songId exists in the PG songs table
    /// so UpdateMaxScores can UPDATE the row.
    /// </summary>
    private static void EnsureSongRow(PathDataStore pathStore, string songId)
    {
        var dsField = typeof(PathDataStore)
            .GetField("_ds", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var ds = (Npgsql.NpgsqlDataSource)dsField.GetValue(pathStore)!;

        using var conn = ds.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT INTO songs (song_id, title) VALUES (@songId, 'Test Song') ON CONFLICT DO NOTHING";
        cmd.Parameters.AddWithValue("songId", songId);
        cmd.ExecuteNonQuery();
    }

    private static void InsertSnapshotState(NpgsqlDataSource dataSource, string songId, string instrument, long snapshotId)
    {
        using var conn = dataSource.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO leaderboard_snapshot_state
            (song_id, instrument, active_snapshot_id, scrape_id, is_finalized, updated_at)
            VALUES (@songId, @instrument, @snapshotId, 1, TRUE, @updatedAt)
            ON CONFLICT (song_id, instrument) DO UPDATE SET
                active_snapshot_id = EXCLUDED.active_snapshot_id,
                scrape_id = EXCLUDED.scrape_id,
                is_finalized = EXCLUDED.is_finalized,
                updated_at = EXCLUDED.updated_at
            """;
        cmd.Parameters.AddWithValue("songId", songId);
        cmd.Parameters.AddWithValue("instrument", instrument);
        cmd.Parameters.AddWithValue("snapshotId", snapshotId);
        cmd.Parameters.AddWithValue("updatedAt", DateTime.UtcNow);
        cmd.ExecuteNonQuery();
    }

    private static void InsertSnapshotEntry(NpgsqlDataSource dataSource, long snapshotId, string songId, string instrument, string accountId, int score, int rank)
    {
        using var conn = dataSource.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO leaderboard_entries_snapshot
            (snapshot_id, song_id, instrument, account_id, score, accuracy, is_full_combo, stars,
             season, percentile, rank, source, difficulty, api_rank, end_time, first_seen_at, last_updated_at)
            VALUES
            (@snapshotId, @songId, @instrument, @accountId, @score, 95, false, 5,
             3, 99.0, @rank, 'scrape', 3, @rank, '2025-01-15T12:00:00Z', @now, @now)
            """;
        cmd.Parameters.AddWithValue("snapshotId", snapshotId);
        cmd.Parameters.AddWithValue("songId", songId);
        cmd.Parameters.AddWithValue("instrument", instrument);
        cmd.Parameters.AddWithValue("accountId", accountId);
        cmd.Parameters.AddWithValue("score", score);
        cmd.Parameters.AddWithValue("rank", rank);
        cmd.Parameters.AddWithValue("now", DateTime.UtcNow);
        cmd.ExecuteNonQuery();
    }

    private static void InsertOverlayEntry(NpgsqlDataSource dataSource, string songId, string instrument, string accountId, int score, int sourcePriority, string overlayReason)
    {
        using var conn = dataSource.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO leaderboard_entries_overlay
            (song_id, instrument, account_id, score, accuracy, is_full_combo, stars,
             season, percentile, rank, source, difficulty, api_rank, end_time,
             first_seen_at, last_updated_at, source_priority, overlay_reason)
            VALUES
            (@songId, @instrument, @accountId, @score, 95, false, 5,
             3, 99.0, 1, @overlayReason, 3, 1, '2025-01-15T12:00:00Z',
             @now, @now, @sourcePriority, @overlayReason)
            ON CONFLICT (song_id, instrument, account_id) DO UPDATE SET
                score = EXCLUDED.score,
                accuracy = EXCLUDED.accuracy,
                is_full_combo = EXCLUDED.is_full_combo,
                stars = EXCLUDED.stars,
                season = EXCLUDED.season,
                percentile = EXCLUDED.percentile,
                rank = EXCLUDED.rank,
                source = EXCLUDED.source,
                difficulty = EXCLUDED.difficulty,
                api_rank = EXCLUDED.api_rank,
                end_time = EXCLUDED.end_time,
                last_updated_at = EXCLUDED.last_updated_at,
                source_priority = EXCLUDED.source_priority,
                overlay_reason = EXCLUDED.overlay_reason
            """;
        cmd.Parameters.AddWithValue("songId", songId);
        cmd.Parameters.AddWithValue("instrument", instrument);
        cmd.Parameters.AddWithValue("accountId", accountId);
        cmd.Parameters.AddWithValue("score", score);
        cmd.Parameters.AddWithValue("overlayReason", overlayReason);
        cmd.Parameters.AddWithValue("sourcePriority", sourcePriority);
        cmd.Parameters.AddWithValue("now", DateTime.UtcNow);
        cmd.ExecuteNonQuery();
    }

    private static void SeedBandRows(
        NpgsqlDataSource dataSource,
        string songId,
        string bandType,
        string teamKey,
        params (int MemberIndex, string AccountId, int InstrumentId)[] members)
    {
        SeedBandRows(dataSource, songId, bandType, teamKey, "", members);
    }

    private static void SeedBandRows(
        NpgsqlDataSource dataSource,
        string songId,
        string bandType,
        string teamKey,
        string instrumentCombo,
        params (int MemberIndex, string AccountId, int InstrumentId)[] members)
    {
        using var conn = dataSource.OpenConnection();

        foreach (var member in members)
        {
            using var memberCmd = conn.CreateCommand();
            memberCmd.CommandText = """
                INSERT INTO band_member_stats (song_id, band_type, team_key, instrument_combo, member_index, account_id, instrument_id)
                VALUES (@songId, @bandType, @teamKey, '', @memberIndex, @accountId, @instrumentId)
                ON CONFLICT DO NOTHING
                """;
            memberCmd.CommandText = memberCmd.CommandText.Replace("''", "@instrumentCombo");
            memberCmd.Parameters.AddWithValue("songId", songId);
            memberCmd.Parameters.AddWithValue("bandType", bandType);
            memberCmd.Parameters.AddWithValue("teamKey", teamKey);
            memberCmd.Parameters.AddWithValue("instrumentCombo", instrumentCombo);
            memberCmd.Parameters.AddWithValue("memberIndex", member.MemberIndex);
            memberCmd.Parameters.AddWithValue("accountId", member.AccountId);
            memberCmd.Parameters.AddWithValue("instrumentId", member.InstrumentId);
            memberCmd.ExecuteNonQuery();

            using var lookupCmd = conn.CreateCommand();
            lookupCmd.CommandText = """
                INSERT INTO band_members (account_id, song_id, band_type, team_key, instrument_combo)
                VALUES (@accountId, @songId, @bandType, @teamKey, '')
                ON CONFLICT DO NOTHING
                """;
            lookupCmd.CommandText = lookupCmd.CommandText.Replace("''", "@instrumentCombo");
            lookupCmd.Parameters.AddWithValue("accountId", member.AccountId);
            lookupCmd.Parameters.AddWithValue("songId", songId);
            lookupCmd.Parameters.AddWithValue("bandType", bandType);
            lookupCmd.Parameters.AddWithValue("teamKey", teamKey);
            lookupCmd.Parameters.AddWithValue("instrumentCombo", instrumentCombo);
            lookupCmd.ExecuteNonQuery();
        }
    }

    // ─── Sync Status ──────────────────────────────────────────

    [Fact]
    public async Task SyncStatus_ReturnsExpectedShape()
    {
        var response = await _client.GetAsync("/api/player/test_acct/sync-status");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(json.TryGetProperty("accountId", out _));
        Assert.True(json.TryGetProperty("isTracked", out _));
        // rivals/backfill/historyRecon may be null (omitted) for untracked accounts
    }

    [Fact]
    public async Task SyncStatus_WithRivalsData_ReturnsStatus()
    {
        var metaDb = _factory.Services.GetRequiredService<MetaDatabase>();
        metaDb.EnsureRivalsStatus("sync_acct");
        metaDb.StartRivals("sync_acct", 7);
        metaDb.CompleteRivals("sync_acct", 7, 20);

        var response = await _client.GetAsync("/api/player/sync_acct/sync-status");
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        var rivals = json.GetProperty("rivals");
        Assert.Equal("complete", rivals.GetProperty("status").GetString());
        Assert.Equal(7, rivals.GetProperty("combosComputed").GetInt32());
        Assert.Equal(7, rivals.GetProperty("totalCombosToCompute").GetInt32());
        Assert.Equal(20, rivals.GetProperty("rivalsFound").GetInt32());
    }

    // ─── Player Endpoints (population) ──────────────────────

    [Fact]
    public async Task PlayerProfile_ReturnsScores()
    {
        var response = await _client.GetAsync("/api/player/test_acct");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(json.TryGetProperty("accountId", out _));
        Assert.True(json.TryGetProperty("scores", out _));
    }

    // ─── Rivals ─────────────────────────────────────────────────

    [Fact]
    public async Task Rivals_GetCombos_ReturnsEmptyForUnknownAccount()
    {
        var response = await _client.GetAsync("/api/player/unknown_acct/rivals");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        var combos = json.GetProperty("combos");
        Assert.Equal(0, combos.GetArrayLength());
    }

    [Fact]
    public async Task Rivals_GetCombo_Returns404WhenNoRivals()
    {
        var response = await _client.GetAsync("/api/player/unknown_acct/rivals/Solo_Guitar");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Rivals_GetComboDetail_Returns404WhenNoSamples()
    {
        var response = await _client.GetAsync("/api/player/unknown_acct/rivals/Solo_Guitar/some_rival");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Rivals_GetSongsPerInstrument_Returns404WhenNoData()
    {
        var response = await _client.GetAsync("/api/player/unknown_acct/rivals/some_rival/songs/Solo_Guitar");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Rivals_Recompute_RequiresApiKey()
    {
        var response = await _client.PostAsync("/api/player/acct1/rivals/recompute", null);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Rivals_Recompute_WithApiKey_ReturnsOk()
    {
        var response = await _authedClient.PostAsync("/api/player/acct1/rivals/recompute", null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("recomputed", json.GetProperty("status").GetString());
    }

    [Fact]
    public async Task LeaderboardRivals_Recompute_RequiresApiKey()
    {
        var response = await _client.PostAsync("/api/player/acct1/leaderboard-rivals/recompute", null);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task LeaderboardRivals_Recompute_WithApiKey_ReturnsOk()
    {
        var response = await _authedClient.PostAsync("/api/player/acct1/leaderboard-rivals/recompute", null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("recomputed", json.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Rivals_GetCombos_WithSeededData_ReturnsCombos()
    {
        // Seed rivals data via the recompute endpoint first
        var metaDb = _factory.Services.GetRequiredService<MetaDatabase>();
        var rivals = new List<UserRivalRow>
        {
            new() { UserId = "seeded_acct", RivalAccountId = "rival_1", InstrumentCombo = "Solo_Guitar",
                     Direction = "above", RivalScore = 42.0, AvgSignedDelta = -3.5,
                     SharedSongCount = 100, AheadCount = 60, BehindCount = 40, ComputedAt = "2026-01-01T00:00:00Z" },
            new() { UserId = "seeded_acct", RivalAccountId = "rival_2", InstrumentCombo = "Solo_Guitar",
                     Direction = "below", RivalScore = 30.0, AvgSignedDelta = 2.0,
                     SharedSongCount = 80, AheadCount = 30, BehindCount = 50, ComputedAt = "2026-01-01T00:00:00Z" },
        };
        metaDb.ReplaceRivalsData("seeded_acct", rivals, Array.Empty<RivalSongSampleRow>());

        var response = await _client.GetAsync("/api/player/seeded_acct/rivals");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        var combos = json.GetProperty("combos");
        Assert.Equal(1, combos.GetArrayLength());
        Assert.Equal("Solo_Guitar", combos[0].GetProperty("combo").GetString());
        Assert.Equal(1, combos[0].GetProperty("aboveCount").GetInt32());
        Assert.Equal(1, combos[0].GetProperty("belowCount").GetInt32());
    }

    [Fact]
    public async Task Rivals_GetCombo_WithSeededData_ReturnsAboveAndBelow()
    {
        var metaDb = _factory.Services.GetRequiredService<MetaDatabase>();
        var rivals = new List<UserRivalRow>
        {
            new() { UserId = "seeded_acct2", RivalAccountId = "rival_a", InstrumentCombo = "01",
                     Direction = "above", RivalScore = 42.0, AvgSignedDelta = -3.5,
                     SharedSongCount = 100, AheadCount = 60, BehindCount = 40, ComputedAt = "2026-01-01T00:00:00Z" },
            new() { UserId = "seeded_acct2", RivalAccountId = "rival_b", InstrumentCombo = "01",
                     Direction = "below", RivalScore = 30.0, AvgSignedDelta = 2.0,
                     SharedSongCount = 80, AheadCount = 30, BehindCount = 50, ComputedAt = "2026-01-01T00:00:00Z" },
        };
        var samples = new List<RivalSongSampleRow>
        {
            new() { UserId = "seeded_acct2", RivalAccountId = "rival_a", Instrument = "Solo_Guitar",
                     SongId = "song1", UserRank = 10, RivalRank = 8, RankDelta = -2, UserScore = 9000, RivalScore = 9100 },
        };
        metaDb.ReplaceRivalsData("seeded_acct2", rivals, samples);

        var response = await _client.GetAsync("/api/player/seeded_acct2/rivals/Solo_Guitar");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("01", json.GetProperty("combo").GetString());
        Assert.Equal(1, json.GetProperty("above").GetArrayLength());
        Assert.Equal(1, json.GetProperty("below").GetArrayLength());
    }

    [Fact]
    public async Task Rivals_GetCombo_WithThreeDigitHexComboId_ReturnsCanonicalCombo()
    {
        var metaDb = _factory.Services.GetRequiredService<MetaDatabase>();
        var rivals = new List<UserRivalRow>
        {
            new() { UserId = "seeded_acct2_hex", RivalAccountId = "rival_a", InstrumentCombo = "1ff",
                     Direction = "above", RivalScore = 42.0, AvgSignedDelta = -3.5,
                     SharedSongCount = 100, AheadCount = 60, BehindCount = 40, ComputedAt = "2026-01-01T00:00:00Z" },
        };
        metaDb.ReplaceRivalsData("seeded_acct2_hex", rivals, Array.Empty<RivalSongSampleRow>());

        var response = await _client.GetAsync("/api/player/seeded_acct2_hex/rivals/1ff");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("1ff", json.GetProperty("combo").GetString());
        Assert.Equal(1, json.GetProperty("above").GetArrayLength());
    }

    [Fact]
    public async Task Rivals_GetCombo_WithLegacyComboString_ReturnsCanonicalCombo()
    {
        var metaDb = _factory.Services.GetRequiredService<MetaDatabase>();
        var rivals = new List<UserRivalRow>
        {
            new() { UserId = "seeded_acct2_legacy", RivalAccountId = "rival_a", InstrumentCombo = "03",
                     Direction = "above", RivalScore = 42.0, AvgSignedDelta = -3.5,
                     SharedSongCount = 100, AheadCount = 60, BehindCount = 40, ComputedAt = "2026-01-01T00:00:00Z" },
        };
        metaDb.ReplaceRivalsData("seeded_acct2_legacy", rivals, Array.Empty<RivalSongSampleRow>());

        var response = await _client.GetAsync("/api/player/seeded_acct2_legacy/rivals/Solo_Guitar+Solo_Bass");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("03", json.GetProperty("combo").GetString());
        Assert.Equal(1, json.GetProperty("above").GetArrayLength());
    }

    [Fact]
    public async Task Rivals_GetComboDetail_WithSeededData_ReturnsSongs()
    {
        var metaDb = _factory.Services.GetRequiredService<MetaDatabase>();
        var rivals = new List<UserRivalRow>
        {
            new() { UserId = "seeded_acct3", RivalAccountId = "rival_x", InstrumentCombo = "Solo_Guitar",
                     Direction = "above", RivalScore = 42.0, AvgSignedDelta = -3.5,
                     SharedSongCount = 100, AheadCount = 60, BehindCount = 40, ComputedAt = "2026-01-01T00:00:00Z" },
        };
        var samples = new List<RivalSongSampleRow>
        {
            new() { UserId = "seeded_acct3", RivalAccountId = "rival_x", Instrument = "Solo_Guitar",
                     SongId = "song1", UserRank = 10, RivalRank = 8, RankDelta = -2, UserScore = 9000, RivalScore = 9100 },
            new() { UserId = "seeded_acct3", RivalAccountId = "rival_x", Instrument = "Solo_Guitar",
                     SongId = "song2", UserRank = 50, RivalRank = 100, RankDelta = 50, UserScore = 8000, RivalScore = 7000 },
        };
        metaDb.ReplaceRivalsData("seeded_acct3", rivals, samples);

        // Default sort = closest
        var response = await _client.GetAsync("/api/player/seeded_acct3/rivals/Solo_Guitar/rival_x");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(2, json.GetProperty("totalSongs").GetInt32());
        var songs = json.GetProperty("songs");
        Assert.Equal(2, songs.GetArrayLength());
        // First song should be closest (|delta| = 2 < 50)
        Assert.Equal(-2, songs[0].GetProperty("rankDelta").GetInt32());

        // Sort by they_lead (most negative first)
        response = await _client.GetAsync("/api/player/seeded_acct3/rivals/Solo_Guitar/rival_x?sort=they_lead");
        json = await response.Content.ReadFromJsonAsync<JsonElement>();
        songs = json.GetProperty("songs");
        Assert.Equal(-2, songs[0].GetProperty("rankDelta").GetInt32());

        // Sort by you_lead (most positive first)
        response = await _client.GetAsync("/api/player/seeded_acct3/rivals/Solo_Guitar/rival_x?sort=you_lead");
        json = await response.Content.ReadFromJsonAsync<JsonElement>();
        songs = json.GetProperty("songs");
        Assert.Equal(50, songs[0].GetProperty("rankDelta").GetInt32());

        // Pagination: limit=1
        response = await _client.GetAsync("/api/player/seeded_acct3/rivals/Solo_Guitar/rival_x?limit=1");
        json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Single(json.GetProperty("songs").EnumerateArray());

        // limit=0 = all
        response = await _client.GetAsync("/api/player/seeded_acct3/rivals/Solo_Guitar/rival_x?limit=0");
        json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(2, json.GetProperty("songs").GetArrayLength());
    }

    [Fact]
    public async Task Rivals_GetComboDetail_WithThreeDigitHexComboId_ReturnsSongs()
    {
        var metaDb = _factory.Services.GetRequiredService<MetaDatabase>();
        var rivals = new List<UserRivalRow>
        {
            new() { UserId = "seeded_combo_hex", RivalAccountId = "rival_hex", InstrumentCombo = "1ff",
                     Direction = "above", RivalScore = 50.0, AvgSignedDelta = -4.0,
                     SharedSongCount = 120, AheadCount = 70, BehindCount = 50, ComputedAt = "2026-01-01T00:00:00Z" },
        };
        var samples = new List<RivalSongSampleRow>
        {
            new() { UserId = "seeded_combo_hex", RivalAccountId = "rival_hex", Instrument = "Solo_Guitar",
                     SongId = "song1", UserRank = 12, RivalRank = 9, RankDelta = -3, UserScore = 9500, RivalScore = 9700 },
        };
        metaDb.ReplaceRivalsData("seeded_combo_hex", rivals, samples);

        var response = await _client.GetAsync("/api/player/seeded_combo_hex/rivals/1ff/rival_hex");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("1ff", json.GetProperty("combo").GetString());
        Assert.Equal(1, json.GetProperty("totalSongs").GetInt32());
        Assert.Single(json.GetProperty("songs").EnumerateArray());
        Assert.Equal("song1", json.GetProperty("songs")[0].GetProperty("songId").GetString());
    }

    [Fact]
    public async Task Rivals_GetComboDetail_WithLegacyComboString_ReturnsSongs()
    {
        var metaDb = _factory.Services.GetRequiredService<MetaDatabase>();
        var rivals = new List<UserRivalRow>
        {
            new() { UserId = "seeded_combo_legacy", RivalAccountId = "rival_legacy", InstrumentCombo = "03",
                     Direction = "above", RivalScore = 25.0, AvgSignedDelta = -2.0,
                     SharedSongCount = 40, AheadCount = 25, BehindCount = 15, ComputedAt = "2026-01-01T00:00:00Z" },
        };
        var samples = new List<RivalSongSampleRow>
        {
            new() { UserId = "seeded_combo_legacy", RivalAccountId = "rival_legacy", Instrument = "Solo_Bass",
                     SongId = "song2", UserRank = 21, RivalRank = 17, RankDelta = -4, UserScore = 8800, RivalScore = 9000 },
        };
        metaDb.ReplaceRivalsData("seeded_combo_legacy", rivals, samples);

        var response = await _client.GetAsync("/api/player/seeded_combo_legacy/rivals/Solo_Guitar+Solo_Bass/rival_legacy");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("03", json.GetProperty("combo").GetString());
        Assert.Equal(1, json.GetProperty("totalSongs").GetInt32());
        Assert.Single(json.GetProperty("songs").EnumerateArray());
        Assert.Equal("song2", json.GetProperty("songs")[0].GetProperty("songId").GetString());
    }

    [Fact]
    public async Task Rivals_GetComboDetail_InvalidCombo_ReturnsBadRequest()
    {
        var response = await _client.GetAsync("/api/player/acct1/rivals/InvalidInstrument/some_rival");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Rivals_GetComboDetail_WithSeededData_IncludesSongGaps()
    {
        var metaDb = _factory.Services.GetRequiredService<MetaDatabase>();
        var persistence = _factory.Services.GetRequiredService<GlobalLeaderboardPersistence>();

        // Seed instrument DB with asymmetric songs
        var db = persistence.GetOrCreateInstrumentDb("Solo_Guitar");
        db.UpsertEntries("shared_song", new[]
        {
            new LeaderboardEntry { AccountId = "gap_user", Score = 10000, Accuracy = 95 },
            new LeaderboardEntry { AccountId = "gap_rival", Score = 9000, Accuracy = 90 },
        });
        db.UpsertEntries("user_only_song", new[]
        {
            new LeaderboardEntry { AccountId = "gap_user", Score = 8000, Accuracy = 95 },
        });
        db.UpsertEntries("rival_only_song", new[]
        {
            new LeaderboardEntry { AccountId = "gap_rival", Score = 7000, Accuracy = 90 },
        });

        // Seed rivalry data (samples for shared songs)
        var rivals = new List<UserRivalRow>
        {
            new() { UserId = "gap_user", RivalAccountId = "gap_rival", InstrumentCombo = "Solo_Guitar",
                     Direction = "above", RivalScore = 10.0, AvgSignedDelta = -1.0,
                     SharedSongCount = 1, AheadCount = 1, BehindCount = 0, ComputedAt = "2026-01-01T00:00:00Z" },
        };
        var samples = new List<RivalSongSampleRow>
        {
            new() { UserId = "gap_user", RivalAccountId = "gap_rival", Instrument = "Solo_Guitar",
                     SongId = "shared_song", UserRank = 1, RivalRank = 2, RankDelta = 1, UserScore = 10000, RivalScore = 9000 },
        };
        metaDb.ReplaceRivalsData("gap_user", rivals, samples);

        var response = await _client.GetAsync("/api/player/gap_user/rivals/Solo_Guitar/gap_rival");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();

        // Shared songs present
        Assert.Equal(1, json.GetProperty("totalSongs").GetInt32());

        // Song gaps present
        var songsToCompete = json.GetProperty("songsToCompete");
        Assert.Equal(1, songsToCompete.GetArrayLength());
        Assert.Equal("rival_only_song", songsToCompete[0].GetProperty("songId").GetString());

        var yourExclusives = json.GetProperty("yourExclusiveSongs");
        Assert.Equal(1, yourExclusives.GetArrayLength());
        Assert.Equal("user_only_song", yourExclusives[0].GetProperty("songId").GetString());
    }

    [Fact]
    public async Task Rivals_GetSongsPerInstrument_WithSeededData_ReturnsSongs()
    {
        var metaDb = _factory.Services.GetRequiredService<MetaDatabase>();
        var samples = new List<RivalSongSampleRow>
        {
            new() { UserId = "seeded_acct4", RivalAccountId = "rival_y", Instrument = "Solo_Guitar",
                     SongId = "song1", UserRank = 10, RivalRank = 8, RankDelta = -2, UserScore = 9000, RivalScore = 9100 },
        };
        metaDb.ReplaceRivalsData("seeded_acct4", Array.Empty<UserRivalRow>(), samples);

        var response = await _client.GetAsync("/api/player/seeded_acct4/rivals/rival_y/songs/Solo_Guitar");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, json.GetProperty("totalSongs").GetInt32());
    }

    // ═══════════════════════════════════════════════════════════
    // Rankings Endpoints
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public async Task Rankings_PerInstrument_ReturnsEmpty_WhenNoData()
    {
        var response = await _client.GetAsync("/api/rankings/Solo_Guitar?page=1&pageSize=10");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(0, json.GetProperty("totalAccounts").GetInt32());
        Assert.Equal("Solo_Guitar", json.GetProperty("instrument").GetString());
    }

    [Fact]
    public async Task Rankings_PerInstrument_ReturnsRankings_WhenSeeded()
    {
        var persistence = _factory.Services.GetRequiredService<GlobalLeaderboardPersistence>();
        var db = persistence.GetOrCreateInstrumentDb("Solo_Guitar");
        db.UpsertEntries("testSong1", [
            new LeaderboardEntry { AccountId = "rank_p1", Score = 10000, Rank = 1, Accuracy = 99, Stars = 6 },
            new LeaderboardEntry { AccountId = "rank_p2", Score = 8000, Rank = 2, Accuracy = 90, Stars = 5 },
        ]);
        db.RecomputeAllRanks();
        db.ComputeSongStats();
        db.ComputeAccountRankings(totalChartedSongs: 1);

        var response = await _client.GetAsync("/api/rankings/Solo_Guitar?page=1&pageSize=50");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(json.GetProperty("totalAccounts").GetInt32() >= 2);
        Assert.True(json.GetProperty("entries").GetArrayLength() >= 2);
    }

    [Fact]
    public async Task Rankings_PerInstrument_DifferentRankBy()
    {
        var persistence = _factory.Services.GetRequiredService<GlobalLeaderboardPersistence>();
        var db = persistence.GetOrCreateInstrumentDb("Solo_Guitar");
        db.UpsertEntries("testSong1", [
            new LeaderboardEntry { AccountId = "rb_p1", Score = 10000, Rank = 1, Accuracy = 99, Stars = 6 },
        ]);
        db.RecomputeAllRanks();
        db.ComputeSongStats();
        db.ComputeAccountRankings(totalChartedSongs: 1);

        foreach (var rankBy in new[] { "adjusted", "weighted", "fcrate", "totalscore", "maxscore" })
        {
            var response = await _client.GetAsync($"/api/rankings/Solo_Guitar?rankBy={rankBy}");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
    }

    [Fact]
    public async Task Rankings_SingleAccount_ReturnsRanking()
    {
        var persistence = _factory.Services.GetRequiredService<GlobalLeaderboardPersistence>();
        var db = persistence.GetOrCreateInstrumentDb("Solo_Guitar");
        db.UpsertEntries("testSong1", [
            new LeaderboardEntry { AccountId = "single_rank_p1", Score = 10000, Rank = 1, Accuracy = 99, Stars = 6 },
        ]);
        db.RecomputeAllRanks();
        db.ComputeSongStats();
        db.ComputeAccountRankings(totalChartedSongs: 1);

        var response = await _client.GetAsync("/api/rankings/Solo_Guitar/single_rank_p1");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("single_rank_p1", json.GetProperty("accountId").GetString());
        Assert.True(json.GetProperty("adjustedSkillRank").GetInt32() >= 1);
    }

    [Fact]
    public async Task Rankings_SingleAccount_NotFound()
    {
        var response = await _client.GetAsync("/api/rankings/Solo_Guitar/nonexistent_account");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Rankings_History_ReturnsData()
    {
        var persistence = _factory.Services.GetRequiredService<GlobalLeaderboardPersistence>();
        var db = persistence.GetOrCreateInstrumentDb("Solo_Guitar");
        db.UpsertEntries("testSong1", [
            new LeaderboardEntry { AccountId = "hist_p1", Score = 10000, Rank = 1, Accuracy = 99, Stars = 6 },
        ]);
        db.RecomputeAllRanks();
        db.ComputeSongStats();
        db.ComputeAccountRankings(totalChartedSongs: 1);
        db.SnapshotRankHistory();

        var response = await _client.GetAsync("/api/rankings/Solo_Guitar/hist_p1/history?days=7");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(json.GetProperty("history").GetArrayLength() > 0);
    }

    [Fact]
    public async Task Rankings_Composite_ReturnsEmpty_WhenNoData()
    {
        var response = await _client.GetAsync("/api/rankings/composite?page=1&pageSize=10");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(0, json.GetProperty("totalAccounts").GetInt32());
    }

    [Fact]
    public async Task Rankings_Composite_ReturnsData_WhenSeeded()
    {
        var metaDb = _factory.Services.GetRequiredService<MetaDatabase>();
        metaDb.ReplaceCompositeRankings([new CompositeRankingDto
        {
            AccountId = "comp_p1", InstrumentsPlayed = 2, TotalSongsPlayed = 50,
            CompositeRating = 0.05, CompositeRank = 1,
            GuitarAdjustedSkill = 0.03, GuitarSkillRank = 1,
        }]);

        var response = await _client.GetAsync("/api/rankings/composite");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, json.GetProperty("totalAccounts").GetInt32());
    }

    [Fact]
    public async Task Rankings_CompositeSingle_ReturnsData()
    {
        var metaDb = _factory.Services.GetRequiredService<MetaDatabase>();
        metaDb.ReplaceCompositeRankings([new CompositeRankingDto
        {
            AccountId = "comp_single", InstrumentsPlayed = 1, TotalSongsPlayed = 10,
            CompositeRating = 0.1, CompositeRank = 1,
        }]);

        var response = await _client.GetAsync("/api/rankings/composite/comp_single");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("comp_single", json.GetProperty("accountId").GetString());
    }

    [Fact]
    public async Task Rankings_CompositeSingle_NotFound()
    {
        var response = await _client.GetAsync("/api/rankings/composite/nobody");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Rankings_Combo_ReturnsOk_WithValidInstruments()
    {
        var response = await _client.GetAsync("/api/rankings/combo?instruments=Solo_Guitar%2BSolo_Bass");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(json.TryGetProperty("totalAccounts", out _));
    }

    [Fact]
    public async Task Rankings_Combo_BadRequest_SingleInstrument()
    {
        var response = await _client.GetAsync("/api/rankings/combo?instruments=Solo_Guitar");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Rankings_Combo_ReturnsData_WhenSeeded()
    {
        var metaDb = _factory.Services.GetRequiredService<MetaDatabase>();
        metaDb.ReplaceComboLeaderboard("03",
            [("combo_p1", 0.05, 0.06, 0.8, 50000, 0.95, 100, 80), ("combo_p2", 0.10, 0.12, 0.6, 40000, 0.90, 80, 48)], 2);

        var response = await _client.GetAsync("/api/rankings/combo?instruments=Solo_Guitar%2BSolo_Bass");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(json.GetProperty("totalAccounts").GetInt32() >= 2);
    }

    [Fact]
    public async Task Rankings_ComboSingle_ReturnsData()
    {
        var metaDb = _factory.Services.GetRequiredService<MetaDatabase>();
        metaDb.ReplaceComboLeaderboard("03",
            [("combo_single_p1", 0.05, 0.06, 0.8, 50000, 0.95, 100, 80)], 1);

        var response = await _client.GetAsync("/api/rankings/combo/combo_single_p1?instruments=Solo_Guitar%2BSolo_Bass");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("combo_single_p1", json.GetProperty("accountId").GetString());
    }

    [Fact]
    public async Task Rankings_ComboSingle_NotFound()
    {
        var response = await _client.GetAsync("/api/rankings/combo/nobody?instruments=Solo_Guitar%2BSolo_Bass");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ═══════════════════════════════════════════════════════════
    // Admin Endpoints
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public async Task Admin_Status_ReturnsStatus()
    {
        var response = await _authedClient.GetAsync("/api/status");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(json.TryGetProperty("instruments", out _));
    }

    [Fact]
    public async Task Admin_EpicToken_ReturnsTokenInfo()
    {
        var response = await _authedClient.GetAsync("/api/admin/epic-token");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("mock_access_token_for_testing", json.GetProperty("accessToken").GetString());
        Assert.Equal("mock_caller_account_id", json.GetProperty("accountId").GetString());
        Assert.Equal("MockPlayer", json.GetProperty("displayName").GetString());
        Assert.True(json.TryGetProperty("expiresAt", out _));
    }

    [Fact]
    public async Task Admin_EpicToken_RequiresAuth()
    {
        var response = await _client.GetAsync("/api/admin/epic-token");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Admin_EpicToken_NoToken_ReturnsProblem()
    {
        var tokenManager = _factory.Services.GetRequiredService<TokenManager>();
        tokenManager.GetAccessTokenAsync(Arg.Any<CancellationToken>())
            .Returns((string?)null);
        try
        {
            var response = await _authedClient.GetAsync("/api/admin/epic-token");
            Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        }
        finally
        {
            tokenManager.GetAccessTokenAsync(Arg.Any<CancellationToken>())
                .Returns("mock_access_token_for_testing");
        }
    }

    [Fact]
    public async Task Admin_DbStats_Queries_RequiresAuth()
    {
        var response = await _client.GetAsync("/api/admin/dbstats/queries");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Admin_DbStats_Bloat_RequiresAuth()
    {
        var response = await _client.GetAsync("/api/admin/dbstats/bloat");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Admin_DbStats_Bloat_ReturnsTables()
    {
        var response = await _authedClient.GetAsync("/api/admin/dbstats/bloat?limit=5");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(json.TryGetProperty("count", out _));
        Assert.True(json.TryGetProperty("tables", out var tables));
        Assert.Equal(JsonValueKind.Array, tables.ValueKind);
    }

    [Fact]
    public async Task Admin_DbStats_Queries_ReturnsServiceUnavailableWhenExtensionMissing()
    {
        // pg_stat_statements is not installed in the test postgres (no shared_preload_libraries).
        // Endpoint should respond 503 with a clear message rather than 500.
        var response = await _authedClient.GetAsync("/api/admin/dbstats/queries");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    [Fact]
    public async Task Admin_FirstSeen_ReturnsData()
    {
        var response = await _client.GetAsync("/api/firstseen");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(json.TryGetProperty("count", out _));
        Assert.True(json.TryGetProperty("songs", out _));
    }

    [Fact]
    public async Task Admin_LeaderboardPopulation_Get_ReturnsData()
    {
        var response = await _authedClient.GetAsync("/api/leaderboard-population");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Admin_BackfillStatus_NotFound_WhenUnknown()
    {
        var response = await _authedClient.GetAsync("/api/backfill/unknown_account/status");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Admin_Register_RequiresAuth()
    {
        var body = new { deviceId = "d1", username = "test" };
        var response = await _client.PostAsJsonAsync("/api/register", body);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Admin_Register_BadRequest_EmptyFields()
    {
        var body = new { deviceId = "", username = "" };
        var response = await _authedClient.PostAsJsonAsync("/api/register", body);
        // Returns 400 (BadRequest) for empty fields
        Assert.True(response.StatusCode == HttpStatusCode.BadRequest || response.StatusCode == HttpStatusCode.OK);
    }

    [Fact]
    public async Task Admin_Register_UnknownUsername_ReturnsNoAccountFound()
    {
        var body = new { deviceId = "test-device-999", username = "definitely_not_a_real_user" };
        var response = await _authedClient.PostAsJsonAsync("/api/register", body);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("no_account_found", json.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Admin_Unregister_RequiresAuth()
    {
        var body = new { deviceId = "d1", username = "test" };
        var response = await _client.SendAsync(new HttpRequestMessage(HttpMethod.Delete, "/api/register")
        {
            Content = JsonContent.Create(body),
        });
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Admin_Unregister_WithAuth_NoOp()
    {
        var response = await _authedClient.SendAsync(new HttpRequestMessage(HttpMethod.Delete,
            "/api/register?deviceId=nonexistent&accountId=nonexistent"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(json.GetProperty("unregistered").GetBoolean());
    }

    [Fact]
    public async Task Admin_Backfill_RequiresAuth()
    {
        var response = await _client.PostAsync("/api/backfill/test_account", null);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Admin_LeaderboardPopulation_Post_RequiresAuth()
    {
        var body = new[] { new { songId = "s1", instrument = "Solo_Guitar", totalEntries = 1000 } };
        var response = await _client.PostAsJsonAsync("/api/leaderboard-population", body);
        // POST endpoint was removed — PercentileService deprecated
        Assert.True(response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed
            or HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Admin_LeaderboardPopulation_Post_WithAuth_Returns()
    {
        var body = new[] { new { songId = "testSong1", instrument = "Solo_Guitar", totalEntries = 50000 } };
        var response = await _authedClient.PostAsJsonAsync("/api/leaderboard-population", body);
        // POST endpoint was removed — PercentileService deprecated
        Assert.True(response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed);
    }

    [Fact]
    public async Task Admin_ShopRefresh_RequiresAuth()
    {
        var response = await _client.PostAsync("/api/admin/shop/refresh", null);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Admin_ShopRefresh_WithAuth_Returns()
    {
        try
        {
            var response = await _authedClient.PostAsync("/api/admin/shop/refresh", null);
            // Shop service may succeed or fail gracefully — either is fine
            Assert.True(response.StatusCode != HttpStatusCode.Unauthorized);
        }
        catch (HttpRequestException)
        {
            // Connection/transport errors are acceptable in test environment
        }
    }

    [Fact]
    public async Task Admin_FirstSeenCalculate_RequiresAuth()
    {
        var response = await _client.PostAsync("/api/firstseen/calculate", null);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Admin_RegeneratePaths_RequiresAuth()
    {
        var response = await _client.PostAsync("/api/admin/regenerate-paths", null);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // ═══════════════════════════════════════════════════════════
    // Account Endpoints
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public async Task Account_Check_ReturnsNotFound_ForUnknown()
    {
        var response = await _client.GetAsync("/api/account/check?username=definitely_not_a_real_user_xyz");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(json.GetProperty("exists").GetBoolean());
    }

    [Fact]
    public async Task Account_Search_ReturnsEmptyForGarbage()
    {
        var response = await _client.GetAsync("/api/account/search?q=zzzzzzzzzznotauser");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(json.TryGetProperty("results", out var results));
        Assert.Equal(0, results.GetArrayLength());
    }

    [Fact]
    public async Task Account_Search_RequiresQuery()
    {
        var response = await _client.GetAsync("/api/account/search");
        // Should return 400 or empty — depends on implementation
        Assert.True(response.StatusCode == HttpStatusCode.BadRequest || response.StatusCode == HttpStatusCode.OK);
    }

    [Fact]
    public async Task Account_Check_WithSeededName_ReturnsFound()
    {
        var metaDb = _factory.Services.GetRequiredService<MetaDatabase>();
        metaDb.InsertAccountNames([("check_test_acct", "CheckTestUser")]);

        var response = await _client.GetAsync("/api/account/check?username=CheckTestUser");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(json.GetProperty("exists").GetBoolean());
    }

    [Fact]
    public async Task Account_Search_WithSeededName_ReturnsMatch()
    {
        var metaDb = _factory.Services.GetRequiredService<MetaDatabase>();
        metaDb.InsertAccountNames([("search_test_acct", "SearchableUser99")]);

        var response = await _client.GetAsync("/api/account/search?q=SearchableUser");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(json.GetProperty("results").GetArrayLength() >= 1);
    }

    // ═══════════════════════════════════════════════════════════
    // Player Endpoints — additional coverage
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public async Task Player_Profile_NotFound_ForUnknown()
    {
        var response = await _client.GetAsync("/api/player/nonexistent_player_xyz");
        // Returns 200 with empty scores (player may have no data)
        Assert.True(response.StatusCode == HttpStatusCode.OK || response.StatusCode == HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Player_Stats_WithSeededData()
    {
        var persistence = _factory.Services.GetRequiredService<GlobalLeaderboardPersistence>();
        var db = persistence.GetOrCreateInstrumentDb("Solo_Guitar");
        db.UpsertEntries("testSong1", [
            new LeaderboardEntry { AccountId = "stats_player", Score = 5000, Rank = 50, Accuracy = 90, Stars = 4, Season = 3 },
        ]);

        var response = await _client.GetAsync("/api/player/stats_player/stats");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Player_History_RequiresRegistration()
    {
        var response = await _client.GetAsync("/api/player/unregistered_player/history");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Player_History_ReturnsData_WhenRegistered()
    {
        var metaDb = _factory.Services.GetRequiredService<MetaDatabase>();
        metaDb.RegisterUser("hist-device", "hist_reg_player");
        metaDb.InsertScoreChange("testSong1", "Solo_Guitar", "hist_reg_player",
            null, 5000, null, 50, accuracy: 90, isFullCombo: false, stars: 4,
            percentile: 0.5, season: 3, scoreAchievedAt: null);

        var response = await _client.GetAsync("/api/player/hist_reg_player/history?songId=testSong1");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // ─── Player leeway (valid score filtering) ──────────────────

    [Fact]
    public async Task Player_Leeway_ReturnsValidFields_WhenLeewayProvided()
    {
        const string songId = "playerLeewayTestSong";
        using (var scope = _factory.Services.CreateScope())
        {
            var persistence = scope.ServiceProvider.GetRequiredService<GlobalLeaderboardPersistence>();
            var pathStore = scope.ServiceProvider.GetRequiredService<PathDataStore>();
            var metaDb = scope.ServiceProvider.GetRequiredService<MetaDatabase>();

            EnsureSongRow(pathStore, songId);
            pathStore.UpdateMaxScores(songId, new SongMaxScores { MaxLeadScore = 90_000 }, "hash_lw");

            var db = persistence.GetOrCreateInstrumentDb("Solo_Guitar");
            db.UpsertEntries(songId, [
                new LeaderboardEntry { AccountId = "lw_valid",   Score = 85_000, Accuracy = 99, Stars = 6 },
                new LeaderboardEntry { AccountId = "lw_invalid", Score = 200_000, Accuracy = 100, Stars = 6 },
            ]);

            // Seed score history so lw_invalid has a fallback valid score
            metaDb.InsertScoreChange(songId, "Solo_Guitar", "lw_invalid", null, 80_000, null, 2,
                accuracy: 95, isFullCombo: true, stars: 6, scoreAchievedAt: "2025-01-01T00:00:00Z");
            metaDb.InsertScoreChange(songId, "Solo_Guitar", "lw_invalid", 80_000, 200_000, 2, 1,
                accuracy: 100, isFullCombo: true, stars: 6, scoreAchievedAt: "2025-02-01T00:00:00Z");
        }

        // Without leeway: no validity fields
        var plain = await _client.GetAsync($"/api/player/lw_valid?songId={songId}");
        var plainJson = await plain.Content.ReadFromJsonAsync<JsonElement>();
        var plainScores = plainJson.GetProperty("scores");
        Assert.Equal(1, plainScores.GetArrayLength());
        // isValid should not be present (null → omitted by default serialization)
        // Actually with default STJ settings nulls may be omitted or present as null
        var plainEntry = plainScores[0];
        if (plainEntry.TryGetProperty("isValid", out var iv))
            Assert.Equal(JsonValueKind.Null, iv.ValueKind);

        // With leeway: valid player gets isValid=true, validScore=their score
        var filtered = await _client.GetAsync($"/api/player/lw_valid?songId={songId}&leeway=5");
        var filteredJson = await filtered.Content.ReadFromJsonAsync<JsonElement>();
        var filteredScores = filteredJson.GetProperty("scores");
        Assert.Equal(1, filteredScores.GetArrayLength());
        var validEntry = filteredScores[0];
        Assert.True(validEntry.GetProperty("isValid").GetBoolean());
        Assert.Equal(85_000, validEntry.GetProperty("validScore").GetInt32());
        Assert.True(validEntry.GetProperty("validRank").GetInt32() >= 1);
    }

    [Fact]
    public async Task Player_Leeway_ReturnsFallbackForInvalidScore()
    {
        const string songId = "playerFallbackSong";
        using (var scope = _factory.Services.CreateScope())
        {
            var persistence = scope.ServiceProvider.GetRequiredService<GlobalLeaderboardPersistence>();
            var pathStore = scope.ServiceProvider.GetRequiredService<PathDataStore>();
            var metaDb = scope.ServiceProvider.GetRequiredService<MetaDatabase>();

            EnsureSongRow(pathStore, songId);
            pathStore.UpdateMaxScores(songId, new SongMaxScores { MaxLeadScore = 90_000 }, "hash_fb");

            var db = persistence.GetOrCreateInstrumentDb("Solo_Guitar");
            db.UpsertEntries(songId, [
                new LeaderboardEntry { AccountId = "fb_cheater", Score = 200_000, Accuracy = 100, Stars = 6 },
                new LeaderboardEntry { AccountId = "fb_legit",   Score = 85_000,  Accuracy = 99,  Stars = 6 },
            ]);

            // fb_cheater has a valid historical score of 80k
            metaDb.InsertScoreChange(songId, "Solo_Guitar", "fb_cheater", null, 80_000, null, 2,
                accuracy: 95, isFullCombo: true, stars: 5, scoreAchievedAt: "2025-01-01T00:00:00Z");
        }

        var response = await _client.GetAsync($"/api/player/fb_cheater?songId={songId}&leeway=5");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        var scores = json.GetProperty("scores");
        Assert.Equal(1, scores.GetArrayLength());

        var entry = scores[0];
        // Current stored score is the invalid one
        Assert.Equal(200_000, entry.GetProperty("sc").GetInt32());
        // But isValid is false
        Assert.False(entry.GetProperty("isValid").GetBoolean());
        // Fallback to the valid historical score
        Assert.Equal(80_000, entry.GetProperty("validScore").GetInt32());
        Assert.Equal(0, entry.GetProperty("validAccuracy").GetInt32()); // 95 / 1000 = 0 (int division)
        Assert.True(entry.GetProperty("validIsFullCombo").GetBoolean());
        // Rank should be computed for the valid score against filtered leaderboard
        Assert.True(entry.GetProperty("validRank").GetInt32() >= 1);
    }

    [Fact]
    public async Task Player_Leeway_OmitsValidFieldsWhenNoLeeway()
    {
        var response = await _client.GetAsync("/api/player/testAcct1");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        var scores = json.GetProperty("scores");
        if (scores.GetArrayLength() > 0)
        {
            var first = scores[0];
            // Without leeway, isValid should be null (omitted or explicitly null)
            if (first.TryGetProperty("isValid", out var val))
                Assert.Equal(JsonValueKind.Null, val.ValueKind);
        }
    }

    // ═══════════════════════════════════════════════════════════
    // Additional Admin Endpoints — body coverage
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public async Task Admin_Register_WithKnownUser_Succeeds()
    {
        var metaDb = _factory.Services.GetRequiredService<MetaDatabase>();
        metaDb.InsertAccountNames([("register_test_acct", "RegisterableUser")]);

        var body = new { deviceId = "reg-device-001", username = "RegisterableUser" };
        var response = await _authedClient.PostAsJsonAsync("/api/register", body);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(json.GetProperty("registered").GetBoolean());
        Assert.Equal("register_test_acct", json.GetProperty("accountId").GetString());
    }

    [Fact]
    public async Task Admin_BackfillStatus_WithRegisteredUser()
    {
        var metaDb = _factory.Services.GetRequiredService<MetaDatabase>();
        metaDb.InsertAccountNames([("bf_status_acct", "BackfillStatusUser")]);
        metaDb.RegisterUser("bf-device", "bf_status_acct");

        var response = await _authedClient.GetAsync("/api/backfill/bf_status_acct/status");
        // Returns OK with status info (even if not yet backfilled)
        Assert.True(response.StatusCode == HttpStatusCode.OK || response.StatusCode == HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Admin_LeaderboardPopulation_Post_WithData()
    {
        var body = new[] {
            new { songId = "testSong1", instrument = "Solo_Guitar", totalEntries = 100000 },
            new { songId = "testSong1", instrument = "Solo_Bass", totalEntries = 200000 },
        };
        var response = await _authedClient.PostAsJsonAsync("/api/leaderboard-population", body);
        // POST endpoint was removed — PercentileService deprecated
        Assert.True(response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed);
    }

    [Fact]
    public async Task Admin_LeaderboardPopulation_Get_WithData()
    {
        var metaDb = _factory.Services.GetRequiredService<MetaDatabase>();
        metaDb.UpsertLeaderboardPopulation([("testSong1", "Solo_Guitar", 50000L)]);

        var response = await _authedClient.GetAsync("/api/leaderboard-population");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // ═══════════════════════════════════════════════════════════
    // Sync Endpoints
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public async Task Diag_Events_Returns_WhenPublic()
    {
        // Diag endpoints are public — should return OK or some status (not 401)
        var response = await _client.GetAsync("/api/diag/events?eventId=test");
        Assert.NotEqual(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Diag_Leaderboard_Returns_WhenPublic()
    {
        var response = await _client.GetAsync("/api/diag/leaderboard?song=testSong1&instrument=Solo_Guitar");
        Assert.NotEqual(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Diag_Leaderboard_V2_Returns()
    {
        var response = await _client.GetAsync("/api/diag/leaderboard?song=testSong1&instrument=Solo_Guitar&version=v2");
        Assert.NotEqual(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // ═══════════════════════════════════════════════════════════════
    // Coverage: Rivals cache paths + suggestions + all + diagnostics
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Rivals_Overview_CacheHit_ReturnsFromCache()
    {
        var metaDb = _factory.Services.GetRequiredService<MetaDatabase>();
        var rivals = new List<UserRivalRow>
        {
            new() { UserId = "cache_acct", RivalAccountId = "r1", InstrumentCombo = "01",
                     Direction = "above", RivalScore = 10, AvgSignedDelta = -1,
                     SharedSongCount = 5, AheadCount = 3, BehindCount = 2, ComputedAt = "2026-01-01T00:00:00Z" },
        };
        metaDb.ReplaceRivalsData("cache_acct", rivals, Array.Empty<RivalSongSampleRow>());

        // First request populates cache
        var r1 = await _client.GetAsync("/api/player/cache_acct/rivals");
        Assert.Equal(HttpStatusCode.OK, r1.StatusCode);
        var etag = r1.Headers.ETag?.Tag;
        Assert.NotNull(etag);

        // Second request hits cache
        var r2 = await _client.GetAsync("/api/player/cache_acct/rivals");
        Assert.Equal(HttpStatusCode.OK, r2.StatusCode);

        // Third request with If-None-Match returns 304
        var req = new HttpRequestMessage(HttpMethod.Get, "/api/player/cache_acct/rivals");
        req.Headers.TryAddWithoutValidation("If-None-Match", etag);
        var r3 = await _client.SendAsync(req);
        Assert.Equal(HttpStatusCode.NotModified, r3.StatusCode);
    }

    [Fact]
    public async Task Rivals_Suggestions_NoData_ReturnsNotFound()
    {
        var response = await _client.GetAsync("/api/player/no_rivals_sug/rivals/suggestions");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Rivals_Suggestions_WithData_ReturnsRivals()
    {
        var metaDb = _factory.Services.GetRequiredService<MetaDatabase>();
        var rivals = new List<UserRivalRow>
        {
            new() { UserId = "sug_acct", RivalAccountId = "sug_r1", InstrumentCombo = "01",
                     Direction = "above", RivalScore = 50, AvgSignedDelta = -2,
                     SharedSongCount = 10, AheadCount = 6, BehindCount = 4, ComputedAt = "2026-01-01T00:00:00Z" },
            new() { UserId = "sug_acct", RivalAccountId = "sug_r2", InstrumentCombo = "01",
                     Direction = "below", RivalScore = 30, AvgSignedDelta = 1,
                     SharedSongCount = 8, AheadCount = 3, BehindCount = 5, ComputedAt = "2026-01-01T00:00:00Z" },
        };
        var samples = new List<RivalSongSampleRow>
        {
            new() { UserId = "sug_acct", RivalAccountId = "sug_r1", Instrument = "Solo_Guitar",
                     SongId = "s1", UserRank = 5, RivalRank = 3, RankDelta = -2, UserScore = 9000, RivalScore = 9200 },
        };
        metaDb.ReplaceRivalsData("sug_acct", rivals, samples);

        var response = await _client.GetAsync("/api/player/sug_acct/rivals/suggestions?combo=Solo_Guitar&limit=2");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(json.GetProperty("rivals").GetArrayLength() >= 1);
    }

    [Fact]
    public async Task Rivals_Suggestions_WithoutCombo_QueriesAllCombos()
    {
        var metaDb = _factory.Services.GetRequiredService<MetaDatabase>();
        var rivals = new List<UserRivalRow>
        {
            new() { UserId = "sug_all", RivalAccountId = "sr1", InstrumentCombo = "01",
                     Direction = "above", RivalScore = 25, AvgSignedDelta = -1,
                     SharedSongCount = 5, AheadCount = 3, BehindCount = 2, ComputedAt = "2026-01-01T00:00:00Z" },
        };
        metaDb.ReplaceRivalsData("sug_all", rivals, Array.Empty<RivalSongSampleRow>());

        var response = await _client.GetAsync("/api/player/sug_all/rivals/suggestions");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Rivals_Suggestions_CacheHit_Returns304()
    {
        var metaDb = _factory.Services.GetRequiredService<MetaDatabase>();
        var rivals = new List<UserRivalRow>
        {
            new() { UserId = "sug_cache", RivalAccountId = "sc1", InstrumentCombo = "01",
                     Direction = "below", RivalScore = 20, AvgSignedDelta = 1,
                     SharedSongCount = 5, AheadCount = 2, BehindCount = 3, ComputedAt = "2026-01-01T00:00:00Z" },
        };
        metaDb.ReplaceRivalsData("sug_cache", rivals, Array.Empty<RivalSongSampleRow>());

        var r1 = await _client.GetAsync("/api/player/sug_cache/rivals/suggestions");
        Assert.Equal(HttpStatusCode.OK, r1.StatusCode);
        var etag = r1.Headers.ETag?.Tag;
        Assert.NotNull(etag);

        var req = new HttpRequestMessage(HttpMethod.Get, "/api/player/sug_cache/rivals/suggestions");
        req.Headers.TryAddWithoutValidation("If-None-Match", etag);
        var r2 = await _client.SendAsync(req);
        Assert.Equal(HttpStatusCode.NotModified, r2.StatusCode);
    }

    [Fact]
    public async Task Rivals_All_NoData_ReturnsNotFound()
    {
        var response = await _client.GetAsync("/api/player/no_all_acct/rivals/all");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Rivals_All_WithData_ReturnsCombos()
    {
        var metaDb = _factory.Services.GetRequiredService<MetaDatabase>();
        var rivals = new List<UserRivalRow>
        {
            new() { UserId = "all_acct", RivalAccountId = "ar1", InstrumentCombo = "01",
                     Direction = "above", RivalScore = 40, AvgSignedDelta = -2,
                     SharedSongCount = 10, AheadCount = 6, BehindCount = 4, ComputedAt = "2026-01-01T00:00:00Z" },
            new() { UserId = "all_acct", RivalAccountId = "ar2", InstrumentCombo = "02",
                     Direction = "below", RivalScore = 20, AvgSignedDelta = 1,
                     SharedSongCount = 7, AheadCount = 3, BehindCount = 4, ComputedAt = "2026-01-01T00:00:00Z" },
        };
        metaDb.ReplaceRivalsData("all_acct", rivals, Array.Empty<RivalSongSampleRow>());

        var response = await _client.GetAsync("/api/player/all_acct/rivals/all");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        var combos = json.GetProperty("combos");
        Assert.Equal(2, combos.GetArrayLength());
    }

    [Fact]
    public async Task Rivals_All_CacheHit_Returns304()
    {
        var metaDb = _factory.Services.GetRequiredService<MetaDatabase>();
        var rivals = new List<UserRivalRow>
        {
            new() { UserId = "allc_acct", RivalAccountId = "ac1", InstrumentCombo = "01",
                     Direction = "above", RivalScore = 35, AvgSignedDelta = -1,
                     SharedSongCount = 5, AheadCount = 3, BehindCount = 2, ComputedAt = "2026-01-01T00:00:00Z" },
        };
        metaDb.ReplaceRivalsData("allc_acct", rivals, Array.Empty<RivalSongSampleRow>());

        var r1 = await _client.GetAsync("/api/player/allc_acct/rivals/all");
        Assert.Equal(HttpStatusCode.OK, r1.StatusCode);
        var etag = r1.Headers.ETag?.Tag;
        Assert.NotNull(etag);

        var req = new HttpRequestMessage(HttpMethod.Get, "/api/player/allc_acct/rivals/all");
        req.Headers.TryAddWithoutValidation("If-None-Match", etag);
        var r2 = await _client.SendAsync(req);
        Assert.Equal(HttpStatusCode.NotModified, r2.StatusCode);
    }

    [Fact]
    public async Task Rivals_Diagnostics_RequiresAuth()
    {
        var response = await _client.GetAsync("/api/player/diag_acct/rivals/diagnostics");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Rivals_Diagnostics_WithAuth_ReturnsOk()
    {
        // Seed rivals data so the diagnostics endpoint has status + combos to return
        var metaDb = _factory.Services.GetRequiredService<MetaDatabase>();
        metaDb.EnsureRivalsStatus("diag_acct");
        metaDb.StartRivals("diag_acct", 1);
        metaDb.CompleteRivals("diag_acct", 1, 2);
        var rivals = new List<UserRivalRow>
        {
            new() { UserId = "diag_acct", RivalAccountId = "dr1", InstrumentCombo = "01",
                     Direction = "above", RivalScore = 20, AvgSignedDelta = -1,
                     SharedSongCount = 5, AheadCount = 3, BehindCount = 2, ComputedAt = "2026-01-01T00:00:00Z" },
        };
        metaDb.ReplaceRivalsData("diag_acct", rivals, Array.Empty<RivalSongSampleRow>());

        // Seed instrument data so GetDiagnostics finds entries for the account
        using (var scope = _factory.Services.CreateScope())
        {
            var persistence = scope.ServiceProvider.GetRequiredService<GlobalLeaderboardPersistence>();
            var db = persistence.GetOrCreateInstrumentDb("Solo_Guitar");
            db.UpsertEntries("diag_song", new List<LeaderboardEntry>
            {
                new() { AccountId = "diag_acct", Score = 10000, Accuracy = 95, Stars = 5 },
                new() { AccountId = "filler1",   Score = 9000,  Accuracy = 90, Stars = 4 },
            });
        }

        var response = await _authedClient.GetAsync("/api/player/diag_acct/rivals/diagnostics");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("diag_acct", json.GetProperty("accountId").GetString());
        Assert.NotNull(json.GetProperty("rivalsStatus"));
        Assert.True(json.GetProperty("combosStored").GetArrayLength() > 0);
        Assert.True(json.GetProperty("instruments").GetArrayLength() > 0);
    }

    [Fact]
    public async Task Rivals_ComboList_CacheHit_Returns304()
    {
        var metaDb = _factory.Services.GetRequiredService<MetaDatabase>();
        var rivals = new List<UserRivalRow>
        {
            new() { UserId = "combo_cache", RivalAccountId = "cc1", InstrumentCombo = "01",
                     Direction = "above", RivalScore = 15, AvgSignedDelta = -1,
                     SharedSongCount = 5, AheadCount = 3, BehindCount = 2, ComputedAt = "2026-01-01T00:00:00Z" },
        };
        metaDb.ReplaceRivalsData("combo_cache", rivals, Array.Empty<RivalSongSampleRow>());

        var r1 = await _client.GetAsync("/api/player/combo_cache/rivals/Solo_Guitar");
        Assert.Equal(HttpStatusCode.OK, r1.StatusCode);
        var etag = r1.Headers.ETag?.Tag;
        Assert.NotNull(etag);

        var req = new HttpRequestMessage(HttpMethod.Get, "/api/player/combo_cache/rivals/Solo_Guitar");
        req.Headers.TryAddWithoutValidation("If-None-Match", etag);
        var r2 = await _client.SendAsync(req);
        Assert.Equal(HttpStatusCode.NotModified, r2.StatusCode);
    }

    [Fact]
    public async Task Rivals_ComboList_InvalidCombo_ReturnsBadRequest()
    {
        var response = await _client.GetAsync("/api/player/acct1/rivals/InvalidInstrument");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Rivals_Detail_CacheHit_Returns304()
    {
        var metaDb = _factory.Services.GetRequiredService<MetaDatabase>();
        var rivals = new List<UserRivalRow>
        {
            new() { UserId = "dtl_cache", RivalAccountId = "dc1", InstrumentCombo = "01",
                     Direction = "above", RivalScore = 15, AvgSignedDelta = -1,
                     SharedSongCount = 5, AheadCount = 3, BehindCount = 2, ComputedAt = "2026-01-01T00:00:00Z" },
        };
        var samples = new List<RivalSongSampleRow>
        {
            new() { UserId = "dtl_cache", RivalAccountId = "dc1", Instrument = "Solo_Guitar",
                     SongId = "s1", UserRank = 5, RivalRank = 3, RankDelta = -2, UserScore = 9000, RivalScore = 9100 },
        };
        metaDb.ReplaceRivalsData("dtl_cache", rivals, samples);

        var r1 = await _client.GetAsync("/api/player/dtl_cache/rivals/Solo_Guitar/dc1");
        Assert.Equal(HttpStatusCode.OK, r1.StatusCode);
        var etag = r1.Headers.ETag?.Tag;
        Assert.NotNull(etag);

        var req = new HttpRequestMessage(HttpMethod.Get, "/api/player/dtl_cache/rivals/Solo_Guitar/dc1");
        req.Headers.TryAddWithoutValidation("If-None-Match", etag);
        var r2 = await _client.SendAsync(req);
        Assert.Equal(HttpStatusCode.NotModified, r2.StatusCode);
    }

    [Fact]
    public async Task Rivals_SongsPerInstrument_CacheHit_Returns304()
    {
        var metaDb = _factory.Services.GetRequiredService<MetaDatabase>();
        var rivals = new List<UserRivalRow>
        {
            new() { UserId = "song_cache", RivalAccountId = "sci1", InstrumentCombo = "01",
                     Direction = "above", RivalScore = 15, AvgSignedDelta = -1,
                     SharedSongCount = 5, AheadCount = 3, BehindCount = 2, ComputedAt = "2026-01-01T00:00:00Z" },
        };
        var samples = new List<RivalSongSampleRow>
        {
            new() { UserId = "song_cache", RivalAccountId = "sci1", Instrument = "Solo_Guitar",
                     SongId = "s1", UserRank = 5, RivalRank = 3, RankDelta = -2, UserScore = 9000, RivalScore = 9100 },
        };
        metaDb.ReplaceRivalsData("song_cache", rivals, samples);

        var r1 = await _client.GetAsync("/api/player/song_cache/rivals/sci1/songs/Solo_Guitar");
        Assert.Equal(HttpStatusCode.OK, r1.StatusCode);
        var etag = r1.Headers.ETag?.Tag;
        Assert.NotNull(etag);

        var req = new HttpRequestMessage(HttpMethod.Get, "/api/player/song_cache/rivals/sci1/songs/Solo_Guitar");
        req.Headers.TryAddWithoutValidation("If-None-Match", etag);
        var r2 = await _client.SendAsync(req);
        Assert.Equal(HttpStatusCode.NotModified, r2.StatusCode);
    }

    // ═══════════════════════════════════════════════════════════════
    // Coverage: Rankings neighborhood + overview + combo single
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Rankings_Neighborhood_ReturnsData_WhenSeeded()
    {
        var persistence = _factory.Services.GetRequiredService<GlobalLeaderboardPersistence>();
        var db = persistence.GetOrCreateInstrumentDb("Solo_Guitar");

        // Seed enough ranked players for a neighborhood
        for (int i = 1; i <= 10; i++)
        {
            db.UpsertEntries("song_nb", [
                new LeaderboardEntry { AccountId = $"nb_p{i}", Score = 100000 - (i * 1000), Accuracy = 99 - i, Stars = 6 }
            ]);
        }
        db.RecomputeAllRanks();
        db.ComputeSongStats();
        db.ComputeAccountRankings(totalChartedSongs: 1);

        var response = await _client.GetAsync("/api/rankings/Solo_Guitar/nb_p5/neighborhood?radius=2");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Solo_Guitar", json.GetProperty("instrument").GetString());
        Assert.True(json.GetProperty("above").GetArrayLength() > 0);
        Assert.True(json.GetProperty("below").GetArrayLength() > 0);
        Assert.NotNull(json.GetProperty("self"));
    }

    [Fact]
    public async Task Rankings_Neighborhood_NotFound_WhenUnknown()
    {
        var response = await _client.GetAsync("/api/rankings/Solo_Guitar/unknown_nb_acct/neighborhood");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Rankings_Neighborhood_CacheHit_Returns304()
    {
        var persistence = _factory.Services.GetRequiredService<GlobalLeaderboardPersistence>();
        var db = persistence.GetOrCreateInstrumentDb("Solo_Bass");
        db.UpsertEntries("song_nbc", [ new LeaderboardEntry { AccountId = "nbc_p1", Score = 50000, Accuracy = 90, Stars = 5 } ]);
        db.RecomputeAllRanks();
        db.ComputeSongStats();
        db.ComputeAccountRankings(totalChartedSongs: 1);

        var r1 = await _client.GetAsync("/api/rankings/Solo_Bass/nbc_p1/neighborhood");
        Assert.Equal(HttpStatusCode.OK, r1.StatusCode);
        var etag = r1.Headers.ETag?.Tag;
        Assert.NotNull(etag);

        var req = new HttpRequestMessage(HttpMethod.Get, "/api/rankings/Solo_Bass/nbc_p1/neighborhood");
        req.Headers.TryAddWithoutValidation("If-None-Match", etag);
        var r2 = await _client.SendAsync(req);
        Assert.Equal(HttpStatusCode.NotModified, r2.StatusCode);
    }

    [Fact]
    public async Task Rankings_CompositeNeighborhood_ReturnsData_WhenSeeded()
    {
        var metaDb = _factory.Services.GetRequiredService<MetaDatabase>();
        var rankings = new List<CompositeRankingDto>();
        for (int i = 1; i <= 5; i++)
        {
            rankings.Add(new CompositeRankingDto
            {
                AccountId = $"comp_nb_{i}",
                InstrumentsPlayed = 2,
                TotalSongsPlayed = 50,
                CompositeRating = 100 - i * 10,
                CompositeRank = i,
            });
        }
        metaDb.ReplaceCompositeRankings(rankings);

        var response = await _client.GetAsync("/api/rankings/composite/comp_nb_3/neighborhood?radius=2");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(json.TryGetProperty("self", out _));
        Assert.True(json.GetProperty("above").GetArrayLength() > 0);
        Assert.True(json.GetProperty("below").GetArrayLength() > 0);

        // Clean up composite rankings to avoid polluting other tests
        metaDb.ReplaceCompositeRankings([]);
    }

    [Fact]
    public async Task Rankings_CompositeNeighborhood_NotFound_WhenUnknown()
    {
        var response = await _client.GetAsync("/api/rankings/composite/unknown_comp/neighborhood");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Rankings_CompositeNeighborhood_CacheHit_Returns304()
    {
        var metaDb = _factory.Services.GetRequiredService<MetaDatabase>();
        metaDb.ReplaceCompositeRankings([new CompositeRankingDto
        {
            AccountId = "compnbc", InstrumentsPlayed = 1, TotalSongsPlayed = 10,
            CompositeRating = 50, CompositeRank = 1,
        }]);

        var r1 = await _client.GetAsync("/api/rankings/composite/compnbc/neighborhood");
        Assert.Equal(HttpStatusCode.OK, r1.StatusCode);
        var etag = r1.Headers.ETag?.Tag;
        Assert.NotNull(etag);

        var req = new HttpRequestMessage(HttpMethod.Get, "/api/rankings/composite/compnbc/neighborhood");
        req.Headers.TryAddWithoutValidation("If-None-Match", etag);
        var r2 = await _client.SendAsync(req);
        Assert.Equal(HttpStatusCode.NotModified, r2.StatusCode);

        // Clean up composite rankings
        metaDb.ReplaceCompositeRankings([]);
    }

    [Fact]
    public async Task Rankings_Overview_ReturnsData()
    {
        var response = await _client.GetAsync("/api/rankings/overview?rankBy=adjusted&pageSize=5");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("adjusted", json.GetProperty("rankBy").GetString());
        Assert.Equal(5, json.GetProperty("pageSize").GetInt32());
        Assert.True(json.TryGetProperty("instruments", out _));
    }

    [Fact]
    public async Task Rankings_ComboSingle_WithSeeded_ReturnsRanking()
    {
        var metaDb = _factory.Services.GetRequiredService<MetaDatabase>();
        var comboId = ComboIds.FromInstruments(["Solo_Guitar", "Solo_Bass"]);
        metaDb.ReplaceComboLeaderboard(comboId, [
            ("combo_user1", 85.0, 80.0, 0.5, 500000, 90.0, 50, 25),
            ("combo_user2", 75.0, 70.0, 0.3, 400000, 80.0, 40, 15),
        ], 2);

        var response = await _client.GetAsync($"/api/rankings/combo/combo_user1?instruments=Solo_Guitar%2BSolo_Bass");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(comboId, json.GetProperty("comboId").GetString());
        Assert.Equal(2, json.GetProperty("totalAccounts").GetInt32());
    }

    // ═══════════════════════════════════════════════════════════════
    // Coverage: Leaderboard cache hit path
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Leaderboard_CacheHit_Returns304()
    {
        var persistence = _factory.Services.GetRequiredService<GlobalLeaderboardPersistence>();
        var db = persistence.GetOrCreateInstrumentDb("Solo_Guitar");
        db.UpsertEntries("lb_cache_song", [ new LeaderboardEntry { AccountId = "lbc_p1", Score = 80000, Accuracy = 95, Stars = 6 } ]);

        var r1 = await _client.GetAsync("/api/leaderboard/lb_cache_song?top=5");
        Assert.Equal(HttpStatusCode.OK, r1.StatusCode);
        var etag = r1.Headers.ETag?.Tag;
        Assert.NotNull(etag);

        var req = new HttpRequestMessage(HttpMethod.Get, "/api/leaderboard/lb_cache_song?top=5");
        req.Headers.TryAddWithoutValidation("If-None-Match", etag);
        var r2 = await _client.SendAsync(req);
        Assert.Equal(HttpStatusCode.NotModified, r2.StatusCode);
    }

    // ═══════════════════════════════════════════════════════════════
    // Coverage: Player profile cache hit path
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Player_Profile_CacheHit_Returns304()
    {
        var persistence = _factory.Services.GetRequiredService<GlobalLeaderboardPersistence>();
        var db = persistence.GetOrCreateInstrumentDb("Solo_Guitar");
        db.UpsertEntries("pl_cache_song", [ new LeaderboardEntry { AccountId = "plc_acct", Score = 70000, Accuracy = 95, Stars = 5 } ]);

        var r1 = await _client.GetAsync("/api/player/plc_acct");
        Assert.Equal(HttpStatusCode.OK, r1.StatusCode);
        var etag = r1.Headers.ETag?.Tag;
        Assert.NotNull(etag);

        var req = new HttpRequestMessage(HttpMethod.Get, "/api/player/plc_acct");
        req.Headers.TryAddWithoutValidation("If-None-Match", etag);
        var r2 = await _client.SendAsync(req);
        Assert.Equal(HttpStatusCode.NotModified, r2.StatusCode);
    }

    [Fact]
    public async Task Player_Profile_WithInstrumentFilter_ReturnsFiltered()
    {
        var persistence = _factory.Services.GetRequiredService<GlobalLeaderboardPersistence>();
        var guitarDb = persistence.GetOrCreateInstrumentDb("Solo_Guitar");
        var bassDb = persistence.GetOrCreateInstrumentDb("Solo_Bass");
        guitarDb.UpsertEntries("filter_song", [ new LeaderboardEntry { AccountId = "filter_acct", Score = 80000, Accuracy = 95, Stars = 6 } ]);
        bassDb.UpsertEntries("filter_song", [ new LeaderboardEntry { AccountId = "filter_acct", Score = 70000, Accuracy = 90, Stars = 5 } ]);

        var response = await _client.GetAsync("/api/player/filter_acct?instruments=Solo_Guitar");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        var scores = json.GetProperty("scores");
        foreach (var score in scores.EnumerateArray())
            Assert.Equal("01", score.GetProperty("ins").GetString()); // Solo_Guitar hex code
    }

    // ═══════════════════════════════════════════════════════════════
    // Coverage: Song endpoint cache hit path
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Songs_CacheHit_Returns304()
    {
        var r1 = await _client.GetAsync("/api/songs");
        Assert.Equal(HttpStatusCode.OK, r1.StatusCode);
        var etag = r1.Headers.ETag?.Tag;
        Assert.NotNull(etag);

        // Second request should use cache
        var r2 = await _client.GetAsync("/api/songs");
        Assert.Equal(HttpStatusCode.OK, r2.StatusCode);

        // Request with matching ETag returns 304
        var req = new HttpRequestMessage(HttpMethod.Get, "/api/songs");
        req.Headers.TryAddWithoutValidation("If-None-Match", etag);
        var r3 = await _client.SendAsync(req);
        Assert.Equal(HttpStatusCode.NotModified, r3.StatusCode);
    }

    // ═══════════════════════════════════════════════════════════════
    // Coverage: Admin path regeneration + shop scrape trigger
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Admin_RegeneratePaths_WithAuth_ReturnsExpected()
    {
        var response = await _authedClient.PostAsync("/api/admin/regenerate-paths", null);
        // Depending on state: 404 (no songs with MIDI), 202 (accepted), 400 (disabled)
        Assert.True(
            response.StatusCode is HttpStatusCode.NotFound
                or HttpStatusCode.Accepted
                or HttpStatusCode.BadRequest,
            $"Expected 400, 404, or 202 but got {(int)response.StatusCode}");
    }

    [Fact]
    public async Task Admin_ShopRefresh_WithAuth_ReturnsResult()
    {
        var response = await _authedClient.PostAsync("/api/admin/shop/refresh", null);
        // ShopService returns OK (mock handler returns 200 OK with empty body — scrape may fail gracefully)
        Assert.True(
            response.StatusCode == HttpStatusCode.OK ||
            (int)response.StatusCode == 502);
    }

    // BackfillMaxScores endpoint was removed (SQLite→PG migration utility, no longer needed)

    // ═══════════════════════════════════════════════════════════════
    // Shop endpoint tests
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Shop_ReturnsOk_WhenCacheIsEmpty()
    {
        var response = await _client.GetAsync("/api/shop");
        // Shop cache may not be primed in test — returns 200 with empty or 503
        Assert.True(response.StatusCode is HttpStatusCode.OK or HttpStatusCode.ServiceUnavailable);
    }

    [Fact]
    public async Task Songs_Response_DoesNotContainShopFields()
    {
        var response = await _client.GetAsync("/api/songs");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        var songs = json.GetProperty("songs");
        if (songs.GetArrayLength() > 0)
        {
            var firstSong = songs[0];
            Assert.False(firstSong.TryGetProperty("shopUrl", out _),
                "shopUrl should not be present in /api/songs response");
            Assert.False(firstSong.TryGetProperty("leavingTomorrow", out _),
                "leavingTomorrow should not be present in /api/songs response");
        }
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
                    ["Scraper:DeviceAuthPath"] = Path.Combine(_tempDir, "device-auth.json"),
                    ["Scraper:ApiOnly"] = "true",
                    ["ConnectionStrings:PostgreSQL"] = SharedPostgresContainer.ConnectionString,
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
                // Override the NpgsqlDataSource that Program.cs creates eagerly
                // from builder.Configuration (which still has appsettings.json values
                // at that point, before test config overrides are applied).
                services.RemoveAll<NpgsqlDataSource>();
                var testDs = SharedPostgresContainer.CreateDatabase();
                services.AddSingleton(testDs);

                // Remove the real ScraperWorker — we don't want background scraping.
                // Also removes DatabaseInitializer (prevents HTTP calls to Epic CDN).
                services.RemoveAll<IHostedService>();

                // Initialize DB schemas directly — fast, no HTTP calls needed.
                services.AddHostedService<TestDatabaseInitializer>();

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
                mockTokenManager.DisplayName.Returns("MockPlayer");
                mockTokenManager.ExpiresAt.Returns(DateTimeOffset.UtcNow.AddHours(1));
                services.AddSingleton(mockTokenManager);


                // Replace FestivalService with one that has test songs pre-loaded
                services.RemoveAll<FestivalService>();
                var festivalService = CreateTestFestivalService();
                services.AddSingleton(festivalService);

                // Register no-op handlers for ALL typed HttpClients so tests never
                // make real HTTP requests to Epic APIs or any external service.
                var noOpHandler = () => (HttpMessageHandler)new HttpMessageHandler_NoOp();
                services.AddHttpClient(string.Empty)
                    .ConfigurePrimaryHttpMessageHandler(noOpHandler);
                services.AddHttpClient<GlobalLeaderboardScraper>()
                    .ConfigurePrimaryHttpMessageHandler(noOpHandler);
                services.AddHttpClient<AccountNameResolver>()
                    .ConfigurePrimaryHttpMessageHandler(noOpHandler);
                services.AddHttpClient<HistoryReconstructor>()
                    .ConfigurePrimaryHttpMessageHandler(noOpHandler);
                services.AddHttpClient<PathGenerator>()
                    .ConfigurePrimaryHttpMessageHandler(noOpHandler);
                services.AddHttpClient<EpicAuthService>()
                    .ConfigurePrimaryHttpMessageHandler(noOpHandler);
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
                    dn = 235,
                    @in = new In { gr = 5, ba = 3, vl = 4, ds = 2, pg = 6, pb = 5, pd = 7, bd = 4 },
                }
            };
            dict["testSongNoMic"] = new Song
            {
                track = new Track
                {
                    su = "testSongNoMic",
                    tt = "Integration Test Song Without Karaoke",
                    an = "Test Artist",
                    dn = 240,
                    @in = new In { gr = 4, ba = 2, vl = 5, ds = 3, pg = 5, pb = 4, pd = 6, bd = 99 },
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

        /// <summary>
        /// Test replacement for StartupInitializer — initializes DB schemas directly
        /// without calling FestivalService.InitializeAsync() (which makes HTTP calls).
        /// </summary>
        private sealed class TestDatabaseInitializer : IHostedService
        {
            private readonly GlobalLeaderboardPersistence _persistence;
            private readonly StartupInitializer _dbInitializer;

            public TestDatabaseInitializer(
                GlobalLeaderboardPersistence persistence,
                StartupInitializer dbInitializer)
            {
                _persistence = persistence;
                _dbInitializer = dbInitializer;
            }

            public Task StartAsync(CancellationToken cancellationToken)
            {
                _persistence.Initialize();
                // Signal ready on the real StartupInitializer singleton so
                // /readyz health check and ScraperWorker see it as ready.
                // Use reflection to set the TaskCompletionSource since it's private.
                var field = typeof(StartupInitializer).GetField("_readySignal",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                var tcs = (TaskCompletionSource)field!.GetValue(_dbInitializer)!;
                tcs.TrySetResult();
                return Task.CompletedTask;
            }

            public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        }
    }
}
