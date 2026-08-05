\set ON_ERROR_STOP on

BEGIN TRANSACTION READ ONLY;
SET LOCAL lock_timeout = '2s';
SET LOCAL statement_timeout = '15s';
SET LOCAL row_security = off;

COPY (
    SELECT expected.family,
           expected.object_order,
           expected.schema_name AS target_schema,
           expected.object_name AS target_name,
           parent_schema.nspname AS parent_schema,
           parent.relname AS parent_name,
           parent.relkind::text AS parent_relkind,
           pg_catalog.pg_get_userbyid(parent.relowner) AS parent_owner,
           inheritance.inhseqno::text AS inheritance_sequence,
           inheritance.inhdetachpending::text AS detach_pending
    FROM retired_cleanup_expected expected
    JOIN pg_catalog.pg_namespace target_schema
      ON target_schema.nspname = expected.schema_name
    JOIN pg_catalog.pg_class target
      ON target.relnamespace = target_schema.oid
     AND target.relname = expected.object_name
    JOIN pg_catalog.pg_inherits inheritance
      ON inheritance.inhrelid = target.oid
    JOIN pg_catalog.pg_class parent
      ON parent.oid = inheritance.inhparent
    JOIN pg_catalog.pg_namespace parent_schema
      ON parent_schema.oid = parent.relnamespace
    ORDER BY expected.object_order,
             inheritance.inhseqno,
             parent_schema.nspname,
             parent.relname
) TO STDOUT WITH (FORMAT CSV, HEADER TRUE);

COMMIT;
