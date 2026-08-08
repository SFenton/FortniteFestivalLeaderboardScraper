#!/usr/bin/env bash
set -euo pipefail

COMPOSE_DIR="${COMPOSE_DIR:-/home/sfenton/Docker/FestivalServiceTracker}"
BASE_FILE="${BASE_FILE:-docker-compose.yml}"
PIA_OVERLAY="${PIA_OVERLAY:-docker-compose.pia-30.yml}"
RUNONCE_OVERLAY="${RUNONCE_OVERLAY:-docker-compose.runonce.yml}"
RUNTIME_PROBES=true
ACTION="check"
THROUGHPUT_PROFILE="baseline-up-to-800-32-4"
DATA_PROFILE="none"
EXPECTED_WORKER_IMAGE="${EXPECTED_WORKER_IMAGE:-}"

usage() {
    cat <<'EOF'
Usage: tools/fst-worker-compose-guard.sh [options]

Validates the canonical production PIA overlay before any fstworker recreate.
The resolved compose config must declare the expected effective proxy arrays,
all 30 canonical PIA services, aligned provider/control/container metadata,
healthy unique egresses, and the selected fail-closed throughput profile.

Options:
  --check                  Validate only (default)
  --check-runonce          Validate the exact merged run-once config without starting
  --recreate               Validate, then recreate and start fstworker
  --recreate-runonce       Validate, then recreate fstworker with run-once overlay
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
        --check-runonce) ACTION="check-runonce"; shift ;;
        --recreate) ACTION="recreate"; shift ;;
        --recreate-runonce) ACTION="recreate-runonce"; shift ;;
        --config-only) RUNTIME_PROBES=false; shift ;;
        --throughput-profile) THROUGHPUT_PROFILE="$2"; shift 2 ;;
        --data-profile) DATA_PROFILE="$2"; shift 2 ;;
        --expected-worker-image) EXPECTED_WORKER_IMAGE="$2"; shift 2 ;;
        --compose-dir) COMPOSE_DIR="$2"; shift 2 ;;
        -h|--help) usage; exit 0 ;;
        *) printf 'ERROR: unknown option: %s\n' "$1" >&2; usage >&2; exit 64 ;;
    esac
done

if ! $RUNTIME_PROBES && [[ "$ACTION" != "check" && "$ACTION" != "check-runonce" ]]; then
    printf 'ERROR: --config-only cannot be used with a worker recreate action\n' >&2
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

if [[ "$DATA_PROFILE" != "none" && -z "$EXPECTED_WORKER_IMAGE" ]]; then
    printf 'ERROR: --expected-worker-image is required with --data-profile\n' >&2
    exit 64
fi

if [[ "$THROUGHPUT_PROFILE" == candidate-* && "$ACTION" == "recreate" ]]; then
    printf 'ERROR: candidate throughput profiles require --recreate-runonce\n' >&2
    exit 64
fi

for command in docker python3 realpath; do
    if ! command -v "$command" >/dev/null 2>&1; then
        printf 'ERROR: required command not found: %s\n' "$command" >&2
        exit 1
    fi
done

retry_probe() {
    local attempts="$1"
    local delay_seconds="$2"
    shift 2

    local attempt
    for ((attempt = 1; attempt <= attempts; attempt++)); do
        if "$@"; then
            return 0
        fi
        if ((attempt < attempts)); then
            sleep "$delay_seconds"
        fi
    done
    return 1
}

compose_dir="$(realpath -m "$COMPOSE_DIR")"
base_file="$(realpath -m "$compose_dir/$BASE_FILE")"
pia_overlay="$(realpath -m "$compose_dir/$PIA_OVERLAY")"
runonce_overlay="$(realpath -m "$compose_dir/$RUNONCE_OVERLAY")"

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
        docker compose -f "$base_file" -f "$pia_overlay" -f "$runonce_overlay" \
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
        docker compose -f "$base_file" -f "$pia_overlay" -f "$runonce_overlay" \
            config --format json
    )"
    REQUIRE_RUN_ONCE=true
else
    compose_json="$(
        cd "$compose_dir"
        docker compose -f "$base_file" -f "$pia_overlay" config --format json
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

config = json.load(sys.stdin)
services = config.get("services") or {}
worker = services.get("fstworker") or {}
environment = worker.get("environment") or {}

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
            continue
        values.append((int(suffix), str(value)))
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
    raise SystemExit(f"ERROR: canonical PIA service count must be 30, found {canonical}")
if expected > canonical:
    raise SystemExit(
        f"ERROR: effective proxy count {expected} exceeds canonical count {canonical}")
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
        raise SystemExit(
            f"ERROR: expected {expected} aligned {label}, found {len(values)}")

expected_canonical_names = {f"pia-gluetun-{index}" for index in range(1, canonical + 1)}
actual_canonical_names = {name for name in services if re.fullmatch(r"pia-gluetun-\d+", name)}
if actual_canonical_names != expected_canonical_names:
    raise SystemExit(
        f"ERROR: expected {canonical} canonical PIA services, found "
        f"{len(actual_canonical_names)}")

if len(set(containers)) != expected:
    raise SystemExit("ERROR: effective proxy container names must be unique")

for index, (proxy, control, container, provider) in enumerate(
    zip(proxies, controls, containers, providers, strict=True)
):
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
    raise SystemExit(
        f"ERROR: aggregate Epic rate {max_rps} exceeds profile "
        f"{profile_name} ceiling {profile_max_aggregate_rps}")
if per_endpoint_rps > profile_max_per_endpoint_rps:
    raise SystemExit(
        f"ERROR: per-endpoint Epic rate {per_endpoint_rps} exceeds profile "
        f"{profile_name} ceiling {profile_max_per_endpoint_rps}")
if per_endpoint_concurrency > profile_max_per_endpoint_concurrency:
    raise SystemExit(
        f"ERROR: per-endpoint concurrency {per_endpoint_concurrency} exceeds profile "
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
' "$THROUGHPUT_PROFILE" "$PROFILE_MAX_AGGREGATE_RPS" \
        "$PROFILE_MAX_PER_ENDPOINT_RPS" \
        "$PROFILE_MAX_PER_ENDPOINT_CONCURRENCY" "$PROFILE_EXACT" \
        "$REQUIRE_RUN_ONCE" "$DATA_PROFILE" \
        "$EXPECTED_WORKER_IMAGE" \
        <<< "$compose_json"
)"

summary="$(head -n 1 <<< "$validation")"
IFS='|' read -r _ throughput_profile data_profile expected_count canonical_count max_rps per_endpoint_rps per_endpoint_concurrency connection_reuse_disabled curl_transport_enabled run_once <<< "$summary"
mapfile -t effective_nodes < <(sed -n 's/^NODE|//p' <<< "$validation")

if [[ "${#effective_nodes[@]}" -ne "$expected_count" ]]; then
    printf 'ERROR: internal guard node-count mismatch\n' >&2
    exit 1
fi

printf 'compose_guard config=ok overlay=%s throughput_profile=%s data_profile=%s effective=%s canonical=%s max_rps=%s per_endpoint_rps=%s per_endpoint_concurrency=%s connection_reuse=disabled transport=curl run_once=%s\n' \
    "$(basename "$pia_overlay")" "$throughput_profile" "$data_profile" "$expected_count" "$canonical_count" "$max_rps" "$per_endpoint_rps" "$per_endpoint_concurrency" "$run_once"

if $RUNTIME_PROBES; then
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

    declare -A egress_owner=()
    for node in "${effective_nodes[@]}"; do
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

        if ! retry_probe 6 5 docker exec "$node" sh -c \
            'getent hosts account-public-service-prod.ol.epicgames.com >/dev/null 2>&1 || nslookup account-public-service-prod.ol.epicgames.com >/dev/null 2>&1'
        then
            printf 'ERROR: Epic DNS lookup failed in %s\n' "$node" >&2
            exit 1
        fi

        control_status=""
        for attempt in 1 2 3 4 5 6; do
            control_status="$(
                docker exec fstservice curl -fsS --connect-timeout 2 --max-time 5 \
                    "http://$node:8000/v1/vpn/status" 2>/dev/null \
                    | python3 -c 'import json,sys; print(json.load(sys.stdin).get("status", ""))' \
                    || true
            )"
            [[ "$control_status" == "running" ]] && break
            ((attempt < 6)) && sleep 5
        done
        if [[ "$control_status" != "running" ]]; then
            printf 'ERROR: control API is not running for %s\n' "$node" >&2
            exit 1
        fi

        egress_output=""
        for attempt in 1 2 3 4 5 6; do
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
            ((attempt < 6)) && sleep 5
        done
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

    printf 'compose_guard runtime=ok healthy=%s unique_egress=%s dns=%s control=%s\n' \
        "$expected_count" "${#egress_owner[@]}" "$expected_count" "$expected_count"
fi

case "$ACTION" in
    check|check-runonce)
        ;;
    recreate)
        cd "$compose_dir"
        docker compose -f "$base_file" -f "$pia_overlay" \
            up -d --no-deps --force-recreate fstworker
        ;;
    recreate-runonce)
        cd "$compose_dir"
        docker compose -f "$base_file" -f "$pia_overlay" -f "$runonce_overlay" \
            up -d --no-deps --force-recreate fstworker
        ;;
esac
