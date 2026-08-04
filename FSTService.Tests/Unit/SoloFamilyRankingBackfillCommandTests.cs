using FSTService.Scraping;

namespace FSTService.Tests.Unit;

public sealed class SoloFamilyRankingBackfillCommandTests
{
    [Fact]
    public void ParserDefaultsToDryRunAndRequiresMaintenanceFlag()
    {
        Assert.Null(SoloFamilyRankingBackfillCommand.Parse([]));

        var dryRun = SoloFamilyRankingBackfillCommand.Parse(
            [SoloFamilyRankingBackfillCommand.MaintenanceFlag]);
        Assert.NotNull(dryRun);
        Assert.False(dryRun.Execute);

        var execute = SoloFamilyRankingBackfillCommand.Parse(
        [
            SoloFamilyRankingBackfillCommand.MaintenanceFlag
                .ToUpperInvariant(),
            SoloFamilyRankingBackfillCommand.ExecuteFlag
                .ToUpperInvariant(),
        ]);
        Assert.NotNull(execute);
        Assert.True(execute.Execute);

        Assert.Throws<ArgumentException>(() =>
            SoloFamilyRankingBackfillCommand.Parse(
                [SoloFamilyRankingBackfillCommand.ExecuteFlag]));
        Assert.Throws<ArgumentException>(() =>
            SoloFamilyRankingBackfillCommand.Parse(
            [
                SoloFamilyRankingBackfillCommand.MaintenanceFlag,
                SoloFamilyRankingBackfillCommand.MaintenanceFlag,
            ]));
        Assert.Throws<ArgumentException>(() =>
            SoloFamilyRankingBackfillCommand.Parse(
            [
                SoloFamilyRankingBackfillCommand.MaintenanceFlag,
                SoloFamilyRankingBackfillCommand.ExecuteFlag,
                SoloFamilyRankingBackfillCommand.ExecuteFlag,
            ]));
    }
}
