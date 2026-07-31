using System.Text.Json;
using FortniteFestival.Core;
using FSTService.Persistence;
using FSTService.Tests.Helpers;
using Npgsql;

namespace FSTService.Tests.Unit;

public sealed class FestivalPersistenceTests : IDisposable
{
    private readonly NpgsqlDataSource _dataSource =
        SharedPostgresContainer.CreateDatabase();

    public void Dispose() => _dataSource.Dispose();

    [Fact]
    public async Task SaveSongsAsync_captures_canonical_provider_catalog()
    {
        var persistence = new FestivalPersistence(_dataSource);
        var first = CreateSong("song-a", "Alpha");
        var second = CreateSong("song-z", "Zulu");
        first.imagePath = "/local/alpha.jpg";
        first.isSelected = true;
        first.isInLocalData = "yes";

        var expected = SongCatalogSnapshotBuilder.Create([first, second]);
        var reversed = SongCatalogSnapshotBuilder.Create([second, first]);
        Assert.Equal(expected.CatalogJson, reversed.CatalogJson);
        Assert.Equal(expected.ContentHash, reversed.ContentHash);

        await persistence.SaveSongsAsync([second, first]);

        string catalogJson;
        string contentHash;
        int songCount;
        DateTime capturedAt;
        await using (var conn = await _dataSource.OpenConnectionAsync())
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                SELECT catalog_json::text, content_hash, song_count, captured_at
                FROM live_song_catalog
                WHERE id = TRUE
                """;
            await using var reader = await cmd.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            catalogJson = reader.GetString(0);
            contentHash = reader.GetString(1);
            songCount = reader.GetInt32(2);
            capturedAt = reader.GetDateTime(3);
        }

        Assert.Equal(expected.ContentHash, contentHash);
        Assert.Equal(2, songCount);

        using (var document = JsonDocument.Parse(catalogJson))
        {
            var root = document.RootElement;
            Assert.Equal(1, root.GetProperty("schemaVersion").GetInt32());
            var songs = root.GetProperty("songs");
            Assert.Equal(2, songs.GetArrayLength());

            var alpha = songs[0];
            Assert.Equal("song-a", alpha.GetProperty("track").GetProperty("su").GetString());
            Assert.Equal("Alpha provider title", alpha.GetProperty("_title").GetString());
            Assert.Equal("en-US", alpha.GetProperty("_locale").GetString());
            Assert.Equal("template-song-a", alpha.GetProperty("_templateName").GetString());
            Assert.Equal(
                "2026-07-30T12:00:00.0000000Z",
                alpha.GetProperty("_activeDate").GetString());
            Assert.Equal(
                "2026-07-31T13:14:15.0000000Z",
                alpha.GetProperty("lastModified").GetString());
            Assert.False(alpha.TryGetProperty("imagePath", out _));
            Assert.False(alpha.TryGetProperty("isSelected", out _));
            Assert.False(alpha.TryGetProperty("isInLocalData", out _));

            var track = alpha.GetProperty("track");
            Assert.Equal("Album", track.GetProperty("ab").GetString());
            Assert.Equal("https://example.test/song-a.dat", track.GetProperty("mu").GetString());
            Assert.Equal("https://example.test/song-a.jpg", track.GetProperty("au").GetString());
            Assert.Equal(
                ["electronic", "rock"],
                track.GetProperty("ge")
                    .EnumerateArray()
                    .Select(static value => value.GetString()!)
                    .ToArray());
            Assert.Equal(8, track.GetProperty("in").GetProperty("bd").GetInt32());
        }

        await persistence.SaveSongsAsync([first, second]);

        await using var verifyConn = await _dataSource.OpenConnectionAsync();
        await using var verify = verifyConn.CreateCommand();
        verify.CommandText = """
            SELECT content_hash, captured_at
            FROM live_song_catalog
            WHERE id = TRUE
            """;
        await using var verifyReader = await verify.ExecuteReaderAsync();
        Assert.True(await verifyReader.ReadAsync());
        Assert.Equal(contentHash, verifyReader.GetString(0));
        Assert.Equal(capturedAt, verifyReader.GetDateTime(1));
    }

    private static Song CreateSong(string songId, string title) =>
        new()
        {
            _title = $"{title} provider title",
            _noIndex = false,
            _activeDate = new DateTime(
                2026, 7, 30, 12, 0, 0, DateTimeKind.Utc),
            lastModified = new DateTime(
                2026, 7, 31, 13, 14, 15, DateTimeKind.Utc),
            _locale = "en-US",
            _templateName = $"template-{songId}",
            track = new Track
            {
                tt = title,
                ry = 2026,
                dn = 245,
                sib = "sib",
                sid = "sid",
                sig = "4/4",
                qi = "qi",
                sn = "sn",
                ge = ["rock", "electronic"],
                mk = "mk",
                mm = "mm",
                ab = "Album",
                siv = "siv",
                su = songId,
                @in = new In
                {
                    pb = 1,
                    pd = 2,
                    vl = 3,
                    pg = 4,
                    _type = "SparkTrackIntensities",
                    gr = 5,
                    ds = 6,
                    ba = 7,
                    bd = 8,
                },
                mt = 120,
                _type = "SparkTrack",
                mu = $"https://example.test/{songId}.dat",
                an = "Artist",
                gt = ["tag-z", "tag-a"],
                ar = "Artist",
                au = $"https://example.test/{songId}.jpg",
                ti = "ti",
                ld = "ld",
                jc = "jc",
            },
        };
}
