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

    private static void SeedEntries(InstrumentDatabase db, string songId,
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
        var guitarRivals = result.Rivals.Where(r => r.InstrumentCombo == "Solo_Guitar").ToList();
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
        Assert.Contains("Solo_Guitar", combos);
        Assert.DoesNotContain("Solo_Bass", combos);
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
        Assert.Contains("Solo_Guitar", combos);
        Assert.Contains("Solo_Bass", combos);
        Assert.Contains("Solo_Bass+Solo_Guitar", combos); // combo key is sorted
    }
}
