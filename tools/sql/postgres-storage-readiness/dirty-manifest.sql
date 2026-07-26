SET statement_timeout = '10min';
SET lock_timeout = '2s';
SET default_transaction_read_only = on;
SET max_parallel_workers_per_gather = 2;
SET temp_file_limit = '256MB';
SET TIME ZONE 'UTC';

SELECT 'scrape_dirty_account' AS table_name, source, dirty_reason,
       COUNT(*) AS row_count, MIN(scrape_id) AS min_scrape_id,
       MAX(scrape_id) AS max_scrape_id, MIN(created_at) AS min_created_at,
       MAX(created_at) AS max_created_at,
       BIT_XOR(hashtextextended(concat_ws(E'\x1f', scrape_id::text, account_id,
           source, dirty_reason, created_at::text), 0)) AS checksum_xor,
       SUM(hashtextextended(concat_ws(E'\x1f', scrape_id::text, account_id,
           source, dirty_reason, created_at::text), 1)::numeric) AS checksum_sum
FROM scrape_dirty_account
GROUP BY GROUPING SETS ((), (source, dirty_reason))
ORDER BY source NULLS FIRST, dirty_reason NULLS FIRST;

SELECT 'scrape_dirty_song_instrument' AS table_name, source, dirty_reason,
       COUNT(*) AS row_count, MIN(scrape_id) AS min_scrape_id,
       MAX(scrape_id) AS max_scrape_id, MIN(created_at) AS min_created_at,
       MAX(created_at) AS max_created_at,
       BIT_XOR(hashtextextended(concat_ws(E'\x1f', scrape_id::text, song_id,
           instrument, source, dirty_reason, created_at::text), 0)) AS checksum_xor,
       SUM(hashtextextended(concat_ws(E'\x1f', scrape_id::text, song_id,
           instrument, source, dirty_reason, created_at::text), 1)::numeric) AS checksum_sum
FROM scrape_dirty_song_instrument
GROUP BY GROUPING SETS ((), (source, dirty_reason))
ORDER BY source NULLS FIRST, dirty_reason NULLS FIRST;

SELECT 'scrape_dirty_band_scope' AS table_name, source, dirty_reason,
       COUNT(*) AS row_count, MIN(scrape_id) AS min_scrape_id,
       MAX(scrape_id) AS max_scrape_id, MIN(created_at) AS min_created_at,
       MAX(created_at) AS max_created_at,
       BIT_XOR(hashtextextended(concat_ws(E'\x1f', scrape_id::text, song_id,
           band_type, ranking_scope, scope_combo_id, source, dirty_reason,
           created_at::text), 0)) AS checksum_xor,
       SUM(hashtextextended(concat_ws(E'\x1f', scrape_id::text, song_id,
           band_type, ranking_scope, scope_combo_id, source, dirty_reason,
           created_at::text), 1)::numeric) AS checksum_sum
FROM scrape_dirty_band_scope
GROUP BY GROUPING SETS ((), (source, dirty_reason))
ORDER BY source NULLS FIRST, dirty_reason NULLS FIRST;

SELECT 'scrape_dirty_band_team' AS table_name, source, dirty_reason,
       COUNT(*) AS row_count, MIN(scrape_id) AS min_scrape_id,
       MAX(scrape_id) AS max_scrape_id, MIN(created_at) AS min_created_at,
       MAX(created_at) AS max_created_at,
       BIT_XOR(hashtextextended(concat_ws(E'\x1f', scrape_id::text, band_type,
           team_key, source, dirty_reason, created_at::text), 0)) AS checksum_xor,
       SUM(hashtextextended(concat_ws(E'\x1f', scrape_id::text, band_type,
           team_key, source, dirty_reason, created_at::text), 1)::numeric) AS checksum_sum
FROM scrape_dirty_band_team
GROUP BY GROUPING SETS ((), (source, dirty_reason))
ORDER BY source NULLS FIRST, dirty_reason NULLS FIRST;

SELECT table_name, total_bytes, heap_bytes, index_bytes
FROM (VALUES
 ('scrape_dirty_account',
  pg_total_relation_size('public.scrape_dirty_account'::regclass),
  pg_relation_size('public.scrape_dirty_account'::regclass),
  pg_indexes_size('public.scrape_dirty_account'::regclass)),
 ('scrape_dirty_song_instrument',
  pg_total_relation_size('public.scrape_dirty_song_instrument'::regclass),
  pg_relation_size('public.scrape_dirty_song_instrument'::regclass),
  pg_indexes_size('public.scrape_dirty_song_instrument'::regclass)),
 ('scrape_dirty_band_scope',
  pg_total_relation_size('public.scrape_dirty_band_scope'::regclass),
  pg_relation_size('public.scrape_dirty_band_scope'::regclass),
  pg_indexes_size('public.scrape_dirty_band_scope'::regclass)),
 ('scrape_dirty_band_team',
  pg_total_relation_size('public.scrape_dirty_band_team'::regclass),
  pg_relation_size('public.scrape_dirty_band_team'::regclass),
  pg_indexes_size('public.scrape_dirty_band_team'::regclass))
) AS sizes(table_name, total_bytes, heap_bytes, index_bytes)
ORDER BY total_bytes DESC;
