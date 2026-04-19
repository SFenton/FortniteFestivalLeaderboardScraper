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
string? serviceUrl = null;
string? combosArg = null;
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
        case "--combos":
            combosArg = args[++i];
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

var comboIds = ParseCombos(combosArg);
var target = new NpgsqlConnectionStringBuilder(pg);

Console.WriteLine($"Target: host={target.Host} database={target.Database} user={target.Username}");
Console.WriteLine($"Combos: {string.Join(", ", comboIds)}");
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
using var persistence = new GlobalLeaderboardPersistence(
    metaDb,
    loggerFactory,
    loggerFactory.CreateLogger<GlobalLeaderboardPersistence>(),
    dataSource,
    Options.Create(new FeatureOptions()));
persistence.Initialize();

var repair = new ComboRankingRepairService(
    persistence,
    metaDb,
    dataSource,
    loggerFactory.CreateLogger<ComboRankingRepairService>());

var before = repair.Inspect(comboIds);
PrintOverview("Before", before);

List<ComboApiProbe>? apiBefore = null;
if (!string.IsNullOrWhiteSpace(serviceUrl))
{
    apiBefore = await ProbeApiAsync(serviceUrl!, comboIds);
    PrintApiProbes("API before", apiBefore);
}

var results = new List<ComboRankingRepairResult>();
var apiAfterEach = new List<ComboApiProbe>();

if (execute)
{
    foreach (var result in repair.Rebuild(comboIds))
    {
        results.Add(result);
        PrintResult(result);

        if (!string.IsNullOrWhiteSpace(serviceUrl))
        {
            if (pauseMs > 0)
                await Task.Delay(pauseMs);

            var probe = await ProbeSingleApiAsync(serviceUrl!, result.ComboId);
            apiAfterEach.Add(probe);
            PrintApiProbe("API after rebuild", probe);
        }
    }
}

var after = repair.Inspect(comboIds);
PrintOverview(execute ? "After" : "Current", after);

var payload = new
{
    capturedAtUtc = DateTime.UtcNow.ToString("o"),
    target = new { host = target.Host, database = target.Database, username = target.Username },
    execute,
    serviceUrl,
    comboIds,
    before,
    after,
    results = results.Select(result => new
    {
        result.ComboId,
        result.Instruments,
        before = result.Before,
        after = result.After,
        elapsedMs = Math.Round(result.Elapsed.TotalMilliseconds, 3),
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
          ComboRankingRepairHarness --pg <connection-string> [--combos <csv>] [--service-url <url>] [--out <path>]
          ComboRankingRepairHarness --pg <connection-string> --execute --allow-prod [--combos <csv>] [--service-url <url>] [--pause-ms <ms>] [--out <path>]

        Notes:
          - Default mode is read-only status/inspection.
          - Writes require both --execute and --allow-prod.
          - --combos accepts within-group combo IDs like 03, 0f, 180 or legacy + delimited instrument lists.
          - --service-url probes /api/rankings/combo?combo={comboId} before and after rebuilds.
        """);
}

static List<string> ParseCombos(string? combosArg)
{
    if (string.IsNullOrWhiteSpace(combosArg))
        return ComboIds.WithinGroupComboMasks.Select(ComboIds.FromMask).ToList();

    var result = new List<string>();
    foreach (var raw in combosArg.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
    {
        var comboId = ComboIds.NormalizeComboParam(raw);
        if (string.IsNullOrWhiteSpace(comboId) || !ComboIds.IsWithinGroupCombo(comboId))
            throw new ArgumentException($"Unknown within-group combo: {raw}", nameof(combosArg));

        if (!result.Contains(comboId, StringComparer.OrdinalIgnoreCase))
            result.Add(comboId);
    }

    return result;
}

static void PrintOverview(string title, ComboRankingRepairOverview overview)
{
    Console.WriteLine();
    Console.WriteLine(title);
    foreach (var combo in overview.Combos)
    {
        Console.WriteLine(
            $"  {combo.ComboId,-4} expected={combo.ExpectedAccounts,8:N0} rows={combo.LeaderboardRows,8:N0} stats={combo.StatsTotalAccounts,8:N0} computedAt={combo.ComputedAt ?? "n/a"} [{string.Join('+', combo.Instruments)}]");
    }
}

static void PrintResult(ComboRankingRepairResult result)
{
    Console.WriteLine();
    Console.WriteLine($"Rebuilt {result.ComboId} in {result.Elapsed.TotalSeconds:F2}s [{string.Join('+', result.Instruments)}]");
    Console.WriteLine(
        $"  rows: {result.Before.LeaderboardRows:N0} -> {result.After.LeaderboardRows:N0} | stats: {result.Before.StatsTotalAccounts:N0} -> {result.After.StatsTotalAccounts:N0} | expected: {result.After.ExpectedAccounts:N0}");
}

static void PrintApiProbes(string title, IReadOnlyList<ComboApiProbe> probes)
{
    Console.WriteLine();
    Console.WriteLine(title);
    foreach (var probe in probes)
        PrintApiProbe(prefix: null, probe);
}

static void PrintApiProbe(string? prefix, ComboApiProbe probe)
{
    if (!string.IsNullOrWhiteSpace(prefix))
        Console.WriteLine(prefix);

    var totalAccounts = probe.TotalAccounts.HasValue ? probe.TotalAccounts.Value.ToString("N0") : "n/a";
    var error = string.IsNullOrWhiteSpace(probe.Error) ? string.Empty : $" error={probe.Error}";
    Console.WriteLine($"  {probe.ComboId,-4} status={probe.StatusCode,3} totalAccounts={totalAccounts}{error}");
}

static async Task<List<ComboApiProbe>> ProbeApiAsync(string serviceUrl, IReadOnlyList<string> comboIds)
{
    var probes = new List<ComboApiProbe>(comboIds.Count);
    foreach (var comboId in comboIds)
        probes.Add(await ProbeSingleApiAsync(serviceUrl, comboId));
    return probes;
}

static async Task<ComboApiProbe> ProbeSingleApiAsync(string serviceUrl, string comboId)
{
    using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
    var url = $"{serviceUrl}/api/rankings/combo?combo={Uri.EscapeDataString(comboId)}";
    try
    {
        using var response = await http.GetAsync(url);
        var body = await response.Content.ReadAsStringAsync();

        int? totalAccounts = null;
        string? error = null;
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("totalAccounts", out var totalAccountsElement) && totalAccountsElement.TryGetInt32(out var parsed))
                totalAccounts = parsed;
            else if (doc.RootElement.TryGetProperty("error", out var errorElement))
                error = errorElement.GetString();
        }
        catch (JsonException ex)
        {
            error = ex.Message;
        }

        return new ComboApiProbe(comboId, (int)response.StatusCode, totalAccounts, error);
    }
    catch (Exception ex)
    {
        return new ComboApiProbe(comboId, 0, null, ex.Message);
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

public sealed record ComboApiProbe(string ComboId, int StatusCode, int? TotalAccounts, string? Error);