using FSTService.Persistence;
using FSTService.Scraping;
using FSTService.Tests.Helpers;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace FSTService.Tests.Unit;

public sealed class BandCurrentProjectionStaleSweepTests
{
    private const string BandType = "Band_Duets";

    [Fact]
    public async Task EmptiedSourceScopeConvergesAfterOneRebuild()
    {
        using var fixture = new InMemoryMetaDatabase();
        Seed(fixture, "song-a", teams: 4);
        var scope = Overall("song-a");
        var builder = CreateBuilder(fixture);
        await builder.RefreshScopesAsync([scope], Options());
        Assert.Equal(4, await ScopeRowCountAsync(fixture, scope));

        await ExecuteAsync(fixture, "DELETE FROM band_member_stats WHERE song_id = 'song-a'; DELETE FROM band_entries WHERE song_id = 'song-a';");

        Assert.Equal([scope], await builder.SelectScopesNeedingRefreshAsync([scope], 0));
        var rebuilt = await builder.RefreshScopesAsync([scope], Options());
        Assert.Equal(1, rebuilt.ScopeCount);
        Assert.Equal(0, await ScopeRowCountAsync(fixture, scope));

        Assert.Empty(await builder.SelectScopesNeedingRefreshAsync([scope], 0));
        var again = await builder.RefreshScopesAsync([scope], Options());
        Assert.Equal(0, again.ScopeCount);
    }

    [Fact]
    public async Task SelectionReturnsOnlyDriftedScopesInOrderAndHonorsCap()
    {
        using var fixture = new InMemoryMetaDatabase();
        foreach (var song in new[] { "song-a", "song-b", "song-c" })
            Seed(fixture, song, teams: 3);
        var builder = CreateBuilder(fixture);
        var overall = new[] { Overall("song-a"), Overall("song-b"), Overall("song-c") };
        await builder.RefreshScopesAsync(overall, Options());
        Assert.Empty(await builder.SelectScopesNeedingRefreshAsync(overall, 0));

        await Task.Delay(20);
        Seed(fixture, "song-b", teams: 5);
        Seed(fixture, "song-c", teams: 6);

        Assert.Equal(
            [Overall("song-b"), Overall("song-c")],
            await builder.SelectScopesNeedingRefreshAsync(overall, 0));
        Assert.Equal(
            [Overall("song-b")],
            await builder.SelectScopesNeedingRefreshAsync(overall, 1));
    }

    [Fact]
    public async Task OnePassSelectionMatchesSeparateImpactedAndStaleFilters()
    {
        using var fixture = new InMemoryMetaDatabase();
        foreach (var song in new[] { "song-a", "song-b", "song-c", "song-d" })
            Seed(fixture, song, teams: 3);
        var builder = CreateBuilder(fixture);
        await builder.RefreshScopesAsync(await builder.LoadCurrentScopesAsync(), Options());

        await Task.Delay(20);
        Seed(fixture, "song-b", teams: 5);
        Seed(fixture, "song-c", teams: 6);
        Seed(fixture, "song-d", teams: 7);
        // song-a is impacted but unchanged; song-b is impacted and changed;
        // song-c and song-d drifted outside the impacted set.
        IReadOnlyCollection<BandCurrentProjectionScopeKey> impacted = [Overall("song-a"), Overall("song-b")];
        var candidates = (await builder.LoadCurrentScopesAsync())
            .Concat(await builder.LoadProjectionScopeKeysAsync())
            .ToArray();

        var selection = await builder.SelectImpactedAndStaleScopesAsync(impacted, candidates, maxStaleScopes: 1);

        Assert.Equal([Overall("song-b")], selection.ImpactedScopes);
        Assert.Equal(
            await builder.SelectScopesNeedingRefreshAsync(
                candidates.Where(scope => !impacted.Contains(scope)).Distinct().ToArray(),
                1),
            selection.StaleScopes);
        // song-b's combo scope changed but is not in the impacted set.
        Assert.Equal(new BandCurrentProjectionScopeKey("song-b", BandType, "combo", Assert.Single(selection.StaleScopes).ScopeComboId), selection.StaleScopes[0]);
        Assert.Equal(candidates.Distinct().Count() - impacted.Count, selection.SweepCandidateCount);
        Assert.Empty((await builder.SelectImpactedAndStaleScopesAsync(impacted, candidates, maxStaleScopes: 0)).StaleScopes);
    }

    [Fact]
    public async Task ScopeKeyAndSourceLoadersCoverProjectionAndSourceScopes()
    {
        using var fixture = new InMemoryMetaDatabase();
        Seed(fixture, "song-a", teams: 2);
        var builder = CreateBuilder(fixture);
        var sourceScopes = await builder.LoadCurrentScopesAsync();
        Assert.Contains(Overall("song-a"), sourceScopes);
        Assert.Empty(await builder.LoadProjectionScopeKeysAsync());

        await builder.RefreshScopesAsync(sourceScopes, Options());

        var projectionKeys = await builder.LoadProjectionScopeKeysAsync();
        Assert.Equal(
            sourceScopes.OrderBy(static s => s.ToString(), StringComparer.Ordinal),
            projectionKeys.OrderBy(static s => s.ToString(), StringComparer.Ordinal));
        Assert.Empty(await builder.SelectScopesNeedingRefreshAsync(
            [.. projectionKeys, new BandCurrentProjectionScopeKey("song-a", BandType, "combo", "not-a-combo")],
            0));
    }

    private static BandCurrentProjectionScopeKey Overall(string songId) =>
        new(songId, BandType, "overall", string.Empty);

    private static BandCurrentProjectionRebuildOptions Options() => new()
    {
        DisableSynchronousCommit = true,
        SkipUnchangedScopes = true,
        PublishOnSuccess = true,
    };

    private static BandCurrentProjectionBuilder CreateBuilder(InMemoryMetaDatabase fixture) =>
        new(fixture.DataSource, Substitute.For<ILogger<BandCurrentProjectionBuilder>>());

    private static void Seed(InMemoryMetaDatabase fixture, string songId, int teams)
    {
        var persistence = new BandLeaderboardPersistence(
            fixture.DataSource,
            Substitute.For<ILogger<BandLeaderboardPersistence>>());
        persistence.UpsertBandEntries(
            songId,
            BandType,
            Enumerable.Range(0, teams).Select(team => Entry(songId, team)).ToArray());
    }

    private static BandLeaderboardEntry Entry(string songId, int team)
    {
        var members = new[] { $"{songId}-{team:D3}-a", $"{songId}-{team:D3}-b" };
        return new BandLeaderboardEntry
        {
            TeamKey = string.Join(':', members.Order(StringComparer.Ordinal)),
            TeamMembers = members,
            InstrumentCombo = "0:1",
            Score = 1_000_000 - team,
            Accuracy = 990_000 - team,
            IsFullCombo = false,
            Stars = 5,
            Difficulty = 3,
            Season = 1,
            Rank = team + 1,
            Percentile = (team + 1d) / 100d,
            EndTime = $"2026-08-16T00:{team % 60:D2}:00Z",
            Source = "test",
            MemberStats =
            [
                new BandMemberStats { MemberIndex = 0, AccountId = members[0], InstrumentId = 0, Score = 500_000, Accuracy = 980_000, Stars = 5, Difficulty = 3 },
                new BandMemberStats { MemberIndex = 1, AccountId = members[1], InstrumentId = 1, Score = 500_000, Accuracy = 980_000, Stars = 5, Difficulty = 3 },
            ],
        };
    }

    private static async Task<long> ScopeRowCountAsync(
        InMemoryMetaDatabase fixture,
        BandCurrentProjectionScopeKey scope)
    {
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT row_count FROM band_current_projection_scope
            WHERE song_id = @songId AND band_type = @bandType
              AND ranking_scope = @rankingScope AND scope_combo_id = @scopeComboId
            """;
        command.Parameters.AddWithValue("songId", scope.SongId);
        command.Parameters.AddWithValue("bandType", scope.BandType);
        command.Parameters.AddWithValue("rankingScope", scope.RankingScope);
        command.Parameters.AddWithValue("scopeComboId", scope.ScopeComboId);
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    private static async Task ExecuteAsync(InMemoryMetaDatabase fixture, string sql)
    {
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }
}
