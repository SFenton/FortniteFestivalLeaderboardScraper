#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd -P)"
REPO_ROOT="$(cd -- "$SCRIPT_DIR/.." && pwd -P)"

SOURCE_CONTAINER="${SOURCE_CONTAINER:-fst-postgres}"
SOURCE_USER="${SOURCE_USER:-fst}"
SOURCE_DB="${SOURCE_DB:-fstservice}"
POSTGRES_IMAGE="${POSTGRES_IMAGE:-fst-postgres:17-repack}"
API_BASE="${API_BASE:-http://127.0.0.1:3001}"
FST_STORAGE_ROOT="${FST_STORAGE_ROOT:-/mnt/docker-storage}"
FST_ARTIFACT_ROOT="${FST_ARTIFACT_ROOT:-/mnt/docker-storage/Docker/FestivalServiceTracker/fst-data/autonomous-artifacts}"
OUT_DIR="${OUT_DIR:-$FST_ARTIFACT_ROOT/postgres-bounded-restore-$(date -u +%Y%m%dT%H%M%SZ)}"
SCRAPE_ID="${SCRAPE_ID:-}"
TARGET_CONTAINER="${TARGET_CONTAINER:-fst-postgres-restore-drill-$$}"

usage() {
    cat <<'EOF'
Usage: tools/postgres-bounded-restore-drill.sh [options]

Creates a schema plus representative scrape/history backup on the FST drive,
restores it into an isolated same-drive PostgreSQL container, and verifies
exact CSV and representative API/SQL parity.

Options:
  --out-dir DIR       New same-drive artifact directory
  --scrape-id ID      Completed scrape to represent (default: published scrape)
  --api-base URL      Festivalweb base URL
  --source-container  Production PostgreSQL container
  --postgres-image    Restore PostgreSQL image
  -h, --help          Show this help

The restored container uses no published ports and Docker network mode `none`.
No source schema, data, publication state, or container is mutated.
EOF
}

while [[ $# -gt 0 ]]; do
    case "$1" in
        --out-dir) OUT_DIR="$2"; shift 2 ;;
        --scrape-id) SCRAPE_ID="$2"; shift 2 ;;
        --api-base) API_BASE="${2%/}"; shift 2 ;;
        --source-container) SOURCE_CONTAINER="$2"; shift 2 ;;
        --postgres-image) POSTGRES_IMAGE="$2"; shift 2 ;;
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

for command in awk curl date docker find git python3 realpath sed sha256sum sleep; do
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
        printf 'ERROR: restore artifacts must stay under FST storage root %s: %s\n' \
            "$fst_storage_root" "$OUT_DIR" >&2
        exit 1
        ;;
esac

if [[ -e "$OUT_DIR" && -n "$(find "$OUT_DIR" -mindepth 1 -maxdepth 1 -print -quit 2>/dev/null)" ]]; then
    printf 'ERROR: output directory is not empty: %s\n' "$OUT_DIR" >&2
    exit 1
fi

BACKUP_DIR="$OUT_DIR/backup"
SOURCE_DATA_DIR="$BACKUP_DIR/data"
RESTORED_DATA_DIR="$OUT_DIR/restored-data"
TARGET_PGDATA="$OUT_DIR/target-pgdata"
mkdir -p "$SOURCE_DATA_DIR" "$RESTORED_DATA_DIR" "$TARGET_PGDATA"

cleanup() {
    docker rm -f "$TARGET_CONTAINER" >/dev/null 2>&1 || true
}
trap cleanup EXIT

source_scalar() {
    docker exec "$SOURCE_CONTAINER" psql \
        -X -v ON_ERROR_STOP=1 -U "$SOURCE_USER" -d "$SOURCE_DB" -At \
        -c "$1"
}

source_csv() {
    local sql="$1"
    local output="$2"
    printf 'COPY (%s) TO STDOUT WITH (FORMAT CSV, HEADER TRUE);\n' "$sql" |
        docker exec -i "$SOURCE_CONTAINER" psql \
            -X -v ON_ERROR_STOP=1 -U "$SOURCE_USER" -d "$SOURCE_DB" \
            > "$output"
}

target_csv() {
    local sql="$1"
    local output="$2"
    printf 'COPY (%s) TO STDOUT WITH (FORMAT CSV, HEADER TRUE);\n' "$sql" |
        docker exec -i "$TARGET_CONTAINER" psql \
            -X -v ON_ERROR_STOP=1 -U fst -d fstservice \
            > "$output"
}

csv_row_count() {
    python3 - "$1" <<'PY'
import csv
import sys

with open(sys.argv[1], newline="", encoding="utf-8") as handle:
    print(max(0, sum(1 for _ in csv.reader(handle)) - 1))
PY
}

sql_quote() {
    local value="${1//\'/\'\'}"
    printf "'%s'" "$value"
}

docker exec "$SOURCE_CONTAINER" pg_isready -U "$SOURCE_USER" -d "$SOURCE_DB" >/dev/null
"$SCRIPT_DIR/postgres-capacity-guard.sh" \
    --action-class observation \
    --pg-container "$SOURCE_CONTAINER" \
    --output "$OUT_DIR/capacity-before.json" \
    >/dev/null

if [[ -z "$SCRAPE_ID" ]]; then
    SCRAPE_ID="$(
        source_scalar "
            SELECT COALESCE(
                (SELECT published_scrape_id FROM scrape_publication_state WHERE id = TRUE),
                (SELECT id FROM scrape_log WHERE completed_at IS NOT NULL ORDER BY id DESC LIMIT 1),
                0
            );
        "
    )"
fi
if [[ "$SCRAPE_ID" == "0" ]]; then
    printf 'ERROR: no published or completed scrape is available\n' >&2
    exit 1
fi

scrape_is_complete="$(source_scalar "SELECT completed_at IS NOT NULL FROM scrape_log WHERE id = $SCRAPE_ID;")"
if [[ "$scrape_is_complete" != "t" ]]; then
    printf 'ERROR: scrape %s is not complete\n' "$SCRAPE_ID" >&2
    exit 1
fi

solo_sample="$(
    source_scalar "
        SELECT song_id || '|' || instrument || '|' || source_snapshot_id || '|' || projection_generation
        FROM solo_current_projection_scope
        WHERE status = 'ready'
          AND row_count > 0
          AND source_snapshot_id IS NOT NULL
        ORDER BY row_count DESC, song_id, instrument
        LIMIT 1;
    "
)"
IFS='|' read -r solo_song solo_instrument solo_snapshot solo_generation <<< "$solo_sample"
solo_account="$(
    source_scalar "
        SELECT account_id
        FROM current_leaderboard_entries
        WHERE song_id = $(sql_quote "$solo_song")
          AND instrument = $(sql_quote "$solo_instrument")
        ORDER BY rank, account_id
        LIMIT 1;
    "
)"

band_sample="$(
    source_scalar "
        SELECT song_id || '|' || band_type || '|' || ranking_scope || '|' || scope_combo_id || '|' || projection_generation
        FROM band_current_projection_scope
        WHERE status = 'ready'
          AND row_count > 0
        ORDER BY row_count DESC, song_id, band_type
        LIMIT 1;
    "
)"
IFS='|' read -r band_song band_type band_scope band_combo band_generation <<< "$band_sample"
band_team="$(
    source_scalar "
        SELECT team_key
        FROM current_band_leaderboard_entries
        WHERE song_id = $(sql_quote "$band_song")
          AND band_type = $(sql_quote "$band_type")
          AND ranking_scope = $(sql_quote "$band_scope")
          AND scope_combo_id = $(sql_quote "$band_combo")
          AND projection_generation = $band_generation
        ORDER BY rank, team_key
        LIMIT 1;
    "
)"

printf '%s\n' "$SCRAPE_ID" > "$OUT_DIR/scrape-id.txt"
printf '%s|%s|%s|%s|%s\n' \
    "$solo_song" "$solo_instrument" "$solo_snapshot" "$solo_generation" "$solo_account" \
    > "$OUT_DIR/solo-sample.txt"
printf '%s|%s|%s|%s|%s|%s\n' \
    "$band_song" "$band_type" "$band_scope" "$band_combo" "$band_generation" "$band_team" \
    > "$OUT_DIR/band-sample.txt"
date -u +%Y-%m-%dT%H:%M:%SZ > "$OUT_DIR/started-at-utc.txt"
git -C "$REPO_ROOT" rev-parse HEAD > "$OUT_DIR/git-commit.txt"

schema_started_ns="$(date +%s%N)"
docker exec "$SOURCE_CONTAINER" pg_dump \
    -U "$SOURCE_USER" -d "$SOURCE_DB" \
    --schema-only --no-owner --no-privileges \
    > "$BACKUP_DIR/schema.sql" \
    2> "$OUT_DIR/schema-backup.stderr.txt"
schema_completed_ns="$(date +%s%N)"

declare -a DATASET_NAMES=()
declare -a DATASET_TABLES=()
declare -a DATASET_QUERIES=()

add_dataset() {
    DATASET_NAMES+=("$1")
    DATASET_TABLES+=("$2")
    DATASET_QUERIES+=("$3")
}

add_dataset "data-version" "data_version" \
    "SELECT * FROM data_version ORDER BY 1"
add_dataset "scrape-log" "scrape_log" \
    "SELECT * FROM scrape_log ORDER BY id"
add_dataset "scrape-publication-state" "scrape_publication_state" \
    "SELECT * FROM scrape_publication_state ORDER BY id"
add_dataset "songs" "songs" \
    "SELECT * FROM songs ORDER BY song_id"
add_dataset "season-windows" "season_windows" \
    "SELECT * FROM season_windows ORDER BY season_number"
add_dataset "song-first-seen-season" "song_first_seen_season" \
    "SELECT * FROM song_first_seen_season ORDER BY song_id"
add_dataset "instrument-scrape-state" "instrument_scrape_state" \
    "SELECT * FROM instrument_scrape_state ORDER BY instrument"
add_dataset "scope-fingerprints" "leaderboard_scope_fingerprints" \
    "SELECT * FROM leaderboard_scope_fingerprints ORDER BY instrument, song_id, scope_kind"
add_dataset "logical-write-metrics" "leaderboard_logical_write_metrics" \
    "SELECT * FROM leaderboard_logical_write_metrics WHERE scrape_id = $SCRAPE_ID ORDER BY instrument"
add_dataset "phase-timings" "scrape_phase_timings" \
    "SELECT * FROM scrape_phase_timings WHERE scrape_id = $SCRAPE_ID ORDER BY id"
add_dataset "leaderboard-population" "leaderboard_population" \
    "SELECT * FROM leaderboard_population ORDER BY instrument, song_id"
add_dataset "song-stats" "song_stats" \
    "SELECT * FROM song_stats ORDER BY instrument, song_id"
add_dataset "snapshot-state" "leaderboard_snapshot_state" \
    "SELECT * FROM leaderboard_snapshot_state ORDER BY instrument, song_id"
add_dataset "solo-projection-state" "solo_current_projection_state" \
    "SELECT * FROM solo_current_projection_state ORDER BY id"
add_dataset "solo-projection-scope" "solo_current_projection_scope" \
    "SELECT * FROM solo_current_projection_scope
     WHERE song_id = $(sql_quote "$solo_song") AND instrument = $(sql_quote "$solo_instrument")
     ORDER BY song_id, instrument"
add_dataset "solo-snapshot" "leaderboard_entries_snapshot" \
    "SELECT * FROM leaderboard_entries_snapshot
     WHERE snapshot_id = $solo_snapshot
       AND song_id = $(sql_quote "$solo_song")
       AND instrument = $(sql_quote "$solo_instrument")
     ORDER BY account_id"
add_dataset "solo-current-projection" "current_leaderboard_entries" \
    "SELECT * FROM current_leaderboard_entries
     WHERE song_id = $(sql_quote "$solo_song")
       AND instrument = $(sql_quote "$solo_instrument")
     ORDER BY account_id"
add_dataset "solo-legacy-current" "leaderboard_entries" \
    "SELECT * FROM leaderboard_entries
     WHERE song_id = $(sql_quote "$solo_song")
       AND instrument = $(sql_quote "$solo_instrument")
     ORDER BY account_id"
add_dataset "solo-overlay" "leaderboard_entries_overlay" \
    "SELECT * FROM leaderboard_entries_overlay
     WHERE song_id = $(sql_quote "$solo_song")
       AND instrument = $(sql_quote "$solo_instrument")
     ORDER BY account_id, source_priority"
add_dataset "solo-logical-current" "leaderboard_current_entries" \
    "SELECT * FROM leaderboard_current_entries
     WHERE song_id = $(sql_quote "$solo_song")
       AND instrument = $(sql_quote "$solo_instrument")
     ORDER BY account_id"
add_dataset "solo-logical-versions" "leaderboard_entry_versions" \
    "SELECT * FROM leaderboard_entry_versions
     WHERE song_id = $(sql_quote "$solo_song")
       AND instrument = $(sql_quote "$solo_instrument")
     ORDER BY account_id, valid_from_scrape_id
     LIMIT 30000"
add_dataset "score-history" "score_history" \
    "SELECT * FROM score_history
     WHERE account_id = $(sql_quote "$solo_account")
     ORDER BY id
     LIMIT 500"
add_dataset "rank-history" "rank_history" \
    "SELECT * FROM rank_history
     WHERE account_id = $(sql_quote "$solo_account")
       AND instrument = $(sql_quote "$solo_instrument")
     ORDER BY snapshot_date, account_id
     LIMIT 500"
add_dataset "composite-rank-history" "composite_rank_history" \
    "SELECT * FROM composite_rank_history
     WHERE account_id = $(sql_quote "$solo_account")
     ORDER BY snapshot_date, account_id
     LIMIT 500"
add_dataset "account-names" "account_names" \
    "SELECT names.*
     FROM account_names names
     WHERE names.account_id IN (
         SELECT account_id
         FROM current_leaderboard_entries
         WHERE song_id = $(sql_quote "$solo_song")
           AND instrument = $(sql_quote "$solo_instrument")
         ORDER BY rank, account_id
         LIMIT 10
     )
     ORDER BY names.account_id"
add_dataset "band-projection-state" "band_current_projection_state" \
    "SELECT * FROM band_current_projection_state ORDER BY id"
add_dataset "band-projection-scope" "band_current_projection_scope" \
    "SELECT * FROM band_current_projection_scope
     WHERE song_id = $(sql_quote "$band_song")
       AND band_type = $(sql_quote "$band_type")
       AND ranking_scope = $(sql_quote "$band_scope")
       AND scope_combo_id = $(sql_quote "$band_combo")
     ORDER BY song_id, band_type, ranking_scope, scope_combo_id"
add_dataset "band-current-projection" "current_band_leaderboard_entries" \
    "SELECT * FROM current_band_leaderboard_entries
     WHERE song_id = $(sql_quote "$band_song")
       AND band_type = $(sql_quote "$band_type")
       AND ranking_scope = $(sql_quote "$band_scope")
       AND scope_combo_id = $(sql_quote "$band_combo")
       AND projection_generation = $band_generation
     ORDER BY rank, team_key
     LIMIT 100"
add_dataset "band-source-entries" "band_entries" \
    "SELECT source.*
     FROM band_entries source
     WHERE source.song_id = $(sql_quote "$band_song")
       AND source.band_type = $(sql_quote "$band_type")
       AND source.team_key IN (
           SELECT team_key
           FROM current_band_leaderboard_entries
           WHERE song_id = $(sql_quote "$band_song")
             AND band_type = $(sql_quote "$band_type")
             AND ranking_scope = $(sql_quote "$band_scope")
             AND scope_combo_id = $(sql_quote "$band_combo")
             AND projection_generation = $band_generation
           ORDER BY rank, team_key
           LIMIT 100
       )
     ORDER BY source.team_key, source.instrument_combo"
add_dataset "band-history-snapshots-v2" "band_team_rank_history_snapshot_v2" \
    "SELECT snapshot.*
     FROM band_team_rank_history_snapshot_v2 snapshot
     WHERE snapshot.snapshot_id IN (
         SELECT snapshot_id
         FROM band_team_rank_history_points_v2
         WHERE band_type = $(sql_quote "$band_type")
           AND team_key = $(sql_quote "$band_team")
         ORDER BY snapshot_date DESC
         LIMIT 500
     )
     ORDER BY snapshot.snapshot_id"
add_dataset "band-history-points-v2" "band_team_rank_history_points_v2" \
    "SELECT *
     FROM band_team_rank_history_points_v2
     WHERE band_type = $(sql_quote "$band_type")
       AND team_key = $(sql_quote "$band_team")
     ORDER BY snapshot_date, ranking_scope, combo_id, team_key
     LIMIT 500"
add_dataset "band-history-latest-v2" "band_team_rank_history_latest_v2" \
    "SELECT *
     FROM band_team_rank_history_latest_v2
     WHERE band_type = $(sql_quote "$band_type")
       AND team_key = $(sql_quote "$band_team")
     ORDER BY ranking_scope, combo_id, team_key"

data_export_started_ns="$(date +%s%N)"
for index in "${!DATASET_NAMES[@]}"; do
    source_csv "${DATASET_QUERIES[$index]}" "$SOURCE_DATA_DIR/${DATASET_NAMES[$index]}.csv"
done
data_export_completed_ns="$(date +%s%N)"

curl --max-time 30 -fsS \
    "$API_BASE/api/leaderboard/$solo_song/$solo_instrument?top=10" \
    -o "$BACKUP_DIR/solo-api-fixture.json"
curl --max-time 30 -fsS \
    "$API_BASE/api/leaderboard/$band_song/bands/$band_type?top=10" \
    -o "$BACKUP_DIR/band-api-fixture.json"

sha256sum "$BACKUP_DIR/schema.sql" "$SOURCE_DATA_DIR"/*.csv "$BACKUP_DIR"/*-api-fixture.json \
    > "$BACKUP_DIR/source-sha256.txt"

docker run -d \
    --name "$TARGET_CONTAINER" \
    --network none \
    -e POSTGRES_USER=fst \
    -e POSTGRES_DB=fstservice \
    -e POSTGRES_HOST_AUTH_METHOD=trust \
    -v "$TARGET_PGDATA:/var/lib/postgresql/data" \
    -v "$BACKUP_DIR:/restore:ro" \
    "$POSTGRES_IMAGE" \
    > "$OUT_DIR/target-container-id.txt"

for _ in {1..120}; do
    if docker exec "$TARGET_CONTAINER" pg_isready -U fst -d fstservice >/dev/null 2>&1; then
        break
    fi
    sleep 1
done
docker exec "$TARGET_CONTAINER" pg_isready -U fst -d fstservice >/dev/null

restore_started_ns="$(date +%s%N)"
docker exec -i "$TARGET_CONTAINER" psql \
    -X -v ON_ERROR_STOP=1 -U fst -d fstservice \
    < "$BACKUP_DIR/schema.sql" \
    > "$OUT_DIR/schema-restore.stdout.txt" \
    2> "$OUT_DIR/schema-restore.stderr.txt"

for index in "${!DATASET_NAMES[@]}"; do
    docker exec "$TARGET_CONTAINER" psql \
        -X -v ON_ERROR_STOP=1 -U fst -d fstservice \
        -c "\\copy ${DATASET_TABLES[$index]} FROM '/restore/data/${DATASET_NAMES[$index]}.csv' WITH (FORMAT csv, HEADER true)" \
        >> "$OUT_DIR/data-restore.stdout.txt" \
        2>> "$OUT_DIR/data-restore.stderr.txt"
done

docker exec "$TARGET_CONTAINER" psql \
    -X -v ON_ERROR_STOP=1 -U fst -d fstservice \
    -c "
        SELECT setval(pg_get_serial_sequence('scrape_log', 'id'), GREATEST(COALESCE((SELECT MAX(id) FROM scrape_log), 1), 1), true);
        SELECT setval(pg_get_serial_sequence('scrape_phase_timings', 'id'), GREATEST(COALESCE((SELECT MAX(id) FROM scrape_phase_timings), 1), 1), true);
        SELECT setval(pg_get_serial_sequence('score_history', 'id'), GREATEST(COALESCE((SELECT MAX(id) FROM score_history), 1), 1), true);
    " \
    >> "$OUT_DIR/data-restore.stdout.txt"
restore_completed_ns="$(date +%s%N)"

printf 'dataset,table,source_rows,target_rows,source_sha256,target_sha256,exact_match\n' \
    > "$OUT_DIR/dataset-parity.csv"
parity_failed=0
for index in "${!DATASET_NAMES[@]}"; do
    name="${DATASET_NAMES[$index]}"
    target_csv "${DATASET_QUERIES[$index]}" "$RESTORED_DATA_DIR/$name.csv"
    source_rows="$(csv_row_count "$SOURCE_DATA_DIR/$name.csv")"
    target_rows="$(csv_row_count "$RESTORED_DATA_DIR/$name.csv")"
    source_sha="$(sha256sum "$SOURCE_DATA_DIR/$name.csv" | awk '{print $1}')"
    target_sha="$(sha256sum "$RESTORED_DATA_DIR/$name.csv" | awk '{print $1}')"
    exact_match=false
    if [[ "$source_rows" == "$target_rows" && "$source_sha" == "$target_sha" ]]; then
        exact_match=true
    else
        parity_failed=1
    fi
    printf '%s,%s,%s,%s,%s,%s,%s\n' \
        "$name" "${DATASET_TABLES[$index]}" "$source_rows" "$target_rows" \
        "$source_sha" "$target_sha" "$exact_match" \
        >> "$OUT_DIR/dataset-parity.csv"
done

target_csv "
    SELECT
        account_id,
        score,
        rank,
        accuracy,
        is_full_combo,
        stars,
        difficulty,
        season,
        percentile,
        source
    FROM current_leaderboard_entries
    WHERE song_id = $(sql_quote "$solo_song")
      AND instrument = $(sql_quote "$solo_instrument")
    ORDER BY rank, account_id
    LIMIT 10
" "$OUT_DIR/solo-target-api-data.csv"

target_csv "
    SELECT
        team_key,
        entry_combo_id,
        score,
        rank,
        accuracy,
        is_full_combo,
        stars,
        difficulty,
        season,
        percentile
    FROM current_band_leaderboard_entries
    WHERE song_id = $(sql_quote "$band_song")
      AND band_type = $(sql_quote "$band_type")
      AND ranking_scope = $(sql_quote "$band_scope")
      AND scope_combo_id = $(sql_quote "$band_combo")
      AND projection_generation = $band_generation
    ORDER BY rank, team_key
    LIMIT 10
" "$OUT_DIR/band-target-api-data.csv"

python3 - "$OUT_DIR" <<'PY'
import csv
import json
import sys
from pathlib import Path

root = Path(sys.argv[1])
solo_api = json.loads((root / "backup/solo-api-fixture.json").read_text(encoding="utf-8"))
band_api = json.loads((root / "backup/band-api-fixture.json").read_text(encoding="utf-8"))

with (root / "solo-target-api-data.csv").open(newline="", encoding="utf-8") as handle:
    solo_rows = list(csv.DictReader(handle))
with (root / "band-target-api-data.csv").open(newline="", encoding="utf-8") as handle:
    band_rows = list(csv.DictReader(handle))

def boolean(value):
    return value.lower() in {"t", "true", "1"}

solo_expected = [
    {
        "accountId": row["account_id"],
        "score": int(row["score"]),
        "rank": int(row["rank"]),
        "accuracy": int(row["accuracy"]) if row["accuracy"] else None,
        "isFullCombo": boolean(row["is_full_combo"]) if row["is_full_combo"] else None,
        "stars": int(row["stars"]) if row["stars"] else None,
        "difficulty": int(row["difficulty"]) if row["difficulty"] else None,
        "season": int(row["season"]) if row["season"] else None,
        "source": row["source"],
    }
    for row in solo_rows
]
solo_actual = [
    {key: entry.get(key) for key in solo_expected[0]}
    for entry in solo_api.get("entries", [])
] if solo_expected else []

band_expected = [
    {
        "teamKey": row["team_key"],
        "comboId": row["entry_combo_id"],
        "score": int(row["score"]),
        "rank": int(row["rank"]),
        "accuracy": int(row["accuracy"]) if row["accuracy"] else None,
        "isFullCombo": boolean(row["is_full_combo"]) if row["is_full_combo"] else None,
        "stars": int(row["stars"]) if row["stars"] else None,
        "difficulty": int(row["difficulty"]) if row["difficulty"] else None,
        "season": int(row["season"]) if row["season"] else None,
    }
    for row in band_rows
]
band_actual = [
    {key: entry.get(key) for key in band_expected[0]}
    for entry in band_api.get("entries", [])
] if band_expected else []

result = {
    "solo": {
        "routeCount": len(solo_api.get("entries", [])),
        "restoredCount": len(solo_rows),
        "exactSelectedFieldParity": bool(solo_expected) and solo_actual == solo_expected,
    },
    "band": {
        "routeCount": len(band_api.get("entries", [])),
        "restoredCount": len(band_rows),
        "exactSelectedFieldParity": bool(band_expected) and band_actual == band_expected,
    },
}
(root / "api-fixture-parity.json").write_text(json.dumps(result, indent=2) + "\n", encoding="utf-8")
if not result["solo"]["exactSelectedFieldParity"] or not result["band"]["exactSelectedFieldParity"]:
    raise SystemExit("representative API fixture parity failed")
PY

target_database_bytes="$(
    docker exec "$TARGET_CONTAINER" psql \
        -X -v ON_ERROR_STOP=1 -U fst -d fstservice -At \
        -c "SELECT pg_database_size(current_database());"
)"
target_pgdata_bytes="$(
    docker exec "$TARGET_CONTAINER" sh -c 'du -sb /var/lib/postgresql/data | cut -f1'
)"
printf '%s\n' "$target_pgdata_bytes" > "$OUT_DIR/target-pgdata-retained-bytes.txt"

docker stats --no-stream \
    --format '{{.Name}},{{.CPUPerc}},{{.MemUsage}},{{.MemPerc}},{{.BlockIO}},{{.PIDs}}' \
    "$TARGET_CONTAINER" \
    > "$OUT_DIR/target-docker-stats.csv"
sed -i '1i container,cpu_percent,memory_usage,memory_percent,block_io,pids' \
    "$OUT_DIR/target-docker-stats.csv"

docker rm -f "$TARGET_CONTAINER" >/dev/null
trap - EXIT

"$SCRIPT_DIR/postgres-capacity-guard.sh" \
    --action-class observation \
    --pg-container "$SOURCE_CONTAINER" \
    --output "$OUT_DIR/capacity-after.json" \
    >/dev/null

python3 - \
    "$OUT_DIR" \
    "$schema_started_ns" "$schema_completed_ns" \
    "$data_export_started_ns" "$data_export_completed_ns" \
    "$restore_started_ns" "$restore_completed_ns" \
    "$target_database_bytes" "$target_pgdata_bytes" "$parity_failed" <<'PY'
import csv
import hashlib
import json
import math
import sys
from pathlib import Path

(
    out_dir,
    schema_started_ns,
    schema_completed_ns,
    export_started_ns,
    export_completed_ns,
    restore_started_ns,
    restore_completed_ns,
    target_database_bytes,
    target_pgdata_bytes,
    parity_failed,
) = sys.argv[1:]
root = Path(out_dir)
before = json.loads((root / "capacity-before.json").read_text(encoding="utf-8"))
after = json.loads((root / "capacity-after.json").read_text(encoding="utf-8"))

db_bytes = before["storage"]["databaseBytes"]
wal_bytes = before["storage"]["walDirectoryBytes"]
free_bytes = before["storage"]["filesystemFreeBytes"]
ten_percent = math.ceil(db_bytes * 0.10)
streaming_additional = db_bytes + wal_bytes + ten_percent
durable_backup_additional = db_bytes * 2 + wal_bytes + ten_percent

with (root / "dataset-parity.csv").open(newline="", encoding="utf-8") as handle:
    parity_rows = list(csv.DictReader(handle))
api_parity = json.loads((root / "api-fixture-parity.json").read_text(encoding="utf-8"))

backup_bytes = sum(path.stat().st_size for path in (root / "backup").rglob("*") if path.is_file())
source_data_bytes = sum(path.stat().st_size for path in (root / "backup/data").glob("*.csv"))
result = {
    "decision": "accepted" if int(parity_failed) == 0 else "rejected",
    "datasetCount": len(parity_rows),
    "datasetParityPassed": all(row["exact_match"] == "true" for row in parity_rows),
    "apiFixtureParityPassed": all(
        item["exactSelectedFieldParity"] for item in api_parity.values()
    ),
    "backupBytes": backup_bytes,
    "sourceDataBytes": source_data_bytes,
    "targetDatabaseBytes": int(target_database_bytes),
    "targetPgDataBytes": int(target_pgdata_bytes),
    "timingsSeconds": {
        "schemaBackup": round((int(schema_completed_ns) - int(schema_started_ns)) / 1e9, 3),
        "dataExport": round((int(export_completed_ns) - int(export_started_ns)) / 1e9, 3),
        "restore": round((int(restore_completed_ns) - int(restore_started_ns)) / 1e9, 3),
    },
    "fullRestoreHeadroom": {
        "sourceDatabaseBytes": db_bytes,
        "sourceWalDirectoryBytes": wal_bytes,
        "restoreSafetyBytes": ten_percent,
        "streamingAdditionalBytesRequired": streaming_additional,
        "streamingShortfallBytes": max(0, streaming_additional - free_bytes),
        "durableBackupAndRestoreAdditionalBytesRequired": durable_backup_additional,
        "durableBackupAndRestoreShortfallBytes": max(0, durable_backup_additional - free_bytes),
        "currentFreeBytes": free_bytes,
        "fullDuplicateRestoreFitsNow": free_bytes >= streaming_additional,
    },
    "filesystemFreeBytesAfter": after["storage"]["filesystemFreeBytes"],
    "restoredDataRetainedAt": str(root / "target-pgdata"),
}
(root / "summary.json").write_text(json.dumps(result, indent=2) + "\n", encoding="utf-8")

manifest_files = []
for path in sorted(root.rglob("*")):
    if path.is_file() and path.name not in {"manifest.json", "report.md"}:
        manifest_files.append({
            "path": path.relative_to(root).as_posix(),
            "bytes": path.stat().st_size,
            "sha256": hashlib.sha256(path.read_bytes()).hexdigest(),
        })
(root / "manifest.json").write_text(json.dumps({"files": manifest_files}, indent=2) + "\n", encoding="utf-8")

report = [
    "# PostgreSQL Bounded Backup and Restore Drill",
    "",
    f"- Decision: `{result['decision']}`",
    f"- Dataset parity: `{result['datasetParityPassed']}` across `{result['datasetCount']}` datasets",
    f"- Representative API fixture parity: `{result['apiFixtureParityPassed']}`",
    f"- Backup bytes: `{result['backupBytes']}`",
    f"- Restored database bytes: `{result['targetDatabaseBytes']}`",
    f"- Restored PGDATA bytes: `{result['targetPgDataBytes']}`",
    f"- Schema backup seconds: `{result['timingsSeconds']['schemaBackup']}`",
    f"- Data export seconds: `{result['timingsSeconds']['dataExport']}`",
    f"- Restore seconds: `{result['timingsSeconds']['restore']}`",
    f"- Current free bytes: `{free_bytes}`",
    f"- Streaming full-restore additional bytes required: `{streaming_additional}`",
    f"- Streaming full-restore shortfall bytes: `{result['fullRestoreHeadroom']['streamingShortfallBytes']}`",
    f"- Durable backup plus restore additional bytes required: `{durable_backup_additional}`",
    f"- Full duplicate restore fits now: `{result['fullRestoreHeadroom']['fullDuplicateRestoreFitsNow']}`",
    "",
    "The isolated target used Docker network mode `none`, no published ports, and a bind-mounted PGDATA directory on the FST drive.",
    "The restored PGDATA is retained with the checksummed evidence for inspection; the drill container was removed.",
]
(root / "report.md").write_text("\n".join(report) + "\n", encoding="utf-8")
PY

if [[ "$parity_failed" != "0" ]]; then
    printf 'ERROR: one or more restored datasets failed exact parity\n' >&2
    exit 1
fi

printf 'PostgreSQL bounded restore drill: %s\n' "$OUT_DIR/report.md"
