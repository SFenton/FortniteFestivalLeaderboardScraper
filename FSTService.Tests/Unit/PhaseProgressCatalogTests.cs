using FSTService.Scraping;

namespace FSTService.Tests.Unit;

public sealed class PhaseProgressCatalogTests
{
    [Fact]
    public void Stable_phase_ids_are_unique_and_test_locked()
    {
        var expected = new[]
        {
            "scrape.leaderboards",
            "post.rank_recompute",
            "post.first_seen_season",
            "post.account_name_resolution",
            "post.refresh_registered_users",
            "post.activate_shadow_snapshots_early",
            "post.band_extraction",
            "post.legacy_band_scrape",
            "post.registered_player_band_discovery",
            "post.registered_band_targeted_processing",
            "post.deferred_registration_sync",
            "post.band_maintenance",
            "post.compute_rankings",
            "post.prepare_solo_current_projection",
            "post.rivals",
            "post.leaderboard_rivals",
            "post.player_stats_tiers",
            "post.checkpoint",
            "post.activate_shadow_snapshots",
            "post.seal_solo_current_projection",
            "post.cleanup_solo_current_projection",
            "post.cleanup_precompute_all",
            "post.cleanup_solo_excess_entries",
            "post.cleanup_rank_history_retention",
            "post.cleanup_band_rank_history_retention",
            "post.cleanup_service_level_retention",
            "publication.commit",
            "post.improvement_notifications",
        };

        Assert.Equal("fst.scrape-plan.v2", PhaseProgressCatalog.PlanVersion);
        Assert.Equal(expected, PhaseProgressCatalog.All.Select(descriptor => descriptor.Id));
        Assert.Equal(
            PhaseProgressCatalog.All.Count,
            PhaseProgressCatalog.All.Select(descriptor => descriptor.Id).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(
            PhaseProgressCatalog.All.Count,
            PhaseProgressCatalog.All.Select(descriptor => descriptor.Ordinal).Distinct().Count());
        Assert.Equal(
            PhaseProgressCatalog.All.Count,
            PhaseProgressCatalog.All.Select(descriptor => descriptor.LegacyPhase)
                .Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(
            PhaseProgressCatalog.All.Select(descriptor => descriptor.Ordinal).Order(),
            PhaseProgressCatalog.All.Select(descriptor => descriptor.Ordinal));
        Assert.All(PhaseProgressCatalog.All, descriptor =>
        {
            Assert.Matches("^[a-z][a-z0-9_.]*$", descriptor.Id);
            Assert.False(string.IsNullOrWhiteSpace(descriptor.Label));
            Assert.False(string.IsNullOrWhiteSpace(descriptor.LegacyPhase));
        });
    }

    [Fact]
    public void Every_post_scrape_policy_phase_has_a_stable_descriptor()
    {
        Assert.All(
            PostScrapePhasePolicy.All.Keys,
            phase => Assert.NotNull(PhaseProgressCatalog.FindPostScrape(phase)));
    }

    [Fact]
    public void Retired_execution_paths_keep_reserved_stable_ids_without_policies()
    {
        Assert.Equal(
            ["post.checkpoint", "post.deferred_registration_sync"],
            PhaseProgressCatalog.Reserved.Order(StringComparer.Ordinal));

        Assert.All(
            PhaseProgressCatalog.Reserved,
            phaseId =>
            {
                var descriptor = Assert.IsType<PhaseProgressDescriptor>(
                    PhaseProgressCatalog.FindById(phaseId));
                Assert.False(PostScrapePhasePolicy.All.ContainsKey(descriptor.LegacyPhase));
            });
    }
}
