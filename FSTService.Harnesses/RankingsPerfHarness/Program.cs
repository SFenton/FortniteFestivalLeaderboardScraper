using System.Diagnostics;
using System.Text.Json;
using FSTService.Persistence;
using FSTService.Scraping;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using RankingsPerfHarness;

// ═══════════════════════════════════════════════════════════════
//  RankingsPerfHarness — Phase 0 diagnostic tool
//
//  Subcommands:
//    latency   Benchmark InstrumentDatabase.GetRankingsAtLeeway
//              under dense (UseTiers=false) vs tiers (UseTiers=true)
//              across a leeway sweep × metrics × pages.
//    pgstat    Snapshot pg_stat_user_indexes + pg_stat_user_tables to JSON.
//    endpoints HTTP latency sweep against /api/rankings/* on a running service.
//
//  All subcommands are read-only — no data mutation.
// ═══════════════════════════════════════════════════════════════

if (args.Length == 0) { PrintUsage(); return 1; }

var sub = args[0].ToLowerInvariant();
var rest = args.Skip(1).ToArray();
return sub switch
{
    "latency"   => await RunLatency(rest),
    "pgstat"    => await RunPgStat(rest),
    "endpoints" => await RunEndpoints(rest),
    _           => Fail($"Unknown subcommand: {sub}")
};

static int Fail(string msg) { Console.Error.WriteLine(msg); PrintUsage(); return 2; }

static void PrintUsage()
{
    Console.Error.WriteLine("""
        Usage:
          RankingsPerfHarness latency   --pg <conn> [--iterations N] [--warmup M] [--out <path>]
          RankingsPerfHarness pgstat    --pg <conn> [--out <path>]
          RankingsPerfHarness endpoints --base-url <url> [--iterations N] [--warmup M] [--out <path>]
        """);
}

// ─── latency: dense vs tiers sweep ───────────────────────────────────────────

static async Task<int> RunLatency(string[] args)
{
    string? pg = null; int iterations = 50; int warmup = 5; string? outPath = null;
    for (int i = 0; i < args.Length; i++)
    {
        switch (args[i])
        {
            case "--pg":         pg = args[++i]; break;
            case "--iterations": iterations = int.Parse(args[++i]); break;
            case "--warmup":     warmup = int.Parse(args[++i]); break;
            case "--out":        outPath = args[++i]; break;
        }
    }
    if (pg is null) return Fail("--pg required");

    await using var ds = NpgsqlDataSource.Create(pg);
    var instruments = GlobalLeaderboardScraper.AllInstruments;
    var metrics = new[] { "adjusted", "weighted", "fcrate", "totalscore", "maxscore" };
    var leewayBuckets = new[] { -5.0, -2.5, 0.0, 2.5, 5.0 };
    var pages = new[] { 1, 2, 10, 100 };

    var results = new List<Dictionary<string, object>>();

    foreach (var instrument in instruments)
    {
        foreach (var useTiers in new[] { false, true })
        {
            var db = new InstrumentDatabase(instrument, ds, NullLogger<InstrumentDatabase>.Instance) { UseTiers = useTiers };

            foreach (var metric in metrics)
            foreach (var bucket in leewayBuckets)
            foreach (var page in pages)
            {
                var coll = new TimingCollector();

                for (int i = 0; i < warmup; i++) db.GetRankingsAtLeeway(bucket, metric, page, 50);

                for (int i = 0; i < iterations; i++)
                {
                    var sw = Stopwatch.StartNew();
                    var (rows, _) = db.GetRankingsAtLeeway(bucket, metric, page, 50);
                    sw.Stop();
                    coll.Record(sw.Elapsed);
                    _ = rows.Count;
                }

                var s = coll.Snapshot();
                var row = new Dictionary<string, object>
                {
                    ["instrument"] = instrument,
                    ["path"] = useTiers ? "tiers" : "dense",
                    ["metric"] = metric,
                    ["leeway"] = bucket,
                    ["page"] = page,
                    ["count"] = s.Count,
                    ["mean_ms"] = Math.Round(s.MeanMs, 3),
                    ["p50_ms"] = Math.Round(s.P50Ms, 3),
                    ["p95_ms"] = Math.Round(s.P95Ms, 3),
                    ["p99_ms"] = Math.Round(s.P99Ms, 3),
                    ["max_ms"] = Math.Round(s.MaxMs, 3),
                };
                results.Add(row);
                Console.WriteLine($"{instrument,-24} {(useTiers?"tiers":"dense"),-5} {metric,-10} leeway={bucket,5:F1} page={page,3}  " +
                                  $"p50={s.P50Ms,7:F2}ms p95={s.P95Ms,7:F2}ms p99={s.P99Ms,7:F2}ms max={s.MaxMs,7:F2}ms");
            }
        }
    }

    EmitJson(outPath, new { iterations, warmup, results });
    return 0;
}

// ─── pgstat: snapshot planner/index stats ────────────────────────────────────

static async Task<int> RunPgStat(string[] args)
{
    string? pg = null; string? outPath = null;
    for (int i = 0; i < args.Length; i++)
    {
        switch (args[i])
        {
            case "--pg":  pg = args[++i]; break;
            case "--out": outPath = args[++i]; break;
        }
    }
    if (pg is null) return Fail("--pg required");

    await using var ds = NpgsqlDataSource.Create(pg);
    await using var conn = await ds.OpenConnectionAsync();

    var indexes = new List<Dictionary<string, object?>>();
    await using (var cmd = conn.CreateCommand())
    {
        cmd.CommandText = """
            SELECT schemaname, relname, indexrelname,
                   idx_scan, idx_tup_read, idx_tup_fetch
            FROM pg_stat_user_indexes
            ORDER BY idx_scan DESC NULLS LAST
            """;
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
            indexes.Add(new()
            {
                ["schema"]        = r.GetString(0),
                ["table"]         = r.GetString(1),
                ["index"]         = r.GetString(2),
                ["idx_scan"]      = r.IsDBNull(3) ? null : (object)r.GetInt64(3),
                ["idx_tup_read"]  = r.IsDBNull(4) ? null : (object)r.GetInt64(4),
                ["idx_tup_fetch"] = r.IsDBNull(5) ? null : (object)r.GetInt64(5),
            });
    }

    var tables = new List<Dictionary<string, object?>>();
    await using (var cmd = conn.CreateCommand())
    {
        cmd.CommandText = """
            SELECT schemaname, relname,
                   seq_scan, seq_tup_read,
                   idx_scan, idx_tup_fetch,
                   n_tup_ins, n_tup_upd, n_tup_del,
                   n_tup_hot_upd, n_live_tup, n_dead_tup,
                   n_mod_since_analyze
            FROM pg_stat_user_tables
            ORDER BY n_tup_upd DESC NULLS LAST
            """;
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
            tables.Add(new()
            {
                ["schema"]              = r.GetString(0),
                ["table"]               = r.GetString(1),
                ["seq_scan"]            = r.IsDBNull(2) ? null : (object)r.GetInt64(2),
                ["seq_tup_read"]        = r.IsDBNull(3) ? null : (object)r.GetInt64(3),
                ["idx_scan"]            = r.IsDBNull(4) ? null : (object)r.GetInt64(4),
                ["idx_tup_fetch"]       = r.IsDBNull(5) ? null : (object)r.GetInt64(5),
                ["n_tup_ins"]           = r.IsDBNull(6) ? null : (object)r.GetInt64(6),
                ["n_tup_upd"]           = r.IsDBNull(7) ? null : (object)r.GetInt64(7),
                ["n_tup_del"]           = r.IsDBNull(8) ? null : (object)r.GetInt64(8),
                ["n_tup_hot_upd"]       = r.IsDBNull(9) ? null : (object)r.GetInt64(9),
                ["n_live_tup"]          = r.IsDBNull(10) ? null : (object)r.GetInt64(10),
                ["n_dead_tup"]          = r.IsDBNull(11) ? null : (object)r.GetInt64(11),
                ["n_mod_since_analyze"] = r.IsDBNull(12) ? null : (object)r.GetInt64(12),
            });
    }

    var snapshot = new
    {
        captured_at_utc = DateTime.UtcNow.ToString("o"),
        indexes,
        tables,
    };
    EmitJson(outPath, snapshot);
    Console.WriteLine($"Captured {indexes.Count} indexes, {tables.Count} tables.");
    return 0;
}

// ─── endpoints: HTTP latency sweep on a running service ──────────────────────

static async Task<int> RunEndpoints(string[] args)
{
    string? baseUrl = null; int iterations = 30; int warmup = 3; string? outPath = null;
    for (int i = 0; i < args.Length; i++)
    {
        switch (args[i])
        {
            case "--base-url":   baseUrl = args[++i].TrimEnd('/'); break;
            case "--iterations": iterations = int.Parse(args[++i]); break;
            case "--warmup":     warmup = int.Parse(args[++i]); break;
            case "--out":        outPath = args[++i]; break;
        }
    }
    if (baseUrl is null) return Fail("--base-url required");

    using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
    var instruments = GlobalLeaderboardScraper.AllInstruments;
    var leewayValues = new double?[] { null, -5.0, -2.5, 0.0, 2.5, 5.0 };
    var metrics = new[] { "adjusted", "weighted", "fcrate", "totalscore", "maxscore" };
    var pages = new[] { 1, 2, 10 };

    var results = new List<Dictionary<string, object?>>();

    foreach (var instrument in instruments)
    foreach (var metric in metrics)
    foreach (var leeway in leewayValues)
    foreach (var page in pages)
    {
        var qs = $"?page={page}&pageSize=50&rankBy={metric}" + (leeway.HasValue ? $"&leeway={leeway.Value}" : "");
        var url = $"{baseUrl}/api/rankings/{instrument}{qs}";

        var coll = new TimingCollector();
        int status = 0;
        for (int i = 0; i < warmup; i++)
        {
            try { (await http.GetAsync(url)).EnsureSuccessStatusCode(); } catch { /* surface below */ }
        }
        for (int i = 0; i < iterations; i++)
        {
            var sw = Stopwatch.StartNew();
            try
            {
                using var resp = await http.GetAsync(url);
                status = (int)resp.StatusCode;
                _ = await resp.Content.ReadAsStringAsync();
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"  {url} failed: {ex.Message}");
                break;
            }
            sw.Stop();
            coll.Record(sw.Elapsed);
        }
        var s = coll.Snapshot();
        results.Add(new()
        {
            ["instrument"] = instrument,
            ["metric"] = metric,
            ["leeway"] = leeway,
            ["page"] = page,
            ["status"] = status,
            ["count"] = s.Count,
            ["mean_ms"] = Math.Round(s.MeanMs, 3),
            ["p50_ms"] = Math.Round(s.P50Ms, 3),
            ["p95_ms"] = Math.Round(s.P95Ms, 3),
            ["p99_ms"] = Math.Round(s.P99Ms, 3),
            ["max_ms"] = Math.Round(s.MaxMs, 3),
        });
        Console.WriteLine($"{instrument,-24} {metric,-10} leeway={(leeway?.ToString("F1") ?? "none"),5} page={page,3}  " +
                          $"p50={s.P50Ms,7:F2}ms p95={s.P95Ms,7:F2}ms p99={s.P99Ms,7:F2}ms max={s.MaxMs,7:F2}ms");
    }

    EmitJson(outPath, new { baseUrl, iterations, warmup, results });
    return 0;
}

// ─── shared ─────────────────────────────────────────────────────────────────

static void EmitJson(string? outPath, object payload)
{
    var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true });
    if (outPath is null)
    {
        Console.WriteLine();
        Console.WriteLine(json);
    }
    else
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath)) ?? ".");
        File.WriteAllText(outPath, json);
        Console.WriteLine($"Wrote {outPath}");
    }
}
