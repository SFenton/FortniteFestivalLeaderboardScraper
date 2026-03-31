using FSTService.Persistence;
using FSTService.Scraping;
using FSTService.Tests.Helpers;
using NSubstitute;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace FSTService.Tests.Unit;

public sealed class RivalsCalculatorTests : IDisposable
{
    private readonly InMemoryMetaDatabase _metaFixture = new();
    private readonly string _dataDir;

    public RivalsCalculatorTests()
    {
        _dataDir = Path.Combine(Path.GetTempPath(), $"fst_rivals_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dataDir);
    }

    public void Dispose()
    {
        _metaFixture.Dispose();
        try { Directory.Delete(_dataDir, recursive: true); } catch { }
    }

    private GlobalLeaderboardPersistence CreatePersistence()
    {
        var loggerFactory = new NullLoggerFactory();
        var glp = new GlobalLeaderboardPersistence(
            _dataDir,
            _metaFixture.Db,
            loggerFactory,
            NullLogger<GlobalLeaderboardPersistence>.Instance);
        glp.Initialize();
        return glp;
    }

    private static RivalsCalculator CreateCalculator(GlobalLeaderboardPersistence persistence)
    {
        return new RivalsCalculator(persistence, NullLogger<RivalsCalculator>.Instance);
    }

    private static void SeedEntries(IInstrumentDatabase db, string songId,
        params (string AccountId, int Score)[] entries)
    {
        var result = new GlobalLeaderboardResult
        {
            SongId = songId,
            Instrument = db.Instrument,
            Entries = entries.Select(e => new LeaderboardEntry
            {
                AccountId = e.AccountId,
                Score = e.Score,
                Accuracy = 95,
                IsFullCombo = false,
                Stars = 5,
                Season = 3,
                Percentile = 99.0,
                EndTime = "2025-01-15T12:00:00Z",
            }).ToList(),
        };
        db.UpsertEntries(songId, result.Entries);
        db.RecomputeAllRanks();
    }

    /// <summary>
    /// Seed entries with explicit Source and ApiRank — for testing scenarios
    /// where backfill entries diverge from scrape-ranked entries.
    /// </summary>
    private static void SeedEntriesEx(IInstrumentDatabase db, string songId,
        params (string AccountId, int Score, string Source, int ApiRank)[] entries)
    {
        var list = entries.Select(e => new LeaderboardEntry
        {
            AccountId = e.AccountId,
            Score = e.Score,
            Rank = e.Source == "scrape" ? 0 : 0, // let RecomputeAllRanks handle scrape; backfill stays 0
            ApiRank = e.ApiRank,
            Source = e.Source,
            Accuracy = 95,
            IsFullCombo = false,
            Stars = 5,
            Season = 3,
            Percentile = 99.0,
            EndTime = "2025-01-15T12:00:00Z",
        }).ToList();

        db.UpsertEntries(songId, list);
        db.RecomputeAllRanks(); // only sets Rank for Source='scrape'
    }

    // ═══ Scoring formula ═════════════════════════════════════════

    [Fact]
    public void ScoringFormula_closer_rank_produces_higher_weight()
    {
        // log2(1000) / (1 + 1) = ~5.0  vs  log2(1000) / (1 + 50) = ~0.2
        var close = Math.Log2(1000) / (1 + 1);
        var far = Math.Log2(1000) / (1 + 50);
        Assert.True(close > far);
        Assert.True(close / far > 20); // ~25x difference
    }

    [Fact]
    public void ScoringFormula_larger_leaderboard_produces_higher_weight()
    {
        // log2(50000) / (1 + 5) ≈ 2.6  vs  log2(500) / (1 + 5) ≈ 1.5
        var large = Math.Log2(50000) / (1 + 5);
        var small = Math.Log2(500) / (1 + 5);
        Assert.True(large > small);
    }

    // ═══ Combo generation ════════════════════════════════════════

    [Fact]
    public void GenerateCombos_produces_all_subsets()
    {
        var instruments = new List<string> { "A", "B", "C" };
        var combos = RivalsCalculator.GenerateCombos(instruments);
        Assert.Equal(7, combos.Count); // 2^3 - 1

        // Check singles
        Assert.Contains(combos, c => c.Count == 1 && c[0] == "A");
        Assert.Contains(combos, c => c.Count == 1 && c[0] == "B");
        Assert.Contains(combos, c => c.Count == 1 && c[0] == "C");

        // Check pairs
        Assert.Contains(combos, c => c.Count == 2 && c.Contains("A") && c.Contains("B"));

        // Check triple
        Assert.Contains(combos, c => c.Count == 3);
    }

    [Fact]
    public void GenerateCombos_single_instrument_returns_one()
    {
        var combos = RivalsCalculator.GenerateCombos(new List<string> { "X" });
        Assert.Single(combos);
    }

    // ═══ SelectRivals ════════════════════════════════════════════

    [Fact]
    public void SelectRivals_splits_above_below_by_delta_sign()
    {
        var candidates = new[]
        {
            new RivalsCalculator.RivalCandidate { AccountId = "ahead1", WeightedScore = 50, SignedDeltaSum = -100, Appearances = 20, AheadCount = 15, BehindCount = 5 },
            new RivalsCalculator.RivalCandidate { AccountId = "behind1", WeightedScore = 40, SignedDeltaSum = 80, Appearances = 20, AheadCount = 5, BehindCount = 15 },
        };

        var output = new List<UserRivalRow>();
        RivalsCalculator.SelectRivals("user", "Solo_Guitar", candidates, 5, 10, "now", output);

        Assert.Equal(2, output.Count);
        var above = output.Single(r => r.Direction == "above");
        var below = output.Single(r => r.Direction == "below");
        Assert.Equal("ahead1", above.RivalAccountId);
        Assert.Equal("behind1", below.RivalAccountId);
    }

    [Fact]
    public void SelectRivals_filters_by_min_shared_songs()
    {
        var candidates = new[]
        {
            new RivalsCalculator.RivalCandidate { AccountId = "few", WeightedScore = 50, SignedDeltaSum = -10, Appearances = 3, AheadCount = 2, BehindCount = 1 },
            new RivalsCalculator.RivalCandidate { AccountId = "enough", WeightedScore = 30, SignedDeltaSum = -20, Appearances = 10, AheadCount = 8, BehindCount = 2 },
        };

        var output = new List<UserRivalRow>();
        RivalsCalculator.SelectRivals("user", "Solo_Guitar", candidates, 5, 10, "now", output);

        Assert.Single(output);
        Assert.Equal("enough", output[0].RivalAccountId);
    }

    [Fact]
    public void SelectRivals_limits_to_N_per_direction()
    {
        var candidates = Enumerable.Range(0, 30).Select(i =>
            new RivalsCalculator.RivalCandidate
            {
                AccountId = $"rival_{i}",
                WeightedScore = 100 - i,
                SignedDeltaSum = -10,
                Appearances = 20,
                AheadCount = 15,
                BehindCount = 5,
            }).ToList();

        var output = new List<UserRivalRow>();
        RivalsCalculator.SelectRivals("user", "Solo_Guitar", candidates, 5, 10, "now", output);

        Assert.Equal(10, output.Count); // all above (negative delta), capped at 10
        Assert.Equal("rival_0", output[0].RivalAccountId); // highest score first
    }

    // ═══ IntersectCandidates ═════════════════════════════════════

    [Fact]
    public void IntersectCandidates_combines_scores_across_instruments()
    {
        var pool1 = new Dictionary<string, RivalsCalculator.RivalCandidate>(StringComparer.OrdinalIgnoreCase)
        {
            ["shared"] = new() { AccountId = "shared", WeightedScore = 20, SignedDeltaSum = -10, Appearances = 10, AheadCount = 8, BehindCount = 2 },
            ["only1"] = new() { AccountId = "only1", WeightedScore = 30, SignedDeltaSum = -5, Appearances = 10, AheadCount = 7, BehindCount = 3 },
        };
        var pool2 = new Dictionary<string, RivalsCalculator.RivalCandidate>(StringComparer.OrdinalIgnoreCase)
        {
            ["shared"] = new() { AccountId = "shared", WeightedScore = 15, SignedDeltaSum = -5, Appearances = 10, AheadCount = 6, BehindCount = 4 },
            ["only2"] = new() { AccountId = "only2", WeightedScore = 25, SignedDeltaSum = 5, Appearances = 10, AheadCount = 3, BehindCount = 7 },
        };

        var allCandidates = new Dictionary<string, Dictionary<string, RivalsCalculator.RivalCandidate>>(StringComparer.OrdinalIgnoreCase)
        {
            ["Solo_Guitar"] = pool1,
            ["Solo_Bass"] = pool2,
        };

        var result = RivalsCalculator.IntersectCandidates(
            new List<string> { "Solo_Guitar", "Solo_Bass" }, allCandidates);

        Assert.Single(result); // only "shared" is in both
        Assert.Equal(35.0, result["shared"].WeightedScore); // 20 + 15
    }

    // ═══ Full computation (integration-style) ════════════════════

    [Fact]
    public void ComputeRivals_returns_empty_when_user_has_too_few_songs()
    {
        var persistence = CreatePersistence();
        var db = persistence.GetOrCreateInstrumentDb("Solo_Guitar");

        // Only 5 songs — below threshold of 10
        for (int i = 0; i < 5; i++)
        {
            SeedEntries(db, $"song_{i}",
                ("user1", 1000 - i * 10),
                ("rival1", 990 - i * 10));
        }

        var calc = CreateCalculator(persistence);
        var result = calc.ComputeRivals("user1");

        Assert.Empty(result.Rivals);
        Assert.Equal(0, result.CombosComputed);
    }

    [Fact]
    public void ComputeRivals_finds_nearby_rival()
    {
        var persistence = CreatePersistence();
        var db = persistence.GetOrCreateInstrumentDb("Solo_Guitar");

        // 15 songs, user1 and rival1 always close, rival2 always far
        for (int i = 0; i < 15; i++)
        {
            var entries = new List<(string, int)>
            {
                ("user1", 10000 - i * 100),
                ("rival1", 10000 - i * 100 - 5), // 5 points behind = rank 2
                ("rival2", 10000 - i * 100 - 5000), // very far = large rank gap
            };

            // Add some filler to make entry count > 1
            for (int j = 0; j < 20; j++)
                entries.Add(($"filler_{j}", 10000 - i * 100 - 200 - j * 50));

            SeedEntries(db, $"song_{i}", entries.ToArray());
        }

        var calc = CreateCalculator(persistence);
        var result = calc.ComputeRivals("user1");

        Assert.NotEmpty(result.Rivals);
        Assert.True(result.CombosComputed > 0);

        // rival1 should be found (close), rival2 likely not (far or lower score)
        var guitarComboId = ComboIds.FromInstruments(["Solo_Guitar"]);
        var guitarRivals = result.Rivals.Where(r => r.InstrumentCombo == guitarComboId).ToList();
        Assert.Contains(guitarRivals, r => r.RivalAccountId == "rival1");
    }

    [Fact]
    public void ComputeRivals_produces_samples_sorted_by_abs_delta()
    {
        var persistence = CreatePersistence();
        var db = persistence.GetOrCreateInstrumentDb("Solo_Guitar");

        // 12 songs with varying proximity
        for (int i = 0; i < 12; i++)
        {
            var entries = new List<(string, int)>
            {
                ("user1", 10000 - i * 100),
                ("rival1", 10000 - i * 100 - (i + 1) * 10), // increasing gap
            };
            for (int j = 0; j < 20; j++)
                entries.Add(($"filler_{j}", 10000 - i * 100 - 200 - j * 50));

            SeedEntries(db, $"song_{i}", entries.ToArray());
        }

        var calc = CreateCalculator(persistence);
        var result = calc.ComputeRivals("user1");

        var samples = result.Samples
            .Where(s => s.RivalAccountId == "rival1" && s.Instrument == "Solo_Guitar")
            .ToList();

        // Should be sorted by |delta| ascending
        for (int i = 1; i < samples.Count; i++)
        {
            Assert.True(Math.Abs(samples[i].RankDelta) >= Math.Abs(samples[i - 1].RankDelta));
        }
    }

    [Fact]
    public void ComputeRivals_respects_dirty_instruments_filter()
    {
        var persistence = CreatePersistence();
        var guitarDb = persistence.GetOrCreateInstrumentDb("Solo_Guitar");
        var bassDb = persistence.GetOrCreateInstrumentDb("Solo_Bass");

        // 15 songs on both instruments
        for (int i = 0; i < 15; i++)
        {
            var entries = new (string, int)[]
            {
                ("user1", 10000 - i * 100),
                ("rival1", 10000 - i * 100 - 5),
            };
            for (int j = 0; j < 10; j++)
            {
                entries = entries.Append(($"filler_{j}", 8000 - j * 100)).ToArray();
            }
            SeedEntries(guitarDb, $"song_{i}", entries);
            SeedEntries(bassDb, $"song_{i}", entries);
        }

        var calc = CreateCalculator(persistence);

        // Only scan Guitar
        var dirtyInstruments = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Solo_Guitar" };
        var result = calc.ComputeRivals("user1", dirtyInstruments);

        // Should only have Guitar combos, not Bass
        var combos = result.Rivals.Select(r => r.InstrumentCombo).Distinct().ToList();
        Assert.Contains(ComboIds.FromInstruments(["Solo_Guitar"]), combos);
        Assert.DoesNotContain(ComboIds.FromInstruments(["Solo_Bass"]), combos);
    }

    // ═══ Multi-instrument combo ═════════════════════════════════

    [Fact]
    public void ComputeRivals_generates_multi_instrument_combos()
    {
        var persistence = CreatePersistence();
        var guitarDb = persistence.GetOrCreateInstrumentDb("Solo_Guitar");
        var bassDb = persistence.GetOrCreateInstrumentDb("Solo_Bass");

        for (int i = 0; i < 15; i++)
        {
            var entries = new List<(string, int)>
            {
                ("user1", 10000 - i * 100),
                ("rival_shared", 10000 - i * 100 - 5),
            };
            for (int j = 0; j < 20; j++)
                entries.Add(($"filler_{j}", 8000 - j * 100));

            SeedEntries(guitarDb, $"song_{i}", entries.ToArray());
            SeedEntries(bassDb, $"song_{i}", entries.ToArray());
        }

        var calc = CreateCalculator(persistence);
        var result = calc.ComputeRivals("user1");

        var combos = result.Rivals.Select(r => r.InstrumentCombo).Distinct().ToList();
        Assert.Contains(ComboIds.FromInstruments(["Solo_Guitar"]), combos);
        Assert.Contains(ComboIds.FromInstruments(["Solo_Bass"]), combos);
        Assert.Contains(ComboIds.FromInstruments(["Solo_Guitar", "Solo_Bass"]), combos); // combo ID for Guitar+Bass
    }

    // ═══ ComputeSongGaps ═════════════════════════════════════════

    [Fact]
    public void ComputeSongGaps_returns_empty_when_both_play_same_songs()
    {
        var persistence = CreatePersistence();
        var db = persistence.GetOrCreateInstrumentDb("Solo_Guitar");

        for (int i = 0; i < 5; i++)
        {
            SeedEntries(db, $"song_{i}",
                ("user1", 10000 - i * 100),
                ("rival1", 9000 - i * 100));
        }

        var calc = CreateCalculator(persistence);
        var gaps = calc.ComputeSongGaps("user1", "rival1", new[] { "Solo_Guitar" });

        Assert.Empty(gaps.SongsToCompete);
        Assert.Empty(gaps.YourExclusives);
    }

    [Fact]
    public void ComputeSongGaps_finds_rival_only_songs()
    {
        var persistence = CreatePersistence();
        var db = persistence.GetOrCreateInstrumentDb("Solo_Guitar");

        // Shared songs
        SeedEntries(db, "shared_1", ("user1", 10000), ("rival1", 9000));
        SeedEntries(db, "shared_2", ("user1", 9500), ("rival1", 8500));

        // Rival-only songs
        SeedEntries(db, "rival_only_1", ("rival1", 8000));
        SeedEntries(db, "rival_only_2", ("rival1", 7000));

        var calc = CreateCalculator(persistence);
        var gaps = calc.ComputeSongGaps("user1", "rival1", new[] { "Solo_Guitar" });

        Assert.Equal(2, gaps.SongsToCompete.Count);
        Assert.Contains(gaps.SongsToCompete, g => g.SongId == "rival_only_1");
        Assert.Contains(gaps.SongsToCompete, g => g.SongId == "rival_only_2");
        Assert.Empty(gaps.YourExclusives);
    }

    [Fact]
    public void ComputeSongGaps_finds_user_only_songs()
    {
        var persistence = CreatePersistence();
        var db = persistence.GetOrCreateInstrumentDb("Solo_Guitar");

        // Shared songs
        SeedEntries(db, "shared_1", ("user1", 10000), ("rival1", 9000));

        // User-only songs
        SeedEntries(db, "user_only_1", ("user1", 9000));
        SeedEntries(db, "user_only_2", ("user1", 8500));

        var calc = CreateCalculator(persistence);
        var gaps = calc.ComputeSongGaps("user1", "rival1", new[] { "Solo_Guitar" });

        Assert.Empty(gaps.SongsToCompete);
        Assert.Equal(2, gaps.YourExclusives.Count);
        Assert.Contains(gaps.YourExclusives, g => g.SongId == "user_only_1");
        Assert.Contains(gaps.YourExclusives, g => g.SongId == "user_only_2");
    }

    [Fact]
    public void ComputeSongGaps_mixed_gaps_both_directions()
    {
        var persistence = CreatePersistence();
        var db = persistence.GetOrCreateInstrumentDb("Solo_Guitar");

        SeedEntries(db, "shared", ("user1", 10000), ("rival1", 9000));
        SeedEntries(db, "rival_only", ("rival1", 7000));
        SeedEntries(db, "user_only", ("user1", 8000));

        var calc = CreateCalculator(persistence);
        var gaps = calc.ComputeSongGaps("user1", "rival1", new[] { "Solo_Guitar" });

        Assert.Single(gaps.SongsToCompete);
        Assert.Equal("rival_only", gaps.SongsToCompete[0].SongId);
        Assert.Single(gaps.YourExclusives);
        Assert.Equal("user_only", gaps.YourExclusives[0].SongId);
    }

    [Fact]
    public void ComputeSongGaps_caps_at_limit()
    {
        var persistence = CreatePersistence();
        var db = persistence.GetOrCreateInstrumentDb("Solo_Guitar");

        // Create 120 rival-only songs (over the 100 cap)
        for (int i = 0; i < 120; i++)
        {
            SeedEntries(db, $"rival_song_{i}", ("rival1", 10000 - i * 10));
        }

        var calc = CreateCalculator(persistence);
        var gaps = calc.ComputeSongGaps("user1", "rival1", new[] { "Solo_Guitar" });

        Assert.Equal(100, gaps.SongsToCompete.Count);
        Assert.Empty(gaps.YourExclusives);
    }

    [Fact]
    public void ComputeSongGaps_sorts_by_rank_ascending()
    {
        var persistence = CreatePersistence();
        var db = persistence.GetOrCreateInstrumentDb("Solo_Guitar");

        // Rival-only songs with varying ranks — seed with different scores
        // Higher score = better rank after RecomputeAllRanks
        SeedEntries(db, "low_rank", ("rival1", 50000));  // best score = rank 1
        SeedEntries(db, "mid_rank", ("rival1", 30000));  // rank 2
        SeedEntries(db, "high_rank", ("rival1", 10000)); // rank 3
        db.RecomputeAllRanks();

        var calc = CreateCalculator(persistence);
        var gaps = calc.ComputeSongGaps("user1", "rival1", new[] { "Solo_Guitar" });

        Assert.Equal(3, gaps.SongsToCompete.Count);
        // Should be sorted by rank ascending — best rank first
        Assert.True(gaps.SongsToCompete[0].Rank <= gaps.SongsToCompete[1].Rank
                  || gaps.SongsToCompete[0].Rank == 0); // 0 = unranked, goes to end
    }

    [Fact]
    public void ComputeSongGaps_multi_instrument_combo()
    {
        var persistence = CreatePersistence();
        var guitarDb = persistence.GetOrCreateInstrumentDb("Solo_Guitar");
        var bassDb = persistence.GetOrCreateInstrumentDb("Solo_Bass");

        // Guitar: rival has extra song
        SeedEntries(guitarDb, "shared", ("user1", 10000), ("rival1", 9000));
        SeedEntries(guitarDb, "guitar_rival_only", ("rival1", 8000));

        // Bass: user has extra song
        SeedEntries(bassDb, "shared", ("user1", 10000), ("rival1", 9000));
        SeedEntries(bassDb, "bass_user_only", ("user1", 7000));

        var calc = CreateCalculator(persistence);
        var gaps = calc.ComputeSongGaps("user1", "rival1", new[] { "Solo_Guitar", "Solo_Bass" });

        Assert.Single(gaps.SongsToCompete);
        Assert.Equal("Solo_Guitar", gaps.SongsToCompete[0].Instrument);
        Assert.Equal("guitar_rival_only", gaps.SongsToCompete[0].SongId);

        Assert.Single(gaps.YourExclusives);
        Assert.Equal("Solo_Bass", gaps.YourExclusives[0].Instrument);
        Assert.Equal("bass_user_only", gaps.YourExclusives[0].SongId);
    }

    // ═══ Diagnostics ═════════════════════════════════════════════

    [Fact]
    public void GetDiagnostics_returns_empty_instruments_when_user_has_no_scores()
    {
        var persistence = CreatePersistence();
        _ = persistence.GetOrCreateInstrumentDb("Solo_Guitar");
        var calc = CreateCalculator(persistence);

        var diag = calc.GetDiagnostics("nonexistent_user");
        Assert.Empty(diag.Instruments);
    }

    [Fact]
    public void GetDiagnostics_reports_rank_breakdown_for_scrape_entries()
    {
        var persistence = CreatePersistence();
        var db = persistence.GetOrCreateInstrumentDb("Solo_Guitar");

        // Seed 12 scrape entries — RecomputeAllRanks will set Rank, ApiRank stays 0
        for (int i = 0; i < 12; i++)
        {
            var entries = new List<(string, int)> { ("user1", 10000 - i * 100) };
            for (int j = 0; j < 5; j++)
                entries.Add(($"filler_{j}", 8000 - j * 100 - i * 10));
            SeedEntries(db, $"song_{i}", entries.ToArray());
        }

        var calc = CreateCalculator(persistence);
        var diag = calc.GetDiagnostics("user1");

        Assert.Single(diag.Instruments);
        var inst = diag.Instruments[0];
        Assert.Equal("Solo_Guitar", inst.Instrument);
        Assert.Equal(12, inst.TotalSongs);
        Assert.True(inst.MeetsMinimum);
        Assert.Equal(12, inst.RankedSongs);
        // Scrape entries: Rank > 0, ApiRank = 0 → all should be "RankOnly"
        Assert.Equal(12, inst.RankOnly);
        Assert.Equal(0, inst.ApiRankOnly);
        Assert.Equal(0, inst.BothZero);
        Assert.Equal(0, inst.BothSet);
    }

    [Fact]
    public void GetDiagnostics_reports_apiRankOnly_for_backfill_entries()
    {
        var persistence = CreatePersistence();
        var db = persistence.GetOrCreateInstrumentDb("Solo_Guitar");

        // Seed 12 backfill entries — Rank stays 0 after RecomputeAllRanks, ApiRank is set
        for (int i = 0; i < 12; i++)
        {
            SeedEntriesEx(db, $"song_{i}",
                ("user1", 10000 - i * 100, "backfill", 500 + i));
        }

        var calc = CreateCalculator(persistence);
        var diag = calc.GetDiagnostics("user1");

        Assert.Single(diag.Instruments);
        var inst = diag.Instruments[0];
        Assert.Equal(12, inst.TotalSongs);
        Assert.True(inst.MeetsMinimum);
        Assert.Equal(12, inst.RankedSongs); // effectiveRank = ApiRank > 0
        // Backfill: Rank = 0, ApiRank > 0 → all "ApiRankOnly"
        Assert.Equal(0, inst.RankOnly);
        Assert.Equal(12, inst.ApiRankOnly);
        Assert.Equal(0, inst.BothZero);
    }

    [Fact]
    public void GetDiagnostics_probe_returns_neighbor_count()
    {
        var persistence = CreatePersistence();
        var db = persistence.GetOrCreateInstrumentDb("Solo_Guitar");

        // Seed 12 songs with user + filler. All scrape-based so Rank is set.
        for (int i = 0; i < 12; i++)
        {
            var entries = new List<(string, int)> { ("user1", 10000 - i * 100) };
            for (int j = 0; j < 10; j++)
                entries.Add(($"filler_{j}", 10000 - i * 100 - (j + 1) * 50));
            SeedEntries(db, $"song_{i}", entries.ToArray());
        }

        var calc = CreateCalculator(persistence);
        var diag = calc.GetDiagnostics("user1");

        var inst = diag.Instruments[0];
        Assert.NotNull(inst.Probe);
        Assert.True(inst.Probe!.NeighborsFound > 0,
            "Probe should find neighbors for scrape entries with dense ranks");
        Assert.True(inst.Probe.EffectiveRank > 0);
    }

    [Fact]
    public void GetDiagnostics_probe_finds_zero_neighbors_for_backfill_only()
    {
        // This captures the suspected bug: backfill entries have ApiRank set but Rank = 0.
        // GetNeighborhood queries Rank, so it won't find any neighbors for the user's
        // ApiRank-based effectiveRank.
        var persistence = CreatePersistence();
        var db = persistence.GetOrCreateInstrumentDb("Solo_Guitar");

        // Seed user as backfill (ApiRank = 500, Rank = 0)
        // Seed neighbors as scrape (Rank dense 1-10, ApiRank = 0)
        for (int i = 0; i < 12; i++)
        {
            // Scrape entries: these will get dense Rank from RecomputeAllRanks
            var scrapeEntries = new List<(string AccountId, int Score, string Source, int ApiRank)>();
            for (int j = 0; j < 10; j++)
                scrapeEntries.Add(($"filler_{j}", 8000 - j * 100 - i * 10, "scrape", 0));

            // User is backfill: ApiRank = 500+i, Rank will stay 0
            scrapeEntries.Add(("user1", 10000 - i * 100, "backfill", 500 + i));

            SeedEntriesEx(db, $"song_{i}", scrapeEntries.ToArray());
        }

        var calc = CreateCalculator(persistence);
        var diag = calc.GetDiagnostics("user1");

        var inst = diag.Instruments[0];
        Assert.NotNull(inst.Probe);
        // The probe uses effectiveRank = ApiRank (e.g. 506 for median) but
        // GetNeighborhood queries Rank column where filler entries are 1-10.
        // So the probe will find 0 neighbors.
        Assert.Equal(0, inst.Probe!.NeighborsFound);
        Assert.True(inst.Probe.EffectiveRank >= 500,
            "EffectiveRank should use ApiRank since Rank = 0");
    }

    [Fact]
    public void GetDiagnostics_detects_mismatch_between_rank_and_apiRank()
    {
        var persistence = CreatePersistence();
        var db = persistence.GetOrCreateInstrumentDb("Solo_Guitar");

        // Entry that is both scraped (gets Rank from RecomputeAllRanks) AND has ApiRank from backfill.
        // Since the entry has Source='scrape', RecomputeAllRanks will set Rank.
        // ApiRank is set explicitly to something different.
        for (int i = 0; i < 12; i++)
        {
            var entries = new List<(string AccountId, int Score, string Source, int ApiRank)>
            {
                // User has scrape source so Rank will be set. ApiRank = 999 is different.
                ("user1", 10000 - i * 100, "scrape", 999),
            };
            for (int j = 0; j < 5; j++)
                entries.Add(($"filler_{j}", 8000 - j * 100 - i * 10, "scrape", 0));
            SeedEntriesEx(db, $"song_{i}", entries.ToArray());
        }

        var calc = CreateCalculator(persistence);
        var diag = calc.GetDiagnostics("user1");

        var inst = diag.Instruments[0];
        Assert.Equal(12, inst.BothSet); // both Rank and ApiRank > 0
        Assert.True(inst.Mismatch > 0, "Rank and ApiRank should differ for user1");
    }

    [Fact]
    public void GetDiagnostics_sample_entries_limited_to_5()
    {
        var persistence = CreatePersistence();
        var db = persistence.GetOrCreateInstrumentDb("Solo_Guitar");

        for (int i = 0; i < 20; i++)
            SeedEntries(db, $"song_{i}", ("user1", 10000 - i * 100), ("filler", 5000));

        var calc = CreateCalculator(persistence);
        var diag = calc.GetDiagnostics("user1");

        Assert.Single(diag.Instruments);
        Assert.Equal(5, diag.Instruments[0].SampleEntries.Count);
    }
}
