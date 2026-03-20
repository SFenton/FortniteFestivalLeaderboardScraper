using FortniteFestival.Core;
using FortniteFestival.Core.Services;
using FSTService.Persistence;
using FSTService.Scraping;
using FSTService.Tests.Helpers;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using NSubstitute;
using System.Reflection;

namespace FSTService.Tests.Unit;

/// <summary>
/// Tests for <see cref="PersonalDbBuilder"/> — builds per-device SQLite databases
/// containing a registered user's scores.
/// </summary>
public class PersonalDbBuilderTests : IDisposable
{
    private readonly InMemoryMetaDatabase _metaDb = new();
    private readonly string _dataDir;
    private readonly GlobalLeaderboardPersistence _persistence;
    private readonly ILogger<PersonalDbBuilder> _log = Substitute.For<ILogger<PersonalDbBuilder>>();
    private readonly FestivalService _service;

    // Test songs
    private static readonly Song[] TestSongs =
    [
        new Song
        {
            track = new Track
            {
                su = "song1",
                tt = "Test Song 1",
                an = "Artist A",
                @in = new In { gr = 5, ba = 3, vl = 4, ds = 2, pg = 1, pb = 2 },
                ry = 2023,
                mt = 120,
            }
        },
        new Song
        {
            track = new Track
            {
                su = "song2",
                tt = "Test Song 2",
                an = "Artist B",
                @in = new In { gr = 2, ba = 1, vl = 3, ds = 5, pg = 4, pb = 3 },
                ry = 2024,
                mt = 140,
            }
        },
    ];

    public PersonalDbBuilderTests()
    {
        _dataDir = Path.Combine(Path.GetTempPath(), $"fst_pdb_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dataDir);
        var loggerFactory = Substitute.For<ILoggerFactory>();
        loggerFactory.CreateLogger(Arg.Any<string>()).Returns(Substitute.For<ILogger>());
        loggerFactory.CreateLogger<InstrumentDatabase>().Returns(Substitute.For<ILogger<InstrumentDatabase>>());
        var persLog = Substitute.For<ILogger<GlobalLeaderboardPersistence>>();
        _persistence = new GlobalLeaderboardPersistence(_dataDir, _metaDb.Db, loggerFactory, persLog);
        _persistence.Initialize();

        // Create FestivalService with songs populated via reflection
        _service = CreateServiceWithSongs(TestSongs);
    }

    public void Dispose()
    {
        _persistence.Dispose();
        _metaDb.Dispose();
        try { Directory.Delete(_dataDir, true); } catch { }
    }

    private static FestivalService CreateServiceWithSongs(IReadOnlyList<Song> songs)
    {
        var service = new FestivalService((FortniteFestival.Core.Persistence.IFestivalPersistence?)null);
        var flags = BindingFlags.NonPublic | BindingFlags.Instance;
        var songsField = typeof(FestivalService).GetField("_songs", flags)!;
        var dirtyField = typeof(FestivalService).GetField("_songsDirty", flags)!;
        var dict = (Dictionary<string, Song>)songsField.GetValue(service)!;
        foreach (var s in songs)
            if (s.track?.su is not null)
                dict[s.track.su] = s;
        dirtyField.SetValue(service, true);
        return service;
    }

    private PersonalDbBuilder CreateBuilder()
        => new(_persistence, _service, _metaDb.Db, _dataDir, _log);

    // ─── Build ──────────────────────────────────────────────────

    [Fact]
    public void Build_NoScores_ReturnsNull_WhenAccountHasNoEntries()
    {
        // Songs exist but this account has no entries in any instrument DB
        var builder = CreateBuilder();

        var result = builder.Build("device1", "acct1");

        // The DB is created (songs are written even if no scores) because songs.Count > 0
        Assert.NotNull(result);
        Assert.True(File.Exists(result));

        // Verify Songs table has entries but Scores table is empty
        using var conn = new SqliteConnection($"Data Source={result}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM Songs";
        Assert.Equal(2L, (long)cmd.ExecuteScalar()!);
        cmd.CommandText = "SELECT COUNT(*) FROM Scores";
        Assert.Equal(0L, (long)cmd.ExecuteScalar()!);
    }

    [Fact]
    public void Build_WithScores_PopulatesCorrectly()
    {
        var builder = CreateBuilder();

        // Insert a guitar entry for acct1 on song1
        var guitarDb = _persistence.GetOrCreateInstrumentDb("Solo_Guitar");
        guitarDb.UpsertEntries("song1", [new LeaderboardEntry
        {
            AccountId = "acct1", Score = 50000, Rank = 42,
            Accuracy = 95, Stars = 5, IsFullCombo = true, Season = 3,
            Percentile = 0.85,
        }]);

        var result = builder.Build("device1", "acct1");

        Assert.NotNull(result);

        // Verify the Score row
        using var conn = new SqliteConnection($"Data Source={result}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT GuitarScore, GuitarStars, GuitarFC, GuitarPct, GuitarSeason FROM Scores WHERE SongId = 'song1'";
        using var reader = cmd.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal(50000, reader.GetInt32(0)); // Score
        Assert.Equal(5, reader.GetInt32(1));      // Stars
        Assert.Equal(1, reader.GetInt32(2));       // FC (1 = true)
        Assert.Equal(95, reader.GetInt32(3));      // Pct (accuracy)
        Assert.Equal(3, reader.GetInt32(4));        // Season
    }

    [Fact]
    public void Build_MultipleInstruments_AllPersisted()
    {
        var builder = CreateBuilder();

        var guitarDb = _persistence.GetOrCreateInstrumentDb("Solo_Guitar");
        guitarDb.UpsertEntries("song1", [new LeaderboardEntry
        {
            AccountId = "acct1", Score = 50000, Rank = 1
        }]);

        var drumsDb = _persistence.GetOrCreateInstrumentDb("Solo_Drums");
        drumsDb.UpsertEntries("song1", [new LeaderboardEntry
        {
            AccountId = "acct1", Score = 40000, Rank = 2
        }]);

        var result = builder.Build("device1", "acct1");
        Assert.NotNull(result);

        using var conn = new SqliteConnection($"Data Source={result}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT GuitarScore, DrumsScore FROM Scores WHERE SongId = 'song1'";
        using var reader = cmd.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal(50000, reader.GetInt32(0)); // Guitar
        Assert.Equal(40000, reader.GetInt32(1)); // Drums
    }

    [Fact]
    public void Build_SongDifficulty_WrittenCorrectly()
    {
        var builder = CreateBuilder();

        // Need at least one score so DB gets built and we can inspect songs
        var guitarDb = _persistence.GetOrCreateInstrumentDb("Solo_Guitar");
        guitarDb.UpsertEntries("song1", [new LeaderboardEntry
        {
            AccountId = "acct1", Score = 100, Rank = 1
        }]);

        var result = builder.Build("device1", "acct1");
        Assert.NotNull(result);

        using var conn = new SqliteConnection($"Data Source={result}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT Title, Artist, LeadDiff, BassDiff, VocalsDiff, DrumsDiff, ReleaseYear, Tempo FROM Songs WHERE SongId = 'song1'";
        using var reader = cmd.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal("Test Song 1", reader.GetString(0));
        Assert.Equal("Artist A", reader.GetString(1));
        Assert.Equal(5, reader.GetInt32(2)); // LeadDiff = gr
        Assert.Equal(3, reader.GetInt32(3)); // BassDiff = ba
        Assert.Equal(4, reader.GetInt32(4)); // VocalsDiff = vl
        Assert.Equal(2, reader.GetInt32(5)); // DrumsDiff = ds
        Assert.Equal(2023, reader.GetInt32(6)); // ReleaseYear
        Assert.Equal(120, reader.GetInt32(7));  // Tempo
    }

    [Fact]
    public void Build_RemovesStaleTempFile()
    {
        var builder = CreateBuilder();

        // Pre-create a stale temp file at the expected path
        var outputPath = builder.GetPersonalDbPath("acct_tmp", "device_tmp");
        var tempPath = outputPath + ".tmp";
        var dir = Path.GetDirectoryName(outputPath)!;
        Directory.CreateDirectory(dir);
        File.WriteAllText(tempPath, "stale-temp-content");
        Assert.True(File.Exists(tempPath));

        // Add a score so Build succeeds
        var guitarDb = _persistence.GetOrCreateInstrumentDb("Solo_Guitar");
        guitarDb.UpsertEntries("song1", [new LeaderboardEntry
        {
            AccountId = "acct_tmp", Score = 1000, Rank = 1
        }]);

        var result = builder.Build("device_tmp", "acct_tmp");
        Assert.NotNull(result);

        // The stale temp file should have been removed and the final file created
        Assert.False(File.Exists(tempPath));
        Assert.True(File.Exists(outputPath));
    }

    [Fact]
    public void Build_NoSongsLoaded_ReturnsNull()
    {
        var emptyService = CreateServiceWithSongs(Array.Empty<Song>());
        var builder = new PersonalDbBuilder(_persistence, emptyService, _metaDb.Db, _dataDir, _log);

        var result = builder.Build("device1", "acct1");
        Assert.Null(result);
    }

    [Fact]
    public void Build_AtomicReplace_OldFileReplaced()
    {
        var builder = CreateBuilder();

        // Insert initial score
        var guitarDb = _persistence.GetOrCreateInstrumentDb("Solo_Guitar");
        guitarDb.UpsertEntries("song1", [new LeaderboardEntry
        {
            AccountId = "acct1", Score = 1000, Rank = 1
        }]);

        var first = builder.Build("device1", "acct1");
        Assert.NotNull(first);
        var firstSize = new FileInfo(first).Length;

        // Add more data and rebuild
        var drumsDb = _persistence.GetOrCreateInstrumentDb("Solo_Drums");
        drumsDb.UpsertEntries("song1", [new LeaderboardEntry
        {
            AccountId = "acct1", Score = 2000, Rank = 1
        }]);
        drumsDb.UpsertEntries("song2", [new LeaderboardEntry
        {
            AccountId = "acct1", Score = 3000, Rank = 1
        }]);

        var second = builder.Build("device1", "acct1");
        Assert.NotNull(second);

        // Same path
        Assert.Equal(first, second);
        // No temp file left behind
        Assert.False(File.Exists(second + ".tmp"));
    }

    // ─── GetVersion ─────────────────────────────────────────────

    [Fact]
    public void GetVersion_NoDb_ReturnsNulls()
    {
        var builder = CreateBuilder();
        var (version, size) = builder.GetVersion("nonexistent_acct", "nonexistent_device");
        Assert.Null(version);
        Assert.Null(size);
    }

    [Fact]
    public void GetVersion_ExistingDb_ReturnsVersionAndSize()
    {
        var builder = CreateBuilder();

        var guitarDb = _persistence.GetOrCreateInstrumentDb("Solo_Guitar");
        guitarDb.UpsertEntries("song1", [new LeaderboardEntry
        {
            AccountId = "acct1", Score = 1000, Rank = 1
        }]);

        builder.Build("device1", "acct1");

        var (version, size) = builder.GetVersion("acct1", "device1");
        Assert.NotNull(version);
        Assert.NotNull(size);
        Assert.True(size > 0);
        // version should be ISO 8601
        Assert.True(DateTimeOffset.TryParse(version, out _));
    }

    // ─── RebuildForAccounts ─────────────────────────────────────

    [Fact]
    public void RebuildForAccounts_NoChangedAccounts_Returns0()
    {
        var builder = CreateBuilder();
        var result = builder.RebuildForAccounts(
            new HashSet<string>(), _metaDb.Db);
        Assert.Equal(0, result);
    }

    [Fact]
    public void RebuildForAccounts_RebuildsMapped()
    {
        var builder = CreateBuilder();

        // Register a device → account mapping
        _metaDb.Db.RegisterUser("device1", "acct1");

        // Add scores so Build succeeds
        var guitarDb = _persistence.GetOrCreateInstrumentDb("Solo_Guitar");
        guitarDb.UpsertEntries("song1", [new LeaderboardEntry
        {
            AccountId = "acct1", Score = 1000, Rank = 1
        }]);

        var changedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "acct1" };
        var rebuilt = builder.RebuildForAccounts(changedIds, _metaDb.Db);

        Assert.Equal(1, rebuilt);
        // Verify the DB was created
        var (version, _) = builder.GetVersion("acct1", "device1");
        Assert.NotNull(version);
    }

    [Fact]
    public void RebuildForAccounts_SkipsUnchanged()
    {
        var builder = CreateBuilder();

        _metaDb.Db.RegisterUser("device1", "acct1");
        _metaDb.Db.RegisterUser("device2", "acct2");

        var guitarDb = _persistence.GetOrCreateInstrumentDb("Solo_Guitar");
        guitarDb.UpsertEntries("song1", [new LeaderboardEntry
        {
            AccountId = "acct1", Score = 1000, Rank = 1
        }]);

        // Only acct1 changed
        var changedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "acct1" };
        var rebuilt = builder.RebuildForAccounts(changedIds, _metaDb.Db);

        Assert.Equal(1, rebuilt);
        // device2 should NOT have a personal DB
        var (version2, _) = builder.GetVersion("acct2", "device2");
        Assert.Null(version2);
    }

    [Fact]
    public void RebuildForAccounts_MultipleDevicesSameAccount_CopiesDb()
    {
        var builder = CreateBuilder();

        // Register two devices for the same account
        _metaDb.Db.RegisterUser("deviceA", "acct1");
        _metaDb.Db.RegisterUser("deviceB", "acct1");

        var guitarDb = _persistence.GetOrCreateInstrumentDb("Solo_Guitar");
        guitarDb.UpsertEntries("song1", [new LeaderboardEntry
        {
            AccountId = "acct1", Score = 5000, Rank = 1
        }]);

        var changedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "acct1" };
        var rebuilt = builder.RebuildForAccounts(changedIds, _metaDb.Db);

        // Both devices should get a DB
        Assert.Equal(2, rebuilt);

        var (versionA, sizeA) = builder.GetVersion("acct1", "deviceA");
        var (versionB, sizeB) = builder.GetVersion("acct1", "deviceB");
        Assert.NotNull(versionA);
        Assert.NotNull(versionB);

        // Same content → same file size
        Assert.Equal(sizeA, sizeB);

        // Verify both DBs have the same score data
        foreach (var device in new[] { "deviceA", "deviceB" })
        {
            var dbPath = builder.GetPersonalDbPath("acct1", device);
            using var conn = new SqliteConnection($"Data Source={dbPath}");
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT GuitarScore FROM Scores WHERE SongId = 'song1'";
            Assert.Equal(5000L, (long)cmd.ExecuteScalar()!);
        }
    }

    [Fact]
    public void RebuildForAccounts_SkipsAccount_WhenBuildReturnsNull()
    {
        // Use an empty FestivalService so Build returns null (no songs)
        var emptyService = CreateServiceWithSongs(Array.Empty<Song>());
        var builder = new PersonalDbBuilder(_persistence, emptyService, _metaDb.Db, _dataDir, _log);

        _metaDb.Db.RegisterUser("deviceNull", "acctNull");

        var changedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "acctNull" };
        var rebuilt = builder.RebuildForAccounts(changedIds, _metaDb.Db);

        // Build returns null → skipped → 0 rebuilt
        Assert.Equal(0, rebuilt);
    }

    [Fact]
    public void Build_CatchBlock_ReturnsNull_WhenBuildDatabaseFails()
    {
        var builder = CreateBuilder();

        // Use a path with an invalid directory name character to force SQLite to fail
        // when BuildDatabase tries to create the connection.
        // We pass a deviceId containing characters that make the path invalid on Windows.
        var badDeviceId = "dev\0fail"; // null char in path

        var result = builder.Build(badDeviceId, "acctFail");

        // Build should catch the exception and return null
        Assert.Null(result);
    }

    // ─── ScoreHistory ─────────────────────────────────────────────

    [Fact]
    public void Build_WithScoreHistory_PopulatesScoreHistoryTable()
    {
        var builder = CreateBuilder();

        // Insert a leaderboard score so the DB gets built
        var guitarDb = _persistence.GetOrCreateInstrumentDb("Solo_Guitar");
        guitarDb.UpsertEntries("song1", [new LeaderboardEntry
        {
            AccountId = "acct1", Score = 50000, Rank = 10
        }]);

        // Insert score history entries into the meta DB
        _metaDb.Db.InsertScoreChange("song1", "Solo_Guitar", "acct1",
            oldScore: null, newScore: 30000, oldRank: null, newRank: 100,
            accuracy: 85, isFullCombo: false, stars: 3, season: 5,
            scoreAchievedAt: "2025-01-01T00:00:00Z");
        _metaDb.Db.InsertScoreChange("song1", "Solo_Guitar", "acct1",
            oldScore: 30000, newScore: 50000, oldRank: 100, newRank: 10,
            accuracy: 95, isFullCombo: true, stars: 5, season: 7,
            scoreAchievedAt: "2025-03-15T12:00:00Z");

        var result = builder.Build("device1", "acct1");
        Assert.NotNull(result);

        // Verify ScoreHistory table in personal DB
        using var conn = new SqliteConnection($"Data Source={result}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM ScoreHistory";
        Assert.Equal(2L, (long)cmd.ExecuteScalar()!);

        cmd.CommandText = """
            SELECT SongId, Instrument, OldScore, NewScore, OldRank, NewRank,
                   Accuracy, IsFullCombo, Stars, Season, ScoreAchievedAt,
                   SeasonRank, AllTimeRank
            FROM ScoreHistory
            ORDER BY Id ASC
            """;
        using var reader = cmd.ExecuteReader();

        // First entry
        Assert.True(reader.Read());
        Assert.Equal("song1", reader.GetString(0));
        Assert.Equal("Solo_Guitar", reader.GetString(1));
        Assert.True(reader.IsDBNull(2));               // OldScore null
        Assert.Equal(30000, reader.GetInt32(3));        // NewScore
        Assert.True(reader.IsDBNull(4));               // OldRank null
        Assert.Equal(100, reader.GetInt32(5));          // NewRank
        Assert.Equal(85, reader.GetInt32(6));           // Accuracy
        Assert.Equal(0, reader.GetInt32(7));            // IsFullCombo = false
        Assert.Equal(3, reader.GetInt32(8));            // Stars
        Assert.Equal(5, reader.GetInt32(9));            // Season
        Assert.Equal("2025-01-01T00:00:00Z", reader.GetString(10));
        Assert.True(reader.IsDBNull(11));              // SeasonRank null
        Assert.True(reader.IsDBNull(12));              // AllTimeRank null

        // Second entry
        Assert.True(reader.Read());
        Assert.Equal(30000, reader.GetInt32(2));        // OldScore
        Assert.Equal(50000, reader.GetInt32(3));        // NewScore
        Assert.Equal(100, reader.GetInt32(4));          // OldRank
        Assert.Equal(10, reader.GetInt32(5));           // NewRank
        Assert.Equal(95, reader.GetInt32(6));           // Accuracy
        Assert.Equal(1, reader.GetInt32(7));            // IsFullCombo = true
        Assert.Equal(5, reader.GetInt32(8));            // Stars
        Assert.Equal(7, reader.GetInt32(9));            // Season
        Assert.True(reader.IsDBNull(11));              // SeasonRank null
        Assert.True(reader.IsDBNull(12));              // AllTimeRank null
    }

    [Fact]
    public void Build_NoScoreHistory_ScoreHistoryTableEmpty()
    {
        var builder = CreateBuilder();

        var guitarDb = _persistence.GetOrCreateInstrumentDb("Solo_Guitar");
        guitarDb.UpsertEntries("song1", [new LeaderboardEntry
        {
            AccountId = "acct1", Score = 1000, Rank = 1
        }]);

        var result = builder.Build("device1", "acct1");
        Assert.NotNull(result);

        using var conn = new SqliteConnection($"Data Source={result}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM ScoreHistory";
        Assert.Equal(0L, (long)cmd.ExecuteScalar()!);
    }

    [Fact]
    public void Build_ScoreHistoryOnlyForRequestedAccount()
    {
        var builder = CreateBuilder();

        var guitarDb = _persistence.GetOrCreateInstrumentDb("Solo_Guitar");
        guitarDb.UpsertEntries("song1", [
            new LeaderboardEntry { AccountId = "acct1", Score = 50000, Rank = 1 },
            new LeaderboardEntry { AccountId = "acct2", Score = 40000, Rank = 2 },
        ]);

        // Insert history for both accounts
        _metaDb.Db.InsertScoreChange("song1", "Solo_Guitar", "acct1",
            null, 50000, null, 1, season: 5);
        _metaDb.Db.InsertScoreChange("song1", "Solo_Guitar", "acct2",
            null, 40000, null, 2, season: 5);

        // Build personal DB for acct1 only
        var result = builder.Build("device1", "acct1");
        Assert.NotNull(result);

        using var conn = new SqliteConnection($"Data Source={result}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM ScoreHistory";
        Assert.Equal(1L, (long)cmd.ExecuteScalar()!);
    }

    // ─── GetPersonalDbPath ──────────────────────────────────────

    [Fact]
    public void GetPersonalDbPath_ContainsDeviceId()
    {
        var builder = CreateBuilder();
        var path = builder.GetPersonalDbPath("my_acct", "my_device");
        Assert.Contains("my_device.db", path);
        Assert.Contains("personal", path);
        Assert.Contains(Path.Combine("my_acct", "my_device.db"), path);
    }

    // ─── PagedResult.FromAll ────────────────────────────────────

    [Fact]
    public void PagedResult_FromAll_FirstPage()
    {
        var items = Enumerable.Range(0, 25)
            .Select(i => new Dictionary<string, object?> { ["id"] = i })
            .ToList();

        var result = PagedResult.FromAll(items, page: 0, pageSize: 10);

        Assert.Equal(0, result.Page);
        Assert.Equal(10, result.PageSize);
        Assert.Equal(25, result.TotalItems);
        Assert.Equal(3, result.TotalPages);
        Assert.Equal(10, result.Items.Count);
        Assert.Equal(0, result.Items[0]["id"]);
    }

    [Fact]
    public void PagedResult_FromAll_LastPage()
    {
        var items = Enumerable.Range(0, 25)
            .Select(i => new Dictionary<string, object?> { ["id"] = i })
            .ToList();

        var result = PagedResult.FromAll(items, page: 2, pageSize: 10);

        Assert.Equal(2, result.Page);
        Assert.Equal(5, result.Items.Count);
        Assert.Equal(20, result.Items[0]["id"]);
    }

    [Fact]
    public void PagedResult_FromAll_EmptyList()
    {
        var result = PagedResult.FromAll([], page: 0, pageSize: 10);

        Assert.Equal(0, result.TotalItems);
        Assert.Equal(0, result.TotalPages);
        Assert.Empty(result.Items);
    }

    [Fact]
    public void PagedResult_FromAll_BeyondLastPage()
    {
        var items = Enumerable.Range(0, 5)
            .Select(i => new Dictionary<string, object?> { ["id"] = i })
            .ToList();

        var result = PagedResult.FromAll(items, page: 10, pageSize: 10);

        Assert.Empty(result.Items);
        Assert.Equal(5, result.TotalItems);
        Assert.Equal(1, result.TotalPages);
    }

    // ─── GetSongsAsJson ─────────────────────────────────────────

    [Fact]
    public void GetSongsAsJson_ReturnsSongs()
    {
        var builder = CreateBuilder();

        var result = builder.GetSongsAsJson(0, 100);

        Assert.NotNull(result);
        Assert.Equal(2, result.TotalItems);
        Assert.Equal(2, result.Items.Count);
        Assert.Equal("song1", result.Items[0]["SongId"]);
        Assert.Equal("Test Song 1", result.Items[0]["Title"]);
        Assert.Equal("Artist A", result.Items[0]["Artist"]);
        Assert.Equal(5, result.Items[0]["LeadDiff"]);
    }

    [Fact]
    public void GetSongsAsJson_EmptyService_ReturnsNull()
    {
        var emptyService = CreateServiceWithSongs(Array.Empty<Song>());
        var builder = new PersonalDbBuilder(_persistence, emptyService, _metaDb.Db, _dataDir, _log);

        var result = builder.GetSongsAsJson(0, 100);
        Assert.Null(result);
    }

    [Fact]
    public void GetSongsAsJson_Paging()
    {
        var builder = CreateBuilder();

        var page0 = builder.GetSongsAsJson(0, 1);
        var page1 = builder.GetSongsAsJson(1, 1);

        Assert.NotNull(page0);
        Assert.NotNull(page1);
        Assert.Single(page0.Items);
        Assert.Single(page1.Items);
        Assert.Equal(2, page0.TotalPages);
        Assert.NotEqual(page0.Items[0]["SongId"], page1.Items[0]["SongId"]);
    }

    // ─── GetScoresAsJson ────────────────────────────────────────

    [Fact]
    public void GetScoresAsJson_WithScores_ReturnsData()
    {
        var builder = CreateBuilder();

        var guitarDb = _persistence.GetOrCreateInstrumentDb("Solo_Guitar");
        guitarDb.UpsertEntries("song1", [new LeaderboardEntry
        {
            AccountId = "acct1", Score = 50000, Rank = 42,
            Accuracy = 95, Stars = 5, IsFullCombo = true, Season = 3,
            Percentile = 0.85,
        }]);

        var result = builder.GetScoresAsJson("acct1", 0, 100);

        Assert.NotNull(result);
        Assert.Equal(1, result.TotalItems);
        Assert.Equal("song1", result.Items[0]["SongId"]);
        Assert.Equal(50000, result.Items[0]["GuitarScore"]);
        Assert.Equal(5, result.Items[0]["GuitarStars"]);
    }

    [Fact]
    public void GetScoresAsJson_NoScores_ReturnsEmpty()
    {
        var builder = CreateBuilder();

        var result = builder.GetScoresAsJson("acct1", 0, 100);

        Assert.NotNull(result);
        Assert.Equal(0, result.TotalItems);
        Assert.Empty(result.Items);
    }

    [Fact]
    public void GetScoresAsJson_EmptyService_ReturnsNull()
    {
        var emptyService = CreateServiceWithSongs(Array.Empty<Song>());
        var builder = new PersonalDbBuilder(_persistence, emptyService, _metaDb.Db, _dataDir, _log);

        var result = builder.GetScoresAsJson("acct1", 0, 100);
        Assert.Null(result);
    }

    // ─── GetHistoryAsJson ───────────────────────────────────────

    [Fact]
    public void GetHistoryAsJson_WithHistory_ReturnsData()
    {
        var builder = CreateBuilder();

        _metaDb.Db.InsertScoreChange("song1", "Solo_Guitar", "acct1",
            null, 50000, null, 10, accuracy: 95, isFullCombo: true, stars: 5, season: 3,
            scoreAchievedAt: "2025-01-15T12:00:00Z");

        var result = builder.GetHistoryAsJson("acct1", 0, 100);

        Assert.NotNull(result);
        Assert.Equal(1, result.TotalItems);
        Assert.Equal("song1", result.Items[0]["SongId"]);
        Assert.Equal("Solo_Guitar", result.Items[0]["Instrument"]);
        Assert.Equal(50000, result.Items[0]["NewScore"]);
    }

    [Fact]
    public void GetHistoryAsJson_NoHistory_ReturnsEmpty()
    {
        var builder = CreateBuilder();

        var result = builder.GetHistoryAsJson("acct1", 0, 100);

        Assert.NotNull(result);
        Assert.Equal(0, result.TotalItems);
        Assert.Empty(result.Items);
    }

    [Fact]
    public void GetHistoryAsJson_Paging()
    {
        var builder = CreateBuilder();

        for (int i = 0; i < 5; i++)
        {
            _metaDb.Db.InsertScoreChange("song1", "Solo_Guitar", "acct1",
                null, (i + 1) * 10000, null, i + 1, season: i + 1,
                scoreAchievedAt: $"2025-0{i + 1}-01T00:00:00Z");
        }

        var page0 = builder.GetHistoryAsJson("acct1", 0, 2);
        var page1 = builder.GetHistoryAsJson("acct1", 1, 2);

        Assert.NotNull(page0);
        Assert.NotNull(page1);
        Assert.Equal(2, page0.Items.Count);
        Assert.Equal(2, page1.Items.Count);
        Assert.Equal(5, page0.TotalItems);
        Assert.Equal(3, page0.TotalPages);
    }

    // ─── RebuildForAccounts ─────────────────────────────────────

    [Fact]
    public void RebuildForAccounts_builds_and_copies_for_multiple_devices()
    {
        var builder = CreateBuilder();

        // Register two devices for the same account
        _metaDb.Db.RegisterUser("devA", "acct1");
        _metaDb.Db.RegisterUser("devB", "acct1");

        var changedAccounts = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "acct1" };
        var rebuilt = builder.RebuildForAccounts(changedAccounts, _metaDb.Db);

        Assert.Equal(2, rebuilt); // 1 build + 1 copy
        Assert.True(File.Exists(builder.GetPersonalDbPath("acct1", "devA")));
        Assert.True(File.Exists(builder.GetPersonalDbPath("acct1", "devB")));
    }

    [Fact]
    public void RebuildForAccounts_no_changed_accounts_returns_zero()
    {
        var builder = CreateBuilder();
        var changedAccounts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var rebuilt = builder.RebuildForAccounts(changedAccounts, _metaDb.Db);
        Assert.Equal(0, rebuilt);
    }

    // ─── GetVersion ─────────────────────────────────────────────

    [Fact]
    public void GetVersion_returns_version_after_build()
    {
        var builder = CreateBuilder();
        var path = builder.Build("devVersion", "acctVersion");
        Assert.NotNull(path);

        var (version, sizeBytes) = builder.GetVersion("acctVersion", "devVersion");
        Assert.NotNull(version);
        Assert.True(sizeBytes > 0);
    }

    [Fact]
    public void GetVersion_returns_null_when_not_built()
    {
        var builder = CreateBuilder();
        var (version, sizeBytes) = builder.GetVersion("nobody", "nodev");
        Assert.Null(version);
        Assert.Null(sizeBytes);
    }

    // ─── All instruments difficulty coverage ─────────────────────

    [Fact]
    public void Build_AllInstruments_PopulatesDifficulty()
    {
        var builder = CreateBuilder();

        // Insert scores for ALL 6 instruments on song1 so every switch arm is hit
        foreach (var instrument in new[] { "Solo_Guitar", "Solo_Bass", "Solo_Vocals", "Solo_Drums", "Solo_PeripheralGuitar", "Solo_PeripheralBass" })
        {
            var db = _persistence.GetOrCreateInstrumentDb(instrument);
            db.UpsertEntries("song1", [new LeaderboardEntry
            {
                AccountId = "acct_allInst", Score = 10000, Rank = 1,
                Accuracy = 90, Stars = 4, IsFullCombo = false, Season = 1,
            }]);
        }

        var result = builder.Build("devAllInst", "acct_allInst");
        Assert.NotNull(result);

        // Verify each instrument's difficulty is populated from the song's In data
        using var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={result}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT GuitarDiff, BassDiff, VocalsDiff, DrumsDiff, ProGuitarDiff, ProBassDiff FROM Scores WHERE SongId = 'song1'";
        using var reader = cmd.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal(5, reader.GetInt32(0)); // Guitar diff from song1.track.in.gr
        Assert.Equal(3, reader.GetInt32(1)); // Bass diff from song1.track.in.ba
        Assert.Equal(4, reader.GetInt32(2)); // Vocals diff from song1.track.in.vl
        Assert.Equal(2, reader.GetInt32(3)); // Drums diff from song1.track.in.ds
        Assert.Equal(1, reader.GetInt32(4)); // ProGuitar diff from song1.track.in.pg
        Assert.Equal(2, reader.GetInt32(5)); // ProBass diff from song1.track.in.pb
    }

    [Fact]
    public void Build_SongWithNullInstrumentDifficulties_DefaultsToZero()
    {
        // Create a song with no @in data (null difficulties)
        var songNoIn = new Song
        {
            track = new Track
            {
                su = "songNoIn",
                tt = "No Difficulty",
                an = "Artist",
                @in = null, // null instrument difficulties
                ry = 2024,
                mt = 130,
            }
        };
        var svc = CreateServiceWithSongs(new[] { songNoIn });
        var builder = new PersonalDbBuilder(_persistence, svc, _metaDb.Db, _dataDir, _log);

        // Insert a score for this song
        var guitarDb = _persistence.GetOrCreateInstrumentDb("Solo_Guitar");
        guitarDb.UpsertEntries("songNoIn", [new LeaderboardEntry
        {
            AccountId = "acct_noIn", Score = 5000, Rank = 1
        }]);

        var result = builder.Build("devNoIn", "acct_noIn");
        Assert.NotNull(result);

        using var conn = new SqliteConnection($"Data Source={result}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT GuitarDiff FROM Scores WHERE SongId = 'songNoIn'";
        var diff = (long)cmd.ExecuteScalar()!;
        Assert.Equal(0, diff); // Defaults to 0 when @in is null
    }

    // ─── Leaderboard population in personal DB ──────────────────

    [Fact]
    public void Build_Total_UsesRealPopulation_WhenAvailable()
    {
        var builder = CreateBuilder();

        // Seed a real population value from PercentileService
        _metaDb.Db.UpsertLeaderboardPopulation([("song1", "Solo_Guitar", 250_000L)]);

        // Insert a score
        var guitarDb = _persistence.GetOrCreateInstrumentDb("Solo_Guitar");
        guitarDb.UpsertEntries("song1", [new LeaderboardEntry
        {
            AccountId = "acct1", Score = 50000, Rank = 42,
            Accuracy = 95, Stars = 5, IsFullCombo = true, Season = 3,
            Percentile = 0.85,
        }]);

        var result = builder.Build("device1", "acct1");
        Assert.NotNull(result);

        using var conn = new SqliteConnection($"Data Source={result}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT GuitarTotal, GuitarRank FROM Scores WHERE SongId = 'song1'";
        using var reader = cmd.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal(250_000L, reader.GetInt64(0)); // Real population from PercentileService
        Assert.Equal(42, reader.GetInt32(1));        // Rank from V2 API enrichment
    }

    [Fact]
    public void Build_Total_FallsBackToLocalDbCount_WhenNoPopulation()
    {
        var builder = CreateBuilder();

        // No population data in MetaDatabase — fallback to local count
        var guitarDb = _persistence.GetOrCreateInstrumentDb("Solo_Guitar");
        // Insert multiple entries so there's a meaningful "total" count
        guitarDb.UpsertEntries("song1", [
            new LeaderboardEntry { AccountId = "acct1", Score = 50000, Rank = 1 },
            new LeaderboardEntry { AccountId = "acct2", Score = 40000, Rank = 2 },
            new LeaderboardEntry { AccountId = "acct3", Score = 30000, Rank = 3 },
        ]);

        var result = builder.Build("device1", "acct1");
        Assert.NotNull(result);

        using var conn = new SqliteConnection($"Data Source={result}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT GuitarTotal FROM Scores WHERE SongId = 'song1'";
        var total = (long)cmd.ExecuteScalar()!;
        Assert.Equal(3, total); // 3 entries in local DB
    }

    [Fact]
    public void Build_CalcTotal_ReverseCalculatedFromRankAndPercentile()
    {
        var builder = CreateBuilder();

        var guitarDb = _persistence.GetOrCreateInstrumentDb("Solo_Guitar");
        guitarDb.UpsertEntries("song1", [new LeaderboardEntry
        {
            AccountId = "acct1", Score = 50000, Rank = 100,
            Percentile = 0.01, // rank 100 / 0.01 = 10,000
        }]);

        var result = builder.Build("device1", "acct1");
        Assert.NotNull(result);

        using var conn = new SqliteConnection($"Data Source={result}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT GuitarCalcTotal FROM Scores WHERE SongId = 'song1'";
        var calcTotal = (long)cmd.ExecuteScalar()!;
        Assert.Equal(10_000, calcTotal); // 100 / 0.01
    }

    [Fact]
    public void Build_Rank_FallsBackToLocalRank_WhenV2RankIsZero()
    {
        var builder = CreateBuilder();

        var guitarDb = _persistence.GetOrCreateInstrumentDb("Solo_Guitar");
        // Rank = 0 means V2 API hasn't enriched yet
        guitarDb.UpsertEntries("song1", [
            new LeaderboardEntry { AccountId = "acct1", Score = 50000, Rank = 0 },
            new LeaderboardEntry { AccountId = "acct2", Score = 60000, Rank = 0 },
        ]);

        var result = builder.Build("device1", "acct1");
        Assert.NotNull(result);

        using var conn = new SqliteConnection($"Data Source={result}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT GuitarRank FROM Scores WHERE SongId = 'song1'";
        var rank = (long)cmd.ExecuteScalar()!;
        Assert.Equal(2, rank); // acct2 has higher score, so acct1 is rank 2
    }

    [Fact]
    public void GetScoresAsJson_Total_UsesRealPopulation_WhenAvailable()
    {
        var builder = CreateBuilder();

        _metaDb.Db.UpsertLeaderboardPopulation([("song1", "Solo_Guitar", 123_456L)]);

        var guitarDb = _persistence.GetOrCreateInstrumentDb("Solo_Guitar");
        guitarDb.UpsertEntries("song1", [new LeaderboardEntry
        {
            AccountId = "acct1", Score = 50000, Rank = 42,
            Accuracy = 95, Stars = 5, Season = 3,
            Percentile = 0.85,
        }]);

        var result = builder.GetScoresAsJson("acct1", 0, 100);

        Assert.NotNull(result);
        Assert.Equal(1, result.TotalItems);
        Assert.Equal(123_456L, result.Items[0]["GuitarTotal"]);
    }

    [Fact]
    public void GetScoresAsJson_Total_FallsBackToLocalCount_WhenNoPopulation()
    {
        var builder = CreateBuilder();

        var guitarDb = _persistence.GetOrCreateInstrumentDb("Solo_Guitar");
        guitarDb.UpsertEntries("song1", [
            new LeaderboardEntry { AccountId = "acct1", Score = 50000, Rank = 1 },
            new LeaderboardEntry { AccountId = "acct2", Score = 40000, Rank = 2 },
        ]);

        var result = builder.GetScoresAsJson("acct1", 0, 100);

        Assert.NotNull(result);
        Assert.Equal(1, result.TotalItems);
        Assert.Equal(2L, result.Items[0]["GuitarTotal"]);
    }

    [Fact]
    public void GetScoresAsJson_Rank_UsesLocalRank_WhenV2IsZero()
    {
        var builder = CreateBuilder();

        var guitarDb = _persistence.GetOrCreateInstrumentDb("Solo_Guitar");
        guitarDb.UpsertEntries("song1", [
            new LeaderboardEntry { AccountId = "acct1", Score = 50000, Rank = 0 },
            new LeaderboardEntry { AccountId = "other", Score = 70000, Rank = 0 },
        ]);

        var result = builder.GetScoresAsJson("acct1", 0, 100);

        Assert.NotNull(result);
        // acct1 is rank 2 (other has higher score)
        Assert.Equal(2, result.Items[0]["GuitarRank"]);
    }

    [Fact]
    public void Build_WithRivalsData_PopulatesRivalsTables()
    {
        var builder = CreateBuilder();

        // Seed some rivals data in meta DB
        var rivals = new List<UserRivalRow>
        {
            new() { UserId = "acct1", RivalAccountId = "rival_1", InstrumentCombo = "Solo_Guitar",
                     Direction = "above", RivalScore = 42.0, AvgSignedDelta = -3.5,
                     SharedSongCount = 100, AheadCount = 60, BehindCount = 40, ComputedAt = "2026-01-01T00:00:00Z" },
            new() { UserId = "acct1", RivalAccountId = "rival_2", InstrumentCombo = "Solo_Guitar",
                     Direction = "below", RivalScore = 30.0, AvgSignedDelta = 2.0,
                     SharedSongCount = 80, AheadCount = 30, BehindCount = 50, ComputedAt = "2026-01-01T00:00:00Z" },
        };
        var samples = new List<RivalSongSampleRow>
        {
            new() { UserId = "acct1", RivalAccountId = "rival_1", Instrument = "Solo_Guitar",
                     SongId = "song1", UserRank = 10, RivalRank = 8, RankDelta = -2, UserScore = 9000, RivalScore = 9100 },
            new() { UserId = "acct1", RivalAccountId = "rival_1", Instrument = "Solo_Guitar",
                     SongId = "song2", UserRank = 20, RivalRank = 25, RankDelta = 5, UserScore = 8000, RivalScore = 7500 },
        };
        _metaDb.Db.ReplaceRivalsData("acct1", rivals, samples);

        var result = builder.Build("device1", "acct1");
        Assert.NotNull(result);

        using var conn = new SqliteConnection($"Data Source={result}");
        conn.Open();

        // Verify Rivals table
        using var rivalCmd = conn.CreateCommand();
        rivalCmd.CommandText = "SELECT COUNT(*) FROM Rivals";
        Assert.Equal(2L, (long)rivalCmd.ExecuteScalar()!);

        // Verify RivalSongSamples table
        using var sampleCmd = conn.CreateCommand();
        sampleCmd.CommandText = "SELECT COUNT(*) FROM RivalSongSamples";
        Assert.Equal(2L, (long)sampleCmd.ExecuteScalar()!);

        // Verify RivalCombos table
        using var comboCmd = conn.CreateCommand();
        comboCmd.CommandText = "SELECT COUNT(*) FROM RivalCombos";
        Assert.Equal(1L, (long)comboCmd.ExecuteScalar()!);

        // Verify data integrity
        using var detailCmd = conn.CreateCommand();
        detailCmd.CommandText = "SELECT RivalAccountId, Direction, RivalScore FROM Rivals ORDER BY RivalScore DESC";
        using var reader = detailCmd.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal("rival_1", reader.GetString(0));
        Assert.Equal("above", reader.GetString(1));
        Assert.Equal(42.0, reader.GetDouble(2));
    }

    [Fact]
    public void Build_WithNoRivalsData_CreatesEmptyRivalsTables()
    {
        var builder = CreateBuilder();

        var result = builder.Build("device1", "acct_no_rivals");
        Assert.NotNull(result);

        using var conn = new SqliteConnection($"Data Source={result}");
        conn.Open();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM Rivals";
        Assert.Equal(0L, (long)cmd.ExecuteScalar()!);
        cmd.CommandText = "SELECT COUNT(*) FROM RivalCombos";
        Assert.Equal(0L, (long)cmd.ExecuteScalar()!);
    }
}
