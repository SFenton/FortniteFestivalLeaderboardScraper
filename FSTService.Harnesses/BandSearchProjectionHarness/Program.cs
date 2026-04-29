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
string? outPath = null;
bool execute = false;
bool allowProd = false;
bool skipSchema = false;
bool catchUp = false;

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
        case "--catch-up":
            catchUp = true;
            break;
        case "--allow-prod":
            allowProd = true;
            break;
        case "--skip-schema":
            skipSchema = true;
            break;
    }
}

if (string.IsNullOrWhiteSpace(pg))
    return Fail("--pg is required");

if (execute && catchUp)
    return Fail("--execute and --catch-up are mutually exclusive");

if ((execute || catchUp) && !allowProd)
    return Fail("--allow-prod is required with --execute or --catch-up");

var target = new NpgsqlConnectionStringBuilder(pg);
Console.WriteLine($"Target: host={target.Host} database={target.Database} user={target.Username}");
Console.WriteLine($"Mode: {(execute ? "execute" : catchUp ? "catch-up" : "status")}");
Console.WriteLine($"Ensure schema: {!skipSchema}");

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

if (!skipSchema)
    await DatabaseInitializer.EnsureSchemaAsync(dataSource);

var builder = new BandSearchProjectionBuilder(
    dataSource,
    loggerFactory.CreateLogger<BandSearchProjectionBuilder>());

if (execute || catchUp)
{
    Console.WriteLine("Ensuring projection state refreshed_at column...");
    await builder.EnsureStateRefreshedAtAsync();
}

var before = builder.Inspect();
PrintStats("Before", before);

BandSearchProjectionBackfillResult? result = null;
BandSearchProjectionCatchUpResult? catchUpResult = null;
if (execute)
{
    result = await builder.RebuildAllAsync();
    PrintStats("After", result.Stats);
    Console.WriteLine();
    Console.WriteLine($"Team load: {result.TeamLoadMs / 1000.0:F2}s");
    Console.WriteLine($"Member load: {result.MemberLoadMs / 1000.0:F2}s");
    Console.WriteLine($"Band id update: {result.BandIdUpdateMs / 1000.0:F2}s");
    Console.WriteLine($"Total: {result.TotalElapsedMs / 1000.0:F2}s");
}
else if (catchUp)
{
    catchUpResult = await builder.CatchUpAsync();
    var after = builder.Inspect();
    PrintStats("After", after);
    Console.WriteLine();
    Console.WriteLine($"Impacted teams:       {catchUpResult.ImpactedTeams:N0}");
    Console.WriteLine($"Dead projected teams: {catchUpResult.DeadProjectedTeams:N0}");
    Console.WriteLine($"Stale live teams:     {catchUpResult.StaleLiveProjectedTeams:N0}");
    Console.WriteLine($"New source teams:     {catchUpResult.NewSourceTeams:N0}");
    Console.WriteLine($"Team rows:            {catchUpResult.DeletedTeamRows:N0} deleted / {catchUpResult.InsertedTeamRows:N0} inserted");
    Console.WriteLine($"Member rows:          {catchUpResult.DeletedMemberRows:N0} deleted / {catchUpResult.InsertedMemberRows:N0} inserted");
    Console.WriteLine($"Final state rows:     {catchUpResult.FinalTeamRows:N0} teams / {catchUpResult.FinalMemberRows:N0} members");
    Console.WriteLine($"Total:                {catchUpResult.TotalElapsedMs / 1000.0:F2}s");
}
else
{
    Console.WriteLine();
    Console.WriteLine("Status mode only. Re-run with --execute --allow-prod to rebuild the projection, or --catch-up --allow-prod to reconcile drift.");
}

var payload = new
{
    capturedAtUtc = DateTime.UtcNow.ToString("o"),
    target = new { host = target.Host, database = target.Database, username = target.Username },
    execute,
    catchUp,
    ensuredSchema = !skipSchema,
    before,
    result,
    catchUpResult,
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
          BandSearchProjectionHarness --pg <connection-string> [--out <path>]
          BandSearchProjectionHarness --pg <connection-string> --execute --allow-prod [--out <path>] [--skip-schema]
                    BandSearchProjectionHarness --pg <connection-string> --catch-up --allow-prod [--out <path>] [--skip-schema]

        Notes:
          - Default mode is read-only status/inspection.
          - Writes require both --execute and --allow-prod.
                    - Catch-up reconciles dead/stale/new projection rows without a full rebuild.
          - This backfills only the band search projection tables. It does not deploy or restart FSTService.
        """);
}

static void PrintStats(string title, BandSearchProjectionStats stats)
{
    Console.WriteLine();
    Console.WriteLine(title);
    Console.WriteLine($"  projection teams:   {stats.TeamRows,12:N0}");
    Console.WriteLine($"  projection members: {stats.MemberRows,12:N0}");
    Console.WriteLine($"  state rebuilt at:   {stats.RebuiltAt?.ToString("o") ?? "n/a"}");
    Console.WriteLine($"  state teams:        {FormatNullable(stats.StateTeamRows)}");
    Console.WriteLine($"  state members:      {FormatNullable(stats.StateMemberRows)}");
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
