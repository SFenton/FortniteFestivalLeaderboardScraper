#!/usr/bin/env python3

"""Archive and prove one immutable snapshot-generation retention candidate.

The source path is read-only. Selection is derived from the newest accepted
planner cycle, and the proof path restores only into an isolated PostgreSQL 17
container.
"""

import argparse
import contextlib
import fcntl
import hashlib
import json
import os
import pathlib
import re
import selectors
import shutil
import stat
import subprocess
import sys
import time
import uuid
from datetime import datetime, timezone
from pathlib import PurePosixPath


TOOL_ID = "fst.snapshot-generation-archive-only.v1"
SCHEMA_VERSION = 1
POSTGRES_MAJOR = 17
PLANNER_VERSION = 3
CONFIG_VERSION = 1
DEFAULT_STORAGE_ROOT = pathlib.Path("/mnt/docker-storage")
ARCHIVE_ROOT = pathlib.Path(
    "/mnt/docker-storage/Docker/FestivalServiceTracker/fst-data/"
    "evidence/snapshot-generation-archives"
)
OPERATION_LOCK_PATH = pathlib.Path(
    "/mnt/docker-storage/Docker/FestivalServiceTracker/fst-data/"
    "evidence/.snapshot-generation-archive-operation.lock"
)
DEFAULT_IMAGE = "postgres:17"
PROOF_CPUS = "1"
PROOF_MEMORY = "1g"
ROOT_RELATION = "leaderboard_entries_snapshot"
PK_COLUMNS = ("snapshot_id", "song_id", "instrument", "account_id")
IDENTIFIER = re.compile(r"^[a-z_][a-z0-9_]*$")
SHA256 = re.compile(r"^[0-9a-f]{64}$")
INSTRUMENTS = (
    ("solo-guitar", "Solo_Guitar", "leaderboard_entries_snapshot_solo_guitar"),
    ("solo-bass", "Solo_Bass", "leaderboard_entries_snapshot_solo_bass"),
    ("solo-vocals", "Solo_Vocals", "leaderboard_entries_snapshot_solo_vocals"),
    ("solo-drums", "Solo_Drums", "leaderboard_entries_snapshot_solo_drums"),
    (
        "pro-guitar",
        "Solo_PeripheralGuitar",
        "leaderboard_entries_snapshot_pro_guitar",
    ),
    (
        "pro-bass",
        "Solo_PeripheralBass",
        "leaderboard_entries_snapshot_pro_bass",
    ),
    (
        "pro-vocals",
        "Solo_PeripheralVocals",
        "leaderboard_entries_snapshot_pro_vocals",
    ),
    (
        "pro-cymbals",
        "Solo_PeripheralCymbals",
        "leaderboard_entries_snapshot_pro_cymbals",
    ),
    (
        "pro-drums",
        "Solo_PeripheralDrums",
        "leaderboard_entries_snapshot_pro_drums",
    ),
)
INSTRUMENT_BY_KEY = {
    key: {"key": key, "instrument": instrument, "rootRelation": relation}
    for key, instrument, relation in INSTRUMENTS
}
INSTRUMENT_BY_NAME = {
    value["instrument"]: value for value in INSTRUMENT_BY_KEY.values()
}
PACKAGE_FILES = (
    "archive.custom",
    "archive.toc",
    "catalog.json",
    "manifest.json",
)


class ArchiveError(RuntimeError):
    """A fail-closed archive or proof error."""


class CommandError(ArchiveError):
    def __init__(self, arguments, returncode, stdout=b"", stderr=b""):
        command = " ".join(str(value) for value in arguments)
        detail = stderr.decode("utf-8", "replace").strip()
        super().__init__(
            f"command failed with exit {returncode}: {command}"
            + (f"\n{detail}" if detail else "")
        )
        self.arguments = tuple(arguments)
        self.returncode = returncode
        self.stdout = stdout
        self.stderr = stderr


def utc_now():
    return datetime.now(timezone.utc).isoformat().replace("+00:00", "Z")


def canonical_json_bytes(value):
    return (
        json.dumps(
            value,
            sort_keys=True,
            separators=(",", ":"),
            ensure_ascii=False,
        )
        + "\n"
    ).encode("utf-8")


def dotnet_canonical_json_bytes(value):
    def encode_string(text):
        pieces = ['"']
        for character in text:
            code = ord(character)
            short = {
                8: "\\b",
                9: "\\t",
                10: "\\n",
                12: "\\f",
                13: "\\r",
                92: "\\\\",
            }.get(code)
            if short is not None:
                pieces.append(short)
            elif (
                32 <= code <= 126
                and code not in (34, 38, 39, 43, 60, 62, 96)
            ):
                pieces.append(character)
            else:
                utf16 = character.encode("utf-16-be")
                for index in range(0, len(utf16), 2):
                    unit = int.from_bytes(utf16[index : index + 2], "big")
                    pieces.append(f"\\u{unit:04X}")
        pieces.append('"')
        return "".join(pieces)

    def write(item):
        if item is None:
            return "null"
        if item is True:
            return "true"
        if item is False:
            return "false"
        if isinstance(item, int):
            return str(item)
        if isinstance(item, float):
            raise ArchiveError(
                "planner canonical evidence cannot contain floating-point values"
            )
        if isinstance(item, str):
            return encode_string(item)
        if isinstance(item, list) or isinstance(item, tuple):
            return "[" + ",".join(write(child) for child in item) + "]"
        if isinstance(item, dict):
            properties = []
            for key in sorted(item, key=str):
                if item[key] is None:
                    continue
                properties.append(
                    encode_string(str(key)) + ":" + write(item[key])
                )
            return "{" + ",".join(properties) + "}"
        raise ArchiveError(
            f"unsupported planner canonical JSON value: {type(item).__name__}"
        )

    return write(value).encode("utf-8")


def sha256_bytes(value):
    return hashlib.sha256(value).hexdigest()


def sha256_path(path):
    digest = hashlib.sha256()
    with pathlib.Path(path).open("rb") as handle:
        for chunk in iter(lambda: handle.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def write_json(path, value):
    path = pathlib.Path(path)
    path.parent.mkdir(parents=True, exist_ok=True, mode=0o700)
    partial = path.with_name(f".{path.name}.partial-{os.getpid()}")
    descriptor = os.open(
        partial,
        os.O_WRONLY | os.O_CREAT | os.O_EXCL | os.O_CLOEXEC,
        0o600,
    )
    try:
        handle = os.fdopen(descriptor, "wb")
        descriptor = None
        with handle:
            handle.write(canonical_json_bytes(value))
            handle.flush()
            os.fsync(handle.fileno())
        os.replace(partial, path)
    finally:
        if descriptor is not None:
            os.close(descriptor)
        with contextlib.suppress(FileNotFoundError):
            partial.unlink()


def write_bytes(path, value):
    path = pathlib.Path(path)
    partial = path.with_name(f".{path.name}.partial-{os.getpid()}")
    descriptor = os.open(
        partial,
        os.O_WRONLY | os.O_CREAT | os.O_EXCL | os.O_CLOEXEC,
        0o600,
    )
    try:
        handle = os.fdopen(descriptor, "wb")
        descriptor = None
        with handle:
            handle.write(value)
            handle.flush()
            os.fsync(handle.fileno())
        os.replace(partial, path)
    finally:
        if descriptor is not None:
            os.close(descriptor)
        with contextlib.suppress(FileNotFoundError):
            partial.unlink()


def run(arguments, *, input_bytes=None, timeout=300, check=True):
    completed = subprocess.run(
        [str(value) for value in arguments],
        input=input_bytes,
        capture_output=True,
        timeout=timeout,
    )
    if check and completed.returncode != 0:
        raise CommandError(
            arguments,
            completed.returncode,
            completed.stdout,
            completed.stderr,
        )
    return completed


def require_commands(*commands):
    missing = [command for command in commands if shutil.which(command) is None]
    if missing:
        raise ArchiveError(
            "required command(s) not found: " + ", ".join(missing)
        )


def remove_none(value):
    if isinstance(value, dict):
        return {
            key: remove_none(item)
            for key, item in value.items()
            if item is not None
        }
    if isinstance(value, list):
        return [remove_none(item) for item in value]
    return value


def validate_identifier(value, label):
    if not IDENTIFIER.fullmatch(str(value)):
        raise ArchiveError(f"{label} is not a safe PostgreSQL identifier")
    return str(value)


def quote_identifier(value):
    return '"' + validate_identifier(value, "identifier").replace('"', '""') + '"'


def sql_literal(value):
    return "'" + str(value).replace("'", "''") + "'"


def is_beneath(path, parent):
    try:
        pathlib.Path(path).relative_to(pathlib.Path(parent))
        return True
    except ValueError:
        return False


def paths_overlap(left, right):
    left = pathlib.Path(left).resolve(strict=False)
    right = pathlib.Path(right).resolve(strict=False)
    return (
        left == right
        or is_beneath(left, right)
        or is_beneath(right, left)
    )


def posix_relative_to(path, parent):
    try:
        return PurePosixPath(path).relative_to(PurePosixPath(parent))
    except ValueError as error:
        raise ArchiveError(
            f"container path {path} is outside expected mount {parent}"
        ) from error


def validate_existing_components_no_symlinks(path):
    path = pathlib.Path(path)
    current = pathlib.Path(path.anchor)
    for part in path.parts[1:]:
        current /= part
        if not current.exists():
            break
        metadata = current.lstat()
        if stat.S_ISLNK(metadata.st_mode):
            raise ArchiveError(f"path contains a symbolic link: {current}")


def validate_storage_path(path, storage_root, *, must_exist, new_directory=False):
    requested = pathlib.Path(path)
    root = pathlib.Path(storage_root)
    if not requested.is_absolute() or not root.is_absolute():
        raise ArchiveError("storage paths must be absolute")
    if any(part in (".", "..") for part in requested.parts):
        raise ArchiveError("storage path cannot contain traversal segments")
    if not root.exists() or not root.is_dir() or root.is_symlink():
        raise ArchiveError("approved storage root is unavailable or unsafe")
    validate_existing_components_no_symlinks(root)
    validate_existing_components_no_symlinks(requested)
    resolved_root = root.resolve(strict=True)
    if must_exist:
        if not requested.exists():
            raise ArchiveError(f"required path does not exist: {requested}")
        resolved = requested.resolve(strict=True)
    else:
        parent = requested.parent
        if not parent.exists() or not parent.is_dir() or parent.is_symlink():
            raise ArchiveError(
                f"operator-created parent must be an existing directory: {parent}"
            )
        resolved = parent.resolve(strict=True) / requested.name
    if not is_beneath(resolved, resolved_root) or resolved == resolved_root:
        raise ArchiveError(
            f"path must be below approved storage root {resolved_root}: {resolved}"
        )
    if new_directory and requested.exists():
        raise ArchiveError(f"output directory already exists: {requested}")
    if must_exist and requested.is_symlink():
        raise ArchiveError(f"path cannot be a symbolic link: {requested}")
    return resolved


@contextlib.contextmanager
def operation_lock(expected_archive_mount, protected_locations):
    root = validate_storage_path(
        ARCHIVE_ROOT,
        DEFAULT_STORAGE_ROOT,
        must_exist=True,
    )
    current_mount = mount_evidence(root)
    if mount_identity(current_mount) != mount_identity(expected_archive_mount):
        raise ArchiveError("archive root mount identity changed before lock")
    reject_nested_mounts(root, "archive root")
    reject_output_overlap(
        root,
        protected_locations,
        output_mount=current_mount,
    )
    lock_path = OPERATION_LOCK_PATH
    if (
        not lock_path.is_file()
        or lock_path.is_symlink()
        or not is_beneath(lock_path.resolve(), DEFAULT_STORAGE_ROOT.resolve())
    ):
        raise ArchiveError(
            "pre-provisioned archive operation lock is missing or unsafe"
        )
    lock_parent_mount = mount_evidence(lock_path.parent)
    require_same_mount_device(
        mount_evidence(DEFAULT_STORAGE_ROOT),
        lock_parent_mount,
        "archive operation lock",
    )
    reject_output_overlap(
        lock_path,
        protected_locations,
        output_mount=lock_parent_mount,
    )
    before = lock_path.stat()
    descriptor = os.open(
        lock_path,
        os.O_RDONLY
        | os.O_CLOEXEC
        | getattr(os, "O_NOFOLLOW", 0),
    )
    opened = os.fstat(descriptor)
    if (opened.st_dev, opened.st_ino) != (before.st_dev, before.st_ino):
        os.close(descriptor)
        raise ArchiveError("archive operation lock identity changed")
    try:
        fcntl.flock(descriptor, fcntl.LOCK_EX | fcntl.LOCK_NB)
    except BlockingIOError as error:
        os.close(descriptor)
        raise ArchiveError(
            "another archive/prove operation owns the FST reservation lock"
        ) from error
    try:
        locked_mount = mount_evidence(root)
        if mount_identity(locked_mount) != mount_identity(
            expected_archive_mount
        ):
            raise ArchiveError(
                "archive root remounted during lock acquisition"
            )
        reject_nested_mounts(root, "archive root")
        reject_output_overlap(
            root,
            protected_locations,
            output_mount=locked_mount,
        )
        yield {
            "path": str(lock_path),
            "device": os.fstat(descriptor).st_dev,
            "archiveRootMount": mount_identity(locked_mount),
        }
    finally:
        fcntl.flock(descriptor, fcntl.LOCK_UN)
        os.close(descriptor)


def mount_evidence(path):
    completed = run(
        [
            "findmnt",
            "--json",
            "-T",
            path,
            "-o",
            "SOURCE,FSTYPE,FSROOT,MAJ:MIN,TARGET",
        ]
    )
    payload = json.loads(completed.stdout.decode("utf-8"))
    filesystems = payload.get("filesystems") or []
    if len(filesystems) != 1:
        raise ArchiveError(f"could not identify filesystem for {path}")
    row = filesystems[0]
    usage = shutil.disk_usage(path)
    return {
        "source": row["source"],
        "sourceDevice": str(row["source"]).split("[", 1)[0],
        "filesystemType": row["fstype"],
        "fsRoot": row["fsroot"],
        "deviceId": row["maj:min"],
        "mountTarget": row["target"],
        "totalBytes": usage.total,
        "usedBytes": usage.used,
        "freeBytes": usage.free,
        "hostDevice": os.stat(path).st_dev,
    }


def mount_location(path, evidence=None):
    path = pathlib.Path(path).resolve(strict=False)
    evidence = evidence or mount_evidence(path)
    relative = path.relative_to(
        pathlib.Path(evidence["mountTarget"]).resolve(strict=False)
    )
    object_path = pathlib.PurePosixPath(evidence["fsRoot"])
    for part in relative.parts:
        object_path /= part
    return {
        "path": str(path),
        "sourceDevice": evidence["sourceDevice"],
        "filesystemType": evidence["filesystemType"],
        "fsRoot": evidence["fsRoot"],
        "deviceId": evidence["deviceId"],
        "mountTarget": evidence["mountTarget"],
        "filesystemObjectPath": str(object_path),
    }


def mount_locations_overlap(left, right):
    if left["deviceId"] != right["deviceId"]:
        return False
    left_path = pathlib.PurePosixPath(left["filesystemObjectPath"])
    right_path = pathlib.PurePosixPath(right["filesystemObjectPath"])
    try:
        left_path.relative_to(right_path)
        return True
    except ValueError:
        pass
    try:
        right_path.relative_to(left_path)
        return True
    except ValueError:
        return False


def decode_mountinfo_path(value):
    return re.sub(
        r"\\([0-7]{3})",
        lambda match: chr(int(match.group(1), 8)),
        value,
    )


def read_mountinfo_entries(path="/proc/self/mountinfo"):
    entries = []
    for line in pathlib.Path(path).read_text(encoding="utf-8").splitlines():
        before, separator, after = line.partition(" - ")
        fields = before.split()
        trailing = after.split()
        if not separator or len(fields) < 6 or len(trailing) < 2:
            raise ArchiveError("host mountinfo contains a malformed row")
        entries.append(
            {
                "deviceId": fields[2],
                "fsRoot": decode_mountinfo_path(fields[3]),
                "mountTarget": decode_mountinfo_path(fields[4]),
                "filesystemType": trailing[0],
                "source": decode_mountinfo_path(trailing[1]),
            }
        )
    return entries


def reject_nested_mounts(path, label, entries=None):
    root = pathlib.Path(path).resolve(strict=True)
    nested = []
    for row in entries or read_mountinfo_entries():
        target = pathlib.Path(row["mountTarget"]).resolve(strict=False)
        if target != root and is_beneath(target, root):
            nested.append(str(target))
    if nested:
        raise ArchiveError(
            f"{label} contains nested mount boundaries: "
            + ", ".join(sorted(set(nested)))
        )


def require_same_mount_device(reference, observed, label):
    if (
        reference["sourceDevice"] != observed["sourceDevice"]
        or reference["deviceId"] != observed["deviceId"]
        or reference["filesystemType"] != observed["filesystemType"]
    ):
        raise ArchiveError(f"{label} is outside the approved FST mount identity")


def required_capacity_bytes(physical_bytes, archive_bytes=0):
    physical_bytes = int(physical_bytes)
    archive_bytes = int(archive_bytes)
    if physical_bytes < 0 or archive_bytes < 0:
        raise ArchiveError("capacity inputs must be non-negative")
    return max(
        2 * physical_bytes + archive_bytes + 1024**3,
        2 * 1024**3,
    )


def sql_without_literals(sql):
    result = []
    index = 0
    while index < len(sql):
        if sql.startswith("--", index):
            end = sql.find("\n", index)
            index = len(sql) if end < 0 else end
            continue
        if sql.startswith("/*", index):
            end = sql.find("*/", index + 2)
            if end < 0:
                raise ArchiveError("unterminated SQL comment")
            index = end + 2
            continue
        if sql[index] == "'":
            result.append("''")
            index += 1
            while index < len(sql):
                if sql[index] != "'":
                    index += 1
                    continue
                if index + 1 < len(sql) and sql[index + 1] == "'":
                    index += 2
                    continue
                index += 1
                break
            else:
                raise ArchiveError("unterminated SQL literal")
            continue
        result.append(sql[index])
        index += 1
    return "".join(result)


def assert_read_only_sql(sql):
    stripped = sql_without_literals(sql).strip()
    if ";" in stripped.rstrip(";"):
        raise ArchiveError("source SQL must contain exactly one statement")
    stripped = stripped.rstrip(";").strip()
    first = re.match(r"^[A-Za-z]+", stripped)
    if first is None or first.group(0).upper() not in ("SELECT", "WITH", "COPY"):
        raise ArchiveError("source SQL is outside the read-only allowlist")
    upper = stripped.upper()
    forbidden = (
        "ALTER",
        "ANALYZE",
        "CALL",
        "CLUSTER",
        "COMMENT",
        "CREATE",
        "DELETE",
        "DISCARD",
        "DO",
        "DROP",
        "GRANT",
        "INSERT",
        "LOCK",
        "MERGE",
        "REFRESH",
        "REINDEX",
        "REVOKE",
        "SECURITY",
        "SET",
        "TRUNCATE",
        "UPDATE",
        "VACUUM",
    )
    tokens = set(re.findall(r"[A-Z_]+", upper))
    present = sorted(tokens.intersection(forbidden))
    if present:
        raise ArchiveError(
            "source SQL contains forbidden operation(s): "
            + ", ".join(present)
        )
    if re.search(r"\bSELECT\b[\s\S]*\bINTO\b", upper):
        raise ArchiveError("source SQL cannot use SELECT INTO")
    dangerous_functions = (
        "DBLINK",
        "LO_EXPORT",
        "LO_IMPORT",
        "PG_CANCEL_BACKEND",
        "PG_LOG_BACKEND_MEMORY_CONTEXTS",
        "PG_RELOAD_CONF",
        "PG_ROTATE_LOGFILE",
        "PG_TERMINATE_BACKEND",
        "SET_CONFIG",
    )
    if any(re.search(rf"\b{name}\s*\(", upper) for name in dangerous_functions):
        raise ArchiveError("source SQL calls a forbidden function")
    if first.group(0).upper() == "COPY":
        if (
            not re.match(r"^COPY\s*\(\s*SELECT\b", upper)
            or not re.search(r"\bTO\s+STDOUT\s*$", upper)
            or re.search(r"\bFROM\s+STDIN\b", upper)
        ):
            raise ArchiveError("source COPY is outside the fingerprint allowlist")
    return sql


def psql_command(
    container,
    user,
    database,
    sql,
    *,
    statement_timeout_seconds=120,
):
    assert_read_only_sql(sql)
    if statement_timeout_seconds < 0:
        raise ArchiveError("statement timeout cannot be negative")
    return [
        "docker",
        "exec",
        "-e",
        "PGCONNECT_TIMEOUT=10",
        "-e",
        (
            "PGOPTIONS=-c lock_timeout=3s "
            f"-c statement_timeout={statement_timeout_seconds}s "
            "-c idle_in_transaction_session_timeout=130s "
            "-c application_name=fst-snapshot-generation-archive-only"
        ),
        container,
        "psql",
        "-X",
        "-q",
        "-v",
        "ON_ERROR_STOP=1",
        "-U",
        user,
        "-d",
        database,
        "-At",
        "-c",
        sql,
    ]


def psql_text(
    container,
    user,
    database,
    sql,
    *,
    timeout=180,
    statement_timeout_seconds=120,
):
    completed = run(
        psql_command(
            container,
            user,
            database,
            sql,
            statement_timeout_seconds=statement_timeout_seconds,
        ),
        timeout=timeout,
    )
    return completed.stdout.decode("utf-8").strip()


def psql_json(
    container,
    user,
    database,
    sql,
    *,
    timeout=180,
    statement_timeout_seconds=120,
):
    text = psql_text(
        container,
        user,
        database,
        sql,
        timeout=timeout,
        statement_timeout_seconds=statement_timeout_seconds,
    )
    if not text:
        raise ArchiveError("PostgreSQL query returned no JSON")
    try:
        return json.loads(text)
    except json.JSONDecodeError as error:
        raise ArchiveError("PostgreSQL query returned malformed JSON") from error


def candidate_selection_sql(instrument=None, snapshot_id=None):
    if (instrument is None) != (snapshot_id is None):
        raise ArchiveError(
            "--instrument and --snapshot-id must be supplied together"
        )
    predicate = ""
    if instrument is not None:
        if instrument not in INSTRUMENT_BY_KEY:
            raise ArchiveError("instrument is outside the fixed allowlist")
        exact = INSTRUMENT_BY_KEY[instrument]["instrument"]
        if int(snapshot_id) <= 0:
            raise ArchiveError("snapshot ID must be positive")
        predicate = (
            f"WHERE observation.instrument = {sql_literal(exact)} "
            f"AND observation.snapshot_id = {int(snapshot_id)}"
        )
    return f"""
        WITH latest_cycle AS (
            SELECT *
            FROM snapshot_generation_retention_cycles
            ORDER BY created_at DESC, cycle_id DESC
            LIMIT 1
        ),
        candidate_pool AS (
            SELECT observation.*
            FROM snapshot_generation_retention_observations observation
            JOIN latest_cycle cycle
              ON cycle.cycle_id = observation.cycle_id
            WHERE observation.classification = 'candidate'
              AND observation.planner_live = FALSE
              AND observation.oracle_live = FALSE
        ),
        selected AS (
            SELECT observation.*
            FROM candidate_pool observation
            {predicate}
            ORDER BY
                observation.snapshot_id,
                observation.instrument,
                observation.child_oid
            LIMIT 1
        )
        SELECT json_build_object(
            'cycle', (
                SELECT to_jsonb(cycle)
                FROM latest_cycle cycle
            ),
            'candidateCountActual', (
                SELECT COUNT(*)::BIGINT FROM candidate_pool
            ),
            'target', (
                SELECT to_jsonb(selected) FROM selected
            ),
            'observations', (
                SELECT COALESCE(
                    json_agg(
                        to_jsonb(observation)
                        ORDER BY observation.observation_id),
                    '[]'::json)
                FROM snapshot_generation_retention_observations observation
                JOIN latest_cycle cycle
                  ON cycle.cycle_id = observation.cycle_id
            ),
            'evidence', (
                SELECT COALESCE(
                    json_agg(
                        to_jsonb(evidence)
                        ORDER BY evidence.sequence),
                    '[]'::json)
                FROM snapshot_generation_retention_evidence evidence
                JOIN latest_cycle cycle
                  ON cycle.cycle_id = evidence.cycle_id
            ),
            'publicationState', (
                SELECT to_jsonb(state)
                FROM scrape_publication_state state
                WHERE state.id = TRUE
            ),
            'triggerScrape', (
                SELECT to_jsonb(scrape)
                FROM scrape_log scrape
                JOIN latest_cycle cycle
                  ON cycle.trigger_scrape_id = scrape.id
            ),
            'triggerPublication', (
                SELECT to_jsonb(generation)
                FROM publication_generations generation
                JOIN latest_cycle cycle
                  ON cycle.trigger_publication_id =
                        generation.publication_id
            ),
            'runningScrapes', (
                SELECT COALESCE(
                    json_agg(scrape.id ORDER BY scrape.id),
                    '[]'::json)
                FROM scrape_log scrape
                WHERE scrape.status = 'running'
            ),
            'activeHoldCount', (
                SELECT COUNT(*)::BIGINT
                FROM snapshot_generation_retention_holds hold_row
                JOIN selected target
                  ON target.instrument = hold_row.instrument
                 AND target.snapshot_id = hold_row.snapshot_id
                WHERE hold_row.released_at IS NULL
            ),
            'unreplayedWriterFailureCount', (
                SELECT COUNT(*)::BIGINT
                FROM scrape_writer_failures failure
                JOIN selected target
                  ON target.instrument = failure.instrument
                 AND target.snapshot_id = failure.scrape_id
                WHERE failure.replayed_at IS NULL
            )
        )
    """


def validate_preflight(value, requested_instrument=None, requested_snapshot=None):
    if not isinstance(value, dict):
        raise ArchiveError("planner preflight is missing")
    cycle = value.get("cycle")
    target = value.get("target")
    state = value.get("publicationState")
    scrape = value.get("triggerScrape")
    publication = value.get("triggerPublication")
    if not all(isinstance(item, dict) for item in (
        cycle,
        target,
        state,
        scrape,
        publication,
    )):
        raise ArchiveError("planner, target, scrape, or publication state is missing")
    material = validate_authentic_planner_evidence(value)
    if cycle.get("status") != "observed":
        raise ArchiveError("latest retention cycle is not observed")
    if cycle.get("report_only") is not True:
        raise ArchiveError("latest retention cycle is not report-only")
    if cycle.get("oracle_agreement") is not True:
        raise ArchiveError("latest retention cycle lacks oracle agreement")
    if int(cycle.get("blocked_count", -1)) != 0:
        raise ArchiveError("latest retention cycle contains blockers")
    if int(cycle.get("candidate_count", -1)) <= 0:
        raise ArchiveError("latest retention cycle has no candidates")
    if int(value.get("candidateCountActual", -1)) != int(
        cycle["candidate_count"]
    ):
        raise ArchiveError("latest candidate rows do not match cycle count")
    if cycle.get("global_blockers") not in ([], None):
        raise ArchiveError("latest retention cycle has global blockers")
    for planner_key, oracle_key in (
        ("planner_child_set", "oracle_child_set"),
        ("planner_live_set", "oracle_live_set"),
        ("planner_candidate_set", "oracle_candidate_set"),
    ):
        if cycle.get(planner_key) != cycle.get(oracle_key):
            raise ArchiveError("latest planner and oracle sets differ")
    if len(cycle.get("planner_candidate_set") or []) != int(
        cycle["candidate_count"]
    ):
        raise ArchiveError("latest candidate set size differs from cycle count")
    if (
        target.get("classification") != "candidate"
        or target.get("planner_live") is not False
        or target.get("oracle_live") is not False
        or target.get("blocker_codes") not in ([], None)
    ):
        raise ArchiveError("selected observation is not an unblocked candidate")
    selected_key = observation_physical_key(target)
    if material["candidates"].count(selected_key) != 1:
        raise ArchiveError("selected physical key is not unique in candidate set")
    matching_observations = [
        observation
        for observation in value["observations"]
        if observation["observation_id"] == target["observation_id"]
        and observation_physical_key(observation) == selected_key
    ]
    if len(matching_observations) != 1:
        raise ArchiveError("selected observation is not unique in latest cycle")
    if target.get("instrument") not in INSTRUMENT_BY_NAME:
        raise ArchiveError("selected observation instrument is unsupported")
    definition = INSTRUMENT_BY_NAME[target["instrument"]]
    expected_child = (
        f"{definition['rootRelation']}_s{int(target.get('snapshot_id', 0))}"
    )
    if (
        target.get("root_schema") != "public"
        or target.get("child_schema") != "public"
        or target.get("root_relation") != definition["rootRelation"]
        or target.get("child_relation") != expected_child
    ):
        raise ArchiveError("selected observation relation identity is invalid")
    if (
        target.get("instrument") == "Solo_Bass"
        and int(target.get("snapshot_id", 0)) == 1308
    ):
        raise ArchiveError("protected Solo Bass snapshot 1308 cannot be archived")
    if requested_instrument is not None:
        expected = INSTRUMENT_BY_KEY[requested_instrument]["instrument"]
        if (
            target.get("instrument") != expected
            or int(target.get("snapshot_id", 0)) != int(requested_snapshot)
        ):
            raise ArchiveError("requested immutable candidate was not selected")
    if (
        state.get("current_publication_id")
        != cycle.get("trigger_publication_id")
        or state.get("published_scrape_id")
        != cycle.get("trigger_scrape_id")
    ):
        raise ArchiveError("current publication differs from cycle trigger")
    if state.get("public_reads_frozen") is not False:
        raise ArchiveError("public reads are frozen")
    if state.get("working_publication_id") is not None:
        raise ArchiveError("a working publication exists")
    for key in (
        "publication_commit_intent_started_at",
        "publication_commit_intent_heartbeat_at",
        "publication_commit_intent_owner",
        "max_score_mutation_gate_token",
        "max_score_mutation_gate_publication_id",
        "max_score_mutation_gate_backend_pid",
        "max_score_mutation_gate_backend_start",
        "max_score_mutation_gate_acquired_at",
    ):
        if state.get(key) is not None:
            raise ArchiveError(f"publication mutation state remains active: {key}")
    if not (
        state.get("improvement_notifications_scrape_id")
        == cycle.get("trigger_scrape_id")
        and state.get("improvement_notifications_status") == "completed"
        and state.get("improvement_notifications_completed_at") is not None
        and state.get("improvement_notifications_projection_ready") is True
        and state.get("improvement_notifications_projection_scrape_id")
        == cycle.get("trigger_scrape_id")
    ):
        raise ArchiveError("trigger scrape notifications are not completed")
    if value.get("runningScrapes") not in ([], None):
        raise ArchiveError("a running or resumable scrape exists")
    if int(value.get("activeHoldCount", -1)) != 0:
        raise ArchiveError("an active hold protects the target")
    if int(value.get("unreplayedWriterFailureCount", -1)) != 0:
        raise ArchiveError("unreplayed writer failure protects the target")
    if (
        scrape.get("status") != "completed"
        or scrape.get("completed_at") is None
        or scrape.get("failed_at") is not None
    ):
        raise ArchiveError("trigger scrape is not exactly terminal completed")
    if (
        publication.get("publication_id")
        != cycle.get("trigger_publication_id")
        or publication.get("scrape_id") != cycle.get("trigger_scrape_id")
        or publication.get("status") != "current"
    ):
        raise ArchiveError("trigger publication identity is not current")
    for field in (
        "candidate_identity_hash",
        "observation_hash",
    ):
        if not SHA256.fullmatch(str(cycle.get(field, ""))):
            raise ArchiveError(f"cycle {field} is invalid")
    return value


def catalog_sql(schema, relations):
    values = ", ".join(
        f"({index}, {sql_literal(name)})"
        for index, name in enumerate(relations)
    )
    return f"""
        WITH requested(ordinal, relation_name) AS (
            VALUES {values}
        ),
        relation_rows AS (
            SELECT
                requested.ordinal,
                namespace.nspname AS schema_name,
                relation.relname AS relation_name,
                relation.oid,
                relation.relfilenode,
                relation.relkind,
                relation.relpersistence,
                pg_get_userbyid(relation.relowner) AS owner_name,
                COALESCE(
                    tablespace.spcname,
                    database_tablespace.spcname) AS tablespace_name,
                COALESCE(access_method.amname, '') AS access_method,
                COALESCE(relation.reloptions, ARRAY[]::TEXT[]) AS relation_options,
                COALESCE(pg_get_partkeydef(relation.oid), '') AS partition_key,
                COALESCE(
                    pg_get_expr(relation.relpartbound, relation.oid, TRUE),
                    '') AS partition_bound,
                parent.oid AS parent_oid,
                parent_namespace.nspname AS parent_schema,
                parent.relname AS parent_relation
            FROM requested
            LEFT JOIN pg_namespace namespace
              ON namespace.nspname = {sql_literal(schema)}
            LEFT JOIN pg_class relation
              ON relation.relnamespace = namespace.oid
             AND relation.relname = requested.relation_name
            LEFT JOIN pg_inherits inheritance
              ON inheritance.inhrelid = relation.oid
            LEFT JOIN pg_class parent
              ON parent.oid = inheritance.inhparent
            LEFT JOIN pg_namespace parent_namespace
              ON parent_namespace.oid = parent.relnamespace
            LEFT JOIN pg_tablespace tablespace
              ON tablespace.oid = relation.reltablespace
            LEFT JOIN pg_am access_method
              ON access_method.oid = relation.relam
            CROSS JOIN LATERAL (
                SELECT default_tablespace.spcname
                FROM pg_database database
                JOIN pg_tablespace default_tablespace
                  ON default_tablespace.oid = database.dattablespace
                WHERE database.datname = current_database()
            ) database_tablespace
        )
        SELECT COALESCE(
            json_agg(
                json_build_object(
                    'schema', row.schema_name,
                    'name', row.relation_name,
                    'oid', row.oid,
                    'relfilenode', row.relfilenode,
                    'relationKind', row.relkind,
                    'persistenceKind', row.relpersistence,
                    'owner', row.owner_name,
                    'tablespace', row.tablespace_name,
                    'accessMethod', row.access_method,
                    'relationOptions', row.relation_options,
                    'partitionKey', row.partition_key,
                    'partitionBound', row.partition_bound,
                    'parentOid', row.parent_oid,
                    'parentSchema', row.parent_schema,
                    'parentRelation', row.parent_relation,
                    'heapBytes', pg_relation_size(row.oid),
                    'indexBytes', pg_indexes_size(row.oid),
                    'totalBytes', pg_total_relation_size(row.oid),
                    'estimatedRows', GREATEST(
                        COALESCE(relation.reltuples, 0),
                        0)::BIGINT,
                    'mutationCounters', json_build_object(
                        'inserts', COALESCE(stats.n_tup_ins, 0),
                        'updates', COALESCE(stats.n_tup_upd, 0),
                        'removals', COALESCE(stats.n_tup_del, 0),
                        'statisticsResetAt', (
                            SELECT stats_reset
                            FROM pg_stat_database
                            WHERE datname = current_database())),
                    'columns', (
                        SELECT COALESCE(
                            json_agg(
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
                                ORDER BY attribute.attnum),
                            '[]'::json)
                        FROM pg_attribute attribute
                        LEFT JOIN pg_attrdef default_value
                          ON default_value.adrelid = attribute.attrelid
                         AND default_value.adnum = attribute.attnum
                        WHERE attribute.attrelid = row.oid
                          AND attribute.attnum > 0
                          AND NOT attribute.attisdropped),
                    'constraints', (
                        SELECT COALESCE(
                            json_agg(
                                json_build_object(
                                    'name', constraint_row.conname,
                                    'type', constraint_row.contype,
                                    'definition', pg_get_constraintdef(
                                        constraint_row.oid, TRUE),
                                    'validated', constraint_row.convalidated)
                                ORDER BY constraint_row.conname),
                            '[]'::json)
                        FROM pg_constraint constraint_row
                        WHERE constraint_row.conrelid = row.oid),
                    'indexes', (
                        SELECT COALESCE(
                            json_agg(
                                json_strip_nulls(json_build_object(
                                    'tableOid', metadata.indrelid::BIGINT,
                                    'indexOid', index_relation.oid::BIGINT,
                                    'indexRelfilenode',
                                        index_relation.relfilenode::BIGINT,
                                    'indexName', index_relation.relname,
                                    'relationKind', index_relation.relkind,
                                    'isValid', metadata.indisvalid,
                                    'isReady', metadata.indisready,
                                    'isPrimary', metadata.indisprimary,
                                    'isUnique', metadata.indisunique,
                                    'accessMethod', index_method.amname,
                                    'tablespaceName', COALESCE(
                                        index_tablespace.spcname,
                                        row.tablespace_name),
                                    'parentIndexOid',
                                        parent_index.oid::BIGINT,
                                    'parentIndexName',
                                        parent_index.relname,
                                    'definition',
                                        pg_get_indexdef(index_relation.oid),
                                    'columnNames', (
                                        SELECT COALESCE(
                                            json_agg(
                                                attribute.attname
                                                ORDER BY key.ordinality),
                                            '[]'::json)
                                        FROM unnest(metadata.indkey)
                                            WITH ORDINALITY key(attnum, ordinality)
                                        LEFT JOIN pg_attribute attribute
                                          ON attribute.attrelid =
                                                metadata.indrelid
                                         AND attribute.attnum = key.attnum
                                        WHERE key.ordinality <=
                                                metadata.indnkeyatts)))
                                ORDER BY index_relation.relname),
                            '[]'::json)
                        FROM pg_index metadata
                        JOIN pg_class index_relation
                          ON index_relation.oid = metadata.indexrelid
                        JOIN pg_am index_method
                          ON index_method.oid = index_relation.relam
                        LEFT JOIN pg_inherits index_inheritance
                          ON index_inheritance.inhrelid =
                                index_relation.oid
                        LEFT JOIN pg_class parent_index
                          ON parent_index.oid =
                                index_inheritance.inhparent
                        LEFT JOIN pg_tablespace index_tablespace
                          ON index_tablespace.oid =
                                index_relation.reltablespace
                        WHERE metadata.indrelid = row.oid)
                )
                ORDER BY row.ordinal),
            '[]'::json)
        FROM relation_rows row
        LEFT JOIN pg_class relation ON relation.oid = row.oid
        LEFT JOIN pg_stat_all_tables stats ON stats.relid = row.oid
    """


def capture_catalog(container, user, database, schema, relations):
    for relation in relations:
        validate_identifier(relation, "catalog relation")
    result = psql_json(
        container,
        user,
        database,
        catalog_sql(schema, relations),
    )
    if not isinstance(result, list) or len(result) != len(relations):
        raise ArchiveError("catalog capture did not return every requested relation")
    if any(item.get("oid") is None for item in result):
        raise ArchiveError("one or more archive relations are missing")
    return remove_none(result)


def planner_index_shape(index):
    keys = (
        "tableOid",
        "indexOid",
        "indexRelfilenode",
        "indexName",
        "relationKind",
        "isValid",
        "isReady",
        "isPrimary",
        "isUnique",
        "accessMethod",
        "tablespaceName",
        "parentIndexOid",
        "definition",
    )
    return remove_none({key: index.get(key) for key in keys})


def canonical_index_rows(relation):
    return sorted(
        (planner_index_shape(index) for index in relation["indexes"]),
        key=lambda item: (item["indexName"], int(item["indexOid"])),
    )


def stable_identity_document(observation):
    keys = (
        "instrument",
        "rootSchema",
        "rootRelation",
        "snapshotParentOid",
        "rootOid",
        "rootPartitionKey",
        "rootPartitionBound",
        "childSchema",
        "childRelation",
        "snapshotId",
        "childOid",
        "childRelfilenode",
        "partitionBound",
    )
    return {key: observation[key] for key in keys}


def stable_config_document(observation):
    value = stable_identity_document(observation)
    value.update(
        {
            "rootTablespaceName": observation["rootTablespaceName"],
            "rootRelationOptions": sorted(observation["rootRelationOptions"]),
            "rootIndexes": sorted(
                observation["rootIndexes"],
                key=lambda item: (item["indexName"], int(item["indexOid"])),
            ),
            "tablespaceName": observation["tablespaceName"],
            "relationKind": observation["relationKind"],
            "persistenceKind": observation["persistenceKind"],
            "accessMethod": observation["accessMethod"],
            "relationOptions": sorted(observation["relationOptions"]),
            "indexes": sorted(
                observation["indexes"],
                key=lambda item: (item["indexName"], int(item["indexOid"])),
            ),
        }
    )
    return value


def stable_hash(value):
    return sha256_bytes(dotnet_canonical_json_bytes(value))


def build_observation_snapshot(instrument, snapshot_id, catalogs):
    definition = INSTRUMENT_BY_NAME[instrument]
    by_name = {item["name"]: item for item in catalogs}
    top = by_name[ROOT_RELATION]
    root = by_name[definition["rootRelation"]]
    child = next(
        item
        for item in catalogs
        if item.get("parentRelation") == definition["rootRelation"]
        and item["partitionBound"] == f"FOR VALUES IN ('{int(snapshot_id)}')"
    )
    observation = {
        "instrument": instrument,
        "rootSchema": root["schema"],
        "rootRelation": root["name"],
        "snapshotParentOid": int(top["oid"]),
        "rootOid": int(root["oid"]),
        "rootPartitionKey": root["partitionKey"],
        "rootPartitionBound": root["partitionBound"],
        "rootTablespaceName": root["tablespace"],
        "rootRelationOptions": sorted(root["relationOptions"]),
        "rootIndexes": canonical_index_rows(root),
        "childSchema": child["schema"],
        "childRelation": child["name"],
        "snapshotId": int(snapshot_id),
        "childOid": int(child["oid"]),
        "childRelfilenode": int(child["relfilenode"]),
        "partitionBound": child["partitionBound"],
        "tablespaceName": child["tablespace"],
        "relationKind": child["relationKind"],
        "persistenceKind": child["persistenceKind"],
        "accessMethod": child["accessMethod"],
        "relationOptions": sorted(child["relationOptions"]),
        "indexes": canonical_index_rows(child),
    }
    observation["stableChildIdentityHash"] = stable_hash(
        stable_identity_document(observation)
    )
    observation["stableConfigSchemaHash"] = stable_hash(
        stable_config_document(observation)
    )
    return observation


def observation_from_preflight(target):
    return {
        "instrument": target["instrument"],
        "rootSchema": target["root_schema"],
        "rootRelation": target["root_relation"],
        "snapshotParentOid": int(target["snapshot_parent_oid"]),
        "rootOid": int(target["root_oid"]),
        "rootPartitionKey": target["root_partition_key"],
        "rootPartitionBound": target["root_partition_bound"],
        "rootTablespaceName": target["root_tablespace_name"],
        "rootRelationOptions": sorted(target["root_relation_options"]),
        "rootIndexes": sorted(
            (remove_none(item) for item in target["root_index_configuration"]),
            key=lambda item: (item["indexName"], int(item["indexOid"])),
        ),
        "childSchema": target["child_schema"],
        "childRelation": target["child_relation"],
        "snapshotId": int(target["snapshot_id"]),
        "childOid": int(target["child_oid"]),
        "childRelfilenode": int(target["child_relfilenode"]),
        "partitionBound": target["partition_bound"],
        "tablespaceName": target["tablespace_name"],
        "relationKind": target["relation_kind"],
        "persistenceKind": target["persistence_kind"],
        "accessMethod": target["access_method"],
        "relationOptions": sorted(target["relation_options"]),
        "indexes": sorted(
            (remove_none(item) for item in target["index_configuration"]),
            key=lambda item: (item["indexName"], int(item["indexOid"])),
        ),
        "stableChildIdentityHash": target["stable_child_identity_hash"],
        "stableConfigSchemaHash": target["stable_config_schema_hash"],
    }


def observation_physical_key(observation):
    return "|".join(
        str(observation[key])
        for key in (
            "instrument",
            "root_schema",
            "root_relation",
            "snapshot_parent_oid",
            "root_oid",
            "root_partition_key",
            "root_partition_bound",
            "child_schema",
            "child_relation",
            "snapshot_id",
            "child_oid",
            "child_relfilenode",
            "partition_bound",
        )
    )


def evaluation_from_observation(observation):
    details = observation.get("details") or {}
    physical_key = observation_physical_key(observation)
    if details.get("childPhysicalKey") != physical_key:
        raise ArchiveError("observation physical key evidence differs")
    root_reasons = list(observation.get("root_reasons") or [])
    blockers = list(details.get("blockers") or [])
    if root_reasons != sorted(set(root_reasons)):
        raise ArchiveError("observation root reasons are not canonical")
    validate_persisted_order(
        blockers,
        blocker_comparison_key,
        "observation blockers",
    )
    if details.get("rootReasons") != root_reasons:
        raise ArchiveError("observation root-reason evidence differs")
    blocker_codes = sorted(
        {str(blocker.get("code")) for blocker in blockers}
    )
    if blocker_codes != sorted(observation.get("blocker_codes") or []):
        raise ArchiveError("observation blocker-code evidence differs")
    current = observation_from_preflight(observation)
    if current["stableChildIdentityHash"] != stable_hash(
        stable_identity_document(current)
    ):
        raise ArchiveError("observation stable child identity hash is invalid")
    if current["stableConfigSchemaHash"] != stable_hash(
        stable_config_document(current)
    ):
        raise ArchiveError("observation stable config/schema hash is invalid")
    metrics_hash = stable_hash(
        {
            "stableChildIdentityHash":
                observation["stable_child_identity_hash"],
            "rowEstimate": observation["row_estimate"],
            "totalBytes": observation["total_bytes"],
        }
    )
    if observation.get("observation_metrics_hash") != metrics_hash:
        raise ArchiveError("observation metrics hash is invalid")
    return {
        "physicalKey": physical_key,
        "stableChildIdentityHash":
            observation["stable_child_identity_hash"],
        "stableConfigSchemaHash":
            observation["stable_config_schema_hash"],
        "rowEstimate": observation["row_estimate"],
        "totalBytes": observation["total_bytes"],
        "observationMetricsHash":
            observation["observation_metrics_hash"],
        "plannerLive": observation["planner_live"],
        "oracleLive": observation["oracle_live"],
        "classification": observation["classification"],
        "rootReasons": root_reasons,
        "blockers": blockers,
        "observationId": observation["observation_id"],
        "instrument": observation["instrument"],
        "snapshotId": observation["snapshot_id"],
        "childRelation": observation["child_relation"],
    }


def comparison_document():
    return {
        "agrees": True,
        "publicationSourceValidationAgrees": True,
        "indexTopologyValidationAgrees": True,
        "plannerOnlyChildren": [],
        "oracleOnlyChildren": [],
        "plannerOnlyLive": [],
        "oracleOnlyLive": [],
        "plannerOnlyCandidates": [],
        "oracleOnlyCandidates": [],
    }


def blocker_comparison_key(item):
    return (
        str(item.get("code", "")),
        str(item.get("detail", "")),
    )


def nullable_ordinal(value):
    return (value is not None, value if value is not None else 0)


def anomaly_comparison_key(item):
    return (
        str(item.get("code", "")),
        nullable_ordinal(item.get("publicationId")),
        nullable_ordinal(item.get("scrapeId")),
        (
            item.get("publicationStatus") is not None,
            str(item.get("publicationStatus") or ""),
        ),
        str(item.get("detail", "")),
    )


def validate_persisted_order(items, comparison_key, label):
    keys = [comparison_key(item) for item in items]
    if keys != sorted(keys) or len(keys) != len(set(keys)):
        raise ArchiveError(f"{label} are not in exact persisted planner order")


def candidate_identity_hash(evaluations):
    return stable_hash(
        [
            {
                "physicalKey": item["physicalKey"],
                "stableChildIdentityHash":
                    item["stableChildIdentityHash"],
                "stableConfigSchemaHash":
                    item["stableConfigSchemaHash"],
            }
            for item in sorted(
                (
                    item
                    for item in evaluations
                    if item["classification"] == "candidate"
                ),
                key=lambda item: item["physicalKey"],
            )
        ]
    )


def cycle_observation_hash(cycle, evaluations):
    return stable_hash(
        {
            "plannerVersion": PLANNER_VERSION,
            "configVersion": CONFIG_VERSION,
            "triggerScrapeId": cycle["trigger_scrape_id"],
            "triggerPublicationId": cycle["trigger_publication_id"],
            "safePointKind": cycle["safe_point_kind"],
            "evaluations": [
                {
                    key: item[key]
                    for key in (
                        "physicalKey",
                        "stableChildIdentityHash",
                        "stableConfigSchemaHash",
                        "rowEstimate",
                        "totalBytes",
                        "observationMetricsHash",
                        "plannerLive",
                        "oracleLive",
                        "classification",
                        "rootReasons",
                        "blockers",
                    )
                }
                for item in sorted(
                    evaluations,
                    key=lambda item: item["physicalKey"],
                )
            ],
            "globalBlockers": cycle.get("global_blockers") or [],
            "anomalies": cycle.get("anomalies") or [],
            "comparison": comparison_document(),
        }
    )


def evidence_hash(cycle_id, evidence):
    return stable_hash(
        {
            "cycleId": cycle_id,
            "observationId": evidence.get("observation_id"),
            "sequence": evidence["sequence"],
            "phase": evidence["phase"],
            "kind": evidence["kind"],
            "payload": evidence["payload"],
            "previousHash": evidence.get("previous_hash"),
        }
    )


def comparison_key_text(value):
    return dotnet_canonical_json_bytes(value).decode("utf-8")


def validate_publication_validation_record(value):
    if not isinstance(value, dict):
        raise ArchiveError(
            "publication source validation must be a production record object"
        )
    keys = (
        "slot",
        "publicationId",
        "scrapeId",
        "expectedRowCount",
        "bindingRowCount",
        "actualRowCount",
        "bindingKeyHash",
        "actualKeyHash",
        "invalidRowCount",
        "duplicateKeyCount",
        "bindingIdentityValid",
        "isValid",
    )
    document = {key: value.get(key) for key in keys}
    expected = comparison_key_text(document)
    if value.get("comparisonKey") != expected:
        raise ArchiveError(
            "publication source validation comparisonKey is invalid"
        )
    return expected


def validate_numeric_index_validation_record(value):
    if not isinstance(value, dict):
        raise ArchiveError(
            "numeric child index validation must be a production record object"
        )
    keys = (
        "instrument",
        "snapshotId",
        "childRelation",
        "indexKeys",
        "expectedParentIndexCount",
        "missingParentIndexCount",
        "duplicateParentIndexCount",
        "detachedIndexCount",
        "invalidIndexCount",
        "unreadyIndexCount",
        "attributeMismatchIndexCount",
        "isValid",
    )
    document = {key: value.get(key) for key in keys}
    expected = comparison_key_text(document)
    if value.get("comparisonKey") != expected:
        raise ArchiveError(
            "numeric child index validation comparisonKey is invalid"
        )
    return expected


def validate_index_topology_validation_record(value):
    if not isinstance(value, dict):
        raise ArchiveError(
            "index topology validation must be a production record object"
        )
    numeric = value.get("effectiveNumericChildIndexValidations")
    if not isinstance(numeric, list):
        raise ArchiveError(
            "index topology effective numeric validation array is missing"
        )
    persisted_numeric = value.get("numericChildIndexValidations")
    if persisted_numeric is not None and persisted_numeric != numeric:
        raise ArchiveError(
            "index topology numeric validation projections differ"
        )
    numeric_keys = [
        validate_numeric_index_validation_record(item)
        for item in numeric
    ]
    numeric_keys = sorted(numeric_keys)
    keys = (
        "instrument",
        "topIndexKeys",
        "rootIndexKeys",
        "defaultIndexKeys",
        "missingRequiredTopIndexNames",
        "invalidTopIndexCount",
        "unreadyTopIndexCount",
        "attachedTopIndexCount",
        "missingRootIndexCount",
        "duplicateRootIndexCount",
        "detachedRootIndexCount",
        "invalidRootIndexCount",
        "unreadyRootIndexCount",
        "missingDefaultIndexCount",
        "duplicateDefaultIndexCount",
        "detachedDefaultIndexCount",
        "invalidDefaultIndexCount",
        "unreadyDefaultIndexCount",
    )
    document = {key: value.get(key) for key in keys}
    document["numericChildIndexValidations"] = numeric_keys
    document["isValid"] = value.get("isValid")
    expected = comparison_key_text(document)
    if value.get("comparisonKey") != expected:
        raise ArchiveError(
            "index topology validation comparisonKey is invalid"
        )
    return expected


def validation_comparison_keys(records, validator, label):
    if not isinstance(records, list):
        raise ArchiveError(f"{label} must be a production record array")
    return [validator(record) for record in records]


def authentic_cycle_material(cycle, observations, summary_validations=None):
    evaluations = [
        evaluation_from_observation(observation)
        for observation in observations
    ]
    children = sorted(item["physicalKey"] for item in evaluations)
    live = sorted(
        item["physicalKey"] for item in evaluations if item["plannerLive"]
    )
    candidates = sorted(
        item["physicalKey"] for item in evaluations
        if not item["plannerLive"]
    )
    validations = summary_validations or {
        "plannerPublicationSourceValidations": [],
        "oraclePublicationSourceValidations": [],
        "plannerIndexTopologyValidations": [],
        "oracleIndexTopologyValidations": [],
    }
    candidate_hash = candidate_identity_hash(evaluations)
    observation_hash = cycle_observation_hash(cycle, evaluations)
    summary = {
        "status": cycle["status"],
        "oracleAgreement": cycle["oracle_agreement"],
        "candidateIdentityHash": candidate_hash,
        "observationHash": observation_hash,
        "plannerChildKeys": children,
        "plannerLiveKeys": live,
        "plannerCandidateKeys": candidates,
        "oracleChildKeys": children,
        "oracleLiveKeys": live,
        "oracleCandidateKeys": candidates,
        **validations,
        "globalBlockers": cycle.get("global_blockers") or [],
        "anomalies": cycle.get("anomalies") or [],
    }
    if cycle.get("error_message") is not None:
        summary["errorMessage"] = cycle["error_message"]
    return {
        "evaluations": evaluations,
        "children": children,
        "live": live,
        "candidates": candidates,
        "candidateIdentityHash": candidate_hash,
        "observationHash": observation_hash,
        "summaryPayload": summary,
    }


def validate_authentic_planner_evidence(value):
    cycle = value["cycle"]
    observations = value.get("observations")
    evidence = value.get("evidence")
    if cycle.get("planner_version") != PLANNER_VERSION:
        raise ArchiveError("latest cycle planner version is not exact")
    if cycle.get("config_version") != CONFIG_VERSION:
        raise ArchiveError("latest cycle config version is not exact")
    validate_persisted_order(
        cycle.get("global_blockers") or [],
        blocker_comparison_key,
        "latest cycle global blockers",
    )
    validate_persisted_order(
        cycle.get("anomalies") or [],
        anomaly_comparison_key,
        "latest cycle anomalies",
    )
    if not isinstance(observations, list) or not observations:
        raise ArchiveError("latest cycle observations are missing")
    if not isinstance(evidence, list) or not evidence:
        raise ArchiveError("latest cycle evidence is missing")
    if any(
        observation.get("cycle_id") != cycle["cycle_id"]
        or observation.get("report_only") is not True
        for observation in observations
    ):
        raise ArchiveError("latest cycle observation ownership is invalid")
    material = authentic_cycle_material(cycle, observations)
    expected_sets = {
        "planner_child_set": material["children"],
        "planner_live_set": material["live"],
        "planner_candidate_set": material["candidates"],
        "oracle_child_set": material["children"],
        "oracle_live_set": material["live"],
        "oracle_candidate_set": material["candidates"],
    }
    for key, expected in expected_sets.items():
        if cycle.get(key) != expected:
            raise ArchiveError(f"latest cycle {key} is not authentic")
    if cycle.get("candidate_identity_hash") != material[
        "candidateIdentityHash"
    ]:
        raise ArchiveError("latest cycle candidate identity hash is invalid")
    if cycle.get("observation_hash") != material["observationHash"]:
        raise ArchiveError("latest cycle observation hash is invalid")
    candidate_evaluations = [
        item
        for item in material["evaluations"]
        if item["classification"] == "candidate"
    ]
    protected_evaluations = [
        item
        for item in material["evaluations"]
        if item["classification"] == "protected"
    ]
    if (
        any(
            item["plannerLive"] != item["oracleLive"]
            or item["blockers"]
            or (
                item["classification"] == "candidate"
                and item["plannerLive"]
            )
            or (
                item["classification"] == "protected"
                and not item["plannerLive"]
            )
            or item["classification"] not in ("candidate", "protected")
            for item in material["evaluations"]
        )
        or len(candidate_evaluations) != cycle["candidate_count"]
        or len(protected_evaluations) != cycle["protected_count"]
        or cycle["blocked_count"] != 0
        or sum(item["totalBytes"] for item in candidate_evaluations)
        != cycle["candidate_bytes"]
    ):
        raise ArchiveError("latest cycle observation classifications are invalid")
    evidence = sorted(evidence, key=lambda item: item["sequence"])
    if [item["sequence"] for item in evidence] != list(
        range(1, len(evidence) + 1)
    ):
        raise ArchiveError("latest cycle evidence sequence is not contiguous")
    if len(evidence) != len(observations) + 1:
        raise ArchiveError("latest cycle evidence cardinality is invalid")
    summary = evidence[0]
    if (
        summary.get("observation_id") is not None
        or summary.get("phase") != "observation"
        or summary.get("kind") != "summary"
        or summary.get("previous_hash") is not None
    ):
        raise ArchiveError("latest cycle summary evidence is invalid")
    payload = summary.get("payload") or {}
    for planner_key, oracle_key in (
        (
            "plannerPublicationSourceValidations",
            "oraclePublicationSourceValidations",
        ),
        (
            "plannerIndexTopologyValidations",
            "oracleIndexTopologyValidations",
        ),
    ):
        planner = payload.get(planner_key)
        oracle = payload.get(oracle_key)
        validator = (
            validate_publication_validation_record
            if "PublicationSource" in planner_key
            else validate_index_topology_validation_record
        )
        planner_keys = validation_comparison_keys(
            planner,
            validator,
            planner_key,
        )
        oracle_keys = validation_comparison_keys(
            oracle,
            validator,
            oracle_key,
        )
        if sorted(planner_keys) != sorted(oracle_keys):
            raise ArchiveError("latest cycle summary validation evidence differs")
    expected_summary = authentic_cycle_material(
        cycle,
        observations,
        {
            "plannerPublicationSourceValidations":
                payload["plannerPublicationSourceValidations"],
            "oraclePublicationSourceValidations":
                payload["oraclePublicationSourceValidations"],
            "plannerIndexTopologyValidations":
                payload["plannerIndexTopologyValidations"],
            "oracleIndexTopologyValidations":
                payload["oracleIndexTopologyValidations"],
        },
    )["summaryPayload"]
    if payload != expected_summary:
        raise ArchiveError("latest cycle summary payload is not authentic")
    instrument_order = {
        instrument: index
        for index, (_, instrument, _) in enumerate(INSTRUMENTS)
    }
    expected_children = sorted(
        material["evaluations"],
        key=lambda item: (
            instrument_order[item["instrument"]],
            item["snapshotId"],
            item["childRelation"],
        ),
    )
    for row, expected in zip(evidence[1:], expected_children, strict=True):
        expected_payload = {
            key: expected[key]
            for key in (
                "physicalKey",
                "stableChildIdentityHash",
                "stableConfigSchemaHash",
                "observationMetricsHash",
                "plannerLive",
                "oracleLive",
                "classification",
                "rootReasons",
                "blockers",
            )
        }
        if (
            row.get("observation_id") != expected["observationId"]
            or row.get("phase") != "observation"
            or row.get("kind") != "child"
            or row.get("payload") != expected_payload
        ):
            raise ArchiveError("latest cycle child evidence is not authentic")
    previous_hash = None
    for row in evidence:
        if row.get("previous_hash") != previous_hash:
            raise ArchiveError("latest cycle evidence linkage is invalid")
        current_hash = evidence_hash(cycle["cycle_id"], row)
        if row.get("current_hash") != current_hash:
            raise ArchiveError("latest cycle evidence hash is invalid")
        previous_hash = current_hash
    return material


def validate_catalog_against_observation(catalogs, target):
    expected = observation_from_preflight(target)
    observed = build_observation_snapshot(
        expected["instrument"],
        expected["snapshotId"],
        catalogs,
    )
    if observed != expected:
        raise ArchiveError("current target identity/config differs from observation")
    by_name = {item["name"]: item for item in catalogs}
    top = by_name[ROOT_RELATION]
    root = by_name[expected["rootRelation"]]
    child = by_name[expected["childRelation"]]
    if (
        top["relationKind"] != "p"
        or top["partitionKey"] != "LIST (instrument)"
        or top.get("parentOid") is not None
        or root["relationKind"] != "p"
        or root["parentOid"] != top["oid"]
        or child["relationKind"] != "r"
        or child["parentOid"] != root["oid"]
    ):
        raise ArchiveError("root/parent/child attachment hierarchy is invalid")
    top_names = {index["indexName"] for index in top["indexes"]}
    if top_names != {
        "leaderboard_entries_snapshot_pkey",
        "ix_les_snapshot_song_score",
    }:
        raise ArchiveError("top snapshot indexes are not the exact required set")
    if any(item["tablespace"] != "pg_default" for item in catalogs):
        raise ArchiveError("archive proof currently requires pg_default tablespaces")
    validate_primary_key(child)
    return observed


def validate_primary_key(child):
    primary = [index for index in child["indexes"] if index["isPrimary"]]
    if len(primary) != 1:
        raise ArchiveError("target child must have exactly one primary index")
    if tuple(primary[0].get("columnNames", ())) != PK_COLUMNS:
        raise ArchiveError("target child primary key shape is not exact")
    if not primary[0]["isUnique"] or primary[0]["accessMethod"] != "btree":
        raise ArchiveError("target child primary index attributes are invalid")


def logical_catalog(catalogs):
    def logical_index(index):
        return {
            key: index[key]
            for key in (
                "indexName",
                "relationKind",
                "isValid",
                "isReady",
                "isPrimary",
                "isUnique",
                "accessMethod",
                "tablespaceName",
                "parentIndexName",
                "definition",
                "columnNames",
            )
            if key in index
        }

    result = []
    for relation in catalogs:
        result.append(
            {
                "schema": relation["schema"],
                "name": relation["name"],
                "relationKind": relation["relationKind"],
                "persistenceKind": relation["persistenceKind"],
                "owner": relation["owner"],
                "tablespace": relation["tablespace"],
                "accessMethod": relation["accessMethod"],
                "relationOptions": relation["relationOptions"],
                "partitionKey": relation["partitionKey"],
                "partitionBound": relation["partitionBound"],
                "parentSchema": relation.get("parentSchema"),
                "parentRelation": relation.get("parentRelation"),
                "columns": relation["columns"],
                "constraints": relation["constraints"],
                "indexes": sorted(
                    (logical_index(index) for index in relation["indexes"]),
                    key=lambda item: item["indexName"],
                ),
            }
        )
    return result


def fingerprint_sql(schema, relation):
    qualified = f"{quote_identifier(schema)}.{quote_identifier(relation)}"
    order = ", ".join(quote_identifier(column) for column in PK_COLUMNS)
    return (
        "COPY (SELECT to_jsonb(row_value)::text "
        f"FROM {qualified} AS row_value ORDER BY {order}) TO STDOUT"
    )


def stream_fingerprint(
    container,
    user,
    database,
    schema,
    relation,
    *,
    timeout_seconds,
):
    sql = fingerprint_sql(schema, relation)
    server_timeout = max(1, timeout_seconds - 30)
    arguments = psql_command(
        container,
        user,
        database,
        sql,
        statement_timeout_seconds=server_timeout,
    )
    process = subprocess.Popen(
        [str(value) for value in arguments],
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
    )
    digest = hashlib.sha256()
    byte_count = 0
    stderr = bytearray()
    selector = selectors.DefaultSelector()
    assert process.stdout is not None and process.stderr is not None
    selector.register(process.stdout, selectors.EVENT_READ, "stdout")
    selector.register(process.stderr, selectors.EVENT_READ, "stderr")
    deadline = time.monotonic() + timeout_seconds
    try:
        while selector.get_map():
            remaining = deadline - time.monotonic()
            if remaining <= 0:
                process.terminate()
                with contextlib.suppress(subprocess.TimeoutExpired):
                    process.wait(timeout=10)
                if process.poll() is None:
                    process.kill()
                    process.wait()
                raise ArchiveError("row fingerprint stream exceeded its timeout")
            events = selector.select(min(1, remaining))
            for key, _ in events:
                chunk = os.read(key.fileobj.fileno(), 1024 * 1024)
                if not chunk:
                    selector.unregister(key.fileobj)
                    continue
                if key.data == "stdout":
                    digest.update(chunk)
                    byte_count += len(chunk)
                elif len(stderr) < 1024 * 1024:
                    stderr.extend(chunk[: 1024 * 1024 - len(stderr)])
    finally:
        selector.close()
    returncode = process.wait(timeout=10)
    if returncode != 0:
        raise CommandError(arguments, returncode, b"", bytes(stderr))
    qualified = f"{quote_identifier(schema)}.{quote_identifier(relation)}"
    row_count = int(
        psql_text(
            container,
            user,
            database,
            f"SELECT COUNT(*)::BIGINT FROM ONLY {qualified}",
            timeout=timeout_seconds,
            statement_timeout_seconds=server_timeout,
        )
    )
    return {
        "algorithm": (
            "sha256-copy-to-jsonb-text-ordered-"
            "snapshot_id-song_id-instrument-account_id-v1"
        ),
        "sha256": digest.hexdigest(),
        "rowCount": row_count,
        "streamBytes": byte_count,
    }


def database_identity(container, user, database):
    sql = """
        SELECT json_build_object(
            'database', current_database(),
            'currentUser', current_user,
            'serverVersion', current_setting('server_version'),
            'serverVersionNum', current_setting('server_version_num')::INTEGER,
            'systemIdentifier', (
                SELECT system_identifier::TEXT FROM pg_control_system()),
            'dataDirectory', current_setting('data_directory'),
            'databaseOid', (
                SELECT oid FROM pg_database WHERE datname = current_database()),
            'databaseTablespace', (
                SELECT tablespace.spcname
                FROM pg_database database
                JOIN pg_tablespace tablespace
                  ON tablespace.oid = database.dattablespace
                WHERE database.datname = current_database()))
    """
    result = psql_json(container, user, database, sql)
    if int(result["serverVersionNum"]) // 10000 != POSTGRES_MAJOR:
        raise ArchiveError("source PostgreSQL major version must be 17")
    if result["database"] != database:
        raise ArchiveError("source database identity differs from requested database")
    return result


def container_identity(container, storage_root):
    inspected = json.loads(
        run(["docker", "inspect", container]).stdout.decode("utf-8")
    )
    if len(inspected) != 1:
        raise ArchiveError("source container inspection is ambiguous")
    value = inspected[0]
    if not value.get("State", {}).get("Running"):
        raise ArchiveError("source PostgreSQL container is not running")
    data_mounts = [
        mount
        for mount in value.get("Mounts", [])
        if mount.get("Destination") == "/var/lib/postgresql/data"
    ]
    if len(data_mounts) != 1:
        raise ArchiveError("source PostgreSQL PGDATA mount is not exact")
    mount = data_mounts[0]
    if mount.get("Type") != "bind" or mount.get("RW") is not True:
        raise ArchiveError("source PostgreSQL PGDATA must be an exact read-write bind")
    source = validate_storage_path(
        mount["Source"],
        storage_root,
        must_exist=True,
    )
    image_id = value.get("Image")
    image = json.loads(
        run(["docker", "image", "inspect", image_id]).stdout.decode("utf-8")
    )[0]
    environment = {}
    for entry in value.get("Config", {}).get("Env") or []:
        key, separator, setting = entry.partition("=")
        if separator:
            environment[key] = setting
    mounts = [
        {
            "type": item.get("Type"),
            "name": item.get("Name"),
            "source": item.get("Source"),
            "destination": item.get("Destination"),
            "readWrite": item.get("RW"),
        }
        for item in value.get("Mounts", [])
    ]
    return {
        "containerId": value["Id"],
        "containerName": value["Name"].lstrip("/"),
        "configuredImage": value.get("Config", {}).get("Image"),
        "imageId": image_id,
        "imageRepoDigests": sorted(image.get("RepoDigests") or []),
        "networkMode": value.get("HostConfig", {}).get("NetworkMode"),
        "pgdataEnvironment": environment.get(
            "PGDATA",
            "/var/lib/postgresql/data",
        ),
        "pgdataMount": {
            "type": mount.get("Type"),
            "source": str(source),
            "destination": mount.get("Destination"),
            "readWrite": mount.get("RW"),
        },
        "mounts": mounts,
    }


def tablespace_inventory(container, user, database, identity, storage_root):
    rows = psql_json(
        container,
        user,
        database,
        """
            SELECT COALESCE(
                json_agg(
                    json_build_object(
                        'oid', tablespace.oid,
                        'name', tablespace.spcname,
                        'containerPath',
                            pg_tablespace_location(tablespace.oid))
                    ORDER BY tablespace.oid),
                '[]'::json)
            FROM pg_tablespace tablespace
        """,
    )
    mounts = identity["mounts"]
    result = []
    for row in rows:
        container_path = row["containerPath"] or identity["pgdataEnvironment"]
        candidates = []
        for mount in mounts:
            destination = mount.get("destination")
            source = mount.get("source")
            if not destination or not source:
                continue
            try:
                relative = PurePosixPath(container_path).relative_to(
                    PurePosixPath(destination)
                )
            except ValueError:
                continue
            candidates.append(
                (
                    len(PurePosixPath(destination).parts),
                    mount,
                    relative,
                )
            )
        if not candidates:
            raise ArchiveError(
                f"tablespace {row['name']} is outside explicit container mounts"
            )
        _, mount, relative = max(candidates, key=lambda item: item[0])
        host_path = pathlib.Path(mount["source"], *relative.parts)
        host_path = validate_storage_path(
            host_path,
            storage_root,
            must_exist=True,
        )
        host_mount = mount_evidence(host_path)
        result.append(
            {
                **row,
                "containerPath": container_path,
                "hostPath": str(host_path),
                "mountType": mount["type"],
                "mountDestination": mount["destination"],
                "mountReadWrite": mount["readWrite"],
                "hostMount": host_mount,
                "hostLocation": mount_location(host_path, host_mount),
            }
        )
    return result


def protected_source_paths(identity, tablespaces):
    paths = {
        pathlib.Path(identity["pgdataMount"]["source"]).resolve(),
        *(pathlib.Path(row["hostPath"]).resolve() for row in tablespaces),
    }
    for mount in identity["mounts"]:
        if mount.get("source"):
            paths.add(pathlib.Path(mount["source"]).resolve())
    docker_root = pathlib.Path(
        run(
            ["docker", "info", "--format", "{{.DockerRootDir}}"]
        ).stdout.decode().strip()
    ).resolve()
    paths.add(docker_root)
    return [
        {
            "path": str(path),
            "mount": mount_evidence(path),
            "location": mount_location(path),
        }
        for path in sorted(paths, key=str)
    ]


def mount_identity(evidence):
    return {
        key: evidence[key]
        for key in (
            "sourceDevice",
            "filesystemType",
            "fsRoot",
            "deviceId",
            "mountTarget",
        )
    }


def reject_output_overlap(output, protected_paths, output_mount=None):
    output = pathlib.Path(output).resolve(strict=False)
    output_parent_mount = output_mount or mount_evidence(output.parent)
    output_location = mount_location(output, output_parent_mount)
    conflicts = []
    for protected in protected_paths:
        if isinstance(protected, dict):
            protected_path = pathlib.Path(protected["path"])
            protected_location = protected["location"]
        else:
            protected_path = pathlib.Path(protected)
            protected_location = mount_location(protected_path)
        if paths_overlap(output, protected_path) or mount_locations_overlap(
            output_location,
            protected_location,
        ):
            conflicts.append(str(protected_path))
    if conflicts:
        raise ArchiveError(
            "archive output overlaps protected source storage: "
            + ", ".join(conflicts)
        )


def source_fence(preflight, catalogs, fingerprint):
    child = catalogs[-1]
    return {
        "capturedAtUtc": utc_now(),
        "preflightSha256": stable_hash(preflight),
        "catalogPhysicalSha256": stable_hash(catalogs),
        "logicalCatalogSha256": stable_hash(logical_catalog(catalogs)),
        "targetOid": child["oid"],
        "targetRelfilenode": child["relfilenode"],
        "heapBytes": child["heapBytes"],
        "indexBytes": child["indexBytes"],
        "totalBytes": child["totalBytes"],
        "mutationCounters": child["mutationCounters"],
        "rowFingerprint": fingerprint,
    }


def compare_source_fences(before, after):
    keys = (
        "preflightSha256",
        "catalogPhysicalSha256",
        "logicalCatalogSha256",
        "targetOid",
        "targetRelfilenode",
        "heapBytes",
        "indexBytes",
        "totalBytes",
        "mutationCounters",
        "rowFingerprint",
    )
    changed = [key for key in keys if before.get(key) != after.get(key)]
    if changed:
        raise ArchiveError(
            "source changed while archive streamed: " + ", ".join(changed)
        )


def archive_command(args, target, source_container):
    relations = (
        ROOT_RELATION,
        target["root_relation"],
        target["child_relation"],
    )
    command = [
        "docker",
        "exec",
        "-e",
        "PGCONNECT_TIMEOUT=10",
        "-e",
        (
            "PGOPTIONS=-c lock_timeout=5s -c statement_timeout=0 "
            "-c idle_in_transaction_session_timeout=0 "
            "-c application_name=fst-snapshot-generation-archive-only"
        ),
        source_container,
        "pg_dump",
        "--format=custom",
        "--compress=6",
        "--no-owner",
        "--no-privileges",
        "--strict-names",
        "--lock-wait-timeout=5s",
        "-U",
        args.pg_user,
        "-d",
        args.pg_database,
    ]
    for relation in relations:
        command.extend(
            ["--table", f"{target['child_schema']}.{relation}"]
        )
    return command


def stream_archive(arguments, path, timeout):
    path = pathlib.Path(path)
    partial = path.with_name(f".{path.name}.partial-{os.getpid()}")
    with partial.open("xb") as output:
        process = subprocess.Popen(
            [str(value) for value in arguments],
            stdout=output,
            stderr=subprocess.PIPE,
        )
        try:
            _, stderr = process.communicate(timeout=timeout)
        except subprocess.TimeoutExpired as error:
            process.terminate()
            try:
                _, stderr = process.communicate(timeout=10)
            except subprocess.TimeoutExpired:
                process.kill()
                _, stderr = process.communicate()
            with contextlib.suppress(FileNotFoundError):
                partial.unlink()
            raise ArchiveError("custom archive stream exceeded its timeout") from error
        returncode = process.returncode
        output.flush()
        os.fsync(output.fileno())
    if returncode != 0:
        with contextlib.suppress(FileNotFoundError):
            partial.unlink()
        raise CommandError(arguments, returncode, b"", stderr)
    if partial.stat().st_size <= 0:
        partial.unlink()
        raise ArchiveError("custom archive is empty")
    os.replace(partial, path)


def archive_toc(container, archive_path):
    arguments = [
        "docker",
        "exec",
        "-i",
        container,
        "pg_restore",
        "-l",
    ]
    with pathlib.Path(archive_path).open("rb") as source:
        completed = subprocess.run(
            arguments,
            stdin=source,
            capture_output=True,
            timeout=600,
        )
    if completed.returncode != 0:
        raise CommandError(
            arguments,
            completed.returncode,
            completed.stdout,
            completed.stderr,
        )
    return completed.stdout


def validate_toc(toc_bytes, target):
    toc = toc_bytes.decode("utf-8")
    required = (
        f"TABLE public {ROOT_RELATION}",
        f"TABLE public {target['root_relation']}",
        f"TABLE public {target['child_relation']}",
        f"TABLE DATA public {target['child_relation']}",
    )
    missing = [token for token in required if token not in toc]
    if missing:
        raise ArchiveError("archive TOC is incomplete: " + ", ".join(missing))
    data_lines = [
        line
        for line in toc.splitlines()
        if " TABLE DATA " in f" {line} "
    ]
    if len(data_lines) != 1 or target["child_relation"] not in data_lines[0]:
        raise ArchiveError("archive TOC contains data outside the selected child")


def repository_identity(script):
    root = pathlib.Path(script).resolve().parent.parent
    commit = run(["git", "-C", root, "rev-parse", "HEAD"]).stdout.decode().strip()
    return {
        "gitCommit": commit,
        "toolPath": str(pathlib.Path(script).resolve().relative_to(root)),
        "toolSha256": sha256_path(script),
    }


def cycle_manifest(cycle, observations, evidence):
    sets = {}
    for key in (
        "planner_child_set",
        "planner_live_set",
        "planner_candidate_set",
        "oracle_child_set",
        "oracle_live_set",
        "oracle_candidate_set",
    ):
        sets[key] = {
            "count": len(cycle[key]),
            "sha256": stable_hash(cycle[key]),
        }
    return {
        "cycleId": cycle["cycle_id"],
        "triggerScrapeId": cycle["trigger_scrape_id"],
        "triggerPublicationId": cycle["trigger_publication_id"],
        "safePointKind": cycle["safe_point_kind"],
        "safePointAt": cycle["safe_point_at"],
        "plannerVersion": cycle["planner_version"],
        "configVersion": cycle["config_version"],
        "status": cycle["status"],
        "reportOnly": cycle["report_only"],
        "oracleAgreement": cycle["oracle_agreement"],
        "candidateIdentityHash": cycle["candidate_identity_hash"],
        "observationHash": cycle["observation_hash"],
        "candidateCount": cycle["candidate_count"],
        "protectedCount": cycle["protected_count"],
        "blockedCount": cycle["blocked_count"],
        "candidateBytes": cycle["candidate_bytes"],
        "sets": sets,
        "globalBlockersSha256": stable_hash(cycle["global_blockers"]),
        "anomaliesSha256": stable_hash(cycle["anomalies"]),
        "observationCount": len(observations),
        "evidenceCount": len(evidence),
        "evidenceFinalHash": evidence[-1]["current_hash"],
        "evidenceSha256": stable_hash(evidence),
    }


def write_checksums(directory, names):
    lines = [
        f"{sha256_path(pathlib.Path(directory) / name)}  {name}\n"
        for name in sorted(names)
    ]
    write_bytes(pathlib.Path(directory) / "SHA256SUMS", "".join(lines).encode())


def discard_partial_package(package):
    for name in (*PACKAGE_FILES, "SHA256SUMS"):
        path = pathlib.Path(package) / name
        with contextlib.suppress(FileNotFoundError):
            path.unlink()
    for path in pathlib.Path(package).glob(".*.partial-*"):
        with contextlib.suppress(OSError):
            path.unlink()


def discover_source_storage(args, package):
    container = container_identity(args.source_container, args.storage_root)
    database = database_identity(
        args.source_container,
        args.pg_user,
        args.pg_database,
    )
    if database["dataDirectory"] != container["pgdataEnvironment"]:
        raise ArchiveError(
            "PostgreSQL data_directory, PGDATA, and container mount differ"
        )
    data_relative = posix_relative_to(
        database["dataDirectory"],
        container["pgdataMount"]["destination"],
    )
    host_data_directory = pathlib.Path(
        container["pgdataMount"]["source"],
        *data_relative.parts,
    )
    host_data_directory = validate_storage_path(
        host_data_directory,
        args.storage_root,
        must_exist=True,
    )
    storage_mount = mount_evidence(args.storage_root)
    host_data_mount = mount_evidence(host_data_directory)
    require_same_mount_device(
        storage_mount,
        host_data_mount,
        "source data_directory",
    )
    container["resolvedDataDirectory"] = {
        "hostPath": str(host_data_directory),
        "mount": host_data_mount,
        "location": mount_location(host_data_directory, host_data_mount),
    }
    tablespaces = tablespace_inventory(
        args.source_container,
        args.pg_user,
        args.pg_database,
        container,
        args.storage_root,
    )
    for tablespace in tablespaces:
        require_same_mount_device(
            storage_mount,
            tablespace["hostMount"],
            f"tablespace {tablespace['name']}",
        )
    protected_paths = protected_source_paths(container, tablespaces)
    reject_nested_mounts(ARCHIVE_ROOT, "archive root")
    reject_output_overlap(package, protected_paths)
    reject_output_overlap(ARCHIVE_ROOT, protected_paths)
    return container, database, tablespaces, protected_paths


def source_provenance_document(container, database, tablespaces):
    mount_keys = (
        "type",
        "name",
        "source",
        "destination",
        "readWrite",
    )
    return {
        "containerId": container["containerId"],
        "imageId": container["imageId"],
        "configuredImage": container["configuredImage"],
        "pgdataEnvironment": container["pgdataEnvironment"],
        "pgdataMount": container["pgdataMount"],
        "resolvedDataDirectory": {
            "hostPath": container["resolvedDataDirectory"]["hostPath"],
            "location": container["resolvedDataDirectory"]["location"],
        },
        "mounts": sorted(
            (
                {key: mount.get(key) for key in mount_keys}
                for mount in container["mounts"]
            ),
            key=lambda mount: (
                str(mount.get("destination")),
                str(mount.get("source")),
            ),
        ),
        "database": {
            key: database[key]
            for key in (
                "database",
                "currentUser",
                "serverVersion",
                "serverVersionNum",
                "systemIdentifier",
                "dataDirectory",
                "databaseOid",
                "databaseTablespace",
            )
        },
        "tablespaces": [
            {
                key: row[key]
                for key in (
                    "oid",
                    "name",
                    "containerPath",
                    "hostPath",
                    "mountType",
                    "mountDestination",
                    "mountReadWrite",
                    "hostLocation",
                )
            }
            for row in tablespaces
        ],
    }


def recapture_source_provenance(args, discovered):
    container_id = discovered["containerId"]
    container = container_identity(container_id, args.storage_root)
    database = database_identity(
        container_id,
        args.pg_user,
        args.pg_database,
    )
    if database["dataDirectory"] != container["pgdataEnvironment"]:
        raise ArchiveError("recaptured source PGDATA identity differs")
    data_relative = posix_relative_to(
        database["dataDirectory"],
        container["pgdataMount"]["destination"],
    )
    host_data_directory = pathlib.Path(
        container["pgdataMount"]["source"],
        *data_relative.parts,
    )
    host_data_directory = validate_storage_path(
        host_data_directory,
        args.storage_root,
        must_exist=True,
    )
    container["resolvedDataDirectory"] = {
        "hostPath": str(host_data_directory),
        "mount": mount_evidence(host_data_directory),
        "location": mount_location(host_data_directory),
    }
    tablespaces = tablespace_inventory(
        container_id,
        args.pg_user,
        args.pg_database,
        container,
        args.storage_root,
    )
    return source_provenance_document(container, database, tablespaces)


def require_source_provenance(args, expected):
    observed = recapture_source_provenance(args, expected)
    if observed != expected:
        raise ArchiveError(
            "source container/database/PGDATA/tablespace provenance changed"
        )
    return observed


def _archive_locked(args, reservation, expected_archive_mount):
    require_commands("docker", "findmnt", "git")
    if (args.instrument is None) != (args.snapshot_id is None):
        raise ArchiveError(
            "--instrument and --snapshot-id must be supplied together"
        )
    package = validate_storage_path(
        args.output,
        ARCHIVE_ROOT,
        must_exist=False,
        new_directory=True,
    )
    docker_bind_mount(package, "/package", readonly=True)
    started = utc_now()
    container, database, tablespaces, protected_paths = (
        discover_source_storage(args, package)
    )
    discovered_provenance = source_provenance_document(
        container,
        database,
        tablespaces,
    )
    source_container = container["containerId"]
    write_mount = mount_evidence(ARCHIVE_ROOT)
    if mount_identity(write_mount) != mount_identity(expected_archive_mount):
        raise ArchiveError("archive root mount changed before package creation")
    reject_nested_mounts(ARCHIVE_ROOT, "archive root")
    reject_output_overlap(
        package,
        protected_paths,
        output_mount=write_mount,
    )
    package.mkdir(mode=0o700)
    reject_nested_mounts(package, "archive package")
    try:
        storage_mount = mount_evidence(args.storage_root)
        output_mount = mount_evidence(package)
        pgdata_mount = mount_evidence(container["pgdataMount"]["source"])
        require_same_mount_device(storage_mount, output_mount, "archive output")
        require_same_mount_device(storage_mount, pgdata_mount, "source PGDATA")
        selection_sql = candidate_selection_sql(
            args.instrument,
            args.snapshot_id,
        )
        before_preflight = validate_preflight(
            psql_json(
                source_container,
                args.pg_user,
                args.pg_database,
                selection_sql,
            ),
            args.instrument,
            args.snapshot_id,
        )
        target = before_preflight["target"]
        relations = (
            ROOT_RELATION,
            target["root_relation"],
            target["child_relation"],
        )
        before_catalog = capture_catalog(
            source_container,
            args.pg_user,
            args.pg_database,
            target["child_schema"],
            relations,
        )
        validate_catalog_against_observation(before_catalog, target)
        before_fingerprint = stream_fingerprint(
            source_container,
            args.pg_user,
            args.pg_database,
            target["child_schema"],
            target["child_relation"],
            timeout_seconds=args.timeout_seconds,
        )
        before_fence = source_fence(
            before_preflight,
            before_catalog,
            before_fingerprint,
        )
        required_free = required_capacity_bytes(
            before_catalog[-1]["totalBytes"]
        )
        if output_mount["freeBytes"] < required_free:
            raise ArchiveError("insufficient FST capacity for archive and proof")
        admission_preflight = validate_preflight(
            psql_json(
                source_container,
                args.pg_user,
                args.pg_database,
                selection_sql,
            ),
            args.instrument,
            args.snapshot_id,
        )
        admission_catalog = capture_catalog(
            source_container,
            args.pg_user,
            args.pg_database,
            target["child_schema"],
            relations,
        )
        validate_catalog_against_observation(admission_catalog, target)
        if (
            admission_preflight != before_preflight
            or admission_catalog != before_catalog
        ):
            raise ArchiveError("source changed before pg_dump admission")
        admission_mount = mount_evidence(package)
        required_free = required_capacity_bytes(
            admission_catalog[-1]["totalBytes"]
        )
        if admission_mount["freeBytes"] < required_free:
            raise ArchiveError(
                "FST capacity changed before pg_dump admission"
            )
        require_same_mount_device(
            storage_mount,
            admission_mount,
            "archive admission output",
        )
        admission_provenance = require_source_provenance(
            args,
            discovered_provenance,
        )
        archive_path = package / "archive.custom"
        stream_archive(
            archive_command(args, target, source_container),
            archive_path,
            args.timeout_seconds,
        )
        streamed_provenance = require_source_provenance(
            args,
            discovered_provenance,
        )
        toc = archive_toc(source_container, archive_path)
        validate_toc(toc, target)
        write_bytes(package / "archive.toc", toc)
        after_preflight = validate_preflight(
            psql_json(
                source_container,
                args.pg_user,
                args.pg_database,
                selection_sql,
            ),
            args.instrument,
            args.snapshot_id,
        )
        after_catalog = capture_catalog(
            source_container,
            args.pg_user,
            args.pg_database,
            target["child_schema"],
            relations,
        )
        validate_catalog_against_observation(after_catalog, target)
        after_fingerprint = stream_fingerprint(
            source_container,
            args.pg_user,
            args.pg_database,
            target["child_schema"],
            target["child_relation"],
            timeout_seconds=args.timeout_seconds,
        )
        after_fence = source_fence(
            after_preflight,
            after_catalog,
            after_fingerprint,
        )
        compare_source_fences(before_fence, after_fence)
        catalog_evidence = {
            "schemaVersion": SCHEMA_VERSION,
            "capturedAtUtc": before_fence["capturedAtUtc"],
            "physicalCatalog": before_catalog,
            "logicalCatalog": logical_catalog(before_catalog),
        }
        write_json(package / "catalog.json", catalog_evidence)
        source_scrape = before_preflight["triggerScrape"]
        source_publication = before_preflight["triggerPublication"]
        manifest = {
            "schemaVersion": SCHEMA_VERSION,
            "toolId": TOOL_ID,
            "status": "accepted",
            "archiveOnly": True,
            "startedAtUtc": started,
            "completedAtUtc": utc_now(),
            "repository": repository_identity(__file__),
            "selection": {
                "mode": "exact" if args.instrument else "latest-smallest",
                "requestedInstrument": args.instrument,
                "requestedSnapshotId": args.snapshot_id,
            },
            "cycle": cycle_manifest(
                before_preflight["cycle"],
                before_preflight["observations"],
                before_preflight["evidence"],
            ),
            "scrape": {
                "id": source_scrape["id"],
                "status": source_scrape["status"],
                "sha256": stable_hash(source_scrape),
            },
            "publication": {
                "id": source_publication["publication_id"],
                "scrapeId": source_publication["scrape_id"],
                "status": source_publication["status"],
                "sha256": stable_hash(source_publication),
            },
            "target": {
                "observationId": target["observation_id"],
                "cycleId": target["cycle_id"],
                "instrument": target["instrument"],
                "snapshotId": target["snapshot_id"],
                "rootSchema": target["root_schema"],
                "rootRelation": target["root_relation"],
                "snapshotParentOid": target["snapshot_parent_oid"],
                "rootOid": target["root_oid"],
                "rootPartitionKey": target["root_partition_key"],
                "rootPartitionBound": target["root_partition_bound"],
                "rootTablespace": target["root_tablespace_name"],
                "rootRelationOptions": target["root_relation_options"],
                "rootIndexConfigurationSha256": stable_hash(
                    target["root_index_configuration"]
                ),
                "childSchema": target["child_schema"],
                "childRelation": target["child_relation"],
                "childOid": target["child_oid"],
                "childRelfilenode": target["child_relfilenode"],
                "partitionBound": target["partition_bound"],
                "tablespace": target["tablespace_name"],
                "relationKind": target["relation_kind"],
                "persistenceKind": target["persistence_kind"],
                "accessMethod": target["access_method"],
                "relationOptions": target["relation_options"],
                "indexConfigurationSha256": stable_hash(
                    target["index_configuration"]
                ),
                "stableChildIdentityHash":
                    target["stable_child_identity_hash"],
                "stableConfigSchemaHash":
                    target["stable_config_schema_hash"],
                "observationMetricsHash":
                    target["observation_metrics_hash"],
            },
            "sourceIdentity": {
                "database": database,
                "container": container,
                "tablespaces": tablespaces,
                "protectedHostLocations": protected_paths,
                "discoveredProvenance": discovered_provenance,
                "dumpAdmissionProvenance": admission_provenance,
                "afterStreamProvenance": streamed_provenance,
            },
            "capacityAndMount": {
                "storage": storage_mount,
                "output": output_mount,
                "dumpAdmission": admission_mount,
                "pgdata": pgdata_mount,
                "requiredFreeBytes": required_free,
                "reservation": reservation,
            },
            "sourceFenceBefore": before_fence,
            "sourceFenceAfter": after_fence,
            "rowFingerprint": before_fingerprint,
            "catalog": {
                "path": "catalog.json",
                "bytes": (package / "catalog.json").stat().st_size,
                "sha256": sha256_path(package / "catalog.json"),
                "logicalSha256": stable_hash(
                    catalog_evidence["logicalCatalog"]
                ),
                "logicalCatalog": catalog_evidence["logicalCatalog"],
            },
            "archive": {
                "path": "archive.custom",
                "format": "PostgreSQL custom",
                "compression": 6,
                "noOwner": True,
                "noPrivileges": True,
                "strictNames": True,
                "lockWaitTimeout": "5s",
                "bytes": archive_path.stat().st_size,
                "sha256": sha256_path(archive_path),
            },
            "toc": {
                "path": "archive.toc",
                "bytes": (package / "archive.toc").stat().st_size,
                "sha256": sha256_path(package / "archive.toc"),
            },
            "proofPolicy": {
                "postgresMajor": POSTGRES_MAJOR,
                "networkMode": "none",
                "publishedPorts": 0,
                "restoreOwner": "fst_archive_proof",
                "packageReadOnly": True,
            },
        }
        write_json(package / "manifest.json", manifest)
        write_checksums(package, PACKAGE_FILES)
        return manifest
    except Exception as error:
        discard_partial_package(package)
        write_json(
            package / "rejected.json",
            {
                "schemaVersion": SCHEMA_VERSION,
                "toolId": TOOL_ID,
                "status": "rejected",
                "archiveOnly": True,
                "startedAtUtc": started,
                "rejectedAtUtc": utc_now(),
                "reason": str(error),
            },
        )
        raise


def archive(args):
    require_commands("docker", "findmnt", "git")
    if (args.instrument is None) != (args.snapshot_id is None):
        raise ArchiveError(
            "--instrument and --snapshot-id must be supplied together"
        )
    package = validate_storage_path(
        args.output,
        ARCHIVE_ROOT,
        must_exist=False,
        new_directory=True,
    )
    _, _, _, protected_paths = discover_source_storage(args, package)
    archive_root_mount = mount_evidence(ARCHIVE_ROOT)
    reject_output_overlap(
        ARCHIVE_ROOT,
        protected_paths,
        output_mount=archive_root_mount,
    )
    with operation_lock(
        archive_root_mount,
        protected_paths,
    ) as reservation:
        return _archive_locked(
            args,
            reservation,
            archive_root_mount,
        )


def read_checksums(package):
    checksum_path = package / "SHA256SUMS"
    if not checksum_path.is_file() or checksum_path.is_symlink():
        raise ArchiveError("package checksum file is missing or unsafe")
    entries = {}
    for line in checksum_path.read_text(encoding="utf-8").splitlines():
        match = re.fullmatch(r"([0-9a-f]{64})  ([a-zA-Z0-9._-]+)", line)
        if match is None or match.group(2) in entries:
            raise ArchiveError("package checksum file is malformed")
        entries[match.group(2)] = match.group(1)
    if set(entries) != set(PACKAGE_FILES):
        raise ArchiveError("package checksum set is not exact")
    for name, expected in entries.items():
        path = package / name
        if (
            not path.is_file()
            or path.is_symlink()
            or sha256_path(path) != expected
        ):
            raise ArchiveError(f"package checksum failed: {name}")
    return entries


def load_completed_package(path, storage_root):
    package = validate_storage_path(
        path,
        ARCHIVE_ROOT,
        must_exist=True,
    )
    checksums = read_checksums(package)
    manifest = json.loads((package / "manifest.json").read_text(encoding="utf-8"))
    if (
        manifest.get("toolId") != TOOL_ID
        or manifest.get("schemaVersion") != SCHEMA_VERSION
        or manifest.get("status") != "accepted"
        or manifest.get("archiveOnly") is not True
    ):
        raise ArchiveError("package manifest is not an accepted archive-only package")
    for key in ("archive", "toc", "catalog"):
        item = manifest[key]
        if item["sha256"] != checksums[item["path"]]:
            raise ArchiveError(f"manifest {key} checksum does not match package")
    if manifest["rowFingerprint"]["sha256"] != manifest[
        "sourceFenceBefore"
    ]["rowFingerprint"]["sha256"]:
        raise ArchiveError("manifest row fingerprint is internally inconsistent")
    catalog = json.loads((package / "catalog.json").read_text(encoding="utf-8"))
    if stable_hash(catalog["logicalCatalog"]) != manifest["catalog"][
        "logicalSha256"
    ]:
        raise ArchiveError("catalog logical hash is inconsistent")
    if catalog["logicalCatalog"] != manifest["catalog"].get("logicalCatalog"):
        raise ArchiveError("manifest logical catalog differs from catalog evidence")
    return package, manifest, catalog, checksums


def proof_container_name(proof_id, package_hash):
    token = re.sub(r"[^a-z0-9]", "", proof_id.lower())[:24]
    if not token:
        raise ArchiveError("proof ID is invalid")
    return f"fst-snapshot-archive-proof-{token}-{package_hash[:8]}"


def docker_bind_mount(source, destination, *, readonly):
    source = str(pathlib.Path(source))
    destination = str(PurePosixPath(destination))
    for label, value in (("source", source), ("destination", destination)):
        if (
            any(ord(character) < 32 for character in value)
            or "," in value
            or '"' in value
        ):
            raise ArchiveError(
                f"Docker bind mount {label} contains an unsafe delimiter"
            )
    if not pathlib.Path(source).is_absolute() or not destination.startswith("/"):
        raise ArchiveError("Docker bind mounts require absolute paths")
    value = f"type=bind,src={source},dst={destination}"
    if readonly:
        value += ",readonly"
    return value


def restore_container_command(
    container,
    image,
    pgdata,
    package,
    proof_id,
    package_hash,
    memory,
    cpus,
):
    return [
        "docker",
        "run",
        "-d",
        "--name",
        container,
        "--network",
        "none",
        "--cpus",
        cpus,
        "--memory",
        memory,
        "--pids-limit",
        "256",
        "--shm-size",
        "128m",
        "--label",
        f"fst.tool={TOOL_ID}",
        "--label",
        f"fst.proof={proof_id}",
        "--label",
        f"fst.package={package_hash}",
        "-e",
        "POSTGRES_HOST_AUTH_METHOD=trust",
        "-e",
        "POSTGRES_USER=fst_archive_proof",
        "-e",
        "POSTGRES_DB=fst_archive_proof",
        "--mount",
        docker_bind_mount(
            pgdata,
            "/var/lib/postgresql/data",
            readonly=False,
        ),
        "--mount",
        docker_bind_mount(package, "/package", readonly=True),
        image,
        "-c",
        "data_directory=/var/lib/postgresql/data",
        "-c",
        "max_connections=20",
        "-c",
        "max_parallel_workers=0",
        "-c",
        "dynamic_shared_memory_type=mmap",
    ]


def wait_ready(container):
    deadline = time.monotonic() + 120
    successes = 0
    while time.monotonic() < deadline:
        completed = run(
            [
                "docker",
                "exec",
                container,
                "pg_isready",
                "-U",
                "fst_archive_proof",
                "-d",
                "fst_archive_proof",
            ],
            timeout=15,
            check=False,
        )
        if completed.returncode == 0:
            successes += 1
            if successes >= 3:
                return
        else:
            successes = 0
        time.sleep(1)
    raise ArchiveError("isolated PostgreSQL 17 proof container did not become ready")


def inspect_proof_container(
    container,
    proof_id,
    package_hash,
    package,
    pgdata,
    storage_root,
):
    inspected = json.loads(
        run(["docker", "inspect", container]).stdout.decode("utf-8")
    )[0]
    labels = inspected.get("Config", {}).get("Labels") or {}
    host = inspected.get("HostConfig") or {}
    if (
        labels.get("fst.tool") != TOOL_ID
        or labels.get("fst.proof") != proof_id
        or labels.get("fst.package") != package_hash
    ):
        raise ArchiveError("proof container ownership labels differ")
    if host.get("NetworkMode") != "none":
        raise ArchiveError("proof container is not network-none")
    if host.get("PortBindings") not in (None, {}):
        raise ArchiveError("proof container has published port bindings")
    mounts = inspected.get("Mounts", [])
    if {item.get("Destination") for item in mounts} != {
        "/package",
        "/var/lib/postgresql/data",
    }:
        raise ArchiveError("proof container has unexpected data mounts")
    package_mounts = [
        item for item in mounts if item.get("Destination") == "/package"
    ]
    if (
        len(package_mounts) != 1
        or package_mounts[0].get("RW") is not False
        or pathlib.Path(package_mounts[0]["Source"]).resolve()
        != pathlib.Path(package).resolve()
    ):
        raise ArchiveError("proof package mount is not exact read-only")
    pgdata_mounts = [
        item
        for item in mounts
        if item.get("Destination") == "/var/lib/postgresql/data"
    ]
    if (
        len(pgdata_mounts) != 1
        or pgdata_mounts[0].get("RW") is not True
        or pgdata_mounts[0].get("Type") != "bind"
        or pathlib.Path(pgdata_mounts[0]["Source"]).resolve()
        != pathlib.Path(pgdata).resolve()
    ):
        raise ArchiveError("proof PGDATA mount is not exact read-write bind")
    if package_mounts[0].get("Type") != "bind":
        raise ArchiveError("proof package mount must be a bind mount")
    storage_device = os.stat(pathlib.Path(storage_root).resolve()).st_dev
    if os.stat(pathlib.Path(pgdata).resolve()).st_dev != storage_device:
        raise ArchiveError("proof PGDATA is outside the approved FST device")
    return {
        "containerId": inspected["Id"],
        "imageId": inspected["Image"],
        "networkMode": host["NetworkMode"],
        "portBindings": host.get("PortBindings"),
        "cpuNano": host.get("NanoCpus"),
        "memoryBytes": host.get("Memory"),
        "pidsLimit": host.get("PidsLimit"),
        "packageReadOnly": True,
        "pgdataReadWrite": True,
        "mountDestinations": sorted(
            item["Destination"] for item in mounts
        ),
    }


def validate_owned_scratch(scratch, proof_dir, proof_id):
    scratch = pathlib.Path(scratch)
    proof_dir = pathlib.Path(proof_dir).resolve(strict=True)
    if scratch.is_symlink() or not scratch.is_dir():
        raise ArchiveError("proof scratch is missing or unsafe")
    resolved = scratch.resolve(strict=True)
    if resolved != proof_dir / ".scratch":
        raise ArchiveError("proof scratch path is outside its owned proof directory")
    marker = json.loads((resolved / "owner.json").read_text(encoding="utf-8"))
    if marker != {
        "toolId": TOOL_ID,
        "proofId": proof_id,
        "scratch": str(resolved),
    }:
        raise ArchiveError("proof scratch ownership marker differs")
    pgdata = resolved / "pgdata"
    if pgdata.is_symlink() or not pgdata.is_dir():
        raise ArchiveError("proof PGDATA is missing or unsafe")
    return pgdata


def cleanup_owned_scratch(
    image,
    scratch,
    proof_dir,
    proof_id,
    package_hash,
):
    pgdata = validate_owned_scratch(scratch, proof_dir, proof_id)
    if image is not None:
        run(
            [
                "docker",
                "run",
                "--rm",
                "--name",
                proof_container_name(proof_id, package_hash) + "-cleanup",
                "--network",
                "none",
                "--label",
                f"fst.tool={TOOL_ID}",
                "--label",
                f"fst.proof={proof_id}",
                "--label",
                f"fst.package={package_hash}",
                "--user",
                "0:0",
                "--mount",
                docker_bind_mount(pgdata, "/owned", readonly=False),
                image,
                "sh",
                "-c",
                (
                    "find /owned -mindepth 1 -maxdepth 1 "
                    "-exec rm -rf -- {} +; "
                    f"chown {os.getuid()}:{os.getgid()} /owned; "
                    "chmod 700 /owned"
                ),
            ],
            timeout=1800,
        )
    elif any(pgdata.iterdir()):
        raise ArchiveError("proof PGDATA is nonempty without a cleanup image")
    if any(pgdata.iterdir()):
        raise ArchiveError("proof PGDATA cleanup is incomplete")
    pgdata.rmdir()
    (pathlib.Path(scratch) / "owner.json").unlink()
    pathlib.Path(scratch).rmdir()


def owned_proof_containers(proof_id, package_hash):
    completed = run(
        [
            "docker",
            "ps",
            "-aq",
            "--filter",
            f"label=fst.tool={TOOL_ID}",
            "--filter",
            f"label=fst.proof={proof_id}",
            "--filter",
            f"label=fst.package={package_hash}",
        ]
    )
    values = [
        value
        for value in completed.stdout.decode().splitlines()
        if value.strip()
    ]
    if len(values) > 1:
        raise ArchiveError("multiple containers claim the same proof identity")
    return values


def owned_container_volumes(container_id, proof_id, package_hash):
    inspected = json.loads(
        run(["docker", "inspect", container_id]).stdout.decode("utf-8")
    )[0]
    labels = inspected.get("Config", {}).get("Labels") or {}
    if (
        labels.get("fst.tool") != TOOL_ID
        or labels.get("fst.proof") != proof_id
        or labels.get("fst.package") != package_hash
    ):
        raise ArchiveError("owned proof container labels changed")
    volumes = []
    for mount in inspected.get("Mounts", []):
        if mount.get("Type") == "volume":
            name = mount.get("Name")
            if not name:
                raise ArchiveError("owned proof container has unnamed volume")
            volumes.append(name)
    return sorted(set(volumes))


def verify_owned_volumes_absent(volume_names):
    remaining = []
    for name in volume_names:
        inspected = run(
            ["docker", "volume", "inspect", name],
            check=False,
        )
        if inspected.returncode == 0:
            remaining.append(name)
        elif b"no such volume" not in inspected.stderr.lower():
            raise ArchiveError(
                f"could not prove owned volume absence: {name}"
            )
    if remaining:
        raise ArchiveError(
            "owned proof volumes remain after container cleanup: "
            + ", ".join(remaining)
        )


def remove_owned_proof_containers(proof_id, package_hash):
    transient_errors = []
    captured_volumes = set()
    attempts = 0
    for attempt in range(3):
        attempts = attempt + 1
        try:
            containers = owned_proof_containers(proof_id, package_hash)
        except Exception as error:
            transient_errors.append(str(error))
            if attempt < 2:
                time.sleep(1)
                continue
            raise ArchiveError(
                "owned proof container discovery failed after retries"
            ) from error
        if not containers:
            verify_owned_volumes_absent(captured_volumes)
            return {
                "containerRemoved": True,
                "removalAttempts": attempts,
                "transientErrorsCleared": bool(transient_errors),
                "unexpectedVolumeNames": sorted(captured_volumes),
                "ownedVolumesRemoved": True,
            }
        captured_volumes.update(
            owned_container_volumes(
                containers[0],
                proof_id,
                package_hash,
            )
        )
        try:
            run(
                ["docker", "rm", "-f", "-v", containers[0]],
                timeout=120,
            )
        except Exception as error:
            transient_errors.append(str(error))
        if attempt < 2:
            time.sleep(1)
    for verification in range(3):
        try:
            if not owned_proof_containers(proof_id, package_hash):
                verify_owned_volumes_absent(captured_volumes)
                return {
                    "containerRemoved": True,
                    "removalAttempts": attempts,
                    "transientErrorsCleared": bool(transient_errors),
                    "unexpectedVolumeNames": sorted(captured_volumes),
                    "ownedVolumesRemoved": True,
                }
        except Exception as error:
            transient_errors.append(str(error))
        if verification < 2:
            time.sleep(1)
    raise ArchiveError(
        "owned proof container remains after removal retries"
    )


def prepare_proof(args):
    require_commands("docker", "findmnt")
    package, manifest, catalog, checksums = load_completed_package(
        args.package,
        args.storage_root,
    )
    proof_id = args.proof_id or (
        datetime.now(timezone.utc).strftime("%Y%m%dT%H%M%SZ")
        + "-"
        + uuid.uuid4().hex[:8]
    )
    if not re.fullmatch(r"[a-z0-9][a-z0-9-]{0,63}", proof_id):
        raise ArchiveError("proof ID must be a bounded lowercase token")
    package_hash = checksums["manifest.json"]
    protected_locations = (
        manifest.get("sourceIdentity", {}).get(
            "protectedHostLocations"
        )
    )
    if not isinstance(protected_locations, list) or not protected_locations:
        raise ArchiveError("archive package lacks protected source locations")
    storage_mount = mount_evidence(args.storage_root)
    archive_root_mount = mount_evidence(ARCHIVE_ROOT)
    require_same_mount_device(
        storage_mount,
        archive_root_mount,
        "archive root",
    )
    reject_nested_mounts(ARCHIVE_ROOT, "archive root")
    reject_output_overlap(
        ARCHIVE_ROOT,
        protected_locations,
        output_mount=archive_root_mount,
    )
    package_mount = mount_evidence(package)
    require_same_mount_device(storage_mount, package_mount, "proof package")
    reject_nested_mounts(package, "proof package")
    proof_parent = package / "proofs"
    requested = (
        pathlib.Path(args.proof_output)
        if args.proof_output
        else proof_parent / proof_id
    )
    if not requested.is_absolute():
        raise ArchiveError("proof output path must be absolute")
    if requested.resolve(strict=False).parent != proof_parent.resolve(
        strict=False
    ):
        raise ArchiveError("proof output must be a direct child of package/proofs")
    if proof_parent.exists():
        if proof_parent.is_symlink() or not proof_parent.is_dir():
            raise ArchiveError("proof output parent is unsafe")
        proof_parent_mount = mount_evidence(proof_parent)
        require_same_mount_device(
            storage_mount,
            proof_parent_mount,
            "proof output parent",
        )
        reject_nested_mounts(proof_parent, "proof output parent")
    else:
        proof_parent_mount = package_mount
    reject_output_overlap(
        proof_parent,
        protected_locations,
        output_mount=proof_parent_mount,
    )
    prospective_proof_dir = requested
    reject_output_overlap(
        prospective_proof_dir,
        protected_locations,
        output_mount=proof_parent_mount,
    )
    docker_bind_mount(package, "/package", readonly=True)
    docker_bind_mount(
        requested / ".scratch" / "pgdata",
        "/var/lib/postgresql/data",
        readonly=False,
    )
    return {
        "package": package,
        "manifest": manifest,
        "catalog": catalog,
        "checksums": checksums,
        "proofId": proof_id,
        "packageHash": package_hash,
        "protectedLocations": protected_locations,
        "storageMount": storage_mount,
        "archiveRootMount": archive_root_mount,
        "packageMount": package_mount,
        "proofParent": proof_parent,
        "proofParentMount": proof_parent_mount,
        "proofParentExisted": proof_parent.exists(),
        "requested": requested,
    }


def _prove_locked(args, reservation, prepared):
    package = prepared["package"]
    manifest = prepared["manifest"]
    catalog = prepared["catalog"]
    checksums = prepared["checksums"]
    proof_id = prepared["proofId"]
    package_hash = prepared["packageHash"]
    protected_locations = prepared["protectedLocations"]
    storage_mount = prepared["storageMount"]
    package_mount = prepared["packageMount"]
    proof_parent = prepared["proofParent"]
    requested = prepared["requested"]
    archive_root_mount = mount_evidence(ARCHIVE_ROOT)
    if mount_identity(archive_root_mount) != mount_identity(
        prepared["archiveRootMount"]
    ):
        raise ArchiveError("archive root mount changed before proof setup")
    reject_nested_mounts(ARCHIVE_ROOT, "archive root")
    reject_output_overlap(
        ARCHIVE_ROOT,
        protected_locations,
        output_mount=archive_root_mount,
    )
    current_package_mount = mount_evidence(package)
    if mount_identity(current_package_mount) != mount_identity(package_mount):
        raise ArchiveError("proof package mount changed after lock acquisition")
    reject_nested_mounts(package, "proof package")
    if proof_parent.exists() != prepared["proofParentExisted"]:
        raise ArchiveError("proof output parent state changed after validation")
    if proof_parent.exists():
        current_parent_mount = mount_evidence(proof_parent)
        if mount_identity(current_parent_mount) != mount_identity(
            prepared["proofParentMount"]
        ):
            raise ArchiveError(
                "proof output parent mount changed after validation"
            )
    reject_output_overlap(
        proof_parent,
        protected_locations,
        output_mount=(
            mount_evidence(proof_parent)
            if proof_parent.exists()
            else current_package_mount
        ),
    )
    proof_parent_mount = (
        mount_evidence(proof_parent)
        if proof_parent.exists()
        else current_package_mount
    )
    parent_admission_mount = mount_evidence(package)
    if mount_identity(parent_admission_mount) != mount_identity(package_mount):
        raise ArchiveError(
            "proof package mount identity changed before parent reservation"
        )
    if not proof_parent.exists():
        proof_parent.mkdir(mode=0o700)
    if proof_parent.resolve() != package.resolve() / "proofs":
        raise ArchiveError("proof output parent escapes the archive package")
    proof_dir = validate_storage_path(
        requested,
        args.storage_root,
        must_exist=False,
        new_directory=True,
    )
    if proof_dir.parent.resolve() != proof_parent.resolve():
        raise ArchiveError("proof output must be a direct child of package/proofs")
    docker_bind_mount(package, "/package", readonly=True)
    docker_bind_mount(
        proof_dir / ".scratch" / "pgdata",
        "/var/lib/postgresql/data",
        readonly=False,
    )
    initial_parent_mount = mount_evidence(proof_parent)
    require_same_mount_device(
        storage_mount,
        initial_parent_mount,
        "proof output parent",
    )
    reject_nested_mounts(proof_parent, "proof output parent")
    reject_output_overlap(
        proof_dir,
        protected_locations,
        output_mount=initial_parent_mount,
    )
    reservation_parent_mount = mount_evidence(proof_parent)
    if mount_identity(reservation_parent_mount) != mount_identity(
        initial_parent_mount
    ):
        raise ArchiveError(
            "proof parent mount identity changed before directory reservation"
        )
    reject_nested_mounts(proof_parent, "proof output parent")
    proof_dir.mkdir(mode=0o700)
    started = utc_now()
    scratch = proof_dir / ".scratch"
    pgdata = scratch / "pgdata"
    container = proof_container_name(proof_id, package_hash)
    image_id = None
    validation = None
    capacity = None
    operation_error = None
    cleanup_errors = []
    removal = {
        "containerRemoved": False,
        "removalAttempts": 0,
        "transientErrorsCleared": False,
        "unexpectedVolumeNames": [],
        "ownedVolumesRemoved": False,
    }
    try:
        scratch.mkdir(mode=0o700)
        write_json(
            scratch / "owner.json",
            {
                "toolId": TOOL_ID,
                "proofId": proof_id,
                "scratch": str(scratch.resolve()),
            },
        )
        pgdata.mkdir(mode=0o700)
        reject_nested_mounts(package, "proof package")
        reject_nested_mounts(proof_dir, "proof output")
        storage_mount = mount_evidence(args.storage_root)
        package_mount = mount_evidence(package)
        proof_mount = mount_evidence(proof_dir)
        pgdata_mount = mount_evidence(pgdata)
        for label, observed in (
            ("proof package", package_mount),
            ("proof output", proof_mount),
            ("proof PGDATA", pgdata_mount),
        ):
            require_same_mount_device(storage_mount, observed, label)
        physical_catalog = catalog.get("physicalCatalog")
        if not isinstance(physical_catalog, list):
            raise ArchiveError("package physical catalog is missing")
        target_catalogs = [
            item
            for item in physical_catalog
            if item.get("name") == manifest["target"]["childRelation"]
        ]
        if len(target_catalogs) != 1:
            raise ArchiveError("package target catalog is not unique")
        archive_path = package / manifest["archive"]["path"]
        archive_bytes = archive_path.stat().st_size
        required_free = required_capacity_bytes(
            target_catalogs[0]["totalBytes"],
            archive_bytes,
        )
        if proof_mount["freeBytes"] < required_free:
            raise ArchiveError("insufficient FST capacity for isolated proof")
        capacity = {
            "package": package_mount,
            "proofOutput": proof_mount,
            "pgdata": pgdata_mount,
            "archiveBytes": archive_bytes,
            "sourcePhysicalBytes": target_catalogs[0]["totalBytes"],
            "requiredFreeBytes": required_free,
            "reservation": reservation,
        }
        image_id = json.loads(
            run(
                ["docker", "image", "inspect", args.postgres_image]
            ).stdout.decode("utf-8")
        )[0]["Id"]
        if owned_proof_containers(proof_id, package_hash):
            raise ArchiveError("proof identity already owns a container")
        admission_mount = mount_evidence(proof_dir)
        if (
            admission_mount["freeBytes"] < required_free
            or archive_path.stat().st_size != archive_bytes
            or sha256_path(archive_path) != manifest["archive"]["sha256"]
        ):
            raise ArchiveError("proof capacity/package changed before admission")
        require_same_mount_device(
            storage_mount,
            admission_mount,
            "proof admission output",
        )
        capacity["admission"] = admission_mount
        run(
            restore_container_command(
                container,
                image_id,
                pgdata,
                package,
                proof_id,
                package_hash,
                PROOF_MEMORY,
                PROOF_CPUS,
            ),
            timeout=120,
        )
        owned = owned_proof_containers(proof_id, package_hash)
        if len(owned) != 1:
            raise ArchiveError("proof container did not acquire unique ownership")
        wait_ready(container)
        identity = database_identity(
            container,
            "fst_archive_proof",
            "fst_archive_proof",
        )
        if identity["dataDirectory"] != "/var/lib/postgresql/data":
            raise ArchiveError("proof data_directory differs from owned PGDATA")
        posix_relative_to(
            identity["dataDirectory"],
            "/var/lib/postgresql/data",
        )
        container_evidence = inspect_proof_container(
            container,
            proof_id,
            package_hash,
            package,
            pgdata,
            args.storage_root,
        )
        run(
            [
                "docker",
                "exec",
                container,
                "pg_restore",
                "--exit-on-error",
                "--no-owner",
                "--no-privileges",
                "-U",
                "fst_archive_proof",
                "-d",
                "fst_archive_proof",
                "/package/archive.custom",
            ],
            timeout=args.timeout_seconds,
        )
        target = manifest["target"]
        relations = (
            ROOT_RELATION,
            target["rootRelation"],
            target["childRelation"],
        )
        restored_catalog = capture_catalog(
            container,
            "fst_archive_proof",
            "fst_archive_proof",
            target["childSchema"],
            relations,
        )
        restored_child = restored_catalog[-1]
        validate_primary_key(restored_child)
        source_logical = catalog["logicalCatalog"]
        expected_logical = [
            {**item, "owner": "fst_archive_proof"} for item in source_logical
        ]
        restored_logical = logical_catalog(restored_catalog)
        if restored_logical != expected_logical:
            raise ArchiveError("restored logical catalog differs from archive manifest")
        restored_fingerprint = stream_fingerprint(
            container,
            "fst_archive_proof",
            "fst_archive_proof",
            target["childSchema"],
            target["childRelation"],
            timeout_seconds=args.timeout_seconds,
        )
        if restored_fingerprint != manifest["rowFingerprint"]:
            raise ArchiveError("restored row count or SHA-256 fingerprint differs")
        if read_checksums(package) != checksums:
            raise ArchiveError("archive package changed during isolated proof")
        if args.keep_proof_outputs:
            write_json(proof_dir / "restored-catalog.json", restored_catalog)
            write_json(proof_dir / "container-evidence.json", container_evidence)
        validation = {
            "databaseIdentity": identity,
            "container": container_evidence,
            "restoredLogicalCatalogSha256": stable_hash(restored_logical),
            "expectedLogicalCatalogSha256": stable_hash(expected_logical),
            "rowFingerprint": restored_fingerprint,
        }
    except Exception as error:
        operation_error = error
    finally:
        try:
            removal = remove_owned_proof_containers(
                proof_id,
                package_hash,
            )
        except Exception as error:
            cleanup_errors.append(str(error))
            container_absent = False
        container_absent = removal["containerRemoved"] and not cleanup_errors
        if container_absent:
            try:
                if scratch.exists():
                    cleanup_owned_scratch(
                        image_id,
                        scratch,
                        proof_dir,
                        proof_id,
                        package_hash,
                    )
            except Exception as error:
                cleanup_errors.append(str(error))
            try:
                post_cleanup_removal = remove_owned_proof_containers(
                    proof_id,
                    package_hash,
                )
                removal["removalAttempts"] += post_cleanup_removal[
                    "removalAttempts"
                ]
                removal["transientErrorsCleared"] = (
                    removal["transientErrorsCleared"]
                    or post_cleanup_removal["transientErrorsCleared"]
                )
                removal["unexpectedVolumeNames"] = sorted(
                    set(removal["unexpectedVolumeNames"])
                    | set(post_cleanup_removal["unexpectedVolumeNames"])
                )
                removal["ownedVolumesRemoved"] = (
                    removal["ownedVolumesRemoved"]
                    and post_cleanup_removal["ownedVolumesRemoved"]
                )
                container_absent = post_cleanup_removal["containerRemoved"]
            except Exception as error:
                cleanup_errors.append(str(error))
                container_absent = False
        else:
            cleanup_errors.append(
                "owned proof container absence was not proven"
            )
        cleanup = {
            **removal,
            "containerAbsenceProven": container_absent,
            "scratchRemoved": not scratch.exists(),
            "pgdataRemoved": not pgdata.exists(),
            "completedAtUtc": utc_now(),
            "errors": cleanup_errors,
        }
        write_json(proof_dir / "cleanup.json", cleanup)

    if operation_error is not None or cleanup_errors or not all(
        cleanup[key]
        for key in (
            "containerRemoved",
            "containerAbsenceProven",
            "ownedVolumesRemoved",
            "scratchRemoved",
            "pgdataRemoved",
        )
    ):
        reason_parts = []
        if operation_error is not None:
            reason_parts.append(str(operation_error))
        reason_parts.extend(cleanup_errors)
        rejection = {
            "schemaVersion": SCHEMA_VERSION,
            "toolId": TOOL_ID,
            "status": "rejected",
            "archiveOnly": True,
            "proofId": proof_id,
            "startedAtUtc": started,
            "rejectedAtUtc": utc_now(),
            "packageManifestSha256": package_hash,
            "reason": "; ".join(reason_parts)
                or "proof cleanup evidence is incomplete",
            "cleanup": cleanup,
        }
        write_json(proof_dir / "proof-rejected.json", rejection)
        write_checksums(
            proof_dir,
            ("cleanup.json", "proof-rejected.json"),
        )
        if isinstance(operation_error, ArchiveError):
            raise operation_error
        raise ArchiveError(rejection["reason"]) from operation_error

    cleanup_evidence = {
        "path": "cleanup.json",
        "sha256": sha256_path(proof_dir / "cleanup.json"),
    }
    proof_manifest = {
        "schemaVersion": SCHEMA_VERSION,
        "toolId": TOOL_ID,
        "status": "accepted",
        "archiveOnly": True,
        "proofId": proof_id,
        "startedAtUtc": started,
        "completedAtUtc": utc_now(),
        "package": str(package),
        "packageManifestSha256": package_hash,
        "archiveSha256": manifest["archive"]["sha256"],
        "imageId": image_id,
        "networkMode": "none",
        "publishedPorts": 0,
        "resources": {
            "cpus": PROOF_CPUS,
            "memory": PROOF_MEMORY,
            "pidsLimit": 256,
        },
        "capacityAndMount": capacity,
        "validation": validation,
        "cleanup": cleanup,
        "cleanupEvidence": cleanup_evidence,
        "verboseOutputsRetained": args.keep_proof_outputs,
    }
    write_json(proof_dir / "proof-manifest.json", proof_manifest)
    proof_files = ["cleanup.json", "proof-manifest.json"]
    if args.keep_proof_outputs:
        proof_files.extend(
            ["container-evidence.json", "restored-catalog.json"]
        )
    write_checksums(proof_dir, proof_files)
    return proof_manifest


def prove(args):
    prepared = prepare_proof(args)
    with operation_lock(
        prepared["archiveRootMount"],
        prepared["protectedLocations"],
    ) as reservation:
        return _prove_locked(args, reservation, prepared)


def build_parser():
    parser = argparse.ArgumentParser(
        description=(
            "Read-only snapshot-generation archive and isolated restore proof"
        )
    )
    subparsers = parser.add_subparsers(dest="command", required=True)
    archive_parser = subparsers.add_parser("archive")
    archive_parser.add_argument("--output", required=True)
    archive_parser.add_argument(
        "--instrument",
        choices=tuple(INSTRUMENT_BY_KEY),
    )
    archive_parser.add_argument("--snapshot-id", type=int)
    archive_parser.add_argument(
        "--source-container",
        default="fst-postgres",
    )
    archive_parser.add_argument("--pg-user", default="fst")
    archive_parser.add_argument("--pg-database", default="fstservice")
    archive_parser.add_argument("--timeout-seconds", type=int, default=7200)
    archive_parser.set_defaults(storage_root=str(DEFAULT_STORAGE_ROOT))

    prove_parser = subparsers.add_parser("prove")
    prove_parser.add_argument("--package", required=True)
    prove_parser.add_argument("--proof-id")
    prove_parser.add_argument("--proof-output")
    prove_parser.add_argument(
        "--postgres-image",
        default=DEFAULT_IMAGE,
    )
    prove_parser.add_argument("--timeout-seconds", type=int, default=7200)
    prove_parser.add_argument(
        "--keep-proof-outputs",
        action="store_true",
    )
    prove_parser.set_defaults(storage_root=str(DEFAULT_STORAGE_ROOT))
    return parser


def main(argv=None):
    args = build_parser().parse_args(argv)
    try:
        if args.timeout_seconds <= 0:
            raise ArchiveError("--timeout-seconds must be positive")
        if args.command == "archive":
            result = archive(args)
        else:
            result = prove(args)
    except ArchiveError as error:
        print(f"ERROR: {error}", file=sys.stderr)
        return 1
    print(json.dumps(result, sort_keys=True))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
