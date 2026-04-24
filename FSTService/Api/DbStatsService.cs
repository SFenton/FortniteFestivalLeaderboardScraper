using System.Collections.Concurrent;
using Npgsql;

namespace FSTService.Api;

/// <summary>
/// Read-only access to Postgres observability views (pg_stat_statements,
/// pg_stat_user_tables). Results are cached in-process for 60 seconds to
/// keep admin polling cheap.
/// </summary>
public sealed class DbStatsService
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(60);

    private readonly NpgsqlDataSource _ds;
    private readonly ILogger<DbStatsService> _log;
    private readonly ConcurrentDictionary<string, CacheEntry> _cache = new();

    public DbStatsService(NpgsqlDataSource dataSource, ILogger<DbStatsService> log)
    {
        _ds = dataSource;
        _log = log;
    }

    public async Task<IReadOnlyList<QueryStat>> GetTopQueriesAsync(string orderBy, int limit, CancellationToken ct)
    {
        var orderColumn = orderBy switch
        {
            "calls" => "calls",
            "mean" => "mean_exec_time",
            _ => "total_exec_time",
        };

        var cacheKey = $"queries:{orderColumn}:{limit}";
        if (TryGetFromCache<IReadOnlyList<QueryStat>>(cacheKey, out var cached))
            return cached;

        var results = new List<QueryStat>(limit);

        await using var conn = await _ds.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        // pg_stat_statements availability is optional — caller handles error.
        cmd.CommandText = $@"
            SELECT
                LEFT(query, 500) AS query_text,
                calls,
                ROUND(total_exec_time::numeric, 2) AS total_ms,
                ROUND(mean_exec_time::numeric, 3) AS mean_ms,
                ROUND(stddev_exec_time::numeric, 3) AS stddev_ms,
                rows,
                shared_blks_hit,
                shared_blks_read
            FROM pg_stat_statements
            WHERE dbid = (SELECT oid FROM pg_database WHERE datname = current_database())
            ORDER BY {orderColumn} DESC NULLS LAST
            LIMIT @limit;";
        cmd.Parameters.AddWithValue("limit", limit);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            results.Add(new QueryStat
            {
                Query = reader.GetString(0),
                Calls = reader.GetInt64(1),
                TotalMs = (double)reader.GetDecimal(2),
                MeanMs = (double)reader.GetDecimal(3),
                StddevMs = (double)reader.GetDecimal(4),
                Rows = reader.GetInt64(5),
                SharedBlksHit = reader.GetInt64(6),
                SharedBlksRead = reader.GetInt64(7),
            });
        }

        StoreInCache<IReadOnlyList<QueryStat>>(cacheKey, results);
        return results;
    }

    public async Task<IReadOnlyList<TableBloat>> GetTableBloatAsync(int limit, CancellationToken ct)
    {
        var cacheKey = $"bloat:{limit}";
        if (TryGetFromCache<IReadOnlyList<TableBloat>>(cacheKey, out var cached))
            return cached;

        var results = new List<TableBloat>(limit);

        await using var conn = await _ds.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT
                schemaname,
                relname,
                n_live_tup,
                n_dead_tup,
                CASE
                    WHEN n_live_tup = 0 THEN NULL
                    ELSE ROUND((n_dead_tup::numeric / n_live_tup::numeric) * 100, 2)
                END AS dead_pct,
                pg_total_relation_size(relid) AS total_bytes,
                pg_relation_size(relid) AS heap_bytes,
                last_autovacuum,
                last_vacuum,
                last_autoanalyze,
                last_analyze
            FROM pg_stat_user_tables
            ORDER BY pg_total_relation_size(relid) DESC
            LIMIT @limit;";
        cmd.Parameters.AddWithValue("limit", limit);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            results.Add(new TableBloat
            {
                Schema = reader.GetString(0),
                Table = reader.GetString(1),
                LiveTuples = reader.GetInt64(2),
                DeadTuples = reader.GetInt64(3),
                DeadPercent = reader.IsDBNull(4) ? null : (double)reader.GetDecimal(4),
                TotalBytes = reader.GetInt64(5),
                HeapBytes = reader.GetInt64(6),
                LastAutovacuum = reader.IsDBNull(7) ? null : reader.GetDateTime(7),
                LastVacuum = reader.IsDBNull(8) ? null : reader.GetDateTime(8),
                LastAutoanalyze = reader.IsDBNull(9) ? null : reader.GetDateTime(9),
                LastAnalyze = reader.IsDBNull(10) ? null : reader.GetDateTime(10),
            });
        }

        StoreInCache<IReadOnlyList<TableBloat>>(cacheKey, results);
        return results;
    }

    private bool TryGetFromCache<T>(string key, out T value) where T : class
    {
        if (_cache.TryGetValue(key, out var entry) && entry.ExpiresAt > DateTime.UtcNow && entry.Value is T typed)
        {
            value = typed;
            return true;
        }

        value = null!;
        return false;
    }

    private void StoreInCache<T>(string key, T value) where T : class
    {
        _cache[key] = new CacheEntry(value, DateTime.UtcNow.Add(CacheTtl));
    }

    private sealed record CacheEntry(object Value, DateTime ExpiresAt);

    public sealed record QueryStat
    {
        public string Query { get; init; } = string.Empty;
        public long Calls { get; init; }
        public double TotalMs { get; init; }
        public double MeanMs { get; init; }
        public double StddevMs { get; init; }
        public long Rows { get; init; }
        public long SharedBlksHit { get; init; }
        public long SharedBlksRead { get; init; }
    }

    public sealed record TableBloat
    {
        public string Schema { get; init; } = string.Empty;
        public string Table { get; init; } = string.Empty;
        public long LiveTuples { get; init; }
        public long DeadTuples { get; init; }
        public double? DeadPercent { get; init; }
        public long TotalBytes { get; init; }
        public long HeapBytes { get; init; }
        public DateTime? LastAutovacuum { get; init; }
        public DateTime? LastVacuum { get; init; }
        public DateTime? LastAutoanalyze { get; init; }
        public DateTime? LastAnalyze { get; init; }
    }
}
