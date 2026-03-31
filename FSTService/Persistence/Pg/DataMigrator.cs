using Microsoft.Data.Sqlite;
using Npgsql;

namespace FSTService.Persistence.Pg;

/// <summary>
/// One-time data migration from SQLite databases to PostgreSQL.
/// All tables use PostgreSQL COPY binary protocol for maximum throughput (200K-500K rows/sec).
/// Usage: run once after PG schema is created, before switching DatabaseProvider.
/// </summary>
public static class DataMigrator
{
    public static async Task MigrateAsync(string dataDir, NpgsqlDataSource pgDataSource, ILogger log, CancellationToken ct = default)
    {
        log.LogInformation("Starting SQLite → PostgreSQL data migration from {DataDir}", dataDir);
        var sw = System.Diagnostics.Stopwatch.StartNew();

        // 1. Meta database
        var metaPath = Path.Combine(dataDir, "fst-meta.db");
        if (File.Exists(metaPath))
        {
            var metaSize = new FileInfo(metaPath).Length / (1024.0 * 1024.0);
            log.LogInformation("Migrating fst-meta.db ({FileSize:F0} MB)...", metaSize);

            await CopyTableAsync(metaPath, pgDataSource, "AccountNames", "account_names",
                new[] { "AccountId", "DisplayName", "LastResolved" },
                new[] { "account_id", "display_name", "last_resolved" }, log, ct);

            await CopyTableAsync(metaPath, pgDataSource, "ScrapeLog", "scrape_log",
                new[] { "Id", "StartedAt", "CompletedAt", "SongsScraped", "TotalEntries", "TotalRequests", "TotalBytes" },
                new[] { "id", "started_at", "completed_at", "songs_scraped", "total_entries", "total_requests", "total_bytes" }, log, ct);

            await CopyTableAsync(metaPath, pgDataSource, "ScoreHistory", "score_history",
                new[] { "Id", "SongId", "Instrument", "AccountId", "OldScore", "NewScore", "OldRank", "NewRank", "Accuracy", "IsFullCombo", "Stars", "Percentile", "Season", "ScoreAchievedAt", "SeasonRank", "AllTimeRank", "Difficulty", "ChangedAt" },
                new[] { "id", "song_id", "instrument", "account_id", "old_score", "new_score", "old_rank", "new_rank", "accuracy", "is_full_combo", "stars", "percentile", "season", "score_achieved_at", "season_rank", "all_time_rank", "difficulty", "changed_at" }, log, ct);

            await CopyTableAsync(metaPath, pgDataSource, "RegisteredUsers", "registered_users",
                new[] { "DeviceId", "AccountId", "DisplayName", "Platform", "LastLoginAt", "RegisteredAt", "LastSyncAt" },
                new[] { "device_id", "account_id", "display_name", "platform", "last_login_at", "registered_at", "last_sync_at" }, log, ct);

            await CopyTableAsync(metaPath, pgDataSource, "BackfillStatus", "backfill_status",
                new[] { "AccountId", "Status", "SongsChecked", "EntriesFound", "TotalSongsToCheck", "StartedAt", "CompletedAt", "LastResumedAt", "ErrorMessage" },
                new[] { "account_id", "status", "songs_checked", "entries_found", "total_songs_to_check", "started_at", "completed_at", "last_resumed_at", "error_message" }, log, ct);

            await CopyTableAsync(metaPath, pgDataSource, "SeasonWindows", "season_windows",
                new[] { "SeasonNumber", "EventId", "WindowId", "DiscoveredAt" },
                new[] { "season_number", "event_id", "window_id", "discovered_at" }, log, ct);

            await CopyTableAsync(metaPath, pgDataSource, "SongFirstSeenSeason", "song_first_seen_season",
                new[] { "SongId", "FirstSeenSeason", "MinObservedSeason", "EstimatedSeason", "ProbeResult", "CalculatedAt" },
                new[] { "song_id", "first_seen_season", "min_observed_season", "estimated_season", "probe_result", "calculated_at" }, log, ct);

            await CopyTableAsync(metaPath, pgDataSource, "LeaderboardPopulation", "leaderboard_population",
                new[] { "SongId", "Instrument", "TotalEntries", "UpdatedAt" },
                new[] { "song_id", "instrument", "total_entries", "updated_at" }, log, ct);

            log.LogInformation("Meta database migration complete.");
        }

        // 2. Song catalog database
        var songDbPath = Path.Combine(dataDir, "fst-service.db");
        if (File.Exists(songDbPath))
        {
            await CopyTableAsync(songDbPath, pgDataSource, "Songs", "songs",
                new[] { "SongId", "Title", "Artist", "ActiveDate", "LastModified", "ImagePath",
                        "LeadDiff", "BassDiff", "VocalsDiff", "DrumsDiff", "ProLeadDiff", "ProBassDiff",
                        "ReleaseYear", "Tempo", "PlasticGuitarDiff", "PlasticBassDiff", "PlasticDrumsDiff", "ProVocalsDiff",
                        "MaxLeadScore", "MaxBassScore", "MaxDrumsScore", "MaxVocalsScore", "MaxProLeadScore", "MaxProBassScore",
                        "DatFileHash", "SongLastModified", "PathsGeneratedAt", "CHOptVersion" },
                new[] { "song_id", "title", "artist", "active_date", "last_modified", "image_path",
                        "lead_diff", "bass_diff", "vocals_diff", "drums_diff", "pro_lead_diff", "pro_bass_diff",
                        "release_year", "tempo", "plastic_guitar_diff", "plastic_bass_diff", "plastic_drums_diff", "pro_vocals_diff",
                        "max_lead_score", "max_bass_score", "max_drums_score", "max_vocals_score", "max_pro_lead_score", "max_pro_bass_score",
                        "dat_file_hash", "song_last_modified", "paths_generated_at", "chopt_version" }, log, ct);

            log.LogInformation("Song catalog migration complete.");
        }

        // 3. Instrument databases — all via COPY protocol
        var instruments = new[] { "Solo_Guitar", "Solo_Bass", "Solo_Drums", "Solo_Vocals", "Solo_PeripheralGuitar", "Solo_PeripheralBass" };
        foreach (var instrument in instruments)
        {
            var instPath = Path.Combine(dataDir, $"fst-{instrument}.db");
            if (!File.Exists(instPath)) continue;

            var instSw = System.Diagnostics.Stopwatch.StartNew();
            var fileSize = new FileInfo(instPath).Length / (1024.0 * 1024.0);
            log.LogInformation("Migrating {Instrument} ({FileSize:F0} MB)...", instrument, fileSize);

            await CopyTableAsync(instPath, pgDataSource, "LeaderboardEntries", "leaderboard_entries",
                new[] { "SongId", "AccountId", "Score", "Accuracy", "IsFullCombo", "Stars", "Season", "Percentile", "Rank", "Source", "Difficulty", "ApiRank", "EndTime", "FirstSeenAt", "LastUpdatedAt" },
                new[] { "song_id", "account_id", "score", "accuracy", "is_full_combo", "stars", "season", "percentile", "rank", "source", "difficulty", "api_rank", "end_time", "first_seen_at", "last_updated_at" },
                log, ct, instrumentPrefix: instrument);

            await CopyTableAsync(instPath, pgDataSource, "SongStats", "song_stats",
                new[] { "SongId", "EntryCount", "PreviousEntryCount", "LogWeight", "MaxScore", "ComputedAt" },
                new[] { "song_id", "entry_count", "previous_entry_count", "log_weight", "max_score", "computed_at" },
                log, ct, instrumentPrefix: instrument);

            await CopyTableAsync(instPath, pgDataSource, "AccountRankings", "account_rankings",
                new[] { "AccountId", "SongsPlayed", "TotalChartedSongs", "Coverage", "RawSkillRating", "AdjustedSkillRating", "AdjustedSkillRank", "WeightedRating", "WeightedRank", "FcRate", "FcRateRank", "TotalScore", "TotalScoreRank", "MaxScorePercent", "MaxScorePercentRank", "AvgAccuracy", "FullComboCount", "AvgStars", "BestRank", "AvgRank", "ComputedAt" },
                new[] { "account_id", "songs_played", "total_charted_songs", "coverage", "raw_skill_rating", "adjusted_skill_rating", "adjusted_skill_rank", "weighted_rating", "weighted_rank", "fc_rate", "fc_rate_rank", "total_score", "total_score_rank", "max_score_percent", "max_score_percent_rank", "avg_accuracy", "full_combo_count", "avg_stars", "best_rank", "avg_rank", "computed_at" },
                log, ct, instrumentPrefix: instrument);

            log.LogInformation("Instrument {Instrument} complete in {Elapsed:F1}s.", instrument, instSw.Elapsed.TotalSeconds);
        }

        log.LogInformation("All data migration complete in {Elapsed:F1}s.", sw.Elapsed.TotalSeconds);
    }

    /// <summary>
    /// Generic COPY-based table migration. Streams all rows from SQLite to PG via binary COPY.
    /// When <paramref name="instrumentPrefix"/> is set, prepends an 'instrument' column to the PG table.
    /// </summary>
    private static async Task CopyTableAsync(string sqlitePath, NpgsqlDataSource pgDataSource,
        string sqliteTable, string pgTable, string[] sqliteCols, string[] pgCols,
        ILogger log, CancellationToken ct, string? instrumentPrefix = null)
    {
        var label = instrumentPrefix is not null ? $"{instrumentPrefix}/{pgTable}" : pgTable;

        // Idempotent: skip if rows already exist
        await using var pgCheckConn = await pgDataSource.OpenConnectionAsync(ct);
        await using (var checkCmd = pgCheckConn.CreateCommand())
        {
            if (instrumentPrefix is not null)
            {
                checkCmd.CommandText = $"SELECT EXISTS (SELECT 1 FROM {pgTable} WHERE instrument = @i)";
                checkCmd.Parameters.AddWithValue("i", instrumentPrefix);
            }
            else
            {
                checkCmd.CommandText = $"SELECT EXISTS (SELECT 1 FROM {pgTable})";
            }
            var exists = (bool)(await checkCmd.ExecuteScalarAsync(ct))!;
            if (exists)
            {
                log.LogInformation("  {Label}: rows already exist, skipping.", label);
                return;
            }
        }

        var connStr = new SqliteConnectionStringBuilder { DataSource = sqlitePath, Mode = SqliteOpenMode.ReadOnly }.ToString();
        await using var sqliteConn = new SqliteConnection(connStr);
        await sqliteConn.OpenAsync(ct);

        var selectSql = $"SELECT {string.Join(", ", sqliteCols)} FROM {sqliteTable}";
        await using var cmd = new SqliteCommand(selectSql, sqliteConn);
        await using var reader = await cmd.ExecuteReaderAsync(ct);

        // Build COPY statement — prepend instrument column if needed
        // Use TEXT format so PG handles type coercion (e.g. ISO 8601 strings → TIMESTAMPTZ)
        var allPgCols = instrumentPrefix is not null ? pgCols.Prepend("instrument").ToArray() : pgCols;
        var copySql = $"COPY {pgTable} ({string.Join(", ", allPgCols)}) FROM STDIN WITH (FORMAT TEXT, NULL '\\N')";

        await using var pgConn = await pgDataSource.OpenConnectionAsync(ct);
        await using var writer = await pgConn.BeginTextImportAsync(copySql, ct);

        long count = 0;
        var tableSw = System.Diagnostics.Stopwatch.StartNew();
        int colCount = sqliteCols.Length;

        while (await reader.ReadAsync(ct))
        {
            var sb = new System.Text.StringBuilder(256);

            // Instrument column first (if applicable)
            if (instrumentPrefix is not null)
                sb.Append(Escape(instrumentPrefix)).Append('\t');

            // All SQLite columns — text format, tab-separated, \N for NULL
            for (int i = 0; i < colCount; i++)
            {
                if (i > 0) sb.Append('\t');
                if (reader.IsDBNull(i))
                    sb.Append("\\N");
                else
                    sb.Append(Escape(reader.GetValue(i).ToString()!));
            }

            await writer.WriteLineAsync(sb.ToString());
            count++;

            if (count % 1_000_000 == 0)
            {
                var rate = count / tableSw.Elapsed.TotalSeconds;
                log.LogInformation("  {Label}: {Count:N0} rows... ({Rate:N0} rows/sec)", label, count, rate);
            }
        }

        writer.Close(); // signals COPY completion
        var finalRate = count > 0 ? count / tableSw.Elapsed.TotalSeconds : 0;
        log.LogInformation("  {Label}: {Count:N0} rows in {Elapsed:F1}s ({Rate:N0} rows/sec)", label, count, tableSw.Elapsed.TotalSeconds, finalRate);
    }

    /// <summary>Escape special characters for COPY TEXT format (backslash, tab, newline, carriage return).</summary>
    private static string Escape(string value)
    {
        if (value.IndexOfAny(['\\', '\t', '\n', '\r']) < 0)
            return value;
        return value
            .Replace("\\", "\\\\")
            .Replace("\t", "\\t")
            .Replace("\n", "\\n")
            .Replace("\r", "\\r");
    }
}
