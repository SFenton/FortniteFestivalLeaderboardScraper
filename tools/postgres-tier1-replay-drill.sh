#!/usr/bin/env bash
set -euo pipefail

usage() {
  cat <<'EOF'
Usage: tools/postgres-tier1-replay-drill.sh \
  --root <approved-fst-evidence-root> \
  --fixture-dir <synthetic-tier1-fixture> \
  --baseline-image <fstservice-image> \
  --candidate-image <fstservice-image>
EOF
}

root=""
fixture_dir=""
baseline_image=""
candidate_image=""

while (($#)); do
  case "$1" in
    --root) root=${2:-}; shift 2 ;;
    --fixture-dir) fixture_dir=${2:-}; shift 2 ;;
    --baseline-image) baseline_image=${2:-}; shift 2 ;;
    --candidate-image) candidate_image=${2:-}; shift 2 ;;
    -h|--help) usage; exit 0 ;;
    *) echo "Unknown option: $1" >&2; usage >&2; exit 64 ;;
  esac
done

[[ -n "$root" && -n "$fixture_dir" && -n "$baseline_image" && -n "$candidate_image" ]] || {
  usage >&2
  exit 64
}

python3 - "$root" <<'PY'
import os
import sys
import unicodedata

path = sys.argv[1]
if not os.path.isabs(path):
    raise SystemExit("Replay drill root must be absolute.")
if unicodedata.normalize("NFC", path) != path:
    raise SystemExit("Replay drill root must use NFC normalization.")
segments = [segment for segment in path.replace("\\", "/").split("/") if segment]
if any(segment == ".." for segment in segments):
    raise SystemExit("Replay drill root cannot contain traversal.")
if any(segment.casefold() in {"pgdata", "pg_wal"} for segment in segments):
    raise SystemExit("Replay drill root cannot use PostgreSQL data path names.")
PY

[[ "$root" = /* && "$root" != *"/../"* && "$root" != *"/.." ]] || {
  echo "Replay drill root must be absolute and traversal-free." >&2
  exit 3
}
root=${root%/}
root_parent=$(dirname "$root")
root_name=$(basename "$root")
[[ -d "$root_parent" && ! -L "$root_parent" ]] || {
  echo "Replay drill parent must be an existing non-symlink directory." >&2
  exit 3
}
canonical_parent=$(realpath -e "$root_parent")
canonical_root="$canonical_parent/$root_name"
[[ "$canonical_root" = "$root" ]] || {
  echo "Replay drill root changes under canonical resolution." >&2
  exit 3
}
case "$canonical_root" in
  /mnt/docker-storage/Docker/FestivalServiceTracker/fst-data/evidence/*|\
  /mnt/docker-storage/Docker/FestivalServiceTracker/fst-data/replay/*) ;;
  *) echo "Replay drill root must be beneath the approved FST evidence/replay root." >&2; exit 3 ;;
esac

[[ "$(stat -c %d "$canonical_parent")" = "$(stat -c %d /mnt/docker-storage)" ]] || {
  echo "Replay drill root is not on the approved FST filesystem." >&2
  exit 3
}
[[ ! -e "$canonical_root" ]] || {
  echo "Replay drill root already exists." >&2
  exit 3
}
scratch_root="$canonical_parent/.${root_name}.scratch"
[[ ! -e "$scratch_root" && ! -L "$scratch_root" ]] || {
  echo "Replay drill scratch root already exists." >&2
  exit 3
}
mkdir "$canonical_root"
mkdir "$scratch_root"
root=$(realpath -e "$canonical_root")
scratch_root=$(realpath -e "$scratch_root")
approved_device=$(stat -c '%Hd:%Ld' /mnt/docker-storage)
fixture_dir=$(realpath -e "$fixture_dir")
[[ -z "$(find "$fixture_dir" -type l -print -quit)" ]] || {
  echo "Synthetic Tier-1 fixture cannot contain symbolic links." >&2
  exit 4
}
[[ -f "$fixture_dir/tier0-parent/manifest.json" &&
   -f "$fixture_dir/tier1-input/manifest.json" ]] || {
  echo "Synthetic Tier-1 fixture is incomplete." >&2
  exit 4
}

input_root="$root/input"
mkdir -p "$input_root"
cp -a "$fixture_dir/." "$input_root/"
baseline_work="$root/baseline-work"
candidate_work="$root/candidate-work"
comparison_work="$root/comparison-work"
mkdir "$baseline_work" "$candidate_work" "$comparison_work"
baseline_view="$scratch_root/baseline-view"
candidate_view="$scratch_root/candidate-view"
comparison_view="$scratch_root/comparison-view"
mkdir "$baseline_view" "$candidate_view" "$comparison_view"
mkdir -p \
  "$baseline_view/input" \
  "$baseline_view/baseline-work" \
  "$candidate_view/input" \
  "$candidate_view/candidate-work" \
  "$comparison_view/baseline-work" \
  "$comparison_view/candidate-work" \
  "$comparison_view/comparison-work"
parent_package="$input_root/tier0-parent"
input_package="$input_root/tier1-input"
replay_id=$(jq -er '.replayId' "$input_package/tier1/phase-input.json")
input_hash=$(jq -er '.packageRootHash' "$input_package/manifest.json")
timing_reason="Deterministic replay overrides differ from production: SkipUnchangedScopes=false, one band-type worker, synchronous commit enabled, and candidate cleanup disabled."

active_containers=()
active_pgdata=()
active_tmpdirs=()
remove_pgdata() {
  local path=$1
  [[ -d "$path" ]] || return 0
  [[ "$path" = "$scratch_root/"* && ! -L "$path" ]] || {
    echo "Refusing unsafe replay PGDATA cleanup path." >&2
    return 1
  }
  docker run --rm --network none --user 0 \
    -v "$path:/cleanup" \
    postgres:17-alpine \
    sh -c 'find /cleanup -mindepth 1 -delete' >/dev/null
  rmdir "$path"
}
cleanup() {
  local container
  for container in "${active_containers[@]:-}"; do
    if [[ -n "$container" ]] && docker inspect "$container" >/dev/null 2>&1; then
      docker rm -f "$container" >/dev/null 2>&1 || true
    fi
  done
  local path
  for path in "${active_pgdata[@]:-}"; do
    [[ -n "$path" ]] && remove_pgdata "$path" || true
  done
  for path in "${active_tmpdirs[@]:-}"; do
    [[ -n "$path" && -d "$path" ]] && rm -rf "$path" || true
  done
  rm -rf "$baseline_view" "$candidate_view" "$comparison_view"
  [[ -d "$scratch_root" ]] && rmdir "$scratch_root" 2>/dev/null || true
}
trap cleanup EXIT INT TERM

run_lane() {
  local label=$1
  local image=$2
  local attempt=$3
  local work
  local view
  if [[ "$label" = baseline ]]; then
    work=$baseline_work
    view=$baseline_view
  else
    work=$candidate_work
    view=$candidate_view
  fi
  local output="$work/output"
  local pgdata="$scratch_root/${label}-pgdata"
  local container_tmp="$work/container-tmp"
  local container="fst-replay-pg-${label}-$$"
  local database="fst_replay_${label}_$$_${RANDOM}"
  local image_digest
  local image_revision
  image_digest=$(docker image inspect "$image" --format '{{.Id}}')
  image_revision=$(docker image inspect "$image_digest" --format '{{index .Config.Labels "org.opencontainers.image.revision"}}')
  [[ "$image_digest" =~ ^sha256:[0-9a-f]{64}$ ]] || {
    echo "Replay image digest is unavailable for $label." >&2
    exit 4
  }
  [[ "$image_revision" =~ ^[0-9a-fA-F]{40}$|^[0-9a-fA-F]{64}$ ]] || {
    echo "Replay image OCI revision is unavailable for $label." >&2
    exit 4
  }
  printf -v "${label}_digest" '%s' "$image_digest"
  printf -v "${label}_revision" '%s' "$image_revision"

  mkdir -p "$pgdata" "$container_tmp"
  active_pgdata+=("$pgdata")
  active_tmpdirs+=("$container_tmp")
  docker run -d --name "$container" \
    --network none \
    --cpus 1 \
    --memory 1g \
    --shm-size 128m \
    -e POSTGRES_USER=replay_admin \
    -e POSTGRES_PASSWORD=replay-admin-test \
    -e POSTGRES_DB=replay \
    -v "$pgdata:/var/lib/postgresql/data" \
    postgres:17-alpine \
    -c max_connections=20 \
    -c max_parallel_workers=0 \
    -c dynamic_shared_memory_type=mmap \
    >/dev/null
  active_containers+=("$container")

  local ready=0
  for _ in $(seq 1 90); do
    if docker exec "$container" pg_isready -U replay_admin -d replay >/dev/null 2>&1; then
      ready=1
      break
    fi
    sleep 1
  done
  [[ "$ready" = 1 ]] || {
    echo "Isolated PostgreSQL did not become ready for $label." >&2
    exit 5
  }

  docker exec "$container" psql -U replay_admin -d replay -v ON_ERROR_STOP=1 \
    -c "CREATE DATABASE \"$database\";" >/dev/null
  local system_id
  system_id=$(docker exec "$container" psql -U replay_admin -d "$database" -Atc \
    "SELECT system_identifier::TEXT FROM pg_control_system()")
  docker exec -i "$container" psql -U replay_admin -d "$database" \
    -v ON_ERROR_STOP=1 \
    -v replay_id="$replay_id" \
    -v package_root_hash="$input_hash" \
    -v database_name="$database" \
    -v system_identifier="$system_id" <<'SQL' >/dev/null
CREATE SCHEMA fst_replay_control;
CREATE TABLE fst_replay_control.target (
  singleton BOOLEAN PRIMARY KEY DEFAULT TRUE CHECK (singleton),
  marker_version INTEGER NOT NULL,
  replay_id TEXT NOT NULL,
  package_root_hash TEXT NOT NULL,
  database_name TEXT NOT NULL,
  system_identifier TEXT NOT NULL,
  status TEXT NOT NULL,
  created_at TIMESTAMPTZ NOT NULL,
  updated_at TIMESTAMPTZ NOT NULL
);
INSERT INTO fst_replay_control.target (
  singleton, marker_version, replay_id, package_root_hash,
  database_name, system_identifier, status, created_at, updated_at
) VALUES (
  TRUE, 1, :'replay_id', :'package_root_hash',
  :'database_name', :'system_identifier', 'created', now(), now()
);
CREATE ROLE replay_runner
  LOGIN
  PASSWORD 'replay-runner-test'
  NOSUPERUSER
  NOCREATEDB
  NOCREATEROLE
  NOINHERIT;
GRANT pg_monitor TO replay_runner;
GRANT CONNECT ON DATABASE :"database_name" TO replay_runner;
GRANT USAGE, CREATE ON SCHEMA public TO replay_runner;
GRANT USAGE ON SCHEMA fst_replay_control TO replay_runner;
GRANT SELECT, UPDATE ON fst_replay_control.target TO replay_runner;
SQL

  docker run --rm \
    --network "container:$container" \
    --user "$(id -u):$(id -g)" \
    --cpus 1 \
    --memory 1g \
    -e TMPDIR="$container_tmp" \
    -e DOTNET_BUNDLE_EXTRACT_BASE_DIR="$container_tmp" \
    -e FST_REPLAY_APPROVED_ROOT="$root" \
    -e FST_REPLAY_APPROVED_DEVICE="$approved_device" \
    -e FST_REPLAY_ROLLBACK_RESERVE_BYTES=0 \
    -e PGPASSWORD=replay-runner-test \
    -e FST_REPLAY_POSTGRES_CONNECTION="Host=127.0.0.1;Port=5432;Database=$database;Username=replay_runner;SSL Mode=Disable" \
    -e FST_REPLAY_GIT_COMMIT="$image_revision" \
    -e FST_REPLAY_IMAGE_DIGEST="$image_digest" \
    -e FST_REPLAY_IMAGE_REVISION="$image_revision" \
    -v "$view:$root:ro" \
    -v "$input_root:$input_root:ro" \
    -v "$work:$work:rw" \
    "$image_digest" \
    --replay-parent-package "$parent_package" \
    --replay-package "$input_package" \
    --replay-phase post.band_maintenance \
    --replay-subphase current_projection_refresh \
    --replay-output "$output" \
    --replay-id "$replay_id" \
    --replay-attempt "$attempt" \
    --no-publication \
    >"$root/${label}.jsonl"

  docker rm -f "$container" >/dev/null
  active_containers=("${active_containers[@]/$container/}")
  remove_pgdata "$pgdata"
  active_pgdata=("${active_pgdata[@]/$pgdata/}")
  rm -rf "$container_tmp"
  active_tmpdirs=("${active_tmpdirs[@]/$container_tmp/}")
  [[ -f "$output/manifest.json" ]] || {
    echo "Replay output was not sealed for $label." >&2
    exit 8
  }
}

run_lane baseline "$baseline_image" 1
run_lane candidate "$candidate_image" 2

docker run --rm \
  --network none \
  --user "$(id -u):$(id -g)" \
  --cpus 1 \
  --memory 512m \
  -e FST_REPLAY_APPROVED_ROOT="$root" \
  -e FST_REPLAY_APPROVED_DEVICE="$approved_device" \
  -e FST_REPLAY_ROLLBACK_RESERVE_BYTES=0 \
  -v "$comparison_view:$root:ro" \
  -v "$baseline_work:$baseline_work:ro" \
  -v "$candidate_work:$candidate_work:ro" \
  -v "$comparison_work:$comparison_work:rw" \
  "$baseline_digest" \
  --replay-compare-baseline "$baseline_work/output" \
  --replay-compare-candidate "$candidate_work/output" \
  --replay-comparison-output "$comparison_work/comparison.json" \
  --replay-baseline-image-digest "$baseline_digest" \
  --replay-candidate-image-digest "$candidate_digest" \
  --replay-baseline-revision "$baseline_revision" \
  --replay-candidate-revision "$candidate_revision" \
  --replay-baseline-git-commit "$baseline_revision" \
  --replay-candidate-git-commit "$candidate_revision" \
  --replay-baseline-attempt 1 \
  --replay-candidate-attempt 2 \
  --no-publication \
  >"$root/comparison-command.jsonl"

jq -e \
  --arg reason "$timing_reason" \
  '.exactParity == true
   and .productionComparableTiming == false
   and .timingComparisonReason == $reason' \
  "$comparison_work/comparison.json" >/dev/null
cp "$comparison_work/comparison.json" "$root/comparison.json"
rm -rf "$baseline_view" "$candidate_view" "$comparison_view"
[[ -z "$(find "$scratch_root" -mindepth 1 -print -quit)" ]] || {
    echo "Replay PostgreSQL scratch cleanup failed." >&2
    exit 8
  }
rmdir "$scratch_root"

jq -n \
  --arg replayId "$replay_id" \
  --arg baselineImage "$baseline_image" \
  --arg candidateImage "$candidate_image" \
  --arg baselineDigest "$baseline_digest" \
  --arg candidateDigest "$candidate_digest" \
  --arg baselineRevision "$baseline_revision" \
  --arg candidateRevision "$candidate_revision" \
  --arg inputRoot "$input_hash" \
  --arg timingReason "$timing_reason" \
  '{
    format: "fst.tier1.replay-drill",
    version: 2,
    replayId: $replayId,
    baseline: {
      image: $baselineImage,
      digest: $baselineDigest,
      revision: $baselineRevision
    },
    candidate: {
      image: $candidateImage,
      digest: $candidateDigest,
      revision: $candidateRevision
    },
    inputRootHash: $inputRoot,
    exactParity: true,
    productionComparableTiming: false,
    timingComparisonReason: $timingReason,
    networkMode: "isolated-container-namespace",
    publishedPorts: false,
    dockerSocketMounted: false,
    productionDataAccessed: false
  }' >"$root/run.json"

cat >"$root/report.md" <<EOF
# Tier-1 isolated replay drill

- Replay ID: \`$replay_id\`
- Parent input root: \`$input_hash\`
- Baseline image: \`$baseline_image\` / \`$baseline_digest\` / \`$baseline_revision\`
- Candidate image: \`$candidate_image\` / \`$candidate_digest\` / \`$candidate_revision\`
- Exact output parity: accepted
- Production-comparable timing: false
- Timing reason: $timing_reason
- PostgreSQL: two fresh PostgreSQL 17 containers, no published ports, network-none namespaces
- Cleanup: both containers and PGDATA directories removed
- Production database/API/provider access: none
EOF

(cd "$root" && find . -type f ! -name checksums.sha256 -printf '%P\0' | sort -z | xargs -0 sha256sum > checksums.sha256)
echo "$root"
