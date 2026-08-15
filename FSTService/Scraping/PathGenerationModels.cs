using System.Text.Json;
using FortniteFestival.Core;
using FSTService.Persistence;

namespace FSTService.Scraping;

internal static class PathGenerationProfiles
{
    internal const string InvalidPlasticDrumsV3 =
        "chopt-fnf-ew0-s20-json-png-prodrums-v3";
    internal const string PlasticDrumsV4 =
        "chopt-fnf-ew0-s20-json-png-prodrums-v4";
    internal const string PlasticDrumsV4ChoptVersion = "1.16.4";
    internal const string PlasticDrumsV4BinarySha256 =
        "4c3f9d55c50e8406080191a138580e377413ecc9b2edb60a877281f97018205f";

    internal static bool HasInvalidPlasticDrumsScores(string? profile)
        => string.Equals(
            profile,
            InvalidPlasticDrumsV3,
            StringComparison.Ordinal);

    internal static bool RequiresAuthoredDrumFills(string? profile)
        => string.Equals(
            profile,
            PlasticDrumsV4,
            StringComparison.Ordinal);

    internal static bool IsApprovedPlasticDrumsV4(
        PathGenerationRuntimeIdentity runtime)
        => string.Equals(
               runtime.Version,
               PlasticDrumsV4ChoptVersion,
               StringComparison.Ordinal)
           && string.Equals(
               runtime.BinarySha256,
               PlasticDrumsV4BinarySha256,
               StringComparison.Ordinal)
           && string.Equals(
               runtime.Profile,
               PlasticDrumsV4,
               StringComparison.Ordinal);
}

public sealed record PathInstrumentDefinition(
    string ProviderProperty,
    string MidiTrackName,
    string Instrument,
    string MidiVariant,
    string ChoptInstrument,
    bool DisableProDrums = false);

public static class PathGenerationInstruments
{
    public static readonly IReadOnlyList<PathInstrumentDefinition> Definitions =
    [
        new("gr", "PART GUITAR", "Solo_Guitar", "og", "guitar"),
        new("ba", "PART BASS", "Solo_Bass", "og", "bass"),
        new("ds", "PART DRUMS", "Solo_Drums", "og", "drums"),
        new("vl", "PART VOCALS", "Solo_Vocals", "og", "vocals"),
        new(
            "pg",
            "PLASTIC GUITAR",
            "Solo_PeripheralGuitar",
            "pro",
            "guitar"),
        new(
            "pb",
            "PLASTIC BASS",
            "Solo_PeripheralBass",
            "pro",
            "bass"),
        // Promote PLASTIC DRUMS to PART DRUMS for CHOpt's dedicated FNF engine.
        new(
            "pd",
            "PLASTIC DRUMS",
            "Solo_PeripheralCymbals",
            "drums",
            "prodrums"),
        new(
            "pd",
            "PLASTIC DRUMS",
            "Solo_PeripheralDrums",
            "drums",
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

    public static bool IsPlasticDrumsInstrument(string instrument)
        => instrument is
            "Solo_PeripheralCymbals" or
            "Solo_PeripheralDrums";

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
        if (TryGetRawIntensity(song, out var intensity))
        {
            return PathGenerationInstruments.Definitions
                .Where(definition => intensity.TryGetProperty(definition.ProviderProperty, out _))
                .Select(definition => definition.Instrument)
                .ToArray();
        }

        var typedIntensity = song.track?.@in;
        if (typedIntensity is null)
            return [];

        return PathGenerationInstruments.Definitions
            .Where(definition =>
                typedIntensity.HasProviderProperty(
                    definition.ProviderProperty))
            .Select(definition => definition.Instrument)
            .ToArray();
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

public sealed record PathGenerationBatchPromotionResult(
    PathGenerationPromotionOutcome Outcome,
    int PromotedCount,
    string? FailedSongId = null);

public sealed record PathGenerationBatchPromotionGate(
    long PublicationId,
    long PublishedScrapeId,
    string FreezeReason);

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
    Staged,
    Promoted,
    Skipped,
    Failed,
    Conflicted,
}
internal sealed record PathGenerationAttemptResult(
    PathGenerationAttemptOutcome Outcome,
    string? FailureStage = null,
    string? Detail = null,
    PathGenerationPromotion? StagedPromotion = null);
