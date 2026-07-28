\set ON_ERROR_STOP on

SET statement_timeout = '90min';
SET lock_timeout = '5s';
SET synchronous_commit = off;
SET wal_compression = on;
SET work_mem = '768MB';
SET max_parallel_workers_per_gather = 2;

INSERT INTO public.band_team_rank_history_points_v3_duets (
    team_id,
    scope_id,
    combo_ref,
    snapshot_date,
    snapshot_id,
    generation_id,
    snapshot_taken_at,
    row_fingerprint,
    adjusted_skill_rank,
    weighted_rank,
    fc_rate_rank,
    total_score_rank,
    adjusted_skill_rating,
    weighted_rating,
    fc_rate,
    total_score,
    songs_played,
    coverage,
    full_combo_count,
    total_charted_songs,
    total_ranked_teams,
    raw_weighted_rating,
    raw_skill_rating)
SELECT
    team.team_id,
    CASE source.ranking_scope WHEN 'overall' THEN 0::smallint ELSE 1::smallint END,
    COALESCE(combo.combo_ref, 0),
    source.snapshot_date,
    source.snapshot_id,
    source.generation_id,
    source.snapshot_taken_at,
    decode(source.row_fingerprint, 'hex'),
    source.adjusted_skill_rank,
    source.weighted_rank,
    source.fc_rate_rank,
    source.total_score_rank,
    source.adjusted_skill_rating,
    source.weighted_rating,
    source.fc_rate,
    source.total_score,
    source.songs_played,
    source.coverage,
    source.full_combo_count,
    source.total_charted_songs,
    source.total_ranked_teams,
    source.raw_weighted_rating,
    source.raw_skill_rating
FROM public.band_team_rank_history_points_v2_duets source
JOIN public.band_rank_history_team_v3_duets team
  ON team.team_key = source.team_key
LEFT JOIN public.band_rank_history_combo_v3_duets combo
  ON combo.combo_id = source.combo_id
 AND source.combo_id <> ''
WHERE source.snapshot_date >= :'start_date'::date
  AND source.snapshot_date < :'end_date'::date;

CHECKPOINT;
