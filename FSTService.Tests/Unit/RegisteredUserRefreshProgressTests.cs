using FSTService.Persistence;
using FSTService.Tests.Helpers;

namespace FSTService.Tests.Unit;

public sealed class RegisteredUserRefreshProgressTests : IDisposable
{
    private readonly InMemoryMetaDatabase _fixture = new();

    public void Dispose() => _fixture.Dispose();

    [Fact]
    public void GetRegisteredUserRefreshSongOrder_prioritizes_missing_then_oldest_coverage()
    {
        var old = new DateTime(2026, 7, 20, 12, 0, 0, DateTimeKind.Utc);
        var fresh = new DateTime(2026, 7, 31, 12, 0, 0, DateTimeKind.Utc);
        string[] instruments = ["Solo_Guitar", "Solo_Bass"];

        _fixture.Db.UpsertRegisteredUserRefreshScopes(
            10,
            instruments.Select(instrument =>
                new SoloCurrentProjectionScopeKey("song-old", instrument)).ToArray(),
            old);
        _fixture.Db.UpsertRegisteredUserRefreshScopes(
            11,
            instruments.Select(instrument =>
                new SoloCurrentProjectionScopeKey("song-fresh", instrument)).ToArray(),
            fresh);
        _fixture.Db.UpsertRegisteredUserRefreshScopes(
            12,
            [new SoloCurrentProjectionScopeKey("song-partial", "Solo_Guitar")],
            old - TimeSpan.FromDays(1));

        var ordered = _fixture.Db.GetRegisteredUserRefreshSongOrder(
            ["song-fresh", "song-missing", "song-old", "song-partial"],
            instruments);

        Assert.Equal(
            ["song-missing", "song-partial", "song-old", "song-fresh"],
            ordered);
    }

    [Fact]
    public void Scope_upsert_and_coverage_preserve_newest_successful_checkpoint()
    {
        var old = new DateTime(2026, 7, 20, 12, 0, 0, DateTimeKind.Utc);
        var newer = new DateTime(2026, 7, 31, 12, 0, 0, DateTimeKind.Utc);
        var initialScopes = new[]
        {
            new SoloCurrentProjectionScopeKey("song-a", "Solo_Guitar"),
            new SoloCurrentProjectionScopeKey("song-a", "Solo_Bass"),
        };

        Assert.Equal(
            2,
            _fixture.Db.UpsertRegisteredUserRefreshScopes(100, initialScopes, old));
        Assert.Equal(
            0,
            _fixture.Db.UpsertRegisteredUserRefreshScopes(
                99,
                [initialScopes[0]],
                newer + TimeSpan.FromHours(1)));
        Assert.Equal(
            1,
            _fixture.Db.UpsertRegisteredUserRefreshScopes(
                101,
                [initialScopes[0]],
                newer));

        var observedAt = new DateTime(2026, 8, 1, 12, 0, 0, DateTimeKind.Utc);
        var coverage = _fixture.Db.GetRegisteredUserRefreshCoverage(
            ["song-a", "song-b"],
            ["Solo_Guitar", "Solo_Bass"],
            currentScrapeId: 101,
            observedAt);

        Assert.Equal(4, coverage.ExpectedScopes);
        Assert.Equal(2, coverage.CheckedScopes);
        Assert.Equal(2, coverage.MissingScopes);
        Assert.Equal(old, coverage.OldestCheckedAtUtc);
        Assert.Equal(observedAt - old, coverage.OldestCheckedAge);
        Assert.Equal(1, coverage.CurrentScrapeCompletions);

        using var conn = _fixture.DataSource.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT status, checked_at, scrape_id
            FROM registered_user_refresh_scope_progress
            WHERE song_id = 'song-a'
              AND instrument = 'Solo_Guitar'
            """;
        using var reader = cmd.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal("complete", reader.GetString(0));
        Assert.Equal(newer, reader.GetDateTime(1));
        Assert.Equal(101, reader.GetInt64(2));
    }
}
