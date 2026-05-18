using ClosedXML.Excel;
using ClosedXML.Excel.Drawings;
using FSTService.Persistence;
using FSTService.Scraping;
using Npgsql;
using NpgsqlTypes;
using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace FSTService.Exports;

public sealed class PlayerDataExportService
{
    public const string ContentType = "application/zip";
    private const string XlsxContentType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";
    private const int HistoryDays = 3650;
    private const bool UseStarImagesInExport = false;
    private const string ScoredAtHeader = "Scored At";
    private static readonly Lazy<StarImageAssets?> StarImages = new(LoadStarImageAssets);
    private static readonly XNamespace RelationshipNamespace = "http://schemas.openxmlformats.org/package/2006/relationships";
    private readonly GlobalLeaderboardPersistence _persistence;
    private readonly IMetaDatabase _metaDb;
    private readonly NpgsqlDataSource _dataSource;

    public PlayerDataExportService(
        GlobalLeaderboardPersistence persistence,
        IMetaDatabase metaDb,
        NpgsqlDataSource dataSource)
    {
        _persistence = persistence;
        _metaDb = metaDb;
        _dataSource = dataSource;
    }

    public PlayerDataExportResult BuildPlayerArchive(string accountId, string? timeZoneId = null, bool usePublishedSnapshot = false)
    {
        if (string.IsNullOrWhiteSpace(accountId))
            throw new ArgumentException("Account ID is required.", nameof(accountId));

        var exportTimeZone = ResolveTimeZone(timeZoneId);
        var snapshot = LoadExportSnapshot(accountId.Trim(), usePublishedSnapshot);
        var generatedAt = DateTimeOffset.UtcNow;
        var timestamp = generatedAt.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var playerFilePart = SanitizeFilePart(snapshot.DisplayName ?? accountId);

        var zipContent = BuildZipArchive(snapshot, exportTimeZone, playerFilePart, timestamp);
        var fileName = $"{playerFilePart}-export-{timestamp}.zip";
        return new PlayerDataExportResult(zipContent, fileName, ContentType);
    }

    public PlayerDataExportResult BuildBandArchive(string bandType, string teamKey, string? timeZoneId = null, bool usePublishedSnapshot = false)
    {
        if (string.IsNullOrWhiteSpace(bandType))
            throw new ArgumentException("Band type is required.", nameof(bandType));
        if (string.IsNullOrWhiteSpace(teamKey))
            throw new ArgumentException("Team key is required.", nameof(teamKey));

        var exportTimeZone = ResolveTimeZone(timeZoneId);
        var snapshot = LoadBandExportSnapshot(bandType.Trim(), teamKey.Trim(), usePublishedSnapshot);
        var generatedAt = DateTimeOffset.UtcNow;
        var timestamp = generatedAt.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var bandFilePart = SanitizeFilePart(snapshot.DisplayName ?? "band");

        var zipContent = BuildBandZipArchive(snapshot, exportTimeZone, bandFilePart, timestamp);
        var fileName = $"{bandFilePart}-export-{timestamp}.zip";
        return new PlayerDataExportResult(zipContent, fileName, ContentType);
    }

    private PlayerExportSnapshot LoadExportSnapshot(string accountId, bool usePublishedSnapshot)
    {
        var displayName = _metaDb.GetDisplayName(accountId);
        var soloScores = usePublishedSnapshot
            ? LoadPublishedSoloScores(accountId)
            : _persistence.GetCurrentStatePlayerProfile(accountId).ToList();
        var soloScoreHistory = _metaDb.GetScoreHistory(accountId, int.MaxValue)
            .ToList();
        var soloRankHistory = LoadSoloRankHistory(accountId);
        var bands = usePublishedSnapshot
            ? LoadPublishedPlayerBands(accountId)
            : _persistence.GetPlayerBandsList(accountId, "all", page: 1, pageSize: null).Entries;
        var bandScores = usePublishedSnapshot ? LoadPublishedBandScores(accountId: accountId) : LoadBandScores(accountId);
        var bandMemberStats = usePublishedSnapshot ? LoadPublishedBandMemberStats(accountId: accountId) : LoadBandMemberStats(accountId);
        var bandRankHistory = LoadBandRankHistory(bands);
        var displayNames = _metaDb.GetDisplayNames(
            bands.SelectMany(static band => band.Members.Select(static member => member.AccountId))
                .Concat(bandScores.SelectMany(static score => score.TeamMembers))
                .Concat(bandMemberStats.Select(static member => member.AccountId))
                .Distinct(StringComparer.OrdinalIgnoreCase));

        var songIds = soloScores.Select(static score => score.SongId)
            .Concat(soloScoreHistory.Select(static history => history.SongId))
            .Concat(bandScores.Select(static score => score.SongId))
            .Concat(bandMemberStats.Select(static stat => stat.SongId))
            .Where(static songId => !string.IsNullOrWhiteSpace(songId))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var songs = LoadSongs(songIds).ToDictionary(static song => song.SongId, StringComparer.OrdinalIgnoreCase);

        return new PlayerExportSnapshot(
            accountId,
            displayName,
            soloScores,
            soloScoreHistory,
            soloRankHistory,
            bands,
            bandScores,
            bandMemberStats,
            bandRankHistory,
            displayNames,
            songs);
    }

    private PlayerExportSnapshot LoadBandExportSnapshot(string bandType, string teamKey, bool usePublishedSnapshot)
    {
        var bandId = BandIdentity.CreateBandId(bandType, teamKey);
        var band = usePublishedSnapshot
            ? LoadPublishedBand(bandType, teamKey)
            : _persistence.GetBandById(bandId);
        band ??= CreateBandEntryFromTeamKey(bandType, teamKey);
        var bands = new List<PlayerBandEntryDto> { band };
        var bandScores = usePublishedSnapshot ? LoadPublishedBandScores(bandType: bandType, teamKey: teamKey) : LoadBandScoresForTeam(bandType, teamKey);
        var bandMemberStats = usePublishedSnapshot ? LoadPublishedBandMemberStats(bandType: bandType, teamKey: teamKey) : LoadBandMemberStatsForTeam(bandType, teamKey);
        var bandRankHistory = LoadBandRankHistory(bands);
        var displayNames = _metaDb.GetDisplayNames(
            bands.SelectMany(static bandEntry => bandEntry.Members.Select(static member => member.AccountId))
                .Concat(bandScores.SelectMany(static score => score.TeamMembers))
                .Concat(bandMemberStats.Select(static member => member.AccountId))
                .Distinct(StringComparer.OrdinalIgnoreCase));

        var songIds = bandScores.Select(static score => score.SongId)
            .Concat(bandMemberStats.Select(static stat => stat.SongId))
            .Where(static songId => !string.IsNullOrWhiteSpace(songId))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var songs = LoadSongs(songIds).ToDictionary(static song => song.SongId, StringComparer.OrdinalIgnoreCase);

        return new PlayerExportSnapshot(
            null,
            FormatBandMembers(band.Members, null),
            [],
            [],
            [],
            bands,
            bandScores,
            bandMemberStats,
            bandRankHistory,
            displayNames,
            songs);
    }

    private static byte[] BuildZipArchive(
        PlayerExportSnapshot snapshot,
        TimeZoneInfo exportTimeZone,
        string playerFilePart,
        string timestamp)
    {
        using var zipStream = new MemoryStream();
        using (var archive = new ZipArchive(zipStream, ZipArchiveMode.Create, leaveOpen: true))
        {
            AddZipWorkbookEntry(archive, $"{playerFilePart}-all-{timestamp}.xlsx", () => BuildOverallWorkbook(snapshot, exportTimeZone));

            foreach (var instrument in SoloInstrumentsWithData(snapshot))
            {
                var instrumentScope = SanitizeFilePart(FriendlyInstrument(instrument)).ToLowerInvariant();
                AddZipWorkbookEntry(archive, $"{playerFilePart}-{instrumentScope}-{timestamp}.xlsx", () => BuildSoloInstrumentWorkbook(snapshot, instrument, exportTimeZone));
            }

            AddZipWorkbookEntry(archive, $"{playerFilePart}-bands-{timestamp}.xlsx", () => BuildBandsWorkbook(snapshot, exportTimeZone));
        }

        return zipStream.ToArray();
    }

    private static byte[] BuildBandZipArchive(
        PlayerExportSnapshot snapshot,
        TimeZoneInfo exportTimeZone,
        string bandFilePart,
        string timestamp)
    {
        using var zipStream = new MemoryStream();
        using (var archive = new ZipArchive(zipStream, ZipArchiveMode.Create, leaveOpen: true))
        {
            AddZipWorkbookEntry(archive, $"{bandFilePart}-bands-{timestamp}.xlsx", () => BuildBandsWorkbook(snapshot, exportTimeZone));
        }

        return zipStream.ToArray();
    }

    private static void AddZipWorkbookEntry(ZipArchive archive, string entryName, Func<byte[]> buildWorkbook)
    {
        var entry = archive.CreateEntry(entryName, CompressionLevel.NoCompression);
        using var entryStream = entry.Open();
        var workbookContent = buildWorkbook();
        entryStream.Write(workbookContent);
    }

    private static byte[] BuildOverallWorkbook(PlayerExportSnapshot snapshot, TimeZoneInfo exportTimeZone)
    {
        using var workbook = new XLWorkbook();
        AddSoloScores(workbook, snapshot.SoloScores, snapshot.Songs);
        AddSoloScoreHistory(workbook, snapshot.SoloScoreHistory, snapshot.Songs, exportTimeZone);
        AddSoloRankHistory(workbook, snapshot.SoloRankHistory, exportTimeZone);
        AddBands(workbook, snapshot.Bands, snapshot.AccountId);
        AddBandMembers(workbook, snapshot.Bands);
        AddBandScores(workbook, snapshot.BandScores, snapshot.Songs, snapshot.DisplayNames, snapshot.AccountId, exportTimeZone);
        AddBandMemberStats(workbook, snapshot.BandMemberStats, snapshot.Songs, snapshot.DisplayNames);
        AddBandRankHistory(workbook, snapshot.BandRankHistory, snapshot.AccountId, exportTimeZone);
        AddSongs(workbook, snapshot.Songs, exportTimeZone);
        return SaveWorkbook(workbook);
    }

    private static byte[] BuildSoloInstrumentWorkbook(PlayerExportSnapshot snapshot, string instrument, TimeZoneInfo exportTimeZone)
    {
        var soloScores = snapshot.SoloScores
            .Where(score => string.Equals(score.Instrument, instrument, StringComparison.OrdinalIgnoreCase))
            .ToList();
        var soloScoreHistory = snapshot.SoloScoreHistory
            .Where(history => string.Equals(history.Instrument, instrument, StringComparison.OrdinalIgnoreCase))
            .ToList();
        var soloRankHistory = snapshot.SoloRankHistory
            .Where(history => string.Equals(history.Instrument, instrument, StringComparison.OrdinalIgnoreCase))
            .ToList();

        var songs = FilterSongs(snapshot.Songs, soloScores.Select(static score => score.SongId)
            .Concat(soloScoreHistory.Select(static history => history.SongId)));

        using var workbook = new XLWorkbook();
        AddSoloScores(workbook, soloScores, songs);
        AddSoloScoreHistory(workbook, soloScoreHistory, songs, exportTimeZone);
        AddSoloRankHistory(workbook, soloRankHistory, exportTimeZone);
        AddSongs(workbook, songs, exportTimeZone);
        return SaveWorkbook(workbook);
    }

    private static byte[] BuildBandsWorkbook(PlayerExportSnapshot snapshot, TimeZoneInfo exportTimeZone)
    {
        var songs = FilterSongs(snapshot.Songs, snapshot.BandScores.Select(static score => score.SongId)
            .Concat(snapshot.BandMemberStats.Select(static stat => stat.SongId)));

        using var workbook = new XLWorkbook();
        AddBands(workbook, snapshot.Bands, snapshot.AccountId);
        AddBandMembers(workbook, snapshot.Bands);
        AddBandScores(workbook, snapshot.BandScores, songs, snapshot.DisplayNames, snapshot.AccountId, exportTimeZone);
        AddBandMemberStats(workbook, snapshot.BandMemberStats, songs, snapshot.DisplayNames);
        AddBandRankHistory(workbook, snapshot.BandRankHistory, snapshot.AccountId, exportTimeZone);
        AddSongs(workbook, songs, exportTimeZone);
        return SaveWorkbook(workbook);
    }

    private static byte[] SaveWorkbook(XLWorkbook workbook)
    {
        using var stream = new MemoryStream();
        workbook.SaveAs(stream);
        return DeduplicateXlsxMedia(stream.ToArray());
    }

    private static Dictionary<string, SongExportRow> FilterSongs(IReadOnlyDictionary<string, SongExportRow> songs, IEnumerable<string> songIds)
    {
        var filteredSongIds = songIds
            .Where(static songId => !string.IsNullOrWhiteSpace(songId))
            .Distinct(StringComparer.OrdinalIgnoreCase);

        return filteredSongIds
            .Where(songs.ContainsKey)
            .ToDictionary(songId => songId, songId => songs[songId], StringComparer.OrdinalIgnoreCase);
    }

    private static IReadOnlyList<string> SoloInstrumentsWithData(PlayerExportSnapshot snapshot)
    {
        return snapshot.SoloScores.Select(static score => score.Instrument)
            .Concat(snapshot.SoloScoreHistory.Select(static history => history.Instrument))
            .Concat(snapshot.SoloRankHistory.Select(static history => history.Instrument))
            .Where(static instrument => !string.IsNullOrWhiteSpace(instrument))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(InstrumentSortOrder)
            .ThenBy(FriendlyInstrument, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private List<SoloRankHistoryExportRow> LoadSoloRankHistory(string accountId)
    {
        var rows = new List<SoloRankHistoryExportRow>();
        foreach (var instrument in _persistence.GetInstrumentKeys())
        {
            var history = _persistence.GetOrCreateInstrumentDb(instrument).GetRankHistory(accountId, HistoryDays);
            rows.AddRange(history.Select(item => new SoloRankHistoryExportRow(instrument, item)));
        }

        return rows
            .OrderBy(static row => row.Instrument, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static row => row.History.SnapshotDate, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private List<BandRankHistoryExportRow> LoadBandRankHistory(IReadOnlyList<PlayerBandEntryDto> bands)
    {
        var rows = new List<BandRankHistoryExportRow>();
        foreach (var band in bands)
        {
            var history = _metaDb.GetBandRankHistory(band.BandType, band.TeamKey, comboId: null, days: HistoryDays);
            rows.AddRange(history.Select(item => new BandRankHistoryExportRow(band.BandId, band.BandType, band.TeamKey, band.Members, "overall", null, item)));
        }

        return rows
            .OrderBy(static row => row.BandType, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static row => row.TeamKey, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static row => row.History.SnapshotDate, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private List<SongExportRow> LoadSongs(IReadOnlyCollection<string> songIds)
    {
        if (songIds.Count == 0)
            return [];

        using var conn = _dataSource.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT song_id, title, artist, active_date, last_modified, release_year, tempo,
                   lead_diff, bass_diff, drums_diff, vocals_diff, plastic_guitar_diff, plastic_bass_diff,
                   plastic_drums_diff, pro_vocals_diff
            FROM songs
            WHERE song_id = ANY(@songIds)
            ORDER BY title NULLS LAST, artist NULLS LAST, song_id
            """;
        cmd.Parameters.Add("songIds", NpgsqlDbType.Array | NpgsqlDbType.Text).Value = songIds.ToArray();

        var rows = new List<SongExportRow>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            rows.Add(new SongExportRow(
                reader.GetString(0),
                GetNullableString(reader, 1),
                GetNullableString(reader, 2),
                GetNullableString(reader, 3),
                GetNullableString(reader, 4),
                GetNullableInt32(reader, 5),
                GetNullableInt32(reader, 6),
                GetNullableInt32(reader, 7),
                GetNullableInt32(reader, 8),
                GetNullableInt32(reader, 9),
                GetNullableInt32(reader, 10),
                GetNullableInt32(reader, 11),
                GetNullableInt32(reader, 12),
                GetNullableInt32(reader, 13),
                GetNullableInt32(reader, 14)));
        }

        return rows;
    }

    private List<PlayerScoreDto> LoadPublishedSoloScores(string accountId)
    {
        using var conn = _dataSource.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            WITH base_rows AS (
                SELECT snapshot.song_id, snapshot.instrument, snapshot.account_id, snapshot.score, snapshot.accuracy,
                       snapshot.is_full_combo, snapshot.stars, snapshot.season, snapshot.difficulty, snapshot.percentile::DOUBLE PRECISION,
                       snapshot.end_time, snapshot.rank, snapshot.api_rank, 1 AS origin_precedence, 0 AS source_priority
                FROM leaderboard_entries_snapshot snapshot
                JOIN leaderboard_snapshot_state state
                  ON state.song_id = snapshot.song_id
                 AND state.instrument = snapshot.instrument
                 AND state.active_snapshot_id = snapshot.snapshot_id
                 AND state.is_finalized = TRUE
                WHERE snapshot.account_id = @accountId
                UNION ALL
                SELECT overlay.song_id, overlay.instrument, overlay.account_id, overlay.score, overlay.accuracy,
                       overlay.is_full_combo, overlay.stars, overlay.season, overlay.difficulty, overlay.percentile::DOUBLE PRECISION,
                       overlay.end_time, overlay.rank, overlay.api_rank, 0 AS origin_precedence, overlay.source_priority
                FROM leaderboard_entries_overlay overlay
                WHERE overlay.account_id = @accountId
            ), resolved_rows AS (
                SELECT DISTINCT ON (song_id, instrument, account_id)
                       song_id, instrument, account_id, score, accuracy, is_full_combo, stars, season, difficulty,
                       percentile, end_time, rank, api_rank
                FROM base_rows
                ORDER BY song_id, instrument, account_id, origin_precedence ASC, source_priority DESC
            )
            SELECT song_id, instrument, score, accuracy, is_full_combo, stars, season, difficulty,
                   percentile, end_time, rank, api_rank
            FROM resolved_rows
            ORDER BY instrument, song_id
            """;
        cmd.CommandTimeout = 0;
        cmd.Parameters.AddWithValue("accountId", accountId);

        var rows = new List<PlayerScoreDto>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            rows.Add(new PlayerScoreDto
            {
                SongId = reader.GetString(0),
                Instrument = reader.GetString(1),
                Score = reader.GetInt32(2),
                Accuracy = reader.IsDBNull(3) ? 0 : reader.GetInt32(3),
                IsFullCombo = !reader.IsDBNull(4) && reader.GetBoolean(4),
                Stars = reader.IsDBNull(5) ? 0 : reader.GetInt32(5),
                Season = reader.IsDBNull(6) ? 0 : reader.GetInt32(6),
                Difficulty = reader.IsDBNull(7) ? 0 : reader.GetInt32(7),
                Percentile = reader.IsDBNull(8) ? 0 : reader.GetDouble(8),
                EndTime = reader.IsDBNull(9) ? null : reader.GetString(9),
                Rank = reader.IsDBNull(10) ? 0 : reader.GetInt32(10),
                ApiRank = reader.IsDBNull(11) ? 0 : reader.GetInt32(11),
            });
        }

        return rows;
    }

    private List<PlayerBandEntryDto> LoadPublishedPlayerBands(string accountId)
    {
        var rows = LoadPublishedBandProjectionRows(accountId, bandType: null, teamKey: null);
        return rows.GroupBy(static row => (row.BandType, row.TeamKey))
            .Select(group => BuildBandEntryFromPublishedRows(group.Key.BandType, group.Key.TeamKey, group.ToList()))
            .OrderBy(static band => band.BandType, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static band => band.TeamKey, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private PlayerBandEntryDto? LoadPublishedBand(string bandType, string teamKey)
    {
        var rows = LoadPublishedBandProjectionRows(accountId: null, bandType, teamKey);
        return rows.Count == 0 ? null : BuildBandEntryFromPublishedRows(bandType, teamKey, rows);
    }

    private PlayerBandEntryDto BuildBandEntryFromPublishedRows(string bandType, string teamKey, IReadOnlyList<PublishedBandProjectionRow> rows)
    {
        var memberIds = rows.SelectMany(static row => row.TeamMembers)
            .Concat(rows.SelectMany(static row => row.MemberAccountIds))
            .Where(static accountId => !string.IsNullOrWhiteSpace(accountId))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(static accountId => accountId, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var displayNames = _metaDb.GetDisplayNames(memberIds);
        var instrumentsByAccount = new Dictionary<string, SortedSet<string>>(StringComparer.OrdinalIgnoreCase);

        foreach (var row in rows)
        {
            for (var index = 0; index < row.MemberAccountIds.Length; index++)
            {
                var accountId = row.MemberAccountIds[index];
                var instrumentId = NullableArrayValue(row.MemberInstrumentIds, index);
                if (instrumentId is null)
                    continue;

                if (!instrumentsByAccount.TryGetValue(accountId, out var instruments))
                {
                    instruments = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
                    instrumentsByAccount[accountId] = instruments;
                }

                var instrument = BandInstrumentMapping.ToLeaderboardType(instrumentId.Value);
                if (!string.IsNullOrWhiteSpace(instrument))
                    instruments.Add(instrument);
            }
        }

        return new PlayerBandEntryDto
        {
            BandId = BandIdentity.CreateBandId(bandType, teamKey),
            TeamKey = teamKey,
            BandType = bandType,
            AppearanceCount = rows.Select(static row => (row.SongId, row.ComboId)).Distinct().Count(),
            Members = memberIds.Select(accountId => new PlayerBandMemberDto
            {
                AccountId = accountId,
                DisplayName = displayNames.GetValueOrDefault(accountId),
                Instruments = instrumentsByAccount.TryGetValue(accountId, out var instruments) ? instruments.ToList() : [],
            }).ToList(),
        };
    }

    private PlayerBandEntryDto CreateBandEntryFromTeamKey(string bandType, string teamKey)
    {
        var memberIds = teamKey.Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var displayNames = _metaDb.GetDisplayNames(memberIds);

        return new PlayerBandEntryDto
        {
            BandId = BandIdentity.CreateBandId(bandType, teamKey),
            TeamKey = teamKey,
            BandType = bandType,
            AppearanceCount = 0,
            Members = memberIds.Select(accountId => new PlayerBandMemberDto
            {
                AccountId = accountId,
                DisplayName = displayNames.GetValueOrDefault(accountId),
            }).ToList(),
        };
    }

    private List<BandScoreExportRow> LoadPublishedBandScores(string? accountId = null, string? bandType = null, string? teamKey = null)
    {
        return LoadPublishedBandProjectionRows(accountId, bandType, teamKey)
            .Select(static row => new BandScoreExportRow(
                row.SongId,
                row.BandType,
                row.TeamKey,
                row.InstrumentCombo,
                row.ComboId,
                row.TeamMembers,
                row.Score,
                null,
                null,
                null,
                row.Accuracy,
                row.IsFullCombo,
                row.Stars,
                row.Difficulty,
                row.Season,
                row.Rank,
                row.Percentile,
                row.EndTime,
                row.FirstSeenAt.ToString("o")))
            .ToList();
    }

    private List<BandMemberStatExportRow> LoadPublishedBandMemberStats(string? accountId = null, string? bandType = null, string? teamKey = null)
    {
        var exportRows = new List<BandMemberStatExportRow>();
        foreach (var row in LoadPublishedBandProjectionRows(accountId, bandType, teamKey))
        {
            var memberAccountIds = row.MemberAccountIds.Length > 0 ? row.MemberAccountIds : row.TeamMembers;
            for (var index = 0; index < memberAccountIds.Length; index++)
            {
                var instrumentId = NullableArrayValue(row.MemberInstrumentIds, index);
                exportRows.Add(new BandMemberStatExportRow(
                    row.SongId,
                    row.BandType,
                    row.TeamKey,
                    row.InstrumentCombo,
                    row.ComboId,
                    index,
                    memberAccountIds[index],
                    instrumentId,
                    instrumentId is null ? null : BandInstrumentMapping.ToLeaderboardType(instrumentId.Value),
                    NullableArrayValue(row.MemberScores, index),
                    NullableArrayValue(row.MemberAccuracies, index),
                    NullableBoolArrayValue(row.MemberFullCombos, index),
                    NullableArrayValue(row.MemberStars, index),
                    NullableArrayValue(row.MemberDifficulties, index),
                    row.Season));
            }
        }

        return exportRows;
    }

    private List<PublishedBandProjectionRow> LoadPublishedBandProjectionRows(string? accountId, string? bandType, string? teamKey)
    {
        using var conn = _dataSource.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT DISTINCT ON (cble.song_id, cble.band_type, cble.team_key, cble.entry_instrument_combo)
                   cble.song_id, cble.band_type, cble.team_key, cble.entry_instrument_combo, cble.entry_combo_id,
                   cble.team_members, cble.member_account_ids, cble.member_instrument_ids, cble.member_scores,
                   cble.member_accuracies, cble.member_full_combos, cble.member_stars, cble.member_difficulties,
                   cble.score, cble.accuracy, cble.is_full_combo, cble.stars, cble.difficulty, cble.season,
                   cble.rank, cble.percentile, cble.end_time, cble.first_seen_at
            FROM current_band_leaderboard_entries cble
            JOIN band_current_projection_scope scope
              ON scope.song_id = cble.song_id
             AND scope.band_type = cble.band_type
             AND scope.ranking_scope = cble.ranking_scope
             AND scope.scope_combo_id = cble.scope_combo_id
             AND scope.published_generation = cble.projection_generation
            WHERE (@accountId IS NULL OR @accountId = ANY(cble.team_members))
              AND (@bandType IS NULL OR cble.band_type = @bandType)
              AND (@teamKey IS NULL OR cble.team_key = @teamKey)
            ORDER BY cble.song_id, cble.band_type, cble.team_key, cble.entry_instrument_combo,
                     CASE cble.ranking_scope WHEN 'overall' THEN 0 ELSE 1 END,
                     cble.rank ASC
            """;
        cmd.CommandTimeout = 0;
        cmd.Parameters.Add("accountId", NpgsqlDbType.Text).Value = accountId is null ? DBNull.Value : accountId;
        cmd.Parameters.Add("bandType", NpgsqlDbType.Text).Value = bandType is null ? DBNull.Value : bandType;
        cmd.Parameters.Add("teamKey", NpgsqlDbType.Text).Value = teamKey is null ? DBNull.Value : teamKey;

        var rows = new List<PublishedBandProjectionRow>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var instrumentCombo = reader.GetString(3);
            var entryComboId = reader.GetString(4);
            var comboId = string.IsNullOrWhiteSpace(entryComboId) ? BandComboIds.FromEpicRawCombo(instrumentCombo) : entryComboId;
            rows.Add(new PublishedBandProjectionRow(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                instrumentCombo,
                comboId,
                reader.GetFieldValue<string[]>(5),
                reader.GetFieldValue<string[]>(6),
                reader.GetFieldValue<int[]>(7),
                reader.GetFieldValue<int[]>(8),
                reader.GetFieldValue<int[]>(9),
                reader.GetFieldValue<int[]>(10),
                reader.GetFieldValue<int[]>(11),
                reader.GetFieldValue<int[]>(12),
                reader.GetInt32(13),
                GetNullableInt32(reader, 14),
                GetNullableBoolean(reader, 15),
                GetNullableInt32(reader, 16),
                GetNullableInt32(reader, 17),
                GetNullableInt32(reader, 18),
                GetNullableInt32(reader, 19),
                GetNullableDouble(reader, 20),
                GetNullableString(reader, 21),
                reader.GetFieldValue<DateTime>(22)));
        }

        return rows;
    }

    private List<BandScoreExportRow> LoadBandScores(string accountId)
    {
        using var conn = _dataSource.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT DISTINCT be.song_id, be.band_type, be.team_key, be.instrument_combo, be.team_members,
                   be.score, be.base_score, be.instrument_bonus, be.overdrive_bonus, be.accuracy,
                   be.is_full_combo, be.stars, be.difficulty, be.season, be.rank, be.percentile,
                     be.end_time, be.first_seen_at
            FROM band_entries be
            JOIN band_members bm
              ON bm.song_id = be.song_id
             AND bm.band_type = be.band_type
             AND bm.team_key = be.team_key
             AND bm.instrument_combo = be.instrument_combo
            WHERE bm.account_id = @accountId
            ORDER BY be.band_type, be.team_key, be.instrument_combo, be.song_id
            """;
        cmd.CommandTimeout = 0;
        cmd.Parameters.AddWithValue("accountId", accountId);

        return ReadBandScoreRows(cmd);
    }

    private List<BandScoreExportRow> LoadBandScoresForTeam(string bandType, string teamKey)
    {
        using var conn = _dataSource.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT DISTINCT be.song_id, be.band_type, be.team_key, be.instrument_combo, be.team_members,
                   be.score, be.base_score, be.instrument_bonus, be.overdrive_bonus, be.accuracy,
                   be.is_full_combo, be.stars, be.difficulty, be.season, be.rank, be.percentile,
                   be.end_time, be.first_seen_at
            FROM band_entries be
            WHERE be.band_type = @bandType AND be.team_key = @teamKey
            ORDER BY be.instrument_combo, be.song_id
            """;
        cmd.CommandTimeout = 0;
        cmd.Parameters.AddWithValue("bandType", bandType);
        cmd.Parameters.AddWithValue("teamKey", teamKey);

        return ReadBandScoreRows(cmd);
    }

    private static List<BandScoreExportRow> ReadBandScoreRows(NpgsqlCommand cmd)
    {
        var rows = new List<BandScoreExportRow>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var rawCombo = reader.GetString(3);
            rows.Add(new BandScoreExportRow(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                rawCombo,
                BandComboIds.FromEpicRawCombo(rawCombo),
                reader.GetFieldValue<string[]>(4),
                reader.GetInt32(5),
                GetNullableInt32(reader, 6),
                GetNullableInt32(reader, 7),
                GetNullableInt32(reader, 8),
                GetNullableInt32(reader, 9),
                GetNullableBoolean(reader, 10),
                GetNullableInt32(reader, 11),
                GetNullableInt32(reader, 12),
                GetNullableInt32(reader, 13),
                GetNullableInt32(reader, 14),
                GetNullableDouble(reader, 15),
                GetNullableString(reader, 16),
                reader.GetFieldValue<DateTime>(17).ToString("o")));
        }

        return rows;
    }

    private List<BandMemberStatExportRow> LoadBandMemberStats(string accountId)
    {
        using var conn = _dataSource.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT DISTINCT bms.song_id, bms.band_type, bms.team_key, bms.instrument_combo,
                   bms.member_index, bms.account_id, bms.instrument_id, bms.score, bms.accuracy,
                   bms.is_full_combo, bms.stars, bms.difficulty, be.season
            FROM band_member_stats bms
            JOIN band_members bm
              ON bm.song_id = bms.song_id
             AND bm.band_type = bms.band_type
             AND bm.team_key = bms.team_key
             AND bm.instrument_combo = bms.instrument_combo
            LEFT JOIN band_entries be
              ON be.song_id = bms.song_id
             AND be.band_type = bms.band_type
             AND be.team_key = bms.team_key
             AND be.instrument_combo = bms.instrument_combo
            WHERE bm.account_id = @accountId
            ORDER BY bms.band_type, bms.team_key, bms.instrument_combo, bms.song_id, bms.member_index
            """;
        cmd.CommandTimeout = 0;
        cmd.Parameters.AddWithValue("accountId", accountId);

        return ReadBandMemberStatRows(cmd);
    }

    private List<BandMemberStatExportRow> LoadBandMemberStatsForTeam(string bandType, string teamKey)
    {
        using var conn = _dataSource.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT DISTINCT bms.song_id, bms.band_type, bms.team_key, bms.instrument_combo,
                   bms.member_index, bms.account_id, bms.instrument_id, bms.score, bms.accuracy,
                   bms.is_full_combo, bms.stars, bms.difficulty, be.season
            FROM band_member_stats bms
            LEFT JOIN band_entries be
              ON be.song_id = bms.song_id
             AND be.band_type = bms.band_type
             AND be.team_key = bms.team_key
             AND be.instrument_combo = bms.instrument_combo
            WHERE bms.band_type = @bandType AND bms.team_key = @teamKey
            ORDER BY bms.instrument_combo, bms.song_id, bms.member_index
            """;
        cmd.CommandTimeout = 0;
        cmd.Parameters.AddWithValue("bandType", bandType);
        cmd.Parameters.AddWithValue("teamKey", teamKey);

        return ReadBandMemberStatRows(cmd);
    }

    private static List<BandMemberStatExportRow> ReadBandMemberStatRows(NpgsqlCommand cmd)
    {
        var rows = new List<BandMemberStatExportRow>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var rawCombo = reader.GetString(3);
            var instrumentId = GetNullableInt32(reader, 6);
            rows.Add(new BandMemberStatExportRow(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                rawCombo,
                BandComboIds.FromEpicRawCombo(rawCombo),
                reader.GetInt32(4),
                reader.GetString(5),
                instrumentId,
                instrumentId is null ? null : BandInstrumentMapping.ToLeaderboardType(instrumentId.Value),
                GetNullableInt32(reader, 7),
                GetNullableInt32(reader, 8),
                GetNullableBoolean(reader, 9),
                GetNullableInt32(reader, 10),
                GetNullableInt32(reader, 11),
                GetNullableInt32(reader, 12)));
        }

        return rows;
    }

    private static void AddSoloScores(XLWorkbook workbook, IReadOnlyList<PlayerScoreDto> scores, IReadOnlyDictionary<string, SongExportRow> songs)
    {
        AddSheet(workbook, "Solo Scores", "SoloScores", [
            "Title", "Artist", "Instrument", "Season", "Score", "Accuracy", "Full Combo", "Stars", "Difficulty", "Rank"
        ], scores
            .OrderBy(score => SortTitle(score.SongId, songs), StringComparer.OrdinalIgnoreCase)
            .ThenBy(score => songs.GetValueOrDefault(score.SongId)?.Artist ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            .ThenBy(score => InstrumentSortOrder(score.Instrument))
            .ThenBy(static score => score.Season)
            .Select(score => (IReadOnlyList<object?>)[
            songs.GetValueOrDefault(score.SongId)?.Title,
            songs.GetValueOrDefault(score.SongId)?.Artist,
            FriendlyInstrument(score.Instrument),
            score.Season,
            ScoreValue(score.Score),
            AccuracyValue(score.Accuracy),
            FullComboDisplay(score.IsFullCombo),
            StarDisplayCell(score.Stars),
            DifficultyName(score.Difficulty),
            RankValue(score.ApiRank),
        ]));
    }

    private static void AddSoloScoreHistory(
        XLWorkbook workbook,
        IReadOnlyList<ScoreHistoryEntry> history,
        IReadOnlyDictionary<string, SongExportRow> songs,
        TimeZoneInfo exportTimeZone)
    {
        AddSheet(workbook, "Solo Score History", "SoloScoreHistory", [
            "Title", "Artist", "Instrument", "Season", "Old Score", "New Score", "Old Rank", "New Rank",
            "Season Rank", "All Time Rank", "Accuracy", "Full Combo", "Stars", "Difficulty", ScoredAtHeader
        ], history
            .OrderBy(row => SortTitle(row.SongId, songs), StringComparer.OrdinalIgnoreCase)
            .ThenBy(row => songs.GetValueOrDefault(row.SongId)?.Artist ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            .ThenBy(row => InstrumentSortOrder(row.Instrument))
            .ThenBy(row => row.ScoreAchievedAt ?? row.ChangedAt, StringComparer.OrdinalIgnoreCase)
            .Select(row => (IReadOnlyList<object?>)[
            songs.GetValueOrDefault(row.SongId)?.Title,
            songs.GetValueOrDefault(row.SongId)?.Artist,
            FriendlyInstrument(row.Instrument),
            row.Season,
            ScoreValueOrZero(row.OldScore),
            ScoreValue(row.NewScore),
            RankValueOrNA(row.OldRank),
            RankValue(row.NewRank),
            RankValue(row.SeasonRank),
            RankValueOrNA(row.AllTimeRank),
            AccuracyValue(row.Accuracy),
            FullComboDisplay(row.IsFullCombo),
            StarDisplayCell(row.Stars),
            DifficultyName(row.Difficulty),
            DateTimeValue(row.ScoreAchievedAt, exportTimeZone),
        ]));
    }

    private static void AddSoloRankHistory(
        XLWorkbook workbook,
        IReadOnlyList<SoloRankHistoryExportRow> history,
        TimeZoneInfo exportTimeZone)
    {
        foreach (var group in history
            .GroupBy(static row => row.Instrument, StringComparer.OrdinalIgnoreCase)
            .OrderBy(group => InstrumentSortOrder(group.Key))
            .ThenBy(group => FriendlyInstrument(group.Key), StringComparer.OrdinalIgnoreCase))
        {
            var instrumentLabel = FriendlyInstrument(group.Key);
            AddSheet(workbook, $"Rank - {instrumentLabel}", $"RankHistory{SafeTableSuffix(instrumentLabel)}", [
                "Snapshot Date", "Snapshot Taken At", "Adjusted Skill Rank", "Weighted Rank", "FC Rate Rank",
                "Total Score Rank", "Max Score Percent Rank", "Adjusted Skill Rating", "Weighted Rating", "FC Rate", "Total Score",
                "Max Score Percent", "Songs Played", "Coverage", "Full Combo Count", "Total Charted Songs", "Ranked Account Count",
                "Raw Max Score Percent", "Raw Weighted Rating", "Raw Skill Rating"
            ], group.OrderByDescending(static row => row.History.SnapshotDate, StringComparer.OrdinalIgnoreCase)
                .ThenByDescending(static row => row.History.SnapshotTakenAt, StringComparer.OrdinalIgnoreCase)
                .Select(row => (IReadOnlyList<object?>)[
                row.History.SnapshotDate,
                DateTimeValue(row.History.SnapshotTakenAt, exportTimeZone),
                RankValue(row.History.AdjustedSkillRank),
                RankValue(row.History.WeightedRank),
                RankValue(row.History.FcRateRank),
                RankValue(row.History.TotalScoreRank),
                RankValue(row.History.MaxScorePercentRank),
                NumberValue(row.History.AdjustedSkillRating),
                NumberValue(row.History.WeightedRating),
                PercentValue(row.History.FcRate),
                ScoreValue(row.History.TotalScore),
                PercentValue(row.History.MaxScorePercent),
                CountValue(row.History.SongsPlayed),
                PercentValue(row.History.Coverage),
                CountValue(row.History.FullComboCount),
                CountValue(row.History.TotalChartedSongs),
                CountValue(row.History.RankedAccountCount),
                PercentValue(row.History.RawMaxScorePercent),
                NumberValue(row.History.RawWeightedRating),
                NumberValue(row.History.RawSkillRating),
            ]));
        }
    }

    private static void AddBands(XLWorkbook workbook, IReadOnlyList<PlayerBandEntryDto> bands, string? selectedAccountId)
    {
        AddSheet(workbook, "Bands", "Bands", [
            "Band Type", "Appearance Count", "Members"
        ], bands.Select(band => (IReadOnlyList<object?>)[
            FriendlyBandType(band.BandType),
            CountValue(band.AppearanceCount),
            FormatBandMembers(band.Members, selectedAccountId),
        ]));
    }

    private static void AddBandMembers(XLWorkbook workbook, IReadOnlyList<PlayerBandEntryDto> bands)
    {
        AddSheet(workbook, "Band Members", "BandMembers", [
            "Band Type", "Display Name", "Instruments Played in Band"
        ], bands.SelectMany(static band => band.Members.Select(member => (IReadOnlyList<object?>)[
            FriendlyBandType(band.BandType),
            DisplayNameOrNA(member.DisplayName),
            FriendlyInstrumentList(member.Instruments),
        ])));
    }

    private static void AddBandScores(
        XLWorkbook workbook,
        IReadOnlyList<BandScoreExportRow> scores,
        IReadOnlyDictionary<string, SongExportRow> songs,
        IReadOnlyDictionary<string, string> displayNames,
        string? selectedAccountId,
        TimeZoneInfo exportTimeZone)
    {
        AddSheet(workbook, "Band Scores", "BandScores", [
            "Title", "Artist", "Band Type", "Instruments", "Team Members",
            "Score", "Base Score", "Instrument Bonus", "Overdrive Bonus", "Accuracy", "Full Combo", "Stars",
            "Difficulty", "Season", "Rank", "Percentile", ScoredAtHeader
        ], scores.Select(score => (IReadOnlyList<object?>)[
            songs.GetValueOrDefault(score.SongId)?.Title,
            songs.GetValueOrDefault(score.SongId)?.Artist,
            FriendlyBandType(score.BandType),
            FriendlyCombo(score.ComboId),
            FormatTeamMembers(score.TeamMembers, displayNames, selectedAccountId),
            ScoreValue(score.Score),
            ScoreValue(score.BaseScore),
            ScoreValue(score.InstrumentBonus),
            ScoreValue(score.OverdriveBonus),
            AccuracyValue(score.Accuracy),
            FullComboDisplay(score.IsFullCombo),
            StarDisplayCell(score.Stars),
            DifficultyName(score.Difficulty),
            score.Season,
            RankValue(score.Rank),
            PercentValue(score.Percentile),
            DateTimeValue(FirstNonBlank(score.EndTime, score.FirstSeenAt), exportTimeZone),
        ]));
    }

    private static void AddBandMemberStats(
        XLWorkbook workbook,
        IReadOnlyList<BandMemberStatExportRow> stats,
        IReadOnlyDictionary<string, SongExportRow> songs,
        IReadOnlyDictionary<string, string> displayNames)
    {
        AddSheet(workbook, "Band Member Stats", "BandMemberStats", [
            "Title", "Artist", "Band Type", "Instruments", "Member Slot", "Display Name", "Instrument", "Score", "Accuracy", "Full Combo", "Stars",
            "Difficulty", "Season"
        ], stats.Select(stat => (IReadOnlyList<object?>)[
            songs.GetValueOrDefault(stat.SongId)?.Title,
            songs.GetValueOrDefault(stat.SongId)?.Artist,
            FriendlyBandType(stat.BandType),
            FriendlyCombo(stat.ComboId),
            CountValue(stat.MemberIndex + 1),
            DisplayNameOrAccountIdOrNA(stat.AccountId, displayNames),
            FriendlyInstrument(stat.Instrument),
            ScoreValue(stat.Score),
            AccuracyValue(stat.Accuracy),
            FullComboDisplay(stat.IsFullCombo),
            StarDisplayCell(stat.Stars),
            DifficultyName(stat.Difficulty),
            stat.Season,
        ]));
    }

    private static void AddBandRankHistory(
        XLWorkbook workbook,
        IReadOnlyList<BandRankHistoryExportRow> history,
        string? selectedAccountId,
        TimeZoneInfo exportTimeZone)
    {
        AddSheet(workbook, "Band Rank History", "BandRankHistory", [
            "Band Type", "Members", "Scope", "Instruments", "Snapshot Date", "Snapshot Taken At",
            "Adjusted Skill Rank", "Weighted Rank", "FC Rate Rank", "Total Score Rank", "Adjusted Skill Rating", "Weighted Rating",
            "FC Rate", "Total Score", "Songs Played", "Coverage", "Full Combo Count", "Total Charted Songs", "Total Ranked Teams",
            "Raw Weighted Rating", "Raw Skill Rating"
        ], history.Select(row => (IReadOnlyList<object?>)[
            FriendlyBandType(row.BandType),
            FormatBandMembers(row.Members, selectedAccountId),
            FriendlyScope(row.Scope),
            FriendlyComboOrNA(row.ComboId),
            row.History.SnapshotDate,
            DateTimeValue(row.History.SnapshotTakenAt, exportTimeZone),
            RankValue(row.History.AdjustedSkillRank),
            RankValue(row.History.WeightedRank),
            RankValue(row.History.FcRateRank),
            RankValue(row.History.TotalScoreRank),
            NumberValue(row.History.AdjustedSkillRating),
            NumberValue(row.History.WeightedRating),
            PercentValue(row.History.FcRate),
            ScoreValue(row.History.TotalScore),
            CountValue(row.History.SongsPlayed),
            PercentValue(row.History.Coverage),
            CountValue(row.History.FullComboCount),
            CountValue(row.History.TotalChartedSongs),
            CountValue(row.History.TotalRankedTeams),
            NumberValue(row.History.RawWeightedRating),
            NumberValue(row.History.RawSkillRating),
        ]));
    }

    private static void AddSongs(XLWorkbook workbook, IReadOnlyDictionary<string, SongExportRow> songs, TimeZoneInfo exportTimeZone)
    {
        AddSheet(workbook, "Songs", "Songs", [
            "Song ID", "Title", "Artist", "Active Date", "Last Modified", "Release Year", "Tempo", "Lead Difficulty",
            "Bass Difficulty", "Drums Difficulty", "Tap Vocals Difficulty", "Pro Lead Difficulty", "Pro Bass Difficulty",
            "Pro Drums Difficulty", "Karaoke Difficulty"
        ], songs.Values
            .OrderBy(static song => song.Title ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static song => song.Artist ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            .Select(song => (IReadOnlyList<object?>)[
            song.SongId,
            song.Title,
            song.Artist,
            DateTimeValue(song.ActiveDate, exportTimeZone),
            DateTimeValue(song.LastModified, exportTimeZone),
            CountValue(song.ReleaseYear),
            CountValue(song.Tempo),
            SongDifficultyValue(song.LeadDifficulty),
            SongDifficultyValue(song.BassDifficulty),
            SongDifficultyValue(song.DrumsDifficulty),
            SongDifficultyValue(song.VocalsDifficulty),
            SongDifficultyValue(song.ProLeadDifficulty),
            SongDifficultyValue(song.ProBassDifficulty),
            SongDifficultyValue(song.ProDrumsDifficulty),
            SongDifficultyValue(song.KaraokeDifficulty),
        ]));
    }

    private static void AddSheet(XLWorkbook workbook, string sheetName, string tableName, IReadOnlyList<string> headers, IEnumerable<IReadOnlyList<object?>> rows)
    {
        var worksheet = workbook.Worksheets.Add(sheetName);
        WriteTable(worksheet, 1, tableName, headers, rows);
        worksheet.SheetView.FreezeRows(1);
        AutoSizeColumns(worksheet, headers.Count);
    }

    private static void WriteTable(IXLWorksheet worksheet, int startRow, string tableName, IReadOnlyList<string> headers, IEnumerable<IReadOnlyList<object?>> rows)
    {
        for (var column = 0; column < headers.Count; column++)
        {
            var cell = worksheet.Cell(startRow, column + 1);
            cell.SetValue(headers[column]);
            cell.Style.Font.Bold = true;
        }

        var rowNumber = startRow + 1;
        foreach (var row in rows)
        {
            for (var column = 0; column < headers.Count; column++)
                SetCellValue(worksheet.Cell(rowNumber, column + 1), column < row.Count ? row[column] : null);
            rowNumber++;
        }

        var lastRow = Math.Max(startRow + 1, rowNumber - 1);
        if (rowNumber == startRow + 1)
        {
            for (var column = 0; column < headers.Count; column++)
                worksheet.Cell(lastRow, column + 1).SetValue(string.Empty);
        }

        var table = worksheet.Range(startRow, 1, lastRow, headers.Count).CreateTable(tableName);
        table.Theme = XLTableTheme.TableStyleMedium2;
    }

    private static void SetCellValue(IXLCell cell, object? value)
    {
        switch (value)
        {
            case null:
                return;
            case ExcelIntegerCell formatted:
                cell.SetValue(formatted.Value);
                cell.Style.NumberFormat.Format = formatted.NumberFormat;
                break;
            case ExcelNumberCell formatted:
                cell.SetValue(formatted.Value);
                cell.Style.NumberFormat.Format = formatted.NumberFormat;
                break;
            case ExcelDateTimeCell formatted:
                cell.SetValue(formatted.Value);
                cell.Style.DateFormat.Format = formatted.NumberFormat;
                break;
            case ExcelStarCell stars:
                if (!TryWriteStarImages(cell, stars.Stars))
                    SetStyledStarText(cell, stars.Stars);
                break;
            case ExcelStyledTextCell styled:
                cell.SetValue(styled.Text);
                cell.Style.NumberFormat.Format = "@";
                cell.Style.Font.FontColor = XLColor.FromHtml(styled.FontColor);
                cell.Style.Font.Bold = styled.Bold;
                cell.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
                break;
            case string text:
                cell.SetValue(text);
                cell.Style.NumberFormat.Format = "@";
                break;
            case bool boolean:
                cell.SetValue(boolean);
                break;
            case int integer:
                cell.SetValue(integer);
                break;
            case long longValue:
                cell.SetValue(longValue);
                break;
            case double doubleValue:
                cell.SetValue(doubleValue);
                break;
            case float floatValue:
                cell.SetValue(floatValue);
                break;
            default:
                cell.SetValue(value.ToString() ?? string.Empty);
                cell.Style.NumberFormat.Format = "@";
                break;
        }
    }

    private static object? ScoreValue(int? score) => score is null ? null : ScoreValue(score.Value);

    private static object? ScoreValue(long? score) => score is null ? null : ScoreValue(score.Value);

    private static ExcelIntegerCell ScoreValue(int score) => ScoreValue((long)score);

    private static ExcelIntegerCell ScoreValue(long score) => new(score, "#,##0");

    private static ExcelIntegerCell ScoreValueOrZero(int? score) => ScoreValue(score ?? 0);

    private static object? CountValue(int? value) => value is null ? null : CountValue(value.Value);

    private static ExcelIntegerCell CountValue(int value) => new(value, "#,##0");

    private static object? NumberValue(double? value)
    {
        if (value is null || double.IsNaN(value.Value) || double.IsInfinity(value.Value))
            return null;

        var format = Math.Abs(value.Value - Math.Round(value.Value)) < 0.000_001d
            ? "#,##0"
            : "#,##0.###";
        return new ExcelNumberCell(value.Value, format);
    }

    private static object? RankValue(int? rank) => rank is > 0 ? ScoreValue(rank.Value) : null;

    private static object RankValueOrNA(int? rank) => RankValue(rank) ?? "N/A";

    private static object? AccuracyValue(int? accuracy)
    {
        if (accuracy is null)
            return null;

        var percentage = Math.Truncate((accuracy.Value / 10_000d) * 10d) / 10d;
        return PercentValue(percentage);
    }

    private static object? PercentValue(double? value)
    {
        if (value is null || double.IsNaN(value.Value) || double.IsInfinity(value.Value))
            return null;

        if (value.Value < 0d)
            return "N/A";

        var normalized = NormalizePercent(value.Value);
        var displayedPercent = normalized * 100d;
        var format = Math.Abs(displayedPercent - Math.Round(displayedPercent)) < 0.000_001d
            ? "0%"
            : "0.0%";
        return new ExcelNumberCell(normalized, format);
    }

    private static double NormalizePercent(double value)
    {
        if (Math.Abs(value) > 1d && Math.Abs(value) <= 100d)
            return value / 100d;

        return value;
    }

    private static void AutoSizeColumns(IXLWorksheet worksheet, int columnCount)
    {
        var minimumWidths = Enumerable.Range(1, columnCount)
            .Select(column => worksheet.Column(column).Width)
            .ToArray();

        worksheet.Columns(1, columnCount).AdjustToContents();
        for (var column = 1; column <= columnCount; column++)
        {
            var currentWidth = Math.Max(worksheet.Column(column).Width, minimumWidths[column - 1]);
            worksheet.Column(column).Width = Math.Clamp(currentWidth + 1d, 8d, 80d);
        }
    }

    private static bool TryWriteStarImages(IXLCell cell, int stars)
    {
        var assets = StarImages.Value;
        if (assets is null)
            return false;

        var imageBytes = stars >= 6 ? assets.Gold : assets.White;
        var displayCount = StarDisplayCount(stars);
        var worksheet = cell.Worksheet;
        var rowNumber = cell.Address.RowNumber;
        var columnNumber = cell.Address.ColumnNumber;

        cell.SetValue(string.Empty);
        cell.Style.NumberFormat.Format = "@";
        worksheet.Row(rowNumber).Height = Math.Max(worksheet.Row(rowNumber).Height, 18d);
        worksheet.Column(columnNumber).Width = Math.Max(worksheet.Column(columnNumber).Width, 12d);

        for (var index = 0; index < displayCount; index++)
        {
            using var imageStream = new MemoryStream(imageBytes, writable: false);
            var picture = worksheet.AddPicture(imageStream, XLPictureFormat.Png, $"star-{worksheet.Position}-{rowNumber}-{columnNumber}-{index}");
            picture.MoveTo(cell, 2 + (index * 14), 2);
            picture.Width = 12;
            picture.Height = 12;
        }

        return true;
    }

    private static void SetStyledStarText(IXLCell cell, int stars)
    {
        var styled = StarTextCell(stars);
        cell.SetValue(styled.Text);
        cell.Style.NumberFormat.Format = "@";
        cell.Style.Font.FontColor = XLColor.FromHtml(styled.FontColor);
        cell.Style.Font.Bold = styled.Bold;
        cell.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
    }

    private static int StarDisplayCount(int stars) => stars >= 6 ? 5 : Math.Clamp(stars, 1, 5);

    private static StarImageAssets? LoadStarImageAssets()
    {
        foreach (var root in CandidateContentRoots())
        {
            var goldPath = Path.Combine(root, "wwwroot", "star_gold.png");
            var whitePath = Path.Combine(root, "wwwroot", "star_white.png");
            if (File.Exists(goldPath) && File.Exists(whitePath))
                return new StarImageAssets(File.ReadAllBytes(goldPath), File.ReadAllBytes(whitePath));
        }

        return null;
    }

    private static IEnumerable<string> CandidateContentRoots()
    {
        yield return AppContext.BaseDirectory;
        yield return Directory.GetCurrentDirectory();
        yield return Path.Combine(Directory.GetCurrentDirectory(), "FSTService");
    }

    private static string FormatTeamMembers(
        IReadOnlyCollection<string> accountIds,
        IReadOnlyDictionary<string, string> displayNames,
        string? selectedAccountId)
    {
        var members = accountIds
            .Where(static accountId => !string.IsNullOrWhiteSpace(accountId))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(accountId => new TeamMemberDisplay(accountId, DisplayNameOrAccountId(accountId, displayNames)))
            .ToList();

        if (members.Count == 0)
            return string.Empty;

        var ordered = string.IsNullOrWhiteSpace(selectedAccountId)
            ? members
                .OrderBy(static member => member.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(static member => member.AccountId, StringComparer.OrdinalIgnoreCase)
            : members
                .OrderByDescending(member => string.Equals(member.AccountId, selectedAccountId, StringComparison.OrdinalIgnoreCase))
                .ThenBy(static member => member.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(static member => member.AccountId, StringComparer.OrdinalIgnoreCase);

        return string.Join(", ", ordered.Select(static member => member.DisplayName));
    }

    private static string FormatBandMembers(IReadOnlyCollection<PlayerBandMemberDto> members, string? selectedAccountId)
    {
        var displayMembers = members
            .Where(static member => !string.IsNullOrWhiteSpace(member.AccountId) || !string.IsNullOrWhiteSpace(member.DisplayName))
            .Select(static member => new TeamMemberDisplay(member.AccountId, string.IsNullOrWhiteSpace(member.DisplayName) ? member.AccountId : member.DisplayName!))
            .ToList();

        if (displayMembers.Count == 0)
            return "N/A";

        var ordered = string.IsNullOrWhiteSpace(selectedAccountId)
            ? displayMembers
                .OrderBy(static member => member.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(static member => member.AccountId, StringComparer.OrdinalIgnoreCase)
            : displayMembers
                .OrderByDescending(member => string.Equals(member.AccountId, selectedAccountId, StringComparison.OrdinalIgnoreCase))
                .ThenBy(static member => member.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(static member => member.AccountId, StringComparer.OrdinalIgnoreCase);

        return string.Join(", ", ordered.Select(static member => member.DisplayName));
    }

    private static string DisplayNameOrAccountId(string accountId, IReadOnlyDictionary<string, string> displayNames)
        => displayNames.TryGetValue(accountId, out var displayName) && !string.IsNullOrWhiteSpace(displayName)
            ? displayName
            : accountId;

    private static string DisplayNameOrAccountIdOrNA(string? accountId, IReadOnlyDictionary<string, string> displayNames)
    {
        if (string.IsNullOrWhiteSpace(accountId))
            return "N/A";

        return DisplayNameOrAccountId(accountId, displayNames);
    }

    private static string DisplayNameOrNA(string? displayName)
        => string.IsNullOrWhiteSpace(displayName) ? "N/A" : displayName;

    private static string FriendlyBandType(string? bandType) => bandType switch
    {
        "Band_Duets" => "Duos",
        "Band_Trios" => "Trios",
        "Band_Quad" => "Quads",
        null or "" => "N/A",
        _ => bandType,
    };

    private static string FriendlyScope(string? scope) => scope switch
    {
        "overall" => "Overall",
        "combo" => "Instruments",
        null or "" => "N/A",
        _ => CultureInfo.InvariantCulture.TextInfo.ToTitleCase(scope.Replace('_', ' ')),
    };

    private static string FriendlyCombo(string? comboId) => FriendlyInstrumentList(BandComboIds.ToInstruments(comboId));

    private static string FriendlyComboOrNA(string? comboId) => string.IsNullOrWhiteSpace(comboId) ? "N/A" : FriendlyCombo(comboId);

    private static string FriendlyInstrumentList(IEnumerable<string> instruments)
    {
        var labels = instruments
            .Where(static instrument => !string.IsNullOrWhiteSpace(instrument))
            .Select(FriendlyInstrument)
            .Where(static instrument => !string.IsNullOrWhiteSpace(instrument))
            .ToArray();

        return labels.Length == 0 ? "N/A" : string.Join(", ", labels);
    }

    private static object? FullComboDisplay(bool? fullCombo) => fullCombo switch
    {
        true => new ExcelStyledTextCell("✓", "#15803D", true),
        false => new ExcelStyledTextCell("X", "#B91C1C", true),
        _ => null,
    };

    private static object StarDisplayCell(int? stars)
    {
        if (stars is not > 0)
            return "N/A";

        return UseStarImagesInExport
            ? new ExcelStarCell(stars.Value)
            : StarTextCell(stars.Value);
    }

    private static ExcelStyledTextCell StarTextCell(int stars)
    {
        var color = stars >= 6 ? "#D97706" : "#4B5563";
        return new ExcelStyledTextCell(new string('★', StarDisplayCount(stars)), color, true);
    }

    private static string? DifficultyName(int? difficulty) => difficulty switch
    {
        null => null,
        0 => "Easy",
        1 => "Medium",
        2 => "Hard",
        3 => "Expert",
        4 => "Expert",
        99 => "N/A",
        _ => difficulty.Value.ToString(CultureInfo.InvariantCulture),
    };

    private static object? SongDifficultyValue(int? difficulty) => difficulty switch
    {
        null => null,
        99 => "N/A",
        _ => CountValue(difficulty.Value),
    };

    private static string FriendlyInstrument(string? instrument) => instrument switch
    {
        "Solo_Guitar" => "Lead",
        "Solo_Bass" => "Bass",
        "Solo_Drums" => "Drums",
        "Solo_Vocals" => "Tap Vocals",
        "Solo_PeripheralGuitar" => "Pro Lead",
        "Solo_PeripheralBass" => "Pro Bass",
        "Solo_PeripheralVocals" => "Karaoke",
        "Solo_PeripheralCymbals" => "Pro Drums + Cymbals",
        "Solo_PeripheralDrums" => "Pro Drums",
        null or "" => string.Empty,
        _ => instrument,
    };

    private static int InstrumentSortOrder(string? instrument) => instrument switch
    {
        "Solo_Guitar" => 0,
        "Solo_Bass" => 1,
        "Solo_Drums" => 2,
        "Solo_Vocals" => 3,
        "Solo_PeripheralGuitar" => 4,
        "Solo_PeripheralBass" => 5,
        "Solo_PeripheralVocals" => 6,
        "Solo_PeripheralCymbals" => 7,
        "Solo_PeripheralDrums" => 8,
        _ => 100,
    };

    private static string SortTitle(string songId, IReadOnlyDictionary<string, SongExportRow> songs)
        => songs.GetValueOrDefault(songId)?.Title ?? songId;

    private static string? FirstNonBlank(params string?[] values)
        => values.FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value));

    private static object? DateTimeValue(string? value, TimeZoneInfo exportTimeZone)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        if (!TryParseLocalDateTime(value, exportTimeZone, out var localDateTime))
            return value;

        return new ExcelDateTimeCell(localDateTime, "mm/dd/yyyy h:mm AM/PM");
    }

    private static bool TryParseLocalDateTime(string value, TimeZoneInfo exportTimeZone, out DateTime localDateTime)
    {
        if (DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AllowWhiteSpaces, out var offset))
        {
            localDateTime = TimeZoneInfo.ConvertTime(offset, exportTimeZone).DateTime;
            return true;
        }

        if (DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal | DateTimeStyles.AllowWhiteSpaces, out var dateTime))
        {
            var utc = dateTime.Kind == DateTimeKind.Utc ? dateTime : DateTime.SpecifyKind(dateTime, DateTimeKind.Utc);
            localDateTime = TimeZoneInfo.ConvertTime(new DateTimeOffset(utc), exportTimeZone).DateTime;
            return true;
        }

        localDateTime = default;
        return false;
    }

    private static byte[] DeduplicateXlsxMedia(byte[] content)
    {
        using var input = new MemoryStream(content, writable: false);
        using var archive = new ZipArchive(input, ZipArchiveMode.Read, leaveOpen: false);
        var entries = archive.Entries
            .Select(static entry => new XlsxPackageEntry(entry.FullName, ReadEntryContent(entry), entry.LastWriteTime))
            .ToList();

        var hashToCanonical = new Dictionary<string, string>(StringComparer.Ordinal);
        var duplicateToCanonical = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var entry in entries.Where(static entry => entry.FullName.StartsWith("xl/media/", StringComparison.OrdinalIgnoreCase)))
        {
            var hash = Convert.ToHexString(SHA256.HashData(entry.Content));
            if (hashToCanonical.TryGetValue(hash, out var canonical))
                duplicateToCanonical[entry.FullName] = canonical;
            else
                hashToCanonical[hash] = entry.FullName;
        }

        if (duplicateToCanonical.Count == 0)
            return content;

        for (var index = 0; index < entries.Count; index++)
        {
            var entry = entries[index];
            if (!entry.FullName.StartsWith("xl/drawings/_rels/", StringComparison.OrdinalIgnoreCase) || !entry.FullName.EndsWith(".rels", StringComparison.OrdinalIgnoreCase))
                continue;

            var document = XDocument.Parse(Encoding.UTF8.GetString(entry.Content), System.Xml.Linq.LoadOptions.PreserveWhitespace);
            var changed = false;
            foreach (var relationship in document.Root?.Elements(RelationshipNamespace + "Relationship") ?? [])
            {
                var targetAttribute = relationship.Attribute("Target");
                if (targetAttribute is null || string.IsNullOrWhiteSpace(targetAttribute.Value))
                    continue;

                var packagePath = ResolveRelationshipTarget(entry.FullName, targetAttribute.Value);
                if (!duplicateToCanonical.TryGetValue(packagePath, out var canonicalPath))
                    continue;

                targetAttribute.Value = RelativeRelationshipTarget(entry.FullName, canonicalPath);
                changed = true;
            }

            if (changed)
                entries[index] = entry with { Content = SerializeXml(document) };
        }

        using var output = new MemoryStream();
        using (var outputArchive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var entry in entries)
            {
                if (duplicateToCanonical.ContainsKey(entry.FullName))
                    continue;

                var outputEntry = outputArchive.CreateEntry(entry.FullName, CompressionLevel.Optimal);
                outputEntry.LastWriteTime = entry.LastWriteTime;
                using var outputStream = outputEntry.Open();
                outputStream.Write(entry.Content);
            }
        }

        return output.ToArray();
    }

    private static byte[] ReadEntryContent(ZipArchiveEntry entry)
    {
        using var entryStream = entry.Open();
        using var content = new MemoryStream();
        entryStream.CopyTo(content);
        return content.ToArray();
    }

    private static byte[] SerializeXml(XDocument document)
    {
        using var content = new MemoryStream();
        using (var writer = XmlWriter.Create(content, new XmlWriterSettings
        {
            Encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            OmitXmlDeclaration = document.Declaration is null,
        }))
        {
            document.Save(writer);
        }

        return content.ToArray();
    }

    private static string ResolveRelationshipTarget(string relationshipPath, string target)
    {
        if (target.StartsWith("/", StringComparison.Ordinal))
            return NormalizePackagePath(target.TrimStart('/'));

        return NormalizePackagePath($"{SourcePartDirectory(relationshipPath)}/{target}");
    }

    private static string RelativeRelationshipTarget(string relationshipPath, string packagePath)
    {
        var sourceParts = SourcePartDirectory(relationshipPath)
            .Split('/', StringSplitOptions.RemoveEmptyEntries);
        var targetParts = NormalizePackagePath(packagePath)
            .Split('/', StringSplitOptions.RemoveEmptyEntries);

        var common = 0;
        while (common < sourceParts.Length && common < targetParts.Length && string.Equals(sourceParts[common], targetParts[common], StringComparison.Ordinal))
            common++;

        var relativeParts = Enumerable.Repeat("..", sourceParts.Length - common)
            .Concat(targetParts.Skip(common))
            .ToArray();

        return relativeParts.Length == 0 ? "." : string.Join("/", relativeParts);
    }

    private static string SourcePartDirectory(string relationshipPath)
    {
        var relsIndex = relationshipPath.IndexOf("/_rels/", StringComparison.Ordinal);
        return relsIndex < 0 ? Path.GetDirectoryName(relationshipPath)?.Replace('\\', '/') ?? string.Empty : relationshipPath[..relsIndex];
    }

    private static string NormalizePackagePath(string path)
    {
        var parts = new List<string>();
        foreach (var part in path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (part == ".")
                continue;
            if (part == "..")
            {
                if (parts.Count > 0)
                    parts.RemoveAt(parts.Count - 1);
                continue;
            }

            parts.Add(part);
        }

        return string.Join("/", parts);
    }

    private static TimeZoneInfo ResolveTimeZone(string? timeZoneId)
    {
        if (string.IsNullOrWhiteSpace(timeZoneId))
            return TimeZoneInfo.Utc;

        var trimmed = timeZoneId.Trim();
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(trimmed);
        }
        catch (TimeZoneNotFoundException)
        {
        }
        catch (InvalidTimeZoneException)
        {
        }

        if (TimeZoneInfo.TryConvertIanaIdToWindowsId(trimmed, out var windowsId))
        {
            try
            {
                return TimeZoneInfo.FindSystemTimeZoneById(windowsId);
            }
            catch (TimeZoneNotFoundException)
            {
            }
            catch (InvalidTimeZoneException)
            {
            }
        }

        if (TimeZoneInfo.TryConvertWindowsIdToIanaId(trimmed, out var ianaId))
        {
            try
            {
                return TimeZoneInfo.FindSystemTimeZoneById(ianaId);
            }
            catch (TimeZoneNotFoundException)
            {
            }
            catch (InvalidTimeZoneException)
            {
            }
        }

        return TimeZoneInfo.Utc;
    }

    private static string SafeTableSuffix(string value)
    {
        var suffix = new string(value.Where(static ch => char.IsLetterOrDigit(ch)).ToArray());
        return string.IsNullOrWhiteSpace(suffix) ? "Unknown" : suffix;
    }

    private static string SanitizeFilePart(string input)
    {
        var builder = new StringBuilder(input.Length);
        var lastWasSeparator = false;
        foreach (var ch in input)
        {
            if (char.IsLetterOrDigit(ch))
            {
                builder.Append(ch);
                lastWasSeparator = false;
                continue;
            }

            if (ch is '-' or '_')
            {
                if (!lastWasSeparator)
                    builder.Append('-');
                lastWasSeparator = true;
                continue;
            }

            if (!lastWasSeparator)
                builder.Append('-');
            lastWasSeparator = true;
        }

        var sanitized = builder.ToString().Trim('-');
        return string.IsNullOrWhiteSpace(sanitized) ? "player" : sanitized;
    }

    private static string? GetNullableString(NpgsqlDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
    private static int? GetNullableInt32(NpgsqlDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetInt32(ordinal);
    private static double? GetNullableDouble(NpgsqlDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetDouble(ordinal);
    private static bool? GetNullableBoolean(NpgsqlDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetBoolean(ordinal);
    private static int? NullableArrayValue(IReadOnlyList<int> values, int index) =>
        index >= 0 && index < values.Count && values[index] >= 0 ? values[index] : null;

    private static bool? NullableBoolArrayValue(IReadOnlyList<int> values, int index) =>
        index >= 0 && index < values.Count
            ? values[index] switch
            {
                0 => false,
                1 => true,
                _ => null,
            }
            : null;

    private sealed record ExcelIntegerCell(long Value, string NumberFormat);
    private sealed record ExcelNumberCell(double Value, string NumberFormat);
    private sealed record ExcelDateTimeCell(DateTime Value, string NumberFormat);
    private sealed record ExcelStarCell(int Stars);
    private sealed record ExcelStyledTextCell(string Text, string FontColor, bool Bold);
    private sealed record StarImageAssets(byte[] Gold, byte[] White);
    private sealed record XlsxPackageEntry(string FullName, byte[] Content, DateTimeOffset LastWriteTime);
    private sealed record TeamMemberDisplay(string AccountId, string DisplayName);
    private sealed record PublishedBandProjectionRow(
        string SongId,
        string BandType,
        string TeamKey,
        string InstrumentCombo,
        string ComboId,
        string[] TeamMembers,
        string[] MemberAccountIds,
        int[] MemberInstrumentIds,
        int[] MemberScores,
        int[] MemberAccuracies,
        int[] MemberFullCombos,
        int[] MemberStars,
        int[] MemberDifficulties,
        int Score,
        int? Accuracy,
        bool? IsFullCombo,
        int? Stars,
        int? Difficulty,
        int? Season,
        int? Rank,
        double? Percentile,
        string? EndTime,
        DateTime FirstSeenAt);

    private sealed record PlayerExportSnapshot(
        string? AccountId,
        string? DisplayName,
        IReadOnlyList<PlayerScoreDto> SoloScores,
        IReadOnlyList<ScoreHistoryEntry> SoloScoreHistory,
        IReadOnlyList<SoloRankHistoryExportRow> SoloRankHistory,
        IReadOnlyList<PlayerBandEntryDto> Bands,
        IReadOnlyList<BandScoreExportRow> BandScores,
        IReadOnlyList<BandMemberStatExportRow> BandMemberStats,
        IReadOnlyList<BandRankHistoryExportRow> BandRankHistory,
        IReadOnlyDictionary<string, string> DisplayNames,
        IReadOnlyDictionary<string, SongExportRow> Songs);

    private sealed record SongExportRow(
        string SongId,
        string? Title,
        string? Artist,
        string? ActiveDate,
        string? LastModified,
        int? ReleaseYear,
        int? Tempo,
        int? LeadDifficulty,
        int? BassDifficulty,
        int? DrumsDifficulty,
        int? VocalsDifficulty,
        int? ProLeadDifficulty,
        int? ProBassDifficulty,
        int? ProDrumsDifficulty,
        int? KaraokeDifficulty);

    private sealed record SoloRankHistoryExportRow(string Instrument, RankHistoryDto History);
    private sealed record BandRankHistoryExportRow(
        string BandId,
        string BandType,
        string TeamKey,
        IReadOnlyList<PlayerBandMemberDto> Members,
        string Scope,
        string? ComboId,
        BandRankHistoryDto History);

    private sealed record BandScoreExportRow(
        string SongId,
        string BandType,
        string TeamKey,
        string InstrumentCombo,
        string ComboId,
        string[] TeamMembers,
        int Score,
        int? BaseScore,
        int? InstrumentBonus,
        int? OverdriveBonus,
        int? Accuracy,
        bool? IsFullCombo,
        int? Stars,
        int? Difficulty,
        int? Season,
        int? Rank,
        double? Percentile,
        string? EndTime,
        string FirstSeenAt);

    private sealed record BandMemberStatExportRow(
        string SongId,
        string BandType,
        string TeamKey,
        string InstrumentCombo,
        string ComboId,
        int MemberIndex,
        string AccountId,
        int? InstrumentId,
        string? Instrument,
        int? Score,
        int? Accuracy,
        bool? IsFullCombo,
        int? Stars,
        int? Difficulty,
        int? Season);
}

public sealed record PlayerDataExportResult(byte[] Content, string FileName, string ContentType);