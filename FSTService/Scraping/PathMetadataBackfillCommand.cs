using System.Text.Json;
using System.Text.Json.Serialization;

namespace FSTService.Scraping;

/// <summary>File-only chart metadata refresh. Does not construct a host or change database state.</summary>
internal static class PathMetadataBackfillCommand
{
    internal const string Flag = "--path-metadata-backfill";

    internal static async Task<int> RunAsync(string[] args, IConfiguration configuration,
        HttpClient http, TextWriter output, CancellationToken ct)
    {
        if (args.Length != 5 || args[0] != Flag || args[1] != "--request" || args[3] != "--report")
            throw new ArgumentException("Expected --path-metadata-backfill --request <file> --report <new file>.");
        var root = Path.GetFullPath(configuration["Scraper:DataDirectory"] ?? "data");
        var requestPath = Path.GetFullPath(Path.Combine(root, args[2]));
        var reportPath = Path.GetFullPath(Path.Combine(root, args[4]));
        PathDoubleBassMetadataStore.EnsureSafePath(root, requestPath);
        PathDoubleBassMetadataStore.EnsureSafePath(root, reportPath);
        if (File.Exists(reportPath))
            throw new InvalidOperationException("Backfill report must be a new file.");
        var requests = JsonSerializer.Deserialize<PathMetadataBackfillRequest[]>(
            await File.ReadAllTextAsync(requestPath, ct), PathArtifactManifest.JsonOptions)
            ?? throw new InvalidDataException("Backfill request is empty.");
        if (requests.Length is < 1 or > 1000
            || requests.Select(x => (x.SongId, x.GenerationId)).Distinct().Count() != requests.Length)
            throw new InvalidDataException("Backfill requires 1 to 1000 unique generation requests.");
        var key = MidiCryptor.ParseHexKey(configuration["Scraper:MidiEncryptionKey"]
            ?? configuration["FESTIVAL_MIDI_KEY"] ?? "");
        var results = new List<object>();
        var failed = 0;
        foreach (var request in requests)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var directory = PathArtifactResolver.GetGenerationDirectory(
                    root, request.SongId, request.GenerationId);
                var manifestPath = Path.Combine(directory, PathArtifactResolver.ManifestFileName);
                PathDoubleBassMetadataStore.EnsureSafePath(root, manifestPath);
                if (new FileInfo(manifestPath).Length > 64 * 1024)
                    throw new InvalidDataException("Artifact manifest is oversized.");
                var bytes = await File.ReadAllBytesAsync(manifestPath, ct);
                var hash = MidiCryptor.ComputeHash(bytes);
                if (hash != request.ManifestSha256)
                    throw new InvalidDataException("Artifact manifest changed from the requested identity.");
                var manifest = JsonSerializer.Deserialize<PathArtifactManifest>(
                    bytes, PathArtifactManifest.JsonOptions)
                    ?? throw new InvalidDataException("Artifact manifest is empty.");
                if (manifest.SongId != request.SongId || manifest.GenerationId != request.GenerationId)
                    throw new InvalidDataException("Artifact identity does not match the request.");
                var existing = manifest.DoubleBassSupported
                    ?? PathDoubleBassMetadataStore.Read(root, manifest, hash);
                bool support;
                if (existing.HasValue)
                {
                    support = existing.Value;
                }
                else
                {
                    if (!Uri.TryCreate(request.DatUrl, UriKind.Absolute, out var uri)
                        || uri.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(uri.UserInfo))
                        throw new InvalidDataException("Chart URL must be HTTPS without embedded credentials.");
                    using var response = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct);
                    response.EnsureSuccessStatusCode();
                    if (response.Content.Headers.ContentLength > 64 * 1024 * 1024)
                        throw new InvalidDataException("Encrypted chart is oversized.");
                    using var chart = new MemoryStream();
                    await using var source = await response.Content.ReadAsStreamAsync(ct);
                    var buffer = new byte[8192];
                    int count;
                    while ((count = await source.ReadAsync(buffer, ct)) > 0)
                    {
                        if (chart.Length + count > 64 * 1024 * 1024)
                            throw new InvalidDataException("Encrypted chart is oversized.");
                        await chart.WriteAsync(buffer.AsMemory(0, count), ct);
                    }
                    var encrypted = chart.ToArray();
                    if (MidiCryptor.ComputeHash(encrypted) != manifest.DatFileHash)
                        throw new InvalidDataException("Downloaded chart does not match the generation hash.");
                    support = MidiTrackInspector.HasDoubleBassSupport(MidiCryptor.Decrypt(encrypted, key));
                    // Recheck the bound artifact immediately before creating supplemental metadata.
                    if (MidiCryptor.ComputeHash(await File.ReadAllBytesAsync(manifestPath, ct)) != hash)
                        throw new InvalidDataException("Artifact manifest changed during inspection.");
                    PathDoubleBassMetadataStore.Write(root, manifest, hash, support);
                }
                results.Add(new { request.SongId, request.GenerationId, success = true,
                    doubleBassSupported = support });
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failed++;
                // URLs, configuration and exception messages never enter output.
                results.Add(new { request.SongId, request.GenerationId, success = false,
                    error = ex.GetType().Name });
            }
            if (results.Count % 10 == 0 || results.Count == requests.Length)
                await output.WriteLineAsync(JsonSerializer.Serialize(new { completed = results.Count,
                    total = requests.Length, failed }));
        }
        using (var report = new FileStream(reportPath, FileMode.CreateNew, FileAccess.Write))
            JsonSerializer.Serialize(report, new { total = results.Count, failed, results },
                PathArtifactManifest.JsonOptions);
        return failed == 0 ? 0 : 2;
    }
}

internal sealed record PathMetadataBackfillRequest(
    [property: JsonRequired] string SongId,
    [property: JsonRequired] string GenerationId,
    [property: JsonRequired] string ManifestSha256,
    [property: JsonRequired] string DatUrl);
