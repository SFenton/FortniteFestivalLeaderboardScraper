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
import selectors
import shutil
import signal
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
    "rollback": ("archive", "restore"),
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

    def json_script(self, sql, *, timeout=600, pgoptions=None):
        output = self.scalar(
            sql,
            timeout=timeout,
            pgoptions=pgoptions,
        )
        for line in reversed(output.splitlines()):
            candidate = line.strip()
            if not candidate:
                continue
            try:
                return json.loads(candidate)
            except json.JSONDecodeError:
                continue
        raise MigrationError(
            f"database script returned no JSON result: {output[:500]}"
        )

    def psql(self, sql, *, timeout=600, pgoptions=None):
        return self.runner.run(
            self._arguments(sql, pgoptions),
            timeout=timeout,
        )

    def open_transaction(self, sql, *, timeout=600, pgoptions=None):
        options = (
            f"-c application_name={APPLICATION_NAME} "
            "-c row_security=off"
        )
        if pgoptions:
            options += " " + pgoptions
        arguments = [
            "docker",
            "exec",
            "-i",
            "-e",
            "PGCONNECT_TIMEOUT=10",
            "-e",
            f"PGOPTIONS={options}",
            self.container,
            "psql",
            "-X",
            "-q",
            "-v",
            "ON_ERROR_STOP=1",
            "-U",
            self.user,
            "-d",
            self.database,
            "-At",
        ]
        session = InteractivePsqlTransaction(
            arguments,
            timeout=timeout,
        )
        session.execute_until_ready(sql)
        return session


class InteractivePsqlTransaction:
    READY_MARKER = "__FST_TRANSACTION_READY__"

    def __init__(self, arguments, timeout):
        self.timeout = timeout
        self.process = subprocess.Popen(
            arguments,
            stdin=subprocess.PIPE,
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
            text=True,
            bufsize=1,
        )
        self.stderr_lines = []
        self.finished = False

    def execute_until_ready(self, sql):
        if self.process.stdin is None:
            raise MigrationError("interactive psql stdin is unavailable")
        self.process.stdin.write(sql)
        self.process.stdin.write(
            f"\n\\echo {self.READY_MARKER}\n"
        )
        self.process.stdin.flush()
        selector = selectors.DefaultSelector()
        assert self.process.stdout is not None
        assert self.process.stderr is not None
        selector.register(self.process.stdout, selectors.EVENT_READ)
        selector.register(self.process.stderr, selectors.EVENT_READ)
        deadline = time.monotonic() + self.timeout
        output_lines = []
        try:
            while time.monotonic() < deadline:
                if self.process.poll() is not None:
                    break
                events = selector.select(timeout=0.5)
                for key, _ in events:
                    line = key.fileobj.readline()
                    if not line:
                        continue
                    if key.fileobj is self.process.stderr:
                        self.stderr_lines.append(line)
                    elif line.strip() == self.READY_MARKER:
                        return output_lines
                    else:
                        output_lines.append(line)
            raise MigrationError(
                "interactive PostgreSQL reproof did not reach decision "
                "point: "
                + "".join(self.stderr_lines)[-2000:]
            )
        except Exception:
            self.rollback()
            raise
        finally:
            selector.close()

    def commit(self, sql):
        self._finish(sql + "\n\\q\n")

    def rollback(self):
        if self.finished:
            return
        with contextlib.suppress(Exception):
            self._finish("ROLLBACK;\n\\q\n")

    def _finish(self, command):
        if self.finished:
            return
        self.finished = True
        if self.process.stdin is not None:
            self.process.stdin.write(command)
            self.process.stdin.flush()
        try:
            stdout, stderr = self.process.communicate(
                timeout=self.timeout
            )
        except subprocess.TimeoutExpired as error:
            self.process.terminate()
            with contextlib.suppress(subprocess.TimeoutExpired):
                self.process.wait(timeout=10)
            raise MigrationError(
                "interactive PostgreSQL transaction timed out"
            ) from error
        if self.process.returncode != 0:
            raise MigrationError(
                "interactive PostgreSQL transaction failed: "
                + ("".join(self.stderr_lines) + stderr)[-4000:]
                + stdout[-1000:]
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


class PublicApiMonitor:
    def __init__(self, base_url, baseline, interval_seconds=5):
        self.base_url = base_url
        self.baseline = baseline
        self.interval_seconds = interval_seconds
        self.samples = 0
        self.failures = []
        self._stopped = threading.Event()
        self._thread = None

    def _observe(self):
        try:
            observed = capture_api_fingerprints(self.base_url)
            if not compare_api_snapshots(self.baseline, observed):
                self.failures.append(
                    {
                        "observedAtUtc": utc_now(),
                        "reason": "fingerprint_mismatch",
                        "observed": observed,
                    }
                )
        except Exception as error:
            self.failures.append(
                {
                    "observedAtUtc": utc_now(),
                    "reason": str(error),
                }
            )
        self.samples += 1

    def _run(self):
        while not self._stopped.wait(self.interval_seconds):
            self._observe()

    def __enter__(self):
        self._observe()
        self._thread = threading.Thread(
            target=self._run,
            name="fst-snapshot-generation-api-monitor",
            daemon=True,
        )
        self._thread.start()
        return self

    def stop(self, timeout_seconds=90):
        self._stopped.set()
        if self._thread is None:
            return True
        self._thread.join(timeout=timeout_seconds)
        if self._thread.is_alive():
            self.failures.append(
                {
                    "observedAtUtc": utc_now(),
                    "reason": "api_monitor_thread_did_not_stop",
                }
            )
            return False
        self._observe()
        self._thread = None
        return True

    def __exit__(self, *_):
        self.stop()


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


def write_stage_report(
    root,
    stage,
    body,
    *,
    dependencies=None,
):
    path = report_path(root, stage)
    if path.exists():
        return load_report(root, stage)
    report = {
        "formatVersion": FORMAT_VERSION,
        "toolId": TOOL_ID,
        "stage": stage,
        "status": "succeeded",
        "completedAtUtc": utc_now(),
        "dependencies": (
            dependency_hashes(root, stage)
            if dependencies is None
            else dependencies
        ),
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
                    'statisticsResetAt', (
                        SELECT stats_reset
                        FROM pg_stat_database
                        WHERE datname = current_database()),
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
            ), 'null'::json) AS value
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
            )
        }


def relation_mutation_counters(state):
        if not state:
            return None
        return {
            key: state[key]
            for key in ("inserts", "updates", "deletes")
        }


def relation_statistics_epoch(state):
        if not state:
            return None
        return state.get("statisticsResetAt")


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
            **relation_mutation_counters(state),
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
                                'tablespace', COALESCE(
                                    index_tablespace.spcname,
                                    'pg_default'),
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
                    LEFT JOIN pg_tablespace index_tablespace
                      ON index_tablespace.oid =
                           index_class.reltablespace
                    WHERE metadata.indrelid = relation.oid),
                'heapBytes', pg_relation_size(relation.oid),
                'indexBytes', pg_indexes_size(relation.oid),
                'totalBytes', pg_total_relation_size(relation.oid)) AS value
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
                    0)::text) AS value
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
                '[]'::json) AS value
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
                       pg_get_userbyid(child.relowner) AS owner,
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
                       COALESCE(
                           index_tablespace.spcname,
                           'pg_default') AS tablespace,
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
                LEFT JOIN pg_tablespace index_tablespace
                  ON index_tablespace.oid =
                       index_class.reltablespace
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
                                'owner', owner,
                                'partitionBound', partition_bound,
                                'tablespace', tablespace,
                                'columns', (
                                    SELECT json_agg(
                                        json_build_object(
                                            'ordinal', attribute.attnum,
                                            'name', attribute.attname,
                                            'type', format_type(
                                                attribute.atttypid,
                                                attribute.atttypmod),
                                            'notNull',
                                                attribute.attnotnull,
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
                                           children.oid
                                      AND attribute.attnum > 0
                                      AND NOT attribute.attisdropped),
                                'constraints', (
                                    SELECT COALESCE(
                                        json_agg(
                                            json_build_object(
                                                'name',
                                                    constraint_row.conname,
                                                'type',
                                                    constraint_row.contype,
                                                'definition',
                                                    pg_get_constraintdef(
                                                        constraint_row.oid,
                                                        true),
                                                'validated',
                                                    constraint_row.convalidated)
                                            ORDER BY
                                                constraint_row.conname),
                                        '[]'::json)
                                    FROM pg_constraint constraint_row
                                    WHERE constraint_row.conrelid =
                                           children.oid),
                                'indexes', (
                                    SELECT COALESCE(
                                        json_agg(
                                            json_build_object(
                                                'name',
                                                    index_class.relname,
                                                'definition',
                                                    pg_get_indexdef(
                                                        index_class.oid),
                                                'isPrimary',
                                                    metadata.indisprimary,
                                                'isUnique',
                                                    metadata.indisunique,
                                                'isValid',
                                                    metadata.indisvalid,
                                                'relationKind',
                                                    index_class.relkind,
                                                'tablespace',
                                                    COALESCE(
                                                        index_tablespace.spcname,
                                                        'pg_default'),
                                                'parentIndex',
                                                    parent_index.relname)
                                            ORDER BY index_class.relname),
                                        '[]'::json)
                                    FROM pg_index metadata
                                    JOIN pg_class index_class
                                      ON index_class.oid =
                                           metadata.indexrelid
                                    LEFT JOIN pg_inherits inheritance
                                      ON inheritance.inhrelid =
                                           index_class.oid
                                    LEFT JOIN pg_class parent_index
                                      ON parent_index.oid =
                                           inheritance.inhparent
                                    LEFT JOIN pg_tablespace index_tablespace
                                      ON index_tablespace.oid =
                                           index_class.reltablespace
                                    WHERE metadata.indrelid =
                                           children.oid))
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
                                'tablespace', tablespace,
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
                        <> 'pg_default')) AS value
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
                or item.get("tablespace") != "pg_default"
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


def validate_partition_child_catalogs(
    shape,
    target,
    plan,
    args,
    *,
    instrument_check_present,
    primary_parent,
    score_parent,
):
    expected_columns = plan["sourceCatalog"]["columns"]
    expected_owner = plan["sourceCatalog"]["owner"]
    source_primary = [
        item
        for item in plan["sourceCatalog"]["constraints"]
        if item["type"] == "p"
    ]
    if len(source_primary) != 1:
        raise MigrationError("source primary-key contract is not exact")
    expected_primary_definition = source_primary[0]["definition"]
    expected_check_name = replacement_instrument_check_name(
        args.run_id,
        target,
    )
    expected_check_definition = (
        f"CHECK (instrument = '{target.instrument}'::text)"
    )
    for child in shape.get("children") or []:
        constraints = child.get("constraints") or []
        primary_constraints = [
            item for item in constraints if item.get("type") == "p"
        ]
        checks = [
            item for item in constraints if item.get("type") == "c"
        ]
        if (
            child.get("owner") != expected_owner
            or child.get("columns") != expected_columns
            or len(constraints)
            != (2 if instrument_check_present else 1)
            or len(primary_constraints) != 1
            or primary_constraints[0].get("definition")
            != expected_primary_definition
            or primary_constraints[0].get("validated") is not True
        ):
            raise MigrationError(
                "generation child column/constraint contract is unexpected"
            )
        if instrument_check_present:
            if (
                len(checks) != 1
                or checks[0].get("name") != expected_check_name
                or checks[0].get("definition")
                != expected_check_definition
                or checks[0].get("validated") is not True
            ):
                raise MigrationError(
                    "generation child instrument check is unexpected"
                )
        elif checks:
            raise MigrationError(
                "generation child retained an unexpected check"
            )
        indexes = child.get("indexes") or []
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
        if (
            len(indexes) != 2
            or len(primary_indexes) != 1
            or len(score_indexes) != 1
            or primary_indexes[0].get("name")
            != primary_constraints[0].get("name")
            or primary_indexes[0].get("parentIndex")
            != primary_parent
            or primary_indexes[0].get("isUnique") is not True
            or primary_indexes[0].get("isValid") is not True
            or primary_indexes[0].get("relationKind") != "i"
            or primary_indexes[0].get("tablespace") != "pg_default"
            or "(snapshot_id, song_id, instrument, account_id)"
            not in primary_indexes[0].get("definition", "")
            or score_indexes[0].get("parentIndex") != score_parent
            or score_indexes[0].get("isUnique") is not False
            or score_indexes[0].get("isValid") is not True
            or score_indexes[0].get("relationKind") != "i"
            or score_indexes[0].get("tablespace") != "pg_default"
        ):
            raise MigrationError(
                "generation child index contract is unexpected"
            )


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
    archive_budget = math.ceil(source * 2.20)
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


def load_archive_manifest(
    root,
    target,
    *,
    verify_checksum=True,
    expected_run_id=None,
    expected_plan_id=None,
):
    manifest = read_json(archive_manifest_path(root, target))
    if (
            manifest.get("formatVersion") != FORMAT_VERSION
            or manifest.get("toolId") != TOOL_ID
            or manifest.get("targetKey") != target.key
            or manifest.get("target")
            != f"{TARGET_SCHEMA}.{target.partition}"
            or manifest.get("instrument") != target.instrument
            or (
                expected_run_id is not None
                and manifest.get("runId") != expected_run_id
            )
            or (
                expected_plan_id is not None
                and manifest.get("planId") != expected_plan_id
            )
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


def verify_archive_evidence_chain(
    args,
    target,
    plan,
    archive_report,
    restore_report,
    validation_report,
    *,
    verify_archive_checksum=True,
):
    for label, report in (
        ("archive", archive_report),
        ("restore", restore_report),
        ("validation", validation_report),
    ):
        if (
            report.get("runId") != args.run_id
            or report.get("planId") != plan["planId"]
            or report.get("targetKey") != target.key
        ):
            raise MigrationError(
                f"{label} evidence belongs to another migration"
            )
    manifest_path = archive_manifest_path(args.scratch_root, target)
    manifest = load_archive_manifest(
        args.scratch_root,
        target,
        verify_checksum=verify_archive_checksum,
        expected_run_id=args.run_id,
        expected_plan_id=plan["planId"],
    )
    manifest_sha = sha256_path(manifest_path)
    archive_sha = manifest["archive"]["sha256"]
    if archive_report.get("archiveManifest") != {
        "path": str(manifest_path),
        "sha256": manifest_sha,
    }:
        raise MigrationError("archive report manifest binding changed")
    if (
        archive_report.get("archive", {}).get("sha256") != archive_sha
        or restore_report.get("archive", {}).get("sha256") != archive_sha
        or validation_report.get("archive", {}).get("sha256")
        != archive_sha
        or validation_report.get("archive", {}).get("restoreProved")
        is not True
    ):
        raise MigrationError("archive SHA chain is inconsistent")
    return {
        "manifest": manifest,
        "manifestPath": str(manifest_path),
        "manifestSha256": manifest_sha,
        "archivePath": manifest["archive"]["path"],
        "archiveSha256": archive_sha,
        "reportPaths": {
            stage: str(report_path(args.scratch_root, stage))
            for stage in (
                "check",
                "plan",
                "archive",
                "restore",
                "validate",
            )
        },
        "reportSha256": {
            stage: sha256_path(report_path(args.scratch_root, stage))
            for stage in (
                "check",
                "plan",
                "archive",
                "restore",
                "validate",
            )
        },
    }


def drop_recovery_evidence_path(root):
    return pathlib.Path(root) / REPORTS_DIR / "drop.recovery.json"


def has_drop_recovery_package(root):
    package_root = (
        pathlib.Path(root)
        / RECOVERED_DIR
        / "drop-recovery-package"
    )
    return bool(
        list(
            package_root.glob(
                ".drop-recovery-manifest-*.recovery"
            )
        )
        or list(
            (
                pathlib.Path(root) / REPORTS_DIR
            ).glob(".drop-recovery-manifest-*.recovery")
        )
    )


def write_drop_recovery_evidence(args, target, plan, pins):
    copies = {
        pin.label: pin.recovery_evidence(include_fallbacks=True)
        for pin in pins
    }
    return write_integrity_evidence(
        drop_recovery_evidence_path(args.scratch_root),
        {
            "formatVersion": FORMAT_VERSION,
            "toolId": TOOL_ID,
            "runId": args.run_id,
            "planId": plan["planId"],
            "targetKey": target.key,
            "target": f"{TARGET_SCHEMA}.{target.partition}",
            "copies": copies,
        },
    )


def select_recovery_copy(
    entry,
    label,
    *,
    verify_checksum=True,
):
    candidates = list(entry.get("paths") or [])
    if entry.get("path") not in candidates:
        candidates.append(entry.get("path"))
    for value in candidates:
        if not value:
            continue
        path = pathlib.Path(value)
        try:
            metadata = path.lstat()
        except FileNotFoundError:
            continue
        if (
            path.is_symlink()
            or not stat.S_ISREG(metadata.st_mode)
            or metadata.st_mode & 0o222
            or (
                verify_checksum
                and sha256_path(path) != entry.get("sha256")
            )
        ):
            continue
        return path
    raise MigrationError(
        f"authoritative {label} recovery copy is unavailable"
    )


def repair_recovery_working_copy(entry, selected, label):
    working_value = entry.get("path")
    if not working_value:
        raise MigrationError(
            f"{label} recovery lacks a working path"
        )
    working = pathlib.Path(working_value)
    if working == selected:
        return working
    with contextlib.suppress(FileNotFoundError):
        working.unlink()
    os.link(selected, working)
    fsync_directory(working.parent)
    metadata = working.lstat()
    if (
        working.is_symlink()
        or not stat.S_ISREG(metadata.st_mode)
        or metadata.st_mode & 0o222
        or sha256_path(working) != entry.get("sha256")
    ):
        raise MigrationError(
            f"{label} working recovery path could not be repaired"
        )
    return working


def load_drop_recovery_chain(
    args,
    target,
    plan=None,
    *,
    verify_archive_checksum=True,
):
    package_root = (
        pathlib.Path(args.scratch_root)
        / RECOVERED_DIR
        / "drop-recovery-package"
    )
    manifest_candidates = sorted(
        (
            pathlib.Path(args.scratch_root) / REPORTS_DIR
        ).glob(".drop-recovery-manifest-*.recovery")
    ) + sorted(
        package_root.glob(
            ".drop-recovery-manifest-*.recovery"
        )
    )
    valid_manifests = []
    for candidate in manifest_candidates:
        match = re.fullmatch(
            r"\.drop-recovery-manifest-([0-9a-f]{64})\.recovery",
            candidate.name,
        )
        if (
            match is not None
            and candidate.is_file()
            and not candidate.is_symlink()
            and sha256_path(candidate) == match.group(1)
        ):
            valid_manifests.append((candidate, match.group(1)))
    if (
        not valid_manifests
        or len({value for _, value in valid_manifests}) != 1
    ):
        raise MigrationError(
            "checksum-addressed drop recovery manifest is unavailable"
        )
    recovery_manifest_path, recovery_manifest_sha = valid_manifests[0]
    evidence = load_integrity_evidence(
        recovery_manifest_path,
        "drop recovery",
    )
    if (
        evidence.get("runId") != args.run_id
        or (
            plan is not None
            and evidence.get("planId") != plan["planId"]
        )
        or evidence.get("targetKey") != target.key
        or evidence.get("target")
        != f"{TARGET_SCHEMA}.{target.partition}"
    ):
        raise MigrationError(
            "drop recovery evidence belongs to another migration"
        )
    copies = evidence.get("copies") or {}
    required = {
        "archive",
        "manifest",
        "planReport",
        "archiveReport",
        "checkReport",
        "restoreReport",
        "validateReport",
    }
    if set(copies) != required:
        raise MigrationError("drop recovery package is incomplete")
    selected = {
        label: select_recovery_copy(
            copies[label],
            label,
            verify_checksum=(
                verify_archive_checksum
                or label != "archive"
            ),
        )
        for label in sorted(required)
    }
    selected = {
        label: repair_recovery_working_copy(
            copies[label],
            path,
            label,
        )
        for label, path in selected.items()
    }
    manifest = read_json(selected["manifest"])
    archive_sha = copies["archive"]["sha256"]
    if (
        manifest.get("runId") != args.run_id
        or manifest.get("planId") != evidence.get("planId")
        or manifest.get("targetKey") != target.key
        or manifest.get("archive", {}).get("sha256") != archive_sha
    ):
        raise MigrationError(
            "recovery manifest/archive identity is inconsistent"
        )
    reports = {}
    for stage in (
        "check",
        "plan",
        "archive",
        "restore",
        "validate",
    ):
        report = read_json(selected[f"{stage}Report"])
        if (
            report.get("stage") != stage
            or report.get("status") != "succeeded"
            or report.get("runId") != args.run_id
            or (
                stage != "check"
                and report.get("targetKey") != target.key
            )
            or report.get("integritySha256")
            != report_integrity(report)
        ):
            raise MigrationError(
                f"recovery {stage} report is inconsistent"
            )
        reports[stage] = report
    recovered_plan = reports["plan"]
    if plan is None:
        plan = recovered_plan
    if (
        recovered_plan.get("planId") != plan["planId"]
        or recovered_plan != plan
        or reports["archive"].get("planId") != plan["planId"]
        or reports["restore"].get("planId") != plan["planId"]
        or reports["validate"].get("planId") != plan["planId"]
        or reports["archive"].get("archive", {}).get("sha256")
        != archive_sha
        or reports["restore"].get("archive", {}).get("sha256")
        != archive_sha
        or reports["validate"].get("archive", {}).get("sha256")
        != archive_sha
    ):
        raise MigrationError("recovery report SHA chain is inconsistent")
    return {
        "manifest": manifest,
        "manifestPath": str(selected["manifest"]),
        "manifestSha256": copies["manifest"]["sha256"],
        "archivePath": str(selected["archive"]),
        "archiveSha256": archive_sha,
        "reportPaths": {
            stage: str(selected[f"{stage}Report"])
            for stage in (
                "check",
                "plan",
                "archive",
                "restore",
                "validate",
            )
        },
        "reportSha256": {
            stage: copies[f"{stage}Report"]["sha256"]
            for stage in (
                "check",
                "plan",
                "archive",
                "restore",
                "validate",
            )
        },
        "recoveryEvidence": evidence,
        "dropRecoveryManifestPath": str(recovery_manifest_path),
        "dropRecoveryManifestSha256": recovery_manifest_sha,
        "reports": reports,
    }


LEASE_BREAK_REQUESTED = False


def file_lease_break_handler(_signal_number, _frame):
    global LEASE_BREAK_REQUESTED
    LEASE_BREAK_REQUESTED = True


def sha256_descriptor(descriptor):
    digest = hashlib.sha256()
    offset = 0
    while True:
        chunk = os.pread(descriptor, 1024 * 1024, offset)
        if not chunk:
            return digest.hexdigest()
        digest.update(chunk)
        offset += len(chunk)


def acquire_read_lease(descriptor):
    if not hasattr(fcntl, "F_SETLEASE"):
        raise MigrationError(
            "kernel file leases are unavailable"
        )
    current_handler = signal.getsignal(signal.SIGIO)
    if current_handler not in (
        signal.SIG_DFL,
        file_lease_break_handler,
    ):
        raise MigrationError("SIGIO is owned by another handler")
    signal.signal(signal.SIGIO, file_lease_break_handler)
    try:
        fcntl.fcntl(descriptor, fcntl.F_SETLEASE, fcntl.F_RDLCK)
    except OSError as error:
        raise MigrationError(
            "could not acquire a kernel read lease for evidence"
        ) from error


def copy_descriptor_to_recovery(
    source_descriptor,
    recovery_path,
    expected_sha256,
):
    temporary = recovery_path.with_name(
        f".{recovery_path.name}.partial-{os.getpid()}-{time.time_ns()}"
    )
    output = os.open(
        temporary,
        os.O_WRONLY
        | os.O_CREAT
        | os.O_EXCL
        | os.O_CLOEXEC
        | getattr(os, "O_NOFOLLOW", 0),
        0o400,
    )
    digest = hashlib.sha256()
    offset = 0
    try:
        while True:
            chunk = os.pread(source_descriptor, 8 * 1024 * 1024, offset)
            if not chunk:
                break
            digest.update(chunk)
            offset += len(chunk)
            remaining = memoryview(chunk)
            while remaining:
                written = os.write(output, remaining)
                remaining = remaining[written:]
        os.fsync(output)
        os.fchmod(output, 0o400)
    except Exception:
        os.close(output)
        with contextlib.suppress(FileNotFoundError):
            temporary.unlink()
        raise
    os.close(output)
    if digest.hexdigest() != expected_sha256:
        temporary.unlink()
        raise MigrationError(
            "source evidence checksum changed during recovery copy"
        )
    os.replace(temporary, recovery_path)
    fsync_directory(recovery_path.parent)


def cleanup_stale_recovery_partials(recovery_paths):
    touched = set()
    for recovery_path in recovery_paths:
        recovery_path = pathlib.Path(recovery_path)
        pattern = f".{recovery_path.name}.partial-*"
        for candidate in recovery_path.parent.glob(pattern):
            metadata = candidate.lstat()
            if (
                candidate.is_symlink()
                or not stat.S_ISREG(metadata.st_mode)
                or metadata.st_uid != os.getuid()
                or candidate.parent != recovery_path.parent
            ):
                raise MigrationError(
                    f"stale recovery partial is unsafe: {candidate}"
                )
            candidate.unlink()
            touched.add(candidate.parent)
    for directory in touched:
        fsync_directory(directory)


class PinnedFileEvidence:
    def __init__(
        self,
        label,
        path,
        recovery_path,
        anchor_path,
        expected_sha256,
    ):
        self.label = label
        self.path = pathlib.Path(path)
        self.recovery_path = pathlib.Path(recovery_path)
        self.anchor_path = pathlib.Path(anchor_path)
        self.expected_sha256 = expected_sha256
        self.source_descriptor = os.open(
            self.path,
            os.O_RDONLY
            | os.O_CLOEXEC
            | getattr(os, "O_NOFOLLOW", 0),
        )
        try:
            acquire_read_lease(self.source_descriptor)
        except Exception:
            os.close(self.source_descriptor)
            self.source_descriptor = None
            raise
        source = os.fstat(self.source_descriptor)
        observed = self.path.lstat()
        if (
            not stat.S_ISREG(source.st_mode)
            or self.path.is_symlink()
            or observed.st_dev != source.st_dev
            or observed.st_ino != source.st_ino
        ):
            self.close()
            raise MigrationError(
                f"source evidence path is unsafe: {self.path}"
            )
        self.source_identity = (
            source.st_dev,
            source.st_ino,
            source.st_size,
            source.st_mtime_ns,
            source.st_ctime_ns,
            source.st_nlink,
        )
        if not self.recovery_path.exists():
            if self.anchor_path.is_file() and not self.anchor_path.is_symlink():
                os.link(self.anchor_path, self.recovery_path)
                fsync_directory(self.recovery_path.parent)
            else:
                try:
                    copy_descriptor_to_recovery(
                        self.source_descriptor,
                        self.recovery_path,
                        expected_sha256,
                    )
                except Exception:
                    self.close()
                    raise
        recovery_metadata = self.recovery_path.lstat()
        if (
            self.recovery_path.is_symlink()
            or not stat.S_ISREG(recovery_metadata.st_mode)
            or recovery_metadata.st_mode & 0o222
            or os.path.samefile(self.path, self.recovery_path)
        ):
            self.close()
            raise MigrationError(
                f"recovery evidence is not independent and read-only: "
                f"{self.recovery_path}"
            )
        if not self.anchor_path.exists():
            os.link(self.recovery_path, self.anchor_path)
            fsync_directory(self.anchor_path.parent)
        if (
            self.anchor_path.is_symlink()
            or not self.anchor_path.is_file()
            or not os.path.samefile(
                self.recovery_path,
                self.anchor_path,
            )
        ):
            self.close()
            raise MigrationError(
                f"recovery evidence anchor is invalid: "
                f"{self.anchor_path}"
            )
        self.recovery_descriptor = os.open(
            self.recovery_path,
            os.O_RDONLY
            | os.O_CLOEXEC
            | getattr(os, "O_NOFOLLOW", 0),
        )
        try:
            acquire_read_lease(self.recovery_descriptor)
        except Exception:
            self.close()
            raise
        recovery = os.fstat(self.recovery_descriptor)
        self.recovery_identity = (
            recovery.st_dev,
            recovery.st_ino,
            recovery.st_size,
            recovery.st_mtime_ns,
            recovery.st_ctime_ns,
            recovery.st_nlink,
        )
        try:
            self.verify(checksum=True)
        except Exception:
            self.close()
            raise

    def verify(self, *, checksum):
        global LEASE_BREAK_REQUESTED
        source = os.fstat(self.source_descriptor)
        observed = self.path.lstat()
        recovery = os.fstat(self.recovery_descriptor)
        recovery_path = self.recovery_path.lstat()
        anchor_path = self.anchor_path.lstat()
        if (
            LEASE_BREAK_REQUESTED
            or self.path.is_symlink()
            or self.recovery_path.is_symlink()
            or (
                observed.st_dev,
                observed.st_ino,
                observed.st_size,
                observed.st_mtime_ns,
                observed.st_ctime_ns,
                observed.st_nlink,
            )
            != self.source_identity
            or (
                source.st_dev,
                source.st_ino,
                source.st_size,
                source.st_mtime_ns,
                source.st_ctime_ns,
                source.st_nlink,
            )
            != self.source_identity
            or (
                recovery_path.st_dev,
                recovery_path.st_ino,
                recovery_path.st_size,
                recovery_path.st_mtime_ns,
                recovery_path.st_ctime_ns,
                recovery_path.st_nlink,
            )
            != self.recovery_identity
            or (
                anchor_path.st_dev,
                anchor_path.st_ino,
                anchor_path.st_size,
                anchor_path.st_mtime_ns,
                anchor_path.st_ctime_ns,
                anchor_path.st_nlink,
            )
            != self.recovery_identity
            or (
                recovery.st_dev,
                recovery.st_ino,
                recovery.st_size,
                recovery.st_mtime_ns,
                recovery.st_ctime_ns,
                recovery.st_nlink,
            )
            != self.recovery_identity
            or (
                checksum
                and (
                    sha256_descriptor(self.source_descriptor)
                    != self.expected_sha256
                    or sha256_descriptor(self.recovery_descriptor)
                    != self.expected_sha256
                )
            )
        ):
            raise MigrationError(
                f"pinned evidence changed before commit: {self.path}"
            )

    def recovery_evidence(self, *, include_fallbacks):
        if (
            sha256_descriptor(self.recovery_descriptor)
            != self.expected_sha256
        ):
            raise MigrationError(
                f"recovery evidence checksum changed: "
                f"{self.recovery_path}"
            )
        descriptor_metadata = os.fstat(self.recovery_descriptor)

        def is_valid_path(path):
            try:
                metadata = path.lstat()
            except FileNotFoundError:
                return False
            return not (
                path.is_symlink()
                or not stat.S_ISREG(metadata.st_mode)
                or metadata.st_mode & 0o222
                or metadata.st_dev != descriptor_metadata.st_dev
                or metadata.st_ino != descriptor_metadata.st_ino
                or metadata.st_size != descriptor_metadata.st_size
            )

        anchor_valid = is_valid_path(self.anchor_path)
        recovery_valid = is_valid_path(self.recovery_path)
        if not recovery_valid and anchor_valid:
            with contextlib.suppress(FileNotFoundError):
                self.recovery_path.unlink()
            os.link(self.anchor_path, self.recovery_path)
            fsync_directory(self.recovery_path.parent)
            recovery_valid = is_valid_path(self.recovery_path)
        if not recovery_valid:
            raise MigrationError(
                f"recovery evidence has no durable path: "
                f"{self.recovery_path}"
            )
        valid_paths = [str(self.recovery_path)]
        if anchor_valid:
            valid_paths.append(str(self.anchor_path))
        result = {
            "path": str(self.recovery_path),
            "sha256": self.expected_sha256,
            "independentCopy": True,
            "readOnly": True,
        }
        if include_fallbacks:
            result["paths"] = valid_paths
        return result

    def close(self):
        for descriptor_name in (
            "recovery_descriptor",
            "source_descriptor",
        ):
            descriptor = getattr(self, descriptor_name, None)
            if descriptor is None:
                continue
            with contextlib.suppress(OSError):
                fcntl.fcntl(
                    descriptor,
                    fcntl.F_SETLEASE,
                    fcntl.F_UNLCK,
                )
            with contextlib.suppress(OSError):
                os.close(descriptor)
            setattr(self, descriptor_name, None)


class RetainedRecoveryEvidence:
    def __init__(self, label, path, expected_sha256):
        self.label = label
        self.path = pathlib.Path(path)
        self.expected_sha256 = expected_sha256
        self.descriptor = os.open(
            self.path,
            os.O_RDONLY
            | os.O_CLOEXEC
            | getattr(os, "O_NOFOLLOW", 0),
        )
        try:
            acquire_read_lease(self.descriptor)
            metadata = os.fstat(self.descriptor)
            observed = self.path.lstat()
            self.identity = (
                metadata.st_dev,
                metadata.st_ino,
                metadata.st_size,
                metadata.st_mtime_ns,
                metadata.st_ctime_ns,
                metadata.st_nlink,
            )
            if (
                self.path.is_symlink()
                or observed.st_mode & 0o222
                or (
                    observed.st_dev,
                    observed.st_ino,
                    observed.st_size,
                    observed.st_mtime_ns,
                    observed.st_ctime_ns,
                    observed.st_nlink,
                )
                != self.identity
            ):
                raise MigrationError(
                    f"retained recovery path is unsafe: {self.path}"
                )
            self.verify(checksum=True)
        except Exception:
            self.close()
            raise

    def verify(self, *, checksum):
        metadata = os.fstat(self.descriptor)
        observed = self.path.lstat()
        identity = (
            metadata.st_dev,
            metadata.st_ino,
            metadata.st_size,
            metadata.st_mtime_ns,
            metadata.st_ctime_ns,
            metadata.st_nlink,
        )
        observed_identity = (
            observed.st_dev,
            observed.st_ino,
            observed.st_size,
            observed.st_mtime_ns,
            observed.st_ctime_ns,
            observed.st_nlink,
        )
        if (
            LEASE_BREAK_REQUESTED
            or self.path.is_symlink()
            or observed.st_mode & 0o222
            or identity != self.identity
            or observed_identity != self.identity
            or (
                checksum
                and sha256_descriptor(self.descriptor)
                != self.expected_sha256
            )
        ):
            raise MigrationError(
                f"retained recovery evidence changed: {self.path}"
            )

    def recovery_evidence(self, *, include_fallbacks):
        self.verify(checksum=True)
        result = {
            "path": str(self.path),
            "sha256": self.expected_sha256,
            "independentCopy": True,
            "readOnly": True,
        }
        if include_fallbacks:
            result["paths"] = [str(self.path)]
        return result

    def close(self):
        descriptor = getattr(self, "descriptor", None)
        if descriptor is None:
            return
        with contextlib.suppress(OSError):
            fcntl.fcntl(
                descriptor,
                fcntl.F_SETLEASE,
                fcntl.F_UNLCK,
            )
        with contextlib.suppress(OSError):
            os.close(descriptor)
        self.descriptor = None


def pin_archive_evidence(chain):
    archive_path = pathlib.Path(chain["archivePath"])
    manifest_path = pathlib.Path(chain["manifestPath"])
    recovery_paths = archive_evidence_recovery_paths(chain)
    anchor_paths = archive_evidence_anchor_paths(chain)
    anchor_root = next(iter(anchor_paths.values())).parent
    anchor_root.parent.mkdir(
        parents=True,
        exist_ok=True,
        mode=0o700,
    )
    if not anchor_root.exists():
        anchor_root.mkdir(mode=0o700)
        fsync_directory(anchor_root.parent)
    anchor_metadata = anchor_root.lstat()
    if (
        anchor_root.is_symlink()
        or not stat.S_ISDIR(anchor_metadata.st_mode)
        or anchor_metadata.st_uid != os.getuid()
    ):
        raise MigrationError(
            "recovery package directory is unsafe"
        )
    sources = [
        (
            "archive",
            archive_path,
            recovery_paths["archive"],
            anchor_paths["archive"],
            chain["archiveSha256"],
        ),
        (
            "manifest",
            manifest_path,
            recovery_paths["manifest"],
            anchor_paths["manifest"],
            chain["manifestSha256"],
        ),
    ]
    sources.extend(
        (
            f"{stage}Report",
            pathlib.Path(path),
            recovery_paths[f"{stage}Report"],
            anchor_paths[f"{stage}Report"],
            chain["reportSha256"][stage],
        )
        for stage, path in (chain.get("reportPaths") or {}).items()
    )
    cleanup_stale_recovery_partials(
        recovery for _, _, recovery, _, _ in sources
    )
    required_bytes = sum(
        source.stat().st_size
        for _, source, recovery, _, _ in sources
        if not recovery.exists()
    )
    free_bytes = shutil.disk_usage(archive_path.parent).free
    if free_bytes - required_bytes < SCRATCH_RESERVE_BYTES:
        raise MigrationError(
            "scratch capacity cannot retain independent recovery copies"
        )
    global LEASE_BREAK_REQUESTED
    LEASE_BREAK_REQUESTED = False
    pins = []
    try:
        for label, source, recovery, anchor, expected_sha in sources:
            pins.append(
                PinnedFileEvidence(
                    label,
                    source,
                    recovery,
                    anchor,
                    expected_sha,
                )
            )
        return pins
    except Exception:
        for pin in pins:
            pin.close()
        raise


def pin_drop_recovery_manifest(args):
    path = drop_recovery_evidence_path(args.scratch_root)
    expected_sha = sha256_path(path)
    recovery_path = path.with_name(
        f".drop-recovery-manifest-{expected_sha}.recovery"
    )
    cleanup_stale_recovery_partials([recovery_path])
    package_root = (
        pathlib.Path(args.scratch_root)
        / RECOVERED_DIR
        / "drop-recovery-package"
    )
    return PinnedFileEvidence(
        "dropRecoveryManifest",
        path,
        recovery_path,
        package_root
        / f".drop-recovery-manifest-{expected_sha}.recovery",
        expected_sha,
    )


def seal_drop_recovery_package(args, *, required):
    package_root = (
        pathlib.Path(args.scratch_root)
        / RECOVERED_DIR
        / "drop-recovery-package"
    )
    if not package_root.exists():
        if required:
            raise MigrationError(
                "drop recovery package directory disappeared"
            )
        return False
    package_root.chmod(0o500)
    fsync_directory(package_root.parent)
    return True


def pin_retained_recovery_chain(chain):
    global LEASE_BREAK_REQUESTED
    LEASE_BREAK_REQUESTED = False
    sources = [
        (
            "archive",
            chain["archivePath"],
            chain["archiveSha256"],
        ),
        (
            "manifest",
            chain["manifestPath"],
            chain["manifestSha256"],
        ),
    ]
    sources.extend(
        (
            f"{stage}Report",
            path,
            chain["reportSha256"][stage],
        )
        for stage, path in chain["reportPaths"].items()
    )
    sources.append(
        (
            "dropRecoveryManifest",
            chain["dropRecoveryManifestPath"],
            chain["dropRecoveryManifestSha256"],
        )
    )
    pins = []
    try:
        for label, path, expected_sha in sources:
            pins.append(
                RetainedRecoveryEvidence(
                    label,
                    path,
                    expected_sha,
                )
            )
        return pins
    except Exception:
        for pin in pins:
            pin.close()
        raise


def archive_evidence_recovery_paths(chain):
    archive_path = pathlib.Path(chain["archivePath"])
    manifest_path = pathlib.Path(chain["manifestPath"])
    paths = {
        "archive": archive_path.with_name(
            "." + archive_path.name + ".drop-recovery"
        ),
        "manifest": manifest_path.with_name(
            "." + manifest_path.name + ".drop-recovery"
        ),
    }
    for stage, path in (chain.get("reportPaths") or {}).items():
        report = pathlib.Path(path)
        paths[f"{stage}Report"] = report.with_name(
            "." + report.name + ".drop-recovery"
        )
    return paths


def archive_evidence_anchor_paths(chain):
    root = pathlib.Path(chain["manifestPath"]).parent.parent
    anchor_root = (
        root
        / RECOVERED_DIR
        / "drop-recovery-package"
    )
    paths = {
        "archive": anchor_root / ".drop-archive.recovery",
        "manifest": anchor_root / ".drop-manifest.recovery",
    }
    for stage in (chain.get("reportPaths") or {}):
        paths[f"{stage}Report"] = (
            anchor_root / f".drop-{stage}-report.recovery"
        )
    return paths


def verify_retained_recovery_copies(pins):
    return {
        pin.label: pin.recovery_evidence(
            include_fallbacks=False
        )
        for pin in pins
    }


def wait_for_test_final_drop_gate(args, phase):
    if not args.test_final_drop_gate:
        return
    gate = pathlib.Path(args.test_final_drop_gate)
    ready = gate.with_name(gate.name + f".{phase}.ready")
    proceed = gate.with_name(gate.name + f".{phase}.continue")
    write_json_exclusive(
        ready,
        {
            "runId": args.run_id,
            "stage": "drop",
            "phase": phase,
            "status": "ready",
            "createdAtUtc": utc_now(),
        },
    )
    deadline = time.monotonic() + 120
    while time.monotonic() < deadline:
        if proceed.is_file() and not proceed.is_symlink():
            return
        time.sleep(0.05)
    raise MigrationError(
        "test final-drop decision gate timed out"
    )


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
                    SET LOCAL idle_in_transaction_session_timeout = 0;
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
                validate_partition_child_catalogs(
                    pre_swap_shape,
                    target,
                    plan,
                    args,
                    instrument_check_present=True,
                    primary_parent=replacement_primary_name(
                        args.run_id,
                        target,
                    ),
                    score_parent=replacement_score_name(
                        args.run_id,
                        target,
                    ),
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
                check_started = time.monotonic()
                original_check_name = ensure_original_instrument_check(
                    database,
                    args,
                    target,
                    args.build_timeout_seconds,
                )
                original_check_elapsed = (
                    time.monotonic() - check_started
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
                    "originalInstrumentCheck": {
                        "name": original_check_name,
                        "validationElapsedSeconds": round(
                            original_check_elapsed,
                            3,
                        ),
                        "validationTimeoutSeconds":
                            args.build_timeout_seconds,
                    },
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


def inspect_original_instrument_check(
    database,
    relation_name,
    check_name,
):
                return database.json(
                    f"""
                        SELECT json_build_object(
                            'validated', constraint_row.convalidated,
                            'definition', pg_get_constraintdef(
                                constraint_row.oid,
                                TRUE))
                        FROM pg_constraint constraint_row
                        WHERE constraint_row.conrelid =
                            'public.{relation_name}'::regclass
                          AND constraint_row.conname =
                            {sql_literal(check_name)}
                    """
                )


def ensure_original_instrument_check(
    database,
    args,
    target,
    validation_timeout_seconds,
):
                check_name = original_instrument_check_name(
                    args.run_id,
                    target,
                )
                database.psql(
                    f"""
                        BEGIN;
                        SET LOCAL lock_timeout = '2s';
                        SET LOCAL statement_timeout =
                            '{int(validation_timeout_seconds)}s';
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
                    timeout=max(
                        60,
                        int(validation_timeout_seconds) + 30,
                    ),
                )
                expected_definition = (
                    "CHECK (instrument = "
                    f"{sql_literal(target.instrument)}::text)"
                )
                observed = inspect_original_instrument_check(
                    database,
                    target.partition,
                    check_name,
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
                    original_check_name = build[
                        "originalInstrumentCheck"
                    ]["name"]
                    observed_check = inspect_original_instrument_check(
                        database,
                        target.partition,
                        original_check_name,
                    )
                    expected_check = {
                        "validated": True,
                        "definition": (
                            "CHECK (instrument = "
                            f"{sql_literal(target.instrument)}::text)"
                        ),
                    }
                    if observed_check != expected_check:
                        raise MigrationError(
                            "original instrument check changed before swap"
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
                validate_partition_child_catalogs(
                    shape,
                    target,
                    plan,
                    args,
                    instrument_check_present=True,
                    primary_parent=replacement_primary_name(
                        args.run_id,
                        target,
                    ),
                    score_parent=replacement_score_name(
                        args.run_id,
                        target,
                    ),
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
                validate_partition_child_catalogs(
                    shape,
                    target,
                    plan,
                    args,
                    instrument_check_present=True,
                    primary_parent=replacement_primary_name(
                        args.run_id,
                        target,
                    ),
                    score_parent=replacement_score_name(
                        args.run_id,
                        target,
                    ),
                )
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
                    and len(catalog.get("constraints") or []) == 1
                    and len(primary_indexes) == 1
                    and len(score_indexes) == 1
                    and len(indexes) == 2
                    and primary[0]["name"] == source_names["primaryConstraint"]
                    and primary_indexes[0]["name"] == source_names["primaryIndex"]
                    and score_indexes[0]["name"] == source_names["scoreIndex"]
                )


def catalog_without_sizes(catalog):
                result = dict(catalog)
                for key in ("heapBytes", "indexBytes", "totalBytes"):
                    result.pop(key, None)
                return result


def validate_final_candidate_catalog(
    catalog,
    plan,
    target,
    source_names,
):
                if (
                    catalog.get("relationKind") != "p"
                    or catalog.get("partitionKey")
                    != "LIST (snapshot_id)"
                    or catalog.get("partitionBound") != target.bound
                    or catalog.get("owner")
                    != plan["sourceCatalog"].get("owner")
                    or catalog.get("tablespace") != "pg_default"
                    or catalog.get("columns")
                    != plan["sourceCatalog"].get("columns")
                    or not final_catalog_names_match(
                        catalog,
                        source_names,
                    )
                ):
                    raise MigrationError(
                        "final partitioned catalog is unexpected"
                    )


def rename_index_catalog_entry(entry, old_name, new_name):
                result = dict(entry)
                result["name"] = new_name
                definition = result.get("definition", "")
                for old_token, new_token in (
                    (
                        f"INDEX {old_name} ",
                        f"INDEX {new_name} ",
                    ),
                    (
                        f'INDEX "{old_name}" ',
                        f'INDEX "{new_name}" ',
                    ),
                ):
                    if old_token in definition:
                        result["definition"] = definition.replace(
                            old_token,
                            new_token,
                            1,
                        )
                        break
                else:
                    raise MigrationError(
                        f"cannot rename expected index definition {old_name}"
                    )
                return result


def expected_final_partition_evidence(
    validation,
    args,
    target,
    source_names,
):
                primary_before = replacement_primary_name(
                    args.run_id,
                    target,
                )
                score_before = replacement_score_name(
                    args.run_id,
                    target,
                )
                shape = json.loads(
                    json.dumps(validation["candidateShape"])
                )
                shape_indexes = []
                for entry in shape["indexes"]:
                    if entry["name"] == primary_before:
                        entry = rename_index_catalog_entry(
                            entry,
                            primary_before,
                            source_names["primaryIndex"],
                        )
                    elif entry["name"] == score_before:
                        entry = rename_index_catalog_entry(
                            entry,
                            score_before,
                            source_names["scoreIndex"],
                        )
                    else:
                        raise MigrationError(
                            "candidate shape has an unexpected root index"
                        )
                    shape_indexes.append(entry)
                shape["indexes"] = sorted(
                    shape_indexes,
                    key=lambda item: item["name"],
                )
                for child in shape.get("children") or []:
                    child["constraints"] = [
                        entry
                        for entry in child.get("constraints") or []
                        if entry.get("name")
                        != replacement_instrument_check_name(
                            args.run_id,
                            target,
                        )
                    ]
                    for entry in child.get("indexes") or []:
                        if entry.get("parentIndex") == primary_before:
                            entry["parentIndex"] = source_names[
                                "primaryIndex"
                            ]
                        elif entry.get("parentIndex") == score_before:
                            entry["parentIndex"] = source_names[
                                "scoreIndex"
                            ]

                catalog = json.loads(
                    json.dumps(validation["candidateCatalog"])
                )
                constraints = []
                for entry in catalog["constraints"]:
                    if entry["name"] == replacement_instrument_check_name(
                        args.run_id,
                        target,
                    ):
                        continue
                    if entry["name"] == primary_before:
                        entry["name"] = source_names[
                            "primaryConstraint"
                        ]
                    constraints.append(entry)
                catalog["constraints"] = sorted(
                    constraints,
                    key=lambda item: item["name"],
                )
                indexes = []
                for entry in catalog["indexes"]:
                    if entry["name"] == primary_before:
                        entry = rename_index_catalog_entry(
                            entry,
                            primary_before,
                            source_names["primaryIndex"],
                        )
                    elif entry["name"] == score_before:
                        entry = rename_index_catalog_entry(
                            entry,
                            score_before,
                            source_names["scoreIndex"],
                        )
                    else:
                        raise MigrationError(
                            "candidate catalog has an unexpected root index"
                        )
                    indexes.append(entry)
                catalog["indexes"] = sorted(
                    indexes,
                    key=lambda item: item["name"],
                )
                return shape, catalog_without_sizes(catalog)


def transactional_partition_evidence_guard_sql(
    target,
    expected_shape,
    expected_catalog,
    label,
):
                shape_query = partition_shape_query(
                    target,
                    target.partition,
                )
                catalog = catalog_query(target.partition)
                return f"""
                    DO ${label}$
                    DECLARE
                        locked_shape JSONB;
                        locked_catalog JSONB;
                    BEGIN
                        SELECT result.value::jsonb
                        INTO locked_shape
                        FROM ({shape_query}) result;
                        SELECT (
                            result.value::jsonb
                            - 'heapBytes'
                            - 'indexBytes'
                            - 'totalBytes')
                        INTO locked_catalog
                        FROM ({catalog}) result;
                        IF locked_shape IS DISTINCT FROM
                                {sql_literal(json.dumps(
                                    expected_shape,
                                    sort_keys=True,
                                    separators=(",", ":"),
                                ))}::jsonb
                           OR locked_catalog IS DISTINCT FROM
                                {sql_literal(json.dumps(
                                    expected_catalog,
                                    sort_keys=True,
                                    separators=(",", ":"),
                                ))}::jsonb
                        THEN
                            RAISE EXCEPTION
                                'partition evidence changed at {label}';
                        END IF;
                    END
                    ${label}$;
                """


def pre_drop_reproof_sql(args, target, retired):
                target_fingerprint = fingerprint_sql(target.partition)
                original_fingerprint = fingerprint_sql(retired)
                original_distribution = snapshot_distribution_query(retired)
                target_state = relation_state_query(target.partition)
                original_state = relation_state_query(retired)
                return f"""
                    BEGIN;
                    SET LOCAL idle_in_transaction_session_timeout = 0;
                    SET LOCAL lock_timeout = '2s';
                    SET LOCAL statement_timeout =
                        '{int(args.query_timeout_seconds)}s';
                    LOCK TABLE {qualified(target.partition)} IN SHARE MODE;
                    LOCK TABLE {qualified(retired)} IN SHARE MODE;
                    WITH publication_slots AS (
                        SELECT current_publication_id AS publication_id
                        FROM scrape_publication_state
                        WHERE id = TRUE
                        UNION ALL
                        SELECT previous_publication_id
                        FROM scrape_publication_state
                        WHERE id = TRUE
                        UNION ALL
                        SELECT working_publication_id
                        FROM scrape_publication_state
                        WHERE id = TRUE
                    ),
                    resolved_publications AS (
                        SELECT generation.scrape_id
                        FROM publication_slots slot
                        JOIN publication_generations generation
                          ON generation.publication_id =
                                slot.publication_id
                        WHERE slot.publication_id IS NOT NULL
                    ),
                    protected AS (
                        SELECT active_snapshot_id AS snapshot_id
                        FROM leaderboard_snapshot_state
                        WHERE instrument =
                                {sql_literal(target.instrument)}
                          AND active_snapshot_id IS NOT NULL
                        UNION
                        SELECT source_snapshot_id
                        FROM solo_current_projection_scope
                        WHERE instrument =
                                {sql_literal(target.instrument)}
                          AND source_snapshot_id IS NOT NULL
                        UNION
                        SELECT source.source_snapshot_id
                        FROM resolved_publications publication
                        JOIN leaderboard_published_scope_source source
                          ON source.published_scrape_id =
                                publication.scrape_id
                        WHERE source.instrument =
                                {sql_literal(target.instrument)}
                          AND source.source_kind = 'snapshot'
                          AND source.source_snapshot_id IS NOT NULL
                    )
                    SELECT json_build_object(
                        'publication', (
                            SELECT json_build_object(
                                'publishedScrapeId',
                                    published_scrape_id,
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
                        'protectedSnapshotIds', (
                            SELECT COALESCE(
                                json_agg(
                                    DISTINCT snapshot_id
                                    ORDER BY snapshot_id),
                                '[]'::json)
                            FROM protected),
                        'targetFingerprint', (
                            SELECT value
                            FROM ({target_fingerprint}) fingerprint),
                        'originalFingerprint', (
                            SELECT value
                            FROM ({original_fingerprint}) fingerprint),
                        'originalDistribution', (
                            SELECT value
                            FROM ({original_distribution}) distribution),
                        'targetState', (
                            SELECT value
                            FROM ({target_state}) state),
                        'originalState', (
                            SELECT value
                            FROM ({original_state}) state))
                        AS value;
                    COMMIT;
                """


def final_drop_sql(
    args,
    target,
    plan,
    restore,
    validation,
    retired,
    source_names,
):
                expected_ids = sorted(
                    plan["planIdentity"]["protectedSnapshotIds"]
                )
                expected_ids_sql = ", ".join(
                    str(value) for value in expected_ids
                )
                expected_target_fingerprint = json.dumps(
                    plan["planIdentity"]["retainedFingerprint"],
                    sort_keys=True,
                    separators=(",", ":"),
                )
                expected_target_distribution = json.dumps(
                    normalized_distribution(
                        plan["planIdentity"]["retainedDistribution"]
                    ),
                    sort_keys=True,
                    separators=(",", ":"),
                )
                expected_original_fingerprint = json.dumps(
                    restore["restoredFingerprint"],
                    sort_keys=True,
                    separators=(",", ":"),
                )
                expected_original_distribution = json.dumps(
                    normalized_distribution(
                        restore["restoredDistribution"]
                    ),
                    sort_keys=True,
                    separators=(",", ":"),
                )
                publication = plan["planIdentity"]["referenceParity"][
                    "publication"
                ]
                expected_working = (
                    "NULL::bigint"
                    if publication.get("workingPublicationId") is None
                    else str(publication["workingPublicationId"])
                )
                locked_guard = (
                    transactional_partition_evidence_guard_sql(
                        target,
                        validation["candidateShape"],
                        catalog_without_sizes(
                            validation["candidateCatalog"]
                        ),
                        "locked_partition_guard",
                    )
                )
                final_shape, final_catalog = (
                    expected_final_partition_evidence(
                        validation,
                        args,
                        target,
                        source_names,
                    )
                )
                post_ddl_guard = (
                    transactional_partition_evidence_guard_sql(
                        target,
                        final_shape,
                        final_catalog,
                        "post_ddl_partition_guard",
                    )
                )
                target_fingerprint_query = fingerprint_sql(
                    target.partition
                )
                target_distribution_query = snapshot_distribution_query(
                    target.partition
                )
                original_fingerprint_query = fingerprint_sql(retired)
                original_distribution_query = snapshot_distribution_query(
                    retired
                )
                return f"""
                    BEGIN;
                    SET LOCAL idle_in_transaction_session_timeout = 0;
                    SET LOCAL lock_timeout = '2s';
                    SET LOCAL statement_timeout =
                        '{int(args.query_timeout_seconds)}s';
                    LOCK TABLE {qualified(target.partition)} IN SHARE MODE;
                    LOCK TABLE {qualified(retired)} IN SHARE MODE;
                    {locked_guard}
                    DO $content_reproof$
                    DECLARE
                        target_fingerprint JSONB;
                        target_distribution JSONB;
                        original_fingerprint JSONB;
                        original_distribution JSONB;
                        original_oid OID;
                        original_relfilenode OID;
                        original_heap BIGINT;
                        original_indexes BIGINT;
                        original_total BIGINT;
                    BEGIN
                        SELECT result.value::jsonb
                        INTO target_fingerprint
                        FROM ({target_fingerprint_query}) result;
                        IF target_fingerprint IS DISTINCT FROM
                                {sql_literal(expected_target_fingerprint)}::jsonb
                        THEN
                            RAISE EXCEPTION
                                'target fingerprint changed before final drop';
                        END IF;
                        SELECT result.value::jsonb
                        INTO target_distribution
                        FROM ({target_distribution_query}) result;
                        IF target_distribution IS DISTINCT FROM
                                {sql_literal(expected_target_distribution)}::jsonb
                        THEN
                            RAISE EXCEPTION
                                'target distribution changed before final drop';
                        END IF;

                        SELECT result.value::jsonb
                        INTO original_fingerprint
                        FROM ({original_fingerprint_query}) result;
                        SELECT result.value::jsonb
                        INTO original_distribution
                        FROM ({original_distribution_query}) result;
                        IF original_fingerprint IS DISTINCT FROM
                                {sql_literal(expected_original_fingerprint)}::jsonb
                           OR original_distribution IS DISTINCT FROM
                                {sql_literal(expected_original_distribution)}::jsonb
                        THEN
                            RAISE EXCEPTION
                                'retained original failed archive reproof';
                        END IF;

                        SELECT relation.oid,
                               relation.relfilenode,
                               pg_relation_size(relation.oid),
                               pg_indexes_size(relation.oid),
                               pg_total_relation_size(relation.oid)
                        INTO original_oid,
                             original_relfilenode,
                             original_heap,
                             original_indexes,
                             original_total
                        FROM pg_class relation
                        JOIN pg_namespace namespace
                          ON namespace.oid = relation.relnamespace
                        WHERE namespace.nspname = 'public'
                          AND relation.relname = {sql_literal(retired)};

                        IF original_oid IS DISTINCT FROM
                                {int(plan["planIdentity"]["sourceRelationIdentity"]["oid"])}
                           OR original_relfilenode IS DISTINCT FROM
                                {int(plan["planIdentity"]["sourceRelationIdentity"]["relfilenode"])}
                           OR original_heap IS DISTINCT FROM
                                {int(plan["planIdentity"]["sourceRelationIdentity"]["heapBytes"])}
                           OR original_indexes IS DISTINCT FROM
                                {int(plan["planIdentity"]["sourceRelationIdentity"]["indexBytes"])}
                           OR original_total IS DISTINCT FROM
                                {int(plan["planIdentity"]["sourceRelationIdentity"]["totalBytes"])}
                        THEN
                            RAISE EXCEPTION
                                'retained original physical identity changed';
                        END IF;
                    END
                    $content_reproof$;

                    SET LOCAL statement_timeout = '30s';
                    {advisory_lock_guard_sql()}
                    DO $final_drop_guard$
                    DECLARE
                        current_ids BIGINT[];
                    BEGIN
                        IF NOT EXISTS (
                            SELECT 1
                            FROM scrape_publication_state state
                            WHERE state.id = TRUE
                              AND state.published_scrape_id =
                                    {int(publication["publishedScrapeId"])}
                              AND state.current_publication_id =
                                    {int(publication["currentPublicationId"])}
                              AND state.previous_publication_id =
                                    {int(publication["previousPublicationId"])}
                              AND state.working_publication_id
                                    IS NOT DISTINCT FROM {expected_working}
                              AND state.public_reads_frozen = FALSE
                        ) THEN
                            RAISE EXCEPTION
                                'publication fence changed before final drop';
                        END IF;

                        WITH publication_slots AS (
                            SELECT current_publication_id AS publication_id
                            FROM scrape_publication_state
                            WHERE id = TRUE
                            UNION ALL
                            SELECT previous_publication_id
                            FROM scrape_publication_state
                            WHERE id = TRUE
                            UNION ALL
                            SELECT working_publication_id
                            FROM scrape_publication_state
                            WHERE id = TRUE
                        ),
                        resolved_publications AS (
                            SELECT generation.scrape_id
                            FROM publication_slots slot
                            JOIN publication_generations generation
                              ON generation.publication_id =
                                    slot.publication_id
                            WHERE slot.publication_id IS NOT NULL
                        ),
                        protected AS (
                            SELECT active_snapshot_id AS snapshot_id
                            FROM leaderboard_snapshot_state
                            WHERE instrument =
                                    {sql_literal(target.instrument)}
                              AND active_snapshot_id IS NOT NULL
                            UNION
                            SELECT source_snapshot_id
                            FROM solo_current_projection_scope
                            WHERE instrument =
                                    {sql_literal(target.instrument)}
                              AND source_snapshot_id IS NOT NULL
                            UNION
                            SELECT source.source_snapshot_id
                            FROM resolved_publications publication
                            JOIN leaderboard_published_scope_source source
                              ON source.published_scrape_id =
                                    publication.scrape_id
                            WHERE source.instrument =
                                    {sql_literal(target.instrument)}
                              AND source.source_kind = 'snapshot'
                              AND source.source_snapshot_id IS NOT NULL
                        )
                        SELECT COALESCE(
                            array_agg(
                                DISTINCT snapshot_id
                                ORDER BY snapshot_id),
                            ARRAY[]::bigint[])
                        INTO current_ids
                        FROM protected;

                        IF current_ids IS DISTINCT FROM
                                ARRAY[{expected_ids_sql}]::bigint[] THEN
                            RAISE EXCEPTION
                                'protected IDs changed before final drop';
                        END IF;

                        IF NOT EXISTS (
                            SELECT 1
                            FROM pg_inherits
                            WHERE inhparent =
                                'public.{TARGET_PARENT}'::regclass
                              AND inhrelid =
                                'public.{target.partition}'::regclass
                        ) OR (
                            SELECT relkind
                            FROM pg_class
                            WHERE oid =
                                'public.{target.partition}'::regclass
                        ) <> 'p' THEN
                            RAISE EXCEPTION
                                'accepted target is not attached partitioned';
                        END IF;
                    END
                    $final_drop_guard$;

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
                    {post_ddl_guard}
                    COMMIT;
                """


def final_drop_transaction_sql(
    args,
    target,
    plan,
    restore,
    validation,
    retired,
    source_names,
):
                sql = final_drop_sql(
                    args,
                    target,
                    plan,
                    restore,
                    validation,
                    retired,
                    source_names,
                )
                decision_boundary = (
                    "SET LOCAL statement_timeout = '30s';"
                )
                if sql.count(decision_boundary) != 1:
                    raise MigrationError(
                        "final drop SQL decision boundary is ambiguous"
                    )
                reproof_sql, ddl_sql = sql.split(
                    decision_boundary,
                    1,
                )
                ddl_sql = decision_boundary + ddl_sql
                if not ddl_sql.rstrip().endswith("COMMIT;"):
                    raise MigrationError(
                        "final drop SQL does not end with COMMIT"
                    )
                ddl_sql = ddl_sql.rstrip()[:-len("COMMIT;")]
                return (
                    reproof_sql,
                    ddl_sql,
                )


def stage_drop(args, runner):
                if not args.execute:
                    raise MigrationError("drop requires --execute")
                assert_no_emergency_breach(args.scratch_root)
                target = TARGET_BY_KEY[args.instrument]
                recovery_chain = None
                if has_drop_recovery_package(args.scratch_root):
                    recovery_chain = load_drop_recovery_chain(
                        args,
                        target,
                    )
                    recovered_reports = recovery_chain["reports"]
                    check = recovered_reports["check"]
                    plan = recovered_reports["plan"]
                    archive = recovered_reports["archive"]
                    restore = recovered_reports["restore"]
                    validation = recovered_reports["validate"]
                else:
                    check = load_report(args.scratch_root, "check")
                    plan = load_report(args.scratch_root, "plan")
                    validation = load_report(
                        args.scratch_root,
                        "validate",
                    )
                    archive = load_report(
                        args.scratch_root,
                        "archive",
                    )
                    restore = load_report(
                        args.scratch_root,
                        "restore",
                    )
                if validation.get("accepted") is not True:
                    raise MigrationError("validation was not accepted")
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
                statistics_counters_match = (
                    not retired_exists
                    or (
                        relation_mutation_counters(retired_state)
                        == relation_mutation_counters(plan["sourceState"])
                        and relation_statistics_epoch(retired_state)
                        == relation_statistics_epoch(plan["sourceState"])
                    )
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
                shape = database.json(
                    partition_shape_query(target, target.partition)
                )
                validate_partition_shape(shape, target, protected_now)
                validate_partition_child_catalogs(
                    shape,
                    target,
                    plan,
                    args,
                    instrument_check_present=retired_exists,
                    primary_parent=(
                        replacement_primary_name(args.run_id, target)
                        if retired_exists
                        else source_names["primaryIndex"]
                    ),
                    score_parent=(
                        replacement_score_name(args.run_id, target)
                        if retired_exists
                        else source_names["scoreIndex"]
                    ),
                )
                validate_parent_index_attachments(shape, plan)
                if recovery_chain is not None:
                    archive_chain = recovery_chain
                else:
                    archive_chain = verify_archive_evidence_chain(
                        args,
                        target,
                        plan,
                        archive,
                        restore,
                        validation,
                    )
                manifest = archive_chain["manifest"]
                pre_drop_api = (
                    capture_api_fingerprints(args.api_base)
                    if args.api_base
                    else []
                )
                if not compare_api_snapshots(
                    check.get("publicApiBaseline", []),
                    pre_drop_api,
                ):
                    raise MigrationError(
                        "public API parity changed before final drop"
                    )
                api_monitor = None
                expected_publication = plan["planIdentity"][
                    "referenceParity"
                ]["publication"]
                free_before = host["dataFilesystem"]["freeBytes"]
                started = time.monotonic()
                if recovery_chain is None:
                    archive_pins = pin_archive_evidence(
                        archive_chain
                    )
                    write_drop_recovery_evidence(
                        args,
                        target,
                        plan,
                        archive_pins,
                    )
                    archive_pins.append(
                        pin_drop_recovery_manifest(args)
                    )
                    seal_drop_recovery_package(
                        args,
                        required=True,
                    )
                    recovery_chain = load_drop_recovery_chain(
                        args,
                        target,
                        plan,
                    )
                else:
                    archive_pins = pin_retained_recovery_chain(
                        archive_chain
                    )
                    seal_drop_recovery_package(
                        args,
                        required=False,
                    )
                if retired_exists:
                    preproof = {
                        "publication": expected_publication,
                        "protectedSnapshotIds":
                            plan["planIdentity"][
                                "protectedSnapshotIds"
                            ],
                        "targetFingerprint":
                            plan["planIdentity"][
                                "retainedFingerprint"
                            ],
                        "targetDistribution":
                            plan["planIdentity"][
                                "retainedDistribution"
                            ],
                        "originalFingerprint":
                            restore["restoredFingerprint"],
                        "originalDistribution":
                            restore["restoredDistribution"],
                    }
                    reproof_sql, ddl_sql = (
                        final_drop_transaction_sql(
                            args,
                            target,
                            plan,
                            restore,
                            validation,
                            retired,
                            source_names,
                        )
                    )
                    session = None
                    try:
                        if args.api_base:
                            api_monitor = PublicApiMonitor(
                                args.api_base,
                                check.get("publicApiBaseline", []),
                            )
                            with api_monitor:
                                session = database.open_transaction(
                                    reproof_sql,
                                    timeout=(
                                        args.query_timeout_seconds
                                        + 120
                                    ),
                                    pgoptions=PLAN_QUERY_PGOPTIONS,
                                )
                            if api_monitor.failures:
                                session.rollback()
                                raise MigrationError(
                                    "public API health changed during "
                                    "final drop reproof"
                                )
                        else:
                            session = database.open_transaction(
                                reproof_sql,
                                timeout=args.query_timeout_seconds + 120,
                                pgoptions=PLAN_QUERY_PGOPTIONS,
                            )
                        wait_for_test_final_drop_gate(args, "pre-ddl")
                        current_chain = load_drop_recovery_chain(
                            args,
                            target,
                            plan,
                        )
                        if (
                            current_chain["manifestSha256"]
                            != archive_chain["manifestSha256"]
                            or current_chain["archiveSha256"]
                            != archive_chain["archiveSha256"]
                            or current_chain["reportSha256"]
                            != archive_chain["reportSha256"]
                        ):
                            session.rollback()
                            raise MigrationError(
                                "archive evidence chain changed during "
                                "final drop reproof"
                            )
                        for pin in archive_pins:
                            pin.verify(checksum=False)
                        session.execute_until_ready(ddl_sql)
                        wait_for_test_final_drop_gate(
                            args,
                            "pre-commit",
                        )
                        precommit_chain = (
                            load_drop_recovery_chain(
                                args,
                                target,
                                plan,
                                verify_archive_checksum=False,
                            )
                        )
                        if (
                            precommit_chain["manifestSha256"]
                            != archive_chain["manifestSha256"]
                            or precommit_chain["archiveSha256"]
                            != archive_chain["archiveSha256"]
                            or precommit_chain["reportSha256"]
                            != archive_chain["reportSha256"]
                        ):
                            session.rollback()
                            raise MigrationError(
                                "archive evidence chain changed during "
                                "final drop DDL"
                            )
                        for pin in archive_pins:
                            pin.verify(checksum=False)
                        wait_for_test_final_drop_gate(
                            args,
                            "commit-entry",
                        )
                        session.commit("COMMIT;")
                        wait_for_test_final_drop_gate(
                            args,
                            "post-commit",
                        )
                    except Exception:
                        if session is not None:
                            session.rollback()
                        for pin in archive_pins:
                            pin.close()
                        raise
                    fingerprint = preproof["targetFingerprint"]
                else:
                    fingerprint = database.json(
                        fingerprint_sql(target.partition),
                        timeout=args.query_timeout_seconds,
                        pgoptions=PLAN_QUERY_PGOPTIONS,
                    )
                    if (
                        fingerprint
                        != plan["planIdentity"]["retainedFingerprint"]
                    ):
                        raise MigrationError(
                            "final target fingerprint changed during "
                            "drop report recovery"
                        )
                    preproof = {
                        "publication": expected_publication,
                        "protectedSnapshotIds": protected_now,
                        "targetFingerprint": fingerprint,
                        "targetDistribution":
                            plan["planIdentity"][
                                "retainedDistribution"
                            ],
                        "originalFingerprint":
                            restore["restoredFingerprint"],
                        "originalDistribution":
                            restore["restoredDistribution"],
                    }
                if database.json(relation_state_query(retired)) is not None:
                    raise MigrationError(
                        "retained original remains after final drop"
                    )
                final_shape = database.json(
                    partition_shape_query(target, target.partition)
                )
                validate_partition_shape(final_shape, target, protected_now)
                validate_partition_child_catalogs(
                    final_shape,
                    target,
                    plan,
                    args,
                    instrument_check_present=False,
                    primary_parent=source_names["primaryIndex"],
                    score_parent=source_names["scoreIndex"],
                )
                validate_parent_index_attachments(final_shape, plan)
                final_catalog = database.json(catalog_query(target.partition))
                validate_final_candidate_catalog(
                    final_catalog,
                    plan,
                    target,
                    source_names,
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
                postcommit_chain = load_drop_recovery_chain(
                    args,
                    target,
                    plan,
                )
                if (
                    postcommit_chain["manifestSha256"]
                    != archive_chain["manifestSha256"]
                    or postcommit_chain["archiveSha256"]
                    != archive_chain["archiveSha256"]
                    or postcommit_chain["reportSha256"]
                    != archive_chain["reportSha256"]
                ):
                    raise MigrationError(
                        "archive evidence chain changed after final drop"
                    )
                wait_for_test_final_drop_gate(
                    args,
                    "report-entry",
                )
                retained_recovery = verify_retained_recovery_copies(
                    archive_pins
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
                        "path": retained_recovery["archive"]["path"],
                        "sha256": retained_recovery["archive"]["sha256"],
                        "originalPath": manifest["archive"]["path"],
                        "sourceLeaseBreakRequested":
                            LEASE_BREAK_REQUESTED,
                        "deletionDecision": "deferred",
                        "recoveryCopies": retained_recovery,
                    },
                    "rollbackModeAfterDrop": (
                        "archive restore only; rename-back rollback is unavailable"
                    ),
                    "targetFingerprint": fingerprint,
                    "finalShape": final_shape,
                    "finalCatalog": final_catalog,
                    "acceptedTablespace": "pg_default",
                    "temporaryScratchTablespaceUsed": False,
                    "statisticsCountersMatched":
                        statistics_counters_match,
                    "fullArchiveReproofRequired":
                        retired_exists
                        and not statistics_counters_match,
                    "preDropReproof": {
                        "publication": preproof["publication"],
                        "protectedSnapshotIds":
                            preproof["protectedSnapshotIds"],
                        "targetFingerprint":
                            preproof["targetFingerprint"],
                        "targetDistribution":
                            preproof["targetDistribution"],
                        "originalFingerprint":
                            preproof["originalFingerprint"],
                        "originalDistribution":
                            preproof["originalDistribution"],
                    },
                    "preDropApiMonitor": {
                        "samples":
                            api_monitor.samples
                            if api_monitor is not None
                            else 0,
                        "failures":
                            api_monitor.failures
                            if api_monitor is not None
                            else [],
                    },
                    "preDropPublicApi": pre_drop_api,
                    "publicApi": public_api,
                }
                report = write_stage_report(
                    args.scratch_root,
                    "drop",
                    body,
                    dependencies={
                        stage: {
                            "path": recovery_chain[
                                "reportPaths"
                            ][stage],
                            "sha256": recovery_chain[
                                "reportSha256"
                            ][stage],
                        }
                        for stage in (
                            "validate",
                            "archive",
                            "restore",
                        )
                    },
                )
                for pin in archive_pins:
                    pin.close()
                return report


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
                restore = load_report(args.scratch_root, "restore")
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
                counters_match = (
                    relation_mutation_counters(original_state)
                    == relation_mutation_counters(plan["sourceState"])
                    and relation_statistics_epoch(original_state)
                    == relation_statistics_epoch(plan["sourceState"])
                )
                full_archive_reproof = False
                if not counters_match:
                    original_fingerprint = database.json(
                        fingerprint_sql(original_name),
                        timeout=args.query_timeout_seconds,
                        pgoptions=PLAN_QUERY_PGOPTIONS,
                    )
                    original_distribution = database.json(
                        snapshot_distribution_query(original_name),
                        timeout=args.query_timeout_seconds,
                        pgoptions=PLAN_QUERY_PGOPTIONS,
                    )
                    if (
                        original_fingerprint
                        != restore["restoredFingerprint"]
                        or normalized_distribution(
                            original_distribution
                        )
                        != normalized_distribution(
                            restore["restoredDistribution"]
                        )
                    ):
                        raise MigrationError(
                            "retained original failed archive reproof "
                            "after statistics counter change"
                        )
                    full_archive_reproof = True
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
                    "statisticsCountersMatched": counters_match,
                    "fullArchiveReproofPerformed": full_archive_reproof,
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
                parser.add_argument(
                    "--test-final-drop-gate",
                    help=argparse.SUPPRESS,
                )
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
                if args.test_final_drop_gate:
                    if not args.test_mode or args.stage != "drop":
                        raise MigrationError(
                            "--test-final-drop-gate is restricted to "
                            "test-mode drop"
                        )
                    gate = pathlib.Path(
                        args.test_final_drop_gate
                    ).resolve()
                    scratch = pathlib.Path(args.scratch_root).resolve()
                    if not path_is_beneath(gate, scratch):
                        raise MigrationError(
                            "--test-final-drop-gate must be inside "
                            "the scratch workspace"
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
