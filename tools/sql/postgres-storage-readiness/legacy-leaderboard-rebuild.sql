\set ON_ERROR_STOP on

BEGIN;
SET LOCAL lock_timeout = '5s';
SET LOCAL statement_timeout = '6h';

DO $$
BEGIN
    IF current_setting('fst.legacy_leaderboard_rebuild', true) IS DISTINCT FROM 'approved' THEN
        RAISE EXCEPTION
            'Set fst.legacy_leaderboard_rebuild=approved for the gated rebuild session';
    END IF;
    IF EXISTS (SELECT 1 FROM public.leaderboard_entries LIMIT 1) THEN
        RAISE EXCEPTION 'leaderboard_entries must be empty before rebuild';
    END IF;
    IF COALESCE((
        SELECT public_reads_frozen
        FROM public.scrape_publication_state
        WHERE id = TRUE
    ), TRUE) THEN
        RAISE EXCEPTION 'Public reads must be unfrozen';
    END IF;
END
$$;

WITH publication AS (
    SELECT published_scrape_id
    FROM public.scrape_publication_state
    WHERE id = TRUE
), published_sources AS (
    SELECT source.song_id, source.instrument, source.source_snapshot_id,
           source.row_count
    FROM public.leaderboard_published_scope_source source
    JOIN publication
      ON publication.published_scrape_id = source.published_scrape_id
    WHERE source.source_kind = 'snapshot'
      AND source.is_complete
)
INSERT INTO public.leaderboard_entries (
    song_id, instrument, account_id, score, accuracy, is_full_combo, stars,
    season, percentile, rank, source, difficulty, api_rank, end_time,
    band_members_json, band_score, base_score, instrument_bonus,
    overdrive_bonus, instrument_combo, first_seen_at, last_updated_at)
SELECT snapshot.song_id, snapshot.instrument, snapshot.account_id,
       snapshot.score, snapshot.accuracy, snapshot.is_full_combo, snapshot.stars,
       snapshot.season, snapshot.percentile, snapshot.rank, snapshot.source,
       snapshot.difficulty, snapshot.api_rank, snapshot.end_time,
       snapshot.band_members_json, snapshot.band_score, snapshot.base_score,
       snapshot.instrument_bonus, snapshot.overdrive_bonus,
       snapshot.instrument_combo, snapshot.first_seen_at, snapshot.last_updated_at
FROM published_sources source
JOIN public.leaderboard_entries_snapshot snapshot
  ON snapshot.snapshot_id = source.source_snapshot_id
 AND snapshot.song_id = source.song_id
 AND snapshot.instrument = source.instrument;

DO $$
DECLARE
    expected_rows bigint;
    rebuilt_rows bigint;
BEGIN
    SELECT SUM(source.row_count)
    INTO expected_rows
    FROM public.leaderboard_published_scope_source source
    JOIN public.scrape_publication_state publication
      ON publication.id = TRUE
     AND publication.published_scrape_id = source.published_scrape_id;

    SELECT COUNT(*) INTO rebuilt_rows
    FROM public.leaderboard_entries;

    IF rebuilt_rows IS DISTINCT FROM expected_rows THEN
        RAISE EXCEPTION
            'legacy rebuild row mismatch: expected %, rebuilt %',
            expected_rows, rebuilt_rows;
    END IF;
END
$$;

COMMIT;

SELECT instrument, COUNT(*) AS row_count
FROM public.leaderboard_entries
GROUP BY instrument
ORDER BY instrument;
