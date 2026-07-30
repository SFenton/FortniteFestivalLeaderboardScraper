\set ON_ERROR_STOP on

BEGIN;
SET LOCAL lock_timeout = '5s';
SET LOCAL statement_timeout = '5min';

DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1
        FROM pg_constraint
        WHERE conrelid = 'public.band_team_rank_history_points_v2_quad'::regclass
          AND conname = 'band_team_rank_history_points_v2_quad_band_type_check'
    ) THEN
        ALTER TABLE public.band_team_rank_history_points_v2_quad
            ADD CONSTRAINT band_team_rank_history_points_v2_quad_band_type_check
            CHECK (band_type = 'Band_Quad') NOT VALID;
    END IF;
END $$;

COMMIT;

BEGIN;
SET LOCAL lock_timeout = '5s';
SET LOCAL statement_timeout = '30min';

ALTER TABLE public.band_team_rank_history_points_v2_quad
    VALIDATE CONSTRAINT band_team_rank_history_points_v2_quad_band_type_check;

COMMIT;
