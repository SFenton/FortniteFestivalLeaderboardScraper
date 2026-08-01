using FSTService.Exports;
using FSTService.Persistence;
using FSTService.Tests.Helpers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace FSTService.Tests.Unit;

public sealed class PlayerDataExportPublishedSourceTests : IDisposable
{
    private readonly InMemoryMetaDatabase _fixture = new();
    private readonly GlobalLeaderboardPersistence _persistence;

    public PlayerDataExportPublishedSourceTests()
    {
        _persistence = new GlobalLeaderboardPersistence(
            _fixture.Db,
            NullLoggerFactory.Instance,
            NullLogger<GlobalLeaderboardPersistence>.Instance,
            _fixture.DataSource,
            Options.Create(new FeatureOptions { UsePublishedScopeSources = true }));
        _persistence.Initialize();
    }

    public void Dispose()
    {
        _persistence.Dispose();
        _fixture.Dispose();
    }

    [Fact]
    public void Published_solo_export_uses_per_scope_source_instead_of_active_snapshot()
    {
        ScrapeRunTestHelper.EnsureAllocated(
            _fixture.DataSource,
            40,
            completed: true);
        ScrapeRunTestHelper.EnsureAllocated(
            _fixture.DataSource,
            41,
            completed: true);
        ScrapeRunTestHelper.EnsureAllocated(
            _fixture.DataSource,
            42,
            completed: true);
        using (var conn = _fixture.DataSource.OpenConnection())
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                INSERT INTO songs (song_id, title, artist, lead_diff)
                VALUES
                    ('song_1', 'Song 1', 'Artist', 3),
                    ('song_empty', 'Empty Song', 'Artist', 3);

                INSERT INTO leaderboard_entries_snapshot (
                    snapshot_id, song_id, instrument, account_id, score, rank, source,
                    first_seen_at, last_updated_at)
                VALUES
                    (40, 'song_1', 'Solo_Guitar', 'acct_1', 100000, 1, 'scrape', now(), now()),
                    (41, 'song_1', 'Solo_Guitar', 'acct_1', 200000, 1, 'scrape', now(), now());

                INSERT INTO leaderboard_snapshot_state (
                    song_id, instrument, active_snapshot_id, scrape_id, is_finalized, updated_at)
                VALUES
                    ('song_1', 'Solo_Guitar', 41, 41, TRUE, now()),
                    ('song_empty', 'Solo_Guitar', 41, 41, TRUE, now());

                INSERT INTO leaderboard_entries (
                    song_id, instrument, account_id, score, rank, source,
                    first_seen_at, last_updated_at)
                VALUES ('song_empty', 'Solo_Guitar', 'acct_legacy', 999000, 1, 'scrape', now(), now());

                INSERT INTO leaderboard_population (song_id, instrument, total_entries, updated_at)
                VALUES
                    ('song_1', 'Solo_Guitar', 999, now()),
                    ('song_empty', 'Solo_Guitar', 999, now());

                INSERT INTO solo_current_projection_scope (
                    song_id, instrument, projection_generation, row_count, source_snapshot_id,
                    status, updated_at)
                VALUES
                    ('song_1', 'Solo_Guitar', 1, 1, 40, 'ready', now()),
                    ('song_empty', 'Solo_Guitar', 1, 0, NULL, 'ready', now());

                INSERT INTO current_leaderboard_entries (
                    song_id, instrument, account_id, score, rank, source,
                    first_seen_at, last_updated_at, projection_generation, computed_at)
                VALUES (
                    'song_1', 'Solo_Guitar', 'acct_1', 100000, 1, 'projection',
                    now(), now(), 1, now());

                INSERT INTO scrape_publication_state (id, published_scrape_id, published_at, updated_at)
                VALUES (TRUE, 42, now(), now())
                ON CONFLICT (id) DO UPDATE SET
                    published_scrape_id = EXCLUDED.published_scrape_id,
                    published_at = EXCLUDED.published_at,
                    updated_at = EXCLUDED.updated_at;

                INSERT INTO leaderboard_published_scope_source (
                    published_scrape_id, song_id, instrument, scope_kind, source_kind,
                    source_snapshot_id, source_scrape_id, row_count, content_fingerprint,
                    coverage_fingerprint, reported_total_entries, reported_total_pages,
                    is_complete, created_at, validated_at)
                VALUES (
                    42, 'song_1', 'Solo_Guitar', 'alltime', 'snapshot',
                    40, 40, 1, md5('content'), md5('coverage'), 1, 1,
                    TRUE, now(), now()),
                    (
                    42, 'song_empty', 'Solo_Guitar', 'alltime', 'empty',
                    NULL, 42, 0, md5('empty'), md5('empty-coverage'), 0, 0,
                    TRUE, now(), now());

                INSERT INTO band_identity (
                    band_id, band_type, team_key, member_account_ids, appearance_count,
                    first_seen_at, last_seen_at, updated_at, source)
                VALUES (
                    'band-test', 'Band_Duets', 'acct_1:acct_2', ARRAY['acct_1','acct_2'], 1,
                    now(), now(), now(), 'test');

                INSERT INTO band_members (account_id, song_id, band_type, team_key, instrument_combo)
                VALUES
                    ('acct_1', 'song_1', 'Band_Duets', 'acct_1:acct_2', '12'),
                    ('acct_2', 'song_1', 'Band_Duets', 'acct_1:acct_2', '12');

                INSERT INTO band_current_projection_scope (
                    song_id, band_type, ranking_scope, scope_combo_id, projection_generation,
                    published_generation, row_count, published_row_count, status, updated_at)
                VALUES (
                    'song_1', 'Band_Duets', 'overall', '', 7,
                    7, 1, 1, 'ready', now());

                INSERT INTO current_band_leaderboard_entries (
                    song_id, band_type, ranking_scope, scope_combo_id, team_key,
                    entry_combo_id, entry_instrument_combo, team_members,
                    member_account_ids, member_instrument_ids, member_scores,
                    member_accuracies, member_full_combos, member_stars,
                    member_difficulties, score, rank, first_seen_at, last_updated_at,
                    projection_generation, computed_at)
                VALUES (
                    'song_1', 'Band_Duets', 'overall', '', 'acct_1:acct_2',
                    '12', '12', ARRAY['acct_1','acct_2'],
                    ARRAY['acct_1','acct_2'], ARRAY[0,1], ARRAY[60000,40000],
                    ARRAY[95,90], ARRAY[1,0], ARRAY[5,5],
                    ARRAY[3,2], 100000, 1, now(), now(),
                    7, now());
                """;
            cmd.ExecuteNonQuery();
        }

        var export = new PlayerDataExportService(_persistence, _fixture.Db, _fixture.DataSource);
        var scores = export.LoadPublishedSoloScores("acct_1");
        var publishedProfile = _persistence.GetCurrentStatePlayerProfileWithFallback("acct_1");
        var leakedLegacyProfile = _persistence.GetCurrentStatePlayerProfileWithFallback("acct_legacy");
        var population = _persistence.GetCurrentStateLeaderboardPopulation();
        var memberSongs = _persistence.GetCurrentStateSongIdsForMemberScoreFilter(
            ["acct_1"],
            [],
            ["Solo_Guitar"]);

        var score = Assert.Single(scores);
        Assert.Equal("song_1", score.SongId);
        Assert.Equal("Solo_Guitar", score.Instrument);
        Assert.Equal(100_000, score.Score);
        Assert.Equal(100_000, Assert.Single(publishedProfile).Score);
        Assert.Empty(leakedLegacyProfile);
        Assert.Equal(1, population[("song_1", "Solo_Guitar")]);
        Assert.Equal(0, population[("song_empty", "Solo_Guitar")]);
        Assert.Equal(1, _persistence.GetCurrentStateLeaderboardPopulation("song_1", "Solo_Guitar"));
        Assert.Equal(0, _persistence.GetCurrentStateLeaderboardPopulation("song_empty", "Solo_Guitar"));
        Assert.Equal(["song_1"], memberSongs);
        Assert.NotEmpty(export.BuildPlayerArchive("acct_1", usePublishedSnapshot: true).Content);

        using var rollbackPersistence = new GlobalLeaderboardPersistence(
            _fixture.Db,
            NullLoggerFactory.Instance,
            NullLogger<GlobalLeaderboardPersistence>.Instance,
            _fixture.DataSource,
            Options.Create(new FeatureOptions { UsePublishedScopeSources = false }));
        rollbackPersistence.Initialize();
        var rollbackExport = new PlayerDataExportService(rollbackPersistence, _fixture.Db, _fixture.DataSource);

        Assert.Equal(200_000, Assert.Single(rollbackExport.LoadPublishedSoloScores("acct_1")).Score);
    }
}
