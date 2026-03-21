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
    // UserComboRankings
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public void ReplaceUserComboRankings_StoresAndRetrieves()
    {
        var combos = new List<UserComboRankingDto>
        {
            new() { InstrumentCombo = "Solo_Guitar+Solo_Bass", ComboRating = 0.05, ComboRank = 42, TotalAccountsInCombo = 10000 },
        };
        Db.ReplaceUserComboRankings("p1", combos);

        var result = Db.GetUserComboRankings("p1");
        Assert.Single(result);
        Assert.Equal("Solo_Guitar+Solo_Bass", result[0].InstrumentCombo);
        Assert.Equal(42, result[0].ComboRank);
        Assert.Equal(10000, result[0].TotalAccountsInCombo);
    }

    [Fact]
    public void ReplaceUserComboRankings_ReplacesOld()
    {
        Db.ReplaceUserComboRankings("p1", [new UserComboRankingDto
            { InstrumentCombo = "old", ComboRating = 0.5, ComboRank = 1, TotalAccountsInCombo = 100 }]);

        Db.ReplaceUserComboRankings("p1", [new UserComboRankingDto
            { InstrumentCombo = "new", ComboRating = 0.1, ComboRank = 5, TotalAccountsInCombo = 200 }]);

        var result = Db.GetUserComboRankings("p1");
        Assert.Single(result);
        Assert.Equal("new", result[0].InstrumentCombo);
    }

    [Fact]
    public void GetUserComboRankings_EmptyForUnknown()
    {
        Assert.Empty(Db.GetUserComboRankings("nobody"));
    }

    // ═══════════════════════════════════════════════════════════
    // UserInstrumentPrefs
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public void SetAndGetUserInstrumentPrefs()
    {
        Db.SetUserInstrumentPrefs("p1", ["Solo_Guitar", "Solo_Bass"]);

        var prefs = Db.GetUserInstrumentPrefs("p1");
        Assert.NotNull(prefs);
        Assert.Equal(2, prefs.Count);
        Assert.Contains("Solo_Guitar", prefs);
        Assert.Contains("Solo_Bass", prefs);
    }

    [Fact]
    public void SetUserInstrumentPrefs_Upserts()
    {
        Db.SetUserInstrumentPrefs("p1", ["Solo_Guitar"]);
        Db.SetUserInstrumentPrefs("p1", ["Solo_Bass", "Solo_Drums"]);

        var prefs = Db.GetUserInstrumentPrefs("p1");
        Assert.NotNull(prefs);
        Assert.Equal(2, prefs.Count);
        Assert.DoesNotContain("Solo_Guitar", prefs);
    }

    [Fact]
    public void GetUserInstrumentPrefs_NullForUnknown()
    {
        Assert.Null(Db.GetUserInstrumentPrefs("nobody"));
    }

    [Fact]
    public void GetAllUserInstrumentPrefs_ReturnsAll()
    {
        Db.SetUserInstrumentPrefs("p1", ["Solo_Guitar"]);
        Db.SetUserInstrumentPrefs("p2", ["Solo_Bass", "Solo_Drums"]);

        var all = Db.GetAllUserInstrumentPrefs();
        Assert.Equal(2, all.Count);
        Assert.Single(all["p1"]);
        Assert.Equal(2, all["p2"].Count);
    }
}
