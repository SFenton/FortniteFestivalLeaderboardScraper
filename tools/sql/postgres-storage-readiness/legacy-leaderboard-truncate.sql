\set ON_ERROR_STOP on

BEGIN;
SET LOCAL lock_timeout = '5s';
SET LOCAL statement_timeout = '15min';
SET LOCAL TIME ZONE 'UTC';

DO $$
DECLARE
    row_count bigint;
    checksum_xor bigint;
    identity_checksum_sum numeric;
BEGIN
    IF current_setting('fst.legacy_leaderboard_maintenance', true) IS DISTINCT FROM 'approved' THEN
        RAISE EXCEPTION
            'Set fst.legacy_leaderboard_maintenance=approved only after the full live-scrape parity gate';
    END IF;
    IF EXISTS (
        SELECT 1
        FROM public.scrape_log
        WHERE completed_at IS NULL
          AND COALESCE(to_jsonb(scrape_log)->>'status', 'running') = 'running'
    ) THEN
        RAISE EXCEPTION 'An active scrape exists';
    END IF;
    IF COALESCE((
        SELECT public_reads_frozen
        FROM public.scrape_publication_state
        WHERE id = TRUE
    ), TRUE) THEN
        RAISE EXCEPTION 'Public reads must be unfrozen';
    END IF;

    SELECT COUNT(*),
           BIT_XOR(hashtextextended(concat_ws(E'\x1f',
               song_id, instrument, account_id, score::text,
               COALESCE(accuracy::text, ''), COALESCE(is_full_combo::text, ''),
               COALESCE(stars::text, ''), COALESCE(season::text, ''),
               COALESCE(percentile::text, ''), COALESCE(rank::text, ''), source,
               COALESCE(difficulty::text, ''), COALESCE(api_rank::text, ''),
               COALESCE(end_time, ''), COALESCE(band_members_json::text, ''),
               COALESCE(band_score::text, ''), COALESCE(base_score::text, ''),
               COALESCE(instrument_bonus::text, ''),
               COALESCE(overdrive_bonus::text, ''), COALESCE(instrument_combo, ''),
               first_seen_at::text, last_updated_at::text), 0)),
           SUM(hashtextextended(
               concat_ws(E'\x1f', song_id, instrument, account_id), 1)::numeric)
    INTO row_count, checksum_xor, identity_checksum_sum
    FROM public.leaderboard_entries;

    IF (row_count, checksum_xor, identity_checksum_sum)
       IS DISTINCT FROM
       (36768081::bigint, 7385397320769596144::bigint,
        -8649576607260852230711::numeric) THEN
        RAISE EXCEPTION
            'leaderboard_entries manifest drifted; regenerate the owner package before maintenance';
    END IF;
END
$$;

TRUNCATE TABLE public.leaderboard_entries;
COMMIT;

SELECT COUNT(*) AS legacy_rows
FROM public.leaderboard_entries;
