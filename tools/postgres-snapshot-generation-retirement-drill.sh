#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd -P)"
REPO_ROOT="$(cd -- "$SCRIPT_DIR/.." && pwd -P)"
APPROVED_ROOT="/mnt/docker-storage/Docker/FestivalServiceTracker/fst-data/evidence/snapshot-generation-retirement-plan-drills"
SOCKET_ROOT="/mnt/docker-storage/.fst-retirement-plan-sockets"
WORK_ROOT=""
IMAGE="postgres:17"

usage() {
    cat <<'EOF'
Usage:
  tools/postgres-snapshot-generation-retirement-drill.sh \
    --work-root /mnt/docker-storage/Docker/FestivalServiceTracker/fst-data/evidence/snapshot-generation-retirement-plan-drills/<new-run>

Runs a network-none disposable PostgreSQL drill for the plan-only retirement
control plane. The repository must be clean and committed.
EOF
}

while (($# > 0)); do
    case "$1" in
        --work-root)
            WORK_ROOT=${2:-}
            shift 2
            ;;
        --image)
            IMAGE=${2:-}
            shift 2
            ;;
        -h|--help)
            usage
            exit 0
            ;;
        *)
            printf 'ERROR: unknown argument: %s\n' "$1" >&2
            usage >&2
            exit 64
            ;;
    esac
done

if [[ -z "$WORK_ROOT" ]]; then
    printf 'ERROR: --work-root is required.\n' >&2
    exit 64
fi

approved_root=$(realpath -m "$APPROVED_ROOT")
work_root=$(realpath -m "$WORK_ROOT")
case "$work_root/" in
    "$approved_root"/*/) ;;
    *)
        printf 'ERROR: work root must be a new child of %s\n' "$approved_root" >&2
        exit 64
        ;;
esac
relative_work_root=${work_root#"$approved_root"/}
if [[ "$relative_work_root" == */* \
      || ! "$relative_work_root" =~ ^[A-Za-z0-9._-]+$ ]]
then
    printf 'ERROR: work root must be one safely named direct child of the approved root.\n' >&2
    exit 64
fi
if [[ "$IMAGE" != "postgres:17" ]]; then
    printf 'ERROR: the drill image is fixed to postgres:17.\n' >&2
    exit 64
fi
if [[ -e "$work_root" ]]; then
    printf 'ERROR: work root already exists: %s\n' "$work_root" >&2
    exit 64
fi
if [[ -n "$(git -C "$REPO_ROOT" status --porcelain --untracked-files=all)" ]]; then
    printf 'ERROR: the drill requires a clean committed repository.\n' >&2
    exit 1
fi

docker_root=$(realpath -m "$(docker info --format '{{.DockerRootDir}}')")
case "$docker_root/" in
    /mnt/docker-storage/*/) ;;
    *)
        printf 'ERROR: Docker root is not on the approved FST drive: %s\n' "$docker_root" >&2
        exit 1
        ;;
esac

mkdir -p "$approved_root"
mkdir "$work_root"
mkdir "$work_root/database"
chmod 0777 "$work_root/database"

operation_id=$(tr -d '\n' </proc/sys/kernel/random/uuid)
socket_root=$(realpath -m "$SOCKET_ROOT")
socket_dir="$socket_root/${operation_id//-/}"
socket_path="$socket_dir/.s.PGSQL.5432"
if ((${#socket_path} >= 108)); then
    printf 'ERROR: PostgreSQL socket path exceeds the Linux AF_UNIX limit: %s\n' "$socket_path" >&2
    exit 1
fi
mkdir -p "$socket_root"
mkdir "$socket_dir"
chmod 0777 "$socket_dir"

container_name="fst-retirement-plan-drill-$(date -u +%Y%m%dT%H%M%SZ)-$$"
container_id=""
remove_owned_containers() {
    local output
    local id
    if ! output=$(docker ps -aq \
        --filter "label=fst.retirement-plan-drill.operation=$operation_id")
    then
        return 1
    fi
    while IFS= read -r id; do
        [[ -z "$id" ]] && continue
        if [[ ! "$id" =~ ^[0-9a-f]{12,64}$ ]]; then
            return 1
        fi
        docker rm -f "$id" >/dev/null || return 1
    done <<<"$output"
    if ! output=$(docker ps -aq \
        --filter "label=fst.retirement-plan-drill.operation=$operation_id")
    then
        return 1
    fi
    [[ -z "$output" ]]
}
cleanup_bind_mounts() {
    if ! docker run --rm \
        --name "$container_name-cleanup" \
        --label "fst.retirement-plan-drill.operation=$operation_id" \
        --network none \
        --mount "type=bind,src=$work_root,dst=/cleanup" \
        --mount "type=bind,src=$socket_dir,dst=/cleanup-socket" \
        --entrypoint /bin/sh \
        "$IMAGE" \
        -c 'rm -rf -- /cleanup/database /cleanup-socket/.[!.]* /cleanup-socket/..?* /cleanup-socket/*'
    then
        remove_owned_containers >/dev/null 2>&1 || true
        return 1
    fi
    remove_owned_containers
    rmdir "$socket_dir"
}
cleanup() {
    if ! remove_owned_containers; then
        return
    fi
    cleanup_bind_mounts >/dev/null 2>&1 || true
}
trap cleanup EXIT

container_id=$(docker run -d \
    --name "$container_name" \
    --label "fst.retirement-plan-drill.operation=$operation_id" \
    --network none \
    --mount "type=bind,src=$work_root/database,dst=/var/lib/postgresql/data" \
    --mount "type=bind,src=$socket_dir,dst=/var/run/postgresql" \
    -e POSTGRES_HOST_AUTH_METHOD=trust \
    -e POSTGRES_USER=fst \
    -e POSTGRES_DB=fstservice \
    -e PGDATA=/var/lib/postgresql/data/pgdata \
    "$IMAGE")

for _ in $(seq 1 60); do
    if docker exec "$container_id" \
        pg_isready -U fst -d fstservice >/dev/null 2>&1
    then
        break
    fi
    sleep 1
done
docker exec "$container_id" \
    pg_isready -U fst -d fstservice >/dev/null

dotnet build \
    "$REPO_ROOT/FSTService/FSTService.csproj" \
    -c Release \
    --nologo >/dev/null
dotnet publish \
    "$REPO_ROOT/tools/FstSnapshotGenerationRetirement/FstSnapshotGenerationRetirement.csproj" \
    -c Release \
    --nologo >/dev/null

connection="Host=$socket_dir;Database=fstservice;Username=fst;Pooling=false"
(
    cd "$work_root"
    ConnectionStrings__PostgreSQL="$connection" \
        dotnet \
        "$REPO_ROOT/FSTService/bin/Release/net9.0/FSTService.dll" \
        --initialize-schema-only >/dev/null
)

docker exec -i "$container_id" \
    psql -X -v ON_ERROR_STOP=1 -U fst -d fstservice <<'SQL'
SELECT
    public.ensure_leaderboard_snapshot_generation_partition(
        'Solo_Guitar',
        9901);
SELECT
    public.ensure_leaderboard_snapshot_generation_partition(
        'Solo_PeripheralDrums',
        9902);

INSERT INTO public.leaderboard_entries_snapshot (
    snapshot_id,
    song_id,
    instrument,
    account_id,
    score,
    first_seen_at,
    last_updated_at)
SELECT
    9901,
    'large-song-' || value::TEXT || '-' || repeat('s', 96),
    'Solo_Guitar',
    'large-account-' || value::TEXT || '-' || repeat('a', 96),
    value,
    pg_catalog.now(),
    pg_catalog.now()
FROM pg_catalog.generate_series(1, 1000) value;

INSERT INTO public.leaderboard_entries_snapshot (
    snapshot_id,
    song_id,
    instrument,
    account_id,
    score,
    first_seen_at,
    last_updated_at)
SELECT
    9902,
    'small-song-' || value::TEXT,
    'Solo_PeripheralDrums',
    'small-account-' || value::TEXT,
    value,
    pg_catalog.now(),
    pg_catalog.now()
FROM pg_catalog.generate_series(1, 10) value;

INSERT INTO public.scrape_log (
    id,
    started_at,
    completed_at,
    status)
VALUES
    (
        9901,
        pg_catalog.now(),
        pg_catalog.now(),
        'completed'),
    (
        9902,
        pg_catalog.now(),
        pg_catalog.now(),
        'completed'),
    (
        990001,
        pg_catalog.now(),
        pg_catalog.now(),
        'completed');

INSERT INTO public.publication_generations (
    publication_id,
    scrape_id,
    status,
    created_at,
    ready_at,
    published_at)
VALUES (
    99001,
    990001,
    'current',
    pg_catalog.now(),
    pg_catalog.now(),
    pg_catalog.now());

UPDATE public.scrape_publication_state
SET published_scrape_id = 990001,
    current_publication_id = 99001,
    previous_publication_id = NULL,
    working_publication_id = NULL,
    public_reads_frozen = FALSE,
    publication_commit_intent_started_at = NULL,
    max_score_mutation_gate_token = NULL,
    improvement_notifications_scrape_id = 990001,
    improvement_notifications_status = 'completed',
    improvement_notifications_completed_at = pg_catalog.now(),
    improvement_notifications_projection_ready = TRUE,
    improvement_notifications_projection_scrape_id = 990001,
    updated_at = pg_catalog.now()
WHERE id = TRUE;

WITH cycle AS (
    INSERT INTO
        public.snapshot_generation_retention_cycles (
            trigger_scrape_id,
            trigger_publication_id,
            safe_point_kind,
            safe_point_at,
            planner_version,
            config_version,
            report_only,
            status,
            oracle_agreement,
            candidate_identity_hash,
            observation_hash,
            planner_child_set,
            planner_live_set,
            planner_candidate_set,
            oracle_child_set,
            oracle_live_set,
            oracle_candidate_set,
            candidate_count,
            protected_count,
            blocked_count,
            candidate_bytes,
            global_blockers,
            anomalies)
    VALUES (
        990001,
        99001,
        'terminal_worker_post_publication',
        pg_catalog.now(),
        3,
        1,
        TRUE,
        'observed',
        TRUE,
        repeat('a', 64),
        repeat('b', 64),
        '[]',
        '[]',
        '[]',
        '[]',
        '[]',
        '[]',
        2,
        0,
        0,
        1,
        '[]',
        '[]')
    RETURNING cycle_id
),
targets AS (
    SELECT
        cycle.cycle_id,
        input.instrument,
        input.snapshot_id,
        input.root_relation,
        input.child_relation,
        root_inheritance.inhparent::BIGINT
            AS snapshot_parent_oid,
        root.oid::BIGINT AS root_oid,
        pg_catalog.pg_get_partkeydef(
            root.oid) AS root_partition_key,
        pg_catalog.pg_get_expr(
            root.relpartbound,
            root.oid,
            TRUE) AS root_partition_bound,
        COALESCE(
            root_tablespace.spcname,
            database_tablespace.spcname)
            AS root_tablespace_name,
        pg_catalog.to_jsonb(
            ARRAY(
                SELECT option
                FROM pg_catalog.unnest(
                    COALESCE(
                        root.reloptions,
                        ARRAY[]::TEXT[]))
                    option
                ORDER BY option))
            AS root_relation_options,
        public.fst_snapshot_generation_retirement_index_configuration(
            root.oid::BIGINT)
            AS root_index_configuration,
        child.oid::BIGINT AS child_oid,
        child.relfilenode::BIGINT AS child_relfilenode,
        pg_catalog.pg_get_expr(
            child.relpartbound,
            child.oid,
            TRUE) AS partition_bound,
        COALESCE(
            child_tablespace.spcname,
            database_tablespace.spcname)
            AS tablespace_name,
        child.relkind::TEXT AS relation_kind,
        child.relpersistence::TEXT AS persistence_kind,
        access_method.amname AS access_method,
        pg_catalog.to_jsonb(
            ARRAY(
                SELECT option
                FROM pg_catalog.unnest(
                    COALESCE(
                        child.reloptions,
                        ARRAY[]::TEXT[]))
                    option
                ORDER BY option))
            AS relation_options,
        public.fst_snapshot_generation_retirement_index_configuration(
            child.oid::BIGINT)
            AS index_configuration,
        pg_catalog.pg_total_relation_size(
            child.oid)::BIGINT AS bytes
    FROM cycle
    CROSS JOIN (
        VALUES
            (
                'Solo_Guitar',
                9901::BIGINT,
                'leaderboard_entries_snapshot_solo_guitar',
                'leaderboard_entries_snapshot_solo_guitar_s9901'),
            (
                'Solo_PeripheralDrums',
                9902::BIGINT,
                'leaderboard_entries_snapshot_pro_drums',
                'leaderboard_entries_snapshot_pro_drums_s9902')
    ) input(
        instrument,
        snapshot_id,
        root_relation,
        child_relation)
    JOIN pg_catalog.pg_class root
      ON root.relname = input.root_relation
    JOIN pg_catalog.pg_namespace root_namespace
      ON root_namespace.oid = root.relnamespace
     AND root_namespace.nspname = 'public'
    JOIN pg_catalog.pg_inherits root_inheritance
      ON root_inheritance.inhrelid = root.oid
    JOIN pg_catalog.pg_class child
      ON child.relname = input.child_relation
    JOIN pg_catalog.pg_namespace child_namespace
      ON child_namespace.oid = child.relnamespace
     AND child_namespace.nspname = 'public'
    JOIN pg_catalog.pg_inherits child_inheritance
      ON child_inheritance.inhrelid = child.oid
     AND child_inheritance.inhparent = root.oid
    JOIN pg_catalog.pg_am access_method
      ON access_method.oid = child.relam
    LEFT JOIN pg_catalog.pg_tablespace root_tablespace
      ON root_tablespace.oid = root.reltablespace
    LEFT JOIN pg_catalog.pg_tablespace child_tablespace
      ON child_tablespace.oid = child.reltablespace
    CROSS JOIN LATERAL (
        SELECT default_tablespace.spcname
        FROM pg_catalog.pg_database database
        JOIN pg_catalog.pg_tablespace default_tablespace
          ON default_tablespace.oid = database.dattablespace
        WHERE database.datname =
                pg_catalog.current_database()
    ) database_tablespace
)
INSERT INTO
    public.snapshot_generation_retention_observations (
        cycle_id,
        report_only,
        instrument,
        root_schema,
        root_relation,
        snapshot_parent_oid,
        root_oid,
        root_partition_key,
        root_partition_bound,
        root_tablespace_name,
        root_relation_options,
        root_index_configuration,
        child_schema,
        child_relation,
        snapshot_id,
        child_oid,
        child_relfilenode,
        partition_bound,
        tablespace_name,
        relation_kind,
        persistence_kind,
        access_method,
        relation_options,
        index_configuration,
        stable_child_identity_hash,
        stable_config_schema_hash,
        row_estimate,
        total_bytes,
        observation_metrics_hash,
        planner_live,
        oracle_live,
        classification,
        root_reasons,
        blocker_codes,
        details)
SELECT
    target.cycle_id,
    TRUE,
    target.instrument,
    'public',
    target.root_relation,
    target.snapshot_parent_oid,
    target.root_oid,
    target.root_partition_key,
    target.root_partition_bound,
    target.root_tablespace_name,
    target.root_relation_options,
    target.root_index_configuration,
    'public',
    target.child_relation,
    target.snapshot_id,
    target.child_oid,
    target.child_relfilenode,
    target.partition_bound,
    target.tablespace_name,
    target.relation_kind,
    target.persistence_kind,
    target.access_method,
    target.relation_options,
    target.index_configuration,
    repeat('c', 64),
    repeat('d', 64),
    1,
    target.bytes,
    repeat('e', 64),
    FALSE,
    FALSE,
    'candidate',
    ARRAY[]::TEXT[],
    ARRAY[]::TEXT[],
    '{}'
FROM targets target;
SQL

source_before=$(docker exec "$container_id" \
    psql -X -A -t -v ON_ERROR_STOP=1 -U fst -d fstservice \
    -c "SELECT oid::BIGINT || ':' || relfilenode::BIGINT || ':' || pg_total_relation_size(oid)::BIGINT FROM pg_class WHERE oid = 'public.leaderboard_entries_snapshot_solo_guitar_s9901'::regclass")
printf '%s\n' "$source_before" >"$work_root/source-before.txt"

binary="$REPO_ROOT/tools/FstSnapshotGenerationRetirement/bin/Release/net9.0/linux-x64/publish/FstSnapshotGenerationRetirement"
binary_sha=$(sha256sum "$binary" | cut -d' ' -f1)
export FST_SNAPSHOT_RETIREMENT_BINARY_SHA256="$binary_sha"
export FST_SNAPSHOT_RETIREMENT_CONNECTION_STRING="$connection"

"$REPO_ROOT/tools/postgres-snapshot-generation-retirement.sh" \
    status >"$work_root/status-before.json"

readarray -t identity < <(
    node - "$work_root/status-before.json" <<'NODE'
const fs = require('fs');
const value = JSON.parse(fs.readFileSync(process.argv[2], 'utf8'));
for (const item of [
  value.observedIdentity.code.repositoryCommit,
  value.observedIdentity.code.repositoryTree,
  value.observedIdentity.code.supervisorBinarySha256,
  value.observedIdentity.code.supervisorSourceSha256,
  value.observedIdentity.code.wrapperSha256,
  value.observedIdentity.controlSchemaSha256,
  value.observedIdentity.sourceIdentitySha256,
]) console.log(item);
NODE
)

not_before=$(date -u -d '1 minute ago' '+%Y-%m-%dT%H:%M:%S.0000000+00:00')
expires_at=$(date -u -d '1 hour' '+%Y-%m-%dT%H:%M:%S.0000000+00:00')
"$REPO_ROOT/tools/postgres-snapshot-generation-retirement.sh" \
    authorize-policy-epoch \
    --not-before "$not_before" \
    --expires-at "$expires_at" \
    --max-jobs 1 \
    --max-total-bytes 1073741824 \
    --approved-by drill-approver \
    --reviewed-by drill-reviewer \
    --approval-reference isolated-network-none-drill \
    --expected-repository-commit "${identity[0]}" \
    --expected-repository-tree "${identity[1]}" \
    --expected-supervisor-binary-sha256 "${identity[2]}" \
    --expected-supervisor-source-sha256 "${identity[3]}" \
    --expected-wrapper-sha256 "${identity[4]}" \
    --expected-control-schema-sha256 "${identity[5]}" \
    --expected-source-identity-sha256 "${identity[6]}" \
    >"$work_root/authorization.json"

"$REPO_ROOT/tools/postgres-snapshot-generation-retirement.sh" \
    plan-cycle >"$work_root/plan.json"
"$REPO_ROOT/tools/postgres-snapshot-generation-retirement.sh" \
    deactivate-policy-epoch \
    >"$work_root/deactivation.json"
"$REPO_ROOT/tools/postgres-snapshot-generation-retirement.sh" \
    status >"$work_root/status-after.json"

node - \
    "$work_root/plan.json" \
    "$work_root/status-after.json" <<'NODE'
const fs = require('fs');
const plan = JSON.parse(fs.readFileSync(process.argv[2], 'utf8'));
const status = JSON.parse(fs.readFileSync(process.argv[3], 'utf8'));
if (plan.instrument !== 'Solo_Guitar' || plan.snapshotId !== 9901) {
  throw new Error('largest-first plan mismatch');
}
if (plan.state !== 'planned') {
  throw new Error('plan did not enter planned state');
}
if (status.control.enabled || status.activeJob !== null) {
  throw new Error('operator deactivation did not clear active state');
}
if (status.latestJob.state !== 'superseded'
    || status.latestJob.stateReason !== 'operator_deactivated') {
  throw new Error('operator deactivation did not terminalize the plan');
}
NODE

source_after=$(docker exec "$container_id" \
    psql -X -A -t -v ON_ERROR_STOP=1 -U fst -d fstservice \
    -c "SELECT oid::BIGINT || ':' || relfilenode::BIGINT || ':' || pg_total_relation_size(oid)::BIGINT FROM pg_class WHERE oid = 'public.leaderboard_entries_snapshot_solo_guitar_s9901'::regclass")
printf '%s\n' "$source_after" >"$work_root/source-after.txt"
if [[ "$source_before" != "$source_after" ]]; then
    printf 'ERROR: source relation identity or bytes changed.\n' >&2
    exit 1
fi

cat >"$work_root/run.json" <<EOF
{
  "schemaVersion": 1,
  "networkMode": "none",
  "postgresImage": "$IMAGE",
  "sourceBefore": "$source_before",
  "sourceAfter": "$source_after",
  "sourceMutation": false,
  "archiveInvoked": false,
  "destructiveOperationInvoked": false
}
EOF

(
    cd "$work_root"
    sha256sum \
        authorization.json \
        deactivation.json \
        plan.json \
        run.json \
        source-after.txt \
        source-before.txt \
        status-after.json \
        status-before.json \
        >SHA256SUMS
)

if ! remove_owned_containers; then
    printf 'ERROR: disposable PostgreSQL container removal failed.\n' >&2
    exit 1
fi
if ! cleanup_bind_mounts; then
    printf 'ERROR: disposable PostgreSQL bind-mount cleanup failed.\n' >&2
    exit 1
fi
if [[ -e "$work_root/database" || -e "$socket_dir" ]]; then
    printf 'ERROR: disposable PostgreSQL scratch cleanup failed.\n' >&2
    exit 1
fi
trap - EXIT

printf 'Retirement plan drill passed: %s\n' "$work_root"
