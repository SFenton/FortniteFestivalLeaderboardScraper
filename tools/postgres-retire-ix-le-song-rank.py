#!/usr/bin/env python3

import argparse
import contextlib
import fcntl
import hashlib
import json
import os
import pathlib
import re
import shutil
import stat
import subprocess
import sys
import urllib.request
from datetime import datetime, timezone


FORMAT_VERSION = 1
FAMILY_ID = "public.ix_le_song_rank"
PRODUCTION_COMPOSE_DIR = pathlib.Path(
    "/home/sfenton/Docker/FestivalServiceTracker"
)
PRODUCTION_PROJECT = "festivalservicetracker"
PRODUCTION_STORAGE_ROOT = pathlib.Path("/mnt/docker-storage")
PRODUCTION_EVIDENCE_ROOT = pathlib.Path(
    "/mnt/docker-storage/Docker/FestivalServiceTracker/fst-data/evidence"
)
POSTGRES_CONTAINER = "fst-postgres"
POSTGRES_SERVICE = "postgres"
WORKER_CONTAINER = "fstworker"
WORKER_SERVICE = "fstworker"
SERVICE_CONTAINER = "fstservice"
WEB_CONTAINER = "festivalweb"
DATABASE_NAME = "fstservice"
DATABASE_USER = "fst"
PUBLICATION_ADVISORY_LOCK_KEY = 5067481511116519500
REGISTRATION_MUTATION_ADVISORY_LOCK_KEY = 5067481511116518500
RETIREMENT_LOCK_NAME = "fst.retire_ix_le_song_rank.v1"
LOCK_TIMEOUT = "2s"
STATEMENT_TIMEOUT = "30s"
ROLLBACK_STATEMENT_TIMEOUT = "30min"
APPLICATION_NAME = "fst-ix-le-song-rank-retirement"
TEST_MODE_ENV = "FST_IX_LE_SONG_RANK_TEST_MODE"
SCRIPT_PATH = pathlib.Path(__file__).resolve()
WRAPPER_PATH = SCRIPT_PATH.with_suffix(".sh")
WORKER_GUARD_LOCK = (
    PRODUCTION_COMPOSE_DIR / ".fst-worker-compose-guard.lock"
)

INDEX_SPECS = (
    {
        "name": "ix_le_song_rank",
        "table": "leaderboard_entries",
        "relkind": "I",
        "parent": None,
        "definition": (
            "CREATE INDEX ix_le_song_rank ON ONLY public.leaderboard_entries "
            "USING btree (song_id, instrument, rank)"
        ),
    },
    {
        "name": (
            "leaderboard_entries_solo_guitar_"
            "song_id_instrument_rank_idx"
        ),
        "table": "leaderboard_entries_solo_guitar",
        "relkind": "i",
        "parent": "ix_le_song_rank",
        "definition": (
            "CREATE INDEX leaderboard_entries_solo_guitar_"
            "song_id_instrument_rank_idx ON "
            "public.leaderboard_entries_solo_guitar USING btree "
            "(song_id, instrument, rank)"
        ),
    },
    {
        "name": (
            "leaderboard_entries_solo_bass_song_id_instrument_rank_idx"
        ),
        "table": "leaderboard_entries_solo_bass",
        "relkind": "i",
        "parent": "ix_le_song_rank",
        "definition": (
            "CREATE INDEX leaderboard_entries_solo_bass_"
            "song_id_instrument_rank_idx ON "
            "public.leaderboard_entries_solo_bass USING btree "
            "(song_id, instrument, rank)"
        ),
    },
    {
        "name": (
            "leaderboard_entries_solo_drums_song_id_instrument_rank_idx"
        ),
        "table": "leaderboard_entries_solo_drums",
        "relkind": "i",
        "parent": "ix_le_song_rank",
        "definition": (
            "CREATE INDEX leaderboard_entries_solo_drums_"
            "song_id_instrument_rank_idx ON "
            "public.leaderboard_entries_solo_drums USING btree "
            "(song_id, instrument, rank)"
        ),
    },
    {
        "name": (
            "leaderboard_entries_solo_vocals_song_id_instrument_rank_idx"
        ),
        "table": "leaderboard_entries_solo_vocals",
        "relkind": "i",
        "parent": "ix_le_song_rank",
        "definition": (
            "CREATE INDEX leaderboard_entries_solo_vocals_"
            "song_id_instrument_rank_idx ON "
            "public.leaderboard_entries_solo_vocals USING btree "
            "(song_id, instrument, rank)"
        ),
    },
    {
        "name": (
            "leaderboard_entries_pro_guitar_song_id_instrument_rank_idx"
        ),
        "table": "leaderboard_entries_pro_guitar",
        "relkind": "i",
        "parent": "ix_le_song_rank",
        "definition": (
            "CREATE INDEX leaderboard_entries_pro_guitar_"
            "song_id_instrument_rank_idx ON "
            "public.leaderboard_entries_pro_guitar USING btree "
            "(song_id, instrument, rank)"
        ),
    },
    {
        "name": (
            "leaderboard_entries_pro_bass_song_id_instrument_rank_idx"
        ),
        "table": "leaderboard_entries_pro_bass",
        "relkind": "i",
        "parent": "ix_le_song_rank",
        "definition": (
            "CREATE INDEX leaderboard_entries_pro_bass_"
            "song_id_instrument_rank_idx ON "
            "public.leaderboard_entries_pro_bass USING btree "
            "(song_id, instrument, rank)"
        ),
    },
    {
        "name": (
            "leaderboard_entries_pro_vocals_song_id_instrument_rank_idx"
        ),
        "table": "leaderboard_entries_pro_vocals",
        "relkind": "i",
        "parent": "ix_le_song_rank",
        "definition": (
            "CREATE INDEX leaderboard_entries_pro_vocals_"
            "song_id_instrument_rank_idx ON "
            "public.leaderboard_entries_pro_vocals USING btree "
            "(song_id, instrument, rank)"
        ),
    },
    {
        "name": (
            "leaderboard_entries_pro_cymbals_song_id_instrument_rank_idx"
        ),
        "table": "leaderboard_entries_pro_cymbals",
        "relkind": "i",
        "parent": "ix_le_song_rank",
        "definition": (
            "CREATE INDEX leaderboard_entries_pro_cymbals_"
            "song_id_instrument_rank_idx ON "
            "public.leaderboard_entries_pro_cymbals USING btree "
            "(song_id, instrument, rank)"
        ),
    },
    {
        "name": (
            "leaderboard_entries_pro_drums_song_id_instrument_rank_idx"
        ),
        "table": "leaderboard_entries_pro_drums",
        "relkind": "i",
        "parent": "ix_le_song_rank",
        "definition": (
            "CREATE INDEX leaderboard_entries_pro_drums_"
            "song_id_instrument_rank_idx ON "
            "public.leaderboard_entries_pro_drums USING btree "
            "(song_id, instrument, rank)"
        ),
    },
)

EXPECTED_NAMES = tuple(spec["name"] for spec in INDEX_SPECS)
EXPECTED_TABLES = tuple(spec["table"] for spec in INDEX_SPECS)
EXPECTED_BY_NAME = {spec["name"]: spec for spec in INDEX_SPECS}


class GuardFailure(RuntimeError):
    pass


class CommandFailure(RuntimeError):
    def __init__(self, message, *, returncode=None, stdout="", stderr=""):
        super().__init__(message)
        self.returncode = returncode
        self.stdout = stdout
        self.stderr = stderr


def utc_now():
    return datetime.now(timezone.utc).isoformat().replace("+00:00", "Z")


def canonical_json_bytes(value):
    return (
        json.dumps(
            value,
            indent=2,
            sort_keys=True,
            ensure_ascii=True,
        )
        + "\n"
    ).encode("utf-8")


def sha256_bytes(value):
    return hashlib.sha256(value).hexdigest()


def sha256_path(path):
    digest = hashlib.sha256()
    with pathlib.Path(path).open("rb") as handle:
        for chunk in iter(lambda: handle.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def read_regular_bytes(path, *, maximum_bytes=2 * 1024 * 1024):
    path = pathlib.Path(path)
    metadata = path.lstat()
    if path.is_symlink() or not stat.S_ISREG(metadata.st_mode):
        raise GuardFailure(f"input is not a regular file: {path}")
    if metadata.st_size > maximum_bytes:
        raise GuardFailure(f"input is too large: {path}")
    return path.read_bytes()


def write_bytes(path, value, mode=0o600):
    path = pathlib.Path(path)
    with path.open("xb") as handle:
        handle.write(value)
    path.chmod(mode)


def write_json(path, value):
    write_bytes(path, canonical_json_bytes(value))


def run_command(arguments, *, stdin_text=None):
    completed = subprocess.run(
        arguments,
        input=stdin_text,
        text=True,
        capture_output=True,
        check=False,
    )
    if completed.returncode != 0:
        raise CommandFailure(
            f"command failed with exit code {completed.returncode}: "
            f"{arguments[0]}",
            returncode=completed.returncode,
            stdout=completed.stdout,
            stderr=completed.stderr,
        )
    return completed.stdout


@contextlib.contextmanager
def acquire_worker_guard_lock(path=WORKER_GUARD_LOCK):
    path = pathlib.Path(path)
    with path.open("a+", encoding="utf-8") as handle:
        try:
            fcntl.flock(
                handle.fileno(),
                fcntl.LOCK_EX | fcntl.LOCK_NB,
            )
        except BlockingIOError as error:
            raise GuardFailure(
                "worker start/recreate guard lock is already held"
            ) from error
        try:
            yield
        finally:
            fcntl.flock(handle.fileno(), fcntl.LOCK_UN)


def probe_worker_guard_lock(path=WORKER_GUARD_LOCK):
    path = pathlib.Path(path)
    with path.open("a+", encoding="utf-8") as handle:
        try:
            fcntl.flock(
                handle.fileno(),
                fcntl.LOCK_EX | fcntl.LOCK_NB,
            )
        except BlockingIOError:
            return "externally_held"
        fcntl.flock(handle.fileno(), fcntl.LOCK_UN)
        return "available"


def command_error_text(error):
    parts = [str(error)]
    if isinstance(error, CommandFailure):
        if error.stderr.strip():
            parts.append(error.stderr.strip())
        elif error.stdout.strip():
            parts.append(error.stdout.strip())
    return "\n".join(parts)[:4000]


def ensure_output_directory(path, *, fixture_mode):
    requested = pathlib.Path(path)
    if requested.exists() or requested.is_symlink():
        raise GuardFailure(f"output path already exists: {requested}")

    parent = requested.parent.resolve(strict=True)
    resolved = parent / requested.name
    if fixture_mode:
        if os.environ.get(TEST_MODE_ENV) != "1":
            raise GuardFailure(
                f"--fixture requires {TEST_MODE_ENV}=1"
            )
    else:
        root = PRODUCTION_EVIDENCE_ROOT.resolve(strict=True)
        if root != parent and root not in parent.parents:
            raise GuardFailure(
                "live output must be a new directory below "
                f"{PRODUCTION_EVIDENCE_ROOT}"
            )

    resolved.mkdir(mode=0o700)
    if stat.S_IMODE(resolved.stat().st_mode) != 0o700:
        resolved.chmod(0o700)
    return resolved


def normalize_definition(value):
    return re.sub(r"\s+", " ", value.strip())


def _json_array(values):
    return ", ".join("'" + value.replace("'", "''") + "'" for value in values)


PROBE_SQL = f"""
WITH expected_names(name) AS (
    VALUES {", ".join(f"('{name}')" for name in EXPECTED_NAMES)}
),
family AS (
    SELECT idx.oid AS index_oid,
           idx.relname AS index_name,
           idx.relkind AS index_relkind,
           idx.relpersistence AS index_persistence,
           idx.relowner AS index_owner_oid,
           idx.reltablespace AS index_tablespace_oid,
           idx.reloptions AS index_reloptions,
           pi.indrelid AS table_oid,
           tbl.relname AS table_name,
           tbl.relkind AS table_relkind,
           pi.indisunique,
           pi.indisprimary,
           pi.indisexclusion,
           pi.indimmediate,
           pi.indisclustered,
           pi.indisreplident,
           pi.indisvalid,
           pi.indisready,
           pi.indislive,
           pi.indkey::text AS indkey,
           pi.indclass::text AS indclass,
           pi.indcollation::text AS indcollation,
           pi.indoption::text AS indoption,
           pg_get_expr(pi.indpred, pi.indrelid) AS predicate,
           pg_get_expr(pi.indexprs, pi.indrelid) AS expressions,
           pg_get_indexdef(idx.oid) AS definition,
           pg_relation_size(idx.oid)::bigint AS index_bytes,
           COALESCE(st.idx_scan, 0)::bigint AS idx_scan,
           st.last_idx_scan,
           inh.inhparent AS parent_index_oid
    FROM expected_names expected
    JOIN pg_class idx ON idx.relname = expected.name
    JOIN pg_namespace ns
      ON ns.oid = idx.relnamespace
     AND ns.nspname = 'public'
    JOIN pg_index pi ON pi.indexrelid = idx.oid
    JOIN pg_class tbl ON tbl.oid = pi.indrelid
    LEFT JOIN pg_stat_user_indexes st ON st.indexrelid = idx.oid
    LEFT JOIN pg_inherits inh ON inh.inhrelid = idx.oid
),
family_oids AS (
    SELECT index_oid AS oid FROM family
),
target_relations AS (
    SELECT index_oid AS oid FROM family
    UNION
    SELECT table_oid FROM family
),
constraint_rows AS (
    SELECT con.oid AS constraint_oid,
           con.conname AS constraint_name,
           con.contype AS constraint_type,
           con.conrelid AS table_oid,
           con.conindid AS index_oid
    FROM pg_constraint con
    JOIN family_oids target ON target.oid = con.conindid
),
dependency_rows AS (
    SELECT dep.classid::regclass::text AS class_name,
           dep.objid AS object_oid,
           dep.objsubid AS object_sub_id,
           dep.refclassid::regclass::text AS ref_class_name,
           dep.refobjid AS ref_object_oid,
           dep.refobjsubid AS ref_object_sub_id,
           dep.deptype AS dependency_type,
           obj.relname AS object_name,
           ref.relname AS referenced_name
    FROM pg_depend dep
    JOIN family_oids target ON target.oid = dep.objid
    LEFT JOIN pg_class obj
      ON dep.classid = 'pg_class'::regclass
     AND obj.oid = dep.objid
    LEFT JOIN pg_class ref
      ON dep.refclassid = 'pg_class'::regclass
     AND ref.oid = dep.refobjid
),
target_lock_rows AS (
    SELECT locks.pid,
           locks.relation,
           locks.mode,
           locks.granted
    FROM pg_locks locks
    JOIN target_relations target ON target.oid = locks.relation
    WHERE locks.pid <> pg_backend_pid()
),
matching_activity AS (
    SELECT activity.pid,
           activity.application_name,
           activity.backend_type,
           activity.state,
           EXTRACT(
               EPOCH FROM (clock_timestamp() - activity.query_start)
           )::double precision AS query_age_seconds,
           activity.wait_event_type,
           activity.wait_event,
           md5(COALESCE(activity.query, '')) AS query_md5,
           EXISTS (
               SELECT 1
               FROM target_lock_rows locks
               WHERE locks.pid = activity.pid
           ) AS target_relation_lock
    FROM pg_stat_activity activity
    WHERE activity.pid <> pg_backend_pid()
      AND activity.datname = current_database()
      AND activity.state <> 'idle'
      AND (
          activity.application_name IN (
              'fstworker-scraper',
              'fstworker-registration',
              'FST Scraper Worker',
              'fst-max-score-maintenance',
              'fst-max-score-resume',
              'fst-max-score-rollback'
          )
          OR EXISTS (
              SELECT 1
              FROM target_lock_rows locks
              WHERE locks.pid = activity.pid
          )
          OR activity.query ILIKE '%leaderboard_entries%'
      )
),
table_partitions AS (
    SELECT child.oid,
           child.relname,
           parent.relname AS parent_name
    FROM pg_inherits inheritance
    JOIN pg_class child ON child.oid = inheritance.inhrelid
    JOIN pg_class parent ON parent.oid = inheritance.inhparent
    JOIN pg_namespace ns
      ON ns.oid = parent.relnamespace
     AND ns.nspname = 'public'
    WHERE parent.relname = 'leaderboard_entries'
)
SELECT jsonb_build_object(
    'cluster', jsonb_build_object(
        'checkedAtUtc', clock_timestamp(),
        'database', current_database(),
        'databaseOid', (
            SELECT oid
            FROM pg_database
            WHERE datname = current_database()
        ),
        'user', current_user,
        'serverVersion', current_setting('server_version'),
        'serverVersionNum', current_setting('server_version_num')::integer,
        'systemIdentifier', (
            SELECT system_identifier::text
            FROM pg_control_system()
        ),
        'postmasterStartedAt', pg_postmaster_start_time(),
        'databaseStatsReset', (
            SELECT stats_reset
            FROM pg_stat_database
            WHERE datname = current_database()
        )
    ),
    'publication', (
        SELECT jsonb_build_object(
            'currentPublicationId', current_publication_id,
            'previousPublicationId', previous_publication_id,
            'workingPublicationId', working_publication_id,
            'publishedScrapeId', published_scrape_id,
            'publicReadsFrozen', public_reads_frozen,
            'frozenScrapeId', public_reads_frozen_scrape_id,
            'freezeReason', public_reads_frozen_reason,
            'updatedAt', updated_at
        )
        FROM scrape_publication_state
        WHERE id = TRUE
    ),
    'worker', (
        SELECT jsonb_build_object(
            'status', status,
            'lastHeartbeatAt', last_heartbeat_at,
            'message', message
        )
        FROM service_worker_status
        WHERE worker_key = 'scraper'
    ),
    'runtime', jsonb_build_object(
        'waitingLocks', (
            SELECT count(*)
            FROM pg_locks locks
            JOIN pg_stat_activity activity ON activity.pid = locks.pid
            WHERE NOT locks.granted
              AND activity.datname = current_database()
        ),
        'workerBackends', (
            SELECT count(*)
            FROM pg_stat_activity
            WHERE pid <> pg_backend_pid()
              AND datname = current_database()
              AND application_name IN (
                  'fstworker-scraper',
                  'fstworker-registration',
                  'FST Scraper Worker'
              )
        ),
        'maintenanceBackends', (
            SELECT count(*)
            FROM pg_stat_activity
            WHERE pid <> pg_backend_pid()
              AND datname = current_database()
              AND (
                  application_name IN (
                      'fst-max-score-maintenance',
                      'fst-max-score-resume',
                      'fst-max-score-rollback'
                  )
                  OR application_name LIKE 'fst-%maintenance%'
              )
        ),
        'runningScrapes', (
            SELECT count(*) FROM scrape_log WHERE status = 'running'
        ),
        'activePhaseAttempts', (
            SELECT count(*)
            FROM scrape_phase_attempts
            WHERE status = 'running'
        ),
        'targetRelationLocks', (
            SELECT count(*) FROM target_lock_rows
        ),
        'targetWaitingLocks', (
            SELECT count(*)
            FROM target_lock_rows
            WHERE NOT granted
        ),
        'matchingActivity', COALESCE((
            SELECT jsonb_agg(
                to_jsonb(matching_activity)
                ORDER BY query_age_seconds DESC
            )
            FROM matching_activity
        ), '[]'::jsonb)
    ),
    'indexes', COALESCE((
        SELECT jsonb_agg(
            jsonb_build_object(
                'oid', index_oid,
                'name', index_name,
                'relkind', index_relkind,
                'persistence', index_persistence,
                'owner', pg_get_userbyid(index_owner_oid),
                'tablespace', COALESCE((
                    SELECT spcname
                    FROM pg_tablespace
                    WHERE oid = index_tablespace_oid
                ), 'pg_default'),
                'reloptions', index_reloptions,
                'tableOid', table_oid,
                'table', table_name,
                'tableRelkind', table_relkind,
                'parentIndexOid', parent_index_oid,
                'definition', definition,
                'predicate', predicate,
                'expressions', expressions,
                'indkey', indkey,
                'indclass', indclass,
                'indcollation', indcollation,
                'indoption', indoption,
                'isUnique', indisunique,
                'isPrimary', indisprimary,
                'isExclusion', indisexclusion,
                'isImmediate', indimmediate,
                'isClustered', indisclustered,
                'isReplicaIdentity', indisreplident,
                'isValid', indisvalid,
                'isReady', indisready,
                'isLive', indislive,
                'bytes', index_bytes,
                'idxScan', idx_scan,
                'lastIdxScan', last_idx_scan
            )
            ORDER BY index_name
        )
        FROM family
    ), '[]'::jsonb),
    'constraints', COALESCE((
        SELECT jsonb_agg(
            to_jsonb(constraint_rows)
            ORDER BY constraint_oid
        )
        FROM constraint_rows
    ), '[]'::jsonb),
    'dependencies', COALESCE((
        SELECT jsonb_agg(
            to_jsonb(dependency_rows)
            ORDER BY object_oid, ref_object_oid, dependency_type
        )
        FROM dependency_rows
    ), '[]'::jsonb),
    'tablePartitions', COALESCE((
        SELECT jsonb_agg(
            jsonb_build_object(
                'oid', oid,
                'name', relname,
                'parent', parent_name
            )
            ORDER BY relname
        )
        FROM table_partitions
    ), '[]'::jsonb)
)::text;
"""


def run_psql(sql):
    output = run_command(
        [
            "docker",
            "exec",
            "-i",
            POSTGRES_CONTAINER,
            "env",
            f"PGAPPNAME={APPLICATION_NAME}",
            "psql",
            "-X",
            "-A",
            "-t",
            "-v",
            "ON_ERROR_STOP=1",
            "-U",
            DATABASE_USER,
            "-d",
            DATABASE_NAME,
        ],
        stdin_text=sql,
    )
    json_lines = [
        line.strip()
        for line in output.splitlines()
        if line.lstrip().startswith("{")
    ]
    if not json_lines:
        raise GuardFailure("PostgreSQL probe returned no JSON result")
    return json.loads(json_lines[-1])


def inspect_containers():
    raw = run_command(
        [
            "docker",
            "inspect",
            POSTGRES_CONTAINER,
            WORKER_CONTAINER,
            SERVICE_CONTAINER,
            WEB_CONTAINER,
        ]
    )
    rows = json.loads(raw)
    result = {}
    for row in rows:
        name = row.get("Name", "").lstrip("/")
        labels = row.get("Config", {}).get("Labels") or {}
        state = row.get("State") or {}
        health = state.get("Health") or {}
        result[name] = {
            "id": row.get("Id"),
            "imageId": row.get("Image"),
            "imageReference": row.get("Config", {}).get("Image"),
            "composeProject": labels.get("com.docker.compose.project"),
            "composeWorkingDir": labels.get(
                "com.docker.compose.project.working_dir"
            ),
            "composeFiles": labels.get(
                "com.docker.compose.project.config_files"
            ),
            "composeService": labels.get("com.docker.compose.service"),
            "state": state.get("Status"),
            "running": bool(state.get("Running")),
            "health": health.get("Status"),
            "mounts": [
                {
                    "type": mount.get("Type"),
                    "source": mount.get("Source"),
                    "destination": mount.get("Destination"),
                    "readWrite": mount.get("RW"),
                }
                for mount in row.get("Mounts") or []
            ],
        }
    return result


def read_json_url(url):
    request = urllib.request.Request(
        url,
        headers={"User-Agent": "fst-index-retirement-check/1"},
    )
    with urllib.request.urlopen(request, timeout=10) as response:
        if response.status != 200:
            raise GuardFailure(
                f"{url} returned HTTP {response.status}"
            )
        return json.load(response)


def read_text_url(url):
    request = urllib.request.Request(
        url,
        headers={"User-Agent": "fst-index-retirement-check/1"},
    )
    with urllib.request.urlopen(request, timeout=10) as response:
        if response.status != 200:
            raise GuardFailure(
                f"{url} returned HTTP {response.status}"
            )
        return response.read().decode("utf-8", errors="replace")


def collect_live_probe(compose_dir):
    compose_dir = pathlib.Path(compose_dir).resolve(strict=True)
    containers = inspect_containers()
    ready = read_text_url("http://127.0.0.1:8081/readyz").strip()
    service_info = read_json_url(
        "http://127.0.0.1:3001/api/service-info"
    )
    database = run_psql(PROBE_SQL)
    usage = shutil.disk_usage(PRODUCTION_STORAGE_ROOT)
    return {
        "capturedAtUtc": utc_now(),
        "host": {
            "composeDir": str(compose_dir),
            "expectedComposeDir": str(PRODUCTION_COMPOSE_DIR),
            "project": PRODUCTION_PROJECT,
            "storageRoot": str(PRODUCTION_STORAGE_ROOT),
            "filesystem": {
                "totalBytes": usage.total,
                "usedBytes": usage.used,
                "freeBytes": usage.free,
            },
            "containers": containers,
            "serviceReady": ready,
            "serviceInfo": {
                "publishedScrapeId": service_info.get(
                    "publishedScrapeId"
                ),
                "currentUpdateStatus": (
                    service_info.get("currentUpdate") or {}
                ).get("status"),
                "publicReadsFrozen": (
                    service_info.get("publication") or {}
                ).get("publicReadsFrozen"),
            },
        },
        "database": database,
    }


def load_fixture(path):
    if os.environ.get(TEST_MODE_ENV) != "1":
        raise GuardFailure(f"--fixture requires {TEST_MODE_ENV}=1")
    with pathlib.Path(path).open(encoding="utf-8") as handle:
        return json.load(handle)


def validate_container_identity(probe):
    host = probe["host"]
    if pathlib.Path(host["composeDir"]).resolve() != PRODUCTION_COMPOSE_DIR:
        raise GuardFailure("production Compose directory identity mismatch")
    if host.get("project") != PRODUCTION_PROJECT:
        raise GuardFailure("production Compose project identity mismatch")
    if host.get("serviceReady") != "Healthy":
        raise GuardFailure("fstservice readiness is not Healthy")

    containers = host.get("containers") or {}
    required = {
        POSTGRES_CONTAINER: POSTGRES_SERVICE,
        WORKER_CONTAINER: WORKER_SERVICE,
        SERVICE_CONTAINER: SERVICE_CONTAINER,
        WEB_CONTAINER: WEB_CONTAINER,
    }
    for name, service in required.items():
        container = containers.get(name)
        if container is None:
            raise GuardFailure(f"required container is missing: {name}")
        if container.get("composeProject") != PRODUCTION_PROJECT:
            raise GuardFailure(
                f"{name} belongs to the wrong Compose project"
            )
        if pathlib.Path(
            container.get("composeWorkingDir") or "/"
        ).resolve() != PRODUCTION_COMPOSE_DIR:
            raise GuardFailure(
                f"{name} has the wrong Compose working directory"
            )
        if container.get("composeService") != service:
            raise GuardFailure(
                f"{name} has the wrong Compose service identity"
            )

    postgres = containers[POSTGRES_CONTAINER]
    if (
        not postgres.get("running")
        or postgres.get("health") != "healthy"
    ):
        raise GuardFailure("PostgreSQL container is not healthy")
    data_mounts = [
        mount
        for mount in postgres.get("mounts") or []
        if mount.get("destination") == "/var/lib/postgresql/data"
    ]
    if len(data_mounts) != 1:
        raise GuardFailure(
            "PostgreSQL must have one exact PGDATA mount"
        )
    source = pathlib.Path(data_mounts[0]["source"]).resolve()
    if (
        source != PRODUCTION_STORAGE_ROOT
        and PRODUCTION_STORAGE_ROOT not in source.parents
    ):
        raise GuardFailure("PostgreSQL PGDATA is not on the FST drive")
    if not data_mounts[0].get("readWrite"):
        raise GuardFailure("PostgreSQL PGDATA is not writable")

    worker = containers[WORKER_CONTAINER]
    if worker.get("running") or worker.get("state") not in {
        "created",
        "exited",
    }:
        raise GuardFailure("fstworker must be offline")

    service = containers[SERVICE_CONTAINER]
    web = containers[WEB_CONTAINER]
    if not service.get("running") or service.get("health") != "healthy":
        raise GuardFailure("fstservice container is not healthy")
    if not web.get("running") or web.get("health") != "healthy":
        raise GuardFailure("festivalweb container is not healthy")


def validate_runtime_guards(probe):
    validate_container_identity(probe)
    database = probe["database"]
    cluster = database.get("cluster") or {}
    if cluster.get("database") != DATABASE_NAME:
        raise GuardFailure("database name identity mismatch")
    if cluster.get("user") != DATABASE_USER:
        raise GuardFailure("database user identity mismatch")
    if not str(cluster.get("systemIdentifier") or "").isdigit():
        raise GuardFailure("PostgreSQL system identifier is unavailable")
    version = int(cluster.get("serverVersionNum") or 0)
    if version < 170000 or version >= 180000:
        raise GuardFailure("retirement package requires PostgreSQL 17")

    publication = database.get("publication") or {}
    if publication.get("workingPublicationId") is not None:
        raise GuardFailure("a working publication exists")
    if publication.get("publicReadsFrozen"):
        raise GuardFailure("public reads are frozen")
    if not publication.get("currentPublicationId"):
        raise GuardFailure("current publication is missing")
    if not publication.get("publishedScrapeId"):
        raise GuardFailure("published scrape is missing")

    service_info = probe["host"].get("serviceInfo") or {}
    if service_info.get("currentUpdateStatus") != "idle":
        raise GuardFailure("service currentUpdate is not idle")
    if service_info.get("publicReadsFrozen"):
        raise GuardFailure("service reports frozen public reads")
    if (
        service_info.get("publishedScrapeId")
        != publication.get("publishedScrapeId")
    ):
        raise GuardFailure(
            "service and database published scrape identities differ"
        )

    worker = database.get("worker") or {}
    if worker.get("status") != "offline":
        raise GuardFailure("durable worker status is not offline")

    runtime = database.get("runtime") or {}
    zero_fields = (
        "waitingLocks",
        "workerBackends",
        "maintenanceBackends",
        "runningScrapes",
        "activePhaseAttempts",
        "targetRelationLocks",
        "targetWaitingLocks",
    )
    for field in zero_fields:
        if int(runtime.get(field) or 0) != 0:
            raise GuardFailure(
                f"runtime guard is nonzero: {field}={runtime.get(field)}"
            )
    if runtime.get("matchingActivity"):
        raise GuardFailure(
            "active query or backend references the retirement surface"
        )


def family_state(probe):
    rows = probe["database"].get("indexes") or []
    names = {row.get("name") for row in rows}
    if not rows:
        return "absent"
    if names != set(EXPECTED_NAMES) or len(rows) != len(INDEX_SPECS):
        return "partial"
    return "present"


def validate_dependencies(probe, indexes_by_name):
    constraints = probe["database"].get("constraints") or []
    if constraints:
        raise GuardFailure(
            "ix_le_song_rank is owned by or backs a constraint"
        )

    dependencies = probe["database"].get("dependencies") or []
    by_oid = {}
    for dependency in dependencies:
        by_oid.setdefault(int(dependency["object_oid"]), []).append(
            dependency
        )

    for spec in INDEX_SPECS:
        row = indexes_by_name[spec["name"]]
        owned = by_oid.get(int(row["oid"]), [])
        if spec["parent"] is None:
            if len(owned) != 3:
                raise GuardFailure(
                    "parent index dependency count changed"
                )
            if {
                (
                    item.get("dependency_type"),
                    item.get("referenced_name"),
                    int(item.get("ref_object_sub_id") or 0),
                )
                for item in owned
            } != {
                ("a", "leaderboard_entries", 1),
                ("a", "leaderboard_entries", 2),
                ("a", "leaderboard_entries", 10),
            }:
                raise GuardFailure(
                    "parent index dependencies changed"
                )
            continue

        if len(owned) != 5:
            raise GuardFailure(
                f"child index dependency count changed: {spec['name']}"
            )
        automatic = {
            int(item.get("ref_object_sub_id") or 0)
            for item in owned
            if item.get("dependency_type") == "a"
            and item.get("referenced_name") == spec["table"]
        }
        partition_primary = [
            item
            for item in owned
            if item.get("dependency_type") == "P"
            and item.get("referenced_name") == "ix_le_song_rank"
        ]
        partition_secondary = [
            item
            for item in owned
            if item.get("dependency_type") == "S"
            and item.get("referenced_name") == spec["table"]
        ]
        if (
            automatic != {1, 2, 10}
            or len(partition_primary) != 1
            or len(partition_secondary) != 1
        ):
            raise GuardFailure(
                f"child index dependencies changed: {spec['name']}"
            )


def catalog_evidence(probe):
    dependencies = probe["database"].get("dependencies") or []
    constraints = probe["database"].get("constraints") or []
    partitions = probe["database"].get("tablePartitions") or []
    return {
        "dependencyCount": len(dependencies),
        "dependencySha256": sha256_bytes(
            canonical_json_bytes(dependencies)
        ),
        "constraintCount": len(constraints),
        "constraintSha256": sha256_bytes(
            canonical_json_bytes(constraints)
        ),
        "tablePartitionCount": len(partitions),
        "tablePartitionSha256": sha256_bytes(
            canonical_json_bytes(partitions)
        ),
    }


def validate_present_family(probe, *, expected_manifest=None):
    rows = probe["database"].get("indexes") or []
    indexes = {row["name"]: row for row in rows}
    if set(indexes) != set(EXPECTED_NAMES):
        raise GuardFailure("index family names changed")

    parent_oid = int(indexes["ix_le_song_rank"]["oid"])
    total_bytes = 0
    total_scans = 0
    seen_oids = set()
    for spec in INDEX_SPECS:
        row = indexes[spec["name"]]
        oid = int(row["oid"])
        if oid <= 0 or oid in seen_oids:
            raise GuardFailure("index OIDs are missing or duplicated")
        seen_oids.add(oid)
        if row.get("table") != spec["table"]:
            raise GuardFailure(
                f"index table changed: {spec['name']}"
            )
        if row.get("relkind") != spec["relkind"]:
            raise GuardFailure(
                f"index relkind changed: {spec['name']}"
            )
        actual_parent_oid = (
            None
            if row.get("parentIndexOid") is None
            else int(row["parentIndexOid"])
        )
        expected_parent_oid = (
            None if spec["parent"] is None else parent_oid
        )
        if actual_parent_oid != expected_parent_oid:
            raise GuardFailure(
                f"index attachment changed: {spec['name']}"
            )
        if normalize_definition(row.get("definition") or "") != (
            normalize_definition(spec["definition"])
        ):
            raise GuardFailure(
                f"index definition changed: {spec['name']}"
            )
        if row.get("predicate") is not None:
            raise GuardFailure(
                f"index became partial: {spec['name']}"
            )
        if row.get("expressions") is not None:
            raise GuardFailure(
                f"index became expression-based: {spec['name']}"
            )
        if (
            row.get("isUnique")
            or row.get("isPrimary")
            or row.get("isExclusion")
            or row.get("isClustered")
            or row.get("isReplicaIdentity")
        ):
            raise GuardFailure(
                f"index ownership/semantics changed: {spec['name']}"
            )
        if not (
            row.get("isImmediate")
            and row.get("isValid")
            and row.get("isReady")
            and row.get("isLive")
        ):
            raise GuardFailure(
                f"index validity changed: {spec['name']}"
            )
        if row.get("persistence") != "p":
            raise GuardFailure(
                f"index persistence changed: {spec['name']}"
            )
        scans = int(row.get("idxScan") or 0)
        if scans != 0 or row.get("lastIdxScan") is not None:
            raise GuardFailure(
                f"zero-use observation changed: {spec['name']}"
            )
        total_bytes += int(row.get("bytes") or 0)
        total_scans += scans

    if total_bytes <= 0:
        raise GuardFailure("index family has no measurable leaf bytes")
    if total_scans != 0:
        raise GuardFailure("index family has observed scans")

    expected_partitions = {
        spec["table"]
        for spec in INDEX_SPECS
        if spec["parent"] is not None
    }
    actual_partitions = {
        row.get("name")
        for row in probe["database"].get("tablePartitions") or []
    }
    if actual_partitions != expected_partitions:
        raise GuardFailure("leaderboard_entries partitions changed")

    validate_dependencies(probe, indexes)

    if expected_manifest is not None:
        expected_indexes = {
            row["name"]: row
            for row in expected_manifest["family"]["indexes"]
        }
        if set(expected_indexes) != set(indexes):
            raise GuardFailure("manifest index inventory changed")
        for name, row in indexes.items():
            expected = expected_indexes[name]
            compared = (
                "oid",
                "tableOid",
                "parentIndexOid",
                "definition",
                "bytes",
                "idxScan",
                "lastIdxScan",
            )
            for field in compared:
                actual_value = row.get(field)
                expected_value = expected.get(field)
                if field in {"oid", "tableOid", "parentIndexOid"}:
                    actual_value = (
                        None
                        if actual_value is None
                        else int(actual_value)
                    )
                    expected_value = (
                        None
                        if expected_value is None
                        else int(expected_value)
                    )
                if actual_value != expected_value:
                    raise GuardFailure(
                        f"manifest drift for {name}: {field}"
                    )
        if (
            total_bytes
            != int(expected_manifest["family"]["totalBytes"])
        ):
            raise GuardFailure("manifest total byte count changed")
        current_catalog = catalog_evidence(probe)
        expected_catalog = expected_manifest["family"][
            "catalogEvidence"
        ]
        if current_catalog != expected_catalog:
            raise GuardFailure("manifest catalog evidence changed")

    return indexes, total_bytes


def validate_manifest_identity(probe, manifest):
    if manifest.get("formatVersion") != FORMAT_VERSION:
        raise GuardFailure("unsupported manifest format")
    if manifest.get("familyId") != FAMILY_ID:
        raise GuardFailure("manifest targets the wrong index family")
    tooling = manifest.get("tooling") or {}
    if tooling.get("pythonSha256") != sha256_path(SCRIPT_PATH):
        raise GuardFailure("manifest Python tool changed")
    if tooling.get("wrapperSha256") != sha256_path(WRAPPER_PATH):
        raise GuardFailure("manifest shell wrapper changed")

    host = probe["host"]
    project = manifest.get("project") or {}
    postgres = host["containers"][POSTGRES_CONTAINER]
    if project.get("composeDir") != host.get("composeDir"):
        raise GuardFailure("manifest Compose directory changed")
    if project.get("composeProject") != host.get("project"):
        raise GuardFailure("manifest Compose project changed")
    if project.get("workerGuardLock") != str(WORKER_GUARD_LOCK):
        raise GuardFailure("manifest worker guard lock changed")
    if project.get("postgresContainerId") != postgres.get("id"):
        raise GuardFailure("manifest PostgreSQL container changed")
    if project.get("postgresImageId") != postgres.get("imageId"):
        raise GuardFailure("manifest PostgreSQL image changed")
    pgdata = next(
        mount
        for mount in postgres["mounts"]
        if mount["destination"] == "/var/lib/postgresql/data"
    )
    if project.get("pgdataSource") != pgdata.get("source"):
        raise GuardFailure("manifest PGDATA source changed")

    cluster = probe["database"]["cluster"]
    expected_cluster = manifest.get("cluster") or {}
    for field in (
        "database",
        "databaseOid",
        "user",
        "serverVersionNum",
        "systemIdentifier",
        "postmasterStartedAt",
        "databaseStatsReset",
    ):
        if cluster.get(field) != expected_cluster.get(field):
            raise GuardFailure(
                f"manifest cluster identity changed: {field}"
            )

    publication = probe["database"]["publication"]
    expected_publication = manifest.get("publication") or {}
    for field in (
        "currentPublicationId",
        "previousPublicationId",
        "workingPublicationId",
        "publishedScrapeId",
        "publicReadsFrozen",
        "frozenScrapeId",
        "freezeReason",
    ):
        if publication.get(field) != expected_publication.get(field):
            raise GuardFailure(
                f"manifest publication identity changed: {field}"
            )


def build_zero_use_observation(probe, indexes, total_bytes):
    cluster = probe["database"]["cluster"]
    rows = []
    for spec in INDEX_SPECS:
        row = indexes[spec["name"]]
        rows.append(
            {
                "name": spec["name"],
                "oid": int(row["oid"]),
                "bytes": row["bytes"],
                "idxScan": row["idxScan"],
                "lastIdxScan": row["lastIdxScan"],
            }
        )
    return {
        "formatVersion": FORMAT_VERSION,
        "familyId": FAMILY_ID,
        "capturedAtUtc": probe["capturedAtUtc"],
        "postmasterStartedAt": cluster.get("postmasterStartedAt"),
        "databaseStatsReset": cluster.get("databaseStatsReset"),
        "totalBytes": total_bytes,
        "totalIdxScan": 0,
        "indexes": rows,
        "caveat": (
            "Zero scans are an observation over the available cumulative "
            "statistics window, not proof of lifetime nonuse. A null "
            "databaseStatsReset means no explicit reset timestamp is "
            "reported; crashes, immediate shutdowns, or statistics resets "
            "can shorten retained history."
        ),
    }


def render_rollback_sql(indexes):
    lines = [
        r"\set ON_ERROR_STOP on",
        f"SET lock_timeout = '{LOCK_TIMEOUT}';",
        f"SET statement_timeout = '{STATEMENT_TIMEOUT}';",
        "",
        "-- Step 1: create the empty partitioned parent metadata.",
        indexes["ix_le_song_rank"]["definition"] + ";",
        "",
        "-- Step 2: build every leaf independently without blocking writes.",
        f"SET statement_timeout = '{ROLLBACK_STATEMENT_TIMEOUT}';",
    ]
    for spec in INDEX_SPECS[1:]:
        definition = indexes[spec["name"]]["definition"]
        concurrent = definition.replace(
            "CREATE INDEX ",
            "CREATE INDEX CONCURRENTLY ",
            1,
        )
        lines.append(concurrent + ";")
    lines.extend(
        [
            "",
            "-- Step 3: attach the exact equivalent leaves to the parent.",
            f"SET statement_timeout = '{STATEMENT_TIMEOUT}';",
        ]
    )
    for spec in INDEX_SPECS[1:]:
        lines.append(
            "ALTER INDEX public.ix_le_song_rank ATTACH PARTITION "
            f"public.{spec['name']};"
        )
    lines.extend(
        [
            "",
            "-- Validate with this package in --check mode before "
            "restarting fstworker.",
        ]
    )
    return "\n".join(lines) + "\n"


def sql_literal(value):
    return "'" + str(value).replace("'", "''") + "'"


def render_drop_sql(manifest):
    expected_rows = []
    for row in manifest["family"]["indexes"]:
        expected_rows.append(
            "("
            + ", ".join(
                [
                    sql_literal(row["name"]),
                    str(int(row["oid"])),
                    sql_literal(row["table"]),
                    str(int(row["tableOid"])),
                    (
                        "NULL"
                        if row["parentIndexOid"] is None
                        else str(int(row["parentIndexOid"]))
                    ),
                    str(int(row["bytes"])),
                    sql_literal(normalize_definition(row["definition"])),
                ]
            )
            + ")"
        )
    values = ",\n        ".join(expected_rows)
    cluster = manifest["cluster"]
    publication = manifest["publication"]
    total_bytes = int(manifest["family"]["totalBytes"])
    return f"""\\set ON_ERROR_STOP on
BEGIN;
SET LOCAL application_name = '{APPLICATION_NAME}';
SET LOCAL lock_timeout = '{LOCK_TIMEOUT}';
SET LOCAL statement_timeout = '{STATEMENT_TIMEOUT}';
SET LOCAL idle_in_transaction_session_timeout = '{STATEMENT_TIMEOUT}';

CREATE TEMP TABLE expected_ix_le_song_rank (
    index_name text PRIMARY KEY,
    index_oid oid NOT NULL,
    table_name text NOT NULL,
    table_oid oid NOT NULL,
    parent_index_oid oid,
    index_bytes bigint NOT NULL,
    definition text NOT NULL
) ON COMMIT DROP;

INSERT INTO expected_ix_le_song_rank VALUES
        {values};

DO $retirement$
DECLARE
    mismatch_count integer;
BEGIN
    IF current_database() <> {sql_literal(cluster["database"])} THEN
        RAISE EXCEPTION 'database identity changed';
    END IF;
    IF (SELECT system_identifier::text FROM pg_control_system())
       <> {sql_literal(cluster["systemIdentifier"])} THEN
        RAISE EXCEPTION 'cluster system identifier changed';
    END IF;
    IF NOT pg_try_advisory_xact_lock(
        hashtextextended({sql_literal(RETIREMENT_LOCK_NAME)}, 0)
    ) THEN
        RAISE EXCEPTION 'another retirement operation is active';
    END IF;
    IF NOT pg_try_advisory_xact_lock_shared(
        {PUBLICATION_ADVISORY_LOCK_KEY}
    ) THEN
        RAISE EXCEPTION 'publication commit lock is active';
    END IF;
    IF NOT pg_try_advisory_xact_lock(
        {REGISTRATION_MUTATION_ADVISORY_LOCK_KEY}
    ) THEN
        RAISE EXCEPTION 'registration mutation gate is active';
    END IF;
    IF NOT EXISTS (
        SELECT 1
        FROM scrape_publication_state
        WHERE id = TRUE
          AND current_publication_id = {
              int(publication["currentPublicationId"])
          }
          AND published_scrape_id = {
              int(publication["publishedScrapeId"])
          }
          AND previous_publication_id IS NOT DISTINCT FROM {
              "NULL" if publication["previousPublicationId"] is None
              else int(publication["previousPublicationId"])
          }
          AND working_publication_id IS NULL
          AND public_reads_frozen = FALSE
          AND public_reads_frozen_scrape_id IS NULL
          AND public_reads_frozen_reason IS NULL
    ) THEN
        RAISE EXCEPTION 'publication state changed';
    END IF;
    IF EXISTS (
        SELECT 1
        FROM pg_locks locks
        JOIN pg_stat_activity activity ON activity.pid = locks.pid
        WHERE NOT locks.granted
          AND activity.datname = current_database()
    ) THEN
        RAISE EXCEPTION 'waiting lock appeared';
    END IF;
    IF EXISTS (
        SELECT 1
        FROM pg_stat_activity
        WHERE pid <> pg_backend_pid()
          AND datname = current_database()
          AND state <> 'idle'
          AND (
              application_name IN (
                  'fstworker-scraper',
                  'fstworker-registration',
                  'FST Scraper Worker',
                  'fst-max-score-maintenance',
                  'fst-max-score-resume',
                  'fst-max-score-rollback'
              )
              OR application_name LIKE 'fst-%maintenance%'
              OR query ILIKE '%leaderboard_entries%'
          )
    ) THEN
        RAISE EXCEPTION 'active worker, maintenance, or target query appeared';
    END IF;
    IF EXISTS (
        SELECT 1
        FROM pg_locks locks
        WHERE locks.pid <> pg_backend_pid()
          AND locks.relation IN (
              SELECT index_oid FROM expected_ix_le_song_rank
              UNION
              SELECT table_oid FROM expected_ix_le_song_rank
          )
    ) THEN
        RAISE EXCEPTION 'target relation lock appeared';
    END IF;
    IF EXISTS (
        SELECT 1
        FROM scrape_log
        WHERE status = 'running'
    ) OR EXISTS (
        SELECT 1
        FROM scrape_phase_attempts
        WHERE status = 'running'
    ) THEN
        RAISE EXCEPTION 'active scrape or phase attempt appeared';
    END IF;
    IF EXISTS (
        SELECT 1
        FROM pg_constraint constraint_row
        JOIN expected_ix_le_song_rank expected
          ON expected.index_oid = constraint_row.conindid
    ) THEN
        RAISE EXCEPTION 'constraint ownership appeared';
    END IF;
    IF (
        SELECT count(*)
        FROM pg_depend dependency
        JOIN expected_ix_le_song_rank expected
          ON expected.index_oid = dependency.objid
        WHERE dependency.classid = 'pg_class'::regclass
    ) <> 48 THEN
        RAISE EXCEPTION 'dependency count changed';
    END IF;
    IF EXISTS (
        SELECT 1
        FROM pg_depend dependency
        JOIN expected_ix_le_song_rank expected
          ON expected.index_oid = dependency.objid
        WHERE dependency.classid = 'pg_class'::regclass
          AND NOT (
              (
                  expected.index_name = 'ix_le_song_rank'
                  AND dependency.deptype = 'a'
                  AND dependency.refclassid = 'pg_class'::regclass
                  AND dependency.refobjid = expected.table_oid
                  AND dependency.refobjsubid IN (1, 2, 10)
              )
              OR (
                  expected.index_name <> 'ix_le_song_rank'
                  AND dependency.deptype = 'a'
                  AND dependency.refclassid = 'pg_class'::regclass
                  AND dependency.refobjid = expected.table_oid
                  AND dependency.refobjsubid IN (1, 2, 10)
              )
              OR (
                  expected.index_name <> 'ix_le_song_rank'
                  AND dependency.deptype = 'P'
                  AND dependency.refclassid = 'pg_class'::regclass
                  AND dependency.refobjid = (
                      SELECT index_oid
                      FROM expected_ix_le_song_rank
                      WHERE index_name = 'ix_le_song_rank'
                  )
                  AND dependency.refobjsubid = 0
              )
              OR (
                  expected.index_name <> 'ix_le_song_rank'
                  AND dependency.deptype = 'S'
                  AND dependency.refclassid = 'pg_class'::regclass
                  AND dependency.refobjid = expected.table_oid
                  AND dependency.refobjsubid = 0
              )
          )
    ) THEN
        RAISE EXCEPTION 'dependency ownership changed';
    END IF;

    WITH actual AS (
        SELECT idx.relname AS index_name,
               idx.oid AS index_oid,
               tbl.relname AS table_name,
               tbl.oid AS table_oid,
               inheritance.inhparent AS parent_index_oid,
               pg_relation_size(idx.oid)::bigint AS index_bytes,
               regexp_replace(
                   btrim(pg_get_indexdef(idx.oid)),
                   '\\s+',
                   ' ',
                   'g'
               ) AS definition,
               COALESCE(stats.idx_scan, 0)::bigint AS idx_scan,
               stats.last_idx_scan,
               index_meta.indisunique,
               index_meta.indisprimary,
               index_meta.indisexclusion,
               index_meta.indisclustered,
               index_meta.indisreplident,
               index_meta.indisvalid,
               index_meta.indisready,
               index_meta.indislive
        FROM pg_class idx
        JOIN pg_namespace namespace
          ON namespace.oid = idx.relnamespace
         AND namespace.nspname = 'public'
        JOIN pg_index index_meta ON index_meta.indexrelid = idx.oid
        JOIN pg_class tbl ON tbl.oid = index_meta.indrelid
        LEFT JOIN pg_inherits inheritance
          ON inheritance.inhrelid = idx.oid
        LEFT JOIN pg_stat_user_indexes stats
          ON stats.indexrelid = idx.oid
        WHERE idx.relname IN (
            SELECT index_name FROM expected_ix_le_song_rank
        )
    )
    SELECT count(*) INTO mismatch_count
    FROM expected_ix_le_song_rank expected
    FULL JOIN actual USING (index_name)
    WHERE actual.index_oid IS DISTINCT FROM expected.index_oid
       OR actual.table_name IS DISTINCT FROM expected.table_name
       OR actual.table_oid IS DISTINCT FROM expected.table_oid
       OR actual.parent_index_oid IS DISTINCT FROM expected.parent_index_oid
       OR actual.index_bytes IS DISTINCT FROM expected.index_bytes
       OR actual.definition IS DISTINCT FROM expected.definition
       OR actual.idx_scan IS DISTINCT FROM 0
       OR actual.last_idx_scan IS NOT NULL
       OR actual.indisunique
       OR actual.indisprimary
       OR actual.indisexclusion
       OR actual.indisclustered
       OR actual.indisreplident
       OR NOT actual.indisvalid
       OR NOT actual.indisready
       OR NOT actual.indislive;
    IF mismatch_count <> 0 THEN
        RAISE EXCEPTION 'exact index catalog changed';
    END IF;
END
$retirement$;

SELECT jsonb_build_object(
    'record', 'drop_before',
    'indexCount', count(*),
    'totalBytes', sum(pg_relation_size(index_oid))
)::text
FROM expected_ix_le_song_rank;

DROP INDEX public.ix_le_song_rank;

DO $retirement$
BEGIN
    IF EXISTS (
        SELECT 1
        FROM pg_class relation
        JOIN pg_namespace namespace
          ON namespace.oid = relation.relnamespace
         AND namespace.nspname = 'public'
        WHERE relation.relname IN (
            SELECT index_name FROM expected_ix_le_song_rank
        )
    ) THEN
        RAISE EXCEPTION 'index family remains after drop';
    END IF;
END
$retirement$;

SELECT jsonb_build_object(
    'record', 'drop_after',
    'indexCount', 0,
    'catalogBytesRemoved', {total_bytes}
)::text;
COMMIT;
"""


def validate_drop_sql(sql):
    normalized = sql.upper()
    if "DROP INDEX CONCURRENTLY" in normalized:
        raise GuardFailure(
            "partitioned parent cannot use DROP INDEX CONCURRENTLY"
        )
    if "CASCADE" in normalized:
        raise GuardFailure("index retirement must not use CASCADE")
    expected = "DROP INDEX PUBLIC.IX_LE_SONG_RANK;"
    if normalized.count(expected) != 1:
        raise GuardFailure("drop SQL must target the exact parent once")
    if f"LOCK_TIMEOUT = '{LOCK_TIMEOUT.upper()}'" not in normalized:
        raise GuardFailure("drop SQL is missing the lock timeout")
    if f"STATEMENT_TIMEOUT = '{STATEMENT_TIMEOUT.upper()}'" not in normalized:
        raise GuardFailure("drop SQL is missing the statement timeout")


def build_manifest(probe, indexes, total_bytes, observation_sha, rollback_sha):
    postgres = probe["host"]["containers"][POSTGRES_CONTAINER]
    pgdata = next(
        mount
        for mount in postgres["mounts"]
        if mount["destination"] == "/var/lib/postgresql/data"
    )
    ordered_indexes = []
    for spec in INDEX_SPECS:
        row = indexes[spec["name"]]
        ordered_indexes.append(
            {
                "name": row["name"],
                "oid": int(row["oid"]),
                "table": row["table"],
                "tableOid": int(row["tableOid"]),
                "parentIndexOid": (
                    None
                    if row["parentIndexOid"] is None
                    else int(row["parentIndexOid"])
                ),
                "relkind": row["relkind"],
                "definition": row["definition"],
                "bytes": row["bytes"],
                "idxScan": row["idxScan"],
                "lastIdxScan": row["lastIdxScan"],
            }
        )
    return {
        "formatVersion": FORMAT_VERSION,
        "familyId": FAMILY_ID,
        "generatedAtUtc": utc_now(),
        "tooling": {
            "pythonSha256": sha256_path(SCRIPT_PATH),
            "wrapperSha256": sha256_path(WRAPPER_PATH),
        },
        "project": {
            "composeDir": probe["host"]["composeDir"],
            "composeProject": probe["host"]["project"],
            "postgresContainerId": postgres["id"],
            "postgresImageId": postgres["imageId"],
            "pgdataSource": pgdata["source"],
            "workerGuardLock": str(WORKER_GUARD_LOCK),
            "workerGuardLockState": probe["host"].get(
                "workerGuardLockState"
            ),
        },
        "cluster": probe["database"]["cluster"],
        "publication": probe["database"]["publication"],
        "family": {
            "parentName": "ix_le_song_rank",
            "indexCount": len(ordered_indexes),
            "childCount": len(ordered_indexes) - 1,
            "totalBytes": total_bytes,
            "totalIdxScan": 0,
            "constraintCount": 0,
            "catalogEvidence": catalog_evidence(probe),
            "indexes": ordered_indexes,
        },
        "zeroUseObservation": {
            "file": "zero-use-observation.json",
            "sha256": observation_sha,
        },
        "rollback": {
            "file": "rollback.sql",
            "sha256": rollback_sha,
            "mechanics": (
                "create parent ON ONLY; build nine leaves CONCURRENTLY; "
                "attach leaves in canonical order"
            ),
        },
        "drop": {
            "mechanics": (
                "one normal DROP INDEX of the partitioned parent inside "
                "a short fail-closed transaction; attached leaves are "
                "dropped automatically"
            ),
            "lockTimeout": LOCK_TIMEOUT,
            "statementTimeout": STATEMENT_TIMEOUT,
            "cascade": False,
            "concurrent": False,
        },
    }


def compare_expected_digests(
    args,
    manifest,
    rollback_bytes,
    manifest_bytes,
):
    actual_manifest = sha256_bytes(manifest_bytes)
    if actual_manifest != args.expected_manifest_sha256:
        raise GuardFailure("manifest SHA-256 mismatch")
    observation_bytes = read_regular_bytes(args.zero_use_observation)
    if sha256_bytes(observation_bytes) != args.expected_zero_use_sha256:
        raise GuardFailure("zero-use observation file SHA-256 mismatch")
    observation = manifest.get("zeroUseObservation") or {}
    if observation.get("file") != "zero-use-observation.json":
        raise GuardFailure("manifest zero-use filename changed")
    if observation.get("sha256") != args.expected_zero_use_sha256:
        raise GuardFailure("zero-use observation SHA-256 mismatch")
    observed = json.loads(observation_bytes)
    if observed.get("formatVersion") != FORMAT_VERSION:
        raise GuardFailure("unsupported zero-use observation format")
    if observed.get("familyId") != FAMILY_ID:
        raise GuardFailure(
            "zero-use observation targets the wrong family"
        )
    if observed.get("totalIdxScan") != 0:
        raise GuardFailure("zero-use observation reports scans")
    reviewed_rollback = read_regular_bytes(args.rollback_file)
    if reviewed_rollback != rollback_bytes:
        raise GuardFailure("reviewed rollback DDL changed")
    actual_rollback = sha256_bytes(rollback_bytes)
    if actual_rollback != args.expected_rollback_sha256:
        raise GuardFailure("rollback SHA-256 mismatch")
    if (
        (manifest.get("rollback") or {}).get("sha256")
        != args.expected_rollback_sha256
    ):
        raise GuardFailure("manifest rollback SHA-256 mismatch")
    if (manifest.get("rollback") or {}).get("file") != "rollback.sql":
        raise GuardFailure("manifest rollback filename changed")
    return manifest_bytes


def classify_failed_execution(probe):
    state = family_state(probe)
    if state == "present":
        return "failed_no_catalog_change"
    if state == "absent":
        return "committed_requires_reconciliation"
    return "failed_partial_catalog"


def write_checksums(output):
    files = sorted(
        path
        for path in output.iterdir()
        if path.is_file() and path.name != "SHA256SUMS"
    )
    content = "".join(
        f"{sha256_path(path)}  {path.name}\n" for path in files
    )
    write_bytes(output / "SHA256SUMS", content.encode("utf-8"))


def check_mode(args, output, probe):
    validate_runtime_guards(probe)
    state = family_state(probe)
    write_json(output / "probe.json", probe)
    if state == "absent":
        report = {
            "formatVersion": FORMAT_VERSION,
            "mode": "check",
            "outcome": "already_absent",
            "mutationAttempted": False,
            "familyId": FAMILY_ID,
            "completedAtUtc": utc_now(),
        }
        write_json(output / "report.json", report)
        write_checksums(output)
        return report
    if state != "present":
        raise GuardFailure("index family is partially present")

    indexes, total_bytes = validate_present_family(probe)
    observation = build_zero_use_observation(
        probe,
        indexes,
        total_bytes,
    )
    observation_bytes = canonical_json_bytes(observation)
    write_bytes(
        output / "zero-use-observation.json",
        observation_bytes,
    )
    rollback = render_rollback_sql(indexes).encode("utf-8")
    write_bytes(output / "rollback.sql", rollback)
    manifest = build_manifest(
        probe,
        indexes,
        total_bytes,
        sha256_bytes(observation_bytes),
        sha256_bytes(rollback),
    )
    manifest_bytes = canonical_json_bytes(manifest)
    write_bytes(output / "manifest.json", manifest_bytes)
    drop_sql = render_drop_sql(manifest)
    validate_drop_sql(drop_sql)
    write_bytes(output / "drop-plan.sql", drop_sql.encode("utf-8"))
    report = {
        "formatVersion": FORMAT_VERSION,
        "mode": "check",
        "outcome": "validated",
        "mutationAttempted": False,
        "familyId": FAMILY_ID,
        "indexCount": 10,
        "childCount": 9,
        "totalBytes": total_bytes,
        "totalIdxScan": 0,
        "manifestSha256": sha256_bytes(manifest_bytes),
        "zeroUseObservationSha256": sha256_bytes(
            observation_bytes
        ),
        "rollbackSha256": sha256_bytes(rollback),
        "dropMechanics": manifest["drop"]["mechanics"],
        "rollbackMechanics": manifest["rollback"]["mechanics"],
        "completedAtUtc": utc_now(),
    }
    write_json(output / "report.json", report)
    write_checksums(output)
    return report


def execute_mode(args, output, probe, *, fixture_result=None):
    validate_runtime_guards(probe)
    input_paths = [
        pathlib.Path(args.manifest),
        pathlib.Path(args.zero_use_observation),
        pathlib.Path(args.rollback_file),
    ]
    if len({path.resolve().parent for path in input_paths}) != 1:
        raise GuardFailure(
            "manifest, zero-use observation, and rollback must share "
            "one reviewed package directory"
        )
    if [path.name for path in input_paths] != [
        "manifest.json",
        "zero-use-observation.json",
        "rollback.sql",
    ]:
        raise GuardFailure("reviewed package filenames changed")
    manifest_bytes = read_regular_bytes(args.manifest)
    manifest = json.loads(manifest_bytes)
    validate_manifest_identity(probe, manifest)

    manifest_indexes = {
        row["name"]: row for row in manifest["family"]["indexes"]
    }
    rollback_bytes = render_rollback_sql(
        manifest_indexes
    ).encode("utf-8")
    manifest_bytes = compare_expected_digests(
        args,
        manifest,
        rollback_bytes,
        manifest_bytes,
    )
    write_bytes(output / "source-manifest.json", manifest_bytes)
    write_bytes(output / "rollback.sql", rollback_bytes)
    write_json(output / "probe-before.json", probe)

    state = family_state(probe)
    if state == "absent":
        report = {
            "formatVersion": FORMAT_VERSION,
            "mode": "execute",
            "outcome": "already_absent",
            "mutationAttempted": False,
            "familyId": FAMILY_ID,
            "catalogBytesBefore": 0,
            "catalogBytesAfter": 0,
            "catalogBytesRemoved": 0,
            "completedAtUtc": utc_now(),
        }
        write_json(output / "report.json", report)
        write_checksums(output)
        return report
    if state != "present":
        raise GuardFailure("index family is partially present")

    _, total_bytes = validate_present_family(
        probe,
        expected_manifest=manifest,
    )
    drop_sql = render_drop_sql(manifest)
    validate_drop_sql(drop_sql)
    write_bytes(output / "execute.sql", drop_sql.encode("utf-8"))

    free_before = int(probe["host"]["filesystem"]["freeBytes"])
    if fixture_result is not None:
        if fixture_result.get("error"):
            post_probe = fixture_result["probe"]
            write_json(output / "probe-after-failure.json", post_probe)
            outcome = classify_failed_execution(post_probe)
            report = {
                "formatVersion": FORMAT_VERSION,
                "mode": "execute",
                "outcome": outcome,
                "mutationAttempted": True,
                "familyId": FAMILY_ID,
                "catalogBytesBefore": total_bytes,
                "error": fixture_result["error"],
                "completedAtUtc": utc_now(),
            }
            write_json(output / "report.json", report)
            write_checksums(output)
            raise GuardFailure(f"execute failed: {outcome}")
        post_probe = fixture_result["probe"]
    else:
        try:
            run_psql(drop_sql)
        except Exception as error:
            try:
                post_probe = collect_live_probe(args.compose_dir)
            except Exception as probe_error:
                report = {
                    "formatVersion": FORMAT_VERSION,
                    "mode": "execute",
                    "outcome": (
                        "failed_reconciliation_unavailable"
                    ),
                    "mutationAttempted": True,
                    "familyId": FAMILY_ID,
                    "catalogBytesBefore": total_bytes,
                    "error": (
                        command_error_text(error)
                        + "\nreconciliation probe: "
                        + command_error_text(probe_error)
                    )[:4000],
                    "completedAtUtc": utc_now(),
                }
                write_json(output / "report.json", report)
                write_checksums(output)
                raise GuardFailure(
                    "execute failed and catalog reconciliation "
                    "is unavailable"
                ) from probe_error
            write_json(output / "probe-after-failure.json", post_probe)
            outcome = classify_failed_execution(post_probe)
            report = {
                "formatVersion": FORMAT_VERSION,
                "mode": "execute",
                "outcome": outcome,
                "mutationAttempted": True,
                "familyId": FAMILY_ID,
                "catalogBytesBefore": total_bytes,
                "error": command_error_text(error),
                "completedAtUtc": utc_now(),
            }
            write_json(output / "report.json", report)
            write_checksums(output)
            raise GuardFailure(f"execute failed: {outcome}") from error
        try:
            post_probe = collect_live_probe(args.compose_dir)
        except Exception as error:
            report = {
                "formatVersion": FORMAT_VERSION,
                "mode": "execute",
                "outcome": "committed_validation_unavailable",
                "mutationAttempted": True,
                "familyId": FAMILY_ID,
                "catalogBytesBefore": total_bytes,
                "error": command_error_text(error),
                "completedAtUtc": utc_now(),
            }
            write_json(output / "report.json", report)
            write_checksums(output)
            raise GuardFailure(
                "drop committed but post-execute validation is unavailable"
            ) from error

    try:
        validate_runtime_guards(post_probe)
        validate_manifest_identity(post_probe, manifest)
        if family_state(post_probe) != "absent":
            raise GuardFailure(
                "post-drop validation did not find the family absent"
            )
    except Exception as error:
        write_json(output / "probe-after-failure.json", post_probe)
        outcome = classify_failed_execution(post_probe)
        report = {
            "formatVersion": FORMAT_VERSION,
            "mode": "execute",
            "outcome": outcome,
            "mutationAttempted": True,
            "familyId": FAMILY_ID,
            "catalogBytesBefore": total_bytes,
            "error": command_error_text(error),
            "completedAtUtc": utc_now(),
        }
        write_json(output / "report.json", report)
        write_checksums(output)
        raise GuardFailure(
            f"post-execute validation failed: {outcome}"
        ) from error
    write_json(output / "probe-after.json", post_probe)
    free_after = int(post_probe["host"]["filesystem"]["freeBytes"])
    report = {
        "formatVersion": FORMAT_VERSION,
        "mode": "execute",
        "outcome": "executed",
        "mutationAttempted": True,
        "familyId": FAMILY_ID,
        "catalogBytesBefore": total_bytes,
        "catalogBytesAfter": 0,
        "catalogBytesRemoved": total_bytes,
        "filesystemFreeBefore": free_before,
        "filesystemFreeAfter": free_after,
        "filesystemFreeDelta": free_after - free_before,
        "completedAtUtc": utc_now(),
    }
    write_json(output / "report.json", report)
    write_checksums(output)
    return report


def parse_args(argv):
    parser = argparse.ArgumentParser(
        description=(
            "Prepare or execute the exact guarded retirement of "
            "public.ix_le_song_rank and its nine attached leaves."
        )
    )
    mode = parser.add_mutually_exclusive_group()
    mode.add_argument(
        "--check",
        action="store_true",
        help="read-only validation and evidence package (default)",
    )
    mode.add_argument(
        "--execute",
        action="store_true",
        help="execute exact reviewed parent-index retirement",
    )
    parser.add_argument("--output", required=True)
    parser.add_argument(
        "--compose-dir",
        default=str(PRODUCTION_COMPOSE_DIR),
    )
    parser.add_argument("--manifest")
    parser.add_argument("--zero-use-observation")
    parser.add_argument("--rollback-file")
    parser.add_argument("--expected-manifest-sha256")
    parser.add_argument("--expected-zero-use-sha256")
    parser.add_argument("--expected-rollback-sha256")
    parser.add_argument(
        "--fixture",
        help=argparse.SUPPRESS,
    )
    parser.add_argument(
        "--fixture-execute-result",
        help=argparse.SUPPRESS,
    )
    args = parser.parse_args(argv)
    args.mode = "execute" if args.execute else "check"
    if args.mode == "execute":
        required = (
            "manifest",
            "zero_use_observation",
            "rollback_file",
            "expected_manifest_sha256",
            "expected_zero_use_sha256",
            "expected_rollback_sha256",
        )
        missing = [field for field in required if not getattr(args, field)]
        if missing:
            parser.error(
                "--execute requires --manifest, --zero-use-observation, "
                "--rollback-file, and all three expected SHA-256 values"
            )
    if args.fixture_execute_result and not args.fixture:
        parser.error("--fixture-execute-result requires --fixture")
    if (
        args.mode == "execute"
        and args.fixture
        and not args.fixture_execute_result
    ):
        parser.error(
            "fixture execute requires --fixture-execute-result and "
            "can never contact PostgreSQL"
        )
    return args


def main(argv=None):
    args = parse_args(argv or sys.argv[1:])
    fixture_mode = args.fixture is not None
    output = None
    try:
        if pathlib.Path(args.compose_dir).resolve() != PRODUCTION_COMPOSE_DIR:
            raise GuardFailure(
                "compose directory must be the production-owned project"
            )
        output = ensure_output_directory(
            args.output,
            fixture_mode=fixture_mode,
        )
        lock_context = contextlib.nullcontext()
        worker_lock_state = "fixture"
        if not fixture_mode and args.mode == "execute":
            lock_context = acquire_worker_guard_lock()
            worker_lock_state = "owned_by_retirement"
        elif not fixture_mode:
            worker_lock_state = probe_worker_guard_lock()
        with lock_context:
            probe = (
                load_fixture(args.fixture)
                if fixture_mode
                else collect_live_probe(args.compose_dir)
            )
            probe["host"]["workerGuardLockState"] = (
                worker_lock_state
            )
            fixture_result = None
            if args.fixture_execute_result:
                fixture_result = load_fixture(
                    args.fixture_execute_result
                )
            if args.mode == "check":
                report = check_mode(args, output, probe)
            else:
                report = execute_mode(
                    args,
                    output,
                    probe,
                    fixture_result=fixture_result,
                )
        print(json.dumps(report, indent=2, sort_keys=True))
        return 0
    except Exception as error:
        if output is not None and not (output / "report.json").exists():
            report = {
                "formatVersion": FORMAT_VERSION,
                "mode": args.mode,
                "outcome": "rejected",
                "mutationAttempted": False,
                "familyId": FAMILY_ID,
                "error": command_error_text(error),
                "completedAtUtc": utc_now(),
            }
            write_json(output / "report.json", report)
            write_checksums(output)
        print(f"ERROR: {error}", file=sys.stderr)
        return 2


if __name__ == "__main__":
    raise SystemExit(main())
