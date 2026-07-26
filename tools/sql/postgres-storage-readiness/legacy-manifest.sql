SET statement_timeout = '10min';
SET lock_timeout = '2s';
SET default_transaction_read_only = on;
SET max_parallel_workers_per_gather = 2;
SET temp_file_limit = '256MB';
SET TIME ZONE 'UTC';

SELECT instrument, source,
       COUNT(*) AS row_count,
       COUNT(*) FILTER (WHERE band_members_json IS NOT NULL) AS band_context_rows,
       MIN(first_seen_at) AS min_first_seen_at,
       MAX(last_updated_at) AS max_last_updated_at,
       MIN(season) AS min_season,
       MAX(season) AS max_season,
       BIT_XOR(hashtextextended(concat_ws(E'\x1f',
           song_id, instrument, account_id, score::text,
           COALESCE(accuracy::text, ''), COALESCE(is_full_combo::text, ''),
           COALESCE(stars::text, ''), COALESCE(season::text, ''),
           COALESCE(percentile::text, ''), COALESCE(rank::text, ''), source,
           COALESCE(difficulty::text, ''), COALESCE(api_rank::text, ''),
           COALESCE(end_time, ''), COALESCE(band_members_json::text, ''),
           COALESCE(band_score::text, ''), COALESCE(base_score::text, ''),
           COALESCE(instrument_bonus::text, ''),
           COALESCE(overdrive_bonus::text, ''), COALESCE(instrument_combo, ''),
           first_seen_at::text, last_updated_at::text), 0)) AS checksum_xor,
       SUM(hashtextextended(
           concat_ws(E'\x1f', song_id, instrument, account_id), 1)::numeric)
           AS identity_checksum_sum
FROM leaderboard_entries
GROUP BY GROUPING SETS ((), (instrument), (instrument, source))
ORDER BY instrument NULLS FIRST, source NULLS FIRST;

WITH publication AS (
    SELECT published_scrape_id
    FROM scrape_publication_state
    WHERE id = TRUE
)
SELECT source.instrument,
       COUNT(*) AS mapped_scope_count,
       COUNT(*) FILTER (WHERE source.source_kind = 'snapshot') AS snapshot_scope_count,
       COUNT(*) FILTER (WHERE source.source_kind = 'empty') AS empty_scope_count,
       SUM(source.row_count) AS mapped_row_count,
       MIN(source.source_scrape_id) AS min_source_scrape_id,
       MAX(source.source_scrape_id) AS max_source_scrape_id
FROM leaderboard_published_scope_source source
JOIN publication ON publication.published_scrape_id = source.published_scrape_id
GROUP BY source.instrument
ORDER BY source.instrument;

SELECT SUM(pg_total_relation_size(child.oid)) AS total_bytes,
       SUM(pg_relation_size(child.oid)) AS heap_bytes,
       SUM(pg_indexes_size(child.oid)) AS index_bytes
FROM pg_inherits inheritance
JOIN pg_class child ON child.oid = inheritance.inhrelid
WHERE inheritance.inhparent = 'public.leaderboard_entries'::regclass;
