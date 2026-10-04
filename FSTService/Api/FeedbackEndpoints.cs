using System.Text;
using FSTService.Feedback;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;

namespace FSTService.Api;

public static partial class ApiEndpoints
{
    public const string FeedbackRateLimitPolicy = "feedback";

    private const int MaxFeedbackFieldBytes = 64 * 1024;
    private const int MaxFeedbackFieldCount = 32;
    private const int MaxFeedbackFileNameLength = 120;

    public static void MapFeedbackEndpoints(this WebApplication app)
    {
        app.MapPost("/api/feedback", SubmitFeedbackAsync)
            .WithTags("Feedback")
            .RequireRateLimiting(FeedbackRateLimitPolicy);

        app.MapGet("/api/feedback/{id}", (
            string id,
            HttpContext httpContext,
            IOptions<FeatureOptions> features,
            IOptions<FeedbackOptions> feedbackOptions) =>
        {
            httpContext.Response.Headers.CacheControl = "no-store";
            if (!IsFeedbackAvailable(features.Value, feedbackOptions.Value))
                return FeedbackDisabled();
            if (id.Length != 32 || !id.All(char.IsAsciiHexDigitLower))
                return FeedbackNotFound();

            var service = httpContext.RequestServices.GetRequiredService<FeedbackSubmissionService>();
            var status = service.GetStatus(id);
            return status is null ? FeedbackNotFound() : Results.Ok(status);
        })
        .WithTags("Feedback")
        .RequireRateLimiting("public");
    }

    public static bool IsFeedbackAvailable(FeatureOptions features, FeedbackOptions feedback)
        => features.Feedback && feedback.IsConfigured;

    private static async Task<IResult> SubmitFeedbackAsync(
        HttpContext httpContext,
        IOptions<FeatureOptions> features,
        IOptions<FeedbackOptions> feedbackOptions,
        CancellationToken cancellationToken)
    {
        httpContext.Response.Headers.CacheControl = "no-store";
        var options = feedbackOptions.Value;
        if (!IsFeedbackAvailable(features.Value, options))
            return FeedbackDisabled();

        var maxBytes = options.MaxRequestBytes;
        if (httpContext.Request.ContentLength > maxBytes)
            return FeedbackTooLarge(maxBytes);
        if (httpContext.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } sizeFeature)
            sizeFeature.MaxRequestBodySize = maxBytes;

        if (!MediaTypeHeaderValue.TryParse(httpContext.Request.ContentType, out var mediaType)
            || !string.Equals(mediaType.MediaType.Value, "multipart/form-data", StringComparison.OrdinalIgnoreCase))
        {
            return FeedbackBadRequest("invalid_form", "Submit feedback as multipart/form-data.");
        }

        var boundary = HeaderUtilities.RemoveQuotes(mediaType.Boundary).Value;
        if (string.IsNullOrWhiteSpace(boundary) || boundary.Length > 70)
            return FeedbackBadRequest("invalid_form", "The multipart boundary is missing or invalid.");

        var service = httpContext.RequestServices.GetRequiredService<FeedbackSubmissionService>();
        var (id, directory) = service.CreateUploadDirectory();
        var queued = false;
        try
        {
            var fields = new Dictionary<string, string>(StringComparer.Ordinal);
            var attachments = new List<FeedbackAttachmentUpload>();
            long totalFileBytes = 0;
            var reader = new MultipartReader(boundary, httpContext.Request.Body)
            {
                HeadersLengthLimit = 16 * 1024,
                BodyLengthLimit = maxBytes,
            };

            MultipartSection? section;
            while ((section = await reader.ReadNextSectionAsync(cancellationToken)) is not null)
            {
                if (!ContentDispositionHeaderValue.TryParse(section.ContentDisposition, out var disposition))
                    return FeedbackBadRequest("invalid_form", "A form part is missing its Content-Disposition.");

                var name = HeaderUtilities.RemoveQuotes(disposition.Name).Value ?? "";
                if (disposition.IsFileDisposition())
                {
                    if (name != "media")
                    {
                        await section.Body.CopyToAsync(Stream.Null, cancellationToken);
                        continue;
                    }

                    if (attachments.Count >= options.MaxAttachments)
                    {
                        return FeedbackBadRequest(
                            "too_many_attachments",
                            $"At most {options.MaxAttachments} attachments are allowed.");
                    }

                    var path = Path.Combine(directory, $"upload-{attachments.Count}");
                    long length;
                    await using (var file = File.Create(path))
                    {
                        length = await CopyBoundedAsync(section.Body, file, maxBytes - totalFileBytes, cancellationToken);
                    }

                    if (length < 0)
                        return FeedbackTooLarge(maxBytes);
                    if (length == 0)
                        return FeedbackBadRequest("unsupported_media", "An attached file is empty.");
                    totalFileBytes += length;
                    attachments.Add(new FeedbackAttachmentUpload(
                        SanitizeUploadFileName(disposition),
                        section.ContentType,
                        path,
                        length));
                }
                else if (disposition.IsFormDisposition())
                {
                    if (fields.Count >= MaxFeedbackFieldCount)
                        return FeedbackBadRequest("invalid_form", "Too many form fields.");
                    var value = await ReadBoundedFieldAsync(section.Body, cancellationToken);
                    if (value is null)
                        return FeedbackBadRequest("field_too_long", $"{name} is too long.");
                    fields.TryAdd(name, value);
                }
            }

            var error = FeedbackSubmissionValidator.Validate(fields, attachments, options.MaxAttachments, out var submission);
            if (error is not null)
                return FeedbackBadRequest(error.Code, error.Message);

            if (service.TryEnqueue(id, directory, submission!) == FeedbackEnqueueResult.Busy)
            {
                httpContext.Response.Headers.RetryAfter = "60";
                return Results.Json(
                    new { error = "Too many reports are being processed right now. Please try again in a minute.", code = "feedback_busy" },
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            }

            queued = true;
            return Results.Json(new { id, status = FeedbackJobStatus.Queued }, statusCode: StatusCodes.Status202Accepted);
        }
        catch (BadHttpRequestException ex) when (ex.StatusCode == StatusCodes.Status413PayloadTooLarge)
        {
            return FeedbackTooLarge(maxBytes);
        }
        catch (InvalidDataException)
        {
            return FeedbackBadRequest("invalid_form", "The multipart form is malformed or too large.");
        }
        finally
        {
            if (!queued)
                service.DiscardUploadDirectory(directory);
        }
    }

    /// <summary>Copies at most <paramref name="remaining"/> bytes; returns -1 when the source is longer.</summary>
    private static async Task<long> CopyBoundedAsync(Stream source, Stream destination, long remaining, CancellationToken cancellationToken)
    {
        var buffer = new byte[81920];
        long total = 0;
        int read;
        while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
        {
            total += read;
            if (total > remaining)
                return -1;
            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }

        return total;
    }

    private static async Task<string?> ReadBoundedFieldAsync(Stream body, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        var length = await CopyBoundedAsync(body, buffer, MaxFeedbackFieldBytes, cancellationToken);
        return length < 0 ? null : Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
    }

    private static string SanitizeUploadFileName(ContentDispositionHeaderValue disposition)
    {
        var raw = HeaderUtilities.RemoveQuotes(disposition.FileNameStar).Value
                  ?? HeaderUtilities.RemoveQuotes(disposition.FileName).Value
                  ?? "";
        var name = Path.GetFileName(raw.Replace('\\', '/'));
        var cleaned = new string(name.Where(static c => !char.IsControl(c)).ToArray()).Trim();
        if (cleaned.Length > MaxFeedbackFileNameLength)
            cleaned = cleaned[^MaxFeedbackFileNameLength..];
        return cleaned.Length == 0 ? "attachment" : cleaned;
    }

    private static IResult FeedbackDisabled()
        => Results.Json(
            new { error = "In-app feedback is not available.", code = "feedback_disabled" },
            statusCode: StatusCodes.Status404NotFound);

    private static IResult FeedbackNotFound()
        => Results.Json(
            new { error = "Feedback submission not found.", code = "not_found" },
            statusCode: StatusCodes.Status404NotFound);

    private static IResult FeedbackTooLarge(long maxBytes)
        => Results.Json(
            new { error = "The submission is too large.", code = "payload_too_large", maxBytes },
            statusCode: StatusCodes.Status413PayloadTooLarge);

    private static IResult FeedbackBadRequest(string code, string error)
        => Results.Json(new { error, code }, statusCode: StatusCodes.Status400BadRequest);
}
