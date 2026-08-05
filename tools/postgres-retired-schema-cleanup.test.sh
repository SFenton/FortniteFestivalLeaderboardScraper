#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd -P)"
REPO_ROOT="$(cd -- "$SCRIPT_DIR/.." && pwd -P)"
SCRIPT="$SCRIPT_DIR/postgres-retired-schema-cleanup.sh"
HELPER="$SCRIPT_DIR/postgres-retired-schema-cleanup.py"
PROCESS_MATCHER="$SCRIPT_DIR/postgres-retired-schema-process-match.sh"
OBJECTS="$SCRIPT_DIR/sql/postgres-retired-schema-cleanup/objects.tsv"
WORK_ROOT="$SCRIPT_DIR/testdata/postgres-retired-schema-cleanup/.work-$$"
READY_FIXTURE="$WORK_ROOT/ready"

cleanup() {
    rm -rf "$WORK_ROOT"
}
trap cleanup EXIT
mkdir -p "$WORK_ROOT"

python3 - \
    "$OBJECTS" \
    "$READY_FIXTURE" \
    "$SCRIPT_DIR/sql/postgres-retired-schema-cleanup/public-fingerprints.tsv" <<'PY'
import csv
import hashlib
import json
import sys
from pathlib import Path

objects_path = Path(sys.argv[1])
fixture = Path(sys.argv[2])
fingerprint_spec = Path(sys.argv[3])
pre = fixture / "pre"
post = fixture / "post"
rollback = fixture / "rollback"
retained = pre / "retained-data"
for directory in [pre, post, rollback, retained]:
    directory.mkdir(parents=True, exist_ok=True)

with objects_path.open(newline="", encoding="utf-8") as handle:
    objects = list(csv.DictReader(handle, delimiter="\t"))

relation_fields = [
    "family", "order", "object_type", "schema", "name",
    "actual_relkind", "owner", "parent_schema", "parent_name",
    "row_count", "total_bytes", "sequence_owned_by",
    "sequence_last_value", "sequence_is_called",
]
relations = []
retained_row_counts = {
    "leaderboard_logical_write_metrics": "108",
    "band_song_team_ranking_state": "3",
}
retained_total_bytes = {
    "leaderboard_logical_write_metrics": "106496",
    "band_song_team_ranking_state": "65536",
}
for item in objects:
    is_table = item["object_type"] in {"table", "partitioned_table"}
    is_sequence = item["object_type"] == "sequence"
    relations.append({
        "family": item["family"],
        "order": item["order"],
        "object_type": item["object_type"],
        "schema": item["schema"],
        "name": item["name"],
        "actual_relkind": item["relkind"],
        "owner": "fst",
        "parent_schema": item["parent_schema"],
        "parent_name": item["parent_name"],
        "row_count": (
            retained_row_counts.get(item["name"], "0") if is_table else ""
        ),
        "total_bytes": (
            "0"
            if item["relkind"] in {"p", "v"}
            else retained_total_bytes.get(item["name"], "16384")
        ),
        "sequence_owned_by": (
            "public.player_score_observations.id" if is_sequence else ""
        ),
        "sequence_last_value": "210281757" if is_sequence else "",
        "sequence_is_called": "true" if is_sequence else "",
    })

def write_csv(path, fields, rows):
    with path.open("w", newline="", encoding="utf-8") as handle:
        writer = csv.DictWriter(handle, fieldnames=fields)
        writer.writeheader()
        writer.writerows(rows)

write_csv(pre / "relations.csv", relation_fields, relations)
parent_orders = {
    (item["schema"], item["name"]): item["order"]
    for item in objects
    if item["object_type"] == "partitioned_table"
}
partition_rows = []
for item in objects:
    if (
        item["object_type"] == "table"
        and item["parent_name"]
        and not item["owner_column"]
    ):
        partition_rows.append({
            "family": item["family"],
            "parent_order": parent_orders[
                (item["parent_schema"], item["parent_name"])
            ],
            "parent_schema": item["parent_schema"],
            "parent_name": item["parent_name"],
            "child_schema": item["schema"],
            "child_name": item["name"],
            "child_relkind": item["relkind"],
            "child_owner": "fst",
            "partition_bound": (
                "DEFAULT"
                if item["name"].endswith("_default")
                else f"FOR VALUES IN ('{item['name']}')"
            ),
        })
write_csv(
    pre / "partition-children.csv",
    [
        "family",
        "parent_order",
        "parent_schema",
        "parent_name",
        "child_schema",
        "child_name",
        "child_relkind",
        "child_owner",
        "partition_bound",
    ],
    partition_rows,
)
incoming_rows = [
    {
        "family": row["family"],
        "object_order": next(
            item["order"]
            for item in objects
            if item["schema"] == row["child_schema"]
            and item["name"] == row["child_name"]
        ),
        "target_schema": row["child_schema"],
        "target_name": row["child_name"],
        "parent_schema": row["parent_schema"],
        "parent_name": row["parent_name"],
        "parent_relkind": "p",
        "parent_owner": "fst",
        "inheritance_sequence": "1",
        "detach_pending": "false",
    }
    for row in partition_rows
]
write_csv(
    pre / "incoming-inheritance.csv",
    [
        "family",
        "object_order",
        "target_schema",
        "target_name",
        "parent_schema",
        "parent_name",
        "parent_relkind",
        "parent_owner",
        "inheritance_sequence",
        "detach_pending",
    ],
    incoming_rows,
)

column_fields = [
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

def column_row(item, attnum, name, formatted_type, *, not_null=True,
               default="", type_name=None, type_oid=None, storage="p",
               collation=False):
    type_values = {
        "bigint": ("int8", "20"),
        "integer": ("int4", "23"),
        "text": ("text", "25"),
        "timestamp with time zone": ("timestamptz", "1184"),
        "boolean": ("bool", "16"),
    }
    inferred_name, inferred_oid = type_values.get(
        formatted_type,
        ("text", "25"),
    )
    return {
        "family": item["family"],
        "object_order": item["order"],
        "schema": item["schema"],
        "name": item["name"],
        "attnum": str(attnum),
        "column_name": name,
        "is_dropped": "false",
        "type_schema": "pg_catalog",
        "type_name": type_name or inferred_name,
        "type_oid": type_oid or inferred_oid,
        "typmod": "-1",
        "formatted_type": formatted_type,
        "not_null": "true" if not_null else "false",
        "has_default": "true" if default else "false",
        "default_expression": default,
        "identity_kind": "",
        "generated_kind": "",
        "collation_schema": "pg_catalog" if collation else "",
        "collation_name": "default" if collation else "",
        "storage": storage,
        "compression": "",
        "is_local": "true",
        "inheritance_count": "0",
        "has_missing": "false",
        "missing_value": "",
        "statistics_target": "-1",
        "options": "",
        "fdw_options": "",
        "acl": "",
    }

metric_column_defs = [
    ("scrape_id", "bigint", "", "p", False),
    ("instrument", "text", "", "x", True),
    ("flush_count", "integer", "0", "p", False),
    ("observed_rows", "bigint", "0", "p", False),
    ("new_rows", "bigint", "0", "p", False),
    ("changed_rows", "bigint", "0", "p", False),
    ("unchanged_rows", "bigint", "0", "p", False),
    ("current_upserts", "bigint", "0", "p", False),
    ("versions_closed", "bigint", "0", "p", False),
    ("versions_opened", "bigint", "0", "p", False),
    ("first_observed_at", "timestamp with time zone", "now()", "p", False),
    ("last_observed_at", "timestamp with time zone", "now()", "p", False),
]
band_state_column_defs = [
    ("band_type", "text", "", "x", True),
    ("source_scope_count", "integer", "0", "p", False),
    ("source_row_count", "bigint", "0", "p", False),
    ("source_fingerprint", "text", "''::text", "x", True),
    ("ranking_row_count", "integer", "0", "p", False),
    ("updated_at", "timestamp with time zone", "now()", "p", False),
]

column_rows = []
for item in objects:
    if item["name"] == "leaderboard_logical_write_metrics":
        definitions = metric_column_defs
    elif item["name"] == "band_song_team_ranking_state":
        definitions = band_state_column_defs
    elif item["object_type"] == "sequence":
        definitions = [
            ("last_value", "bigint", "", "p", False),
            ("log_cnt", "bigint", "", "p", False),
            ("is_called", "boolean", "", "p", False),
        ]
    else:
        definitions = [("fixture_column", "text", "", "x", True)]
    for attnum, (name, formatted_type, default, storage, collation) in enumerate(
        definitions,
        1,
    ):
        column_rows.append(
            column_row(
                item,
                attnum,
                name,
                formatted_type,
                default=default,
                storage=storage,
                collation=collation,
            )
        )
write_csv(pre / "column-catalog.raw.csv", column_fields, column_rows)

catalog_rows = []
for item in objects:
    catalog_rows.append({
        "category": "relation",
        "object_identity": f"{item['schema']}.{item['name']}",
        "detail": json.dumps({
            "family": item["family"],
            "order": int(item["order"]),
            "relkind": item["relkind"],
            "rowSecurity": False,
            "forceRowSecurity": False,
            "fixture": True,
        }, sort_keys=True),
    })
for row in column_rows:
    detail = {
        "name": row["column_name"],
        "dropped": row["is_dropped"] == "true",
        "typeSchema": row["type_schema"],
        "typeName": row["type_name"],
        "typeOid": int(row["type_oid"]),
        "typmod": int(row["typmod"]),
        "formattedType": row["formatted_type"],
        "notNull": row["not_null"] == "true",
        "hasDefault": row["has_default"] == "true",
        "default": row["default_expression"],
        "identity": row["identity_kind"],
        "generated": row["generated_kind"],
        "collationSchema": row["collation_schema"],
        "collation": row["collation_name"],
        "storage": row["storage"],
        "compression": row["compression"],
        "isLocal": row["is_local"] == "true",
        "inheritanceCount": int(row["inheritance_count"]),
        "hasMissing": row["has_missing"] == "true",
        "missingValue": row["missing_value"],
        "statisticsTarget": int(row["statistics_target"]),
        "options": [],
        "fdwOptions": [],
        "acl": [],
    }
    catalog_rows.append({
        "category": "column",
        "object_identity": (
            f"{row['schema']}.{row['name']}#{row['attnum']}"
        ),
        "detail": json.dumps(detail, sort_keys=True),
    })
for row in partition_rows:
    catalog_rows.append({
        "category": "partition",
        "object_identity": (
            f"{row['parent_schema']}.{row['parent_name']}->"
            f"{row['child_schema']}.{row['child_name']}"
        ),
        "detail": json.dumps({
            "bound": row["partition_bound"],
            "childRelkind": row["child_relkind"],
            "childOwner": row["child_owner"],
            "sequence": 1,
            "detachPending": False,
        }, sort_keys=True),
    })
for row in incoming_rows:
    catalog_rows.append({
        "category": "incoming-inheritance",
        "object_identity": (
            f"{row['target_schema']}.{row['target_name']}<-"
            f"{row['parent_schema']}.{row['parent_name']}"
        ),
        "detail": json.dumps({
            "parentRelkind": row["parent_relkind"],
            "parentOwner": row["parent_owner"],
            "sequence": 1,
            "detachPending": False,
        }, sort_keys=True),
    })
catalog_rows.extend([
    {
        "category": "sequence",
        "object_identity": "public.player_score_observations_id_seq",
        "detail": json.dumps({
            "lastValue": "210281757",
            "isCalled": "true",
            "ownedBy": "public.player_score_observations.id",
        }, sort_keys=True),
    },
    {
        "category": "view",
        "object_identity": "public.player_score_observation_union",
        "detail": json.dumps({"definition": "fixture view"}, sort_keys=True),
    },
    {
        "category": "constraint",
        "object_identity": (
            "public.band_song_team_ranking_state."
            "band_song_team_ranking_state_pkey"
        ),
        "detail": json.dumps({"definition": "PRIMARY KEY (band_type)"}),
    },
    {
        "category": "index",
        "object_identity": "public.ix_llwm_scrape",
        "detail": json.dumps({
            "definition": "fixture index",
            "valid": True,
            "ready": True,
            "live": True,
            "checkXmin": False,
        }),
    },
    {
        "category": "trigger",
        "object_identity": "public.fixture.fixture_trigger",
        "detail": json.dumps({"internal": True}),
    },
    {
        "category": "policy",
        "object_identity": "public.fixture.fixture_policy",
        "detail": json.dumps({"command": "*"}),
    },
    {
        "category": "dependency",
        "object_identity": "fixture dependency",
        "detail": json.dumps({"type": "a"}),
    },
])
write_csv(
    pre / "catalog-signature.raw.csv",
    ["category", "object_identity", "detail"],
    catalog_rows,
)
write_csv(
    pre / "unexpected-relations.csv",
    ["schema", "name", "relkind", "owner"],
    [],
)
write_csv(
    pre / "external-dependencies.csv",
    ["dependency_kind", "dependent_object", "referenced_object", "detail"],
    [],
)
write_csv(
    pre / "owned-objects.csv",
    ["kind", "schema", "name", "target_schema", "target_name", "definition", "state"],
    [{
        "kind": "sequence-owner",
        "schema": "public",
        "name": "player_score_observations_id_seq",
        "target_schema": "public",
        "target_name": "player_score_observations",
        "definition": "id",
        "state": "a",
    }],
)

metric_fields = [
    "scrape_id",
    "instrument",
    "flush_count",
    "observed_rows",
    "new_rows",
    "changed_rows",
    "unchanged_rows",
    "current_upserts",
    "versions_closed",
    "versions_opened",
    "first_observed_at",
    "last_observed_at",
]
instruments = [
    "Bass",
    "Drums",
    "Guitar",
    "Solo_Bass",
    "Solo_Drums",
    "Solo_Guitar",
    "Solo_Vocals",
    "Vocals",
    "Pro_Cymbals",
]
metric_rows = []
for scrape_id in range(1255, 1267):
    for index, instrument in enumerate(instruments):
        observed = 400000 + (scrape_id - 1255) * 1000 + index * 100
        new_rows = 10 + index
        changed_rows = 20 + index
        metric_rows.append({
            "scrape_id": str(scrape_id),
            "instrument": instrument,
            "flush_count": str(40 + index),
            "observed_rows": str(observed),
            "new_rows": str(new_rows),
            "changed_rows": str(changed_rows),
            "unchanged_rows": str(observed - new_rows - changed_rows),
            "current_upserts": str(new_rows + changed_rows),
            "versions_closed": str(changed_rows),
            "versions_opened": str(new_rows + changed_rows),
            "first_observed_at": (
                f"2026-07-{10 + scrape_id - 1255:02d} "
                "01:00:00+00"
            ),
            "last_observed_at": (
                f"2026-07-{10 + scrape_id - 1255:02d} "
                "05:00:00+00"
            ),
        })
write_csv(
    retained / "leaderboard_logical_write_metrics.csv",
    metric_fields,
    metric_rows,
)

band_state_fields = [
    "band_type",
    "source_scope_count",
    "source_row_count",
    "source_fingerprint",
    "ranking_row_count",
    "updated_at",
]
write_csv(
    retained / "band_song_team_ranking_state.csv",
    band_state_fields,
    [
        {
            "band_type": "Band_Duets",
            "source_scope_count": "2044",
            "source_row_count": "148713761",
            "source_fingerprint": "a" * 64,
            "ranking_row_count": "32747954",
            "updated_at": "2026-07-28 08:01:02+00",
        },
        {
            "band_type": "Band_Quad",
            "source_scope_count": "8232",
            "source_row_count": "240373768",
            "source_fingerprint": "b" * 64,
            "ranking_row_count": "33205430",
            "updated_at": "2026-07-28 08:03:04+00",
        },
        {
            "band_type": "Band_Trios",
            "source_scope_count": "6174",
            "source_row_count": "227017957",
            "source_fingerprint": "c" * 64,
            "ranking_row_count": "48005285",
            "updated_at": "2026-07-28 08:02:03+00",
        },
    ],
)

preflight_fields = [
    "database_name", "server_version_num", "published_scrape_id",
    "published_at", "publication_updated_at", "public_reads_frozen",
    "working_publication_id",
    "current_publication_id", "current_publication_scrape_id",
    "current_publication_status", "current_publication_published_at",
    "cleanup_scrape_status", "cleanup_scrape_completed",
    "cleanup_scrape_completed_at", "active_scrape_count", "worker_status",
    "worker_current_operation", "ungranted_lock_count", "long_query_count",
    "target_query_count", "active_vacuum_count", "active_index_build_count",
    "active_rewrite_count", "critical_phase_failure_count",
    "ddl_guard_available", "sequence_guard_available",
]
preflight = {
    "database_name": "fstservice",
    "server_version_num": "170009",
    "published_scrape_id": "1278",
    "published_at": "2026-08-04 12:00:00+00",
    "publication_updated_at": "2026-08-04 12:00:01+00",
    "public_reads_frozen": "false",
    "working_publication_id": "",
    "current_publication_id": "9001",
    "current_publication_scrape_id": "1278",
    "current_publication_status": "current",
    "current_publication_published_at": "2026-08-04 12:00:00+00",
    "cleanup_scrape_status": "completed",
    "cleanup_scrape_completed": "true",
    "cleanup_scrape_completed_at": "2026-08-04 11:59:59+00",
    "active_scrape_count": "0",
    "worker_status": "offline",
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
write_csv(pre / "preflight.csv", preflight_fields, [preflight])

container_fields = [
    "service", "container_id", "container", "state", "health", "image_id",
    "compose_image_id", "restart_policy",
]
containers = [
    {
        "service": "postgres", "container": "fst-postgres",
        "container_id": "a" * 64,
        "state": "running", "health": "healthy",
        "image_id": "sha256:postgres", "compose_image_id": "sha256:postgres",
        "restart_policy": "unless-stopped",
    },
    {
        "service": "fstservice", "container": "fstservice",
        "container_id": "b" * 64,
        "state": "running", "health": "healthy",
        "image_id": "sha256:" + "1" * 64,
        "compose_image_id": "sha256:" + "1" * 64,
        "restart_policy": "unless-stopped",
    },
    {
        "service": "festivalweb", "container": "festivalweb",
        "container_id": "c" * 64,
        "state": "running", "health": "healthy",
        "image_id": "sha256:web", "compose_image_id": "sha256:web",
        "restart_policy": "unless-stopped",
    },
    {
        "service": "fstworker", "container": "fstworker",
        "container_id": "d" * 64,
        "state": "exited", "health": "none",
        "image_id": "sha256:" + "1" * 64,
        "compose_image_id": "sha256:" + "1" * 64,
        "restart_policy": "no",
    },
]
write_csv(pre / "containers.csv", container_fields, containers)
write_csv(
    pre / "health.csv",
    ["check", "status", "detail"],
    [
        {"check": "postgres-readiness", "status": "ok", "detail": "fixture"},
        {"check": "readyz", "status": "ok", "detail": "fixture"},
        {"check": "web-shell", "status": "ok", "detail": "fixture"},
        {"check": "service-info", "status": "ok", "detail": "fixture"},
        {"check": "capacity-guard", "status": "ok", "detail": "fixture"},
    ],
)
write_csv(
    pre / "storage.csv",
    [
        "pgdata_source", "filesystem_source", "filesystem_type",
        "evidence_root", "on_fst_drive", "filesystem_total_bytes",
        "filesystem_used_bytes", "filesystem_free_bytes",
        "filesystem_used_percent", "filesystem_mount", "database_bytes",
        "target_total_bytes",
    ],
    [{
        "pgdata_source": "/mnt/docker-storage/postgres",
        "filesystem_source": "/dev/fixture",
        "filesystem_type": "ext4",
        "evidence_root": "/mnt/docker-storage/Docker/FestivalServiceTracker/fst-data/evidence",
        "on_fst_drive": "true",
        "filesystem_total_bytes": "4000000000000",
        "filesystem_used_bytes": "3000000000000",
        "filesystem_free_bytes": "1000000000000",
        "filesystem_used_percent": "75",
        "filesystem_mount": "/mnt/docker-storage",
        "database_bytes": "3000000000000",
        "target_total_bytes": str(
            sum(int(row["total_bytes"] or 0) for row in relations)
        ),
    }],
)

fingerprint_fields = [
    "name", "url", "resolved_url", "format", "expected_status", "gate",
    "http_status", "body_bytes", "sha256",
]
fingerprints = []
with fingerprint_spec.open(newline="", encoding="utf-8") as handle:
    fingerprint_requests = list(csv.DictReader(handle, delimiter="\t"))
for request in fingerprint_requests:
    name = request["name"]
    resolved_url = (
        request["url"]
        .replace("{account_id}", "a" * 32)
        .replace("{team_key}", "b" * 32 + ":" + "c" * 32)
    )
    fingerprints.append({
        "name": name,
        "url": request["url"],
        "resolved_url": resolved_url,
        "format": request["format"],
        "expected_status": request["expected_status"],
        "gate": request["gate"],
        "http_status": request["expected_status"],
        "body_bytes": str(len(name)),
        "sha256": hashlib.sha256(name.encode()).hexdigest(),
    })
write_csv(pre / "fingerprints.csv", fingerprint_fields, fingerprints)
(pre / "runtime-source-references.txt").write_text("", encoding="utf-8")
(pre / "retained-reference-audit.txt").write_text(
    "fixture retained documentation reference\n",
    encoding="utf-8",
)
write_csv(
    pre / "source-scan-roots.csv",
    ["scan", "root", "exit_code", "status"],
    [
        {
            "scan": "active-runtime",
            "root": root,
            "exit_code": "1",
            "status": "ok",
        }
        for root in [
            "FSTService",
            "FortniteFestival.Core",
            "FortniteFestivalWeb/src",
            "packages",
            "docker-compose.yml",
            "deploy/docker-compose.yml",
        ]
    ]
    + [{
        "scan": "retained-audit",
        "root": ".",
        "exit_code": "0",
        "status": "ok",
    }]
    + [{
        "scan": "production-compose-raw",
        "root": (
            "/home/sfenton/Docker/FestivalServiceTracker/"
            "docker-compose.yml"
        ),
        "exit_code": "1",
        "status": "ok",
    }]
    + [{
        "scan": "production-compose-raw",
        "root": (
            "/home/sfenton/Docker/FestivalServiceTracker/"
            "docker-compose.pia.yml"
        ),
        "exit_code": "1",
        "status": "ok",
    }]
    + [{
        "scan": "production-compose-rendered",
        "root": "production-compose.sanitized.json",
        "exit_code": "1",
        "status": "ok",
    }]
    + [{
        "scan": "production-bind-inventory",
        "root": "production-compose-binds.tsv",
        "exit_code": "1",
        "status": "ok",
    }]
    + [{
        "scan": "production-bind-config",
        "root": (
            "/home/sfenton/Docker/FestivalServiceTracker/"
            "config/appsettings.Production.json"
        ),
        "exit_code": "1",
        "status": "ok",
    }]
    + [{
        "scan": "production-bind-config",
        "root": (
            "/home/sfenton/Docker/FestivalServiceTracker/"
            "config/pia-routing.yml"
        ),
        "exit_code": "1",
        "status": "ok",
    }],
)
write_csv(
    pre / "production-compose-files.csv",
    ["ordinal", "path", "sha256", "label_discovered"],
    [
        {
            "ordinal": "1",
            "path": (
                "/home/sfenton/Docker/FestivalServiceTracker/"
                "docker-compose.yml"
            ),
            "sha256": "d" * 64,
            "label_discovered": "true",
        },
        {
            "ordinal": "2",
            "path": (
                "/home/sfenton/Docker/FestivalServiceTracker/"
                "docker-compose.pia.yml"
            ),
            "sha256": "f" * 64,
            "label_discovered": "true",
        },
    ],
)
write_csv(
    pre / "production-compose-project-containers.csv",
    [
        "container_id",
        "container_name",
        "service",
        "project",
        "working_dir",
        "config_files_sha256",
    ],
    [
        {
            "container_id": container_id,
            "container_name": container_name,
            "service": service,
            "project": "festival-service-tracker",
            "working_dir": (
                "/home/sfenton/Docker/FestivalServiceTracker"
            ),
            "config_files_sha256": "9" * 64,
        }
        for container_id, container_name, service in [
            ("a" * 64, "fst-postgres", "postgres"),
            ("b" * 64, "fstservice", "fstservice"),
            ("c" * 64, "festivalweb", "festivalweb"),
            ("d" * 64, "fstworker", "fstworker"),
        ]
    ],
)
write_csv(
    pre / "production-database-target.csv",
    [
        "compose_project",
        "service",
        "host",
        "port",
        "database",
        "user",
        "container_id",
    ],
    [{
        "compose_project": "festival-service-tracker",
        "service": "postgres",
        "host": "postgres",
        "port": "5432",
        "database": "fstservice",
        "user": "fst",
        "container_id": "a" * 64,
    }],
)
target_attestation_fields = [
    "compose_project",
    "service",
    "configured_host",
    "configured_port",
    "configured_database",
    "configured_user",
    "container_id",
    "runtime_address",
    "runtime_port",
    "runtime_database",
    "runtime_user",
    "in_recovery",
    "system_identifier",
    "role_superuser",
    "role_bypass_rls",
]
target_attestation = {
    "compose_project": "festival-service-tracker",
    "service": "postgres",
    "configured_host": "postgres",
    "configured_port": "5432",
    "configured_database": "fstservice",
    "configured_user": "fst",
    "container_id": "a" * 64,
    "runtime_address": "local-socket",
    "runtime_port": "5432",
    "runtime_database": "fstservice",
    "runtime_user": "fst",
    "in_recovery": "false",
    "system_identifier": "7429301450012345678",
    "role_superuser": "true",
    "role_bypass_rls": "false",
}
write_csv(
    pre / "production-target-attestation.csv",
    target_attestation_fields,
    [target_attestation],
)
(pre / "production-compose.sanitized.json").write_text(
    json.dumps({
        "schemaVersion": 1,
        "projectName": "festival-service-tracker",
        "services": {
            "postgres": {
                "environment": {
                    "POSTGRES_DB": "fstservice",
                    "POSTGRES_USER": "fst",
                    "POSTGRES_PASSWORD": "must-not-survive",
                },
            },
            "fstservice": {
                "imagePresent": True,
                "imageReferenceSha256": "1" * 64,
                "environment": {
                    "names": ["Features__Fixture"],
                    "retiredValueReferences": [],
                },
                "volumes": [{
                    "type": "bind",
                    "source": (
                        "/home/sfenton/Docker/FestivalServiceTracker/"
                        "config/pia-routing.yml"
                    ),
                    "target": "/app/config/pia-routing.yml",
                    "readOnly": True,
                }],
            },
        },
        "configNames": [],
        "secretNames": ["database-password"],
        "volumeNames": [],
        "networkNames": [],
        "databaseTarget": {
            "service": "postgres",
            "host": "postgres",
            "port": "5432",
            "database": "fstservice",
            "user": "fst",
            "passwordConfigured": True,
            "consumers": ["fstservice", "fstworker"],
        },
    }, sort_keys=True),
    encoding="utf-8",
)
with (pre / "production-compose-binds.tsv").open(
    "w",
    newline="",
    encoding="utf-8",
) as handle:
    writer = csv.DictWriter(
        handle,
        fieldnames=[
            "service",
            "source",
            "target",
            "read_only",
            "classification",
        ],
        delimiter="\t",
    )
    writer.writeheader()
    writer.writerows([
        {
            "service": "fstservice",
            "source": (
                "/home/sfenton/Docker/FestivalServiceTracker/"
                "config/appsettings.Production.json"
            ),
            "target": "/app/appsettings.Production.json",
            "read_only": "true",
            "classification": "config-file",
        },
        {
            "service": "fstservice",
            "source": (
                "/home/sfenton/Docker/FestivalServiceTracker/"
                "config/pia-routing.yml"
            ),
            "target": "/app/config/pia-routing.yml",
            "read_only": "true",
            "classification": "config-file",
        },
        {
            "service": "postgres",
            "source": "<redacted-secret-bind>",
            "target": "<redacted-secret-target>",
            "read_only": "true",
            "classification": "secret",
        },
    ])
write_csv(
    pre / "production-bind-config-files.csv",
    ["path", "sha256"],
    [
        {
            "path": (
                "/home/sfenton/Docker/FestivalServiceTracker/"
                "config/appsettings.Production.json"
            ),
            "sha256": "e" * 64,
        },
        {
            "path": (
                "/home/sfenton/Docker/FestivalServiceTracker/"
                "config/pia-routing.yml"
            ),
            "sha256": "8" * 64,
        },
    ],
)

for family in [
    "logical-shadow",
    "score-observations",
    "band-song-projection",
    "aggregate-ranking-deltas",
]:
    (rollback / f"{family}.sql").write_text(
        (
            f"-- fixture rollback for {family}\nSELECT 1;\n"
            + (
                "SELECT pg_catalog.setval("
                "'public.player_score_observations_id_seq'::regclass, "
                "210281757, true);\n"
                if family == "score-observations"
                else ""
            )
        ),
        encoding="utf-8",
    )

write_csv(
    fixture / "rollback-hashes.csv",
    ["kind", "family", "name", "sha256", "expected_sha256", "verified"],
    [{
        "kind": "existing-evidence",
        "family": "all",
        "name": "fixture-existing-evidence",
        "sha256": "a" * 64,
        "expected_sha256": "a" * 64,
        "verified": "true",
    }],
)
(fixture / "parity.json").write_text(
    json.dumps({
        "schemaVersion": 1,
        "decision": "accepted",
        "scrapeId": 1278,
        "published": True,
        "unfrozen": True,
        "exactPublicFingerprintParity": True,
        "fingerprintCount": 13,
        "cleanupImageId": "sha256:" + "1" * 64,
        "fingerprintSpecSha256": hashlib.sha256(
            fingerprint_spec.read_bytes()
        ).hexdigest(),
        "acceptedAtUtc": "2026-08-04T12:00:00Z",
        "evidenceRoot": (
            "/mnt/docker-storage/Docker/FestivalServiceTracker/"
            "fst-data/evidence/fixture"
        ),
    }, sort_keys=True),
    encoding="utf-8",
)

absent = []
for row in relations:
    copy = dict(row)
    for key in [
        "actual_relkind", "owner", "parent_schema", "parent_name",
        "row_count", "total_bytes", "sequence_owned_by",
        "sequence_last_value", "sequence_is_called",
    ]:
        copy[key] = ""
    absent.append(copy)
for filename in ["relations.csv", "startup-relations.csv", "rehearsal-relations.csv"]:
    write_csv(post / filename, relation_fields, absent)
write_csv(post / "preflight.csv", preflight_fields, [preflight])
write_csv(
    post / "production-target-attestation.csv",
    target_attestation_fields,
    [target_attestation],
)
write_csv(post / "containers.csv", container_fields, containers)
write_csv(post / "health.csv", ["check", "status", "detail"], [
    {"check": "postgres-readiness", "status": "ok", "detail": "fixture"},
    {"check": "readyz", "status": "ok", "detail": "fixture"},
    {"check": "web-shell", "status": "ok", "detail": "fixture"},
    {"check": "service-info", "status": "ok", "detail": "fixture"},
    {"check": "capacity-guard", "status": "ok", "detail": "fixture"},
])
write_csv(post / "fingerprints.csv", fingerprint_fields, fingerprints)
write_csv(post / "storage.csv", [
    "pgdata_source", "filesystem_source", "filesystem_type",
    "evidence_root", "on_fst_drive", "filesystem_total_bytes",
    "filesystem_used_bytes", "filesystem_free_bytes",
    "filesystem_used_percent", "filesystem_mount", "database_bytes",
    "target_total_bytes",
], [{
    "pgdata_source": "/mnt/docker-storage/postgres",
    "filesystem_source": "/dev/fixture",
    "filesystem_type": "ext4",
    "evidence_root": "/mnt/docker-storage/Docker/FestivalServiceTracker/fst-data/evidence",
    "on_fst_drive": "true",
    "filesystem_total_bytes": "4000000000000",
    "filesystem_used_bytes": "2999999000000",
    "filesystem_free_bytes": "1000001000000",
    "filesystem_used_percent": "75",
    "filesystem_mount": "/mnt/docker-storage",
    "database_bytes": "2999999000000",
    "target_total_bytes": "0",
}])
write_csv(
    post / "startup-check.csv",
    ["check", "status"],
    [{"check": "startup-schema", "status": "0"}],
)
write_csv(
    post / "rollback-rehearsal.csv",
    ["check", "status"],
    [{"check": "rollback-rehearsal", "status": "0"}],
)
PY

copy_fixture() {
    local name="$1"
    local destination="$WORK_ROOT/$name"
    cp -R "$READY_FIXTURE" "$destination"
    printf '%s\n' "$destination"
}

mutate_csv() {
    local file="$1"
    local key="$2"
    local value="$3"
    python3 - "$file" "$key" "$value" <<'PY'
import csv
import sys
from pathlib import Path

path = Path(sys.argv[1])
key = sys.argv[2]
value = sys.argv[3]
with path.open(newline="", encoding="utf-8") as handle:
    reader = csv.DictReader(handle)
    fields = reader.fieldnames
    rows = list(reader)
rows[0][key] = value
with path.open("w", newline="", encoding="utf-8") as handle:
    writer = csv.DictWriter(handle, fieldnames=fields)
    writer.writeheader()
    writer.writerows(rows)
PY
}

run_status() {
    local expected="$1"
    local label="$2"
    shift 2
    local status=0
    "$@" > "$WORK_ROOT/$label.stdout" 2> "$WORK_ROOT/$label.stderr" || status=$?
    if [[ "$status" != "$expected" ]]; then
        printf 'FAIL: %s (expected %s, got %s)\n' "$label" "$expected" "$status" >&2
        cat "$WORK_ROOT/$label.stdout" >&2 || true
        cat "$WORK_ROOT/$label.stderr" >&2 || true
        exit 1
    fi
    printf 'PASS: %s\n' "$label"
}

run_cleanup() {
    local fixture="$1"
    local output="$2"
    shift 2
    env FST_RETIRED_SCHEMA_TEST_MODE=1 \
        "$SCRIPT" \
        --fixture-dir "$fixture" \
        --output "$output" \
        --parity-evidence "$fixture/parity.json" \
        "$@"
}

run_status 64 parsing-unknown-option \
    "$SCRIPT" --output "$WORK_ROOT/parse-unknown" --unknown
run_status 64 parsing-conflicting-modes \
    "$SCRIPT" --check --execute --output "$WORK_ROOT/parse-conflict"
run_status 64 parsing-execute-needs-hash \
    "$SCRIPT" --execute --output "$WORK_ROOT/parse-no-hash"
run_status 64 parsing-execute-needs-parity \
    "$SCRIPT" \
        --execute \
        --expected-manifest-sha256 \
        aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa \
        --output "$WORK_ROOT/parse-no-parity"
run_status 64 parsing-fixture-needs-test-mode \
    "$SCRIPT" --fixture-dir "$READY_FIXTURE" --output "$WORK_ROOT/parse-fixture"
run_status 64 libpq-pghost-override-rejected \
    env PGHOST=remote.example \
        "$SCRIPT" --output "$WORK_ROOT/parse-pghost"
run_status 64 libpq-pgservice-override-rejected \
    env PGSERVICE=clone \
        "$SCRIPT" --output "$WORK_ROOT/parse-pgservice"
run_status 64 libpq-pgservicefile-override-rejected \
    env PGSERVICEFILE="$WORK_ROOT/fake-service.conf" \
        "$SCRIPT" --output "$WORK_ROOT/parse-pgservicefile"
run_status 64 libpq-pgport-override-rejected \
    env PGPORT=6543 \
        "$SCRIPT" --output "$WORK_ROOT/parse-pgport"
run_status 64 production-target-rejects-clone-container \
    env FST_RETIRED_SCHEMA_TEST_MODE=1 \
        "$SCRIPT" \
        --fixture-dir "$READY_FIXTURE" \
        --output "$WORK_ROOT/parse-clone-container" \
        --pg-container clone-postgres
run_status 64 production-target-rejects-clone-database \
    env FST_RETIRED_SCHEMA_TEST_MODE=1 \
        "$SCRIPT" \
        --fixture-dir "$READY_FIXTURE" \
        --output "$WORK_ROOT/parse-clone-database" \
        --pg-db clone_db
run_status 64 production-target-rejects-clone-user \
    env FST_RETIRED_SCHEMA_TEST_MODE=1 \
        "$SCRIPT" \
        --fixture-dir "$READY_FIXTURE" \
        --output "$WORK_ROOT/parse-clone-user" \
        --pg-user clone_user

run_status 0 default-check-ready \
    run_cleanup "$READY_FIXTURE" "$WORK_ROOT/check-one"
run_status 0 explicit-check-ready \
    run_cleanup "$READY_FIXTURE" "$WORK_ROOT/check-two" --check
run_status 3 check-without-accepted-parity \
    env FST_RETIRED_SCHEMA_TEST_MODE=1 \
        "$SCRIPT" \
        --fixture-dir "$READY_FIXTURE" \
        --output "$WORK_ROOT/check-no-parity"
sha_one="$(cat "$WORK_ROOT/check-one/manifest-sha256.txt")"
sha_two="$(cat "$WORK_ROOT/check-two/manifest-sha256.txt")"
[[ "$sha_one" == "$sha_two" ]] || {
    printf 'FAIL: deterministic manifest hashes differ\n' >&2
    exit 1
}
printf 'PASS: deterministic manifest\n'

python3 - "$WORK_ROOT/check-one" <<'PY'
import csv
import hashlib
import json
import sys
from pathlib import Path

root = Path(sys.argv[1])
manifest = json.loads(
    (root / "package" / "manifest.json").read_text(encoding="utf-8")
)
columns_path = root / "package" / "catalog" / "columns.csv"
assert hashlib.sha256(columns_path.read_bytes()).hexdigest() == \
    manifest["columnCatalogSha256"]
with columns_path.open(newline="", encoding="utf-8") as handle:
    columns = list(csv.DictReader(handle))
metric_columns = [
    row for row in columns
    if row["name"] == "leaderboard_logical_write_metrics"
]
band_columns = [
    row for row in columns
    if row["name"] == "band_song_team_ranking_state"
]
assert [row["column_name"] for row in metric_columns] == [
    "scrape_id",
    "instrument",
    "flush_count",
    "observed_rows",
    "new_rows",
    "changed_rows",
    "unchanged_rows",
    "current_upserts",
    "versions_closed",
    "versions_opened",
    "first_observed_at",
    "last_observed_at",
]
assert [row["column_name"] for row in band_columns] == [
    "band_type",
    "source_scope_count",
    "source_row_count",
    "source_fingerprint",
    "ranking_row_count",
    "updated_at",
]
assert all(row["not_null"] == "true" for row in metric_columns + band_columns)
assert next(
    row for row in metric_columns if row["column_name"] == "flush_count"
)["default_expression"] == "0"
assert next(
    row for row in band_columns if row["column_name"] == "band_type"
)["collation_name"] == "default"
assert manifest["catalogSignature"]["categoryCounts"]["relation"] == 61
assert manifest["catalogSignature"]["categoryCounts"]["partition"] == 45
assert (
    manifest["catalogSignature"]["categoryCounts"]["incoming-inheritance"]
    == 45
)
assert [
    Path(row["path"]).name
    for row in manifest["productionComposeOwnership"]["files"]
] == ["docker-compose.yml", "docker-compose.pia.yml"]
assert manifest["productionDatabaseTarget"]["runtime"]["container_id"] == \
    "a" * 64
retained = {
    row["name"]: row for row in manifest["retainedData"]
}
assert retained["leaderboard_logical_write_metrics"]["row_count"] == "108"
assert retained["band_song_team_ranking_state"]["row_count"] == "3"
for row in retained.values():
    payload = root / "package" / "retained-data" / row["canonical_file"]
    assert hashlib.sha256(payload.read_bytes()).hexdigest() == row["sha256"]

logical_rollback = (
    root / "package" / "rollback" / "logical-shadow.sql"
).read_text(encoding="utf-8")
band_rollback = (
    root / "package" / "rollback" / "band-song-projection.sql"
).read_text(encoding="utf-8")
assert "-- Retained payload: public.leaderboard_logical_write_metrics" in \
    logical_rollback
assert "-- Retained payload: public.band_song_team_ranking_state" in \
    band_rollback
assert logical_rollback.count("\n1255,") == 9
assert logical_rollback.count("\n1266,") == 9
assert "\nBand_Duets," in band_rollback
assert "\nBand_Trios," in band_rollback
assert "\nBand_Quad," in band_rollback
rollback_hashes = {
    row["name"]: row
    for row in manifest["rollbackHashes"]
    if row["kind"] == "generated-family"
}
assert rollback_hashes["logical-shadow.sql"]["sha256"] == hashlib.sha256(
    logical_rollback.encode()
).hexdigest()
assert rollback_hashes["band-song-projection.sql"]["sha256"] == \
    hashlib.sha256(band_rollback.encode()).hexdigest()

metric_capture = (
    root / "package" / "retained-capture" /
    "leaderboard_logical_write_metrics.sql"
).read_text(encoding="utf-8")
for row in metric_columns:
    assert f'"{row["column_name"]}"' in metric_capture
PY
printf 'PASS: bound columns, retained payload, catalog, and rollback hashes\n'

unexpected_fixture="$(copy_fixture unexpected)"
printf 'public,ranking_deltas_unexpected,r,fst\n' \
    >> "$unexpected_fixture/pre/unexpected-relations.csv"
run_status 3 unexpected-object-gate \
    run_cleanup "$unexpected_fixture" "$WORK_ROOT/out-unexpected"

custom_partition_fixture="$(copy_fixture custom-partition)"
printf '%s\n' \
    'logical-shadow,10,public,leaderboard_current_entries,archive,custom_2026_partition,r,fst,FOR VALUES IN ('"'"'custom'"'"')' \
    >> "$custom_partition_fixture/pre/partition-children.csv"
run_status 3 custom-attached-partition-gate \
    run_cleanup \
        "$custom_partition_fixture" \
        "$WORK_ROOT/out-custom-partition"

attached_standalone_fixture="$(copy_fixture attached-standalone)"
printf '%s\n' \
    'logical-shadow,21,public,leaderboard_logical_write_metrics,external,unexpected_parent,p,other_owner,1,false' \
    >> "$attached_standalone_fixture/pre/incoming-inheritance.csv"
run_status 3 attached-standalone-parent-gate \
    run_cleanup \
        "$attached_standalone_fixture" \
        "$WORK_ROOT/out-attached-standalone"

attached_parent_fixture="$(copy_fixture attached-parent)"
printf '%s\n' \
    'aggregate-ranking-deltas,39,public,ranking_deltas,external,unexpected_parent,p,other_owner,1,false' \
    >> "$attached_parent_fixture/pre/incoming-inheritance.csv"
run_status 3 attached-partitioned-parent-gate \
    run_cleanup \
        "$attached_parent_fixture" \
        "$WORK_ROOT/out-attached-parent"

missing_fixture="$(copy_fixture missing)"
mutate_csv "$missing_fixture/pre/relations.csv" actual_relkind ""
run_status 3 missing-object-gate \
    run_cleanup "$missing_fixture" "$WORK_ROOT/out-missing"

dependency_fixture="$(copy_fixture dependency)"
printf 'view,public.consumer,public.ranking_deltas,fixture\n' \
    >> "$dependency_fixture/pre/external-dependencies.csv"
run_status 3 dependency-gate \
    run_cleanup "$dependency_fixture" "$WORK_ROOT/out-dependency"

nonzero_fixture="$(copy_fixture nonzero)"
mutate_csv "$nonzero_fixture/pre/relations.csv" row_count 1
run_status 3 nonzero-row-gate \
    run_cleanup "$nonzero_fixture" "$WORK_ROOT/out-nonzero"

active_fixture="$(copy_fixture active)"
mutate_csv "$active_fixture/pre/preflight.csv" active_scrape_count 1
run_status 3 active-scrape-gate \
    run_cleanup "$active_fixture" "$WORK_ROOT/out-active"

frozen_fixture="$(copy_fixture frozen)"
mutate_csv "$frozen_fixture/pre/preflight.csv" public_reads_frozen true
run_status 3 frozen-public-read-gate \
    run_cleanup "$frozen_fixture" "$WORK_ROOT/out-frozen"

working_publication_fixture="$(copy_fixture working-publication)"
mutate_csv \
    "$working_publication_fixture/pre/preflight.csv" \
    working_publication_id 9002
run_status 3 working-publication-gate \
    run_cleanup \
        "$working_publication_fixture" \
        "$WORK_ROOT/out-working-publication"

lock_fixture="$(copy_fixture ungranted-lock)"
mutate_csv "$lock_fixture/pre/preflight.csv" ungranted_lock_count 1
run_status 3 ungranted-lock-gate \
    run_cleanup "$lock_fixture" "$WORK_ROOT/out-ungranted-lock"

ddl_guard_fixture="$(copy_fixture ddl-guard)"
mutate_csv \
    "$ddl_guard_fixture/pre/preflight.csv" \
    ddl_guard_available \
    false
run_status 3 ddl-maintenance-guard-gate \
    run_cleanup "$ddl_guard_fixture" "$WORK_ROOT/out-ddl-guard"

sequence_guard_fixture="$(copy_fixture sequence-guard)"
mutate_csv \
    "$sequence_guard_fixture/pre/preflight.csv" \
    sequence_guard_available \
    false
run_status 3 sequence-maintenance-guard-gate \
    run_cleanup "$sequence_guard_fixture" "$WORK_ROOT/out-sequence-guard"

worker_fixture="$(copy_fixture worker)"
mutate_csv "$worker_fixture/pre/preflight.csv" worker_status running
mutate_csv "$worker_fixture/pre/preflight.csv" worker_current_operation true
run_status 3 worker-gate \
    run_cleanup "$worker_fixture" "$WORK_ROOT/out-worker"

restart_fixture="$(copy_fixture worker-restart)"
python3 - "$restart_fixture/pre/containers.csv" <<'PY'
import csv
import sys
from pathlib import Path

path = Path(sys.argv[1])
with path.open(newline="", encoding="utf-8") as handle:
    reader = csv.DictReader(handle)
    fields = reader.fieldnames
    rows = list(reader)
for row in rows:
    if row["service"] == "fstworker":
        row["restart_policy"] = "unless-stopped"
with path.open("w", newline="", encoding="utf-8") as handle:
    writer = csv.DictWriter(handle, fieldnames=fields)
    writer.writeheader()
    writer.writerows(rows)
PY
run_status 3 worker-restart-policy-gate \
    run_cleanup "$restart_fixture" "$WORK_ROOT/out-worker-restart"

source_fixture="$(copy_fixture source-reference)"
printf 'FSTService/fixture.cs:1:ranking_deltas\n' \
    > "$source_fixture/pre/runtime-source-references.txt"
run_status 3 runtime-source-reference-gate \
    run_cleanup "$source_fixture" "$WORK_ROOT/out-source"

source_scan_error_fixture="$(copy_fixture source-scan-error)"
mutate_csv \
    "$source_scan_error_fixture/pre/source-scan-roots.csv" \
    exit_code \
    2
mutate_csv \
    "$source_scan_error_fixture/pre/source-scan-roots.csv" \
    status \
    error
run_status 3 source-scan-error-gate \
    run_cleanup \
        "$source_scan_error_fixture" \
        "$WORK_ROOT/out-source-scan-error"

source_scan_missing_fixture="$(copy_fixture source-scan-missing)"
python3 - "$source_scan_missing_fixture/pre/source-scan-roots.csv" <<'PY'
import csv
import sys
from pathlib import Path

path = Path(sys.argv[1])
with path.open(newline="", encoding="utf-8") as handle:
    reader = csv.DictReader(handle)
    fields = reader.fieldnames
    rows = list(reader)
rows = [row for row in rows if row["root"] != "packages"]
with path.open("w", newline="", encoding="utf-8") as handle:
    writer = csv.DictWriter(handle, fieldnames=fields)
    writer.writeheader()
    writer.writerows(rows)
PY
run_status 3 source-scan-missing-root-gate \
    run_cleanup \
        "$source_scan_missing_fixture" \
        "$WORK_ROOT/out-source-scan-missing"

compose_render_error_fixture="$(copy_fixture compose-render-error)"
python3 - "$compose_render_error_fixture/pre/source-scan-roots.csv" <<'PY'
import csv
import sys
from pathlib import Path

path = Path(sys.argv[1])
with path.open(newline="", encoding="utf-8") as handle:
    reader = csv.DictReader(handle)
    fields = reader.fieldnames
    rows = list(reader)
for row in rows:
    if row["scan"] == "production-compose-rendered":
        row["exit_code"] = "2"
        row["status"] = "error"
with path.open("w", newline="", encoding="utf-8") as handle:
    writer = csv.DictWriter(handle, fieldnames=fields)
    writer.writeheader()
    writer.writerows(rows)
PY
run_status 3 production-compose-render-error-gate \
    run_cleanup \
        "$compose_render_error_fixture" \
        "$WORK_ROOT/out-compose-render-error"

compose_secret_fixture="$(copy_fixture compose-secret)"
python3 - "$compose_secret_fixture/pre/production-compose-binds.tsv" <<'PY'
import csv
import sys
from pathlib import Path

path = Path(sys.argv[1])
with path.open(newline="", encoding="utf-8") as handle:
    reader = csv.DictReader(handle, delimiter="\t")
    fields = reader.fieldnames
    rows = list(reader)
for row in rows:
    if row["classification"] == "secret":
        row["source"] = "/host/plaintext-secret"
with path.open("w", newline="", encoding="utf-8") as handle:
    writer = csv.DictWriter(
        handle,
        fieldnames=fields,
        delimiter="\t",
    )
    writer.writeheader()
    writer.writerows(rows)
PY
run_status 3 production-compose-secret-redaction-gate \
    run_cleanup \
        "$compose_secret_fixture" \
        "$WORK_ROOT/out-compose-secret"

compose_label_fixture="$(copy_fixture compose-label)"
mutate_csv \
    "$compose_label_fixture/pre/production-compose-files.csv" \
    label_discovered \
    false
run_status 3 production-compose-label-ownership-gate \
    run_cleanup \
        "$compose_label_fixture" \
        "$WORK_ROOT/out-compose-label"

compose_order_fixture="$(copy_fixture compose-order)"
python3 - "$compose_order_fixture/pre/production-compose-files.csv" <<'PY'
import csv
import sys
from pathlib import Path

path = Path(sys.argv[1])
with path.open(newline="", encoding="utf-8") as handle:
    reader = csv.DictReader(handle)
    fields = reader.fieldnames
    rows = list(reader)
rows[0]["ordinal"], rows[1]["ordinal"] = rows[1]["ordinal"], rows[0]["ordinal"]
with path.open("w", newline="", encoding="utf-8") as handle:
    writer = csv.DictWriter(handle, fieldnames=fields)
    writer.writeheader()
    writer.writerows(rows)
PY
run_status 3 production-compose-order-gate \
    run_cleanup "$compose_order_fixture" "$WORK_ROOT/out-compose-order"

compose_disagreement_fixture="$(copy_fixture compose-disagreement)"
python3 - \
    "$compose_disagreement_fixture/pre/production-compose-project-containers.csv" <<'PY'
import csv
import sys
from pathlib import Path

path = Path(sys.argv[1])
with path.open(newline="", encoding="utf-8") as handle:
    reader = csv.DictReader(handle)
    fields = reader.fieldnames
    rows = list(reader)
rows[-1]["config_files_sha256"] = "7" * 64
with path.open("w", newline="", encoding="utf-8") as handle:
    writer = csv.DictWriter(handle, fieldnames=fields)
    writer.writeheader()
    writer.writerows(rows)
PY
run_status 3 production-compose-container-agreement-gate \
    run_cleanup \
        "$compose_disagreement_fixture" \
        "$WORK_ROOT/out-compose-disagreement"

parity_fixture="$(copy_fixture parity-spec)"
python3 - "$parity_fixture/parity.json" <<'PY'
import json
import sys
from pathlib import Path

path = Path(sys.argv[1])
value = json.loads(path.read_text(encoding="utf-8"))
value["fingerprintSpecSha256"] = "c" * 64
path.write_text(json.dumps(value, sort_keys=True), encoding="utf-8")
PY
run_status 3 parity-fingerprint-spec-gate \
    run_cleanup "$parity_fixture" "$WORK_ROOT/out-parity-spec"

parity_count_fixture="$(copy_fixture parity-count)"
python3 - "$parity_count_fixture/parity.json" <<'PY'
import json
import sys
from pathlib import Path

path = Path(sys.argv[1])
value = json.loads(path.read_text(encoding="utf-8"))
value["fingerprintCount"] = 12
path.write_text(json.dumps(value, sort_keys=True), encoding="utf-8")
PY
run_status 3 parity-fingerprint-count-gate \
    run_cleanup "$parity_count_fixture" "$WORK_ROOT/out-parity-count"

rollback_hash_fixture="$(copy_fixture rollback-hash)"
mutate_csv \
    "$rollback_hash_fixture/rollback-hashes.csv" \
    verified false
run_status 3 rollback-hash-gate \
    run_cleanup "$rollback_hash_fixture" "$WORK_ROOT/out-rollback-hash"

rollback_timeout_fixture="$(copy_fixture rollback-timeout)"
printf '%s\n' logical-shadow \
    > "$rollback_timeout_fixture/pre/rollback-generation-failures.txt"
run_status 3 rollback-capture-timeout-gate \
    run_cleanup "$rollback_timeout_fixture" "$WORK_ROOT/out-rollback-timeout"

run_status 3 execute-manifest-drift \
    run_cleanup "$READY_FIXTURE" "$WORK_ROOT/execute-drift" \
        --execute \
        --expected-manifest-sha256 \
        aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa

pia_override_drift_fixture="$(copy_fixture pia-override-drift)"
python3 - \
    "$pia_override_drift_fixture/pre/production-compose.sanitized.json" <<'PY'
import json
import sys
from pathlib import Path

path = Path(sys.argv[1])
value = json.loads(path.read_text(encoding="utf-8"))
value["services"]["fstservice"]["imageReferenceSha256"] = "6" * 64
value["services"]["fstservice"]["volumes"][0]["source"] = (
    "/home/sfenton/Docker/FestivalServiceTracker/config/other-routing.yml"
)
path.write_text(json.dumps(value, sort_keys=True), encoding="utf-8")
PY
run_status 3 pia-override-render-manifest-drift \
    run_cleanup \
        "$pia_override_drift_fixture" \
        "$WORK_ROOT/out-pia-override-drift" \
        --execute \
        --expected-manifest-sha256 "$sha_one"

target_clone_fixture="$(copy_fixture target-clone)"
mutate_csv \
    "$target_clone_fixture/pre/production-target-attestation.csv" \
    container_id \
    ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff
run_status 3 production-target-clone-attestation-gate \
    run_cleanup "$target_clone_fixture" "$WORK_ROOT/out-target-clone"

remote_target_fixture="$(copy_fixture remote-target)"
mutate_csv \
    "$remote_target_fixture/pre/production-target-attestation.csv" \
    runtime_address \
    10.0.0.55
run_status 3 remote-matching-cluster-target-gate \
    run_cleanup "$remote_target_fixture" "$WORK_ROOT/out-remote-target"

rls_privilege_fixture="$(copy_fixture rls-privilege)"
mutate_csv \
    "$rls_privilege_fixture/pre/production-target-attestation.csv" \
    role_superuser \
    false
mutate_csv \
    "$rls_privilege_fixture/pre/production-target-attestation.csv" \
    role_bypass_rls \
    false
run_status 3 row-security-bypass-privilege-gate \
    run_cleanup "$rls_privilege_fixture" "$WORK_ROOT/out-rls-privilege"

retained_drift_fixture="$(copy_fixture retained-drift)"
mutate_csv \
    "$retained_drift_fixture/pre/retained-data/leaderboard_logical_write_metrics.csv" \
    changed_rows \
    999
run_status 3 retained-payload-manifest-drift \
    run_cleanup "$retained_drift_fixture" "$WORK_ROOT/execute-retained-drift" \
        --execute \
        --expected-manifest-sha256 "$sha_one"

catalog_drift_fixture="$(copy_fixture catalog-drift)"
mutate_csv \
    "$catalog_drift_fixture/pre/catalog-signature.raw.csv" \
    detail \
    '{"drift":true,"forceRowSecurity":false,"rowSecurity":false}'
run_status 3 complete-catalog-manifest-drift \
    run_cleanup "$catalog_drift_fixture" "$WORK_ROOT/execute-catalog-drift" \
        --execute \
        --expected-manifest-sha256 "$sha_one"

forced_rls_fixture="$(copy_fixture forced-rls-hidden-row)"
python3 - "$forced_rls_fixture/pre/catalog-signature.raw.csv" <<'PY'
import csv
import json
import sys
from pathlib import Path

path = Path(sys.argv[1])
with path.open(newline="", encoding="utf-8") as handle:
    reader = csv.DictReader(handle)
    fields = reader.fieldnames
    rows = list(reader)
for row in rows:
    if (
        row["category"] == "relation"
        and row["object_identity"] ==
            "public.leaderboard_current_entries_bass"
    ):
        detail = json.loads(row["detail"])
        detail["rowSecurity"] = True
        detail["forceRowSecurity"] = True
        row["detail"] = json.dumps(detail, sort_keys=True)
with path.open("w", newline="", encoding="utf-8") as handle:
    writer = csv.DictWriter(handle, fieldnames=fields)
    writer.writeheader()
    writer.writerows(rows)
PY
run_status 2 forced-rls-hidden-row-check-gate \
    run_cleanup "$forced_rls_fixture" "$WORK_ROOT/out-forced-rls"

nextval_drift_fixture="$(copy_fixture nextval-drift)"
python3 - "$nextval_drift_fixture/pre/catalog-signature.raw.csv" <<'PY'
import csv
import json
import sys
from pathlib import Path

path = Path(sys.argv[1])
with path.open(newline="", encoding="utf-8") as handle:
    reader = csv.DictReader(handle)
    fields = reader.fieldnames
    rows = list(reader)
for row in rows:
    if row["category"] == "sequence":
        detail = json.loads(row["detail"])
        detail["lastValue"] = "210281758"
        row["detail"] = json.dumps(detail, sort_keys=True)
with path.open("w", newline="", encoding="utf-8") as handle:
    writer = csv.DictWriter(handle, fieldnames=fields)
    writer.writeheader()
    writer.writerows(rows)
PY
run_status 3 sequence-nextval-drift-gate \
    run_cleanup "$nextval_drift_fixture" "$WORK_ROOT/out-nextval-drift" \
        --execute \
        --expected-manifest-sha256 "$sha_one"

routine_drift_fixture="$(copy_fixture routine-drift)"
printf '%s\n' \
    'routine-reference,public.fixture_runtime(),"{""definition"":""SELECT ranking_deltas"",""kind"":""f"",""language"":""sql""}"' \
    >> "$routine_drift_fixture/pre/catalog-signature.raw.csv"
run_status 3 routine-creation-drift-gate \
    run_cleanup "$routine_drift_fixture" "$WORK_ROOT/out-routine-drift" \
        --execute \
        --expected-manifest-sha256 "$sha_one"

column_missing_fixture="$(copy_fixture column-missing)"
python3 - "$column_missing_fixture/pre/column-catalog.raw.csv" <<'PY'
import csv
import sys
from pathlib import Path

path = Path(sys.argv[1])
with path.open(newline="", encoding="utf-8") as handle:
    reader = csv.DictReader(handle)
    fields = reader.fieldnames
    rows = list(reader)
rows = [
    row for row in rows
    if not (
        row["name"] == "leaderboard_logical_write_metrics"
        and row["column_name"] == "last_observed_at"
    )
]
with path.open("w", newline="", encoding="utf-8") as handle:
    writer = csv.DictWriter(handle, fieldnames=fields)
    writer.writeheader()
    writer.writerows(rows)
PY
run_status 2 retained-column-catalog-missing-gate \
    run_cleanup "$column_missing_fixture" "$WORK_ROOT/out-column-missing"

column_default_drift_fixture="$(copy_fixture column-default-drift)"
mutate_csv \
    "$column_default_drift_fixture/pre/column-catalog.raw.csv" \
    default_expression \
    999
run_status 2 complete-column-catalog-default-drift-gate \
    run_cleanup \
        "$column_default_drift_fixture" \
        "$WORK_ROOT/out-column-default-drift"

column_missing_value_fixture="$(copy_fixture column-missing-value)"
mutate_csv \
    "$column_missing_value_fixture/pre/column-catalog.raw.csv" \
    has_missing \
    true
mutate_csv \
    "$column_missing_value_fixture/pre/column-catalog.raw.csv" \
    missing_value \
    '{0}'
run_status 2 nonrestorable-column-missing-value-gate \
    run_cleanup \
        "$column_missing_value_fixture" \
        "$WORK_ROOT/out-column-missing-value"

invalid_index_fixture="$(copy_fixture invalid-index)"
python3 - "$invalid_index_fixture/pre/catalog-signature.raw.csv" <<'PY'
import csv
import json
import sys
from pathlib import Path

path = Path(sys.argv[1])
with path.open(newline="", encoding="utf-8") as handle:
    reader = csv.DictReader(handle)
    fields = reader.fieldnames
    rows = list(reader)
for row in rows:
    if row["category"] == "index":
        detail = json.loads(row["detail"])
        detail["valid"] = False
        row["detail"] = json.dumps(detail, sort_keys=True)
with path.open("w", newline="", encoding="utf-8") as handle:
    writer = csv.DictWriter(handle, fieldnames=fields)
    writer.writeheader()
    writer.writerows(rows)
PY
run_status 2 nonrestorable-invalid-index-gate \
    run_cleanup "$invalid_index_fixture" "$WORK_ROOT/out-invalid-index"

scratch_failure_fixture="$(copy_fixture scratch-roundtrip-failure)"
: > "$scratch_failure_fixture/scratch-proof-failure"
run_status 3 predestructive-scratch-roundtrip-gate \
    run_cleanup \
        "$scratch_failure_fixture" \
        "$WORK_ROOT/out-scratch-roundtrip-failure" \
        --execute \
        --expected-manifest-sha256 "$sha_one"

hanging_psql_fixture="$(copy_fixture hanging-psql)"
printf '%s\n' 124 > "$hanging_psql_fixture/psql-stream-exit-code"
run_status 124 hanging-shared-psql-gate \
    run_cleanup "$hanging_psql_fixture" "$WORK_ROOT/out-hanging-psql"

hanging_drop_committed_fixture="$(copy_fixture hanging-drop-committed)"
printf '%s\n' 124 \
    > "$hanging_drop_committed_fixture/drop-process-exit-code"
run_status 0 hanging-drop-commit-reconciliation \
    run_cleanup \
        "$hanging_drop_committed_fixture" \
        "$WORK_ROOT/out-hanging-drop-committed" \
        --execute \
        --expected-manifest-sha256 "$sha_one"

hanging_drop_rollback_fixture="$(copy_fixture hanging-drop-rollback)"
printf '%s\n' 124 \
    > "$hanging_drop_rollback_fixture/drop-process-exit-code"
cp \
    "$hanging_drop_rollback_fixture/pre/relations.csv" \
    "$hanging_drop_rollback_fixture/post/relations.csv"
run_status 124 hanging-drop-rollback-reconciliation \
    run_cleanup \
        "$hanging_drop_rollback_fixture" \
        "$WORK_ROOT/out-hanging-drop-rollback" \
        --execute \
        --expected-manifest-sha256 "$sha_one"

active_backend_fixture="$(copy_fixture active-backend-timeout)"
printf '%s\n' 124 > "$active_backend_fixture/drop-process-exit-code"
: > "$active_backend_fixture/drop-simulate-active-backend"
: > "$active_backend_fixture/drop-backend-stays-active"
run_status 3 active-backend-blocks-reconciliation \
    run_cleanup \
        "$active_backend_fixture" \
        "$WORK_ROOT/out-active-backend-timeout" \
        --execute \
        --expected-manifest-sha256 "$sha_one"

control_failure_fixture="$(copy_fixture control-query-failure)"
printf '%s\n' 124 > "$control_failure_fixture/drop-process-exit-code"
: > "$control_failure_fixture/drop-simulate-active-backend"
: > "$control_failure_fixture/drop-control-query-failure"
cp "$control_failure_fixture/pre/relations.csv" \
    "$control_failure_fixture/post/relations.csv"
run_status 3 control-failure-still-terminates-known-clients \
    run_cleanup \
        "$control_failure_fixture" \
        "$WORK_ROOT/out-control-query-failure" \
        --execute \
        --expected-manifest-sha256 "$sha_one"
test -f \
    "$WORK_ROOT/out-control-query-failure/post/known-local-client-terminated"
test -f \
    "$WORK_ROOT/out-control-query-failure/post/known-container-client-terminated"
grep -q ',all-present,' \
    "$WORK_ROOT/out-control-query-failure/post/drop-process-status.csv"

pid_reuse_fixture="$(copy_fixture local-pid-reuse)"
printf '%s\n' 124 > "$pid_reuse_fixture/drop-process-exit-code"
: > "$pid_reuse_fixture/drop-simulate-active-backend"
: > "$pid_reuse_fixture/drop-local-pid-reused"
cp "$pid_reuse_fixture/pre/relations.csv" \
    "$pid_reuse_fixture/post/relations.csv"
run_status 3 local-pid-reuse-not-signaled \
    run_cleanup \
        "$pid_reuse_fixture" \
        "$WORK_ROOT/out-local-pid-reuse" \
        --execute \
        --expected-manifest-sha256 "$sha_one"
grep -q ',terminated-pid-reused,' \
    "$WORK_ROOT/out-local-pid-reuse/post/drop-process-control.csv"

run_signal_case() {
    local signal="$1"
    local expected_status="$2"
    local expected_state="$3"
    local name="${signal,,}"
    local fixture output
    fixture="$(copy_fixture "signal-$name")"
    output="$WORK_ROOT/out-signal-$name"
    : > "$fixture/drop-wait-for-signal"
    case "$expected_state" in
        committed) ;;
        all-present)
            cp "$fixture/pre/relations.csv" "$fixture/post/relations.csv"
            ;;
        partial)
            cp "$fixture/pre/relations.csv" "$fixture/post/relations.csv"
            mutate_csv "$fixture/post/relations.csv" actual_relkind ""
            ;;
        *) exit 1 ;;
    esac
    python3 - \
        "$SCRIPT" \
        "$fixture" \
        "$output" \
        "$sha_one" \
        "$signal" \
        "$expected_status" \
        "$WORK_ROOT/signal-$name.stdout" \
        "$WORK_ROOT/signal-$name.stderr" <<'PY'
import os
import signal
import subprocess
import sys
import time
from pathlib import Path

script, fixture, output, manifest, signal_name, expected, stdout, stderr = (
    sys.argv[1:]
)
environment = dict(os.environ)
environment["FST_RETIRED_SCHEMA_TEST_MODE"] = "1"
command = [
    script,
    "--fixture-dir", fixture,
    "--output", output,
    "--parity-evidence", str(Path(fixture) / "parity.json"),
    "--execute",
    "--expected-manifest-sha256", manifest,
]
with open(stdout, "wb") as stdout_handle, open(stderr, "wb") as stderr_handle:
    process = subprocess.Popen(
        command,
        env=environment,
        stdout=stdout_handle,
        stderr=stderr_handle,
    )
    waiting = Path(output) / "post" / "drop-waiting"
    for _ in range(100):
        if waiting.is_file():
            break
        if process.poll() is not None:
            raise SystemExit(
                f"signal fixture exited early with {process.returncode}"
            )
        time.sleep(0.1)
    else:
        process.terminate()
        process.wait(timeout=10)
        raise SystemExit("signal fixture never became active")
    os.kill(process.pid, getattr(signal, f"SIG{signal_name}"))
    status = process.wait(timeout=20)
if status != int(expected):
    raise SystemExit(
        f"{signal_name} expected {expected}, got {status}"
    )
PY
    grep -q ',terminated,' "$output/post/drop-process-control.csv"
    grep -q ",$expected_state," "$output/post/drop-process-status.csv"
    grep -q '^status=signal$' "$output/FAILED.txt"
    printf 'PASS: delayed transaction %s cleanup\n' "$signal"
}

run_signal_case INT 130 committed
run_signal_case TERM 143 all-present
run_signal_case HUP 129 partial

launch_signal_fixture="$(copy_fixture signal-at-launch)"
: > "$launch_signal_fixture/drop-signal-at-launch"
cp "$launch_signal_fixture/pre/relations.csv" \
    "$launch_signal_fixture/post/relations.csv"
launch_signal_output="$WORK_ROOT/out-signal-at-launch"
python3 - \
    "$SCRIPT" \
    "$launch_signal_fixture" \
    "$launch_signal_output" \
    "$sha_one" <<'PY'
import os
import signal
import subprocess
import sys
import time
from pathlib import Path

script, fixture, output, manifest = sys.argv[1:]
environment = dict(os.environ)
environment["FST_RETIRED_SCHEMA_TEST_MODE"] = "1"
process = subprocess.Popen(
    [
        script,
        "--fixture-dir", fixture,
        "--output", output,
        "--parity-evidence", str(Path(fixture) / "parity.json"),
        "--execute",
        "--expected-manifest-sha256", manifest,
    ],
    env=environment,
    stdout=subprocess.DEVNULL,
    stderr=subprocess.DEVNULL,
)
barrier = Path(output) / "post" / "drop-launch-barrier-ready"
for _ in range(100):
    if barrier.is_file():
        break
    if process.poll() is not None:
        raise SystemExit(f"launch fixture exited early: {process.returncode}")
    time.sleep(0.1)
else:
    process.terminate()
    process.wait(timeout=10)
    raise SystemExit("launch barrier was never ready")
os.kill(process.pid, signal.SIGTERM)
status = process.wait(timeout=20)
if status != 143:
    raise SystemExit(f"launch SIGTERM expected 143, got {status}")
PY
grep -q ',false,false,,,terminated,' \
    "$launch_signal_output/post/drop-process-control.csv"
grep -q ',all-present,' \
    "$launch_signal_output/post/drop-process-status.csv"
printf 'PASS: deterministic signal-at-launch barrier cleanup\n'

post_connect_fixture="$(copy_fixture signal-post-connect)"
: > "$post_connect_fixture/drop-signal-post-connect"
cp "$post_connect_fixture/pre/relations.csv" \
    "$post_connect_fixture/post/relations.csv"
post_connect_output="$WORK_ROOT/out-signal-post-connect"
python3 - \
    "$SCRIPT" \
    "$post_connect_fixture" \
    "$post_connect_output" \
    "$sha_one" <<'PY'
import os
import signal
import subprocess
import sys
import time
from pathlib import Path

script, fixture, output, manifest = sys.argv[1:]
environment = dict(os.environ)
environment["FST_RETIRED_SCHEMA_TEST_MODE"] = "1"
process = subprocess.Popen(
    [
        script,
        "--fixture-dir", fixture,
        "--output", output,
        "--parity-evidence", str(Path(fixture) / "parity.json"),
        "--execute",
        "--expected-manifest-sha256", manifest,
    ],
    env=environment,
    stdout=subprocess.DEVNULL,
    stderr=subprocess.DEVNULL,
)
barrier = Path(output) / "post" / "drop-post-connect-barrier-ready"
for _ in range(100):
    if barrier.is_file():
        break
    if process.poll() is not None:
        raise SystemExit(
            f"post-connect fixture exited early: {process.returncode}"
        )
    time.sleep(0.1)
else:
    process.terminate()
    process.wait(timeout=10)
    raise SystemExit("post-connect barrier was never ready")
os.kill(process.pid, signal.SIGTERM)
status = process.wait(timeout=20)
if status != 143:
    raise SystemExit(f"post-connect SIGTERM expected 143, got {status}")
PY
grep -q ',true,false,4242,5252,terminated,' \
    "$post_connect_output/post/drop-process-control.csv"
grep -q ',all-present,' \
    "$post_connect_output/post/drop-process-status.csv"
printf 'PASS: deterministic interrupt between connect and SQL cleanup\n'

run_status 0 execute-fixture-success \
    run_cleanup "$READY_FIXTURE" "$WORK_ROOT/execute-success" \
        --execute \
        --expected-manifest-sha256 "$sha_one"

startup_recreate_fixture="$(copy_fixture startup-recreate)"
mutate_csv \
    "$startup_recreate_fixture/post/startup-relations.csv" \
    actual_relkind r
run_status 3 startup-recreation-post-gate \
    run_cleanup "$startup_recreate_fixture" "$WORK_ROOT/out-startup-recreate" \
        --execute \
        --expected-manifest-sha256 "$sha_one"

post_fingerprint_fixture="$(copy_fixture post-fingerprint)"
python3 - "$post_fingerprint_fixture/post/fingerprints.csv" <<'PY'
import csv
import sys
from pathlib import Path

path = Path(sys.argv[1])
with path.open(newline="", encoding="utf-8") as handle:
    reader = csv.DictReader(handle)
    fields = reader.fieldnames
    rows = list(reader)
for row in rows:
    if row["name"] == "leaderboard":
        row["sha256"] = "b" * 64
with path.open("w", newline="", encoding="utf-8") as handle:
    writer = csv.DictWriter(handle, fieldnames=fields)
    writer.writeheader()
    writer.writerows(rows)
PY
run_status 3 post-fingerprint-gate \
    run_cleanup "$post_fingerprint_fixture" "$WORK_ROOT/out-post-fingerprint" \
        --execute \
        --expected-manifest-sha256 "$sha_one"

python3 - "$WORK_ROOT/compose-input.json" <<'PY'
import json
import sys
from pathlib import Path

Path(sys.argv[1]).write_text(
    json.dumps({
        "name": "fixture",
        "services": {
            "postgres": {
                "environment": {
                    "POSTGRES_DB": "fstservice",
                    "POSTGRES_USER": "fst",
                    "POSTGRES_PASSWORD": "fixture-value",
                },
            },
            "fstservice": {
                "image": "example/fstservice:fixture",
                "command": [
                    "dotnet",
                    "--token=must-not-survive",
                    "leaderboard_current_entries",
                ],
                "environment": {
                    "DB_PASSWORD": "must-not-survive",
                    "TABLE_NAME": "leaderboard_entry_versions",
                    "ConnectionStrings__PostgreSQLPasswordConfigured": "true",
                    "ConnectionStrings__PostgreSQL": (
                        "Host=postgres;Port=5432;Database=fstservice;"
                        "Username=fst;Password=must-not-survive"
                    ),
                },
                "volumes": [{
                    "type": "bind",
                    "source": "/host/secrets/database-password",
                    "target": "/run/secrets/database-password",
                    "read_only": True,
                }],
            },
            "fstworker": {
                "environment": {
                    "ConnectionStrings__PostgreSQLPasswordConfigured": "true",
                    "ConnectionStrings__PostgreSQL": (
                        "Host=postgres;Port=5432;Database=fstservice;"
                        "Username=fst;Password=must-not-survive"
                    ),
                },
            },
        },
        "secrets": {"database-password": {"file": "/secret/path"}},
    }),
    encoding="utf-8",
)
PY
python3 "$HELPER" sanitize-compose-config \
    --output "$WORK_ROOT/compose-sanitized.json" \
    --binds-output "$WORK_ROOT/compose-binds.tsv" \
    < "$WORK_ROOT/compose-input.json"
python3 - \
    "$WORK_ROOT/compose-sanitized.json" \
    "$WORK_ROOT/compose-binds.tsv" <<'PY'
import csv
import json
import sys
from pathlib import Path

text = Path(sys.argv[1]).read_text(encoding="utf-8")
assert "must-not-survive" not in text
value = json.loads(text)
service = value["services"]["fstservice"]
assert service["environment"]["names"] == [
    "ConnectionStrings__PostgreSQL",
    "ConnectionStrings__PostgreSQLPasswordConfigured",
    "DB_PASSWORD",
    "TABLE_NAME",
]
assert service["environment"]["retiredValueReferences"] == [
    "leaderboard_entry_versions"
]
assert service["command"]["retiredReferences"] == [
    "leaderboard_current_entries"
]
assert value["databaseTarget"] == {
    "service": "postgres",
    "host": "postgres",
    "port": "5432",
    "database": "fstservice",
    "user": "fst",
    "passwordConfigured": True,
    "consumers": ["fstservice", "fstworker"],
}
with Path(sys.argv[2]).open(newline="", encoding="utf-8") as handle:
    rows = list(csv.DictReader(handle, delimiter="\t"))
assert rows[0]["source"] == "<redacted-secret-bind>"
assert rows[0]["target"] == "<redacted-secret-target>"
PY
printf 'PASS: sanitized production compose ownership projection\n'

python3 - "$WORK_ROOT/proc-fixture" <<'PY'
import os
import sys
from pathlib import Path

root = Path(sys.argv[1])
root.mkdir(parents=True, exist_ok=True)
processes = {
    100: (
        "/bin/sh",
        [
            "sh",
            "-c",
            "scan cmdline for psql and application_name=drop_target",
        ],
    ),
    101: (
        "/usr/bin/psql",
        [
            "psql",
            (
                "host=/var/run/postgresql dbname=fstservice "
                "application_name=drop_target connect_timeout=10"
            ),
        ],
    ),
    102: (
        "/usr/bin/psql",
        [
            "psql",
            (
                "host=/var/run/postgresql dbname=fstservice "
                "application_name=drop_target_suffix connect_timeout=10"
            ),
        ],
    ),
    103: (
        "/usr/bin/psql",
        [
            "psql",
            (
                "host=/var/run/postgresql dbname=fstservice "
                "application_name=drop_target connect_timeout=10"
            ),
        ],
    ),
}
for pid, (executable, arguments) in processes.items():
    directory = root / str(pid)
    directory.mkdir()
    os.symlink(executable, directory / "exe")
    (directory / "cmdline").write_bytes(
        b"\0".join(argument.encode() for argument in arguments) + b"\0"
    )
PY
scanner_matches="$(
    sh "$PROCESS_MATCHER" "$WORK_ROOT/proc-fixture" drop_target 103
)"
[[ "$scanner_matches" == "101" ]]
[[ -z "$(
    sh "$PROCESS_MATCHER" /proc fst_scanner_self_match_regression
)" ]]
printf 'PASS: exact psql scanner excludes self/control/prefix matches\n'

python3 - "$WORK_ROOT" <<'PY'
import io
import json
import sys
import zipfile
from pathlib import Path

root = Path(sys.argv[1])
(root / "rankings.json").write_text(
    json.dumps({"entries": [{"accountId": "a" * 32}]}),
    encoding="utf-8",
)
(root / "band-rankings.json").write_text(
    json.dumps({"entries": [{"teamKey": "b" * 32 + ":" + "c" * 32}]}),
    encoding="utf-8",
)
(root / "leaderboard.json").write_text(
    json.dumps({
        "songId": "song",
        "instrument": "Solo_Vocals",
        "count": 1,
        "totalEntries": 1,
        "entries": [{
            "accountId": "a" * 32,
            "score": 123,
            "rank": 1,
            "volatile": "discard",
        }],
        "volatile": "discard",
    }),
    encoding="utf-8",
)

worksheet = b"""<?xml version="1.0" encoding="UTF-8"?>
<worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main">
  <sheetData>
    <row r="1"><c r="A1" t="inlineStr"><is><t>Generated at now</t></is></c></row>
    <row r="2"><c r="A2" t="inlineStr"><is><t>stable</t></is></c></row>
  </sheetData>
</worksheet>
"""

def workbook_bytes():
    buffer = io.BytesIO()
    with zipfile.ZipFile(buffer, "w") as workbook:
        workbook.writestr("xl/worksheets/sheet1.xml", worksheet)
    return buffer.getvalue()

with zipfile.ZipFile(root / "player-export.zip", "w") as archive:
    archive.writestr(
        "player-all-20260804-120000.xlsx",
        workbook_bytes(),
    )
    archive.writestr(
        "player-bands-20260804-120000.xlsx",
        workbook_bytes(),
    )
PY

account_id="$(
    python3 "$HELPER" extract-account-id \
        --input "$WORK_ROOT/rankings.json"
)"
[[ "$account_id" == "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa" ]]
team_key="$(
    python3 "$HELPER" extract-team-key \
        --input "$WORK_ROOT/band-rankings.json"
)"
[[ "$team_key" == \
    "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb:cccccccccccccccccccccccccccccccc" ]]
python3 "$HELPER" normalize-leaderboard \
    --input "$WORK_ROOT/leaderboard.json" \
    --output "$WORK_ROOT/leaderboard.normalized.json"
python3 "$HELPER" normalize-player-export \
    --input "$WORK_ROOT/player-export.zip" \
    --output "$WORK_ROOT/player-export.normalized.json"
python3 "$HELPER" normalize-solo-export \
    --input "$WORK_ROOT/player-export.normalized.json" \
    --output "$WORK_ROOT/player-export.solo.json"
python3 - "$WORK_ROOT" <<'PY'
import json
import sys
from pathlib import Path

root = Path(sys.argv[1])
leaderboard = json.loads(
    (root / "leaderboard.normalized.json").read_text(encoding="utf-8")
)
assert "volatile" not in leaderboard
assert "volatile" not in leaderboard["entries"][0]
assert leaderboard["entries"][0]["score"] == 123

full = json.loads(
    (root / "player-export.normalized.json").read_text(encoding="utf-8")
)
assert "player-all-TIMESTAMP.xlsx" in full
assert "player-bands-TIMESTAMP.xlsx" in full
rows = full["player-all-TIMESTAMP.xlsx"]["xl/worksheets/sheet1.xml"]
assert rows == [[["A2", "stable"]]]

solo = json.loads(
    (root / "player-export.solo.json").read_text(encoding="utf-8")
)
assert "player-all-TIMESTAMP.xlsx" in solo
assert "player-bands-TIMESTAMP.xlsx" not in solo
PY
printf 'PASS: proven 13-surface public fingerprint normalizers\n'

python3 "$HELPER" render-drop-sql \
    --objects "$OBJECTS" \
    --catalog-expected-sql \
        "$WORK_ROOT/check-one/package/catalog-expected.sql" \
    --catalog-assert-sql \
        "$WORK_ROOT/check-one/package/catalog-assert.sql" \
    --retained-expected-sql \
        "$WORK_ROOT/check-one/package/retained-expected.sql" \
    --retained-assert-sql \
        "$WORK_ROOT/check-one/package/retained-assert.sql" \
    --output "$WORK_ROOT/drop.sql"
run_status 0 static-drop-validation \
    python3 "$HELPER" validate-drop-sql \
        --objects "$OBJECTS" \
        --sql "$WORK_ROOT/drop.sql"

python3 - "$WORK_ROOT/drop.sql" "$WORK_ROOT/drop-nonatomic.sql" <<'PY'
import sys
from pathlib import Path

source = Path(sys.argv[1]).read_text(encoding="utf-8")
marker = "\\echo 'FST_FAMILY_DROP_BEGIN score-observations'"
Path(sys.argv[2]).write_text(
    source.replace(marker, "COMMIT;\nBEGIN;\n" + marker, 1),
    encoding="utf-8",
)
PY
run_status 3 atomic-concurrency-regression \
    python3 "$HELPER" validate-drop-sql \
        --objects "$OBJECTS" \
        --sql "$WORK_ROOT/drop-nonatomic.sql"

python3 - "$WORK_ROOT/drop.sql" "$WORK_ROOT/drop-missing-rls-gate.sql" <<'PY'
import sys
from pathlib import Path

source = Path(sys.argv[1]).read_text(encoding="utf-8")
Path(sys.argv[2]).write_text(
    source.replace("Row security is enabled:", "RLS gate removed:", 1),
    encoding="utf-8",
)
PY
run_status 3 forced-rls-under-lock-regression \
    python3 "$HELPER" validate-drop-sql \
        --objects "$OBJECTS" \
        --sql "$WORK_ROOT/drop-missing-rls-gate.sql"

python3 - \
    "$OBJECTS" \
    "$WORK_ROOT/drop.sql" \
    "$SCRIPT_DIR/sql/postgres-retired-schema-cleanup/public-fingerprints.tsv" <<'PY'
import csv
import re
import sys
from collections import Counter
from pathlib import Path

with Path(sys.argv[1]).open(newline="", encoding="utf-8") as handle:
    objects = list(csv.DictReader(handle, delimiter="\t"))
sql = Path(sys.argv[2]).read_text(encoding="utf-8")
with Path(sys.argv[3]).open(newline="", encoding="utf-8") as handle:
    fingerprints = list(csv.DictReader(handle, delimiter="\t"))
assert len(objects) == 61
assert Counter(row["family"] for row in objects) == {
    "logical-shadow": 21,
    "score-observations": 3,
    "band-song-projection": 5,
    "aggregate-ranking-deltas": 32,
}
assert not re.search(r"\bCASCADE\b", sql, re.IGNORECASE)
assert "DROP INDEX" not in sql
assert "TRUNCATE " not in sql
assert len(re.findall(r"^DROP (?:TABLE|VIEW|SEQUENCE) ", sql, re.MULTILINE)) == 61
assert len(re.findall(r"^BEGIN;$", sql, re.MULTILINE)) == 1
assert len(re.findall(r"^COMMIT;$", sql, re.MULTILINE)) == 1
assert sql.count("FST_ALL_COMMITTED") == 1
first_drop = min(
    sql.index(f'DROP {kind} "public"."{row["name"]}";')
    for row in objects
    for kind in [{
        "table": "TABLE",
        "partitioned_table": "TABLE",
        "view": "VIEW",
        "sequence": "SEQUENCE",
    }[row["object_type"]]]
)
assert sql.rindex("LOCK TABLE ") < first_drop
assert sql.rindex("LOCK TABLE ") < sql.index("DO $catalog_signature$")
assert sql.index("DO $catalog_signature$") < sql.index("DO $objects$")
assert sql.count("Complete cleanup catalog signature drifted") == 2
assert sql.count("Row security is enabled:") == 61
assert "SET LOCAL row_security = off;" in sql
assert sql.rindex("DO $catalog_signature$") < first_drop
assert "5067481511116519501" in sql
assert "5067481511116519502" in sql
assert (
    'ALTER SEQUENCE "public"."player_score_observations_id_seq"\n'
    '    OWNED BY "public"."player_score_observations"."id";'
) in sql
assert sql.index("DO $partition_set$") < sql.index(
    'LOCK TABLE "public"."leaderboard_current_entries_bass"'
)
assert "Retained payload changed: public.leaderboard_logical_write_metrics" in sql
assert "Retained payload changed: public.band_song_team_ranking_state" in sql
assert (
    'IF EXISTS (SELECT 1 FROM '
    '"public"."leaderboard_logical_write_metrics" LIMIT 1)'
) not in sql
assert (
    'IF EXISTS (SELECT 1 FROM '
    '"public"."band_song_team_ranking_state" LIMIT 1)'
) not in sql
assert sql.index('DROP VIEW "public"."player_score_observation_union";') < \
       sql.index('DROP SEQUENCE "public"."player_score_observations_id_seq";') < \
       sql.index('DROP TABLE "public"."player_score_observations";')
for child, parent in [
    ("leaderboard_current_entries_bass", "leaderboard_current_entries"),
    ("leaderboard_entry_versions_bass", "leaderboard_entry_versions"),
    ("ranking_deltas_pro_bass", "ranking_deltas"),
    ("ranking_delta_tiers_pro_bass", "ranking_delta_tiers"),
    ("rank_history_deltas_pro_bass", "rank_history_deltas"),
]:
    assert sql.index(f'DROP TABLE "public"."{child}";') < \
           sql.index(f'DROP TABLE "public"."{parent}";')
for forbidden in [
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
]:
    assert f'"{forbidden}"' not in sql
assert [row["name"] for row in fingerprints if row["gate"] == "true"] == [
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
PY
printf 'PASS: exact object count, order, sequence, and active-table exclusion\n'

python3 - \
    "$SCRIPT_DIR/sql/postgres-retired-schema-cleanup" \
    "$SCRIPT" \
    "$SCRIPT_DIR/postgres-capacity-guard.sh" \
    "$PROCESS_MATCHER" <<'PY'
import re
import sys
from pathlib import Path

sql_dir = Path(sys.argv[1])
captures = sorted(sql_dir.glob("capture-*.sql"))
assert len(captures) == 9
for path in captures:
    text = path.read_text(encoding="utf-8")
    assert "BEGIN TRANSACTION READ ONLY;" in text, path
    assert "SET LOCAL lock_timeout = '2s';" in text, path
    assert "SET LOCAL statement_timeout = '15s';" in text, path
    assert "SET LOCAL row_security = off;" in text, path
    assert "TO STDOUT WITH (FORMAT CSV, HEADER TRUE);" in text, path
    assert not re.search(
        r"^\s*(DROP|TRUNCATE|DELETE|UPDATE|ALTER)\s",
        text,
        re.IGNORECASE | re.MULTILINE,
    ), path

catalog_query = (sql_dir / "catalog-signature-query.sql").read_text(
    encoding="utf-8"
)
for category in [
    "relation",
    "column",
    "constraint",
    "index",
    "trigger",
    "policy",
    "view",
    "dependent-view",
    "rule",
    "publication",
    "sequence",
    "partition",
    "incoming-inheritance",
    "dependency",
    "shared-dependency",
    "routine-reference",
]:
    assert f"SELECT '{category}'" in catalog_query, category
for required in [
    "attnum",
    "atttypmod",
    "attnotnull",
    "attidentity",
    "attgenerated",
    "attcollation",
    "pg_get_expr",
    "pg_get_constraintdef",
    "pg_get_indexdef",
    "pg_get_triggerdef",
    "relrowsecurity",
    "relforcerowsecurity",
    "last_value",
    "is_called",
    "relpartbound",
    "pg_describe_object",
    "pg_shdepend",
]:
    assert required in catalog_query, required

script = Path(sys.argv[2]).read_text(encoding="utf-8")
assert "--lock-wait-timeout=5s" in script
assert "timeout --signal=TERM --kill-after=10s 2m" in script
assert 'PGOPTIONS="-c statement_timeout=30s -c row_security=off"' in script
assert "timeout --signal=TERM --kill-after=30s 7m" in script
assert "-e PGCONNECT_TIMEOUT=10" in script
assert "atomic drop process reconciliation" in script
assert "FST_ALL_COMMITTED_RECONCILED_BY_ABSENCE" in script
assert "com.docker.compose.project.config_files" in script
compose_lines = [
    line for line in script.splitlines() if "docker compose " in line
]
assert compose_lines
assert all("COMPOSE_FILE_ARGS" in line for line in compose_lines)
assert "sanitize-compose-config" in script
assert "production-bind-config" in script
assert "rg -n -o" in script
assert "FST_BACKEND_PID=" in script
assert "FST_CONTAINER_PSQL_PID=" in script
assert "pg_cancel_backend" in script
assert "pg_terminate_backend" in script
assert "drop-process-control.csv" in script
assert "trap 'on_signal INT 130' INT" in script
assert "trap 'on_signal TERM 143' TERM" in script
assert 'PG_SOCKET_DIR="/var/run/postgresql"' in script
assert 'PGOPTIONS="-c row_security=off"' in script
assert "PGSERVICEFILE" in script
assert '-h "$PG_SOCKET_DIR"' in script
assert "pre-destructive scratch round-trip proof" in script
assert "createdb -h" in script
assert "dropdb --if-exists -h" in script
assert '"/proc/$DROP_LOCAL_PID/stat"' in script
assert '"/proc/$DROP_LOCAL_PID/cmdline"' in script
assert "DROP_LOCAL_WAIT_COMPLETED" in script
assert "coproc DROP_LAUNCHER" in script
assert "launch-barrier-ready" in script
assert "connect-barrier-ready" in script
assert "post-connect-barrier-ready" in script
assert "sql-released" in script
assert "sync -f" in script
launch = script[
    script.index("run_live_atomic_drop()"):
    script.index("reconcile_drop_process()")
]
assert launch.index("post-connect-barrier-ready") < launch.index(
    'cat "$runtime_sql"'
)
assert "SET statement_timeout = '15s'; SELECT last_value" in script
source_scan = script[
    script.index("capture_source_references()"):
    script.index("normalize_dump()")
]
assert "if (( status > 1 )); then" in source_scan
assert "|| true" not in source_scan

capacity_guard = Path(sys.argv[3]).read_text(encoding="utf-8")
assert "-h /var/run/postgresql" in capacity_guard
assert '-p "$PG_PORT"' in capacity_guard
assert "-e PGHOST=" in capacity_guard
assert "-e PGPORT=" in capacity_guard
assert 'PGOPTIONS="-c row_security=off"' in capacity_guard

matcher = Path(sys.argv[4]).read_text(encoding="utf-8")
assert '[ "${executable##*/}" = "psql" ]' in matcher
assert 'application_name=$application_name' in matcher
assert '[ "$candidate" = "$$" ]' in matcher
assert '[ "$candidate" = "$PPID" ]' in matcher
cleanup = script[
    script.index("cleanup_active_drop()"):
    script.index("run_live_atomic_drop()")
]
assert cleanup.index("terminate_recorded_local_child") < cleanup.index(
    "discovery_ambiguous=true"
)
ambiguity_to_local_kill = cleanup[
    cleanup.index("discovery_ambiguous=true"):
    cleanup.index("local empty_polls")
]
assert "DROP_CLEANUP_RUNNING=false" not in ambiguity_to_local_kill
assert "write_drop_control ambiguous" not in ambiguity_to_local_kill
assert "empty_polls" in cleanup
assert "pg_terminate_backend(pid)" in cleanup
assert 'terminate_container_psql_exact "$late_pid"' in cleanup
PY
printf 'PASS: bounded capture, catalog signature, rollback timeout, and rg checks\n'

EVIDENCE_ROOT="/mnt/docker-storage/Docker/FestivalServiceTracker/fst-data/evidence/branch-cleanup-20260803"
if [[ -f "$EVIDENCE_ROOT/retired-schema-rollback.sql" \
      && -f "$EVIDENCE_ROOT/ranking-deltas/rollback-schema.sql" ]]; then
    python3 - "$OBJECTS" \
        "$EVIDENCE_ROOT/retired-schema-rollback.sql" \
        "$EVIDENCE_ROOT/ranking-deltas/rollback-schema.sql" <<'PY'
import csv
import re
import sys
from pathlib import Path

with Path(sys.argv[1]).open(newline="", encoding="utf-8") as handle:
    expected = {
        f"{row['schema']}.{row['name']}"
        for row in csv.DictReader(handle, delimiter="\t")
    }
actual = set()
for filename in sys.argv[2:]:
    text = Path(filename).read_text(encoding="utf-8")
    for match in re.finditer(
        r"^-- Name: (.*?); Type: (TABLE|VIEW|SEQUENCE); Schema: (.*?); Owner:",
        text,
        re.MULTILINE,
    ):
        name, _kind, schema = match.groups()
        actual.add(f"{schema}.{name}")
assert expected == actual, (
    f"missing={sorted(expected - actual)} extra={sorted(actual - expected)}"
)
PY
    printf 'PASS: exact object list matches retained rollback evidence\n'
fi

printf 'All retired schema cleanup fixture/static tests passed.\n'
