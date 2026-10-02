using System.Buffers.Binary;
using System.Text;
using FSTService.Feedback;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace FSTService.Tests.Unit.Feedback;

/// <summary>
/// Drives every <see cref="FfmpegFeedbackMediaProcessor"/> branch through scripted stand-ins
/// for ffmpeg/ffprobe, so the decision logic is verified without a real ffmpeg install.
/// Real-ffmpeg behavior is covered by <see cref="FeedbackMediaTests"/> where ffmpeg exists.
/// </summary>
public sealed class FfmpegFeedbackMediaProcessorTests : IDisposable
{
    private readonly string _dir = Path.Combine(AppContext.BaseDirectory, "test-artifacts", $"feedback_fake_{Guid.NewGuid():N}");

    public FfmpegFeedbackMediaProcessorTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public async Task Png_WhenStrippedCopyIsTooLarge_IsConvertedToJpeg()
    {
        if (OperatingSystem.IsWindows()) return;
        var source = WriteFile("shot.png", SyntheticPng(400));
        var tools = FakeTools(outputBytes: 50);

        var result = await Prepare(tools, source, "shot.png", limit: 100);

        Assert.True(result.Transcoded);
        Assert.Equal("shot.jpg", result.FileName);
        Assert.Equal("image/jpeg", result.ContentType);
        Assert.Equal("Resized to fit GitHub's attachment size limit.", result.Note);
        Assert.Contains("png_pipe", tools.Calls().Single());
    }

    [Fact]
    public async Task Gif_UnderLimit_IsAttachedUnchanged()
    {
        if (OperatingSystem.IsWindows()) return;
        var source = WriteFile("loop.gif", [.. "GIF89a"u8, .. new byte[20]]);
        var tools = FakeTools(outputBytes: 10);

        var result = await Prepare(tools, source, "loop.gif");

        Assert.False(result.Transcoded);
        Assert.Equal(source, result.FilePath);
        Assert.Equal("image/gif", result.ContentType);
        Assert.Empty(tools.Calls());
    }

    [Fact]
    public async Task Gif_OverLimit_IsConvertedToMp4()
    {
        if (OperatingSystem.IsWindows()) return;
        var source = WriteFile("loop.gif", [.. "GIF89a"u8, .. new byte[100_000]]);
        var tools = FakeTools(outputBytes: 50, probeOutput: "duration=2.5\ncodec_type=video\n");

        var result = await Prepare(tools, source, "loop.gif", limit: 90_000);

        Assert.True(result.Transcoded);
        Assert.Equal("loop.mp4", result.FileName);
        Assert.Equal("video", result.Kind);
        Assert.Equal("Converted from GIF to MP4 to fit GitHub's attachment size limit.", result.Note);
        var encode = tools.Calls().Single();
        Assert.Contains("libx264", encode);
        Assert.DoesNotContain(" -t ", encode);
        Assert.Contains("format=duration", tools.ProbeCalls().Single());
    }

    [Theory]
    [InlineData("RIFF\0\0\0\0WEBPVP8 ", "pic.webp", "webp_pipe")]
    [InlineData("BM\0\0\0\0\0\0\0\0\0\0\0\0", "pic.bmp", "bmp_pipe")]
    [InlineData("II*\0\u0008\0\0\0", "pic.tiff", "tiff_pipe")]
    [InlineData("\0\0\0\u0018ftypheic", "pic.heic", "mov")]
    public async Task NonNativeImages_AreConvertedToJpeg(string header, string name, string demuxer)
    {
        if (OperatingSystem.IsWindows()) return;
        var source = WriteFile(name, [.. Encoding.Latin1.GetBytes(header), .. new byte[16]]);
        var tools = FakeTools(outputBytes: 20);

        var result = await Prepare(tools, source, name);

        Assert.True(result.Transcoded);
        Assert.Equal("pic.jpg", result.FileName);
        Assert.Equal("Converted to JPEG for GitHub.", result.Note);
        Assert.Contains("-f " + demuxer + " ", tools.Calls().Single());
    }

    [Fact]
    public async Task Image_ThatNeverFits_WalksTheWholeResizeLadder()
    {
        if (OperatingSystem.IsWindows()) return;
        var source = WriteFile("big.bmp", [.. "BM"u8, .. new byte[30]]);
        var tools = FakeTools(outputBytes: 500);

        var error = await Assert.ThrowsAsync<FeedbackMediaException>(() => Prepare(tools, source, "big.bmp", limit: 100));

        Assert.Contains("too large to attach even after resizing", error.Message);
        var calls = tools.Calls();
        Assert.Equal(5, calls.Count);
        Assert.Contains("min(4096,iw)", calls[0]);
        Assert.Contains("min(960,iw)", calls[4]);
    }

    [Fact]
    public async Task Image_WhenFfmpegFails_ReportsConversionError()
    {
        if (OperatingSystem.IsWindows()) return;
        var source = WriteFile("pic.bmp", [.. "BM"u8, .. new byte[30]]);
        var tools = FakeTools(exitCode: 1);

        var error = await Assert.ThrowsAsync<FeedbackMediaException>(() => Prepare(tools, source, "pic.bmp"));

        Assert.Equal("This image couldn't be converted.", error.Message);
    }

    [Fact]
    public async Task Jpeg_WithoutOrientation_IsStrippedNotTranscoded()
    {
        if (OperatingSystem.IsWindows()) return;
        var source = WriteFile("photo.jpg", SyntheticJpeg(CommentSegment("secret")));
        var tools = FakeTools(outputBytes: 10);

        var result = await Prepare(tools, source, "photo.jpg");

        Assert.False(result.Transcoded);
        Assert.Equal("photo.jpg", result.FileName);
        Assert.Equal(-1, File.ReadAllBytes(result.FilePath).AsSpan().IndexOf("secret"u8));
        Assert.Empty(tools.Calls());
    }

    [Fact]
    public async Task Jpeg_WithRotation_IsReencodedUpright()
    {
        if (OperatingSystem.IsWindows()) return;
        var source = WriteFile("photo.jpg", SyntheticJpeg(ExifOrientation(8)));
        var tools = FakeTools(outputBytes: 10);

        var result = await Prepare(tools, source, "photo.jpg");

        Assert.True(result.Transcoded);
        Assert.Null(result.Note);
        Assert.Contains("transpose=2,scale=", tools.Calls().Single());
        Assert.Contains("photo-clean.jpg", tools.Calls().Single());
    }

    [Fact]
    public async Task Jpeg_Malformed_IsReencodedFromTheOriginal()
    {
        if (OperatingSystem.IsWindows()) return;
        var source = WriteFile("odd.jpg", [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x04, 0x00, 0x00, 0x12, 0x34, .. new byte[200]]);
        var tools = FakeTools(outputBytes: 10);

        var result = await Prepare(tools, source, "odd.jpg", limit: 100);

        Assert.True(result.Transcoded);
        Assert.Equal("Resized to fit GitHub's attachment size limit.", result.Note);
        var call = tools.Calls().Single();
        Assert.Contains("-i " + source, call);
        Assert.DoesNotContain("transpose", call);
    }

    [Theory]
    [InlineData("\0\0\0\u0018ftypqt  ", "clip.mov", ".mov", "video/quicktime", "-f mov ")]
    [InlineData("\0\0\0\u0018ftypisom", "clip.mp4", ".mp4", "video/mp4", "-f mp4 ")]
    [InlineData("\u001A\u0045\u00DF\u00A3webm", "clip.webm", ".webm", "video/webm", "-f webm ")]
    public async Task SmallVideo_IsRemuxedWithoutReencoding(string header, string name, string extension, string contentType, string muxer)
    {
        if (OperatingSystem.IsWindows()) return;
        var source = WriteFile(name, [.. Encoding.Latin1.GetBytes(header), .. new byte[16]]);
        var tools = FakeTools(outputBytes: 30);

        var result = await Prepare(tools, source, name);

        Assert.False(result.Transcoded);
        Assert.Equal("clip" + extension, result.FileName);
        Assert.Equal(contentType, result.ContentType);
        var call = tools.Calls().Single();
        Assert.Contains("-c copy", call);
        Assert.Contains(muxer, call);
        Assert.Equal(extension != ".webm", call.Contains("+faststart"));
        Assert.Empty(tools.ProbeCalls());
    }

    [Fact]
    public async Task SmallVideo_WhenRemuxFails_IsReencoded()
    {
        if (OperatingSystem.IsWindows()) return;
        var source = WriteFile("clip.mp4", [.. "\0\0\0\u0018ftypisom"u8, .. new byte[16]]);
        var tools = FakeTools(exitCode: 1, exitOnlyWhenArg: "copy", outputBytes: 20,
            probeOutput: "duration=1.0\ncodec_type=video\ncodec_type=audio\n");

        var result = await Prepare(tools, source, "clip.mp4");

        Assert.True(result.Transcoded);
        Assert.Equal("clip.mp4", result.FileName);
        Assert.Equal("Re-encoded to fit GitHub's attachment size limit.", result.Note);
        var calls = tools.Calls();
        Assert.Equal(2, calls.Count);
        Assert.Contains("-c copy", calls[0]);
        Assert.Contains("libx264", calls[1]);
    }

    [Theory]
    [InlineData("\u001A\u0045\u00DF\u00A3\0\0\0\0", "clip.mkv")]
    [InlineData("RIFF\0\0\0\0AVI LIST", "clip.avi")]
    public async Task OtherContainers_AreConvertedToMp4(string header, string name)
    {
        if (OperatingSystem.IsWindows()) return;
        var source = WriteFile(name, [.. Encoding.Latin1.GetBytes(header), .. new byte[16]]);
        var tools = FakeTools(outputBytes: 20, probeExitCode: 1);

        var result = await Prepare(tools, source, name);

        Assert.True(result.Transcoded);
        Assert.Equal("clip.mp4", result.FileName);
        Assert.Equal("Converted to MP4 for GitHub.", result.Note);
        Assert.Contains("-b:a 64000", tools.Calls().Single());
    }

    [Fact]
    public async Task LongVideo_IsTrimmedAndSaysSo()
    {
        if (OperatingSystem.IsWindows()) return;
        var source = WriteFile("long.avi", [.. "RIFF\0\0\0\0AVI LIST"u8, .. new byte[400]]);
        var tools = FakeTools(outputBytes: 50, probeOutput: "codec_type=video\nduration=3600\n");

        var result = await Prepare(tools, source, "long.avi", limit: 100_000);

        Assert.True(result.Transcoded);
        Assert.Contains("Only the first 2 seconds are included.", result.Note);
        Assert.Contains("-t 2 ", tools.Calls().Single());
    }

    [Fact]
    public async Task Video_FitsOnlyOnFinalSizeCappedAttempt_WarnsAboutCutOff()
    {
        if (OperatingSystem.IsWindows()) return;
        var source = WriteFile("clip.avi", [.. "RIFF\0\0\0\0AVI LIST"u8, .. new byte[16]]);
        var tools = FakeTools(outputBytes: 60_000, cappedOutputBytes: 40_000, probeOutput: "duration=1\n");

        var result = await Prepare(tools, source, "clip.avi", limit: 50_000);

        Assert.EndsWith("The end of the video may be cut off.", result.Note);
        var calls = tools.Calls();
        Assert.Equal(3, calls.Count);
        Assert.DoesNotContain("-fs", calls[1]);
        Assert.Contains("-fs 50000", calls[2]);
    }

    [Fact]
    public async Task Video_ThatNeverFits_ReportsTooLarge()
    {
        if (OperatingSystem.IsWindows()) return;
        var source = WriteFile("clip.avi", [.. "RIFF\0\0\0\0AVI LIST"u8, .. new byte[16]]);
        var tools = FakeTools(outputBytes: 500, probeOutput: "duration=1\n");

        var error = await Assert.ThrowsAsync<FeedbackMediaException>(() => Prepare(tools, source, "clip.avi", limit: 100));

        Assert.Contains("too large to attach even after re-encoding", error.Message);
        Assert.Equal(3, tools.Calls().Count);
    }

    [Fact]
    public async Task Video_WhenFfmpegFails_ReportsConversionError()
    {
        if (OperatingSystem.IsWindows()) return;
        var source = WriteFile("clip.avi", [.. "RIFF\0\0\0\0AVI LIST"u8, .. new byte[16]]);
        var tools = FakeTools(exitCode: 2, probeOutput: "duration=1\n");

        var error = await Assert.ThrowsAsync<FeedbackMediaException>(() => Prepare(tools, source, "clip.avi"));

        Assert.Equal("This video couldn't be converted.", error.Message);
    }

    [Fact]
    public async Task MissingTool_ReportsProcessingUnavailable()
    {
        var source = WriteFile("pic.bmp", [.. "BM"u8, .. new byte[30]]);
        var processor = CreateProcessor(new FeedbackOptions { FfmpegPath = Path.Combine(_dir, "no-such-ffmpeg") });

        var error = await Assert.ThrowsAsync<FeedbackMediaException>(() =>
            processor.PrepareAsync(Upload(source, "pic.bmp"), Path.Combine(_dir, "w"), CancellationToken.None));

        Assert.Equal("Media processing is unavailable on the server.", error.Message);
    }

    [Fact]
    public async Task SlowTool_IsKilledAfterTheTimeout()
    {
        if (OperatingSystem.IsWindows()) return;
        var source = WriteFile("pic.bmp", [.. "BM"u8, .. new byte[30]]);
        var tools = FakeTools(sleepSeconds: 60);

        var error = await Assert.ThrowsAsync<FeedbackMediaException>(() =>
            Prepare(tools, source, "pic.bmp", timeoutSeconds: 1));

        Assert.Equal("Processing this file took too long.", error.Message);
    }

    [Fact]
    public async Task Cancellation_StopsTheToolAndPropagates()
    {
        if (OperatingSystem.IsWindows()) return;
        var source = WriteFile("pic.bmp", [.. "BM"u8, .. new byte[30]]);
        var tools = FakeTools(sleepSeconds: 60);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            CreateProcessor(tools.Options(10 * 1024 * 1024, 240))
                .PrepareAsync(Upload(source, "pic.bmp"), Path.Combine(_dir, "w"), cancellation.Token));
    }

    // ─── Helpers ────────────────────────────────────────────────

    private Task<FeedbackPreparedMedia> Prepare(
        FakeToolSet tools,
        string source,
        string name,
        long limit = 10 * 1024 * 1024,
        int timeoutSeconds = 240)
        => CreateProcessor(tools.Options(limit, timeoutSeconds))
            .PrepareAsync(Upload(source, name), Path.Combine(_dir, "w"), CancellationToken.None);

    private static FfmpegFeedbackMediaProcessor CreateProcessor(FeedbackOptions options)
        => new(Options.Create(options), NullLogger<FfmpegFeedbackMediaProcessor>.Instance);

    private static FeedbackAttachmentUpload Upload(string path, string name)
        => new(name, "application/octet-stream", path, new FileInfo(path).Length);

    private string WriteFile(string name, byte[] content)
    {
        var path = Path.Combine(_dir, Guid.NewGuid().ToString("N")[..8] + "-" + name);
        File.WriteAllBytes(path, content);
        return path;
    }

    /// <summary>
    /// Writes a fake ffmpeg that logs its arguments, optionally sleeps or fails, and writes a
    /// zero-filled output of the requested size to its last argument (the output path).
    /// </summary>
    private FakeToolSet FakeTools(
        int outputBytes = 10,
        int? cappedOutputBytes = null,
        int exitCode = 0,
        string? exitOnlyWhenArg = null,
        int sleepSeconds = 0,
        string probeOutput = "",
        int probeExitCode = 0)
    {
        var id = Guid.NewGuid().ToString("N")[..8];
        var log = Path.Combine(_dir, id + "-ffmpeg.log");
        var probeLog = Path.Combine(_dir, id + "-ffprobe.log");
        var failCondition = exitOnlyWhenArg is null ? "true" : $"[ \"$fail\" = 1 ]";
        var ffmpeg = Path.Combine(_dir, id + "-ffmpeg.sh");
        WriteScript(ffmpeg, $$"""
            #!/bin/sh
            printf '%s\n' "$*" >> '{{log}}'
            out=""; capped=0; fail=0
            for a in "$@"; do
              out="$a"
              [ "$a" = "-fs" ] && capped=1
              [ "$a" = "{{exitOnlyWhenArg ?? ""}}" ] && fail=1
            done
            if [ {{sleepSeconds}} -gt 0 ]; then sleep {{sleepSeconds}}; fi
            if [ {{exitCode}} -ne 0 ] && {{failCondition}}; then echo "fake failure" >&2; exit {{exitCode}}; fi
            size={{outputBytes}}
            if [ $capped = 1 ]; then size={{cappedOutputBytes ?? outputBytes}}; fi
            head -c "$size" /dev/zero > "$out"
            """);
        var ffprobe = Path.Combine(_dir, id + "-ffprobe.sh");
        WriteScript(ffprobe, $$"""
            #!/bin/sh
            printf '%s\n' "$*" >> '{{probeLog}}'
            printf '{{probeOutput.Replace("\n", "\\n")}}'
            exit {{probeExitCode}}
            """);
        return new FakeToolSet(ffmpeg, ffprobe, log, probeLog);
    }

    private static void WriteScript(string path, string content)
    {
        File.WriteAllText(path, content.Replace("\r\n", "\n") + "\n");
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    private sealed record FakeToolSet(string Ffmpeg, string Ffprobe, string Log, string ProbeLog)
    {
        public FeedbackOptions Options(long limit, int timeoutSeconds) => new()
        {
            FfmpegPath = Ffmpeg,
            FfprobePath = Ffprobe,
            GitHubAttachmentMaxBytes = limit,
            TranscodeTimeoutSeconds = timeoutSeconds,
        };

        public List<string> Calls() => ReadLines(Log);

        public List<string> ProbeCalls() => ReadLines(ProbeLog);

        private static List<string> ReadLines(string path)
            => File.Exists(path) ? [.. File.ReadAllLines(path).Where(static line => line.Length > 0)] : [];
    }

    private static byte[] SyntheticPng(int idatBytes)
    {
        using var stream = new MemoryStream();
        stream.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        WritePngChunk(stream, "IHDR", new byte[13]);
        WritePngChunk(stream, "IDAT", new byte[idatBytes]);
        WritePngChunk(stream, "IEND", []);
        return stream.ToArray();
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

    private static byte[] ExifOrientation(ushort orientation)
    {
        var tiff = new byte[26];
        "MM"u8.CopyTo(tiff);
        BinaryPrimitives.WriteUInt16BigEndian(tiff.AsSpan(2), 42);
        BinaryPrimitives.WriteUInt32BigEndian(tiff.AsSpan(4), 8);
        BinaryPrimitives.WriteUInt16BigEndian(tiff.AsSpan(8), 1);
        BinaryPrimitives.WriteUInt16BigEndian(tiff.AsSpan(10), 0x0112);
        BinaryPrimitives.WriteUInt16BigEndian(tiff.AsSpan(12), 3);
        BinaryPrimitives.WriteUInt32BigEndian(tiff.AsSpan(14), 1);
        BinaryPrimitives.WriteUInt16BigEndian(tiff.AsSpan(18), orientation);
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
}
