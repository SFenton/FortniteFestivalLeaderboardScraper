using System.Text.Json;
using System.Text.Json.Serialization;
using FSTService;
using FSTService.Persistence;
using FSTService.Scraping;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

if (args.Contains("--help", StringComparer.OrdinalIgnoreCase) || args.Contains("-h", StringComparer.OrdinalIgnoreCase))
{
    PrintUsage();
    return 0;
}

string? pg = null;
string? pgEnv = null;
string? outPath = null;
long? scrapeId = null;
DateOnly? snapshotDate = null;
DateOnly? backfillStartDate = null;
DateOnly? backfillEndDate = null;
string? bandTypesArg = null;
string? rankingScope = null;
string? comboId = null;
string? teamKey = null;
var maxAttempts = 3;
var retryDelaySeconds = 900;
var commandTimeoutSeconds = 0;
var sampleLimit = 10;
var days = 30;
var writeMode = BandRankHistoryWriteMode.Legacy;
var execute = false;
var process = false;
var parity = false;
var v2Coverage = false;
var v2ValueParity = false;
var v2LatestParity = false;
var v2ReadPreview = false;
var v2Readiness = false;
var v2Backfill = false;
var v2BackfillExecute = false;
var synchronousCommitOff = false;
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
        case "--scrape-id":
            scrapeId = long.Parse(args[++i]);
            break;
        case "--snapshot-date":
            snapshotDate = DateOnly.Parse(args[++i]);
            break;
        case "--backfill-start-date":
            backfillStartDate = DateOnly.Parse(args[++i]);
            break;
        case "--backfill-end-date":
            backfillEndDate = DateOnly.Parse(args[++i]);
            break;
        case "--band-types":
            bandTypesArg = args[++i];
            break;
        case "--scope":
        case "--ranking-scope":
            rankingScope = args[++i];
            break;
        case "--combo-id":
            comboId = args[++i];
            break;
        case "--team-key":
            teamKey = args[++i];
            break;
        case "--max-attempts":
            maxAttempts = int.Parse(args[++i]);
            break;
        case "--retry-delay-seconds":
            retryDelaySeconds = int.Parse(args[++i]);
            break;
        case "--command-timeout-seconds":
            commandTimeoutSeconds = int.Parse(args[++i]);
            break;
        case "--sample-limit":
            sampleLimit = int.Parse(args[++i]);
            break;
        case "--days":
            days = int.Parse(args[++i]);
            break;
        case "--write-mode":
            if (!Enum.TryParse<BandRankHistoryWriteMode>(args[++i], ignoreCase: true, out writeMode))
                return Fail("--write-mode must be Legacy or Dual");
            break;
        case "--execute":
            execute = true;
            break;
        case "--process":
            process = true;
            break;
        case "--parity":
            parity = true;
            break;
        case "--v2-coverage":
            v2Coverage = true;
            break;
        case "--v2-value-parity":
            v2ValueParity = true;
            break;
        case "--v2-latest-parity":
            v2LatestParity = true;
            break;
        case "--v2-read-preview":
            v2ReadPreview = true;
            break;
        case "--v2-readiness":
            v2Readiness = true;
            break;
        case "--v2-backfill":
            v2Backfill = true;
            break;
        case "--v2-backfill-execute":
            v2Backfill = true;
            v2BackfillExecute = true;
            break;
        case "--synchronous-commit-off":
            synchronousCommitOff = true;
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

if (!scrapeId.HasValue && !v2Backfill)
    return Fail("--scrape-id is required unless --v2-backfill is used");

if ((execute || process) && !scrapeId.HasValue)
    return Fail("--scrape-id is required with --execute or --process");

if (maxAttempts <= 0)
    return Fail("--max-attempts must be greater than zero");

if (retryDelaySeconds < 0)
    return Fail("--retry-delay-seconds must be zero or greater");

if (commandTimeoutSeconds < 0)
    return Fail("--command-timeout-seconds must be zero or greater");

if (sampleLimit < 0)
    return Fail("--sample-limit must be zero or greater");

if (days <= 0)
    return Fail("--days must be greater than zero");

try
{
    rankingScope = NormalizeRankingScope(rankingScope);
}
catch (ArgumentException ex)
{
    return Fail(ex.Message);
}

if (parity && !snapshotDate.HasValue)
    return Fail("--snapshot-date is required with --parity");

if ((v2Coverage || v2ValueParity || v2LatestParity || v2Readiness) && !snapshotDate.HasValue)
    return Fail("--snapshot-date is required with --v2-coverage, --v2-value-parity, --v2-latest-parity, or --v2-readiness");

if (v2ReadPreview && string.IsNullOrWhiteSpace(teamKey))
    return Fail("--team-key is required with --v2-read-preview");

if (v2Readiness && string.IsNullOrWhiteSpace(teamKey))
    return Fail("--team-key is required with --v2-readiness so read-switch truncation risk is checked");

if (v2Readiness && string.IsNullOrWhiteSpace(rankingScope))
    return Fail("--scope overall|combo is required with --v2-readiness so coverage/latest and read preview use the same API read shape");

if ((v2ReadPreview || v2Readiness) && string.Equals(rankingScope, "combo", StringComparison.OrdinalIgnoreCase) && string.IsNullOrWhiteSpace(comboId))
    return Fail("--combo-id is required when --scope combo is used with V2 read-preview/readiness");

if ((v2ReadPreview || v2Readiness) && string.Equals(rankingScope, "overall", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(comboId))
    return Fail("--combo-id cannot be used with --scope overall in V2 read-preview/readiness");

if ((execute || process) && !allowProd)
    return Fail("--allow-prod is required with --execute or --process");

if (v2BackfillExecute && !allowProd)
    return Fail("--allow-prod is required with --v2-backfill-execute");

if (v2Backfill && snapshotDate.HasValue)
{
    backfillStartDate ??= snapshotDate;
    backfillEndDate ??= snapshotDate;
}

if (v2Backfill && backfillStartDate.HasValue && backfillEndDate.HasValue && backfillStartDate.Value > backfillEndDate.Value)
    return Fail("--backfill-start-date must be on or before --backfill-end-date");

var bandTypes = ParseBandTypes(bandTypesArg);
var target = new NpgsqlConnectionStringBuilder(pg);
Console.WriteLine($"Target: host={target.Host} database={target.Database} user={target.Username}");
Console.WriteLine($"Mode: {DescribeMode(execute, process, parity, v2Backfill, v2BackfillExecute)}");
Console.WriteLine($"Scrape id: {(scrapeId.HasValue ? scrapeId.Value.ToString("N0") : "none")}");
Console.WriteLine($"Snapshot date: {(snapshotDate.HasValue ? snapshotDate.Value.ToString("yyyy-MM-dd") : "any")}");
Console.WriteLine($"Backfill date range: {DescribeDateRange(backfillStartDate, backfillEndDate)}");
Console.WriteLine($"Band types: {string.Join(", ", bandTypes)}");
Console.WriteLine($"Parity scope: {(string.IsNullOrWhiteSpace(rankingScope) ? "all" : rankingScope)}");
Console.WriteLine($"Parity combo id: {(comboId is null ? "all" : comboId)}");
Console.WriteLine($"Team key: {(string.IsNullOrWhiteSpace(teamKey) ? "none" : teamKey)}");
Console.WriteLine($"Parity sample limit: {sampleLimit:N0}");
Console.WriteLine($"Read preview days: {days:N0}");
Console.WriteLine($"Max attempts: {maxAttempts:N0}");
Console.WriteLine($"Retry delay seconds: {retryDelaySeconds:N0}");
Console.WriteLine($"Command timeout seconds: {commandTimeoutSeconds:N0}");
Console.WriteLine($"Synchronous commit off: {synchronousCommitOff}");
Console.WriteLine($"Write mode: {writeMode}");
Console.WriteLine($"V2 reports: {DescribeV2Reports(v2Coverage, v2ValueParity, v2LatestParity, v2ReadPreview, v2Readiness, v2Backfill, v2BackfillExecute)}");

await using var dataSource = NpgsqlDataSource.Create(pg);
var capturedAt = DateTime.UtcNow;
IReadOnlyList<BandRankHistoryRetryJob> before = [];
if (scrapeId.HasValue)
{
    before = await LoadJobsAsync(dataSource, scrapeId.Value, snapshotDate, bandTypes, maxAttempts, TimeSpan.FromSeconds(retryDelaySeconds));
    PrintJobs("Before", before);
}

BandRankHistoryRetryExecution? execution = null;
IReadOnlyList<BandRankHistoryProcessResult>? processing = null;
IReadOnlyList<BandRankHistoryParityReport>? parityReports = null;
IReadOnlyList<BandRankHistoryV2ParitySummary>? v2CoverageReports = null;
IReadOnlyList<BandRankHistoryV2ParitySummary>? v2ValueParityReports = null;
IReadOnlyList<BandRankHistoryV2LatestParitySummary>? v2LatestParityReports = null;
IReadOnlyList<BandRankHistoryV2ReadPreview>? v2ReadPreviewReports = null;
IReadOnlyList<BandRankHistoryV2ReadinessReport>? v2ReadinessReports = null;
IReadOnlyList<BandRankHistoryV2BackfillResult>? v2BackfillReports = null;
IReadOnlyList<BandRankHistoryRetryJob> after = before;
if (execute)
{
    execution = await QueueRetryAsync(dataSource, before.Where(static job => job.EligibleForRetry).Select(static job => job.JobId).ToArray());
    Console.WriteLine();
    Console.WriteLine($"Queued retry jobs: {execution.JobsQueued:N0}");
    Console.WriteLine($"Reset retry chunks: {execution.ChunksQueued:N0}");
    after = await LoadJobsAsync(dataSource, scrapeId.GetValueOrDefault(), snapshotDate, bandTypes, maxAttempts, TimeSpan.FromSeconds(retryDelaySeconds));
    PrintJobs("After", after);
}

if (process)
{
    processing = ProcessJobs(dataSource, after, maxAttempts, TimeSpan.FromSeconds(retryDelaySeconds), commandTimeoutSeconds, writeMode);
    after = await LoadJobsAsync(dataSource, scrapeId.GetValueOrDefault(), snapshotDate, bandTypes, maxAttempts, TimeSpan.FromSeconds(retryDelaySeconds));
    PrintJobs("After processing", after);
}

if (parity)
{
    parityReports = RunParity(dataSource, bandTypes, snapshotDate!.Value, rankingScope, comboId, sampleLimit);
    PrintParity(parityReports);
}

if (v2Coverage)
{
    v2CoverageReports = RunV2Coverage(dataSource, bandTypes, snapshotDate!.Value, rankingScope, comboId);
    PrintV2Coverage(v2CoverageReports);
}

if (v2ValueParity)
{
    v2ValueParityReports = RunV2ValueParity(dataSource, bandTypes, snapshotDate!.Value, rankingScope, comboId, sampleLimit);
    PrintV2ValueParity(v2ValueParityReports);
}

if (v2LatestParity)
{
    v2LatestParityReports = RunV2LatestParity(dataSource, bandTypes, snapshotDate!.Value, rankingScope, comboId, sampleLimit);
    PrintV2LatestParity(v2LatestParityReports);
}

if (v2ReadPreview)
{
    v2ReadPreviewReports = RunV2ReadPreview(dataSource, bandTypes, teamKey!, comboId, days);
    PrintV2ReadPreview(v2ReadPreviewReports);
}

if (v2Readiness)
{
    v2ReadinessReports = RunV2Readiness(
        dataSource,
        bandTypes,
        snapshotDate!.Value,
        rankingScope,
        comboId,
        teamKey,
        days,
        sampleLimit);
    PrintV2Readiness(v2ReadinessReports);
}

if (v2Backfill)
{
    v2BackfillReports = RunV2Backfill(
        dataSource,
        bandTypes,
        backfillStartDate,
        backfillEndDate,
        rankingScope,
        comboId,
        v2BackfillExecute,
        synchronousCommitOff,
        commandTimeoutSeconds);
    PrintV2Backfill(v2BackfillReports);
}

var payload = new
{
    capturedAtUtc = capturedAt.ToString("o"),
    target = new { host = target.Host, database = target.Database, username = target.Username },
    execute,
    v2Backfill,
    v2BackfillExecute,
    parity,
    scrapeId,
    snapshotDate = snapshotDate?.ToString("yyyy-MM-dd"),
    backfillStartDate = backfillStartDate?.ToString("yyyy-MM-dd"),
    backfillEndDate = backfillEndDate?.ToString("yyyy-MM-dd"),
    bandTypes,
    rankingScope,
    comboId,
    teamKey,
    sampleLimit,
    days,
    maxAttempts,
    retryDelaySeconds,
    writeMode = writeMode.ToString(),
    before,
    execution,
    processing,
    parityReports,
    v2CoverageReports,
    v2ValueParityReports,
    v2LatestParityReports,
    v2ReadPreviewReports,
    v2ReadinessReports,
    v2BackfillReports,
    after = execute || process ? after : null,
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
          BandRankHistoryHarness --pg <connection-string> --scrape-id <id> [--snapshot-date yyyy-mm-dd] [--band-types <csv>] [--max-attempts <n>] [--retry-delay-seconds <n>] [--out <path>]
          BandRankHistoryHarness --pg-env <env-var-name> --scrape-id <id> [--snapshot-date yyyy-mm-dd] [--band-types <csv>] [--max-attempts <n>] [--retry-delay-seconds <n>] [--out <path>]
          BandRankHistoryHarness --pg <connection-string> --scrape-id <id> --execute --allow-prod [--snapshot-date yyyy-mm-dd] [--band-types <csv>] [--max-attempts <n>] [--retry-delay-seconds <n>] [--out <path>]
                    BandRankHistoryHarness --pg <connection-string> --scrape-id <id> --process --allow-prod [--snapshot-date yyyy-mm-dd] [--band-types <csv>] [--max-attempts <n>] [--retry-delay-seconds <n>] [--command-timeout-seconds <n>] [--write-mode Legacy|Dual] [--out <path>]
                    BandRankHistoryHarness --pg <connection-string> --scrape-id <id> --snapshot-date yyyy-mm-dd --parity [--band-types <csv>] [--scope overall|combo] [--combo-id <id>] [--sample-limit <n>] [--out <path>]
                                        BandRankHistoryHarness --pg <connection-string> --scrape-id <id> --snapshot-date yyyy-mm-dd --v2-coverage [--band-types <csv>] [--scope overall|combo] [--combo-id <id>] [--out <path>]
                                        BandRankHistoryHarness --pg <connection-string> --scrape-id <id> --snapshot-date yyyy-mm-dd --v2-value-parity [--band-types <csv>] [--scope overall|combo] [--combo-id <id>] [--sample-limit <n>] [--out <path>]
                                        BandRankHistoryHarness --pg <connection-string> --scrape-id <id> --snapshot-date yyyy-mm-dd --v2-latest-parity [--band-types <csv>] [--scope overall|combo] [--combo-id <id>] [--sample-limit <n>] [--out <path>]
                                        BandRankHistoryHarness --pg <connection-string> --scrape-id <id> --v2-read-preview --team-key <team-key> [--band-types <csv>] [--combo-id <id>] [--days <n>] [--out <path>]
                                        BandRankHistoryHarness --pg <connection-string> --scrape-id <id> --snapshot-date yyyy-mm-dd --v2-readiness --team-key <team-key> [--band-types <csv>] [--scope overall|combo] [--combo-id <id>] [--days <n>] [--sample-limit <n>] [--out <path>]
                                        BandRankHistoryHarness --pg <connection-string> --v2-backfill [--v2-backfill-execute --allow-prod] [--backfill-start-date yyyy-mm-dd] [--backfill-end-date yyyy-mm-dd] [--snapshot-date yyyy-mm-dd] [--band-types <csv>] [--scope overall|combo] [--combo-id <id>] [--command-timeout-seconds <n>] [--synchronous-commit-off] [--out <path>]

        Notes:
          - Default mode is dry-run/read-only.
          - Execute mode only queues eligible failed jobs; it preserves complete chunks and resets failed/running chunks to queued.
                    - Process mode runs matching queued/paused jobs and eligible failed jobs directly in this harness, without starting ScraperWorker.
          - Process mode defaults to --write-mode Legacy; Dual enables v2 shadow writes for the processed chunks.
                    - Parity mode is read-only and does not require --allow-prod. It compares legacy wide history to legacy narrow points, and separately reports legacy narrow vs v2 when v2 rows exist.
                    - V2 report modes are read-only and do not require --allow-prod. They are migration proof reports, not API read-source switches.
                    - V2 read-preview shows the current V2 fallback truncation risk for one --team-key; it does not change appsettings or service options.
                      - V2 backfill dry-run is read-only except schema ensure. --v2-backfill-execute copies legacy narrow rows into V2 with idempotent inserts and monotonic latest upserts.
          - Jobs are eligible when status=failed, attempts < max attempts, and updated_at is older than retry delay.
          - If --pg and --pg-env are omitted, PG_CONN is used.
        """);
}

            static string DescribeMode(bool execute, bool process, bool parity, bool v2Backfill, bool v2BackfillExecute) => (execute, process, parity, v2Backfill, v2BackfillExecute) switch
{
                (_, _, _, true, true) => "v2 backfill execute",
                (_, _, _, true, false) => "v2 backfill dry-run/read-only",
                (true, true, true, false, false) => "execute retry queue + process jobs + parity report",
                (true, true, false, false, false) => "execute retry queue + process jobs",
                (true, false, true, false, false) => "execute retry queue + parity report",
                (true, false, false, false, false) => "execute retry queue",
                (false, true, true, false, false) => "process jobs + parity report",
                (false, true, false, false, false) => "process jobs",
                (false, false, true, false, false) => "parity report/read-only",
    _ => "dry-run/read-only",
};

            static string DescribeDateRange(DateOnly? startDate, DateOnly? endDate) => (startDate, endDate) switch
            {
                (null, null) => "all missing dates",
                ({ } start, null) => $"{start:yyyy-MM-dd}..max",
                (null, { } end) => $"min..{end:yyyy-MM-dd}",
                ({ } start, { } end) when start == end => start.ToString("yyyy-MM-dd"),
                ({ } start, { } end) => $"{start:yyyy-MM-dd}..{end:yyyy-MM-dd}",
            };

            static string DescribeV2Reports(bool coverage, bool valueParity, bool latestParity, bool readPreview, bool readiness, bool backfill, bool backfillExecute)
{
    var reports = new List<string>();
    if (coverage)
        reports.Add("coverage");
    if (valueParity)
        reports.Add("value-parity");
    if (latestParity)
        reports.Add("latest-parity");
    if (readPreview)
        reports.Add("read-preview");
    if (readiness)
        reports.Add("readiness");
    if (backfill)
        reports.Add(backfillExecute ? "backfill-execute" : "backfill-dry-run");

    return reports.Count == 0 ? "none" : string.Join(", ", reports);
}

static async Task<IReadOnlyList<BandRankHistoryRetryJob>> LoadJobsAsync(
    NpgsqlDataSource dataSource,
    long scrapeId,
    DateOnly? snapshotDate,
    IReadOnlyList<string> bandTypes,
    int maxAttempts,
    TimeSpan retryDelay)
{
    await using var conn = await dataSource.OpenConnectionAsync();
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = $$"""
        SELECT job.job_id,
               job.scrape_id,
               job.snapshot_date,
               job.band_type,
               job.mode,
               job.status,
               job.started_at,
               job.completed_at,
               job.failed_at,
               job.paused_at,
               job.superseded_at,
               job.last_error,
               job.attempts,
               job.chunks_total,
               job.chunks_completed,
               job.rows_scanned,
               job.rows_inserted,
               job.rows_skipped,
               job.current_ranking_scope,
               job.current_combo_id,
               job.updated_at,
               COALESCE(chunks.total_chunks, 0)::int,
               COALESCE(chunks.queued_chunks, 0)::int,
               COALESCE(chunks.running_chunks, 0)::int,
               COALESCE(chunks.complete_chunks, 0)::int,
               COALESCE(chunks.failed_chunks, 0)::int,
               COALESCE(chunks.rows_scanned, 0)::bigint,
               COALESCE(chunks.rows_inserted, 0)::bigint,
               COALESCE(chunks.rows_skipped, 0)::bigint
        FROM band_rank_history_jobs job
        LEFT JOIN LATERAL (
            SELECT count(*) AS total_chunks,
                   count(*) FILTER (WHERE status = 'queued') AS queued_chunks,
                   count(*) FILTER (WHERE status = 'running') AS running_chunks,
                   count(*) FILTER (WHERE status = 'complete') AS complete_chunks,
                   count(*) FILTER (WHERE status = 'failed') AS failed_chunks,
                   sum(rows_scanned) AS rows_scanned,
                   sum(rows_inserted) AS rows_inserted,
                   sum(rows_skipped) AS rows_skipped
            FROM band_rank_history_job_chunks chunk
            WHERE chunk.job_id = job.job_id
        ) chunks ON TRUE
        WHERE job.scrape_id = @scrapeId
          AND job.band_type = ANY(@bandTypes)
          {{(snapshotDate.HasValue ? "AND job.snapshot_date = @snapshotDate" : string.Empty)}}
        ORDER BY job.band_type, job.job_id;
        """;
    cmd.Parameters.AddWithValue("scrapeId", scrapeId);
    cmd.Parameters.AddWithValue("bandTypes", bandTypes.ToArray());
    if (snapshotDate.HasValue)
        cmd.Parameters.AddWithValue("snapshotDate", snapshotDate.Value);

    var jobs = new List<BandRankHistoryRetryJob>();
    await using var reader = await cmd.ExecuteReaderAsync();
    while (await reader.ReadAsync())
    {
        var status = reader.GetString(5);
        var attempts = reader.GetInt32(12);
        var updatedAt = reader.GetDateTime(20);
        var eligible = IsEligible(status, attempts, updatedAt, maxAttempts, retryDelay, out var reason);
        jobs.Add(new BandRankHistoryRetryJob(
            reader.GetInt64(0),
            reader.GetInt64(1),
            DateOnly.FromDateTime(reader.GetDateTime(2)).ToString("yyyy-MM-dd"),
            reader.GetString(3),
            reader.GetString(4),
            status,
            ReadDateTime(reader, 6),
            ReadDateTime(reader, 7),
            ReadDateTime(reader, 8),
            ReadDateTime(reader, 9),
            ReadDateTime(reader, 10),
            reader.IsDBNull(11) ? null : reader.GetString(11),
            attempts,
            reader.GetInt32(13),
            reader.GetInt32(14),
            reader.GetInt64(15),
            reader.GetInt64(16),
            reader.GetInt64(17),
            reader.IsDBNull(18) ? null : reader.GetString(18),
            reader.IsDBNull(19) ? null : reader.GetString(19),
            updatedAt.ToString("o"),
            reader.GetInt32(21),
            reader.GetInt32(22),
            reader.GetInt32(23),
            reader.GetInt32(24),
            reader.GetInt32(25),
            reader.GetInt64(26),
            reader.GetInt64(27),
            reader.GetInt64(28),
            eligible,
            reason));
    }

    return jobs;
}

static async Task<BandRankHistoryRetryExecution> QueueRetryAsync(NpgsqlDataSource dataSource, long[] jobIds)
{
    if (jobIds.Length == 0)
        return new BandRankHistoryRetryExecution(0, 0);

    const string reason = "Manual retry queued by BandRankHistoryHarness.";
    await using var conn = await dataSource.OpenConnectionAsync();
    await using var tx = await conn.BeginTransactionAsync();

    await using var jobs = conn.CreateCommand();
    jobs.Transaction = tx;
    jobs.CommandText = """
        UPDATE band_rank_history_jobs
        SET status = 'queued',
            failed_at = NULL,
            paused_at = NULL,
            completed_at = NULL,
            current_ranking_scope = NULL,
            current_combo_id = NULL,
            last_error = @reason,
            updated_at = now()
        WHERE job_id = ANY(@jobIds)
          AND status = 'failed'
        """;
    jobs.Parameters.AddWithValue("jobIds", jobIds);
    jobs.Parameters.AddWithValue("reason", reason);
    var jobsQueued = await jobs.ExecuteNonQueryAsync();

    await using var chunks = conn.CreateCommand();
    chunks.Transaction = tx;
    chunks.CommandText = """
        UPDATE band_rank_history_job_chunks
        SET status = 'queued',
            last_error = @reason,
            updated_at = now()
        WHERE job_id = ANY(@jobIds)
          AND status IN ('failed', 'running')
        """;
    chunks.Parameters.AddWithValue("jobIds", jobIds);
    chunks.Parameters.AddWithValue("reason", reason);
    var chunksQueued = await chunks.ExecuteNonQueryAsync();

    await tx.CommitAsync();
    return new BandRankHistoryRetryExecution(jobsQueued, chunksQueued);
}

static IReadOnlyList<BandRankHistoryProcessResult> ProcessJobs(
    NpgsqlDataSource dataSource,
    IReadOnlyList<BandRankHistoryRetryJob> jobs,
    int maxAttempts,
    TimeSpan retryDelay,
    int commandTimeoutSeconds,
    BandRankHistoryWriteMode writeMode)
{
    var metaDb = new MetaDatabase(dataSource, NullLogger<MetaDatabase>.Instance);
    var results = new List<BandRankHistoryProcessResult>();
    var options = new BandRankHistorySnapshotOptions { CommandTimeoutSeconds = commandTimeoutSeconds, WriteMode = writeMode };

    foreach (var job in jobs)
    {
        if (!IsProcessCandidate(job, maxAttempts, retryDelay, out var reason))
        {
            results.Add(BandRankHistoryProcessResult.Skipped(job.JobId, job.BandType, reason));
            continue;
        }

        Console.WriteLine();
        Console.WriteLine($"Processing job {job.JobId:N0} {job.BandType}: status={job.Status} attempts={job.Attempts:N0} chunks={job.CompleteChunks:N0}/{job.TotalChunks:N0}");
        if (!metaDb.TryStartBandRankHistoryJob(job.JobId, maxAttempts))
        {
            results.Add(BandRankHistoryProcessResult.Skipped(job.JobId, job.BandType, "TryStartBandRankHistoryJob returned false"));
            Console.WriteLine("  skipped: TryStartBandRankHistoryJob returned false");
            continue;
        }

        var startedAt = DateTime.UtcNow;
        try
        {
            var result = metaDb.SnapshotBandRankHistoryChunked(job.BandType, options, job.JobId, CancellationToken.None);
            metaDb.CompleteBandRankHistoryJob(job.JobId, result);
            var elapsed = DateTime.UtcNow - startedAt;
            Console.WriteLine($"  complete in {elapsed}: chunks={result.ChunksCompleted:N0}/{result.ChunksTotal:N0} inserted={result.RowsInserted:N0} skipped={result.RowsSkipped:N0} scanned={result.RowsScanned:N0}");
            results.Add(BandRankHistoryProcessResult.Completed(job.JobId, job.BandType, elapsed, result));
        }
        catch (Exception ex)
        {
            metaDb.FailBandRankHistoryJob(job.JobId, ex.Message);
            var elapsed = DateTime.UtcNow - startedAt;
            Console.WriteLine($"  failed in {elapsed}: {ex.GetType().Name}: {ex.Message}");
            results.Add(BandRankHistoryProcessResult.Failed(job.JobId, job.BandType, elapsed, ex));
        }
    }

    return results;
}

static bool IsProcessCandidate(BandRankHistoryRetryJob job, int maxAttempts, TimeSpan retryDelay, out string reason)
{
    if (string.Equals(job.Status, "queued", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(job.Status, "paused", StringComparison.OrdinalIgnoreCase))
    {
        reason = "queued or paused";
        return true;
    }

    if (string.Equals(job.Status, "failed", StringComparison.OrdinalIgnoreCase))
        return IsEligible(job.Status, job.Attempts, DateTime.Parse(job.UpdatedAt), maxAttempts, retryDelay, out reason);

    reason = $"status is {job.Status}";
    return false;
}

static bool IsEligible(string status, int attempts, DateTime updatedAt, int maxAttempts, TimeSpan retryDelay, out string reason)
{
    if (!string.Equals(status, "failed", StringComparison.OrdinalIgnoreCase))
    {
        reason = $"status is {status}";
        return false;
    }

    if (attempts >= maxAttempts)
    {
        reason = $"attempts {attempts:N0} >= max {maxAttempts:N0}";
        return false;
    }

    var eligibleAt = updatedAt.ToUniversalTime() + retryDelay;
    if (DateTime.UtcNow < eligibleAt)
    {
        reason = $"retry delay not elapsed; eligible at {eligibleAt:o}";
        return false;
    }

    reason = "eligible";
    return true;
}

static string? ReadDateTime(NpgsqlDataReader reader, int ordinal) =>
    reader.IsDBNull(ordinal) ? null : reader.GetDateTime(ordinal).ToString("o");

static void PrintJobs(string title, IReadOnlyList<BandRankHistoryRetryJob> jobs)
{
    Console.WriteLine();
    Console.WriteLine(title);
    if (jobs.Count == 0)
    {
        Console.WriteLine("  no matching jobs");
        return;
    }

    foreach (var job in jobs)
    {
        Console.WriteLine($"  job {job.JobId:N0} {job.BandType} scrape={job.ScrapeId:N0} date={job.SnapshotDate} status={job.Status} attempts={job.Attempts:N0} chunks={job.CompleteChunks:N0}/{job.TotalChunks:N0} failedChunks={job.FailedChunks:N0} eligible={job.EligibleForRetry} ({job.EligibilityReason})");
        if (!string.IsNullOrWhiteSpace(job.LastError))
            Console.WriteLine($"    error: {job.LastError}");
    }
}

static IReadOnlyList<BandRankHistoryParityReport> RunParity(
    NpgsqlDataSource dataSource,
    IReadOnlyList<string> bandTypes,
    DateOnly snapshotDate,
    string? rankingScope,
    string? comboId,
    int sampleLimit)
{
    using var metaDb = new MetaDatabase(dataSource, NullLogger<MetaDatabase>.Instance);
    var hasV2Points = TableExists(dataSource, "band_team_rank_history_points_v2");
    return bandTypes
        .Select(bandType => new BandRankHistoryParityReport(
            bandType,
            metaDb.GetBandRankHistoryWideNarrowParity(bandType, snapshotDate, rankingScope, comboId, sampleLimit, ensureSchema: false),
            hasV2Points ? metaDb.GetBandRankHistoryV2Parity(bandType, snapshotDate, rankingScope, comboId, sampleLimit, ensureSchema: false) : null))
        .ToList();
}

static bool TableExists(NpgsqlDataSource dataSource, string tableName)
{
    using var conn = dataSource.OpenConnection();
    using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT to_regclass(@tableName) IS NOT NULL";
    cmd.Parameters.AddWithValue("tableName", $"public.{tableName}");
    return Convert.ToBoolean(cmd.ExecuteScalar() ?? false);
}

static void PrintParity(IReadOnlyList<BandRankHistoryParityReport> reports)
{
    Console.WriteLine();
    Console.WriteLine("Parity");
    foreach (var report in reports)
    {
        var wideNarrow = report.WideNarrow;
        var wideNarrowPass = wideNarrow.MissingFromNarrow == 0 &&
            wideNarrow.MissingFromWide == 0 &&
            wideNarrow.ValueMismatches == 0;

        Console.WriteLine(
            $"  {report.BandType} wide<->narrow: {(wideNarrowPass ? "PASS" : "FAIL")} " +
            $"wide={wideNarrow.WideRows:N0} narrow={wideNarrow.NarrowRows:N0} matching={wideNarrow.MatchingRows:N0} " +
            $"missingNarrow={wideNarrow.MissingFromNarrow:N0} missingWide={wideNarrow.MissingFromWide:N0} valueMismatches={wideNarrow.ValueMismatches:N0}");

        foreach (var sample in wideNarrow.Samples.Take(5))
        {
            var columns = sample.MismatchedColumns.Count == 0 ? "-" : string.Join(",", sample.MismatchedColumns);
            Console.WriteLine($"    sample {sample.MismatchKind}: {sample.RankingScope}/{sample.ComboId}/{sample.TeamKey} columns={columns}");
        }

        var v2 = report.LegacyNarrowV2;
        if (v2 is null)
        {
            Console.WriteLine($"  {report.BandType} narrow<->v2: v2 table is absent");
        }
        else if (v2.V2Rows > 0)
        {
            var v2Pass = v2.MissingFromV2 == 0 && v2.MissingFromLegacy == 0 && v2.ValueMismatches == 0;
            Console.WriteLine(
                $"  {report.BandType} narrow<->v2: {(v2Pass ? "PASS" : "FAIL")} " +
                $"legacy={v2.LegacyRows:N0} v2={v2.V2Rows:N0} matching={v2.MatchingRows:N0} " +
                $"missingV2={v2.MissingFromV2:N0} missingLegacy={v2.MissingFromLegacy:N0} valueMismatches={v2.ValueMismatches:N0}");
            PrintSamples(v2.Samples, indent: "    ");
        }
        else
        {
            Console.WriteLine($"  {report.BandType} narrow<->v2: no v2 rows for this snapshot/scope");
        }
    }
}

static IReadOnlyList<BandRankHistoryV2ParitySummary> RunV2Coverage(
    NpgsqlDataSource dataSource,
    IReadOnlyList<string> bandTypes,
    DateOnly snapshotDate,
    string? rankingScope,
    string? comboId)
{
    using var metaDb = new MetaDatabase(dataSource, NullLogger<MetaDatabase>.Instance);
    return bandTypes
        .Select(bandType => metaDb.GetBandRankHistoryV2Parity(bandType, snapshotDate, rankingScope, comboId, sampleLimit: 0, ensureSchema: false))
        .ToList();
}

static IReadOnlyList<BandRankHistoryV2ParitySummary> RunV2ValueParity(
    NpgsqlDataSource dataSource,
    IReadOnlyList<string> bandTypes,
    DateOnly snapshotDate,
    string? rankingScope,
    string? comboId,
    int sampleLimit)
{
    using var metaDb = new MetaDatabase(dataSource, NullLogger<MetaDatabase>.Instance);
    return bandTypes
        .Select(bandType => metaDb.GetBandRankHistoryV2Parity(bandType, snapshotDate, rankingScope, comboId, sampleLimit, ensureSchema: false))
        .ToList();
}

static IReadOnlyList<BandRankHistoryV2LatestParitySummary> RunV2LatestParity(
    NpgsqlDataSource dataSource,
    IReadOnlyList<string> bandTypes,
    DateOnly snapshotDate,
    string? rankingScope,
    string? comboId,
    int sampleLimit)
{
    using var metaDb = new MetaDatabase(dataSource, NullLogger<MetaDatabase>.Instance);
    return bandTypes
        .Select(bandType => metaDb.GetBandRankHistoryV2LatestParity(bandType, snapshotDate, rankingScope, comboId, sampleLimit, ensureSchema: false))
        .ToList();
}

static IReadOnlyList<BandRankHistoryV2ReadPreview> RunV2ReadPreview(
    NpgsqlDataSource dataSource,
    IReadOnlyList<string> bandTypes,
    string teamKey,
    string? comboId,
    int days)
{
    using var metaDb = new MetaDatabase(dataSource, NullLogger<MetaDatabase>.Instance);
    return bandTypes
        .Select(bandType => metaDb.GetBandRankHistoryV2ReadPreview(bandType, teamKey, comboId, days, ensureSchema: false))
        .ToList();
}

static IReadOnlyList<BandRankHistoryV2ReadinessReport> RunV2Readiness(
    NpgsqlDataSource dataSource,
    IReadOnlyList<string> bandTypes,
    DateOnly snapshotDate,
    string? rankingScope,
    string? comboId,
    string? teamKey,
    int days,
    int sampleLimit)
{
    using var metaDb = new MetaDatabase(dataSource, NullLogger<MetaDatabase>.Instance);
    return bandTypes.Select(bandType =>
    {
        var coverage = metaDb.GetBandRankHistoryV2Parity(bandType, snapshotDate, rankingScope, comboId, sampleLimit, ensureSchema: false);
        var latest = metaDb.GetBandRankHistoryV2LatestParity(bandType, snapshotDate, rankingScope, comboId, sampleLimit, ensureSchema: false);
        var preview = string.IsNullOrWhiteSpace(teamKey)
            ? null
            : metaDb.GetBandRankHistoryV2ReadPreview(bandType, teamKey, comboId, days, ensureSchema: false);
        var blockers = BuildV2ReadinessBlockers(coverage, latest, preview);
        return new BandRankHistoryV2ReadinessReport(bandType, snapshotDate.ToString("yyyy-MM-dd"), blockers.Count == 0, blockers, coverage, latest, preview);
    }).ToList();
}

static IReadOnlyList<BandRankHistoryV2BackfillResult> RunV2Backfill(
    NpgsqlDataSource dataSource,
    IReadOnlyList<string> bandTypes,
    DateOnly? startDate,
    DateOnly? endDate,
    string? rankingScope,
    string? comboId,
    bool execute,
    bool synchronousCommitOff,
    int commandTimeoutSeconds)
{
    using var metaDb = new MetaDatabase(dataSource, NullLogger<MetaDatabase>.Instance);
    var options = new BandRankHistoryV2BackfillOptions
    {
        StartDate = startDate,
        EndDate = endDate,
        RankingScope = rankingScope,
        ComboId = comboId,
        Execute = execute,
        SynchronousCommitOff = synchronousCommitOff,
        CommandTimeoutSeconds = commandTimeoutSeconds,
    };

    var results = new List<BandRankHistoryV2BackfillResult>();
    foreach (var bandType in bandTypes)
    {
        Console.WriteLine();
        Console.WriteLine($"V2 backfill {(execute ? "execute" : "dry-run")} {bandType}");
        results.Add(metaDb.BackfillBandRankHistoryV2FromLegacy(bandType, options));
    }

    return results;
}

static IReadOnlyList<string> BuildV2ReadinessBlockers(
    BandRankHistoryV2ParitySummary coverage,
    BandRankHistoryV2LatestParitySummary latest,
    BandRankHistoryV2ReadPreview? preview)
{
    var blockers = new List<string>();
    if (coverage.LegacyRows == 0)
        blockers.Add("legacy narrow has no rows for the selected snapshot/scope");
    if (coverage.V2Rows == 0)
        blockers.Add("v2 has no rows for the selected snapshot/scope");
    if (coverage.CompleteSnapshots == 0)
        blockers.Add("v2 has no complete snapshot metadata row for the selected snapshot/scope");
    if (coverage.IncompleteSnapshots > 0)
        blockers.Add($"{coverage.IncompleteSnapshots:N0} v2 snapshot metadata rows are not complete");
    if (coverage.MissingFromV2 > 0)
        blockers.Add($"{coverage.MissingFromV2:N0} legacy rows are missing from v2");
    if (coverage.MissingFromLegacy > 0)
        blockers.Add($"{coverage.MissingFromLegacy:N0} v2 rows are missing from legacy narrow");
    if (coverage.ValueMismatches > 0)
        blockers.Add($"{coverage.ValueMismatches:N0} v2 rows differ from legacy narrow values");
    if (latest.MissingFromLatest > 0)
        blockers.Add($"{latest.MissingFromLatest:N0} v2 point rows are missing latest rows");
    if (latest.LatestMismatches > 0)
        blockers.Add($"{latest.LatestMismatches:N0} latest rows are stale or have mismatched metadata");
    if (latest.ExtraLatestRowsForSnapshot > 0)
        blockers.Add($"{latest.ExtraLatestRowsForSnapshot:N0} latest rows for the snapshot have no matching v2 point row");
    if (latest.MatchingLatestRows < latest.V2PointRows)
        blockers.Add($"{latest.V2PointRows - latest.MatchingLatestRows:N0} v2 point rows do not match latest metadata for the selected snapshot");
    if (preview is null)
        blockers.Add("read preview was not run, so current V2 fallback truncation risk is unknown");
    if (preview?.CurrentV2FallbackWouldHideLegacyDates == true)
        blockers.Add($"current V2 fallback would hide {preview.LegacyDatesHiddenByCurrentV2Fallback.Count:N0} legacy-only dates for team {preview.TeamKey}");

    return blockers;
}

static void PrintV2Coverage(IReadOnlyList<BandRankHistoryV2ParitySummary> reports)
{
    Console.WriteLine();
    Console.WriteLine("V2 coverage");
    foreach (var report in reports)
    {
        var pass = report.MissingFromV2 == 0 &&
            report.MissingFromLegacy == 0 &&
            report.IncompleteSnapshots == 0;
        var sourceRowsAdvisory = DescribeV2SourceRowsAdvisory(report);
        Console.WriteLine(
            $"  {report.BandType}: {(pass ? "PASS" : "FAIL")} legacy={report.LegacyRows:N0} v2={report.V2Rows:N0} " +
            $"matching={report.MatchingRows:N0} missingV2={report.MissingFromV2:N0} missingLegacy={report.MissingFromLegacy:N0} " +
            $"completeSnapshots={report.CompleteSnapshots:N0} incompleteSnapshots={report.IncompleteSnapshots:N0} " +
            $"v2SourceRows={report.V2SnapshotSourceRows:N0} legacyStatsRows={report.LegacyStatsRows:N0}{sourceRowsAdvisory}");
    }
}

static void PrintV2ValueParity(IReadOnlyList<BandRankHistoryV2ParitySummary> reports)
{
    Console.WriteLine();
    Console.WriteLine("V2 value parity");
    foreach (var report in reports)
    {
        var pass = report.MissingFromV2 == 0 &&
            report.MissingFromLegacy == 0 &&
            report.ValueMismatches == 0 &&
            report.IncompleteSnapshots == 0;
        var sourceRowsAdvisory = DescribeV2SourceRowsAdvisory(report);
        Console.WriteLine(
            $"  {report.BandType}: {(pass ? "PASS" : "FAIL")} legacy={report.LegacyRows:N0} v2={report.V2Rows:N0} " +
            $"matching={report.MatchingRows:N0} missingV2={report.MissingFromV2:N0} missingLegacy={report.MissingFromLegacy:N0} " +
            $"valueMismatches={report.ValueMismatches:N0} completeSnapshots={report.CompleteSnapshots:N0} incompleteSnapshots={report.IncompleteSnapshots:N0} " +
            $"v2SourceRows={report.V2SnapshotSourceRows:N0} legacyStatsRows={report.LegacyStatsRows:N0}{sourceRowsAdvisory}");
        PrintSamples(report.Samples, indent: "    ");
    }
}

static string DescribeV2SourceRowsAdvisory(BandRankHistoryV2ParitySummary report)
{
    if (report.LegacyStatsRows <= 0 || report.V2SnapshotSourceRows >= report.LegacyStatsRows)
        return string.Empty;

    return " sourceRowsAdvisory=chunked-metadata-below-legacy-stats";
}

static void PrintV2LatestParity(IReadOnlyList<BandRankHistoryV2LatestParitySummary> reports)
{
    Console.WriteLine();
    Console.WriteLine("V2 latest parity");
    foreach (var report in reports)
    {
        var pass = report.MissingFromLatest == 0 && report.LatestMismatches == 0 && report.ExtraLatestRowsForSnapshot == 0;
        Console.WriteLine(
            $"  {report.BandType}: {(pass ? "PASS" : "FAIL")} points={report.V2PointRows:N0} latestForSnapshot={report.LatestRowsForSnapshot:N0} " +
            $"matching={report.MatchingLatestRows:N0} missingLatest={report.MissingFromLatest:N0} latestMismatches={report.LatestMismatches:N0} extraLatest={report.ExtraLatestRowsForSnapshot:N0}");
        PrintSamples(report.Samples, indent: "    ");
    }
}

static void PrintV2ReadPreview(IReadOnlyList<BandRankHistoryV2ReadPreview> reports)
{
    Console.WriteLine();
    Console.WriteLine("V2 read preview");
    foreach (var report in reports)
    {
        Console.WriteLine(
            $"  {report.BandType}: legacy={report.LegacyRows:N0} v2Only={report.V2OnlyRows:N0} " +
            $"currentFallback={report.CurrentV2FallbackRows:N0} merged={report.MergedRows:N0} " +
            $"hiddenLegacyDates={report.LegacyDatesHiddenByCurrentV2Fallback.Count:N0}");
        if (report.LegacyDatesHiddenByCurrentV2Fallback.Count > 0)
            Console.WriteLine($"    hidden dates: {string.Join(", ", report.LegacyDatesHiddenByCurrentV2Fallback.Take(10))}");
    }
}

static void PrintV2Readiness(IReadOnlyList<BandRankHistoryV2ReadinessReport> reports)
{
    Console.WriteLine();
    Console.WriteLine("V2 readiness");
    foreach (var report in reports)
    {
        Console.WriteLine($"  {report.BandType}: {(report.Ready ? "READY" : "BLOCKED")}");
        foreach (var blocker in report.Blockers)
            Console.WriteLine($"    blocker: {blocker}");
    }
}

static void PrintV2Backfill(IReadOnlyList<BandRankHistoryV2BackfillResult> reports)
{
    Console.WriteLine();
    Console.WriteLine("V2 backfill");
    foreach (var report in reports)
    {
        Console.WriteLine(
            $"  {report.BandType}: {(report.Execute ? "EXECUTED" : "DRY-RUN")} " +
            $"slices={report.SlicesTotal:N0} legacy={report.LegacyRows:N0} existingV2={report.ExistingV2Rows:N0} " +
            $"missingV2={report.MissingV2Rows:N0} snapshotUpserts={report.SnapshotRowsUpserted:N0} " +
            $"pointInserts={report.PointRowsInserted:N0} latestUpserts={report.LatestRowsUpserted:N0}");

        foreach (var slice in report.Slices.Where(static slice => slice.MissingV2Rows > 0 || slice.SnapshotRowsUpserted > 0 || slice.PointRowsInserted > 0).Take(12))
        {
            Console.WriteLine(
                $"    {slice.SnapshotDate} {slice.RankingScope}/{(string.IsNullOrEmpty(slice.ComboId) ? "overall" : slice.ComboId)} " +
                $"legacy={slice.LegacyRows:N0} existingV2={slice.ExistingV2Rows:N0} missingV2={slice.MissingV2Rows:N0} " +
                $"snapshotUpserts={slice.SnapshotRowsUpserted:N0} pointInserts={slice.PointRowsInserted:N0} latestUpserts={slice.LatestRowsUpserted:N0}");
        }

        var remaining = report.Slices.Count(slice => slice.MissingV2Rows > 0 || slice.SnapshotRowsUpserted > 0 || slice.PointRowsInserted > 0) - 12;
        if (remaining > 0)
            Console.WriteLine($"    ... {remaining:N0} more slices omitted");
    }
}

static void PrintSamples(IReadOnlyList<BandRankHistoryParityMismatchSample> samples, string indent)
{
    foreach (var sample in samples.Take(5))
    {
        var columns = sample.MismatchedColumns.Count == 0 ? "-" : string.Join(",", sample.MismatchedColumns);
        Console.WriteLine($"{indent}sample {sample.MismatchKind}: {sample.RankingScope}/{sample.ComboId}/{sample.TeamKey} columns={columns}");
    }
}

static List<string> ParseBandTypes(string? value)
{
    if (string.IsNullOrWhiteSpace(value))
        return BandInstrumentMapping.AllBandTypes.ToList();

    return value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
        .Select(NormalizeBandType)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToList();
}

static string? NormalizeRankingScope(string? value)
{
    if (string.IsNullOrWhiteSpace(value))
        return null;

    if (string.Equals(value, "overall", StringComparison.OrdinalIgnoreCase))
        return "overall";

    if (string.Equals(value, "combo", StringComparison.OrdinalIgnoreCase))
        return "combo";

    throw new ArgumentException("--scope must be overall or combo");
}

static string NormalizeBandType(string value)
{
    foreach (var bandType in BandInstrumentMapping.AllBandTypes)
    {
        if (string.Equals(value, bandType, StringComparison.OrdinalIgnoreCase))
            return bandType;
    }

    throw new ArgumentException($"Unknown band type '{value}'. Expected one of: {string.Join(", ", BandInstrumentMapping.AllBandTypes)}");
}

static void EmitJson(string? outPath, object payload)
{
    if (string.IsNullOrWhiteSpace(outPath))
        return;

    var fullPath = Path.GetFullPath(outPath);
    Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
    File.WriteAllText(fullPath, JsonSerializer.Serialize(payload, new JsonSerializerOptions
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    }));
    Console.WriteLine($"Wrote {fullPath}");
}

public sealed record BandRankHistoryRetryJob(
    long JobId,
    long ScrapeId,
    string SnapshotDate,
    string BandType,
    string Mode,
    string Status,
    string? StartedAt,
    string? CompletedAt,
    string? FailedAt,
    string? PausedAt,
    string? SupersededAt,
    string? LastError,
    int Attempts,
    int JobChunksTotal,
    int JobChunksCompleted,
    long JobRowsScanned,
    long JobRowsInserted,
    long JobRowsSkipped,
    string? CurrentRankingScope,
    string? CurrentComboId,
    string UpdatedAt,
    int TotalChunks,
    int QueuedChunks,
    int RunningChunks,
    int CompleteChunks,
    int FailedChunks,
    long ChunkRowsScanned,
    long ChunkRowsInserted,
    long ChunkRowsSkipped,
    bool EligibleForRetry,
    string EligibilityReason);

public sealed record BandRankHistoryRetryExecution(int JobsQueued, int ChunksQueued);

public sealed record BandRankHistoryParityReport(
    string BandType,
    BandRankHistoryWideNarrowParitySummary WideNarrow,
    BandRankHistoryV2ParitySummary? LegacyNarrowV2);

public sealed record BandRankHistoryV2ReadinessReport(
    string BandType,
    string SnapshotDate,
    bool Ready,
    IReadOnlyList<string> Blockers,
    BandRankHistoryV2ParitySummary Coverage,
    BandRankHistoryV2LatestParitySummary Latest,
    BandRankHistoryV2ReadPreview? ReadPreview);

public sealed record BandRankHistoryProcessResult(
    long JobId,
    string BandType,
    string Status,
    string Message,
    string? Elapsed,
    int? ChunksCompleted,
    int? ChunksTotal,
    long? RowsScanned,
    long? RowsInserted,
    long? RowsSkipped)
{
    public static BandRankHistoryProcessResult Skipped(long jobId, string bandType, string reason) =>
        new(jobId, bandType, "skipped", reason, null, null, null, null, null, null);

    public static BandRankHistoryProcessResult Completed(long jobId, string bandType, TimeSpan elapsed, BandRankHistorySnapshotResult result) =>
        new(jobId, bandType, "complete", "complete", elapsed.ToString(), result.ChunksCompleted, result.ChunksTotal, result.RowsScanned, result.RowsInserted, result.RowsSkipped);

    public static BandRankHistoryProcessResult Failed(long jobId, string bandType, TimeSpan elapsed, Exception ex) =>
        new(jobId, bandType, "failed", $"{ex.GetType().Name}: {ex.Message}", elapsed.ToString(), null, null, null, null, null);
}