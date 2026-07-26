#!/usr/bin/env bash
set -euo pipefail

PG_CONTAINER="${PG_CONTAINER:-fst-postgres}"
PG_USER="${PG_USER:-fst}"
PG_DB="${PG_DB:-fstservice}"
FST_STORAGE_ROOT="${FST_STORAGE_ROOT:-/mnt/docker-storage}"
COMPOSE_DIR="${COMPOSE_DIR:-/home/sfenton/Docker/FestivalServiceTracker}"
SURFACE="all"
OUTPUT_DIR=""

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
SQL_DIR="$SCRIPT_DIR/sql/postgres-storage-readiness"

usage() {
    cat <<'EOF'
Usage: tools/postgres-storage-ownership-readiness.sh --output DIR [options]

Captures read-only ownership manifests for storage-retirement candidates and
copies the parity-gated future maintenance SQL into the same evidence package.
It never starts workers or mutates production schema, data, indexes, or config.

Options:
  --output DIR       Required evidence directory under /mnt/docker-storage
  --surface NAME     all, observations, dirty, or legacy (default: all)
  --pg-container N   PostgreSQL container (default: fst-postgres)
  --compose-dir DIR  Production compose directory
  -h, --help         Show this help
EOF
}

while [[ $# -gt 0 ]]; do
    case "$1" in
        --output) OUTPUT_DIR="$2"; shift 2 ;;
        --surface) SURFACE="$2"; shift 2 ;;
        --pg-container) PG_CONTAINER="$2"; shift 2 ;;
        --compose-dir) COMPOSE_DIR="$2"; shift 2 ;;
        -h|--help) usage; exit 0 ;;
        *) printf 'Unknown option: %s\n\n' "$1" >&2; usage >&2; exit 64 ;;
    esac
done

if [[ -z "$OUTPUT_DIR" ]]; then
    printf 'ERROR: --output is required\n' >&2
    exit 64
fi

case "$SURFACE" in
    all|observations|dirty|legacy) ;;
    *) printf 'ERROR: unsupported surface: %s\n' "$SURFACE" >&2; exit 64 ;;
esac

for command in docker realpath sha256sum; do
    command -v "$command" >/dev/null 2>&1 || {
        printf 'ERROR: required command not found: %s\n' "$command" >&2
        exit 1
    }
done

output_dir="$(realpath -m "$OUTPUT_DIR")"
storage_root="$(realpath -m "$FST_STORAGE_ROOT")"
case "$output_dir/" in
    "$storage_root/"*) ;;
    *)
        printf 'ERROR: output must remain on the FST storage root: %s\n' "$storage_root" >&2
        exit 64
        ;;
esac

[[ -d "$COMPOSE_DIR" ]] || {
    printf 'ERROR: compose directory not found: %s\n' "$COMPOSE_DIR" >&2
    exit 1
}
[[ -d "$SQL_DIR" ]] || {
    printf 'ERROR: SQL package directory not found: %s\n' "$SQL_DIR" >&2
    exit 1
}

mkdir -p "$output_dir/sql"

psql_cmd() {
    docker exec -i "$PG_CONTAINER" psql \
        -X -v ON_ERROR_STOP=1 -U "$PG_USER" -d "$PG_DB" -P pager=off "$@"
}

preflight="$(
    docker exec "$PG_CONTAINER" psql \
        -X -v ON_ERROR_STOP=1 -U "$PG_USER" -d "$PG_DB" -AtF '|' \
        -c "
            SELECT
                COALESCE((
                    SELECT MAX(id)
                    FROM scrape_log
                    WHERE completed_at IS NULL
                      AND COALESCE(to_jsonb(scrape_log)->>'status', 'running') = 'running'
                ), 0),
                (SELECT COUNT(*) FROM pg_locks WHERE NOT granted),
                (SELECT COUNT(*) FROM pg_stat_progress_vacuum),
                (SELECT COUNT(*) FROM pg_stat_progress_create_index),
                COALESCE((SELECT published_scrape_id FROM scrape_publication_state WHERE id = TRUE), 0),
                COALESCE((SELECT public_reads_frozen FROM scrape_publication_state WHERE id = TRUE), FALSE);
        "
)"
IFS='|' read -r active_scrape ungranted_locks active_vacuums active_indexes published_scrape public_frozen <<< "$preflight"

if (( active_scrape != 0 || ungranted_locks != 0 || active_vacuums != 0 || active_indexes != 0 )); then
    printf 'ERROR: unsafe live state: scrape=%s locks=%s vacuums=%s indexes=%s\n' \
        "$active_scrape" "$ungranted_locks" "$active_vacuums" "$active_indexes" >&2
    exit 3
fi

{
    printf 'captured_at=%s\n' "$(date -u +%Y-%m-%dT%H:%M:%SZ)"
    printf 'surface=%s\n' "$SURFACE"
    printf 'published_scrape_id=%s\n' "$published_scrape"
    printf 'public_reads_frozen=%s\n' "$public_frozen"
    printf 'active_scrape_id=%s\n' "$active_scrape"
    printf 'ungranted_locks=%s\n' "$ungranted_locks"
    printf 'active_vacuums=%s\n' "$active_vacuums"
    printf 'active_index_builds=%s\n' "$active_indexes"
} > "$output_dir/preflight.txt"

"$SCRIPT_DIR/postgres-capacity-guard.sh" \
    --action-class observation \
    --compose-dir "$COMPOSE_DIR" \
    --pg-container "$PG_CONTAINER" \
    --fst-storage-root "$FST_STORAGE_ROOT" \
    --output "$output_dir/capacity-guard.json" \
    > "$output_dir/capacity-guard.stdout.json"

run_manifest() {
    local name="$1"
    psql_cmd < "$SQL_DIR/$name-manifest.sql" > "$output_dir/$name-manifest.txt"
}

case "$SURFACE" in
    all)
        run_manifest observations
        run_manifest dirty
        run_manifest legacy
        ;;
    observations) run_manifest observations ;;
    dirty) run_manifest dirty ;;
    legacy) run_manifest legacy ;;
esac

cp "$SQL_DIR"/*-truncate.sql "$output_dir/sql/"
cp "$SQL_DIR"/*-rehydrate.sql "$output_dir/sql/"
cp "$SQL_DIR"/*-rebuild.sql "$output_dir/sql/"
cp "$SQL_DIR"/*-drop.sql "$output_dir/sql/"

(
    cd "$output_dir"
    sha256sum preflight.txt capacity-guard.json *-manifest.txt sql/*.sql \
        > package-checksums.sha256
)

printf 'Storage ownership evidence: %s\n' "$output_dir"
