using System.Diagnostics;
using System.Text.Json;
using FSTService;
using FSTService.Api;
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
bool includeRegistered = false;
bool clearCache = false;
bool ensureSchema = false;
int? limit = null;
var requestedAccounts = new List<string>();

for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--pg":
            pg = RequireValue(args, ref i, "--pg");
            break;
        case "--account":
            requestedAccounts.Add(RequireValue(args, ref i, "--account"));
            break;
        case "--accounts":
            requestedAccounts.AddRange(SplitCsv(RequireValue(args, ref i, "--accounts")));
            break;
        case "--registered":
            includeRegistered = true;
            break;
        case "--limit":
            limit = int.Parse(RequireValue(args, ref i, "--limit"));
            break;
        case "--out":
            outPath = RequireValue(args, ref i, "--out");
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
        case "--ensure-schema":
            ensureSchema = true;
            break;
        default:
            return Fail($"Unknown argument: {args[i]}");
    }
}

if (string.IsNullOrWhiteSpace(pg))
    return Fail("--pg is required");

if (!includeRegistered && requestedAccounts.Count == 0)
    return Fail("Provide at least one --account/--accounts value or use --registered");

if (execute && !allowProd)
    return Fail("--allow-prod is required with --execute");

if (clearCache && !execute)
    return Fail("--clear-cache requires --execute");

if (limit is <= 0)
    return Fail("--limit must be greater than zero");

var target = new NpgsqlConnectionStringBuilder(pg);

Console.WriteLine($"Target: host={target.Host} database={target.Database} user={target.Username}");
Console.WriteLine($"Mode: {(execute ? "execute" : "inspect")}");
Console.WriteLine($"Registered: {(includeRegistered ? "yes" : "no")}");
Console.WriteLine($"Requested accounts: {(requestedAccounts.Count == 0 ? "none" : string.Join(", ", requestedAccounts))}");
Console.WriteLine($"Limit: {(limit.HasValue ? limit.Value.ToString() : "none")}");
Console.WriteLine($"Clear cache: {(clearCache ? "yes" : "no")}");
Console.WriteLine($"Ensure schema: {(ensureSchema ? "yes" : "no")}");

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

if (ensureSchema)
    await DatabaseInitializer.EnsureSchemaAsync(dataSource);

using var metaDb = new MetaDatabase(dataSource, loggerFactory.CreateLogger<MetaDatabase>());
using var persistence = new GlobalLeaderboardPersistence(
    metaDb,
    loggerFactory,
    loggerFactory.CreateLogger<GlobalLeaderboardPersistence>(),
    dataSource,
    Options.Create(new FeatureOptions()));
persistence.Initialize();

var accountIds = ResolveTargets(metaDb, requestedAccounts, includeRegistered, limit);
if (accountIds.Count == 0)
    return Fail("No account IDs resolved for recompute");

Console.WriteLine($"Resolved accounts: {accountIds.Count:N0}");

var reports = new List<AccountRecomputeReport>();
foreach (var accountId in accountIds)
{
    var before = InspectAccount(metaDb, dataSource, accountId);
    PrintSummary("Before", before);

    RecomputeOutcomeReport? outcome = null;
    string? error = null;
    var elapsed = TimeSpan.Zero;

    if (execute)
    {
        var orchestrator = CreateOrchestrator(persistence, loggerFactory);
        var sw = Stopwatch.StartNew();
        try
        {
            var result = orchestrator.ComputeForUser(accountId, forceRecompute: true);
            outcome = new RecomputeOutcomeReport(
                result.OutcomeCode,
                result.WasRecomputed,
                result.WasSkipped,
                result.DirtySongCount);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            error = BuildErrorMessage(ex);
        }
        finally
        {
            sw.Stop();
            elapsed = sw.Elapsed;
        }
    }

    var after = execute ? InspectAccount(metaDb, dataSource, accountId) : null;
    if (after is not null)
        PrintSummary("After", after);

    if (execute)
    {
        var status = error is null ? outcome?.OutcomeCode ?? "unknown" : $"error: {error}";
        Console.WriteLine($"Recompute {accountId}: {status} elapsed={elapsed.TotalSeconds:F2}s");
    }

    reports.Add(new AccountRecomputeReport(accountId, before, after, outcome, error, Math.Round(elapsed.TotalMilliseconds, 3)));
}

var cacheCleared = false;
if (execute && clearCache)
{
    metaDb.ClearCachedResponses();
    cacheCleared = true;
    Console.WriteLine("Cleared api_response_cache.");
}

var payload = new
{
    capturedAtUtc = DateTime.UtcNow.ToString("o"),
    target = new { host = target.Host, database = target.Database, username = target.Username },
    execute,
    allowProd,
    includeRegistered,
    limit,
    clearCache = cacheCleared,
    ensureSchema,
    requestedAccounts,
    accountIds,
    reports,
};

EmitJson(outPath, payload);
return reports.Any(report => report.Error is not null) ? 1 : 0;

static RivalsOrchestrator CreateOrchestrator(GlobalLeaderboardPersistence persistence, ILoggerFactory loggerFactory)
{
    var notifications = new NotificationService(loggerFactory.CreateLogger<NotificationService>());
    var syncTracker = new UserSyncProgressTracker(notifications, loggerFactory.CreateLogger<UserSyncProgressTracker>());
    notifications.SetSyncTracker(syncTracker);

    return new RivalsOrchestrator(
        new RivalsCalculator(persistence, loggerFactory.CreateLogger<RivalsCalculator>()),
        persistence,
        notifications,
        new ScrapeProgressTracker(),
        syncTracker,
        new ResponseCacheService(TimeSpan.FromMinutes(5)),
        loggerFactory.CreateLogger<RivalsOrchestrator>());
}

static List<string> ResolveTargets(MetaDatabase metaDb, IReadOnlyList<string> requestedAccounts, bool includeRegistered, int? limit)
{
    var targets = new List<string>();
    var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    if (includeRegistered)
    {
        foreach (var accountId in metaDb.GetRegisteredAccountIds().OrderBy(id => id, StringComparer.OrdinalIgnoreCase))
        {
            if (seen.Add(accountId))
                targets.Add(accountId);
        }
    }

    foreach (var raw in requestedAccounts)
    {
        var accountId = ResolveAccountId(metaDb, raw);
        if (seen.Add(accountId))
            targets.Add(accountId);
    }

    return limit.HasValue ? targets.Take(limit.Value).ToList() : targets;
}

static string ResolveAccountId(MetaDatabase metaDb, string raw)
{
    var value = raw.Trim();
    if (string.IsNullOrWhiteSpace(value))
        throw new ArgumentException("Account value cannot be empty", nameof(raw));

    return metaDb.GetAccountIdForUsername(value) ?? value;
}

static AccountRivalsSummary InspectAccount(MetaDatabase metaDb, NpgsqlDataSource dataSource, string accountId)
{
    var status = metaDb.GetRivalsStatus(accountId);
    var combos = metaDb.GetRivalCombos(accountId)
        .OrderBy(combo => combo.InstrumentCombo, StringComparer.OrdinalIgnoreCase)
        .Select(combo =>
        {
            var rows = metaDb.GetUserRivals(accountId, combo.InstrumentCombo);
            return new ComboSummary(
                combo.InstrumentCombo,
                combo.AboveCount,
                combo.BelowCount,
                rows.Count,
                rows.Sum(row => row.SharedSongCount));
        })
        .ToList();

    return new AccountRivalsSummary(
        accountId,
        status is null ? null : new RivalsStatusSummary(
            status.Status,
            status.CombosComputed,
            status.TotalCombosToCompute,
            status.RivalsFound,
            status.AlgorithmVersion,
            status.StartedAt,
            status.CompletedAt,
            status.ErrorMessage),
        combos,
        combos.Sum(combo => combo.RivalRows),
        CountSampleRows(dataSource, accountId));
}

static int CountSampleRows(NpgsqlDataSource dataSource, string accountId)
{
    using var conn = dataSource.OpenConnection();
    using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT COUNT(*) FROM rival_song_samples WHERE user_id = @id";
    cmd.Parameters.AddWithValue("id", accountId);
    return Convert.ToInt32(cmd.ExecuteScalar());
}

static void PrintSummary(string title, AccountRivalsSummary summary)
{
    var status = summary.Status?.Status ?? "missing";
    var computedAt = summary.Status?.CompletedAt ?? "n/a";
    Console.WriteLine();
    Console.WriteLine($"{title}: {summary.AccountId}");
    Console.WriteLine($"  status={status} combos={summary.ComboCount:N0} rivals={summary.RivalRows:N0} samples={summary.SampleRows:N0} completedAt={computedAt}");
    foreach (var combo in summary.Combos)
    {
        Console.WriteLine(
            $"  {combo.Combo,-10} above={combo.AboveCount,3:N0} below={combo.BelowCount,3:N0} rows={combo.RivalRows,3:N0} sharedSongSum={combo.SharedSongCountSum,6:N0}");
    }
}

static IReadOnlyList<string> SplitCsv(string value) => value
    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
    .Where(item => !string.IsNullOrWhiteSpace(item))
    .ToArray();

static string RequireValue(string[] args, ref int index, string option)
{
    if (index + 1 >= args.Length)
        throw new ArgumentException($"{option} requires a value", nameof(args));

    return args[++index];
}

static int Fail(string message)
{
    Console.Error.WriteLine(message);
    PrintUsage();
    return 2;
}

static void PrintUsage()
{
        Console.Error.WriteLine(string.Join(Environment.NewLine,
                "Usage:",
                "  RivalsRecomputeHarness --pg <connection-string> --account <accountId-or-username> [--account <...>] [--out <path>]",
                "  RivalsRecomputeHarness --pg <connection-string> --accounts <csv> [--out <path>]",
                "  RivalsRecomputeHarness --pg <connection-string> --registered [--limit <n>] [--out <path>]",
                "  RivalsRecomputeHarness --pg <connection-string> --account <accountId-or-username> --execute --allow-prod [--clear-cache] [--out <path>]",
                "",
                "Notes:",
                "  - Default mode is read-only inspection of current rivals status/data.",
                "  - Writes require both --execute and --allow-prod.",
                "  - Schema initialization is skipped unless --ensure-schema is supplied.",
                "  - --account accepts either an account ID or a username resolvable from account_names.",
                "  - --registered targets all registered accounts; --limit caps the final target list.",
                "  - Execute mode calls RivalsOrchestrator.ComputeForUser(accountId, forceRecompute: true).",
                "  - --clear-cache truncates api_response_cache after successful execution."));
}

static void EmitJson(string? outPath, object payload)
{
    if (string.IsNullOrWhiteSpace(outPath)) return;
    var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true });
    File.WriteAllText(outPath, json);
    Console.WriteLine($"Wrote {outPath}");
}

static string BuildErrorMessage(Exception ex)
{
    var messages = new List<string>();
    for (var current = ex; current is not null; current = current.InnerException)
    {
        if (!string.IsNullOrWhiteSpace(current.Message) && !messages.Contains(current.Message, StringComparer.Ordinal))
            messages.Add(current.Message);
    }

    return messages.Count == 0 ? ex.GetType().Name : string.Join(" | ", messages);
}

public sealed record AccountRivalsSummary(
    string AccountId,
    RivalsStatusSummary? Status,
    IReadOnlyList<ComboSummary> Combos,
    int RivalRows,
    int SampleRows)
{
    public int ComboCount => Combos.Count;
}

public sealed record RivalsStatusSummary(
    string Status,
    int CombosComputed,
    int TotalCombosToCompute,
    int RivalsFound,
    int AlgorithmVersion,
    string? StartedAt,
    string? CompletedAt,
    string? ErrorMessage);

public sealed record ComboSummary(
    string Combo,
    int AboveCount,
    int BelowCount,
    int RivalRows,
    int SharedSongCountSum);

public sealed record RecomputeOutcomeReport(
    string OutcomeCode,
    bool WasRecomputed,
    bool WasSkipped,
    int DirtySongCount);

public sealed record AccountRecomputeReport(
    string AccountId,
    AccountRivalsSummary Before,
    AccountRivalsSummary? After,
    RecomputeOutcomeReport? Outcome,
    string? Error,
    double ElapsedMs);