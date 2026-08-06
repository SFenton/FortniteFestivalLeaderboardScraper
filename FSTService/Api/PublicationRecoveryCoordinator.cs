using FSTService.Persistence;
using Microsoft.Extensions.Options;

namespace FSTService.Api;

public interface IPublicationRecoveryCoordinator
{
    void Trigger();
    PublicationRecoveryRunResult RunOnce();
}

public sealed record PublicationRecoveryRunResult(
    PublicationCommitIntentReconciliationResult CommitIntent,
    PublicationCommitIntentReconciliationResult AbandonedWorking,
    PublicationBandOrphanSweepResult BandSweep);

public sealed class PublicationRecoveryCoordinator
    : IPublicationRecoveryCoordinator
{
    private static readonly TimeSpan TriggerTtl =
        TimeSpan.FromSeconds(2);
    private readonly IMetaDatabase _metaDb;
    private readonly PublicationCommitOptions _options;
    private readonly bool _readOnly;
    private readonly ILogger<PublicationRecoveryCoordinator> _log;
    private long _nextTriggerTicks;
    private int _running;

    public PublicationRecoveryCoordinator(
        IMetaDatabase metaDb,
        IOptions<PublicationCommitOptions> options,
        IOptions<ScraperOptions> scraperOptions,
        ILogger<PublicationRecoveryCoordinator> log)
    {
        _metaDb = metaDb;
        _options = options.Value;
        _readOnly = scraperOptions.Value.RolloutReadOnlyStartup;
        _log = log;
    }

    public void Trigger()
    {
        if (_readOnly)
            return;

        var nowTicks = DateTime.UtcNow.Ticks;
        if (nowTicks < Interlocked.Read(ref _nextTriggerTicks)
            || Interlocked.CompareExchange(
                ref _running,
                1,
                0) != 0)
        {
            return;
        }

        Interlocked.Exchange(
            ref _nextTriggerTicks,
            DateTime.UtcNow.Add(TriggerTtl).Ticks);
        _ = Task.Run(() =>
        {
            try
            {
                _ = RunOnce();
            }
            catch (Exception ex)
            {
                _log.LogWarning(
                    ex,
                    "Background publication recovery pass failed.");
            }
            finally
            {
                Volatile.Write(ref _running, 0);
            }
        });
    }

    public PublicationRecoveryRunResult RunOnce()
    {
        if (_readOnly)
        {
            return new PublicationRecoveryRunResult(
                new PublicationCommitIntentReconciliationResult(
                    PublicationCommitIntentReconciliationStatus
                        .NotPresent,
                    null,
                    null),
                new PublicationCommitIntentReconciliationResult(
                    PublicationCommitIntentReconciliationStatus
                        .NotPresent,
                    null,
                    null),
                new PublicationBandOrphanSweepResult(
                    LockAcquired: false,
                    Completed: false,
                    ExaminedTableCount: 0,
                    DroppedTables: []));
        }

        var commitIntent =
            _metaDb.ReconcileStalePublicationCommitIntent(
                TimeSpan.FromSeconds(
                    Math.Max(
                        1,
                        _options.StaleCommitIntentSeconds)));
        var abandoned =
            _metaDb.ReconcileAbandonedWorkingPublication(
                TimeSpan.FromSeconds(
                    Math.Max(
                        1,
                        _options.AbandonedReadyGraceSeconds)),
                TimeSpan.FromSeconds(
                    Math.Max(
                        1,
                        _options.WorkerHeartbeatFreshSeconds)));
        var sweep =
            _metaDb.SweepPublicationBandTableOrphans();
        return new PublicationRecoveryRunResult(
            commitIntent,
            abandoned,
            sweep);
    }
}
