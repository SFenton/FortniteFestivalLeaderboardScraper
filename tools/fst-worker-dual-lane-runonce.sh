#!/usr/bin/env bash
set -euo pipefail

COMPOSE_DIR="${COMPOSE_DIR:-/home/sfenton/Docker/FestivalServiceTracker}"
NETWORK_PROFILE=""
EXPECTED_WORKER_IMAGE="${EXPECTED_WORKER_IMAGE:-}"
ACTION="check"
CONFIG_ONLY=false

usage() {
    cat <<'EOF'
Usage: tools/fst-worker-dual-lane-runonce.sh --network-profile PROFILE [options]

Selects and validates the complete two-candidate scrape card for the current
notification DB-only data lane, then optionally starts exactly one worker pass.

Options:
  --network-profile P   candidate-800-32-4, candidate-1600-64-8,
                        or candidate-2880-128-16
  --expected-worker-image I
                        Exact fstworker image required by the data lane
  --check               Validate only (default)
  --recreate            Validate and recreate the run-once worker
  --config-only         Skip live proxy probes (check only)
  --compose-dir DIR     Production compose directory
  -h, --help            Show help
EOF
}

while [[ $# -gt 0 ]]; do
    case "$1" in
        --network-profile) NETWORK_PROFILE="$2"; shift 2 ;;
        --expected-worker-image) EXPECTED_WORKER_IMAGE="$2"; shift 2 ;;
        --check) ACTION="check"; shift ;;
        --recreate) ACTION="recreate"; shift ;;
        --config-only) CONFIG_ONLY=true; shift ;;
        --compose-dir) COMPOSE_DIR="$2"; shift 2 ;;
        -h|--help) usage; exit 0 ;;
        *) printf 'ERROR: unknown option: %s\n' "$1" >&2; usage >&2; exit 64 ;;
    esac
done

if [[ -z "$NETWORK_PROFILE" ]]; then
    printf 'ERROR: --network-profile is required\n' >&2
    usage >&2
    exit 64
fi
if [[ -z "$EXPECTED_WORKER_IMAGE" ]]; then
    printf 'ERROR: --expected-worker-image is required\n' >&2
    usage >&2
    exit 64
fi
if $CONFIG_ONLY && [[ "$ACTION" != "check" ]]; then
    printf 'ERROR: --config-only cannot be used with --recreate\n' >&2
    exit 64
fi

case "$NETWORK_PROFILE" in
    candidate-800-32-4)
        MAX_RPS=800
        PER_ENDPOINT_RPS=32
        PER_ENDPOINT_CONCURRENCY=4
        INITIAL_DOP=50
        ;;
    candidate-1600-64-8)
        MAX_RPS=1600
        PER_ENDPOINT_RPS=64
        PER_ENDPOINT_CONCURRENCY=8
        INITIAL_DOP=50
        ;;
    candidate-2880-128-16)
        MAX_RPS=2880
        PER_ENDPOINT_RPS=128
        PER_ENDPOINT_CONCURRENCY=16
        INITIAL_DOP=50
        ;;
    *)
        printf 'ERROR: unsupported network profile: %s\n' "$NETWORK_PROFILE" >&2
        usage >&2
        exit 64
        ;;
esac

guard_action="--check-runonce"
if [[ "$ACTION" == "recreate" ]]; then
    guard_action="--recreate-runonce"
fi

guard_args=(
    --compose-dir "$COMPOSE_DIR"
    --throughput-profile "$NETWORK_PROFILE"
    --data-profile catalog-path-notification-source-cut
    --expected-worker-image "$EXPECTED_WORKER_IMAGE"
    "$guard_action"
)
if $CONFIG_ONLY; then
    guard_args+=(--config-only)
fi

RUN_ONCE=true \
ENABLED_PHASES=All \
PIA_MAX_REQUESTS_PER_SECOND="$MAX_RPS" \
PIA_PROXY_MAX_REQUESTS_PER_SECOND_PER_ENDPOINT="$PER_ENDPOINT_RPS" \
PIA_PROXY_MAX_CONCURRENT_REQUESTS_PER_ENDPOINT="$PER_ENDPOINT_CONCURRENCY" \
PIA_INITIAL_DOP="$INITIAL_DOP" \
PIA_DEGREE_OF_PARALLELISM=200 \
PIA_PAGE_CONCURRENCY=50 \
ENABLE_AUTOMATIC_PATH_GENERATION=false \
REGISTERED_USER_REFRESH_TIMEOUT=00:00:00 \
REGISTERED_PLAYER_BAND_DISCOVERY_TIMEOUT=00:05:00 \
REGISTERED_BAND_TARGETED_PROCESSING_TIMEOUT=00:05:00 \
REGISTERED_PLAYER_BAND_DISCOVERY_MAX_LOOKUPS_PER_PASS=80 \
REGISTERED_BAND_PROCESSING_MAX_LOOKUPS_PER_PASS=80 \
IMPROVEMENT_NOTIFICATIONS_ENABLED=true \
IMPROVEMENT_NOTIFICATIONS_SCOPE=registered \
IMPROVEMENT_NOTIFICATIONS_INCLUDE_PLAYERS=true \
IMPROVEMENT_NOTIFICATIONS_INCLUDE_BANDS=true \
IMPROVEMENT_NOTIFICATIONS_INCLUDE_SONG_EVENTS=true \
IMPROVEMENT_NOTIFICATIONS_INCLUDE_RANKINGS=true \
IMPROVEMENT_NOTIFICATIONS_REFRESH_SOLO_PROJECTION=true \
IMPROVEMENT_NOTIFICATIONS_REFRESH_ALL_SOLO_SCOPES_WHEN_NO_IMPACTED_SCOPES=false \
"$(dirname "$0")/fst-worker-compose-guard.sh" "${guard_args[@]}"
