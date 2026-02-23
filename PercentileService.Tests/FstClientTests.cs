using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using PercentileService.Tests.Helpers;

namespace PercentileService.Tests;

public sealed class FstClientTests
{
    [Fact]
    public async Task GetPlayerEntriesAsync_returns_entries_with_positive_scores()
    {
        var profileJson = """
        {
            "accountId": "test-account",
            "displayName": "TestUser",
            "totalScores": 3,
            "scores": [
                { "songId": "song1", "instrument": "Solo_Guitar", "score": 500000 },
                { "songId": "song2", "instrument": "Solo_Drums", "score": 0 },
                { "songId": "song3", "instrument": "Solo_Bass", "score": 123 }
            ]
        }
        """;
        var handler = MockHttpHandler.WithJsonResponse(profileJson);
        var client = TestFactory.CreateFstClient(handler);

        var entries = await client.GetPlayerEntriesAsync("test-account", CancellationToken.None);

        Assert.Equal(2, entries.Count);
        Assert.Equal("song1", entries[0].SongId);
        Assert.Equal("Solo_Guitar", entries[0].Instrument);
        Assert.Equal("song3", entries[1].SongId);
        Assert.Equal("Solo_Bass", entries[1].Instrument);
    }

    [Fact]
    public async Task GetPlayerEntriesAsync_returns_empty_list_when_no_scores()
    {
        var profileJson = """
        {
            "accountId": "test-account",
            "displayName": "TestUser",
            "totalScores": 0,
            "scores": []
        }
        """;
        var handler = MockHttpHandler.WithJsonResponse(profileJson);
        var client = TestFactory.CreateFstClient(handler);

        var entries = await client.GetPlayerEntriesAsync("test-account", CancellationToken.None);

        Assert.Empty(entries);
    }

    [Fact]
    public async Task GetPlayerEntriesAsync_returns_empty_when_scores_null()
    {
        var profileJson = """
        {
            "accountId": "test-account",
            "displayName": null,
            "totalScores": 0
        }
        """;
        var handler = MockHttpHandler.WithJsonResponse(profileJson);
        var client = TestFactory.CreateFstClient(handler);

        var entries = await client.GetPlayerEntriesAsync("test-account", CancellationToken.None);

        Assert.Empty(entries);
    }

    [Fact]
    public async Task GetPlayerEntriesAsync_sends_api_key_header()
    {
        var handler = MockHttpHandler.WithJsonResponse("""{ "scores": [] }""");
        var client = TestFactory.CreateFstClient(handler, o => o.FstApiKey = "my-secret-key");

        await client.GetPlayerEntriesAsync("acct", CancellationToken.None);

        Assert.Single(handler.Requests);
        Assert.True(handler.Requests[0].Headers.Contains("X-API-Key"));
        Assert.Equal("my-secret-key", handler.Requests[0].Headers.GetValues("X-API-Key").First());
    }

    [Fact]
    public async Task GetPlayerEntriesAsync_uses_correct_url()
    {
        var handler = MockHttpHandler.WithJsonResponse("""{ "scores": [] }""");
        var client = TestFactory.CreateFstClient(handler);

        await client.GetPlayerEntriesAsync("myAcct123", CancellationToken.None);

        var url = handler.Requests[0].RequestUri!.PathAndQuery;
        Assert.Contains("api/player/myAcct123", url);
    }

    [Fact]
    public async Task GetPlayerEntriesAsync_throws_on_non_success()
    {
        var handler = MockHttpHandler.WithJsonResponse("{}", HttpStatusCode.InternalServerError);
        var client = TestFactory.CreateFstClient(handler);

        await Assert.ThrowsAsync<HttpRequestException>(() =>
            client.GetPlayerEntriesAsync("acct", CancellationToken.None));
    }

    [Fact]
    public async Task PostLeaderboardPopulationAsync_sends_correct_request()
    {
        var handler = MockHttpHandler.WithJsonResponse("""{ "upserted": 2 }""");
        var client = TestFactory.CreateFstClient(handler, o => o.FstApiKey = "the-key");

        var items = new List<LeaderboardPopulationItem>
        {
            new() { SongId = "s1", Instrument = "Solo_Guitar", TotalEntries = 100000 },
            new() { SongId = "s2", Instrument = "Solo_Drums", TotalEntries = 50000 },
        };

        await client.PostLeaderboardPopulationAsync(items, CancellationToken.None);

        Assert.Single(handler.Requests);
        var req = handler.Requests[0];
        Assert.Equal(HttpMethod.Post, req.Method);
        Assert.Contains("api/leaderboard-population", req.RequestUri!.PathAndQuery);
        Assert.Equal("the-key", req.Headers.GetValues("X-API-Key").First());

        // Verify body is JSON array
        var bodyStr = await req.Content!.ReadAsStringAsync();
        var body = JsonSerializer.Deserialize<JsonElement>(bodyStr);
        Assert.Equal(JsonValueKind.Array, body.ValueKind);
        Assert.Equal(2, body.GetArrayLength());
    }

    [Fact]
    public async Task PostLeaderboardPopulationAsync_no_op_for_empty_list()
    {
        var handler = MockHttpHandler.WithJsonResponse("{}");
        var client = TestFactory.CreateFstClient(handler);

        await client.PostLeaderboardPopulationAsync([], CancellationToken.None);

        Assert.Empty(handler.Requests); // Should not make any HTTP call
    }

    [Fact]
    public async Task PostLeaderboardPopulationAsync_throws_on_failure()
    {
        var handler = MockHttpHandler.WithJsonResponse(
            """{ "error": "bad" }""", HttpStatusCode.BadRequest);
        var client = TestFactory.CreateFstClient(handler);

        var items = new List<LeaderboardPopulationItem>
        {
            new() { SongId = "s", Instrument = "i", TotalEntries = 1 },
        };

        await Assert.ThrowsAsync<HttpRequestException>(() =>
            client.PostLeaderboardPopulationAsync(items, CancellationToken.None));
    }
}
