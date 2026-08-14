using System.Text.Json;
using FortniteFestival.Core;
using FSTService.Persistence;

namespace FSTService.Scraping;

public sealed record PathInstrumentDefinition(
    string ProviderProperty,
    string Instrument,
    string MidiVariant,
    string ChoptInstrument,
    bool DisableProDrums = false);

public static class PathGenerationInstruments
{
    public static readonly IReadOnlyList<PathInstrumentDefinition> Definitions =
    [
        new("gr", "Solo_Guitar", "og", "guitar"),
        new("ba", "Solo_Bass", "og", "bass"),
        new("ds", "Solo_Drums", "og", "drums"),
        new("vl", "Solo_Vocals", "og", "vocals"),
        new("pg", "Solo_PeripheralGuitar", "pro", "guitar"),
        new("pb", "Solo_PeripheralBass", "pro", "bass"),
        // CHOpt's FNF prodrums mode reads PLASTIC DRUMS from the original MIDI.
        new("pd", "Solo_PeripheralCymbals", "og", "prodrums"),
        new(
            "pd",
            "Solo_PeripheralDrums",
            "og",
            "prodrums",
            DisableProDrums: true),
    ];

    public static readonly IReadOnlyList<string> Difficulties =
        ["easy", "medium", "hard", "expert"];

    private static readonly Dictionary<string, PathInstrumentDefinition> ByInstrument =
        Definitions.ToDictionary(d => d.Instrument, StringComparer.Ordinal);

    public static PathInstrumentDefinition GetDefinition(string instrument)
        => ByInstrument.TryGetValue(instrument, out var definition)
            ? definition
            : throw new ArgumentOutOfRangeException(nameof(instrument), instrument, "Unsupported path instrument.");

    public static string[] NormalizeExpected(IEnumerable<string> instruments)
    {
        var requested = instruments.ToHashSet(StringComparer.Ordinal);
        return Definitions
            .Where(definition => requested.Contains(definition.Instrument))
            .Select(definition => definition.Instrument)
            .ToArray();
    }
}

public sealed record SongPathRequest(
    string SongId,
    string Title,
    string Artist,
    string DatUrl,
    string? LastModified,
    IReadOnlyList<string> ExpectedInstruments)
{
    private static readonly HashSet<string> MissingGuitarIntensitySongIds =
        new(StringComparer.Ordinal)
        {
            "3d7901c9-7ae2-4adb-9393-4ec4c54c2e3b",
            "ddd5447c-b5d7-4fe4-8f22-c9854168d11b",
        };

    private static readonly string[] MissingGuitarIntensityInstruments =
        ["Solo_Guitar", "Solo_PeripheralGuitar"];

    public static SongPathRequest? FromSong(Song song)
    {
        if (string.IsNullOrWhiteSpace(song.track?.su) ||
            string.IsNullOrWhiteSpace(song.track.mu))
        {
            return null;
        }

        var expected = GetExpectedInstruments(song);
        return new SongPathRequest(
            song.track.su,
            song.track.tt ?? song.track.su,
            song.track.an ?? "Unknown",
            song.track.mu,
            song.lastModified == DateTime.MinValue
                ? null
                : song.lastModified.ToString("o"),
            expected);
    }

    internal static string[] GetExpectedInstruments(Song song)
    {
        IEnumerable<string> expected;
        if (TryGetRawIntensity(song, out var intensity))
        {
            expected = PathGenerationInstruments.Definitions
                .Where(definition => intensity.TryGetProperty(definition.ProviderProperty, out _))
                .Select(definition => definition.Instrument);
        }
        else
        {
            var typedIntensity = song.track?.@in;
            expected = typedIntensity is null
                ? []
                : PathGenerationInstruments.Definitions
                    .Where(definition =>
                        typedIntensity.HasProviderProperty(
                            definition.ProviderProperty))
                    .Select(definition => definition.Instrument);
        }

        // Epic omits gr/pg for these charts even though both guitar tracks
        // exist in the MIDI and their live leaderboards are populated.
        if (song.track?.su is { } songId &&
            MissingGuitarIntensitySongIds.Contains(songId))
        {
            expected = expected.Concat(MissingGuitarIntensityInstruments);
        }

        return PathGenerationInstruments.NormalizeExpected(expected);
    }

    private static bool TryGetRawIntensity(Song song, out JsonElement intensity)
    {
        intensity = default;
        if (song.providerJson is not JsonElement provider ||
            provider.ValueKind != JsonValueKind.Object ||
            !provider.TryGetProperty("track", out var track) ||
            track.ValueKind != JsonValueKind.Object ||
            !track.TryGetProperty("in", out intensity) ||
            intensity.ValueKind != JsonValueKind.Object)
        {
            intensity = default;
            return false;
        }

        return true;
    }
}

public sealed record PathGenerationRuntimeIdentity(
    string Version,
    string BinarySha256,
    string Profile);

public sealed record PathGenerationState(
    string SongId,
    long Revision,
    string? DatFileHash,
    string? SongLastModified,
    DateTime? GeneratedAtUtc,
    string? ChoptVersion,
    string? ChoptBinarySha256,
    string? GenerationProfile,
    string? ArtifactGenerationId,
    IReadOnlyList<string> ExpectedInstruments,
    SongMaxScores MaxScores,
    string? CatalogLastModified = null,
    bool PathGenerationPending = false);

public sealed record PathGenerationPromotion(
    string AttemptId,
    string SongId,
    long ExpectedRevision,
    string ArtifactGenerationId,
    string DatFileHash,
    string? SongLastModified,
    DateTime GeneratedAtUtc,
    PathGenerationRuntimeIdentity Runtime,
    IReadOnlyList<string> ExpectedInstruments,
    SongMaxScores MaxScores);

public enum PathGenerationPromotionOutcome
{
    Promoted,
    Conflict,
    SongMissing,
}

public sealed record PathGenerationError(
    string AttemptId,
    string SongId,
    string? DatFileHash,
    string? ChoptVersion,
    string? ChoptBinarySha256,
    string? GenerationProfile,
    IReadOnlyList<string> ExpectedInstruments,
    string FailureStage,
    string? Instrument,
    string? Difficulty,
    string Detail,
    DateTime CreatedAtUtc);

public sealed record PathGenerationBatchResult(
    int Requested,
    int Promoted,
    int Skipped,
    int Failed,
    int Conflicted)
{
    public bool Changed => Promoted > 0;
}

internal enum PathGenerationAttemptOutcome
{
    Promoted,
    Skipped,
    Failed,
    Conflicted,
}
internal sealed record PathGenerationAttemptResult(
    PathGenerationAttemptOutcome Outcome,
    string? FailureStage = null,
    string? Detail = null);
