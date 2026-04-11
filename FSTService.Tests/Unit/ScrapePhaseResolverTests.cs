namespace FSTService.Tests.Unit;

public class ScrapePhaseResolverTests
{
    [Fact]
    public void Resolve_None_ReturnsAll()
    {
        var result = ScrapePhaseResolver.Resolve(ScrapePhase.None);
        Assert.Equal(ScrapePhase.All, result);
    }

    [Fact]
    public void Resolve_SoloScrape_ExpandsToGroup()
    {
        var result = ScrapePhaseResolver.Resolve(ScrapePhase.SoloScrape);

        Assert.True(result.HasFlag(ScrapePhase.SoloScrape));
        Assert.True(result.HasFlag(ScrapePhase.SoloEnrichment));
        Assert.True(result.HasFlag(ScrapePhase.SoloRefreshUsers));
        // Should NOT include downstream solo phases
        Assert.False(result.HasFlag(ScrapePhase.SoloRankings));
        Assert.False(result.HasFlag(ScrapePhase.SoloRivals));
        Assert.False(result.HasFlag(ScrapePhase.SoloPlayerStats));
        Assert.False(result.HasFlag(ScrapePhase.SoloPrecompute));
        Assert.False(result.HasFlag(ScrapePhase.SoloFinalize));
        // Should NOT include band phases
        Assert.False(result.HasFlag(ScrapePhase.BandScrape));
    }

    [Fact]
    public void Resolve_SoloLeaderboards_ExpandsToGroup()
    {
        var result = ScrapePhaseResolver.Resolve(ScrapePhase.SoloRankings);

        Assert.True(result.HasFlag(ScrapePhase.SoloRankings));
        Assert.True(result.HasFlag(ScrapePhase.SoloRivals));
        Assert.True(result.HasFlag(ScrapePhase.SoloPlayerStats));
        Assert.True(result.HasFlag(ScrapePhase.SoloPrecompute));
        Assert.True(result.HasFlag(ScrapePhase.SoloFinalize));
        // Should NOT include upstream solo phases
        Assert.False(result.HasFlag(ScrapePhase.SoloScrape));
        Assert.False(result.HasFlag(ScrapePhase.SoloEnrichment));
        Assert.False(result.HasFlag(ScrapePhase.SoloRefreshUsers));
    }

    [Fact]
    public void Resolve_BandScrape_ExpandsToGroup()
    {
        var result = ScrapePhaseResolver.Resolve(ScrapePhase.BandScrape);

        Assert.True(result.HasFlag(ScrapePhase.BandScrape));
        Assert.True(result.HasFlag(ScrapePhase.BandScrapePhase));
        Assert.True(result.HasFlag(ScrapePhase.BandExtraction));
        // Should NOT include any solo phases
        Assert.False(result.HasFlag(ScrapePhase.SoloScrape));
        Assert.False(result.HasFlag(ScrapePhase.SoloRankings));
    }

    [Fact]
    public void Resolve_SoloScrapeAndSoloLeaderboards_FillsIntermediaries()
    {
        // --solo-scrape --solo-leaderboards should enable ALL 8 solo phases
        var input = ScrapePhase.SoloScrape | ScrapePhase.SoloRankings;
        var result = ScrapePhaseResolver.Resolve(input);

        Assert.True(result.HasFlag(ScrapePhase.SoloScrape));
        Assert.True(result.HasFlag(ScrapePhase.SoloEnrichment));
        Assert.True(result.HasFlag(ScrapePhase.SoloRefreshUsers));
        Assert.True(result.HasFlag(ScrapePhase.SoloRankings));
        Assert.True(result.HasFlag(ScrapePhase.SoloRivals));
        Assert.True(result.HasFlag(ScrapePhase.SoloPlayerStats));
        Assert.True(result.HasFlag(ScrapePhase.SoloPrecompute));
        Assert.True(result.HasFlag(ScrapePhase.SoloFinalize));
        // Band should NOT be included
        Assert.False(result.HasFlag(ScrapePhase.BandScrape));
    }

    [Fact]
    public void Resolve_BandScrapeAndSoloLeaderboards_BothChainsIndependent()
    {
        var input = ScrapePhase.BandScrape | ScrapePhase.SoloRankings;
        var result = ScrapePhaseResolver.Resolve(input);

        // Band chain fully enabled
        Assert.True(result.HasFlag(ScrapePhase.BandScrape));
        Assert.True(result.HasFlag(ScrapePhase.BandScrapePhase));
        Assert.True(result.HasFlag(ScrapePhase.BandExtraction));
        // Solo leaderboards group enabled
        Assert.True(result.HasFlag(ScrapePhase.SoloRankings));
        Assert.True(result.HasFlag(ScrapePhase.SoloRivals));
        Assert.True(result.HasFlag(ScrapePhase.SoloPlayerStats));
        Assert.True(result.HasFlag(ScrapePhase.SoloPrecompute));
        Assert.True(result.HasFlag(ScrapePhase.SoloFinalize));
        // Solo scrape chain NOT enabled (no intermediary fill — gap is not between two solo phases on the chain)
        Assert.False(result.HasFlag(ScrapePhase.SoloScrape));
        Assert.False(result.HasFlag(ScrapePhase.SoloEnrichment));
        Assert.False(result.HasFlag(ScrapePhase.SoloRefreshUsers));
    }

    [Fact]
    public void Resolve_SoloScrapeAndBandScrape_BothScrapeGroupsNoLeaderboards()
    {
        var input = ScrapePhase.SoloScrape | ScrapePhase.BandScrape;
        var result = ScrapePhaseResolver.Resolve(input);

        // Solo scrape group
        Assert.True(result.HasFlag(ScrapePhase.SoloScrape));
        Assert.True(result.HasFlag(ScrapePhase.SoloEnrichment));
        Assert.True(result.HasFlag(ScrapePhase.SoloRefreshUsers));
        // Band scrape group
        Assert.True(result.HasFlag(ScrapePhase.BandScrape));
        Assert.True(result.HasFlag(ScrapePhase.BandScrapePhase));
        Assert.True(result.HasFlag(ScrapePhase.BandExtraction));
        // No leaderboard phases
        Assert.False(result.HasFlag(ScrapePhase.SoloRankings));
    }

    [Fact]
    public void Resolve_AllThreeFlags_EverythingEnabled()
    {
        var input = ScrapePhase.SoloScrape | ScrapePhase.BandScrape | ScrapePhase.SoloRankings;
        var result = ScrapePhaseResolver.Resolve(input);

        Assert.Equal(ScrapePhase.All, result);
    }

    [Fact]
    public void Resolve_BandScrapeOnly_NoSoloPhases()
    {
        var result = ScrapePhaseResolver.Resolve(ScrapePhase.BandScrape);

        // Only band phases
        Assert.Equal(ScrapePhase.BandAll, result & ScrapePhase.SoloAll | result & ScrapePhase.BandAll);
        Assert.Equal(ScrapePhase.None, result & ScrapePhase.SoloAll);
    }

    // ─── Format tests ──────────────────────────────────────────

    [Fact]
    public void Format_All_ReturnsFullPipeline()
    {
        Assert.Equal("All (full pipeline)", ScrapePhaseResolver.Format(ScrapePhase.All));
    }

    [Fact]
    public void Format_SoloScrapeGroup_ListsPhases()
    {
        var formatted = ScrapePhaseResolver.Format(ScrapePhaseResolver.SoloScrapeGroup);
        Assert.Contains("SoloScrape", formatted);
        Assert.Contains("SoloEnrichment", formatted);
        Assert.Contains("SoloRefreshUsers", formatted);
        Assert.DoesNotContain("SoloRankings", formatted);
    }

    // ─── ScraperOptions integration ─────────────────────────────

    [Fact]
    public void ScraperOptions_NoFlags_ResolvesToAll()
    {
        var opts = new ScraperOptions();
        Assert.Equal(ScrapePhase.None, opts.EnabledPhases);
        Assert.Equal(ScrapePhase.All, opts.ResolvedPhases);
    }

    [Fact]
    public void ScraperOptions_WithFlags_ResolvesCorrectly()
    {
        var opts = new ScraperOptions { EnabledPhases = ScrapePhase.SoloScrape };
        var resolved = opts.ResolvedPhases;

        Assert.True(resolved.HasFlag(ScrapePhase.SoloScrape));
        Assert.True(resolved.HasFlag(ScrapePhase.SoloEnrichment));
        Assert.True(resolved.HasFlag(ScrapePhase.SoloRefreshUsers));
        Assert.False(resolved.HasFlag(ScrapePhase.SoloRankings));
    }

    // ─── Micro-phase tests ──────────────────────────────────────

    [Fact]
    public void Resolve_SoloEnrichmentAlone_OnlyEnrichment()
    {
        // --solo-enrichment sets just that one phase (no group expansion)
        var result = ScrapePhaseResolver.Resolve(ScrapePhase.SoloEnrichment);

        Assert.True(result.HasFlag(ScrapePhase.SoloEnrichment));
        // No group expansion — only intermediary fill, but min==max so just the one phase
        Assert.False(result.HasFlag(ScrapePhase.SoloScrape));
        Assert.False(result.HasFlag(ScrapePhase.SoloRefreshUsers));
        Assert.False(result.HasFlag(ScrapePhase.SoloRankings));
    }

    [Fact]
    public void Resolve_SoloEnrichmentAndSoloLeaderboards_FillsGap()
    {
        // --solo-enrichment --solo-leaderboards → positions 2 + 4-8 → fill 3 → 2-8
        var input = ScrapePhase.SoloEnrichment | ScrapePhase.SoloRankings;
        var result = ScrapePhaseResolver.Resolve(input);

        Assert.False(result.HasFlag(ScrapePhase.SoloScrape));      // pos 1 — NOT filled
        Assert.True(result.HasFlag(ScrapePhase.SoloEnrichment));    // pos 2
        Assert.True(result.HasFlag(ScrapePhase.SoloRefreshUsers));  // pos 3 — filled
        Assert.True(result.HasFlag(ScrapePhase.SoloRankings));      // pos 4
        Assert.True(result.HasFlag(ScrapePhase.SoloRivals));        // pos 5
        Assert.True(result.HasFlag(ScrapePhase.SoloPlayerStats));   // pos 6
        Assert.True(result.HasFlag(ScrapePhase.SoloPrecompute));    // pos 7
        Assert.True(result.HasFlag(ScrapePhase.SoloFinalize));      // pos 8
    }

    [Fact]
    public void Resolve_SoloRefreshUsersAndSoloRivals_FillsRankingsBetween()
    {
        // --solo-refresh-users --solo-rivals → positions 3 + 5 → fill 4 → 3-5
        var input = ScrapePhase.SoloRefreshUsers | ScrapePhase.SoloRivals;
        var result = ScrapePhaseResolver.Resolve(input);

        Assert.False(result.HasFlag(ScrapePhase.SoloScrape));
        Assert.False(result.HasFlag(ScrapePhase.SoloEnrichment));
        Assert.True(result.HasFlag(ScrapePhase.SoloRefreshUsers));
        Assert.True(result.HasFlag(ScrapePhase.SoloRankings));   // filled
        Assert.True(result.HasFlag(ScrapePhase.SoloRivals));
        Assert.False(result.HasFlag(ScrapePhase.SoloPlayerStats));
        Assert.False(result.HasFlag(ScrapePhase.SoloPrecompute));
        Assert.False(result.HasFlag(ScrapePhase.SoloFinalize));
    }

    [Fact]
    public void Resolve_SoloPrecomputeAlone_OnlyPrecompute()
    {
        var result = ScrapePhaseResolver.Resolve(ScrapePhase.SoloPrecompute);

        Assert.True(result.HasFlag(ScrapePhase.SoloPrecompute));
        Assert.False(result.HasFlag(ScrapePhase.SoloRankings));
        Assert.False(result.HasFlag(ScrapePhase.SoloFinalize));
    }

    [Fact]
    public void Resolve_BandExtractionAlone_OnlyExtraction()
    {
        // --band-extraction sets just BandExtraction (no group expansion)
        var result = ScrapePhaseResolver.Resolve(ScrapePhase.BandExtraction);

        Assert.True(result.HasFlag(ScrapePhase.BandExtraction));
        Assert.False(result.HasFlag(ScrapePhase.BandScrape));
        Assert.False(result.HasFlag(ScrapePhase.BandScrapePhase));
    }

    [Fact]
    public void Resolve_BandPostScrapeAlone_OnlyBandScrapePhase()
    {
        var result = ScrapePhaseResolver.Resolve(ScrapePhase.BandScrapePhase);

        Assert.True(result.HasFlag(ScrapePhase.BandScrapePhase));
        Assert.False(result.HasFlag(ScrapePhase.BandScrape));
        Assert.False(result.HasFlag(ScrapePhase.BandExtraction));
    }

    [Fact]
    public void Resolve_SoloEnrichmentAndBandScrape_IndependentChains()
    {
        // Micro solo + group band
        var input = ScrapePhase.SoloEnrichment | ScrapePhase.BandScrape;
        var result = ScrapePhaseResolver.Resolve(input);

        // Solo: only enrichment
        Assert.False(result.HasFlag(ScrapePhase.SoloScrape));
        Assert.True(result.HasFlag(ScrapePhase.SoloEnrichment));
        Assert.False(result.HasFlag(ScrapePhase.SoloRefreshUsers));
        // Band: full group
        Assert.True(result.HasFlag(ScrapePhase.BandScrape));
        Assert.True(result.HasFlag(ScrapePhase.BandScrapePhase));
        Assert.True(result.HasFlag(ScrapePhase.BandExtraction));
    }
}
