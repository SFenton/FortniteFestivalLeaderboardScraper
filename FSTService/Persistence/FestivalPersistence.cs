using FortniteFestival.Core;
using FortniteFestival.Core.Persistence;
using Npgsql;
using NpgsqlTypes;

namespace FSTService.Persistence;

/// <summary>
/// <see cref="IFestivalPersistence"/> implementation for the Core library's
/// FestivalService. Reads/writes the <c>songs</c> table.
/// The Scores table is not used — leaderboard data lives in leaderboard_entries.
/// </summary>
public sealed class FestivalPersistence :
    IFestivalPersistence,
    IVersionedSongCatalogPersistence
{
    private readonly NpgsqlDataSource _ds;

    public FestivalPersistence(NpgsqlDataSource dataSource)
    {
        _ds = dataSource;
    }

    public async Task<IList<Song>> LoadSongsAsync()
    {
        await using var conn = await _ds.OpenConnectionAsync();

        await using (var catalog = conn.CreateCommand())
        {
            catalog.CommandText = """
                SELECT catalog_json::text
                FROM live_song_catalog
                WHERE id = TRUE
                  AND is_exact
                  AND source_kind = 'provider_exact'
                  AND schema_version = @schemaVersion
                """;
            catalog.Parameters.AddWithValue(
                "schemaVersion",
                SongCatalogSnapshotBuilder.SchemaVersion);
            if (await catalog.ExecuteScalarAsync() is string catalogJson)
            {
                var exactSongs =
                    SongCatalogSnapshotBuilder.DeserializeCatalog(
                        catalogJson);
                await RestoreLocalImagePathsAsync(conn, exactSongs);
                return exactSongs;
            }
        }

        var list = new List<Song>();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT song_id, title, artist, active_date, last_modified, image_path,
                   lead_diff, bass_diff, vocals_diff, drums_diff,
                   pro_lead_diff, pro_bass_diff, release_year, tempo,
                   plastic_guitar_diff, plastic_bass_diff, plastic_drums_diff,
                   pro_vocals_diff, provider_json::text
            FROM songs
            """;
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
        {
            if (!r.IsDBNull(18))
            {
                var providerSong =
                    SongCatalogSnapshotBuilder.DeserializeProviderSong(
                        r.GetString(18));
                providerSong.imagePath =
                    r.IsDBNull(5) ? null : r.GetString(5);
                list.Add(providerSong);
                continue;
            }

            var song = new Song
            {
                track = new Track
                {
                    su = r.IsDBNull(0) ? null : r.GetString(0),
                    tt = r.IsDBNull(1) ? null : r.GetString(1),
                    an = r.IsDBNull(2) ? null : r.GetString(2),
                    @in = new In
                    {
                        gr = r.IsDBNull(6) ? 0 : r.GetInt32(6),
                        ba = r.IsDBNull(7) ? 0 : r.GetInt32(7),
                        vl = r.IsDBNull(8) ? 0 : r.GetInt32(8),
                        ds = r.IsDBNull(9) ? 0 : r.GetInt32(9),
                        pg = r.IsDBNull(10) ? 0 : r.GetInt32(10),
                        pb = r.IsDBNull(11) ? 0 : r.GetInt32(11),
                    },
                    ry = r.IsDBNull(12) ? 0 : r.GetInt32(12),
                    mt = r.IsDBNull(13) ? 0 : r.GetInt32(13),
                },
                _activeDate = ParseDate(r, 3),
                lastModified = ParseDate(r, 4),
                imagePath = r.IsDBNull(5) ? null : r.GetString(5),
            };
            if (song.track?.@in != null)
            {
                if (!r.IsDBNull(14)) song.track.@in.pg = r.GetInt32(14);
                if (!r.IsDBNull(15)) song.track.@in.pb = r.GetInt32(15);
                if (!r.IsDBNull(16)) song.track.@in.pd = r.GetInt32(16);
                if (!r.IsDBNull(17))
                {
                    var pv = r.GetInt32(17);
                    song.track.@in.bd = Track.HasChartedDifficulty(pv) ? pv : 99;
                }
            }
            list.Add(song);
        }
        return list;
    }

    public async Task SaveSongsAsync(IEnumerable<Song> songs)
    {
        await SaveSongsVersionedAsync(songs);
    }

    public async Task<SongCatalogPersistenceToken> SaveSongsVersionedAsync(
        IEnumerable<Song> songs)
    {
        var songList = songs.ToArray();
        var catalogSnapshot = SongCatalogSnapshotBuilder.Create(songList);

        await using var conn = await _ds.OpenConnectionAsync();
        await using var tx = await conn.BeginTransactionAsync();

        await using (var publicationLock = conn.CreateCommand())
        {
            publicationLock.Transaction = tx;
            publicationLock.CommandText =
                "SELECT pg_advisory_xact_lock(@lockKey)";
            publicationLock.Parameters.AddWithValue(
                "lockKey",
                PublicationGenerationSchema.AdvisoryLockKey);
            await publicationLock.ExecuteNonQueryAsync();
        }

        foreach (var s in songList)
        {
            await using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO songs (song_id, title, artist, active_date, last_modified, image_path,
                                   lead_diff, bass_diff, vocals_diff, drums_diff,
                                   pro_lead_diff, pro_bass_diff, release_year, tempo,
                                   plastic_guitar_diff, plastic_bass_diff,
                                   plastic_drums_diff, pro_vocals_diff,
                                   provider_json)
                VALUES (@id, @title, @artist, @active, @modified, @image,
                        @lead, @bass, @vocals, @drums, @plead, @pbass, @ry, @tempo,
                        @plGtr, @plBass, @plDrums, @proVocals, @providerJson)
                ON CONFLICT (song_id) DO UPDATE SET
                    title = EXCLUDED.title, artist = EXCLUDED.artist,
                    active_date = EXCLUDED.active_date, last_modified = EXCLUDED.last_modified,
                    image_path = EXCLUDED.image_path,
                    lead_diff = EXCLUDED.lead_diff, bass_diff = EXCLUDED.bass_diff,
                    vocals_diff = EXCLUDED.vocals_diff, drums_diff = EXCLUDED.drums_diff,
                    pro_lead_diff = EXCLUDED.pro_lead_diff, pro_bass_diff = EXCLUDED.pro_bass_diff,
                    release_year = EXCLUDED.release_year, tempo = EXCLUDED.tempo,
                    plastic_guitar_diff = EXCLUDED.plastic_guitar_diff, plastic_bass_diff = EXCLUDED.plastic_bass_diff,
                    plastic_drums_diff = EXCLUDED.plastic_drums_diff, pro_vocals_diff = EXCLUDED.pro_vocals_diff,
                    provider_json = EXCLUDED.provider_json
                """;

            var rawProVocals = s.track?.@in?.bd;
            var proVocals = Track.HasChartedDifficulty(rawProVocals) ? rawProVocals!.Value : 99;

            cmd.Parameters.AddWithValue("id", s.track?.su ?? string.Empty);
            cmd.Parameters.AddWithValue("title", s.track?.tt ?? string.Empty);
            cmd.Parameters.AddWithValue("artist", s.track?.an ?? string.Empty);
            cmd.Parameters.AddWithValue("active", s._activeDate == DateTime.MinValue ? "" : s._activeDate.ToString("o"));
            cmd.Parameters.AddWithValue("modified", s.lastModified == DateTime.MinValue ? "" : s.lastModified.ToString("o"));
            cmd.Parameters.AddWithValue("image", s.imagePath ?? string.Empty);
            cmd.Parameters.AddWithValue("lead", s.track?.@in?.gr ?? 0);
            cmd.Parameters.AddWithValue("bass", s.track?.@in?.ba ?? 0);
            cmd.Parameters.AddWithValue("vocals", s.track?.@in?.vl ?? 0);
            cmd.Parameters.AddWithValue("drums", s.track?.@in?.ds ?? 0);
            cmd.Parameters.AddWithValue("plead", s.track?.@in?.pg ?? 0);
            cmd.Parameters.AddWithValue("pbass", s.track?.@in?.pb ?? 0);
            cmd.Parameters.AddWithValue("ry", s.track?.ry ?? 0);
            cmd.Parameters.AddWithValue("tempo", s.track?.mt ?? 0);
            cmd.Parameters.AddWithValue("plGtr", s.track?.@in?.pg ?? 0);
            cmd.Parameters.AddWithValue("plBass", s.track?.@in?.pb ?? 0);
            cmd.Parameters.AddWithValue("plDrums", s.track?.@in?.pd ?? 0);
            cmd.Parameters.AddWithValue("proVocals", proVocals);
            cmd.Parameters.Add(
                "providerJson",
                NpgsqlDbType.Jsonb).Value =
                SongCatalogSnapshotBuilder.CreateProviderSongJson(s);

            await cmd.ExecuteNonQueryAsync();
        }

        long? existingVersion = null;
        var existingIsExact = false;
        var existingSchemaVersion = 0;
        string? existingHash = null;
        var existingSongCount = -1;
        DateTime? existingCapturedAt = null;
        await using (var current = conn.CreateCommand())
        {
            current.Transaction = tx;
            current.CommandText = """
                SELECT catalog_version, schema_version, content_hash,
                       song_count, captured_at, is_exact
                FROM live_song_catalog
                WHERE id = TRUE
                FOR UPDATE
                """;
            await using var reader = await current.ExecuteReaderAsync();
            if (await reader.ReadAsync())
            {
                existingVersion =
                    reader.IsDBNull(0) ? null : reader.GetInt64(0);
                existingSchemaVersion = reader.GetInt32(1);
                existingHash = reader.GetString(2);
                existingSongCount = reader.GetInt32(3);
                existingCapturedAt = reader.GetDateTime(4);
                existingIsExact = reader.GetBoolean(5);
            }
        }

        var unchanged =
            existingVersion.HasValue
            && existingIsExact
            && existingSchemaVersion == SongCatalogSnapshotBuilder.SchemaVersion
            && existingSongCount == catalogSnapshot.SongCount
            && string.Equals(
                existingHash,
                catalogSnapshot.ContentHash,
                StringComparison.Ordinal);
        var catalogVersion = unchanged
            ? existingVersion!.Value
            : await NextCatalogVersionAsync(conn, tx);
        var capturedAt = unchanged
            ? existingCapturedAt!.Value
            : DateTime.UtcNow;

        await using (var catalog = conn.CreateCommand())
        {
            catalog.Transaction = tx;
            catalog.CommandText = """
                INSERT INTO live_song_catalog (
                    id, catalog_version, schema_version, catalog_json,
                    content_hash, song_count, source_kind, is_exact,
                    captured_at)
                VALUES (
                    TRUE, @catalogVersion, @schemaVersion, @catalogJson,
                    @contentHash, @songCount, 'provider_exact', TRUE,
                    @capturedAt)
                ON CONFLICT (id) DO UPDATE SET
                    catalog_version = EXCLUDED.catalog_version,
                    schema_version = EXCLUDED.schema_version,
                    catalog_json = EXCLUDED.catalog_json,
                    content_hash = EXCLUDED.content_hash,
                    song_count = EXCLUDED.song_count,
                    source_kind = EXCLUDED.source_kind,
                    is_exact = EXCLUDED.is_exact,
                    captured_at = EXCLUDED.captured_at
                """;
            catalog.Parameters.AddWithValue(
                "catalogVersion",
                catalogVersion);
            catalog.Parameters.AddWithValue(
                "schemaVersion",
                SongCatalogSnapshotBuilder.SchemaVersion);
            catalog.Parameters.Add(
                "catalogJson",
                NpgsqlDbType.Jsonb).Value = catalogSnapshot.CatalogJson;
            catalog.Parameters.AddWithValue(
                "contentHash",
                catalogSnapshot.ContentHash);
            catalog.Parameters.AddWithValue(
                "songCount",
                catalogSnapshot.SongCount);
            catalog.Parameters.AddWithValue("capturedAt", capturedAt);
            await catalog.ExecuteNonQueryAsync();
        }

        await tx.CommitAsync();
        return new SongCatalogPersistenceToken(
            catalogVersion,
            SongCatalogSnapshotBuilder.SchemaVersion,
            catalogSnapshot.ContentHash,
            catalogSnapshot.SongCount);
    }

    public Task<IList<LeaderboardData>> LoadScoresAsync()
    {
        // The per-user Scores table is deprecated in PG — data lives in leaderboard_entries
        return Task.FromResult<IList<LeaderboardData>>(new List<LeaderboardData>());
    }

    public Task SaveScoresAsync(IEnumerable<LeaderboardData> scores)
    {
        // No-op: scores are managed by GlobalLeaderboardPersistence via leaderboard_entries
        return Task.CompletedTask;
    }

    private static DateTime ParseDate(NpgsqlDataReader r, int ord)
    {
        if (r.IsDBNull(ord)) return DateTime.MinValue;
        var s = r.GetString(ord);
        return DateTime.TryParse(s, out var dt) ? dt : DateTime.MinValue;
    }

    private static async Task RestoreLocalImagePathsAsync(
        NpgsqlConnection conn,
        IList<Song> songs)
    {
        if (songs.Count == 0)
            return;

        var songLookup = songs
            .Where(static song => song.track?.su is not null)
            .ToDictionary(
                static song => song.track.su,
                StringComparer.Ordinal);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT song_id, image_path
            FROM songs
            WHERE song_id = ANY(@songIds)
            """;
        cmd.Parameters.Add(
            "songIds",
            NpgsqlDbType.Array | NpgsqlDbType.Text).Value =
            songLookup.Keys.ToArray();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            if (songLookup.TryGetValue(
                    reader.GetString(0),
                    out var song))
            {
                song.imagePath =
                    reader.IsDBNull(1) ? null : reader.GetString(1);
            }
        }
    }

    private static async Task<long> NextCatalogVersionAsync(
        NpgsqlConnection conn,
        NpgsqlTransaction tx)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText =
            "SELECT nextval('song_catalog_version_seq')";
        return (long)(await cmd.ExecuteScalarAsync())!;
    }
}
