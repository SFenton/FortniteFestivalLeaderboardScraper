using FSTService.Feedback;

namespace FSTService.Tests.Unit.Feedback;

public sealed class FeedbackSubmissionValidatorTests
{
    private static Dictionary<string, string> BugFields(Action<Dictionary<string, string>>? change = null)
    {
        var fields = new Dictionary<string, string>
        {
            ["kind"] = "bug",
            ["platform"] = "web",
            ["title"] = "[Bug] Leaderboard crashes",
            ["description"] = "It crashes.",
            ["repro"] = "1. Open\r\n2. Tap",
            ["expected"] = "No crash",
        };
        change?.Invoke(fields);
        return fields;
    }

    private static FeedbackValidationError? Validate(
        Dictionary<string, string> fields,
        out FeedbackSubmission? submission,
        IReadOnlyList<FeedbackAttachmentUpload>? attachments = null)
        => FeedbackSubmissionValidator.Validate(fields, attachments ?? [], 4, out submission);

    [Fact]
    public void ValidBug_NormalizesFields()
    {
        var error = Validate(BugFields(), out var submission);

        Assert.Null(error);
        Assert.NotNull(submission);
        Assert.Equal(FeedbackKind.Bug, submission!.Kind);
        Assert.Equal("[Bug] Leaderboard crashes", submission.Title);
        Assert.Equal("1. Open\n2. Tap", submission.Repro);
    }

    [Theory]
    [InlineData("Leaderboard crashes", "[Bug] Leaderboard crashes")]
    [InlineData("[bug]Leaderboard crashes", "[Bug] Leaderboard crashes")]
    [InlineData("[Feature] [Bug]  Leaderboard\ncrashes ", "[Bug] Leaderboard crashes")]
    public void Title_GetsKindPrefix(string raw, string expected)
        => Assert.Equal(expected, FeedbackSubmissionValidator.NormalizeTitle(FeedbackKind.Bug, raw));

    [Fact]
    public void FeatureTitle_ReplacesBugTag()
        => Assert.Equal("[Feature] Dark mode", FeedbackSubmissionValidator.NormalizeTitle(FeedbackKind.Feature, "[Bug] Dark mode"));

    [Theory]
    [InlineData("[Bug] ")]
    [InlineData("  ")]
    [InlineData("[Bug][Feature]")]
    public void PrefixOnlyTitle_IsRejected(string title)
    {
        var error = Validate(BugFields(f => f["title"] = title), out _);
        Assert.Equal("title_required", error?.Code);
    }

    [Theory]
    [InlineData("kind", "question", "invalid_kind")]
    [InlineData("platform", "playstation", "invalid_platform")]
    [InlineData("description", "   ", "description_required")]
    public void InvalidField_ReturnsCode(string field, string value, string code)
    {
        var error = Validate(BugFields(f => f[field] = value), out var submission);
        Assert.Equal(code, error?.Code);
        Assert.Null(submission);
    }

    [Fact]
    public void MissingKind_IsRejected()
    {
        var error = Validate(BugFields(f => f.Remove("kind")), out _);
        Assert.Equal("invalid_kind", error?.Code);
    }

    [Theory]
    [InlineData("title", 201)]
    [InlineData("description", 10_001)]
    [InlineData("repro", 10_001)]
    [InlineData("appVersion", 65)]
    [InlineData("clientInfo", 257)]
    public void OverlongField_IsRejected(string field, int length)
    {
        var error = Validate(BugFields(f => f[field] = "[Bug] " + new string('a', length)), out _);
        Assert.Equal("field_too_long", error?.Code);
    }

    [Fact]
    public void Feature_IgnoresBugOnlyFields()
    {
        var error = Validate(BugFields(f =>
        {
            f["kind"] = "feature";
            f["title"] = "Dark mode";
        }), out var submission);

        Assert.Null(error);
        Assert.Equal("[Feature] Dark mode", submission!.Title);
        Assert.Null(submission.Repro);
        Assert.Null(submission.Expected);
    }

    [Theory]
    [InlineData("web")]
    [InlineData("ios")]
    [InlineData("iphone-duo")]
    [InlineData("ipados")]
    [InlineData("macos")]
    [InlineData("android")]
    [InlineData("windows")]
    public void AllSubmittablePlatforms_AreAccepted(string platform)
        => Assert.Null(Validate(BugFields(f => f["platform"] = platform), out _));

    [Fact]
    public void TooManyAttachments_IsRejected()
    {
        var attachments = Enumerable.Range(0, 5)
            .Select(i => new FeedbackAttachmentUpload($"{i}.png", "image/png", "/tmp/x", 1))
            .ToList();
        var error = Validate(BugFields(), out _, attachments);
        Assert.Equal("too_many_attachments", error?.Code);
    }

    [Fact]
    public void NonMediaAttachment_IsRejected()
    {
        var error = Validate(BugFields(), out _, [new FeedbackAttachmentUpload("notes.txt", "text/plain", "/tmp/x", 1)]);
        Assert.Equal("unsupported_media", error?.Code);
    }

    [Fact]
    public void OctetStreamWithMediaExtension_IsAccepted()
    {
        var error = Validate(BugFields(), out var submission,
            [new FeedbackAttachmentUpload("clip.MOV", "application/octet-stream", "/tmp/x", 1)]);
        Assert.Null(error);
        Assert.Single(submission!.Attachments);
    }
}
