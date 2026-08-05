\set ON_ERROR_STOP on

BEGIN TRANSACTION READ ONLY;
SET LOCAL lock_timeout = '2s';
SET LOCAL statement_timeout = '15s';
SET LOCAL row_security = off;

COPY (
    SELECT current_database() AS database_name,
           current_setting('server_version_num') AS server_version_num,
           COALESCE(publication.published_scrape_id::text, '')
               AS published_scrape_id,
           COALESCE(publication.published_at::text, '')
               AS published_at,
           COALESCE(publication.updated_at::text, '')
               AS publication_updated_at,
           COALESCE(publication.public_reads_frozen, false)::text
               AS public_reads_frozen,
           COALESCE(publication.working_publication_id::text, '')
               AS working_publication_id,
           COALESCE(publication.current_publication_id::text, '')
               AS current_publication_id,
           COALESCE(generation.scrape_id::text, '')
               AS current_publication_scrape_id,
           COALESCE(generation.status, '')
               AS current_publication_status,
           COALESCE(generation.published_at::text, '')
               AS current_publication_published_at,
           COALESCE(cleanup_scrape.status, '')
               AS cleanup_scrape_status,
           (cleanup_scrape.completed_at IS NOT NULL)::text
               AS cleanup_scrape_completed,
           COALESCE(cleanup_scrape.completed_at::text, '')
               AS cleanup_scrape_completed_at,
           (
               SELECT count(*) FROM public.scrape_log
               WHERE status = 'running'
           )::text AS active_scrape_count,
           COALESCE(lower(worker.status), 'absent') AS worker_status,
           (worker.current_operation_json IS NOT NULL)::text
               AS worker_current_operation,
           (
               SELECT count(*) FROM pg_catalog.pg_locks WHERE NOT granted
           )::text AS ungranted_lock_count,
           (
               SELECT count(*)
               FROM pg_catalog.pg_stat_activity
               WHERE pid <> pg_backend_pid()
                 AND backend_type = 'client backend'
                 AND state = 'active'
                 AND now() - query_start > interval '30 seconds'
           )::text AS long_query_count,
           (
               SELECT count(*)
               FROM pg_catalog.pg_stat_activity
               WHERE pid <> pg_backend_pid()
                 AND state = 'active'
                 AND query ~*
                     '(leaderboard_current_entries|leaderboard_entry_versions|leaderboard_logical_write_metrics|player_score_observations|player_score_observation_union|band_song_team_rankings|band_song_team_ranking_state|ranking_deltas|ranking_delta_tiers|rank_history_deltas|composite_ranking_deltas|combo_ranking_deltas)'
           )::text AS target_query_count,
           (SELECT count(*) FROM pg_catalog.pg_stat_progress_vacuum)::text
               AS active_vacuum_count,
           (SELECT count(*) FROM pg_catalog.pg_stat_progress_create_index)::text
               AS active_index_build_count,
           (
               SELECT count(*)
               FROM pg_catalog.pg_stat_activity
               WHERE pid <> pg_backend_pid()
                 AND state = 'active'
                 AND (
                     query ~* '(^|[[:space:]])vacuum[[:space:]]+full'
                     OR query ~* 'repack'
                     OR query ~* 'rewrite'
                 )
           )::text AS active_rewrite_count,
           (
               SELECT count(*)
               FROM public.scrape_phase_outcomes
               WHERE scrape_id = 1278
                 AND criticality = 'publication_critical'
                 AND status = 'failed'
           )::text AS critical_phase_failure_count,
           pg_catalog.pg_try_advisory_xact_lock(
               5067481511116519501)::text AS ddl_guard_available,
           pg_catalog.pg_try_advisory_xact_lock(
               5067481511116519502)::text AS sequence_guard_available
    FROM public.scrape_publication_state publication
    LEFT JOIN public.publication_generations generation
      ON generation.publication_id = publication.current_publication_id
    LEFT JOIN public.scrape_log cleanup_scrape
      ON cleanup_scrape.id = 1278
    LEFT JOIN public.service_worker_status worker
      ON worker.worker_key = 'scraper'
    WHERE publication.id = TRUE
) TO STDOUT WITH (FORMAT CSV, HEADER TRUE);

COMMIT;
