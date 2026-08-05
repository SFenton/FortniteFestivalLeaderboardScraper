#!/usr/bin/env python3

import argparse
import csv
import hashlib
import io
import json
import re
import sys
import zipfile
from collections import Counter
from dataclasses import asdict, dataclass
from pathlib import Path
from xml.etree import ElementTree


CLEANUP_SCRAPE_ID = 1278
PUBLICATION_LOCK_KEY = 5067481511116519500
DDL_MAINTENANCE_LOCK_KEY = 5067481511116519501
SEQUENCE_MAINTENANCE_LOCK_KEY = 5067481511116519502
FAMILY_COUNTS = {
    "logical-shadow": 21,
    "score-observations": 3,
    "band-song-projection": 5,
    "aggregate-ranking-deltas": 32,
}
FAMILY_ORDER = list(FAMILY_COUNTS)
PUBLIC_FINGERPRINT_NAMES = [
    "leaderboard",
    "leaderboard-semantic",
    "rankings",
    "player-ranking",
    "rank-history",
    "composite-rankings",
    "band-rankings",
    "band-ranking",
    "band-history",
    "band-songs",
    "band-song-rows",
    "player-export",
    "player-export-solo",
]
RETAINED_DATA = {
    "public.leaderboard_logical_write_metrics": {
        "family": "logical-shadow",
        "expected_rows": 108,
        "canonical_file": "leaderboard_logical_write_metrics.csv",
        "key": ["scrape_id", "instrument"],
    },
    "public.band_song_team_ranking_state": {
        "family": "band-song-projection",
        "expected_rows": 3,
        "canonical_file": "band_song_team_ranking_state.csv",
        "key": ["band_type"],
    },
}
RETAINED_TEMP_TABLES = {
    "public.leaderboard_logical_write_metrics":
        "retired_cleanup_expected_logical_metrics",
    "public.band_song_team_ranking_state":
        "retired_cleanup_expected_band_state",
}
COLUMN_CATALOG_FIELDS = [
    "family",
    "object_order",
    "schema",
    "name",
    "attnum",
    "column_name",
    "is_dropped",
    "type_schema",
    "type_name",
    "type_oid",
    "typmod",
    "formatted_type",
    "not_null",
    "has_default",
    "default_expression",
    "identity_kind",
    "generated_kind",
    "collation_schema",
    "collation_name",
    "storage",
    "compression",
    "is_local",
    "inheritance_count",
    "has_missing",
    "missing_value",
    "statistics_target",
    "options",
    "fdw_options",
    "acl",
]
TARGET_REFERENCE_PATTERN = (
    "leaderboard_current_entries|leaderboard_entry_versions|"
    "leaderboard_logical_write_metrics|player_score_observations|"
    "player_score_observation_union|band_song_team_rankings|"
    "band_song_team_ranking_state|ranking_deltas|ranking_delta_tiers|"
    "rank_history_deltas|composite_ranking_deltas|combo_ranking_deltas"
)


@dataclass(frozen=True)
class CleanupObject:
    order: int
    family: str
    object_type: str
    relkind: str
    schema: str
    name: str
    parent_schema: str
    parent_name: str
    owner_column: str
    drop_method: str

    @property
    def key(self):
        return f"{self.schema}.{self.name}"

    @property
    def qualified(self):
        return f'"{self.schema}"."{self.name}"'


def sha256_path(path):
    digest = hashlib.sha256()
    with Path(path).open("rb") as handle:
        for chunk in iter(lambda: handle.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def read_csv(path):
    path = Path(path)
    if not path.is_file():
        return []
    with path.open(newline="", encoding="utf-8") as handle:
        return list(csv.DictReader(handle))


def read_objects(path):
    rows = []
    with Path(path).open(newline="", encoding="utf-8") as handle:
        for row in csv.DictReader(handle, delimiter="\t"):
            rows.append(
                CleanupObject(
                    order=int(row["order"]),
                    family=row["family"],
                    object_type=row["object_type"],
                    relkind=row["relkind"],
                    schema=row["schema"],
                    name=row["name"],
                    parent_schema=row["parent_schema"],
                    parent_name=row["parent_name"],
                    owner_column=row["owner_column"],
                    drop_method=row["drop_method"],
                )
            )

    errors = []
    if len(rows) != 61:
        errors.append(f"expected 61 cleanup objects, found {len(rows)}")
    if [row.order for row in rows] != list(range(1, len(rows) + 1)):
        errors.append("cleanup object order must be contiguous from 1")
    duplicates = [
        name for name, count in Counter(row.key for row in rows).items()
        if count != 1
    ]
    if duplicates:
        errors.append("duplicate cleanup objects: " + ", ".join(duplicates))
    if dict(Counter(row.family for row in rows)) != FAMILY_COUNTS:
        errors.append(
            "cleanup family counts differ from the exact 21/3/5/32 contract"
        )
    if [family for family, _ in _group_families(rows)] != FAMILY_ORDER:
        errors.append("cleanup families are not in the required order")
    for row in rows:
        if row.object_type in {"table", "partitioned_table"}:
            if row.drop_method != "table":
                errors.append(f"{row.key} has an invalid table drop method")
        elif row.object_type == "view":
            if row.drop_method != "view":
                errors.append(f"{row.key} has an invalid view drop method")
        elif row.object_type == "sequence":
            if row.drop_method != "owned_sequence":
                errors.append(f"{row.key} has an invalid sequence drop method")
            if not row.parent_name or not row.owner_column:
                errors.append(f"{row.key} lacks its owned-column identity")
        else:
            errors.append(f"{row.key} has unknown object type {row.object_type}")
    by_key = {row.key: row for row in rows}
    for key, retained in RETAINED_DATA.items():
        row = by_key.get(key)
        if row is None:
            errors.append(f"retained-data object is absent from allowlist: {key}")
        elif row.object_type != "table":
            errors.append(f"retained-data object is not a regular table: {key}")
        elif row.family != retained["family"]:
            errors.append(f"retained-data object has wrong family: {key}")

    order_by_key = {row.key: row.order for row in rows}
    for row in rows:
        if (
            row.object_type == "table"
            and row.parent_name
            and row.owner_column == ""
        ):
            parent_key = f"{row.parent_schema}.{row.parent_name}"
            if parent_key not in order_by_key:
                errors.append(f"{row.key} has unknown parent {parent_key}")
            elif row.order >= order_by_key[parent_key]:
                errors.append(f"{row.key} must precede parent {parent_key}")
    if errors:
        raise ValueError("; ".join(errors))
    return rows


def _group_families(objects):
    result = []
    for family in FAMILY_ORDER:
        result.append((family, [row for row in objects if row.family == family]))
    return result


def sql_literal(value):
    return "'" + value.replace("'", "''") + "'"


def render_expected_sql(objects):
    lines = [
        "CREATE TEMP TABLE retired_cleanup_expected (",
        "    object_order integer NOT NULL,",
        "    family text NOT NULL,",
        "    object_type text NOT NULL,",
        "    expected_relkind \"char\" NOT NULL,",
        "    schema_name text NOT NULL,",
        "    object_name text NOT NULL,",
        "    parent_schema text,",
        "    parent_name text,",
        "    owner_column text,",
        "    row_policy text NOT NULL,",
        "    expected_rows bigint,",
        "    PRIMARY KEY (schema_name, object_name)",
        ") ON COMMIT PRESERVE ROWS;",
        "",
        "INSERT INTO retired_cleanup_expected VALUES",
    ]
    values = []
    for row in objects:
        retained = RETAINED_DATA.get(row.key)
        if row.object_type in {"table", "partitioned_table"}:
            row_policy = "retained" if retained else "zero"
            expected_rows = retained["expected_rows"] if retained else 0
        else:
            row_policy = "none"
            expected_rows = None
        values.append(
            "    ("
            + ", ".join(
                [
                    str(row.order),
                    sql_literal(row.family),
                    sql_literal(row.object_type),
                    sql_literal(row.relkind) + '::"char"',
                    sql_literal(row.schema),
                    sql_literal(row.name),
                    sql_literal(row.parent_schema) if row.parent_schema else "NULL",
                    sql_literal(row.parent_name) if row.parent_name else "NULL",
                    sql_literal(row.owner_column) if row.owner_column else "NULL",
                    sql_literal(row_policy),
                    str(expected_rows) if expected_rows is not None else "NULL",
                ]
            )
            + ")"
        )
    lines.append(",\n".join(values) + ";")
    return "\n".join(lines) + "\n"


def runtime_assertion_sql():
    return f"""
CREATE OR REPLACE FUNCTION pg_temp.fst_assert_retired_cleanup_runtime()
RETURNS void
LANGUAGE plpgsql
AS $function$
DECLARE
    publication_row public.scrape_publication_state%ROWTYPE;
BEGIN
    IF current_setting(
        'fst.retired_schema_cleanup_manifest_sha256',
        true) !~ '^[0-9a-f]{{64}}$' THEN
        RAISE EXCEPTION 'A validated cleanup manifest SHA-256 is required';
    END IF;

    IF (SELECT count(*) FROM public.scrape_publication_state WHERE id = TRUE) <> 1 THEN
        RAISE EXCEPTION 'The publication-state singleton is missing or duplicated';
    END IF;

    SELECT *
    INTO publication_row
    FROM public.scrape_publication_state
    WHERE id = TRUE
    FOR SHARE;

    IF publication_row.published_scrape_id IS DISTINCT FROM {CLEANUP_SCRAPE_ID} THEN
        RAISE EXCEPTION 'Cleanup scrape {CLEANUP_SCRAPE_ID} is not published';
    END IF;
    IF publication_row.public_reads_frozen THEN
        RAISE EXCEPTION 'Public reads are frozen';
    END IF;
    IF publication_row.working_publication_id IS NOT NULL THEN
        RAISE EXCEPTION 'A working publication is active';
    END IF;
    IF publication_row.current_publication_id IS NULL THEN
        RAISE EXCEPTION 'The current publication pointer is missing';
    END IF;

    IF NOT EXISTS (
        SELECT 1
        FROM public.publication_generations generation
        WHERE generation.publication_id =
              publication_row.current_publication_id
          AND generation.scrape_id = {CLEANUP_SCRAPE_ID}
          AND generation.status = 'current'
          AND generation.published_at IS NOT NULL
    ) THEN
        RAISE EXCEPTION 'Cleanup scrape publication generation is not stable';
    END IF;

    IF NOT EXISTS (
        SELECT 1
        FROM public.scrape_log
        WHERE id = {CLEANUP_SCRAPE_ID}
          AND status = 'completed'
          AND completed_at IS NOT NULL
    ) THEN
        RAISE EXCEPTION 'Cleanup scrape {CLEANUP_SCRAPE_ID} is not completed';
    END IF;
    IF EXISTS (
        SELECT 1 FROM public.scrape_log WHERE status = 'running'
    ) THEN
        RAISE EXCEPTION 'A scrape is active';
    END IF;
    IF EXISTS (
        SELECT 1
        FROM public.scrape_phase_outcomes
        WHERE scrape_id = {CLEANUP_SCRAPE_ID}
          AND criticality = 'publication_critical'
          AND status = 'failed'
    ) THEN
        RAISE EXCEPTION 'Cleanup scrape has a failed critical phase';
    END IF;
    IF EXISTS (
        SELECT 1
        FROM public.service_worker_status
        WHERE worker_key = 'scraper'
          AND (
              lower(status) <> 'offline'
              OR current_operation_json IS NOT NULL
          )
    ) THEN
        RAISE EXCEPTION 'The scraper worker ledger is not offline';
    END IF;
    IF EXISTS (SELECT 1 FROM pg_catalog.pg_locks WHERE NOT granted) THEN
        RAISE EXCEPTION 'An ungranted database lock exists';
    END IF;
    IF EXISTS (SELECT 1 FROM pg_catalog.pg_stat_progress_vacuum) THEN
        RAISE EXCEPTION 'Vacuum progress is active';
    END IF;
    IF EXISTS (SELECT 1 FROM pg_catalog.pg_stat_progress_create_index) THEN
        RAISE EXCEPTION 'Index build progress is active';
    END IF;
    IF EXISTS (
        SELECT 1
        FROM pg_catalog.pg_stat_activity
        WHERE pid <> pg_backend_pid()
          AND backend_type = 'client backend'
          AND state = 'active'
          AND now() - query_start > interval '30 seconds'
    ) THEN
        RAISE EXCEPTION 'A long-running client query is active';
    END IF;
    IF EXISTS (
        SELECT 1
        FROM pg_catalog.pg_stat_activity
        WHERE pid <> pg_backend_pid()
          AND state = 'active'
          AND query ~* '{TARGET_REFERENCE_PATTERN}'
    ) THEN
        RAISE EXCEPTION 'Another session is referencing a cleanup object';
    END IF;
    IF EXISTS (
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
        SELECT 1
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
                    replace(roots.root_name, '_', '\\_') || '\\_%'
                    ESCAPE '\\'
          )
          AND NOT EXISTS (
              SELECT 1
              FROM retired_cleanup_expected expected
              WHERE expected.schema_name = schema_row.nspname
                AND expected.object_name = relation.relname
          )
    ) THEN
        RAISE EXCEPTION 'An unexpected matching cleanup relation exists';
    END IF;
END
$function$;
""".strip()


def render_drop_sql(
    objects,
    catalog_expected_sql,
    catalog_assert_sql,
    retained_expected_sql,
    retained_assert_sql,
):
    if (
        not catalog_expected_sql.strip()
        or not catalog_assert_sql.strip()
        or not retained_expected_sql.strip()
        or not retained_assert_sql.strip()
    ):
        raise ValueError("catalog and retained-data SQL are required")
    lines = [
        r"\set ON_ERROR_STOP on",
        r"\if :{?expected_manifest_sha256}",
        r"\else",
        r"\echo 'expected_manifest_sha256 psql variable is required'",
        r"\quit 64",
        r"\endif",
        "",
        "SET lock_timeout = '5s';",
        "SET statement_timeout = '30s';",
        "SET idle_in_transaction_session_timeout = '60s';",
        "SELECT set_config(",
        "    'fst.retired_schema_cleanup_manifest_sha256',",
        "    :'expected_manifest_sha256',",
        "    false);",
        "",
        render_expected_sql(objects).rstrip(),
        "",
        catalog_expected_sql.rstrip(),
        "",
        retained_expected_sql.rstrip(),
        "",
        "DO $lock$",
        "BEGIN",
        f"    IF NOT pg_catalog.pg_try_advisory_lock({PUBLICATION_LOCK_KEY}) THEN",
        "        RAISE EXCEPTION 'The publication maintenance lock is busy';",
        "    END IF;",
        f"    IF NOT pg_catalog.pg_try_advisory_lock({DDL_MAINTENANCE_LOCK_KEY}) THEN",
        "        RAISE EXCEPTION 'The FST schema-DDL maintenance guard is busy';",
        "    END IF;",
        "END",
        "$lock$;",
        "",
        runtime_assertion_sql(),
        "",
        r"\echo 'FST_ATOMIC_DROP_BEGIN'",
        "BEGIN;",
        "SET LOCAL lock_timeout = '5s';",
        "SET LOCAL statement_timeout = '30s';",
        "SET LOCAL transaction_timeout = '5min';",
        "SET LOCAL idle_in_transaction_session_timeout = '60s';",
        "SET LOCAL row_security = off;",
        "SELECT pg_temp.fst_assert_retired_cleanup_runtime();",
        "DO $sequence_guard$",
        "BEGIN",
        f"    IF NOT pg_catalog.pg_try_advisory_xact_lock({SEQUENCE_MAINTENANCE_LOCK_KEY}) THEN",
        "        RAISE EXCEPTION 'The retired sequence guard is busy';",
        "    END IF;",
        "END",
        "$sequence_guard$;",
        'ALTER SEQUENCE "public"."player_score_observations_id_seq"',
        '    OWNED BY "public"."player_score_observations"."id";',
        "",
    ]

    partitioned_parents = [
        row for row in objects if row.object_type == "partitioned_table"
    ]
    for row in partitioned_parents:
        lines.append(
            f"LOCK TABLE ONLY {row.qualified} IN ACCESS EXCLUSIVE MODE;"
        )
    lines.extend(
        [
            "",
            "DO $partition_set$",
            "BEGIN",
            "    IF EXISTS (",
            "        WITH actual_children AS (",
            "            SELECT parent_schema.nspname AS parent_schema,",
            "                   parent.relname AS parent_name,",
            "                   child_schema.nspname AS child_schema,",
            "                   child.relname AS child_name",
            "            FROM retired_cleanup_expected parent_expected",
            "            JOIN pg_catalog.pg_namespace parent_schema",
            "              ON parent_schema.nspname =",
            "                 parent_expected.schema_name",
            "            JOIN pg_catalog.pg_class parent",
            "              ON parent.relnamespace = parent_schema.oid",
            "             AND parent.relname = parent_expected.object_name",
            "            JOIN pg_catalog.pg_inherits inheritance",
            "              ON inheritance.inhparent = parent.oid",
            "            JOIN pg_catalog.pg_class child",
            "              ON child.oid = inheritance.inhrelid",
            "            JOIN pg_catalog.pg_namespace child_schema",
            "              ON child_schema.oid = child.relnamespace",
            "            WHERE parent_expected.object_type =",
            "                  'partitioned_table'",
            "        ),",
            "        expected_children AS (",
            "            SELECT child.parent_schema,",
            "                   child.parent_name,",
            "                   child.schema_name AS child_schema,",
            "                   child.object_name AS child_name",
            "            FROM retired_cleanup_expected child",
            "            WHERE child.object_type = 'table'",
            "              AND child.parent_name IS NOT NULL",
            "              AND child.owner_column IS NULL",
            "        )",
            "        SELECT 1",
            "        FROM (",
            "            (",
            "                SELECT * FROM actual_children",
            "                EXCEPT",
            "                SELECT * FROM expected_children",
            "            )",
            "            UNION ALL",
            "            (",
            "                SELECT * FROM expected_children",
            "                EXCEPT",
            "                SELECT * FROM actual_children",
            "            )",
            "        ) mismatch",
            "    ) THEN",
            "        RAISE EXCEPTION",
            "            'Attached partition set differs from allowlist';",
            "    END IF;",
            "END",
            "$partition_set$;",
            "",
        ]
    )

    for row in objects:
        if row.object_type == "table":
            lines.append(
                f"LOCK TABLE {row.qualified} IN ACCESS EXCLUSIVE MODE;"
            )
    for row in objects:
        if row.object_type == "view":
            lines.append(
                f"LOCK TABLE {row.qualified} IN ACCESS EXCLUSIVE MODE;"
            )
    lines.extend(
        [
            "",
            catalog_assert_sql.rstrip(),
            "",
            "DO $incoming_inheritance$",
            "BEGIN",
            "    IF EXISTS (",
            "        WITH actual_edges AS (",
            "            SELECT child_schema.nspname AS child_schema,",
            "                   child.relname AS child_name,",
            "                   parent_schema.nspname AS parent_schema,",
            "                   parent.relname AS parent_name",
            "            FROM retired_cleanup_expected target_expected",
            "            JOIN pg_catalog.pg_namespace child_schema",
            "              ON child_schema.nspname =",
            "                 target_expected.schema_name",
            "            JOIN pg_catalog.pg_class child",
            "              ON child.relnamespace = child_schema.oid",
            "             AND child.relname = target_expected.object_name",
            "            JOIN pg_catalog.pg_inherits inheritance",
            "              ON inheritance.inhrelid = child.oid",
            "            JOIN pg_catalog.pg_class parent",
            "              ON parent.oid = inheritance.inhparent",
            "            JOIN pg_catalog.pg_namespace parent_schema",
            "              ON parent_schema.oid = parent.relnamespace",
            "        ),",
            "        expected_edges AS (",
            "            SELECT child.schema_name AS child_schema,",
            "                   child.object_name AS child_name,",
            "                   child.parent_schema,",
            "                   child.parent_name",
            "            FROM retired_cleanup_expected child",
            "            WHERE child.object_type = 'table'",
            "              AND child.parent_name IS NOT NULL",
            "              AND child.owner_column IS NULL",
            "        )",
            "        SELECT 1",
            "        FROM (",
            "            (SELECT * FROM actual_edges",
            "             EXCEPT SELECT * FROM expected_edges)",
            "            UNION ALL",
            "            (SELECT * FROM expected_edges",
            "             EXCEPT SELECT * FROM actual_edges)",
            "        ) mismatch",
            "    ) THEN",
            "        RAISE EXCEPTION",
            "            'Incoming inheritance edges differ from allowlist';",
            "    END IF;",
            "END",
            "$incoming_inheritance$;",
            "",
            "DO $objects$",
            "BEGIN",
        ]
    )
    for row in objects:
        lines.extend(
            [
                "    IF (",
                "        SELECT relation.relkind",
                "        FROM pg_catalog.pg_class relation",
                "        JOIN pg_catalog.pg_namespace schema_row",
                "          ON schema_row.oid = relation.relnamespace",
                f"        WHERE schema_row.nspname = {sql_literal(row.schema)}",
                f"          AND relation.relname = {sql_literal(row.name)}",
                f"    ) IS DISTINCT FROM {sql_literal(row.relkind)}::\"char\" THEN",
                f"        RAISE EXCEPTION 'Missing or inexact object: {row.key}';",
                "    END IF;",
                "    IF (",
                "        SELECT pg_catalog.pg_get_userbyid(relation.relowner)",
                "        FROM pg_catalog.pg_class relation",
                "        JOIN pg_catalog.pg_namespace schema_row",
                "          ON schema_row.oid = relation.relnamespace",
                f"        WHERE schema_row.nspname = {sql_literal(row.schema)}",
                f"          AND relation.relname = {sql_literal(row.name)}",
                "    ) IS DISTINCT FROM current_user THEN",
                f"        RAISE EXCEPTION 'Unexpected owner for {row.key}';",
                "    END IF;",
                "    IF COALESCE((",
                "        SELECT relation.relrowsecurity",
                "            OR relation.relforcerowsecurity",
                "        FROM pg_catalog.pg_class relation",
                "        JOIN pg_catalog.pg_namespace schema_row",
                "          ON schema_row.oid = relation.relnamespace",
                f"        WHERE schema_row.nspname = {sql_literal(row.schema)}",
                f"          AND relation.relname = {sql_literal(row.name)}",
                "    ), TRUE) THEN",
                f"        RAISE EXCEPTION 'Row security is enabled: {row.key}';",
                "    END IF;",
            ]
        )
        if (
            row.object_type == "table"
            and row.parent_name
            and not row.owner_column
        ):
            lines.extend(
                [
                    "    IF NOT EXISTS (",
                    "        SELECT 1",
                    "        FROM pg_catalog.pg_inherits inheritance",
                    "        JOIN pg_catalog.pg_class child",
                    "          ON child.oid = inheritance.inhrelid",
                    "        JOIN pg_catalog.pg_namespace child_schema",
                    "          ON child_schema.oid = child.relnamespace",
                    "        JOIN pg_catalog.pg_class parent",
                    "          ON parent.oid = inheritance.inhparent",
                    "        JOIN pg_catalog.pg_namespace parent_schema",
                    "          ON parent_schema.oid = parent.relnamespace",
                    f"        WHERE child_schema.nspname = {sql_literal(row.schema)}",
                    f"          AND child.relname = {sql_literal(row.name)}",
                    f"          AND parent_schema.nspname = {sql_literal(row.parent_schema)}",
                    f"          AND parent.relname = {sql_literal(row.parent_name)}",
                    "    ) THEN",
                    f"        RAISE EXCEPTION 'Unexpected partition parent for {row.key}';",
                    "    END IF;",
                ]
            )
        if row.object_type == "sequence":
            owner_identity = (
                f"{row.parent_schema}.{row.parent_name}.{row.owner_column}"
            )
            lines.extend(
                [
                    "    IF NOT EXISTS (",
                    "        SELECT 1",
                    "        FROM pg_catalog.pg_depend dependency",
                    "        JOIN pg_catalog.pg_class sequence_row",
                    "          ON sequence_row.oid = dependency.objid",
                    "        JOIN pg_catalog.pg_namespace sequence_schema",
                    "          ON sequence_schema.oid = sequence_row.relnamespace",
                    "        JOIN pg_catalog.pg_class owner_table",
                    "          ON owner_table.oid = dependency.refobjid",
                    "        JOIN pg_catalog.pg_namespace owner_schema",
                    "          ON owner_schema.oid = owner_table.relnamespace",
                    "        JOIN pg_catalog.pg_attribute owner_attribute",
                    "          ON owner_attribute.attrelid = owner_table.oid",
                    "         AND owner_attribute.attnum = dependency.refobjsubid",
                    "        WHERE dependency.deptype = 'a'",
                    f"          AND sequence_schema.nspname = {sql_literal(row.schema)}",
                    f"          AND sequence_row.relname = {sql_literal(row.name)}",
                    f"          AND owner_schema.nspname = {sql_literal(row.parent_schema)}",
                    f"          AND owner_table.relname = {sql_literal(row.parent_name)}",
                    f"          AND owner_attribute.attname = {sql_literal(row.owner_column)}",
                    "    ) THEN",
                    f"        RAISE EXCEPTION 'Unexpected sequence owner for {owner_identity}';",
                    "    END IF;",
                ]
            )
        if (
            row.object_type in {"table", "partitioned_table"}
            and row.key not in RETAINED_DATA
        ):
            lines.extend(
                [
                    f"    IF EXISTS (SELECT 1 FROM {row.qualified} LIMIT 1) THEN",
                    f"        RAISE EXCEPTION 'Cleanup object is not empty: {row.key}';",
                    "    END IF;",
                ]
            )
    lines.extend(
        [
            "END",
            "$objects$;",
            "",
            retained_assert_sql.rstrip(),
            "",
            catalog_assert_sql.rstrip(),
            "",
        ]
    )

    for family, family_objects in _group_families(objects):
        lines.append(f"\\echo 'FST_FAMILY_DROP_BEGIN {family}'")
        for row in family_objects:
            if row.drop_method == "view":
                lines.append(f"DROP VIEW {row.qualified};")
            elif row.drop_method == "owned_sequence":
                owner_table = f'"{row.parent_schema}"."{row.parent_name}"'
                lines.extend(
                    [
                        f"ALTER TABLE {owner_table}",
                        f'    ALTER COLUMN "{row.owner_column}" DROP DEFAULT;',
                        f"ALTER SEQUENCE {row.qualified} OWNED BY NONE;",
                        f"DROP SEQUENCE {row.qualified};",
                    ]
                )
            elif row.drop_method == "table":
                lines.append(f"DROP TABLE {row.qualified};")
            else:
                raise ValueError(f"unsupported drop method {row.drop_method}")
        lines.extend([f"\\echo 'FST_FAMILY_DROPPED {family}'", ""])

    lines.extend(
        [
            "COMMIT;",
            r"\echo 'FST_ALL_COMMITTED'",
            "",
            "DO $unlock$",
            "BEGIN",
            f"    IF NOT pg_catalog.pg_advisory_unlock({DDL_MAINTENANCE_LOCK_KEY}) THEN",
            "        RAISE EXCEPTION 'The schema-DDL guard was not held';",
            "    END IF;",
            f"    IF NOT pg_catalog.pg_advisory_unlock({PUBLICATION_LOCK_KEY}) THEN",
            "        RAISE EXCEPTION 'The publication maintenance lock was not held';",
            "    END IF;",
            "END",
            "$unlock$;",
            "",
        ]
    )
    sql = "\n".join(lines)
    errors = validate_drop_sql_text(objects, sql)
    if errors:
        raise ValueError("; ".join(errors))
    return sql


def render_rehearsal_check_sql(objects):
    lines = ["DO $rollback_check$", "BEGIN"]
    for row in objects:
        lines.extend(
            [
                "    IF (",
                "        SELECT relation.relkind",
                "        FROM pg_catalog.pg_class relation",
                "        JOIN pg_catalog.pg_namespace schema_row",
                "          ON schema_row.oid = relation.relnamespace",
                f"        WHERE schema_row.nspname = {sql_literal(row.schema)}",
                f"          AND relation.relname = {sql_literal(row.name)}",
                f"    ) IS DISTINCT FROM {sql_literal(row.relkind)}::\"char\" THEN",
                f"        RAISE EXCEPTION 'Rollback rehearsal did not recreate {row.key}';",
                "    END IF;",
            ]
        )
    lines.extend(["END", "$rollback_check$;"])
    return "\n".join(lines) + "\n"


def validate_drop_sql_text(objects, sql):
    errors = []
    if re.search(
        r"^\s*DROP\s+(?:TABLE|VIEW|SEQUENCE).*\bCASCADE\b",
        sql,
        re.IGNORECASE | re.MULTILINE,
    ):
        errors.append("drop SQL contains a cascading clause")
    if re.search(
        r"DROP\s+(?:TABLE|VIEW|SEQUENCE)\s+IF\s+EXISTS",
        sql,
        re.IGNORECASE,
    ):
        errors.append("drop SQL uses IF EXISTS")
    if re.search(r"\bDROP\s+INDEX\b", sql, re.IGNORECASE):
        errors.append("drop SQL contains an explicit index drop")
    if re.search(
        r"^\s*TRUNCATE\b",
        sql,
        re.IGNORECASE | re.MULTILINE,
    ):
        errors.append("drop SQL contains a truncate")
    positions = []
    for row in objects:
        keyword = {
            "table": "DROP TABLE",
            "view": "DROP VIEW",
            "owned_sequence": "DROP SEQUENCE",
        }[row.drop_method]
        statement = f"{keyword} {row.qualified};"
        count = sql.count(statement)
        if count != 1:
            errors.append(f"{row.key} drop statement count is {count}")
        positions.append((row.order, sql.find(statement), row.key))
    if any(position < 0 for _, position, _ in positions):
        return errors
    if [position for _, position, _ in positions] != sorted(
        position for _, position, _ in positions
    ):
        errors.append("drop statements do not follow objects.tsv order")

    sequence = next(row for row in objects if row.object_type == "sequence")
    sequence_drop = sql.index(f"DROP SEQUENCE {sequence.qualified};")
    owner_table = f'"{sequence.parent_schema}"."{sequence.parent_name}"'
    default_drop = sql.index(
        f"ALTER TABLE {owner_table}\n"
        f'    ALTER COLUMN "{sequence.owner_column}" DROP DEFAULT;'
    )
    owned_none = sql.index(
        f"ALTER SEQUENCE {sequence.qualified} OWNED BY NONE;"
    )
    owner_table_drop = sql.index(f"DROP TABLE {owner_table};")
    if not (default_drop < owned_none < sequence_drop < owner_table_drop):
        errors.append("owned sequence handling is not dependency-safe")

    for family in FAMILY_ORDER:
        if sql.count(f"FST_FAMILY_DROP_BEGIN {family}") != 1:
            errors.append(f"{family} drop marker is missing")
        if sql.count(f"FST_FAMILY_DROPPED {family}") != 1:
            errors.append(f"{family} dropped marker is missing")
    if sql.count("FST_ALL_COMMITTED") != 1:
        errors.append("atomic commit marker is missing")
    if len(re.findall(r"^BEGIN;$", sql, re.MULTILINE)) != 1:
        errors.append("drop SQL must use exactly one explicit transaction")
    if len(re.findall(r"^COMMIT;$", sql, re.MULTILINE)) != 1:
        errors.append("drop SQL must use exactly one atomic commit")
    if sql.count("SET LOCAL lock_timeout = '5s';") != 1:
        errors.append("atomic transaction must set one short lock timeout")
    if sql.count("SET LOCAL statement_timeout = '30s';") != 1:
        errors.append("atomic transaction must set one statement timeout")
    if sql.count("SET LOCAL transaction_timeout = '5min';") != 1:
        errors.append("atomic transaction must set one bounded timeout")
    first_lock = sql.find("LOCK TABLE ")
    first_drop = min(position for _, position, _ in positions)
    last_lock = sql.rfind("LOCK TABLE ")
    runtime_gate = sql.find(
        "SELECT pg_temp.fst_assert_retired_cleanup_runtime();"
    )
    if not (0 <= runtime_gate < first_lock <= last_lock < first_drop):
        errors.append(
            "contention gate and all table locks must precede every drop"
        )
    if sql.count(
        "SELECT pg_temp.fst_assert_retired_cleanup_runtime();"
    ) != 1:
        errors.append("runtime contention gate must run exactly once")
    catalog_gate = sql.find("DO $catalog_signature$")
    final_catalog_gate = sql.rfind("DO $catalog_signature$")
    object_gate = sql.find("DO $objects$")
    if not (last_lock < catalog_gate < object_gate < first_drop):
        errors.append(
            "complete catalog signature must be recaptured immediately "
            "after all target locks and before object checks/drops"
        )
    if not (object_gate < final_catalog_gate < first_drop):
        errors.append(
            "final catalog signature recheck must be the last database "
            "gate before drops"
        )
    if sql.count("Complete cleanup catalog signature drifted") != 2:
        errors.append("both complete catalog signature gates are required")
    if sql.count("Incoming inheritance edges differ from allowlist") != 1:
        errors.append("complete incoming inheritance gate is missing")
    if str(SEQUENCE_MAINTENANCE_LOCK_KEY) not in sql:
        errors.append("retired sequence advisory guard is missing")
    if str(DDL_MAINTENANCE_LOCK_KEY) not in sql:
        errors.append("schema-DDL advisory guard is missing")
    sequence_lock = sql.find(
        'ALTER SEQUENCE "public"."player_score_observations_id_seq"\n'
        '    OWNED BY "public"."player_score_observations"."id";'
    )
    if not (runtime_gate < sequence_lock < first_lock):
        errors.append(
            "owned sequence must take its transactional ALTER lock "
            "before catalog signatures and drops"
        )
    partition_gate = sql.find("DO $partition_set$")
    first_child_lock = min(
        sql.find(f"LOCK TABLE {row.qualified} IN ACCESS EXCLUSIVE MODE;")
        for row in objects
        if row.object_type == "table"
    )
    last_parent_lock = max(
        sql.find(
            f"LOCK TABLE ONLY {row.qualified} "
            "IN ACCESS EXCLUSIVE MODE;"
        )
        for row in objects
        if row.object_type == "partitioned_table"
    )
    if not (last_parent_lock < partition_gate < first_child_lock):
        errors.append(
            "complete partition set must be checked after parent locks "
            "and before child locks"
        )
    for key in RETAINED_DATA:
        schema, name = key.split(".", 1)
        if (
            f"IF EXISTS (SELECT 1 FROM "
            f'"{schema}"."{name}" LIMIT 1)'
        ) in sql:
            errors.append(f"retained table incorrectly uses zero-row gate: {key}")
        if f"Retained payload changed: {key}" not in sql:
            errors.append(f"retained equality gate is missing: {key}")
    if sql.count("Row security is enabled:") != len(objects):
        errors.append("every target must have an under-lock RLS rejection gate")
    if sql.count("SET LOCAL row_security = off;") < 1:
        errors.append("destructive data probes must disable row security")
    forbidden = {
        "account_rankings",
        "current_leaderboard_entries",
        "leaderboard_entries_snapshot",
        "leaderboard_entries_overlay",
        "score_history",
        "band_team_rankings_current",
        "band_team_rankings_published",
        "current_band_leaderboard_entries",
        "composite_rankings",
        "composite_rank_history",
        "solo_family_rankings",
        "rank_history",
        "combo_leaderboard",
    }
    for name in forbidden:
        if f'"{name}"' in sql:
            errors.append(f"active relation {name} appears in drop SQL")
    return errors


def bool_value(value):
    return str(value).strip().lower() in {"1", "t", "true", "yes", "ok"}


def stable_rows(rows):
    return sorted(
        ({str(key): str(value) for key, value in row.items()} for row in rows),
        key=lambda row: json.dumps(row, sort_keys=True),
    )


def read_retained_specs(path):
    with Path(path).open(newline="", encoding="utf-8") as handle:
        rows = list(csv.DictReader(handle, delimiter="\t"))
    expected_fields = [
        "family",
        "schema",
        "name",
        "expected_rows",
        "canonical_file",
    ]
    if not rows or list(rows[0]) != expected_fields:
        raise ValueError("retained-data specification has an invalid header")
    by_key = {
        f"{row.get('schema', '')}.{row.get('name', '')}": row
        for row in rows
    }
    if set(by_key) != set(RETAINED_DATA):
        raise ValueError("retained-data specification has an inexact table set")
    for key, expected in RETAINED_DATA.items():
        row = by_key[key]
        exact = {
            "family": expected["family"],
            "schema": key.split(".", 1)[0],
            "name": key.split(".", 1)[1],
            "expected_rows": str(expected["expected_rows"]),
            "canonical_file": expected["canonical_file"],
        }
        if row != exact:
            raise ValueError(f"retained-data specification drift: {key}")
    return rows


def _retained_sort_key(key, row):
    if key == "public.leaderboard_logical_write_metrics":
        return (int(row["scrape_id"]), row["instrument"])
    return (row["band_type"],)


def read_column_catalog(path):
    with Path(path).open(newline="", encoding="utf-8") as handle:
        reader = csv.DictReader(handle)
        if reader.fieldnames != COLUMN_CATALOG_FIELDS:
            raise ValueError("column catalog has an invalid header")
        return list(reader)


def prepare_column_catalog(args):
    objects = read_objects(args.objects)
    rows = read_column_catalog(args.input)
    object_keys = {row.key for row in objects}
    seen = set()
    captured_objects = set()
    for row in rows:
        key = f"{row['schema']}.{row['name']}"
        if key not in object_keys:
            raise ValueError(f"column catalog contains non-target object: {key}")
        try:
            attnum = int(row["attnum"])
            int(row["typmod"])
            int(row["type_oid"])
            int(row["inheritance_count"])
            int(row["statistics_target"])
        except ValueError as exc:
            raise ValueError(f"column catalog has invalid numeric data: {key}") from exc
        if attnum <= 0:
            raise ValueError(f"column catalog has invalid attnum: {key}")
        identity = (key, attnum)
        if identity in seen:
            raise ValueError(f"column catalog has duplicate attnum: {key}")
        seen.add(identity)
        captured_objects.add(key)
        if bool_value(row["is_dropped"]):
            raise ValueError(
                f"column catalog contains non-restorable dropped column: {key}"
            )
        if bool_value(row["has_missing"]) or row["missing_value"]:
            raise ValueError(
                f"column catalog contains non-restorable missing value: {key}"
            )
        if not row["column_name"] or not row["formatted_type"]:
            raise ValueError(f"column catalog has incomplete column: {key}")
    if captured_objects != object_keys:
        missing = sorted(object_keys - captured_objects)
        raise ValueError(
            "column catalog lacks target objects: " + ", ".join(missing)
        )
    rows.sort(key=lambda row: (int(row["object_order"]), int(row["attnum"])))
    with Path(args.output).open(
        "w",
        newline="",
        encoding="utf-8",
    ) as handle:
        writer = csv.DictWriter(
            handle,
            fieldnames=COLUMN_CATALOG_FIELDS,
            lineterminator="\n",
        )
        writer.writeheader()
        writer.writerows(rows)


def retained_columns(column_rows, key):
    schema, name = key.split(".", 1)
    rows = sorted(
        (
            row
            for row in column_rows
            if row["schema"] == schema
            and row["name"] == name
            and not bool_value(row["is_dropped"])
        ),
        key=lambda row: int(row["attnum"]),
    )
    if not rows:
        raise ValueError(f"retained table has no bound columns: {key}")
    names = [row["column_name"] for row in rows]
    if len(names) != len(set(names)):
        raise ValueError(f"retained table has duplicate columns: {key}")
    for required in RETAINED_DATA[key]["key"]:
        if required not in names:
            raise ValueError(f"retained table lacks key column {key}.{required}")
    generated = [
        row["column_name"] for row in rows if row["generated_kind"]
    ]
    if generated:
        raise ValueError(
            f"retained generated columns are unsupported: "
            f"{key}.{','.join(generated)}"
        )
    return rows


def column_signature_projection(row):
    return {
        "name": row["column_name"],
        "dropped": bool_value(row["is_dropped"]),
        "typeSchema": row["type_schema"],
        "typeName": row["type_name"],
        "typeOid": int(row["type_oid"]),
        "typmod": int(row["typmod"]),
        "formattedType": row["formatted_type"],
        "notNull": bool_value(row["not_null"]),
        "hasDefault": bool_value(row["has_default"]),
        "default": row["default_expression"],
        "identity": row["identity_kind"],
        "generated": row["generated_kind"],
        "collationSchema": row["collation_schema"],
        "collation": row["collation_name"],
        "storage": row["storage"],
        "compression": row["compression"],
        "isLocal": bool_value(row["is_local"]),
        "inheritanceCount": int(row["inheritance_count"]),
        "hasMissing": bool_value(row["has_missing"]),
        "missingValue": row["missing_value"],
        "statisticsTarget": int(row["statistics_target"]),
    }


def render_retained_capture_sql(args):
    specs = read_retained_specs(args.spec)
    columns = read_column_catalog(args.column_catalog)
    output_dir = Path(args.output_dir)
    output_dir.mkdir(parents=True, exist_ok=True)
    for spec in specs:
        key = f"{spec['schema']}.{spec['name']}"
        bound_columns = retained_columns(columns, key)
        column_sql = ",\n           ".join(
            f'"{row["column_name"]}"' for row in bound_columns
        )
        order_sql = ", ".join(
            f'"{name}"' for name in RETAINED_DATA[key]["key"]
        )
        sql = "\n".join(
            [
                r"\set ON_ERROR_STOP on",
                "",
                "BEGIN TRANSACTION READ ONLY;",
                "SET LOCAL lock_timeout = '2s';",
                "SET LOCAL statement_timeout = '15s';",
                "SET LOCAL TIME ZONE 'UTC';",
                "SET LOCAL DateStyle = 'ISO, YMD';",
                "SET LOCAL row_security = off;",
                "",
                "COPY (",
                f"    SELECT {column_sql}",
                f'    FROM "{spec["schema"]}"."{spec["name"]}"',
                f"    ORDER BY {order_sql}",
                ") TO STDOUT WITH (FORMAT CSV, HEADER TRUE);",
                "",
                "COMMIT;",
                "",
            ]
        )
        filename = Path(spec["canonical_file"]).with_suffix(".sql").name
        (output_dir / filename).write_text(sql, encoding="utf-8")


def _validate_retained_rows(key, rows, columns):
    expected = RETAINED_DATA[key]
    field_names = [row["column_name"] for row in columns]
    if len(rows) != expected["expected_rows"]:
        raise ValueError(
            f"{key} has {len(rows)} retained rows; "
            f"expected {expected['expected_rows']}"
        )
    seen = set()
    integer_fields = {
        row["column_name"]
        for row in columns
        if row["formatted_type"] in {
            "smallint",
            "integer",
            "bigint",
        }
    }
    timestamp_fields = {
        row["column_name"]
        for row in columns
        if row["formatted_type"].startswith("timestamp")
    }
    for row in rows:
        if list(row) != field_names:
            raise ValueError(f"{key} retained payload has an invalid header")
        identity = tuple(row[field] for field in expected["key"])
        if identity in seen:
            raise ValueError(f"{key} retained payload has a duplicate key")
        seen.add(identity)
        for field in integer_fields:
            if not re.fullmatch(r"\d+", row[field]):
                raise ValueError(
                    f"{key}.{field} is not a nonnegative integer"
                )
        for field in timestamp_fields:
            if not row[field].strip():
                raise ValueError(f"{key}.{field} timestamp is empty")
    if key == "public.band_song_team_ranking_state":
        if {row["band_type"] for row in rows} != {
            "Band_Duets",
            "Band_Trios",
            "Band_Quad",
        }:
            raise ValueError("band-song retained state has an inexact band set")
    return sorted(rows, key=lambda row: _retained_sort_key(key, row))


def _render_copy_payload(table_name, columns, csv_text):
    column_sql = ", ".join(f'"{row["column_name"]}"' for row in columns)
    return "\n".join(
        [
            f"COPY {table_name} ({column_sql})",
            "FROM STDIN WITH (FORMAT csv, HEADER true);",
            csv_text.rstrip("\n"),
            r"\.",
            "",
        ]
    )


def prepare_retained_data(args):
    specs = read_retained_specs(args.spec)
    column_catalog = read_column_catalog(args.column_catalog)
    raw_dir = Path(args.raw_dir)
    canonical_dir = Path(args.canonical_dir)
    canonical_dir.mkdir(parents=True, exist_ok=True)
    expected_sql = [
        "-- Exact manifest-bound retained data used for under-lock equality.",
    ]
    assert_sql = ["DO $retained_data$", "BEGIN"]
    metadata = []

    for spec in specs:
        key = f"{spec['schema']}.{spec['name']}"
        definition = RETAINED_DATA[key]
        bound_columns = retained_columns(column_catalog, key)
        raw_path = raw_dir / definition["canonical_file"]
        with raw_path.open(newline="", encoding="utf-8") as handle:
            reader = csv.DictReader(handle)
            field_names = [
                row["column_name"] for row in bound_columns
            ]
            if reader.fieldnames != field_names:
                raise ValueError(f"{key} retained payload header is inexact")
            rows = _validate_retained_rows(
                key,
                list(reader),
                bound_columns,
            )

        canonical_path = canonical_dir / definition["canonical_file"]
        with canonical_path.open("w", newline="", encoding="utf-8") as handle:
            writer = csv.DictWriter(
                handle,
                fieldnames=field_names,
                lineterminator="\n",
            )
            writer.writeheader()
            writer.writerows(rows)
        csv_text = canonical_path.read_text(encoding="utf-8")
        temp_name = RETAINED_TEMP_TABLES[key]
        column_definitions = ",\n    ".join(
            f'"{row["column_name"]}" {row["formatted_type"]}'
            + (" NOT NULL" if bool_value(row["not_null"]) else "")
            for row in bound_columns
        )
        expected_sql.extend(
            [
                "",
                f"CREATE TEMP TABLE {temp_name} (",
                f"    {column_definitions}",
                ") ON COMMIT PRESERVE ROWS;",
                _render_copy_payload(
                    f"pg_temp.{temp_name}",
                    bound_columns,
                    csv_text,
                ).rstrip(),
            ]
        )

        actual_name = (
            f'"{spec["schema"]}"."{spec["name"]}"'
        )
        column_sql = ", ".join(
            f'"{row["column_name"]}"' for row in bound_columns
        )
        assert_sql.extend(
            [
                f"    IF (SELECT count(*) FROM {actual_name}) "
                f"<> {definition['expected_rows']} THEN",
                f"        RAISE EXCEPTION "
                f"'Retained row count changed: {key}';",
                "    END IF;",
                "    IF EXISTS (",
                "        (",
                f"            SELECT {column_sql} FROM {actual_name}",
                "            EXCEPT ALL",
                f"            SELECT {column_sql} "
                f"FROM pg_temp.{temp_name}",
                "        )",
                "        UNION ALL",
                "        (",
                f"            SELECT {column_sql} "
                f"FROM pg_temp.{temp_name}",
                "            EXCEPT ALL",
                f"            SELECT {column_sql} FROM {actual_name}",
                "        )",
                "    ) THEN",
                f"        RAISE EXCEPTION "
                f"'Retained payload changed: {key}';",
                "    END IF;",
            ]
        )

        rollback_path = (
            Path(args.logical_rollback)
            if definition["family"] == "logical-shadow"
            else Path(args.band_rollback)
        )
        marker = f"-- Retained payload: {key}"
        rollback_text = rollback_path.read_text(encoding="utf-8")
        if marker in rollback_text:
            raise ValueError(f"rollback payload already exists: {key}")
        with rollback_path.open("a", encoding="utf-8", newline="") as handle:
            handle.write("\n" + marker + "\n")
            handle.write(
                _render_copy_payload(
                    actual_name,
                    bound_columns,
                    csv_text,
                )
            )

        metadata.append(
            {
                "family": definition["family"],
                "schema": spec["schema"],
                "name": spec["name"],
                "expected_rows": str(definition["expected_rows"]),
                "row_count": str(len(rows)),
                "canonical_file": definition["canonical_file"],
                "sha256": sha256_path(canonical_path),
                "payload_bytes": str(canonical_path.stat().st_size),
                "column_count": str(len(bound_columns)),
                "column_catalog_sha256": sha256_path(args.column_catalog),
            }
        )

    assert_sql.extend(["END", "$retained_data$;", ""])
    Path(args.expected_sql_output).write_text(
        "\n".join(expected_sql) + "\n",
        encoding="utf-8",
    )
    Path(args.assert_sql_output).write_text(
        "\n".join(assert_sql),
        encoding="utf-8",
    )
    metadata_fields = [
        "family",
        "schema",
        "name",
        "expected_rows",
        "row_count",
        "canonical_file",
        "sha256",
        "payload_bytes",
        "column_count",
        "column_catalog_sha256",
    ]
    with Path(args.metadata_output).open(
        "w",
        newline="",
        encoding="utf-8",
    ) as handle:
        writer = csv.DictWriter(
            handle,
            fieldnames=metadata_fields,
            lineterminator="\n",
        )
        writer.writeheader()
        writer.writerows(metadata)


def prepare_catalog_signature(args):
    query_text = Path(args.query).read_text(encoding="utf-8").strip()
    if query_text.endswith(";"):
        query_text = query_text[:-1].rstrip()
    with Path(args.input).open(newline="", encoding="utf-8") as handle:
        reader = csv.DictReader(handle)
        if reader.fieldnames != ["category", "object_identity", "detail"]:
            raise ValueError("catalog signature has an invalid header")
        rows = list(reader)
    canonical_rows = []
    category_counts = Counter()
    for row in rows:
        detail = json.dumps(
            json.loads(row["detail"]),
            sort_keys=True,
            separators=(",", ":"),
            ensure_ascii=False,
        )
        canonical_rows.append(
            {
                "category": row["category"],
                "object_identity": row["object_identity"],
                "detail": detail,
            }
        )
        category_counts[row["category"]] += 1
        detail_value = json.loads(detail)
        if row["category"] == "relation":
            if (
                detail_value.get("rowSecurity") is not False
                or detail_value.get("forceRowSecurity") is not False
            ):
                raise ValueError(
                    f"target row security is enabled: "
                    f"{row['object_identity']}"
                )
        if row["category"] == "index":
            if (
                detail_value.get("valid") is not True
                or detail_value.get("ready") is not True
                or detail_value.get("live") is not True
                or detail_value.get("checkXmin") is not False
            ):
                raise ValueError(
                    f"non-restorable index state: {row['object_identity']}"
                )
        if row["category"] in {"partition", "incoming-inheritance"}:
            if detail_value.get("detachPending") is not False:
                raise ValueError(
                    f"non-restorable detach state: {row['object_identity']}"
                )
    canonical_rows.sort(
        key=lambda row: (
            row["category"],
            row["object_identity"],
            row["detail"],
        )
    )
    if category_counts["relation"] != 61:
        raise ValueError("catalog signature must contain 61 relations")
    if category_counts["partition"] != 45:
        raise ValueError("catalog signature must contain 45 attachments")
    if category_counts["incoming-inheritance"] != 45:
        raise ValueError(
            "catalog signature must contain 45 incoming inheritance edges"
        )
    if category_counts["sequence"] != 1 or category_counts["view"] != 1:
        raise ValueError("catalog signature sequence/view coverage is inexact")

    column_rows = read_column_catalog(args.column_catalog)
    expected_column_ids = {
        f"{row['schema']}.{row['name']}#{row['attnum']}"
        for row in column_rows
    }
    actual_column_ids = {
        row["object_identity"]
        for row in canonical_rows
        if row["category"] == "column"
    }
    if actual_column_ids != expected_column_ids:
        raise ValueError(
            "catalog signature column set differs from column catalog"
        )
    signature_columns = {
        row["object_identity"]: json.loads(row["detail"])
        for row in canonical_rows
        if row["category"] == "column"
    }
    for row in column_rows:
        identity = f"{row['schema']}.{row['name']}#{row['attnum']}"
        actual = signature_columns[identity]
        expected = column_signature_projection(row)
        for field, expected_value in expected.items():
            if actual.get(field) != expected_value:
                raise ValueError(
                    f"catalog signature column drift: {identity}.{field}"
                )

    output = Path(args.output)
    with output.open("w", newline="", encoding="utf-8") as handle:
        writer = csv.DictWriter(
            handle,
            fieldnames=["category", "object_identity", "detail"],
            lineterminator="\n",
        )
        writer.writeheader()
        writer.writerows(canonical_rows)
    csv_text = output.read_text(encoding="utf-8")
    expected_sql = "\n".join(
        [
            "-- Manifest-bound complete catalog signature.",
            "CREATE TEMP TABLE retired_cleanup_expected_catalog (",
            "    category text NOT NULL,",
            "    object_identity text NOT NULL,",
            "    detail jsonb NOT NULL",
            ") ON COMMIT PRESERVE ROWS;",
            _render_copy_payload(
                "pg_temp.retired_cleanup_expected_catalog",
                [
                    {"column_name": "category"},
                    {"column_name": "object_identity"},
                    {"column_name": "detail"},
                ],
                csv_text,
            ).rstrip(),
            "",
        ]
    )
    Path(args.expected_sql_output).write_text(
        expected_sql,
        encoding="utf-8",
    )
    indented_query = "\n".join(
        "            " + line for line in query_text.splitlines()
    )
    assert_sql = "\n".join(
        [
            "DO $catalog_signature$",
            "BEGIN",
            "    IF EXISTS (",
            "        WITH current_signature AS (",
            indented_query,
            "        )",
            "        SELECT 1",
            "        FROM (",
            "            (",
            "                SELECT category, object_identity, detail",
            "                FROM current_signature",
            "                EXCEPT ALL",
            "                SELECT category, object_identity, detail",
            "                FROM pg_temp.retired_cleanup_expected_catalog",
            "            )",
            "            UNION ALL",
            "            (",
            "                SELECT category, object_identity, detail",
            "                FROM pg_temp.retired_cleanup_expected_catalog",
            "                EXCEPT ALL",
            "                SELECT category, object_identity, detail",
            "                FROM current_signature",
            "            )",
            "        ) mismatch",
            "    ) THEN",
            "        RAISE EXCEPTION",
            "            'Complete cleanup catalog signature drifted';",
            "    END IF;",
            "END",
            "$catalog_signature$;",
            "",
        ]
    )
    Path(args.assert_sql_output).write_text(
        assert_sql,
        encoding="utf-8",
    )
    metadata = {
        "schemaVersion": 1,
        "rowCount": len(canonical_rows),
        "sha256": sha256_path(output),
        "querySha256": sha256_path(args.query),
        "columnCatalogSha256": sha256_path(args.column_catalog),
        "categoryCounts": dict(sorted(category_counts.items())),
    }
    Path(args.metadata_output).write_text(
        json.dumps(metadata, sort_keys=True, separators=(",", ":")) + "\n",
        encoding="utf-8",
    )


def build_manifest(args):
    objects = read_objects(args.objects)
    capture_dir = Path(args.capture_dir)
    failures = []

    relation_rows = read_csv(capture_dir / "relations.csv")
    relation_by_key = {
        f"{row.get('schema', '')}.{row.get('name', '')}": row
        for row in relation_rows
    }
    if len(relation_rows) != len(objects):
        failures.append(
            f"relation inventory has {len(relation_rows)} rows, expected 61"
        )
    stable_relations = []
    for expected in objects:
        actual = relation_by_key.get(expected.key)
        if actual is None:
            failures.append(f"relation inventory is missing {expected.key}")
            continue
        if actual.get("actual_relkind", "") != expected.relkind:
            failures.append(f"{expected.key} has an unexpected relation kind")
        if actual.get("owner", "") != args.expected_owner:
            failures.append(f"{expected.key} has an unexpected owner")
        if actual.get("parent_schema", "") != expected.parent_schema:
            failures.append(f"{expected.key} has an unexpected parent schema")
        if actual.get("parent_name", "") != expected.parent_name:
            failures.append(f"{expected.key} has an unexpected parent")
        if expected.object_type in {"table", "partitioned_table"}:
            retained = RETAINED_DATA.get(expected.key)
            expected_rows = str(retained["expected_rows"] if retained else 0)
            if actual.get("row_count", "") != expected_rows:
                if retained:
                    failures.append(
                        f"{expected.key} retained row count is not "
                        f"{expected_rows}"
                    )
                else:
                    failures.append(f"{expected.key} is not exactly empty")
        if expected.object_type == "sequence":
            expected_owner = (
                f"{expected.parent_schema}.{expected.parent_name}."
                f"{expected.owner_column}"
            )
            if actual.get("sequence_owned_by", "") != expected_owner:
                failures.append(f"{expected.key} has an unexpected sequence owner")
        stable_relations.append(
            {
                key: actual.get(key, "")
                for key in [
                    "family",
                    "order",
                    "object_type",
                    "schema",
                    "name",
                    "actual_relkind",
                    "owner",
                    "parent_schema",
                    "parent_name",
                    "row_count",
                    "total_bytes",
                    "sequence_owned_by",
                    "sequence_last_value",
                    "sequence_is_called",
                ]
            }
        )

    unexpected_rows = read_csv(capture_dir / "unexpected-relations.csv")
    if unexpected_rows:
        failures.append(
            f"{len(unexpected_rows)} unexpected matching relation(s) exist"
        )
    partition_rows = read_csv(capture_dir / "partition-children.csv")
    expected_partition_children = {
        (
            f"{row.parent_schema}.{row.parent_name}",
            row.key,
        ): row
        for row in objects
        if row.object_type == "table"
        and row.parent_name
        and not row.owner_column
    }
    actual_partition_children = {
        (
            f"{row.get('parent_schema', '')}.{row.get('parent_name', '')}",
            f"{row.get('child_schema', '')}.{row.get('child_name', '')}",
        ): row
        for row in partition_rows
    }
    missing_partition_children = sorted(
        set(expected_partition_children) - set(actual_partition_children)
    )
    unexpected_partition_children = sorted(
        set(actual_partition_children) - set(expected_partition_children)
    )
    if missing_partition_children:
        failures.append(
            f"{len(missing_partition_children)} expected partition child(ren) "
            "are missing"
        )
    if unexpected_partition_children:
        failures.append(
            f"{len(unexpected_partition_children)} unexpected attached "
            "partition child(ren) exist"
        )
    for identity, row in actual_partition_children.items():
        expected_child = expected_partition_children.get(identity)
        if expected_child is None:
            continue
        if row.get("child_relkind", "") != expected_child.relkind:
            failures.append(
                f"partition child has an unexpected relkind: {identity[1]}"
            )
        if row.get("child_owner", "") != args.expected_owner:
            failures.append(
                f"partition child has an unexpected owner: {identity[1]}"
            )
        if not row.get("partition_bound", ""):
            failures.append(f"partition child lacks a bound: {identity[1]}")
    incoming_rows = read_csv(capture_dir / "incoming-inheritance.csv")
    expected_incoming = {
        (
            row.key,
            f"{row.parent_schema}.{row.parent_name}",
        )
        for row in objects
        if row.object_type == "table"
        and row.parent_name
        and not row.owner_column
    }
    actual_incoming = {
        (
            f"{row.get('target_schema', '')}.{row.get('target_name', '')}",
            f"{row.get('parent_schema', '')}.{row.get('parent_name', '')}",
        )
        for row in incoming_rows
    }
    if actual_incoming != expected_incoming:
        failures.append(
            "incoming inheritance edges differ from the exact allowlist"
        )
    for row in incoming_rows:
        parent_key = (
            f"{row.get('parent_schema', '')}.{row.get('parent_name', '')}"
        )
        if parent_key not in {item.key for item in objects}:
            failures.append(
                f"target has an external inheritance parent: {parent_key}"
            )
        if row.get("parent_relkind") != "p":
            failures.append("incoming inheritance parent is not partitioned")
        if row.get("parent_owner") != args.expected_owner:
            failures.append("incoming inheritance parent owner drift")
        if bool_value(row.get("detach_pending", "false")):
            failures.append("incoming inheritance detach is pending")
    dependency_rows = read_csv(capture_dir / "external-dependencies.csv")
    if dependency_rows:
        failures.append(
            f"{len(dependency_rows)} external database dependency row(s) exist"
        )
    owned_rows = read_csv(capture_dir / "owned-objects.csv")
    if not (capture_dir / "owned-objects.csv").is_file():
        failures.append("owned-object inventory is missing")
    tooling_rows = read_csv(capture_dir / "tooling-hashes.csv")
    if not tooling_rows:
        failures.append("tooling hash inventory is missing")
    for row in tooling_rows:
        if not re.fullmatch(r"[0-9a-f]{64}", row.get("sha256", "")):
            failures.append(f"invalid tooling hash: {row.get('name')}")

    preflight_rows = read_csv(capture_dir / "preflight.csv")
    preflight = preflight_rows[0] if len(preflight_rows) == 1 else {}
    if len(preflight_rows) != 1:
        failures.append("preflight must contain exactly one row")

    required_preflight = {
        "published_scrape_id": str(CLEANUP_SCRAPE_ID),
        "public_reads_frozen": "false",
        "working_publication_id": "",
        "current_publication_scrape_id": str(CLEANUP_SCRAPE_ID),
        "current_publication_status": "current",
        "cleanup_scrape_status": "completed",
        "cleanup_scrape_completed": "true",
        "active_scrape_count": "0",
        "worker_current_operation": "false",
        "ungranted_lock_count": "0",
        "long_query_count": "0",
        "target_query_count": "0",
        "active_vacuum_count": "0",
        "active_index_build_count": "0",
        "active_rewrite_count": "0",
        "critical_phase_failure_count": "0",
        "ddl_guard_available": "true",
        "sequence_guard_available": "true",
    }
    for key, expected_value in required_preflight.items():
        if preflight.get(key, "").lower() != expected_value:
            failures.append(
                f"preflight {key}={preflight.get(key, '<missing>')} "
                f"(expected {expected_value or '<empty>'})"
            )
    if preflight.get("worker_status", "").lower() not in {"absent", "offline"}:
        failures.append("worker ledger is not absent/offline")

    container_rows = read_csv(capture_dir / "containers.csv")
    containers = {row.get("service", ""): row for row in container_rows}
    for service in ["postgres", "fstservice", "festivalweb"]:
        row = containers.get(service)
        if row is None:
            failures.append(f"{service} container state is missing")
            continue
        if row.get("state", "").lower() != "running":
            failures.append(f"{service} is not running")
        if row.get("health", "").lower() != "healthy":
            failures.append(f"{service} is not healthy")
    worker_container = containers.get("fstworker")
    if worker_container is None:
        failures.append("fstworker container state is missing")
    elif worker_container.get("state", "").lower() not in {
        "created",
        "dead",
        "exited",
        "stopped",
    }:
        failures.append("fstworker is not stopped")
    elif worker_container.get("restart_policy", "").lower() != "no":
        failures.append("fstworker restart policy is not no")

    service_container = containers.get("fstservice", {})
    if (
        service_container.get("image_id", "")
        != service_container.get("compose_image_id", "")
    ):
        failures.append("running service image differs from compose cleanup image")
    if worker_container is not None and (
        worker_container.get("image_id", "")
        != worker_container.get("compose_image_id", "")
        or worker_container.get("image_id", "")
        != service_container.get("image_id", "")
    ):
        failures.append("stopped worker image differs from the cleanup image")

    health_rows = read_csv(capture_dir / "health.csv")
    if not health_rows:
        failures.append("health evidence is missing")
    required_health_checks = {
        "postgres-readiness",
        "readyz",
        "web-shell",
        "service-info",
        "capacity-guard",
    }
    captured_health_checks = {
        row.get("check", "") for row in health_rows
    }
    if not required_health_checks.issubset(captured_health_checks):
        failures.append("required health checks are missing")
    for row in health_rows:
        if row.get("status", "").lower() != "ok":
            failures.append(f"health check failed: {row.get('check', '<unknown>')}")

    source_path = capture_dir / "runtime-source-references.txt"
    source_references = (
        [
            line
            for line in source_path.read_text(encoding="utf-8").splitlines()
            if line.strip()
        ]
        if source_path.is_file()
        else []
    )
    if source_references:
        failures.append(
            f"{len(source_references)} active runtime source reference(s) remain"
        )
    if not source_path.is_file():
        failures.append("runtime source-reference audit is missing")
    source_scan_rows = read_csv(capture_dir / "source-scan-roots.csv")
    expected_repository_scans = {
        ("active-runtime", "FSTService"),
        ("active-runtime", "FortniteFestival.Core"),
        ("active-runtime", "FortniteFestivalWeb/src"),
        ("active-runtime", "packages"),
        ("active-runtime", "docker-compose.yml"),
        ("active-runtime", "deploy/docker-compose.yml"),
        ("retained-audit", "."),
    }
    actual_source_scans = {
        (row.get("scan", ""), row.get("root", ""))
        for row in source_scan_rows
    }
    repository_scans = {
        item
        for item in actual_source_scans
        if item[0] in {"active-runtime", "retained-audit"}
    }
    if repository_scans != expected_repository_scans:
        failures.append("repository source scan roots are incomplete")
    production_raw_roots = {
        root
        for scan, root in actual_source_scans
        if scan == "production-compose-raw"
    }
    if not production_raw_roots or any(
        not root.startswith(
            "/home/sfenton/Docker/FestivalServiceTracker/"
        )
        for root in production_raw_roots
    ):
        failures.append("production compose raw scan roots are incomplete")
    for required_scan, required_root in [
        (
            "production-compose-rendered",
            "production-compose.sanitized.json",
        ),
        ("production-bind-inventory", "production-compose-binds.tsv"),
    ]:
        if (required_scan, required_root) not in actual_source_scans:
            failures.append(f"{required_scan} scan root is missing")
    for row in source_scan_rows:
        if (
            row.get("status", "") != "ok"
            or row.get("exit_code", "") not in {"0", "1"}
        ):
            failures.append(
                f"source scan failed: "
                f"{row.get('scan')}/{row.get('root')}"
            )

    compose_file_rows = read_csv(
        capture_dir / "production-compose-files.csv"
    )
    if {
        row.get("path", "") for row in compose_file_rows
    } != production_raw_roots:
        failures.append("production compose file inventory differs from scans")
    for row in compose_file_rows:
        if not re.fullmatch(r"[0-9a-f]{64}", row.get("sha256", "")):
            failures.append("production compose file hash is invalid")
    if [
        row.get("ordinal", "") for row in compose_file_rows
    ] != [str(index) for index in range(1, len(compose_file_rows) + 1)]:
        failures.append("production compose file order is not contiguous")
    if not compose_file_rows or any(
        not bool_value(row.get("label_discovered", ""))
        for row in compose_file_rows
    ):
        failures.append("ordered compose files are not label-discovered")

    project_container_rows = read_csv(
        capture_dir / "production-compose-project-containers.csv"
    )
    if not project_container_rows:
        failures.append("production compose project-container evidence is missing")
    config_label_hashes = {
        row.get("config_files_sha256", "")
        for row in project_container_rows
    }
    project_names = {
        row.get("project", "") for row in project_container_rows
    }
    working_dirs = {
        row.get("working_dir", "") for row in project_container_rows
    }
    if (
        len(config_label_hashes) != 1
        or "" in config_label_hashes
        or len(project_names) != 1
        or "" in project_names
        or working_dirs != {
            "/home/sfenton/Docker/FestivalServiceTracker"
        }
    ):
        failures.append("production compose containers disagree on project labels")
    project_ids_by_service = {
        row.get("service", ""): row.get("container_id", "")
        for row in project_container_rows
    }
    project_service_counts = Counter(
        row.get("service", "") for row in project_container_rows
    )
    for service in ["postgres", "fstservice", "festivalweb", "fstworker"]:
        if (
            not project_ids_by_service.get(service)
            or project_service_counts[service] != 1
        ):
            failures.append(
                f"production compose label evidence lacks service {service}"
            )
        elif containers.get(service, {}).get("container_id") != (
            project_ids_by_service[service]
        ):
            failures.append(
                f"captured container ID differs from project service: {service}"
            )
    sanitized_compose_path = (
        capture_dir / "production-compose.sanitized.json"
    )
    if not sanitized_compose_path.is_file():
        failures.append("sanitized rendered production compose is missing")
        sanitized_compose_sha = ""
    else:
        sanitized_compose_sha = sha256_path(sanitized_compose_path)
        try:
            sanitized_compose = json.loads(
                sanitized_compose_path.read_text(encoding="utf-8")
            )
        except (json.JSONDecodeError, OSError) as exc:
            failures.append(f"sanitized production compose is invalid: {exc}")
            sanitized_compose = {}
        if sanitized_compose.get("schemaVersion") != 1:
            failures.append("sanitized production compose schema is invalid")
    compose_binds_path = capture_dir / "production-compose-binds.tsv"
    compose_bind_rows = []
    if not compose_binds_path.is_file():
        failures.append("production compose bind inventory is missing")
    else:
        with compose_binds_path.open(
            newline="",
            encoding="utf-8",
        ) as handle:
            reader = csv.DictReader(handle, delimiter="\t")
            if reader.fieldnames != [
                "service",
                "source",
                "target",
                "read_only",
                "classification",
            ]:
                failures.append("production compose bind header is invalid")
            else:
                compose_bind_rows = list(reader)
    allowed_bind_classes = {
        "secret",
        "data",
        "config-file",
        "config-directory",
        "other",
    }
    for row in compose_bind_rows:
        if row.get("classification") not in allowed_bind_classes:
            failures.append("production compose bind classification is invalid")
        if row.get("classification") == "secret" and (
            row.get("source") != "<redacted-secret-bind>"
            or row.get("target") != "<redacted-secret-target>"
        ):
            failures.append("production secret bind was not redacted")
    bind_config_rows = read_csv(
        capture_dir / "production-bind-config-files.csv"
    )
    bind_config_roots = {
        root
        for scan, root in actual_source_scans
        if scan == "production-bind-config"
    }
    if {
        row.get("path", "") for row in bind_config_rows
    } != bind_config_roots:
        failures.append(
            "production bind-config inventory differs from scanned roots"
        )
    for row in bind_config_rows:
        if not re.fullmatch(r"[0-9a-f]{64}", row.get("sha256", "")):
            failures.append("production bind-config hash is invalid")

    configured_target_rows = read_csv(
        capture_dir / "production-database-target.csv"
    )
    runtime_target_rows = read_csv(
        capture_dir / "production-target-attestation.csv"
    )
    if len(configured_target_rows) != 1 or len(runtime_target_rows) != 1:
        failures.append("production database target evidence is incomplete")
        configured_target = {}
        runtime_target = {}
    else:
        configured_target = configured_target_rows[0]
        runtime_target = runtime_target_rows[0]
    if configured_target:
        if (
            configured_target.get("service") != "postgres"
            or configured_target.get("host") != "postgres"
            or configured_target.get("container_id")
            != project_ids_by_service.get("postgres")
        ):
            failures.append("configured production PostgreSQL target is invalid")
    if runtime_target:
        if (
            runtime_target.get("container_id")
            != project_ids_by_service.get("postgres")
            or runtime_target.get("runtime_database")
            != configured_target.get("database")
            or runtime_target.get("runtime_user")
            != configured_target.get("user")
            or runtime_target.get("configured_host") != "postgres"
            or runtime_target.get("runtime_address") != "local-socket"
            or runtime_target.get("in_recovery") != "false"
            or (
                runtime_target.get("role_superuser") != "true"
                and runtime_target.get("role_bypass_rls") != "true"
            )
            or not re.fullmatch(
                r"\d+",
                runtime_target.get("system_identifier", ""),
            )
        ):
            failures.append("runtime PostgreSQL target attestation is invalid")

    storage_rows = read_csv(capture_dir / "storage.csv")
    storage = storage_rows[0] if len(storage_rows) == 1 else {}
    if len(storage_rows) != 1:
        failures.append("storage evidence must contain exactly one row")
    if not bool_value(storage.get("on_fst_drive", "false")):
        failures.append("Postgres or evidence output is outside the FST drive")

    column_catalog_path = Path(args.column_catalog)
    if not column_catalog_path.is_file():
        failures.append("complete column catalog is missing")
        column_catalog_rows = []
        column_catalog_sha = ""
    else:
        try:
            column_catalog_rows = read_column_catalog(column_catalog_path)
        except ValueError as exc:
            failures.append(str(exc))
            column_catalog_rows = []
        column_catalog_sha = sha256_path(column_catalog_path)
    column_catalog_keys = {
        f"{row.get('schema', '')}.{row.get('name', '')}"
        for row in column_catalog_rows
    }
    if column_catalog_keys != {row.key for row in objects}:
        failures.append("complete column catalog object coverage is inexact")

    catalog_signature_path = Path(args.catalog_signature)
    catalog_metadata_path = Path(args.catalog_metadata)
    catalog_query_path = Path(args.catalog_query)
    catalog_expected_sql = Path(args.catalog_expected_sql)
    catalog_assert_sql = Path(args.catalog_assert_sql)
    catalog_metadata = {}
    if not catalog_signature_path.is_file():
        failures.append("complete catalog signature is missing")
    if not catalog_metadata_path.is_file():
        failures.append("catalog signature metadata is missing")
    else:
        try:
            catalog_metadata = json.loads(
                catalog_metadata_path.read_text(encoding="utf-8")
            )
        except (json.JSONDecodeError, OSError) as exc:
            failures.append(f"catalog signature metadata is invalid: {exc}")
    if catalog_signature_path.is_file():
        if catalog_metadata.get("sha256") != sha256_path(
            catalog_signature_path
        ):
            failures.append("complete catalog signature hash drift")
        catalog_signature_rows = read_csv(catalog_signature_path)
        if catalog_metadata.get("rowCount") != len(catalog_signature_rows):
            failures.append("complete catalog signature row-count drift")
        actual_category_counts = dict(
            sorted(
                Counter(
                    row.get("category", "")
                    for row in catalog_signature_rows
                ).items()
            )
        )
        if catalog_metadata.get("categoryCounts") != actual_category_counts:
            failures.append("complete catalog category-count drift")
    if catalog_query_path.is_file():
        if catalog_metadata.get("querySha256") != sha256_path(
            catalog_query_path
        ):
            failures.append("catalog signature query hash drift")
    else:
        failures.append("catalog signature query is missing")
    if catalog_metadata.get("columnCatalogSha256") != column_catalog_sha:
        failures.append("catalog signature column-catalog binding drift")
    for path, label in [
        (catalog_expected_sql, "catalog expected SQL"),
        (catalog_assert_sql, "catalog assertion SQL"),
    ]:
        if not path.is_file():
            failures.append(f"{label} is missing")

    retained_specs = read_retained_specs(args.retained_spec)
    retained_capture_dir = Path(args.retained_capture_dir)
    retained_capture_hashes = []
    for spec in retained_specs:
        filename = Path(spec["canonical_file"]).with_suffix(".sql").name
        path = retained_capture_dir / filename
        if not path.is_file():
            failures.append(f"retained capture SQL is missing: {filename}")
            continue
        retained_capture_hashes.append(
            {"name": filename, "sha256": sha256_path(path)}
        )
    retained_rows = read_csv(args.retained_metadata)
    retained_by_key = {
        f"{row.get('schema', '')}.{row.get('name', '')}": row
        for row in retained_rows
    }
    if set(retained_by_key) != set(RETAINED_DATA):
        failures.append("retained-data metadata has an inexact table set")
    retained_dir = Path(args.retained_dir)
    for spec in retained_specs:
        key = f"{spec['schema']}.{spec['name']}"
        definition = RETAINED_DATA[key]
        metadata = retained_by_key.get(key, {})
        if metadata.get("family", "") != definition["family"]:
            failures.append(f"retained-data family drift: {key}")
        for field in ["expected_rows", "row_count"]:
            if metadata.get(field, "") != str(definition["expected_rows"]):
                failures.append(f"retained-data {field} drift: {key}")
        if (
            metadata.get("canonical_file", "")
            != definition["canonical_file"]
        ):
            failures.append(f"retained-data filename drift: {key}")
        canonical_path = retained_dir / definition["canonical_file"]
        if not canonical_path.is_file():
            failures.append(f"retained-data payload is missing: {key}")
        else:
            actual_hash = sha256_path(canonical_path)
            if metadata.get("sha256", "") != actual_hash:
                failures.append(f"retained-data payload hash drift: {key}")
            if metadata.get("payload_bytes", "") != str(
                canonical_path.stat().st_size
            ):
                failures.append(f"retained-data payload size drift: {key}")
            bound_column_count = sum(
                1
                for row in column_catalog_rows
                if row.get("schema") == spec["schema"]
                and row.get("name") == spec["name"]
                and not bool_value(row.get("is_dropped", ""))
            )
            if metadata.get("column_count", "") != str(bound_column_count):
                failures.append(f"retained-data column count drift: {key}")
            if metadata.get("column_catalog_sha256", "") != column_catalog_sha:
                failures.append(f"retained-data column binding drift: {key}")
    retained_expected_sql = Path(args.retained_expected_sql)
    retained_assert_sql = Path(args.retained_assert_sql)
    for path, label in [
        (retained_expected_sql, "retained expected SQL"),
        (retained_assert_sql, "retained assertion SQL"),
    ]:
        if not path.is_file():
            failures.append(f"{label} is missing")

    fingerprint_spec_path = Path(args.fingerprint_spec)
    with fingerprint_spec_path.open(newline="", encoding="utf-8") as handle:
        fingerprint_spec_rows = list(csv.DictReader(handle, delimiter="\t"))
    if sha256_path(fingerprint_spec_path) != args.fingerprint_spec_sha256:
        failures.append("fingerprint specification hash is inconsistent")
    specification_gate_names = [
        row.get("name", "")
        for row in fingerprint_spec_rows
        if bool_value(row.get("gate", "false"))
    ]
    if specification_gate_names != PUBLIC_FINGERPRINT_NAMES:
        failures.append(
            "fingerprint specification must contain the exact ordered "
            "13-surface public parity suite"
        )
    fingerprint_rows = read_csv(capture_dir / "fingerprints.csv")
    fingerprints_by_name = {
        row.get("name", ""): row for row in fingerprint_rows
    }
    if set(fingerprints_by_name) != {
        row.get("name", "") for row in fingerprint_spec_rows
    }:
        failures.append("captured fingerprint set differs from the specification")
    for specification in fingerprint_spec_rows:
        captured = fingerprints_by_name.get(specification.get("name", ""))
        if captured is None:
            continue
        for key in ["url", "format", "expected_status", "gate"]:
            if captured.get(key, "") != specification.get(key, ""):
                failures.append(
                    f"fingerprint specification drift: "
                    f"{specification.get('name')}.{key}"
                )
        resolved_url = captured.get("resolved_url", "")
        if not re.fullmatch(r"http://(?:127\.0\.0\.1|localhost):\d+/[^,\r\n]*", resolved_url):
            failures.append(
                f"fingerprint resolved URL is invalid: "
                f"{specification.get('name')}"
            )
        if "{" in resolved_url or "}" in resolved_url:
            failures.append(
                f"fingerprint URL placeholder was not resolved: "
                f"{specification.get('name')}"
            )
    gated_fingerprints = [
        row for row in fingerprint_rows if bool_value(row.get("gate", "false"))
    ]
    if [
        row.get("name", "") for row in gated_fingerprints
    ] != PUBLIC_FINGERPRINT_NAMES:
        failures.append("captured public/API fingerprint suite is not exact")
    for row in gated_fingerprints:
        if row.get("http_status", "") != row.get("expected_status", ""):
            failures.append(f"fingerprint HTTP status failed: {row.get('name')}")
        if not re.fullmatch(r"[0-9a-f]{64}", row.get("sha256", "")):
            failures.append(f"fingerprint hash is invalid: {row.get('name')}")

    rollback_rows = read_csv(capture_dir / "rollback-hashes.csv")
    if not rollback_rows:
        failures.append("rollback hash inventory is missing")
    for row in rollback_rows:
        if not bool_value(row.get("verified", "false")):
            failures.append(f"rollback hash failed: {row.get('name')}")
    generated_families = {
        row.get("family")
        for row in rollback_rows
        if row.get("kind") == "generated-family"
    }
    if generated_families != set(FAMILY_ORDER):
        failures.append("generated rollback DDL does not cover all four families")

    parity = {}
    parity_path = Path(args.parity_evidence) if args.parity_evidence else None
    if parity_path is None or not parity_path.is_file():
        failures.append("accepted scrape-1278 parity evidence is required")
        parity_sha = ""
    else:
        parity_sha = sha256_path(parity_path)
        try:
            parity = json.loads(parity_path.read_text(encoding="utf-8"))
        except (json.JSONDecodeError, OSError) as exc:
            failures.append(f"parity evidence is not valid JSON: {exc}")
            parity = {}
        expected_parity = {
            "schemaVersion": 1,
            "decision": "accepted",
            "scrapeId": CLEANUP_SCRAPE_ID,
            "published": True,
            "unfrozen": True,
            "exactPublicFingerprintParity": True,
            "fingerprintCount": len(PUBLIC_FINGERPRINT_NAMES),
        }
        for key, expected_value in expected_parity.items():
            if parity.get(key) != expected_value:
                failures.append(
                    f"parity evidence {key}={parity.get(key)!r}, "
                    f"expected {expected_value!r}"
                )
        if parity.get("cleanupImageId") != service_container.get("image_id", ""):
            failures.append("parity evidence cleanup image does not match service")
        if not re.fullmatch(
            r"sha256:[0-9a-f]{64}",
            str(parity.get("cleanupImageId", "")),
        ):
            failures.append("parity evidence cleanup image ID is invalid")
        if (
            parity.get("fingerprintSpecSha256")
            != args.fingerprint_spec_sha256
        ):
            failures.append(
                "parity evidence fingerprint specification does not match"
            )
        if not re.fullmatch(
            r"\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d+)?Z",
            str(parity.get("acceptedAtUtc", "")),
        ):
            failures.append("parity evidence acceptance time is missing/invalid")
        if not str(parity.get("evidenceRoot", "")).startswith(
            "/mnt/docker-storage/Docker/FestivalServiceTracker/"
            "fst-data/evidence/"
        ):
            failures.append("parity evidence root is outside FST evidence")

    drop_sql = Path(args.drop_sql)
    rollback_all = Path(args.rollback_all)
    if not drop_sql.is_file():
        failures.append("generated drop SQL is missing")
        drop_sha = ""
    else:
        drop_sha = sha256_path(drop_sql)
        failures.extend(
            validate_drop_sql_text(
                objects, drop_sql.read_text(encoding="utf-8")
            )
        )
    if not rollback_all.is_file():
        failures.append("generated combined rollback SQL is missing")
        rollback_sha = ""
    else:
        rollback_sha = sha256_path(rollback_all)

    manifest = {
        "schemaVersion": 1,
        "cleanupScrapeId": CLEANUP_SCRAPE_ID,
        "ready": not failures,
        "failures": sorted(set(failures)),
        "familyCounts": FAMILY_COUNTS,
        "objectCount": len(objects),
        "expectedObjectsSha256": sha256_path(args.objects),
        "expectedObjects": [asdict(row) for row in objects],
        "relations": stable_rows(stable_relations),
        "partitionChildren": stable_rows(partition_rows),
        "incomingInheritance": stable_rows(incoming_rows),
        "ownedObjects": stable_rows(owned_rows),
        "toolingHashes": stable_rows(tooling_rows),
        "unexpectedRelations": stable_rows(unexpected_rows),
        "externalDependencies": stable_rows(dependency_rows),
        "preflight": {
            key: preflight.get(key, "")
            for key in sorted(set(required_preflight) | {
                "worker_status",
                "current_publication_id",
                "published_at",
                "publication_updated_at",
                "current_publication_published_at",
                "cleanup_scrape_completed_at",
                "database_name",
                "server_version_num",
            })
        },
        "containers": stable_rows(container_rows),
        "health": stable_rows(health_rows),
        "storageIdentity": {
            key: storage.get(key, "")
            for key in [
                "pgdata_source",
                "filesystem_source",
                "filesystem_type",
                "evidence_root",
                "on_fst_drive",
            ]
        },
        "runtimeSourceReferences": source_references,
        "sourceScanRoots": stable_rows(source_scan_rows),
        "productionComposeOwnership": {
            "files": [
                {str(key): str(value) for key, value in row.items()}
                for row in compose_file_rows
            ],
            "projectContainers": stable_rows(project_container_rows),
            "sanitizedConfigSha256": sanitized_compose_sha,
            "binds": stable_rows(compose_bind_rows),
            "bindConfigFiles": stable_rows(bind_config_rows),
        },
        "productionDatabaseTarget": {
            "configured": configured_target,
            "runtime": runtime_target,
        },
        "columnCatalogSha256": column_catalog_sha,
        "columnCatalogRowCount": len(column_catalog_rows),
        "catalogSignature": catalog_metadata,
        "catalogMetadataSha256": (
            sha256_path(catalog_metadata_path)
            if catalog_metadata_path.is_file()
            else ""
        ),
        "catalogExpectedSqlSha256": (
            sha256_path(catalog_expected_sql)
            if catalog_expected_sql.is_file()
            else ""
        ),
        "catalogAssertSqlSha256": (
            sha256_path(catalog_assert_sql)
            if catalog_assert_sql.is_file()
            else ""
        ),
        "retainedDataSpecSha256": sha256_path(args.retained_spec),
        "retainedCaptureSql": stable_rows(retained_capture_hashes),
        "retainedData": stable_rows(retained_rows),
        "retainedExpectedSqlSha256": (
            sha256_path(retained_expected_sql)
            if retained_expected_sql.is_file()
            else ""
        ),
        "retainedAssertSqlSha256": (
            sha256_path(retained_assert_sql)
            if retained_assert_sql.is_file()
            else ""
        ),
        "fingerprints": stable_rows(gated_fingerprints),
        "fingerprintSpecSha256": args.fingerprint_spec_sha256,
        "parityEvidence": {
            "sha256": parity_sha,
            "schemaVersion": parity.get("schemaVersion"),
            "decision": parity.get("decision"),
            "scrapeId": parity.get("scrapeId"),
            "cleanupImageId": parity.get("cleanupImageId"),
            "fingerprintCount": parity.get("fingerprintCount"),
            "fingerprintSpecSha256": parity.get(
                "fingerprintSpecSha256"
            ),
            "exactPublicFingerprintParity": parity.get(
                "exactPublicFingerprintParity"
            ),
        },
        "dropSqlSha256": drop_sha,
        "rollbackAllSha256": rollback_sha,
        "rollbackHashes": stable_rows(rollback_rows),
    }
    output_dir = Path(args.output_dir)
    output_dir.mkdir(parents=True, exist_ok=True)
    manifest_path = output_dir / "manifest.json"
    manifest_text = json.dumps(
        manifest, sort_keys=True, separators=(",", ":"), ensure_ascii=False
    ) + "\n"
    manifest_path.write_text(manifest_text, encoding="utf-8")
    manifest_sha = hashlib.sha256(manifest_text.encode("utf-8")).hexdigest()
    (output_dir / "manifest.sha256").write_text(
        f"{manifest_sha}  manifest.json\n", encoding="utf-8"
    )
    report_lines = [
        f"ready={str(manifest['ready']).lower()}",
        f"manifest_sha256={manifest_sha}",
        f"object_count={len(objects)}",
    ]
    report_lines.extend(f"failure={failure}" for failure in manifest["failures"])
    (output_dir / "gate-report.txt").write_text(
        "\n".join(report_lines) + "\n", encoding="utf-8"
    )
    print(manifest_sha)


def validate_absent_inventory(path, objects, label, failures):
    rows = read_csv(path)
    if len(rows) != len(objects):
        failures.append(f"{label} relation inventory is incomplete")
        return
    by_key = {
        f"{row.get('schema', '')}.{row.get('name', '')}": row for row in rows
    }
    for expected in objects:
        row = by_key.get(expected.key)
        if row is None:
            failures.append(f"{label} inventory lacks {expected.key}")
        elif row.get("actual_relkind", ""):
            failures.append(f"{label} recreated or retained {expected.key}")


def validate_post(args):
    objects = read_objects(args.objects)
    manifest = json.loads(Path(args.before_manifest).read_text(encoding="utf-8"))
    post_dir = Path(args.post_dir)
    failures = []
    scratch_rows = read_csv(
        post_dir.parent / "pre-destructive-roundtrip" / "status.csv"
    )
    if len(scratch_rows) != 1 or scratch_rows[0].get("status") != "passed":
        failures.append("pre-destructive scratch round-trip proof is missing")
    process_rows = read_csv(post_dir / "drop-process-status.csv")
    if len(process_rows) != 1:
        failures.append("atomic drop process status is missing")
    else:
        process = process_rows[0]
        if process.get("process_status") == "0":
            if process.get("reconciliation_state") != "not-required":
                failures.append("successful drop has invalid reconciliation")
        elif process.get("reconciliation_state") != "committed":
            failures.append("failed drop process was not safely reconciled")
    control_rows = read_csv(post_dir / "drop-process-control.csv")
    if len(control_rows) != 1:
        failures.append("destructive process control record is missing")
    else:
        control = control_rows[0]
        if control.get("state") not in {"completed", "terminated"}:
            failures.append("destructive process/backend is not confirmed gone")
        if not control.get("application_name"):
            failures.append("destructive application name is missing")
        for key in ["container_psql_pid", "backend_pid"]:
            if not re.fullmatch(r"\d+", control.get(key, "")):
                failures.append(f"destructive control {key} is invalid")
        if not re.fullmatch(r"\d+", control.get("local_start_ticks", "")):
            failures.append("destructive local process start time is invalid")
        if not re.fullmatch(
            r"[0-9a-f]{64}",
            control.get("local_cmd_sha256", ""),
        ):
            failures.append("destructive local command identity is invalid")
        if control.get("local_wait_completed") not in {"true", "false"}:
            failures.append("destructive local wait state is invalid")
        if control.get("connect_released") != "true":
            failures.append("destructive connection barrier was not released")
        if control.get("sql_released") != "true":
            failures.append("destructive SQL barrier was not released")
    for filename, label in [
        ("relations.csv", "post-drop"),
        ("startup-relations.csv", "startup schema check"),
        ("rehearsal-relations.csv", "rollback rehearsal"),
    ]:
        validate_absent_inventory(post_dir / filename, objects, label, failures)

    status_requirements = [
        ("startup-check.csv", "startup schema check"),
        ("rollback-rehearsal.csv", "rollback rehearsal"),
    ]
    for filename, label in status_requirements:
        rows = read_csv(post_dir / filename)
        if len(rows) != 1 or rows[0].get("status") != "0":
            failures.append(f"{label} did not succeed")

    preflight_rows = read_csv(post_dir / "preflight.csv")
    preflight = preflight_rows[0] if len(preflight_rows) == 1 else {}
    before = manifest.get("preflight", {})
    for key in [
        "published_scrape_id",
        "published_at",
        "publication_updated_at",
        "current_publication_id",
        "current_publication_scrape_id",
        "current_publication_status",
        "current_publication_published_at",
        "cleanup_scrape_status",
        "cleanup_scrape_completed",
        "cleanup_scrape_completed_at",
    ]:
        if preflight.get(key, "") != before.get(key, ""):
            failures.append(f"publication state changed: {key}")
    for key, expected in {
        "public_reads_frozen": "false",
        "working_publication_id": "",
        "active_scrape_count": "0",
        "worker_current_operation": "false",
        "ungranted_lock_count": "0",
        "long_query_count": "0",
        "target_query_count": "0",
        "active_vacuum_count": "0",
        "active_index_build_count": "0",
        "active_rewrite_count": "0",
        "critical_phase_failure_count": "0",
        "ddl_guard_available": "true",
        "sequence_guard_available": "true",
    }.items():
        if preflight.get(key, "").lower() != expected:
            failures.append(f"post-action gate failed: {key}")
    if preflight.get("worker_status", "").lower() not in {"absent", "offline"}:
        failures.append("post-action worker ledger is not absent/offline")

    post_target_rows = read_csv(
        post_dir / "production-target-attestation.csv"
    )
    before_target = manifest.get(
        "productionDatabaseTarget",
        {},
    ).get("runtime", {})
    if len(post_target_rows) != 1 or post_target_rows[0] != before_target:
        failures.append("post-action production database target changed")

    after_fingerprints = {
        row.get("name"): row
        for row in read_csv(post_dir / "fingerprints.csv")
        if bool_value(row.get("gate", "false"))
    }
    before_fingerprint_names = {
        row.get("name") for row in manifest.get("fingerprints", [])
    }
    if set(after_fingerprints) != before_fingerprint_names:
        failures.append("post-action fingerprint set changed")
    for before_row in manifest.get("fingerprints", []):
        after_row = after_fingerprints.get(before_row.get("name"))
        if after_row is None:
            failures.append(
                f"post-action fingerprint is missing: {before_row.get('name')}"
            )
            continue
        if after_row.get("http_status") != before_row.get("http_status"):
            failures.append(
                f"post-action HTTP status changed: {before_row.get('name')}"
            )
        if after_row.get("resolved_url") != before_row.get("resolved_url"):
            failures.append(
                f"post-action fingerprint sample changed: "
                f"{before_row.get('name')}"
            )
        if after_row.get("sha256") != before_row.get("sha256"):
            failures.append(
                f"post-action fingerprint changed: {before_row.get('name')}"
            )

    health_rows = read_csv(post_dir / "health.csv")
    if not health_rows:
        failures.append("post-action health evidence is missing")
    if not {
        "postgres-readiness",
        "readyz",
        "web-shell",
        "service-info",
        "capacity-guard",
    }.issubset({row.get("check", "") for row in health_rows}):
        failures.append("post-action required health checks are missing")
    for row in health_rows:
        if row.get("status", "").lower() != "ok":
            failures.append(f"post-action health failed: {row.get('check')}")
    containers = {
        row.get("service"): row for row in read_csv(post_dir / "containers.csv")
    }
    before_containers = {
        row.get("service"): row for row in manifest.get("containers", [])
    }
    for service in ["postgres", "fstservice", "festivalweb"]:
        row = containers.get(service, {})
        if (
            row.get("state", "").lower() != "running"
            or row.get("health", "").lower() != "healthy"
        ):
            failures.append(f"post-action container is unhealthy: {service}")
        before_row = before_containers.get(service, {})
        if row.get("container_id", "") != before_row.get("container_id", ""):
            failures.append(f"post-action container ID changed: {service}")
        if row.get("image_id", "") != before_row.get("image_id", ""):
            failures.append(f"post-action image changed: {service}")
    if containers.get("fstworker", {}).get("state", "").lower() not in {
        "created",
        "dead",
        "exited",
        "stopped",
    }:
        failures.append("fstworker did not remain stopped")
    elif containers.get("fstworker", {}).get(
        "restart_policy", ""
    ).lower() != "no":
        failures.append("fstworker restart policy changed")
    elif containers.get("fstworker", {}).get(
        "image_id", ""
    ) != before_containers.get("fstworker", {}).get("image_id", ""):
        failures.append("fstworker image changed")
    elif containers.get("fstworker", {}).get(
        "container_id", ""
    ) != before_containers.get("fstworker", {}).get("container_id", ""):
        failures.append("fstworker container ID changed")

    storage_rows = read_csv(post_dir / "storage.csv")
    if len(storage_rows) != 1:
        failures.append("post-action storage evidence is missing")
    else:
        storage = storage_rows[0]
        if not bool_value(storage.get("on_fst_drive", "false")):
            failures.append("post-action storage left the FST drive")
        if storage.get("target_total_bytes", "") != "0":
            failures.append("post-action target bytes are not zero")

    result = {
        "schemaVersion": 1,
        "success": not failures,
        "failures": sorted(set(failures)),
        "publishedScrapeId": preflight.get("published_scrape_id"),
        "objectCountAbsent": len(objects) if not failures else None,
        "storage": storage_rows,
    }
    output = Path(args.output)
    output.write_text(
        json.dumps(result, sort_keys=True, indent=2) + "\n",
        encoding="utf-8",
    )
    if failures:
        for failure in result["failures"]:
            print(f"ERROR: {failure}", file=sys.stderr)
        return 3
    return 0


def normalize_leaderboard(args):
    value = json.loads(Path(args.input).read_text(encoding="utf-8"))
    if not isinstance(value, dict):
        raise ValueError("leaderboard response must be a JSON object")
    entry_fields = [
        "accountId",
        "score",
        "rank",
        "localRank",
        "apiRank",
        "rankSource",
        "accuracy",
        "isFullCombo",
        "stars",
        "difficulty",
        "season",
        "percentile",
        "endTime",
        "source",
    ]
    entries = value.get("entries", [])
    if not isinstance(entries, list):
        raise ValueError("leaderboard entries must be a JSON array")
    normalized = {
        "songId": value.get("songId"),
        "instrument": value.get("instrument"),
        "showLeaderboardEntryTotals": value.get(
            "showLeaderboardEntryTotals"
        ),
        "count": value.get("count"),
        "totalEntries": value.get("totalEntries"),
        "localEntries": value.get("localEntries"),
        "entries": [
            {field: entry.get(field) for field in entry_fields}
            for entry in entries
            if isinstance(entry, dict)
        ],
    }
    Path(args.output).write_text(
        json.dumps(
            normalized,
            sort_keys=True,
            separators=(",", ":"),
            ensure_ascii=False,
        )
        + "\n",
        encoding="utf-8",
    )


def _workbook_rows(data):
    namespace = {
        "m": "http://schemas.openxmlformats.org/spreadsheetml/2006/main"
    }
    with zipfile.ZipFile(io.BytesIO(data)) as workbook:
        try:
            shared_root = ElementTree.fromstring(
                workbook.read("xl/sharedStrings.xml")
            )
        except KeyError:
            shared_strings = []
        else:
            shared_strings = [
                "".join(
                    text.text or ""
                    for text in item.findall(".//m:t", namespace)
                )
                for item in shared_root.findall("m:si", namespace)
            ]

        result = {}
        sheet_names = sorted(
            name
            for name in workbook.namelist()
            if re.fullmatch(r"xl/worksheets/sheet\d+\.xml", name)
        )
        for name in sheet_names:
            root = ElementTree.fromstring(workbook.read(name))
            rows = []
            for row in root.findall(".//m:sheetData/m:row", namespace):
                values = []
                for cell in row.findall("m:c", namespace):
                    kind = cell.get("t")
                    value = cell.find("m:v", namespace)
                    if kind == "inlineStr":
                        text = "".join(
                            item.text or ""
                            for item in cell.findall(".//m:t", namespace)
                        )
                    elif value is None:
                        text = ""
                    elif kind == "s":
                        text = shared_strings[int(value.text)]
                    else:
                        text = value.text or ""
                    values.append([cell.get("r"), text])
                if re.search(
                    r"generated(?: at)?",
                    " ".join(text for _cell, text in values),
                    re.IGNORECASE,
                ):
                    continue
                rows.append(values)
            result[name] = rows
        return result


def normalize_player_export(args):
    source = Path(args.input)
    if zipfile.is_zipfile(source):
        normalized = {}
        with zipfile.ZipFile(source) as archive:
            workbook_names = sorted(
                name
                for name in archive.namelist()
                if name.lower().endswith(".xlsx")
            )
            if not workbook_names:
                raise ValueError("player export contains no XLSX workbooks")
            for name in workbook_names:
                stable_name = re.sub(
                    r"-\d{8}-\d{6}(?=\.xlsx$)",
                    "-TIMESTAMP",
                    Path(name).name,
                )
                normalized[stable_name] = _workbook_rows(
                    archive.read(name)
                )
    else:
        normalized = json.loads(source.read_text(encoding="utf-8"))
    Path(args.output).write_text(
        json.dumps(
            normalized,
            sort_keys=True,
            separators=(",", ":"),
            ensure_ascii=False,
        )
        + "\n",
        encoding="utf-8",
    )


def normalize_solo_export(args):
    value = json.loads(Path(args.input).read_text(encoding="utf-8"))
    if not isinstance(value, dict):
        raise ValueError("normalized player export must be a JSON object")
    normalized = {}
    retained_all_sheets = {
        "xl/worksheets/sheet1.xml",
        "xl/worksheets/sheet2.xml",
        "xl/worksheets/sheet3.xml",
    }
    for workbook, sheets in value.items():
        if "-bands-" in workbook:
            continue
        if "-all-" in workbook and isinstance(sheets, dict):
            normalized[workbook] = {
                name: rows
                for name, rows in sheets.items()
                if name in retained_all_sheets
            }
        else:
            normalized[workbook] = sheets
    Path(args.output).write_text(
        json.dumps(
            normalized,
            sort_keys=True,
            separators=(",", ":"),
            ensure_ascii=False,
        )
        + "\n",
        encoding="utf-8",
    )


def extract_public_identifier(args):
    value = json.loads(Path(args.input).read_text(encoding="utf-8"))
    entries = value.get("entries") if isinstance(value, dict) else None
    if not isinstance(entries, list):
        raise ValueError("public ranking response has no entries array")
    field_names = (
        ("accountId", "AccountId", "account_id")
        if args.command == "extract-account-id"
        else ("teamKey", "TeamKey", "team_key")
    )
    identifier = ""
    for entry in entries:
        if not isinstance(entry, dict):
            continue
        identifier = next(
            (
                str(entry.get(field, "")).strip()
                for field in field_names
                if entry.get(field)
            ),
            "",
        )
        if identifier:
            break
    if args.command == "extract-account-id":
        valid = re.fullmatch(r"[0-9a-fA-F]{32}", identifier)
    else:
        valid = re.fullmatch(
            r"[0-9a-fA-F]{32}(?::[0-9a-fA-F]{32})+",
            identifier,
        )
    if not valid:
        raise ValueError(f"response lacks a safe {args.command} value")
    print(identifier)


def canonicalize_json(args):
    value = json.loads(Path(args.input).read_text(encoding="utf-8"))
    Path(args.output).write_text(
        json.dumps(value, sort_keys=True, separators=(",", ":")) + "\n",
        encoding="utf-8",
    )


def sanitize_compose_config(args):
    value = json.load(sys.stdin)
    if not isinstance(value, dict):
        raise ValueError("rendered compose config must be a JSON object")
    target_pattern = re.compile(TARGET_REFERENCE_PATTERN, re.IGNORECASE)
    sensitive_path = re.compile(
        r"(^|[/_.-])(secrets?|password|passwd|tokens?|credentials?|private|"
        r"certificates?|certs?|api[-_]?keys?|ssh[-_]?keys?)([/_.-]|$)",
        re.IGNORECASE,
    )
    config_suffixes = {
        ".json",
        ".yaml",
        ".yml",
        ".toml",
        ".conf",
        ".cfg",
        ".ini",
        ".xml",
        ".properties",
    }

    def sequence(value):
        if value is None:
            return []
        if isinstance(value, list):
            return value
        return [value]

    def environment_projection(environment):
        names = []
        references = []
        if isinstance(environment, dict):
            for name, raw_value in environment.items():
                names.append(str(name))
                references.extend(
                    match.group(0)
                    for match in target_pattern.finditer(str(raw_value or ""))
                )
        else:
            for item in sequence(environment):
                text = str(item)
                name, _, raw_value = text.partition("=")
                if name.strip():
                    names.append(name.strip())
                references.extend(
                    match.group(0)
                    for match in target_pattern.finditer(raw_value)
                )
        return {
            "names": sorted(set(names)),
            "retiredValueReferences": sorted(set(references)),
        }

    def environment_map(environment):
        if isinstance(environment, dict):
            return {
                str(name): "" if raw_value is None else str(raw_value)
                for name, raw_value in environment.items()
            }
        result = {}
        for item in sequence(environment):
            name, separator, raw_value = str(item).partition("=")
            if separator:
                result[name.strip()] = raw_value
        return result

    def command_projection(command):
        flags = []
        references = []
        for token in sequence(command):
            text = str(token)
            if text.startswith("--"):
                flags.append(text.split("=", 1)[0])
            references.extend(
                match.group(0) for match in target_pattern.finditer(text)
            )
        return {
            "flags": sorted(set(flags)),
            "retiredReferences": sorted(set(references)),
        }

    def classify_bind(source, target):
        lowered = f"{source} {target}".lower()
        if (
            "/run/secrets" in lowered
            or sensitive_path.search(source)
            or sensitive_path.search(target)
            or Path(source).name == ".env"
        ):
            return "secret"
        if (
            "/var/lib/postgresql/data" in target
            or "docker.sock" in lowered
            or "/fst-data" in source
            or "postgres-data" in source.lower()
        ):
            return "data"
        suffix = Path(source).suffix.lower()
        if suffix in config_suffixes:
            return "config-file"
        if (
            "/config" in target.lower()
            or target.lower().startswith("/etc/")
            or "config" in Path(source).name.lower()
        ):
            return "config-directory"
        return "other"

    services_projection = {}
    bind_rows = []
    services = value.get("services", {})
    if not isinstance(services, dict):
        raise ValueError("rendered compose services must be an object")
    for service_name in sorted(services):
        service = services[service_name] or {}
        if not isinstance(service, dict):
            raise ValueError(f"invalid rendered compose service: {service_name}")
        volumes = []
        for volume in sequence(service.get("volumes")):
            if isinstance(volume, str):
                parts = volume.split(":")
                source = parts[0] if len(parts) > 1 else ""
                target = parts[1] if len(parts) > 1 else parts[0]
                read_only = len(parts) > 2 and "ro" in parts[2].split(",")
                volume_type = "bind" if source.startswith("/") else "volume"
            elif isinstance(volume, dict):
                source = str(volume.get("source") or "")
                target = str(volume.get("target") or "")
                read_only = bool(volume.get("read_only", False))
                volume_type = str(volume.get("type") or "")
            else:
                raise ValueError(
                    f"invalid volume entry for service {service_name}"
                )
            if any(character in source + target for character in "\t\r\n"):
                raise ValueError("compose bind path contains control characters")
            classification = (
                classify_bind(source, target)
                if volume_type == "bind"
                else "not-bind"
            )
            displayed_source = (
                "<redacted-secret-bind>"
                if classification == "secret"
                else source
            )
            displayed_target = (
                "<redacted-secret-target>"
                if classification == "secret"
                else target
            )
            volumes.append(
                {
                    "type": volume_type,
                    "source": displayed_source,
                    "target": displayed_target,
                    "readOnly": read_only,
                }
            )
            if volume_type == "bind":
                bind_rows.append(
                    {
                        "service": service_name,
                        "source": displayed_source,
                        "target": displayed_target,
                        "read_only": str(read_only).lower(),
                        "classification": classification,
                    }
                )

        configs = []
        for config in sequence(service.get("configs")):
            if isinstance(config, str):
                configs.append({"source": config, "target": ""})
            elif isinstance(config, dict):
                configs.append(
                    {
                        "source": str(config.get("source") or ""),
                        "target": str(config.get("target") or ""),
                    }
                )
        secrets = []
        for secret in sequence(service.get("secrets")):
            if isinstance(secret, str):
                secrets.append(secret)
            elif isinstance(secret, dict):
                secrets.append(str(secret.get("source") or ""))
        labels = service.get("labels", {})
        label_names = (
            sorted(str(name) for name in labels)
            if isinstance(labels, dict)
            else sorted(
                str(item).split("=", 1)[0]
                for item in sequence(labels)
            )
        )
        services_projection[service_name] = {
            "imagePresent": bool(service.get("image")),
            "imageReferenceSha256": hashlib.sha256(
                str(service.get("image") or "").encode("utf-8")
            ).hexdigest(),
            "imageRetiredReferences": sorted(
                {
                    match.group(0)
                    for match in target_pattern.finditer(
                        str(service.get("image") or "")
                    )
                }
            ),
            "command": command_projection(service.get("command")),
            "entrypoint": command_projection(service.get("entrypoint")),
            "environment": environment_projection(
                service.get("environment")
            ),
            "volumes": volumes,
            "configs": sorted(
                configs,
                key=lambda row: (row["source"], row["target"]),
            ),
            "secretNames": sorted(set(secrets)),
            "labelNames": label_names,
            "profiles": sorted(str(item) for item in sequence(
                service.get("profiles")
            )),
        }

    postgres_environment = environment_map(
        (services.get("postgres") or {}).get("environment")
    )
    configured_database = postgres_environment.get("POSTGRES_DB", "").strip()
    configured_user = postgres_environment.get("POSTGRES_USER", "").strip()
    if not configured_database or not configured_user:
        raise ValueError("postgres service lacks POSTGRES_DB/POSTGRES_USER")

    connection_targets = {}
    for service_name in ["fstservice", "fstworker"]:
        service_environment = environment_map(
            (services.get(service_name) or {}).get("environment")
        )
        connection = service_environment.get(
            "ConnectionStrings__PostgreSQL",
            "",
        )
        values = {}
        for item in connection.split(";"):
            key, separator, raw_value = item.partition("=")
            if separator:
                values[key.strip().casefold()] = raw_value.strip()
        target = {
            "host": values.get("host", ""),
            "port": values.get("port", "5432"),
            "database": values.get("database", ""),
            "user": (
                values.get("username")
                or values.get("user id")
                or values.get("user")
                or ""
            ),
            "passwordConfigured": any(
                name in values
                for name in {"password", "pwd"}
            ) or service_environment.get(
                "ConnectionStrings__PostgreSQLPasswordConfigured",
                "",
            ).casefold() == "true",
        }
        if (
            target["host"] != "postgres"
            or target["database"] != configured_database
            or target["user"] != configured_user
            or not target["passwordConfigured"]
        ):
            raise ValueError(
                f"{service_name} PostgreSQL target is inconsistent"
            )
        connection_targets[service_name] = target
    if connection_targets["fstservice"] != connection_targets["fstworker"]:
        raise ValueError("service and worker PostgreSQL targets differ")

    projection = {
        "schemaVersion": 1,
        "projectName": str(value.get("name") or ""),
        "services": services_projection,
        "configNames": sorted(
            str(name) for name in (value.get("configs") or {})
        ),
        "secretNames": sorted(
            str(name) for name in (value.get("secrets") or {})
        ),
        "volumeNames": sorted(
            str(name) for name in (value.get("volumes") or {})
        ),
        "networkNames": sorted(
            str(name) for name in (value.get("networks") or {})
        ),
        "databaseTarget": {
            "service": "postgres",
            "host": "postgres",
            "port": connection_targets["fstservice"]["port"],
            "database": configured_database,
            "user": configured_user,
            "passwordConfigured": True,
            "consumers": ["fstservice", "fstworker"],
        },
    }
    Path(args.output).write_text(
        json.dumps(projection, sort_keys=True, indent=2) + "\n",
        encoding="utf-8",
    )
    with Path(args.binds_output).open(
        "w",
        newline="",
        encoding="utf-8",
    ) as handle:
        fields = [
            "service",
            "source",
            "target",
            "read_only",
            "classification",
        ]
        writer = csv.DictWriter(
            handle,
            fieldnames=fields,
            delimiter="\t",
            lineterminator="\n",
        )
        writer.writeheader()
        writer.writerows(
            sorted(
                bind_rows,
                key=lambda row: (
                    row["service"],
                    row["source"],
                    row["target"],
                ),
            )
        )


def validate_drop_command(args):
    objects = read_objects(args.objects)
    errors = validate_drop_sql_text(
        objects, Path(args.sql).read_text(encoding="utf-8")
    )
    if errors:
        for error in errors:
            print(f"ERROR: {error}", file=sys.stderr)
        return 3
    print(f"validated {len(objects)} exact cleanup objects")
    return 0


def manifest_ready(args):
    manifest = json.loads(Path(args.manifest).read_text(encoding="utf-8"))
    if manifest.get("ready") is True:
        return 0
    for failure in manifest.get("failures", []):
        print(f"ERROR: {failure}", file=sys.stderr)
    return 3


def build_parser():
    parser = argparse.ArgumentParser()
    subparsers = parser.add_subparsers(dest="command", required=True)

    render_expected = subparsers.add_parser("render-expected-sql")
    render_expected.add_argument("--objects", required=True)

    render_drop = subparsers.add_parser("render-drop-sql")
    render_drop.add_argument("--objects", required=True)
    render_drop.add_argument("--catalog-expected-sql", required=True)
    render_drop.add_argument("--catalog-assert-sql", required=True)
    render_drop.add_argument("--retained-expected-sql", required=True)
    render_drop.add_argument("--retained-assert-sql", required=True)
    render_drop.add_argument("--output", required=True)

    rehearsal = subparsers.add_parser("render-rehearsal-check-sql")
    rehearsal.add_argument("--objects", required=True)

    validate_drop = subparsers.add_parser("validate-drop-sql")
    validate_drop.add_argument("--objects", required=True)
    validate_drop.add_argument("--sql", required=True)

    manifest = subparsers.add_parser("build-manifest")
    manifest.add_argument("--objects", required=True)
    manifest.add_argument("--capture-dir", required=True)
    manifest.add_argument("--drop-sql", required=True)
    manifest.add_argument("--rollback-all", required=True)
    manifest.add_argument("--column-catalog", required=True)
    manifest.add_argument("--catalog-signature", required=True)
    manifest.add_argument("--catalog-metadata", required=True)
    manifest.add_argument("--catalog-query", required=True)
    manifest.add_argument("--catalog-expected-sql", required=True)
    manifest.add_argument("--catalog-assert-sql", required=True)
    manifest.add_argument("--retained-spec", required=True)
    manifest.add_argument("--retained-capture-dir", required=True)
    manifest.add_argument("--retained-dir", required=True)
    manifest.add_argument("--retained-metadata", required=True)
    manifest.add_argument("--retained-expected-sql", required=True)
    manifest.add_argument("--retained-assert-sql", required=True)
    manifest.add_argument("--parity-evidence")
    manifest.add_argument("--fingerprint-spec", required=True)
    manifest.add_argument("--fingerprint-spec-sha256", required=True)
    manifest.add_argument("--expected-owner", required=True)
    manifest.add_argument("--output-dir", required=True)

    ready = subparsers.add_parser("manifest-ready")
    ready.add_argument("--manifest", required=True)

    post = subparsers.add_parser("validate-post")
    post.add_argument("--objects", required=True)
    post.add_argument("--before-manifest", required=True)
    post.add_argument("--post-dir", required=True)
    post.add_argument("--output", required=True)

    canonical = subparsers.add_parser("canonicalize-json")
    canonical.add_argument("--input", required=True)
    canonical.add_argument("--output", required=True)

    leaderboard = subparsers.add_parser("normalize-leaderboard")
    leaderboard.add_argument("--input", required=True)
    leaderboard.add_argument("--output", required=True)

    player_export = subparsers.add_parser("normalize-player-export")
    player_export.add_argument("--input", required=True)
    player_export.add_argument("--output", required=True)

    solo_export = subparsers.add_parser("normalize-solo-export")
    solo_export.add_argument("--input", required=True)
    solo_export.add_argument("--output", required=True)

    account_id = subparsers.add_parser("extract-account-id")
    account_id.add_argument("--input", required=True)

    team_key = subparsers.add_parser("extract-team-key")
    team_key.add_argument("--input", required=True)

    retained = subparsers.add_parser("prepare-retained-data")
    retained.add_argument("--spec", required=True)
    retained.add_argument("--column-catalog", required=True)
    retained.add_argument("--raw-dir", required=True)
    retained.add_argument("--canonical-dir", required=True)
    retained.add_argument("--metadata-output", required=True)
    retained.add_argument("--expected-sql-output", required=True)
    retained.add_argument("--assert-sql-output", required=True)
    retained.add_argument("--logical-rollback", required=True)
    retained.add_argument("--band-rollback", required=True)

    columns = subparsers.add_parser("prepare-column-catalog")
    columns.add_argument("--objects", required=True)
    columns.add_argument("--input", required=True)
    columns.add_argument("--output", required=True)

    retained_capture = subparsers.add_parser(
        "render-retained-capture-sql"
    )
    retained_capture.add_argument("--spec", required=True)
    retained_capture.add_argument("--column-catalog", required=True)
    retained_capture.add_argument("--output-dir", required=True)

    catalog = subparsers.add_parser("prepare-catalog-signature")
    catalog.add_argument("--input", required=True)
    catalog.add_argument("--query", required=True)
    catalog.add_argument("--column-catalog", required=True)
    catalog.add_argument("--output", required=True)
    catalog.add_argument("--metadata-output", required=True)
    catalog.add_argument("--expected-sql-output", required=True)
    catalog.add_argument("--assert-sql-output", required=True)

    compose = subparsers.add_parser("sanitize-compose-config")
    compose.add_argument("--output", required=True)
    compose.add_argument("--binds-output", required=True)
    return parser


def main():
    parser = build_parser()
    args = parser.parse_args()
    try:
        if args.command == "render-expected-sql":
            print(render_expected_sql(read_objects(args.objects)), end="")
            return 0
        if args.command == "render-drop-sql":
            Path(args.output).write_text(
                render_drop_sql(
                    read_objects(args.objects),
                    Path(args.catalog_expected_sql).read_text(
                        encoding="utf-8"
                    ),
                    Path(args.catalog_assert_sql).read_text(
                        encoding="utf-8"
                    ),
                    Path(args.retained_expected_sql).read_text(
                        encoding="utf-8"
                    ),
                    Path(args.retained_assert_sql).read_text(
                        encoding="utf-8"
                    ),
                ),
                encoding="utf-8",
            )
            return 0
        if args.command == "render-rehearsal-check-sql":
            print(render_rehearsal_check_sql(read_objects(args.objects)), end="")
            return 0
        if args.command == "validate-drop-sql":
            return validate_drop_command(args)
        if args.command == "build-manifest":
            build_manifest(args)
            return 0
        if args.command == "manifest-ready":
            return manifest_ready(args)
        if args.command == "validate-post":
            return validate_post(args)
        if args.command == "canonicalize-json":
            canonicalize_json(args)
            return 0
        if args.command == "normalize-leaderboard":
            normalize_leaderboard(args)
            return 0
        if args.command == "normalize-player-export":
            normalize_player_export(args)
            return 0
        if args.command == "normalize-solo-export":
            normalize_solo_export(args)
            return 0
        if args.command in {"extract-account-id", "extract-team-key"}:
            extract_public_identifier(args)
            return 0
        if args.command == "prepare-retained-data":
            prepare_retained_data(args)
            return 0
        if args.command == "prepare-column-catalog":
            prepare_column_catalog(args)
            return 0
        if args.command == "render-retained-capture-sql":
            render_retained_capture_sql(args)
            return 0
        if args.command == "prepare-catalog-signature":
            prepare_catalog_signature(args)
            return 0
        if args.command == "sanitize-compose-config":
            sanitize_compose_config(args)
            return 0
    except (
        ElementTree.ParseError,
        IndexError,
        KeyError,
        OSError,
        ValueError,
        json.JSONDecodeError,
        zipfile.BadZipFile,
    ) as exc:
        print(f"ERROR: {exc}", file=sys.stderr)
        return 2
    return 2


if __name__ == "__main__":
    raise SystemExit(main())
