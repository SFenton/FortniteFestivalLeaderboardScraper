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
    owned AS (
        SELECT 'index'::text AS kind,
               index_schema.nspname AS schema_name,
               index_row.relname AS owned_name,
               target.schema_name AS target_schema,
               target.object_name AS target_name,
               pg_catalog.pg_get_indexdef(index_row.oid) AS definition,
               format(
                   'valid=%s ready=%s live=%s primary=%s unique=%s',
                   index_meta.indisvalid,
                   index_meta.indisready,
                   index_meta.indislive,
                   index_meta.indisprimary,
                   index_meta.indisunique) AS state
        FROM target
        JOIN pg_catalog.pg_index index_meta
          ON index_meta.indrelid = target.oid
        JOIN pg_catalog.pg_class index_row
          ON index_row.oid = index_meta.indexrelid
        JOIN pg_catalog.pg_namespace index_schema
          ON index_schema.oid = index_row.relnamespace

        UNION ALL

        SELECT 'constraint',
               constraint_schema.nspname,
               constraint_row.conname,
               target.schema_name,
               target.object_name,
               pg_catalog.pg_get_constraintdef(
                   constraint_row.oid,
                   true),
               format(
                   'type=%s validated=%s',
                   constraint_row.contype,
                   constraint_row.convalidated)
        FROM target
        JOIN pg_catalog.pg_constraint constraint_row
          ON constraint_row.conrelid = target.oid
        JOIN pg_catalog.pg_namespace constraint_schema
          ON constraint_schema.oid = constraint_row.connamespace

        UNION ALL

        SELECT 'partition-binding',
               child_schema.nspname,
               child.relname,
               parent_schema.nspname,
               parent.relname,
               pg_catalog.pg_get_expr(
                   child.relpartbound,
                   child.oid,
                   true),
               'attached'
        FROM pg_catalog.pg_inherits inheritance
        JOIN pg_catalog.pg_class child
          ON child.oid = inheritance.inhrelid
        JOIN pg_catalog.pg_namespace child_schema
          ON child_schema.oid = child.relnamespace
        JOIN pg_catalog.pg_class parent
          ON parent.oid = inheritance.inhparent
        JOIN pg_catalog.pg_namespace parent_schema
          ON parent_schema.oid = parent.relnamespace
        JOIN target child_target
          ON child_target.oid = child.oid
        JOIN target parent_target
          ON parent_target.oid = parent.oid

        UNION ALL

        SELECT 'toast',
               toast_schema.nspname,
               toast.relname,
               target.schema_name,
               target.object_name,
               '',
               'internal'
        FROM target
        JOIN pg_catalog.pg_class target_row
          ON target_row.oid = target.oid
        JOIN pg_catalog.pg_class toast
          ON toast.oid = target_row.reltoastrelid
        JOIN pg_catalog.pg_namespace toast_schema
          ON toast_schema.oid = toast.relnamespace

        UNION ALL

        SELECT 'sequence-owner',
               sequence_schema.nspname,
               sequence_row.relname,
               owner_schema.nspname,
               owner_table.relname,
               owner_attribute.attname,
               dependency.deptype::text
        FROM target sequence_target
        JOIN pg_catalog.pg_class sequence_row
          ON sequence_row.oid = sequence_target.oid
         AND sequence_row.relkind = 'S'
        JOIN pg_catalog.pg_namespace sequence_schema
          ON sequence_schema.oid = sequence_row.relnamespace
        JOIN pg_catalog.pg_depend dependency
          ON dependency.objid = sequence_row.oid
         AND dependency.deptype = 'a'
        JOIN pg_catalog.pg_class owner_table
          ON owner_table.oid = dependency.refobjid
        JOIN pg_catalog.pg_namespace owner_schema
          ON owner_schema.oid = owner_table.relnamespace
        JOIN pg_catalog.pg_attribute owner_attribute
          ON owner_attribute.attrelid = owner_table.oid
         AND owner_attribute.attnum = dependency.refobjsubid
    )
    SELECT kind,
           schema_name AS schema,
           owned_name AS name,
           target_schema,
           target_name,
           definition,
           state
    FROM owned
    ORDER BY kind, schema, name, target_schema, target_name, definition
) TO STDOUT WITH (FORMAT CSV, HEADER TRUE);

COMMIT;
