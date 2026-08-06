#!/usr/bin/env python3

import argparse
import copy
import csv
import fcntl
import hashlib
import http.client
import ipaddress
import io
import json
import os
import re
import socket
import stat
import sys
import zipfile
from collections import Counter, defaultdict
from dataclasses import asdict, dataclass
from pathlib import Path
from urllib.parse import quote, urlparse
from xml.etree import ElementTree


CLEANUP_SCRAPE_ID = 1278
PUBLICATION_LOCK_KEY = 5067481511116519500
DDL_MAINTENANCE_LOCK_KEY = 5067481511116519501
SEQUENCE_MAINTENANCE_LOCK_KEY = 5067481511116519502
ROUNDTRIP_CATALOG_RULE = "partition-primary-key-noinherit-v1"
ROUNDTRIP_CATALOG_SENTINEL = "partition-attach-reconstructed"
CAPACITY_POLICY = {
    "estimatedFullScrapeGrowthBytes": 60392999803,
    "expectedFullScrapesPerDay": 2,
    "minimumHeadroomDays": 7,
    "minimumHeadroomBytesOverride": 0,
    "transientBuildBytes": 0,
    "requiredScratchBytes": 0,
    "expectedReclaimBytes": 0,
}
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


def effective_publication_gate_sql():
    return """
DO $effective_publication_membership$
BEGIN
    IF EXISTS (
        SELECT 1
        FROM retired_cleanup_expected expected
        JOIN pg_catalog.pg_publication_tables publication_table
          ON publication_table.schemaname = expected.schema_name
         AND publication_table.tablename = expected.object_name
    ) THEN
        RAISE EXCEPTION
            'Effective publication membership exists for a cleanup target';
    END IF;
END
$effective_publication_membership$;
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
            "DO $sequence_dependency_lock$",
            "DECLARE",
            "    locked_dependency record;",
            "BEGIN",
            "    FOR locked_dependency IN",
            "        SELECT dependency.objid,",
            "               dependency.refobjid,",
            "               dependency.refobjsubid,",
            "               dependency.deptype",
            "        FROM pg_catalog.pg_depend dependency",
            "        WHERE dependency.classid = 'pg_class'::regclass",
            "          AND dependency.objsubid = 0",
            "          AND dependency.refclassid = 'pg_class'::regclass",
            "          AND dependency.refobjsubid > 0",
            "          AND dependency.deptype IN ('a', 'i')",
            "          AND (",
            "              dependency.objid IN (",
            "                  SELECT sequence_row.oid",
            "                  FROM retired_cleanup_expected",
            "                       sequence_expected",
            "                  JOIN pg_catalog.pg_namespace sequence_schema",
            "                    ON sequence_schema.nspname =",
            "                       sequence_expected.schema_name",
            "                  JOIN pg_catalog.pg_class sequence_row",
            "                    ON sequence_row.relnamespace =",
            "                       sequence_schema.oid",
            "                   AND sequence_row.relname =",
            "                       sequence_expected.object_name",
            "                   AND sequence_row.relkind = 'S'",
            "                  WHERE sequence_expected.object_type =",
            "                        'sequence'",
            "              )",
            "              OR dependency.refobjid IN (",
            "                  SELECT owner_table.oid",
            "                  FROM retired_cleanup_expected owner_expected",
            "                  JOIN pg_catalog.pg_namespace owner_schema",
            "                    ON owner_schema.nspname =",
            "                       owner_expected.schema_name",
            "                  JOIN pg_catalog.pg_class owner_table",
            "                    ON owner_table.relnamespace =",
            "                       owner_schema.oid",
            "                   AND owner_table.relname =",
            "                       owner_expected.object_name",
            "                  WHERE owner_expected.object_type IN",
            "                        ('table', 'partitioned_table')",
            "              )",
            "          )",
            "        ORDER BY dependency.objid,",
            "                 dependency.refobjid,",
            "                 dependency.refobjsubid",
            "        FOR SHARE OF dependency",
            "    LOOP",
            "        NULL;",
            "    END LOOP;",
            "END",
            "$sequence_dependency_lock$;",
            "",
            "DO $owned_sequence_set$",
            "BEGIN",
            "    IF EXISTS (",
            "        WITH actual_owned_sequences AS (",
            "            SELECT sequence_schema.nspname AS sequence_schema,",
            "                   sequence_row.relname AS sequence_name,",
            "                   owner_schema.nspname AS target_schema,",
            "                   owner_table.relname AS target_name,",
            "                   owner_attribute.attname AS target_column,",
            "                   dependency.deptype::text AS dependency_type",
            "            FROM retired_cleanup_expected owner_expected",
            "            JOIN pg_catalog.pg_namespace owner_schema",
            "              ON owner_schema.nspname =",
            "                 owner_expected.schema_name",
            "            JOIN pg_catalog.pg_class owner_table",
            "              ON owner_table.relnamespace = owner_schema.oid",
            "             AND owner_table.relname =",
            "                 owner_expected.object_name",
            "            JOIN pg_catalog.pg_depend dependency",
            "              ON dependency.refclassid =",
            "                 'pg_class'::regclass",
            "             AND dependency.refobjid = owner_table.oid",
            "             AND dependency.refobjsubid > 0",
            "             AND dependency.classid =",
            "                 'pg_class'::regclass",
            "             AND dependency.objsubid = 0",
            "             AND dependency.deptype IN ('a', 'i')",
            "            JOIN pg_catalog.pg_class sequence_row",
            "              ON sequence_row.oid = dependency.objid",
            "             AND sequence_row.relkind = 'S'",
            "            JOIN pg_catalog.pg_namespace sequence_schema",
            "              ON sequence_schema.oid =",
            "                 sequence_row.relnamespace",
            "            JOIN pg_catalog.pg_attribute owner_attribute",
            "              ON owner_attribute.attrelid = owner_table.oid",
            "             AND owner_attribute.attnum =",
            "                 dependency.refobjsubid",
            "             AND NOT owner_attribute.attisdropped",
            "            WHERE owner_expected.object_type IN",
            "                  ('table', 'partitioned_table')",
            "        ),",
            "        expected_owned_sequences AS (",
            "            SELECT sequence_expected.schema_name",
            "                       AS sequence_schema,",
            "                   sequence_expected.object_name",
            "                       AS sequence_name,",
            "                   sequence_expected.parent_schema",
            "                       AS target_schema,",
            "                   sequence_expected.parent_name",
            "                       AS target_name,",
            "                   sequence_expected.owner_column",
            "                       AS target_column,",
            "                   'a'::text AS dependency_type",
            "            FROM retired_cleanup_expected sequence_expected",
            "            WHERE sequence_expected.object_type = 'sequence'",
            "        )",
            "        SELECT 1",
            "        FROM (",
            "            (SELECT * FROM actual_owned_sequences",
            "             EXCEPT ALL",
            "             SELECT * FROM expected_owned_sequences)",
            "            UNION ALL",
            "            (SELECT * FROM expected_owned_sequences",
            "             EXCEPT ALL",
            "             SELECT * FROM actual_owned_sequences)",
            "        ) mismatch",
            "    ) THEN",
            "        RAISE EXCEPTION",
            "            'Owned sequence set differs from allowlist';",
            "    END IF;",
            "END",
            "$owned_sequence_set$;",
            "",
            "DO $sequence_guard$",
            "BEGIN",
            f"    IF NOT pg_catalog.pg_try_advisory_xact_lock({SEQUENCE_MAINTENANCE_LOCK_KEY}) THEN",
            "        RAISE EXCEPTION 'The retired sequence guard is busy';",
            "    END IF;",
            "END",
            "$sequence_guard$;",
            "",
            "DO $sequence_state_lock$",
            "DECLARE",
            "    locked_catalog_row record;",
            "BEGIN",
            "    PERFORM last_value, is_called",
            '    FROM "public"."player_score_observations_id_seq";',
            "",
            "    FOR locked_catalog_row IN",
            "        SELECT relation.oid",
            "        FROM pg_catalog.pg_class relation",
            "        JOIN pg_catalog.pg_namespace sequence_schema",
            "          ON sequence_schema.oid = relation.relnamespace",
            "        WHERE sequence_schema.nspname = 'public'",
            "          AND relation.relname =",
            "              'player_score_observations_id_seq'",
            "          AND relation.relkind = 'S'",
            "        FOR SHARE OF relation",
            "    LOOP",
            "        NULL;",
            "    END LOOP;",
            "",
            "    FOR locked_catalog_row IN",
            "        SELECT sequence_catalog.seqrelid",
            "        FROM pg_catalog.pg_sequence sequence_catalog",
            "        WHERE sequence_catalog.seqrelid =",
            "              'public.player_score_observations_id_seq'::regclass",
            "        FOR SHARE OF sequence_catalog",
            "    LOOP",
            "        NULL;",
            "    END LOOP;",
            "END",
            "$sequence_state_lock$;",
            "",
            effective_publication_gate_sql(),
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
            effective_publication_gate_sql(),
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
    owned_sequence_gate = sql.find("DO $owned_sequence_set$")
    sequence_dependency_lock = sql.find("DO $sequence_dependency_lock$")
    sequence_guard = sql.find("DO $sequence_guard$")
    sequence_state_lock = sql.find("DO $sequence_state_lock$")
    publication_gate = sql.find("DO $effective_publication_membership$")
    final_publication_gate = sql.rfind(
        "DO $effective_publication_membership$"
    )
    if not (
        last_lock
        < sequence_dependency_lock
        < owned_sequence_gate
        < sequence_guard
        < sequence_state_lock
        < publication_gate
        < catalog_gate
        < object_gate
        < first_drop
    ):
        errors.append(
            "sequence ownership/state locks and complete catalog gates must "
            "run after all target locks and before object checks/drops"
        )
    if not (
        object_gate
        < final_catalog_gate
        < final_publication_gate
        < first_drop
    ):
        errors.append(
            "final catalog and effective publication rechecks must be the "
            "last database gates before drops"
        )
    if not (
        final_publication_gate
        < default_drop
        < owned_none
        < sequence_drop
        < owner_table_drop
    ):
        errors.append(
            "owned sequence changes are allowed only in the exact "
            "post-validation destructive order"
        )
    if sql.count("Complete cleanup catalog signature drifted") != 2:
        errors.append("both complete catalog signature gates are required")
    if sql.count("Incoming inheritance edges differ from allowlist") != 1:
        errors.append("complete incoming inheritance gate is missing")
    if sql.count("Owned sequence set differs from allowlist") != 1:
        errors.append("exact inverse owned-sequence gate is missing")
    if sql.count(
        "Effective publication membership exists for a cleanup target"
    ) != 2:
        errors.append("both effective publication gates are required")
    if str(SEQUENCE_MAINTENANCE_LOCK_KEY) not in sql:
        errors.append("retired sequence advisory guard is missing")
    if sql.count("DO $sequence_guard$") != 1:
        errors.append("exactly one retired sequence advisory guard is required")
    if str(DDL_MAINTENANCE_LOCK_KEY) not in sql:
        errors.append("schema-DDL advisory guard is missing")
    if (
        'ALTER SEQUENCE "public"."player_score_observations_id_seq"\n'
        '    OWNED BY "public"."player_score_observations"."id";'
    ) in sql:
        errors.append("pre-validation sequence ownership mutation is forbidden")
    if "FOR SHARE OF dependency" not in sql:
        errors.append("sequence ownership dependency rows are not locked")
    if sql.count("DO $sequence_dependency_lock$") != 1:
        errors.append("exactly one sequence dependency lock gate is required")
    if sql.count("DO $sequence_state_lock$") != 1:
        errors.append("exactly one sequence state lock gate is required")
    if (
        "FOR SHARE OF relation" not in sql
        or "FOR SHARE OF sequence_catalog" not in sql
    ):
        errors.append("sequence state/option catalog rows are not locked")
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


def nullable_integer(value):
    if value == "":
        return None
    if not re.fullmatch(r"-?\d+", value):
        raise ValueError("value is neither empty nor an integer")
    return int(value)


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
            nullable_integer(row["statistics_target"])
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
        "statisticsTarget": nullable_integer(row["statistics_target"]),
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


def render_rehearsal_capture_sql(args):
    query_text = Path(args.query).read_text(encoding="utf-8").strip()
    if query_text.endswith(";"):
        query_text = query_text[:-1].rstrip()
    specs = read_retained_specs(args.spec)
    columns = read_column_catalog(args.column_catalog)
    lines = [
        r"\o",
        r"\echo FST_REHEARSAL_CATALOG_BEGIN",
        "COPY (",
        query_text,
        ") TO STDOUT WITH (FORMAT CSV, HEADER TRUE);",
        r"\echo FST_REHEARSAL_CATALOG_END",
    ]
    for spec in specs:
        key = f"{spec['schema']}.{spec['name']}"
        bound_columns = retained_columns(columns, key)
        column_sql = ", ".join(
            f'"{row["column_name"]}"' for row in bound_columns
        )
        order_sql = ", ".join(
            f'"{name}"' for name in RETAINED_DATA[key]["key"]
        )
        marker_name = RETAINED_DATA[key]["canonical_file"]
        relation = f'"{spec["schema"]}"."{spec["name"]}"'
        lines.extend([
            f"\\echo FST_REHEARSAL_RETAINED_BEGIN:{marker_name}",
            "COPY (",
            f"    SELECT {column_sql}",
            f"    FROM {relation}",
            f"    ORDER BY {order_sql}",
            ") TO STDOUT WITH (FORMAT CSV, HEADER TRUE);",
            f"\\echo FST_REHEARSAL_RETAINED_END:{marker_name}",
        ])
    Path(args.output).write_text(
        "\n".join(lines) + "\n",
        encoding="utf-8",
    )


def parse_rehearsal_capture(args):
    lines = Path(args.input).read_text(encoding="utf-8").splitlines()
    output_dir = Path(args.output_dir)
    retained_dir = output_dir / "retained-data"
    output_dir.mkdir(parents=True, exist_ok=True)
    retained_dir.mkdir(parents=True, exist_ok=True)

    def extract(begin, end):
        try:
            start = lines.index(begin)
            finish = lines.index(end, start + 1)
        except ValueError as exc:
            raise ValueError(
                f"rehearsal capture marker is missing: {begin}/{end}"
            ) from exc
        if finish <= start + 1:
            raise ValueError(f"rehearsal capture is empty: {begin}")
        return lines[start + 1:finish]

    catalog_lines = extract(
        "FST_REHEARSAL_CATALOG_BEGIN",
        "FST_REHEARSAL_CATALOG_END",
    )
    catalog_path = output_dir / "actual-catalog-signature.csv"
    catalog_path.write_text(
        "\n".join(catalog_lines) + "\n",
        encoding="utf-8",
    )
    retained_files = []
    for filename in [
        definition["canonical_file"]
        for definition in RETAINED_DATA.values()
    ]:
        retained_lines = extract(
            f"FST_REHEARSAL_RETAINED_BEGIN:{filename}",
            f"FST_REHEARSAL_RETAINED_END:{filename}",
        )
        path = retained_dir / filename
        path.write_text(
            "\n".join(retained_lines) + "\n",
            encoding="utf-8",
        )
        retained_files.append({
            "name": filename,
            "sha256": sha256_path(path),
        })
    if "FST_REHEARSAL_ROLLBACK_COMPLETE" not in lines:
        raise ValueError("rehearsal did not record explicit rollback completion")
    result = {
        "schemaVersion": 1,
        "explicitRollbackComplete": True,
        "catalogSignatureSha256": sha256_path(catalog_path),
        "retainedFiles": sorted(
            retained_files,
            key=lambda row: row["name"],
        ),
    }
    Path(args.output).write_text(
        json.dumps(result, sort_keys=True, indent=2) + "\n",
        encoding="utf-8",
    )


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

        data_path = (
            Path(args.logical_data)
            if definition["family"] == "logical-shadow"
            else Path(args.band_data)
        )
        marker = f"-- Retained payload: {key}"
        if data_path.exists() and data_path.stat().st_size:
            raise ValueError(f"rollback payload already exists: {key}")
        with data_path.open("w", encoding="utf-8", newline="") as handle:
            handle.write(marker + "\n")
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


def validate_retained_recapture(args):
    specs = read_retained_specs(args.spec)
    column_catalog = read_column_catalog(args.column_catalog)
    expected_rows = read_csv(args.expected_metadata)
    expected_by_key = {
        f"{row.get('schema', '')}.{row.get('name', '')}": row
        for row in expected_rows
    }
    output_dir = Path(args.output_dir)
    output_dir.mkdir(parents=True, exist_ok=True)
    metadata = []
    fields = [
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
    for spec in specs:
        key = f"{spec['schema']}.{spec['name']}"
        definition = RETAINED_DATA[key]
        bound_columns = retained_columns(column_catalog, key)
        field_names = [row["column_name"] for row in bound_columns]
        raw_path = Path(args.raw_dir) / definition["canonical_file"]
        with raw_path.open(newline="", encoding="utf-8") as handle:
            reader = csv.DictReader(handle)
            if reader.fieldnames != field_names:
                raise ValueError(f"{key} recapture header is inexact")
            rows = _validate_retained_rows(
                key,
                list(reader),
                bound_columns,
            )
        canonical_path = output_dir / definition["canonical_file"]
        with canonical_path.open("w", newline="", encoding="utf-8") as handle:
            writer = csv.DictWriter(
                handle,
                fieldnames=field_names,
                lineterminator="\n",
            )
            writer.writeheader()
            writer.writerows(rows)
        row = {
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
        metadata.append(row)
        if row != expected_by_key.get(key):
            raise ValueError(f"retained data drifted after scratch: {key}")
    with Path(args.metadata_output).open(
        "w",
        newline="",
        encoding="utf-8",
    ) as handle:
        writer = csv.DictWriter(
            handle,
            fieldnames=fields,
            lineterminator="\n",
        )
        writer.writeheader()
        writer.writerows(metadata)


def _write_catalog_signature(path, rows):
    with Path(path).open("w", newline="", encoding="utf-8") as handle:
        writer = csv.DictWriter(
            handle,
            fieldnames=["category", "object_identity", "detail"],
            lineterminator="\n",
        )
        writer.writeheader()
        writer.writerows(rows)


def _roundtrip_constraint_identities(rows, objects):
    partition_children = {
        row.key
        for row in objects
        if (
            row.object_type == "table"
            and row.parent_name
            and not row.owner_column
        )
    }
    by_table = {}
    for row in rows:
        if row["category"] != "constraint":
            continue
        table_identity, separator, _constraint_name = (
            row["object_identity"].rpartition(".")
        )
        if not separator or table_identity not in partition_children:
            continue
        detail = json.loads(row["detail"])
        if (
            detail.get("type") == "p"
            and detail.get("parentConstraint")
        ):
            if detail.get("noInherit") is not False:
                raise ValueError(
                    "live partition primary-key constraint has unexpected "
                    f"noInherit state: {row['object_identity']}"
                )
            if table_identity in by_table:
                raise ValueError(
                    "multiple partition primary-key constraints require "
                    f"roundtrip normalization: {table_identity}"
                )
            by_table[table_identity] = row["object_identity"]
    if set(by_table) != partition_children:
        missing = sorted(partition_children - set(by_table))
        raise ValueError(
            "roundtrip catalog normalization lacks partition primary keys: "
            + ", ".join(missing)
        )
    return sorted(by_table.values())


def _normalize_roundtrip_catalog(rows, constraint_identities):
    allowed = set(constraint_identities)
    normalized = []
    for row in rows:
        detail = json.loads(row["detail"])
        if row["object_identity"] in allowed:
            if (
                row["category"] != "constraint"
                or detail.get("type") != "p"
                or not detail.get("parentConstraint")
                or not isinstance(detail.get("noInherit"), bool)
            ):
                raise ValueError(
                    "roundtrip normalization target has unexpected shape: "
                    f"{row['object_identity']}"
                )
            detail["noInherit"] = ROUNDTRIP_CATALOG_SENTINEL
        normalized.append({
            "category": row["category"],
            "object_identity": row["object_identity"],
            "detail": json.dumps(
                detail,
                sort_keys=True,
                separators=(",", ":"),
                ensure_ascii=False,
            ),
        })
    normalized.sort(
        key=lambda row: (
            row["category"],
            row["object_identity"],
            row["detail"],
        )
    )
    return normalized


def _render_catalog_expected_sql(table_name, comment, csv_text):
    return "\n".join(
        [
            comment,
            f"CREATE TEMP TABLE {table_name} (",
            "    category text NOT NULL,",
            "    object_identity text NOT NULL,",
            "    detail jsonb NOT NULL",
            ") ON COMMIT PRESERVE ROWS;",
            _render_copy_payload(
                f"pg_temp.{table_name}",
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


def _render_catalog_assert_sql(
    query_text,
    expected_table,
    dollar_tag,
    error_message,
    constraint_identities=None,
):
    indented_query = "\n".join(
        "            " + line for line in query_text.splitlines()
    )
    lines = [
        f"DO ${dollar_tag}$",
        "BEGIN",
        "    IF EXISTS (",
        "        WITH current_signature_raw AS (",
        indented_query,
        "        ),",
    ]
    if constraint_identities:
        identity_sql = ",\n".join(
            "                    " + sql_literal(identity)
            for identity in constraint_identities
        )
        lines.extend([
            "        current_signature AS (",
            "            SELECT category,",
            "                   object_identity,",
            "                   CASE",
            "                       WHEN category = 'constraint'",
            "                        AND object_identity IN (",
            identity_sql,
            "                        )",
            "                        AND detail->>'type' = 'p'",
            "                        AND COALESCE(",
            "                            detail->>'parentConstraint',",
            "                            '') <> ''",
            "                        AND jsonb_typeof(",
            "                            detail->'noInherit') = 'boolean'",
            "                       THEN jsonb_set(",
            "                           detail,",
            "                           '{noInherit}',",
            "                           to_jsonb(",
            f"                               {sql_literal(ROUNDTRIP_CATALOG_SENTINEL)}::text),",
            "                           false)",
            "                       ELSE detail",
            "                   END AS detail",
            "            FROM current_signature_raw",
            "        )",
        ])
    else:
        lines.extend([
            "        current_signature AS (",
            "            SELECT category, object_identity, detail",
            "            FROM current_signature_raw",
            "        )",
        ])
    lines.extend([
        "        SELECT 1",
        "        FROM (",
        "            (",
        "                SELECT category, object_identity, detail",
        "                FROM current_signature",
        "                EXCEPT ALL",
        "                SELECT category, object_identity, detail",
        f"                FROM pg_temp.{expected_table}",
        "            )",
        "            UNION ALL",
        "            (",
        "                SELECT category, object_identity, detail",
        f"                FROM pg_temp.{expected_table}",
        "                EXCEPT ALL",
        "                SELECT category, object_identity, detail",
        "                FROM current_signature",
        "            )",
        "        ) mismatch",
        "    ) THEN",
        "        RAISE EXCEPTION",
        f"            {sql_literal(error_message)};",
        "    END IF;",
        "END",
        f"${dollar_tag}$;",
        "",
    ])
    return "\n".join(lines)


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

    roundtrip_paths = [
        args.objects,
        args.rollback_canonical_output,
        args.rollback_canonical_metadata_output,
        args.rollback_canonical_expected_sql_output,
        args.rollback_canonical_assert_sql_output,
    ]
    if any(roundtrip_paths) and not all(roundtrip_paths):
        raise ValueError(
            "roundtrip catalog outputs require objects, signature, metadata, "
            "expected SQL, and assertion SQL"
        )
    if all(roundtrip_paths):
        objects = read_objects(args.objects)
        constraint_identities = _roundtrip_constraint_identities(
            canonical_rows,
            objects,
        )
        roundtrip_rows = _normalize_roundtrip_catalog(
            canonical_rows,
            constraint_identities,
        )
        _write_catalog_signature(
            args.rollback_canonical_output,
            roundtrip_rows,
        )
        roundtrip_csv = Path(
            args.rollback_canonical_output
        ).read_text(encoding="utf-8")
        Path(args.rollback_canonical_expected_sql_output).write_text(
            _render_catalog_expected_sql(
                "retired_cleanup_expected_rollback_catalog",
                "-- Manifest-bound rollback-canonical catalog signature.",
                roundtrip_csv,
            ),
            encoding="utf-8",
        )
        Path(args.rollback_canonical_assert_sql_output).write_text(
            _render_catalog_assert_sql(
                query_text,
                "retired_cleanup_expected_rollback_catalog",
                "rollback_catalog_signature",
                "Rollback-canonical cleanup catalog signature drifted",
                constraint_identities,
            ),
            encoding="utf-8",
        )
        roundtrip_metadata = {
            "schemaVersion": 1,
            "rule": ROUNDTRIP_CATALOG_RULE,
            "sentinel": ROUNDTRIP_CATALOG_SENTINEL,
            "rowCount": len(roundtrip_rows),
            "sha256": sha256_path(args.rollback_canonical_output),
            "exactSignatureSha256": sha256_path(output),
            "querySha256": sha256_path(args.query),
            "columnCatalogSha256": sha256_path(args.column_catalog),
            "normalizedConstraintCount": len(constraint_identities),
            "normalizedConstraintIdentities": constraint_identities,
        }
        Path(args.rollback_canonical_metadata_output).write_text(
            json.dumps(
                roundtrip_metadata,
                sort_keys=True,
                separators=(",", ":"),
            )
            + "\n",
            encoding="utf-8",
        )


def _read_catalog_signature(path):
    with Path(path).open(newline="", encoding="utf-8") as handle:
        reader = csv.DictReader(handle)
        if reader.fieldnames != ["category", "object_identity", "detail"]:
            raise ValueError(f"catalog signature header is invalid: {path}")
        rows = []
        for row in reader:
            detail = json.loads(row["detail"])
            rows.append({
                "category": row["category"],
                "object_identity": row["object_identity"],
                "detail": json.dumps(
                    detail,
                    sort_keys=True,
                    separators=(",", ":"),
                    ensure_ascii=False,
                ),
            })
    rows.sort(
        key=lambda row: (
            row["category"],
            row["object_identity"],
            row["detail"],
        )
    )
    return rows


def _json_field_differences(expected, actual, path=""):
    if isinstance(expected, dict) and isinstance(actual, dict):
        differences = []
        for key in sorted(set(expected) | set(actual)):
            child_path = f"{path}.{key}" if path else key
            if key not in expected:
                differences.append({
                    "field": child_path,
                    "expected": None,
                    "actual": actual[key],
                    "kind": "added",
                })
            elif key not in actual:
                differences.append({
                    "field": child_path,
                    "expected": expected[key],
                    "actual": None,
                    "kind": "removed",
                })
            else:
                differences.extend(
                    _json_field_differences(
                        expected[key],
                        actual[key],
                        child_path,
                    )
                )
        return differences
    if expected != actual:
        return [{
            "field": path,
            "expected": expected,
            "actual": actual,
            "kind": "changed",
        }]
    return []


def diff_catalog_signatures(args):
    objects = read_objects(args.objects)
    expected_rows = _read_catalog_signature(args.expected)
    actual_rows = _read_catalog_signature(args.actual)
    constraint_identities = _roundtrip_constraint_identities(
        expected_rows,
        objects,
    )
    expected_counter = Counter(
        (
            row["category"],
            row["object_identity"],
            row["detail"],
        )
        for row in expected_rows
    )
    actual_counter = Counter(
        (
            row["category"],
            row["object_identity"],
            row["detail"],
        )
        for row in actual_rows
    )
    missing_counter = expected_counter - actual_counter
    extra_counter = actual_counter - expected_counter

    expected_by_identity = defaultdict(list)
    actual_by_identity = defaultdict(list)
    for row in expected_rows:
        expected_by_identity[
            (row["category"], row["object_identity"])
        ].append(json.loads(row["detail"]))
    for row in actual_rows:
        actual_by_identity[
            (row["category"], row["object_identity"])
        ].append(json.loads(row["detail"]))

    allowed = set(constraint_identities)
    field_differences = []
    for identity in sorted(
        set(expected_by_identity) | set(actual_by_identity)
    ):
        expected_details = expected_by_identity.get(identity, [])
        actual_details = actual_by_identity.get(identity, [])
        expected_detail_counter = Counter(
            json.dumps(detail, sort_keys=True, separators=(",", ":"))
            for detail in expected_details
        )
        actual_detail_counter = Counter(
            json.dumps(detail, sort_keys=True, separators=(",", ":"))
            for detail in actual_details
        )
        remaining_expected = list(
            (expected_detail_counter - actual_detail_counter).elements()
        )
        remaining_actual = list(
            (actual_detail_counter - expected_detail_counter).elements()
        )
        if len(remaining_expected) == 1 and len(remaining_actual) == 1:
            expected_detail = json.loads(remaining_expected[0])
            actual_detail = json.loads(remaining_actual[0])
            for difference in _json_field_differences(
                expected_detail,
                actual_detail,
            ):
                classification = "fatal"
                if (
                    identity[0] == "constraint"
                    and identity[1] in allowed
                    and difference["field"] == "noInherit"
                    and difference["expected"] is False
                    and difference["actual"] is True
                ):
                    classification = "allowed-roundtrip-normalization"
                field_differences.append({
                    "category": identity[0],
                    "objectIdentity": identity[1],
                    **difference,
                    "classification": classification,
                })

    expected_roundtrip = _normalize_roundtrip_catalog(
        expected_rows,
        constraint_identities,
    )
    actual_roundtrip = _normalize_roundtrip_catalog(
        actual_rows,
        constraint_identities,
    )
    expected_roundtrip_counter = Counter(
        (
            row["category"],
            row["object_identity"],
            row["detail"],
        )
        for row in expected_roundtrip
    )
    actual_roundtrip_counter = Counter(
        (
            row["category"],
            row["object_identity"],
            row["detail"],
        )
        for row in actual_roundtrip
    )
    canonical_match = (
        expected_roundtrip_counter == actual_roundtrip_counter
    )
    exact_match = expected_counter == actual_counter

    def expanded_rows(counter):
        rows = []
        for (category, object_identity, detail), count in sorted(
            counter.items()
        ):
            for _ in range(count):
                rows.append({
                    "category": category,
                    "objectIdentity": object_identity,
                    "detail": json.loads(detail),
                })
        return rows

    result = {
        "schemaVersion": 1,
        "normalizationRule": ROUNDTRIP_CATALOG_RULE,
        "normalizationSentinel": ROUNDTRIP_CATALOG_SENTINEL,
        "exactMatch": exact_match,
        "rollbackCanonicalMatch": canonical_match,
        "expectedRowCount": len(expected_rows),
        "actualRowCount": len(actual_rows),
        "exactMissingCount": sum(missing_counter.values()),
        "exactExtraCount": sum(extra_counter.values()),
        "normalizedConstraintCount": len(constraint_identities),
        "normalizedConstraintIdentities": constraint_identities,
        "fieldDifferences": field_differences,
        "exactMissingRows": expanded_rows(missing_counter),
        "exactExtraRows": expanded_rows(extra_counter),
    }
    Path(args.output_json).write_text(
        json.dumps(result, sort_keys=True, indent=2) + "\n",
        encoding="utf-8",
    )
    with Path(args.output_csv).open(
        "w",
        newline="",
        encoding="utf-8",
    ) as handle:
        fields = [
            "category",
            "objectIdentity",
            "field",
            "kind",
            "expected",
            "actual",
            "classification",
        ]
        writer = csv.DictWriter(
            handle,
            fieldnames=fields,
            lineterminator="\n",
        )
        writer.writeheader()
        for row in field_differences:
            writer.writerow({
                **row,
                "expected": json.dumps(
                    row["expected"],
                    sort_keys=True,
                    separators=(",", ":"),
                ),
                "actual": json.dumps(
                    row["actual"],
                    sort_keys=True,
                    separators=(",", ":"),
                ),
            })
    report_lines = [
        "# Catalog signature round-trip diff",
        "",
        f"- Exact match: `{str(exact_match).lower()}`",
        f"- Rollback-canonical match: `{str(canonical_match).lower()}`",
        f"- Expected rows: `{len(expected_rows)}`",
        f"- Actual rows: `{len(actual_rows)}`",
        f"- Exact missing rows: `{sum(missing_counter.values())}`",
        f"- Exact extra rows: `{sum(extra_counter.values())}`",
        f"- Field differences: `{len(field_differences)}`",
        f"- Normalization rule: `{ROUNDTRIP_CATALOG_RULE}`",
        "",
        "## Field differences",
        "",
    ]
    for row in field_differences:
        report_lines.append(
            "- "
            f"`{row['category']}:{row['objectIdentity']}` "
            f"`{row['field']}`: "
            f"`{json.dumps(row['expected'])}` → "
            f"`{json.dumps(row['actual'])}` "
            f"({row['classification']})"
        )
    Path(args.output_report).write_text(
        "\n".join(report_lines) + "\n",
        encoding="utf-8",
    )
    return 0 if canonical_match else 3


def validate_pg_dump_source(source):
    if "\x00" in source:
        raise ValueError("pg_dump contains a NUL byte")
    meta_commands = []
    for line_number, line in enumerate(source.splitlines(), 1):
        stripped = line.lstrip()
        if not stripped.startswith("\\"):
            continue
        parts = stripped.split()
        command = parts[0]
        meta_commands.append((line_number, command, parts[1:]))
    if len(meta_commands) != 2:
        raise ValueError(
            "pg_dump must contain exactly one restrict/unrestrict pair"
        )
    restrict = meta_commands[0]
    unrestrict = meta_commands[1]
    if restrict[1] != r"\restrict" or unrestrict[1] != r"\unrestrict":
        raise ValueError(
            "pg_dump contains an unsafe or unexpected psql meta-command"
        )
    if len(restrict[2]) != 1 or len(unrestrict[2]) != 1:
        raise ValueError("pg_dump restrict boundaries are malformed")
    key = restrict[2][0]
    if key != unrestrict[2][0]:
        raise ValueError("pg_dump restrict boundary keys differ")
    if not re.fullmatch(r"[A-Za-z0-9]{32,}", key):
        raise ValueError("pg_dump restrict boundary key is invalid")
    return key


def canonicalize_pg_dump(args):
    source = Path(args.input).read_text(encoding="utf-8")
    validate_pg_dump_source(source)
    canonical_lines = []
    for line in source.splitlines(keepends=True):
        stripped = line.lstrip()
        prefix = line[: len(line) - len(stripped)]
        ending = "\n" if line.endswith("\n") else ""
        if stripped.startswith(r"\restrict "):
            canonical_lines.append(
                prefix + r"\restrict <PG_DUMP_RANDOM_KEY>" + ending
            )
        elif stripped.startswith(r"\unrestrict "):
            canonical_lines.append(
                prefix + r"\unrestrict <PG_DUMP_RANDOM_KEY>" + ending
            )
        else:
            canonical_lines.append(line)
    canonical = (
        "-- DIGEST-ONLY CANONICAL PG_DUMP; NEVER EXECUTE THIS FILE.\n"
        + "".join(canonical_lines)
    )
    Path(args.output).write_text(canonical, encoding="utf-8")


def prepare_executable_pg_dump(args):
    source = Path(args.input).read_text(encoding="utf-8")
    boundary_key = validate_pg_dump_source(source)
    replacements = {
        "SET statement_timeout = 0;": "SET statement_timeout = '30s';",
        "SET lock_timeout = 0;": "SET lock_timeout = '5s';",
        "SET idle_in_transaction_session_timeout = 0;":
            "SET idle_in_transaction_session_timeout = '60s';",
        "SET transaction_timeout = 0;":
            "SET transaction_timeout = '5min';",
    }
    lines = source.splitlines(keepends=True)
    counts = {line: 0 for line in replacements}
    output_lines = []
    for line in lines:
        ending = "\n" if line.endswith("\n") else ""
        content = line[:-1] if ending else line
        if content in replacements:
            counts[content] += 1
            output_lines.append(replacements[content] + ending)
        else:
            output_lines.append(line)
    missing = [line for line, count in counts.items() if count != 1]
    if missing:
        raise ValueError(
            "pg_dump timeout preamble is missing, duplicated, or drifted: "
            + ", ".join(missing)
        )
    output = "".join(output_lines)
    validate_pg_dump_source(output)
    if (
        f"\\restrict {boundary_key}" not in output
        or f"\\unrestrict {boundary_key}" not in output
    ):
        raise ValueError("executable pg_dump changed restriction boundaries")
    for unsafe in replacements:
        if unsafe in output:
            raise ValueError("executable pg_dump can disable timeout bounds")
    Path(args.output).write_text(output, encoding="utf-8")


def stream_verified_drop_sql(args):
    flags = os.O_RDONLY
    if hasattr(os, "O_NOFOLLOW"):
        flags |= os.O_NOFOLLOW
    source_fd = os.open(args.input, flags)
    try:
        before = os.fstat(source_fd)
        if not stat.S_ISREG(before.st_mode):
            raise ValueError("drop SQL source is not a regular file")
        chunks = []
        while True:
            chunk = os.read(source_fd, 1024 * 1024)
            if not chunk:
                break
            chunks.append(chunk)
        after = os.fstat(source_fd)
        identity_before = (
            before.st_dev,
            before.st_ino,
            before.st_size,
            before.st_mtime_ns,
        )
        identity_after = (
            after.st_dev,
            after.st_ino,
            after.st_size,
            after.st_mtime_ns,
        )
        if identity_before != identity_after:
            raise ValueError("drop SQL source changed while being read")
        data = b"".join(chunks)
    finally:
        os.close(source_fd)

    digest = hashlib.sha256(data).hexdigest()
    if digest != args.expected_sha256:
        raise ValueError("drop SQL SHA-256 differs from accepted manifest")
    if not hasattr(os, "memfd_create"):
        raise ValueError("sealed memfd support is required")
    memfd = os.memfd_create(
        "fst-retired-schema-drop-sql",
        os.MFD_CLOEXEC | os.MFD_ALLOW_SEALING,
    )
    try:
        offset = 0
        while offset < len(data):
            offset += os.write(memfd, data[offset:])
        seal_mask = (
            fcntl.F_SEAL_SEAL
            | fcntl.F_SEAL_SHRINK
            | fcntl.F_SEAL_GROW
            | fcntl.F_SEAL_WRITE
        )
        fcntl.fcntl(memfd, fcntl.F_ADD_SEALS, seal_mask)
        applied_seals = fcntl.fcntl(memfd, fcntl.F_GET_SEALS)
        if applied_seals & seal_mask != seal_mask:
            raise ValueError("drop SQL memfd was not fully sealed")
        memfd_stat = os.fstat(memfd)
        proof = {
            "schemaVersion": 1,
            "sha256": digest,
            "bytes": len(data),
            "source": {
                "device": before.st_dev,
                "inode": before.st_ino,
                "size": before.st_size,
                "mtimeNs": before.st_mtime_ns,
            },
            "sealedMemfd": {
                "inode": memfd_stat.st_ino,
                "size": memfd_stat.st_size,
                "seals": applied_seals,
                "requiredSeals": seal_mask,
            },
        }
        proof_path = Path(args.proof)
        with proof_path.open("w", encoding="utf-8") as handle:
            handle.write(
                json.dumps(proof, sort_keys=True, indent=2) + "\n"
            )
            handle.flush()
            os.fsync(handle.fileno())
        if args.wait_for_release:
            release = sys.stdin.readline()
            if release != "RELEASE\n":
                raise ValueError("drop SQL release token was not received")
        os.lseek(memfd, 0, os.SEEK_SET)
        while True:
            chunk = os.read(memfd, 1024 * 1024)
            if not chunk:
                break
            sys.stdout.buffer.write(chunk)
        sys.stdout.buffer.flush()
    finally:
        os.close(memfd)


def stream_verified_manifest_drop_sql(args):
    def read_stable(path):
        flags = os.O_RDONLY
        if hasattr(os, "O_NOFOLLOW"):
            flags |= os.O_NOFOLLOW
        descriptor = os.open(path, flags)
        try:
            before = os.fstat(descriptor)
            if not stat.S_ISREG(before.st_mode):
                raise ValueError(f"not a regular file: {path}")
            chunks = []
            while True:
                chunk = os.read(descriptor, 1024 * 1024)
                if not chunk:
                    break
                chunks.append(chunk)
            after = os.fstat(descriptor)
            before_identity = (
                before.st_dev,
                before.st_ino,
                before.st_size,
                before.st_mtime_ns,
            )
            after_identity = (
                after.st_dev,
                after.st_ino,
                after.st_size,
                after.st_mtime_ns,
            )
            if before_identity != after_identity:
                raise ValueError(f"file changed while reading: {path}")
            return b"".join(chunks), before
        finally:
            os.close(descriptor)

    def seal_bytes(name, data):
        if not hasattr(os, "memfd_create"):
            raise ValueError("sealed memfd support is required")
        descriptor = os.memfd_create(
            name,
            os.MFD_CLOEXEC | os.MFD_ALLOW_SEALING,
        )
        offset = 0
        while offset < len(data):
            offset += os.write(descriptor, data[offset:])
        required = (
            fcntl.F_SEAL_SEAL
            | fcntl.F_SEAL_SHRINK
            | fcntl.F_SEAL_GROW
            | fcntl.F_SEAL_WRITE
        )
        fcntl.fcntl(descriptor, fcntl.F_ADD_SEALS, required)
        seals = fcntl.fcntl(descriptor, fcntl.F_GET_SEALS)
        if seals & required != required:
            os.close(descriptor)
            raise ValueError(f"memfd was not fully sealed: {name}")
        return descriptor, required, seals

    manifest_data, manifest_stat = read_stable(args.manifest)
    manifest_sha = hashlib.sha256(manifest_data).hexdigest()
    if manifest_sha != args.expected_manifest_sha256:
        raise ValueError("manifest SHA-256 differs from operator approval")
    try:
        manifest = json.loads(manifest_data.decode("utf-8"))
    except (UnicodeDecodeError, json.JSONDecodeError) as exc:
        raise ValueError(f"sealed manifest is invalid JSON: {exc}") from exc
    drop_sha = manifest.get("dropSqlSha256", "")
    if not re.fullmatch(r"[0-9a-f]{64}", drop_sha):
        raise ValueError("sealed manifest has an invalid drop SQL hash")
    service_rows = [
        row
        for row in manifest.get("containers", [])
        if row.get("service") == "fstservice"
    ]
    if len(service_rows) != 1:
        raise ValueError("sealed manifest lacks one fstservice container")
    service_row = service_rows[0]
    service_image_id = service_row.get("image_id", "")
    service_compose_image_id = service_row.get("compose_image_id", "")
    service_container_id = service_row.get("container_id", "")
    if not re.fullmatch(r"sha256:[0-9a-f]{64}", service_image_id):
        raise ValueError("sealed manifest lacks one immutable service image ID")
    if service_compose_image_id != service_image_id:
        raise ValueError("sealed manifest service and Compose image IDs differ")
    if not re.fullmatch(r"[0-9a-f]{64}", service_container_id):
        raise ValueError("sealed manifest lacks one immutable service container ID")
    service_image_reference_sha256 = (
        manifest.get("productionComposeOwnership", {}).get(
            "fstserviceImageReferenceSha256",
            "",
        )
    )
    if not re.fullmatch(r"[0-9a-f]{64}", service_image_reference_sha256):
        raise ValueError(
            "sealed manifest lacks the fstservice image reference hash"
        )
    service_networks = sorted(
        manifest.get("containerConfigAttestation", {}).get(
            "services",
            {},
        ).get("fstservice", {}).get("networks", {})
    )
    if not service_networks or any(
        not isinstance(network, str) or not network
        for network in service_networks
    ):
        raise ValueError("sealed manifest lacks fstservice network ownership")
    postgres_rows = [
        row
        for row in manifest.get("containers", [])
        if row.get("service") == "postgres"
    ]
    if len(postgres_rows) != 1:
        raise ValueError("sealed manifest lacks one postgres container")
    postgres_container_id = postgres_rows[0].get("container_id", "")
    if not re.fullmatch(r"[0-9a-f]{64}", postgres_container_id):
        raise ValueError("sealed manifest postgres container ID is invalid")
    production_target = manifest.get("productionDatabaseTarget", {}).get(
        "runtime",
        {},
    )
    required_target = {
        "container_id": postgres_container_id,
        "configured_host": "postgres",
        "configured_port": production_target.get("runtime_port", ""),
        "configured_database": production_target.get("runtime_database", ""),
        "configured_user": production_target.get("runtime_user", ""),
        "runtime_address": "local-socket",
        "runtime_port": production_target.get("configured_port", ""),
        "runtime_database": production_target.get("configured_database", ""),
        "runtime_user": production_target.get("configured_user", ""),
        "in_recovery": "false",
    }
    if any(
        production_target.get(key, "") != value
        for key, value in required_target.items()
    ):
        raise ValueError("sealed manifest production database target is invalid")
    postgres_system_identifier = production_target.get(
        "system_identifier",
        "",
    )
    if not re.fullmatch(r"\d+", postgres_system_identifier):
        raise ValueError("sealed manifest system identifier is invalid")
    host_mapping = manifest.get("containerConfigAttestation", {}).get(
        "databaseHostMapping",
        {},
    )
    if (
        host_mapping.get("host") != production_target["configured_host"]
        or host_mapping.get("postgresContainerId") != postgres_container_id
        or host_mapping.get("postgresSystemIdentifier")
        != postgres_system_identifier
        or sorted(host_mapping.get("sharedNetworks", []))
        != service_networks
    ):
        raise ValueError("sealed manifest database host mapping is inconsistent")

    drop_data, drop_stat = read_stable(args.drop_sql)
    actual_drop_sha = hashlib.sha256(drop_data).hexdigest()
    if actual_drop_sha != drop_sha:
        raise ValueError("drop SQL differs from sealed manifest")

    manifest_fd, manifest_required, manifest_seals = seal_bytes(
        "fst-retired-schema-manifest",
        manifest_data,
    )
    drop_fd, drop_required, drop_seals = seal_bytes(
        "fst-retired-schema-drop-sql",
        drop_data,
    )
    try:
        proof = {
            "schemaVersion": 1,
            "manifest": {
                "sha256": manifest_sha,
                "bytes": len(manifest_data),
                "source": {
                    "device": manifest_stat.st_dev,
                    "inode": manifest_stat.st_ino,
                    "size": manifest_stat.st_size,
                    "mtimeNs": manifest_stat.st_mtime_ns,
                },
                "sealedMemfd": {
                    "inode": os.fstat(manifest_fd).st_ino,
                    "size": os.fstat(manifest_fd).st_size,
                    "seals": manifest_seals,
                    "requiredSeals": manifest_required,
                },
                "approvedFstserviceImageId": service_image_id,
                "approvedFstserviceContainerId": service_container_id,
                "approvedFstserviceImageReferenceSha256": (
                    service_image_reference_sha256
                ),
                "approvedFstserviceNetworks": service_networks,
                "approvedPostgresContainerId": postgres_container_id,
                "approvedPostgresSystemIdentifier": (
                    postgres_system_identifier
                ),
                "approvedProductionDatabaseTarget": production_target,
            },
            "dropSql": {
                "sha256": actual_drop_sha,
                "manifestSha256": drop_sha,
                "bytes": len(drop_data),
                "source": {
                    "device": drop_stat.st_dev,
                    "inode": drop_stat.st_ino,
                    "size": drop_stat.st_size,
                    "mtimeNs": drop_stat.st_mtime_ns,
                },
                "sealedMemfd": {
                    "inode": os.fstat(drop_fd).st_ino,
                    "size": os.fstat(drop_fd).st_size,
                    "seals": drop_seals,
                    "requiredSeals": drop_required,
                },
            },
        }
        with Path(args.proof).open("w", encoding="utf-8") as handle:
            handle.write(
                json.dumps(proof, sort_keys=True, indent=2) + "\n"
            )
            handle.flush()
            os.fsync(handle.fileno())
        if args.wait_for_release:
            if sys.stdin.readline() != "RELEASE\n":
                raise ValueError("immutable manifest/SQL release was not received")
        os.lseek(drop_fd, 0, os.SEEK_SET)
        while True:
            chunk = os.read(drop_fd, 1024 * 1024)
            if not chunk:
                break
            sys.stdout.buffer.write(chunk)
        sys.stdout.buffer.flush()
    finally:
        os.close(manifest_fd)
        os.close(drop_fd)


class _DockerSocketConnection(http.client.HTTPConnection):
    def __init__(self, socket_path):
        super().__init__("localhost")
        self.socket_path = socket_path

    def connect(self):
        self.sock = socket.socket(socket.AF_UNIX, socket.SOCK_STREAM)
        self.sock.connect(self.socket_path)


def _docker_api_request(
    socket_path,
    method,
    path,
    payload=None,
    accepted_statuses=None,
):
    body = None
    headers = {"Host": "localhost"}
    if payload is not None:
        body = json.dumps(
            payload,
            sort_keys=True,
            separators=(",", ":"),
        ).encode("utf-8")
        headers.update({
            "Content-Type": "application/json",
            "Content-Length": str(len(body)),
        })
    connection = _DockerSocketConnection(socket_path)
    try:
        connection.request(method, path, body=body, headers=headers)
        response = connection.getresponse()
        response_body = response.read()
    finally:
        connection.close()
    accepted = accepted_statuses or range(200, 300)
    if response.status not in accepted:
        safe_path = path.partition("?")[0]
        raise ValueError(
            f"Docker API {method} {safe_path} failed with HTTP "
            f"{response.status}"
        )
    if not response_body:
        return None
    return json.loads(response_body.decode("utf-8"))


def _write_fsynced_json(path, value):
    output = Path(path)
    output.parent.mkdir(parents=True, exist_ok=True)
    with output.open("x", encoding="utf-8") as handle:
        handle.write(json.dumps(value, sort_keys=True, indent=2) + "\n")
        handle.flush()
        os.fsync(handle.fileno())
    directory_fd = os.open(output.parent, os.O_RDONLY | os.O_DIRECTORY)
    try:
        os.fsync(directory_fd)
    finally:
        os.close(directory_fd)


def _startup_command(args):
    command = json.loads(args.command_json)
    if (
        not isinstance(command, list)
        or not command
        or any(not isinstance(item, str) or not item for item in command)
    ):
        raise ValueError("startup command must be a nonempty JSON string array")
    return command


def _startup_clone_projection(container):
    config = container.get("Config") or {}
    host_config = container.get("HostConfig") or {}
    excluded_config = {
        "AttachStderr",
        "AttachStdin",
        "AttachStdout",
        "Cmd",
        "Healthcheck",
        "Hostname",
        "Image",
        "Labels",
        "OpenStdin",
        "StdinOnce",
        "Tty",
    }
    excluded_host = {
        "AutoRemove",
        "ContainerIDFile",
        "ExtraHosts",
        "Links",
        "NetworkMode",
        "PortBindings",
        "PublishAllPorts",
        "RestartPolicy",
        "VolumesFrom",
    }
    projected_host = {
        key: value
        for key, value in sorted(host_config.items())
        if key not in excluded_host
    }
    if "OomKillDisable" in projected_host:
        projected_host["OomKillDisable"] = bool(
            projected_host["OomKillDisable"]
        )
    return {
        "config": {
            key: value
            for key, value in sorted(config.items())
            if key not in excluded_config
        },
        "hostConfig": projected_host,
    }


def _startup_projection_sha256(container):
    encoded = json.dumps(
        _startup_clone_projection(container),
        sort_keys=True,
        separators=(",", ":"),
    ).encode("utf-8")
    return hashlib.sha256(encoded).hexdigest()


def _inspect_docker_container(socket_path, container_id):
    return _docker_api_request(
        socket_path,
        "GET",
        f"/containers/{quote(container_id, safe='')}/json",
    )


def _inspect_docker_image(socket_path, image_reference):
    return _docker_api_request(
        socket_path,
        "GET",
        f"/images/{quote(image_reference, safe='')}/json",
    )


def _validate_startup_identity_args(args):
    if not re.fullmatch(r"[0-9a-f]{64}", args.source_container_id):
        raise ValueError("source service container ID is invalid")
    if not re.fullmatch(r"sha256:[0-9a-f]{64}", args.expected_image_id):
        raise ValueError("expected service image ID is invalid")
    if not re.fullmatch(r"[0-9a-f]{64}", args.expected_manifest_sha256):
        raise ValueError("expected manifest SHA-256 is invalid")
    if not re.fullmatch(
        r"[0-9a-f]{64}",
        args.expected_image_reference_sha256,
    ):
        raise ValueError("expected image reference SHA-256 is invalid")
    if (
        hashlib.sha256(
            args.compose_image_reference.encode("utf-8")
        ).hexdigest()
        != args.expected_image_reference_sha256
    ):
        raise ValueError("Compose service image reference differs from manifest")
    if not re.fullmatch(r"[a-z0-9][a-z0-9_.-]{0,127}", args.container_name):
        raise ValueError("startup container name is invalid")
    if not re.fullmatch(r"[0-9a-f]{64}", args.expected_postgres_container_id):
        raise ValueError("expected postgres container ID is invalid")
    if not re.fullmatch(r"[A-Za-z0-9_.-]+", args.database_host):
        raise ValueError("database host is invalid")


def _expected_startup_networks(args):
    networks = json.loads(args.expected_networks_json)
    if (
        not isinstance(networks, list)
        or not networks
        or any(not isinstance(network, str) or not network for network in networks)
        or len(set(networks)) != len(networks)
        or networks != sorted(networks)
    ):
        raise ValueError("expected startup networks must be a sorted JSON array")
    return networks


def _startup_database_host_pin(source, args):
    postgres = _inspect_docker_container(
        args.docker_socket,
        args.expected_postgres_container_id,
    )
    if postgres.get("Id") != args.expected_postgres_container_id:
        raise ValueError("postgres container identity drift")
    if not (postgres.get("State") or {}).get("Running"):
        raise ValueError("postgres container is not running")
    primary_network = str(
        (source.get("HostConfig") or {}).get("NetworkMode") or ""
    )
    endpoint = (
        (postgres.get("NetworkSettings") or {})
        .get("Networks", {})
        .get(primary_network)
    ) or {}
    postgres_ip = str(endpoint.get("IPAddress") or "")
    try:
        parsed_ip = ipaddress.ip_address(postgres_ip)
    except ValueError as exc:
        raise ValueError("postgres primary-network IP is invalid") from exc
    if parsed_ip.version != 4:
        raise ValueError("postgres primary-network pin must be IPv4")
    return {
        "host": args.database_host,
        "ipAddress": postgres_ip,
        "network": primary_network,
        "postgresContainerId": args.expected_postgres_container_id,
        "extraHost": f"{args.database_host}:{postgres_ip}",
    }


def _attest_immutable_startup_container(args, phase):
    _validate_startup_identity_args(args)
    command = _startup_command(args)
    expected_networks = _expected_startup_networks(args)
    source = _inspect_docker_container(
        args.docker_socket,
        args.source_container_id,
    )
    if source.get("Id") != args.source_container_id:
        raise ValueError("source service container identity drift")
    if source.get("Image") != args.expected_image_id:
        raise ValueError("source service image differs from manifest")
    if not (source.get("State") or {}).get("Running"):
        raise ValueError("source service container is not running")

    compose_image = _inspect_docker_image(
        args.docker_socket,
        args.compose_image_reference,
    )
    compose_image_id = compose_image.get("Id", "")
    if compose_image_id != args.expected_image_id:
        raise ValueError("mutable Compose service image reference was retagged")

    container = _inspect_docker_container(
        args.docker_socket,
        args.container_id,
    )
    state = container.get("State") or {}
    config = container.get("Config") or {}
    host_config = container.get("HostConfig") or {}
    if container.get("Id") != args.container_id:
        raise ValueError("startup container identity drift")
    if container.get("Name", "").lstrip("/") != args.container_name:
        raise ValueError("startup container name drift")
    if container.get("Image") != args.expected_image_id:
        raise ValueError("startup container actual image differs from manifest")
    if config.get("Image") != args.expected_image_id:
        raise ValueError("startup container configured image differs from manifest")
    if config.get("Cmd") != command:
        raise ValueError("startup container command drift")
    if (
        state.get("Status") != "created"
        or bool(state.get("Running"))
        or int(state.get("Pid") or 0) != 0
    ):
        raise ValueError("startup container was started before image attestation")
    if bool(host_config.get("AutoRemove")):
        raise ValueError("startup container must not auto-remove")
    if (host_config.get("RestartPolicy") or {}).get("Name") != "no":
        raise ValueError("startup container restart policy is not disabled")
    if host_config.get("PortBindings"):
        raise ValueError("startup container unexpectedly publishes ports")

    source_networks = set(
        (source.get("NetworkSettings") or {}).get("Networks") or {}
    )
    if source_networks != set(expected_networks):
        raise ValueError("source service network set differs from manifest")
    database_host_pin = _startup_database_host_pin(source, args)
    actual_networks = (
        (container.get("NetworkSettings") or {}).get("Networks") or {}
    )
    if set(actual_networks) != set(expected_networks):
        raise ValueError("startup container network set differs from service")
    forbidden_aliases = {"fstservice"}
    actual_aliases = sorted({
        str(alias)
        for network in actual_networks.values()
        for alias in (network.get("Aliases") or [])
        if alias
    })
    if forbidden_aliases.intersection(actual_aliases):
        raise ValueError("startup container acquired a production service alias")
    resolution_names = sorted({
        str(name)
        for network in actual_networks.values()
        for name in (
            list(network.get("Aliases") or [])
            + list(network.get("DNSNames") or [])
        )
        if name
    } | {container.get("Name", "").lstrip("/")})
    if args.database_host in resolution_names:
        raise ValueError("startup container itself resolves as the database host")
    source_extra_hosts = list(
        (source.get("HostConfig") or {}).get("ExtraHosts") or []
    )
    if any(
        str(entry).partition(":")[0] == args.database_host
        for entry in source_extra_hosts
    ):
        raise ValueError("source service already overrides the database host")
    expected_extra_hosts = source_extra_hosts + [
        database_host_pin["extraHost"]
    ]
    if list(host_config.get("ExtraHosts") or []) != expected_extra_hosts:
        raise ValueError("startup database host pin drift")

    source_projection_sha256 = _startup_projection_sha256(source)
    actual_projection_sha256 = _startup_projection_sha256(container)
    if actual_projection_sha256 != source_projection_sha256:
        raise ValueError("startup container runtime configuration drift")

    command_sha256 = hashlib.sha256(
        json.dumps(command, separators=(",", ":")).encode("utf-8")
    ).hexdigest()
    return {
        "schemaVersion": 1,
        "phase": phase,
        "expectedManifestSha256": args.expected_manifest_sha256,
        "sourceContainerId": args.source_container_id,
        "containerName": args.container_name,
        "containerId": args.container_id,
        "expectedImageId": args.expected_image_id,
        "composeImageReferenceSha256": (
            args.expected_image_reference_sha256
        ),
        "composeImageResolvedId": compose_image_id,
        "actualImageId": container.get("Image", ""),
        "configuredImage": config.get("Image", ""),
        "state": {
            "status": state.get("Status", ""),
            "running": bool(state.get("Running")),
            "pid": int(state.get("Pid") or 0),
            "startedAt": state.get("StartedAt", ""),
        },
        "commandSha256": command_sha256,
        "sourceConfigurationSha256": source_projection_sha256,
        "actualConfigurationSha256": actual_projection_sha256,
        "networks": expected_networks,
        "networkAliases": actual_aliases,
        "networkResolutionNames": resolution_names,
        "databaseHostPin": database_host_pin,
        "autoRemove": bool(host_config.get("AutoRemove")),
        "restartPolicy": (
            (host_config.get("RestartPolicy") or {}).get("Name") or ""
        ),
        "portBindingsPresent": bool(host_config.get("PortBindings")),
    }


def create_immutable_startup_container(args):
    _validate_startup_identity_args(args)
    command = _startup_command(args)
    expected_networks = _expected_startup_networks(args)
    source = _inspect_docker_container(
        args.docker_socket,
        args.source_container_id,
    )
    if source.get("Id") != args.source_container_id:
        raise ValueError("source service container identity drift")
    if source.get("Image") != args.expected_image_id:
        raise ValueError("source service image differs from manifest")
    if not (source.get("State") or {}).get("Running"):
        raise ValueError("source service container is not running")
    compose_image = _inspect_docker_image(
        args.docker_socket,
        args.compose_image_reference,
    )
    if compose_image.get("Id") != args.expected_image_id:
        raise ValueError("mutable Compose service image reference was retagged")

    source_networks = (
        (source.get("NetworkSettings") or {}).get("Networks") or {}
    )
    if set(source_networks) != set(expected_networks):
        raise ValueError("source service network set differs from manifest")
    source_host_config = source.get("HostConfig") or {}
    primary_network = str(source_host_config.get("NetworkMode") or "")
    if primary_network not in source_networks:
        raise ValueError("source service primary network is not attestable")
    database_host_pin = _startup_database_host_pin(source, args)
    source_extra_hosts = list(source_host_config.get("ExtraHosts") or [])
    if any(
        str(entry).partition(":")[0] == args.database_host
        for entry in source_extra_hosts
    ):
        raise ValueError("source service already overrides the database host")

    config = copy.deepcopy(source.get("Config") or {})
    config.update({
        "AttachStderr": True,
        "AttachStdin": False,
        "AttachStdout": True,
        "Cmd": command,
        "Healthcheck": {"Test": ["NONE"]},
        "Hostname": args.container_name,
        "Image": args.expected_image_id,
        "Labels": {
            "com.fst.retired-schema-cleanup": "startup-check",
            "com.fst.retired-schema-cleanup.manifest-sha256": (
                args.expected_manifest_sha256
            ),
            "com.fst.retired-schema-cleanup.source-container-id": (
                args.source_container_id
            ),
        },
        "OpenStdin": False,
        "StdinOnce": False,
        "Tty": False,
    })
    host_config = copy.deepcopy(source_host_config)
    host_config.update({
        "AutoRemove": False,
        "ContainerIDFile": "",
        "ExtraHosts": source_extra_hosts + [database_host_pin["extraHost"]],
        "Links": None,
        "NetworkMode": primary_network,
        "PortBindings": {},
        "PublishAllPorts": False,
        "RestartPolicy": {"Name": "no", "MaximumRetryCount": 0},
        "VolumesFrom": None,
    })
    primary_endpoint = {}
    if primary_network not in {"bridge", "host", "none"}:
        primary_endpoint["Aliases"] = [args.container_name]
    create_payload = {
        **config,
        "HostConfig": host_config,
        "Image": args.expected_image_id,
        "NetworkingConfig": {
            "EndpointsConfig": {
                primary_network: primary_endpoint,
            },
        },
    }

    created_id = ""
    try:
        created = _docker_api_request(
            args.docker_socket,
            "POST",
            f"/containers/create?name={quote(args.container_name, safe='')}",
            create_payload,
        )
        created_id = str((created or {}).get("Id") or "")
        if not re.fullmatch(r"[0-9a-f]{64}", created_id):
            raise ValueError("Docker returned an invalid startup container ID")
        for network_name, endpoint in sorted(source_networks.items()):
            if network_name == primary_network:
                continue
            network_id = str(endpoint.get("NetworkID") or "")
            if not re.fullmatch(r"[0-9a-f]{64}", network_id):
                raise ValueError("source service network ID is invalid")
            endpoint_config = {}
            if network_name not in {"bridge", "host", "none"}:
                endpoint_config["Aliases"] = [args.container_name]
            _docker_api_request(
                args.docker_socket,
                "POST",
                f"/networks/{quote(network_id, safe='')}/connect",
                {
                    "Container": created_id,
                    "EndpointConfig": endpoint_config,
                },
            )
        args.container_id = created_id
        attestation = _attest_immutable_startup_container(
            args,
            "created-prestart",
        )
        _write_fsynced_json(args.output, attestation)
    except BaseException:
        if created_id:
            try:
                _docker_api_request(
                    args.docker_socket,
                    "DELETE",
                    f"/containers/{quote(created_id, safe='')}?force=1&v=1",
                    accepted_statuses={204, 404},
                )
            except Exception:
                pass
        raise
    print(created_id)


def attest_immutable_startup_container(args):
    attestation = _attest_immutable_startup_container(
        args,
        "attested-before-start",
    )
    _write_fsynced_json(args.output, attestation)


def attest_startup_database_routing(args):
    failures = []
    if not re.fullmatch(r"[0-9a-f]{64}", args.expected_manifest_sha256):
        raise ValueError("expected manifest SHA-256 is invalid")
    if not re.fullmatch(r"[0-9a-f]{64}", args.expected_postgres_container_id):
        raise ValueError("expected postgres container ID is invalid")
    if not re.fullmatch(r"\d+", args.expected_system_identifier):
        raise ValueError("expected postgres system identifier is invalid")
    if not re.fullmatch(r"[A-Za-z0-9_.-]+", args.expected_host):
        raise ValueError("expected database host is invalid")
    if not re.fullmatch(r"\d+", args.expected_port):
        raise ValueError("expected database port is invalid")
    if not args.expected_database or not args.expected_user:
        raise ValueError("expected database identity is incomplete")
    expected_networks = _expected_startup_networks(args)

    startup = json.loads(
        Path(args.startup_attestation).read_text(encoding="utf-8")
    )
    target_rows = read_csv(args.target_attestation)
    inspected = json.load(sys.stdin)
    if not isinstance(inspected, list):
        raise ValueError("docker inspect payload must be a JSON array")
    inspect_by_id = {
        str(item.get("Id") or ""): item
        for item in inspected
        if item.get("Id")
    }

    startup_container_id = str(startup.get("containerId") or "")
    startup_item = inspect_by_id.get(startup_container_id, {})
    startup_state = startup_item.get("State") or {}
    startup_host_config = startup_item.get("HostConfig") or {}
    startup_networks = (
        (startup_item.get("NetworkSettings") or {}).get("Networks") or {}
    )
    database_host_pin = startup.get("databaseHostPin") or {}
    expected_extra_host = (
        f"{database_host_pin.get('host', '')}:"
        f"{database_host_pin.get('ipAddress', '')}"
    )
    if (
        startup.get("phase") != "attested-before-start"
        or startup.get("expectedManifestSha256")
        != args.expected_manifest_sha256
        or startup.get("networks") != expected_networks
        or not re.fullmatch(r"[0-9a-f]{64}", startup_container_id)
        or startup_item.get("Id") != startup_container_id
        or startup_state.get("Status") != "created"
        or bool(startup_state.get("Running"))
        or int(startup_state.get("Pid") or 0) != 0
        or set(startup_networks) != set(expected_networks)
        or database_host_pin.get("host") != args.expected_host
        or database_host_pin.get("network") not in expected_networks
        or database_host_pin.get("postgresContainerId")
        != args.expected_postgres_container_id
        or expected_extra_host
        not in list(startup_host_config.get("ExtraHosts") or [])
    ):
        failures.append("startup container prestart identity/state drift")

    target = target_rows[0] if len(target_rows) == 1 else {}
    expected_target = {
        "configured_host": args.expected_host,
        "configured_port": args.expected_port,
        "configured_database": args.expected_database,
        "configured_user": args.expected_user,
        "container_id": args.expected_postgres_container_id,
        "runtime_address": "local-socket",
        "runtime_port": args.expected_port,
        "runtime_database": args.expected_database,
        "runtime_user": args.expected_user,
        "in_recovery": "false",
        "system_identifier": args.expected_system_identifier,
    }
    for key, expected in expected_target.items():
        if target.get(key, "") != expected:
            failures.append(f"startup database target drift: {key}")

    postgres_item = inspect_by_id.get(args.expected_postgres_container_id, {})
    postgres_state = postgres_item.get("State") or {}
    postgres_networks = (
        (postgres_item.get("NetworkSettings") or {}).get("Networks") or {}
    )
    if (
        postgres_item.get("Id") != args.expected_postgres_container_id
        or postgres_state.get("Status") != "running"
        or not bool(postgres_state.get("Running"))
    ):
        failures.append("manifest-bound postgres container is not running")

    alias_owners = {}
    postgres_endpoints = {}
    for network_name in expected_networks:
        startup_endpoint = startup_networks.get(network_name) or {}
        postgres_endpoint = postgres_networks.get(network_name) or {}
        postgres_network_id = str(postgres_endpoint.get("NetworkID") or "")
        postgres_aliases = {
            str(alias)
            for alias in (postgres_endpoint.get("Aliases") or [])
            if alias
        }
        postgres_ip = str(postgres_endpoint.get("IPAddress") or "")
        if (
            not startup_endpoint
            or not re.fullmatch(r"[0-9a-f]{64}", postgres_network_id)
            or args.expected_host not in postgres_aliases
            or not postgres_ip
        ):
            failures.append(
                f"manifest-bound postgres network mapping drift: {network_name}"
            )
        postgres_endpoints[network_name] = {
            "networkId": postgres_network_id,
            "ipAddress": postgres_ip,
            "hostAliasPresent": args.expected_host in postgres_aliases,
        }
        if (
            network_name == database_host_pin.get("network")
            and postgres_ip != database_host_pin.get("ipAddress")
        ):
            failures.append("startup database host pin no longer maps to postgres")

        owners = []
        for item in inspected:
            endpoint = (
                (item.get("NetworkSettings") or {})
                .get("Networks", {})
                .get(network_name)
            )
            if not endpoint:
                continue
            aliases = {
                str(alias)
                for alias in (endpoint.get("Aliases") or [])
                if alias
            }
            dns_names = {
                str(name)
                for name in (endpoint.get("DNSNames") or [])
                if name
            }
            container_name = str(item.get("Name") or "").lstrip("/")
            resolution_sources = []
            if args.expected_host in aliases:
                resolution_sources.append("Aliases")
            if args.expected_host in dns_names:
                resolution_sources.append("DNSNames")
            if args.expected_host == container_name:
                resolution_sources.append("containerName")
            if not resolution_sources:
                continue
            state = item.get("State") or {}
            owners.append({
                "containerId": str(item.get("Id") or ""),
                "containerName": container_name,
                "state": str(state.get("Status") or ""),
                "running": bool(state.get("Running")),
                "networkId": str(endpoint.get("NetworkID") or ""),
                "ipAddress": str(endpoint.get("IPAddress") or ""),
                "resolutionSources": resolution_sources,
                "resolutionNames": sorted(
                    aliases | dns_names | {container_name}
                ),
            })
        owners.sort(key=lambda row: row["containerId"])
        alias_owners[network_name] = owners
        if (
            len(owners) != 1
            or owners[0]["containerId"]
            != args.expected_postgres_container_id
            or owners[0]["state"] != "running"
            or owners[0]["running"] is not True
        ):
            failures.append(
                f"database alias ownership is not exclusive: {network_name}"
            )

    result = {
        "schemaVersion": 1,
        "success": not failures,
        "acceptedManifestSha256": args.expected_manifest_sha256,
        "startupContainerId": startup_container_id,
        "attachedNetworks": expected_networks,
        "databaseTarget": expected_target,
        "databaseHostPin": database_host_pin,
        "postgres": {
            "containerId": args.expected_postgres_container_id,
            "systemIdentifier": args.expected_system_identifier,
            "endpoints": postgres_endpoints,
        },
        "aliasOwners": alias_owners,
        "failures": sorted(set(failures)),
    }
    _write_fsynced_json(args.output, result)
    if failures:
        for failure in result["failures"]:
            print(f"ERROR: {failure}", file=sys.stderr)
        return 3
    return 0


def validate_capacity_evidence(capture_dir, failures):
    capture_dir = Path(capture_dir)
    report_path = capture_dir / "capacity-guard.json"
    policy_path = capture_dir / "capacity-guard.policy.json"
    if not report_path.is_file():
        failures.append("full capacity guard JSON report is missing")
        report = {}
    else:
        try:
            report = json.loads(report_path.read_text(encoding="utf-8"))
        except (json.JSONDecodeError, OSError) as exc:
            failures.append(f"capacity guard report is invalid: {exc}")
            report = {}
    if not policy_path.is_file():
        failures.append("capacity guard effective policy is missing")
        policy = {}
    else:
        try:
            policy = json.loads(policy_path.read_text(encoding="utf-8"))
        except (json.JSONDecodeError, OSError) as exc:
            failures.append(f"capacity guard policy is invalid: {exc}")
            policy = {}

    if policy.get("schemaVersion") != 1:
        failures.append("capacity guard policy schema is invalid")
    if policy.get("actionClass") != "reclaim":
        failures.append("capacity guard policy action is not reclaim")
    if policy.get("effectiveParameters") != CAPACITY_POLICY:
        failures.append("capacity guard effective parameters are not pinned")
    script_hash = policy.get("guardScriptSha256", "")
    if not re.fullmatch(r"[0-9a-f]{64}", script_hash):
        failures.append("capacity guard script hash is invalid")
    capacity = report.get("capacity", {})
    for key, expected in CAPACITY_POLICY.items():
        if capacity.get(key) != expected:
            failures.append(f"capacity guard report policy drift: {key}")
    if report.get("actionClass") != "reclaim":
        failures.append("capacity guard report action is not reclaim")
    if report.get("decision") not in {
        "accepted",
        "accepted_with_capacity_alert",
    }:
        failures.append("capacity guard decision is not accepted")
    if capacity.get("reclaimAllowed") is not True:
        failures.append("capacity guard did not allow reclaim")
    return report, policy


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
    expected_owned_sequences = Counter(
        (
            "sequence-owner",
            row.schema,
            row.name,
            row.parent_schema,
            row.parent_name,
            row.owner_column,
            "a",
        )
        for row in objects
        if row.object_type == "sequence"
    )
    actual_owned_sequences = Counter(
        (
            row.get("kind", ""),
            row.get("schema", ""),
            row.get("name", ""),
            row.get("target_schema", ""),
            row.get("target_name", ""),
            row.get("definition", ""),
            row.get("state", ""),
        )
        for row in owned_rows
        if row.get("kind", "") == "sequence-owner"
    )
    if actual_owned_sequences != expected_owned_sequences:
        missing = list(
            (expected_owned_sequences - actual_owned_sequences).elements()
        )
        unexpected = list(
            (actual_owned_sequences - expected_owned_sequences).elements()
        )
        if missing:
            failures.append(
                f"{len(missing)} expected owned sequence relationship(s) "
                "are missing"
            )
        if unexpected:
            failures.append(
                f"{len(unexpected)} unexpected owned sequence "
                "relationship(s) exist"
            )
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
    capacity_report, capacity_policy = validate_capacity_evidence(
        capture_dir,
        failures,
    )
    tooling_by_name = {
        row.get("name", ""): row.get("sha256", "")
        for row in tooling_rows
    }
    if capacity_policy.get("guardScriptSha256") != tooling_by_name.get(
        "tools/postgres-capacity-guard.sh"
    ):
        failures.append("capacity guard policy script hash is not tooling-bound")

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
    sanitized_compose = {}
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
    fstservice_image_reference_sha256 = (
        sanitized_compose.get("services", {}).get(
            "fstservice",
            {},
        ).get("imageReferenceSha256", "")
    )
    if not re.fullmatch(
        r"[0-9a-f]{64}",
        fstservice_image_reference_sha256,
    ):
        failures.append("fstservice Compose image reference hash is invalid")
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
    container_config_path = (
        capture_dir / "container-config-attestation.json"
    )
    if not container_config_path.is_file():
        failures.append("actual container configuration attestation is missing")
        container_config = {}
    else:
        try:
            container_config = json.loads(
                container_config_path.read_text(encoding="utf-8")
            )
        except (json.JSONDecodeError, OSError) as exc:
            failures.append(
                f"actual container configuration attestation is invalid: {exc}"
            )
            container_config = {}
    if container_config.get("success") is not True:
        failures.append("actual containers do not match resolved compose")
    config_services = container_config.get("services", {})
    if set(config_services) != {
        "postgres",
        "fstservice",
        "festivalweb",
        "fstworker",
    }:
        failures.append("actual container attestation service set is inexact")
    host_mapping = container_config.get("databaseHostMapping", {})
    if (
        host_mapping.get("postgresContainerId")
        != runtime_target.get("container_id")
        or host_mapping.get("postgresSystemIdentifier")
        != runtime_target.get("system_identifier")
        or host_mapping.get("host") != "postgres"
        or not host_mapping.get("sharedNetworks")
    ):
        failures.append("fstservice-to-postgres host mapping is invalid")
    alias_owners = host_mapping.get("aliasOwners", {})
    if not alias_owners or any(
        len(owners) != 1
        or owners[0].get("containerId")
        != runtime_target.get("container_id")
        for owners in alias_owners.values()
    ):
        failures.append("postgres network alias ownership is not exclusive")
    expected_fingerprint_names = set(PUBLIC_FINGERPRINT_NAMES) | {
        "readyz",
        "web-shell",
        "service-info",
    }
    if set(container_config.get("fingerprintBindings", {})) != (
        expected_fingerprint_names
    ):
        failures.append("fingerprint-to-container port binding is incomplete")
    bases = container_config.get("fingerprintBaseUrls", {})
    if (
        not re.fullmatch(r"http://(?:127\.0\.0\.1|localhost):\d+", bases.get(
            "service",
            "",
        ))
        or not re.fullmatch(
            r"http://(?:127\.0\.0\.1|localhost):\d+",
            bases.get("web", ""),
        )
    ):
        failures.append("fingerprint base URLs are not attested")

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

    rollback_catalog_path = Path(args.rollback_catalog_signature)
    rollback_catalog_metadata_path = Path(
        args.rollback_catalog_metadata
    )
    rollback_catalog_expected_sql = Path(
        args.rollback_catalog_expected_sql
    )
    rollback_catalog_assert_sql = Path(
        args.rollback_catalog_assert_sql
    )
    rollback_catalog_metadata = {}
    if not rollback_catalog_path.is_file():
        failures.append("rollback-canonical catalog signature is missing")
    if not rollback_catalog_metadata_path.is_file():
        failures.append("rollback-canonical catalog metadata is missing")
    else:
        try:
            rollback_catalog_metadata = json.loads(
                rollback_catalog_metadata_path.read_text(encoding="utf-8")
            )
        except (json.JSONDecodeError, OSError) as exc:
            failures.append(
                f"rollback-canonical catalog metadata is invalid: {exc}"
            )
    if rollback_catalog_path.is_file() and (
        rollback_catalog_metadata.get("sha256")
        != sha256_path(rollback_catalog_path)
    ):
        failures.append("rollback-canonical catalog signature hash drift")
    if (
        rollback_catalog_metadata.get("schemaVersion") != 1
        or rollback_catalog_metadata.get("rule")
        != ROUNDTRIP_CATALOG_RULE
        or rollback_catalog_metadata.get("sentinel")
        != ROUNDTRIP_CATALOG_SENTINEL
        or rollback_catalog_metadata.get("exactSignatureSha256")
        != catalog_metadata.get("sha256")
        or rollback_catalog_metadata.get("querySha256")
        != catalog_metadata.get("querySha256")
        or rollback_catalog_metadata.get("columnCatalogSha256")
        != column_catalog_sha
        or rollback_catalog_metadata.get("normalizedConstraintCount")
        != 45
        or len(
            rollback_catalog_metadata.get(
                "normalizedConstraintIdentities",
                [],
            )
        )
        != 45
    ):
        failures.append("rollback-canonical catalog policy drift")
    for path, label in [
        (
            rollback_catalog_expected_sql,
            "rollback-canonical catalog expected SQL",
        ),
        (
            rollback_catalog_assert_sql,
            "rollback-canonical catalog assertion SQL",
        ),
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
    generated_data_families = {
        row.get("family")
        for row in rollback_rows
        if row.get("kind") == "generated-data"
    }
    if generated_data_families != {
        "logical-shadow",
        "score-observations",
        "band-song-projection",
    }:
        failures.append("generated rollback data coverage is inexact")

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
        "capacityGuardPolicy": capacity_policy,
        "capacityGuardPolicySha256": sha256_path(
            capture_dir / "capacity-guard.policy.json"
        ),
        "capacityGuardCheck": {
            "decision": capacity_report.get("decision"),
            "actionClass": capacity_report.get("actionClass"),
            "reclaimAllowed": capacity_report.get(
                "capacity",
                {},
            ).get("reclaimAllowed"),
        },
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
            "fstserviceImageReferenceSha256": (
                fstservice_image_reference_sha256
            ),
            "binds": stable_rows(compose_bind_rows),
            "bindConfigFiles": stable_rows(bind_config_rows),
        },
        "productionDatabaseTarget": {
            "configured": configured_target,
            "runtime": runtime_target,
        },
        "containerConfigAttestation": container_config,
        "containerConfigAttestationSha256": (
            sha256_path(container_config_path)
            if container_config_path.is_file()
            else ""
        ),
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
        "rollbackCatalogSignature": rollback_catalog_metadata,
        "rollbackCatalogMetadataSha256": (
            sha256_path(rollback_catalog_metadata_path)
            if rollback_catalog_metadata_path.is_file()
            else ""
        ),
        "rollbackCatalogExpectedSqlSha256": (
            sha256_path(rollback_catalog_expected_sql)
            if rollback_catalog_expected_sql.is_file()
            else ""
        ),
        "rollbackCatalogAssertSqlSha256": (
            sha256_path(rollback_catalog_assert_sql)
            if rollback_catalog_assert_sql.is_file()
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


def validate_committed_resume_source(args):
    source = Path(args.source)
    package = source / "package"
    manifest_path = package / "manifest.json"
    failures = []
    checked_files = {}

    if not manifest_path.is_file():
        raise ValueError("resume source manifest is missing")
    manifest_sha = sha256_path(manifest_path)
    if manifest_sha != args.expected_manifest_sha256:
        failures.append("resume source manifest hash differs from approval")
    manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
    if manifest.get("ready") is not True:
        failures.append("resume source manifest was not ready")
    parity = manifest.get("parityEvidence", {})
    if (
        parity.get("decision") != "accepted"
        or parity.get("scrapeId") != CLEANUP_SCRAPE_ID
        or parity.get("exactPublicFingerprintParity") is not True
        or parity.get("fingerprintCount") != len(PUBLIC_FINGERPRINT_NAMES)
    ):
        failures.append("resume source parity acceptance is invalid")

    manifest_text_path = source / "manifest-sha256.txt"
    if (
        not manifest_text_path.is_file()
        or manifest_text_path.read_text(encoding="utf-8").strip()
        != args.expected_manifest_sha256
    ):
        failures.append("resume source manifest-sha256.txt is invalid")
    manifest_sum_path = package / "manifest.sha256"
    if (
        not manifest_sum_path.is_file()
        or manifest_sum_path.read_text(encoding="utf-8").split()[0]
        != args.expected_manifest_sha256
    ):
        failures.append("resume source package manifest checksum is invalid")

    def check(relative, expected, label):
        path = package / relative
        if not path.is_file():
            failures.append(f"resume package file is missing: {label}")
            return
        actual = sha256_path(path)
        checked_files[str(relative)] = actual
        if actual != expected:
            failures.append(f"resume package hash drift: {label}")

    check("objects.tsv", manifest.get("expectedObjectsSha256", ""), "objects")
    check("drop.sql", manifest.get("dropSqlSha256", ""), "drop SQL")
    check(
        "rollback-canonical/rollback-all.sql",
        manifest.get("rollbackAllSha256", ""),
        "canonical rollback-all",
    )
    check(
        "catalog/columns.csv",
        manifest.get("columnCatalogSha256", ""),
        "column catalog",
    )
    check(
        "catalog/signature.csv",
        manifest.get("catalogSignature", {}).get("sha256", ""),
        "exact catalog signature",
    )
    check(
        "catalog/signature-metadata.json",
        manifest.get("catalogMetadataSha256", ""),
        "exact catalog metadata",
    )
    check(
        "catalog/query.sql",
        manifest.get("catalogSignature", {}).get("querySha256", ""),
        "catalog query",
    )
    check(
        "catalog-expected.sql",
        manifest.get("catalogExpectedSqlSha256", ""),
        "exact catalog expected SQL",
    )
    check(
        "catalog-assert.sql",
        manifest.get("catalogAssertSqlSha256", ""),
        "exact catalog assertion SQL",
    )
    check(
        "catalog/rollback-signature.csv",
        manifest.get("rollbackCatalogSignature", {}).get("sha256", ""),
        "rollback-canonical catalog signature",
    )
    check(
        "catalog/rollback-signature-metadata.json",
        manifest.get("rollbackCatalogMetadataSha256", ""),
        "rollback-canonical catalog metadata",
    )
    check(
        "catalog-rollback-expected.sql",
        manifest.get("rollbackCatalogExpectedSqlSha256", ""),
        "rollback-canonical expected SQL",
    )
    check(
        "catalog-rollback-assert.sql",
        manifest.get("rollbackCatalogAssertSqlSha256", ""),
        "rollback-canonical assertion SQL",
    )
    check(
        "retained-data.tsv",
        manifest.get("retainedDataSpecSha256", ""),
        "retained-data specification",
    )
    check(
        "retained-expected.sql",
        manifest.get("retainedExpectedSqlSha256", ""),
        "retained expected SQL",
    )
    check(
        "retained-assert.sql",
        manifest.get("retainedAssertSqlSha256", ""),
        "retained assertion SQL",
    )
    check(
        "public-fingerprints.tsv",
        manifest.get("fingerprintSpecSha256", ""),
        "fingerprint specification",
    )
    check(
        "parity-acceptance.json",
        parity.get("sha256", ""),
        "parity acceptance",
    )
    for row in manifest.get("retainedCaptureSql", []):
        check(
            f"retained-capture/{row.get('name', '')}",
            row.get("sha256", ""),
            f"retained capture {row.get('name', '')}",
        )
    for row in manifest.get("retainedData", []):
        check(
            f"retained-data/{row.get('canonical_file', '')}",
            row.get("sha256", ""),
            f"retained payload {row.get('canonical_file', '')}",
        )
    for row in manifest.get("rollbackHashes", []):
        kind = row.get("kind", "")
        name = row.get("name", "")
        if kind == "generated-family":
            relative = f"rollback-canonical/{name}"
        elif kind == "generated-data":
            relative = f"rollback-data/{name}"
        elif kind == "generated-all":
            relative = "rollback-canonical/rollback-all.sql"
        else:
            continue
        check(relative, row.get("sha256", ""), f"{kind} {name}")

    failed_path = source / "FAILED.txt"
    failed_text = (
        failed_path.read_text(encoding="utf-8")
        if failed_path.is_file()
        else ""
    )
    if (
        "status=failed" not in failed_text
        or "stage=post-drop initializer" not in failed_text
    ):
        failures.append("resume source is not a post-drop initializer failure")
    committed = (
        (source / "committed-families.txt")
        .read_text(encoding="utf-8")
        .splitlines()
        if (source / "committed-families.txt").is_file()
        else []
    )
    if committed != FAMILY_ORDER:
        failures.append("resume source does not record all four committed families")
    process_rows = read_csv(source / "post" / "drop-process-status.csv")
    if (
        len(process_rows) != 1
        or process_rows[0].get("process_status") == "0"
        or process_rows[0].get("reconciliation_state") != "committed"
        or not re.fullmatch(r"[1-9]\d*", process_rows[0].get("attempts", ""))
    ):
        failures.append("resume source drop reconciliation is not committed")
    control_rows = read_csv(source / "post" / "drop-process-control.csv")
    control = control_rows[0] if len(control_rows) == 1 else {}
    if (
        control.get("state") != "completed"
        or control.get("sql_released") != "true"
        or control.get("local_wait_completed") != "true"
        or control.get("sql_streamer_wait_completed") != "true"
    ):
        failures.append("resume source destructive process control is incomplete")

    try:
        objects = read_objects(package / "objects.tsv")
    except (OSError, ValueError) as exc:
        failures.append(f"resume source object allowlist is invalid: {exc}")
        objects = []
    if objects:
        validate_absent_inventory(
            source / "post" / "relations.csv",
            objects,
            "resume source post-drop",
            failures,
        )
        validate_absent_inventory(
            source / "post" / "drop-reconciliation-1.csv",
            objects,
            "resume source reconciliation",
            failures,
        )

    result = {
        "schemaVersion": 1,
        "success": not failures,
        "sourceEvidence": str(source),
        "acceptedManifestSha256": args.expected_manifest_sha256,
        "committedFamilies": committed,
        "reconciliation": process_rows[0] if len(process_rows) == 1 else {},
        "checkedPackageFiles": sorted(
            (
                {"path": path, "sha256": sha256}
                for path, sha256 in checked_files.items()
            ),
            key=lambda row: row["path"],
        ),
        "failures": sorted(set(failures)),
    }
    Path(args.output).write_text(
        json.dumps(result, sort_keys=True, indent=2) + "\n",
        encoding="utf-8",
    )
    if failures:
        for failure in result["failures"]:
            print(f"ERROR: {failure}", file=sys.stderr)
        return 3
    return 0


def validate_committed_resume_copied_package(args):
    package = Path(args.package)
    source_validation = json.loads(
        Path(args.source_validation).read_text(encoding="utf-8")
    )
    failures = []
    if (
        source_validation.get("success") is not True
        or source_validation.get("acceptedManifestSha256")
        != args.expected_manifest_sha256
    ):
        failures.append("source validation is not approved for package copy")
    manifest_path = package / "manifest.json"
    manifest_sha = (
        sha256_path(manifest_path) if manifest_path.is_file() else ""
    )
    if manifest_sha != args.expected_manifest_sha256:
        failures.append("copied resume manifest hash differs from approval")
    checked_rows = source_validation.get("checkedPackageFiles", [])
    if not checked_rows:
        failures.append("source validation has no package file inventory")
    if len({row.get("path", "") for row in checked_rows}) != len(checked_rows):
        failures.append("source validation package inventory has duplicates")
    copied_files = []
    for row in checked_rows:
        relative = row.get("path", "")
        expected = row.get("sha256", "")
        path = package / relative
        actual = sha256_path(path) if path.is_file() else ""
        copied_files.append({
            "path": relative,
            "expectedSha256": expected,
            "actualSha256": actual,
            "verified": actual == expected,
        })
        if actual != expected:
            failures.append(f"copied resume package drift: {relative}")
    result = {
        "schemaVersion": 1,
        "success": not failures,
        "acceptedManifestSha256": args.expected_manifest_sha256,
        "measuredManifestSha256": manifest_sha,
        "checkedPackageFiles": sorted(
            copied_files,
            key=lambda row: row["path"],
        ),
        "failures": sorted(set(failures)),
    }
    Path(args.output).write_text(
        json.dumps(result, sort_keys=True, indent=2) + "\n",
        encoding="utf-8",
    )
    if failures:
        for failure in result["failures"]:
            print(f"ERROR: {failure}", file=sys.stderr)
        return 3
    return 0


def validate_committed_resume_tooling(args):
    manifest = json.loads(Path(args.manifest).read_text(encoding="utf-8"))
    repository_root = Path(args.repository_root)
    expected_operator_hashes = {
        "tools/postgres-retired-schema-cleanup.sh":
            args.expected_orchestrator_sha256,
        "tools/postgres-retired-schema-cleanup.py":
            args.expected_helper_sha256,
    }
    failures = []
    if sha256_path(args.manifest) != args.expected_manifest_sha256:
        failures.append("resume tooling manifest hash differs from approval")
    rows = []
    source_tooling = {
        row.get("name", ""): row.get("sha256", "")
        for row in manifest.get("toolingHashes", [])
    }
    if set(expected_operator_hashes) - set(source_tooling):
        failures.append("source manifest lacks resume orchestrator/helper hashes")
    for name, source_sha in sorted(source_tooling.items()):
        path = repository_root / name
        actual_sha = sha256_path(path) if path.is_file() else ""
        mode = "source-manifest-exact"
        accepted_sha = source_sha
        if name in expected_operator_hashes:
            mode = "operator-approved-current"
            accepted_sha = expected_operator_hashes[name]
        if not re.fullmatch(r"[0-9a-f]{64}", accepted_sha):
            failures.append(f"resume tooling approval hash is invalid: {name}")
        if actual_sha != accepted_sha:
            failures.append(f"resume tooling drift: {name}")
        rows.append({
            "name": name,
            "sourceManifestSha256": source_sha,
            "acceptedSha256": accepted_sha,
            "actualSha256": actual_sha,
            "mode": mode,
            "matchesSourceManifest": actual_sha == source_sha,
            "accepted": actual_sha == accepted_sha,
        })
    required_capture_artifacts = {
        "tools/postgres-capacity-guard.sh",
        "tools/sql/postgres-retired-schema-cleanup/capture-preflight.sql",
        "tools/sql/postgres-retired-schema-cleanup/capture-relations.sql",
        "tools/sql/postgres-retired-schema-cleanup/capture-target-attestation.sql",
        "tools/sql/postgres-retired-schema-cleanup/catalog-signature-query.sql",
        "tools/sql/postgres-retired-schema-cleanup/objects.tsv",
        "tools/sql/postgres-retired-schema-cleanup/public-fingerprints.tsv",
        "tools/sql/postgres-retired-schema-cleanup/retained-data.tsv",
    }
    if not required_capture_artifacts.issubset(source_tooling):
        failures.append("source manifest lacks required resume capture tooling")
    result = {
        "schemaVersion": 1,
        "success": not failures,
        "sourceManifestSha256": sha256_path(args.manifest),
        "tooling": rows,
        "failures": sorted(set(failures)),
    }
    Path(args.output).write_text(
        json.dumps(result, sort_keys=True, indent=2) + "\n",
        encoding="utf-8",
    )
    if failures:
        for failure in result["failures"]:
            print(f"ERROR: {failure}", file=sys.stderr)
        return 3
    return 0


def validate_committed_resume_gate(args):
    manifest = json.loads(Path(args.manifest).read_text(encoding="utf-8"))
    gate = Path(args.gate_dir)
    objects = read_objects(args.objects)
    failures = []
    validate_absent_inventory(
        gate / "relations.csv",
        objects,
        "committed resume gate",
        failures,
    )
    preflight_rows = read_csv(gate / "preflight.csv")
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
            failures.append(f"committed resume publication drift: {key}")
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
            failures.append(f"committed resume gate failed: {key}")
    if preflight.get("worker_status", "").lower() not in {"absent", "offline"}:
        failures.append("committed resume worker ledger is not offline")

    target_rows = read_csv(gate / "production-target-attestation.csv")
    expected_target = manifest.get("productionDatabaseTarget", {}).get(
        "runtime",
        {},
    )
    if len(target_rows) != 1 or target_rows[0] != expected_target:
        failures.append("committed resume production target drift")
    container_config_path = gate / "container-config-attestation.json"
    container_config = (
        json.loads(container_config_path.read_text(encoding="utf-8"))
        if container_config_path.is_file()
        else {}
    )
    if container_config != manifest.get("containerConfigAttestation"):
        failures.append("committed resume container configuration drift")
    containers = {
        row.get("service"): row
        for row in read_csv(gate / "containers.csv")
    }
    expected_containers = {
        row.get("service"): row for row in manifest.get("containers", [])
    }
    for service in ["postgres", "fstservice", "festivalweb"]:
        actual = containers.get(service, {})
        expected = expected_containers.get(service, {})
        if (
            actual.get("container_id") != expected.get("container_id")
            or actual.get("image_id") != expected.get("image_id")
            or actual.get("state") != "running"
            or actual.get("health") != "healthy"
        ):
            failures.append(f"committed resume container drift: {service}")
    worker = containers.get("fstworker", {})
    expected_worker = expected_containers.get("fstworker", {})
    if (
        worker.get("container_id") != expected_worker.get("container_id")
        or worker.get("image_id") != expected_worker.get("image_id")
        or worker.get("state", "").lower()
        not in {"created", "dead", "exited", "missing"}
        or worker.get("restart_policy", "").lower() != "no"
    ):
        failures.append("committed resume worker container drift")

    health_rows = read_csv(gate / "health.csv")
    if not health_rows or any(
        row.get("status") != "ok" for row in health_rows
    ):
        failures.append("committed resume health gate failed")
    capacity_report, capacity_policy = validate_capacity_evidence(
        gate,
        failures,
    )
    if capacity_policy != manifest.get("capacityGuardPolicy"):
        failures.append("committed resume capacity policy drift")
    fingerprint_rows = [
        row
        for row in read_csv(gate / "fingerprints.csv")
        if bool_value(row.get("gate", "false"))
    ]
    if stable_rows(fingerprint_rows) != manifest.get("fingerprints"):
        failures.append("committed resume public fingerprint drift")
    storage_rows = read_csv(gate / "storage.csv")
    storage = storage_rows[0] if len(storage_rows) == 1 else {}
    if storage.get("target_total_bytes", "") != "0":
        failures.append("committed resume target relations consume bytes")
    storage_identity = {
        key: storage.get(key, "")
        for key in [
            "pgdata_source",
            "filesystem_source",
            "filesystem_type",
            "evidence_root",
            "on_fst_drive",
        ]
    }
    if storage_identity != manifest.get("storageIdentity"):
        failures.append("committed resume storage identity drift")

    result = {
        "schemaVersion": 1,
        "success": not failures,
        "phase": args.phase,
        "acceptedManifestSha256": args.expected_manifest_sha256,
        "capacityDecision": capacity_report.get("decision"),
        "failures": sorted(set(failures)),
    }
    Path(args.output).write_text(
        json.dumps(result, sort_keys=True, indent=2) + "\n",
        encoding="utf-8",
    )
    if failures:
        for failure in result["failures"]:
            print(f"ERROR: {failure}", file=sys.stderr)
        return 3
    return 0


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
        if not re.fullmatch(
            r"[0-9a-f]{64}",
            control.get("second_gate_sha256", ""),
        ):
            failures.append("complete second live gate is not process-bound")
        else:
            second_gate_path = (
                post_dir.parent
                / "post-scratch-live-gate"
                / "validation.json"
            )
            if (
                not second_gate_path.is_file()
                or sha256_path(second_gate_path)
                != control.get("second_gate_sha256")
            ):
                failures.append("complete second live gate evidence hash drift")
            else:
                second_gate = json.loads(
                    second_gate_path.read_text(encoding="utf-8")
                )
                if (
                    second_gate.get("success") is not True
                    or second_gate.get("acceptedManifestSha256")
                    != sha256_path(args.before_manifest)
                ):
                    failures.append(
                        "complete second live gate did not bind accepted manifest"
                    )
        for key in ["sql_streamer_pid", "sql_streamer_start_ticks"]:
            if not re.fullmatch(r"\d+", control.get(key, "")):
                failures.append(f"immutable SQL streamer {key} is invalid")
        if not re.fullmatch(
            r"[0-9a-f]{64}",
            control.get("sql_streamer_cmd_sha256", ""),
        ):
            failures.append("immutable SQL streamer command identity is invalid")
        if control.get("sql_streamer_wait_completed") != "true":
            failures.append("immutable SQL streamer was not waited")
        immutable_proof_path = post_dir / "drop-sql-stream-proof.json"
        if not re.fullmatch(
            r"[0-9a-f]{64}",
            control.get("immutable_bundle_sha256", ""),
        ):
            failures.append("immutable manifest/SQL proof is not process-bound")
        elif (
            not immutable_proof_path.is_file()
            or sha256_path(immutable_proof_path)
            != control.get("immutable_bundle_sha256")
        ):
            failures.append("immutable manifest/SQL proof hash drift")
        else:
            immutable_proof = json.loads(
                immutable_proof_path.read_text(encoding="utf-8")
            )
            proof_manifest = immutable_proof.get("manifest", {})
            manifest_service_rows = [
                row
                for row in manifest.get("containers", [])
                if row.get("service") == "fstservice"
            ]
            manifest_service = (
                manifest_service_rows[0]
                if len(manifest_service_rows) == 1
                else {}
            )
            manifest_postgres_rows = [
                row
                for row in manifest.get("containers", [])
                if row.get("service") == "postgres"
            ]
            manifest_postgres = (
                manifest_postgres_rows[0]
                if len(manifest_postgres_rows) == 1
                else {}
            )
            expected_networks = sorted(
                manifest.get("containerConfigAttestation", {}).get(
                    "services",
                    {},
                ).get("fstservice", {}).get("networks", {})
            )
            expected_target = manifest.get(
                "productionDatabaseTarget",
                {},
            ).get("runtime", {})
            if (
                proof_manifest.get("sha256")
                != sha256_path(args.before_manifest)
                or immutable_proof.get("dropSql", {}).get("sha256")
                != manifest.get("dropSqlSha256")
                or proof_manifest.get("approvedFstserviceImageId")
                != manifest_service.get("image_id")
                or proof_manifest.get("approvedFstserviceContainerId")
                != manifest_service.get("container_id")
                or proof_manifest.get(
                    "approvedFstserviceImageReferenceSha256"
                )
                != manifest.get("productionComposeOwnership", {}).get(
                    "fstserviceImageReferenceSha256"
                )
                or proof_manifest.get("approvedFstserviceNetworks")
                != expected_networks
                or proof_manifest.get("approvedPostgresContainerId")
                != manifest_postgres.get("container_id")
                or proof_manifest.get("approvedPostgresSystemIdentifier")
                != expected_target.get("system_identifier")
                or proof_manifest.get("approvedProductionDatabaseTarget")
                != expected_target
            ):
                failures.append(
                    "immutable manifest/SQL proof did not bind accepted bytes"
                )
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
    startup_image_rows = read_csv(
        post_dir / "startup-image-attestation.csv"
    )
    expected_service_rows = [
        row
        for row in manifest.get("containers", [])
        if row.get("service") == "fstservice"
    ]
    prestart_path = post_dir / "startup-image-prestart-attestation.json"
    creation_path = post_dir / "startup-image-creation-attestation.json"
    routing_dir = post_dir / "startup-database-routing"
    routing_path = routing_dir / "attestation.json"
    routing_target_path = routing_dir / "production-target-attestation.csv"
    routing_status_path = routing_dir / "status.csv"
    release_path = post_dir / "startup-initializer-release.json"
    if (
        len(startup_image_rows) != 1
        or len(expected_service_rows) != 1
        or not prestart_path.is_file()
        or not creation_path.is_file()
        or not routing_path.is_file()
        or not routing_target_path.is_file()
        or not routing_status_path.is_file()
        or not release_path.is_file()
    ):
        failures.append("startup image attestation is missing")
    else:
        startup_image = startup_image_rows[0]
        expected_service = expected_service_rows[0]
        expected_image = expected_service.get("image_id", "")
        expected_source_container = expected_service.get("container_id", "")
        expected_reference_sha256 = manifest.get(
            "productionComposeOwnership",
            {},
        ).get("fstserviceImageReferenceSha256", "")
        expected_networks = sorted(
            manifest.get("containerConfigAttestation", {}).get(
                "services",
                {},
            ).get("fstservice", {}).get("networks", {})
        )
        expected_target = manifest.get(
            "productionDatabaseTarget",
            {},
        ).get("runtime", {})
        expected_postgres_container = expected_target.get("container_id", "")
        expected_system_identifier = expected_target.get(
            "system_identifier",
            "",
        )
        expected_pin = {
            "host": expected_target.get("configured_host", ""),
            "ipAddress": startup_image_rows[0].get(
                "pinned_database_ip",
                "",
            ),
            "network": startup_image_rows[0].get(
                "pinned_database_network",
                "",
            ),
            "postgresContainerId": expected_postgres_container,
            "extraHost": (
                f"{expected_target.get('configured_host', '')}:"
                f"{startup_image_rows[0].get('pinned_database_ip', '')}"
            ),
        }
        prestart = json.loads(prestart_path.read_text(encoding="utf-8"))
        creation = json.loads(creation_path.read_text(encoding="utf-8"))
        routing = json.loads(routing_path.read_text(encoding="utf-8"))
        release = json.loads(release_path.read_text(encoding="utf-8"))
        routing_target_rows = read_csv(routing_target_path)
        routing_status_rows = read_csv(routing_status_path)
        expected_command_sha256 = hashlib.sha256(
            b'["--initialize-schema-only"]'
        ).hexdigest()
        marker_path = post_dir / "startup-image-attested-before-start.sha256"
        marker = (
            marker_path.read_text(encoding="utf-8").strip()
            if marker_path.is_file()
            else ""
        )
        if (
            startup_image.get("manifest_image_id") != expected_image
            or startup_image.get("source_container_id")
            != expected_source_container
            or startup_image.get("compose_image_reference_sha256")
            != expected_reference_sha256
            or startup_image.get("compose_image_id_before_create")
            != expected_image
            or startup_image.get("compose_image_id_before_start")
            != expected_image
            or startup_image.get("prestart_actual_image_id")
            != expected_image
            or startup_image.get("prestart_config_image") != expected_image
            or startup_image.get("prestart_state") != "created"
            or startup_image.get("prestart_running") != "false"
            or startup_image.get("prestart_command_sha256")
            != expected_command_sha256
            or startup_image.get("pinned_database_host")
            != expected_pin["host"]
            or not re.fullmatch(
                r"(?:\d{1,3}\.){3}\d{1,3}",
                startup_image.get("pinned_database_ip", ""),
            )
            or startup_image.get("pinned_database_network")
            not in expected_networks
            or startup_image.get("pinned_postgres_container_id")
            != expected_postgres_container
            or startup_image.get("poststart_actual_image_id")
            != expected_image
            or startup_image.get("poststart_config_image") != expected_image
            or startup_image.get("exit_code") != "0"
            or not re.fullmatch(
                r"[0-9a-f]{64}",
                startup_image.get("container_id", ""),
            )
            or not re.fullmatch(
                r"[0-9a-f]{64}",
                startup_image.get("creation_attestation_sha256", ""),
            )
            or not re.fullmatch(
                r"[0-9a-f]{64}",
                startup_image.get("prestart_attestation_sha256", ""),
            )
            or startup_image.get("creation_attestation_sha256")
            != sha256_path(creation_path)
            or startup_image.get("prestart_attestation_sha256")
            != sha256_path(prestart_path)
            or startup_image.get("database_routing_attestation_sha256")
            != sha256_path(routing_path)
            or startup_image.get("database_target_attestation_sha256")
            != sha256_path(routing_target_path)
            or startup_image.get("initializer_release_sha256")
            != sha256_path(release_path)
            or marker != sha256_path(prestart_path)
        ):
            failures.append("startup initializer image identity drift")
        for attestation, phase in [
            (creation, "created-prestart"),
            (prestart, "attested-before-start"),
        ]:
            state = attestation.get("state", {})
            if (
                attestation.get("schemaVersion") != 1
                or attestation.get("phase") != phase
                or attestation.get("expectedManifestSha256")
                != sha256_path(args.before_manifest)
                or attestation.get("sourceContainerId")
                != expected_source_container
                or attestation.get("containerId")
                != startup_image.get("container_id")
                or attestation.get("expectedImageId") != expected_image
                or attestation.get("composeImageReferenceSha256")
                != expected_reference_sha256
                or attestation.get("composeImageResolvedId")
                != expected_image
                or attestation.get("actualImageId") != expected_image
                or attestation.get("configuredImage") != expected_image
                or attestation.get("networks") != expected_networks
                or attestation.get("databaseHostPin") != expected_pin
                or expected_target.get("configured_host", "")
                in attestation.get("networkResolutionNames", [])
                or state.get("status") != "created"
                or state.get("running") is not False
                or state.get("pid") != 0
                or attestation.get("commandSha256")
                != expected_command_sha256
                or attestation.get("sourceConfigurationSha256")
                != attestation.get("actualConfigurationSha256")
                or attestation.get("autoRemove") is not False
                or attestation.get("restartPolicy") != "no"
                or attestation.get("portBindingsPresent") is not False
                or "fstservice" in attestation.get("networkAliases", [])
            ):
                failures.append(
                    f"startup initializer {phase} attestation drift"
                )
        expected_routing_target = {
            "configured_host": expected_target.get("configured_host", ""),
            "configured_port": expected_target.get("configured_port", ""),
            "configured_database": expected_target.get(
                "configured_database",
                "",
            ),
            "configured_user": expected_target.get("configured_user", ""),
            "container_id": expected_postgres_container,
            "runtime_address": "local-socket",
            "runtime_port": expected_target.get("runtime_port", ""),
            "runtime_database": expected_target.get("runtime_database", ""),
            "runtime_user": expected_target.get("runtime_user", ""),
            "in_recovery": "false",
            "system_identifier": expected_system_identifier,
        }
        routing_owners = routing.get("aliasOwners", {})
        routing_endpoints = routing.get("postgres", {}).get("endpoints", {})
        if (
            routing.get("schemaVersion") != 1
            or routing.get("success") is not True
            or routing.get("acceptedManifestSha256")
            != sha256_path(args.before_manifest)
            or routing.get("startupContainerId")
            != startup_image.get("container_id")
            or routing.get("attachedNetworks") != expected_networks
            or routing.get("databaseTarget") != expected_routing_target
            or routing.get("databaseHostPin") != expected_pin
            or routing.get("postgres", {}).get("containerId")
            != expected_postgres_container
            or routing.get("postgres", {}).get("systemIdentifier")
            != expected_system_identifier
            or sorted(routing_owners) != expected_networks
            or sorted(routing_endpoints) != expected_networks
            or any(
                len(routing_owners.get(network, [])) != 1
                or routing_owners[network][0].get("containerId")
                != expected_postgres_container
                or routing_owners[network][0].get("state") != "running"
                or routing_owners[network][0].get("running") is not True
                or not routing_owners[network][0].get("resolutionSources")
                or expected_target.get("configured_host", "")
                not in routing_owners[network][0].get(
                    "resolutionNames",
                    [],
                )
                or routing_endpoints[network].get("hostAliasPresent")
                is not True
                for network in expected_networks
            )
            or routing_endpoints.get(
                expected_pin["network"],
                {},
            ).get("ipAddress") != expected_pin["ipAddress"]
            or len(routing_target_rows) != 1
            or routing_target_rows[0] != expected_target
            or len(routing_status_rows) != 1
            or routing_status_rows[0].get("status") != "passed"
            or routing_status_rows[0].get("manifest_sha256")
            != sha256_path(args.before_manifest)
            or (post_dir / "startup-database-routing-failure.csv").exists()
        ):
            failures.append("startup database routing attestation drift")
        if (
            release.get("schemaVersion") != 1
            or release.get("acceptedManifestSha256")
            != sha256_path(args.before_manifest)
            or release.get("containerId")
            != startup_image.get("container_id")
            or release.get("prestartAttestationSha256")
            != sha256_path(prestart_path)
            or release.get("databaseRoutingAttestationSha256")
            != sha256_path(routing_path)
            or release.get("databaseTargetAttestationSha256")
            != sha256_path(routing_target_path)
            or release.get("released") is not True
        ):
            failures.append("startup initializer release evidence drift")

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
    post_container_config_path = (
        post_dir / "container-config-attestation.json"
    )
    post_container_config = (
        json.loads(
            post_container_config_path.read_text(encoding="utf-8")
        )
        if post_container_config_path.is_file()
        else {}
    )
    if post_container_config != manifest.get(
        "containerConfigAttestation"
    ):
        failures.append("post-action actual container configuration changed")

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
    post_capacity_report, post_capacity_policy = validate_capacity_evidence(
        post_dir,
        failures,
    )
    if post_capacity_policy != manifest.get("capacityGuardPolicy"):
        failures.append("post-action capacity policy changed")
    elif sha256_path(
        post_dir / "capacity-guard.policy.json"
    ) != manifest.get("capacityGuardPolicySha256"):
        failures.append("post-action capacity policy hash changed")
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


def validate_complete_live_gate(args):
    manifest_path = Path(args.manifest)
    manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
    gate = Path(args.gate_dir)
    failures = []
    if sha256_path(manifest_path) != args.expected_manifest_sha256:
        failures.append("accepted manifest hash changed")

    csv_bindings = [
        ("relations.csv", "relations"),
        ("partition-children.csv", "partitionChildren"),
        ("incoming-inheritance.csv", "incomingInheritance"),
        ("owned-objects.csv", "ownedObjects"),
        ("unexpected-relations.csv", "unexpectedRelations"),
        ("external-dependencies.csv", "externalDependencies"),
        ("containers.csv", "containers"),
        ("health.csv", "health"),
        ("retained-data.csv", "retainedData"),
    ]
    stable_state = {}
    for filename, manifest_key in csv_bindings:
        actual = stable_rows(read_csv(gate / filename))
        expected = manifest.get(manifest_key, [])
        stable_state[manifest_key] = actual
        if actual != expected:
            failures.append(f"complete live gate drift: {manifest_key}")

    preflight_rows = read_csv(gate / "preflight.csv")
    preflight = preflight_rows[0] if len(preflight_rows) == 1 else {}
    stable_state["preflight"] = preflight
    if preflight != manifest.get("preflight"):
        failures.append("complete live gate preflight drift")

    target_rows = read_csv(gate / "production-target-attestation.csv")
    target = target_rows[0] if len(target_rows) == 1 else {}
    stable_state["productionDatabaseTarget"] = target
    if target != manifest.get("productionDatabaseTarget", {}).get("runtime"):
        failures.append("complete live gate production target drift")
    container_config_path = gate / "container-config-attestation.json"
    container_config = (
        json.loads(container_config_path.read_text(encoding="utf-8"))
        if container_config_path.is_file()
        else {}
    )
    stable_state["containerConfigAttestation"] = container_config
    if container_config != manifest.get("containerConfigAttestation"):
        failures.append("complete live gate container configuration drift")

    columns_path = gate / "catalog" / "columns.csv"
    column_hash = sha256_path(columns_path) if columns_path.is_file() else ""
    stable_state["columnCatalogSha256"] = column_hash
    if column_hash != manifest.get("columnCatalogSha256"):
        failures.append("complete live gate column catalog drift")

    metadata_path = gate / "catalog" / "signature-metadata.json"
    metadata = (
        json.loads(metadata_path.read_text(encoding="utf-8"))
        if metadata_path.is_file()
        else {}
    )
    stable_state["catalogSignature"] = metadata
    if metadata != manifest.get("catalogSignature"):
        failures.append("complete live gate catalog signature drift")

    capacity_report, capacity_policy = validate_capacity_evidence(
        gate,
        failures,
    )
    stable_state["capacityGuardPolicy"] = capacity_policy
    stable_state["capacityGuardDecision"] = capacity_report.get("decision")
    if capacity_policy != manifest.get("capacityGuardPolicy"):
        failures.append("complete live gate capacity policy drift")
    if sha256_path(gate / "capacity-guard.policy.json") != manifest.get(
        "capacityGuardPolicySha256"
    ):
        failures.append("complete live gate capacity policy hash drift")

    fingerprint_rows = [
        row
        for row in read_csv(gate / "fingerprints.csv")
        if bool_value(row.get("gate", "false"))
    ]
    fingerprints = stable_rows(fingerprint_rows)
    stable_state["fingerprints"] = fingerprints
    if fingerprints != manifest.get("fingerprints"):
        failures.append("complete live gate public fingerprint drift")

    storage_rows = read_csv(gate / "storage.csv")
    storage = storage_rows[0] if len(storage_rows) == 1 else {}
    storage_identity = {
        key: storage.get(key, "")
        for key in [
            "pgdata_source",
            "filesystem_source",
            "filesystem_type",
            "evidence_root",
            "on_fst_drive",
        ]
    }
    stable_state["storageIdentity"] = storage_identity
    if storage_identity != manifest.get("storageIdentity"):
        failures.append("complete live gate storage identity drift")

    stable_text = json.dumps(
        stable_state,
        sort_keys=True,
        separators=(",", ":"),
        ensure_ascii=False,
    )
    result = {
        "schemaVersion": 1,
        "success": not failures,
        "acceptedManifestSha256": args.expected_manifest_sha256,
        "stableStateSha256": hashlib.sha256(
            stable_text.encode("utf-8")
        ).hexdigest(),
        "failures": sorted(set(failures)),
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
        nonsecret_hashes = {}
        secret_names = []
        sensitive_name = re.compile(
            r"(^|__|[_-])(password|passwd|secret|token|credential|"
            r"private|certificate|cert|api[_-]?key|ssh[_-]?key)"
            r"($|__|[_-])",
            re.IGNORECASE,
        )
        if isinstance(environment, dict):
            items = environment.items()
        else:
            parsed = []
            for item in sequence(environment):
                text = str(item)
                name, separator, raw_value = text.partition("=")
                parsed.append((name.strip(), raw_value if separator else ""))
            items = parsed
        for name, raw_value in items:
            name = str(name)
            raw_value = "" if raw_value is None else str(raw_value)
            names.append(name)
            references.extend(
                match.group(0) for match in target_pattern.finditer(raw_value)
            )
            if (
                sensitive_name.search(name)
                or name.casefold() == "connectionstrings__postgresql"
            ):
                secret_names.append(name)
            else:
                nonsecret_hashes[name] = hashlib.sha256(
                    raw_value.encode("utf-8")
                ).hexdigest()
        return {
            "names": sorted(set(names)),
            "retiredValueReferences": sorted(set(references)),
            "nonSecretValueSha256": dict(sorted(nonsecret_hashes.items())),
            "secretConfiguredNames": sorted(set(secret_names)),
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
        tokens = [str(token) for token in sequence(command)]
        for text in tokens:
            if text.startswith("--"):
                flags.append(text.split("=", 1)[0])
            references.extend(
                match.group(0) for match in target_pattern.finditer(text)
            )
        return {
            "present": command is not None,
            "argvSha256": hashlib.sha256(
                json.dumps(tokens, separators=(",", ":")).encode("utf-8")
            ).hexdigest(),
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
        if isinstance(labels, dict):
            label_map = {
                str(name): "" if raw_value is None else str(raw_value)
                for name, raw_value in labels.items()
            }
        else:
            label_map = {}
            for item in sequence(labels):
                name, separator, raw_value = str(item).partition("=")
                if separator:
                    label_map[name] = raw_value
        ports = []
        for port in sequence(service.get("ports")):
            if isinstance(port, dict):
                ports.append(
                    {
                        "target": int(port.get("target")),
                        "published": str(port.get("published")),
                        "protocol": str(port.get("protocol") or "tcp"),
                        "hostIp": str(
                            port.get("host_ip")
                            or port.get("hostIp")
                            or "0.0.0.0"
                        ),
                        "mode": str(port.get("mode") or "ingress"),
                    }
                )
            elif isinstance(port, str):
                parts = port.split(":")
                if len(parts) == 3:
                    host_ip, published, target = parts
                elif len(parts) == 2:
                    host_ip, (published, target) = "0.0.0.0", parts
                else:
                    raise ValueError(
                        f"invalid rendered compose port: {service_name}"
                    )
                target_value, _, protocol = target.partition("/")
                ports.append(
                    {
                        "target": int(target_value),
                        "published": str(published),
                        "protocol": protocol or "tcp",
                        "hostIp": host_ip,
                        "mode": "ingress",
                    }
                )
        ports.sort(
            key=lambda row: (
                row["protocol"],
                row["published"],
                row["target"],
                row["hostIp"],
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
            "labels": {
                "names": sorted(label_map),
                "valueSha256": {
                    name: hashlib.sha256(
                        value.encode("utf-8")
                    ).hexdigest()
                    for name, value in sorted(label_map.items())
                },
            },
            "profiles": sorted(str(item) for item in sequence(
                service.get("profiles")
            )),
            "networks": {
                str(network_name): {
                    "aliases": sorted(
                        str(alias)
                        for alias in (
                            network_config.get("aliases", [])
                            or []
                            if isinstance(network_config, dict)
                            else []
                        )
                    )
                }
                for network_name, network_config in sorted(
                    (
                        service.get("networks")
                        if isinstance(service.get("networks"), dict)
                        else {
                            str(name): {}
                            for name in sequence(service.get("networks"))
                        }
                    ).items()
                )
            },
            "ports": ports,
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
        "networks": {
            str(name): {
                "name": str(
                    (config or {}).get("name") or name
                ),
                "external": bool((config or {}).get("external", False)),
            }
            for name, config in sorted((value.get("networks") or {}).items())
        },
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


def attest_container_config(args):
    compose = json.loads(Path(args.compose).read_text(encoding="utf-8"))
    target_rows = read_csv(args.target_attestation)
    project_rows = read_csv(args.project_containers)
    inspected = json.load(sys.stdin)
    with Path(args.fingerprint_spec).open(
        newline="",
        encoding="utf-8",
    ) as handle:
        fingerprint_rows = list(csv.DictReader(handle, delimiter="\t"))
    failures = []
    required_services = {"postgres", "fstservice", "festivalweb", "fstworker"}
    if not isinstance(inspected, list):
        raise ValueError("docker inspect payload must be a JSON array")

    sensitive_name = re.compile(
        r"(^|__|[_-])(password|passwd|secret|token|credential|"
        r"private|certificate|cert|api[_-]?key|ssh[_-]?key)"
        r"($|__|[_-])",
        re.IGNORECASE,
    )
    sensitive_path = re.compile(
        r"(^|[/_.-])(secrets?|password|passwd|tokens?|credentials?|private|"
        r"certificates?|certs?|api[-_]?keys?|ssh[-_]?keys?)([/_.-]|$)",
        re.IGNORECASE,
    )

    def env_map(values):
        result = {}
        for item in values or []:
            name, separator, raw_value = str(item).partition("=")
            if separator:
                result[name] = raw_value
        return result

    def env_projection(values, expected):
        actual = env_map(values)
        expected_names = expected.get("names", [])
        projection = {
            "names": sorted(actual),
            "requiredNames": expected_names,
            "nonSecretValueSha256": {},
            "secretConfiguredNames": [],
        }
        for name in expected_names:
            if name not in actual:
                failures.append(f"container environment lacks {name}")
                continue
            if (
                sensitive_name.search(name)
                or name.casefold() == "connectionstrings__postgresql"
            ):
                projection["secretConfiguredNames"].append(name)
            else:
                projection["nonSecretValueSha256"][name] = hashlib.sha256(
                    actual[name].encode("utf-8")
                ).hexdigest()
        projection["secretConfiguredNames"].sort()
        projection["nonSecretValueSha256"] = dict(
            sorted(projection["nonSecretValueSha256"].items())
        )
        if (
            projection["nonSecretValueSha256"]
            != expected.get("nonSecretValueSha256", {})
        ):
            failures.append("container nonsecret environment value drift")
        if (
            projection["secretConfiguredNames"]
            != expected.get("secretConfiguredNames", [])
        ):
            failures.append("container secret environment presence drift")
        return projection, actual

    def argv_projection(values):
        tokens = [str(token) for token in values or []]
        return {
            "present": bool(tokens),
            "argvSha256": hashlib.sha256(
                json.dumps(tokens, separators=(",", ":")).encode("utf-8")
            ).hexdigest(),
        }

    def classify_bind(source, target):
        lowered = f"{source} {target}".lower()
        if (
            "/run/secrets" in lowered
            or sensitive_path.search(source)
            or sensitive_path.search(target)
        ):
            return "secret"
        if (
            "/var/lib/postgresql/data" in target
            or "docker.sock" in lowered
            or "/fst-data" in source
            or "postgres-data" in source.lower()
        ):
            return "data"
        return "other"

    project_by_service = {
        row.get("service", ""): row for row in project_rows
    }
    inspect_by_id = {
        item.get("Id", ""): item for item in inspected if item.get("Id")
    }

    expected_networks = compose.get("networks", {})
    service_results = {}
    actual_env_maps = {}
    for service in sorted(required_services):
        project = project_by_service.get(service, {})
        item = inspect_by_id.get(project.get("container_id", ""), {})
        config = item.get("Config") or {}
        host_config = item.get("HostConfig") or {}
        network_settings = item.get("NetworkSettings") or {}
        container_state = str((item.get("State") or {}).get("Status") or "")
        labels = config.get("Labels") or {}
        expected_service = compose.get("services", {}).get(service, {})
        container_id = item.get("Id", "")
        if container_id != project.get("container_id", ""):
            failures.append(f"container ID drift: {service}")
        if labels.get("com.docker.compose.service", "") != service:
            failures.append(f"container service label drift: {service}")
        for label_name, expected_value in {
            "com.docker.compose.service": service,
            "com.docker.compose.project": project.get("project", ""),
            "com.docker.compose.project.working_dir": project.get(
                "working_dir",
                "",
            ),
        }.items():
            if labels.get(label_name, "") != expected_value:
                failures.append(
                    f"container compose label drift: {service}.{label_name}"
                )
        config_files_hash = hashlib.sha256(
            labels.get(
                "com.docker.compose.project.config_files",
                "",
            ).encode("utf-8")
        ).hexdigest()
        if config_files_hash != project.get("config_files_sha256", ""):
            failures.append(f"container compose file label drift: {service}")
        expected_labels = expected_service.get("labels", {})
        actual_label_hashes = {}
        for name in expected_labels.get("names", []):
            if name not in labels:
                failures.append(f"container label missing: {service}.{name}")
                continue
            actual_label_hashes[name] = hashlib.sha256(
                str(labels[name]).encode("utf-8")
            ).hexdigest()
        if actual_label_hashes != expected_labels.get("valueSha256", {}):
            failures.append(f"container label value drift: {service}")

        environment, actual_env = env_projection(
            config.get("Env") or [],
            expected_service.get("environment", {}),
        )
        actual_env_maps[service] = actual_env
        command = argv_projection(config.get("Cmd"))
        entrypoint = argv_projection(config.get("Entrypoint"))
        for name, actual_projection in [
            ("command", command),
            ("entrypoint", entrypoint),
        ]:
            expected_projection = expected_service.get(name, {})
            if expected_projection.get("present") and (
                actual_projection["argvSha256"]
                != expected_projection.get("argvSha256")
            ):
                failures.append(f"container {name} drift: {service}")

        expected_volumes = expected_service.get("volumes", [])
        actual_mounts = []
        for mount in item.get("Mounts") or []:
            mount_type = str(mount.get("Type") or "")
            source = (
                str(mount.get("Name") or "")
                if mount_type == "volume"
                else str(mount.get("Source") or "")
            )
            target = str(mount.get("Destination") or "")
            classification = (
                classify_bind(source, target)
                if mount_type == "bind"
                else "not-bind"
            )
            actual_mounts.append(
                {
                    "type": mount_type,
                    "source": (
                        "<redacted-secret-bind>"
                        if classification == "secret"
                        else source
                    ),
                    "target": (
                        "<redacted-secret-target>"
                        if classification == "secret"
                        else target
                    ),
                    "readOnly": not bool(mount.get("RW", False)),
                }
            )
        actual_mounts.sort(
            key=lambda row: (row["type"], row["target"], row["source"])
        )
        expected_volumes = sorted(
            expected_volumes,
            key=lambda row: (row["type"], row["target"], row["source"]),
        )
        if actual_mounts != expected_volumes:
            failures.append(f"container mount drift: {service}")

        def inspect_port_rows(mapping):
            rows = []
            for container_port, bindings in sorted((mapping or {}).items()):
                target_text, _, protocol = container_port.partition("/")
                for binding in bindings or []:
                    rows.append(
                        {
                            "target": int(target_text),
                            "published": str(
                                binding.get("HostPort") or ""
                            ),
                            "protocol": protocol or "tcp",
                            "hostIp": str(
                                binding.get("HostIp") or "0.0.0.0"
                            ),
                            "mode": "ingress",
                        }
                    )
            return sorted(
                rows,
                key=lambda row: (
                    row["protocol"],
                    row["published"],
                    row["target"],
                    row["hostIp"],
                ),
            )

        expected_ports = expected_service.get("ports", [])
        host_ports = inspect_port_rows(host_config.get("PortBindings"))
        runtime_ports = inspect_port_rows(network_settings.get("Ports"))
        if host_ports != expected_ports:
            failures.append(f"container host port binding drift: {service}")
        if runtime_ports != expected_ports:
            failures.append(f"container runtime port binding drift: {service}")

        actual_networks = {}
        for network_name, network in sorted(
            (network_settings.get("Networks") or {}).items()
        ):
            actual_networks[network_name] = {
                "aliases": sorted(
                    str(alias)
                    for alias in (network.get("Aliases") or [])
                    if alias
                ),
                "ipAddress": str(network.get("IPAddress") or ""),
            }
            if (
                container_state == "running"
                and not actual_networks[network_name]["ipAddress"]
            ):
                failures.append(
                    f"container network lacks IP: {service}.{network_name}"
                )
        expected_service_networks = expected_service.get("networks", {})
        expected_actual_names = {
            expected_networks.get(logical_name, {}).get("name", logical_name)
            for logical_name in expected_service_networks
        }
        if set(actual_networks) != expected_actual_names:
            failures.append(f"container network set drift: {service}")
        for logical_name, expected_network in expected_service_networks.items():
            actual_name = expected_networks.get(logical_name, {}).get(
                "name",
                logical_name,
            )
            aliases = set(actual_networks.get(actual_name, {}).get(
                "aliases",
                [],
            ))
            required_aliases = set(expected_network.get("aliases", []))
            required_aliases.add(service)
            if not required_aliases.issubset(aliases):
                failures.append(
                    f"container network alias drift: {service}.{actual_name}"
                )

        service_results[service] = {
            "containerId": container_id,
            "imageId": str(item.get("Image") or ""),
            "command": command,
            "entrypoint": entrypoint,
            "environment": environment,
            "mounts": actual_mounts,
            "ports": host_ports,
            "networks": actual_networks,
            "composeLabels": {
                "project": labels.get("com.docker.compose.project", ""),
                "service": labels.get("com.docker.compose.service", ""),
                "workingDir": labels.get(
                    "com.docker.compose.project.working_dir",
                    "",
                ),
                "configFilesSha256": config_files_hash,
                "resolvedLabelValueSha256": dict(
                    sorted(actual_label_hashes.items())
                ),
            },
            "restartPolicy": str(
                (host_config.get("RestartPolicy") or {}).get("Name") or ""
            ),
            "state": container_state,
        }

    published_owners = {}
    for service, result in service_results.items():
        for port in result.get("ports", []):
            key = (port["protocol"], port["hostIp"], port["published"])
            if key in published_owners:
                failures.append(
                    f"published port has multiple target owners: {key}"
                )
            published_owners[key] = service

    target_container_ids = {
        row.get("container_id", "") for row in project_rows
    }
    wildcard_ips = {"", "0.0.0.0", "::"}
    for item in inspected:
        if item.get("Id", "") in target_container_ids:
            continue
        other_ports = inspect_port_rows(
            (item.get("HostConfig") or {}).get("PortBindings")
        )
        for other in other_ports:
            for expected_key, owner in published_owners.items():
                protocol, host_ip, published = expected_key
                if (
                    other["protocol"] == protocol
                    and other["published"] == published
                    and (
                        other["hostIp"] == host_ip
                        or other["hostIp"] in wildcard_ips
                        or host_ip in wildcard_ips
                    )
                ):
                    failures.append(
                        f"stale container also owns {host_ip}:{published}/"
                        f"{protocol} for {owner}"
                    )

    target = compose.get("databaseTarget", {})
    connection_targets = {}
    for service in ["fstservice", "fstworker"]:
        connection = actual_env_maps.get(service, {}).get(
            "ConnectionStrings__PostgreSQL",
            "",
        )
        values = {}
        for item in connection.split(";"):
            key, separator, raw_value = item.partition("=")
            if separator:
                values[key.strip().casefold()] = raw_value.strip()
        connection_targets[service] = {
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
                key in values for key in {"password", "pwd"}
            ) or actual_env_maps.get(service, {}).get(
                "ConnectionStrings__PostgreSQLPasswordConfigured",
                "",
            ).casefold() == "true",
        }
        if connection_targets[service] != {
            key: target.get(key)
            for key in [
                "host",
                "port",
                "database",
                "user",
                "passwordConfigured",
            ]
        }:
            failures.append(f"actual database target drift: {service}")

    target_attestation = target_rows[0] if len(target_rows) == 1 else {}
    postgres_networks = service_results.get("postgres", {}).get("networks", {})
    service_networks = service_results.get("fstservice", {}).get("networks", {})
    shared_networks = sorted(set(postgres_networks) & set(service_networks))
    host = target.get("host", "")
    mapped_networks = [
        network
        for network in shared_networks
        if host in postgres_networks.get(network, {}).get("aliases", [])
    ]
    if not mapped_networks:
        failures.append("fstservice database host does not map to postgres")
    if target_attestation.get("container_id") != service_results.get(
        "postgres",
        {},
    ).get("containerId"):
        failures.append("database system identifier container mapping drift")
    system_identifier = target_attestation.get("system_identifier", "")
    if not re.fullmatch(r"\d+", system_identifier):
        failures.append("database system identifier is invalid")
    alias_owners = {}
    for network_name in shared_networks:
        owners = []
        for item in inspected:
            if str((item.get("State") or {}).get("Status") or "") != "running":
                continue
            endpoint = (
                (item.get("NetworkSettings") or {})
                .get("Networks", {})
                .get(network_name)
            )
            if not endpoint:
                continue
            aliases = {
                str(alias)
                for alias in (endpoint.get("Aliases") or [])
                if alias
            }
            if host not in aliases:
                continue
            item_labels = (item.get("Config") or {}).get("Labels") or {}
            owners.append(
                {
                    "containerId": str(item.get("Id") or ""),
                    "containerName": str(
                        item.get("Name") or ""
                    ).lstrip("/"),
                    "composeProject": str(
                        item_labels.get("com.docker.compose.project") or ""
                    ),
                    "composeService": str(
                        item_labels.get("com.docker.compose.service") or ""
                    ),
                    "ipAddress": str(endpoint.get("IPAddress") or ""),
                }
            )
        owners.sort(key=lambda row: row["containerId"])
        alias_owners[network_name] = owners
        if (
            len(owners) != 1
            or owners[0]["containerId"]
            != service_results.get("postgres", {}).get("containerId")
        ):
            failures.append(
                f"database alias ownership is not exclusive: {network_name}"
            )

    fingerprint_bindings = {}
    bases = {}
    for row in fingerprint_rows:
        name = row.get("name", "")
        parsed = urlparse(row.get("url", ""))
        service = "fstservice" if name == "readyz" else "festivalweb"
        if (
            parsed.scheme != "http"
            or parsed.hostname not in {"127.0.0.1", "localhost"}
            or parsed.port is None
        ):
            failures.append(f"fingerprint URL is not locally bound: {name}")
            continue
        candidates = [
            port
            for port in service_results.get(service, {}).get("ports", [])
            if port["published"] == str(parsed.port)
            and port["protocol"] == "tcp"
            and port["hostIp"] in {"127.0.0.1", "::1"}
        ]
        if len(candidates) != 1:
            failures.append(
                f"fingerprint URL does not map to {service}: {name}"
            )
            continue
        binding = candidates[0]
        base = f"http://{parsed.hostname}:{parsed.port}"
        base_kind = "service" if service == "fstservice" else "web"
        if base_kind in bases and bases[base_kind] != base:
            failures.append(f"fingerprint base URL drift: {base_kind}")
        bases[base_kind] = base
        fingerprint_bindings[name] = {
            "service": service,
            "containerId": service_results[service]["containerId"],
            "hostIp": binding["hostIp"],
            "hostPort": binding["published"],
            "containerPort": binding["target"],
            "protocol": binding["protocol"],
            "baseUrl": base,
        }
    if set(fingerprint_bindings) != {
        row.get("name", "") for row in fingerprint_rows
    }:
        failures.append("fingerprint port binding coverage is incomplete")

    result = {
        "schemaVersion": 1,
        "success": not failures,
        "failures": sorted(set(failures)),
        "services": service_results,
        "databaseHostMapping": {
            "consumer": "fstservice",
            "host": host,
            "sharedNetworks": mapped_networks,
            "postgresContainerId": service_results.get("postgres", {}).get(
                "containerId"
            ),
            "postgresSystemIdentifier": system_identifier,
            "aliasOwners": alias_owners,
        },
        "fingerprintBindings": dict(sorted(fingerprint_bindings.items())),
        "fingerprintBaseUrls": dict(sorted(bases.items())),
    }
    Path(args.output).write_text(
        json.dumps(result, sort_keys=True, indent=2) + "\n",
        encoding="utf-8",
    )
    if failures:
        for failure in result["failures"]:
            print(f"ERROR: {failure}", file=sys.stderr)
        return 3
    return 0


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
    manifest.add_argument("--rollback-catalog-signature", required=True)
    manifest.add_argument("--rollback-catalog-metadata", required=True)
    manifest.add_argument(
        "--rollback-catalog-expected-sql",
        required=True,
    )
    manifest.add_argument(
        "--rollback-catalog-assert-sql",
        required=True,
    )
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

    resume_source = subparsers.add_parser(
        "validate-committed-resume-source"
    )
    resume_source.add_argument("--source", required=True)
    resume_source.add_argument(
        "--expected-manifest-sha256",
        required=True,
    )
    resume_source.add_argument("--output", required=True)

    resume_copy = subparsers.add_parser(
        "validate-committed-resume-copied-package"
    )
    resume_copy.add_argument("--package", required=True)
    resume_copy.add_argument("--source-validation", required=True)
    resume_copy.add_argument(
        "--expected-manifest-sha256",
        required=True,
    )
    resume_copy.add_argument("--output", required=True)

    resume_tooling = subparsers.add_parser(
        "validate-committed-resume-tooling"
    )
    resume_tooling.add_argument("--manifest", required=True)
    resume_tooling.add_argument(
        "--expected-manifest-sha256",
        required=True,
    )
    resume_tooling.add_argument("--repository-root", required=True)
    resume_tooling.add_argument(
        "--expected-orchestrator-sha256",
        required=True,
    )
    resume_tooling.add_argument(
        "--expected-helper-sha256",
        required=True,
    )
    resume_tooling.add_argument("--output", required=True)

    resume_gate = subparsers.add_parser(
        "validate-committed-resume-gate"
    )
    resume_gate.add_argument("--manifest", required=True)
    resume_gate.add_argument("--objects", required=True)
    resume_gate.add_argument("--gate-dir", required=True)
    resume_gate.add_argument(
        "--expected-manifest-sha256",
        required=True,
    )
    resume_gate.add_argument("--phase", required=True)
    resume_gate.add_argument("--output", required=True)

    live_gate = subparsers.add_parser("validate-complete-live-gate")
    live_gate.add_argument("--manifest", required=True)
    live_gate.add_argument("--expected-manifest-sha256", required=True)
    live_gate.add_argument("--gate-dir", required=True)
    live_gate.add_argument("--output", required=True)

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
    retained.add_argument("--logical-data", required=True)
    retained.add_argument("--band-data", required=True)

    retained_recheck = subparsers.add_parser(
        "validate-retained-recapture"
    )
    retained_recheck.add_argument("--spec", required=True)
    retained_recheck.add_argument("--column-catalog", required=True)
    retained_recheck.add_argument("--raw-dir", required=True)
    retained_recheck.add_argument("--expected-metadata", required=True)
    retained_recheck.add_argument("--output-dir", required=True)
    retained_recheck.add_argument("--metadata-output", required=True)

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

    rehearsal_capture = subparsers.add_parser(
        "render-rehearsal-capture-sql"
    )
    rehearsal_capture.add_argument("--query", required=True)
    rehearsal_capture.add_argument("--spec", required=True)
    rehearsal_capture.add_argument("--column-catalog", required=True)
    rehearsal_capture.add_argument("--output", required=True)

    rehearsal_parse = subparsers.add_parser(
        "parse-rehearsal-capture"
    )
    rehearsal_parse.add_argument("--input", required=True)
    rehearsal_parse.add_argument("--output-dir", required=True)
    rehearsal_parse.add_argument("--output", required=True)

    catalog = subparsers.add_parser("prepare-catalog-signature")
    catalog.add_argument("--input", required=True)
    catalog.add_argument("--query", required=True)
    catalog.add_argument("--column-catalog", required=True)
    catalog.add_argument("--output", required=True)
    catalog.add_argument("--metadata-output", required=True)
    catalog.add_argument("--expected-sql-output", required=True)
    catalog.add_argument("--assert-sql-output", required=True)
    catalog.add_argument("--objects")
    catalog.add_argument("--rollback-canonical-output")
    catalog.add_argument("--rollback-canonical-metadata-output")
    catalog.add_argument("--rollback-canonical-expected-sql-output")
    catalog.add_argument("--rollback-canonical-assert-sql-output")

    catalog_diff = subparsers.add_parser("diff-catalog-signatures")
    catalog_diff.add_argument("--objects", required=True)
    catalog_diff.add_argument("--expected", required=True)
    catalog_diff.add_argument("--actual", required=True)
    catalog_diff.add_argument("--output-json", required=True)
    catalog_diff.add_argument("--output-csv", required=True)
    catalog_diff.add_argument("--output-report", required=True)

    compose = subparsers.add_parser("sanitize-compose-config")
    compose.add_argument("--output", required=True)
    compose.add_argument("--binds-output", required=True)

    container_config = subparsers.add_parser(
        "attest-container-config"
    )
    container_config.add_argument("--compose", required=True)
    container_config.add_argument("--target-attestation", required=True)
    container_config.add_argument("--project-containers", required=True)
    container_config.add_argument("--fingerprint-spec", required=True)
    container_config.add_argument("--output", required=True)

    pg_dump = subparsers.add_parser("canonicalize-pg-dump")
    pg_dump.add_argument("--input", required=True)
    pg_dump.add_argument("--output", required=True)

    executable_pg_dump = subparsers.add_parser(
        "prepare-executable-pg-dump"
    )
    executable_pg_dump.add_argument("--input", required=True)
    executable_pg_dump.add_argument("--output", required=True)

    drop_stream = subparsers.add_parser("stream-verified-drop-sql")
    drop_stream.add_argument("--input", required=True)
    drop_stream.add_argument("--expected-sha256", required=True)
    drop_stream.add_argument("--proof", required=True)
    drop_stream.add_argument(
        "--wait-for-release",
        action="store_true",
    )

    manifest_drop_stream = subparsers.add_parser(
        "stream-verified-manifest-drop-sql"
    )
    manifest_drop_stream.add_argument("--manifest", required=True)
    manifest_drop_stream.add_argument(
        "--expected-manifest-sha256",
        required=True,
    )
    manifest_drop_stream.add_argument("--drop-sql", required=True)
    manifest_drop_stream.add_argument("--proof", required=True)
    manifest_drop_stream.add_argument(
        "--wait-for-release",
        action="store_true",
    )

    def add_startup_identity_arguments(startup_parser):
        startup_parser.add_argument(
            "--docker-socket",
            default="/var/run/docker.sock",
        )
        startup_parser.add_argument("--source-container-id", required=True)
        startup_parser.add_argument("--expected-image-id", required=True)
        startup_parser.add_argument(
            "--compose-image-reference",
            required=True,
        )
        startup_parser.add_argument(
            "--expected-image-reference-sha256",
            required=True,
        )
        startup_parser.add_argument(
            "--expected-manifest-sha256",
            required=True,
        )
        startup_parser.add_argument("--container-name", required=True)
        startup_parser.add_argument("--command-json", required=True)
        startup_parser.add_argument(
            "--expected-networks-json",
            required=True,
        )
        startup_parser.add_argument(
            "--expected-postgres-container-id",
            required=True,
        )
        startup_parser.add_argument("--database-host", required=True)
        startup_parser.add_argument("--output", required=True)

    create_startup = subparsers.add_parser(
        "create-immutable-startup-container"
    )
    add_startup_identity_arguments(create_startup)

    attest_startup = subparsers.add_parser(
        "attest-immutable-startup-container"
    )
    add_startup_identity_arguments(attest_startup)
    attest_startup.add_argument("--container-id", required=True)

    startup_routing = subparsers.add_parser(
        "attest-startup-database-routing"
    )
    startup_routing.add_argument("--startup-attestation", required=True)
    startup_routing.add_argument("--target-attestation", required=True)
    startup_routing.add_argument(
        "--expected-manifest-sha256",
        required=True,
    )
    startup_routing.add_argument(
        "--expected-postgres-container-id",
        required=True,
    )
    startup_routing.add_argument(
        "--expected-system-identifier",
        required=True,
    )
    startup_routing.add_argument("--expected-host", required=True)
    startup_routing.add_argument("--expected-port", required=True)
    startup_routing.add_argument("--expected-database", required=True)
    startup_routing.add_argument("--expected-user", required=True)
    startup_routing.add_argument(
        "--expected-networks-json",
        required=True,
    )
    startup_routing.add_argument("--output", required=True)
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
        if args.command == "validate-committed-resume-source":
            return validate_committed_resume_source(args)
        if args.command == "validate-committed-resume-copied-package":
            return validate_committed_resume_copied_package(args)
        if args.command == "validate-committed-resume-tooling":
            return validate_committed_resume_tooling(args)
        if args.command == "validate-committed-resume-gate":
            return validate_committed_resume_gate(args)
        if args.command == "validate-complete-live-gate":
            return validate_complete_live_gate(args)
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
        if args.command == "validate-retained-recapture":
            validate_retained_recapture(args)
            return 0
        if args.command == "prepare-column-catalog":
            prepare_column_catalog(args)
            return 0
        if args.command == "render-retained-capture-sql":
            render_retained_capture_sql(args)
            return 0
        if args.command == "render-rehearsal-capture-sql":
            render_rehearsal_capture_sql(args)
            return 0
        if args.command == "parse-rehearsal-capture":
            parse_rehearsal_capture(args)
            return 0
        if args.command == "prepare-catalog-signature":
            prepare_catalog_signature(args)
            return 0
        if args.command == "diff-catalog-signatures":
            return diff_catalog_signatures(args)
        if args.command == "sanitize-compose-config":
            sanitize_compose_config(args)
            return 0
        if args.command == "attest-container-config":
            return attest_container_config(args)
        if args.command == "canonicalize-pg-dump":
            canonicalize_pg_dump(args)
            return 0
        if args.command == "prepare-executable-pg-dump":
            prepare_executable_pg_dump(args)
            return 0
        if args.command == "stream-verified-drop-sql":
            stream_verified_drop_sql(args)
            return 0
        if args.command == "stream-verified-manifest-drop-sql":
            stream_verified_manifest_drop_sql(args)
            return 0
        if args.command == "create-immutable-startup-container":
            create_immutable_startup_container(args)
            return 0
        if args.command == "attest-immutable-startup-container":
            attest_immutable_startup_container(args)
            return 0
        if args.command == "attest-startup-database-routing":
            return attest_startup_database_routing(args)
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
