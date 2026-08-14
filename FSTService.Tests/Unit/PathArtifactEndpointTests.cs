using FSTService.Api;
using FSTService.Persistence;
using FSTService.Scraping;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Options;

namespace FSTService.Tests.Unit;

public sealed class PathArtifactEndpointTests : IDisposable
{
    private readonly string _dataDirectory = Path.Combine(
        Directory.GetCurrentDirectory(),
        ".test-temp",
        $"path-endpoints-{Guid.NewGuid():N}");

    public PathArtifactEndpointTests()
    {
        Directory.CreateDirectory(_dataDirectory);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_dataDirectory))
                Directory.Delete(_dataDirectory, recursive: true);
        }
        catch
        {
        }
    }

    [Fact]
    public void Image_and_json_endpoints_resolve_the_same_generation_pointer()
    {
        const string songId = "same-generation";
        const string generationId = "generation-2";
        var store = new ResolverStore(CreateState(songId, generationId));
        var resolver = new PathArtifactResolver(
            store,
            Options.Create(new ScraperOptions { DataDirectory = _dataDirectory }));
        var generationDirectory = PathArtifactResolver.GetGenerationDirectory(
            _dataDirectory,
            songId,
            generationId);
        var instrumentDirectory = Path.Combine(
            generationDirectory,
            "Solo_Guitar");
        Directory.CreateDirectory(instrumentDirectory);
        File.WriteAllBytes(
            Path.Combine(instrumentDirectory, "expert.png"),
            [1]);
        File.WriteAllText(
            Path.Combine(instrumentDirectory, "expert.json"),
            "{}");

        var image = Assert.IsType<PhysicalFileHttpResult>(
            ApiEndpoints.GetPathArtifactResult(
                songId,
                "Solo_Guitar",
                "expert",
                "png",
                generationId,
                resolver));
        var json = Assert.IsType<PhysicalFileHttpResult>(
            ApiEndpoints.GetPathArtifactResult(
                songId,
                "Solo_Guitar",
                "expert",
                "json",
                generationId,
                resolver));

        Assert.Contains(
            Path.Combine("generations", generationId),
            image.FileName,
            StringComparison.Ordinal);
        Assert.Contains(
            Path.Combine("generations", generationId),
            json.FileName,
            StringComparison.Ordinal);
        Assert.Equal(
            Path.GetDirectoryName(image.FileName),
            Path.GetDirectoryName(json.FileName));
    }

    [Fact]
    public void Null_generation_pointer_preserves_legacy_layout()
    {
        const string songId = "legacy";
        var store = new ResolverStore(CreateState(songId, generationId: null));
        var resolver = new PathArtifactResolver(
            store,
            Options.Create(new ScraperOptions { DataDirectory = _dataDirectory }));
        var legacyDirectory = Path.Combine(
            _dataDirectory,
            "paths",
            songId,
            "Solo_Guitar");
        Directory.CreateDirectory(legacyDirectory);
        File.WriteAllBytes(Path.Combine(legacyDirectory, "expert.png"), [1]);
        File.WriteAllText(Path.Combine(legacyDirectory, "expert.json"), "{}");

        var image = Assert.IsType<PhysicalFileHttpResult>(
            ApiEndpoints.GetPathArtifactResult(
                songId,
                "Solo_Guitar",
                "expert",
                "png",
                null,
                resolver));
        var json = Assert.IsType<PhysicalFileHttpResult>(
            ApiEndpoints.GetPathArtifactResult(
                songId,
                "Solo_Guitar",
                "expert",
                "json",
                null,
                resolver));

        Assert.DoesNotContain("generations", image.FileName, StringComparison.Ordinal);
        Assert.DoesNotContain("generations", json.FileName, StringComparison.Ordinal);
        Assert.Equal(legacyDirectory, Path.GetDirectoryName(image.FileName));
        Assert.Equal(legacyDirectory, Path.GetDirectoryName(json.FileName));
    }

    [Fact]
    public void Instrument_selective_generation_preserves_other_legacy_artifacts()
    {
        const string songId = "pro-lead-repair";
        const string generationId = "generation-pro-lead";
        var state = CreateState(songId, generationId) with
        {
            ExpectedInstruments = ["Solo_PeripheralGuitar"],
        };
        var resolver = new PathArtifactResolver(
            new ResolverStore(state),
            Options.Create(new ScraperOptions { DataDirectory = _dataDirectory }));
        var legacyDirectory = Path.Combine(
            _dataDirectory,
            "paths",
            songId,
            "Solo_Guitar");
        Directory.CreateDirectory(legacyDirectory);
        File.WriteAllBytes(Path.Combine(legacyDirectory, "expert.png"), [1]);

        var image = Assert.IsType<PhysicalFileHttpResult>(
            ApiEndpoints.GetPathArtifactResult(
                songId,
                "Solo_Guitar",
                "expert",
                "png",
                generationId,
                resolver));

        Assert.Equal(
            Path.Combine(legacyDirectory, "expert.png"),
            image.FileName);
    }

    [Fact]
    public void Explicit_generation_rejects_a_pointer_mismatch()
    {
        const string songId = "pinned-generation";
        const string requestedGeneration = "generation-1";
        var store = new ResolverStore(CreateState(songId, "generation-2"));
        var resolver = new PathArtifactResolver(
            store,
            Options.Create(new ScraperOptions { DataDirectory = _dataDirectory }));

        var image = ApiEndpoints.GetPathArtifactResult(
            songId,
            "Solo_Guitar",
            "expert",
            "png",
            requestedGeneration,
            resolver);
        var json = ApiEndpoints.GetPathArtifactResult(
            songId,
            "Solo_Guitar",
            "expert",
            "json",
            requestedGeneration,
            resolver);

        Assert.Equal(
            StatusCodes.Status400BadRequest,
            Assert.IsAssignableFrom<IStatusCodeHttpResult>(image).StatusCode);
        Assert.Equal(
            StatusCodes.Status400BadRequest,
            Assert.IsAssignableFrom<IStatusCodeHttpResult>(json).StatusCode);
    }

    [Theory]
    [InlineData("Solo_PeripheralCymbals")]
    [InlineData("Solo_PeripheralDrums")]
    public void Plastic_drum_instruments_are_valid_path_routes(
        string instrument)
    {
        const string songId = "plastic-drums";
        var state = CreateState(songId, generationId: null) with
        {
            ExpectedInstruments = [instrument],
        };
        var resolver = new PathArtifactResolver(
            new ResolverStore(state),
            Options.Create(
                new ScraperOptions
                {
                    DataDirectory = _dataDirectory,
                }));

        var result = ApiEndpoints.GetPathArtifactResult(
            songId,
            instrument,
            "expert",
            "json",
            null,
            resolver);

        Assert.Equal(
            StatusCodes.Status404NotFound,
            Assert.IsAssignableFrom<IStatusCodeHttpResult>(result)
                .StatusCode);
    }

    [Theory]
    [InlineData("Solo_PeripheralCymbals")]
    [InlineData("Solo_PeripheralDrums")]
    public void Plastic_drum_migration_rejects_stale_legacy_artifacts(
        string instrument)
    {
        const string songId = "plastic-drums-migration";
        const string generationId = "generation-v2";
        var resolver = new PathArtifactResolver(
            new ResolverStore(CreateState(songId, generationId)),
            Options.Create(
                new ScraperOptions
                {
                    DataDirectory = _dataDirectory,
                }));
        var legacyDirectory = Path.Combine(
            _dataDirectory,
            "paths",
            songId,
            instrument);
        Directory.CreateDirectory(legacyDirectory);
        File.WriteAllText(
            Path.Combine(legacyDirectory, "expert.json"),
            """{"schemaVersion":1,"totalScore":1}""");

        var result = ApiEndpoints.GetPathArtifactResult(
            songId,
            instrument,
            "expert",
            "json",
            generationId,
            resolver);

        Assert.Equal(
            StatusCodes.Status404NotFound,
            Assert.IsAssignableFrom<IStatusCodeHttpResult>(result)
                .StatusCode);
    }

    private static PathGenerationState CreateState(
        string songId,
        string? generationId)
        => new(
            songId,
            1,
            "hash",
            null,
            DateTime.UtcNow,
            "1.0.0",
            "binary",
            "profile",
            generationId,
            ["Solo_Guitar"],
            new SongMaxScores
            {
                MaxLeadScore = 100,
                ArtifactGenerationId = generationId,
                ExpectedInstruments = ["Solo_Guitar"],
            });

    private sealed class ResolverStore(PathGenerationState state) : IPathDataStore
    {
        public Dictionary<string, PathGenerationState> GetPathGenerationStates()
            => new(StringComparer.Ordinal) { [state.SongId] = state };

        public PathGenerationState? GetPathGenerationState(string songId)
            => songId == state.SongId ? state : null;

        public HashSet<string> GetPendingPathGenerationSongIds()
            => [];

        public Dictionary<string, SongMaxScores> GetAllMaxScores()
            => new(StringComparer.Ordinal) { [state.SongId] = state.MaxScores };

        public Task<PathGenerationPromotionOutcome> TryPromoteGenerationAsync(
            PathGenerationPromotion promotion,
            CancellationToken ct)
            => throw new NotSupportedException();

        public Task AppendPathGenerationErrorAsync(
            PathGenerationError error,
            CancellationToken ct)
            => throw new NotSupportedException();

    }
}
