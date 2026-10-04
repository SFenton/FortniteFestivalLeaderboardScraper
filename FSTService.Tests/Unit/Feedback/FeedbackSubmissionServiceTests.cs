using FSTService.Feedback;
using FSTService;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace FSTService.Tests.Unit.Feedback;

public sealed class FeedbackSubmissionServiceTests : IDisposable
{
    private readonly string _dataDir = Path.Combine(AppContext.BaseDirectory, "test-artifacts", $"feedback_svc_{Guid.NewGuid():N}");
    private readonly IFeedbackMediaProcessor _processor = Substitute.For<IFeedbackMediaProcessor>();
    private readonly IFeedbackIssueClient _issues = Substitute.For<IFeedbackIssueClient>();
    private readonly List<FeedbackSubmissionService> _services = [];

    public void Dispose()
    {
        foreach (var service in _services)
            service.Dispose();
        try { Directory.Delete(_dataDir, recursive: true); } catch (IOException) { }
    }

    private FeedbackSubmissionService CreateService(int maxQueued = 10)
    {
        var service = new FeedbackSubmissionService(
            Options.Create(new FeedbackOptions { MaxQueuedSubmissions = maxQueued }),
            Options.Create(new ScraperOptions { DataDirectory = _dataDir }),
            _processor,
            _issues,
            NullLogger<FeedbackSubmissionService>.Instance);
        _services.Add(service);
        return service;
    }

    private static FeedbackSubmission Submission(string platform, params FeedbackAttachmentUpload[] attachments)
        => new(FeedbackKind.Bug, platform, "[Bug] Crash", "Broken", "Steps", "Works", null, null, attachments);

    private static async Task<FeedbackStatusResponse> WaitForTerminalAsync(FeedbackSubmissionService service, string id)
    {
        for (var i = 0; i < 200; i++)
        {
            var status = service.GetStatus(id);
            if (status?.Status is FeedbackJobStatus.Submitted or FeedbackJobStatus.Failed)
                return status;
            await Task.Delay(25);
        }

        throw new TimeoutException("Feedback job did not finish.");
    }

    private (string Id, FeedbackAttachmentUpload Upload) Stage(FeedbackSubmissionService service, string name, string contentType)
    {
        var (id, directory) = service.CreateUploadDirectory();
        var path = Path.Combine(directory, "0.upload");
        File.WriteAllBytes(path, [1, 2, 3]);
        return (id, new FeedbackAttachmentUpload(name, contentType, path, 3));
    }

    [Fact]
    public async Task Submission_UploadsMediaAndFilesLabeledIssue()
    {
        var service = CreateService();
        var (id, upload) = Stage(service, "clip.mov", "video/quicktime");
        _processor.PrepareAsync(upload, Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new FeedbackPreparedMedia("/scratch/clip.mp4", "clip.mp4", "video/mp4", "video", true, "Re-encoded."));
        _issues.UploadAttachmentAsync("/scratch/clip.mp4", "clip.mp4", "video/mp4", Arg.Any<CancellationToken>())
            .Returns("https://github.com/user-attachments/assets/abc");
        _issues.CreateIssueAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(321);

        Assert.Equal(FeedbackEnqueueResult.Accepted, service.TryEnqueue(id, Path.GetDirectoryName(upload.FilePath)!, Submission("android", upload)));
        var status = await WaitForTerminalAsync(service, id);

        Assert.Equal(FeedbackJobStatus.Submitted, status.Status);
        Assert.Equal(321, status.IssueNumber);
        var attachment = Assert.Single(status.Attachments);
        Assert.Equal(FeedbackAttachmentOutcome.Transcoded, attachment.Outcome);
        Assert.Equal("video", attachment.Kind);
        await _issues.Received(1).CreateIssueAsync(
            "[Bug] Crash",
            Arg.Is<string>(body => body.Contains("https://github.com/user-attachments/assets/abc") && body.Contains("- [x] Android")
                                   && body.EndsWith("\n" + FeedbackIssueComposer.SubmissionMarker, StringComparison.Ordinal)),
            Arg.Is<IReadOnlyList<string>>(labels => labels.SequenceEqual(new[] { "From App", "Android" })),
            Arg.Any<CancellationToken>());
        Assert.False(Directory.Exists(Path.GetDirectoryName(upload.FilePath)));
    }

    [Fact]
    public async Task UnconvertibleOrFailedUploads_AreSkippedButIssueStillFiled()
    {
        var service = CreateService();
        var (id, first) = Stage(service, "photo.heic", "image/heic");
        var second = first with { FileName = "shot.png", ContentType = "image/png" };
        _processor.PrepareAsync(first, Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Throws(new FeedbackMediaException("This image couldn't be converted."));
        _processor.PrepareAsync(second, Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new FeedbackPreparedMedia("/scratch/shot.png", "shot.png", "image/png", "image", false, null));
        _issues.UploadAttachmentAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Throws(new FeedbackGitHubException("upload failed", System.Net.HttpStatusCode.BadGateway));
        _issues.CreateIssueAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(7);

        service.TryEnqueue(id, Path.GetDirectoryName(first.FilePath)!, Submission("web", first, second));
        var status = await WaitForTerminalAsync(service, id);

        Assert.Equal(FeedbackJobStatus.Submitted, status.Status);
        Assert.All(status.Attachments, a => Assert.Equal(FeedbackAttachmentOutcome.Skipped, a.Outcome));
        Assert.Equal("This image couldn't be converted.", status.Attachments[0].Note);
        Assert.Equal("Couldn't upload this file to GitHub.", status.Attachments[1].Note);
        await _issues.Received(1).CreateIssueAsync(Arg.Any<string>(),
            Arg.Is<string>(body => body.Contains("`photo.heic`: Not attached: This image couldn't be converted.")),
            Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task IssueCreationFailure_ReportsGenericError()
    {
        var service = CreateService();
        var (id, directory) = service.CreateUploadDirectory();
        _issues.CreateIssueAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Throws(new FeedbackGitHubException("Bad credentials", System.Net.HttpStatusCode.Unauthorized));

        service.TryEnqueue(id, directory, Submission("windows"));
        var status = await WaitForTerminalAsync(service, id);

        Assert.Equal(FeedbackJobStatus.Failed, status.Status);
        Assert.Null(status.IssueNumber);
        Assert.DoesNotContain("credentials", status.Error);
    }

    [Fact]
    public async Task UnexpectedFailure_FailsJobButKeepsWorkerAlive()
    {
        var service = CreateService();
        var (badId, badDir) = service.CreateUploadDirectory();
        _issues.CreateIssueAsync("[Bug] Crash", Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns<int>(_ => throw new InvalidOperationException("boom"), _ => 9);

        service.TryEnqueue(badId, badDir, Submission("macos"));
        Assert.Equal(FeedbackJobStatus.Failed, (await WaitForTerminalAsync(service, badId)).Status);

        var (goodId, goodDir) = service.CreateUploadDirectory();
        service.TryEnqueue(goodId, goodDir, Submission("macos"));
        var good = await WaitForTerminalAsync(service, goodId);
        Assert.Equal(FeedbackJobStatus.Submitted, good.Status);
        Assert.Equal(9, good.IssueNumber);
    }

    [Fact]
    public void Queue_RejectsWhenFull()
    {
        var gate = new TaskCompletionSource<int>();
        _issues.CreateIssueAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(gate.Task);
        var service = CreateService(maxQueued: 1);

        var (first, firstDir) = service.CreateUploadDirectory();
        var (second, secondDir) = service.CreateUploadDirectory();
        Assert.Equal(FeedbackEnqueueResult.Accepted, service.TryEnqueue(first, firstDir, Submission("ios")));
        Assert.Equal(FeedbackEnqueueResult.Busy, service.TryEnqueue(second, secondDir, Submission("ios")));
        Assert.Null(service.GetStatus(second));
        gate.SetResult(1);
    }

    [Fact]
    public void ScratchRoot_ResolvesUnderDataDirectory()
    {
        var root = FeedbackSubmissionService.ResolveScratchRoot(new FeedbackOptions(), new ScraperOptions { DataDirectory = "/data" });
        Assert.Equal("/data/feedback-scratch", root);
        Assert.Equal("/abs/x", FeedbackSubmissionService.ResolveScratchRoot(new FeedbackOptions { ScratchDirectory = "/abs/x" }, new ScraperOptions()));
    }
}
