using System.Text.Json;
using FSTService;
using FSTService.Persistence;
using FSTService.Scraping;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;

if (args.Contains("--help", StringComparer.OrdinalIgnoreCase) || args.Contains("-h", StringComparer.OrdinalIgnoreCase))
{
    PrintUsage();
    return 0;
}

string? pg = null;
string? outPath = null;
bool execute = false;
bool allowProd = false;
bool clearCache = false;

for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--pg":
            pg = args[++i];
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
        case "--clear-cache":
            clearCache = true;
            break;
    }
}

if (string.IsNullOrWhiteSpace(pg))
    return Fail("--pg is required");

if (execute && !allowProd)
    return Fail("--allow-prod is required with --execute");

var target = new NpgsqlConnectionStringBuilder(pg);
Console.WriteLine($"Target: host={target.Host} database={target.Database} user={target.Username}");
Console.WriteLine($"Mode: {(execute ? "execute" : "dry-run")}");
Console.WriteLine($"Clear cache: {(clearCache ? "yes" : "no")}");

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

await DatabaseInitializer.EnsureSchemaAsync(dataSource);

using var metaDb = new MetaDatabase(dataSource, loggerFactory.CreateLogger<MetaDatabase>());
using var persistence = new GlobalLeaderboardPersistence(
    metaDb,
    loggerFactory,
    loggerFactory.CreateLogger<GlobalLeaderboardPersistence>(),
    dataSource,
    Options.Create(new FeatureOptions()));
persistence.Initialize();

var service = new SoloFamilyRankingBackfillService(
    persistence,
    metaDb,
    loggerFactory.CreateLogger<SoloFamilyRankingBackfillService>());

var result = service.Rebuild(execute);

if (execute && clearCache)
{
    metaDb.ClearCachedResponses();
    Console.WriteLine("Cleared api_response_cache.");
}

PrintResult(result);

var payload = new
{
    capturedAtUtc = DateTime.UtcNow.ToString("o"),
    target = new { host = target.Host, database = target.Database, username = target.Username },
    execute,
    clearCache = execute && clearCache,
    result,
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
          SoloFamilyRankingBackfillHarness --pg <connection-string> [--out <path>]
          SoloFamilyRankingBackfillHarness --pg <connection-string> --execute --allow-prod [--clear-cache] [--out <path>]

        Notes:
          - Default mode is a dry-run that reads current account_rankings and reports projected family rows.
          - Writes require both --execute and --allow-prod.
          - --clear-cache truncates api_response_cache after a successful write so cached player stats refresh with familyRanks.
        """);
}

static void PrintResult(SoloFamilyRankingBackfillResult result)
{
    Console.WriteLine();
    Console.WriteLine(result.Executed ? "Rebuilt solo family rankings" : "Projected solo family rankings");
    Console.WriteLine($"  total rows: {result.TotalRows:N0}");

    foreach (var scope in SoloFamilyRankingScopes.All)
        Console.WriteLine($"  {scope.ScopeId,-12} rows={result.ScopeRows.GetValueOrDefault(scope.ScopeId),8:N0} instruments={string.Join(',', scope.Instruments)}");

    Console.WriteLine();
    Console.WriteLine("Source rows by instrument");
    foreach (var (instrument, rows) in result.SourceRowsByInstrument.OrderBy(kvp => kvp.Key, StringComparer.OrdinalIgnoreCase))
        Console.WriteLine($"  {instrument,-24} rows={rows,8:N0} charted={result.TotalChartedByInstrument.GetValueOrDefault(instrument),5:N0}");
}

static void EmitJson(string? outPath, object payload)
{
    if (string.IsNullOrWhiteSpace(outPath)) return;
    var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true });
    File.WriteAllText(outPath, json);
    Console.WriteLine($"Wrote {outPath}");
}