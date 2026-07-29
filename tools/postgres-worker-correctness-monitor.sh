#!/usr/bin/env bash
set -euo pipefail

PG_CONTAINER="${PG_CONTAINER:-fst-postgres}"
PG_USER="${PG_USER:-fst}"
PG_DB="${PG_DB:-fstservice}"
API_BASE="${API_BASE:-http://127.0.0.1:3001}"
READY_URL="${READY_URL:-http://127.0.0.1:8081/readyz}"
FST_STORAGE_ROOT="${FST_STORAGE_ROOT:-/mnt/docker-storage}"
SCRAPE_ID="${SCRAPE_ID:-}"
OUTPUT_FILE="${OUTPUT_FILE:-}"
PROBE_PATH="${PUBLIC_PROBE_PATH:-}"
INTERVAL_SECONDS="${INTERVAL_SECONDS:-60}"
MAX_TICKS="${MAX_TICKS:-1000}"

usage() {
    cat <<'EOF'
Usage: tools/postgres-worker-correctness-monitor.sh --scrape-id ID --output FILE [options]

Options:
  --scrape-id ID       Candidate scrape to monitor
  --output FILE        TSV monitor log under /mnt/docker-storage
  --interval SECONDS   Sampling interval (default: 60)
  --max-ticks COUNT    Safety stop (default: 1000)
  --api-base URL       Festivalweb base URL
  --ready-url URL      FSTService readiness URL
  --probe-path PATH    Representative Festivalweb API path. By default, selects
                       a ready solo leaderboard scope from PostgreSQL.
  -h, --help           Show help

The monitor is read-only and stops after the scrape is no longer running,
public reads are unfrozen, and /api/service-info is no longer updating.
EOF
}

while [[ $# -gt 0 ]]; do
    case "$1" in
        --scrape-id) SCRAPE_ID="$2"; shift 2 ;;
        --output) OUTPUT_FILE="$2"; shift 2 ;;
        --interval) INTERVAL_SECONDS="$2"; shift 2 ;;
        --max-ticks) MAX_TICKS="$2"; shift 2 ;;
        --api-base) API_BASE="${2%/}"; shift 2 ;;
        --ready-url) READY_URL="$2"; shift 2 ;;
        --probe-path) PROBE_PATH="$2"; shift 2 ;;
        -h|--help) usage; exit 0 ;;
        *) printf 'Unknown option: %s\n' "$1" >&2; usage >&2; exit 64 ;;
    esac
done

for value in "$SCRAPE_ID" "$INTERVAL_SECONDS" "$MAX_TICKS"; do
    if [[ ! "$value" =~ ^[0-9]+$ ]]; then
        printf 'ERROR: numeric options must contain only digits\n' >&2
        exit 64
    fi
done
if [[ -z "$OUTPUT_FILE" ]]; then
    printf 'ERROR: --output is required\n' >&2
    exit 64
fi

storage_root="$(realpath -m "$FST_STORAGE_ROOT")"
output_file="$(realpath -m "$OUTPUT_FILE")"
case "$output_file/" in
    "$storage_root/"*) ;;
    *)
        printf 'ERROR: monitor output must stay under %s: %s\n' "$storage_root" "$output_file" >&2
        exit 1
        ;;
esac

mkdir -p "$(dirname "$output_file")"
if [[ -z "$PROBE_PATH" ]]; then
    probe_scope="$(
        docker exec "$PG_CONTAINER" psql \
            -X -v ON_ERROR_STOP=1 -U "$PG_USER" -d "$PG_DB" -AtF '|' \
            -c "
                SELECT song_id, instrument
                FROM solo_current_projection_scope
                WHERE status = 'ready'
                  AND row_count > 0
                ORDER BY row_count DESC, song_id, instrument
                LIMIT 1;
            "
    )"
    IFS='|' read -r probe_song_id probe_instrument <<< "$probe_scope"
    if [[ -z "$probe_song_id" || -z "$probe_instrument" ]]; then
        printf 'ERROR: could not resolve a representative public leaderboard probe\n' >&2
        exit 1
    fi
    PROBE_PATH="/api/leaderboard/$probe_song_id/$probe_instrument?top=10"
fi
if [[ "$PROBE_PATH" != /* ]]; then
    printf 'ERROR: --probe-path must begin with /: %s\n' "$PROBE_PATH" >&2
    exit 64
fi

printf '%s\n' \
    'timestamp_utc	scrape_id	scrape_status	api_status	phase	sub_operation	published_scrape_id	frozen	ready_http	web_http	probe_http	probe_seconds	disk_free_bytes	wal_bytes	temp_bytes	ungranted_locks	long_queries	worker_cpu	worker_memory	postgres_cpu	postgres_memory	service_cpu	service_memory	web_cpu	web_memory	tick' \
    > "$output_file"

for ((tick = 1; tick <= MAX_TICKS; tick++)); do
    timestamp="$(date -u +%Y-%m-%dT%H:%M:%SZ)"
    service_json="$(curl -fsS --max-time 15 "$API_BASE/api/service-info" || printf '{}')"
    api_status="$(jq -r '.currentUpdate.status // "unknown"' <<< "$service_json")"
    phase="$(jq -r '.currentUpdate.phase // ""' <<< "$service_json" | tr '\t\n' '  ')"
    sub_operation="$(jq -r '.currentUpdate.subOperation // ""' <<< "$service_json" | tr '\t\n' '  ')"
    published_scrape_id="$(jq -r '.publishedScrapeId // 0' <<< "$service_json")"

    ready_http="$(curl -sS -o /dev/null -w '%{http_code}' --max-time 10 "$READY_URL" || printf '000')"
    web_http="$(curl -sS -o /dev/null -w '%{http_code}' --max-time 15 "$API_BASE/" || printf '000')"
    probe_result="$(
        curl -sS -o /dev/null -w '%{http_code}|%{time_total}' \
            --max-time 30 "$API_BASE$PROBE_PATH" \
            || printf '000|30'
    )"
    IFS='|' read -r probe_http probe_seconds <<< "$probe_result"
    disk_free_bytes="$(df -B1 --output=avail "$storage_root" | tail -n 1 | tr -d ' ')"

    db_state="$(
        docker exec "$PG_CONTAINER" psql \
            -X -v ON_ERROR_STOP=1 -U "$PG_USER" -d "$PG_DB" -AtF '|' \
            -c "
                SELECT
                    COALESCE((
                        SELECT COALESCE(
                            to_jsonb(scrape_log)->>'status',
                            CASE WHEN completed_at IS NULL THEN 'running' ELSE 'completed' END)
                        FROM scrape_log
                        WHERE id = $SCRAPE_ID
                    ), 'missing'),
                    COALESCE((SELECT public_reads_frozen FROM scrape_publication_state WHERE id = TRUE), FALSE),
                    COALESCE((SELECT SUM(size)::bigint FROM pg_ls_waldir()), 0),
                    COALESCE((SELECT temp_bytes FROM pg_stat_database WHERE datname = current_database()), 0),
                    (SELECT COUNT(*) FROM pg_locks WHERE NOT granted),
                    (
                        SELECT COUNT(*)
                        FROM pg_stat_activity
                        WHERE datname = current_database()
                          AND pid <> pg_backend_pid()
                          AND state = 'active'
                          AND now() - query_start > INTERVAL '5 minutes'
                    );
            "
    )"
    IFS='|' read -r scrape_status frozen wal_bytes temp_bytes ungranted_locks long_queries <<< "$db_state"

    stats="$(docker stats --no-stream --format '{{.Name}}|{{.CPUPerc}}|{{.MemUsage}}' \
        fstworker fst-postgres fstservice festivalweb 2>/dev/null || true)"
    stat_value() {
        local name="$1"
        local column="$2"
        awk -F '|' -v name="$name" -v column="$column" '$1 == name { print $column; exit }' <<< "$stats"
    }

    printf '%s\t%s\t%s\t%s\t%s\t%s\t%s\t%s\t%s\t%s\t%s\t%s\t%s\t%s\t%s\t%s\t%s\t%s\t%s\t%s\t%s\t%s\t%s\t%s\t%s\t%s\n' \
        "$timestamp" "$SCRAPE_ID" "$scrape_status" "$api_status" "$phase" "$sub_operation" \
        "$published_scrape_id" "$frozen" "$ready_http" "$web_http" "$probe_http" "$probe_seconds" \
        "$disk_free_bytes" "$wal_bytes" "$temp_bytes" "$ungranted_locks" "$long_queries" \
        "$(stat_value fstworker 2)" "$(stat_value fstworker 3)" \
        "$(stat_value fst-postgres 2)" "$(stat_value fst-postgres 3)" \
        "$(stat_value fstservice 2)" "$(stat_value fstservice 3)" \
        "$(stat_value festivalweb 2)" "$(stat_value festivalweb 3)" \
        "$tick" \
        >> "$output_file"

    printf '%s scrape=%s db=%s api=%s phase=%s/%s published=%s frozen=%s ready=%s web=%s probe=%s/%ss free=%s artifact=%s\n' \
        "$timestamp" "$SCRAPE_ID" "$scrape_status" "$api_status" "$phase" "$sub_operation" \
        "$published_scrape_id" "$frozen" "$ready_http" "$web_http" "$probe_http" "$probe_seconds" \
        "$disk_free_bytes" "$output_file"

    if [[ "$probe_http" != "200" ]]; then
        printf 'PUBLIC_HEALTH_FAILURE: %s returned HTTP %s after %ss\n' \
            "$PROBE_PATH" "$probe_http" "$probe_seconds" >&2
    fi

    if [[ "$scrape_status" != "running" && "$frozen" == "f" && "$api_status" != "updating" ]]; then
        exit 0
    fi
    sleep "$INTERVAL_SECONDS"
done

printf 'ERROR: monitor reached max ticks without a terminal decision\n' >&2
exit 2
