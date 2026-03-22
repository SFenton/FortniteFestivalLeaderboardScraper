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
        Assert.Equal(0.03, entries[0].GuitarAdjustedSkill);
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

        Db.SnapshotCompositeRankHistory(topN: 1); // Only top 1

        // p1 (rank 1) should be snapshotted, p2 (rank 2) should not be (unless additional)
    }

    [Fact]
    public void SnapshotCompositeRankHistory_IncludesAdditional()
    {
        Db.ReplaceCompositeRankings([
            new CompositeRankingDto { AccountId = "p1", InstrumentsPlayed = 1, TotalSongsPlayed = 10, CompositeRating = 0.1, CompositeRank = 1 },
            new CompositeRankingDto { AccountId = "p2", InstrumentsPlayed = 1, TotalSongsPlayed = 5, CompositeRating = 0.2, CompositeRank = 2 },
        ]);

        Db.SnapshotCompositeRankHistory(topN: 0, additionalAccountIds: new HashSet<string> { "p2" });
        // p2 should be included as additional even though top 0
    }

    // ═══════════════════════════════════════════════════════════
    // ComboLeaderboard
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public void ReplaceComboLeaderboard_StoresAndRetrieves()
    {
        var ranked = new List<(string AccountId, double ComboRating, int SongsPlayed)>
        {
            ("p1", 0.05, 100),
            ("p2", 0.10, 80),
        };
        Db.ReplaceComboLeaderboard("Solo_Bass+Solo_Guitar", ranked, 2);

        var (entries, total) = Db.GetComboLeaderboard("Solo_Bass+Solo_Guitar", 1, 50);
        Assert.Equal(2, total);
        Assert.Equal(2, entries.Count);
        Assert.Equal("p1", entries[0].AccountId);
        Assert.Equal(1, entries[0].Rank);
        Assert.Equal(0.05, entries[0].ComboRating, 4);
    }

    [Fact]
    public void ReplaceComboLeaderboard_ReplacesOld()
    {
        Db.ReplaceComboLeaderboard("Solo_Bass+Solo_Guitar",
            [("old", 0.5, 10)], 1);
        Db.ReplaceComboLeaderboard("Solo_Bass+Solo_Guitar",
            [("new", 0.1, 20)], 1);

        var (entries, total) = Db.GetComboLeaderboard("Solo_Bass+Solo_Guitar");
        Assert.Equal(1, total);
        Assert.Equal("new", entries[0].AccountId);
    }

    [Fact]
    public void GetComboRank_SingleAccount()
    {
        Db.ReplaceComboLeaderboard("Solo_Bass+Solo_Guitar",
            [("p1", 0.05, 100), ("p2", 0.10, 80)], 2);

        var entry = Db.GetComboRank("Solo_Bass+Solo_Guitar", "p2");
        Assert.NotNull(entry);
        Assert.Equal(2, entry.Rank);
        Assert.Equal("p2", entry.AccountId);
    }

    [Fact]
    public void GetComboRank_ReturnsNull_ForUnknown()
    {
        Assert.Null(Db.GetComboRank("Solo_Bass+Solo_Guitar", "nobody"));
    }

    [Fact]
    public void GetComboTotalAccounts_ReturnsCount()
    {
        Db.ReplaceComboLeaderboard("Solo_Bass+Solo_Guitar",
            [("p1", 0.05, 100)], 500_000);

        Assert.Equal(500_000, Db.GetComboTotalAccounts("Solo_Bass+Solo_Guitar"));
    }

    [Fact]
    public void GetComboTotalAccounts_ZeroForUnknown()
    {
        Assert.Equal(0, Db.GetComboTotalAccounts("nonexistent"));
    }

    [Fact]
    public void GetComboLeaderboard_Pagination()
    {
        var ranked = Enumerable.Range(0, 10)
            .Select(i => ($"p{i}", 0.01 * i, 100 - i))
            .ToList();
        Db.ReplaceComboLeaderboard("Solo_Bass+Solo_Guitar", ranked, 10);

        var (page1, total) = Db.GetComboLeaderboard("Solo_Bass+Solo_Guitar", 1, 3);
        var (page2, _) = Db.GetComboLeaderboard("Solo_Bass+Solo_Guitar", 2, 3);

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
}
