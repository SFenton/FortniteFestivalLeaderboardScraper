using FSTService.Persistence.Maintenance;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;

namespace FSTService.Tests.Unit;

public sealed class RetentionConfigurationReloadTests
{
    [Fact]
    public void WorkerOptionsAndPlannerDoNotReloadWithinAnInstance()
    {
        const string key = "DatabaseMaintenance:SnapshotGenerationRetentionReportOnlyEnabled";
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { [key] = "true" })
            .Build();
        var services = new ServiceCollection();
        services.Configure<DatabaseMaintenanceOptions>(
            configuration.GetSection("DatabaseMaintenance"));
        using var original = services.BuildServiceProvider();
        var options = original.GetRequiredService<IOptions<DatabaseMaintenanceOptions>>();
        var monitor = original.GetRequiredService<IOptionsMonitor<DatabaseMaintenanceOptions>>();
        using var source = NpgsqlDataSource.Create("Host=127.0.0.1;Database=unused");
        var planner = new SnapshotGenerationRetentionPlanner(
            source, new(source), new SnapshotGenerationRetentionOracle(),
            new ServiceMaintenanceLock(), options, Options.Create(new ScraperOptions()),
            NullLogger<SnapshotGenerationRetentionPlanner>.Instance);
        var originalValue = options.Value;
        Assert.True(planner.IsEnabled);
        configuration[key] = "false";
        configuration.Reload();
        Assert.False(monitor.CurrentValue.SnapshotGenerationRetentionReportOnlyEnabled);
        Assert.Same(originalValue, options.Value);
        Assert.True(planner.IsEnabled);
        using var restarted = services.BuildServiceProvider();
        Assert.False(restarted.GetRequiredService<IOptions<DatabaseMaintenanceOptions>>()
            .Value.SnapshotGenerationRetentionReportOnlyEnabled);
    }
}
