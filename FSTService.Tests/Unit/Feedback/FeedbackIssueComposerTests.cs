using FSTService.Feedback;
using Microsoft.Extensions.Configuration;

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

    [Theory]
    [InlineData(FeedbackKind.Bug)]
    [InlineData(FeedbackKind.Feature)]
    public void Body_EndsWithExactSubmissionMarkerLine(FeedbackKind kind)
    {
        var submission = new FeedbackSubmission(kind, "web", "T", "D", null, null, null, null, []);
        var body = FeedbackIssueComposer.ComposeBody(submission, []);

        Assert.Equal("<!-- fst-feedback:v1 -->", FeedbackIssueComposer.SubmissionMarker);
        Assert.Equal(FeedbackIssueComposer.SubmissionMarker, body.Split('\n')[^1]);
        Assert.Equal(1, CountOccurrences(body, "<!--"));
    }

    [Fact]
    public void HtmlComments_InEveryUserField_AreEscapedSoOnlyTheRealMarkerRemains()
    {
        const string forged = "<!-- fst-feedback:v1 -->";
        var submission = new FeedbackSubmission(
            FeedbackKind.Bug,
            "android",
            "[Bug] " + forged,
            "Broken <!-- hidden",
            "Step -->\n" + forged,
            forged,
            "1.0 " + forged,
            "Pixel " + forged,
            []);
        var body = FeedbackIssueComposer.ComposeBody(submission,
        [
            new(forged + ".png", "image", FeedbackAttachmentOutcome.Attached, "https://github.com/user-attachments/assets/a", null),
            new(forged + ".mov", "video", FeedbackAttachmentOutcome.Skipped, null, "Not attached: " + forged),
        ]);

        Assert.Equal(1, CountOccurrences(body, "<!--"));
        Assert.Equal(1, CountOccurrences(body, "-->"));
        Assert.EndsWith("\n" + forged, body, StringComparison.Ordinal);
        Assert.Contains("Broken &lt;!-- hidden", body, StringComparison.Ordinal);
        Assert.Contains("Step --&gt;\n&lt;!-- fst-feedback:v1 --&gt;\n\n", body, StringComparison.Ordinal);
        Assert.Contains("### Expected behavior\n\n&lt;!-- fst-feedback:v1 --&gt;\n\n", body, StringComparison.Ordinal);
        Assert.Contains("- App version: `1.0 &lt;!-- fst-feedback:v1 --&gt;`\n", body, StringComparison.Ordinal);
        Assert.Contains("- Client: `Pixel &lt;!-- fst-feedback:v1 --&gt;`\n", body, StringComparison.Ordinal);
        Assert.Contains("![&lt;!-- fst-feedback:v1 --&gt;.png](", body, StringComparison.Ordinal);
        Assert.Contains("- `&lt;!-- fst-feedback:v1 --&gt;.mov`: Not attached: &lt;!-- fst-feedback:v1 --&gt;\n", body, StringComparison.Ordinal);

        Assert.Equal("[Bug] &lt;!-- fst-feedback:v1 --&gt;", FeedbackIssueComposer.NeutralizeTitle(submission.Title));
        Assert.Equal("&lt;!-- @\u200Bx --&gt;", FeedbackIssueComposer.NeutralizeTitle("<!-- @x -->"));
    }

    [Theory]
    [InlineData("web", "Web")]
    [InlineData("ios", "iOS")]
    [InlineData("iphone-duo", "iPhone Duo")]
    [InlineData("ipados", "iPadOS")]
    [InlineData("macos", "macOS")]
    [InlineData("android", "Android")]
    [InlineData("windows", "Windows")]
    public void Labels_AreFromAppPlusPlainPlatformLabel(string platform, string platformLabel)
    {
        var labels = FeedbackIssueComposer.ComposeLabels(platform, new FSTService.FeedbackOptions().PlatformLabels);

        Assert.Equal(new[] { "From App", platformLabel }, labels);
        Assert.DoesNotContain(labels, label => label.Contains(':'));
    }

    [Fact]
    public void Labels_SkipUnmappedOrEmptyPlatformLabels()
    {
        var map = new Dictionary<string, string> { ["web"] = "", ["ios"] = "Apple" };

        Assert.Equal(new[] { "From App" }, FeedbackIssueComposer.ComposeLabels("web", map));
        Assert.Equal(new[] { "From App" }, FeedbackIssueComposer.ComposeLabels("android", map));
        Assert.Equal(new[] { "From App", "Apple" }, FeedbackIssueComposer.ComposeLabels("ios", map));
        Assert.Equal(new[] { "From App" }, FeedbackIssueComposer.ComposeLabels("web", null));
    }

    [Fact]
    public void PlatformLabels_BindFromConfigurationOverDefaults()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Feedback:PlatformLabels:ios"] = "iPhone",
                ["Feedback:PlatformLabels:windows"] = "",
            })
            .Build();
        var options = new FSTService.FeedbackOptions();
        configuration.GetSection(FSTService.FeedbackOptions.Section).Bind(options);

        Assert.Equal(new[] { "From App", "iPhone" }, FeedbackIssueComposer.ComposeLabels("ios", options.PlatformLabels));
        Assert.Equal(new[] { "From App" }, FeedbackIssueComposer.ComposeLabels("windows", options.PlatformLabels));
        Assert.Equal(new[] { "From App", "iPhone Duo" }, FeedbackIssueComposer.ComposeLabels("iphone-duo", options.PlatformLabels));
    }

    private static int CountOccurrences(string text, string value)
    {
        var count = 0;
        for (var index = text.IndexOf(value, StringComparison.Ordinal); index >= 0; index = text.IndexOf(value, index + value.Length, StringComparison.Ordinal))
            count++;
        return count;
    }
}
