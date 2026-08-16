using Microsoft.Extensions.Logging;

namespace FSTService.Scraping.Replay;

public static class ReplayEntryPoint
{
    public static async Task<int> RunAsync(
        IReadOnlyList<string> args,
        ReplayExecutionEnvironment? environmentOverride = null,
        CancellationToken cancellationToken = default)
    {
        using var interrupt = CancellationTokenSource
            .CreateLinkedTokenSource(cancellationToken);
        ConsoleCancelEventHandler? handler = null;
        if (!cancellationToken.CanBeCanceled)
        {
            handler = (_, eventArgs) =>
            {
                eventArgs.Cancel = true;
                interrupt.Cancel();
            };
            Console.CancelKeyPress += handler;
        }

        try
        {
            var command = ReplayCommand.Parse(args);
            if (command.Kind == ReplayCommandKind.Compare)
            {
                var policy = new ReplayRootAdmission(
                    environmentOverride?.RootPolicy ??
                    ReplayExecutionEnvironment
                        .RootPolicyFromProcessEnvironment());
                var paths = policy.AdmitComparison(
                    command.BaselinePath!,
                    command.CandidatePath!,
                    command.ComparisonOutputPath!);
                var report = await new ReplayComparisonService()
                    .CompareAsync(
                        paths.Baseline,
                        paths.Candidate,
                        paths.Report,
                        command.ComparisonExpectations!,
                        interrupt.Token);
                WriteResult(new
                {
                    status = "completed",
                    kind = "comparison",
                    exactParity = report.ExactParity,
                    datasets = report.Datasets.Count,
                    baselineExecutionProfile =
                        report.BaselineExecutionProfile,
                    candidateExecutionProfile =
                        report.CandidateExecutionProfile,
                    elapsedDeltaPercent =
                        report.ElapsedDeltaPercent,
                    memberStatsAggregationPassDeltaPercent =
                        report.MemberStatsAggregationPassDeltaPercent,
                });
                return (int)ReplayExitCode.Success;
            }

            var environment =
                environmentOverride ??
                ReplayExecutionEnvironment.FromProcessEnvironment();
            using var loggerFactory = LoggerFactory.Create(builder =>
                builder
                    .SetMinimumLevel(LogLevel.Information)
                    .AddSimpleConsole(options =>
                    {
                        options.SingleLine = true;
                        options.TimestampFormat = "O ";
                    }));
            var rootAdmission =
                new ReplayRootAdmission(environment.RootPolicy);
            var targetGuard =
                new ReplayDatabaseTargetGuard(
                    environment.ReplayPostgresConnection,
                    environment.ProductionPostgresConnection,
                    environment.AllowTestServerAddress);
            var runner = new TierOneReplayRunner(
                rootAdmission,
                targetGuard,
                environment,
                loggerFactory);
            var result = await runner.ExecuteAsync(
                command,
                interrupt.Token);
            WriteResult(new
            {
                status = "completed",
                kind = "phase-replay",
                phaseId = command.PhaseId,
                subphaseId = command.SubphaseId,
                executionProfile = command.ExecutionProfile,
                packageRootHash = result.PackageRootHash,
                elapsedMilliseconds =
                    result.Metrics.ElapsedMilliseconds,
                refreshedScopes =
                    result.Metrics.RefreshedScopes,
                insertedRows = result.Metrics.InsertedRows,
                deletedRows = result.Metrics.DeletedRows,
                scopeTransactions =
                    result.Metrics.SuccessfulScopeTransactions,
                scopeCommands =
                    result.Metrics.SuccessfulScopeCommandExecutions,
                scopeRoundTrips =
                    result.Metrics.SuccessfulScopeRoundTrips,
                memberStatsAggregationPasses =
                    result.Metrics.MemberStatsAggregationPasses,
                noPublication = true,
            });
            return (int)ReplayExitCode.Success;
        }
        catch (OperationCanceledException)
        {
            WriteResult(new
            {
                status = "cancelled",
                exitCode = (int)ReplayExitCode.Cancelled,
            });
            return (int)ReplayExitCode.Cancelled;
        }
        catch (ReplayException exception)
        {
            WriteResult(new
            {
                status = "failed",
                kind = exception.Kind.ToString(),
                exitCode = (int)exception.ExitCode,
                message = exception.Message,
            });
            return (int)exception.ExitCode;
        }
        catch (Exception)
        {
            WriteResult(new
            {
                status = "failed",
                kind = "UnexpectedFailure",
                exitCode =
                    (int)ReplayExitCode.UnexpectedFailure,
                message = "Isolated replay failed unexpectedly.",
            });
            return (int)ReplayExitCode.UnexpectedFailure;
        }
        finally
        {
            if (handler is not null)
                Console.CancelKeyPress -= handler;
        }
    }

    private static void WriteResult<T>(T result) =>
        Console.WriteLine(
            TierZeroCanonicalJson.SerializeToString(result));
}
