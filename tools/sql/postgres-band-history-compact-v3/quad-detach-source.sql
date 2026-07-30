\set ON_ERROR_STOP on

BEGIN;
SET LOCAL lock_timeout = '5s';
SET LOCAL statement_timeout = '10min';

DO $$
DECLARE
    ready_rows BIGINT;
    validated_at_value TIMESTAMPTZ;
BEGIN
    IF (SELECT published_scrape_id FROM public.scrape_publication_state WHERE id = TRUE) <> 1271
       OR (SELECT public_reads_frozen FROM public.scrape_publication_state WHERE id = TRUE) THEN
        RAISE EXCEPTION 'publication boundary changed';
    END IF;

    IF (SELECT status FROM public.service_worker_status WHERE worker_key = 'scraper') <> 'offline'
       OR (SELECT current_operation_json FROM public.service_worker_status WHERE worker_key = 'scraper') IS NOT NULL THEN
        RAISE EXCEPTION 'worker boundary changed';
    END IF;

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

    IF pg_total_relation_size('public.band_team_rank_history_points_v2_quad'::regclass) <> 388779032576 THEN
        RAISE EXCEPTION 'Quad v2 source size drift';
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

    IF EXISTS (SELECT 1 FROM pg_stat_progress_vacuum)
       OR EXISTS (SELECT 1 FROM pg_stat_progress_create_index) THEN
        RAISE EXCEPTION 'database maintenance is active';
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
