using System.Text.Json;
using System.Text.Json.Serialization;
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
string? bandTypesArg = null;
var maxAttempts = 3;
var retryDelaySeconds = 900;
var commandTimeoutSeconds = 0;
var execute = false;
var process = false;
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
        case "--band-types":
            bandTypesArg = args[++i];
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
        case "--execute":
            execute = true;
            break;
        case "--process":
            process = true;
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

if (!scrapeId.HasValue)
    return Fail("--scrape-id is required");

if (maxAttempts <= 0)
    return Fail("--max-attempts must be greater than zero");

if (retryDelaySeconds < 0)
    return Fail("--retry-delay-seconds must be zero or greater");

if (commandTimeoutSeconds < 0)
    return Fail("--command-timeout-seconds must be zero or greater");

if ((execute || process) && !allowProd)
    return Fail("--allow-prod is required with --execute or --process");

var bandTypes = ParseBandTypes(bandTypesArg);
var target = new NpgsqlConnectionStringBuilder(pg);
Console.WriteLine($"Target: host={target.Host} database={target.Database} user={target.Username}");
Console.WriteLine($"Mode: {DescribeMode(execute, process)}");
Console.WriteLine($"Scrape id: {scrapeId.Value:N0}");
Console.WriteLine($"Snapshot date: {(snapshotDate.HasValue ? snapshotDate.Value.ToString("yyyy-MM-dd") : "any")}");
Console.WriteLine($"Band types: {string.Join(", ", bandTypes)}");
Console.WriteLine($"Max attempts: {maxAttempts:N0}");
Console.WriteLine($"Retry delay seconds: {retryDelaySeconds:N0}");
Console.WriteLine($"Command timeout seconds: {commandTimeoutSeconds:N0}");

await using var dataSource = NpgsqlDataSource.Create(pg);
var capturedAt = DateTime.UtcNow;
var before = await LoadJobsAsync(dataSource, scrapeId.Value, snapshotDate, bandTypes, maxAttempts, TimeSpan.FromSeconds(retryDelaySeconds));
PrintJobs("Before", before);

BandRankHistoryRetryExecution? execution = null;
IReadOnlyList<BandRankHistoryProcessResult>? processing = null;
IReadOnlyList<BandRankHistoryRetryJob> after = before;
if (execute)
{
    execution = await QueueRetryAsync(dataSource, before.Where(static job => job.EligibleForRetry).Select(static job => job.JobId).ToArray());
    Console.WriteLine();
    Console.WriteLine($"Queued retry jobs: {execution.JobsQueued:N0}");
    Console.WriteLine($"Reset retry chunks: {execution.ChunksQueued:N0}");
    after = await LoadJobsAsync(dataSource, scrapeId.Value, snapshotDate, bandTypes, maxAttempts, TimeSpan.FromSeconds(retryDelaySeconds));
    PrintJobs("After", after);
}

if (process)
{
    processing = ProcessJobs(dataSource, after, maxAttempts, TimeSpan.FromSeconds(retryDelaySeconds), commandTimeoutSeconds);
    after = await LoadJobsAsync(dataSource, scrapeId.Value, snapshotDate, bandTypes, maxAttempts, TimeSpan.FromSeconds(retryDelaySeconds));
    PrintJobs("After processing", after);
}

var payload = new
{
    capturedAtUtc = capturedAt.ToString("o"),
    target = new { host = target.Host, database = target.Database, username = target.Username },
    execute,
    scrapeId,
    snapshotDate = snapshotDate?.ToString("yyyy-MM-dd"),
    bandTypes,
    maxAttempts,
    retryDelaySeconds,
    before,
    execution,
    processing,
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
                    BandRankHistoryHarness --pg <connection-string> --scrape-id <id> --process --allow-prod [--snapshot-date yyyy-mm-dd] [--band-types <csv>] [--max-attempts <n>] [--retry-delay-seconds <n>] [--command-timeout-seconds <n>] [--out <path>]

        Notes:
          - Default mode is dry-run/read-only.
          - Execute mode only queues eligible failed jobs; it preserves complete chunks and resets failed/running chunks to queued.
                    - Process mode runs matching queued/paused jobs and eligible failed jobs directly in this harness, without starting ScraperWorker.
          - Jobs are eligible when status=failed, attempts < max attempts, and updated_at is older than retry delay.
          - If --pg and --pg-env are omitted, PG_CONN is used.
        """);
}

static string DescribeMode(bool execute, bool process) => (execute, process) switch
{
        (true, true) => "execute retry queue + process jobs",
        (true, false) => "execute retry queue",
        (false, true) => "process jobs",
        _ => "dry-run/read-only",
};

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
    int commandTimeoutSeconds)
{
    var metaDb = new MetaDatabase(dataSource, NullLogger<MetaDatabase>.Instance);
    var results = new List<BandRankHistoryProcessResult>();
    var options = new BandRankHistorySnapshotOptions { CommandTimeoutSeconds = commandTimeoutSeconds };

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

static List<string> ParseBandTypes(string? value)
{
    if (string.IsNullOrWhiteSpace(value))
        return BandInstrumentMapping.AllBandTypes.ToList();

    return value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
        .Select(NormalizeBandType)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToList();
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