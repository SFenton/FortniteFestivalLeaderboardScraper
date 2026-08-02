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

    internal const string CurrentResolvedEntriesCte =
        CurrentSourcesCte +
        """
        ,
        selected_sources AS (
            SELECT source.song_id,
                   source.source_kind,
                   source.source_snapshot_id
            FROM published_sources source
            WHERE source.instrument = @instrument
        ),
        base_rows AS (
            SELECT snapshot.song_id,
                   snapshot.account_id,
                   snapshot.score,
                   snapshot.accuracy,
                   snapshot.is_full_combo,
                   snapshot.stars,
                   snapshot.rank,
                   snapshot.api_rank,
                   snapshot.first_seen_at,
                   snapshot.end_time,
                   1 AS origin_precedence,
                   0 AS source_priority
            FROM leaderboard_entries_snapshot snapshot
            JOIN selected_sources selected
              ON selected.song_id = snapshot.song_id
             AND selected.source_kind = 'snapshot'
             AND selected.source_snapshot_id = snapshot.snapshot_id
            WHERE snapshot.instrument = @instrument
            UNION ALL
            SELECT overlay.song_id,
                   overlay.account_id,
                   overlay.score,
                   overlay.accuracy,
                   overlay.is_full_combo,
                   overlay.stars,
                   overlay.rank,
                   overlay.api_rank,
                   overlay.first_seen_at,
                   overlay.end_time,
                   0 AS origin_precedence,
                   overlay.source_priority
            FROM leaderboard_entries_overlay overlay
            WHERE overlay.instrument = @instrument
              AND EXISTS (
                  SELECT 1
                  FROM selected_sources selected
                  WHERE selected.song_id = overlay.song_id
              )
        ),
        resolved_rows AS (
            SELECT DISTINCT ON (song_id, account_id)
                   song_id,
                   account_id,
                   score,
                   accuracy,
                   is_full_combo,
                   stars,
                   rank,
                   api_rank,
                   first_seen_at,
                   end_time
            FROM base_rows
            ORDER BY song_id,
                     account_id,
                     origin_precedence ASC,
                     source_priority DESC
        )
        """;
}
