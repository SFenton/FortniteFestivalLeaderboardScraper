\set ON_ERROR_STOP on

SET statement_timeout = '0';
SET lock_timeout = '5s';
SET synchronous_commit = off;
SET wal_compression = on;
SET work_mem = '768MB';
SET max_parallel_workers_per_gather = 2;

CREATE TABLE public.band_team_rank_history_points_v2_quad
    PARTITION OF public.band_team_rank_history_points_v2
    FOR VALUES IN ('Band_Quad');

INSERT INTO public.band_team_rank_history_points_v2_quad (
    band_type,
    ranking_scope,
    combo_id,
    team_key,
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
    'Band_Quad',
    CASE points.scope_id WHEN 0 THEN 'overall' ELSE 'combo' END,
    CASE WHEN points.combo_ref = 0 THEN '' ELSE combo.combo_id END,
    team.team_key,
    points.snapshot_date,
    points.snapshot_id,
    points.generation_id,
    points.snapshot_taken_at,
    encode(points.row_fingerprint, 'hex'),
    points.adjusted_skill_rank,
    points.weighted_rank,
    points.fc_rate_rank,
    points.total_score_rank,
    points.adjusted_skill_rating,
    points.weighted_rating,
    points.fc_rate,
    points.total_score,
    points.songs_played,
    points.coverage,
    points.full_combo_count,
    points.total_charted_songs,
    points.total_ranked_teams,
    points.raw_weighted_rating,
    points.raw_skill_rating
FROM public.band_team_rank_history_points_v3_quad points
JOIN public.band_rank_history_team_v3_quad team USING (team_id)
LEFT JOIN public.band_rank_history_combo_v3_quad combo USING (combo_ref);

ANALYZE public.band_team_rank_history_points_v2_quad;
CHECKPOINT;

