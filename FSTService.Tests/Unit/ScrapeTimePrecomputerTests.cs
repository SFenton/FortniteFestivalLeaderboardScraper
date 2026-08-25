using System.Text.Json;
using FortniteFestival.Core;
using FortniteFestival.Core.Services;
using FSTService.Api;
using FSTService.Persistence;
using FSTService.Scraping;
using FSTService.Tests.Helpers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace FSTService.Tests.Unit;

public sealed class ScrapeTimePrecomputerTests : IDisposable
{
    private readonly string _tempDir;
    private readonly InMemoryMetaDatabase _metaFixture = new();
    private readonly MetaDatabase _metaDb;
    private readonly GlobalLeaderboardPersistence _persistence;
    private readonly PathDataStore _pathDataStore;
    private readonly ScrapeTimePrecomputer _sut;

    public ScrapeTimePrecomputerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"precomp_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);

        _metaDb = new MetaDatabase(_metaFixture.DataSource,
            Substitute.For<ILogger<MetaDatabase>>());

        _persistence = new GlobalLeaderboardPersistence(
            _metaDb,
            Substitute.For<ILoggerFactory>(),
            Substitute.For<ILogger<GlobalLeaderboardPersistence>>(),
            _metaFixture.DataSource,
            Options.Create(new FeatureOptions()));
        _persistence.Initialize();

        _pathDataStore = new PathDataStore(SharedPostgresContainer.CreateDatabase());

        _sut = new ScrapeTimePrecomputer(
            _persistence, _metaDb, _pathDataStore,
            new ScrapeProgressTracker(),
            Substitute.For<ILogger<ScrapeTimePrecomputer>>(),
            NullLoggerFactory.Instance,
            new JsonSerializerOptions(JsonSerializerDefaults.Web),
            new FeatureOptions());
    }

    public void Dispose()
    {
        _persistence.Dispose();
        _metaDb.Dispose();
        _metaFixture.Dispose();
        try { Directory.Delete(_tempDir, true); } catch { }
    }

    private void SeedSong(string songId, string instrument, int maxScore, params (string AccountId, int Score)[] entries)
    {
        // Ensure core song row exists for PathDataStore
        EnsureSongRow(songId);
        _pathDataStore.UpdateMaxScores(songId, CreateMaxScores(instrument, maxScore), "hash");

        var db = _persistence.GetOrCreateInstrumentDb(instrument);
        var list = entries.Select(e => new LeaderboardEntry
        {
            AccountId = e.AccountId, Score = e.Score,
            Accuracy = 95, Stars = 5, Season = 3,
        }).ToList();
        db.UpsertEntries(songId, list);
        db.RecomputeAllRanks();
    }

    private void RegisterUser(string accountId)
    {
        _metaDb.InsertAccountIds(new[] { accountId });
        _metaDb.InsertAccountNames(new[] { (accountId, (string?)"TestUser") });
        _metaDb.RegisterUser("web-tracker", accountId);
    }

    private void SeedBandSong(
        string songId,
        string bandType,
        params (string[] Members, string InstrumentCombo, int Score)[] entries)
    {
        var persistence = new BandLeaderboardPersistence(
            _metaFixture.DataSource,
            Substitute.For<ILogger<BandLeaderboardPersistence>>());

        persistence.UpsertBandEntries(songId, bandType, entries.Select(entry =>
        {
            var sortedMembers = entry.Members.OrderBy(static member => member, StringComparer.OrdinalIgnoreCase).ToArray();
            var instrumentIds = entry.InstrumentCombo.Split(':', StringSplitOptions.RemoveEmptyEntries)
                .Select(int.Parse)
                .ToArray();
            return new BandLeaderboardEntry
            {
                TeamKey = string.Join(':', sortedMembers),
                TeamMembers = sortedMembers,
                InstrumentCombo = entry.InstrumentCombo,
                Score = entry.Score,
                Accuracy = 950_000,
                IsFullCombo = true,
                Stars = 5,
                Difficulty = 3,
                Season = 1,
                Rank = 1,
                Percentile = 0.1,
                Source = "test",
                MemberStats = sortedMembers.Select((member, index) => new BandMemberStats
                {
                    MemberIndex = index,
                    AccountId = member,
                    InstrumentId = instrumentIds[index],
                    Score = entry.Score / sortedMembers.Length,
                    Accuracy = 950_000,
                    IsFullCombo = true,
                    Stars = 5,
                    Difficulty = 3,
                }).ToList(),
            };
        }).ToList());
    }

    private void PublishBandCurrentProjection(string songId, string bandType, string rankingScope = "overall", string scopeComboId = "")
    {
        var builder = new BandCurrentProjectionBuilder(
            _metaFixture.DataSource,
            Substitute.For<ILogger<BandCurrentProjectionBuilder>>());

        builder.RebuildScopeAsync(
                new BandCurrentProjectionScopeKey(songId, bandType, rankingScope, scopeComboId),
                new BandCurrentProjectionRebuildOptions())
            .GetAwaiter()
            .GetResult();
    }

    private void InsertScoreHistory(string accountId, string songId, string instrument, int score)
    {
        _metaDb.InsertScoreChange(songId, instrument, accountId,
            null, score, null, 1, accuracy: 90, isFullCombo: false, stars: 4);
    }

    private void InsertSnapshotEntry(long snapshotId, string songId, string instrument, string accountId, int score)
    {
        using var conn = _metaFixture.DataSource.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO leaderboard_entries_snapshot
            (snapshot_id, song_id, instrument, account_id, score, accuracy, is_full_combo, stars,
             season, percentile, rank, source, difficulty, api_rank, end_time, first_seen_at, last_updated_at)
            VALUES
            (@snapshotId, @songId, @instrument, @accountId, @score, 95, false, 5,
             3, 99.0, 1, 'scrape', 3, 1, '2025-01-15T12:00:00Z', @now, @now)
            """;
        cmd.Parameters.AddWithValue("snapshotId", snapshotId);
        cmd.Parameters.AddWithValue("songId", songId);
        cmd.Parameters.AddWithValue("instrument", instrument);
        cmd.Parameters.AddWithValue("accountId", accountId);
        cmd.Parameters.AddWithValue("score", score);
        cmd.Parameters.AddWithValue("now", DateTime.UtcNow);
        cmd.ExecuteNonQuery();
    }

    private void InsertSnapshotState(string songId, string instrument, long snapshotId)
    {
        using var conn = _metaFixture.DataSource.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO leaderboard_snapshot_state
            (song_id, instrument, active_snapshot_id, scrape_id, is_finalized, wave1_finalized_at, updated_at)
            VALUES (@songId, @instrument, @snapshotId, @snapshotId, TRUE, @now, @now)
            ON CONFLICT (song_id, instrument) DO UPDATE SET
                active_snapshot_id = EXCLUDED.active_snapshot_id,
                scrape_id = EXCLUDED.scrape_id,
                is_finalized = EXCLUDED.is_finalized,
                wave1_finalized_at = EXCLUDED.wave1_finalized_at,
                updated_at = EXCLUDED.updated_at
            """;
        cmd.Parameters.AddWithValue("songId", songId);
        cmd.Parameters.AddWithValue("instrument", instrument);
        cmd.Parameters.AddWithValue("snapshotId", snapshotId);
        cmd.Parameters.AddWithValue("now", DateTime.UtcNow);
        cmd.ExecuteNonQuery();
    }

    // ── Tests ────────────────────────────────────────────────────

    [Fact]
    public async Task PrecomputeAllAsync_EmptyDb_DoesNotThrow()
    {
        await _sut.PrecomputeAllAsync(CancellationToken.None);
    }

    [Fact]
    public async Task PrecomputeAllAsync_PublishImmediatelyRejectsActiveWorkingPublication()
    {
        _metaDb.StartScrapeRun();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _sut.PrecomputeAllAsync(CancellationToken.None));

        Assert.Contains(
            "cannot publish while a working publication generation exists",
            exception.Message);
        // Static data (firstseen) is always precomputed, even on empty DB
        Assert.True(_sut.Count >= 0);
    }

    [Fact]
    public async Task Candidate_precompute_uses_the_supplied_publication_catalog()
    {
        var persistence = new FestivalPersistence(
            _metaFixture.DataSource);
        var publishedSong = CreateCatalogSong(
            "published-song",
            "Published Song");
        var token = await persistence.SaveSongsVersionedAsync(
            [publishedSong]);
        var scrapeId = _metaDb.StartScrapeRun(token);
        var publicationId = _metaDb
            .GetPublicationGenerationForScrape(scrapeId)!
            .PublicationId;
        await persistence.SaveSongsVersionedAsync(
        [
            CreateCatalogSong(
                "live-song",
                "Live Song"),
        ]);
        var pathStore = new PathDataStore(
            _metaFixture.DataSource);
        var precomputer = new ScrapeTimePrecomputer(
            _persistence,
            _metaDb,
            pathStore,
            new ScrapeProgressTracker(),
            Substitute.For<ILogger<ScrapeTimePrecomputer>>(),
            NullLoggerFactory.Instance,
            new JsonSerializerOptions(
                JsonSerializerDefaults.Web),
            new FeatureOptions());

        await precomputer.PrecomputeAllAsync(
            showLeaderboardEntryTotals: false,
            CancellationToken.None,
            publishImmediately: false,
            publicationCatalogSongs: [publishedSong]);

        using var connection =
            _metaFixture.DataSource.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT convert_from(json_data, 'UTF8')
            FROM publication_api_response_cache_staging
            WHERE publication_id = @publicationId
              AND cache_key = @cacheKey
            """;
        command.Parameters.AddWithValue(
            "publicationId",
            publicationId);
        command.Parameters.AddWithValue(
            "cacheKey",
            PublicationApiCacheKeys.Songs);
        var json = Assert.IsType<string>(
            command.ExecuteScalar());
        using var document = JsonDocument.Parse(json);
        var songs = document.RootElement
            .GetProperty("songs");
        Assert.Equal(1, songs.GetArrayLength());
        Assert.Equal(
            "published-song",
            songs[0].GetProperty("songId").GetString());
    }

    [Fact]
    public async Task Candidate_precompute_rejects_an_empty_catalog()
    {
        var failure = await Assert.ThrowsAsync<
            InvalidOperationException>(
            () => _sut.PrecomputeAllAsync(
                showLeaderboardEntryTotals: false,
                CancellationToken.None,
                publishImmediately: false,
                publicationCatalogSongs: []));

        Assert.Contains(
            "empty publication catalog",
            failure.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task PrecomputeAllAsync_RegisteredUser_ProducesPlayerEntry()
    {
        RegisterUser("user1");
        SeedSong("s1", "Solo_Guitar", 100000,
            ("user1", 95000), ("p2", 90000), ("p3", 85000));

        await _sut.PrecomputeAllAsync(CancellationToken.None);

        var result = _sut.TryGet("player:user1:::");
        Assert.NotNull(result);

        var json = JsonDocument.Parse(result.Value.Json);
        Assert.Equal("user1", json.RootElement.GetProperty("accountId").GetString());
        Assert.Equal("TestUser", json.RootElement.GetProperty("displayName").GetString());
        var scores = json.RootElement.GetProperty("scores");
        Assert.Equal(1, scores.GetArrayLength());

        var score = scores[0];
        Assert.Equal("s1", score.GetProperty("si").GetString());
        Assert.Equal(95000, score.GetProperty("sc").GetInt32());
        Assert.True(score.GetProperty("rk").GetInt32() > 0);
    }

    [Fact]
    public void PrecomputePlayerLeaderboardRivals_UsesPersistedRowsWithoutLiveFallback()
    {
        _metaDb.ReplaceLeaderboardRivalsData(
            "user1",
            "Solo_Guitar",
            [
                new LeaderboardRivalRow
                {
                    UserId = "user1",
                    RivalAccountId = "rival1",
                    Instrument = "Solo_Guitar",
                    RankMethod = "totalscore",
                    Direction = "above",
                    UserRank = 10,
                    RivalRank = 9,
                    SharedSongCount = 3,
                    AheadCount = 1,
                    BehindCount = 2,
                    AvgSignedDelta = 1.5,
                    ComputedAt = DateTime.UtcNow.ToString("o"),
                },
                new LeaderboardRivalRow
                {
                    UserId = "user1",
                    RivalAccountId = "rival2",
                    Instrument = "Solo_Guitar",
                    RankMethod = "adjusted",
                    Direction = "below",
                    UserRank = 12,
                    RivalRank = 13,
                    SharedSongCount = 4,
                    AheadCount = 2,
                    BehindCount = 2,
                    AvgSignedDelta = -0.5,
                    ComputedAt = DateTime.UtcNow.ToString("o"),
                },
            ],
            []);

        var entries = new List<(string Key, byte[] Json, string ETag)>();
        _sut.PrecomputePlayerLeaderboardRivals(
            "user1",
            ["Solo_Guitar"],
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            allowLiveFallback: false,
            storeOverride: entries);

        Assert.Equal(2, entries.Count);
        var totalScore = Assert.Single(entries, entry =>
            entry.Key == "lb-rivals:user1:Solo_Guitar:totalscore");
        var totalScorePayload = JsonDocument.Parse(totalScore.Json).RootElement;
        Assert.Equal(10, totalScorePayload.GetProperty("userRank").GetInt32());
        Assert.Single(totalScorePayload.GetProperty("above").EnumerateArray());
        Assert.Empty(totalScorePayload.GetProperty("below").EnumerateArray());

        var adjusted = Assert.Single(entries, entry =>
            entry.Key == "lb-rivals:user1:Solo_Guitar:adjusted");
        var adjustedPayload = JsonDocument.Parse(adjusted.Json).RootElement;
        Assert.Equal(12, adjustedPayload.GetProperty("userRank").GetInt32());
        Assert.Empty(adjustedPayload.GetProperty("above").EnumerateArray());
        Assert.Single(adjustedPayload.GetProperty("below").EnumerateArray());
    }

    [Fact]
    public void PrecomputePlayerLeaderboardRivals_SkipsMissingPersistedRowsWithoutLiveFallback()
    {
        var entries = new List<(string Key, byte[] Json, string ETag)>();

        _sut.PrecomputePlayerLeaderboardRivals(
            "user1",
            ["Solo_Guitar"],
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            allowLiveFallback: false,
            storeOverride: entries);

        Assert.Empty(entries);
    }

    [Fact]
    public void PrecomputePlayerLeaderboardRivals_EmitsCompletedEmptyMethods()
    {
        _metaDb.ReplaceLeaderboardRivalsData(
            "user1",
            "Solo_Guitar",
            [],
            [],
            LeaderboardRivalsCalculator.RankMethods,
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
            {
                ["adjusted"] = 12,
            });
        var entries = new List<(string Key, byte[] Json, string ETag)>();

        _sut.PrecomputePlayerLeaderboardRivals(
            "user1",
            ["Solo_Guitar"],
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            allowLiveFallback: false,
            storeOverride: entries);

        Assert.Equal(LeaderboardRivalsCalculator.RankMethods.Length, entries.Count);
        var adjusted = Assert.Single(entries, entry =>
            entry.Key == "lb-rivals:user1:Solo_Guitar:adjusted");
        var payload = JsonDocument.Parse(adjusted.Json).RootElement;
        Assert.Equal(12, payload.GetProperty("userRank").GetInt32());
        Assert.Empty(payload.GetProperty("above").EnumerateArray());
        Assert.Empty(payload.GetProperty("below").EnumerateArray());
    }

    [Fact]
    public async Task PrecomputeAllAsync_PlayerWithInvalidScore_HasMinLeeway()
    {
        RegisterUser("user1");
        // maxScore=100000, user score=105000 → minLeeway = 5.0
        SeedSong("s1", "Solo_Guitar", 100000,
            ("user1", 105000), ("p2", 90000));

        await _sut.PrecomputeAllAsync(CancellationToken.None);

        var result = _sut.TryGet("player:user1:::");
        Assert.NotNull(result);

        var json = JsonDocument.Parse(result.Value.Json);
        var score = json.RootElement.GetProperty("scores")[0];
        Assert.Equal(5.0, score.GetProperty("ml").GetDouble());
    }

    [Fact]
    public async Task PrecomputeAllAsync_PlayerWithFallbackScores_HasValidScores()
    {
        RegisterUser("user1");
        SeedSong("s1", "Solo_Guitar", 100000,
            ("user1", 106000), ("p2", 90000), ("p3", 99000));

        // Insert a historical valid score
        InsertScoreHistory("user1", "s1", "Solo_Guitar", 99000);

        await _sut.PrecomputeAllAsync(CancellationToken.None);

        var result = _sut.TryGet("player:user1:::");
        Assert.NotNull(result);

        var json = JsonDocument.Parse(result.Value.Json);
        var score = json.RootElement.GetProperty("scores")[0];

        // Should have validScores array with the 99000 fallback
        Assert.True(score.TryGetProperty("vs", out var validScores));
        Assert.True(validScores.GetArrayLength() > 0);

        var fallback = validScores[0];
        Assert.Equal(99000, fallback.GetProperty("sc").GetInt32());
        Assert.True(fallback.GetProperty("ml").GetDouble() <= 0);
    }

    [Fact]
    public async Task PrecomputeAllAsync_ProducesLeaderboardAllEntries()
    {
        SeedSong("s1", "Solo_Guitar", 100000,
            ("p1", 95000), ("p2", 90000), ("p3", 85000));

        await _sut.PrecomputeAllAsync(CancellationToken.None);

        // Should have leaderboard-all entries for the song
        var noLeeway = _sut.TryGet("lb:s1:10:");
        Assert.NotNull(noLeeway);

        var json = JsonDocument.Parse(noLeeway.Value.Json);
        Assert.Equal("s1", json.RootElement.GetProperty("songId").GetString());

        var lb1 = _sut.TryGet(global::FSTService.LeaderboardCacheKeys.LeaderboardAll("s1", 10, 1));
        Assert.NotNull(lb1);

        var offsets = _sut.TryGet(global::FSTService.LeaderboardCacheKeys.LeaderboardRankOffsets("s1", "Solo_Guitar"));
        Assert.NotNull(offsets);
        var offsetsJson = JsonDocument.Parse(offsets.Value.Json);
        Assert.Equal("s1", offsetsJson.RootElement.GetProperty("songId").GetString());
        Assert.Equal("Solo_Guitar", offsetsJson.RootElement.GetProperty("instrument").GetString());
        Assert.Equal(101, offsetsJson.RootElement.GetProperty("removed").GetArrayLength());
    }

    [Fact]
    public async Task MaxScoreMaintenance_rankings_and_precompute_share_fenced_population()
    {
        const string songId = "population-fence-song";
        const string instrument = "Solo_Guitar";
        RegisterUser("population-fence-user");
        SeedSong(
            songId,
            instrument,
            100_000,
            ("population-fence-user", 95_000),
            ("population-fence-peer", 90_000));
        _metaDb.UpsertLeaderboardPopulation(
            [(songId, instrument, 100L)]);
        var scrapeId = _metaDb.StartScrapeRun();
        _metaDb.CompleteScrapeRun(
            scrapeId,
            1,
            2,
            1,
            1);
        _metaDb.PublishScrapeRun(
            scrapeId,
            promoteCachedResponses: false);
        using (var published = _metaFixture.DataSource
                   .OpenConnection())
        using (var seedPublished =
               published.CreateCommand())
        {
            seedPublished.CommandText = """
                INSERT INTO leaderboard_entries_snapshot (
                    snapshot_id,
                    song_id,
                    instrument,
                    account_id,
                    score,
                    accuracy,
                    stars,
                    season,
                    rank,
                    source,
                    first_seen_at,
                    last_updated_at)
                SELECT @scrapeId,
                       song_id,
                       instrument,
                       account_id,
                       score,
                       accuracy,
                       stars,
                       season,
                       rank,
                       source,
                       first_seen_at,
                       last_updated_at
                FROM leaderboard_entries
                WHERE song_id = @songId
                  AND instrument = @instrument;

                INSERT INTO leaderboard_published_scope_source (
                    published_scrape_id,
                    song_id,
                    instrument,
                    scope_kind,
                    source_kind,
                    source_snapshot_id,
                    source_scrape_id,
                    row_count,
                    content_fingerprint,
                    coverage_fingerprint,
                    reported_total_entries,
                    reported_total_pages,
                    is_complete,
                    created_at,
                    validated_at)
                VALUES (
                    @scrapeId,
                    @songId,
                    @instrument,
                    'alltime',
                    'snapshot',
                    @scrapeId,
                    @scrapeId,
                    2,
                    md5('population-cache-source'),
                    md5('population-cache-coverage'),
                    100,
                    1,
                    TRUE,
                    now(),
                    now());
                """;
            seedPublished.Parameters.AddWithValue(
                "scrapeId",
                scrapeId);
            seedPublished.Parameters.AddWithValue(
                "songId",
                songId);
            seedPublished.Parameters.AddWithValue(
                "instrument",
                instrument);
            seedPublished.ExecuteNonQuery();
        }
        _metaDb.UpsertLeaderboardPopulation(
            [(songId, instrument, 200L)]);
        var publicationId = _metaDb
            .GetPublicationPointerState()
            .CurrentPublicationId!.Value;
        var festivalService =
            FestivalService.CreateFromSongCatalogSnapshot(
            [
                new Song
                {
                    track = new Track
                    {
                        su = songId,
                        tt = "Population Fence Song",
                        an = "Test Artist",
                        @in = new In { gr = 3 },
                    },
                },
            ]);
        var rankings = new RankingsCalculator(
            _persistence,
            _metaDb,
            _pathDataStore,
            new ScrapeProgressTracker(),
            Substitute.For<ILogger<RankingsCalculator>>());

        await using var maintenanceLease =
            await _metaDb
                .AcquireMaxScoreMaintenanceLeaseAsync(
                    publicationId);
        var blockedPopulation =
            Assert.Throws<Npgsql.PostgresException>(() =>
                _metaDb.RaiseLeaderboardPopulationFloor(
                    songId,
                    instrument,
                    300));
        Assert.Equal("55000", blockedPopulation.SqlState);

        using var publishedReadPass =
            _persistence
                .BeginMaxScoreMaintenancePublishedReadPass();
        var publicationPopulation =
            _persistence.GetCurrentStateLeaderboardPopulation();
        await rankings.ComputeForMaxScoreMaintenanceAsync(
            festivalService,
            [instrument],
            publicationPopulation,
            maintenanceLease,
            CancellationToken.None);
        var stagedCount =
            await _sut
                .StageCurrentPublicationCachesForMaintenanceAsync(
                    publicationId,
                    festivalService.Songs,
                    _pathDataStore.GetAllMaxScores(),
                    publicationPopulation,
                    maintenanceLease,
                    CancellationToken.None);
        Assert.True(stagedCount > 0);

        using var connection =
            _metaFixture.DataSource.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                (
                    SELECT entry_count
                    FROM song_stats
                    WHERE song_id = @songId
                      AND instrument = @instrument
                ),
                (
                    SELECT json_data
                    FROM publication_api_response_cache_staging
                    WHERE publication_id = @publicationId
                      AND cache_key = @cacheKey
                ),
                (
                    SELECT json_data
                    FROM publication_api_response_cache_staging
                    WHERE publication_id = @publicationId
                      AND cache_key = @songsCacheKey
                ),
                (
                    SELECT json_data
                    FROM publication_api_response_cache_staging
                    WHERE publication_id = @publicationId
                      AND cache_key = @instrumentCacheKey
                )
            """;
        command.Parameters.AddWithValue("songId", songId);
        command.Parameters.AddWithValue(
            "instrument",
            instrument);
        command.Parameters.AddWithValue(
            "publicationId",
            publicationId);
        command.Parameters.AddWithValue(
            "cacheKey",
            $"lb:{songId}:10:");
        command.Parameters.AddWithValue(
            "songsCacheKey",
            PublicationApiCacheKeys.Songs);
        command.Parameters.AddWithValue(
            "instrumentCacheKey",
            PublicationApiCacheKeys.InstrumentLeaderboard(
                songId,
                instrument,
                10,
                leeway: null));
        using var reader = command.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal(100, reader.GetInt32(0));
        var json =
            JsonDocument.Parse(reader.GetFieldValue<byte[]>(1));
        var guitar = json.RootElement
            .GetProperty("instruments")
            .EnumerateArray()
            .Single(entry =>
                entry.GetProperty("instrument").GetString()
                == instrument);
        Assert.Equal(
            100,
            guitar.GetProperty("totalEntries").GetInt32());
        var songsJson =
            JsonDocument.Parse(reader.GetFieldValue<byte[]>(2));
        Assert.Equal(
            songId,
            songsJson.RootElement
                .GetProperty("songs")[0]
                .GetProperty("songId")
                .GetString());
        var instrumentJson =
            JsonDocument.Parse(reader.GetFieldValue<byte[]>(3));
        Assert.Equal(
            instrument,
            instrumentJson.RootElement
                .GetProperty("instrument")
                .GetString());
        Assert.Equal(
            100,
            instrumentJson.RootElement
                .GetProperty("totalEntries")
                .GetInt32());
        Assert.Equal(
            200,
            _metaDb.GetLeaderboardPopulation(
                songId,
                instrument));
    }

    [Fact]
    public async Task PrecomputeAllAsync_UnfilteredLeaderboardAllUsesComputedRank()
    {
        SeedSong("s1", "Solo_Guitar", 100000,
            ("computed-first", 95000), ("computed-second", 90000));
        var db = _persistence.GetOrCreateInstrumentDb("Solo_Guitar");
        db.UpsertEntries("s1",
        [
            new LeaderboardEntry { AccountId = "computed-first", Score = 95000, ApiRank = 2 },
            new LeaderboardEntry { AccountId = "computed-second", Score = 90000, ApiRank = 1 },
        ]);
        db.RecomputeAllRanks();

        await _sut.PrecomputeAllAsync(CancellationToken.None);

        var result = _sut.TryGet("lb:s1:10:");
        Assert.NotNull(result);
        var json = JsonDocument.Parse(result.Value.Json);
        var instrument = json.RootElement.GetProperty("instruments")
            .EnumerateArray()
            .Single(x => x.GetProperty("instrument").GetString() == "Solo_Guitar");
        var entries = instrument.GetProperty("entries");

        Assert.Equal("computed-first", entries[0].GetProperty("accountId").GetString());
        Assert.Equal(1, entries[0].GetProperty("rank").GetInt32());
        Assert.Equal(2, entries[0].GetProperty("apiRank").GetInt32());
        Assert.Equal("computed", entries[0].GetProperty("rankSource").GetString());
        Assert.Equal("computed-second", entries[1].GetProperty("accountId").GetString());
        Assert.Equal(2, entries[1].GetProperty("rank").GetInt32());
        Assert.Equal(1, entries[1].GetProperty("apiRank").GetInt32());
        Assert.Equal("computed", entries[1].GetProperty("rankSource").GetString());
    }

    [Fact]
    public async Task PrecomputeAllAsync_ProducesSongBandLeaderboardAllEntries()
    {
        SeedBandSong("band-song-1", "Band_Duets",
            (["band-p1", "band-p2"], "0:1", 1_200),
            (["band-p3", "band-p4"], "2:3", 1_100));
        PublishBandCurrentProjection("band-song-1", "Band_Duets");

        await _sut.PrecomputeAllAsync(CancellationToken.None);

        var result = _sut.TryGet(global::FSTService.LeaderboardCacheKeys.SongBandLeaderboardsAll("band-song-1", 10));
        Assert.NotNull(result);

        var json = JsonDocument.Parse(result.Value.Json);
        Assert.Equal("band-song-1", json.RootElement.GetProperty("songId").GetString());

        var duos = json.RootElement.GetProperty("bands")
            .EnumerateArray()
            .Single(band => band.GetProperty("bandType").GetString() == "Band_Duets");
        Assert.Equal(2, duos.GetProperty("count").GetInt32());
        Assert.Equal("band-p1:band-p2", duos.GetProperty("entries")[0].GetProperty("teamKey").GetString());
        Assert.Equal(1_200, duos.GetProperty("entries")[0].GetProperty("score").GetInt32());
        Assert.Equal(JsonValueKind.Null, duos.GetProperty("selectedPlayerEntry").ValueKind);
        Assert.Equal(JsonValueKind.Null, duos.GetProperty("selectedBandEntry").ValueKind);
    }

    [Fact]
    public async Task PrecomputeAllAsync_LeaderboardAll_prefers_current_state_snapshot_rows()
    {
        SeedSong("s1", "Solo_Guitar", 100000,
            ("live_only", 80000));
        InsertSnapshotEntry(77, "s1", "Solo_Guitar", "snap_top", 99000);
        InsertSnapshotState("s1", "Solo_Guitar", 77);

        await _sut.PrecomputeAllAsync(CancellationToken.None);

        var result = _sut.TryGet("lb:s1:10:");
        Assert.NotNull(result);

        var json = JsonDocument.Parse(result.Value.Json);
        var instrument = json.RootElement.GetProperty("instruments")
            .EnumerateArray()
            .Single(x => x.GetProperty("instrument").GetString() == "Solo_Guitar");
        var topEntry = instrument.GetProperty("entries")[0];

        Assert.Equal("snap_top", topEntry.GetProperty("accountId").GetString());
        Assert.Equal(99000, topEntry.GetProperty("score").GetInt32());
    }

    [Fact]
    public async Task PrecomputeAllAsync_ProducesPopulationTiers()
    {
        SeedSong("s1", "Solo_Guitar", 100000,
            ("p1", 105000), ("p2", 101000), ("p3", 99000), ("p4", 90000));

        await _sut.PrecomputeAllAsync(CancellationToken.None);

        var tiers = _sut.GetPopulationTiers();
        Assert.NotNull(tiers);
        Assert.True(tiers.ContainsKey(("s1", "Solo_Guitar")));

        var tierData = tiers[("s1", "Solo_Guitar")];
        // baseCount = entries with score <= 95000 → p4 (90000) = 1
        Assert.Equal(1, tierData.BaseCount);
        // tiers should have changepoints for the scores in the band
        Assert.True(tierData.Tiers.Count > 0);
    }

    [Fact]
    public async Task PrecomputeAllAsync_PopulationTiers_UseCurrentStateThresholdBandRows()
    {
        SeedSong("s1", "Solo_Guitar", 100000,
            ("live_low", 90000), ("live_band", 97000));
        InsertSnapshotEntry(77, "s1", "Solo_Guitar", "snap_band", 99000);
        InsertSnapshotState("s1", "Solo_Guitar", 77);

        await _sut.PrecomputeAllAsync(CancellationToken.None);

        var tiers = _sut.GetPopulationTiers();
        Assert.NotNull(tiers);

        var tierData = tiers![("s1", "Solo_Guitar")];
        Assert.Equal(0, tierData.BaseCount);
        Assert.Single(tierData.Tiers);
        Assert.Equal(-1.0, tierData.Tiers[0].Leeway);
        Assert.Equal(1, tierData.Tiers[0].Total);
    }

    [Fact]
    public void InvalidateAll_ClearsEverything()
    {
        // Manually store something
        _sut.PrecomputeUser("nonexistent"); // no-op but exercises the method
        _sut.InvalidateAll();
        Assert.Equal(0, _sut.Count);
        Assert.Null(_sut.GetPopulationTiers());
    }

    [Fact]
    public void TryGet_NonExistentKey_ReturnsNull()
    {
        Assert.Null(_sut.TryGet("player:unknown:::"));
    }

    [Fact]
    public async Task PrecomputeAllAsync_MultipleUsers_PrecomputesAll()
    {
        RegisterUser("user1");
        RegisterUser("user2");
        SeedSong("s1", "Solo_Guitar", 100000,
            ("user1", 95000), ("user2", 88000), ("p3", 80000));

        await _sut.PrecomputeAllAsync(CancellationToken.None);

        Assert.NotNull(_sut.TryGet("player:user1:::"));
        Assert.NotNull(_sut.TryGet("player:user2:::"));
    }

    [Fact]
    public async Task PrecomputeAllAsync_EvictsUnregisteredPlayerEntries()
    {
        RegisterUser("user1");
        RegisterUser("user2");
        SeedSong("s1", "Solo_Guitar", 100000,
            ("user1", 95000), ("user2", 88000));

        await _sut.PrecomputeAllAsync(CancellationToken.None);
        Assert.NotNull(_sut.TryGet("player:user1:::"));
        Assert.NotNull(_sut.TryGet("player:user2:::"));

        // Unregister user2 between scrapes
        _metaDb.UnregisterUser("web-tracker", "user2");

        // Re-precompute — user2's entry should be evicted
        await _sut.PrecomputeAllAsync(CancellationToken.None);
        Assert.NotNull(_sut.TryGet("player:user1:::"));
        Assert.Null(_sut.TryGet("player:user2:::"));
    }

    [Fact]
    public async Task PrecomputeAllAsync_RankTiers_IncludesChangepoints()
    {
        RegisterUser("user1");
        SeedSong("s1", "Solo_Guitar", 100000,
            ("user1", 106000),
            ("p2", 103000),  // leeway = 3.0
            ("p3", 101000),  // leeway = 1.0
            ("p4", 99000),   // leeway = -1.0
            ("p5", 90000));

        // user1 has a valid fallback in history
        InsertScoreHistory("user1", "s1", "Solo_Guitar", 98000);

        await _sut.PrecomputeAllAsync(CancellationToken.None);

        var result = _sut.TryGet("player:user1:::");
        Assert.NotNull(result);

        var json = JsonDocument.Parse(result.Value.Json);
        var validScores = json.RootElement.GetProperty("scores")[0].GetProperty("vs");
        var fallback = validScores[0];

        // Should have rankTiers
        Assert.True(fallback.TryGetProperty("rt", out var rankTiers));
        Assert.True(rankTiers.GetArrayLength() > 0);

        // Each tier should have leeway and rank
        var firstTier = rankTiers[0];
        Assert.True(firstTier.TryGetProperty("l", out _));
        Assert.True(firstTier.TryGetProperty("r", out _));
    }

    [Fact]
    public async Task PrecomputeAllAsync_RankTiers_UseCurrentStateThresholdBandRows()
    {
        RegisterUser("user1");
        SeedSong("s1", "Solo_Guitar", 100000,
            ("user1", 106000),
            ("live_only_above", 103000),
            ("live_only_below", 90000));
        InsertSnapshotEntry(77, "s1", "Solo_Guitar", "user1", 106000);
        InsertSnapshotEntry(77, "s1", "Solo_Guitar", "snap_band", 101000);
        InsertSnapshotState("s1", "Solo_Guitar", 77);
        InsertScoreHistory("user1", "s1", "Solo_Guitar", 98000);

        await _sut.PrecomputeAllAsync(CancellationToken.None);

        var result = _sut.TryGet("player:user1:::");
        Assert.NotNull(result);

        var json = JsonDocument.Parse(result.Value.Json);
        var rankTiers = json.RootElement
            .GetProperty("scores")[0]
            .GetProperty("vs")[0]
            .GetProperty("rt");

        Assert.Equal(-5.0, rankTiers[0].GetProperty("l").GetDouble());
        Assert.Equal(1, rankTiers[0].GetProperty("r").GetInt32());
        Assert.Equal(1.0, rankTiers[1].GetProperty("l").GetDouble());
        Assert.Equal(2, rankTiers[1].GetProperty("r").GetInt32());
    }

    [Fact]
    public void ComputeRankTiers_UsesPrecomputedScoresAndStoredRankWithoutDatabase()
    {
        var tiers = ScrapeTimePrecomputer.ComputeRankTiers(
            fallbackScore: 94_000,
            maxScore: 100_000,
            bandScores: [99_000, 101_000, 103_000],
            storedRank: 5,
            populationTierData: new PopulationTierData { BaseCount = 10, Tiers = [] });

        Assert.NotNull(tiers);
        Assert.Equal(-5.0, tiers![0].Leeway);
        Assert.Equal(5, tiers[0].Rank);
        Assert.Equal(-1.0, tiers[1].Leeway);
        Assert.Equal(6, tiers[1].Rank);
        Assert.Equal(1.0, tiers[2].Leeway);
        Assert.Equal(7, tiers[2].Rank);
        Assert.Equal(3.0, tiers[3].Leeway);
        Assert.Equal(8, tiers[3].Rank);
    }

    [Fact]
    public async Task PrecomputeAll_FlushesToPostgreSQLAndClearsRAM()
    {
        RegisterUser("user1");
        SeedSong("s1", "Solo_Guitar", 100000,
            ("user1", 95000), ("p2", 90000));

        await _sut.PrecomputeAllAsync(CancellationToken.None);

        // _store should be cleared after flush
        Assert.Equal(0, _sut.Count);

        // But responses should be available via TryGet (reads from PostgreSQL cache)
        var response = _sut.TryGet("player:user1:::");
        Assert.NotNull(response);

        // Verify population tiers survived (stored separately, not in PostgreSQL)
        Assert.NotNull(_sut.GetPopulationTiers());
    }

    [Fact]
    public async Task PrecomputeAll_ClearsStalePostgreSQLDataBeforeFlush()
    {
        RegisterUser("user1");
        SeedSong("s1", "Solo_Guitar", 100000,
            ("user1", 95000));

        // First precomputation
        await _sut.PrecomputeAllAsync(CancellationToken.None);

        // Verify data is in cache
        Assert.NotNull(_sut.TryGet("player:user1:::"));

        // Second precomputation should TRUNCATE and re-insert
        await _sut.PrecomputeAllAsync(CancellationToken.None);
        Assert.NotNull(_sut.TryGet("player:user1:::"));
    }

    [Fact]
    public void GetCachedResponse_MissingKey_ReturnsNull()
    {
        // ClearCachedResponses should not throw even with empty table
        _metaDb.ClearCachedResponses();
        var response = _sut.TryGet("nonexistent:key");
        Assert.Null(response);
    }

    [Fact]
    public void InvalidateAll_PreservesPublishedPostgreSQLResponses()
    {
        const string cacheKey = "leaderboard:all:song-1:10:";
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(new { songId = "song-1" });
        const string etag = "\"published\"";

        _metaDb.BulkSetCachedResponses([(cacheKey, json, etag)]);

        _sut.InvalidateAll();

        var response = _sut.TryGet(cacheKey);
        Assert.NotNull(response);
        Assert.Equal(etag, response.Value.ETag);
        Assert.Equal(json, response.Value.Json);
    }

    // ── Helpers ──────────────────────────────────────────────────

    private static SongMaxScores CreateMaxScores(string instrument, int maxScore)
    {
        var ms = new SongMaxScores();
        switch (instrument)
        {
            case "Solo_Guitar": ms.MaxLeadScore = maxScore; break;
            case "Solo_Bass": ms.MaxBassScore = maxScore; break;
            case "Solo_Drums": ms.MaxDrumsScore = maxScore; break;
            case "Solo_Vocals": ms.MaxVocalsScore = maxScore; break;
            case "Solo_PeripheralGuitar": ms.MaxProLeadScore = maxScore; break;
            case "Solo_PeripheralBass": ms.MaxProBassScore = maxScore; break;
            case "Solo_PeripheralCymbals": ms.MaxProCymbalsScore = maxScore; break;
            case "Solo_PeripheralDrums": ms.MaxProDrumsScore = maxScore; break;
        }
        return ms;
    }

    private static Song CreateCatalogSong(
        string songId,
        string title) =>
        new()
        {
            _title = title,
            lastModified = new DateTime(
                2026,
                8,
                25,
                12,
                0,
                0,
                DateTimeKind.Utc),
            track = new Track
            {
                su = songId,
                tt = title,
                an = "Artist",
                ab = "Album",
                au = $"https://example.test/{songId}.jpg",
                mu = $"https://example.test/{songId}.dat",
                sig = "4/4",
                ge = ["rock"],
                ry = 2026,
                mt = 120,
                dn = 200,
                @in = new In { gr = 1 },
            },
        };

    private void EnsureSongRow(string songId)
    {
        var dsField = typeof(PathDataStore)
            .GetField("_ds", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        var ds = (Npgsql.NpgsqlDataSource)dsField.GetValue(_pathDataStore)!;
        using var conn = ds.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT INTO songs (song_id) VALUES (@sid) ON CONFLICT DO NOTHING";
        cmd.Parameters.AddWithValue("sid", songId);
        cmd.ExecuteNonQuery();
    }
}
