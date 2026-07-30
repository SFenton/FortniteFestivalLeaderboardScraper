\set ON_ERROR_STOP on

BEGIN;
SET LOCAL lock_timeout = '5s';
SET LOCAL statement_timeout = '10min';

DO $$
DECLARE
    ready_rows BIGINT;
    validated_at_value TIMESTAMPTZ;
BEGIN
    SELECT row_count, validated_at
    INTO ready_rows, validated_at_value
    FROM public.band_rank_history_compact_v3_state
    WHERE band_type = 'Band_Quad'
      AND status = 'ready';

    IF ready_rows IS DISTINCT FROM 359383226 THEN
        RAISE EXCEPTION 'compact Quad v3 is not ready with the expected row count: %', ready_rows;
    END IF;

    IF validated_at_value IS NULL THEN
        RAISE EXCEPTION 'compact Quad v3 readiness has no validation timestamp';
    END IF;

    IF NOT EXISTS (
        SELECT 1
        FROM pg_constraint
        WHERE conrelid = 'public.band_team_rank_history_points_v2_quad'::regclass
          AND conname = 'band_team_rank_history_points_v2_quad_band_type_check'
          AND convalidated
    ) THEN
        RAISE EXCEPTION 'validated Quad source band-type constraint is missing';
    END IF;
END $$;

ALTER TABLE public.band_team_rank_history_points_v2
    DETACH PARTITION public.band_team_rank_history_points_v2_quad;

UPDATE public.band_rank_history_compact_v3_state
SET promoted_at = now(),
    updated_at = now()
WHERE band_type = 'Band_Quad'
  AND status = 'ready';

COMMIT;
