using System.Diagnostics;
using System.Text.Json;
using FSTService.Persistence;
using FSTService.Persistence.Maintenance;

namespace FSTService.Tests.Unit;

public sealed class SnapshotRetentionSchemaCommandTests
{
    [Theory]
    [InlineData("--initialize-schema-only")]
    [InlineData("--recover-improvement-notifications")]
    [InlineData("--score-history-dedup-maintenance")]
    [InlineData("--solo-family-ranking-backfill")]
    [InlineData("--leaderboard-rivals-recompute-account")]
    [InlineData(MaxScoreMaintenanceCommand.StageFlag)]
    [InlineData(MaxScoreMaintenanceCommand.PlanFlag)]
    [InlineData(MaxScoreMaintenanceCommand.ApplyFlag)]
    [InlineData(MaxScoreMaintenanceCommand.ResumeFlag)]
    [InlineData(MaxScoreMaintenanceCommand.RollbackFlag)]
    [InlineData("--setup")]
    [InlineData("--replay-tier0")]
    [InlineData("--replay-tier1")]
    [InlineData("--api-only")]
    [InlineData("--once")]
    [InlineData("--backfill-only")]
    [InlineData("--registration-sync-worker")]
    [InlineData("--no-scraper-worker")]
    [InlineData("--rollout-read-only-startup")]
    [InlineData("--rollout-postgres-read-only")]
    [InlineData("--published-scrape-id")]
    [InlineData("--target")]
    [InlineData("--sql")]
    [InlineData("--path")]
    [InlineData("--connection-string")]
    [InlineData("--force")]
    [InlineData("--archive")]
    [InlineData("--drop")]
    [InlineData("--initialize-snapshot-retention-schema-only")]
    public async Task Rejects_every_additional_argument_before_connection(string argument)
    {
        using var output = new StringWriter();
        string[] args = [SnapshotRetentionSchemaCommand.Flag, argument, "private-input"];
        Assert.True(SnapshotRetentionSchemaCommand.IsRequested(args));
        Assert.Equal(64, await SnapshotRetentionSchemaCommand.RunAsync(
            args, "not a connection string;Password=private-secret", output));
        using var json = JsonDocument.Parse(output.ToString());
        Assert.Equal("invalid_command_arguments", json.RootElement.GetProperty("code").GetString());
        Assert.False(json.RootElement.GetProperty("hostedServicesStarted").GetBoolean());
        Assert.DoesNotContain("private", output.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("--initialize-snapshot-retention-schema-only=true")]
    [InlineData("--initialize-snapshot-retention-schema-only-extra")]
    [InlineData("/initialize-snapshot-retention-schema-only")]
    [InlineData("-initialize-snapshot-retention-schema-only")]
    [InlineData("initialize-snapshot-retention-schema-only=true")]
    public async Task Malformed_command_forms_cannot_fall_through_to_a_worker(string argument)
    {
        Assert.True(SnapshotRetentionSchemaCommand.IsRequested([argument]));
        using var output = new StringWriter();
        Assert.Equal(64, await SnapshotRetentionSchemaCommand.RunAsync([argument], null, output));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public async Task Missing_environment_is_an_explicit_refusal(string? connection)
    {
        using var output = new StringWriter();
        Assert.Equal(2, await SnapshotRetentionSchemaCommand.RunAsync(
            [SnapshotRetentionSchemaCommand.Flag.ToUpperInvariant()], connection, output));
        Assert.Contains("postgresql_connection_missing", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Invalid_connection_is_not_echoed()
    {
        using var output = new StringWriter();
        Assert.Equal(2, await SnapshotRetentionSchemaCommand.RunAsync(
            [SnapshotRetentionSchemaCommand.Flag], "private-secret=invalid", output));
        Assert.Contains("postgresql_connection_invalid", output.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("private-secret", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Dedicated_step_is_the_exact_shared_bounded_retention_step()
    {
        var step = DatabaseInitializer.SnapshotGenerationRetentionInitializationStep;
        Assert.Equal(step, DatabaseInitializer.GetSchemaInitializationPlan().Single(
            item => item.Name == "snapshot-generation-retention-report-only"));
        Assert.Equal(SnapshotGenerationRetentionSchema.Sql, step.Sql);
        Assert.True(step.UseShortTransaction);
        Assert.False(step.UseConcurrentIndex);
        Assert.Equal(20, step.CommandTimeoutSeconds);
        Assert.Equal("2s", step.LockTimeout);
        Assert.Equal("15s", step.StatementTimeout);
    }

    [Fact]
    public void Dispatch_precedes_replay_dotenv_and_host_construction()
    {
        var root = RepositoryRoot();
        var program = File.ReadAllText(Path.Combine(root, "FSTService/Program.cs"));
        var dispatch = program.IndexOf("SnapshotRetentionSchemaCommand.IsRequested(args)", StringComparison.Ordinal);
        Assert.InRange(dispatch, 0, program.IndexOf("ReplayCommand.IsRequested(args)", StringComparison.Ordinal));
        Assert.True(dispatch < program.IndexOf("var envPath", StringComparison.Ordinal));
        Assert.True(dispatch < program.IndexOf("WebApplication.CreateBuilder", StringComparison.Ordinal));
        var command = File.ReadAllText(Path.Combine(root, "FSTService/Persistence/SnapshotRetentionSchemaCommand.cs"));
        foreach (var forbidden in new[]
        {
            "WebApplication", "AddHostedService", "StartupInitializer", "PublicationPathArtifactSchema",
            "EnsureSchemaAsync(", "StartScrape", "Freeze", "DockerClient", "Process.Start", ".env",
        })
            Assert.DoesNotContain(forbidden, command, StringComparison.Ordinal);
        var initializer = File.ReadAllText(Path.Combine(root, "FSTService/Persistence/DatabaseInitializer.cs"));
        var start = initializer.IndexOf("internal static async Task<SnapshotRetentionSchemaDmlProof> EnsureSnapshotGenerationRetentionSchemaAsync",
            StringComparison.Ordinal);
        var end = initializer.IndexOf("internal static DatabaseSchemaInitializationStep", start, StringComparison.Ordinal);
        var dedicated = initializer[start..end];
        Assert.Contains("ExecuteSchemaInitializationStepAsync", dedicated, StringComparison.Ordinal);
        Assert.DoesNotContain("GetSchemaInitializationPlan", dedicated, StringComparison.Ordinal);
        Assert.DoesNotContain("EnsureSchemaAsync(", dedicated, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("--initialize-schema-only")]
    [InlineData("--replay-tier0")]
    [InlineData("--once")]
    public async Task Real_process_refuses_mixed_modes_without_loading_dotenv_or_starting_hosts(string other)
    {
        var directory = Path.Combine(Path.GetTempPath(), "retention_command_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(directory, ".env"), "invalid\0environment-name=value");
            var start = new ProcessStartInfo("dotnet")
            {
                WorkingDirectory = directory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            start.ArgumentList.Add(typeof(DatabaseInitializer).Assembly.Location);
            start.ArgumentList.Add(other);
            start.ArgumentList.Add(SnapshotRetentionSchemaCommand.Flag);
            start.Environment[SnapshotRetentionSchemaCommand.ConnectionEnvironment] = "private-secret=invalid";
            using var process = Process.Start(start)!;
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            finally
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync();
                }
            }
            Assert.Equal(64, process.ExitCode);
            using var result = JsonDocument.Parse(await stdout);
            Assert.Equal("invalid_command_arguments", result.RootElement.GetProperty("code").GetString());
            Assert.False(result.RootElement.GetProperty("hostedServicesStarted").GetBoolean());
            Assert.Empty(await stderr);
            Assert.DoesNotContain("private-secret", await stdout, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Runtime_pool_selection_immediately_follows_build_before_dispatch_or_pipeline()
    {
        var program = File.ReadAllText(Path.Combine(RepositoryRoot(), "FSTService/Program.cs"));
        Assert.Matches(
            @"var app = builder\.Build\(\);\s*(?://[^\n]*\n\s*)?_ = app\.Services\.GetRequiredService<NpgsqlDataSource>\(\);",
            program);
        Assert.Contains("strictOneShotWithoutHostedServices || initializeSchemaOnlyRequested", program, StringComparison.Ordinal);
        Assert.Contains("StartupPublicationReadOnlyState.ForInitializedDatabase(", program, StringComparison.Ordinal);
    }

    private static string RepositoryRoot()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "FortniteFestivalLeaderboardScraper.sln")))
            root = root.Parent;
        Assert.NotNull(root);
        return root.FullName;
    }
}
