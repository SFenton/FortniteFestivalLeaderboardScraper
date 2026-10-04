using FSTService.Feedback;

namespace FSTService.Tests.Unit.Feedback;

public sealed class FeedbackIssueComposerTests
{
    private static FeedbackSubmission Bug(string platform = "ios", string? repro = "1. Open", string? expected = null)
        => new(FeedbackKind.Bug, platform, "[Bug] Crash", "Ping @octocat please", repro, expected, "2.4.0", "iPhone `17`", []);

    [Fact]
    public void BugBody_MatchesIssueFormHeadingsAndChecksPlatform()
    {
        var body = FeedbackIssueComposer.ComposeBody(Bug(), []);

        Assert.Contains("### What is wrong?\n\n", body);
        Assert.Contains("### Steps to reproduce\n\n1. Open\n\n", body);
        Assert.Contains("### Expected behavior\n\n_No response_\n\n", body);
        Assert.Contains("### Where does it happen?\n\n", body);
        Assert.Contains("- [x] iOS (iPhone)\n", body);
        Assert.Contains("- [ ] Web\n", body);
        Assert.Contains("- [ ] Service (data / API)\n", body);
        Assert.Single(body.Split('\n'), line => line.StartsWith("- [x]", StringComparison.Ordinal));
        Assert.Contains("- Submitted in-app from: iOS (iPhone)\n", body);
        Assert.Contains("- App version: `2.4.0`\n", body);
        Assert.Contains("- Client: `iPhone '17'`\n", body);
    }

    [Fact]
    public void FeatureBody_UsesFeatureFormHeadings()
    {
        var submission = new FeedbackSubmission(FeedbackKind.Feature, "iphone-duo", "[Feature] X", "Please add X", null, null, null, null, []);
        var body = FeedbackIssueComposer.ComposeBody(submission, []);

        Assert.StartsWith("### What would you like?\n\nPlease add X\n\n### Which platforms?\n\n", body);
        Assert.Contains("- [x] iPhone Duo\n", body);
        Assert.DoesNotContain("Steps to reproduce", body);
        Assert.DoesNotContain("App version", body);
    }

    [Fact]
    public void Mentions_AreNeutralized()
    {
        var body = FeedbackIssueComposer.ComposeBody(Bug(), []);
        Assert.DoesNotContain("@octocat", body, StringComparison.Ordinal);
        Assert.Contains("@\u200Boctocat", body, StringComparison.Ordinal);
        Assert.Equal("mail me at a@b.com", FeedbackIssueComposer.NeutralizeMentions("mail me at a@b.com"));
    }

    [Fact]
    public void Attachments_EmbedImagesInlineAndVideosOnOwnLine()
    {
        var body = FeedbackIssueComposer.ComposeBody(Bug(),
        [
            new("shot [1].png", "image", FeedbackAttachmentOutcome.Attached, "https://github.com/user-attachments/assets/a", null),
            new("clip.mov", "video", FeedbackAttachmentOutcome.Transcoded, "https://github.com/user-attachments/assets/b", "Re-encoded."),
            new("broken.heic", "image", FeedbackAttachmentOutcome.Skipped, null, "Not attached: This image couldn't be converted."),
        ]);

        Assert.Contains("### Attachments\n\n![shot (1).png](https://github.com/user-attachments/assets/a)\n\nhttps://github.com/user-attachments/assets/b\n\n", body);
        Assert.Contains("- `clip.mov`: Re-encoded.\n", body);
        Assert.Contains("- `broken.heic`: Not attached: This image couldn't be converted.\n", body);
    }
}
