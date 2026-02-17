using FSTService.Scraping;
using FSTService.Persistence;

namespace FSTService.Tests.Unit;

/// <summary>
/// Tests for the internal static helper methods on <see cref="HistoryReconstructor"/>.
/// These are pure functions (no I/O, no mocking required).
/// </summary>
public class HistoryReconstructorTests
{
    // ═══ ExtractSeasonNumber ════════════════════════════════════

    [Theory]
    [InlineData("season_1", 1)]
    [InlineData("season_3", 3)]
    [InlineData("season_15", 15)]
    [InlineData("Season_2", 2)]
    [InlineData("SEASON_4", 4)]
    public void ExtractSeasonNumber_matches_season_underscore_N(string input, int expected)
    {
        Assert.Equal(expected, HistoryReconstructor.ExtractSeasonNumber(input));
    }

    // ═══ GetSeasonPrefix ════════════════════════════════════════

    [Theory]
    [InlineData(1, "evergreen")]
    [InlineData(2, "season002")]
    [InlineData(3, "season003")]
    [InlineData(9, "season009")]
    [InlineData(10, "season010")]
    [InlineData(15, "season015")]
    public void GetSeasonPrefix_returns_correct_format(int season, string expected)
    {
        Assert.Equal(expected, HistoryReconstructor.GetSeasonPrefix(season));
    }

    [Theory]
    [InlineData("Season1", 1)]
    [InlineData("Season10", 10)]
    public void ExtractSeasonNumber_matches_SeasonN_no_separator(string input, int expected)
    {
        Assert.Equal(expected, HistoryReconstructor.ExtractSeasonNumber(input));
    }

    [Theory]
    [InlineData("s_1", 1)]
    [InlineData("s_5", 5)]
    [InlineData("S_3", 3)]
    [InlineData("s3", 3)]
    public void ExtractSeasonNumber_matches_short_form(string input, int expected)
    {
        Assert.Equal(expected, HistoryReconstructor.ExtractSeasonNumber(input));
    }

    [Theory]
    [InlineData("")]
    [InlineData("noseason")]
    [InlineData("random_string")]
    [InlineData("epoch_123")]
    public void ExtractSeasonNumber_returns_zero_for_no_match(string input)
    {
        Assert.Equal(0, HistoryReconstructor.ExtractSeasonNumber(input));
    }

    // ═══ ParseSeasonWindowsFromEventsJson ═══════════════════════

    [Fact]
    public void ParseSeasonWindows_parses_valid_events_json()
    {
        var json = """
        {
            "events": [
                {
                    "eventId": "evt_fnfestival",
                    "eventWindows": [
                        { "eventWindowId": "season_1" },
                        { "eventWindowId": "season_2" },
                        { "eventWindowId": "season_3" }
                    ]
                }
            ]
        }
        """;

        var results = HistoryReconstructor.ParseSeasonWindowsFromEventsJson(json);

        Assert.Equal(3, results.Count);
        Assert.Equal(1, results[0].SeasonNumber);
        Assert.Equal("season_1", results[0].WindowId);
        Assert.Equal("evt_fnfestival", results[0].EventId);
        Assert.Equal(2, results[1].SeasonNumber);
        Assert.Equal(3, results[2].SeasonNumber);
    }

    [Fact]
    public void ParseSeasonWindows_deduplicates_by_season_number()
    {
        var json = """
        {
            "events": [
                {
                    "eventId": "evt_a",
                    "eventWindows": [
                        { "eventWindowId": "season_1" }
                    ]
                },
                {
                    "eventId": "evt_b",
                    "eventWindows": [
                        { "eventWindowId": "Season_1" }
                    ]
                }
            ]
        }
        """;

        var results = HistoryReconstructor.ParseSeasonWindowsFromEventsJson(json);
        Assert.Single(results);
        Assert.Equal(1, results[0].SeasonNumber);
    }

    [Fact]
    public void ParseSeasonWindows_orders_by_season_number()
    {
        var json = """
        {
            "events": [
                {
                    "eventId": "evt",
                    "eventWindows": [
                        { "eventWindowId": "season_3" },
                        { "eventWindowId": "season_1" },
                        { "eventWindowId": "season_2" }
                    ]
                }
            ]
        }
        """;

        var results = HistoryReconstructor.ParseSeasonWindowsFromEventsJson(json);
        Assert.Equal(3, results.Count);
        Assert.Equal(1, results[0].SeasonNumber);
        Assert.Equal(2, results[1].SeasonNumber);
        Assert.Equal(3, results[2].SeasonNumber);
    }

    [Fact]
    public void ParseSeasonWindows_skips_non_seasonal_windows()
    {
        var json = """
        {
            "events": [
                {
                    "eventId": "evt",
                    "eventWindows": [
                        { "eventWindowId": "random_window" },
                        { "eventWindowId": "season_5" },
                        { "eventWindowId": "not_a_season" }
                    ]
                }
            ]
        }
        """;

        var results = HistoryReconstructor.ParseSeasonWindowsFromEventsJson(json);
        Assert.Single(results);
        Assert.Equal(5, results[0].SeasonNumber);
    }

    [Fact]
    public void ParseSeasonWindows_returns_empty_for_empty_json()
    {
        var results = HistoryReconstructor.ParseSeasonWindowsFromEventsJson("{}");
        Assert.Empty(results);
    }

    [Fact]
    public void ParseSeasonWindows_returns_empty_for_malformed_json()
    {
        var results = HistoryReconstructor.ParseSeasonWindowsFromEventsJson("not valid json {{");
        Assert.Empty(results);
    }

    [Fact]
    public void ParseSeasonWindows_returns_empty_when_no_events_property()
    {
        var json = """{ "something": "else" }""";
        var results = HistoryReconstructor.ParseSeasonWindowsFromEventsJson(json);
        Assert.Empty(results);
    }

    [Fact]
    public void ParseSeasonWindows_handles_missing_eventId()
    {
        var json = """
        {
            "events": [
                {
                    "eventWindows": [
                        { "eventWindowId": "season_1" }
                    ]
                }
            ]
        }
        """;

        var results = HistoryReconstructor.ParseSeasonWindowsFromEventsJson(json);
        Assert.Single(results);
        Assert.Equal("", results[0].EventId);
    }

    [Fact]
    public void ParseSeasonWindows_handles_missing_eventWindowId()
    {
        var json = """
        {
            "events": [
                {
                    "eventId": "evt",
                    "eventWindows": [
                        { "someOtherProp": "value" }
                    ]
                }
            ]
        }
        """;

        var results = HistoryReconstructor.ParseSeasonWindowsFromEventsJson(json);
        Assert.Empty(results);
    }
}
