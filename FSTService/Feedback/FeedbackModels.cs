namespace FSTService.Feedback;

public enum FeedbackKind
{
    Bug,
    Feature,
}

/// <summary>Platforms that can submit in-app feedback, in issue-form checkbox order.</summary>
public static class FeedbackPlatforms
{
    public const string NotSure = "Not sure";

    /// <summary>Wire value to the matching tracker issue-form checkbox label.</summary>
    public static readonly IReadOnlyList<(string Value, string Label)> Submittable =
    [
        ("web", "Web"),
        ("ios", "iOS (iPhone)"),
        ("iphone-duo", "iPhone Duo"),
        ("ipados", "iPadOS"),
        ("macos", "macOS"),
        ("android", "Android"),
        ("windows", "Windows"),
    ];

    /// <summary>All checkbox labels of the tracker issue forms, in order.</summary>
    public static readonly IReadOnlyList<string> FormCheckboxLabels =
    [
        "Service (data / API)",
        "Web",
        "iOS (iPhone)",
        "iPhone Duo",
        "iPadOS",
        "macOS",
        "Android",
        "Windows",
        NotSure,
    ];

    public static bool TryGetLabel(string? value, out string label)
    {
        foreach (var (candidate, candidateLabel) in Submittable)
        {
            if (string.Equals(candidate, value, StringComparison.Ordinal))
            {
                label = candidateLabel;
                return true;
            }
        }

        label = "";
        return false;
    }
}

public sealed record FeedbackAttachmentUpload(
    string FileName,
    string? ContentType,
    string FilePath,
    long Length);

public sealed record FeedbackSubmission(
    FeedbackKind Kind,
    string Platform,
    string Title,
    string Description,
    string? Repro,
    string? Expected,
    string? AppVersion,
    string? ClientInfo,
    IReadOnlyList<FeedbackAttachmentUpload> Attachments);

public static class FeedbackJobStatus
{
    public const string Queued = "queued";
    public const string Processing = "processing";
    public const string Submitted = "submitted";
    public const string Failed = "failed";
}

public static class FeedbackAttachmentOutcome
{
    public const string Pending = "pending";
    public const string Attached = "attached";
    public const string Transcoded = "transcoded";
    public const string Skipped = "skipped";
}

public sealed record FeedbackAttachmentStatus(
    string Name,
    string Kind,
    string Outcome,
    string? Note = null);

public sealed record FeedbackStatusResponse(
    string Id,
    string Status,
    int? IssueNumber,
    string? Error,
    IReadOnlyList<FeedbackAttachmentStatus> Attachments);
