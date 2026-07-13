namespace FSTService.Persistence;

internal static class PublishedSoloScopeSql
{
    internal const string CurrentSourcesCte = """
        publication AS (
            SELECT published_scrape_id
            FROM scrape_publication_state
            WHERE id = TRUE
        ),
        published_sources AS (
            SELECT
                source.published_scrape_id,
                source.song_id,
                source.instrument,
                source.source_kind,
                source.source_snapshot_id,
                source.source_scrape_id,
                COALESCE(source.source_snapshot_id, source.source_scrape_id) AS projection_source_snapshot_id,
                source.row_count,
                source.reported_total_entries,
                source.reported_total_pages
            FROM leaderboard_published_scope_source source
            JOIN publication
              ON publication.published_scrape_id = source.published_scrape_id
            WHERE source.scope_kind = 'alltime'
              AND source.is_complete
        )
        """;
}
