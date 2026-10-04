using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft.Extensions.Options;

namespace FSTService.Feedback;

public enum FeedbackEnqueueResult
{
    Accepted,
    Busy,
}

/// <summary>
/// Accepts validated submissions, processes them one at a time in the background
/// (media preparation, attachment upload, issue creation), and keeps a short-lived
/// in-memory status record per submission for client polling.
/// </summary>
public sealed class FeedbackSubmissionService : IDisposable
{
    private const string FailedMessage = "We couldn't file your report right now. Please try again later.";

    private readonly Channel<FeedbackJob> _queue =
        Channel.CreateUnbounded<FeedbackJob>(new UnboundedChannelOptions { SingleReader = true });
    private readonly ConcurrentDictionary<string, FeedbackJob> _jobs = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _stopping = new();
    private readonly IOptions<FeedbackOptions> _options;
    private readonly IFeedbackMediaProcessor _mediaProcessor;
    private readonly IFeedbackIssueClient _issueClient;
    private readonly ILogger<FeedbackSubmissionService> _logger;
    private readonly TimeProvider _timeProvider;
    private readonly Task _worker;
    private int _pending;

    public FeedbackSubmissionService(
        IOptions<FeedbackOptions> options,
        IOptions<ScraperOptions> scraperOptions,
        IFeedbackMediaProcessor mediaProcessor,
        IFeedbackIssueClient issueClient,
        ILogger<FeedbackSubmissionService> logger,
        TimeProvider? timeProvider = null)
    {
        _options = options;
        _mediaProcessor = mediaProcessor;
        _issueClient = issueClient;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
        ScratchRoot = ResolveScratchRoot(options.Value, scraperOptions.Value);
        DeleteStaleScratch();
        _worker = Task.Run(RunAsync);
    }

    /// <summary>Absolute directory holding per-submission upload folders.</summary>
    public string ScratchRoot { get; }

    public static string ResolveScratchRoot(FeedbackOptions options, ScraperOptions scraperOptions)
    {
        var configured = string.IsNullOrWhiteSpace(options.ScratchDirectory) ? "feedback-scratch" : options.ScratchDirectory;
        return Path.GetFullPath(Path.IsPathRooted(configured)
            ? configured
            : Path.Combine(scraperOptions.DataDirectory, configured));
    }

    /// <summary>Creates a new submission id and its private upload directory.</summary>
    public (string Id, string Directory) CreateUploadDirectory()
    {
        var id = Guid.NewGuid().ToString("N");
        var directory = Path.Combine(ScratchRoot, id);
        Directory.CreateDirectory(directory);
        return (id, directory);
    }

    public FeedbackEnqueueResult TryEnqueue(string id, string directory, FeedbackSubmission submission)
    {
        PurgeExpired();
        if (Interlocked.Increment(ref _pending) > Math.Max(1, _options.Value.MaxQueuedSubmissions))
        {
            Interlocked.Decrement(ref _pending);
            return FeedbackEnqueueResult.Busy;
        }

        var job = new FeedbackJob(id, directory, submission, _timeProvider.GetUtcNow());
        _jobs[id] = job;
        if (!_queue.Writer.TryWrite(job))
        {
            _jobs.TryRemove(id, out _);
            Interlocked.Decrement(ref _pending);
            return FeedbackEnqueueResult.Busy;
        }

        _logger.LogInformation(
            "Feedback submission {Id} queued: kind={Kind} platform={Platform} attachments={Attachments}",
            id, submission.Kind, submission.Platform, submission.Attachments.Count);
        return FeedbackEnqueueResult.Accepted;
    }

    public FeedbackStatusResponse? GetStatus(string id)
    {
        PurgeExpired();
        return _jobs.TryGetValue(id, out var job) ? job.Snapshot() : null;
    }

    /// <summary>Removes an upload directory that will not be queued.</summary>
    public void DiscardUploadDirectory(string directory) => TryDeleteDirectory(directory);

    private async Task RunAsync()
    {
        try
        {
            await foreach (var job in _queue.Reader.ReadAllAsync(_stopping.Token))
            {
                try
                {
                    await ProcessAsync(job, _stopping.Token);
                }
                catch (OperationCanceledException) when (_stopping.IsCancellationRequested)
                {
                    job.Fail(FailedMessage);
                    return;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Feedback submission {Id} failed unexpectedly.", job.Id);
                    job.Fail(FailedMessage);
                }
                finally
                {
                    Interlocked.Decrement(ref _pending);
                    TryDeleteDirectory(job.Directory);
                }
            }
        }
        catch (OperationCanceledException) when (_stopping.IsCancellationRequested)
        {
        }
    }

    internal async Task ProcessAsync(FeedbackJob job, CancellationToken cancellationToken)
    {
        job.SetStatus(FeedbackJobStatus.Processing);
        var submission = job.Submission;
        var issueAttachments = new List<FeedbackIssueAttachment>();
        var workDirectory = Path.Combine(job.Directory, "out");

        for (var index = 0; index < submission.Attachments.Count; index++)
        {
            var upload = submission.Attachments[index];
            FeedbackPreparedMedia? prepared = null;
            try
            {
                prepared = await _mediaProcessor.PrepareAsync(upload, Path.Combine(workDirectory, index.ToString()), cancellationToken);
                var url = await _issueClient.UploadAttachmentAsync(prepared.FilePath, prepared.FileName, prepared.ContentType, cancellationToken);
                var outcome = prepared.Transcoded ? FeedbackAttachmentOutcome.Transcoded : FeedbackAttachmentOutcome.Attached;
                job.SetAttachment(index, new FeedbackAttachmentStatus(upload.FileName, prepared.Kind, outcome, prepared.Note));
                issueAttachments.Add(new FeedbackIssueAttachment(upload.FileName, prepared.Kind, outcome, url, prepared.Note));
            }
            catch (FeedbackMediaException ex)
            {
                SkipAttachment(job, index, upload, prepared?.Kind, ex.Message, issueAttachments);
            }
            catch (Exception ex) when (ex is FeedbackGitHubException or HttpRequestException
                                           || (ex is TaskCanceledException && !cancellationToken.IsCancellationRequested))
            {
                _logger.LogWarning("Feedback submission {Id} attachment {Index} upload failed: {Error}", job.Id, index, ex.Message);
                SkipAttachment(job, index, upload, prepared?.Kind, "Couldn't upload this file to GitHub.", issueAttachments);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Feedback submission {Id} attachment {Index} could not be processed.", job.Id, index);
                SkipAttachment(job, index, upload, prepared?.Kind, "This file couldn't be processed.", issueAttachments);
            }
        }

        var labels = new List<string>();
        if (!string.IsNullOrWhiteSpace(_options.Value.PlatformLabelPrefix))
            labels.Add(_options.Value.PlatformLabelPrefix + submission.Platform);

        try
        {
            var body = FeedbackIssueComposer.ComposeBody(submission, issueAttachments);
            var title = FeedbackIssueComposer.NeutralizeTitle(submission.Title);
            var number = await _issueClient.CreateIssueAsync(title, body, labels, cancellationToken);
            job.Submit(number);
            _logger.LogInformation("Feedback submission {Id} filed as issue #{Number}.", job.Id, number);
        }
        catch (Exception ex) when (ex is FeedbackGitHubException or HttpRequestException
                                       || (ex is TaskCanceledException && !cancellationToken.IsCancellationRequested))
        {
            _logger.LogError("Feedback submission {Id} could not be filed: {Error}", job.Id, ex.Message);
            job.Fail(FailedMessage);
        }
    }

    private static void SkipAttachment(
        FeedbackJob job,
        int index,
        FeedbackAttachmentUpload upload,
        string? kind,
        string reason,
        List<FeedbackIssueAttachment> issueAttachments)
    {
        var resolvedKind = kind ?? FeedbackJob.GuessKind(upload);
        job.SetAttachment(index, new FeedbackAttachmentStatus(upload.FileName, resolvedKind, FeedbackAttachmentOutcome.Skipped, reason));
        issueAttachments.Add(new FeedbackIssueAttachment(upload.FileName, resolvedKind, FeedbackAttachmentOutcome.Skipped, null,
            "Not attached: " + reason));
    }

    private void PurgeExpired()
    {
        var cutoff = _timeProvider.GetUtcNow() - TimeSpan.FromMinutes(Math.Max(1, _options.Value.StatusRetentionMinutes));
        foreach (var (id, job) in _jobs)
        {
            if (job.IsTerminal && job.CreatedAt < cutoff)
                _jobs.TryRemove(id, out _);
        }
    }

    private void DeleteStaleScratch()
    {
        if (!Directory.Exists(ScratchRoot))
            return;
        var cutoff = DateTime.UtcNow - TimeSpan.FromMinutes(Math.Max(1, _options.Value.StatusRetentionMinutes));
        foreach (var directory in Directory.EnumerateDirectories(ScratchRoot))
        {
            if (Directory.GetLastWriteTimeUtc(directory) < cutoff)
                TryDeleteDirectory(directory);
        }
    }

    private void TryDeleteDirectory(string directory)
    {
        var full = Path.GetFullPath(directory);
        if (!full.StartsWith(ScratchRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            return;
        try
        {
            if (Directory.Exists(full))
                Directory.Delete(full, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning("Feedback scratch directory cleanup failed: {Error}", ex.Message);
        }
    }

    public void Dispose()
    {
        _stopping.Cancel();
        _queue.Writer.TryComplete();
        try
        {
            _worker.Wait(TimeSpan.FromSeconds(5));
        }
        catch (AggregateException)
        {
        }

        _stopping.Dispose();
    }
}

internal sealed class FeedbackJob
{
    private readonly object _gate = new();
    private readonly FeedbackAttachmentStatus[] _attachments;
    private string _status = FeedbackJobStatus.Queued;
    private int? _issueNumber;
    private string? _error;

    public FeedbackJob(string id, string directory, FeedbackSubmission submission, DateTimeOffset createdAt)
    {
        Id = id;
        Directory = directory;
        Submission = submission;
        CreatedAt = createdAt;
        _attachments = submission.Attachments
            .Select(upload => new FeedbackAttachmentStatus(upload.FileName, GuessKind(upload), FeedbackAttachmentOutcome.Pending))
            .ToArray();
    }

    public string Id { get; }
    public string Directory { get; }
    public FeedbackSubmission Submission { get; }
    public DateTimeOffset CreatedAt { get; }

    public bool IsTerminal
    {
        get
        {
            lock (_gate)
                return _status is FeedbackJobStatus.Submitted or FeedbackJobStatus.Failed;
        }
    }

    public static string GuessKind(FeedbackAttachmentUpload upload)
        => upload.ContentType?.StartsWith("video/", StringComparison.OrdinalIgnoreCase) == true ? "video" : "image";

    public void SetStatus(string status)
    {
        lock (_gate)
            _status = status;
    }

    public void SetAttachment(int index, FeedbackAttachmentStatus status)
    {
        lock (_gate)
            _attachments[index] = status;
    }

    public void Submit(int issueNumber)
    {
        lock (_gate)
        {
            _issueNumber = issueNumber;
            _status = FeedbackJobStatus.Submitted;
        }
    }

    public void Fail(string error)
    {
        lock (_gate)
        {
            _error = error;
            _status = FeedbackJobStatus.Failed;
        }
    }

    public FeedbackStatusResponse Snapshot()
    {
        lock (_gate)
            return new FeedbackStatusResponse(Id, _status, _issueNumber, _error, _attachments.ToArray());
    }
}
