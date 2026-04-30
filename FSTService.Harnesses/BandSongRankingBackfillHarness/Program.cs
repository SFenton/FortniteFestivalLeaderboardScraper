using System.Diagnostics;
using System.Text.Json;
using FSTService.Persistence;
using FSTService.Scraping;
using Microsoft.Extensions.Logging;
using Npgsql;

if (args.Contains("--help", StringComparer.OrdinalIgnoreCase) || args.Contains("-h", StringComparer.OrdinalIgnoreCase))
{
    PrintUsage();
    return 0;
}

string? pg = null;
string? pgEnv = null;
string? bandTypesArg = null;
string? outPath = null;
bool execute = false;
bool allowProd = false;
bool skipSchema = false;
int timeoutSeconds = 0;

for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--pg":
            pg = args[++i];
            break;
        case "--pg-env":
            pgEnv = args[++i];
            break;
        case "--band-types":
            bandTypesArg = args[++i];
            break;
        case "--out":
            outPath = args[++i];
            break;
        case "--execute":
            execute = true;
            break;
        case "--allow-prod":
            allowProd = true;
            break;
        case "--skip-schema":
            skipSchema = true;
            break;
        case "--timeout-seconds":
            timeoutSeconds = int.Parse(args[++i]);
            break;
    }
}

pg ??= Environment.GetEnvironmentVariable(string.IsNullOrWhiteSpace(pgEnv) ? "PG_CONN" : pgEnv);

if (string.IsNullOrWhiteSpace(pg))
    return Fail("--pg or PG_CONN is required");

if (execute && !allowProd)
    return Fail("--allow-prod is required with --execute");

var bandTypes = ParseBandTypes(bandTypesArg);
var target = new NpgsqlConnectionStringBuilder(pg);
Console.WriteLine($"Target: host={target.Host} database={target.Database} user={target.Username}");
Console.WriteLine($"Band types: {string.Join(", ", bandTypes)}");
Console.WriteLine($"Mode: {(execute ? "execute" : "status")}");
Console.WriteLine($"Ensure schema: {execute && !skipSchema}");
Console.WriteLine($"Timeout seconds: {(timeoutSeconds <= 0 ? "unlimited" : timeoutSeconds)}");

await using var dataSource = NpgsqlDataSource.Create(pg);
using var loggerFactory = LoggerFactory.Create(builder =>
{
    builder.AddSimpleConsole(options =>
    {
        options.TimestampFormat = "HH:mm:ss ";
        options.SingleLine = true;
    });
    builder.SetMinimumLevel(LogLevel.Information);
});

if (execute && !skipSchema)
    await DatabaseInitializer.EnsureSchemaAsync(dataSource);

using var metaDb = new MetaDatabase(dataSource, loggerFactory.CreateLogger<MetaDatabase>());

var before = Inspect(dataSource, bandTypes);
PrintStats("Before", before);

var options = new BandTeamRankingRebuildOptions
{
    CommandTimeoutSeconds = timeoutSeconds,
    DisableSynchronousCommit = true,
};

var results = new List<BandSongTeamRankingRebuildMetrics>();
var totalSw = Stopwatch.StartNew();
if (execute)
{
    foreach (var bandType in bandTypes)
    {
        var metrics = metaDb.RebuildBandSongTeamRankings(bandType, options);
        results.Add(metrics);
        Console.WriteLine();
        Console.WriteLine($"Rebuilt {metrics.BandType} in {metrics.TotalElapsedMs / 1000.0:F2}s");
        Console.WriteLine($"  rows: {metrics.RowCount:N0} overall={metrics.OverallRows:N0} combo={metrics.ComboRows:N0}");
        Console.WriteLine($"  materialize={metrics.MaterializeMs / 1000.0:F2}s swap={metrics.SwapMs / 1000.0:F2}s");
    }
}
else
{
    Console.WriteLine();
    Console.WriteLine("Status mode only. Re-run with --execute --allow-prod to rebuild band song ranking rows.");
}
totalSw.Stop();

var after = Inspect(dataSource, bandTypes);
PrintStats(execute ? "After" : "Current", after);

var payload = new
{
    capturedAtUtc = DateTime.UtcNow.ToString("o"),
    target = new { host = target.Host, database = target.Database, username = target.Username },
    execute,
    ensuredSchema = execute && !skipSchema,
    timeoutSeconds,
    bandTypes,
    before,
    after,
    results,
    totalElapsedMs = Math.Round(totalSw.Elapsed.TotalMilliseconds, 3),
};

EmitJson(outPath, payload);
return 0;

static int Fail(string message)
{
    Console.Error.WriteLine(message);
    PrintUsage();
    return 2;
}

static void PrintUsage()
{
    Console.Error.WriteLine("""
        Usage:
                    BandSongRankingBackfillHarness --pg <connection-string> [--band-types <csv>] [--out <path>]
                    BandSongRankingBackfillHarness --pg-env <env-var-name> [--band-types <csv>] [--out <path>]
                    BandSongRankingBackfillHarness --pg <connection-string> --execute --allow-prod [--band-types <csv>] [--timeout-seconds <seconds>] [--skip-schema] [--out <path>]
                    BandSongRankingBackfillHarness --pg-env <env-var-name> --execute --allow-prod [--band-types <csv>] [--timeout-seconds <seconds>] [--skip-schema] [--out <path>]

        Notes:
          - Default mode is read-only status/inspection.
                    - If --pg and --pg-env are omitted, PG_CONN is used.
          - Writes require both --execute and --allow-prod.
          - This backfills band_song_team_rankings for band profile best/worst song reads.
          - Run this before deploying a service image that reads the derived projection.
        """);
}

static List<string> ParseBandTypes(string? bandTypesArg)
{
    if (string.IsNullOrWhiteSpace(bandTypesArg))
        return BandInstrumentMapping.AllBandTypes.ToList();

    var result = new List<string>();
    foreach (var bandType in bandTypesArg.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
    {
        if (!BandComboIds.IsValidBandType(bandType))
            throw new ArgumentException($"Unknown band type: {bandType}", nameof(bandTypesArg));

        if (!result.Contains(bandType, StringComparer.OrdinalIgnoreCase))
            result.Add(bandType);
    }

    return result;
}

static List<BandSongRankingProjectionStats> Inspect(NpgsqlDataSource dataSource, IReadOnlyList<string> bandTypes)
{
    var results = new List<BandSongRankingProjectionStats>(bandTypes.Count);
    using var conn = dataSource.OpenConnection();

    using (var tableCmd = conn.CreateCommand())
    {
        tableCmd.CommandText = "SELECT to_regclass('public.band_song_team_rankings') IS NOT NULL";
        if (tableCmd.ExecuteScalar() is not bool tableExists || !tableExists)
        {
            foreach (var bandType in bandTypes)
                results.Add(new BandSongRankingProjectionStats(bandType, 0, 0, 0, 0, 0, null));

            return results;
        }
    }

    foreach (var bandType in bandTypes)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT
                COUNT(*)::BIGINT AS row_count,
                COUNT(*) FILTER (WHERE ranking_scope = 'overall')::BIGINT AS overall_rows,
                COUNT(*) FILTER (WHERE ranking_scope = 'combo')::BIGINT AS combo_rows,
                COUNT(DISTINCT team_key)::BIGINT AS teams,
                COUNT(DISTINCT song_id)::BIGINT AS songs,
                MAX(computed_at) AS computed_at
            FROM band_song_team_rankings
            WHERE band_type = @bandType;";
        cmd.Parameters.AddWithValue("bandType", bandType);

        using var reader = cmd.ExecuteReader();
        if (!reader.Read())
            continue;

        results.Add(new BandSongRankingProjectionStats(
            bandType,
            reader.GetInt64(0),
            reader.GetInt64(1),
            reader.GetInt64(2),
            reader.GetInt64(3),
            reader.GetInt64(4),
            reader.IsDBNull(5) ? null : reader.GetDateTime(5).ToUniversalTime().ToString("o")));
    }

    return results;
}

static void PrintStats(string title, IReadOnlyList<BandSongRankingProjectionStats> stats)
{
    Console.WriteLine();
    Console.WriteLine(title);
    foreach (var row in stats)
    {
        Console.WriteLine(
            $"  {row.BandType,-11} rows={row.RowCount,12:N0} overall={row.OverallRows,12:N0} combo={row.ComboRows,12:N0} teams={row.Teams,10:N0} songs={row.Songs,6:N0} computed={row.ComputedAt ?? "n/a"}");
    }
}

static void EmitJson(string? outPath, object payload)
{
    var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true });
    if (outPath is null)
    {
        Console.WriteLine();
        Console.WriteLine(json);
        return;
    }

    var fullPath = Path.GetFullPath(outPath);
    var directory = Path.GetDirectoryName(fullPath);
    if (!string.IsNullOrWhiteSpace(directory))
        Directory.CreateDirectory(directory);

    File.WriteAllText(fullPath, json);
    Console.WriteLine();
    Console.WriteLine($"Wrote {fullPath}");
}

sealed record BandSongRankingProjectionStats(
    string BandType,
    long RowCount,
    long OverallRows,
    long ComboRows,
    long Teams,
    long Songs,
    string? ComputedAt);
