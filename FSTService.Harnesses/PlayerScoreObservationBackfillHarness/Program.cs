using FSTService.Persistence;
using Npgsql;

var options = BackfillOptions.Parse(args);
if (options.ShowHelp)
{
    PrintUsage();
    return 0;
}

await using var dataSource = NpgsqlDataSource.Create(options.PostgresConnectionString);
if (options.EnsureSchema)
    await DatabaseInitializer.EnsureSchemaAsync(dataSource);

await using var connection = await dataSource.OpenConnectionAsync();
await using var transaction = await connection.BeginTransactionAsync();

var observationsBefore = await ScalarLongAsync(connection, transaction, "SELECT COUNT(*) FROM player_score_observations");
var unionBefore = await ScalarLongAsync(connection, transaction, "SELECT COUNT(*) FROM player_score_observation_union");
var soloTouched = await ScalarLongAsync(connection, transaction, BackfillSql.ApplyLimit(BackfillSql.Solo, options.Limit));
var bandTouched = await ScalarLongAsync(connection, transaction, BackfillSql.ApplyLimit(BackfillSql.Band, options.Limit));
var observationsAfter = await ScalarLongAsync(connection, transaction, "SELECT COUNT(*) FROM player_score_observations");
var unionAfter = await ScalarLongAsync(connection, transaction, "SELECT COUNT(*) FROM player_score_observation_union");

if (options.DryRun)
    await transaction.RollbackAsync();
else
    await transaction.CommitAsync();

Console.WriteLine("Player score observation backfill");
Console.WriteLine($"Mode:                    {(options.DryRun ? "dry-run rollback" : "committed")}");
Console.WriteLine($"Source row limit:        {(options.Limit.HasValue ? options.Limit.Value.ToString("N0") : "none")}");
Console.WriteLine($"Observations before:     {observationsBefore:N0}");
Console.WriteLine($"Union rows before:       {unionBefore:N0}");
Console.WriteLine($"Solo observations touched:{soloTouched:N0}");
Console.WriteLine($"Band observations touched:{bandTouched:N0}");
Console.WriteLine($"Observations after:      {observationsAfter:N0}");
Console.WriteLine($"Union rows after:        {unionAfter:N0}");

return 0;

static void PrintUsage()
{
    Console.WriteLine("""
        Usage:
          PlayerScoreObservationBackfillHarness [options]

        Options:
          --pg <conn>          PostgreSQL connection string. Defaults to FST_POSTGRES or local dev settings.
          --dry-run            Run the backfill in a transaction and roll it back after printing counts.
          --limit <n>          Limit source rows per source kind for validation runs.
          --no-ensure-schema   Skip DatabaseInitializer.EnsureSchemaAsync before the backfill.
          --help               Show usage.
        """);
}

static async Task<long> ScalarLongAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string sql)
{
    await using var command = connection.CreateCommand();
    command.Transaction = transaction;
    command.CommandTimeout = 0;
    command.CommandText = sql;
    var value = await command.ExecuteScalarAsync();
    return Convert.ToInt64(value ?? 0);
}

sealed record BackfillOptions(string PostgresConnectionString, bool DryRun, bool EnsureSchema, int? Limit, bool ShowHelp)
{
    public static BackfillOptions Parse(string[] args)
    {
        var options = new BackfillOptions(
            Environment.GetEnvironmentVariable("FST_POSTGRES")
                ?? "Host=localhost;Port=5432;Database=fstservice;Username=fst;Password=fst_dev;Command Timeout=0",
            DryRun: false,
            EnsureSchema: true,
            Limit: null,
            ShowHelp: false);

        for (var index = 0; index < args.Length; index++)
        {
            switch (args[index])
            {
                case "--help" or "-h":
                    options = options with { ShowHelp = true };
                    break;
                case "--pg":
                    options = options with { PostgresConnectionString = args[++index] };
                    break;
                case "--dry-run":
                    options = options with { DryRun = true };
                    break;
                case "--limit":
                    var limit = int.Parse(args[++index]);
                    if (limit <= 0)
                        throw new ArgumentOutOfRangeException(nameof(args), "--limit must be positive.");
                    options = options with { Limit = limit };
                    break;
                case "--no-ensure-schema":
                    options = options with { EnsureSchema = false };
                    break;
            }
        }

        return options;
    }
}

static class BackfillSql
{
    public static string ApplyLimit(string sql, int? limit) =>
        sql.Replace("/*SOURCE_LIMIT*/", limit.HasValue ? $"LIMIT {limit.Value}" : string.Empty, StringComparison.Ordinal);

    public const string Solo = """
    WITH source_rows AS (
        SELECT DISTINCT ON (account_id, song_id, instrument, source_id)
            account_id,
            song_id,
            instrument,
            new_score,
            accuracy,
            is_full_combo,
            stars,
            difficulty,
            season,
            score_achieved_at,
            source_id,
            CASE WHEN season IS NOT NULL THEN 'season:' || season::TEXT ELSE 'alltime' END AS source_scope,
            NULLIF(new_rank, 0) AS solo_rank,
            season_rank,
            all_time_rank,
            percentile,
            changed_at
        FROM (
            SELECT *,
                CONCAT_WS(':',
                    'solo-history', account_id, song_id, instrument, new_score::TEXT,
                    COALESCE(TO_CHAR(score_achieved_at AT TIME ZONE 'UTC', 'YYYY-MM-DD"T"HH24:MI:SS.US"Z"'), 'no-time'),
                    COALESCE(difficulty::TEXT, 'no-difficulty'),
                    COALESCE(season::TEXT, 'no-season')) AS source_id
            FROM score_history
            WHERE new_score IS NOT NULL
            /*SOURCE_LIMIT*/
        ) staged
        ORDER BY account_id, song_id, instrument, source_id, changed_at DESC
    ), touched AS (
        INSERT INTO player_score_observations (
            account_id, song_id, instrument, score, accuracy, is_full_combo, stars,
            difficulty, season, score_achieved_at, source_kind, source_id, source_scope,
            solo_rank, season_rank, all_time_rank, solo_percentile, observed_at)
        SELECT
            account_id,
            song_id,
            instrument,
            new_score,
            accuracy,
            is_full_combo,
            stars,
            difficulty,
            season,
            score_achieved_at,
            'solo-history',
            source_id,
            source_scope,
            solo_rank,
            season_rank,
            all_time_rank,
            percentile,
            changed_at
        FROM source_rows
        ON CONFLICT (account_id, song_id, instrument, source_kind, source_id) DO UPDATE SET
            score = EXCLUDED.score,
            accuracy = COALESCE(EXCLUDED.accuracy, player_score_observations.accuracy),
            is_full_combo = COALESCE(EXCLUDED.is_full_combo, player_score_observations.is_full_combo),
            stars = COALESCE(EXCLUDED.stars, player_score_observations.stars),
            difficulty = COALESCE(EXCLUDED.difficulty, player_score_observations.difficulty),
            season = COALESCE(EXCLUDED.season, player_score_observations.season),
            score_achieved_at = COALESCE(EXCLUDED.score_achieved_at, player_score_observations.score_achieved_at),
            source_scope = COALESCE(NULLIF(EXCLUDED.source_scope, ''), player_score_observations.source_scope),
            solo_rank = COALESCE(EXCLUDED.solo_rank, player_score_observations.solo_rank),
            season_rank = COALESCE(EXCLUDED.season_rank, player_score_observations.season_rank),
            all_time_rank = COALESCE(EXCLUDED.all_time_rank, player_score_observations.all_time_rank),
            solo_percentile = COALESCE(EXCLUDED.solo_percentile, player_score_observations.solo_percentile),
            observed_at = GREATEST(player_score_observations.observed_at, EXCLUDED.observed_at)
        RETURNING 1
    )
    SELECT COUNT(*) FROM touched
    """;

    public const string Band = """
    WITH band_source AS (
        SELECT *
        FROM band_member_stats
        WHERE account_id IS NOT NULL
          AND account_id <> ''
          AND score IS NOT NULL
        /*SOURCE_LIMIT*/
    ), source_rows AS (
        SELECT DISTINCT ON (bms.account_id, be.song_id, mapped.instrument, source_values.source_id)
            bms.account_id,
            be.song_id,
            mapped.instrument,
            bms.score,
            bms.accuracy,
            bms.is_full_combo,
            bms.stars,
            bms.difficulty,
            NULLIF(be.season, 0) AS season,
            CASE WHEN NULLIF(be.end_time, '') IS NULL THEN NULL ELSE be.end_time::TIMESTAMPTZ END AS score_achieved_at,
            source_values.source_id,
            CASE WHEN be.season > 0 THEN 'season:' || be.season::TEXT ELSE COALESCE(NULLIF(be.source, ''), 'band') END AS source_scope,
            be.band_type,
            be.team_key,
            be.instrument_combo,
            be.score AS band_score,
            NULLIF(be.rank, 0) AS band_rank,
            be.percentile AS band_percentile,
            be.source AS band_source,
            bms.member_index,
            be.last_updated_at AS observed_at
        FROM band_source bms
        JOIN band_entries be
          ON be.song_id = bms.song_id
         AND be.band_type = bms.band_type
         AND be.team_key = bms.team_key
         AND be.instrument_combo = bms.instrument_combo
        CROSS JOIN LATERAL (
            VALUES (CASE bms.instrument_id
                WHEN 0 THEN 'Solo_Guitar'
                WHEN 1 THEN 'Solo_Bass'
                WHEN 2 THEN 'Solo_Vocals'
                WHEN 3 THEN 'Solo_Drums'
                WHEN 4 THEN 'Solo_PeripheralGuitar'
                WHEN 5 THEN 'Solo_PeripheralBass'
                WHEN 6 THEN 'Solo_PeripheralDrums'
                WHEN 7 THEN 'Solo_PeripheralVocals'
                WHEN 8 THEN 'Solo_PeripheralCymbals'
                ELSE NULL
            END)
        ) AS mapped(instrument)
        CROSS JOIN LATERAL (
            VALUES (CONCAT_WS(':',
                'band-member', bms.account_id, be.song_id, be.band_type, be.team_key,
                be.instrument_combo, bms.member_index::TEXT, bms.score::TEXT,
                COALESCE(NULLIF(be.end_time, ''), 'no-time'),
                COALESCE(bms.difficulty::TEXT, 'no-difficulty')))
        ) AS source_values(source_id)
        WHERE mapped.instrument IS NOT NULL
        ORDER BY bms.account_id, be.song_id, mapped.instrument, source_values.source_id, be.score DESC, be.last_updated_at DESC
    ), touched AS (
        INSERT INTO player_score_observations (
            account_id, song_id, instrument, score, accuracy, is_full_combo, stars,
            difficulty, season, score_achieved_at, source_kind, source_id, source_scope,
            band_type, team_key, instrument_combo, band_score, band_rank, band_percentile,
            band_source, member_index, observed_at)
        SELECT
            account_id, song_id, instrument, score, accuracy, is_full_combo, stars,
            difficulty, season, score_achieved_at, 'band-member', source_id, source_scope,
            band_type, team_key, instrument_combo, band_score, band_rank, band_percentile,
            band_source, member_index, observed_at
        FROM source_rows
        ON CONFLICT (account_id, song_id, instrument, source_kind, source_id) DO UPDATE SET
            score = EXCLUDED.score,
            accuracy = COALESCE(EXCLUDED.accuracy, player_score_observations.accuracy),
            is_full_combo = COALESCE(EXCLUDED.is_full_combo, player_score_observations.is_full_combo),
            stars = COALESCE(EXCLUDED.stars, player_score_observations.stars),
            difficulty = COALESCE(EXCLUDED.difficulty, player_score_observations.difficulty),
            season = COALESCE(EXCLUDED.season, player_score_observations.season),
            score_achieved_at = COALESCE(EXCLUDED.score_achieved_at, player_score_observations.score_achieved_at),
            source_scope = COALESCE(NULLIF(EXCLUDED.source_scope, ''), player_score_observations.source_scope),
            band_type = COALESCE(EXCLUDED.band_type, player_score_observations.band_type),
            team_key = COALESCE(EXCLUDED.team_key, player_score_observations.team_key),
            instrument_combo = COALESCE(EXCLUDED.instrument_combo, player_score_observations.instrument_combo),
            band_score = COALESCE(EXCLUDED.band_score, player_score_observations.band_score),
            band_rank = COALESCE(EXCLUDED.band_rank, player_score_observations.band_rank),
            band_percentile = COALESCE(EXCLUDED.band_percentile, player_score_observations.band_percentile),
            band_source = COALESCE(EXCLUDED.band_source, player_score_observations.band_source),
            member_index = COALESCE(EXCLUDED.member_index, player_score_observations.member_index),
            observed_at = GREATEST(player_score_observations.observed_at, EXCLUDED.observed_at)
        RETURNING 1
    )
    SELECT COUNT(*) FROM touched
    """;
}
