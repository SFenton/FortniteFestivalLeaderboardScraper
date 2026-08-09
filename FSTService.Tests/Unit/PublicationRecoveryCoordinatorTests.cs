using FSTService.Api;
using FSTService.Persistence;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace FSTService.Tests.Unit;

public sealed class PublicationRecoveryCoordinatorTests
{
    [Fact]
    public void RunOnce_ReadOnlyModeDoesNotMutatePublicationState()
    {
        var metaDb = Substitute.For<IMetaDatabase>();
        var coordinator = new PublicationRecoveryCoordinator(
            metaDb,
            Options.Create(new PublicationCommitOptions()),
            Options.Create(new ScraperOptions
            {
                RolloutReadOnlyStartup = true,
            }),
            NullLogger<PublicationRecoveryCoordinator>.Instance);

        var result = coordinator.RunOnce();
        coordinator.Trigger();

        Assert.Equal(
            PublicationCommitIntentReconciliationStatus.NotPresent,
            result.CommitIntent.Status);
        Assert.Equal(
            PublicationCommitIntentReconciliationStatus.NotPresent,
            result.AbandonedWorking.Status);
        Assert.False(result.BandSweep.LockAcquired);
        Assert.False(result.BandSweep.Completed);
        _ = metaDb.DidNotReceive()
            .ReconcileStalePublicationCommitIntent(Arg.Any<TimeSpan>());
        _ = metaDb.DidNotReceive()
            .ReconcileAbandonedWorkingPublication(
                Arg.Any<TimeSpan>(),
                Arg.Any<TimeSpan>());
        _ = metaDb.DidNotReceive()
            .SweepPublicationBandTableOrphans();
    }

    [Fact]
    public void RunOnce_ClampsRecoveryDurationsAndReturnsAllResults()
    {
        var metaDb = Substitute.For<IMetaDatabase>();
        var commit = new PublicationCommitIntentReconciliationResult(
            PublicationCommitIntentReconciliationStatus.Fresh,
            11,
            TimeSpan.FromSeconds(2));
        var abandoned = new PublicationCommitIntentReconciliationResult(
            PublicationCommitIntentReconciliationStatus.AbandonedWorkingIsolated,
            12,
            TimeSpan.FromMinutes(3));
        var sweep = new PublicationBandOrphanSweepResult(
            LockAcquired: true,
            Completed: true,
            ExaminedTableCount: 4,
            DroppedTables: ["band_orphan"]);
        metaDb.ReconcileStalePublicationCommitIntent(
                TimeSpan.FromSeconds(1))
            .Returns(commit);
        metaDb.ReconcileAbandonedWorkingPublication(
                TimeSpan.FromSeconds(1),
                TimeSpan.FromSeconds(1))
            .Returns(abandoned);
        metaDb.SweepPublicationBandTableOrphans().Returns(sweep);
        var coordinator = new PublicationRecoveryCoordinator(
            metaDb,
            Options.Create(new PublicationCommitOptions
            {
                StaleCommitIntentSeconds = 0,
                AbandonedReadyGraceSeconds = 0,
                WorkerHeartbeatFreshSeconds = 0,
            }),
            Options.Create(new ScraperOptions()),
            NullLogger<PublicationRecoveryCoordinator>.Instance);

        var result = coordinator.RunOnce();

        Assert.Equal(commit, result.CommitIntent);
        Assert.Equal(abandoned, result.AbandonedWorking);
        Assert.Equal(sweep, result.BandSweep);
    }

    [Fact]
    public async Task Trigger_LogsBackgroundRecoveryFailure()
    {
        var metaDb = Substitute.For<IMetaDatabase>();
        metaDb.ReconcileStalePublicationCommitIntent(Arg.Any<TimeSpan>())
            .Returns(_ => throw new InvalidOperationException("recovery failed"));
        var logger = new SignalingLogger();
        var coordinator = new PublicationRecoveryCoordinator(
            metaDb,
            Options.Create(new PublicationCommitOptions()),
            Options.Create(new ScraperOptions()),
            logger);

        coordinator.Trigger();
        var entry = await logger.Warning.Task.WaitAsync(
            TimeSpan.FromSeconds(2));

        Assert.Contains(
            "Background publication recovery pass failed",
            entry.Message,
            StringComparison.Ordinal);
        Assert.IsType<InvalidOperationException>(entry.Exception);
    }

    private sealed class SignalingLogger
        : ILogger<PublicationRecoveryCoordinator>
    {
        public TaskCompletionSource<LogEntry> Warning { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public IDisposable BeginScope<TState>(TState state)
            where TState : notnull =>
            NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning)
            {
                Warning.TrySetResult(new LogEntry(
                    formatter(state, exception),
                    exception));
            }
        }
    }

    private sealed record LogEntry(
        string Message,
        Exception? Exception);

    private sealed class NullScope : IDisposable
    {
        public static NullScope Instance { get; } = new();

        public void Dispose()
        {
        }
    }
}
