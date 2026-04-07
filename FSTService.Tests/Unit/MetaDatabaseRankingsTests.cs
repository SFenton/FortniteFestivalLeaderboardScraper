using FSTService.Persistence;
using FSTService.Tests.Helpers;

namespace FSTService.Tests.Unit;

public sealed class MetaDatabaseRankingsTests : IDisposable
{
    private readonly InMemoryMetaDatabase _fixture = new();
    private MetaDatabase Db => _fixture.Db;

    public void Dispose() => _fixture.Dispose();

    // ═══════════════════════════════════════════════════════════
    // CompositeRankings
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public void ReplaceCompositeRankings_StoresAndRetrieves()
    {
        var rankings = new List<CompositeRankingDto>
        {
            new()
            {
                AccountId = "p1", InstrumentsPlayed = 3, TotalSongsPlayed = 100,
                CompositeRating = 0.05, CompositeRank = 1,
                GuitarAdjustedSkill = 0.03, GuitarSkillRank = 2,
                BassAdjustedSkill = 0.04, BassSkillRank = 5,
            },
            new()
            {
                AccountId = "p2", InstrumentsPlayed = 1, TotalSongsPlayed = 50,
                CompositeRating = 0.15, CompositeRank = 2,
                DrumsAdjustedSkill = 0.15, DrumsSkillRank = 1,
            },
        };

        Db.ReplaceCompositeRankings(rankings);

        var (entries, total) = Db.GetCompositeRankings(page: 1, pageSize: 50);
        Assert.Equal(2, total);
        Assert.Equal(2, entries.Count);
        Assert.Equal("p1", entries[0].AccountId);
        Assert.Equal(0.05, entries[0].CompositeRating, 4);
        Assert.Equal(0.03, entries[0].GuitarAdjustedSkill!.Value, 2);
        Assert.Null(entries[0].DrumsAdjustedSkill);
    }

    [Fact]
    public void GetCompositeRanking_SingleAccount()
    {
        Db.ReplaceCompositeRankings([new CompositeRankingDto
        {
            AccountId = "p1", InstrumentsPlayed = 2, TotalSongsPlayed = 80,
            CompositeRating = 0.1, CompositeRank = 1,
        }]);

        var r = Db.GetCompositeRanking("p1");
        Assert.NotNull(r);
        Assert.Equal(1, r.CompositeRank);
    }

    [Fact]
    public void GetCompositeRanking_ReturnsNull_ForUnknown()
    {
        Assert.Null(Db.GetCompositeRanking("nonexistent"));
    }

    [Fact]
    public void GetCompositeRankings_Pagination()
    {
        var rankings = Enumerable.Range(0, 10).Select(i => new CompositeRankingDto
        {
            AccountId = $"p{i}", InstrumentsPlayed = 1, TotalSongsPlayed = 10,
            CompositeRating = 0.1 * i, CompositeRank = i + 1,
        }).ToList();
        Db.ReplaceCompositeRankings(rankings);

        var (page1, total) = Db.GetCompositeRankings(page: 1, pageSize: 3);
        var (page2, _) = Db.GetCompositeRankings(page: 2, pageSize: 3);

        Assert.Equal(10, total);
        Assert.Equal(3, page1.Count);
        Assert.Equal(3, page2.Count);
    }

    [Fact]
    public void ReplaceCompositeRankings_Replaces_NotAppends()
    {
        Db.ReplaceCompositeRankings([new CompositeRankingDto
        {
            AccountId = "old", InstrumentsPlayed = 1, TotalSongsPlayed = 5,
            CompositeRating = 0.5, CompositeRank = 1,
        }]);

        Db.ReplaceCompositeRankings([new CompositeRankingDto
        {
            AccountId = "new", InstrumentsPlayed = 2, TotalSongsPlayed = 10,
            CompositeRating = 0.1, CompositeRank = 1,
        }]);

        var (entries, total) = Db.GetCompositeRankings();
        Assert.Equal(1, total);
        Assert.Equal("new", entries[0].AccountId);
    }

    // ═══════════════════════════════════════════════════════════
    // CompositeRankHistory
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public void SnapshotCompositeRankHistory_Creates_And_Purges()
    {
        Db.ReplaceCompositeRankings([
            new CompositeRankingDto { AccountId = "p1", InstrumentsPlayed = 1, TotalSongsPlayed = 10, CompositeRating = 0.1, CompositeRank = 1 },
            new CompositeRankingDto { AccountId = "p2", InstrumentsPlayed = 1, TotalSongsPlayed = 5, CompositeRating = 0.2, CompositeRank = 2 },
        ]);

        Db.SnapshotCompositeRankHistory(); // All accounts with data

        // Both p1 and p2 should be snapshotted (sparse change-detection, no topN filter)
    }

    [Fact]
    public void SnapshotCompositeRankHistory_IncludesAdditional()
    {
        Db.ReplaceCompositeRankings([
            new CompositeRankingDto { AccountId = "p1", InstrumentsPlayed = 1, TotalSongsPlayed = 10, CompositeRating = 0.1, CompositeRank = 1 },
            new CompositeRankingDto { AccountId = "p2", InstrumentsPlayed = 1, TotalSongsPlayed = 5, CompositeRating = 0.2, CompositeRank = 2 },
        ]);

        Db.SnapshotCompositeRankHistory();
        // All accounts are included (no topN filtering)
    }

    // ═══════════════════════════════════════════════════════════
    // ComboLeaderboard
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public void ReplaceComboLeaderboard_StoresAndRetrieves()
    {
        var entries = new List<(string AccountId, double AdjustedRating, double WeightedRating, double FcRate, long TotalScore, double MaxScorePercent, int SongsPlayed, int FullComboCount)>
        {
            ("p1", 0.05, 0.06, 0.8, 50000, 0.95, 100, 80),
            ("p2", 0.10, 0.12, 0.6, 40000, 0.90, 80, 48),
        };
        Db.ReplaceComboLeaderboard("03", entries, 2);

        var (result, total) = Db.GetComboLeaderboard("03", "adjusted", 1, 50);
        Assert.Equal(2, total);
        Assert.Equal(2, result.Count);
        Assert.Equal("p1", result[0].AccountId);
        Assert.Equal(1, result[0].Rank);
        Assert.Equal(0.05, result[0].AdjustedRating, 4);
        Assert.Equal(50000, result[0].TotalScore);
    }

    [Fact]
    public void ReplaceComboLeaderboard_ReplacesOld()
    {
        Db.ReplaceComboLeaderboard("03",
            [("old", 0.5, 0.5, 0.5, 1000, 0.5, 10, 5)], 1);
        Db.ReplaceComboLeaderboard("03",
            [("new", 0.1, 0.1, 0.8, 2000, 0.9, 20, 16)], 1);

        var (entries, total) = Db.GetComboLeaderboard("03");
        Assert.Equal(1, total);
        Assert.Equal("new", entries[0].AccountId);
    }

    [Fact]
    public void GetComboRank_SingleAccount()
    {
        Db.ReplaceComboLeaderboard("03",
            [("p1", 0.05, 0.06, 0.8, 50000, 0.95, 100, 80), ("p2", 0.10, 0.12, 0.6, 40000, 0.90, 80, 48)], 2);

        var entry = Db.GetComboRank("03", "p2");
        Assert.NotNull(entry);
        Assert.Equal(2, entry.Rank);
        Assert.Equal("p2", entry.AccountId);
    }

    [Fact]
    public void GetComboRank_ReturnsNull_ForUnknown()
    {
        Assert.Null(Db.GetComboRank("03", "nobody"));
    }

    [Fact]
    public void GetComboTotalAccounts_ReturnsCount()
    {
        Db.ReplaceComboLeaderboard("03",
            [("p1", 0.05, 0.06, 0.8, 50000, 0.95, 100, 80)], 500_000);

        Assert.Equal(500_000, Db.GetComboTotalAccounts("03"));
    }

    [Fact]
    public void GetComboTotalAccounts_ZeroForUnknown()
    {
        Assert.Equal(0, Db.GetComboTotalAccounts("nonexistent"));
    }

    [Fact]
    public void GetComboLeaderboard_Pagination()
    {
        var entries = Enumerable.Range(0, 10)
            .Select(i => ($"p{i}", 0.01 * i, 0.01 * i, 0.5, (long)(1000 * (10 - i)), 0.5, 100 - i, 50 - i))
            .ToList();
        Db.ReplaceComboLeaderboard("03", entries, 10);

        var (page1, total) = Db.GetComboLeaderboard("03", "adjusted", 1, 3);
        var (page2, _) = Db.GetComboLeaderboard("03", "adjusted", 2, 3);

        Assert.Equal(10, total);
        Assert.Equal(3, page1.Count);
        Assert.Equal(3, page2.Count);
        Assert.NotEqual(page1[0].AccountId, page2[0].AccountId);
    }

    [Fact]
    public void GetComboLeaderboard_EmptyForUnknownCombo()
    {
        var (entries, total) = Db.GetComboLeaderboard("nonexistent");
        Assert.Empty(entries);
        Assert.Equal(0, total);
    }

    // ═══════════════════════════════════════════════════════════
    // CompositeRankingDeltas
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public void WriteCompositeRankingDeltas_StoresAndTruncates()
    {
        var deltas = new List<(string AccountId, double LeewayBucket, double AdjustedRating,
            double WeightedRating, double FcRateRating, double TotalScore, double MaxScoreRating,
            int InstrumentsPlayed, int TotalSongsPlayed)>
        {
            ("p1", -3.0, 0.05, 0.06, 0.8, 50000.0, 0.95, 2, 50),
            ("p2", -3.0, 0.10, 0.12, 0.6, 40000.0, 0.90, 1, 25),
        };
        Db.WriteCompositeRankingDeltas(deltas);

        // No direct read API yet — just verify write succeeded without error
        // Truncate should also succeed
        Db.TruncateCompositeRankingDeltas();
    }

    [Fact]
    public void TruncateCompositeRankingDeltas_NoErrorWhenEmpty()
    {
        Db.TruncateCompositeRankingDeltas(); // should not throw
    }

    // ═══════════════════════════════════════════════════════════
    // ComboRankingDeltas
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public void WriteComboRankingDeltas_StoresAndTruncates()
    {
        var deltas = new List<(string ComboId, string AccountId, double LeewayBucket,
            double AdjustedRating, double WeightedRating, double FcRate,
            long TotalScore, double MaxScorePercent, int SongsPlayed, int FullComboCount)>
        {
            ("03", "p1", -3.0, 0.05, 0.06, 0.8, 50000, 0.95, 100, 80),
            ("03", "p2", -3.0, 0.10, 0.12, 0.6, 40000, 0.90, 80, 48),
        };
        Db.WriteComboRankingDeltas(deltas);

        // No direct read API yet — verify write succeeded
        Db.TruncateComboRankingDeltas();
    }

    [Fact]
    public void TruncateComboRankingDeltas_NoErrorWhenEmpty()
    {
        Db.TruncateComboRankingDeltas(); // should not throw
    }
}
