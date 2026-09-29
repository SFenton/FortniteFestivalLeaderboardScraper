using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using FSTService.Scraping;
using Microsoft.Extensions.Configuration;

namespace FSTService.Tests.Unit;

public sealed class PathDoubleBassMetadataTests : IDisposable
{
    private readonly string _root = Path.Combine(Directory.GetCurrentDirectory(),
        ".test-temp", "path-metadata-" + Guid.NewGuid().ToString("N"));

    public PathDoubleBassMetadataTests() => Directory.CreateDirectory(_root);
    public void Dispose() => Directory.Delete(_root, recursive: true);

    private PathArtifactManifest WriteManifest(string datHash = "dat-hash")
    {
        var manifest = new PathArtifactManifest("generation-a", "song-a", datHash, null,
            "1.16.4", "binary-hash", PathGenerationProfiles.PlasticDrumsV4,
            ["Solo_Guitar"], new() { ["Solo_Guitar"] = 1000 }, DateTime.UtcNow);
        var directory = PathArtifactResolver.GetGenerationDirectory(_root, "song-a", "generation-a");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, PathArtifactResolver.ManifestFileName),
            JsonSerializer.Serialize(manifest, PathArtifactManifest.JsonOptions));
        return manifest;
    }

    private string ManifestPath => Path.Combine(
        PathArtifactResolver.GetGenerationDirectory(_root, "song-a", "generation-a"),
        PathArtifactResolver.ManifestFileName);

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Supplement_is_immutable_bound_and_preserves_the_original_manifest(bool support)
    {
        var manifest = WriteManifest();
        var before = File.ReadAllBytes(ManifestPath);
        var hash = MidiCryptor.ComputeHash(before);
        Assert.Null(PathArtifactResolver.ReadDoubleBassSupport(_root, "song-a", "generation-a"));
        PathDoubleBassMetadataStore.Write(_root, manifest, hash, support);
        var supplement = PathDoubleBassMetadataStore.GetPath(_root, "song-a", "generation-a");
        var metadataBytes = File.ReadAllBytes(supplement);
        Assert.Equal(support, PathArtifactResolver.ReadDoubleBassSupport(_root, "song-a", "generation-a"));
        PathDoubleBassMetadataStore.Write(_root, manifest, hash, support);
        Assert.Equal(metadataBytes, File.ReadAllBytes(supplement));
        Assert.Equal(before, File.ReadAllBytes(ManifestPath));
        Assert.Throws<InvalidDataException>(() =>
            PathDoubleBassMetadataStore.Write(_root, manifest, hash, !support));
        File.AppendAllText(ManifestPath, "\n");
        Assert.Throws<InvalidDataException>(() =>
            PathArtifactResolver.ReadDoubleBassSupport(_root, "song-a", "generation-a"));
    }

    [Fact]
    public void Missing_boolean_and_symbolic_links_are_rejected()
    {
        var manifest = WriteManifest();
        var hash = MidiCryptor.ComputeHash(File.ReadAllBytes(ManifestPath));
        PathDoubleBassMetadataStore.Write(_root, manifest, hash, true);
        var path = PathDoubleBassMetadataStore.GetPath(_root, "song-a", "generation-a");
        File.WriteAllText(path, File.ReadAllText(path).Replace(
            "\"doubleBassSupported\": true,", ""));
        Assert.Throws<JsonException>(() => PathDoubleBassMetadataStore.Read(_root, manifest, hash));
        if (!OperatingSystem.IsWindows())
        {
            File.Delete(path);
            File.CreateSymbolicLink(path, ManifestPath);
            Assert.Throws<InvalidDataException>(() =>
                PathDoubleBassMetadataStore.Read(_root, manifest, hash));
        }
    }

    [Theory]
    [InlineData(95, true, false)]
    [InlineData(96, false, false)]
    [InlineData(95, true, true)]
    public async Task Backfill_checks_the_encrypted_chart_hash_before_writing(byte pitch,
        bool expectedSupport, bool wrongHash)
    {
        var key = new byte[16];
        // One named plastic-drum track containing a Note On and End Of Track.
        byte[] midi = [77,84,104,100,0,0,0,6,0,1,0,1,1,224,
            77,84,114,107,0,0,0,25,0,255,3,13,
            80,76,65,83,84,73,67,32,68,82,85,77,83,
            0,144,pitch,100,0,255,47,0];
        using var aes = Aes.Create();
        aes.Key = key;
        aes.Mode = CipherMode.ECB;
        aes.Padding = PaddingMode.Zeros;
        var encrypted = aes.CreateEncryptor().TransformFinalBlock(midi, 0, midi.Length);
        var manifest = WriteManifest(wrongHash ? "wrong-hash" : MidiCryptor.ComputeHash(encrypted));
        var before = File.ReadAllBytes(ManifestPath);
        var request = new[] { new PathMetadataBackfillRequest("song-a", "generation-a",
            MidiCryptor.ComputeHash(before), "https://example.invalid/chart.dat") };
        await File.WriteAllTextAsync(Path.Combine(_root, "request.json"),
            JsonSerializer.Serialize(request, PathArtifactManifest.JsonOptions));
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Scraper:DataDirectory"] = _root,
            ["Scraper:MidiEncryptionKey"] = Convert.ToHexString(key),
        }).Build();
        using var http = new HttpClient(new ChartHandler(encrypted));
        var code = await PathMetadataBackfillCommand.RunAsync(
            ["--path-metadata-backfill", "--request", "request.json", "--report", "report.json"],
            configuration, http, TextWriter.Null, CancellationToken.None);
        Assert.Equal(wrongHash ? 2 : 0, code);
        Assert.Equal(before, File.ReadAllBytes(ManifestPath));
        Assert.Equal(wrongHash ? null : expectedSupport,
            PathArtifactResolver.ReadDoubleBassSupport(_root, "song-a", "generation-a"));
    }

    private sealed class ChartHandler(byte[] bytes) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new ByteArrayContent(bytes) });
    }
}
