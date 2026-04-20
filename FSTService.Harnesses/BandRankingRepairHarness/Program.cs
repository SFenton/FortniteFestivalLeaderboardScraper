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
string? serviceUrl = null;
string? bandTypesArg = null;
string? outPath = null;
bool execute = false;
bool allowProd = false;
int pauseMs = 0;

for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--pg":
            pg = args[++i];
            break;
        case "--service-url":
            serviceUrl = args[++i].TrimEnd('/');
            break;
        case "--band-types":
            bandTypesArg = args[++i];
            break;
        case "--out":
            outPath = args[++i];
            break;
        case "--pause-ms":
            pauseMs = int.Parse(args[++i]);
            break;
        case "--execute":
            execute = true;
            break;
        case "--allow-prod":
            allowProd = true;
            break;
    }
}

if (string.IsNullOrWhiteSpace(pg))
    return Fail("--pg is required");

if (execute && !allowProd)
    return Fail("--allow-prod is required with --execute");

var bandTypes = ParseBandTypes(bandTypesArg);
var target = new NpgsqlConnectionStringBuilder(pg);

Console.WriteLine($"Target: host={target.Host} database={target.Database} user={target.Username}");
Console.WriteLine($"Band types: {string.Join(", ", bandTypes)}");
Console.WriteLine($"Mode: {(execute ? "execute" : "status")}");

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

using var metaDb = new MetaDatabase(dataSource, loggerFactory.CreateLogger<MetaDatabase>());
var repair = new BandRankingRepairService(metaDb, dataSource, loggerFactory.CreateLogger<BandRankingRepairService>());

var before = repair.Inspect(bandTypes);
PrintOverview("Before", before);

List<BandApiProbe>? apiBefore = null;
if (!string.IsNullOrWhiteSpace(serviceUrl))
{
    apiBefore = await ProbeApiAsync(serviceUrl!, bandTypes);
    PrintApiProbes("API before", apiBefore);
}

var results = new List<BandRankingRepairResult>();
var apiAfterEach = new List<BandApiProbe>();

if (execute)
{
    foreach (var result in repair.Rebuild(bandTypes))
    {
        results.Add(result);
        PrintResult(result);

        if (!string.IsNullOrWhiteSpace(serviceUrl))
        {
            if (pauseMs > 0)
                await Task.Delay(pauseMs);

            var probe = await ProbeSingleApiAsync(serviceUrl!, result.BandType);
            apiAfterEach.Add(probe);
            PrintApiProbe("API after rebuild", probe);
        }
    }
}

var after = repair.Inspect(bandTypes);
PrintOverview(execute ? "After" : "Current", after);

var payload = new
{
    capturedAtUtc = DateTime.UtcNow.ToString("o"),
    target = new { host = target.Host, database = target.Database, username = target.Username },
    execute,
    serviceUrl,
    bandTypes,
    before,
    after,
    results = results.Select(r => new
    {
        r.BandType,
        r.TotalChartedSongs,
        before = r.Before,
        after = r.After,
        elapsedMs = Math.Round(r.Elapsed.TotalMilliseconds, 3),
    }).ToList(),
    apiBefore,
    apiAfterEach,
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
          BandRankingRepairHarness --pg <connection-string> [--band-types <csv>] [--service-url <url>] [--out <path>]
          BandRankingRepairHarness --pg <connection-string> --execute --allow-prod [--band-types <csv>] [--service-url <url>] [--pause-ms <ms>] [--out <path>]

        Notes:
          - Default mode is read-only status/inspection.
          - Writes require both --execute and --allow-prod.
          - --service-url probes /api/rankings/bands/{bandType} before and after rebuilds.
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

static void PrintOverview(string title, BandRankingRepairOverview overview)
{
    Console.WriteLine();
    Console.WriteLine(title);
    Console.WriteLine($"  Total songs: {overview.TotalChartedSongs:N0}");
    foreach (var band in overview.Bands)
    {
        Console.WriteLine(
            $"  {band.BandType,-11} source={band.SourceRows,8:N0} rankable={band.RankableRows,8:N0} derived={band.RankingRows,8:N0} overallTeams={band.OverallTeams,8:N0} combos={band.ComboCatalogEntries,6:N0}");
    }
}

static void PrintResult(BandRankingRepairResult result)
{
    Console.WriteLine();
    Console.WriteLine($"Rebuilt {result.BandType} in {result.Elapsed.TotalSeconds:F2}s");
    Console.WriteLine(
        $"  derived rows: {result.Before.RankingRows:N0} -> {result.After.RankingRows:N0} | overall teams: {result.Before.OverallTeams:N0} -> {result.After.OverallTeams:N0} | combo stats: {result.Before.ComboCatalogEntries:N0} -> {result.After.ComboCatalogEntries:N0}");
}

static void PrintApiProbes(string title, IReadOnlyList<BandApiProbe> probes)
{
    Console.WriteLine();
    Console.WriteLine(title);
    foreach (var probe in probes)
        PrintApiProbe(prefix: null, probe);
}

static void PrintApiProbe(string? prefix, BandApiProbe probe)
{
    if (!string.IsNullOrWhiteSpace(prefix))
        Console.WriteLine(prefix);

    var totalTeams = probe.TotalTeams.HasValue ? probe.TotalTeams.Value.ToString("N0") : "n/a";
    var error = string.IsNullOrWhiteSpace(probe.Error) ? string.Empty : $" error={probe.Error}";
    Console.WriteLine($"  {probe.BandType,-11} status={probe.StatusCode,3} totalTeams={totalTeams}{error}");
}

static async Task<List<BandApiProbe>> ProbeApiAsync(string serviceUrl, IReadOnlyList<string> bandTypes)
{
    var probes = new List<BandApiProbe>(bandTypes.Count);
    foreach (var bandType in bandTypes)
        probes.Add(await ProbeSingleApiAsync(serviceUrl, bandType));
    return probes;
}

static async Task<BandApiProbe> ProbeSingleApiAsync(string serviceUrl, string bandType)
{
    using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
    var url = $"{serviceUrl}/api/rankings/bands/{bandType}";
    try
    {
        using var response = await http.GetAsync(url);
        var body = await response.Content.ReadAsStringAsync();

        int? totalTeams = null;
        string? error = null;
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("totalTeams", out var totalTeamsElement) && totalTeamsElement.TryGetInt32(out var parsed))
                totalTeams = parsed;
            else if (doc.RootElement.TryGetProperty("error", out var errorElement))
                error = errorElement.GetString();
        }
        catch (JsonException ex)
        {
            error = ex.Message;
        }

        return new BandApiProbe(bandType, (int)response.StatusCode, totalTeams, error);
    }
    catch (Exception ex)
    {
        return new BandApiProbe(bandType, 0, null, ex.Message);
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

    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath)) ?? ".");
    File.WriteAllText(outPath, json);
    Console.WriteLine();
    Console.WriteLine($"Wrote {outPath}");
}

public sealed record BandApiProbe(string BandType, int StatusCode, int? TotalTeams, string? Error);