using FSTService.Persistence;
using FSTService.Scraping;
using FSTService.Tests.Helpers;
using NSubstitute;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace FSTService.Tests.Unit;

public sealed class MetaDatabaseRivalsTests : IDisposable
{
    private readonly InMemoryMetaDatabase _fixture = new();
    private MetaDatabase Db => _fixture.Db;

    public void Dispose() => _fixture.Dispose();

    // ═══ RivalsStatus lifecycle ═══════════════════════════════════

    [Fact]
    public void EnsureRivalsStatus_creates_pending_row()
    {
        Db.EnsureRivalsStatus("acct_1");
        var status = Db.GetRivalsStatus("acct_1");
        Assert.NotNull(status);
        Assert.Equal("pending", status.Status);
        Assert.Equal(0, status.AlgorithmVersion);
    }

    [Fact]
    public void EnsureRivalsStatus_idempotent()
    {
        Db.EnsureRivalsStatus("acct_1");
        Db.StartRivals("acct_1");
        Db.EnsureRivalsStatus("acct_1"); // should not reset to pending
        var status = Db.GetRivalsStatus("acct_1");
        Assert.Equal("in_progress", status!.Status);
    }

    [Fact]
    public void StartRivals_sets_in_progress()
    {
        Db.EnsureRivalsStatus("acct_1");
        Db.StartRivals("acct_1");
        var status = Db.GetRivalsStatus("acct_1");
        Assert.Equal("in_progress", status!.Status);
        Assert.NotNull(status.StartedAt);
    }

    [Fact]
    public void CompleteRivals_sets_complete_with_counts()
    {
        Db.EnsureRivalsStatus("acct_1");
        Db.StartRivals("acct_1");
        Db.CompleteRivals("acct_1", combosComputed: 7, rivalsFound: 42);
        var status = Db.GetRivalsStatus("acct_1");
        Assert.Equal("complete", status!.Status);
        Assert.Equal(7, status.CombosComputed);
        Assert.Equal(42, status.RivalsFound);
        Assert.Equal(RivalsAlgorithmVersion.SongRivals, status.AlgorithmVersion);
        Assert.NotNull(status.CompletedAt);
    }

    [Fact]
    public void FailRivals_sets_error_with_message()
    {
        Db.EnsureRivalsStatus("acct_1");
        Db.StartRivals("acct_1");
        Db.FailRivals("acct_1", "test error");
        var status = Db.GetRivalsStatus("acct_1");
        Assert.Equal("error", status!.Status);
        Assert.Equal("test error", status.ErrorMessage);
    }

    [Fact]
    public void GetPendingRivalsAccounts_returns_pending_and_in_progress()
    {
        Db.EnsureRivalsStatus("acct_1");
        Db.EnsureRivalsStatus("acct_2");
        Db.EnsureRivalsStatus("acct_3");
        Db.StartRivals("acct_2");
        Db.CompleteRivals("acct_3", 1, 1);

        var pending = Db.GetPendingRivalsAccounts();
        Assert.Equal(2, pending.Count);
        Assert.Contains("acct_1", pending);
        Assert.Contains("acct_2", pending);
        Assert.DoesNotContain("acct_3", pending);
    }

    // ═══ ReplaceRivalsData ═══════════════════════════════════════

    [Fact]
    public void ReplaceRivalsData_inserts_and_replaces()
    {
        var rivals = new List<UserRivalRow>
        {
            new() { UserId = "u1", RivalAccountId = "r1", InstrumentCombo = "Solo_Guitar",
                     Direction = "above", RivalScore = 42.0, AvgSignedDelta = -3.5,
                     SharedSongCount = 100, AheadCount = 60, BehindCount = 40, ComputedAt = "2026-01-01T00:00:00Z" },
        };
        var samples = new List<RivalSongSampleRow>
        {
            new() { UserId = "u1", RivalAccountId = "r1", Instrument = "Solo_Guitar",
                     SongId = "s1", UserRank = 10, RivalRank = 8, RankDelta = -2, UserScore = 9000, RivalScore = 9100 },
        };

        Db.ReplaceRivalsData("u1", rivals, samples);

        var stored = Db.GetUserRivals("u1");
        Assert.Single(stored);
        Assert.Equal("r1", stored[0].RivalAccountId);
        Assert.Equal(42.0, stored[0].RivalScore);

        var storedSamples = Db.GetRivalSongSamples("u1", "r1");
        Assert.Single(storedSamples);
        Assert.Equal(-2, storedSamples[0].RankDelta);

        // Replace with different data
        var newRivals = new List<UserRivalRow>
        {
            new() { UserId = "u1", RivalAccountId = "r2", InstrumentCombo = "Solo_Bass",
                     Direction = "below", RivalScore = 10.0, AvgSignedDelta = 5.0,
                     SharedSongCount = 50, AheadCount = 10, BehindCount = 40, ComputedAt = "2026-01-02T00:00:00Z" },
        };
        Db.ReplaceRivalsData("u1", newRivals, Array.Empty<RivalSongSampleRow>());

        stored = Db.GetUserRivals("u1");
        Assert.Single(stored);
        Assert.Equal("r2", stored[0].RivalAccountId);

        storedSamples = Db.GetRivalSongSamples("u1", "r1");
        Assert.Empty(storedSamples); // old samples deleted
    }

    [Fact]
    public void DirtyRivalSongs_round_trip_and_clear()
    {
        Db.UpsertDirtyRivalSongs(
        [
            new RivalDirtySongRow
            {
                AccountId = "u1",
                Instrument = "Solo_Guitar",
                SongId = "s1",
                DirtyReason = RivalsDirtyReason.SelfScoreChange,
                DetectedAt = "2026-01-01T00:00:00Z",
            },
            new RivalDirtySongRow
            {
                AccountId = "u1",
                Instrument = "Solo_Guitar",
                SongId = "s2",
                DirtyReason = RivalsDirtyReason.NeighborWindowChange,
                DetectedAt = "2026-01-01T00:00:01Z",
            },
        ]);

        var accounts = Db.GetDirtyRivalAccounts();
        Assert.Single(accounts);
        Assert.Equal("u1", accounts[0]);

        var dirtySongs = Db.GetDirtyRivalSongs("u1");
        Assert.Equal(2, dirtySongs.Count);

        Db.ClearDirtyRivalSongs("u1", "Solo_Guitar", ["s1"]);

        dirtySongs = Db.GetDirtyRivalSongs("u1");
        Assert.Single(dirtySongs);
        Assert.Equal("s2", dirtySongs[0].SongId);

        Db.ClearAllDirtyRivalSongs("u1");
        Assert.Empty(Db.GetDirtyRivalSongs("u1"));
    }

    [Fact]
    public void ReplaceRivalSelectionState_replaces_fingerprints_and_states()
    {
        Db.ReplaceRivalSelectionState(
            "u1",
            [
                new RivalSongFingerprintRow
                {
                    AccountId = "u1",
                    Instrument = "Solo_Guitar",
                    SongId = "s1",
                    UserRank = 42,
                    NeighborhoodSignature = "ABC123",
                    ComputedAt = "2026-01-01T00:00:00Z",
                },
            ],
            [
                new RivalInstrumentStateRow
                {
                    AccountId = "u1",
                    Instrument = "Solo_Guitar",
                    SongCount = 12,
                    IsEligible = true,
                    ComputedAt = "2026-01-01T00:00:00Z",
                },
            ]);

        var fingerprints = Db.GetRivalSongFingerprints("u1", "Solo_Guitar", ["s1"]);
        Assert.Single(fingerprints);
        Assert.Equal(42, fingerprints["s1"].UserRank);

        var states = Db.GetRivalInstrumentStates("u1");
        Assert.Single(states);
        Assert.True(states["Solo_Guitar"].IsEligible);

        Db.ReplaceRivalSelectionState(
            "u1",
            [
                new RivalSongFingerprintRow
                {
                    AccountId = "u1",
                    Instrument = "Solo_Bass",
                    SongId = "s2",
                    UserRank = 7,
                    NeighborhoodSignature = "DEF456",
                    ComputedAt = "2026-01-02T00:00:00Z",
                },
            ],
            [
                new RivalInstrumentStateRow
                {
                    AccountId = "u1",
                    Instrument = "Solo_Bass",
                    SongCount = 3,
                    IsEligible = false,
                    ComputedAt = "2026-01-02T00:00:00Z",
                },
            ]);

        Assert.Empty(Db.GetRivalSongFingerprints("u1", "Solo_Guitar", ["s1"]));
        fingerprints = Db.GetRivalSongFingerprints("u1", "Solo_Bass", ["s2"]);
        Assert.Single(fingerprints);
        Assert.Equal("DEF456", fingerprints["s2"].NeighborhoodSignature);

        states = Db.GetRivalInstrumentStates("u1");
        Assert.Single(states);
        Assert.False(states["Solo_Bass"].IsEligible);
    }

    // ═══ GetRivalCombos ══════════════════════════════════════════

    [Fact]
    public void GetRivalCombos_returns_grouped_counts()
    {
        var rivals = new List<UserRivalRow>
        {
            new() { UserId = "u1", RivalAccountId = "r1", InstrumentCombo = "Solo_Guitar",
                     Direction = "above", RivalScore = 10, AvgSignedDelta = -1,
                     SharedSongCount = 20, AheadCount = 15, BehindCount = 5, ComputedAt = "2026-01-01T00:00:00Z" },
            new() { UserId = "u1", RivalAccountId = "r2", InstrumentCombo = "Solo_Guitar",
                     Direction = "below", RivalScore = 8, AvgSignedDelta = 2,
                     SharedSongCount = 15, AheadCount = 5, BehindCount = 10, ComputedAt = "2026-01-01T00:00:00Z" },
            new() { UserId = "u1", RivalAccountId = "r1", InstrumentCombo = "Solo_Bass",
                     Direction = "above", RivalScore = 5, AvgSignedDelta = -2,
                     SharedSongCount = 10, AheadCount = 8, BehindCount = 2, ComputedAt = "2026-01-01T00:00:00Z" },
        };
        Db.ReplaceRivalsData("u1", rivals, Array.Empty<RivalSongSampleRow>());

        var combos = Db.GetRivalCombos("u1");
        Assert.Equal(2, combos.Count);
        var guitar = combos.First(c => c.InstrumentCombo == "Solo_Guitar");
        Assert.Equal(1, guitar.AboveCount);
        Assert.Equal(1, guitar.BelowCount);
    }

    [Fact]
    public void GetRivalCombos_and_GetUserRivals_filter_unsupported_multi_instrument_rows()
    {
        var rivals = new List<UserRivalRow>
        {
            new() { UserId = "u1", RivalAccountId = "r1", InstrumentCombo = "03",
                     Direction = "above", RivalScore = 10, AvgSignedDelta = -1,
                     SharedSongCount = 20, AheadCount = 15, BehindCount = 5, ComputedAt = "2026-01-01T00:00:00Z" },
            new() { UserId = "u1", RivalAccountId = "r2", InstrumentCombo = "c0",
                     Direction = "above", RivalScore = 8, AvgSignedDelta = -2,
                     SharedSongCount = 10, AheadCount = 7, BehindCount = 3, ComputedAt = "2026-01-01T00:00:00Z" },
        };
        Db.ReplaceRivalsData("u1", rivals, Array.Empty<RivalSongSampleRow>());

        var combos = Db.GetRivalCombos("u1");
        Assert.Single(combos);
        Assert.Equal("03", combos[0].InstrumentCombo);

        Assert.Single(Db.GetUserRivals("u1", "03"));
        Assert.Empty(Db.GetUserRivals("u1", "c0"));
    }

    // ═══ GetRivalSongSamples filters ════════════════════════════

    [Fact]
    public void GetRivalSongSamples_filters_by_instrument()
    {
        var samples = new List<RivalSongSampleRow>
        {
            new() { UserId = "u1", RivalAccountId = "r1", Instrument = "Solo_Guitar",
                     SongId = "s1", UserRank = 10, RivalRank = 8, RankDelta = -2 },
            new() { UserId = "u1", RivalAccountId = "r1", Instrument = "Solo_Bass",
                     SongId = "s2", UserRank = 20, RivalRank = 25, RankDelta = 5 },
        };
        Db.ReplaceRivalsData("u1", Array.Empty<UserRivalRow>(), samples);

        var guitarOnly = Db.GetRivalSongSamples("u1", "r1", "Solo_Guitar");
        Assert.Single(guitarOnly);
        Assert.Equal("s1", guitarOnly[0].SongId);

        var all = Db.GetRivalSongSamples("u1", "r1");
        Assert.Equal(2, all.Count);
    }

    // ═══ Cleanup on unregister ═══════════════════════════════════

    [Fact]
    public void UnregisterAccount_cleans_up_rivals_data()
    {
        Db.RegisterUser("dev1", "acct_1");
        Db.EnsureRivalsStatus("acct_1");

        var rivals = new List<UserRivalRow>
        {
            new() { UserId = "acct_1", RivalAccountId = "r1", InstrumentCombo = "Solo_Guitar",
                     Direction = "above", RivalScore = 10, AvgSignedDelta = -1,
                     SharedSongCount = 20, AheadCount = 15, BehindCount = 5, ComputedAt = "2026-01-01T00:00:00Z" },
        };
        var samples = new List<RivalSongSampleRow>
        {
            new() { UserId = "acct_1", RivalAccountId = "r1", Instrument = "Solo_Guitar",
                     SongId = "s1", UserRank = 10, RivalRank = 8, RankDelta = -2 },
        };
        Db.ReplaceRivalsData("acct_1", rivals, samples);
        Db.UpsertDirtyRivalSongs(
        [
            new RivalDirtySongRow
            {
                AccountId = "acct_1",
                Instrument = "Solo_Guitar",
                SongId = "s1",
                DirtyReason = RivalsDirtyReason.SelfScoreChange,
                DetectedAt = "2026-01-01T00:00:00Z",
            },
        ]);
        Db.ReplaceRivalSelectionState(
            "acct_1",
            [
                new RivalSongFingerprintRow
                {
                    AccountId = "acct_1",
                    Instrument = "Solo_Guitar",
                    SongId = "s1",
                    UserRank = 10,
                    NeighborhoodSignature = "ABC123",
                    ComputedAt = "2026-01-01T00:00:00Z",
                },
            ],
            [
                new RivalInstrumentStateRow
                {
                    AccountId = "acct_1",
                    Instrument = "Solo_Guitar",
                    SongCount = 1,
                    IsEligible = false,
                    ComputedAt = "2026-01-01T00:00:00Z",
                },
            ]);

        // Unregister last device → cascades to full cleanup
        Db.UnregisterUser("dev1", "acct_1");

        Assert.Empty(Db.GetUserRivals("acct_1"));
        Assert.Empty(Db.GetRivalSongSamples("acct_1", "r1"));
        Assert.Empty(Db.GetDirtyRivalSongs("acct_1"));
        Assert.Empty(Db.GetRivalInstrumentStates("acct_1"));
        Assert.Null(Db.GetRivalsStatus("acct_1"));
    }

    // ═══ ResetStaleRivals ═══════════════════════════════════════

    [Fact]
    public void ResetStaleRivals_resets_complete_with_zero_rivals()
    {
        Db.EnsureRivalsStatus("stale");
        Db.StartRivals("stale");
        Db.CompleteRivals("stale", 0, 0);

        var count = Db.ResetStaleRivals();
        Assert.Equal(1, count);

        var status = Db.GetRivalsStatus("stale");
        Assert.Equal("pending", status!.Status);
        Assert.Equal(0, status.CombosComputed);
        Assert.Equal(0, status.RivalsFound);
    }

    [Fact]
    public void ResetStaleRivals_does_not_touch_users_with_rivals()
    {
        Db.EnsureRivalsStatus("rich");
        Db.StartRivals("rich");
        Db.CompleteRivals("rich", 5, 20);

        var count = Db.ResetStaleRivals();
        Assert.Equal(0, count);

        var status = Db.GetRivalsStatus("rich");
        Assert.Equal("complete", status!.Status);
        Assert.Equal(20, status.RivalsFound);
    }

    [Fact]
    public void ResetStaleRivals_resets_complete_rows_with_old_algorithm_version()
    {
        using var conn = _fixture.DataSource.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO rivals_status (account_id, status, combos_computed, total_combos_to_compute, rivals_found, algorithm_version)
            VALUES ('old_version_user', 'complete', 3, 3, 12, 1)";
        cmd.ExecuteNonQuery();

        var count = Db.ResetStaleRivals();
        Assert.Equal(1, count);

        var status = Db.GetRivalsStatus("old_version_user");
        Assert.NotNull(status);
        Assert.Equal("pending", status!.Status);
        Assert.Equal(0, status.CombosComputed);
        Assert.Equal(0, status.RivalsFound);
        Assert.Equal(1, status.AlgorithmVersion);
    }

    [Fact]
    public void ResetStaleRivals_does_not_touch_pending_or_error()
    {
        Db.EnsureRivalsStatus("pending_user");
        Db.EnsureRivalsStatus("error_user");
        Db.StartRivals("error_user");
        Db.FailRivals("error_user", "test error");

        var count = Db.ResetStaleRivals();
        Assert.Equal(0, count);

        Assert.Equal("pending", Db.GetRivalsStatus("pending_user")!.Status);
        Assert.Equal("error", Db.GetRivalsStatus("error_user")!.Status);
    }
}
