using System.Diagnostics;
using System.Globalization;
using System.Text;
using Microsoft.Extensions.Options;

namespace FSTService.Feedback;

/// <summary>A media file ready for upload to GitHub.</summary>
public sealed record FeedbackPreparedMedia(
    string FilePath,
    string FileName,
    string ContentType,
    string Kind,
    bool Transcoded,
    string? Note);

/// <summary>Media that cannot be attached; <see cref="Exception.Message"/> is safe to show users.</summary>
public sealed class FeedbackMediaException(string message) : Exception(message);

public interface IFeedbackMediaProcessor
{
    /// <summary>
    /// Strips location/EXIF metadata and converts or shrinks the upload so GitHub accepts it.
    /// Writes outputs inside <paramref name="workDirectory"/>.
    /// </summary>
    Task<FeedbackPreparedMedia> PrepareAsync(
        FeedbackAttachmentUpload upload,
        string workDirectory,
        CancellationToken cancellationToken);
}

public sealed class FfmpegFeedbackMediaProcessor(
    IOptions<FeedbackOptions> options,
    ILogger<FfmpegFeedbackMediaProcessor> logger) : IFeedbackMediaProcessor
{
    private const double BitBudgetFraction = 0.9;
    private const long AudioBitsPerSecond = 64_000;
    private const long MinVideoBitsPerSecond = 250_000;
    private const long MaxVideoBitsPerSecond = 4_000_000;

    private static readonly (int LongSide, int Quality)[] ImageLadder =
    [
        (4096, 2),
        (2560, 3),
        (1920, 5),
        (1280, 7),
        (960, 10),
    ];

    private FeedbackOptions Options => options.Value;

    public async Task<FeedbackPreparedMedia> PrepareAsync(
        FeedbackAttachmentUpload upload,
        string workDirectory,
        CancellationToken cancellationToken)
    {
        var format = FeedbackMediaSniffer.Detect(upload.FilePath);
        if (FeedbackMediaSniffer.FfmpegDemuxer(format) is null)
            throw new FeedbackMediaException("This file isn't a supported image or video.");

        var baseName = SafeBaseName(upload.FileName);
        var limit = Options.GitHubAttachmentMaxBytes;
        Directory.CreateDirectory(workDirectory);

        switch (format)
        {
            case FeedbackMediaFormat.Jpeg:
                return await PrepareJpegAsync(upload, baseName, workDirectory, limit, cancellationToken);
            case FeedbackMediaFormat.Png:
            {
                var stripped = Path.Combine(workDirectory, baseName + "-clean.png");
                if (FeedbackMetadataStripper.StripPng(upload.FilePath, stripped) && new FileInfo(stripped).Length <= limit)
                    return new(stripped, baseName + ".png", "image/png", "image", false, null);
                return await ConvertImageAsync(upload.FilePath, format, null, baseName, workDirectory, limit,
                    "Resized to fit GitHub's attachment size limit.", cancellationToken);
            }
            case FeedbackMediaFormat.Gif:
                if (upload.Length <= limit)
                    return new(upload.FilePath, baseName + ".gif", "image/gif", "image", false, null);
                return await ConvertVideoAsync(upload.FilePath, format, baseName, workDirectory, limit,
                    "Converted from GIF to MP4 to fit GitHub's attachment size limit.", cancellationToken);
            case FeedbackMediaFormat.Webp:
            case FeedbackMediaFormat.Bmp:
            case FeedbackMediaFormat.Tiff:
            case FeedbackMediaFormat.Heif:
                return await ConvertImageAsync(upload.FilePath, format, null, baseName, workDirectory, limit,
                    "Converted to JPEG for GitHub.", cancellationToken);
            case FeedbackMediaFormat.IsoVideo:
            case FeedbackMediaFormat.WebM:
            {
                var remuxed = await TryRemuxWithoutMetadataAsync(upload, format, baseName, workDirectory, limit, cancellationToken);
                if (remuxed is not null)
                    return remuxed;
                return await ConvertVideoAsync(upload.FilePath, format, baseName, workDirectory, limit,
                    "Re-encoded to fit GitHub's attachment size limit.", cancellationToken);
            }
            default:
                return await ConvertVideoAsync(upload.FilePath, format, baseName, workDirectory, limit,
                    "Converted to MP4 for GitHub.", cancellationToken);
        }
    }

    private async Task<FeedbackPreparedMedia> PrepareJpegAsync(
        FeedbackAttachmentUpload upload,
        string baseName,
        string workDirectory,
        long limit,
        CancellationToken cancellationToken)
    {
        var stripped = Path.Combine(workDirectory, baseName + "-clean.jpg");
        var orientation = FeedbackMetadataStripper.StripJpeg(upload.FilePath, stripped);
        var orientationFilter = orientation is { } value ? FeedbackMetadataStripper.OrientationFilter(value) : null;
        if (orientation is not null && orientationFilter is null && new FileInfo(stripped).Length <= limit)
            return new(stripped, baseName + ".jpg", "image/jpeg", "image", false, null);

        var oversized = upload.Length > limit;
        var source = orientation is not null ? stripped : upload.FilePath;
        return await ConvertImageAsync(source, FeedbackMediaFormat.Jpeg, orientationFilter, baseName, workDirectory, limit,
            oversized ? "Resized to fit GitHub's attachment size limit." : null, cancellationToken);
    }

    private async Task<FeedbackPreparedMedia> ConvertImageAsync(
        string source,
        FeedbackMediaFormat format,
        string? preFilter,
        string baseName,
        string workDirectory,
        long limit,
        string? note,
        CancellationToken cancellationToken)
    {
        var output = Path.Combine(workDirectory, baseName + "-converted.jpg");
        foreach (var (longSide, quality) in ImageLadder)
        {
            var scale = string.Create(CultureInfo.InvariantCulture,
                $"scale=w='min({longSide},iw)':h='min({longSide},ih)':force_original_aspect_ratio=decrease");
            var filter = preFilter is null ? scale : preFilter + "," + scale;
            var args = InputArgs(format, source)
                .Concat(["-frames:v", "1", "-update", "1", "-vf", filter, "-map_metadata", "-1",
                    "-q:v", quality.ToString(CultureInfo.InvariantCulture), "-f", "image2", output])
                .ToList();
            var result = await RunAsync(Options.FfmpegPath, args, cancellationToken);
            if (result.ExitCode != 0 || !File.Exists(output))
            {
                logger.LogWarning("Feedback image conversion failed: {Error}", Truncate(result.StdErr, 300));
                throw new FeedbackMediaException("This image couldn't be converted.");
            }

            if (new FileInfo(output).Length <= limit)
                return new(output, baseName + ".jpg", "image/jpeg", "image", true, note);
        }

        throw new FeedbackMediaException("This image is too large to attach even after resizing.");
    }

    private async Task<FeedbackPreparedMedia?> TryRemuxWithoutMetadataAsync(
        FeedbackAttachmentUpload upload,
        FeedbackMediaFormat format,
        string baseName,
        string workDirectory,
        long limit,
        CancellationToken cancellationToken)
    {
        if (upload.Length > limit)
            return null;

        var isMov = format == FeedbackMediaFormat.IsoVideo
                    && string.Equals(Path.GetExtension(upload.FileName), ".mov", StringComparison.OrdinalIgnoreCase);
        var (extension, contentType, muxer) = format == FeedbackMediaFormat.WebM
            ? (".webm", "video/webm", "webm")
            : isMov ? (".mov", "video/quicktime", "mov") : (".mp4", "video/mp4", "mp4");
        var output = Path.Combine(workDirectory, baseName + "-clean" + extension);
        var args = InputArgs(format, upload.FilePath)
            .Concat(["-map", "0:v", "-map", "0:a?", "-dn", "-sn", "-c", "copy", "-map_metadata", "-1",
                "-map_chapters", "-1"])
            .Concat(muxer == "webm" ? [] : new[] { "-movflags", "+faststart" })
            .Concat(["-f", muxer, output])
            .ToList();
        var result = await RunAsync(Options.FfmpegPath, args, cancellationToken);
        if (result.ExitCode != 0 || !File.Exists(output) || new FileInfo(output).Length > limit)
        {
            logger.LogInformation("Feedback video remux not usable; re-encoding: {Error}", Truncate(result.StdErr, 300));
            return null;
        }

        return new(output, baseName + extension, contentType, "video", false, null);
    }

    private async Task<FeedbackPreparedMedia> ConvertVideoAsync(
        string source,
        FeedbackMediaFormat format,
        string baseName,
        string workDirectory,
        long limit,
        string note,
        CancellationToken cancellationToken)
    {
        var (duration, hasAudio) = await ProbeAsync(source, format, cancellationToken);
        var audioBps = hasAudio ? AudioBitsPerSecond : 0;
        var plan = PlanVideoEncode(limit, duration, audioBps);
        var output = Path.Combine(workDirectory, baseName + "-converted.mp4");
        double[] factors = [1.0, 0.7, 0.5];
        for (var attempt = 0; attempt < factors.Length; attempt++)
        {
            var isLast = attempt == factors.Length - 1;
            var videoBps = Math.Max(MinVideoBitsPerSecond / 2, (long)(plan.VideoBitsPerSecond * factors[attempt]));
            var longSide = LongSideFor(videoBps);
            var args = InputArgs(format, source).ToList();
            if (plan.TrimSeconds is { } trim)
                args.AddRange(["-t", trim.ToString(CultureInfo.InvariantCulture)]);
            args.AddRange(["-map", "0:v:0", "-map", "0:a:0?", "-dn", "-sn", "-map_metadata", "-1", "-map_chapters", "-1",
                "-vf", string.Create(CultureInfo.InvariantCulture,
                    $"scale=w='min({longSide},iw)':h='min({longSide},ih)':force_original_aspect_ratio=decrease:force_divisible_by=2"),
                "-c:v", "libx264", "-preset", "veryfast", "-pix_fmt", "yuv420p",
                "-b:v", videoBps.ToString(CultureInfo.InvariantCulture),
                "-maxrate", videoBps.ToString(CultureInfo.InvariantCulture),
                "-bufsize", (videoBps * 2).ToString(CultureInfo.InvariantCulture),
                "-c:a", "aac", "-b:a", AudioBitsPerSecond.ToString(CultureInfo.InvariantCulture), "-ac", "2",
                "-movflags", "+faststart"]);
            if (isLast)
                args.AddRange(["-fs", limit.ToString(CultureInfo.InvariantCulture)]);
            args.AddRange(["-f", "mp4", output]);

            var result = await RunAsync(Options.FfmpegPath, args, cancellationToken);
            if (result.ExitCode != 0 || !File.Exists(output))
            {
                logger.LogWarning("Feedback video conversion failed: {Error}", Truncate(result.StdErr, 300));
                throw new FeedbackMediaException("This video couldn't be converted.");
            }

            var size = new FileInfo(output).Length;
            if (size <= limit && size > 0)
            {
                var finalNote = note;
                if (plan.TrimSeconds is { } trimmed)
                    finalNote += string.Create(CultureInfo.InvariantCulture, $" Only the first {trimmed} seconds are included.");
                else if (isLast)
                    finalNote += " The end of the video may be cut off.";
                return new(output, baseName + ".mp4", "video/mp4", "video", true, finalNote);
            }
        }

        throw new FeedbackMediaException("This video is too large to attach even after re-encoding.");
    }

    internal sealed record VideoEncodePlan(long VideoBitsPerSecond, int? TrimSeconds);

    /// <summary>Chooses a bitrate that fits the size limit, trimming only when even the minimum bitrate cannot fit.</summary>
    internal static VideoEncodePlan PlanVideoEncode(long limitBytes, double? durationSeconds, long audioBitsPerSecond)
    {
        var budgetBits = limitBytes * 8 * BitBudgetFraction;
        var maxDuration = budgetBits / (MinVideoBitsPerSecond + audioBitsPerSecond);
        var duration = durationSeconds is > 0 ? durationSeconds.Value : maxDuration;
        int? trim = null;
        if (duration > maxDuration)
        {
            trim = (int)Math.Floor(maxDuration);
            duration = trim.Value;
        }

        var videoBps = (long)(budgetBits / duration) - audioBitsPerSecond;
        videoBps = Math.Clamp(videoBps, MinVideoBitsPerSecond, MaxVideoBitsPerSecond);
        return new VideoEncodePlan(videoBps, trim);
    }

    internal static int LongSideFor(long videoBitsPerSecond) => videoBitsPerSecond switch
    {
        >= 2_500_000 => 1920,
        >= 1_200_000 => 1280,
        >= 600_000 => 960,
        _ => 640,
    };

    private async Task<(double? Duration, bool HasAudio)> ProbeAsync(
        string source,
        FeedbackMediaFormat format,
        CancellationToken cancellationToken)
    {
        var demuxer = FeedbackMediaSniffer.FfmpegDemuxer(format)!;
        var result = await RunAsync(Options.FfprobePath,
        [
            "-v", "error", "-protocol_whitelist", "file", "-f", demuxer,
            "-show_entries", "format=duration:stream=codec_type",
            "-of", "default=noprint_wrappers=1", source,
        ], cancellationToken);
        if (result.ExitCode != 0)
            return (null, true);

        double? duration = null;
        var hasAudio = false;
        foreach (var line in result.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (line.StartsWith("duration=", StringComparison.Ordinal)
                && double.TryParse(line["duration=".Length..], NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
                && parsed > 0)
            {
                duration = parsed;
            }
            else if (line == "codec_type=audio")
            {
                hasAudio = true;
            }
        }

        return (duration, hasAudio);
    }

    private static IEnumerable<string> InputArgs(FeedbackMediaFormat format, string source)
        =>
        [
            "-hide_banner", "-nostdin", "-loglevel", "error", "-y",
            "-protocol_whitelist", "file",
            "-f", FeedbackMediaSniffer.FfmpegDemuxer(format)!,
            "-i", source,
        ];

    internal static string SafeBaseName(string fileName)
    {
        var stem = Path.GetFileNameWithoutExtension(fileName);
        var builder = new StringBuilder();
        foreach (var c in stem)
        {
            if (builder.Length >= 60)
                break;
            builder.Append(char.IsAsciiLetterOrDigit(c) || c is '-' or '_' ? c : '-');
        }

        var cleaned = builder.ToString().Trim('-');
        return cleaned.Length == 0 ? "attachment" : cleaned;
    }

    private async Task<ProcessResult> RunAsync(string fileName, IReadOnlyList<string> args, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo(fileName)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = false,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in args)
            startInfo.ArgumentList.Add(arg);

        using var process = new Process { StartInfo = startInfo };
        try
        {
            process.Start();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            logger.LogError(ex, "Feedback media tool {Tool} could not be started.", fileName);
            throw new FeedbackMediaException("Media processing is unavailable on the server.");
        }

        var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(5, Options.TranscodeTimeoutSeconds)));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
            }

            cancellationToken.ThrowIfCancellationRequested();
            throw new FeedbackMediaException("Processing this file took too long.");
        }

        return new ProcessResult(process.ExitCode, await stdout, await stderr);
    }

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];

    private sealed record ProcessResult(int ExitCode, string StdOut, string StdErr);
}
