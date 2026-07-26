SET statement_timeout = '10min';
SET lock_timeout = '2s';
SET default_transaction_read_only = on;
SET max_parallel_workers_per_gather = 2;
SET temp_file_limit = '256MB';
SET TIME ZONE 'UTC';

SELECT source_kind, instrument,
       COUNT(*) AS row_count,
       MIN(id) AS min_id,
       MAX(id) AS max_id,
       MIN(observed_at) AS min_observed_at,
       MAX(observed_at) AS max_observed_at,
       BIT_XOR(hashtextextended(concat_ws(E'\x1f',
           id::text, account_id, song_id, instrument, score::text,
           COALESCE(accuracy::text, ''), COALESCE(is_full_combo::text, ''),
           COALESCE(stars::text, ''), COALESCE(difficulty::text, ''),
           COALESCE(season::text, ''), COALESCE(score_achieved_at::text, ''),
           source_kind, source_id, source_scope, COALESCE(solo_rank::text, ''),
           COALESCE(season_rank::text, ''), COALESCE(all_time_rank::text, ''),
           COALESCE(solo_percentile::text, ''), COALESCE(band_type, ''),
           COALESCE(team_key, ''), COALESCE(instrument_combo, ''),
           COALESCE(band_score::text, ''), COALESCE(band_rank::text, ''),
           COALESCE(band_percentile::text, ''), COALESCE(band_source, ''),
           COALESCE(member_index::text, ''), observed_at::text), 0)) AS checksum_xor,
       SUM(hashtextextended(
           concat_ws(E'\x1f', account_id, song_id, instrument, source_kind, source_id),
           1)::numeric) AS identity_checksum_sum
FROM player_score_observations
GROUP BY GROUPING SETS ((), (source_kind), (source_kind, instrument))
ORDER BY source_kind NULLS FIRST, instrument NULLS FIRST;

SELECT pg_total_relation_size('public.player_score_observations'::regclass) AS total_bytes,
       pg_relation_size('public.player_score_observations'::regclass) AS heap_bytes,
       pg_indexes_size('public.player_score_observations'::regclass) AS index_bytes;
