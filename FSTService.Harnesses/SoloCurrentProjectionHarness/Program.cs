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
string? songId = null;
string? instrument = null;
bool execute = false;
bool allowProd = false;
bool skipSchema = false;
bool clearExisting = false;
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
        case "--song-id":
            songId = args[++i];
            break;
        case "--instrument":
            instrument = args[++i];
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
        case "--clear-existing":
            clearExisting = true;
            break;
        case "--timeout-seconds":
            timeoutSeconds = int.Parse(args[++i]);
            break;
        default:
            return Fail($"Unknown argument: {args[i]}");
    }
}

pg ??= Environment.GetEnvironmentVariable(string.IsNullOrWhiteSpace(pgEnv) ? "PG_CONN" : pgEnv);

if (string.IsNullOrWhiteSpace(pg))
    return Fail("--pg, --pg-env, or PG_CONN is required");

if (execute && !allowProd)
    return Fail("--allow-prod is required with --execute");

if (clearExisting && !execute)
    return Fail("--clear-existing is only valid with --execute");

if (!string.IsNullOrWhiteSpace(songId) ^ !string.IsNullOrWhiteSpace(instrument))
    return Fail("--song-id and --instrument must be provided together for scoped rebuild");

var scoped = !string.IsNullOrWhiteSpace(songId) && !string.IsNullOrWhiteSpace(instrument);
var ensureSchema = execute && !skipSchema;
var target = new NpgsqlConnectionStringBuilder(pg);
Console.WriteLine($"Target: host={target.Host} database={target.Database} user={target.Username}");
Console.WriteLine($"Mode: {(execute ? scoped ? "execute-scope" : "execute-full" : "status")}");
Console.WriteLine($"Ensure schema: {ensureSchema}");
Console.WriteLine($"Timeout seconds: {(timeoutSeconds <= 0 ? "unlimited" : timeoutSeconds)}");
if (scoped)
    Console.WriteLine($"Scope: {songId}/{instrument}");
if (clearExisting)
    Console.WriteLine("Clear existing projection rows before full rebuild: true");

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

var builder = new SoloCurrentProjectionBuilder(
    dataSource,
    loggerFactory.CreateLogger<SoloCurrentProjectionBuilder>());

if (ensureSchema)
    await builder.EnsureSchemaAsync();

var before = builder.Inspect();
PrintStats("Before", before);

var options = new SoloCurrentProjectionRebuildOptions
{
    CommandTimeoutSeconds = timeoutSeconds,
    DisableSynchronousCommit = true,
    ClearExisting = clearExisting,
};

SoloCurrentProjectionRebuildResult? rebuildResult = null;
SoloCurrentProjectionScopeResult? scopeResult = null;

if (execute && scoped)
{
    scopeResult = await builder.RebuildScopeAsync(new SoloCurrentProjectionScopeKey(songId!, instrument!), options);
    Console.WriteLine();
    Console.WriteLine($"Rebuilt scope {scopeResult.SongId}/{scopeResult.Instrument} in {scopeResult.ElapsedMs / 1000.0:F2}s");
    Console.WriteLine($"  rows: {scopeResult.DeletedRows:N0} deleted / {scopeResult.InsertedRows:N0} inserted");
    var source = !scopeResult.SourceScopeExists ? "missing" : scopeResult.SourceSnapshotId?.ToString() ?? "live";
    Console.WriteLine($"  generation: {scopeResult.Generation:N0} source: {source}");
}
else if (execute)
{
    rebuildResult = await builder.RebuildAllAsync(options);
    Console.WriteLine();
    Console.WriteLine($"Rebuilt all solo current projection scopes in {rebuildResult.TotalElapsedMs / 1000.0:F2}s");
    Console.WriteLine($"  scopes: {rebuildResult.ScopeCount:N0}");
    Console.WriteLine($"  rows: {rebuildResult.DeletedRows:N0} deleted / {rebuildResult.InsertedRows:N0} inserted");
    Console.WriteLine($"  orphaned rows deleted: {rebuildResult.OrphanedRowsDeleted:N0}");
    Console.WriteLine($"  generation: {rebuildResult.Generation:N0}");
}
else
{
    Console.WriteLine();
    Console.WriteLine("Status mode only. Re-run with --execute --allow-prod to rebuild the full projection.");
    Console.WriteLine("Add --song-id <id> --instrument <instrument> for a scoped rebuild.");
}

var after = builder.Inspect();
PrintStats(execute ? "After" : "Current", after);

var payload = new
{
    capturedAtUtc = DateTime.UtcNow.ToString("o"),
    target = new { host = target.Host, database = target.Database, username = target.Username },
    execute,
    scoped,
    ensuredSchema = ensureSchema,
    clearExisting,
    timeoutSeconds,
    scope = scoped ? new { songId, instrument } : null,
    before,
    after,
    scopeResult,
    rebuildResult,
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
          SoloCurrentProjectionHarness --pg <connection-string> [--out <path>]
          SoloCurrentProjectionHarness --pg-env <env-var-name> [--out <path>]
          SoloCurrentProjectionHarness --pg <connection-string> --execute --allow-prod [--timeout-seconds <seconds>] [--clear-existing] [--out <path>]
          SoloCurrentProjectionHarness --pg <connection-string> --execute --allow-prod --song-id <song-id> --instrument <instrument> [--timeout-seconds <seconds>] [--out <path>]

        Notes:
          - Default mode is read-only status/inspection.
          - If --pg and --pg-env are omitted, PG_CONN is used.
          - Writes require both --execute and --allow-prod.
          - Status mode is read-only and does not run schema migrations.
          - Execute mode ensures schema unless --skip-schema is provided.
          - Full rebuild uses the same scoped updater as normal incremental projection maintenance.
          - Run full rebuild before deploying API code that reads current_leaderboard_entries.
        """);
}

static void PrintStats(string title, SoloCurrentProjectionStats stats)
{
    Console.WriteLine();
    Console.WriteLine(title);
    Console.WriteLine($"  projection exists:     {stats.ProjectionExists}");
    Console.WriteLine($"  projection rows:       {stats.RowCount,12:N0}");
    Console.WriteLine($"  projection scopes:     {stats.ScopeCount,12:N0}");
    Console.WriteLine($"  failed scopes:         {stats.FailedScopeCount,12:N0}");
    Console.WriteLine($"  current generation:    {FormatNullable(stats.CurrentGeneration)}");
    Console.WriteLine($"  full rebuilt at:       {stats.FullRebuiltAt?.ToString("o") ?? "n/a"}");
    Console.WriteLine($"  last scope rebuilt at: {stats.LastScopeRebuiltAt?.ToString("o") ?? "n/a"}");
    Console.WriteLine($"  total size:            {stats.TotalSize}");

    if (stats.RecentScopes.Count > 0)
    {
        Console.WriteLine("  recent scopes:");
        foreach (var scope in stats.RecentScopes.Take(5))
        {
            var error = string.IsNullOrWhiteSpace(scope.ErrorMessage) ? string.Empty : $" error={scope.ErrorMessage}";
            Console.WriteLine($"    {scope.Instrument}/{scope.SongId}: rows={scope.RowCount:N0} status={scope.Status} gen={scope.ProjectionGeneration:N0}{error}");
        }
    }
}

static string FormatNullable(long? value) => value.HasValue ? value.Value.ToString("N0") : "n/a";

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