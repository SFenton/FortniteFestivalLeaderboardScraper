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
    IF NOT EXISTS (
        SELECT 1
        FROM public.leaderboard_band_context_state
        WHERE id = TRUE
          AND seeded_at IS NOT NULL
    ) THEN
        RAISE EXCEPTION 'leaderboard_band_context must be durably seeded before rebuild';
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
), base_candidates AS (
    SELECT snapshot.song_id, snapshot.instrument, snapshot.account_id,
           snapshot.score, snapshot.accuracy, snapshot.is_full_combo,
           snapshot.stars, snapshot.season, snapshot.percentile, snapshot.rank,
           snapshot.source, snapshot.difficulty, snapshot.api_rank,
           snapshot.end_time, snapshot.band_members_json, snapshot.band_score,
           snapshot.base_score, snapshot.instrument_bonus,
           snapshot.overdrive_bonus, snapshot.instrument_combo,
           snapshot.first_seen_at, snapshot.last_updated_at,
           1 AS origin_precedence,
           0 AS source_priority
    FROM published_sources source
    JOIN public.leaderboard_entries_snapshot snapshot
      ON snapshot.snapshot_id = source.source_snapshot_id
     AND snapshot.song_id = source.song_id
     AND snapshot.instrument = source.instrument
    UNION ALL
    SELECT overlay.song_id, overlay.instrument, overlay.account_id,
           overlay.score, overlay.accuracy, overlay.is_full_combo,
           overlay.stars, overlay.season, overlay.percentile, overlay.rank,
           overlay.source, overlay.difficulty, overlay.api_rank,
           overlay.end_time, overlay.band_members_json, overlay.band_score,
           overlay.base_score, overlay.instrument_bonus,
           overlay.overdrive_bonus, overlay.instrument_combo,
           overlay.first_seen_at, overlay.last_updated_at,
           0 AS origin_precedence,
           overlay.source_priority
    FROM public.leaderboard_entries_overlay overlay
), resolved_base AS (
    SELECT DISTINCT ON (song_id, instrument, account_id)
           song_id, instrument, account_id, score, accuracy, is_full_combo,
           stars, season, percentile, rank, source, difficulty, api_rank,
           end_time, band_members_json, band_score, base_score,
           instrument_bonus, overdrive_bonus, instrument_combo,
           first_seen_at, last_updated_at
    FROM base_candidates
    ORDER BY song_id, instrument, account_id,
             origin_precedence ASC, source_priority DESC
), rebuild_rows AS (
    SELECT COALESCE(context.song_id, base.song_id) AS song_id,
           COALESCE(context.instrument, base.instrument) AS instrument,
           COALESCE(context.account_id, base.account_id) AS account_id,
           CASE WHEN context.account_id IS NOT NULL THEN context.score ELSE base.score END AS score,
           CASE WHEN context.account_id IS NOT NULL THEN context.accuracy ELSE base.accuracy END AS accuracy,
           CASE WHEN context.account_id IS NOT NULL THEN context.is_full_combo ELSE base.is_full_combo END AS is_full_combo,
           CASE WHEN context.account_id IS NOT NULL THEN context.stars ELSE base.stars END AS stars,
           CASE WHEN context.account_id IS NOT NULL THEN context.season ELSE base.season END AS season,
           CASE WHEN context.account_id IS NOT NULL THEN context.percentile ELSE base.percentile END AS percentile,
           COALESCE(base.rank, 0) AS rank,
           CASE WHEN context.account_id IS NOT NULL THEN context.source ELSE base.source END AS source,
           CASE WHEN context.account_id IS NOT NULL THEN context.difficulty ELSE base.difficulty END AS difficulty,
           base.api_rank,
           CASE WHEN context.account_id IS NOT NULL THEN context.end_time ELSE base.end_time END AS end_time,
           CASE WHEN context.account_id IS NOT NULL THEN context.band_members_json ELSE base.band_members_json END AS band_members_json,
           CASE WHEN context.account_id IS NOT NULL THEN context.band_score ELSE base.band_score END AS band_score,
           CASE WHEN context.account_id IS NOT NULL THEN context.base_score ELSE base.base_score END AS base_score,
           CASE WHEN context.account_id IS NOT NULL THEN context.instrument_bonus ELSE base.instrument_bonus END AS instrument_bonus,
           CASE WHEN context.account_id IS NOT NULL THEN context.overdrive_bonus ELSE base.overdrive_bonus END AS overdrive_bonus,
           CASE WHEN context.account_id IS NOT NULL THEN context.instrument_combo ELSE base.instrument_combo END AS instrument_combo,
           CASE WHEN context.account_id IS NOT NULL THEN context.first_seen_at ELSE base.first_seen_at END AS first_seen_at,
           CASE WHEN context.account_id IS NOT NULL THEN context.last_updated_at ELSE base.last_updated_at END AS last_updated_at
    FROM resolved_base base
    FULL JOIN public.leaderboard_band_context context
      ON context.song_id = base.song_id
     AND context.instrument = base.instrument
     AND context.account_id = base.account_id
)
INSERT INTO public.leaderboard_entries (
    song_id, instrument, account_id, score, accuracy, is_full_combo, stars,
    season, percentile, rank, source, difficulty, api_rank, end_time,
    band_members_json, band_score, base_score, instrument_bonus,
    overdrive_bonus, instrument_combo, first_seen_at, last_updated_at)
SELECT song_id, instrument, account_id, score, accuracy, is_full_combo, stars,
       season, percentile, rank, source, difficulty, api_rank, end_time,
       band_members_json, band_score, base_score, instrument_bonus,
       overdrive_bonus, instrument_combo, first_seen_at, last_updated_at
FROM rebuild_rows;

DO $$
DECLARE
    expected_rows bigint;
    rebuilt_rows bigint;
BEGIN
    WITH publication AS (
        SELECT published_scrape_id
        FROM public.scrape_publication_state
        WHERE id = TRUE
    ), expected_keys AS (
        SELECT snapshot.song_id, snapshot.instrument, snapshot.account_id
        FROM public.leaderboard_published_scope_source source
        JOIN publication
          ON publication.published_scrape_id = source.published_scrape_id
        JOIN public.leaderboard_entries_snapshot snapshot
          ON snapshot.snapshot_id = source.source_snapshot_id
         AND snapshot.song_id = source.song_id
         AND snapshot.instrument = source.instrument
        WHERE source.source_kind = 'snapshot'
          AND source.is_complete
        UNION
        SELECT overlay.song_id, overlay.instrument, overlay.account_id
        FROM public.leaderboard_entries_overlay overlay
        UNION
        SELECT context.song_id, context.instrument, context.account_id
        FROM public.leaderboard_band_context context
    )
    SELECT COUNT(*)
    INTO expected_rows
    FROM expected_keys;

    SELECT COUNT(*) INTO rebuilt_rows
    FROM public.leaderboard_entries;

    IF rebuilt_rows IS DISTINCT FROM expected_rows THEN
        RAISE EXCEPTION
            'legacy rebuild row mismatch: expected %, rebuilt %',
            expected_rows, rebuilt_rows;
    END IF;

    IF EXISTS (
        SELECT 1
        FROM public.leaderboard_band_context context
        LEFT JOIN public.leaderboard_entries rebuilt
          ON rebuilt.song_id = context.song_id
         AND rebuilt.instrument = context.instrument
         AND rebuilt.account_id = context.account_id
        WHERE rebuilt.account_id IS NULL
           OR ROW(
                  rebuilt.score, rebuilt.accuracy, rebuilt.is_full_combo,
                  rebuilt.stars, rebuilt.season, rebuilt.percentile,
                  rebuilt.source, rebuilt.difficulty, rebuilt.end_time,
                  rebuilt.band_members_json, rebuilt.band_score,
                  rebuilt.base_score, rebuilt.instrument_bonus,
                  rebuilt.overdrive_bonus, rebuilt.instrument_combo,
                  rebuilt.first_seen_at, rebuilt.last_updated_at
              ) IS DISTINCT FROM ROW(
                  context.score, context.accuracy, context.is_full_combo,
                  context.stars, context.season, context.percentile,
                  context.source, context.difficulty, context.end_time,
                  context.band_members_json, context.band_score,
                  context.base_score, context.instrument_bonus,
                  context.overdrive_bonus, context.instrument_combo,
                  context.first_seen_at, context.last_updated_at
              )
    ) THEN
        RAISE EXCEPTION 'legacy rebuild does not preserve accumulated band context';
    END IF;
END
$$;

COMMIT;

SELECT instrument, COUNT(*) AS row_count
FROM public.leaderboard_entries
GROUP BY instrument
ORDER BY instrument;
