\set ON_ERROR_STOP on

BEGIN;
SET LOCAL lock_timeout = '5s';
SET LOCAL statement_timeout = '15min';
SET LOCAL TIME ZONE 'UTC';

DO $$
DECLARE
    row_count bigint;
    min_scrape bigint;
    max_scrape bigint;
    checksum_xor bigint;
    checksum_sum numeric;
BEGIN
    IF current_setting('fst.scrape_dirty_maintenance', true) IS DISTINCT FROM 'approved' THEN
        RAISE EXCEPTION
            'Set fst.scrape_dirty_maintenance=approved for the gated maintenance session';
    END IF;
    IF EXISTS (
        SELECT 1
        FROM public.scrape_log
        WHERE completed_at IS NULL
          AND COALESCE(to_jsonb(scrape_log)->>'status', 'running') = 'running'
    ) THEN
        RAISE EXCEPTION 'An active scrape exists';
    END IF;

    -- ORPHAN-RECLAIM emptied the complete family on 2026-07-27. Keep this
    -- package idempotent, but fail closed on any partial or newly populated
    -- state rather than silently accepting a changed manifest.
    IF NOT EXISTS (SELECT 1 FROM public.scrape_dirty_account)
       AND NOT EXISTS (SELECT 1 FROM public.scrape_dirty_song_instrument)
       AND NOT EXISTS (SELECT 1 FROM public.scrape_dirty_band_scope)
       AND NOT EXISTS (SELECT 1 FROM public.scrape_dirty_band_team) THEN
        RETURN;
    END IF;

    SELECT COUNT(*), MIN(scrape_id), MAX(scrape_id),
           BIT_XOR(hashtextextended(concat_ws(E'\x1f', scrape_id::text, account_id,
               source, dirty_reason, created_at::text), 0)),
           SUM(hashtextextended(concat_ws(E'\x1f', scrape_id::text, account_id,
               source, dirty_reason, created_at::text), 1)::numeric)
    INTO row_count, min_scrape, max_scrape, checksum_xor, checksum_sum
    FROM public.scrape_dirty_account;
    IF (row_count, min_scrape, max_scrape, checksum_xor, checksum_sum)
       IS DISTINCT FROM
       (2728::bigint, 926::bigint, 1146::bigint,
        7259030257282644463::bigint, 2170039668959936499::numeric) THEN
        RAISE EXCEPTION 'scrape_dirty_account manifest drifted';
    END IF;

    SELECT COUNT(*), MIN(scrape_id), MAX(scrape_id),
           BIT_XOR(hashtextextended(concat_ws(E'\x1f', scrape_id::text, song_id,
               instrument, source, dirty_reason, created_at::text), 0)),
           SUM(hashtextextended(concat_ws(E'\x1f', scrape_id::text, song_id,
               instrument, source, dirty_reason, created_at::text), 1)::numeric)
    INTO row_count, min_scrape, max_scrape, checksum_xor, checksum_sum
    FROM public.scrape_dirty_song_instrument;
    IF (row_count, min_scrape, max_scrape, checksum_xor, checksum_sum)
       IS DISTINCT FROM
       (2425194::bigint, 926::bigint, 1146::bigint,
        -7798567217114007197::bigint, -3139813262865378067396::numeric) THEN
        RAISE EXCEPTION 'scrape_dirty_song_instrument manifest drifted';
    END IF;

    SELECT COUNT(*), MIN(scrape_id), MAX(scrape_id),
           BIT_XOR(hashtextextended(concat_ws(E'\x1f', scrape_id::text, song_id,
               band_type, ranking_scope, scope_combo_id, source, dirty_reason,
               created_at::text), 0)),
           SUM(hashtextextended(concat_ws(E'\x1f', scrape_id::text, song_id,
               band_type, ranking_scope, scope_combo_id, source, dirty_reason,
               created_at::text), 1)::numeric)
    INTO row_count, min_scrape, max_scrape, checksum_xor, checksum_sum
    FROM public.scrape_dirty_band_scope;
    IF (row_count, min_scrape, max_scrape, checksum_xor, checksum_sum)
       IS DISTINCT FROM
       (2561011::bigint, 926::bigint, 1146::bigint,
        495685557723333575::bigint, -4058724295767685060880::numeric) THEN
        RAISE EXCEPTION 'scrape_dirty_band_scope manifest drifted';
    END IF;

    SELECT COUNT(*), MIN(scrape_id), MAX(scrape_id),
           BIT_XOR(hashtextextended(concat_ws(E'\x1f', scrape_id::text, band_type,
               team_key, source, dirty_reason, created_at::text), 0)),
           SUM(hashtextextended(concat_ws(E'\x1f', scrape_id::text, band_type,
               team_key, source, dirty_reason, created_at::text), 1)::numeric)
    INTO row_count, min_scrape, max_scrape, checksum_xor, checksum_sum
    FROM public.scrape_dirty_band_team;
    IF (row_count, min_scrape, max_scrape, checksum_xor, checksum_sum)
       IS DISTINCT FROM
       (16847728::bigint, 926::bigint, 1146::bigint,
        714486769169159679::bigint, 9514101806962822550796::numeric) THEN
        RAISE EXCEPTION 'scrape_dirty_band_team manifest drifted';
    END IF;
END
$$;

TRUNCATE TABLE
    public.scrape_dirty_account,
    public.scrape_dirty_song_instrument,
    public.scrape_dirty_band_scope,
    public.scrape_dirty_band_team;
COMMIT;

SELECT 'scrape_dirty_account' AS table_name, COUNT(*) AS row_count
FROM public.scrape_dirty_account
UNION ALL
SELECT 'scrape_dirty_song_instrument', COUNT(*)
FROM public.scrape_dirty_song_instrument
UNION ALL
SELECT 'scrape_dirty_band_scope', COUNT(*)
FROM public.scrape_dirty_band_scope
UNION ALL
SELECT 'scrape_dirty_band_team', COUNT(*)
FROM public.scrape_dirty_band_team;
