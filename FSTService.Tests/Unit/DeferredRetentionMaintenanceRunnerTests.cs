using FSTService.Persistence.Maintenance;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace FSTService.Tests.Unit;

public sealed class DeferredRetentionMaintenanceRunnerTests
{
    [Fact]
    public async Task ScheduleAfterPublication_WaitsForPressureToClearBeforeRunningMaintenance()
    {
        var maintenance = Substitute.For<IDatabaseRetentionMaintenanceService>();
        var pressure = Substitute.For<IDatabasePressureMonitor>();
        var options = Options.Create(new DatabaseMaintenanceOptions
        {
            ServiceLevelRetentionMaintenanceEnabled = true,
            DeferredServiceLevelRetentionEnabled = true,
            DeferredServiceLevelRetentionInitialDelaySeconds = 0,
            DeferredServiceLevelRetentionPollSeconds = 1,
            DeferredServiceLevelRetentionMaxAttempts = 3,
            DeferredServiceLevelRetentionMaxRuntimeMinutes = 1,
        });
        var pressureSnapshot = new DatabasePressureSnapshot(true, 1, 0, 0, 0, ["active vacuum count 1"]);

        pressure.GetPressureSnapshotAsync(options.Value, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(pressureSnapshot), Task.FromResult(DatabasePressureSnapshot.None));
        maintenance.RunAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new DatabaseRetentionMaintenanceResult(
                DateTime.UtcNow,
                DateTime.UtcNow,
                Skipped: false,
                "completed",
                SnapshotRetentionMaintenanceResult.Skipped("none"),
                new MetadataRetentionCleanupResult(true, 12, [], "deleted rows"))));

        using var runner = new DeferredRetentionMaintenanceRunner(
            maintenance,
            pressure,
            options,
            NullLogger<DeferredRetentionMaintenanceRunner>.Instance);

        var scheduled = runner.ScheduleAfterPublication("test");
        await runner.CurrentRunForTests!;

        Assert.True(scheduled);
        await pressure.Received(2).GetPressureSnapshotAsync(options.Value, Arg.Any<CancellationToken>());
        await maintenance.Received(1).RunAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public void ScheduleAfterPublication_DoesNotScheduleWhenDisabled()
    {
        var maintenance = Substitute.For<IDatabaseRetentionMaintenanceService>();
        var pressure = Substitute.For<IDatabasePressureMonitor>();
        var options = Options.Create(new DatabaseMaintenanceOptions
        {
            ServiceLevelRetentionMaintenanceEnabled = true,
            DeferredServiceLevelRetentionEnabled = false,
        });

        using var runner = new DeferredRetentionMaintenanceRunner(
            maintenance,
            pressure,
            options,
            NullLogger<DeferredRetentionMaintenanceRunner>.Instance);

        Assert.False(runner.ScheduleAfterPublication("test"));
        Assert.Null(runner.CurrentRunForTests);
    }
}