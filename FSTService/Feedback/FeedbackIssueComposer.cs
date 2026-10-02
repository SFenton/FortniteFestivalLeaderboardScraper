using System.Text;
using System.Text.RegularExpressions;

namespace FSTService.Feedback;

/// <summary>An attachment after media processing and upload; <see cref="Url"/> is null when skipped.</summary>
public sealed record FeedbackIssueAttachment(
    string Name,
    string Kind,
    string Outcome,
    string? Url,
    string? Note);

/// <summary>
/// Builds the issue body in the same shape as the tracker's bug/feature issue forms
/// (same headings and platform checkboxes) so triage parses in-app reports the same way.
/// </summary>
public static partial class FeedbackIssueComposer
{
    public const string NoResponse = "_No response_";

    public static string ComposeBody(FeedbackSubmission submission, IReadOnlyList<FeedbackIssueAttachment> attachments)
    {
        FeedbackPlatforms.TryGetLabel(submission.Platform, out var platformLabel);
        var body = new StringBuilder();

        if (submission.Kind == FeedbackKind.Bug)
        {
            AppendSection(body, "What is wrong?", submission.Description);
            AppendSection(body, "Steps to reproduce", submission.Repro);
            AppendSection(body, "Expected behavior", submission.Expected);
            body.Append("### Where does it happen?\n\n");
        }
        else
        {
            AppendSection(body, "What would you like?", submission.Description);
            body.Append("### Which platforms?\n\n");
        }

        foreach (var label in FeedbackPlatforms.FormCheckboxLabels)
        {
            var check = label == platformLabel ? "x" : " ";
            body.Append("- [").Append(check).Append("] ").Append(label).Append('\n');
        }
        body.Append('\n');

        if (attachments.Count > 0)
        {
            body.Append("### Attachments\n\n");
            foreach (var attachment in attachments)
            {
                if (attachment.Url is null)
                    continue;
                if (attachment.Kind == "image")
                    body.Append("![").Append(EscapeLinkText(attachment.Name)).Append("](").Append(attachment.Url).Append(")\n\n");
                else
                    body.Append(attachment.Url).Append("\n\n");
            }

            var notes = attachments.Where(static a => a.Note is not null).ToList();
            foreach (var attachment in notes)
                body.Append("- ").Append(InlineCode(attachment.Name)).Append(": ").Append(attachment.Note).Append('\n');
            if (notes.Count > 0)
                body.Append('\n');
        }

        body.Append("### Submission details\n\n");
        body.Append("- Submitted in-app from: ").Append(platformLabel).Append('\n');
        if (submission.AppVersion is not null)
            body.Append("- App version: ").Append(InlineCode(submission.AppVersion)).Append('\n');
        if (submission.ClientInfo is not null)
            body.Append("- Client: ").Append(InlineCode(submission.ClientInfo)).Append('\n');

        return body.ToString().TrimEnd() + "\n";
    }

    private static void AppendSection(StringBuilder body, string heading, string? text)
    {
        body.Append("### ").Append(heading).Append("\n\n");
        body.Append(text is null ? NoResponse : NeutralizeMentions(text)).Append("\n\n");
    }

    /// <summary>Prevents user text from pinging GitHub users or teams.</summary>
    public static string NeutralizeMentions(string text)
        => MentionRegex().Replace(text, "@\u200B");

    public static string NeutralizeTitle(string title) => NeutralizeMentions(title);

    private static string InlineCode(string value)
        => "`" + value.Replace("`", "'") + "`";

    private static string EscapeLinkText(string value)
        => value.Replace("[", "(").Replace("]", ")").Replace("\n", " ");

    [GeneratedRegex(@"(?<![\w@`])@(?=[A-Za-z0-9])")]
    private static partial Regex MentionRegex();
}
