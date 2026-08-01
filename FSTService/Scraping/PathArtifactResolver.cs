using System.Text.Json;
using Microsoft.Extensions.Options;

namespace FSTService.Scraping;

public sealed record ResolvedPathArtifact(
    string FilePath,
    string? GenerationId,
    bool IsLegacy);

public sealed class PathArtifactResolver
{
    internal const string ManifestFileName = "generation.json";

    private readonly IPathDataStore _store;
    private readonly IOptions<ScraperOptions> _options;

    public PathArtifactResolver(
        IPathDataStore store,
        IOptions<ScraperOptions> options)
    {
        _store = store;
        _options = options;
    }

    public ResolvedPathArtifact? Resolve(
        string songId,
        string instrument,
        string difficulty,
        string extension,
        string? requestedGenerationId = null)
    {
        var currentGenerationId =
            _store.GetPathGenerationState(songId)?.ArtifactGenerationId;
        if (requestedGenerationId is not null &&
            !string.Equals(
                requestedGenerationId,
                currentGenerationId,
                StringComparison.Ordinal))
        {
            return null;
        }

        var generationId = currentGenerationId;
        return Resolve(
            _options.Value.DataDirectory,
            songId,
            instrument,
            difficulty,
            extension,
            generationId);
    }

    internal static ResolvedPathArtifact? Resolve(
        string dataDirectory,
        string songId,
        string instrument,
        string difficulty,
        string extension,
        string? generationId)
    {
        if (!IsSafePathSegment(songId) ||
            (generationId is not null && !IsSafePathSegment(generationId)) ||
            !PathGenerationInstruments.Definitions.Any(
                definition => definition.Instrument == instrument) ||
            !PathGenerationInstruments.Difficulties.Contains(
                difficulty,
                StringComparer.OrdinalIgnoreCase) ||
            extension is not ("png" or "json"))
        {
            return null;
        }

        var dataRoot = Path.GetFullPath(dataDirectory);
        var relativeSegments = generationId is null
            ? new[] { "paths", songId, instrument, $"{difficulty.ToLowerInvariant()}.{extension}" }
            : new[]
            {
                "paths",
                songId,
                "generations",
                generationId,
                instrument,
                $"{difficulty.ToLowerInvariant()}.{extension}",
            };
        var candidate = Path.GetFullPath(Path.Combine([dataRoot, .. relativeSegments]));
        if (!IsWithin(dataRoot, candidate))
            return null;

        return new ResolvedPathArtifact(
            candidate,
            generationId,
            generationId is null);
    }

    internal static string GetGenerationDirectory(
        string dataDirectory,
        string songId,
        string generationId)
    {
        if (!IsSafePathSegment(songId) ||
            !IsSafePathSegment(generationId))
        {
            throw new InvalidOperationException("Path generation identifiers must be single safe path segments.");
        }

        var dataRoot = Path.GetFullPath(dataDirectory);
        var generationDirectory = Path.GetFullPath(
            Path.Combine(dataRoot, "paths", songId, "generations", generationId));
        if (!IsWithin(dataRoot, generationDirectory))
            throw new InvalidOperationException("Resolved path generation directory escapes the configured data directory.");

        return generationDirectory;
    }

    internal static bool IsGenerationComplete(
        string dataDirectory,
        PathGenerationState state)
    {
        if (string.IsNullOrWhiteSpace(state.ArtifactGenerationId))
            return false;

        try
        {
            var generationDirectory = GetGenerationDirectory(
                dataDirectory,
                state.SongId,
                state.ArtifactGenerationId);
            var manifestPath = Path.Combine(generationDirectory, ManifestFileName);
            if (!File.Exists(manifestPath))
                return false;

            var manifest = JsonSerializer.Deserialize<PathArtifactManifest>(
                File.ReadAllText(manifestPath),
                PathArtifactManifest.JsonOptions);
            if (manifest is null ||
                manifest.GenerationId != state.ArtifactGenerationId ||
                manifest.SongId != state.SongId ||
                manifest.DatFileHash != state.DatFileHash ||
                manifest.SongLastModified != state.SongLastModified ||
                manifest.ChoptVersion != state.ChoptVersion ||
                manifest.ChoptBinarySha256 != state.ChoptBinarySha256 ||
                manifest.GenerationProfile != state.GenerationProfile ||
                manifest.ExpectedInstruments is null ||
                manifest.ExpertMaxScores is null)
            {
                return false;
            }

            var expected = PathGenerationInstruments.NormalizeExpected(
                state.ExpectedInstruments);
            if (!manifest.ExpectedInstruments.SequenceEqual(
                    expected,
                    StringComparer.Ordinal))
            {
                return false;
            }

            foreach (var instrument in expected)
            {
                var expectedMax = state.MaxScores.GetByInstrument(instrument);
                if (expectedMax is not > 0 ||
                    !manifest.ExpertMaxScores.TryGetValue(instrument, out var manifestMax) ||
                    manifestMax != expectedMax)
                {
                    return false;
                }

                foreach (var difficulty in PathGenerationInstruments.Difficulties)
                {
                    var pngPath = Path.Combine(
                        generationDirectory,
                        instrument,
                        $"{difficulty}.png");
                    var jsonPath = Path.Combine(
                        generationDirectory,
                        instrument,
                        $"{difficulty}.json");
                    if (!PathArtifactValidator.IsValidPng(pngPath) ||
                        !PathArtifactValidator.TryReadJson(
                            jsonPath,
                            requirePositiveScore: difficulty == "expert",
                            out var score))
                    {
                        return false;
                    }

                    if (difficulty == "expert" && score != expectedMax)
                        return false;
                }
            }

            return expected.Length > 0;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
        catch (JsonException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static bool IsWithin(string root, string candidate)
    {
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        var normalizedRoot = Path.TrimEndingDirectorySeparator(root);
        var rootPrefix = normalizedRoot + Path.DirectorySeparatorChar;
        return candidate.Equals(normalizedRoot, comparison) ||
               candidate.StartsWith(rootPrefix, comparison);
    }

    private static bool IsSafePathSegment(string value)
        => !string.IsNullOrWhiteSpace(value) &&
           value is not "." and not ".." &&
           value.IndexOfAny(
               [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]) < 0 &&
           !value.Any(char.IsControl);
}

internal sealed record PathArtifactManifest(
    string GenerationId,
    string SongId,
    string DatFileHash,
    string? SongLastModified,
    string ChoptVersion,
    string ChoptBinarySha256,
    string GenerationProfile,
    string[] ExpectedInstruments,
    Dictionary<string, int> ExpertMaxScores,
    DateTime GeneratedAtUtc)
{
    internal static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };
}

internal static class PathArtifactValidator
{
    private static readonly byte[] PngSignature =
        [0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a];

    public static bool IsValidPng(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            if (stream.Length <= PngSignature.Length)
                return false;

            Span<byte> signature = stackalloc byte[PngSignature.Length];
            stream.ReadExactly(signature);
            return signature.SequenceEqual(PngSignature);
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    public static bool TryReadJson(
        string path,
        bool requirePositiveScore,
        out int? totalScore)
    {
        totalScore = null;
        try
        {
            return TryParseJson(
                File.ReadAllText(path),
                requirePositiveScore,
                out totalScore);
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    public static bool TryParseJson(
        string json,
        bool requirePositiveScore,
        out int? totalScore)
    {
        totalScore = null;
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                return false;

            if (document.RootElement.TryGetProperty("totalScore", out var score) &&
                score.ValueKind == JsonValueKind.Number &&
                score.TryGetInt32(out var parsed))
            {
                totalScore = parsed;
            }

            return !requirePositiveScore || totalScore is > 0;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
