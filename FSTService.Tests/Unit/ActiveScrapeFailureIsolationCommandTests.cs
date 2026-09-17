using System.Diagnostics;
using System.Text.Json;
using FSTService.Persistence;
using FSTService.Scraping;
using FSTService.Tests.Helpers;

namespace FSTService.Tests.Unit;

public sealed class ActiveScrapeFailureIsolationCommandTests
{
    [Fact]
    public void Parse_accepts_execute_command()
    {
        var command =
            ActiveScrapeFailureIsolationCommand.Parse(
                [
                    ActiveScrapeFailureIsolationCommand
                        .MaintenanceFlag,
                    ActiveScrapeFailureIsolationCommand
                        .ExecuteFlag,
                    $"{ActiveScrapeFailureIsolationCommand.ScrapeIdFlag}=1399",
                    $"{PublishedScrapeIdArgument.Flag}=1398",
                    ActiveScrapeFailureIsolationCommand
                        .FailurePhaseFlag,
                    MetaDatabase.NoProgressReadIsolationFailurePhase,
                    ActiveScrapeFailureIsolationCommand
                        .FailureMessageFlag,
                    "watchdog timeout",
                ],
                PublishedScrapeIdArgument.Parse(
                    [$"{PublishedScrapeIdArgument.Flag}=1398"]));

        Assert.NotNull(command);
        Assert.True(command!.Execute);
        Assert.False(command.CheckOnly);
        Assert.Equal(1399, command.ScrapeId);
        Assert.Equal(1398, command.ExpectedPublishedScrapeId);
        Assert.Equal(
            MetaDatabase.NoProgressReadIsolationFailurePhase,
            command.FailurePhase);
        Assert.Equal("watchdog timeout", command.FailureMessage);
    }

    [Fact]
    public void Parse_rejects_unsupported_failure_phase()
    {
        var error = Assert.Throws<ArgumentException>(() =>
            ActiveScrapeFailureIsolationCommand.Parse(
                [
                    ActiveScrapeFailureIsolationCommand
                        .MaintenanceFlag,
                    ActiveScrapeFailureIsolationCommand
                        .ExecuteFlag,
                    $"{ActiveScrapeFailureIsolationCommand.ScrapeIdFlag}=1399",
                    $"{PublishedScrapeIdArgument.Flag}=1398",
                    $"{ActiveScrapeFailureIsolationCommand.FailurePhaseFlag}=wrong",
                    $"{ActiveScrapeFailureIsolationCommand.FailureMessageFlag}=watchdog timeout",
                ],
                PublishedScrapeIdArgument.Parse(
                    [$"{PublishedScrapeIdArgument.Flag}=1398"])));

        Assert.Contains(
            ActiveScrapeFailureIsolationCommand.FailurePhaseFlag,
            error.Message);
    }

    [Fact]
    public void Parse_accepts_acquisition_failure_phase()
    {
        var command =
            ActiveScrapeFailureIsolationCommand.Parse(
                [
                    ActiveScrapeFailureIsolationCommand
                        .MaintenanceFlag,
                    ActiveScrapeFailureIsolationCommand
                        .ExecuteFlag,
                    $"{ActiveScrapeFailureIsolationCommand.ScrapeIdFlag}=1400",
                    $"{PublishedScrapeIdArgument.Flag}=1398",
                    ActiveScrapeFailureIsolationCommand
                        .FailurePhaseFlag,
                    MetaDatabase
                        .AcquisitionFailureIsolationFailurePhase,
                    ActiveScrapeFailureIsolationCommand
                        .FailureMessageFlag,
                    "acquisition checkpoint persistence failed",
                ],
                PublishedScrapeIdArgument.Parse(
                    [$"{PublishedScrapeIdArgument.Flag}=1398"]));

        Assert.NotNull(command);
        Assert.Equal(
            MetaDatabase
                .AcquisitionFailureIsolationFailurePhase,
            command!.FailurePhase);
    }

    [Fact]
    public void Parse_accepts_check_mode_without_failure_payload()
    {
        var command =
            ActiveScrapeFailureIsolationCommand.Parse(
                [
                    ActiveScrapeFailureIsolationCommand
                        .MaintenanceFlag,
                    ActiveScrapeFailureIsolationCommand
                        .CheckFlag,
                    $"{ActiveScrapeFailureIsolationCommand.ScrapeIdFlag}=1399",
                    $"{PublishedScrapeIdArgument.Flag}=1398",
                ],
                PublishedScrapeIdArgument.Parse(
                    [$"{PublishedScrapeIdArgument.Flag}=1398"]));

        Assert.NotNull(command);
        Assert.False(command!.Execute);
        Assert.True(command.CheckOnly);
        Assert.Equal(1399, command.ScrapeId);
        Assert.Equal(1398, command.ExpectedPublishedScrapeId);
        Assert.Null(command.FailurePhase);
        Assert.Null(command.FailureMessage);
    }

    [Fact]
    public void Parse_rejects_check_mode_with_failure_payload()
    {
        var error = Assert.Throws<ArgumentException>(() =>
            ActiveScrapeFailureIsolationCommand.Parse(
                [
                    ActiveScrapeFailureIsolationCommand
                        .MaintenanceFlag,
                    ActiveScrapeFailureIsolationCommand
                        .CheckFlag,
                    $"{ActiveScrapeFailureIsolationCommand.ScrapeIdFlag}=1399",
                    $"{PublishedScrapeIdArgument.Flag}=1398",
                    $"{ActiveScrapeFailureIsolationCommand.FailurePhaseFlag}={MetaDatabase.NoProgressReadIsolationFailurePhase}",
                    $"{ActiveScrapeFailureIsolationCommand.FailureMessageFlag}=watchdog timeout",
                ],
                PublishedScrapeIdArgument.Parse(
                    [$"{PublishedScrapeIdArgument.Flag}=1398"])));

        Assert.Contains(
            ActiveScrapeFailureIsolationCommand.CheckFlag,
            error.Message);
    }

    [Fact]
    public void Parse_rejects_missing_mode()
    {
        var error = Assert.Throws<ArgumentException>(() =>
            ActiveScrapeFailureIsolationCommand.Parse(
                [
                    ActiveScrapeFailureIsolationCommand
                        .MaintenanceFlag,
                    $"{ActiveScrapeFailureIsolationCommand.ScrapeIdFlag}=1399",
                    $"{PublishedScrapeIdArgument.Flag}=1398",
                    $"{ActiveScrapeFailureIsolationCommand.FailurePhaseFlag}={MetaDatabase.NoProgressReadIsolationFailurePhase}",
                    $"{ActiveScrapeFailureIsolationCommand.FailureMessageFlag}=watchdog timeout",
                ],
                PublishedScrapeIdArgument.Parse(
                    [$"{PublishedScrapeIdArgument.Flag}=1398"])));

        Assert.Contains(
            "exactly one",
            error.Message);
    }

    [Fact]
    public void Parse_requires_published_scrape_id()
    {
        var published =
            PublishedScrapeIdArgument.Parse([]);

        var error = Assert.Throws<ArgumentException>(() =>
            ActiveScrapeFailureIsolationCommand.Parse(
                [
                    ActiveScrapeFailureIsolationCommand
                        .MaintenanceFlag,
                    ActiveScrapeFailureIsolationCommand
                        .ExecuteFlag,
                    $"{ActiveScrapeFailureIsolationCommand.ScrapeIdFlag}=1399",
                    $"{ActiveScrapeFailureIsolationCommand.FailurePhaseFlag}={MetaDatabase.NoProgressReadIsolationFailurePhase}",
                    $"{ActiveScrapeFailureIsolationCommand.FailureMessageFlag}=watchdog timeout",
                ],
                published));

        Assert.Contains(
            PublishedScrapeIdArgument.Flag,
            error.Message);
    }

    [Fact]
    public void Parse_rejects_duplicate_maintenance_flag()
    {
        var error = Assert.Throws<ArgumentException>(() =>
            ActiveScrapeFailureIsolationCommand.Parse(
                [
                    ActiveScrapeFailureIsolationCommand.MaintenanceFlag,
                    ActiveScrapeFailureIsolationCommand.MaintenanceFlag,
                    ActiveScrapeFailureIsolationCommand.ExecuteFlag,
                    $"{ActiveScrapeFailureIsolationCommand.ScrapeIdFlag}=1399",
                ],
                PublishedScrapeIdArgument.Parse(
                    [$"{PublishedScrapeIdArgument.Flag}=1398"])));

        Assert.Contains(
            ActiveScrapeFailureIsolationCommand.MaintenanceFlag,
            error.Message);
    }

    [Fact]
    public void Parse_rejects_duplicate_execute_flag()
    {
        var error = Assert.Throws<ArgumentException>(() =>
            ActiveScrapeFailureIsolationCommand.Parse(
                [
                    ActiveScrapeFailureIsolationCommand.MaintenanceFlag,
                    ActiveScrapeFailureIsolationCommand.ExecuteFlag,
                    ActiveScrapeFailureIsolationCommand.ExecuteFlag,
                    $"{ActiveScrapeFailureIsolationCommand.ScrapeIdFlag}=1399",
                ],
                PublishedScrapeIdArgument.Parse(
                    [$"{PublishedScrapeIdArgument.Flag}=1398"])));

        Assert.Contains(
            ActiveScrapeFailureIsolationCommand.ExecuteFlag,
            error.Message);
    }

    [Fact]
    public void Parse_rejects_duplicate_check_flag()
    {
        var error = Assert.Throws<ArgumentException>(() =>
            ActiveScrapeFailureIsolationCommand.Parse(
                [
                    ActiveScrapeFailureIsolationCommand.MaintenanceFlag,
                    ActiveScrapeFailureIsolationCommand.CheckFlag,
                    ActiveScrapeFailureIsolationCommand.CheckFlag,
                    $"{ActiveScrapeFailureIsolationCommand.ScrapeIdFlag}=1399",
                ],
                PublishedScrapeIdArgument.Parse(
                    [$"{PublishedScrapeIdArgument.Flag}=1398"])));

        Assert.Contains(
            ActiveScrapeFailureIsolationCommand.CheckFlag,
            error.Message);
    }

    [Fact]
    public void Parse_rejects_missing_failure_message_value()
    {
        var error = Assert.Throws<ArgumentException>(() =>
            ActiveScrapeFailureIsolationCommand.Parse(
                [
                    ActiveScrapeFailureIsolationCommand.MaintenanceFlag,
                    ActiveScrapeFailureIsolationCommand.ExecuteFlag,
                    $"{ActiveScrapeFailureIsolationCommand.ScrapeIdFlag}=1399",
                    $"{ActiveScrapeFailureIsolationCommand.FailurePhaseFlag}={MetaDatabase.NoProgressReadIsolationFailurePhase}",
                    ActiveScrapeFailureIsolationCommand.FailureMessageFlag,
                ],
                PublishedScrapeIdArgument.Parse(
                    [$"{PublishedScrapeIdArgument.Flag}=1398"])));

        Assert.Contains(
            ActiveScrapeFailureIsolationCommand.FailureMessageFlag,
            error.Message);
    }

    [Fact]
    public void Parse_rejects_empty_failure_message()
    {
        var error = Assert.Throws<ArgumentException>(() =>
            ActiveScrapeFailureIsolationCommand.Parse(
                [
                    ActiveScrapeFailureIsolationCommand.MaintenanceFlag,
                    ActiveScrapeFailureIsolationCommand.ExecuteFlag,
                    $"{ActiveScrapeFailureIsolationCommand.ScrapeIdFlag}=1399",
                    $"{ActiveScrapeFailureIsolationCommand.FailurePhaseFlag}={MetaDatabase.NoProgressReadIsolationFailurePhase}",
                    $"{ActiveScrapeFailureIsolationCommand.FailureMessageFlag}=",
                ],
                PublishedScrapeIdArgument.Parse(
                    [$"{PublishedScrapeIdArgument.Flag}=1398"])));

        Assert.Contains(
            "non-empty value",
            error.Message);
    }

    [Fact]
    public void Parse_rejects_duplicate_failure_phase()
    {
        var error = Assert.Throws<ArgumentException>(() =>
            ActiveScrapeFailureIsolationCommand.Parse(
                [
                    ActiveScrapeFailureIsolationCommand.MaintenanceFlag,
                    ActiveScrapeFailureIsolationCommand.ExecuteFlag,
                    $"{ActiveScrapeFailureIsolationCommand.ScrapeIdFlag}=1399",
                    $"{ActiveScrapeFailureIsolationCommand.FailurePhaseFlag}={MetaDatabase.NoProgressReadIsolationFailurePhase}",
                    $"{ActiveScrapeFailureIsolationCommand.FailurePhaseFlag}={MetaDatabase.AcquisitionFailureIsolationFailurePhase}",
                    $"{ActiveScrapeFailureIsolationCommand.FailureMessageFlag}=watchdog timeout",
                ],
                PublishedScrapeIdArgument.Parse(
                    [$"{PublishedScrapeIdArgument.Flag}=1398"])));

        Assert.Contains(
            ActiveScrapeFailureIsolationCommand.FailurePhaseFlag,
            error.Message);
    }

    [Fact]
    public void Parse_rejects_non_positive_scrape_id()
    {
        var error = Assert.Throws<ArgumentException>(() =>
            ActiveScrapeFailureIsolationCommand.Parse(
                [
                    ActiveScrapeFailureIsolationCommand.MaintenanceFlag,
                    ActiveScrapeFailureIsolationCommand.ExecuteFlag,
                    $"{ActiveScrapeFailureIsolationCommand.ScrapeIdFlag}=0",
                    $"{ActiveScrapeFailureIsolationCommand.FailurePhaseFlag}={MetaDatabase.NoProgressReadIsolationFailurePhase}",
                    $"{ActiveScrapeFailureIsolationCommand.FailureMessageFlag}=watchdog timeout",
                ],
                PublishedScrapeIdArgument.Parse(
                    [$"{PublishedScrapeIdArgument.Flag}=1398"])));

        Assert.Contains(
            "positive integer",
            error.Message);
    }
}

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ActiveScrapeFailureIsolationEntryPointCollection
{
    public const string Name =
        "Active scrape failure isolation entrypoint";
}

[Collection(
    ActiveScrapeFailureIsolationEntryPointCollection.Name)]
public sealed class ActiveScrapeFailureIsolationEntryPointTests : IDisposable
{
    private readonly InMemoryMetaDatabase _fixture = new();

    public void Dispose() => _fixture.Dispose();

    [Fact]
    public async Task Entrypoint_writes_only_json_to_stdout()
    {
        var publishedScrapeId = _fixture.Db.StartScrapeRun();
        _fixture.Db.CompleteScrapeRun(
            publishedScrapeId,
            songsScraped: 1,
            totalEntries: 10,
            totalRequests: 1,
            totalBytes: 100);
        _fixture.Db.PublishScrapeRun(
            publishedScrapeId,
            promoteCachedResponses: false);

        var candidateScrapeId = _fixture.Db.StartScrapeRun();
        _fixture.Db.SetPublicReadFreeze(
            true,
            reason:
                MetaDatabase
                    .ActiveScrapeFailureIsolationFreezeReason);
        SeedActiveScrapeRuntime(
            candidateScrapeId);

        var check = await RunEntrypointAsync(
            execute: false,
            candidateScrapeId,
            publishedScrapeId);
        AssertEntrypointResult(
            check,
            execute: false,
            candidateScrapeId,
            publishedScrapeId);

        var execution = await RunEntrypointAsync(
            execute: true,
            candidateScrapeId,
            publishedScrapeId);
        AssertEntrypointResult(
            execution,
            execute: true,
            candidateScrapeId,
            publishedScrapeId);
    }

    private async Task<ProgramResult> RunEntrypointAsync(
        bool execute,
        long candidateScrapeId,
        long publishedScrapeId)
    {
        var workingDirectory = Path.Combine(
            Directory.GetCurrentDirectory(),
            ".test-temp",
            $"active-scrape-failure-isolation-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workingDirectory);

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "dotnet",
                WorkingDirectory = workingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            startInfo.Environment["ConnectionStrings__PostgreSQL"] =
                SharedPostgresContainer.OriginalConnectionStringFor(
                    _fixture.DataSource);
            // The child only reads startup configuration; avoid consuming an
            // inotify watcher in shared test hosts.
            startInfo.Environment[
                "DOTNET_HOSTBUILDER__RELOADCONFIGONCHANGE"] = "false";
            startInfo.Environment["TMPDIR"] = workingDirectory;
            startInfo.ArgumentList.Add(typeof(Program).Assembly.Location);
            startInfo.ArgumentList.Add(
                ActiveScrapeFailureIsolationCommand.MaintenanceFlag);
            startInfo.ArgumentList.Add(
                execute
                    ? ActiveScrapeFailureIsolationCommand.ExecuteFlag
                    : ActiveScrapeFailureIsolationCommand.CheckFlag);
            startInfo.ArgumentList.Add(
                ActiveScrapeFailureIsolationCommand.ScrapeIdFlag);
            startInfo.ArgumentList.Add(
                candidateScrapeId.ToString(
                    System.Globalization.CultureInfo.InvariantCulture));
            startInfo.ArgumentList.Add(PublishedScrapeIdArgument.Flag);
            startInfo.ArgumentList.Add(
                publishedScrapeId.ToString(
                    System.Globalization.CultureInfo.InvariantCulture));
            if (execute)
            {
                startInfo.ArgumentList.Add(
                    ActiveScrapeFailureIsolationCommand.FailurePhaseFlag);
                startInfo.ArgumentList.Add(
                    MetaDatabase.NoProgressReadIsolationFailurePhase);
                startInfo.ArgumentList.Add(
                    ActiveScrapeFailureIsolationCommand.FailureMessageFlag);
                startInfo.ArgumentList.Add("entrypoint test");
            }

            using var process = Process.Start(startInfo)
                ?? throw new InvalidOperationException(
                    "Could not start FSTService for failure-isolation output validation.");
            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(
                TimeSpan.FromSeconds(30));
            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                if (!process.HasExited)
                    process.Kill(entireProcessTree: true);
                throw;
            }

            return new ProgramResult(
                process.ExitCode,
                await stdoutTask,
                await stderrTask);
        }
        finally
        {
            if (Directory.Exists(workingDirectory))
                Directory.Delete(workingDirectory, recursive: true);
        }
    }

    private static void AssertEntrypointResult(
        ProgramResult result,
        bool execute,
        long candidateScrapeId,
        long publishedScrapeId)
    {
        Assert.True(
            result.ExitCode == 0,
            $"Expected exit code 0, received {result.ExitCode}.{Environment.NewLine}stdout:{Environment.NewLine}{result.Stdout}{Environment.NewLine}stderr:{Environment.NewLine}{result.Stderr}");
        using var document = JsonDocument.Parse(result.Stdout);
        var root = document.RootElement;
        var identity = execute
            ? root.GetProperty("Before")
            : root;
        Assert.Equal(
            candidateScrapeId,
            identity.GetProperty("ScrapeId").GetInt64());
        Assert.Equal(
            publishedScrapeId,
            identity.GetProperty("ExpectedPublishedScrapeId").GetInt64());
        Assert.True(
            root.GetProperty(
                execute
                    ? "Succeeded"
                    : "CanExecute").GetBoolean());
        if (execute)
        {
            var after = root.GetProperty("After");
            Assert.Equal(
                0,
                after.GetProperty(
                    "RunningPhaseAttemptCount").GetInt32());
            Assert.Equal(
                "offline",
                after.GetProperty(
                    "WorkerStatus").GetString());
            Assert.False(
                after.GetProperty(
                    "WorkerCurrentOperationPresent").GetBoolean());
        }
        Assert.DoesNotContain(
            "ThreadPool.SetMinThreads",
            result.Stdout,
            StringComparison.Ordinal);
        Assert.Contains(
            "ThreadPool.SetMinThreads",
            result.Stderr,
            StringComparison.Ordinal);
    }

    private void SeedActiveScrapeRuntime(
        long scrapeId)
    {
        var now = DateTime.UtcNow;
        var workerInstanceId =
            $"failure-isolation-entrypoint-{scrapeId}";
        _fixture.Db.UpsertWorkerHeartbeat(
            WorkerStatusPublisher.ScraperWorkerKey,
            "running",
            "scraper",
            workerInstanceId,
            now.AddMinutes(-10),
            now.AddMinutes(-1),
            "Worker ready",
            new WorkerOperationInfo
            {
                ContractVersion = 2,
                OperationKey =
                    "rankings.band.Band_Quad",
                OperationLabel =
                    "Computing Band Quads Rankings",
                Status = "running",
                Phase = "ComputingRankings",
                SubOperation = "band_rankings",
                Detail = "Band_Quad",
                StartedAtUtc =
                    now.AddMinutes(-5),
                UpdatedAtUtc =
                    now.AddMinutes(-1),
            });
        _fixture.Db.StartScrapePhaseAttempt(
            new ScrapePhaseAttemptStart(
                scrapeId,
                "post.compute_rankings",
                "scrape.update",
                310,
                PhaseProgressCatalog.PlanVersion,
                workerInstanceId,
                "band_rankings",
                "running",
                "instruments",
                24,
                25,
                true,
                96,
                "indeterminate",
                null,
                null,
                null,
                null,
                null,
                null,
                now.AddMinutes(-5),
                now.AddMinutes(-1),
                now.AddMinutes(-1),
                "build-test",
                "config-test"));
    }

    private sealed record ProgramResult(
        int ExitCode,
        string Stdout,
        string Stderr);
}
