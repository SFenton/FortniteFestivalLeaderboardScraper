#!/usr/bin/env bash
set -euo pipefail

COMPOSE_DIR="${COMPOSE_DIR:-/home/sfenton/Docker/FestivalServiceTracker}"
BASE_FILE="${BASE_FILE:-docker-compose.yml}"
PIA_OVERLAY="${PIA_OVERLAY:-docker-compose.pia-30.yml}"
RUNONCE_OVERLAY="${RUNONCE_OVERLAY:-docker-compose.runonce.yml}"
RUNTIME_PROBES=true
ACTION="check"

usage() {
    cat <<'EOF'
Usage: tools/fst-worker-compose-guard.sh [options]

Validates the canonical production PIA overlay before any fstworker recreate.
The resolved compose config must declare the expected effective proxy arrays,
all 30 canonical PIA services, aligned provider/control/container metadata,
healthy unique egresses, and no more than 16 Epic requests/s per effective exit.

Options:
  --check                  Validate only (default)
  --recreate               Validate, then recreate and start fstworker
  --recreate-runonce       Validate, then recreate fstworker with run-once overlay
  --config-only            Skip live DNS/control/egress probes
  --compose-dir DIR        Production compose directory
  -h, --help               Show help
EOF
}

while [[ $# -gt 0 ]]; do
    case "$1" in
        --check) ACTION="check"; shift ;;
        --recreate) ACTION="recreate"; shift ;;
        --recreate-runonce) ACTION="recreate-runonce"; shift ;;
        --config-only) RUNTIME_PROBES=false; shift ;;
        --compose-dir) COMPOSE_DIR="$2"; shift 2 ;;
        -h|--help) usage; exit 0 ;;
        *) printf 'ERROR: unknown option: %s\n' "$1" >&2; usage >&2; exit 64 ;;
    esac
done

if ! $RUNTIME_PROBES && [[ "$ACTION" != "check" ]]; then
    printf 'ERROR: --config-only cannot be used with a worker recreate action\n' >&2
    exit 64
fi

for command in docker python3 realpath; do
    if ! command -v "$command" >/dev/null 2>&1; then
        printf 'ERROR: required command not found: %s\n' "$command" >&2
        exit 1
    fi
done

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
if [[ "$ACTION" == "recreate-runonce" && ! -f "$runonce_overlay" ]]; then
    printf 'ERROR: run-once overlay not found: %s\n' "$runonce_overlay" >&2
    exit 1
fi

compose_json="$(
    cd "$compose_dir"
    docker compose -f "$base_file" -f "$pia_overlay" config --format json
)"

validation="$(
    python3 -c '
import json
import re
import sys
from urllib.parse import urlparse

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

if max_rps > expected * 16:
    raise SystemExit(
        f"ERROR: aggregate Epic rate {max_rps} exceeds 16 RPS across "
        f"{expected} effective exits")
if per_endpoint_rps > 16:
    raise SystemExit(
        f"ERROR: per-endpoint Epic rate {per_endpoint_rps} exceeds the 16 RPS ceiling")
if per_endpoint_concurrency > 4:
    raise SystemExit(
        f"ERROR: per-endpoint concurrency {per_endpoint_concurrency} exceeds the qualified ceiling of 4")
if not disable_connection_reuse:
    raise SystemExit("ERROR: canonical PIA worker must disable proxy connection reuse")

print(f"SUMMARY|{expected}|{canonical}|{max_rps}|{per_endpoint_rps}|{per_endpoint_concurrency}|true")
for container in containers:
    print(f"NODE|{container}")
' <<< "$compose_json"
)"

summary="$(head -n 1 <<< "$validation")"
IFS='|' read -r _ expected_count canonical_count max_rps per_endpoint_rps per_endpoint_concurrency connection_reuse_disabled <<< "$summary"
mapfile -t effective_nodes < <(sed -n 's/^NODE|//p' <<< "$validation")

if [[ "${#effective_nodes[@]}" -ne "$expected_count" ]]; then
    printf 'ERROR: internal guard node-count mismatch\n' >&2
    exit 1
fi

printf 'compose_guard config=ok overlay=%s effective=%s canonical=%s max_rps=%s per_endpoint_rps=%s per_endpoint_concurrency=%s connection_reuse=disabled\n' \
    "$(basename "$pia_overlay")" "$expected_count" "$canonical_count" "$max_rps" "$per_endpoint_rps" "$per_endpoint_concurrency"

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

        if ! docker exec "$node" sh -c \
            'getent hosts account-public-service-prod.ol.epicgames.com >/dev/null 2>&1 || nslookup account-public-service-prod.ol.epicgames.com >/dev/null 2>&1'
        then
            printf 'ERROR: Epic DNS lookup failed in %s\n' "$node" >&2
            exit 1
        fi

        control_status="$(
            docker exec fstservice curl -fsS --connect-timeout 2 --max-time 5 \
                "http://$node:8000/v1/vpn/status" 2>/dev/null \
                | python3 -c 'import json,sys; print(json.load(sys.stdin).get("status", ""))' \
                || true
        )"
        if [[ "$control_status" != "running" ]]; then
            printf 'ERROR: control API is not running for %s\n' "$node" >&2
            exit 1
        fi

        egress_output="$(
            docker exec fstservice curl -sS --connect-timeout 3 --max-time 10 \
                -x "http://$node:8888" https://api.ipify.org 2>/dev/null || true
        )"
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
    check)
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
