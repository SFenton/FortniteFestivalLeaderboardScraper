#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd -P)"
REPO_ROOT="$(cd -- "$SCRIPT_DIR/.." && pwd -P)"

PG_CONTAINER="${PG_CONTAINER:-fst-postgres}"
PG_USER="${PG_USER:-fst}"
PG_DB="${PG_DB:-fstservice}"
API_BASE="${API_BASE:-http://127.0.0.1:3001}"
FST_STORAGE_ROOT="${FST_STORAGE_ROOT:-/mnt/docker-storage}"
FST_ARTIFACT_ROOT="${FST_ARTIFACT_ROOT:-/mnt/docker-storage/Docker/FestivalServiceTracker/fst-data/autonomous-artifacts}"
OUT_DIR="${OUT_DIR:-$FST_ARTIFACT_ROOT/postgres-scrape-evidence-$(date -u +%Y%m%dT%H%M%SZ)}"
SCRAPE_ID="${SCRAPE_ID:-}"
LABEL="${LABEL:-capture}"
COMPARE_TO="${COMPARE_TO:-}"

usage() {
    cat <<'EOF'
Usage: tools/postgres-scrape-evidence.sh [options]

Captures a bounded, read-only PostgreSQL/public-route evidence pack for one
scrape. The pack is suitable for later matched A/B comparison.

Options:
  --out-dir DIR       New output directory
  --scrape-id ID      Scrape to describe (default: latest completed scrape)
  --label LABEL       Human-readable capture label
  --api-base URL      Festivalweb base URL (default: http://127.0.0.1:3001)
  --compare-to DIR    Existing evidence pack to compare against
  --pg-container NAME PostgreSQL container name
  -h, --help          Show this help

The command records no credentials and performs no schema, data, container, or
publication mutation.
EOF
}

while [[ $# -gt 0 ]]; do
    case "$1" in
        --out-dir) OUT_DIR="$2"; shift 2 ;;
        --scrape-id) SCRAPE_ID="$2"; shift 2 ;;
        --label) LABEL="$2"; shift 2 ;;
        --api-base) API_BASE="${2%/}"; shift 2 ;;
        --compare-to) COMPARE_TO="$2"; shift 2 ;;
        --pg-container) PG_CONTAINER="$2"; shift 2 ;;
        -h|--help) usage; exit 0 ;;
        *) printf 'Unknown option: %s\n\n' "$1" >&2; usage >&2; exit 64 ;;
    esac
done

require_command() {
    if ! command -v "$1" >/dev/null 2>&1; then
        printf 'ERROR: required command not found: %s\n' "$1" >&2
        exit 1
    fi
}

for command in awk curl cut docker find git python3 realpath sed sha256sum; do
    require_command "$command"
done

if [[ -n "$SCRAPE_ID" && ! "$SCRAPE_ID" =~ ^[0-9]+$ ]]; then
    printf 'ERROR: --scrape-id must be a non-negative integer\n' >&2
    exit 64
fi

fst_storage_root="$(realpath -m "$FST_STORAGE_ROOT")"
OUT_DIR="$(realpath -m "$OUT_DIR")"
case "$OUT_DIR/" in
    "$fst_storage_root/"*) ;;
    *)
        printf 'ERROR: evidence output must stay under FST storage root %s: %s\n' \
            "$fst_storage_root" "$OUT_DIR" >&2
        exit 1
        ;;
esac

if [[ -n "$COMPARE_TO" ]]; then
    COMPARE_TO="$(realpath -m "$COMPARE_TO")"
    case "$COMPARE_TO/" in
        "$fst_storage_root/"*) ;;
        *)
            printf 'ERROR: comparison evidence must stay under FST storage root %s: %s\n' \
                "$fst_storage_root" "$COMPARE_TO" >&2
            exit 1
            ;;
    esac
fi

if [[ -e "$OUT_DIR" && -n "$(find "$OUT_DIR" -mindepth 1 -maxdepth 1 -print -quit 2>/dev/null)" ]]; then
    printf 'ERROR: output directory is not empty: %s\n' "$OUT_DIR" >&2
    exit 1
fi
mkdir -p "$OUT_DIR/routes"

psql_scalar() {
    docker exec "$PG_CONTAINER" psql \
        -X -v ON_ERROR_STOP=1 -U "$PG_USER" -d "$PG_DB" -At \
        -c "$1"
}

psql_csv() {
    local sql="$1"
    local output="$2"
    printf 'COPY (%s) TO STDOUT WITH (FORMAT CSV, HEADER TRUE);\n' "$sql" |
        docker exec -i "$PG_CONTAINER" psql \
            -X -v ON_ERROR_STOP=1 -U "$PG_USER" -d "$PG_DB" \
            > "$output"
}

if [[ -z "$SCRAPE_ID" ]]; then
    SCRAPE_ID="$(psql_scalar "
        SELECT COALESCE((
            SELECT id
            FROM scrape_log
            WHERE completed_at IS NOT NULL
              AND COALESCE(to_jsonb(scrape_log)->>'status', 'completed') = 'completed'
            ORDER BY id DESC
            LIMIT 1
        ), 0);
    ")"
fi
if [[ "$SCRAPE_ID" == "0" ]]; then
    printf 'ERROR: no completed scrape is available and --scrape-id was not supplied\n' >&2
    exit 1
fi

docker exec "$PG_CONTAINER" pg_isready -U "$PG_USER" -d "$PG_DB" >/dev/null

has_scope_complete="$(
    psql_scalar "
        SELECT EXISTS (
            SELECT 1
            FROM information_schema.columns
            WHERE table_schema = 'public'
              AND table_name = 'leaderboard_scope_fingerprints'
              AND column_name = 'is_complete'
        );
    "
)"
if [[ "$has_scope_complete" == "t" ]]; then
    scope_complete_select="is_complete"
    scope_incomplete_predicate="NOT is_complete"
else
    scope_complete_select="FALSE AS is_complete"
    scope_incomplete_predicate="TRUE"
fi

has_scrape_status="$(
    psql_scalar "
        SELECT EXISTS (
            SELECT 1
            FROM information_schema.columns
            WHERE table_schema = 'public'
              AND table_name = 'scrape_log'
              AND column_name = 'status'
        );
    "
)"
if [[ "$has_scrape_status" == "t" ]]; then
    scrape_status_select="scrape.status, scrape.failed_at, scrape.failure_phase, scrape.failure_message, scrape.best_effort_failure_count, scrape.best_effort_failed_phases"
else
    scrape_status_select="CASE WHEN scrape.completed_at IS NULL THEN 'running' ELSE 'completed' END AS status, NULL::timestamptz AS failed_at, NULL::text AS failure_phase, NULL::text AS failure_message, 0::integer AS best_effort_failure_count, ARRAY[]::text[] AS best_effort_failed_phases"
fi

"$SCRIPT_DIR/postgres-capacity-guard.sh" \
    --action-class observation \
    --pg-container "$PG_CONTAINER" \
    --output "$OUT_DIR/capacity.json" \
    >/dev/null

date -u +%Y-%m-%dT%H:%M:%SZ > "$OUT_DIR/captured-at-utc.txt"
printf '%s\n' "$LABEL" > "$OUT_DIR/label.txt"
printf '%s\n' "$SCRAPE_ID" > "$OUT_DIR/scrape-id.txt"
git -C "$REPO_ROOT" rev-parse HEAD > "$OUT_DIR/git-commit.txt"
git -C "$REPO_ROOT" status --short > "$OUT_DIR/git-status.txt"

{
    printf 'container,status,health,image_id,started_at\n'
    for container in fst-postgres fstservice festivalweb fstworker; do
        docker inspect --format \
            '{{.Name}},{{.State.Status}},{{if .State.Health}}{{.State.Health.Status}}{{else}}none{{end}},{{.Image}},{{.State.StartedAt}}' \
            "$container" | sed 's#^/##'
    done
} > "$OUT_DIR/containers.csv"

docker stats --no-stream \
    --format '{{.Name}},{{.CPUPerc}},{{.MemUsage}},{{.MemPerc}},{{.BlockIO}},{{.PIDs}}' \
    fst-postgres fstservice festivalweb fstworker \
    > "$OUT_DIR/docker-stats.csv"
sed -i '1i container,cpu_percent,memory_usage,memory_percent,block_io,pids' "$OUT_DIR/docker-stats.csv"

psql_csv "
    SELECT
        scrape.id,
        scrape.started_at,
        scrape.completed_at,
        scrape.songs_scraped,
        scrape.total_entries,
        scrape.total_requests,
        scrape.total_bytes,
        scrape.epic_reported_over_100_pages,
        $scrape_status_select,
        publication.published_scrape_id,
        publication.published_at,
        publication.public_reads_frozen,
        publication.public_reads_frozen_at,
        publication.public_reads_frozen_scrape_id,
        publication.public_reads_frozen_reason,
        publication.updated_at AS publication_updated_at
    FROM scrape_log scrape
    CROSS JOIN scrape_publication_state publication
    WHERE scrape.id = $SCRAPE_ID
" "$OUT_DIR/scrape-publication.csv"

if [[ "$(psql_scalar "SELECT to_regclass('public.leaderboard_scope_manifests') IS NOT NULL;")" == "t" ]]; then
    psql_csv "
        SELECT
            scrape_id, song_id, instrument, scope_kind,
            expected_first_page, expected_last_page, received_pages,
            page_statuses, terminal_boundary, terminal_boundary_page,
            parse_status, retry_exhausted, reported_total_entries,
            reported_total_pages, deep_start_page, deep_end_page,
            content_fingerprint, coverage_fingerprint, is_complete,
            failure_reason, created_at, updated_at
        FROM leaderboard_scope_manifests
        WHERE scrape_id = $SCRAPE_ID
        ORDER BY instrument, song_id, scope_kind
    " "$OUT_DIR/scope-manifests.csv"
else
    printf '%s\n' \
        'scrape_id,song_id,instrument,scope_kind,expected_first_page,expected_last_page,received_pages,page_statuses,terminal_boundary,terminal_boundary_page,parse_status,retry_exhausted,reported_total_entries,reported_total_pages,deep_start_page,deep_end_page,content_fingerprint,coverage_fingerprint,is_complete,failure_reason,created_at,updated_at' \
        > "$OUT_DIR/scope-manifests.csv"
fi

if [[ "$(psql_scalar "SELECT to_regclass('public.scrape_writer_failures') IS NOT NULL;")" == "t" ]]; then
    psql_csv "
        SELECT writer_kind, instrument, song_id, page_count, row_count,
               artifact_path, exception_type, error_message, occurred_at, replayed_at
        FROM scrape_writer_failures
        WHERE scrape_id = $SCRAPE_ID
        ORDER BY writer_kind, instrument, song_id, id
    " "$OUT_DIR/writer-failures.csv"
else
    printf '%s\n' \
        'writer_kind,instrument,song_id,page_count,row_count,artifact_path,exception_type,error_message,occurred_at,replayed_at' \
        > "$OUT_DIR/writer-failures.csv"
fi

if [[ "$(psql_scalar "SELECT to_regclass('public.scrape_phase_outcomes') IS NOT NULL;")" == "t" ]]; then
    psql_csv "
        SELECT phase, criticality, status, started_at, completed_at,
               duration_ms, error_message
        FROM scrape_phase_outcomes
        WHERE scrape_id = $SCRAPE_ID
        ORDER BY started_at, phase
    " "$OUT_DIR/phase-outcomes.csv"
else
    printf '%s\n' \
        'phase,criticality,status,started_at,completed_at,duration_ms,error_message' \
        > "$OUT_DIR/phase-outcomes.csv"
fi

psql_csv "
    SELECT
        song_id,
        instrument,
        scope_kind,
        fingerprint_version,
        content_fingerprint,
        coverage_fingerprint,
        entry_count,
        reported_total_entries,
        reported_total_pages,
        $scope_complete_select,
        min_rank,
        max_rank,
        source_scrape_id,
        published_scrape_id,
        first_seen_scrape_id,
        last_changed_scrape_id,
        last_seen_scrape_id,
        changed_at,
        seen_at
    FROM leaderboard_scope_fingerprints
    ORDER BY instrument, song_id, scope_kind
" "$OUT_DIR/scope-fingerprints.csv"

psql_csv "
    SELECT
        instrument,
        scope_kind,
        COUNT(*) AS scope_count,
        SUM(entry_count)::bigint AS entry_count,
        MIN(min_rank) AS min_rank,
        MAX(max_rank) AS max_rank,
        COUNT(*) FILTER (WHERE published_scrape_id IS NULL) AS missing_published_scrape_id,
        COUNT(*) FILTER (WHERE reported_total_entries IS NULL) AS missing_reported_entries,
        COUNT(*) FILTER (WHERE reported_total_pages IS NULL) AS missing_reported_pages,
        COUNT(*) FILTER (WHERE $scope_incomplete_predicate) AS incomplete_scopes,
        COUNT(*) FILTER (WHERE source_scrape_id = $SCRAPE_ID) AS source_scope_count,
        COUNT(*) FILTER (WHERE last_seen_scrape_id = $SCRAPE_ID) AS seen_scope_count,
        COUNT(*) FILTER (WHERE last_changed_scrape_id = $SCRAPE_ID) AS changed_scope_count
    FROM leaderboard_scope_fingerprints
    GROUP BY instrument, scope_kind
    ORDER BY instrument, scope_kind
" "$OUT_DIR/scope-summary.csv"

if [[ "$(psql_scalar "SELECT to_regclass('public.leaderboard_published_scope_source') IS NOT NULL;")" == "t" ]]; then
    psql_csv "
        SELECT
            published_scrape_id,
            song_id,
            instrument,
            scope_kind,
            source_kind,
            source_snapshot_id,
            source_scrape_id,
            row_count,
            content_fingerprint,
            coverage_fingerprint,
            reported_total_entries,
            reported_total_pages,
            is_complete,
            created_at,
            validated_at
        FROM leaderboard_published_scope_source
        WHERE published_scrape_id = $SCRAPE_ID
        ORDER BY instrument, song_id, scope_kind
    " "$OUT_DIR/published-scope-sources.csv"
else
    printf '%s\n' \
        'published_scrape_id,song_id,instrument,scope_kind,source_kind,source_snapshot_id,source_scrape_id,row_count,content_fingerprint,coverage_fingerprint,reported_total_entries,reported_total_pages,is_complete,created_at,validated_at' \
        > "$OUT_DIR/published-scope-sources.csv"
fi

psql_csv "
    SELECT *
    FROM leaderboard_logical_write_metrics
    WHERE scrape_id = $SCRAPE_ID
    ORDER BY instrument
" "$OUT_DIR/logical-write-metrics.csv"

psql_csv "
    SELECT
        phase,
        subphase,
        item_key,
        started_at,
        completed_at,
        duration_ms,
        rows_read,
        rows_written,
        rows_deleted,
        scope_count,
        success,
        error_message
    FROM scrape_phase_timings
    WHERE scrape_id = $SCRAPE_ID
    ORDER BY started_at, phase, subphase, item_key
" "$OUT_DIR/phase-timings.csv"

psql_csv "
    SELECT
        c.relname AS relation_name,
        CASE c.relkind WHEN 'p' THEN 'partitioned' ELSE 'table' END AS relation_kind,
        COALESCE(parent.relname, '') AS parent_name,
        pg_total_relation_size(c.oid) AS total_bytes,
        pg_relation_size(c.oid) AS heap_bytes,
        pg_indexes_size(c.oid) AS index_bytes,
        COALESCE(stats.n_live_tup, 0)::bigint AS estimated_live_rows,
        COALESCE(stats.n_dead_tup, 0)::bigint AS estimated_dead_rows
    FROM pg_class c
    JOIN pg_namespace namespace ON namespace.oid = c.relnamespace
    LEFT JOIN pg_inherits inheritance ON inheritance.inhrelid = c.oid
    LEFT JOIN pg_class parent ON parent.oid = inheritance.inhparent
    LEFT JOIN pg_stat_user_tables stats ON stats.relid = c.oid
    WHERE namespace.nspname = 'public'
      AND c.relkind IN ('r', 'p')
    ORDER BY total_bytes DESC, relation_name
" "$OUT_DIR/relation-sizes.csv"

psql_csv "
    SELECT
        tablename,
        indexname,
        pg_relation_size((quote_ident(schemaname) || '.' || quote_ident(indexname))::regclass) AS index_bytes,
        indexdef
    FROM pg_indexes
    WHERE schemaname = 'public'
    ORDER BY index_bytes DESC, tablename, indexname
" "$OUT_DIR/index-sizes.csv"

psql_csv "
    SELECT
        datname,
        xact_commit,
        xact_rollback,
        blks_read,
        blks_hit,
        temp_files,
        temp_bytes,
        deadlocks,
        blk_read_time,
        blk_write_time,
        stats_reset
    FROM pg_stat_database
    WHERE datname = current_database()
" "$OUT_DIR/database-counters.csv"

psql_csv "
    SELECT
        wal_records,
        wal_fpi,
        wal_bytes,
        wal_buffers_full,
        wal_write,
        wal_sync,
        wal_write_time,
        wal_sync_time,
        stats_reset
    FROM pg_stat_wal
" "$OUT_DIR/wal-counters.csv"

psql_csv "
    SELECT
        num_timed,
        num_requested,
        restartpoints_timed,
        restartpoints_req,
        restartpoints_done,
        write_time,
        sync_time,
        buffers_written,
        stats_reset
    FROM pg_stat_checkpointer
" "$OUT_DIR/checkpoint-counters.csv"

psql_csv "
    SELECT
        pid,
        state,
        now() - query_start AS query_age,
        wait_event_type,
        wait_event,
        LEFT(query, 500) AS query
    FROM pg_stat_activity
    WHERE datname = current_database()
      AND pid <> pg_backend_pid()
      AND state <> 'idle'
    ORDER BY query_start NULLS LAST
" "$OUT_DIR/active-queries.csv"

psql_csv "
    SELECT
        (SELECT COUNT(*) FROM pg_locks WHERE NOT granted) AS ungranted_locks,
        (SELECT COUNT(*) FROM pg_stat_progress_vacuum) AS active_vacuums,
        (SELECT COUNT(*) FROM pg_stat_progress_create_index) AS active_index_builds
" "$OUT_DIR/lock-maintenance-summary.csv"

psql_csv "
    SELECT
        instrument,
        status,
        COUNT(*) AS scope_count,
        SUM(row_count)::bigint AS row_count,
        MIN(source_snapshot_id) AS min_source_snapshot_id,
        MAX(source_snapshot_id) AS max_source_snapshot_id,
        MIN(last_rebuilt_at) AS oldest_rebuild,
        MAX(last_rebuilt_at) AS newest_rebuild
    FROM solo_current_projection_scope
    GROUP BY instrument, status
    ORDER BY instrument, status
" "$OUT_DIR/solo-projection-summary.csv"

psql_csv "
    SELECT
        band_type,
        ranking_scope,
        status,
        COUNT(*) AS scope_count,
        SUM(row_count)::bigint AS row_count,
        MIN(projection_generation) AS min_generation,
        MAX(projection_generation) AS max_generation,
        MIN(published_generation) AS min_published_generation,
        MAX(published_generation) AS max_published_generation,
        MIN(last_rebuilt_at) AS oldest_rebuild,
        MAX(last_rebuilt_at) AS newest_rebuild
    FROM band_current_projection_scope
    GROUP BY band_type, ranking_scope, status
    ORDER BY band_type, ranking_scope, status
" "$OUT_DIR/band-projection-summary.csv"

solo_sample="$(
    psql_scalar "
        SELECT song_id || '|' || instrument
        FROM solo_current_projection_scope
        WHERE status = 'ready' AND row_count > 0
        ORDER BY row_count DESC, song_id, instrument
        LIMIT 1
    "
)"
band_sample="$(
    psql_scalar "
        SELECT song_id || '|' || band_type
        FROM band_current_projection_scope
        WHERE status = 'ready' AND row_count > 0
        ORDER BY row_count DESC, song_id, band_type
        LIMIT 1
    "
)"

capture_route() {
    local name="$1"
    local path="$2"
    local body="$OUT_DIR/routes/$name.body"
    local metrics
    metrics="$(curl --max-time 30 -sS -o "$body" -w '%{http_code}|%{time_total}|%{size_download}' "$API_BASE$path")"
    local sha
    sha="$(sha256sum "$body" | awk '{print $1}')"
    printf 'name,path,http_status,time_seconds,body_bytes,sha256\n' > "$OUT_DIR/routes/$name.csv"
    printf '%s,%s,%s,%s,%s,%s\n' \
        "$name" "$path" "${metrics%%|*}" "$(cut -d'|' -f2 <<< "$metrics")" \
        "$(cut -d'|' -f3 <<< "$metrics")" "$sha" \
        >> "$OUT_DIR/routes/$name.csv"
}

capture_route "service-info" "/api/service-info"
capture_route "features" "/api/features"
capture_route "songs" "/api/songs"

if [[ -n "$solo_sample" ]]; then
    capture_route \
        "solo-leaderboard" \
        "/api/leaderboard/${solo_sample%%|*}/${solo_sample##*|}?top=10"
fi
if [[ -n "$band_sample" ]]; then
    capture_route \
        "band-leaderboard" \
        "/api/leaderboard/${band_sample%%|*}/bands/${band_sample##*|}?top=10"
fi

sha256sum "$OUT_DIR"/routes/*.body > "$OUT_DIR/route-fingerprints.sha256"

python3 - "$OUT_DIR" "$COMPARE_TO" <<'PY'
import csv
import hashlib
import json
import sys
from pathlib import Path

out_dir = Path(sys.argv[1])
compare_to = Path(sys.argv[2]).resolve() if sys.argv[2] else None

def first_row(name):
    with (out_dir / name).open(newline="", encoding="utf-8") as handle:
        return next(csv.DictReader(handle), {})

def rows(name, root=out_dir):
    with (root / name).open(newline="", encoding="utf-8") as handle:
        return list(csv.DictReader(handle))

def keyed(name, key, root=out_dir):
    return {row[key]: row for row in rows(name, root)}

scrape = first_row("scrape-publication.csv")
capacity = json.loads((out_dir / "capacity.json").read_text(encoding="utf-8"))
scope_rows = rows("scope-summary.csv")
published_source_rows = rows("published-scope-sources.csv")
logical_rows = rows("logical-write-metrics.csv")
phase_rows = rows("phase-timings.csv")
manifest_rows = rows("scope-manifests.csv")
writer_failure_rows = rows("writer-failures.csv")
phase_outcome_rows = rows("phase-outcomes.csv")

summary = {
    "label": (out_dir / "label.txt").read_text(encoding="utf-8").strip(),
    "scrapeId": int((out_dir / "scrape-id.txt").read_text(encoding="utf-8").strip()),
    "gitCommit": (out_dir / "git-commit.txt").read_text(encoding="utf-8").strip(),
    "scrape": scrape,
    "capacity": capacity["capacity"],
    "storage": capacity["storage"],
    "scopeTotals": {
        "scopes": sum(int(row["scope_count"]) for row in scope_rows),
        "entries": sum(int(row["entry_count"] or 0) for row in scope_rows),
        "missingPublishedScrapeId": sum(int(row["missing_published_scrape_id"]) for row in scope_rows),
        "missingReportedEntries": sum(int(row["missing_reported_entries"]) for row in scope_rows),
        "missingReportedPages": sum(int(row["missing_reported_pages"]) for row in scope_rows),
        "incompleteScopes": sum(int(row["incomplete_scopes"]) for row in scope_rows),
        "targetSourceScopes": sum(int(row["source_scope_count"]) for row in scope_rows),
        "targetSeenScopes": sum(int(row["seen_scope_count"]) for row in scope_rows),
        "targetChangedScopes": sum(int(row["changed_scope_count"]) for row in scope_rows),
    },
    "publishedSources": {
        "scopes": len(published_source_rows),
        "snapshotScopes": sum(row["source_kind"] == "snapshot" for row in published_source_rows),
        "emptyScopes": sum(row["source_kind"] == "empty" for row in published_source_rows),
        "rows": sum(int(row["row_count"] or 0) for row in published_source_rows),
        "incompleteScopes": sum(row["is_complete"].lower() not in {"t", "true", "1"} for row in published_source_rows),
        "minSourceScrapeId": min((int(row["source_scrape_id"]) for row in published_source_rows), default=None),
        "maxSourceScrapeId": max((int(row["source_scrape_id"]) for row in published_source_rows), default=None),
    },
    "logicalWrites": {
        column: sum(int(row[column] or 0) for row in logical_rows)
        for column in (
            "flush_count",
            "observed_rows",
            "new_rows",
            "changed_rows",
            "unchanged_rows",
            "current_upserts",
            "versions_closed",
            "versions_opened",
        )
    },
    "phaseTimings": {
        "rows": len(phase_rows),
        "failed": sum(row["success"].lower() not in {"t", "true", "1"} for row in phase_rows),
        "durationMs": sum(int(row["duration_ms"] or 0) for row in phase_rows),
    },
    "scopeManifests": {
        "scopes": len(manifest_rows),
        "complete": sum(row["is_complete"].lower() in {"t", "true", "1"} for row in manifest_rows),
        "incomplete": sum(row["is_complete"].lower() not in {"t", "true", "1"} for row in manifest_rows),
        "parseFailures": sum(row["parse_status"] == "failed" for row in manifest_rows),
        "retryExhausted": sum(row["retry_exhausted"].lower() in {"t", "true", "1"} for row in manifest_rows),
        "terminalBoundaries": sum(row["terminal_boundary"] not in {"none", ""} for row in manifest_rows),
        "deepScopes": sum(bool(row["deep_start_page"]) for row in manifest_rows),
    },
    "writerFailures": {
        "scopes": len(writer_failure_rows),
        "pages": sum(int(row["page_count"] or 0) for row in writer_failure_rows),
        "rows": sum(int(row["row_count"] or 0) for row in writer_failure_rows),
    },
    "phaseOutcomes": {
        "phases": len(phase_outcome_rows),
        "criticalFailures": sum(
            row["criticality"] == "publication_critical" and row["status"] == "failed"
            for row in phase_outcome_rows
        ),
        "bestEffortFailures": sum(
            row["criticality"] == "best_effort" and row["status"] == "failed"
            for row in phase_outcome_rows
        ),
    },
    "comparison": None,
}
summary["scopeTotals"]["fingerprintSnapshotCompleteForTarget"] = (
    summary["scopeTotals"]["targetSeenScopes"] == summary["scopeTotals"]["scopes"]
)

if compare_to:
    if not compare_to.is_dir():
        raise SystemExit(f"comparison directory does not exist: {compare_to}")
    baseline_summary = json.loads((compare_to / "summary.json").read_text(encoding="utf-8"))
    baseline_relations = keyed("relation-sizes.csv", "relation_name", compare_to)
    candidate_relations = keyed("relation-sizes.csv", "relation_name")
    relation_deltas = []
    for name in sorted(set(baseline_relations) | set(candidate_relations)):
        before = int(baseline_relations.get(name, {}).get("total_bytes", 0))
        after = int(candidate_relations.get(name, {}).get("total_bytes", 0))
        if before != after:
            relation_deltas.append({"relation": name, "beforeBytes": before, "afterBytes": after, "deltaBytes": after - before})

    baseline_routes = {
        path.name: hashlib.sha256(path.read_bytes()).hexdigest()
        for path in (compare_to / "routes").glob("*.body")
    }
    candidate_routes = {
        path.name: hashlib.sha256(path.read_bytes()).hexdigest()
        for path in (out_dir / "routes").glob("*.body")
    }
    route_equality = {
        name: baseline_routes.get(name) == candidate_routes.get(name)
        for name in sorted(set(baseline_routes) | set(candidate_routes))
    }
    stable_route_names = [name for name in route_equality if name != "service-info.body"]
    summary["comparison"] = {
        "baseline": str(compare_to),
        "baselineScrapeId": baseline_summary["scrapeId"],
        "scopeTotalsEqual": baseline_summary["scopeTotals"] == summary["scopeTotals"],
        "routeFingerprintEquality": route_equality,
        "stableRouteFingerprintsEqual": all(route_equality[name] for name in stable_route_names),
        "relationDeltas": relation_deltas,
    }

(out_dir / "summary.json").write_text(json.dumps(summary, indent=2) + "\n", encoding="utf-8")

files = []
for path in sorted(out_dir.rglob("*")):
    if path.is_file() and path.name not in {"manifest.json", "report.md"}:
        files.append({
            "path": path.relative_to(out_dir).as_posix(),
            "bytes": path.stat().st_size,
            "sha256": hashlib.sha256(path.read_bytes()).hexdigest(),
        })
(out_dir / "manifest.json").write_text(json.dumps({"files": files}, indent=2) + "\n", encoding="utf-8")

report = [
    "# PostgreSQL Scrape Evidence",
    "",
    f"- Label: `{summary['label']}`",
    f"- Scrape: `{summary['scrapeId']}`",
    f"- Commit: `{summary['gitCommit']}`",
    f"- Published scrape: `{scrape.get('published_scrape_id', 'unknown')}`",
    f"- Public reads frozen: `{scrape.get('public_reads_frozen', 'unknown')}`",
    f"- Free bytes: `{summary['storage']['filesystemFreeBytes']}`",
    f"- Projected headroom days: `{summary['capacity']['projectedHeadroomDays']}`",
    f"- Scope rows: `{summary['scopeTotals']['scopes']}`",
    f"- Fingerprint entry total: `{summary['scopeTotals']['entries']}`",
    f"- Missing published IDs: `{summary['scopeTotals']['missingPublishedScrapeId']}`",
    f"- Missing reported entry/page fields: `{summary['scopeTotals']['missingReportedEntries']}` / `{summary['scopeTotals']['missingReportedPages']}`",
    f"- Incomplete fingerprint scopes: `{summary['scopeTotals']['incompleteScopes']}`",
    f"- Fingerprint rows last seen in target scrape: `{summary['scopeTotals']['targetSeenScopes']}` / `{summary['scopeTotals']['scopes']}`",
    f"- Fingerprint snapshot complete for target: `{summary['scopeTotals']['fingerprintSnapshotCompleteForTarget']}`",
    f"- Published source scopes (snapshot/empty/incomplete): `{summary['publishedSources']['scopes']}` / `{summary['publishedSources']['snapshotScopes']}` / `{summary['publishedSources']['emptyScopes']}` / `{summary['publishedSources']['incompleteScopes']}`",
    f"- Published source row total and source scrape range: `{summary['publishedSources']['rows']}` / `{summary['publishedSources']['minSourceScrapeId']}`-`{summary['publishedSources']['maxSourceScrapeId']}`",
    f"- Logical observed/changed/unchanged rows: `{summary['logicalWrites']['observed_rows']}` / `{summary['logicalWrites']['changed_rows']}` / `{summary['logicalWrites']['unchanged_rows']}`",
    f"- Phase timing rows/failures: `{summary['phaseTimings']['rows']}` / `{summary['phaseTimings']['failed']}`",
    f"- Scope manifests (complete/incomplete): `{summary['scopeManifests']['scopes']}` / `{summary['scopeManifests']['complete']}` / `{summary['scopeManifests']['incomplete']}`",
    f"- Manifest parse failures/retry exhaustion/terminal boundaries/deep scopes: `{summary['scopeManifests']['parseFailures']}` / `{summary['scopeManifests']['retryExhausted']}` / `{summary['scopeManifests']['terminalBoundaries']}` / `{summary['scopeManifests']['deepScopes']}`",
    f"- Writer failure scopes/pages/rows: `{summary['writerFailures']['scopes']}` / `{summary['writerFailures']['pages']}` / `{summary['writerFailures']['rows']}`",
    f"- Phase outcomes critical/best-effort failures: `{summary['phaseOutcomes']['criticalFailures']}` / `{summary['phaseOutcomes']['bestEffortFailures']}`",
    "",
    "The manifest contains SHA-256 checksums for every captured evidence file.",
]
if summary["comparison"]:
    report.extend([
        "",
        "## Comparison",
        "",
        f"- Baseline: `{summary['comparison']['baseline']}`",
        f"- Scope totals equal: `{summary['comparison']['scopeTotalsEqual']}`",
        f"- Stable route fingerprints equal: `{summary['comparison']['stableRouteFingerprintsEqual']}`",
        f"- Changed relation count: `{len(summary['comparison']['relationDeltas'])}`",
    ])
(out_dir / "report.md").write_text("\n".join(report) + "\n", encoding="utf-8")
PY

printf 'PostgreSQL scrape evidence: %s\n' "$OUT_DIR/report.md"
