#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd -P)"
SCRIPT="$SCRIPT_DIR/postgres-scrape-evidence.sh"

bash -n "$SCRIPT"

for column in \
    wire_send_total \
    wire_send_probe_sends \
    wire_send_probe_successes \
    wire_send_status_retries \
    wire_send_network_errors \
    wire_send_cdn_blocks
do
    grep -Fq "NULL::bigint AS $column" "$SCRIPT"
    grep -Fq "\"$column\"" "$SCRIPT"
done

grep -Fq '"available": all(nullable_int(wire_send, key) is not None' "$SCRIPT"
grep -Fq '"wireSendTelemetry"' "$SCRIPT"

printf '%s\n' \
    'postgres-scrape-evidence legacy-schema fallback assertions passed'
