using System.Diagnostics;
using FSTService;
using FSTService.Persistence;
using Microsoft.Extensions.Configuration;

namespace FSTService.Tests.Unit;

public sealed class RetiredMaintenanceCommandGuardTests
{
    public static TheoryData<string> RetiredOptions => new()
    {
        "--path-repair-stage-exact-four",
        "--path-repair-align-rankings",
        "--path-repair-promote-exact-four",
        "--path-repair-rebuild-rankings",
        "--path-repair-manifest",
        "--path-repair-manifest-output",
        "--path-repair-rollback-output",
        "--notification-maintenance-pro-lead-max-score-repair",
        "--notification-maintenance-execute",
        "--notification-maintenance-manifest",
        "--expected-notification-dry-run-digest",
        "--notification-reopen-completed",
    };

    public static TheoryData<string[]> AlternateStartupForms => new()
    {
        new[] { "/notification-maintenance-execute=true" },
        new[] { "/path-repair-manifest", "manifest.json" },
        new[] { "path-repair-stage-exact-four=true" },
        new[] { "-path-repair-rebuild-rankings" },
    };

    public static TheoryData<string[]> ConsumedValueForms => new()
    {
        new[] { "--test", "path-repair-manifest" },
        new[] { "--Scraper:DataDirectory", "path-repair-manifest" },
        new[] { "/Scraper:DataDirectory", "path-repair-manifest" },
        new[] { "--test", "--path-repair-manifest" },
    };

    public static TheoryData<string[]> ActiveScoreHistoryForms => new()
    {
        new[]
        {
            ScoreHistoryDedupMaintenanceCommand.MaintenanceFlag,
        },
        new[]
        {
            ScoreHistoryDedupMaintenanceCommand.MaintenanceFlag,
            "--api-only",
        },
        new[]
        {
            ScoreHistoryDedupMaintenanceCommand.MaintenanceFlag,
            ScoreHistoryDedupMaintenanceCommand.ExecuteFlag,
            ScoreHistoryDedupMaintenanceCommand.ExpectedDigestFlag,
            new string('a', 64),
        },
        new[]
        {
            ScoreHistoryDedupMaintenanceCommand.ExpectedDigestFlag
                .ToUpperInvariant(),
            new string('A', 64),
            ScoreHistoryDedupMaintenanceCommand.ExecuteFlag
                .ToUpperInvariant(),
            ScoreHistoryDedupMaintenanceCommand.MaintenanceFlag
                .ToUpperInvariant(),
        },
    };

    [Theory]
    [MemberData(nameof(RetiredOptions))]
    public void GuardRejectsEachRetiredOption(string option)
    {
        var exception = Assert.Throws<ArgumentException>(
            () => RetiredMaintenanceCommandGuard.ThrowIfPresent([option]));

        Assert.Contains(option, exception.Message, StringComparison.Ordinal);
        Assert.Contains(
            "before hosted scraper mode selection",
            exception.Message,
            StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(RetiredOptions))]
    public void GuardRejectsEachRetiredOptionWithInlineValue(string option)
    {
        var exception = Assert.Throws<ArgumentException>(
            () => RetiredMaintenanceCommandGuard.ThrowIfPresent(
                [$"{option}=retired-value"]));

        Assert.Contains(option, exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(RetiredOptions))]
    public void GuardRejectsEachRetiredOptionWithSeparateValue(string option)
    {
        var exception = Assert.Throws<ArgumentException>(
            () => RetiredMaintenanceCommandGuard.ThrowIfPresent(
                [option, "retired-value"]));

        Assert.Contains(option, exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(RetiredOptions))]
    public void GuardRejectsEachRetiredSlashOptionWithInlineValue(
        string option)
    {
        var slashOption = $"/{CanonicalName(option)}";
        var exception = Assert.Throws<ArgumentException>(
            () => RetiredMaintenanceCommandGuard.ThrowIfPresent(
                [$"{slashOption}=retired-value"]));

        Assert.Contains(
            slashOption,
            exception.Message,
            StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(RetiredOptions))]
    public void GuardRejectsEachRetiredSlashOptionWithSeparateValue(
        string option)
    {
        var slashOption = $"/{CanonicalName(option)}";
        var exception = Assert.Throws<ArgumentException>(
            () => RetiredMaintenanceCommandGuard.ThrowIfPresent(
                [slashOption, "retired-value"]));

        Assert.Contains(
            slashOption,
            exception.Message,
            StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(RetiredOptions))]
    public void GuardRejectsEachRetiredBareOptionWithInlineValue(
        string option)
    {
        var bareOption = CanonicalName(option);
        var exception = Assert.Throws<ArgumentException>(
            () => RetiredMaintenanceCommandGuard.ThrowIfPresent(
                [$"{bareOption}=retired-value"]));

        Assert.Contains(
            bareOption,
            exception.Message,
            StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(RetiredOptions))]
    public void GuardIgnoresUnsupportedBareOptionWithoutEquals(string option)
    {
        var bareOption = CanonicalName(option);
        RetiredMaintenanceCommandGuard.ThrowIfPresent([bareOption]);
    }

    [Theory]
    [MemberData(nameof(RetiredOptions))]
    public void GuardRejectsEachRetiredSingleDashOption(string option)
    {
        var singleDashOption = $"-{CanonicalName(option)}";
        var exception = Assert.Throws<ArgumentException>(
            () => RetiredMaintenanceCommandGuard.ThrowIfPresent(
                [singleDashOption]));

        Assert.Contains(
            singleDashOption,
            exception.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void GuardRejectsConflictingRetiredAndActiveModes()
    {
        var exception = Assert.Throws<ArgumentException>(
            () => RetiredMaintenanceCommandGuard.ThrowIfPresent(
            [
                "--once",
                "--notification-maintenance-execute",
                "--path-repair-manifest",
                "manifest.json",
            ]));

        Assert.Contains(
            "--notification-maintenance-execute",
            exception.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void GuardMatchesRetiredOptionsCaseInsensitively()
    {
        Assert.Throws<ArgumentException>(
            () => RetiredMaintenanceCommandGuard.ThrowIfPresent(
                ["--PATH-REPAIR-STAGE-EXACT-FOUR"]));
    }

    [Fact]
    public void GuardAllowsRecurringNotificationRecoveryArguments()
    {
        RetiredMaintenanceCommandGuard.ThrowIfPresent(
        [
            "--recover-improvement-notifications",
            "--published-scrape-id",
            "1276",
            "--notification-dry-run",
            "--notification-baseline-only",
            "--notification-force",
            "--notification-skip-projection-refresh",
            "--api-only",
        ]);
    }

    [Theory]
    [MemberData(nameof(ActiveScoreHistoryForms))]
    public void GuardAllowsActiveScoreHistoryMaintenanceArguments(
        string[] arguments)
    {
        RetiredMaintenanceCommandGuard.ThrowIfPresent(arguments);

        var command = ScoreHistoryDedupMaintenanceCommand.Parse(arguments);
        Assert.NotNull(command);
        Assert.Equal(
            arguments.Any(argument => argument.Equals(
                ScoreHistoryDedupMaintenanceCommand.ExecuteFlag,
                StringComparison.OrdinalIgnoreCase)),
            command.Execute);
    }

    [Fact]
    public void GuardDoesNotMatchUnrelatedArgumentsContainingRetiredText()
    {
        RetiredMaintenanceCommandGuard.ThrowIfPresent(
        [
            "--active-option=path-repair-stage-exact-four",
            "/var/lib/path-repair-manifest",
            "prefix-notification-maintenance-execute",
            "--recover-improvement-notifications",
        ]);
    }

    [Theory]
    [MemberData(nameof(ConsumedValueForms))]
    public void GuardDoesNotReinterpretConsumedValuesAsKeys(string[] arguments)
    {
        RetiredMaintenanceCommandGuard.ThrowIfPresent(arguments);
    }

    [Fact]
    public void GuardStillRejectsRetiredKeyAfterManualStandaloneFlag()
    {
        Assert.Throws<ArgumentException>(
            () => RetiredMaintenanceCommandGuard.ThrowIfPresent(
            [
                "--once",
                "path-repair-manifest=true",
            ]));
    }

    [Fact]
    public void MicrosoftProviderConsumesSeparateConfigurationValues()
    {
        var configuration = new ConfigurationBuilder()
            .AddCommandLine(
            [
                "--Scraper:DataDirectory",
                "path-repair-manifest",
            ])
            .Build();

        Assert.Equal(
            "path-repair-manifest",
            configuration["Scraper:DataDirectory"]);
        Assert.Null(configuration["path-repair-manifest"]);
    }

    [Fact]
    public void MicrosoftProviderSupportsSlashAndBareEqualsKeys()
    {
        var configuration = new ConfigurationBuilder()
            .AddCommandLine(
            [
                "/notification-maintenance-execute=true",
                "path-repair-stage-exact-four=true",
            ])
            .Build();

        Assert.Equal(
            "true",
            configuration["notification-maintenance-execute"]);
        Assert.Equal(
            "true",
            configuration["path-repair-stage-exact-four"]);
    }

    [Fact]
    public void MicrosoftProviderIgnoresBareTokenWithoutEquals()
    {
        var configuration = new ConfigurationBuilder()
            .AddCommandLine(["path-repair-stage-exact-four"])
            .Build();

        Assert.Null(configuration["path-repair-stage-exact-four"]);
    }

    [Theory]
    [MemberData(nameof(RetiredOptions))]
    public async Task ProgramStartupRejectsEachRetiredOption(string option)
    {
        var result = await RunProgramAsync(option);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains(option, result.Output, StringComparison.Ordinal);
        Assert.Contains(
            "before hosted scraper mode selection",
            result.Output,
            StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(RetiredOptions))]
    public async Task ProgramStartupRejectsEachRetiredInlineValue(
        string option)
    {
        var result = await RunProgramAsync($"{option}=retired-value");

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains(option, result.Output, StringComparison.Ordinal);
        Assert.Contains(
            "before hosted scraper mode selection",
            result.Output,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProgramStartupRejectsConflictingRetiredModes()
    {
        var result = await RunProgramAsync(
            "--api-only",
            "--path-repair-promote-exact-four",
            "--notification-maintenance-execute");

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains(
            "--path-repair-promote-exact-four",
            result.Output,
            StringComparison.Ordinal);
        Assert.Contains(
            "before hosted scraper mode selection",
            result.Output,
            StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(AlternateStartupForms))]
    public async Task ProgramStartupRejectsAlternateRetiredForms(
        string[] arguments)
    {
        var result = await RunProgramAsync(arguments);
        var option = arguments[0].Split('=', 2)[0];

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains(option, result.Output, StringComparison.Ordinal);
        Assert.Contains(
            "before hosted scraper mode selection",
            result.Output,
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("--test")]
    [InlineData("--Scraper:DataDirectory")]
    public async Task ProgramStartupAcceptsRetiredNameAsConsumedValue(
        string consumingOption)
    {
        var result = await RunProgramAsync(
            consumingOption,
            "path-repair-manifest",
            "--score-history-dedup-execute");

        Assert.NotEqual(0, result.ExitCode);
        Assert.DoesNotContain(
            "Retired maintenance option",
            result.Output,
            StringComparison.Ordinal);
        Assert.Contains(
            "--score-history-dedup-maintenance must be specified exactly once",
            result.Output,
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("--initialize-schema-only")]
    [InlineData("--recover-improvement-notifications")]
    public async Task ProgramStartupRejectsConflictingScoreHistoryOneShots(
        string otherOneShot)
    {
        var result = await RunProgramAsync(
            ScoreHistoryDedupMaintenanceCommand.MaintenanceFlag,
            otherOneShot);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains(
            "cannot run with another one-shot",
            result.Output,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "Retired maintenance option",
            result.Output,
            StringComparison.Ordinal);
    }

    private static async Task<ProgramResult> RunProgramAsync(
        params string[] arguments)
    {
        var workingDirectory = Path.Combine(
            Directory.GetCurrentDirectory(),
            ".test-temp",
            $"retired-startup-{Guid.NewGuid():N}");
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
            startInfo.ArgumentList.Add(typeof(Program).Assembly.Location);
            foreach (var argument in arguments)
                startInfo.ArgumentList.Add(argument);

            using var process = Process.Start(startInfo)
                ?? throw new InvalidOperationException(
                    "Could not start FSTService for startup validation.");
            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(
                TimeSpan.FromSeconds(15));
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

            await Task.WhenAll(stdoutTask, stderrTask);
            return new ProgramResult(
                process.ExitCode,
                string.Concat(stdoutTask.Result, "\n", stderrTask.Result));
        }
        finally
        {
            if (Directory.Exists(workingDirectory))
                Directory.Delete(workingDirectory, recursive: true);
        }
    }

    private static string CanonicalName(string option)
        => option.StartsWith("--", StringComparison.Ordinal)
            ? option[2..]
            : option;

    private sealed record ProgramResult(int ExitCode, string Output);
}
