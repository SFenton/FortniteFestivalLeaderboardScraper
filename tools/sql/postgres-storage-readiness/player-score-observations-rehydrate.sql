\set ON_ERROR_STOP on

BEGIN;
SET LOCAL lock_timeout = '5s';
SET LOCAL statement_timeout = '6h';

DO $$
BEGIN
    IF current_setting('fst.player_score_observations_rebuild', true) IS DISTINCT FROM 'approved' THEN
        RAISE EXCEPTION
            'Set fst.player_score_observations_rebuild=approved for the gated rebuild session';
    END IF;
    IF EXISTS (SELECT 1 FROM public.player_score_observations LIMIT 1) THEN
        RAISE EXCEPTION 'player_score_observations must be empty before rebuild';
    END IF;
END
$$;

WITH source_rows AS (
    SELECT DISTINCT ON (account_id, song_id, instrument, source_id)
           account_id,
           song_id,
           instrument,
           new_score AS score,
           accuracy,
           is_full_combo,
           stars,
           difficulty,
           season,
           score_achieved_at,
           source_id,
           CASE WHEN season IS NOT NULL
                THEN 'season:' || season::text
                ELSE 'alltime'
           END AS source_scope,
           NULLIF(new_rank, 0) AS solo_rank,
           season_rank,
           all_time_rank,
           percentile::double precision AS solo_percentile,
           changed_at AS observed_at
    FROM (
        SELECT history.*,
               CONCAT_WS(':',
                   'solo-history',
                   account_id,
                   song_id,
                   instrument,
                   new_score::text,
                   CASE WHEN score_achieved_at IS NULL
                        THEN 'no-time'
                        ELSE TO_CHAR(
                            score_achieved_at AT TIME ZONE 'UTC',
                            'YYYY-MM-DD"T"HH24:MI:SS.US"Z"')
                   END,
                   COALESCE(difficulty::text, 'no-difficulty'),
                   COALESCE(season::text, 'no-season')) AS source_id
        FROM public.score_history history
    ) history_with_source
    ORDER BY account_id, song_id, instrument, source_id, changed_at DESC, id DESC
)
INSERT INTO public.player_score_observations (
    account_id, song_id, instrument, score, accuracy, is_full_combo, stars,
    difficulty, season, score_achieved_at, source_kind, source_id, source_scope,
    solo_rank, season_rank, all_time_rank, solo_percentile, observed_at)
SELECT account_id, song_id, instrument, score, accuracy, is_full_combo, stars,
       difficulty, season, score_achieved_at, 'solo-history', source_id, source_scope,
       solo_rank, season_rank, all_time_rank, solo_percentile, observed_at
FROM source_rows;

INSERT INTO public.player_score_observations (
    account_id, song_id, instrument, score, accuracy, is_full_combo, stars,
    difficulty, season, score_achieved_at, source_kind, source_id, source_scope,
    band_type, team_key, instrument_combo, band_score, band_rank, band_percentile,
    band_source, member_index, observed_at)
SELECT member.account_id,
       entry.song_id,
       mapped.instrument,
       member.score,
       member.accuracy,
       member.is_full_combo,
       member.stars,
       member.difficulty,
       NULLIF(entry.season, 0),
       CASE WHEN NULLIF(entry.end_time, '') IS NULL
            THEN NULL
            ELSE entry.end_time::timestamptz
       END,
       'band-member',
       CONCAT_WS(':',
           'band-member', member.account_id, entry.song_id, entry.band_type,
           entry.team_key, entry.instrument_combo, member.member_index::text,
           member.score::text, COALESCE(NULLIF(entry.end_time, ''), 'no-time'),
           COALESCE(member.difficulty::text, 'no-difficulty')),
       CASE WHEN entry.season > 0
            THEN 'season:' || entry.season::text
            ELSE COALESCE(NULLIF(entry.source, ''), 'band')
       END,
       entry.band_type,
       entry.team_key,
       entry.instrument_combo,
       entry.score,
       NULLIF(entry.rank, 0),
       entry.percentile,
       entry.source,
       member.member_index,
       entry.last_updated_at
FROM public.band_member_stats member
JOIN public.band_entries entry
  ON entry.song_id = member.song_id
 AND entry.band_type = member.band_type
 AND entry.team_key = member.team_key
 AND entry.instrument_combo = member.instrument_combo
CROSS JOIN LATERAL (
    VALUES (CASE member.instrument_id
        WHEN 0 THEN 'Solo_Guitar'
        WHEN 1 THEN 'Solo_Bass'
        WHEN 2 THEN 'Solo_Vocals'
        WHEN 3 THEN 'Solo_Drums'
        WHEN 4 THEN 'Solo_PeripheralGuitar'
        WHEN 5 THEN 'Solo_PeripheralBass'
        WHEN 6 THEN 'Solo_PeripheralDrums'
        WHEN 7 THEN 'Solo_PeripheralVocals'
        WHEN 8 THEN 'Solo_PeripheralCymbals'
        ELSE NULL
    END)
) mapped(instrument)
WHERE member.account_id <> ''
  AND member.score IS NOT NULL
  AND mapped.instrument IS NOT NULL
ON CONFLICT (account_id, song_id, instrument, source_kind, source_id) DO NOTHING;

COMMIT;

SELECT source_kind, COUNT(*) AS row_count
FROM public.player_score_observations
GROUP BY source_kind
ORDER BY source_kind;
