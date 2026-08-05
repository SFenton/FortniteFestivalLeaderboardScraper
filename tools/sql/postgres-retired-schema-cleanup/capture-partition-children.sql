\set ON_ERROR_STOP on

BEGIN TRANSACTION READ ONLY;
SET LOCAL lock_timeout = '2s';
SET LOCAL statement_timeout = '15s';
SET LOCAL row_security = off;

COPY (
    SELECT expected.family,
           expected.object_order AS parent_order,
           expected.schema_name AS parent_schema,
           expected.object_name AS parent_name,
           child_schema.nspname AS child_schema,
           child.relname AS child_name,
           child.relkind::text AS child_relkind,
           pg_catalog.pg_get_userbyid(child.relowner) AS child_owner,
           pg_catalog.pg_get_expr(
               child.relpartbound,
               child.oid,
               true) AS partition_bound
    FROM retired_cleanup_expected expected
    JOIN pg_catalog.pg_namespace parent_schema
      ON parent_schema.nspname = expected.schema_name
    JOIN pg_catalog.pg_class parent
      ON parent.relnamespace = parent_schema.oid
     AND parent.relname = expected.object_name
    JOIN pg_catalog.pg_inherits inheritance
      ON inheritance.inhparent = parent.oid
    JOIN pg_catalog.pg_class child
      ON child.oid = inheritance.inhrelid
    JOIN pg_catalog.pg_namespace child_schema
      ON child_schema.oid = child.relnamespace
    WHERE expected.object_type = 'partitioned_table'
    ORDER BY expected.object_order, child_schema.nspname, child.relname
) TO STDOUT WITH (FORMAT CSV, HEADER TRUE);

COMMIT;
