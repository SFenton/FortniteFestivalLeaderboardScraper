#!/usr/bin/env bash
set -euo pipefail

COMPOSE_DIR="${COMPOSE_DIR:-/home/sfenton/Docker/FestivalServiceTracker}"
BASE_FILE="${BASE_FILE:-docker-compose.yml}"
PIA_OVERLAY="${PIA_OVERLAY:-docker-compose.pia-30.yml}"
RUNONCE_OVERLAY="${RUNONCE_OVERLAY:-docker-compose.runonce.yml}"
RUNTIME_PROBES=true
ACTION="check"
RECOVER_START_REQUESTED=false
RUNONCE_ACTION_REQUESTED=false
THROUGHPUT_PROFILE="baseline-up-to-800-32-4"
DATA_PROFILE="none"
EXPECTED_WORKER_IMAGE="${EXPECTED_WORKER_IMAGE:-}"
EXPECTED_WORKER_IMAGE_ID=""
EXPECTED_WORKER_REVISION=""
EXPECTED_WORKER_CONFIG_SHA256=""
PUBLICATION_COMMIT_DEFERRED_REASON="publication-commit-deferred"
WORKER_MUTATION_LOCK_PATH="${FST_WORKER_COMPOSE_GUARD_LOCK_PATH:-}"
INHERITED_WORKER_LOCK_FD=""
INHERITED_WORKER_LOCK_OWNER_PID=""
WORKER_LOCK_FD=""
WORKER_CREATE_ATTEMPTED=0
CREATED_WORKER_CONTAINER_ID=""
PREVIOUS_WORKER_CONTAINER_ID=""
DIRECT_WORKER_START_ACCEPTED=0
WORKER_CLEANUP_FAILED=0
RECOVERY_CORE_WAIT_SECONDS="${FST_WORKER_RECOVERY_CORE_WAIT_SECONDS:-60}"
RECOVERY_INITIAL_WAIT_SECONDS="${FST_WORKER_RECOVERY_INITIAL_WAIT_SECONDS:-360}"
RECOVERY_RECREATE_WAIT_SECONDS="${FST_WORKER_RECOVERY_RECREATE_WAIT_SECONDS:-360}"
RECOVERY_WORKER_WAIT_SECONDS="${FST_WORKER_RECOVERY_WORKER_WAIT_SECONDS:-180}"
RECOVERY_TOTAL_DEADLINE_SECONDS="${FST_WORKER_RECOVERY_TOTAL_DEADLINE_SECONDS:-1800}"
RECOVERY_POLL_INTERVAL_SECONDS="${FST_WORKER_RECOVERY_POLL_INTERVAL_SECONDS:-5}"
RECOVERY_MAX_PROXY_RECREATES="${FST_WORKER_RECOVERY_MAX_PROXY_RECREATES:-3}"
RECOVERY_HEARTBEAT_FRESH_SECONDS="${FST_WORKER_RECOVERY_HEARTBEAT_FRESH_SECONDS:-30}"
RECOVERY_WORKER_STOP_TIMEOUT_SECONDS="${FST_WORKER_RECOVERY_WORKER_STOP_TIMEOUT_SECONDS:-30}"

usage() {
    cat <<'EOF'
Usage: tools/fst-worker-compose-guard.sh [options]

Validates the canonical production PIA overlay before any fstworker recreate.
The resolved compose config must declare the expected effective proxy arrays,
all 30 canonical PIA services, aligned provider/control/container metadata,
the guard-only worker profile/restart policy, healthy unique egresses, and the
selected fail-closed throughput profile.

Options:
  --check                  Validate only (default)
  --check-runonce          Validate the exact merged run-once config without starting
  --recreate               Validate, then recreate and start fstworker
  --recreate-runonce       Validate, then recreate fstworker with run-once overlay
  --recover-start          Recover continuous startup using only the effective
                           PIA set, then recreate/start fstworker
  --config-only            Skip live DNS/control/egress probes
  --throughput-profile P   Select a named throughput profile:
                             baseline-up-to-800-32-4 (default)
                             candidate-800-32-4
                             candidate-1600-64-8
                             candidate-1800-72-9
                             candidate-2000-80-10
                             candidate-2880-128-16
                           Candidate profiles require --recreate-runonce for startup.
  --data-profile P         Select the paired data profile:
                             notification-db-only
                             publication-cache-generation
                             registered-refresh-repair
                             catalog-path-notification-source-cut
                             snapshot-reuse
                             leaderboard-rivals-batch
                             legacy-reader-migration
                             acquisition-checkpoint-terminalization
                             band-maintenance-progress
                             wire-send-telemetry
                             scrape-resume
                           Every run-once config requires a data profile.
  --expected-worker-image I
                           Require the resolved fstworker image to match I.
                           Enforced whenever supplied and required whenever
                           --data-profile is not none.
  --expected-worker-image-id I
                           Require I to be the exact local image ID resolved
                           by --expected-worker-image before and after startup.
  --expected-worker-revision R
                           Require the image and started worker OCI revision
                           label to match the exact 40-hex commit R.
  --expected-worker-config-sha256 H
                           Require the canonical resolved fstworker service
                           configuration, excluding only image, to hash to H.
  --inherited-worker-lock-fd N
                           For a mutating action, require descriptor N to
                           already own the canonical worker lock in this same
                           process. The guard retains it through startup.
  --compose-dir DIR        Production compose directory
  -h, --help               Show help
EOF
}

while [[ $# -gt 0 ]]; do
    case "$1" in
        --check) ACTION="check"; shift ;;
        --check-runonce) ACTION="check-runonce"; RUNONCE_ACTION_REQUESTED=true; shift ;;
        --recreate) ACTION="recreate"; shift ;;
        --recreate-runonce) ACTION="recreate-runonce"; RUNONCE_ACTION_REQUESTED=true; shift ;;
        --recover-start) ACTION="recover-start"; RECOVER_START_REQUESTED=true; shift ;;
        --config-only) RUNTIME_PROBES=false; shift ;;
        --throughput-profile) THROUGHPUT_PROFILE="$2"; shift 2 ;;
        --data-profile) DATA_PROFILE="$2"; shift 2 ;;
        --expected-worker-image) EXPECTED_WORKER_IMAGE="$2"; shift 2 ;;
        --expected-worker-image-id) EXPECTED_WORKER_IMAGE_ID="$2"; shift 2 ;;
        --expected-worker-revision) EXPECTED_WORKER_REVISION="$2"; shift 2 ;;
        --expected-worker-config-sha256) EXPECTED_WORKER_CONFIG_SHA256="$2"; shift 2 ;;
        --inherited-worker-lock-fd) INHERITED_WORKER_LOCK_FD="$2"; shift 2 ;;
        --inherited-worker-lock-owner-pid) INHERITED_WORKER_LOCK_OWNER_PID="$2"; shift 2 ;;
        --compose-dir) COMPOSE_DIR="$2"; shift 2 ;;
        -h|--help) usage; exit 0 ;;
        *) printf 'ERROR: unknown option: %s\n' "$1" >&2; usage >&2; exit 64 ;;
    esac
done

if $RECOVER_START_REQUESTED \
    && { $RUNONCE_ACTION_REQUESTED || [[ "$ACTION" != "recover-start" ]]; }
then
    printf 'ERROR: --recover-start cannot be combined with a run-once or other action\n' >&2
    exit 64
fi
if ! $RUNTIME_PROBES && [[ "$ACTION" != "check" && "$ACTION" != "check-runonce" ]]; then
    printf 'ERROR: --config-only cannot be used with a worker start action\n' >&2
    exit 64
fi

case "$THROUGHPUT_PROFILE" in
    baseline-up-to-800-32-4)
        PROFILE_MAX_AGGREGATE_RPS=800
        PROFILE_MAX_PER_ENDPOINT_RPS=32
        PROFILE_MAX_PER_ENDPOINT_CONCURRENCY=4
        PROFILE_EXACT=false
        ;;
    candidate-800-32-4)
        PROFILE_MAX_AGGREGATE_RPS=800
        PROFILE_MAX_PER_ENDPOINT_RPS=32
        PROFILE_MAX_PER_ENDPOINT_CONCURRENCY=4
        PROFILE_EXACT=true
        ;;
    candidate-1600-64-8)
        PROFILE_MAX_AGGREGATE_RPS=1600
        PROFILE_MAX_PER_ENDPOINT_RPS=64
        PROFILE_MAX_PER_ENDPOINT_CONCURRENCY=8
        PROFILE_EXACT=true
        ;;
    candidate-1800-72-9)
        PROFILE_MAX_AGGREGATE_RPS=1800
        PROFILE_MAX_PER_ENDPOINT_RPS=72
        PROFILE_MAX_PER_ENDPOINT_CONCURRENCY=9
        PROFILE_EXACT=true
        ;;
    candidate-2000-80-10)
        PROFILE_MAX_AGGREGATE_RPS=2000
        PROFILE_MAX_PER_ENDPOINT_RPS=80
        PROFILE_MAX_PER_ENDPOINT_CONCURRENCY=10
        PROFILE_EXACT=true
        ;;
    candidate-2880-128-16)
        PROFILE_MAX_AGGREGATE_RPS=2880
        PROFILE_MAX_PER_ENDPOINT_RPS=128
        PROFILE_MAX_PER_ENDPOINT_CONCURRENCY=16
        PROFILE_EXACT=true
        ;;
    *)
        printf 'ERROR: unknown throughput profile: %s\n' "$THROUGHPUT_PROFILE" >&2
        usage >&2
        exit 64
        ;;
esac

case "$DATA_PROFILE" in
    none|notification-db-only|publication-cache-generation|registered-refresh-repair|catalog-path-notification-source-cut|snapshot-reuse|leaderboard-rivals-batch|legacy-reader-migration|acquisition-checkpoint-terminalization|band-maintenance-progress|wire-send-telemetry|scrape-resume)
        ;;
    *)
        printf 'ERROR: unknown data profile: %s\n' "$DATA_PROFILE" >&2
        usage >&2
        exit 64
        ;;
esac

if [[ "$ACTION" =~ ^(check-runonce|recreate-runonce)$ && "$DATA_PROFILE" == "none" ]]; then
    printf 'ERROR: run-once validation requires --data-profile\n' >&2
    exit 64
fi

if [[ "$ACTION" == "recover-start" && "$DATA_PROFILE" != "none" ]]; then
    printf 'ERROR: --recover-start cannot be used with a data profile\n' >&2
    exit 64
fi

if [[ "$DATA_PROFILE" == "scrape-resume" \
    && ! "$ACTION" =~ ^(check-runonce|recreate-runonce)$ ]]
then
    printf 'ERROR: data profile scrape-resume requires --check-runonce or --recreate-runonce\n' >&2
    exit 64
fi

if [[ "$DATA_PROFILE" == "acquisition-checkpoint-terminalization" \
    && ! "$ACTION" =~ ^(check-runonce|recreate-runonce)$ ]]
then
    printf 'ERROR: data profile acquisition-checkpoint-terminalization requires --check-runonce or --recreate-runonce\n' >&2
    exit 64
fi

if [[ "$DATA_PROFILE" == "band-maintenance-progress" \
    && ! "$ACTION" =~ ^(check-runonce|recreate-runonce)$ ]]
then
    printf 'ERROR: data profile band-maintenance-progress requires --check-runonce or --recreate-runonce\n' >&2
    exit 64
fi
if [[ "$DATA_PROFILE" == "wire-send-telemetry" \
    && ! "$ACTION" =~ ^(check-runonce|recreate-runonce)$ ]]
then
    printf 'ERROR: data profile wire-send-telemetry requires --check-runonce or --recreate-runonce\n' >&2
    exit 64
fi

if [[ "$DATA_PROFILE" != "none" && -z "$EXPECTED_WORKER_IMAGE" ]]; then
    printf 'ERROR: --expected-worker-image is required with --data-profile\n' >&2
    exit 64
fi

if [[ "$DATA_PROFILE" == "band-maintenance-progress" \
    && "$THROUGHPUT_PROFILE" != "candidate-800-32-4" ]]
then
    printf 'ERROR: data profile band-maintenance-progress requires throughput profile candidate-800-32-4\n' >&2
    exit 64
fi
if [[ "$DATA_PROFILE" == "wire-send-telemetry" \
    && "$THROUGHPUT_PROFILE" != "candidate-800-32-4" ]]
then
    printf 'ERROR: data profile wire-send-telemetry requires throughput profile candidate-800-32-4\n' >&2
    exit 64
fi
if [[ ( "$DATA_PROFILE" == "band-maintenance-progress" \
        || "$DATA_PROFILE" == "wire-send-telemetry" ) \
    && ( -z "$EXPECTED_WORKER_IMAGE_ID" || -z "$EXPECTED_WORKER_REVISION" ) ]]
then
    printf 'ERROR: data profile %s requires exact expected worker image ID and revision\n' \
        "$DATA_PROFILE" >&2
    exit 64
fi

if [[ -n "$EXPECTED_WORKER_IMAGE_ID" \
    && ! "$EXPECTED_WORKER_IMAGE_ID" =~ ^sha256:[0-9a-f]{64}$ ]]
then
    printf 'ERROR: --expected-worker-image-id must be a lowercase sha256 image ID\n' >&2
    exit 64
fi
if [[ -n "$EXPECTED_WORKER_REVISION" \
    && ! "$EXPECTED_WORKER_REVISION" =~ ^[0-9a-f]{40}$ ]]
then
    printf 'ERROR: --expected-worker-revision must be a lowercase 40-hex commit\n' >&2
    exit 64
fi
if [[ -n "$EXPECTED_WORKER_CONFIG_SHA256" \
    && ! "$EXPECTED_WORKER_CONFIG_SHA256" =~ ^[0-9a-f]{64}$ ]]
then
    printf 'ERROR: --expected-worker-config-sha256 must be a lowercase SHA-256\n' >&2
    exit 64
fi
if [[ -n "$EXPECTED_WORKER_IMAGE_ID" && -z "$EXPECTED_WORKER_IMAGE" ]]; then
    printf 'ERROR: --expected-worker-image-id requires --expected-worker-image\n' >&2
    exit 64
fi
if [[ -n "$EXPECTED_WORKER_REVISION" && -z "$EXPECTED_WORKER_IMAGE_ID" ]]; then
    printf 'ERROR: --expected-worker-revision requires --expected-worker-image-id\n' >&2
    exit 64
fi

if [[ "$THROUGHPUT_PROFILE" == candidate-* \
    && "$ACTION" =~ ^(recreate|recover-start)$ ]]
then
    printf 'ERROR: candidate throughput profiles require --recreate-runonce\n' >&2
    exit 64
fi

MUTATING_WORKER_ACTION=false
if [[ "$ACTION" =~ ^(recreate|recreate-runonce|recover-start)$ ]]; then
    MUTATING_WORKER_ACTION=true
fi
if [[ ( "$DATA_PROFILE" == "band-maintenance-progress" \
        || "$DATA_PROFILE" == "wire-send-telemetry" ) \
    && "$MUTATING_WORKER_ACTION" == "true" \
    && -z "$EXPECTED_WORKER_CONFIG_SHA256" ]]
then
    printf 'ERROR: data profile %s requires --expected-worker-config-sha256 for recreate\n' \
        "$DATA_PROFILE" >&2
    exit 64
fi

if [[ -n "$INHERITED_WORKER_LOCK_FD" ]]; then
    require_inherited_lock_action="$MUTATING_WORKER_ACTION"
    if [[ "$require_inherited_lock_action" != "true" ]]; then
        printf 'ERROR: --inherited-worker-lock-fd requires a mutating worker action\n' >&2
        exit 64
    fi
    if [[ -z "$EXPECTED_WORKER_IMAGE_ID" \
        || -z "$EXPECTED_WORKER_REVISION" \
        || -z "$EXPECTED_WORKER_CONFIG_SHA256" ]]
    then
        printf 'ERROR: inherited worker handoff requires exact image ID, revision, and configuration assertions\n' >&2
        exit 64
    fi
    if [[ ! "$EXPECTED_WORKER_IMAGE" =~ @sha256:[0-9a-f]{64}$ ]]; then
        printf 'ERROR: inherited worker handoff requires an immutable digest image reference\n' >&2
        exit 64
    fi
    if [[ -z "$INHERITED_WORKER_LOCK_OWNER_PID" ]]; then
        INHERITED_WORKER_LOCK_OWNER_PID="$$"
    fi
fi

if [[ -n "${COMPOSE_FILE:-}" \
    || -n "${COMPOSE_PROJECT_NAME:-}" \
    || -n "${COMPOSE_ENV_FILES:-}" \
    || -n "${COMPOSE_PROFILES:-}" \
    || -n "${DOCKER_HOST:-}" \
    || -n "${DOCKER_CONTEXT:-}" ]]
then
    printf 'ERROR: Docker/Compose routing environment overrides are not permitted\n' >&2
    exit 64
fi
DOCKER_HOST="unix:///var/run/docker.sock"
DOCKER_CONFIG="/nonexistent/fst-worker-compose-guard"
COMPOSE_PROJECT_NAME="festivalservicetracker"
export DOCKER_HOST DOCKER_CONFIG COMPOSE_PROJECT_NAME
unset DOCKER_CONTEXT COMPOSE_FILE COMPOSE_ENV_FILES COMPOSE_PROFILES

for command in docker python3 realpath; do
    if ! command -v "$command" >/dev/null 2>&1; then
        printf 'ERROR: required command not found: %s\n' "$command" >&2
        exit 1
    fi
done
if $MUTATING_WORKER_ACTION && ! command -v flock >/dev/null 2>&1; then
    printf 'ERROR: required command not found: flock\n' >&2
    exit 1
fi

require_nonnegative_integer() {
    local name="$1"
    local value="$2"
    if [[ ! "$value" =~ ^[0-9]+$ ]]; then
        printf 'ERROR: %s must be a nonnegative integer\n' "$name" >&2
        exit 64
    fi
}

require_positive_integer() {
    local name="$1"
    local value="$2"
    require_nonnegative_integer "$name" "$value"
    if ((10#$value == 0)); then
        printf 'ERROR: %s must be greater than zero\n' "$name" >&2
        exit 64
    fi
}

verify_inherited_worker_mutation_lock() {
    if [[ -z "$INHERITED_WORKER_LOCK_FD" ]]; then
        return 0
    fi
    python3 - "$WORKER_MUTATION_LOCK_PATH" "$INHERITED_WORKER_LOCK_FD" \
        "$INHERITED_WORKER_LOCK_OWNER_PID" <<'PY'
import fcntl
import os
import pathlib
import stat
import sys

path = pathlib.Path(sys.argv[1])
descriptor = int(sys.argv[2])
owner_pid = int(sys.argv[3])
if descriptor < 3 or not path.is_absolute():
    raise SystemExit("ERROR: inherited worker lock descriptor or path is invalid")
parent = path.parent
parent_before = parent.stat()
parent_descriptor = os.open(
    parent,
    os.O_RDONLY | os.O_CLOEXEC | os.O_DIRECTORY | os.O_NOFOLLOW,
)
try:
    parent_opened = os.fstat(parent_descriptor)
    if (
        (parent_opened.st_dev, parent_opened.st_ino)
        != (parent_before.st_dev, parent_before.st_ino)
        or not stat.S_ISDIR(parent_opened.st_mode)
    ):
        raise SystemExit("ERROR: canonical worker lock parent identity changed")
    before = os.stat(
        path.name,
        dir_fd=parent_descriptor,
        follow_symlinks=False,
    )
    if (
        not stat.S_ISREG(before.st_mode)
        or before.st_uid != os.getuid()
        or before.st_nlink != 1
    ):
        raise SystemExit("ERROR: canonical worker lock path is not a safe owned regular file")
    opened = os.open(
        path.name,
        os.O_RDONLY | os.O_CLOEXEC | os.O_NOFOLLOW,
        dir_fd=parent_descriptor,
    )
finally:
    os.close(parent_descriptor)
try:
    canonical = os.fstat(opened)
    inherited = os.fstat(descriptor)
    if (
        (before.st_dev, before.st_ino) != (canonical.st_dev, canonical.st_ino)
        or (canonical.st_dev, canonical.st_ino)
        != (inherited.st_dev, inherited.st_ino)
        or not stat.S_ISREG(inherited.st_mode)
        or inherited.st_uid != os.getuid()
    ):
        raise SystemExit("ERROR: inherited worker lock inode identity changed")
    matches = []
    for line in pathlib.Path("/proc/locks").read_text().splitlines():
        fields = line.split()
        if len(fields) < 8 or fields[1:4] != ["FLOCK", "ADVISORY", "WRITE"]:
            continue
        device = fields[5].split(":")
        if len(device) != 3:
            continue
        if (
            int(fields[4]) == owner_pid
            and int(device[0], 16) == os.major(inherited.st_dev)
            and int(device[1], 16) == os.minor(inherited.st_dev)
            and int(device[2]) == inherited.st_ino
        ):
            matches.append(line)
    if len(matches) != 1:
        raise SystemExit("ERROR: inherited worker lock is not owned by this exact process")
    try:
        fcntl.flock(opened, fcntl.LOCK_EX | fcntl.LOCK_NB)
    except BlockingIOError:
        pass
    else:
        fcntl.flock(opened, fcntl.LOCK_UN)
        raise SystemExit("ERROR: canonical worker lock path is not excluded by the inherited lock")
finally:
    os.close(opened)
PY
}

acquire_worker_mutation_lock() {
    local lock_parent

    if [[ "$WORKER_MUTATION_LOCK_PATH" != /* ]]; then
        printf 'ERROR: worker mutation lock path must be absolute\n' >&2
        exit 64
    fi
    lock_parent="$(dirname "$WORKER_MUTATION_LOCK_PATH")"
    if [[ ! -d "$lock_parent" ]]; then
        printf 'ERROR: worker mutation lock path is not a safe regular file location\n' >&2
        exit 1
    fi

    if [[ ! -e "$WORKER_MUTATION_LOCK_PATH" && ! -L "$WORKER_MUTATION_LOCK_PATH" ]]; then
        if ! (
            umask 077
            set -o noclobber
            : > "$WORKER_MUTATION_LOCK_PATH"
        ) 2>/dev/null \
            && [[ ! -e "$WORKER_MUTATION_LOCK_PATH" \
                && ! -L "$WORKER_MUTATION_LOCK_PATH" ]]
        then
            printf 'ERROR: worker mutation lock file could not be created\n' >&2
            exit 1
        fi
    fi
    if [[ -L "$WORKER_MUTATION_LOCK_PATH" || ! -f "$WORKER_MUTATION_LOCK_PATH" \
        || ! -O "$WORKER_MUTATION_LOCK_PATH" ]]
    then
        printf 'ERROR: worker mutation lock path is not a safe regular file location\n' >&2
        exit 1
    fi
    if [[ ! -w "$WORKER_MUTATION_LOCK_PATH" ]]; then
        printf 'ERROR: worker mutation lock file is not writable by the current user\n' >&2
        exit 1
    fi

    if [[ -n "$INHERITED_WORKER_LOCK_FD" ]]; then
        require_positive_integer \
            inherited_worker_lock_fd \
            "$INHERITED_WORKER_LOCK_FD"
        require_positive_integer \
            inherited_worker_lock_owner_pid \
            "$INHERITED_WORKER_LOCK_OWNER_PID"
        WORKER_LOCK_FD="$INHERITED_WORKER_LOCK_FD"
        verify_inherited_worker_mutation_lock
        return
    fi

    if ! exec 9>>"$WORKER_MUTATION_LOCK_PATH"; then
        printf 'ERROR: worker mutation lock file could not be opened\n' >&2
        exit 1
    fi
    if ! flock -n 9; then
        printf 'ERROR: another fstworker start/recreate action is already running\n' >&2
        exit 1
    fi
    WORKER_LOCK_FD=9
}

if [[ "$ACTION" == "recover-start" ]]; then
    require_nonnegative_integer \
        FST_WORKER_RECOVERY_CORE_WAIT_SECONDS \
        "$RECOVERY_CORE_WAIT_SECONDS"
    require_nonnegative_integer \
        FST_WORKER_RECOVERY_INITIAL_WAIT_SECONDS \
        "$RECOVERY_INITIAL_WAIT_SECONDS"
    require_nonnegative_integer \
        FST_WORKER_RECOVERY_RECREATE_WAIT_SECONDS \
        "$RECOVERY_RECREATE_WAIT_SECONDS"
    require_nonnegative_integer \
        FST_WORKER_RECOVERY_WORKER_WAIT_SECONDS \
        "$RECOVERY_WORKER_WAIT_SECONDS"
    require_positive_integer \
        FST_WORKER_RECOVERY_TOTAL_DEADLINE_SECONDS \
        "$RECOVERY_TOTAL_DEADLINE_SECONDS"
    require_positive_integer \
        FST_WORKER_RECOVERY_POLL_INTERVAL_SECONDS \
        "$RECOVERY_POLL_INTERVAL_SECONDS"
    require_nonnegative_integer \
        FST_WORKER_RECOVERY_MAX_PROXY_RECREATES \
        "$RECOVERY_MAX_PROXY_RECREATES"
    require_positive_integer \
        FST_WORKER_RECOVERY_HEARTBEAT_FRESH_SECONDS \
        "$RECOVERY_HEARTBEAT_FRESH_SECONDS"
    require_nonnegative_integer \
        FST_WORKER_RECOVERY_WORKER_STOP_TIMEOUT_SECONDS \
        "$RECOVERY_WORKER_STOP_TIMEOUT_SECONDS"

    RECOVERY_CORE_WAIT_SECONDS=$((10#$RECOVERY_CORE_WAIT_SECONDS))
    RECOVERY_INITIAL_WAIT_SECONDS=$((10#$RECOVERY_INITIAL_WAIT_SECONDS))
    RECOVERY_RECREATE_WAIT_SECONDS=$((10#$RECOVERY_RECREATE_WAIT_SECONDS))
    RECOVERY_WORKER_WAIT_SECONDS=$((10#$RECOVERY_WORKER_WAIT_SECONDS))
    RECOVERY_TOTAL_DEADLINE_SECONDS=$((10#$RECOVERY_TOTAL_DEADLINE_SECONDS))
    RECOVERY_POLL_INTERVAL_SECONDS=$((10#$RECOVERY_POLL_INTERVAL_SECONDS))
    RECOVERY_MAX_PROXY_RECREATES=$((10#$RECOVERY_MAX_PROXY_RECREATES))
    RECOVERY_HEARTBEAT_FRESH_SECONDS=$((10#$RECOVERY_HEARTBEAT_FRESH_SECONDS))
    RECOVERY_WORKER_STOP_TIMEOUT_SECONDS=$((10#$RECOVERY_WORKER_STOP_TIMEOUT_SECONDS))
fi

RECOVERY_TOTAL_DEADLINE_AT=0
RECOVERY_TOTAL_DEADLINE_REPORTED=0

enforce_recovery_total_deadline() {
    if [[ "$ACTION" != "recover-start" || "$RECOVERY_TOTAL_DEADLINE_AT" -eq 0 \
        || "$SECONDS" -lt "$RECOVERY_TOTAL_DEADLINE_AT" ]]
    then
        return 0
    fi

    if ((RECOVERY_TOTAL_DEADLINE_REPORTED == 0)); then
        printf 'ERROR: fstworker startup recovery exceeded its total deadline\n' >&2
        RECOVERY_TOTAL_DEADLINE_REPORTED=1
    fi
    return 2
}

retry_probe() {
    local attempts="$1"
    local delay_seconds="$2"
    shift 2

    local attempt
    for ((attempt = 1; attempt <= attempts; attempt++)); do
        if ! enforce_recovery_total_deadline; then
            return 2
        fi
        if "$@"; then
            return 0
        fi
        if ! enforce_recovery_total_deadline; then
            return 2
        fi
        if ((attempt < attempts)); then
            sleep_until_deadline \
                "$((SECONDS + delay_seconds))" \
                "$delay_seconds"
        fi
    done
    return 1
}

compose_dir="$(realpath -m "$COMPOSE_DIR")"
base_file="$(realpath -m "$compose_dir/$BASE_FILE")"
pia_overlay="$(realpath -m "$compose_dir/$PIA_OVERLAY")"
runonce_overlay="$(realpath -m "$compose_dir/$RUNONCE_OVERLAY")"
if [[ -z "$WORKER_MUTATION_LOCK_PATH" ]]; then
    WORKER_MUTATION_LOCK_PATH="$compose_dir/.fst-worker-compose-guard.lock"
fi

if $MUTATING_WORKER_ACTION; then
    acquire_worker_mutation_lock
fi

if [[ "$base_file" != "$compose_dir/docker-compose.yml" ]]; then
    printf 'ERROR: canonical base file must resolve inside the Compose directory\n' >&2
    exit 1
fi
if [[ "$pia_overlay" != "$compose_dir/docker-compose.pia-30.yml" ]]; then
    printf 'ERROR: canonical PIA overlay must resolve inside the Compose directory\n' >&2
    exit 1
fi
for file in "$base_file" "$pia_overlay"; do
    if [[ ! -f "$file" ]]; then
        printf 'ERROR: required compose file not found: %s\n' "$file" >&2
        exit 1
    fi
done

compose_json_worker_binding() {
    python3 -c '
import hashlib
import json
import sys

config = json.load(sys.stdin)
worker = dict((config.get("services") or {}).get("fstworker") or {})
image = str(worker.get("image") or "").strip()
normalized = dict(worker)
normalized.pop("image", None)
config_sha256 = hashlib.sha256(
    json.dumps(
        normalized,
        sort_keys=True,
        separators=(",", ":"),
    ).encode()
).hexdigest()
print(image)
print(config_sha256)
' <<< "$1"
}

load_resolved_compose_json() {
    local require_run_once="$1"

    if [[ "$require_run_once" == "true" ]]; then
        if [[ ! -f "$runonce_overlay" ]]; then
            printf 'ERROR: run-once overlay not found: %s\n' \
                "$runonce_overlay" >&2
            return 1
        fi
        (
            cd "$compose_dir"
            docker compose --project-name "$COMPOSE_PROJECT_NAME" \
                --project-directory "$compose_dir" --profile worker \
                -f "$base_file" -f "$pia_overlay" -f "$runonce_overlay" \
                config --format json
        )
        return 0
    fi

    (
        cd "$compose_dir"
        docker compose --project-name "$COMPOSE_PROJECT_NAME" \
            --project-directory "$compose_dir" --profile worker \
            -f "$base_file" -f "$pia_overlay" config --format json
    )
}

build_active_resume_compose_json() {
    local continuous_compose_json="$1"
    local scrape_id="$2"
    local worker_image="$3"

    python3 -c '
import json
import sys

scrape_id = int(sys.argv[1])
worker_image = sys.argv[2]
config = json.load(sys.stdin)
worker = (config.get("services") or {}).get("fstworker")
if not isinstance(worker, dict):
    raise SystemExit(
        "ERROR: recovery could not resolve the continuous worker service")
environment = worker.get("environment")
if not isinstance(environment, dict):
    environment = {}
    worker["environment"] = environment

worker["image"] = worker_image
worker["restart"] = "no"
environment.update({
    "Scraper__RunOnce": "true",
    "Scraper__ApiOnly": "false",
    "Scraper__DisableScraperWorker": "false",
    "Scraper__RegistrationSyncWorkerOnly": "false",
    "Scraper__EnabledPhases": "SoloRankings",
    "Scraper__RegisteredUserRefreshTimeout": "00:00:00",
    "Scraper__ResumeScrapeId": str(scrape_id),
    "Scraper__ResumeSongsScraped": "0",
    "Scraper__ResumeTotalEntries": "0",
    "Scraper__ResumeTotalRequests": "0",
    "Scraper__ResumeTotalBytes": "0",
    "Scraper__ResumeEpicReportedOver100Pages": "false",
    "Scraper__RivalsMaxDegreeOfParallelism": "2",
    "Scraper__QueryLead": "true",
    "Scraper__QueryDrums": "true",
    "Scraper__QueryVocals": "true",
    "Scraper__QueryBass": "true",
    "Scraper__QueryProLead": "true",
    "Scraper__QueryProBass": "true",
    "Scraper__QueryProVocals": "true",
    "Scraper__QueryProCymbals": "true",
    "Scraper__QueryProDrums": "true",
    "Features__EnforcePublicationCriticalPhases": "true",
    "Features__EnforceScopeCompletenessManifests": "true",
    "Features__RequireSuccessfulScrapeWriters": "true",
    "Features__UseLeaderboardScopeFingerprints": "true",
    "Features__WritePublishedScopeSources": "true",
    "Features__SkipUnchangedPhysicalLeaderboardSnapshots": "true",
    "Features__UseStoredSoloProjectionRanksForFilteredReads": "false",
    "Features__WriteLogicalLeaderboardVersions": "false",
    "DatabaseMaintenance__SnapshotRetentionRewriteEnabled": "false",
})
print(json.dumps(config, sort_keys=True, separators=(",", ":")))
' "$scrape_id" "$worker_image" <<< "$continuous_compose_json"
}

validate_scrape_resume_worker_binding() {
    local compose_json_arg="$1"
    local expected_scrape_id="${2:-}"
    local expected_worker_image="${3:-}"
    local binding_context="${4:-data profile scrape-resume}"

    python3 -c '
import json
import sys

config = json.load(sys.stdin)
worker = dict((config.get("services") or {}).get("fstworker") or {})
environment = dict(worker.get("environment") or {})
expected_scrape_id = sys.argv[1].strip()
expected_worker_image = sys.argv[2].strip()
binding_context = sys.argv[3].strip()

def fail(message):
    raise SystemExit(f"ERROR: {binding_context} requires {message}")

def require_exact(name, expected):
    actual = str(environment.get(name) or "").strip()
    if actual != expected:
        display = actual if actual else "<empty>"
        fail(f"{name}={expected}, found {display}")

def require_positive_integer(name):
    actual = str(environment.get(name) or "").strip()
    try:
        value = int(actual)
    except ValueError as exc:
        raise SystemExit(
            f"ERROR: {binding_context} requires {name} to be an integer"
        ) from exc
    if value <= 0:
        raise SystemExit(
            f"ERROR: {binding_context} requires {name} to be greater than zero"
        )
    return actual

image = str(worker.get("image") or "").strip()
restart = str(worker.get("restart") or "").strip().lower()
if expected_worker_image and image != expected_worker_image:
    raise SystemExit(
        f"ERROR: {binding_context} image must match {expected_worker_image}"
    )
if restart != "no":
    display_restart = restart or "<empty>"
    raise SystemExit(
        f"ERROR: {binding_context} requires restart policy no, found {display_restart}"
    )

require_exact("Scraper__RunOnce", "true")
require_exact("Scraper__ApiOnly", "false")
require_exact("Scraper__DisableScraperWorker", "false")
require_exact("Scraper__RegistrationSyncWorkerOnly", "false")
require_exact("Scraper__EnabledPhases", "SoloRankings")
require_exact("Scraper__RegisteredUserRefreshTimeout", "00:00:00")
resume_scrape_id = require_positive_integer("Scraper__ResumeScrapeId")
if expected_scrape_id and resume_scrape_id != expected_scrape_id:
    fail(
        f"Scraper__ResumeScrapeId={expected_scrape_id}, found {resume_scrape_id}"
    )
require_exact("Scraper__RivalsMaxDegreeOfParallelism", "2")
for name in (
    "Scraper__QueryLead",
    "Scraper__QueryDrums",
    "Scraper__QueryVocals",
    "Scraper__QueryBass",
    "Scraper__QueryProLead",
    "Scraper__QueryProBass",
    "Scraper__QueryProVocals",
    "Scraper__QueryProCymbals",
    "Scraper__QueryProDrums",
):
    require_exact(name, "true")
for name in (
    "Features__EnforcePublicationCriticalPhases",
    "Features__EnforceScopeCompletenessManifests",
    "Features__RequireSuccessfulScrapeWriters",
    "Features__UseLeaderboardScopeFingerprints",
    "Features__WritePublishedScopeSources",
    "Features__SkipUnchangedPhysicalLeaderboardSnapshots",
):
    require_exact(name, "true")
for name in (
    "Features__UseStoredSoloProjectionRanksForFilteredReads",
    "Features__WriteLogicalLeaderboardVersions",
    "DatabaseMaintenance__SnapshotRetentionRewriteEnabled",
):
    require_exact(name, "false")
' "$expected_scrape_id" "$expected_worker_image" "$binding_context" \
        <<< "$compose_json_arg"
}

if [[ "$ACTION" =~ ^(check-runonce|recreate-runonce)$ && ! -f "$runonce_overlay" ]]; then
    printf 'ERROR: run-once overlay not found: %s\n' "$runonce_overlay" >&2
    exit 1
fi

if [[ "$ACTION" =~ ^(check-runonce|recreate-runonce)$ ]]; then
    runonce_restart="$(
        load_resolved_compose_json true \
            | python3 -c 'import json,sys; print((json.load(sys.stdin).get("services", {}).get("fstworker", {}).get("restart") or "").strip())'
    )"
    if [[ "$runonce_restart" != "no" ]]; then
        printf 'ERROR: run-once worker restart policy must resolve to no, found: %s\n' \
            "${runonce_restart:-<empty>}" >&2
        exit 1
    fi
fi

if [[ "$ACTION" =~ ^(check-runonce|recreate-runonce)$ ]]; then
    compose_json="$(load_resolved_compose_json true)"
    REQUIRE_RUN_ONCE=true
else
    compose_json="$(load_resolved_compose_json false)"
    REQUIRE_RUN_ONCE=false
fi

validation="$(
    python3 -c '
import hashlib
import json
import re
import sys
from urllib.parse import urlparse

profile_name = sys.argv[1]
profile_max_aggregate_rps = int(sys.argv[2])
profile_max_per_endpoint_rps = int(sys.argv[3])
profile_max_per_endpoint_concurrency = int(sys.argv[4])
profile_exact = sys.argv[5].casefold() == "true"
require_run_once = sys.argv[6].casefold() == "true"
data_profile = sys.argv[7]
expected_worker_image = sys.argv[8]
expected_worker_config_sha256 = sys.argv[9]
action = sys.argv[10]
recovery_mode = action == "recover-start"
continuous_mode = not require_run_once

config = json.load(sys.stdin)
services = config.get("services") or {}
worker = services.get("fstworker") or {}
environment = worker.get("environment") or {}
worker_restart = str(worker.get("restart") or "").strip().casefold()
worker_profiles = worker.get("profiles") or []

def validation_error(sanitized, detailed=None):
    message = sanitized if recovery_mode or detailed is None else detailed
    raise SystemExit(f"ERROR: {message}")

def integer(name):
    try:
        value = int(str(environment.get(name, "")))
    except ValueError:
        raise SystemExit(f"ERROR: {name} must be an integer")
    if value <= 0:
        raise SystemExit(f"ERROR: {name} must be greater than zero")
    return value

def boolean(name):
    value = str(environment.get(name, "")).strip().casefold()
    if value not in {"true", "false"}:
        raise SystemExit(f"ERROR: {name} must be true or false")
    return value == "true"

def nonnegative_integer(name):
    try:
        value = int(str(environment.get(name, "")))
    except ValueError:
        raise SystemExit(f"ERROR: {name} must be an integer")
    if value < 0:
        raise SystemExit(f"ERROR: {name} must be zero or greater")
    return value

def exact_value(name, expected_value):
    actual = str(environment.get(name, "")).strip()
    if actual != expected_value:
        display_actual = actual if actual else "<empty>"
        raise SystemExit(
            f"ERROR: data profile {data_profile} requires "
            f"{name}={expected_value}, found {display_actual}")

def optional_exact_value(name, expected_value):
    if name not in environment:
        actual_worker_image = str(worker.get("image") or "").strip()
        default_proven = (
            expected_worker_image
            and actual_worker_image == expected_worker_image
        )
        if default_proven:
            return
        raise SystemExit(
            f"ERROR: data profile {data_profile} requires "
            f"{name}={expected_value}, found <empty>")
    exact_value(name, expected_value)

def indexed(prefix):
    values = []
    for key, value in environment.items():
        if not key.startswith(prefix):
            continue
        suffix = key[len(prefix):]
        if not suffix.isdigit():
            raise SystemExit(
                f"ERROR: {prefix} entries must use numeric indexes")
        values.append((int(suffix), "" if value is None else str(value)))
    values.sort()
    indexes = [index for index, _ in values]
    if indexes != list(range(len(values))):
        raise SystemExit(f"ERROR: {prefix} indexes must be contiguous from zero")
    return [value for _, value in values]

expected = integer("Scraper__ExpectedProxyEndpointCount")
canonical = integer("Scraper__CanonicalProxyServiceCount")
max_rps = integer("Scraper__MaxRequestsPerSecond")
per_endpoint_rps = integer("Scraper__ProxyMaxRequestsPerSecondPerEndpoint")
per_endpoint_concurrency = integer("Scraper__ProxyMaxConcurrentRequestsPerEndpoint")
disable_connection_reuse = boolean("Scraper__ProxyDisableConnectionReuse")
use_curl_transport = boolean("Scraper__ProxyUseCurlTransport")
run_once = (
    boolean("Scraper__RunOnce")
    if "Scraper__RunOnce" in environment
    else False
)
initial_dop = integer("Scraper__InitialDop")
degree_of_parallelism = integer("Scraper__DegreeOfParallelism")
page_concurrency = integer("Scraper__PageConcurrency")
curl_temp_directory = str(environment.get("Scraper__ProxyCurlTempDirectory", "")).strip()
if require_run_once and not run_once:
    raise SystemExit("ERROR: merged run-once config must set Scraper__RunOnce=true")
if continuous_mode and run_once:
    raise SystemExit(
        "ERROR: continuous guard actions require Scraper__RunOnce=false")
if require_run_once and worker_restart != "no":
    raise SystemExit(
        "ERROR: run-once worker restart policy must resolve to no")
if continuous_mode and worker_restart != "on-failure:5":
    raise SystemExit(
        "ERROR: continuous worker restart policy must resolve to on-failure:5")
if not isinstance(worker_profiles, list) or "worker" not in {
    str(profile).strip() for profile in worker_profiles
}:
    raise SystemExit(
        "ERROR: fstworker must include the worker Compose profile")
if profile_name in {
    "candidate-1600-64-8",
    "candidate-1800-72-9",
    "candidate-2000-80-10",
}:
    if initial_dop != 50:
        raise SystemExit(
            f"ERROR: {profile_name} requires "
            f"Scraper__InitialDop=50, found {initial_dop}")
    if degree_of_parallelism != 200 or page_concurrency != 50:
        raise SystemExit(
            f"ERROR: {profile_name} requires "
            "Scraper__DegreeOfParallelism=200 and Scraper__PageConcurrency=50")
if data_profile == "notification-db-only":
    exact_value("Scraper__EnabledPhases", "None")
    if not boolean("ImprovementNotifications__Enabled"):
        raise SystemExit(
            "ERROR: data profile notification-db-only requires "
            "ImprovementNotifications__Enabled=true")
    exact_value("ImprovementNotifications__Scope", "registered")
    for name in (
        "ImprovementNotifications__IncludePlayers",
        "ImprovementNotifications__IncludeBands",
        "ImprovementNotifications__IncludeSongEvents",
        "ImprovementNotifications__IncludeRankings",
        "ImprovementNotifications__RefreshSoloProjection",
    ):
        if not boolean(name):
            raise SystemExit(
                f"ERROR: data profile notification-db-only requires {name}=true")
    exact_value(
        "ImprovementNotifications__RefreshAllSoloScopesWhenNoImpactedScopes",
        "false")
    exact_value("Scraper__RegisteredUserRefreshTimeout", "00:00:00")
    exact_value("Scraper__RegisteredPlayerBandDiscoveryTimeout", "00:06:00")
    exact_value("Scraper__RegisteredBandTargetedProcessingTimeout", "00:05:00")
    exact_value(
        "Scraper__EnableRegisteredPlayerBandDiscoveryRemainingWorkGrace",
        "false")
    exact_value(
        "Scraper__EnableRegisteredBandTargetedProcessingRemainingWorkGrace",
        "false")
    exact_value(
        "Scraper__RegisteredBandRemainingWorkGraceMaxDuration",
        "00:02:00")
    exact_value(
        "Scraper__RegisteredBandRemainingWorkGraceRecentProgressWindow",
        "00:01:30")
    exact_value(
        "Scraper__RegisteredBandRemainingWorkGraceMaxRemainingLookups",
        "3")
    for name in (
        "Scraper__RegisteredPlayerBandDiscoveryMaxLookupsPerPass",
        "Scraper__RegisteredBandProcessingMaxLookupsPerPass",
    ):
        if nonnegative_integer(name) != 80:
            raise SystemExit(
                f"ERROR: data profile notification-db-only requires {name}=80")
if expected_worker_image:
    actual_worker_image = str(worker.get("image") or "").strip()
    if actual_worker_image != expected_worker_image:
        display_actual = actual_worker_image if actual_worker_image else "<empty>"
        raise SystemExit(
            "ERROR: resolved fstworker image must match "
            f"{expected_worker_image}, found {display_actual}")
if expected_worker_config_sha256:
    normalized_worker = dict(worker)
    normalized_worker.pop("image", None)
    actual_worker_config_sha256 = hashlib.sha256(
        json.dumps(
            normalized_worker,
            sort_keys=True,
            separators=(",", ":"),
        ).encode()
    ).hexdigest()
    if actual_worker_config_sha256 != expected_worker_config_sha256:
        raise SystemExit(
            "ERROR: resolved fstworker non-image configuration hash does not match")

if data_profile == "publication-cache-generation":
    exact_value("Scraper__EnabledPhases", "All")
    for name in (
        "Features__EnforcePublicationCriticalPhases",
        "Features__EnforceScopeCompletenessManifests",
        "Features__RequireSuccessfulScrapeWriters",
        "Features__UseLeaderboardScopeFingerprints",
        "Features__WritePublishedScopeSources",
    ):
        if not boolean(name):
            raise SystemExit(
                f"ERROR: data profile publication-cache-generation requires {name}=true")
    for name in (
        "Features__UseStoredSoloProjectionRanksForFilteredReads",
        "Features__SkipUnchangedPhysicalLeaderboardSnapshots",
    ):
        if boolean(name):
            raise SystemExit(
                f"ERROR: data profile publication-cache-generation requires {name}=false")
if data_profile == "registered-refresh-repair":
    exact_value("Scraper__EnabledPhases", "All")
    exact_value("Scraper__RegisteredUserRefreshTimeout", "00:00:00")
    for name in (
        "Features__EnforcePublicationCriticalPhases",
        "Features__EnforceScopeCompletenessManifests",
        "Features__RequireSuccessfulScrapeWriters",
        "Features__UseLeaderboardScopeFingerprints",
        "Features__WritePublishedScopeSources",
    ):
        if not boolean(name):
            raise SystemExit(
                f"ERROR: data profile registered-refresh-repair requires {name}=true")
    for name in (
        "Features__UseStoredSoloProjectionRanksForFilteredReads",
        "Features__SkipUnchangedPhysicalLeaderboardSnapshots",
    ):
        if boolean(name):
            raise SystemExit(
                f"ERROR: data profile registered-refresh-repair requires {name}=false")
if data_profile in {
    "catalog-path-notification-source-cut",
    "acquisition-checkpoint-terminalization",
    "band-maintenance-progress",
    "wire-send-telemetry",
}:
    exact_value("Scraper__EnabledPhases", "All")
    exact_value("Scraper__RegisteredUserRefreshTimeout", "00:00:00")
    exact_value("Scraper__EnableAutomaticPathGeneration", "false")
    for name in (
        "Features__EnforcePublicationCriticalPhases",
        "Features__EnforceScopeCompletenessManifests",
        "Features__RequireSuccessfulScrapeWriters",
        "Features__UseLeaderboardScopeFingerprints",
        "Features__WritePublishedScopeSources",
        "ImprovementNotifications__Enabled",
        "ImprovementNotifications__IncludePlayers",
        "ImprovementNotifications__IncludeBands",
        "ImprovementNotifications__IncludeSongEvents",
        "ImprovementNotifications__IncludeRankings",
    ):
        if not boolean(name):
            raise SystemExit(
                f"ERROR: data profile {data_profile} "
                f"requires {name}=true")
    for name in (
        "Features__UseStoredSoloProjectionRanksForFilteredReads",
        "Features__SkipUnchangedPhysicalLeaderboardSnapshots",
    ):
        if boolean(name):
            raise SystemExit(
                f"ERROR: data profile {data_profile} "
                f"requires {name}=false")
if data_profile in {
    "acquisition-checkpoint-terminalization",
    "band-maintenance-progress",
    "wire-send-telemetry",
}:
    exact_value("Features__UseSnapshotOverlayWorkerReaders", "false")
    exact_value("ImprovementNotifications__Scope", "registered")
    exact_value("ImprovementNotifications__RefreshSoloProjection", "true")
    exact_value(
        "ImprovementNotifications__RefreshAllSoloScopesWhenNoImpactedScopes",
        "false")
    exact_value("Scraper__RegisteredPlayerBandDiscoveryTimeout", "00:06:00")
    exact_value("Scraper__RegisteredBandTargetedProcessingTimeout", "00:05:00")
    optional_exact_value(
        "Scraper__EnableRegisteredPlayerBandDiscoveryRemainingWorkGrace",
        "false")
    optional_exact_value(
        "Scraper__EnableRegisteredBandTargetedProcessingRemainingWorkGrace",
        "false")
    optional_exact_value(
        "Scraper__RegisteredBandRemainingWorkGraceMaxDuration",
        "00:02:00")
    optional_exact_value(
        "Scraper__RegisteredBandRemainingWorkGraceRecentProgressWindow",
        "00:01:30")
    optional_exact_value(
        "Scraper__RegisteredBandRemainingWorkGraceMaxRemainingLookups",
        "3")
    for name in (
        "Scraper__RegisteredPlayerBandDiscoveryMaxLookupsPerPass",
        "Scraper__RegisteredBandProcessingMaxLookupsPerPass",
    ):
        if nonnegative_integer(name) != 80:
            raise SystemExit(
                f"ERROR: data profile {data_profile} "
                f"requires {name}=80")
    for name in (
        "Features__WriteLogicalLeaderboardVersions",
        "DatabaseMaintenance__SnapshotRetentionRewriteEnabled",
    ):
        if boolean(name):
            raise SystemExit(
                f"ERROR: data profile {data_profile} "
                f"requires {name}=false")
if data_profile in {"band-maintenance-progress", "wire-send-telemetry"}:
    exact_value(
        "Scraper__BandCurrentProjectionUseBatchedMemberStatsAggregation",
        "false")
if data_profile == "snapshot-reuse":
    exact_value("Scraper__EnabledPhases", "All")
    exact_value("Scraper__RegisteredUserRefreshTimeout", "00:00:00")
    for name in (
        "Features__EnforcePublicationCriticalPhases",
        "Features__EnforceScopeCompletenessManifests",
        "Features__RequireSuccessfulScrapeWriters",
        "Features__UseLeaderboardScopeFingerprints",
        "Features__WritePublishedScopeSources",
        "Features__SkipUnchangedPhysicalLeaderboardSnapshots",
    ):
        if not boolean(name):
            raise SystemExit(
                f"ERROR: data profile snapshot-reuse requires {name}=true")
    for name in (
        "Features__UseStoredSoloProjectionRanksForFilteredReads",
        "Features__WriteLogicalLeaderboardVersions",
        "DatabaseMaintenance__SnapshotRetentionRewriteEnabled",
    ):
        if boolean(name):
            raise SystemExit(
                f"ERROR: data profile snapshot-reuse requires {name}=false")
if data_profile == "leaderboard-rivals-batch":
    exact_value("Scraper__EnabledPhases", "All")
    exact_value("Scraper__RegisteredUserRefreshTimeout", "00:00:00")
    if integer("Scraper__InitialCdnLearnedMaxDop") != 360:
        raise SystemExit(
            "ERROR: data profile leaderboard-rivals-batch requires "
            "Scraper__InitialCdnLearnedMaxDop=360")
    if integer("Scraper__RivalsMaxDegreeOfParallelism") != 2:
        raise SystemExit(
            "ERROR: data profile leaderboard-rivals-batch requires "
            "Scraper__RivalsMaxDegreeOfParallelism=2")
    if integer("Scraper__LeaderboardRivalsMaxDegreeOfParallelism") != 4:
        raise SystemExit(
            "ERROR: data profile leaderboard-rivals-batch requires "
            "Scraper__LeaderboardRivalsMaxDegreeOfParallelism=4")
    for name in (
        "Scraper__UsePublicationPathArtifacts",
        "Scraper__EnableScrapePassPathGeneration",
        "Features__EnforcePublicationCriticalPhases",
        "Features__EnforceScopeCompletenessManifests",
        "Features__RequireSuccessfulScrapeWriters",
        "Features__UseLeaderboardScopeFingerprints",
        "Features__WritePublishedScopeSources",
        "Features__SkipUnchangedPhysicalLeaderboardSnapshots",
        "ImprovementNotifications__Enabled",
        "ImprovementNotifications__IncludePlayers",
        "ImprovementNotifications__IncludeBands",
        "ImprovementNotifications__IncludeSongEvents",
        "ImprovementNotifications__IncludeRankings",
    ):
        if not boolean(name):
            raise SystemExit(
                "ERROR: data profile leaderboard-rivals-batch "
                f"requires {name}=true")
    for name in (
        "Scraper__EnableAutomaticPathGeneration",
        "Features__UseStoredSoloProjectionRanksForFilteredReads",
        "Features__WriteLogicalLeaderboardVersions",
        "DatabaseMaintenance__SnapshotRetentionRewriteEnabled",
    ):
        if boolean(name):
            raise SystemExit(
                "ERROR: data profile leaderboard-rivals-batch "
                f"requires {name}=false")
if data_profile == "scrape-resume":
    exact_value("Scraper__EnabledPhases", "SoloRankings")
    exact_value("Scraper__RegistrationSyncWorkerOnly", "false")
    exact_value("Scraper__RegisteredUserRefreshTimeout", "00:00:00")
    integer("Scraper__ResumeScrapeId")
    if integer("Scraper__RivalsMaxDegreeOfParallelism") != 2:
        raise SystemExit(
            "ERROR: data profile scrape-resume requires "
            "Scraper__RivalsMaxDegreeOfParallelism=2")
    for name in (
        "Features__EnforcePublicationCriticalPhases",
        "Features__EnforceScopeCompletenessManifests",
        "Features__RequireSuccessfulScrapeWriters",
        "Features__UseLeaderboardScopeFingerprints",
        "Features__WritePublishedScopeSources",
        "Features__SkipUnchangedPhysicalLeaderboardSnapshots",
    ):
        if not boolean(name):
            raise SystemExit(
                f"ERROR: data profile scrape-resume requires {name}=true")
    for name in (
        "Features__UseStoredSoloProjectionRanksForFilteredReads",
        "Features__WriteLogicalLeaderboardVersions",
        "DatabaseMaintenance__SnapshotRetentionRewriteEnabled",
    ):
        if boolean(name):
            raise SystemExit(
                f"ERROR: data profile scrape-resume requires {name}=false")
if data_profile == "legacy-reader-migration":
    exact_value("Scraper__EnabledPhases", "All")
    exact_value("Scraper__RegisteredUserRefreshTimeout", "00:00:00")
    for name in (
        "Features__EnforcePublicationCriticalPhases",
        "Features__EnforceScopeCompletenessManifests",
        "Features__RequireSuccessfulScrapeWriters",
        "Features__UseLeaderboardScopeFingerprints",
        "Features__WritePublishedScopeSources",
        "Features__UseSnapshotOverlayWorkerReaders",
        "Features__WriteLegacyLiveLeaderboardSupplementalRows",
        "ImprovementNotifications__Enabled",
        "ImprovementNotifications__IncludePlayers",
        "ImprovementNotifications__IncludeBands",
        "ImprovementNotifications__IncludeSongEvents",
        "ImprovementNotifications__IncludeRankings",
    ):
        if not boolean(name):
            raise SystemExit(
                f"ERROR: data profile legacy-reader-migration requires {name}=true")
    for name in (
        "Features__WriteLegacyLiveLeaderboardDuringScrape",
        "Features__UseStoredSoloProjectionRanksForFilteredReads",
        "Features__SkipUnchangedPhysicalLeaderboardSnapshots",
        "Features__WriteLogicalLeaderboardVersions",
        "DatabaseMaintenance__SnapshotRetentionRewriteEnabled",
    ):
        if boolean(name):
            raise SystemExit(
                f"ERROR: data profile legacy-reader-migration requires {name}=false")
if canonical != 30:
    validation_error(
        "canonical PIA service count must be 30",
        f"canonical PIA service count must be 30, found {canonical}")
if expected > canonical:
    validation_error(
        "effective proxy count exceeds the canonical count",
        f"effective proxy count {expected} exceeds canonical count {canonical}")
proxies = indexed("Scraper__ProxyUrls__")
containers = indexed("Scraper__ContainerNames__")
controls = indexed("Scraper__ControlUrls__")
providers = indexed("Scraper__VpnProviders__")

for label, values in (
    ("proxy URLs", proxies),
    ("container names", containers),
    ("control URLs", controls),
    ("provider labels", providers),
):
    if len(values) != expected or any(not value.strip() for value in values):
        validation_error(
            f"effective {label} are not exact and aligned",
            f"expected {expected} aligned {label}, found {len(values)}")

expected_canonical_names = {f"pia-gluetun-{index}" for index in range(1, canonical + 1)}
actual_canonical_names = {name for name in services if re.fullmatch(r"pia-gluetun-\d+", name)}
if actual_canonical_names != expected_canonical_names:
    validation_error(
        "canonical PIA service definitions are incomplete",
        f"expected {canonical} canonical PIA services, found "
        f"{len(actual_canonical_names)}")

if len(set(containers)) != expected:
    raise SystemExit("ERROR: effective proxy container names must be unique")
if not set(containers).issubset(expected_canonical_names):
    raise SystemExit(
        "ERROR: effective proxy container names must be canonical PIA services")

for index, (proxy, control, container, provider) in enumerate(
    zip(proxies, controls, containers, providers, strict=True)
):
    proxy_service = services.get(container)
    if not isinstance(proxy_service, dict):
        raise SystemExit(
            f"ERROR: effective PIA service {container} is not defined")
    resolved_container_name = str(
        proxy_service.get("container_name") or container).strip()
    if resolved_container_name != container:
        raise SystemExit(
            f"ERROR: effective PIA service {container} has a mismatched container name")
    proxy_environment = proxy_service.get("environment") or {}
    if not isinstance(proxy_environment, dict):
        raise SystemExit(
            f"ERROR: effective PIA service {container} has invalid environment metadata")
    endpoint_ip = proxy_environment.get("OPENVPN_ENDPOINT_IP")
    if endpoint_ip is not None and str(endpoint_ip).strip():
        raise SystemExit(
            f"ERROR: effective PIA service {container} must not set "
            "OPENVPN_ENDPOINT_IP")

    proxy_uri = urlparse(proxy)
    control_uri = urlparse(control)
    if proxy_uri.scheme != "http" or proxy_uri.hostname != container or proxy_uri.port != 8888:
        raise SystemExit(f"ERROR: proxy index {index} is not aligned with {container}:8888")
    if control_uri.scheme != "http" or control_uri.hostname != container or control_uri.port != 8000:
        raise SystemExit(f"ERROR: control index {index} is not aligned with {container}:8000")
    if provider.casefold() != "private internet access":
        raise SystemExit(f"ERROR: provider index {index} is not Private Internet Access")

depends_on = set((worker.get("depends_on") or {}).keys())
if not set(containers).issubset(depends_on):
    raise SystemExit("ERROR: fstworker is missing an effective PIA dependency")
unexpected_pia_dependencies = (depends_on & expected_canonical_names) - set(containers)
if unexpected_pia_dependencies:
    raise SystemExit(
        "ERROR: fstworker still depends on quarantined PIA services: "
        + ",".join(sorted(unexpected_pia_dependencies)))

if max_rps > profile_max_aggregate_rps:
    validation_error(
        "aggregate Epic rate exceeds the selected profile",
        f"aggregate Epic rate {max_rps} exceeds profile "
        f"{profile_name} ceiling {profile_max_aggregate_rps}")
if per_endpoint_rps > profile_max_per_endpoint_rps:
    validation_error(
        "per-endpoint Epic rate exceeds the selected profile",
        f"per-endpoint Epic rate {per_endpoint_rps} exceeds profile "
        f"{profile_name} ceiling {profile_max_per_endpoint_rps}")
if per_endpoint_concurrency > profile_max_per_endpoint_concurrency:
    validation_error(
        "per-endpoint concurrency exceeds the selected profile",
        f"per-endpoint concurrency {per_endpoint_concurrency} exceeds profile "
        f"{profile_name} ceiling {profile_max_per_endpoint_concurrency}")
if profile_exact and (
    max_rps != profile_max_aggregate_rps
    or per_endpoint_rps != profile_max_per_endpoint_rps
    or per_endpoint_concurrency != profile_max_per_endpoint_concurrency
):
    raise SystemExit(
        f"ERROR: candidate profile {profile_name} requires exact "
        f"{profile_max_aggregate_rps}/{profile_max_per_endpoint_rps}/"
        f"{profile_max_per_endpoint_concurrency}, found "
        f"{max_rps}/{per_endpoint_rps}/{per_endpoint_concurrency}")
if not disable_connection_reuse:
    raise SystemExit("ERROR: canonical PIA worker must disable proxy connection reuse")
if not use_curl_transport:
    raise SystemExit("ERROR: canonical PIA worker must use the qualified curl proxy transport")
if curl_temp_directory != "/app/data/curl-transport":
    raise SystemExit(
        "ERROR: curl proxy scratch must be /app/data/curl-transport on the FST data mount")

print(
    f"SUMMARY|{profile_name}|{data_profile}|{expected}|{canonical}|{max_rps}|"
    f"{per_endpoint_rps}|{per_endpoint_concurrency}|true|true|"
    f"{str(run_once).lower()}")
for container in containers:
    print(f"NODE|{container}")
if recovery_mode:
    required_core = {}
    for service_name in ("postgres", "fstservice", "fstworker"):
        service = services.get(service_name)
        if not isinstance(service, dict):
            raise SystemExit(
                f"ERROR: recovery requires compose service {service_name}")
        container_name = str(
            service.get("container_name") or service_name).strip()
        if not container_name:
            raise SystemExit(
                f"ERROR: recovery could not resolve {service_name} container")
        required_core[service_name] = container_name
    if required_core["postgres"] not in {"postgres", "fst-postgres"}:
        raise SystemExit(
            "ERROR: recovery requires the postgres/fst-postgres container")
    if required_core["fstservice"] != "fstservice":
        raise SystemExit(
            "ERROR: recovery requires the fstservice container")
    if required_core["fstworker"] != "fstworker":
        raise SystemExit(
            "ERROR: recovery requires the fstworker container")
    print("CORE|postgres|" + required_core["postgres"])
    print("CORE|fstservice|" + required_core["fstservice"])
    print("CORE|fstworker|" + required_core["fstworker"])
' "$THROUGHPUT_PROFILE" "$PROFILE_MAX_AGGREGATE_RPS" \
        "$PROFILE_MAX_PER_ENDPOINT_RPS" \
        "$PROFILE_MAX_PER_ENDPOINT_CONCURRENCY" "$PROFILE_EXACT" \
        "$REQUIRE_RUN_ONCE" "$DATA_PROFILE" \
        "$EXPECTED_WORKER_IMAGE" "$EXPECTED_WORKER_CONFIG_SHA256" "$ACTION" \
        <<< "$compose_json"
)"

compose_snapshot() {
    local include_worker_profile="$1"
    shift
    if [[ "$include_worker_profile" == "true" ]]; then
        printf '%s\n' "$compose_json" \
            | docker compose --project-name "$COMPOSE_PROJECT_NAME" \
                --project-directory "$compose_dir" --profile worker \
                -f - "$@"
    else
        printf '%s\n' "$compose_json" \
            | docker compose --project-name "$COMPOSE_PROJECT_NAME" \
                --project-directory "$compose_dir" -f - "$@"
    fi
}

summary="$(head -n 1 <<< "$validation")"
IFS='|' read -r _ throughput_profile data_profile expected_count canonical_count max_rps per_endpoint_rps per_endpoint_concurrency connection_reuse_disabled curl_transport_enabled run_once <<< "$summary"
mapfile -t effective_nodes < <(sed -n 's/^NODE|//p' <<< "$validation")
postgres_container="$(sed -n 's/^CORE|postgres|//p' <<< "$validation")"
service_container="$(sed -n 's/^CORE|fstservice|//p' <<< "$validation")"
worker_container="$(sed -n 's/^CORE|fstworker|//p' <<< "$validation")"
if [[ -z "$postgres_container" ]]; then
    postgres_container="fst-postgres"
fi

if [[ "${#effective_nodes[@]}" -ne "$expected_count" ]]; then
    printf 'ERROR: internal guard node-count mismatch\n' >&2
    exit 1
fi

if [[ "$DATA_PROFILE" == "scrape-resume" ]]; then
    if ! validate_scrape_resume_worker_binding \
        "$compose_json" \
        "" \
        "" \
        "data profile scrape-resume"
    then
        exit 1
    fi
fi

verify_expected_worker_image_object() {
    local identity actual_id actual_revision

    if [[ -z "$EXPECTED_WORKER_IMAGE_ID" ]]; then
        return 0
    fi
    if ! identity="$(
        docker image inspect --format \
            '{{.Id}}|{{index .Config.Labels "org.opencontainers.image.revision"}}' \
            "$EXPECTED_WORKER_IMAGE" 2>/dev/null
    )"
    then
        printf 'ERROR: expected worker image object is unavailable\n' >&2
        exit 1
    fi
    IFS='|' read -r actual_id actual_revision <<< "$identity"
    if [[ "$actual_id" != "$EXPECTED_WORKER_IMAGE_ID" ]]; then
        printf 'ERROR: expected worker image reference resolved to a different image ID\n' >&2
        exit 1
    fi
    if [[ -n "$EXPECTED_WORKER_REVISION" \
        && "$actual_revision" != "$EXPECTED_WORKER_REVISION" ]]
    then
        printf 'ERROR: expected worker image revision label does not match\n' >&2
        exit 1
    fi
}

remove_created_worker() {
    local container_id="$1"
    if [[ "$container_id" =~ ^[0-9a-f]{64}$ ]]; then
        if ! docker rm --force "$container_id" >/dev/null 2>&1; then
            WORKER_CLEANUP_FAILED=1
            printf 'ERROR: unaccepted worker cleanup failed for exact container %s; stop and remove it before retry\n' \
                "$container_id" >&2
            return 0
        fi
        if docker inspect "$container_id" >/dev/null 2>&1 \
            || ! docker info >/dev/null 2>&1
        then
            WORKER_CLEANUP_FAILED=1
            printf 'ERROR: unaccepted worker absence could not be proven for exact container %s; stop and remove it before retry\n' \
                "$container_id" >&2
            return 0
        fi
        if [[ "$CREATED_WORKER_CONTAINER_ID" == "$container_id" ]]; then
            CREATED_WORKER_CONTAINER_ID=""
        fi
    fi
}

verify_created_worker_image_identity() {
    local container_id="$1"
    local expected_state="$2"
    local identity actual_container actual_id actual_image actual_revision
    local actual_running actual_status actual_exit_code actual_started_at

    if ! identity="$(
        docker inspect --format \
            '{{.Id}}|{{.Image}}|{{.Config.Image}}|{{index .Config.Labels "org.opencontainers.image.revision"}}|{{.State.Running}}|{{.State.Status}}|{{.State.ExitCode}}|{{.State.StartedAt}}' \
            "$container_id" 2>/dev/null
    )"
    then
        remove_created_worker "$container_id"
        printf 'ERROR: created worker identity is unavailable\n' >&2
        exit 1
    fi
    IFS='|' read -r actual_container actual_id actual_image actual_revision \
        actual_running actual_status actual_exit_code actual_started_at <<< "$identity"
    if [[ "$actual_container" != "$container_id" ]]
    then
        remove_created_worker "$container_id"
        printf 'ERROR: created worker runtime identity changed\n' >&2
        exit 1
    fi
    case "$expected_state" in
        created)
            if [[ "$actual_running" != "false" \
                || "$actual_status" != "created" \
                || "$actual_started_at" != "0001-01-01T00:00:00Z" ]]
            then
                remove_created_worker "$container_id"
                printf 'ERROR: worker executed before identity acceptance\n' >&2
                exit 1
            fi
            ;;
        continuous)
            if [[ "$actual_running" != "true" \
                || "$actual_status" != "running" ]]
            then
                remove_created_worker "$container_id"
                printf 'ERROR: continuous worker did not remain running after start\n' >&2
                exit 1
            fi
            ;;
        runonce)
            if [[ "$actual_started_at" == "0001-01-01T00:00:00Z" ]] \
                || { [[ "$actual_running" != "true" ]] \
                    && { [[ "$actual_status" != "exited" ]] \
                        || [[ "$actual_exit_code" != "0" ]]; }; }
            then
                remove_created_worker "$container_id"
                printf 'ERROR: run-once worker start was not observed\n' >&2
                exit 1
            fi
            ;;
        *)
            printf 'ERROR: internal worker identity state is invalid\n' >&2
            exit 1
            ;;
    esac
    if [[ -z "$EXPECTED_WORKER_IMAGE_ID" ]]; then
        return 0
    fi
    if [[ "$actual_id" == "$EXPECTED_WORKER_IMAGE_ID" \
        && "$actual_image" == "$EXPECTED_WORKER_IMAGE" ]] \
        && { [[ -z "$EXPECTED_WORKER_REVISION" ]] \
            || [[ "$actual_revision" == "$EXPECTED_WORKER_REVISION" ]]; }
    then
        return 0
    fi
    remove_created_worker "$container_id"
    printf 'ERROR: created worker image identity does not match the approved image\n' >&2
    exit 1
}

create_and_start_worker() {
    local container_id post_start_state

    PREVIOUS_WORKER_CONTAINER_ID="$(
        docker inspect --format '{{.Id}}' fstworker 2>/dev/null || true
    )"
    WORKER_CREATE_ATTEMPTED=1

    if ! compose_snapshot true up --no-start --no-deps --force-recreate \
        --pull never fstworker >/dev/null 2>&1
    then
        container_id="$(
            compose_snapshot true ps --all --quiet fstworker 2>/dev/null \
                | head -n 1 || true
        )"
        if [[ "$container_id" != "$PREVIOUS_WORKER_CONTAINER_ID" ]]; then
            CREATED_WORKER_CONTAINER_ID="$container_id"
            remove_created_worker "$container_id"
        fi
        printf 'ERROR: fstworker create failed\n' >&2
        return 1
    fi
    if ! container_id="$(
        compose_snapshot true ps --all --quiet fstworker \
            | head -n 1
    )"
    then
        container_id="$(
            docker inspect --format '{{.Id}}' fstworker 2>/dev/null || true
        )"
        CREATED_WORKER_CONTAINER_ID="$container_id"
        remove_created_worker "$container_id"
        printf 'ERROR: created worker container identity is unavailable\n' >&2
        return 1
    fi
    if [[ ! "$container_id" =~ ^[0-9a-f]{64}$ ]]; then
        remove_created_worker "$container_id"
        printf 'ERROR: created worker container identity is unavailable\n' >&2
        return 1
    fi
    CREATED_WORKER_CONTAINER_ID="$container_id"
    verify_created_worker_image_identity "$container_id" created
    if ! docker start "$container_id" >/dev/null; then
        remove_created_worker "$container_id"
        printf 'ERROR: fstworker start failed\n' >&2
        return 1
    fi
    post_start_state=continuous
    if [[ "$ACTION" == "recreate-runonce" ]]; then
        post_start_state=runonce
    fi
    verify_created_worker_image_identity "$container_id" "$post_start_state"
    DIRECT_WORKER_START_ACCEPTED=1
}

verify_expected_worker_image_object

inspect_container_state() {
    local container="$1"
    local state

    if ! state="$(
        docker inspect --format \
            '{{.State.Status}}|{{if .State.Health}}{{.State.Health.Status}}{{else}}none{{end}}' \
            "$container" 2>/dev/null
    )"
    then
        printf 'missing|none'
        return 0
    fi
    printf '%s' "$state"
}

sleep_until_deadline() {
    local deadline="$1"
    local remaining
    local delay="${2:-$RECOVERY_POLL_INTERVAL_SECONDS}"

    if [[ "$ACTION" == "recover-start" && "$RECOVERY_TOTAL_DEADLINE_AT" -gt 0 \
        && "$RECOVERY_TOTAL_DEADLINE_AT" -lt "$deadline" ]]
    then
        deadline="$RECOVERY_TOTAL_DEADLINE_AT"
    fi
    remaining=$((deadline - SECONDS))
    if ((remaining <= 0)); then
        return 0
    fi
    if ((delay > remaining)); then
        delay="$remaining"
    fi
    sleep "$delay"
}

core_is_ready() {
    if [[ "$(inspect_container_state "$postgres_container")" != "running|healthy" \
        || "$(inspect_container_state "$service_container")" != "running|healthy" ]]
    then
        return 1
    fi

    docker exec "$service_container" \
        curl -fsS --connect-timeout 2 --max-time 5 \
        http://localhost:8080/readyz >/dev/null 2>&1
}

wait_for_core_ready() {
    local deadline=$((SECONDS + RECOVERY_CORE_WAIT_SECONDS))

    while true; do
        if ! enforce_recovery_total_deadline; then
            return 2
        fi
        if core_is_ready; then
            return 0
        fi
        if ! enforce_recovery_total_deadline; then
            return 2
        fi
        if ((SECONDS >= deadline)); then
            return 1
        fi
        sleep_until_deadline "$deadline"
    done
}

verify_acquisition_checkpoint_schema() {
    local schema_state

    if ! schema_state="$(
        docker exec "$postgres_container" \
            psql -X -A -t -q -U fst -d fstservice \
                -v ON_ERROR_STOP=1 \
                -c "/* fst_boot_acquisition_checkpoint_schema */ SELECT CASE
                        WHEN (
                            SELECT COUNT(*)
                            FROM information_schema.columns
                            WHERE table_schema = 'public'
                              AND table_name = 'scrape_log'
                              AND column_name IN (
                                  'acquisition_completed_at',
                                  'expected_solo_scope_count',
                                  'expected_solo_scope_fingerprint_version',
                                  'expected_solo_scope_fingerprint')
                        ) = 4
                        AND EXISTS (
                            SELECT 1
                            FROM pg_constraint
                            WHERE conrelid =
                                    'public.scrape_log'::regclass
                              AND conname =
                                    'ck_scrape_log_acquisition_checkpoint'
                              AND convalidated)
                        THEN 'ready'
                        ELSE 'missing'
                    END"
    )"
    then
        printf 'ERROR: recovery could not verify the acquisition checkpoint release schema\n' >&2
        return 1
    fi
    if [[ "$schema_state" != "ready" ]]; then
        printf 'ERROR: recovery requires the acquisition checkpoint release schema; run the candidate service with --initialize-schema-only before starting fstworker\n' >&2
        return 1
    fi
}

verify_wire_send_telemetry_schema() {
    local schema_state

    if ! schema_state="$(
        docker exec "$postgres_container" \
            psql -X -A -t -q -U fst -d fstservice \
                -v ON_ERROR_STOP=1 \
                -c "/* fst_boot_wire_send_telemetry_schema */ SELECT CASE
                        WHEN (
                            SELECT COUNT(*)
                            FROM information_schema.columns
                            WHERE table_schema = 'public'
                              AND table_name = 'scrape_log'
                              AND data_type = 'bigint'
                              AND udt_name = 'int8'
                              AND column_name IN (
                                  'wire_send_total',
                                  'wire_send_probe_sends',
                                  'wire_send_probe_successes',
                                  'wire_send_status_retries',
                                  'wire_send_network_errors',
                                  'wire_send_cdn_blocks'
                              )
                        ) = 6
                        AND (
                            SELECT COUNT(*)
                            FROM pg_constraint
                            WHERE conrelid = 'public.scrape_log'::regclass
                              AND conname IN (
                                  'ck_scrape_log_wire_send_telemetry',
                                  'ck_scrape_log_wire_send_telemetry_complete'
                              )
                              AND convalidated
                        ) = 2
                        THEN 'ready'
                        ELSE 'missing'
                    END"
    )"
    then
        printf 'ERROR: wire-send-telemetry could not verify the durable telemetry schema\n' >&2
        return 1
    fi
    if [[ "$schema_state" != "ready" ]]; then
        printf 'ERROR: wire-send-telemetry requires all six telemetry columns and validated telemetry constraints; initialize the candidate schema before starting fstworker\n' >&2
        return 1
    fi
}

recovery_baseline_worker_instance=""
recovery_baseline_worker_heartbeat=""
recovery_current_update_status=""
recovery_current_scrape_id=""
recovery_public_reads_frozen=""
recovery_freeze_reason=""
recovery_published_scrape_id=""
recovery_worker_api_status=""
recovery_worker_api_instance=""
recovery_worker_api_heartbeat=""
recovery_worker_api_heartbeat_age=""
recovery_worker_api_stale_after=""

read_recovery_service_snapshot() {
    local allow_worker_present="${1:-false}"
    local worker_state worker_runtime_status service_info
    local -a snapshot

    worker_state="$(inspect_container_state "$worker_container")"
    worker_runtime_status="${worker_state%%|*}"
    if [[ "$allow_worker_present" != "true" ]]; then
        case "$worker_runtime_status" in
            missing|created|exited|dead)
                ;;
            *)
                printf 'ERROR: recovery requires fstworker to be stopped or absent\n' >&2
                return 1
                ;;
        esac
    fi

    if ! service_info="$(
        docker exec "$service_container" \
            curl -fsS --connect-timeout 2 --max-time 10 \
            http://localhost:8080/api/service-info 2>/dev/null
    )"
    then
        printf 'ERROR: recovery could not read the operational service state\n' >&2
        return 1
    fi

    if ! mapfile -t snapshot < <(
        python3 -c '
import json
import sys

try:
    payload = json.load(sys.stdin)
except (json.JSONDecodeError, TypeError):
    raise SystemExit(
        "ERROR: recovery received an invalid operational service response")

current = payload.get("currentUpdate")
publication = payload.get("publication")
if not isinstance(current, dict) or not isinstance(publication, dict):
    raise SystemExit(
        "ERROR: recovery requires current update and publication state")

worker = payload.get("workerStatus")
if not isinstance(worker, dict):
    worker = {}

def stringify(value):
    if value is None:
        return ""
    if isinstance(value, bool):
        return "true" if value else "false"
    return str(value)

print(stringify(current.get("status")))
print(stringify(current.get("scrapeId")))
print(stringify(publication.get("publicReadsFrozen")))
print(stringify(publication.get("freezeReason")))
print(stringify(publication.get("publishedScrapeId")))
print(stringify(worker.get("status")))
print(stringify(worker.get("instanceId")))
print(stringify(worker.get("lastHeartbeatAt")))
print(stringify(worker.get("heartbeatAgeSeconds")))
print(stringify(worker.get("staleAfterSeconds")))
' <<< "$service_info"
    )
    then
        return 1
    fi

    recovery_current_update_status="${snapshot[0]:-}"
    recovery_current_scrape_id="${snapshot[1]:-}"
    recovery_public_reads_frozen="${snapshot[2]:-}"
    recovery_freeze_reason="${snapshot[3]:-}"
    recovery_published_scrape_id="${snapshot[4]:-}"
    recovery_worker_api_status="${snapshot[5]:-}"
    recovery_worker_api_instance="${snapshot[6]:-}"
    recovery_worker_api_heartbeat="${snapshot[7]:-}"
    recovery_worker_api_heartbeat_age="${snapshot[8]:-}"
    recovery_worker_api_stale_after="${snapshot[9]:-}"
}

worker_api_is_stale_or_offline() {
    if [[ "$recovery_worker_api_status" != "online" ]]; then
        return 0
    fi
    if [[ -n "$recovery_worker_api_heartbeat_age" \
        && "$recovery_worker_api_heartbeat_age" =~ ^[0-9]+([.][0-9]+)?$ \
        && -n "$recovery_worker_api_stale_after" \
        && "$recovery_worker_api_stale_after" =~ ^[0-9]+([.][0-9]+)?$ ]]
    then
        python3 -c '
import sys
age = float(sys.argv[1])
stale_after = float(sys.argv[2])
raise SystemExit(0 if stale_after > 0 and age > stale_after else 1)
' "$recovery_worker_api_heartbeat_age" "$recovery_worker_api_stale_after"
        return $?
    fi
    return 1
}

recovery_update_is_terminal() {
    [[ "$recovery_current_update_status" == "idle" \
        || "$recovery_current_update_status" == "failed" ]]
}

capture_recovery_safety_snapshot() {
    if ! read_recovery_service_snapshot true; then
        return 1
    fi
    if ! recovery_update_is_terminal; then
        printf 'ERROR: recovery requires the current update state to be idle or failed\n' >&2
        return 1
    fi
    if [[ "$recovery_public_reads_frozen" != "false" ]]; then
        printf 'ERROR: recovery requires public reads to be unfrozen\n' >&2
        return 1
    fi

    recovery_baseline_worker_instance="$recovery_worker_api_instance"
    recovery_baseline_worker_heartbeat="$recovery_worker_api_heartbeat"
}

validate_scrape_resume_runtime_state() {
    local worker_state worker_runtime_status service_info resume_scrape_id

    worker_state="$(inspect_container_state "fstworker")"
    worker_runtime_status="${worker_state%%|*}"
    case "$worker_runtime_status" in
        missing|created|exited|dead)
            ;;
        *)
            printf 'ERROR: scrape resume requires fstworker to be stopped or absent\n' >&2
            return 1
            ;;
    esac

    resume_scrape_id="$(
        python3 -c '
import json
import sys
config = json.load(sys.stdin)
environment = (
    config.get("services", {})
    .get("fstworker", {})
    .get("environment", {})
)
print(environment.get("Scraper__ResumeScrapeId", ""))
' <<< "$compose_json"
    )"

    if ! service_info="$(
        docker exec fstservice \
            curl -fsS --connect-timeout 2 --max-time 10 \
            http://localhost:8080/api/service-info 2>/dev/null
    )"
    then
        printf 'ERROR: scrape resume could not read the operational service state\n' >&2
        return 1
    fi

    if ! python3 -c '
import json
import sys

expected_scrape_id = int(sys.argv[1])
try:
    payload = json.load(sys.stdin)
except (json.JSONDecodeError, TypeError):
    raise SystemExit(
        "ERROR: scrape resume received an invalid operational service response")

current = payload.get("currentUpdate")
publication = payload.get("publication")
if not isinstance(current, dict) or not isinstance(publication, dict):
    raise SystemExit(
        "ERROR: scrape resume requires current update and publication state")
if current.get("status") not in {"updating", "stalled"}:
    raise SystemExit(
        "ERROR: scrape resume requires the current update state to be updating or stalled")
if current.get("scrapeId") != expected_scrape_id:
    raise SystemExit(
        "ERROR: scrape resume current update does not match the configured scrape")
if publication.get("publicReadsFrozen") is not True:
    raise SystemExit(
        "ERROR: scrape resume requires public reads to remain frozen")
if publication.get("freezeReason") != "post-process":
    raise SystemExit(
        "ERROR: scrape resume requires publication freeze reason post-process")
if publication.get("publishedScrapeId") == expected_scrape_id:
    raise SystemExit(
        "ERROR: scrape resume target is already published")
' "$resume_scrape_id" <<< "$service_info"
    then
        return 1
    fi

    printf 'compose_guard resume=preflight worker=stopped scrape=%s update=resume-eligible reads=frozen\n' \
        "$resume_scrape_id"
}

recovery_boot_mode=""
recovery_active_scrape_id=""
recovery_deferred_publication_id=""
recovery_active_resume_state_json=""

resolve_worker_image_binding() {
    local image_ref="$1"
    local identity actual_id actual_revision

    if ! identity="$(
        docker image inspect --format \
            '{{.Id}}|{{index .Config.Labels "org.opencontainers.image.revision"}}' \
            "$image_ref" 2>/dev/null
    )"
    then
        printf 'ERROR: expected worker image object is unavailable\n' >&2
        return 1
    fi
    IFS='|' read -r actual_id actual_revision <<< "$identity"
    printf '%s\n%s\n' "$actual_id" "$actual_revision"
}

load_active_resume_state_json() {
    local scrape_id="$1"

    docker exec "$postgres_container" \
        psql -X -A -t -q -U fst -d fstservice \
            -v ON_ERROR_STOP=1 \
            -c "/* fst_boot_active_recovery_state */ WITH target_scrape AS (
                    SELECT
                        scrape.id,
                        scrape.started_at,
                        scrape.status,
                        scrape.acquisition_completed_at,
                        scrape.songs_scraped,
                        scrape.total_entries,
                        scrape.total_requests,
                        scrape.total_bytes,
                        scrape.epic_reported_over_100_pages,
                        scrape.expected_solo_scope_count,
                        scrape.expected_solo_scope_fingerprint_version,
                        scrape.expected_solo_scope_fingerprint
                    FROM scrape_log scrape
                    WHERE scrape.id = ${scrape_id}
                ),
                manifest_counts AS (
                    SELECT
                        COUNT(*)::int AS manifest_count,
                        COUNT(*) FILTER (WHERE is_complete)::int
                            AS complete_manifest_count
                    FROM leaderboard_scope_manifests
                    WHERE scrape_id = ${scrape_id}
                ),
                writer_failures AS (
                    SELECT COUNT(*)::int AS writer_failure_count
                    FROM scrape_writer_failures
                    WHERE scrape_id = ${scrape_id}
                ),
                critical_failures AS (
                    SELECT COUNT(*)::int AS critical_failure_count
                    FROM scrape_phase_outcomes
                    WHERE scrape_id = ${scrape_id}
                      AND criticality = 'publication_critical'
                      AND status <> 'completed'
                ),
                candidate_generation AS (
                    SELECT publication_id, status
                    FROM publication_generations
                    WHERE scrape_id = ${scrape_id}
                ),
                publication_state AS (
                    SELECT
                        published_scrape_id,
                        working_publication_id,
                        public_reads_frozen_reason,
                        improvement_notifications_scrape_id,
                        improvement_notifications_status
                    FROM scrape_publication_state
                    WHERE id = TRUE
                ),
                catalog AS (
                    SELECT
                        catalog.publication_id,
                        catalog.catalog_version,
                        catalog.schema_version,
                        catalog.catalog_json::text AS catalog_json,
                        catalog.content_hash,
                        catalog.song_count
                    FROM candidate_generation generation
                    JOIN publication_song_catalog catalog
                      ON catalog.publication_id = generation.publication_id
                    JOIN publication_surface_bindings binding
                      ON binding.publication_id = generation.publication_id
                     AND binding.surface_name = 'song_catalog'
                    WHERE catalog.is_exact
                      AND catalog.source_kind = 'provider_exact'
                      AND binding.binding_kind =
                            'generation_catalog_snapshot'
                      AND binding.status = 'ready'
                      AND binding.row_count = catalog.song_count
                      AND binding.content_hash = catalog.content_hash
                ),
                complete_solo_pairs AS (
                    SELECT DISTINCT manifest.song_id, manifest.instrument
                    FROM leaderboard_scope_manifests manifest
                    WHERE manifest.scrape_id = ${scrape_id}
                      AND manifest.scope_kind = 'alltime'
                      AND manifest.is_complete
                      AND manifest.instrument = ANY(ARRAY[
                          'Solo_Guitar',
                          'Solo_Bass',
                          'Solo_Vocals',
                          'Solo_Drums',
                          'Solo_PeripheralGuitar',
                          'Solo_PeripheralBass',
                          'Solo_PeripheralVocals',
                          'Solo_PeripheralCymbals',
                          'Solo_PeripheralDrums'
                      ])
                )
                SELECT row_to_json(result)::text
                FROM (
                    SELECT
                        target.id AS \"scrapeId\",
                        target.started_at AS \"startedAtUtc\",
                        target.status,
                        publication.published_scrape_id AS \"publishedScrapeId\",
                        publication.working_publication_id AS \"workingPublicationId\",
                        publication.public_reads_frozen_reason
                            AS \"publicReadsFrozenReason\",
                        publication.improvement_notifications_scrape_id
                            AS \"improvementNotificationsScrapeId\",
                        publication.improvement_notifications_status
                            AS \"improvementNotificationsStatus\",
                        generation.publication_id AS \"candidatePublicationId\",
                        generation.status AS \"candidatePublicationStatus\",
                        manifests.manifest_count AS \"manifestCount\",
                        manifests.complete_manifest_count
                            AS \"completeManifestCount\",
                        writers.writer_failure_count
                            AS \"writerFailureCount\",
                        critical.critical_failure_count
                            AS \"criticalPhaseFailureCount\",
                        target.acquisition_completed_at
                            AS \"acquisitionCompletedAtUtc\",
                        target.songs_scraped AS \"songsScraped\",
                        target.total_entries AS \"totalEntries\",
                        target.total_requests AS \"totalRequests\",
                        target.total_bytes AS \"totalBytes\",
                        target.epic_reported_over_100_pages
                            AS \"epicReportedOver100Pages\",
                        target.expected_solo_scope_count
                            AS \"expectedSoloScopeCount\",
                        target.expected_solo_scope_fingerprint_version
                            AS \"expectedSoloScopeFingerprintVersion\",
                        target.expected_solo_scope_fingerprint
                            AS \"expectedSoloScopeFingerprint\",
                        catalog.catalog_version
                            AS \"publicationCatalogVersion\",
                        catalog.schema_version
                            AS \"publicationCatalogSchemaVersion\",
                        catalog.content_hash
                            AS \"publicationCatalogContentHash\",
                        catalog.song_count AS \"publicationSongCount\",
                        catalog.catalog_json AS \"publicationCatalogJson\",
                        COALESCE((
                            SELECT json_agg(
                                json_build_array(song_id, instrument)
                                ORDER BY instrument, song_id)
                            FROM complete_solo_pairs
                        ), '[]'::json) AS \"completeSoloPairs\",
                        EXISTS (
                            SELECT 1
                            FROM scrape_publication_state state
                            LEFT JOIN publication_generations working
                              ON working.publication_id =
                                    state.working_publication_id
                            WHERE state.id = TRUE
                              AND (
                                  state.public_reads_frozen_reason =
                                      '${PUBLICATION_COMMIT_DEFERRED_REASON}'
                                  OR working.status = 'ready'
                              )
                        ) AS \"startupShouldResumeDeferredPublication\"
                    FROM target_scrape target
                    LEFT JOIN manifest_counts manifests ON TRUE
                    LEFT JOIN writer_failures writers ON TRUE
                    LEFT JOIN critical_failures critical ON TRUE
                    LEFT JOIN publication_state publication ON TRUE
                    LEFT JOIN candidate_generation generation ON TRUE
                    LEFT JOIN catalog ON TRUE
                ) result"
}

validate_active_resume_state() {
    local expected_scrape_id="$1"
    local expected_published_scrape_id="$2"

    if ! recovery_active_resume_state_json="$(
        load_active_resume_state_json "$expected_scrape_id"
    )"
    then
        printf 'ERROR: recovery could not read the durable resume candidate state\n' >&2
        return 1
    fi
    if [[ -z "$recovery_active_resume_state_json" ]]; then
        printf 'ERROR: recovery resume candidate scrape is missing\n' >&2
        return 1
    fi

    if ! python3 -c '
import hashlib
import json
import struct
import sys

expected_scrape_id = int(sys.argv[1])
expected_published_scrape_id = int(sys.argv[2])
state = json.load(sys.stdin)
canonical_instruments = [
    "Solo_Guitar",
    "Solo_Bass",
    "Solo_Vocals",
    "Solo_Drums",
    "Solo_PeripheralGuitar",
    "Solo_PeripheralBass",
    "Solo_PeripheralVocals",
    "Solo_PeripheralCymbals",
    "Solo_PeripheralDrums",
]

def fail(message):
    raise SystemExit(f"ERROR: {message}")

if state.get("scrapeId") != expected_scrape_id:
    fail("recovery resume candidate scrape does not match the service state")
if state.get("status") != "running":
    fail("recovery resume candidate is not running")
if state.get("publishedScrapeId") != expected_published_scrape_id:
    fail("recovery published scrape does not match the service state")
if state.get("candidatePublicationId") != state.get("workingPublicationId"):
    fail("recovery candidate publication is not the working publication")
if state.get("candidatePublicationStatus") == "ready" or state.get("startupShouldResumeDeferredPublication"):
    fail("recovery candidate should resume through the existing deferred-publication startup path")
if state.get("manifestCount", 0) <= 0:
    fail("recovery candidate has no durable manifests")
if state.get("completeManifestCount") != state.get("manifestCount"):
    fail("recovery candidate manifests are incomplete")
if state.get("writerFailureCount") != 0:
    fail("recovery candidate has writer failures")
if state.get("criticalPhaseFailureCount") != 0:
    fail("recovery candidate has publication-critical failures")
if state.get("acquisitionCompletedAtUtc") is None:
    fail("recovery candidate acquisition checkpoint is missing")
for field in ("songsScraped", "totalEntries", "totalRequests", "totalBytes"):
    value = state.get(field)
    if not isinstance(value, int) or value <= 0:
        fail(f"recovery candidate {field} must be a positive persisted value")
if not isinstance(state.get("epicReportedOver100Pages"), bool):
    fail("recovery candidate Epic page-count signal is missing")
if state.get("expectedSoloScopeFingerprintVersion") != 1:
    fail("recovery candidate solo scope fingerprint version is unsupported")
fingerprint = state.get("expectedSoloScopeFingerprint")
if not isinstance(fingerprint, str) or len(fingerprint) != 64 or any(ch not in "0123456789abcdef" for ch in fingerprint):
    fail("recovery candidate solo scope fingerprint is invalid")
catalog_json = state.get("publicationCatalogJson")
if not isinstance(catalog_json, str) or not catalog_json:
    fail("recovery candidate publication song catalog is missing")
catalog_document = json.loads(catalog_json)
catalog_schema_version = state.get("publicationCatalogSchemaVersion")
if catalog_schema_version == 1:
    catalog = catalog_document
elif catalog_schema_version == 2 and isinstance(catalog_document, dict):
    catalog = catalog_document.get("songs")
else:
    fail("recovery candidate publication song catalog schema is unsupported")
if not isinstance(catalog, list):
    fail("recovery candidate publication song catalog is invalid")
catalog_song_ids = []
for item in catalog:
    if not isinstance(item, dict):
        fail("recovery candidate publication song catalog is invalid")
    track = item.get("track")
    song_id = track.get("su") if isinstance(track, dict) else None
    if not isinstance(song_id, str) or not song_id.strip():
        fail("recovery candidate publication song catalog is invalid")
    catalog_song_ids.append(song_id)
catalog_song_set = set(catalog_song_ids)
if len(catalog_song_set) != len(catalog_song_ids):
    fail("recovery candidate publication song catalog is invalid")
if state.get("publicationSongCount") != len(catalog_song_ids):
    fail("recovery candidate publication song count does not match the catalog")
if state.get("songsScraped") > len(catalog_song_ids):
    fail("recovery candidate songs scraped exceeds the publication song catalog")
expected_scope_count = len(catalog_song_ids) * len(canonical_instruments)
if state.get("expectedSoloScopeCount") != expected_scope_count:
    fail("recovery candidate solo scope count does not cover the exact catalog and canonical instruments")
pairs = state.get("completeSoloPairs")
if not isinstance(pairs, list):
    fail("recovery candidate complete solo manifests are invalid")
normalized_pairs = []
for pair in pairs:
    if not isinstance(pair, list) or len(pair) != 2:
        fail("recovery candidate complete solo manifests are invalid")
    song_id, instrument = pair
    if not isinstance(song_id, str) or not song_id.strip():
        fail("recovery candidate complete solo manifests are invalid")
    if instrument not in canonical_instruments:
        fail("recovery candidate complete solo manifests include a noncanonical instrument")
    if song_id not in catalog_song_set:
        fail("recovery candidate complete solo manifests are not owned by the publication catalog")
    normalized_pairs.append((song_id, instrument))
if len(set(normalized_pairs)) != expected_scope_count:
    fail("recovery candidate complete solo manifest count differs from the acquisition checkpoint")
if set(normalized_pairs) != {
    (song_id, instrument)
    for song_id in catalog_song_ids
    for instrument in canonical_instruments
}:
    fail("recovery candidate solo scope does not cover every catalog song and canonical instrument")
hash_state = hashlib.sha256()
hash_state.update(b"fst-solo-acquisition-scope\x00v1\x00")
for song_id, instrument in sorted(normalized_pairs, key=lambda item: (item[1], item[0])):
    instrument_bytes = instrument.encode()
    song_bytes = song_id.encode()
    hash_state.update(struct.pack(">i", len(instrument_bytes)))
    hash_state.update(instrument_bytes)
    hash_state.update(struct.pack(">i", len(song_bytes)))
    hash_state.update(song_bytes)
if hash_state.hexdigest() != fingerprint:
    fail("recovery candidate complete solo manifest fingerprint differs from the acquisition checkpoint")
' "$expected_scrape_id" "$expected_published_scrape_id" \
        <<< "$recovery_active_resume_state_json"
    then
        return 1
    fi
}

determine_recovery_boot_mode() {
    recovery_boot_mode=""
    recovery_active_scrape_id=""
    recovery_deferred_publication_id=""

    if ! read_recovery_service_snapshot; then
        return 1
    fi

    recovery_baseline_worker_instance="$recovery_worker_api_instance"
    recovery_baseline_worker_heartbeat="$recovery_worker_api_heartbeat"

    if recovery_update_is_terminal \
        && [[ "$recovery_public_reads_frozen" == "false" ]]
    then
        recovery_boot_mode="continuous-idle"
        return 0
    fi

    if [[ "$recovery_current_update_status" == "idle" \
        && "$recovery_public_reads_frozen" == "true" \
        && "$recovery_freeze_reason" == "$PUBLICATION_COMMIT_DEFERRED_REASON" ]]
    then
        recovery_boot_mode="continuous-deferred"
        recovery_deferred_publication_id="$recovery_published_scrape_id"
        return 0
    fi
    if recovery_update_is_terminal; then
        printf 'ERROR: recovery requires public reads to be unfrozen\n' >&2
        return 1
    fi

    if [[ "$recovery_current_update_status" != "updating" \
        && "$recovery_current_update_status" != "stalled" ]]
    then
        printf 'ERROR: recovery requires the current update state to be idle, failed, updating, or stalled\n' >&2
        return 1
    fi
    if [[ "$recovery_public_reads_frozen" != "true" ]]; then
        printf 'ERROR: recovery requires the current update state to be idle\n' >&2
        return 1
    fi
    if [[ ! "$recovery_current_scrape_id" =~ ^[0-9]+$ ]]; then
        printf 'ERROR: recovery active candidate state is missing the current scrape ID\n' >&2
        return 1
    fi
    if [[ "$recovery_freeze_reason" != "post-process" ]]; then
        printf 'ERROR: recovery active candidate requires publication freeze reason post-process\n' >&2
        return 1
    fi
    if [[ "$recovery_published_scrape_id" == "$recovery_current_scrape_id" ]]; then
        printf 'ERROR: recovery active candidate is already published\n' >&2
        return 1
    fi
    if ! worker_api_is_stale_or_offline; then
        printf 'ERROR: recovery active candidate requires the prior worker heartbeat to be stale or offline\n' >&2
        return 1
    fi

    recovery_boot_mode="active-resume"
    recovery_active_scrape_id="$recovery_current_scrape_id"
}

declare -a unhealthy_effective_nodes=()

refresh_unhealthy_effective_nodes() {
    local node

    unhealthy_effective_nodes=()
    for node in "${effective_nodes[@]}"; do
        if ! enforce_recovery_total_deadline; then
            return 2
        fi
        if [[ "$(inspect_container_state "$node")" != "running|healthy" ]]; then
            unhealthy_effective_nodes+=("$node")
        fi
    done
    enforce_recovery_total_deadline
}

wait_for_effective_proxy_health() {
    local timeout_seconds="$1"
    local deadline=$((SECONDS + timeout_seconds))
    local refresh_status

    while true; do
        refresh_status=0
        refresh_unhealthy_effective_nodes || refresh_status=$?
        if ((refresh_status == 2)); then
            return 2
        fi
        if ((${#unhealthy_effective_nodes[@]} == 0)); then
            return 0
        fi
        if ((SECONDS >= deadline)); then
            return 1
        fi
        sleep_until_deadline "$deadline"
    done
}

recreated_proxy_count=0

recreate_unhealthy_effective_proxies() {
    local recreate_count refresh_status

    refresh_status=0
    refresh_unhealthy_effective_nodes || refresh_status=$?
    if ((refresh_status == 2)); then
        return 2
    fi
    recreate_count="${#unhealthy_effective_nodes[@]}"
    if ((recreate_count == 0)); then
        return 0
    fi
    if ((recreate_count > RECOVERY_MAX_PROXY_RECREATES)); then
        printf 'ERROR: unhealthy effective proxy count exceeds the recovery cap\n' >&2
        return 1
    fi

    printf 'compose_guard recovery=proxy-recreate count=%s\n' \
        "$recreate_count"
    if ! enforce_recovery_total_deadline; then
        return 2
    fi
    if ! (
        compose_snapshot false up -d --no-deps --force-recreate \
            "${unhealthy_effective_nodes[@]}" >/dev/null 2>&1
    )
    then
        printf 'ERROR: effective proxy recreate failed\n' >&2
        return 1
    fi
    if ! enforce_recovery_total_deadline; then
        return 2
    fi
    recreated_proxy_count="$recreate_count"
}

worker_api_is_fresh() {
    local service_info

    if ! service_info="$(
        docker exec "$service_container" \
            curl -fsS --connect-timeout 2 --max-time 10 \
            http://localhost:8080/api/service-info 2>/dev/null
    )"
    then
        return 1
    fi

    python3 -c '
import json
import sys

baseline_instance = sys.argv[1]
baseline_heartbeat = sys.argv[2]
fresh_limit = float(sys.argv[3])

try:
    payload = json.load(sys.stdin)
except (json.JSONDecodeError, TypeError):
    raise SystemExit(1)

worker = payload.get("workerStatus")
if not isinstance(worker, dict) or worker.get("status") != "online":
    raise SystemExit(1)

instance = worker.get("instanceId")
heartbeat = worker.get("lastHeartbeatAt")
age = worker.get("heartbeatAgeSeconds")
stale_after = worker.get("staleAfterSeconds")
number_types = (int, float)
if not isinstance(instance, str) or not instance or instance == baseline_instance:
    raise SystemExit(1)
if not isinstance(heartbeat, str) or not heartbeat or heartbeat == baseline_heartbeat:
    raise SystemExit(1)
if isinstance(age, bool) or not isinstance(age, number_types) or age < 0:
    raise SystemExit(1)
if age > fresh_limit:
    raise SystemExit(1)
if (not isinstance(stale_after, bool)
        and isinstance(stale_after, number_types)
        and stale_after > 0
        and age > stale_after):
    raise SystemExit(1)
' "$recovery_baseline_worker_instance" \
        "$recovery_baseline_worker_heartbeat" \
        "$RECOVERY_HEARTBEAT_FRESH_SECONDS" \
        <<< "$service_info" >/dev/null 2>&1
}

worker_recovery_is_ready() {
    [[ "$(inspect_container_state "$worker_container")" == "running|healthy" ]] \
        && worker_api_is_fresh
}

wait_for_worker_recovery_ready() {
    local deadline=$((SECONDS + RECOVERY_WORKER_WAIT_SECONDS))

    while true; do
        if ! enforce_recovery_total_deadline; then
            return 2
        fi
        if worker_recovery_is_ready; then
            return 0
        fi
        if ! enforce_recovery_total_deadline; then
            return 2
        fi
        if ((SECONDS >= deadline)); then
            return 1
        fi
        sleep_until_deadline "$deadline"
    done
}

worker_identity_matches_binding() {
    local expected_image="$1"
    local expected_image_id="$2"
    local expected_revision="$3"
    local identity actual_container actual_id actual_image actual_revision
    local actual_running actual_status actual_exit_code actual_started_at

    if ! identity="$(
        docker inspect --format \
            '{{.Id}}|{{.Image}}|{{.Config.Image}}|{{index .Config.Labels "org.opencontainers.image.revision"}}|{{.State.Running}}|{{.State.Status}}|{{.State.ExitCode}}|{{.State.StartedAt}}' \
            "$worker_container" 2>/dev/null
    )"
    then
        return 1
    fi
    IFS='|' read -r actual_container actual_id actual_image actual_revision \
        actual_running actual_status actual_exit_code actual_started_at <<< "$identity"
    [[ "$actual_id" == "$expected_image_id" \
        && "$actual_image" == "$expected_image" \
        && "$actual_revision" == "$expected_revision" ]]
}

run_proxy_recovery_sequence() {
    local initial_wait_status recreate_status recreate_wait_status

    printf 'compose_guard recovery=proxy-wait phase=initial\n'
    initial_wait_status=0
    wait_for_effective_proxy_health "$RECOVERY_INITIAL_WAIT_SECONDS" \
        || initial_wait_status=$?
    if ((initial_wait_status == 0)); then
        printf 'compose_guard recovery=proxy-convergence phase=initial status=healthy\n'
        return 0
    fi
    if ((initial_wait_status == 2)); then
        return 1
    fi

    printf 'compose_guard recovery=proxy-convergence phase=initial unhealthy=%s\n' \
        "${#unhealthy_effective_nodes[@]}"
    if ! core_is_ready; then
        printf 'ERROR: core readiness was lost before proxy recovery\n' >&2
        return 1
    fi
    if ! enforce_recovery_total_deadline; then
        return 1
    fi
    recreate_status=0
    recreate_unhealthy_effective_proxies || recreate_status=$?
    if ((recreate_status == 2)); then
        return 1
    fi
    if ((recreate_status != 0)); then
        return 1
    fi

    printf 'compose_guard recovery=proxy-wait phase=post-recreate\n'
    recreate_wait_status=0
    wait_for_effective_proxy_health "$RECOVERY_RECREATE_WAIT_SECONDS" \
        || recreate_wait_status=$?
    if ((recreate_wait_status == 2)); then
        return 1
    fi
    if ((recreate_wait_status != 0)); then
        printf 'ERROR: effective proxies did not become healthy after bounded recovery\n' >&2
        return 1
    fi
    printf 'compose_guard recovery=proxy-convergence phase=post-recreate status=healthy\n'
}

start_continuous_worker_after_preflight() {
    printf 'compose_guard recovery=worker-start service=fstworker mode=continuous\n'
    recovery_worker_start_attempted=1
    verify_inherited_worker_mutation_lock
    verify_expected_worker_image_object
    if ! create_and_start_worker
    then
        printf 'ERROR: fstworker recreate/start failed\n' >&2
        return 1
    fi
    if ! enforce_recovery_total_deadline; then
        return 1
    fi

    printf 'compose_guard recovery=worker-wait\n'
    worker_wait_status=0
    wait_for_worker_recovery_ready || worker_wait_status=$?
    if ((worker_wait_status == 2)); then
        return 1
    fi
    if ((worker_wait_status != 0)); then
        printf 'ERROR: fstworker health and fresh heartbeat did not converge\n' >&2
        return 1
    fi
    if ! enforce_recovery_total_deadline; then
        return 1
    fi

    recovery_worker_accepted=1
    printf 'compose_guard recovery=ok recreated=%s worker=online heartbeat=fresh\n' \
        "$recreated_proxy_count"
}

start_runonce_worker_with_existing_lock() {
    local runonce_compose_json_arg="$1"
    local runonce_worker_image_arg="$2"
    local expected_worker_image_id_arg="$3"
    local expected_worker_revision_arg="$4"
    local runonce_worker_config_sha256_arg="$5"
    local original_action="$ACTION"
    local original_data_profile="$DATA_PROFILE"
    local original_require_run_once="$REQUIRE_RUN_ONCE"
    local original_compose_json="$compose_json"
    local original_expected_worker_image="$EXPECTED_WORKER_IMAGE"
    local original_expected_worker_image_id="$EXPECTED_WORKER_IMAGE_ID"
    local original_expected_worker_revision="$EXPECTED_WORKER_REVISION"
    local original_expected_worker_config_sha256="$EXPECTED_WORKER_CONFIG_SHA256"

    ACTION="recreate-runonce"
    DATA_PROFILE="scrape-resume"
    REQUIRE_RUN_ONCE=true
    compose_json="$runonce_compose_json_arg"
    EXPECTED_WORKER_IMAGE="$runonce_worker_image_arg"
    EXPECTED_WORKER_IMAGE_ID="$expected_worker_image_id_arg"
    EXPECTED_WORKER_REVISION="$expected_worker_revision_arg"
    EXPECTED_WORKER_CONFIG_SHA256="$runonce_worker_config_sha256_arg"

    recovery_worker_start_attempted=1
    verify_expected_worker_image_object
    if ! create_and_start_worker; then
        ACTION="$original_action"
        DATA_PROFILE="$original_data_profile"
        REQUIRE_RUN_ONCE="$original_require_run_once"
        compose_json="$original_compose_json"
        EXPECTED_WORKER_IMAGE="$original_expected_worker_image"
        EXPECTED_WORKER_IMAGE_ID="$original_expected_worker_image_id"
        EXPECTED_WORKER_REVISION="$original_expected_worker_revision"
        EXPECTED_WORKER_CONFIG_SHA256="$original_expected_worker_config_sha256"
        return 1
    fi

    ACTION="$original_action"
    DATA_PROFILE="$original_data_profile"
    REQUIRE_RUN_ONCE="$original_require_run_once"
    compose_json="$original_compose_json"
    EXPECTED_WORKER_IMAGE="$original_expected_worker_image"
    EXPECTED_WORKER_IMAGE_ID="$original_expected_worker_image_id"
    EXPECTED_WORKER_REVISION="$original_expected_worker_revision"
    EXPECTED_WORKER_CONFIG_SHA256="$original_expected_worker_config_sha256"
}

run_active_resume_recovery() {
    local continuous_binding runonce_compose_json runonce_binding
    local -a continuous_binding_lines runonce_binding_lines
    local -a worker_image_identity
    local continuous_worker_image continuous_worker_config_sha256
    local runonce_worker_image runonce_worker_config_sha256
    local expected_worker_image_id expected_worker_revision

    if ! mapfile -t continuous_binding_lines < <(
        compose_json_worker_binding "$compose_json"
    )
    then
        printf 'ERROR: recovery could not resolve the continuous worker binding\n' >&2
        return 1
    fi
    continuous_worker_image="${continuous_binding_lines[0]:-}"
    continuous_worker_config_sha256="${continuous_binding_lines[1]:-}"
    if [[ -z "$continuous_worker_image" \
        || ! "$continuous_worker_config_sha256" =~ ^[0-9a-f]{64}$ ]]
    then
        printf 'ERROR: recovery could not resolve the continuous worker binding\n' >&2
        return 1
    fi
    if [[ ! "$continuous_worker_image" =~ @sha256:[0-9a-f]{64}$ ]]; then
        printf 'ERROR: recovery active candidate requires an immutable digest worker image reference\n' >&2
        return 1
    fi
    if ! mapfile -t worker_image_identity < <(
        resolve_worker_image_binding "$continuous_worker_image"
    )
    then
        return 1
    fi
    expected_worker_image_id="${worker_image_identity[0]:-}"
    expected_worker_revision="${worker_image_identity[1]:-}"
    if [[ ! "$expected_worker_image_id" =~ ^sha256:[0-9a-f]{64}$ \
        || ! "$expected_worker_revision" =~ ^[0-9a-f]{40}$ ]]
    then
        printf 'ERROR: recovery could not resolve the exact worker image identity\n' >&2
        return 1
    fi

    if ! runonce_compose_json="$(
        build_active_resume_compose_json \
            "$compose_json" \
            "$recovery_active_scrape_id" \
            "$continuous_worker_image"
    )"
    then
        return 1
    fi
    if ! mapfile -t runonce_binding_lines < <(
        compose_json_worker_binding "$runonce_compose_json"
    )
    then
        printf 'ERROR: recovery could not resolve the run-once scrape-resume binding\n' >&2
        return 1
    fi
    runonce_worker_image="${runonce_binding_lines[0]:-}"
    runonce_worker_config_sha256="${runonce_binding_lines[1]:-}"
    if [[ "$runonce_worker_image" != "$continuous_worker_image" ]]; then
        printf 'ERROR: recovery run-once worker image must match the continuous worker image\n' >&2
        return 1
    fi
    if [[ ! "$runonce_worker_config_sha256" =~ ^[0-9a-f]{64}$ ]]; then
        printf 'ERROR: recovery could not resolve the run-once scrape-resume binding\n' >&2
        return 1
    fi
    if ! validate_scrape_resume_worker_binding \
        "$runonce_compose_json" \
        "$recovery_active_scrape_id" \
        "$continuous_worker_image" \
        "recovery run-once worker"
    then
        return 1
    fi

    if ! validate_active_resume_state \
        "$recovery_active_scrape_id" \
        "$recovery_published_scrape_id"
    then
        return 1
    fi

    printf 'compose_guard recovery=active-candidate scrape=%s published=%s mode=scrape-resume\n' \
        "$recovery_active_scrape_id" \
        "$recovery_published_scrape_id"
    if ! start_runonce_worker_with_existing_lock \
        "$runonce_compose_json" \
        "$runonce_worker_image" \
        "$expected_worker_image_id" \
        "$expected_worker_revision" \
        "$runonce_worker_config_sha256"
    then
        printf 'ERROR: active recovery could not start the scrape-resume worker\n' >&2
        return 1
    fi

    while true; do
        local worker_state worker_runtime_status

        if ! enforce_recovery_total_deadline; then
            printf 'ERROR: active recovery exceeded its total deadline before publication convergence\n' >&2
            return 1
        fi
        if ! read_recovery_service_snapshot true; then
            return 1
        fi
        if ! recovery_active_resume_state_json="$(
            load_active_resume_state_json "$recovery_active_scrape_id"
        )"
        then
            printf 'ERROR: active recovery could not refresh the durable candidate state\n' >&2
            return 1
        fi
        if [[ -z "$recovery_active_resume_state_json" ]]; then
            printf 'ERROR: active recovery candidate scrape is missing\n' >&2
            return 1
        fi

        durable_status=0
        if durable_message="$(
            python3 -c '
import json
import sys

expected_scrape_id = int(sys.argv[1])
state = json.load(sys.stdin)
status = state.get("status")
if status == "failed":
    raise SystemExit("ERROR: active recovery candidate recorded a durable failure")
if status == "completed":
    notifications_scrape_id = state.get("improvementNotificationsScrapeId")
    notifications_status = state.get("improvementNotificationsStatus")
    if notifications_scrape_id == expected_scrape_id and notifications_status in {"pending", "running", "failed"}:
        raise SystemExit("ERROR: active recovery candidate still requires improvement notification recovery")
    raise SystemExit(0)
if status != "running":
    raise SystemExit("ERROR: active recovery candidate entered an unexpected durable state")
raise SystemExit(3)
' "$recovery_active_scrape_id" \
                <<< "$recovery_active_resume_state_json" 2>&1
        )"
        then
            durable_status=0
        else
            durable_status=$?
        fi
        case "$durable_status" in
            0)
                ;;
            3)
                ;;
            *)
                if [[ -n "$durable_message" ]]; then
                    printf '%s\n' "$durable_message" >&2
                fi
                return 1
                ;;
        esac

        worker_state="$(inspect_container_state "$worker_container")"
        worker_runtime_status="${worker_state%%|*}"
        if [[ "$worker_runtime_status" == "running" \
            || "$worker_runtime_status" == "restarting" \
            || "$worker_runtime_status" == "paused" ]]
        then
            if ! worker_identity_matches_binding \
                "$runonce_worker_image" \
                "$expected_worker_image_id" \
                "$expected_worker_revision"
            then
                printf 'ERROR: active recovery worker identity drifted during publication recovery\n' >&2
                return 1
            fi
        fi

        if ((durable_status == 0)) \
            && [[ "$recovery_current_update_status" == "idle" ]] \
            && [[ "$recovery_public_reads_frozen" == "false" ]] \
            && [[ "$recovery_published_scrape_id" == "$recovery_active_scrape_id" ]] \
            && [[ "$worker_runtime_status" != "running" ]] \
            && [[ "$worker_runtime_status" != "restarting" ]] \
            && [[ "$worker_runtime_status" != "paused" ]]
        then
            break
        fi

        if [[ "$recovery_current_update_status" == "idle" \
            && "$recovery_public_reads_frozen" == "false" \
            && "$recovery_published_scrape_id" != "$recovery_active_scrape_id" ]]
        then
            printf 'ERROR: active recovery state drifted before the candidate became the published scrape\n' >&2
            return 1
        fi
        if [[ "$recovery_current_update_status" != "updating" \
            && "$recovery_current_update_status" != "stalled" \
            && "$recovery_current_update_status" != "idle" ]]
        then
            printf 'ERROR: active recovery current update state drifted unexpectedly\n' >&2
            return 1
        fi
        if [[ "$recovery_public_reads_frozen" == "true" \
            && "$recovery_freeze_reason" != "post-process" ]]
        then
            printf 'ERROR: active recovery freeze state drifted away from post-process\n' >&2
            return 1
        fi

        sleep_until_deadline "$RECOVERY_TOTAL_DEADLINE_AT"
    done

    if ! enforce_recovery_total_deadline; then
        return 1
    fi
    if ! core_is_ready; then
        printf 'ERROR: core readiness was lost before continuous worker restart\n' >&2
        return 1
    fi
    if ! capture_recovery_safety_snapshot; then
        return 1
    fi
    if ! run_proxy_recovery_sequence; then
        return 1
    fi

    EXPECTED_WORKER_IMAGE="$continuous_worker_image"
    EXPECTED_WORKER_IMAGE_ID="$expected_worker_image_id"
    EXPECTED_WORKER_REVISION="$expected_worker_revision"
    EXPECTED_WORKER_CONFIG_SHA256="$continuous_worker_config_sha256"
    verify_expected_worker_image_object
    start_continuous_worker_after_preflight
}

stop_recovery_worker() {
    local state status

    state="$(inspect_container_state "$worker_container")"
    status="${state%%|*}"
    case "$status" in
        running|restarting|paused)
            if ! docker stop --time "$RECOVERY_WORKER_STOP_TIMEOUT_SECONDS" \
                "$worker_container" >/dev/null 2>&1
            then
                return 1
            fi
            ;;
    esac

    state="$(inspect_container_state "$worker_container")"
    status="${state%%|*}"
    [[ "$status" != "running" && "$status" != "restarting" && "$status" != "paused" ]]
}

worker_operational_state_allows_stop() {
    local service_info

    if ! service_info="$(
        docker exec "$service_container" \
            curl -fsS --connect-timeout 2 --max-time 10 \
            http://localhost:8080/api/service-info 2>/dev/null
    )"
    then
        return 2
    fi

    python3 -c '
import json
import sys

try:
    payload = json.load(sys.stdin)
except (json.JSONDecodeError, TypeError):
    raise SystemExit(2)

current = payload.get("currentUpdate")
publication = payload.get("publication")
if not isinstance(current, dict) or not isinstance(publication, dict):
    raise SystemExit(2)
status = current.get("status")
frozen = publication.get("publicReadsFrozen")
if not isinstance(status, str) or not isinstance(frozen, bool):
    raise SystemExit(2)
raise SystemExit(
    0 if status in {"idle", "failed"} and frozen is False else 3)
' <<< "$service_info" >/dev/null 2>&1
}

recovery_worker_start_attempted=0
recovery_worker_accepted=0

cleanup_unaccepted_direct_worker() {
    local status=$?
    local container_id="$CREATED_WORKER_CONTAINER_ID"

    trap - EXIT INT TERM
    if ((WORKER_CREATE_ATTEMPTED != 0 && DIRECT_WORKER_START_ACCEPTED == 0)); then
        if [[ ! "$container_id" =~ ^[0-9a-f]{64}$ ]]; then
            container_id="$(
                docker inspect --format '{{.Id}}' fstworker 2>/dev/null || true
            )"
            if [[ "$container_id" == "$PREVIOUS_WORKER_CONTAINER_ID" ]]; then
                container_id=""
            fi
        fi
        remove_created_worker "$container_id"
    fi
    exit "$status"
}

exit_direct_from_signal() {
    local status="$1"

    trap '' INT TERM
    exit "$status"
}

cleanup_unaccepted_recovery_worker() {
    local status=$?
    local state runtime_status stop_safety_status

    trap - EXIT INT TERM
    if ((recovery_worker_start_attempted != 0 && recovery_worker_accepted == 0)); then
        state="$(inspect_container_state "$worker_container")"
        runtime_status="${state%%|*}"
        case "$runtime_status" in
            running|restarting|paused)
                stop_safety_status=0
                worker_operational_state_allows_stop || stop_safety_status=$?
                case "$stop_safety_status" in
                    0)
                        if ! stop_recovery_worker; then
                            printf 'ERROR: fstworker could not be returned to a stopped state\n' >&2
                        fi
                        ;;
                    3)
                        printf '%s\n' \
                            'ERROR: fstworker startup did not converge after operational work began or public reads froze; leaving the worker running. Use tools/fst-worker-no-progress-watchdog.mjs and docs/operations/live-safety.md.' \
                            >&2
                        ;;
                    *)
                        printf '%s\n' \
                            'ERROR: fstworker cleanup could not verify idle and unfrozen operational state; leaving the worker running. Use tools/fst-worker-no-progress-watchdog.mjs and docs/operations/live-safety.md.' \
                            >&2
                        ;;
                esac
                ;;
        esac
    fi
    exit "$status"
}

exit_recovery_from_signal() {
    local status="$1"

    trap '' INT TERM
    exit "$status"
}

if [[ "$ACTION" =~ ^(recreate|recreate-runonce)$ ]]; then
    trap cleanup_unaccepted_direct_worker EXIT
    trap 'exit_direct_from_signal 130' INT
    trap 'exit_direct_from_signal 143' TERM
fi

if [[ "$ACTION" == "recover-start" ]]; then
    trap cleanup_unaccepted_recovery_worker EXIT
    trap 'exit_recovery_from_signal 130' INT
    trap 'exit_recovery_from_signal 143' TERM
    RECOVERY_TOTAL_DEADLINE_AT=$((SECONDS + RECOVERY_TOTAL_DEADLINE_SECONDS))

    printf 'compose_guard config=ok overlay=%s throughput_profile=%s effective_set=validated canonical_set=validated run_once=false\n' \
        "$(basename "$pia_overlay")" "$throughput_profile"

    printf 'compose_guard recovery=core-wait\n'
    core_wait_status=0
    wait_for_core_ready || core_wait_status=$?
    if ((core_wait_status == 2)); then
        exit 1
    fi
    if ((core_wait_status != 0)); then
        printf 'ERROR: postgres and fstservice did not become healthy and ready\n' >&2
        exit 1
    fi
    if ! enforce_recovery_total_deadline; then
        exit 1
    fi
    if ! verify_acquisition_checkpoint_schema; then
        exit 1
    fi
    printf 'compose_guard recovery=schema acquisition-checkpoint=ready\n'
    if ! determine_recovery_boot_mode; then
        exit 1
    fi
    if ! enforce_recovery_total_deadline; then
        exit 1
    fi
    case "$recovery_boot_mode" in
        continuous-idle)
            printf 'compose_guard recovery=preflight core=ready worker=stopped update=%s reads=unfrozen\n' \
                "$recovery_current_update_status"
            if ! run_proxy_recovery_sequence; then
                exit 1
            fi
            ;;
        continuous-deferred)
            printf 'compose_guard recovery=preflight core=ready worker=stopped update=idle reads=frozen deferred-publication=%s\n' \
                "${recovery_deferred_publication_id:-unknown}"
            if ! run_proxy_recovery_sequence; then
                exit 1
            fi
            ;;
        active-resume)
            printf 'compose_guard recovery=preflight core=ready worker=stopped update=%s reads=frozen scrape=%s\n' \
                "$recovery_current_update_status" \
                "$recovery_active_scrape_id"
            ;;
        *)
            printf 'ERROR: internal recovery boot mode is invalid\n' >&2
            exit 1
            ;;
    esac
else
    printf 'compose_guard config=ok overlay=%s throughput_profile=%s data_profile=%s effective=%s canonical=%s max_rps=%s per_endpoint_rps=%s per_endpoint_concurrency=%s connection_reuse=disabled transport=curl run_once=%s\n' \
        "$(basename "$pia_overlay")" "$throughput_profile" "$data_profile" "$expected_count" "$canonical_count" "$max_rps" "$per_endpoint_rps" "$per_endpoint_concurrency" "$run_once"
fi

if $RUNTIME_PROBES; then
    if [[ "$ACTION" == "recover-start" ]] \
        && ! enforce_recovery_total_deadline
    then
        exit 1
    fi
    if ! docker inspect fstservice >/dev/null 2>&1; then
        printf 'ERROR: fstservice container is required for isolated proxy probes\n' >&2
        exit 1
    fi

    direct_output="$(
        docker exec fstservice curl -sS --connect-timeout 3 --max-time 10 \
            https://api.ipify.org 2>/dev/null || true
    )"
    direct_hash="$(
        python3 -c '
import hashlib
import ipaddress
import sys
value = sys.stdin.read().strip()
try:
    ipaddress.ip_address(value)
except ValueError:
    raise SystemExit(1)
print(hashlib.sha256(value.encode()).hexdigest())
' <<< "$direct_output"
    )" || {
        printf 'ERROR: direct egress probe did not return an address\n' >&2
        exit 1
    }
    if [[ "$ACTION" == "recover-start" ]] \
        && ! enforce_recovery_total_deadline
    then
        exit 1
    fi

    declare -A egress_owner=()
    for node in "${effective_nodes[@]}"; do
        if [[ "$ACTION" == "recover-start" ]] \
            && ! enforce_recovery_total_deadline
        then
            exit 1
        fi
        state="$(
            docker inspect --format \
                '{{.State.Status}}|{{if .State.Health}}{{.State.Health.Status}}{{else}}none{{end}}' \
                "$node"
        )"
        if [[ "$state" != "running|healthy" ]]; then
            printf 'ERROR: effective proxy %s is not running and healthy (%s)\n' \
                "$node" "$state" >&2
            exit 1
        fi

        dns_probe_status=0
        retry_probe 6 5 docker exec "$node" sh -c \
            'getent hosts account-public-service-prod.ol.epicgames.com >/dev/null 2>&1 || nslookup account-public-service-prod.ol.epicgames.com >/dev/null 2>&1' \
            || dns_probe_status=$?
        if ((dns_probe_status == 2)); then
            exit 1
        fi
        if ((dns_probe_status != 0)); then
            printf 'ERROR: Epic DNS lookup failed in %s\n' "$node" >&2
            exit 1
        fi

        control_status=""
        for attempt in 1 2 3 4 5 6; do
            if [[ "$ACTION" == "recover-start" ]] \
                && ! enforce_recovery_total_deadline
            then
                exit 1
            fi
            control_status="$(
                docker exec fstservice curl -fsS --connect-timeout 2 --max-time 5 \
                    "http://$node:8000/v1/vpn/status" 2>/dev/null \
                    | python3 -c 'import json,sys; print(json.load(sys.stdin).get("status", ""))' \
                    || true
            )"
            [[ "$control_status" == "running" ]] && break
            if ((attempt < 6)); then
                sleep_until_deadline "$((SECONDS + 5))" 5
            fi
        done
        if [[ "$ACTION" == "recover-start" ]] \
            && ! enforce_recovery_total_deadline
        then
            exit 1
        fi
        if [[ "$control_status" != "running" ]]; then
            printf 'ERROR: control API is not running for %s\n' "$node" >&2
            exit 1
        fi

        egress_output=""
        for attempt in 1 2 3 4 5 6; do
            if [[ "$ACTION" == "recover-start" ]] \
                && ! enforce_recovery_total_deadline
            then
                exit 1
            fi
            egress_output="$(
                docker exec fstservice curl -sS --connect-timeout 3 --max-time 10 \
                    -x "http://$node:8888" https://api.ipify.org 2>/dev/null || true
            )"
            if python3 -c '
import ipaddress
import sys
ipaddress.ip_address(sys.stdin.read().strip())
' <<< "$egress_output" 2>/dev/null
            then
                break
            fi
            if ((attempt < 6)); then
                sleep_until_deadline "$((SECONDS + 5))" 5
            fi
        done
        if [[ "$ACTION" == "recover-start" ]] \
            && ! enforce_recovery_total_deadline
        then
            exit 1
        fi
        egress_hash="$(
            python3 -c '
import hashlib
import ipaddress
import sys
value = sys.stdin.read().strip()
try:
    ipaddress.ip_address(value)
except ValueError:
    raise SystemExit(1)
print(hashlib.sha256(value.encode()).hexdigest())
' <<< "$egress_output"
        )" || {
            printf 'ERROR: HTTP proxy probe failed for %s\n' "$node" >&2
            exit 1
        }

        if [[ "$egress_hash" == "$direct_hash" ]]; then
            printf 'ERROR: %s did not use a distinct VPN egress\n' "$node" >&2
            exit 1
        fi
        if [[ -n "${egress_owner[$egress_hash]:-}" ]]; then
            printf 'ERROR: duplicate egress detected for %s and %s\n' \
                "${egress_owner[$egress_hash]}" "$node" >&2
            exit 1
        fi
        egress_owner[$egress_hash]="$node"
    done

    if [[ "$ACTION" == "recover-start" ]] \
        && ! enforce_recovery_total_deadline
    then
        exit 1
    fi

    if [[ "$ACTION" == "recover-start" ]]; then
        printf 'compose_guard runtime=ok effective_set=qualified dns=ok control=ok egress=distinct\n'
    else
        printf 'compose_guard runtime=ok healthy=%s unique_egress=%s dns=%s control=%s\n' \
            "$expected_count" "${#egress_owner[@]}" "$expected_count" "$expected_count"
    fi
fi

if $RUNTIME_PROBES && [[ "$DATA_PROFILE" == "scrape-resume" ]]; then
    if ! validate_scrape_resume_runtime_state; then
        exit 1
    fi
fi

if $RUNTIME_PROBES && [[ "$DATA_PROFILE" == "wire-send-telemetry" ]]; then
    if ! verify_wire_send_telemetry_schema; then
        exit 1
    fi
    printf 'compose_guard schema=wire-send-telemetry-ready\n'
fi

case "$ACTION" in
    check|check-runonce)
        ;;
    recreate)
        verify_inherited_worker_mutation_lock
        verify_expected_worker_image_object
        create_and_start_worker
        ;;
    recreate-runonce)
        verify_inherited_worker_mutation_lock
        verify_expected_worker_image_object
        create_and_start_worker
        ;;
    recover-start)
        if ! enforce_recovery_total_deadline; then
            exit 1
        fi
        if ! core_is_ready; then
            printf 'ERROR: core readiness was lost before worker startup\n' >&2
            exit 1
        fi
        case "$recovery_boot_mode" in
            active-resume)
                if ! run_active_resume_recovery; then
                    exit 1
                fi
                ;;
            continuous-idle|continuous-deferred)
                if [[ "$recovery_boot_mode" == "continuous-idle" ]]; then
                    if ! capture_recovery_safety_snapshot; then
                        exit 1
                    fi
                else
                    recovery_baseline_worker_instance="$recovery_worker_api_instance"
                    recovery_baseline_worker_heartbeat="$recovery_worker_api_heartbeat"
                fi
                if ! start_continuous_worker_after_preflight; then
                    exit 1
                fi
                ;;
            *)
                printf 'ERROR: internal recovery boot mode is invalid\n' >&2
                exit 1
                ;;
        esac
        ;;
esac
