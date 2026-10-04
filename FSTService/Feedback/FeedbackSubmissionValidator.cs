using System.Text.RegularExpressions;

namespace FSTService.Feedback;

public sealed record FeedbackValidationError(string Code, string Message);

/// <summary>Validates and normalizes submitted form fields into a <see cref="FeedbackSubmission"/>.</summary>
public static partial class FeedbackSubmissionValidator
{
    public const int MaxTitleLength = 200;
    public const int MaxTextLength = 10_000;
    public const int MaxAppVersionLength = 64;
    public const int MaxClientInfoLength = 256;

    public static string TitlePrefix(FeedbackKind kind)
        => kind == FeedbackKind.Bug ? "[Bug]" : "[Feature]";

    public static bool TryParseKind(string? value, out FeedbackKind kind)
    {
        switch (value)
        {
            case "bug":
                kind = FeedbackKind.Bug;
                return true;
            case "feature":
                kind = FeedbackKind.Feature;
                return true;
            default:
                kind = default;
                return false;
        }
    }

    public static FeedbackValidationError? Validate(
        IReadOnlyDictionary<string, string> fields,
        IReadOnlyList<FeedbackAttachmentUpload> attachments,
        int maxAttachments,
        out FeedbackSubmission? submission)
    {
        submission = null;
        string? Field(string name) => fields.TryGetValue(name, out var value) ? value : null;

        if (!TryParseKind(Field("kind"), out var kind))
            return new("invalid_kind", "kind must be \"bug\" or \"feature\".");

        var platform = Field("platform");
        if (!FeedbackPlatforms.TryGetLabel(platform, out _))
        {
            return new(
                "invalid_platform",
                "platform must be one of: " + string.Join(", ", FeedbackPlatforms.Submittable.Select(p => p.Value)) + ".");
        }

        var rawTitle = Field("title") ?? "";
        if (rawTitle.Length > MaxTitleLength)
            return TooLong("title", MaxTitleLength);
        var title = NormalizeTitle(kind, rawTitle);
        if (title is null)
            return new("title_required", "A title is required.");

        var description = NormalizeText(Field("description"));
        if (description is null)
            return new("description_required", "A description is required.");

        string? repro = null;
        string? expected = null;
        if (kind == FeedbackKind.Bug)
        {
            repro = NormalizeText(Field("repro"));
            expected = NormalizeText(Field("expected"));
        }

        foreach (var (name, value, max) in new[]
                 {
                     ("description", description, MaxTextLength),
                     ("repro", repro, MaxTextLength),
                     ("expected", expected, MaxTextLength),
                 })
        {
            if (value is not null && value.Length > max)
                return TooLong(name, max);
        }

        var appVersion = NormalizeSingleLine(Field("appVersion"));
        if (appVersion is not null && appVersion.Length > MaxAppVersionLength)
            return TooLong("appVersion", MaxAppVersionLength);
        var clientInfo = NormalizeSingleLine(Field("clientInfo"));
        if (clientInfo is not null && clientInfo.Length > MaxClientInfoLength)
            return TooLong("clientInfo", MaxClientInfoLength);

        if (attachments.Count > maxAttachments)
            return new("too_many_attachments", $"At most {maxAttachments} attachments are allowed.");
        foreach (var attachment in attachments)
        {
            if (!IsMediaContentType(attachment.ContentType, attachment.FileName))
                return new("unsupported_media", $"{attachment.FileName} is not an image or video.");
        }

        submission = new FeedbackSubmission(
            kind,
            platform!,
            title,
            description,
            repro,
            expected,
            appVersion,
            clientInfo,
            attachments);
        return null;
    }

    /// <summary>
    /// Applies the kind's prefix, replacing any existing leading [Bug]/[Feature] tag.
    /// Returns null when nothing remains beyond the prefix.
    /// </summary>
    public static string? NormalizeTitle(FeedbackKind kind, string rawTitle)
    {
        var singleLine = NormalizeSingleLine(rawTitle) ?? "";
        var remainder = LeadingTagRegex().Replace(singleLine, "").Trim();
        return remainder.Length == 0 ? null : $"{TitlePrefix(kind)} {remainder}";
    }

    internal static bool IsMediaContentType(string? contentType, string fileName)
    {
        if (contentType is not null
            && (contentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase)
                || contentType.StartsWith("video/", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        // Some native pickers send application/octet-stream; fall back to the extension.
        return FeedbackMediaSniffer.IsKnownMediaExtension(Path.GetExtension(fileName));
    }

    private static FeedbackValidationError TooLong(string field, int max)
        => new("field_too_long", $"{field} must be at most {max} characters.");

    private static string? NormalizeText(string? value)
    {
        if (value is null)
            return null;
        var normalized = value.Replace("\r\n", "\n").Replace('\r', '\n').Trim();
        return normalized.Length == 0 ? null : normalized;
    }

    private static string? NormalizeSingleLine(string? value)
    {
        if (value is null)
            return null;
        var normalized = WhitespaceRunRegex().Replace(value, " ").Trim();
        return normalized.Length == 0 ? null : normalized;
    }

    [GeneratedRegex(@"^(\s*\[(bug|feature)\]\s*)+", RegexOptions.IgnoreCase)]
    private static partial Regex LeadingTagRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRunRegex();
}
