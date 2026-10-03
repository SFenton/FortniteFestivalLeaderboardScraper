using System.Collections.Concurrent;
using System.Text.Json;
using FSTService.Persistence;

namespace FSTService.Api;

/// <summary>
/// Reads one rival's stored song samples from the published, precomputed
/// <c>rivals-all:{accountId}</c> response. Rival detail responses are not
/// precomputed, so while public reads are frozen this payload is the only
/// published copy of <c>rival_song_samples</c>; the live table may already hold
/// candidate data for the next publication.
/// </summary>
internal static class PublishedRivalSamples
{
    private const int MaxMemoEntries = 256;
    private static readonly string[] Directions = ["above", "below"];

    private static readonly ConcurrentDictionary<string, PublishedRival?> Memo =
        new(StringComparer.Ordinal);

    internal sealed record PublishedRival(
        string? DisplayName,
        IReadOnlyList<RivalSongSampleRow> Samples);

    /// <summary>
    /// Returns the rival's published samples, or null when the payload does not
    /// list the rival or cannot be read. Results are memoized per source ETag so
    /// clients that request one instrument at a time parse the payload once.
    /// </summary>
    public static PublishedRival? TryRead(
        (byte[] Json, string ETag) rivalsAll,
        string accountId,
        string rivalId)
    {
        var key = string.Concat(rivalsAll.ETag, "|", accountId, "|", rivalId);
        if (Memo.TryGetValue(key, out var memoized))
            return memoized;

        var parsed = Parse(rivalsAll.Json, accountId, rivalId);
        if (Memo.Count >= MaxMemoEntries)
            Memo.Clear();
        Memo[key] = parsed;
        return parsed;
    }

    internal static PublishedRival? Parse(
        byte[] json,
        string accountId,
        string rivalId)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("combos", out var combos)
                || combos.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            var songIds = new List<string?>();
            if (root.TryGetProperty("songs", out var songs)
                && songs.ValueKind == JsonValueKind.Array)
            {
                foreach (var song in songs.EnumerateArray())
                    songIds.Add(song.ValueKind == JsonValueKind.String ? song.GetString() : null);
            }

            foreach (var combo in combos.EnumerateArray())
            {
                foreach (var direction in Directions)
                {
                    if (!combo.TryGetProperty(direction, out var rivals)
                        || rivals.ValueKind != JsonValueKind.Array)
                    {
                        continue;
                    }

                    foreach (var rival in rivals.EnumerateArray())
                    {
                        if (!string.Equals(
                                ReadString(rival, "accountId"),
                                rivalId,
                                StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        // Samples are stored per (user, rival, instrument, song), so
                        // every listing of the rival carries the same sample set.
                        return new PublishedRival(
                            ReadString(rival, "displayName"),
                            ReadSamples(rival, songIds, accountId, rivalId));
                    }
                }
            }

            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static List<RivalSongSampleRow> ReadSamples(
        JsonElement rival,
        List<string?> songIds,
        string accountId,
        string rivalId)
    {
        var samples = new List<RivalSongSampleRow>();
        if (!rival.TryGetProperty("samples", out var rows)
            || rows.ValueKind != JsonValueKind.Array)
        {
            return samples;
        }

        foreach (var row in rows.EnumerateArray())
        {
            var songIndex = ReadInt(row, "s");
            var instrument = ReadString(row, "i");
            var userRank = ReadInt(row, "ur");
            var rivalRank = ReadInt(row, "rr");
            if (songIndex is not { } index
                || index < 0
                || index >= songIds.Count
                || songIds[index] is not { } songId
                || string.IsNullOrEmpty(instrument)
                || userRank is not { } ur
                || rivalRank is not { } rr)
            {
                continue;
            }

            samples.Add(new RivalSongSampleRow
            {
                UserId = accountId,
                RivalAccountId = rivalId,
                Instrument = instrument,
                SongId = songId,
                UserRank = ur,
                RivalRank = rr,
                // Every stored sample is written with rank_delta = rival_rank - user_rank.
                RankDelta = rr - ur,
                UserScore = ReadInt(row, "us"),
                RivalScore = ReadInt(row, "rs"),
            });
        }

        return samples;
    }

    private static string? ReadString(JsonElement element, string name)
        => element.TryGetProperty(name, out var value)
           && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static int? ReadInt(JsonElement element, string name)
        => element.TryGetProperty(name, out var value)
           && value.ValueKind == JsonValueKind.Number
           && value.TryGetInt32(out var parsed)
            ? parsed
            : null;
}
