using FSTService.Persistence;
using FSTService.Scraping;
using FSTService.Tests.Helpers;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace FSTService.Tests.Unit;

public sealed class BandRetentionFloorTests : IDisposable
{
    private const string BandType = "Band_Duets";
    private const int MaxValid = 5;

    private readonly InMemoryMetaDatabase _enforced = new();
    private readonly InMemoryMetaDatabase _baseline = new();

    public void Dispose()
    {
        _enforced.Dispose();
        _baseline.Dispose();
    }

    [Fact]
    public void Prune_records_floor_at_last_unconditionally_kept_rank()
    {
        var persistence = Persistence(_enforced);
        var rows = new List<BandLeaderboardEntry>
        {
            Entry("over-a", 900, isOverThreshold: true),
            Entry("over-b", 890, isOverThreshold: true),
        };
        for (var i = 0; i < 7; i++)
            rows.Add(Entry($"valid-{i}", 800 - (i * 10)));
        Upsert(persistence, "song-a", rows);
        Upsert(persistence, "song-b", [Entry("small-0", 500), Entry("small-1", 400)]);

        var result = persistence.PruneBandEntriesDetailed(
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            MaxValid,
            captureRetentionFloor: true, retentionFloorMarginRows: 0);

        Assert.Equal(2, result.DeletedEntries);
        Assert.Equal(1, result.RetentionFloor!.Scopes);
        var floor = Assert.Single(ReadFloors(_enforced));
        Assert.Equal(("song-a", 7, 760, TeamKey("valid-0")), (floor.SongId, floor.Rank, floor.Score, floor.FirstValidTeamKey));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    public async Task Enforced_floor_leaves_the_same_rows_as_flushing_everything(int seed)
    {
        var random = new Random(seed);
        var registered = new HashSet<string>(["reg-1"], StringComparer.OrdinalIgnoreCase);
        var enforced = Persistence(_enforced);
        var baseline = Persistence(_baseline);
        var songs = new[] { "song-a", "song-b" };
        var initial = songs.ToDictionary(
            static song => song,
            song => GenerateBoard(random, song, 14));
        foreach (var (song, entries) in initial)
        {
            Upsert(enforced, song, entries);
            Upsert(baseline, song, entries);
        }

        enforced.PruneBandEntriesDetailed(registered, MaxValid, captureRetentionFloor: true, retentionFloorMarginRows: 0);
        baseline.PruneBandEntriesDetailed(registered, MaxValid);
        Assert.Equal(Snapshot(_baseline), Snapshot(_enforced));

        long belowFloor = 0;
        for (var round = 0; round < 3; round++)
        {
            var staged = songs.ToDictionary(
                static song => song,
                song => GenerateStagedPage(random, song, ReadKeys(_baseline, song), round));

            var filter = new BandRetentionFloorFilter(BandRetentionFloorMode.Enforce, registered, MaxValid);
            await using (var spool = BandSpoolWriterFactory.Create(Logger(), enforced, retentionFloor: filter))
            {
                foreach (var (song, entries) in staged)
                    spool.Enqueue(song, BandType, entries);
                spool.Complete();
                filter.PrepareForFlush(_enforced.DataSource);
                spool.FlushAll(maxBatchPages: 1);
            }

            await using (var spool = BandSpoolWriterFactory.Create(Logger(), baseline))
            {
                foreach (var (song, entries) in staged)
                    spool.Enqueue(song, BandType, entries);
                spool.Complete();
                spool.FlushAll(maxBatchPages: 1);
            }

            belowFloor += filter.BelowFloorRows;
            enforced.PruneBandEntriesDetailed(registered, MaxValid, captureRetentionFloor: true, retentionFloorMarginRows: 0);
            baseline.PruneBandEntriesDetailed(registered, MaxValid);

            Assert.Equal(Snapshot(_baseline), Snapshot(_enforced));
        }

        Assert.True(belowFloor > 0, "The floor never applied, so the comparison proves nothing.");
    }

    [Fact]
    public async Task Report_mode_keeps_rows_and_prune_reports_no_survivors()
    {
        var persistence = Persistence(_enforced);
        var board = Enumerable.Range(0, 8).Select(i => Entry($"team-{i}", 800 - (i * 10))).ToList();
        Upsert(persistence, "song-a", board);
        var registered = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        persistence.PruneBandEntriesDetailed(registered, MaxValid, captureRetentionFloor: true, retentionFloorMarginRows: 0);

        var filter = new BandRetentionFloorFilter(BandRetentionFloorMode.Report, registered, MaxValid);
        await using (var spool = BandSpoolWriterFactory.Create(Logger(), persistence, retentionFloor: filter))
        {
            spool.Enqueue("song-a", BandType, [Entry("late-1", 500), Entry("late-2", 400), Entry("high", 999)]);
            spool.Complete();
            filter.PrepareForFlush(_enforced.DataSource);
            spool.FlushAll();
        }

        Assert.Equal(2, filter.BelowFloorRows);
        Assert.Contains(TeamKey("late-1"), ReadKeys(_enforced, "song-a"));

        var result = persistence.PruneBandEntriesDetailed(registered, MaxValid, captureRetentionFloor: true, retentionFloorMarginRows: 0);

        Assert.Equal((2L, 0L), (result.RetentionFloor!.ShadowRows, result.RetentionFloor.ShadowRowsKept));
        Assert.DoesNotContain(TeamKey("late-1"), ReadKeys(_enforced, "song-a"));
    }

    [Fact]
    public async Task Report_mode_detects_a_floor_lowered_outside_the_flush()
    {
        var persistence = Persistence(_enforced);
        var board = Enumerable.Range(0, 8).Select(i => Entry($"team-{i}", 800 - (i * 10))).ToList();
        Upsert(persistence, "song-a", board);
        var registered = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        persistence.PruneBandEntriesDetailed(registered, MaxValid, captureRetentionFloor: true, retentionFloorMarginRows: 0);

        // The first valid entry turns over-threshold without invalidating the floor,
        // so prune now keeps one more row than the floor promised.
        Execute(_enforced, $"UPDATE band_entries SET is_over_threshold = TRUE WHERE team_key = '{TeamKey("team-0")}'");

        var filter = new BandRetentionFloorFilter(BandRetentionFloorMode.Report, registered, MaxValid);
        await using (var spool = BandSpoolWriterFactory.Create(Logger(), persistence, retentionFloor: filter))
        {
            spool.Enqueue("song-a", BandType, [Entry("late-1", 755)]);
            spool.Complete();
            filter.PrepareForFlush(_enforced.DataSource);
            spool.FlushAll();
        }

        var result = persistence.PruneBandEntriesDetailed(registered, MaxValid, captureRetentionFloor: true, retentionFloorMarginRows: 0);

        Assert.Equal((1L, 1L), (result.RetentionFloor!.ShadowRows, result.RetentionFloor.ShadowRowsKept));
    }

    [Theory]
    [InlineData(0, 1L)]
    [InlineData(2, 0L)]
    public async Task Enforce_detects_a_window_lowered_after_the_flush_and_margin_absorbs_it(int margin, long expectedKept)
    {
        var registered = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var enforced = Persistence(_enforced);
        var baseline = Persistence(_baseline);
        var board = Enumerable.Range(0, 9).Select(i => Entry($"team-{i}", 800 - (i * 10))).ToList();
        Upsert(enforced, "song-a", board);
        Upsert(baseline, "song-a", board);
        enforced.PruneBandEntriesDetailed(registered, MaxValid, captureRetentionFloor: true, retentionFloorMarginRows: margin);
        baseline.PruneBandEntriesDetailed(registered, MaxValid);

        // Production re-fetches the rows just below the window every scrape, so the
        // staged page includes them along with rows further down.
        BandLeaderboardEntry[] staged = [Entry("team-5", 750), Entry("team-6", 740), Entry("late-1", 735), Entry("late-2", 300)];
        var filter = new BandRetentionFloorFilter(BandRetentionFloorMode.Enforce, registered, MaxValid);
        await using (var spool = BandSpoolWriterFactory.Create(Logger(), enforced, retentionFloor: filter))
        {
            spool.Enqueue("song-a", BandType, staged);
            spool.Complete();
            filter.PrepareForFlush(_enforced.DataSource);
            spool.FlushAll();
        }

        await using (var spool = BandSpoolWriterFactory.Create(Logger(), baseline))
        {
            spool.Enqueue("song-a", BandType, staged);
            spool.Complete();
            spool.FlushAll();
        }

        // Band extraction can mark the first valid entry over-threshold after the
        // flush; prune then keeps one more row at the bottom.
        var flip = Entry("team-0", 800, isOverThreshold: true);
        Upsert(enforced, "song-a", [flip]);
        Upsert(baseline, "song-a", [flip]);

        var result = enforced.PruneBandEntriesDetailed(registered, MaxValid, captureRetentionFloor: true, retentionFloorMarginRows: margin);
        baseline.PruneBandEntriesDetailed(registered, MaxValid);

        Assert.Equal(expectedKept, result.RetentionFloor!.ShadowRowsKept);
        if (expectedKept == 0)
            Assert.Equal(Snapshot(_baseline), Snapshot(_enforced));
        else
            Assert.NotEqual(Snapshot(_baseline), Snapshot(_enforced));
    }

    [Fact]
    public async Task Staged_over_threshold_first_valid_entry_drops_the_scope_floor()
    {
        var persistence = Persistence(_enforced);
        Upsert(persistence, "song-a", Enumerable.Range(0, 8).Select(i => Entry($"team-{i}", 800 - (i * 10))).ToList());
        Upsert(persistence, "song-b", Enumerable.Range(0, 8).Select(i => Entry($"team-{i}", 800 - (i * 10))).ToList());
        var registered = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        persistence.PruneBandEntriesDetailed(registered, MaxValid, captureRetentionFloor: true, retentionFloorMarginRows: 0);
        Assert.Equal(2, ReadFloors(_enforced).Count);

        var filter = new BandRetentionFloorFilter(BandRetentionFloorMode.Enforce, registered, MaxValid);
        await using var spool = BandSpoolWriterFactory.Create(Logger(), persistence, retentionFloor: filter);
        spool.Enqueue("song-a", BandType, [Entry("team-0", 800, isOverThreshold: true), Entry("late-a", 100)]);
        spool.Enqueue("song-b", BandType, [Entry("late-b", 100)]);
        spool.Complete();

        Assert.Equal(1, filter.PrepareForFlush(_enforced.DataSource));
        spool.FlushAll();

        Assert.Equal("song-b", Assert.Single(ReadFloors(_enforced)).SongId);
        Assert.Contains(TeamKey("late-a"), ReadKeys(_enforced, "song-a"));
        Assert.DoesNotContain(TeamKey("late-b"), ReadKeys(_enforced, "song-b"));
        Assert.Equal(1, filter.BelowFloorRows);
    }

    [Fact]
    public async Task Unchecked_rows_from_an_earlier_flush_turn_the_floor_off_and_are_kept()
    {
        var persistence = Persistence(_enforced);
        Upsert(persistence, "song-a", Enumerable.Range(0, 8).Select(i => Entry($"team-{i}", 800 - (i * 10))).ToList());
        var registered = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        persistence.PruneBandEntriesDetailed(registered, MaxValid, captureRetentionFloor: true, retentionFloorMarginRows: 0);

        var first = new BandRetentionFloorFilter(BandRetentionFloorMode.Enforce, registered, MaxValid);
        await using (var spool = BandSpoolWriterFactory.Create(Logger(), persistence, retentionFloor: first))
        {
            spool.Enqueue("song-a", BandType, [Entry("late-1", 100)]);
            spool.Complete();
            first.PrepareForFlush(_enforced.DataSource);
            spool.FlushAll();
        }

        Assert.Equal(1, first.BelowFloorRows);

        // No prune ran, so the next flush must not discard the recorded row.
        var second = new BandRetentionFloorFilter(BandRetentionFloorMode.Enforce, registered, MaxValid);
        await using (var spool = BandSpoolWriterFactory.Create(Logger(), persistence, retentionFloor: second))
        {
            spool.Enqueue("song-a", BandType, [Entry("late-2", 100)]);
            spool.Complete();
            second.PrepareForFlush(_enforced.DataSource);
            spool.FlushAll();
        }

        Assert.True(second.PendingEvidence);
        Assert.False(second.IsActive);
        Assert.Contains(TeamKey("late-2"), ReadKeys(_enforced, "song-a"));
        var result = persistence.PruneBandEntriesDetailed(registered, MaxValid, captureRetentionFloor: true, retentionFloorMarginRows: 0);
        Assert.Equal((1L, 0L), (result.RetentionFloor!.ShadowRows, result.RetentionFloor.ShadowRowsKept));
    }

    [Fact]
    public void Over_threshold_recompute_invalidation_drops_matching_floors()
    {
        var persistence = Persistence(_enforced);
        Upsert(persistence, "song-a", Enumerable.Range(0, 8).Select(i => Entry($"team-{i}", 800 - (i * 10))).ToList());
        Upsert(persistence, "song-b", Enumerable.Range(0, 8).Select(i => Entry($"team-{i}", 800 - (i * 10))).ToList());
        persistence.PruneBandEntriesDetailed(new HashSet<string>(StringComparer.OrdinalIgnoreCase), MaxValid, captureRetentionFloor: true, retentionFloorMarginRows: 0);

        using var conn = _enforced.DataSource.OpenConnection();
        Assert.Equal(1, BandRetentionFloorSchema.InvalidateForSongs(conn, null, [BandType], ["song-a"]));
        Assert.Equal(1, BandRetentionFloorSchema.InvalidateForSongs(conn, null, [BandType], null));
        Assert.Empty(ReadFloors(_enforced));
    }

    private static List<BandLeaderboardEntry> GenerateBoard(Random random, string song, int count)
    {
        var entries = new List<BandLeaderboardEntry>();
        if (random.Next(2) == 0)
            entries.Add(Entry($"{song}-over-{random.Next(1000)}", 5_000 + random.Next(100), isOverThreshold: true, endTime: EndTime(random)));
        for (var i = 0; i < count; i++)
        {
            var name = random.Next(5) == 0 ? $"reg-1|{song}-init-{i}" : $"{song}-init-{i}";
            entries.Add(Entry(name, 100 + random.Next(60) * 10, endTime: EndTime(random)));
        }

        return entries;
    }

    private static List<BandLeaderboardEntry> GenerateStagedPage(
        Random random,
        string song,
        IReadOnlyCollection<string> existingKeys,
        int round)
    {
        var entries = new List<BandLeaderboardEntry>();
        foreach (var key in existingKeys)
        {
            var members = key.Split(':');
            var roll = random.Next(10);
            if (roll < 5)
                continue;
            var entry = EntryForMembers(members, 100 + random.Next(80) * 10, endTime: EndTime(random));
            if (roll == 9)
                entry.IsOverThreshold = true;
            entries.Add(entry);
        }

        for (var i = 0; i < 8; i++)
        {
            var name = random.Next(6) == 0 ? $"reg-1|{song}-r{round}-{i}" : $"{song}-r{round}-{i}";
            entries.Add(Entry(name, random.Next(4) == 0 ? 5_000 + random.Next(100) : 100 + random.Next(60) * 10, isOverThreshold: random.Next(8) == 0, endTime: EndTime(random)));
        }

        return entries;
    }

    private static int _endTimeSequence;

    // Prune breaks exact (score, end time) ties arbitrarily, so generated rows
    // keep that pair unique; otherwise the two databases could keep different
    // tied rows for reasons unrelated to the floor.
    private static string EndTime(Random random) =>
        new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)
            .AddMinutes(random.Next(10))
            .AddMilliseconds(Interlocked.Increment(ref _endTimeSequence))
            .ToString("O");

    private static BandLeaderboardPersistence Persistence(InMemoryMetaDatabase fixture) =>
        new(fixture.DataSource, Substitute.For<ILogger<BandLeaderboardPersistence>>());

    private static ILogger Logger() => Substitute.For<ILogger<BandLeaderboardPersistence>>();

    private static void Upsert(BandLeaderboardPersistence persistence, string songId, IReadOnlyList<BandLeaderboardEntry> entries)
    {
        using var conn = persistence.DataSource.OpenConnection();
        using var tx = conn.BeginTransaction();
        persistence.UpsertBandEntriesDirect(songId, BandType, entries, conn, tx, rebuildTeamMembership: false);
        tx.Commit();
    }

    private static string TeamKey(string name) => string.Join(':', Members(name));

    private static string[] Members(string name) =>
        (name.Contains('|') ? name.Split('|') : [$"{name}-x", $"{name}-y"])
            .OrderBy(static member => member, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static BandLeaderboardEntry Entry(string name, int score, bool isOverThreshold = false, string? endTime = null) =>
        EntryForMembers(Members(name), score, isOverThreshold, endTime);

    private static BandLeaderboardEntry EntryForMembers(string[] members, int score, bool isOverThreshold = false, string? endTime = null) => new()
    {
        TeamKey = string.Join(':', members),
        TeamMembers = members,
        InstrumentCombo = "0:1",
        Score = score,
        Accuracy = 950_000,
        IsFullCombo = true,
        Stars = 5,
        Difficulty = 3,
        Season = 1,
        Rank = 1,
        Percentile = 0.1,
        EndTime = endTime,
        Source = "test",
        IsOverThreshold = isOverThreshold,
        MemberStats =
        [
            new BandMemberStats { MemberIndex = 0, AccountId = members[0], InstrumentId = 0, Score = score / 2, Accuracy = 950_000, IsFullCombo = true, Stars = 5, Difficulty = 3 },
            new BandMemberStats { MemberIndex = 1, AccountId = members[1], InstrumentId = 1, Score = score / 2, Accuracy = 950_000, IsFullCombo = true, Stars = 5, Difficulty = 3 },
        ],
    };

    private static void Execute(InMemoryMetaDatabase fixture, string sql)
    {
        using var conn = fixture.DataSource.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static List<string> ReadKeys(InMemoryMetaDatabase fixture, string songId)
    {
        using var conn = fixture.DataSource.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT team_key FROM band_entries WHERE song_id = @songId AND band_type = 'Band_Duets' ORDER BY team_key";
        cmd.Parameters.AddWithValue("songId", songId);
        var keys = new List<string>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            keys.Add(reader.GetString(0));
        return keys;
    }

    private static List<(string SongId, int Rank, int Score, string FirstValidTeamKey)> ReadFloors(InMemoryMetaDatabase fixture)
    {
        using var conn = fixture.DataSource.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT song_id, floor_rank, floor_score, first_valid_team_key FROM band_retention_floor ORDER BY song_id";
        var floors = new List<(string, int, int, string)>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            floors.Add((reader.GetString(0), reader.GetInt32(1), reader.GetInt32(2), reader.GetString(3)));
        return floors;
    }

    private static string Snapshot(InMemoryMetaDatabase fixture)
    {
        using var conn = fixture.DataSource.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT
                (SELECT string_agg(format('%s|%s|%s|%s|%s|%s|%s|%s|%s|%s', song_id, team_key, instrument_combo, score,
                        base_score, instrument_bonus, overdrive_bonus, is_over_threshold, end_time, accuracy), E'\n'
                        ORDER BY song_id, team_key, instrument_combo)
                 FROM band_entries)
                || E'\n--\n' ||
                COALESCE((SELECT string_agg(format('%s|%s|%s|%s|%s', song_id, team_key, member_index, account_id, score), E'\n'
                        ORDER BY song_id, team_key, member_index)
                 FROM band_member_stats), '')
                || E'\n--\n' ||
                COALESCE((SELECT string_agg(format('%s|%s|%s', account_id, song_id, team_key), E'\n'
                        ORDER BY account_id, song_id, team_key)
                 FROM band_members), '')
            """;
        return (string)cmd.ExecuteScalar()!;
    }
}
