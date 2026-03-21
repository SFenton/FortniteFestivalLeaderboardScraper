using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using FortniteFestival.Core.Services;
using Microsoft.Extensions.Options;

namespace FSTService.Scraping;

/// <summary>
/// Orchestrates path generation for Fortnite Festival songs:
///   1. Downloads encrypted .dat from Epic CDN
///   2. Compares hash with cached version (skip if unchanged)
///   3. Decrypts to MIDI, produces instrument variants
///   4. Runs CHOpt CLI for each instrument
///   5. Stores max scores in Songs DB and path images on disk
/// </summary>
public sealed class PathGenerator
{
    private readonly HttpClient _http;
    private readonly IOptions<ScraperOptions> _options;
    private readonly ScrapeProgressTracker _progress;
    private readonly ILogger<PathGenerator> _log;
    private readonly SemaphoreSlim _concurrency;

    /// <summary>
    /// Map from our instrument DB names to CHOpt arguments.
    /// Pro instruments use a MIDI variant with PLASTIC tracks promoted.
    /// Other instruments use the original MIDI with native CHOpt instrument names.
    /// </summary>
    private static readonly (string Instrument, string MidiVariant, string CHOptInstrument)[] InstrumentMap =
    [
        ("Solo_PeripheralGuitar", "pro",     "guitar"),    // Pro Lead
        ("Solo_PeripheralBass",   "pro",     "bass"),      // Pro Bass
        ("Solo_Drums",            "og",      "drums"),     // Drums (native CHOpt FNF drums)
        ("Solo_Vocals",           "og",      "vocals"),    // Vocals (native CHOpt FNF vocals)
        ("Solo_Guitar",           "og",      "guitar"),    // Lead
        ("Solo_Bass",             "og",      "bass"),      // Bass
    ];

    private static readonly string[] Difficulties = ["easy", "medium", "hard", "expert"];

    public PathGenerator(
        HttpClient http,
        IOptions<ScraperOptions> options,
        ScrapeProgressTracker progress,
        ILogger<PathGenerator> log)
    {
        _http = http;
        _options = options;
        _progress = progress;
        _log = log;
        _concurrency = new SemaphoreSlim(options.Value.PathGenerationParallelism);
    }

    /// <summary>
    /// Result of generating paths for a single song/instrument/difficulty.
    /// </summary>
    public sealed record PathResult(string Instrument, string Difficulty, int? MaxScore, string? ImagePath);

    /// <summary>
    /// Result of generating paths for an entire song.
    /// </summary>
    public sealed record SongPathResult(
        string SongId,
        string DatFileHash,
        IReadOnlyList<PathResult> Results);

    /// <summary>
    /// Generate paths for a set of songs. Returns results as they complete.
    /// Skips songs whose .dat file hash hasn't changed (unless force=true).
    /// </summary>
    public async Task<List<SongPathResult>> GeneratePathsAsync(
        IReadOnlyList<SongPathRequest> songs,
        bool force,
        CancellationToken ct)
    {
        var opts = _options.Value;
        var key = GetMidiKey(opts);
        if (key is null)
        {
            _log.LogWarning("MIDI encryption key not configured. Skipping path generation.");
            return [];
        }

        var choptPath = GetCHOptPath(opts);
        if (choptPath is null)
        {
            _log.LogWarning("CHOpt binary not found at '{Path}'. Skipping path generation.", opts.CHOptPath);
            return [];
        }

        var dataDir = Path.GetFullPath(opts.DataDirectory);
        var midiDir = Path.Combine(dataDir, "midi");
        var pathsDir = Path.Combine(dataDir, "paths");
        Directory.CreateDirectory(midiDir);

        var tasks = songs.Select(async song =>
        {
            try
            {
                return await ProcessSongAsync(song, key, choptPath, midiDir, pathsDir, force, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.LogError(ex, "Path generation failed for song {SongId} ({Title}).", song.SongId, song.Title);
                _progress.PathGenSongFailed();
                return null;
            }
        });

        var results = await Task.WhenAll(tasks);
        return results.Where(r => r is not null).ToList()!;
    }

    [ExcludeFromCodeCoverage] // Coverlet async state machine gap: error/cleanup paths tested via GeneratePathsAsync integration tests
    private async Task<SongPathResult?> ProcessSongAsync(
        SongPathRequest song,
        byte[] key,
        string choptPath,
        string midiDir,
        string pathsDir,
        bool force,
        CancellationToken ct)
    {
        if (string.IsNullOrEmpty(song.DatUrl))
        {
            _progress.PathGenSongSkipped();
            return null;
        }

        // Fast check: skip download entirely if the song's lastModified hasn't changed
        var lastModStr = song.LastModified?.ToString("o");
        if (!force && lastModStr is not null && lastModStr == song.ExistingLastModified)
        {
            _progress.PathGenSongSkipped();
            return null;
        }

        _progress.PathGenProcessing(song.Title);

        // Download .dat file
        byte[] datBytes;
        try
        {
            datBytes = await DownloadDatAsync(song.DatUrl, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning(ex, "Failed to download .dat for {SongId}.", song.SongId);
            _progress.PathGenSongFailed();
            return null;
        }

        // Check hash against cached version
        var hash = MidiCryptor.ComputeHash(datBytes);
        if (!force && hash == song.ExistingDatHash)
        {
            _progress.PathGenSongSkipped();
            return null;
        }

        // Cache the .dat file on disk
        var datPath = Path.Combine(midiDir, $"{song.SongId}.dat");
        await File.WriteAllBytesAsync(datPath, datBytes, ct);

        // Decrypt
        var midiBytes = MidiCryptor.Decrypt(datBytes, key);

        // Produce variants
        var variants = MidiTrackRenamer.ProduceVariants(midiBytes);

        // Write variants to temp files for CHOpt
        var tempDir = Path.Combine(Path.GetTempPath(), $"fst-paths-{song.SongId}");
        Directory.CreateDirectory(tempDir);

        try
        {
            var proPath = Path.Combine(tempDir, $"{song.SongId}_pro.mid");
            var ogPath = Path.Combine(tempDir, $"{song.SongId}_og.mid");

            await File.WriteAllBytesAsync(proPath, variants.ProMidi, ct);
            await File.WriteAllBytesAsync(ogPath, variants.OgMidi, ct);

            // Write song.ini so CHOpt renders song metadata in the image header
            var songIni = Path.Combine(tempDir, "song.ini");
            await File.WriteAllTextAsync(songIni,
                $"[song]\nname = {song.Title}\nartist = {song.Artist}\ncharter = Epic Games\n", ct);

            // Create output directory for this song's path images
            var songPathsDir = Path.Combine(pathsDir, song.SongId);

            // Run CHOpt for each instrument × difficulty
            var results = new List<PathResult>();
            foreach (var (instrument, variant, choptInstrument) in InstrumentMap)
            {
                var midiFile = variant switch
                {
                    "pro" => proPath,
                    _ => ogPath, // "og" and any future variants default to original
                };

                // Output: paths/{songId}/{instrument}/{difficulty}.png
                var instrumentDir = Path.Combine(songPathsDir, instrument);
                Directory.CreateDirectory(instrumentDir);

                foreach (var difficulty in Difficulties)
                {
                    ct.ThrowIfCancellationRequested();

                    var outputImage = Path.Combine(instrumentDir, $"{difficulty}.png");

                    await _concurrency.WaitAsync(ct);
                    try
                    {
                        var maxScore = await RunCHOptAsync(choptPath, midiFile, choptInstrument, difficulty, outputImage, ct);
                        results.Add(new PathResult(
                            instrument,
                            difficulty,
                            maxScore,
                            File.Exists(outputImage) ? outputImage : null));

                        if (difficulty == "expert")
                        {
                            if (maxScore.HasValue)
                                _log.LogDebug("{SongId}/{Instrument}: max score = {Score}", song.SongId, instrument, maxScore.Value);
                            else
                                _log.LogWarning("{SongId}/{Instrument}: CHOpt returned no score on expert.", song.SongId, instrument);
                        }
                    }
                    finally
                    {
                        _concurrency.Release();
                    }
                }
            }

            _log.LogInformation("Generated paths for {Title} ({SongId}): {Results}",
                song.Title, song.SongId,
                string.Join(", ", results
                    .Where(r => r.Difficulty == "expert" && r.MaxScore.HasValue)
                    .Select(r => $"{r.Instrument}={r.MaxScore}")));

            _progress.PathGenSongCompleted();

            return new SongPathResult(song.SongId, hash, results);
        }
        finally
        {
            // Clean up temp files
            try { Directory.Delete(tempDir, recursive: true); }
            catch { /* best effort */ }
        }
    }

    /// <summary>
    /// Run CHOpt CLI and parse the "Total score: NNNNN" output.
    /// </summary>
    [ExcludeFromCodeCoverage] // Coverlet async state machine gap: error-exit tested via RunCHOptAsync_nonzero_exit_returns_null
    internal async Task<int?> RunCHOptAsync(
        string choptPath,
        string midiFile,
        string instrument,
        string difficulty,
        string outputImage,
        CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = choptPath,
            ArgumentList =
            {
                "-f", midiFile,
                "-i", instrument,
                "-d", difficulty,
                "-o", outputImage,
                "--engine", "fnf",
                "--early-whammy", "0",
                "--squeeze", "20",
            },
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        // CHOpt bundles Qt6 libraries in a sibling "libs/" directory.
        // Set LD_LIBRARY_PATH so the dynamic linker finds them at runtime.
        var libsDir = Path.Combine(Path.GetDirectoryName(choptPath)!, "libs");
        if (Directory.Exists(libsDir))
            psi.Environment["LD_LIBRARY_PATH"] = libsDir;

        using var process = Process.Start(psi);
        if (process is null)
        {
            _log.LogError("Failed to start CHOpt process.");
            return null;
        }

        var stdout = await process.StandardOutput.ReadToEndAsync(ct);
        var stderr = await process.StandardError.ReadToEndAsync(ct);

        await process.WaitForExitAsync(ct);

        if (process.ExitCode != 0)
        {
            _log.LogWarning("CHOpt exited with code {Code} for {Midi} -i {Instrument}. stderr: {Stderr}",
                process.ExitCode, Path.GetFileName(midiFile), instrument, stderr.Trim());
            return null;
        }

        return ParseTotalScore(stdout);
    }

    /// <summary>
    /// Parse "Total score: NNNNN" from CHOpt stdout.
    /// </summary>
    public static int? ParseTotalScore(string choptOutput)
    {
        // CHOpt outputs "Total score: 234567" on its own line
        foreach (var line in choptOutput.AsSpan().EnumerateLines())
        {
            var trimmed = line.Trim();
            const string prefix = "Total score: ";
            if (trimmed.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                var scoreText = trimmed.Slice(prefix.Length).Trim();
                if (int.TryParse(scoreText, out var score))
                    return score;
            }
        }
        return null;
    }

    private async Task<byte[]> DownloadDatAsync(string url, CancellationToken ct)
    {
        using var response = await _http.GetAsync(url, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsByteArrayAsync(ct);
    }

    [ExcludeFromCodeCoverage] // Config helper with env-var fallback
    private byte[]? GetMidiKey(ScraperOptions opts)
    {
        var keyHex = opts.MidiEncryptionKey;
        if (string.IsNullOrWhiteSpace(keyHex))
            keyHex = Environment.GetEnvironmentVariable("FESTIVAL_MIDI_KEY");

        if (string.IsNullOrWhiteSpace(keyHex))
            return null;

        try
        {
            return MidiCryptor.ParseHexKey(keyHex);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Invalid MIDI encryption key.");
            return null;
        }
    }

    [ExcludeFromCodeCoverage] // Platform-specific path probing
    private string? GetCHOptPath(ScraperOptions opts)
    {
        var path = Path.GetFullPath(opts.CHOptPath);
        if (File.Exists(path)) return path;

        // Try common suffixes
        if (OperatingSystem.IsWindows())
        {
            var withExe = path + ".exe";
            if (File.Exists(withExe)) return withExe;
        }

        return null;
    }

    /// <summary>
    /// Describes a song to be processed by the path generator.
    /// </summary>
    public sealed record SongPathRequest(
        string SongId,
        string Title,
        string Artist,
        string? DatUrl,
        DateTime? LastModified,
        string? ExistingDatHash,
        string? ExistingLastModified);
}
