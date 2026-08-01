using FortniteFestival.Core;
using FSTService.Persistence;
using FSTService.Scraping;

namespace FSTService.Tests.Unit;

public sealed class PathGenerationParsingTests
{
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
            """{"totalScore":123456}""",
            requirePositiveScore: true,
            out var score));
        Assert.Equal(123456, score);

        Assert.False(PathArtifactValidator.TryParseJson(
            """{"totalScore":0}""",
            requirePositiveScore: true,
            out _));
        Assert.False(PathArtifactValidator.TryParseJson(
            "{",
            requirePositiveScore: false,
            out _));
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
