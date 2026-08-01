using System.Buffers;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using FortniteFestival.Core;
using FortniteFestival.Core.Persistence;

namespace FSTService.Persistence;

internal sealed record SongCatalogSnapshot(
    string CatalogJson,
    string ContentHash,
    int SongCount);

internal static class SongCatalogSnapshotBuilder
{
    internal const int SchemaVersion = 2;

    private static readonly HashSet<string> LocalSongFields =
    [
        "imagePath",
        "isInLocalData",
        "isSelected",
        "providerFields",
        "providerJson",
    ];

    internal static SongCatalogSnapshot Create(IEnumerable<Song> songs)
    {
        ArgumentNullException.ThrowIfNull(songs);

        var songList = songs.ToArray();
        var invalidSong = songList.FirstOrDefault(
            static song => string.IsNullOrWhiteSpace(song.track?.su));
        if (invalidSong is not null)
        {
            throw new InvalidOperationException(
                "Song catalog contains a song without a provider song ID.");
        }

        var canonicalSongs = songList
            .Select(static song => (
                SongId: song.track.su!,
                Json: CreateProviderSongJson(song)))
            .OrderBy(static song => song.SongId, StringComparer.Ordinal)
            .ToArray();

        for (var i = 1; i < canonicalSongs.Length; i++)
        {
            if (string.Equals(
                    canonicalSongs[i - 1].SongId,
                    canonicalSongs[i].SongId,
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Song catalog contains duplicate song ID '{canonicalSongs[i].SongId}'.");
            }
        }

        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schemaVersion", SchemaVersion);
            writer.WritePropertyName("songs");
            writer.WriteStartArray();
            foreach (var song in canonicalSongs)
                writer.WriteRawValue(song.Json, skipInputValidation: true);
            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        var jsonBytes = buffer.WrittenSpan.ToArray();
        var contentHash = Convert.ToHexString(SHA256.HashData(jsonBytes))
            .ToLowerInvariant();

        return new SongCatalogSnapshot(
            Encoding.UTF8.GetString(jsonBytes),
            contentHash,
            canonicalSongs.Length);
    }

    internal static string CreateProviderSongJson(Song song)
    {
        ArgumentNullException.ThrowIfNull(song);

        JsonElement providerElement;
        if (song.providerJson is { ValueKind: JsonValueKind.Object } raw)
        {
            providerElement = raw;
        }
        else
        {
            providerElement = JsonSerializer.SerializeToElement(
                BuildProviderSongNode(song));
        }

        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
            WriteCanonical(writer, providerElement, isSongRoot: true);
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    internal static Song DeserializeProviderSong(string providerJson)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerJson);

        using var document = JsonDocument.Parse(providerJson);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException("Provider song JSON must be an object.");

        var song = JsonSerializer.Deserialize<Song>(
            document.RootElement.GetRawText())
            ?? throw new InvalidOperationException(
                "Provider song JSON could not be deserialized.");
        if (string.IsNullOrWhiteSpace(song.track?.su))
            throw new InvalidOperationException(
                "Provider song JSON has no track song ID.");

        song.providerJson = document.RootElement.Clone();
        return song;
    }

    internal static IList<Song> DeserializeCatalog(string catalogJson)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(catalogJson);

        using var document = JsonDocument.Parse(catalogJson);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("schemaVersion", out var schemaVersion)
            || schemaVersion.GetInt32() != SchemaVersion
            || !root.TryGetProperty("songs", out var songs)
            || songs.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException(
                "The persisted exact song catalog has an unsupported schema.");
        }

        return songs.EnumerateArray()
            .Select(static song =>
                DeserializeProviderSong(song.GetRawText()))
            .ToList();
    }

    internal static void ValidateToken(
        SongCatalogSnapshot snapshot,
        SongCatalogPersistenceToken token)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(token);

        if (token.SchemaVersion != SchemaVersion
            || token.SongCount != snapshot.SongCount
            || !string.Equals(
                token.ContentHash,
                snapshot.ContentHash,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The persisted song catalog token does not match the exact catalog selected for the scrape.");
        }
    }

    private static JsonObject BuildProviderSongNode(Song song)
    {
        var root = new JsonObject();
        Add(root, "_title", song._title);
        root["track"] = BuildProviderTrackNode(song.track);
        root["_noIndex"] = song._noIndex;
        Add(root, "_activeDate", CanonicalDate(song._activeDate));
        Add(root, "lastModified", CanonicalDate(song.lastModified));
        Add(root, "_locale", song._locale);
        Add(root, "_templateName", song._templateName);
        AddExtensions(root, song.providerFields);
        return root;
    }

    private static JsonNode? BuildProviderTrackNode(Track? track)
    {
        if (track is null)
            return null;

        var node = new JsonObject
        {
            ["ry"] = track.ry,
            ["dn"] = track.dn,
            ["mt"] = track.mt,
        };
        Add(node, "tt", track.tt);
        Add(node, "sib", track.sib);
        Add(node, "sid", track.sid);
        Add(node, "sig", track.sig);
        Add(node, "qi", track.qi);
        Add(node, "sn", track.sn);
        Add(node, "ge", track.ge);
        Add(node, "mk", track.mk);
        Add(node, "mm", track.mm);
        Add(node, "ab", track.ab);
        Add(node, "siv", track.siv);
        Add(node, "su", track.su);
        node["in"] = BuildProviderIntensityNode(track.@in);
        Add(node, "_type", track._type);
        Add(node, "mu", track.mu);
        Add(node, "an", track.an);
        Add(node, "gt", track.gt);
        Add(node, "ar", track.ar);
        Add(node, "au", track.au);
        Add(node, "ti", track.ti);
        Add(node, "ld", track.ld);
        Add(node, "jc", track.jc);
        AddExtensions(node, track.providerFields);
        return node;
    }

    private static JsonNode? BuildProviderIntensityNode(In? intensity)
    {
        if (intensity is null)
            return null;

        var node = new JsonObject();
        AddIfPresent(node, intensity, "pb", intensity.pb);
        AddIfPresent(node, intensity, "pd", intensity.pd);
        AddIfPresent(node, intensity, "vl", intensity.vl);
        AddIfPresent(node, intensity, "pg", intensity.pg);
        AddIfPresent(node, intensity, "gr", intensity.gr);
        AddIfPresent(node, intensity, "ds", intensity.ds);
        AddIfPresent(node, intensity, "ba", intensity.ba);
        AddIfPresent(node, intensity, "bd", intensity.bd);
        Add(node, "_type", intensity._type);
        AddExtensions(node, intensity.providerFields);
        return node;
    }

    private static void AddIfPresent(
        JsonObject target,
        In intensity,
        string name,
        int value)
    {
        if (intensity.HasProviderProperty(name))
            target[name] = value;
    }

    private static void Add(JsonObject target, string name, string? value)
    {
        if (value is not null)
            target[name] = value;
    }

    private static void Add(
        JsonObject target,
        string name,
        IEnumerable<string>? values)
    {
        if (values is not null)
            target[name] = new JsonArray(
                values.Select(static value => JsonValue.Create(value)).ToArray());
    }

    private static void AddExtensions(
        JsonObject target,
        IReadOnlyDictionary<string, JsonElement>? extensions)
    {
        if (extensions is null)
            return;

        foreach (var extension in extensions)
        {
            if (!target.ContainsKey(extension.Key))
            {
                target[extension.Key] =
                    JsonNode.Parse(extension.Value.GetRawText());
            }
        }
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

    private static void WriteCanonical(
        Utf8JsonWriter writer,
        JsonElement element,
        bool isSongRoot = false)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in element.EnumerateObject()
                             .Where(property =>
                                 !isSongRoot
                                 || !LocalSongFields.Contains(property.Name))
                             .OrderBy(
                                 static property => property.Name,
                                 StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    WriteCanonical(writer, property.Value);
                }
                writer.WriteEndObject();
                break;

            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                    WriteCanonical(writer, item);
                writer.WriteEndArray();
                break;

            case JsonValueKind.String:
                writer.WriteStringValue(element.GetString());
                break;

            case JsonValueKind.Number:
                if (element.TryGetInt64(out var int64))
                    writer.WriteNumberValue(int64);
                else if (element.TryGetDecimal(out var decimalValue))
                    writer.WriteNumberValue(decimalValue);
                else
                    writer.WriteNumberValue(element.GetDouble());
                break;

            case JsonValueKind.True:
                writer.WriteBooleanValue(true);
                break;

            case JsonValueKind.False:
                writer.WriteBooleanValue(false);
                break;

            case JsonValueKind.Null:
            case JsonValueKind.Undefined:
                writer.WriteNullValue();
                break;

            default:
                throw new InvalidOperationException(
                    $"Unsupported provider JSON value kind {element.ValueKind}.");
        }
    }
}
