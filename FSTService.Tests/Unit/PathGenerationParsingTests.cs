using System.Text.Json;
using FortniteFestival.Core;
using FSTService.Persistence;
using FSTService.Scraping;

namespace FSTService.Tests.Unit;

public sealed class PathGenerationParsingTests
{
    private const string ValidPathJson =
        """{"songName":"Song","artist":"Artist","charter":"Charter","difficulty":"expert","totalScore":123456,"pathSummary":"","activations":[],"notes":[],"spPhrases":[],"measures":[],"bpms":[],"timeSignatures":[]}""";
    private const string ValidPngBase64 =
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=";
    private const string UndecodablePngBase64 =
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAAAUlEQVR4duaE5gAAAABJRU5ErkJggg==";

    [Fact]
    public void Published_catalog_fallback_accepts_legacy_schema_without_weakening_exact_decode()
    {
        const string legacyCatalog =
            """{"songs":[{"_title":"Legacy","track":{"su":"legacy-song","tt":"Legacy","an":"Artist","in":{"pg":1}}}]}""";

        var songs = SongCatalogSnapshotBuilder.DeserializeCatalogForFallback(
            legacyCatalog,
            schemaVersion: 1);

        var song = Assert.Single(songs);
        Assert.Equal("legacy-song", song.track.su);
        Assert.Equal(JsonValueKind.Object, song.providerJson?.ValueKind);
        Assert.Throws<InvalidOperationException>(
            () => SongCatalogSnapshotBuilder.DeserializeCatalog(
                legacyCatalog));
    }

    [Theory]
    [InlineData("CHOpt 1.10.3", "", "1.10.3")]
    [InlineData("chopt version v2.4.0-beta.1", "", "2.4.0-beta.1")]
    [InlineData("", "CHOpt 3.0.1", "3.0.1")]
    public void ParseVersionOutput_extracts_runtime_version(
        string stdout,
        string stderr,
        string expected)
    {
        Assert.Equal(
            expected,
            PathGenerationCoordinator.ParseVersionOutput(stdout, stderr));
    }

    [Theory]
    [InlineData("")]
    [InlineData("CHOpt version unknown")]
    [InlineData("build abcdef")]
    public void ParseVersionOutput_rejects_unparseable_output(string output)
    {
        Assert.Null(PathGenerationCoordinator.ParseVersionOutput(output));
    }

    [Fact]
    public void Json_validation_requires_positive_expert_score()
    {
        Assert.True(PathArtifactValidator.TryParseJson(
            ValidPathJson,
            requirePositiveScore: true,
            out var score));
        Assert.Equal(123456, score);

        Assert.False(PathArtifactValidator.TryParseJson(
            ValidPathJson.Replace(
                "\"totalScore\":123456",
                "\"totalScore\":0",
                StringComparison.Ordinal),
            requirePositiveScore: true,
            out _));
        Assert.False(PathArtifactValidator.TryParseJson(
            ValidPathJson.Replace(
                ",\"notes\":[]",
                "",
                StringComparison.Ordinal),
            requirePositiveScore: false,
            out _));
        Assert.False(PathArtifactValidator.TryParseJson(
            "{",
            requirePositiveScore: false,
            out _));
    }

    [Fact]
    public void Png_validation_requires_complete_crc_valid_chunk_structure()
    {
        var directory = Path.Combine(
            Directory.GetCurrentDirectory(),
            ".test-temp",
            $"path-png-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var valid = Path.Combine(directory, "valid.png");
            var truncated = Path.Combine(directory, "truncated.png");
            var corrupt = Path.Combine(directory, "corrupt.png");
            var undecodable = Path.Combine(directory, "undecodable.png");
            var bytes = Convert.FromBase64String(ValidPngBase64);
            File.WriteAllBytes(valid, bytes);
            File.WriteAllBytes(truncated, bytes[..8]);
            var corruptBytes = bytes.ToArray();
            corruptBytes[^5] ^= 0xff;
            File.WriteAllBytes(corrupt, corruptBytes);
            File.WriteAllBytes(
                undecodable,
                Convert.FromBase64String(UndecodablePngBase64));

            Assert.True(PathArtifactValidator.IsValidPng(valid));
            Assert.False(PathArtifactValidator.IsValidPng(truncated));
            Assert.False(PathArtifactValidator.IsValidPng(corrupt));
            Assert.False(PathArtifactValidator.IsValidPng(undecodable));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Provider_snapshot_preserves_known_property_presence()
    {
        var song = new Song
        {
            track = new Track
            {
                su = "presence",
                mu = "https://example.invalid/presence.dat",
                @in = new In { gr = 0 },
            },
        };

        var json = SongCatalogSnapshotBuilder.CreateProviderSongJson(song);
        var restarted = SongCatalogSnapshotBuilder.DeserializeProviderSong(json);
        var request = SongPathRequest.FromSong(restarted);

        Assert.NotNull(request);
        Assert.Equal(["Solo_Guitar"], request!.ExpectedInstruments);
    }
}
