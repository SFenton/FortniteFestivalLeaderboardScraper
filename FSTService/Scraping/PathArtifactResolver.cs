using System.Buffers.Binary;
using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;
using FSTService.Persistence;
using Microsoft.Extensions.Options;

namespace FSTService.Scraping;

public sealed record ResolvedPathArtifact(
    string FilePath,
    string? GenerationId,
    bool IsLegacy);

internal sealed record ValidatedPathGeneration(
    string GenerationDirectory,
    PathArtifactManifest Manifest,
    SongMaxScores MaxScores);

public sealed class PathArtifactResolver
{
    internal const string ManifestFileName = "generation.json";
    private const long MaximumManifestBytes = 256 * 1024;
    private const long MaximumArtifactJsonBytes = 64L * 1024 * 1024;
    private const long MaximumArtifactPngBytes = 256L * 1024 * 1024;

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
        var state = _store.GetPathGenerationState(songId);
        var currentGenerationId = state?.ArtifactGenerationId;
        if (requestedGenerationId is not null &&
            !string.Equals(
                requestedGenerationId,
                currentGenerationId,
                StringComparison.Ordinal))
        {
            return null;
        }

        var generationId =
            currentGenerationId is not null &&
            state!.ExpectedInstruments.Contains(
                instrument,
                StringComparer.Ordinal)
                ? currentGenerationId
                : null;
        if (currentGenerationId is not null &&
            generationId is null &&
            instrument is
                "Solo_PeripheralCymbals" or
                "Solo_PeripheralDrums")
        {
            return null;
        }

        return Resolve(
            _options.Value.DataDirectory,
            songId,
            instrument,
            difficulty,
            extension,
            generationId);
    }

    public bool IsUnavailableInCurrentGeneration(
        string songId,
        string instrument,
        string? requestedGenerationId)
    {
        if (instrument is not
            ("Solo_PeripheralCymbals" or "Solo_PeripheralDrums"))
        {
            return false;
        }

        var state = _store.GetPathGenerationState(songId);
        return state?.ArtifactGenerationId is { } currentGenerationId &&
               (requestedGenerationId is null ||
                string.Equals(
                    requestedGenerationId,
                    currentGenerationId,
                    StringComparison.Ordinal)) &&
               !state.ExpectedInstruments.Contains(
                   instrument,
                   StringComparer.Ordinal);
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
            var validated = ValidateImmutableGeneration(
                dataDirectory,
                state.SongId,
                state.ArtifactGenerationId);
            var manifest = validated.Manifest;
            if (manifest.DatFileHash != state.DatFileHash ||
                manifest.SongLastModified != state.SongLastModified ||
                manifest.ChoptVersion != state.ChoptVersion ||
                manifest.ChoptBinarySha256 != state.ChoptBinarySha256 ||
                manifest.GenerationProfile != state.GenerationProfile)
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
                    validated.MaxScores.GetByInstrument(instrument) != expectedMax)
                {
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

    internal static ValidatedPathGeneration ValidateImmutableGeneration(
        string dataDirectory,
        string songId,
        string generationId)
    {
        var dataRoot = Path.GetFullPath(dataDirectory);
        var generationDirectory = GetGenerationDirectory(
            dataRoot,
            songId,
            generationId);
        EnsureNoReparsePoints(dataRoot, generationDirectory);

        var directory = new DirectoryInfo(generationDirectory);
        if (!directory.Exists ||
            (directory.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidOperationException(
                $"Immutable path generation '{generationId}' does not identify a regular directory.");
        }

        var manifestPath = Path.Combine(generationDirectory, ManifestFileName);
        var manifestFile = GetRegularFile(manifestPath, "generation manifest");
        if (manifestFile.Length <= 0 ||
            manifestFile.Length > MaximumManifestBytes)
        {
            throw new InvalidOperationException(
                $"Immutable path generation '{generationId}' manifest size is invalid.");
        }

        PathArtifactManifest manifest;
        try
        {
            using var stream = new FileStream(
                manifestPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 16 * 1024,
                FileOptions.SequentialScan);
            manifest = JsonSerializer.Deserialize<PathArtifactManifest>(
                    stream,
                    PathArtifactManifest.JsonOptions)
                ?? throw new InvalidOperationException(
                    $"Immutable path generation '{generationId}' manifest cannot be JSON null.");
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                $"Immutable path generation '{generationId}' manifest is not strict JSON.",
                ex);
        }

        if (!string.Equals(manifest.GenerationId, generationId, StringComparison.Ordinal) ||
            !string.Equals(manifest.SongId, songId, StringComparison.Ordinal) ||
            !IsSha256(manifest.DatFileHash) ||
            string.IsNullOrWhiteSpace(manifest.ChoptVersion) ||
            !IsSha256(manifest.ChoptBinarySha256) ||
            string.IsNullOrWhiteSpace(manifest.GenerationProfile) ||
            manifest.GeneratedAtUtc == default ||
            manifest.ExpectedInstruments is null ||
            manifest.ExpertMaxScores is null)
        {
            throw new InvalidOperationException(
                $"Immutable path generation '{generationId}' manifest identity is invalid.");
        }

        var expected = PathGenerationInstruments.NormalizeExpected(
            manifest.ExpectedInstruments);
        if (expected.Length == 0 ||
            !manifest.ExpectedInstruments.SequenceEqual(
                expected,
                StringComparer.Ordinal) ||
            manifest.ExpertMaxScores.Count != expected.Length ||
            manifest.ExpertMaxScores.Keys.Any(key =>
                !expected.Contains(key, StringComparer.Ordinal)))
        {
            throw new InvalidOperationException(
                $"Immutable path generation '{generationId}' expected instrument set is invalid.");
        }

        var scores = new SongMaxScores
        {
            GeneratedAt = manifest.GeneratedAtUtc.ToString("o"),
            CHOptVersion = manifest.ChoptVersion,
            CHOptBinarySha256 = manifest.ChoptBinarySha256,
            GenerationProfile = manifest.GenerationProfile,
            ArtifactGenerationId = manifest.GenerationId,
            ExpectedInstruments = expected,
        };
        foreach (var instrument in expected)
        {
            if (!manifest.ExpertMaxScores.TryGetValue(
                    instrument,
                    out var expertMaximum) ||
                expertMaximum <= 0)
            {
                throw new InvalidOperationException(
                    $"Immutable path generation '{generationId}' has no positive expert maximum for {instrument}.");
            }

            var instrumentDirectory = Path.Combine(
                generationDirectory,
                instrument);
            EnsureNoReparsePoints(dataRoot, instrumentDirectory);
            var instrumentInfo = new DirectoryInfo(instrumentDirectory);
            if (!instrumentInfo.Exists ||
                (instrumentInfo.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidOperationException(
                    $"Immutable path generation '{generationId}' is missing instrument directory {instrument}.");
            }

            foreach (var difficulty in PathGenerationInstruments.Difficulties)
            {
                var pngPath = Path.Combine(
                    instrumentDirectory,
                    $"{difficulty}.png");
                var jsonPath = Path.Combine(
                    instrumentDirectory,
                    $"{difficulty}.json");
                var pngFile = GetRegularFile(
                    pngPath,
                    $"{instrument}/{difficulty} PNG");
                var jsonFile = GetRegularFile(
                    jsonPath,
                    $"{instrument}/{difficulty} JSON");
                if (pngFile.Length <= 0 ||
                    pngFile.Length > MaximumArtifactPngBytes ||
                    jsonFile.Length <= 0 ||
                    jsonFile.Length > MaximumArtifactJsonBytes)
                {
                    throw new InvalidOperationException(
                        $"Immutable path generation '{generationId}' artifact size is invalid for {instrument}/{difficulty}.");
                }
                if (!PathArtifactValidator.IsValidPng(pngPath) ||
                    !PathArtifactValidator.TryReadJson(
                        jsonPath,
                        requirePositiveScore: difficulty == "expert",
                        out var score,
                        requiredSchemaVersion:
                            PathArtifactValidator.RequiredSchemaVersion(
                                manifest.GenerationProfile)) ||
                    (difficulty == "expert" && score != expertMaximum))
                {
                    throw new InvalidOperationException(
                        $"Immutable path generation '{generationId}' failed artifact validation for {instrument}/{difficulty}.");
                }
            }

            scores.SetByInstrument(instrument, expertMaximum);
        }

        return new ValidatedPathGeneration(
            generationDirectory,
            manifest,
            scores);
    }

    internal static bool IsWithin(string root, string candidate)
    {
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        var normalizedRoot = Path.TrimEndingDirectorySeparator(root);
        var rootPrefix = normalizedRoot + Path.DirectorySeparatorChar;
        return candidate.Equals(normalizedRoot, comparison) ||
               candidate.StartsWith(rootPrefix, comparison);
    }

    private static FileInfo GetRegularFile(string path, string description)
    {
        var file = new FileInfo(path);
        if (!file.Exists ||
            (file.Attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
        {
            throw new InvalidOperationException(
                $"Immutable path {description} must be a regular non-symbolic-link file.");
        }

        return file;
    }

    private static void EnsureNoReparsePoints(string root, string candidate)
    {
        var normalizedRoot = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(root));
        var current = Path.GetFullPath(candidate);
        if (!IsWithin(normalizedRoot, current))
        {
            throw new InvalidOperationException(
                "Immutable path generation escapes the configured data directory.");
        }

        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        while (!current.Equals(normalizedRoot, comparison))
        {
            if (File.Exists(current) || Directory.Exists(current))
            {
                var attributes = File.GetAttributes(current);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    throw new InvalidOperationException(
                        $"Immutable path generation contains symbolic link '{current}'.");
                }
            }

            current = Path.GetDirectoryName(current)
                ?? throw new InvalidOperationException(
                    "Immutable path generation has no configured data-directory ancestor.");
        }
    }

    private static bool IsSha256(string? value)
        => value is { Length: 64 } &&
           value.All(static character =>
               character is >= '0' and <= '9' or >= 'a' and <= 'f');

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
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };
}

internal static class PathArtifactValidator
{
    internal const int CurrentSchemaVersion = 2;

    private static readonly byte[] PngSignature =
        [0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a];
    private static readonly byte[] IhdrChunk = "IHDR"u8.ToArray();
    private static readonly byte[] IdatChunk = "IDAT"u8.ToArray();
    private static readonly byte[] IendChunk = "IEND"u8.ToArray();
    private static readonly byte[] PlteChunk = "PLTE"u8.ToArray();
    private const int MaxPngChunkLength = 64 * 1024 * 1024;
    private const int MaxPngDimension = 32_768;
    private const long MaxPngDecodedBytes = 256L * 1024 * 1024;

    public static bool IsValidPng(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            if (stream.Length < PngSignature.Length + 12 + 13 + 12)
                return false;

            Span<byte> signature = stackalloc byte[PngSignature.Length];
            stream.ReadExactly(signature);
            if (!signature.SequenceEqual(PngSignature))
                return false;

            var sawHeader = false;
            var sawImageData = false;
            var sawEnd = false;
            var sawPalette = false;
            var leftImageData = false;
            PngHeader? header = null;
            using var compressedImageData = new MemoryStream();
            var lengthBytes = new byte[4];
            var chunkType = new byte[4];
            var crcBytes = new byte[4];
            while (stream.Position < stream.Length)
            {
                stream.ReadExactly(lengthBytes);
                var chunkLength = BinaryPrimitives.ReadUInt32BigEndian(lengthBytes);
                if (chunkLength > MaxPngChunkLength ||
                    chunkLength > stream.Length - stream.Position - 8)
                {
                    return false;
                }

                stream.ReadExactly(chunkType);
                if (!chunkType.All(static value =>
                        value is >= (byte)'A' and <= (byte)'Z'
                            or >= (byte)'a' and <= (byte)'z'))
                {
                    return false;
                }

                var chunkData = new byte[(int)chunkLength];
                stream.ReadExactly(chunkData);
                stream.ReadExactly(crcBytes);
                var expectedCrc = BinaryPrimitives.ReadUInt32BigEndian(crcBytes);
                if (ComputePngCrc(chunkType, chunkData) != expectedCrc)
                    return false;

                if (!sawHeader)
                {
                    if (!chunkType.SequenceEqual(IhdrChunk) ||
                        !TryReadPngHeader(chunkData, out var parsedHeader))
                    {
                        return false;
                    }

                    header = parsedHeader;
                    sawHeader = true;
                    continue;
                }

                if (chunkType.SequenceEqual(IhdrChunk))
                    return false;
                if (chunkType.SequenceEqual(IdatChunk))
                {
                    if (chunkLength == 0 ||
                        sawEnd ||
                        leftImageData ||
                        compressedImageData.Length + chunkLength
                            > MaxPngChunkLength)
                    {
                        return false;
                    }

                    compressedImageData.Write(chunkData);
                    sawImageData = true;
                }
                else if (chunkType.SequenceEqual(PlteChunk))
                {
                    if (sawImageData ||
                        chunkLength is < 3 or > 768 ||
                        chunkLength % 3 != 0 ||
                        header is null ||
                        header.Value.ColorType is 0 or 4)
                    {
                        return false;
                    }

                    sawPalette = true;
                }
                else if (chunkType.SequenceEqual(IendChunk))
                {
                    if (chunkLength != 0 ||
                        !sawImageData ||
                        header is null ||
                        (header.Value.ColorType == 3 && !sawPalette) ||
                        stream.Position != stream.Length)
                    {
                        return false;
                    }

                    sawEnd = true;
                }
                else
                {
                    if (sawImageData)
                        leftImageData = true;
                    if (sawEnd || IsCriticalPngChunk(chunkType))
                        return false;
                }
            }

            return sawHeader &&
                   sawImageData &&
                   sawEnd &&
                   header is not null &&
                   CanDecodePngImageData(
                       header.Value,
                       compressedImageData.ToArray());
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
        catch (InvalidDataException)
        {
            return false;
        }
    }

    public static bool TryReadJson(
        string path,
        bool requirePositiveScore,
        out int? totalScore,
        int? requiredSchemaVersion = null)
    {
        totalScore = null;
        try
        {
            return TryParseJson(
                File.ReadAllText(path),
                requirePositiveScore,
                out totalScore,
                requiredSchemaVersion);
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
        out int? totalScore,
        int? requiredSchemaVersion = null)
    {
        totalScore = null;
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                return false;

            var root = document.RootElement;
            if ((requiredSchemaVersion is not null &&
                 (!root.TryGetProperty("schemaVersion", out var schemaVersion) ||
                  schemaVersion.ValueKind != JsonValueKind.Number ||
                  !schemaVersion.TryGetInt32(out var parsedSchemaVersion) ||
                  parsedSchemaVersion != requiredSchemaVersion)) ||
                !HasString(root, "songName") ||
                !HasString(root, "artist") ||
                !HasString(root, "charter") ||
                !HasString(root, "difficulty") ||
                !HasString(root, "pathSummary") ||
                !HasArray(
                    root,
                    "activations",
                    activation => ValidateActivation(
                        activation,
                        requiredSchemaVersion)) ||
                !HasArray(root, "notes", ValidateNote) ||
                !HasArray(root, "spPhrases") ||
                !HasArray(root, "measures") ||
                !HasArray(root, "bpms") ||
                !HasArray(root, "timeSignatures") ||
                !root.TryGetProperty("totalScore", out var score) ||
                score.ValueKind != JsonValueKind.Number ||
                !score.TryGetInt32(out var parsed))
            {
                return false;
            }

            totalScore = parsed;
            return !requirePositiveScore || totalScore is > 0;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool HasString(JsonElement root, string propertyName)
        => root.TryGetProperty(propertyName, out var property) &&
           property.ValueKind == JsonValueKind.String;

    private static bool HasArray(
        JsonElement root,
        string propertyName,
        Func<JsonElement, bool>? validateElement = null)
    {
        if (!root.TryGetProperty(propertyName, out var property) ||
            property.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        return validateElement is null ||
               property.EnumerateArray().All(validateElement);
    }

    private static bool ValidateNote(JsonElement note)
    {
        if (note.ValueKind != JsonValueKind.Object ||
            !HasNumber(note, "beat") ||
            !HasBoolean(note, "isSpNote") ||
            !note.TryGetProperty("frets", out var frets) ||
            frets.ValueKind != JsonValueKind.Object ||
            (note.TryGetProperty("seconds", out var seconds) &&
             seconds.ValueKind != JsonValueKind.Number))
        {
            return false;
        }

        return frets.EnumerateObject().All(static fret =>
            fret.Value.ValueKind == JsonValueKind.Number);
    }

    private static bool ValidateActivation(
        JsonElement activation,
        int? requiredSchemaVersion)
    {
        if (activation.ValueKind != JsonValueKind.Object ||
            !HasNumber(activation, "startBeat") ||
            !HasNumber(activation, "endBeat") ||
            !IsOptionalNumber(activation, "startSeconds") ||
            !IsOptionalNumber(activation, "endSeconds") ||
            !IsOptionalNumber(activation, "scoreBeforeActivation"))
        {
            return false;
        }

        if (requiredSchemaVersion == CurrentSchemaVersion &&
            (!HasNonEmptyString(activation, "instruction") ||
             !HasNumber(activation, "activationBeat") ||
             !HasNumber(activation, "activationSeconds") ||
             !HasNumber(activation, "scoreBeforeActivation") ||
             !HasNumberInRange(
                 activation,
                 "odAtActivation",
                 0.0,
                 1.0) ||
             !HasNumber(activation, "anchorBeat") ||
             !HasNumber(activation, "anchorSeconds") ||
             !IsOptionalNumber(activation, "beatsAfterAnchor")))
        {
            return false;
        }

        if (!activation.TryGetProperty("startNotes", out var startNotes))
            return true;
        if (startNotes.ValueKind != JsonValueKind.Array)
            return false;

        return startNotes.EnumerateArray().All(static note =>
            note.ValueKind == JsonValueKind.Object &&
            HasNumber(note, "beat") &&
            HasNumber(note, "cumulativeScore") &&
            HasNumber(note, "noteValue") &&
            HasNumber(note, "odPercent") &&
            HasBoolean(note, "isSpGranting") &&
            IsOptionalNumber(note, "seconds"));
    }

    private static bool HasNumber(JsonElement root, string propertyName)
        => root.TryGetProperty(propertyName, out var property) &&
           property.ValueKind == JsonValueKind.Number;

    private static bool HasNumberInRange(
        JsonElement root,
        string propertyName,
        double minimum,
        double maximum)
        => root.TryGetProperty(propertyName, out var property) &&
           property.ValueKind == JsonValueKind.Number &&
           property.TryGetDouble(out var value) &&
           value >= minimum &&
           value <= maximum;

    private static bool HasBoolean(JsonElement root, string propertyName)
        => root.TryGetProperty(propertyName, out var property) &&
           property.ValueKind is JsonValueKind.True or JsonValueKind.False;

    private static bool HasNonEmptyString(
        JsonElement root,
        string propertyName)
        => root.TryGetProperty(propertyName, out var property) &&
           property.ValueKind == JsonValueKind.String &&
           !string.IsNullOrWhiteSpace(property.GetString());

    private static bool IsOptionalNumber(
        JsonElement root,
        string propertyName)
        => !root.TryGetProperty(propertyName, out var property) ||
           property.ValueKind == JsonValueKind.Number;

    internal static int? RequiredSchemaVersion(string? generationProfile)
        => generationProfile?.EndsWith("-v2", StringComparison.Ordinal) == true ||
           generationProfile == "chopt-fnf-ew0-s20-json-png-prodrums-v3"
                ? CurrentSchemaVersion
                : null;

    private static bool TryReadPngHeader(
        ReadOnlySpan<byte> header,
        out PngHeader parsed)
    {
        parsed = default;
        if (header.Length != 13)
            return false;

        var width = BinaryPrimitives.ReadUInt32BigEndian(header[..4]);
        var height = BinaryPrimitives.ReadUInt32BigEndian(header.Slice(4, 4));
        var bitDepth = header[8];
        var colorType = header[9];
        if (width == 0 ||
            height == 0 ||
            width > MaxPngDimension ||
            height > MaxPngDimension ||
            header[10] != 0 ||
            header[11] != 0 ||
            header[12] != 0)
        {
            return false;
        }

        var valid = colorType switch
        {
            0 => bitDepth is 1 or 2 or 4 or 8 or 16,
            2 => bitDepth is 8 or 16,
            3 => bitDepth is 1 or 2 or 4 or 8,
            4 => bitDepth is 8 or 16,
            6 => bitDepth is 8 or 16,
            _ => false,
        };
        if (!valid)
            return false;

        parsed = new PngHeader(
            (int)width,
            (int)height,
            bitDepth,
            colorType);
        return true;
    }

    private static bool CanDecodePngImageData(
        PngHeader header,
        byte[] compressedImageData)
    {
        var channels = header.ColorType switch
        {
            0 => 1,
            2 => 3,
            3 => 1,
            4 => 2,
            6 => 4,
            _ => 0,
        };
        if (channels == 0)
            return false;

        try
        {
            var bitsPerRow =
                checked((long)header.Width * channels * header.BitDepth);
            var bytesPerRow = (bitsPerRow + 7) / 8;
            var decodedBytes =
                checked((bytesPerRow + 1) * header.Height);
            if (decodedBytes <= 0 ||
                decodedBytes > MaxPngDecodedBytes ||
                bytesPerRow > int.MaxValue - 1)
            {
                return false;
            }

            using var compressed = new MemoryStream(compressedImageData);
            using var decoder = new ZLibStream(
                compressed,
                CompressionMode.Decompress);
            var scanline = new byte[(int)bytesPerRow + 1];
            for (var row = 0; row < header.Height; row++)
            {
                decoder.ReadExactly(scanline);
                if (scanline[0] > 4)
                    return false;
            }

            return decoder.ReadByte() == -1;
        }
        catch (EndOfStreamException)
        {
            return false;
        }
        catch (InvalidDataException)
        {
            return false;
        }
        catch (OverflowException)
        {
            return false;
        }
    }

    private static bool IsCriticalPngChunk(ReadOnlySpan<byte> chunkType)
        => chunkType.Length == 4 &&
           chunkType[0] is >= (byte)'A' and <= (byte)'Z';

    private static uint ComputePngCrc(
        ReadOnlySpan<byte> chunkType,
        ReadOnlySpan<byte> chunkData)
    {
        var crc = uint.MaxValue;
        foreach (var value in chunkType)
            crc = UpdatePngCrc(crc, value);
        foreach (var value in chunkData)
            crc = UpdatePngCrc(crc, value);
        return ~crc;
    }

    private static uint UpdatePngCrc(uint crc, byte value)
    {
        crc ^= value;
        for (var bit = 0; bit < 8; bit++)
        {
            crc = (crc & 1) != 0
                ? 0xedb88320U ^ (crc >> 1)
                : crc >> 1;
        }

        return crc;
    }

    private readonly record struct PngHeader(
        int Width,
        int Height,
        byte BitDepth,
        byte ColorType);
}
