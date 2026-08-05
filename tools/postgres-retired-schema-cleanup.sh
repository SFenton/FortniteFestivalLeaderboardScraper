#!/usr/bin/env bash
set -Eeuo pipefail

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd -P)"
REPO_ROOT="$(cd -- "$SCRIPT_DIR/.." && pwd -P)"
SQL_DIR="$SCRIPT_DIR/sql/postgres-retired-schema-cleanup"
ORCHESTRATOR="$SCRIPT_DIR/postgres-retired-schema-cleanup.sh"
HELPER="$SCRIPT_DIR/postgres-retired-schema-cleanup.py"
PROCESS_MATCHER="$SCRIPT_DIR/postgres-retired-schema-process-match.sh"
OBJECTS_FILE="$SQL_DIR/objects.tsv"
RETAINED_SPEC="$SQL_DIR/retained-data.tsv"
CATALOG_QUERY="$SQL_DIR/catalog-signature-query.sql"
CAPACITY_GUARD="$SCRIPT_DIR/postgres-capacity-guard.sh"
CAPACITY_ESTIMATED_FULL_SCRAPE_GROWTH_BYTES="60392999803"
CAPACITY_EXPECTED_FULL_SCRAPES_PER_DAY="2"
CAPACITY_MINIMUM_HEADROOM_DAYS="7"
CAPACITY_MINIMUM_HEADROOM_BYTES_OVERRIDE="0"
CAPACITY_TRANSIENT_BUILD_BYTES="0"
CAPACITY_REQUIRED_SCRATCH_BYTES="0"
CAPACITY_EXPECTED_RECLAIM_BYTES="0"

PRODUCTION_COMPOSE_DIR="/home/sfenton/Docker/FestivalServiceTracker"
PRODUCTION_FST_STORAGE_ROOT="/mnt/docker-storage"
PRODUCTION_FST_EVIDENCE_ROOT="/mnt/docker-storage/Docker/FestivalServiceTracker/fst-data/evidence"
FST_STORAGE_ROOT="${FST_STORAGE_ROOT:-/mnt/docker-storage}"
FST_EVIDENCE_ROOT="${FST_EVIDENCE_ROOT:-$PRODUCTION_FST_EVIDENCE_ROOT}"
ROLLBACK_EVIDENCE_ROOT="${ROLLBACK_EVIDENCE_ROOT:-$FST_EVIDENCE_ROOT/branch-cleanup-20260803}"
COMPOSE_DIR="${COMPOSE_DIR:-$PRODUCTION_COMPOSE_DIR}"
PG_CONTAINER="${PG_CONTAINER:-fst-postgres}"
PG_USER="${PG_USER:-fst}"
PG_DB="${PG_DB:-fstservice}"
PG_SOCKET_DIR="/var/run/postgresql"
PG_PORT="5432"
PG_CONTAINER_EXPLICIT=false
PG_USER_EXPLICIT=false
PG_DB_EXPLICIT=false
EXPECTED_OWNER="$PG_USER"
FINGERPRINT_SPEC="${FINGERPRINT_SPEC:-$SQL_DIR/public-fingerprints.tsv}"

MODE="check"
MODE_EXPLICIT=false
OUTPUT_DIR=""
EXPECTED_MANIFEST_SHA256=""
PARITY_EVIDENCE=""
FIXTURE_DIR=""
OUTPUT_READY=false
CURRENT_STAGE="argument validation"
DROP_PROCESS_ACTIVE=false
DROP_CLEANUP_RUNNING=false
DROP_LOCAL_PID=""
DROP_LOCAL_START_TICKS=""
DROP_LOCAL_CMD_SHA256=""
DROP_LOCAL_WAIT_COMPLETED=false
DROP_CONNECT_RELEASED=false
DROP_SQL_RELEASED=false
DROP_SECOND_GATE_SHA256=""
DROP_SQL_STREAMER_PID=""
DROP_SQL_STREAMER_START_TICKS=""
DROP_SQL_STREAMER_CMD_SHA256=""
DROP_SQL_STREAMER_WAIT_COMPLETED=false
DROP_IMMUTABLE_BUNDLE_SHA256=""
APPROVED_FSTSERVICE_IMAGE_ID=""
APPROVED_FSTSERVICE_CONTAINER_ID=""
APPROVED_FSTSERVICE_IMAGE_REFERENCE_SHA256=""
APPROVED_FSTSERVICE_NETWORKS_JSON=""
APPROVED_POSTGRES_CONTAINER_ID=""
APPROVED_POSTGRES_SYSTEM_IDENTIFIER=""
APPROVED_DATABASE_HOST=""
APPROVED_DATABASE_PORT=""
APPROVED_DATABASE_NAME=""
APPROVED_DATABASE_USER=""
STARTUP_CHECK_CONTAINER_NAME=""
STARTUP_CHECK_CONTAINER_ACTIVE=false
DROP_SQL_RELEASED=false
DROP_LOCAL_START_TICKS=""
DROP_LOCAL_CMD_SHA256=""
DROP_LOCAL_WAIT_COMPLETED=false
DROP_CONTAINER_PSQL_PID=""
DROP_BACKEND_PID=""
DROP_PROCESS_IDENTITY_CONFIRMED=false
DROP_RUN_STATUS=0
DROP_EXECUTION_STARTED=false
TRAP_RECONCILIATION_RUNNING=false
SCRATCH_DB_ACTIVE=false
SCRATCH_DB_NAME=""
DROP_APP_NAME=""
DROP_CONTROL_FILE=""
DROP_PROCESS_IDENTITY_CONFIRMED=false

usage() {
    cat <<'EOF'
Usage: tools/postgres-retired-schema-cleanup.sh [--check|--execute] --output DIR [options]

Prepares or executes the exact parity-gated cleanup of 61 retired PostgreSQL
relations. Check mode is the default and is read-only. Execute is blocked until
scrape 1278 is completed, published, unfrozen, explicitly accepted by a parity
attestation, and the freshly regenerated manifest matches the supplied SHA-256.

Options:
  --check                         Inventory and prepare only (default)
  --execute                       Revalidate and execute exact drops
  --expected-manifest-sha256 SHA  Required with --execute
  --output DIR                    New evidence directory on the FST drive
  --parity-evidence FILE          Accepted scrape-1278 attestation JSON
  --fingerprint-spec FILE         Public/API fingerprint request TSV
  --compose-dir DIR               Must be the production compose directory
  --rollback-evidence-root DIR    Existing branch-cleanup evidence root
  --pg-container ID               Optional; must equal resolved production ID
  --pg-user USER                  Optional; must equal production compose user
  --pg-db DATABASE                Optional; must equal production compose DB
  -h, --help                      Show help

Test-only:
  --fixture-dir DIR               Requires FST_RETIRED_SCHEMA_TEST_MODE=1;
                                  never contacts Docker, PostgreSQL, or HTTP

The script never restores automatically. A failed execute retains exact
per-family rollback DDL and writes explicit operator-only recovery steps.
EOF
}

require_option_value() {
    if (( $# < 2 )) || [[ -z "$2" || "$2" == --* ]]; then
        printf 'ERROR: %s requires a value\n' "$1" >&2
        exit 64
    fi
}

while [[ $# -gt 0 ]]; do
    case "$1" in
        --check)
            if $MODE_EXPLICIT && [[ "$MODE" != "check" ]]; then
                printf 'ERROR: --check and --execute are mutually exclusive\n' >&2
                exit 64
            fi
            MODE="check"
            MODE_EXPLICIT=true
            shift
            ;;
        --execute)
            if $MODE_EXPLICIT && [[ "$MODE" != "execute" ]]; then
                printf 'ERROR: --check and --execute are mutually exclusive\n' >&2
                exit 64
            fi
            MODE="execute"
            MODE_EXPLICIT=true
            shift
            ;;
        --expected-manifest-sha256)
            require_option_value "$@"
            EXPECTED_MANIFEST_SHA256="$2"
            shift 2
            ;;
        --output)
            require_option_value "$@"
            OUTPUT_DIR="$2"
            shift 2
            ;;
        --parity-evidence)
            require_option_value "$@"
            PARITY_EVIDENCE="$2"
            shift 2
            ;;
        --fingerprint-spec)
            require_option_value "$@"
            FINGERPRINT_SPEC="$2"
            shift 2
            ;;
        --compose-dir)
            require_option_value "$@"
            COMPOSE_DIR="$2"
            shift 2
            ;;
        --rollback-evidence-root)
            require_option_value "$@"
            ROLLBACK_EVIDENCE_ROOT="$2"
            shift 2
            ;;
        --pg-container)
            require_option_value "$@"
            PG_CONTAINER="$2"
            PG_CONTAINER_EXPLICIT=true
            shift 2
            ;;
        --pg-user)
            require_option_value "$@"
            PG_USER="$2"
            EXPECTED_OWNER="$2"
            PG_USER_EXPLICIT=true
            shift 2
            ;;
        --pg-db)
            require_option_value "$@"
            PG_DB="$2"
            PG_DB_EXPLICIT=true
            shift 2
            ;;
        --fixture-dir)
            require_option_value "$@"
            FIXTURE_DIR="$2"
            shift 2
            ;;
        -h|--help)
            usage
            exit 0
            ;;
        *)
            printf 'ERROR: unknown option: %s\n\n' "$1" >&2
            usage >&2
            exit 64
            ;;
    esac
done

if [[ -z "$OUTPUT_DIR" ]]; then
    printf 'ERROR: --output is required\n' >&2
    exit 64
fi
if [[ "$MODE" == "check" && -n "$EXPECTED_MANIFEST_SHA256" ]]; then
    printf 'ERROR: --expected-manifest-sha256 is valid only with --execute\n' >&2
    exit 64
fi
if [[ "$MODE" == "execute" ]]; then
    if [[ ! "$EXPECTED_MANIFEST_SHA256" =~ ^[0-9a-f]{64}$ ]]; then
        printf 'ERROR: --execute requires a lowercase 64-character manifest SHA-256\n' >&2
        exit 64
    fi

    for libpq_override in PGHOST PGHOSTADDR PGPORT PGSERVICE PGSERVICEFILE; do
        if [[ -n "${!libpq_override:-}" ]]; then
            printf 'ERROR: %s must be unset; local Unix socket is mandatory\n' \
                "$libpq_override" >&2
            exit 64
        fi
        unset "$libpq_override"
    done
    if [[ -z "$PARITY_EVIDENCE" ]]; then
        printf 'ERROR: --execute requires --parity-evidence\n' >&2
        exit 64
    fi
fi

require_command() {
    if ! command -v "$1" >/dev/null 2>&1; then
        printf 'ERROR: required command not found: %s\n' "$1" >&2
        exit 1
    fi
}

for command in awk cp find grep python3 realpath sed sha256sum sort sync xargs; do
    require_command "$command"
done

TEST_MODE=false
if [[ -n "$FIXTURE_DIR" ]]; then
    if [[ "${FST_RETIRED_SCHEMA_TEST_MODE:-0}" != "1" ]]; then
        printf 'ERROR: --fixture-dir requires FST_RETIRED_SCHEMA_TEST_MODE=1\n' >&2
        exit 64
    fi
    TEST_MODE=true
else
    for command in curl df docker findmnt rg timeout; do
        require_command "$command"
    done
fi

realpath_under() {
    local child="$1"
    local root="$2"
    case "$child/" in
        "$root/"*) return 0 ;;
        *) return 1 ;;
    esac
}

OUTPUT_DIR="$(realpath -m "$OUTPUT_DIR")"
REPO_ROOT_REAL="$(realpath -m "$REPO_ROOT")"
FST_STORAGE_ROOT="$(realpath -m "$FST_STORAGE_ROOT")"
FST_EVIDENCE_ROOT="$(realpath -m "$FST_EVIDENCE_ROOT")"
ROLLBACK_EVIDENCE_ROOT="$(realpath -m "$ROLLBACK_EVIDENCE_ROOT")"
COMPOSE_DIR="$(realpath -m "$COMPOSE_DIR")"
FINGERPRINT_SPEC="$(realpath -m "$FINGERPRINT_SPEC")"

if ! realpath_under "$FINGERPRINT_SPEC" "$REPO_ROOT_REAL" \
    && ! realpath_under "$FINGERPRINT_SPEC" "$FST_EVIDENCE_ROOT"; then
    printf 'ERROR: fingerprint specification must be in the repository or FST evidence root\n' >&2
    exit 64
fi

if $TEST_MODE; then
    FIXTURE_DIR="$(realpath -m "$FIXTURE_DIR")"
    realpath_under "$OUTPUT_DIR" "$REPO_ROOT_REAL" || {
        printf 'ERROR: fixture output must remain in the repository\n' >&2
        exit 64
    }
    realpath_under "$FIXTURE_DIR" "$REPO_ROOT_REAL" || {
        printf 'ERROR: fixture input must remain in the repository\n' >&2
        exit 64
    }
    [[ -d "$FIXTURE_DIR/pre" ]] || {
        printf 'ERROR: fixture pre-capture directory is missing\n' >&2
        exit 1
    }
else
    if [[ "$FST_STORAGE_ROOT" != "$PRODUCTION_FST_STORAGE_ROOT" ]]; then
        printf 'ERROR: FST storage root must be %s\n' \
            "$PRODUCTION_FST_STORAGE_ROOT" >&2
        exit 64
    fi
    if [[ "$FST_EVIDENCE_ROOT" != "$PRODUCTION_FST_EVIDENCE_ROOT" ]]; then
        printf 'ERROR: FST evidence root must be %s\n' \
            "$PRODUCTION_FST_EVIDENCE_ROOT" >&2
        exit 64
    fi
    [[ -d "$FST_STORAGE_ROOT" && -d "$FST_EVIDENCE_ROOT" ]] || {
        printf 'ERROR: FST storage/evidence root is missing\n' >&2
        exit 1
    }
    realpath_under "$FST_EVIDENCE_ROOT" "$FST_STORAGE_ROOT" || {
        printf 'ERROR: FST evidence root must remain on the 4 TB FST drive\n' >&2
        exit 64
    }
    if [[ "$COMPOSE_DIR" != "$PRODUCTION_COMPOSE_DIR" ]]; then
        printf 'ERROR: compose directory must be %s\n' "$PRODUCTION_COMPOSE_DIR" >&2
        exit 64
    fi
    [[ -d "$COMPOSE_DIR" ]] || {
        printf 'ERROR: production compose directory is missing: %s\n' "$COMPOSE_DIR" >&2
        exit 1
    }
    realpath_under "$OUTPUT_DIR" "$FST_EVIDENCE_ROOT" || {
        printf 'ERROR: output must remain under FST evidence root %s\n' \
            "$FST_EVIDENCE_ROOT" >&2
        exit 64
    }
    realpath_under "$ROLLBACK_EVIDENCE_ROOT" "$FST_EVIDENCE_ROOT" || {
        printf 'ERROR: rollback evidence must remain on the FST evidence root\n' >&2
        exit 64
    }
    if [[ -n "$PARITY_EVIDENCE" ]]; then
        PARITY_EVIDENCE="$(realpath -m "$PARITY_EVIDENCE")"
        realpath_under "$PARITY_EVIDENCE" "$FST_EVIDENCE_ROOT" || {
            printf 'ERROR: parity evidence must remain on the FST evidence root\n' >&2
            exit 64
        }
    fi
fi

for file in \
    "$HELPER" \
    "$PROCESS_MATCHER" \
    "$CAPACITY_GUARD" \
    "$OBJECTS_FILE" \
    "$RETAINED_SPEC" \
    "$CATALOG_QUERY" \
    "$FINGERPRINT_SPEC"; do
    [[ -f "$file" ]] || {
        printf 'ERROR: required package file is missing: %s\n' "$file" >&2
        exit 1
    }
done
if [[ -e "$OUTPUT_DIR" ]] \
    && [[ -n "$(find "$OUTPUT_DIR" -mindepth 1 -maxdepth 1 -print -quit 2>/dev/null)" ]]; then
    printf 'ERROR: output directory must be new or empty: %s\n' "$OUTPUT_DIR" >&2
    exit 1
fi

CAPTURE_DIR="$OUTPUT_DIR/capture"
PACKAGE_DIR="$OUTPUT_DIR/package"
ROLLBACK_DIR="$PACKAGE_DIR/rollback"
ROLLBACK_EXECUTABLE_DIR="$PACKAGE_DIR/rollback-executable"
ROLLBACK_CANONICAL_DIR="$PACKAGE_DIR/rollback-canonical"
ROLLBACK_DATA_DIR="$PACKAGE_DIR/rollback-data"
RETAINED_DIR="$PACKAGE_DIR/retained-data"
RETAINED_RAW_DIR="$CAPTURE_DIR/retained-data"
CATALOG_DIR="$PACKAGE_DIR/catalog"
RETAINED_CAPTURE_SQL_DIR="$PACKAGE_DIR/retained-capture"
POST_DIR="$OUTPUT_DIR/post"
LOG_DIR="$OUTPUT_DIR/logs"
declare -a COMPOSE_FILE_PATHS=()
declare -a COMPOSE_FILE_ARGS=()
declare -A PROJECT_SERVICE_CONTAINER_IDS=()
COMPOSE_PROJECT_NAME=""
COMPOSE_CONFIG_FILES_LABEL=""
RESOLVED_PG_CONTAINER_ID=""
COMPOSE_PROJECT_INITIALIZED=false
mkdir -p \
    "$CAPTURE_DIR" \
    "$PACKAGE_DIR" \
    "$ROLLBACK_DIR" \
    "$ROLLBACK_EXECUTABLE_DIR" \
    "$ROLLBACK_CANONICAL_DIR" \
    "$ROLLBACK_DATA_DIR" \
    "$RETAINED_DIR" \
    "$RETAINED_RAW_DIR" \
    "$CATALOG_DIR" \
    "$RETAINED_CAPTURE_SQL_DIR" \
    "$POST_DIR" \
    "$LOG_DIR"
OUTPUT_READY=true

write_rollback_instructions() {
    local destination="$OUTPUT_DIR/ROLLBACK-INSTRUCTIONS.txt"
    local committed_file="$OUTPUT_DIR/committed-families.txt"
    : > "$committed_file"
    if [[ -f "$LOG_DIR/drop.log" ]] \
        && grep -q 'FST_ALL_COMMITTED' "$LOG_DIR/drop.log"; then
        printf '%s\n' \
            logical-shadow \
            score-observations \
            band-song-projection \
            aggregate-ranking-deltas \
            > "$committed_file"
    fi

    {
        printf 'Retired schema cleanup rollback is operator-invoked only.\n'
        printf 'The orchestrator never restores automatically.\n\n'
        printf 'Current stage: %s\n' "$CURRENT_STAGE"
        printf 'Manifest: %s\n' "${EXPECTED_MANIFEST_SHA256:-not supplied}"
        printf 'Atomic commit state (confirm against catalog evidence):\n'
        if [[ -s "$committed_file" ]]; then
            sed 's/^/  - /' "$committed_file"
        else
            printf '  - none recorded\n'
        fi
        printf '\nBefore any restore:\n'
        printf '  1. Keep fstworker stopped and the worker ledger offline.\n'
        printf '  2. Keep public reads on published scrape 1278 and unfrozen.\n'
        printf '  3. Reinventory exact absent/present objects and dependencies.\n'
        printf '  4. Re-parse original pg_dump boundaries and verify canonical/data SHA-256 values.\n'
        printf '  5. If all 61 objects are absent, restore rollback-all.sql atomically.\n'
        printf '  6. Repeat startup, absence/presence, public fingerprint, publication, and health checks.\n\n'
        printf 'Per-family rollback DDL:\n'
        for family in logical-shadow score-observations band-song-projection aggregate-ranking-deltas; do
            printf '  - %s: %s/%s.sql\n' "$family" "$ROLLBACK_DIR" "$family"
        done
        printf '\nExample explicit full restore command after catalog review:\n'
        printf '  timeout --signal=TERM --kill-after=30s 7m docker exec -i -e PGCONNECT_TIMEOUT=10 -e PGOPTIONS="-c row_security=off" %q env -u PGHOST -u PGHOSTADDR -u PGPORT -u PGSERVICE -u PGSERVICEFILE psql -X --single-transaction -v ON_ERROR_STOP=1 -h %q -p %q -U %q -d %q \\\n' \
            "$PG_CONTAINER" "$PG_SOCKET_DIR" "$PG_PORT" "$PG_USER" "$PG_DB"
        printf '    < %q/rollback-all.sql\n' "$ROLLBACK_EXECUTABLE_DIR"
        printf '\nThe drop is one transaction: either all families commit or none do.\n'
        printf 'Do not apply rollback-all.sql unless all 61 objects are absent.\n'
    } > "$destination"
}

on_error() {
    local status=$?
    local final_status
    local safety_status=0
    set +e
    trap - ERR
    trap '' INT TERM HUP
    if declare -F trap_cleanup_and_reconcile >/dev/null; then
        trap_cleanup_and_reconcile ERR "$status" || safety_status=$?
    fi
    if $OUTPUT_READY; then
        write_rollback_instructions
        {
            printf 'status=failed\n'
            printf 'exit_code=%s\n' "$status"
            printf 'safety_status=%s\n' "$safety_status"
            printf 'stage=%s\n' "$CURRENT_STAGE"
        } > "$OUTPUT_DIR/FAILED.txt"
    fi
    (( safety_status == 0 )) || printf \
        'ERROR: cleanup/reconciliation safety handling failed (%s)\n' \
        "$safety_status" >&2
    final_status="$status"
    (( safety_status == 0 )) || final_status=3
    printf 'ERROR: retired schema cleanup stopped during %s\n' "$CURRENT_STAGE" >&2
    exit "$final_status"
}

on_signal() {
    local signal="$1"
    local status="$2"
    local safety_status=0
    set +e
    trap - ERR
    trap '' INT TERM HUP
    if declare -F trap_cleanup_and_reconcile >/dev/null; then
        trap_cleanup_and_reconcile "$signal" "$status" \
            || safety_status=$?
    fi
    (( safety_status == 0 )) || printf \
        'ERROR: signal cleanup/reconciliation failed (%s)\n' \
        "$safety_status" >&2
    if $OUTPUT_READY; then
        write_rollback_instructions
        {
            printf 'status=signal\n'
            printf 'signal=%s\n' "$signal"
            printf 'exit_code=%s\n' "$status"
            printf 'safety_status=%s\n' "$safety_status"
            printf 'stage=%s\n' "$CURRENT_STAGE"
        } > "$OUTPUT_DIR/FAILED.txt"
    fi
    printf 'ERROR: retired schema cleanup interrupted by %s during %s\n' \
        "$signal" "$CURRENT_STAGE" >&2
    exit "$status"
}
trap on_error ERR
trap 'on_signal INT 130' INT
trap 'on_signal TERM 143' TERM
trap 'on_signal HUP 129' HUP

cp "$OBJECTS_FILE" "$PACKAGE_DIR/objects.tsv"
cp "$RETAINED_SPEC" "$PACKAGE_DIR/retained-data.tsv"
cp "$CATALOG_QUERY" "$CATALOG_DIR/query.sql"
cp "$FINGERPRINT_SPEC" "$PACKAGE_DIR/public-fingerprints.tsv"
python3 - "$PACKAGE_DIR/public-fingerprints.tsv" <<'PY'
import csv
import re
import sys
from pathlib import Path
from urllib.parse import parse_qsl, urlparse

path = Path(sys.argv[1])
with path.open(newline="", encoding="utf-8") as handle:
    rows = list(csv.DictReader(handle, delimiter="\t"))
if not rows:
    raise SystemExit("fingerprint specification is empty")
names = [row.get("name", "") for row in rows]
if len(names) != len(set(names)):
    raise SystemExit("fingerprint names must be unique")
health_names = ["readyz", "web-shell", "service-info"]
gate_names = [
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
if names != health_names + gate_names:
    raise SystemExit(
        "fingerprint specification must contain the exact ordered health "
        "and 13-surface public parity suite"
    )
for row in rows:
    if not re.fullmatch(r"[a-z0-9][a-z0-9-]*", row.get("name", "")):
        raise SystemExit("fingerprint names must be lowercase kebab-case")
    if row.get("format") not in {
        "json",
        "text",
        "leaderboard-semantic",
        "player-export",
        "player-export-solo",
    }:
        raise SystemExit(f"invalid fingerprint format for {row.get('name')}")
    if not re.fullmatch(r"[1-5][0-9]{2}", row.get("expected_status", "")):
        raise SystemExit(f"invalid expected status for {row.get('name')}")
    if row.get("gate") not in {"true", "false"}:
        raise SystemExit(f"invalid gate value for {row.get('name')}")
    if not re.fullmatch(r"https?://[^\t,\r\n]+", row.get("url", "")):
        raise SystemExit(f"invalid fingerprint URL for {row.get('name')}")
    parsed = urlparse(row["url"])
    if (
        parsed.scheme != "http"
        or parsed.hostname not in {"127.0.0.1", "localhost"}
        or parsed.username is not None
        or parsed.password is not None
        or parsed.fragment
    ):
        raise SystemExit(
            f"fingerprint URL must be an unauthenticated local HTTP route: "
            f"{row.get('name')}"
        )
    sensitive_query_names = {
        "apikey", "api_key", "authorization", "password", "secret", "token"
    }
    if any(
        key.casefold() in sensitive_query_names
        for key, _value in parse_qsl(parsed.query, keep_blank_values=True)
    ):
        raise SystemExit(
            f"fingerprint URL contains a sensitive query key: "
            f"{row.get('name')}"
        )
for row in rows:
    expected_gate = "false" if row["name"] in health_names else "true"
    if row["gate"] != expected_gate:
        raise SystemExit(f"invalid gate assignment for {row['name']}")
if rows[0]["format"] != "text" or rows[1]["format"] != "text":
    raise SystemExit("readyz and web-shell must use text fingerprints")
if rows[2]["format"] != "json":
    raise SystemExit("service-info must use a JSON fingerprint")
by_name = {row["name"]: row for row in rows}
for name in {
    "player-ranking",
    "rank-history",
    "player-export",
    "player-export-solo",
}:
    if "{account_id}" not in by_name[name]["url"]:
        raise SystemExit(f"{name} must use the derived account ID")
for name in {
    "band-ranking",
    "band-history",
    "band-songs",
    "band-song-rows",
}:
    if "{team_key}" not in by_name[name]["url"]:
        raise SystemExit(f"{name} must use the derived band team key")
if by_name["leaderboard-semantic"]["format"] != "leaderboard-semantic":
    raise SystemExit("leaderboard-semantic has the wrong normalizer")
if by_name["player-export"]["format"] != "player-export":
    raise SystemExit("player-export has the wrong normalizer")
if by_name["player-export-solo"]["format"] != "player-export-solo":
    raise SystemExit("player-export-solo has the wrong normalizer")
if by_name["leaderboard-semantic"]["url"] != by_name["leaderboard"]["url"]:
    raise SystemExit("leaderboard fingerprints must share one exact request")
if by_name["player-export-solo"]["url"] != by_name["player-export"]["url"]:
    raise SystemExit("player export fingerprints must share one exact request")
required_route_fragments = {
    "leaderboard": "/api/leaderboard/",
    "rankings": "/api/rankings/Solo_Vocals?",
    "player-ranking": "/api/rankings/Solo_Vocals/{account_id}?",
    "rank-history": "/api/rankings/Solo_Vocals/{account_id}/history?",
    "composite-rankings": "/api/rankings/composite?",
    "band-rankings": "/api/rankings/bands/Band_Duets?",
    "band-ranking": "/api/rankings/bands/Band_Duets/{team_key}?",
    "band-history": "/api/rankings/bands/Band_Duets/{team_key}/history?",
    "band-songs": "/api/rankings/bands/Band_Duets/{team_key}/songs?",
    "band-song-rows": "/api/rankings/bands/Band_Duets/{team_key}/song-rows",
    "player-export": "/api/player/{account_id}/export",
}
for name, fragment in required_route_fragments.items():
    if fragment not in by_name[name]["url"]:
        raise SystemExit(f"{name} does not target its required public surface")
PY
FINGERPRINT_SPEC_SHA256="$(sha256sum "$PACKAGE_DIR/public-fingerprints.tsv" | awk '{print $1}')"
python3 "$HELPER" render-expected-sql \
    --objects "$PACKAGE_DIR/objects.tsv" \
    > "$PACKAGE_DIR/expected-objects.sql"
{
    printf 'name,sha256\n'
    for source_file in \
        "$ORCHESTRATOR" \
        "$HELPER" \
        "$PROCESS_MATCHER" \
        "$CAPACITY_GUARD" \
        "$SQL_DIR"/capture-*.sql \
        "$SQL_DIR/catalog-signature-query.sql" \
        "$SQL_DIR/objects.tsv" \
        "$SQL_DIR/retained-data.tsv" \
        "$SQL_DIR/public-fingerprints.tsv"; do
        printf '%s,%s\n' \
            "${source_file#"$REPO_ROOT/"}" \
            "$(sha256sum "$source_file" | awk '{print $1}')"
    done
} > "$CAPTURE_DIR/tooling-hashes.csv"

PACKAGE_PARITY_EVIDENCE=""
if [[ -n "$PARITY_EVIDENCE" ]]; then
    [[ -f "$PARITY_EVIDENCE" ]] || {
        printf 'ERROR: parity evidence is missing: %s\n' "$PARITY_EVIDENCE" >&2
        exit 1
    }
    python3 - "$PARITY_EVIDENCE" <<'PY'
import json
import sys
from pathlib import Path

path = Path(sys.argv[1])
value = json.loads(path.read_text(encoding="utf-8"))
allowed = {
    "schemaVersion",
    "decision",
    "scrapeId",
    "published",
    "unfrozen",
    "exactPublicFingerprintParity",
    "fingerprintCount",
    "cleanupImageId",
    "fingerprintSpecSha256",
    "acceptedAtUtc",
    "evidenceRoot",
}
extra = sorted(set(value) - allowed)
if extra:
    raise SystemExit(
        "parity evidence contains unsupported fields: " + ", ".join(extra)
    )
PY
    cp "$PARITY_EVIDENCE" "$PACKAGE_DIR/parity-acceptance.json"
    PACKAGE_PARITY_EVIDENCE="$PACKAGE_DIR/parity-acceptance.json"
fi

compose_command() {
    (
        cd "$COMPOSE_DIR"
        docker compose "${COMPOSE_FILE_ARGS[@]}" "$@"
    )
}

initialize_production_compose_project() {
    local project_csv="$CAPTURE_DIR/production-compose-project-containers.csv"
    local compose_files_csv="$CAPTURE_DIR/production-compose-files.csv"
    local sanitized_json="$CAPTURE_DIR/production-compose.sanitized.json"
    local binds_tsv="$CAPTURE_DIR/production-compose-binds.tsv"
    local ids_output id inspect_row container_name service project working_dir
    local config_files_label first_config_files="" first_project=""
    local path resolved ordinal=0 service_ids target_values target_host target_port
    local target_database target_user target_service target_container_id
    local -A seen_files=()

    if ! ids_output="$(
        docker ps -a \
            --filter "label=com.docker.compose.project.working_dir=$COMPOSE_DIR" \
            --format '{{.ID}}'
    )"; then
        printf 'ERROR: failed to discover production compose containers\n' >&2
        return 2
    fi
    [[ -n "$ids_output" ]] || {
        printf 'ERROR: production compose project has no labeled containers\n' >&2
        return 2
    }
    printf 'container_id,container_name,service,project,working_dir,config_files_sha256\n' \
        > "$project_csv"
    while IFS= read -r id; do
        [[ -z "$id" ]] && continue
        if ! inspect_row="$(
            docker inspect --format \
                '{{.Id}}|{{.Name}}|{{index .Config.Labels "com.docker.compose.service"}}|{{index .Config.Labels "com.docker.compose.project"}}|{{index .Config.Labels "com.docker.compose.project.working_dir"}}|{{index .Config.Labels "com.docker.compose.project.config_files"}}' \
                "$id"
        )"; then
            printf 'ERROR: failed to inspect production compose labels\n' >&2
            return 2
        fi
        IFS='|' read -r id container_name service project working_dir \
            config_files_label <<< "$inspect_row"
        container_name="${container_name#/}"
        [[ "$working_dir" == "$COMPOSE_DIR" ]] || {
            printf 'ERROR: compose container working directory drift\n' >&2
            return 2
        }
        [[ -n "$service" && -n "$project" && -n "$config_files_label" ]] || {
            printf 'ERROR: incomplete production compose labels\n' >&2
            return 2
        }
        if [[ -z "$first_config_files" ]]; then
            first_config_files="$config_files_label"
            first_project="$project"
        elif [[ "$config_files_label" != "$first_config_files" \
            || "$project" != "$first_project" ]]; then
            printf 'ERROR: production compose containers disagree on project files\n' >&2
            return 2
        fi
        if [[ "$container_name$service$project$working_dir" \
            == *','* ]]; then
            printf 'ERROR: compose label fields contain unsupported commas\n' >&2
            return 2
        fi
        printf '%s,%s,%s,%s,%s,%s\n' \
            "$id" "$container_name" "$service" "$project" "$working_dir" \
            "$(printf '%s' "$config_files_label" | sha256sum | awk '{print $1}')" \
            >> "$project_csv"
    done <<< "$ids_output"
    COMPOSE_PROJECT_NAME="$first_project"
    COMPOSE_CONFIG_FILES_LABEL="$first_config_files"

    IFS=',' read -ra label_paths <<< "$first_config_files"
    printf 'ordinal,path,sha256,label_discovered\n' > "$compose_files_csv"
    for path in "${label_paths[@]}"; do
        path="${path#"${path%%[![:space:]]*}"}"
        path="${path%"${path##*[![:space:]]}"}"
        [[ "$path" == /* ]] || path="$COMPOSE_DIR/$path"
        resolved="$(realpath -e "$path")" || return 2
        realpath_under "$resolved" "$COMPOSE_DIR" || return 2
        [[ -f "$resolved" && -r "$resolved" ]] || return 2
        [[ -z "${seen_files[$resolved]:-}" ]] || {
            printf 'ERROR: duplicate compose file in ordered label list\n' >&2
            return 2
        }
        seen_files["$resolved"]=1
        COMPOSE_FILE_PATHS+=("$path")
        COMPOSE_FILE_ARGS+=(-f "$path")
        ((ordinal += 1))
        printf '%s,%s,%s,true\n' \
            "$ordinal" "$path" \
            "$(sha256sum "$resolved" | awk '{print $1}')" \
            >> "$compose_files_csv"
    done
    (( ordinal > 0 )) || return 2

    if ! compose_command config --format json 2>/dev/null \
        | python3 "$HELPER" sanitize-compose-config \
            --output "$sanitized_json" \
            --binds-output "$binds_tsv"; then
        printf 'ERROR: exact production compose render failed\n' >&2
        return 2
    fi

    for service in postgres fstservice festivalweb fstworker; do
        if ! service_ids="$(compose_command ps -a -q "$service")"; then
            printf 'ERROR: exact compose service lookup failed: %s\n' \
                "$service" >&2
            return 2
        fi
        if [[ "$(wc -w <<< "$service_ids")" != "1" ]]; then
            printf 'ERROR: compose service does not resolve to one container: %s\n' \
                "$service" >&2
            return 2
        fi
        PROJECT_SERVICE_CONTAINER_IDS["$service"]="$service_ids"
        grep -q "^$service_ids," "$project_csv" || {
            printf 'ERROR: resolved service container lacks project label attestation\n' >&2
            return 2
        }
    done

    target_values="$(
        python3 - "$sanitized_json" <<'PY'
import json
import sys
from pathlib import Path

target = json.loads(Path(sys.argv[1]).read_text(encoding="utf-8"))[
    "databaseTarget"
]
print("\t".join([
    target["service"],
    target["host"],
    target["port"],
    target["database"],
    target["user"],
]))
PY
    )"
    IFS=$'\t' read -r target_service target_host target_port \
        target_database target_user <<< "$target_values"
    [[ "$target_service" == "postgres" && "$target_host" == "postgres" ]] \
        || return 2
    [[ "$target_port" =~ ^[0-9]+$ ]] || return 2
    target_container_id="${PROJECT_SERVICE_CONTAINER_IDS[postgres]}"
    if $PG_CONTAINER_EXPLICIT && [[ "$PG_CONTAINER" != "$target_container_id" ]]; then
        printf 'ERROR: --pg-container must equal the resolved production ID\n' >&2
        return 64
    fi
    if $PG_DB_EXPLICIT && [[ "$PG_DB" != "$target_database" ]]; then
        printf 'ERROR: --pg-db differs from production compose target\n' >&2
        return 64
    fi
    if $PG_USER_EXPLICIT && [[ "$PG_USER" != "$target_user" ]]; then
        printf 'ERROR: --pg-user differs from production compose target\n' >&2
        return 64
    fi
    PG_CONTAINER="$target_container_id"
    RESOLVED_PG_CONTAINER_ID="$target_container_id"
    PG_DB="$target_database"
    PG_USER="$target_user"
    PG_PORT="$target_port"
    EXPECTED_OWNER="$target_user"
    printf 'compose_project,service,host,port,database,user,container_id\n' \
        > "$CAPTURE_DIR/production-database-target.csv"
    printf '%s,%s,%s,%s,%s,%s,%s\n' \
        "$COMPOSE_PROJECT_NAME" "$target_service" "$target_host" "$target_port" \
        "$PG_DB" "$PG_USER" "$PG_CONTAINER" \
        >> "$CAPTURE_DIR/production-database-target.csv"
    COMPOSE_PROJECT_INITIALIZED=true
}

reverify_production_compose_project() {
    local evidence_dir="$1"
    local ids_output id inspect_row project working_dir config_files_label
    local service service_ids path rendered_json binds_tsv
    mkdir -p "$evidence_dir" || return 3
    if ! ids_output="$(
        docker ps -a \
            --filter "label=com.docker.compose.project.working_dir=$COMPOSE_DIR" \
            --format '{{.ID}}'
    )"; then
        return 2
    fi
    [[ -n "$ids_output" ]] || return 2
    while IFS= read -r id; do
        [[ -z "$id" ]] && continue
        inspect_row="$(
            docker inspect --format \
                '{{index .Config.Labels "com.docker.compose.project"}}|{{index .Config.Labels "com.docker.compose.project.working_dir"}}|{{index .Config.Labels "com.docker.compose.project.config_files"}}' \
                "$id"
        )" || return 2
        IFS='|' read -r project working_dir config_files_label \
            <<< "$inspect_row"
        [[ "$project" == "$COMPOSE_PROJECT_NAME" \
            && "$working_dir" == "$COMPOSE_DIR" \
            && "$config_files_label" == "$COMPOSE_CONFIG_FILES_LABEL" ]] \
            || return 2
    done <<< "$ids_output"
    for path in "${COMPOSE_FILE_PATHS[@]}"; do
        grep -Fq ",$path,$(sha256sum "$path" | awk '{print $1}'),true" \
            "$CAPTURE_DIR/production-compose-files.csv" || return 2
    done
    for service in postgres fstservice festivalweb fstworker; do
        service_ids="$(compose_command ps -a -q "$service")" || return 2
        [[ "$service_ids" == "${PROJECT_SERVICE_CONTAINER_IDS[$service]}" ]] \
            || return 2
    done
    rendered_json="$evidence_dir/production-compose.sanitized.json"
    binds_tsv="$evidence_dir/production-compose-binds.tsv"
    if ! compose_command config --format json 2>/dev/null \
        | python3 "$HELPER" sanitize-compose-config \
            --output "$rendered_json" \
            --binds-output "$binds_tsv"; then
        return 2
    fi
    [[ "$(sha256sum "$rendered_json" | awk '{print $1}')" \
        == "$(sha256sum "$CAPTURE_DIR/production-compose.sanitized.json" \
            | awk '{print $1}')" ]] || return 2
    printf 'status,project,postgres_container_id\nok,%s,%s\n' \
        "$COMPOSE_PROJECT_NAME" "$RESOLVED_PG_CONTAINER_ID" \
        > "$evidence_dir/compose-reverification.csv" || return 3
    return 0
}

psql_stream_database() {
    local database="$1"
    timeout --signal=TERM --kill-after=30s 7m \
        docker exec -i \
        -e PGCONNECT_TIMEOUT=10 \
        -e PGOPTIONS="-c row_security=off" \
        "$PG_CONTAINER" \
        env -u PGHOST -u PGHOSTADDR -u PGPORT \
        -u PGSERVICE -u PGSERVICEFILE \
        psql -X -q -v ON_ERROR_STOP=1 \
        -h "$PG_SOCKET_DIR" -p "$PG_PORT" \
        -U "$PG_USER" -d "$database" -P pager=off
}

psql_stream() {
    psql_stream_database "$PG_DB"
}

run_expected_capture() {
    local sql_file="$1"
    local output_file="$2"
    {
        cat "$PACKAGE_DIR/expected-objects.sql"
        cat "$SQL_DIR/$sql_file"
    } | psql_stream > "$output_file" || return 3
}

run_plain_capture() {
    local sql_file="$1"
    local output_file="$2"
    psql_stream < "$SQL_DIR/$sql_file" > "$output_file" || return 3
}

capture_target_attestation() {
    local output_file="$1"
    local raw_file="$output_file.raw"
    run_plain_capture capture-target-attestation.sql "$raw_file" || return 3
    python3 - \
        "$CAPTURE_DIR/production-database-target.csv" \
        "$raw_file" \
        "$output_file" <<'PY' || return 3
import csv
import re
import sys
from pathlib import Path

def rows(path):
    with Path(path).open(newline="", encoding="utf-8") as handle:
        return list(csv.DictReader(handle))

configured = rows(sys.argv[1])
runtime = rows(sys.argv[2])
if len(configured) != 1 or len(runtime) != 1:
    raise SystemExit("production database target attestation is incomplete")
configured = configured[0]
runtime = runtime[0]
if runtime["database_name"] != configured["database"]:
    raise SystemExit("runtime database differs from production compose")
if runtime["database_user"] != configured["user"]:
    raise SystemExit("runtime database user differs from production compose")
if runtime["server_port"] != configured["port"]:
    raise SystemExit("runtime database port differs from production compose")
if runtime["server_address"] != "local-socket":
    raise SystemExit("runtime database connection is not a local Unix socket")
if runtime["in_recovery"] != "false":
    raise SystemExit("production target unexpectedly reports recovery mode")
if (
    runtime["role_superuser"] != "true"
    and runtime["role_bypass_rls"] != "true"
):
    raise SystemExit("production database role cannot bypass row security")
if not re.fullmatch(r"\d+", runtime["system_identifier"]):
    raise SystemExit("production PostgreSQL system identifier is invalid")
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
    "compose_project": configured["compose_project"],
    "service": configured["service"],
    "configured_host": configured["host"],
    "configured_port": configured["port"],
    "configured_database": configured["database"],
    "configured_user": configured["user"],
    "container_id": configured["container_id"],
    "runtime_address": runtime["server_address"],
    "runtime_port": runtime["server_port"],
    "runtime_database": runtime["database_name"],
    "runtime_user": runtime["database_user"],
    "in_recovery": runtime["in_recovery"],
    "system_identifier": runtime["system_identifier"],
    "role_superuser": runtime["role_superuser"],
    "role_bypass_rls": runtime["role_bypass_rls"],
}
with Path(sys.argv[3]).open("w", newline="", encoding="utf-8") as handle:
    writer = csv.DictWriter(handle, fieldnames=fields, lineterminator="\n")
    writer.writeheader()
    writer.writerow(row)
PY
    [[ -s "$output_file" ]] || return 3
    rm -f "$raw_file" || return 3
}

capture_container_config_attestation() {
    local output_file="$1"
    local ids_output
    local -a ids=()
    ids_output="$(docker ps -a -q)" || return 3
    while IFS= read -r container_id; do
        [[ -n "$container_id" ]] && ids+=("$container_id")
    done <<< "$ids_output"
    (( ${#ids[@]} > 0 )) || return 3
    docker inspect "${ids[@]}" \
        | python3 "$HELPER" attest-container-config \
            --compose "$CAPTURE_DIR/production-compose.sanitized.json" \
            --target-attestation \
                "${output_file%/*}/production-target-attestation.csv" \
            --project-containers \
                "$CAPTURE_DIR/production-compose-project-containers.csv" \
            --fingerprint-spec "$PACKAGE_DIR/public-fingerprints.tsv" \
            --output "$output_file" || return 3
    [[ -s "$output_file" ]] || return 3
}

run_catalog_signature_capture() {
    local output_file="$1"
    {
        cat "$PACKAGE_DIR/expected-objects.sql"
        printf '%s\n' \
            '\set ON_ERROR_STOP on' \
            'BEGIN TRANSACTION READ ONLY;' \
            "SET LOCAL lock_timeout = '2s';" \
            "SET LOCAL statement_timeout = '15s';" \
            'SET LOCAL row_security = off;' \
            'COPY ('
        cat "$CATALOG_DIR/query.sql"
        printf '%s\n' \
            ') TO STDOUT WITH (FORMAT CSV, HEADER TRUE);' \
            'COMMIT;'
    } | psql_stream > "$output_file" || return 3
}

capture_containers() {
    local output_file="$1"
    local compose_json
    compose_json="$(compose_command config --format json)" || return 3
    printf 'service,container_id,container,state,health,image_id,compose_image_id,restart_policy\n' \
        > "$output_file" || return 3
    local service container_id inspect_row state health image_id image_ref compose_image_id container_name restart_policy
    for service in postgres fstservice festivalweb fstworker; do
        container_id="$(compose_command ps -a -q "$service")" || return 3
        [[ "$container_id" == "${PROJECT_SERVICE_CONTAINER_IDS[$service]}" ]] \
            || {
                printf 'ERROR: exact compose service container drift: %s\n' \
                    "$service" >&2
                return 2
            }
        image_ref="$(
            python3 -c '
import json, sys
service = sys.argv[1]
config = json.load(sys.stdin)
print((config.get("services", {}).get(service, {}).get("image") or "").strip())
' "$service" <<< "$compose_json"
        )" || return 3
        compose_image_id=""
        if [[ -n "$image_ref" ]]; then
            compose_image_id="$(docker image inspect --format '{{.Id}}' "$image_ref" 2>/dev/null || true)"
        fi
        if [[ -z "$container_id" ]]; then
            printf '%s,,,missing,none,,%s,\n' \
                "$service" "$compose_image_id" >> "$output_file" || return 3
            continue
        fi
        inspect_row="$(
            docker inspect --format \
                '{{.Name}}|{{.State.Status}}|{{if .State.Health}}{{.State.Health.Status}}{{else}}none{{end}}|{{.Image}}|{{.HostConfig.RestartPolicy.Name}}' \
                "$container_id"
        )" || return 3
        IFS='|' read -r container_name state health image_id restart_policy <<< "$inspect_row"
        container_name="${container_name#/}"
        printf '%s,%s,%s,%s,%s,%s,%s,%s\n' \
            "$service" "$container_id" "$container_name" "$state" "$health" \
            "$image_id" "$compose_image_id" "$restart_policy" \
            >> "$output_file" || return 3
    done
    return 0
}

capture_health() {
    local output_file="$1"
    local body_dir="$2"
    mkdir -p "$body_dir" || return 3
    printf 'check,status,detail\n' > "$output_file" || return 3
    if docker exec \
        "$PG_CONTAINER" \
        env -u PGHOST -u PGHOSTADDR -u PGPORT \
        -u PGSERVICE -u PGSERVICEFILE \
        pg_isready -h "$PG_SOCKET_DIR" -p "$PG_PORT" \
        -U "$PG_USER" -d "$PG_DB" \
        > "$body_dir/pg-isready.txt" 2>&1; then
        printf 'postgres-readiness,ok,pg_isready\n' >> "$output_file" \
            || return 3
    else
        printf 'postgres-readiness,failed,pg_isready\n' >> "$output_file" \
            || return 3
    fi

    local name url code _format _gate
    while IFS=$'\t' read -r name url _format expected_status _gate; do
        [[ "$name" == "name" || -z "$name" ]] && continue
        if [[ "$name" != "readyz" && "$name" != "service-info" && "$name" != "web-shell" ]]; then
            continue
        fi
        if ! code="$(
            curl -sS --max-time 20 \
                -o "$body_dir/$name.body" \
                -w '%{http_code}' \
                "$url"
        )"; then
            code="000"
        fi
        if [[ "$code" == "$expected_status" ]]; then
            printf '%s,ok,http-%s\n' "$name" "$code" >> "$output_file" \
                || return 3
        else
            printf '%s,failed,http-%s\n' "$name" "$code" >> "$output_file" \
                || return 3
        fi
    done < "$PACKAGE_DIR/public-fingerprints.tsv"
    return 0
}

capture_capacity_guard() {
    local output_file="$1"
    local health_file="$2"
    local stdout_file="$3"
    local policy_file="${output_file%.json}.policy.json"
    python3 - \
        "$CAPACITY_GUARD" \
        "$policy_file" \
        "$CAPACITY_ESTIMATED_FULL_SCRAPE_GROWTH_BYTES" \
        "$CAPACITY_EXPECTED_FULL_SCRAPES_PER_DAY" \
        "$CAPACITY_MINIMUM_HEADROOM_DAYS" \
        "$CAPACITY_MINIMUM_HEADROOM_BYTES_OVERRIDE" \
        "$CAPACITY_TRANSIENT_BUILD_BYTES" \
        "$CAPACITY_REQUIRED_SCRATCH_BYTES" \
        "$CAPACITY_EXPECTED_RECLAIM_BYTES" <<'PY' || return 3
import hashlib
import json
import sys
from pathlib import Path

script, output, growth, runs, days, override, transient, scratch, reclaim = (
    sys.argv[1:]
)
policy = {
    "schemaVersion": 1,
    "actionClass": "reclaim",
    "guardScriptSha256": hashlib.sha256(
        Path(script).read_bytes()
    ).hexdigest(),
    "effectiveParameters": {
        "estimatedFullScrapeGrowthBytes": int(growth),
        "expectedFullScrapesPerDay": int(runs),
        "minimumHeadroomDays": int(days),
        "minimumHeadroomBytesOverride": int(override),
        "transientBuildBytes": int(transient),
        "requiredScratchBytes": int(scratch),
        "expectedReclaimBytes": int(reclaim),
    },
}
Path(output).write_text(
    json.dumps(policy, sort_keys=True, indent=2) + "\n",
    encoding="utf-8",
)
PY
    if env \
        -u ACTION_CLASS \
        -u TRANSIENT_BUILD_BYTES \
        -u REQUIRED_SCRATCH_BYTES \
        -u EXPECTED_RECLAIM_BYTES \
        -u ESTIMATED_FULL_SCRAPE_GROWTH_BYTES \
        -u EXPECTED_FULL_SCRAPES_PER_DAY \
        -u MINIMUM_HEADROOM_DAYS \
        -u MINIMUM_HEADROOM_BYTES_OVERRIDE \
        "$CAPACITY_GUARD" \
        --action-class reclaim \
        --transient-build-bytes "$CAPACITY_TRANSIENT_BUILD_BYTES" \
        --required-scratch-bytes "$CAPACITY_REQUIRED_SCRATCH_BYTES" \
        --expected-reclaim-bytes "$CAPACITY_EXPECTED_RECLAIM_BYTES" \
        --estimated-full-scrape-growth-bytes \
            "$CAPACITY_ESTIMATED_FULL_SCRAPE_GROWTH_BYTES" \
        --expected-full-scrapes-per-day \
            "$CAPACITY_EXPECTED_FULL_SCRAPES_PER_DAY" \
        --minimum-headroom-days "$CAPACITY_MINIMUM_HEADROOM_DAYS" \
        --minimum-headroom-bytes \
            "$CAPACITY_MINIMUM_HEADROOM_BYTES_OVERRIDE" \
        --compose-dir "$COMPOSE_DIR" \
        --pg-container "$PG_CONTAINER" \
        --pg-user "$PG_USER" \
        --pg-db "$PG_DB" \
        --pg-port "$PG_PORT" \
        --fst-storage-root "$FST_STORAGE_ROOT" \
        --output "$output_file" \
        > "$stdout_file" 2>&1; then
        printf 'capacity-guard,ok,reclaim\n' >> "$health_file"
    else
        printf 'capacity-guard,failed,reclaim\n' >> "$health_file"
        return 3
    fi
    [[ -s "$output_file" && -s "$policy_file" ]] || return 3
    return 0
}

capture_fingerprints() {
    local output_file="$1"
    local body_dir="$2"
    mkdir -p "$body_dir" || return 3
    printf 'name,url,resolved_url,format,expected_status,gate,http_status,body_bytes,sha256\n' \
        > "$output_file" || return 3
    local -A cached_body=()
    local -A cached_code=()
    local account_id=""
    local team_key=""
    local name url resolved_url format expected_status gate code bytes hash
    local body canonical max_time source_canonical extracted unresolved
    while IFS=$'\t' read -r name url format expected_status gate; do
        [[ "$name" == "name" || -z "$name" ]] && continue
        unresolved=false
        if [[ "$url" == *"{account_id}"* && -z "$account_id" ]] \
            || [[ "$url" == *"{team_key}"* && -z "$team_key" ]]; then
            unresolved=true
        fi
        if $unresolved; then
            resolved_url="$url"
        else
            resolved_url="${url//\{account_id\}/$account_id}"
            resolved_url="${resolved_url//\{team_key\}/$team_key}"
        fi
        body="$body_dir/$name.body"
        canonical="$body_dir/$name.canonical"
        max_time=120
        [[ "$name" == "band-song-rows" ]] && max_time=180
        [[ "$format" == player-export* ]] && max_time=300

        if $unresolved \
            || [[ "$resolved_url" == *"{"* || "$resolved_url" == *"}"* ]]; then
            code="000"
            : > "$body"
        elif [[ -n "${cached_body[$resolved_url]:-}" ]]; then
            cp "${cached_body[$resolved_url]}" "$body" || return 3
            code="${cached_code[$resolved_url]}"
        else
            if ! code="$(
                curl -sS --max-time "$max_time" \
                    -o "$body" \
                    -w '%{http_code}' \
                    "$resolved_url"
            )"; then
                code="000"
            fi
            [[ -f "$body" ]] || : > "$body"
            cached_body["$resolved_url"]="$body"
            cached_code["$resolved_url"]="$code"
        fi

        case "$format" in
            json)
                if ! python3 "$HELPER" canonicalize-json \
                    --input "$body" \
                    --output "$canonical" \
                    > "$body_dir/$name.canonicalize.log" 2>&1; then
                    : > "$canonical"
                fi
                ;;
            text)
                cp "$body" "$canonical" || return 3
                ;;
            leaderboard-semantic)
                if ! python3 "$HELPER" normalize-leaderboard \
                    --input "$body" \
                    --output "$canonical" \
                    > "$body_dir/$name.canonicalize.log" 2>&1; then
                    : > "$canonical"
                fi
                ;;
            player-export)
                if ! python3 "$HELPER" normalize-player-export \
                    --input "$body" \
                    --output "$canonical" \
                    > "$body_dir/$name.canonicalize.log" 2>&1; then
                    : > "$canonical"
                fi
                ;;
            player-export-solo)
                source_canonical="$body_dir/player-export.canonical"
                if [[ ! -s "$source_canonical" ]] \
                    || ! python3 "$HELPER" normalize-solo-export \
                        --input "$source_canonical" \
                        --output "$canonical" \
                        > "$body_dir/$name.canonicalize.log" 2>&1; then
                    : > "$canonical"
                fi
                ;;
            *)
                printf 'ERROR: unsupported fingerprint format: %s\n' \
                    "$format" >&2
                return 1
                ;;
        esac

        if [[ "$name" == "rankings" && "$code" == "$expected_status" ]]; then
            extracted=""
            if extracted="$(
                python3 "$HELPER" extract-account-id --input "$body" \
                    2> "$body_dir/$name.identifier.log"
            )"; then
                account_id="$extracted"
            fi
        elif [[ "$name" == "band-rankings" && "$code" == "$expected_status" ]]; then
            extracted=""
            if extracted="$(
                python3 "$HELPER" extract-team-key --input "$body" \
                    2> "$body_dir/$name.identifier.log"
            )"; then
                team_key="$extracted"
            fi
        fi

        bytes="$(wc -c < "$canonical" | tr -d ' ')" || return 3
        if [[ "$bytes" == "0" ]]; then
            hash=""
        else
            hash="$(sha256sum "$canonical" | awk '{print $1}')" || return 3
        fi
        printf '%s,%s,%s,%s,%s,%s,%s,%s,%s\n' \
            "$name" "$url" "$resolved_url" "$format" "$expected_status" \
            "$gate" "$code" "$bytes" "$hash" >> "$output_file" || return 3
        [[ -s "$output_file" ]] || return 3
    done < "$PACKAGE_DIR/public-fingerprints.tsv"
    return 0
}

capture_storage() {
    local output_file="$1"
    local relations_file="$2"
    local pgdata_source filesystem_source filesystem_type
    local total_bytes used_bytes free_bytes used_percent mount_point database_bytes target_bytes
    pgdata_source="$(
        docker inspect --format \
            '{{range .Mounts}}{{if eq .Destination "/var/lib/postgresql/data"}}{{.Source}}{{end}}{{end}}' \
            "$PG_CONTAINER"
    )" || return 3
    if [[ -z "$pgdata_source" ]]; then
        printf 'ERROR: %s has no PostgreSQL data mount\n' "$PG_CONTAINER" >&2
        return 1
    fi
    pgdata_source="$(realpath -m "$pgdata_source")"
    read -r total_bytes used_bytes free_bytes used_percent mount_point < <(
        df -B1 --output=size,used,avail,pcent,target "$pgdata_source" | tail -n 1
    ) || return 3
    used_percent="${used_percent%\%}"
    read -r filesystem_source filesystem_type < <(
        findmnt -T "$pgdata_source" -n -o SOURCE,FSTYPE
    ) || return 3
    database_bytes="$(
        timeout --signal=TERM --kill-after=30s 7m \
            docker exec \
            -e PGCONNECT_TIMEOUT=10 \
            -e PGOPTIONS="-c row_security=off" \
            "$PG_CONTAINER" \
            env -u PGHOST -u PGHOSTADDR -u PGPORT \
            -u PGSERVICE -u PGSERVICEFILE \
            psql -X -qAt -v ON_ERROR_STOP=1 \
            -h "$PG_SOCKET_DIR" -p "$PG_PORT" \
            -U "$PG_USER" -d "$PG_DB" \
            -c 'SELECT pg_database_size(current_database())'
    )" || return 3
    target_bytes="$(
        python3 - "$relations_file" <<'PY'
import csv, sys
with open(sys.argv[1], newline="", encoding="utf-8") as handle:
    print(sum(int(row["total_bytes"] or 0) for row in csv.DictReader(handle)))
PY
    )" || return 3
    local on_fst_drive=false
    if realpath_under "$pgdata_source" "$FST_STORAGE_ROOT" \
        && realpath_under "$OUTPUT_DIR" "$FST_STORAGE_ROOT"; then
        on_fst_drive=true
    fi
    printf 'pgdata_source,filesystem_source,filesystem_type,evidence_root,on_fst_drive,filesystem_total_bytes,filesystem_used_bytes,filesystem_free_bytes,filesystem_used_percent,filesystem_mount,database_bytes,target_total_bytes\n' \
        > "$output_file" || return 3
    printf '%s,%s,%s,%s,%s,%s,%s,%s,%s,%s,%s,%s\n' \
        "$pgdata_source" "$filesystem_source" "$filesystem_type" \
        "$FST_EVIDENCE_ROOT" "$on_fst_drive" "$total_bytes" "$used_bytes" \
        "$free_bytes" "$used_percent" "$mount_point" "$database_bytes" \
        "$target_bytes" >> "$output_file" || return 3
    return 0
}

scan_rg_root() {
    local scan="$1"
    local root_label="$2"
    local full_root="$3"
    local output_file="$4"
    local roots_file="$5"
    local stderr_file="$6"
    local pattern="$7"
    local status
    if [[ "$root_label" == *','* || "$root_label" == *$'\t'* \
        || "$root_label" == *$'\n'* ]]; then
        printf 'ERROR: unsafe source scan root label\n' >&2
        return 2
    fi
    set +e
    rg -n -o --no-heading --color never "$pattern" "$full_root" \
        --glob '!**/bin/**' --glob '!**/obj/**' \
        >> "$output_file" 2> "$stderr_file"
    status=$?
    set -e
    if (( status > 1 )); then
        printf '%s,%s,%s,error\n' \
            "$scan" "$root_label" "$status" >> "$roots_file"
        printf 'ERROR: source scan failed for %s (rg exit %s)\n' \
            "$root_label" "$status" >&2
        return "$status"
    fi
    printf '%s,%s,%s,ok\n' \
        "$scan" "$root_label" "$status" >> "$roots_file"
}

capture_production_compose_ownership() {
    local output_file="$1"
    local roots_file="$2"
    local pattern="$3"
    local compose_files_csv="$CAPTURE_DIR/production-compose-files.csv"
    local sanitized_json="$CAPTURE_DIR/production-compose.sanitized.json"
    local binds_tsv="$CAPTURE_DIR/production-compose-binds.tsv"
    local bind_files_csv="$CAPTURE_DIR/production-bind-config-files.csv"
    local path resolved safe_root source target classification
    local service read_only ordinal sha label_discovered expected_ordinal=0
    local candidate_count
    local before_hash after_hash candidate_list
    $COMPOSE_PROJECT_INITIALIZED || return 2
    printf 'path,sha256\n' > "$bind_files_csv"

    while IFS=',' read -r ordinal path sha label_discovered; do
        [[ "$ordinal" == "ordinal" || -z "$ordinal" ]] && continue
        ((expected_ordinal += 1))
        [[ "$ordinal" == "$expected_ordinal" ]] || return 2
        [[ "$label_discovered" == "true" ]] || return 2
        [[ "$path" == "${COMPOSE_FILE_PATHS[$((ordinal - 1))]}" ]] \
            || return 2
        resolved="$(realpath -e "$path")" || return 2
        before_hash="$(sha256sum "$resolved" | awk '{print $1}')"
        [[ "$before_hash" == "$sha" ]] || return 2
        safe_root="production-$(
            printf '%s' "$resolved" | sha256sum | awk '{print $1}'
        )"
        scan_rg_root \
            production-compose-raw \
            "$path" \
            "$resolved" \
            "$output_file" \
            "$roots_file" \
            "$CAPTURE_DIR/source-scan-$safe_root.stderr" \
            "$pattern"
        after_hash="$(sha256sum "$resolved" | awk '{print $1}')"
        [[ "$before_hash" == "$after_hash" ]] || {
            printf 'ERROR: compose config changed during ownership scan\n' >&2
            return 2
        }
    done < "$compose_files_csv"
    (( expected_ordinal == ${#COMPOSE_FILE_PATHS[@]} )) || return 2

    scan_rg_root \
        production-compose-rendered \
        production-compose.sanitized.json \
        "$sanitized_json" \
        "$output_file" \
        "$roots_file" \
        "$CAPTURE_DIR/source-scan-compose-rendered.stderr" \
        "$pattern"
    scan_rg_root \
        production-bind-inventory \
        production-compose-binds.tsv \
        "$binds_tsv" \
        "$output_file" \
        "$roots_file" \
        "$CAPTURE_DIR/source-scan-compose-binds.stderr" \
        "$pattern"

    while IFS=$'\t' read -r service source target read_only classification; do
        [[ "$service" == "service" || -z "$service" ]] && continue
        case "$classification" in
            config-file)
                resolved="$(realpath -e "$source")" || return 2
                [[ -f "$resolved" && -r "$resolved" ]] || return 2
                [[ "$(stat -c '%s' "$resolved")" -le 2097152 ]] || {
                    printf 'ERROR: bind-mounted config file exceeds 2 MiB\n' >&2
                    return 2
                }
                before_hash="$(sha256sum "$resolved" | awk '{print $1}')"
                safe_root="production-$(
                    printf '%s' "$resolved" | sha256sum | awk '{print $1}'
                )"
                scan_rg_root \
                    production-bind-config \
                    "$resolved" \
                    "$resolved" \
                    "$output_file" \
                    "$roots_file" \
                    "$CAPTURE_DIR/source-scan-$safe_root.stderr" \
                    "$pattern"
                after_hash="$(sha256sum "$resolved" | awk '{print $1}')"
                [[ "$before_hash" == "$after_hash" ]] || return 2
                printf '%s,%s\n' "$resolved" "$after_hash" \
                    >> "$bind_files_csv"
                ;;
            config-directory)
                resolved="$(realpath -e "$source")" || return 2
                [[ -d "$resolved" && -r "$resolved" ]] || return 2
                candidate_count=0
                candidate_list="$CAPTURE_DIR/bind-config-$(
                    printf '%s' "$resolved" | sha256sum | awk '{print $1}'
                ).list"
                if ! find "$resolved" -maxdepth 2 -type f -size -2097153c \
                    \( -iname 'appsettings*.json' \
                       -o -iname '*.yaml' -o -iname '*.yml' \
                       -o -iname '*.toml' -o -iname '*.conf' \
                       -o -iname '*.cfg' \
                       -o -iname '*.ini' -o -iname '*.xml' \
                       -o -iname '*.properties' \) \
                    ! -iname '.env*' \
                    ! -iregex '.*[/_.-]\(secrets\?\|password\|tokens\?\|credentials\?\|private\|certs\?\|keys\?\)[/_.-].*' \
                    -print0 > "$candidate_list"; then
                    printf 'ERROR: bind config directory enumeration failed\n' >&2
                    return 2
                fi
                while IFS= read -r -d '' path; do
                    before_hash="$(sha256sum "$path" | awk '{print $1}')"
                    safe_root="production-$(
                        printf '%s' "$path" | sha256sum | awk '{print $1}'
                    )"
                    scan_rg_root \
                        production-bind-config \
                        "$path" \
                        "$path" \
                        "$output_file" \
                        "$roots_file" \
                        "$CAPTURE_DIR/source-scan-$safe_root.stderr" \
                        "$pattern"
                    after_hash="$(sha256sum "$path" | awk '{print $1}')"
                    [[ "$before_hash" == "$after_hash" ]] || return 2
                    printf '%s,%s\n' "$path" "$after_hash" \
                        >> "$bind_files_csv"
                    ((candidate_count += 1))
                done < "$candidate_list"
                rm -f "$candidate_list"
                ;;
            secret|data|other|not-bind) ;;
            *)
                printf 'ERROR: unknown compose bind classification\n' >&2
                return 2
                ;;
        esac
    done < "$binds_tsv"
    {
        head -n 1 "$bind_files_csv"
        tail -n +2 "$bind_files_csv" | LC_ALL=C sort -u
    } > "$bind_files_csv.sorted"
    mv "$bind_files_csv.sorted" "$bind_files_csv"
}

capture_source_references() {
    local output_file="$1"
    local retained_file="$2"
    local roots_file="$3"
    local pattern
    pattern='leaderboard_current_entries|leaderboard_entry_versions|leaderboard_logical_write_metrics|player_score_observations|player_score_observation_union|band_song_team_rankings|band_song_team_ranking_state|ranking_deltas|ranking_delta_tiers|rank_history_deltas|composite_ranking_deltas|combo_ranking_deltas'
    : > "$output_file"
    printf 'scan,root,exit_code,status\n' > "$roots_file"
    local root full_root safe_root status
    local -a active_roots=(
        FSTService
        FortniteFestival.Core
        FortniteFestivalWeb/src
        packages
        docker-compose.yml
        deploy/docker-compose.yml
    )
    for root in "${active_roots[@]}"; do
        full_root="$REPO_ROOT/$root"
        safe_root="${root//\//_}"
        scan_rg_root \
            active-runtime \
            "$root" \
            "$full_root" \
            "$output_file" \
            "$roots_file" \
            "$CAPTURE_DIR/source-scan-$safe_root.stderr" \
            "$pattern"
    done

    set +e
    rg -n -o --no-heading --color never "$pattern" "$REPO_ROOT" \
        --glob '!**/.git/**' --glob '!FortniteFestivalWeb/test-results/**' \
        > "$retained_file" \
        2> "$CAPTURE_DIR/source-scan-retained.stderr"
    status=$?
    set -e
    if (( status > 1 )); then
        printf 'retained-audit,.,%s,error\n' "$status" >> "$roots_file"
        printf 'ERROR: retained source audit failed (rg exit %s)\n' \
            "$status" >&2
        return "$status"
    fi
    printf 'retained-audit,.,%s,ok\n' "$status" >> "$roots_file"

    capture_production_compose_ownership \
        "$output_file" \
        "$roots_file" \
        "$pattern"
}

canonicalize_dump_for_digest() {
    local input_file="$1"
    local output_file="$2"
    python3 "$HELPER" canonicalize-pg-dump \
        --input "$input_file" \
        --output "$output_file"
}

prepare_rollback_variants() {
    local executable_dir="$1"
    local canonical_dir="$2"
    local raw_hash_file="$3"
    local family raw_hash
    mkdir -p "$executable_dir" "$canonical_dir" || return 3
    printf 'family,file,sha256\n' > "$raw_hash_file" || return 3
    for family in \
        logical-shadow \
        score-observations \
        band-song-projection \
        aggregate-ranking-deltas; do
        raw_hash="$(
            sha256sum "$ROLLBACK_DIR/$family.sql" | awk '{print $1}'
        )" || return 3
        printf '%s,%s,%s\n' \
            "$family" "$family.sql" "$raw_hash" \
            >> "$raw_hash_file" || return 3
        python3 "$HELPER" prepare-executable-pg-dump \
            --input "$ROLLBACK_DIR/$family.sql" \
            --output "$executable_dir/$family.sql" || return 3
        canonicalize_dump_for_digest \
            "$executable_dir/$family.sql" \
            "$canonical_dir/$family.sql" || return 3
    done
    return 0
}

prepare_live_rollbacks() {
    local family raw output
    : > "$CAPTURE_DIR/rollback-generation-failures.txt"
    for family in logical-shadow score-observations band-song-projection aggregate-ranking-deltas; do
        local -a dump_args=(
            pg_dump
            --schema-only
            --strict-names
            --no-owner
            --quote-all-identifiers
            --lock-wait-timeout=5s
            -h "$PG_SOCKET_DIR"
            -p "$PG_PORT"
            -U "$PG_USER"
            -d "$PG_DB"
        )
        while IFS=$'\t' read -r _order object_family _type _relkind schema name _rest; do
            [[ "$_order" == "order" || "$object_family" != "$family" ]] && continue
            dump_args+=("--table=$schema.$name")
        done < "$PACKAGE_DIR/objects.tsv"
        raw="$ROLLBACK_DIR/$family.raw.sql"
        output="$ROLLBACK_DIR/$family.sql"
        if ! timeout --signal=TERM --kill-after=10s 2m \
            docker exec \
                -e PGOPTIONS="-c statement_timeout=30s -c row_security=off" \
                "$PG_CONTAINER" \
                env -u PGHOST -u PGHOSTADDR -u PGPORT \
                -u PGSERVICE -u PGSERVICEFILE \
                "${dump_args[@]}" > "$raw" \
            2> "$LOG_DIR/pg-dump-$family.log"; then
            printf '%s\n' "$family" >> "$CAPTURE_DIR/rollback-generation-failures.txt"
            printf '%s\n' \
                "-- Rollback generation failed for $family; execution is blocked." \
                > "$output"
            rm -f "$raw"
            continue
        fi
        mv "$raw" "$output"
        if [[ "$family" == "score-observations" ]]; then
            local sequence_state sequence_last_value sequence_is_called sequence_called_sql
            if ! sequence_state="$(
                timeout --signal=TERM --kill-after=5s 30s \
                    docker exec \
                    -e PGCONNECT_TIMEOUT=10 \
                    -e PGOPTIONS="-c row_security=off" \
                    "$PG_CONTAINER" \
                    env -u PGHOST -u PGHOSTADDR -u PGPORT \
                    -u PGSERVICE -u PGSERVICEFILE \
                    psql -X -qAt -v ON_ERROR_STOP=1 -F '|' \
                    -h "$PG_SOCKET_DIR" -p "$PG_PORT" \
                    -U "$PG_USER" -d "$PG_DB" \
                    -c "SET lock_timeout = '2s'; SET statement_timeout = '15s'; SELECT last_value, is_called FROM public.player_score_observations_id_seq"
            )"; then
                printf '%s\n' "$family" \
                    >> "$CAPTURE_DIR/rollback-generation-failures.txt"
                continue
            fi
            IFS='|' read -r sequence_last_value sequence_is_called \
                <<< "$sequence_state"
            if [[ ! "$sequence_last_value" =~ ^[0-9]+$ \
                  || ! "$sequence_is_called" =~ ^(t|f)$ ]]; then
                printf '%s\n' "$family" \
                    >> "$CAPTURE_DIR/rollback-generation-failures.txt"
                continue
            fi
            sequence_called_sql=false
            [[ "$sequence_is_called" == "t" ]] && sequence_called_sql=true
            {
                printf '\n-- Preserve the captured empty-table sequence state.\n'
                printf "SELECT pg_catalog.setval('public.player_score_observations_id_seq'::regclass, %s, %s);\n" \
                    "$sequence_last_value" "$sequence_called_sql"
            } > "$ROLLBACK_DATA_DIR/score-observations.data.sql"
        fi
    done
}

combine_rollbacks() {
    {
        printf '%s\n' \
            '-- Exact combined rollback DDL and retained rows. Apply only to a fully absent cleanup schema.' \
            'DO $guards$' \
            'BEGIN' \
            '    IF NOT pg_catalog.pg_try_advisory_xact_lock(5067481511116519501) THEN' \
            "        RAISE EXCEPTION 'The FST schema-DDL maintenance guard is busy';" \
            '    END IF;' \
            '    IF NOT pg_catalog.pg_try_advisory_xact_lock(5067481511116519502) THEN' \
            "        RAISE EXCEPTION 'The retired sequence guard is busy';" \
            '    END IF;' \
            'END' \
            '$guards$;'
        for family in logical-shadow score-observations band-song-projection aggregate-ranking-deltas; do
            cat "$ROLLBACK_EXECUTABLE_DIR/$family.sql"
            printf '\n'
            if [[ -f "$ROLLBACK_DATA_DIR/$family.data.sql" ]]; then
                cat "$ROLLBACK_DATA_DIR/$family.data.sql"
                printf '\n'
            fi
        done
    } > "$ROLLBACK_EXECUTABLE_DIR/rollback-all.sql"

    {
        printf '%s\n' \
            '-- DIGEST-ONLY CANONICAL ROLLBACK PLAN; NEVER EXECUTE THIS FILE.' \
            '-- Canonical pg_dump boundaries plus trusted generated data payloads.' \
            'DO $guards$' \
            'BEGIN' \
            '    IF NOT pg_catalog.pg_try_advisory_xact_lock(5067481511116519501) THEN' \
            "        RAISE EXCEPTION 'The FST schema-DDL maintenance guard is busy';" \
            '    END IF;' \
            '    IF NOT pg_catalog.pg_try_advisory_xact_lock(5067481511116519502) THEN' \
            "        RAISE EXCEPTION 'The retired sequence guard is busy';" \
            '    END IF;' \
            'END' \
            '$guards$;'
        for family in logical-shadow score-observations band-song-projection aggregate-ranking-deltas; do
            cat "$ROLLBACK_CANONICAL_DIR/$family.sql"
            printf '\n'
            if [[ -f "$ROLLBACK_DATA_DIR/$family.data.sql" ]]; then
                cat "$ROLLBACK_DATA_DIR/$family.data.sql"
                printf '\n'
            fi
        done
    } > "$ROLLBACK_CANONICAL_DIR/rollback-all.sql"
}

append_hash_row() {
    local file="$1"
    local kind="$2"
    local family="$3"
    local name="$4"
    local actual="$5"
    local expected="$6"
    local verified=false
    [[ -n "$actual" && "$actual" == "$expected" ]] && verified=true
    printf '%s,%s,%s,%s,%s,%s\n' \
        "$kind" "$family" "$name" "$actual" "$expected" "$verified" >> "$file"
}

prepare_rollback_hashes() {
    local output_file="$CAPTURE_DIR/rollback-hashes.csv"
    printf 'kind,family,name,sha256,expected_sha256,verified\n' > "$output_file"
    if $TEST_MODE; then
        if [[ -f "$FIXTURE_DIR/rollback-hashes.csv" ]]; then
            tail -n +2 "$FIXTURE_DIR/rollback-hashes.csv" >> "$output_file"
        fi
    else
        local actual expected
        actual="$(sha256sum "$ROLLBACK_EVIDENCE_ROOT/retired-schema-rollback.sql" | awk '{print $1}')"
        expected="$(awk 'NR == 1 {print $1}' "$ROLLBACK_EVIDENCE_ROOT/retired-schema-rollback.sha256")"
        append_hash_row "$output_file" existing-evidence all \
            retired-schema-rollback.sql "$actual" "$expected"

        actual="$(sha256sum "$ROLLBACK_EVIDENCE_ROOT/retired-schema-baseline.txt" | awk '{print $1}')"
        expected="$(awk 'NR == 1 {print $1}' "$ROLLBACK_EVIDENCE_ROOT/retired-schema-baseline.sha256")"
        append_hash_row "$output_file" existing-evidence all \
            retired-schema-baseline.txt "$actual" "$expected"

        actual="$(sha256sum "$ROLLBACK_EVIDENCE_ROOT/retired-schema-external-dependencies.txt" | awk '{print $1}')"
        expected="$(awk 'NR == 1 {print $1}' "$ROLLBACK_EVIDENCE_ROOT/retired-schema-external-dependencies.sha256")"
        append_hash_row "$output_file" existing-evidence all \
            retired-schema-external-dependencies.txt "$actual" "$expected"

        actual="$(sha256sum "$ROLLBACK_EVIDENCE_ROOT/ranking-deltas/rollback-schema.sql" | awk '{print $1}')"
        expected="$(
            awk '$2 ~ /\/rollback-schema.sql$/ {print $1}' \
                "$ROLLBACK_EVIDENCE_ROOT/ranking-deltas/SHA256SUMS"
        )"
        append_hash_row "$output_file" existing-evidence aggregate-ranking-deltas \
            ranking-deltas/rollback-schema.sql "$actual" "$expected"

        actual="$(sha256sum "$ROLLBACK_EVIDENCE_ROOT/ranking-deltas/catalog-baseline.tsv" | awk '{print $1}')"
        expected="$(
            awk '$2 ~ /\/catalog-baseline.tsv$/ {print $1}' \
                "$ROLLBACK_EVIDENCE_ROOT/ranking-deltas/SHA256SUMS"
        )"
        append_hash_row "$output_file" existing-evidence aggregate-ranking-deltas \
            ranking-deltas/catalog-baseline.tsv "$actual" "$expected"
    fi

    local family hash
    for family in logical-shadow score-observations band-song-projection aggregate-ranking-deltas; do
        hash="$(sha256sum "$ROLLBACK_CANONICAL_DIR/$family.sql" | awk '{print $1}')"
        if [[ -f "$CAPTURE_DIR/rollback-generation-failures.txt" ]] \
            && grep -Fxq "$family" "$CAPTURE_DIR/rollback-generation-failures.txt"; then
            append_hash_row "$output_file" generated-family "$family" \
                "$family.sql" "$hash" ""
        else
            append_hash_row "$output_file" generated-family "$family" \
                "$family.sql" "$hash" "$hash"
        fi
    done
    for family in logical-shadow score-observations band-song-projection; do
        hash="$(sha256sum "$ROLLBACK_DATA_DIR/$family.data.sql" | awk '{print $1}')"
        append_hash_row "$output_file" generated-data "$family" \
            "$family.data.sql" "$hash" "$hash"
    done
    hash="$(sha256sum "$ROLLBACK_CANONICAL_DIR/rollback-all.sql" | awk '{print $1}')"
    if [[ -s "$CAPTURE_DIR/rollback-generation-failures.txt" ]]; then
        append_hash_row "$output_file" generated-all all rollback-all.sql "$hash" ""
    else
        append_hash_row "$output_file" generated-all all rollback-all.sql "$hash" "$hash"
    fi
}

capture_live_precheck() {
    CURRENT_STAGE="read-only catalog capture"
    run_expected_capture capture-relations.sql "$CAPTURE_DIR/relations.csv"
    run_expected_capture \
        capture-column-catalog.sql \
        "$CAPTURE_DIR/column-catalog.raw.csv"
    run_catalog_signature_capture \
        "$CAPTURE_DIR/catalog-signature.raw.csv"
    run_expected_capture \
        capture-partition-children.sql \
        "$CAPTURE_DIR/partition-children.csv"
    run_expected_capture \
        capture-incoming-inheritance.sql \
        "$CAPTURE_DIR/incoming-inheritance.csv"
    run_expected_capture capture-unexpected-relations.sql "$CAPTURE_DIR/unexpected-relations.csv"
    run_expected_capture capture-owned-objects.sql "$CAPTURE_DIR/owned-objects.csv"
    run_expected_capture capture-external-dependencies.sql "$CAPTURE_DIR/external-dependencies.csv"
    run_plain_capture capture-preflight.sql "$CAPTURE_DIR/preflight.csv"
    capture_target_attestation "$CAPTURE_DIR/production-target-attestation.csv"
    capture_containers "$CAPTURE_DIR/containers.csv"
    capture_container_config_attestation \
        "$CAPTURE_DIR/container-config-attestation.json"
    capture_health "$CAPTURE_DIR/health.csv" "$CAPTURE_DIR/health-bodies"
    capture_capacity_guard \
        "$CAPTURE_DIR/capacity-guard.json" \
        "$CAPTURE_DIR/health.csv" \
        "$CAPTURE_DIR/capacity-guard.stdout.txt"
    capture_fingerprints "$CAPTURE_DIR/fingerprints.csv" "$CAPTURE_DIR/fingerprint-bodies"
    capture_storage "$CAPTURE_DIR/storage.csv" "$CAPTURE_DIR/relations.csv"
    capture_source_references \
        "$CAPTURE_DIR/runtime-source-references.txt" \
        "$CAPTURE_DIR/retained-reference-audit.txt" \
        "$CAPTURE_DIR/source-scan-roots.csv"
    printf 'container,cpu_percent,memory_usage,memory_percent,block_io,pids\n' \
        > "$CAPTURE_DIR/docker-stats.csv"
    docker stats --no-stream \
        --format '{{.Name}},{{.CPUPerc}},{{.MemUsage}},{{.MemPerc}},{{.BlockIO}},{{.PIDs}}' \
        "$PG_CONTAINER" \
        "${PROJECT_SERVICE_CONTAINER_IDS[fstservice]}" \
        "${PROJECT_SERVICE_CONTAINER_IDS[festivalweb]}" \
        "${PROJECT_SERVICE_CONTAINER_IDS[fstworker]}" \
        >> "$CAPTURE_DIR/docker-stats.csv" || true
}

capture_fixture_precheck() {
    CURRENT_STAGE="fixture catalog capture"
    if [[ -f "$FIXTURE_DIR/psql-stream-exit-code" ]]; then
        return "$(< "$FIXTURE_DIR/psql-stream-exit-code")"
    fi
    local fixture_target fixture_container fixture_db fixture_user fixture_port
    fixture_target="$(
        python3 - "$FIXTURE_DIR/pre/production-database-target.csv" <<'PY'
import csv
import sys

with open(sys.argv[1], newline="", encoding="utf-8") as handle:
    rows = list(csv.DictReader(handle))
if len(rows) != 1:
    raise SystemExit("fixture target is incomplete")
print("\t".join([
    rows[0]["container_id"],
    rows[0]["database"],
    rows[0]["user"],
    rows[0]["port"],
]))
PY
    )"
    IFS=$'\t' read -r fixture_container fixture_db fixture_user fixture_port \
        <<< "$fixture_target"
    if $PG_CONTAINER_EXPLICIT && [[ "$PG_CONTAINER" != "$fixture_container" ]]; then
        return 64
    fi
    if $PG_DB_EXPLICIT && [[ "$PG_DB" != "$fixture_db" ]]; then
        return 64
    fi
    if $PG_USER_EXPLICIT && [[ "$PG_USER" != "$fixture_user" ]]; then
        return 64
    fi
    PG_CONTAINER="$fixture_container"
    PG_DB="$fixture_db"
    PG_USER="$fixture_user"
    PG_PORT="$fixture_port"
    EXPECTED_OWNER="$fixture_user"
    local fixture_file
    for fixture_file in "$FIXTURE_DIR/pre/"*; do
        [[ -f "$fixture_file" ]] && cp "$fixture_file" "$CAPTURE_DIR/"
    done
    cp "$FIXTURE_DIR/pre/retained-data/"*.csv "$RETAINED_RAW_DIR/"
    if [[ -d "$FIXTURE_DIR/rollback" ]]; then
        cp "$FIXTURE_DIR/rollback/"*.sql "$ROLLBACK_DIR/"
    fi
    if [[ -d "$FIXTURE_DIR/rollback-data" ]]; then
        cp "$FIXTURE_DIR/rollback-data/"*.sql "$ROLLBACK_DATA_DIR/"
    fi
    for family in logical-shadow score-observations band-song-projection aggregate-ranking-deltas; do
        [[ -f "$ROLLBACK_DIR/$family.sql" ]] || {
            printf 'ERROR: fixture rollback file is missing for %s\n' "$family" >&2
            return 1
        }
    done
}

if ! $TEST_MODE; then
    CURRENT_STAGE="production compose and database target resolution"
    initialize_production_compose_project
fi

if $TEST_MODE; then
    capture_fixture_precheck
else
    CURRENT_STAGE="rollback DDL capture"
    prepare_live_rollbacks
    capture_live_precheck
fi

CURRENT_STAGE="rollback pg_dump boundary and meta-command verification"
prepare_rollback_variants \
    "$ROLLBACK_EXECUTABLE_DIR" \
    "$ROLLBACK_CANONICAL_DIR" \
    "$CAPTURE_DIR/rollback-raw-hashes.csv"

CURRENT_STAGE="complete catalog canonicalization"
python3 "$HELPER" prepare-column-catalog \
    --objects "$PACKAGE_DIR/objects.tsv" \
    --input "$CAPTURE_DIR/column-catalog.raw.csv" \
    --output "$CATALOG_DIR/columns.csv"
python3 "$HELPER" prepare-catalog-signature \
    --input "$CAPTURE_DIR/catalog-signature.raw.csv" \
    --query "$CATALOG_DIR/query.sql" \
    --column-catalog "$CATALOG_DIR/columns.csv" \
    --output "$CATALOG_DIR/signature.csv" \
    --metadata-output "$CATALOG_DIR/signature-metadata.json" \
    --expected-sql-output "$PACKAGE_DIR/catalog-expected.sql" \
    --assert-sql-output "$PACKAGE_DIR/catalog-assert.sql"

CURRENT_STAGE="retained capture generation"
python3 "$HELPER" render-retained-capture-sql \
    --spec "$PACKAGE_DIR/retained-data.tsv" \
    --column-catalog "$CATALOG_DIR/columns.csv" \
    --output-dir "$RETAINED_CAPTURE_SQL_DIR"
if ! $TEST_MODE; then
    psql_stream \
        < "$RETAINED_CAPTURE_SQL_DIR/leaderboard_logical_write_metrics.sql" \
        > "$RETAINED_RAW_DIR/leaderboard_logical_write_metrics.csv"
    psql_stream \
        < "$RETAINED_CAPTURE_SQL_DIR/band_song_team_ranking_state.sql" \
        > "$RETAINED_RAW_DIR/band_song_team_ranking_state.csv"
fi

CURRENT_STAGE="retained data canonicalization"
python3 "$HELPER" prepare-retained-data \
    --spec "$PACKAGE_DIR/retained-data.tsv" \
    --column-catalog "$CATALOG_DIR/columns.csv" \
    --raw-dir "$RETAINED_RAW_DIR" \
    --canonical-dir "$RETAINED_DIR" \
    --metadata-output "$CAPTURE_DIR/retained-data.csv" \
    --expected-sql-output "$PACKAGE_DIR/retained-expected.sql" \
    --assert-sql-output "$PACKAGE_DIR/retained-assert.sql" \
    --logical-data "$ROLLBACK_DATA_DIR/logical-shadow.data.sql" \
    --band-data "$ROLLBACK_DATA_DIR/band-song-projection.data.sql"
combine_rollbacks

CURRENT_STAGE="exact atomic drop generation"
python3 "$HELPER" render-drop-sql \
    --objects "$PACKAGE_DIR/objects.tsv" \
    --catalog-expected-sql "$PACKAGE_DIR/catalog-expected.sql" \
    --catalog-assert-sql "$PACKAGE_DIR/catalog-assert.sql" \
    --retained-expected-sql "$PACKAGE_DIR/retained-expected.sql" \
    --retained-assert-sql "$PACKAGE_DIR/retained-assert.sql" \
    --output "$PACKAGE_DIR/drop.sql"
python3 "$HELPER" render-rehearsal-check-sql \
    --objects "$PACKAGE_DIR/objects.tsv" \
    > "$PACKAGE_DIR/rollback-rehearsal-check.sql"

CURRENT_STAGE="rollback hash verification"
prepare_rollback_hashes

CURRENT_STAGE="deterministic manifest generation"
manifest_args=(
    python3 "$HELPER" build-manifest
    --objects "$PACKAGE_DIR/objects.tsv"
    --capture-dir "$CAPTURE_DIR"
    --drop-sql "$PACKAGE_DIR/drop.sql"
    --rollback-all "$ROLLBACK_CANONICAL_DIR/rollback-all.sql"
    --column-catalog "$CATALOG_DIR/columns.csv"
    --catalog-signature "$CATALOG_DIR/signature.csv"
    --catalog-metadata "$CATALOG_DIR/signature-metadata.json"
    --catalog-query "$CATALOG_DIR/query.sql"
    --catalog-expected-sql "$PACKAGE_DIR/catalog-expected.sql"
    --catalog-assert-sql "$PACKAGE_DIR/catalog-assert.sql"
    --retained-spec "$PACKAGE_DIR/retained-data.tsv"
    --retained-capture-dir "$RETAINED_CAPTURE_SQL_DIR"
    --retained-dir "$RETAINED_DIR"
    --retained-metadata "$CAPTURE_DIR/retained-data.csv"
    --retained-expected-sql "$PACKAGE_DIR/retained-expected.sql"
    --retained-assert-sql "$PACKAGE_DIR/retained-assert.sql"
    --fingerprint-spec "$PACKAGE_DIR/public-fingerprints.tsv"
    --fingerprint-spec-sha256 "$FINGERPRINT_SPEC_SHA256"
    --expected-owner "$EXPECTED_OWNER"
    --output-dir "$PACKAGE_DIR"
)
if [[ -n "$PACKAGE_PARITY_EVIDENCE" ]]; then
    manifest_args+=(--parity-evidence "$PACKAGE_PARITY_EVIDENCE")
fi
MANIFEST_SHA256="$("${manifest_args[@]}")"
printf '%s\n' "$MANIFEST_SHA256" > "$OUTPUT_DIR/manifest-sha256.txt"

manifest_ready=false
if python3 "$HELPER" manifest-ready --manifest "$PACKAGE_DIR/manifest.json"; then
    manifest_ready=true
fi

if [[ "$MODE" == "check" ]]; then
    write_rollback_instructions
    (
        cd "$OUTPUT_DIR"
        find . -type f ! -name package-checksums.sha256 -print0 \
            | sort -z \
            | xargs -0 sha256sum > package-checksums.sha256
    )
    printf 'Mode: check\n'
    printf 'Manifest SHA-256: %s\n' "$MANIFEST_SHA256"
    printf 'Evidence: %s\n' "$OUTPUT_DIR"
    if $manifest_ready; then
        printf 'Decision: prepared; execution still requires explicit --execute and accepted hash\n'
        exit 0
    fi
    printf 'Decision: blocked; see package/gate-report.txt\n' >&2
    exit 3
fi

if ! $manifest_ready; then
    printf 'ERROR: execute gates are not clear; see package/gate-report.txt\n' >&2
    exit 3
fi
if [[ "$MANIFEST_SHA256" != "$EXPECTED_MANIFEST_SHA256" ]]; then
    printf 'ERROR: manifest drift: expected %s, regenerated %s\n' \
        "$EXPECTED_MANIFEST_SHA256" "$MANIFEST_SHA256" >&2
    exit 3
fi

capture_preexecute_gate() {
    local gate_dir="$OUTPUT_DIR/pre-execute"
    mkdir -p "$gate_dir"
    prepare_rollback_variants \
        "$gate_dir/rollback-executable" \
        "$gate_dir/rollback-canonical" \
        "$gate_dir/rollback-raw-hashes.csv"
    reverify_production_compose_project "$gate_dir/compose"
    run_plain_capture capture-preflight.sql "$gate_dir/preflight.csv"
    capture_target_attestation "$gate_dir/production-target-attestation.csv"
    capture_containers "$gate_dir/containers.csv"
    capture_container_config_attestation \
        "$gate_dir/container-config-attestation.json"
    capture_health "$gate_dir/health.csv" "$gate_dir/health-bodies"
    capture_capacity_guard \
        "$gate_dir/capacity-guard.json" \
        "$gate_dir/health.csv" \
        "$gate_dir/capacity-guard.stdout.txt"
    capture_fingerprints "$gate_dir/fingerprints.csv" "$gate_dir/fingerprint-bodies"
    python3 - \
        "$PACKAGE_DIR/manifest.json" \
        "$gate_dir" \
        "$PACKAGE_DIR" \
        "$EXPECTED_MANIFEST_SHA256" <<'PY'
import csv
import hashlib
import json
import sys
from pathlib import Path

manifest = json.loads(Path(sys.argv[1]).read_text(encoding="utf-8"))
gate = Path(sys.argv[2])
package = Path(sys.argv[3])
expected_manifest_sha256 = sys.argv[4]

def sha256(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()

if sha256(Path(sys.argv[1])) != expected_manifest_sha256:
    raise SystemExit("pre-execute manifest file hash drift")

for path, expected in [
    (package / "objects.tsv", manifest["expectedObjectsSha256"]),
    (package / "drop.sql", manifest["dropSqlSha256"]),
    (
        package / "rollback-canonical" / "rollback-all.sql",
        manifest["rollbackAllSha256"],
    ),
    (
        package / "catalog" / "columns.csv",
        manifest["columnCatalogSha256"],
    ),
    (
        package / "catalog" / "signature.csv",
        manifest["catalogSignature"]["sha256"],
    ),
    (
        package / "catalog" / "signature-metadata.json",
        manifest["catalogMetadataSha256"],
    ),
    (
        package / "catalog" / "query.sql",
        manifest["catalogSignature"]["querySha256"],
    ),
    (
        package / "catalog-expected.sql",
        manifest["catalogExpectedSqlSha256"],
    ),
    (
        package / "catalog-assert.sql",
        manifest["catalogAssertSqlSha256"],
    ),
    (
        package / "retained-data.tsv",
        manifest["retainedDataSpecSha256"],
    ),
    (
        package / "retained-expected.sql",
        manifest["retainedExpectedSqlSha256"],
    ),
    (
        package / "retained-assert.sql",
        manifest["retainedAssertSqlSha256"],
    ),
    (package / "public-fingerprints.tsv", manifest["fingerprintSpecSha256"]),
    (package / "parity-acceptance.json", manifest["parityEvidence"]["sha256"]),
]:
    if not path.is_file() or sha256(path) != expected:
        raise SystemExit(f"pre-execute package hash drift: {path.name}")

for row in manifest["rollbackHashes"]:
    if row.get("kind") == "generated-family":
        path = package / "rollback-canonical" / row["name"]
        recaptured = gate / "rollback-canonical" / row["name"]
        executable = package / "rollback-executable" / row["name"]
        recaptured_executable = gate / "rollback-executable" / row["name"]
        if (
            not path.is_file()
            or not recaptured.is_file()
            or not executable.is_file()
            or not recaptured_executable.is_file()
            or sha256(path) != row["sha256"]
            or sha256(recaptured) != row["sha256"]
            or sha256(executable) != sha256(recaptured_executable)
        ):
            raise SystemExit(
                f"pre-execute rollback canonical drift: {row['name']}"
            )
    elif row.get("kind") == "generated-data":
        path = package / "rollback-data" / row["name"]
        if not path.is_file() or sha256(path) != row["sha256"]:
            raise SystemExit(
                f"pre-execute rollback data drift: {row['name']}"
            )

for row in manifest["retainedData"]:
    path = package / "retained-data" / row["canonical_file"]
    if not path.is_file() or sha256(path) != row["sha256"]:
        raise SystemExit(
            f"pre-execute retained payload drift: {row['name']}"
        )

for row in manifest["retainedCaptureSql"]:
    path = package / "retained-capture" / row["name"]
    if not path.is_file() or sha256(path) != row["sha256"]:
        raise SystemExit(
            f"pre-execute retained capture SQL drift: {row['name']}"
        )

def rows(name):
    with (gate / name).open(newline="", encoding="utf-8") as handle:
        return list(csv.DictReader(handle))

preflight_rows = rows("preflight.csv")
if len(preflight_rows) != 1:
    raise SystemExit("pre-execute preflight is missing")
preflight = preflight_rows[0]
for key, expected in manifest["preflight"].items():
    if preflight.get(key, "") != expected:
        raise SystemExit(
            f"pre-execute manifest drift for {key}: "
            f"{preflight.get(key, '<missing>')} != {expected}"
        )

target_rows = rows("production-target-attestation.csv")
if (
    len(target_rows) != 1
    or target_rows[0] != manifest["productionDatabaseTarget"]["runtime"]
):
    raise SystemExit("pre-execute production database target drift")
container_config = json.loads(
    (gate / "container-config-attestation.json").read_text(
        encoding="utf-8"
    )
)
if container_config != manifest["containerConfigAttestation"]:
    raise SystemExit("pre-execute actual container configuration drift")
if sha256(gate / "container-config-attestation.json") != \
        manifest["containerConfigAttestationSha256"]:
    raise SystemExit("pre-execute container attestation hash drift")

before_containers = {
    row["service"]: row for row in manifest["containers"]
}
after_containers = {
    row["service"]: row for row in rows("containers.csv")
}
for service in ["postgres", "fstservice", "festivalweb", "fstworker"]:
    before = before_containers.get(service)
    after = after_containers.get(service)
    if before is None or after is None:
        raise SystemExit(f"pre-execute container state missing: {service}")
    for key in [
        "container_id", "state", "health", "image_id",
        "compose_image_id", "restart_policy"
    ]:
        if after.get(key, "") != before.get(key, ""):
            raise SystemExit(
                f"pre-execute container drift for {service}.{key}"
            )

for row in rows("health.csv"):
    if row.get("status", "").lower() != "ok":
        raise SystemExit(f"pre-execute health failed: {row.get('check')}")

capacity_policy = json.loads(
    (gate / "capacity-guard.policy.json").read_text(encoding="utf-8")
)
capacity_report = json.loads(
    (gate / "capacity-guard.json").read_text(encoding="utf-8")
)
if capacity_policy != manifest["capacityGuardPolicy"]:
    raise SystemExit("pre-execute capacity policy drift")
if sha256(gate / "capacity-guard.policy.json") != \
        manifest["capacityGuardPolicySha256"]:
    raise SystemExit("pre-execute capacity policy hash drift")
if capacity_report.get("actionClass") != "reclaim":
    raise SystemExit("pre-execute capacity action drift")
if capacity_report.get("decision") not in {
    "accepted",
    "accepted_with_capacity_alert",
}:
    raise SystemExit("pre-execute capacity decision blocked")
if capacity_report.get("capacity", {}).get("reclaimAllowed") is not True:
    raise SystemExit("pre-execute reclaim capacity is not allowed")
if {
    key: capacity_report.get("capacity", {}).get(key)
    for key in capacity_policy["effectiveParameters"]
} != capacity_policy["effectiveParameters"]:
    raise SystemExit("pre-execute capacity parameters drift")

before_fingerprints = {
    row["name"]: row
    for row in manifest["fingerprints"]
}
after_fingerprints = {
    row["name"]: row
    for row in rows("fingerprints.csv")
    if row.get("gate", "").lower() == "true"
}
if set(before_fingerprints) != set(after_fingerprints):
    raise SystemExit("pre-execute fingerprint set drifted")
for name, before in before_fingerprints.items():
    after = after_fingerprints[name]
    if (
        after.get("http_status") != before.get("http_status")
        or after.get("resolved_url") != before.get("resolved_url")
        or after.get("sha256") != before.get("sha256")
    ):
        raise SystemExit(f"pre-execute fingerprint drift: {name}")
PY
    combine_rollbacks
}

capture_complete_post_scratch_gate() {
    local gate_dir="$OUTPUT_DIR/post-scratch-live-gate"
    local source_dir
    rm -rf "$gate_dir" || return 3
    mkdir -p \
        "$gate_dir/catalog" \
        "$gate_dir/retained-data-raw" \
        "$gate_dir/retained-data" \
        "$gate_dir/health-bodies" \
        "$gate_dir/fingerprint-bodies" || return 3
    if $TEST_MODE && [[ -f "$FIXTURE_DIR/fail-complete-gate-setup" ]]; then
        return 3
    fi
    if $TEST_MODE; then
        source_dir="$FIXTURE_DIR/pre"
        [[ -d "$FIXTURE_DIR/post-scratch-gate" ]] \
            && source_dir="$FIXTURE_DIR/post-scratch-gate"
        local filename
        for filename in \
            relations.csv \
            partition-children.csv \
            incoming-inheritance.csv \
            owned-objects.csv \
            unexpected-relations.csv \
            external-dependencies.csv \
            preflight.csv \
            production-target-attestation.csv \
            container-config-attestation.json \
            containers.csv \
            health.csv \
            capacity-guard.json \
            capacity-guard.policy.json \
            fingerprints.csv \
            storage.csv \
            column-catalog.raw.csv \
            catalog-signature.raw.csv; do
            cp "$source_dir/$filename" "$gate_dir/$filename" || return 3
        done
        cp "$source_dir/retained-data/"*.csv \
            "$gate_dir/retained-data-raw/" || return 3
    else
        reverify_production_compose_project "$gate_dir/compose" || return 3
        run_expected_capture capture-relations.sql "$gate_dir/relations.csv" \
            || return 3
        run_expected_capture \
            capture-column-catalog.sql \
            "$gate_dir/column-catalog.raw.csv" || return 3
        run_catalog_signature_capture \
            "$gate_dir/catalog-signature.raw.csv" || return 3
        run_expected_capture \
            capture-partition-children.sql \
            "$gate_dir/partition-children.csv" || return 3
        run_expected_capture \
            capture-incoming-inheritance.sql \
            "$gate_dir/incoming-inheritance.csv" || return 3
        run_expected_capture \
            capture-unexpected-relations.sql \
            "$gate_dir/unexpected-relations.csv" || return 3
        run_expected_capture \
            capture-owned-objects.sql \
            "$gate_dir/owned-objects.csv" || return 3
        run_expected_capture \
            capture-external-dependencies.sql \
            "$gate_dir/external-dependencies.csv" || return 3
        run_plain_capture capture-preflight.sql "$gate_dir/preflight.csv" \
            || return 3
        capture_target_attestation \
            "$gate_dir/production-target-attestation.csv" || return 3
        capture_containers "$gate_dir/containers.csv" || return 3
        capture_container_config_attestation \
            "$gate_dir/container-config-attestation.json" || return 3
        capture_health "$gate_dir/health.csv" "$gate_dir/health-bodies" \
            || return 3
        capture_capacity_guard \
            "$gate_dir/capacity-guard.json" \
            "$gate_dir/health.csv" \
            "$gate_dir/capacity-guard.stdout.txt" || return 3
        capture_fingerprints \
            "$gate_dir/fingerprints.csv" \
            "$gate_dir/fingerprint-bodies" || return 3
        capture_storage "$gate_dir/storage.csv" "$gate_dir/relations.csv" \
            || return 3
        psql_stream \
            < "$RETAINED_CAPTURE_SQL_DIR/leaderboard_logical_write_metrics.sql" \
            > "$gate_dir/retained-data-raw/leaderboard_logical_write_metrics.csv" \
            || return 3
        psql_stream \
            < "$RETAINED_CAPTURE_SQL_DIR/band_song_team_ranking_state.sql" \
            > "$gate_dir/retained-data-raw/band_song_team_ranking_state.csv" \
            || return 3
    fi
    if $TEST_MODE && [[ -f "$FIXTURE_DIR/fail-complete-gate-capture" ]]; then
        return 3
    fi

    python3 "$HELPER" prepare-column-catalog \
        --objects "$PACKAGE_DIR/objects.tsv" \
        --input "$gate_dir/column-catalog.raw.csv" \
        --output "$gate_dir/catalog/columns.csv" || return 3
    python3 "$HELPER" prepare-catalog-signature \
        --input "$gate_dir/catalog-signature.raw.csv" \
        --query "$CATALOG_DIR/query.sql" \
        --column-catalog "$gate_dir/catalog/columns.csv" \
        --output "$gate_dir/catalog/signature.csv" \
        --metadata-output "$gate_dir/catalog/signature-metadata.json" \
        --expected-sql-output "$gate_dir/catalog-expected.sql" \
        --assert-sql-output "$gate_dir/catalog-assert.sql" || return 3
    if $TEST_MODE && [[ -f "$FIXTURE_DIR/fail-complete-gate-catalog" ]]; then
        return 3
    fi
    python3 "$HELPER" validate-retained-recapture \
        --spec "$PACKAGE_DIR/retained-data.tsv" \
        --column-catalog "$gate_dir/catalog/columns.csv" \
        --raw-dir "$gate_dir/retained-data-raw" \
        --expected-metadata "$CAPTURE_DIR/retained-data.csv" \
        --output-dir "$gate_dir/retained-data" \
        --metadata-output "$gate_dir/retained-data.csv" || return 3
    if $TEST_MODE && [[ -f "$FIXTURE_DIR/fail-complete-gate-retained" ]]; then
        return 3
    fi
    python3 "$HELPER" validate-complete-live-gate \
        --manifest "$PACKAGE_DIR/manifest.json" \
        --expected-manifest-sha256 "$EXPECTED_MANIFEST_SHA256" \
        --gate-dir "$gate_dir" \
        --output "$gate_dir/validation.json" || return 3
    if $TEST_MODE && [[ -f "$FIXTURE_DIR/fail-complete-gate-validation" ]]; then
        return 3
    fi
    DROP_SECOND_GATE_SHA256="$(
        sha256sum "$gate_dir/validation.json" | awk '{print $1}'
    )" || return 3
    printf '%s  validation.json\n' "$DROP_SECOND_GATE_SHA256" \
        > "$gate_dir/validation.sha256" || return 3
    python3 - "$gate_dir/validation.json" "$EXPECTED_MANIFEST_SHA256" \
        <<'PY' || return 3
import json
import sys
from pathlib import Path

value = json.loads(Path(sys.argv[1]).read_text(encoding="utf-8"))
if value.get("success") is not True:
    raise SystemExit("second gate validation did not succeed")
if value.get("acceptedManifestSha256") != sys.argv[2]:
    raise SystemExit("second gate did not bind accepted manifest")
PY
    [[ "$(sha256sum "$gate_dir/validation.json" | awk '{print $1}')" \
        == "$DROP_SECOND_GATE_SHA256" ]] || return 3
    if $TEST_MODE && [[ -f "$FIXTURE_DIR/fail-complete-gate-artifact" ]]; then
        return 3
    fi
    return 0
}

drop_scratch_database() {
    $SCRATCH_DB_ACTIVE || return 0
    if timeout --signal=TERM --kill-after=15s 2m \
        docker exec \
        -e PGCONNECT_TIMEOUT=10 \
        "$PG_CONTAINER" \
        env -u PGHOST -u PGHOSTADDR -u PGPORT \
        -u PGSERVICE -u PGSERVICEFILE \
        dropdb --if-exists -h "$PG_SOCKET_DIR" -p "$PG_PORT" -U "$PG_USER" \
        "$SCRATCH_DB_NAME"; then
        SCRATCH_DB_ACTIVE=false
        return 0
    fi
    return 1
}

run_scratch_roundtrip_proof() {
    local proof_dir="$OUTPUT_DIR/pre-destructive-roundtrip"
    local verification_sql="$proof_dir/verify.sql"
    local existing
    mkdir -p "$proof_dir"
    if $TEST_MODE; then
        if [[ -f "$FIXTURE_DIR/scratch-proof-failure" ]]; then
            printf 'status,detail\nfailed,fixture\n' \
                > "$proof_dir/status.csv"
            return 3
        fi
        printf 'status,detail\npassed,fixture-schema-data-catalog\n' \
            > "$proof_dir/status.csv"
        return 0
    fi

    SCRATCH_DB_NAME="fst_cleanup_verify_${EXPECTED_MANIFEST_SHA256:0:12}_$$"
    [[ "$SCRATCH_DB_NAME" =~ ^[a-z0-9_]+$ ]] || return 2
    existing="$(control_psql_scalar \
        "SELECT count(*) FROM pg_catalog.pg_database WHERE datname = '$SCRATCH_DB_NAME'")" \
        || return 2
    [[ "$existing" == "0" ]] || return 2
    SCRATCH_DB_ACTIVE=true
    timeout --signal=TERM --kill-after=15s 2m \
        docker exec \
        -e PGCONNECT_TIMEOUT=10 \
        "$PG_CONTAINER" \
        env -u PGHOST -u PGHOSTADDR -u PGPORT \
        -u PGSERVICE -u PGSERVICEFILE \
        createdb -h "$PG_SOCKET_DIR" -p "$PG_PORT" -U "$PG_USER" \
        --owner="$PG_USER" --template=template0 "$SCRATCH_DB_NAME"

    timeout --signal=TERM --kill-after=30s 7m \
        docker exec -i \
        -e PGCONNECT_TIMEOUT=10 \
        -e PGOPTIONS="-c row_security=off" \
        "$PG_CONTAINER" \
        env -u PGHOST -u PGHOSTADDR -u PGPORT \
        -u PGSERVICE -u PGSERVICEFILE \
        psql -X --single-transaction -v ON_ERROR_STOP=1 \
        -h "$PG_SOCKET_DIR" -p "$PG_PORT" \
        -U "$PG_USER" -d "$SCRATCH_DB_NAME" \
        -P pager=off \
        < "$ROLLBACK_EXECUTABLE_DIR/rollback-all.sql" \
        > "$proof_dir/restore.log" 2>&1

    {
        printf '%s\n' '\set ON_ERROR_STOP on' 'BEGIN;'
        printf '%s\n' \
            "SET LOCAL lock_timeout = '5s';" \
            "SET LOCAL statement_timeout = '30s';" \
            "SET LOCAL transaction_timeout = '5min';" \
            'SET LOCAL row_security = off;'
        cat "$PACKAGE_DIR/expected-objects.sql"
        cat "$PACKAGE_DIR/catalog-expected.sql"
        cat "$PACKAGE_DIR/retained-expected.sql"
        cat "$PACKAGE_DIR/catalog-assert.sql"
        cat "$PACKAGE_DIR/retained-assert.sql"
        cat "$PACKAGE_DIR/rollback-rehearsal-check.sql"
        printf '%s\n' 'ROLLBACK;'
    } > "$verification_sql"
    psql_stream_database "$SCRATCH_DB_NAME" \
        < "$verification_sql" > "$proof_dir/verify.log" 2>&1
    drop_scratch_database
    printf 'status,detail\npassed,schema-retained-catalog-signature\n' \
        > "$proof_dir/status.csv"
}

load_approved_startup_identity_from_proof() {
    local proof_file="$1" identity
    identity="$(
        python3 - "$proof_file" <<'PY'
import json
import sys
from pathlib import Path

manifest = json.loads(Path(sys.argv[1]).read_text(encoding="utf-8"))[
    "manifest"
]
print(
    "|".join([
        manifest["approvedFstserviceImageId"],
        manifest["approvedFstserviceContainerId"],
        manifest["approvedFstserviceImageReferenceSha256"],
        json.dumps(
            manifest["approvedFstserviceNetworks"],
            separators=(",", ":"),
        ),
        manifest["approvedPostgresContainerId"],
        manifest["approvedPostgresSystemIdentifier"],
        manifest["approvedProductionDatabaseTarget"]["configured_host"],
        manifest["approvedProductionDatabaseTarget"]["configured_port"],
        manifest["approvedProductionDatabaseTarget"]["configured_database"],
        manifest["approvedProductionDatabaseTarget"]["configured_user"],
    ])
)
PY
    )" || return 3
    IFS='|' read -r \
        APPROVED_FSTSERVICE_IMAGE_ID \
        APPROVED_FSTSERVICE_CONTAINER_ID \
        APPROVED_FSTSERVICE_IMAGE_REFERENCE_SHA256 \
        APPROVED_FSTSERVICE_NETWORKS_JSON \
        APPROVED_POSTGRES_CONTAINER_ID \
        APPROVED_POSTGRES_SYSTEM_IDENTIFIER \
        APPROVED_DATABASE_HOST \
        APPROVED_DATABASE_PORT \
        APPROVED_DATABASE_NAME \
        APPROVED_DATABASE_USER \
        <<< "$identity"
    [[ "$APPROVED_FSTSERVICE_IMAGE_ID" =~ ^sha256:[0-9a-f]{64}$ ]] \
        || return 3
    [[ "$APPROVED_FSTSERVICE_CONTAINER_ID" =~ ^[0-9a-f]{64}$ ]] \
        || return 3
    [[ "$APPROVED_FSTSERVICE_IMAGE_REFERENCE_SHA256" \
        =~ ^[0-9a-f]{64}$ ]] || return 3
    python3 - "$APPROVED_FSTSERVICE_NETWORKS_JSON" <<'PY' || return 3
import json
import sys

networks = json.loads(sys.argv[1])
if (
    not isinstance(networks, list)
    or not networks
    or networks != sorted(set(networks))
    or any(not isinstance(network, str) or not network for network in networks)
):
    raise SystemExit("approved startup network set is invalid")
PY
    [[ "$APPROVED_POSTGRES_CONTAINER_ID" =~ ^[0-9a-f]{64}$ ]] \
        || return 3
    [[ "$APPROVED_POSTGRES_SYSTEM_IDENTIFIER" =~ ^[0-9]+$ ]] \
        || return 3
    [[ "$APPROVED_DATABASE_HOST" == "postgres" ]] || return 3
    [[ "$APPROVED_DATABASE_PORT" =~ ^[0-9]+$ ]] || return 3
    [[ -n "$APPROVED_DATABASE_NAME" && -n "$APPROVED_DATABASE_USER" ]] \
        || return 3
    return 0
}

execute_fixture() {
    printf '%s\n' \
        "'FST_FAMILY_DROPPED logical-shadow'" \
        "'FST_FAMILY_DROPPED score-observations'" \
        "'FST_FAMILY_DROPPED band-song-projection'" \
        "'FST_FAMILY_DROPPED aggregate-ranking-deltas'" \
        > "$LOG_DIR/drop.log"
    if [[ ! -f "$FIXTURE_DIR/drop-process-exit-code" ]]; then
        printf '%s\n' "'FST_ALL_COMMITTED'" >> "$LOG_DIR/drop.log"
    fi
    cp -R "$FIXTURE_DIR/post/." "$POST_DIR/"
    python3 - \
        "$PACKAGE_DIR/manifest.json" \
        "$POST_DIR/startup-image-creation-attestation.json" \
        "$POST_DIR/startup-image-prestart-attestation.json" \
        "$POST_DIR/startup-image-attestation.csv" \
        "$POST_DIR/startup-image-attested-before-start.sha256" \
        "$POST_DIR/startup-database-routing/attestation.json" \
        "$POST_DIR/startup-database-routing/production-target-attestation.csv" \
        "$POST_DIR/startup-database-routing/status.csv" \
        "$POST_DIR/startup-initializer-release.json" <<'PY'
import csv
import hashlib
import json
import sys
from pathlib import Path

(
    manifest_path,
    creation_path,
    prestart_path,
    csv_path,
    marker_path,
    routing_path,
    target_path,
    routing_status_path,
    release_path,
) = map(Path, sys.argv[1:])
manifest_sha256 = hashlib.sha256(manifest_path.read_bytes()).hexdigest()
for path in [creation_path, prestart_path]:
    value = json.loads(path.read_text(encoding="utf-8"))
    value["expectedManifestSha256"] = manifest_sha256
    path.write_text(
        json.dumps(value, sort_keys=True, indent=2) + "\n",
        encoding="utf-8",
    )
creation_sha256 = hashlib.sha256(creation_path.read_bytes()).hexdigest()
prestart_sha256 = hashlib.sha256(prestart_path.read_bytes()).hexdigest()
routing = json.loads(routing_path.read_text(encoding="utf-8"))
routing["acceptedManifestSha256"] = manifest_sha256
routing_path.write_text(
    json.dumps(routing, sort_keys=True, indent=2) + "\n",
    encoding="utf-8",
)
routing_sha256 = hashlib.sha256(routing_path.read_bytes()).hexdigest()
target_sha256 = hashlib.sha256(target_path.read_bytes()).hexdigest()
with routing_status_path.open(newline="", encoding="utf-8") as handle:
    status_reader = csv.DictReader(handle)
    status_fields = status_reader.fieldnames
    status_rows = list(status_reader)
status_rows[0]["manifest_sha256"] = manifest_sha256
with routing_status_path.open("w", newline="", encoding="utf-8") as handle:
    writer = csv.DictWriter(handle, fieldnames=status_fields)
    writer.writeheader()
    writer.writerows(status_rows)
release = json.loads(release_path.read_text(encoding="utf-8"))
release.update({
    "acceptedManifestSha256": manifest_sha256,
    "prestartAttestationSha256": prestart_sha256,
    "databaseRoutingAttestationSha256": routing_sha256,
    "databaseTargetAttestationSha256": target_sha256,
})
release_path.write_text(
    json.dumps(release, sort_keys=True, indent=2) + "\n",
    encoding="utf-8",
)
release_sha256 = hashlib.sha256(release_path.read_bytes()).hexdigest()
with csv_path.open(newline="", encoding="utf-8") as handle:
    reader = csv.DictReader(handle)
    rows = list(reader)
    fields = reader.fieldnames
if len(rows) != 1 or not fields:
    raise SystemExit("fixture startup image attestation is invalid")
rows[0]["creation_attestation_sha256"] = creation_sha256
rows[0]["prestart_attestation_sha256"] = prestart_sha256
rows[0]["database_routing_attestation_sha256"] = routing_sha256
rows[0]["database_target_attestation_sha256"] = target_sha256
rows[0]["initializer_release_sha256"] = release_sha256
with csv_path.open("w", newline="", encoding="utf-8") as handle:
    writer = csv.DictWriter(handle, fieldnames=fields)
    writer.writeheader()
    writer.writerows(rows)
marker_path.write_text(prestart_sha256 + "\n", encoding="utf-8")
PY
    DROP_APP_NAME="fixture_drop_process"
    DROP_CONTROL_FILE="$POST_DIR/drop-process-control.csv"
    DROP_LOCAL_PID="2121"
    DROP_LOCAL_START_TICKS="100"
    DROP_LOCAL_CMD_SHA256="$(printf fixture | sha256sum | awk '{print $1}')"
    DROP_LOCAL_WAIT_COMPLETED=true
    DROP_CONNECT_RELEASED=true
    DROP_SQL_RELEASED=true
    DROP_CONTAINER_PSQL_PID="4242"
    DROP_BACKEND_PID="5252"
    DROP_PROCESS_IDENTITY_CONFIRMED=true
    DROP_SQL_STREAMER_PID="6161"
    DROP_SQL_STREAMER_START_TICKS="400"
    DROP_SQL_STREAMER_CMD_SHA256="$(
        printf streamer | sha256sum | awk '{print $1}'
    )"
    DROP_SQL_STREAMER_WAIT_COMPLETED=true
    python3 - \
        "$PACKAGE_DIR/manifest.json" \
        "$PACKAGE_DIR/drop.sql" \
        "$POST_DIR/drop-sql-stream-proof.json" <<'PY'
import hashlib
import json
import sys
from pathlib import Path

manifest_path, drop_path, output_path = map(Path, sys.argv[1:])
manifest_hash = hashlib.sha256(manifest_path.read_bytes()).hexdigest()
drop_hash = hashlib.sha256(drop_path.read_bytes()).hexdigest()
manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
service_rows = [
    row
    for row in manifest["containers"]
    if row.get("service") == "fstservice"
]
if len(service_rows) != 1:
    raise SystemExit("fixture manifest lacks fstservice")
service = service_rows[0]
proof = {
    "schemaVersion": 1,
    "manifest": {
        "sha256": manifest_hash,
        "bytes": manifest_path.stat().st_size,
        "approvedFstserviceImageId": service["image_id"],
        "approvedFstserviceContainerId": service["container_id"],
        "approvedFstserviceImageReferenceSha256": (
            manifest["productionComposeOwnership"][
                "fstserviceImageReferenceSha256"
            ]
        ),
        "approvedFstserviceNetworks": sorted(
            manifest["containerConfigAttestation"]["services"][
                "fstservice"
            ]["networks"]
        ),
        "approvedPostgresContainerId": (
            manifest["productionDatabaseTarget"]["runtime"]["container_id"]
        ),
        "approvedPostgresSystemIdentifier": (
            manifest["productionDatabaseTarget"]["runtime"][
                "system_identifier"
            ]
        ),
        "approvedProductionDatabaseTarget": (
            manifest["productionDatabaseTarget"]["runtime"]
        ),
        "sealedMemfd": {
            "size": manifest_path.stat().st_size,
            "seals": 15,
            "requiredSeals": 15,
        },
    },
    "dropSql": {
        "sha256": drop_hash,
        "manifestSha256": drop_hash,
        "bytes": drop_path.stat().st_size,
        "sealedMemfd": {
            "size": drop_path.stat().st_size,
            "seals": 15,
            "requiredSeals": 15,
        },
    },
}
output_path.write_text(json.dumps(proof, sort_keys=True), encoding="utf-8")
PY
    load_approved_startup_identity_from_proof \
        "$POST_DIR/drop-sql-stream-proof.json"
    DROP_IMMUTABLE_BUNDLE_SHA256="$(
        sha256sum "$POST_DIR/drop-sql-stream-proof.json" | awk '{print $1}'
    )"
    write_drop_control completed fixture
    if [[ -f "$FIXTURE_DIR/drop-signal-at-launch" ]]; then
        DROP_APP_NAME="fixture_launch_barrier"
        DROP_CONTROL_FILE="$POST_DIR/drop-process-control.csv"
        DROP_LOCAL_PID="$$"
        DROP_LOCAL_START_TICKS="150"
        DROP_LOCAL_CMD_SHA256="$(printf barrier | sha256sum | awk '{print $1}')"
        DROP_LOCAL_WAIT_COMPLETED=false
        DROP_CONNECT_RELEASED=false
        DROP_SQL_RELEASED=false
        DROP_CONTAINER_PSQL_PID=""
        DROP_BACKEND_PID=""
        DROP_PROCESS_ACTIVE=true
        DROP_PROCESS_IDENTITY_CONFIRMED=true
        DROP_SQL_STREAMER_PID=""
        DROP_SQL_STREAMER_START_TICKS=""
        DROP_SQL_STREAMER_CMD_SHA256=""
        DROP_SQL_STREAMER_WAIT_COMPLETED=false
        DROP_IMMUTABLE_BUNDLE_SHA256=""
        write_drop_control launch-barrier-ready fixture-launch
        : > "$POST_DIR/drop-launch-barrier-ready"
        while :; do
            sleep 1
        done
    fi
    if [[ -f "$FIXTURE_DIR/drop-signal-post-connect" ]]; then
        DROP_APP_NAME="fixture_post_connect_barrier"
        DROP_CONTROL_FILE="$POST_DIR/drop-process-control.csv"
        DROP_LOCAL_PID="$$"
        DROP_LOCAL_START_TICKS="175"
        DROP_LOCAL_CMD_SHA256="$(
            printf post-connect | sha256sum | awk '{print $1}'
        )"
        DROP_LOCAL_WAIT_COMPLETED=false
        DROP_CONNECT_RELEASED=true
        DROP_SQL_RELEASED=false
        DROP_CONTAINER_PSQL_PID="4242"
        DROP_BACKEND_PID="5252"
        DROP_PROCESS_ACTIVE=true
        DROP_PROCESS_IDENTITY_CONFIRMED=true
        DROP_SQL_STREAMER_PID=""
        DROP_SQL_STREAMER_START_TICKS=""
        DROP_SQL_STREAMER_CMD_SHA256=""
        DROP_SQL_STREAMER_WAIT_COMPLETED=false
        DROP_IMMUTABLE_BUNDLE_SHA256=""
        write_drop_control post-connect-barrier-ready fixture-post-connect
        : > "$POST_DIR/drop-post-connect-barrier-ready"
        while :; do
            sleep 1
        done
    fi
    if [[ -f "$FIXTURE_DIR/drop-wait-for-signal" ]]; then
        DROP_APP_NAME="fixture_delayed_transaction"
        DROP_CONTROL_FILE="$POST_DIR/drop-process-control.csv"
        DROP_LOCAL_PID="$$"
        DROP_LOCAL_START_TICKS="200"
        DROP_LOCAL_CMD_SHA256="$(printf delayed | sha256sum | awk '{print $1}')"
        DROP_LOCAL_WAIT_COMPLETED=false
        DROP_CONNECT_RELEASED=true
        DROP_SQL_RELEASED=true
        DROP_CONTAINER_PSQL_PID="4242"
        DROP_BACKEND_PID="5252"
        DROP_PROCESS_ACTIVE=true
        write_drop_control active fixture-delay
        : > "$POST_DIR/drop-waiting"
        while :; do
            sleep 1
        done
    fi
    if [[ -f "$FIXTURE_DIR/drop-process-exit-code" ]]; then
        if [[ -f "$FIXTURE_DIR/drop-simulate-active-backend" ]]; then
            DROP_APP_NAME="fixture_active_backend"
            DROP_CONTROL_FILE="$POST_DIR/drop-process-control.csv"
            DROP_LOCAL_PID="3131"
            DROP_LOCAL_START_TICKS="300"
            DROP_LOCAL_CMD_SHA256="$(printf active | sha256sum | awk '{print $1}')"
            DROP_LOCAL_WAIT_COMPLETED=false
            DROP_CONNECT_RELEASED=true
            DROP_SQL_RELEASED=true
            DROP_CONTAINER_PSQL_PID="4242"
            DROP_BACKEND_PID="5252"
            DROP_PROCESS_ACTIVE=true
            write_drop_control active fixture-process-exit
        else
            write_drop_control terminated fixture-process-exit
        fi
        return "$(< "$FIXTURE_DIR/drop-process-exit-code")"
    fi
}

capture_post_relations() {
    local output_file="$1"
    run_expected_capture capture-relations.sql "$output_file"
}

classify_drop_inventory() {
    local input_file="$1"
    python3 - "$input_file" <<'PY'
import csv
import sys

with open(sys.argv[1], newline="", encoding="utf-8") as handle:
    rows = list(csv.DictReader(handle))
if len(rows) != 61:
    print("unknown")
elif all(not row.get("actual_relkind") for row in rows):
    print("committed")
elif all(row.get("actual_relkind") for row in rows):
    print("all-present")
else:
    print("partial")
PY
}

write_drop_control() {
    local state="$1"
    local reason="$2"
    [[ -n "$DROP_CONTROL_FILE" ]] || return 0
    printf 'application_name,local_pid,local_start_ticks,local_cmd_sha256,local_wait_completed,connect_released,sql_released,second_gate_sha256,immutable_bundle_sha256,sql_streamer_pid,sql_streamer_start_ticks,sql_streamer_cmd_sha256,sql_streamer_wait_completed,container_psql_pid,backend_pid,state,reason\n' \
        > "$DROP_CONTROL_FILE" || return 3
    printf '%s,%s,%s,%s,%s,%s,%s,%s,%s,%s,%s,%s,%s,%s,%s,%s,%s\n' \
        "$DROP_APP_NAME" "$DROP_LOCAL_PID" "$DROP_LOCAL_START_TICKS" \
        "$DROP_LOCAL_CMD_SHA256" "$DROP_LOCAL_WAIT_COMPLETED" \
        "$DROP_CONNECT_RELEASED" "$DROP_SQL_RELEASED" \
        "$DROP_SECOND_GATE_SHA256" "$DROP_IMMUTABLE_BUNDLE_SHA256" \
        "$DROP_SQL_STREAMER_PID" "$DROP_SQL_STREAMER_START_TICKS" \
        "$DROP_SQL_STREAMER_CMD_SHA256" \
        "$DROP_SQL_STREAMER_WAIT_COMPLETED" \
        "$DROP_CONTAINER_PSQL_PID" "$DROP_BACKEND_PID" "$state" "$reason" \
        >> "$DROP_CONTROL_FILE" || return 3
    sync -f "$DROP_CONTROL_FILE" || return 3
    return 0
}

control_psql_scalar() {
    local sql="$1"
    timeout --signal=TERM --kill-after=10s 1m \
        docker exec \
        -e PGCONNECT_TIMEOUT=10 \
        -e PGOPTIONS="-c row_security=off" \
        "$PG_CONTAINER" \
        env -u PGHOST -u PGHOSTADDR -u PGPORT \
        -u PGSERVICE -u PGSERVICEFILE \
        psql -X -qAt -v ON_ERROR_STOP=1 \
        "host=$PG_SOCKET_DIR port=$PG_PORT dbname=$PG_DB user=$PG_USER application_name=fst_cleanup_control connect_timeout=10" \
        -c "$sql"
}

backend_rows_for_drop_app() {
    control_psql_scalar \
        "SELECT pid::text FROM pg_catalog.pg_stat_activity WHERE application_name = '$DROP_APP_NAME' AND datname = '$PG_DB' AND usename = '$PG_USER' ORDER BY pid"
}

container_psql_rows_for_drop_app() {
    docker exec -i "$PG_CONTAINER" \
        sh -s -- /proc "$DROP_APP_NAME" \
        < "$PROCESS_MATCHER"
}

terminate_container_psql_exact() {
    local pid="$1"
    [[ "$pid" =~ ^[0-9]+$ ]] || return 1
    docker exec "$PG_CONTAINER" sh -c '
pid="$1"
app="$2"
is_target() {
    exe="$(readlink "/proc/$pid/exe" 2>/dev/null || true)"
    [ "${exe##*/}" = "psql" ] || return 1
    tr "\000" "\n" < "/proc/$pid/cmdline" 2>/dev/null |
        tr " " "\n" |
        grep -F -x -- "application_name=$app" >/dev/null
}
is_target || exit 0
kill -TERM "$pid" 2>/dev/null || true
for _ in 1 2 3 4 5; do
    [ ! -e "/proc/$pid" ] && exit 0
    is_target || exit 0
    sleep 1
done
is_target && kill -KILL "$pid" 2>/dev/null || true
' sh "$pid" "$DROP_APP_NAME"
}

terminate_recorded_local_child() {
    local current_start current_cmd_sha current_cmd_text
    $DROP_LOCAL_WAIT_COMPLETED && return 0
    [[ "$DROP_LOCAL_PID" =~ ^[0-9]+$ ]] || return 1
    if ! kill -0 "$DROP_LOCAL_PID" 2>/dev/null; then
        DROP_LOCAL_WAIT_COMPLETED=true
        return 0
    fi
    current_start="$(
        awk '{print $22}' "/proc/$DROP_LOCAL_PID/stat" 2>/dev/null
    )"
    current_cmd_sha="$(
        sha256sum "/proc/$DROP_LOCAL_PID/cmdline" 2>/dev/null |
            awk '{print $1}'
    )"
    current_cmd_text="$(
        tr '\000' ' ' < "/proc/$DROP_LOCAL_PID/cmdline" 2>/dev/null
    )"
    if [[ "$current_start" != "$DROP_LOCAL_START_TICKS" ]]; then
        DROP_LOCAL_WAIT_COMPLETED=true
        return 0
    fi
    if [[ "$current_cmd_sha" != "$DROP_LOCAL_CMD_SHA256" \
        && "$current_cmd_text" != *"$DROP_APP_NAME"* ]]; then
        return 1
    fi
    DROP_LOCAL_CMD_SHA256="$current_cmd_sha"
    kill -TERM "$DROP_LOCAL_PID" 2>/dev/null || true
    wait "$DROP_LOCAL_PID" 2>/dev/null || true
    DROP_LOCAL_WAIT_COMPLETED=true
    return 0
}

terminate_recorded_sql_streamer() {
    local current_start current_cmd_sha
    $DROP_SQL_STREAMER_WAIT_COMPLETED && return 0
    [[ "$DROP_SQL_STREAMER_PID" =~ ^[0-9]+$ ]] || return 0
    if ! kill -0 "$DROP_SQL_STREAMER_PID" 2>/dev/null; then
        DROP_SQL_STREAMER_WAIT_COMPLETED=true
        return 0
    fi
    current_start="$(
        awk '{print $22}' "/proc/$DROP_SQL_STREAMER_PID/stat" 2>/dev/null
    )"
    current_cmd_sha="$(
        sha256sum "/proc/$DROP_SQL_STREAMER_PID/cmdline" 2>/dev/null |
            awk '{print $1}'
    )"
    if [[ "$current_start" != "$DROP_SQL_STREAMER_START_TICKS" ]]; then
        DROP_SQL_STREAMER_WAIT_COMPLETED=true
        return 0
    fi
    [[ "$current_cmd_sha" == "$DROP_SQL_STREAMER_CMD_SHA256" ]] || return 1
    kill -TERM "$DROP_SQL_STREAMER_PID" 2>/dev/null || true
    wait "$DROP_SQL_STREAMER_PID" 2>/dev/null || true
    DROP_SQL_STREAMER_WAIT_COMPLETED=true
    return 0
}

cleanup_active_drop() {
    local reason="${1:-cleanup}"
    local rows count attempt container_state backend_state
    local errexit_was_set=false
    local control_query_failed=false
    local discovery_ambiguous=false
    local local_cleanup_failed=false
    local streamer_cleanup_failed=false
    [[ $- == *e* ]] && errexit_was_set=true
    $DROP_PROCESS_ACTIVE || return 0
    $DROP_CLEANUP_RUNNING && return 1
    DROP_CLEANUP_RUNNING=true
    set +e

    if $TEST_MODE; then
        if [[ -f "$FIXTURE_DIR/drop-control-query-failure" ]]; then
            : > "$POST_DIR/known-local-client-terminated"
            : > "$POST_DIR/known-container-client-terminated"
            write_drop_control cleanup-incomplete "$reason" || {
                DROP_CLEANUP_RUNNING=false
                $errexit_was_set && set -e || set +e
                return 1
            }
            DROP_CLEANUP_RUNNING=false
            $errexit_was_set && set -e || set +e
            return 1
        fi
        if [[ -f "$FIXTURE_DIR/drop-backend-stays-active" ]]; then
            write_drop_control active "$reason" || {
                DROP_CLEANUP_RUNNING=false
                $errexit_was_set && set -e || set +e
                return 1
            }
            DROP_CLEANUP_RUNNING=false
            $errexit_was_set && set -e || set +e
            return 1
        fi
        if [[ -f "$FIXTURE_DIR/drop-local-pid-reused" ]]; then
            DROP_PROCESS_ACTIVE=false
            write_drop_control terminated-pid-reused "$reason" || {
                DROP_CLEANUP_RUNNING=false
                $errexit_was_set && set -e || set +e
                return 1
            }
            DROP_CLEANUP_RUNNING=false
            $errexit_was_set && set -e || set +e
            return 0
        fi
        DROP_PROCESS_ACTIVE=false
        write_drop_control terminated "$reason" || {
            DROP_CLEANUP_RUNNING=false
            $errexit_was_set && set -e || set +e
            return 1
        }
        DROP_CLEANUP_RUNNING=false
        $errexit_was_set && set -e || set +e
        return 0
    fi

    if [[ ! "$DROP_LOCAL_PID" =~ ^[0-9]+$ \
        && "${DROP_LAUNCHER_PID:-}" =~ ^[0-9]+$ ]]; then
        DROP_LOCAL_PID="$DROP_LAUNCHER_PID"
        DROP_LOCAL_START_TICKS="$(
            awk '{print $22}' "/proc/$DROP_LOCAL_PID/stat" 2>/dev/null
        )"
        DROP_LOCAL_CMD_SHA256="$(
            sha256sum "/proc/$DROP_LOCAL_PID/cmdline" 2>/dev/null |
                awk '{print $1}'
        )"
    fi
    terminate_recorded_sql_streamer || streamer_cleanup_failed=true
    terminate_recorded_local_child || local_cleanup_failed=true

    if ! rows="$(backend_rows_for_drop_app 2>/dev/null)"; then
        control_query_failed=true
        rows=""
    fi
    count="$(grep -c . <<< "$rows" 2>/dev/null || true)"
    if [[ "$count" == "1" ]]; then
        DROP_BACKEND_PID="$rows"
    elif [[ "$count" != "0" ]]; then
        discovery_ambiguous=true
    fi
    if [[ ! "$DROP_CONTAINER_PSQL_PID" =~ ^[0-9]+$ ]]; then
        if ! rows="$(container_psql_rows_for_drop_app 2>/dev/null)"; then
            control_query_failed=true
            rows=""
        fi
        count="$(grep -c . <<< "$rows" 2>/dev/null || true)"
        if [[ "$count" == "1" ]]; then
            DROP_CONTAINER_PSQL_PID="$rows"
        elif [[ "$count" != "0" ]]; then
            discovery_ambiguous=true
        fi
    fi
    write_drop_control cancelling "$reason" || control_query_failed=true

    if [[ -n "$DROP_BACKEND_PID" ]]; then
        control_psql_scalar \
            "SELECT pg_catalog.pg_cancel_backend(pid) FROM pg_catalog.pg_stat_activity WHERE pid = $DROP_BACKEND_PID AND application_name = '$DROP_APP_NAME' AND datname = '$PG_DB' AND usename = '$PG_USER'" \
            >/dev/null 2>&1 || true
    fi
    if [[ -n "$DROP_CONTAINER_PSQL_PID" ]]; then
        terminate_container_psql_exact "$DROP_CONTAINER_PSQL_PID" \
            >/dev/null 2>&1 || true
    fi

    local empty_polls=0 backend_rows container_rows late_pid
    for attempt in 1 2 3 4 5 6 7 8 9 10; do
        if ! backend_rows="$(backend_rows_for_drop_app 2>/dev/null)"; then
            control_query_failed=true
            backend_rows=""
        fi
        if ! container_rows="$(container_psql_rows_for_drop_app 2>/dev/null)"; then
            control_query_failed=true
            container_rows=""
        fi
        count="$(grep -c . <<< "$backend_rows" 2>/dev/null || true)"
        (( count > 1 )) && discovery_ambiguous=true
        while IFS= read -r late_pid; do
            [[ "$late_pid" =~ ^[0-9]+$ ]] || continue
            [[ -n "$DROP_BACKEND_PID" ]] || DROP_BACKEND_PID="$late_pid"
            control_psql_scalar \
                "SELECT pg_catalog.pg_terminate_backend(pid) FROM pg_catalog.pg_stat_activity WHERE pid = $late_pid AND application_name = '$DROP_APP_NAME' AND datname = '$PG_DB' AND usename = '$PG_USER'" \
                >/dev/null 2>&1 || true
        done <<< "$backend_rows"
        count="$(grep -c . <<< "$container_rows" 2>/dev/null || true)"
        (( count > 1 )) && discovery_ambiguous=true
        while IFS= read -r late_pid; do
            [[ "$late_pid" =~ ^[0-9]+$ ]] || continue
            [[ -n "$DROP_CONTAINER_PSQL_PID" ]] \
                || DROP_CONTAINER_PSQL_PID="$late_pid"
            terminate_container_psql_exact "$late_pid" \
                >/dev/null 2>&1 || true
        done <<< "$container_rows"
        if [[ -z "$backend_rows" && -z "$container_rows" ]]; then
            ((empty_polls += 1))
            (( empty_polls >= 3 )) && break
        else
            empty_polls=0
        fi
        sleep 0.5
    done
    if ! rows="$(backend_rows_for_drop_app 2>/dev/null)"; then
        control_query_failed=true
        rows=""
    fi
    backend_state=gone
    $control_query_failed && backend_state=unknown
    [[ -n "$rows" ]] && backend_state=active
    container_state=gone
    if ! rows="$(container_psql_rows_for_drop_app 2>/dev/null)"; then
        control_query_failed=true
        container_state=unknown
    elif [[ -n "$rows" ]]; then
        container_state=active
    fi
    if $control_query_failed || $discovery_ambiguous || $local_cleanup_failed \
        || $streamer_cleanup_failed \
        || [[ "$backend_state" != "gone" || "$container_state" != "gone" ]]; then
        write_drop_control cleanup-incomplete "$reason" || {
            DROP_CLEANUP_RUNNING=false
            $errexit_was_set && set -e || set +e
            return 1
        }
        DROP_CLEANUP_RUNNING=false
        $errexit_was_set && set -e || set +e
        return 1
    fi
    DROP_PROCESS_ACTIVE=false
    write_drop_control terminated "$reason" || {
        DROP_CLEANUP_RUNNING=false
        $errexit_was_set && set -e || set +e
        return 1
    }
    DROP_CLEANUP_RUNNING=false
    $errexit_was_set && set -e || set +e
    return 0
}

run_live_atomic_drop() {
    local status=0 attempt rows
    DROP_APP_NAME="fst_retired_cleanup_$$_$(date +%s)"
    DROP_EXECUTION_STARTED=true
    DROP_RUN_STATUS=0
    [[ "$DROP_APP_NAME" =~ ^[a-z0-9_]+$ ]]
    DROP_CONTROL_FILE="$POST_DIR/drop-process-control.csv"
    DROP_LOCAL_PID=""
    DROP_LOCAL_START_TICKS=""
    DROP_LOCAL_CMD_SHA256=""
    DROP_LOCAL_WAIT_COMPLETED=false
    DROP_CONNECT_RELEASED=false
    DROP_CONTAINER_PSQL_PID=""
    DROP_BACKEND_PID=""
    DROP_PROCESS_IDENTITY_CONFIRMED=false
    DROP_SQL_STREAMER_PID=""
    DROP_SQL_STREAMER_START_TICKS=""
    DROP_SQL_STREAMER_CMD_SHA256=""
    DROP_SQL_STREAMER_WAIT_COMPLETED=false
    DROP_IMMUTABLE_BUNDLE_SHA256=""
    : > "$LOG_DIR/drop.log"
    DROP_PROCESS_ACTIVE=true
    write_drop_control starting launch

    coproc DROP_LAUNCHER {
        IFS= read -r launch_token
        [[ "$launch_token" == "LAUNCH" ]] || exit 125
        exec timeout --signal=TERM --kill-after=30s 25m \
            docker exec -i \
            -e PGCONNECT_TIMEOUT=10 \
            -e PGOPTIONS="-c row_security=off" \
            "$PG_CONTAINER" \
            env -u PGHOST -u PGHOSTADDR -u PGPORT \
            -u PGSERVICE -u PGSERVICEFILE \
            sh -c '
IFS= read -r connect_token
[ "$connect_token" = "CONNECT" ] || exit 126
printf "FST_CONTAINER_PSQL_PID=%s\n" "$$"
exec psql "$@"
' sh \
            "host=$PG_SOCKET_DIR port=$PG_PORT dbname=$PG_DB user=$PG_USER application_name=$DROP_APP_NAME connect_timeout=10" \
            -X -qAt -v ON_ERROR_STOP=1 \
            -v expected_manifest_sha256="$EXPECTED_MANIFEST_SHA256" \
            -P pager=off
    } > "$LOG_DIR/drop.log" 2>&1
    DROP_LOCAL_PID="$DROP_LAUNCHER_PID"
    DROP_LOCAL_START_TICKS="$(
        awk '{print $22}' "/proc/$DROP_LOCAL_PID/stat"
    )"
    DROP_LOCAL_CMD_SHA256="$(
        sha256sum "/proc/$DROP_LOCAL_PID/cmdline" | awk '{print $1}'
    )"
    write_drop_control launch-barrier-ready launch

    local launch_fd="${DROP_LAUNCHER[1]}"
    printf 'LAUNCH\n' >&"$launch_fd"
    local current_cmd=""
    for attempt in {1..100}; do
        current_cmd="$(
            tr '\000' ' ' < "/proc/$DROP_LOCAL_PID/cmdline" 2>/dev/null
        )"
        if [[ "$current_cmd" == *"$DROP_APP_NAME"* ]]; then
            DROP_LOCAL_CMD_SHA256="$(
                sha256sum "/proc/$DROP_LOCAL_PID/cmdline" | awk '{print $1}'
            )"
            write_drop_control connect-barrier-ready launch
            break
        fi
        kill -0 "$DROP_LOCAL_PID" 2>/dev/null || break
        sleep 0.05
    done
    if [[ "$current_cmd" != *"$DROP_APP_NAME"* ]]; then
        cleanup_active_drop "local-launch-identity-failed" || true
        DROP_RUN_STATUS=3
        exec {launch_fd}>&-
        return 0
    fi
    {
        printf 'CONNECT\n'
        printf "SELECT 'FST_BACKEND_PID=' || pg_catalog.pg_backend_pid();\n"
    } >&"$launch_fd"
    DROP_CONNECT_RELEASED=true
    write_drop_control connected-awaiting-identity launch

    for attempt in 1 2 3 4 5 6 7 8 9 10; do
        DROP_CONTAINER_PSQL_PID="$(
            sed -n 's/^FST_CONTAINER_PSQL_PID=//p' "$LOG_DIR/drop.log" |
                head -n 1
        )"
        DROP_BACKEND_PID="$(
            sed -n 's/^FST_BACKEND_PID=//p' "$LOG_DIR/drop.log" |
                head -n 1
        )"
        if [[ "$DROP_CONTAINER_PSQL_PID" =~ ^[0-9]+$ \
            && "$DROP_BACKEND_PID" =~ ^[0-9]+$ ]]; then
            break
        fi
        kill -0 "$DROP_LOCAL_PID" 2>/dev/null || break
        sleep 1
    done

    if [[ ! "$DROP_CONTAINER_PSQL_PID" =~ ^[0-9]+$ \
        || ! "$DROP_BACKEND_PID" =~ ^[0-9]+$ ]]; then
        exec {launch_fd}>&-
        cleanup_active_drop "post-connect-identity-failed" || true
        DROP_RUN_STATUS=3
        return 0
    fi
    local exact_container_rows exact_backend_rows
    exact_container_rows="$(container_psql_rows_for_drop_app)" || {
        exec {launch_fd}>&-
        cleanup_active_drop "post-connect-container-scan-failed" || true
        DROP_RUN_STATUS=3
        return 0
    }
    exact_backend_rows="$(backend_rows_for_drop_app)" || {
        exec {launch_fd}>&-
        cleanup_active_drop "post-connect-backend-scan-failed" || true
        DROP_RUN_STATUS=3
        return 0
    }
    if [[ "$exact_container_rows" != "$DROP_CONTAINER_PSQL_PID" \
        || "$exact_backend_rows" != "$DROP_BACKEND_PID" ]]; then
        exec {launch_fd}>&-
        cleanup_active_drop "post-connect-identity-ambiguous" || true
        DROP_RUN_STATUS=3
        return 0
    fi
    DROP_PROCESS_IDENTITY_CONFIRMED=true
    write_drop_control post-connect-barrier-ready launch

    CURRENT_STAGE="complete post-scratch live safety gate"
    capture_complete_post_scratch_gate
    write_drop_control complete-live-gate-passed launch

    local drop_sql_proof
    drop_sql_proof="$POST_DIR/drop-sql-stream-proof.json"
    rm -f "$drop_sql_proof"
    coproc IMMUTABLE_SQL_STREAMER {
        python3 "$HELPER" stream-verified-manifest-drop-sql \
            --manifest "$PACKAGE_DIR/manifest.json" \
            --expected-manifest-sha256 "$EXPECTED_MANIFEST_SHA256" \
            --drop-sql "$PACKAGE_DIR/drop.sql" \
            --proof "$drop_sql_proof" \
            --wait-for-release
    } >&"$launch_fd"
    DROP_SQL_STREAMER_PID="$IMMUTABLE_SQL_STREAMER_PID"
    DROP_SQL_STREAMER_START_TICKS="$(
        awk '{print $22}' "/proc/$DROP_SQL_STREAMER_PID/stat"
    )"
    DROP_SQL_STREAMER_CMD_SHA256="$(
        sha256sum "/proc/$DROP_SQL_STREAMER_PID/cmdline" | awk '{print $1}'
    )"
    local streamer_ready=false
    for attempt in {1..100}; do
        if [[ -s "$drop_sql_proof" ]]; then
            streamer_ready=true
            break
        fi
        kill -0 "$DROP_SQL_STREAMER_PID" 2>/dev/null || break
        sleep 0.05
    done
    $streamer_ready || {
        cleanup_active_drop "immutable-drop-sql-proof-missing" || true
        DROP_RUN_STATUS=3
        exec {launch_fd}>&-
        return 0
    }
    python3 - "$drop_sql_proof" "$EXPECTED_MANIFEST_SHA256" <<'PY'
import json
import sys
from pathlib import Path

proof = json.loads(Path(sys.argv[1]).read_text(encoding="utf-8"))
manifest = proof.get("manifest", {})
drop_sql = proof.get("dropSql", {})
if manifest.get("sha256") != sys.argv[2]:
    raise SystemExit("immutable manifest proof hash mismatch")
if drop_sql.get("sha256") != drop_sql.get("manifestSha256"):
    raise SystemExit("immutable drop SQL differs from sealed manifest")
for label, value in [("manifest", manifest), ("dropSql", drop_sql)]:
    sealed = value.get("sealedMemfd", {})
    if sealed.get("seals") != sealed.get("requiredSeals"):
        raise SystemExit(f"immutable {label} memfd is not fully sealed")
    if sealed.get("size") != value.get("bytes"):
        raise SystemExit(f"immutable {label} memfd size mismatch")
PY
    load_approved_startup_identity_from_proof "$drop_sql_proof"
    DROP_IMMUTABLE_BUNDLE_SHA256="$(
        sha256sum "$drop_sql_proof" | awk '{print $1}'
    )"
    write_drop_control immutable-drop-sql-ready launch
    local streamer_fd="${IMMUTABLE_SQL_STREAMER[1]}"
    printf 'RELEASE\n' >&"$streamer_fd"
    exec {streamer_fd}>&-
    if wait "$DROP_SQL_STREAMER_PID"; then
        DROP_SQL_STREAMER_WAIT_COMPLETED=true
    else
        status=$?
        DROP_SQL_STREAMER_WAIT_COMPLETED=true
        cleanup_active_drop "immutable-drop-sql-stream-failed-$status" || true
        DROP_RUN_STATUS=3
        exec {launch_fd}>&-
        return 0
    fi
    exec {launch_fd}>&-
    DROP_SQL_RELEASED=true
    write_drop_control sql-released launch

    if wait "$DROP_LOCAL_PID"; then
        status=0
    else
        status=$?
    fi
    DROP_LOCAL_WAIT_COMPLETED=true
    [[ "$DROP_CONTAINER_PSQL_PID" =~ ^[0-9]+$ ]] || \
        DROP_CONTAINER_PSQL_PID="$(
            sed -n 's/^FST_CONTAINER_PSQL_PID=//p' "$LOG_DIR/drop.log" |
                head -n 1
        )"
    [[ "$DROP_BACKEND_PID" =~ ^[0-9]+$ ]] || \
        DROP_BACKEND_PID="$(
            sed -n 's/^FST_BACKEND_PID=//p' "$LOG_DIR/drop.log" |
                head -n 1
        )"
    if [[ ! "$DROP_CONTAINER_PSQL_PID" =~ ^[0-9]+$ \
        || ! "$DROP_BACKEND_PID" =~ ^[0-9]+$ ]]; then
        if cleanup_active_drop "process-identification-failed"; then
            DROP_RUN_STATUS=3
        else
            DROP_RUN_STATUS=3
        fi
        return 0
    fi
    DROP_PROCESS_IDENTITY_CONFIRMED=true
    write_drop_control identified process-exit
    if (( status != 0 )); then
        if cleanup_active_drop "process-exit-$status"; then
            DROP_RUN_STATUS="$status"
        else
            DROP_RUN_STATUS=3
        fi
        return 0
    fi
    if ! rows="$(backend_rows_for_drop_app 2>/dev/null)"; then
        cleanup_active_drop "post-success-control-query-failed" || true
        DROP_RUN_STATUS=3
        return 0
    fi
    if [[ -n "$rows" ]]; then
        cleanup_active_drop "client-exited-backend-active" || true
        DROP_RUN_STATUS=3
        return 0
    fi
    DROP_PROCESS_ACTIVE=false
    write_drop_control completed success
    DROP_RUN_STATUS=0
    return 0
}

reconcile_drop_process() {
    local process_status="$1"
    local mode="${2:-normal}"
    local attempt inventory state="unknown"
    if $TEST_MODE; then
        inventory="$POST_DIR/relations.csv"
        state="$(classify_drop_inventory "$inventory")"
        attempt=1
    else
    if ! reverify_production_compose_project "$POST_DIR/reconcile-compose"; then
        printf 'process_status,reconciliation_state,attempts\n%s,unknown,0\n' \
            "$process_status" > "$POST_DIR/drop-process-status.csv" \
            || return 3
        return 3
    fi
    if ! capture_target_attestation \
        "$POST_DIR/reconcile-production-target-attestation.csv"; then
        printf 'process_status,reconciliation_state,attempts\n%s,unknown,0\n' \
            "$process_status" > "$POST_DIR/drop-process-status.csv" \
            || return 3
        return 3
    fi
    for attempt in 1 2 3; do
        inventory="$POST_DIR/drop-reconciliation-$attempt.csv"
        if capture_post_relations "$inventory"; then
            state="$(classify_drop_inventory "$inventory")"
            [[ "$state" != "partial" ]] && break
        fi
        sleep 2
    done
    fi
    printf 'process_status,reconciliation_state,attempts\n%s,%s,%s\n' \
        "$process_status" "$state" "$attempt" \
        > "$POST_DIR/drop-process-status.csv" || return 3
    case "$state" in
        committed)
            if $DROP_PROCESS_ACTIVE; then
                printf 'ERROR: commit state observed while backend remains active\n' >&2
                return 3
            fi
            printf '%s\n' \
                "'FST_ALL_COMMITTED_RECONCILED_BY_ABSENCE'" \
                >> "$LOG_DIR/drop.log"
            return 0
            ;;
        all-present)
            if $DROP_PROCESS_ACTIVE; then
                printf 'ERROR: all-present is unsafe while launch remains active\n' >&2
                return 3
            fi
            printf 'ERROR: atomic drop did not commit; all objects remain\n' >&2
            [[ "$mode" == "trap" ]] && return 0
            return "$process_status"
            ;;
        partial)
            printf 'ERROR: atomic drop catalog state is partial\n' >&2
            [[ "$mode" == "trap" ]] && return 0
            return 3
            ;;
        unknown|*)
            printf 'ERROR: atomic drop state is ambiguous after process failure\n' >&2
            return 3
            ;;
    esac
}

trap_cleanup_and_reconcile() {
    local reason="$1"
    local process_status="$2"
    local cleanup_status=0 scratch_status=0 startup_status=0
    local reconciliation_status=0
    $TRAP_RECONCILIATION_RUNNING && return 3
    TRAP_RECONCILIATION_RUNNING=true
    if $DROP_PROCESS_ACTIVE; then
        cleanup_active_drop "$reason" || cleanup_status=$?
    fi
    if $SCRATCH_DB_ACTIVE; then
        drop_scratch_database || scratch_status=$?
    fi
    if $STARTUP_CHECK_CONTAINER_ACTIVE; then
        cleanup_startup_check_container || startup_status=$?
    fi
    if $DROP_EXECUTION_STARTED; then
        reconcile_drop_process "$process_status" trap \
            || reconciliation_status=$?
    fi
    if $OUTPUT_READY; then
        printf 'reason,cleanup_status,scratch_status,startup_status,reconciliation_status,backend_active\n%s,%s,%s,%s,%s,%s\n' \
            "$reason" "$cleanup_status" "$scratch_status" "$startup_status" \
            "$reconciliation_status" "$DROP_PROCESS_ACTIVE" \
            > "$POST_DIR/trap-reconciliation.csv" || return 3
    fi
    TRAP_RECONCILIATION_RUNNING=false
    if (( cleanup_status != 0 || scratch_status != 0 || startup_status != 0 \
        || reconciliation_status != 0 )); then
        return 3
    fi
    return 0
}

perform_atomic_drop() {
    local status=0
    if $TEST_MODE; then
        CURRENT_STAGE="complete post-scratch live safety gate"
        capture_complete_post_scratch_gate
        DROP_EXECUTION_STARTED=true
        if execute_fixture; then
            status=0
        else
            status=$?
        fi
    else
        run_live_atomic_drop
        status="$DROP_RUN_STATUS"
    fi
    if (( status == 0 )); then
        printf 'process_status,reconciliation_state,attempts\n0,not-required,0\n' \
            > "$POST_DIR/drop-process-status.csv" || return 3
        return 0
    fi
    if $DROP_PROCESS_ACTIVE; then
        printf 'ERROR: destructive backend/process termination is unconfirmed\n' >&2
        return 3
    fi
    if ! $DROP_PROCESS_IDENTITY_CONFIRMED; then
        printf 'ERROR: destructive process identity was never confirmed\n' >&2
        return 3
    fi
    CURRENT_STAGE="atomic drop process reconciliation"
    reconcile_drop_process "$status"
}

assert_absent_capture() {
    local input_file="$1"
    local label="$2"
    python3 - "$input_file" "$label" <<'PY'
import csv
import sys

with open(sys.argv[1], newline="", encoding="utf-8") as handle:
    present = [
        f"{row['schema']}.{row['name']}"
        for row in csv.DictReader(handle)
        if row.get("actual_relkind")
    ]
if present:
    raise SystemExit(
        f"{sys.argv[2]} retained or recreated: {', '.join(present)}"
    )
PY
}

cleanup_startup_check_container() {
    local container_id
    $STARTUP_CHECK_CONTAINER_ACTIVE || return 0
    container_id="$(
        docker ps -a -q \
            --filter "name=^/${STARTUP_CHECK_CONTAINER_NAME}$"
    )" || return 3
    if [[ -n "$container_id" ]]; then
        [[ "$container_id" != *$'\n'* ]] || return 3
        docker rm -f "$container_id" >/dev/null 2>&1 || return 3
    fi
    STARTUP_CHECK_CONTAINER_ACTIVE=false
    return 0
}

record_startup_database_routing_failure() {
    local reason="$1"
    local status="$2"
    local failure_file="$POST_DIR/startup-database-routing-failure.csv"
    printf 'reason,status,stage,initializer_started\n%s,%s,%s,false\n' \
        "$reason" "$status" "$CURRENT_STAGE" > "$failure_file" || return 3
    sync -f "$failure_file" || return 3
    rm -f "$POST_DIR/startup-initializer-release.json" || return 3
    return 0
}

capture_startup_database_routing_attestation() {
    local startup_attestation="$1"
    local output_dir="$2"
    local target_file="$output_dir/production-target-attestation.csv"
    local routing_file="$output_dir/attestation.json"
    local ids_output status
    local -a ids=()
    mkdir -p "$output_dir" || return 3

    set +e
    capture_target_attestation "$target_file"
    status=$?
    set -e
    if (( status != 0 )); then
        record_startup_database_routing_failure \
            target-attestation "$status" || return 3
        return 3
    fi

    ids_output="$(docker ps -a -q)" || {
        record_startup_database_routing_failure \
            container-inventory 3 || return 3
        return 3
    }
    while IFS= read -r container_id; do
        [[ -n "$container_id" ]] && ids+=("$container_id")
    done <<< "$ids_output"
    if (( ${#ids[@]} == 0 )); then
        record_startup_database_routing_failure \
            empty-container-inventory 3 || return 3
        return 3
    fi

    set +e
    docker inspect "${ids[@]}" \
        | python3 "$HELPER" attest-startup-database-routing \
            --startup-attestation "$startup_attestation" \
            --target-attestation "$target_file" \
            --expected-manifest-sha256 "$EXPECTED_MANIFEST_SHA256" \
            --expected-postgres-container-id \
                "$APPROVED_POSTGRES_CONTAINER_ID" \
            --expected-system-identifier \
                "$APPROVED_POSTGRES_SYSTEM_IDENTIFIER" \
            --expected-host "$APPROVED_DATABASE_HOST" \
            --expected-port "$APPROVED_DATABASE_PORT" \
            --expected-database "$APPROVED_DATABASE_NAME" \
            --expected-user "$APPROVED_DATABASE_USER" \
            --expected-networks-json \
                "$APPROVED_FSTSERVICE_NETWORKS_JSON" \
            --output "$routing_file" \
        2> "$LOG_DIR/startup-database-routing.log"
    status=$?
    set -e
    if (( status != 0 )); then
        record_startup_database_routing_failure \
            alias-or-target-drift "$status" || return 3
        return 3
    fi
    [[ -s "$routing_file" && -s "$target_file" ]] || {
        record_startup_database_routing_failure \
            missing-routing-evidence 3 || return 3
        return 3
    }
    set +e
    python3 - "$routing_file" "$EXPECTED_MANIFEST_SHA256" <<'PY'
import json
import sys
from pathlib import Path

value = json.loads(Path(sys.argv[1]).read_text(encoding="utf-8"))
if (
    value.get("success") is not True
    or value.get("acceptedManifestSha256") != sys.argv[2]
):
    raise SystemExit("startup database routing evidence is not accepted")
PY
    status=$?
    set -e
    if (( status != 0 )); then
        record_startup_database_routing_failure \
            invalid-routing-evidence "$status" || return 3
        return 3
    fi
    chmod 0400 "$routing_file" "$target_file" || return 3
    sync -f "$routing_file" || return 3
    sync -f "$target_file" || return 3
    printf 'status,manifest_sha256\npassed,%s\n' \
        "$EXPECTED_MANIFEST_SHA256" > "$output_dir/status.csv" || return 3
    sync -f "$output_dir/status.csv" || return 3
    return 0
}

run_startup_schema_check() {
    local status=0 resolved_image_id compose_image_ref compose_ref_sha
    local container_id creation_file prestart_file marker_file
    local creation_sha prestart_sha prestart_values inspect_row
    local prestart_actual_image prestart_config_image prestart_state
    local prestart_running prestart_command_sha compose_image_before_create
    local compose_image_before_start source_container_id
    local pinned_database_host pinned_database_ip pinned_database_network
    local pinned_postgres_container_id
    local post_container_id post_actual_image post_config_image post_state
    local post_running exit_code
    local routing_dir routing_file routing_target_file routing_sha
    local routing_target_sha release_file release_sha
    [[ "$APPROVED_FSTSERVICE_IMAGE_ID" =~ ^sha256:[0-9a-f]{64}$ ]] \
        || return 3
    [[ "$APPROVED_FSTSERVICE_CONTAINER_ID" =~ ^[0-9a-f]{64}$ ]] \
        || return 3
    [[ "$APPROVED_FSTSERVICE_IMAGE_REFERENCE_SHA256" \
        =~ ^[0-9a-f]{64}$ ]] || return 3
    [[ "${PROJECT_SERVICE_CONTAINER_IDS[fstservice]}" \
        == "$APPROVED_FSTSERVICE_CONTAINER_ID" ]] || return 3
    resolved_image_id="$(
        docker image inspect \
            --format '{{.Id}}' \
            "$APPROVED_FSTSERVICE_IMAGE_ID"
    )" || return 3
    [[ "$resolved_image_id" == "$APPROVED_FSTSERVICE_IMAGE_ID" ]] \
        || return 3
    compose_image_ref="$(
        compose_command config --format json \
            | python3 -c '
import json
import sys

value = json.load(sys.stdin)
image = str(
    value.get("services", {}).get("fstservice", {}).get("image") or ""
).strip()
if not image:
    raise SystemExit("resolved Compose fstservice image is empty")
print(image)
'
    )" || return 3
    compose_ref_sha="$(
        printf '%s' "$compose_image_ref" | sha256sum | awk '{print $1}'
    )" || return 3
    [[ "$compose_ref_sha" \
        == "$APPROVED_FSTSERVICE_IMAGE_REFERENCE_SHA256" ]] || return 3
    STARTUP_CHECK_CONTAINER_NAME="fst-retired-schema-init-${EXPECTED_MANIFEST_SHA256:0:12}-$$"
    [[ "$STARTUP_CHECK_CONTAINER_NAME" =~ ^[a-z0-9-]+$ ]] || return 3
    if docker inspect "$STARTUP_CHECK_CONTAINER_NAME" >/dev/null 2>&1; then
        return 3
    fi
    creation_file="$POST_DIR/startup-image-creation-attestation.json"
    prestart_file="$POST_DIR/startup-image-prestart-attestation.json"
    marker_file="$POST_DIR/startup-image-attested-before-start.sha256"
    [[ ! -e "$creation_file" && ! -e "$prestart_file" \
        && ! -e "$marker_file" ]] || return 3
    STARTUP_CHECK_CONTAINER_ACTIVE=true
    container_id="$(
        python3 "$HELPER" create-immutable-startup-container \
            --source-container-id "$APPROVED_FSTSERVICE_CONTAINER_ID" \
            --expected-image-id "$APPROVED_FSTSERVICE_IMAGE_ID" \
            --compose-image-reference "$compose_image_ref" \
            --expected-image-reference-sha256 \
                "$APPROVED_FSTSERVICE_IMAGE_REFERENCE_SHA256" \
            --expected-manifest-sha256 "$EXPECTED_MANIFEST_SHA256" \
            --container-name "$STARTUP_CHECK_CONTAINER_NAME" \
            --command-json '["--initialize-schema-only"]' \
            --expected-networks-json \
                "$APPROVED_FSTSERVICE_NETWORKS_JSON" \
            --expected-postgres-container-id \
                "$APPROVED_POSTGRES_CONTAINER_ID" \
            --database-host "$APPROVED_DATABASE_HOST" \
            --output "$creation_file"
    )" || return 3
    [[ "$container_id" =~ ^[0-9a-f]{64}$ ]] || return 3
    chmod 0400 "$creation_file" || return 3
    python3 "$HELPER" attest-immutable-startup-container \
        --source-container-id "$APPROVED_FSTSERVICE_CONTAINER_ID" \
        --expected-image-id "$APPROVED_FSTSERVICE_IMAGE_ID" \
        --compose-image-reference "$compose_image_ref" \
        --expected-image-reference-sha256 \
            "$APPROVED_FSTSERVICE_IMAGE_REFERENCE_SHA256" \
        --expected-manifest-sha256 "$EXPECTED_MANIFEST_SHA256" \
        --container-name "$STARTUP_CHECK_CONTAINER_NAME" \
        --container-id "$container_id" \
        --command-json '["--initialize-schema-only"]' \
        --expected-networks-json "$APPROVED_FSTSERVICE_NETWORKS_JSON" \
        --expected-postgres-container-id \
            "$APPROVED_POSTGRES_CONTAINER_ID" \
        --database-host "$APPROVED_DATABASE_HOST" \
        --output "$prestart_file" || {
            cleanup_startup_check_container || true
            return 3
        }
    chmod 0400 "$prestart_file" || return 3
    creation_sha="$(sha256sum "$creation_file" | awk '{print $1}')" \
        || return 3
    prestart_sha="$(sha256sum "$prestart_file" | awk '{print $1}')" \
        || return 3
    prestart_values="$(
        python3 - "$prestart_file" <<'PY'
import json
import sys
from pathlib import Path

value = json.loads(Path(sys.argv[1]).read_text(encoding="utf-8"))
state = value.get("state", {})
print("|".join([
    value.get("containerId", ""),
    value.get("actualImageId", ""),
    value.get("configuredImage", ""),
    state.get("status", ""),
    str(state.get("running", "")).lower(),
    value.get("commandSha256", ""),
    value.get("composeImageResolvedId", ""),
    value.get("sourceContainerId", ""),
    value.get("databaseHostPin", {}).get("host", ""),
    value.get("databaseHostPin", {}).get("ipAddress", ""),
    value.get("databaseHostPin", {}).get("network", ""),
    value.get("databaseHostPin", {}).get("postgresContainerId", ""),
]))
PY
    )" || return 3
    IFS='|' read -r \
        container_id \
        prestart_actual_image \
        prestart_config_image \
        prestart_state \
        prestart_running \
        prestart_command_sha \
        compose_image_before_start \
        source_container_id \
        pinned_database_host \
        pinned_database_ip \
        pinned_database_network \
        pinned_postgres_container_id \
        <<< "$prestart_values"
    compose_image_before_create="$(
        python3 - "$creation_file" <<'PY'
import json
import sys
from pathlib import Path

print(
    json.loads(Path(sys.argv[1]).read_text(encoding="utf-8")).get(
        "composeImageResolvedId",
        "",
    )
)
PY
    )" || return 3
    [[ "$source_container_id" == "$APPROVED_FSTSERVICE_CONTAINER_ID" \
        && "$prestart_actual_image" == "$APPROVED_FSTSERVICE_IMAGE_ID" \
        && "$prestart_config_image" == "$APPROVED_FSTSERVICE_IMAGE_ID" \
        && "$compose_image_before_create" \
            == "$APPROVED_FSTSERVICE_IMAGE_ID" \
        && "$compose_image_before_start" \
            == "$APPROVED_FSTSERVICE_IMAGE_ID" \
        && "$prestart_state" == "created" \
        && "$prestart_running" == "false" \
        && "$prestart_command_sha" \
            == "$(printf '%s' '["--initialize-schema-only"]' \
                | sha256sum | awk '{print $1}')" \
        && "$pinned_database_host" == "$APPROVED_DATABASE_HOST" \
        && "$pinned_database_ip" =~ ^[0-9]+\.[0-9]+\.[0-9]+\.[0-9]+$ \
        && "$pinned_postgres_container_id" \
            == "$APPROVED_POSTGRES_CONTAINER_ID" \
        && "$APPROVED_FSTSERVICE_NETWORKS_JSON" \
            == *"\"$pinned_database_network\""* ]] || {
            cleanup_startup_check_container || true
            return 3
        }
    inspect_row="$(
        docker inspect --format \
            '{{.Id}}|{{.Image}}|{{.Config.Image}}|{{.State.Status}}|{{.State.Running}}|{{json .Config.Cmd}}' \
            "$container_id"
    )" || {
        cleanup_startup_check_container || true
        return 3
    }
    [[ "$inspect_row" \
        == "$container_id|$APPROVED_FSTSERVICE_IMAGE_ID|$APPROVED_FSTSERVICE_IMAGE_ID|created|false|[\"--initialize-schema-only\"]" ]] \
        || {
            cleanup_startup_check_container || true
            return 3
        }
    printf '%s\n' "$prestart_sha" > "$marker_file" || return 3
    chmod 0400 "$marker_file" || return 3
    sync -f "$creation_file" || return 3
    sync -f "$prestart_file" || return 3
    sync -f "$marker_file" || return 3

    CURRENT_STAGE="post-drop initializer database routing attestation"
    routing_dir="$POST_DIR/startup-database-routing"
    routing_file="$routing_dir/attestation.json"
    routing_target_file="$routing_dir/production-target-attestation.csv"
    capture_startup_database_routing_attestation \
        "$prestart_file" "$routing_dir"
    routing_sha="$(sha256sum "$routing_file" | awk '{print $1}')" \
        || return 3
    routing_target_sha="$(
        sha256sum "$routing_target_file" | awk '{print $1}'
    )" || return 3
    release_file="$POST_DIR/startup-initializer-release.json"
    python3 - \
        "$release_file" \
        "$EXPECTED_MANIFEST_SHA256" \
        "$container_id" \
        "$prestart_sha" \
        "$routing_sha" \
        "$routing_target_sha" <<'PY' || return 3
import json
import os
import sys
from pathlib import Path

output = Path(sys.argv[1])
value = {
    "schemaVersion": 1,
    "acceptedManifestSha256": sys.argv[2],
    "containerId": sys.argv[3],
    "prestartAttestationSha256": sys.argv[4],
    "databaseRoutingAttestationSha256": sys.argv[5],
    "databaseTargetAttestationSha256": sys.argv[6],
    "released": True,
}
with output.open("x", encoding="utf-8") as handle:
    handle.write(json.dumps(value, sort_keys=True, indent=2) + "\n")
    handle.flush()
    os.fsync(handle.fileno())
directory_fd = os.open(output.parent, os.O_RDONLY | os.O_DIRECTORY)
try:
    os.fsync(directory_fd)
finally:
    os.close(directory_fd)
PY
    chmod 0400 "$release_file" || return 3
    sync -f "$release_file" || return 3
    release_sha="$(sha256sum "$release_file" | awk '{print $1}')" \
        || return 3

    CURRENT_STAGE="post-drop initializer execution"
    set +e
    timeout 10m docker start -a "$container_id" \
        > "$LOG_DIR/startup-schema.log" 2>&1
    status=$?
    set -e
    if ! inspect_row="$(
        docker inspect --format \
            '{{.Id}}|{{.Image}}|{{.Config.Image}}|{{.State.Status}}|{{.State.Running}}|{{.State.ExitCode}}' \
            "$container_id"
    )"; then
        printf 'check,status\nstartup-schema,%s\n' "$status" \
            > "$POST_DIR/startup-check.csv"
        cleanup_startup_check_container || true
        return 3
    fi
    IFS='|' read -r \
        post_container_id \
        post_actual_image \
        post_config_image \
        post_state \
        post_running \
        exit_code \
        <<< "$inspect_row"
    printf 'container_name,container_id,source_container_id,manifest_image_id,compose_image_reference_sha256,compose_image_id_before_create,compose_image_id_before_start,prestart_actual_image_id,prestart_config_image,prestart_state,prestart_running,prestart_command_sha256,pinned_database_host,pinned_database_ip,pinned_database_network,pinned_postgres_container_id,poststart_actual_image_id,poststart_config_image,exit_code,creation_attestation_sha256,prestart_attestation_sha256,database_routing_attestation_sha256,database_target_attestation_sha256,initializer_release_sha256\n' \
        > "$POST_DIR/startup-image-attestation.csv" || return 3
    printf '%s,%s,%s,%s,%s,%s,%s,%s,%s,%s,%s,%s,%s,%s,%s,%s,%s,%s,%s,%s,%s,%s,%s,%s\n' \
        "$STARTUP_CHECK_CONTAINER_NAME" \
        "$post_container_id" \
        "$source_container_id" \
        "$APPROVED_FSTSERVICE_IMAGE_ID" \
        "$APPROVED_FSTSERVICE_IMAGE_REFERENCE_SHA256" \
        "$compose_image_before_create" \
        "$compose_image_before_start" \
        "$prestart_actual_image" \
        "$prestart_config_image" \
        "$prestart_state" \
        "$prestart_running" \
        "$prestart_command_sha" \
        "$pinned_database_host" \
        "$pinned_database_ip" \
        "$pinned_database_network" \
        "$pinned_postgres_container_id" \
        "$post_actual_image" \
        "$post_config_image" \
        "$exit_code" \
        "$creation_sha" \
        "$prestart_sha" \
        "$routing_sha" \
        "$routing_target_sha" \
        "$release_sha" \
        >> "$POST_DIR/startup-image-attestation.csv" || return 3
    printf 'check,status\nstartup-schema,%s\n' "$status" \
        > "$POST_DIR/startup-check.csv" || return 3
    cleanup_startup_check_container || return 3
    if (( status != 0 )) \
        || [[ "$post_container_id" != "$container_id" \
            || "$post_state" != "exited" \
            || "$post_running" != "false" \
            || "$exit_code" != "0" \
            || "$post_actual_image" != "$APPROVED_FSTSERVICE_IMAGE_ID" \
            || "$post_config_image" != "$APPROVED_FSTSERVICE_IMAGE_ID" ]]; then
        return 3
    fi
    return 0
}

run_rollback_rehearsal() {
    local rehearsal_sql="$POST_DIR/rollback-rehearsal.sql"
    {
        printf '%s\n' '\set ON_ERROR_STOP on'
        printf '%s\n' 'BEGIN;'
        printf '%s\n' "SET LOCAL lock_timeout = '5s';"
        printf '%s\n' "SET LOCAL statement_timeout = '30s';"
        printf '%s\n' "SET LOCAL transaction_timeout = '5min';"
        printf '%s\n' 'SET LOCAL row_security = off;'
        printf '%s\n' \
            'DO $guards$' \
            'BEGIN' \
            '    IF NOT pg_catalog.pg_try_advisory_xact_lock(5067481511116519501) THEN' \
            "        RAISE EXCEPTION 'The FST schema-DDL maintenance guard is busy';" \
            '    END IF;' \
            '    IF NOT pg_catalog.pg_try_advisory_xact_lock(5067481511116519502) THEN' \
            "        RAISE EXCEPTION 'The retired sequence guard is busy';" \
            '    END IF;' \
            'END' \
            '$guards$;'
        cat "$PACKAGE_DIR/expected-objects.sql"
        cat "$PACKAGE_DIR/catalog-expected.sql"
        cat "$PACKAGE_DIR/retained-expected.sql"
        cat "$ROLLBACK_EXECUTABLE_DIR/rollback-all.sql"
        cat "$PACKAGE_DIR/catalog-assert.sql"
        cat "$PACKAGE_DIR/retained-assert.sql"
        cat "$PACKAGE_DIR/rollback-rehearsal-check.sql"
        printf '%s\n' 'ROLLBACK;'
    } > "$rehearsal_sql"

    local status
    set +e
    psql_stream < "$rehearsal_sql" > "$LOG_DIR/rollback-rehearsal.log" 2>&1
    status=$?
    set -e
    printf 'check,status\nrollback-rehearsal,%s\n' "$status" \
        > "$POST_DIR/rollback-rehearsal.csv"
    if (( status != 0 )); then
        return "$status"
    fi
}

if ! $TEST_MODE; then
    CURRENT_STAGE="immediate pre-execute revalidation"
    capture_preexecute_gate
fi

CURRENT_STAGE="pre-destructive scratch round-trip proof"
run_scratch_roundtrip_proof

if ! $TEST_MODE; then
    CURRENT_STAGE="final production target revalidation"
    reverify_production_compose_project "$OUTPUT_DIR/pre-drop-final/compose"
    capture_target_attestation \
        "$OUTPUT_DIR/pre-drop-final/production-target-attestation.csv"
    [[ "$(
        sha256sum \
            "$OUTPUT_DIR/pre-drop-final/production-target-attestation.csv" \
            | awk '{print $1}'
    )" == "$(
        sha256sum "$CAPTURE_DIR/production-target-attestation.csv" \
            | awk '{print $1}'
    )" ]] || {
        printf 'ERROR: production target drifted after scratch proof\n' >&2
        (exit 3)
    }
fi

CURRENT_STAGE="atomic all-family drop"
perform_atomic_drop

if $TEST_MODE \
    && [[ -f "$FIXTURE_DIR/startup-alias-drift-before-start" ]]; then
    CURRENT_STAGE="post-drop initializer database routing attestation"
    python3 - \
        "$POST_DIR/startup-database-routing/attestation.json" <<'PY'
import json
import sys
from pathlib import Path

path = Path(sys.argv[1])
value = json.loads(path.read_text(encoding="utf-8"))
network = value["attachedNetworks"][0]
value["success"] = False
value["failures"] = [
    f"database alias ownership is not exclusive: {network}"
]
value["aliasOwners"][network].append({
    "containerId": "9" * 64,
    "containerName": "stale-postgres-alias",
    "state": "running",
    "running": True,
    "networkId": "8" * 64,
    "ipAddress": "172.31.0.99",
})
path.write_text(
    json.dumps(value, sort_keys=True, indent=2) + "\n",
    encoding="utf-8",
)
PY
    rm -f "$POST_DIR/startup-database-routing/status.csv"
    record_startup_database_routing_failure alias-or-target-drift 3
    (exit 3)
fi

if ! $TEST_MODE; then
    CURRENT_STAGE="post-drop absence capture"
    capture_post_relations "$POST_DIR/relations.csv"
    assert_absent_capture "$POST_DIR/relations.csv" "post-drop validation"

    CURRENT_STAGE="cleanup image startup schema check"
    reverify_production_compose_project "$POST_DIR/pre-startup-compose"
    run_startup_schema_check
    capture_post_relations "$POST_DIR/startup-relations.csv"
    assert_absent_capture \
        "$POST_DIR/startup-relations.csv" \
        "cleanup image startup validation"

    CURRENT_STAGE="rollback DDL rehearsal"
    run_rollback_rehearsal
    capture_post_relations "$POST_DIR/rehearsal-relations.csv"
    assert_absent_capture \
        "$POST_DIR/rehearsal-relations.csv" \
        "rollback rehearsal validation"

    CURRENT_STAGE="post-action health and parity capture"
    reverify_production_compose_project "$POST_DIR/post-compose"
    run_plain_capture capture-preflight.sql "$POST_DIR/preflight.csv"
    capture_target_attestation "$POST_DIR/production-target-attestation.csv"
    capture_containers "$POST_DIR/containers.csv"
    capture_container_config_attestation \
        "$POST_DIR/container-config-attestation.json"
    capture_health "$POST_DIR/health.csv" "$POST_DIR/health-bodies"
    capture_capacity_guard \
        "$POST_DIR/capacity-guard.json" \
        "$POST_DIR/health.csv" \
        "$POST_DIR/capacity-guard.stdout.txt"
    capture_fingerprints "$POST_DIR/fingerprints.csv" "$POST_DIR/fingerprint-bodies"
    capture_storage "$POST_DIR/storage.csv" "$POST_DIR/relations.csv"
    printf 'container,cpu_percent,memory_usage,memory_percent,block_io,pids\n' \
        > "$POST_DIR/docker-stats.csv"
    docker stats --no-stream \
        --format '{{.Name}},{{.CPUPerc}},{{.MemUsage}},{{.MemPerc}},{{.BlockIO}},{{.PIDs}}' \
        "$PG_CONTAINER" \
        "${PROJECT_SERVICE_CONTAINER_IDS[fstservice]}" \
        "${PROJECT_SERVICE_CONTAINER_IDS[festivalweb]}" \
        "${PROJECT_SERVICE_CONTAINER_IDS[fstworker]}" \
        >> "$POST_DIR/docker-stats.csv" || true
fi

CURRENT_STAGE="post-action validation"
python3 "$HELPER" validate-post \
    --objects "$PACKAGE_DIR/objects.tsv" \
    --before-manifest "$PACKAGE_DIR/manifest.json" \
    --post-dir "$POST_DIR" \
    --output "$POST_DIR/validation.json"

CURRENT_STAGE="final evidence checksums"
write_rollback_instructions
(
    cd "$OUTPUT_DIR"
    find . -type f ! -name package-checksums.sha256 -print0 \
        | sort -z \
        | xargs -0 sha256sum > package-checksums.sha256
)
printf 'Mode: execute\n'
printf 'Manifest SHA-256: %s\n' "$MANIFEST_SHA256"
printf 'Decision: cleanup completed and post-action validation passed\n'
printf 'Evidence: %s\n' "$OUTPUT_DIR"
