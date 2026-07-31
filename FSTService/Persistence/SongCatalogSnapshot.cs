using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using FortniteFestival.Core;

namespace FSTService.Persistence;

internal sealed record SongCatalogSnapshot(
    string CatalogJson,
    string ContentHash,
    int SongCount);

internal static class SongCatalogSnapshotBuilder
{
    internal const int SchemaVersion = 1;

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    internal static SongCatalogSnapshot Create(IEnumerable<Song> songs)
    {
        ArgumentNullException.ThrowIfNull(songs);

        var canonicalSongs = songs
            .Where(static song => !string.IsNullOrWhiteSpace(song.track?.su))
            .Select(CanonicalSongCatalogEntry.FromSong)
            .OrderBy(static song => song.Track.SongId, StringComparer.Ordinal)
            .ToArray();

        for (var i = 1; i < canonicalSongs.Length; i++)
        {
            if (string.Equals(
                    canonicalSongs[i - 1].Track.SongId,
                    canonicalSongs[i].Track.SongId,
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Song catalog contains duplicate song ID '{canonicalSongs[i].Track.SongId}'.");
            }
        }

        var payload = new CanonicalSongCatalog(SchemaVersion, canonicalSongs);
        var jsonBytes = JsonSerializer.SerializeToUtf8Bytes(
            payload,
            SerializerOptions);
        var contentHash = Convert.ToHexString(SHA256.HashData(jsonBytes))
            .ToLowerInvariant();

        return new SongCatalogSnapshot(
            Encoding.UTF8.GetString(jsonBytes),
            contentHash,
            canonicalSongs.Length);
    }

    private static string? CanonicalDate(DateTime value)
    {
        if (value == DateTime.MinValue)
            return null;

        var utc = value.Kind switch
        {
            DateTimeKind.Utc => value,
            DateTimeKind.Local => value.ToUniversalTime(),
            _ => DateTime.SpecifyKind(value, DateTimeKind.Utc),
        };
        return utc.ToString("O", CultureInfo.InvariantCulture);
    }

    private static string[]? CanonicalStrings(IEnumerable<string>? values) =>
        values?
            .OrderBy(static value => value, StringComparer.Ordinal)
            .ToArray();

    private sealed record CanonicalSongCatalog(
        [property: JsonPropertyName("schemaVersion")] int SchemaVersion,
        [property: JsonPropertyName("songs")] CanonicalSongCatalogEntry[] Songs);

    internal sealed record CanonicalSongCatalogEntry(
        [property: JsonPropertyName("_title")] string? ProviderTitle,
        [property: JsonPropertyName("track")] CanonicalSongTrack Track,
        [property: JsonPropertyName("_noIndex")] bool NoIndex,
        [property: JsonPropertyName("_activeDate")] string? ActiveDate,
        [property: JsonPropertyName("lastModified")] string? LastModified,
        [property: JsonPropertyName("_locale")] string? Locale,
        [property: JsonPropertyName("_templateName")] string? TemplateName)
    {
        internal static CanonicalSongCatalogEntry FromSong(Song song) =>
            new(
                song._title,
                CanonicalSongTrack.FromTrack(song.track),
                song._noIndex,
                CanonicalDate(song._activeDate),
                CanonicalDate(song.lastModified),
                song._locale,
                song._templateName);
    }

    internal sealed record CanonicalSongTrack(
        [property: JsonPropertyName("tt")] string? Title,
        [property: JsonPropertyName("ry")] int ReleaseYear,
        [property: JsonPropertyName("dn")] int DurationSeconds,
        [property: JsonPropertyName("sib")] string? Sib,
        [property: JsonPropertyName("sid")] string? Sid,
        [property: JsonPropertyName("sig")] string? Signature,
        [property: JsonPropertyName("qi")] string? Qi,
        [property: JsonPropertyName("sn")] string? Sn,
        [property: JsonPropertyName("ge")] string[]? Genres,
        [property: JsonPropertyName("mk")] string? Mk,
        [property: JsonPropertyName("mm")] string? Mm,
        [property: JsonPropertyName("ab")] string? Album,
        [property: JsonPropertyName("siv")] string? Siv,
        [property: JsonPropertyName("su")] string SongId,
        [property: JsonPropertyName("in")] CanonicalSongIntensity? Intensity,
        [property: JsonPropertyName("mt")] int Tempo,
        [property: JsonPropertyName("_type")] string? Type,
        [property: JsonPropertyName("mu")] string? MidiUrl,
        [property: JsonPropertyName("an")] string? Artist,
        [property: JsonPropertyName("gt")] string[]? GameplayTags,
        [property: JsonPropertyName("ar")] string? ArtistRole,
        [property: JsonPropertyName("au")] string? AlbumArtUrl,
        [property: JsonPropertyName("ti")] string? Ti,
        [property: JsonPropertyName("ld")] string? Ld,
        [property: JsonPropertyName("jc")] string? Jc)
    {
        internal static CanonicalSongTrack FromTrack(Track track) =>
            new(
                track.tt,
                track.ry,
                track.dn,
                track.sib,
                track.sid,
                track.sig,
                track.qi,
                track.sn,
                CanonicalStrings(track.ge),
                track.mk,
                track.mm,
                track.ab,
                track.siv,
                track.su,
                CanonicalSongIntensity.FromIntensity(track.@in),
                track.mt,
                track._type,
                track.mu,
                track.an,
                CanonicalStrings(track.gt),
                track.ar,
                track.au,
                track.ti,
                track.ld,
                track.jc);
    }

    internal sealed record CanonicalSongIntensity(
        [property: JsonPropertyName("pb")] int ProBass,
        [property: JsonPropertyName("pd")] int ProDrums,
        [property: JsonPropertyName("vl")] int Vocals,
        [property: JsonPropertyName("pg")] int ProGuitar,
        [property: JsonPropertyName("_type")] string? Type,
        [property: JsonPropertyName("gr")] int Guitar,
        [property: JsonPropertyName("ds")] int Drums,
        [property: JsonPropertyName("ba")] int Bass,
        [property: JsonPropertyName("bd")] int ProVocals)
    {
        internal static CanonicalSongIntensity? FromIntensity(In? intensity) =>
            intensity is null
                ? null
                : new CanonicalSongIntensity(
                    intensity.pb,
                    intensity.pd,
                    intensity.vl,
                    intensity.pg,
                    intensity._type,
                    intensity.gr,
                    intensity.ds,
                    intensity.ba,
                    intensity.bd);
    }
}
