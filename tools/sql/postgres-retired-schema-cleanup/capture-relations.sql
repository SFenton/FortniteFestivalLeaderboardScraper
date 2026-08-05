\set ON_ERROR_STOP on

BEGIN TRANSACTION READ ONLY;
SET LOCAL lock_timeout = '2s';
SET LOCAL statement_timeout = '15s';
SET LOCAL row_security = off;

CREATE TEMP TABLE retired_cleanup_relation_capture (
    family text NOT NULL,
    object_order integer NOT NULL,
    object_type text NOT NULL,
    schema_name text NOT NULL,
    object_name text NOT NULL,
    actual_relkind text,
    owner_name text,
    parent_schema text,
    parent_name text,
    row_count text,
    total_bytes bigint,
    sequence_owned_by text,
    sequence_last_value text,
    sequence_is_called text
) ON COMMIT DROP;

DO $capture$
DECLARE
    expected record;
    relation_oid oid;
    relation_kind "char";
    relation_owner text;
    captured_parent_schema text;
    captured_parent_name text;
    has_rows boolean;
    exact_count bigint;
    bytes bigint;
    owned_by text;
    sequence_last text;
    sequence_called text;
BEGIN
    FOR expected IN
        SELECT *
        FROM retired_cleanup_expected
        ORDER BY object_order
    LOOP
        relation_oid := NULL;
        relation_kind := NULL;
        relation_owner := NULL;
        captured_parent_schema := NULL;
        captured_parent_name := NULL;
        has_rows := NULL;
        exact_count := NULL;
        bytes := NULL;
        owned_by := NULL;
        sequence_last := NULL;
        sequence_called := NULL;

        SELECT relation.oid,
               relation.relkind,
               pg_catalog.pg_get_userbyid(relation.relowner)
        INTO relation_oid, relation_kind, relation_owner
        FROM pg_catalog.pg_class relation
        JOIN pg_catalog.pg_namespace schema_row
          ON schema_row.oid = relation.relnamespace
        WHERE schema_row.nspname = expected.schema_name
          AND relation.relname = expected.object_name;

        IF relation_oid IS NOT NULL THEN
            IF relation_kind IN ('r', 'p') THEN
                IF expected.row_policy = 'retained' THEN
                    EXECUTE format(
                        'SELECT count(*) FROM %I.%I',
                        expected.schema_name,
                        expected.object_name)
                    INTO exact_count;
                ELSE
                    EXECUTE format(
                        'SELECT EXISTS (SELECT 1 FROM %I.%I LIMIT 1)',
                        expected.schema_name,
                        expected.object_name)
                    INTO has_rows;
                END IF;
            END IF;

            IF relation_kind IN ('r', 'p', 'S', 'm') THEN
                bytes := pg_catalog.pg_total_relation_size(relation_oid);
            END IF;

            IF expected.object_type = 'table'
               AND expected.parent_name IS NOT NULL
               AND expected.owner_column IS NULL THEN
                SELECT parent_schema_row.nspname,
                       parent.relname
                INTO captured_parent_schema, captured_parent_name
                FROM pg_catalog.pg_inherits inheritance
                JOIN pg_catalog.pg_class parent
                  ON parent.oid = inheritance.inhparent
                JOIN pg_catalog.pg_namespace parent_schema_row
                  ON parent_schema_row.oid = parent.relnamespace
                WHERE inheritance.inhrelid = relation_oid;
            ELSIF expected.object_type = 'sequence' THEN
                SELECT owner_schema.nspname,
                       owner_table.relname,
                       format(
                           '%s.%s.%s',
                           owner_schema.nspname,
                           owner_table.relname,
                           owner_attribute.attname)
                INTO captured_parent_schema, captured_parent_name, owned_by
                FROM pg_catalog.pg_depend dependency
                JOIN pg_catalog.pg_class owner_table
                  ON owner_table.oid = dependency.refobjid
                JOIN pg_catalog.pg_namespace owner_schema
                  ON owner_schema.oid = owner_table.relnamespace
                JOIN pg_catalog.pg_attribute owner_attribute
                  ON owner_attribute.attrelid = owner_table.oid
                 AND owner_attribute.attnum = dependency.refobjsubid
                WHERE dependency.objid = relation_oid
                  AND dependency.deptype = 'a';

                EXECUTE format(
                    'SELECT last_value::text, is_called::text FROM %I.%I',
                    expected.schema_name,
                    expected.object_name)
                INTO sequence_last, sequence_called;
            END IF;
        END IF;

        INSERT INTO retired_cleanup_relation_capture (
            family,
            object_order,
            object_type,
            schema_name,
            object_name,
            actual_relkind,
            owner_name,
            parent_schema,
            parent_name,
            row_count,
            total_bytes,
            sequence_owned_by,
            sequence_last_value,
            sequence_is_called)
        VALUES (
            expected.family,
            expected.object_order,
            expected.object_type,
            expected.schema_name,
            expected.object_name,
            relation_kind::text,
            relation_owner,
            captured_parent_schema,
            captured_parent_name,
            CASE
                WHEN exact_count IS NOT NULL THEN exact_count::text
                WHEN has_rows IS NULL THEN NULL
                WHEN has_rows THEN '>=1'
                ELSE '0'
            END,
            bytes,
            owned_by,
            sequence_last,
            sequence_called);
    END LOOP;
END
$capture$;

COPY (
    SELECT family,
           object_order AS "order",
           object_type,
           schema_name AS schema,
           object_name AS name,
           COALESCE(actual_relkind, '') AS actual_relkind,
           COALESCE(owner_name, '') AS owner,
           COALESCE(parent_schema, '') AS parent_schema,
           COALESCE(parent_name, '') AS parent_name,
           COALESCE(row_count, '') AS row_count,
           COALESCE(total_bytes::text, '') AS total_bytes,
           COALESCE(sequence_owned_by, '') AS sequence_owned_by,
           COALESCE(sequence_last_value, '') AS sequence_last_value,
           COALESCE(sequence_is_called, '') AS sequence_is_called
    FROM retired_cleanup_relation_capture
    ORDER BY object_order
) TO STDOUT WITH (FORMAT CSV, HEADER TRUE);

COMMIT;
