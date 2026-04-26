using FSTService;
using FSTService.Persistence;
using Microsoft.Extensions.Options;

namespace FSTService.Scraping;

/// <summary>
/// Processes best-effort band rank-history jobs outside the scrape-critical path.
/// Jobs are resumable and scrape-aware: the coordinator cancels active chunks when
/// a new scrape starts, and the worker marks them paused for later continuation.
/// </summary>
public sealed class BandRankHistoryWorker : BackgroundService
{
    private static readonly TimeSpan IdleDelay = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan ErrorDelay = TimeSpan.FromSeconds(30);

    private readonly IMetaDatabase _metaDb;
    private readonly IOptions<BandRankHistoryOptions> _options;
    private readonly BackgroundWorkCoordinator _coordinator;
    private readonly ScrapeProgressTracker _progress;
    private readonly ILogger<BandRankHistoryWorker> _log;

    public BandRankHistoryWorker(
        IMetaDatabase metaDb,
        IOptions<BandRankHistoryOptions> options,
        BackgroundWorkCoordinator coordinator,
        ScrapeProgressTracker progress,
        ILogger<BandRankHistoryWorker> log)
    {
        _metaDb = metaDb;
        _options = options;
        _coordinator = coordinator;
        _progress = progress;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var opts = _options.Value;
            if (opts.Mode != BandRankHistoryMode.Background)
            {
                await DelaySafely(IdleDelay, stoppingToken);
                continue;
            }

            if (_coordinator.ScrapeRunning)
            {
                await DelaySafely(IdleDelay, stoppingToken);
                continue;
            }

            BandRankHistoryJobInfo? job = null;
            try
            {
                job = _metaDb.GetNextBandRankHistoryJob();
                if (job is null)
                {
                    await DelaySafely(IdleDelay, stoppingToken);
                    continue;
                }

                if (!_metaDb.TryStartBandRankHistoryJob(job.JobId))
                    continue;

                _log.LogInformation("Band rank-history background job {JobId} started for {BandType} scrape {ScrapeId}.",
                    job.JobId, job.BandType, job.ScrapeId);

                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, _coordinator.BackgroundToken);
                var snapshotOptions = CreateSnapshotOptions(opts);
                var result = _metaDb.SnapshotBandRankHistoryChunked(job.BandType, snapshotOptions, job.JobId, linkedCts.Token);
                _metaDb.CompleteBandRankHistoryJob(job.JobId, result);
                _progress.ReportBandRankHistoryProgress(
                    mode: opts.Mode.ToString(),
                    status: "complete",
                    bandType: job.BandType,
                    rankingScope: null,
                    comboId: null,
                    chunksCompleted: result.ChunksCompleted,
                    chunksTotal: result.ChunksTotal,
                    rowsScanned: result.RowsScanned,
                    rowsInserted: result.RowsInserted,
                    rowsSkipped: result.RowsSkipped,
                    message: "history catch-up complete",
                    updatedAtUtc: DateTime.UtcNow);

                _log.LogInformation(
                    "Band rank-history background job {JobId} completed for {BandType}. chunks={ChunksCompleted}/{ChunksTotal} inserted={RowsInserted:N0} skipped={RowsSkipped:N0} scanned={RowsScanned:N0}.",
                    job.JobId,
                    job.BandType,
                    result.ChunksCompleted,
                    result.ChunksTotal,
                    result.RowsInserted,
                    result.RowsSkipped,
                    result.RowsScanned);
            }
            catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested && job is not null)
            {
                _metaDb.PauseBandRankHistoryJob(job.JobId, "Paused because a scrape started or background work was cancelled.");
                _log.LogInformation("Band rank-history job {JobId} paused for scrape/backpressure.", job.JobId);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                if (job is not null)
                    _metaDb.FailBandRankHistoryJob(job.JobId, ex.Message);
                _log.LogWarning(ex, "Band rank-history background worker failed while processing job {JobId}.", job?.JobId);
                await DelaySafely(ErrorDelay, stoppingToken);
            }
        }
    }

    private static BandRankHistorySnapshotOptions CreateSnapshotOptions(BandRankHistoryOptions opts) => new()
    {
        UseLatestState = opts.UseLatestState,
        UseNarrowHistory = opts.UseNarrowHistory,
        UseWideHistoryCompatibilityWrite = opts.UseWideHistoryCompatibilityWrite,
        SynchronousCommitOff = opts.SynchronousCommitOff,
        CommandTimeoutSeconds = opts.CommandTimeoutSeconds,
        RetentionDays = opts.RetentionDays,
    };

    private static async Task DelaySafely(TimeSpan delay, CancellationToken ct)
    {
        try { await Task.Delay(delay, ct); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }
}
