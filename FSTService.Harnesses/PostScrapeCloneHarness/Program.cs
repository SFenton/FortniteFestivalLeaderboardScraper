using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FortniteFestival.Core.Services;
using FSTService;
using FSTService.Api;
using FSTService.Persistence;
using FSTService.Scraping;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using NpgsqlTypes;

if (args.Length == 0 || args.Contains("--help", StringComparer.OrdinalIgnoreCase) || args.Contains("-h", StringComparer.OrdinalIgnoreCase))
{
    PrintUsage();
    return 0;
}

var command = args[0].ToLowerInvariant();
var rest = args.Skip(1).ToArray();

return command switch
{
    "clone" => RunClone(rest),
    "inspect" => RunInspect(rest),
    "compare" => RunCompare(rest),
    "run-rankings" => RunRankings(rest),
    "run-rivals" => RunRivals(rest),
    "run-leaderboard-rivals" => RunLeaderboardRivals(rest),
    "run-player-stats" => RunPlayerStats(rest),
    "run-precompute" => RunPrecompute(rest),
    "run-band-rankings" => RunBandRankings(rest),
    "run-band-extraction" => RunBandExtraction(rest),
    _ => Fail($"Unknown subcommand: {command}")
};

static int RunClone(string[] args)
{
    string? sourcePg = null;
    string? targetPg = null;
    string preset = "post-scrape";
    string? tablesArg = null;
    string? outPath = null;
    string? accountIdsArg = null;
    string? songIdsArg = null;
    string? instrumentsArg = null;
    string? bandTypesArg = null;
    bool resetTarget = true;
    bool ensureSchema = true;

    for (int i = 0; i < args.Length; i++)
    {
        switch (args[i])
        {
            case "--source-pg":
                sourcePg = args[++i];
                break;
            case "--target-pg":
                targetPg = args[++i];
                break;
            case "--preset":
                preset = args[++i];
                break;
            case "--tables":
                tablesArg = args[++i];
                break;
            case "--out":
                outPath = args[++i];
                break;
            case "--account-ids":
                accountIdsArg = args[++i];
                break;
            case "--song-ids":
                songIdsArg = args[++i];
                break;
            case "--instruments":
                instrumentsArg = args[++i];
                break;
            case "--band-types":
                bandTypesArg = args[++i];
                break;
            case "--no-reset":
                resetTarget = false;
                break;
            case "--skip-schema":
                ensureSchema = false;
                break;
        }
    }

    if (string.IsNullOrWhiteSpace(sourcePg))
        return Fail("--source-pg is required");
    if (string.IsNullOrWhiteSpace(targetPg))
        return Fail("--target-pg is required");

    var tables = ResolveTables(preset, tablesArg);
    var filters = CreateFilters(accountIdsArg, songIdsArg, instrumentsArg, bandTypesArg);

    Console.WriteLine($"Source: {FormatConnection(sourcePg!)}");
    Console.WriteLine($"Target: {FormatConnection(targetPg!)}");
    Console.WriteLine($"Preset: {preset}");
    Console.WriteLine($"Tables: {string.Join(", ", tables)}");
    Console.WriteLine($"Filters: {FormatFilters(filters)}");
    Console.WriteLine($"Reset target: {resetTarget}");
    Console.WriteLine($"Ensure schema: {ensureSchema}");

    using var source = NpgsqlDataSource.Create(sourcePg!);
    using var target = NpgsqlDataSource.Create(targetPg!);

    if (ensureSchema)
        DatabaseInitializer.EnsureSchemaAsync(target).GetAwaiter().GetResult();

    ValidateTablesExist(source, tables, label: "source");
    ValidateTablesExist(target, tables, label: "target");

    if (resetTarget)
        TruncateTables(target, tables);

    var totalSw = Stopwatch.StartNew();
    var results = new List<TableCloneSummary>(tables.Count);
    foreach (var table in tables)
    {
        var result = CloneTable(source, target, table, filters);
        results.Add(result);
        Console.WriteLine(
            $"{table,-32} matched={result.SourceRows,12:N0} total={result.TotalSourceRows,12:N0} copied={result.CopiedRows,12:N0} target={result.TargetRows,12:N0} elapsed={result.ElapsedMs,10:F1}ms scope={FormatAppliedFilters(result.AppliedFilters)}");
    }

    ResetKnownSequences(target);
    totalSw.Stop();

    var payload = new CloneRunSummary
    {
        CapturedAtUtc = DateTime.UtcNow.ToString("o"),
        Mode = "clone",
        Preset = preset,
        ResetTarget = resetTarget,
        EnsuredSchema = ensureSchema,
        Filters = filters,
        Source = DescribeConnection(sourcePg!),
        Target = DescribeConnection(targetPg!),
        Tables = tables,
        TotalElapsedMs = Math.Round(totalSw.Elapsed.TotalMilliseconds, 3),
        Results = results,
    };

    EmitJson(outPath, payload);
    return 0;
}

static int RunInspect(string[] args)
{
    string? pg = null;
    string preset = "post-scrape";
    string? tablesArg = null;
    string? outPath = null;
    string? accountIdsArg = null;
    string? songIdsArg = null;
    string? instrumentsArg = null;
    string? bandTypesArg = null;

    for (int i = 0; i < args.Length; i++)
    {
        switch (args[i])
        {
            case "--pg":
                pg = args[++i];
                break;
            case "--preset":
                preset = args[++i];
                break;
            case "--tables":
                tablesArg = args[++i];
                break;
            case "--out":
                outPath = args[++i];
                break;
            case "--account-ids":
                accountIdsArg = args[++i];
                break;
            case "--song-ids":
                songIdsArg = args[++i];
                break;
            case "--instruments":
                instrumentsArg = args[++i];
                break;
            case "--band-types":
                bandTypesArg = args[++i];
                break;
        }
    }

    if (string.IsNullOrWhiteSpace(pg))
        return Fail("--pg is required");

    var tables = ResolveTables(preset, tablesArg);
    var filters = CreateFilters(accountIdsArg, songIdsArg, instrumentsArg, bandTypesArg);

    Console.WriteLine($"Database: {FormatConnection(pg!)}");
    Console.WriteLine($"Preset: {preset}");
    Console.WriteLine($"Tables: {string.Join(", ", tables)}");
    Console.WriteLine($"Filters: {FormatFilters(filters)}");

    using var dataSource = NpgsqlDataSource.Create(pg!);
    ValidateTablesExist(dataSource, tables, label: "inspect");

    var totalSw = Stopwatch.StartNew();
    var results = new List<TableInspectSummary>(tables.Count);
    foreach (var table in tables)
    {
        var columns = GetColumns(dataSource, table);
        var querySpec = BuildTableQuerySpec(columns, filters);
        var sw = Stopwatch.StartNew();
        var totalRows = CountRows(dataSource, table);
        var rows = querySpec.HasFilters ? CountRows(dataSource, table, querySpec) : totalRows;
        sw.Stop();

        results.Add(new TableInspectSummary
        {
            Table = table,
            Rows = rows,
            TotalRows = totalRows,
            ElapsedMs = Math.Round(sw.Elapsed.TotalMilliseconds, 3),
            AppliedFilters = querySpec.AppliedFilters,
        });

        Console.WriteLine(
            $"{table,-32} matched={rows,12:N0} total={totalRows,12:N0} elapsed={sw.Elapsed.TotalMilliseconds,10:F1}ms scope={FormatAppliedFilters(querySpec.AppliedFilters)}");
    }
    totalSw.Stop();

    var payload = new
    {
        capturedAtUtc = DateTime.UtcNow.ToString("o"),
        mode = "inspect",
        preset,
        filters,
        database = DescribeConnection(pg!),
        tables,
        totalElapsedMs = Math.Round(totalSw.Elapsed.TotalMilliseconds, 3),
        results,
    };

    EmitJson(outPath, payload);
    return 0;
}

static int RunCompare(string[] args)
{
    string? sourcePg = null;
    string? targetPg = null;
    string preset = "post-scrape";
    string? tablesArg = null;
    string? outPath = null;
    string? accountIdsArg = null;
    string? songIdsArg = null;
    string? instrumentsArg = null;
    string? bandTypesArg = null;

    for (int i = 0; i < args.Length; i++)
    {
        switch (args[i])
        {
            case "--source-pg":
                sourcePg = args[++i];
                break;
            case "--target-pg":
                targetPg = args[++i];
                break;
            case "--preset":
                preset = args[++i];
                break;
            case "--tables":
                tablesArg = args[++i];
                break;
            case "--out":
                outPath = args[++i];
                break;
            case "--account-ids":
                accountIdsArg = args[++i];
                break;
            case "--song-ids":
                songIdsArg = args[++i];
                break;
            case "--instruments":
                instrumentsArg = args[++i];
                break;
            case "--band-types":
                bandTypesArg = args[++i];
                break;
        }
    }

    if (string.IsNullOrWhiteSpace(sourcePg))
        return Fail("--source-pg is required");
    if (string.IsNullOrWhiteSpace(targetPg))
        return Fail("--target-pg is required");

    var tables = ResolveTables(preset, tablesArg);
    var filters = CreateFilters(accountIdsArg, songIdsArg, instrumentsArg, bandTypesArg);

    Console.WriteLine($"Source: {FormatConnection(sourcePg!)}");
    Console.WriteLine($"Target: {FormatConnection(targetPg!)}");
    Console.WriteLine($"Preset: {preset}");
    Console.WriteLine($"Tables: {string.Join(", ", tables)}");
    Console.WriteLine($"Filters: {FormatFilters(filters)}");

    using var source = NpgsqlDataSource.Create(sourcePg!);
    using var target = NpgsqlDataSource.Create(targetPg!);

    ValidateTablesExist(source, tables, label: "source");
    ValidateTablesExist(target, tables, label: "target");

    var totalSw = Stopwatch.StartNew();
    var results = new List<TableCompareSummary>(tables.Count);
    foreach (var table in tables)
    {
        var result = CompareTable(source, target, table, filters);
        results.Add(result);
        Console.WriteLine(
            $"{table,-32} data={(result.DataMatch ? "match" : "diff"),5} exact={(result.ExactMatch ? "yes" : "no"),3} matched={result.Source.Rows,12:N0}/{result.Target.Rows,12:N0} total={result.Source.TotalRows,12:N0}/{result.Target.TotalRows,12:N0} cols={result.ComparedColumns.Count,3} scope={FormatAppliedFilters(result.Source.AppliedFilters)} schema={FormatSchemaDrift(result)} elapsed={result.ElapsedMs,10:F1}ms");
    }

    totalSw.Stop();

    var payload = new CompareRunSummary
    {
        CapturedAtUtc = DateTime.UtcNow.ToString("o"),
        Mode = "compare",
        Preset = preset,
        Filters = filters,
        Source = DescribeConnection(sourcePg!),
        Target = DescribeConnection(targetPg!),
        Tables = tables,
        TotalElapsedMs = Math.Round(totalSw.Elapsed.TotalMilliseconds, 3),
        Results = results,
    };

    EmitJson(outPath, payload);
    return 0;
}

static int RunRankings(string[] args)
{
    string? pg = null;
    string? outPath = null;
    bool computeRankingDeltas = false;
    bool useRankingDeltaTiers = true;

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
            case "--compute-ranking-deltas":
                computeRankingDeltas = ParseBoolArg(args[++i], "--compute-ranking-deltas");
                break;
            case "--use-ranking-delta-tiers":
                useRankingDeltaTiers = ParseBoolArg(args[++i], "--use-ranking-delta-tiers");
                break;
        }
    }

    if (string.IsNullOrWhiteSpace(pg))
        return Fail("--pg is required");

    Console.WriteLine($"Database: {FormatConnection(pg!)}");
    Console.WriteLine($"Compute ranking deltas: {computeRankingDeltas}");
    Console.WriteLine($"Use ranking delta tiers: {useRankingDeltaTiers}");

    using var dataSource = NpgsqlDataSource.Create(pg!);
    ValidateTablesExist(dataSource, PresetCatalog.RankingsExecutionTables, label: "rankings execution");

    var beforeCounts = CaptureTableCounts(dataSource, PresetCatalog.RankingsOutputTables);
    using var phaseCollector = new RankingsPhaseLogCollector();
    using var loggerFactory = LoggerFactory.Create(builder =>
    {
        builder.SetMinimumLevel(LogLevel.Information);
        builder.AddProvider(phaseCollector);
    });

    var features = Options.Create(new FeatureOptions
    {
        ComputeRankingDeltas = computeRankingDeltas,
        UseRankingDeltaTiers = useRankingDeltaTiers,
    });

    var metaDb = new MetaDatabase(dataSource, loggerFactory.CreateLogger<MetaDatabase>());
    using var persistence = new GlobalLeaderboardPersistence(
        metaDb,
        loggerFactory,
        loggerFactory.CreateLogger<GlobalLeaderboardPersistence>(),
        dataSource,
        features);
    persistence.Initialize();

    var pathStore = new PathDataStore(dataSource, loggerFactory.CreateLogger<PathDataStore>());
    var progress = new ScrapeProgressTracker();
    var festivalService = new FestivalService(new FestivalPersistence(dataSource));
    festivalService.InitializeAsync().GetAwaiter().GetResult();

    var calculator = new RankingsCalculator(
        persistence,
        metaDb,
        pathStore,
        progress,
        features,
        loggerFactory.CreateLogger<RankingsCalculator>());

    var sw = Stopwatch.StartNew();
    calculator.ComputeAllAsync(festivalService).GetAwaiter().GetResult();
    sw.Stop();

    var afterCounts = CaptureTableCounts(dataSource, PresetCatalog.RankingsOutputTables);
    foreach (var count in afterCounts)
    {
        var before = beforeCounts.FirstOrDefault(x => x.Table.Equals(count.Table, StringComparison.OrdinalIgnoreCase));
        Console.WriteLine($"{count.Table,-32} before={before?.Rows ?? 0,12:N0} after={count.Rows,12:N0} delta={count.Rows - (before?.Rows ?? 0),12:N0}");
    }

    var payload = new RankingsRunSummary
    {
        CapturedAtUtc = DateTime.UtcNow.ToString("o"),
        Mode = "run-rankings",
        Database = DescribeConnection(pg!),
        ComputeRankingDeltas = computeRankingDeltas,
        UseRankingDeltaTiers = useRankingDeltaTiers,
        TotalElapsedMs = Math.Round(sw.Elapsed.TotalMilliseconds, 3),
        SongsLoaded = festivalService.Songs.Count,
        PhaseTimings = phaseCollector.GetEntries(),
        TableCounts = MergeTableCounts(beforeCounts, afterCounts),
    };

    EmitJson(outPath, payload);
    return 0;
}

static int RunRivals(string[] args)
{
    string? pg = null;
    string? outPath = null;
    string? accountIdsArg = null;
    int maxDegreeOfParallelism = 8;

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
            case "--account-ids":
                accountIdsArg = args[++i];
                break;
            case "--max-degree-of-parallelism":
                maxDegreeOfParallelism = ParsePositiveIntArg(args[++i], "--max-degree-of-parallelism");
                break;
        }
    }

    if (string.IsNullOrWhiteSpace(pg))
        return Fail("--pg is required");

    using var dataSource = NpgsqlDataSource.Create(pg!);
    ValidateTablesExist(dataSource, PresetCatalog.RivalsExecutionTables, label: "rivals execution");

    using var services = CreateHarnessServices(dataSource);
    var accountIds = ResolveTargetAccountIds(services.MetaDb, accountIdsArg);
    var scope = ResolveAccountScope(accountIdsArg);

    Console.WriteLine($"Database: {FormatConnection(pg!)}");
    Console.WriteLine($"Accounts: {accountIds.Count:N0} ({scope})");
    Console.WriteLine($"Max degree of parallelism: {maxDegreeOfParallelism}");

    var beforeCounts = CaptureTableCounts(dataSource, PresetCatalog.RivalsOutputTables);

    using var rivalsCache = new ResponseCacheService(TimeSpan.FromMinutes(5));
    var notifications = new NotificationService(services.LoggerFactory.CreateLogger<NotificationService>());
    var syncTracker = new UserSyncProgressTracker(notifications, services.LoggerFactory.CreateLogger<UserSyncProgressTracker>());
    notifications.SetSyncTracker(syncTracker);

    var calculator = new RivalsCalculator(
        services.Persistence,
        services.LoggerFactory.CreateLogger<RivalsCalculator>());
    var orchestrator = new RivalsOrchestrator(
        calculator,
        services.Persistence,
        notifications,
        services.Progress,
        syncTracker,
        rivalsCache,
        services.LoggerFactory.CreateLogger<RivalsOrchestrator>());

    var sw = Stopwatch.StartNew();
    Parallel.ForEach(accountIds,
        new ParallelOptions { MaxDegreeOfParallelism = maxDegreeOfParallelism },
        accountId => orchestrator.ComputeForUser(accountId));
    sw.Stop();

    var afterCounts = CaptureTableCounts(dataSource, PresetCatalog.RivalsOutputTables);
    foreach (var count in afterCounts)
    {
        var before = beforeCounts.FirstOrDefault(x => x.Table.Equals(count.Table, StringComparison.OrdinalIgnoreCase));
        Console.WriteLine($"{count.Table,-32} before={before?.Rows ?? 0,12:N0} after={count.Rows,12:N0} delta={count.Rows - (before?.Rows ?? 0),12:N0}");
    }

    var payload = new PhaseRunSummary
    {
        CapturedAtUtc = DateTime.UtcNow.ToString("o"),
        Mode = "run-rivals",
        Database = DescribeConnection(pg!),
        Scope = scope,
        TargetAccountCount = accountIds.Count,
        ProcessedAccountCount = accountIds.Count,
        MaxDegreeOfParallelism = maxDegreeOfParallelism,
        TotalElapsedMs = Math.Round(sw.Elapsed.TotalMilliseconds, 3),
        TableCounts = MergeTableCounts(beforeCounts, afterCounts),
    };

    EmitJson(outPath, payload);
    return 0;
}

static int RunLeaderboardRivals(string[] args)
{
    string? pg = null;
    string? outPath = null;
    string? accountIdsArg = null;
    int maxDegreeOfParallelism = 8;
    int leaderboardRivalRadius = 10;

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
            case "--account-ids":
                accountIdsArg = args[++i];
                break;
            case "--max-degree-of-parallelism":
                maxDegreeOfParallelism = ParsePositiveIntArg(args[++i], "--max-degree-of-parallelism");
                break;
            case "--leaderboard-rival-radius":
                leaderboardRivalRadius = ParsePositiveIntArg(args[++i], "--leaderboard-rival-radius");
                break;
        }
    }

    if (string.IsNullOrWhiteSpace(pg))
        return Fail("--pg is required");

    using var dataSource = NpgsqlDataSource.Create(pg!);
    ValidateTablesExist(dataSource, PresetCatalog.LeaderboardRivalsExecutionTables, label: "leaderboard rivals execution");

    using var services = CreateHarnessServices(dataSource);
    var accountIds = ResolveTargetAccountIds(services.MetaDb, accountIdsArg);
    var scope = ResolveAccountScope(accountIdsArg);

    Console.WriteLine($"Database: {FormatConnection(pg!)}");
    Console.WriteLine($"Accounts: {accountIds.Count:N0} ({scope})");
    Console.WriteLine($"Max degree of parallelism: {maxDegreeOfParallelism}");
    Console.WriteLine($"Leaderboard rival radius: {leaderboardRivalRadius}");

    var beforeCounts = CaptureTableCounts(dataSource, PresetCatalog.LeaderboardRivalsOutputTables);
    var options = Options.Create(new ScraperOptions
    {
        LeaderboardRivalRadius = leaderboardRivalRadius,
    });
    var calculator = new LeaderboardRivalsCalculator(
        services.Persistence,
        services.MetaDb,
        options,
        services.LoggerFactory.CreateLogger<LeaderboardRivalsCalculator>());

    var sw = Stopwatch.StartNew();
    Parallel.ForEach(accountIds,
        new ParallelOptions { MaxDegreeOfParallelism = maxDegreeOfParallelism },
        accountId => calculator.ComputeForUser(accountId));
    sw.Stop();

    var afterCounts = CaptureTableCounts(dataSource, PresetCatalog.LeaderboardRivalsOutputTables);
    foreach (var count in afterCounts)
    {
        var before = beforeCounts.FirstOrDefault(x => x.Table.Equals(count.Table, StringComparison.OrdinalIgnoreCase));
        Console.WriteLine($"{count.Table,-32} before={before?.Rows ?? 0,12:N0} after={count.Rows,12:N0} delta={count.Rows - (before?.Rows ?? 0),12:N0}");
    }

    var payload = new PhaseRunSummary
    {
        CapturedAtUtc = DateTime.UtcNow.ToString("o"),
        Mode = "run-leaderboard-rivals",
        Database = DescribeConnection(pg!),
        Scope = scope,
        TargetAccountCount = accountIds.Count,
        ProcessedAccountCount = accountIds.Count,
        MaxDegreeOfParallelism = maxDegreeOfParallelism,
        TotalElapsedMs = Math.Round(sw.Elapsed.TotalMilliseconds, 3),
        TableCounts = MergeTableCounts(beforeCounts, afterCounts),
    };

    EmitJson(outPath, payload);
    return 0;
}

static int RunPlayerStats(string[] args)
{
    string? pg = null;
    string? outPath = null;
    string? accountIdsArg = null;
    int maxDegreeOfParallelism = 8;

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
            case "--account-ids":
                accountIdsArg = args[++i];
                break;
            case "--max-degree-of-parallelism":
                maxDegreeOfParallelism = ParsePositiveIntArg(args[++i], "--max-degree-of-parallelism");
                break;
        }
    }

    if (string.IsNullOrWhiteSpace(pg))
        return Fail("--pg is required");

    using var dataSource = NpgsqlDataSource.Create(pg!);
    ValidateTablesExist(dataSource, PresetCatalog.PlayerStatsExecutionTables, label: "player stats execution");

    using var services = CreateHarnessServices(dataSource);
    var accountIds = ResolveTargetAccountIds(services.MetaDb, accountIdsArg);
    var scope = ResolveAccountScope(accountIdsArg);

    Console.WriteLine($"Database: {FormatConnection(pg!)}");
    Console.WriteLine($"Accounts: {accountIds.Count:N0} ({scope})");
    Console.WriteLine($"Max degree of parallelism: {maxDegreeOfParallelism}");

    var beforeCounts = CaptureTableCounts(dataSource, PresetCatalog.PlayerStatsOutputTables);
    var allMaxScores = services.PathStore.GetAllMaxScores();
    var instrumentKeys = services.Persistence.GetInstrumentKeys();
    var totalSongs = services.Persistence.GetTotalSongCount();
    var population = services.MetaDb.GetAllLeaderboardPopulation();
    int processed = 0;

    var sw = Stopwatch.StartNew();
    Parallel.ForEach(accountIds,
        new ParallelOptions { MaxDegreeOfParallelism = maxDegreeOfParallelism },
        accountId =>
        {
            ComputeAndStorePlayerStats(
                services.Persistence,
                services.MetaDb,
                accountId,
                allMaxScores,
                instrumentKeys,
                totalSongs,
                population);
            Interlocked.Increment(ref processed);
        });
    sw.Stop();

    var afterCounts = CaptureTableCounts(dataSource, PresetCatalog.PlayerStatsOutputTables);
    foreach (var count in afterCounts)
    {
        var before = beforeCounts.FirstOrDefault(x => x.Table.Equals(count.Table, StringComparison.OrdinalIgnoreCase));
        Console.WriteLine($"{count.Table,-32} before={before?.Rows ?? 0,12:N0} after={count.Rows,12:N0} delta={count.Rows - (before?.Rows ?? 0),12:N0}");
    }

    var payload = new PhaseRunSummary
    {
        CapturedAtUtc = DateTime.UtcNow.ToString("o"),
        Mode = "run-player-stats",
        Database = DescribeConnection(pg!),
        Scope = scope,
        TargetAccountCount = accountIds.Count,
        ProcessedAccountCount = processed,
        MaxDegreeOfParallelism = maxDegreeOfParallelism,
        TotalElapsedMs = Math.Round(sw.Elapsed.TotalMilliseconds, 3),
        TableCounts = MergeTableCounts(beforeCounts, afterCounts),
    };

    EmitJson(outPath, payload);
    return 0;
}

static int RunPrecompute(string[] args)
{
    string? pg = null;
    string? outPath = null;
    string? accountIdsArg = null;
    bool playerBands = true;

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
            case "--account-ids":
                accountIdsArg = args[++i];
                break;
            case "--player-bands":
                playerBands = ParseBoolArg(args[++i], "--player-bands");
                break;
        }
    }

    if (string.IsNullOrWhiteSpace(pg))
        return Fail("--pg is required");

    using var dataSource = NpgsqlDataSource.Create(pg!);
    ValidateTablesExist(dataSource, PresetCatalog.PrecomputeExecutionTables, label: "precompute execution");

    using var phaseCollector = new PrecomputePhaseLogCollector();

    using var services = CreateHarnessServices(
        dataSource,
        new FeatureOptions
        {
            PlayerBands = playerBands,
            UseRankingDeltaTiers = true,
        },
        phaseCollector,
        LogLevel.Information);
    var accountIds = ResolveTargetAccountIds(services.MetaDb, accountIdsArg);
    var scope = ResolveAccountScope(accountIdsArg);

    Console.WriteLine($"Database: {FormatConnection(pg!)}");
    Console.WriteLine($"Accounts: {accountIds.Count:N0} ({scope})");
    Console.WriteLine($"Player bands: {playerBands}");

    var beforeCounts = CaptureTableCounts(dataSource, PresetCatalog.PrecomputeOutputTables);
    var precomputer = new ScrapeTimePrecomputer(
        services.Persistence,
        services.MetaDb,
        services.PathStore,
        services.Progress,
        services.LoggerFactory.CreateLogger<ScrapeTimePrecomputer>(),
        services.LoggerFactory,
        CreateJsonSerializerOptions(),
        services.Features,
        services.LeaderboardRivalsCalculator);

    var sw = Stopwatch.StartNew();
    if (string.IsNullOrWhiteSpace(accountIdsArg))
    {
        precomputer.PrecomputeAllAsync(CancellationToken.None).GetAwaiter().GetResult();
    }
    else
    {
        foreach (var accountId in accountIds)
            precomputer.PrecomputeUser(accountId);
    }
    sw.Stop();

    var afterCounts = CaptureTableCounts(dataSource, PresetCatalog.PrecomputeOutputTables);
    foreach (var count in afterCounts)
    {
        var before = beforeCounts.FirstOrDefault(x => x.Table.Equals(count.Table, StringComparison.OrdinalIgnoreCase));
        Console.WriteLine($"{count.Table,-32} before={before?.Rows ?? 0,12:N0} after={count.Rows,12:N0} delta={count.Rows - (before?.Rows ?? 0),12:N0}");
    }

    var stepTimings = phaseCollector.GetEntries();
    var stepSummaries = SummarizePrecomputeSteps(stepTimings);
    foreach (var summary in stepSummaries)
    {
        Console.WriteLine(
            $"precompute:{summary.Step,-22} samples={summary.Samples,4} total={summary.TotalDurationMs,8}ms avg={summary.AverageDurationMs,8:F1}ms max={summary.MaxDurationMs,8}ms cacheEntries={summary.TotalCacheEntries,6}");
    }

    var payload = new PrecomputeRunSummary
    {
        CapturedAtUtc = DateTime.UtcNow.ToString("o"),
        Mode = "run-precompute",
        Database = DescribeConnection(pg!),
        Scope = string.IsNullOrWhiteSpace(accountIdsArg) ? "registered-all" : scope,
        TargetAccountCount = accountIds.Count,
        ProcessedAccountCount = accountIds.Count,
        TotalElapsedMs = Math.Round(sw.Elapsed.TotalMilliseconds, 3),
        StepTimings = stepTimings,
        StepSummaries = stepSummaries,
        TableCounts = MergeTableCounts(beforeCounts, afterCounts),
    };

    EmitJson(outPath, payload);
    return 0;
}

static int RunBandExtraction(string[] args)
{
    string? pg = null;
    string? outPath = null;

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
        }
    }

    if (string.IsNullOrWhiteSpace(pg))
        return Fail("--pg is required");

    using var dataSource = NpgsqlDataSource.Create(pg!);
    ValidateTablesExist(dataSource, PresetCatalog.BandExecutionTables, label: "band extraction execution");

    using var services = CreateHarnessServices(dataSource);
    Console.WriteLine($"Database: {FormatConnection(pg!)}");

    var beforeCounts = CaptureTableCounts(dataSource, PresetCatalog.BandOutputTables);
    var extractor = new PostScrapeBandExtractor(
        dataSource,
        services.PathStore,
        services.LoggerFactory.CreateLogger<PostScrapeBandExtractor>());

    var sw = Stopwatch.StartNew();
    extractor.RunAsync(CancellationToken.None).GetAwaiter().GetResult();
    sw.Stop();

    var afterCounts = CaptureTableCounts(dataSource, PresetCatalog.BandOutputTables);
    foreach (var count in afterCounts)
    {
        var before = beforeCounts.FirstOrDefault(x => x.Table.Equals(count.Table, StringComparison.OrdinalIgnoreCase));
        Console.WriteLine($"{count.Table,-32} before={before?.Rows ?? 0,12:N0} after={count.Rows,12:N0} delta={count.Rows - (before?.Rows ?? 0),12:N0}");
    }

    var payload = new PhaseRunSummary
    {
        CapturedAtUtc = DateTime.UtcNow.ToString("o"),
        Mode = "run-band-extraction",
        Database = DescribeConnection(pg!),
        Scope = "all",
        TotalElapsedMs = Math.Round(sw.Elapsed.TotalMilliseconds, 3),
        TableCounts = MergeTableCounts(beforeCounts, afterCounts),
    };

    EmitJson(outPath, payload);
    return 0;
}

static int RunBandRankings(string[] args)
{
    string? pg = null;
    string? outPath = null;
    string? bandTypesArg = null;
    var defaults = BandTeamRankingRebuildOptions.Default;
    var writeMode = defaults.WriteMode;
    var commandTimeoutSeconds = defaults.CommandTimeoutSeconds;
    var analyzeStagingTable = defaults.AnalyzeStagingTable;
    var disableSynchronousCommit = defaults.DisableSynchronousCommit;

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
            case "--band-types":
                bandTypesArg = args[++i];
                break;
            case "--write-mode":
                writeMode = ParseBandTeamRankingWriteMode(args[++i], "--write-mode");
                break;
            case "--command-timeout-seconds":
                commandTimeoutSeconds = ParseNonNegativeIntArg(args[++i], "--command-timeout-seconds");
                break;
            case "--analyze-staging":
                analyzeStagingTable = ParseBoolArg(args[++i], "--analyze-staging");
                break;
            case "--disable-synchronous-commit":
                disableSynchronousCommit = ParseBoolArg(args[++i], "--disable-synchronous-commit");
                break;
        }
    }

    if (string.IsNullOrWhiteSpace(pg))
        return Fail("--pg is required");

    using var dataSource = NpgsqlDataSource.Create(pg!);
    ValidateTablesExist(dataSource, PresetCatalog.BandRankingsExecutionTables, label: "band rankings execution");

    using var services = CreateHarnessServices(dataSource);
    var requestedBandTypes = ParseCsvListOrEmpty(bandTypesArg);
    var scope = requestedBandTypes.Count == 0 ? "all" : "explicit";
    var options = new BandTeamRankingRebuildOptions
    {
        WriteMode = writeMode,
        CommandTimeoutSeconds = commandTimeoutSeconds,
        AnalyzeStagingTable = analyzeStagingTable,
        DisableSynchronousCommit = disableSynchronousCommit,
    };

    Console.WriteLine($"Database: {FormatConnection(pg!)}");
    Console.WriteLine($"Band types: {(requestedBandTypes.Count == 0 ? "all" : string.Join(", ", requestedBandTypes))}");
    Console.WriteLine($"Write mode: {writeMode}");
    Console.WriteLine($"Command timeout: {(commandTimeoutSeconds == 0 ? "none" : $"{commandTimeoutSeconds}s")}");
    Console.WriteLine($"Analyze staging: {analyzeStagingTable}");
    Console.WriteLine($"Disable synchronous commit: {disableSynchronousCommit}");

    var beforeCounts = CaptureTableCounts(dataSource, PresetCatalog.BandRankingsOutputTables);
    var repairService = new BandRankingRepairService(
        services.MetaDb,
        dataSource,
        services.LoggerFactory.CreateLogger<BandRankingRepairService>());

    var sw = Stopwatch.StartNew();
    var results = repairService.Rebuild(requestedBandTypes.Count == 0 ? null : requestedBandTypes, options: options);
    sw.Stop();

    var afterCounts = CaptureTableCounts(dataSource, PresetCatalog.BandRankingsOutputTables);
    foreach (var count in afterCounts)
    {
        var before = beforeCounts.FirstOrDefault(x => x.Table.Equals(count.Table, StringComparison.OrdinalIgnoreCase));
        Console.WriteLine($"{count.Table,-32} before={before?.Rows ?? 0,12:N0} after={count.Rows,12:N0} delta={count.Rows - (before?.Rows ?? 0),12:N0}");
    }

    foreach (var result in results)
    {
        Console.WriteLine(
            $"{result.BandType,-16} source={result.After.SourceRows,12:N0} rankable={result.After.RankableRows,12:N0} rankings={result.After.RankingRows,12:N0} combos={result.After.ComboCatalogEntries,6:N0} total={result.Elapsed.TotalMilliseconds,9:F1}ms");

        if (result.Metrics is not null)
        {
            Console.WriteLine(
                $"{string.Empty,16} materialize={result.Metrics.MaterializeResultsMs,9:F1}ms analyze={result.Metrics.AnalyzeResultsMs,9:F1}ms delete={result.Metrics.DeleteExistingMs,9:F1}ms insert={result.Metrics.InsertRankingsMs,9:F1}ms stats={result.Metrics.InsertStatsMs,9:F1}ms rows={result.Metrics.ResultRowCount,12:N0}");
        }
    }

    var payload = new BandRankingsRunSummary
    {
        CapturedAtUtc = DateTime.UtcNow.ToString("o"),
        Mode = "run-band-rankings",
        Database = DescribeConnection(pg!),
        Scope = scope,
        BandTypes = results.Select(result => result.BandType).ToArray(),
        WriteMode = writeMode.ToString(),
        CommandTimeoutSeconds = commandTimeoutSeconds,
        AnalyzeStagingTable = analyzeStagingTable,
        DisableSynchronousCommit = disableSynchronousCommit,
        TotalChartedSongs = results.FirstOrDefault()?.TotalChartedSongs ?? 0,
        TotalElapsedMs = Math.Round(sw.Elapsed.TotalMilliseconds, 3),
        Results = results,
        TableCounts = MergeTableCounts(beforeCounts, afterCounts),
    };

    EmitJson(outPath, payload);
    return 0;
}

static int ComputeAndStorePlayerStats(
    GlobalLeaderboardPersistence persistence,
    IMetaDatabase metaDb,
    string accountId,
    Dictionary<string, SongMaxScores> allMaxScores,
    IReadOnlyList<string> instrumentKeys,
    int totalSongs,
    Dictionary<(string SongId, string Instrument), long> population)
{
    var allScores = persistence.GetPlayerProfile(accountId);
    if (allScores.Count == 0)
        return 0;

    var byInstrument = new Dictionary<string, List<PlayerScoreDto>>(StringComparer.OrdinalIgnoreCase);
    foreach (var score in allScores)
    {
        if (!byInstrument.TryGetValue(score.Instrument, out var list))
        {
            list = [];
            byInstrument[score.Instrument] = list;
        }

        list.Add(score);
    }

    Dictionary<(string SongId, string Instrument), List<ValidScoreFallback>>? fallbacks = null;
    var maxThresholds = new Dictionary<(string SongId, string Instrument), int>();
    foreach (var score in allScores)
    {
        if (!allMaxScores.TryGetValue(score.SongId, out var maxScores))
            continue;

        var max = maxScores.GetByInstrument(score.Instrument);
        if (max.HasValue && max.Value > 0 && score.Score > max.Value)
            maxThresholds[(score.SongId, score.Instrument)] = (int)(max.Value * 1.05);
    }

    if (maxThresholds.Count > 0)
        fallbacks = metaDb.GetAllValidScoreTiers(accountId, maxThresholds);

    var rows = new List<PlayerStatsTiersRow>();
    var perInstrumentTiers = new Dictionary<string, List<PlayerStatsTier>>(StringComparer.OrdinalIgnoreCase);

    foreach (var instrument in instrumentKeys)
    {
        var scores = byInstrument.GetValueOrDefault(instrument);
        if (scores is null || scores.Count == 0)
            continue;

        var tiers = PlayerStatsCalculator.ComputeTiers(scores, allMaxScores, instrument, totalSongs, population, fallbacks);
        perInstrumentTiers[instrument] = tiers;

        rows.Add(new PlayerStatsTiersRow
        {
            AccountId = accountId,
            Instrument = instrument,
            TiersJson = JsonSerializer.Serialize(tiers),
        });
    }

    if (perInstrumentTiers.Count > 0)
    {
        var overallTiers = PlayerStatsCalculator.ComputeOverallTiers(perInstrumentTiers, totalSongs);
        rows.Add(new PlayerStatsTiersRow
        {
            AccountId = accountId,
            Instrument = "Overall",
            TiersJson = JsonSerializer.Serialize(overallTiers),
        });
    }

    metaDb.UpsertPlayerStatsTiersBatch(rows);
    return rows.Count;
}

static HarnessServices CreateHarnessServices(
    NpgsqlDataSource dataSource,
    FeatureOptions? features = null,
    ILoggerProvider? additionalProvider = null,
    LogLevel minimumLogLevel = LogLevel.Warning)
{
    var featureOptions = features ?? new FeatureOptions
    {
        UseRankingDeltaTiers = true,
    };

    var loggerFactory = LoggerFactory.Create(builder =>
    {
        builder.SetMinimumLevel(minimumLogLevel);
        if (additionalProvider is not null)
            builder.AddProvider(additionalProvider);
    });

    var metaDb = new MetaDatabase(dataSource, loggerFactory.CreateLogger<MetaDatabase>());
    var persistence = new GlobalLeaderboardPersistence(
        metaDb,
        loggerFactory,
        loggerFactory.CreateLogger<GlobalLeaderboardPersistence>(),
        dataSource,
        Options.Create(featureOptions));
    persistence.Initialize();
    var leaderboardRivalsCalculator = new LeaderboardRivalsCalculator(
        persistence,
        metaDb,
        Options.Create(new ScraperOptions()),
        loggerFactory.CreateLogger<LeaderboardRivalsCalculator>());

    return new HarnessServices
    {
        LoggerFactory = loggerFactory,
        MetaDb = metaDb,
        Persistence = persistence,
        PathStore = new PathDataStore(dataSource, loggerFactory.CreateLogger<PathDataStore>()),
        Progress = new ScrapeProgressTracker(),
        Features = featureOptions,
        LeaderboardRivalsCalculator = leaderboardRivalsCalculator,
    };
}

static IReadOnlyList<string> ResolveTargetAccountIds(MetaDatabase metaDb, string? accountIdsArg)
{
    var ids = string.IsNullOrWhiteSpace(accountIdsArg)
        ? metaDb.GetRegisteredAccountIds().ToList()
        : ParseCsvList(accountIdsArg);

    ids.Sort(StringComparer.OrdinalIgnoreCase);
    return ids;
}

static string ResolveAccountScope(string? accountIdsArg) =>
    string.IsNullOrWhiteSpace(accountIdsArg) ? "registered" : "explicit";

static JsonSerializerOptions CreateJsonSerializerOptions()
{
    return new JsonSerializerOptions(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };
}

static TableCloneSummary CloneTable(NpgsqlDataSource source, NpgsqlDataSource target, string table, CloneFilters filters)
{
    var sw = Stopwatch.StartNew();
    var sourceColumns = GetColumns(source, table);
    var targetColumns = GetColumns(target, table);
    var cloneColumns = ResolveCloneColumns(table, sourceColumns, targetColumns);

    var querySpec = BuildTableQuerySpec(sourceColumns, filters);
    var quotedColumns = string.Join(", ", cloneColumns.Select(column => QuoteIdent(column.Name)));
    var totalSourceRows = CountRows(source, table);
    var sourceRows = querySpec.HasFilters ? CountRows(source, table, querySpec) : totalSourceRows;
    long copiedRows = 0;

    using var sourceConn = source.OpenConnection();
    using var targetConn = target.OpenConnection();
    using var targetTx = targetConn.BeginTransaction();
    using (var settingsCmd = targetConn.CreateCommand())
    {
        settingsCmd.Transaction = targetTx;
        settingsCmd.CommandText = "SET LOCAL synchronous_commit = off";
        settingsCmd.ExecuteNonQuery();
    }

    if (sourceRows > 0)
    {
        using var sourceCmd = sourceConn.CreateCommand();
        sourceCmd.CommandTimeout = 0;
        sourceCmd.CommandText = $"SELECT {quotedColumns} FROM {QualifiedTable(table)} t{querySpec.WhereClause}";
        AddBindings(sourceCmd, querySpec.Parameters);

        using var reader = sourceCmd.ExecuteReader();
        using var importer = targetConn.BeginBinaryImport($"COPY {QualifiedTable(table)} ({quotedColumns}) FROM STDIN (FORMAT BINARY)");
        while (reader.Read())
        {
            importer.StartRow();
            for (int ordinal = 0; ordinal < cloneColumns.Count; ordinal++)
            {
                if (reader.IsDBNull(ordinal))
                {
                    importer.WriteNull();
                    continue;
                }

                var column = cloneColumns[ordinal];
                var value = reader.GetValue(ordinal);
                importer.Write(NormalizeValue(value, column.Type), column.Type);
            }

            copiedRows++;
        }

        importer.Complete();
    }

    targetTx.Commit();
    var targetRows = CountRows(target, table);
    sw.Stop();

    if (sourceRows != targetRows)
        throw new InvalidOperationException($"Row mismatch for {table}: source={sourceRows}, target={targetRows}");

    return new TableCloneSummary
    {
        Table = table,
        TotalSourceRows = totalSourceRows,
        SourceRows = sourceRows,
        CopiedRows = copiedRows,
        TargetRows = targetRows,
        ElapsedMs = Math.Round(sw.Elapsed.TotalMilliseconds, 3),
        AppliedFilters = querySpec.AppliedFilters,
    };
}

static TableCompareSummary CompareTable(NpgsqlDataSource source, NpgsqlDataSource target, string table, CloneFilters filters)
{
    var sw = Stopwatch.StartNew();
    var sourceColumns = GetColumns(source, table);
    var targetColumns = GetColumns(target, table);
    var schema = ResolveComparableColumns(table, sourceColumns, targetColumns);
    var sourceQuerySpec = BuildTableQuerySpec(sourceColumns, filters);
    var targetQuerySpec = BuildTableQuerySpec(targetColumns, filters);

    EnsureFilterScopesMatch(table, sourceQuerySpec, targetQuerySpec);

    var sourceSnapshot = SnapshotTable(source, table, sourceQuerySpec, schema.ComparedColumns);
    var targetSnapshot = SnapshotTable(target, table, targetQuerySpec, schema.ComparedColumns);
    sw.Stop();

    var dataMatch = sourceSnapshot.Rows == targetSnapshot.Rows
        && string.Equals(sourceSnapshot.FingerprintSha256, targetSnapshot.FingerprintSha256, StringComparison.OrdinalIgnoreCase);
    var schemaDiffers = schema.SourceOnlyColumns.Count > 0 || schema.TargetOnlyColumns.Count > 0 || schema.TypeMismatchColumns.Count > 0;

    return new TableCompareSummary
    {
        Table = table,
        Source = sourceSnapshot,
        Target = targetSnapshot,
        ComparedColumns = schema.ComparedColumns.Select(column => column.Name).ToArray(),
        SourceOnlyColumns = schema.SourceOnlyColumns,
        TargetOnlyColumns = schema.TargetOnlyColumns,
        TypeMismatchColumns = schema.TypeMismatchColumns,
        DataMatch = dataMatch,
        ExactMatch = dataMatch && !schemaDiffers,
        ElapsedMs = Math.Round(sw.Elapsed.TotalMilliseconds, 3),
    };
}

static TableSnapshot SnapshotTable(NpgsqlDataSource dataSource, string table, TableQuerySpec querySpec, IReadOnlyList<ColumnDefinition> comparedColumns)
{
    var totalRows = CountRows(dataSource, table);
    var rows = querySpec.HasFilters ? CountRows(dataSource, table, querySpec) : totalRows;
    var fingerprint = rows == 0
        ? Convert.ToHexString(SHA256.HashData(Array.Empty<byte>()))
        : ComputeRowFingerprint(dataSource, table, querySpec, comparedColumns);

    return new TableSnapshot
    {
        Rows = rows,
        TotalRows = totalRows,
        FingerprintSha256 = fingerprint,
        AppliedFilters = querySpec.AppliedFilters,
    };
}

static IReadOnlyList<ColumnDefinition> GetColumns(NpgsqlDataSource dataSource, string table)
{
    using var conn = dataSource.OpenConnection();
    using var cmd = conn.CreateCommand();
    cmd.CommandText = @"
        SELECT column_name, data_type, udt_name, is_nullable, column_default
        FROM information_schema.columns
        WHERE table_schema = 'public' AND table_name = @table
        ORDER BY ordinal_position";
    cmd.Parameters.AddWithValue("table", table);

    var columns = new List<ColumnDefinition>();
    using var reader = cmd.ExecuteReader();
    while (reader.Read())
    {
        var columnName = reader.GetString(0);
        var dataType = reader.GetString(1);
        var udtName = reader.GetString(2);
        var isNullable = string.Equals(reader.GetString(3), "YES", StringComparison.OrdinalIgnoreCase);
        var hasDefault = !reader.IsDBNull(4);
        columns.Add(new ColumnDefinition(columnName, dataType, udtName, MapColumnType(dataType, udtName), isNullable, hasDefault));
    }

    if (columns.Count == 0)
        throw new InvalidOperationException($"Table not found in public schema: {table}");

    return columns;
}

static void ValidateTablesExist(NpgsqlDataSource dataSource, IReadOnlyList<string> tables, string label)
{
    using var conn = dataSource.OpenConnection();
    using var cmd = conn.CreateCommand();
    cmd.CommandText = @"
        SELECT table_name
        FROM information_schema.tables
        WHERE table_schema = 'public' AND table_name = ANY(@tables)";
    cmd.Parameters.AddWithValue("tables", tables.ToArray());

    var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    using var reader = cmd.ExecuteReader();
    while (reader.Read())
        found.Add(reader.GetString(0));

    var missing = tables.Where(table => !found.Contains(table)).ToList();
    if (missing.Count > 0)
        throw new InvalidOperationException($"Missing {label} tables: {string.Join(", ", missing)}");
}

static IReadOnlyList<ColumnDefinition> ResolveCloneColumns(string table, IReadOnlyList<ColumnDefinition> sourceColumns, IReadOnlyList<ColumnDefinition> targetColumns)
{
    var sourceByName = sourceColumns.ToDictionary(column => column.Name, StringComparer.OrdinalIgnoreCase);
    var cloneColumns = new List<ColumnDefinition>(targetColumns.Count);

    foreach (var targetColumn in targetColumns)
    {
        if (!sourceByName.TryGetValue(targetColumn.Name, out var sourceColumn))
        {
            if (!targetColumn.IsNullable && !targetColumn.HasDefault)
            {
                throw new InvalidOperationException(
                    $"Target column {targetColumn.Name} is missing from source table {table} and has no default/null allowance");
            }

            continue;
        }

        if (sourceColumn.Type != targetColumn.Type)
        {
            throw new InvalidOperationException(
                $"Column type mismatch for {table}.{targetColumn.Name}: source={sourceColumn.Type}, target={targetColumn.Type}");
        }

        cloneColumns.Add(sourceColumn);
    }

    if (cloneColumns.Count == 0)
        throw new InvalidOperationException($"No compatible columns found for {table}");

    return cloneColumns;
}

static ComparableSchema ResolveComparableColumns(string table, IReadOnlyList<ColumnDefinition> sourceColumns, IReadOnlyList<ColumnDefinition> targetColumns)
{
    var sourceByName = sourceColumns.ToDictionary(column => column.Name, StringComparer.OrdinalIgnoreCase);
    var targetByName = targetColumns.ToDictionary(column => column.Name, StringComparer.OrdinalIgnoreCase);

    var comparedColumns = new List<ColumnDefinition>();
    var typeMismatches = new List<string>();
    foreach (var columnName in sourceByName.Keys.Intersect(targetByName.Keys, StringComparer.OrdinalIgnoreCase).OrderBy(name => name, StringComparer.OrdinalIgnoreCase))
    {
        var sourceColumn = sourceByName[columnName];
        var targetColumn = targetByName[columnName];
        if (sourceColumn.Type != targetColumn.Type)
        {
            typeMismatches.Add($"{columnName} ({sourceColumn.Type} != {targetColumn.Type})");
            continue;
        }

        comparedColumns.Add(sourceColumn);
    }

    if (comparedColumns.Count == 0)
        throw new InvalidOperationException($"No comparable columns found for {table}");

    var sourceOnlyColumns = sourceByName.Keys
        .Except(targetByName.Keys, StringComparer.OrdinalIgnoreCase)
        .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
        .ToArray();
    var targetOnlyColumns = targetByName.Keys
        .Except(sourceByName.Keys, StringComparer.OrdinalIgnoreCase)
        .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
        .ToArray();

    return new ComparableSchema
    {
        ComparedColumns = comparedColumns,
        SourceOnlyColumns = sourceOnlyColumns,
        TargetOnlyColumns = targetOnlyColumns,
        TypeMismatchColumns = typeMismatches,
    };
}

static void EnsureFilterScopesMatch(string table, TableQuerySpec sourceQuerySpec, TableQuerySpec targetQuerySpec)
{
    var sourceFilters = sourceQuerySpec.AppliedFilters.OrderBy(filter => filter, StringComparer.OrdinalIgnoreCase).ToArray();
    var targetFilters = targetQuerySpec.AppliedFilters.OrderBy(filter => filter, StringComparer.OrdinalIgnoreCase).ToArray();
    if (!sourceFilters.SequenceEqual(targetFilters, StringComparer.OrdinalIgnoreCase))
    {
        throw new InvalidOperationException(
            $"Filter scope mismatch for {table}: source={string.Join(",", sourceFilters)} target={string.Join(",", targetFilters)}");
    }
}

static string ComputeRowFingerprint(NpgsqlDataSource dataSource, string table, TableQuerySpec querySpec, IReadOnlyList<ColumnDefinition> columns)
{
    var selectedColumns = string.Join(", ", columns.Select(column => $"t.{QuoteIdent(column.Name)}"));
    using var conn = dataSource.OpenConnection();
    using var cmd = conn.CreateCommand();
    cmd.CommandTimeout = 0;
    cmd.CommandText = $"""
        SELECT q.payload
        FROM (
            SELECT row_to_json(r)::text AS payload
            FROM (
                SELECT {selectedColumns}
                FROM {QualifiedTable(table)} t{querySpec.WhereClause}
            ) r
        ) q
        ORDER BY q.payload
        """;
    AddBindings(cmd, querySpec.Parameters);

    using var reader = cmd.ExecuteReader();
    using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    while (reader.Read())
    {
        var payload = reader.GetString(0);
        hasher.AppendData(Encoding.UTF8.GetBytes(payload));
        hasher.AppendData("\n"u8);
    }

    return Convert.ToHexString(hasher.GetHashAndReset());
}

static object NormalizeValue(object value, NpgsqlDbType type)
{
    if ((type == NpgsqlDbType.Json || type == NpgsqlDbType.Jsonb) && value is not string)
    {
        return value switch
        {
            JsonDocument jsonDocument => jsonDocument.RootElement.GetRawText(),
            JsonElement jsonElement => jsonElement.GetRawText(),
            _ => value.ToString() ?? string.Empty,
        };
    }

    return value;
}

static void TruncateTables(NpgsqlDataSource dataSource, IReadOnlyList<string> tables)
{
    if (tables.Count == 0)
        return;

    using var conn = dataSource.OpenConnection();
    using var cmd = conn.CreateCommand();
    cmd.CommandTimeout = 0;
    cmd.CommandText = $"TRUNCATE TABLE {string.Join(", ", tables.Select(QualifiedTable))} RESTART IDENTITY CASCADE";
    cmd.ExecuteNonQuery();
}

static long CountRows(NpgsqlDataSource dataSource, string table, TableQuerySpec? querySpec = null)
{
    using var conn = dataSource.OpenConnection();
    using var cmd = conn.CreateCommand();
    cmd.CommandTimeout = 0;
    cmd.CommandText = $"SELECT COUNT(*) FROM {QualifiedTable(table)} t{querySpec?.WhereClause ?? string.Empty}";
    if (querySpec is not null)
        AddBindings(cmd, querySpec.Parameters);
    return Convert.ToInt64(cmd.ExecuteScalar());
}

static IReadOnlyList<TableRowCount> CaptureTableCounts(NpgsqlDataSource dataSource, IReadOnlyList<string> tables)
{
    var results = new List<TableRowCount>(tables.Count);
    foreach (var table in tables)
    {
        results.Add(new TableRowCount
        {
            Table = table,
            Rows = CountRows(dataSource, table),
        });
    }

    return results;
}

static IReadOnlyList<TableCountDelta> MergeTableCounts(IReadOnlyList<TableRowCount> beforeCounts, IReadOnlyList<TableRowCount> afterCounts)
{
    var beforeLookup = beforeCounts.ToDictionary(item => item.Table, StringComparer.OrdinalIgnoreCase);
    var results = new List<TableCountDelta>(afterCounts.Count);
    foreach (var after in afterCounts)
    {
        beforeLookup.TryGetValue(after.Table, out var before);
        var beforeRows = before?.Rows ?? 0;
        results.Add(new TableCountDelta
        {
            Table = after.Table,
            BeforeRows = beforeRows,
            AfterRows = after.Rows,
            DeltaRows = after.Rows - beforeRows,
        });
    }

    return results;
}

static IReadOnlyList<PrecomputeStepSummary> SummarizePrecomputeSteps(IReadOnlyList<PrecomputeStepTiming> stepTimings)
{
    return stepTimings
        .GroupBy(entry => entry.Step, StringComparer.OrdinalIgnoreCase)
        .Select(group => new PrecomputeStepSummary
        {
            Step = group.Key,
            Samples = group.Count(),
            TotalDurationMs = group.Sum(entry => entry.DurationMs),
            AverageDurationMs = group.Average(entry => entry.DurationMs),
            MaxDurationMs = group.Max(entry => entry.DurationMs),
            TotalCacheEntries = group.Sum(entry => entry.CacheEntries ?? 0),
        })
        .OrderByDescending(summary => summary.TotalDurationMs)
        .ThenBy(summary => summary.Step, StringComparer.OrdinalIgnoreCase)
        .ToArray();
}

static void ResetKnownSequences(NpgsqlDataSource dataSource)
{
    using var conn = dataSource.OpenConnection();
    using var cmd = conn.CreateCommand();
    cmd.CommandText = @"
        SELECT setval(pg_get_serial_sequence('scrape_log', 'id'), COALESCE((SELECT MAX(id) FROM scrape_log), 0) + 1, false);
        SELECT setval(pg_get_serial_sequence('score_history', 'id'), COALESCE((SELECT MAX(id) FROM score_history), 0) + 1, false);
        SELECT setval(pg_get_serial_sequence('user_sessions', 'id'), COALESCE((SELECT MAX(id) FROM user_sessions), 0) + 1, false);";
    cmd.ExecuteNonQuery();
}

static IReadOnlyList<string> ResolveTables(string preset, string? tablesArg)
{
    if (!string.IsNullOrWhiteSpace(tablesArg))
        return ParseCsvList(tablesArg);

    if (!PresetCatalog.TablePresets.TryGetValue(preset, out var tables))
        throw new ArgumentException($"Unknown preset '{preset}'. Available presets: {string.Join(", ", PresetCatalog.TablePresets.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase))}", nameof(preset));

    return tables;
}

static List<string> ParseCsvList(string value)
{
    var results = new List<string>();
    foreach (var item in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
    {
        if (!results.Contains(item, StringComparer.OrdinalIgnoreCase))
            results.Add(item);
    }

    return results;
}

static IReadOnlyList<string> ParseCsvListOrEmpty(string? value) =>
    string.IsNullOrWhiteSpace(value) ? [] : ParseCsvList(value);

static bool ParseBoolArg(string value, string argumentName)
{
    if (bool.TryParse(value, out var parsed))
        return parsed;

    throw new ArgumentException($"{argumentName} must be 'true' or 'false'", argumentName);
}

static int ParsePositiveIntArg(string value, string argumentName)
{
    if (int.TryParse(value, out var parsed) && parsed > 0)
        return parsed;

    throw new ArgumentException($"{argumentName} must be a positive integer", argumentName);
}

static int ParseNonNegativeIntArg(string value, string argumentName)
{
    if (int.TryParse(value, out var parsed) && parsed >= 0)
        return parsed;

    throw new ArgumentException($"{argumentName} must be zero or a positive integer", argumentName);
}

static BandTeamRankingWriteMode ParseBandTeamRankingWriteMode(string value, string argumentName)
{
    return value.Trim().ToLowerInvariant() switch
    {
        "combo-batched" or "combo_batched" or "combobatched" => BandTeamRankingWriteMode.ComboBatched,
        "monolithic" => BandTeamRankingWriteMode.Monolithic,
        _ => throw new ArgumentException($"{argumentName} must be one of: combo-batched, monolithic", argumentName),
    };
}

static CloneFilters CreateFilters(string? accountIdsArg, string? songIdsArg, string? instrumentsArg, string? bandTypesArg)
{
    return new CloneFilters
    {
        AccountIds = ParseCsvListOrEmpty(accountIdsArg),
        SongIds = ParseCsvListOrEmpty(songIdsArg),
        Instruments = ParseCsvListOrEmpty(instrumentsArg),
        BandTypes = ParseCsvListOrEmpty(bandTypesArg),
    };
}

static TableQuerySpec BuildTableQuerySpec(IReadOnlyList<ColumnDefinition> columns, CloneFilters filters)
{
    if (!filters.HasAny)
        return TableQuerySpec.Empty;

    var availableColumns = columns.Select(column => column.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
    var clauses = new List<string>();
    var parameters = new Dictionary<string, ParameterBinding>(StringComparer.OrdinalIgnoreCase);
    var appliedFilters = new List<string>();

    if (filters.AccountIds.Count > 0)
    {
        if (availableColumns.Contains("account_id"))
        {
            clauses.Add($"t.{QuoteIdent("account_id")} = ANY(@account_ids)");
            AddTextArrayParameter(parameters, "account_ids", filters.AccountIds);
            appliedFilters.Add("account_id");
        }
        else if (availableColumns.Contains("user_id"))
        {
            clauses.Add($"t.{QuoteIdent("user_id")} = ANY(@account_ids)");
            AddTextArrayParameter(parameters, "account_ids", filters.AccountIds);
            appliedFilters.Add("user_id");
        }
        else if (availableColumns.Contains("team_members"))
        {
            clauses.Add($"t.{QuoteIdent("team_members")} && @account_ids");
            AddTextArrayParameter(parameters, "account_ids", filters.AccountIds);
            appliedFilters.Add("team_members");
        }
    }

    if (filters.SongIds.Count > 0 && availableColumns.Contains("song_id"))
    {
        clauses.Add($"t.{QuoteIdent("song_id")} = ANY(@song_ids)");
        AddTextArrayParameter(parameters, "song_ids", filters.SongIds);
        appliedFilters.Add("song_id");
    }

    if (filters.Instruments.Count > 0 && availableColumns.Contains("instrument"))
    {
        clauses.Add($"t.{QuoteIdent("instrument")} = ANY(@instruments)");
        AddTextArrayParameter(parameters, "instruments", filters.Instruments);
        appliedFilters.Add("instrument");
    }

    if (filters.BandTypes.Count > 0 && availableColumns.Contains("band_type"))
    {
        clauses.Add($"t.{QuoteIdent("band_type")} = ANY(@band_types)");
        AddTextArrayParameter(parameters, "band_types", filters.BandTypes);
        appliedFilters.Add("band_type");
    }

    return new TableQuerySpec
    {
        WhereClause = clauses.Count == 0 ? string.Empty : $" WHERE {string.Join(" AND ", clauses)}",
        Parameters = parameters.Values.ToArray(),
        AppliedFilters = appliedFilters,
    };
}

static void AddTextArrayParameter(IDictionary<string, ParameterBinding> parameters, string name, IReadOnlyList<string> values)
{
    if (!parameters.ContainsKey(name))
        parameters[name] = new ParameterBinding(name, values.ToArray(), NpgsqlDbType.Array | NpgsqlDbType.Text);
}

static void AddBindings(NpgsqlCommand command, IReadOnlyList<ParameterBinding> bindings)
{
    foreach (var binding in bindings)
        command.Parameters.AddWithValue(binding.Name, binding.Type, binding.Value);
}

static string FormatSchemaDrift(TableCompareSummary result)
{
    var parts = new List<string>();
    if (result.SourceOnlyColumns.Count > 0)
        parts.Add($"src+{result.SourceOnlyColumns.Count}");
    if (result.TargetOnlyColumns.Count > 0)
        parts.Add($"tgt+{result.TargetOnlyColumns.Count}");
    if (result.TypeMismatchColumns.Count > 0)
        parts.Add($"type!{result.TypeMismatchColumns.Count}");
    return parts.Count == 0 ? "aligned" : string.Join(",", parts);
}

static string FormatFilters(CloneFilters filters)
{
    if (!filters.HasAny)
        return "none";

    var parts = new List<string>();
    if (filters.AccountIds.Count > 0)
        parts.Add($"accountIds={filters.AccountIds.Count}");
    if (filters.SongIds.Count > 0)
        parts.Add($"songIds={filters.SongIds.Count}");
    if (filters.Instruments.Count > 0)
        parts.Add($"instruments={filters.Instruments.Count}");
    if (filters.BandTypes.Count > 0)
        parts.Add($"bandTypes={filters.BandTypes.Count}");
    return string.Join(", ", parts);
}

static string FormatAppliedFilters(IReadOnlyList<string> appliedFilters) =>
    appliedFilters.Count == 0 ? "full" : string.Join("+", appliedFilters);

static string FormatConnection(string connectionString)
{
    var builder = new NpgsqlConnectionStringBuilder(connectionString);
    return $"host={builder.Host ?? string.Empty} port={builder.Port} db={builder.Database ?? string.Empty} user={builder.Username ?? string.Empty}";
}

static ConnectionSummary DescribeConnection(string connectionString)
{
    var builder = new NpgsqlConnectionStringBuilder(connectionString);
    return new ConnectionSummary
    {
        Host = builder.Host ?? string.Empty,
        Port = builder.Port,
        Database = builder.Database ?? string.Empty,
        Username = builder.Username ?? string.Empty,
    };
}

static string QualifiedTable(string table) => $"public.{QuoteIdent(table)}";

static string QuoteIdent(string value) => $"\"{value.Replace("\"", "\"\"")}\"";

static NpgsqlDbType MapColumnType(string dataType, string udtName) => dataType switch
{
    "text" => NpgsqlDbType.Text,
    "character varying" => NpgsqlDbType.Text,
    "integer" => NpgsqlDbType.Integer,
    "bigint" => NpgsqlDbType.Bigint,
    "smallint" => NpgsqlDbType.Smallint,
    "real" => NpgsqlDbType.Real,
    "double precision" => NpgsqlDbType.Double,
    "numeric" => NpgsqlDbType.Numeric,
    "boolean" => NpgsqlDbType.Boolean,
    "timestamp with time zone" => NpgsqlDbType.TimestampTz,
    "timestamp without time zone" => NpgsqlDbType.Timestamp,
    "date" => NpgsqlDbType.Date,
    "bytea" => NpgsqlDbType.Bytea,
    "uuid" => NpgsqlDbType.Uuid,
    "json" => NpgsqlDbType.Json,
    "jsonb" => NpgsqlDbType.Jsonb,
    "ARRAY" => MapArrayType(udtName),
    _ => throw new NotSupportedException($"Unsupported PostgreSQL type: data_type={dataType}, udt_name={udtName}"),
};

static NpgsqlDbType MapArrayType(string udtName) => udtName switch
{
    "_text" => NpgsqlDbType.Array | NpgsqlDbType.Text,
    "_varchar" => NpgsqlDbType.Array | NpgsqlDbType.Text,
    "_int4" => NpgsqlDbType.Array | NpgsqlDbType.Integer,
    "_int8" => NpgsqlDbType.Array | NpgsqlDbType.Bigint,
    "_float4" => NpgsqlDbType.Array | NpgsqlDbType.Real,
    "_float8" => NpgsqlDbType.Array | NpgsqlDbType.Double,
    "_bool" => NpgsqlDbType.Array | NpgsqlDbType.Boolean,
    _ => throw new NotSupportedException($"Unsupported PostgreSQL array type: {udtName}"),
};

static void EmitJson(string? outPath, object payload)
{
    var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true });
    if (string.IsNullOrWhiteSpace(outPath))
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
          PostScrapeCloneHarness inspect --pg <connection-string> [--preset <name>] [--tables <csv>] [--account-ids <csv>] [--song-ids <csv>] [--instruments <csv>] [--band-types <csv>] [--out <path>]
          PostScrapeCloneHarness clone --source-pg <connection-string> --target-pg <connection-string> [--preset <name>] [--tables <csv>] [--account-ids <csv>] [--song-ids <csv>] [--instruments <csv>] [--band-types <csv>] [--no-reset] [--skip-schema] [--out <path>]
                    PostScrapeCloneHarness compare --source-pg <connection-string> --target-pg <connection-string> [--preset <name>] [--tables <csv>] [--account-ids <csv>] [--song-ids <csv>] [--instruments <csv>] [--band-types <csv>] [--out <path>]
                    PostScrapeCloneHarness run-rankings --pg <connection-string> [--compute-ranking-deltas <true|false>] [--use-ranking-delta-tiers <true|false>] [--out <path>]
                    PostScrapeCloneHarness run-rivals --pg <connection-string> [--account-ids <csv>] [--max-degree-of-parallelism <int>] [--out <path>]
                    PostScrapeCloneHarness run-leaderboard-rivals --pg <connection-string> [--account-ids <csv>] [--max-degree-of-parallelism <int>] [--leaderboard-rival-radius <int>] [--out <path>]
                    PostScrapeCloneHarness run-player-stats --pg <connection-string> [--account-ids <csv>] [--max-degree-of-parallelism <int>] [--out <path>]
                    PostScrapeCloneHarness run-precompute --pg <connection-string> [--account-ids <csv>] [--player-bands <true|false>] [--out <path>]
                    PostScrapeCloneHarness run-band-rankings --pg <connection-string> [--band-types <csv>] [--write-mode <combo-batched|monolithic>] [--command-timeout-seconds <int>] [--analyze-staging <true|false>] [--disable-synchronous-commit <true|false>] [--out <path>]
                    PostScrapeCloneHarness run-band-extraction --pg <connection-string> [--out <path>]

        Notes:
          - Default preset is `post-scrape`.
          - `clone` ensures the target schema by default, truncates selected tables, then streams source rows into the target via COPY binary import.
                    - `compare` fingerprints sorted JSON payloads built from the compatible columns shared by both databases.
                    - `run-rankings` executes `RankingsCalculator.ComputeAllAsync` directly against the selected database and records phase timings plus before/after output table counts.
                    - `run-rivals`, `run-leaderboard-rivals`, and `run-player-stats` default to all registered users unless `--account-ids` is supplied.
                    - `run-precompute` runs the full registered-user precompute when `--account-ids` is omitted; otherwise it precomputes only the supplied users.
                    - `run-band-rankings` rebuilds derived band team rankings with a selectable write strategy and emits per-band timing breakdowns.
          - Filter options are best-effort and apply directly to matching table columns (`account_id`, `user_id`, `team_members`, `song_id`, `instrument`, `band_type`).
          - `--tables` overrides the preset with an explicit table list.
          - This harness never mutates the source database.
        """);
}

sealed class ColumnDefinition(string name, string dataType, string udtName, NpgsqlDbType type, bool isNullable, bool hasDefault)
{
    public string Name { get; } = name;
    public string DataType { get; } = dataType;
    public string UdtName { get; } = udtName;
    public NpgsqlDbType Type { get; } = type;
    public bool IsNullable { get; } = isNullable;
    public bool HasDefault { get; } = hasDefault;
}

sealed class ConnectionSummary
{
    public string Host { get; init; } = string.Empty;
    public int Port { get; init; }
    public string Database { get; init; } = string.Empty;
    public string Username { get; init; } = string.Empty;
}

sealed class CloneRunSummary
{
    public string CapturedAtUtc { get; init; } = string.Empty;
    public string Mode { get; init; } = string.Empty;
    public string Preset { get; init; } = string.Empty;
    public bool ResetTarget { get; init; }
    public bool EnsuredSchema { get; init; }
    public CloneFilters Filters { get; init; } = new();
    public ConnectionSummary Source { get; init; } = new();
    public ConnectionSummary Target { get; init; } = new();
    public IReadOnlyList<string> Tables { get; init; } = [];
    public double TotalElapsedMs { get; init; }
    public IReadOnlyList<TableCloneSummary> Results { get; init; } = [];
}

sealed class CompareRunSummary
{
    public string CapturedAtUtc { get; init; } = string.Empty;
    public string Mode { get; init; } = string.Empty;
    public string Preset { get; init; } = string.Empty;
    public CloneFilters Filters { get; init; } = new();
    public ConnectionSummary Source { get; init; } = new();
    public ConnectionSummary Target { get; init; } = new();
    public IReadOnlyList<string> Tables { get; init; } = [];
    public double TotalElapsedMs { get; init; }
    public IReadOnlyList<TableCompareSummary> Results { get; init; } = [];
}

sealed class RankingsRunSummary
{
    public string CapturedAtUtc { get; init; } = string.Empty;
    public string Mode { get; init; } = string.Empty;
    public ConnectionSummary Database { get; init; } = new();
    public bool ComputeRankingDeltas { get; init; }
    public bool UseRankingDeltaTiers { get; init; }
    public int SongsLoaded { get; init; }
    public double TotalElapsedMs { get; init; }
    public IReadOnlyList<RankingsPhaseTiming> PhaseTimings { get; init; } = [];
    public IReadOnlyList<TableCountDelta> TableCounts { get; init; } = [];
}

sealed class BandRankingsRunSummary
{
    public string CapturedAtUtc { get; init; } = string.Empty;
    public string Mode { get; init; } = string.Empty;
    public ConnectionSummary Database { get; init; } = new();
    public string Scope { get; init; } = string.Empty;
    public IReadOnlyList<string> BandTypes { get; init; } = [];
    public string WriteMode { get; init; } = string.Empty;
    public int CommandTimeoutSeconds { get; init; }
    public bool AnalyzeStagingTable { get; init; }
    public bool DisableSynchronousCommit { get; init; }
    public int TotalChartedSongs { get; init; }
    public double TotalElapsedMs { get; init; }
    public IReadOnlyList<BandRankingRepairResult> Results { get; init; } = [];
    public IReadOnlyList<TableCountDelta> TableCounts { get; init; } = [];
}

sealed class PhaseRunSummary
{
    public string CapturedAtUtc { get; init; } = string.Empty;
    public string Mode { get; init; } = string.Empty;
    public ConnectionSummary Database { get; init; } = new();
    public string Scope { get; init; } = string.Empty;
    public int TargetAccountCount { get; init; }
    public int ProcessedAccountCount { get; init; }
    public int? MaxDegreeOfParallelism { get; init; }
    public double TotalElapsedMs { get; init; }
    public IReadOnlyList<TableCountDelta> TableCounts { get; init; } = [];
}

sealed class PrecomputeRunSummary
{
    public string CapturedAtUtc { get; init; } = string.Empty;
    public string Mode { get; init; } = string.Empty;
    public ConnectionSummary Database { get; init; } = new();
    public string Scope { get; init; } = string.Empty;
    public int TargetAccountCount { get; init; }
    public int ProcessedAccountCount { get; init; }
    public double TotalElapsedMs { get; init; }
    public IReadOnlyList<PrecomputeStepTiming> StepTimings { get; init; } = [];
    public IReadOnlyList<PrecomputeStepSummary> StepSummaries { get; init; } = [];
    public IReadOnlyList<TableCountDelta> TableCounts { get; init; } = [];
}

sealed class TableCloneSummary
{
    public string Table { get; init; } = string.Empty;
    public long TotalSourceRows { get; init; }
    public long SourceRows { get; init; }
    public long CopiedRows { get; init; }
    public long TargetRows { get; init; }
    public double ElapsedMs { get; init; }
    public IReadOnlyList<string> AppliedFilters { get; init; } = [];
}

sealed class TableInspectSummary
{
    public string Table { get; init; } = string.Empty;
    public long Rows { get; init; }
    public long TotalRows { get; init; }
    public double ElapsedMs { get; init; }
    public IReadOnlyList<string> AppliedFilters { get; init; } = [];
}

sealed class TableCompareSummary
{
    public string Table { get; init; } = string.Empty;
    public TableSnapshot Source { get; init; } = new();
    public TableSnapshot Target { get; init; } = new();
    public IReadOnlyList<string> ComparedColumns { get; init; } = [];
    public IReadOnlyList<string> SourceOnlyColumns { get; init; } = [];
    public IReadOnlyList<string> TargetOnlyColumns { get; init; } = [];
    public IReadOnlyList<string> TypeMismatchColumns { get; init; } = [];
    public bool DataMatch { get; init; }
    public bool ExactMatch { get; init; }
    public double ElapsedMs { get; init; }
}

sealed class TableSnapshot
{
    public long Rows { get; init; }
    public long TotalRows { get; init; }
    public string FingerprintSha256 { get; init; } = string.Empty;
    public IReadOnlyList<string> AppliedFilters { get; init; } = [];
}

sealed class TableRowCount
{
    public string Table { get; init; } = string.Empty;
    public long Rows { get; init; }
}

sealed class TableCountDelta
{
    public string Table { get; init; } = string.Empty;
    public long BeforeRows { get; init; }
    public long AfterRows { get; init; }
    public long DeltaRows { get; init; }
}

sealed class CloneFilters
{
    public IReadOnlyList<string> AccountIds { get; init; } = [];
    public IReadOnlyList<string> SongIds { get; init; } = [];
    public IReadOnlyList<string> Instruments { get; init; } = [];
    public IReadOnlyList<string> BandTypes { get; init; } = [];

    internal bool HasAny => AccountIds.Count > 0 || SongIds.Count > 0 || Instruments.Count > 0 || BandTypes.Count > 0;
}

sealed class TableQuerySpec
{
    public static TableQuerySpec Empty { get; } = new();

    public string WhereClause { get; init; } = string.Empty;
    public IReadOnlyList<ParameterBinding> Parameters { get; init; } = [];
    public IReadOnlyList<string> AppliedFilters { get; init; } = [];

    internal bool HasFilters => AppliedFilters.Count > 0;
}

sealed class ParameterBinding(string name, object value, NpgsqlDbType type)
{
    public string Name { get; } = name;
    public object Value { get; } = value;
    public NpgsqlDbType Type { get; } = type;
}

sealed class ComparableSchema
{
    public IReadOnlyList<ColumnDefinition> ComparedColumns { get; init; } = [];
    public IReadOnlyList<string> SourceOnlyColumns { get; init; } = [];
    public IReadOnlyList<string> TargetOnlyColumns { get; init; } = [];
    public IReadOnlyList<string> TypeMismatchColumns { get; init; } = [];
}

sealed class RankingsPhaseTiming
{
    public string Phase { get; init; } = string.Empty;
    public string Instrument { get; init; } = string.Empty;
    public long DurationMs { get; init; }
    public long? RowCount { get; init; }
}

sealed class PrecomputeStepTiming
{
    public string AccountId { get; init; } = string.Empty;
    public string Step { get; init; } = string.Empty;
    public long DurationMs { get; init; }
    public int? CacheEntries { get; init; }
}

sealed class PrecomputeStepSummary
{
    public string Step { get; init; } = string.Empty;
    public int Samples { get; init; }
    public long TotalDurationMs { get; init; }
    public double AverageDurationMs { get; init; }
    public long MaxDurationMs { get; init; }
    public long TotalCacheEntries { get; init; }
}

sealed class RankingsPhaseLogCollector : ILoggerProvider
{
    private readonly List<RankingsPhaseTiming> _entries = [];
    private readonly object _sync = new();

    public IReadOnlyList<RankingsPhaseTiming> GetEntries()
    {
        lock (_sync)
            return _entries.ToArray();
    }

    public ILogger CreateLogger(string categoryName) => new RankingsPhaseLogger(categoryName, this);

    public void Dispose()
    {
    }

    private void Add(RankingsPhaseTiming timing)
    {
        lock (_sync)
            _entries.Add(timing);
    }

    private sealed class RankingsPhaseLogger(string categoryName, RankingsPhaseLogCollector owner) : ILogger
    {
        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
                return;

            var message = formatter(state, exception);
            if (string.IsNullOrWhiteSpace(message))
                return;

            if (message.StartsWith("[Rankings.Phase] ", StringComparison.Ordinal))
            {
                if (TryParseTiming(message, out var timing))
                    owner.Add(timing);
                Console.WriteLine(message);
                return;
            }

            if (logLevel >= LogLevel.Warning)
                Console.Error.WriteLine($"[{categoryName}] {message}");
        }

        private static bool TryParseTiming(string message, out RankingsPhaseTiming timing)
        {
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var token in message[17..].Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var split = token.Split('=', 2);
                if (split.Length == 2)
                    values[split[0]] = split[1];
            }

            if (!values.TryGetValue("phase", out var phase)
                || !values.TryGetValue("instrument", out var instrument)
                || !values.TryGetValue("duration_ms", out var durationRaw)
                || !long.TryParse(durationRaw, out var durationMs))
            {
                timing = new RankingsPhaseTiming();
                return false;
            }

            long? rowCount = null;
            if (values.TryGetValue("row_count", out var rowCountRaw)
                && !string.Equals(rowCountRaw, "-", StringComparison.Ordinal)
                && long.TryParse(rowCountRaw, out var parsedRowCount))
            {
                rowCount = parsedRowCount;
            }

            timing = new RankingsPhaseTiming
            {
                Phase = phase,
                Instrument = instrument,
                DurationMs = durationMs,
                RowCount = rowCount,
            };
            return true;
        }
    }

    private sealed class NullScope : IDisposable
    {
        public static NullScope Instance { get; } = new();

        public void Dispose()
        {
        }
    }
}

sealed class PrecomputePhaseLogCollector : ILoggerProvider
{
    private readonly List<PrecomputeStepTiming> _entries = [];
    private readonly object _sync = new();

    public IReadOnlyList<PrecomputeStepTiming> GetEntries()
    {
        lock (_sync)
            return _entries.ToArray();
    }

    public ILogger CreateLogger(string categoryName) => new PrecomputePhaseLogger(categoryName, this);

    public void Dispose()
    {
    }

    private void Add(PrecomputeStepTiming timing)
    {
        lock (_sync)
            _entries.Add(timing);
    }

    private sealed class PrecomputePhaseLogger(string categoryName, PrecomputePhaseLogCollector owner) : ILogger
    {
        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
                return;

            var message = formatter(state, exception);
            if (string.IsNullOrWhiteSpace(message))
                return;

            if (message.StartsWith("[Precompute.Step] ", StringComparison.Ordinal))
            {
                if (TryParseTiming(message, out var timing))
                    owner.Add(timing);
                Console.WriteLine(message);
                return;
            }

            if (logLevel >= LogLevel.Warning)
                Console.Error.WriteLine($"[{categoryName}] {message}");
        }

        private static bool TryParseTiming(string message, out PrecomputeStepTiming timing)
        {
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var token in message[18..].Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var split = token.Split('=', 2);
                if (split.Length == 2)
                    values[split[0]] = split[1];
            }

            if (!values.TryGetValue("account", out var accountId)
                || !values.TryGetValue("step", out var step)
                || !values.TryGetValue("duration_ms", out var durationRaw)
                || !long.TryParse(durationRaw, out var durationMs))
            {
                timing = new PrecomputeStepTiming();
                return false;
            }

            int? cacheEntries = null;
            if (values.TryGetValue("cache_entries", out var cacheEntriesRaw)
                && !string.Equals(cacheEntriesRaw, "-", StringComparison.Ordinal)
                && int.TryParse(cacheEntriesRaw, out var parsedCacheEntries))
            {
                cacheEntries = parsedCacheEntries;
            }

            timing = new PrecomputeStepTiming
            {
                AccountId = accountId,
                Step = step,
                DurationMs = durationMs,
                CacheEntries = cacheEntries,
            };
            return true;
        }
    }

    private sealed class NullScope : IDisposable
    {
        public static NullScope Instance { get; } = new();

        public void Dispose()
        {
        }
    }
}

sealed class HarnessServices : IDisposable
{
    public ILoggerFactory LoggerFactory { get; init; } = null!;
    public MetaDatabase MetaDb { get; init; } = null!;
    public GlobalLeaderboardPersistence Persistence { get; init; } = null!;
    public PathDataStore PathStore { get; init; } = null!;
    public ScrapeProgressTracker Progress { get; init; } = null!;
    public FeatureOptions Features { get; init; } = null!;
    public LeaderboardRivalsCalculator LeaderboardRivalsCalculator { get; init; } = null!;

    public void Dispose()
    {
        Persistence.Dispose();
        LoggerFactory.Dispose();
    }
}

static class PresetCatalog
{
    private static readonly string[] CoreInputs =
    [
        "songs",
        "leaderboard_entries",
        "score_history",
        "account_names",
        "registered_users",
        "season_windows",
        "song_first_seen_season",
        "leaderboard_population",
    ];

    private static readonly string[] RankingsTables =
    [
        "song_stats",
        "valid_score_overrides",
        "account_rankings",
        "rank_history",
        "rank_history_deltas",
        "ranking_deltas",
        "ranking_delta_tiers",
        "composite_rankings",
        "composite_rank_history",
        "composite_ranking_deltas",
        "combo_leaderboard",
        "combo_stats",
        "combo_ranking_deltas",
    ];

    public static readonly IReadOnlyList<string> RankingsOutputTables = Unique([
        .. RankingsTables,
        "band_team_rankings",
        "band_team_ranking_stats",
    ]);

    public static readonly IReadOnlyList<string> RankingsExecutionTables = Unique([
        "songs",
        "leaderboard_entries",
        "score_history",
        "leaderboard_population",
        .. RankingsOutputTables,
        "band_entries",
        "band_member_stats",
        "band_members",
    ]);

    public static readonly IReadOnlyList<string> RivalsOutputTables = Unique([
        "rivals_status",
        "user_rivals",
        "rival_song_samples",
    ]);

    public static readonly IReadOnlyList<string> RivalsExecutionTables = Unique([
        "leaderboard_entries",
        "registered_users",
        .. RivalsOutputTables,
    ]);

    public static readonly IReadOnlyList<string> LeaderboardRivalsOutputTables = Unique([
        "leaderboard_rivals",
        "leaderboard_rival_song_samples",
    ]);

    public static readonly IReadOnlyList<string> LeaderboardRivalsExecutionTables = Unique([
        "leaderboard_entries",
        "account_rankings",
        "registered_users",
        .. LeaderboardRivalsOutputTables,
    ]);

    public static readonly IReadOnlyList<string> PlayerStatsOutputTables = Unique([
        "player_stats_tiers",
    ]);

    public static readonly IReadOnlyList<string> PlayerStatsExecutionTables = Unique([
        "songs",
        "leaderboard_entries",
        "score_history",
        "leaderboard_population",
        .. PlayerStatsOutputTables,
    ]);

    public static readonly IReadOnlyList<string> PrecomputeOutputTables = Unique([
        "api_response_cache",
        "api_response_cache_staging",
    ]);

    public static readonly IReadOnlyList<string> PrecomputeExecutionTables = Unique([
        .. CoreInputs,
        .. RankingsTables,
        "api_response_cache",
        "api_response_cache_staging",
        "rivals_status",
        "user_rivals",
        "rival_song_samples",
        "leaderboard_rivals",
        "leaderboard_rival_song_samples",
        "player_stats",
        "player_stats_tiers",
        "band_entries",
        "band_member_stats",
        "band_members",
        "band_team_rankings",
        "band_team_ranking_stats",
        "backfill_status",
        "backfill_progress",
        "history_recon_status",
        "history_recon_progress",
    ]);

    public static readonly IReadOnlyList<string> BandOutputTables = Unique([
        "band_entries",
        "band_member_stats",
        "band_members",
        "band_team_rankings",
        "band_team_ranking_stats",
    ]);

    public static readonly IReadOnlyList<string> BandRankingsOutputTables = Unique([
        "band_team_rankings",
        "band_team_ranking_stats",
    ]);

    public static readonly IReadOnlyList<string> BandRankingsExecutionTables = Unique([
        "songs",
        "band_entries",
        .. BandRankingsOutputTables,
    ]);

    public static readonly IReadOnlyList<string> BandExecutionTables = Unique([
        "songs",
        "leaderboard_entries",
        "band_entries",
        "band_member_stats",
        "band_members",
        "band_team_rankings",
        "band_team_ranking_stats",
    ]);

    private static readonly string[] RivalsTables =
    [
        "rivals_status",
        "user_rivals",
        "rival_song_samples",
        "leaderboard_rivals",
        "leaderboard_rival_song_samples",
    ];

    private static readonly string[] PlayerStatsTables =
    [
        "player_stats",
        "player_stats_tiers",
    ];

    private static readonly string[] CacheTables =
    [
        "api_response_cache",
        "api_response_cache_staging",
    ];

    private static readonly string[] BandTables =
    [
        "band_entries",
        "band_member_stats",
        "band_members",
        "band_team_rankings",
        "band_team_ranking_stats",
    ];

    private static readonly string[] StatusTables =
    [
        "backfill_status",
        "backfill_progress",
        "history_recon_status",
        "history_recon_progress",
    ];

    public static readonly IReadOnlyDictionary<string, IReadOnlyList<string>> TablePresets =
        new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["post-scrape"] = Unique([
                .. CoreInputs,
                .. RankingsTables,
                .. RivalsTables,
                .. PlayerStatsTables,
                .. CacheTables,
                .. BandTables,
                .. StatusTables,
            ]),
            ["enrichment"] = Unique([
                "songs",
                "leaderboard_entries",
                "score_history",
                "account_names",
                "season_windows",
                "song_first_seen_season",
                .. BandTables,
            ]),
            ["refresh"] = Unique([
                "leaderboard_entries",
                "score_history",
                "registered_users",
                .. StatusTables,
                "season_windows",
                "song_first_seen_season",
                "leaderboard_population",
            ]),
            ["rankings"] = Unique([
                "songs",
                "leaderboard_entries",
                "score_history",
                "leaderboard_population",
                .. RankingsTables,
                .. BandTables,
            ]),
            ["rivals"] = Unique([
                "leaderboard_entries",
                "account_rankings",
                "composite_rankings",
                "registered_users",
                "account_names",
                .. RivalsTables,
            ]),
            ["player-stats"] = Unique([
                "songs",
                "leaderboard_entries",
                "score_history",
                "leaderboard_population",
                .. PlayerStatsTables,
            ]),
            ["precompute"] = Unique([
                "songs",
                "leaderboard_entries",
                "account_names",
                "registered_users",
                "account_rankings",
                "composite_rankings",
                .. RivalsTables,
                .. PlayerStatsTables,
                .. CacheTables,
            ]),
            ["band"] = Unique([
                "songs",
                "leaderboard_entries",
                .. BandTables,
            ]),
        };

    private static IReadOnlyList<string> Unique(IEnumerable<string> tables)
    {
        var results = new List<string>();
        foreach (var table in tables)
        {
            if (!results.Contains(table, StringComparer.OrdinalIgnoreCase))
                results.Add(table);
        }

        return results;
    }
}