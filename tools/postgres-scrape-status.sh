#!/usr/bin/env bash
set -euo pipefail

API_BASE="${API_BASE:-http://127.0.0.1:8081}"
PG_CONTAINER="${PG_CONTAINER:-fst-postgres}"
PG_USER="${PG_USER:-fst}"
PG_DB="${PG_DB:-fstservice}"
OUT_DIR="${OUT_DIR:-harness-output/postgres-scrape-status-$(date -u +%Y%m%dT%H%M%SZ)}"

mkdir -p "$OUT_DIR"

psql_cmd() {
    docker exec "$PG_CONTAINER" psql -U "$PG_USER" -d "$PG_DB" -v ON_ERROR_STOP=1 -P pager=off "$@"
}

curl_json() {
    local path="$1"
    local output="$2"
    if curl -fsS --max-time 5 "$API_BASE$path" -o "$output"; then
        return 0
    fi

    printf '{"error":"request failed","path":"%s"}\n' "$path" > "$output"
}

curl_json "/api/service-info" "$OUT_DIR/service-info.json"
curl_json "/api/progress" "$OUT_DIR/progress.json"

psql_cmd -c """
SELECT now() AS sampled_at;

SELECT id, started_at, completed_at, now() - started_at AS age,
       songs_scraped, total_entries, total_requests, total_bytes
FROM scrape_log
ORDER BY id DESC
LIMIT 5;

SELECT published_scrape_id, published_at, public_reads_frozen,
       public_reads_frozen_at, public_reads_frozen_scrape_id,
       public_reads_frozen_reason, updated_at
FROM scrape_publication_state;

SELECT worker_key, status, mode, started_at, last_heartbeat_at,
       last_status_change_at, message, current_operation_json,
       last_operation_json
FROM service_worker_status
ORDER BY worker_key;

SELECT pid, state, now() - query_start AS query_age,
       wait_event_type, wait_event, LEFT(query, 500) AS query
FROM pg_stat_activity
WHERE datname = current_database()
  AND state <> 'idle'
ORDER BY query_start NULLS LAST;

SELECT phase, subphase, item_key, started_at, completed_at,
       duration_ms, rows_read, rows_written, rows_deleted,
       scope_count, success
FROM scrape_phase_timings
ORDER BY completed_at DESC NULLS LAST, started_at DESC
LIMIT 40;
""" > "$OUT_DIR/database-status.txt"

docker stats --no-stream \
    --format 'table {{.Name}}\t{{.CPUPerc}}\t{{.MemUsage}}\t{{.BlockIO}}' \
    fstworker fstservice "$PG_CONTAINER" \
    > "$OUT_DIR/docker-stats.txt" 2>/dev/null || true

docker logs --since "${LOG_SINCE:-30m}" --tail "${LOG_TAIL:-300}" fstworker \
    > "$OUT_DIR/fstworker.log" 2>&1 || true

python3 - "$OUT_DIR" <<'PY'
import json
import sys
from pathlib import Path

out_dir = Path(sys.argv[1])

def load_json(name):
    try:
        return json.loads((out_dir / name).read_text())
    except Exception as exc:
        return {"error": str(exc)}

service = load_json("service-info.json")
progress = load_json("progress.json")

worker = service.get("workerStatus") or {}
current = worker.get("currentOperation") or {}
last = worker.get("lastOperation") or {}
current_update = service.get("currentUpdate") or {}
last_update = service.get("lastCompletedUpdate") or {}

lines = [
    "# Postgres Scrape Status",
    "",
    f"- Current update status: `{current_update.get('status', 'unknown')}`",
    f"- Worker raw status: `{worker.get('rawStatus', worker.get('status', 'unknown'))}`",
    f"- Worker message: `{worker.get('message', '')}`",
    f"- Worker heartbeat age seconds: `{worker.get('heartbeatAgeSeconds', 'unknown')}`",
    f"- Current operation: `{current.get('operationKey', 'none')}` / `{current.get('phase', 'none')}` / `{current.get('subOperation', 'none')}`",
    f"- Current operation started: `{current.get('startedAt', current.get('StartedAtUtc', 'unknown'))}`",
    f"- Last operation: `{last.get('operationKey', 'none')}` / `{last.get('status', 'none')}` / `{last.get('phase', 'none')}`",
    f"- Last completed update: `{last_update.get('startedAt', 'unknown')}` -> `{last_update.get('completedAt', 'unknown')}`",
    "",
    "## Progress endpoint",
    "",
    f"- Running operations: `{len(progress.get('running') or [])}`",
    f"- Completed operations: `{len(progress.get('completedOperations') or [])}`",
    "",
    "## Raw files",
    "",
    "- `service-info.json`",
    "- `progress.json`",
    "- `database-status.txt`",
    "- `docker-stats.txt`",
    "- `fstworker.log`",
    "",
]

(out_dir / "report.md").write_text("\n".join(lines))
PY

printf 'Scrape status report: %s\n' "$OUT_DIR/report.md"
