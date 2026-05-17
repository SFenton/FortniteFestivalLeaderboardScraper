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
string? pgEnv = null;
string? outPath = null;
string? songId = null;
string? bandType = null;
string? combo = null;
string? bandTypesArg = null;
string scopeMode = "all";
long? publishGeneration = null;
bool execute = false;
bool allowProd = false;
bool skipSchema = false;
bool clearExisting = false;
bool warmSongBandCache = false;
int timeoutSeconds = 0;
int progressEvery = 100;

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
        case "--song-id":
            songId = args[++i];
            break;
        case "--band-type":
            bandType = args[++i];
            break;
        case "--combo":
            combo = args[++i];
            break;
        case "--band-types":
            bandTypesArg = args[++i];
            break;
        case "--scope-mode":
            scopeMode = args[++i];
            break;
        case "--publish-generation":
            publishGeneration = long.Parse(args[++i]);
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
        case "--warm-song-band-cache":
            warmSongBandCache = true;
            break;
        case "--timeout-seconds":
            timeoutSeconds = int.Parse(args[++i]);
            break;
        case "--progress-every":
            progressEvery = int.Parse(args[++i]);
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

if (publishGeneration.HasValue && !execute)
    return Fail("--publish-generation requires --execute and --allow-prod");

if (publishGeneration is <= 0)
    return Fail("--publish-generation must be greater than zero");

if (clearExisting && !execute)
    return Fail("--clear-existing is only valid with --execute");

if (clearExisting && publishGeneration.HasValue)
    return Fail("--clear-existing is not valid with --publish-generation");

if (warmSongBandCache && string.IsNullOrWhiteSpace(songId))
    return Fail("--warm-song-band-cache requires --song-id");

if (warmSongBandCache && !execute)
    return Fail("--warm-song-band-cache writes api_response_cache and requires --execute --allow-prod");

if (!string.IsNullOrWhiteSpace(combo) && (string.IsNullOrWhiteSpace(songId) || string.IsNullOrWhiteSpace(bandType)))
    return Fail("--combo is only valid with --song-id and --band-type");

if (!string.IsNullOrWhiteSpace(songId) ^ !string.IsNullOrWhiteSpace(bandType))
    return Fail("--song-id and --band-type must be provided together for scoped rebuild");

if (!IsValidScopeMode(scopeMode))
    return Fail("--scope-mode must be all, overall, or combo");

var scoped = !string.IsNullOrWhiteSpace(songId) && !string.IsNullOrWhiteSpace(bandType);
if (scoped && !BandComboIds.IsValidBandType(bandType!))
    return Fail($"Unknown band type: {bandType}");

var bandTypes = ParseBandTypes(bandTypesArg);
var includeOverall = scopeMode.Equals("all", StringComparison.OrdinalIgnoreCase) || scopeMode.Equals("overall", StringComparison.OrdinalIgnoreCase);
var includeCombo = scopeMode.Equals("all", StringComparison.OrdinalIgnoreCase) || scopeMode.Equals("combo", StringComparison.OrdinalIgnoreCase);
var ensureSchema = execute && !skipSchema;
var rankingScope = string.IsNullOrWhiteSpace(combo) ? "overall" : "combo";
var normalizedCombo = string.Empty;
if (scoped && rankingScope == "combo")
{
    var validation = BandComboIds.TryNormalizeForBandType(bandType!, combo);
    if (validation.Error is not null || string.IsNullOrWhiteSpace(validation.ComboId))
        return Fail(validation.Error ?? "Invalid band combo.");
    normalizedCombo = validation.ComboId;
}

var target = new NpgsqlConnectionStringBuilder(pg);
Console.WriteLine($"Target: host={target.Host} database={target.Database} user={target.Username}");
Console.WriteLine($"Mode: {(publishGeneration.HasValue ? scoped ? "publish-scope" : "publish-generation" : execute ? scoped ? "execute-scope" : "execute-full" : "status")}");
Console.WriteLine($"Ensure schema: {ensureSchema}");
Console.WriteLine($"Timeout seconds: {(timeoutSeconds <= 0 ? "unlimited" : timeoutSeconds)}");
if (!scoped)
{
    Console.WriteLine($"Band types: {string.Join(", ", bandTypes)}");
    Console.WriteLine($"Scope mode: {scopeMode}");
}
else
{
    Console.WriteLine($"Scope: {songId}/{bandType}/{rankingScope}{(rankingScope == "combo" ? $"/{normalizedCombo}" : string.Empty)}");
}
if (clearExisting)
    Console.WriteLine("Clear existing projection rows before full rebuild: true");
if (publishGeneration.HasValue)
    Console.WriteLine($"Publish generation: {publishGeneration.Value:N0}");
if (warmSongBandCache)
    Console.WriteLine("Warm song-band cache: true");

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

var builder = new BandCurrentProjectionBuilder(
    dataSource,
    loggerFactory.CreateLogger<BandCurrentProjectionBuilder>());

if (ensureSchema)
    await builder.EnsureSchemaAsync();

var before = builder.Inspect();
PrintStats("Before", before);

var options = new BandCurrentProjectionRebuildOptions
{
    CommandTimeoutSeconds = timeoutSeconds,
    DisableSynchronousCommit = true,
    ClearExisting = clearExisting,
    BandTypes = bandTypes,
    IncludeOverallScopes = includeOverall,
    IncludeComboScopes = includeCombo,
};

BandCurrentProjectionRebuildResult? rebuildResult = null;
BandCurrentProjectionScopeResult? scopeResult = null;
BandCurrentProjectionPublishResult? publishResult = null;
IReadOnlyList<BandCurrentProjectionScopeKey> publishScopes = [];

if (execute && publishGeneration.HasValue)
{
    publishScopes = scoped
        ? [new BandCurrentProjectionScopeKey(songId!, bandType!, rankingScope, normalizedCombo)]
        : await LoadPublishScopesAsync(dataSource, publishGeneration.Value, bandTypes, includeOverall, includeCombo);

    publishResult = await builder.TryPublishGenerationAsync(publishGeneration.Value, publishScopes);
    Console.WriteLine();
    Console.WriteLine($"Published generation {publishGeneration.Value:N0} without rebuilding.");
    Console.WriteLine($"  requested scopes: {publishResult.ScopeCount:N0}");
    Console.WriteLine($"  ready scopes:     {publishResult.ReadyScopes:N0}");
    Console.WriteLine($"  failed scopes:    {publishResult.FailedScopes:N0}");
    Console.WriteLine($"  missing scopes:   {publishResult.MissingScopes:N0}");
    Console.WriteLine($"  published scopes: {publishResult.PublishedScopes:N0}");
    Console.WriteLine($"  published rows:   {publishResult.PublishedRows:N0}");
    Console.WriteLine($"  old rows deleted: {publishResult.DeletedRows:N0}");
}
else if (execute && scoped)
{
    scopeResult = await builder.RebuildScopeAsync(new BandCurrentProjectionScopeKey(songId!, bandType!, rankingScope, normalizedCombo), options);
    Console.WriteLine();
    Console.WriteLine($"Rebuilt scope {scopeResult.SongId}/{scopeResult.BandType}/{scopeResult.RankingScope}/{DisplayCombo(scopeResult.ScopeComboId)} in {scopeResult.ElapsedMs / 1000.0:F2}s");
    Console.WriteLine($"  rows: {scopeResult.DeletedRows:N0} deleted / {scopeResult.InsertedRows:N0} inserted");
    Console.WriteLine($"  generation: {scopeResult.Generation:N0} source: {(scopeResult.SourceScopeExists ? "present" : "missing")}");
}
else if (execute)
{
    rebuildResult = await builder.RebuildAllAsync(options, (completed, total, result) =>
    {
        if (progressEvery <= 0)
            return;
        if (completed == 1 || completed == total || completed % progressEvery == 0)
        {
            Console.WriteLine($"  progress: {completed:N0}/{total:N0} {result.BandType}/{result.RankingScope}/{DisplayCombo(result.ScopeComboId)} rows={result.InsertedRows:N0} elapsed={result.ElapsedMs / 1000.0:F2}s");
        }
    });
    Console.WriteLine();
    Console.WriteLine($"Rebuilt all band current projection scopes in {rebuildResult.TotalElapsedMs / 1000.0:F2}s");
    Console.WriteLine($"  scopes: {rebuildResult.ScopeCount:N0}");
    Console.WriteLine($"  rows: {rebuildResult.DeletedRows:N0} deleted / {rebuildResult.InsertedRows:N0} inserted");
    Console.WriteLine($"  orphaned rows deleted: {rebuildResult.OrphanedRowsDeleted:N0}");
    Console.WriteLine($"  unpublished candidates deleted: {rebuildResult.CandidateRowsDeleted:N0}");
    Console.WriteLine($"  generation: {rebuildResult.Generation:N0}");
    Console.WriteLine($"  publish: {(rebuildResult.PublishResult.Published ? "published" : "not published")} {rebuildResult.PublishResult.PublishedScopes:N0}/{rebuildResult.PublishResult.ScopeCount:N0} scope(s), ready={rebuildResult.PublishResult.ReadyScopes:N0}, failed={rebuildResult.PublishResult.FailedScopes:N0}, missing={rebuildResult.PublishResult.MissingScopes:N0}, rows={rebuildResult.PublishResult.PublishedRows:N0}, oldRowsDeleted={rebuildResult.PublishResult.DeletedRows:N0}");
}
else
{
    Console.WriteLine();
    Console.WriteLine("Status mode only. Re-run with --execute --allow-prod to rebuild the full projection.");
    Console.WriteLine("Add --song-id <id> --band-type <bandType> [--combo <combo>] for a scoped rebuild.");
}

SongBandPreviewCacheWarmResult? cacheWarmResult = null;
if (warmSongBandCache)
{
    cacheWarmResult = WarmSongBandPreviewCache(
        dataSource,
        loggerFactory,
        songId!,
        LeaderboardCacheKeys.SongDetailPreviewTop);
    Console.WriteLine();
    Console.WriteLine($"Warmed {cacheWarmResult.CacheKey}: {cacheWarmResult.JsonBytes:N0} byte(s), total entries {cacheWarmResult.TotalEntries:N0} across {cacheWarmResult.BandCount:N0} band type(s).");
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
    bandTypes,
    scopeMode,
    scope = scoped ? new { songId, bandType, rankingScope, comboId = string.IsNullOrWhiteSpace(normalizedCombo) ? null : normalizedCombo } : null,
    publishGeneration,
    publishScopeCount = publishScopes.Count,
    before,
    after,
    scopeResult,
    publishResult,
    cacheWarmResult,
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
          BandCurrentProjectionHarness --pg <connection-string> [--out <path>]
          BandCurrentProjectionHarness --pg-env <env-var-name> [--out <path>]
          BandCurrentProjectionHarness --pg <connection-string> --execute --allow-prod [--band-types <csv>] [--scope-mode all|overall|combo] [--timeout-seconds <seconds>] [--clear-existing] [--progress-every <count>] [--out <path>]
          BandCurrentProjectionHarness --pg <connection-string> --execute --allow-prod --song-id <song-id> --band-type <band-type> [--combo <combo>] [--timeout-seconds <seconds>] [--warm-song-band-cache] [--out <path>]
                    BandCurrentProjectionHarness --pg <connection-string> --execute --allow-prod --publish-generation <generation> [--band-types <csv>] [--scope-mode all|overall|combo] [--out <path>]
                    BandCurrentProjectionHarness --pg <connection-string> --execute --allow-prod --publish-generation <generation> --song-id <song-id> --band-type <band-type> [--combo <combo>] [--out <path>]

        Notes:
          - Default mode is read-only status/inspection.
          - If --pg and --pg-env are omitted, PG_CONN is used.
          - Writes require both --execute and --allow-prod.
          - Status mode is read-only and does not run schema migrations.
          - Execute mode ensures projection schema unless --skip-schema is provided.
          - Full rebuild uses the same scoped updater as normal incremental projection maintenance.
          - Publish mode advances matching existing ready scopes for one generation without rebuilding rows.
          - Run full rebuild before deploying API code that reads current_band_leaderboard_entries.
          - --warm-song-band-cache rewrites the persisted song-bands-all:{song}:10 API cache row for the scoped song.
        """);
}

static SongBandPreviewCacheWarmResult WarmSongBandPreviewCache(
    NpgsqlDataSource dataSource,
    ILoggerFactory loggerFactory,
    string songId,
    int top)
{
    using var metaDb = new MetaDatabase(
        dataSource,
        loggerFactory.CreateLogger<MetaDatabase>(),
        Options.Create(new BandRankHistoryOptions()));
    var showLeaderboardEntryTotals = metaDb.ShouldShowLeaderboardEntryTotals();
    var bandPayloads = new List<object>();
    var totalEntriesAcrossBands = 0;

    foreach (var bandType in BandInstrumentMapping.AllBandTypes)
    {
        var (entries, totalEntries) = metaDb.GetSongBandLeaderboard(songId, bandType, top, 0);
        totalEntriesAcrossBands += totalEntries;
        var names = metaDb.GetDisplayNames(entries.SelectMany(entry => entry.Members.Select(member => member.AccountId)));
        bandPayloads.Add(new
        {
            bandType,
            count = entries.Count,
            totalEntries,
            localEntries = totalEntries,
            entries = entries.Select(entry => MapSongBandLeaderboardEntry(entry, names)).ToList(),
            selectedPlayerEntry = (object?)null,
            selectedBandEntry = (object?)null,
        });
    }

    var payload = new { songId, showLeaderboardEntryTotals, bands = bandPayloads };
    var jsonBytes = JsonSerializer.SerializeToUtf8Bytes(payload, new JsonSerializerOptions(JsonSerializerDefaults.Web));
    var cacheKey = LeaderboardCacheKeys.SongBandLeaderboardsAll(songId, top);
    metaDb.BulkSetCachedResponses([(cacheKey, jsonBytes, ResponseCacheService.ComputeETag(jsonBytes))]);
    return new SongBandPreviewCacheWarmResult(cacheKey, jsonBytes.Length, bandPayloads.Count, totalEntriesAcrossBands);
}

static object MapSongBandLeaderboardEntry(SongBandLeaderboardEntryDto entry, IReadOnlyDictionary<string, string> names) => new
{
    entry.BandId,
    entry.BandType,
    entry.TeamKey,
    entry.ComboId,
    Members = entry.Members.Select(member => new
    {
        member.AccountId,
        DisplayName = names.GetValueOrDefault(member.AccountId),
        member.Instruments,
        member.Score,
        member.Accuracy,
        member.IsFullCombo,
        member.Stars,
        member.Difficulty,
        member.Season,
    }).ToList(),
    entry.Score,
    entry.Rank,
    entry.Accuracy,
    entry.IsFullCombo,
    entry.Stars,
    entry.Difficulty,
    entry.Season,
    entry.Percentile,
    entry.EndTime,
};

static async Task<IReadOnlyList<BandCurrentProjectionScopeKey>> LoadPublishScopesAsync(
    NpgsqlDataSource dataSource,
    long generation,
    IReadOnlyCollection<string> bandTypes,
    bool includeOverall,
    bool includeCombo)
{
    await using var conn = await dataSource.OpenConnectionAsync();
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = """
        SELECT song_id, band_type, ranking_scope, scope_combo_id
        FROM band_current_projection_scope
        WHERE projection_generation = @generation
          AND band_type = ANY(@bandTypes)
          AND (
              (@includeOverall AND ranking_scope = 'overall')
              OR (@includeCombo AND ranking_scope = 'combo')
          )
        ORDER BY band_type, ranking_scope, scope_combo_id, song_id;
        """;
    cmd.Parameters.AddWithValue("generation", generation);
    cmd.Parameters.AddWithValue("bandTypes", bandTypes.ToArray());
    cmd.Parameters.AddWithValue("includeOverall", includeOverall);
    cmd.Parameters.AddWithValue("includeCombo", includeCombo);

    var scopes = new List<BandCurrentProjectionScopeKey>();
    await using var reader = await cmd.ExecuteReaderAsync();
    while (await reader.ReadAsync())
    {
        scopes.Add(new BandCurrentProjectionScopeKey(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3)));
    }

    return scopes;
}

static List<string> ParseBandTypes(string? value)
{
    if (string.IsNullOrWhiteSpace(value))
        return BandInstrumentMapping.AllBandTypes.ToList();

    var result = new List<string>();
    foreach (var bandType in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
    {
        if (!BandComboIds.IsValidBandType(bandType))
            throw new ArgumentException($"Unknown band type: {bandType}", nameof(value));
        if (!result.Contains(bandType, StringComparer.OrdinalIgnoreCase))
            result.Add(bandType);
    }

    return result;
}

static bool IsValidScopeMode(string value) =>
    value.Equals("all", StringComparison.OrdinalIgnoreCase)
    || value.Equals("overall", StringComparison.OrdinalIgnoreCase)
    || value.Equals("combo", StringComparison.OrdinalIgnoreCase);

static string DisplayCombo(string comboId) => string.IsNullOrWhiteSpace(comboId) ? "overall" : comboId;

static void PrintStats(string title, BandCurrentProjectionStats stats)
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
            Console.WriteLine($"    {scope.BandType}/{scope.RankingScope}/{DisplayCombo(scope.ScopeComboId)}/{scope.SongId}: rows={scope.RowCount:N0} status={scope.Status} gen={scope.ProjectionGeneration:N0} published={FormatNullable(scope.PublishedGeneration)} publishedRows={scope.PublishedRowCount:N0}{error}");
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

public sealed record SongBandPreviewCacheWarmResult(
    string CacheKey,
    int JsonBytes,
    int BandCount,
    int TotalEntries);
