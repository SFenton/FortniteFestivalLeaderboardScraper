using System.Net;
using System.Text.Json;
using PercentileService.Tests.Helpers;

namespace PercentileService.Tests;

public sealed class LeaderboardQuerierTests
{
    private const string AccountId = "195e93ef108143b2975ee46662d4d0e1";

    [Fact]
    public async Task QueryAsync_parses_entry_and_derives_totalEntries()
    {
        // rank=19, percentile=1.378e-05 → totalEntries ≈ 1,378,810
        var json = """
        {
            "entries": [
                {
                    "teamAccountIds": ["195e93ef108143b2975ee46662d4d0e1"],
                    "rank": 19,
                    "score": 696274,
                    "percentile": 1.378e-05
                }
            ]
        }
        """;
        var handler = MockHttpHandler.WithJsonResponse(json);
        var querier = TestFactory.CreateQuerier(handler);

        var result = await querier.QueryAsync("ThroughTheFireAndFlames", "Solo_Guitar", AccountId, "token123");

        Assert.NotNull(result);
        Assert.Equal("ThroughTheFireAndFlames", result.SongId);
        Assert.Equal("Solo_Guitar", result.Instrument);
        Assert.Equal(19, result.Rank);
        Assert.Equal(696274, result.Score);
        Assert.Equal(1.378e-05, result.Percentile);
        Assert.True(result.TotalEntries > 1_000_000);
    }

    [Fact]
    public async Task QueryAsync_builds_correct_url()
    {
        var handler = MockHttpHandler.WithJsonResponse("""{ "entries": [] }""");
        var querier = TestFactory.CreateQuerier(handler);

        await querier.QueryAsync("TestSong", "Solo_Drums", AccountId, "myToken");

        Assert.Single(handler.Requests);
        var url = handler.Requests[0].RequestUri!.ToString();
        Assert.Contains("alltime_TestSong_Solo_Drums", url);
        Assert.Contains($"teamAccountIds={AccountId}", url);
        Assert.Contains("appId=Fortnite", url);
    }

    [Fact]
    public async Task QueryAsync_sets_bearer_token()
    {
        var handler = MockHttpHandler.WithJsonResponse("""{ "entries": [] }""");
        var querier = TestFactory.CreateQuerier(handler);

        await querier.QueryAsync("s", "i", AccountId, "secret-token");

        var authHeader = handler.Requests[0].Headers.Authorization;
        Assert.NotNull(authHeader);
        Assert.Equal("Bearer", authHeader.Scheme);
        Assert.Equal("secret-token", authHeader.Parameter);
    }

    [Fact]
    public async Task QueryAsync_returns_null_for_404()
    {
        var handler = MockHttpHandler.WithJsonResponse("{}", HttpStatusCode.NotFound);
        var querier = TestFactory.CreateQuerier(handler);

        var result = await querier.QueryAsync("s", "i", AccountId, "token");

        Assert.Null(result);
    }

    [Fact]
    public async Task QueryAsync_returns_null_for_non_success()
    {
        var handler = MockHttpHandler.WithJsonResponse("""{"error":"forbidden"}""",
            HttpStatusCode.Forbidden);
        var querier = TestFactory.CreateQuerier(handler);

        var result = await querier.QueryAsync("s", "i", AccountId, "token");

        Assert.Null(result);
    }

    [Fact]
    public async Task QueryAsync_returns_null_when_no_entries_property()
    {
        var handler = MockHttpHandler.WithJsonResponse("""{ "totalPages": 100 }""");
        var querier = TestFactory.CreateQuerier(handler);

        var result = await querier.QueryAsync("s", "i", AccountId, "token");

        Assert.Null(result);
    }

    [Fact]
    public async Task QueryAsync_returns_null_when_entries_empty()
    {
        var handler = MockHttpHandler.WithJsonResponse("""{ "entries": [] }""");
        var querier = TestFactory.CreateQuerier(handler);

        var result = await querier.QueryAsync("s", "i", AccountId, "token");

        Assert.Null(result);
    }

    [Fact]
    public async Task QueryAsync_skips_entries_without_matching_teamAccountIds()
    {
        var json = """
        {
            "entries": [
                {
                    "teamAccountIds": ["other-account-id"],
                    "rank": 1,
                    "score": 999999,
                    "percentile": 0.001
                }
            ]
        }
        """;
        var handler = MockHttpHandler.WithJsonResponse(json);
        var querier = TestFactory.CreateQuerier(handler);

        var result = await querier.QueryAsync("s", "i", AccountId, "token");

        Assert.Null(result);
    }

    [Fact]
    public async Task QueryAsync_returns_minus1_totalEntries_when_percentile_zero()
    {
        var json = """
        {
            "entries": [
                {
                    "teamAccountIds": ["195e93ef108143b2975ee46662d4d0e1"],
                    "rank": 1,
                    "score": 100,
                    "percentile": 0.0
                }
            ]
        }
        """;
        var handler = MockHttpHandler.WithJsonResponse(json);
        var querier = TestFactory.CreateQuerier(handler);

        var result = await querier.QueryAsync("s", "i", AccountId, "token");

        Assert.NotNull(result);
        Assert.Equal(-1, result.TotalEntries);
    }

    [Fact]
    public async Task QueryAsync_returns_null_on_http_exception()
    {
        var handler = new MockHttpHandler(_ =>
            throw new HttpRequestException("connection refused"));
        var querier = TestFactory.CreateQuerier(handler);

        var result = await querier.QueryAsync("s", "i", AccountId, "token");

        Assert.Null(result);
    }

    [Fact]
    public async Task QueryAsync_skips_entries_without_teamAccountIds()
    {
        var json = """
        {
            "entries": [
                {
                    "rank": 1,
                    "score": 100,
                    "percentile": 0.5
                }
            ]
        }
        """;
        var handler = MockHttpHandler.WithJsonResponse(json);
        var querier = TestFactory.CreateQuerier(handler);

        var result = await querier.QueryAsync("s", "i", AccountId, "token");

        Assert.Null(result);
    }

    [Fact]
    public async Task QueryAsync_skips_entries_with_empty_teamAccountIds_array()
    {
        var json = """
        {
            "entries": [
                {
                    "teamAccountIds": [],
                    "rank": 1,
                    "score": 100,
                    "percentile": 0.5
                }
            ]
        }
        """;
        var handler = MockHttpHandler.WithJsonResponse(json);
        var querier = TestFactory.CreateQuerier(handler);

        var result = await querier.QueryAsync("s", "i", AccountId, "token");

        Assert.Null(result);
    }
}
