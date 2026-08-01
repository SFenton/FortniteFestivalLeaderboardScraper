using FSTService.Persistence;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace FSTService.Tests.Helpers;

internal static class ScrapeRunTestHelper
{
    public static void EnsureAllocated(
        NpgsqlDataSource dataSource,
        long scrapeId,
        bool completed,
        DateTime? startedAtUtc = null,
        DateTime? completedAtUtc = null)
    {
        using (var conn = dataSource.OpenConnection())
        using (var exists = conn.CreateCommand())
        {
            exists.CommandText = """
                SELECT EXISTS (
                    SELECT 1
                    FROM scrape_log
                    WHERE id = @scrapeId
                )
                """;
            exists.Parameters.AddWithValue("scrapeId", scrapeId);
            if ((bool)exists.ExecuteScalar()!)
                return;
        }

        using (var conn = dataSource.OpenConnection())
        using (var sequence = conn.CreateCommand())
        {
            sequence.CommandText =
                "SELECT setval('scrape_log_id_seq', @scrapeId, false)";
            sequence.Parameters.AddWithValue("scrapeId", scrapeId);
            sequence.ExecuteNonQuery();
        }

        var meta = new MetaDatabase(
            dataSource,
            NullLogger<MetaDatabase>.Instance);
        var catalogToken = new FestivalPersistence(dataSource)
            .GetExactSongCatalogTokenAsync()
            .GetAwaiter()
            .GetResult()
            ?? throw new InvalidOperationException(
                "Test scrape allocation requires an exact catalog token.");
        var allocatedScrapeId = meta.StartScrapeRun(catalogToken);
        if (allocatedScrapeId != scrapeId)
        {
            throw new InvalidOperationException(
                $"Expected scrape {scrapeId}, allocated {allocatedScrapeId}.");
        }

        if (completed)
        {
            meta.CompleteScrapeRun(
                scrapeId,
                songsScraped: 1,
                totalEntries: 1,
                totalRequests: 1,
                totalBytes: 1);
        }

        if (startedAtUtc.HasValue || completedAtUtc.HasValue)
        {
            using var conn = dataSource.OpenConnection();
            using var update = conn.CreateCommand();
            update.CommandText = """
                UPDATE scrape_log
                SET started_at = COALESCE(@startedAt, started_at),
                    completed_at = CASE
                        WHEN @completed THEN COALESCE(
                            @completedAt,
                            completed_at,
                            started_at)
                        ELSE NULL
                    END,
                    status = CASE
                        WHEN @completed THEN 'completed'
                        ELSE 'running'
                    END
                WHERE id = @scrapeId
                """;
            update.Parameters.AddWithValue("scrapeId", scrapeId);
            update.Parameters.AddWithValue("completed", completed);
            update.Parameters.AddWithValue(
                "startedAt",
                (object?)startedAtUtc ?? DBNull.Value);
            update.Parameters.AddWithValue(
                "completedAt",
                (object?)completedAtUtc ?? DBNull.Value);
            update.ExecuteNonQuery();
        }
    }
}
