using System.Text.Json;
using System.Text.Json.Serialization;
using FSTService.Persistence.Maintenance;
using Npgsql;

if (args.Contains("--help", StringComparer.OrdinalIgnoreCase) || args.Contains("-h", StringComparer.OrdinalIgnoreCase))
{
    PrintUsage();
    return 0;
}

string? pg = null;
string? pgEnv = null;
string? outPath = null;
var rollbackCompleted = 1;
var executeLegacyStagingCleanup = false;
var executeSnapshotRetention = false;
string? snapshotPartition = null;
var allowProd = false;

for (var i = 0; i < args.Length; i++)
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
        case "--rollback-completed":
            rollbackCompleted = int.Parse(args[++i]);
            break;
        case "--execute-legacy-staging-cleanup":
            executeLegacyStagingCleanup = true;
            break;
        case "--execute-snapshot-retention":
            executeSnapshotRetention = true;
            break;
        case "--snapshot-partition":
            snapshotPartition = args[++i];
            break;
        case "--allow-prod":
            allowProd = true;
            break;
        default:
            return Fail($"Unknown argument: {args[i]}");
    }
}

pg ??= Environment.GetEnvironmentVariable(string.IsNullOrWhiteSpace(pgEnv) ? "PG_CONN" : pgEnv);
if (string.IsNullOrWhiteSpace(pg))
    return Fail("--pg, --pg-env, or PG_CONN is required");

if (executeLegacyStagingCleanup && !allowProd)
    return Fail("--allow-prod is required with --execute-legacy-staging-cleanup");

if (executeSnapshotRetention && !allowProd)
    return Fail("--allow-prod is required with --execute-snapshot-retention");

if (executeLegacyStagingCleanup && executeSnapshotRetention)
    return Fail("Choose only one execute mode per run");

if (executeSnapshotRetention && string.IsNullOrWhiteSpace(snapshotPartition))
    return Fail("--snapshot-partition <partition-name> is required with --execute-snapshot-retention");

var target = new NpgsqlConnectionStringBuilder(pg);
Console.WriteLine($"Target: host={target.Host} database={target.Database} user={target.Username}");
Console.WriteLine($"Mode: {ResolveMode(executeLegacyStagingCleanup, executeSnapshotRetention)}");
Console.WriteLine($"Rollback completed snapshots kept beyond active/projection source: {rollbackCompleted:N0}");
if (!string.IsNullOrWhiteSpace(snapshotPartition))
    Console.WriteLine($"Snapshot partition: {snapshotPartition}");

await using var dataSource = NpgsqlDataSource.Create(pg);
var reporter = new DatabaseMaintenanceDryRunReporter(dataSource);
var report = await reporter.BuildReportAsync(new DatabaseMaintenanceDryRunOptions(rollbackCompleted));

PrintSummary(report);

LegacyStagingCleanupResult? cleanupResult = null;
if (executeLegacyStagingCleanup)
{
    Console.WriteLine();
    Console.WriteLine("Executing legacy staging cleanup with guarded preflight...");
    cleanupResult = await reporter.CleanupLegacyStagingAsync();
    Console.WriteLine($"  executed: {cleanupResult.Executed}");
    Console.WriteLine($"  reason:   {cleanupResult.Reason}");
    if (cleanupResult.After?.Footprint is not null)
        Console.WriteLine($"  after:    rows={cleanupResult.After.ExactRowCount:N0}, total={FormatBytes(cleanupResult.After.Footprint.TotalBytes)}, indexes={FormatBytes(cleanupResult.After.Footprint.IndexBytes)}");
}

SnapshotPartitionRewriteResult? snapshotRewriteResult = null;
if (executeSnapshotRetention)
{
    Console.WriteLine();
    Console.WriteLine("Executing snapshot retention rewrite with guarded preflight...");
    snapshotRewriteResult = await reporter.RewriteSnapshotPartitionAsync(snapshotPartition!, new DatabaseMaintenanceDryRunOptions(rollbackCompleted));
    Console.WriteLine($"  executed:  {snapshotRewriteResult.Executed}");
    Console.WriteLine($"  reason:    {snapshotRewriteResult.Reason}");
    Console.WriteLine($"  retained:  {snapshotRewriteResult.Preflight?.RetainedRows ?? 0:N0} row(s) across {FormatIds(snapshotRewriteResult.Plan.KeepSnapshotIds)}");
    Console.WriteLine($"  purged:    {snapshotRewriteResult.Preflight?.PurgeRows ?? 0:N0} row(s) across {FormatIds(snapshotRewriteResult.Plan.PurgeSnapshotIds)}");
    Console.WriteLine($"  reclaimed: {FormatBytes(snapshotRewriteResult.ReclaimedBytes)} ({FormatBytes(snapshotRewriteResult.BeforeTotalBytes)} -> {FormatBytes(snapshotRewriteResult.AfterTotalBytes)})");
}

object payload = cleanupResult is null && snapshotRewriteResult is null
    ? report
    : new { report, cleanupResult, snapshotRewriteResult };
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
          DatabaseMaintenanceDryRunHarness --pg <connection-string> [--rollback-completed <count>] [--out <path>]
          DatabaseMaintenanceDryRunHarness --pg-env <env-var-name> [--rollback-completed <count>] [--out <path>]
          DatabaseMaintenanceDryRunHarness --pg <connection-string> --execute-legacy-staging-cleanup --allow-prod [--rollback-completed <count>] [--out <path>]
          DatabaseMaintenanceDryRunHarness --pg <connection-string> --execute-snapshot-retention --allow-prod --snapshot-partition <partition-name> [--rollback-completed <count>] [--out <path>]

        Notes:
          - Default mode is read-only and does not execute cleanup SQL.
          - Execute modes require --allow-prod and still refuse cleanup if preflight fails.
          - Snapshot retention execution rewrites one explicit partition per run.
          - If --pg and --pg-env are omitted, PG_CONN is used.
          - Default rollback-completed is 1.
        """);
}

static string ResolveMode(bool executeLegacyStagingCleanup, bool executeSnapshotRetention)
{
    if (executeLegacyStagingCleanup)
        return "execute legacy staging cleanup";
    if (executeSnapshotRetention)
        return "execute snapshot retention rewrite";
    return "dry-run/read-only";
}

static void PrintSummary(DatabaseMaintenanceDryRunReport report)
{
    Console.WriteLine();
    Console.WriteLine("Snapshot dry-run");
    Console.WriteLine($"  active ids:             {FormatIds(report.Snapshots.ActiveSnapshotIds)}");
    Console.WriteLine($"  projection source ids:  {FormatIds(report.Snapshots.ProjectionSourceSnapshotIds)}");
    Console.WriteLine($"  snapshot ids observed:  {report.Snapshots.SnapshotDecisions.Count:N0}");
    Console.WriteLine($"  purge candidates:       {report.Snapshots.SnapshotDecisions.Count(d => d.Action == SnapshotCleanupAction.PurgeCandidate):N0}");
    Console.WriteLine($"  blocked candidates:     {report.Snapshots.SnapshotDecisions.Count(d => d.Action == SnapshotCleanupAction.Blocked):N0}");
    Console.WriteLine($"  estimated purge rows:   {report.Snapshots.EstimatedPurgeRows:N0}");
    Console.WriteLine($"  estimated purge bytes:  {FormatBytes(report.Snapshots.EstimatedPurgeBytes)}");

    var smallestRewrite = report.SnapshotRewritePlans.FirstOrDefault(plan => plan.CanExecute);
    if (smallestRewrite is not null)
    {
        Console.WriteLine($"  smallest rewrite:       {smallestRewrite.PartitionName} ({FormatBytes(smallestRewrite.TotalBytes)}, estimated purge {FormatBytes(smallestRewrite.EstimatedPurgeBytes)})");
        Console.WriteLine($"  rewrite keep ids:       {FormatIds(smallestRewrite.KeepSnapshotIds)}");
        Console.WriteLine($"  rewrite purge ids:      {FormatIds(smallestRewrite.PurgeSnapshotIds)}");
    }

    Console.WriteLine();
    Console.WriteLine("Legacy live planned drop");
    Console.WriteLine($"  tables:                 {report.LegacyLive.Tables.Count:N0}");
    Console.WriteLine($"  live tuples:            {report.LegacyLive.LiveTuples:N0}");
    Console.WriteLine($"  total bytes:            {FormatBytes(report.LegacyLive.TotalBytes)}");
    Console.WriteLine($"  reason:                 {report.LegacyLive.Reason}");

    Console.WriteLine();
    Console.WriteLine("Legacy staging");
    Console.WriteLine($"  cleanup eligible:       {report.LegacyStaging.CleanupEligible}");
    Console.WriteLine($"  exact rows:             {report.LegacyStaging.ExactRowCount:N0}");
    Console.WriteLine($"  active staging meta:    {report.LegacyStaging.ActiveStagingMetaRows:N0}");
    Console.WriteLine($"  reason:                 {report.LegacyStaging.Reason}");

    Console.WriteLine();
    Console.WriteLine("Index candidates");
    foreach (var candidate in report.IndexCandidates.Take(12))
    {
        Console.WriteLine($"  {candidate.Name} on {candidate.TableName}: {FormatBytes(candidate.IndexBytes)}, scans={candidate.ScanCount:N0}");
    }
    if (report.IndexCandidates.Count > 12)
        Console.WriteLine($"  ... {report.IndexCandidates.Count - 12:N0} more in JSON output");
}

static string FormatIds(IReadOnlyList<long> ids) => ids.Count == 0 ? "none" : string.Join(", ", ids);

static string FormatBytes(long bytes)
{
    string[] units = ["bytes", "KB", "MB", "GB", "TB"];
    double value = bytes;
    var unit = 0;
    while (value >= 1024 && unit < units.Length - 1)
    {
        value /= 1024;
        unit++;
    }

    return unit == 0 ? $"{bytes:N0} {units[unit]}" : $"{value:N1} {units[unit]}";
}

static void EmitJson(string? outPath, object payload)
{
    if (string.IsNullOrWhiteSpace(outPath))
        return;

    var directory = Path.GetDirectoryName(outPath);
    if (!string.IsNullOrWhiteSpace(directory))
        Directory.CreateDirectory(directory);

    var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    });
    File.WriteAllText(outPath, json);
    Console.WriteLine();
    Console.WriteLine($"Wrote JSON report: {outPath}");
}