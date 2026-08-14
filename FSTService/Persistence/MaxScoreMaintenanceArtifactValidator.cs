using System.Security.Cryptography;
using System.Text.Json;
using FSTService.Scraping;

namespace FSTService.Persistence;

internal static class MaxScoreMaintenanceArtifactValidator
{
    internal static MaxScoreMaintenancePathIdentity CaptureCurrentIdentity(
        string dataDirectory,
        PathGenerationState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        var generationId = state.ArtifactGenerationId
            ?? throw new InvalidOperationException(
                $"Current immutable generation is missing for {state.SongId}.");
        var validated = PathArtifactResolver.ValidateImmutableGeneration(
            dataDirectory,
            state.SongId,
            generationId);
        var identity = MaxScoreMaintenancePathIdentity.From(
            state,
            validated);
        ValidateIdentity(
            state.SongId,
            identity,
            validated,
            "current");
        return identity;
    }

    internal static MaxScoreMaintenancePlasticDrumsEvidence
        CapturePlasticDrumsEvidence(
            ValidatedPathGeneration validated)
    {
        ArgumentNullException.ThrowIfNull(validated);
        var soloDrums = ReadExpertEvidence(
            validated,
            "Solo_Drums",
            requireAuthoredWindows: false);
        var proCymbals = ReadExpertEvidence(
            validated,
            "Solo_PeripheralCymbals",
            requireAuthoredWindows: true);
        var proDrums = ReadExpertEvidence(
            validated,
            "Solo_PeripheralDrums",
            requireAuthoredWindows: true);
        return new MaxScoreMaintenancePlasticDrumsEvidence(
                proCymbals.AuthoredWindowCount,
                proDrums.AuthoredWindowCount,
                soloDrums.NoteInventorySha256,
                proCymbals.NoteInventorySha256,
                proDrums.NoteInventorySha256)
            .ValidateAndNormalize();
    }

    internal static MaxScoreMaintenanceArtifactEvidence ValidateManifestSong(
        string dataDirectory,
        MaxScoreMaintenanceManifestSong song)
    {
        ArgumentNullException.ThrowIfNull(song);
        var current = PathArtifactResolver.ValidateImmutableGeneration(
            dataDirectory,
            song.SongId,
            song.CurrentPath.ArtifactGenerationId!);
        ValidateIdentity(
            song.SongId,
            song.CurrentPath,
            current,
            "current");

        var staged = PathArtifactResolver.ValidateImmutableGeneration(
            dataDirectory,
            song.SongId,
            song.StagedPath.ArtifactGenerationId!);
        ValidateIdentity(
            song.SongId,
            song.StagedPath,
            staged,
            "staged");

        if (song.PlasticDrumsEvidence is not null)
        {
            var actualPlasticEvidence =
                CapturePlasticDrumsEvidence(staged);
            if (actualPlasticEvidence != song.PlasticDrumsEvidence)
            {
                throw new InvalidOperationException(
                    $"Staged plastic-drums artifact evidence changed for {song.SongId}.");
            }
        }

        return new MaxScoreMaintenanceArtifactEvidence(
            song.SongId,
            song.CurrentPath.ArtifactGenerationId!,
            current.ArtifactTreeSha256,
            current.ArtifactFileCount,
            song.StagedPath.ArtifactGenerationId!,
            staged.ArtifactTreeSha256,
            staged.ArtifactFileCount,
            song.PlasticDrumsEvidence);
    }

    internal static void ValidateIdentity(
        string songId,
        MaxScoreMaintenancePathIdentity expected,
        ValidatedPathGeneration validated,
        string label)
    {
        var manifest = validated.Manifest;
        if (!string.Equals(
                manifest.GenerationId,
                expected.ArtifactGenerationId,
                StringComparison.Ordinal)
            || !string.Equals(
                manifest.SongId,
                songId,
                StringComparison.Ordinal)
            || !string.Equals(
                manifest.DatFileHash,
                expected.DatFileHash,
                StringComparison.Ordinal)
            || !ProviderTimestampIdentity.Equivalent(
                manifest.SongLastModified,
                expected.SongLastModified)
            || manifest.GeneratedAtUtc != expected.GeneratedAtUtc
            || !string.Equals(
                manifest.ChoptVersion,
                expected.ChoptVersion,
                StringComparison.Ordinal)
            || !string.Equals(
                manifest.ChoptBinarySha256,
                expected.ChoptBinarySha256,
                StringComparison.Ordinal)
            || !string.Equals(
                manifest.GenerationProfile,
                expected.GenerationProfile,
                StringComparison.Ordinal)
            || !manifest.ExpectedInstruments.SequenceEqual(
                expected.ExpectedInstruments,
                StringComparer.Ordinal)
            || MaxScoreMaintenanceMaxima.From(validated.MaxScores)
                != expected.Maxima
            || !string.Equals(
                validated.ArtifactTreeSha256,
                expected.ArtifactTreeSha256,
                StringComparison.Ordinal)
            || validated.ArtifactFileCount
                != expected.ArtifactFileCount)
        {
            throw new InvalidOperationException(
                $"Immutable {label} generation failed manifest/artifact-tree identity for {songId}.");
        }
    }

    private static ExpertArtifactEvidence ReadExpertEvidence(
        ValidatedPathGeneration validated,
        string instrument,
        bool requireAuthoredWindows)
    {
        if (!validated.Manifest.ExpectedInstruments.Contains(
                instrument,
                StringComparer.Ordinal))
        {
            throw new InvalidOperationException(
                $"Staged generation lacks required evidence instrument {instrument}.");
        }

        var path = Path.Combine(
            validated.GenerationDirectory,
            instrument,
            "expert.json");
        using var document = JsonDocument.Parse(
            File.ReadAllBytes(path));
        var root = document.RootElement;
        if (!root.TryGetProperty("notes", out var notes)
            || notes.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException(
                $"Expert path evidence lacks a note inventory for {instrument}.");
        }

        var authoredWindowCount = 0;
        if (root.TryGetProperty("drumFills", out var drumFills)
            && drumFills.ValueKind == JsonValueKind.Array)
        {
            authoredWindowCount = drumFills.GetArrayLength();
        }
        if (requireAuthoredWindows && authoredWindowCount <= 0)
        {
            throw new InvalidOperationException(
                $"Expert path evidence lacks authored activation windows for {instrument}.");
        }

        var noteInventorySha256 = Convert.ToHexStringLower(
            SHA256.HashData(
                JsonSerializer.SerializeToUtf8Bytes(notes)));
        return new ExpertArtifactEvidence(
            authoredWindowCount,
            noteInventorySha256);
    }

    private sealed record ExpertArtifactEvidence(
        int AuthoredWindowCount,
        string NoteInventorySha256);
}
