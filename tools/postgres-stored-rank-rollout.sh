#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd -P)"
REPO_ROOT="$(cd -- "$SCRIPT_DIR/.." && pwd -P)"
TOOL_PROJECT="$REPO_ROOT/tools/FstStoredRankRollout/FstStoredRankRollout.csproj"
TOOL_DLL="$REPO_ROOT/tools/FstStoredRankRollout/bin/Release/net9.0/FstStoredRankRollout.dll"
TRUE_OVERLAY="$REPO_ROOT/deploy/rollout/stored-rank-filtered-reads/compose.true.yml"
FALSE_OVERLAY="$REPO_ROOT/deploy/rollout/stored-rank-filtered-reads/compose.false.yml"
RECOVERY_OVERLAY="$REPO_ROOT/deploy/rollout/stored-rank-filtered-reads/compose.recovery.yml"

ACTION="${1:-help}"
if [[ $# -gt 0 ]]; then
    shift
fi

COMPOSE_DIR="${COMPOSE_DIR:-/home/sfenton/Docker/FestivalServiceTracker}"
BASE_COMPOSE_FILE="${BASE_COMPOSE_FILE:-$COMPOSE_DIR/docker-compose.yml}"
FST_STORED_RANK_EVIDENCE_ROOT="${FST_STORED_RANK_EVIDENCE_ROOT:-}"
EVIDENCE_DIR="${EVIDENCE_DIR:-}"
EXPECTED_PUBLISHED_SCRAPE_ID="${EXPECTED_PUBLISHED_SCRAPE_ID:-}"
EXPECTED_FSTSERVICE_IMAGE="${EXPECTED_FSTSERVICE_IMAGE:-}"
EXPECTED_FST_EVIDENCE_DEVICE="${EXPECTED_FST_EVIDENCE_DEVICE:-}"
EXPECTED_FST_EVIDENCE_FSTYPE="${EXPECTED_FST_EVIDENCE_FSTYPE:-}"
REQUESTED_BASE_URL="${BASE_URL:-}"
BASE_URL=""
WEB_BASE_URL="${WEB_BASE_URL:-http://127.0.0.1:3001}"
POSTGRES_CONTAINER="${POSTGRES_CONTAINER:-fst-postgres}"
ROLLOUT_SEED="${ROLLOUT_SEED:-20260804}"
WARM_REQUEST_STARTS_PER_SECOND="${WARM_REQUEST_STARTS_PER_SECOND:-80}"
PUBLIC_PATH_CONNECT_TIMEOUT_SECONDS="${PUBLIC_PATH_CONNECT_TIMEOUT_SECONDS:-2}"
PUBLIC_PATH_MAX_TIME_SECONDS="${PUBLIC_PATH_MAX_TIME_SECONDS:-5}"
PUBLIC_PATH_TOTAL_TIMEOUT_SECONDS="${PUBLIC_PATH_TOTAL_TIMEOUT_SECONDS:-180}"
PUBLIC_PATH_RETRY_DELAY_SECONDS="${PUBLIC_PATH_RETRY_DELAY_SECONDS:-2}"
DOCKER_QUERY_TIMEOUT_SECONDS="${DOCKER_QUERY_TIMEOUT_SECONDS:-15}"
DOCKER_RECREATE_TIMEOUT_SECONDS="${DOCKER_RECREATE_TIMEOUT_SECONDS:-120}"
ALLOW_SERVICE_RECREATE="${ALLOW_SERVICE_RECREATE:-}"
FST_STORED_RANK_SERVICE_IMAGE=""
PINNED_SERVICE_IMAGE_ID=""
PINNED_SERVICE_CONFIGURED_TAG=""
PINNED_WORKER_CONTAINER_ID=""
PINNED_WORKER_IMAGE_REFERENCE=""
PINNED_WORKER_IMAGE_ID=""
PINNED_WORKER_CONTAINER_STATUS=""
PINNED_WORKER_CONTAINER_STATE=""
PINNED_POSTGRES_CONTAINER_ID=""
PINNED_POSTGRES_IMAGE_REFERENCE=""
PINNED_POSTGRES_IMAGE_ID=""
PINNED_POSTGRES_NETWORK_NAMES=""
PINNED_POSTGRES_NETWORK_ALIASES=""
PINNED_POSTGRES_SERVER_ADDRESSES=""
PINNED_POSTGRES_NETWORK_BINDINGS_JSON=""
SERVICE_DB_HOST=""
SERVICE_DB_PORT=""
SERVICE_DB_NAME=""
SERVICE_DB_USERNAME=""
SERVICE_DB_CONNECTION_STRING=""
EVIDENCE_MOUNT_TARGET=""
EVIDENCE_MOUNT_SOURCE=""
EVIDENCE_MOUNT_FSTYPE=""
ROLLOUT_MANIFEST_FINGERPRINT=""
LAST_ROLE_VERIFICATION_PATH=""
LAST_ROLLBACK_VERIFICATION_PATH=""
LAST_RECOVERY_VERIFICATION_PATH=""
LAST_FINAL_VERIFICATION_PATH=""
ACTIVE_BLOCK_CONTAINER_ID=""
ACTIVE_BLOCK_VARIANT=""
EXPECTED_SERVICE_READ_ONLY_STARTUP=false
LAST_SERVICE_INFO_JSON=""
EXPECTED_SERVICE_CONTAINER_ID=""
EXPECTED_SERVICE_CONTAINER_HOSTNAME=""
EXPECTED_SERVICE_INSTANCE_NONCE=""
PREVIOUS_SERVICE_INSTANCE_NONCE=""
PINNED_RECOVERY_SERVICE_ID=""
rollout_lock_held=0
PROVISIONAL_ANALYSIS_STATUS=0
ROLLOUT_FAILURE_PHASE=""
rollout_mutation_attempted=0
mutated_service=0

usage() {
    cat <<'EOF'
Usage: tools/postgres-stored-rank-rollout.sh <validate|prepare|run|rollback>

validate
  Builds the rollout tool, runs its self-test, and statically validates both
  compose overlays. It does not connect to live services.

prepare
  Future operator action. Runs the bounded read-only post-cleanup preflight,
  deterministic manifest generation, and exact InstrumentDatabase row parity.

run
  Future operator action. Requires ALLOW_SERVICE_RECREATE=YES. Performs the
  service-only false/true API ABBA, deterministic cold/warm c1/c8 benchmark
  schedule, resource capture, analysis, and an automatic false rollback.

rollback
  Recreates only fstservice with the false override and verifies service,
  worker-role separation, festivalweb shell, and festivalweb API proxy.

Required for prepare/run:
  FST_STORED_RANK_CONNECTION_STRING  SELECT+TEMP-only PostgreSQL connection
  FST_STORED_RANK_EVIDENCE_ROOT      Under the 4 TB FST evidence directory
  EVIDENCE_DIR                       One run directory under that root
  EXPECTED_PUBLISHED_SCRAPE_ID       Cleanup scrape that is complete/published
  EXPECTED_FSTSERVICE_IMAGE          Reviewed tag@sha256:<64-hex> service image
  EXPECTED_FST_EVIDENCE_DEVICE       Reviewed /mnt/docker-storage source device
  EXPECTED_FST_EVIDENCE_FSTYPE       Reviewed /mnt/docker-storage filesystem

Optional:
  COMPOSE_DIR, BASE_COMPOSE_FILE, WEB_BASE_URL,
  POSTGRES_CONTAINER, ROLLOUT_SEED, WARM_REQUEST_STARTS_PER_SECOND,
  PUBLIC_PATH_CONNECT_TIMEOUT_SECONDS, PUBLIC_PATH_MAX_TIME_SECONDS,
  PUBLIC_PATH_TOTAL_TIMEOUT_SECONDS, PUBLIC_PATH_RETRY_DELAY_SECONDS,
  DOCKER_QUERY_TIMEOUT_SECONDS, DOCKER_RECREATE_TIMEOUT_SECONDS.

BASE_URL is derived from the exact inspected fstservice 8080/tcp host binding.
If supplied, it must exactly match that derived loopback endpoint.
EOF
}

require_command() {
    command -v "$1" >/dev/null 2>&1 || {
        printf 'ERROR: required command not found: %s\n' "$1" >&2
        exit 1
    }
}

require_rollout_commands() {
    local command
    for command in dotnet docker python3 realpath curl grep df timeout findmnt flock sha256sum base64; do
        require_command "$command"
    done
}

run_docker_query_bounded() {
    timeout \
        --kill-after=2s \
        "${DOCKER_QUERY_TIMEOUT_SECONDS}s" \
        docker "$@"
}

run_docker_recreate_bounded() {
    timeout \
        --kill-after=2s \
        "${DOCKER_RECREATE_TIMEOUT_SECONDS}s" \
        docker "$@"
}

run_findmnt() {
    findmnt "$@"
}

verify_evidence_mount() {
    local require_writable="${1:-true}"
    local mount_json mount_values mount_options
    if [[ -z "$EXPECTED_FST_EVIDENCE_DEVICE" \
        || -z "$EXPECTED_FST_EVIDENCE_FSTYPE" ]]; then
        printf 'ERROR: expected FST evidence device and filesystem are required\n' >&2
        return 64
    fi
    if ! mount_json="$(
        run_findmnt \
            --json \
            --target /mnt/docker-storage \
            --output TARGET,SOURCE,FSTYPE,OPTIONS
    )"; then
        printf 'ERROR: /mnt/docker-storage is not an active mount\n' >&2
        return 1
    fi
    if ! mount_values="$(
        python3 -c '
import json
import re
import sys
rows = json.load(sys.stdin).get("filesystems") or []
if len(rows) != 1:
    raise SystemExit(1)
row = rows[0]
print("\t".join([
    str(row.get("target") or ""),
    str(row.get("source") or ""),
    str(row.get("fstype") or ""),
    str(row.get("options") or ""),
]))
' <<<"$mount_json"
    )"; then
        printf 'ERROR: unable to parse FST evidence mount\n' >&2
        return 1
    fi
    IFS=$'\t' read -r EVIDENCE_MOUNT_TARGET EVIDENCE_MOUNT_SOURCE \
        EVIDENCE_MOUNT_FSTYPE mount_options <<<"$mount_values"
    if [[ "$EVIDENCE_MOUNT_TARGET" != "/mnt/docker-storage" \
        || "$EVIDENCE_MOUNT_SOURCE" != "$EXPECTED_FST_EVIDENCE_DEVICE" \
        || "$EVIDENCE_MOUNT_FSTYPE" != "$EXPECTED_FST_EVIDENCE_FSTYPE" ]]; then
        printf 'ERROR: FST evidence mount binding mismatch: %s %s %s\n' \
            "$EVIDENCE_MOUNT_TARGET" \
            "$EVIDENCE_MOUNT_SOURCE" \
            "$EVIDENCE_MOUNT_FSTYPE" >&2
        return 1
    fi
    if [[ "$require_writable" == "true" ]]; then
        case ",$mount_options," in
            *,rw,*) ;;
            *)
                printf 'ERROR: FST evidence mount is not read-write\n' >&2
                return 1
                ;;
        esac
    fi
}

acquire_rollout_lock() {
    local lock_file="${ROLLOUT_TEST_LOCK_FILE:-/mnt/docker-storage/Docker/FestivalServiceTracker/fst-data/evidence/.stored-rank-filtered-reads.lock}"
    exec 9>>"$lock_file"
    if ! flock -n 9; then
        printf 'ERROR: another stored-rank rollout owns %s\n' "$lock_file" >&2
        return 1
    fi
    rollout_lock_held=1
    printf 'pid=%s evidence=%s started=%s\n' \
        "$$" \
        "$EVIDENCE_DIR" \
        "$(date -u +%Y-%m-%dT%H:%M:%SZ)" \
        >&9
}

release_rollout_lock() {
    if (( rollout_lock_held != 0 )); then
        flock -u 9
        exec 9>&-
        rollout_lock_held=0
    fi
}

require_unfinalized_run_directory() {
    local path
    for path in \
        "$EVIDENCE_DIR/acceptance.json" \
        "$EVIDENCE_DIR/analysis-provisional.json" \
        "$EVIDENCE_DIR/rollback-evidence.jsonl"; do
        if [[ -e "$path" ]]; then
            printf 'ERROR: rollout evidence directory already contains final-run state: %s\n' \
                "$path" >&2
            return 1
        fi
    done
}

validate_docker_timeouts() {
    if [[ ! "$DOCKER_QUERY_TIMEOUT_SECONDS" =~ ^[0-9]+$ ]] \
        || [[ ! "$DOCKER_RECREATE_TIMEOUT_SECONDS" =~ ^[0-9]+$ ]] \
        || (( DOCKER_QUERY_TIMEOUT_SECONDS < 1 )) \
        || (( DOCKER_RECREATE_TIMEOUT_SECONDS < 1 )); then
        printf 'ERROR: Docker command timeouts must be positive integers\n' >&2
        return 64
    fi
}

build_tool() {
    dotnet build "$TOOL_PROJECT" -c Release --nologo --verbosity quiet
}

run_tool() {
    dotnet "$TOOL_DLL" "$@"
}

require_evidence_configuration() {
    local require_existing="${1:-false}"
    local require_writable="${2:-true}"
    if [[ -z "$FST_STORED_RANK_EVIDENCE_ROOT" || -z "$EVIDENCE_DIR" ]]; then
        printf 'ERROR: FST_STORED_RANK_EVIDENCE_ROOT and EVIDENCE_DIR are required\n' >&2
        exit 64
    fi
    verify_evidence_mount "$require_writable"
    export FST_STORED_RANK_EVIDENCE_ROOT
    local resolved_root resolved_run required_base
    required_base="$(realpath -m /mnt/docker-storage/Docker/FestivalServiceTracker/fst-data/evidence)"
    resolved_root="$(realpath -m "$FST_STORED_RANK_EVIDENCE_ROOT")"
    resolved_run="$(realpath -m "$EVIDENCE_DIR")"
    case "$resolved_root/" in
        "$required_base/"*) ;;
        *)
            printf 'ERROR: evidence root must remain on the configured 4 TB evidence path\n' >&2
            exit 64
            ;;
    esac
    case "$resolved_run/" in
        "$resolved_root/"*) ;;
        *)
            printf 'ERROR: EVIDENCE_DIR must remain under FST_STORED_RANK_EVIDENCE_ROOT\n' >&2
            exit 64
            ;;
    esac
    EVIDENCE_DIR="$resolved_run"
    if [[ "$require_existing" == "true" ]]; then
        if [[ ! -d "$EVIDENCE_DIR" ]]; then
            printf 'ERROR: standalone rollback evidence directory does not exist\n' >&2
            exit 64
        fi
    else
        mkdir -p "$EVIDENCE_DIR"
    fi
}

capture_worker_pin() {
    local worker_id observed worker_stored worker_read_only worker_published
    local worker_postgres_read_only
    local worker_started worker_finished worker_restarts
    worker_id="$(get_compose_container_id fstworker)" || return 1
    if [[ -z "$worker_id" ]]; then
        printf 'ERROR: fstworker container is not running\n' >&2
        return 1
    fi
    observed="$(
        run_docker_query_bounded \
            inspect \
            --format '{{.Image}}|{{.Config.Image}}|{{.State.Status}}|{{.State.StartedAt}}|{{.State.FinishedAt}}|{{.RestartCount}}' \
            "$worker_id"
    )" || return 1
    IFS='|' read -r PINNED_WORKER_IMAGE_ID PINNED_WORKER_IMAGE_REFERENCE \
        PINNED_WORKER_CONTAINER_STATUS worker_started worker_finished \
        worker_restarts <<<"$observed"
    PINNED_WORKER_CONTAINER_STATE="$PINNED_WORKER_CONTAINER_STATUS|$worker_started|$worker_finished|$worker_restarts"
    worker_stored="$(read_container_env_value \
        "$worker_id" \
        Features__UseStoredSoloProjectionRanksForFilteredReads)" || return 1
    worker_read_only="$(read_container_env_value \
        "$worker_id" \
        Scraper__RolloutReadOnlyStartup)" || return 1
    worker_published="$(read_container_env_value \
        "$worker_id" \
        Features__UsePublishedScopeSources)" || return 1
    worker_postgres_read_only="$(read_container_env_value \
        "$worker_id" \
        Scraper__RolloutPostgresReadOnly)" || return 1
    if [[ "$PINNED_WORKER_CONTAINER_STATUS" != "exited" \
        && "$PINNED_WORKER_CONTAINER_STATUS" != "created" ]]; then
        printf 'ERROR: fstworker must be stopped (exited/created) for rollout\n' >&2
        return 1
    fi
    if [[ "$worker_stored" != "false" \
        || "$worker_read_only" != "false" \
        || "$worker_published" != "false" \
        || "$worker_postgres_read_only" != "false" ]]; then
        printf 'ERROR: fstworker false role flags are not enforced\n' >&2
        return 1
    fi
    PINNED_WORKER_CONTAINER_ID="$worker_id"
}

verify_worker_pin() {
    local worker_id observed worker_stored worker_read_only worker_published
    local worker_postgres_read_only
    worker_id="$(get_compose_container_id fstworker)" || return 1
    if [[ "$worker_id" != "$PINNED_WORKER_CONTAINER_ID" ]]; then
        printf 'ERROR: fstworker container identity changed during rollout\n' >&2
        return 1
    fi
    observed="$(
        run_docker_query_bounded \
            inspect \
            --format '{{.Image}}|{{.Config.Image}}|{{.State.Status}}|{{.State.StartedAt}}|{{.State.FinishedAt}}|{{.RestartCount}}' \
            "$worker_id"
    )" || return 1
    if [[ "$observed" != \
        "$PINNED_WORKER_IMAGE_ID|$PINNED_WORKER_IMAGE_REFERENCE|$PINNED_WORKER_CONTAINER_STATE" ]]; then
        printf 'ERROR: fstworker image or status changed during rollout\n' >&2
        return 1
    fi
    worker_stored="$(read_container_env_value \
        "$worker_id" \
        Features__UseStoredSoloProjectionRanksForFilteredReads)" || return 1
    worker_read_only="$(read_container_env_value \
        "$worker_id" \
        Scraper__RolloutReadOnlyStartup)" || return 1
    worker_published="$(read_container_env_value \
        "$worker_id" \
        Features__UsePublishedScopeSources)" || return 1
    worker_postgres_read_only="$(read_container_env_value \
        "$worker_id" \
        Scraper__RolloutPostgresReadOnly)" || return 1
    if [[ "$worker_stored" != "false" \
        || "$worker_read_only" != "false" \
        || "$worker_published" != "false" \
        || "$worker_postgres_read_only" != "false" ]]; then
        printf 'ERROR: fstworker role flags changed during rollout\n' >&2
        return 1
    fi
}

resolve_pinned_service_image() {
    local require_running_match="${1:-true}"
    local configured_tag service_id running_image_id running_image_ref expected_image_id
    if [[ -z "$EXPECTED_FSTSERVICE_IMAGE" ]]; then
        printf 'ERROR: EXPECTED_FSTSERVICE_IMAGE is required\n' >&2
        return 64
    fi
    if ! python3 -c '
import re
import sys
value = sys.argv[1]
if not re.fullmatch(r"[^@\s]+:[^@/\s]+@sha256:[0-9a-f]{64}", value):
    raise SystemExit(1)
' "$EXPECTED_FSTSERVICE_IMAGE"; then
        printf 'ERROR: expected service image must be immutable tag@sha256 digest\n' >&2
        return 64
    fi

    PINNED_SERVICE_CONFIGURED_TAG="${EXPECTED_FSTSERVICE_IMAGE%@sha256:*}"
    if ! configured_tag="$(
        run_docker_query_bounded compose \
            --project-directory "$COMPOSE_DIR" \
            -f "$BASE_COMPOSE_FILE" \
            config --format json \
            | python3 -c '
import json
import re
import sys
print(json.load(sys.stdin)["services"]["fstservice"]["image"])
'
    )"; then
        printf 'ERROR: unable to resolve configured fstservice image tag\n' >&2
        return 1
    fi
    if [[ "$configured_tag" != "$PINNED_SERVICE_CONFIGURED_TAG" ]]; then
        printf 'ERROR: reviewed image tag does not match compose: %s != %s\n' \
            "$PINNED_SERVICE_CONFIGURED_TAG" \
            "$configured_tag" >&2
        return 1
    fi

    service_id="$(
        run_docker_query_bounded compose \
            --project-directory "$COMPOSE_DIR" \
            -f "$BASE_COMPOSE_FILE" \
            ps -q fstservice
    )" || service_id=""
    running_image_id=""
    running_image_ref=""
    if [[ -n "$service_id" ]]; then
        running_image_id="$(
            run_docker_query_bounded inspect --format '{{.Image}}' "$service_id"
        )" || running_image_id=""
        running_image_ref="$(
            run_docker_query_bounded inspect --format '{{.Config.Image}}' "$service_id"
        )" || running_image_ref=""
    fi
    if [[ "$require_running_match" == "true" \
        && ( -z "$running_image_id" || -z "$running_image_ref" ) ]]; then
        printf 'ERROR: unable to inspect running fstservice image\n' >&2
        return 1
    fi
    if ! expected_image_id="$(
        run_docker_query_bounded \
            image inspect \
            --format '{{.Id}}' \
            "$EXPECTED_FSTSERVICE_IMAGE"
    )"; then
        printf 'ERROR: reviewed image digest is not available locally\n' >&2
        return 1
    fi
    if [[ ! "$expected_image_id" =~ ^sha256:[0-9a-f]{64}$ ]]; then
        printf 'ERROR: reviewed image resolved to an invalid image ID\n' >&2
        return 1
    fi
    if [[ "$require_running_match" == "true" \
        && "$running_image_id" != "$expected_image_id" ]]; then
        printf 'ERROR: running fstservice image does not match reviewed digest\n' >&2
        return 1
    fi
    if ! capture_worker_pin; then
        printf 'ERROR: unable to capture fstworker rollout pin\n' >&2
        return 1
    fi

    FST_STORED_RANK_SERVICE_IMAGE="$EXPECTED_FSTSERVICE_IMAGE"
    PINNED_SERVICE_IMAGE_ID="$expected_image_id"
    export FST_STORED_RANK_SERVICE_IMAGE

    if [[ -n "$EVIDENCE_DIR" ]]; then
        if ! python3 -c '
import json
import pathlib
import sys
path = pathlib.Path(sys.argv[1])
payload = {
    "configuredTag": sys.argv[2],
    "pinnedReference": sys.argv[3],
    "pinnedImageId": sys.argv[4],
    "preMutationContainerImageReference": sys.argv[5],
    "preMutationContainerImageId": sys.argv[6],
    "workerContainerId": sys.argv[7],
    "workerImageReference": sys.argv[8],
    "workerImageId": sys.argv[9],
    "workerContainerStatus": sys.argv[10],
    "workerContainerState": sys.argv[11],
    "evidenceMountTarget": sys.argv[12],
    "evidenceMountSource": sys.argv[13],
    "evidenceMountFileSystem": sys.argv[14],
}
path.write_text(json.dumps(payload, indent=2) + "\n")
' \
            "$EVIDENCE_DIR/image-pin.json" \
            "$configured_tag" \
            "$FST_STORED_RANK_SERVICE_IMAGE" \
            "$PINNED_SERVICE_IMAGE_ID" \
            "$running_image_ref" \
            "$running_image_id" \
            "$PINNED_WORKER_CONTAINER_ID" \
            "$PINNED_WORKER_IMAGE_REFERENCE" \
            "$PINNED_WORKER_IMAGE_ID" \
            "$PINNED_WORKER_CONTAINER_STATUS" \
            "$PINNED_WORKER_CONTAINER_STATE" \
            "$EVIDENCE_MOUNT_TARGET" \
            "$EVIDENCE_MOUNT_SOURCE" \
            "$EVIDENCE_MOUNT_FSTYPE"; then
            printf 'ERROR: unable to write image pin evidence\n' >&2
            return 1
        fi
    fi
}

resolve_database_target_binding() {
    local write_evidence="${1:-true}"
    local compose_json target_values compose_postgres_id inspected_postgres_id
    local observed_image postgres_networks service_networks network_values service_id
    local network_name network_id network_members_json member_entry member_id
    local member_name member_networks
    local network_member_bindings="" network_member_values binding_json
    local -a network_member_ids
    if ! compose_json="$(
        run_docker_query_bounded compose \
            --project-directory "$COMPOSE_DIR" \
            -f "$BASE_COMPOSE_FILE" \
            config --format json
    )"; then
        printf 'ERROR: unable to resolve Compose database target\n' >&2
        return 1
    fi
    if ! target_values="$(
        python3 -c '
import base64
import json
import sys
config = json.load(sys.stdin)
environment = config["services"]["fstservice"].get("environment") or {}
value = environment.get("ConnectionStrings__PostgreSQL") or ""
pairs = {}
for item in value.split(";"):
    if "=" not in item:
        continue
    key, raw = item.split("=", 1)
    pairs[key.strip().lower()] = raw.strip()
host = pairs.get("host", "")
port = pairs.get("port", "5432")
database = pairs.get("database", "")
username = pairs.get("username", pairs.get("user id", ""))
if not host or not database or not username or not port.isdigit():
    raise SystemExit(1)
encoded = base64.b64encode(value.encode("utf-8")).decode("ascii")
print("\t".join([host, port, database, username, encoded]))
' <<<"$compose_json"
    )"; then
        printf 'ERROR: service PostgreSQL target is missing or invalid\n' >&2
        return 1
    fi
    local connection_string_base64
    IFS=$'\t' read -r SERVICE_DB_HOST SERVICE_DB_PORT SERVICE_DB_NAME \
        SERVICE_DB_USERNAME connection_string_base64 <<<"$target_values"
    if ! SERVICE_DB_CONNECTION_STRING="$(
        printf '%s' "$connection_string_base64" | base64 --decode
    )" || [[ -z "$SERVICE_DB_CONNECTION_STRING" ]]; then
        printf 'ERROR: unable to retain service PostgreSQL visibility probe target\n' >&2
        return 1
    fi
    compose_postgres_id="$(
        run_docker_query_bounded compose \
            --project-directory "$COMPOSE_DIR" \
            -f "$BASE_COMPOSE_FILE" \
            ps --all -q postgres
    )" || return 1
    inspected_postgres_id="$(
        run_docker_query_bounded inspect --format '{{.Id}}' "$POSTGRES_CONTAINER"
    )" || return 1
    if [[ -z "$compose_postgres_id" \
        || "$compose_postgres_id" != "$inspected_postgres_id" ]]; then
        printf 'ERROR: POSTGRES_CONTAINER does not match production Compose postgres\n' >&2
        return 1
    fi
    PINNED_POSTGRES_CONTAINER_ID="$inspected_postgres_id"
    observed_image="$(
        run_docker_query_bounded \
            inspect \
            --format '{{.Image}}|{{.Config.Image}}' \
            "$PINNED_POSTGRES_CONTAINER_ID"
    )" || return 1
    PINNED_POSTGRES_IMAGE_ID="${observed_image%%|*}"
    PINNED_POSTGRES_IMAGE_REFERENCE="${observed_image#*|}"
    if [[ ! "$PINNED_POSTGRES_IMAGE_ID" =~ ^sha256:[0-9a-f]{64}$ ]]; then
        printf 'ERROR: production Postgres image ID is invalid\n' >&2
        return 1
    fi
    postgres_networks="$(
        run_docker_query_bounded \
            inspect \
            --format '{{json .NetworkSettings.Networks}}' \
            "$PINNED_POSTGRES_CONTAINER_ID"
    )" || return 1
    service_id="$(get_compose_container_id fstservice)" || return 1
    service_networks="$(
        run_docker_query_bounded \
            inspect \
            --format '{{json .NetworkSettings.Networks}}' \
            "$service_id"
    )" || return 1
    if ! network_values="$(
        python3 -c '
import json
import sys
postgres = json.loads(sys.argv[1])
service = json.loads(sys.argv[2])
host = sys.argv[3]
shared = sorted(set(postgres) & set(service))
if not shared:
    raise SystemExit("no shared network")
alias_networks = []
for network in shared:
    postgres_network = postgres[network]
    service_network = service[network]
    network_id = str(postgres_network.get("NetworkID") or "")
    if not network_id or network_id != str(service_network.get("NetworkID") or ""):
        raise SystemExit("service/Postgres network identity mismatch")
    aliases = sorted({
        alias for alias in (postgres_network.get("Aliases") or []) if alias
    })
    if host.casefold() not in {alias.casefold() for alias in aliases}:
        continue
    addresses = sorted({
        address
        for address in (
            postgres_network.get("IPAddress"),
            postgres_network.get("GlobalIPv6Address"),
        )
        if address
    })
    if not addresses:
        raise SystemExit("Postgres has no address on the service alias network")
    alias_networks.append((network, network_id, aliases, addresses))
if len(alias_networks) != 1:
    raise SystemExit("service DB alias must exist on exactly one shared network")
network, network_id, aliases, addresses = alias_networks[0]
if not addresses:
    raise SystemExit("Postgres has no address on a shared service network")
print("\t".join([
    network,
    network_id,
    ",".join(aliases),
    ",".join(addresses),
]))
' "$postgres_networks" "$service_networks" "$SERVICE_DB_HOST"
    )"; then
        printf 'ERROR: service/Postgres network alias attestation failed\n' >&2
        return 1
    fi
    IFS=$'\t' read -r network_name network_id \
        PINNED_POSTGRES_NETWORK_ALIASES PINNED_POSTGRES_SERVER_ADDRESSES \
        <<<"$network_values"
    PINNED_POSTGRES_NETWORK_NAMES="$network_name"

    network_members_json="$(
        run_docker_query_bounded \
            network inspect \
            --format '{{json .Containers}}' \
            "$network_name"
    )" || return 1
    if ! network_member_values="$(
        python3 -c '
import json
import sys
containers = json.load(sys.stdin) or {}
for container_id in sorted(containers):
    name = str((containers.get(container_id) or {}).get("Name") or "")
    print(f"{container_id}\t{name}")
' <<<"$network_members_json"
    )"; then
        printf 'ERROR: unable to enumerate service database network members\n' >&2
        return 1
    fi
    if [[ -z "$network_member_values" ]]; then
        printf 'ERROR: service database network has no active container endpoints\n' >&2
        return 1
    fi
    mapfile -t network_member_ids <<<"$network_member_values"
    if (( ${#network_member_ids[@]} > 64 )); then
        printf 'ERROR: service database network has too many endpoints to attest safely\n' >&2
        return 1
    fi
    for member_entry in "${network_member_ids[@]}"; do
        IFS=$'\t' read -r member_id member_name <<<"$member_entry"
        if [[ -z "$member_id" ]]; then
            printf 'ERROR: service database network member ID is missing\n' >&2
            return 1
        fi
        member_networks="$(
            run_docker_query_bounded \
                inspect \
                --format '{{json .NetworkSettings.Networks}}' \
                "$member_id"
        )" || return 1
        network_member_bindings+="$member_id"$'\t'"$member_name"$'\t'"$member_networks"$'\n'
    done
    if ! binding_json="$(
        python3 -c '
import json
import sys
network_name = sys.argv[1]
network_id = sys.argv[2]
service_alias = sys.argv[3]
expected_owner = sys.argv[4]
addresses = [value for value in sys.argv[5].split(",") if value]
owners = []
def normalized_names(value):
    normalized = str(value or "").strip().lstrip("/").rstrip(".").casefold()
    if not normalized:
        return set()
    values = {normalized}
    if "." in normalized:
        values.add(normalized.split(".", 1)[0])
    return values
target_names = normalized_names(service_alias)
for line in sys.stdin:
    line = line.rstrip("\n")
    if not line:
        continue
    container_id, container_name, networks_json = line.split("\t", 2)
    networks = json.loads(networks_json)
    network = networks.get(network_name)
    if network is None:
        raise SystemExit("network member disappeared during alias attestation")
    if str(network.get("NetworkID") or "") != network_id:
        raise SystemExit("network ID drifted during alias attestation")
    resolvable_names = normalized_names(container_name)
    for value in network.get("Aliases") or []:
        resolvable_names.update(normalized_names(value))
    for value in (
        network.get("DNSNames")
        or network.get("DnsNames")
        or []
    ):
        resolvable_names.update(normalized_names(value))
    if target_names & resolvable_names:
        owners.append(container_id)
if owners != [expected_owner]:
    raise SystemExit(
        "service database alias is not exclusively owned by production Postgres")
binding = [{
    "networkName": network_name,
    "networkId": network_id,
    "serviceAlias": service_alias,
    "exclusiveOwnerContainerId": expected_owner,
    "serverAddresses": sorted(addresses),
}]
print(json.dumps(binding, separators=(",", ":"), sort_keys=True))
' \
            "$network_name" \
            "$network_id" \
            "$SERVICE_DB_HOST" \
            "$PINNED_POSTGRES_CONTAINER_ID" \
            "$PINNED_POSTGRES_SERVER_ADDRESSES" \
            <<<"$network_member_bindings"
    )"; then
        printf 'ERROR: service database network alias is not exclusive\n' >&2
        return 1
    fi
    PINNED_POSTGRES_NETWORK_BINDINGS_JSON="$binding_json"
    local visibility_host_segment="Host=$SERVICE_DB_HOST;"
    local visibility_address="${PINNED_POSTGRES_SERVER_ADDRESSES%%,*}"
    if [[ -z "$visibility_address" \
        || "$SERVICE_DB_CONNECTION_STRING" != *"$visibility_host_segment"* ]]; then
        printf 'ERROR: unable to bind visibility probe to attested Postgres address\n' >&2
        return 1
    fi
    FST_STORED_RANK_VISIBILITY_PROBE_CONNECTION_STRING="$(
        printf '%s' "${SERVICE_DB_CONNECTION_STRING/"$visibility_host_segment"/"Host=$visibility_address;"}"
    )"
    export FST_STORED_RANK_VISIBILITY_PROBE_CONNECTION_STRING

    if [[ "$write_evidence" == "true" && -n "$EVIDENCE_DIR" ]]; then
        if ! python3 -c '
import json
import pathlib
import sys
payload = {
    "serviceTarget": {
        "host": sys.argv[2],
        "port": int(sys.argv[3]),
        "database": sys.argv[4],
        "username": sys.argv[5],
    },
    "postgresContainerId": sys.argv[6],
    "postgresImageReference": sys.argv[7],
    "postgresImageId": sys.argv[8],
    "postgresNetworkNames": sys.argv[9].split(","),
    "postgresNetworkAliases": sys.argv[10].split(","),
    "postgresServerAddresses": sys.argv[11].split(","),
    "postgresNetworkBindings": json.loads(sys.argv[12]),
}
pathlib.Path(sys.argv[1]).write_text(json.dumps(payload, indent=2) + "\n")
' \
            "$EVIDENCE_DIR/database-target.json" \
            "$SERVICE_DB_HOST" \
            "$SERVICE_DB_PORT" \
            "$SERVICE_DB_NAME" \
            "$SERVICE_DB_USERNAME" \
            "$PINNED_POSTGRES_CONTAINER_ID" \
            "$PINNED_POSTGRES_IMAGE_REFERENCE" \
            "$PINNED_POSTGRES_IMAGE_ID" \
            "$PINNED_POSTGRES_NETWORK_NAMES" \
            "$PINNED_POSTGRES_NETWORK_ALIASES" \
            "$PINNED_POSTGRES_SERVER_ADDRESSES" \
            "$PINNED_POSTGRES_NETWORK_BINDINGS_JSON"; then
            printf 'ERROR: unable to write database target evidence\n' >&2
            return 1
        fi
    fi
}

verify_database_target_binding() {
    local previous_container_id="$PINNED_POSTGRES_CONTAINER_ID"
    local previous_image_reference="$PINNED_POSTGRES_IMAGE_REFERENCE"
    local previous_image_id="$PINNED_POSTGRES_IMAGE_ID"
    local previous_network_names="$PINNED_POSTGRES_NETWORK_NAMES"
    local previous_aliases="$PINNED_POSTGRES_NETWORK_ALIASES"
    local previous_addresses="$PINNED_POSTGRES_SERVER_ADDRESSES"
    local previous_network_bindings="$PINNED_POSTGRES_NETWORK_BINDINGS_JSON"
    local previous_host="$SERVICE_DB_HOST"
    local previous_port="$SERVICE_DB_PORT"
    local previous_database="$SERVICE_DB_NAME"
    local previous_username="$SERVICE_DB_USERNAME"
    local previous_connection_string="$SERVICE_DB_CONNECTION_STRING"
    local matches=0
    if ! resolve_database_target_binding false; then
        PINNED_POSTGRES_CONTAINER_ID="$previous_container_id"
        PINNED_POSTGRES_IMAGE_REFERENCE="$previous_image_reference"
        PINNED_POSTGRES_IMAGE_ID="$previous_image_id"
        PINNED_POSTGRES_NETWORK_NAMES="$previous_network_names"
        PINNED_POSTGRES_NETWORK_ALIASES="$previous_aliases"
        PINNED_POSTGRES_SERVER_ADDRESSES="$previous_addresses"
        PINNED_POSTGRES_NETWORK_BINDINGS_JSON="$previous_network_bindings"
        SERVICE_DB_HOST="$previous_host"
        SERVICE_DB_PORT="$previous_port"
        SERVICE_DB_NAME="$previous_database"
        SERVICE_DB_USERNAME="$previous_username"
        SERVICE_DB_CONNECTION_STRING="$previous_connection_string"
        export FST_STORED_RANK_VISIBILITY_PROBE_CONNECTION_STRING="$SERVICE_DB_CONNECTION_STRING"
        return 1
    fi
    if [[ "$PINNED_POSTGRES_CONTAINER_ID" == "$previous_container_id" \
        && "$PINNED_POSTGRES_IMAGE_REFERENCE" == "$previous_image_reference" \
        && "$PINNED_POSTGRES_IMAGE_ID" == "$previous_image_id" \
        && "$PINNED_POSTGRES_NETWORK_NAMES" == "$previous_network_names" \
        && "$PINNED_POSTGRES_NETWORK_ALIASES" == "$previous_aliases" \
        && "$PINNED_POSTGRES_SERVER_ADDRESSES" == "$previous_addresses" \
        && "$PINNED_POSTGRES_NETWORK_BINDINGS_JSON" == "$previous_network_bindings" \
        && "$SERVICE_DB_HOST" == "$previous_host" \
        && "$SERVICE_DB_PORT" == "$previous_port" \
        && "$SERVICE_DB_NAME" == "$previous_database" \
        && "$SERVICE_DB_USERNAME" == "$previous_username" ]]; then
        matches=1
    fi
    PINNED_POSTGRES_CONTAINER_ID="$previous_container_id"
    PINNED_POSTGRES_IMAGE_REFERENCE="$previous_image_reference"
    PINNED_POSTGRES_IMAGE_ID="$previous_image_id"
    PINNED_POSTGRES_NETWORK_NAMES="$previous_network_names"
    PINNED_POSTGRES_NETWORK_ALIASES="$previous_aliases"
    PINNED_POSTGRES_SERVER_ADDRESSES="$previous_addresses"
    PINNED_POSTGRES_NETWORK_BINDINGS_JSON="$previous_network_bindings"
    SERVICE_DB_HOST="$previous_host"
    SERVICE_DB_PORT="$previous_port"
    SERVICE_DB_NAME="$previous_database"
    SERVICE_DB_USERNAME="$previous_username"
    if (( matches == 0 )); then
        printf 'ERROR: service/evidence/Postgres runtime target drifted from manifest binding\n' >&2
        return 1
    fi
}

verify_container_image() {
    local service_name="$1"
    local expected_container_id="${2:-}"
    local container_id observed
    if [[ -z "$FST_STORED_RANK_SERVICE_IMAGE" \
        || -z "$PINNED_SERVICE_IMAGE_ID" ]]; then
        printf 'ERROR: service image pin is unresolved\n' >&2
        return 1
    fi
    if ! container_id="$(
        run_docker_query_bounded compose \
            --project-directory "$COMPOSE_DIR" \
            -f "$BASE_COMPOSE_FILE" \
            ps -q "$service_name"
    )" || [[ -z "$container_id" ]]; then
        printf 'ERROR: unable to resolve %s for image verification\n' "$service_name" >&2
        return 1
    fi
    if [[ -n "$expected_container_id" \
        && "$container_id" != "$expected_container_id" ]]; then
        printf 'ERROR: %s container identity changed before image verification\n' \
            "$service_name" >&2
        return 1
    fi
    if ! observed="$(
        run_docker_query_bounded \
            inspect \
            --format '{{.Image}}|{{.Config.Image}}' \
            "$container_id"
    )"; then
        printf 'ERROR: unable to inspect %s image pin\n' "$service_name" >&2
        return 1
    fi
    if [[ "$observed" != "$PINNED_SERVICE_IMAGE_ID|$FST_STORED_RANK_SERVICE_IMAGE" ]]; then
        printf 'ERROR: %s image pin mismatch: %s\n' "$service_name" "$observed" >&2
        return 1
    fi
}

verify_manifest_image_pin() {
    local manifest_path="$1"
    local fingerprint
    if ! fingerprint="$(python3 -c '
import json
import sys
manifest = json.loads(open(sys.argv[1], encoding="utf-8").read())
if manifest.get("serviceImageReference") != sys.argv[2]:
    raise SystemExit("manifest service image reference mismatch")
if manifest.get("serviceImageId") != sys.argv[3]:
    raise SystemExit("manifest service image ID mismatch")
if manifest.get("workerContainerId") != sys.argv[4]:
    raise SystemExit("manifest worker container mismatch")
if manifest.get("workerImageReference") != sys.argv[5]:
    raise SystemExit("manifest worker image reference mismatch")
if manifest.get("workerImageId") != sys.argv[6]:
    raise SystemExit("manifest worker image ID mismatch")
if manifest.get("workerContainerStatus") != sys.argv[7]:
    raise SystemExit("manifest worker status mismatch")
if manifest.get("workerContainerState") != sys.argv[8]:
    raise SystemExit("manifest worker state mismatch")
print(manifest.get("selectionFingerprint") or "")
' \
        "$manifest_path" \
        "$FST_STORED_RANK_SERVICE_IMAGE" \
        "$PINNED_SERVICE_IMAGE_ID" \
        "$PINNED_WORKER_CONTAINER_ID" \
        "$PINNED_WORKER_IMAGE_REFERENCE" \
        "$PINNED_WORKER_IMAGE_ID" \
        "$PINNED_WORKER_CONTAINER_STATUS" \
        "$PINNED_WORKER_CONTAINER_STATE")"; then
        printf 'ERROR: manifest image pin does not match resolved rollout image\n' >&2
        return 1
    fi
    if [[ -z "$fingerprint" ]]; then
        printf 'ERROR: manifest selection fingerprint is missing\n' >&2
        return 1
    fi
    ROLLOUT_MANIFEST_FINGERPRINT="$fingerprint"
}

verify_manifest_mount_binding() {
    local manifest_path="$1"
    if ! python3 -c '
import json
import sys
manifest = json.loads(open(sys.argv[1], encoding="utf-8").read())
actual = (
    manifest.get("evidenceMountTarget"),
    manifest.get("evidenceMountSource"),
    manifest.get("evidenceMountFileSystem"),
)
expected = tuple(sys.argv[2:5])
if actual != expected:
    raise SystemExit(f"manifest mount mismatch: {actual} != {expected}")
' \
        "$manifest_path" \
        "$EVIDENCE_MOUNT_TARGET" \
        "$EVIDENCE_MOUNT_SOURCE" \
        "$EVIDENCE_MOUNT_FSTYPE"; then
        printf 'ERROR: manifest evidence mount binding changed\n' >&2
        return 1
    fi
}

load_rollout_manifest_bindings() {
    local manifest_path="$EVIDENCE_DIR/manifest.json"
    local values resolved_image_id
    local manifest_mount_target manifest_mount_source manifest_mount_fstype
    local manifest_published_scrape_id supplied_published_scrape_id
    if [[ ! -f "$manifest_path" ]]; then
        printf 'ERROR: standalone rollback requires the original manifest.json\n' >&2
        return 1
    fi
    run_tool validate-manifest --manifest "$manifest_path"
    if ! values="$(
        python3 -c '
import json
import sys
manifest = json.loads(open(sys.argv[1], encoding="utf-8").read())
print("\t".join([
    manifest["serviceImageReference"],
    manifest["serviceImageId"],
    manifest["workerContainerId"],
    manifest["workerImageReference"],
    manifest["workerImageId"],
    manifest["workerContainerStatus"],
    manifest["workerContainerState"],
    manifest["evidenceMountTarget"],
    manifest["evidenceMountSource"],
    manifest["evidenceMountFileSystem"],
    manifest["selectionFingerprint"],
    str(manifest["publishedScrapeId"]),
    manifest["serviceDatabaseTarget"]["host"],
    str(manifest["serviceDatabaseTarget"]["port"]),
    manifest["serviceDatabaseTarget"]["database"],
    manifest["serviceDatabaseTarget"]["username"],
    manifest["postgresContainerId"],
    manifest["postgresImageReference"],
    manifest["postgresImageId"],
    ",".join(manifest["postgresNetworkNames"]),
    ",".join(manifest["postgresNetworkAliases"]),
    ",".join(manifest["postgresServerAddresses"]),
    json.dumps(
        manifest["postgresNetworkBindings"],
        separators=(",", ":"),
        sort_keys=True),
]))
' "$manifest_path"
    )"; then
        printf 'ERROR: unable to read original manifest bindings\n' >&2
        return 1
    fi
    IFS=$'\t' read -r FST_STORED_RANK_SERVICE_IMAGE PINNED_SERVICE_IMAGE_ID \
        PINNED_WORKER_CONTAINER_ID PINNED_WORKER_IMAGE_REFERENCE \
        PINNED_WORKER_IMAGE_ID PINNED_WORKER_CONTAINER_STATUS \
        PINNED_WORKER_CONTAINER_STATE \
        manifest_mount_target manifest_mount_source manifest_mount_fstype \
        ROLLOUT_MANIFEST_FINGERPRINT manifest_published_scrape_id \
        SERVICE_DB_HOST SERVICE_DB_PORT SERVICE_DB_NAME SERVICE_DB_USERNAME \
        PINNED_POSTGRES_CONTAINER_ID PINNED_POSTGRES_IMAGE_REFERENCE \
        PINNED_POSTGRES_IMAGE_ID PINNED_POSTGRES_NETWORK_NAMES \
        PINNED_POSTGRES_NETWORK_ALIASES PINNED_POSTGRES_SERVER_ADDRESSES \
        PINNED_POSTGRES_NETWORK_BINDINGS_JSON \
        <<<"$values"
    export FST_STORED_RANK_SERVICE_IMAGE
    if [[ ! "$manifest_published_scrape_id" =~ ^[1-9][0-9]*$ ]]; then
        printf 'ERROR: original manifest published scrape ID is invalid\n' >&2
        return 1
    fi
    supplied_published_scrape_id="$EXPECTED_PUBLISHED_SCRAPE_ID"
    if [[ -n "$supplied_published_scrape_id" \
        && "$supplied_published_scrape_id" != "$manifest_published_scrape_id" ]]; then
        printf 'ERROR: supplied published scrape ID conflicts with original manifest: %s != %s\n' \
            "$supplied_published_scrape_id" \
            "$manifest_published_scrape_id" >&2
        return 1
    fi
    EXPECTED_PUBLISHED_SCRAPE_ID="$manifest_published_scrape_id"
    export EXPECTED_PUBLISHED_SCRAPE_ID
    if [[ "$manifest_mount_target" != "$EVIDENCE_MOUNT_TARGET" \
        || "$manifest_mount_source" != "$EVIDENCE_MOUNT_SOURCE" \
        || "$manifest_mount_fstype" != "$EVIDENCE_MOUNT_FSTYPE" ]]; then
        printf 'ERROR: original manifest mount binding no longer matches\n' >&2
        return 1
    fi
    if ! resolved_image_id="$(
        run_docker_query_bounded \
            image inspect \
            --format '{{.Id}}' \
            "$FST_STORED_RANK_SERVICE_IMAGE"
    )" || [[ "$resolved_image_id" != "$PINNED_SERVICE_IMAGE_ID" ]]; then
        printf 'ERROR: original manifest image pin is unavailable or mismatched\n' >&2
        return 1
    fi
    if ! verify_database_target_binding; then
        printf 'ERROR: original manifest database binding no longer matches\n' >&2
        return 1
    fi
}

validate_overlay() {
    local overlay="$1"
    local expected_service="$2"
    local expected_read_only="$3"
    local compose_dir="${4:-$COMPOSE_DIR}"
    local base_compose_file="${5:-$BASE_COMPOSE_FILE}"
    local compose_json
    compose_json="$(
        run_docker_query_bounded compose \
            --project-directory "$compose_dir" \
            -f "$base_compose_file" \
            -f "$overlay" \
            config --format json
    )"
    python3 -c '
import json
import re
import sys

config = json.load(sys.stdin)
services = config.get("services") or {}
service_env = (services.get("fstservice") or {}).get("environment") or {}
worker_env = (services.get("fstworker") or {}).get("environment") or {}
flag = "Features__UseStoredSoloProjectionRanksForFilteredReads"
published_flag = "Features__UsePublishedScopeSources"
read_only_startup = "Scraper__RolloutReadOnlyStartup"
postgres_read_only = "Scraper__RolloutPostgresReadOnly"
expected_service = sys.argv[1]
expected_read_only = sys.argv[2]
service_image = str((services.get("fstservice") or {}).get("image") or "")
if not re.fullmatch(r"[^@\s]+:[^@/\s]+@sha256:[0-9a-f]{64}", service_image):
    raise SystemExit("fstservice image must resolve to immutable tag@digest")
if str(service_env.get(flag, "")).lower() != expected_service:
    raise SystemExit(f"fstservice {flag} did not resolve to {expected_service}")
if str(worker_env.get(flag, "")).lower() != "false":
    raise SystemExit(f"fstworker {flag} must resolve to false")
if str(service_env.get(published_flag, "")).lower() != "true":
    raise SystemExit(f"fstservice {published_flag} must resolve to true")
if str(worker_env.get(published_flag, "")).lower() != "false":
    raise SystemExit(f"fstworker {published_flag} must resolve to false")
if str(service_env.get(read_only_startup, "")).lower() != expected_read_only:
    raise SystemExit(
        f"fstservice {read_only_startup} must resolve to {expected_read_only}")
if str(worker_env.get(read_only_startup, "")).lower() != "false":
    raise SystemExit(f"fstworker {read_only_startup} must resolve to false")
if str(service_env.get(postgres_read_only, "")).lower() != expected_read_only:
    raise SystemExit(
        f"fstservice {postgres_read_only} must resolve to {expected_read_only}")
if str(worker_env.get(postgres_read_only, "")).lower() != "false":
    raise SystemExit(f"fstworker {postgres_read_only} must resolve to false")
' "$expected_service" "$expected_read_only" <<<"$compose_json"
}

overlay_for_variant() {
    case "$1" in
        baseline) printf '%s\n' "$FALSE_OVERLAY" ;;
        candidate) printf '%s\n' "$TRUE_OVERLAY" ;;
        *) printf 'ERROR: unknown variant: %s\n' "$1" >&2; exit 64 ;;
    esac
}

verify_container_env() {
    local service_name="$1"
    local key="$2"
    local expected="$3"
    local container_id
    container_id="$(get_compose_container_id "$service_name")"
    [[ -n "$container_id" ]] || {
        printf 'ERROR: container is not running: %s\n' "$service_name" >&2
        return 1
    }
    verify_container_env_by_id "$container_id" "$key" "$expected"
}

verify_container_env_by_id() {
    local container_id="$1"
    local key="$2"
    local expected="$3"
    run_docker_query_bounded \
        inspect \
        --format '{{range .Config.Env}}{{println .}}{{end}}' \
        "$container_id" \
        | grep -Fxq "$key=$expected"
}

read_container_env_value() {
    local container_id="$1"
    local key="$2"
    local environment line
    if ! environment="$(
        run_docker_query_bounded \
            inspect \
            --format '{{range .Config.Env}}{{println .}}{{end}}' \
            "$container_id"
    )"; then
        return 1
    fi
    while IFS= read -r line; do
        if [[ "$line" == "$key="* ]]; then
            printf '%s\n' "${line#*=}"
            return 0
        fi
    done <<<"$environment"
    return 1
}

get_compose_container_id() {
    local service_name="$1"
    run_docker_query_bounded compose \
        --project-directory "$COMPOSE_DIR" \
        -f "$BASE_COMPOSE_FILE" \
        ps --all -q "$service_name"
}

get_service_container_id() {
    get_compose_container_id fstservice
}

resolve_service_traffic_binding() {
    local expected_container_id="${1:-}"
    local container_id inspected values hostname derived_url
    container_id="$(get_service_container_id)" || return 1
    if [[ -z "$container_id" \
        || ( -n "$expected_container_id" \
            && "$container_id" != "$expected_container_id") ]]; then
        printf 'ERROR: fstservice container changed while resolving traffic endpoint\n' >&2
        return 1
    fi
    inspected="$(
        run_docker_query_bounded inspect \
            --format '{{.Config.Hostname}}|{{json (index .NetworkSettings.Ports "8080/tcp")}}' \
            "$container_id"
    )" || return 1
    if ! values="$(
        python3 -c '
import ipaddress
import json
import sys
hostname, bindings_json = sys.stdin.read().strip().split("|", 1)
bindings = json.loads(bindings_json)
if not hostname or not isinstance(bindings, list) or len(bindings) != 1:
    raise SystemExit("fstservice must have exactly one 8080/tcp host binding")
binding = bindings[0]
host_ip = str(binding.get("HostIp") or "")
host_port = str(binding.get("HostPort") or "")
address = ipaddress.ip_address(host_ip)
if not address.is_loopback or not host_port.isdigit():
    raise SystemExit("fstservice host binding must be a loopback TCP port")
url_host = f"[{address.compressed}]" if address.version == 6 else address.compressed
print(f"{hostname}\thttp://{url_host}:{int(host_port)}")
' <<<"$inspected"
    )"; then
        printf 'ERROR: unable to attest fstservice host port binding\n' >&2
        return 1
    fi
    IFS=$'\t' read -r hostname derived_url <<<"$values"
    if [[ -n "$REQUESTED_BASE_URL" \
        && "${REQUESTED_BASE_URL%/}" != "$derived_url" ]]; then
        printf 'ERROR: supplied BASE_URL does not match inspected fstservice endpoint\n' >&2
        return 1
    fi
    if [[ "$EXPECTED_SERVICE_CONTAINER_ID" != "$container_id" ]]; then
        PREVIOUS_SERVICE_INSTANCE_NONCE="$EXPECTED_SERVICE_INSTANCE_NONCE"
        EXPECTED_SERVICE_INSTANCE_NONCE=""
    fi
    EXPECTED_SERVICE_CONTAINER_ID="$container_id"
    EXPECTED_SERVICE_CONTAINER_HOSTNAME="$hostname"
    BASE_URL="$derived_url"
}

verify_block_state() {
    local variant="$1"
    local expected=false current_id
    if [[ "$variant" == "candidate" ]]; then
        expected=true
    fi
    EXPECTED_SERVICE_READ_ONLY_STARTUP=true
    if ! current_id="$(get_service_container_id)" \
        || [[ -z "$current_id" ]] \
        || [[ "$current_id" != "$ACTIVE_BLOCK_CONTAINER_ID" ]]; then
        printf 'ERROR: fstservice container identity changed during %s block\n' \
            "$variant" >&2
        return 1
    fi
    if [[ "$ACTIVE_BLOCK_VARIANT" != "$variant" ]]; then
        printf 'ERROR: active block variant changed unexpectedly\n' >&2
        return 1
    fi
    verify_container_image fstservice
    verify_worker_pin
    verify_container_env \
        fstservice \
        Features__UseStoredSoloProjectionRanksForFilteredReads \
        "$expected"
    verify_container_env \
        fstworker \
        Features__UseStoredSoloProjectionRanksForFilteredReads \
        false
    verify_container_env fstservice Features__UsePublishedScopeSources true
    verify_container_env fstworker Features__UsePublishedScopeSources false
    verify_container_env fstservice Scraper__RolloutReadOnlyStartup true
    verify_container_env fstworker Scraper__RolloutReadOnlyStartup false
    verify_container_env fstservice Scraper__RolloutPostgresReadOnly true
    verify_container_env fstworker Scraper__RolloutPostgresReadOnly false
    verify_database_target_binding
    wait_public_path "$current_id"
}

curl_public_path_before_deadline() {
    local url="$1"
    local deadline="$2"
    local remaining max_time
    remaining=$((deadline - SECONDS))
    if (( remaining <= 0 )); then
        return 1
    fi
    max_time="$PUBLIC_PATH_MAX_TIME_SECONDS"
    if (( max_time > remaining )); then
        max_time="$remaining"
    fi
    local response status body
    if ! response="$(curl \
        --silent \
        --show-error \
        --connect-timeout "$PUBLIC_PATH_CONNECT_TIMEOUT_SECONDS" \
        --max-time "$max_time" \
        --write-out $'\n%{http_code}' \
        "$url")"; then
        return 1
    fi
    status="${response##*$'\n'}"
    body="${response%$'\n'*}"
    if [[ "$status" != "200" ]]; then
        printf 'ERROR: expected HTTP 200 from %s, received %s\n' \
            "$url" "$status" >&2
        return 1
    fi
    printf '%s' "$body"
}

validate_service_info_json() {
    python3 -c '
import json
import re
import sys
expected = int(sys.argv[1])
expected_read_only = sys.argv[2].lower() == "true"
value = json.load(sys.stdin)
required = [
    "publishedScrapeId",
    "publication",
    "workerStatus",
    "currentUpdate",
    "rolloutReadOnlyStartup",
    "postgresDefaultTransactionReadOnly",
    "postgresConnectionTarget",
    "serviceInstance",
    "readOnlyViolationDetected",
]
if any(key not in value for key in required):
    raise SystemExit("missing service-info fields")
if value["publishedScrapeId"] != expected:
    raise SystemExit("published scrape mismatch")
publication = value["publication"]
if publication.get("publishedScrapeId") != expected:
    raise SystemExit("publication scrape mismatch")
if publication.get("publicReadsFrozen") is not False:
    raise SystemExit("public reads are frozen")
if value.get("activeScrapeId") is not None:
    raise SystemExit("active scrape present")
current = value["currentUpdate"]
if current.get("status") != "idle":
    raise SystemExit("service is not idle")
worker = value["workerStatus"]
if worker.get("workerKey") != "scraper":
    raise SystemExit("worker key mismatch")
if worker.get("status") not in ("offline", "stale"):
    raise SystemExit("worker is not offline/stale")
if worker.get("currentOperation") is not None:
    raise SystemExit("worker operation is active")
if value["rolloutReadOnlyStartup"] is not expected_read_only:
    raise SystemExit("rollout read-only startup state mismatch")
if value["postgresDefaultTransactionReadOnly"] is not expected_read_only:
    raise SystemExit("PostgreSQL read-only state mismatch")
target = value["postgresConnectionTarget"]
expected_target = {
    "host": sys.argv[3],
    "port": int(sys.argv[4]),
    "database": sys.argv[5],
    "username": sys.argv[6],
}
for key, expected_value in expected_target.items():
    if target.get(key) != expected_value:
        raise SystemExit(f"service PostgreSQL target mismatch: {key}")
if target.get("defaultTransactionReadOnlyOption") is not expected_read_only:
    raise SystemExit("service PostgreSQL connection read-only option mismatch")
instance = value["serviceInstance"]
nonce = str(instance.get("nonce") or "")
if instance.get("hostName") != sys.argv[7]:
    raise SystemExit("service container hostname mismatch")
if not re.fullmatch(r"[0-9a-f]{32}", nonce):
    raise SystemExit("service instance nonce is invalid")
if sys.argv[8] and nonce != sys.argv[8]:
    raise SystemExit("service instance nonce mismatch")
if not isinstance(instance.get("processId"), int) or instance["processId"] <= 0:
    raise SystemExit("service process ID is invalid")
if value["readOnlyViolationDetected"] is not False:
    raise SystemExit("PostgreSQL read-only violation detected")
print(nonce)
' \
        "$EXPECTED_PUBLISHED_SCRAPE_ID" \
        "$EXPECTED_SERVICE_READ_ONLY_STARTUP" \
        "$SERVICE_DB_HOST" \
        "$SERVICE_DB_PORT" \
        "$SERVICE_DB_NAME" \
        "$SERVICE_DB_USERNAME" \
        "$EXPECTED_SERVICE_CONTAINER_HOSTNAME" \
        "$EXPECTED_SERVICE_INSTANCE_NONCE"
}

wait_public_path() {
    local pinned_container_id="${1:-$EXPECTED_SERVICE_CONTAINER_ID}"
    local deadline remaining retry_delay
    if [[ ! "$PUBLIC_PATH_CONNECT_TIMEOUT_SECONDS" =~ ^[0-9]+$ ]] \
        || [[ ! "$PUBLIC_PATH_MAX_TIME_SECONDS" =~ ^[0-9]+$ ]] \
        || [[ ! "$PUBLIC_PATH_TOTAL_TIMEOUT_SECONDS" =~ ^[0-9]+$ ]] \
        || [[ ! "$PUBLIC_PATH_RETRY_DELAY_SECONDS" =~ ^[0-9]+$ ]] \
        || (( PUBLIC_PATH_CONNECT_TIMEOUT_SECONDS < 1 )) \
        || (( PUBLIC_PATH_MAX_TIME_SECONDS < 1 )) \
        || (( PUBLIC_PATH_TOTAL_TIMEOUT_SECONDS < 1 )); then
        printf 'ERROR: public-path timeout values must be bounded non-negative integers\n' >&2
        return 64
    fi

    if [[ -z "$pinned_container_id" ]]; then
        printf 'ERROR: public-path health requires a pinned fstservice container ID\n' >&2
        return 1
    fi
    resolve_service_traffic_binding "$pinned_container_id" || return 1
    deadline=$((SECONDS + PUBLIC_PATH_TOTAL_TIMEOUT_SECONDS))
    while (( SECONDS < deadline )); do
        local service_info web_service_info validated_nonce
        if ! resolve_service_traffic_binding \
            "$pinned_container_id"; then
            return 1
        fi
        if curl_public_path_before_deadline "$BASE_URL/readyz" "$deadline" >/dev/null \
            && curl_public_path_before_deadline "$WEB_BASE_URL/" "$deadline" >/dev/null \
            && service_info="$(
                curl_public_path_before_deadline \
                    "$BASE_URL/api/service-info" \
                    "$deadline"
            )" \
            && validated_nonce="$(validate_service_info_json <<<"$service_info")"; then
            if [[ -z "$EXPECTED_SERVICE_INSTANCE_NONCE" ]]; then
                if [[ -n "$PREVIOUS_SERVICE_INSTANCE_NONCE" \
                    && "$validated_nonce" == "$PREVIOUS_SERVICE_INSTANCE_NONCE" ]]; then
                    printf 'ERROR: recreated fstservice reused the previous process nonce\n' >&2
                    return 1
                fi
                EXPECTED_SERVICE_INSTANCE_NONCE="$validated_nonce"
            fi
            if web_service_info="$(
                curl_public_path_before_deadline \
                    "$WEB_BASE_URL/api/service-info" \
                    "$deadline"
            )" \
                && [[ "$(validate_service_info_json <<<"$web_service_info")" \
                    == "$EXPECTED_SERVICE_INSTANCE_NONCE" ]] \
                && [[ "$(get_service_container_id)" \
                    == "$pinned_container_id" ]]; then
                LAST_SERVICE_INFO_JSON="$service_info"
                return 0
            fi
        fi
        if (( SECONDS < deadline )); then
            remaining=$((deadline - SECONDS))
            retry_delay="$PUBLIC_PATH_RETRY_DELAY_SECONDS"
            if (( retry_delay > remaining )); then
                retry_delay="$remaining"
            fi
            sleep "$retry_delay"
        fi
    done
    printf 'ERROR: service/public path did not become healthy within %ss\n' \
        "$PUBLIC_PATH_TOTAL_TIMEOUT_SECONDS" >&2
    return 1
}

recreate_service_variant() {
    local variant="$1"
    local overlay expected recreated_service_id
    overlay="$(overlay_for_variant "$variant")"
    expected=false
    if [[ "$variant" == "candidate" ]]; then
        expected=true
    fi
    EXPECTED_SERVICE_READ_ONLY_STARTUP=true
    validate_overlay "$overlay" "$expected" true
    mutated_service=1
    rollout_mutation_attempted=1
    run_docker_recreate_bounded compose \
        --project-directory "$COMPOSE_DIR" \
        -f "$BASE_COMPOSE_FILE" \
        -f "$overlay" \
        up -d --no-deps --force-recreate --pull never fstservice
    recreated_service_id="$(get_service_container_id)" || return 1
    resolve_service_traffic_binding "$recreated_service_id" || return 1
    verify_container_image fstservice
    verify_worker_pin
    verify_container_env \
        fstservice \
        Features__UseStoredSoloProjectionRanksForFilteredReads \
        "$expected"
    verify_container_env \
        fstworker \
        Features__UseStoredSoloProjectionRanksForFilteredReads \
        false
    verify_container_env fstservice Features__UsePublishedScopeSources true
    verify_container_env fstworker Features__UsePublishedScopeSources false
    verify_container_env fstservice Scraper__RolloutReadOnlyStartup true
    verify_container_env fstworker Scraper__RolloutReadOnlyStartup false
    verify_container_env fstservice Scraper__RolloutPostgresReadOnly true
    verify_container_env fstworker Scraper__RolloutPostgresReadOnly false
    verify_database_target_binding
    wait_public_path "$recreated_service_id"
    ACTIVE_BLOCK_CONTAINER_ID="$(get_service_container_id)"
    if [[ -z "$ACTIVE_BLOCK_CONTAINER_ID" ]]; then
        printf 'ERROR: unable to capture active block container identity\n' >&2
        return 1
    fi
    ACTIVE_BLOCK_VARIANT="$variant"
    record_role_verification "$variant" "$recreated_service_id"
}

rollback_service() {
    local state_failed=0
    local evidence_failed=0
    local evidence_phase=""
    local rollback_service_id=""
    ROLLOUT_FAILURE_PHASE="read-only-rollback"
    EXPECTED_SERVICE_READ_ONLY_STARTUP=true
    if ! validate_overlay "$FALSE_OVERLAY" false true; then
        printf 'ERROR: rollback false override validation failed\n' >&2
        state_failed=1
    fi

    mutated_service=1
    rollout_mutation_attempted=1
    if (( state_failed == 0 )) && ! run_docker_recreate_bounded compose \
        --project-directory "$COMPOSE_DIR" \
        -f "$BASE_COMPOSE_FILE" \
        -f "$FALSE_OVERLAY" \
        up -d --no-deps --force-recreate --pull never fstservice; then
        printf 'ERROR: rollback fstservice recreate failed\n' >&2
        state_failed=1
    fi
    if (( state_failed == 0 )); then
        rollback_service_id="$(get_service_container_id)" || state_failed=1
        if (( state_failed == 0 )) \
            && ! resolve_service_traffic_binding "$rollback_service_id"; then
            printf 'ERROR: rollback fstservice traffic binding is unverified\n' >&2
            state_failed=1
        fi
    fi
    if ! verify_container_image fstservice; then
        printf 'ERROR: rollback fstservice image pin is unverified\n' >&2
        state_failed=1
    fi
    if ! verify_worker_pin; then
        printf 'ERROR: rollback fstworker runtime pin changed\n' >&2
        state_failed=1
    fi
    if ! verify_container_env \
        fstservice \
        Features__UseStoredSoloProjectionRanksForFilteredReads \
        false; then
        printf 'ERROR: rollback fstservice stored-rank flag is not false\n' >&2
        state_failed=1
    fi
    if ! verify_container_env \
        fstworker \
        Features__UseStoredSoloProjectionRanksForFilteredReads \
        false; then
        printf 'ERROR: rollback fstworker stored-rank flag is not false\n' >&2
        state_failed=1
    fi
    if ! verify_container_env fstservice Features__UsePublishedScopeSources true; then
        printf 'ERROR: rollback fstservice published-source flag is not true\n' >&2
        state_failed=1
    fi
    if ! verify_container_env fstworker Features__UsePublishedScopeSources false; then
        printf 'ERROR: rollback fstworker published-source flag is not false\n' >&2
        state_failed=1
    fi
    if ! verify_container_env fstservice Scraper__RolloutReadOnlyStartup true; then
        printf 'ERROR: rollback fstservice read-only startup mode is not true\n' >&2
        state_failed=1
    fi
    if ! verify_container_env fstworker Scraper__RolloutReadOnlyStartup false; then
        printf 'ERROR: rollback fstworker read-only startup mode is not false\n' >&2
        state_failed=1
    fi
    if ! verify_container_env fstservice Scraper__RolloutPostgresReadOnly true; then
        printf 'ERROR: rollback fstservice PostgreSQL read-only mode is not true\n' >&2
        state_failed=1
    fi
    if ! verify_container_env fstworker Scraper__RolloutPostgresReadOnly false; then
        printf 'ERROR: rollback fstworker PostgreSQL read-only mode is not false\n' >&2
        state_failed=1
    fi
    if ! verify_database_target_binding; then
        printf 'ERROR: rollback PostgreSQL runtime binding changed\n' >&2
        state_failed=1
    fi
    if [[ -z "$rollback_service_id" ]] \
        || ! wait_public_path "$rollback_service_id"; then
        printf 'ERROR: rollback service/public health verification failed\n' >&2
        state_failed=1
    fi
    if (( state_failed == 0 )) && ! capture_db_quiescence \
        "after-read-only-rollback" \
        "$EVIDENCE_DIR/quiescence-after-read-only-rollback.json" \
        "$rollback_service_id"; then
        state_failed=1
        printf 'ERROR: DB quiescence failed after read-only rollback\n' >&2
    fi

    if (( state_failed == 0 )); then
        if ! persist_role_evidence rollback "$rollback_service_id"; then
            evidence_failed=1
            evidence_phase="rollback-evidence"
        else
            LAST_ROLLBACK_VERIFICATION_PATH="$LAST_ROLE_VERIFICATION_PATH"
        fi
    else
        evidence_failed=1
        evidence_phase="rollback-evidence"
    fi

    # Normal-mode recovery is unconditional after any rollback attempt.
    if ! recover_normal_service; then
        return 1
    fi
    if ! complete_normal_recovery_evidence "$PINNED_RECOVERY_SERVICE_ID"; then
        state_failed=1
        evidence_failed=1
        evidence_phase="normal-recovery-evidence"
    fi

    if (( state_failed != 0 || evidence_failed != 0 )); then
        ROLLOUT_FAILURE_PHASE="${evidence_phase:-read-only-rollback}"
        printf 'ERROR: service recovered normally but rollback evidence is incomplete\n' >&2
        return 1
    fi
    ROLLOUT_FAILURE_PHASE=""
    return 0
}

recover_normal_service() {
    local recovery_service_id
    ROLLOUT_FAILURE_PHASE="normal-mode-recovery"
    EXPECTED_SERVICE_READ_ONLY_STARTUP=false
    PINNED_RECOVERY_SERVICE_ID=""
    if ! validate_overlay "$RECOVERY_OVERLAY" false false; then
        printf 'ERROR: normal-mode recovery override validation failed\n' >&2
        return 1
    fi
    mutated_service=1
    rollout_mutation_attempted=1
    if ! run_docker_recreate_bounded compose \
        --project-directory "$COMPOSE_DIR" \
        -f "$BASE_COMPOSE_FILE" \
        -f "$RECOVERY_OVERLAY" \
        up -d --no-deps --force-recreate --pull never fstservice; then
        printf 'ERROR: normal-mode fstservice recreate failed\n' >&2
        return 1
    fi
    recovery_service_id="$(get_service_container_id)" || return 1
    if [[ -z "$recovery_service_id" ]]; then
        return 1
    fi
    PINNED_RECOVERY_SERVICE_ID="$recovery_service_id"
    if ! resolve_service_traffic_binding "$recovery_service_id"; then
        printf 'ERROR: normal-mode fstservice traffic binding is unverified\n' >&2
        return 1
    fi
    if ! verify_normal_service_state "$recovery_service_id"; then
        printf 'ERROR: normal-mode recovery is unverified; mutation marker remains armed\n' >&2
        return 1
    fi

    return 0
}

complete_normal_recovery_evidence() {
    local pinned_service_id="${1:-$PINNED_RECOVERY_SERVICE_ID}"
    ROLLOUT_FAILURE_PHASE="normal-recovery-evidence"
    if [[ -z "$pinned_service_id" \
        || "$pinned_service_id" != "$PINNED_RECOVERY_SERVICE_ID" ]]; then
        return 1
    fi
    if ! capture_db_quiescence \
        "after-normal-recovery" \
        "$EVIDENCE_DIR/quiescence-after-normal-recovery.json" \
        "$pinned_service_id"; then
        return 1
    fi
    if ! persist_role_evidence recovery "$pinned_service_id"; then
        return 1
    fi
    LAST_RECOVERY_VERIFICATION_PATH="$LAST_ROLE_VERIFICATION_PATH"
    if [[ "$(get_service_container_id)" != "$pinned_service_id" ]] \
        || ! verify_role_evidence_current "$LAST_RECOVERY_VERIFICATION_PATH"; then
        return 1
    fi
    mutated_service=0
}

verify_normal_service_state() {
    local pinned_container_id="${1:-$EXPECTED_SERVICE_CONTAINER_ID}"
    local current_container_id
    EXPECTED_SERVICE_READ_ONLY_STARTUP=false
    if [[ -z "$pinned_container_id" ]]; then
        return 1
    fi
    current_container_id="$(get_service_container_id)" || return 1
    [[ "$current_container_id" == "$pinned_container_id" ]] || return 1
    verify_container_image fstservice "$pinned_container_id" || return 1
    verify_worker_pin || return 1
    verify_container_env_by_id \
        "$pinned_container_id" \
        Features__UseStoredSoloProjectionRanksForFilteredReads \
        false || return 1
    verify_container_env \
        fstworker \
        Features__UseStoredSoloProjectionRanksForFilteredReads \
        false || return 1
    verify_container_env_by_id \
        "$pinned_container_id" \
        Scraper__RolloutReadOnlyStartup \
        false || return 1
    verify_container_env fstworker Scraper__RolloutReadOnlyStartup false \
        || return 1
    verify_container_env_by_id \
        "$pinned_container_id" \
        Scraper__RolloutPostgresReadOnly \
        false || return 1
    verify_container_env fstworker Scraper__RolloutPostgresReadOnly false \
        || return 1
    verify_container_env_by_id \
        "$pinned_container_id" \
        Features__UsePublishedScopeSources \
        true || return 1
    verify_container_env fstworker Features__UsePublishedScopeSources false \
        || return 1
    verify_database_target_binding || return 1
    resolve_service_traffic_binding "$pinned_container_id" || return 1
    wait_public_path "$pinned_container_id" || return 1

    current_container_id="$(get_service_container_id)" || return 1
    [[ "$current_container_id" == "$pinned_container_id" ]] || return 1
    verify_container_image fstservice "$pinned_container_id" || return 1
    verify_worker_pin || return 1
    verify_container_env_by_id \
        "$pinned_container_id" \
        Features__UseStoredSoloProjectionRanksForFilteredReads \
        false || return 1
    verify_container_env_by_id \
        "$pinned_container_id" \
        Scraper__RolloutReadOnlyStartup \
        false || return 1
    verify_container_env_by_id \
        "$pinned_container_id" \
        Scraper__RolloutPostgresReadOnly \
        false || return 1
    verify_container_env_by_id \
        "$pinned_container_id" \
        Features__UsePublishedScopeSources \
        true
}

persist_role_evidence() {
    local label="$1"
    local pinned_service_id="${2:-$EXPECTED_SERVICE_CONTAINER_ID}"
    if [[ -z "$EVIDENCE_DIR" ]]; then
        printf 'ERROR: %s evidence directory is unavailable\n' "$label" >&2
        return 1
    fi
    if ! verify_evidence_mount \
        || ! verify_manifest_mount_binding "$EVIDENCE_DIR/manifest.json"; then
        printf 'ERROR: %s evidence mount binding is unavailable\n' "$label" >&2
        return 1
    fi
    if ! record_role_verification "$label" "$pinned_service_id"; then
        printf 'ERROR: %s role evidence write failed\n' "$label" >&2
        return 1
    fi
    append_rollback_evidence "$LAST_ROLE_VERIFICATION_PATH"
}

record_role_verification() {
    local label="$1"
    local pinned_service_id="${2:-$EXPECTED_SERVICE_CONTAINER_ID}"
    local expected_service_stored expected_service_read_only
    local service_id worker_id ending_service_id ending_worker_id output
    local observed_image observed_image_id observed_image_reference
    local observed_worker observed_worker_image_id observed_worker_image_reference
    local observed_worker_status observed_worker_started observed_worker_finished
    local observed_worker_restarts observed_worker_state
    local service_stored worker_stored service_published worker_published
    local service_read_only worker_read_only
    local service_postgres_read_only worker_postgres_read_only
    local service_target_values observed_db_host observed_db_port
    local observed_db_name observed_db_username observed_db_read_only_option
    local observed_service_hostname observed_service_nonce
    case "$label" in
        candidate)
            expected_service_stored=true
            expected_service_read_only=true
            ;;
        baseline|rollback)
            expected_service_stored=false
            expected_service_read_only=true
            ;;
        recovery|final)
            expected_service_stored=false
            expected_service_read_only=false
            ;;
        *)
            printf 'ERROR: unknown role evidence label: %s\n' "$label" >&2
            return 1
            ;;
    esac
    if [[ -z "$pinned_service_id" ]]; then
        printf 'ERROR: role evidence requires a pinned fstservice container ID\n' >&2
        return 1
    fi
    if [[ -n "$EVIDENCE_DIR" ]]; then
        if ! verify_evidence_mount; then
            return 1
        fi
        if [[ -f "$EVIDENCE_DIR/manifest.json" ]] \
            && ! verify_manifest_mount_binding "$EVIDENCE_DIR/manifest.json"; then
            return 1
        fi
    fi
    if ! service_id="$(get_compose_container_id fstservice)"; then
        printf 'ERROR: unable to resolve fstservice container for role evidence\n' >&2
        return 1
    fi
    if [[ "$service_id" != "$pinned_service_id" ]]; then
        printf 'ERROR: fstservice container changed before role evidence capture\n' >&2
        return 1
    fi
    if ! worker_id="$(get_compose_container_id fstworker)"; then
        printf 'ERROR: unable to resolve fstworker container for role evidence\n' >&2
        return 1
    fi
    if [[ -z "$service_id" || -z "$worker_id" ]]; then
        printf 'ERROR: role evidence requires running service and worker containers\n' >&2
        return 1
    fi
    if ! observed_image="$(
        run_docker_query_bounded \
            inspect \
            --format '{{.Image}}|{{.Config.Image}}' \
            "$service_id"
    )"; then
        printf 'ERROR: unable to capture service image evidence\n' >&2
        return 1
    fi
    observed_image_id="${observed_image%%|*}"
    observed_image_reference="${observed_image#*|}"
    if ! observed_worker="$(
        run_docker_query_bounded \
            inspect \
            --format '{{.Image}}|{{.Config.Image}}|{{.State.Status}}|{{.State.StartedAt}}|{{.State.FinishedAt}}|{{.RestartCount}}' \
            "$worker_id"
    )"; then
        printf 'ERROR: unable to capture worker image evidence\n' >&2
        return 1
    fi
    IFS='|' read -r observed_worker_image_id observed_worker_image_reference \
        observed_worker_status observed_worker_started observed_worker_finished \
        observed_worker_restarts <<<"$observed_worker"
    observed_worker_state="$observed_worker_status|$observed_worker_started|$observed_worker_finished|$observed_worker_restarts"
    service_stored="$(read_container_env_value \
        "$service_id" \
        Features__UseStoredSoloProjectionRanksForFilteredReads)" || return 1
    worker_stored="$(read_container_env_value \
        "$worker_id" \
        Features__UseStoredSoloProjectionRanksForFilteredReads)" || return 1
    service_published="$(read_container_env_value \
        "$service_id" \
        Features__UsePublishedScopeSources)" || return 1
    worker_published="$(read_container_env_value \
        "$worker_id" \
        Features__UsePublishedScopeSources)" || return 1
    service_read_only="$(read_container_env_value \
        "$service_id" \
        Scraper__RolloutReadOnlyStartup)" || return 1
    worker_read_only="$(read_container_env_value \
        "$worker_id" \
        Scraper__RolloutReadOnlyStartup)" || return 1
    service_postgres_read_only="$(read_container_env_value \
        "$service_id" \
        Scraper__RolloutPostgresReadOnly)" || return 1
    worker_postgres_read_only="$(read_container_env_value \
        "$worker_id" \
        Scraper__RolloutPostgresReadOnly)" || return 1
    if [[ "$observed_image_id" != "$PINNED_SERVICE_IMAGE_ID" \
        || "$observed_image_reference" != "$FST_STORED_RANK_SERVICE_IMAGE" \
        || "$service_stored" != "$expected_service_stored" \
        || "$worker_stored" != "false" \
        || "$service_published" != "true" \
        || "$worker_published" != "false" \
        || "$service_read_only" != "$expected_service_read_only" \
        || "$worker_read_only" != "false" \
        || "$service_postgres_read_only" != "$expected_service_read_only" \
        || "$worker_postgres_read_only" != "false" \
        || "$worker_id" != "$PINNED_WORKER_CONTAINER_ID" \
        || "$observed_worker_image_id" != "$PINNED_WORKER_IMAGE_ID" \
        || "$observed_worker_image_reference" != "$PINNED_WORKER_IMAGE_REFERENCE" \
        || "$observed_worker_status" != "$PINNED_WORKER_CONTAINER_STATUS" \
        || "$observed_worker_state" != "$PINNED_WORKER_CONTAINER_STATE" ]]; then
        printf 'ERROR: captured role evidence does not match expected %s state\n' \
            "$label" >&2
        return 1
    fi
    if ! wait_public_path "$pinned_service_id"; then
        printf 'ERROR: role evidence health verification failed\n' >&2
        return 1
    fi
    if ! service_target_values="$(
        python3 -c '
import json
import sys
payload = json.load(sys.stdin)
value = payload["postgresConnectionTarget"]
instance = payload["serviceInstance"]
print("\t".join([
    str(value.get("host") or ""),
    str(value.get("port") or ""),
    str(value.get("database") or ""),
    str(value.get("username") or ""),
    str(bool(value.get("defaultTransactionReadOnlyOption"))).lower(),
    str(instance.get("hostName") or ""),
    str(instance.get("nonce") or ""),
]))
' <<<"$LAST_SERVICE_INFO_JSON"
    )"; then
        printf 'ERROR: unable to capture effective service database target\n' >&2
        return 1
    fi
    IFS=$'\t' read -r observed_db_host observed_db_port observed_db_name \
        observed_db_username observed_db_read_only_option \
        observed_service_hostname observed_service_nonce <<<"$service_target_values"
    if [[ "$observed_db_host" != "$SERVICE_DB_HOST" \
        || "$observed_db_port" != "$SERVICE_DB_PORT" \
        || "$observed_db_name" != "$SERVICE_DB_NAME" \
        || "$observed_db_username" != "$SERVICE_DB_USERNAME" \
        || "$observed_db_read_only_option" != "$expected_service_read_only" \
        || "$observed_service_hostname" != "$EXPECTED_SERVICE_CONTAINER_HOSTNAME" \
        || "$observed_service_nonce" != "$EXPECTED_SERVICE_INSTANCE_NONCE" ]]; then
        printf 'ERROR: captured service database target does not match manifest binding\n' >&2
        return 1
    fi
    ending_service_id="$(get_compose_container_id fstservice)" || return 1
    ending_worker_id="$(get_compose_container_id fstworker)" || return 1
    if [[ "$ending_service_id" != "$pinned_service_id" \
        || "$ending_worker_id" != "$worker_id" ]]; then
        printf 'ERROR: container identity changed while capturing role evidence\n' >&2
        return 1
    fi
    if ! verify_database_target_binding; then
        printf 'ERROR: database runtime binding changed while capturing role evidence\n' >&2
        return 1
    fi
    if ! mkdir -p "$EVIDENCE_DIR/role-verification"; then
        printf 'ERROR: unable to create role-verification evidence directory\n' >&2
        return 1
    fi
    output="$EVIDENCE_DIR/role-verification/$(date -u +%Y%m%dT%H%M%S%NZ)-$label.json"
    if ! python3 -c '
import json
import pathlib
import sys
def flag(value):
    return value.lower() == "true"
payload = {
    "observedAtUtc": sys.argv[2],
    "label": sys.argv[3],
    "manifestFingerprint": sys.argv[4],
    "fstserviceContainerId": sys.argv[5],
    "fstworkerContainerId": sys.argv[6],
    "fstserviceImageReference": sys.argv[7],
    "fstserviceImageId": sys.argv[8],
    "fstworkerImageReference": sys.argv[9],
    "fstworkerImageId": sys.argv[10],
    "fstworkerContainerStatus": sys.argv[11],
    "fstworkerContainerState": sys.argv[12],
    "fstserviceStoredRankFlag": flag(sys.argv[13]),
    "fstworkerStoredRankFlag": flag(sys.argv[14]),
    "fstservicePublishedSources": flag(sys.argv[15]),
    "fstworkerPublishedSources": flag(sys.argv[16]),
    "fstserviceReadOnlyStartup": flag(sys.argv[17]),
    "fstworkerReadOnlyStartup": flag(sys.argv[18]),
    "fstservicePostgresReadOnly": flag(sys.argv[19]),
    "fstworkerPostgresReadOnly": flag(sys.argv[20]),
    "fstserviceDatabaseTarget": {
        "host": sys.argv[21],
        "port": int(sys.argv[22]),
        "database": sys.argv[23],
        "username": sys.argv[24],
    },
    "fstserviceDefaultTransactionReadOnlyOption": flag(sys.argv[25]),
    "postgresContainerId": sys.argv[26],
    "postgresImageReference": sys.argv[27],
    "postgresImageId": sys.argv[28],
    "postgresNetworkNames": sys.argv[29].split(","),
    "postgresNetworkAliases": sys.argv[30].split(","),
    "postgresServerAddresses": sys.argv[31].split(","),
    "postgresNetworkBindings": json.loads(sys.argv[32]),
    "fstserviceContainerHostname": sys.argv[33],
    "fstserviceInstanceNonce": sys.argv[34],
    "fstserviceBaseUrl": sys.argv[35],
    "healthVerified": True,
}
pathlib.Path(sys.argv[1]).write_text(
    json.dumps(payload, indent=2) + "\n",
    encoding="utf-8")
' \
        "$output" \
        "$(date -u +%Y-%m-%dT%H:%M:%SZ)" \
        "$label" \
        "$ROLLOUT_MANIFEST_FINGERPRINT" \
        "$service_id" \
        "$worker_id" \
        "$observed_image_reference" \
        "$observed_image_id" \
        "$observed_worker_image_reference" \
        "$observed_worker_image_id" \
        "$observed_worker_status" \
        "$observed_worker_state" \
        "$service_stored" \
        "$worker_stored" \
        "$service_published" \
        "$worker_published" \
        "$service_read_only" \
        "$worker_read_only" \
        "$service_postgres_read_only" \
        "$worker_postgres_read_only" \
        "$observed_db_host" \
        "$observed_db_port" \
        "$observed_db_name" \
        "$observed_db_username" \
        "$observed_db_read_only_option" \
        "$PINNED_POSTGRES_CONTAINER_ID" \
        "$PINNED_POSTGRES_IMAGE_REFERENCE" \
        "$PINNED_POSTGRES_IMAGE_ID" \
        "$PINNED_POSTGRES_NETWORK_NAMES" \
        "$PINNED_POSTGRES_NETWORK_ALIASES" \
        "$PINNED_POSTGRES_SERVER_ADDRESSES" \
        "$PINNED_POSTGRES_NETWORK_BINDINGS_JSON" \
        "$observed_service_hostname" \
        "$observed_service_nonce" \
        "$BASE_URL"; then
        printf 'ERROR: unable to write role-verification evidence\n' >&2
        return 1
    fi
    LAST_ROLE_VERIFICATION_PATH="$output"
    return 0
}

verify_role_evidence_current() {
    local evidence_path="$1"
    local values label expected_service_id expected_worker_id
    local expected_image_reference expected_image_id
    local expected_worker_image_reference expected_worker_image_id
    local expected_worker_status expected_worker_state
    local expected_service_stored expected_worker_stored
    local expected_service_published expected_worker_published
    local expected_service_read_only expected_worker_read_only
    local expected_service_postgres_read_only expected_worker_postgres_read_only
    local expected_db_host expected_db_port expected_db_name expected_db_username
    local expected_db_read_only_option expected_postgres_container_id
    local expected_postgres_image_reference expected_postgres_image_id
    local expected_postgres_network_names expected_postgres_network_aliases
    local expected_postgres_server_addresses expected_postgres_network_bindings
    local expected_service_hostname expected_service_nonce expected_service_base_url
    local service_id worker_id ending_service_id ending_worker_id observed_image
    local observed_worker
    local service_stored worker_stored service_published worker_published
    local service_read_only worker_read_only
    local service_postgres_read_only worker_postgres_read_only
    if ! values="$(
        python3 -c '
import json
import sys
value = json.loads(open(sys.argv[1], encoding="utf-8").read())
fields = [
    value.get("label", ""),
    value.get("fstserviceContainerId", ""),
    value.get("fstworkerContainerId", ""),
    value.get("fstserviceImageReference", ""),
    value.get("fstserviceImageId", ""),
    value.get("fstworkerImageReference", ""),
    value.get("fstworkerImageId", ""),
    value.get("fstworkerContainerStatus", ""),
    value.get("fstworkerContainerState", ""),
    str(bool(value.get("fstserviceStoredRankFlag"))).lower(),
    str(bool(value.get("fstworkerStoredRankFlag"))).lower(),
    str(bool(value.get("fstservicePublishedSources"))).lower(),
    str(bool(value.get("fstworkerPublishedSources"))).lower(),
    str(bool(value.get("fstserviceReadOnlyStartup"))).lower(),
    str(bool(value.get("fstworkerReadOnlyStartup"))).lower(),
    str(bool(value.get("fstservicePostgresReadOnly"))).lower(),
    str(bool(value.get("fstworkerPostgresReadOnly"))).lower(),
    str((value.get("fstserviceDatabaseTarget") or {}).get("host") or ""),
    str((value.get("fstserviceDatabaseTarget") or {}).get("port") or ""),
    str((value.get("fstserviceDatabaseTarget") or {}).get("database") or ""),
    str((value.get("fstserviceDatabaseTarget") or {}).get("username") or ""),
    str(bool(value.get("fstserviceDefaultTransactionReadOnlyOption"))).lower(),
    value.get("postgresContainerId", ""),
    value.get("postgresImageReference", ""),
    value.get("postgresImageId", ""),
    ",".join(value.get("postgresNetworkNames") or []),
    ",".join(value.get("postgresNetworkAliases") or []),
    ",".join(value.get("postgresServerAddresses") or []),
    json.dumps(
        value.get("postgresNetworkBindings") or [],
        separators=(",", ":"),
        sort_keys=True),
    value.get("fstserviceContainerHostname", ""),
    value.get("fstserviceInstanceNonce", ""),
    value.get("fstserviceBaseUrl", ""),
]
print("\t".join(fields))
' "$evidence_path"
    )"; then
        return 1
    fi
    IFS=$'\t' read -r label expected_service_id expected_worker_id \
        expected_image_reference expected_image_id \
        expected_worker_image_reference expected_worker_image_id \
        expected_worker_status expected_worker_state \
        expected_service_stored expected_worker_stored \
        expected_service_published expected_worker_published \
        expected_service_read_only expected_worker_read_only \
        expected_service_postgres_read_only expected_worker_postgres_read_only \
        expected_db_host expected_db_port expected_db_name expected_db_username \
        expected_db_read_only_option expected_postgres_container_id \
        expected_postgres_image_reference expected_postgres_image_id \
        expected_postgres_network_names expected_postgres_network_aliases \
        expected_postgres_server_addresses expected_postgres_network_bindings \
        expected_service_hostname expected_service_nonce expected_service_base_url \
        <<<"$values"
    if [[ "$label" != "recovery" ]]; then
        return 1
    fi
    if [[ "$expected_db_host" != "$SERVICE_DB_HOST" \
        || "$expected_db_port" != "$SERVICE_DB_PORT" \
        || "$expected_db_name" != "$SERVICE_DB_NAME" \
        || "$expected_db_username" != "$SERVICE_DB_USERNAME" \
        || "$expected_db_read_only_option" != "false" \
        || "$expected_postgres_container_id" != "$PINNED_POSTGRES_CONTAINER_ID" \
        || "$expected_postgres_image_reference" != "$PINNED_POSTGRES_IMAGE_REFERENCE" \
        || "$expected_postgres_image_id" != "$PINNED_POSTGRES_IMAGE_ID" \
        || "$expected_postgres_network_names" != "$PINNED_POSTGRES_NETWORK_NAMES" \
        || "$expected_postgres_network_aliases" != "$PINNED_POSTGRES_NETWORK_ALIASES" \
        || "$expected_postgres_server_addresses" != "$PINNED_POSTGRES_SERVER_ADDRESSES" \
        || "$expected_postgres_network_bindings" != "$PINNED_POSTGRES_NETWORK_BINDINGS_JSON" \
        || "$expected_service_hostname" != "$EXPECTED_SERVICE_CONTAINER_HOSTNAME" \
        || "$expected_service_nonce" != "$EXPECTED_SERVICE_INSTANCE_NONCE" \
        || "$expected_service_base_url" != "$BASE_URL" ]]; then
        return 1
    fi
    service_id="$(get_compose_container_id fstservice)" || return 1
    worker_id="$(get_compose_container_id fstworker)" || return 1
    if [[ "$service_id" != "$expected_service_id" \
        || "$worker_id" != "$expected_worker_id" ]]; then
        return 1
    fi
    observed_image="$(
        run_docker_query_bounded \
            inspect \
            --format '{{.Image}}|{{.Config.Image}}' \
            "$service_id"
    )" || return 1
    if [[ "$observed_image" != "$expected_image_id|$expected_image_reference" ]]; then
        return 1
    fi
    observed_worker="$(
        run_docker_query_bounded \
            inspect \
            --format '{{.Image}}|{{.Config.Image}}|{{.State.Status}}|{{.State.StartedAt}}|{{.State.FinishedAt}}|{{.RestartCount}}' \
            "$worker_id"
    )" || return 1
    if [[ "$observed_worker" != \
        "$expected_worker_image_id|$expected_worker_image_reference|$expected_worker_state" ]]; then
        return 1
    fi
    service_stored="$(read_container_env_value \
        "$service_id" \
        Features__UseStoredSoloProjectionRanksForFilteredReads)" || return 1
    worker_stored="$(read_container_env_value \
        "$worker_id" \
        Features__UseStoredSoloProjectionRanksForFilteredReads)" || return 1
    service_published="$(read_container_env_value \
        "$service_id" \
        Features__UsePublishedScopeSources)" || return 1
    worker_published="$(read_container_env_value \
        "$worker_id" \
        Features__UsePublishedScopeSources)" || return 1
    service_read_only="$(read_container_env_value \
        "$service_id" \
        Scraper__RolloutReadOnlyStartup)" || return 1
    worker_read_only="$(read_container_env_value \
        "$worker_id" \
        Scraper__RolloutReadOnlyStartup)" || return 1
    service_postgres_read_only="$(read_container_env_value \
        "$service_id" \
        Scraper__RolloutPostgresReadOnly)" || return 1
    worker_postgres_read_only="$(read_container_env_value \
        "$worker_id" \
        Scraper__RolloutPostgresReadOnly)" || return 1
    if [[ "$service_stored" != "$expected_service_stored" \
        || "$worker_stored" != "$expected_worker_stored" \
        || "$service_published" != "$expected_service_published" \
        || "$worker_published" != "$expected_worker_published" \
        || "$service_read_only" != "$expected_service_read_only" \
        || "$worker_read_only" != "$expected_worker_read_only" \
        || "$service_postgres_read_only" != "$expected_service_postgres_read_only" \
        || "$worker_postgres_read_only" != "$expected_worker_postgres_read_only" ]]; then
        return 1
    fi
    wait_public_path "$expected_service_id" || return 1
    verify_database_target_binding || return 1
    ending_service_id="$(get_compose_container_id fstservice)" || return 1
    ending_worker_id="$(get_compose_container_id fstworker)" || return 1
    [[ "$ending_service_id" == "$expected_service_id" \
        && "$ending_worker_id" == "$expected_worker_id" ]]
}

verify_final_recovery_state() {
    if verify_role_evidence_current "$LAST_RECOVERY_VERIFICATION_PATH"; then
        return 0
    fi
    ROLLOUT_FAILURE_PHASE="final-recovery-drift"
    mutated_service=1
    printf 'ERROR: final recovery state drifted after evidence capture\n' >&2
    return 1
}

capture_final_acceptance_snapshot() {
    local quiescence_path="$EVIDENCE_DIR/quiescence-before-acceptance.json"
    EXPECTED_SERVICE_READ_ONLY_STARTUP=false
    if [[ -z "$PINNED_RECOVERY_SERVICE_ID" ]]; then
        ROLLOUT_FAILURE_PHASE="final-recovery-drift"
        mutated_service=1
        return 1
    fi
    if ! capture_db_quiescence \
        "before-acceptance" \
        "$quiescence_path" \
        "$PINNED_RECOVERY_SERVICE_ID"; then
        ROLLOUT_FAILURE_PHASE="final-quiescence-drift"
        mutated_service=1
        printf 'ERROR: final DB quiescence snapshot failed\n' >&2
        return 1
    fi
    if ! verify_final_recovery_state; then
        return 1
    fi
    if ! record_role_verification final "$PINNED_RECOVERY_SERVICE_ID"; then
        ROLLOUT_FAILURE_PHASE="final-runtime-drift"
        mutated_service=1
        printf 'ERROR: final runtime snapshot failed after DB quiescence\n' >&2
        return 1
    fi
    if [[ "$(get_service_container_id)" != "$PINNED_RECOVERY_SERVICE_ID" ]]; then
        ROLLOUT_FAILURE_PHASE="final-runtime-drift"
        mutated_service=1
        return 1
    fi
    LAST_FINAL_VERIFICATION_PATH="$LAST_ROLE_VERIFICATION_PATH"
}

append_rollback_evidence() {
    local role_path="$1"
    local output="$EVIDENCE_DIR/rollback-evidence.jsonl"
    if ! python3 -c '
import json
import pathlib
import sys
role = json.loads(pathlib.Path(sys.argv[1]).read_text())
event = {
    "label": role.get("label"),
    "recordedAtUtc": role.get("observedAtUtc"),
    "manifestFingerprint": role.get("manifestFingerprint"),
    "serviceImageReference": role.get("fstserviceImageReference"),
    "serviceImageId": role.get("fstserviceImageId"),
    "serviceContainerId": role.get("fstserviceContainerId"),
    "serviceContainerHostname": role.get("fstserviceContainerHostname"),
    "serviceInstanceNonce": role.get("fstserviceInstanceNonce"),
    "serviceBaseUrl": role.get("fstserviceBaseUrl"),
    "workerContainerId": role.get("fstworkerContainerId"),
    "workerImageReference": role.get("fstworkerImageReference"),
    "workerImageId": role.get("fstworkerImageId"),
    "workerContainerStatus": role.get("fstworkerContainerStatus"),
    "workerContainerState": role.get("fstworkerContainerState"),
    "serviceReadOnlyStartup": role.get("fstserviceReadOnlyStartup"),
    "servicePostgresReadOnly": role.get("fstservicePostgresReadOnly"),
    "serviceDatabaseTarget": role.get("fstserviceDatabaseTarget"),
    "serviceDefaultTransactionReadOnlyOption":
        role.get("fstserviceDefaultTransactionReadOnlyOption"),
    "postgresContainerId": role.get("postgresContainerId"),
    "postgresImageReference": role.get("postgresImageReference"),
    "postgresImageId": role.get("postgresImageId"),
    "postgresNetworkNames": role.get("postgresNetworkNames"),
    "postgresNetworkAliases": role.get("postgresNetworkAliases"),
    "postgresServerAddresses": role.get("postgresServerAddresses"),
    "postgresNetworkBindings": role.get("postgresNetworkBindings"),
    "roleEvidenceFile": pathlib.Path(sys.argv[1]).name,
    "verified": True,
}
with pathlib.Path(sys.argv[2]).open("a", encoding="utf-8") as stream:
    stream.write(json.dumps(event, sort_keys=True) + "\n")
' "$role_path" "$output"; then
        printf 'ERROR: unable to append rollback evidence\n' >&2
        return 1
    fi
}

write_rollout_incident() {
    local exit_status="$1"
    local rollback_status="$2"
    local output
    if [[ -z "$EVIDENCE_DIR" ]] \
        || ! verify_evidence_mount \
        || [[ ! -f "$EVIDENCE_DIR/manifest.json" ]] \
        || ! verify_manifest_mount_binding "$EVIDENCE_DIR/manifest.json"; then
        return 1
    fi
    output="$EVIDENCE_DIR/rollout-incident-$(date -u +%Y%m%dT%H%M%S%NZ).json"
    python3 -c '
import json
import pathlib
import sys
payload = {
    "recordedAtUtc": sys.argv[2],
    "exitStatus": int(sys.argv[3]),
    "rollbackStatus": int(sys.argv[4]),
    "analysisStatus": int(sys.argv[5]),
    "mutationMarkerArmed": sys.argv[6] == "1",
    "failurePhase": sys.argv[7],
    "manifestFingerprint": sys.argv[8],
    "serviceImageReference": sys.argv[9],
    "serviceImageId": sys.argv[10],
    "accepted": False,
}
pathlib.Path(sys.argv[1]).write_text(json.dumps(payload, indent=2) + "\n")
' \
        "$output" \
        "$(date -u +%Y-%m-%dT%H:%M:%SZ)" \
        "$exit_status" \
        "$rollback_status" \
        "$PROVISIONAL_ANALYSIS_STATUS" \
        "$mutated_service" \
        "$ROLLOUT_FAILURE_PHASE" \
        "$ROLLOUT_MANIFEST_FINGERPRINT" \
        "$FST_STORED_RANK_SERVICE_IMAGE" \
        "$PINNED_SERVICE_IMAGE_ID"
}

rollback_on_exit() {
    local status=$?
    local rollback_status=0
    local final_status="$status"
    local failure_phase="$ROLLOUT_FAILURE_PHASE"
    trap - EXIT
    if (( mutated_service != 0 )); then
        set +e
        if [[ -n "$PINNED_RECOVERY_SERVICE_ID" ]] \
            && verify_normal_service_state "$PINNED_RECOVERY_SERVICE_ID"; then
            printf 'Completing evidence for the pinned normal fstservice state.\n' >&2
            complete_normal_recovery_evidence "$PINNED_RECOVERY_SERVICE_ID"
            rollback_status=$?
        else
            printf 'Restoring fstservice through false rollback and normal recovery after exit status %s\n' \
                "$status" >&2
            rollback_service
            rollback_status=$?
        fi
        set -e
        if (( rollback_status != 0 )); then
            printf 'ERROR: automatic rollback failed with status %s\n' \
                "$rollback_status" >&2
            if (( final_status == 0 )); then
                final_status="$rollback_status"
            fi
        fi
    fi
    if (( rollout_mutation_attempted != 0 \
        && (status != 0 || rollback_status != 0) )); then
        ROLLOUT_FAILURE_PHASE="$failure_phase"
        set +e
        write_rollout_incident "$status" "$rollback_status"
        set -e
    fi
    release_rollout_lock
    exit "$final_status"
}

attest_database_target() {
    local label="$1"
    local output digest
    verify_database_target_binding || return 1
    if [[ ! -f "$EVIDENCE_DIR/manifest.json" ]]; then
        printf 'ERROR: database attestation requires manifest.json\n' >&2
        return 1
    fi
    output="$EVIDENCE_DIR/database-attestation-$(date -u +%Y%m%dT%H%M%S%NZ)-$label.json"
    run_tool db-attest \
        --manifest "$EVIDENCE_DIR/manifest.json" \
        --output "$output" \
        || return 1
    read -r digest _ < <(sha256sum "$output") || return 1
    printf '%s  %s\n' "$digest" "$(basename "$output")" > "$output.sha256"
}

validate_database_target_in_memory() {
    verify_database_target_binding || return 1
    if [[ ! -f "$EVIDENCE_DIR/manifest.json" ]]; then
        printf 'ERROR: database attestation requires manifest.json\n' >&2
        return 1
    fi
    run_tool db-attest --manifest "$EVIDENCE_DIR/manifest.json"
}

capture_db_quiescence() {
    local label="$1"
    local output="$2"
    local pinned_service_id="${3:-}"
    local current_service_id
    local status=0 digest
    local -a preflight_arguments
    if [[ -n "$pinned_service_id" ]]; then
        current_service_id="$(get_service_container_id)" || return 1
        [[ "$current_service_id" == "$pinned_service_id" ]] || return 1
        resolve_service_traffic_binding "$pinned_service_id" || return 1
        wait_public_path "$pinned_service_id" || return 1
    fi
    verify_evidence_mount || return 1
    verify_worker_pin || return 1
    verify_database_target_binding || return 1
    if [[ -f "$EVIDENCE_DIR/manifest.json" ]]; then
        verify_manifest_image_pin "$EVIDENCE_DIR/manifest.json" || return 1
        verify_manifest_mount_binding "$EVIDENCE_DIR/manifest.json" || return 1
    fi
    preflight_arguments=(
        preflight
        --expected-published-scrape "$EXPECTED_PUBLISHED_SCRAPE_ID" \
        --output "$output"
    )
    if [[ -f "$EVIDENCE_DIR/manifest.json" ]]; then
        preflight_arguments+=(
            --manifest "$EVIDENCE_DIR/manifest.json"
        )
    fi
    if run_tool "${preflight_arguments[@]}"; then
        status=0
    else
        status=$?
    fi
    if [[ ! -f "$output" ]]; then
        return 1
    fi
    if ! python3 -c '
import json
import pathlib
import sys
path = pathlib.Path(sys.argv[1])
payload = json.loads(path.read_text(encoding="utf-8"))
payload["runtimeDatabaseBinding"] = {
    "serviceTarget": {
        "host": sys.argv[2],
        "port": int(sys.argv[3]),
        "database": sys.argv[4],
        "username": sys.argv[5],
    },
    "postgresContainerId": sys.argv[6],
    "postgresImageReference": sys.argv[7],
    "postgresImageId": sys.argv[8],
    "postgresNetworkNames": sys.argv[9].split(","),
    "postgresNetworkAliases": sys.argv[10].split(","),
    "postgresServerAddresses": sys.argv[11].split(","),
    "postgresNetworkBindings": json.loads(sys.argv[12]),
    "fstserviceContainerId": sys.argv[13],
    "fstserviceInstanceNonce": sys.argv[14],
}
path.write_text(json.dumps(payload, indent=2) + "\n", encoding="utf-8")
' \
        "$output" \
        "$SERVICE_DB_HOST" \
        "$SERVICE_DB_PORT" \
        "$SERVICE_DB_NAME" \
        "$SERVICE_DB_USERNAME" \
        "$PINNED_POSTGRES_CONTAINER_ID" \
        "$PINNED_POSTGRES_IMAGE_REFERENCE" \
        "$PINNED_POSTGRES_IMAGE_ID" \
        "$PINNED_POSTGRES_NETWORK_NAMES" \
        "$PINNED_POSTGRES_NETWORK_ALIASES" \
        "$PINNED_POSTGRES_SERVER_ADDRESSES" \
        "$PINNED_POSTGRES_NETWORK_BINDINGS_JSON" \
        "$pinned_service_id" \
        "$EXPECTED_SERVICE_INSTANCE_NONCE"; then
        return 1
    fi
    if ! read -r digest _ < <(sha256sum "$output"); then
        return 1
    fi
    if ! printf '%s  %s\n' "$digest" "$(basename "$output")" > "$output.sha256"; then
        return 1
    fi
    if ! python3 -c '
import json
import pathlib
import sys
entry = {
    "label": sys.argv[1],
    "file": pathlib.Path(sys.argv[2]).name,
    "sha256": sys.argv[3],
}
with pathlib.Path(sys.argv[4]).open("a", encoding="utf-8") as stream:
    stream.write(json.dumps(entry, sort_keys=True) + "\n")
' \
        "$label" \
        "$output" \
        "$digest" \
        "$EVIDENCE_DIR/quiescence-manifest.jsonl"; then
        return 1
    fi
    if [[ -n "$pinned_service_id" ]]; then
        current_service_id="$(get_service_container_id)" || return 1
        [[ "$current_service_id" == "$pinned_service_id" ]] || return 1
        wait_public_path "$pinned_service_id" || return 1
        current_service_id="$(get_service_container_id)" || return 1
        [[ "$current_service_id" == "$pinned_service_id" ]] || return 1
    fi
    return "$status"
}

preflight_path() {
    printf '%s/preflight-%s.json\n' "$EVIDENCE_DIR" "$1"
}

run_preflight() {
    local label="$1"
    local validate_manifest="${2:-true}"
    local runtime_dir="$EVIDENCE_DIR/runtime-preflight/$label"
    local pinned_service_id
    pinned_service_id="$(get_service_container_id)" || return 1
    [[ -n "$pinned_service_id" ]] || return 1
    resolve_service_traffic_binding "$pinned_service_id" || return 1
    verify_evidence_mount
    verify_worker_pin
    mkdir -p "$runtime_dir"
    run_docker_query_bounded compose \
        --project-directory "$COMPOSE_DIR" \
        -f "$BASE_COMPOSE_FILE" \
        ps --all --format json \
        > "$runtime_dir/compose-ps.jsonl"
    mapfile -t runtime_containers < <(
        run_docker_query_bounded compose \
            --project-directory "$COMPOSE_DIR" \
            -f "$BASE_COMPOSE_FILE" \
            ps -q postgres fstservice festivalweb
    )
    if (( ${#runtime_containers[@]} != 3 )); then
        printf 'ERROR: expected running postgres/service/web containers before A/B\n' >&2
        return 1
    fi
    run_docker_query_bounded \
        stats \
        --no-stream \
        --format '{{json .}}' \
        "${runtime_containers[@]}" \
        > "$runtime_dir/docker-stats.jsonl"
    df -P "$FST_STORED_RANK_EVIDENCE_ROOT" > "$runtime_dir/disk.txt"
    capture_db_quiescence \
        "$label" \
        "$(preflight_path "$label")" \
        "$pinned_service_id"
    if [[ "$validate_manifest" == "true" && -f "$EVIDENCE_DIR/manifest.json" ]]; then
        verify_manifest_image_pin "$EVIDENCE_DIR/manifest.json"
        verify_manifest_mount_binding "$EVIDENCE_DIR/manifest.json"
        run_tool guard \
            --manifest "$EVIDENCE_DIR/manifest.json" \
            --output "$EVIDENCE_DIR/manifest-guard-$label.json"
    fi
    [[ "$(get_service_container_id)" == "$pinned_service_id" ]]
}

prepare_package() {
    if [[ -z "$EXPECTED_PUBLISHED_SCRAPE_ID" ]]; then
        printf 'ERROR: EXPECTED_PUBLISHED_SCRAPE_ID is required\n' >&2
        exit 64
    fi
    run_preflight prepare false
    run_tool manifest \
        --seed "$ROLLOUT_SEED" \
        --service-image "$FST_STORED_RANK_SERVICE_IMAGE" \
        --service-image-id "$PINNED_SERVICE_IMAGE_ID" \
        --worker-container-id "$PINNED_WORKER_CONTAINER_ID" \
        --worker-image "$PINNED_WORKER_IMAGE_REFERENCE" \
        --worker-image-id "$PINNED_WORKER_IMAGE_ID" \
        --worker-container-status "$PINNED_WORKER_CONTAINER_STATUS" \
        --worker-container-state "$PINNED_WORKER_CONTAINER_STATE" \
        --service-db-host "$SERVICE_DB_HOST" \
        --service-db-port "$SERVICE_DB_PORT" \
        --service-db-name "$SERVICE_DB_NAME" \
        --service-db-username "$SERVICE_DB_USERNAME" \
        --postgres-container-id "$PINNED_POSTGRES_CONTAINER_ID" \
        --postgres-image "$PINNED_POSTGRES_IMAGE_REFERENCE" \
        --postgres-image-id "$PINNED_POSTGRES_IMAGE_ID" \
        --postgres-network-names "$PINNED_POSTGRES_NETWORK_NAMES" \
        --postgres-network-aliases "$PINNED_POSTGRES_NETWORK_ALIASES" \
        --postgres-server-addresses "$PINNED_POSTGRES_SERVER_ADDRESSES" \
        --postgres-network-bindings-json "$PINNED_POSTGRES_NETWORK_BINDINGS_JSON" \
        --evidence-mount-target "$EVIDENCE_MOUNT_TARGET" \
        --evidence-mount-source "$EVIDENCE_MOUNT_SOURCE" \
        --evidence-mount-filesystem "$EVIDENCE_MOUNT_FSTYPE" \
        --output "$EVIDENCE_DIR/manifest.json"
    verify_manifest_image_pin "$EVIDENCE_DIR/manifest.json"
    verify_manifest_mount_binding "$EVIDENCE_DIR/manifest.json"
    capture_db_quiescence \
        "manifest-bound" \
        "$EVIDENCE_DIR/quiescence-manifest-bound.json" \
        "$EXPECTED_SERVICE_CONTAINER_ID"
    run_tool guard \
        --manifest "$EVIDENCE_DIR/manifest.json" \
        --output "$EVIDENCE_DIR/manifest-guard-prepare.json"
    run_tool row-parity \
        --manifest "$EVIDENCE_DIR/manifest.json" \
        --output "$EVIDENCE_DIR/row-parity.json"
    run_tool schedule \
        --seed "$ROLLOUT_SEED" \
        --manifest "$EVIDENCE_DIR/manifest.json" \
        --output "$EVIDENCE_DIR/benchmark-schedule.tsv"
}

capture_variant() {
    local sequence="$1"
    local variant="$2"
    run_preflight "api-$sequence-$variant"
    recreate_service_variant "$variant"
    attest_database_target "before-api-$sequence-$variant"
    run_tool api-capture \
        --manifest "$EVIDENCE_DIR/manifest.json" \
        --base-url "$BASE_URL" \
        --variant "$variant-$sequence" \
        --output-dir "$EVIDENCE_DIR/api/$sequence-$variant"
    verify_block_state "$variant"
    capture_db_quiescence \
        "after-api-$sequence-$variant" \
        "$EVIDENCE_DIR/quiescence-after-api-$sequence-$variant.json" \
        "$ACTIVE_BLOCK_CONTAINER_ID"
}

run_api_abba() {
    capture_variant 1 baseline
    capture_variant 2 candidate
    capture_variant 3 candidate
    capture_variant 4 baseline

    run_tool api-compare \
        --baseline-report "$EVIDENCE_DIR/api/1-baseline/capture.json" \
        --candidate-report "$EVIDENCE_DIR/api/2-candidate/capture.json" \
        --output "$EVIDENCE_DIR/api-comparison.json"
    run_tool api-compare \
        --baseline-report "$EVIDENCE_DIR/api/1-baseline/capture.json" \
        --candidate-report "$EVIDENCE_DIR/api/3-candidate/capture.json" \
        --output "$EVIDENCE_DIR/api-comparison-candidate-repeat.json"
    run_tool api-compare \
        --baseline-report "$EVIDENCE_DIR/api/1-baseline/capture.json" \
        --candidate-report "$EVIDENCE_DIR/api/4-baseline/capture.json" \
        --output "$EVIDENCE_DIR/api-comparison-baseline-repeat.json"
}

run_benchmarks() {
    mkdir -p "$EVIDENCE_DIR/benchmark-blocks"
    while IFS=$'\t' read -r sequence mode concurrency workload_id abba_block position variant request_count; do
        if [[ "$sequence" == "sequence" ]]; then
            continue
        fi
        run_preflight "bench-$sequence"
        recreate_service_variant "$variant"
        attest_database_target "before-benchmark-$sequence-$variant"
        run_tool benchmark-block \
            --manifest "$EVIDENCE_DIR/manifest.json" \
            --workload-id "$workload_id" \
            --sequence "$sequence" \
            --mode "$mode" \
            --concurrency "$concurrency" \
            --variant "$variant" \
            --request-count "$request_count" \
            --base-url "$BASE_URL" \
            --postgres-container "$POSTGRES_CONTAINER" \
            --warm-request-starts-per-second "$WARM_REQUEST_STARTS_PER_SECOND" \
            --output "$EVIDENCE_DIR/benchmark-blocks/block-$sequence.json"
        verify_block_state "$variant"
        capture_db_quiescence \
            "after-benchmark-$sequence-$variant" \
            "$EVIDENCE_DIR/quiescence-after-benchmark-$sequence-$variant.json" \
            "$ACTIVE_BLOCK_CONTAINER_ID"
    done < "$EVIDENCE_DIR/benchmark-schedule.tsv"

    run_preflight benchmark-final
    PROVISIONAL_ANALYSIS_STATUS=0
    set +e
    run_tool analyze \
        --manifest "$EVIDENCE_DIR/manifest.json" \
        --row-parity "$EVIDENCE_DIR/row-parity.json" \
        --api-comparison "$EVIDENCE_DIR/api-comparison.json" \
        --blocks-dir "$EVIDENCE_DIR/benchmark-blocks" \
        --output "$EVIDENCE_DIR/analysis-provisional.json"
    PROVISIONAL_ANALYSIS_STATUS=$?
    set -e
}

run_standalone_rollback() {
    local lock_status
    require_evidence_configuration true false
    build_tool
    load_rollout_manifest_bindings
    validate_database_target_in_memory

    ROLLOUT_FAILURE_PHASE="standalone-rollback-armed"
    mutated_service=1
    rollout_mutation_attempted=1
    trap rollback_on_exit EXIT

    if acquire_rollout_lock; then
        :
    else
        lock_status=$?
        ROLLOUT_FAILURE_PHASE="standalone-evidence-lock"
        return "$lock_status"
    fi

    rollback_service
    trap - EXIT
    release_rollout_lock
}

case "$ACTION" in
    help|-h|--help)
        usage
        ;;
    validate)
        require_rollout_commands
        validate_docker_timeouts
        build_tool
        run_tool self-test
        PG_PASSWORD=static-validation API_KEY=static-validation \
            FST_STORED_RANK_SERVICE_IMAGE="ghcr.io/sfenton/fstservice:reviewed@sha256:0000000000000000000000000000000000000000000000000000000000000000" \
            validate_overlay \
                "$TRUE_OVERLAY" \
                true \
                true \
                "$REPO_ROOT/deploy" \
                "$REPO_ROOT/deploy/docker-compose.yml"
        PG_PASSWORD=static-validation API_KEY=static-validation \
            FST_STORED_RANK_SERVICE_IMAGE="ghcr.io/sfenton/fstservice:reviewed@sha256:0000000000000000000000000000000000000000000000000000000000000000" \
            validate_overlay \
                "$FALSE_OVERLAY" \
                false \
                true \
                "$REPO_ROOT/deploy" \
                "$REPO_ROOT/deploy/docker-compose.yml"
        PG_PASSWORD=static-validation API_KEY=static-validation \
            FST_STORED_RANK_SERVICE_IMAGE="ghcr.io/sfenton/fstservice:reviewed@sha256:0000000000000000000000000000000000000000000000000000000000000000" \
            validate_overlay \
                "$RECOVERY_OVERLAY" \
                false \
                false \
                "$REPO_ROOT/deploy" \
                "$REPO_ROOT/deploy/docker-compose.yml"
        printf 'Stored-rank rollout package validation passed.\n'
        ;;
    prepare)
        require_rollout_commands
        validate_docker_timeouts
        require_evidence_configuration
        acquire_rollout_lock
        resolve_pinned_service_image
        build_tool
        resolve_database_target_binding
        prepare_package
        release_rollout_lock
        ;;
    run)
        require_rollout_commands
        validate_docker_timeouts
        require_evidence_configuration
        if [[ "$ALLOW_SERVICE_RECREATE" != "YES" ]]; then
            printf 'ERROR: run requires ALLOW_SERVICE_RECREATE=YES\n' >&2
            exit 64
        fi
        if [[ ! "$WARM_REQUEST_STARTS_PER_SECOND" =~ ^[0-9]+$ ]] \
            || (( WARM_REQUEST_STARTS_PER_SECOND < 1 || WARM_REQUEST_STARTS_PER_SECOND > 90 )); then
            printf 'ERROR: WARM_REQUEST_STARTS_PER_SECOND must be between 1 and 90\n' >&2
            exit 64
        fi
        acquire_rollout_lock
        require_unfinalized_run_directory
        resolve_pinned_service_image
        trap rollback_on_exit EXIT
        build_tool
        resolve_database_target_binding
        prepare_package
        run_api_abba
        run_benchmarks
        rollback_service
        verify_evidence_mount
        verify_manifest_mount_binding "$EVIDENCE_DIR/manifest.json"
        capture_final_acceptance_snapshot
        run_tool finalize-acceptance \
            --manifest "$EVIDENCE_DIR/manifest.json" \
            --analysis "$EVIDENCE_DIR/analysis-provisional.json" \
            --rollback-evidence "$LAST_ROLLBACK_VERIFICATION_PATH" \
            --recovery-evidence "$LAST_RECOVERY_VERIFICATION_PATH" \
            --final-evidence "$LAST_FINAL_VERIFICATION_PATH" \
            --final-quiescence "$EVIDENCE_DIR/quiescence-before-acceptance.json" \
            --final-quiescence-sha256 "$EVIDENCE_DIR/quiescence-before-acceptance.json.sha256" \
            --output "$EVIDENCE_DIR/acceptance.json"
        trap - EXIT
        release_rollout_lock
        printf 'Stored-rank service-only A/B complete: %s\n' "$EVIDENCE_DIR"
        ;;
    rollback)
        require_rollout_commands
        validate_docker_timeouts
        run_standalone_rollback
        ;;
    test-wait-public-path)
        require_command curl
        require_command realpath
        if [[ -z "${ROLLOUT_TEST_ROLLBACK_MARKER:-}" ]]; then
            printf 'ERROR: ROLLOUT_TEST_ROLLBACK_MARKER is required\n' >&2
            exit 64
        fi
        test_marker="$(realpath -m "$ROLLOUT_TEST_ROLLBACK_MARKER")"
        case "$test_marker/" in
            "$REPO_ROOT/"*) ;;
            *)
                printf 'ERROR: test rollback marker must remain under the repository\n' >&2
                exit 64
                ;;
        esac
        rollback_service() {
            printf 'rollback\n' > "$test_marker"
            mutated_service=0
        }
        BASE_URL="$REQUESTED_BASE_URL"
        EXPECTED_SERVICE_CONTAINER_ID="test-service"
        EXPECTED_SERVICE_CONTAINER_HOSTNAME="test-service-host"
        EXPECTED_SERVICE_INSTANCE_NONCE="aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"
        resolve_service_traffic_binding() {
            return 0
        }
        get_service_container_id() {
            printf 'test-service\n'
        }
        trap rollback_on_exit EXIT
        mutated_service=1
        wait_status=0
        wait_public_path || wait_status=$?
        if (( wait_status == 0 )); then
            trap - EXIT
            printf 'ERROR: hanging endpoint unexpectedly became healthy\n' >&2
            exit 1
        fi
        exit "$wait_status"
        ;;
    test-partial-recreate)
        require_command realpath
        if [[ -z "${ROLLOUT_TEST_ROLLBACK_MARKER:-}" \
            || -z "${ROLLOUT_TEST_EVENT_LOG:-}" ]]; then
            printf 'ERROR: rollback marker and event log are required\n' >&2
            exit 64
        fi
        test_marker="$(realpath -m "$ROLLOUT_TEST_ROLLBACK_MARKER")"
        test_log="$(realpath -m "$ROLLOUT_TEST_EVENT_LOG")"
        case "$test_marker/" in "$REPO_ROOT/"*) ;; *) exit 64 ;; esac
        case "$test_log/" in "$REPO_ROOT/"*) ;; *) exit 64 ;; esac
        : > "$test_log"
        validate_overlay() {
            printf 'validate\n' >> "$test_log"
            return 0
        }
        run_docker_recreate_bounded() {
            printf 'compose-up:marker=%s\n' "$mutated_service" >> "$test_log"
            return 42
        }
        rollback_service() {
            printf 'rollback:marker=%s\n' "$mutated_service" >> "$test_log"
            printf 'rollback\n' > "$test_marker"
            mutated_service=0
            return 0
        }
        trap rollback_on_exit EXIT
        recreate_service_variant candidate
        ;;
    test-hanging-docker-recreate)
        require_command realpath
        require_command timeout
        if [[ -z "${ROLLOUT_TEST_ROLLBACK_MARKER:-}" \
            || -z "${ROLLOUT_TEST_EVENT_LOG:-}" ]]; then
            printf 'ERROR: rollback marker and event log are required\n' >&2
            exit 64
        fi
        test_marker="$(realpath -m "$ROLLOUT_TEST_ROLLBACK_MARKER")"
        test_log="$(realpath -m "$ROLLOUT_TEST_EVENT_LOG")"
        case "$test_marker/" in "$REPO_ROOT/"*) ;; *) exit 64 ;; esac
        case "$test_log/" in "$REPO_ROOT/"*) ;; *) exit 64 ;; esac
        : > "$test_log"
        validate_overlay() {
            printf 'validate\n' >> "$test_log"
            return 0
        }
        rollback_service() {
            printf 'rollback:marker=%s\n' "$mutated_service" >> "$test_log"
            printf 'rollback\n' > "$test_marker"
            mutated_service=0
            return 0
        }
        validate_docker_timeouts
        trap rollback_on_exit EXIT
        recreate_service_variant candidate
        ;;
    test-rollback-step-failure)
        require_command realpath
        if [[ -z "${ROLLOUT_TEST_EVENT_LOG:-}" ]]; then
            printf 'ERROR: event log is required\n' >&2
            exit 64
        fi
        test_log="$(realpath -m "$ROLLOUT_TEST_EVENT_LOG")"
        case "$test_log/" in "$REPO_ROOT/"*) ;; *) exit 64 ;; esac
        : > "$test_log"
        recreate_count=0
        validate_overlay() {
            printf 'validate\n' >> "$test_log"
            return 0
        }
        run_docker_recreate_bounded() {
            recreate_count=$((recreate_count + 1))
            printf 'false-recreate\n' >> "$test_log"
            return 0
        }
        verify_container_image() {
            return 0
        }
        verify_worker_pin() {
            return 0
        }
        verify_database_target_binding() {
            return 0
        }
        get_service_container_id() {
            printf 'service-%s\n' "$recreate_count"
        }
        resolve_service_traffic_binding() {
            return 0
        }
        verify_container_env() {
            printf 'verify:%s:%s:%s\n' "$1" "$2" "$3" >> "$test_log"
            if (( recreate_count == 1 )) \
                && [[ "$1" == "fstworker" \
                && "$2" == "Features__UseStoredSoloProjectionRanksForFilteredReads" ]]; then
                return 1
            fi
            return 0
        }
        verify_container_env_by_id() {
            return 0
        }
        wait_public_path() {
            printf 'health\n' >> "$test_log"
            return 0
        }
        record_role_verification() {
            printf 'evidence\n' >> "$test_log"
            return 0
        }
        mutated_service=1
        set +e
        rollback_service
        rollback_status=$?
        set -e
        printf 'marker=%s\n' "$mutated_service" >> "$test_log"
        exit "$rollback_status"
        ;;
    test-image-pin-resolution)
        require_command realpath
        require_command python3
        if [[ -z "${ROLLOUT_TEST_EVENT_LOG:-}" ]]; then
            printf 'ERROR: event log is required\n' >&2
            exit 64
        fi
        test_log="$(realpath -m "$ROLLOUT_TEST_EVENT_LOG")"
        case "$test_log/" in "$REPO_ROOT/"*) ;; *) exit 64 ;; esac
        : > "$test_log"
        test_expected_id="${ROLLOUT_TEST_EXPECTED_IMAGE_ID:-sha256:bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb}"
        test_running_id="${ROLLOUT_TEST_RUNNING_IMAGE_ID:-$test_expected_id}"
        test_configured_tag="${EXPECTED_FSTSERVICE_IMAGE%@sha256:*}"
        run_docker_query_bounded() {
            local joined="$*"
            if [[ "$joined" == *"config --format json"* ]]; then
                printf '{"services":{"fstservice":{"image":"%s"}}}\n' \
                    "$test_configured_tag"
            elif [[ "$joined" == *"ps -q fstservice"* \
                || "$joined" == *"ps --all -q fstservice"* ]]; then
                printf 'service-container\n'
            elif [[ "$joined" == *"ps --all -q fstworker"* ]]; then
                printf 'worker-container\n'
            elif [[ "$joined" == *"worker-container"* \
                && "$joined" == *"{{.Image}}|{{.Config.Image}}|{{.State.Status}}"* ]]; then
                printf 'sha256:cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc|ghcr.io/sfenton/fstservice:worker|exited|2026-08-04T00:00:00Z|2026-08-04T01:00:00Z|0\n'
            elif [[ "$joined" == *"worker-container"* \
                && "$joined" == *"{{range .Config.Env}}"* ]]; then
                printf '%s\n' \
                    'Features__UseStoredSoloProjectionRanksForFilteredReads=false' \
                    'Scraper__RolloutReadOnlyStartup=false' \
                    'Scraper__RolloutPostgresReadOnly=false' \
                    'Features__UsePublishedScopeSources=false'
            elif [[ "$joined" == *"{{.Image}}|{{.Config.Image}}"* ]]; then
                printf '%s|%s\n' "$test_running_id" "$EXPECTED_FSTSERVICE_IMAGE"
            elif [[ "$joined" == *"{{.Config.Image}}"* ]]; then
                printf '%s\n' "$test_configured_tag"
            elif [[ "$joined" == *"{{.Image}}"* ]]; then
                printf '%s\n' "$test_running_id"
            elif [[ "$joined" == image\ inspect* ]]; then
                printf '%s\n' "$test_expected_id"
            else
                printf 'ERROR: unexpected mock docker call: %s\n' "$joined" >&2
                return 1
            fi
        }
        resolve_pinned_service_image
        verify_container_image fstservice
        printf 'reference=%s\nid=%s\n' \
            "$FST_STORED_RANK_SERVICE_IMAGE" \
            "$PINNED_SERVICE_IMAGE_ID" \
            >> "$test_log"
        ;;
    test-database-target-binding)
        if [[ -z "${ROLLOUT_TEST_EVENT_LOG:-}" ]]; then
            exit 64
        fi
        test_log="$(realpath -m "$ROLLOUT_TEST_EVENT_LOG")"
        case "$test_log/" in "$REPO_ROOT/"*) ;; *) exit 64 ;; esac
        : > "$test_log"
        EVIDENCE_DIR=""
        test_mode="${ROLLOUT_TEST_DATABASE_BINDING_MODE:-valid}"
        test_phase=0
        run_docker_query_bounded() {
            local joined="$*"
            if [[ "$joined" == *"config --format json"* ]]; then
                printf '%s\n' \
                    '{"services":{"fstservice":{"environment":{"ConnectionStrings__PostgreSQL":"Host=postgres;Port=5432;Database=fstservice;Username=fst;Password=not-logged"}}}}'
            elif [[ "$joined" == *"ps --all -q postgres"* ]]; then
                if [[ "$test_mode" == "container-drift" && "$test_phase" == "1" ]]; then
                    printf 'replacement-postgres\n'
                else
                    printf 'production-postgres\n'
                fi
            elif [[ "$joined" == *"{{.Id}}"* ]]; then
                if [[ "$test_mode" == "container-mismatch" ]]; then
                    printf 'clone-postgres\n'
                else
                    printf 'production-postgres\n'
                fi
            elif [[ "$joined" == *"{{.Image}}|{{.Config.Image}}"* \
                && "$joined" == *"production-postgres"* ]]; then
                printf '%s\n' \
                    'sha256:dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd|fst-postgres:17-repack'
            elif [[ "$joined" == *"ps --all -q fstservice"* ]]; then
                printf 'service-container\n'
            elif [[ "$joined" == *"{{json .NetworkSettings.Networks}}"* \
                && "$joined" == *"production-postgres"* ]]; then
                if [[ "$test_mode" == "alias-drift" && "$test_phase" == "1" ]]; then
                    printf '%s\n' \
                        '{"fst-network":{"NetworkID":"network-id","IPAddress":"172.20.0.2","Aliases":["clone-postgres"]}}'
                else
                    test_network_id="network-id"
                    if [[ "$test_mode" == "network-id-drift" \
                        && "$test_phase" == "1" ]]; then
                        test_network_id="replacement-network-id"
                    fi
                    printf '%s\n' \
                        "{\"fst-network\":{\"NetworkID\":\"$test_network_id\",\"IPAddress\":\"172.20.0.2\",\"Aliases\":[\"postgres\",\"fst-postgres\",\"production-postgres\"],\"DNSNames\":[\"postgres\",\"fst-postgres\"]}}"
                fi
            elif [[ "$joined" == *"{{json .NetworkSettings.Networks}}"* \
                && "$joined" == *"service-container"* ]]; then
                test_network_id="network-id"
                if [[ "$test_mode" == "network-id-drift" \
                    && "$test_phase" == "1" ]]; then
                    test_network_id="replacement-network-id"
                fi
                printf '%s\n' \
                    "{\"fst-network\":{\"NetworkID\":\"$test_network_id\",\"IPAddress\":\"172.20.0.3\",\"Aliases\":[\"fstservice\"],\"DNSNames\":[\"fstservice\"]}}"
            elif [[ "$joined" == *"network inspect"* ]]; then
                if [[ "$test_mode" == "duplicate-alias" \
                    || "$test_mode" == "dns-name-clone" \
                    || "$test_mode" == "container-name-clone" ]]; then
                    test_clone_name="clone-postgres"
                    if [[ "$test_mode" == "container-name-clone" ]]; then
                        test_clone_name="/postgres"
                    fi
                    printf '%s\n' \
                        "{\"production-postgres\":{\"Name\":\"fst-postgres\"},\"service-container\":{\"Name\":\"fstservice\"},\"clone-postgres\":{\"Name\":\"$test_clone_name\"}}"
                else
                    printf '%s\n' \
                        '{"production-postgres":{"Name":"fst-postgres"},"service-container":{"Name":"fstservice"}}'
                fi
            elif [[ "$joined" == *"{{json .NetworkSettings.Networks}}"* \
                && "$joined" == *"clone-postgres"* ]]; then
                if [[ "$test_mode" == "duplicate-alias" ]]; then
                    printf '%s\n' \
                        '{"fst-network":{"NetworkID":"network-id","IPAddress":"172.20.0.4","Aliases":["postgres","clone-postgres"],"DNSNames":["clone-postgres"]}}'
                elif [[ "$test_mode" == "dns-name-clone" ]]; then
                    printf '%s\n' \
                        '{"fst-network":{"NetworkID":"network-id","IPAddress":"172.20.0.4","Aliases":["clone-postgres"],"DNSNames":["postgres"]}}'
                else
                    printf '%s\n' \
                        '{"fst-network":{"NetworkID":"network-id","IPAddress":"172.20.0.4","Aliases":["clone-postgres"],"DNSNames":[]}}'
                fi
            else
                printf 'ERROR: unexpected mock docker call: %s\n' "$joined" >&2
                return 1
            fi
        }
        resolve_database_target_binding
        printf '%s\n' \
            "$SERVICE_DB_HOST|$SERVICE_DB_PORT|$SERVICE_DB_NAME|$SERVICE_DB_USERNAME" \
            "$PINNED_POSTGRES_CONTAINER_ID|$PINNED_POSTGRES_IMAGE_REFERENCE|$PINNED_POSTGRES_IMAGE_ID" \
            "$PINNED_POSTGRES_NETWORK_NAMES|$PINNED_POSTGRES_NETWORK_ALIASES|$PINNED_POSTGRES_SERVER_ADDRESSES" \
            "$PINNED_POSTGRES_NETWORK_BINDINGS_JSON" \
            "visibility=$(
                python3 -c '
import sys
pairs = {}
for item in sys.argv[1].split(";"):
    if "=" in item:
        key, value = item.split("=", 1)
        pairs[key.strip().lower()] = value.strip()
print(pairs.get("host", "") + "|" + pairs.get("username", ""))
' "$FST_STORED_RANK_VISIBILITY_PROBE_CONNECTION_STRING"
            )" \
            >> "$test_log"
        test_phase=1
        set +e
        verify_database_target_binding
        verify_status=$?
        set -e
        printf 'verify=%s\n' "$verify_status" >> "$test_log"
        exit "$verify_status"
        ;;
    test-health-check)
        require_command curl
        require_command python3
        BASE_URL="$REQUESTED_BASE_URL"
        SERVICE_DB_HOST="${ROLLOUT_TEST_SERVICE_DB_HOST:-postgres}"
        SERVICE_DB_PORT="${ROLLOUT_TEST_SERVICE_DB_PORT:-5432}"
        SERVICE_DB_NAME="${ROLLOUT_TEST_SERVICE_DB_NAME:-fstservice}"
        SERVICE_DB_USERNAME="${ROLLOUT_TEST_SERVICE_DB_USERNAME:-fst}"
        EXPECTED_SERVICE_CONTAINER_ID="${ROLLOUT_TEST_SERVICE_CONTAINER_ID:-test-service}"
        EXPECTED_SERVICE_CONTAINER_HOSTNAME="${ROLLOUT_TEST_SERVICE_HOSTNAME:-test-service-host}"
        EXPECTED_SERVICE_INSTANCE_NONCE="${ROLLOUT_TEST_SERVICE_NONCE:-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa}"
        resolve_service_traffic_binding() {
            return 0
        }
        get_service_container_id() {
            printf '%s\n' "$EXPECTED_SERVICE_CONTAINER_ID"
        }
        wait_public_path
        ;;
    test-service-traffic-binding)
        if [[ -z "${ROLLOUT_TEST_EVENT_LOG:-}" ]]; then
            exit 64
        fi
        test_log="$(realpath -m "$ROLLOUT_TEST_EVENT_LOG")"
        get_service_container_id() {
            printf 'service-container-id\n'
        }
        run_docker_query_bounded() {
            printf '%s\n' \
                'service-hostname|[{"HostIp":"127.0.0.1","HostPort":"18081"}]'
        }
        resolve_service_traffic_binding
        printf '%s|%s|%s\n' \
            "$EXPECTED_SERVICE_CONTAINER_ID" \
            "$EXPECTED_SERVICE_CONTAINER_HOSTNAME" \
            "$BASE_URL" \
            > "$test_log"
        ;;
    test-mount-binding)
        require_command python3
        if [[ -z "${ROLLOUT_TEST_EVENT_LOG:-}" ]]; then
            exit 64
        fi
        test_log="$(realpath -m "$ROLLOUT_TEST_EVENT_LOG")"
        run_findmnt() {
            printf '{"filesystems":[{"target":"%s","source":"%s","fstype":"%s","options":"rw,relatime"}]}\n' \
                "${ROLLOUT_TEST_MOUNT_TARGET:-/mnt/docker-storage}" \
                "${ROLLOUT_TEST_MOUNT_SOURCE:-/dev/test-fst}" \
                "${ROLLOUT_TEST_MOUNT_FSTYPE:-ext4}"
        }
        verify_evidence_mount
        printf '%s|%s|%s\n' \
            "$EVIDENCE_MOUNT_TARGET" \
            "$EVIDENCE_MOUNT_SOURCE" \
            "$EVIDENCE_MOUNT_FSTYPE" \
            > "$test_log"
        ;;
    test-hold-lock)
        require_command flock
        acquire_rollout_lock
        printf 'locked\n'
        sleep "${ROLLOUT_TEST_LOCK_HOLD_SECONDS:-3}"
        release_rollout_lock
        ;;
    test-try-lock)
        require_command flock
        acquire_rollout_lock
        release_rollout_lock
        ;;
    test-block-identity)
        ACTIVE_BLOCK_CONTAINER_ID="expected-container"
        ACTIVE_BLOCK_VARIANT="candidate"
        get_service_container_id() {
            printf 'replacement-container\n'
        }
        verify_block_state candidate
        ;;
    test-standalone-rollback-incident)
        if [[ -z "${ROLLOUT_TEST_EVIDENCE_DIR:-}" ]]; then
            exit 64
        fi
        EVIDENCE_DIR="$(realpath -m "$ROLLOUT_TEST_EVIDENCE_DIR")"
        case "$EVIDENCE_DIR/" in "$REPO_ROOT/"*) ;; *) exit 64 ;; esac
        ROLLOUT_MANIFEST_FINGERPRINT="test-manifest"
        FST_STORED_RANK_SERVICE_IMAGE="ghcr.io/test/service:test@sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"
        PINNED_SERVICE_IMAGE_ID="sha256:bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"
        ROLLOUT_FAILURE_PHASE="read-only-rollback"
        verify_evidence_mount() {
            return 0
        }
        verify_manifest_mount_binding() {
            return 0
        }
        verify_normal_service_state() {
            return 1
        }
        rollback_service() {
            mutated_service=1
            return 55
        }
        trap rollback_on_exit EXIT
        mutated_service=1
        rollout_mutation_attempted=1
        rollback_service
        ;;
    test-load-rollback-manifest)
        if [[ -z "${ROLLOUT_TEST_EVIDENCE_DIR:-}" \
            || -z "${ROLLOUT_TEST_EVENT_LOG:-}" ]]; then
            exit 64
        fi
        EVIDENCE_DIR="$(realpath -m "$ROLLOUT_TEST_EVIDENCE_DIR")"
        test_log="$(realpath -m "$ROLLOUT_TEST_EVENT_LOG")"
        case "$EVIDENCE_DIR/" in "$REPO_ROOT/"*) ;; *) exit 64 ;; esac
        case "$test_log/" in "$REPO_ROOT/"*) ;; *) exit 64 ;; esac
        EVIDENCE_MOUNT_TARGET="/mnt/docker-storage"
        EVIDENCE_MOUNT_SOURCE="/dev/test-fst"
        EVIDENCE_MOUNT_FSTYPE="ext4"
        run_tool() {
            return 0
        }
        run_docker_query_bounded() {
            printf 'sha256:bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb\n'
        }
        verify_database_target_binding() {
            return 0
        }
        load_rollout_manifest_bindings
        printf 'publishedScrapeId=%s\n' "$EXPECTED_PUBLISHED_SCRAPE_ID" > "$test_log"
        ;;
    test-normal-recovery-incident)
        if [[ -z "${ROLLOUT_TEST_EVIDENCE_DIR:-}" ]]; then
            exit 64
        fi
        EVIDENCE_DIR="$(realpath -m "$ROLLOUT_TEST_EVIDENCE_DIR")"
        case "$EVIDENCE_DIR/" in "$REPO_ROOT/"*) ;; *) exit 64 ;; esac
        ROLLOUT_MANIFEST_FINGERPRINT="test-manifest"
        FST_STORED_RANK_SERVICE_IMAGE="ghcr.io/test/service:test@sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"
        PINNED_SERVICE_IMAGE_ID="sha256:bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"
        ROLLOUT_FAILURE_PHASE="normal-mode-recovery"
        verify_evidence_mount() {
            return 0
        }
        verify_manifest_mount_binding() {
            return 0
        }
        verify_normal_service_state() {
            return 1
        }
        rollback_service() {
            mutated_service=1
            return 66
        }
        trap rollback_on_exit EXIT
        mutated_service=1
        rollout_mutation_attempted=1
        rollback_service
        ;;
    test-rollback-evidence-failure)
        if [[ -z "${ROLLOUT_TEST_EVENT_LOG:-}" ]]; then
            exit 64
        fi
        test_log="$(realpath -m "$ROLLOUT_TEST_EVENT_LOG")"
        : > "$test_log"
        validate_overlay() {
            return 0
        }
        run_docker_recreate_bounded() {
            printf 'recreate\n' >> "$test_log"
            return 0
        }
        verify_container_image() {
            return 0
        }
        verify_worker_pin() {
            return 0
        }
        verify_database_target_binding() {
            return 0
        }
        get_service_container_id() {
            printf 'service-container\n'
        }
        resolve_service_traffic_binding() {
            return 0
        }
        verify_container_env() {
            return 0
        }
        verify_container_env_by_id() {
            return 0
        }
        wait_public_path() {
            return 0
        }
        capture_db_quiescence() {
            return 0
        }
        persist_role_evidence() {
            printf 'evidence:%s:%s\n' \
                "$1" \
                "${ROLLOUT_TEST_EVIDENCE_FAILURE:-unavailable}" \
                >> "$test_log"
            return 1
        }
        set +e
        rollback_service
        rollback_status=$?
        set -e
        printf 'status=%s marker=%s phase=%s\n' \
            "$rollback_status" \
            "$mutated_service" \
            "$ROLLOUT_FAILURE_PHASE" \
            >> "$test_log"
        exit "$rollback_status"
        ;;
    test-recovery-evidence-failure)
        if [[ -z "${ROLLOUT_TEST_EVENT_LOG:-}" ]]; then
            exit 64
        fi
        test_log="$(realpath -m "$ROLLOUT_TEST_EVENT_LOG")"
        : > "$test_log"
        validate_overlay() {
            return 0
        }
        run_docker_recreate_bounded() {
            printf 'recreate\n' >> "$test_log"
            return 0
        }
        verify_container_image() {
            return 0
        }
        verify_worker_pin() {
            return 0
        }
        verify_database_target_binding() {
            return 0
        }
        get_service_container_id() {
            printf 'service-container\n'
        }
        resolve_service_traffic_binding() {
            return 0
        }
        verify_container_env() {
            return 0
        }
        verify_container_env_by_id() {
            return 0
        }
        wait_public_path() {
            return 0
        }
        capture_db_quiescence() {
            return 0
        }
        persist_role_evidence() {
            printf 'evidence:%s\n' "$1" >> "$test_log"
            [[ "$1" == "rollback" ]]
        }
        write_rollout_incident() {
            printf 'incident:%s\n' "$ROLLOUT_FAILURE_PHASE" >> "$test_log"
            return 0
        }
        trap rollback_on_exit EXIT
        mutated_service=1
        rollout_mutation_attempted=1
        rollback_service
        ;;
    test-concurrent-normal-replacement)
        if [[ -z "${ROLLOUT_TEST_EVENT_LOG:-}" ]]; then
            exit 64
        fi
        test_log="$(realpath -m "$ROLLOUT_TEST_EVENT_LOG")"
        : > "$test_log"
        current_service_id="candidate-service"
        validate_overlay() {
            return 0
        }
        run_docker_recreate_bounded() {
            current_service_id="recovery-service"
            printf 'recreate\n' >> "$test_log"
            return 0
        }
        get_service_container_id() {
            printf '%s\n' "$current_service_id"
        }
        resolve_service_traffic_binding() {
            EXPECTED_SERVICE_CONTAINER_ID="$1"
            EXPECTED_SERVICE_CONTAINER_HOSTNAME="recovery-host"
            EXPECTED_SERVICE_INSTANCE_NONCE="aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"
            BASE_URL="http://127.0.0.1:18081"
            return 0
        }
        verify_container_image() {
            return 0
        }
        verify_worker_pin() {
            return 0
        }
        verify_container_env() {
            return 0
        }
        verify_container_env_by_id() {
            return 0
        }
        verify_database_target_binding() {
            return 0
        }
        wait_public_path() {
            printf 'health:%s\n' "$1" >> "$test_log"
            current_service_id="concurrent-replacement"
            return 0
        }
        rollback_service() {
            printf 'rollback:marker=%s\n' "$mutated_service" >> "$test_log"
            mutated_service=0
            return 0
        }
        write_rollout_incident() {
            printf 'incident:%s\n' "$ROLLOUT_FAILURE_PHASE" >> "$test_log"
            return 0
        }
        trap rollback_on_exit EXIT
        mutated_service=1
        rollout_mutation_attempted=1
        recover_normal_service
        ;;
    test-replacement-during-recovery-evidence)
        if [[ -z "${ROLLOUT_TEST_EVENT_LOG:-}" ]]; then
            exit 64
        fi
        test_log="$(realpath -m "$ROLLOUT_TEST_EVENT_LOG")"
        : > "$test_log"
        current_service_id="recovery-service"
        PINNED_RECOVERY_SERVICE_ID="recovery-service"
        EXPECTED_SERVICE_CONTAINER_ID="recovery-service"
        mutated_service=1
        rollout_mutation_attempted=1
        capture_db_quiescence() {
            printf 'quiescence:%s\n' "$3" >> "$test_log"
            current_service_id="replacement-service"
            return 0
        }
        persist_role_evidence() {
            printf 'role:%s:current=%s\n' "$2" "$current_service_id" >> "$test_log"
            [[ "$2" == "$current_service_id" ]]
        }
        get_service_container_id() {
            printf '%s\n' "$current_service_id"
        }
        verify_normal_service_state() {
            return 1
        }
        rollback_service() {
            printf 'rollback:marker=%s\n' "$mutated_service" >> "$test_log"
            mutated_service=0
            return 0
        }
        write_rollout_incident() {
            printf 'incident:%s\n' "$ROLLOUT_FAILURE_PHASE" >> "$test_log"
            return 0
        }
        trap rollback_on_exit EXIT
        complete_normal_recovery_evidence "$PINNED_RECOVERY_SERVICE_ID"
        ;;
    test-standalone-full-mount-rollback)
        if [[ -z "${ROLLOUT_TEST_EVENT_LOG:-}" ]]; then
            exit 64
        fi
        test_log="$(realpath -m "$ROLLOUT_TEST_EVENT_LOG")"
        case "$test_log/" in "$REPO_ROOT/"*) ;; *) exit 64 ;; esac
        : > "$test_log"
        require_evidence_configuration() {
            printf 'config:%s:%s\n' "$1" "$2" >> "$test_log"
            return 0
        }
        build_tool() {
            printf 'build\n' >> "$test_log"
            return 0
        }
        load_rollout_manifest_bindings() {
            printf 'load\n' >> "$test_log"
            return 0
        }
        validate_database_target_in_memory() {
            printf 'target-memory\n' >> "$test_log"
            return 0
        }
        acquire_rollout_lock() {
            printf 'lock-enospc:marker=%s\n' "$mutated_service" >> "$test_log"
            return 28
        }
        verify_normal_service_state() {
            printf 'normal-probe\n' >> "$test_log"
            return 1
        }
        rollback_service() {
            printf 'recover-normal:marker=%s\n' "$mutated_service" >> "$test_log"
            mutated_service=0
            return 0
        }
        write_rollout_incident() {
            printf 'incident-best-effort:%s\n' "$ROLLOUT_FAILURE_PHASE" >> "$test_log"
            return 1
        }
        run_standalone_rollback
        ;;
    test-final-recovery-drift)
        if [[ -z "${ROLLOUT_TEST_RECOVERY_EVIDENCE:-}" \
            || -z "${ROLLOUT_TEST_EVENT_LOG:-}" ]]; then
            exit 64
        fi
        LAST_RECOVERY_VERIFICATION_PATH="$ROLLOUT_TEST_RECOVERY_EVIDENCE"
        test_log="$(realpath -m "$ROLLOUT_TEST_EVENT_LOG")"
        get_compose_container_id() {
            if [[ "$1" == "fstservice" ]]; then
                printf 'replacement-service\n'
            else
                printf 'worker-container\n'
            fi
        }
        run_docker_query_bounded() {
            printf 'sha256:bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb|ghcr.io/test/service:test@sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\n'
        }
        set +e
        verify_final_recovery_state
        verify_status=$?
        set -e
        printf 'status=%s marker=%s phase=%s\n' \
            "$verify_status" \
            "$mutated_service" \
            "$ROLLOUT_FAILURE_PHASE" \
            > "$test_log"
        exit "$verify_status"
        ;;
    test-recreate-during-final-quiescence)
        if [[ -z "${ROLLOUT_TEST_EVENT_LOG:-}" ]]; then
            exit 64
        fi
        test_log="$(realpath -m "$ROLLOUT_TEST_EVENT_LOG")"
        EVIDENCE_DIR="$(dirname "$test_log")"
        PINNED_RECOVERY_SERVICE_ID="recovery-service"
        capture_db_quiescence() {
            printf 'quiescence-complete\n' >> "$test_log"
            printf 'same-image-recreate\n' >> "$test_log"
            return 0
        }
        verify_final_recovery_state() {
            printf 'recovery-identity-drift\n' >> "$test_log"
            ROLLOUT_FAILURE_PHASE="final-recovery-drift"
            mutated_service=1
            return 1
        }
        record_role_verification() {
            printf 'final-runtime-drift\n' >> "$test_log"
            return 1
        }
        set +e
        capture_final_acceptance_snapshot
        snapshot_status=$?
        set -e
        printf 'status=%s marker=%s phase=%s\n' \
            "$snapshot_status" \
            "$mutated_service" \
            "$ROLLOUT_FAILURE_PHASE" \
            >> "$test_log"
        exit "$snapshot_status"
        ;;
    test-final-capture-recovery-identity)
        if [[ -z "${ROLLOUT_TEST_EVENT_LOG:-}" ]]; then
            exit 64
        fi
        test_log="$(realpath -m "$ROLLOUT_TEST_EVENT_LOG")"
        EVIDENCE_DIR="$(dirname "$test_log")"
        PINNED_RECOVERY_SERVICE_ID="recovery-service"
        capture_db_quiescence() {
            printf 'quiescence-complete\n' >> "$test_log"
            return 0
        }
        verify_final_recovery_state() {
            printf 'recovery-container-id-mismatch\n' >> "$test_log"
            ROLLOUT_FAILURE_PHASE="final-recovery-drift"
            mutated_service=1
            return 1
        }
        record_role_verification() {
            printf 'unexpected-final-evidence\n' >> "$test_log"
            return 0
        }
        set +e
        capture_final_acceptance_snapshot
        snapshot_status=$?
        set -e
        printf 'status=%s marker=%s phase=%s\n' \
            "$snapshot_status" \
            "$mutated_service" \
            "$ROLLOUT_FAILURE_PHASE" \
            >> "$test_log"
        exit "$snapshot_status"
        ;;
    test-worker-pin-drift)
        if [[ -z "${ROLLOUT_TEST_EVENT_LOG:-}" ]]; then
            exit 64
        fi
        test_log="$(realpath -m "$ROLLOUT_TEST_EVENT_LOG")"
        PINNED_WORKER_CONTAINER_ID="original-worker"
        PINNED_WORKER_IMAGE_ID="sha256:cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc"
        PINNED_WORKER_IMAGE_REFERENCE="ghcr.io/sfenton/fstservice:worker"
        PINNED_WORKER_CONTAINER_STATUS="exited"
        get_compose_container_id() {
            printf 'replacement-worker\n'
        }
        set +e
        verify_worker_pin
        verify_status=$?
        set -e
        printf 'status=%s original=%s current=replacement-worker image=%s\n' \
            "$verify_status" \
            "$PINNED_WORKER_CONTAINER_ID" \
            "$PINNED_WORKER_IMAGE_ID" \
            > "$test_log"
        exit "$verify_status"
        ;;
    test-errexit-quiescence-rollback)
        if [[ -z "${ROLLOUT_TEST_EVIDENCE_DIR:-}" ]]; then
            exit 64
        fi
        EVIDENCE_DIR="$(realpath -m "$ROLLOUT_TEST_EVIDENCE_DIR")"
        case "$EVIDENCE_DIR/" in "$REPO_ROOT/"*) ;; *) exit 64 ;; esac
        EXPECTED_PUBLISHED_SCRAPE_ID=1278
        ROLLOUT_MANIFEST_FINGERPRINT="test-manifest"
        FST_STORED_RANK_SERVICE_IMAGE="ghcr.io/test/service:test@sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"
        PINNED_SERVICE_IMAGE_ID="sha256:bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"
        SERVICE_DB_HOST="postgres"
        SERVICE_DB_PORT="5432"
        SERVICE_DB_NAME="fstservice"
        SERVICE_DB_USERNAME="fst"
        PINNED_POSTGRES_CONTAINER_ID="postgres-container"
        PINNED_POSTGRES_IMAGE_REFERENCE="fst-postgres:17-repack"
        PINNED_POSTGRES_IMAGE_ID="sha256:dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd"
        PINNED_POSTGRES_NETWORK_NAMES="fst-network"
        PINNED_POSTGRES_NETWORK_ALIASES="fst-postgres,postgres"
        PINNED_POSTGRES_SERVER_ADDRESSES="172.20.0.2"
        PINNED_POSTGRES_NETWORK_BINDINGS_JSON='[{"exclusiveOwnerContainerId":"postgres-container","networkId":"network-id","networkName":"fst-network","serverAddresses":["172.20.0.2"],"serviceAlias":"postgres"}]'
        ROLLOUT_FAILURE_PHASE="injected-original-failure"
        verify_evidence_mount() {
            return 0
        }
        verify_worker_pin() {
            return 0
        }
        verify_manifest_image_pin() {
            return 0
        }
        verify_manifest_mount_binding() {
            return 0
        }
        verify_database_target_binding() {
            return 0
        }
        get_service_container_id() {
            printf 'service-container\n'
        }
        resolve_service_traffic_binding() {
            return 0
        }
        run_tool() {
            local output=""
            while (( $# > 0 )); do
                if [[ "$1" == "--output" ]]; then
                    output="$2"
                    shift 2
                    continue
                fi
                shift
            done
            mkdir -p "$(dirname "$output")"
            printf '{"passed":true}\n' > "$output"
            return 0
        }
        validate_overlay() {
            return 0
        }
        run_docker_recreate_bounded() {
            return 0
        }
        verify_container_image() {
            return 0
        }
        verify_container_env() {
            return 0
        }
        wait_public_path() {
            return 0
        }
        persist_role_evidence() {
            return 0
        }
        recover_normal_service() {
            return 77
        }
        verify_normal_service_state() {
            return 1
        }
        trap rollback_on_exit EXIT
        mutated_service=1
        rollout_mutation_attempted=1
        exit 42
        ;;
    *)
        printf 'ERROR: unknown action: %s\n\n' "$ACTION" >&2
        usage >&2
        exit 64
        ;;
esac
