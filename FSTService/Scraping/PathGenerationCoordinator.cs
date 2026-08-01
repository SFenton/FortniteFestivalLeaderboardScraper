using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using FortniteFestival.Core;
using FortniteFestival.Core.Services;
using FSTService.Api;
using FSTService.Persistence;
using Microsoft.Extensions.Options;

namespace FSTService.Scraping;

public sealed partial class PathGenerationCoordinator
{
    private static readonly TimeSpan VersionTimeout = TimeSpan.FromSeconds(10);
    private const int ErrorDetailLimit = 2048;

    private readonly HttpClient _http;
    private readonly IPathDataStore _store;
    private readonly SongsCacheService _songsCache;
    private readonly IOptions<ScraperOptions> _options;
    private readonly ScrapeProgressTracker _progress;
    private readonly ILogger<PathGenerationCoordinator> _log;
    private readonly SemaphoreSlim _choptConcurrency;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _songLocks =
        new(StringComparer.OrdinalIgnoreCase);

    public PathGenerationCoordinator(
        HttpClient http,
        IPathDataStore store,
        SongsCacheService songsCache,
        IOptions<ScraperOptions> options,
        ScrapeProgressTracker progress,
        ILogger<PathGenerationCoordinator> log)
    {
        _http = http;
        _store = store;
        _songsCache = songsCache;
        _options = options;
        _progress = progress;
        _log = log;
        _choptConcurrency = new SemaphoreSlim(
            Math.Max(1, options.Value.PathGenerationParallelism));
    }

    public async Task<PathGenerationBatchResult> GeneratePathsAsync(
        IReadOnlyCollection<Song> songs,
        bool force,
        CancellationToken ct)
    {
        if (!_options.Value.EnablePathGeneration)
            return new PathGenerationBatchResult(0, 0, 0, 0, 0);

        var requests = new List<SongPathRequest>(songs.Count);
        foreach (var song in songs)
        {
            if (SongPathRequest.FromSong(song) is { } request)
                requests.Add(request);
        }

        if (requests.Count == 0)
            return new PathGenerationBatchResult(0, 0, 0, 0, 0);

        var ownsProgress = _progress.BeginPathGeneration(requests.Count);
        try
        {
            byte[] key;
            string choptPath;
            PathGenerationRuntimeIdentity runtime;
            try
            {
                key = GetMidiKey(_options.Value) ??
                    throw new PathGenerationException(
                        "configuration",
                        "MIDI encryption key is not configured or is invalid.");
                choptPath = GetChoptPath(_options.Value) ??
                    throw new PathGenerationException(
                        "configuration",
                        $"CHOpt binary was not found at '{_options.Value.CHOptPath}'.");
                runtime = await DetectRuntimeIdentityAsync(choptPath, ct);
            }
            catch (OperationCanceledException)
            {
                await RecordBatchFailureAsync(
                    requests,
                    null,
                    "cancelled",
                    "Path generation was cancelled before song processing began.",
                    ownsProgress);
                throw;
            }
            catch (PathGenerationException ex)
            {
                await RecordBatchFailureAsync(
                    requests,
                    null,
                    ex.Stage,
                    ex.Message,
                    ownsProgress);
                return new PathGenerationBatchResult(
                    requests.Count,
                    0,
                    0,
                    requests.Count,
                    0);
            }
            catch (Exception ex)
            {
                await RecordBatchFailureAsync(
                    requests,
                    null,
                    "runtime_identity",
                    ex.Message,
                    ownsProgress);
                return new PathGenerationBatchResult(
                    requests.Count,
                    0,
                    0,
                    requests.Count,
                    0);
            }

            Dictionary<string, PathGenerationState> initialStates;
            try
            {
                initialStates = _store.GetPathGenerationStates();
            }
            catch (Exception ex)
            {
                await RecordBatchFailureAsync(
                    requests,
                    runtime,
                    "state_read",
                    ex.Message,
                    ownsProgress);
                return new PathGenerationBatchResult(
                    requests.Count,
                    0,
                    0,
                    requests.Count,
                    0);
            }

            var tasks = requests.Select(request =>
            {
                initialStates.TryGetValue(request.SongId, out var state);
                return ProcessSongAsync(
                    request,
                    state,
                    key,
                    choptPath,
                    runtime,
                    force,
                    ownsProgress,
                    ct);
            });
            var outcomes = await Task.WhenAll(tasks);

            var result = new PathGenerationBatchResult(
                requests.Count,
                outcomes.Count(outcome => outcome == SongOutcome.Promoted),
                outcomes.Count(outcome => outcome == SongOutcome.Skipped),
                outcomes.Count(outcome => outcome == SongOutcome.Failed),
                outcomes.Count(outcome => outcome == SongOutcome.Conflicted));
            _log.LogInformation(
                "Path generation finished. Requested={Requested}, Promoted={Promoted}, Skipped={Skipped}, Failed={Failed}, Conflicted={Conflicted}.",
                result.Requested,
                result.Promoted,
                result.Skipped,
                result.Failed,
                result.Conflicted);
            return result;
        }
        finally
        {
            if (ownsProgress)
                _progress.EndPathGeneration();
        }
    }

    private async Task<SongOutcome> ProcessSongAsync(
        SongPathRequest request,
        PathGenerationState? initialState,
        byte[] key,
        string choptPath,
        PathGenerationRuntimeIdentity runtime,
        bool force,
        bool ownsProgress,
        CancellationToken ct)
    {
        var attemptId = Guid.NewGuid().ToString("N");
        var songLock = _songLocks.GetOrAdd(
            request.SongId,
            static _ => new SemaphoreSlim(1, 1));
        var expected = PathGenerationInstruments.NormalizeExpected(
            request.ExpectedInstruments);
        var acquiredImmediately = false;
        var lockAcquired = false;
        try
        {
            acquiredImmediately = await songLock.WaitAsync(0, ct);
            if (!acquiredImmediately)
                await songLock.WaitAsync(ct);
            lockAcquired = true;
        }
        catch (OperationCanceledException)
        {
            await AppendErrorBestEffortAsync(
                CreateError(
                    attemptId,
                    request,
                    null,
                    runtime,
                    "cancelled",
                    "Path generation was cancelled while waiting for the per-song lock."));
            if (ownsProgress)
                _progress.PathGenSongFailed();
            throw;
        }

        PathGenerationState? state = initialState;
        string? datHash = null;
        string? stagingDirectory = null;
        try
        {
            if (!acquiredImmediately)
                state = _store.GetPathGenerationState(request.SongId);

            if (ownsProgress)
                _progress.PathGenProcessing(request.Title);

            var dataDirectory = Path.GetFullPath(_options.Value.DataDirectory);
            if (expected.Length == 0)
            {
                throw new PathGenerationException(
                    "request_validation",
                    "The raw chart metadata contains none of gr/ba/ds/vl/pg/pb.");
            }

            byte[] datBytes;
            try
            {
                datBytes = await DownloadDatAsync(request.DatUrl, ct);
                datHash = MidiCryptor.ComputeHash(datBytes);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw new PathGenerationException(
                    "download",
                    $"Failed to download the encrypted chart: {ex.Message}",
                    innerException: ex);
            }

            if (!force &&
                state is not null &&
                CanSkipAfterDownload(
                    request,
                    state,
                    runtime,
                    expected,
                    datHash,
                    dataDirectory))
            {
                if (ownsProgress)
                    _progress.PathGenSongSkipped();
                return SongOutcome.Skipped;
            }

            stagingDirectory = Path.Combine(
                dataDirectory,
                ".path-work",
                attemptId);
            Directory.CreateDirectory(stagingDirectory);

            MidiTrackRenamer.MidiVariants variants;
            try
            {
                variants = MidiTrackRenamer.ProduceVariants(
                    MidiCryptor.Decrypt(datBytes, key));
            }
            catch (Exception ex)
            {
                throw new PathGenerationException(
                    "decrypt",
                    $"Failed to decrypt or transform the chart: {ex.Message}",
                    innerException: ex);
            }

            var proMidiPath = Path.Combine(stagingDirectory, "chart-pro.mid");
            var originalMidiPath = Path.Combine(stagingDirectory, "chart-og.mid");
            await File.WriteAllBytesAsync(proMidiPath, variants.ProMidi, ct);
            await File.WriteAllBytesAsync(originalMidiPath, variants.OgMidi, ct);
            await File.WriteAllTextAsync(
                Path.Combine(stagingDirectory, "song.ini"),
                BuildSongIni(request),
                ct);

            var artifactDirectory = Path.Combine(stagingDirectory, "artifacts");
            Directory.CreateDirectory(artifactDirectory);
            var maxScores = new SongMaxScores();
            var expertScores = new Dictionary<string, int>(StringComparer.Ordinal);

            foreach (var instrument in expected)
            {
                var definition = PathGenerationInstruments.GetDefinition(instrument);
                var midiPath = definition.MidiVariant == "pro"
                    ? proMidiPath
                    : originalMidiPath;
                var instrumentDirectory = Path.Combine(
                    artifactDirectory,
                    definition.Instrument);
                Directory.CreateDirectory(instrumentDirectory);

                foreach (var difficulty in PathGenerationInstruments.Difficulties)
                {
                    var pngPath = Path.Combine(
                        instrumentDirectory,
                        $"{difficulty}.png");
                    var jsonPath = Path.Combine(
                        instrumentDirectory,
                        $"{difficulty}.json");

                    await _choptConcurrency.WaitAsync(ct);
                    int? totalScore;
                    try
                    {
                        totalScore = await RunChoptAsync(
                            choptPath,
                            midiPath,
                            definition,
                            difficulty,
                            pngPath,
                            jsonPath,
                            ct);
                    }
                    finally
                    {
                        _choptConcurrency.Release();
                    }

                    if (difficulty == "expert")
                    {
                        var expertScore = totalScore ??
                            throw new PathGenerationException(
                                "artifact_validation",
                                "Expert JSON did not contain a maximum score.",
                                definition.Instrument,
                                difficulty);
                        expertScores[definition.Instrument] = expertScore;
                        maxScores.SetByInstrument(
                            definition.Instrument,
                            expertScore);
                    }
                }
            }

            var binaryHashAfterGeneration = await ComputeSha256Async(
                choptPath,
                ct);
            if (!binaryHashAfterGeneration.Equals(
                    runtime.BinarySha256,
                    StringComparison.Ordinal))
            {
                throw new PathGenerationException(
                    "runtime_identity",
                    "The CHOpt binary changed while path artifacts were being generated.");
            }

            var generatedAtUtc = DateTime.UtcNow;
            var manifest = new PathArtifactManifest(
                attemptId,
                request.SongId,
                datHash,
                request.LastModified,
                runtime.Version,
                runtime.BinarySha256,
                runtime.Profile,
                expected,
                expertScores,
                generatedAtUtc);
            await File.WriteAllTextAsync(
                Path.Combine(
                    artifactDirectory,
                    PathArtifactResolver.ManifestFileName),
                JsonSerializer.Serialize(
                    manifest,
                    PathArtifactManifest.JsonOptions),
                ct);

            var generationDirectory = PathArtifactResolver.GetGenerationDirectory(
                dataDirectory,
                request.SongId,
                attemptId);
            Directory.CreateDirectory(Path.GetDirectoryName(generationDirectory)!);
            try
            {
                Directory.Move(artifactDirectory, generationDirectory);
            }
            catch (Exception ex)
            {
                throw new PathGenerationException(
                    "promotion_move",
                    $"Failed to move the validated generation into immutable storage: {ex.Message}",
                    innerException: ex);
            }

            PathGenerationPromotionOutcome promotionOutcome;
            try
            {
                promotionOutcome = await _store.TryPromoteGenerationAsync(
                    new PathGenerationPromotion(
                        attemptId,
                        request.SongId,
                        state?.Revision ?? 0,
                        attemptId,
                        datHash,
                        request.LastModified,
                        generatedAtUtc,
                        runtime,
                        expected,
                        maxScores),
                    ct);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw new PathGenerationException(
                    "persistence",
                    $"The immutable generation was written, but its database pointer was not promoted: {ex.Message}",
                    innerException: ex);
            }

            if (promotionOutcome != PathGenerationPromotionOutcome.Promoted)
            {
                var detail = promotionOutcome == PathGenerationPromotionOutcome.Conflict
                    ? "A newer path generation revision won the compare-and-swap."
                    : "The song row no longer exists.";
                await AppendErrorBestEffortAsync(
                    CreateError(
                        attemptId,
                        request,
                        datHash,
                        runtime,
                        "concurrency",
                        detail));
                if (ownsProgress)
                    _progress.PathGenSongFailed();
                return promotionOutcome == PathGenerationPromotionOutcome.Conflict
                    ? SongOutcome.Conflicted
                    : SongOutcome.Failed;
            }

            _songsCache.Invalidate();
            if (ownsProgress)
                _progress.PathGenSongCompleted();
            return SongOutcome.Promoted;
        }
        catch (OperationCanceledException)
        {
            await AppendErrorBestEffortAsync(
                CreateError(
                    attemptId,
                    request,
                    datHash,
                    runtime,
                    "cancelled",
                    "Path generation was cancelled."));
            if (ownsProgress)
                _progress.PathGenSongFailed();
            throw;
        }
        catch (PathGenerationException ex)
        {
            await AppendErrorBestEffortAsync(
                CreateError(
                    attemptId,
                    request,
                    datHash,
                    runtime,
                    ex.Stage,
                    ex.Message,
                    ex.Instrument,
                    ex.Difficulty));
            _log.LogWarning(
                ex,
                "Path generation failed for {SongId} at {Stage} ({Instrument}/{Difficulty}).",
                request.SongId,
                ex.Stage,
                ex.Instrument,
                ex.Difficulty);
            if (ownsProgress)
                _progress.PathGenSongFailed();
            return SongOutcome.Failed;
        }
        catch (Exception ex)
        {
            await AppendErrorBestEffortAsync(
                CreateError(
                    attemptId,
                    request,
                    datHash,
                    runtime,
                    "unexpected",
                    ex.Message));
            _log.LogError(
                ex,
                "Unexpected path generation failure for {SongId}.",
                request.SongId);
            if (ownsProgress)
                _progress.PathGenSongFailed();
            return SongOutcome.Failed;
        }
        finally
        {
            if (stagingDirectory is not null)
            {
                try
                {
                    if (Directory.Exists(stagingDirectory))
                        Directory.Delete(stagingDirectory, recursive: true);
                }
                catch (Exception ex)
                {
                    _log.LogDebug(
                        ex,
                        "Could not remove path staging directory {Directory}.",
                        stagingDirectory);
                }
            }

            if (lockAcquired)
                songLock.Release();
        }
    }

    internal async Task<int?> RunChoptAsync(
        string choptPath,
        string midiFile,
        PathInstrumentDefinition instrument,
        string difficulty,
        string outputImage,
        string jsonOutput,
        CancellationToken ct)
    {
        var startInfo = CreateProcessStartInfo(choptPath);
        startInfo.ArgumentList.Add("-f");
        startInfo.ArgumentList.Add(midiFile);
        startInfo.ArgumentList.Add("-i");
        startInfo.ArgumentList.Add(instrument.ChoptInstrument);
        startInfo.ArgumentList.Add("-d");
        startInfo.ArgumentList.Add(difficulty);
        startInfo.ArgumentList.Add("-o");
        startInfo.ArgumentList.Add(outputImage);
        startInfo.ArgumentList.Add("--engine");
        startInfo.ArgumentList.Add("fnf");
        startInfo.ArgumentList.Add("--early-whammy");
        startInfo.ArgumentList.Add("0");
        startInfo.ArgumentList.Add("--squeeze");
        startInfo.ArgumentList.Add("20");
        startInfo.ArgumentList.Add("--json");

        ProcessResult result;
        try
        {
            result = await RunProcessAsync(startInfo, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new PathGenerationException(
                "chopt_execution",
                $"CHOpt could not be executed: {ex.Message}",
                instrument.Instrument,
                difficulty,
                ex);
        }
        if (result.ExitCode != 0)
        {
            throw new PathGenerationException(
                "chopt_execution",
                BoundDetail(
                    $"CHOpt exited with code {result.ExitCode}. stderr: {result.StandardError.Trim()}"),
                instrument.Instrument,
                difficulty);
        }

        if (!PathArtifactValidator.IsValidPng(outputImage))
        {
            throw new PathGenerationException(
                "artifact_validation",
                "CHOpt did not produce a non-empty PNG with a valid signature.",
                instrument.Instrument,
                difficulty);
        }

        var requirePositiveScore = difficulty == "expert";
        if (!PathArtifactValidator.TryParseJson(
                result.StandardOutput,
                requirePositiveScore,
                out var totalScore))
        {
            throw new PathGenerationException(
                "artifact_validation",
                requirePositiveScore
                    ? "CHOpt JSON was malformed or had a non-positive expert totalScore."
                    : "CHOpt JSON was malformed.",
                instrument.Instrument,
                difficulty);
        }

        await File.WriteAllTextAsync(jsonOutput, result.StandardOutput, ct);
        return totalScore;
    }

    internal async Task<PathGenerationRuntimeIdentity> DetectRuntimeIdentityAsync(
        string choptPath,
        CancellationToken ct)
    {
        var hashBefore = await ComputeSha256Async(choptPath, ct);
        var startInfo = CreateProcessStartInfo(choptPath);
        startInfo.ArgumentList.Add("--version");

        using var timeout = new CancellationTokenSource(VersionTimeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            ct,
            timeout.Token);
        ProcessResult result;
        try
        {
            result = await RunProcessAsync(startInfo, linked.Token);
        }
        catch (OperationCanceledException) when (
            timeout.IsCancellationRequested &&
            !ct.IsCancellationRequested)
        {
            throw new PathGenerationException(
                "runtime_version",
                $"CHOpt --version did not complete within {VersionTimeout.TotalSeconds:0} seconds.");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new PathGenerationException(
                "runtime_version",
                $"CHOpt --version could not be executed: {ex.Message}",
                innerException: ex);
        }

        if (result.ExitCode != 0)
        {
            throw new PathGenerationException(
                "runtime_version",
                BoundDetail(
                    $"CHOpt --version exited with code {result.ExitCode}. stderr: {result.StandardError.Trim()}"));
        }

        var version = ParseVersionOutput(
            result.StandardOutput,
            result.StandardError);
        if (version is null)
        {
            throw new PathGenerationException(
                "runtime_version",
                "CHOpt --version returned no parseable version.");
        }

        var hashAfter = await ComputeSha256Async(choptPath, ct);
        if (!hashBefore.Equals(hashAfter, StringComparison.Ordinal))
        {
            throw new PathGenerationException(
                "runtime_version",
                "The CHOpt binary changed while its runtime identity was being detected.");
        }

        var profile = _options.Value.PathGenerationProfile?.Trim();
        if (string.IsNullOrWhiteSpace(profile))
        {
            throw new PathGenerationException(
                "configuration",
                "PathGenerationProfile must be non-empty.");
        }

        return new PathGenerationRuntimeIdentity(
            version,
            hashAfter,
            profile);
    }

    public static string? ParseVersionOutput(
        string standardOutput,
        string standardError = "")
    {
        var match = ChoptVersionRegex().Match(
            string.Concat(standardOutput, "\n", standardError));
        return match.Success
            ? match.Groups["version"].Value
            : null;
    }

    private static async Task<ProcessResult> RunProcessAsync(
        ProcessStartInfo startInfo,
        CancellationToken ct)
    {
        using var process = new Process { StartInfo = startInfo };
        if (!process.Start())
            throw new InvalidOperationException($"Failed to start '{startInfo.FileName}'.");

        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        try
        {
            await process.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            try
            {
                if (!process.HasExited)
                    process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
            }

            try
            {
                await process.WaitForExitAsync(CancellationToken.None);
            }
            catch (InvalidOperationException)
            {
            }

            await Task.WhenAll(stdoutTask, stderrTask);
            throw;
        }

        await Task.WhenAll(stdoutTask, stderrTask);
        return new ProcessResult(
            process.ExitCode,
            stdoutTask.Result,
            stderrTask.Result);
    }

    private static ProcessStartInfo CreateProcessStartInfo(string choptPath)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = choptPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        var libsDirectory = Path.Combine(
            Path.GetDirectoryName(choptPath)!,
            "libs");
        if (Directory.Exists(libsDirectory))
            startInfo.Environment["LD_LIBRARY_PATH"] = libsDirectory;

        return startInfo;
    }

    private async Task<byte[]> DownloadDatAsync(
        string url,
        CancellationToken ct)
    {
        using var response = await _http.GetAsync(url, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsByteArrayAsync(ct);
    }

    private static async Task<string> ComputeSha256Async(
        string path,
        CancellationToken ct)
    {
        await using var stream = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(stream, ct);
        return Convert.ToHexStringLower(hash);
    }

    private byte[]? GetMidiKey(ScraperOptions options)
    {
        var keyHex = options.MidiEncryptionKey;
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

    private static string? GetChoptPath(ScraperOptions options)
    {
        var path = Path.GetFullPath(options.CHOptPath);
        if (File.Exists(path))
            return path;
        if (OperatingSystem.IsWindows() && File.Exists(path + ".exe"))
            return path + ".exe";
        return null;
    }

    private static bool CanSkipAfterDownload(
        SongPathRequest request,
        PathGenerationState state,
        PathGenerationRuntimeIdentity runtime,
        IReadOnlyList<string> expected,
        string datHash,
        string dataDirectory)
        => state.DatFileHash == datHash &&
           state.SongLastModified == request.LastModified &&
           HasCompleteMatchingGeneration(
               state,
               runtime,
               expected,
               dataDirectory);

    private static bool HasCompleteMatchingGeneration(
        PathGenerationState state,
        PathGenerationRuntimeIdentity runtime,
        IReadOnlyList<string> expected,
        string dataDirectory)
    {
        if (state.GeneratedAtUtc is null ||
            state.ChoptVersion != runtime.Version ||
            state.ChoptBinarySha256 != runtime.BinarySha256 ||
            state.GenerationProfile != runtime.Profile)
        {
            return false;
        }

        var stateExpected = PathGenerationInstruments.NormalizeExpected(
            state.ExpectedInstruments);
        if (!stateExpected.SequenceEqual(expected, StringComparer.Ordinal))
            return false;

        if (expected.Any(instrument =>
                state.MaxScores.GetByInstrument(instrument) is not > 0))
        {
            return false;
        }

        return PathArtifactResolver.IsGenerationComplete(
            dataDirectory,
            state);
    }

    private async Task RecordBatchFailureAsync(
        IReadOnlyList<SongPathRequest> requests,
        PathGenerationRuntimeIdentity? runtime,
        string stage,
        string detail,
        bool ownsProgress)
    {
        foreach (var request in requests)
        {
            await AppendErrorBestEffortAsync(
                CreateError(
                    Guid.NewGuid().ToString("N"),
                    request,
                    null,
                    runtime,
                    stage,
                    detail));
            if (ownsProgress)
                _progress.PathGenSongFailed();
        }
    }

    private async Task AppendErrorBestEffortAsync(PathGenerationError error)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            await _store.AppendPathGenerationErrorAsync(error, timeout.Token);
        }
        catch (Exception ex)
        {
            _log.LogWarning(
                ex,
                "Could not append path generation error for {SongId}/{AttemptId}.",
                error.SongId,
                error.AttemptId);
        }
    }

    private PathGenerationError CreateError(
        string attemptId,
        SongPathRequest request,
        string? datHash,
        PathGenerationRuntimeIdentity? runtime,
        string stage,
        string detail,
        string? instrument = null,
        string? difficulty = null)
        => new(
            attemptId,
            request.SongId,
            datHash,
            runtime?.Version,
            runtime?.BinarySha256,
            runtime?.Profile ?? _options.Value.PathGenerationProfile,
            PathGenerationInstruments.NormalizeExpected(
                request.ExpectedInstruments),
            stage,
            instrument,
            difficulty,
            BoundDetail(detail),
            DateTime.UtcNow);

    private static string BuildSongIni(SongPathRequest request)
        => $"[song]\nname = {SingleLine(request.Title)}\nartist = {SingleLine(request.Artist)}\ncharter = Epic Games\n";

    private static string SingleLine(string value)
        => value.Replace('\r', ' ').Replace('\n', ' ');

    private static string BoundDetail(string detail)
    {
        var sanitized = detail.Replace('\0', ' ');
        return sanitized.Length <= ErrorDetailLimit
            ? sanitized
            : sanitized[..ErrorDetailLimit];
    }

    [GeneratedRegex(
        @"(?<![0-9A-Za-z])v?(?<version>\d+(?:\.\d+){1,3}(?:[-+][0-9A-Za-z.-]+)?)",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex ChoptVersionRegex();

    private sealed record ProcessResult(
        int ExitCode,
        string StandardOutput,
        string StandardError);

    private enum SongOutcome
    {
        Promoted,
        Skipped,
        Failed,
        Conflicted,
    }
}

internal sealed class PathGenerationException : Exception
{
    public PathGenerationException(
        string stage,
        string message,
        string? instrument = null,
        string? difficulty = null,
        Exception? innerException = null)
        : base(message, innerException)
    {
        Stage = stage;
        Instrument = instrument;
        Difficulty = difficulty;
    }

    public string Stage { get; }
    public string? Instrument { get; }
    public string? Difficulty { get; }
}
