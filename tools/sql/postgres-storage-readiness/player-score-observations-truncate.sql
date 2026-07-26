\set ON_ERROR_STOP on

BEGIN;
SET LOCAL lock_timeout = '5s';
SET LOCAL statement_timeout = '5min';

DO $$
BEGIN
    IF current_setting('fst.player_score_observations_maintenance', true) IS DISTINCT FROM 'approved' THEN
        RAISE EXCEPTION
            'Set fst.player_score_observations_maintenance=approved for the gated maintenance session';
    END IF;
END
$$;

TRUNCATE TABLE public.player_score_observations;
COMMIT;

SELECT COUNT(*) AS observation_rows
FROM public.player_score_observations;

SELECT COUNT(*) AS union_rows
FROM public.player_score_observation_union;
