\set ON_ERROR_STOP on

BEGIN TRANSACTION READ ONLY;
SET LOCAL lock_timeout = '2s';
SET LOCAL statement_timeout = '15s';
SET LOCAL row_security = off;

COPY (
    SELECT expected.family,
           expected.object_order,
           expected.schema_name AS schema,
           expected.object_name AS name,
           attribute.attnum,
           attribute.attname AS column_name,
           attribute.attisdropped AS is_dropped,
           COALESCE(type_schema.nspname, '') AS type_schema,
           COALESCE(type_row.typname, '') AS type_name,
           attribute.atttypid::text AS type_oid,
           attribute.atttypmod::text AS typmod,
           pg_catalog.format_type(
               attribute.atttypid,
               attribute.atttypmod) AS formatted_type,
           attribute.attnotnull AS not_null,
           (default_row.oid IS NOT NULL) AS has_default,
           COALESCE(
               pg_catalog.pg_get_expr(
                   default_row.adbin,
                   default_row.adrelid,
                   true),
               '') AS default_expression,
           attribute.attidentity AS identity_kind,
           attribute.attgenerated AS generated_kind,
           COALESCE(collation_schema.nspname, '') AS collation_schema,
           COALESCE(collation_row.collname, '') AS collation_name,
           attribute.attstorage AS storage,
           attribute.attcompression AS compression,
           attribute.attislocal AS is_local,
           attribute.attinhcount::text AS inheritance_count,
           attribute.atthasmissing AS has_missing,
           COALESCE(attribute.attmissingval::text, '') AS missing_value,
           attribute.attstattarget::text AS statistics_target,
           COALESCE(attribute.attoptions::text, '') AS options,
           COALESCE(attribute.attfdwoptions::text, '') AS fdw_options,
           COALESCE(attribute.attacl::text, '') AS acl
    FROM retired_cleanup_expected expected
    JOIN pg_catalog.pg_namespace relation_schema
      ON relation_schema.nspname = expected.schema_name
    JOIN pg_catalog.pg_class relation
      ON relation.relnamespace = relation_schema.oid
     AND relation.relname = expected.object_name
    JOIN pg_catalog.pg_attribute attribute
      ON attribute.attrelid = relation.oid
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
    ORDER BY expected.object_order, attribute.attnum
) TO STDOUT WITH (FORMAT CSV, HEADER TRUE);

COMMIT;
