using System.Buffers.Binary;
using System.Diagnostics;
using System.Text;
using FSTService.Feedback;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace FSTService.Tests.Unit.Feedback;

public sealed class FeedbackMediaTests : IDisposable
{
    private readonly string _dir = Path.Combine(AppContext.BaseDirectory, "test-artifacts", $"feedback_media_{Guid.NewGuid():N}");

    public FeedbackMediaTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    // ─── Sniffer ────────────────────────────────────────────────

    [Theory]
    [InlineData(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 0 }, FeedbackMediaFormat.Png)]
    [InlineData(new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 0, 0 }, FeedbackMediaFormat.Jpeg)]
    [InlineData(new byte[] { 0x1A, 0x45, 0xDF, 0xA3, 0x77, 0x65, 0x62, 0x6D }, FeedbackMediaFormat.WebM)]
    [InlineData(new byte[] { 0x1A, 0x45, 0xDF, 0xA3, 0, 0, 0, 0 }, FeedbackMediaFormat.Matroska)]
    [InlineData(new byte[] { 0x3C, 0x68, 0x74, 0x6D, 0x6C, 0x3E }, FeedbackMediaFormat.Unknown)]
    [InlineData(new byte[] { }, FeedbackMediaFormat.Unknown)]
    public void Sniffer_DetectsMagicBytes(byte[] header, FeedbackMediaFormat expected)
        => Assert.Equal(expected, FeedbackMediaSniffer.Detect(header));

    [Theory]
    [InlineData("GIF89a", FeedbackMediaFormat.Gif)]
    [InlineData("RIFF\0\0\0\0WEBPVP8 ", FeedbackMediaFormat.Webp)]
    [InlineData("RIFF\0\0\0\0AVI LIST", FeedbackMediaFormat.Avi)]
    [InlineData("RIFF\0\0\0\0WAVEfmt ", FeedbackMediaFormat.Unknown)]
    [InlineData("\0\0\0\u0018ftypqt  ", FeedbackMediaFormat.IsoVideo)]
    [InlineData("\0\0\0\u0018ftypisom", FeedbackMediaFormat.IsoVideo)]
    [InlineData("\0\0\0\u0018ftypheic", FeedbackMediaFormat.Heif)]
    [InlineData("II*\0\u0008\0\0\0", FeedbackMediaFormat.Tiff)]
    [InlineData("BM\0\0\0\0\0\0\0\0\0\0\0\0", FeedbackMediaFormat.Bmp)]
    public void Sniffer_DetectsAsciiSignatures(string header, FeedbackMediaFormat expected)
        => Assert.Equal(expected, FeedbackMediaSniffer.Detect(Encoding.Latin1.GetBytes(header)));

    [Fact]
    public void Sniffer_PinsOneDemuxerPerFormat()
    {
        Assert.Null(FeedbackMediaSniffer.FfmpegDemuxer(FeedbackMediaFormat.Unknown));
        Assert.Equal("mov", FeedbackMediaSniffer.FfmpegDemuxer(FeedbackMediaFormat.Heif));
        Assert.False(FeedbackMediaSniffer.IsVideo(FeedbackMediaFormat.Gif));
        Assert.True(FeedbackMediaSniffer.IsVideo(FeedbackMediaFormat.Avi));
    }

    // ─── Metadata stripping ─────────────────────────────────────

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void StripJpeg_RemovesExifAndCommentsAndReturnsOrientation(bool littleEndian)
    {
        var source = Path.Combine(_dir, "in.jpg");
        var dest = Path.Combine(_dir, "out.jpg");
        File.WriteAllBytes(source, SyntheticJpeg(ExifSegment(6, littleEndian), CommentSegment("GPS secret")));

        var orientation = FeedbackMetadataStripper.StripJpeg(source, dest);

        Assert.Equal(6, orientation);
        var output = File.ReadAllBytes(dest);
        Assert.Equal(-1, output.AsSpan().IndexOf("Exif"u8));
        Assert.Equal(-1, output.AsSpan().IndexOf("GPS secret"u8));
        Assert.True(output.AsSpan().IndexOf("JFIF"u8) >= 0);
        Assert.True(output.AsSpan().EndsWith(new byte[] { 0xFF, 0xD9 }));
    }

    [Fact]
    public void StripJpeg_RejectsMalformedInput()
    {
        var source = Path.Combine(_dir, "bad.jpg");
        File.WriteAllBytes(source, [0xFF, 0xD8, 0x00, 0x00, 0x00, 0x00]);
        Assert.Null(FeedbackMetadataStripper.StripJpeg(source, Path.Combine(_dir, "bad-out.jpg")));
    }

    [Fact]
    public void StripPng_RemovesTextChunks()
    {
        var source = Path.Combine(_dir, "in.png");
        var dest = Path.Combine(_dir, "out.png");
        using (var stream = File.Create(source))
        {
            stream.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
            WritePngChunk(stream, "IHDR", new byte[13]);
            WritePngChunk(stream, "tEXt", "Comment\0secret location"u8.ToArray());
            WritePngChunk(stream, "IDAT", [1, 2, 3]);
            WritePngChunk(stream, "IEND", []);
        }

        Assert.True(FeedbackMetadataStripper.StripPng(source, dest));
        var output = File.ReadAllBytes(dest);
        Assert.Equal(-1, output.AsSpan().IndexOf("secret"u8));
        Assert.True(output.AsSpan().IndexOf("IDAT"u8) > 0);
        Assert.True(output.AsSpan().IndexOf("IEND"u8) > 0);
    }

    [Theory]
    [InlineData(1, null)]
    [InlineData(3, "hflip,vflip")]
    [InlineData(6, "transpose=1")]
    [InlineData(8, "transpose=2")]
    public void OrientationFilter_MapsExifValues(int orientation, string? expected)
        => Assert.Equal(expected, FeedbackMetadataStripper.OrientationFilter(orientation));

    // ─── Encode planning ────────────────────────────────────────

    [Fact]
    public void PlanVideoEncode_FitsShortVideoWithoutTrimming()
    {
        var plan = FfmpegFeedbackMediaProcessor.PlanVideoEncode(10 * 1024 * 1024, 30, 64_000);
        Assert.Null(plan.TrimSeconds);
        Assert.InRange(plan.VideoBitsPerSecond, 2_000_000, 2_600_000);
        Assert.True((plan.VideoBitsPerSecond + 64_000) * 30 / 8 <= 10 * 1024 * 1024);
    }

    [Fact]
    public void PlanVideoEncode_TrimsOnlyWhenMinimumBitrateCannotFit()
    {
        var plan = FfmpegFeedbackMediaProcessor.PlanVideoEncode(10 * 1024 * 1024, 3600, 64_000);
        Assert.Equal(240, plan.TrimSeconds);
        Assert.InRange(plan.VideoBitsPerSecond, 250_000, 255_000);
    }

    [Fact]
    public void PlanVideoEncode_CapsBitrateForTinyClips()
        => Assert.Equal(4_000_000, FfmpegFeedbackMediaProcessor.PlanVideoEncode(10 * 1024 * 1024, 1, 0).VideoBitsPerSecond);

    [Theory]
    [InlineData(4_000_000, 1920)]
    [InlineData(1_500_000, 1280)]
    [InlineData(700_000, 960)]
    [InlineData(250_000, 640)]
    public void LongSideFor_ScalesWithBitrate(long bps, int expected)
        => Assert.Equal(expected, FfmpegFeedbackMediaProcessor.LongSideFor(bps));

    [Theory]
    [InlineData("../../etc/passwd.png", "passwd")]
    [InlineData("My Screenshot (1).PNG", "My-Screenshot--1")]
    [InlineData("日本.jpg", "attachment")]
    public void SafeBaseName_Sanitizes(string input, string expected)
        => Assert.Equal(expected, FfmpegFeedbackMediaProcessor.SafeBaseName(input));

    // ─── ffmpeg-backed processing ───────────────────────────────

    [Fact]
    public async Task Prepare_RejectsNonMedia()
    {
        var path = Path.Combine(_dir, "evil.png");
        await File.WriteAllTextAsync(path, "#EXTM3U\nhttp://example.invalid/x");
        var processor = CreateProcessor();

        var error = await Assert.ThrowsAsync<FeedbackMediaException>(() =>
            processor.PrepareAsync(new FeedbackAttachmentUpload("evil.png", "image/png", path, 30), Path.Combine(_dir, "w"), CancellationToken.None));
        Assert.Contains("isn't a supported", error.Message);
    }

    [Fact]
    public async Task Prepare_SmallPng_IsStrippedNotTranscoded()
    {
        if (!FfmpegAvailable()) return;
        var source = Path.Combine(_dir, "shot.png");
        Ffmpeg("-f", "lavfi", "-i", "testsrc=size=320x240", "-frames:v", "1", source);

        var result = await CreateProcessor().PrepareAsync(Upload(source, "Shot 1.png", "image/png"), Path.Combine(_dir, "w"), CancellationToken.None);

        Assert.False(result.Transcoded);
        Assert.Equal("Shot-1.png", result.FileName);
        Assert.Equal("image/png", result.ContentType);
        Assert.Equal("image", result.Kind);
    }

    [Fact]
    public async Task Prepare_RotatedJpeg_IsUprightWithoutExif()
    {
        if (!FfmpegAvailable()) return;
        var plain = Path.Combine(_dir, "plain.jpg");
        Ffmpeg("-f", "lavfi", "-i", "testsrc=size=320x240", "-frames:v", "1", plain);
        var withExif = Path.Combine(_dir, "photo.jpg");
        var bytes = File.ReadAllBytes(plain);
        var exif = ExifSegment(6, littleEndian: false);
        File.WriteAllBytes(withExif, [.. bytes.AsSpan(0, 2), .. exif, .. bytes.AsSpan(2)]);

        var result = await CreateProcessor().PrepareAsync(Upload(withExif, "photo.jpg", "image/jpeg"), Path.Combine(_dir, "w"), CancellationToken.None);

        Assert.Equal("image/jpeg", result.ContentType);
        Assert.Equal(-1, File.ReadAllBytes(result.FilePath).AsSpan().IndexOf("Exif"u8));
        Assert.Equal("240x320", ProbeDimensions(result.FilePath));
    }

    [Fact]
    public async Task Prepare_OversizedImage_IsResizedUnderLimit()
    {
        if (!FfmpegAvailable()) return;
        var source = Path.Combine(_dir, "big.bmp");
        Ffmpeg("-f", "lavfi", "-i", "testsrc2=size=1600x1200", "-frames:v", "1", source);

        var result = await CreateProcessor(limit: 60_000).PrepareAsync(Upload(source, "big.bmp", "image/bmp"), Path.Combine(_dir, "w"), CancellationToken.None);

        Assert.True(result.Transcoded);
        Assert.Equal("big.jpg", result.FileName);
        Assert.True(new FileInfo(result.FilePath).Length <= 60_000);
    }

    [Fact]
    public async Task Prepare_SmallVideo_IsRemuxedWithoutMetadata()
    {
        if (!FfmpegAvailable()) return;
        var source = Path.Combine(_dir, "clip.mov");
        Ffmpeg("-f", "lavfi", "-i", "testsrc=size=320x240:duration=1", "-c:v", "libx264", "-pix_fmt", "yuv420p",
            "-metadata", "title=secret-title", "-metadata", "location=+37.3349-122.0090/", source);

        var result = await CreateProcessor().PrepareAsync(Upload(source, "clip.mov", "video/quicktime"), Path.Combine(_dir, "w"), CancellationToken.None);

        Assert.False(result.Transcoded);
        Assert.Equal("video/quicktime", result.ContentType);
        Assert.Equal("clip.mov", result.FileName);
        var output = File.ReadAllBytes(result.FilePath);
        Assert.Equal(-1, output.AsSpan().IndexOf("secret-title"u8));
        Assert.Equal(-1, output.AsSpan().IndexOf("+37.3349"u8));
    }

    [Fact]
    public async Task Prepare_OversizedVideo_IsReencodedUnderLimit()
    {
        if (!FfmpegAvailable()) return;
        var source = Path.Combine(_dir, "long.mp4");
        Ffmpeg("-f", "lavfi", "-i", "testsrc2=size=640x480:duration=4", "-f", "lavfi", "-i", "sine=duration=4",
            "-c:v", "libx264", "-b:v", "3M", "-pix_fmt", "yuv420p", "-c:a", "aac", "-shortest", source);
        const long limit = 250_000;
        Assert.True(new FileInfo(source).Length > limit);

        var result = await CreateProcessor(limit).PrepareAsync(Upload(source, "long.mp4", "video/mp4"), Path.Combine(_dir, "w"), CancellationToken.None);

        Assert.True(result.Transcoded);
        Assert.Equal("video/mp4", result.ContentType);
        Assert.Equal("video", result.Kind);
        Assert.InRange(new FileInfo(result.FilePath).Length, 1, limit);
    }

    // ─── Helpers ────────────────────────────────────────────────

    private static FeedbackAttachmentUpload Upload(string path, string name, string contentType)
        => new(name, contentType, path, new FileInfo(path).Length);

    private static FfmpegFeedbackMediaProcessor CreateProcessor(long limit = 10 * 1024 * 1024)
        => new(Options.Create(new FeedbackOptions { GitHubAttachmentMaxBytes = limit }), NullLogger<FfmpegFeedbackMediaProcessor>.Instance);

    private static bool FfmpegAvailable()
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo("ffmpeg", "-version") { RedirectStandardOutput = true, RedirectStandardError = true });
            process!.WaitForExit(10_000);
            return process.ExitCode == 0;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    private static void Ffmpeg(params string[] args)
    {
        var info = new ProcessStartInfo("ffmpeg") { RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (var arg in new[] { "-hide_banner", "-loglevel", "error", "-y" }.Concat(args))
            info.ArgumentList.Add(arg);
        using var process = Process.Start(info)!;
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, stderr);
    }

    private static string ProbeDimensions(string path)
    {
        var info = new ProcessStartInfo("ffprobe") { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in new[] { "-v", "error", "-select_streams", "v:0", "-show_entries", "stream=width,height", "-of", "csv=s=x:p=0", path })
            info.ArgumentList.Add(arg);
        using var process = Process.Start(info)!;
        var output = process.StandardOutput.ReadToEnd().Trim();
        process.WaitForExit();
        return output;
    }

    private static byte[] SyntheticJpeg(params byte[][] segments)
    {
        using var stream = new MemoryStream();
        stream.Write([0xFF, 0xD8]);
        stream.Write([0xFF, 0xE0, 0x00, 0x10, .. "JFIF\0"u8, 1, 1, 0, 0, 1, 0, 1, 0, 0]);
        foreach (var segment in segments)
            stream.Write(segment);
        stream.Write([0xFF, 0xDA, 0x00, 0x08, 1, 1, 0, 0, 0x3F, 0, 0x12, 0x34, 0xFF, 0xD9]);
        return stream.ToArray();
    }

    private static byte[] ExifSegment(ushort orientation, bool littleEndian)
    {
        var tiff = new byte[26];
        if (littleEndian)
        {
            "II"u8.CopyTo(tiff);
            BinaryPrimitives.WriteUInt16LittleEndian(tiff.AsSpan(2), 42);
            BinaryPrimitives.WriteUInt32LittleEndian(tiff.AsSpan(4), 8);
            BinaryPrimitives.WriteUInt16LittleEndian(tiff.AsSpan(8), 1);
            BinaryPrimitives.WriteUInt16LittleEndian(tiff.AsSpan(10), 0x0112);
            BinaryPrimitives.WriteUInt16LittleEndian(tiff.AsSpan(12), 3);
            BinaryPrimitives.WriteUInt32LittleEndian(tiff.AsSpan(14), 1);
            BinaryPrimitives.WriteUInt16LittleEndian(tiff.AsSpan(18), orientation);
        }
        else
        {
            "MM"u8.CopyTo(tiff);
            BinaryPrimitives.WriteUInt16BigEndian(tiff.AsSpan(2), 42);
            BinaryPrimitives.WriteUInt32BigEndian(tiff.AsSpan(4), 8);
            BinaryPrimitives.WriteUInt16BigEndian(tiff.AsSpan(8), 1);
            BinaryPrimitives.WriteUInt16BigEndian(tiff.AsSpan(10), 0x0112);
            BinaryPrimitives.WriteUInt16BigEndian(tiff.AsSpan(12), 3);
            BinaryPrimitives.WriteUInt32BigEndian(tiff.AsSpan(14), 1);
            BinaryPrimitives.WriteUInt16BigEndian(tiff.AsSpan(18), orientation);
        }

        byte[] payload = [.. "Exif\0\0"u8, .. tiff];
        var length = (ushort)(payload.Length + 2);
        return [0xFF, 0xE1, (byte)(length >> 8), (byte)length, .. payload];
    }

    private static byte[] CommentSegment(string text)
    {
        var payload = Encoding.ASCII.GetBytes(text);
        var length = (ushort)(payload.Length + 2);
        return [0xFF, 0xFE, (byte)(length >> 8), (byte)length, .. payload];
    }

    private static void WritePngChunk(Stream stream, string type, byte[] data)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(length, (uint)data.Length);
        stream.Write(length);
        stream.Write(Encoding.ASCII.GetBytes(type));
        stream.Write(data);
        stream.Write([0, 0, 0, 0]);
    }
}
