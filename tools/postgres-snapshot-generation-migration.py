#!/usr/bin/env python3

"""Guarded migration of the nine snapshot instrument partitions.

The accepted targets are compiled into this file.  The command line accepts a
fixed instrument key, never a relation name or SQL text.  Each workspace moves
one instrument through archive, isolated restore, retained-generation build,
short-lock swap, validation, and either rollback or final drop.
"""

import argparse
import contextlib
import dataclasses
import fcntl
import hashlib
import json
import math
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
TOOL_ID = "fst.snapshot-generation-partition-migration.v1"
TARGET_SCHEMA = "public"
TARGET_PARENT = "leaderboard_entries_snapshot"
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
APPLICATION_NAME = "fst-snapshot-generation-migration"
POSTGRES_MAJOR = 17
WORKSPACE_MARKER = ".fst-snapshot-generation-migration.json"
LOCK_FILE = ".fst-snapshot-generation-migration.lock"
REPORTS_DIR = "reports"
ARCHIVE_DIR = "archive"
RESTORE_DIR = "restore"
RECOVERED_DIR = "recovered-evidence"
DEFAULT_EXPIRY_DAYS = 90
EMERGENCY_FLOOR_BYTES = 60_392_999_803
FST_MONITOR_MARGIN_BYTES = 512 * 1024**2
SCRATCH_RESERVE_BYTES = 20 * 1024**3
PUBLICATION_ADVISORY_LOCK_KEY = 5_067_481_511_116_519_500
MAINTENANCE_ADVISORY_LOCK_KEY = 5_067_481_511_116_519_502
PLAN_QUERY_PGOPTIONS = (
    "-c work_mem=64MB "
    "-c temp_file_limit=262144kB "
    "-c max_parallel_workers_per_gather=0"
)
LOOSE_ID_PGOPTIONS = PLAN_QUERY_PGOPTIONS + " -c enable_seqscan=off"
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
    "restore",
    "build",
    "swap",
    "validate",
    "drop",
    "rollback",
)
MUTATING_DATABASE_STAGES = {
    "build",
    "swap",
    "drop",
    "rollback",
}
EXECUTE_STAGES = {
    "archive",
    "restore",
    *MUTATING_DATABASE_STAGES,
}
DEPENDENCIES = {
    "plan": ("check",),
    "archive": ("plan",),
    "restore": ("archive",),
    "build": ("plan", "restore"),
    "swap": ("build", "archive", "restore"),
    "validate": ("swap", "archive", "restore"),
    "drop": ("validate", "archive", "restore"),
    "rollback": ("archive",),
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


@dataclasses.dataclass(frozen=True)
class Target:
    key: str
    partition: str
    instrument: str
    slug: str

    @property
    def bound(self):
        return f"FOR VALUES IN ('{self.instrument}')"


TARGETS = (
    Target(
        "solo-guitar",
        "leaderboard_entries_snapshot_solo_guitar",
        "Solo_Guitar",
        "sg",
    ),
    Target(
        "solo-bass",
        "leaderboard_entries_snapshot_solo_bass",
        "Solo_Bass",
        "sb",
    ),
    Target(
        "solo-drums",
        "leaderboard_entries_snapshot_solo_drums",
        "Solo_Drums",
        "sd",
    ),
    Target(
        "solo-vocals",
        "leaderboard_entries_snapshot_solo_vocals",
        "Solo_Vocals",
        "sv",
    ),
    Target(
        "pro-guitar",
        "leaderboard_entries_snapshot_pro_guitar",
        "Solo_PeripheralGuitar",
        "pg",
    ),
    Target(
        "pro-bass",
        "leaderboard_entries_snapshot_pro_bass",
        "Solo_PeripheralBass",
        "pb",
    ),
    Target(
        "pro-vocals",
        "leaderboard_entries_snapshot_pro_vocals",
        "Solo_PeripheralVocals",
        "pv",
    ),
    Target(
        "pro-cymbals",
        "leaderboard_entries_snapshot_pro_cymbals",
        "Solo_PeripheralCymbals",
        "pc",
    ),
    Target(
        "pro-drums",
        "leaderboard_entries_snapshot_pro_drums",
        "Solo_PeripheralDrums",
        "pd",
    ),
)
TARGET_BY_KEY = {target.key: target for target in TARGETS}


class MigrationError(RuntimeError):
    """A fail-closed guard or stage error."""


class CommandError(MigrationError):
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


def fsync_directory(path):
    descriptor = os.open(
        pathlib.Path(path),
        os.O_RDONLY | getattr(os, "O_DIRECTORY", 0),
    )
    try:
        os.fsync(descriptor)
    finally:
        os.close(descriptor)


def write_bytes_exclusive(path, value, mode=0o600):
    path = pathlib.Path(path)
    path.parent.mkdir(parents=True, exist_ok=True, mode=0o700)
    temporary = path.with_name(
        f".{path.name}.partial-{os.getpid()}-{time.time_ns()}"
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
        fsync_directory(path.parent)
    finally:
        with contextlib.suppress(FileNotFoundError):
            temporary.unlink()


def write_json_exclusive(path, value):
    write_bytes_exclusive(path, canonical_json_bytes(value))


def write_or_verify_bytes(path, value, mode=0o600):
    path = pathlib.Path(path)
    if path.exists():
        if path.read_bytes() != value:
            raise MigrationError(
                f"immutable artifact differs from expected bytes: {path}"
            )
        return False
    write_bytes_exclusive(path, value, mode)
    return True


def write_or_verify_json(path, value):
    return write_or_verify_bytes(path, canonical_json_bytes(value))


def load_or_create_started_evidence(path, value, identity_keys):
    path = pathlib.Path(path)
    if path.exists():
        existing = read_json(path)
        for key in identity_keys:
            if existing.get(key) != value.get(key):
                raise MigrationError(
                    f"started evidence {path} differs at {key}"
                )
        return existing, True
    write_json_exclusive(path, value)
    return value, False


def read_json(path, maximum_bytes=32 * 1024 * 1024):
    path = pathlib.Path(path)
    metadata = path.lstat()
    if path.is_symlink() or not stat.S_ISREG(metadata.st_mode):
        raise MigrationError(f"JSON input is not a regular file: {path}")
    if metadata.st_size > maximum_bytes:
        raise MigrationError(f"JSON input is too large: {path}")
    try:
        return json.loads(path.read_text(encoding="utf-8"))
    except (UnicodeDecodeError, json.JSONDecodeError) as error:
        raise MigrationError(f"JSON input is malformed: {path}") from error


def ensure_integer(value, label, minimum=0):
    if isinstance(value, bool) or not isinstance(value, int):
        raise MigrationError(f"{label} must be an integer")
    if value < minimum:
        raise MigrationError(f"{label} must be at least {minimum}")
    return value


def ensure_safe_identifier(value, label="identifier"):
    if not re.fullmatch(r"[a-z][a-z0-9_]*", value):
        raise MigrationError(f"unsafe {label}: {value}")
    if len(value) > 63:
        raise MigrationError(f"{label} exceeds PostgreSQL's name limit")
    return value


def sql_literal(value):
    return "'" + str(value).replace("'", "''") + "'"


def qualified(name):
    return f'"{TARGET_SCHEMA}"."{ensure_safe_identifier(name)}"'


def relation_token(run_id, target):
    return hashlib.sha256(
        f"{run_id}\0{target.key}".encode("utf-8")
    ).hexdigest()[:12]


def replacement_name(run_id, target):
    return f"sgm_{target.slug}_{relation_token(run_id, target)}_new"


def retired_name(run_id, target):
    return f"sgm_{target.slug}_{relation_token(run_id, target)}_old"


def failed_name(run_id, target):
    return f"sgm_{target.slug}_{relation_token(run_id, target)}_failed"


def replacement_primary_name(run_id, target):
    return f"sgm_{target.slug}_{relation_token(run_id, target)}_pk"


def replacement_score_name(run_id, target):
    return f"sgm_{target.slug}_{relation_token(run_id, target)}_score"


def replacement_instrument_check_name(run_id, target):
    return f"sgm_{target.slug}_{relation_token(run_id, target)}_inst"


def original_instrument_check_name(run_id, target):
    return f"sgm_{target.slug}_{relation_token(run_id, target)}_orig_inst"


def swap_commit_path(root):
    return pathlib.Path(root) / REPORTS_DIR / "swap.committed.json"


def generation_child_name(target, snapshot_id):
    snapshot_id = ensure_integer(snapshot_id, "snapshot ID", 1)
    return ensure_safe_identifier(
        f"{target.partition}_s{snapshot_id}",
        "generation child name",
    )


def default_child_name(target):
    return ensure_safe_identifier(
        f"{target.partition}_default",
        "default child name",
    )


def failed_child_name(run_id, target, snapshot_id=None):
    suffix = (
        "default"
        if snapshot_id is None
        else f"s{ensure_integer(snapshot_id, 'snapshot ID', 1)}"
    )
    return ensure_safe_identifier(
        f"sgm_{target.slug}_{relation_token(run_id, target)}_{suffix}",
        "failed child name",
    )


def expected_artifact_names(run_id, target, protected_ids=()):
    names = {
        replacement_name(run_id, target),
        retired_name(run_id, target),
        failed_name(run_id, target),
        replacement_primary_name(run_id, target),
        replacement_score_name(run_id, target),
    }
    for snapshot_id in protected_ids:
        names.add(generation_child_name(target, snapshot_id))
        names.add(failed_child_name(run_id, target, snapshot_id))
    names.add(default_child_name(target))
    names.add(failed_child_name(run_id, target))
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
        completed = subprocess.run(
            [str(value) for value in arguments],
            input=input_text,
            text=True,
            capture_output=True,
            timeout=timeout,
            env=env,
        )
        if check and completed.returncode != 0:
            raise CommandError(
                arguments,
                completed.returncode,
                completed.stdout,
                completed.stderr,
            )
        return completed

    def run_to_file(self, arguments, path, *, timeout, env=None):
        path = pathlib.Path(path)
        path.parent.mkdir(parents=True, exist_ok=True, mode=0o700)
        temporary = path.with_name(
            f".{path.name}.partial-{os.getpid()}-{time.time_ns()}"
        )
        try:
            descriptor = os.open(
                temporary,
                os.O_WRONLY | os.O_CREAT | os.O_EXCL,
                0o600,
            )
            with os.fdopen(descriptor, "wb") as output:
                process = subprocess.Popen(
                    [str(value) for value in arguments],
                    stdin=subprocess.DEVNULL,
                    stdout=output,
                    stderr=subprocess.PIPE,
                    env=env,
                )
                try:
                    _, stderr = process.communicate(timeout=timeout)
                except subprocess.TimeoutExpired:
                    process.kill()
                    _, stderr = process.communicate()
                    raise
                output.flush()
                os.fsync(output.fileno())
            if process.returncode != 0:
                raise CommandError(
                    arguments,
                    process.returncode,
                    stderr=(stderr or b"").decode(
                        "utf-8",
                        errors="replace",
                    ),
                )
            os.replace(temporary, path)
            fsync_directory(path.parent)
        finally:
            with contextlib.suppress(FileNotFoundError):
                temporary.unlink()


class Database:
    def __init__(self, runner, container, user, database):
        self.runner = runner
        self.container = container
        self.user = user
        self.database = database

    def _arguments(self, sql, pgoptions=None):
        options = (
            f"-c application_name={APPLICATION_NAME} "
            "-c row_security=off"
        )
        if pgoptions:
            options += " " + pgoptions
        return [
            "docker",
            "exec",
            "-e",
            "PGCONNECT_TIMEOUT=10",
            "-e",
            f"PGOPTIONS={options}",
            self.container,
            "psql",
            "-X",
            "-v",
            "ON_ERROR_STOP=1",
            "-U",
            self.user,
            "-d",
            self.database,
            "-At",
            "-c",
            sql,
        ]

    def scalar(self, sql, *, timeout=600, pgoptions=None):
        return self.runner.run(
            self._arguments(sql, pgoptions),
            timeout=timeout,
        ).stdout.strip()

    def json(self, sql, *, timeout=600, pgoptions=None):
        output = self.scalar(
            sql,
            timeout=timeout,
            pgoptions=pgoptions,
        )
        if not output:
            raise MigrationError("database JSON query returned no rows")
        try:
            return json.loads(output)
        except json.JSONDecodeError as error:
            raise MigrationError(
                f"database returned malformed JSON: {output[:500]}"
            ) from error

    def psql(self, sql, *, timeout=600, pgoptions=None):
        return self.runner.run(
            self._arguments(sql, pgoptions),
            timeout=timeout,
        )


class FilesystemMonitor:
    def __init__(
        self,
        path,
        minimum_allowed_bytes,
        on_breach,
        interval_seconds=0.25,
    ):
        self.path = pathlib.Path(path)
        self.minimum_allowed_bytes = minimum_allowed_bytes
        self.on_breach = on_breach
        self.interval_seconds = interval_seconds
        self.minimum_free_bytes = None
        self.samples = 0
        self.breached = False
        self.breach_handled = False
        self.breach_error = None
        self._stopped = threading.Event()
        self._thread = None

    def _observe(self):
        free = shutil.disk_usage(self.path).free
        self.samples += 1
        self.minimum_free_bytes = (
            free
            if self.minimum_free_bytes is None
            else min(self.minimum_free_bytes, free)
        )
        if free < self.minimum_allowed_bytes:
            self.breached = True
            if not self.breach_handled:
                try:
                    self.on_breach(free)
                    self.breach_handled = True
                except Exception as error:
                    self.breach_error = str(error)

    def _run(self):
        while not self._stopped.wait(self.interval_seconds):
            self._observe()

    def __enter__(self):
        self._observe()
        self._thread = threading.Thread(
            target=self._run,
            name="fst-snapshot-generation-filesystem-monitor",
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
        raise MigrationError(
            f"could not identify filesystem for {path}"
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
            raise MigrationError(
                f"path contains a symbolic link: {current}"
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
        raise MigrationError("--scratch-root must be an absolute path")
    if not requested.exists() or not requested.is_dir():
        raise MigrationError(
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
        raise MigrationError(f"scratch root is forbidden: {resolved}")
    for denied in (
        PRODUCTION_STORAGE_ROOT,
        PRODUCTION_DOCKER_ROOT,
        pathlib.Path("/tmp"),
        pathlib.Path("/var/tmp"),
    ):
        if path_is_beneath(resolved, denied):
            raise MigrationError(
                f"scratch root is beneath a forbidden path: {resolved}"
            )
    mount = find_mount(runner, resolved)
    filesystem = mount["filesystemType"].lower()
    if (
        REMOTE_FILESYSTEM_PATTERN.search(filesystem)
        or filesystem not in LOCAL_FILESYSTEMS
    ):
        raise MigrationError(
            "scratch root must use an allowlisted local filesystem"
        )
    if mount["deviceId"] != expected_device_id:
        raise MigrationError(
            "scratch device identity mismatch: expected "
            f"{expected_device_id}, observed {mount['deviceId']}"
        )
    if not test_mode:
        if mount["source"] != PRODUCTION_SCRATCH_DEVICE:
            raise MigrationError(
                "production scratch must resolve to "
                f"{PRODUCTION_SCRATCH_DEVICE}, observed {mount['source']}"
            )
        if mount["mountTarget"] != "/":
            raise MigrationError(
                "production scratch device must be mounted at /"
            )
    marker_path = resolved / WORKSPACE_MARKER
    entries = {
        entry.name
        for entry in resolved.iterdir()
        if entry.name != LOCK_FILE
    }
    allowed = {
        WORKSPACE_MARKER,
        REPORTS_DIR,
        ARCHIVE_DIR,
        RESTORE_DIR,
        RECOVERED_DIR,
    }
    if marker_path.exists():
        marker = read_json(marker_path)
        if marker.get("toolId") != TOOL_ID:
            raise MigrationError(
                "scratch workspace marker belongs to another tool"
            )
        foreign = sorted(entries - allowed)
        if foreign:
            raise MigrationError(
                "scratch workspace contains foreign entries: "
                + ", ".join(foreign[:10])
            )
        for name in allowed - {WORKSPACE_MARKER}:
            child = resolved / name
            metadata = child.lstat()
            if child.is_symlink() or not stat.S_ISDIR(metadata.st_mode):
                raise MigrationError(
                    "workspace-owned path is not a real directory: "
                    f"{child}"
                )
    elif entries:
        message = (
            "new scratch workspace must be empty before it is claimed"
            if allow_unclaimed
            else "unclaimed scratch workspace is not empty"
        )
        raise MigrationError(
            message + ": " + ", ".join(sorted(entries)[:10])
        )
    usage = shutil.disk_usage(resolved)
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
    target,
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
            raise MigrationError(
                "workspace is already claimed by run "
                f"{marker.get('runId')}"
            )
        return marker
    marker = {
        "formatVersion": FORMAT_VERSION,
        "toolId": TOOL_ID,
        "runId": run_id,
        "targetKey": target.key,
        "target": f"{TARGET_SCHEMA}.{target.partition}",
        "instrument": target.instrument,
        "acceptedTargetCount": len(TARGETS),
        "temporaryScratchOnly": True,
        "acceptedDatabaseDataMayRemainHere": False,
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
        RECOVERED_DIR,
    ):
        (root / name).mkdir(mode=0o700, exist_ok=True)
    fsync_directory(root)
    return marker


def validate_workspace_marker(
    marker,
    run_id,
    target,
    repository_commit,
    tool_source_sha256,
    *,
    now=None,
):
    expected = {
        "toolId": TOOL_ID,
        "runId": run_id,
        "targetKey": target.key,
        "target": f"{TARGET_SCHEMA}.{target.partition}",
        "instrument": target.instrument,
        "repositoryCommit": repository_commit,
        "toolSourceSha256": tool_source_sha256,
    }
    for key, value in expected.items():
        if marker.get(key) != value:
            raise MigrationError(
                f"workspace marker {key} differs from this invocation"
            )
    try:
        expires_at = datetime.fromisoformat(
            str(marker.get("expiresAtUtc")).replace("Z", "+00:00")
        )
    except ValueError as error:
        raise MigrationError(
            "workspace expiry is not valid ISO-8601"
        ) from error
    if expires_at.tzinfo is None:
        raise MigrationError("workspace expiry must include a timezone")
    if expires_at <= (now or datetime.now(timezone.utc)):
        raise MigrationError("scratch workspace ownership has expired")


@contextlib.contextmanager
def workspace_lock(root):
    lock_path = pathlib.Path(root) / LOCK_FILE
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
        raise MigrationError(
            f"another migration process owns {lock_path}"
        ) from error
    try:
        yield
    finally:
        fcntl.flock(descriptor, fcntl.LOCK_UN)
        os.close(descriptor)


def report_path(root, stage):
    return pathlib.Path(root) / REPORTS_DIR / f"{stage}.json"


def report_integrity(report):
    unsigned = dict(report)
    unsigned.pop("integritySha256", None)
    return sha256_bytes(canonical_json_bytes(unsigned))


def write_integrity_evidence(path, value):
    path = pathlib.Path(path)
    body = dict(value)
    body["integritySha256"] = report_integrity(body)
    if path.exists():
        observed = read_json(path)
        if observed != body:
            raise MigrationError(
                f"immutable evidence differs from expected content: {path}"
            )
        return observed
    write_json_exclusive(path, body)
    return body


def load_integrity_evidence(path, label):
    path = pathlib.Path(path)
    if not path.is_file():
        raise MigrationError(f"required {label} evidence is missing: {path}")
    value = read_json(path)
    if value.get("integritySha256") != report_integrity(value):
        raise MigrationError(f"required {label} evidence failed integrity")
    return value


def load_report(root, stage):
    path = report_path(root, stage)
    if not path.is_file():
        raise MigrationError(
            f"required {stage} report does not exist: {path}"
        )
    report = read_json(path)
    if (
        report.get("stage") != stage
        or report.get("status") != "succeeded"
        or report.get("toolId") != TOOL_ID
    ):
        raise MigrationError(
            f"required {stage} report is not a valid success report"
        )
    if report.get("integritySha256") != report_integrity(report):
        raise MigrationError(
            f"required {stage} report failed its integrity check"
        )
    for dependency, evidence in (
        report.get("dependencies") or {}
    ).items():
        dependency_path = pathlib.Path(evidence.get("path", ""))
        if (
            not dependency_path.is_file()
            or dependency_path.is_symlink()
            or sha256_path(dependency_path) != evidence.get("sha256")
        ):
            raise MigrationError(
                f"{stage} dependency {dependency} changed"
            )
    return report


def dependency_hashes(root, stage):
    result = {}
    for dependency in DEPENDENCIES.get(stage, ()):
        load_report(root, dependency)
        path = report_path(root, dependency)
        result[dependency] = {
            "path": str(path),
            "sha256": sha256_path(path),
        }
    return result


def write_stage_report(root, stage, body):
    path = report_path(root, stage)
    if path.exists():
        return load_report(root, stage)
    report = {
        "formatVersion": FORMAT_VERSION,
        "toolId": TOOL_ID,
        "stage": stage,
        "status": "succeeded",
        "completedAtUtc": utc_now(),
        "dependencies": dependency_hashes(root, stage),
        **body,
    }
    report["integritySha256"] = report_integrity(report)
    write_json_exclusive(path, report)
    return report


def write_failure_report(root, stage, error):
    timestamp = datetime.now(timezone.utc).strftime("%Y%m%dT%H%M%S%fZ")
    path = (
        pathlib.Path(root)
        / REPORTS_DIR
        / f"{stage}.failed-{timestamp}.json"
    )
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


def recover_torn_report(root, stage):
    path = report_path(root, stage)
    if not path.exists():
        return None
    metadata = path.lstat()
    if path.is_symlink() or not stat.S_ISREG(metadata.st_mode):
        raise MigrationError(
            f"stage report is not a regular file: {path}"
        )
    try:
        return load_report(root, stage)
    except MigrationError as error:
        malformed = metadata.st_size == 0
        if not malformed:
            try:
                json.loads(path.read_text(encoding="utf-8"))
            except (UnicodeDecodeError, json.JSONDecodeError):
                malformed = True
        if not malformed:
            raise
        recovered_root = pathlib.Path(root) / RECOVERED_DIR
        recovered_root.mkdir(mode=0o700, exist_ok=True)
        timestamp = datetime.now(timezone.utc).strftime(
            "%Y%m%dT%H%M%S%fZ"
        )
        destination = recovered_root / f"{stage}.torn-{timestamp}.json"
        os.replace(path, destination)
        fsync_directory(path.parent)
        fsync_directory(recovered_root)
        evidence = {
            "formatVersion": FORMAT_VERSION,
            "toolId": TOOL_ID,
            "stage": stage,
            "recoveredAtUtc": utc_now(),
            "reason": str(error),
            "tornPath": str(destination),
            "bytes": destination.stat().st_size,
            "sha256": sha256_path(destination),
            "stageWillReconcileDatabaseState": True,
        }
        write_json_exclusive(
            recovered_root / f"{stage}.recovery-{timestamp}.json",
            evidence,
        )
        return None


def inspect_container(runner, name, *, allow_missing=False):
    completed = runner.run(
        ["docker", "inspect", name],
        timeout=30,
        check=not allow_missing,
    )
    if allow_missing and completed.returncode != 0:
        return None
    try:
        rows = json.loads(completed.stdout)
    except json.JSONDecodeError as error:
        raise MigrationError(
            f"docker inspect returned malformed JSON for {name}"
        ) from error
    if len(rows) != 1:
        raise MigrationError(f"unexpected docker inspect result for {name}")
    return rows[0]


def collect_host_guard(args, runner):
    postgres = inspect_container(runner, args.pg_container)
    state = postgres.get("State") or {}
    health = (state.get("Health") or {}).get("Status")
    if not state.get("Running") or health not in (None, "healthy"):
        raise MigrationError(
            "PostgreSQL container is not healthy and running"
        )
    labels = (postgres.get("Config") or {}).get("Labels") or {}
    mounts = postgres.get("Mounts") or []
    data_mounts = [
        mount
        for mount in mounts
        if mount.get("Destination") == "/var/lib/postgresql/data"
    ]
    if len(data_mounts) != 1:
        raise MigrationError(
            "PostgreSQL must have exactly one PGDATA mount"
        )
    mount = data_mounts[0]
    if (
        mount.get("Type") != "bind"
        or not mount.get("RW")
        or not mount.get("Source")
    ):
        raise MigrationError(
            "PostgreSQL PGDATA must be a read-write bind mount"
        )
    data_source = pathlib.Path(mount["Source"]).resolve(strict=True)
    data_filesystem = find_mount(runner, data_source)
    if not args.test_mode:
        if labels.get("com.docker.compose.project") != PRODUCTION_PROJECT:
            raise MigrationError("unexpected production Compose project")
        working_dir = pathlib.Path(
            labels.get("com.docker.compose.project.working_dir", "")
        )
        if working_dir.resolve() != PRODUCTION_COMPOSE_DIR:
            raise MigrationError(
                "unexpected production Compose working directory"
            )
        if not path_is_beneath(data_source, PRODUCTION_STORAGE_ROOT):
            raise MigrationError(
                "PostgreSQL PGDATA is outside the 4 TB FST root"
            )
        worker = inspect_container(runner, args.worker_container)
        if (worker.get("State") or {}).get("Running"):
            raise MigrationError("fstworker must be held offline")
        worker_labels = (worker.get("Config") or {}).get("Labels") or {}
        if (
            worker_labels.get("com.docker.compose.project")
            != PRODUCTION_PROJECT
        ):
            raise MigrationError(
                "unexpected worker Compose project"
            )
    elif not args.pg_container.startswith(
        "fst-snapshot-generation-test-"
    ):
        raise MigrationError(
            "test mode requires an isolated test container name"
        )
    usage = shutil.disk_usage(data_source)
    return {
        "postgresContainerId": postgres.get("Id"),
        "postgresImage": (postgres.get("Config") or {}).get("Image"),
        "composeProject": labels.get("com.docker.compose.project"),
        "composeWorkingDir": labels.get(
            "com.docker.compose.project.working_dir"
        ),
        "dataMount": {
            "type": mount.get("Type"),
            "source": str(data_source),
            "destination": mount.get("Destination"),
            "readWrite": mount.get("RW"),
            "filesystem": data_filesystem,
        },
        "dataFilesystem": {
            "totalBytes": usage.total,
            "usedBytes": usage.used,
            "freeBytes": usage.free,
        },
        "hostResources": {
            "cpuCount": os.cpu_count(),
            "loadAverage": list(os.getloadavg()),
            "availableMemoryBytes": available_memory_bytes(),
        },
        "testMode": args.test_mode,
    }


def available_memory_bytes():
    try:
        for line in pathlib.Path("/proc/meminfo").read_text(
            encoding="utf-8"
        ).splitlines():
            if line.startswith("MemAvailable:"):
                return int(line.split()[1]) * 1024
    except (OSError, ValueError, IndexError):
        return None


def artifact_probe_query():
        return """
            SELECT COALESCE(
                json_agg(relation.relname ORDER BY relation.relname),
                '[]'::json)
            FROM pg_class relation
            JOIN pg_namespace namespace
              ON namespace.oid = relation.relnamespace
            WHERE namespace.nspname = 'public'
              AND relation.relname LIKE 'sgm\\_%' ESCAPE '\\'
        """


def relation_state_query(relation_name):
        relation = f"{TARGET_SCHEMA}.{relation_name}"
        return f"""
            SELECT COALESCE((
                SELECT json_build_object(
                    'oid', relation.oid,
                    'relfilenode', relation.relfilenode,
                    'relationKind', relation.relkind,
                    'heapBytes', pg_relation_size(relation.oid),
                    'indexBytes', pg_indexes_size(relation.oid),
                    'totalBytes', pg_total_relation_size(relation.oid),
                    'treeBytes', (
                        SELECT COALESCE(
                            SUM(pg_total_relation_size(tree.relid)),
                            pg_total_relation_size(relation.oid))
                        FROM pg_partition_tree(relation.oid) tree),
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
                    'partitionKey', pg_get_partkeydef(relation.oid),
                    'owner', pg_get_userbyid(relation.relowner),
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
            ), 'null'::json)
        """


def physical_relation_identity(state):
        if not state:
            return None
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


def source_fence_from_state(state):
        identity = physical_relation_identity(state)
        if identity is None:
            raise MigrationError("cannot create a fence for a missing relation")
        return {
            "partitionOid": identity["oid"],
            "relfilenode": identity["relfilenode"],
            "heapBytes": identity["heapBytes"],
            "indexBytes": identity["indexBytes"],
            "totalBytes": identity["totalBytes"],
            "inserts": identity["inserts"],
            "updates": identity["updates"],
            "deletes": identity["deletes"],
        }


def source_fence_matches(expected, observed):
        return all(
            int(expected[key]) == int(observed[key])
            for key in (
                "partitionOid",
                "relfilenode",
                "heapBytes",
                "indexBytes",
                "totalBytes",
                "inserts",
                "updates",
                "deletes",
            )
        )


def parent_catalog_query():
        return f"""
            SELECT json_build_object(
                'oid', parent.oid,
                'relationKind', parent.relkind,
                'partitionKey', pg_get_partkeydef(parent.oid),
                'owner', pg_get_userbyid(parent.relowner),
                'tablespace', COALESCE(
                    tablespace.spcname, 'pg_default'),
                'columns', (
                    SELECT json_agg(
                        json_build_object(
                            'ordinal', attribute.attnum,
                            'name', attribute.attname,
                            'type', format_type(
                                attribute.atttypid,
                                attribute.atttypmod),
                            'notNull', attribute.attnotnull,
                            'defaultExpression', pg_get_expr(
                                default_value.adbin,
                                default_value.adrelid))
                        ORDER BY attribute.attnum)
                    FROM pg_attribute attribute
                    LEFT JOIN pg_attrdef default_value
                      ON default_value.adrelid = attribute.attrelid
                     AND default_value.adnum = attribute.attnum
                    WHERE attribute.attrelid = parent.oid
                      AND attribute.attnum > 0
                      AND NOT attribute.attisdropped),
                'constraints', (
                    SELECT COALESCE(
                        json_agg(
                            json_build_object(
                                'name', constraint_row.conname,
                                'type', constraint_row.contype,
                                'definition', pg_get_constraintdef(
                                    constraint_row.oid, true))
                            ORDER BY constraint_row.conname),
                        '[]'::json)
                    FROM pg_constraint constraint_row
                    WHERE constraint_row.conrelid = parent.oid),
                'indexes', (
                    SELECT COALESCE(
                        json_agg(
                            json_build_object(
                                'name', index_class.relname,
                                'definition', pg_get_indexdef(
                                    index_class.oid),
                                'isPrimary', metadata.indisprimary,
                                'isUnique', metadata.indisunique,
                                'isValid', metadata.indisvalid,
                                'relationKind', index_class.relkind)
                            ORDER BY index_class.relname),
                        '[]'::json)
                    FROM pg_index metadata
                    JOIN pg_class index_class
                      ON index_class.oid = metadata.indexrelid
                    WHERE metadata.indrelid = parent.oid)
            )
            FROM pg_class parent
            JOIN pg_namespace namespace
              ON namespace.oid = parent.relnamespace
            LEFT JOIN pg_tablespace tablespace
              ON tablespace.oid = parent.reltablespace
            WHERE namespace.nspname = {sql_literal(TARGET_SCHEMA)}
              AND parent.relname = {sql_literal(TARGET_PARENT)}
        """


def catalog_query(relation_name):
        return f"""
            SELECT json_build_object(
                'relationKind', relation.relkind,
                'partitionKey', pg_get_partkeydef(relation.oid),
                'partitionBound', pg_get_expr(
                    relation.relpartbound, relation.oid, true),
                'owner', pg_get_userbyid(relation.relowner),
                'tablespace', COALESCE(
                    tablespace.spcname, 'pg_default'),
                'columns', (
                    SELECT json_agg(
                        json_build_object(
                            'ordinal', attribute.attnum,
                            'name', attribute.attname,
                            'type', format_type(
                                attribute.atttypid,
                                attribute.atttypmod),
                            'notNull', attribute.attnotnull,
                            'defaultExpression', pg_get_expr(
                                default_value.adbin,
                                default_value.adrelid))
                        ORDER BY attribute.attnum)
                    FROM pg_attribute attribute
                    LEFT JOIN pg_attrdef default_value
                      ON default_value.adrelid = attribute.attrelid
                     AND default_value.adnum = attribute.attnum
                    WHERE attribute.attrelid = relation.oid
                      AND attribute.attnum > 0
                      AND NOT attribute.attisdropped),
                'constraints', (
                    SELECT COALESCE(
                        json_agg(
                            json_build_object(
                                'name', constraint_row.conname,
                                'type', constraint_row.contype,
                                'definition', pg_get_constraintdef(
                                    constraint_row.oid, true),
                                'validated', constraint_row.convalidated)
                            ORDER BY constraint_row.conname),
                        '[]'::json)
                    FROM pg_constraint constraint_row
                    WHERE constraint_row.conrelid = relation.oid),
                'indexes', (
                    SELECT COALESCE(
                        json_agg(
                            json_build_object(
                                'name', index_class.relname,
                                'definition', pg_get_indexdef(
                                    index_class.oid),
                                'isPrimary', metadata.indisprimary,
                                'isUnique', metadata.indisunique,
                                'isValid', metadata.indisvalid,
                                'relationKind', index_class.relkind,
                                'parentIndex', parent_index.relname)
                            ORDER BY index_class.relname),
                        '[]'::json)
                    FROM pg_index metadata
                    JOIN pg_class index_class
                      ON index_class.oid = metadata.indexrelid
                    LEFT JOIN pg_inherits inheritance
                      ON inheritance.inhrelid = index_class.oid
                    LEFT JOIN pg_class parent_index
                      ON parent_index.oid = inheritance.inhparent
                    WHERE metadata.indrelid = relation.oid),
                'heapBytes', pg_relation_size(relation.oid),
                'indexBytes', pg_indexes_size(relation.oid),
                'totalBytes', pg_total_relation_size(relation.oid))
            FROM pg_class relation
            JOIN pg_namespace namespace
              ON namespace.oid = relation.relnamespace
            LEFT JOIN pg_tablespace tablespace
              ON tablespace.oid = relation.reltablespace
            WHERE namespace.nspname = {sql_literal(TARGET_SCHEMA)}
              AND relation.relname = {sql_literal(relation_name)}
        """


def normalize_index_definition(definition):
        return re.sub(
            r"^CREATE (UNIQUE )?INDEX \S+ ON(?: ONLY)? \S+ ",
            lambda match: (
                "CREATE UNIQUE INDEX <name> ON <table> "
                if match.group(1)
                else "CREATE INDEX <name> ON <table> "
            ),
            definition,
        )


def catalog_semantic_shape(catalog, *, ignored_constraints=()):
        return {
            "relationKind": catalog["relationKind"],
            "partitionKey": catalog["partitionKey"],
            "partitionBound": catalog["partitionBound"],
            "owner": catalog["owner"],
            "tablespace": catalog["tablespace"],
            "columns": catalog["columns"],
            "constraints": sorted(
                (
                    {
                        "type": item["type"],
                        "definition": item["definition"],
                        "validated": item.get("validated", True),
                    }
                    for item in catalog["constraints"]
                    if item["name"] not in ignored_constraints
                ),
                key=lambda item: (
                    item["type"],
                    item["definition"],
                ),
            ),
            "indexes": sorted(
                (
                    {
                        "definition": normalize_index_definition(
                            item["definition"]
                        ),
                        "isPrimary": item["isPrimary"],
                        "isUnique": item["isUnique"],
                        "isValid": item["isValid"],
                        "relationKind": item["relationKind"],
                    }
                    for item in catalog["indexes"]
                ),
                key=lambda item: (
                    not item["isPrimary"],
                    item["definition"],
                ),
            ),
        }


def source_catalog_names(catalog):
        primary_constraints = [
            item
            for item in catalog["constraints"]
            if item["type"] == "p"
        ]
        primary_indexes = [
            item for item in catalog["indexes"] if item["isPrimary"]
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
            raise MigrationError(
                "source catalog is not the exact primary/score-index shape"
            )
        names = {
            "primaryConstraint": primary_constraints[0]["name"],
            "primaryIndex": primary_indexes[0]["name"],
            "scoreIndex": score_indexes[0]["name"],
        }
        if names["primaryConstraint"] != names["primaryIndex"]:
            raise MigrationError(
                "source primary constraint/index names do not match"
            )
        for value in names.values():
            ensure_safe_identifier(value, "source catalog name")
        return names


def collect_database_guard(database, target, allowed_artifacts=()):
        query = f"""
            SELECT json_build_object(
                'database', current_database(),
                'databaseOid', (
                    SELECT oid
                    FROM pg_database
                    WHERE datname = current_database()),
                'user', current_user,
                'serverVersion', current_setting('server_version'),
                'serverVersionNum',
                    current_setting('server_version_num')::integer,
                'systemIdentifier',
                    (SELECT system_identifier::text
                     FROM pg_control_system()),
                'postmasterStartedAt', pg_postmaster_start_time(),
                'dataDirectory', current_setting('data_directory'),
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
                    SELECT COUNT(*)
                    FROM pg_locks
                    WHERE NOT granted),
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
                    WHERE lock.pid <> pg_backend_pid()
                      AND lock.relation IN (
                          to_regclass(
                              'public.{TARGET_PARENT}'),
                          to_regclass(
                              'public.{target.partition}'))),
                'parentCatalog', ({parent_catalog_query()}),
                'target', ({relation_state_query(target.partition)}),
                'migrationArtifacts', ({artifact_probe_query()}))
        """
        guard = database.json(query)
        publication = guard.get("publication")
        if not publication:
            raise MigrationError(
                "scrape_publication_state singleton is missing"
            )
        if publication.get("publicReadsFrozen"):
            raise MigrationError("public reads must be unfrozen")
        if publication.get("workingPublicationId") is not None:
            raise MigrationError("working publication must be empty")
        if (
            publication.get("commitIntentOwner")
            or publication.get("commitIntentStartedAt")
        ):
            raise MigrationError(
                "publication commit intent must be empty"
            )
        if publication.get("maxScoreMutationGateToken"):
            raise MigrationError("max-score mutation gate must be empty")
        if not publication.get("currentPublicationId"):
            raise MigrationError("current publication is missing")
        if not publication.get("previousPublicationId"):
            raise MigrationError("previous publication is missing")
        for key in (
            "runningScrapes",
            "activePhaseAttempts",
            "waitingLocks",
            "workerBackends",
            "maintenanceBackends",
            "targetLocks",
        ):
            if ensure_integer(guard.get(key), key) != 0:
                raise MigrationError(f"database guard {key} is nonzero")
        for row in guard.get("workerStatus") or []:
            if str(row.get("status", "")).lower() not in {
                "offline",
                "stopped",
                "idle",
            }:
                raise MigrationError(
                    "durable worker state is not held offline"
                )
        if int(guard["serverVersionNum"]) // 10000 != POSTGRES_MAJOR:
            raise MigrationError("migration requires PostgreSQL 17")
        parent = guard.get("parentCatalog") or {}
        if (
            parent.get("relationKind") != "p"
            or parent.get("partitionKey") != "LIST (instrument)"
            or parent.get("tablespace") != "pg_default"
        ):
            raise MigrationError(
                "snapshot parent identity/partitioning is unexpected"
            )
        target_state = guard.get("target")
        if not target_state:
            raise MigrationError("the fixed target partition is missing")
        if (
            not target_state.get("attached")
            or target_state.get("partitionBound") != target.bound
            or target_state.get("tablespace") != "pg_default"
        ):
            raise MigrationError(
                "fixed target attachment, bound, or tablespace differs"
            )
        allowed = set(allowed_artifacts)
        unexpected = sorted(
            set(guard.get("migrationArtifacts") or []) - allowed
        )
        if unexpected:
            raise MigrationError(
                "another snapshot migration artifact is present: "
                + ", ".join(unexpected[:10])
            )
        return guard


def assert_identity_matches(check, host, guard):
        expected = check["identity"]
        observed = {
            "postgresContainerId": host["postgresContainerId"],
            "postgresImage": host["postgresImage"],
            "pgdataMountSource": host["dataMount"]["source"],
            "pgdataMountDestination": host["dataMount"]["destination"],
            "pgdataDeviceId": host["dataMount"]["filesystem"]["deviceId"],
            "database": guard["database"],
            "databaseOid": guard["databaseOid"],
            "systemIdentifier": guard["systemIdentifier"],
            "parentOid": guard["parentCatalog"]["oid"],
        }
        if observed != expected:
            raise MigrationError(
                "system/database/parent identity changed after check"
            )
        data_directory = pathlib.PurePosixPath(guard["dataDirectory"])
        destination = pathlib.PurePosixPath(
            host["dataMount"]["destination"]
        )
        try:
            data_directory.relative_to(destination)
        except ValueError as error:
            raise MigrationError(
                "PostgreSQL data_directory is outside the bound PGDATA mount"
            ) from error
        expected_publication = check["publicationFence"]
        publication = guard["publication"]
        observed_publication = {
            key: publication[key]
            for key in (
                "publishedScrapeId",
                "currentPublicationId",
                "previousPublicationId",
                "workingPublicationId",
            )
        }
        if observed_publication != expected_publication:
            raise MigrationError(
                "publication identity changed after check"
            )


def load_git_commit(runner):
        repository = pathlib.Path(__file__).resolve().parent.parent
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
                "postgres-snapshot-generation-migration-drill.py"
            ),
            script.with_name(
                "postgres-snapshot-generation-migration-drill.sh"
            ),
        )
        digest = hashlib.sha256()
        for source in sources:
            if not source.is_file():
                continue
            digest.update(source.name.encode("utf-8"))
            digest.update(b"\0")
            digest.update(source.read_bytes())
            digest.update(b"\0")
        return digest.hexdigest()


def require_clean_repository(runner):
        repository = pathlib.Path(__file__).resolve().parent.parent
        status = runner.run(
            [
                "git",
                "-C",
                str(repository),
                "status",
                "--porcelain=v1",
                "--untracked-files=all",
            ],
            timeout=30,
        ).stdout.strip()
        if status:
            raise MigrationError(
                "production execution requires a clean repository checkout"
            )


def snapshot_row_expression(alias="row"):
    values = []
    for column in SNAPSHOT_COLUMNS:
            values.append(
                f"COALESCE({alias}.\"{column}\"::text, '<null>')"
            )
    return "concat_ws(E'\\x1f', " + ", ".join(values) + ")"


def snapshot_id_predicate(snapshot_ids, alias=None):
    values = sorted(
            {
                ensure_integer(value, "snapshot ID", 1)
                for value in snapshot_ids
            }
    )
    if not values:
            raise MigrationError("snapshot ID predicate cannot be empty")
    prefix = f"{alias}." if alias else ""
    return (
            f"{prefix}snapshot_id = ANY(ARRAY["
            + ", ".join(str(value) for value in values)
            + "]::bigint[])"
    )


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
                    SUM(hashtextextended({expression}, 2)::numeric),
                    0)::text)
            FROM {qualified(relation_name)} row
            WHERE {predicate}
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
                       md5(MIN(snapshot.account_id))
                           AS account_id_min_hash,
                       md5(MAX(snapshot.account_id))
                           AS account_id_max_hash,
                       MIN(snapshot.score) AS score_min,
                       MAX(snapshot.score) AS score_max,
                       COALESCE(bit_xor(hashtextextended(
                           {expression}, 0)), 0) AS hash_xor_0,
                       COALESCE(bit_xor(hashtextextended(
                           {expression}, 1)), 0) AS hash_xor_1,
                       COALESCE(SUM(hashtextextended(
                           {expression}, 2)::numeric), 0)::text
                           AS hash_sum_2
                FROM {qualified(relation_name)} snapshot
                WHERE {predicate}
                GROUP BY snapshot.snapshot_id
            ) distribution
    """


def normalized_distribution(rows):
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


def protected_sources_query(target):
    return f"""
            WITH publication_slots AS (
                SELECT current_publication_id AS publication_id,
                       'current_publication'::text AS slot
                FROM scrape_publication_state
                WHERE id = TRUE
                UNION ALL
                SELECT previous_publication_id,
                       'previous_publication'
                FROM scrape_publication_state
                WHERE id = TRUE
                UNION ALL
                SELECT working_publication_id,
                       'working_publication'
                FROM scrape_publication_state
                WHERE id = TRUE
            ),
            resolved_publications AS (
                SELECT slot.publication_id,
                       slot.slot,
                       generation.scrape_id AS published_scrape_id
                FROM publication_slots slot
                LEFT JOIN publication_generations generation
                  ON generation.publication_id = slot.publication_id
                WHERE slot.publication_id IS NOT NULL
            ),
            protected AS (
                SELECT state.active_snapshot_id AS snapshot_id,
                       'active_snapshot_state'::text AS reason
                FROM leaderboard_snapshot_state state
                WHERE state.instrument =
                        {sql_literal(target.instrument)}
                  AND state.active_snapshot_id IS NOT NULL
                UNION ALL
                SELECT scope.source_snapshot_id,
                       'current_projection_source'
                FROM solo_current_projection_scope scope
                WHERE scope.instrument =
                        {sql_literal(target.instrument)}
                  AND scope.source_snapshot_id IS NOT NULL
                UNION ALL
                SELECT source.source_snapshot_id,
                       publication.slot || '_physical_source'
                FROM resolved_publications publication
                JOIN leaderboard_published_scope_source source
                  ON source.published_scrape_id =
                        publication.published_scrape_id
                WHERE source.instrument =
                        {sql_literal(target.instrument)}
                  AND source.source_kind = 'snapshot'
                  AND source.source_snapshot_id IS NOT NULL
            )
            SELECT json_build_object(
                'publicationSlots', (
                    SELECT COALESCE(
                        json_agg(
                            json_build_object(
                                'publicationId', publication_id,
                                'slot', slot,
                                'publishedScrapeId',
                                    published_scrape_id)
                            ORDER BY slot),
                        '[]'::json)
                    FROM resolved_publications),
                'unresolvedPublicationCount', (
                    SELECT COUNT(*)
                    FROM resolved_publications
                    WHERE published_scrape_id IS NULL),
                'invalidNamedSnapshotSourceCount', (
                    SELECT COUNT(*)
                    FROM resolved_publications publication
                    JOIN leaderboard_published_scope_source source
                      ON source.published_scrape_id =
                            publication.published_scrape_id
                    WHERE source.instrument =
                            {sql_literal(target.instrument)}
                      AND source.source_kind = 'snapshot'
                      AND (
                          source.source_snapshot_id IS NULL
                          OR source.source_snapshot_id <= 0)),
                'protected', (
                    SELECT COALESCE(
                        json_agg(
                            json_build_object(
                                'snapshotId', grouped.snapshot_id,
                                'reasons', grouped.reasons,
                                'referenceCount',
                                    grouped.reference_count)
                            ORDER BY grouped.snapshot_id DESC),
                        '[]'::json)
                    FROM (
                        SELECT snapshot_id,
                               array_agg(
                                   DISTINCT reason
                                   ORDER BY reason) AS reasons,
                               COUNT(*)::bigint AS reference_count
                        FROM protected
                        WHERE snapshot_id IS NOT NULL
                        GROUP BY snapshot_id
                    ) grouped))
    """


def inventory_query(target):
    return f"""
            WITH RECURSIVE snapshot_ids(snapshot_id) AS (
                (
                    SELECT MIN(snapshot_id)
                    FROM {qualified(target.partition)}
                )
                UNION ALL
                SELECT (
                    SELECT MIN(next_snapshot.snapshot_id)
                    FROM {qualified(target.partition)} next_snapshot
                    WHERE next_snapshot.snapshot_id >
                        current_snapshot.snapshot_id)
                FROM snapshot_ids current_snapshot
                WHERE current_snapshot.snapshot_id IS NOT NULL
            )
            SELECT COALESCE(
                json_agg(snapshot_id ORDER BY snapshot_id DESC),
                '[]'::json)
            FROM snapshot_ids
            WHERE snapshot_id IS NOT NULL
    """


def derive_protected_ids(protected_result, inventory_ids):
    if ensure_integer(
            protected_result.get("unresolvedPublicationCount"),
            "unresolvedPublicationCount",
    ):
            raise MigrationError(
                "a named publication ID does not resolve to a generation"
            )
    if ensure_integer(
            protected_result.get("invalidNamedSnapshotSourceCount"),
            "invalidNamedSnapshotSourceCount",
    ):
            raise MigrationError(
                "a named publication has an invalid physical snapshot source"
            )
    rows = protected_result.get("protected") or []
    if not rows:
            raise MigrationError(
                "no protected snapshot IDs were derived for the instrument"
            )
    ids = []
    seen = set()
    for row in rows:
            snapshot_id = ensure_integer(
                row.get("snapshotId"),
                "protected snapshot ID",
                1,
            )
            if snapshot_id in seen:
                raise MigrationError(
                    "protected snapshot ID rows are not unique"
                )
            reasons = row.get("reasons") or []
            if not reasons or any(
                not isinstance(reason, str) or not reason
                for reason in reasons
            ):
                raise MigrationError(
                    "protected snapshot ID lacks typed reasons"
                )
            ensure_integer(
                row.get("referenceCount"),
                "protected reference count",
                1,
            )
            ids.append(snapshot_id)
            seen.add(snapshot_id)
    inventory = {
            ensure_integer(value, "inventory snapshot ID", 1)
            for value in inventory_ids
    }
    missing = sorted(seen - inventory)
    if missing:
            raise MigrationError(
                "protected snapshot IDs are absent from the source partition: "
                + ", ".join(str(value) for value in missing)
            )
    return sorted(ids)


def reference_parity_query(target, relation_name):
    return f"""
            WITH publication_slots AS (
                SELECT current_publication_id AS publication_id,
                       'current'::text AS slot
                FROM scrape_publication_state
                WHERE id = TRUE
                UNION ALL
                SELECT previous_publication_id, 'previous'
                FROM scrape_publication_state
                WHERE id = TRUE
                UNION ALL
                SELECT working_publication_id, 'working'
                FROM scrape_publication_state
                WHERE id = TRUE
            ),
            named_publications AS (
                SELECT slot.slot,
                       generation.scrape_id AS published_scrape_id
                FROM publication_slots slot
                JOIN publication_generations generation
                  ON generation.publication_id = slot.publication_id
                WHERE slot.publication_id IS NOT NULL
            ),
            named_sources AS (
                SELECT publication.slot,
                       source.*
                FROM named_publications publication
                JOIN leaderboard_published_scope_source source
                  ON source.published_scrape_id =
                        publication.published_scrape_id
                WHERE source.instrument =
                        {sql_literal(target.instrument)}
                  AND source.source_kind = 'snapshot'
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
                        'publicReadsFrozen', public_reads_frozen)
                    FROM scrape_publication_state
                    WHERE id = TRUE),
                'missingNamedSourceRows', (
                    SELECT COUNT(*)::bigint
                    FROM named_sources source
                    WHERE source.source_snapshot_id IS NULL
                       OR NOT EXISTS (
                           SELECT 1
                           FROM {qualified(relation_name)} snapshot
                           WHERE snapshot.snapshot_id =
                                    source.source_snapshot_id
                             AND snapshot.song_id = source.song_id
                             AND snapshot.instrument =
                                    {sql_literal(target.instrument)})),
                'activeStateMissingRows', (
                    SELECT COUNT(*)::bigint
                    FROM leaderboard_snapshot_state state
                    WHERE state.instrument =
                            {sql_literal(target.instrument)}
                      AND state.active_snapshot_id IS NOT NULL
                      AND NOT EXISTS (
                          SELECT 1
                          FROM {qualified(relation_name)} snapshot
                          WHERE snapshot.snapshot_id =
                                    state.active_snapshot_id
                            AND snapshot.song_id = state.song_id
                            AND snapshot.instrument =
                                    {sql_literal(target.instrument)})),
                'projectionMissingRows', (
                    SELECT COUNT(*)::bigint
                    FROM solo_current_projection_scope scope
                    WHERE scope.instrument =
                            {sql_literal(target.instrument)}
                      AND scope.source_snapshot_id IS NOT NULL
                      AND NOT EXISTS (
                          SELECT 1
                          FROM {qualified(relation_name)} snapshot
                          WHERE snapshot.snapshot_id =
                                    scope.source_snapshot_id
                            AND snapshot.song_id = scope.song_id
                            AND snapshot.instrument =
                                    {sql_literal(target.instrument)})),
                'namedSourceCount', (
                    SELECT COUNT(*)::bigint
                    FROM named_sources),
                'namedSourceFingerprint', (
                    SELECT md5(COALESCE(string_agg(
                        md5(concat_ws('|',
                            slot,
                            published_scrape_id::text,
                            song_id,
                            scope_kind,
                            source_snapshot_id::text,
                            row_count::text,
                            content_fingerprint,
                            coverage_fingerprint)),
                        '' ORDER BY
                            slot, published_scrape_id, song_id, scope_kind),
                        ''))
                    FROM named_sources),
                'activeStateFingerprint', (
                    SELECT md5(COALESCE(string_agg(
                        md5(concat_ws('|',
                            song_id,
                            active_snapshot_id::text,
                            scrape_id::text,
                            is_finalized::text)),
                        '' ORDER BY song_id),
                        ''))
                    FROM leaderboard_snapshot_state
                    WHERE instrument =
                        {sql_literal(target.instrument)}),
                'projectionFingerprint', (
                    SELECT md5(COALESCE(string_agg(
                        md5(concat_ws('|',
                            song_id,
                            source_snapshot_id::text,
                            projection_generation::text,
                            row_count::text,
                            source_kind,
                            status)),
                        '' ORDER BY song_id),
                        ''))
                    FROM solo_current_projection_scope
                    WHERE instrument =
                        {sql_literal(target.instrument)}))
    """


def assert_reference_parity(value):
    for key in (
            "missingNamedSourceRows",
            "activeStateMissingRows",
            "projectionMissingRows",
    ):
            if ensure_integer(value.get(key), key) != 0:
                raise MigrationError(
                    f"reference parity failed: {key} is nonzero"
                )


def partition_shape_query(target, relation_name):
    return f"""
            WITH root AS (
                SELECT relation.oid
                FROM pg_class relation
                JOIN pg_namespace namespace
                  ON namespace.oid = relation.relnamespace
                WHERE namespace.nspname = 'public'
                  AND relation.relname =
                        {sql_literal(relation_name)}
            ),
            children AS (
                SELECT child.oid,
                       child.relname,
                       child.relkind,
                       pg_get_expr(
                           child.relpartbound, child.oid, true)
                           AS partition_bound,
                       COALESCE(
                           tablespace.spcname, 'pg_default')
                           AS tablespace
                FROM root
                JOIN pg_inherits inheritance
                  ON inheritance.inhparent = root.oid
                JOIN pg_class child
                  ON child.oid = inheritance.inhrelid
                LEFT JOIN pg_tablespace tablespace
                  ON tablespace.oid = child.reltablespace
            ),
            root_indexes AS (
                SELECT index_class.relname,
                       index_class.relkind,
                       metadata.indisprimary,
                       metadata.indisunique,
                       metadata.indisvalid,
                       pg_get_indexdef(index_class.oid) AS definition,
                       parent_index.relname AS parent_index
                FROM root
                JOIN pg_index metadata
                  ON metadata.indrelid = root.oid
                JOIN pg_class index_class
                  ON index_class.oid = metadata.indexrelid
                LEFT JOIN pg_inherits inheritance
                  ON inheritance.inhrelid = index_class.oid
                LEFT JOIN pg_class parent_index
                  ON parent_index.oid = inheritance.inhparent
            )
            SELECT json_build_object(
                'relationKind', (
                    SELECT relation.relkind
                    FROM root
                    JOIN pg_class relation
                      ON relation.oid = root.oid),
                'partitionKey', (
                    SELECT pg_get_partkeydef(root.oid)
                    FROM root),
                'partitionBound', (
                    SELECT pg_get_expr(
                        relation.relpartbound, relation.oid, true)
                    FROM root
                    JOIN pg_class relation
                      ON relation.oid = root.oid),
                'rootTablespace', (
                    SELECT COALESCE(
                        tablespace.spcname, 'pg_default')
                    FROM root
                    JOIN pg_class relation
                      ON relation.oid = root.oid
                    LEFT JOIN pg_tablespace tablespace
                      ON tablespace.oid = relation.reltablespace),
                'children', (
                    SELECT COALESCE(
                        json_agg(
                            json_build_object(
                                'name', relname,
                                'relationKind', relkind,
                                'partitionBound', partition_bound,
                                'tablespace', tablespace)
                            ORDER BY relname),
                        '[]'::json)
                    FROM children),
                'defaultRows', (
                    SELECT COUNT(*)::bigint
                    FROM {qualified(default_child_name(target))}),
                'indexes', (
                    SELECT COALESCE(
                        json_agg(
                            json_build_object(
                                'name', relname,
                                'relationKind', relkind,
                                'isPrimary', indisprimary,
                                'isUnique', indisunique,
                                'isValid', indisvalid,
                                'definition', definition,
                                'parentIndex', parent_index)
                            ORDER BY relname),
                        '[]'::json)
                    FROM root_indexes),
                'nonDefaultTablespaces', (
                    SELECT COUNT(*)::bigint
                    FROM (
                        SELECT root.oid
                        FROM root
                        UNION ALL
                        SELECT child.oid
                        FROM children child
                    ) tree
                    JOIN pg_class relation
                      ON relation.oid = tree.oid
                    LEFT JOIN pg_tablespace tablespace
                      ON tablespace.oid = relation.reltablespace
                    WHERE COALESCE(
                        tablespace.spcname, 'pg_default')
                        <> 'pg_default'))
    """


def parse_list_bound(bound):
    match = re.fullmatch(
            r"FOR VALUES IN \('?([0-9]+)'?\)",
            str(bound),
    )
    if not match:
            return None
    return int(match.group(1))


def validate_partition_shape(
    shape,
    target,
    protected_ids,
    *,
    attached=True,
):
    if (
            shape.get("relationKind") != "p"
            or shape.get("partitionKey") != "LIST (snapshot_id)"
            or (
                attached
                and shape.get("partitionBound") != target.bound
            )
            or (
                not attached
                and shape.get("partitionBound") is not None
            )
            or shape.get("rootTablespace") != "pg_default"
    ):
            raise MigrationError(
                "replacement root partition shape is unexpected"
            )
    if ensure_integer(
            shape.get("nonDefaultTablespaces"),
            "nonDefaultTablespaces",
    ):
            raise MigrationError(
                "replacement root or child is outside pg_default"
            )
    if ensure_integer(shape.get("defaultRows"), "defaultRows") != 0:
            raise MigrationError(
                "replacement DEFAULT partition is not empty"
            )
    observed_ids = set()
    default_count = 0
    expected_names = {
            generation_child_name(target, value)
            for value in protected_ids
    }
    for child in shape.get("children") or []:
            if (
                child.get("relationKind") != "r"
                or child.get("tablespace") != "pg_default"
            ):
                raise MigrationError(
                    "replacement child relation shape is unexpected"
                )
            if child.get("partitionBound") == "DEFAULT":
                default_count += 1
                if child.get("name") != default_child_name(target):
                    raise MigrationError(
                        "replacement DEFAULT child has an unexpected name"
                    )
                continue
            snapshot_id = parse_list_bound(child.get("partitionBound"))
            if snapshot_id is None:
                raise MigrationError(
                    "replacement child has a non-fixed snapshot bound"
                )
            if child.get("name") != generation_child_name(
                target, snapshot_id
            ):
                raise MigrationError(
                    "replacement child name does not match its snapshot ID"
                )
            observed_ids.add(snapshot_id)
    if observed_ids != set(protected_ids) or default_count != 1:
            raise MigrationError(
                "replacement children do not exactly match protected IDs "
                "plus one DEFAULT"
            )
    if expected_names != {
            child["name"]
            for child in shape.get("children") or []
            if child.get("partitionBound") != "DEFAULT"
    }:
            raise MigrationError(
                "replacement generation child names are incomplete"
            )
    indexes = shape.get("indexes") or []
    primary = [item for item in indexes if item.get("isPrimary")]
    score = [
            item
            for item in indexes
            if (
                not item.get("isPrimary")
                and "(snapshot_id, song_id, instrument, score DESC)"
                in item.get("definition", "")
            )
    ]
    if (
            len(primary) != 1
            or len(score) != 1
            or len(indexes) != 2
            or any(
                item.get("relationKind") != "I"
                or not item.get("isValid")
                for item in indexes
            )
    ):
            raise MigrationError(
                "replacement lacks the exact valid partitioned PK/score "
                "index shape"
            )
    if any(not item.get("parentIndex") for item in indexes):
            raise MigrationError(
                "replacement indexes are not attached to top-parent indexes"
            )
    return True


def validate_parent_index_attachments(shape, plan):
    names = plan["parentCatalogNames"]
    primary = [
        item
        for item in shape.get("indexes") or []
        if item.get("isPrimary")
    ]
    score = [
        item
        for item in shape.get("indexes") or []
        if (
            not item.get("isPrimary")
            and "(snapshot_id, song_id, instrument, score DESC)"
            in item.get("definition", "")
        )
    ]
    if (
        len(primary) != 1
        or len(score) != 1
        or primary[0].get("parentIndex") != names["primaryIndex"]
        or score[0].get("parentIndex") != names["scoreIndex"]
    ):
        raise MigrationError(
            "candidate indexes are not attached to the exact top-parent "
            "primary/score indexes"
        )
    return True


def calculate_archive_capacity(source_total_bytes, scratch_free_bytes):
    source = ensure_integer(
            source_total_bytes,
            "source total bytes",
            1,
    )
    free = ensure_integer(
            scratch_free_bytes,
            "scratch free bytes",
            0,
    )
    archive_budget = math.ceil(source * 1.10)
    restore_budget = math.ceil(source * 1.25) + 10 * 1024**3
    required = archive_budget + restore_budget + SCRATCH_RESERVE_BYTES
    return {
            "sourceTotalBytes": source,
            "archiveBudgetBytes": archive_budget,
            "restoreBudgetBytes": restore_budget,
            "reserveBytes": SCRATCH_RESERVE_BYTES,
            "requiredFreeBytes": required,
            "observedFreeBytes": free,
            "marginBytes": free - required,
            "allowed": free >= required,
    }


def calculate_build_capacity(
    source_total_bytes,
    exact_total_rows,
    retained_rows,
    fst_free_bytes,
):
    source = ensure_integer(
            source_total_bytes,
            "source total bytes",
            1,
    )
    total_rows = ensure_integer(
            exact_total_rows,
            "exact total rows",
            1,
    )
    retained = ensure_integer(
            retained_rows,
            "retained rows",
            1,
    )
    free = ensure_integer(fst_free_bytes, "FST free bytes", 0)
    if retained > total_rows:
            raise MigrationError(
                "retained rows exceed archive-restored total rows"
            )
    retained_ratio = retained / total_rows
    source_retained = math.ceil(source * retained_ratio)
    replacement = max(
            64 * 1024**2,
            math.ceil(source_retained * 1.50),
    )
    wal = max(
            512 * 1024**2,
            math.ceil(replacement * 1.50),
    )
    temp = math.ceil(replacement * 0.75)
    failure_reserve = replacement
    required = (
            EMERGENCY_FLOOR_BYTES
            + replacement
            + wal
            + temp
            + failure_reserve
    )
    return {
            "model": "accepted-pro-bass-live-profile-with-fixed-safety-margins",
            "retainedRowRatio": retained_ratio,
            "estimatedSourceRetainedBytes": source_retained,
            "estimatedReplacementBytes": replacement,
            "estimatedWalBytes": wal,
            "estimatedTempBytes": temp,
            "failureReserveBytes": failure_reserve,
            "emergencyFloorBytes": EMERGENCY_FLOOR_BYTES,
            "requiredFreeBytes": required,
            "observedFreeBytes": free,
            "marginBytes": free - required,
            "allowed": free >= required,
    }


def archive_manifest_path(root, target):
    return (
            pathlib.Path(root)
            / ARCHIVE_DIR
            / f"{target.key}-manifest.json"
    )


def load_archive_manifest(root, target, *, verify_checksum=True):
    manifest = read_json(archive_manifest_path(root, target))
    if (
            manifest.get("formatVersion") != FORMAT_VERSION
            or manifest.get("toolId") != TOOL_ID
            or manifest.get("targetKey") != target.key
            or manifest.get("target")
            != f"{TARGET_SCHEMA}.{target.partition}"
            or manifest.get("instrument") != target.instrument
    ):
            raise MigrationError(
                "archive manifest target identity is invalid"
            )
    archive = manifest.get("archive") or {}
    archive_path = pathlib.Path(str(archive.get("path", "")))
    if (
            not archive_path.is_file()
            or archive_path.is_symlink()
            or archive_path.stat().st_size
            != ensure_integer(archive.get("bytes"), "archive bytes", 1)
    ):
            raise MigrationError("archive file is unavailable or changed")
    if (
            verify_checksum
            and sha256_path(archive_path) != archive.get("sha256")
    ):
            raise MigrationError("archive checksum changed")
    before = (manifest.get("source") or {}).get("before")
    after = (manifest.get("source") or {}).get("after")
    if (
            not before
            or not after
            or not source_fence_matches(before, after)
            or manifest.get("sourceChangedDuringArchive") is not False
    ):
            raise MigrationError(
                "archive manifest lacks an unchanged source fence"
            )
    toc = manifest.get("toc") or {}
    toc_path = pathlib.Path(str(toc.get("path", "")))
    if (
            not toc_path.is_file()
            or toc_path.is_symlink()
            or sha256_path(toc_path) != toc.get("sha256")
    ):
            raise MigrationError("archive TOC evidence changed")
    return manifest


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
                        'snapshot migration advisory lock unavailable';
                END IF;
            END
            $guard$;
    """


def emergency_breach_path(root):
    return (
            pathlib.Path(root)
            / REPORTS_DIR
            / "emergency-floor-breach.json"
    )


def record_emergency_breach(
    root,
    *,
    filesystem,
    free_bytes,
    threshold_bytes,
):
    path = emergency_breach_path(root)
    value = {
            "formatVersion": FORMAT_VERSION,
            "toolId": TOOL_ID,
            "status": "blocked",
            "recordedAtUtc": utc_now(),
            "filesystem": filesystem,
            "freeBytes": free_bytes,
            "thresholdBytes": threshold_bytes,
            "applicationName": APPLICATION_NAME,
            "resumeAllowed": False,
    }
    if path.exists():
            existing = read_json(path)
            if existing.get("resumeAllowed") is not False:
                raise MigrationError(
                    "existing emergency breach evidence is invalid"
                )
            return existing
    write_json_exclusive(path, value)
    return value


def assert_no_emergency_breach(root):
    if emergency_breach_path(root).exists():
            raise MigrationError(
                "workspace recorded an emergency breach; use a new run "
                "after reconciling storage and PostgreSQL"
            )


def cancel_migration_backends(
    runner,
    args,
    root,
    *,
    filesystem,
    free_bytes,
    threshold_bytes,
):
    record_emergency_breach(
            root,
            filesystem=filesystem,
            free_bytes=free_bytes,
            threshold_bytes=threshold_bytes,
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
                return ensure_integer(
                    int(output),
                    "remaining migration backend count",
                )
            except ValueError as error:
                raise MigrationError(
                    "remaining backend count is not an integer"
                ) from error

    for attempt in range(10):
        if backend_count() == 0:
            return True
        function = (
            "pg_cancel_backend"
            if attempt < 4
            else "pg_terminate_backend"
        )
        runner.run(
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
        )
        time.sleep(0.5)
    if backend_count():
        raise MigrationError(
            "migration backends remain after emergency cancellation"
        )
    return True


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
                expected_by_route = {row["route"]: row for row in baseline}
                observed_by_route = {row["route"]: row for row in observed}
                if set(expected_by_route) != set(observed_by_route):
                    return False
                for route, expected in expected_by_route.items():
                    actual = observed_by_route[route]
                    if (
                        expected.get("comparison")
                        != actual.get("comparison")
                        or expected.get("status") != 200
                        or actual.get("status") != 200
                    ):
                        return False
                    if expected["comparison"] == "exact":
                        if expected != actual:
                            return False
                    elif expected.get("contentType") != actual.get("contentType"):
                        return False
                return True


def stage_check(args, runner, scratch_info, marker):
                target = TARGET_BY_KEY[args.instrument]
                host = collect_host_guard(args, runner)
                database = Database(
                    runner,
                    args.pg_container,
                    args.pg_user,
                    args.pg_database,
                )
                guard = collect_database_guard(database, target)
                target_state = guard["target"]
                if target_state["relationKind"] not in ("r", "p"):
                    raise MigrationError(
                        "fixed target is neither a regular nor partitioned table"
                    )
                already_subpartitioned = target_state["relationKind"] == "p"
                if already_subpartitioned:
                    shape = database.json(
                        partition_shape_query(target, target.partition)
                    )
                    if (
                        shape.get("partitionKey") != "LIST (snapshot_id)"
                        or shape.get("rootTablespace") != "pg_default"
                        or ensure_integer(
                            shape.get("nonDefaultTablespaces"),
                            "nonDefaultTablespaces",
                        )
                    ):
                        raise MigrationError(
                            "already-subpartitioned target has an unsafe shape"
                        )
                publication = guard["publication"]
                body = {
                    "runId": args.run_id,
                    "repositoryCommit": marker["repositoryCommit"],
                    "toolSourceSha256": marker["toolSourceSha256"],
                    "target": {
                        "key": target.key,
                        "parent": f"{TARGET_SCHEMA}.{TARGET_PARENT}",
                        "partition": f"{TARGET_SCHEMA}.{target.partition}",
                        "instrument": target.instrument,
                        "bound": target.bound,
                        "fixedAllowlistCount": len(TARGETS),
                        "arbitraryTargetInputAccepted": False,
                    },
                    "migrationState": (
                        "already_subpartitioned"
                        if already_subpartitioned
                        else "legacy_regular_partition"
                    ),
                    "scratch": scratch_info,
                    "host": host,
                    "databaseGuard": guard,
                    "identity": {
                        "postgresContainerId": host["postgresContainerId"],
                        "postgresImage": host["postgresImage"],
                        "pgdataMountSource": host["dataMount"]["source"],
                        "pgdataMountDestination": host["dataMount"][
                            "destination"
                        ],
                        "pgdataDeviceId": host["dataMount"]["filesystem"][
                            "deviceId"
                        ],
                        "database": guard["database"],
                        "databaseOid": guard["databaseOid"],
                        "systemIdentifier": guard["systemIdentifier"],
                        "parentOid": guard["parentCatalog"]["oid"],
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
                    "sourceRelationState": target_state,
                    "archiveLifecycle": {
                        "scratchIsTemporary": True,
                        "archiveMustExistAndRestoreBeforeDrop": True,
                        "archiveMustSurviveDrop": True,
                        "deletionRequiresSeparateDecision": True,
                    },
                    "acceptedStorage": {
                        "tablespace": "pg_default",
                        "filesystem": "4 TB FST PGDATA",
                        "scratchTablespaceAllowed": False,
                    },
                    "publicApiBaseline": (
                        capture_api_fingerprints(args.api_base)
                        if args.api_base
                        else []
                    ),
                }
                return write_stage_report(args.scratch_root, "check", body)


def stage_plan(args, runner):
                target = TARGET_BY_KEY[args.instrument]
                check = load_report(args.scratch_root, "check")
                if check["migrationState"] != "legacy_regular_partition":
                    raise MigrationError(
                        "target is already snapshot-ID subpartitioned; the accepted "
                        "pro-bass pilot or a prior migration owns that state"
                    )
                host = collect_host_guard(args, runner)
                database = Database(
                    runner,
                    args.pg_container,
                    args.pg_user,
                    args.pg_database,
                )
                guard = collect_database_guard(database, target)
                assert_identity_matches(check, host, guard)
                source_state = guard["target"]
                if source_state["relationKind"] != "r":
                    raise MigrationError(
                        "planning requires the original regular instrument partition"
                    )
                if (
                    physical_relation_identity(source_state)
                    != physical_relation_identity(check["sourceRelationState"])
                ):
                    raise MigrationError(
                        "source partition changed after the check stage"
                    )
                inventory = database.json(
                    inventory_query(target),
                    timeout=args.query_timeout_seconds,
                    pgoptions=LOOSE_ID_PGOPTIONS,
                )
                protected_result = database.json(
                    protected_sources_query(target),
                    timeout=args.query_timeout_seconds,
                    pgoptions=PLAN_QUERY_PGOPTIONS,
                )
                protected_ids = derive_protected_ids(
                    protected_result,
                    inventory,
                )
                predicate = snapshot_id_predicate(protected_ids)
                retained_distribution = database.json(
                    snapshot_distribution_query(
                        target.partition,
                        predicate,
                    ),
                    timeout=args.query_timeout_seconds,
                    pgoptions=PLAN_QUERY_PGOPTIONS,
                )
                retained_fingerprint = database.json(
                    fingerprint_sql(target.partition, predicate),
                    timeout=args.query_timeout_seconds,
                    pgoptions=PLAN_QUERY_PGOPTIONS,
                )
                exact_retained_rows = ensure_integer(
                    retained_fingerprint.get("rowCount"),
                    "exact retained row count",
                    1,
                )
                if {
                    row["snapshotId"] for row in retained_distribution
                } != set(protected_ids):
                    raise MigrationError(
                        "retained distribution does not cover every protected ID"
                    )
                reference = database.json(
                    reference_parity_query(target, target.partition),
                    timeout=args.query_timeout_seconds,
                    pgoptions=PLAN_QUERY_PGOPTIONS,
                )
                assert_reference_parity(reference)
                catalog = database.json(catalog_query(target.partition))
                if (
                    catalog.get("relationKind") != "r"
                    or catalog.get("partitionKey") is not None
                    or catalog.get("partitionBound") != target.bound
                    or catalog.get("tablespace") != "pg_default"
                ):
                    raise MigrationError(
                        "source catalog is not the expected regular partition"
                    )
                observed_columns = tuple(
                    item.get("name")
                    for item in catalog.get("columns") or []
                )
                parent_columns = tuple(
                    item.get("name")
                    for item in guard["parentCatalog"].get("columns") or []
                )
                if (
                    observed_columns != SNAPSHOT_COLUMNS
                    or parent_columns != SNAPSHOT_COLUMNS
                    or catalog["columns"]
                    != guard["parentCatalog"]["columns"]
                ):
                    raise MigrationError(
                        "source/parent snapshot columns differ from the "
                        "fixed 23-column migration contract"
                    )
                catalog_names = source_catalog_names(catalog)
                parent_catalog_names = source_catalog_names(
                    guard["parentCatalog"]
                )
                source_index_parents = {
                    item["name"]: item.get("parentIndex")
                    for item in catalog["indexes"]
                }
                if (
                    source_index_parents.get(
                        catalog_names["primaryIndex"]
                    )
                    != parent_catalog_names["primaryIndex"]
                    or source_index_parents.get(
                        catalog_names["scoreIndex"]
                    )
                    != parent_catalog_names["scoreIndex"]
                ):
                    raise MigrationError(
                        "source indexes are not attached to the exact "
                        "top-parent primary/score indexes"
                    )
                scratch_usage = shutil.disk_usage(args.scratch_root)
                archive_capacity = calculate_archive_capacity(
                    source_state["totalBytes"],
                    scratch_usage.free,
                )
                if not archive_capacity["allowed"]:
                    raise MigrationError(
                        "archive/restore scratch capacity gate failed: "
                        f"need {archive_capacity['requiredFreeBytes']} bytes, "
                        f"observed {archive_capacity['observedFreeBytes']}"
                    )
                plan_identity = {
                    "sourceFence": source_fence_from_state(source_state),
                    "sourceRelationIdentity": physical_relation_identity(
                        source_state
                    ),
                    "inventorySnapshotIds": inventory,
                    "protectedSnapshotIds": protected_ids,
                    "exactRetainedRows": exact_retained_rows,
                    "retainedFingerprint": retained_fingerprint,
                    "retainedDistribution": normalized_distribution(
                        retained_distribution
                    ),
                    "referenceParity": reference,
                }
                plan_id = sha256_bytes(canonical_json_bytes(plan_identity))
                body = {
                    "runId": args.run_id,
                    "planId": plan_id,
                    "targetKey": target.key,
                    "target": f"{TARGET_SCHEMA}.{target.partition}",
                    "instrument": target.instrument,
                    "sourceState": source_state,
                    "sourceCatalog": catalog,
                    "sourceCatalogNames": catalog_names,
                    "parentCatalogNames": parent_catalog_names,
                    "protectedSources": protected_result,
                    "planIdentity": plan_identity,
                    "archiveCapacityGate": archive_capacity,
                    "replacement": {
                        "relation": (
                            f"{TARGET_SCHEMA}."
                            f"{replacement_name(args.run_id, target)}"
                        ),
                        "partitionKey": "LIST (snapshot_id)",
                        "generationChildren": [
                            generation_child_name(target, snapshot_id)
                            for snapshot_id in protected_ids
                        ],
                        "defaultChild": default_child_name(target),
                        "tablespace": "pg_default",
                    },
                    "rollbackRelation": (
                        f"{TARGET_SCHEMA}.{retired_name(args.run_id, target)}"
                    ),
                    "executable": True,
                }
                return write_stage_report(args.scratch_root, "plan", body)


def pg_dump_command(args, target):
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
                    "--no-owner",
                    "--no-privileges",
                    "-U",
                    args.pg_user,
                    "-d",
                    args.pg_database,
                    "--table",
                    f"{TARGET_SCHEMA}.{TARGET_PARENT}",
                    "--table",
                    f"{TARGET_SCHEMA}.{target.partition}",
                ]


def verify_archive_toc(toc, target, source_catalog):
                required_tokens = (
                    f"TABLE public {TARGET_PARENT}",
                    f"TABLE public {target.partition}",
                    f"TABLE DATA public {target.partition}",
                    source_catalog["primaryIndex"],
                    source_catalog["scoreIndex"],
                )
                missing = [token for token in required_tokens if token not in toc]
                if missing:
                    raise MigrationError(
                        "custom archive TOC lacks fixed target entries: "
                        + ", ".join(missing)
                    )
                for other in TARGETS:
                    if (
                        other != target
                        and f"TABLE DATA public {other.partition}" in toc
                    ):
                        raise MigrationError(
                            "custom archive unexpectedly includes another "
                            "instrument partition"
                        )
                return True


def stage_archive(args, runner):
                if not args.execute:
                    raise MigrationError("archive requires --execute")
                assert_no_emergency_breach(args.scratch_root)
                target = TARGET_BY_KEY[args.instrument]
                check = load_report(args.scratch_root, "check")
                plan = load_report(args.scratch_root, "plan")
                host = collect_host_guard(args, runner)
                database = Database(
                    runner,
                    args.pg_container,
                    args.pg_user,
                    args.pg_database,
                )
                guard = collect_database_guard(database, target)
                assert_identity_matches(check, host, guard)
                source_state = guard["target"]
                if (
                    physical_relation_identity(source_state)
                    != plan["planIdentity"]["sourceRelationIdentity"]
                ):
                    raise MigrationError(
                        "source partition changed after exact planning"
                    )
                current_capacity = calculate_archive_capacity(
                    source_state["totalBytes"],
                    shutil.disk_usage(args.scratch_root).free,
                )
                archive_root = pathlib.Path(args.scratch_root) / ARCHIVE_DIR
                archive_path = archive_root / f"{target.key}-original.custom"
                toc_path = archive_root / f"{target.key}-original.list"
                catalog_path = archive_root / f"{target.key}-source-catalog.json"
                start_path = (
                    pathlib.Path(args.scratch_root)
                    / REPORTS_DIR
                    / "archive.started.json"
                )
                start = {
                    "formatVersion": FORMAT_VERSION,
                    "toolId": TOOL_ID,
                    "runId": args.run_id,
                    "planId": plan["planId"],
                    "targetKey": target.key,
                    "startedAtUtc": utc_now(),
                    "sourceFence": source_fence_from_state(source_state),
                    "archivePath": str(archive_path),
                    "capacityGate": current_capacity,
                }
                start, resumed_start = load_or_create_started_evidence(
                    start_path,
                    start,
                    (
                        "toolId",
                        "runId",
                        "planId",
                        "targetKey",
                        "sourceFence",
                        "archivePath",
                    ),
                )
                resumed = archive_path.is_file()
                capacity = start.get("capacityGate") or {}
                if not capacity.get("allowed"):
                    raise MigrationError(
                        "persisted archive/restore scratch capacity gate "
                        "was not allowed"
                    )
                if resumed and not resumed_start:
                    raise MigrationError(
                        "archive exists without durable start evidence"
                    )
                if not resumed and not current_capacity["allowed"]:
                    raise MigrationError(
                        "current archive/restore scratch capacity gate failed"
                    )
                if resumed:
                    metadata = archive_path.lstat()
                    if archive_path.is_symlink() or not stat.S_ISREG(
                        metadata.st_mode
                    ):
                        raise MigrationError(
                            "existing archive is not a regular file"
                        )
                started = time.monotonic()
                monitor = FilesystemMonitor(
                    args.scratch_root,
                    SCRATCH_RESERVE_BYTES,
                    lambda free: cancel_migration_backends(
                        runner,
                        args,
                        args.scratch_root,
                        filesystem="scratch",
                        free_bytes=free,
                        threshold_bytes=SCRATCH_RESERVE_BYTES,
                    ),
                )
                with monitor:
                    if not resumed:
                        runner.run_to_file(
                            pg_dump_command(args, target),
                            archive_path,
                            timeout=args.archive_timeout_seconds,
                        )
                if monitor.breached:
                    raise MigrationError(
                        "archive was cancelled at the scratch reserve"
                    )
                if archive_path.stat().st_size <= 0:
                    raise MigrationError("pg_dump archive is empty")
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
                        f"/archive/{archive_path.name}",
                    ],
                    timeout=600,
                ).stdout
                verify_archive_toc(
                    listing,
                    target,
                    plan["sourceCatalogNames"],
                )
                write_or_verify_bytes(toc_path, listing.encode("utf-8"))
                write_or_verify_json(catalog_path, plan["sourceCatalog"])
                post_state = database.json(
                    relation_state_query(target.partition)
                )
                before_fence = source_fence_from_state(source_state)
                after_fence = source_fence_from_state(post_state)
                if not source_fence_matches(before_fence, after_fence):
                    raise MigrationError(
                        "source partition changed while archive streamed"
                    )
                manifest = {
                    "formatVersion": FORMAT_VERSION,
                    "toolId": TOOL_ID,
                    "runId": args.run_id,
                    "planId": plan["planId"],
                    "targetKey": target.key,
                    "target": f"{TARGET_SCHEMA}.{target.partition}",
                    "instrument": target.instrument,
                    "createdAtUtc": utc_now(),
                    "archive": {
                        "path": str(archive_path),
                        "bytes": archive_path.stat().st_size,
                        "sha256": sha256_path(archive_path),
                        "format": "PostgreSQL custom",
                        "compression": 6,
                        "elapsedSeconds": round(
                            time.monotonic() - started,
                            3,
                        ),
                    },
                    "toc": {
                        "path": str(toc_path),
                        "sha256": sha256_path(toc_path),
                    },
                    "catalog": {
                        "path": str(catalog_path),
                        "sha256": sha256_path(catalog_path),
                    },
                    "source": {
                        "before": before_fence,
                        "after": after_fence,
                        "protectedSnapshotIds": plan["planIdentity"][
                            "protectedSnapshotIds"
                        ],
                        "retainedFingerprint": plan["planIdentity"][
                            "retainedFingerprint"
                        ],
                    },
                    "sourceChangedDuringArchive": False,
                    "restoreRequiredBeforeDrop": True,
                    "retention": {
                        "retainAfterFinalDrop": True,
                        "deleteOnlyAfterSeparateOperatorDecision": True,
                    },
                }
                manifest_path = archive_manifest_path(args.scratch_root, target)
                if manifest_path.exists():
                    verified = load_archive_manifest(args.scratch_root, target)
                    if (
                        verified.get("runId") != args.run_id
                        or verified.get("planId") != plan["planId"]
                        or not source_fence_matches(
                            verified["source"]["before"],
                            before_fence,
                        )
                        or verified["archive"]["sha256"]
                        != sha256_path(archive_path)
                    ):
                        raise MigrationError(
                            "existing archive manifest differs from this run"
                        )
                else:
                    write_json_exclusive(manifest_path, manifest)
                    verified = load_archive_manifest(args.scratch_root, target)
                body = {
                    "runId": args.run_id,
                    "planId": plan["planId"],
                    "targetKey": target.key,
                    "archiveManifest": {
                        "path": str(manifest_path),
                        "sha256": sha256_path(manifest_path),
                    },
                    "archive": verified["archive"],
                    "sourceFenceBefore": before_fence,
                    "sourceFenceAfter": after_fence,
                    "sourceChangedDuringArchive": False,
                    "scratchCapacityGate": capacity,
                    "scratchMonitor": {
                        "samples": monitor.samples,
                        "minimumFreeBytes": monitor.minimum_free_bytes,
                        "thresholdBytes": SCRATCH_RESERVE_BYTES,
                        "breached": monitor.breached,
                    },
                    "resumedExistingArchive": resumed,
                    "resumedArchiveStartEvidence": resumed_start,
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
                        version = runner.run(
                            [
                                "docker",
                                "exec",
                                container,
                                "psql",
                                "-X",
                                "-U",
                                user,
                                "-d",
                                database,
                                "-At",
                                "-c",
                                "SHOW server_version_num",
                            ],
                            timeout=15,
                            check=False,
                        )
                        if (
                            version.returncode == 0
                            and version.stdout.strip().isdigit()
                            and int(version.stdout.strip()) // 10000
                            == POSTGRES_MAJOR
                        ):
                            consecutive_successes += 1
                        else:
                            consecutive_successes = 0
                        if consecutive_successes >= 3:
                            return
                    else:
                        consecutive_successes = 0
                    time.sleep(1)
                raise MigrationError(
                    f"isolated PostgreSQL 17 container {container} did not become ready"
                )


def cleanup_owned_directory(runner, image, directory):
                directory = pathlib.Path(directory)
                if not directory.exists():
                    return
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
                            f"chown {os.getuid()}:{os.getgid()} /owned && "
                            "chmod 700 /owned"
                        ),
                    ],
                    timeout=1800,
                )


def restore_container_name(run_id, target):
                return ensure_safe_identifier(
                    "fst_sgm_" + target.slug + "_" + relation_token(run_id, target),
                    "restore container token",
                ).replace("_", "-")


def remove_owned_restore_container(
                runner,
                container,
                run_id,
                target,
):
                inspected = inspect_container(
                    runner,
                    container,
                    allow_missing=True,
                )
                if inspected is None:
                    return False
                labels = (inspected.get("Config") or {}).get("Labels") or {}
                expected = {
                    "fst.tool": TOOL_ID,
                    "fst.run": run_id,
                    "fst.target": target.key,
                }
                if any(labels.get(key) != value for key, value in expected.items()):
                    raise MigrationError(
                        "deterministic restore container is not owned by this run"
                    )
                runner.run(["docker", "rm", "-f", container], timeout=120)
                return True


def restore_evidence_paths(root, target):
                restore_root = pathlib.Path(root) / RESTORE_DIR
                return {
                    "validation": (
                        restore_root / f"{target.key}-restore-validation.json"
                    ),
                    "distribution": (
                        restore_root / f"{target.key}-snapshot-distribution.json"
                    ),
                    "catalog": restore_root / f"{target.key}-catalog.json",
                    "cleanup": restore_root / f"{target.key}-cleanup-proof.json",
                }


def quarantine_partial_restore_evidence(root, target, paths):
                existing = [path for path in paths.values() if path.exists()]
                if not existing:
                    return False
                recovered_root = pathlib.Path(root) / RECOVERED_DIR
                timestamp = datetime.now(timezone.utc).strftime(
                    "%Y%m%dT%H%M%S%fZ"
                )
                lane = recovered_root / f"restore-partial-{timestamp}"
                lane.mkdir(mode=0o700)
                for path in existing:
                    metadata = path.lstat()
                    if path.is_symlink() or not stat.S_ISREG(metadata.st_mode):
                        raise MigrationError(
                            f"partial restore evidence is not a regular file: {path}"
                        )
                    os.replace(path, lane / path.name)
                fsync_directory(lane)
                fsync_directory(pathlib.Path(root) / RESTORE_DIR)
                write_json_exclusive(
                    recovered_root / f"restore.recovery-{timestamp}.json",
                    {
                        "formatVersion": FORMAT_VERSION,
                        "toolId": TOOL_ID,
                        "targetKey": target.key,
                        "recoveredAtUtc": utc_now(),
                        "reason": "partial restore evidence set",
                        "directory": str(lane),
                        "files": sorted(path.name for path in existing),
                        "stageWillRepeatIsolatedRestore": True,
                    },
                )
                return True


def load_existing_restore_evidence(root, target, args, plan, manifest):
                paths = restore_evidence_paths(root, target)
                present = {key: path.exists() for key, path in paths.items()}
                if not any(present.values()):
                    return None
                if not all(present.values()):
                    quarantine_partial_restore_evidence(root, target, paths)
                    return None
                validation = read_json(paths["validation"])
                distribution = read_json(paths["distribution"])
                catalog = read_json(paths["catalog"])
                cleanup = read_json(paths["cleanup"])
                if (
                    validation.get("toolId") != TOOL_ID
                    or validation.get("runId") != args.run_id
                    or validation.get("planId") != plan["planId"]
                    or validation.get("targetKey") != target.key
                    or validation.get("archiveSha256")
                    != manifest["archive"]["sha256"]
                    or validation.get("networkMode") != "none"
                    or validation.get("postgresMajor") != POSTGRES_MAJOR
                    or validation.get("distribution") != distribution
                    or validation.get("catalog") != catalog
                ):
                    raise MigrationError(
                        "existing restore validation identity is inconsistent"
                    )
                if (
                    cleanup.get("validationPath") != str(paths["validation"])
                    or cleanup.get("validationSha256")
                    != sha256_path(paths["validation"])
                    or cleanup.get("containerRemoved") is not True
                    or cleanup.get("restorePgdataRemoved") is not True
                    or cleanup.get("archiveRetained") is not True
                ):
                    raise MigrationError(
                        "existing restore cleanup proof is inconsistent"
                    )
                if normalized_distribution(distribution) != normalized_distribution(
                    validation["distribution"]
                ):
                    raise MigrationError(
                        "existing restore distribution evidence changed"
                    )
                retained = [
                    row
                    for row in distribution
                    if row["snapshotId"] in set(
                        plan["planIdentity"]["protectedSnapshotIds"]
                    )
                ]
                if normalized_distribution(retained) != plan["planIdentity"][
                    "retainedDistribution"
                ]:
                    raise MigrationError(
                        "existing restore protected distribution differs from plan"
                    )
                return {
                    "runId": args.run_id,
                    "planId": plan["planId"],
                    "targetKey": target.key,
                    "archive": manifest["archive"],
                    "restoreValidation": {
                        "path": str(paths["validation"]),
                        "sha256": sha256_path(paths["validation"]),
                    },
                    "cleanupProof": {
                        "path": str(paths["cleanup"]),
                        "sha256": sha256_path(paths["cleanup"]),
                    },
                    "exactRows": validation["exactRows"],
                    "restoredFingerprint": validation["fingerprint"],
                    "restoredCatalog": validation["catalog"],
                    "restoredDistribution": validation["distribution"],
                    "restorePgdataPeakBytes": validation["restorePgdataBytes"],
                    "networkMode": "none",
                    "postgresMajor": POSTGRES_MAJOR,
                    "scratchMonitor": {
                        "resumedFromCompleteEvidence": True,
                        "breached": False,
                    },
                    "recoveredStaleContainer": False,
                    "archiveRetained": True,
                    "resumedCompleteRestoreEvidence": True,
                }


def stage_restore(args, runner):
                if not args.execute:
                    raise MigrationError("restore requires --execute")
                assert_no_emergency_breach(args.scratch_root)
                target = TARGET_BY_KEY[args.instrument]
                check = load_report(args.scratch_root, "check")
                plan = load_report(args.scratch_root, "plan")
                load_report(args.scratch_root, "archive")
                host = collect_host_guard(args, runner)
                database = Database(
                    runner,
                    args.pg_container,
                    args.pg_user,
                    args.pg_database,
                )
                guard = collect_database_guard(database, target)
                assert_identity_matches(check, host, guard)
                if (
                    physical_relation_identity(guard["target"])
                    != plan["planIdentity"]["sourceRelationIdentity"]
                ):
                    raise MigrationError(
                        "source partition changed before archive restore proof"
                    )
                manifest = load_archive_manifest(args.scratch_root, target)
                existing_body = load_existing_restore_evidence(
                    args.scratch_root,
                    target,
                    args,
                    plan,
                    manifest,
                )
                if existing_body is not None:
                    return write_stage_report(
                        args.scratch_root,
                        "restore",
                        existing_body,
                    )
                archive_path = pathlib.Path(manifest["archive"]["path"])
                restore_root = pathlib.Path(args.scratch_root) / RESTORE_DIR
                data_root = restore_root / "pgdata"
                data_root.mkdir(mode=0o700, exist_ok=True)
                image = args.restore_image or host["postgresImage"]
                container = restore_container_name(args.run_id, target)
                removed_stale_container = remove_owned_restore_container(
                    runner,
                    container,
                    args.run_id,
                    target,
                )
                if any(data_root.iterdir()):
                    cleanup_owned_directory(runner, image, data_root)
                before_usage = shutil.disk_usage(args.scratch_root)
                monitor = FilesystemMonitor(
                    args.scratch_root,
                    SCRATCH_RESERVE_BYTES,
                    lambda free: (
                        record_emergency_breach(
                            args.scratch_root,
                            filesystem="scratch-restore",
                            free_bytes=free,
                            threshold_bytes=SCRATCH_RESERVE_BYTES,
                        ),
                        runner.run(
                            ["docker", "stop", "--time", "1", container],
                            timeout=30,
                            check=False,
                        ),
                    ),
                )
                evidence_paths = restore_evidence_paths(args.scratch_root, target)
                validation_path = evidence_paths["validation"]
                distribution_path = evidence_paths["distribution"]
                catalog_path = evidence_paths["catalog"]
                cleanup_path = evidence_paths["cleanup"]
                started = time.monotonic()
                container_started = False
                restored_fingerprint = None
                restored_distribution = None
                restored_catalog = None
                restored_state = None
                restore_bytes = None
                try:
                    runner.run(
                        [
                            "docker",
                            "run",
                            "--name",
                            container,
                            "--detach",
                            "--network",
                            "none",
                            "--label",
                            f"fst.tool={TOOL_ID}",
                            "--label",
                            f"fst.run={args.run_id}",
                            "--label",
                            f"fst.target={target.key}",
                            "-e",
                            "POSTGRES_HOST_AUTH_METHOD=trust",
                            "-e",
                            f"POSTGRES_USER={args.pg_user}",
                            "-e",
                            f"POSTGRES_DB={args.pg_database}",
                            "-v",
                            f"{data_root}:/var/lib/postgresql/data",
                            "-v",
                            f"{archive_path.parent}:/archive:ro",
                            image,
                        ],
                        timeout=120,
                    )
                    container_started = True
                    with monitor:
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
                                "--no-owner",
                                "--no-privileges",
                                "-U",
                                args.pg_user,
                                "-d",
                                args.pg_database,
                                f"/archive/{archive_path.name}",
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
                            fingerprint_sql(target.partition),
                            timeout=args.query_timeout_seconds,
                        )
                        restored_distribution = restored.json(
                            snapshot_distribution_query(target.partition),
                            timeout=args.query_timeout_seconds,
                        )
                        restored_catalog = restored.json(
                            catalog_query(target.partition)
                        )
                        restored_state = restored.json(
                            relation_state_query(target.partition)
                        )
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
                    if monitor.breached:
                        raise MigrationError(
                            "isolated restore crossed the scratch reserve"
                        )
                    if (
                        restored_catalog.get("relationKind") != "r"
                        or catalog_semantic_shape(restored_catalog)
                        != catalog_semantic_shape(plan["sourceCatalog"])
                    ):
                        raise MigrationError(
                            "archive-restored catalog differs from the source catalog"
                        )
                    restored_ids = [
                        row["snapshotId"] for row in restored_distribution
                    ]
                    if restored_ids != plan["planIdentity"][
                        "inventorySnapshotIds"
                    ]:
                        raise MigrationError(
                            "archive-restored snapshot IDs differ from the plan"
                        )
                    retained_rows = [
                        row
                        for row in restored_distribution
                        if row["snapshotId"] in set(
                            plan["planIdentity"]["protectedSnapshotIds"]
                        )
                    ]
                    if normalized_distribution(retained_rows) != plan[
                        "planIdentity"
                    ]["retainedDistribution"]:
                        raise MigrationError(
                            "archive-restored protected rows differ from the plan"
                        )
                    exact_total_rows = ensure_integer(
                        restored_fingerprint.get("rowCount"),
                        "restored exact row count",
                        1,
                    )
                    if sum(
                        ensure_integer(
                            row.get("rowCount"),
                            "restored per-snapshot row count",
                            1,
                        )
                        for row in restored_distribution
                    ) != exact_total_rows:
                        raise MigrationError(
                            "archive-restored distribution does not cover all rows"
                        )
                    validation = {
                        "formatVersion": FORMAT_VERSION,
                        "toolId": TOOL_ID,
                        "runId": args.run_id,
                        "planId": plan["planId"],
                        "targetKey": target.key,
                        "archiveSha256": manifest["archive"]["sha256"],
                        "networkMode": "none",
                        "postgresMajor": POSTGRES_MAJOR,
                        "fingerprint": restored_fingerprint,
                        "distribution": normalized_distribution(
                            restored_distribution
                        ),
                        "catalog": restored_catalog,
                        "relationState": restored_state,
                        "exactRows": {
                            "total": exact_total_rows,
                            "retained": plan["planIdentity"][
                                "exactRetainedRows"
                            ],
                            "purge": (
                                exact_total_rows
                                - plan["planIdentity"]["exactRetainedRows"]
                            ),
                        },
                        "restorePgdataBytes": restore_bytes,
                        "elapsedSeconds": round(
                            time.monotonic() - started,
                            3,
                        ),
                    }
                    write_or_verify_json(distribution_path, validation["distribution"])
                    write_or_verify_json(catalog_path, restored_catalog)
                    validation["distributionEvidence"] = {
                        "path": str(distribution_path),
                        "sha256": sha256_path(distribution_path),
                    }
                    validation["catalogEvidence"] = {
                        "path": str(catalog_path),
                        "sha256": sha256_path(catalog_path),
                    }
                    write_or_verify_json(validation_path, validation)
                finally:
                    if container_started:
                        remove_owned_restore_container(
                            runner,
                            container,
                            args.run_id,
                            target,
                        )
                    cleanup_owned_directory(runner, image, data_root)
                if any(data_root.iterdir()):
                    raise MigrationError(
                        "isolated restore PGDATA was not cleaned"
                    )
                data_root.rmdir()
                fsync_directory(restore_root)
                after_usage = shutil.disk_usage(args.scratch_root)
                cleanup = {
                    "formatVersion": FORMAT_VERSION,
                    "toolId": TOOL_ID,
                    "runId": args.run_id,
                    "targetKey": target.key,
                    "container": container,
                    "containerRemoved": (
                        inspect_container(
                            runner,
                            container,
                            allow_missing=True,
                        )
                        is None
                    ),
                    "restorePgdataRemoved": not data_root.exists(),
                    "archiveRetained": (
                        archive_path.is_file()
                        and sha256_path(archive_path)
                        == manifest["archive"]["sha256"]
                    ),
                    "validationPath": str(validation_path),
                    "validationSha256": sha256_path(validation_path),
                    "scratchFreeBytesBefore": before_usage.free,
                    "scratchFreeBytesAfter": after_usage.free,
                }
                if (
                    not cleanup["containerRemoved"]
                    or not cleanup["archiveRetained"]
                ):
                    raise MigrationError(
                        "archive restore cleanup proof is incomplete"
                    )
                write_or_verify_json(cleanup_path, cleanup)
                validation = read_json(validation_path)
                body = {
                    "runId": args.run_id,
                    "planId": plan["planId"],
                    "targetKey": target.key,
                    "archive": manifest["archive"],
                    "restoreValidation": {
                        "path": str(validation_path),
                        "sha256": sha256_path(validation_path),
                    },
                    "cleanupProof": {
                        "path": str(cleanup_path),
                        "sha256": sha256_path(cleanup_path),
                    },
                    "exactRows": validation["exactRows"],
                    "restoredFingerprint": validation["fingerprint"],
                    "restoredCatalog": validation["catalog"],
                    "restoredDistribution": validation["distribution"],
                    "restorePgdataPeakBytes": restore_bytes,
                    "networkMode": "none",
                    "postgresMajor": POSTGRES_MAJOR,
                    "scratchMonitor": {
                        "samples": monitor.samples,
                        "minimumFreeBytes": monitor.minimum_free_bytes,
                        "thresholdBytes": SCRATCH_RESERVE_BYTES,
                        "breached": monitor.breached,
                    },
                    "recoveredStaleContainer": removed_stale_container,
                    "archiveRetained": True,
                }
                return write_stage_report(args.scratch_root, "restore", body)


def build_sql(args, target, plan):
                replacement = replacement_name(args.run_id, target)
                primary_name = replacement_primary_name(args.run_id, target)
                score_name = replacement_score_name(args.run_id, target)
                check_name = replacement_instrument_check_name(
                    args.run_id,
                    target,
                )
                owner = plan["sourceCatalog"]["owner"]
                if not re.fullmatch(r"[A-Za-z_][A-Za-z0-9_$]*", owner):
                    raise MigrationError(f"unsafe target owner name: {owner}")
                protected_ids = plan["planIdentity"]["protectedSnapshotIds"]
                child_sql = "\n".join(
                    (
                        f"CREATE TABLE {qualified(generation_child_name(target, value))} "
                        f"PARTITION OF {qualified(replacement)} "
                        f"FOR VALUES IN ({value}) TABLESPACE pg_default;"
                    )
                    for value in protected_ids
                )
                columns = ", ".join(f'"{column}"' for column in SNAPSHOT_COLUMNS)
                values = ", ".join(str(value) for value in protected_ids)
                return f"""
                    BEGIN;
                    SET LOCAL lock_timeout = '2s';
                    SET LOCAL statement_timeout = '0';
                    SET LOCAL temp_tablespaces = 'pg_default';
                    {advisory_lock_guard_sql()}
                    CREATE TABLE {qualified(replacement)}
                        (LIKE {qualified(target.partition)}
                            INCLUDING DEFAULTS
                            INCLUDING STORAGE
                            INCLUDING GENERATED
                            INCLUDING IDENTITY)
                        PARTITION BY LIST (snapshot_id);
                    ALTER TABLE {qualified(replacement)}
                        OWNER TO "{owner}";
                    ALTER TABLE {qualified(replacement)}
                        ADD CONSTRAINT "{check_name}"
                        CHECK (instrument = {sql_literal(target.instrument)});
                    {child_sql}
                    CREATE TABLE {qualified(default_child_name(target))}
                        PARTITION OF {qualified(replacement)}
                        DEFAULT TABLESPACE pg_default;
                    INSERT INTO {qualified(replacement)} ({columns})
                    SELECT {columns}
                    FROM {qualified(target.partition)}
                    WHERE snapshot_id = ANY(
                        ARRAY[{values}]::bigint[]);
                    ALTER TABLE {qualified(replacement)}
                        ADD CONSTRAINT "{primary_name}"
                        PRIMARY KEY (
                            snapshot_id, song_id, instrument, account_id);
                    CREATE INDEX "{score_name}"
                        ON {qualified(replacement)}
                        (snapshot_id, song_id, instrument, score DESC);
                    ANALYZE {qualified(replacement)};
                    COMMIT;
                """


def stage_build(args, runner):
                if not args.execute:
                    raise MigrationError("build requires --execute")
                assert_no_emergency_breach(args.scratch_root)
                target = TARGET_BY_KEY[args.instrument]
                check = load_report(args.scratch_root, "check")
                plan = load_report(args.scratch_root, "plan")
                restore = load_report(args.scratch_root, "restore")
                host = collect_host_guard(args, runner)
                database = Database(
                    runner,
                    args.pg_container,
                    args.pg_user,
                    args.pg_database,
                )
                allowed = expected_artifact_names(
                    args.run_id,
                    target,
                    plan["planIdentity"]["protectedSnapshotIds"],
                )
                guard = collect_database_guard(
                    database,
                    target,
                    allowed_artifacts=allowed,
                )
                assert_identity_matches(check, host, guard)
                if (
                    physical_relation_identity(guard["target"])
                    != plan["planIdentity"]["sourceRelationIdentity"]
                ):
                    raise MigrationError(
                        "source partition changed before replacement build"
                    )
                manifest = load_archive_manifest(args.scratch_root, target)
                if (
                    manifest["archive"]["sha256"]
                    != restore["archive"]["sha256"]
                ):
                    raise MigrationError(
                        "archive and restore reports refer to different archives"
                    )
                current_capacity = calculate_build_capacity(
                    plan["sourceState"]["totalBytes"],
                    restore["exactRows"]["total"],
                    plan["planIdentity"]["exactRetainedRows"],
                    host["dataFilesystem"]["freeBytes"],
                )
                replacement = replacement_name(args.run_id, target)
                existing = database.scalar(
                    "SELECT to_regclass("
                    f"{sql_literal('public.' + replacement)}) IS NOT NULL"
                )
                resumed = existing == "t"
                start_path = (
                    pathlib.Path(args.scratch_root)
                    / REPORTS_DIR
                    / "build.started.json"
                )
                if resumed and not start_path.exists():
                    raise MigrationError(
                        "replacement exists without durable build-start "
                        "evidence"
                    )
                start = {
                    "formatVersion": FORMAT_VERSION,
                    "toolId": TOOL_ID,
                    "runId": args.run_id,
                    "planId": plan["planId"],
                    "targetKey": target.key,
                    "startedAtUtc": utc_now(),
                    "sourceFence": plan["planIdentity"]["sourceFence"],
                    "capacityGate": current_capacity,
                    "replacement": f"{TARGET_SCHEMA}.{replacement}",
                    "tablespace": "pg_default",
                }
                start, resumed_start = load_or_create_started_evidence(
                    start_path,
                    start,
                    (
                        "toolId",
                        "runId",
                        "planId",
                        "targetKey",
                        "sourceFence",
                        "replacement",
                        "tablespace",
                    ),
                )
                capacity = start["capacityGate"]
                if not start.get("capacityGate", {}).get("allowed"):
                    raise MigrationError(
                        "persisted build-start capacity gate was not allowed"
                    )
                if not resumed and not current_capacity["allowed"]:
                    raise MigrationError(
                        "one-instrument pg_default build capacity gate failed: "
                        f"need {current_capacity['requiredFreeBytes']} bytes, "
                        f"observed {current_capacity['observedFreeBytes']}"
                    )
                source_fingerprint = database.json(
                    fingerprint_sql(
                        target.partition,
                        snapshot_id_predicate(
                            plan["planIdentity"]["protectedSnapshotIds"]
                        ),
                    ),
                    timeout=args.query_timeout_seconds,
                    pgoptions=PLAN_QUERY_PGOPTIONS,
                )
                if (
                    source_fingerprint
                    != plan["planIdentity"]["retainedFingerprint"]
                ):
                    raise MigrationError(
                        "protected source rows changed before replacement build"
                    )
                lsn_before = database.scalar("SELECT pg_current_wal_lsn()")
                temp_before = int(
                    database.scalar(
                        "SELECT temp_bytes FROM pg_stat_database "
                        "WHERE datname = current_database()"
                    )
                )
                free_before = host["dataFilesystem"]["freeBytes"]
                monitor = FilesystemMonitor(
                    host["dataMount"]["source"],
                    EMERGENCY_FLOOR_BYTES + FST_MONITOR_MARGIN_BYTES,
                    lambda free: cancel_migration_backends(
                        runner,
                        args,
                        args.scratch_root,
                        filesystem="fst-pgdata",
                        free_bytes=free,
                        threshold_bytes=(
                            EMERGENCY_FLOOR_BYTES + FST_MONITOR_MARGIN_BYTES
                        ),
                    ),
                )
                started = time.monotonic()
                try:
                    with monitor:
                        if not resumed:
                            database.psql(
                                build_sql(args, target, plan),
                                timeout=args.build_timeout_seconds,
                            )
                except CommandError as error:
                    if monitor.breached:
                        raise MigrationError(
                            "replacement build was cancelled at the FST reserve"
                        ) from error
                    raise
                if monitor.breached:
                    raise MigrationError(
                        "replacement build crossed the emergency threshold"
                    )
                lsn_after = database.scalar("SELECT pg_current_wal_lsn()")
                temp_after = int(
                    database.scalar(
                        "SELECT temp_bytes FROM pg_stat_database "
                        "WHERE datname = current_database()"
                    )
                )
                built_fingerprint = database.json(
                    fingerprint_sql(replacement),
                    timeout=args.query_timeout_seconds,
                )
                if (
                    built_fingerprint
                    != plan["planIdentity"]["retainedFingerprint"]
                ):
                    raise MigrationError(
                        "replacement fingerprint differs from retained source rows"
                    )
                pre_swap_shape = database.json(
                    partition_shape_query(target, replacement)
                )
                # Before attachment the two partitioned indexes intentionally have no
                # top-parent index parent.  Validate every other structural property here.
                for index in pre_swap_shape.get("indexes") or []:
                    index["parentIndex"] = index.get("parentIndex") or "<pending-attach>"
                validate_partition_shape(
                    pre_swap_shape,
                    target,
                    plan["planIdentity"]["protectedSnapshotIds"],
                    attached=False,
                )
                sizes = database.json(
                    f"""
                        SELECT json_build_object(
                            'heapBytes', (
                                SELECT COALESCE(
                                    SUM(pg_relation_size(tree.relid)), 0)
                                FROM pg_partition_tree(
                                    'public.{replacement}'::regclass) tree),
                            'indexBytes', (
                                SELECT COALESCE(
                                    SUM(pg_indexes_size(tree.relid)), 0)
                                FROM pg_partition_tree(
                                    'public.{replacement}'::regclass) tree),
                            'totalBytes', (
                                SELECT COALESCE(
                                    SUM(pg_total_relation_size(tree.relid)), 0)
                                FROM pg_partition_tree(
                                    'public.{replacement}'::regclass) tree),
                            'walBytes', pg_wal_lsn_diff(
                                {sql_literal(lsn_after)}::pg_lsn,
                                {sql_literal(lsn_before)}::pg_lsn)::bigint,
                            'tempBytes',
                                {max(0, temp_after - temp_before)}::bigint,
                            'nonDefaultTablespaces', (
                                SELECT COUNT(*)::bigint
                                FROM pg_partition_tree(
                                    'public.{replacement}'::regclass) tree
                                JOIN pg_class relation
                                  ON relation.oid = tree.relid
                                LEFT JOIN pg_tablespace tablespace
                                  ON tablespace.oid = relation.reltablespace
                                WHERE COALESCE(
                                    tablespace.spcname, 'pg_default')
                                    <> 'pg_default'))
                    """
                )
                if sizes["nonDefaultTablespaces"] != 0:
                    raise MigrationError(
                        "replacement data exists outside pg_default"
                    )
                host_after = collect_host_guard(args, runner)
                body = {
                    "runId": args.run_id,
                    "planId": plan["planId"],
                    "targetKey": target.key,
                    "replacementRelation": f"{TARGET_SCHEMA}.{replacement}",
                    "fingerprint": built_fingerprint,
                    "shape": pre_swap_shape,
                    "sizes": sizes,
                    "capacityGate": capacity,
                    "buildStorage": {
                        "tablespace": "pg_default",
                        "temporaryScratchTablespaceUsed": False,
                    },
                    "elapsedSeconds": round(time.monotonic() - started, 3),
                    "freeBytesBefore": free_before,
                    "freeBytesAfter": host_after["dataFilesystem"][
                        "freeBytes"
                    ],
                    "filesystemMonitor": {
                        "samples": monitor.samples,
                        "minimumFreeBytes": monitor.minimum_free_bytes,
                        "thresholdBytes": monitor.minimum_allowed_bytes,
                        "breached": monitor.breached,
                        "breachHandled": monitor.breach_handled,
                    },
                    "sourceRetainedFingerprint": source_fingerprint,
                    "sourceRelationState": guard["target"],
                    "originalStillAttached": True,
                    "resumedCommittedBuild": resumed,
                    "resumedBuildStartEvidence": resumed_start,
                    "buildStartEvidence": {
                        "path": str(start_path),
                        "sha256": sha256_path(start_path),
                    },
                }
                return write_stage_report(args.scratch_root, "build", body)


def swap_probe_query(args, target):
                replacement = replacement_name(args.run_id, target)
                retired = retired_name(args.run_id, target)
                return f"""
                    SELECT json_build_object(
                        'targetExists',
                            to_regclass(
                                'public.{target.partition}') IS NOT NULL,
                        'targetAttached', EXISTS (
                            SELECT 1
                            FROM pg_inherits
                            WHERE inhparent =
                                'public.{TARGET_PARENT}'::regclass
                              AND inhrelid =
                                to_regclass(
                                    'public.{target.partition}')),
                        'targetKind', (
                            SELECT relkind
                            FROM pg_class
                            WHERE oid = to_regclass(
                                'public.{target.partition}')),
                        'replacementExists',
                            to_regclass(
                                'public.{replacement}') IS NOT NULL,
                        'retiredExists',
                            to_regclass(
                                'public.{retired}') IS NOT NULL,
                        'retiredAttached', EXISTS (
                            SELECT 1
                            FROM pg_inherits
                            WHERE inhrelid =
                                to_regclass('public.{retired}')))
                """


def ensure_original_instrument_check(database, args, target):
                check_name = original_instrument_check_name(
                    args.run_id,
                    target,
                )
                database.psql(
                    f"""
                        BEGIN;
                        SET LOCAL lock_timeout = '2s';
                        SET LOCAL statement_timeout = '30s';
                        DO $ensure_original_check$
                        BEGIN
                            IF NOT EXISTS (
                                SELECT 1
                                FROM pg_constraint
                                WHERE conrelid =
                                    'public.{target.partition}'::regclass
                                  AND conname =
                                    {sql_literal(check_name)}
                            ) THEN
                                ALTER TABLE {qualified(target.partition)}
                                    ADD CONSTRAINT "{check_name}"
                                    CHECK (
                                        instrument =
                                            {sql_literal(target.instrument)})
                                    NOT VALID;
                            END IF;
                        END
                        $ensure_original_check$;
                        ALTER TABLE {qualified(target.partition)}
                            VALIDATE CONSTRAINT "{check_name}";
                        COMMIT;
                    """,
                    timeout=60,
                )
                expected_definition = (
                    "CHECK (instrument = "
                    f"{sql_literal(target.instrument)}::text)"
                )
                observed = database.json(
                    f"""
                        SELECT json_build_object(
                            'validated', constraint_row.convalidated,
                            'definition', pg_get_constraintdef(
                                constraint_row.oid,
                                TRUE))
                        FROM pg_constraint constraint_row
                        WHERE constraint_row.conrelid =
                            'public.{target.partition}'::regclass
                          AND constraint_row.conname =
                            {sql_literal(check_name)}
                    """
                )
                if observed != {
                    "validated": True,
                    "definition": expected_definition,
                }:
                    raise MigrationError(
                        "original instrument check is not exact and validated"
                    )
                return check_name


def stage_swap(args, runner):
                if not args.execute:
                    raise MigrationError("swap requires --execute")
                assert_no_emergency_breach(args.scratch_root)
                target = TARGET_BY_KEY[args.instrument]
                check = load_report(args.scratch_root, "check")
                plan = load_report(args.scratch_root, "plan")
                load_report(args.scratch_root, "archive")
                load_report(args.scratch_root, "restore")
                build = load_report(args.scratch_root, "build")
                host = collect_host_guard(args, runner)
                database = Database(
                    runner,
                    args.pg_container,
                    args.pg_user,
                    args.pg_database,
                )
                allowed = expected_artifact_names(
                    args.run_id,
                    target,
                    plan["planIdentity"]["protectedSnapshotIds"],
                )
                guard = collect_database_guard(
                    database,
                    target,
                    allowed_artifacts=allowed,
                )
                assert_identity_matches(check, host, guard)
                replacement = replacement_name(args.run_id, target)
                retired = retired_name(args.run_id, target)
                probe = database.json(swap_probe_query(args, target))
                pre_swap = {
                    "targetExists": True,
                    "targetAttached": True,
                    "targetKind": "r",
                    "replacementExists": True,
                    "retiredExists": False,
                    "retiredAttached": False,
                }
                post_swap = {
                    "targetExists": True,
                    "targetAttached": True,
                    "targetKind": "p",
                    "replacementExists": False,
                    "retiredExists": True,
                    "retiredAttached": False,
                }
                if probe not in (pre_swap, post_swap):
                    raise MigrationError(
                        f"unexpected pre-swap relation state: {probe}"
                    )
                if probe == pre_swap and (
                    physical_relation_identity(guard["target"])
                    != plan["planIdentity"]["sourceRelationIdentity"]
                ):
                    raise MigrationError(
                        "original source changed before the short-lock swap"
                    )
                commit_path = swap_commit_path(args.scratch_root)
                commit_evidence = (
                    load_integrity_evidence(
                        commit_path,
                        "swap committed",
                    )
                    if commit_path.exists()
                    else None
                )
                if commit_evidence is not None and (
                    commit_evidence.get("runId") != args.run_id
                    or commit_evidence.get("planId") != plan["planId"]
                    or commit_evidence.get("targetKey") != target.key
                ):
                    raise MigrationError(
                        "swap committed evidence belongs to another run"
                    )

                original_check_name = None
                elapsed = 0.0
                if probe == pre_swap:
                    if commit_evidence is not None:
                        raise MigrationError(
                            "swap committed evidence exists before catalog swap"
                        )
                    original_check_name = ensure_original_instrument_check(
                        database,
                        args,
                        target,
                    )
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
                                DETACH PARTITION {qualified(target.partition)};
                            ALTER TABLE {qualified(target.partition)}
                                RENAME TO "{retired}";
                            ALTER TABLE {qualified(replacement)}
                                RENAME TO "{target.partition}";
                            ALTER TABLE {qualified(TARGET_PARENT)}
                                ATTACH PARTITION {qualified(target.partition)}
                                FOR VALUES IN (
                                    {sql_literal(target.instrument)});
                            COMMIT;
                        """,
                        timeout=60,
                    )
                    elapsed = time.monotonic() - started
                    commit_evidence = write_integrity_evidence(
                        commit_path,
                        {
                            "formatVersion": FORMAT_VERSION,
                            "toolId": TOOL_ID,
                            "status": "committed",
                            "recordedAtUtc": utc_now(),
                            "runId": args.run_id,
                            "planId": plan["planId"],
                            "targetKey": target.key,
                            "elapsedSeconds": round(elapsed, 6),
                            "durationKnown": True,
                            "maximumSwapSeconds":
                                args.maximum_swap_seconds,
                            "withinBound":
                                elapsed <= args.maximum_swap_seconds,
                            "originalInstrumentCheck":
                                original_check_name,
                        },
                    )
                elif commit_evidence is None:
                    commit_evidence = write_integrity_evidence(
                        commit_path,
                        {
                            "formatVersion": FORMAT_VERSION,
                            "toolId": TOOL_ID,
                            "status": "recovered_unmeasured",
                            "recordedAtUtc": utc_now(),
                            "runId": args.run_id,
                            "planId": plan["planId"],
                            "targetKey": target.key,
                            "elapsedSeconds": None,
                            "durationKnown": False,
                            "maximumSwapSeconds":
                                args.maximum_swap_seconds,
                            "withinBound": False,
                            "originalInstrumentCheck":
                                original_instrument_check_name(
                                    args.run_id,
                                    target,
                                ),
                        },
                    )
                    raise MigrationError(
                        "committed swap was recovered without duration "
                        "evidence; roll back before continuing"
                    )
                else:
                    elapsed = float(commit_evidence["elapsedSeconds"])

                if commit_evidence.get("withinBound") is not True:
                    raise MigrationError(
                        "swap committed but exceeded the approved duration; "
                        "validate or roll back before drop"
                    )
                post = database.json(swap_probe_query(args, target))
                if post != post_swap:
                    raise MigrationError(
                        f"post-swap relation state is unexpected: {post}"
                    )
                retired_state = database.json(relation_state_query(retired))
                if (
                    physical_relation_identity(retired_state)
                    != plan["planIdentity"]["sourceRelationIdentity"]
                ):
                    raise MigrationError(
                        "retained original differs from the planned source"
                    )
                candidate_fingerprint = database.json(
                    fingerprint_sql(target.partition),
                    timeout=args.query_timeout_seconds,
                )
                if (
                    candidate_fingerprint
                    != plan["planIdentity"]["retainedFingerprint"]
                ):
                    raise MigrationError(
                        "attached replacement differs from retained rows"
                    )
                shape = database.json(
                    partition_shape_query(target, target.partition)
                )
                validate_partition_shape(
                    shape,
                    target,
                    plan["planIdentity"]["protectedSnapshotIds"],
                )
                validate_parent_index_attachments(shape, plan)
                body = {
                    "runId": args.run_id,
                    "planId": plan["planId"],
                    "targetKey": target.key,
                    "elapsedSeconds": round(elapsed, 3),
                    "maximumSwapSeconds": args.maximum_swap_seconds,
                    "postSwap": post,
                    "candidateFingerprint": candidate_fingerprint,
                    "candidateShape": shape,
                    "originalRetainedRelation": (
                        f"{TARGET_SCHEMA}.{retired}"
                    ),
                    "originalRetainedState": retired_state,
                    "rollbackAvailable": True,
                    "dropPerformed": False,
                    "resumedCommittedSwap": probe == post_swap,
                    "swapCommittedEvidence": {
                        "path": str(commit_path),
                        "sha256": sha256_path(commit_path),
                    },
                    "originalInstrumentCheck":
                        commit_evidence["originalInstrumentCheck"],
                    "buildSizes": build["sizes"],
                }
                return write_stage_report(args.scratch_root, "swap", body)


def compare_reference_parity(expected, observed):
                assert_reference_parity(observed)
                for key in (
                    "namedSourceCount",
                    "namedSourceFingerprint",
                    "activeStateFingerprint",
                    "projectionFingerprint",
                ):
                    if observed.get(key) != expected.get(key):
                        raise MigrationError(
                            f"reference parity field changed: {key}"
                        )
                if observed.get("publication") != expected.get("publication"):
                    raise MigrationError(
                        "publication state changed across migration"
                    )
                return True


def stage_validate(args, runner):
                target = TARGET_BY_KEY[args.instrument]
                check = load_report(args.scratch_root, "check")
                plan = load_report(args.scratch_root, "plan")
                archive = load_report(args.scratch_root, "archive")
                restore = load_report(args.scratch_root, "restore")
                load_report(args.scratch_root, "swap")
                host = collect_host_guard(args, runner)
                database = Database(
                    runner,
                    args.pg_container,
                    args.pg_user,
                    args.pg_database,
                )
                allowed = expected_artifact_names(
                    args.run_id,
                    target,
                    plan["planIdentity"]["protectedSnapshotIds"],
                )
                guard = collect_database_guard(
                    database,
                    target,
                    allowed_artifacts=allowed,
                )
                assert_identity_matches(check, host, guard)
                retired = retired_name(args.run_id, target)
                retired_state = database.json(relation_state_query(retired))
                if (
                    physical_relation_identity(retired_state)
                    != plan["planIdentity"]["sourceRelationIdentity"]
                ):
                    raise MigrationError(
                        "retained original changed after swap"
                    )
                protected_now = database.json(
                    protected_sources_query(target),
                    timeout=args.query_timeout_seconds,
                    pgoptions=PLAN_QUERY_PGOPTIONS,
                )
                inventory_now = database.json(
                    inventory_query(target),
                    timeout=args.query_timeout_seconds,
                    pgoptions=LOOSE_ID_PGOPTIONS,
                )
                protected_ids_now = derive_protected_ids(
                    protected_now,
                    inventory_now,
                )
                if protected_ids_now != plan["planIdentity"][
                    "protectedSnapshotIds"
                ]:
                    raise MigrationError(
                        "protected snapshot IDs changed after planning"
                    )
                original_retained = database.json(
                    fingerprint_sql(
                        retired,
                        snapshot_id_predicate(protected_ids_now),
                    ),
                    timeout=args.query_timeout_seconds,
                    pgoptions=PLAN_QUERY_PGOPTIONS,
                )
                candidate = database.json(
                    fingerprint_sql(target.partition),
                    timeout=args.query_timeout_seconds,
                )
                expected_fingerprint = plan["planIdentity"][
                    "retainedFingerprint"
                ]
                if (
                    original_retained != expected_fingerprint
                    or candidate != expected_fingerprint
                ):
                    raise MigrationError(
                        "candidate/original retained fingerprints differ from plan"
                    )
                candidate_distribution = database.json(
                    snapshot_distribution_query(target.partition),
                    timeout=args.query_timeout_seconds,
                )
                if normalized_distribution(candidate_distribution) != plan[
                    "planIdentity"
                ]["retainedDistribution"]:
                    raise MigrationError(
                        "candidate per-snapshot fingerprints differ from plan"
                    )
                shape = database.json(
                    partition_shape_query(target, target.partition)
                )
                validate_partition_shape(shape, target, protected_ids_now)
                validate_parent_index_attachments(shape, plan)
                catalog = database.json(catalog_query(target.partition))
                if (
                    catalog.get("columns")
                    != plan["sourceCatalog"].get("columns")
                    or catalog.get("owner")
                    != plan["sourceCatalog"].get("owner")
                    or catalog.get("tablespace") != "pg_default"
                    or catalog.get("partitionKey") != "LIST (snapshot_id)"
                    or catalog.get("partitionBound") != target.bound
                ):
                    raise MigrationError(
                        "candidate column/owner/partition catalog parity failed"
                    )
                reference = database.json(
                    reference_parity_query(target, target.partition),
                    timeout=args.query_timeout_seconds,
                    pgoptions=PLAN_QUERY_PGOPTIONS,
                )
                compare_reference_parity(
                    plan["planIdentity"]["referenceParity"],
                    reference,
                )
                manifest = load_archive_manifest(args.scratch_root, target)
                if (
                    manifest["archive"]["sha256"]
                    != archive["archive"]["sha256"]
                    or manifest["archive"]["sha256"]
                    != restore["archive"]["sha256"]
                ):
                    raise MigrationError(
                        "archive checksum differs across lifecycle evidence"
                    )
                cleanup = read_json(restore["cleanupProof"]["path"])
                if (
                    sha256_path(restore["cleanupProof"]["path"])
                    != restore["cleanupProof"]["sha256"]
                    or cleanup.get("archiveRetained") is not True
                    or cleanup.get("containerRemoved") is not True
                    or cleanup.get("restorePgdataRemoved") is not True
                ):
                    raise MigrationError(
                        "isolated restore cleanup evidence is incomplete"
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
                    raise MigrationError(
                        "representative public API parity changed after swap"
                    )
                body = {
                    "runId": args.run_id,
                    "planId": plan["planId"],
                    "targetKey": target.key,
                    "accepted": True,
                    "protectedSnapshotIds": protected_ids_now,
                    "candidateFingerprint": candidate,
                    "candidateDistribution": normalized_distribution(
                        candidate_distribution
                    ),
                    "retainedOriginalFingerprint": original_retained,
                    "retainedOriginalRelationState": retired_state,
                    "candidateShape": shape,
                    "candidateCatalog": catalog,
                    "referenceParity": reference,
                    "publicApi": public_api,
                    "archive": {
                        "path": manifest["archive"]["path"],
                        "sha256": manifest["archive"]["sha256"],
                        "present": True,
                        "restoreProved": True,
                    },
                    "oldRelationStillPresent": True,
                    "rollbackStillAvailable": True,
                    "acceptedTablespace": "pg_default",
                }
                return write_stage_report(args.scratch_root, "validate", body)


def final_catalog_names_match(catalog, source_names):
                primary = [
                    item
                    for item in catalog.get("constraints") or []
                    if item.get("type") == "p"
                ]
                indexes = catalog.get("indexes") or []
                primary_indexes = [
                    item for item in indexes if item.get("isPrimary")
                ]
                score_indexes = [
                    item
                    for item in indexes
                    if (
                        not item.get("isPrimary")
                        and "(snapshot_id, song_id, instrument, score DESC)"
                        in item.get("definition", "")
                    )
                ]
                return (
                    len(primary) == 1
                    and len(primary_indexes) == 1
                    and len(score_indexes) == 1
                    and primary[0]["name"] == source_names["primaryConstraint"]
                    and primary_indexes[0]["name"] == source_names["primaryIndex"]
                    and score_indexes[0]["name"] == source_names["scoreIndex"]
                )


def stage_drop(args, runner):
                if not args.execute:
                    raise MigrationError("drop requires --execute")
                assert_no_emergency_breach(args.scratch_root)
                target = TARGET_BY_KEY[args.instrument]
                check = load_report(args.scratch_root, "check")
                plan = load_report(args.scratch_root, "plan")
                validation = load_report(args.scratch_root, "validate")
                if validation.get("accepted") is not True:
                    raise MigrationError("validation was not accepted")
                load_report(args.scratch_root, "archive")
                load_report(args.scratch_root, "restore")
                host = collect_host_guard(args, runner)
                database = Database(
                    runner,
                    args.pg_container,
                    args.pg_user,
                    args.pg_database,
                )
                allowed = expected_artifact_names(
                    args.run_id,
                    target,
                    plan["planIdentity"]["protectedSnapshotIds"],
                )
                guard = collect_database_guard(
                    database,
                    target,
                    allowed_artifacts=allowed,
                )
                assert_identity_matches(check, host, guard)
                retired = retired_name(args.run_id, target)
                retired_state = database.json(relation_state_query(retired))
                retired_exists = retired_state is not None
                source_names = plan["sourceCatalogNames"]
                candidate_catalog = database.json(
                    catalog_query(target.partition)
                )
                already_finalized = (
                    not retired_exists
                    and final_catalog_names_match(candidate_catalog, source_names)
                )
                if not retired_exists and not already_finalized:
                    raise MigrationError(
                        "original is absent but final catalog names are incomplete"
                    )
                if retired_exists and (
                    physical_relation_identity(retired_state)
                    != plan["planIdentity"]["sourceRelationIdentity"]
                ):
                    raise MigrationError(
                        "retained original changed before final drop"
                    )
                protected_now = derive_protected_ids(
                    database.json(
                        protected_sources_query(target),
                        timeout=args.query_timeout_seconds,
                        pgoptions=PLAN_QUERY_PGOPTIONS,
                    ),
                    database.json(
                        inventory_query(target),
                        timeout=args.query_timeout_seconds,
                        pgoptions=LOOSE_ID_PGOPTIONS,
                    ),
                )
                if protected_now != plan["planIdentity"][
                    "protectedSnapshotIds"
                ]:
                    raise MigrationError(
                        "protected IDs changed before final drop"
                    )
                fingerprint = database.json(
                    fingerprint_sql(target.partition),
                    timeout=args.query_timeout_seconds,
                )
                if fingerprint != plan["planIdentity"]["retainedFingerprint"]:
                    raise MigrationError(
                        "accepted target fingerprint changed before final drop"
                    )
                shape = database.json(
                    partition_shape_query(target, target.partition)
                )
                validate_partition_shape(shape, target, protected_now)
                validate_parent_index_attachments(shape, plan)
                manifest = load_archive_manifest(args.scratch_root, target)
                source_fence = (
                    source_fence_from_state(retired_state)
                    if retired_exists
                    else source_fence_from_state(
                        validation["retainedOriginalRelationState"]
                    )
                )
                if not source_fence_matches(
                    manifest["source"]["before"],
                    source_fence,
                ):
                    raise MigrationError(
                        "archive source fence differs from retained original"
                    )
                free_before = host["dataFilesystem"]["freeBytes"]
                started = time.monotonic()
                if retired_exists:
                    database.psql(
                        f"""
                            BEGIN;
                            SET LOCAL lock_timeout = '2s';
                            SET LOCAL statement_timeout = '30s';
                            {advisory_lock_guard_sql()}
                            DROP TABLE {qualified(retired)};
                            ALTER TABLE {qualified(target.partition)}
                                DROP CONSTRAINT
                                    "{replacement_instrument_check_name(args.run_id, target)}";
                            ALTER TABLE {qualified(target.partition)}
                                RENAME CONSTRAINT
                                    "{replacement_primary_name(args.run_id, target)}"
                                TO "{source_names['primaryConstraint']}";
                            ALTER INDEX
                                {qualified(replacement_score_name(args.run_id, target))}
                                RENAME TO "{source_names['scoreIndex']}";
                            COMMIT;
                        """,
                        timeout=60,
                    )
                if database.json(relation_state_query(retired)) is not None:
                    raise MigrationError(
                        "retained original remains after final drop"
                    )
                final_shape = database.json(
                    partition_shape_query(target, target.partition)
                )
                validate_partition_shape(final_shape, target, protected_now)
                validate_parent_index_attachments(final_shape, plan)
                final_catalog = database.json(catalog_query(target.partition))
                if not final_catalog_names_match(final_catalog, source_names):
                    raise MigrationError(
                        "final partitioned PK/score catalog names are unexpected"
                    )
                if any(
                    item["name"]
                    == replacement_instrument_check_name(args.run_id, target)
                    for item in final_catalog["constraints"]
                ):
                    raise MigrationError(
                        "temporary instrument check remains after final drop"
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
                    raise MigrationError(
                        "public API parity changed after final drop"
                    )
                host_after = collect_host_guard(args, runner)
                if (
                    not pathlib.Path(manifest["archive"]["path"]).is_file()
                    or sha256_path(manifest["archive"]["path"])
                    != manifest["archive"]["sha256"]
                ):
                    raise MigrationError(
                        "archive was not retained after final drop"
                    )
                body = {
                    "runId": args.run_id,
                    "planId": plan["planId"],
                    "targetKey": target.key,
                    "droppedRelation": f"{TARGET_SCHEMA}.{retired}",
                    "droppedOriginalBytes": (
                        retired_state["totalBytes"]
                        if retired_state
                        else plan["sourceState"]["totalBytes"]
                    ),
                    "elapsedSeconds": round(time.monotonic() - started, 3),
                    "freeBytesBefore": free_before,
                    "freeBytesAfter": host_after["dataFilesystem"][
                        "freeBytes"
                    ],
                    "filesystemBytesReturned": max(
                        0,
                        host_after["dataFilesystem"]["freeBytes"] - free_before,
                    ),
                    "resumedCommittedDrop": already_finalized,
                    "archiveRetained": {
                        "path": manifest["archive"]["path"],
                        "sha256": manifest["archive"]["sha256"],
                        "deletionDecision": "deferred",
                    },
                    "rollbackModeAfterDrop": (
                        "archive restore only; rename-back rollback is unavailable"
                    ),
                    "targetFingerprint": fingerprint,
                    "finalShape": final_shape,
                    "finalCatalog": final_catalog,
                    "acceptedTablespace": "pg_default",
                    "temporaryScratchTablespaceUsed": False,
                    "publicApi": public_api,
                }
                return write_stage_report(args.scratch_root, "drop", body)


def rollback_probe_query(args, target):
                retired = retired_name(args.run_id, target)
                failed = failed_name(args.run_id, target)
                return f"""
                    SELECT json_build_object(
                        'targetExists',
                            to_regclass(
                                'public.{target.partition}') IS NOT NULL,
                        'targetAttached', EXISTS (
                            SELECT 1 FROM pg_inherits
                            WHERE inhparent =
                                'public.{TARGET_PARENT}'::regclass
                              AND inhrelid =
                                to_regclass(
                                    'public.{target.partition}')),
                        'targetKind', (
                            SELECT relkind FROM pg_class
                            WHERE oid = to_regclass(
                                'public.{target.partition}')),
                        'retiredExists',
                            to_regclass('public.{retired}') IS NOT NULL,
                        'failedExists',
                            to_regclass('public.{failed}') IS NOT NULL)
                """


def stage_rollback(args, runner):
                if not args.execute:
                    raise MigrationError("rollback requires --execute")
                assert_no_emergency_breach(args.scratch_root)
                if report_path(args.scratch_root, "drop").exists():
                    raise MigrationError(
                        "rename-back rollback is unavailable after final drop"
                    )
                target = TARGET_BY_KEY[args.instrument]
                check = load_report(args.scratch_root, "check")
                plan = load_report(args.scratch_root, "plan")
                load_report(args.scratch_root, "archive")
                commit_path = swap_commit_path(args.scratch_root)
                commit_evidence = load_integrity_evidence(
                    commit_path,
                    "swap committed",
                )
                if (
                    commit_evidence.get("runId") != args.run_id
                    or commit_evidence.get("planId") != plan["planId"]
                    or commit_evidence.get("targetKey") != target.key
                    or commit_evidence.get("status")
                    not in ("committed", "recovered_unmeasured")
                ):
                    raise MigrationError(
                        "swap committed evidence is invalid for rollback"
                    )
                host = collect_host_guard(args, runner)
                database = Database(
                    runner,
                    args.pg_container,
                    args.pg_user,
                    args.pg_database,
                )
                allowed = expected_artifact_names(
                    args.run_id,
                    target,
                    plan["planIdentity"]["protectedSnapshotIds"],
                )
                guard = collect_database_guard(
                    database,
                    target,
                    allowed_artifacts=allowed,
                )
                assert_identity_matches(check, host, guard)
                retired = retired_name(args.run_id, target)
                failed = failed_name(args.run_id, target)
                probe = database.json(rollback_probe_query(args, target))
                pre = {
                    "targetExists": True,
                    "targetAttached": True,
                    "targetKind": "p",
                    "retiredExists": True,
                    "failedExists": False,
                }
                post = {
                    "targetExists": True,
                    "targetAttached": True,
                    "targetKind": "r",
                    "retiredExists": False,
                    "failedExists": True,
                }
                if probe not in (pre, post):
                    raise MigrationError(
                        f"unexpected rollback relation state: {probe}"
                    )
                original_name = (
                    target.partition if probe == post else retired
                )
                original_state = database.json(
                    relation_state_query(original_name)
                )
                if (
                    physical_relation_identity(original_state)
                    != plan["planIdentity"]["sourceRelationIdentity"]
                ):
                    raise MigrationError(
                        "retained original changed; refusing rollback"
                    )
                if probe == pre:
                    check_name = original_instrument_check_name(
                        args.run_id,
                        target,
                    )
                    expected_definition = (
                        "CHECK (instrument = "
                        f"{sql_literal(target.instrument)}::text)"
                    )
                    observed_check = database.json(
                        f"""
                            SELECT json_build_object(
                                'validated',
                                    constraint_row.convalidated,
                                'definition',
                                    pg_get_constraintdef(
                                        constraint_row.oid,
                                        TRUE))
                            FROM pg_constraint constraint_row
                            WHERE constraint_row.conrelid =
                                'public.{retired}'::regclass
                              AND constraint_row.conname =
                                {sql_literal(check_name)}
                        """
                    )
                    if observed_check != {
                        "validated": True,
                        "definition": expected_definition,
                    }:
                        raise MigrationError(
                            "retained original lacks exact validated "
                            "instrument check"
                        )
                original_retained = database.json(
                    fingerprint_sql(
                        original_name,
                        snapshot_id_predicate(
                            plan["planIdentity"]["protectedSnapshotIds"]
                        ),
                    ),
                    timeout=args.query_timeout_seconds,
                    pgoptions=PLAN_QUERY_PGOPTIONS,
                )
                if (
                    original_retained
                    != plan["planIdentity"]["retainedFingerprint"]
                ):
                    raise MigrationError(
                        "retained original protected rows changed"
                    )
                started = time.monotonic()
                if probe == pre:
                    child_renames = "\n".join(
                        (
                            f"ALTER TABLE {qualified(generation_child_name(target, value))} "
                            f"RENAME TO \"{failed_child_name(args.run_id, target, value)}\";"
                        )
                        for value in plan["planIdentity"][
                            "protectedSnapshotIds"
                        ]
                    )
                    database.psql(
                        f"""
                            BEGIN;
                            SET LOCAL lock_timeout = '2s';
                            SET LOCAL statement_timeout = '30s';
                            {advisory_lock_guard_sql()}
                            LOCK TABLE {qualified(TARGET_PARENT)}
                                IN ACCESS EXCLUSIVE MODE;
                            ALTER TABLE {qualified(TARGET_PARENT)}
                                DETACH PARTITION {qualified(target.partition)};
                            ALTER TABLE {qualified(target.partition)}
                                RENAME TO "{failed}";
                            {child_renames}
                            ALTER TABLE {qualified(default_child_name(target))}
                                RENAME TO
                                    "{failed_child_name(args.run_id, target)}";
                            ALTER TABLE {qualified(retired)}
                                RENAME TO "{target.partition}";
                            ALTER TABLE {qualified(TARGET_PARENT)}
                                ATTACH PARTITION {qualified(target.partition)}
                                FOR VALUES IN (
                                    {sql_literal(target.instrument)});
                            ALTER TABLE {qualified(target.partition)}
                                DROP CONSTRAINT
                                    "{original_instrument_check_name(args.run_id, target)}";
                            COMMIT;
                        """,
                        timeout=60,
                    )
                else:
                    check_name = original_instrument_check_name(
                        args.run_id,
                        target,
                    )
                    if database.scalar(
                        "SELECT EXISTS("
                        "SELECT 1 FROM pg_constraint "
                        "WHERE conrelid = "
                        f"'public.{target.partition}'::regclass "
                        f"AND conname = {sql_literal(check_name)})"
                    ) == "t":
                        database.psql(
                            f"""
                                BEGIN;
                                SET LOCAL lock_timeout = '2s';
                                SET LOCAL statement_timeout = '30s';
                                ALTER TABLE {qualified(target.partition)}
                                    DROP CONSTRAINT "{check_name}";
                                COMMIT;
                            """,
                            timeout=60,
                        )
                observed = database.json(rollback_probe_query(args, target))
                if observed != post:
                    raise MigrationError(
                        f"post-rollback relation state is unexpected: {observed}"
                    )
                restored_state = database.json(
                    relation_state_query(target.partition)
                )
                if (
                    physical_relation_identity(restored_state)
                    != plan["planIdentity"]["sourceRelationIdentity"]
                ):
                    raise MigrationError(
                        "rename-back relation differs from original"
                    )
                restored_fingerprint = database.json(
                    fingerprint_sql(
                        target.partition,
                        snapshot_id_predicate(
                            plan["planIdentity"]["protectedSnapshotIds"]
                        ),
                    ),
                    timeout=args.query_timeout_seconds,
                    pgoptions=PLAN_QUERY_PGOPTIONS,
                )
                if restored_fingerprint != plan["planIdentity"][
                    "retainedFingerprint"
                ]:
                    raise MigrationError(
                        "rename-back protected rows differ from original"
                    )
                reference = database.json(
                    reference_parity_query(target, target.partition),
                    timeout=args.query_timeout_seconds,
                    pgoptions=PLAN_QUERY_PGOPTIONS,
                )
                compare_reference_parity(
                    plan["planIdentity"]["referenceParity"],
                    reference,
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
                    raise MigrationError(
                        "public API parity changed after rename-back rollback"
                    )
                body = {
                    "runId": args.run_id,
                    "planId": plan["planId"],
                    "targetKey": target.key,
                    "elapsedSeconds": round(time.monotonic() - started, 3),
                    "restoredRelation": (
                        f"{TARGET_SCHEMA}.{target.partition}"
                    ),
                    "restoredRelationState": restored_state,
                    "restoredRetainedFingerprint": restored_fingerprint,
                    "failedCandidateRetainedRelation": (
                        f"{TARGET_SCHEMA}.{failed}"
                    ),
                    "archiveStillRetained": True,
                    "finalDropPerformed": False,
                    "publicApi": public_api,
                    "resumedCommittedRollback": probe == post,
                    "swapCommittedEvidence": {
                        "path": str(commit_path),
                        "sha256": sha256_path(commit_path),
                    },
                }
                return write_stage_report(args.scratch_root, "rollback", body)


def build_parser():
                parser = argparse.ArgumentParser(
                    description=(
                        "Guarded snapshot-ID subpartition migration for exactly nine "
                        "compiled FST instrument partitions. No arbitrary relation or "
                        "SQL input is accepted."
                    )
                )
                parser.add_argument("stage", choices=STAGES)
                parser.add_argument(
                    "--instrument",
                    required=True,
                    choices=tuple(target.key for target in TARGETS),
                )
                parser.add_argument("--scratch-root", required=True)
                parser.add_argument("--expected-device-id", required=True)
                parser.add_argument("--run-id", required=True)
                parser.add_argument(
                    "--expires-at",
                    help=(
                        "ISO-8601 workspace expiry; defaults to 90 days from "
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
                parser.add_argument(
                    "--pg-container",
                    default=POSTGRES_CONTAINER,
                )
                parser.add_argument(
                    "--worker-container",
                    default=WORKER_CONTAINER,
                )
                parser.add_argument("--pg-user", default=DATABASE_USER)
                parser.add_argument("--pg-database", default=DATABASE_NAME)
                parser.add_argument("--restore-image")
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
                parser.add_argument("--api-base")
                return parser


def validate_args(args):
                if not re.fullmatch(
                    r"[A-Za-z0-9][A-Za-z0-9._-]{7,80}",
                    args.run_id,
                ):
                    raise MigrationError(
                        "--run-id must be 8-81 safe identifier characters"
                    )
                for label in (
                    "query_timeout_seconds",
                    "archive_timeout_seconds",
                    "build_timeout_seconds",
                ):
                    if getattr(args, label) <= 0:
                        raise MigrationError(
                            f"--{label.replace('_', '-')} must be positive"
                        )
                if args.maximum_swap_seconds <= 0:
                    raise MigrationError(
                        "--maximum-swap-seconds must be positive"
                    )
                if args.stage in EXECUTE_STAGES and not args.execute:
                    raise MigrationError(f"{args.stage} requires --execute")
                if args.stage == "check" and not args.claim_workspace:
                    raise MigrationError(
                        "initial check requires --claim-workspace"
                    )
                if not args.test_mode:
                    if pathlib.Path(args.compose_dir).resolve() != (
                        PRODUCTION_COMPOSE_DIR
                    ):
                        raise MigrationError(
                            "production Compose directory must be exact"
                        )
                    if args.pg_container != POSTGRES_CONTAINER:
                        raise MigrationError(
                            "production PostgreSQL container must be exact"
                        )
                    if args.worker_container != WORKER_CONTAINER:
                        raise MigrationError(
                            "production worker container must be exact"
                        )
                    if args.pg_user != DATABASE_USER:
                        raise MigrationError(
                            "production PostgreSQL user must be exact"
                        )
                    if args.pg_database != DATABASE_NAME:
                        raise MigrationError(
                            "production database must be exact"
                        )
                    if args.restore_image:
                        raise MigrationError(
                            "production restore must use the checked live image"
                        )
                    if args.stage in (
                        "check",
                        "validate",
                        "drop",
                        "rollback",
                    ) and not args.api_base:
                        raise MigrationError(
                            "production check/validate/drop/rollback requires "
                            "--api-base for public API parity"
                        )


def stage_function(stage):
                return {
                    "check": stage_check,
                    "plan": stage_plan,
                    "archive": stage_archive,
                    "restore": stage_restore,
                    "build": stage_build,
                    "swap": stage_swap,
                    "validate": stage_validate,
                    "drop": stage_drop,
                    "rollback": stage_rollback,
                }[stage]


def main(argv=None):
                parser = build_parser()
                args = parser.parse_args(argv)
                runner = Runner()
                try:
                    validate_args(args)
                    target = TARGET_BY_KEY[args.instrument]
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
                        target,
                        expires_at,
                        repository_commit,
                        tool_source_sha256,
                        args.test_mode,
                    )
                    validate_workspace_marker(
                        marker,
                        args.run_id,
                        target,
                        repository_commit,
                        tool_source_sha256,
                    )
                    with workspace_lock(args.scratch_root):
                        existing = recover_torn_report(
                            args.scratch_root,
                            args.stage,
                        )
                        if existing is not None:
                            report = existing
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
                    path = report_path(args.scratch_root, args.stage)
                    print(
                        json.dumps(
                            {
                                "stage": args.stage,
                                "status": "succeeded",
                                "instrument": target.key,
                                "report": str(path),
                                "reportSha256": sha256_path(path),
                                "runId": report.get("runId"),
                            },
                            sort_keys=True,
                        )
                    )
                    return 0
                except MigrationError as error:
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
