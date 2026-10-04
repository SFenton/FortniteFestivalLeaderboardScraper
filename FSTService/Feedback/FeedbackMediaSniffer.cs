using System.Text;

namespace FSTService.Feedback;

/// <summary>Container/codec family detected from file magic bytes, never from client-supplied names.</summary>
public enum FeedbackMediaFormat
{
    Unknown,
    Png,
    Jpeg,
    Gif,
    Webp,
    Bmp,
    Tiff,
    Heif,
    IsoVideo,
    WebM,
    Matroska,
    Avi,
}

public static class FeedbackMediaSniffer
{
    private static readonly HashSet<string> KnownExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".gif", ".webp", ".bmp", ".tif", ".tiff", ".heic", ".heif", ".avif",
        ".mp4", ".m4v", ".mov", ".3gp", ".webm", ".mkv", ".avi",
    };

    private static readonly HashSet<string> HeifBrands = new(StringComparer.Ordinal)
    {
        "heic", "heix", "heim", "heis", "hevc", "hevx", "mif1", "msf1", "avif", "avis",
    };

    public static bool IsKnownMediaExtension(string? extension)
        => extension is not null && KnownExtensions.Contains(extension);

    public static FeedbackMediaFormat Detect(string path)
    {
        Span<byte> header = stackalloc byte[64];
        int read;
        using (var stream = File.OpenRead(path))
            read = stream.ReadAtLeast(header, header.Length, throwOnEndOfStream: false);
        return Detect(header[..read]);
    }

    public static FeedbackMediaFormat Detect(ReadOnlySpan<byte> header)
    {
        if (header.StartsWith(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }))
            return FeedbackMediaFormat.Png;
        if (header.StartsWith(new byte[] { 0xFF, 0xD8, 0xFF }))
            return FeedbackMediaFormat.Jpeg;
        if (header.StartsWith("GIF87a"u8) || header.StartsWith("GIF89a"u8))
            return FeedbackMediaFormat.Gif;
        if (header.Length >= 12 && header.StartsWith("RIFF"u8))
        {
            var type = header.Slice(8, 4);
            if (type.SequenceEqual("WEBP"u8))
                return FeedbackMediaFormat.Webp;
            if (type.SequenceEqual("AVI "u8))
                return FeedbackMediaFormat.Avi;
            return FeedbackMediaFormat.Unknown;
        }
        if (header.StartsWith("II*\0"u8) || header.StartsWith("MM\0*"u8))
            return FeedbackMediaFormat.Tiff;
        if (header.Length >= 12 && header.Slice(4, 4).SequenceEqual("ftyp"u8))
        {
            var brand = Encoding.ASCII.GetString(header.Slice(8, 4));
            return HeifBrands.Contains(brand) ? FeedbackMediaFormat.Heif : FeedbackMediaFormat.IsoVideo;
        }
        if (header.StartsWith(new byte[] { 0x1A, 0x45, 0xDF, 0xA3 }))
        {
            return header.IndexOf("webm"u8) >= 0 ? FeedbackMediaFormat.WebM : FeedbackMediaFormat.Matroska;
        }
        if (header.StartsWith("BM"u8) && header.Length >= 14)
            return FeedbackMediaFormat.Bmp;
        return FeedbackMediaFormat.Unknown;
    }

    public static bool IsVideo(FeedbackMediaFormat format)
        => format is FeedbackMediaFormat.IsoVideo or FeedbackMediaFormat.WebM
            or FeedbackMediaFormat.Matroska or FeedbackMediaFormat.Avi;

    /// <summary>Formats GitHub accepts as issue attachments without conversion.</summary>
    public static bool IsGitHubNative(FeedbackMediaFormat format)
        => format is FeedbackMediaFormat.Png or FeedbackMediaFormat.Jpeg or FeedbackMediaFormat.Gif
            or FeedbackMediaFormat.IsoVideo or FeedbackMediaFormat.WebM;

    /// <summary>The single ffmpeg demuxer allowed for a detected format.</summary>
    public static string? FfmpegDemuxer(FeedbackMediaFormat format) => format switch
    {
        FeedbackMediaFormat.Png => "png_pipe",
        FeedbackMediaFormat.Jpeg => "jpeg_pipe",
        FeedbackMediaFormat.Gif => "gif",
        FeedbackMediaFormat.Webp => "webp_pipe",
        FeedbackMediaFormat.Bmp => "bmp_pipe",
        FeedbackMediaFormat.Tiff => "tiff_pipe",
        FeedbackMediaFormat.Heif or FeedbackMediaFormat.IsoVideo => "mov",
        FeedbackMediaFormat.WebM or FeedbackMediaFormat.Matroska => "matroska",
        FeedbackMediaFormat.Avi => "avi",
        _ => null,
    };

    public static (string Extension, string ContentType) NativeFileType(FeedbackMediaFormat format) => format switch
    {
        FeedbackMediaFormat.Png => (".png", "image/png"),
        FeedbackMediaFormat.Jpeg => (".jpg", "image/jpeg"),
        FeedbackMediaFormat.Gif => (".gif", "image/gif"),
        FeedbackMediaFormat.WebM => (".webm", "video/webm"),
        _ => (".mp4", "video/mp4"),
    };
}
