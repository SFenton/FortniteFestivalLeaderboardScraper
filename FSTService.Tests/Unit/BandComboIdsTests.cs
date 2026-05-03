using FSTService.Persistence;
using FSTService.Scraping;

namespace FSTService.Tests.Unit;

public sealed class BandComboIdsTests
{
    [Fact]
    public void FromEpicRawCombo_NormalizesToCanonicalServerOrder()
    {
        var comboId = BandComboIds.FromEpicRawCombo("0:2:3");

        Assert.Equal("Solo_Guitar+Solo_Drums+Solo_Vocals", comboId);
    }

    [Fact]
    public void FromInstruments_PreservesRepeatedInstruments()
    {
        var comboId = BandComboIds.FromInstruments(["Solo_Guitar", "Solo_Guitar", "Solo_Bass"]);

        Assert.Equal("Solo_Guitar+Solo_Guitar+Solo_Bass", comboId);
    }

    [Fact]
    public void TryNormalizeComboParam_AcceptsNormalizedInstrumentLists()
    {
        var success = BandComboIds.TryNormalizeComboParam("Solo_Bass+Solo_Guitar", out var comboId);

        Assert.True(success);
        Assert.Equal("Solo_Guitar+Solo_Bass", comboId);
    }

    [Fact]
    public void TryNormalizeForBandType_AcceptsMatchingComboSize()
    {
        var (comboId, error) = BandComboIds.TryNormalizeForBandType("Band_Duets", "Solo_Bass+Solo_Guitar");

        Assert.Null(error);
        Assert.Equal("Solo_Guitar+Solo_Bass", comboId);
    }

    [Fact]
    public void TryNormalizeForBandType_RejectsMismatchedComboSize()
    {
        var (comboId, error) = BandComboIds.TryNormalizeForBandType("Band_Duets", "Solo_Guitar+Solo_Bass+Solo_Drums");

        Assert.Null(comboId);
        Assert.Equal("Combo size does not match band type Band_Duets.", error);
    }

    [Fact]
    public void ToEpicRawComboCandidates_PreservesDuplicatesAndIncludesReorderedRawCombos()
    {
        var leadBass = BandComboIds.ToEpicRawComboCandidates("Solo_Guitar+Solo_Bass");
        var doubleLead = BandComboIds.ToEpicRawComboCandidates("Solo_Guitar+Solo_Guitar");

        Assert.Equal(["0:1", "1:0"], leadBass);
        Assert.Equal(["0:0"], doubleLead);
    }

    [Fact]
    public void CurrentProjectionScopeTracker_AddsOverallAndMatchingComboScopes()
    {
        var scopes = new HashSet<BandCurrentProjectionScopeKey>();

        BandCurrentProjectionScopeTracker.AddScopes(scopes, "song-1", "Band_Duets", "1:0");

        Assert.Contains(scopes, scope =>
            scope.SongId == "song-1"
            && scope.BandType == "Band_Duets"
            && scope.RankingScope == "overall"
            && scope.ScopeComboId == string.Empty);
        Assert.Contains(scopes, scope =>
            scope.SongId == "song-1"
            && scope.BandType == "Band_Duets"
            && scope.RankingScope == "combo"
            && scope.ScopeComboId == "Solo_Guitar+Solo_Bass");
    }

    [Fact]
    public void CurrentProjectionScopeTracker_IgnoresMismatchedComboScope()
    {
        var scopes = new HashSet<BandCurrentProjectionScopeKey>();

        BandCurrentProjectionScopeTracker.AddScopes(scopes, "song-1", "Band_Trios", "0:1");

        Assert.Single(scopes);
        Assert.Contains(scopes, scope =>
            scope.SongId == "song-1"
            && scope.BandType == "Band_Trios"
            && scope.RankingScope == "overall"
            && scope.ScopeComboId == string.Empty);
    }
}
