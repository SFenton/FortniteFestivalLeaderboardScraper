namespace FSTService;

/// <summary>
/// Configuration for in-app bug reports and feature requests that the service
/// files as GitHub issues. The public switch is <see cref="FeatureOptions.Feedback"/>;
/// these settings only take effect when that flag is on and the target repository
/// and token are configured.
/// </summary>
public sealed class FeedbackOptions
{
    public const string Section = "Feedback";

    /// <summary>Target repository in <c>owner/name</c> form.</summary>
    public string GitHubRepository { get; set; } = "";

    /// <summary>Secret token with issue write access. Supply through the environment only.</summary>
    public string GitHubToken { get; set; } = "";

    public string GitHubApiBaseUrl { get; set; } = "https://api.github.com";

    public string GitHubUploadsBaseUrl { get; set; } = "https://uploads.github.com";

    /// <summary>Prefix of the label applied for the submitting platform, e.g. <c>surface:web</c>.</summary>
    public string PlatformLabelPrefix { get; set; } = "surface:";

    /// <summary>Maximum accepted multipart request size in bytes (default 90 MiB).</summary>
    public long MaxRequestBytes { get; set; } = 90L * 1024 * 1024;

    public int MaxAttachments { get; set; } = 4;

    /// <summary>Largest file GitHub accepts as an issue attachment.</summary>
    public long GitHubAttachmentMaxBytes { get; set; } = 10L * 1024 * 1024;

    /// <summary>Submissions accepted per client IP within <see cref="SubmissionWindowMinutes"/>.</summary>
    public int SubmissionsPerWindow { get; set; } = 5;

    public int SubmissionWindowMinutes { get; set; } = 10;

    /// <summary>Accepted submissions waiting for processing before new ones are refused.</summary>
    public int MaxQueuedSubmissions { get; set; } = 10;

    /// <summary>How long submission status remains queryable.</summary>
    public int StatusRetentionMinutes { get; set; } = 60;

    public string FfmpegPath { get; set; } = "ffmpeg";

    public string FfprobePath { get; set; } = "ffprobe";

    public int TranscodeTimeoutSeconds { get; set; } = 240;

    /// <summary>
    /// Scratch directory for uploaded and transcoded media. Relative paths resolve
    /// below <c>Scraper:DataDirectory</c> so scratch stays on the FST data drive.
    /// </summary>
    public string ScratchDirectory { get; set; } = "feedback-scratch";

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(GitHubToken)
        && IsValidRepository(GitHubRepository);

    internal static bool IsValidRepository(string? repository)
    {
        if (string.IsNullOrWhiteSpace(repository))
            return false;
        var parts = repository.Split('/');
        return parts.Length == 2
               && parts.All(static part => part.Length > 0
                   && part.All(static c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.'));
    }
}
