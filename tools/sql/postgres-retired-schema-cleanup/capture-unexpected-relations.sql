\set ON_ERROR_STOP on

BEGIN TRANSACTION READ ONLY;
SET LOCAL lock_timeout = '2s';
SET LOCAL statement_timeout = '15s';
SET LOCAL row_security = off;

COPY (
    WITH roots(root_name) AS (
        VALUES
            ('leaderboard_current_entries'),
            ('leaderboard_entry_versions'),
            ('leaderboard_logical_write_metrics'),
            ('player_score_observations'),
            ('player_score_observation_union'),
            ('band_song_team_rankings'),
            ('band_song_team_ranking_state'),
            ('ranking_deltas'),
            ('ranking_delta_tiers'),
            ('rank_history_deltas'),
            ('composite_ranking_deltas'),
            ('combo_ranking_deltas')
    )
    SELECT schema_row.nspname AS schema,
           relation.relname AS name,
           relation.relkind::text AS relkind,
           pg_catalog.pg_get_userbyid(relation.relowner) AS owner
    FROM pg_catalog.pg_class relation
    JOIN pg_catalog.pg_namespace schema_row
      ON schema_row.oid = relation.relnamespace
    WHERE schema_row.nspname = 'public'
      AND relation.relkind IN ('r', 'p', 'v', 'm', 'S', 'f')
      AND EXISTS (
          SELECT 1
          FROM roots
          WHERE relation.relname = roots.root_name
             OR relation.relname LIKE
                replace(roots.root_name, '_', '\_') || '\_%' ESCAPE '\'
      )
      AND NOT EXISTS (
          SELECT 1
          FROM retired_cleanup_expected expected
          WHERE expected.schema_name = schema_row.nspname
            AND expected.object_name = relation.relname
      )
    ORDER BY schema, name
) TO STDOUT WITH (FORMAT CSV, HEADER TRUE);

COMMIT;
