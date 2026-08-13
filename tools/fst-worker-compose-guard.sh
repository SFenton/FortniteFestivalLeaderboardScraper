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
WORKER_MUTATION_LOCK_PATH="${FST_WORKER_COMPOSE_GUARD_LOCK_PATH:-}"
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
                             legacy-reader-migration
                           Every run-once config requires a data profile.
  --expected-worker-image I
                           Require the resolved fstworker image to match I.
                           Required whenever --data-profile is not none.
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
    none|notification-db-only|publication-cache-generation|registered-refresh-repair|catalog-path-notification-source-cut|snapshot-reuse|legacy-reader-migration)
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

if [[ "$DATA_PROFILE" != "none" && -z "$EXPECTED_WORKER_IMAGE" ]]; then
    printf 'ERROR: --expected-worker-image is required with --data-profile\n' >&2
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

    if ! exec 9>>"$WORKER_MUTATION_LOCK_PATH"; then
        printf 'ERROR: worker mutation lock file could not be opened\n' >&2
        exit 1
    fi
    if ! flock -n 9; then
        printf 'ERROR: another fstworker start/recreate action is already running\n' >&2
        exit 1
    fi
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

if [[ "$(basename "$pia_overlay")" != "docker-compose.pia-30.yml" ]]; then
    printf 'ERROR: canonical PIA overlay must be docker-compose.pia-30.yml\n' >&2
    exit 1
fi
for file in "$base_file" "$pia_overlay"; do
    if [[ ! -f "$file" ]]; then
        printf 'ERROR: required compose file not found: %s\n' "$file" >&2
        exit 1
    fi
done
if [[ "$ACTION" =~ ^(check-runonce|recreate-runonce)$ && ! -f "$runonce_overlay" ]]; then
    printf 'ERROR: run-once overlay not found: %s\n' "$runonce_overlay" >&2
    exit 1
fi

if [[ "$ACTION" =~ ^(check-runonce|recreate-runonce)$ ]]; then
    runonce_restart="$(
        cd "$compose_dir"
        docker compose --profile worker \
            -f "$base_file" -f "$pia_overlay" -f "$runonce_overlay" \
            config --format json \
            | python3 -c 'import json,sys; print((json.load(sys.stdin).get("services", {}).get("fstworker", {}).get("restart") or "").strip())'
    )"
    if [[ "$runonce_restart" != "no" ]]; then
        printf 'ERROR: run-once worker restart policy must resolve to no, found: %s\n' \
            "${runonce_restart:-<empty>}" >&2
        exit 1
    fi
fi

if [[ "$ACTION" =~ ^(check-runonce|recreate-runonce)$ ]]; then
    compose_json="$(
        cd "$compose_dir"
        docker compose --profile worker \
            -f "$base_file" -f "$pia_overlay" -f "$runonce_overlay" \
            config --format json
    )"
    REQUIRE_RUN_ONCE=true
else
    compose_json="$(
        cd "$compose_dir"
        docker compose --profile worker \
            -f "$base_file" -f "$pia_overlay" config --format json
    )"
    REQUIRE_RUN_ONCE=false
fi

validation="$(
    python3 -c '
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
action = sys.argv[9]
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
    for name in (
        "Scraper__RegisteredPlayerBandDiscoveryMaxLookupsPerPass",
        "Scraper__RegisteredBandProcessingMaxLookupsPerPass",
    ):
        if nonnegative_integer(name) != 80:
            raise SystemExit(
                f"ERROR: data profile notification-db-only requires {name}=80")
if data_profile != "none":
    actual_worker_image = str(worker.get("image") or "").strip()
    if actual_worker_image != expected_worker_image:
        display_actual = actual_worker_image if actual_worker_image else "<empty>"
        raise SystemExit(
            f"ERROR: data profile {data_profile} requires worker image "
            f"{expected_worker_image}, found {display_actual}")

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
if data_profile == "catalog-path-notification-source-cut":
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
                "ERROR: data profile catalog-path-notification-source-cut "
                f"requires {name}=true")
    for name in (
        "Features__UseStoredSoloProjectionRanksForFilteredReads",
        "Features__SkipUnchangedPhysicalLeaderboardSnapshots",
    ):
        if boolean(name):
            raise SystemExit(
                "ERROR: data profile catalog-path-notification-source-cut "
                f"requires {name}=false")
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
        "$EXPECTED_WORKER_IMAGE" "$ACTION" \
        <<< "$compose_json"
)"

summary="$(head -n 1 <<< "$validation")"
IFS='|' read -r _ throughput_profile data_profile expected_count canonical_count max_rps per_endpoint_rps per_endpoint_concurrency connection_reuse_disabled curl_transport_enabled run_once <<< "$summary"
mapfile -t effective_nodes < <(sed -n 's/^NODE|//p' <<< "$validation")
postgres_container="$(sed -n 's/^CORE|postgres|//p' <<< "$validation")"
service_container="$(sed -n 's/^CORE|fstservice|//p' <<< "$validation")"
worker_container="$(sed -n 's/^CORE|fstworker|//p' <<< "$validation")"

if [[ "${#effective_nodes[@]}" -ne "$expected_count" ]]; then
    printf 'ERROR: internal guard node-count mismatch\n' >&2
    exit 1
fi

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

recovery_baseline_worker_instance=""
recovery_baseline_worker_heartbeat=""

capture_recovery_safety_snapshot() {
    local worker_state worker_runtime_status service_info snapshot

    worker_state="$(inspect_container_state "$worker_container")"
    worker_runtime_status="${worker_state%%|*}"
    case "$worker_runtime_status" in
        missing|created|exited|dead)
            ;;
        *)
            printf 'ERROR: recovery requires fstworker to be stopped or absent\n' >&2
            return 1
            ;;
    esac

    if ! service_info="$(
        docker exec "$service_container" \
            curl -fsS --connect-timeout 2 --max-time 10 \
            http://localhost:8080/api/service-info 2>/dev/null
    )"
    then
        printf 'ERROR: recovery could not read the operational service state\n' >&2
        return 1
    fi

    if ! snapshot="$(
        python3 -c '
import json
import sys

try:
    payload = json.load(sys.stdin)
except (json.JSONDecodeError, TypeError):
    raise SystemExit(
        "ERROR: recovery received an invalid operational service response")

current = payload.get("currentUpdate")
if not isinstance(current, dict) or current.get("status") != "idle":
    raise SystemExit(
        "ERROR: recovery requires the current update state to be idle")

publication = payload.get("publication")
if not isinstance(publication, dict) or publication.get("publicReadsFrozen") is not False:
    raise SystemExit(
        "ERROR: recovery requires public reads to be unfrozen")

worker = payload.get("workerStatus")
if not isinstance(worker, dict):
    worker = {}
instance = worker.get("instanceId")
heartbeat = worker.get("lastHeartbeatAt")
print(instance if isinstance(instance, str) else "")
print(heartbeat if isinstance(heartbeat, str) else "")
' <<< "$service_info"
    )"
    then
        return 1
    fi

    recovery_baseline_worker_instance="${snapshot%%$'\n'*}"
    if [[ "$snapshot" == *$'\n'* ]]; then
        recovery_baseline_worker_heartbeat="${snapshot#*$'\n'}"
    else
        recovery_baseline_worker_heartbeat=""
    fi
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
        cd "$compose_dir"
        docker compose -f "$base_file" -f "$pia_overlay" \
            up -d --no-deps --force-recreate \
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
raise SystemExit(0 if status == "idle" and frozen is False else 3)
' <<< "$service_info" >/dev/null 2>&1
}

recovery_worker_start_attempted=0
recovery_worker_accepted=0

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
    if ! capture_recovery_safety_snapshot; then
        exit 1
    fi
    if ! enforce_recovery_total_deadline; then
        exit 1
    fi
    printf 'compose_guard recovery=preflight core=ready worker=stopped update=idle reads=unfrozen\n'

    printf 'compose_guard recovery=proxy-wait phase=initial\n'
    initial_wait_status=0
    wait_for_effective_proxy_health "$RECOVERY_INITIAL_WAIT_SECONDS" \
        || initial_wait_status=$?
    if ((initial_wait_status == 0)); then
        printf 'compose_guard recovery=proxy-convergence phase=initial status=healthy\n'
    elif ((initial_wait_status == 2)); then
        exit 1
    else
        printf 'compose_guard recovery=proxy-convergence phase=initial unhealthy=%s\n' \
            "${#unhealthy_effective_nodes[@]}"
        if ! core_is_ready; then
            printf 'ERROR: core readiness was lost before proxy recovery\n' >&2
            exit 1
        fi
        if ! enforce_recovery_total_deadline; then
            exit 1
        fi
        if ! capture_recovery_safety_snapshot; then
            exit 1
        fi
        if ! enforce_recovery_total_deadline; then
            exit 1
        fi
        recreate_status=0
        recreate_unhealthy_effective_proxies || recreate_status=$?
        if ((recreate_status == 2)); then
            exit 1
        fi
        if ((recreate_status != 0)); then
            exit 1
        fi

        printf 'compose_guard recovery=proxy-wait phase=post-recreate\n'
        recreate_wait_status=0
        wait_for_effective_proxy_health "$RECOVERY_RECREATE_WAIT_SECONDS" \
            || recreate_wait_status=$?
        if ((recreate_wait_status == 2)); then
            exit 1
        fi
        if ((recreate_wait_status != 0)); then
            printf 'ERROR: effective proxies did not become healthy after bounded recovery\n' >&2
            exit 1
        fi
        printf 'compose_guard recovery=proxy-convergence phase=post-recreate status=healthy\n'
    fi
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

case "$ACTION" in
    check|check-runonce)
        ;;
    recreate)
        cd "$compose_dir"
        docker compose --profile worker -f "$base_file" -f "$pia_overlay" \
            up -d --no-deps --force-recreate fstworker
        ;;
    recreate-runonce)
        cd "$compose_dir"
        docker compose --profile worker \
            -f "$base_file" -f "$pia_overlay" -f "$runonce_overlay" \
            up -d --no-deps --force-recreate fstworker
        ;;
    recover-start)
        if ! enforce_recovery_total_deadline; then
            exit 1
        fi
        if ! core_is_ready; then
            printf 'ERROR: core readiness was lost before worker startup\n' >&2
            exit 1
        fi
        if ! enforce_recovery_total_deadline; then
            exit 1
        fi
        if ! capture_recovery_safety_snapshot; then
            exit 1
        fi
        if ! enforce_recovery_total_deadline; then
            exit 1
        fi

        printf 'compose_guard recovery=worker-start service=fstworker mode=continuous\n'
        recovery_worker_start_attempted=1
        if ! (
            cd "$compose_dir"
            docker compose --profile worker -f "$base_file" -f "$pia_overlay" \
                up -d --no-deps --force-recreate fstworker \
                >/dev/null 2>&1
        )
        then
            printf 'ERROR: fstworker recreate/start failed\n' >&2
            exit 1
        fi
        if ! enforce_recovery_total_deadline; then
            exit 1
        fi

        printf 'compose_guard recovery=worker-wait\n'
        worker_wait_status=0
        wait_for_worker_recovery_ready || worker_wait_status=$?
        if ((worker_wait_status == 2)); then
            exit 1
        fi
        if ((worker_wait_status != 0)); then
            printf 'ERROR: fstworker health and fresh heartbeat did not converge\n' >&2
            exit 1
        fi
        if ! enforce_recovery_total_deadline; then
            exit 1
        fi

        recovery_worker_accepted=1
        printf 'compose_guard recovery=ok recreated=%s worker=online heartbeat=fresh\n' \
            "$recreated_proxy_count"
        ;;
esac
