using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using FSTService.Persistence;
using FSTService.Scraping;

namespace FSTService.Tests.Unit;

public sealed class PathArtifactValidationTests
{
    private const string RichPathJson =
        """
        {
          "schemaVersion": 2,
          "songName": "Song",
          "artist": "Artist",
          "charter": "Charter",
          "difficulty": "expert",
          "totalScore": 123456,
          "pathSummary": "summary",
          "activations": [
            {
              "startBeat": 1,
              "endBeat": 2,
              "startSeconds": 0.5,
              "endSeconds": 1.5,
              "instruction": "2: NN (G)",
              "activationBeat": 1,
              "activationSeconds": 0.5,
              "anchorBeat": 1,
              "anchorSeconds": 0.5,
              "odAtActivation": 0.25,
              "scoreBeforeActivation": 100,
              "startNotes": [
                {
                  "beat": 1,
                  "cumulativeScore": 100,
                  "noteValue": 50,
                  "odPercent": 0.25,
                  "isSpGranting": true,
                  "seconds": 0.5
                }
              ]
            }
          ],
          "notes": [
            {
              "beat": 1,
              "isSpNote": true,
              "frets": { "green": 1 },
              "seconds": 0.5
            }
          ],
          "spPhrases": [],
          "drumFills": [{ "startBeat": 0, "endBeat": 1 }],
          "measures": [],
          "bpms": [],
          "timeSignatures": []
        }
        """;

    [Fact]
    public void JsonValidation_AcceptsCompleteNestedShapes()
    {
        Assert.True(PathArtifactValidator.TryParseJson(
            RichPathJson,
            requirePositiveScore: true,
            out var score));
        Assert.Equal(123456, score);
    }

    [Fact]
    public void JsonValidation_RequiresCompleteV2ActivationMetadataWhenRequested()
    {
        Assert.True(PathArtifactValidator.TryParseJson(
            RichPathJson,
            requirePositiveScore: true,
            out var score,
            requiredSchemaVersion: 2));
        Assert.Equal(123456, score);

        foreach (var property in new[]
        {
            "\"instruction\": \"2: NN (G)\",",
            "\"activationBeat\": 1,",
            "\"activationSeconds\": 0.5,",
            "\"odAtActivation\": 0.25,",
            "\"scoreBeforeActivation\": 100,",
        })
        {
            Assert.False(PathArtifactValidator.TryParseJson(
                RichPathJson.Replace(property, "", StringComparison.Ordinal),
                requirePositiveScore: true,
                out _,
                requiredSchemaVersion: 2));
        }

        Assert.False(PathArtifactValidator.TryParseJson(
            RichPathJson.Replace(
                "\"schemaVersion\": 2,",
                "\"schemaVersion\": 1,",
                StringComparison.Ordinal),
            requirePositiveScore: true,
            out _,
            requiredSchemaVersion: 2));

        Assert.False(PathArtifactValidator.TryParseJson(
            RichPathJson.Replace(
                "\"instruction\": \"2: NN (G)\"",
                "\"instruction\": \" \"",
                StringComparison.Ordinal),
            requirePositiveScore: true,
            out _,
            requiredSchemaVersion: 2));
        Assert.False(PathArtifactValidator.TryParseJson(
            RichPathJson.Replace(
                "\"anchorBeat\": 1",
                "\"anchorBeat\": \"one\"",
                StringComparison.Ordinal),
            requirePositiveScore: true,
            out _,
            requiredSchemaVersion: 2));
        Assert.False(PathArtifactValidator.TryParseJson(
            RichPathJson.Replace(
                "\"anchorSeconds\": 0.5,",
                "",
                StringComparison.Ordinal),
            requirePositiveScore: true,
            out _,
            requiredSchemaVersion: 2));
        Assert.False(PathArtifactValidator.TryParseJson(
            RichPathJson.Replace(
                "\"odAtActivation\": 0.25,",
                "\"odAtActivation\": 1.25,",
                StringComparison.Ordinal),
            requirePositiveScore: true,
            out _,
            requiredSchemaVersion: 2));
        Assert.False(PathArtifactValidator.TryParseJson(
            RichPathJson.Replace(
                "\"anchorSeconds\": 0.5,",
                "\"anchorSeconds\": 0.5,\n              \"beatsAfterAnchor\": \"later\",",
                StringComparison.Ordinal),
            requirePositiveScore: true,
            out _,
            requiredSchemaVersion: 2));
    }

    [Fact]
    public void JsonValidation_RequiresAuthoredDrumFillsWhenRequested()
    {
        Assert.True(PathArtifactValidator.TryParseJson(
            RichPathJson,
            requirePositiveScore: true,
            out _,
            requiredSchemaVersion: 2,
            requireNonEmptyDrumFills: true));
        Assert.False(PathArtifactValidator.TryParseJson(
            RichPathJson.Replace(
                """[{ "startBeat": 0, "endBeat": 1 }]""",
                "[]",
                StringComparison.Ordinal),
            requirePositiveScore: true,
            out _,
            requiredSchemaVersion: 2,
            requireNonEmptyDrumFills: true));
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("chopt-fnf-ew0-s20-json-png-v1", null)]
    [InlineData("chopt-fnf-ew0-s20-json-png-v2", 2)]
    [InlineData("custom-profile-v2", 2)]
    [InlineData("chopt-fnf-ew0-s20-json-png-prodrums-v3", 2)]
    [InlineData("chopt-fnf-ew0-s20-json-png-prodrums-v4", 2)]
    public void JsonValidation_MapsProfilesToRequiredSchema(
        string? profile,
        int? expected)
    {
        Assert.Equal(
            expected,
            PathArtifactValidator.RequiredSchemaVersion(profile));
    }

    [Fact]
    public void JsonValidation_RejectsMalformedNestedShapesAndNonObjectRoot()
    {
        var invalidFrets = RichPathJson.Replace(
            "\"green\": 1",
            "\"green\": \"invalid\"",
            StringComparison.Ordinal);
        var invalidStartNote = RichPathJson.Replace(
            "\"isSpGranting\": true",
            "\"isSpGranting\": \"yes\"",
            StringComparison.Ordinal);
        var invalidActivation = RichPathJson.Replace(
            "\"startSeconds\": 0.5",
            "\"startSeconds\": \"soon\"",
            StringComparison.Ordinal);
        var invalidNotes = RichPathJson.Replace(
            "\"notes\": [",
            "\"notes\": { \"not\": [",
            StringComparison.Ordinal);

        Assert.False(PathArtifactValidator.TryParseJson(
            "[]",
            requirePositiveScore: false,
            out _));
        Assert.False(PathArtifactValidator.TryParseJson(
            invalidFrets,
            requirePositiveScore: false,
            out _));
        Assert.False(PathArtifactValidator.TryParseJson(
            invalidStartNote,
            requirePositiveScore: false,
            out _));
        Assert.False(PathArtifactValidator.TryParseJson(
            invalidActivation,
            requirePositiveScore: false,
            out _));
        Assert.False(PathArtifactValidator.TryParseJson(
            invalidNotes,
            requirePositiveScore: false,
            out _));
    }

    [Fact]
    public void JsonValidation_ReadsRegularFilesAndRejectsUnavailablePaths()
    {
        var directory = CreateTestDirectory("path-json");
        try
        {
            var valid = Path.Combine(directory, "valid.json");
            File.WriteAllText(valid, RichPathJson);

            Assert.True(PathArtifactValidator.TryReadJson(
                valid,
                requirePositiveScore: true,
                out var score));
            Assert.Equal(123456, score);
            Assert.False(PathArtifactValidator.TryReadJson(
                Path.Combine(directory, "missing.json"),
                requirePositiveScore: false,
                out _));
            Assert.False(PathArtifactValidator.TryReadJson(
                directory,
                requirePositiveScore: false,
                out _));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(2, false)]
    [InlineData(3, true)]
    [InlineData(4, false)]
    [InlineData(6, false)]
    public void PngValidation_AcceptsSupportedColorTypes(
        byte colorType,
        bool includePalette)
    {
        var directory = CreateTestDirectory("path-png-colors");
        try
        {
            var path = Path.Combine(directory, $"{colorType}.png");
            File.WriteAllBytes(
                path,
                BuildValidPng(
                    bitDepth: 8,
                    colorType: colorType,
                    includePalette: includePalette));

            Assert.True(
                PathArtifactValidator.IsValidPng(path),
                $"Color type {colorType} should be accepted.");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void PngValidation_RejectsMalformedChunkStructure()
    {
        var directory = CreateTestDirectory("path-png-structure");
        try
        {
            var validHeader = PngHeader(1, 1, 8, 6);
            var validData = Compress([0, 0, 0, 0, 0]);
            var badSignature = BuildValidPng();
            badSignature[0] = 0;
            var badCrc = BuildValidPng();
            badCrc[29] ^= 0xff;
            var oversizedLength = new byte[45];
            PngSignature.CopyTo(oversizedLength, 0);
            BinaryPrimitives.WriteUInt32BigEndian(
                oversizedLength.AsSpan(8, 4),
                64U * 1024 * 1024 + 1);

            var cases = new Dictionary<string, byte[]>
            {
                ["bad-signature"] = badSignature,
                ["oversized-length"] = oversizedLength,
                ["bad-crc"] = badCrc,
                ["first-chunk-not-header"] = BuildPng(
                    Chunk("IDAT", validData),
                    Chunk("IEND", [])),
                ["duplicate-header"] = BuildPng(
                    Chunk("IHDR", validHeader),
                    Chunk("IHDR", validHeader),
                    Chunk("IDAT", validData),
                    Chunk("IEND", [])),
                ["empty-image-data"] = BuildPng(
                    Chunk("IHDR", validHeader),
                    Chunk("IDAT", []),
                    Chunk("IEND", [])),
                ["critical-unknown-chunk"] = BuildPng(
                    Chunk("IHDR", validHeader),
                    Chunk("ABCD", []),
                    Chunk("IDAT", validData),
                    Chunk("IEND", [])),
                ["split-image-data"] = BuildPng(
                    Chunk("IHDR", validHeader),
                    Chunk("IDAT", validData),
                    Chunk("tEXt", Encoding.ASCII.GetBytes("metadata")),
                    Chunk("IDAT", validData),
                    Chunk("IEND", [])),
                ["palette-after-image-data"] = BuildPng(
                    Chunk("IHDR", validHeader),
                    Chunk("IDAT", validData),
                    Chunk("PLTE", [0, 0, 0]),
                    Chunk("IEND", [])),
                ["short-palette"] = BuildPng(
                    Chunk("IHDR", PngHeader(1, 1, 8, 3)),
                    Chunk("PLTE", [0, 0]),
                    Chunk("IDAT", Compress([0, 0])),
                    Chunk("IEND", [])),
                ["misaligned-palette"] = BuildPng(
                    Chunk("IHDR", PngHeader(1, 1, 8, 3)),
                    Chunk("PLTE", [0, 0, 0, 0]),
                    Chunk("IDAT", Compress([0, 0])),
                    Chunk("IEND", [])),
                ["palette-on-grayscale"] = BuildPng(
                    Chunk("IHDR", PngHeader(1, 1, 8, 0)),
                    Chunk("PLTE", [0, 0, 0]),
                    Chunk("IDAT", Compress([0, 0])),
                    Chunk("IEND", [])),
                ["indexed-without-palette"] = BuildPng(
                    Chunk("IHDR", PngHeader(1, 1, 8, 3)),
                    Chunk("IDAT", Compress([0, 0])),
                    Chunk("IEND", [])),
                ["end-before-image-data"] = BuildPng(
                    Chunk("IHDR", validHeader),
                    Chunk("IEND", [])),
                ["nonempty-end"] = BuildPng(
                    Chunk("IHDR", validHeader),
                    Chunk("IDAT", validData),
                    Chunk("IEND", [0])),
                ["trailing-chunk"] = BuildPng(
                    Chunk("IHDR", validHeader),
                    Chunk("IDAT", validData),
                    Chunk("IEND", []),
                    Chunk("tEXt", Encoding.ASCII.GetBytes("trailing"))),
                ["missing-end"] = BuildPng(
                    Chunk("IHDR", validHeader),
                    Chunk("IDAT", validData)),
            };

            foreach (var (name, bytes) in cases)
            {
                var path = Path.Combine(directory, $"{name}.png");
                File.WriteAllBytes(path, bytes);
                Assert.False(
                    PathArtifactValidator.IsValidPng(path),
                    name);
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void PngValidation_RejectsInvalidHeadersAndDecodedData()
    {
        var directory = CreateTestDirectory("path-png-decoding");
        try
        {
            var cases = new Dictionary<string, byte[]>
            {
                ["zero-width"] = BuildPng(
                    Chunk("IHDR", PngHeader(0, 1, 8, 6)),
                    Chunk("IDAT", Compress([0])),
                    Chunk("IEND", [])),
                ["too-wide"] = BuildPng(
                    Chunk("IHDR", PngHeader(32_769, 1, 8, 6)),
                    Chunk("IDAT", Compress([0])),
                    Chunk("IEND", [])),
                ["invalid-compression-method"] = BuildPng(
                    Chunk(
                        "IHDR",
                        PngHeader(
                            1,
                            1,
                            8,
                            6,
                            compressionMethod: 1)),
                    Chunk("IDAT", Compress([0])),
                    Chunk("IEND", [])),
                ["invalid-color-type"] = BuildPng(
                    Chunk("IHDR", PngHeader(1, 1, 8, 1)),
                    Chunk("IDAT", Compress([0])),
                    Chunk("IEND", [])),
                ["invalid-bit-depth"] = BuildPng(
                    Chunk("IHDR", PngHeader(1, 1, 4, 6)),
                    Chunk("IDAT", Compress([0])),
                    Chunk("IEND", [])),
                ["invalid-zlib"] = BuildPng(
                    Chunk("IHDR", PngHeader(1, 1, 8, 6)),
                    Chunk("IDAT", [1, 2, 3]),
                    Chunk("IEND", [])),
                ["invalid-filter"] = BuildPng(
                    Chunk("IHDR", PngHeader(1, 1, 8, 6)),
                    Chunk("IDAT", Compress([5, 0, 0, 0, 0])),
                    Chunk("IEND", [])),
                ["truncated-decoded-data"] = BuildPng(
                    Chunk("IHDR", PngHeader(1, 2, 8, 0)),
                    Chunk("IDAT", Compress([0, 0])),
                    Chunk("IEND", [])),
                ["decoded-image-too-large"] = BuildPng(
                    Chunk("IHDR", PngHeader(32_768, 32_768, 16, 6)),
                    Chunk("IDAT", Compress([0])),
                    Chunk("IEND", [])),
            };

            foreach (var (name, bytes) in cases)
            {
                var path = Path.Combine(directory, $"{name}.png");
                File.WriteAllBytes(path, bytes);
                Assert.False(
                    PathArtifactValidator.IsValidPng(path),
                    name);
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void PngValidation_AcceptsTallFestivalPathWithinDecodedBudget()
    {
        var directory = CreateTestDirectory("path-png-tall");
        try
        {
            var path = Path.Combine(directory, "tall.png");
            File.WriteAllBytes(
                path,
                BuildValidPng(
                    width: 1,
                    height: 17_965));

            Assert.True(PathArtifactValidator.IsValidPng(path));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Resolver_RejectsUnsafeSegmentsAndBuildsCanonicalPaths()
    {
        var root = CreateTestDirectory("path-resolver");
        try
        {
            Assert.Null(PathArtifactResolver.Resolve(
                root,
                "../song",
                "Solo_Guitar",
                "expert",
                "png",
                null));
            Assert.Null(PathArtifactResolver.Resolve(
                root,
                "song",
                "Unknown",
                "expert",
                "png",
                null));
            Assert.Null(PathArtifactResolver.Resolve(
                root,
                "song",
                "Solo_Guitar",
                "impossible",
                "png",
                null));
            Assert.Null(PathArtifactResolver.Resolve(
                root,
                "song",
                "Solo_Guitar",
                "expert",
                "svg",
                null));
            Assert.Throws<InvalidOperationException>(() =>
                PathArtifactResolver.GetGenerationDirectory(
                    root,
                    "song",
                    "../generation"));

            var legacy = Assert.IsType<ResolvedPathArtifact>(
                PathArtifactResolver.Resolve(
                    root,
                    "song",
                    "Solo_Guitar",
                    "EXPERT",
                    "png",
                    null));
            Assert.True(legacy.IsLegacy);
            Assert.Null(legacy.GenerationId);
            Assert.EndsWith(
                Path.Combine("Solo_Guitar", "expert.png"),
                legacy.FilePath,
                StringComparison.Ordinal);

            var generated = Assert.IsType<ResolvedPathArtifact>(
                PathArtifactResolver.Resolve(
                    root,
                    "song",
                    "Solo_Guitar",
                    "expert",
                    "json",
                    "generation-1"));
            Assert.False(generated.IsLegacy);
            Assert.Equal("generation-1", generated.GenerationId);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ImmutableGeneration_ValidatesIdentityAndCompleteness()
    {
        using var fixture = CreateValidGeneration();

        var validated = PathArtifactResolver.ValidateImmutableGeneration(
            fixture.Root,
            fixture.State.SongId,
            fixture.State.ArtifactGenerationId!);

        Assert.Equal(fixture.GenerationDirectory, validated.GenerationDirectory);
        Assert.Equal(123456, validated.MaxScores.MaxLeadScore);
        Assert.True(PathArtifactResolver.IsGenerationComplete(
            fixture.Root,
            fixture.State));
        Assert.False(PathArtifactResolver.IsGenerationComplete(
            fixture.Root,
            fixture.State with { ArtifactGenerationId = null }));
        Assert.False(PathArtifactResolver.IsGenerationComplete(
            fixture.Root,
            fixture.State with { DatFileHash = new string('b', 64) }));
        Assert.False(PathArtifactResolver.IsGenerationComplete(
            fixture.Root,
            fixture.State with
            {
                ExpectedInstruments = ["Solo_Bass"],
            }));
        Assert.False(PathArtifactResolver.IsGenerationComplete(
            fixture.Root,
            fixture.State with
            {
                MaxScores = new SongMaxScores
                {
                    MaxLeadScore = 123455,
                },
            }));
    }

    [Fact]
    public void ImmutableGeneration_RejectsInvalidManifestAndArtifacts()
    {
        AssertInvalidGeneration(
            fixture => Directory.Delete(
                fixture.GenerationDirectory,
                recursive: true),
            "regular directory");
        AssertInvalidGeneration(
            fixture => File.WriteAllText(fixture.ManifestPath, string.Empty),
            "manifest size");
        AssertInvalidGeneration(
            fixture => File.WriteAllText(fixture.ManifestPath, "{"),
            "strict JSON");
        AssertInvalidGeneration(
            fixture => fixture.WriteManifest(
                fixture.Manifest with { DatFileHash = "invalid" }),
            "manifest identity");
        AssertInvalidGeneration(
            fixture => fixture.WriteManifest(
                fixture.Manifest with
                {
                    ExpectedInstruments =
                    [
                        "Solo_Guitar",
                        "Solo_Guitar",
                    ],
                }),
            "expected instrument set");
        AssertInvalidGeneration(
            fixture => fixture.WriteManifest(
                fixture.Manifest with
                {
                    ExpertMaxScores = new Dictionary<string, int>
                    {
                        ["Solo_Guitar"] = 0,
                    },
                }),
            "no positive expert maximum");
        AssertInvalidGeneration(
            fixture => Directory.Delete(
                fixture.InstrumentDirectory,
                recursive: true),
            "missing instrument directory");
        AssertInvalidGeneration(
            fixture => File.WriteAllBytes(
                Path.Combine(fixture.InstrumentDirectory, "easy.png"),
                []),
            "artifact size");
        AssertInvalidGeneration(
            fixture => File.WriteAllBytes(
                Path.Combine(fixture.InstrumentDirectory, "easy.png"),
                [1, 2, 3]),
            "failed artifact validation");
    }

    [Fact]
    public void ImmutableGeneration_RejectsSymbolicLinkComponents()
    {
        using var fixture = CreateValidGeneration();
        var target = Path.Combine(fixture.Root, "linked-instrument");
        Directory.Move(fixture.InstrumentDirectory, target);
        Directory.CreateSymbolicLink(
            fixture.InstrumentDirectory,
            target);

        var exception = Assert.Throws<InvalidOperationException>(() =>
            PathArtifactResolver.ValidateImmutableGeneration(
                fixture.Root,
                fixture.State.SongId,
                fixture.State.ArtifactGenerationId!));

        Assert.Contains(
            "symbolic link",
            exception.Message,
            StringComparison.Ordinal);
    }

    private static void AssertInvalidGeneration(
        Action<GenerationFixture> corrupt,
        string expectedMessage)
    {
        using var fixture = CreateValidGeneration();
        corrupt(fixture);

        var exception = Assert.Throws<InvalidOperationException>(() =>
            PathArtifactResolver.ValidateImmutableGeneration(
                fixture.Root,
                fixture.State.SongId,
                fixture.State.ArtifactGenerationId!));

        Assert.Contains(
            expectedMessage,
            exception.Message,
            StringComparison.OrdinalIgnoreCase);
    }

    private static GenerationFixture CreateValidGeneration()
    {
        var root = CreateTestDirectory("path-generation");
        const string songId = "song-1";
        const string generationId = "generation-1";
        const string instrument = "Solo_Guitar";
        var generationDirectory =
            PathArtifactResolver.GetGenerationDirectory(
                root,
                songId,
                generationId);
        var instrumentDirectory = Path.Combine(
            generationDirectory,
            instrument);
        Directory.CreateDirectory(instrumentDirectory);
        var manifest = new PathArtifactManifest(
            GenerationId: generationId,
            SongId: songId,
            DatFileHash: new string('a', 64),
            SongLastModified: "2026-08-09T00:00:00Z",
            ChoptVersion: "1.2.3",
            ChoptBinarySha256: new string('b', 64),
            GenerationProfile: "profile-v1",
            ExpectedInstruments: [instrument],
            ExpertMaxScores: new Dictionary<string, int>
            {
                [instrument] = 123456,
            },
            GeneratedAtUtc: new DateTime(
                2026,
                8,
                9,
                12,
                0,
                0,
                DateTimeKind.Utc));
        var manifestPath = Path.Combine(
            generationDirectory,
            PathArtifactResolver.ManifestFileName);
        File.WriteAllText(
            manifestPath,
            JsonSerializer.Serialize(
                manifest,
                PathArtifactManifest.JsonOptions));
        foreach (var difficulty in PathGenerationInstruments.Difficulties)
        {
            File.WriteAllBytes(
                Path.Combine(instrumentDirectory, $"{difficulty}.png"),
                BuildValidPng());
            File.WriteAllText(
                Path.Combine(instrumentDirectory, $"{difficulty}.json"),
                BuildPathJson(
                    difficulty,
                    difficulty == "expert" ? 123456 : 0));
        }

        var scores = new SongMaxScores
        {
            MaxLeadScore = 123456,
            GeneratedAt = manifest.GeneratedAtUtc.ToString("o"),
            CHOptVersion = manifest.ChoptVersion,
            CHOptBinarySha256 = manifest.ChoptBinarySha256,
            GenerationProfile = manifest.GenerationProfile,
            ArtifactGenerationId = generationId,
            ExpectedInstruments = [instrument],
        };
        var state = new PathGenerationState(
            SongId: songId,
            Revision: 1,
            DatFileHash: manifest.DatFileHash,
            SongLastModified: manifest.SongLastModified,
            GeneratedAtUtc: manifest.GeneratedAtUtc,
            ChoptVersion: manifest.ChoptVersion,
            ChoptBinarySha256: manifest.ChoptBinarySha256,
            GenerationProfile: manifest.GenerationProfile,
            ArtifactGenerationId: generationId,
            ExpectedInstruments: [instrument],
            MaxScores: scores);
        return new GenerationFixture(
            root,
            generationDirectory,
            instrumentDirectory,
            manifestPath,
            manifest,
            state);
    }

    private static string BuildPathJson(
        string difficulty,
        int totalScore) => JsonSerializer.Serialize(new
    {
        songName = "Song",
        artist = "Artist",
        charter = "Charter",
        difficulty,
        totalScore,
        pathSummary = string.Empty,
        activations = Array.Empty<object>(),
        notes = Array.Empty<object>(),
        spPhrases = Array.Empty<object>(),
        measures = Array.Empty<object>(),
        bpms = Array.Empty<object>(),
        timeSignatures = Array.Empty<object>(),
    });

    private static readonly byte[] PngSignature =
        [0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a];

    private static byte[] BuildValidPng(
        byte bitDepth = 8,
        byte colorType = 6,
        int width = 1,
        int height = 1,
        byte filter = 0,
        bool includePalette = false)
    {
        var channels = colorType switch
        {
            0 => 1,
            2 => 3,
            3 => 1,
            4 => 2,
            6 => 4,
            _ => throw new ArgumentOutOfRangeException(nameof(colorType)),
        };
        var bytesPerRow = (width * channels * bitDepth + 7) / 8;
        var decoded = new byte[(bytesPerRow + 1) * height];
        for (var row = 0; row < height; row++)
            decoded[row * (bytesPerRow + 1)] = filter;
        var chunks = new List<byte[]>
        {
            Chunk(
                "IHDR",
                PngHeader(width, height, bitDepth, colorType)),
        };
        if (includePalette)
            chunks.Add(Chunk("PLTE", [0, 0, 0]));
        chunks.Add(Chunk("IDAT", Compress(decoded)));
        chunks.Add(Chunk("IEND", []));
        return BuildPng(chunks.ToArray());
    }

    private static byte[] PngHeader(
        int width,
        int height,
        byte bitDepth,
        byte colorType,
        byte compressionMethod = 0,
        byte filterMethod = 0,
        byte interlaceMethod = 0)
    {
        var result = new byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(
            result.AsSpan(0, 4),
            unchecked((uint)width));
        BinaryPrimitives.WriteUInt32BigEndian(
            result.AsSpan(4, 4),
            unchecked((uint)height));
        result[8] = bitDepth;
        result[9] = colorType;
        result[10] = compressionMethod;
        result[11] = filterMethod;
        result[12] = interlaceMethod;
        return result;
    }

    private static byte[] Compress(byte[] decoded)
    {
        using var output = new MemoryStream();
        using (var compressor = new ZLibStream(
                   output,
                   CompressionLevel.SmallestSize,
                   leaveOpen: true))
        {
            compressor.Write(decoded);
        }
        return output.ToArray();
    }

    private static byte[] BuildPng(params byte[][] chunks)
    {
        using var output = new MemoryStream();
        output.Write(PngSignature);
        foreach (var chunk in chunks)
            output.Write(chunk);
        return output.ToArray();
    }

    private static byte[] Chunk(string type, byte[] data)
    {
        var typeBytes = Encoding.ASCII.GetBytes(type);
        Assert.Equal(4, typeBytes.Length);
        var result = new byte[12 + data.Length];
        BinaryPrimitives.WriteUInt32BigEndian(
            result.AsSpan(0, 4),
            checked((uint)data.Length));
        typeBytes.CopyTo(result, 4);
        data.CopyTo(result, 8);
        BinaryPrimitives.WriteUInt32BigEndian(
            result.AsSpan(8 + data.Length, 4),
            ComputeCrc(typeBytes, data));
        return result;
    }

    private static uint ComputeCrc(
        ReadOnlySpan<byte> type,
        ReadOnlySpan<byte> data)
    {
        var crc = uint.MaxValue;
        foreach (var value in type)
            crc = UpdateCrc(crc, value);
        foreach (var value in data)
            crc = UpdateCrc(crc, value);
        return ~crc;
    }

    private static uint UpdateCrc(uint crc, byte value)
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

    private static string CreateTestDirectory(string prefix)
    {
        var directory = Path.Combine(
            Directory.GetCurrentDirectory(),
            ".test-temp",
            $"{prefix}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        return directory;
    }

    private sealed record GenerationFixture(
        string Root,
        string GenerationDirectory,
        string InstrumentDirectory,
        string ManifestPath,
        PathArtifactManifest Manifest,
        PathGenerationState State) : IDisposable
    {
        public void WriteManifest(PathArtifactManifest manifest)
        {
            File.WriteAllText(
                ManifestPath,
                JsonSerializer.Serialize(
                    manifest,
                    PathArtifactManifest.JsonOptions));
        }

        public void Dispose()
        {
            if (Directory.Exists(Root))
                Directory.Delete(Root, recursive: true);
        }
    }
}
