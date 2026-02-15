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
        => new(_persistence, _service, _dataDir, _log);

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
    public void Build_NoSongsLoaded_ReturnsNull()
    {
        var emptyService = CreateServiceWithSongs(Array.Empty<Song>());
        var builder = new PersonalDbBuilder(_persistence, emptyService, _dataDir, _log);

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
        var (version, size) = builder.GetVersion("nonexistent_device");
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

        var (version, size) = builder.GetVersion("device1");
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
        var (version, _) = builder.GetVersion("device1");
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
        var (version2, _) = builder.GetVersion("device2");
        Assert.Null(version2);
    }

    // ─── GetPersonalDbPath ──────────────────────────────────────

    [Fact]
    public void GetPersonalDbPath_ContainsDeviceId()
    {
        var builder = CreateBuilder();
        var path = builder.GetPersonalDbPath("my_device");
        Assert.Contains("my_device.db", path);
        Assert.Contains("personal", path);
    }
}
