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
PG_TEST_CONTAINER=""
STARTUP_TEST_SOURCE=""
STARTUP_TEST_CLONE=""
STARTUP_TEST_STALE=""
STARTUP_TEST_NETWORK=""
STARTUP_TEST_ALIAS=""
STARTUP_TEST_ALT_IMAGE=""

cleanup() {
    if [[ -n "$PG_TEST_CONTAINER" ]]; then
        docker rm -f "$PG_TEST_CONTAINER" >/dev/null 2>&1 || true
    fi
    if [[ -n "$STARTUP_TEST_CLONE" ]]; then
        docker rm -f "$STARTUP_TEST_CLONE" >/dev/null 2>&1 || true
    fi
    if [[ -n "$STARTUP_TEST_STALE" ]]; then
        docker rm -f "$STARTUP_TEST_STALE" >/dev/null 2>&1 || true
    fi
    if [[ -n "$STARTUP_TEST_SOURCE" ]]; then
        docker rm -f "$STARTUP_TEST_SOURCE" >/dev/null 2>&1 || true
    fi
    if [[ -n "$STARTUP_TEST_NETWORK" ]]; then
        docker network rm "$STARTUP_TEST_NETWORK" >/dev/null 2>&1 || true
    fi
    if [[ -n "$STARTUP_TEST_ALIAS" ]]; then
        docker image rm "$STARTUP_TEST_ALIAS" >/dev/null 2>&1 || true
    fi
    if [[ -n "$STARTUP_TEST_ALT_IMAGE" ]]; then
        docker image rm "$STARTUP_TEST_ALT_IMAGE" >/dev/null 2>&1 || true
    fi
    rm -rf "$WORK_ROOT"
}
trap cleanup EXIT
mkdir -p "$WORK_ROOT"

python3 - \
    "$OBJECTS" \
    "$READY_FIXTURE" \
    "$SCRIPT_DIR/sql/postgres-retired-schema-cleanup/public-fingerprints.tsv" \
    "$SCRIPT_DIR/postgres-capacity-guard.sh" <<'PY'
import csv
import hashlib
import json
import sys
from pathlib import Path

objects_path = Path(sys.argv[1])
fixture = Path(sys.argv[2])
fingerprint_spec = Path(sys.argv[3])
capacity_guard = Path(sys.argv[4])
pre = fixture / "pre"
post = fixture / "post"
rollback = fixture / "rollback"
rollback_data = fixture / "rollback-data"
retained = pre / "retained-data"
for directory in [pre, post, rollback, rollback_data, retained]:
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
    inherited_partition_column = (
        item["object_type"] == "table"
        and bool(item["parent_name"])
        and not item["owner_column"]
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
        "statistics_target": (
            "" if inherited_partition_column else "-1"
        ),
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
        "statisticsTarget": (
            None
            if row["statistics_target"] == ""
            else int(row["statistics_target"])
        ),
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
(pre / "capacity-guard.policy.json").write_text(
    json.dumps({
        "schemaVersion": 1,
        "actionClass": "reclaim",
        "guardScriptSha256": hashlib.sha256(
            capacity_guard.read_bytes()
        ).hexdigest(),
        "effectiveParameters": {
            "estimatedFullScrapeGrowthBytes": 60392999803,
            "expectedFullScrapesPerDay": 2,
            "minimumHeadroomDays": 7,
            "minimumHeadroomBytesOverride": 0,
            "transientBuildBytes": 0,
            "requiredScratchBytes": 0,
            "expectedReclaimBytes": 0,
        },
    }, sort_keys=True),
    encoding="utf-8",
)
(pre / "capacity-guard.json").write_text(
    json.dumps({
        "sampledAtUtc": "2026-08-04T12:00:00+00:00",
        "actionClass": "reclaim",
        "decision": "accepted_with_capacity_alert",
        "reasons": ["fixture capacity alert"],
        "capacity": {
            "estimatedFullScrapeGrowthBytes": 60392999803,
            "expectedFullScrapesPerDay": 2,
            "minimumHeadroomDays": 7,
            "minimumHeadroomBytesOverride": 0,
            "transientBuildBytes": 0,
            "requiredScratchBytes": 0,
            "expectedReclaimBytes": 0,
            "reclaimAllowed": True,
        },
    }, sort_keys=True),
    encoding="utf-8",
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
container_config_attestation = {
    "schemaVersion": 1,
    "success": True,
    "failures": [],
    "services": {
        service: {
            "containerId": container_id,
            "imageId": f"sha256:{service}",
            "command": {
                "present": True,
                "argvSha256": hashlib.sha256(
                    json.dumps(
                        [service],
                        separators=(",", ":"),
                    ).encode()
                ).hexdigest(),
            },
            "entrypoint": {
                "present": False,
                "argvSha256": hashlib.sha256(b"[]").hexdigest(),
            },
            "environment": {
                "names": [],
                "requiredNames": [],
                "nonSecretValueSha256": {},
                "secretConfiguredNames": [],
            },
            "mounts": [],
            "networks": {
                "festival-service-tracker_default": {
                    "aliases": [service],
                    "ipAddress": f"172.31.0.{index}",
                },
            },
            "composeLabels": {
                "project": "festival-service-tracker",
                "service": service,
                "workingDir": (
                    "/home/sfenton/Docker/FestivalServiceTracker"
                ),
                "configFilesSha256": "9" * 64,
            },
            "restartPolicy": (
                "no" if service == "fstworker" else "unless-stopped"
            ),
        }
        for index, (service, container_id) in enumerate(
            [
                ("postgres", "a" * 64),
                ("fstservice", "b" * 64),
                ("festivalweb", "c" * 64),
                ("fstworker", "d" * 64),
            ],
            2,
        )
    },
    "databaseHostMapping": {
        "consumer": "fstservice",
        "host": "postgres",
        "sharedNetworks": ["festival-service-tracker_default"],
        "postgresContainerId": "a" * 64,
        "postgresSystemIdentifier": "7429301450012345678",
        "aliasOwners": {
            "festival-service-tracker_default": [{
                "containerId": "a" * 64,
                "containerName": "fst-postgres",
                "composeProject": "festival-service-tracker",
                "composeService": "postgres",
                "ipAddress": "172.31.0.2",
            }],
        },
    },
}
container_config_attestation["fingerprintBindings"] = {
    request["name"]: {
        "service": (
            "fstservice" if request["name"] == "readyz" else "festivalweb"
        ),
        "containerId": (
            "b" * 64 if request["name"] == "readyz" else "c" * 64
        ),
        "hostIp": "127.0.0.1",
        "hostPort": (
            "8081" if request["name"] == "readyz" else "3001"
        ),
        "containerPort": 8080 if request["name"] == "readyz" else 80,
        "protocol": "tcp",
        "baseUrl": (
            "http://127.0.0.1:8081"
            if request["name"] == "readyz"
            else "http://127.0.0.1:3001"
        ),
    }
    for request in fingerprint_requests
}
container_config_attestation["fingerprintBaseUrls"] = {
    "service": "http://127.0.0.1:8081",
    "web": "http://127.0.0.1:3001",
}
(pre / "container-config-attestation.json").write_text(
    json.dumps(container_config_attestation, sort_keys=True),
    encoding="utf-8",
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
    restrict_key = (
        "A" * 48
        + {
            "logical-shadow": "01",
            "score-observations": "02",
            "band-song-projection": "03",
            "aggregate-ranking-deltas": "04",
        }[family]
    )
    (rollback / f"{family}.sql").write_text(
        (
            f"\\restrict {restrict_key}\n"
            f"-- fixture rollback for {family}\n"
            "SET statement_timeout = 0;\n"
            "SET lock_timeout = 0;\n"
            "SET idle_in_transaction_session_timeout = 0;\n"
            "SET transaction_timeout = 0;\n"
            "SELECT 1;\n"
            f"\\unrestrict {restrict_key}\n"
        ),
        encoding="utf-8",
    )
(rollback_data / "score-observations.data.sql").write_text(
    "-- Preserve the captured empty-table sequence state.\n"
    "SELECT pg_catalog.setval("
    "'public.player_score_observations_id_seq'::regclass, "
    "210281757, true);\n",
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
(post / "container-config-attestation.json").write_bytes(
    (pre / "container-config-attestation.json").read_bytes()
)
write_csv(post / "containers.csv", container_fields, containers)
write_csv(post / "health.csv", ["check", "status", "detail"], [
    {"check": "postgres-readiness", "status": "ok", "detail": "fixture"},
    {"check": "readyz", "status": "ok", "detail": "fixture"},
    {"check": "web-shell", "status": "ok", "detail": "fixture"},
    {"check": "service-info", "status": "ok", "detail": "fixture"},
    {"check": "capacity-guard", "status": "ok", "detail": "fixture"},
])
(post / "capacity-guard.policy.json").write_bytes(
    (pre / "capacity-guard.policy.json").read_bytes()
)
(post / "capacity-guard.json").write_bytes(
    (pre / "capacity-guard.json").read_bytes()
)
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
    post / "startup-image-attestation.csv",
    [
        "container_name",
        "container_id",
        "source_container_id",
        "manifest_image_id",
        "compose_image_reference_sha256",
        "compose_image_id_before_create",
        "compose_image_id_before_start",
        "prestart_actual_image_id",
        "prestart_config_image",
        "prestart_state",
        "prestart_running",
        "prestart_command_sha256",
        "pinned_database_host",
        "pinned_database_ip",
        "pinned_database_network",
        "pinned_postgres_container_id",
        "poststart_actual_image_id",
        "poststart_config_image",
        "exit_code",
        "creation_attestation_sha256",
        "prestart_attestation_sha256",
        "database_routing_attestation_sha256",
        "database_target_attestation_sha256",
        "initializer_release_sha256",
    ],
    [{
        "container_name": "fst-retired-schema-init-fixture",
        "container_id": "5" * 64,
        "source_container_id": "b" * 64,
        "manifest_image_id": "sha256:" + "1" * 64,
        "compose_image_reference_sha256": "1" * 64,
        "compose_image_id_before_create": "sha256:" + "1" * 64,
        "compose_image_id_before_start": "sha256:" + "1" * 64,
        "prestart_actual_image_id": "sha256:" + "1" * 64,
        "prestart_config_image": "sha256:" + "1" * 64,
        "prestart_state": "created",
        "prestart_running": "false",
        "prestart_command_sha256": hashlib.sha256(
            b'["--initialize-schema-only"]'
        ).hexdigest(),
        "pinned_database_host": "postgres",
        "pinned_database_ip": "172.31.0.2",
        "pinned_database_network": "festival-service-tracker_default",
        "pinned_postgres_container_id": "a" * 64,
        "poststart_actual_image_id": "sha256:" + "1" * 64,
        "poststart_config_image": "sha256:" + "1" * 64,
        "exit_code": "0",
        "creation_attestation_sha256": "6" * 64,
        "prestart_attestation_sha256": "7" * 64,
        "database_routing_attestation_sha256": "8" * 64,
        "database_target_attestation_sha256": "9" * 64,
        "initializer_release_sha256": "a" * 64,
    }],
)
startup_attestation_common = {
    "schemaVersion": 1,
    "expectedManifestSha256": "__MANIFEST_SHA256__",
    "sourceContainerId": "b" * 64,
    "containerName": "fst-retired-schema-init-fixture",
    "containerId": "5" * 64,
    "expectedImageId": "sha256:" + "1" * 64,
    "composeImageReferenceSha256": "1" * 64,
    "composeImageResolvedId": "sha256:" + "1" * 64,
    "actualImageId": "sha256:" + "1" * 64,
    "configuredImage": "sha256:" + "1" * 64,
    "state": {
        "status": "created",
        "running": False,
        "pid": 0,
        "startedAt": "0001-01-01T00:00:00Z",
    },
    "commandSha256": hashlib.sha256(
        b'["--initialize-schema-only"]'
    ).hexdigest(),
    "sourceConfigurationSha256": "8" * 64,
    "actualConfigurationSha256": "8" * 64,
    "networks": ["festival-service-tracker_default"],
    "networkAliases": ["fst-retired-schema-init-fixture"],
    "networkResolutionNames": ["fst-retired-schema-init-fixture"],
    "databaseHostPin": {
        "host": "postgres",
        "ipAddress": "172.31.0.2",
        "network": "festival-service-tracker_default",
        "postgresContainerId": "a" * 64,
        "extraHost": "postgres:172.31.0.2",
    },
    "autoRemove": False,
    "restartPolicy": "no",
    "portBindingsPresent": False,
}
for filename, phase in [
    ("startup-image-creation-attestation.json", "created-prestart"),
    ("startup-image-prestart-attestation.json", "attested-before-start"),
]:
    value = dict(startup_attestation_common)
    value["phase"] = phase
    (post / filename).write_text(
        json.dumps(value, sort_keys=True, indent=2) + "\n",
        encoding="utf-8",
    )
(post / "startup-image-attested-before-start.sha256").write_text(
    "7" * 64 + "\n",
    encoding="utf-8",
)
startup_routing = post / "startup-database-routing"
startup_routing.mkdir()
write_csv(
    startup_routing / "production-target-attestation.csv",
    target_attestation_fields,
    [target_attestation],
)
(startup_routing / "attestation.json").write_text(
    json.dumps({
        "schemaVersion": 1,
        "success": True,
        "acceptedManifestSha256": "__MANIFEST_SHA256__",
        "startupContainerId": "5" * 64,
        "attachedNetworks": ["festival-service-tracker_default"],
        "databaseTarget": {
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
        },
        "databaseHostPin": {
            "host": "postgres",
            "ipAddress": "172.31.0.2",
            "network": "festival-service-tracker_default",
            "postgresContainerId": "a" * 64,
            "extraHost": "postgres:172.31.0.2",
        },
        "postgres": {
            "containerId": "a" * 64,
            "systemIdentifier": "7429301450012345678",
            "endpoints": {
                "festival-service-tracker_default": {
                    "networkId": "e" * 64,
                    "ipAddress": "172.31.0.2",
                    "hostAliasPresent": True,
                },
            },
        },
        "aliasOwners": {
            "festival-service-tracker_default": [{
                "containerId": "a" * 64,
                "containerName": "fst-postgres",
                "state": "running",
                "running": True,
                "networkId": "e" * 64,
                "ipAddress": "172.31.0.2",
                "resolutionSources": ["Aliases", "DNSNames"],
                "resolutionNames": ["fst-postgres", "postgres"],
            }],
        },
        "failures": [],
    }, sort_keys=True, indent=2) + "\n",
    encoding="utf-8",
)
write_csv(
    startup_routing / "status.csv",
    ["status", "manifest_sha256"],
    [{"status": "passed", "manifest_sha256": "__MANIFEST_SHA256__"}],
)
(post / "startup-initializer-release.json").write_text(
    json.dumps({
        "schemaVersion": 1,
        "acceptedManifestSha256": "__MANIFEST_SHA256__",
        "containerId": "5" * 64,
        "prestartAttestationSha256": "7" * 64,
        "databaseRoutingAttestationSha256": "8" * 64,
        "databaseTargetAttestationSha256": "9" * 64,
        "released": True,
    }, sort_keys=True, indent=2) + "\n",
    encoding="utf-8",
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

run_status 0 hostile-capacity-environment-ignored \
    env \
        EXPECTED_RECLAIM_BYTES=999999999999999 \
        TRANSIENT_BUILD_BYTES=1 \
        REQUIRED_SCRATCH_BYTES=1 \
        ESTIMATED_FULL_SCRAPE_GROWTH_BYTES=1 \
        EXPECTED_FULL_SCRAPES_PER_DAY=1 \
        MINIMUM_HEADROOM_DAYS=1 \
        MINIMUM_HEADROOM_BYTES_OVERRIDE=1 \
        FST_RETIRED_SCHEMA_TEST_MODE=1 \
        "$SCRIPT" \
        --fixture-dir "$READY_FIXTURE" \
        --output "$WORK_ROOT/check-hostile-capacity" \
        --parity-evidence "$READY_FIXTURE/parity.json"
sha_hostile="$(cat "$WORK_ROOT/check-hostile-capacity/manifest-sha256.txt")"
[[ "$sha_hostile" == "$sha_one" ]] || {
    printf 'FAIL: hostile capacity environment changed manifest\n' >&2
    exit 1
}
printf 'PASS: hostile capacity environment cannot weaken policy\n'

cat > "$WORK_ROOT/valid-pg-dump.sql" <<'SQL'
\restrict AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA
SET statement_timeout = 0;
SET lock_timeout = 0;
SET idle_in_transaction_session_timeout = 0;
SET transaction_timeout = 0;
CREATE TABLE public.fixture (id bigint);
\unrestrict AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA
SQL
run_status 0 pg-dump-boundary-canonicalization \
    python3 "$HELPER" canonicalize-pg-dump \
        --input "$WORK_ROOT/valid-pg-dump.sql" \
        --output "$WORK_ROOT/valid-pg-dump.canonical.sql"
grep -q '^-- DIGEST-ONLY CANONICAL PG_DUMP; NEVER EXECUTE THIS FILE.$' \
    "$WORK_ROOT/valid-pg-dump.canonical.sql"
grep -q '^\\restrict <PG_DUMP_RANDOM_KEY>$' \
    "$WORK_ROOT/valid-pg-dump.canonical.sql"
run_status 0 bounded-executable-pg-dump \
    python3 "$HELPER" prepare-executable-pg-dump \
        --input "$WORK_ROOT/valid-pg-dump.sql" \
        --output "$WORK_ROOT/valid-pg-dump.executable.sql"
grep -q '^\\restrict AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA$' \
    "$WORK_ROOT/valid-pg-dump.executable.sql"
grep -q "^SET statement_timeout = '30s';$" \
    "$WORK_ROOT/valid-pg-dump.executable.sql"
grep -q "^SET lock_timeout = '5s';$" \
    "$WORK_ROOT/valid-pg-dump.executable.sql"
grep -q "^SET idle_in_transaction_session_timeout = '60s';$" \
    "$WORK_ROOT/valid-pg-dump.executable.sql"
grep -q "^SET transaction_timeout = '5min';$" \
    "$WORK_ROOT/valid-pg-dump.executable.sql"
grep -q '^SET statement_timeout = 0;$' "$WORK_ROOT/valid-pg-dump.sql"
! grep -q '^SET statement_timeout = 0;$' \
    "$WORK_ROOT/valid-pg-dump.executable.sql"

cat > "$WORK_ROOT/unsafe-unrestrict.sql" <<'SQL'
\restrict AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA
\unrestrict AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA
SELECT 1;
\unrestrict AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA
SQL
run_status 2 pg-dump-extra-unrestrict-rejected \
    python3 "$HELPER" canonicalize-pg-dump \
        --input "$WORK_ROOT/unsafe-unrestrict.sql" \
        --output "$WORK_ROOT/unsafe-unrestrict.canonical.sql"

cat > "$WORK_ROOT/unsafe-shell.sql" <<'SQL'
\restrict AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA
\! echo unsafe
\unrestrict AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA
SQL
run_status 2 pg-dump-shell-command-rejected \
    python3 "$HELPER" canonicalize-pg-dump \
        --input "$WORK_ROOT/unsafe-shell.sql" \
        --output "$WORK_ROOT/unsafe-shell.canonical.sql"

cat > "$WORK_ROOT/unsafe-connect.sql" <<'SQL'
\restrict AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA
\connect clone
\unrestrict AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA
SQL
run_status 2 pg-dump-connect-command-rejected \
    python3 "$HELPER" canonicalize-pg-dump \
        --input "$WORK_ROOT/unsafe-connect.sql" \
        --output "$WORK_ROOT/unsafe-connect.canonical.sql"
printf 'PASS: pg_dump meta-command injection rejection\n'

python3 - \
    "$HELPER" \
    "$WORK_ROOT/drop-sql-source.sql" \
    "$WORK_ROOT/drop-sql-proof.json" \
    "$WORK_ROOT/drop-sql-streamed.sql" <<'PY'
import hashlib
import json
import subprocess
import sys
import time
from pathlib import Path

helper, source_name, proof_name, output_name = sys.argv[1:]
source = Path(source_name)
proof = Path(proof_name)
output = Path(output_name)
original = b"SELECT 'SAFE_DROP_SQL';\n"
source.write_bytes(original)
expected = hashlib.sha256(original).hexdigest()
with output.open("wb") as output_handle:
    process = subprocess.Popen(
        [
            sys.executable,
            helper,
            "stream-verified-drop-sql",
            "--input", str(source),
            "--expected-sha256", expected,
            "--proof", str(proof),
            "--wait-for-release",
        ],
        stdin=subprocess.PIPE,
        stdout=output_handle,
        stderr=subprocess.PIPE,
    )
    for _ in range(100):
        if proof.is_file() and proof.stat().st_size:
            break
        if process.poll() is not None:
            raise SystemExit(process.stderr.read().decode())
        time.sleep(0.05)
    else:
        process.terminate()
        process.wait(timeout=10)
        raise SystemExit("immutable SQL proof was not created")
    source.write_bytes(original + b"SELECT 'APPENDED_EVIL_SQL';\n")
    process.stdin.write(b"RELEASE\n")
    process.stdin.flush()
    process.stdin.close()
    status = process.wait(timeout=20)
    if status:
        raise SystemExit(process.stderr.read().decode())
assert output.read_bytes() == original
metadata = json.loads(proof.read_text(encoding="utf-8"))
assert metadata["sha256"] == expected
assert metadata["sealedMemfd"]["seals"] == \
    metadata["sealedMemfd"]["requiredSeals"]
PY
! grep -q 'APPENDED_EVIL_SQL' "$WORK_ROOT/drop-sql-streamed.sql"
printf 'PASS: concurrent drop.sql append cannot alter sealed stream\n'

python3 - \
    "$HELPER" \
    "$WORK_ROOT/manifest-source.json" \
    "$WORK_ROOT/manifest-drop-source.sql" \
    "$WORK_ROOT/manifest-drop-proof.json" \
    "$WORK_ROOT/manifest-drop-streamed.sql" <<'PY'
import hashlib
import json
import subprocess
import sys
import time
from pathlib import Path

helper, manifest_name, drop_name, proof_name, output_name = sys.argv[1:]
manifest_path = Path(manifest_name)
drop_path = Path(drop_name)
proof_path = Path(proof_name)
output_path = Path(output_name)
safe_sql = b"SELECT 'SAFE_SEALED_SQL';\n"
safe_drop_hash = hashlib.sha256(safe_sql).hexdigest()
safe_manifest = (
    json.dumps(
        {
            "schemaVersion": 1,
            "dropSqlSha256": safe_drop_hash,
            "containers": [
                {
                    "service": "fstservice",
                    "container_id": "2" * 64,
                    "image_id": "sha256:" + "1" * 64,
                    "compose_image_id": "sha256:" + "1" * 64,
                },
                {
                    "service": "postgres",
                    "container_id": "4" * 64,
                    "image_id": "sha256:" + "5" * 64,
                    "compose_image_id": "sha256:" + "5" * 64,
                },
            ],
            "productionComposeOwnership": {
                "fstserviceImageReferenceSha256": "3" * 64,
            },
            "productionDatabaseTarget": {
                "runtime": {
                    "configured_host": "postgres",
                    "configured_port": "5432",
                    "configured_database": "fstservice",
                    "configured_user": "fst",
                    "container_id": "4" * 64,
                    "runtime_address": "local-socket",
                    "runtime_port": "5432",
                    "runtime_database": "fstservice",
                    "runtime_user": "fst",
                    "in_recovery": "false",
                    "system_identifier": "7429301450012345678",
                },
            },
            "containerConfigAttestation": {
                "services": {
                    "fstservice": {
                        "networks": {"safe-network": {}},
                    },
                },
                "databaseHostMapping": {
                    "host": "postgres",
                    "sharedNetworks": ["safe-network"],
                    "postgresContainerId": "4" * 64,
                    "postgresSystemIdentifier": "7429301450012345678",
                },
            },
        },
        sort_keys=True,
        separators=(",", ":"),
    )
    + "\n"
).encode()
manifest_path.write_bytes(safe_manifest)
drop_path.write_bytes(safe_sql)
expected_manifest = hashlib.sha256(safe_manifest).hexdigest()
with output_path.open("wb") as output_handle:
    process = subprocess.Popen(
        [
            sys.executable,
            helper,
            "stream-verified-manifest-drop-sql",
            "--manifest", str(manifest_path),
            "--expected-manifest-sha256", expected_manifest,
            "--drop-sql", str(drop_path),
            "--proof", str(proof_path),
            "--wait-for-release",
        ],
        stdin=subprocess.PIPE,
        stdout=output_handle,
        stderr=subprocess.PIPE,
    )
    for _ in range(100):
        if proof_path.is_file() and proof_path.stat().st_size:
            break
        if process.poll() is not None:
            raise SystemExit(process.stderr.read().decode())
        time.sleep(0.05)
    else:
        process.terminate()
        process.wait(timeout=10)
        raise SystemExit("sealed manifest/SQL proof was not created")
    evil_sql = b"SELECT 'APPENDED_EVIL_SQL';\n"
    evil_hash = hashlib.sha256(evil_sql).hexdigest()
    manifest_path.write_text(
        json.dumps({
            "schemaVersion": 1,
            "dropSqlSha256": evil_hash,
            "containers": [{
                "service": "fstservice",
                "container_id": "8" * 64,
                "image_id": "sha256:" + "9" * 64,
                "compose_image_id": "sha256:" + "9" * 64,
            }],
            "productionComposeOwnership": {
                "fstserviceImageReferenceSha256": "7" * 64,
            },
        }),
        encoding="utf-8",
    )
    drop_path.write_bytes(evil_sql)
    process.stdin.write(b"RELEASE\n")
    process.stdin.flush()
    process.stdin.close()
    status = process.wait(timeout=20)
    if status:
        raise SystemExit(process.stderr.read().decode())
assert output_path.read_bytes() == safe_sql
proof = json.loads(proof_path.read_text(encoding="utf-8"))
assert proof["manifest"]["sha256"] == expected_manifest
assert proof["dropSql"]["sha256"] == safe_drop_hash
assert proof["dropSql"]["manifestSha256"] == safe_drop_hash
PY
! grep -q 'APPENDED_EVIL_SQL' "$WORK_ROOT/manifest-drop-streamed.sql"
printf 'PASS: simultaneous manifest/drop replacement cannot alter sealed SQL\n'

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
inherited_partition_column = next(
    row
    for row in columns
    if row["name"] == "leaderboard_current_entries_bass"
    and row["column_name"] == "fixture_column"
)
assert inherited_partition_column["statistics_target"] == ""
with (
    root / "package" / "catalog" / "signature.csv"
).open(newline="", encoding="utf-8") as handle:
    signature_rows = list(csv.DictReader(handle))
inherited_signature = next(
    row
    for row in signature_rows
    if row["category"] == "column"
    and row["object_identity"]
    == "public.leaderboard_current_entries_bass#1"
)
assert json.loads(inherited_signature["detail"])["statisticsTarget"] is None
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
assert manifest["capacityGuardPolicy"]["effectiveParameters"] == {
    "estimatedFullScrapeGrowthBytes": 60392999803,
    "expectedFullScrapesPerDay": 2,
    "minimumHeadroomDays": 7,
    "minimumHeadroomBytesOverride": 0,
    "transientBuildBytes": 0,
    "requiredScratchBytes": 0,
    "expectedReclaimBytes": 0,
}
assert len(manifest["capacityGuardPolicy"]["guardScriptSha256"]) == 64
assert len(manifest["capacityGuardPolicySha256"]) == 64
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
logical_executable = (
    root / "package" / "rollback-executable" / "logical-shadow.sql"
).read_text(encoding="utf-8")
logical_data = (
    root / "package" / "rollback-data" / "logical-shadow.data.sql"
).read_text(encoding="utf-8")
band_data = (
    root / "package" / "rollback-data" / "band-song-projection.data.sql"
).read_text(encoding="utf-8")
assert logical_rollback.count("\\restrict ") == 1
assert logical_rollback.count("\\unrestrict ") == 1
raw_restrict = next(
    line for line in logical_rollback.splitlines()
    if line.startswith("\\restrict ")
)
executable_restrict = next(
    line for line in logical_executable.splitlines()
    if line.startswith("\\restrict ")
)
assert raw_restrict == executable_restrict
assert "SET statement_timeout = 0;" in logical_rollback
assert "SET lock_timeout = 0;" in logical_rollback
assert "SET statement_timeout = '30s';" in logical_executable
assert "SET lock_timeout = '5s';" in logical_executable
assert "SET transaction_timeout = '5min';" in logical_executable
assert "SET statement_timeout = 0;" not in logical_executable
assert "-- Retained payload:" not in logical_rollback
assert "-- Retained payload:" not in band_rollback
assert "-- Retained payload: public.leaderboard_logical_write_metrics" in \
    logical_data
assert "-- Retained payload: public.band_song_team_ranking_state" in \
    band_data
assert logical_data.count("\n1255,") == 9
assert logical_data.count("\n1266,") == 9
assert "\nBand_Duets," in band_data
assert "\nBand_Trios," in band_data
assert "\nBand_Quad," in band_data
rollback_hashes = {
    row["name"]: row
    for row in manifest["rollbackHashes"]
    if row["kind"] == "generated-family"
}
logical_canonical = (
    root / "package" / "rollback-canonical" / "logical-shadow.sql"
).read_bytes()
band_canonical = (
    root / "package" / "rollback-canonical" /
    "band-song-projection.sql"
).read_bytes()
assert rollback_hashes["logical-shadow.sql"]["sha256"] == hashlib.sha256(
    logical_canonical
).hexdigest()
assert rollback_hashes["band-song-projection.sql"]["sha256"] == \
    hashlib.sha256(band_canonical).hexdigest()
data_hashes = {
    row["name"]: row
    for row in manifest["rollbackHashes"]
    if row["kind"] == "generated-data"
}
assert data_hashes["logical-shadow.data.sql"]["sha256"] == hashlib.sha256(
    logical_data.encode()
).hexdigest()

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

publication_all_fixture="$(copy_fixture publication-all-tables)"
printf '%s\n' \
    'publication,fixture_all_tables,public.ranking_deltas,membership_mode=all-tables' \
    >> "$publication_all_fixture/pre/external-dependencies.csv"
run_status 3 publication-all-tables-gate \
    run_cleanup \
        "$publication_all_fixture" \
        "$WORK_ROOT/out-publication-all-tables"

publication_schema_fixture="$(copy_fixture publication-schema)"
printf '%s\n' \
    'publication,fixture_schema,public.ranking_deltas,membership_mode=schema' \
    >> "$publication_schema_fixture/pre/external-dependencies.csv"
run_status 3 publication-schema-gate \
    run_cleanup \
        "$publication_schema_fixture" \
        "$WORK_ROOT/out-publication-schema"

publication_explicit_fixture="$(copy_fixture publication-explicit)"
printf '%s\n' \
    'publication,fixture_explicit,public.ranking_deltas,membership_mode=explicit' \
    >> "$publication_explicit_fixture/pre/external-dependencies.csv"
run_status 3 publication-explicit-gate \
    run_cleanup \
        "$publication_explicit_fixture" \
        "$WORK_ROOT/out-publication-explicit"

unexpected_owned_sequence_fixture="$(
    copy_fixture unexpected-owned-sequence
)"
printf '%s\n' \
    'sequence-owner,public,custom_owned_sequence_2026,public,player_score_observations,id,a' \
    >> "$unexpected_owned_sequence_fixture/pre/owned-objects.csv"
run_status 3 unexpected-owned-sequence-gate \
    run_cleanup \
        "$unexpected_owned_sequence_fixture" \
        "$WORK_ROOT/out-unexpected-owned-sequence"

active_owner_fixture="$(copy_fixture active-column-sequence-owner)"
python3 - \
    "$active_owner_fixture/pre/relations.csv" \
    "$active_owner_fixture/pre/owned-objects.csv" \
    "$active_owner_fixture/pre/catalog-signature.raw.csv" <<'PY'
import csv
import json
import sys
from pathlib import Path

relations_path, owned_path, catalog_path = map(Path, sys.argv[1:])
with relations_path.open(newline="", encoding="utf-8") as handle:
    reader = csv.DictReader(handle)
    relation_fields = reader.fieldnames
    relations = list(reader)
for row in relations:
    if row["name"] == "player_score_observations_id_seq":
        row["sequence_owned_by"] = "public.active_score_entries.id"
with relations_path.open("w", newline="", encoding="utf-8") as handle:
    writer = csv.DictWriter(handle, fieldnames=relation_fields)
    writer.writeheader()
    writer.writerows(relations)

with owned_path.open(newline="", encoding="utf-8") as handle:
    reader = csv.DictReader(handle)
    owned_fields = reader.fieldnames
    owned = [
        row
        for row in reader
        if row["kind"] != "sequence-owner"
    ]
with owned_path.open("w", newline="", encoding="utf-8") as handle:
    writer = csv.DictWriter(handle, fieldnames=owned_fields)
    writer.writeheader()
    writer.writerows(owned)

with catalog_path.open(newline="", encoding="utf-8") as handle:
    reader = csv.DictReader(handle)
    catalog_fields = reader.fieldnames
    catalog = list(reader)
for row in catalog:
    if (
        row["category"] == "sequence"
        and row["object_identity"]
        == "public.player_score_observations_id_seq"
    ):
        detail = json.loads(row["detail"])
        detail["ownedBy"] = "public.active_score_entries.id"
        row["detail"] = json.dumps(detail, sort_keys=True)
with catalog_path.open("w", newline="", encoding="utf-8") as handle:
    writer = csv.DictWriter(handle, fieldnames=catalog_fields)
    writer.writeheader()
    writer.writerows(catalog)
PY
run_status 3 active-column-sequence-reassignment-gate \
    run_cleanup \
        "$active_owner_fixture" \
        "$WORK_ROOT/out-active-column-sequence-owner"

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

unsafe_dump_fixture="$(copy_fixture unsafe-pg-dump)"
python3 - "$unsafe_dump_fixture/rollback/logical-shadow.sql" <<'PY'
import sys
from pathlib import Path

path = Path(sys.argv[1])
text = path.read_text(encoding="utf-8")
path.write_text(
    text.replace("\\unrestrict ", "\\! echo unsafe\n\\unrestrict ", 1),
    encoding="utf-8",
)
PY
run_status 3 unsafe-pg-dump-package-gate \
    run_cleanup "$unsafe_dump_fixture" "$WORK_ROOT/out-unsafe-pg-dump"

capacity_policy_fixture="$(copy_fixture capacity-policy)"
python3 - "$capacity_policy_fixture/pre/capacity-guard.policy.json" <<'PY'
import json
import sys
from pathlib import Path

path = Path(sys.argv[1])
value = json.loads(path.read_text(encoding="utf-8"))
value["effectiveParameters"]["expectedReclaimBytes"] = 999999999999999
path.write_text(json.dumps(value, sort_keys=True), encoding="utf-8")
PY
run_status 3 capacity-policy-manifest-gate \
    run_cleanup "$capacity_policy_fixture" "$WORK_ROOT/out-capacity-policy"

capacity_report_fixture="$(copy_fixture capacity-report)"
python3 - "$capacity_report_fixture/pre/capacity-guard.json" <<'PY'
import json
import sys
from pathlib import Path

path = Path(sys.argv[1])
value = json.loads(path.read_text(encoding="utf-8"))
value["capacity"]["expectedReclaimBytes"] = 999999999999999
path.write_text(json.dumps(value, sort_keys=True), encoding="utf-8")
PY
run_status 3 capacity-report-policy-gate \
    run_cleanup "$capacity_report_fixture" "$WORK_ROOT/out-capacity-report"

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

stale_connection_fixture="$(copy_fixture stale-container-connection)"
python3 - \
    "$stale_connection_fixture/pre/container-config-attestation.json" <<'PY'
import json
import sys
from pathlib import Path

path = Path(sys.argv[1])
value = json.loads(path.read_text(encoding="utf-8"))
value["databaseHostMapping"]["host"] = "stale-postgres"
path.write_text(json.dumps(value, sort_keys=True), encoding="utf-8")
PY
run_status 3 stale-container-connection-gate \
    run_cleanup \
        "$stale_connection_fixture" \
        "$WORK_ROOT/out-stale-container-connection"

stale_network_fixture="$(copy_fixture stale-container-network)"
python3 - \
    "$stale_network_fixture/pre/container-config-attestation.json" <<'PY'
import json
import sys
from pathlib import Path

path = Path(sys.argv[1])
value = json.loads(path.read_text(encoding="utf-8"))
value["databaseHostMapping"]["sharedNetworks"] = []
path.write_text(json.dumps(value, sort_keys=True), encoding="utf-8")
PY
run_status 3 stale-container-network-gate \
    run_cleanup \
        "$stale_network_fixture" \
        "$WORK_ROOT/out-stale-container-network"

stale_alias_fixture="$(copy_fixture stale-container-alias)"
python3 - \
    "$stale_alias_fixture/pre/container-config-attestation.json" <<'PY'
import json
import sys
from pathlib import Path

path = Path(sys.argv[1])
value = json.loads(path.read_text(encoding="utf-8"))
owners = value["databaseHostMapping"]["aliasOwners"][
    "festival-service-tracker_default"
]
owners.append({
    "containerId": "8" * 64,
    "containerName": "stale-postgres-clone",
    "composeProject": "stale-project",
    "composeService": "postgres",
    "ipAddress": "172.31.0.99",
})
path.write_text(json.dumps(value, sort_keys=True), encoding="utf-8")
PY
run_status 3 stale-container-duplicate-db-alias-gate \
    run_cleanup \
        "$stale_alias_fixture" \
        "$WORK_ROOT/out-stale-container-alias"

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

invalid_statistics_target_fixture="$(
    copy_fixture invalid-statistics-target
)"
mutate_csv \
    "$invalid_statistics_target_fixture/pre/column-catalog.raw.csv" \
    statistics_target \
    not-an-integer
run_status 2 invalid-column-statistics-target-gate \
    run_cleanup \
        "$invalid_statistics_target_fixture" \
        "$WORK_ROOT/out-invalid-statistics-target"

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

scratch_drift_fixture="$(copy_fixture scratch-drift)"
cp -R \
    "$scratch_drift_fixture/pre" \
    "$scratch_drift_fixture/post-scratch-gate"
mutate_csv \
    "$scratch_drift_fixture/post-scratch-gate/preflight.csv" \
    active_scrape_count \
    1
run_status 3 drift-during-scratch-second-gate \
    run_cleanup \
        "$scratch_drift_fixture" \
        "$WORK_ROOT/out-scratch-drift" \
        --execute \
        --expected-manifest-sha256 "$sha_one"

for gate_stage in setup capture catalog retained validation artifact; do
    gate_failure_fixture="$(copy_fixture "complete-gate-$gate_stage")"
    : > "$gate_failure_fixture/fail-complete-gate-$gate_stage"
    gate_failure_output="$WORK_ROOT/out-complete-gate-$gate_stage"
    run_status 3 "complete-gate-$gate_stage-failure" \
        run_cleanup \
            "$gate_failure_fixture" \
            "$gate_failure_output" \
            --execute \
            --expected-manifest-sha256 "$sha_one"
    test ! -f "$gate_failure_output/post/drop-process-control.csv"
    if [[ -f "$gate_failure_output/logs/drop.log" ]]; then
        ! grep -q 'FST_FAMILY_DROP_BEGIN' \
            "$gate_failure_output/logs/drop.log"
    fi
done
printf 'PASS: every complete-gate stage blocks SQL release on failure\n'

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
python3 - "$launch_signal_output/post/drop-process-control.csv" <<'PY'
import csv
import re
import sys

with open(sys.argv[1], newline="", encoding="utf-8") as handle:
    rows = list(csv.DictReader(handle))
assert len(rows) == 1
row = rows[0]
assert row["connect_released"] == "false"
assert row["sql_released"] == "false"
assert re.fullmatch(r"[0-9a-f]{64}", row["second_gate_sha256"])
assert row["container_psql_pid"] == ""
assert row["backend_pid"] == ""
assert row["state"] == "terminated"
PY
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
python3 - "$post_connect_output/post/drop-process-control.csv" <<'PY'
import csv
import re
import sys

with open(sys.argv[1], newline="", encoding="utf-8") as handle:
    rows = list(csv.DictReader(handle))
assert len(rows) == 1
row = rows[0]
assert row["connect_released"] == "true"
assert row["sql_released"] == "false"
assert re.fullmatch(r"[0-9a-f]{64}", row["second_gate_sha256"])
assert row["container_psql_pid"] == "4242"
assert row["backend_pid"] == "5252"
assert row["state"] == "terminated"
PY
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

startup_retag_fixture="$(copy_fixture startup-retag)"
mutate_csv \
    "$startup_retag_fixture/post/startup-image-attestation.csv" \
    prestart_actual_image_id \
    "sha256:$(printf '9%.0s' {1..64})"
mutate_csv \
    "$startup_retag_fixture/post/startup-image-attestation.csv" \
    poststart_config_image \
    "sha256:$(printf '9%.0s' {1..64})"
run_status 3 startup-image-retag-post-gate \
    run_cleanup "$startup_retag_fixture" "$WORK_ROOT/out-startup-retag" \
        --execute \
        --expected-manifest-sha256 "$sha_one"

startup_prestart_retag_fixture="$(copy_fixture startup-prestart-retag)"
mutate_csv \
    "$startup_prestart_retag_fixture/post/startup-image-attestation.csv" \
    compose_image_id_before_start \
    "sha256:$(printf '9%.0s' {1..64})"
run_status 3 startup-image-retag-before-start-gate \
    run_cleanup \
        "$startup_prestart_retag_fixture" \
        "$WORK_ROOT/out-startup-prestart-retag" \
        --execute \
        --expected-manifest-sha256 "$sha_one"

startup_alias_drift_fixture="$(copy_fixture startup-alias-drift)"
: > "$startup_alias_drift_fixture/startup-alias-drift-before-start"
run_status 3 startup-alias-drift-before-start-recovery \
    run_cleanup \
        "$startup_alias_drift_fixture" \
        "$WORK_ROOT/out-startup-alias-drift" \
        --execute \
        --expected-manifest-sha256 "$sha_one"
[[ ! -e \
    "$WORK_ROOT/out-startup-alias-drift/post/startup-initializer-release.json" ]]
grep -q '^alias-or-target-drift,3,' \
    "$WORK_ROOT/out-startup-alias-drift/post/startup-database-routing-failure.csv"
grep -q ',committed,' \
    "$WORK_ROOT/out-startup-alias-drift/post/drop-process-status.csv"
grep -q '^status=failed$' \
    "$WORK_ROOT/out-startup-alias-drift/FAILED.txt"
grep -q 'post-drop initializer database routing attestation' \
    "$WORK_ROOT/out-startup-alias-drift/FAILED.txt"
test -s "$WORK_ROOT/out-startup-alias-drift/ROLLBACK-INSTRUCTIONS.txt"
test -s "$WORK_ROOT/out-startup-alias-drift/post/trap-reconciliation.csv"
printf 'PASS: startup alias drift records reconciliation and recovery evidence\n'

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

python3 - "$WORK_ROOT" <<'PY'
import csv
import hashlib
import json
import sys
from pathlib import Path

root = Path(sys.argv[1])
connection = (
    "Host=postgres;Port=5432;Database=fstservice;"
    "Username=fst;Password=fixture-password"
)
services = {
    "postgres": {
        "image": "postgres:17",
        "command": ["postgres"],
        "environment": {
            "POSTGRES_DB": "fstservice",
            "POSTGRES_USER": "fst",
            "POSTGRES_PASSWORD": "fixture-password",
        },
        "networks": {"default": {"aliases": ["database"]}},
    },
    "fstservice": {
        "image": "example/fstservice:fixture",
        "command": ["dotnet", "FSTService.dll"],
        "environment": {
            "ConnectionStrings__PostgreSQL": connection,
            "Feature__Fixture": "false",
        },
        "labels": {"fixture.label": "service"},
        "networks": {"default": {"aliases": ["api"]}},
        "ports": [{
            "target": 8080,
            "published": "8081",
            "protocol": "tcp",
            "host_ip": "127.0.0.1",
        }],
    },
    "fstworker": {
        "image": "example/fstservice:fixture",
        "command": ["dotnet", "FSTService.dll", "--worker"],
        "environment": {
            "ConnectionStrings__PostgreSQL": connection,
            "Feature__Fixture": "false",
        },
        "networks": {"default": {}},
    },
    "festivalweb": {
        "image": "example/festivalweb:fixture",
        "command": ["nginx"],
        "environment": {"WEB_FIXTURE": "true"},
        "networks": {"default": {}},
        "ports": [{
            "target": 80,
            "published": "3001",
            "protocol": "tcp",
            "host_ip": "127.0.0.1",
        }],
    },
}
compose = {
    "name": "fixture-project",
    "services": services,
    "networks": {
        "default": {
            "name": "fixture-project_default",
            "external": False,
        },
    },
}
(root / "container-compose-input.json").write_text(
    json.dumps(compose),
    encoding="utf-8",
)
config_files = "base.yml,override-pia.yml"
config_hash = hashlib.sha256(config_files.encode()).hexdigest()
ids = {
    "postgres": "1" * 64,
    "fstservice": "2" * 64,
    "fstworker": "3" * 64,
    "festivalweb": "4" * 64,
}
with (root / "container-project.csv").open(
    "w",
    newline="",
    encoding="utf-8",
) as handle:
    fields = [
        "container_id",
        "container_name",
        "service",
        "project",
        "working_dir",
        "config_files_sha256",
    ]
    writer = csv.DictWriter(handle, fieldnames=fields)
    writer.writeheader()
    for service, container_id in ids.items():
        writer.writerow({
            "container_id": container_id,
            "container_name": service,
            "service": service,
            "project": "fixture-project",
            "working_dir": "/home/sfenton/Docker/FestivalServiceTracker",
            "config_files_sha256": config_hash,
        })
target_fields = [
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
with (root / "container-target.csv").open(
    "w",
    newline="",
    encoding="utf-8",
) as handle:
    writer = csv.DictWriter(handle, fieldnames=target_fields)
    writer.writeheader()
    writer.writerow({
        "compose_project": "fixture-project",
        "service": "postgres",
        "configured_host": "postgres",
        "configured_port": "5432",
        "configured_database": "fstservice",
        "configured_user": "fst",
        "container_id": ids["postgres"],
        "runtime_address": "local-socket",
        "runtime_port": "5432",
        "runtime_database": "fstservice",
        "runtime_user": "fst",
        "in_recovery": "false",
        "system_identifier": "7429301450012345678",
        "role_superuser": "true",
        "role_bypass_rls": "false",
    })
inspect = []
for index, (service, definition) in enumerate(services.items(), 2):
    aliases = [service] + definition["networks"]["default"].get(
        "aliases",
        [],
    )
    labels = {
        "com.docker.compose.service": service,
        "com.docker.compose.project": "fixture-project",
        "com.docker.compose.project.working_dir": (
            "/home/sfenton/Docker/FestivalServiceTracker"
        ),
        "com.docker.compose.project.config_files": config_files,
    }
    labels.update(definition.get("labels", {}))
    port_bindings = {}
    for port in definition.get("ports", []):
        key = f"{port['target']}/{port['protocol']}"
        port_bindings[key] = [{
            "HostIp": port["host_ip"],
            "HostPort": port["published"],
        }]
    inspect.append({
        "Id": ids[service],
        "Image": f"sha256:{service}",
        "Config": {
            "Labels": labels,
            "Env": [
                f"{name}={value}"
                for name, value in definition.get("environment", {}).items()
            ],
            "Cmd": definition["command"],
            "Entrypoint": [],
        },
        "HostConfig": {
            "RestartPolicy": {
                "Name": "no" if service == "fstworker" else "unless-stopped"
            },
            "PortBindings": port_bindings,
        },
        "Mounts": [],
        "State": {
            "Status": "exited" if service == "fstworker" else "running",
        },
        "NetworkSettings": {
            "Ports": port_bindings,
            "Networks": {
                "fixture-project_default": {
                    "Aliases": aliases,
                    "IPAddress": f"172.30.0.{index}",
                },
            },
        },
    })
(root / "container-inspect.json").write_text(
    json.dumps(inspect),
    encoding="utf-8",
)
PY
python3 "$HELPER" sanitize-compose-config \
    --output "$WORK_ROOT/container-compose-sanitized.json" \
    --binds-output "$WORK_ROOT/container-compose-binds.tsv" \
    < "$WORK_ROOT/container-compose-input.json"
run_status 0 actual-container-compose-attestation \
    bash -c '
        python3 "$1" attest-container-config \
            --compose "$2" \
            --target-attestation "$3" \
            --project-containers "$4" \
            --fingerprint-spec "$5" \
            --output "$6" < "$7"
    ' _ \
        "$HELPER" \
        "$WORK_ROOT/container-compose-sanitized.json" \
        "$WORK_ROOT/container-target.csv" \
        "$WORK_ROOT/container-project.csv" \
        "$SCRIPT_DIR/sql/postgres-retired-schema-cleanup/public-fingerprints.tsv" \
        "$WORK_ROOT/container-attestation.json" \
        "$WORK_ROOT/container-inspect.json"

python3 - "$WORK_ROOT/container-inspect.json" <<'PY'
import json
import sys
from pathlib import Path

path = Path(sys.argv[1])
value = json.loads(path.read_text(encoding="utf-8"))
for item in value:
    if item["Config"]["Labels"]["com.docker.compose.service"] == "fstservice":
        item["Config"]["Env"] = [
            env.replace("Host=postgres", "Host=stale-postgres")
            for env in item["Config"]["Env"]
        ]
path.write_text(json.dumps(value), encoding="utf-8")
PY
run_status 3 actual-container-stale-db-target \
    bash -c '
        python3 "$1" attest-container-config \
            --compose "$2" \
            --target-attestation "$3" \
            --project-containers "$4" \
            --fingerprint-spec "$5" \
            --output "$6" < "$7"
    ' _ \
        "$HELPER" \
        "$WORK_ROOT/container-compose-sanitized.json" \
        "$WORK_ROOT/container-target.csv" \
        "$WORK_ROOT/container-project.csv" \
        "$SCRIPT_DIR/sql/postgres-retired-schema-cleanup/public-fingerprints.tsv" \
        "$WORK_ROOT/container-attestation-stale-db.json" \
        "$WORK_ROOT/container-inspect.json"

python3 - "$WORK_ROOT/container-inspect.json" <<'PY'
import json
import sys
from pathlib import Path

path = Path(sys.argv[1])
value = json.loads(path.read_text(encoding="utf-8"))
for item in value:
    service = item["Config"]["Labels"]["com.docker.compose.service"]
    if service == "fstservice":
        item["Config"]["Env"] = [
            env.replace("Host=stale-postgres", "Host=postgres")
            for env in item["Config"]["Env"]
        ]
    if service == "postgres":
        item["NetworkSettings"]["Networks"][
            "fixture-project_default"
        ]["Aliases"] = ["database"]
path.write_text(json.dumps(value), encoding="utf-8")
PY
run_status 3 actual-container-stale-network-alias \
    bash -c '
        python3 "$1" attest-container-config \
            --compose "$2" \
            --target-attestation "$3" \
            --project-containers "$4" \
            --fingerprint-spec "$5" \
            --output "$6" < "$7"
    ' _ \
        "$HELPER" \
        "$WORK_ROOT/container-compose-sanitized.json" \
        "$WORK_ROOT/container-target.csv" \
        "$WORK_ROOT/container-project.csv" \
        "$SCRIPT_DIR/sql/postgres-retired-schema-cleanup/public-fingerprints.tsv" \
        "$WORK_ROOT/container-attestation-stale-network.json" \
        "$WORK_ROOT/container-inspect.json"

python3 - "$WORK_ROOT/container-inspect.json" <<'PY'
import json
import sys
from pathlib import Path

path = Path(sys.argv[1])
value = json.loads(path.read_text(encoding="utf-8"))
for item in value:
    service = item["Config"]["Labels"]["com.docker.compose.service"]
    if service == "postgres":
        item["NetworkSettings"]["Networks"][
            "fixture-project_default"
        ]["Aliases"] = ["postgres", "database"]
    if service == "festivalweb":
        for section in [
            item["HostConfig"]["PortBindings"],
            item["NetworkSettings"]["Ports"],
        ]:
            section["80/tcp"][0]["HostPort"] = "3002"
path.write_text(json.dumps(value), encoding="utf-8")
PY
run_status 3 actual-container-wrong-fingerprint-port \
    bash -c '
        python3 "$1" attest-container-config \
            --compose "$2" \
            --target-attestation "$3" \
            --project-containers "$4" \
            --fingerprint-spec "$5" \
            --output "$6" < "$7"
    ' _ \
        "$HELPER" \
        "$WORK_ROOT/container-compose-sanitized.json" \
        "$WORK_ROOT/container-target.csv" \
        "$WORK_ROOT/container-project.csv" \
        "$SCRIPT_DIR/sql/postgres-retired-schema-cleanup/public-fingerprints.tsv" \
        "$WORK_ROOT/container-attestation-wrong-port.json" \
        "$WORK_ROOT/container-inspect.json"

python3 - "$WORK_ROOT/container-inspect.json" <<'PY'
import json
import sys
from pathlib import Path

path = Path(sys.argv[1])
value = json.loads(path.read_text(encoding="utf-8"))
for item in value:
    service = item["Config"]["Labels"]["com.docker.compose.service"]
    if service == "festivalweb":
        for section in [
            item["HostConfig"]["PortBindings"],
            item["NetworkSettings"]["Ports"],
        ]:
            section["80/tcp"][0]["HostPort"] = "3001"
value.append({
    "Id": "9" * 64,
    "Image": "sha256:stale",
    "Config": {
        "Labels": {},
        "Env": [],
        "Cmd": ["stale-responder"],
        "Entrypoint": [],
    },
    "HostConfig": {
        "RestartPolicy": {"Name": "no"},
        "PortBindings": {
            "80/tcp": [{
                "HostIp": "127.0.0.1",
                "HostPort": "3001",
            }],
        },
    },
    "Mounts": [],
    "State": {"Status": "exited"},
    "NetworkSettings": {"Ports": {}, "Networks": {}},
})
path.write_text(json.dumps(value), encoding="utf-8")
PY
run_status 3 stale-port-responder-ownership-gate \
    bash -c '
        python3 "$1" attest-container-config \
            --compose "$2" \
            --target-attestation "$3" \
            --project-containers "$4" \
            --fingerprint-spec "$5" \
            --output "$6" < "$7"
    ' _ \
        "$HELPER" \
        "$WORK_ROOT/container-compose-sanitized.json" \
        "$WORK_ROOT/container-target.csv" \
        "$WORK_ROOT/container-project.csv" \
        "$SCRIPT_DIR/sql/postgres-retired-schema-cleanup/public-fingerprints.tsv" \
        "$WORK_ROOT/container-attestation-stale-port.json" \
        "$WORK_ROOT/container-inspect.json"

python3 - "$WORK_ROOT/container-inspect.json" <<'PY'
import json
import sys
from pathlib import Path

path = Path(sys.argv[1])
value = json.loads(path.read_text(encoding="utf-8"))
value = [item for item in value if item.get("Id") != "9" * 64]
value.append({
    "Id": "8" * 64,
    "Name": "/stale-postgres-clone",
    "Image": "sha256:stale-postgres",
    "Config": {
        "Labels": {
            "com.docker.compose.project": "stale-project",
            "com.docker.compose.service": "postgres",
        },
        "Env": [],
        "Cmd": ["postgres"],
        "Entrypoint": [],
    },
    "HostConfig": {
        "RestartPolicy": {"Name": "unless-stopped"},
        "PortBindings": {},
    },
    "Mounts": [],
    "State": {"Status": "running"},
    "NetworkSettings": {
        "Ports": {},
        "Networks": {
            "fixture-project_default": {
                "Aliases": ["postgres", "stale-postgres-clone"],
                "IPAddress": "172.30.0.99",
            },
        },
    },
})
path.write_text(json.dumps(value), encoding="utf-8")
PY
run_status 3 stale-clone-database-alias-gate \
    bash -c '
        python3 "$1" attest-container-config \
            --compose "$2" \
            --target-attestation "$3" \
            --project-containers "$4" \
            --fingerprint-spec "$5" \
            --output "$6" < "$7"
    ' _ \
        "$HELPER" \
        "$WORK_ROOT/container-compose-sanitized.json" \
        "$WORK_ROOT/container-target.csv" \
        "$WORK_ROOT/container-project.csv" \
        "$SCRIPT_DIR/sql/postgres-retired-schema-cleanup/public-fingerprints.tsv" \
        "$WORK_ROOT/container-attestation-stale-alias.json" \
        "$WORK_ROOT/container-inspect.json"
printf 'PASS: actual container config and DB host/network attestation\n'

cat > "$WORK_ROOT/fake-psql" <<'SH'
#!/usr/bin/env bash
set -euo pipefail
for variable in PGHOST PGHOSTADDR PGPORT PGSERVICE PGSERVICEFILE; do
    [[ ! -v "$variable" ]] || {
        printf 'hostile libpq override survived: %s\n' "$variable" >&2
        exit 1
    }
done
[[ "$*" == *"-h /var/run/postgresql"* ]]
[[ "$*" == *"-p 5432"* ]]
SH
chmod +x "$WORK_ROOT/fake-psql"
env \
    PGHOST=remote.example \
    PGHOSTADDR=10.0.0.55 \
    PGPORT=6543 \
    PGSERVICE=hostile \
    PGSERVICEFILE=/missing/service.conf \
    env -u PGHOST -u PGHOSTADDR -u PGPORT \
        -u PGSERVICE -u PGSERVICEFILE \
        "$WORK_ROOT/fake-psql" \
        -h /var/run/postgresql -p 5432
printf 'PASS: hostile container libpq overrides are truly unset\n'

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
    "$WORK_ROOT/drop.sql" \
    "$WORK_ROOT/drop-ownership-masking.sql" <<'PY'
import sys
from pathlib import Path

source = Path(sys.argv[1]).read_text(encoding="utf-8")
marker = 'LOCK TABLE ONLY "public"."leaderboard_current_entries"'
mutation = (
    'ALTER SEQUENCE "public"."player_score_observations_id_seq"\n'
    '    OWNED BY "public"."player_score_observations"."id";\n'
)
Path(sys.argv[2]).write_text(
    source.replace(marker, mutation + marker, 1),
    encoding="utf-8",
)
PY
run_status 3 ownership-masking-regression \
    python3 "$HELPER" validate-drop-sql \
        --objects "$OBJECTS" \
        --sql "$WORK_ROOT/drop-ownership-masking.sql"

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
assert sql.rindex("LOCK TABLE ") < sql.index("DO $owned_sequence_set$")
assert sql.index("DO $owned_sequence_set$") < \
       sql.index("DO $effective_publication_membership$") < \
       sql.index("DO $catalog_signature$")
assert sql.index("DO $catalog_signature$") < sql.index("DO $objects$")
assert sql.count("Owned sequence set differs from allowlist") == 1
assert sql.count("DO $effective_publication_membership$") == 2
assert sql.count(
    "Effective publication membership exists for a cleanup target"
) == 2
assert sql.count("EXCEPT ALL") >= 2
assert "dependency.refobjid = owner_table.oid" in sql
assert "dependency.deptype IN ('a', 'i')" in sql
assert sql.count("Complete cleanup catalog signature drifted") == 2
assert sql.count("Row security is enabled:") == 61
assert "SET LOCAL row_security = off;" in sql
assert sql.rindex("DO $catalog_signature$") < \
       sql.rindex("DO $effective_publication_membership$") < first_drop
assert "5067481511116519501" in sql
assert "5067481511116519502" in sql
assert (
    'ALTER SEQUENCE "public"."player_score_observations_id_seq"\n'
    '    OWNED BY "public"."player_score_observations"."id";'
) not in sql
assert sql.rindex("LOCK TABLE ") < \
       sql.index("DO $sequence_dependency_lock$") < \
       sql.index("DO $owned_sequence_set$") < \
       sql.index("DO $sequence_guard$") < \
       sql.index("DO $sequence_state_lock$") < \
       sql.index("DO $effective_publication_membership$") < \
       sql.index("DO $catalog_signature$")
assert "FOR SHARE OF dependency" in sql
assert "FOR SHARE OF relation" in sql
assert "FOR SHARE OF sequence_catalog" in sql
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
    if "CREATE TEMP TABLE" in text:
        assert text.index("CREATE TEMP TABLE") < text.index(
            "BEGIN TRANSACTION READ ONLY;"
        ), path
        assert "ON COMMIT PRESERVE ROWS" in text, path
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
    "attstattarget",
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
assert "'typeOid', attribute.atttypid::bigint" in catalog_query
assert "'typeOid', attribute.atttypid," not in catalog_query
for intentional_textual_oid in [
    "dependency.classid::regclass::text",
    "dependency.refclassid::regclass::text",
    "shared_dependency.classid::regclass::text",
    "shared_dependency.refclassid::regclass::text",
    "pg_catalog.format_type(sequence.seqtypid, NULL)",
]:
    assert intentional_textual_oid in catalog_query
column_capture = (sql_dir / "capture-column-catalog.sql").read_text(
    encoding="utf-8"
)
assert "attribute.attstattarget::text AS statistics_target" in column_capture
assert "COALESCE(attribute.attstattarget" not in column_capture
for required in [
    "pg_catalog.pg_publication_tables",
    "pg_catalog.pg_publication_namespace",
    "publication_table.attnames",
    "publication_table.rowfilter",
    "'membershipMode'",
    "'all-tables'",
    "'schema'",
    "'explicit'",
]:
    assert required in catalog_query, required

external_dependencies = (
    sql_dir / "capture-external-dependencies.sql"
).read_text(encoding="utf-8")
for required in [
    "pg_catalog.pg_publication_tables",
    "pg_catalog.pg_publication_namespace",
    "publication_table.attnames",
    "publication_table.rowfilter",
    "'membershipMode'",
    "'all-tables'",
    "'schema'",
    "'explicit'",
]:
    assert required in external_dependencies, required

owned_capture = (sql_dir / "capture-owned-objects.sql").read_text(
    encoding="utf-8"
)
assert "FROM target owner_target" in owned_capture
assert "dependency.refobjid = owner_target.oid" in owned_capture
assert "dependency.refobjsubid > 0" in owned_capture
assert "dependency.deptype IN ('a', 'i')" in owned_capture
assert "sequence_row.relkind = 'S'" in owned_capture
assert "FROM target sequence_target" not in owned_capture

script = Path(sys.argv[2]).read_text(encoding="utf-8")
assert "--lock-wait-timeout=5s" in script
assert "timeout --signal=TERM --kill-after=10s 2m" in script
assert 'PGOPTIONS="-c statement_timeout=30s -c row_security=off"' in script
assert "timeout --signal=TERM --kill-after=30s 7m" in script
assert "-e PGCONNECT_TIMEOUT=10" in script
assert "atomic drop process reconciliation" in script
assert "FST_ALL_COMMITTED_RECONCILED_BY_ABSENCE" in script
assert "com.docker.compose.project.config_files" in script
assert 'docker compose "${COMPOSE_FILE_ARGS[@]}" "$@"' in script
assert "compose_command config --format json" in script
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
assert "env -u PGHOST -u PGHOSTADDR -u PGPORT" in script
assert "-u PGSERVICE -u PGSERVICEFILE" in script
assert "-e PGSERVICE=" not in script
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
startup_check = script[
    script.index("run_startup_schema_check()"):
    script.index("run_rollback_rehearsal()")
]
assert "create-immutable-startup-container" in startup_check
assert "attest-immutable-startup-container" in startup_check
assert "--expected-image-id \"$APPROVED_FSTSERVICE_IMAGE_ID\"" in startup_check
assert "--source-container-id \"$APPROVED_FSTSERVICE_CONTAINER_ID\"" in \
    startup_check
assert 'docker start -a "$container_id"' in startup_check
assert "startup-image-attested-before-start.sha256" in startup_check
assert "capture_startup_database_routing_attestation" in startup_check
assert "startup-initializer-release.json" in startup_check
assert "{{.Id}}|{{.Image}}|{{.Config.Image}}|{{.State.Status}}" in \
    startup_check
assert startup_check.index("create-immutable-startup-container") < \
    startup_check.index("attest-immutable-startup-container")
assert startup_check.index("attest-immutable-startup-container") < \
    startup_check.index("capture_startup_database_routing_attestation")
assert startup_check.index("capture_startup_database_routing_attestation") < \
    startup_check.index("startup-initializer-release.json")
assert startup_check.index("startup-initializer-release.json") < \
    startup_check.index('docker start -a "$container_id"')
assert startup_check.index("startup-image-attested-before-start.sha256") < \
    startup_check.index('docker start -a "$container_id"')
assert "compose_image_before_start" in startup_check
assert "startup-image-override.yml" not in startup_check
assert "docker compose" not in startup_check
assert "run --rm" not in startup_check
assert "if ! capture_startup_database_routing_attestation" not in script
assert "startup-database-routing-failure.csv" in script
assert "attest-startup-database-routing" in script
assert "drop-runtime.sql" not in script
assert "immutable-drop-sql-ready" in script
capacity_capture = script[
    script.index("capture_capacity_guard()"):
    script.index("capture_fingerprints()")
]
for variable in [
    "TRANSIENT_BUILD_BYTES",
    "REQUIRED_SCRATCH_BYTES",
    "EXPECTED_RECLAIM_BYTES",
    "ESTIMATED_FULL_SCRAPE_GROWTH_BYTES",
    "EXPECTED_FULL_SCRAPES_PER_DAY",
    "MINIMUM_HEADROOM_DAYS",
    "MINIMUM_HEADROOM_BYTES_OVERRIDE",
]:
    assert f"-u {variable}" in capacity_capture
for option in [
    "--transient-build-bytes",
    "--required-scratch-bytes",
    "--expected-reclaim-bytes",
    "--estimated-full-scrape-growth-bytes",
    "--expected-full-scrapes-per-day",
    "--minimum-headroom-days",
    "--minimum-headroom-bytes",
]:
    assert option in capacity_capture
assert "guardScriptSha256" in capacity_capture
assert '< "$ROLLBACK_CANONICAL_DIR' not in script
assert "DIGEST-ONLY CANONICAL ROLLBACK PLAN; NEVER EXECUTE" in script
assert "FSTRetiredSchemaCleanup20260804" not in script
assert "canonicalize-pg-dump" in script
assert "prepare-executable-pg-dump" in script
assert 'cat "$ROLLBACK_EXECUTABLE_DIR/$family.sql"' in script
assert '< "$ROLLBACK_EXECUTABLE_DIR/rollback-all.sql"' in script
assert 'cat "$ROLLBACK_EXECUTABLE_DIR/rollback-all.sql"' in script
launch = script[
    script.index("run_live_atomic_drop()"):
    script.index("reconcile_drop_process()")
]
assert "stream-verified-manifest-drop-sql" in launch
assert "drop_sql_expected_sha" not in launch
assert "if ! capture_complete_post_scratch_gate" not in script
assert "capture_complete_post_scratch_gate\n" in launch
assert launch.index("post-connect-barrier-ready") < launch.index(
    "capture_complete_post_scratch_gate"
)
assert launch.index("capture_complete_post_scratch_gate") < launch.index(
    "immutable-drop-sql-ready"
)
assert launch.index("immutable-drop-sql-ready") < launch.index(
    "printf 'RELEASE"
)
assert "complete-live-gate-passed" in launch
assert "timeout --signal=TERM --kill-after=30s 25m" in launch
assert "SET statement_timeout = '15s'; SELECT last_value" in script
source_scan = script[
    script.index("capture_source_references()"):
    script.index("canonicalize_dump_for_digest()")
]
assert "if (( status > 1 )); then" in source_scan
assert "|| true" not in source_scan

capacity_guard = Path(sys.argv[3]).read_text(encoding="utf-8")
assert "-h /var/run/postgresql" in capacity_guard
assert '-p "$PG_PORT"' in capacity_guard
assert "env -u PGHOST -u PGHOSTADDR -u PGPORT" in capacity_guard
assert "-u PGSERVICE -u PGSERVICEFILE" in capacity_guard
assert "-e PGSERVICE=" not in capacity_guard
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

STARTUP_TEST_SOURCE="fst-retired-schema-startup-source-$$"
STARTUP_TEST_CLONE="fst-retired-schema-startup-clone-$$"
STARTUP_TEST_STALE="fst-retired-schema-startup-stale-$$"
STARTUP_TEST_NETWORK="fst-retired-schema-startup-network-$$"
STARTUP_TEST_ALIAS="fst-retired-schema-startup-retag-$$:current"
STARTUP_TEST_ALT_IMAGE="fst-retired-schema-startup-retag-$$:alternate"
docker network create "$STARTUP_TEST_NETWORK" >/dev/null
docker run -d --pull never \
    --name "$STARTUP_TEST_SOURCE" \
    --network "$STARTUP_TEST_NETWORK" \
    --network-alias postgres \
    postgres:17 sleep 300 >/dev/null
startup_source_id="$(
    docker inspect --format '{{.Id}}' "$STARTUP_TEST_SOURCE"
)"
startup_source_image="$(
    docker inspect --format '{{.Image}}' "$STARTUP_TEST_SOURCE"
)"
docker tag "$startup_source_image" "$STARTUP_TEST_ALIAS"
startup_alt_image="$(
    docker commit \
        --change 'LABEL fst.retired-schema.retag-fixture=alternate' \
        "$STARTUP_TEST_SOURCE" \
        "$STARTUP_TEST_ALT_IMAGE"
)"
[[ "$startup_alt_image" != "$startup_source_image" ]]
startup_image_reference_sha="$(
    printf '%s' "$STARTUP_TEST_ALIAS" | sha256sum | awk '{print $1}'
)"
startup_manifest_sha="$(printf '4%.0s' {1..64})"
startup_networks_json="$(
    python3 -c 'import json,sys; print(json.dumps([sys.argv[1]]))' \
        "$STARTUP_TEST_NETWORK"
)"
startup_creation="$WORK_ROOT/startup-creation.json"
startup_prestart="$WORK_ROOT/startup-prestart.json"
startup_clone_id="$(
    python3 "$HELPER" create-immutable-startup-container \
        --source-container-id "$startup_source_id" \
        --expected-image-id "$startup_source_image" \
        --compose-image-reference "$STARTUP_TEST_ALIAS" \
        --expected-image-reference-sha256 "$startup_image_reference_sha" \
        --expected-manifest-sha256 "$startup_manifest_sha" \
        --container-name "$STARTUP_TEST_CLONE" \
        --command-json '["getent","hosts","postgres"]' \
        --expected-networks-json "$startup_networks_json" \
        --expected-postgres-container-id "$startup_source_id" \
        --database-host postgres \
        --output "$startup_creation"
)"
python3 - \
    "$startup_creation" \
    "$startup_clone_id" \
    "$startup_source_image" <<'PY'
import json
import sys
from pathlib import Path

value = json.loads(Path(sys.argv[1]).read_text(encoding="utf-8"))
assert value["phase"] == "created-prestart"
assert value["containerId"] == sys.argv[2]
assert value["actualImageId"] == sys.argv[3]
assert value["configuredImage"] == sys.argv[3]
assert value["state"]["status"] == "created"
assert value["state"]["running"] is False
assert value["state"]["pid"] == 0
assert value["sourceConfigurationSha256"] == (
    value["actualConfigurationSha256"]
)
assert value["databaseHostPin"]["host"] == "postgres"
assert value["databaseHostPin"]["postgresContainerId"]
assert value["databaseHostPin"]["extraHost"].startswith("postgres:")
PY
startup_pinned_ip="$(
    python3 -c '
import json, sys
print(json.load(open(sys.argv[1], encoding="utf-8"))["databaseHostPin"]["ipAddress"])
' "$startup_creation"
)"
docker tag "$startup_alt_image" "$STARTUP_TEST_ALIAS"
set +e
python3 "$HELPER" attest-immutable-startup-container \
    --source-container-id "$startup_source_id" \
    --expected-image-id "$startup_source_image" \
    --compose-image-reference "$STARTUP_TEST_ALIAS" \
    --expected-image-reference-sha256 "$startup_image_reference_sha" \
    --expected-manifest-sha256 "$startup_manifest_sha" \
    --container-name "$STARTUP_TEST_CLONE" \
    --container-id "$startup_clone_id" \
    --command-json '["getent","hosts","postgres"]' \
    --expected-networks-json "$startup_networks_json" \
    --expected-postgres-container-id "$startup_source_id" \
    --database-host postgres \
    --output "$startup_prestart" \
    >/dev/null 2>&1
startup_retag_status=$?
set -e
[[ "$startup_retag_status" -eq 2 ]]
[[ ! -e "$startup_prestart" ]]
[[ "$(
    docker inspect --format \
        '{{.State.Status}}|{{.State.Running}}|{{.State.Pid}}|{{.Image}}|{{.Config.Image}}' \
        "$startup_clone_id"
)" == "created|false|0|$startup_source_image|$startup_source_image" ]]
docker tag "$startup_source_image" "$STARTUP_TEST_ALIAS"
python3 "$HELPER" attest-immutable-startup-container \
    --source-container-id "$startup_source_id" \
    --expected-image-id "$startup_source_image" \
    --compose-image-reference "$STARTUP_TEST_ALIAS" \
    --expected-image-reference-sha256 "$startup_image_reference_sha" \
    --expected-manifest-sha256 "$startup_manifest_sha" \
    --container-name "$STARTUP_TEST_CLONE" \
    --container-id "$startup_clone_id" \
    --command-json '["getent","hosts","postgres"]' \
    --expected-networks-json "$startup_networks_json" \
    --expected-postgres-container-id "$startup_source_id" \
    --database-host postgres \
    --output "$startup_prestart"

startup_target="$WORK_ROOT/startup-routing-target.csv"
python3 - \
    "$startup_target" \
    "$startup_source_id" <<'PY'
import csv
import sys
from pathlib import Path

fields = [
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
row = {
    "compose_project": "fixture",
    "service": "postgres",
    "configured_host": "postgres",
    "configured_port": "5432",
    "configured_database": "fstservice",
    "configured_user": "fst",
    "container_id": sys.argv[2],
    "runtime_address": "local-socket",
    "runtime_port": "5432",
    "runtime_database": "fstservice",
    "runtime_user": "fst",
    "in_recovery": "false",
    "system_identifier": "7429301450012345678",
    "role_superuser": "true",
    "role_bypass_rls": "false",
}
with Path(sys.argv[1]).open("w", newline="", encoding="utf-8") as handle:
    writer = csv.DictWriter(handle, fieldnames=fields)
    writer.writeheader()
    writer.writerow(row)
PY
startup_routing_before="$WORK_ROOT/startup-routing-before.json"
docker inspect $(docker ps -a -q) \
    | python3 "$HELPER" attest-startup-database-routing \
        --startup-attestation "$startup_prestart" \
        --target-attestation "$startup_target" \
        --expected-manifest-sha256 "$startup_manifest_sha" \
        --expected-postgres-container-id "$startup_source_id" \
        --expected-system-identifier "7429301450012345678" \
        --expected-host postgres \
        --expected-port 5432 \
        --expected-database fstservice \
        --expected-user fst \
        --expected-networks-json "$startup_networks_json" \
        --output "$startup_routing_before"
docker run -d --pull never \
    --name "$STARTUP_TEST_STALE" \
    --network "$STARTUP_TEST_NETWORK" \
    --network-alias postgres \
    postgres:17 sleep 300 >/dev/null
startup_routing_drift="$WORK_ROOT/startup-routing-drift.json"
set +e
docker inspect $(docker ps -a -q) \
    | python3 "$HELPER" attest-startup-database-routing \
        --startup-attestation "$startup_prestart" \
        --target-attestation "$startup_target" \
        --expected-manifest-sha256 "$startup_manifest_sha" \
        --expected-postgres-container-id "$startup_source_id" \
        --expected-system-identifier "7429301450012345678" \
        --expected-host postgres \
        --expected-port 5432 \
        --expected-database fstservice \
        --expected-user fst \
        --expected-networks-json "$startup_networks_json" \
        --output "$startup_routing_drift" \
        >/dev/null 2>&1
startup_alias_status=$?
set -e
[[ "$startup_alias_status" -eq 3 ]]
python3 - "$startup_routing_drift" "$STARTUP_TEST_NETWORK" <<'PY'
import json
import sys
from pathlib import Path

value = json.loads(Path(sys.argv[1]).read_text(encoding="utf-8"))
assert value["success"] is False
assert len(value["aliasOwners"][sys.argv[2]]) == 2
assert any(
    "database alias ownership is not exclusive" in failure
    for failure in value["failures"]
)
PY
[[ "$(
    docker inspect --format \
        '{{.State.Status}}|{{.State.Running}}|{{.State.Pid}}' \
        "$startup_clone_id"
)" == "created|false|0" ]]
docker rm -f "$STARTUP_TEST_STALE" >/dev/null
STARTUP_TEST_STALE=""
if docker inspect postgres >/dev/null 2>&1; then
    printf 'FAIL: container-named-postgres fixture name is already occupied\n' \
        >&2
    exit 1
fi
STARTUP_TEST_STALE="postgres"
docker run -d --pull never \
    --name "$STARTUP_TEST_STALE" \
    --network "$STARTUP_TEST_NETWORK" \
    postgres:17 sleep 300 >/dev/null
startup_routing_named_owner="$WORK_ROOT/startup-routing-named-owner.json"
set +e
docker inspect $(docker ps -a -q) \
    | python3 "$HELPER" attest-startup-database-routing \
        --startup-attestation "$startup_prestart" \
        --target-attestation "$startup_target" \
        --expected-manifest-sha256 "$startup_manifest_sha" \
        --expected-postgres-container-id "$startup_source_id" \
        --expected-system-identifier "7429301450012345678" \
        --expected-host postgres \
        --expected-port 5432 \
        --expected-database fstservice \
        --expected-user fst \
        --expected-networks-json "$startup_networks_json" \
        --output "$startup_routing_named_owner" \
        >/dev/null 2>&1
startup_named_owner_status=$?
set -e
[[ "$startup_named_owner_status" -eq 3 ]]
python3 - \
    "$startup_routing_named_owner" \
    "$STARTUP_TEST_NETWORK" <<'PY'
import json
import sys
from pathlib import Path

value = json.loads(Path(sys.argv[1]).read_text(encoding="utf-8"))
owners = value["aliasOwners"][sys.argv[2]]
named = [owner for owner in owners if owner["containerName"] == "postgres"]
assert len(named) == 1
assert "Aliases" not in named[0]["resolutionSources"]
assert (
    "DNSNames" in named[0]["resolutionSources"]
    or "containerName" in named[0]["resolutionSources"]
)
assert value["success"] is False
PY
[[ "$(
    docker inspect --format \
        '{{.State.Status}}|{{.State.Running}}|{{.State.Pid}}' \
        "$startup_clone_id"
)" == "created|false|0" ]]
docker rm -f "$STARTUP_TEST_STALE" >/dev/null
STARTUP_TEST_STALE=""
printf 'PASS: container-named postgres without alias is rejected\n'
startup_routing_after="$WORK_ROOT/startup-routing-after.json"
docker inspect $(docker ps -a -q) \
    | python3 "$HELPER" attest-startup-database-routing \
        --startup-attestation "$startup_prestart" \
        --target-attestation "$startup_target" \
        --expected-manifest-sha256 "$startup_manifest_sha" \
        --expected-postgres-container-id "$startup_source_id" \
        --expected-system-identifier "7429301450012345678" \
        --expected-host postgres \
        --expected-port 5432 \
        --expected-database fstservice \
        --expected-user fst \
        --expected-networks-json "$startup_networks_json" \
        --output "$startup_routing_after"
docker start -a "$startup_clone_id" \
    > "$WORK_ROOT/startup-clone.log" 2>&1
grep -q "^$startup_pinned_ip[[:space:]]" \
    "$WORK_ROOT/startup-clone.log"
[[ "$(
    docker inspect --format \
        '{{.State.Status}}|{{.State.ExitCode}}|{{.Image}}|{{.Config.Image}}' \
        "$startup_clone_id"
)" == "exited|0|$startup_source_image|$startup_source_image" ]]
docker rm -f "$STARTUP_TEST_CLONE" "$STARTUP_TEST_SOURCE" >/dev/null
STARTUP_TEST_CLONE=""
STARTUP_TEST_SOURCE=""
docker network rm "$STARTUP_TEST_NETWORK" >/dev/null
STARTUP_TEST_NETWORK=""
docker image rm "$STARTUP_TEST_ALIAS" "$STARTUP_TEST_ALT_IMAGE" >/dev/null
STARTUP_TEST_ALIAS=""
STARTUP_TEST_ALT_IMAGE=""
printf 'PASS: immutable startup image and exclusive database alias attested before start\n'

PG_TEST_CONTAINER="fst-retired-schema-capture-test-$$"
docker run -d --rm --pull never \
    --name "$PG_TEST_CONTAINER" \
    -e POSTGRES_HOST_AUTH_METHOD=trust \
    postgres:17 -c wal_level=logical >/dev/null
for _ in {1..60}; do
    if docker exec "$PG_TEST_CONTAINER" \
        pg_isready -h /var/run/postgresql -U postgres -d postgres \
        >/dev/null 2>&1; then
        break
    fi
    sleep 0.5
done
docker exec "$PG_TEST_CONTAINER" \
    pg_isready -h /var/run/postgresql -U postgres -d postgres \
    >/dev/null
{
    cat <<'SQL'
CREATE TABLE public.fixture_capture_relation (id bigint);
CREATE TEMP TABLE retired_cleanup_expected (
    object_order integer NOT NULL,
    family text NOT NULL,
    object_type text NOT NULL,
    expected_relkind "char" NOT NULL,
    schema_name text NOT NULL,
    object_name text NOT NULL,
    parent_schema text,
    parent_name text,
    owner_column text,
    row_policy text NOT NULL,
    expected_rows bigint,
    PRIMARY KEY (schema_name, object_name)
) ON COMMIT PRESERVE ROWS;
INSERT INTO retired_cleanup_expected VALUES (
    1,
    'fixture-family',
    'table',
    'r'::"char",
    'public',
    'fixture_capture_relation',
    NULL,
    NULL,
    NULL,
    'zero',
    0
);
SQL
    cat "$SCRIPT_DIR/sql/postgres-retired-schema-cleanup/capture-relations.sql"
} | docker exec -i "$PG_TEST_CONTAINER" \
    env -u PGHOST -u PGHOSTADDR -u PGPORT \
        -u PGSERVICE -u PGSERVICEFILE \
        psql -X -q -v ON_ERROR_STOP=1 \
        -h /var/run/postgresql -U postgres -d postgres -P pager=off \
    > "$WORK_ROOT/real-postgres-capture.csv"
python3 - "$WORK_ROOT/real-postgres-capture.csv" <<'PY'
import csv
import sys
from pathlib import Path

with Path(sys.argv[1]).open(newline="", encoding="utf-8") as handle:
    rows = list(csv.DictReader(handle))
assert len(rows) == 1
row = rows[0]
assert row["family"] == "fixture-family"
assert row["name"] == "fixture_capture_relation"
assert row["actual_relkind"] == "r"
assert row["owner"] == "postgres"
assert row["row_count"] == "0"
PY
{
    cat <<'SQL'
CREATE TABLE public.leaderboard_current_entries (
    id bigint,
    value text
) PARTITION BY RANGE (id);
CREATE TABLE public.leaderboard_current_entries_bass
    PARTITION OF public.leaderboard_current_entries
    FOR VALUES FROM (0) TO (10);
CREATE SEQUENCE public.player_score_observations_id_seq;
CREATE TEMP TABLE retired_cleanup_expected (
    object_order integer NOT NULL,
    family text NOT NULL,
    object_type text NOT NULL,
    expected_relkind "char" NOT NULL,
    schema_name text NOT NULL,
    object_name text NOT NULL,
    parent_schema text,
    parent_name text,
    owner_column text,
    row_policy text NOT NULL,
    expected_rows bigint,
    PRIMARY KEY (schema_name, object_name)
) ON COMMIT PRESERVE ROWS;
INSERT INTO retired_cleanup_expected VALUES (
    1,
    'logical-shadow',
    'table',
    'r'::"char",
    'public',
    'leaderboard_current_entries_bass',
    'public',
    'leaderboard_current_entries',
    NULL,
    'zero',
    0
);
SQL
    cat "$SCRIPT_DIR/sql/postgres-retired-schema-cleanup/capture-column-catalog.sql"
} | docker exec -i "$PG_TEST_CONTAINER" \
    env -u PGHOST -u PGHOSTADDR -u PGPORT \
        -u PGSERVICE -u PGSERVICEFILE \
        psql -X -q -v ON_ERROR_STOP=1 \
        -h /var/run/postgresql -U postgres -d postgres -P pager=off \
    > "$WORK_ROOT/real-postgres-null-statistics-target.csv"
python3 - "$WORK_ROOT/real-postgres-null-statistics-target.csv" <<'PY'
import csv
import sys
from pathlib import Path

with Path(sys.argv[1]).open(newline="", encoding="utf-8") as handle:
    rows = list(csv.DictReader(handle))
assert [row["column_name"] for row in rows] == ["id", "value"]
assert all(row["statistics_target"] == "" for row in rows)
PY
{
    cat <<'SQL'
CREATE TEMP TABLE retired_cleanup_expected (
    object_order integer NOT NULL,
    family text NOT NULL,
    object_type text NOT NULL,
    expected_relkind "char" NOT NULL,
    schema_name text NOT NULL,
    object_name text NOT NULL,
    parent_schema text,
    parent_name text,
    owner_column text,
    row_policy text NOT NULL,
    expected_rows bigint,
    PRIMARY KEY (schema_name, object_name)
) ON COMMIT PRESERVE ROWS;
INSERT INTO retired_cleanup_expected VALUES (
    1,
    'logical-shadow',
    'table',
    'r'::"char",
    'public',
    'leaderboard_current_entries_bass',
    'public',
    'leaderboard_current_entries',
    NULL,
    'zero',
    0
);
\set ON_ERROR_STOP on
COPY (
SQL
    cat "$SCRIPT_DIR/sql/postgres-retired-schema-cleanup/catalog-signature-query.sql"
    printf '%s\n' ') TO STDOUT WITH (FORMAT CSV, HEADER TRUE);'
} | docker exec -i "$PG_TEST_CONTAINER" \
    env -u PGHOST -u PGHOSTADDR -u PGPORT \
        -u PGSERVICE -u PGSERVICEFILE \
        psql -X -q -v ON_ERROR_STOP=1 \
        -h /var/run/postgresql -U postgres -d postgres -P pager=off \
    > "$WORK_ROOT/real-postgres-null-statistics-signature.csv"
python3 - \
    "$READY_FIXTURE/pre/column-catalog.raw.csv" \
    "$READY_FIXTURE/pre/catalog-signature.raw.csv" \
    "$WORK_ROOT/real-postgres-null-statistics-target.csv" \
    "$WORK_ROOT/real-postgres-null-statistics-signature.csv" \
    "$WORK_ROOT/real-canonical-column-input.csv" \
    "$WORK_ROOT/real-canonical-signature-input.csv" <<'PY'
import csv
import sys
from pathlib import Path

(
    fixture_columns_path,
    fixture_signature_path,
    real_columns_path,
    real_signature_path,
    output_columns_path,
    output_signature_path,
) = map(Path, sys.argv[1:])

with fixture_columns_path.open(newline="", encoding="utf-8") as handle:
    reader = csv.DictReader(handle)
    column_fields = reader.fieldnames
    columns = [
        row
        for row in reader
        if row["name"] != "leaderboard_current_entries_bass"
    ]
with real_columns_path.open(newline="", encoding="utf-8") as handle:
    columns.extend(csv.DictReader(handle))
with output_columns_path.open("w", newline="", encoding="utf-8") as handle:
    writer = csv.DictWriter(handle, fieldnames=column_fields)
    writer.writeheader()
    writer.writerows(columns)

with fixture_signature_path.open(newline="", encoding="utf-8") as handle:
    reader = csv.DictReader(handle)
    signature_fields = reader.fieldnames
    signature = [
        row
        for row in reader
        if not (
            row["category"] == "column"
            and row["object_identity"].startswith(
                "public.leaderboard_current_entries_bass#"
            )
        )
    ]
with real_signature_path.open(newline="", encoding="utf-8") as handle:
    signature.extend(
        row
        for row in csv.DictReader(handle)
        if row["category"] == "column"
    )
with output_signature_path.open(
    "w",
    newline="",
    encoding="utf-8",
) as handle:
    writer = csv.DictWriter(handle, fieldnames=signature_fields)
    writer.writeheader()
    writer.writerows(signature)
PY
python3 "$HELPER" prepare-column-catalog \
    --objects "$OBJECTS" \
    --input "$WORK_ROOT/real-canonical-column-input.csv" \
    --output "$WORK_ROOT/real-canonical-columns.csv"
python3 "$HELPER" prepare-catalog-signature \
    --input "$WORK_ROOT/real-canonical-signature-input.csv" \
    --query \
        "$SCRIPT_DIR/sql/postgres-retired-schema-cleanup/catalog-signature-query.sql" \
    --column-catalog "$WORK_ROOT/real-canonical-columns.csv" \
    --output "$WORK_ROOT/real-canonical-signature.csv" \
    --metadata-output "$WORK_ROOT/real-canonical-signature-metadata.json" \
    --expected-sql-output "$WORK_ROOT/real-canonical-expected.sql" \
    --assert-sql-output "$WORK_ROOT/real-canonical-assert.sql"
python3 - "$WORK_ROOT/real-canonical-signature.csv" <<'PY'
import csv
import json
import sys
from pathlib import Path

with Path(sys.argv[1]).open(newline="", encoding="utf-8") as handle:
    rows = [
        row
        for row in csv.DictReader(handle)
        if row["category"] == "column"
        and row["object_identity"].startswith(
            "public.leaderboard_current_entries_bass#"
        )
    ]
assert len(rows) == 2
for row in rows:
    detail = json.loads(row["detail"])
    assert isinstance(detail["typeOid"], int)
    assert detail["statisticsTarget"] is None
PY
printf 'PASS: real PostgreSQL OID/null catalog canonicalization\n'
{
    cat <<'SQL'
CREATE SEQUENCE public.fixture_expected_owned_seq
    OWNED BY public.fixture_capture_relation.id;
CREATE SEQUENCE public.fixture_custom_owned_seq
    OWNED BY public.fixture_capture_relation.id;
CREATE TEMP TABLE retired_cleanup_expected (
    object_order integer NOT NULL,
    family text NOT NULL,
    object_type text NOT NULL,
    expected_relkind "char" NOT NULL,
    schema_name text NOT NULL,
    object_name text NOT NULL,
    parent_schema text,
    parent_name text,
    owner_column text,
    row_policy text NOT NULL,
    expected_rows bigint,
    PRIMARY KEY (schema_name, object_name)
) ON COMMIT PRESERVE ROWS;
INSERT INTO retired_cleanup_expected VALUES (
    1,
    'fixture-family',
    'table',
    'r'::"char",
    'public',
    'fixture_capture_relation',
    NULL,
    NULL,
    NULL,
    'zero',
    0
);
SQL
    cat "$SCRIPT_DIR/sql/postgres-retired-schema-cleanup/capture-owned-objects.sql"
} | docker exec -i "$PG_TEST_CONTAINER" \
    env -u PGHOST -u PGHOSTADDR -u PGPORT \
        -u PGSERVICE -u PGSERVICEFILE \
        psql -X -q -v ON_ERROR_STOP=1 \
        -h /var/run/postgresql -U postgres -d postgres -P pager=off \
    > "$WORK_ROOT/real-postgres-owned-objects.csv"
python3 - "$WORK_ROOT/real-postgres-owned-objects.csv" <<'PY'
import csv
import sys
from pathlib import Path

with Path(sys.argv[1]).open(newline="", encoding="utf-8") as handle:
    rows = [
        row
        for row in csv.DictReader(handle)
        if row["kind"] == "sequence-owner"
    ]
assert {
    (
        row["schema"],
        row["name"],
        row["target_schema"],
        row["target_name"],
        row["definition"],
        row["state"],
    )
    for row in rows
} == {
    (
        "public",
        "fixture_expected_owned_seq",
        "public",
        "fixture_capture_relation",
        "id",
        "a",
    ),
    (
        "public",
        "fixture_custom_owned_seq",
        "public",
        "fixture_capture_relation",
        "id",
        "a",
    ),
}
PY
docker exec -i "$PG_TEST_CONTAINER" \
    env -u PGHOST -u PGHOSTADDR -u PGPORT \
        -u PGSERVICE -u PGSERVICEFILE \
        psql -X -q -v ON_ERROR_STOP=1 \
        -h /var/run/postgresql -U postgres -d postgres -P pager=off <<'SQL'
CREATE TABLE public.fixture_sequence_lock_owner (id bigint);
CREATE TABLE public.fixture_active_sequence_owner (id bigint);
CREATE SEQUENCE public.fixture_sequence_lock_seq
    OWNED BY public.fixture_sequence_lock_owner.id;
CREATE SEQUENCE public.fixture_sequence_state_seq
    AS bigint
    START WITH 100
    INCREMENT BY 7
    MINVALUE 10
    MAXVALUE 100000
    CACHE 1
    NO CYCLE;
SQL
python3 - "$PG_TEST_CONTAINER" <<'PY'
import subprocess
import sys

container = sys.argv[1]
psql = [
    "docker",
    "exec",
    "-i",
    container,
    "env",
    "-u",
    "PGHOST",
    "-u",
    "PGHOSTADDR",
    "-u",
    "PGPORT",
    "-u",
    "PGSERVICE",
    "-u",
    "PGSERVICEFILE",
    "psql",
    "-X",
    "-qAt",
    "-v",
    "ON_ERROR_STOP=1",
    "-h",
    "/var/run/postgresql",
    "-U",
    "postgres",
    "-d",
    "postgres",
]
holder = subprocess.Popen(
    psql,
    stdin=subprocess.PIPE,
    stdout=subprocess.PIPE,
    stderr=subprocess.PIPE,
    text=True,
    bufsize=1,
)
holder.stdin.write(
    """
BEGIN;
SELECT pg_catalog.pg_advisory_xact_lock(5067481511116519502);
LOCK TABLE public.fixture_sequence_lock_owner
    IN ACCESS EXCLUSIVE MODE;
DO $sequence_dependency_lock$
DECLARE
    locked_dependency record;
BEGIN
    FOR locked_dependency IN
        SELECT dependency.objid,
               dependency.refobjid,
               dependency.refobjsubid,
               dependency.deptype
        FROM pg_catalog.pg_depend dependency
        WHERE dependency.classid = 'pg_class'::regclass
          AND dependency.objid =
              'public.fixture_sequence_lock_seq'::regclass
          AND dependency.refclassid = 'pg_class'::regclass
          AND dependency.deptype IN ('a', 'i')
        FOR SHARE OF dependency
    LOOP
        NULL;
    END LOOP;
END
$sequence_dependency_lock$;
SELECT 'LOCKED';
"""
)
holder.stdin.flush()
while True:
    line = holder.stdout.readline()
    if not line:
        raise SystemExit(
            "sequence lock holder ended early: " + holder.stderr.read()
        )
    if line.strip() == "LOCKED":
        break

contender = subprocess.run(
    psql,
    input=(
        "SET lock_timeout = '1s'; "
        "ALTER SEQUENCE public.fixture_sequence_lock_seq "
        "OWNED BY public.fixture_active_sequence_owner.id;\n"
    ),
    text=True,
    capture_output=True,
    timeout=10,
)
assert contender.returncode != 0
assert "lock timeout" in contender.stderr.lower()

holder.stdin.write("ROLLBACK;\n\\q\n")
holder.stdin.flush()
holder.wait(timeout=10)
assert holder.returncode == 0, holder.stderr.read()

owner = subprocess.run(
    psql,
    input="""
SELECT owner_table.relname || '.' || owner_attribute.attname
FROM pg_catalog.pg_depend dependency
JOIN pg_catalog.pg_class sequence_row
  ON sequence_row.oid = dependency.objid
JOIN pg_catalog.pg_class owner_table
  ON owner_table.oid = dependency.refobjid
JOIN pg_catalog.pg_attribute owner_attribute
  ON owner_attribute.attrelid = owner_table.oid
 AND owner_attribute.attnum = dependency.refobjsubid
WHERE sequence_row.relname = 'fixture_sequence_lock_seq'
  AND dependency.deptype = 'a';
""",
    text=True,
    capture_output=True,
    timeout=10,
)
assert owner.returncode == 0, owner.stderr
assert owner.stdout.strip() == "fixture_sequence_lock_owner.id"

state_query = """
SELECT sequence_state.last_value::text || '|' ||
       sequence_state.is_called::text || '|' ||
       sequence_catalog.seqstart::text || '|' ||
       sequence_catalog.seqincrement::text || '|' ||
       sequence_catalog.seqmin::text || '|' ||
       sequence_catalog.seqmax::text || '|' ||
       sequence_catalog.seqcache::text || '|' ||
       sequence_catalog.seqcycle::text
FROM public.fixture_sequence_state_seq sequence_state
JOIN pg_catalog.pg_sequence sequence_catalog
  ON sequence_catalog.seqrelid =
     'public.fixture_sequence_state_seq'::regclass;
"""
state_holder = subprocess.Popen(
    psql,
    stdin=subprocess.PIPE,
    stdout=subprocess.PIPE,
    stderr=subprocess.PIPE,
    text=True,
    bufsize=1,
)
state_holder.stdin.write(
    """
BEGIN;
SELECT pg_catalog.pg_advisory_xact_lock(5067481511116519502);
DO $sequence_state_lock$
DECLARE
    locked_catalog_row record;
BEGIN
    PERFORM last_value, is_called
    FROM public.fixture_sequence_state_seq;
    FOR locked_catalog_row IN
        SELECT relation.oid
        FROM pg_catalog.pg_class relation
        WHERE relation.oid =
              'public.fixture_sequence_state_seq'::regclass
        FOR SHARE OF relation
    LOOP
        NULL;
    END LOOP;
    FOR locked_catalog_row IN
        SELECT sequence_catalog.seqrelid
        FROM pg_catalog.pg_sequence sequence_catalog
        WHERE sequence_catalog.seqrelid =
              'public.fixture_sequence_state_seq'::regclass
        FOR SHARE OF sequence_catalog
    LOOP
        NULL;
    END LOOP;
END
$sequence_state_lock$;
"""
    + state_query
    + "SELECT 'STATE_LOCKED';\n"
)
state_holder.stdin.flush()
baseline = ""
while True:
    line = state_holder.stdout.readline()
    if not line:
        raise SystemExit(
            "sequence state holder ended early: "
            + state_holder.stderr.read()
        )
    value = line.strip()
    if value == "STATE_LOCKED":
        break
    if "|" in value:
        baseline = value
assert baseline

def blocked_mutator(sql, guarded):
    prefix = (
        "BEGIN; "
        "SET statement_timeout = '2s'; "
    )
    if guarded:
        prefix += (
            "SELECT pg_catalog.pg_advisory_xact_lock"
            "(5067481511116519502); "
        )
    else:
        prefix += "SET lock_timeout = '1s'; "
    result = subprocess.run(
        psql,
        input=prefix + sql + " COMMIT;\n",
        text=True,
        capture_output=True,
        timeout=10,
    )
    assert result.returncode != 0, (sql, result.stdout, result.stderr)
    return result.stderr.lower()

assert "statement timeout" in blocked_mutator(
    "SELECT nextval('public.fixture_sequence_state_seq');",
    guarded=True,
)
assert "statement timeout" in blocked_mutator(
    "SELECT setval('public.fixture_sequence_state_seq', 999, true);",
    guarded=True,
)
assert "lock timeout" in blocked_mutator(
    "ALTER SEQUENCE public.fixture_sequence_state_seq "
    "RESTART WITH 777;",
    guarded=False,
)
assert "lock timeout" in blocked_mutator(
    "ALTER SEQUENCE public.fixture_sequence_state_seq "
    "INCREMENT BY 11;",
    guarded=False,
)

state_holder.stdin.write(state_query + "SELECT 'STATE_RECHECK';\n")
state_holder.stdin.flush()
recaptured = ""
while True:
    line = state_holder.stdout.readline()
    if not line:
        raise SystemExit(
            "sequence state holder ended before recapture: "
            + state_holder.stderr.read()
        )
    value = line.strip()
    if value == "STATE_RECHECK":
        break
    if "|" in value:
        recaptured = value
assert recaptured == baseline
state_holder.stdin.write("ROLLBACK;\n\\q\n")
state_holder.stdin.flush()
state_holder.wait(timeout=10)
assert state_holder.returncode == 0, state_holder.stderr.read()

after = subprocess.run(
    psql,
    input=state_query,
    text=True,
    capture_output=True,
    timeout=10,
)
assert after.returncode == 0, after.stderr
assert after.stdout.strip() == baseline
PY
printf 'PASS: dependency row lock blocks active-column sequence reassignment\n'
printf 'PASS: guarded nextval/setval and locked restart/options preserve sequence state\n'
{
    cat <<'SQL'
CREATE SCHEMA fixture_publication_schema;
CREATE TABLE fixture_publication_schema.schema_member (id bigint);
CREATE TABLE public.fixture_explicit_publication_member (id bigint);
CREATE PUBLICATION fixture_all_tables FOR ALL TABLES;
CREATE PUBLICATION fixture_schema
    FOR TABLES IN SCHEMA fixture_publication_schema;
CREATE PUBLICATION fixture_explicit
    FOR TABLE public.fixture_explicit_publication_member;
CREATE TEMP TABLE retired_cleanup_expected (
    object_order integer NOT NULL,
    family text NOT NULL,
    object_type text NOT NULL,
    expected_relkind "char" NOT NULL,
    schema_name text NOT NULL,
    object_name text NOT NULL,
    parent_schema text,
    parent_name text,
    owner_column text,
    row_policy text NOT NULL,
    expected_rows bigint,
    PRIMARY KEY (schema_name, object_name)
) ON COMMIT PRESERVE ROWS;
INSERT INTO retired_cleanup_expected VALUES
    (
        1, 'fixture-family', 'table', 'r'::"char",
        'public', 'fixture_capture_relation',
        NULL, NULL, NULL, 'zero', 0
    ),
    (
        2, 'fixture-family', 'table', 'r'::"char",
        'fixture_publication_schema', 'schema_member',
        NULL, NULL, NULL, 'zero', 0
    ),
    (
        3, 'fixture-family', 'table', 'r'::"char",
        'public', 'fixture_explicit_publication_member',
        NULL, NULL, NULL, 'zero', 0
    );
SQL
    cat "$SCRIPT_DIR/sql/postgres-retired-schema-cleanup/capture-external-dependencies.sql"
} | docker exec -i "$PG_TEST_CONTAINER" \
    env -u PGHOST -u PGHOSTADDR -u PGPORT \
        -u PGSERVICE -u PGSERVICEFILE \
        psql -X -q -v ON_ERROR_STOP=1 \
        -h /var/run/postgresql -U postgres -d postgres -P pager=off \
    > "$WORK_ROOT/real-postgres-effective-publications.csv"
python3 - "$WORK_ROOT/real-postgres-effective-publications.csv" <<'PY'
import csv
import json
import sys
from pathlib import Path

with Path(sys.argv[1]).open(newline="", encoding="utf-8") as handle:
    rows = [
        row
        for row in csv.DictReader(handle)
        if row["dependency_kind"] == "publication"
    ]
by_publication = {}
for row in rows:
    by_publication.setdefault(row["dependent_object"], []).append(
        (
            row["referenced_object"],
            json.loads(row["detail"])["membershipMode"],
        )
    )
assert sorted(by_publication["fixture_all_tables"]) == [
    ("fixture_publication_schema.schema_member", "all-tables"),
    ("public.fixture_capture_relation", "all-tables"),
    ("public.fixture_explicit_publication_member", "all-tables"),
]
assert by_publication["fixture_schema"] == [
    ("fixture_publication_schema.schema_member", "schema")
]
assert by_publication["fixture_explicit"] == [
    ("public.fixture_explicit_publication_member", "explicit")
]
PY
{
    cat <<'SQL'
CREATE TEMP TABLE retired_cleanup_expected (
    object_order integer NOT NULL,
    family text NOT NULL,
    object_type text NOT NULL,
    expected_relkind "char" NOT NULL,
    schema_name text NOT NULL,
    object_name text NOT NULL,
    parent_schema text,
    parent_name text,
    owner_column text,
    row_policy text NOT NULL,
    expected_rows bigint,
    PRIMARY KEY (schema_name, object_name)
) ON COMMIT PRESERVE ROWS;
INSERT INTO retired_cleanup_expected VALUES
    (
        1, 'fixture-family', 'table', 'r'::"char",
        'public', 'fixture_capture_relation',
        NULL, NULL, NULL, 'zero', 0
    ),
    (
        2, 'fixture-family', 'table', 'r'::"char",
        'fixture_publication_schema', 'schema_member',
        NULL, NULL, NULL, 'zero', 0
    ),
    (
        3, 'fixture-family', 'table', 'r'::"char",
        'public', 'fixture_explicit_publication_member',
        NULL, NULL, NULL, 'zero', 0
    );
\set ON_ERROR_STOP on
COPY (
SQL
    cat "$SCRIPT_DIR/sql/postgres-retired-schema-cleanup/catalog-signature-query.sql"
    printf '%s\n' ') TO STDOUT WITH (FORMAT CSV, HEADER TRUE);'
} | docker exec -i "$PG_TEST_CONTAINER" \
    env -u PGHOST -u PGHOSTADDR -u PGPORT \
        -u PGSERVICE -u PGSERVICEFILE \
        psql -X -q -v ON_ERROR_STOP=1 \
        -h /var/run/postgresql -U postgres -d postgres -P pager=off \
    > "$WORK_ROOT/real-postgres-publication-signature.csv"
python3 - "$WORK_ROOT/real-postgres-publication-signature.csv" <<'PY'
import csv
import json
import sys
from pathlib import Path

with Path(sys.argv[1]).open(newline="", encoding="utf-8") as handle:
    rows = [
        row
        for row in csv.DictReader(handle)
        if row["category"] == "publication"
    ]
modes = {
    row["object_identity"]: json.loads(row["detail"])["membershipMode"]
    for row in rows
}
assert modes[
    "fixture_all_tables:public.fixture_capture_relation"
] == "all-tables"
assert modes[
    "fixture_schema:fixture_publication_schema.schema_member"
] == "schema"
assert modes[
    "fixture_explicit:public.fixture_explicit_publication_member"
] == "explicit"
PY
docker rm -f "$PG_TEST_CONTAINER" >/dev/null
PG_TEST_CONTAINER=""
printf 'PASS: real PostgreSQL read-only capture transaction\n'
printf 'PASS: real inherited partition NULL statistics target capture\n'
printf 'PASS: real PostgreSQL inverse owned-sequence capture\n'
printf 'PASS: real PostgreSQL effective publication capture/signature\n'

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
