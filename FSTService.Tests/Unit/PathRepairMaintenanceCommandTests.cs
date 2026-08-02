using FSTService.Persistence;

namespace FSTService.Tests.Unit;

public sealed class PathRepairMaintenanceCommandTests
{
    [Fact]
    public void Maintenance_lock_is_outside_publication_cache_build_range()
    {
        Assert.True(
            PathRepairMaintenanceLock.AdvisoryLockKey <
            PublicationGenerationSchema.CacheBuildAdvisoryLockBase);
        Assert.NotEqual(
            PublicationGenerationSchema.AdvisoryLockKey,
            PathRepairMaintenanceLock.AdvisoryLockKey);
    }

    [Fact]
    public void Parse_returns_null_without_path_repair_arguments()
    {
        Assert.Null(PathRepairMaintenanceCommand.Parse(
            ["--published-scrape-id", "1274"]));
    }

    [Fact]
    public void Parse_stage_requires_only_explicit_manifest_output()
    {
        var command = PathRepairMaintenanceCommand.Parse(
        [
            PathRepairMaintenanceCommand.StageFlag,
            PathRepairMaintenanceCommand.ManifestOutputFlag,
            "repair/manifest.json",
        ]);

        Assert.NotNull(command);
        Assert.Equal(
            PathRepairMaintenanceAction.StageExactFour,
            command!.Action);
        Assert.Equal("repair/manifest.json", command.ManifestOutputPath);
        Assert.Null(command.ManifestPath);
        Assert.Null(command.ExpectedPublishedScrapeId);
    }

    [Fact]
    public void Parse_promotion_binds_manifest_rollback_and_published_scrape()
    {
        var command = PathRepairMaintenanceCommand.Parse(
        [
            PathRepairMaintenanceCommand.PromoteFlag,
            PathRepairMaintenanceCommand.ManifestFlag,
            "repair/manifest.json",
            PathRepairMaintenanceCommand.RollbackOutputFlag,
            "repair/rollback.json",
            PathRepairMaintenanceCommand.PublishedScrapeIdFlag,
            "1274",
        ]);

        Assert.NotNull(command);
        Assert.Equal(
            PathRepairMaintenanceAction.PromoteExactFour,
            command!.Action);
        Assert.Equal("repair/manifest.json", command.ManifestPath);
        Assert.Equal("repair/rollback.json", command.RollbackOutputPath);
        Assert.Equal(1274, command.ExpectedPublishedScrapeId);
    }

    [Fact]
    public void Parse_ranking_rebuild_rejects_rollback_output()
    {
        Assert.Throws<ArgumentException>(() =>
            PathRepairMaintenanceCommand.Parse(
            [
                PathRepairMaintenanceCommand.RebuildRankingsFlag,
                PathRepairMaintenanceCommand.ManifestFlag,
                "repair/manifest.json",
                PathRepairMaintenanceCommand.RollbackOutputFlag,
                "repair/rollback.json",
                PathRepairMaintenanceCommand.PublishedScrapeIdFlag,
                "1274",
            ]));
    }

    [Theory]
    [InlineData(
        "--path-repair-stage-exact-four",
        "--path-repair-promote-exact-four")]
    [InlineData(
        "--path-repair-promote-exact-four",
        "--path-repair-rebuild-rankings")]
    public void Parse_rejects_multiple_actions(
        string first,
        string second)
    {
        Assert.Throws<ArgumentException>(() =>
            PathRepairMaintenanceCommand.Parse([first, second]));
    }

    [Fact]
    public void Parse_rejects_missing_published_scrape_for_mutating_commands()
    {
        Assert.Throws<ArgumentException>(() =>
            PathRepairMaintenanceCommand.Parse(
            [
                PathRepairMaintenanceCommand.PromoteFlag,
                PathRepairMaintenanceCommand.ManifestFlag,
                "repair/manifest.json",
                PathRepairMaintenanceCommand.RollbackOutputFlag,
                "repair/rollback.json",
            ]));
    }
}
