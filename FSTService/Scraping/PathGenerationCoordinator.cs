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
    private static readonly TimeSpan ErrorRecordingTimeout =
        TimeSpan.FromSeconds(5);
    private const int ErrorDetailLimit = 2048;

    private readonly HttpClient _http;
    private readonly IPathDataStore _store;
    private readonly SongsCacheService _songsCache;
    private readonly IOptions<ScraperOptions> _options;
    private readonly ScrapeProgressTracker _progress;
    private readonly ILogger<PathGenerationCoordinator> _log;
    private readonly IPathGenerationAdmissionLeaseProvider _admissionLeaseProvider;
    private readonly SemaphoreSlim _choptConcurrency;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _songLocks =
        new(StringComparer.OrdinalIgnoreCase);

    public PathGenerationCoordinator(
        HttpClient http,
        IPathDataStore store,
        SongsCacheService songsCache,
        IOptions<ScraperOptions> options,
        ScrapeProgressTracker progress,
        ILogger<PathGenerationCoordinator> log,
        IPathGenerationAdmissionLeaseProvider admissionLeaseProvider)
    {
        _http = http;
        _store = store;
        _songsCache = songsCache;
        _options = options;
        _progress = progress;
        _log = log;
        _admissionLeaseProvider = admissionLeaseProvider
            ?? throw new ArgumentNullException(nameof(admissionLeaseProvider));
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
        IAsyncDisposable? admissionLease = null;

        async ValueTask ReleaseAdmissionAsync()
        {
            var lease = admissionLease;
            if (lease is null)
                return;

            admissionLease = null;
            await lease.DisposeAsync();
        }

        try
        {
            try
            {
                admissionLease =
                    await _admissionLeaseProvider.AcquireAsync(ct);
            }
            catch (OperationCanceledException)
            {
                _log.LogInformation(
                    "Path generation was cancelled while waiting for " +
                    "distributed admission.");
                await RecordBatchFailureAsync(
                    requests,
                    null,
                    "cancelled",
                    "Path generation was cancelled while waiting for " +
                    "distributed admission.",
                    ownsProgress);
                throw;
            }
            catch (Exception ex)
            {
                _log.LogWarning(
                    ex,
                    "Distributed path-generation admission failed.");
                await RecordBatchFailureAsync(
                    requests,
                    null,
                    "admission",
                    $"Distributed path-generation admission failed: {ex.Message}",
                    ownsProgress);
                return new PathGenerationBatchResult(
                    requests.Count,
                    0,
                    0,
                    requests.Count,
                    0);
            }

            PathGenerationExecutionContext execution;
            try
            {
                execution = await CreateExecutionContextAsync(ct);
            }
            catch (OperationCanceledException)
            {
                await ReleaseAdmissionAsync();
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
                await ReleaseAdmissionAsync();
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
                await ReleaseAdmissionAsync();
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
                await ReleaseAdmissionAsync();
                await RecordBatchFailureAsync(
                    requests,
                    execution.Runtime,
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
                    execution,
                    force,
                    ownsProgress,
                    ct);
            });
            var outcomes = await Task.WhenAll(tasks);

            var result = new PathGenerationBatchResult(
                requests.Count,
                outcomes.Count(outcome =>
                    outcome.Outcome == PathGenerationAttemptOutcome.Promoted),
                outcomes.Count(outcome =>
                    outcome.Outcome == PathGenerationAttemptOutcome.Skipped),
                outcomes.Count(outcome =>
                    outcome.Outcome == PathGenerationAttemptOutcome.Failed),
                outcomes.Count(outcome =>
                    outcome.Outcome == PathGenerationAttemptOutcome.Conflicted));
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
            await ReleaseAdmissionAsync();
            if (ownsProgress)
                _progress.EndPathGeneration();
        }
    }

    public Task<PathGenerationBatchResult> GenerateAutomaticPathsAsync(
        IReadOnlyCollection<Song> songs,
        CancellationToken ct)
    {
        var options = _options.Value;
        if (!options.EnablePathGeneration ||
            !options.EnableAutomaticPathGeneration)
        {
            return Task.FromResult(
                new PathGenerationBatchResult(0, 0, 0, 0, 0));
        }

        var pendingSongIds =
            _store.GetPendingPathGenerationSongIds();
        var candidates = songs
            .Where(song =>
                SongPathRequest.FromSong(song) is { } request &&
                pendingSongIds.Contains(request.SongId))
            .ToArray();
        return GeneratePathsAsync(candidates, force: false, ct);
    }

    private async Task<PathGenerationAttemptResult> ProcessSongAsync(
        SongPathRequest request,
        PathGenerationState? initialState,
        PathGenerationExecutionContext execution,
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
                    execution.Runtime,
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
            if (state?.CatalogLastModified is { } catalogLastModified &&
                !ProviderTimestampIdentity.Equivalent(
                    catalogLastModified,
                    request.LastModified))
            {
                throw new PathGenerationException(
                    "state_validation",
                    "The exact catalog last-modified identity changed before path generation.");
            }

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
                    execution.Runtime,
                    expected,
                    datHash,
                    dataDirectory))
            {
                if (ownsProgress)
                    _progress.PathGenSongSkipped();
                return new PathGenerationAttemptResult(
                    PathGenerationAttemptOutcome.Skipped);
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
                    MidiCryptor.Decrypt(datBytes, execution.MidiKey));
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
                            execution.ChoptPath,
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
                execution.ChoptPath,
                ct);
            if (!binaryHashAfterGeneration.Equals(
                    execution.Runtime.BinarySha256,
                    StringComparison.Ordinal))
            {
                throw new PathGenerationException(
                    "runtime_identity",
                    "The CHOpt binary changed while path artifacts were being generated.");
            }

            // Keep generation.json and PostgreSQL timestamptz identity byte-for-byte comparable.
            var nowUtc = DateTime.UtcNow;
            var generatedAtUtc = new DateTime(
                nowUtc.Ticks - nowUtc.Ticks % 10,
                DateTimeKind.Utc);
            var manifest = new PathArtifactManifest(
                attemptId,
                request.SongId,
                datHash,
                request.LastModified,
                execution.Runtime.Version,
                execution.Runtime.BinarySha256,
                execution.Runtime.Profile,
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

            var promotion = new PathGenerationPromotion(
                attemptId,
                request.SongId,
                state?.Revision ?? 0,
                attemptId,
                datHash,
                request.LastModified,
                generatedAtUtc,
                execution.Runtime,
                expected,
                maxScores);

            PathGenerationPromotionOutcome promotionOutcome;
            try
            {
                using var cacheMutation =
                    _songsCache.BeginContentMutation();
                promotionOutcome = await _store.TryPromoteGenerationAsync(
                    promotion,
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
                        execution.Runtime,
                        "concurrency",
                        detail));
                await CleanupRejectedGenerationAsync(
                    request,
                    promotion,
                    generationDirectory);
                if (ownsProgress)
                    _progress.PathGenSongFailed();
                return new PathGenerationAttemptResult(
                    promotionOutcome == PathGenerationPromotionOutcome.Conflict
                        ? PathGenerationAttemptOutcome.Conflicted
                        : PathGenerationAttemptOutcome.Failed,
                    FailureStage: "concurrency",
                    Detail: detail);
            }

            if (ownsProgress)
                _progress.PathGenSongCompleted();
            return new PathGenerationAttemptResult(
                PathGenerationAttemptOutcome.Promoted);
        }
        catch (OperationCanceledException)
        {
            await AppendErrorBestEffortAsync(
                CreateError(
                    attemptId,
                    request,
                    datHash,
                    execution.Runtime,
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
                    execution.Runtime,
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
            return new PathGenerationAttemptResult(
                PathGenerationAttemptOutcome.Failed,
                FailureStage: ex.Stage,
                Detail: ex.Message);
        }
        catch (Exception ex)
        {
            await AppendErrorBestEffortAsync(
                CreateError(
                    attemptId,
                    request,
                    datHash,
                    execution.Runtime,
                    "unexpected",
                    ex.Message));
            _log.LogError(
                ex,
                "Unexpected path generation failure for {SongId}.",
                request.SongId);
            if (ownsProgress)
                _progress.PathGenSongFailed();
            return new PathGenerationAttemptResult(
                PathGenerationAttemptOutcome.Failed,
                FailureStage: "unexpected",
                Detail: ex.Message);
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
                "CHOpt did not produce a structurally valid PNG.",
                instrument.Instrument,
                difficulty);
        }

        var requirePositiveScore = difficulty == "expert";
        if (!PathArtifactValidator.TryParseJson(
                result.StandardOutput,
                requirePositiveScore,
                out var totalScore,
                requiredSchemaVersion:
                    PathArtifactValidator.RequiredSchemaVersion(
                        _options.Value.PathGenerationProfile)))
        {
            throw new PathGenerationException(
                "artifact_validation",
                requirePositiveScore
                    ? "CHOpt JSON did not match the path-data contract or had a non-positive expert totalScore."
                    : "CHOpt JSON did not match the path-data contract.",
                instrument.Instrument,
                difficulty);
        }

        await File.WriteAllTextAsync(jsonOutput, result.StandardOutput, ct);
        return totalScore;
    }

    private async Task<PathGenerationExecutionContext> CreateExecutionContextAsync(
        CancellationToken ct)
    {
        var key = GetMidiKey(_options.Value) ??
            throw new PathGenerationException(
                "configuration",
                "MIDI encryption key is not configured or is invalid.");
        var choptPath = GetChoptPath(_options.Value) ??
            throw new PathGenerationException(
                "configuration",
                $"CHOpt binary was not found at '{_options.Value.CHOptPath}'.");
        var runtime = await DetectRuntimeIdentityAsync(choptPath, ct);
        return new PathGenerationExecutionContext(key, choptPath, runtime);
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
        using var timeout = new CancellationTokenSource(
            ErrorRecordingTimeout);
        for (var index = 0; index < requests.Count; index++)
        {
            var error = CreateError(
                    Guid.NewGuid().ToString("N"),
                    requests[index],
                    null,
                    runtime,
                    stage,
                    detail);
            try
            {
                await _store.AppendPathGenerationErrorAsync(
                    error,
                    timeout.Token);
            }
            catch (OperationCanceledException) when (
                timeout.IsCancellationRequested)
            {
                var remaining = requests.Count - index;
                _log.LogWarning(
                    "Path generation batch error recording exceeded the " +
                    "{TimeoutSeconds:0}-second budget; skipped {Remaining:N0} " +
                    "of {Total:N0} durable error rows.",
                    ErrorRecordingTimeout.TotalSeconds,
                    remaining,
                    requests.Count);
                if (ownsProgress)
                {
                    for (var remainingIndex = index;
                         remainingIndex < requests.Count;
                         remainingIndex++)
                    {
                        _progress.PathGenSongFailed();
                    }
                }
                return;
            }
            catch (Exception ex)
            {
                _log.LogWarning(
                    ex,
                    "Could not append path generation error for " +
                    "{SongId}/{AttemptId}.",
                    error.SongId,
                    error.AttemptId);
            }

            if (ownsProgress)
                _progress.PathGenSongFailed();
        }
    }

    private async Task CleanupRejectedGenerationAsync(
        SongPathRequest request,
        PathGenerationPromotion promotion,
        string generationDirectory)
    {
        PathGenerationState? current;
        try
        {
            current = _store.GetPathGenerationState(promotion.SongId);
        }
        catch (Exception ex)
        {
            var detail =
                "Could not verify whether the rejected immutable generation " +
                $"is current; retained it: {ex.Message}";
            _log.LogWarning(
                ex,
                "Retaining rejected path generation {GenerationId} for " +
                "{SongId} because current-state verification failed.",
                promotion.ArtifactGenerationId,
                promotion.SongId);
            await AppendErrorBestEffortAsync(
                CreateError(
                    promotion.AttemptId,
                    request,
                    promotion.DatFileHash,
                    promotion.Runtime,
                    "orphan_cleanup",
                    detail));
            return;
        }

        if (string.Equals(
                current?.ArtifactGenerationId,
                promotion.ArtifactGenerationId,
                StringComparison.Ordinal))
        {
            _log.LogInformation(
                "Retained path generation {GenerationId} for {SongId} because " +
                "it is the current database generation despite the rejected " +
                "promotion result.",
                promotion.ArtifactGenerationId,
                promotion.SongId);
            return;
        }

        try
        {
            if (Directory.Exists(generationDirectory))
                Directory.Delete(generationDirectory, recursive: true);
            _log.LogInformation(
                "Removed unreachable rejected path generation {GenerationId} " +
                "for {SongId}.",
                promotion.ArtifactGenerationId,
                promotion.SongId);
        }
        catch (Exception ex)
        {
            var detail =
                "Could not remove the unreachable rejected immutable " +
                $"generation: {ex.Message}";
            _log.LogWarning(
                ex,
                "Could not remove rejected path generation {GenerationId} " +
                "for {SongId}.",
                promotion.ArtifactGenerationId,
                promotion.SongId);
            await AppendErrorBestEffortAsync(
                CreateError(
                    promotion.AttemptId,
                    request,
                    promotion.DatFileHash,
                    promotion.Runtime,
                    "orphan_cleanup",
                    detail));
        }
    }

    private async Task AppendErrorBestEffortAsync(PathGenerationError error)
    {
        using var timeout = new CancellationTokenSource(
            ErrorRecordingTimeout);
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
}

internal sealed record PathGenerationExecutionContext(
    byte[] MidiKey,
    string ChoptPath,
    PathGenerationRuntimeIdentity Runtime);

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
