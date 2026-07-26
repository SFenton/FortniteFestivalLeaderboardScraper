\set ON_ERROR_STOP on

BEGIN;
SET LOCAL lock_timeout = '5s';
SET LOCAL statement_timeout = '5min';

DO $$
BEGIN
    IF current_setting('fst.legacy_leaderboard_drop', true) IS DISTINCT FROM 'approved' THEN
        RAISE EXCEPTION
            'Set fst.legacy_leaderboard_drop=approved only after all writers/readers/schema creation are removed';
    END IF;
END
$$;

DROP TABLE public.leaderboard_entries;
COMMIT;
