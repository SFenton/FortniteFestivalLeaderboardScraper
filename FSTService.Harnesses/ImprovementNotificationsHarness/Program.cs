using System.Text.Json;
using FSTService.Persistence;
using Microsoft.Extensions.Logging;
using Npgsql;

if (args.Contains("--help", StringComparer.OrdinalIgnoreCase) || args.Contains("-h", StringComparer.OrdinalIgnoreCase))
{
    PrintUsage();
    return 0;
}

string? pg = null;
string? pgEnv = null;
string? outPath = null;
string source = "precompute";
string scope = "registered";
bool execute = false;
bool allowProd = false;
bool baselineOnly = false;
bool skipSchema = false;
bool pruneExpired = true;
bool includePlayers = true;
bool includeBands = true;
bool includeSongEvents = true;
bool includeRankings = true;
bool playersSpecified = false;
bool bandsSpecified = false;
bool songEventsSpecified = false;
bool rankingsSpecified = false;
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
        case "--out":
            outPath = args[++i];
            break;
        case "--scope":
            scope = args[++i];
            break;
        case "--source":
            source = args[++i];
            break;
        case "--execute":
            execute = true;
            break;
        case "--dry-run":
            execute = false;
            break;
        case "--allow-prod":
            allowProd = true;
            break;
        case "--baseline-only":
            baselineOnly = true;
            break;
        case "--skip-schema":
            skipSchema = true;
            break;
        case "--prune-expired":
            pruneExpired = true;
            break;
        case "--no-prune-expired":
            pruneExpired = false;
            break;
        case "--players":
            playersSpecified = true;
            includePlayers = true;
            break;
        case "--bands":
            bandsSpecified = true;
            includeBands = true;
            break;
        case "--song-events":
            songEventsSpecified = true;
            includeSongEvents = true;
            break;
        case "--rankings":
            rankingsSpecified = true;
            includeRankings = true;
            break;
        case "--no-players":
            playersSpecified = true;
            includePlayers = false;
            break;
        case "--no-bands":
            bandsSpecified = true;
            includeBands = false;
            break;
        case "--no-song-events":
            songEventsSpecified = true;
            includeSongEvents = false;
            break;
        case "--no-rankings":
            rankingsSpecified = true;
            includeRankings = false;
            break;
        case "--timeout-seconds":
            timeoutSeconds = int.Parse(args[++i]);
            break;
        default:
            return Fail($"Unknown argument: {args[i]}");
    }
}

if (playersSpecified && !bandsSpecified)
    includeBands = false;
if (bandsSpecified && !playersSpecified)
    includePlayers = false;
if (songEventsSpecified && !rankingsSpecified)
    includeRankings = false;
if (rankingsSpecified && !songEventsSpecified)
    includeSongEvents = false;

pg ??= Environment.GetEnvironmentVariable(string.IsNullOrWhiteSpace(pgEnv) ? "PG_CONN" : pgEnv);

if (string.IsNullOrWhiteSpace(pg))
    return Fail("--pg, --pg-env, or PG_CONN is required");

if (scope is not ("registered" or "all"))
    return Fail("--scope must be registered or all");

if (execute && !allowProd)
    return Fail("--allow-prod is required with --execute");

if (baselineOnly && !execute)
    return Fail("--baseline-only requires --execute --allow-prod");

if (!includePlayers && !includeBands)
    return Fail("At least one of players or bands must be included");

if (!includeSongEvents && !includeRankings)
    return Fail("At least one of song events or rankings must be included");

var target = new NpgsqlConnectionStringBuilder(pg);
Console.WriteLine($"Target: host={target.Host} database={target.Database} user={target.Username}");
Console.WriteLine($"Mode: {(execute ? baselineOnly ? "baseline" : "execute" : "dry-run")}");
Console.WriteLine($"Scope: {scope}");
Console.WriteLine($"Source: {source}");
Console.WriteLine($"Players: {includePlayers}; Bands: {includeBands}; Song events: {includeSongEvents}; Rankings: {includeRankings}");
Console.WriteLine($"Prune expired: {pruneExpired}");
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
{
    Console.WriteLine("Ensuring notification schema...");
    await EnsureNotificationSchemaAsync(dataSource, timeoutSeconds);
}

var service = new ImprovementNotificationService(
    dataSource,
    loggerFactory.CreateLogger<ImprovementNotificationService>());

var report = service.Precompute(new ImprovementNotificationPrecomputeOptions(
    Execute: execute,
    BaselineOnly: baselineOnly,
    Scope: scope,
    IncludePlayers: includePlayers,
    IncludeBands: includeBands,
    IncludeSongEvents: includeSongEvents,
    IncludeRankings: includeRankings,
    PruneExpired: pruneExpired,
    CommandTimeoutSeconds: timeoutSeconds,
    Source: source));

PrintReport(report);

var payload = new
{
    capturedAtUtc = DateTime.UtcNow.ToString("o"),
    target = new { host = target.Host, database = target.Database, username = target.Username },
    report,
};
EmitJson(outPath, payload);

return report.ErrorMessage is null ? 0 : 1;

static int Fail(string message)
{
    Console.Error.WriteLine(message);
    PrintUsage();
    return 2;
}

static async Task EnsureNotificationSchemaAsync(NpgsqlDataSource dataSource, int timeoutSeconds)
{
    await using var conn = await dataSource.OpenConnectionAsync();
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = ImprovementNotificationSchema.Sql;
    cmd.CommandTimeout = timeoutSeconds;
    await cmd.ExecuteNonQueryAsync();
}

static void PrintUsage()
{
    Console.Error.WriteLine("""
        Usage:
          ImprovementNotificationsHarness --pg <connection-string> [--scope registered|all] [--out <path>]
          ImprovementNotificationsHarness --pg-env <env-var-name> [--scope registered|all] [--out <path>]
          ImprovementNotificationsHarness --pg <connection-string> --execute --allow-prod [--baseline-only] [--scope registered|all] [--out <path>]

        Options:
          --players / --bands              Limit to one subject family. Default: both.
          --song-events / --rankings       Limit to one event family. Default: both.
          --no-players / --no-bands        Exclude a subject family.
          --no-song-events / --no-rankings Exclude an event family.
          --baseline-only                  Seed durable state without emitting notification events.
          --source <label>                 Source label written to run/event rows. Default: precompute.
          --prune-expired                  Delete expired live events in execute mode; count them in dry-run mode. Default: true.
          --no-prune-expired               Skip expiration cleanup.
          --timeout-seconds <seconds>      PostgreSQL command timeout. 0 means unlimited.
          --skip-schema                    Do not run schema init in execute mode.

        Notes:
          - Default mode is dry-run and does not write.
          - If --pg and --pg-env are omitted, PG_CONN is used.
          - Writes require both --execute and --allow-prod.
          - Use --baseline-only for the first all-user/all-band seed to avoid massive first-score feeds.
        """);
}

static void PrintReport(ImprovementNotificationPrecomputeReport report)
{
    Console.WriteLine();
    Console.WriteLine("Improvement notification report");
    Console.WriteLine($"  run id:              {report.RunId?.ToString() ?? "n/a"}");
    Console.WriteLine($"  mode:                {report.Mode}");
    Console.WriteLine($"  scope:               {report.Scope}");
    Console.WriteLine($"  player song rows:    {report.PlayerSongRowsScanned:N0}");
    Console.WriteLine($"  player song events:  {report.PlayerSongEventsInserted:N0}");
    Console.WriteLine($"  player rank rows:    {report.PlayerRankRowsScanned:N0}");
    Console.WriteLine($"  player rank events:  {report.PlayerRankEventsInserted:N0}");
    Console.WriteLine($"  band subjects:       {report.BandSubjectsUpserted:N0}");
    Console.WriteLine($"  band song rows:      {report.BandSongRowsScanned:N0}");
    Console.WriteLine($"  band song events:    {report.BandSongEventsInserted:N0}");
    Console.WriteLine($"  band rank rows:      {report.BandRankRowsScanned:N0}");
    Console.WriteLine($"  band rank events:    {report.BandRankEventsInserted:N0}");
    Console.WriteLine($"  expired player rows: {report.ExpiredPlayerEventsDeleted:N0}");
    Console.WriteLine($"  expired band rows:   {report.ExpiredBandEventsDeleted:N0}");
    if (!string.IsNullOrWhiteSpace(report.ErrorMessage))
        Console.WriteLine($"  error:               {report.ErrorMessage}");
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