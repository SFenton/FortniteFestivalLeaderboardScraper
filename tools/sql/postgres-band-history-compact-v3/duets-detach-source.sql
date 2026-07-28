\set ON_ERROR_STOP on

BEGIN;
SET LOCAL lock_timeout = '5s';
SET LOCAL statement_timeout = '10min';

DO $$
DECLARE
    ready_rows BIGINT;
    source_rows BIGINT;
BEGIN
    SELECT row_count
    INTO ready_rows
    FROM public.band_rank_history_compact_v3_state
    WHERE band_type = 'Band_Duets'
      AND status = 'ready';

    IF ready_rows IS NULL THEN
        RAISE EXCEPTION 'compact Duets v3 is not ready';
    END IF;

    SELECT reltuples::bigint
    INTO source_rows
    FROM pg_class
    WHERE oid = 'public.band_team_rank_history_points_v2_duets'::regclass;

    IF source_rows <= 0 THEN
        RAISE EXCEPTION 'Duets v2 source estimate is empty';
    END IF;
END $$;

ALTER TABLE public.band_team_rank_history_points_v2
    DETACH PARTITION public.band_team_rank_history_points_v2_duets;

COMMIT;
