using System.Text.Json;
using FSTService.Persistence;
using FSTService.Scraping;
using FSTService.Tests.Helpers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace FSTService.Tests.Unit;

public sealed class PostScrapeBandExtractorTests : IDisposable
{
    private readonly TempInstrumentDatabase _fixture = new();

    public void Dispose() => _fixture.Dispose();

    [Fact]
    public async Task SnapshotDerivedContextSource_PreservesBandContextAndIgnoresLegacyOnlyRows()
    {
        var snapshotMemberRows = CreateMembers("acct-a", "acct-b");
        var overlayMemberRows = CreateMembers("acct-e", "acct-f");
        var snapshotMembers = JsonSerializer.Serialize(snapshotMemberRows);
        var legacyMembers = SerializeMembers("acct-c", "acct-d");

        using (var connection = _fixture.DataSource.OpenConnection())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                INSERT INTO leaderboard_band_context (
                    song_id, instrument, account_id, score, accuracy, is_full_combo,
                    stars, season, difficulty, end_time, band_members_json,
                    band_score, base_score, instrument_bonus, overdrive_bonus, instrument_combo,
                    first_seen_at, last_updated_at)
                VALUES (
                    'song-snapshot', 'Solo_Guitar', 'acct-a', 100000, 98, TRUE,
                    6, 3, 3, '2026-01-01T00:00:00Z', @snapshotMembers,
                    200000, 150000, 30000, 20000, '0:1', now(), now());

                INSERT INTO leaderboard_band_context (
                    song_id, instrument, account_id, score, accuracy, is_full_combo,
                    stars, season, difficulty, end_time, band_members_json,
                    band_score, base_score, instrument_bonus, overdrive_bonus, instrument_combo,
                    first_seen_at, last_updated_at)
                VALUES (
                    'song-same-score', 'Solo_Guitar', 'acct-a', 100000, 90, FALSE,
                    4, 3, 2, '2026-01-01T00:00:00Z', @snapshotMembers,
                    180000, 140000, 25000, 15000, '0:1', now(), now());

                INSERT INTO leaderboard_entries (
                    song_id, instrument, account_id, score, accuracy, is_full_combo, stars,
                    season, difficulty, rank, source, end_time, band_members_json, band_score,
                    base_score, instrument_bonus, overdrive_bonus, instrument_combo,
                    first_seen_at, last_updated_at)
                VALUES (
                    'song-legacy', 'Solo_Drums', 'acct-c', 999000, 100, TRUE, 6,
                    3, 3, 1, 'backfill', '2026-01-04T00:00:00Z', @legacyMembers, 999000,
                    900000, 50000, 49000, '2:3', now(), now());

                INSERT INTO leaderboard_band_context_state (
                    id, seeded_at, legacy_source_rows, overlay_source_rows,
                    context_rows, updated_at)
                VALUES (TRUE, now(), 0, 0, 2, now());
                """;
            command.Parameters.AddWithValue("snapshotMembers", NpgsqlTypes.NpgsqlDbType.Jsonb, snapshotMembers);
            command.Parameters.AddWithValue("legacyMembers", NpgsqlTypes.NpgsqlDbType.Jsonb, legacyMembers);
            command.ExecuteNonQuery();
        }

        _fixture.Db.WriteLegacyLiveLeaderboardSupplementalRows = false;
        _fixture.Db.UpsertEntries("song-snapshot",
        [
            new LeaderboardEntry
            {
                AccountId = "acct-a",
                Score = 120_000,
                Accuracy = 99,
                IsFullCombo = true,
                Stars = 6,
                Season = 3,
                Difficulty = 3,
                Source = "backfill",
            },
        ]);
        _fixture.Db.UpsertEntries("song-same-score",
        [
            new LeaderboardEntry
            {
                AccountId = "acct-a",
                Score = 100_000,
                Accuracy = 99,
                IsFullCombo = true,
                Stars = 6,
                Season = 4,
                Difficulty = 3,
                Source = "backfill",
                EndTime = "2026-02-01T00:00:00Z",
                BandMembers = overlayMemberRows,
                BandScore = 999_000,
                BaseScore = 140_000,
                InstrumentBonus = 999_999,
                OverdriveBonus = 15_000,
                InstrumentCombo = "9:9",
            },
        ]);
        _fixture.Db.UpsertEntries("song-overlay",
        [
            new LeaderboardEntry
            {
                AccountId = "acct-e",
                Score = 130_000,
                Accuracy = 97,
                IsFullCombo = false,
                Stars = 5,
                Season = 3,
                Difficulty = 3,
                Source = "backfill",
                EndTime = "2026-01-03T00:00:00Z",
                BandMembers = overlayMemberRows,
                BandScore = 250_000,
                BaseScore = 190_000,
                InstrumentBonus = 35_000,
                OverdriveBonus = 25_000,
                InstrumentCombo = "1:2",
            },
        ]);

        var pathDataStore = Substitute.For<IPathDataStore>();
        pathDataStore.GetAllMaxScores().Returns(new Dictionary<string, SongMaxScores>(StringComparer.OrdinalIgnoreCase));
        var extractor = new PostScrapeBandExtractor(
            _fixture.DataSource,
            pathDataStore,
            Substitute.For<ILogger<PostScrapeBandExtractor>>(),
            options: Options.Create(new ScraperOptions { BandExtractionParallelism = 1 }),
            featureOptions: Options.Create(new FeatureOptions { UseSnapshotOverlayWorkerReaders = true }));

        var result = await extractor.RunAsync(CancellationToken.None);

        Assert.Equal(3, result.BandRows);
        Assert.Equal(6, result.MemberStats);
        Assert.Equal(6, result.MemberLookups);

        using var readConnection = _fixture.DataSource.OpenConnection();
        using var readCommand = readConnection.CreateCommand();
        readCommand.CommandText = """
            SELECT song_id, team_key, team_members, score, base_score, instrument_bonus,
                   overdrive_bonus, instrument_combo, source, end_time
            FROM band_entries
            ORDER BY song_id
            """;
        using var reader = readCommand.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal("song-overlay", reader.GetString(0));
        Assert.Equal("acct-e:acct-f", reader.GetString(1));
        Assert.Equal(["acct-e", "acct-f"], reader.GetFieldValue<string[]>(2));
        Assert.Equal(250_000, reader.GetInt32(3));
        Assert.Equal(190_000, reader.GetInt32(4));
        Assert.Equal(35_000, reader.GetInt32(5));
        Assert.Equal(25_000, reader.GetInt32(6));
        Assert.Equal("1:2", reader.GetString(7));
        Assert.Equal("solo_extract", reader.GetString(8));

        Assert.True(reader.Read());
        Assert.Equal("song-same-score", reader.GetString(0));
        Assert.Equal("acct-a:acct-b", reader.GetString(1));
        Assert.Equal(180_000, reader.GetInt32(3));
        Assert.Equal(140_000, reader.GetInt32(4));
        Assert.Equal(25_000, reader.GetInt32(5));
        Assert.Equal(15_000, reader.GetInt32(6));
        Assert.Equal("0:1", reader.GetString(7));
        Assert.Equal("solo_extract", reader.GetString(8));

        Assert.True(reader.Read());
        Assert.Equal("song-snapshot", reader.GetString(0));
        Assert.Equal("acct-a:acct-b", reader.GetString(1));
        Assert.Equal(["acct-a", "acct-b"], reader.GetFieldValue<string[]>(2));
        Assert.Equal(200_000, reader.GetInt32(3));
        Assert.Equal(150_000, reader.GetInt32(4));
        Assert.Equal(30_000, reader.GetInt32(5));
        Assert.Equal(20_000, reader.GetInt32(6));
        Assert.Equal("0:1", reader.GetString(7));
        Assert.Equal("solo_extract", reader.GetString(8));
        Assert.True(reader.IsDBNull(9));
        Assert.False(reader.Read());
        reader.Close();

        using var sameScoreCommand = readConnection.CreateCommand();
        sameScoreCommand.CommandText = """
            SELECT accuracy, is_full_combo, stars, difficulty, season, end_time
            FROM band_entries
            WHERE song_id = 'song-same-score'
            """;
        using var sameScoreReader = sameScoreCommand.ExecuteReader();
        Assert.True(sameScoreReader.Read());
        Assert.Equal(90, sameScoreReader.GetInt32(0));
        Assert.False(sameScoreReader.GetBoolean(1));
        Assert.Equal(4, sameScoreReader.GetInt32(2));
        Assert.Equal(2, sameScoreReader.GetInt32(3));
        Assert.Equal(3, sameScoreReader.GetInt32(4));
        Assert.Equal("2026-01-01T00:00:00Z", sameScoreReader.GetString(5));
    }

    [Fact]
    public async Task SnapshotReconciliation_RepairsContextScalarOrderingBeforeExtraction()
    {
        ScrapeRunTestHelper.EnsureAllocated(_fixture.DataSource, 42, completed: true);
        var members = SerializeMembers("acct-a", "acct-b");
        using (var connection = _fixture.DataSource.OpenConnection())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                INSERT INTO leaderboard_band_context (
                    song_id, instrument, account_id, score, accuracy, is_full_combo,
                    stars, season, percentile, source, difficulty, end_time,
                    band_members_json, first_seen_at, last_updated_at)
                VALUES (
                    'song-reconcile', 'Solo_Guitar', 'acct-a', 100000, 90, FALSE,
                    4, 3, 90.0, 'scrape', 2, '2026-01-01T00:00:00Z',
                    @members, now(), now());

                INSERT INTO leaderboard_entries_snapshot (
                    snapshot_id, song_id, instrument, account_id, score, accuracy,
                    is_full_combo, stars, season, percentile, rank, source, difficulty,
                    end_time, first_seen_at, last_updated_at)
                VALUES (
                    42, 'song-reconcile', 'Solo_Guitar', 'acct-a', 200000, 99,
                    TRUE, 6, 4, 99.0, 1, 'scrape', 3,
                    NULL, now(), now());
                """;
            command.Parameters.AddWithValue("members", NpgsqlTypes.NpgsqlDbType.Jsonb, members);
            command.ExecuteNonQuery();
        }

        var pathDataStore = Substitute.For<IPathDataStore>();
        pathDataStore.GetAllMaxScores().Returns(new Dictionary<string, SongMaxScores>(StringComparer.OrdinalIgnoreCase));
        var extractor = new PostScrapeBandExtractor(
            _fixture.DataSource,
            pathDataStore,
            Substitute.For<ILogger<PostScrapeBandExtractor>>(),
            options: Options.Create(new ScraperOptions { BandExtractionParallelism = 1 }),
            featureOptions: Options.Create(new FeatureOptions { UseSnapshotOverlayWorkerReaders = true }));

        var result = await extractor.RunAsync(42, CancellationToken.None);

        Assert.Equal(1, result.BandRows);
        using var readConnection = _fixture.DataSource.OpenConnection();
        using var readCommand = readConnection.CreateCommand();
        readCommand.CommandText = """
            SELECT context.score, context.accuracy, context.is_full_combo,
                   context.stars, context.season, context.difficulty, context.end_time,
                   band.score
            FROM leaderboard_band_context context
            JOIN band_entries band
              ON band.song_id = context.song_id
             AND band.team_key = 'acct-a:acct-b'
            WHERE context.song_id = 'song-reconcile'
              AND context.account_id = 'acct-a'
            """;
        using var reader = readCommand.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal(200_000, reader.GetInt32(0));
        Assert.Equal(99, reader.GetInt32(1));
        Assert.True(reader.GetBoolean(2));
        Assert.Equal(6, reader.GetInt32(3));
        Assert.Equal(4, reader.GetInt32(4));
        Assert.Equal(3, reader.GetInt32(5));
        Assert.True(reader.IsDBNull(6));
        Assert.Equal(200_000, reader.GetInt32(7));
    }

    [Fact]
    public async Task SnapshotReconciliation_PreservesLaterSupplementalUpdates()
    {
        ScrapeRunTestHelper.EnsureAllocated(_fixture.DataSource, 42, completed: true);
        var members = SerializeMembers("acct-a", "acct-b");
        using (var connection = _fixture.DataSource.OpenConnection())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                INSERT INTO leaderboard_band_context (
                    song_id, instrument, account_id, score, accuracy, is_full_combo,
                    stars, season, percentile, source, difficulty, end_time,
                    band_members_json, first_seen_at, last_updated_at)
                VALUES (
                    'song-later-refresh', 'Solo_Guitar', 'acct-a', 100000, 90, FALSE,
                    4, 3, 90.0, 'scrape', 2, '2026-01-01T00:00:00Z',
                    @members, now() - interval '2 minutes', now() - interval '2 minutes');

                INSERT INTO leaderboard_entries_snapshot (
                    snapshot_id, song_id, instrument, account_id, score, accuracy,
                    is_full_combo, stars, season, percentile, rank, source, difficulty,
                    end_time, first_seen_at, last_updated_at)
                VALUES (
                    42, 'song-later-refresh', 'Solo_Guitar', 'acct-a', 200000, 95,
                    FALSE, 5, 3, 95.0, 1, 'scrape', 3,
                    '2026-01-02T00:00:00Z', now() - interval '1 minute', now() - interval '1 minute');

                INSERT INTO leaderboard_band_context_state (
                    id, seeded_at, legacy_source_rows, overlay_source_rows,
                    context_rows, updated_at)
                VALUES (TRUE, now(), 0, 0, 1, now());
                """;
            command.Parameters.AddWithValue("members", NpgsqlTypes.NpgsqlDbType.Jsonb, members);
            command.ExecuteNonQuery();
        }
        _fixture.Db.WriteLegacyLiveLeaderboardSupplementalRows = false;
        _fixture.Db.UpsertEntries("song-later-refresh",
        [
            new LeaderboardEntry
            {
                AccountId = "acct-a",
                Score = 300_000,
                Accuracy = 99,
                IsFullCombo = true,
                Stars = 6,
                Season = 4,
                Percentile = 99.0,
                Difficulty = 3,
                Source = "backfill",
                EndTime = "2026-01-03T00:00:00Z",
            },
        ]);

        var pathDataStore = Substitute.For<IPathDataStore>();
        pathDataStore.GetAllMaxScores().Returns(new Dictionary<string, SongMaxScores>(StringComparer.OrdinalIgnoreCase));
        var extractor = new PostScrapeBandExtractor(
            _fixture.DataSource,
            pathDataStore,
            Substitute.For<ILogger<PostScrapeBandExtractor>>(),
            options: Options.Create(new ScraperOptions { BandExtractionParallelism = 1 }),
            featureOptions: Options.Create(new FeatureOptions { UseSnapshotOverlayWorkerReaders = true }));

        await extractor.RunAsync(42, CancellationToken.None);

        using var readConnection = _fixture.DataSource.OpenConnection();
        using var readCommand = readConnection.CreateCommand();
        readCommand.CommandText = """
            SELECT context.score, context.accuracy, context.end_time, band.score
            FROM leaderboard_band_context context
            JOIN band_entries band
              ON band.song_id = context.song_id
             AND band.team_key = 'acct-a:acct-b'
            WHERE context.song_id = 'song-later-refresh'
              AND context.account_id = 'acct-a'
            """;
        using var reader = readCommand.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal(300_000, reader.GetInt32(0));
        Assert.Equal(99, reader.GetInt32(1));
        Assert.Equal("2026-01-03T00:00:00Z", reader.GetString(2));
        Assert.Equal(300_000, reader.GetInt32(3));
    }

    [Fact]
    public async Task BandContextSeed_IsOneTimeAndFeatureGated()
    {
        var members = SerializeMembers("acct-seed", "acct-mate");
        InsertLegacyBandRow("song-seed", "acct-seed", members);
        var pathDataStore = Substitute.For<IPathDataStore>();
        pathDataStore.GetAllMaxScores().Returns(new Dictionary<string, SongMaxScores>(StringComparer.OrdinalIgnoreCase));
        var disabledExtractor = new PostScrapeBandExtractor(
            _fixture.DataSource,
            pathDataStore,
            Substitute.For<ILogger<PostScrapeBandExtractor>>());
        await disabledExtractor.EnsureBandContextReadyAsync(CancellationToken.None);
        using (var disabledConnection = _fixture.DataSource.OpenConnection())
        using (var disabledCommand = disabledConnection.CreateCommand())
        {
            disabledCommand.CommandText = "SELECT COUNT(*) FROM leaderboard_band_context_state";
            Assert.Equal(0L, (long)disabledCommand.ExecuteScalar()!);
        }
        var extractor = new PostScrapeBandExtractor(
            _fixture.DataSource,
            pathDataStore,
            Substitute.For<ILogger<PostScrapeBandExtractor>>(),
            featureOptions: Options.Create(new FeatureOptions { UseSnapshotOverlayWorkerReaders = true }));

        await extractor.EnsureBandContextReadyAsync(CancellationToken.None);
        InsertLegacyBandRow("song-late", "acct-late", members);
        await extractor.EnsureBandContextReadyAsync(CancellationToken.None);

        using var connection = _fixture.DataSource.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                (SELECT COUNT(*) FROM leaderboard_band_context),
                (SELECT legacy_source_rows FROM leaderboard_band_context_state WHERE id = TRUE),
                (SELECT seeded_at IS NOT NULL FROM leaderboard_band_context_state WHERE id = TRUE)
            """;
        using var reader = command.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal(1L, reader.GetInt64(0));
        Assert.Equal(1L, reader.GetInt64(1));
        Assert.True(reader.GetBoolean(2));
    }

    private void InsertLegacyBandRow(string songId, string accountId, string members)
    {
        using var connection = _fixture.DataSource.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO leaderboard_entries (
                song_id, instrument, account_id, score, accuracy, is_full_combo,
                stars, season, percentile, rank, source, difficulty, end_time,
                band_members_json, band_score, base_score, instrument_bonus,
                overdrive_bonus, instrument_combo, first_seen_at, last_updated_at)
            VALUES (
                @songId, 'Solo_Guitar', @accountId, 100000, 95, FALSE,
                5, 3, 95.0, 1, 'backfill', 3, '2026-01-01T00:00:00Z',
                @members, 150000, 100000, 30000,
                20000, '0:1', now(), now())
            """;
        command.Parameters.AddWithValue("songId", songId);
        command.Parameters.AddWithValue("accountId", accountId);
        command.Parameters.AddWithValue("members", NpgsqlTypes.NpgsqlDbType.Jsonb, members);
        command.ExecuteNonQuery();
    }

    private static string SerializeMembers(string firstAccountId, string secondAccountId) =>
        JsonSerializer.Serialize(CreateMembers(firstAccountId, secondAccountId));

    private static List<BandMemberStats> CreateMembers(string firstAccountId, string secondAccountId) =>
        new()
        {
            new()
            {
                MemberIndex = 0,
                AccountId = firstAccountId,
                InstrumentId = 0,
                Score = 60_000,
                Accuracy = 98,
                IsFullCombo = true,
                Stars = 6,
                Difficulty = 3,
            },
            new()
            {
                MemberIndex = 1,
                AccountId = secondAccountId,
                InstrumentId = 1,
                Score = 40_000,
                Accuracy = 97,
                IsFullCombo = false,
                Stars = 5,
                Difficulty = 3,
            },
        };
}
