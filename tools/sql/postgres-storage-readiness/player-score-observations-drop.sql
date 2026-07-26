\set ON_ERROR_STOP on

BEGIN;
SET LOCAL lock_timeout = '5s';
SET LOCAL statement_timeout = '5min';

DO $$
BEGIN
    IF current_setting('fst.player_score_observations_drop', true) IS DISTINCT FROM 'approved' THEN
        RAISE EXCEPTION
            'Set fst.player_score_observations_drop=approved only after code/schema removal and live parity';
    END IF;
END
$$;

DROP VIEW public.player_score_observation_union;
DROP TABLE public.player_score_observations;
COMMIT;
