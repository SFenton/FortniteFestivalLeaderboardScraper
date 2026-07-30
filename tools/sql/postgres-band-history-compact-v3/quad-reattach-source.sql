\set ON_ERROR_STOP on

BEGIN;
SET LOCAL lock_timeout = '5s';
SET LOCAL statement_timeout = '30min';

ALTER TABLE public.band_team_rank_history_points_v2
    ATTACH PARTITION public.band_team_rank_history_points_v2_quad
    FOR VALUES IN ('Band_Quad');

COMMIT;

