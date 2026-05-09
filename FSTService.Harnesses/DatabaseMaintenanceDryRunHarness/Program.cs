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
string? maintenanceIndexName = null;
string? snapshotPartition = null;
var allowProd = false;
var includeBandHistoryCoverage = false;
var bandHistoryCoverageTimeoutSeconds = 30;

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
        case "--execute-maintenance-index":
        case "--execute-retention-index":
            maintenanceIndexName = args[++i];
            break;
        case "--snapshot-partition":
            snapshotPartition = args[++i];
            break;
        case "--allow-prod":
            allowProd = true;
            break;
        case "--include-band-history-coverage":
            includeBandHistoryCoverage = true;
            break;
        case "--band-history-coverage-timeout-seconds":
            bandHistoryCoverageTimeoutSeconds = int.Parse(args[++i]);
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

if (!string.IsNullOrWhiteSpace(maintenanceIndexName) && !allowProd)
    return Fail("--allow-prod is required with --execute-maintenance-index");

var executeModeCount = (executeLegacyStagingCleanup ? 1 : 0)
    + (executeSnapshotRetention ? 1 : 0)
    + (!string.IsNullOrWhiteSpace(maintenanceIndexName) ? 1 : 0);
if (executeModeCount > 1)
    return Fail("Choose only one execute mode per run");

if (executeSnapshotRetention && string.IsNullOrWhiteSpace(snapshotPartition))
    return Fail("--snapshot-partition <partition-name> is required with --execute-snapshot-retention");

var target = new NpgsqlConnectionStringBuilder(pg);
Console.WriteLine($"Target: host={target.Host} database={target.Database} user={target.Username}");
Console.WriteLine($"Mode: {ResolveMode(executeLegacyStagingCleanup, executeSnapshotRetention, maintenanceIndexName)}");
Console.WriteLine($"Rollback completed snapshots kept beyond active/projection source: {rollbackCompleted:N0}");
Console.WriteLine($"Include band history coverage scan: {includeBandHistoryCoverage}");
Console.WriteLine($"Band history coverage command timeout: {bandHistoryCoverageTimeoutSeconds:N0}s");
if (!string.IsNullOrWhiteSpace(snapshotPartition))
    Console.WriteLine($"Snapshot partition: {snapshotPartition}");
if (!string.IsNullOrWhiteSpace(maintenanceIndexName))
    Console.WriteLine($"Maintenance index: {maintenanceIndexName}");

await using var dataSource = NpgsqlDataSource.Create(pg);
var reporter = new DatabaseMaintenanceDryRunReporter(dataSource);
var report = await reporter.BuildReportAsync(new DatabaseMaintenanceDryRunOptions(
    rollbackCompleted,
    includeBandHistoryCoverage,
    bandHistoryCoverageTimeoutSeconds));

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

RetentionHelperIndexExecutionResult? maintenanceIndexResult = null;
if (!string.IsNullOrWhiteSpace(maintenanceIndexName))
{
    Console.WriteLine();
    Console.WriteLine("Executing one maintenance index build with guarded preflight...");
    maintenanceIndexResult = await reporter.CreateMaintenanceIndexAsync(maintenanceIndexName);
    Console.WriteLine($"  executed: {maintenanceIndexResult.Executed}");
    Console.WriteLine($"  reason:   {maintenanceIndexResult.Reason}");
    Console.WriteLine($"  sql:      {maintenanceIndexResult.Sql}");
    if (maintenanceIndexResult.After?.IndexFootprint is not null)
        Console.WriteLine($"  after:    index={maintenanceIndexResult.After.IndexFootprint.Name}, bytes={FormatBytes(maintenanceIndexResult.After.IndexFootprint.IndexBytes)}");
}

object payload = cleanupResult is null && snapshotRewriteResult is null && maintenanceIndexResult is null
    ? report
    : new { report, cleanupResult, snapshotRewriteResult, maintenanceIndexResult };
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
          DatabaseMaintenanceDryRunHarness --pg <connection-string> --execute-maintenance-index <index-name> --allow-prod [--rollback-completed <count>] [--out <path>]

        Notes:
          - Default mode is read-only and does not execute cleanup SQL.
          - Band history coverage scans are skipped unless --include-band-history-coverage is set.
          - Band history coverage commands default to a 30s timeout; use --band-history-coverage-timeout-seconds <seconds> to tune it.
          - Execute modes require --allow-prod and still refuse cleanup if preflight fails.
          - Snapshot retention execution rewrites one explicit partition per run.
          - Maintenance index execution builds one explicit CREATE INDEX CONCURRENTLY target per run.
          - --execute-retention-index remains accepted as an alias for existing runbooks.
          - If --pg and --pg-env are omitted, PG_CONN is used.
          - Default rollback-completed is 1.
        """);
}

static string ResolveMode(bool executeLegacyStagingCleanup, bool executeSnapshotRetention, string? maintenanceIndexName)
{
    if (executeLegacyStagingCleanup)
        return "execute legacy staging cleanup";
    if (executeSnapshotRetention)
        return "execute snapshot retention rewrite";
    if (!string.IsNullOrWhiteSpace(maintenanceIndexName))
        return "execute one maintenance index";
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
    Console.WriteLine($"  rewrite candidates:     {report.SnapshotRewritePlans.Count(plan => plan.CanExecute):N0}/{report.SnapshotRewritePlans.Count:N0}");
    foreach (var plan in report.SnapshotRewritePlans.Take(8))
    {
        Console.WriteLine($"  {plan.PartitionName}: keepRows={plan.EstimatedRetainRows:N0}, purgeRows={plan.EstimatedPurgeRows:N0}, purgeBytes={FormatBytes(plan.EstimatedPurgeBytes)}, blockedIds={FormatIds(plan.BlockedSnapshotIds)}, canExecute={plan.CanExecute}");
        if (!plan.CanExecute)
            Console.WriteLine($"    reason: {plan.Reason}");
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

    Console.WriteLine();
    Console.WriteLine("Maintenance indexes");
    foreach (var status in report.RetentionHelperIndexes)
    {
        var state = status.IndexExists ? "present" : status.TableExists ? "missing" : "blocked";
        Console.WriteLine($"  {status.Definition.Name} on {status.Definition.TableName}: {state}");
        if (!status.IndexExists && status.TableExists)
            Console.WriteLine($"    sql: {status.Definition.CreateSql}");
    }

    Console.WriteLine();
    Console.WriteLine("Band history coverage");
    Console.WriteLine($"  status:                {report.BandHistoryCoverage.Status}");
    Console.WriteLine($"  included:              {report.BandHistoryCoverage.Included}");
    Console.WriteLine($"  wide table:            {report.BandHistoryCoverage.Tables.WideHistoryExists}");
    Console.WriteLine($"  narrow points table:   {report.BandHistoryCoverage.Tables.NarrowPointsExists}");
    Console.WriteLine($"  narrow stats table:    {report.BandHistoryCoverage.Tables.NarrowStatsExists}");
    Console.WriteLine($"  complete scopes:       {report.BandHistoryCoverage.CompleteCount:N0}");
    Console.WriteLine($"  partial scopes:        {report.BandHistoryCoverage.PartialCount:N0}");
    Console.WriteLine($"  wide-only scopes:      {report.BandHistoryCoverage.WideOnlyCount:N0}");
    Console.WriteLine($"  recommendation:        {report.BandHistoryCoverage.Recommendation}");
    foreach (var item in report.BandHistoryCoverage.Items
        .Where(item => item.Classification != BandHistoryCoverageClassification.Complete)
        .Take(8))
    {
        Console.WriteLine($"  {item.BandType}/{item.RankingScope}/{FormatCombo(item.ComboId)}: {item.Classification}, missingWideRows={item.WideRowsMissingFromNarrow:N0}, missingStatsRows={item.NarrowRowsMissingStats:N0}");
    }
}

static string FormatIds(IReadOnlyList<long> ids) => ids.Count == 0 ? "none" : string.Join(", ", ids);

static string FormatCombo(string comboId) => string.IsNullOrWhiteSpace(comboId) ? "overall" : comboId;

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