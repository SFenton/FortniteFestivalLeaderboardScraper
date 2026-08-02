using System.Buffers.Binary;
using System.IO.Compression;
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
    private static readonly byte[] IhdrChunk = "IHDR"u8.ToArray();
    private static readonly byte[] IdatChunk = "IDAT"u8.ToArray();
    private static readonly byte[] IendChunk = "IEND"u8.ToArray();
    private static readonly byte[] PlteChunk = "PLTE"u8.ToArray();
    private const int MaxPngChunkLength = 64 * 1024 * 1024;
    private const int MaxPngDimension = 16_384;
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

            var root = document.RootElement;
            if (!HasString(root, "songName") ||
                !HasString(root, "artist") ||
                !HasString(root, "charter") ||
                !HasString(root, "difficulty") ||
                !HasString(root, "pathSummary") ||
                !HasArray(root, "activations", ValidateActivation) ||
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

    private static bool ValidateActivation(JsonElement activation)
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

    private static bool HasBoolean(JsonElement root, string propertyName)
        => root.TryGetProperty(propertyName, out var property) &&
           property.ValueKind is JsonValueKind.True or JsonValueKind.False;

    private static bool IsOptionalNumber(
        JsonElement root,
        string propertyName)
        => !root.TryGetProperty(propertyName, out var property) ||
           property.ValueKind == JsonValueKind.Number;

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
