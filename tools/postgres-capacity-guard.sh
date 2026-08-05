#!/usr/bin/env bash
set -euo pipefail

COMPOSE_DIR="${COMPOSE_DIR:-/home/sfenton/Docker/FestivalServiceTracker}"
PG_CONTAINER="${PG_CONTAINER:-fst-postgres}"
PG_USER="${PG_USER:-fst}"
PG_DB="${PG_DB:-fstservice}"
PG_PORT="${PG_PORT:-5432}"
FST_STORAGE_ROOT="${FST_STORAGE_ROOT:-/mnt/docker-storage}"
ACTION_CLASS="${ACTION_CLASS:-observation}"
OUTPUT_FILE="${OUTPUT_FILE:-}"
TRANSIENT_BUILD_BYTES="${TRANSIENT_BUILD_BYTES:-0}"
REQUIRED_SCRATCH_BYTES="${REQUIRED_SCRATCH_BYTES:-0}"
EXPECTED_RECLAIM_BYTES="${EXPECTED_RECLAIM_BYTES:-0}"
ESTIMATED_FULL_SCRAPE_GROWTH_BYTES="${ESTIMATED_FULL_SCRAPE_GROWTH_BYTES:-60392999803}"
EXPECTED_FULL_SCRAPES_PER_DAY="${EXPECTED_FULL_SCRAPES_PER_DAY:-2}"
MINIMUM_HEADROOM_DAYS="${MINIMUM_HEADROOM_DAYS:-7}"
MINIMUM_HEADROOM_BYTES_OVERRIDE="${MINIMUM_HEADROOM_BYTES_OVERRIDE:-0}"

usage() {
    cat <<'EOF'
Usage: tools/postgres-capacity-guard.sh [options]

Captures the live FST Postgres capacity/publication preflight and enforces
action-specific same-drive headroom gates without changing production.

Options:
  --action-class CLASS          observation, scrape, post-process,
                                optional-build, reclaim, maintenance, or rewrite
  --transient-build-bytes N     Estimated temporary bytes for the action
  --required-scratch-bytes N    Additional rewrite/repack scratch bytes
  --expected-reclaim-bytes N    Explicit reclaim estimate used only to prove
                                the action restores one emergency window
  --minimum-headroom-bytes N    Explicit maintenance/rewrite floor replacing
                                the default days-based alert threshold
  --output FILE                 Also persist the JSON report to FILE
  --compose-dir DIR             Production compose directory
  --pg-container NAME           PostgreSQL container name
  --pg-user USER                PostgreSQL user
  --pg-db DATABASE              PostgreSQL database
  --pg-port PORT                PostgreSQL local socket port
  --fst-storage-root DIR        Required FST storage root
  -h, --help                    Show this help

Defaults use the corrected 60,392,999,803-byte full-run emergency window
measured through scrape 1265, two full scrapes per day, and a seven-day
alert/defer threshold. Override the matching environment variables only with
newer measured evidence.

Exit codes:
  0  Action is allowed (possibly with a seven-day capacity alert)
  2  Optional build deferred by the headroom gate
  3  Reclaim/rewrite/maintenance action rejected by its safety gate
  4  Scrape/post-process rejected below one full-scrape emergency headroom

The reclaim class is only for a proven space-releasing action with zero
transient-build and required-scratch bytes. By default it still requires one
full-scrape emergency buffer. Below that buffer, an explicit conservative
--expected-reclaim-bytes estimate may allow a conflict-free reclaim only when
the projected post-action free space restores the full emergency window.
EOF
}

while [[ $# -gt 0 ]]; do
    case "$1" in
        --action-class) ACTION_CLASS="$2"; shift 2 ;;
        --transient-build-bytes) TRANSIENT_BUILD_BYTES="$2"; shift 2 ;;
        --required-scratch-bytes) REQUIRED_SCRATCH_BYTES="$2"; shift 2 ;;
        --expected-reclaim-bytes) EXPECTED_RECLAIM_BYTES="$2"; shift 2 ;;
        --minimum-headroom-bytes) MINIMUM_HEADROOM_BYTES_OVERRIDE="$2"; shift 2 ;;
        --output) OUTPUT_FILE="$2"; shift 2 ;;
        --compose-dir) COMPOSE_DIR="$2"; shift 2 ;;
        --pg-container) PG_CONTAINER="$2"; shift 2 ;;
        --pg-user) PG_USER="$2"; shift 2 ;;
        --pg-db) PG_DB="$2"; shift 2 ;;
        --pg-port) PG_PORT="$2"; shift 2 ;;
        --fst-storage-root) FST_STORAGE_ROOT="$2"; shift 2 ;;
        -h|--help) usage; exit 0 ;;
        *) printf 'Unknown option: %s\n\n' "$1" >&2; usage >&2; exit 64 ;;
    esac
done

require_command() {
    if ! command -v "$1" >/dev/null 2>&1; then
        printf 'ERROR: required command not found: %s\n' "$1" >&2
        exit 1
    fi
}

require_non_negative_integer() {
    if [[ ! "$2" =~ ^[0-9]+$ ]]; then
        printf 'ERROR: %s must be a non-negative integer, got %s\n' "$1" "$2" >&2
        exit 64
    fi
}

require_positive_integer() {
    require_non_negative_integer "$1" "$2"
    if [[ "$2" == "0" ]]; then
        printf 'ERROR: %s must be greater than zero\n' "$1" >&2
        exit 64
    fi
}

for command in docker df findmnt python3 realpath timeout; do
    require_command "$command"
done

for pair in \
    "TRANSIENT_BUILD_BYTES:$TRANSIENT_BUILD_BYTES" \
    "REQUIRED_SCRATCH_BYTES:$REQUIRED_SCRATCH_BYTES" \
    "EXPECTED_RECLAIM_BYTES:$EXPECTED_RECLAIM_BYTES" \
    "MINIMUM_HEADROOM_BYTES_OVERRIDE:$MINIMUM_HEADROOM_BYTES_OVERRIDE"
do
    require_non_negative_integer "${pair%%:*}" "${pair#*:}"
done

for pair in \
    "ESTIMATED_FULL_SCRAPE_GROWTH_BYTES:$ESTIMATED_FULL_SCRAPE_GROWTH_BYTES" \
    "EXPECTED_FULL_SCRAPES_PER_DAY:$EXPECTED_FULL_SCRAPES_PER_DAY" \
    "MINIMUM_HEADROOM_DAYS:$MINIMUM_HEADROOM_DAYS"
do
    require_positive_integer "${pair%%:*}" "${pair#*:}"
done

case "$ACTION_CLASS" in
    observation|scrape|post-process|optional-build|reclaim|maintenance|rewrite) ;;
    *)
        printf 'ERROR: unsupported action class: %s\n' "$ACTION_CLASS" >&2
        exit 64
        ;;
esac

if [[ "$ACTION_CLASS" == "reclaim" ]] \
    && (( TRANSIENT_BUILD_BYTES != 0 || REQUIRED_SCRATCH_BYTES != 0 )); then
    printf 'ERROR: reclaim requires zero transient-build and required-scratch bytes\n' >&2
    exit 64
fi

if [[ "$ACTION_CLASS" != "reclaim" ]] && (( EXPECTED_RECLAIM_BYTES != 0 )); then
    printf 'ERROR: expected-reclaim-bytes is only valid for reclaim actions\n' >&2
    exit 64
fi

if (( MINIMUM_HEADROOM_BYTES_OVERRIDE != 0 )) \
    && [[ "$ACTION_CLASS" != "maintenance" && "$ACTION_CLASS" != "rewrite" ]]; then
    printf 'ERROR: minimum-headroom-bytes is only valid for maintenance or rewrite actions\n' >&2
    exit 64
fi

if (( MINIMUM_HEADROOM_BYTES_OVERRIDE != 0 \
      && MINIMUM_HEADROOM_BYTES_OVERRIDE < ESTIMATED_FULL_SCRAPE_GROWTH_BYTES )); then
    printf 'ERROR: minimum-headroom-bytes cannot be below one full-scrape emergency window (%s)\n' \
        "$ESTIMATED_FULL_SCRAPE_GROWTH_BYTES" >&2
    exit 64
fi

if [[ ! -d "$COMPOSE_DIR" ]]; then
    printf 'ERROR: production compose directory does not exist: %s\n' "$COMPOSE_DIR" >&2
    exit 1
fi

pg_data_source="$(docker inspect --format '{{range .Mounts}}{{if eq .Destination "/var/lib/postgresql/data"}}{{.Source}}{{end}}{{end}}' "$PG_CONTAINER")"
if [[ -z "$pg_data_source" ]]; then
    printf 'ERROR: %s has no /var/lib/postgresql/data mount\n' "$PG_CONTAINER" >&2
    exit 1
fi

pg_data_source="$(realpath -m "$pg_data_source")"
fst_storage_root="$(realpath -m "$FST_STORAGE_ROOT")"
case "$pg_data_source/" in
    "$fst_storage_root/"*) ;;
    *)
        printf 'ERROR: Postgres data path %s is outside required FST storage root %s\n' \
            "$pg_data_source" "$fst_storage_root" >&2
        exit 1
        ;;
esac

read -r filesystem_total_bytes filesystem_used_bytes filesystem_free_bytes filesystem_used_percent filesystem_mount < <(
    df -B1 --output=size,used,avail,pcent,target "$pg_data_source" | tail -n 1
)
filesystem_used_percent="${filesystem_used_percent%\%}"
read -r filesystem_source filesystem_type < <(
    findmnt -T "$pg_data_source" -n -o SOURCE,FSTYPE
)

sql_result="$(
    timeout --signal=TERM --kill-after=30s 7m \
        docker exec \
        -e PGCONNECT_TIMEOUT=10 \
        -e PGOPTIONS="-c row_security=off" \
        -e PGHOST= \
        -e PGHOSTADDR= \
        -e PGPORT= \
        -e PGSERVICE= \
        -e PGSERVICEFILE= \
        "$PG_CONTAINER" psql \
        -X -v ON_ERROR_STOP=1 -h /var/run/postgresql -p "$PG_PORT" \
        -U "$PG_USER" -d "$PG_DB" -AtF '|' \
        -c "
            SELECT
                pg_database_size(current_database()),
                COALESCE((SELECT SUM(size)::bigint FROM pg_ls_waldir()), 0),
                COALESCE((
                    SELECT MAX(id)
                    FROM scrape_log
                    WHERE completed_at IS NULL
                      AND COALESCE(to_jsonb(scrape_log)->>'status', 'running') = 'running'
                ), 0),
                COALESCE((SELECT published_scrape_id FROM scrape_publication_state WHERE id = TRUE), 0),
                COALESCE((SELECT public_reads_frozen FROM scrape_publication_state WHERE id = TRUE), FALSE),
                COALESCE((SELECT public_reads_frozen_reason FROM scrape_publication_state WHERE id = TRUE), ''),
                (SELECT COUNT(*) FROM pg_stat_progress_vacuum),
                (SELECT COUNT(*) FROM pg_stat_progress_create_index),
                (
                    SELECT COUNT(*)
                    FROM pg_stat_activity
                    WHERE pid <> pg_backend_pid()
                      AND state = 'active'
                      AND (
                          query ILIKE '%repack%'
                          OR query ILIKE '%rewrite%'
                          OR query ILIKE 'vacuum full%'
                      )
                ),
                (SELECT COUNT(*) FROM pg_locks WHERE NOT granted);
        "
)"

IFS='|' read -r \
    database_bytes wal_directory_bytes active_scrape_id published_scrape_id \
    public_reads_frozen public_reads_frozen_reason active_vacuums active_index_builds \
    active_rewrites ungranted_locks <<< "$sql_result"

if [[ -n "$OUTPUT_FILE" ]]; then
    mkdir -p "$(dirname -- "$OUTPUT_FILE")"
fi

python3 - \
    "$ACTION_CLASS" "$OUTPUT_FILE" "$COMPOSE_DIR" "$PG_CONTAINER" \
    "$pg_data_source" "$fst_storage_root" "$filesystem_mount" "$filesystem_source" "$filesystem_type" \
    "$filesystem_total_bytes" "$filesystem_used_bytes" "$filesystem_free_bytes" "$filesystem_used_percent" \
    "$database_bytes" "$wal_directory_bytes" "$active_scrape_id" "$published_scrape_id" \
    "$public_reads_frozen" "$public_reads_frozen_reason" "$active_vacuums" "$active_index_builds" \
    "$active_rewrites" "$ungranted_locks" "$TRANSIENT_BUILD_BYTES" "$REQUIRED_SCRATCH_BYTES" \
    "$EXPECTED_RECLAIM_BYTES" \
    "$ESTIMATED_FULL_SCRAPE_GROWTH_BYTES" "$EXPECTED_FULL_SCRAPES_PER_DAY" "$MINIMUM_HEADROOM_DAYS" \
    "$MINIMUM_HEADROOM_BYTES_OVERRIDE" <<'PY'
import json
import sys
from datetime import datetime, timezone
from pathlib import Path

(
    action_class,
    output_file,
    compose_dir,
    pg_container,
    pg_data_source,
    fst_storage_root,
    filesystem_mount,
    filesystem_source,
    filesystem_type,
    filesystem_total_bytes,
    filesystem_used_bytes,
    filesystem_free_bytes,
    filesystem_used_percent,
    database_bytes,
    wal_directory_bytes,
    active_scrape_id,
    published_scrape_id,
    public_reads_frozen,
    public_reads_frozen_reason,
    active_vacuums,
    active_index_builds,
    active_rewrites,
    ungranted_locks,
    transient_build_bytes,
    required_scratch_bytes,
    expected_reclaim_bytes,
    estimated_growth_bytes,
    expected_scrapes_per_day,
    minimum_headroom_days,
    minimum_headroom_bytes_override,
) = sys.argv[1:]

numeric = {
    "filesystemTotalBytes": int(filesystem_total_bytes),
    "filesystemUsedBytes": int(filesystem_used_bytes),
    "filesystemFreeBytes": int(filesystem_free_bytes),
    "filesystemUsedPercent": int(filesystem_used_percent),
    "databaseBytes": int(database_bytes),
    "walDirectoryBytes": int(wal_directory_bytes),
    "activeScrapeId": int(active_scrape_id),
    "publishedScrapeId": int(published_scrape_id),
    "activeVacuums": int(active_vacuums),
    "activeIndexBuilds": int(active_index_builds),
    "activeRewrites": int(active_rewrites),
    "ungrantedLocks": int(ungranted_locks),
    "transientBuildBytes": int(transient_build_bytes),
    "requiredScratchBytes": int(required_scratch_bytes),
    "expectedReclaimBytes": int(expected_reclaim_bytes),
    "estimatedFullScrapeGrowthBytes": int(estimated_growth_bytes),
    "expectedFullScrapesPerDay": int(expected_scrapes_per_day),
    "minimumHeadroomDays": int(minimum_headroom_days),
    "minimumHeadroomBytesOverride": int(minimum_headroom_bytes_override),
}

daily_growth = numeric["estimatedFullScrapeGrowthBytes"] * numeric["expectedFullScrapesPerDay"]
minimum_headroom_bytes = (
    numeric["minimumHeadroomBytesOverride"]
    or daily_growth * numeric["minimumHeadroomDays"]
)
free_bytes = numeric["filesystemFreeBytes"]
headroom_days = (free_bytes / daily_growth) if daily_growth else None
emergency_required_bytes = numeric["estimatedFullScrapeGrowthBytes"] + numeric["transientBuildBytes"]
optional_required_bytes = minimum_headroom_bytes + numeric["transientBuildBytes"]
rewrite_required_bytes = optional_required_bytes + numeric["requiredScratchBytes"]
projected_post_reclaim_free_bytes = free_bytes + numeric["expectedReclaimBytes"]
maintenance_conflict = (
    numeric["activeVacuums"] > 0
    or numeric["activeIndexBuilds"] > 0
    or numeric["activeRewrites"] > 0
    or numeric["ungrantedLocks"] > 0
)

capacity_alert = free_bytes < minimum_headroom_bytes
scrape_allowed = free_bytes >= emergency_required_bytes
optional_build_allowed = free_bytes >= optional_required_bytes
reclaim_restores_emergency = (
    numeric["expectedReclaimBytes"] > 0
    and projected_post_reclaim_free_bytes >= emergency_required_bytes
)
reclaim_allowed = (scrape_allowed or reclaim_restores_emergency) and not maintenance_conflict
rewrite_allowed = free_bytes >= rewrite_required_bytes and not maintenance_conflict
reasons = []
exit_code = 0

if capacity_alert:
    threshold_description = (
        "explicit minimum-headroom threshold"
        if numeric["minimumHeadroomBytesOverride"]
        else f"{numeric['minimumHeadroomDays']}-day alert threshold"
    )
    reasons.append(
        f"free space is below the {threshold_description} "
        f"({free_bytes} < {minimum_headroom_bytes} bytes)"
    )

if action_class in {"scrape", "post-process"} and not scrape_allowed:
    exit_code = 4
    reasons.append(
        f"{action_class} requires at least one estimated full-scrape growth window "
        f"({emergency_required_bytes} bytes)"
    )
elif action_class == "optional-build" and not optional_build_allowed:
    exit_code = 2
    reasons.append(f"optional build deferred; requires {optional_required_bytes} bytes")
elif action_class == "reclaim" and not reclaim_allowed:
    exit_code = 3
    if not scrape_allowed:
        reasons.append(
            f"reclaim requires at least one estimated full-scrape growth window "
            f"({emergency_required_bytes} bytes), or an explicit reclaim estimate "
            f"that restores it ({projected_post_reclaim_free_bytes} projected bytes)"
        )
    if maintenance_conflict:
        reasons.append("maintenance conflict detected from vacuum/index/rewrite/lock activity")
elif action_class == "reclaim" and not scrape_allowed and reclaim_restores_emergency:
    reasons.append(
        f"explicit reclaim estimate restores the emergency window "
        f"({free_bytes} + {numeric['expectedReclaimBytes']} = "
        f"{projected_post_reclaim_free_bytes} bytes)"
    )
elif action_class in {"maintenance", "rewrite"} and not rewrite_allowed:
    exit_code = 3
    if free_bytes < rewrite_required_bytes:
        reasons.append(f"{action_class} requires {rewrite_required_bytes} free bytes including scratch")
    if maintenance_conflict:
        reasons.append("maintenance conflict detected from vacuum/index/rewrite/lock activity")

decision = "accepted"
if exit_code:
    decision = "blocked"
elif capacity_alert:
    decision = "accepted_with_capacity_alert"

report = {
    "sampledAtUtc": datetime.now(timezone.utc).isoformat(),
    "actionClass": action_class,
    "decision": decision,
    "reasons": reasons,
    "productionComposeDirectory": compose_dir,
    "postgresContainer": pg_container,
    "storage": {
        "requiredRoot": fst_storage_root,
        "postgresDataSource": pg_data_source,
        "filesystemMount": filesystem_mount,
        "filesystemSource": filesystem_source,
        "filesystemType": filesystem_type,
        **{key: numeric[key] for key in (
            "filesystemTotalBytes",
            "filesystemUsedBytes",
            "filesystemFreeBytes",
            "filesystemUsedPercent",
            "databaseBytes",
            "walDirectoryBytes",
        )},
    },
    "capacity": {
        **{key: numeric[key] for key in (
            "estimatedFullScrapeGrowthBytes",
            "expectedFullScrapesPerDay",
            "minimumHeadroomDays",
            "minimumHeadroomBytesOverride",
            "transientBuildBytes",
            "requiredScratchBytes",
            "expectedReclaimBytes",
        )},
        "dailyGrowthBytes": daily_growth,
        "minimumHeadroomBytes": minimum_headroom_bytes,
        "projectedHeadroomDays": round(headroom_days, 2) if headroom_days is not None else None,
        "capacityAlert": capacity_alert,
        "scrapeAllowed": scrape_allowed,
        "optionalBuildAllowed": optional_build_allowed,
        "reclaimAllowed": reclaim_allowed,
        "reclaimRestoresEmergency": reclaim_restores_emergency,
        "projectedPostReclaimFreeBytes": projected_post_reclaim_free_bytes,
        "rewriteAllowed": rewrite_allowed,
    },
    "databaseState": {
        **{key: numeric[key] for key in (
            "activeScrapeId",
            "publishedScrapeId",
            "activeVacuums",
            "activeIndexBuilds",
            "activeRewrites",
            "ungrantedLocks",
        )},
        "publicReadsFrozen": public_reads_frozen == "t",
        "publicReadsFrozenReason": public_reads_frozen_reason,
        "maintenanceConflict": maintenance_conflict,
    },
}

rendered = json.dumps(report, indent=2)
if output_file:
    Path(output_file).write_text(rendered + "\n", encoding="utf-8")
print(rendered)
print(
    f"capacity-guard decision={decision} action={action_class} "
    f"freeBytes={free_bytes} headroomDays={report['capacity']['projectedHeadroomDays']}",
    file=sys.stderr,
)
raise SystemExit(exit_code)
PY
