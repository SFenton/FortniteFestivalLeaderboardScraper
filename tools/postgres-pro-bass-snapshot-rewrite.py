#!/usr/bin/env python3

"""Guarded archive/rewrite pilot for the pro-bass snapshot partition.

The tool intentionally has no relation-name or arbitrary-SQL input. Production
execution is split into independently guarded, resumable stages so the original
partition and archive remain available until validation has succeeded.
"""

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
import threading
import time
import urllib.request
from datetime import datetime, timedelta, timezone


FORMAT_VERSION = 1
TOOL_ID = "fst.pro-bass-snapshot-rewrite-pilot.v1"
TARGET_SCHEMA = "public"
TARGET_PARENT = "leaderboard_entries_snapshot"
TARGET_PARTITION = "leaderboard_entries_snapshot_pro_bass"
TARGET_INSTRUMENT = "Solo_PeripheralBass"
PRODUCTION_COMPOSE_DIR = pathlib.Path(
    "/home/sfenton/Docker/FestivalServiceTracker"
)
PRODUCTION_PROJECT = "festivalservicetracker"
PRODUCTION_STORAGE_ROOT = pathlib.Path("/mnt/docker-storage")
PRODUCTION_DOCKER_ROOT = pathlib.Path("/var/lib/docker")
PRODUCTION_SCRATCH_DEVICE = "/dev/nvme2n1p2"
POSTGRES_CONTAINER = "fst-postgres"
WORKER_CONTAINER = "fstworker"
DATABASE_NAME = "fstservice"
DATABASE_USER = "fst"
APPLICATION_NAME = "fst-pro-bass-snapshot-rewrite-pilot"
PLAN_QUERY_PGOPTIONS = (
    "-c work_mem=64MB "
    "-c temp_file_limit=262144kB "
    "-c max_parallel_workers_per_gather=0"
)
LOOSE_ID_PGOPTIONS = (
    PLAN_QUERY_PGOPTIONS
    + " -c enable_seqscan=off"
)
WORKSPACE_MARKER = ".fst-pro-bass-pilot-workspace.json"
LOCK_FILE = ".fst-pro-bass-pilot.lock"
REPORTS_DIR = "reports"
ARCHIVE_DIR = "archive"
RESTORE_DIR = "restore-drill"
TABLESPACE_DIR = "postgres-tablespace"
TABLESPACE_CONTAINER_PATH = "/fst-pro-bass-scratch"
EMERGENCY_FLOOR_BYTES = 60_392_999_803
PUBLICATION_ADVISORY_LOCK_KEY = 5_067_481_511_116_519_500
MAINTENANCE_ADVISORY_LOCK_KEY = 5_067_481_511_116_519_501
DEFAULT_EXPIRY_DAYS = 45
LOCAL_FILESYSTEMS = {
    "btrfs",
    "ext2",
    "ext3",
    "ext4",
    "xfs",
    "zfs",
}
REMOTE_FILESYSTEM_PATTERN = re.compile(
    r"(nfs|cifs|smb|fuse|sshfs|9p|ceph|gluster)",
    re.IGNORECASE,
)
STAGES = (
    "check",
    "plan",
    "archive",
    "drill",
    "build",
    "swap",
    "validate",
    "repatriate",
    "drop",
    "rollback",
)
MUTATING_DATABASE_STAGES = {
    "build",
    "swap",
    "drop",
    "repatriate",
    "rollback",
}
DEPENDENCIES = {
    "plan": ("check",),
    "archive": ("plan",),
    "drill": ("archive",),
    "build": ("plan", "drill"),
    "swap": ("build", "archive", "drill"),
    "validate": ("swap", "archive", "drill"),
    "repatriate": ("validate", "archive", "drill"),
    "drop": ("repatriate", "archive", "drill"),
    "rollback": ("swap", "archive"),
}
SNAPSHOT_COLUMNS = (
    "snapshot_id",
    "song_id",
    "instrument",
    "account_id",
    "score",
    "accuracy",
    "is_full_combo",
    "stars",
    "season",
    "percentile",
    "rank",
    "source",
    "difficulty",
    "api_rank",
    "end_time",
    "band_members_json",
    "band_score",
    "base_score",
    "instrument_bonus",
    "overdrive_bonus",
    "instrument_combo",
    "first_seen_at",
    "last_updated_at",
)


class PilotError(RuntimeError):
    """A fail-closed guard or stage error."""


class CommandError(PilotError):
    def __init__(self, arguments, returncode, stdout="", stderr=""):
        command = " ".join(str(value) for value in arguments)
        super().__init__(
            f"command failed with exit {returncode}: {command}\n"
            f"{stderr.strip()}"
        )
        self.arguments = tuple(arguments)
        self.returncode = returncode
        self.stdout = stdout
        self.stderr = stderr


def utc_now():
    return datetime.now(timezone.utc).isoformat().replace("+00:00", "Z")


def canonical_json_bytes(value):
    return (
        json.dumps(value, indent=2, sort_keys=True, ensure_ascii=True)
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


def write_bytes_exclusive(path, value, mode=0o600):
    path = pathlib.Path(path)
    path.parent.mkdir(parents=True, exist_ok=True, mode=0o700)
    temporary = path.with_name(
        f".{path.name}.tmp-{os.getpid()}-{time.time_ns()}"
    )
    try:
        descriptor = os.open(
            temporary,
            os.O_WRONLY | os.O_CREAT | os.O_EXCL,
            mode,
        )
        with os.fdopen(descriptor, "wb") as handle:
            handle.write(value)
            handle.flush()
            os.fsync(handle.fileno())
        os.link(temporary, path)
        os.unlink(temporary)
        directory = os.open(
            path.parent,
            os.O_RDONLY | getattr(os, "O_DIRECTORY", 0),
        )
        try:
            os.fsync(directory)
        finally:
            os.close(directory)
    finally:
        with contextlib.suppress(FileNotFoundError):
            temporary.unlink()


def write_json_exclusive(path, value):
    write_bytes_exclusive(path, canonical_json_bytes(value))


def write_or_verify_bytes(path, value, mode=0o600):
    path = pathlib.Path(path)
    if path.exists():
        existing = path.read_bytes()
        if existing != value:
            raise PilotError(
                f"immutable artifact differs from expected bytes: {path}"
            )
        return False
    write_bytes_exclusive(path, value, mode)
    return True


def write_or_verify_json(path, value):
    return write_or_verify_bytes(path, canonical_json_bytes(value))


def read_json(path, maximum_bytes=16 * 1024 * 1024):
    path = pathlib.Path(path)
    metadata = path.lstat()
    if path.is_symlink() or not stat.S_ISREG(metadata.st_mode):
        raise PilotError(f"JSON input is not a regular file: {path}")
    if metadata.st_size > maximum_bytes:
        raise PilotError(f"JSON input is too large: {path}")
    try:
        try:
            return json.loads(path.read_text(encoding="utf-8"))
        except (UnicodeDecodeError, json.JSONDecodeError) as error:
            raise PilotError(f"JSON input is malformed: {path}") from error
    except (UnicodeDecodeError, json.JSONDecodeError) as error:
        raise PilotError(f"invalid JSON input {path}: {error}") from error


def ensure_integer(value, label, minimum=0):
    if isinstance(value, bool) or not isinstance(value, int):
        raise PilotError(f"{label} must be an integer")
    if value < minimum:
        raise PilotError(f"{label} must be at least {minimum}")
    return value


def sql_literal(value):
    return "'" + str(value).replace("'", "''") + "'"


def qualified(name):
    if not re.fullmatch(r"[a-z][a-z0-9_]*", name):
        raise PilotError(f"unsafe SQL identifier: {name}")
    return f'"{TARGET_SCHEMA}"."{name}"'


def relation_token(run_id):
    digest = hashlib.sha256(run_id.encode("utf-8")).hexdigest()[:12]
    return digest


def replacement_name(run_id):
    return f"{TARGET_PARTITION}_rewrite_{relation_token(run_id)}"


def retired_name(run_id):
    return f"{TARGET_PARTITION}_retired_{relation_token(run_id)}"


def failed_name(run_id):
    return f"{TARGET_PARTITION}_failed_{relation_token(run_id)}"


def scratch_retired_name(run_id):
    return f"pb_scratch_retired_{relation_token(run_id)}"


def home_name(run_id):
    return f"pb_home_{relation_token(run_id)}"


def replacement_primary_name(run_id):
    return f"pb_rewrite_{relation_token(run_id)}_pkey"


def replacement_score_name(run_id):
    return f"pb_rewrite_{relation_token(run_id)}_score_idx"


def replacement_instrument_check_name(run_id):
    return f"pb_rewrite_{relation_token(run_id)}_instrument_ck"


def home_primary_name(run_id):
    return f"pb_home_{relation_token(run_id)}_pkey"


def home_score_name(run_id):
    return f"pb_home_{relation_token(run_id)}_score_idx"


def tablespace_name(run_id):
    return f"fst_pb_{relation_token(run_id)}"


def snapshot_row_expression(alias="row"):
    values = []
    for column in SNAPSHOT_COLUMNS:
        reference = f'{alias}."{column}"'
        if column == "band_members_json":
            reference = f"COALESCE({reference}::text, '<null>')"
        else:
            reference = f"COALESCE({reference}::text, '<null>')"
        values.append(reference)
    return "concat_ws(E'\\x1f', " + ", ".join(values) + ")"


def fingerprint_sql(relation_name, predicate="TRUE"):
    expression = snapshot_row_expression()
    return f"""
        SELECT json_build_object(
            'rowCount', COUNT(*)::bigint,
            'snapshotIdMin', MIN(snapshot_id),
            'snapshotIdMax', MAX(snapshot_id),
            'songIdMin', MIN(song_id),
            'songIdMax', MAX(song_id),
            'accountIdMinHash', md5(MIN(account_id)),
            'accountIdMaxHash', md5(MAX(account_id)),
            'scoreMin', MIN(score),
            'scoreMax', MAX(score),
            'hashXor0', COALESCE(
                bit_xor(hashtextextended({expression}, 0)), 0),
            'hashXor1', COALESCE(
                bit_xor(hashtextextended({expression}, 1)), 0),
            'hashSum2', COALESCE(
                SUM(hashtextextended({expression}, 2)::numeric), 0)::text
        )
        FROM {qualified(relation_name)} row
        WHERE {predicate}
    """


def snapshot_id_predicate(snapshot_ids):
    values = [
        ensure_integer(value, "snapshot ID", 1)
        for value in snapshot_ids
    ]
    if not values:
        raise PilotError("snapshot ID predicate cannot be empty")
    return (
        "snapshot_id = ANY(ARRAY["
        + ", ".join(str(value) for value in values)
        + "]::bigint[])"
    )


def relation_state_query(relation_name):
    relation = f"{TARGET_SCHEMA}.{relation_name}"
    return f"""
        SELECT json_build_object(
            'oid', relation.oid,
            'relfilenode', relation.relfilenode,
            'heapBytes', pg_relation_size(relation.oid),
            'indexBytes', pg_indexes_size(relation.oid),
            'totalBytes', pg_total_relation_size(relation.oid),
            'estimatedRows', relation.reltuples::bigint,
            'inserts', COALESCE(stats.n_tup_ins, 0),
            'updates', COALESCE(stats.n_tup_upd, 0),
            'deletes', COALESCE(stats.n_tup_del, 0),
            'attached', EXISTS (
                SELECT 1
                FROM pg_inherits
                WHERE inhrelid = relation.oid),
            'partitionBound', pg_get_expr(
                relation.relpartbound, relation.oid, true),
            'tablespace', COALESCE(
                tablespace.spcname, 'pg_default'))
        FROM pg_class relation
        JOIN pg_namespace namespace
          ON namespace.oid = relation.relnamespace
        LEFT JOIN pg_stat_all_tables stats
          ON stats.relid = relation.oid
        LEFT JOIN pg_tablespace tablespace
          ON tablespace.oid = relation.reltablespace
        WHERE namespace.nspname = {sql_literal(TARGET_SCHEMA)}
          AND relation.relname = {sql_literal(relation_name)}
    """


def physical_relation_identity(state):
    return {
        key: state[key]
        for key in (
            "oid",
            "relfilenode",
            "heapBytes",
            "indexBytes",
            "totalBytes",
            "inserts",
            "updates",
            "deletes",
        )
    }


def catalog_shape(catalog):
    return {
        key: catalog[key]
        for key in (
            "parentDefinition",
            "partitionBound",
            "owner",
            "tablespace",
            "columns",
            "constraints",
            "indexes",
        )
    }


def catalog_semantic_shape(
    catalog,
    *,
    ignored_constraint_names=(),
):
    indexes = []
    for item in catalog["indexes"]:
        definition = re.sub(
            r"^CREATE (UNIQUE )?INDEX \S+ ON \S+ ",
            lambda match: (
                "CREATE UNIQUE INDEX <name> ON <table> "
                if match.group(1)
                else "CREATE INDEX <name> ON <table> "
            ),
            item["definition"],
        )
        indexes.append(
            {
                "definition": definition,
                "isPrimary": item["isPrimary"],
                "isUnique": item["isUnique"],
                "isValid": item["isValid"],
            }
        )
    return {
        "parentDefinition": catalog["parentDefinition"],
        "partitionBound": catalog["partitionBound"],
        "owner": catalog["owner"],
        "tablespace": catalog["tablespace"],
        "columns": catalog["columns"],
        "constraints": sorted(
            (
                {
                    "type": item["type"],
                    "definition": item["definition"],
                }
                for item in catalog["constraints"]
                if item["name"] not in ignored_constraint_names
            ),
            key=lambda item: (
                item["type"],
                item["definition"],
            ),
        ),
        "indexes": sorted(
            indexes,
            key=lambda item: (
                not item["isPrimary"],
                item["definition"],
            ),
        ),
    }


def source_fence_matches(expected, observed):
    keys = (
        "partitionOid",
        "relfilenode",
        "heapBytes",
        "indexBytes",
        "totalBytes",
        "inserts",
        "updates",
        "deletes",
    )
    return all(
        int(expected[key]) == int(observed[key])
        for key in keys
    )


def source_fence_from_relation_state(state):
    return {
        "partitionOid": state["oid"],
        "relfilenode": state["relfilenode"],
        "heapBytes": state["heapBytes"],
        "indexBytes": state["indexBytes"],
        "totalBytes": state["totalBytes"],
        "inserts": state["inserts"],
        "updates": state["updates"],
        "deletes": state["deletes"],
    }


def source_catalog_names(catalog):
    primary_constraints = [
        item
        for item in catalog["constraints"]
        if item["type"] == "p"
    ]
    primary_indexes = [
        item
        for item in catalog["indexes"]
        if item["isPrimary"]
    ]
    score_indexes = [
        item
        for item in catalog["indexes"]
        if (
            not item["isPrimary"]
            and "(snapshot_id, song_id, instrument, score DESC)"
            in item["definition"]
        )
    ]
    if (
        len(primary_constraints) != 1
        or len(primary_indexes) != 1
        or len(score_indexes) != 1
        or len(catalog["indexes"]) != 2
    ):
        raise PilotError(
            "source catalog is not the exact primary/score-index shape"
        )
    names = {
        "primaryConstraint": primary_constraints[0]["name"],
        "primaryIndex": primary_indexes[0]["name"],
        "scoreIndex": score_indexes[0]["name"],
    }
    for name in names.values():
        if not re.fullmatch(r"[a-z][a-z0-9_]*", name):
            raise PilotError(f"unsafe source catalog name: {name}")
    if names["primaryConstraint"] != names["primaryIndex"]:
        raise PilotError(
            "source primary constraint/index names do not match"
        )
    return names


class Runner:
    def run(
        self,
        arguments,
        *,
        input_text=None,
        timeout=600,
        env=None,
        check=True,
    ):
        merged_env = os.environ.copy()
        if env:
            merged_env.update(env)
        completed = subprocess.run(
            [str(value) for value in arguments],
            input=input_text,
            text=True,
            capture_output=True,
            timeout=timeout,
            env=merged_env,
        )
        if check and completed.returncode != 0:
            raise CommandError(
                arguments,
                completed.returncode,
                completed.stdout,
                completed.stderr,
            )
        return completed

    def run_to_file(
        self,
        arguments,
        output_path,
        *,
        timeout=86_400,
        env=None,
    ):
        merged_env = os.environ.copy()
        if env:
            merged_env.update(env)
        output_path = pathlib.Path(output_path)
        partial = output_path.with_name(output_path.name + ".partial")
        if output_path.exists() or partial.exists():
            raise PilotError(
                f"archive output already exists: {output_path}"
            )
        with partial.open("xb") as output:
            completed = subprocess.run(
                [str(value) for value in arguments],
                stdout=output,
                stderr=subprocess.PIPE,
                timeout=timeout,
                env=merged_env,
            )
            output.flush()
            os.fsync(output.fileno())
        if completed.returncode != 0:
            partial.unlink(missing_ok=True)
            raise CommandError(
                arguments,
                completed.returncode,
                stderr=completed.stderr.decode(
                    "utf-8", errors="replace"
                ),
            )
        partial.chmod(0o600)
        partial.replace(output_path)
        return completed


class Database:
    def __init__(self, runner, container, user, database):
        self.runner = runner
        self.container = container
        self.user = user
        self.database = database

    def psql(self, sql, *, timeout=3600, pgoptions=None):
        options = (
            "PGOPTIONS=-c row_security=off "
            f"-c application_name={APPLICATION_NAME}"
        )
        if pgoptions:
            options += f" {pgoptions}"
        arguments = [
            "docker",
            "exec",
            "-e",
            "PGCONNECT_TIMEOUT=10",
            "-e",
            options,
            self.container,
            "psql",
            "-X",
            "-v",
            "ON_ERROR_STOP=1",
            "-h",
            "/var/run/postgresql",
            "-U",
            self.user,
            "-d",
            self.database,
            "-At",
            "-c",
            sql,
        ]
        return self.runner.run(
            arguments,
            timeout=timeout,
        ).stdout.strip()

    def json(self, sql, *, timeout=3600, pgoptions=None):
        output = self.psql(
            sql,
            timeout=timeout,
            pgoptions=pgoptions,
        )
        if not output:
            raise PilotError("database query returned no JSON")
        try:
            return json.loads(output)
        except json.JSONDecodeError as error:
            raise PilotError(
                f"database query returned invalid JSON: {output[:500]}"
            ) from error

    def scalar(self, sql, *, timeout=3600, pgoptions=None):
        return self.psql(
            sql,
            timeout=timeout,
            pgoptions=pgoptions,
        )


class FilesystemMonitor:
    def __init__(
        self,
        path,
        interval_seconds=0.25,
        minimum_allowed_bytes=None,
        on_breach=None,
    ):
        self.path = pathlib.Path(path)
        self.interval_seconds = interval_seconds
        self.minimum_allowed_bytes = minimum_allowed_bytes
        self.on_breach = on_breach
        self.minimum_free_bytes = shutil.disk_usage(self.path).free
        self.samples = 1
        self.breached = False
        self.breach_handled = False
        self.breach_error = None
        self._stopped = threading.Event()
        self._thread = None

    def _observe(self):
        free = shutil.disk_usage(self.path).free
        self.minimum_free_bytes = min(
            self.minimum_free_bytes,
            free,
        )
        self.samples += 1
        if (
            self.minimum_allowed_bytes is not None
            and free < self.minimum_allowed_bytes
        ):
            self.breached = True
            if (
                self.on_breach is not None
                and not self.breach_handled
            ):
                try:
                    self.breach_handled = bool(
                        self.on_breach(free)
                    )
                    if self.breach_handled:
                        self.breach_error = None
                except Exception as error:
                    self.breach_error = str(error)

    def _run(self):
        while not self._stopped.wait(self.interval_seconds):
            self._observe()

    def __enter__(self):
        self._thread = threading.Thread(
            target=self._run,
            name="fst-pro-bass-filesystem-monitor",
            daemon=True,
        )
        self._thread.start()
        return self

    def __exit__(self, *_):
        self._stopped.set()
        self._thread.join(timeout=5)
        self._observe()


def find_mount(runner, path):
    completed = runner.run(
        [
            "findmnt",
            "-T",
            str(path),
            "-n",
            "-o",
            "SOURCE,FSTYPE,MAJ:MIN,TARGET",
        ],
        timeout=30,
    )
    fields = completed.stdout.strip().split()
    if len(fields) != 4:
        raise PilotError(
            f"could not identify filesystem for scratch root {path}"
        )
    return {
        "source": fields[0],
        "filesystemType": fields[1],
        "deviceId": fields[2],
        "mountTarget": fields[3],
    }


def path_is_beneath(path, parent):
    try:
        pathlib.Path(path).relative_to(pathlib.Path(parent))
        return True
    except ValueError:
        return False


def validate_no_symlink_components(path):
    path = pathlib.Path(path)
    current = pathlib.Path(path.anchor)
    for part in path.parts[1:]:
        current /= part
        metadata = current.lstat()
        if stat.S_ISLNK(metadata.st_mode):
            raise PilotError(
                f"scratch path contains a symbolic link: {current}"
            )


def validate_scratch_root(
    runner,
    scratch_root,
    expected_device_id,
    *,
    test_mode=False,
    allow_unclaimed=False,
):
    requested = pathlib.Path(scratch_root)
    if not requested.is_absolute():
        raise PilotError("--scratch-root must be an absolute path")
    if not requested.exists() or not requested.is_dir():
        raise PilotError(
            "scratch root must be an existing operator-created directory"
        )
    validate_no_symlink_components(requested)
    resolved = requested.resolve(strict=True)
    denied_exact = {
        pathlib.Path("/"),
        pathlib.Path("/tmp"),
        pathlib.Path("/var/tmp"),
        PRODUCTION_STORAGE_ROOT,
        PRODUCTION_DOCKER_ROOT,
    }
    if resolved in denied_exact:
        raise PilotError(f"scratch root is forbidden: {resolved}")
    if (
        path_is_beneath(resolved, PRODUCTION_STORAGE_ROOT)
        or path_is_beneath(resolved, PRODUCTION_DOCKER_ROOT)
        or path_is_beneath(resolved, "/tmp")
        or path_is_beneath(resolved, "/var/tmp")
    ):
        raise PilotError(
            f"scratch root is beneath a forbidden storage root: {resolved}"
        )
    mount = find_mount(runner, resolved)
    if REMOTE_FILESYSTEM_PATTERN.search(mount["filesystemType"]):
        raise PilotError(
            "scratch root must use a local filesystem, not "
            f"{mount['filesystemType']}"
        )
    if mount["filesystemType"].lower() not in LOCAL_FILESYSTEMS:
        raise PilotError(
            "scratch filesystem is not in the local allowlist: "
            f"{mount['filesystemType']}"
        )
    if mount["deviceId"] != expected_device_id:
        raise PilotError(
            "scratch device identity mismatch: expected "
            f"{expected_device_id}, observed {mount['deviceId']}"
        )
    if not test_mode:
        if mount["source"] != PRODUCTION_SCRATCH_DEVICE:
            raise PilotError(
                "production scratch must resolve to "
                f"{PRODUCTION_SCRATCH_DEVICE}, observed "
                f"{mount['source']}"
            )
        if mount["mountTarget"] != "/":
            raise PilotError(
                "production scratch device must be mounted at /"
            )
    usage = shutil.disk_usage(resolved)
    marker_path = resolved / WORKSPACE_MARKER
    entries = {
        entry.name
        for entry in resolved.iterdir()
        if entry.name != LOCK_FILE
    }
    if marker_path.exists():
        marker = read_json(marker_path)
        if marker.get("toolId") != TOOL_ID:
            raise PilotError(
                "scratch workspace marker belongs to another tool"
            )
        allowed = {
            WORKSPACE_MARKER,
            REPORTS_DIR,
            ARCHIVE_DIR,
            RESTORE_DIR,
            TABLESPACE_DIR,
        }
        foreign = sorted(entries - allowed)
        if foreign:
            raise PilotError(
                "scratch workspace contains foreign entries: "
                + ", ".join(foreign[:10])
            )
        for name in (
            REPORTS_DIR,
            ARCHIVE_DIR,
            RESTORE_DIR,
            TABLESPACE_DIR,
        ):
            child = resolved / name
            metadata = child.lstat()
            if child.is_symlink() or not stat.S_ISDIR(
                metadata.st_mode
            ):
                raise PilotError(
                    "workspace-owned path is not a real directory: "
                    f"{child}"
                )
    elif entries and allow_unclaimed and entries == {TABLESPACE_DIR}:
        tablespace_path = resolved / TABLESPACE_DIR
        metadata = tablespace_path.lstat()
        if (
            tablespace_path.is_symlink()
            or not stat.S_ISDIR(metadata.st_mode)
            or any(tablespace_path.iterdir())
        ):
            raise PilotError(
                "pre-created tablespace mount directory must be "
                "real and empty"
            )
    elif entries and not allow_unclaimed:
        raise PilotError(
            "unclaimed scratch workspace is not empty: "
            + ", ".join(sorted(entries)[:10])
        )
    elif entries:
        raise PilotError(
            "new scratch workspace must be empty before it is claimed"
        )
    return {
        "requestedPath": str(requested),
        "resolvedPath": str(resolved),
        "device": mount,
        "totalBytes": usage.total,
        "usedBytes": usage.used,
        "freeBytes": usage.free,
    }


def claim_workspace(
    scratch_root,
    scratch_info,
    run_id,
    expires_at,
    repository_commit,
    tool_source_sha256,
    test_mode,
):
    root = pathlib.Path(scratch_root).resolve(strict=True)
    marker_path = root / WORKSPACE_MARKER
    if marker_path.exists():
        marker = read_json(marker_path)
        if marker.get("runId") != run_id:
            raise PilotError(
                "workspace is already claimed by run "
                f"{marker.get('runId')}"
            )
        return marker
    marker = {
        "formatVersion": FORMAT_VERSION,
        "toolId": TOOL_ID,
        "runId": run_id,
        "target": f"{TARGET_SCHEMA}.{TARGET_PARTITION}",
        "instrument": TARGET_INSTRUMENT,
        "temporaryOnly": True,
        "acceptedDataMayRemainHere": False,
        "archiveDeletionRequiresSeparateOperatorDecision": True,
        "createdAtUtc": utc_now(),
        "expiresAtUtc": expires_at,
        "repositoryCommit": repository_commit,
        "toolSourceSha256": tool_source_sha256,
        "testMode": test_mode,
        "scratch": scratch_info,
    }
    write_json_exclusive(marker_path, marker)
    for name in (
        REPORTS_DIR,
        ARCHIVE_DIR,
        RESTORE_DIR,
        TABLESPACE_DIR,
    ):
        (root / name).mkdir(mode=0o700, exist_ok=True)
    return marker


def validate_workspace_marker(
    marker,
    run_id,
    repository_commit,
    tool_source_sha256,
    *,
    now=None,
):
    if marker.get("runId") != run_id:
        raise PilotError("workspace run ID does not match --run-id")
    if marker.get("repositoryCommit") != repository_commit:
        raise PilotError(
            "workspace repository commit differs from the active "
            "checkout"
        )
    if marker.get("toolSourceSha256") != tool_source_sha256:
        raise PilotError(
            "workspace tool source hash differs from the active checkout"
        )
    expires_at_value = marker.get("expiresAtUtc")
    try:
        expires_at = datetime.fromisoformat(
            str(expires_at_value).replace("Z", "+00:00")
        )
    except ValueError as error:
        raise PilotError(
            "workspace expiry is not a valid ISO-8601 timestamp"
        ) from error
    if expires_at.tzinfo is None:
        raise PilotError(
            "workspace expiry must include a timezone"
        )
    current = now or datetime.now(timezone.utc)
    if expires_at <= current:
        raise PilotError(
            "scratch workspace ownership has expired"
        )


@contextlib.contextmanager
def workspace_lock(root):
    root = pathlib.Path(root)
    lock_path = root / LOCK_FILE
    descriptor = os.open(
        lock_path,
        os.O_CREAT
        | os.O_RDWR
        | os.O_CLOEXEC
        | getattr(os, "O_NOFOLLOW", 0),
        0o600,
    )
    try:
        fcntl.flock(descriptor, fcntl.LOCK_EX | fcntl.LOCK_NB)
    except BlockingIOError as error:
        os.close(descriptor)
        raise PilotError(
            f"another pilot process owns {lock_path}"
        ) from error
    try:
        yield
    finally:
        fcntl.flock(descriptor, fcntl.LOCK_UN)
        os.close(descriptor)


def report_path(root, stage):
    return pathlib.Path(root) / REPORTS_DIR / f"{stage}.json"


def load_report(root, stage):
    path = report_path(root, stage)
    if not path.is_file():
        raise PilotError(
            f"required {stage} report does not exist: {path}"
        )
    report = read_json(path)
    if report.get("stage") != stage or report.get("status") != "succeeded":
        raise PilotError(
            f"required {stage} report is not a successful stage report"
        )
    if report.get("toolId") != TOOL_ID:
        raise PilotError(
            f"required {stage} report belongs to another tool"
        )
    return report


def dependency_hashes(root, stage):
    hashes = {}
    for dependency in DEPENDENCIES.get(stage, ()):
        load_report(root, dependency)
        path = report_path(root, dependency)
        hashes[dependency] = {
            "path": str(path),
            "sha256": sha256_path(path),
        }
    return hashes


def write_stage_report(root, stage, body):
    path = report_path(root, stage)
    if path.exists():
        existing = load_report(root, stage)
        return existing
    report = {
        "formatVersion": FORMAT_VERSION,
        "toolId": TOOL_ID,
        "stage": stage,
        "status": "succeeded",
        "completedAtUtc": utc_now(),
        "dependencies": dependency_hashes(root, stage),
        **body,
    }
    write_json_exclusive(path, report)
    return report


def write_failure_report(root, stage, error):
    timestamp = datetime.now(timezone.utc).strftime("%Y%m%dT%H%M%SZ")
    path = (
        pathlib.Path(root)
        / REPORTS_DIR
        / f"{stage}.failed-{timestamp}.json"
    )
    suffix = 1
    while path.exists():
        path = path.with_name(
            f"{stage}.failed-{timestamp}-{suffix}.json"
        )
        suffix += 1
    value = {
        "formatVersion": FORMAT_VERSION,
        "toolId": TOOL_ID,
        "stage": stage,
        "status": "failed",
        "failedAtUtc": utc_now(),
        "errorType": type(error).__name__,
        "error": str(error),
    }
    write_json_exclusive(path, value)
    return path


def inspect_container(runner, name):
    output = runner.run(
        ["docker", "inspect", name],
        timeout=30,
    ).stdout
    rows = json.loads(output)
    if len(rows) != 1:
        raise PilotError(f"unexpected docker inspect result for {name}")
    return rows[0]


def collect_tablespace_mount(args, runner):
    postgres = inspect_container(runner, args.pg_container)
    expected_source = (
        pathlib.Path(args.scratch_root) / TABLESPACE_DIR
    ).resolve(strict=True)
    matches = [
        mount
        for mount in postgres.get("Mounts") or []
        if mount.get("Destination") == TABLESPACE_CONTAINER_PATH
    ]
    if args.test_mode and not matches:
        return {
            "enabled": False,
            "tablespace": "pg_default",
            "hostPath": None,
            "containerPath": None,
        }
    if len(matches) != 1:
        raise PilotError(
            "PostgreSQL must have exactly one guarded pro-bass "
            "scratch mount"
        )
    mount = matches[0]
    observed_source = pathlib.Path(
        mount.get("Source", "")
    ).resolve(strict=True)
    if (
        mount.get("Type") != "bind"
        or not mount.get("RW")
        or (
            not args.test_mode
            and observed_source != expected_source
        )
    ):
        raise PilotError(
            "PostgreSQL scratch mount differs from the claimed "
            "workspace tablespace directory"
        )
    return {
        "enabled": True,
        "tablespace": tablespace_name(args.run_id),
        "hostPath": str(observed_source),
        "containerPath": TABLESPACE_CONTAINER_PATH,
        "readWrite": True,
    }


def prepare_scratch_tablespace(args, runner, database):
    mount = collect_tablespace_mount(args, runner)
    if not mount["enabled"]:
        return mount
    name = mount["tablespace"]
    existing = database.json(
        f"""
            SELECT COALESCE((
                SELECT json_build_object(
                    'name', tablespace.spcname,
                    'owner', pg_get_userbyid(tablespace.spcowner),
                    'location', pg_tablespace_location(
                        tablespace.oid))
                FROM pg_tablespace tablespace
                WHERE tablespace.spcname = {sql_literal(name)}
            ), 'null'::json)
        """
    )
    if existing is None:
        postgres_uid = runner.run(
            [
                "docker",
                "exec",
                args.pg_container,
                "id",
                "-u",
                "postgres",
            ],
            timeout=30,
        ).stdout.strip()
        postgres_gid = runner.run(
            [
                "docker",
                "exec",
                args.pg_container,
                "id",
                "-g",
                "postgres",
            ],
            timeout=30,
        ).stdout.strip()
        if (
            not postgres_uid.isdigit()
            or not postgres_gid.isdigit()
        ):
            raise PilotError(
                "could not identify the PostgreSQL container user"
            )
        runner.run(
            [
                "docker",
                "exec",
                "--user",
                "0:0",
                args.pg_container,
                "sh",
                "-c",
                (
                    f"test -d {TABLESPACE_CONTAINER_PATH} && "
                    f"test -z \"$(find {TABLESPACE_CONTAINER_PATH} "
                    "-mindepth 1 -maxdepth 1 -print -quit)\" && "
                    f"chown {postgres_uid}:{postgres_gid} "
                    f"{TABLESPACE_CONTAINER_PATH} && "
                    f"chmod 700 {TABLESPACE_CONTAINER_PATH}"
                ),
            ],
            timeout=120,
        )
        database.psql(
            f"""
                CREATE TABLESPACE "{name}"
                OWNER "{args.pg_user}"
                LOCATION {sql_literal(TABLESPACE_CONTAINER_PATH)}
            """,
            timeout=120,
        )
        existing = {
            "name": name,
            "owner": args.pg_user,
            "location": TABLESPACE_CONTAINER_PATH,
        }
    if existing != {
        "name": name,
        "owner": args.pg_user,
        "location": TABLESPACE_CONTAINER_PATH,
    }:
        raise PilotError(
            "existing scratch tablespace identity is unsafe"
        )
    return {
        **mount,
        "database": existing,
    }


def collect_host_guard(args, runner):
    postgres = inspect_container(runner, args.pg_container)
    state = postgres.get("State") or {}
    if not state.get("Running") or (state.get("Health") or {}).get(
        "Status"
    ) not in (None, "healthy"):
        raise PilotError("PostgreSQL container is not healthy and running")
    labels = (postgres.get("Config") or {}).get("Labels") or {}
    mounts = postgres.get("Mounts") or []
    data_mounts = [
        mount
        for mount in mounts
        if mount.get("Destination") == "/var/lib/postgresql/data"
    ]
    if len(data_mounts) != 1:
        raise PilotError(
            "PostgreSQL must have exactly one data-directory mount"
        )
    data_source = pathlib.Path(data_mounts[0].get("Source", "")).resolve()
    if not args.test_mode:
        if labels.get("com.docker.compose.project") != PRODUCTION_PROJECT:
            raise PilotError("unexpected production Compose project")
        working_dir = pathlib.Path(
            labels.get("com.docker.compose.project.working_dir", "")
        )
        if working_dir.resolve() != PRODUCTION_COMPOSE_DIR:
            raise PilotError("unexpected production Compose working dir")
        if not path_is_beneath(
            data_source,
            PRODUCTION_STORAGE_ROOT,
        ):
            raise PilotError(
                "PostgreSQL data is outside the 4 TB FST storage root"
            )
        worker = inspect_container(runner, args.worker_container)
        worker_state = worker.get("State") or {}
        if worker_state.get("Running"):
            raise PilotError("fstworker must be held offline")
        worker_labels = (worker.get("Config") or {}).get("Labels") or {}
        if (
            worker_labels.get("com.docker.compose.project")
            != PRODUCTION_PROJECT
        ):
            raise PilotError("unexpected worker Compose project")
    else:
        if not args.pg_container.startswith("fst-pro-bass-pilot-test-"):
            raise PilotError(
                "test mode requires an isolated test container name"
            )
    data_usage = shutil.disk_usage(data_source)
    return {
        "postgresContainerId": postgres.get("Id"),
        "postgresImage": (postgres.get("Config") or {}).get("Image"),
        "composeProject": labels.get("com.docker.compose.project"),
        "composeWorkingDir": labels.get(
            "com.docker.compose.project.working_dir"
        ),
        "dataPath": str(data_source),
        "dataFilesystem": {
            "totalBytes": data_usage.total,
            "usedBytes": data_usage.used,
            "freeBytes": data_usage.free,
        },
        "testMode": args.test_mode,
    }


def collect_database_guard(database):
    query = f"""
        SELECT json_build_object(
            'database', current_database(),
            'databaseOid', (SELECT oid FROM pg_database
                            WHERE datname = current_database()),
            'user', current_user,
            'serverVersion', current_setting('server_version'),
            'serverVersionNum',
                current_setting('server_version_num')::integer,
            'systemIdentifier',
                (SELECT system_identifier::text FROM pg_control_system()),
            'postmasterStartedAt', pg_postmaster_start_time(),
            'publication', (
                SELECT json_build_object(
                    'publishedScrapeId', published_scrape_id,
                    'currentPublicationId', current_publication_id,
                    'previousPublicationId', previous_publication_id,
                    'workingPublicationId', working_publication_id,
                    'publicReadsFrozen', public_reads_frozen,
                    'freezeReason', public_reads_frozen_reason,
                    'commitIntentOwner',
                        to_jsonb(scrape_publication_state)
                            ->>'publication_commit_intent_owner',
                    'commitIntentStartedAt',
                        to_jsonb(scrape_publication_state)
                            ->>'publication_commit_intent_started_at',
                    'maxScoreMutationGateToken',
                        to_jsonb(scrape_publication_state)
                            ->>'max_score_mutation_gate_token',
                    'updatedAt', updated_at)
                FROM scrape_publication_state
                WHERE id = TRUE),
            'workerStatus', (
                SELECT COALESCE(
                    json_agg(row_to_json(worker_status)),
                    '[]'::json)
                FROM (
                    SELECT worker_key, status, last_heartbeat_at
                    FROM service_worker_status
                    ORDER BY worker_key
                ) worker_status),
            'runningScrapes', (
                SELECT COUNT(*)
                FROM scrape_log
                WHERE completed_at IS NULL
                  AND COALESCE(
                      to_jsonb(scrape_log)->>'status',
                      'running') = 'running'),
            'activePhaseAttempts', (
                SELECT COUNT(*)
                FROM scrape_phase_attempts
                WHERE status = 'running'),
            'waitingLocks', (
                SELECT COUNT(*) FROM pg_locks WHERE NOT granted),
            'workerBackends', (
                SELECT COUNT(*)
                FROM pg_stat_activity
                WHERE pid <> pg_backend_pid()
                  AND (
                      application_name ILIKE '%fstworker%'
                      OR application_name ILIKE '%scrape%')),
            'maintenanceBackends', (
                SELECT COUNT(*)
                FROM pg_stat_activity
                WHERE pid <> pg_backend_pid()
                  AND (
                      application_name =
                          {sql_literal(APPLICATION_NAME)}
                      OR application_name ILIKE '%maintenance%'
                      OR application_name ILIKE '%repack%'
                      OR application_name ILIKE '%rewrite%')),
            'targetLocks', (
                SELECT COUNT(*)
                FROM pg_locks lock
                WHERE lock.relation IN (
                    to_regclass(
                        'public.{TARGET_PARENT}'),
                    to_regclass(
                        'public.{TARGET_PARTITION}'))
                  AND lock.pid <> pg_backend_pid()),
            'target', (
                SELECT json_build_object(
                    'parentOid',
                        to_regclass(
                            'public.{TARGET_PARENT}')::oid,
                    'partitionOid',
                        to_regclass(
                            'public.{TARGET_PARTITION}')::oid,
                    'relfilenode', (
                        SELECT relfilenode
                        FROM pg_class
                        WHERE oid = to_regclass(
                            'public.{TARGET_PARTITION}')),
                    'attached', EXISTS (
                        SELECT 1
                        FROM pg_inherits
                        WHERE inhparent = to_regclass(
                            'public.{TARGET_PARENT}')
                          AND inhrelid = to_regclass(
                            'public.{TARGET_PARTITION}')),
                    'partitionBound', (
                        SELECT pg_get_expr(
                            relpartbound, oid, true)
                        FROM pg_class
                        WHERE oid = to_regclass(
                            'public.{TARGET_PARTITION}')),
                    'owner', (
                        SELECT pg_get_userbyid(relowner)
                        FROM pg_class
                        WHERE oid = to_regclass(
                            'public.{TARGET_PARTITION}')),
                    'tablespace', (
                        SELECT COALESCE(
                            NULLIF(tablespace.spcname, ''),
                            'pg_default')
                        FROM pg_class relation
                        LEFT JOIN pg_tablespace tablespace
                          ON tablespace.oid =
                                relation.reltablespace
                        WHERE relation.oid = to_regclass(
                            'public.{TARGET_PARTITION}')),
                    'heapBytes', pg_relation_size(
                        'public.{TARGET_PARTITION}'),
                    'indexBytes', pg_indexes_size(
                        'public.{TARGET_PARTITION}'),
                    'totalBytes', pg_total_relation_size(
                        'public.{TARGET_PARTITION}'),
                    'inserts', (
                        SELECT COALESCE(n_tup_ins, 0)
                        FROM pg_stat_user_tables
                        WHERE relid = to_regclass(
                            'public.{TARGET_PARTITION}')),
                    'updates', (
                        SELECT COALESCE(n_tup_upd, 0)
                        FROM pg_stat_user_tables
                        WHERE relid = to_regclass(
                            'public.{TARGET_PARTITION}')),
                    'deletes', (
                        SELECT COALESCE(n_tup_del, 0)
                        FROM pg_stat_user_tables
                        WHERE relid = to_regclass(
                            'public.{TARGET_PARTITION}'))))
        )
    """
    guard = database.json(query)
    publication = guard.get("publication")
    if not publication:
        raise PilotError("scrape_publication_state singleton is missing")
    if publication.get("publicReadsFrozen"):
        raise PilotError("public reads must be unfrozen")
    if publication.get("workingPublicationId") is not None:
        raise PilotError("working publication must be empty")
    if (
        publication.get("commitIntentOwner")
        or publication.get("commitIntentStartedAt")
    ):
        raise PilotError("publication commit intent must be empty")
    if publication.get("maxScoreMutationGateToken"):
        raise PilotError("max-score mutation gate must be empty")
    if not publication.get("currentPublicationId"):
        raise PilotError("current publication is missing")
    if ensure_integer(guard.get("runningScrapes"), "runningScrapes") != 0:
        raise PilotError("a scrape is still running")
    if (
        ensure_integer(
            guard.get("activePhaseAttempts"),
            "activePhaseAttempts",
        )
        != 0
    ):
        raise PilotError("a scrape phase attempt is still running")
    if ensure_integer(guard.get("waitingLocks"), "waitingLocks") != 0:
        raise PilotError("waiting locks are present")
    if ensure_integer(guard.get("workerBackends"), "workerBackends") != 0:
        raise PilotError("worker-owned database backends are present")
    if (
        ensure_integer(
            guard.get("maintenanceBackends"),
            "maintenanceBackends",
        )
        != 0
    ):
        raise PilotError("another pilot maintenance backend is present")
    if ensure_integer(guard.get("targetLocks"), "targetLocks") != 0:
        raise PilotError("the target relation is locked")
    for worker_status in guard.get("workerStatus") or []:
        if str(worker_status.get("status", "")).lower() not in {
            "offline",
            "stopped",
            "idle",
        }:
            raise PilotError(
                "durable worker status is not held/offline"
            )
    target = guard.get("target") or {}
    if not target.get("parentOid") or not target.get("partitionOid"):
        raise PilotError("the exact parent/partition target is missing")
    if not target.get("attached"):
        raise PilotError("the target partition is not attached")
    if (
        target.get("partitionBound")
        != "FOR VALUES IN ('Solo_PeripheralBass')"
    ):
        raise PilotError(
            "the target partition bound is not the exact pro-bass value"
        )
    return guard


def assert_identity_matches(check_report, host, database_guard):
    expected = check_report["identity"]
    observed = {
        "postgresContainerId": host["postgresContainerId"],
        "databaseOid": database_guard["databaseOid"],
        "systemIdentifier": database_guard["systemIdentifier"],
        "database": database_guard["database"],
        "targetParentOid": database_guard["target"]["parentOid"],
    }
    if observed != expected:
        raise PilotError(
            "production identity differs from the check-stage identity"
        )
    expected_publication = check_report["publicationFence"]
    publication = database_guard["publication"]
    observed_publication = {
        "publishedScrapeId": publication["publishedScrapeId"],
        "currentPublicationId": publication["currentPublicationId"],
        "previousPublicationId": publication[
            "previousPublicationId"
        ],
        "workingPublicationId": publication[
            "workingPublicationId"
        ],
    }
    if observed_publication != expected_publication:
        raise PilotError(
            "publication identity changed after the check stage"
        )


def assert_plan_target_matches(plan, database_guard):
    if (
        database_guard["target"]["partitionOid"]
        != plan["planIdentity"]["partitionOid"]
    ):
        raise PilotError("target partition OID changed after planning")


def load_git_commit(runner):
    script = pathlib.Path(__file__).resolve()
    repository = script.parent.parent
    return runner.run(
        ["git", "-C", str(repository), "rev-parse", "HEAD"],
        timeout=30,
    ).stdout.strip()


def load_tool_source_sha256():
    script = pathlib.Path(__file__).resolve()
    sources = (
        script,
        script.with_suffix(".sh"),
        script.with_name(
            "postgres-pro-bass-snapshot-rewrite-drill.py"
        ),
        script.with_name(
            "postgres-pro-bass-snapshot-rewrite-drill.sh"
        ),
    )
    digest = hashlib.sha256()
    for source in sources:
        digest.update(source.name.encode("utf-8"))
        digest.update(b"\0")
        digest.update(source.read_bytes())
        digest.update(b"\0")
    return digest.hexdigest()


def require_clean_repository(runner):
    script = pathlib.Path(__file__).resolve()
    repository = script.parent.parent
    output = runner.run(
        [
            "git",
            "-C",
            str(repository),
            "status",
            "--porcelain",
            "--untracked-files=no",
        ],
        timeout=30,
    ).stdout.strip()
    if output:
        raise PilotError(
            "production execution requires a clean tracked worktree"
        )


def load_verified_archive_input(
    args,
    runner,
    check,
    observed_source_fence,
    *,
    verify_archive_checksum,
):
    path_value = args.verified_live_archive_input
    expected_sha = args.expected_live_archive_input_sha256
    if not path_value or not expected_sha:
        raise PilotError(
            "production planning requires --verified-live-archive-input "
            "and --expected-live-archive-input-sha256"
        )
    path = pathlib.Path(path_value)
    observed_sha = sha256_path(path)
    if observed_sha != expected_sha:
        raise PilotError(
            "verified live archive input checksum mismatch"
        )
    value = read_json(path)
    if (
        value.get("formatVersion") != FORMAT_VERSION
        or value.get("toolId") != TOOL_ID
        or value.get("target")
        != f"{TARGET_SCHEMA}.{TARGET_PARTITION}"
        or value.get("instrument") != TARGET_INSTRUMENT
    ):
        raise PilotError(
            "verified live archive input identity is invalid"
        )
    source = value.get("source") or {}
    restore = value.get("restore") or {}
    cleanup = value.get("cleanup") or {}
    archive = value.get("archive") or {}
    if source.get("changedDuringArchive") is not False:
        raise PilotError("source changed during the verified archive")
    if (
        restore.get("status") != "succeeded"
        or cleanup.get("status") != "succeeded"
        or not cleanup.get("containerRemoved")
        or not cleanup.get("restorePgdataRemoved")
        or not cleanup.get("archiveRetained")
    ):
        raise PilotError(
            "verified archive restore/cleanup evidence is incomplete"
        )
    if (
        str(source.get("systemIdentifier"))
        != str(check["identity"]["systemIdentifier"])
        or source.get("database") != check["identity"]["database"]
    ):
        raise PilotError(
            "verified archive belongs to another PostgreSQL cluster"
        )
    if not source_fence_matches(source, observed_source_fence):
        raise PilotError(
            "production source fence changed after verified archive"
        )
    if (
        int(restore.get("rowCount", 0)) <= 0
        or int(restore.get("snapshotIdCount", 0)) <= 0
        or int(restore.get("snapshotIdMin", 0)) <= 0
        or int(restore.get("snapshotIdMax", 0))
        < int(restore.get("snapshotIdMin", 0))
    ):
        raise PilotError(
            "verified archive row/snapshot evidence is invalid"
        )
    snapshot_ids = [
        ensure_integer(value, "verified archive snapshot ID", 1)
        for value in restore.get("snapshotIds") or []
    ]
    if (
        snapshot_ids != sorted(set(snapshot_ids))
        or len(snapshot_ids) != int(restore["snapshotIdCount"])
        or snapshot_ids[0] != int(restore["snapshotIdMin"])
        or snapshot_ids[-1] != int(restore["snapshotIdMax"])
    ):
        raise PilotError(
            "verified archive exact snapshot IDs are invalid"
        )
    archive_path = pathlib.Path(str(archive.get("path", "")))
    if (
        not archive_path.is_absolute()
        or not archive_path.is_file()
        or archive_path.is_symlink()
    ):
        raise PilotError("verified archive file is unavailable")
    mount = find_mount(runner, archive_path)
    if (
        mount["source"] != archive.get("deviceSource")
        or mount["deviceId"] != archive.get("deviceId")
        or (
            not args.test_mode
            and mount["source"] != PRODUCTION_SCRATCH_DEVICE
        )
        or (
            not args.test_mode
            and mount["mountTarget"] != "/"
        )
    ):
        raise PilotError(
            "verified archive device identity changed"
        )
    if archive_path.stat().st_size != int(archive.get("bytes", -1)):
        raise PilotError("verified archive byte size changed")
    if verify_archive_checksum:
        archive_sha = sha256_path(archive_path)
        if archive_sha != archive.get("sha256"):
            raise PilotError("verified archive checksum changed")
    distribution_path = pathlib.Path(
        str(restore.get("distributionPath", ""))
    )
    if (
        not distribution_path.is_file()
        or distribution_path.is_symlink()
        or sha256_path(distribution_path)
        != restore.get("distributionSha256")
    ):
        raise PilotError(
            "verified archive distribution checksum changed"
        )
    distribution = read_json(distribution_path)
    distribution_rows = distribution.get("distribution") or []
    distribution_ids = [
        ensure_integer(
            row.get("snapshotId"),
            "verified archive distribution snapshot ID",
            1,
        )
        for row in distribution_rows
    ]
    try:
        content_hashes_valid = all(
            isinstance(row.get("contentHashXor"), int)
            and not isinstance(row.get("contentHashXor"), bool)
            and str(int(row.get("contentHashSum")))
            == str(row.get("contentHashSum"))
            for row in distribution_rows
        )
    except (TypeError, ValueError):
        content_hashes_valid = False
    if (
        distribution.get("status") != "succeeded"
        or int(distribution.get("rowCount", -1))
        != int(restore["rowCount"])
        or distribution.get("snapshotIds") != snapshot_ids
        or distribution_ids != snapshot_ids
        or len(set(distribution_ids)) != len(snapshot_ids)
        or not content_hashes_valid
        or sum(
            ensure_integer(
                row.get("rowCount"),
                "verified archive distribution row count",
                1,
            )
            for row in distribution_rows
        )
        != int(restore["rowCount"])
    ):
        raise PilotError(
            "verified archive distribution is inconsistent"
        )
    catalog_path = pathlib.Path(
        str(restore.get("catalogPath", ""))
    )
    if (
        not catalog_path.is_file()
        or catalog_path.is_symlink()
        or sha256_path(catalog_path)
        != restore.get("catalogSha256")
    ):
        raise PilotError(
            "verified archive catalog checksum changed"
        )
    restored_catalog = read_json(catalog_path)
    evidence_values = {}
    for evidence_path_key, evidence_sha_key, container, label in (
        (
            "validationPath",
            "validationSha256",
            restore,
            "restore validation",
        ),
        (
            "proofPath",
            "proofSha256",
            cleanup,
            "cleanup proof",
        ),
    ):
        evidence_path = pathlib.Path(
            str(container.get(evidence_path_key, ""))
        )
        if (
            not evidence_path.is_file()
            or evidence_path.is_symlink()
            or sha256_path(evidence_path)
            != container.get(evidence_sha_key)
        ):
            raise PilotError(
                "verified archive evidence checksum changed"
            )
        evidence_values[label] = read_json(evidence_path)
    validation = evidence_values["restore validation"]
    cleanup_proof = evidence_values["cleanup proof"]
    validation_data = validation.get("data") or {}
    validation_archive = validation.get("archive") or {}
    validation_catalog = validation.get("catalog") or {}
    validation_source = validation.get("source") or {}
    validation_source_fence = {
        "partitionOid": validation_source.get(
            "partitionOid",
            validation_source.get("oid"),
        ),
        "relfilenode": validation_source.get("relfilenode"),
        "heapBytes": validation_source.get("heapBytes"),
        "indexBytes": validation_source.get("indexBytes"),
        "totalBytes": validation_source.get("totalBytes"),
        "inserts": validation_source.get("inserts"),
        "updates": validation_source.get("updates"),
        "deletes": validation_source.get("deletes"),
    }
    validation_constraints = {
        item.get("name"): item.get("definition")
        for item in validation_catalog.get("constraints") or []
    }
    validation_indexes = {
        item.get("name"): item.get("definition")
        for item in validation_catalog.get("indexes") or []
    }
    if (
        validation.get("status") != "succeeded"
        or validation.get("productionDatabaseMutated") is not False
        or validation.get("sourceChangedDuringArchive") is not False
        or validation_archive.get("checksumMatches") is not True
        or validation_archive.get("path") != str(archive_path)
        or int(validation_archive.get("bytes", -1))
        != int(archive["bytes"])
        or validation_archive.get("sha256") != archive["sha256"]
        or int(validation_data.get("rowCount", -1))
        != int(restore["rowCount"])
        or validation_data.get("snapshotIds") != snapshot_ids
        or validation_data.get("distributionPath")
        != restore.get("distributionPath")
        or validation_data.get("distributionSha256")
        != restore.get("distributionSha256")
        or not source_fence_matches(
            source,
            validation_source_fence,
        )
        or validation_catalog.get("partitionBound")
        != restore.get("partitionBound")
        or catalog_shape(validation_catalog)
        != catalog_shape(restored_catalog)
        or validation_catalog.get("owner")
        != restore.get("owner")
        or validation_constraints.get(
            restore.get("primaryConstraint")
        )
        != restore.get("primaryConstraintDefinition")
        or validation_indexes.get(restore.get("primaryIndex"))
        != restore.get("primaryIndexDefinition")
        or validation_indexes.get(restore.get("scoreIndex"))
        != restore.get("scoreIndexDefinition")
    ):
        raise PilotError(
            "verified archive restore validation is inconsistent"
        )
    if (
        cleanup_proof.get("status") != "succeeded"
        or cleanup_proof.get("containerRemoved") is not True
        or cleanup_proof.get("restorePgdataRemoved") is not True
        or cleanup_proof.get("archiveRetained") is not True
        or cleanup_proof.get("archivePath") != str(archive_path)
        or cleanup_proof.get("archiveSha256") != archive["sha256"]
        or cleanup_proof.get("validationPath")
        != restore["validationPath"]
        or cleanup_proof.get("validationSha256")
        != restore["validationSha256"]
    ):
        raise PilotError(
            "verified archive cleanup proof is inconsistent"
        )
    return value, observed_sha


def stage_check(args, runner, scratch_info, marker):
    host = collect_host_guard(args, runner)
    tablespace_mount = collect_tablespace_mount(args, runner)
    database = Database(
        runner,
        args.pg_container,
        args.pg_user,
        args.pg_database,
    )
    guard = collect_database_guard(database)
    publication = guard["publication"]
    body = {
        "runId": args.run_id,
        "repositoryCommit": marker["repositoryCommit"],
        "toolSourceSha256": marker["toolSourceSha256"],
        "target": {
            "parent": f"{TARGET_SCHEMA}.{TARGET_PARENT}",
            "partition": (
                f"{TARGET_SCHEMA}.{TARGET_PARTITION}"
            ),
            "instrument": TARGET_INSTRUMENT,
            "arbitraryTargetInputAccepted": False,
        },
        "scratch": scratch_info,
        "tablespaceMount": tablespace_mount,
        "host": host,
        "databaseGuard": guard,
        "identity": {
            "postgresContainerId": host["postgresContainerId"],
            "databaseOid": guard["databaseOid"],
            "systemIdentifier": guard["systemIdentifier"],
            "database": guard["database"],
            "targetParentOid": guard["target"]["parentOid"],
        },
        "publicationFence": {
            "publishedScrapeId": publication["publishedScrapeId"],
            "currentPublicationId": publication[
                "currentPublicationId"
            ],
            "previousPublicationId": publication[
                "previousPublicationId"
            ],
            "workingPublicationId": publication[
                "workingPublicationId"
            ],
        },
        "archiveLifecycle": {
            "temporaryScratchOnly": True,
            "archiveMustSurviveDrop": True,
            "deletionRequiresSeparateDecision": True,
        },
        "publicApiBaseline": (
            capture_api_fingerprints(args.api_base)
            if args.api_base
            else []
        ),
    }
    return write_stage_report(args.scratch_root, "check", body)


def protected_sources_query(rollback_count):
    return f"""
        WITH publication_ids AS (
            SELECT current_publication_id AS publication_id,
                   'current_publication'::text AS reason
            FROM scrape_publication_state
            WHERE id = TRUE
              AND current_publication_id IS NOT NULL
            UNION ALL
            SELECT previous_publication_id,
                   'previous_publication'
            FROM scrape_publication_state
            WHERE id = TRUE
              AND previous_publication_id IS NOT NULL
            UNION ALL
            SELECT working_publication_id,
                   'working_publication'
            FROM scrape_publication_state
            WHERE id = TRUE
              AND working_publication_id IS NOT NULL
        ),
        publication_scrapes AS (
            SELECT generation.scrape_id AS published_scrape_id,
                   publication.reason
            FROM publication_ids publication
            JOIN publication_generations generation
              ON generation.publication_id =
                    publication.publication_id
        ),
        protected AS (
            SELECT state.active_snapshot_id AS snapshot_id,
                   'active_snapshot_state'::text AS reason
            FROM leaderboard_snapshot_state state
            WHERE state.instrument =
                    {sql_literal(TARGET_INSTRUMENT)}
              AND state.active_snapshot_id IS NOT NULL
            UNION ALL
            SELECT scope.source_snapshot_id,
                   'current_projection_source'
            FROM solo_current_projection_scope scope
            WHERE scope.instrument =
                    {sql_literal(TARGET_INSTRUMENT)}
              AND scope.source_snapshot_id IS NOT NULL
            UNION ALL
            SELECT source.source_snapshot_id,
                   publication.reason ||
                       '_physical_source'
            FROM publication_scrapes publication
            JOIN leaderboard_published_scope_source source
              ON source.published_scrape_id =
                    publication.published_scrape_id
            WHERE source.instrument =
                    {sql_literal(TARGET_INSTRUMENT)}
              AND source.source_kind = 'snapshot'
              AND source.source_snapshot_id IS NOT NULL
            UNION ALL
            SELECT rollback.id,
                   'rollback_completed_snapshot'
            FROM (
                SELECT id
                FROM scrape_log
                WHERE completed_at IS NOT NULL
                  AND COALESCE(
                      to_jsonb(scrape_log)->>'status',
                      'completed') = 'completed'
                ORDER BY id DESC
                LIMIT {int(rollback_count)}
            ) rollback
        )
        SELECT COALESCE(
            json_agg(
                json_build_object(
                    'snapshotId', grouped.snapshot_id,
                    'reasons', grouped.reasons)
                ORDER BY grouped.snapshot_id DESC),
            '[]'::json)
        FROM (
            SELECT snapshot_id,
                   array_agg(
                       DISTINCT reason ORDER BY reason) AS reasons
            FROM protected
            WHERE snapshot_id IS NOT NULL
            GROUP BY snapshot_id
        ) grouped
    """


def inventory_query():
    return f"""
        WITH RECURSIVE snapshot_ids(snapshot_id) AS (
            (
                SELECT MIN(snapshot_id)
                FROM {qualified(TARGET_PARTITION)}
            )
            UNION ALL
            SELECT (
                SELECT MIN(next_snapshot.snapshot_id)
                FROM {qualified(TARGET_PARTITION)} next_snapshot
                WHERE next_snapshot.snapshot_id >
                    current_snapshot.snapshot_id
            )
            FROM snapshot_ids current_snapshot
            WHERE current_snapshot.snapshot_id IS NOT NULL
        ),
        existing_snapshot_ids AS (
            SELECT snapshot_id
            FROM snapshot_ids
            WHERE snapshot_id IS NOT NULL
        ),
        named_publication_scrapes AS (
            SELECT generation.scrape_id
            FROM scrape_publication_state state
            CROSS JOIN LATERAL unnest(ARRAY[
                state.current_publication_id,
                state.previous_publication_id,
                state.working_publication_id
            ]) AS selected(publication_id)
            JOIN publication_generations generation
              ON generation.publication_id =
                    selected.publication_id
            WHERE state.id = TRUE
              AND selected.publication_id IS NOT NULL
        ),
        source_ownership AS (
            SELECT source.source_snapshot_id AS snapshot_id,
                   COUNT(*)::bigint AS source_map_count,
                   COUNT(*) FILTER (
                       WHERE source.published_scrape_id IN (
                           SELECT scrape_id
                           FROM named_publication_scrapes)
                   )::bigint AS named_source_map_count,
                   array_agg(
                       DISTINCT source.published_scrape_id
                       ORDER BY source.published_scrape_id
                   ) AS published_scrape_ids
            FROM leaderboard_published_scope_source source
            WHERE source.instrument =
                    {sql_literal(TARGET_INSTRUMENT)}
              AND source.source_kind = 'snapshot'
              AND source.source_snapshot_id IS NOT NULL
            GROUP BY source.source_snapshot_id
        )
        SELECT COALESCE(
            json_agg(
                json_build_object(
                    'snapshotId', ids.snapshot_id,
                    'scrapeLogPresent', scrape.id IS NOT NULL,
                    'scrapeCompletedAt', scrape.completed_at,
                    'scrapeStatus',
                        COALESCE(
                            to_jsonb(scrape)->>'status',
                            CASE WHEN scrape.completed_at IS NULL
                                 THEN 'running'
                                 ELSE 'completed' END),
                    'sourceMapCount',
                        COALESCE(ownership.source_map_count, 0),
                    'namedSourceMapCount',
                        COALESCE(
                            ownership.named_source_map_count, 0),
                    'publishedScrapeIds',
                        COALESCE(
                            ownership.published_scrape_ids,
                            ARRAY[]::bigint[]))
                ORDER BY ids.snapshot_id DESC),
            '[]'::json)
        FROM existing_snapshot_ids ids
        LEFT JOIN scrape_log scrape
          ON scrape.id = ids.snapshot_id
        LEFT JOIN source_ownership ownership
          ON ownership.snapshot_id = ids.snapshot_id
    """


def snapshot_distribution_query(relation_name, predicate="TRUE"):
    expression = snapshot_row_expression("snapshot")
    return f"""
        SELECT COALESCE(
            json_agg(
                json_build_object(
                    'snapshotId', snapshot_id,
                    'rowCount', row_count,
                    'songIdMin', song_id_min,
                    'songIdMax', song_id_max,
                    'accountIdMinHash', account_id_min_hash,
                    'accountIdMaxHash', account_id_max_hash,
                    'scoreMin', score_min,
                    'scoreMax', score_max,
                    'hashXor0', hash_xor_0,
                    'hashXor1', hash_xor_1,
                    'hashSum2', hash_sum_2)
                ORDER BY snapshot_id DESC),
            '[]'::json)
        FROM (
            SELECT snapshot.snapshot_id,
                   COUNT(*)::bigint AS row_count,
                   MIN(snapshot.song_id) AS song_id_min,
                   MAX(snapshot.song_id) AS song_id_max,
                   md5(MIN(snapshot.account_id)) AS
                       account_id_min_hash,
                   md5(MAX(snapshot.account_id)) AS
                       account_id_max_hash,
                   MIN(snapshot.score) AS score_min,
                   MAX(snapshot.score) AS score_max,
                   COALESCE(bit_xor(hashtextextended(
                       {expression}, 0)), 0) AS hash_xor_0,
                   COALESCE(bit_xor(hashtextextended(
                       {expression}, 1)), 0) AS hash_xor_1,
                   COALESCE(SUM(hashtextextended(
                       {expression}, 2)::numeric), 0)::text AS
                       hash_sum_2
            FROM {qualified(relation_name)} snapshot
            WHERE {predicate}
            GROUP BY snapshot.snapshot_id
        ) distribution
    """


def data_distribution(rows):
    keys = (
        "snapshotId",
        "rowCount",
        "songIdMin",
        "songIdMax",
        "accountIdMinHash",
        "accountIdMaxHash",
        "scoreMin",
        "scoreMax",
        "hashXor0",
        "hashXor1",
        "hashSum2",
    )
    return [
        {key: row.get(key) for key in keys}
        for row in sorted(
            rows,
            key=lambda value: value["snapshotId"],
            reverse=True,
        )
    ]


def catalog_query():
    return f"""
        SELECT json_build_object(
            'parentDefinition', pg_get_partkeydef(
                'public.{TARGET_PARENT}'::regclass),
            'partitionBound', (
                SELECT pg_get_expr(relpartbound, oid, true)
                FROM pg_class
                WHERE oid =
                    'public.{TARGET_PARTITION}'::regclass),
            'owner', (
                SELECT pg_get_userbyid(relowner)
                FROM pg_class
                WHERE oid =
                    'public.{TARGET_PARTITION}'::regclass),
            'tablespace', (
                SELECT COALESCE(
                    tablespace.spcname, 'pg_default')
                FROM pg_class relation
                LEFT JOIN pg_tablespace tablespace
                  ON tablespace.oid = relation.reltablespace
                WHERE relation.oid =
                    'public.{TARGET_PARTITION}'::regclass),
            'columns', (
                SELECT json_agg(
                    json_build_object(
                        'ordinal', attribute.attnum,
                        'name', attribute.attname,
                        'type', format_type(
                            attribute.atttypid,
                            attribute.atttypmod),
                        'notNull', attribute.attnotnull,
                        'defaultExpression',
                            pg_get_expr(
                                default_value.adbin,
                                default_value.adrelid))
                    ORDER BY attribute.attnum)
                FROM pg_attribute attribute
                LEFT JOIN pg_attrdef default_value
                  ON default_value.adrelid =
                        attribute.attrelid
                 AND default_value.adnum =
                        attribute.attnum
                WHERE attribute.attrelid =
                    'public.{TARGET_PARTITION}'::regclass
                  AND attribute.attnum > 0
                  AND NOT attribute.attisdropped),
            'constraints', (
                SELECT COALESCE(
                    json_agg(
                        json_build_object(
                            'name', constraint_name,
                            'type', constraint_type,
                            'definition', definition)
                        ORDER BY constraint_name),
                    '[]'::json)
                FROM (
                    SELECT constraint_row.conname AS constraint_name,
                           constraint_row.contype AS constraint_type,
                           pg_get_constraintdef(
                               constraint_row.oid, true) AS definition
                    FROM pg_constraint constraint_row
                    WHERE constraint_row.conrelid =
                        'public.{TARGET_PARTITION}'::regclass
                ) constraints),
            'indexes', (
                SELECT COALESCE(
                    json_agg(
                        json_build_object(
                            'name', index_name,
                            'definition', definition,
                            'isPrimary', is_primary,
                            'isUnique', is_unique,
                            'isValid', is_valid)
                        ORDER BY index_name),
                    '[]'::json)
                FROM (
                    SELECT index_class.relname AS index_name,
                           pg_get_indexdef(
                               index_class.oid) AS definition,
                           index_metadata.indisprimary AS is_primary,
                           index_metadata.indisunique AS is_unique,
                           index_metadata.indisvalid AS is_valid
                    FROM pg_index index_metadata
                    JOIN pg_class index_class
                      ON index_class.oid =
                            index_metadata.indexrelid
                    WHERE index_metadata.indrelid =
                        'public.{TARGET_PARTITION}'::regclass
                ) indexes),
            'heapBytes', pg_relation_size(
                'public.{TARGET_PARTITION}'),
            'indexBytes', pg_indexes_size(
                'public.{TARGET_PARTITION}'),
            'totalBytes', pg_total_relation_size(
                'public.{TARGET_PARTITION}')
        )
    """


def classify_inventory(
    protected,
    inventory,
    *,
    archive_verified=False,
):
    reasons_by_id = {
        ensure_integer(row["snapshotId"], "protected snapshot ID", 1):
            list(row.get("reasons") or [])
        for row in protected
    }
    inventory_by_id = {
        ensure_integer(row["snapshotId"], "inventory snapshot ID", 1):
            row
        for row in inventory
    }
    missing_protected = sorted(
        set(reasons_by_id) - set(inventory_by_id),
        reverse=True,
    )
    missing_required = [
        snapshot_id
        for snapshot_id in missing_protected
        if any(
            reason != "rollback_completed_snapshot"
            for reason in reasons_by_id[snapshot_id]
        )
    ]
    blocked = []
    keep = []
    purge = []
    for snapshot_id, row in sorted(
        inventory_by_id.items(),
        reverse=True,
    ):
        item = {
            **row,
            "snapshotId": snapshot_id,
        }
        if snapshot_id in reasons_by_id:
            item["classification"] = "keep"
            item["reasons"] = reasons_by_id[snapshot_id]
            keep.append(item)
            continue
        ownership_caveats = []
        if not row.get("scrapeLogPresent"):
            ownership_caveats.append("missing scrape_log ownership")
        if row.get("scrapeCompletedAt") is None:
            ownership_caveats.append("scrape is incomplete")
        if row.get("scrapeStatus") not in ("completed", "complete"):
            ownership_caveats.append(
                f"scrape status is {row.get('scrapeStatus')}"
            )
        hard_failures = []
        if ensure_integer(
            row.get("namedSourceMapCount", 0),
            "namedSourceMapCount",
        ):
            hard_failures.append(
                "named publication source map was not protected"
            )
        failures = (
            hard_failures
            + ([] if archive_verified else ownership_caveats)
        )
        if failures:
            item["classification"] = "blocked"
            item["reasons"] = failures
            blocked.append(item)
        else:
            item["classification"] = "archive_then_purge"
            item["reasons"] = [
                (
                    "verified archive preserves this exact snapshot "
                    "content"
                    if archive_verified
                    else "completed scrape outside all protected ownership"
                ),
                (
                    "historical source maps, if any, are preserved "
                    "by the immutable archive"
                ),
                *(
                    [
                        "legacy scrape ownership caveats: "
                        + "; ".join(ownership_caveats)
                    ]
                    if ownership_caveats
                    else []
                ),
            ]
            purge.append(item)
    return {
        "keep": keep,
        "archiveThenPurge": purge,
        "blocked": blocked,
        "missingProtectedSnapshotIds": missing_protected,
        "missingRequiredSnapshotIds": missing_required,
    }


def stage_plan(args, runner):
    check = load_report(args.scratch_root, "check")
    host = collect_host_guard(args, runner)
    database = Database(
        runner,
        args.pg_container,
        args.pg_user,
        args.pg_database,
    )
    guard = collect_database_guard(database)
    assert_identity_matches(check, host, guard)
    protected = database.json(
        protected_sources_query(args.rollback_completed_to_keep),
        timeout=args.query_timeout_seconds,
    )
    verified_archive = None
    verified_archive_sha = None
    if args.test_mode and not args.verified_live_archive_input:
        inventory = database.json(
            inventory_query(),
            timeout=args.query_timeout_seconds,
            pgoptions=PLAN_QUERY_PGOPTIONS,
        )
    else:
        verified_archive, verified_archive_sha = (
            load_verified_archive_input(
                args,
                runner,
                check,
                guard["target"],
                verify_archive_checksum=True,
            )
        )
        inventory = database.json(
            inventory_query(),
            timeout=args.query_timeout_seconds,
            pgoptions=LOOSE_ID_PGOPTIONS,
        )
        snapshot_ids = sorted(
            row["snapshotId"] for row in inventory
        )
        restore = verified_archive["restore"]
        if snapshot_ids != restore["snapshotIds"]:
            raise PilotError(
                "loose-index snapshot IDs differ from verified archive"
            )
    classification = classify_inventory(
        protected,
        inventory,
        archive_verified=verified_archive is not None,
    )
    if classification["blocked"]:
        raise PilotError(
            "snapshot ownership classification contains blocked IDs"
        )
    if classification["missingRequiredSnapshotIds"]:
        raise PilotError(
            "protected source IDs are missing from the exact target "
            "partition: "
            + ", ".join(
                str(value)
                for value in classification[
                    "missingRequiredSnapshotIds"
                ]
            )
        )
    if not classification["keep"]:
        raise PilotError("exact retained snapshot set is empty")
    if not classification["archiveThenPurge"]:
        raise PilotError("exact purge candidate set is empty")
    keep_ids = [
        row["snapshotId"] for row in classification["keep"]
    ]
    keep_predicate = snapshot_id_predicate(keep_ids)
    retained_fingerprint = database.json(
        fingerprint_sql(TARGET_PARTITION, keep_predicate),
        timeout=args.query_timeout_seconds,
        pgoptions=PLAN_QUERY_PGOPTIONS,
    )
    catalog = database.json(catalog_query())
    if catalog["tablespace"] != "pg_default":
        raise PilotError(
            "pilot target must reside in the default FST tablespace"
        )
    catalog_names = source_catalog_names(catalog)
    if verified_archive is not None:
        restore_catalog = verified_archive["restore"]
        restored_catalog = read_json(
            pathlib.Path(restore_catalog["catalogPath"])
        )
        constraint_definitions = {
            item["name"]: item["definition"]
            for item in catalog["constraints"]
        }
        index_definitions = {
            item["name"]: item["definition"]
            for item in catalog["indexes"]
        }
        if (
            catalog_shape(restored_catalog)
            != catalog_shape(catalog)
            or restore_catalog.get("partitionBound")
            != catalog["partitionBound"]
            or restore_catalog.get("owner") != catalog["owner"]
            or restore_catalog.get("primaryConstraint")
            != catalog_names["primaryConstraint"]
            or restore_catalog.get("primaryIndex")
            != catalog_names["primaryIndex"]
            or restore_catalog.get("scoreIndex")
            != catalog_names["scoreIndex"]
            or restore_catalog.get("primaryConstraintDefinition")
            != constraint_definitions[
                catalog_names["primaryConstraint"]
            ]
            or restore_catalog.get("primaryIndexDefinition")
            != index_definitions[catalog_names["primaryIndex"]]
            or restore_catalog.get("scoreIndexDefinition")
            != index_definitions[catalog_names["scoreIndex"]]
        ):
            raise PilotError(
                "verified restore catalog differs from the live source"
            )
    exact_retained_rows = ensure_integer(
        retained_fingerprint.get("rowCount"),
        "protected fingerprint row count",
        1,
    )
    exact_total_rows = (
        int(verified_archive["restore"]["rowCount"])
        if verified_archive is not None
        else None
    )
    exact_purge_rows = (
        exact_total_rows - exact_retained_rows
        if exact_total_rows is not None
        else None
    )
    if exact_purge_rows is not None and exact_purge_rows <= 0:
        raise PilotError(
            "verified archive has no purgeable rows after protection"
        )
    source_relation_state = database.json(
        relation_state_query(TARGET_PARTITION)
    )
    if not source_relation_state:
        raise PilotError("source relation state is unavailable")
    pre_swap_reference_parity = database.json(
        reference_parity_query(TARGET_PARTITION),
        timeout=args.query_timeout_seconds,
    )
    plan_identity = {
        "runId": args.run_id,
        "partitionOid": guard["target"]["partitionOid"],
        "publicationFence": check["publicationFence"],
        "snapshotIds": [
            row["snapshotId"] for row in inventory
        ],
        "keepSnapshotIds": keep_ids,
        "purgeSnapshotIds": [
            row["snapshotId"]
            for row in classification["archiveThenPurge"]
        ],
        "exactRetainedRows": exact_retained_rows,
        "exactTotalRows": exact_total_rows,
        "exactPurgeRows": exact_purge_rows,
        "retainedFingerprint": retained_fingerprint,
        "sourceRelationIdentity": physical_relation_identity(
            source_relation_state
        ),
        "verifiedLiveArchiveInputSha256": verified_archive_sha,
        "referenceParity": pre_swap_reference_parity,
    }
    plan_hash = sha256_bytes(canonical_json_bytes(plan_identity))
    body = {
        "runId": args.run_id,
        "planId": f"pro-bass-{plan_hash[:20]}",
        "planSha256": plan_hash,
        "planIdentity": plan_identity,
        "protectedSources": protected,
        "classification": classification,
        "catalog": catalog,
        "sourceRelationState": source_relation_state,
        "verifiedLiveArchive": (
            {
                "inputPath": str(args.verified_live_archive_input),
                "inputSha256": verified_archive_sha,
                "archivePath": verified_archive["archive"]["path"],
                "archiveSha256": verified_archive["archive"]["sha256"],
                "archiveBytes": verified_archive["archive"]["bytes"],
                "restore": verified_archive["restore"],
                "cleanup": verified_archive["cleanup"],
            }
            if verified_archive is not None
            else None
        ),
        "sourceCatalogNames": catalog_names,
        "replacementRelation": (
            f"{TARGET_SCHEMA}.{replacement_name(args.run_id)}"
        ),
        "retiredRelation": (
            f"{TARGET_SCHEMA}.{retired_name(args.run_id)}"
        ),
        "capacityInputs": {
            "currentHeapBytes": catalog["heapBytes"],
            "currentIndexBytes": catalog["indexBytes"],
            "currentTotalBytes": catalog["totalBytes"],
            "emergencyFloorBytes": EMERGENCY_FLOOR_BYTES,
        },
        "executable": True,
    }
    return write_stage_report(args.scratch_root, "plan", body)


def pg_dump_command(args):
    return [
        "docker",
        "exec",
        "-e",
        "PGCONNECT_TIMEOUT=10",
        "-e",
        (
            "PGOPTIONS=-c row_security=off "
            f"-c application_name={APPLICATION_NAME}"
        ),
        args.pg_container,
        "pg_dump",
        "-Fc",
        "--compress=6",
        "--no-privileges",
        "-U",
        args.pg_user,
        "-d",
        args.pg_database,
        "--table",
        f"{TARGET_SCHEMA}.{TARGET_PARENT}",
        "--table",
        f"{TARGET_SCHEMA}.{TARGET_PARTITION}",
    ]


def stage_archive(args, runner):
    if not args.execute:
        raise PilotError("archive requires --execute")
    check = load_report(args.scratch_root, "check")
    plan = load_report(args.scratch_root, "plan")
    host = collect_host_guard(args, runner)
    database = Database(
        runner,
        args.pg_container,
        args.pg_user,
        args.pg_database,
    )
    guard = collect_database_guard(database)
    assert_identity_matches(check, host, guard)
    assert_plan_target_matches(plan, guard)
    pre_archive_state = database.json(
        relation_state_query(TARGET_PARTITION)
    )
    if (
        physical_relation_identity(pre_archive_state)
        != plan["planIdentity"]["sourceRelationIdentity"]
    ):
        raise PilotError(
            "source partition changed after exact planning"
        )
    if plan.get("verifiedLiveArchive") is not None:
        verified, verified_sha = load_verified_archive_input(
            args,
            runner,
            check,
            guard["target"],
            verify_archive_checksum=True,
        )
        if (
            verified_sha
            != plan["verifiedLiveArchive"]["inputSha256"]
        ):
            raise PilotError(
                "verified archive input differs from the plan"
            )
        body = {
            "runId": args.run_id,
            "planId": plan["planId"],
            "adoptedVerifiedArchive": True,
            "verifiedLiveArchiveInput": {
                "path": str(args.verified_live_archive_input),
                "sha256": verified_sha,
            },
            "archive": {
                "path": verified["archive"]["path"],
                "bytes": verified["archive"]["bytes"],
                "sha256": verified["archive"]["sha256"],
                "format": "PostgreSQL custom",
            },
            "sourceExactRows": verified["restore"]["rowCount"],
            "sourceRelationState": pre_archive_state,
            "sourceChangedDuringArchive": False,
        }
        return write_stage_report(
            args.scratch_root,
            "archive",
            body,
        )
    archive_root = pathlib.Path(args.scratch_root) / ARCHIVE_DIR
    scratch_usage = shutil.disk_usage(args.scratch_root)
    source_total_bytes = plan["catalog"]["totalBytes"]
    archive_budget_bytes = source_total_bytes
    restore_budget_bytes = source_total_bytes + 1024**3
    scratch_reserve_bytes = max(
        source_total_bytes // 4,
        10 * 1024**3,
    )
    scratch_required_bytes = (
        archive_budget_bytes
        + restore_budget_bytes
        + scratch_reserve_bytes
    )
    scratch_gate = {
        "observedFreeBytes": scratch_usage.free,
        "archiveBudgetBytes": archive_budget_bytes,
        "restoreWorkspaceBudgetBytes": restore_budget_bytes,
        "reserveBytes": scratch_reserve_bytes,
        "requiredFreeBytes": scratch_required_bytes,
        "allowed": scratch_usage.free >= scratch_required_bytes,
    }
    if not scratch_gate["allowed"]:
        raise PilotError(
            "scratch capacity gate failed: "
            f"need {scratch_required_bytes} bytes, "
            f"observed {scratch_usage.free} bytes"
        )
    archive_path = archive_root / "pro-bass-original.custom"
    started = time.monotonic()
    resumed_archive = archive_path.is_file()
    if resumed_archive:
        metadata = archive_path.lstat()
        if archive_path.is_symlink() or not stat.S_ISREG(
            metadata.st_mode
        ):
            raise PilotError(
                "existing archive is not a regular file"
            )
    else:
        runner.run_to_file(
            pg_dump_command(args),
            archive_path,
            timeout=args.archive_timeout_seconds,
        )
    elapsed = time.monotonic() - started
    archive_bytes = archive_path.stat().st_size
    if archive_bytes <= 0:
        raise PilotError("pg_dump archive is empty")
    list_path = archive_root / "pro-bass-original.list"
    # pg_restore cannot read the host archive from stdin via docker exec
    # without binary piping, so inspect it with a read-only bind container.
    image = host["postgresImage"]
    listing = runner.run(
        [
            "docker",
            "run",
            "--rm",
            "--network",
            "none",
            "--read-only",
            "-v",
            f"{archive_root}:/archive:ro",
            image,
            "pg_restore",
            "-l",
            "/archive/pro-bass-original.custom",
        ],
        timeout=600,
    )
    write_or_verify_bytes(
        list_path,
        listing.stdout.encode("utf-8"),
    )
    required_listing_tokens = (
        f"TABLE public {TARGET_PARENT}",
        f"TABLE public {TARGET_PARTITION}",
        f"TABLE ATTACH public {TARGET_PARTITION}",
        f"TABLE DATA public {TARGET_PARTITION}",
        f"CONSTRAINT public {TARGET_PARTITION}",
        "INDEX ATTACH public",
        *(
            item["name"]
            for item in plan["catalog"]["indexes"]
        ),
    )
    missing_tokens = [
        token
        for token in required_listing_tokens
        if token not in listing.stdout
    ]
    if missing_tokens:
        raise PilotError(
            "custom archive lacks required schema/data/index entries: "
            + ", ".join(missing_tokens)
        )
    post_archive_state = database.json(
        relation_state_query(TARGET_PARTITION)
    )
    if (
        physical_relation_identity(post_archive_state)
        != physical_relation_identity(pre_archive_state)
    ):
        raise PilotError(
            "source partition changed while the archive was streaming"
        )
    catalog_path = archive_root / "original-catalog.json"
    write_or_verify_json(catalog_path, plan["catalog"])
    manifest_path = archive_root / "manifest.json"
    manifest = {
        "formatVersion": FORMAT_VERSION,
        "toolId": TOOL_ID,
        "runId": args.run_id,
        "planId": plan["planId"],
        "createdAtUtc": utc_now(),
        "archive": {
            "path": str(archive_path),
            "bytes": archive_bytes,
            "sha256": sha256_path(archive_path),
            "format": "PostgreSQL custom",
            "compression": 6,
            "elapsedSeconds": round(elapsed, 3),
        },
        "catalog": {
            "path": str(catalog_path),
            "sha256": sha256_path(catalog_path),
        },
        "listing": {
            "path": str(list_path),
            "sha256": sha256_path(list_path),
        },
        "source": {
            "databaseIdentity": check["identity"],
            "publicationFence": check["publicationFence"],
            "partitionOid": plan["planIdentity"]["partitionOid"],
            "estimatedRows": pre_archive_state["estimatedRows"],
            "exactProtectedRows": plan["planIdentity"][
                "exactRetainedRows"
            ],
            "snapshotIds": [
                row["snapshotId"]
                for row in (
                    plan["classification"]["keep"]
                    + plan["classification"]["archiveThenPurge"]
                )
            ],
            "snapshotIdCount": len(
                plan["classification"]["keep"]
                + plan["classification"]["archiveThenPurge"]
            ),
            "snapshotIdMin": min(
                row["snapshotId"]
                for row in (
                    plan["classification"]["keep"]
                    + plan["classification"]["archiveThenPurge"]
                )
            ),
            "snapshotIdMax": max(
                row["snapshotId"]
                for row in (
                    plan["classification"]["keep"]
                    + plan["classification"]["archiveThenPurge"]
                )
            ),
            "snapshotInventory": (
                plan["classification"]["keep"]
                + plan["classification"]["archiveThenPurge"]
            ),
            "retainedFingerprint": plan["planIdentity"][
                "retainedFingerprint"
            ],
            "relationStateBefore": pre_archive_state,
            "relationStateAfter": post_archive_state,
        },
        "restoreCommandTemplate": (
            "pg_restore --exit-on-error --dbname <isolated-db> "
            "pro-bass-original.custom"
        ),
        "retention": {
            "temporaryScratchOnly": True,
            "retainThroughAcceptance": True,
            "deleteOnlyAfterSeparateOperatorDecision": True,
        },
    }
    if manifest_path.exists():
        manifest = read_json(manifest_path)
        if (
            manifest.get("planId") != plan["planId"]
            or manifest.get("archive", {}).get("sha256")
            != sha256_path(archive_path)
        ):
            raise PilotError(
                "existing archive manifest differs from the active plan"
            )
    else:
        write_json_exclusive(manifest_path, manifest)
    body = {
        "runId": args.run_id,
        "planId": plan["planId"],
        "archiveManifest": {
            "path": str(manifest_path),
            "sha256": sha256_path(manifest_path),
        },
        "archive": manifest["archive"],
        "sourceRetainedFingerprint": manifest["source"][
            "retainedFingerprint"
        ],
        "sourceExactProtectedRows": manifest["source"][
            "exactProtectedRows"
        ],
        "sourceEstimatedRows": manifest["source"]["estimatedRows"],
        "sourceRelationStateBefore": pre_archive_state,
        "sourceRelationStateAfter": post_archive_state,
        "scratchCapacityGate": scratch_gate,
        "resumedExistingArchive": resumed_archive,
    }
    return write_stage_report(args.scratch_root, "archive", body)


def wait_for_postgres(runner, container, user, database, timeout=120):
    deadline = time.monotonic() + timeout
    consecutive_successes = 0
    while time.monotonic() < deadline:
        completed = runner.run(
            [
                "docker",
                "exec",
                container,
                "psql",
                "-X",
                "-v",
                "ON_ERROR_STOP=1",
                "-U",
                user,
                "-d",
                database,
                "-At",
                "-c",
                "SELECT 1",
            ],
            timeout=15,
            check=False,
        )
        if completed.returncode == 0:
            logs = runner.run(
                ["docker", "logs", container],
                timeout=15,
                check=False,
            )
            initialized = (
                "PostgreSQL init process complete"
                in (logs.stdout + logs.stderr)
            )
            consecutive_successes = (
                consecutive_successes + 1
                if initialized
                else 0
            )
            if consecutive_successes >= 3:
                return
        else:
            consecutive_successes = 0
        time.sleep(1)
    raise PilotError(
        f"isolated PostgreSQL container {container} did not become ready"
    )


def cleanup_owned_directory(runner, image, directory):
    uid = os.getuid()
    gid = os.getgid()
    runner.run(
        [
            "docker",
            "run",
            "--rm",
            "--network",
            "none",
            "--user",
            "0:0",
            "-v",
            f"{directory}:/owned",
            image,
            "sh",
            "-c",
            (
                "find /owned -mindepth 1 -maxdepth 1 "
                "-exec rm -rf -- {} + && "
                f"chown {uid}:{gid} /owned && chmod 700 /owned"
            ),
        ],
        timeout=600,
    )


def stage_drill(args, runner):
    if not args.execute:
        raise PilotError("drill requires --execute")
    check = load_report(args.scratch_root, "check")
    plan = load_report(args.scratch_root, "plan")
    archive = load_report(args.scratch_root, "archive")
    if archive.get("adoptedVerifiedArchive"):
        host = collect_host_guard(args, runner)
        database = Database(
            runner,
            args.pg_container,
            args.pg_user,
            args.pg_database,
        )
        guard = collect_database_guard(database)
        assert_identity_matches(check, host, guard)
        verified, verified_sha = load_verified_archive_input(
            args,
            runner,
            check,
            guard["target"],
            verify_archive_checksum=True,
        )
        total_rows = ensure_integer(
            verified["restore"]["rowCount"],
            "verified archive row count",
            1,
        )
        retained_rows = ensure_integer(
            plan["planIdentity"]["exactRetainedRows"],
            "exact retained row count",
            1,
        )
        purge_rows = total_rows - retained_rows
        if purge_rows <= 0:
            raise PilotError(
                "verified archive contains no purgeable rows"
            )
        body = {
            "runId": args.run_id,
            "planId": plan["planId"],
            "adoptedVerifiedRestore": True,
            "verifiedLiveArchiveInput": {
                "path": str(args.verified_live_archive_input),
                "sha256": verified_sha,
            },
            "archiveSha256": verified["archive"]["sha256"],
            "exactRows": {
                "total": total_rows,
                "retained": retained_rows,
                "purge": purge_rows,
            },
            "restoredCatalog": {
                "partitionBound": verified["restore"][
                    "partitionBound"
                ],
                "primaryIndex": verified["restore"][
                    "primaryIndex"
                ],
                "scoreIndex": verified["restore"]["scoreIndex"],
            },
            "cleanupProof": verified["cleanup"],
        }
        return write_stage_report(
            args.scratch_root,
            "drill",
            body,
        )
    manifest = read_json(
        pathlib.Path(args.scratch_root)
        / ARCHIVE_DIR
        / "manifest.json"
    )
    archive_path = pathlib.Path(
        manifest["archive"]["path"]
    )
    if sha256_path(archive_path) != manifest["archive"]["sha256"]:
        raise PilotError("archive checksum changed before restore drill")
    restore_root = pathlib.Path(args.scratch_root) / RESTORE_DIR
    data_root = restore_root / "pgdata"
    if data_root.exists():
        metadata = data_root.lstat()
        if data_root.is_symlink() or not stat.S_ISDIR(
            metadata.st_mode
        ):
            raise PilotError(
                "restore drill data path is not a real directory"
            )
        if any(data_root.iterdir()):
            raise PilotError(
                "restore drill data directory is not empty"
            )
    data_root.mkdir(exist_ok=True, mode=0o700)
    image = args.restore_image or check["host"]["postgresImage"]
    container = (
        f"fst-pro-bass-restore-{relation_token(args.run_id)}"
    )
    before_usage = shutil.disk_usage(args.scratch_root)
    runner.run(
        [
            "docker",
            "run",
            "--name",
            container,
            "--detach",
            "--network",
            "none",
            "-e",
            "POSTGRES_HOST_AUTH_METHOD=trust",
            "-e",
            f"POSTGRES_USER={args.pg_user}",
            "-e",
            f"POSTGRES_DB={args.pg_database}",
            "-v",
            f"{data_root}:/var/lib/postgresql/data",
            "-v",
            (
                f"{pathlib.Path(args.scratch_root) / ARCHIVE_DIR}"
                ":/archive:ro"
            ),
            image,
        ],
        timeout=120,
    )
    started = time.monotonic()
    try:
        wait_for_postgres(
            runner,
            container,
            args.pg_user,
            args.pg_database,
        )
        runner.run(
            [
                "docker",
                "exec",
                container,
                "pg_restore",
                "--exit-on-error",
                "--no-privileges",
                "-U",
                args.pg_user,
                "-d",
                args.pg_database,
                "/archive/pro-bass-original.custom",
            ],
            timeout=args.archive_timeout_seconds,
        )
        restored = Database(
            runner,
            container,
            args.pg_user,
            args.pg_database,
        )
        restored_fingerprint = restored.json(
            fingerprint_sql(TARGET_PARTITION),
            timeout=args.query_timeout_seconds,
        )
        restored_retained_fingerprint = restored.json(
            fingerprint_sql(
                TARGET_PARTITION,
                snapshot_id_predicate(
                    plan["planIdentity"]["keepSnapshotIds"]
                ),
            ),
            timeout=args.query_timeout_seconds,
        )
        restored_catalog = restored.json(catalog_query())
        restored_distribution = restored.json(
            snapshot_distribution_query(TARGET_PARTITION),
            timeout=args.query_timeout_seconds,
        )
        expected_snapshot_ids = sorted(
            plan["planIdentity"]["snapshotIds"],
            reverse=True,
        )
        restored_snapshot_ids = [
            row["snapshotId"] for row in restored_distribution
        ]
        if restored_snapshot_ids != expected_snapshot_ids:
            raise PilotError(
                "restored snapshot IDs differ from the production plan"
            )
        if catalog_shape(restored_catalog) != catalog_shape(
            plan["catalog"]
        ):
            raise PilotError(
                "restored schema/index/constraint/ownership catalog "
                "differs from source"
            )
        if (
            restored_retained_fingerprint
            != plan["planIdentity"]["retainedFingerprint"]
        ):
            raise PilotError(
                "restored protected rows differ from the production plan"
            )
        exact_total_rows = ensure_integer(
            restored_fingerprint.get("rowCount"),
            "restored exact row count",
            1,
        )
        distribution_rows = sum(
            ensure_integer(
                row.get("rowCount"),
                "restored snapshot row count",
                1,
            )
            for row in restored_distribution
        )
        if distribution_rows != exact_total_rows:
            raise PilotError(
                "restored distribution does not cover the archive"
            )
        exact_purge_rows = (
            exact_total_rows
            - plan["planIdentity"]["exactRetainedRows"]
        )
        if exact_purge_rows <= 0:
            raise PilotError(
                "archive restore does not contain purgeable rows"
            )
        restore_elapsed = time.monotonic() - started
        restore_bytes = int(
            runner.run(
                [
                    "docker",
                    "exec",
                    container,
                    "du",
                    "-sb",
                    "/var/lib/postgresql/data",
                ],
                timeout=120,
            ).stdout.split()[0]
        )
    finally:
        runner.run(
            ["docker", "rm", "-f", container],
            timeout=120,
            check=False,
        )
    cleanup_owned_directory(runner, image, data_root)
    after_cleanup_usage = shutil.disk_usage(args.scratch_root)
    cleanup_proof = {
        "dataDirectory": str(data_root),
        "remainingEntries": [
            value.name for value in data_root.iterdir()
        ],
        "freeBytesBefore": before_usage.free,
        "freeBytesAfterCleanup": after_cleanup_usage.free,
    }
    if cleanup_proof["remainingEntries"]:
        raise PilotError(
            "restore drill workspace cleanup was incomplete"
        )
    body = {
        "runId": args.run_id,
        "planId": plan["planId"],
        "archiveSha256": manifest["archive"]["sha256"],
        "restoredFingerprint": restored_fingerprint,
        "restoredRetainedFingerprint": (
            restored_retained_fingerprint
        ),
        "restoredCatalog": restored_catalog,
        "restoredSnapshotDistribution": restored_distribution,
        "exactRows": {
            "total": exact_total_rows,
            "retained": plan["planIdentity"]["exactRetainedRows"],
            "purge": exact_purge_rows,
        },
        "restoreElapsedSeconds": round(restore_elapsed, 3),
        "restorePeakBytes": restore_bytes,
        "cleanupProof": cleanup_proof,
        "isolated": {
            "container": container,
            "image": image,
            "network": "none",
            "containerRemoved": True,
        },
    }
    return write_stage_report(args.scratch_root, "drill", body)


def calculate_capacity(
    plan,
    profile,
    free_bytes,
    exact_total_rows,
):
    retained_rows = ensure_integer(
        plan["planIdentity"]["exactRetainedRows"],
        "exactRetainedRows",
        1,
    )
    total_rows = ensure_integer(
        exact_total_rows,
        "exactTotalRows",
        1,
    )
    current_heap = ensure_integer(
        plan["catalog"]["heapBytes"],
        "current heap bytes",
        1,
    )
    current_indexes = ensure_integer(
        plan["catalog"]["indexBytes"],
        "current index bytes",
        1,
    )
    retained_ratio = retained_rows / total_rows
    replacement_heap_ratio = float(
        profile["replacementHeapToSourceRetainedRatio"]
    )
    replacement_index_ratio = float(
        profile["replacementIndexToSourceRetainedRatio"]
    )
    wal_ratio = float(profile["walToReplacementRatio"])
    temp_ratio = float(profile["tempToReplacementRatio"])
    failure_ratio = float(profile["failureReserveRatio"])
    for label, value in (
        ("replacementHeapToSourceRetainedRatio", replacement_heap_ratio),
        ("replacementIndexToSourceRetainedRatio", replacement_index_ratio),
        ("walToReplacementRatio", wal_ratio),
        ("tempToReplacementRatio", temp_ratio),
        ("failureReserveRatio", failure_ratio),
    ):
        if value < 0 or value > 10:
            raise PilotError(f"unsafe measured profile ratio {label}")
    source_retained_heap = int(current_heap * retained_ratio)
    source_retained_indexes = int(current_indexes * retained_ratio)
    replacement_heap = int(
        source_retained_heap * replacement_heap_ratio
    )
    replacement_indexes = int(
        source_retained_indexes * replacement_index_ratio
    )
    replacement_total = replacement_heap + replacement_indexes
    wal = int(replacement_total * wal_ratio)
    temp = int(replacement_total * temp_ratio)
    failure_reserve = int(replacement_total * failure_ratio)
    transient = (
        replacement_total + wal + temp + failure_reserve
    )
    required_free = EMERGENCY_FLOOR_BYTES + transient
    return {
        "profileId": profile.get("profileId"),
        "measured": True,
        "retainedRowRatio": retained_ratio,
        "estimatedReplacementHeapBytes": replacement_heap,
        "estimatedReplacementIndexBytes": replacement_indexes,
        "estimatedWalBytes": wal,
        "estimatedTempBytes": temp,
        "failureReserveBytes": failure_reserve,
        "transientPeakBytes": transient,
        "emergencyFloorBytes": EMERGENCY_FLOOR_BYTES,
        "requiredFreeBytes": required_free,
        "observedFreeBytes": free_bytes,
        "marginBytes": free_bytes - required_free,
        "allowed": free_bytes >= required_free,
    }


def calculate_scratch_capacity(
    plan,
    profile,
    fst_free_bytes,
    scratch_free_bytes,
    exact_total_rows,
):
    measured = calculate_capacity(
        plan,
        profile,
        fst_free_bytes,
        exact_total_rows,
    )
    wal_budget = max(
        int(measured["estimatedWalBytes"] * 1.25),
        measured["estimatedWalBytes"] + 512 * 1024**2,
    )
    fst_required = EMERGENCY_FLOOR_BYTES + wal_budget
    scratch_reserve = 10 * 1024**3
    scratch_required = (
        measured["estimatedReplacementHeapBytes"]
        + measured["estimatedReplacementIndexBytes"]
        + measured["estimatedTempBytes"]
        + measured["failureReserveBytes"]
        + scratch_reserve
    )
    return {
        **measured,
        "mode": "temporary_scratch_tablespace",
        "walBudgetBytes": wal_budget,
        "fstRequiredFreeBytes": fst_required,
        "fstObservedFreeBytes": fst_free_bytes,
        "fstMarginBytes": fst_free_bytes - fst_required,
        "scratchReserveBytes": scratch_reserve,
        "scratchRequiredFreeBytes": scratch_required,
        "scratchObservedFreeBytes": scratch_free_bytes,
        "scratchMarginBytes": scratch_free_bytes - scratch_required,
        "requiredFreeBytes": fst_required,
        "observedFreeBytes": fst_free_bytes,
        "marginBytes": fst_free_bytes - fst_required,
        "allowed": (
            fst_free_bytes >= fst_required
            and scratch_free_bytes >= scratch_required
        ),
    }


def calculate_repatriation_capacity(build, free_bytes):
    sizes = build["sizes"]
    replacement_bytes = ensure_integer(
        sizes["totalBytes"],
        "built replacement bytes",
        1,
    )
    observed_wal = ensure_integer(
        sizes["walBytes"],
        "built replacement WAL bytes",
        0,
    )
    wal_budget = max(
        int(observed_wal * 1.25),
        observed_wal + 512 * 1024**2,
    )
    required = (
        EMERGENCY_FLOOR_BYTES
        + replacement_bytes
        + wal_budget
    )
    return {
        "mode": "pre_drop_pg_default_repatriation",
        "measuredReplacementBytes": replacement_bytes,
        "measuredWalBytes": observed_wal,
        "walBudgetBytes": wal_budget,
        "emergencyFloorBytes": EMERGENCY_FLOOR_BYTES,
        "requiredFreeBytes": required,
        "observedFreeBytes": free_bytes,
        "marginBytes": free_bytes - required,
        "allowed": free_bytes >= required,
    }


def verify_profile(path, expected_sha256, *, allow_seed=False):
    if not path or not expected_sha256:
        raise PilotError(
            "build requires --measured-profile and "
            "--expected-profile-sha256"
        )
    path = pathlib.Path(path)
    observed = sha256_path(path)
    if observed != expected_sha256:
        raise PilotError(
            "measured profile checksum differs from the expected value"
        )
    profile = read_json(path)
    if profile.get("toolId") != TOOL_ID:
        raise PilotError("measured profile belongs to another tool")
    if not profile.get("isolatedPg17DrillPassed"):
        raise PilotError("measured profile lacks a passed PG17 drill")
    if not profile.get("promotionEligible") and not allow_seed:
        raise PilotError(
            "measured profile is not eligible for production capacity "
            "planning"
        )
    scale = profile.get("scale") or {}
    if not allow_seed and (
        scale.get("totalRows", 0) < 100_000
        or scale.get("retainedRows", 0) < 10_000
    ):
        raise PilotError(
            "measured profile synthetic scale is too small for "
            "production planning"
        )
    return profile, observed


def advisory_lock_guard_sql():
    return f"""
        DO $guard$
        BEGIN
            IF NOT pg_try_advisory_xact_lock(
                {MAINTENANCE_ADVISORY_LOCK_KEY})
               OR NOT pg_try_advisory_xact_lock(
                    {PUBLICATION_ADVISORY_LOCK_KEY})
            THEN
                RAISE EXCEPTION
                    'maintenance advisory lock unavailable';
            END IF;
        END
        $guard$;
    """


def assert_no_emergency_breach(root):
    path = (
        pathlib.Path(root)
        / REPORTS_DIR
        / "emergency-floor-breach.json"
    )
    if path.exists():
        raise PilotError(
            "workspace recorded an emergency-floor breach; use a new "
            "run after reconciling PostgreSQL WAL and filesystem state"
        )


def cancel_pilot_backends(
    runner,
    args,
    free_bytes,
    threshold_bytes,
    filesystem,
):
    breach_path = (
        pathlib.Path(args.scratch_root)
        / REPORTS_DIR
        / "emergency-floor-breach.json"
    )
    if not breach_path.exists():
        write_json_exclusive(
            breach_path,
            {
                "formatVersion": FORMAT_VERSION,
                "toolId": TOOL_ID,
                "status": "blocked",
                "recordedAtUtc": utc_now(),
                "freeBytes": free_bytes,
                "thresholdBytes": threshold_bytes,
                "filesystem": filesystem,
                "applicationName": APPLICATION_NAME,
                "resumeAllowed": False,
            },
        )

    def backend_count():
        output = runner.run(
            [
                "docker",
                "exec",
                args.pg_container,
                "psql",
                "-X",
                "-v",
                "ON_ERROR_STOP=1",
                "-U",
                args.pg_user,
                "-d",
                args.pg_database,
                "-At",
                "-c",
                (
                    "SELECT COUNT(*) FROM pg_stat_activity "
                    "WHERE pid <> pg_backend_pid() "
                    "AND application_name = "
                    f"{sql_literal(APPLICATION_NAME)}"
                ),
            ],
            timeout=30,
        ).stdout.strip()
        try:
            parsed = int(output)
        except ValueError as error:
            raise PilotError(
                "remaining pilot backend count is not an integer"
            ) from error
        return ensure_integer(
            parsed,
            "remaining pilot backend count",
            0,
        )

    for attempt in range(10):
        if backend_count() == 0:
            return True
        function = (
            "pg_cancel_backend"
            if attempt < 4
            else "pg_terminate_backend"
        )
        output = runner.run(
            [
                "docker",
                "exec",
                args.pg_container,
                "psql",
                "-X",
                "-v",
                "ON_ERROR_STOP=1",
                "-U",
                args.pg_user,
                "-d",
                args.pg_database,
                "-At",
                "-c",
                (
                    f"SELECT COUNT(*) FILTER (WHERE {function}(pid)) "
                    "FROM pg_stat_activity "
                    "WHERE pid <> pg_backend_pid() "
                    "AND application_name = "
                    f"{sql_literal(APPLICATION_NAME)}"
                ),
            ],
            timeout=30,
        ).stdout.strip()
        try:
            parsed = int(output)
        except ValueError as error:
            raise PilotError(
                "cancelled pilot backend count is not an integer"
            ) from error
        ensure_integer(
            parsed,
            "cancelled pilot backend count",
            0,
        )
        time.sleep(0.5)
    if backend_count() != 0:
        raise PilotError(
            "pilot backends remain after emergency cancellation"
        )
    return True


def stage_build(args, runner):
    if not args.execute:
        raise PilotError("build requires --execute")
    assert_no_emergency_breach(args.scratch_root)
    check = load_report(args.scratch_root, "check")
    plan = load_report(args.scratch_root, "plan")
    drill = load_report(args.scratch_root, "drill")
    host = collect_host_guard(args, runner)
    database = Database(
        runner,
        args.pg_container,
        args.pg_user,
        args.pg_database,
    )
    guard = collect_database_guard(database)
    assert_identity_matches(check, host, guard)
    assert_plan_target_matches(plan, guard)
    profile, profile_sha = verify_profile(
        args.measured_profile,
        args.expected_profile_sha256,
        allow_seed=args.test_mode,
    )
    replacement = replacement_name(args.run_id)
    primary_name = replacement_primary_name(args.run_id)
    score_name = replacement_score_name(args.run_id)
    instrument_check_name = replacement_instrument_check_name(
        args.run_id
    )
    existing = database.scalar(
        "SELECT to_regclass("
        f"{sql_literal('public.' + replacement)}) "
        "IS NOT NULL"
    )
    resumed_existing_build = existing == "t"
    build_started_path = (
        pathlib.Path(args.scratch_root)
        / REPORTS_DIR
        / "build.started.json"
    )
    build_started = (
        read_json(build_started_path)
        if build_started_path.exists()
        else None
    )
    if build_started is not None and (
        build_started.get("toolId") != TOOL_ID
        or build_started.get("runId") != args.run_id
        or build_started.get("planId") != plan["planId"]
        or build_started.get("profileSha256") != profile_sha
    ):
        raise PilotError(
            "existing build-start evidence differs from this plan"
        )
    if resumed_existing_build and build_started is None:
        raise PilotError(
            "replacement exists without immutable build-start evidence"
        )
    scratch_mount = collect_tablespace_mount(args, runner)
    scratch_free_before = shutil.disk_usage(
        args.scratch_root
    ).free
    observed_free_before = host["dataFilesystem"]["freeBytes"]
    current_capacity = (
        calculate_scratch_capacity(
            plan,
            profile,
            observed_free_before,
            scratch_free_before,
            drill["exactRows"]["total"],
        )
        if scratch_mount["enabled"]
        else calculate_capacity(
            plan,
            profile,
            observed_free_before,
            drill["exactRows"]["total"],
        )
    )
    capacity = (
        build_started["capacityGate"]
        if resumed_existing_build
        else current_capacity
    )
    if not capacity["allowed"]:
        raise PilotError(
            "pilot-specific measured capacity gate failed: "
            f"need {capacity['requiredFreeBytes']} bytes, "
            f"observed {capacity['observedFreeBytes']} bytes"
        )
    build_storage = prepare_scratch_tablespace(
        args,
        runner,
        database,
    )
    build_tablespace = build_storage["tablespace"]
    if build_started is not None and (
        build_started.get("buildTablespace") != build_tablespace
    ):
        raise PilotError(
            "existing build-start evidence names another tablespace"
        )
    source_relation_state = database.json(
        relation_state_query(TARGET_PARTITION)
    )
    if (
        physical_relation_identity(source_relation_state)
        != plan["planIdentity"]["sourceRelationIdentity"]
    ):
        raise PilotError(
            "source partition changed before replacement build"
        )
    source_retained_fingerprint = database.json(
        fingerprint_sql(
            TARGET_PARTITION,
            snapshot_id_predicate(
                plan["planIdentity"]["keepSnapshotIds"]
            ),
        ),
        timeout=args.query_timeout_seconds,
        pgoptions=PLAN_QUERY_PGOPTIONS,
    )
    if (
        source_retained_fingerprint
        != plan["planIdentity"]["retainedFingerprint"]
    ):
        raise PilotError(
            "protected source rows changed before replacement build"
        )
    keep_ids = plan["planIdentity"]["keepSnapshotIds"]
    keep_array = ", ".join(str(value) for value in keep_ids)
    owner = plan["catalog"]["owner"]
    if not re.fullmatch(r"[A-Za-z_][A-Za-z0-9_$]*", owner):
        raise PilotError(f"unsafe target owner name: {owner}")
    if build_started is None:
        build_started = {
            "formatVersion": FORMAT_VERSION,
            "toolId": TOOL_ID,
            "stage": "build",
            "status": "started",
            "runId": args.run_id,
            "planId": plan["planId"],
            "startedAtUtc": utc_now(),
            "startedLsn": database.scalar(
                "SELECT pg_current_wal_lsn()"
            ),
            "tempBytesBefore": int(
                database.scalar(
                    "SELECT temp_bytes FROM pg_stat_database "
                    "WHERE datname = current_database()"
                )
            ),
            "freeBytesBefore": observed_free_before,
            "profileSha256": profile_sha,
            "capacityGate": capacity,
            "buildTablespace": build_tablespace,
            "scratchMount": build_storage,
        }
        write_json_exclusive(build_started_path, build_started)
    if resumed_existing_build:
        attempt_path = build_started_path
        attempt = build_started
    else:
        attempt_path = (
            pathlib.Path(args.scratch_root)
            / REPORTS_DIR
            / (
                "build.attempt-"
                + datetime.now(timezone.utc).strftime(
                    "%Y%m%dT%H%M%S%fZ"
                )
                + ".json"
            )
        )
        attempt = {
            "formatVersion": FORMAT_VERSION,
            "toolId": TOOL_ID,
            "stage": "build",
            "status": "attempting",
            "runId": args.run_id,
            "planId": plan["planId"],
            "startedAtUtc": utc_now(),
            "startedLsn": database.scalar(
                "SELECT pg_current_wal_lsn()"
            ),
            "tempBytesBefore": int(
                database.scalar(
                    "SELECT temp_bytes FROM pg_stat_database "
                    "WHERE datname = current_database()"
                )
            ),
            "freeBytesBefore": observed_free_before,
            "scratchFreeBytesBefore": scratch_free_before,
            "capacityGate": current_capacity,
        }
        write_json_exclusive(attempt_path, attempt)
    started_lsn = attempt["startedLsn"]
    temp_bytes_before = attempt["tempBytesBefore"]
    free_before = attempt["freeBytesBefore"]
    started_wall = datetime.fromisoformat(
        attempt["startedAtUtc"].replace("Z", "+00:00")
    )
    started = time.monotonic()
    sql = f"""
        BEGIN;
        SET LOCAL lock_timeout = '2s';
        SET LOCAL statement_timeout = '0';
        SET LOCAL temp_tablespaces =
            {sql_literal(build_tablespace)};
        {advisory_lock_guard_sql()}
        CREATE TABLE {qualified(replacement)}
            (LIKE {qualified(TARGET_PARTITION)}
                INCLUDING DEFAULTS
                INCLUDING CONSTRAINTS
                INCLUDING STORAGE
                INCLUDING GENERATED
                INCLUDING IDENTITY)
            TABLESPACE "{build_tablespace}";
        ALTER TABLE {qualified(replacement)}
            ADD CONSTRAINT "{instrument_check_name}"
            CHECK (
                instrument =
                    {sql_literal(TARGET_INSTRUMENT)});
        ALTER TABLE {qualified(replacement)}
            OWNER TO "{owner}";
        INSERT INTO {qualified(replacement)}
        SELECT *
        FROM {qualified(TARGET_PARTITION)}
        WHERE snapshot_id = ANY(
            ARRAY[{keep_array}]::bigint[]);
        CREATE UNIQUE INDEX "{primary_name}"
            ON {qualified(replacement)}
            (snapshot_id, song_id, instrument, account_id)
            TABLESPACE "{build_tablespace}";
        ALTER TABLE {qualified(replacement)}
            ADD CONSTRAINT "{primary_name}"
            PRIMARY KEY USING INDEX "{primary_name}";
        CREATE INDEX "{score_name}"
            ON {qualified(replacement)}
            (snapshot_id, song_id, instrument, score DESC)
            TABLESPACE "{build_tablespace}";
        ANALYZE {qualified(replacement)};
        COMMIT;
    """
    filesystem_monitor = FilesystemMonitor(
        host["dataPath"],
        minimum_allowed_bytes=(
            EMERGENCY_FLOOR_BYTES + 512 * 1024**2
        ),
        on_breach=lambda free: cancel_pilot_backends(
            runner,
            args,
            free,
            EMERGENCY_FLOOR_BYTES + 512 * 1024**2,
            "fst",
        ),
    )
    scratch_monitor = FilesystemMonitor(
        args.scratch_root,
        minimum_allowed_bytes=(
            current_capacity.get("scratchReserveBytes")
            if scratch_mount["enabled"]
            else None
        ),
        on_breach=lambda free: cancel_pilot_backends(
            runner,
            args,
            free,
            current_capacity.get("scratchReserveBytes"),
            "scratch",
        ),
    )
    try:
        with filesystem_monitor, scratch_monitor:
            if not resumed_existing_build:
                database.psql(
                    sql,
                    timeout=args.build_timeout_seconds,
                )
    except CommandError as error:
        if filesystem_monitor.breached or scratch_monitor.breached:
            raise PilotError(
                "replacement build was cancelled before breaching a "
                "filesystem reserve"
            ) from error
        raise
    if filesystem_monitor.breached:
        raise PilotError(
            "replacement build crossed the emergency cancellation "
            "threshold"
        )
    if scratch_monitor.breached:
        raise PilotError(
            "replacement build crossed the scratch reserve "
            "cancellation threshold"
        )
    invocation_elapsed = time.monotonic() - started
    wall_elapsed = (
        datetime.now(timezone.utc) - started_wall
    ).total_seconds()
    completed_lsn = database.scalar("SELECT pg_current_wal_lsn()")
    temp_bytes_after = int(
        database.scalar(
            "SELECT temp_bytes FROM pg_stat_database "
            "WHERE datname = current_database()"
        )
    )
    sizes = database.json(
        f"""
            SELECT json_build_object(
                'heapBytes', pg_relation_size(
                    'public.{replacement}'),
                'indexBytes', pg_indexes_size(
                    'public.{replacement}'),
                'totalBytes', pg_total_relation_size(
                    'public.{replacement}'),
                'walBytes', pg_wal_lsn_diff(
                    {sql_literal(completed_lsn)}::pg_lsn,
                    {sql_literal(started_lsn)}::pg_lsn)::bigint,
                'tempBytes',
                    {max(0, temp_bytes_after - temp_bytes_before)}::bigint,
                'tablespace', (
                    SELECT COALESCE(
                        tablespace.spcname, 'pg_default')
                    FROM pg_class relation
                    LEFT JOIN pg_tablespace tablespace
                      ON tablespace.oid = relation.reltablespace
                    WHERE relation.oid =
                        'public.{replacement}'::regclass))
        """
    )
    if sizes["tablespace"] != build_tablespace:
        raise PilotError(
            "replacement was built in an unexpected tablespace"
        )
    built_fingerprint = database.json(
        fingerprint_sql(replacement),
        timeout=args.query_timeout_seconds,
    )
    if (
        built_fingerprint
        != plan["planIdentity"]["retainedFingerprint"]
    ):
        raise PilotError(
            "replacement fingerprint differs from the exact retained set"
        )
    host_after = collect_host_guard(args, runner)
    body = {
        "runId": args.run_id,
        "planId": plan["planId"],
        "replacementRelation": (
            f"{TARGET_SCHEMA}.{replacement}"
        ),
        "fingerprint": built_fingerprint,
        "sizes": sizes,
        "elapsedSeconds": round(wall_elapsed, 3),
        "invocationElapsedSeconds": round(
            invocation_elapsed,
            3,
        ),
        "freeBytesBefore": free_before,
        "freeBytesAfter": host_after["dataFilesystem"][
            "freeBytes"
        ],
        "observedFilesystemGrowthBytes": max(
            0,
            free_before
            - host_after["dataFilesystem"]["freeBytes"],
        ),
        "observedPeakFilesystemGrowthBytes": max(
            0,
            free_before - filesystem_monitor.minimum_free_bytes,
        ),
        "scratchFreeBytesBefore": scratch_free_before,
        "scratchFreeBytesAfter": shutil.disk_usage(
            args.scratch_root
        ).free,
        "observedScratchGrowthBytes": max(
            0,
            scratch_free_before
            - shutil.disk_usage(args.scratch_root).free,
        ),
        "observedPeakScratchGrowthBytes": max(
            0,
            scratch_free_before
            - scratch_monitor.minimum_free_bytes,
        ),
        "filesystemMonitorSamples": filesystem_monitor.samples,
        "filesystemEmergencyThresholdBytes": (
            filesystem_monitor.minimum_allowed_bytes
        ),
        "filesystemEmergencyThresholdBreached": (
            filesystem_monitor.breached
        ),
        "filesystemEmergencyCancellationHandled": (
            filesystem_monitor.breach_handled
        ),
        "filesystemEmergencyCancellationError": (
            filesystem_monitor.breach_error
        ),
        "scratchMonitorSamples": scratch_monitor.samples,
        "scratchReserveThresholdBytes": (
            scratch_monitor.minimum_allowed_bytes
        ),
        "scratchReserveThresholdBreached": (
            scratch_monitor.breached
        ),
        "scratchReserveCancellationHandled": (
            scratch_monitor.breach_handled
        ),
        "scratchReserveCancellationError": (
            scratch_monitor.breach_error
        ),
        "filesystemPeakMeasurementComplete": (
            not resumed_existing_build
        ),
        "resourceMeasurementComplete": True,
        "buildStartedEvidence": {
            "path": str(build_started_path),
            "sha256": sha256_path(build_started_path),
        },
        "buildAttemptEvidence": {
            "path": str(attempt_path),
            "sha256": sha256_path(attempt_path),
        },
        "capacityGate": capacity,
        "buildStorage": build_storage,
        "measuredProfile": {
            "path": str(args.measured_profile),
            "sha256": profile_sha,
        },
        "oldPartitionStillAttached": True,
        "resumedExistingAtomicBuild": resumed_existing_build,
        "sourceRetainedFingerprint": source_retained_fingerprint,
        "sourceRelationState": source_relation_state,
    }
    return write_stage_report(args.scratch_root, "build", body)


def stage_swap(args, runner):
    if not args.execute:
        raise PilotError("swap requires --execute")
    check = load_report(args.scratch_root, "check")
    plan = load_report(args.scratch_root, "plan")
    build = load_report(args.scratch_root, "build")
    archive = load_report(args.scratch_root, "archive")
    load_report(args.scratch_root, "drill")
    host = collect_host_guard(args, runner)
    database = Database(
        runner,
        args.pg_container,
        args.pg_user,
        args.pg_database,
    )
    guard = collect_database_guard(database)
    assert_identity_matches(check, host, guard)
    replacement = replacement_name(args.run_id)
    retired = retired_name(args.run_id)
    probe = database.json(
        f"""
            SELECT json_build_object(
                'targetAttached', EXISTS (
                    SELECT 1 FROM pg_inherits
                    WHERE inhparent =
                        'public.{TARGET_PARENT}'::regclass
                      AND inhrelid =
                        'public.{TARGET_PARTITION}'::regclass),
                'replacementExists',
                    to_regclass(
                        'public.{replacement}') IS NOT NULL,
                'retiredExists',
                    to_regclass(
                        'public.{retired}') IS NOT NULL)
        """
    )
    expected_pre_swap = {
        "targetAttached": True,
        "replacementExists": True,
        "retiredExists": False,
    }
    expected_post_swap = {
        "targetAttached": True,
        "replacementExists": False,
        "retiredExists": True,
    }
    if probe not in (expected_pre_swap, expected_post_swap):
        raise PilotError(
            f"unexpected pre-swap relation state: {probe}"
        )
    if probe == expected_pre_swap:
        assert_plan_target_matches(plan, guard)
    started = time.monotonic()
    sql = f"""
        BEGIN;
        SET LOCAL lock_timeout = '2s';
        SET LOCAL statement_timeout = '30s';
        {advisory_lock_guard_sql()}
        LOCK TABLE {qualified(TARGET_PARENT)}
            IN ACCESS EXCLUSIVE MODE;
        ALTER TABLE {qualified(TARGET_PARENT)}
            DETACH PARTITION {qualified(TARGET_PARTITION)};
        ALTER TABLE {qualified(TARGET_PARTITION)}
            RENAME TO "{retired}";
        ALTER TABLE {qualified(replacement)}
            RENAME TO "{TARGET_PARTITION}";
        ALTER TABLE {qualified(TARGET_PARENT)}
            ATTACH PARTITION {qualified(TARGET_PARTITION)}
            FOR VALUES IN (
                {sql_literal(TARGET_INSTRUMENT)});
        COMMIT;
    """
    resumed_committed_swap = probe == expected_post_swap
    if not resumed_committed_swap:
        database.psql(sql, timeout=60)
    elapsed = time.monotonic() - started
    if elapsed > args.maximum_swap_seconds:
        raise PilotError(
            "swap committed but exceeded its approved wall-clock bound; "
            "validate or roll back before any drop"
        )
    post = database.json(
        f"""
            SELECT json_build_object(
                'targetAttached', EXISTS (
                    SELECT 1 FROM pg_inherits
                    WHERE inhparent =
                        'public.{TARGET_PARENT}'::regclass
                      AND inhrelid =
                        'public.{TARGET_PARTITION}'::regclass),
                'retiredExists',
                    to_regclass(
                        'public.{retired}') IS NOT NULL,
                'retiredAttached', EXISTS (
                    SELECT 1 FROM pg_inherits
                    WHERE inhrelid =
                        to_regclass(
                            'public.{retired}')),
                'targetFingerprint', (
                    {fingerprint_sql(TARGET_PARTITION)}
                ))
        """,
        timeout=args.query_timeout_seconds,
    )
    if not post["targetAttached"]:
        raise PilotError("replacement is not attached after swap")
    if not post["retiredExists"] or post["retiredAttached"]:
        raise PilotError(
            "original relation is not retained detached after swap"
        )
    if (
        post["targetFingerprint"]
        != plan["planIdentity"]["retainedFingerprint"]
    ):
        raise PilotError(
            "attached replacement differs from retained fingerprint"
        )
    body = {
        "runId": args.run_id,
        "planId": plan["planId"],
        "elapsedSeconds": round(elapsed, 3),
        "maximumSwapSeconds": args.maximum_swap_seconds,
        "postSwap": post,
        "originalRetainedRelation": (
            f"{TARGET_SCHEMA}.{retired}"
        ),
        "rollbackAvailable": True,
        "dropPerformed": False,
        "resumedCommittedSwap": resumed_committed_swap,
        "buildReportSha256": sha256_path(
            report_path(args.scratch_root, "build")
        ),
        "buildSizes": build["sizes"],
    }
    return write_stage_report(args.scratch_root, "swap", body)


def capture_api_fingerprints(base_url, timeout=20):
    routes = (
        ("/readyz", "health"),
        ("/api/service-info", "health"),
        ("/api/songs", "exact"),
        ("/api/rankings/overview", "exact"),
    )
    rows = []
    for route, comparison in routes:
        request = urllib.request.Request(
            base_url.rstrip("/") + route,
            headers={"Accept": "application/json"},
        )
        with urllib.request.urlopen(
            request,
            timeout=timeout,
        ) as response:
            body = response.read()
            rows.append(
                {
                    "route": route,
                    "comparison": comparison,
                    "status": response.status,
                    "contentType": response.headers.get(
                        "Content-Type"
                    ),
                    "etag": response.headers.get("ETag"),
                    "bytes": len(body),
                    "sha256": sha256_bytes(body),
                }
            )
    return rows


def compare_api_snapshots(baseline, observed):
    baseline_by_route = {
        row["route"]: row for row in baseline
    }
    observed_by_route = {
        row["route"]: row for row in observed
    }
    if set(baseline_by_route) != set(observed_by_route):
        return False
    for route, expected in baseline_by_route.items():
        actual = observed_by_route[route]
        if (
            expected.get("comparison")
            != actual.get("comparison")
            or expected.get("status") != 200
            or actual.get("status") != 200
        ):
            return False
        if expected.get("comparison") == "exact":
            if expected != actual:
                return False
        elif expected.get("contentType") != actual.get("contentType"):
            return False
    return True


def reference_parity_query(relation_name):
    return f"""
        WITH named_publications AS (
            SELECT generation.scrape_id
            FROM scrape_publication_state state
            CROSS JOIN LATERAL unnest(ARRAY[
                state.current_publication_id,
                state.previous_publication_id,
                state.working_publication_id
            ]) selected(publication_id)
            JOIN publication_generations generation
              ON generation.publication_id =
                    selected.publication_id
            WHERE state.id = TRUE
              AND selected.publication_id IS NOT NULL
        ),
        required_sources AS (
            SELECT DISTINCT source.source_snapshot_id
            FROM leaderboard_published_scope_source source
            WHERE source.published_scrape_id IN (
                    SELECT scrape_id FROM named_publications)
              AND source.instrument =
                    {sql_literal(TARGET_INSTRUMENT)}
              AND source.source_kind = 'snapshot'
        ),
        protected_sources AS (
            SELECT source_snapshot_id AS snapshot_id
            FROM required_sources
            UNION
            SELECT active_snapshot_id
            FROM leaderboard_snapshot_state
            WHERE instrument =
                {sql_literal(TARGET_INSTRUMENT)}
              AND active_snapshot_id IS NOT NULL
            UNION
            SELECT source_snapshot_id
            FROM solo_current_projection_scope
            WHERE instrument =
                {sql_literal(TARGET_INSTRUMENT)}
              AND source_snapshot_id IS NOT NULL
        )
        SELECT json_build_object(
            'publication', (
                SELECT json_build_object(
                    'publishedScrapeId', published_scrape_id,
                    'currentPublicationId',
                        current_publication_id,
                    'previousPublicationId',
                        previous_publication_id,
                    'workingPublicationId',
                        working_publication_id,
                    'publicReadsFrozen',
                        public_reads_frozen)
                FROM scrape_publication_state
                WHERE id = TRUE),
            'missingRequiredSourceIds', (
                SELECT COALESCE(
                    json_agg(source_snapshot_id),
                    '[]'::json)
                FROM required_sources source
                WHERE NOT EXISTS (
                    SELECT 1
                    FROM {qualified(relation_name)} snapshot
                    WHERE snapshot.snapshot_id =
                        source.source_snapshot_id)),
            'activeStateMissingRows', (
                SELECT COUNT(*)::bigint
                FROM leaderboard_snapshot_state state
                WHERE state.instrument =
                        {sql_literal(TARGET_INSTRUMENT)}
                  AND state.active_snapshot_id IS NOT NULL
                  AND NOT EXISTS (
                      SELECT 1
                      FROM {qualified(relation_name)} snapshot
                      WHERE snapshot.snapshot_id =
                            state.active_snapshot_id
                        AND snapshot.song_id =
                            state.song_id)),
            'projectionMissingRows', (
                SELECT COUNT(*)::bigint
                FROM solo_current_projection_scope scope
                WHERE scope.instrument =
                        {sql_literal(TARGET_INSTRUMENT)}
                  AND scope.source_snapshot_id IS NOT NULL
                  AND NOT EXISTS (
                      SELECT 1
                      FROM {qualified(relation_name)} snapshot
                      WHERE snapshot.snapshot_id =
                            scope.source_snapshot_id
                        AND snapshot.song_id =
                            scope.song_id)),
            'sourceMapRows', (
                SELECT COUNT(*)::bigint
                FROM leaderboard_published_scope_source
                WHERE instrument =
                    {sql_literal(TARGET_INSTRUMENT)}),
            'maxScoreFingerprint', (
                SELECT md5(
                    concat_ws('|',
                        COUNT(*)::text,
                        COALESCE(MIN(score), 0)::text,
                        COALESCE(MAX(score), 0)::text,
                        COALESCE(
                            SUM(score::numeric), 0)::text))
                FROM {qualified(relation_name)}
                WHERE snapshot_id IN (
                    SELECT snapshot_id
                    FROM protected_sources)),
            'referenceCount', (
                SELECT COUNT(*)::bigint
                FROM (
                    SELECT active_snapshot_id AS snapshot_id
                    FROM leaderboard_snapshot_state
                    WHERE instrument =
                        {sql_literal(TARGET_INSTRUMENT)}
                    UNION
                    SELECT source_snapshot_id
                    FROM solo_current_projection_scope
                    WHERE instrument =
                        {sql_literal(TARGET_INSTRUMENT)}
                    UNION
                    SELECT source_snapshot_id
                    FROM leaderboard_published_scope_source
                    WHERE instrument =
                        {sql_literal(TARGET_INSTRUMENT)}
                ) source_references
                WHERE snapshot_id IS NOT NULL)
        )
    """


def stage_validate(args, runner):
    check = load_report(args.scratch_root, "check")
    plan = load_report(args.scratch_root, "plan")
    archive = load_report(args.scratch_root, "archive")
    drill = load_report(args.scratch_root, "drill")
    load_report(args.scratch_root, "swap")
    host = collect_host_guard(args, runner)
    database = Database(
        runner,
        args.pg_container,
        args.pg_user,
        args.pg_database,
    )
    guard = collect_database_guard(database)
    assert_identity_matches(check, host, guard)
    retired = retired_name(args.run_id)
    original_relation_state = database.json(
        relation_state_query(retired)
    )
    if (
        physical_relation_identity(original_relation_state)
        != plan["planIdentity"]["sourceRelationIdentity"]
    ):
        raise PilotError(
            "retained original relation changed after the swap"
        )
    original_retained_fingerprint = database.json(
        fingerprint_sql(
            retired,
            snapshot_id_predicate(
                plan["planIdentity"]["keepSnapshotIds"]
            ),
        ),
        timeout=args.query_timeout_seconds,
        pgoptions=PLAN_QUERY_PGOPTIONS,
    )
    candidate_fingerprint = database.json(
        fingerprint_sql(TARGET_PARTITION),
        timeout=args.query_timeout_seconds,
    )
    if (
        original_retained_fingerprint
        != plan["planIdentity"]["retainedFingerprint"]
    ):
        raise PilotError(
            "protected rows in the retained original differ from the plan"
        )
    if (
        candidate_fingerprint
        != plan["planIdentity"]["retainedFingerprint"]
    ):
        raise PilotError(
            "attached replacement differs from retained rows"
        )
    reference_parity = database.json(
        reference_parity_query(TARGET_PARTITION),
        timeout=args.query_timeout_seconds,
    )
    if reference_parity["missingRequiredSourceIds"]:
        raise PilotError(
            "published source IDs are missing after swap"
        )
    if reference_parity["activeStateMissingRows"] != 0:
        raise PilotError(
            "active snapshot state references missing rows"
        )
    if reference_parity["projectionMissingRows"] != 0:
        raise PilotError(
            "current projection references missing rows"
        )
    baseline_reference = plan["planIdentity"]["referenceParity"]
    for key in (
        "sourceMapRows",
        "maxScoreFingerprint",
        "referenceCount",
    ):
        if reference_parity[key] != baseline_reference[key]:
            raise PilotError(
                f"{key} changed across the partition swap"
            )
    expected_publication = check["publicationFence"]
    observed_publication = {
        key: reference_parity["publication"][key]
        for key in expected_publication
    }
    if observed_publication != expected_publication:
        raise PilotError(
            "publication changed during rewrite validation"
        )
    api = (
        capture_api_fingerprints(args.api_base)
        if args.api_base
        else []
    )
    if api and any(row["status"] != 200 for row in api):
        raise PilotError("a representative public API route is unhealthy")
    if not compare_api_snapshots(
        check.get("publicApiBaseline", []),
        api,
    ):
        raise PilotError(
            "representative public API body/header fingerprints changed"
        )
    if archive.get("adoptedVerifiedArchive"):
        verified, _ = load_verified_archive_input(
            args,
            runner,
            check,
            source_fence_from_relation_state(
                original_relation_state
            ),
            verify_archive_checksum=True,
        )
        archive_path = pathlib.Path(verified["archive"]["path"])
        archive_sha = verified["archive"]["sha256"]
        archived_whole_fingerprint = None
    else:
        manifest = read_json(
            pathlib.Path(args.scratch_root)
            / ARCHIVE_DIR
            / "manifest.json"
        )
        archive_path = pathlib.Path(manifest["archive"]["path"])
        archive_sha = manifest["archive"]["sha256"]
        if sha256_path(archive_path) != archive_sha:
            raise PilotError(
                "archive checksum changed during validation"
            )
        archived_whole_fingerprint = drill[
            "restoredFingerprint"
        ]
    body = {
        "runId": args.run_id,
        "planId": plan["planId"],
        "accepted": True,
        "candidateFingerprint": candidate_fingerprint,
        "retainedOriginalFingerprint": (
            original_retained_fingerprint
        ),
        "retainedOriginalRelationState": original_relation_state,
        "archivedWholeFingerprint": archived_whole_fingerprint,
        "archivedExactRows": drill["exactRows"],
        "referenceParity": reference_parity,
        "publicApi": api,
        "archive": {
            "path": str(archive_path),
            "sha256": archive_sha,
            "present": archive_path.is_file(),
        },
        "oldRelationStillPresent": True,
        "rollbackStillAvailable": True,
        "archiveReportSha256": sha256_path(
            report_path(args.scratch_root, "archive")
        ),
    }
    return write_stage_report(args.scratch_root, "validate", body)


def stage_drop(args, runner):
    if not args.execute:
        raise PilotError("drop requires --execute")
    assert_no_emergency_breach(args.scratch_root)
    check = load_report(args.scratch_root, "check")
    plan = load_report(args.scratch_root, "plan")
    validation = load_report(args.scratch_root, "validate")
    repatriation = load_report(
        args.scratch_root,
        "repatriate",
    )
    build = load_report(args.scratch_root, "build")
    if (
        not validation.get("accepted")
        or not repatriation.get("accepted")
    ):
        raise PilotError(
            "validation and repatriation must both be accepted"
        )
    for key in ("copyEvidence", "swapEvidence"):
        evidence = repatriation.get(key) or {}
        path = pathlib.Path(str(evidence.get("path", "")))
        if (
            not path.is_file()
            or path.is_symlink()
            or sha256_path(path) != evidence.get("sha256")
        ):
            raise PilotError(
                f"repatriation {key} is unavailable before final drop"
            )
    if (
        not repatriation.get("capacityGate", {}).get("allowed")
        or repatriation.get("homeBuildResources", {}).get(
            "filesystemEmergencyThresholdBreached"
        )
        is not False
    ):
        raise PilotError(
            "repatriation safety evidence is incomplete"
        )
    archive = load_report(args.scratch_root, "archive")
    load_report(args.scratch_root, "drill")
    host = collect_host_guard(args, runner)
    database = Database(
        runner,
        args.pg_container,
        args.pg_user,
        args.pg_database,
    )
    guard = collect_database_guard(database)
    assert_identity_matches(check, host, guard)
    retired = retired_name(args.run_id)
    scratch_retired = scratch_retired_name(args.run_id)
    home_primary = home_primary_name(args.run_id)
    home_score = home_score_name(args.run_id)
    temporary_check = replacement_instrument_check_name(
        args.run_id
    )
    source_catalog = source_catalog_names(plan["catalog"])
    scratch_mount = collect_tablespace_mount(args, runner)
    relation_probe = database.json(
        f"""
            SELECT json_build_object(
                'originalExists', to_regclass(
                    'public.{retired}') IS NOT NULL,
                'originalAttached', EXISTS (
                    SELECT 1 FROM pg_inherits
                    WHERE inhrelid = to_regclass(
                        'public.{retired}')),
                'originalBytes', CASE
                    WHEN to_regclass(
                        'public.{retired}') IS NULL
                    THEN NULL
                    ELSE pg_total_relation_size(
                        to_regclass('public.{retired}'))
                END,
                'scratchExists', to_regclass(
                    'public.{scratch_retired}') IS NOT NULL,
                'scratchAttached', EXISTS (
                    SELECT 1 FROM pg_inherits
                    WHERE inhrelid = to_regclass(
                        'public.{scratch_retired}')),
                'scratchBytes', CASE
                    WHEN to_regclass(
                        'public.{scratch_retired}') IS NULL
                    THEN NULL
                    ELSE pg_total_relation_size(
                        to_regclass('public.{scratch_retired}'))
                END,
                'targetTablespace', (
                    SELECT COALESCE(
                        tablespace.spcname, 'pg_default')
                    FROM pg_class relation
                    LEFT JOIN pg_tablespace tablespace
                      ON tablespace.oid = relation.reltablespace
                    WHERE relation.oid =
                        'public.{TARGET_PARTITION}'::regclass))
        """
    )
    pre_drop = (
        relation_probe["originalExists"]
        and relation_probe["scratchExists"]
    )
    post_drop = (
        not relation_probe["originalExists"]
        and not relation_probe["scratchExists"]
    )
    if not (pre_drop or post_drop):
        raise PilotError(
            f"mixed final-drop relation state: {relation_probe}"
        )
    if (
        relation_probe["originalAttached"]
        or relation_probe["scratchAttached"]
    ):
        raise PilotError(
            "a rollback relation is attached before final drop"
        )
    if relation_probe["targetTablespace"] != "pg_default":
        raise PilotError(
            "accepted partition is not in pg_default before final drop"
        )
    retired_relation_state = (
        database.json(relation_state_query(retired))
        if relation_probe["originalExists"]
        else None
    )
    if (
        retired_relation_state is not None
        and physical_relation_identity(retired_relation_state)
        != plan["planIdentity"]["sourceRelationIdentity"]
    ):
        raise PilotError(
            "old relation changed before final drop"
        )
    source_fence = (
        source_fence_from_relation_state(
            retired_relation_state
        )
        if retired_relation_state is not None
        else source_fence_from_relation_state(
            validation["retainedOriginalRelationState"]
        )
    )
    if archive.get("adoptedVerifiedArchive"):
        verified, _ = load_verified_archive_input(
            args,
            runner,
            check,
            source_fence,
            verify_archive_checksum=True,
        )
        archive_path = pathlib.Path(verified["archive"]["path"])
        archive_sha = verified["archive"]["sha256"]
    else:
        manifest = read_json(
            pathlib.Path(args.scratch_root)
            / ARCHIVE_DIR
            / "manifest.json"
        )
        archive_path = pathlib.Path(manifest["archive"]["path"])
        archive_sha = manifest["archive"]["sha256"]
        if (
            not archive_path.is_file()
            or sha256_path(archive_path) != archive_sha
        ):
            raise PilotError(
                "verified archive is unavailable; refusing final drop"
            )
    if pre_drop:
        scratch_fingerprint = database.json(
            fingerprint_sql(scratch_retired),
            timeout=args.query_timeout_seconds,
        )
        if (
            scratch_fingerprint
            != plan["planIdentity"]["retainedFingerprint"]
        ):
            raise PilotError(
                "scratch rollback relation differs before final drop"
            )
    else:
        scratch_fingerprint = repatriation[
            "scratchRollbackFingerprint"
        ]
    target_fingerprint = database.json(
        fingerprint_sql(TARGET_PARTITION),
        timeout=args.query_timeout_seconds,
    )
    if (
        target_fingerprint
        != plan["planIdentity"]["retainedFingerprint"]
    ):
        raise PilotError(
            "accepted pg_default relation differs before final drop"
        )
    pre_drop_catalog = database.json(catalog_query())
    if (
        catalog_semantic_shape(
            pre_drop_catalog,
            ignored_constraint_names=(temporary_check,),
        )
        != catalog_semantic_shape(plan["catalog"])
    ):
        raise PilotError(
            "accepted pg_default catalog differs before final drop"
        )
    free_before = host["dataFilesystem"]["freeBytes"]
    started = time.monotonic()
    if pre_drop:
        database.psql(
            f"""
                BEGIN;
                SET LOCAL lock_timeout = '2s';
                SET LOCAL statement_timeout = '30s';
                {advisory_lock_guard_sql()}
                DROP TABLE {qualified(retired)};
                DROP TABLE {qualified(scratch_retired)};
                ALTER TABLE {qualified(TARGET_PARTITION)}
                    DROP CONSTRAINT "{temporary_check}";
                ALTER TABLE {qualified(TARGET_PARTITION)}
                    RENAME CONSTRAINT "{home_primary}"
                    TO "{source_catalog['primaryConstraint']}";
                ALTER INDEX {qualified(home_score)}
                    RENAME TO "{source_catalog['scoreIndex']}";
                COMMIT;
            """,
            timeout=60,
        )
    elapsed = time.monotonic() - started
    if database.scalar(
        "SELECT to_regclass("
        f"{sql_literal('public.' + retired)}) IS NULL "
        "AND to_regclass("
        f"{sql_literal('public.' + scratch_retired)}) IS NULL"
    ) != "t":
        raise PilotError(
            "a rollback relation remains after final drop"
        )
    tablespace_removed = False
    if scratch_mount["enabled"]:
        tablespace = scratch_mount["tablespace"]
        relation_count = int(
            database.scalar(
                "SELECT COUNT(*) FROM pg_class "
                "WHERE reltablespace = ("
                "SELECT oid FROM pg_tablespace WHERE spcname = "
                f"{sql_literal(tablespace)})"
            )
        )
        if relation_count != 0:
            raise PilotError(
                "relations remain in the temporary scratch tablespace"
            )
        if database.scalar(
            "SELECT EXISTS(SELECT 1 FROM pg_tablespace "
            f"WHERE spcname = {sql_literal(tablespace)})"
        ) == "t":
            database.psql(
                f'DROP TABLESPACE "{tablespace}"',
                timeout=120,
            )
        runner.run(
            [
                "docker",
                "exec",
                "--user",
                "0:0",
                args.pg_container,
                "sh",
                "-c",
                (
                    f"find {TABLESPACE_CONTAINER_PATH} "
                    "-mindepth 1 -maxdepth 1 "
                    "-exec rm -rf -- {} +"
                ),
            ],
            timeout=600,
        )
        tablespace_removed = True
    host_after = collect_host_guard(args, runner)
    free_after = host_after["dataFilesystem"]["freeBytes"]
    final_catalog = database.json(catalog_query())
    if catalog_shape(final_catalog) != catalog_shape(plan["catalog"]):
        raise PilotError(
            "final partition catalog differs from the source"
        )
    public_api = (
        capture_api_fingerprints(args.api_base)
        if args.api_base
        else []
    )
    if not compare_api_snapshots(
        check.get("publicApiBaseline", []),
        public_api,
    ):
        raise PilotError(
            "public API parity changed after final drop"
        )
    body = {
        "runId": args.run_id,
        "planId": plan["planId"],
        "droppedRelations": [
            f"{TARGET_SCHEMA}.{retired}",
            f"{TARGET_SCHEMA}.{scratch_retired}",
        ],
        "droppedOriginalBytes": (
            relation_probe["originalBytes"]
            if relation_probe["originalBytes"] is not None
            else plan["catalog"]["totalBytes"]
        ),
        "droppedScratchBytes": (
            relation_probe["scratchBytes"]
            if relation_probe["scratchBytes"] is not None
            else build["sizes"]["totalBytes"]
        ),
        "elapsedSeconds": round(elapsed, 3),
        "freeBytesBefore": free_before,
        "freeBytesAfter": free_after,
        "filesystemBytesReturned": max(
            0,
            free_after - free_before,
        ),
        "resumedCommittedDrop": post_drop,
        "filesystemDeltaObservedDuringThisInvocation": (
            pre_drop
        ),
        "archiveRetained": {
            "path": str(archive_path),
            "sha256": archive_sha,
            "deletionDecision": "deferred",
        },
        "rollbackModeAfterDrop": (
            "archive restore only; rename-back rollback is no longer "
            "available"
        ),
        "droppedRelationState": retired_relation_state,
        "scratchRollbackFingerprint": scratch_fingerprint,
        "targetFingerprint": target_fingerprint,
        "finalCatalog": final_catalog,
        "temporaryTablespaceRemoved": tablespace_removed,
        "publicApi": public_api,
    }
    return write_stage_report(args.scratch_root, "drop", body)


def stage_repatriate(args, runner):
    if not args.execute:
        raise PilotError("repatriate requires --execute")
    assert_no_emergency_breach(args.scratch_root)
    check = load_report(args.scratch_root, "check")
    plan = load_report(args.scratch_root, "plan")
    validation = load_report(args.scratch_root, "validate")
    if not validation.get("accepted"):
        raise PilotError("validation was not accepted")
    load_report(args.scratch_root, "drill")
    build = load_report(args.scratch_root, "build")
    host = collect_host_guard(args, runner)
    database = Database(
        runner,
        args.pg_container,
        args.pg_user,
        args.pg_database,
    )
    guard = collect_database_guard(database)
    assert_identity_matches(check, host, guard)
    scratch_mount = collect_tablespace_mount(args, runner)
    home = home_name(args.run_id)
    scratch_retired = scratch_retired_name(args.run_id)
    home_primary = home_primary_name(args.run_id)
    home_score = home_score_name(args.run_id)
    temporary_check = replacement_instrument_check_name(
        args.run_id
    )
    keep_fingerprint = plan["planIdentity"]["retainedFingerprint"]
    retired = retired_name(args.run_id)
    state = database.json(
        f"""
            SELECT json_build_object(
                'targetTablespace', (
                    SELECT COALESCE(
                        tablespace.spcname, 'pg_default')
                    FROM pg_class relation
                    LEFT JOIN pg_tablespace tablespace
                      ON tablespace.oid = relation.reltablespace
                    WHERE relation.oid =
                        'public.{TARGET_PARTITION}'::regclass),
                'homeExists', to_regclass(
                    'public.{home}') IS NOT NULL,
                'scratchRetiredExists', to_regclass(
                    'public.{scratch_retired}') IS NOT NULL,
                'originalRetainedExists', to_regclass(
                    'public.{retired}') IS NOT NULL)
        """
    )
    copy_path = (
        pathlib.Path(args.scratch_root)
        / REPORTS_DIR
        / "repatriate.copy.json"
    )
    swap_path = (
        pathlib.Path(args.scratch_root)
        / REPORTS_DIR
        / "repatriate.swap.json"
    )
    malformed_evidence = []
    try:
        copy_evidence = (
            read_json(copy_path) if copy_path.exists() else None
        )
    except PilotError:
        copy_evidence = None
        malformed_evidence.append("copy")
    try:
        swap_evidence = (
            read_json(swap_path) if swap_path.exists() else None
        )
    except PilotError:
        swap_evidence = None
        malformed_evidence.append("swap")
    for label, evidence in (
        ("copy", copy_evidence),
        ("swap", swap_evidence),
    ):
        if evidence is not None and (
            evidence.get("toolId") != TOOL_ID
            or evidence.get("runId") != args.run_id
            or evidence.get("planId") != plan["planId"]
            or evidence.get("status") != "succeeded"
        ):
            raise PilotError(
                f"existing repatriation {label} evidence is invalid"
            )

    def rollback_unacknowledged_swap():
        database.psql(
            f"""
                BEGIN;
                SET LOCAL lock_timeout = '2s';
                SET LOCAL statement_timeout = '30s';
                {advisory_lock_guard_sql()}
                LOCK TABLE {qualified(TARGET_PARENT)}
                    IN ACCESS EXCLUSIVE MODE;
                ALTER TABLE {qualified(TARGET_PARENT)}
                    DETACH PARTITION {qualified(TARGET_PARTITION)};
                ALTER TABLE {qualified(TARGET_PARTITION)}
                    RENAME TO "{home}";
                ALTER TABLE {qualified(scratch_retired)}
                    RENAME TO "{TARGET_PARTITION}";
                ALTER TABLE {qualified(TARGET_PARENT)}
                    ATTACH PARTITION {qualified(TARGET_PARTITION)}
                    FOR VALUES IN (
                        {sql_literal(TARGET_INSTRUMENT)});
                COMMIT;
            """,
            timeout=60,
        )

    if malformed_evidence:
        if (
            state["targetTablespace"] == "pg_default"
            and state["scratchRetiredExists"]
        ):
            rollback_unacknowledged_swap()
        elif (
            state["targetTablespace"] != "pg_default"
            and state["homeExists"]
        ):
            database.psql(
                f"""
                    BEGIN;
                    SET LOCAL lock_timeout = '2s';
                    SET LOCAL statement_timeout = '30s';
                    {advisory_lock_guard_sql()}
                    DROP TABLE {qualified(home)};
                    COMMIT;
                """,
                timeout=60,
            )
        raise PilotError(
            "malformed repatriation evidence was recovered to the "
            "scratch candidate; use a new run"
        )

    if (
        state["targetTablespace"] == "pg_default"
        and state["scratchRetiredExists"]
        and swap_evidence is None
    ):
        rollback_unacknowledged_swap()
        state["targetTablespace"] = scratch_mount["tablespace"]
        state["homeExists"] = True
        state["scratchRetiredExists"] = False
    if (
        state["targetTablespace"] != "pg_default"
        and state["homeExists"]
        and copy_evidence is None
    ):
        database.psql(
            f"""
                BEGIN;
                SET LOCAL lock_timeout = '2s';
                SET LOCAL statement_timeout = '30s';
                {advisory_lock_guard_sql()}
                DROP TABLE {qualified(home)};
                COMMIT;
            """,
            timeout=60,
        )
        state["homeExists"] = False
    if (
        state["targetTablespace"] != "pg_default"
        and swap_evidence is not None
    ):
        raise PilotError(
            "prior repatriation swap evidence exists without its "
            "committed catalog state; use a new run"
        )
    if not state["originalRetainedExists"]:
        raise PilotError(
            "original rollback relation is missing before repatriation"
        )
    original_state = database.json(
        relation_state_query(retired)
    )
    if (
        physical_relation_identity(original_state)
        != plan["planIdentity"]["sourceRelationIdentity"]
    ):
        raise PilotError(
            "original rollback relation changed before repatriation"
        )
    capacity = calculate_repatriation_capacity(
        build,
        host["dataFilesystem"]["freeBytes"],
    )
    if copy_evidence is not None:
        capacity = copy_evidence.get("capacityGate")
        if (
            not isinstance(capacity, dict)
            or not capacity.get("allowed")
            or copy_evidence.get(
                "filesystemEmergencyThresholdBreached"
            )
            is not False
        ):
            raise PilotError(
                "repatriation copy evidence lacks a safe capacity "
                "decision"
            )
    home_build_elapsed = 0.0
    free_before = host["dataFilesystem"]["freeBytes"]
    if (
        state["targetTablespace"] != "pg_default"
        and not state["homeExists"]
        and not state["scratchRetiredExists"]
    ):
        if not capacity["allowed"]:
            raise PilotError(
                "pre-drop pg_default repatriation capacity gate failed: "
                f"need {capacity['requiredFreeBytes']} bytes, "
                f"observed {capacity['observedFreeBytes']} bytes"
            )
        owner = plan["catalog"]["owner"]
        if not re.fullmatch(r"[A-Za-z_][A-Za-z0-9_$]*", owner):
            raise PilotError(f"unsafe target owner name: {owner}")
        tablespace_for_temp = (
            scratch_mount["tablespace"]
            if scratch_mount["enabled"]
            else "pg_default"
        )
        attempt_path = (
            pathlib.Path(args.scratch_root)
            / REPORTS_DIR
            / (
                "repatriate.attempt-"
                + datetime.now(timezone.utc).strftime(
                    "%Y%m%dT%H%M%S%fZ"
                )
                + ".json"
            )
        )
        started_lsn = database.scalar(
            "SELECT pg_current_wal_lsn()"
        )
        temp_before = int(
            database.scalar(
                "SELECT temp_bytes FROM pg_stat_database "
                "WHERE datname = current_database()"
            )
        )
        write_json_exclusive(
            attempt_path,
            {
                "formatVersion": FORMAT_VERSION,
                "toolId": TOOL_ID,
                "stage": "repatriate",
                "status": "attempting",
                "runId": args.run_id,
                "planId": plan["planId"],
                "startedAtUtc": utc_now(),
                "startedLsn": started_lsn,
                "tempBytesBefore": temp_before,
                "freeBytesBefore": free_before,
                "capacityGate": capacity,
            },
        )
        started = time.monotonic()
        filesystem_monitor = FilesystemMonitor(
            host["dataPath"],
            minimum_allowed_bytes=(
                EMERGENCY_FLOOR_BYTES + 512 * 1024**2
            ),
            on_breach=lambda free: cancel_pilot_backends(
                runner,
                args,
                free,
                EMERGENCY_FLOOR_BYTES + 512 * 1024**2,
                "fst",
            ),
        )
        try:
            with filesystem_monitor:
                database.psql(
                    f"""
                        BEGIN;
                        SET LOCAL lock_timeout = '2s';
                        SET LOCAL statement_timeout = '0';
                        SET LOCAL temp_tablespaces =
                            {sql_literal(tablespace_for_temp)};
                        {advisory_lock_guard_sql()}
                        CREATE TABLE {qualified(home)}
                            (LIKE {qualified(TARGET_PARTITION)}
                                INCLUDING DEFAULTS
                                INCLUDING CONSTRAINTS
                                INCLUDING STORAGE
                                INCLUDING GENERATED
                                INCLUDING IDENTITY)
                            TABLESPACE pg_default;
                        ALTER TABLE {qualified(home)}
                            OWNER TO "{owner}";
                        INSERT INTO {qualified(home)}
                        SELECT * FROM {qualified(TARGET_PARTITION)};
                        CREATE UNIQUE INDEX "{home_primary}"
                            ON {qualified(home)}
                            (snapshot_id, song_id, instrument, account_id)
                            TABLESPACE pg_default;
                        ALTER TABLE {qualified(home)}
                            ADD CONSTRAINT "{home_primary}"
                            PRIMARY KEY USING INDEX "{home_primary}";
                        CREATE INDEX "{home_score}"
                            ON {qualified(home)}
                            (snapshot_id, song_id, instrument, score DESC)
                            TABLESPACE pg_default;
                        ANALYZE {qualified(home)};
                        COMMIT;
                    """,
                    timeout=args.build_timeout_seconds,
                )
        except CommandError as error:
            if filesystem_monitor.breached:
                raise PilotError(
                    "repatriation build was cancelled before breaching "
                    "the 4 TB emergency floor"
                ) from error
            raise
        if filesystem_monitor.breached:
            raise PilotError(
                "repatriation build crossed the emergency threshold"
            )
        home_build_elapsed = time.monotonic() - started
        completed_lsn = database.scalar(
            "SELECT pg_current_wal_lsn()"
        )
        temp_after = int(
            database.scalar(
                "SELECT temp_bytes FROM pg_stat_database "
                "WHERE datname = current_database()"
            )
        )
        home_build_resources = database.json(
            f"""
                SELECT json_build_object(
                    'heapBytes', pg_relation_size(
                        'public.{home}'),
                    'indexBytes', pg_indexes_size(
                        'public.{home}'),
                    'totalBytes', pg_total_relation_size(
                        'public.{home}'),
                    'walBytes', pg_wal_lsn_diff(
                        {sql_literal(completed_lsn)}::pg_lsn,
                        {sql_literal(started_lsn)}::pg_lsn)::bigint,
                    'tempBytes',
                        {max(0, temp_after - temp_before)}::bigint,
                    'minimumFstFreeBytes',
                        {filesystem_monitor.minimum_free_bytes}::bigint,
                    'monitorSamples',
                        {filesystem_monitor.samples}::bigint)
            """
        )
        home_build_resources.update(
            {
                "filesystemEmergencyThresholdBytes": (
                    filesystem_monitor.minimum_allowed_bytes
                ),
                "filesystemEmergencyThresholdBreached": (
                    filesystem_monitor.breached
                ),
                "filesystemEmergencyCancellationHandled": (
                    filesystem_monitor.breach_handled
                ),
                "filesystemEmergencyCancellationError": (
                    filesystem_monitor.breach_error
                ),
                "attemptPath": str(attempt_path),
                "attemptSha256": sha256_path(attempt_path),
            }
        )
        state["homeExists"] = True
    else:
        home_build_resources = (
            copy_evidence.get("resources")
            if copy_evidence is not None
            else None
        )
    if state["homeExists"]:
        home_fingerprint = database.json(
            fingerprint_sql(home),
            timeout=args.query_timeout_seconds,
        )
        if home_fingerprint != keep_fingerprint:
            raise PilotError(
                "pg_default repatriation copy differs from the "
                "accepted retained rows"
            )
        if copy_evidence is None:
            copy_evidence = {
                "formatVersion": FORMAT_VERSION,
                "toolId": TOOL_ID,
                "stage": "repatriate.copy",
                "status": "succeeded",
                "runId": args.run_id,
                "planId": plan["planId"],
                "completedAtUtc": utc_now(),
                "capacityGate": capacity,
                "fingerprint": home_fingerprint,
                "resources": home_build_resources,
                "filesystemEmergencyThresholdBreached": False,
            }
            write_json_exclusive(copy_path, copy_evidence)
        elif copy_evidence.get("fingerprint") != home_fingerprint:
            raise PilotError(
                "repatriation copy evidence fingerprint changed"
            )
    else:
        home_fingerprint = keep_fingerprint
    resumed_committed_swap = (
        state["targetTablespace"] == "pg_default"
        and state["scratchRetiredExists"]
    )
    swap_elapsed = (
        float(swap_evidence["elapsedSeconds"])
        if resumed_committed_swap
        else 0.0
    )
    if (
        state["targetTablespace"] != "pg_default"
        and state["homeExists"]
        and not state["scratchRetiredExists"]
    ):
        started = time.monotonic()
        database.psql(
            f"""
                BEGIN;
                SET LOCAL lock_timeout = '2s';
                SET LOCAL statement_timeout = '30s';
                {advisory_lock_guard_sql()}
                LOCK TABLE {qualified(TARGET_PARENT)}
                    IN ACCESS EXCLUSIVE MODE;
                ALTER TABLE {qualified(TARGET_PARENT)}
                    DETACH PARTITION {qualified(TARGET_PARTITION)};
                ALTER TABLE {qualified(TARGET_PARTITION)}
                    RENAME TO "{scratch_retired}";
                ALTER TABLE {qualified(home)}
                    RENAME TO "{TARGET_PARTITION}";
                ALTER TABLE {qualified(TARGET_PARENT)}
                    ATTACH PARTITION {qualified(TARGET_PARTITION)}
                    FOR VALUES IN (
                        {sql_literal(TARGET_INSTRUMENT)});
                COMMIT;
            """,
            timeout=60,
        )
        swap_elapsed = time.monotonic() - started
        swap_evidence = {
            "formatVersion": FORMAT_VERSION,
            "toolId": TOOL_ID,
            "stage": "repatriate.swap",
            "status": "succeeded",
            "runId": args.run_id,
            "planId": plan["planId"],
            "completedAtUtc": utc_now(),
            "elapsedSeconds": round(swap_elapsed, 6),
            "maximumSwapSeconds": args.maximum_swap_seconds,
            "withinBound": (
                swap_elapsed <= args.maximum_swap_seconds
            ),
            "copyEvidenceSha256": sha256_path(copy_path),
        }
        write_json_exclusive(swap_path, swap_evidence)
        resumed_committed_swap = False
    if (
        swap_evidence is None
        or swap_evidence.get("copyEvidenceSha256")
        != sha256_path(copy_path)
        or swap_evidence.get("withinBound") is not True
    ):
        if database.scalar(
            "SELECT to_regclass("
            f"{sql_literal('public.' + scratch_retired)}) "
            "IS NOT NULL"
        ) == "t":
            rollback_unacknowledged_swap()
        raise PilotError(
            "repatriation swap lacks complete bounded evidence"
        )
    post_swap = database.json(
        f"""
            SELECT json_build_object(
                'targetTablespace', (
                    SELECT COALESCE(
                        tablespace.spcname, 'pg_default')
                    FROM pg_class relation
                    LEFT JOIN pg_tablespace tablespace
                      ON tablespace.oid = relation.reltablespace
                    WHERE relation.oid =
                        'public.{TARGET_PARTITION}'::regclass),
                'scratchRetiredExists', to_regclass(
                    'public.{scratch_retired}') IS NOT NULL)
        """
    )
    if post_swap["targetTablespace"] != "pg_default":
        raise PilotError(
            "accepted partition is not back in pg_default"
        )
    target_fingerprint = database.json(
        fingerprint_sql(TARGET_PARTITION),
        timeout=args.query_timeout_seconds,
    )
    scratch_fingerprint = database.json(
        fingerprint_sql(scratch_retired),
        timeout=args.query_timeout_seconds,
    )
    candidate_catalog = database.json(catalog_query())
    reference_parity = database.json(
        reference_parity_query(TARGET_PARTITION),
        timeout=args.query_timeout_seconds,
    )
    baseline_reference = plan["planIdentity"]["referenceParity"]
    reference_ok = (
        not reference_parity["missingRequiredSourceIds"]
        and reference_parity["activeStateMissingRows"] == 0
        and reference_parity["projectionMissingRows"] == 0
        and all(
            reference_parity[key] == baseline_reference[key]
            for key in (
                "sourceMapRows",
                "maxScoreFingerprint",
                "referenceCount",
            )
        )
    )
    public_api = (
        capture_api_fingerprints(args.api_base)
        if args.api_base
        else []
    )
    parity_ok = (
        target_fingerprint == keep_fingerprint
        and scratch_fingerprint == keep_fingerprint
        and catalog_semantic_shape(
            candidate_catalog,
            ignored_constraint_names=(temporary_check,),
        )
        == catalog_semantic_shape(plan["catalog"])
        and reference_ok
        and swap_elapsed <= args.maximum_swap_seconds
        and compare_api_snapshots(
            check.get("publicApiBaseline", []),
            public_api,
        )
    )
    if not parity_ok and post_swap["scratchRetiredExists"]:
        database.psql(
            f"""
                BEGIN;
                SET LOCAL lock_timeout = '2s';
                SET LOCAL statement_timeout = '30s';
                {advisory_lock_guard_sql()}
                LOCK TABLE {qualified(TARGET_PARENT)}
                    IN ACCESS EXCLUSIVE MODE;
                ALTER TABLE {qualified(TARGET_PARENT)}
                    DETACH PARTITION {qualified(TARGET_PARTITION)};
                ALTER TABLE {qualified(TARGET_PARTITION)}
                    RENAME TO "{home}";
                ALTER TABLE {qualified(scratch_retired)}
                    RENAME TO "{TARGET_PARTITION}";
                ALTER TABLE {qualified(TARGET_PARENT)}
                    ATTACH PARTITION {qualified(TARGET_PARTITION)}
                    FOR VALUES IN (
                        {sql_literal(TARGET_INSTRUMENT)});
                COMMIT;
            """,
            timeout=60,
        )
        raise PilotError(
            "repatriation parity failed and the scratch candidate "
            "was restored"
        )
    if not parity_ok:
        raise PilotError("repatriation parity failed")
    host_after = collect_host_guard(args, runner)
    body = {
        "runId": args.run_id,
        "planId": plan["planId"],
        "accepted": True,
        "homeBuildElapsedSeconds": round(
            home_build_elapsed, 3
        ),
        "homeBuildResources": home_build_resources,
        "copyEvidence": {
            "path": str(copy_path),
            "sha256": sha256_path(copy_path),
        },
        "swapElapsedSeconds": round(swap_elapsed, 3),
        "maximumSwapSeconds": args.maximum_swap_seconds,
        "resumedCommittedSwap": resumed_committed_swap,
        "swapEvidence": {
            "path": str(swap_path),
            "sha256": sha256_path(swap_path),
        },
        "capacityGate": capacity,
        "targetFingerprint": target_fingerprint,
        "scratchRollbackFingerprint": scratch_fingerprint,
        "candidateCatalog": candidate_catalog,
        "referenceParity": reference_parity,
        "publicApi": public_api,
        "originalRollbackRelationPresent": True,
        "scratchRollbackRelationPresent": True,
        "acceptedRelationTablespace": candidate_catalog[
            "tablespace"
        ],
        "freeBytesBefore": free_before,
        "freeBytesAfter": host_after["dataFilesystem"][
            "freeBytes"
        ],
        "buildReportSha256": sha256_path(
            report_path(args.scratch_root, "build")
        ),
    }
    return write_stage_report(
        args.scratch_root,
        "repatriate",
        body,
    )


def stage_rollback(args, runner):
    if not args.execute:
        raise PilotError("rollback requires --execute")
    if report_path(args.scratch_root, "drop").exists():
        raise PilotError(
            "rename-back rollback is unavailable after final drop"
        )
    check = load_report(args.scratch_root, "check")
    plan = load_report(args.scratch_root, "plan")
    load_report(args.scratch_root, "swap")
    load_report(args.scratch_root, "archive")
    host = collect_host_guard(args, runner)
    database = Database(
        runner,
        args.pg_container,
        args.pg_user,
        args.pg_database,
    )
    guard = collect_database_guard(database)
    assert_identity_matches(check, host, guard)
    retired = retired_name(args.run_id)
    failed = failed_name(args.run_id)
    relation_state = database.json(
        f"""
            SELECT json_build_object(
                'targetAttached', EXISTS (
                    SELECT 1 FROM pg_inherits
                    WHERE inhparent =
                        'public.{TARGET_PARENT}'::regclass
                      AND inhrelid =
                        'public.{TARGET_PARTITION}'::regclass),
                'retiredExists', to_regclass(
                    'public.{retired}') IS NOT NULL,
                'failedExists', to_regclass(
                    'public.{failed}') IS NOT NULL)
        """
    )
    pre_rollback = {
        "targetAttached": True,
        "retiredExists": True,
        "failedExists": False,
    }
    post_rollback = {
        "targetAttached": True,
        "retiredExists": False,
        "failedExists": True,
    }
    if relation_state not in (pre_rollback, post_rollback):
        raise PilotError(
            f"unexpected rollback relation state: {relation_state}"
        )
    resumed_committed_rollback = relation_state == post_rollback
    original_relation_name = (
        TARGET_PARTITION if resumed_committed_rollback else retired
    )
    original_relation_state = database.json(
        relation_state_query(original_relation_name)
    )
    if (
        physical_relation_identity(original_relation_state)
        != plan["planIdentity"]["sourceRelationIdentity"]
    ):
        raise PilotError(
            "retained original relation changed; refusing rollback"
        )
    original_retained_fingerprint = database.json(
        fingerprint_sql(
            original_relation_name,
            snapshot_id_predicate(
                plan["planIdentity"]["keepSnapshotIds"]
            ),
        ),
        timeout=args.query_timeout_seconds,
        pgoptions=PLAN_QUERY_PGOPTIONS,
    )
    if (
        original_retained_fingerprint
        != plan["planIdentity"]["retainedFingerprint"]
    ):
        raise PilotError(
            "protected rows in the retained original changed"
        )
    started = time.monotonic()
    if not resumed_committed_rollback:
        database.psql(
            f"""
                BEGIN;
                SET LOCAL lock_timeout = '2s';
                SET LOCAL statement_timeout = '30s';
                {advisory_lock_guard_sql()}
                LOCK TABLE {qualified(TARGET_PARENT)}
                    IN ACCESS EXCLUSIVE MODE;
                ALTER TABLE {qualified(TARGET_PARENT)}
                    DETACH PARTITION {qualified(TARGET_PARTITION)};
                ALTER TABLE {qualified(TARGET_PARTITION)}
                    RENAME TO "{failed}";
                ALTER TABLE {qualified(retired)}
                    RENAME TO "{TARGET_PARTITION}";
                ALTER TABLE {qualified(TARGET_PARENT)}
                    ATTACH PARTITION {qualified(TARGET_PARTITION)}
                    FOR VALUES IN (
                        {sql_literal(TARGET_INSTRUMENT)});
                COMMIT;
            """,
            timeout=60,
        )
    elapsed = time.monotonic() - started
    restored_relation_state = database.json(
        relation_state_query(TARGET_PARTITION)
    )
    if (
        physical_relation_identity(restored_relation_state)
        != plan["planIdentity"]["sourceRelationIdentity"]
    ):
        raise PilotError(
            "rename-back rollback relation differs from the original"
        )
    restored_retained_fingerprint = database.json(
        fingerprint_sql(
            TARGET_PARTITION,
            snapshot_id_predicate(
                plan["planIdentity"]["keepSnapshotIds"]
            ),
        ),
        timeout=args.query_timeout_seconds,
        pgoptions=PLAN_QUERY_PGOPTIONS,
    )
    if (
        restored_retained_fingerprint
        != plan["planIdentity"]["retainedFingerprint"]
    ):
        raise PilotError(
            "rename-back protected rows differ from the original"
        )
    public_api = (
        capture_api_fingerprints(args.api_base)
        if args.api_base
        else []
    )
    if not compare_api_snapshots(
        check.get("publicApiBaseline", []),
        public_api,
    ):
        raise PilotError(
            "public API parity changed after rename-back rollback"
        )
    body = {
        "runId": args.run_id,
        "planId": plan["planId"],
        "elapsedSeconds": round(elapsed, 3),
        "restoredRetainedFingerprint": (
            restored_retained_fingerprint
        ),
        "restoredRelationState": restored_relation_state,
        "restoredRelation": (
            f"{TARGET_SCHEMA}.{TARGET_PARTITION}"
        ),
        "failedCandidateRetainedRelation": (
            f"{TARGET_SCHEMA}.{failed}"
        ),
        "archiveStillRetained": True,
        "finalDropPerformed": False,
        "publicApi": public_api,
        "resumedCommittedRollback": resumed_committed_rollback,
    }
    return write_stage_report(args.scratch_root, "rollback", body)


def build_parser():
    parser = argparse.ArgumentParser(
        description=(
            "Guarded pro-bass snapshot archive/rewrite pilot. "
            "No arbitrary table or SQL input is accepted."
        )
    )
    parser.add_argument("stage", choices=STAGES)
    parser.add_argument("--scratch-root", required=True)
    parser.add_argument("--expected-device-id", required=True)
    parser.add_argument("--run-id", required=True)
    parser.add_argument(
        "--expires-at",
        help=(
            "ISO-8601 workspace expiry; defaults to 45 days from "
            "the initial check"
        ),
    )
    parser.add_argument("--claim-workspace", action="store_true")
    parser.add_argument("--execute", action="store_true")
    parser.add_argument("--test-mode", action="store_true")
    parser.add_argument(
        "--compose-dir",
        default=str(PRODUCTION_COMPOSE_DIR),
    )
    parser.add_argument("--pg-container", default=POSTGRES_CONTAINER)
    parser.add_argument("--worker-container", default=WORKER_CONTAINER)
    parser.add_argument("--pg-user", default=DATABASE_USER)
    parser.add_argument("--pg-database", default=DATABASE_NAME)
    parser.add_argument("--restore-image")
    parser.add_argument(
        "--rollback-completed-to-keep",
        type=int,
        default=1,
    )
    parser.add_argument(
        "--query-timeout-seconds",
        type=int,
        default=14_400,
    )
    parser.add_argument(
        "--archive-timeout-seconds",
        type=int,
        default=86_400,
    )
    parser.add_argument(
        "--build-timeout-seconds",
        type=int,
        default=86_400,
    )
    parser.add_argument(
        "--maximum-swap-seconds",
        type=float,
        default=30.0,
    )
    parser.add_argument("--measured-profile")
    parser.add_argument("--expected-profile-sha256")
    parser.add_argument("--verified-live-archive-input")
    parser.add_argument(
        "--expected-live-archive-input-sha256"
    )
    parser.add_argument("--api-base")
    return parser


def validate_args(args):
    if not re.fullmatch(r"[a-zA-Z0-9][a-zA-Z0-9._-]{7,80}", args.run_id):
        raise PilotError(
            "--run-id must be 8-81 safe identifier characters"
        )
    if args.rollback_completed_to_keep < 0:
        raise PilotError(
            "--rollback-completed-to-keep cannot be negative"
        )
    for label in (
        "query_timeout_seconds",
        "archive_timeout_seconds",
        "build_timeout_seconds",
    ):
        if getattr(args, label) <= 0:
            raise PilotError(f"--{label.replace('_', '-')} must be positive")
    if args.maximum_swap_seconds <= 0:
        raise PilotError("--maximum-swap-seconds must be positive")
    if args.stage == "build" and (
        not args.measured_profile
        or not args.expected_profile_sha256
    ):
        raise PilotError(
            f"{args.stage} requires --measured-profile and "
            "--expected-profile-sha256"
        )
    if not args.test_mode:
        if pathlib.Path(args.compose_dir).resolve() != PRODUCTION_COMPOSE_DIR:
            raise PilotError(
                "production compose directory must be exact"
            )
        if args.pg_container != POSTGRES_CONTAINER:
            raise PilotError(
                "production PostgreSQL container must be exact"
            )
        if args.worker_container != WORKER_CONTAINER:
            raise PilotError("production worker container must be exact")
        if args.pg_user != DATABASE_USER:
            raise PilotError("production PostgreSQL user must be exact")
        if args.pg_database != DATABASE_NAME:
            raise PilotError("production database must be exact")
        if args.restore_image:
            raise PilotError(
                "production restore drill must use the exact checked "
                "PostgreSQL image"
            )
        if args.stage != "check" and (
            not args.verified_live_archive_input
            or not args.expected_live_archive_input_sha256
        ):
            raise PilotError(
                "production stages after check require the verified "
                "live archive input and SHA-256"
            )
        if args.stage in (
            "check",
            "validate",
            "drop",
            "repatriate",
            "rollback",
        ) and not args.api_base:
            raise PilotError(
                "production check/validate/drop/rollback requires "
                "--api-base for "
                "public-route parity"
            )
    if args.stage in MUTATING_DATABASE_STAGES and not args.execute:
        raise PilotError(f"{args.stage} requires --execute")
    if args.stage == "check" and not args.claim_workspace:
        raise PilotError("initial check requires --claim-workspace")


def stage_function(stage):
    return {
        "check": stage_check,
        "plan": stage_plan,
        "archive": stage_archive,
        "drill": stage_drill,
        "build": stage_build,
        "swap": stage_swap,
        "validate": stage_validate,
        "drop": stage_drop,
        "repatriate": stage_repatriate,
        "rollback": stage_rollback,
    }[stage]


def main(argv=None):
    parser = build_parser()
    args = parser.parse_args(argv)
    runner = Runner()
    try:
        validate_args(args)
        scratch_info = validate_scratch_root(
            runner,
            args.scratch_root,
            args.expected_device_id,
            test_mode=args.test_mode,
            allow_unclaimed=args.stage == "check",
        )
        repository_commit = load_git_commit(runner)
        tool_source_sha256 = load_tool_source_sha256()
        if not args.test_mode:
            require_clean_repository(runner)
        expires_at = args.expires_at or (
            datetime.now(timezone.utc)
            + timedelta(days=DEFAULT_EXPIRY_DAYS)
        ).isoformat().replace("+00:00", "Z")
        marker = claim_workspace(
            args.scratch_root,
            scratch_info,
            args.run_id,
            expires_at,
            repository_commit,
            tool_source_sha256,
            args.test_mode,
        )
        validate_workspace_marker(
            marker,
            args.run_id,
            repository_commit,
            tool_source_sha256,
        )
        with workspace_lock(args.scratch_root):
            path = report_path(args.scratch_root, args.stage)
            if path.exists():
                report = load_report(args.scratch_root, args.stage)
            else:
                try:
                    function = stage_function(args.stage)
                    if args.stage == "check":
                        report = function(
                            args,
                            runner,
                            scratch_info,
                            marker,
                        )
                    else:
                        report = function(args, runner)
                except Exception as error:
                    write_failure_report(
                        args.scratch_root,
                        args.stage,
                        error,
                    )
                    raise
        print(
            json.dumps(
                {
                    "stage": args.stage,
                    "status": "succeeded",
                    "report": str(
                        report_path(
                            args.scratch_root,
                            args.stage,
                        )
                    ),
                    "reportSha256": sha256_path(
                        report_path(
                            args.scratch_root,
                            args.stage,
                        )
                    ),
                    "runId": report.get("runId"),
                },
                sort_keys=True,
            )
        )
        return 0
    except PilotError as error:
        print(f"ERROR: {error}", file=sys.stderr)
        return 3
    except subprocess.TimeoutExpired as error:
        print(
            f"ERROR: command timed out after {error.timeout}s",
            file=sys.stderr,
        )
        return 4


if __name__ == "__main__":
    sys.exit(main())
