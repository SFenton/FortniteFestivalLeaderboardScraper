\set ON_ERROR_STOP on

BEGIN TRANSACTION READ ONLY;
SET LOCAL lock_timeout = '2s';
SET LOCAL statement_timeout = '15s';
SET LOCAL row_security = off;

COPY (
    WITH target AS (
        SELECT relation.oid,
               schema_row.nspname AS schema_name,
               relation.relname AS object_name
        FROM retired_cleanup_expected expected
        JOIN pg_catalog.pg_namespace schema_row
          ON schema_row.nspname = expected.schema_name
        JOIN pg_catalog.pg_class relation
          ON relation.relnamespace = schema_row.oid
         AND relation.relname = expected.object_name
    ),
    dependencies AS (
        SELECT 'view'::text AS dependency_kind,
               dependent_schema.nspname || '.' || dependent.relname
                   AS dependent_object,
               target.schema_name || '.' || target.object_name
                   AS referenced_object,
               'definition_md5=' ||
                   md5(pg_catalog.pg_get_viewdef(dependent.oid, true))
                   AS detail
        FROM target
        JOIN pg_catalog.pg_depend dependency
          ON dependency.refobjid = target.oid
        JOIN pg_catalog.pg_rewrite rewrite
          ON dependency.classid = 'pg_rewrite'::regclass
         AND rewrite.oid = dependency.objid
        JOIN pg_catalog.pg_class dependent
          ON dependent.oid = rewrite.ev_class
        JOIN pg_catalog.pg_namespace dependent_schema
          ON dependent_schema.oid = dependent.relnamespace
        WHERE dependent.relkind IN ('v', 'm')
          AND NOT EXISTS (
              SELECT 1 FROM target expected_dependent
              WHERE expected_dependent.oid = dependent.oid
          )

        UNION ALL

        SELECT 'foreign-key',
               relation_schema.nspname || '.' || relation.relname ||
                   '.' || constraint_row.conname,
               referenced_schema.nspname || '.' || referenced.relname,
               pg_catalog.pg_get_constraintdef(constraint_row.oid, true)
        FROM pg_catalog.pg_constraint constraint_row
        JOIN pg_catalog.pg_class relation
          ON relation.oid = constraint_row.conrelid
        JOIN pg_catalog.pg_namespace relation_schema
          ON relation_schema.oid = relation.relnamespace
        JOIN pg_catalog.pg_class referenced
          ON referenced.oid = constraint_row.confrelid
        JOIN pg_catalog.pg_namespace referenced_schema
          ON referenced_schema.oid = referenced.relnamespace
        WHERE constraint_row.contype = 'f'
          AND (
              constraint_row.conrelid IN (SELECT oid FROM target)
              OR constraint_row.confrelid IN (SELECT oid FROM target)
          )
          AND NOT (
              constraint_row.conrelid IN (SELECT oid FROM target)
              AND constraint_row.confrelid IN (SELECT oid FROM target)
          )

        UNION ALL

        SELECT 'trigger',
               target.schema_name || '.' || target.object_name ||
                   '.' || trigger_row.tgname,
               target.schema_name || '.' || target.object_name,
               'definition_md5=' ||
                   md5(pg_catalog.pg_get_triggerdef(trigger_row.oid, true))
        FROM target
        JOIN pg_catalog.pg_trigger trigger_row
          ON trigger_row.tgrelid = target.oid
        WHERE NOT trigger_row.tgisinternal

        UNION ALL

        SELECT 'routine',
               routine_schema.nspname || '.' ||
                   routine.proname || '(' ||
                   pg_catalog.pg_get_function_identity_arguments(routine.oid) ||
                   ')',
               'cleanup-name-reference',
               format(
                   'language=%s definition_md5=%s',
                   routine_language.lanname,
                   md5(pg_catalog.pg_get_functiondef(routine.oid)))
        FROM pg_catalog.pg_proc routine
        JOIN pg_catalog.pg_namespace routine_schema
          ON routine_schema.oid = routine.pronamespace
        JOIN pg_catalog.pg_language routine_language
          ON routine_language.oid = routine.prolang
        WHERE routine_schema.nspname NOT IN ('pg_catalog', 'information_schema')
          AND routine.prokind IN ('f', 'p')
          AND pg_catalog.pg_get_functiondef(routine.oid) ~*
              '(leaderboard_current_entries|leaderboard_entry_versions|leaderboard_logical_write_metrics|player_score_observations|player_score_observation_union|band_song_team_rankings|band_song_team_ranking_state|ranking_deltas|ranking_delta_tiers|rank_history_deltas|composite_ranking_deltas|combo_ranking_deltas)'

        UNION ALL

        SELECT 'rule',
               rule_schema.nspname || '.' || rule_table.relname ||
                   '.' || rewrite.rulename,
               target.schema_name || '.' || target.object_name,
               'definition_md5=' ||
                   md5(pg_catalog.pg_get_ruledef(rewrite.oid, true))
        FROM target
        JOIN pg_catalog.pg_depend dependency
          ON dependency.refobjid = target.oid
        JOIN pg_catalog.pg_rewrite rewrite
          ON dependency.classid = 'pg_rewrite'::regclass
         AND rewrite.oid = dependency.objid
        JOIN pg_catalog.pg_class rule_table
          ON rule_table.oid = rewrite.ev_class
        JOIN pg_catalog.pg_namespace rule_schema
          ON rule_schema.oid = rule_table.relnamespace
        WHERE NOT (
            rewrite.rulename = '_RETURN'
            AND rule_table.oid IN (SELECT oid FROM target)
        )

        UNION ALL

        SELECT 'policy',
               target.schema_name || '.' || target.object_name ||
                   '.' || policy.polname,
               target.schema_name || '.' || target.object_name,
               'definition_md5=' ||
                   md5(concat_ws(
                       ' ',
                       pg_catalog.pg_get_expr(
                           policy.polqual,
                           policy.polrelid),
                       pg_catalog.pg_get_expr(
                           policy.polwithcheck,
                           policy.polrelid)))
        FROM target
        JOIN pg_catalog.pg_policy policy
          ON policy.polrelid = target.oid

        UNION ALL

        SELECT 'publication',
               publication.pubname,
               target.schema_name || '.' || target.object_name,
               ''
        FROM target
        JOIN pg_catalog.pg_publication_rel publication_relation
          ON publication_relation.prrelid = target.oid
        JOIN pg_catalog.pg_publication publication
          ON publication.oid = publication_relation.prpubid
    )
    SELECT dependency_kind,
           dependent_object,
           referenced_object,
           detail
    FROM dependencies
    ORDER BY dependency_kind, dependent_object, referenced_object
) TO STDOUT WITH (FORMAT CSV, HEADER TRUE);

COMMIT;
