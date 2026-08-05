WITH target AS (
    SELECT relation.oid,
           expected.family,
           expected.object_order,
           schema_row.oid AS namespace_oid,
           schema_row.nspname AS schema_name,
           relation.relname AS object_name,
           relation.relkind
    FROM retired_cleanup_expected expected
    JOIN pg_catalog.pg_namespace schema_row
      ON schema_row.nspname = expected.schema_name
    JOIN pg_catalog.pg_class relation
      ON relation.relnamespace = schema_row.oid
     AND relation.relname = expected.object_name
),
effective_publication AS (
    SELECT target.oid AS target_oid,
            target.schema_name,
            target.object_name,
            publication.oid AS publication_oid,
            publication.pubname,
            publication.puballtables,
            publication.pubinsert,
            publication.pubupdate,
            publication.pubdelete,
            publication.pubtruncate,
            publication.pubviaroot,
            publication_table.attnames,
            publication_table.rowfilter,
            EXISTS (
                SELECT 1
                FROM pg_catalog.pg_publication_namespace
                     publication_namespace
                WHERE publication_namespace.pnpubid = publication.oid
                  AND publication_namespace.pnnspid =
                      target.namespace_oid
            ) AS schema_member,
            EXISTS (
                SELECT 1
                FROM pg_catalog.pg_publication_rel direct_relation
                WHERE direct_relation.prpubid = publication.oid
                  AND direct_relation.prrelid = target.oid
            ) AS explicit_member
    FROM target
    JOIN pg_catalog.pg_publication_tables publication_table
       ON publication_table.schemaname = target.schema_name
      AND publication_table.tablename = target.object_name
    JOIN pg_catalog.pg_publication publication
       ON publication.pubname = publication_table.pubname
),
signature AS (
    SELECT 'relation'::text AS category,
           target.schema_name || '.' || target.object_name AS object_identity,
           jsonb_build_object(
               'family', target.family,
               'order', target.object_order,
               'relkind', relation.relkind,
               'owner', pg_catalog.pg_get_userbyid(relation.relowner),
               'persistence', relation.relpersistence,
               'replicaIdentity', relation.relreplident,
               'isPartition', relation.relispartition,
               'isPopulated', relation.relispopulated,
               'hasToast', relation.reltoastrelid <> 0,
               'rowSecurity', relation.relrowsecurity,
               'forceRowSecurity', relation.relforcerowsecurity,
               'options', COALESCE(to_jsonb(relation.reloptions), '[]'::jsonb),
               'acl', COALESCE(to_jsonb(relation.relacl), '[]'::jsonb),
               'tablespace', COALESCE(tablespace.spcname, ''),
               'partitionKey', COALESCE(
                   pg_catalog.pg_get_partkeydef(relation.oid),
                   '')
           ) AS detail
    FROM target
    JOIN pg_catalog.pg_class relation
      ON relation.oid = target.oid
    LEFT JOIN pg_catalog.pg_tablespace tablespace
      ON tablespace.oid = relation.reltablespace

    UNION ALL

    SELECT 'column',
           target.schema_name || '.' || target.object_name || '#' ||
               attribute.attnum::text,
           jsonb_build_object(
               'name', attribute.attname,
               'dropped', attribute.attisdropped,
               'typeSchema', COALESCE(type_schema.nspname, ''),
               'typeName', COALESCE(type_row.typname, ''),
               'typeOid', attribute.atttypid::bigint,
               'typmod', attribute.atttypmod,
               'formattedType', pg_catalog.format_type(
                   attribute.atttypid,
                   attribute.atttypmod),
               'notNull', attribute.attnotnull,
               'hasDefault', default_row.oid IS NOT NULL,
               'default', COALESCE(
                   pg_catalog.pg_get_expr(
                       default_row.adbin,
                       default_row.adrelid,
                       true),
                   ''),
               'identity', attribute.attidentity,
               'generated', attribute.attgenerated,
               'collationSchema', COALESCE(collation_schema.nspname, ''),
               'collation', COALESCE(collation_row.collname, ''),
               'storage', attribute.attstorage,
               'compression', attribute.attcompression,
               'isLocal', attribute.attislocal,
               'inheritanceCount', attribute.attinhcount,
               'hasMissing', attribute.atthasmissing,
               'missingValue', COALESCE(
                   attribute.attmissingval::text,
                   ''),
               'statisticsTarget', attribute.attstattarget,
               'options', COALESCE(
                   to_jsonb(attribute.attoptions),
                   '[]'::jsonb),
               'fdwOptions', COALESCE(
                   to_jsonb(attribute.attfdwoptions),
                   '[]'::jsonb),
               'acl', COALESCE(to_jsonb(attribute.attacl), '[]'::jsonb)
           )
    FROM target
    JOIN pg_catalog.pg_attribute attribute
      ON attribute.attrelid = target.oid
     AND attribute.attnum > 0
    LEFT JOIN pg_catalog.pg_type type_row
      ON type_row.oid = attribute.atttypid
    LEFT JOIN pg_catalog.pg_namespace type_schema
      ON type_schema.oid = type_row.typnamespace
    LEFT JOIN pg_catalog.pg_attrdef default_row
      ON default_row.adrelid = attribute.attrelid
     AND default_row.adnum = attribute.attnum
    LEFT JOIN pg_catalog.pg_collation collation_row
      ON collation_row.oid = attribute.attcollation
     AND attribute.attcollation <> 0
    LEFT JOIN pg_catalog.pg_namespace collation_schema
      ON collation_schema.oid = collation_row.collnamespace

    UNION ALL

    SELECT 'constraint',
           constraint_schema.nspname || '.' ||
               constraint_relation.relname || '.' ||
               constraint_row.conname,
           jsonb_build_object(
               'type', constraint_row.contype,
               'deferrable', constraint_row.condeferrable,
               'deferred', constraint_row.condeferred,
               'validated', constraint_row.convalidated,
               'noInherit', constraint_row.connoinherit,
               'definition', pg_catalog.pg_get_constraintdef(
                   constraint_row.oid,
                   true),
               'key', COALESCE(to_jsonb(constraint_row.conkey), '[]'::jsonb),
               'foreignKey', COALESCE(
                   to_jsonb(constraint_row.confkey),
                   '[]'::jsonb),
               'referencedRelation', CASE
                   WHEN constraint_row.confrelid = 0 THEN ''
                   ELSE pg_catalog.pg_describe_object(
                       'pg_class'::regclass,
                       constraint_row.confrelid,
                       0)
               END,
               'parentConstraint', CASE
                   WHEN constraint_row.conparentid = 0 THEN ''
                   ELSE pg_catalog.pg_describe_object(
                       'pg_constraint'::regclass,
                       constraint_row.conparentid,
                       0)
               END
           )
    FROM pg_catalog.pg_constraint constraint_row
    JOIN pg_catalog.pg_class constraint_relation
      ON constraint_relation.oid = constraint_row.conrelid
    JOIN pg_catalog.pg_namespace constraint_schema
      ON constraint_schema.oid = constraint_relation.relnamespace
    WHERE constraint_row.conrelid IN (SELECT oid FROM target)
       OR constraint_row.confrelid IN (SELECT oid FROM target)

    UNION ALL

    SELECT 'index',
           index_schema.nspname || '.' || index_row.relname,
           jsonb_build_object(
               'target', target.schema_name || '.' || target.object_name,
               'owner', pg_catalog.pg_get_userbyid(index_row.relowner),
               'relkind', index_row.relkind,
               'valid', index_meta.indisvalid,
               'ready', index_meta.indisready,
               'live', index_meta.indislive,
               'checkXmin', index_meta.indcheckxmin,
               'primary', index_meta.indisprimary,
               'unique', index_meta.indisunique,
               'nullsNotDistinct', index_meta.indnullsnotdistinct,
               'exclusion', index_meta.indisexclusion,
               'immediate', index_meta.indimmediate,
               'clustered', index_meta.indisclustered,
               'replIdent', index_meta.indisreplident,
               'definition', pg_catalog.pg_get_indexdef(index_row.oid),
               'predicate', COALESCE(
                   pg_catalog.pg_get_expr(
                       index_meta.indpred,
                       index_meta.indrelid,
                       true),
                   ''),
               'expressions', COALESCE(
                   pg_catalog.pg_get_expr(
                       index_meta.indexprs,
                       index_meta.indrelid,
                       true),
                   ''),
               'options', COALESCE(to_jsonb(index_row.reloptions), '[]'::jsonb),
               'tablespace', COALESCE(tablespace.spcname, ''),
               'parentIndex', COALESCE(
                   parent_schema.nspname || '.' || parent_index.relname,
                   '')
           )
    FROM target
    JOIN pg_catalog.pg_index index_meta
      ON index_meta.indrelid = target.oid
    JOIN pg_catalog.pg_class index_row
      ON index_row.oid = index_meta.indexrelid
    JOIN pg_catalog.pg_namespace index_schema
      ON index_schema.oid = index_row.relnamespace
    LEFT JOIN pg_catalog.pg_tablespace tablespace
      ON tablespace.oid = index_row.reltablespace
    LEFT JOIN pg_catalog.pg_inherits index_inheritance
      ON index_inheritance.inhrelid = index_row.oid
    LEFT JOIN pg_catalog.pg_class parent_index
      ON parent_index.oid = index_inheritance.inhparent
    LEFT JOIN pg_catalog.pg_namespace parent_schema
      ON parent_schema.oid = parent_index.relnamespace

    UNION ALL

    SELECT 'trigger',
           target.schema_name || '.' || target.object_name || '.' ||
               trigger_row.tgname,
           jsonb_build_object(
               'internal', trigger_row.tgisinternal,
               'enabled', trigger_row.tgenabled,
               'type', trigger_row.tgtype,
               'function', pg_catalog.pg_describe_object(
                   'pg_proc'::regclass,
                   trigger_row.tgfoid,
                   0),
               'constraint', CASE
                   WHEN trigger_row.tgconstraint = 0 THEN ''
                   ELSE pg_catalog.pg_describe_object(
                       'pg_constraint'::regclass,
                       trigger_row.tgconstraint,
                       0)
               END,
               'definition', pg_catalog.pg_get_triggerdef(
                   trigger_row.oid,
                   true)
           )
    FROM target
    JOIN pg_catalog.pg_trigger trigger_row
      ON trigger_row.tgrelid = target.oid

    UNION ALL

    SELECT 'policy',
           target.schema_name || '.' || target.object_name || '.' ||
               policy.polname,
           jsonb_build_object(
               'permissive', policy.polpermissive,
               'command', policy.polcmd,
               'roles', COALESCE(
                   (
                       SELECT jsonb_agg(
                           COALESCE(role_row.rolname, 'PUBLIC')
                           ORDER BY COALESCE(role_row.rolname, 'PUBLIC'))
                       FROM unnest(policy.polroles) AS role_oid(oid)
                       LEFT JOIN pg_catalog.pg_roles role_row
                         ON role_row.oid = role_oid.oid
                   ),
                   '[]'::jsonb),
               'using', COALESCE(
                   pg_catalog.pg_get_expr(
                       policy.polqual,
                       policy.polrelid,
                       true),
                   ''),
               'check', COALESCE(
                   pg_catalog.pg_get_expr(
                       policy.polwithcheck,
                       policy.polrelid,
                       true),
                   '')
           )
    FROM target
    JOIN pg_catalog.pg_policy policy
      ON policy.polrelid = target.oid

    UNION ALL

    SELECT 'view',
           target.schema_name || '.' || target.object_name,
           jsonb_build_object(
               'definition', pg_catalog.pg_get_viewdef(target.oid, true)
           )
    FROM target
    WHERE target.relkind IN ('v', 'm')

    UNION ALL

    SELECT 'dependent-view',
           dependent_schema.nspname || '.' || dependent.relname,
           jsonb_build_object(
               'relkind', dependent.relkind,
               'definition', pg_catalog.pg_get_viewdef(
                   dependent.oid,
                   true)
           )
    FROM target
    JOIN pg_catalog.pg_depend dependency
      ON dependency.refclassid = 'pg_class'::regclass
     AND dependency.refobjid = target.oid
    JOIN pg_catalog.pg_rewrite rewrite
      ON dependency.classid = 'pg_rewrite'::regclass
     AND rewrite.oid = dependency.objid
    JOIN pg_catalog.pg_class dependent
      ON dependent.oid = rewrite.ev_class
    JOIN pg_catalog.pg_namespace dependent_schema
      ON dependent_schema.oid = dependent.relnamespace
    WHERE dependent.relkind IN ('v', 'm')

    UNION ALL

    SELECT 'rule',
           rule_schema.nspname || '.' || rule_relation.relname || '.' ||
               rewrite.rulename,
           jsonb_build_object(
               'eventType', rewrite.ev_type,
               'enabled', rewrite.ev_enabled,
               'instead', rewrite.is_instead,
               'definition', pg_catalog.pg_get_ruledef(rewrite.oid, true)
           )
    FROM pg_catalog.pg_rewrite rewrite
    JOIN pg_catalog.pg_class rule_relation
      ON rule_relation.oid = rewrite.ev_class
    JOIN pg_catalog.pg_namespace rule_schema
      ON rule_schema.oid = rule_relation.relnamespace
    WHERE rewrite.ev_class IN (SELECT oid FROM target)

    UNION ALL

    SELECT 'publication',
           effective_publication.pubname || ':' ||
               effective_publication.schema_name || '.' ||
               effective_publication.object_name,
           jsonb_build_object(
               'membershipMode', CASE
                   WHEN effective_publication.puballtables
                   THEN 'all-tables'
                   WHEN effective_publication.schema_member
                   THEN 'schema'
                   WHEN effective_publication.explicit_member
                   THEN 'explicit'
                   ELSE 'effective'
               END,
               'allTables', effective_publication.puballtables,
               'schemaMember', effective_publication.schema_member,
               'explicitMember', effective_publication.explicit_member,
               'insert', effective_publication.pubinsert,
               'update', effective_publication.pubupdate,
               'delete', effective_publication.pubdelete,
               'truncate', effective_publication.pubtruncate,
               'viaRoot', effective_publication.pubviaroot,
               'columns', COALESCE(
                   to_jsonb(effective_publication.attnames),
                   '[]'::jsonb),
               'rowFilter', COALESCE(
                   effective_publication.rowfilter,
                   '')
           )
    FROM effective_publication

    UNION ALL

    SELECT 'sequence',
           target.schema_name || '.' || target.object_name,
           jsonb_build_object(
               'type', pg_catalog.format_type(sequence.seqtypid, NULL),
               'start', sequence.seqstart,
               'increment', sequence.seqincrement,
               'max', sequence.seqmax,
               'min', sequence.seqmin,
               'cache', sequence.seqcache,
               'cycle', sequence.seqcycle,
               'ownedBy', COALESCE(
                   owner_schema.nspname || '.' || owner_table.relname ||
                       '.' || owner_attribute.attname,
                   ''),
               'ownershipType', COALESCE(dependency.deptype::text, ''),
               'lastValue', CASE
                   WHEN target.schema_name = 'public'
                    AND target.object_name =
                        'player_score_observations_id_seq'
                   THEN (
                       SELECT last_value::text
                       FROM public.player_score_observations_id_seq
                   )
                   ELSE ''
               END,
               'isCalled', CASE
                   WHEN target.schema_name = 'public'
                    AND target.object_name =
                        'player_score_observations_id_seq'
                   THEN (
                       SELECT is_called::text
                       FROM public.player_score_observations_id_seq
                   )
                   ELSE ''
               END
           )
    FROM target
    JOIN pg_catalog.pg_sequence sequence
      ON sequence.seqrelid = target.oid
    LEFT JOIN pg_catalog.pg_depend dependency
      ON dependency.objid = target.oid
     AND dependency.classid = 'pg_class'::regclass
     AND dependency.deptype IN ('a', 'i')
    LEFT JOIN pg_catalog.pg_class owner_table
      ON owner_table.oid = dependency.refobjid
    LEFT JOIN pg_catalog.pg_namespace owner_schema
      ON owner_schema.oid = owner_table.relnamespace
    LEFT JOIN pg_catalog.pg_attribute owner_attribute
      ON owner_attribute.attrelid = owner_table.oid
     AND owner_attribute.attnum = dependency.refobjsubid
    WHERE target.relkind = 'S'

    UNION ALL

    SELECT 'partition',
           parent_schema.nspname || '.' || parent.relname || '->' ||
               child_schema.nspname || '.' || child.relname,
           jsonb_build_object(
               'bound', pg_catalog.pg_get_expr(
                   child.relpartbound,
                   child.oid,
                   true),
               'childRelkind', child.relkind,
               'childOwner', pg_catalog.pg_get_userbyid(child.relowner),
               'sequence', inheritance.inhseqno,
               'detachPending', inheritance.inhdetachpending
           )
    FROM target parent_target
    JOIN pg_catalog.pg_inherits inheritance
      ON inheritance.inhparent = parent_target.oid
    JOIN pg_catalog.pg_class parent
      ON parent.oid = inheritance.inhparent
    JOIN pg_catalog.pg_namespace parent_schema
      ON parent_schema.oid = parent.relnamespace
    JOIN pg_catalog.pg_class child
      ON child.oid = inheritance.inhrelid
    JOIN pg_catalog.pg_namespace child_schema
      ON child_schema.oid = child.relnamespace
    WHERE parent_target.relkind = 'p'

    UNION ALL

    SELECT 'incoming-inheritance',
           child_target.schema_name || '.' || child_target.object_name ||
               '<-' || parent_schema.nspname || '.' || parent.relname,
           jsonb_build_object(
               'parentRelkind', parent.relkind,
               'parentOwner', pg_catalog.pg_get_userbyid(parent.relowner),
               'sequence', inheritance.inhseqno,
               'detachPending', inheritance.inhdetachpending
           )
    FROM target child_target
    JOIN pg_catalog.pg_inherits inheritance
      ON inheritance.inhrelid = child_target.oid
    JOIN pg_catalog.pg_class parent
      ON parent.oid = inheritance.inhparent
    JOIN pg_catalog.pg_namespace parent_schema
      ON parent_schema.oid = parent.relnamespace

    UNION ALL

    SELECT 'dependency',
           CASE
               WHEN dependent_class.relkind = 't'
                AND referenced_target.oid IS NOT NULL
               THEN 'toast relation for ' ||
                   referenced_target.schema_name || '.' ||
                   referenced_target.object_name
               ELSE COALESCE(
                   pg_catalog.pg_describe_object(
                       dependency.classid,
                       dependency.objid,
                       dependency.objsubid),
                   '')
           END || '->' ||
               COALESCE(
                   pg_catalog.pg_describe_object(
                       dependency.refclassid,
                       dependency.refobjid,
                       dependency.refobjsubid),
                   '') || ':' || dependency.deptype::text,
           jsonb_build_object(
               'dependentClass', dependency.classid::regclass::text,
               'dependentSubId', dependency.objsubid,
               'dependent', CASE
                   WHEN dependent_class.relkind = 't'
                    AND referenced_target.oid IS NOT NULL
                   THEN 'toast relation for ' ||
                       referenced_target.schema_name || '.' ||
                       referenced_target.object_name
                   ELSE pg_catalog.pg_describe_object(
                       dependency.classid,
                       dependency.objid,
                       dependency.objsubid)
               END,
               'referencedClass', dependency.refclassid::regclass::text,
               'referencedSubId', dependency.refobjsubid,
               'referenced', pg_catalog.pg_describe_object(
                   dependency.refclassid,
                   dependency.refobjid,
                   dependency.refobjsubid),
               'type', dependency.deptype
           )
    FROM pg_catalog.pg_depend dependency
    LEFT JOIN pg_catalog.pg_class dependent_class
      ON dependency.classid = 'pg_class'::regclass
     AND dependent_class.oid = dependency.objid
    LEFT JOIN target referenced_target
      ON dependency.refclassid = 'pg_class'::regclass
     AND referenced_target.oid = dependency.refobjid
    WHERE (
              dependency.classid = 'pg_class'::regclass
          AND dependency.objid IN (SELECT oid FROM target)
          )
       OR (
              dependency.refclassid = 'pg_class'::regclass
          AND dependency.refobjid IN (SELECT oid FROM target)
          )

    UNION ALL

    SELECT 'shared-dependency',
           target.schema_name || '.' || target.object_name || '->' ||
               CASE
                   WHEN shared_dependency.refclassid =
                        'pg_authid'::regclass
                   THEN 'role ' || pg_catalog.pg_get_userbyid(
                       shared_dependency.refobjid)
                   ELSE shared_dependency.refclassid::regclass::text ||
                       ':' || shared_dependency.refobjid::text
               END || ':' || shared_dependency.deptype::text,
               jsonb_build_object(
                   'dependentClass',
                   shared_dependency.classid::regclass::text,
               'referencedClass',
                   shared_dependency.refclassid::regclass::text,
               'referenced', CASE
                   WHEN shared_dependency.refclassid =
                        'pg_authid'::regclass
                   THEN 'role ' || pg_catalog.pg_get_userbyid(
                       shared_dependency.refobjid)
                   ELSE shared_dependency.refobjid::text
               END,
               'type', shared_dependency.deptype
           )
    FROM target
    JOIN pg_catalog.pg_shdepend shared_dependency
      ON shared_dependency.classid = 'pg_class'::regclass
     AND shared_dependency.objid = target.oid
     AND shared_dependency.dbid = (
         SELECT oid
         FROM pg_catalog.pg_database
         WHERE datname = current_database()
     )

    UNION ALL

    SELECT 'routine-reference',
           routine_schema.nspname || '.' || routine.proname || '(' ||
               pg_catalog.pg_get_function_identity_arguments(routine.oid) ||
               ')',
           jsonb_build_object(
               'kind', routine.prokind,
               'language', routine_language.lanname,
               'definition', pg_catalog.pg_get_functiondef(routine.oid)
           )
    FROM pg_catalog.pg_proc routine
    JOIN pg_catalog.pg_namespace routine_schema
      ON routine_schema.oid = routine.pronamespace
    JOIN pg_catalog.pg_language routine_language
      ON routine_language.oid = routine.prolang
    WHERE routine_schema.nspname NOT IN ('pg_catalog', 'information_schema')
      AND routine_schema.nspname !~ '^pg_(toast_)?temp_'
      AND routine.prokind IN ('f', 'p')
      AND pg_catalog.pg_get_functiondef(routine.oid) ~*
          '(leaderboard_current_entries|leaderboard_entry_versions|leaderboard_logical_write_metrics|player_score_observations|player_score_observation_union|band_song_team_rankings|band_song_team_ranking_state|ranking_deltas|ranking_delta_tiers|rank_history_deltas|composite_ranking_deltas|combo_ranking_deltas)'
)
SELECT category,
       object_identity,
       detail
FROM signature
ORDER BY category, object_identity, detail
