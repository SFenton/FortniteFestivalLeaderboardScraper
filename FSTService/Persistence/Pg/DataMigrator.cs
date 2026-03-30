using Microsoft.Data.Sqlite;
using Npgsql;

namespace FSTService.Persistence.Pg;

/// <summary>
/// One-time data migration from SQLite databases to PostgreSQL.
/// Reads all rows from the existing SQLite files and bulk-inserts them into PG.
/// Usage: run once after PG schema is created, before switching DatabaseProvider.
/// </summary>
public static class DataMigrator
{
    public static async Task MigrateAsync(string dataDir, NpgsqlDataSource pgDataSource, ILogger log, CancellationToken ct = default)
    {
        log.LogInformation("Starting SQLite → PostgreSQL data migration from {DataDir}", dataDir);

        // 1. Meta database
        var metaPath = Path.Combine(dataDir, "fst-meta.db");
        if (File.Exists(metaPath))
        {
            await MigrateTableAsync(metaPath, pgDataSource, "AccountNames", "account_names",
                new[] { "AccountId", "DisplayName", "LastResolved" },
                new[] { "account_id", "display_name", "last_resolved" }, log, ct);

            await MigrateTableAsync(metaPath, pgDataSource, "ScrapeLog", "scrape_log",
                new[] { "Id", "StartedAt", "CompletedAt", "SongsScraped", "TotalEntries", "TotalRequests", "TotalBytes" },
                new[] { "id", "started_at", "completed_at", "songs_scraped", "total_entries", "total_requests", "total_bytes" }, log, ct);

            await MigrateTableAsync(metaPath, pgDataSource, "ScoreHistory", "score_history",
                new[] { "Id", "SongId", "Instrument", "AccountId", "OldScore", "NewScore", "OldRank", "NewRank", "Accuracy", "IsFullCombo", "Stars", "Percentile", "Season", "ScoreAchievedAt", "SeasonRank", "AllTimeRank", "Difficulty", "ChangedAt" },
                new[] { "id", "song_id", "instrument", "account_id", "old_score", "new_score", "old_rank", "new_rank", "accuracy", "is_full_combo", "stars", "percentile", "season", "score_achieved_at", "season_rank", "all_time_rank", "difficulty", "changed_at" }, log, ct);

            await MigrateTableAsync(metaPath, pgDataSource, "RegisteredUsers", "registered_users",
                new[] { "DeviceId", "AccountId", "DisplayName", "Platform", "LastLoginAt", "RegisteredAt", "LastSyncAt" },
                new[] { "device_id", "account_id", "display_name", "platform", "last_login_at", "registered_at", "last_sync_at" }, log, ct);

            await MigrateTableAsync(metaPath, pgDataSource, "BackfillStatus", "backfill_status",
                new[] { "AccountId", "Status", "SongsChecked", "EntriesFound", "TotalSongsToCheck", "StartedAt", "CompletedAt", "LastResumedAt", "ErrorMessage" },
                new[] { "account_id", "status", "songs_checked", "entries_found", "total_songs_to_check", "started_at", "completed_at", "last_resumed_at", "error_message" }, log, ct);

            await MigrateTableAsync(metaPath, pgDataSource, "SeasonWindows", "season_windows",
                new[] { "SeasonNumber", "EventId", "WindowId", "DiscoveredAt" },
                new[] { "season_number", "event_id", "window_id", "discovered_at" }, log, ct);

            await MigrateTableAsync(metaPath, pgDataSource, "SongFirstSeenSeason", "song_first_seen_season",
                new[] { "SongId", "FirstSeenSeason", "MinObservedSeason", "EstimatedSeason", "ProbeResult", "CalculatedAt" },
                new[] { "song_id", "first_seen_season", "min_observed_season", "estimated_season", "probe_result", "calculated_at" }, log, ct);

            await MigrateTableAsync(metaPath, pgDataSource, "LeaderboardPopulation", "leaderboard_population",
                new[] { "SongId", "Instrument", "TotalEntries", "UpdatedAt" },
                new[] { "song_id", "instrument", "total_entries", "updated_at" }, log, ct);

            log.LogInformation("Meta database migration complete.");
        }

        // 2. Song catalog database
        var songDbPath = Path.Combine(dataDir, "fst-service.db");
        if (File.Exists(songDbPath))
        {
            await MigrateTableAsync(songDbPath, pgDataSource, "Songs", "songs",
                new[] { "SongId", "Title", "Artist", "ActiveDate", "LastModified", "ImagePath",
                        "LeadDiff", "BassDiff", "VocalsDiff", "DrumsDiff", "ProLeadDiff", "ProBassDiff",
                        "ReleaseYear", "Tempo", "PlasticGuitarDiff", "PlasticBassDiff", "PlasticDrumsDiff", "ProVocalsDiff" },
                new[] { "song_id", "title", "artist", "active_date", "last_modified", "image_path",
                        "lead_diff", "bass_diff", "vocals_diff", "drums_diff", "pro_lead_diff", "pro_bass_diff",
                        "release_year", "tempo", "plastic_guitar_diff", "plastic_bass_diff", "plastic_drums_diff", "pro_vocals_diff" }, log, ct);

            log.LogInformation("Song catalog migration complete.");
        }

        // 3. Instrument databases (6 files → 1 consolidated table with instrument column)
        var instruments = new[] { "Solo_Guitar", "Solo_Bass", "Solo_Drums", "Solo_Vocals", "Solo_PeripheralGuitar", "Solo_PeripheralBass" };
        foreach (var instrument in instruments)
        {
            var instPath = Path.Combine(dataDir, $"fst-{instrument}.db");
            if (!File.Exists(instPath)) continue;

            await MigrateInstrumentTableAsync(instPath, pgDataSource, instrument, "LeaderboardEntries", "leaderboard_entries",
                new[] { "SongId", "AccountId", "Score", "Accuracy", "IsFullCombo", "Stars", "Season", "Percentile", "Rank", "Source", "Difficulty", "ApiRank", "EndTime", "FirstSeenAt", "LastUpdatedAt" },
                new[] { "song_id", "account_id", "score", "accuracy", "is_full_combo", "stars", "season", "percentile", "rank", "source", "difficulty", "api_rank", "end_time", "first_seen_at", "last_updated_at" }, log, ct);

            await MigrateInstrumentTableAsync(instPath, pgDataSource, instrument, "SongStats", "song_stats",
                new[] { "SongId", "EntryCount", "PreviousEntryCount", "LogWeight", "MaxScore", "ComputedAt" },
                new[] { "song_id", "entry_count", "previous_entry_count", "log_weight", "max_score", "computed_at" }, log, ct);

            await MigrateInstrumentTableAsync(instPath, pgDataSource, instrument, "AccountRankings", "account_rankings",
                new[] { "AccountId", "SongsPlayed", "TotalChartedSongs", "Coverage", "RawSkillRating", "AdjustedSkillRating", "AdjustedSkillRank", "WeightedRating", "WeightedRank", "FcRate", "FcRateRank", "TotalScore", "TotalScoreRank", "MaxScorePercent", "MaxScorePercentRank", "AvgAccuracy", "FullComboCount", "AvgStars", "BestRank", "AvgRank", "ComputedAt" },
                new[] { "account_id", "songs_played", "total_charted_songs", "coverage", "raw_skill_rating", "adjusted_skill_rating", "adjusted_skill_rank", "weighted_rating", "weighted_rank", "fc_rate", "fc_rate_rank", "total_score", "total_score_rank", "max_score_percent", "max_score_percent_rank", "avg_accuracy", "full_combo_count", "avg_stars", "best_rank", "avg_rank", "computed_at" }, log, ct);

            log.LogInformation("Instrument {Instrument} migration complete.", instrument);
        }

        log.LogInformation("All data migration complete.");
    }

    private static async Task MigrateTableAsync(string sqlitePath, NpgsqlDataSource pgDataSource,
        string sqliteTable, string pgTable, string[] sqliteCols, string[] pgCols, ILogger log, CancellationToken ct)
    {
        var connStr = new SqliteConnectionStringBuilder { DataSource = sqlitePath, Mode = SqliteOpenMode.ReadOnly }.ToString();
        await using var sqliteConn = new SqliteConnection(connStr);
        await sqliteConn.OpenAsync(ct);

        await using var pgConn = await pgDataSource.OpenConnectionAsync(ct);

        var selectSql = $"SELECT {string.Join(", ", sqliteCols)} FROM {sqliteTable}";
        await using var cmd = new SqliteCommand(selectSql, sqliteConn);
        await using var reader = await cmd.ExecuteReaderAsync(ct);

        var pgParams = string.Join(", ", pgCols.Select((_, i) => $"@p{i}"));
        var insertSql = $"INSERT INTO {pgTable} ({string.Join(", ", pgCols)}) VALUES ({pgParams}) ON CONFLICT DO NOTHING";

        int count = 0;
        await using var tx = await pgConn.BeginTransactionAsync(ct);
        await using var insertCmd = new NpgsqlCommand(insertSql, pgConn, tx);
        for (int i = 0; i < pgCols.Length; i++)
            insertCmd.Parameters.Add($"p{i}", NpgsqlTypes.NpgsqlDbType.Unknown);
        await insertCmd.PrepareAsync(ct);

        while (await reader.ReadAsync(ct))
        {
            for (int i = 0; i < pgCols.Length; i++)
                insertCmd.Parameters[$"p{i}"].Value = reader.IsDBNull(i) ? DBNull.Value : reader.GetValue(i);
            await insertCmd.ExecuteNonQueryAsync(ct);
            count++;
        }

        await tx.CommitAsync(ct);
        log.LogInformation("Migrated {Count} rows: {SqliteTable} → {PgTable}", count, sqliteTable, pgTable);
    }

    private static async Task MigrateInstrumentTableAsync(string sqlitePath, NpgsqlDataSource pgDataSource,
        string instrument, string sqliteTable, string pgTable,
        string[] sqliteCols, string[] pgCols, ILogger log, CancellationToken ct)
    {
        var connStr = new SqliteConnectionStringBuilder { DataSource = sqlitePath, Mode = SqliteOpenMode.ReadOnly }.ToString();
        await using var sqliteConn = new SqliteConnection(connStr);
        await sqliteConn.OpenAsync(ct);

        await using var pgConn = await pgDataSource.OpenConnectionAsync(ct);

        var selectSql = $"SELECT {string.Join(", ", sqliteCols)} FROM {sqliteTable}";
        await using var cmd = new SqliteCommand(selectSql, sqliteConn);
        await using var reader = await cmd.ExecuteReaderAsync(ct);

        // Add instrument column for PG
        var allPgCols = pgCols.Prepend("instrument").ToArray();
        var pgParams = string.Join(", ", allPgCols.Select((_, i) => $"@p{i}"));
        var insertSql = $"INSERT INTO {pgTable} ({string.Join(", ", allPgCols)}) VALUES ({pgParams}) ON CONFLICT DO NOTHING";

        int count = 0;
        await using var tx = await pgConn.BeginTransactionAsync(ct);
        await using var insertCmd = new NpgsqlCommand(insertSql, pgConn, tx);
        insertCmd.Parameters.Add("p0", NpgsqlTypes.NpgsqlDbType.Text); // instrument
        for (int i = 0; i < pgCols.Length; i++)
            insertCmd.Parameters.Add($"p{i + 1}", NpgsqlTypes.NpgsqlDbType.Unknown);
        await insertCmd.PrepareAsync(ct);

        while (await reader.ReadAsync(ct))
        {
            insertCmd.Parameters["p0"].Value = instrument;
            for (int i = 0; i < pgCols.Length; i++)
                insertCmd.Parameters[$"p{i + 1}"].Value = reader.IsDBNull(i) ? DBNull.Value : reader.GetValue(i);
            await insertCmd.ExecuteNonQueryAsync(ct);
            count++;
        }

        await tx.CommitAsync(ct);
        log.LogInformation("Migrated {Count} rows: {Instrument}/{SqliteTable} → {PgTable}", count, instrument, sqliteTable, pgTable);
    }
}
