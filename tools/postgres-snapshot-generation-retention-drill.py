#!/usr/bin/env python3

"""Isolated PostgreSQL 17 safety drill for generation-leaf retention.

The drill never accepts a database/container target.  It builds one synthetic
fixture, archives and restore-proves one unreferenced generation leaf, proves a
filesystem-only mailbox/prover handoff, and measures attached DROP versus
ordinary DETACH/reattach/detached-DROP catalog paths.
"""

import argparse
import contextlib
import hashlib
import importlib.util
import json
import os
import pathlib
import re
import secrets
import stat
import subprocess
import sys
import time
from datetime import datetime, timezone


SCRIPT_DIR = pathlib.Path(__file__).resolve().parent
MIGRATION_SCRIPT = SCRIPT_DIR / "postgres-snapshot-generation-migration.py"
FST_MOUNT_ROOT = pathlib.Path("/mnt/docker-storage")
ARTIFACT_ROOT = pathlib.Path(
    "/mnt/docker-storage/Docker/FestivalServiceTracker/"
    "fst-data/autonomous-artifacts"
)
DOCKER_SOCKET = pathlib.Path("/var/run/docker.sock")
DOCKER_ENDPOINT = "unix:///var/run/docker.sock"
MIN_FST_CAPACITY_BYTES = 3_500_000_000_000
FORMAT_VERSION = 1
TOOL_ID = "fst.snapshot-generation-retention-drill.v1"
POSTGRES_IMAGE = "postgres:17"
IMAGE_ID_PATTERN = re.compile(r"sha256:[0-9a-f]{64}")
SUCCESS_ARTIFACT_NAMES = (
    "drill-report.json",
    "checksums.json",
    "seal.json",
)
SEAL_FAILURE_NAME = "seal-failure.json"
POSTGRES_USER = "fst"
POSTGRES_DATABASE = "fstservice"
POSTGRES_MAJOR = 17
TARGET_SNAPSHOT_ID = 1401
PREVIOUS_SNAPSHOT_ID = 1402
CURRENT_SNAPSHOT_ID = 1403
WORKING_SNAPSHOT_ID = 1404
FIXTURE_SNAPSHOT_IDS = (
    TARGET_SNAPSHOT_ID,
    PREVIOUS_SNAPSHOT_ID,
    CURRENT_SNAPSHOT_ID,
    WORKING_SNAPSHOT_ID,
)
PROTECTED_SNAPSHOT_IDS = (
    PREVIOUS_SNAPSHOT_ID,
    CURRENT_SNAPSHOT_ID,
    WORKING_SNAPSHOT_ID,
)


class DrillError(RuntimeError):
    """A fail-closed drill invariant."""


def docker_engine_command(*arguments):
    return [
        "docker",
        "--host",
        DOCKER_ENDPOINT,
        *(str(argument) for argument in arguments),
    ]


def parse_docker_invocation(arguments):
    values = [str(value) for value in arguments]
    if not values or values[0] != "docker":
        return {
            "isDocker": False,
            "isContext": False,
            "host": None,
            "command": values,
        }
    if len(values) > 1 and values[1] == "context":
        return {
            "isDocker": True,
            "isContext": True,
            "host": None,
            "command": values[1:],
        }

    index = 1
    host = None
    if index < len(values) and values[index] in ("--host", "-H"):
        if index + 1 >= len(values):
            raise DrillError("Docker --host option is missing its endpoint")
        host = values[index + 1]
        index += 2
    elif index < len(values) and (
        values[index].startswith("--host=")
        or values[index].startswith("-H=")
    ):
        host = values[index].split("=", 1)[1]
        index += 1
    return {
        "isDocker": True,
        "isContext": False,
        "host": host,
        "command": values[index:],
    }


class DockerFenceRunner:
    """Block Docker mutations until the local-daemon identity fence passes."""

    def __init__(self, delegate):
        self.delegate = delegate
        self.docker_mutations_allowed = False
        self.immutable_image_id = None
        self._docker_engine_audit = []
        self._docker_context_audit = []

    @staticmethod
    def docker_command_is_read_only(arguments):
        invocation = parse_docker_invocation(arguments)
        if not invocation["isDocker"]:
            return True
        command = invocation["command"]
        if not command:
            return False
        if invocation["isContext"]:
            return command[:2] in (
                ["context", "show"],
                ["context", "inspect"],
            )
        if command[0] in ("info", "inspect", "ps"):
            return True
        if command[:2] in (
            ["image", "inspect"],
            ["volume", "ls"],
        ):
            return True
        return False

    @staticmethod
    def docker_command_allowed_before_authorization(arguments):
        invocation = parse_docker_invocation(arguments)
        if not invocation["isDocker"]:
            return True
        if invocation["isContext"]:
            return invocation["command"][:2] in (
                ["context", "show"],
                ["context", "inspect"],
            )
        return invocation["command"][:1] == ["info"]

    def authorize_docker_mutations(self, evidence):
        if evidence.get("passed") is not True:
            raise DrillError("Docker identity evidence did not pass")
        if evidence.get("endpoint") != DOCKER_ENDPOINT:
            raise DrillError("Docker identity evidence has the wrong endpoint")
        self.docker_mutations_allowed = True

    def authorize_container_image(self, image_id):
        if not self.docker_mutations_allowed:
            raise DrillError(
                "container image authorization requires the Docker fence"
            )
        if not IMAGE_ID_PATTERN.fullmatch(str(image_id)):
            raise DrillError("container image ID is not an immutable sha256 ID")
        self.immutable_image_id = str(image_id)

    def _prepare(self, arguments, transport):
        values = [str(value) for value in arguments]
        invocation = parse_docker_invocation(values)
        if not invocation["isDocker"]:
            return values
        if invocation["isContext"]:
            if not self.docker_command_is_read_only(values):
                raise DrillError("Docker context mutation is forbidden")
            self._docker_context_audit.append(
                {
                    "transport": transport,
                    "command": " ".join(invocation["command"]),
                }
            )
            return values

        host = invocation["host"]
        if host not in (None, DOCKER_ENDPOINT):
            raise DrillError(
                f"Docker Engine endpoint must be {DOCKER_ENDPOINT}, "
                f"observed {host}"
            )
        if (
            not self.docker_mutations_allowed
            and not self.docker_command_allowed_before_authorization(values)
        ):
            raise DrillError(
                "Docker Engine operation attempted before the daemon/context "
                "fence"
            )
        command = invocation["command"]
        if not command:
            raise DrillError("Docker Engine command is missing")
        prepared = docker_engine_command(*command)
        creation = command[0] in ("create", "run")
        if creation:
            if self.immutable_image_id is None:
                raise DrillError(
                    "Docker container creation attempted before immutable "
                    "image authorization"
                )
            if "--pull=never" not in command:
                raise DrillError(
                    "Docker container creation must use --pull=never"
                )
            if self.immutable_image_id not in command:
                raise DrillError(
                    "Docker container creation must use the authorized "
                    "immutable image ID"
                )
        self._docker_engine_audit.append(
            {
                "transport": transport,
                "operation": " ".join(command[:2]),
                "endpoint": DOCKER_ENDPOINT,
                "endpointPinned": True,
                "containerCreation": creation,
                "pullNever": (not creation or "--pull=never" in command),
                "immutableImage": (
                    not creation or self.immutable_image_id in command
                ),
            }
        )
        return prepared

    def run(self, arguments, **kwargs):
        return self.delegate.run(
            self._prepare(arguments, "run"),
            **kwargs,
        )

    def run_to_file(self, arguments, path, **kwargs):
        return self.delegate.run_to_file(
            self._prepare(arguments, "run_to_file"),
            path,
            **kwargs,
        )

    def popen(self, arguments, **kwargs):
        return subprocess.Popen(
            self._prepare(arguments, "popen"),
            **kwargs,
        )

    def docker_command_evidence(self):
        operation_counts = {}
        for item in self._docker_engine_audit:
            operation = item["operation"]
            operation_counts[operation] = operation_counts.get(operation, 0) + 1
        creations = [
            item
            for item in self._docker_engine_audit
            if item["containerCreation"]
        ]
        return {
            "endpoint": DOCKER_ENDPOINT,
            "engineInvocationCount": len(self._docker_engine_audit),
            "endpointPinnedCount": sum(
                1 for item in self._docker_engine_audit
                if item["endpointPinned"]
            ),
            "unpinnedEngineInvocationCount": sum(
                1 for item in self._docker_engine_audit
                if not item["endpointPinned"]
            ),
            "popenInvocationCount": sum(
                1 for item in self._docker_engine_audit
                if item["transport"] == "popen"
            ),
            "containerCreationCount": len(creations),
            "pullNeverContainerCreationCount": sum(
                1 for item in creations if item["pullNever"]
            ),
            "immutableImageContainerCreationCount": sum(
                1 for item in creations if item["immutableImage"]
            ),
            "contextReadCount": len(self._docker_context_audit),
            "operationCounts": dict(sorted(operation_counts.items())),
            "passed": (
                bool(self._docker_engine_audit)
                and all(
                    item["endpointPinned"]
                    and item["pullNever"]
                    and item["immutableImage"]
                    for item in self._docker_engine_audit
                )
            ),
        }


def load_migration_primitives():
    spec = importlib.util.spec_from_file_location(
        "fst_snapshot_generation_migration_primitives",
        MIGRATION_SCRIPT,
    )
    module = importlib.util.module_from_spec(spec)
    sys.modules[spec.name] = module
    spec.loader.exec_module(module)
    return module


migration = load_migration_primitives()
TARGETS = migration.TARGETS
TARGET_BY_KEY = migration.TARGET_BY_KEY


def utc_now():
    return datetime.now(timezone.utc).isoformat().replace("+00:00", "Z")


def canonical_compact_bytes(value):
    return (
        json.dumps(
            value,
            sort_keys=True,
            separators=(",", ":"),
            ensure_ascii=True,
        )
        + "\n"
    ).encode("utf-8")


def sha256_bytes(value):
    return hashlib.sha256(value).hexdigest()


def sha256_path(path):
    return migration.sha256_path(path)


def read_json(path):
    return migration.read_json(path)


def integrity_document(value):
    body = dict(value)
    body["integritySha256"] = migration.report_integrity(body)
    return body


def write_integrity_json(path, value):
    body = integrity_document(value)
    migration.write_json_exclusive(path, body)
    return body


def atomic_publish_bytes(path, value, *, mode=0o600):
    """Publish bytes by same-directory write, fsync, rename, and dir fsync."""

    path = pathlib.Path(path)
    path.parent.mkdir(parents=True, exist_ok=True, mode=0o700)
    if path.exists():
        raise DrillError(f"atomic publication target already exists: {path}")
    temporary = path.with_name(
        f".{path.name}.partial-{os.getpid()}-{time.time_ns()}"
    )
    descriptor = os.open(
        temporary,
        os.O_WRONLY | os.O_CREAT | os.O_EXCL | os.O_CLOEXEC,
        mode,
    )
    try:
        with os.fdopen(descriptor, "wb") as handle:
            handle.write(value)
            handle.flush()
            os.fsync(handle.fileno())
        os.replace(temporary, path)
        migration.fsync_directory(path.parent)
    finally:
        with contextlib.suppress(FileNotFoundError):
            temporary.unlink()
    return path


def atomic_publish_json(path, value):
    return atomic_publish_bytes(path, migration.canonical_json_bytes(value))


def write_torn_bytes(path, value):
    """Durably leave a same-directory partial without publishing a final file."""

    path = pathlib.Path(path)
    path.parent.mkdir(parents=True, exist_ok=True, mode=0o700)
    if path.exists():
        raise DrillError(f"torn-evidence path already exists: {path}")
    descriptor = os.open(
        path,
        os.O_WRONLY | os.O_CREAT | os.O_EXCL | os.O_CLOEXEC,
        0o600,
    )
    with os.fdopen(descriptor, "wb") as handle:
        handle.write(value)
        handle.flush()
        os.fsync(handle.fileno())
    migration.fsync_directory(path.parent)
    return path


def find_mount(runner, path):
    completed = runner.run(
        [
            "findmnt",
            "-T",
            str(path),
            "-n",
            "-b",
            "-o",
            "SOURCE,FSTYPE,MAJ:MIN,TARGET,UUID,SIZE",
        ],
        timeout=30,
    )
    fields = completed.stdout.strip().split()
    if len(fields) != 6:
        raise DrillError(f"could not identify filesystem for {path}")
    try:
        total_bytes = int(fields[5])
    except ValueError as error:
        raise DrillError(
            f"could not identify filesystem capacity for {path}"
        ) from error
    return {
        "source": fields[0],
        "filesystemType": fields[1],
        "deviceId": fields[2],
        "mountTarget": fields[3],
        "uuid": fields[4],
        "totalBytes": total_bytes,
    }


def mount_identity(mount):
    return {
        key: mount[key]
        for key in (
            "source",
            "filesystemType",
            "deviceId",
            "mountTarget",
            "uuid",
        )
    }


def validate_work_root(
    runner,
    requested,
    *,
    expected_device_id,
    expected_device_uuid,
    allowed_root=ARTIFACT_ROOT,
    mount_root=FST_MOUNT_ROOT,
    minimum_capacity_bytes=MIN_FST_CAPACITY_BYTES,
):
    if not expected_device_id:
        raise DrillError("--expected-device-id is required")
    if not expected_device_uuid:
        raise DrillError("--expected-device-uuid is required")

    mount_root = pathlib.Path(mount_root)
    if not mount_root.is_absolute() or not mount_root.is_dir():
        raise DrillError("the fixed 4 TB mount root is unavailable")
    migration.validate_no_symlink_components(mount_root)
    resolved_mount_root = mount_root.resolve(strict=True)

    allowed_root = pathlib.Path(allowed_root)
    if not allowed_root.is_absolute() or not allowed_root.is_dir():
        raise DrillError("the fixed autonomous artifact root is unavailable")
    migration.validate_no_symlink_components(allowed_root)
    allowed = allowed_root.resolve(strict=True)
    if not migration.path_is_beneath(allowed, resolved_mount_root):
        raise DrillError(
            "the fixed autonomous artifact root is outside the 4 TB mount"
        )

    drive_mount = find_mount(runner, resolved_mount_root)
    filesystem = drive_mount["filesystemType"].lower()
    if drive_mount["mountTarget"] != str(resolved_mount_root):
        raise DrillError(
            "the fixed 4 TB root is not an exact mountpoint"
        )
    if (
        filesystem != "ext4"
        or migration.REMOTE_FILESYSTEM_PATTERN.search(filesystem)
        or filesystem not in migration.LOCAL_FILESYSTEMS
    ):
        raise DrillError("the fixed 4 TB root must use local ext4")
    if drive_mount["deviceId"] != expected_device_id:
        raise DrillError(
            "4 TB device identity mismatch: expected "
            f"{expected_device_id}, observed {drive_mount['deviceId']}"
        )
    if drive_mount["uuid"] != expected_device_uuid:
        raise DrillError(
            "4 TB device UUID mismatch: expected "
            f"{expected_device_uuid}, observed {drive_mount['uuid']}"
        )
    if drive_mount["totalBytes"] < minimum_capacity_bytes:
        raise DrillError(
            "4 TB mount capacity is below the required "
            f"{minimum_capacity_bytes} bytes"
        )

    allowed_mount = find_mount(runner, allowed)
    if mount_identity(allowed_mount) != mount_identity(drive_mount):
        raise DrillError(
            "the fixed autonomous artifact root is not on the exact "
            "4 TB mount/device/source/UUID"
        )

    requested = pathlib.Path(requested)
    if not requested.is_absolute():
        raise DrillError("--work-root must be absolute")
    if requested == allowed_root:
        raise DrillError("--work-root must be a run-specific child directory")
    requested_parent = requested.parent
    if not requested_parent.exists():
        raise DrillError("--work-root must be a direct child of the fixed root")
    migration.validate_no_symlink_components(requested_parent)
    if requested_parent.resolve(strict=True) != allowed:
        raise DrillError("--work-root must be a direct child of the fixed root")
    resolved = requested.resolve(strict=False)
    if resolved.parent != allowed:
        raise DrillError(
            f"work root must remain beneath the 4 TB artifact root: {allowed}"
        )
    if pathlib.Path("/tmp") in (resolved, *resolved.parents):
        raise DrillError("work root must not use a temporary-system path")
    if requested.exists():
        migration.validate_no_symlink_components(requested)
        if requested.is_symlink() or not requested.is_dir():
            raise DrillError("existing work root must be a real directory")
        if any(requested.iterdir()):
            raise DrillError("existing work root must be empty")

    mount_probe = requested if requested.exists() else requested_parent
    requested_mount = find_mount(runner, mount_probe.resolve(strict=True))
    if mount_identity(requested_mount) != mount_identity(drive_mount):
        raise DrillError(
            "work root is not on the exact 4 TB mount/device/source/UUID"
        )
    return {
        "requestedPath": str(requested),
        "resolvedPath": str(resolved),
        "allowedRoot": str(allowed),
        "mountRoot": str(resolved_mount_root),
        "minimumCapacityBytes": minimum_capacity_bytes,
        "device": drive_mount,
    }


def validate_created_work_root(runner, root, path_evidence):
    root = pathlib.Path(root)
    migration.validate_no_symlink_components(root)
    if root.is_symlink() or not root.is_dir():
        raise DrillError("created work root is not a real directory")
    observed = find_mount(runner, root.resolve(strict=True))
    if mount_identity(observed) != mount_identity(path_evidence["device"]):
        raise DrillError(
            "created work root escaped the exact 4 TB mount/device/source/UUID"
        )
    path_evidence["workRootDevice"] = observed
    return path_evidence


def validate_docker_identity(
    runner,
    *,
    expected_context,
    expected_daemon_id,
    environ=None,
    socket_path=DOCKER_SOCKET,
):
    environ = os.environ if environ is None else environ
    overrides = [
        name
        for name in ("DOCKER_HOST", "DOCKER_CONTEXT")
        if environ.get(name, "") != ""
    ]
    if overrides:
        raise DrillError(
            "Docker environment overrides are forbidden: "
            + ", ".join(overrides)
        )
    if not expected_context:
        raise DrillError("--expected-docker-context is required")
    if not expected_daemon_id:
        raise DrillError("--expected-daemon-id is required")

    active_context = runner.run(
        ["docker", "context", "show"],
        timeout=30,
    ).stdout.strip()
    if active_context != expected_context:
        raise DrillError(
            "Docker context mismatch: expected "
            f"{expected_context}, observed {active_context}"
        )

    context_output = runner.run(
        ["docker", "context", "inspect", expected_context],
        timeout=30,
    )
    try:
        contexts = json.loads(context_output.stdout)
        endpoint = contexts[0]["Endpoints"]["docker"]["Host"]
    except (IndexError, KeyError, TypeError, json.JSONDecodeError) as error:
        raise DrillError("could not read Docker context endpoint") from error
    if len(contexts) != 1:
        raise DrillError("Docker context inspection was ambiguous")
    if endpoint != DOCKER_ENDPOINT:
        raise DrillError(
            "Docker endpoint must be exactly "
            f"{DOCKER_ENDPOINT}, observed {endpoint}"
        )

    socket_path = pathlib.Path(socket_path)
    try:
        socket_metadata = socket_path.lstat()
    except FileNotFoundError as error:
        raise DrillError("the local Docker Unix socket is missing") from error
    if not stat.S_ISSOCK(socket_metadata.st_mode):
        raise DrillError("the local Docker endpoint is not a real Unix socket")

    info_output = runner.run(
        docker_engine_command("info", "--format", "{{json .ID}}"),
        timeout=30,
    )
    try:
        daemon_id = json.loads(info_output.stdout)
    except json.JSONDecodeError as error:
        raise DrillError("could not parse Docker daemon identity") from error
    if not isinstance(daemon_id, str) or not daemon_id:
        raise DrillError("Docker daemon identity is unavailable")
    if daemon_id != expected_daemon_id:
        raise DrillError(
            "Docker daemon identity mismatch: expected "
            f"{expected_daemon_id}, observed {daemon_id}"
        )
    return {
        "environmentOverridesAbsent": True,
        "expectedContext": expected_context,
        "activeContext": active_context,
        "endpoint": endpoint,
        "socketPath": str(socket_path),
        "socketType": "unix",
        "socketDevice": socket_metadata.st_dev,
        "socketInode": socket_metadata.st_ino,
        "expectedDaemonId": expected_daemon_id,
        "daemonId": daemon_id,
        "passed": True,
    }


def docker_volume_inventory(runner):
    completed = runner.run(
        docker_engine_command("volume", "ls", "--quiet"),
        timeout=30,
    )
    return sorted(
        {
            line.strip()
            for line in completed.stdout.splitlines()
            if line.strip()
        }
    )


def volume_delta_evidence(before, after):
    before = sorted(set(before))
    after = sorted(set(after))
    added = sorted(set(after) - set(before))
    removed = sorted(set(before) - set(after))
    return {
        "before": before,
        "after": after,
        "beforeCount": len(before),
        "afterCount": len(after),
        "beforeSha256": sha256_bytes(canonical_compact_bytes(before)),
        "afterSha256": sha256_bytes(canonical_compact_bytes(after)),
        "added": added,
        "removed": removed,
        "zeroDelta": not added and not removed,
    }


def require_zero_volume_delta(before, after):
    evidence = volume_delta_evidence(before, after)
    if not evidence["zeroDelta"]:
        raise DrillError(
            "Docker volume inventory changed during the drill: "
            f"added={evidence['added']}, removed={evidence['removed']}"
        )
    return evidence


def run_token(run_id):
    token = re.sub(r"[^a-z0-9]+", "-", run_id.lower()).strip("-")
    digest = hashlib.sha256(run_id.encode("utf-8")).hexdigest()[:10]
    token = token[:30].strip("-")
    return f"{token}-{digest}" if token else digest


def validate_run_id(value):
    if not re.fullmatch(r"[A-Za-z0-9][A-Za-z0-9_.-]{0,127}", value):
        raise DrillError(
            "run ID must be 1-128 safe alphanumeric/dot/dash/underscore "
            "characters"
        )
    return value


def container_name(token, role):
    value = f"fst-sgr-{token}-{role}"
    if len(value) > 63:
        value = f"fst-sgr-{hashlib.sha256(value.encode()).hexdigest()[:18]}-{role}"
    if not re.fullmatch(r"[a-z0-9][a-z0-9_.-]+", value):
        raise DrillError(f"unsafe container name: {value}")
    return value


def inspect_container(runner, name, *, allow_missing=False):
    completed = runner.run(
        docker_engine_command("inspect", name),
        timeout=30,
        check=False,
    )
    if completed.returncode != 0:
        if allow_missing:
            return None
        raise DrillError(f"could not inspect owned container {name}")
    values = json.loads(completed.stdout)
    if len(values) != 1:
        raise DrillError(f"unexpected inspect result for {name}")
    return values[0]


def owned_labels(run_id, role):
    return {
        "fst.tool": TOOL_ID,
        "fst.run": run_id,
        "fst.role": role,
    }


def label_arguments(run_id, role):
    result = []
    for key, value in owned_labels(run_id, role).items():
        result.extend(["--label", f"{key}={value}"])
    return result


def assert_owned_container(inspected, run_id, role):
    labels = (inspected.get("Config") or {}).get("Labels") or {}
    expected = owned_labels(run_id, role)
    if any(labels.get(key) != value for key, value in expected.items()):
        raise DrillError("refusing to remove a container not owned by this run")


def remove_owned_container(runner, name, run_id, role):
    inspected = inspect_container(runner, name, allow_missing=True)
    if inspected is None:
        return False
    assert_owned_container(inspected, run_id, role)
    runner.run(
        docker_engine_command("rm", "-f", "-v", name),
        timeout=120,
    )
    return True


def created_container_id(completed):
    value = completed.stdout.strip()
    if re.fullmatch(r"[0-9a-f]{12,64}", value):
        return value
    return None


def remove_created_container(
    runner,
    name,
    run_id,
    role,
    *,
    container_id=None,
):
    if container_id is not None:
        completed = runner.run(
            docker_engine_command("rm", "-f", "-v", container_id),
            timeout=120,
            check=False,
        )
        if completed.returncode == 0:
            return True
    return remove_owned_container(runner, name, run_id, role)


def validate_runtime_child(runtime_root, path, expected_name):
    runtime_root = pathlib.Path(runtime_root)
    path = pathlib.Path(path)
    migration.validate_no_symlink_components(runtime_root)
    if runtime_root.is_symlink() or not runtime_root.is_dir():
        raise DrillError("runtime root is not a real directory")
    if path.name != expected_name or path.parent != runtime_root:
        raise DrillError("runtime directory is outside its fixed owned path")
    if path.exists():
        migration.validate_no_symlink_components(path)
        if path.is_symlink() or not path.is_dir():
            raise DrillError("runtime directory is not a real directory")
    return path


def create_empty_runtime_directory(runtime_root, name):
    if not re.fullmatch(r"[a-z0-9][a-z0-9_.-]{0,127}", name):
        raise DrillError(f"unsafe runtime directory name: {name}")
    path = validate_runtime_child(
        runtime_root,
        pathlib.Path(runtime_root) / name,
        name,
    )
    if path.exists():
        raise DrillError(f"runtime directory already exists: {path}")
    path.mkdir(mode=0o700)
    migration.fsync_directory(path.parent)
    return path


def remove_empty_runtime_directory(runtime_root, path, expected_name):
    path = validate_runtime_child(runtime_root, path, expected_name)
    if not path.exists():
        return False
    if any(path.iterdir()):
        raise DrillError(f"runtime directory is unexpectedly nonempty: {path}")
    path.rmdir()
    migration.fsync_directory(path.parent)
    return True


def cleanup_owned_directory(
    runner,
    image,
    directory,
    runtime_root,
    work_root,
    run_id,
    token,
    role,
):
    directory = pathlib.Path(directory)
    if not directory.exists():
        return {
            "needed": False,
            "targetRemoved": True,
            "containerRemoved": True,
            "containerPgdataRemoved": True,
        }
    validate_runtime_child(runtime_root, directory, role)
    try:
        directory_is_empty = not any(directory.iterdir())
    except PermissionError:
        directory_is_empty = False
    if directory_is_empty:
        directory.rmdir()
        migration.fsync_directory(directory.parent)
        return {
            "needed": False,
            "targetRemoved": True,
            "containerRemoved": True,
            "containerPgdataRemoved": True,
        }
    cleanup_role = f"cleanup-{role}"
    name = container_name(token, cleanup_role)
    helper_name = f"{cleanup_role}-container-pgdata"
    helper_pgdata = create_empty_runtime_directory(runtime_root, helper_name)
    container_id = None
    inspected = None
    pgdata_bind = None
    completed = None
    error = None
    cleanup_error = None
    arguments = [
        *docker_engine_command("create", "--pull=never"),
        "--name",
        name,
        "--network",
        "none",
        "--read-only",
        "--user",
        "0:0",
        *label_arguments(run_id, cleanup_role),
        "-v",
        f"{directory}:/owned",
        "-v",
        f"{helper_pgdata}:/var/lib/postgresql/data",
        "--entrypoint",
        "sh",
        image,
        "-eu",
        "-c",
        (
            "find /owned -mindepth 1 -maxdepth 1 "
            "-exec rm -rf -- {} +; "
            f"chown {os.getuid()}:{os.getgid()} /owned; "
            "chmod 700 /owned"
        ),
    ]
    try:
        remove_owned_container(runner, name, run_id, cleanup_role)
        created = runner.run(arguments, timeout=120)
        container_id = created_container_id(created)
        inspected = inspect_container(runner, name)
        assert_owned_container(inspected, run_id, cleanup_role)
        assert_container_image(inspected, image, cleanup_role)
        pgdata_bind = validate_pgdata_bind(
            inspected,
            helper_pgdata,
            work_root,
            cleanup_role,
        )
        completed = runner.run(
            docker_engine_command("start", "-a", name),
            timeout=1800,
            check=False,
        )
        inspected_after = inspect_container(runner, name)
        exit_code = int(
            (inspected_after.get("State") or {}).get("ExitCode", -1)
        )
        if completed.returncode != 0 or exit_code != 0:
            raise DrillError(
                f"{cleanup_role} container failed with exit {exit_code}"
            )
    except Exception as caught:
        error = caught
    finally:
        try:
            remove_created_container(
                runner,
                name,
                run_id,
                cleanup_role,
                container_id=container_id,
            )
        except Exception as caught:
            cleanup_error = caught
        try:
            remove_empty_runtime_directory(
                runtime_root,
                helper_pgdata,
                helper_name,
            )
        except Exception as caught:
            if cleanup_error is None:
                cleanup_error = caught
    if error is not None:
        if cleanup_error is not None:
            raise DrillError(
                f"{cleanup_role} failed and cleanup also failed: "
                f"{cleanup_error}"
            ) from error
        raise error
    if cleanup_error is not None:
        raise cleanup_error
    if any(directory.iterdir()):
        raise DrillError(f"owned runtime directory was not emptied: {directory}")
    directory.rmdir()
    migration.fsync_directory(directory.parent)
    return {
        "needed": True,
        "targetRemoved": not directory.exists(),
        "container": name,
        "containerRemoved": (
            inspect_container(runner, name, allow_missing=True) is None
        ),
        "containerPgdata": pgdata_bind,
        "containerPgdataRemoved": not helper_pgdata.exists(),
    }


def wait_for_postgres(runner, container):
    migration.wait_for_postgres(
        runner,
        container,
        POSTGRES_USER,
        POSTGRES_DATABASE,
        timeout=120,
    )


def local_image_evidence(runner, image):
    completed = runner.run(
        docker_engine_command("image", "inspect", image),
        timeout=30,
        check=False,
    )
    if completed.returncode != 0:
        raise DrillError(
            f"{image} must already exist locally; the drill never pulls images"
        )
    try:
        values = json.loads(completed.stdout)
    except json.JSONDecodeError as error:
        raise DrillError("could not parse local image inspection") from error
    if len(values) != 1:
        raise DrillError("unexpected local image inspection result")
    value = values[0]
    image_id = value.get("Id")
    if not isinstance(image_id, str) or not IMAGE_ID_PATTERN.fullmatch(image_id):
        raise DrillError("local image ID is not an immutable sha256 ID")
    return {
        "requested": image,
        "id": image_id,
        "immutableId": image_id,
        "repoDigests": sorted(value.get("RepoDigests") or []),
    }


def docker_identity_snapshot(evidence):
    return {
        key: evidence.get(key)
        for key in (
            "activeContext",
            "endpoint",
            "socketPath",
            "socketType",
            "socketDevice",
            "socketInode",
            "daemonId",
        )
    }


def revalidate_terminal_docker_and_image(
    runner,
    *,
    initial_docker,
    initial_image,
    expected_context,
    expected_daemon_id,
    environ=None,
    socket_path=DOCKER_SOCKET,
):
    final_docker = validate_docker_identity(
        runner,
        expected_context=expected_context,
        expected_daemon_id=expected_daemon_id,
        environ=environ,
        socket_path=socket_path,
    )
    initial_identity = docker_identity_snapshot(initial_docker)
    final_identity = docker_identity_snapshot(final_docker)
    if final_identity != initial_identity:
        raise DrillError(
            "Docker context/socket/daemon identity drifted before acceptance"
        )

    requested = initial_image.get("requested")
    immutable_id = initial_image.get("immutableId")
    if not isinstance(requested, str) or not requested:
        raise DrillError("initial requested image reference is unavailable")
    if (
        not isinstance(immutable_id, str)
        or not IMAGE_ID_PATTERN.fullmatch(immutable_id)
    ):
        raise DrillError("initial immutable image ID is invalid")
    final_requested = local_image_evidence(runner, requested)
    if final_requested["immutableId"] != immutable_id:
        raise DrillError(
            "requested local image resolves to a different immutable ID"
        )
    final_immutable = local_image_evidence(runner, immutable_id)
    if final_immutable["immutableId"] != immutable_id:
        raise DrillError("immutable local image ID is no longer present")
    return {
        "docker": {
            "initial": initial_docker,
            "final": final_docker,
            "exactMatch": True,
        },
        "image": {
            "initial": initial_image,
            "finalRequestedReference": final_requested,
            "finalImmutableReference": final_immutable,
            "exactMatch": True,
        },
        "passed": True,
    }


def assert_container_image(inspected, image_id, role):
    if inspected.get("Image") != image_id:
        raise DrillError(
            f"{role} container does not use the authorized immutable image ID"
        )
    return True


def start_postgres(
    runner,
    *,
    container,
    pgdata,
    work_root,
    image,
    run_id,
    role,
    archive_mount=None,
):
    remove_owned_container(runner, container, run_id, role)
    arguments = [
        *docker_engine_command("run", "--pull=never"),
        "--name",
        container,
        "--detach",
        "--network",
        "none",
        "--shm-size",
        "256m",
        *label_arguments(run_id, role),
        "-e",
        "POSTGRES_HOST_AUTH_METHOD=trust",
        "-e",
        f"POSTGRES_USER={POSTGRES_USER}",
        "-e",
        f"POSTGRES_DB={POSTGRES_DATABASE}",
        "-v",
        f"{pgdata}:/var/lib/postgresql/data",
    ]
    if archive_mount is not None:
        arguments.extend(["-v", f"{archive_mount}:/archive:ro"])
    arguments.append(image)
    runner.run(arguments, timeout=120)
    wait_for_postgres(runner, container)
    inspected = inspect_container(runner, container)
    assert_owned_container(inspected, run_id, role)
    assert_container_image(inspected, image, role)
    if (inspected.get("HostConfig") or {}).get("NetworkMode") != "none":
        raise DrillError(f"{role} PostgreSQL container is not network-none")
    for mount in inspected.get("Mounts") or []:
        if mount.get("Destination") == "/var/run/docker.sock":
            raise DrillError(f"{role} container unexpectedly has Docker socket")
    pgdata_bind = validate_pgdata_bind(
        inspected,
        pgdata,
        work_root,
        role,
    )
    return inspected, pgdata_bind


def database_for(runner, container):
    return migration.Database(
        runner,
        container,
        POSTGRES_USER,
        POSTGRES_DATABASE,
    )


def qualified(name):
    return migration.qualified(name)


def child_names(target):
    return {
        snapshot_id: migration.generation_child_name(target, snapshot_id)
        for snapshot_id in FIXTURE_SNAPSHOT_IDS
    }


def fixture_sql(target, row_counts):
    children = child_names(target)
    default_child = migration.default_child_name(target)
    instrument = migration.sql_literal(target.instrument)
    rows = [
        (
            TARGET_SNAPSHOT_ID,
            "retired-song",
            row_counts["target"],
        ),
        (
            PREVIOUS_SNAPSHOT_ID,
            "previous-song",
            row_counts["previous"],
        ),
        (
            CURRENT_SNAPSHOT_ID,
            "current-song",
            row_counts["current"],
        ),
        (
            WORKING_SNAPSHOT_ID,
            "working-song",
            row_counts["working"],
        ),
    ]
    values = ",\n".join(
        (
            f"({snapshot_id}::bigint, "
            f"{migration.sql_literal(prefix)}, {count})"
        )
        for snapshot_id, prefix, count in rows
    )
    leaf_ddl = "\n".join(
        (
            f"CREATE TABLE {qualified(name)} "
            f"PARTITION OF {qualified(target.partition)} "
            f"FOR VALUES IN ({snapshot_id});"
        )
        for snapshot_id, name in children.items()
    )
    return f"""
        CREATE TABLE scrape_log (
            id bigint PRIMARY KEY,
            started_at timestamptz NOT NULL,
            completed_at timestamptz,
            status text NOT NULL);
        INSERT INTO scrape_log VALUES
            ({TARGET_SNAPSHOT_ID}, '2026-08-20T00:00:00Z',
             '2026-08-20T00:10:00Z', 'failed'),
            ({PREVIOUS_SNAPSHOT_ID}, '2026-08-21T00:00:00Z',
             '2026-08-21T00:10:00Z', 'completed'),
            ({CURRENT_SNAPSHOT_ID}, '2026-08-22T00:00:00Z',
             '2026-08-22T00:10:00Z', 'completed'),
            ({WORKING_SNAPSHOT_ID}, '2026-08-23T00:00:00Z',
             NULL, 'running');

        CREATE TABLE publication_generations (
            publication_id bigint PRIMARY KEY,
            scrape_id bigint UNIQUE NOT NULL,
            status text NOT NULL);
        INSERT INTO publication_generations VALUES
            (201, {PREVIOUS_SNAPSHOT_ID}, 'previous'),
            (202, {CURRENT_SNAPSHOT_ID}, 'current'),
            (203, {WORKING_SNAPSHOT_ID}, 'working');

        CREATE TABLE scrape_publication_state (
            id boolean PRIMARY KEY CHECK (id),
            published_scrape_id bigint,
            current_publication_id bigint,
            previous_publication_id bigint,
            working_publication_id bigint,
            public_reads_frozen boolean NOT NULL,
            public_reads_frozen_reason text,
            updated_at timestamptz NOT NULL);
        INSERT INTO scrape_publication_state VALUES (
            true, {CURRENT_SNAPSHOT_ID}, 202, 201, 203, true,
            'synthetic working publication',
            '2026-08-23T00:05:00Z');

        CREATE TABLE leaderboard_snapshot_state (
            song_id text NOT NULL,
            instrument text NOT NULL,
            active_snapshot_id bigint,
            scrape_id bigint,
            is_finalized boolean NOT NULL DEFAULT false,
            updated_at timestamptz NOT NULL,
            PRIMARY KEY (song_id, instrument));
        INSERT INTO leaderboard_snapshot_state VALUES
            ('current-song-000001', {instrument},
             {CURRENT_SNAPSHOT_ID}, {CURRENT_SNAPSHOT_ID}, true,
             '2026-08-23T00:05:00Z'),
            ('working-song-000001', {instrument},
             {WORKING_SNAPSHOT_ID}, {WORKING_SNAPSHOT_ID}, false,
             '2026-08-23T00:05:00Z'),
            ('empty-song', {instrument},
             {CURRENT_SNAPSHOT_ID}, {CURRENT_SNAPSHOT_ID}, true,
             '2026-08-23T00:05:00Z');

        CREATE TABLE solo_current_projection_scope (
            song_id text NOT NULL,
            instrument text NOT NULL,
            projection_generation bigint NOT NULL DEFAULT 0,
            row_count bigint NOT NULL DEFAULT 0,
            source_snapshot_id bigint,
            source_kind text NOT NULL DEFAULT 'snapshot',
            status text NOT NULL DEFAULT 'ready',
            updated_at timestamptz NOT NULL,
            PRIMARY KEY (song_id, instrument));
        INSERT INTO solo_current_projection_scope VALUES
            ('previous-song-000001', {instrument},
             {PREVIOUS_SNAPSHOT_ID}, {row_counts["previous"]},
             {PREVIOUS_SNAPSHOT_ID}, 'snapshot', 'ready',
             '2026-08-23T00:05:00Z'),
            ('current-song-000001', {instrument},
             {CURRENT_SNAPSHOT_ID}, {row_counts["current"]},
             {CURRENT_SNAPSHOT_ID}, 'snapshot', 'ready',
             '2026-08-23T00:05:00Z'),
            ('working-song-000001', {instrument},
             {WORKING_SNAPSHOT_ID}, {row_counts["working"]},
             {WORKING_SNAPSHOT_ID}, 'snapshot', 'building',
             '2026-08-23T00:05:00Z'),
            ('empty-song', {instrument},
             {CURRENT_SNAPSHOT_ID}, 0,
             {CURRENT_SNAPSHOT_ID}, 'snapshot', 'ready',
             '2026-08-23T00:05:00Z');

        CREATE TABLE leaderboard_published_scope_source (
            published_scrape_id bigint NOT NULL,
            song_id text NOT NULL,
            instrument text NOT NULL,
            scope_kind text NOT NULL,
            source_kind text NOT NULL,
            source_snapshot_id bigint,
            source_scrape_id bigint NOT NULL,
            row_count bigint NOT NULL,
            content_fingerprint text NOT NULL,
            coverage_fingerprint text NOT NULL,
            reported_total_entries bigint NOT NULL,
            reported_total_pages integer NOT NULL,
            is_complete boolean NOT NULL,
            created_at timestamptz NOT NULL,
            validated_at timestamptz NOT NULL,
            PRIMARY KEY (
                published_scrape_id, instrument, song_id, scope_kind));
        INSERT INTO leaderboard_published_scope_source VALUES
            ({PREVIOUS_SNAPSHOT_ID}, 'previous-song-000001', {instrument},
             'alltime', 'snapshot', {PREVIOUS_SNAPSHOT_ID},
             {PREVIOUS_SNAPSHOT_ID}, {row_counts["previous"]},
             'previous-content', 'previous-coverage',
             {row_counts["previous"]}, 1, true,
             '2026-08-23T00:05:00Z', '2026-08-23T00:05:00Z'),
            ({CURRENT_SNAPSHOT_ID}, 'current-song-000001', {instrument},
             'alltime', 'snapshot', {CURRENT_SNAPSHOT_ID},
             {CURRENT_SNAPSHOT_ID}, {row_counts["current"]},
             'current-content', 'current-coverage',
             {row_counts["current"]}, 1, true,
             '2026-08-23T00:05:00Z', '2026-08-23T00:05:00Z'),
            ({CURRENT_SNAPSHOT_ID}, 'empty-song', {instrument},
             'alltime', 'empty', NULL, {CURRENT_SNAPSHOT_ID}, 0,
             'empty-content', 'empty-coverage', 0, 0, true,
             '2026-08-23T00:05:00Z', '2026-08-23T00:05:00Z'),
            ({WORKING_SNAPSHOT_ID}, 'working-song-000001', {instrument},
             'alltime', 'snapshot', {WORKING_SNAPSHOT_ID},
             {WORKING_SNAPSHOT_ID}, {row_counts["working"]},
             'working-content', 'working-coverage',
             {row_counts["working"]}, 1, true,
             '2026-08-23T00:05:00Z', '2026-08-23T00:05:00Z');

        CREATE TABLE {qualified(migration.TARGET_PARENT)} (
            snapshot_id bigint NOT NULL,
            song_id text NOT NULL,
            instrument text NOT NULL,
            account_id text NOT NULL,
            score integer NOT NULL,
            accuracy integer,
            is_full_combo boolean,
            stars integer,
            season integer,
            percentile real,
            rank integer DEFAULT 0,
            source text NOT NULL DEFAULT 'scrape',
            difficulty integer DEFAULT -1,
            api_rank integer,
            end_time text,
            band_members_json jsonb,
            band_score integer,
            base_score integer,
            instrument_bonus integer,
            overdrive_bonus integer,
            instrument_combo text,
            first_seen_at timestamptz NOT NULL,
            last_updated_at timestamptz NOT NULL,
            PRIMARY KEY (
                snapshot_id, song_id, instrument, account_id)
        ) PARTITION BY LIST (instrument);

        CREATE TABLE {qualified(target.partition)}
            PARTITION OF {qualified(migration.TARGET_PARENT)}
            FOR VALUES IN ({instrument})
            PARTITION BY LIST (snapshot_id);

        {leaf_ddl}

        CREATE TABLE {qualified(default_child)}
            PARTITION OF {qualified(target.partition)} DEFAULT;

        CREATE INDEX ix_les_snapshot_song_score
            ON {qualified(migration.TARGET_PARENT)}
            (snapshot_id, song_id, instrument, score DESC);

        INSERT INTO {qualified(migration.TARGET_PARENT)} (
            snapshot_id, song_id, instrument, account_id,
            score, accuracy, is_full_combo, stars, season,
            percentile, rank, source, difficulty, api_rank,
            end_time, band_members_json, band_score, base_score,
            instrument_bonus, overdrive_bonus, instrument_combo,
            first_seen_at, last_updated_at)
        SELECT
            input.snapshot_id,
            input.song_prefix || '-' || lpad(series::text, 6, '0'),
            {instrument},
            'account-' || input.snapshot_id || '-' ||
                lpad(series::text, 6, '0'),
            1500000000 - series,
            900000 + (series % 100000),
            series % 11 = 0,
            5 + (series % 2),
            34,
            (series % 1000)::real / 1000,
            series,
            'synthetic-retention-drill',
            3,
            series,
            NULL,
            CASE WHEN series % 29 = 0
                 THEN jsonb_build_array('member-a', 'member-b')
                 ELSE NULL END,
            NULL, NULL, NULL, NULL, NULL,
            timestamptz '2026-08-20T00:00:00Z' +
                series * interval '1 millisecond',
            timestamptz '2026-08-23T00:00:00Z' +
                series * interval '1 millisecond'
        FROM (
            VALUES
                {values}
        ) input(snapshot_id, song_prefix, row_count)
        CROSS JOIN LATERAL generate_series(1, input.row_count) series;

        ANALYZE {qualified(target.partition)};
        SELECT pg_stat_force_next_flush();
        CHECKPOINT;
    """


def relation_catalog_bundle(database, relation_names):
    return {
        relation_name: database.json(migration.catalog_query(relation_name))
        for relation_name in relation_names
    }


def relation_fingerprint(database, relation_name, predicate="TRUE"):
    return database.json(
        migration.fingerprint_sql(relation_name, predicate),
        timeout=600,
    )


def relation_distribution(database, relation_name, predicate="TRUE"):
    return migration.normalized_distribution(
        database.json(
            migration.snapshot_distribution_query(
                relation_name,
                predicate,
            ),
            timeout=600,
        )
    )


def scalar_int(database, sql):
    value = database.scalar(sql)
    if not re.fullmatch(r"-?[0-9]+", value):
        raise DrillError(f"database did not return an integer: {value}")
    return int(value)


def default_row_count(database, default_child):
    return scalar_int(
        database,
        f"SELECT COUNT(*) FROM {qualified(default_child)}",
    )


def candidate_snapshot_ids(inventory_ids, protected_ids, *, default_rows):
    if int(default_rows) != 0:
        raise DrillError("default child is nonempty; no candidate is safe")
    inventory = {
        migration.ensure_integer(value, "inventory snapshot ID", 1)
        for value in inventory_ids
    }
    protected = {
        migration.ensure_integer(value, "protected snapshot ID", 1)
        for value in protected_ids
    }
    if not protected.issubset(inventory):
        raise DrillError("protected IDs are absent from the fixture inventory")
    return sorted(inventory - protected)


def selected_archive_relations(target):
    children = child_names(target)
    return (
        migration.TARGET_PARENT,
        target.partition,
        children[TARGET_SNAPSHOT_ID],
        migration.default_child_name(target),
    )


def pg_dump_command(container, target):
    command = docker_engine_command(
        "exec",
        "-e",
        "PGCONNECT_TIMEOUT=10",
        "-e",
        (
            "PGOPTIONS=-c row_security=off "
            "-c application_name=fst-snapshot-retention-drill"
        ),
        container,
        "pg_dump",
        "-Fc",
        "--compress=6",
        "--no-owner",
        "--no-privileges",
        "--strict-names",
        "-U",
        POSTGRES_USER,
        "-d",
        POSTGRES_DATABASE,
    )
    for relation in selected_archive_relations(target):
        command.extend(["--table", f"public.{relation}"])
    return command


def archive_catalog_names(catalogs):
    names = set(catalogs)
    for catalog in catalogs.values():
        names.update(item["name"] for item in catalog["constraints"])
        names.update(item["name"] for item in catalog["indexes"])
    return names


def verify_archive_toc(toc, target, catalogs):
    selected = set(selected_archive_relations(target))
    required = [
        f"TABLE public {relation}"
        for relation in selected
    ]
    required.extend(
        [
            (
                "TABLE DATA public "
                f"{migration.generation_child_name(target, TARGET_SNAPSHOT_ID)}"
            ),
            (
                "TABLE DATA public "
                f"{migration.default_child_name(target)}"
            ),
        ]
    )
    missing = [token for token in required if token not in toc]
    if missing:
        raise DrillError(
            "archive TOC lacks required selected objects: "
            + ", ".join(sorted(missing))
        )

    for other in TARGETS:
        if other != target and other.partition in toc:
            raise DrillError(
                "archive TOC is contaminated by another instrument root"
            )

    allowed_names = archive_catalog_names(catalogs)
    public_entry = re.compile(
        r"^\d+;\s+\d+\s+\d+\s+"
        r"(?:TABLE|TABLE DATA|TABLE ATTACH|CONSTRAINT|INDEX|INDEX ATTACH)"
        r"\s+public\s+(.+?)\s+\S+\s*$"
    )
    for line in toc.splitlines():
        match = public_entry.match(line.strip())
        if not match:
            continue
        fields = match.group(1).split()
        if not fields:
            continue
        if not any(field in allowed_names for field in fields):
            raise DrillError(
                f"archive TOC public object is outside the allowlist: {line}"
            )
    return True


def container_isolation_evidence(inspected):
    host = inspected.get("HostConfig") or {}
    mounts = []
    for mount in inspected.get("Mounts") or []:
        mounts.append(
            {
                "destination": mount.get("Destination"),
                "source": mount.get("Source"),
                "readWrite": mount.get("RW"),
                "type": mount.get("Type"),
            }
        )
    return {
        "networkMode": host.get("NetworkMode"),
        "readOnlyRootfs": bool(host.get("ReadonlyRootfs")),
        "user": (inspected.get("Config") or {}).get("User") or "image-default",
        "mounts": sorted(mounts, key=lambda value: value["destination"] or ""),
        "dockerSocketMounted": any(
            mount["destination"] == "/var/run/docker.sock"
            for mount in mounts
        ),
    }


def validate_pgdata_bind(inspected, pgdata, work_root, role):
    pgdata = pathlib.Path(pgdata)
    work_root = pathlib.Path(work_root)
    migration.validate_no_symlink_components(pgdata)
    migration.validate_no_symlink_components(work_root)
    resolved_pgdata = pgdata.resolve(strict=True)
    resolved_work_root = work_root.resolve(strict=True)
    if not migration.path_is_beneath(resolved_pgdata, resolved_work_root):
        raise DrillError(f"{role} PGDATA is outside the fixed work root")

    mounts = inspected.get("Mounts") or []
    volume_mounts = [
        mount for mount in mounts if mount.get("Type") == "volume"
    ]
    if volume_mounts:
        raise DrillError(f"{role} container has an anonymous Docker volume")
    pgdata_mounts = [
        mount
        for mount in mounts
        if mount.get("Destination") == "/var/lib/postgresql/data"
    ]
    if len(pgdata_mounts) != 1:
        raise DrillError(f"{role} container must have one exact PGDATA mount")
    mount = pgdata_mounts[0]
    source = pathlib.Path(mount.get("Source", "")).resolve(strict=False)
    if (
        mount.get("Type") != "bind"
        or source != resolved_pgdata
        or mount.get("RW") is not True
    ):
        raise DrillError(
            f"{role} PGDATA must be the exact read-write bind mount"
        )
    return {
        "destination": "/var/lib/postgresql/data",
        "source": str(resolved_pgdata),
        "type": "bind",
        "readWrite": True,
        "beneathWorkRoot": True,
        "anonymousVolumes": 0,
    }


def prove_restore_isolation(runner, container):
    completed = runner.run(
        docker_engine_command(
            "exec",
            container,
            "sh",
            "-eu",
            "-c",
            (
                "test ! -e /var/run/docker.sock; "
                "awk -F: 'NR > 2 {gsub(/ /, \"\", $1); "
                "if ($1 != \"lo\" && $1 != \"\") exit 9} "
                "END {print \"network-none-no-docker-socket\"}' "
                "/proc/net/dev"
            ),
        ),
        timeout=30,
    )
    return completed.stdout.strip()


def validate_catalog_parity(source, restored):
    if migration.catalog_semantic_shape(source) != (
        migration.catalog_semantic_shape(restored)
    ):
        raise DrillError("restored catalog semantic shape differs from source")


def run_toc_container(
    runner,
    *,
    image,
    run_id,
    token,
    archive_root,
    archive_path,
    runtime_root,
    work_root,
):
    role = "toc"
    name = container_name(token, role)
    pgdata_name = f"{role}-pgdata"
    pgdata = create_empty_runtime_directory(runtime_root, pgdata_name)
    container_id = None
    inspected = None
    pgdata_bind = None
    completed = None
    exit_code = None
    error = None
    cleanup_error = None
    try:
        remove_owned_container(runner, name, run_id, role)
        created = runner.run(
            [
                *docker_engine_command("create", "--pull=never"),
                "--name",
                name,
                "--network",
                "none",
                "--read-only",
                "--user",
                f"{os.getuid()}:{os.getgid()}",
                *label_arguments(run_id, role),
                "-v",
                f"{pgdata}:/var/lib/postgresql/data",
                "-v",
                f"{archive_root}:/archive:ro",
                "--entrypoint",
                "pg_restore",
                image,
                "-l",
                f"/archive/{archive_path.name}",
            ],
            timeout=120,
        )
        container_id = created_container_id(created)
        inspected = inspect_container(runner, name)
        assert_owned_container(inspected, run_id, role)
        assert_container_image(inspected, image, role)
        pgdata_bind = validate_pgdata_bind(
            inspected,
            pgdata,
            work_root,
            role,
        )
        completed = runner.run(
            docker_engine_command("start", "-a", name),
            timeout=120,
            check=False,
        )
        inspected_after = inspect_container(runner, name)
        exit_code = int(
            (inspected_after.get("State") or {}).get("ExitCode", -1)
        )
        if completed.returncode != 0 or exit_code != 0:
            raise DrillError(f"TOC container failed with exit {exit_code}")
    except Exception as caught:
        error = caught
    finally:
        try:
            remove_created_container(
                runner,
                name,
                run_id,
                role,
                container_id=container_id,
            )
        except Exception as caught:
            cleanup_error = caught
        try:
            remove_empty_runtime_directory(
                runtime_root,
                pgdata,
                pgdata_name,
            )
        except Exception as caught:
            if cleanup_error is None:
                cleanup_error = caught
    if error is not None:
        if cleanup_error is not None:
            raise DrillError(
                f"TOC execution failed and cleanup also failed: {cleanup_error}"
            ) from error
        raise error
    if cleanup_error is not None:
        raise cleanup_error
    removed = inspect_container(runner, name, allow_missing=True) is None
    pgdata_removed = not pgdata.exists()
    if not removed or not pgdata_removed:
        raise DrillError("TOC container or controlled PGDATA remains")
    return {
        "stdout": completed.stdout,
        "container": name,
        "exitCode": exit_code,
        "isolation": {
            **container_isolation_evidence(inspected),
            "pgdataBind": pgdata_bind,
        },
        "removed": removed,
        "pgdataRemoved": pgdata_removed,
    }


def archive_and_restore(
    runner,
    *,
    root,
    target,
    source_container,
    source_database,
    image,
    run_id,
    token,
    source_pgdata,
):
    archive_root = root / "evidence" / "archive"
    restore_root = root / "evidence" / "restore"
    runtime_root = root / "runtime"
    archive_root.mkdir(parents=True, mode=0o700)
    restore_root.mkdir(parents=True, mode=0o700)
    runtime_root.mkdir(parents=True, exist_ok=True, mode=0o700)
    archive_path = archive_root / "target-generation.custom"
    toc_path = archive_root / "target-generation.list"
    selected = selected_archive_relations(target)
    catalogs = relation_catalog_bundle(source_database, selected)
    target_child = migration.generation_child_name(
        target,
        TARGET_SNAPSHOT_ID,
    )
    default_child = migration.default_child_name(target)
    source_fingerprint = relation_fingerprint(
        source_database,
        target_child,
    )
    source_distribution = relation_distribution(
        source_database,
        target_child,
    )
    source_default_rows = default_row_count(source_database, default_child)
    source_state = source_database.json(
        migration.relation_state_query(target_child)
    )
    before_fence = migration.source_fence_from_state(source_state)

    started = time.monotonic()
    runner.run_to_file(
        pg_dump_command(source_container, target),
        archive_path,
        timeout=1800,
    )
    if archive_path.stat().st_size <= 0:
        raise DrillError("custom archive is empty")

    toc_run = run_toc_container(
        runner,
        image=image,
        run_id=run_id,
        token=token,
        archive_root=archive_root,
        archive_path=archive_path,
        runtime_root=runtime_root,
        work_root=root,
    )
    atomic_publish_bytes(toc_path, toc_run["stdout"].encode("utf-8"))
    verify_archive_toc(toc_run["stdout"], target, catalogs)

    post_state = source_database.json(
        migration.relation_state_query(target_child)
    )
    after_fence = migration.source_fence_from_state(post_state)
    if not migration.source_fence_matches(before_fence, after_fence):
        raise DrillError("source fence drifted while pg_dump streamed")

    archive_manifest = write_integrity_json(
        archive_root / "archive-manifest.json",
        {
            "formatVersion": FORMAT_VERSION,
            "toolId": TOOL_ID,
            "createdAtUtc": utc_now(),
            "selectedRelations": [
                f"public.{relation}" for relation in selected
            ],
            "archive": {
                "path": str(archive_path),
                "bytes": archive_path.stat().st_size,
                "sha256": sha256_path(archive_path),
                "format": "PostgreSQL custom",
                "compression": 6,
                "elapsedSeconds": round(time.monotonic() - started, 6),
            },
            "toc": {
                "path": str(toc_path),
                "bytes": toc_path.stat().st_size,
                "sha256": sha256_path(toc_path),
                "allowlistPassed": True,
                "containerRun": {
                    key: value
                    for key, value in toc_run.items()
                    if key != "stdout"
                },
            },
            "sourceFenceBefore": before_fence,
            "sourceFenceAfter": after_fence,
            "sourceChangedDuringArchive": False,
        },
    )

    restore_pgdata = create_empty_runtime_directory(
        runtime_root,
        "restore-pgdata",
    )
    restore_role = "restore"
    restore_container = container_name(token, restore_role)
    restored_database = None
    restore_started = time.monotonic()
    restored = {}
    inspected = None
    isolation_probe = None
    restore_cleanup_container = None
    try:
        inspected, restore_pgdata_bind = start_postgres(
            runner,
            container=restore_container,
            pgdata=restore_pgdata,
            work_root=root,
            image=image,
            run_id=run_id,
            role=restore_role,
            archive_mount=archive_root,
        )
        isolation_probe = prove_restore_isolation(runner, restore_container)
        runner.run(
            docker_engine_command(
                "exec",
                restore_container,
                "pg_restore",
                "--exit-on-error",
                "--no-owner",
                "--no-privileges",
                "-U",
                POSTGRES_USER,
                "-d",
                POSTGRES_DATABASE,
                f"/archive/{archive_path.name}",
            ),
            timeout=1800,
        )
        restored_database = database_for(runner, restore_container)
        restored_catalogs = relation_catalog_bundle(
            restored_database,
            selected,
        )
        for relation in selected:
            validate_catalog_parity(catalogs[relation], restored_catalogs[relation])

        restored_fingerprint = relation_fingerprint(
            restored_database,
            target_child,
        )
        restored_distribution = relation_distribution(
            restored_database,
            target_child,
        )
        restored_default_rows = default_row_count(
            restored_database,
            default_child,
        )
        if restored_fingerprint != source_fingerprint:
            raise DrillError("restored leaf fingerprint differs from source")
        if restored_distribution != source_distribution:
            raise DrillError("restored leaf distribution differs from source")
        if source_default_rows != 0 or restored_default_rows != 0:
            raise DrillError("source/restored default child is not empty")
        restored_row_count = migration.ensure_integer(
            restored_fingerprint.get("rowCount"),
            "restored row count",
            1,
        )
        if restored_row_count != source_fingerprint["rowCount"]:
            raise DrillError("restored exact row count differs from source")
        pgdata_output = runner.run(
            docker_engine_command(
                "exec",
                restore_container,
                "du",
                "-sb",
                "/var/lib/postgresql/data",
            ),
            timeout=120,
        ).stdout.split()
        if not pgdata_output or not pgdata_output[0].isdigit():
            raise DrillError("could not measure isolated restore PGDATA")
        pgdata_bytes = int(pgdata_output[0])
        restored = {
            "exactRowCount": restored_row_count,
            "fingerprint": restored_fingerprint,
            "distribution": restored_distribution,
            "catalogs": restored_catalogs,
            "defaultRows": restored_default_rows,
            "pgdataBytes": pgdata_bytes,
        }
    finally:
        remove_owned_container(
            runner,
            restore_container,
            run_id,
            restore_role,
        )
        restore_cleanup_container = cleanup_owned_directory(
            runner,
            image,
            restore_pgdata,
            runtime_root,
            root,
            run_id,
            token,
            "restore-pgdata",
        )

    cleanup = {
        "containerRemoved": (
            inspect_container(
                runner,
                restore_container,
                allow_missing=True,
            )
            is None
        ),
        "restorePgdataRemoved": not restore_pgdata.exists(),
        "sourcePgdataStillPresent": source_pgdata.is_dir(),
        "archiveRetained": (
            archive_path.is_file()
            and sha256_path(archive_path)
            == archive_manifest["archive"]["sha256"]
        ),
        "cleanupContainer": restore_cleanup_container,
    }
    if not all(
        cleanup[key]
        for key in (
            "containerRemoved",
            "restorePgdataRemoved",
            "sourcePgdataStillPresent",
            "archiveRetained",
        )
    ):
        raise DrillError("restore cleanup evidence is incomplete")

    restore_report = write_integrity_json(
        restore_root / "restore-proof.json",
        {
            "formatVersion": FORMAT_VERSION,
            "toolId": TOOL_ID,
            "completedAtUtc": utc_now(),
            "postgresMajor": POSTGRES_MAJOR,
            "archiveSha256": archive_manifest["archive"]["sha256"],
            "source": {
                "exactRowCount": source_fingerprint["rowCount"],
                "fingerprint": source_fingerprint,
                "distribution": source_distribution,
                "catalogs": catalogs,
                "defaultRows": source_default_rows,
            },
            "restored": restored,
            "parity": {
                "exactRows": True,
                "fingerprint": True,
                "distribution": True,
                "columnsConstraintsIndexesBoundsTablespace": True,
                "defaultEmpty": True,
            },
            "isolation": {
                **container_isolation_evidence(inspected),
                "pgdataBind": restore_pgdata_bind,
                "insideProbe": isolation_probe,
                "networkDependency": False,
                "credentialDependency": False,
            },
            "cleanup": cleanup,
            "elapsedSeconds": round(time.monotonic() - restore_started, 6),
        },
    )
    return {
        "archivePath": archive_path,
        "archiveManifest": archive_manifest,
        "restoreReport": restore_report,
        "sourceCatalogs": catalogs,
        "sourceFingerprint": source_fingerprint,
        "sourceDistribution": source_distribution,
    }


PROVER_SOURCE = r'''#!/usr/bin/perl
use strict;
use warnings;
use Digest::SHA qw(sha256_hex);
use Fcntl qw(:DEFAULT);
use IO::Handle;
use JSON::PP;
use File::Basename qw(dirname basename);

sub read_bytes {
    my ($path) = @_;
    open(my $handle, '<:raw', $path) or die "read $path: $!\n";
    local $/;
    my $value = <$handle>;
    close($handle) or die "close $path: $!\n";
    return $value;
}

sub canonical {
    my ($value) = @_;
    return JSON::PP->new->canonical(1)->utf8(1)->encode($value) . "\n";
}

sub integrity {
    my ($value) = @_;
    my %copy = %{$value};
    delete $copy{integritySha256};
    return sha256_hex(canonical(\%copy));
}

sub sync_dir {
    my ($path) = @_;
    sysopen(my $directory, $path, O_RDONLY)
        or die "open directory $path: $!\n";
    $directory->sync or die "sync directory $path: $!\n";
    close($directory) or die "close directory $path: $!\n";
}

sub atomic_write {
    my ($path, $value) = @_;
    my $directory = dirname($path);
    my $temporary = $directory . '/.' . basename($path)
        . '.partial-' . $$ . '-' . time();
    sysopen(my $handle, $temporary, O_WRONLY | O_CREAT | O_EXCL, 0600)
        or die "create $temporary: $!\n";
    binmode($handle);
    print {$handle} $value or die "write $temporary: $!\n";
    $handle->flush or die "flush $temporary: $!\n";
    $handle->sync or die "sync $temporary: $!\n";
    close($handle) or die "close $temporary: $!\n";
    rename($temporary, $path) or die "rename $temporary: $!\n";
    sync_dir($directory);
    chmod(0444, $path) or die "chmod $path: $!\n";
}

sub sha_file {
    my ($path) = @_;
    open(my $handle, '<:raw', $path) or die "read archive $path: $!\n";
    my $digest = Digest::SHA->new(256);
    $digest->addfile($handle);
    close($handle) or die "close archive $path: $!\n";
    return $digest->hexdigest;
}

sub writable_probe {
    my ($directory) = @_;
    my $path = $directory . '/.fst-write-probe-' . $$;
    if (sysopen(my $handle, $path, O_WRONLY | O_CREAT | O_EXCL, 0600)) {
        close($handle);
        unlink($path);
        return JSON::PP::true;
    }
    return JSON::PP::false;
}

sub network_interfaces {
    open(my $handle, '<', '/proc/net/dev') or die "read network state: $!\n";
    my @interfaces;
    while (my $line = <$handle>) {
        next unless $line =~ /^\s*([^:]+):/;
        my $name = $1;
        $name =~ s/\s+//g;
        push @interfaces, $name if $name ne '';
    }
    close($handle);
    return \@interfaces;
}

my ($request_path, $archive_path, $proof_path, $reject_path, $expected_token)
    = @ARGV;
die "invalid prover arguments\n" unless defined $expected_token;

if (!-f $request_path) {
    print canonical({status => 'incomplete-request-ignored'});
    exit 4;
}

my $request_bytes = read_bytes($request_path);
my $request = eval { JSON::PP->new->utf8(1)->decode($request_bytes) };
if (!$request || ref($request) ne 'HASH') {
    print canonical({status => 'malformed-request-rejected'});
    exit 6;
}
if (($request->{requestToken} // '') ne $expected_token
        || !$request->{complete}
        || ($request->{toolId} // '') ne
            'fst.snapshot-generation-retention-drill.v1') {
    print canonical({status => 'request-fence-rejected'});
    exit 6;
}

if (-f $proof_path) {
    my $existing_bytes = read_bytes($proof_path);
    my $existing = eval {
        JSON::PP->new->utf8(1)->decode($existing_bytes)
    };
    die "existing proof is not valid JSON\n"
        unless $existing && ref($existing) eq 'HASH';
    die "existing proof failed integrity\n"
        unless ($existing->{integritySha256} // '') eq integrity($existing);
    die "existing proof identity differs\n"
        unless ($existing->{requestToken} // '') eq $expected_token
            && ($existing->{requestSha256} // '') eq sha256_hex($request_bytes)
            && ($existing->{archiveSha256} // '') eq
                ($request->{archiveSha256} // '');
    print canonical({status => 'resumed-existing-proof'});
    exit 0;
}

my $observed_archive_sha = sha_file($archive_path);
my $archive_bytes = -s $archive_path;
my $environment = {
    networkInterfaces => network_interfaces(),
    dockerSocketPresent => (-e '/var/run/docker.sock')
        ? JSON::PP::true : JSON::PP::false,
    requestsMountWritable => writable_probe(dirname($request_path)),
    archiveMountWritable => writable_probe(dirname($archive_path)),
    proofsMountWritable => writable_probe(dirname($proof_path)),
};

if ($observed_archive_sha ne ($request->{archiveSha256} // '')) {
    my $rejection = {
        formatVersion => 1,
        toolId => 'fst.snapshot-generation-retention-drill.v1',
        status => 'rejected',
        reason => 'archive-digest-mismatch',
        requestToken => $expected_token,
        requestSha256 => sha256_hex($request_bytes),
        expectedArchiveSha256 => ($request->{archiveSha256} // ''),
        observedArchiveSha256 => $observed_archive_sha,
        environment => $environment,
    };
    $rejection->{integritySha256} = integrity($rejection);
    atomic_write($reject_path, canonical($rejection));
    print canonical({status => 'archive-digest-mismatch'});
    exit 5;
}

my $proof = {
    formatVersion => 1,
    toolId => 'fst.snapshot-generation-retention-drill.v1',
    status => 'proved',
    requestToken => $expected_token,
    requestSha256 => sha256_hex($request_bytes),
    sourceFenceSha256 => ($request->{sourceFenceSha256} // ''),
    archiveSha256 => $observed_archive_sha,
    archiveBytes => 0 + $archive_bytes,
    environment => $environment,
};
$proof->{integritySha256} = integrity($proof);
atomic_write($proof_path, canonical($proof));
print canonical({status => 'proved'});
exit 0;
'''


def prover_integrity(value):
    body = dict(value)
    body.pop("integritySha256", None)
    return sha256_bytes(canonical_compact_bytes(body))


def validate_request_payload(value, archive_path, expected_token):
    if value.get("toolId") != TOOL_ID or value.get("complete") is not True:
        raise DrillError("mailbox request is not complete")
    if value.get("requestToken") != expected_token:
        raise DrillError("mailbox request token fence differs")
    if pathlib.Path(value.get("archivePath", "")).name != pathlib.Path(
        archive_path
    ).name:
        raise DrillError("mailbox request archive basename differs")
    observed = sha256_path(archive_path)
    if value.get("archiveSha256") != observed:
        raise DrillError("mailbox request archive digest differs")
    return True


def complete_mailbox_requests(directory):
    directory = pathlib.Path(directory)
    requests = []
    for path in sorted(directory.glob("*.request.json")):
        metadata = path.lstat()
        if path.is_symlink() or not stat.S_ISREG(metadata.st_mode):
            raise DrillError(f"mailbox request is not a regular file: {path}")
        requests.append(path)
    return requests


def validate_prover_document(value, *, expected_status, expected_token):
    if value.get("status") != expected_status:
        raise DrillError("prover document has the wrong status")
    if value.get("requestToken") != expected_token:
        raise DrillError("prover document token differs")
    if value.get("integritySha256") != prover_integrity(value):
        raise DrillError("prover document failed integrity")
    environment = value.get("environment") or {}
    if environment.get("dockerSocketPresent") is not False:
        raise DrillError("prover observed a Docker socket")
    if environment.get("requestsMountWritable") is not False:
        raise DrillError("prover request mount was writable")
    if environment.get("archiveMountWritable") is not False:
        raise DrillError("prover archive mount was writable")
    if environment.get("proofsMountWritable") is not True:
        raise DrillError("prover proof mount was not writable")
    if environment.get("networkInterfaces") != ["lo"]:
        raise DrillError("prover had a non-loopback network interface")
    return True


def run_prover_container(
    runner,
    *,
    image,
    runtime_root,
    work_root,
    run_id,
    token,
    role,
    script_path,
    request_path,
    archive_path,
    proof_path,
    rejection_path,
    expected_token,
):
    name = container_name(token, role)
    pgdata_name = f"{role}-pgdata"
    pgdata = create_empty_runtime_directory(runtime_root, pgdata_name)
    container_id = None
    inspected_before = None
    isolation = None
    pgdata_bind = None
    completed = None
    exit_code = None
    error = None
    cleanup_error = None
    try:
        remove_owned_container(runner, name, run_id, role)
        created = runner.run(
            [
                *docker_engine_command("create", "--pull=never"),
                "--name",
                name,
                "--network",
                "none",
                "--read-only",
                "--security-opt",
                "no-new-privileges",
                "--cap-drop",
                "ALL",
                "--user",
                f"{os.getuid()}:{os.getgid()}",
                *label_arguments(run_id, role),
                "-v",
                f"{pgdata}:/var/lib/postgresql/data",
                "-v",
                f"{script_path}:/tool/prover.pl:ro",
                "-v",
                f"{request_path.parent}:/requests:ro",
                "-v",
                f"{archive_path.parent}:/archive:ro",
                "-v",
                f"{proof_path.parent}:/proofs:rw",
                "--entrypoint",
                "perl",
                image,
                "/tool/prover.pl",
                f"/requests/{request_path.name}",
                f"/archive/{archive_path.name}",
                f"/proofs/{proof_path.name}",
                f"/proofs/{rejection_path.name}",
                expected_token,
            ],
            timeout=60,
        )
        container_id = created_container_id(created)
        inspected_before = inspect_container(runner, name)
        assert_owned_container(inspected_before, run_id, role)
        assert_container_image(inspected_before, image, role)
        isolation = container_isolation_evidence(inspected_before)
        pgdata_bind = validate_pgdata_bind(
            inspected_before,
            pgdata,
            work_root,
            role,
        )
        if (
            isolation["networkMode"] != "none"
            or isolation["dockerSocketMounted"]
            or isolation["readOnlyRootfs"] is not True
        ):
            raise DrillError("prover container configuration is not isolated")
        completed = runner.run(
            docker_engine_command("start", "-a", name),
            timeout=300,
            check=False,
        )
        inspected_after = inspect_container(runner, name)
        exit_code = int(
            (inspected_after.get("State") or {}).get("ExitCode", -1)
        )
    except Exception as caught:
        error = caught
    finally:
        try:
            remove_created_container(
                runner,
                name,
                run_id,
                role,
                container_id=container_id,
            )
        except Exception as caught:
            cleanup_error = caught
        try:
            remove_empty_runtime_directory(
                runtime_root,
                pgdata,
                pgdata_name,
            )
        except Exception as caught:
            if cleanup_error is None:
                cleanup_error = caught
    if error is not None:
        if cleanup_error is not None:
            raise DrillError(
                f"{role} prover failed and cleanup also failed: "
                f"{cleanup_error}"
            ) from error
        raise error
    if cleanup_error is not None:
        raise cleanup_error
    removed = inspect_container(runner, name, allow_missing=True) is None
    pgdata_removed = not pgdata.exists()
    if not removed or not pgdata_removed:
        raise DrillError(f"{role} container or controlled PGDATA remains")
    return {
        "container": name,
        "exitCode": exit_code,
        "stdout": completed.stdout.strip(),
        "stderr": completed.stderr.strip(),
        "isolation": {
            **isolation,
            "pgdataBind": pgdata_bind,
        },
        "removed": removed,
        "pgdataRemoved": pgdata_removed,
    }


def mailbox_request(token, archive_path, archive_sha, source_fence_sha):
    return {
        "formatVersion": FORMAT_VERSION,
        "toolId": TOOL_ID,
        "requestToken": token,
        "complete": True,
        "archivePath": archive_path.name,
        "archiveSha256": archive_sha,
        "sourceFenceSha256": source_fence_sha,
        "publishedAtUtc": utc_now(),
    }


def prove_mailbox_contract(
    runner,
    *,
    root,
    image,
    run_id,
    token,
    archive_path,
    source_fence,
):
    mailbox_root = root / "evidence" / "mailbox"
    runtime_root = root / "runtime"
    requests = mailbox_root / "requests"
    proofs = mailbox_root / "proofs"
    requests.mkdir(parents=True, mode=0o700)
    proofs.mkdir(mode=0o700)
    script_path = mailbox_root / "prover.pl"
    atomic_publish_bytes(script_path, PROVER_SOURCE.encode("utf-8"))
    archive_sha = sha256_path(archive_path)
    source_fence_sha = sha256_bytes(
        migration.canonical_json_bytes(source_fence)
    )

    torn_token = f"{token}-torn"
    torn_request = requests / f"{torn_token}.request.json"
    torn_partial = requests / f".{torn_request.name}.partial-torn"
    write_torn_bytes(
        torn_partial,
        migration.canonical_json_bytes(
            mailbox_request(
                torn_token,
                archive_path,
                archive_sha,
                source_fence_sha,
            )
        )[:79],
    )
    torn_proof = proofs / f"{torn_token}.proof.json"
    torn_rejection = proofs / f"{torn_token}.rejected.json"
    torn_run = run_prover_container(
        runner,
        image=image,
        runtime_root=runtime_root,
        work_root=root,
        run_id=run_id,
        token=token,
        role="prover-torn",
        script_path=script_path,
        request_path=torn_request,
        archive_path=archive_path,
        proof_path=torn_proof,
        rejection_path=torn_rejection,
        expected_token=torn_token,
    )
    if (
        torn_run["exitCode"] != 4
        or torn_proof.exists()
        or torn_rejection.exists()
    ):
        raise DrillError("torn mailbox request was not safely ignored")

    mismatch_token = f"{token}-digest"
    mismatch_request = requests / f"{mismatch_token}.request.json"
    mismatch_value = mailbox_request(
        mismatch_token,
        archive_path,
        "0" * 64,
        source_fence_sha,
    )
    atomic_publish_json(mismatch_request, mismatch_value)
    mismatch_proof = proofs / f"{mismatch_token}.proof.json"
    mismatch_rejection = proofs / f"{mismatch_token}.rejected.json"
    mismatch_run = run_prover_container(
        runner,
        image=image,
        runtime_root=runtime_root,
        work_root=root,
        run_id=run_id,
        token=token,
        role="prover-digest",
        script_path=script_path,
        request_path=mismatch_request,
        archive_path=archive_path,
        proof_path=mismatch_proof,
        rejection_path=mismatch_rejection,
        expected_token=mismatch_token,
    )
    if (
        mismatch_run["exitCode"] != 5
        or mismatch_proof.exists()
        or not mismatch_rejection.is_file()
    ):
        raise DrillError("digest-mismatched request was not rejected")
    rejection = read_json(mismatch_rejection)
    validate_prover_document(
        rejection,
        expected_status="rejected",
        expected_token=mismatch_token,
    )
    if rejection.get("reason") != "archive-digest-mismatch":
        raise DrillError("digest rejection reason differs")

    good_token = f"{token}-good"
    good_request = requests / f"{good_token}.request.json"
    good_value = mailbox_request(
        good_token,
        archive_path,
        archive_sha,
        source_fence_sha,
    )
    atomic_publish_json(good_request, good_value)
    validate_request_payload(good_value, archive_path, good_token)
    good_proof = proofs / f"{good_token}.proof.json"
    good_rejection = proofs / f"{good_token}.rejected.json"
    torn_proof_partial = proofs / f".{good_proof.name}.partial-torn"
    write_torn_bytes(torn_proof_partial, b'{"status":"proved"')

    first_run = run_prover_container(
        runner,
        image=image,
        runtime_root=runtime_root,
        work_root=root,
        run_id=run_id,
        token=token,
        role="prover-good",
        script_path=script_path,
        request_path=good_request,
        archive_path=archive_path,
        proof_path=good_proof,
        rejection_path=good_rejection,
        expected_token=good_token,
    )
    if (
        first_run["exitCode"] != 0
        or not good_proof.is_file()
        or good_rejection.exists()
    ):
        raise DrillError("complete mailbox request was not proved")
    proof = read_json(good_proof)
    validate_prover_document(
        proof,
        expected_status="proved",
        expected_token=good_token,
    )
    if (
        proof.get("archiveSha256") != archive_sha
        or proof.get("archiveBytes") != archive_path.stat().st_size
        or proof.get("sourceFenceSha256") != source_fence_sha
    ):
        raise DrillError("prover output is not fenced to archive/source")

    proof_sha_before = sha256_path(good_proof)
    resumed_run = run_prover_container(
        runner,
        image=image,
        runtime_root=runtime_root,
        work_root=root,
        run_id=run_id,
        token=token,
        role="prover-resume",
        script_path=script_path,
        request_path=good_request,
        archive_path=archive_path,
        proof_path=good_proof,
        rejection_path=good_rejection,
        expected_token=good_token,
    )
    if (
        resumed_run["exitCode"] != 0
        or sha256_path(good_proof) != proof_sha_before
        or "resumed-existing-proof" not in resumed_run["stdout"]
    ):
        raise DrillError("prover restart/resume was not idempotent")

    return write_integrity_json(
        mailbox_root / "mailbox-proof.json",
        {
            "formatVersion": FORMAT_VERSION,
            "toolId": TOOL_ID,
            "completedAtUtc": utc_now(),
            "archiveSha256": archive_sha,
            "sourceFenceSha256": source_fence_sha,
            "atomicRequestPublication": {
                "sameDirectoryTemporary": True,
                "fileFsync": True,
                "rename": True,
                "directoryFsync": True,
            },
            "tornRequest": {
                "partialPath": str(torn_partial),
                "finalPath": str(torn_request),
                "finalAbsent": not torn_request.exists(),
                "proofAbsent": not torn_proof.exists(),
                "containerRun": torn_run,
            },
            "digestMismatch": {
                "requestPath": str(mismatch_request),
                "successProofAbsent": not mismatch_proof.exists(),
                "rejectionPath": str(mismatch_rejection),
                "rejectionSha256": sha256_path(mismatch_rejection),
                "containerRun": mismatch_run,
            },
            "completeRequest": {
                "requestPath": str(good_request),
                "requestSha256": sha256_path(good_request),
                "proofPath": str(good_proof),
                "proofSha256": proof_sha_before,
                "tornProofPath": str(torn_proof_partial),
                "tornProofNotAccepted": True,
                "firstRun": first_run,
                "resumedRun": resumed_run,
                "idempotent": True,
            },
            "asymmetricMounts": {
                "requests": "read-only",
                "archive": "read-only",
                "proofs": "read-write",
            },
            "networkNone": True,
            "dockerSocketInsideProver": False,
            "passed": True,
        },
    )


def reference_guard_sql(target, expected, label):
    return migration.transactional_reference_parity_guard_sql(
        target,
        target.partition,
        expected,
        label,
    )


def default_guard_sql(default_child, label):
    return f"""
        DO ${label}$
        BEGIN
            IF (SELECT COUNT(*) FROM {qualified(default_child)}) <> 0 THEN
                RAISE EXCEPTION 'default child changed before {label}';
            END IF;
        END
        ${label}$;
    """


def attachment_guard_sql(target, child, *, attached, label):
    expected = "EXISTS" if attached else "NOT EXISTS"
    return f"""
        DO ${label}$
        BEGIN
            IF {expected} (
                SELECT 1
                FROM pg_inherits
                WHERE inhparent = {migration.sql_literal(
                    f"public.{target.partition}"
                )}::regclass
                  AND inhrelid = {migration.sql_literal(
                    f"public.{child}"
                )}::regclass
            ) IS NOT TRUE THEN
                RAISE EXCEPTION 'attachment fence failed before {label}';
            END IF;
        END
        ${label}$;
    """


def measured_transaction_sql(
    target,
    child,
    default_child,
    expected_reference,
    *,
    label,
    ddl,
    initially_attached,
):
    return f"""
        BEGIN;
        SET LOCAL lock_timeout = '5s';
        SET LOCAL statement_timeout = '120s';
        SET LOCAL idle_in_transaction_session_timeout = 0;
        SELECT json_build_object(
            'event', 'started',
            'backendPid', pg_backend_pid(),
            'epoch', extract(epoch FROM clock_timestamp()));
        {reference_guard_sql(target, expected_reference, label + "_references")}
        {default_guard_sql(default_child, label + "_default")}
        {attachment_guard_sql(
            target,
            child,
            attached=initially_attached,
            label=label + "_attachment",
        )}
        {ddl}
        SELECT json_build_object(
            'event', 'ddl-ready',
            'epoch', extract(epoch FROM clock_timestamp()));
    """


def direct_drop_sql(target, expected_reference):
    child = migration.generation_child_name(target, TARGET_SNAPSHOT_ID)
    return measured_transaction_sql(
        target,
        child,
        migration.default_child_name(target),
        expected_reference,
        label="direct_drop",
        ddl=f"DROP TABLE {qualified(child)};",
        initially_attached=True,
    )


def detach_sql(target, expected_reference, *, label="ordinary_detach"):
    child = migration.generation_child_name(target, TARGET_SNAPSHOT_ID)
    return measured_transaction_sql(
        target,
        child,
        migration.default_child_name(target),
        expected_reference,
        label=label,
        ddl=(
            f"ALTER TABLE {qualified(target.partition)} "
            f"DETACH PARTITION {qualified(child)};"
        ),
        initially_attached=True,
    )


def reattach_sql(target, expected_reference):
    child = migration.generation_child_name(target, TARGET_SNAPSHOT_ID)
    return measured_transaction_sql(
        target,
        child,
        migration.default_child_name(target),
        expected_reference,
        label="bounded_reattach",
        ddl=(
            f"ALTER TABLE {qualified(target.partition)} "
            f"ATTACH PARTITION {qualified(child)} "
            f"FOR VALUES IN ({TARGET_SNAPSHOT_ID});"
        ),
        initially_attached=False,
    )


def detached_drop_sql(target, expected_reference):
    child = migration.generation_child_name(target, TARGET_SNAPSHOT_ID)
    return measured_transaction_sql(
        target,
        child,
        migration.default_child_name(target),
        expected_reference,
        label="detached_drop",
        ddl=f"DROP TABLE {qualified(child)};",
        initially_attached=False,
    )


def lock_query(backend_pid):
    return f"""
        SELECT COALESCE(
            json_agg(
                json_build_object(
                    'namespace', namespace.nspname,
                    'relation', relation.relname,
                    'mode', lock.mode,
                    'granted', lock.granted)
                ORDER BY namespace.nspname, relation.relname, lock.mode),
            '[]'::json)
        FROM pg_locks lock
        LEFT JOIN pg_class relation
          ON relation.oid = lock.relation
        LEFT JOIN pg_namespace namespace
          ON namespace.oid = relation.relnamespace
        WHERE lock.pid = {int(backend_pid)}
          AND lock.locktype = 'relation'
          AND lock.granted
          AND namespace.nspname = 'public'
    """


def parse_transaction_events(lines):
    events = []
    for line in lines:
        candidate = line.strip()
        if not candidate:
            continue
        try:
            value = json.loads(candidate)
        except json.JSONDecodeError:
            continue
        if isinstance(value, dict) and value.get("event"):
            events.append(value)
    if [event.get("event") for event in events] != [
        "started",
        "ddl-ready",
        "observation-release",
    ]:
        raise DrillError(f"transaction markers are incomplete: {events}")
    return events


def measurement_psql_arguments(database, sql, application_name):
    return docker_engine_command(
        "exec",
        "-e",
        "PGCONNECT_TIMEOUT=10",
        "-e",
        f"PGAPPNAME={application_name}",
        "-e",
        "PGOPTIONS=-c row_security=off",
        database.container,
        "psql",
        "-X",
        "-q",
        "-v",
        "ON_ERROR_STOP=1",
        "-U",
        database.user,
        "-d",
        database.database,
        "-At",
        "-c",
        sql,
    )


def activity_query(application_name):
    return f"""
        SELECT COALESCE(
            (
                SELECT json_build_object(
                    'pid', activity.pid,
                    'state', activity.state,
                    'waitEventType', activity.wait_event_type,
                    'waitEvent', activity.wait_event)
                FROM pg_stat_activity activity
                WHERE activity.application_name =
                        {migration.sql_literal(application_name)}
                ORDER BY activity.backend_start DESC
                LIMIT 1
            ),
            '{{}}'::json)
    """


def wait_for_activity(database, application_name, predicate, timeout=15):
    deadline = time.monotonic() + timeout
    last = {}
    while time.monotonic() < deadline:
        last = database.json(activity_query(application_name))
        if last and predicate(last):
            return last
        time.sleep(0.02)
    raise DrillError(
        f"timed out waiting for measurement backend {application_name}: {last}"
    )


def run_measured_transaction(
    database,
    sql,
    *,
    finish,
    top_parent,
    instrument_root,
):
    if finish not in ("commit", "rollback"):
        raise DrillError(f"unknown transaction finish: {finish}")
    nonce = secrets.token_hex(4)
    advisory_key = int(
        hashlib.sha256(
            f"{database.container}:{nonce}".encode("utf-8")
        ).hexdigest()[:15],
        16,
    )
    blocker_application = f"fst-sgr-blocker-{nonce}"
    measured_application = f"fst-sgr-measure-{nonce}"
    blocker_sql = (
        f"SELECT pg_advisory_lock({advisory_key}); "
        "SELECT pg_sleep(120);"
    )
    blocker = database.runner.popen(
        measurement_psql_arguments(
            database,
            blocker_sql,
            blocker_application,
        ),
        stdin=subprocess.DEVNULL,
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
        text=True,
    )
    blocker_pid = None
    measured = None
    started = None
    try:
        blocker_activity = wait_for_activity(
            database,
            blocker_application,
            lambda value: value.get("waitEvent") == "PgSleep",
        )
        blocker_pid = migration.ensure_integer(
            blocker_activity.get("pid"),
            "measurement blocker PID",
            1,
        )
        finish_sql = "COMMIT;" if finish == "commit" else "ROLLBACK;"
        measured_sql = (
            sql
            + f"""
                SELECT pg_advisory_xact_lock({advisory_key});
                SELECT json_build_object(
                    'event', 'observation-release',
                    'epoch', extract(epoch FROM clock_timestamp()));
                {finish_sql}
            """
        )
        started = time.monotonic()
        measured = database.runner.popen(
            measurement_psql_arguments(
                database,
                measured_sql,
                measured_application,
            ),
            stdin=subprocess.DEVNULL,
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
            text=True,
        )
        measured_activity = wait_for_activity(
            database,
            measured_application,
            lambda value: (
                value.get("waitEventType") == "Lock"
                and value.get("waitEvent") == "advisory"
            ),
        )
        backend_pid = migration.ensure_integer(
            measured_activity.get("pid"),
            "measurement backend PID",
            1,
        )
        locks = database.json(lock_query(backend_pid))
        parent_locks = [
            lock
            for lock in locks
            if lock.get("relation") in (top_parent, instrument_root)
        ]
        terminated = database.scalar(
            f"SELECT pg_terminate_backend({blocker_pid})"
        )
        if terminated != "t":
            raise DrillError("could not release measurement advisory blocker")
        stdout, stderr = measured.communicate(timeout=60)
        finished = time.monotonic()
        if measured.returncode != 0:
            raise DrillError(
                "measured catalog transaction failed: "
                + stderr.strip()[-2000:]
            )
        events = parse_transaction_events(stdout.splitlines())
        return {
            "finish": finish,
            "backendPid": backend_pid,
            "databaseToDdlReadySeconds": round(
                float(events[1]["epoch"]) - float(events[0]["epoch"]),
                6,
            ),
            "databaseLockHoldSeconds": round(
                float(events[2]["epoch"]) - float(events[0]["epoch"]),
                6,
            ),
            "lockObservationSeconds": round(
                float(events[2]["epoch"]) - float(events[1]["epoch"]),
                6,
            ),
            "wallClockSeconds": round(finished - started, 6),
            "relationLocks": locks,
            "parentLocks": parent_locks,
        }
    finally:
        if blocker_pid is not None:
            with contextlib.suppress(Exception):
                database.scalar(
                    "SELECT CASE WHEN EXISTS ("
                    "SELECT 1 FROM pg_stat_activity "
                    f"WHERE pid = {blocker_pid}) "
                    f"THEN pg_terminate_backend({blocker_pid}) ELSE TRUE END"
                )
        if measured is not None and measured.poll() is None:
            measured.terminate()
            with contextlib.suppress(subprocess.TimeoutExpired):
                measured.wait(timeout=10)
        if blocker.poll() is None:
            blocker.terminate()
            with contextlib.suppress(subprocess.TimeoutExpired):
                blocker.wait(timeout=10)
        with contextlib.suppress(Exception):
            blocker.communicate(timeout=1)


def parent_query_evidence(database, target):
    return {
        "fingerprint": relation_fingerprint(
            database,
            target.partition,
        ),
        "distribution": relation_distribution(
            database,
            target.partition,
        ),
    }


def check_constraint_evidence(database, child, constraint):
    return database.json(
        f"""
            SELECT json_build_object(
                'name', metadata.conname,
                'validated', metadata.convalidated,
                'definition', pg_get_constraintdef(
                    metadata.oid, true))
            FROM pg_constraint metadata
            WHERE metadata.conrelid =
                    {migration.sql_literal(f"public.{child}")}::regclass
              AND metadata.conname =
                    {migration.sql_literal(constraint)}
        """
    )


def matching_check_sql(child, constraint_name):
    return f"""
        ALTER TABLE {qualified(child)}
            ADD CONSTRAINT "{constraint_name}"
            CHECK (snapshot_id = {TARGET_SNAPSHOT_ID}) NOT VALID;
        ALTER TABLE {qualified(child)}
            VALIDATE CONSTRAINT "{constraint_name}";
    """


def concurrent_detach_rejections(runner, database, target, child):
    statements = {
        "insideRequiredTransaction": f"""
            BEGIN;
            ALTER TABLE {qualified(target.partition)}
                DETACH PARTITION {qualified(child)} CONCURRENTLY;
        """,
        "withDefaultPartition": f"""
            ALTER TABLE {qualified(target.partition)}
                DETACH PARTITION {qualified(child)} CONCURRENTLY;
        """,
    }
    results = {}
    for label, sql in statements.items():
        completed = runner.run(
            database._arguments(sql),
            timeout=60,
            check=False,
        )
        if completed.returncode == 0:
            raise DrillError(
                f"DETACH CONCURRENTLY unexpectedly succeeded: {label}"
            )
        results[label] = {
            "returnCode": completed.returncode,
            "stderr": completed.stderr.strip(),
        }
    return {
        "accepted": False,
        "reason": (
            "ordinary transactional DETACH is required: concurrent detach "
            "cannot run in the exact-reference transaction and PostgreSQL "
            "rejects it while the instrument root has a default partition"
        ),
        "runtimeRejections": results,
    }


def strategy_comparison(
    runner,
    *,
    root,
    target,
    database,
    expected_reference,
):
    strategy_root = root / "evidence" / "strategy"
    strategy_root.mkdir(parents=True, mode=0o700)
    child = migration.generation_child_name(target, TARGET_SNAPSHOT_ID)
    default_child = migration.default_child_name(target)
    baseline = parent_query_evidence(database, target)
    target_fingerprint = relation_fingerprint(database, child)
    concurrent_rejection = concurrent_detach_rejections(
        runner,
        database,
        target,
        child,
    )

    direct = run_measured_transaction(
        database,
        direct_drop_sql(target, expected_reference),
        finish="rollback",
        top_parent=migration.TARGET_PARENT,
        instrument_root=target.partition,
    )
    after_direct_rollback = parent_query_evidence(database, target)
    if after_direct_rollback != baseline:
        raise DrillError("direct DROP rollback did not restore parent parity")
    if default_row_count(database, default_child) != 0:
        raise DrillError("default child changed after direct DROP rollback")

    check_name = f"fst_sgr_snapshot_{TARGET_SNAPSHOT_ID}_check"
    check_started = time.monotonic()
    database.psql(
        matching_check_sql(child, check_name),
        timeout=180,
    )
    check_evidence = check_constraint_evidence(
        database,
        child,
        check_name,
    )
    check_evidence["elapsedSeconds"] = round(
        time.monotonic() - check_started,
        6,
    )
    if (
        check_evidence.get("validated") is not True
        or f"snapshot_id = {TARGET_SNAPSHOT_ID}"
        not in check_evidence.get("definition", "")
    ):
        raise DrillError("matching reattach CHECK was not validated")

    first_detach = run_measured_transaction(
        database,
        detach_sql(target, expected_reference, label="rollback_detach"),
        finish="commit",
        top_parent=migration.TARGET_PARENT,
        instrument_root=target.partition,
    )
    detached_parent = parent_query_evidence(database, target)
    if TARGET_SNAPSHOT_ID in {
        row["snapshotId"] for row in detached_parent["distribution"]
    }:
        raise DrillError("ordinary DETACH left target rows visible in parent")

    reattach = run_measured_transaction(
        database,
        reattach_sql(target, expected_reference),
        finish="commit",
        top_parent=migration.TARGET_PARENT,
        instrument_root=target.partition,
    )
    after_reattach = parent_query_evidence(database, target)
    if after_reattach != baseline:
        raise DrillError("bounded re-ATTACH did not restore exact parent parity")
    if relation_fingerprint(database, child) != target_fingerprint:
        raise DrillError("bounded re-ATTACH changed target leaf contents")
    retained_check = check_constraint_evidence(
        database,
        child,
        check_name,
    )
    if retained_check.get("validated") is not True:
        raise DrillError("bounded re-ATTACH lost the validated CHECK")

    final_detach = run_measured_transaction(
        database,
        detach_sql(target, expected_reference, label="final_detach"),
        finish="commit",
        top_parent=migration.TARGET_PARENT,
        instrument_root=target.partition,
    )
    detached_drop = run_measured_transaction(
        database,
        detached_drop_sql(target, expected_reference),
        finish="commit",
        top_parent=migration.TARGET_PARENT,
        instrument_root=target.partition,
    )
    final_parent = parent_query_evidence(database, target)
    final_ids = [
        row["snapshotId"] for row in final_parent["distribution"]
    ]
    if final_ids != sorted(PROTECTED_SNAPSHOT_IDS, reverse=True):
        raise DrillError(f"final fixture snapshot IDs differ: {final_ids}")
    if default_row_count(database, default_child) != 0:
        raise DrillError("final default child is not empty")
    if database.scalar(
        "SELECT to_regclass("
        f"{migration.sql_literal(f'public.{child}')}) IS NULL"
    ) != "t":
        raise DrillError("detached target child still exists after DROP")
    final_reference = database.json(
        migration.reference_parity_query(target, target.partition)
    )
    migration.assert_reference_parity(final_reference)
    if final_reference != expected_reference:
        raise DrillError("final fixture references differ from the exact fence")

    report = write_integrity_json(
        strategy_root / "drop-strategy-proof.json",
        {
            "formatVersion": FORMAT_VERSION,
            "toolId": TOOL_ID,
            "completedAtUtc": utc_now(),
            "targetChild": f"public.{child}",
            "exactReferenceFence": expected_reference,
            "defaultChild": {
                "relation": f"public.{default_child}",
                "remainedEmpty": True,
            },
            "directAttachedDrop": {
                "transactionalReferenceRecheckBeforeDdl": True,
                "catalogChangeExecuted": True,
                "rolledBackForMatchedInput": True,
                "measurement": direct,
                "parentParityAfterRollback": True,
            },
            "ordinaryDetachPath": {
                "matchingCheck": check_evidence,
                "firstDetach": first_detach,
                "reattach": reattach,
                "parentParityAfterReattach": True,
                "targetFingerprintAfterReattach": target_fingerprint,
                "finalDetach": final_detach,
                "detachedDrop": detached_drop,
                "transactionalReferenceRecheckBeforeEveryCatalogChange": True,
            },
            "detachConcurrently": concurrent_rejection,
            "finalFixture": {
                "accepted": True,
                "targetRelationAbsent": True,
                "snapshotIds": final_ids,
                "parentEvidence": final_parent,
                "referenceParity": final_reference,
                "defaultRows": 0,
            },
            "comparison": {
                "directDrop": {
                    "databaseToDdlReadySeconds":
                        direct["databaseToDdlReadySeconds"],
                    "databaseLockHoldSeconds":
                        direct["databaseLockHoldSeconds"],
                    "lockObservationSeconds":
                        direct["lockObservationSeconds"],
                    "wallClockSeconds": direct["wallClockSeconds"],
                    "parentLocks": direct["parentLocks"],
                },
                "ordinaryDetach": {
                    "databaseToDdlReadySeconds":
                        final_detach["databaseToDdlReadySeconds"],
                    "databaseLockHoldSeconds":
                        final_detach["databaseLockHoldSeconds"],
                    "lockObservationSeconds":
                        final_detach["lockObservationSeconds"],
                    "wallClockSeconds": final_detach["wallClockSeconds"],
                    "parentLocks": final_detach["parentLocks"],
                },
                "designConclusion": (
                    "not hard-coded; both measured paths are reported for "
                    "operator adjudication"
                ),
            },
            "noCascade": True,
            "passed": True,
        },
    )
    return report


def fixture_inventory(database, target):
    inventory = database.json(migration.inventory_query(target))
    protected_result = database.json(migration.protected_sources_query(target))
    protected = migration.derive_protected_ids(
        protected_result,
        inventory,
    )
    default_child = migration.default_child_name(target)
    default_rows = default_row_count(database, default_child)
    candidates = candidate_snapshot_ids(
        inventory,
        protected,
        default_rows=default_rows,
    )
    if candidates != [TARGET_SNAPSHOT_ID]:
        raise DrillError(f"fixture candidate set differs: {candidates}")
    reference = database.json(
        migration.reference_parity_query(target, target.partition)
    )
    migration.assert_reference_parity(reference)
    return {
        "inventorySnapshotIds": sorted(inventory),
        "protectedSources": protected_result,
        "protectedSnapshotIds": protected,
        "candidateSnapshotIds": candidates,
        "defaultChild": f"public.{default_child}",
        "defaultRows": default_rows,
        "referenceParity": reference,
    }


def directory_size(path):
    total = 0
    for entry in pathlib.Path(path).rglob("*"):
        if entry.is_file() and not entry.is_symlink():
            total += entry.stat().st_size
    return total


def artifact_tree_entries(root):
    root = pathlib.Path(root)
    metadata = root.lstat()
    if stat.S_ISLNK(metadata.st_mode) or not stat.S_ISDIR(metadata.st_mode):
        raise DrillError("artifact root must be a real directory")
    directories = []
    files = []
    for current, names, filenames in os.walk(root, followlinks=False):
        current_path = pathlib.Path(current)
        for name in names:
            path = current_path / name
            metadata = path.lstat()
            if stat.S_ISLNK(metadata.st_mode):
                raise DrillError(f"artifact tree contains a symlink: {path}")
            if not stat.S_ISDIR(metadata.st_mode):
                raise DrillError(
                    f"artifact tree contains a non-directory entry: {path}"
                )
            directories.append(path)
        for name in filenames:
            path = current_path / name
            metadata = path.lstat()
            if stat.S_ISLNK(metadata.st_mode):
                raise DrillError(f"artifact tree contains a symlink: {path}")
            if not stat.S_ISREG(metadata.st_mode):
                raise DrillError(
                    f"artifact tree contains a non-regular file: {path}"
                )
            files.append(path)
    return sorted(directories), sorted(files)


def verify_integrity_document(value, label):
    if not isinstance(value, dict):
        raise DrillError(f"{label} is not a JSON object")
    if value.get("integritySha256") != migration.report_integrity(value):
        raise DrillError(f"{label} failed integrity")
    return value


def manifest_entry(path, root):
    return {
        "path": str(path.relative_to(root)),
        "bytes": path.stat().st_size,
        "sha256": sha256_path(path),
    }


def current_sealable_manifest(root):
    _, files = artifact_tree_entries(root)
    return sorted(
        (
            manifest_entry(path, root)
            for path in files
            if path.name not in ("checksums.json", "seal.json")
            and path.name != SEAL_FAILURE_NAME
        ),
        key=lambda value: value["path"],
    )


def verify_nonwritable_artifact_tree(root):
    directories, files = artifact_tree_entries(root)
    writable = []
    for path in [root, *directories, *files]:
        if stat.S_IMODE(path.lstat().st_mode) & 0o222:
            writable.append(str(path.relative_to(root)) or ".")
    if writable:
        raise DrillError(
            "sealed artifact tree remains writable: " + ", ".join(writable)
        )
    return True


def verify_terminal_seal(root, *, require_nonwritable):
    root = pathlib.Path(root)
    artifact_tree_entries(root)
    failure_path = root / SEAL_FAILURE_NAME
    if failure_path.exists() or failure_path.is_symlink():
        raise DrillError("terminal seal conflicts with seal-failure evidence")
    report_path = root / "drill-report.json"
    checksums_path = root / "checksums.json"
    seal_path = root / "seal.json"
    for path in (report_path, checksums_path, seal_path):
        try:
            metadata = path.lstat()
        except FileNotFoundError as error:
            raise DrillError(
                f"terminal success artifact is missing: {path.name}"
            ) from error
        if stat.S_ISLNK(metadata.st_mode) or not stat.S_ISREG(metadata.st_mode):
            raise DrillError(
                f"terminal success artifact is not a regular file: {path.name}"
            )

    report = verify_integrity_document(read_json(report_path), "drill report")
    checksums = verify_integrity_document(
        read_json(checksums_path),
        "checksum manifest",
    )
    seal = verify_integrity_document(read_json(seal_path), "terminal seal")
    if report.get("passed") is not True:
        raise DrillError("terminal drill report is not successful")
    if seal.get("passed") is not True:
        raise DrillError("terminal seal is not successful")
    if seal.get("report") != {
        "path": report_path.name,
        "sha256": sha256_path(report_path),
    }:
        raise DrillError("terminal seal report reference differs")
    if seal.get("checksums") != {
        "path": checksums_path.name,
        "sha256": sha256_path(checksums_path),
    }:
        raise DrillError("terminal seal checksum reference differs")

    observed_manifest = current_sealable_manifest(root)
    if checksums.get("files") != observed_manifest:
        raise DrillError("terminal checksum manifest differs from artifact tree")
    if seal.get("fileCount") != len(observed_manifest):
        raise DrillError("terminal seal file count differs")
    if require_nonwritable:
        verify_nonwritable_artifact_tree(root)
    return {
        "report": report,
        "checksums": checksums,
        "seal": seal,
        "reportSha256": sha256_path(report_path),
        "checksumsSha256": sha256_path(checksums_path),
        "sealSha256": sha256_path(seal_path),
        "nonwritable": require_nonwritable,
        "passed": True,
    }


def restore_owner_permissions(root):
    root = pathlib.Path(root)
    if not root.exists() or root.is_symlink():
        return
    os.chmod(root, 0o700)
    for current, names, filenames in os.walk(root, followlinks=False):
        current_path = pathlib.Path(current)
        for name in names:
            path = current_path / name
            if not path.is_symlink():
                os.chmod(path, 0o700)
        for name in filenames:
            path = current_path / name
            if not path.is_symlink():
                os.chmod(path, 0o600)


def remove_terminal_success_artifacts(root):
    root = pathlib.Path(root)
    removed = []
    for name in SUCCESS_ARTIFACT_NAMES:
        path = root / name
        if path.exists() or path.is_symlink():
            if path.is_dir() and not path.is_symlink():
                raise DrillError(
                    f"terminal success artifact path is a directory: {path}"
                )
            path.unlink()
            removed.append(name)
        for partial in root.glob(f".{name}.partial-*"):
            if partial.is_dir() and not partial.is_symlink():
                raise DrillError(
                    f"terminal partial marker is a directory: {partial}"
                )
            partial.unlink()
            removed.append(partial.name)
    migration.fsync_directory(root)
    return sorted(removed)


def make_artifact_tree_nonwritable(root, fault_injector=None):
    directories, files = artifact_tree_entries(root)
    injected = False
    for path in files:
        os.chmod(path, 0o444)
        if not injected and fault_injector is not None:
            injected = True
            fault_injector("chmod")
    for path in sorted(directories, key=lambda value: len(value.parts), reverse=True):
        os.chmod(path, 0o555)
    os.chmod(root, 0o555)


def seal_artifacts(root, report, *, fault_injector=None):
    root = pathlib.Path(root)
    stage = "prevalidation"

    def inject(value):
        if fault_injector is not None:
            fault_injector(value)

    try:
        _, existing_files = artifact_tree_entries(root)
        conflicting = [
            path.name
            for path in existing_files
            if path.name in SUCCESS_ARTIFACT_NAMES
            or path.name == SEAL_FAILURE_NAME
        ]
        if conflicting:
            raise DrillError(
                "terminal artifact names already exist: "
                + ", ".join(sorted(conflicting))
            )
        if report.get("passed") is not True:
            raise DrillError("refusing to publish a non-success report")

        report_path = root / "drill-report.json"
        checksums_path = root / "checksums.json"
        seal_path = root / "seal.json"
        report_document = integrity_document(report)
        report_bytes = migration.canonical_json_bytes(report_document)
        files = [
            manifest_entry(path, root)
            for path in existing_files
        ]
        files.append(
            {
                "path": report_path.name,
                "bytes": len(report_bytes),
                "sha256": sha256_bytes(report_bytes),
            }
        )
        files = sorted(files, key=lambda value: value["path"])
        checksums_document = integrity_document(
            {
                "formatVersion": FORMAT_VERSION,
                "toolId": TOOL_ID,
                "createdAtUtc": utc_now(),
                "files": files,
            }
        )
        checksums_bytes = migration.canonical_json_bytes(checksums_document)
        seal_document = integrity_document(
            {
                "formatVersion": FORMAT_VERSION,
                "toolId": TOOL_ID,
                "sealedAtUtc": utc_now(),
                "report": {
                    "path": report_path.name,
                    "sha256": sha256_bytes(report_bytes),
                },
                "checksums": {
                    "path": checksums_path.name,
                    "sha256": sha256_bytes(checksums_bytes),
                },
                "fileCount": len(files),
                "passed": True,
            }
        )
        seal_bytes = migration.canonical_json_bytes(seal_document)

        stage = "report-write"
        inject(stage)
        atomic_publish_bytes(report_path, report_bytes)
        stage = "checksums-write"
        inject(stage)
        atomic_publish_bytes(checksums_path, checksums_bytes)
        stage = "seal-write"
        inject(stage)
        atomic_publish_bytes(seal_path, seal_bytes)

        stage = "initial-verification"
        inject(stage)
        verify_terminal_seal(root, require_nonwritable=False)
        stage = "chmod"
        make_artifact_tree_nonwritable(root, fault_injector=fault_injector)
        stage = "final-verification"
        inject(stage)
        verified = verify_terminal_seal(root, require_nonwritable=True)
        return {
            "report": report_document,
            "reportPath": report_path,
            "checksums": checksums_document,
            "seal": seal_document,
            "sealPath": seal_path,
            "verification": verified,
        }
    except Exception as error:
        try:
            restore_owner_permissions(root)
            removed = remove_terminal_success_artifacts(root)
            failure_path = root / SEAL_FAILURE_NAME
            if failure_path.exists() or failure_path.is_symlink():
                failure_path.unlink()
                migration.fsync_directory(root)
            failure = write_integrity_json(
                failure_path,
                {
                    "formatVersion": FORMAT_VERSION,
                    "toolId": TOOL_ID,
                    "failedAtUtc": utc_now(),
                    "stage": stage,
                    "errorType": type(error).__name__,
                    "error": str(error),
                    "successArtifactsRemoved": removed,
                    "terminalSealPresent": False,
                    "passed": False,
                },
            )
            verify_integrity_document(failure, "seal failure")
            os.chmod(failure_path, 0o444)
            migration.fsync_directory(root)
        except Exception as recovery_error:
            raise DrillError(
                "terminal artifact publication failed at "
                f"{stage} ({error}); failure cleanup also failed: "
                f"{recovery_error}"
            ) from error
        raise DrillError(
            f"terminal artifact publication failed at {stage}: {error}"
        ) from error


def owned_container_names(runner, run_id):
    completed = runner.run(
        docker_engine_command(
            "ps",
            "-a",
            "--filter",
            f"label=fst.tool={TOOL_ID}",
            "--filter",
            f"label=fst.run={run_id}",
            "--format",
            "{{.Names}}",
        ),
        timeout=30,
    )
    return sorted(
        line.strip()
        for line in completed.stdout.splitlines()
        if line.strip()
    )


def cleanup_all_owned_containers(runner, run_id):
    result = {
        "discovered": [],
        "removed": [],
        "failures": [],
    }
    try:
        result["discovered"] = owned_container_names(runner, run_id)
    except Exception as error:
        result["failures"].append(
            {
                "container": None,
                "stage": "inventory",
                "errorType": type(error).__name__,
                "error": str(error),
            }
        )
        return result

    for name in result["discovered"]:
        try:
            inspected = inspect_container(runner, name)
            labels = (inspected.get("Config") or {}).get("Labels") or {}
            role = labels.get("fst.role")
            if not role:
                raise DrillError(
                    f"owned container {name} lacks its role label"
                )
            assert_owned_container(inspected, run_id, role)
            runner.run(
                docker_engine_command("rm", "-f", "-v", name),
                timeout=120,
            )
            result["removed"].append(name)
        except Exception as error:
            result["failures"].append(
                {
                    "container": name,
                    "stage": "inspect-or-remove",
                    "errorType": type(error).__name__,
                    "error": str(error),
                }
            )
    return result


def cleanup_owned_container_passes(runner, run_id, *, max_passes=3):
    if max_passes < 2:
        raise DrillError("owned-container cleanup requires repeated passes")
    passes = []
    all_removed = []
    all_failures = []
    final_inventory = None
    for number in range(1, max_passes + 1):
        result = cleanup_all_owned_containers(runner, run_id)
        result["pass"] = number
        passes.append(result)
        all_removed.extend(result["removed"])
        all_failures.extend(result["failures"])
        try:
            final_inventory = owned_container_names(runner, run_id)
        except Exception as error:
            final_inventory = None
            all_failures.append(
                {
                    "container": None,
                    "stage": f"post-pass-{number}-inventory",
                    "errorType": type(error).__name__,
                    "error": str(error),
                }
            )
            continue
        if not final_inventory and number >= 2:
            break
    return {
        "passes": passes,
        "removed": sorted(set(all_removed)),
        "failures": all_failures,
        "finalInventory": final_inventory,
        "inventoryVerified": final_inventory is not None,
        "empty": final_inventory == [],
    }


def require_empty_owned_inventory(evidence):
    if evidence.get("inventoryVerified") is not True:
        raise DrillError(
            "final owned-container inventory could not be verified: "
            + "; ".join(
                f"{item['stage']}: {item['error']}"
                for item in evidence.get("failures") or []
            )
        )
    remaining = evidence.get("finalInventory") or []
    if remaining:
        failures = "; ".join(
            (
                f"{item.get('container') or '<inventory>'}"
                f"@{item['stage']}: {item['error']}"
            )
            for item in evidence.get("failures") or []
        )
        suffix = f"; failures={failures}" if failures else ""
        raise DrillError(
            "owned transient containers remain after repeated cleanup: "
            + ", ".join(remaining)
            + suffix
        )
    return True


def ensure_no_owned_containers(runner, run_id):
    names = owned_container_names(runner, run_id)
    if names:
        raise DrillError(
            "owned transient containers remain: " + ", ".join(names)
        )
    return True


def validate_row_counts(args):
    values = {
        "target": args.target_rows,
        "previous": args.previous_rows,
        "current": args.current_rows,
        "working": args.working_rows,
    }
    for key, value in values.items():
        if value < 1_000 or value > 250_000:
            raise DrillError(
                f"{key} row count must be between 1,000 and 250,000"
            )
    if sum(values.values()) > 500_000:
        raise DrillError("synthetic fixture is bounded to 500,000 rows")
    return values


def run_drill(args):
    runner = DockerFenceRunner(migration.Runner())
    path_evidence = validate_work_root(
        runner,
        args.work_root,
        expected_device_id=args.expected_device_id,
        expected_device_uuid=args.expected_device_uuid,
    )
    initial_docker_evidence = validate_docker_identity(
        runner,
        expected_context=args.expected_docker_context,
        expected_daemon_id=args.expected_daemon_id,
    )
    runner.authorize_docker_mutations(initial_docker_evidence)
    initial_image_evidence = local_image_evidence(runner, args.image)
    immutable_image_id = initial_image_evidence["immutableId"]
    runner.authorize_container_image(immutable_image_id)
    volumes_before = docker_volume_inventory(runner)

    root = pathlib.Path(path_evidence["resolvedPath"])
    root.mkdir(exist_ok=True, mode=0o700)
    root.chmod(0o700)
    path_evidence = validate_created_work_root(runner, root, path_evidence)
    run_id = args.run_id or (
        "snapshot-generation-retention-"
        + datetime.now(timezone.utc).strftime("%Y%m%dT%H%M%SZ")
        + "-"
        + secrets.token_hex(4)
    )
    run_id = validate_run_id(run_id)
    token = run_token(run_id)
    target = TARGET_BY_KEY[args.instrument]
    row_counts = validate_row_counts(args)
    repository_commit = runner.run(
        [
            "git",
            "-C",
            str(SCRIPT_DIR.parent),
            "rev-parse",
            "HEAD",
        ],
        timeout=30,
    ).stdout.strip()
    source_evidence = {
        "repositoryBaseCommit": repository_commit,
        "toolPath": str(pathlib.Path(__file__).resolve()),
        "toolSha256": sha256_path(pathlib.Path(__file__).resolve()),
        "migrationPrimitivePath": str(MIGRATION_SCRIPT),
        "migrationPrimitiveSha256": sha256_path(MIGRATION_SCRIPT),
    }
    runtime_root = root / "runtime"
    evidence_root = root / "evidence"
    runtime_root.mkdir(mode=0o700)
    evidence_root.mkdir(mode=0o700)
    source_pgdata = create_empty_runtime_directory(
        runtime_root,
        "source-pgdata",
    )
    source_role = "source"
    source_container = container_name(token, source_role)
    source_inspect = None
    source_pgdata_bind = None
    source_cleanup = None
    fixture = None
    archive_restore = None
    mailbox = None
    strategies = None
    volume_evidence = None
    finalization_errors = []
    finalization_removed_containers = []
    initial_container_cleanup = None
    terminal_container_cleanup = None
    terminal_revalidation = None
    docker_command_evidence = None
    started = time.monotonic()
    failure = None
    try:
        source_inspect, source_pgdata_bind = start_postgres(
            runner,
            container=source_container,
            pgdata=source_pgdata,
            work_root=root,
            image=immutable_image_id,
            run_id=run_id,
            role=source_role,
        )
        source_database = database_for(runner, source_container)
        source_database.psql(
            fixture_sql(target, row_counts),
            timeout=1800,
        )
        version = scalar_int(
            source_database,
            "SELECT current_setting('server_version_num')::integer",
        )
        if version // 10000 != POSTGRES_MAJOR:
            raise DrillError(f"fixture PostgreSQL major differs: {version}")
        fixture = fixture_inventory(source_database, target)
        fixture["rowCounts"] = row_counts
        fixture["totalRows"] = sum(row_counts.values())
        fixture["postgresVersionNumber"] = version
        fixture["sourceContainer"] = {
            **container_isolation_evidence(source_inspect),
            "pgdataBind": source_pgdata_bind,
        }
        write_integrity_json(
            evidence_root / "fixture-inventory.json",
            {
                "formatVersion": FORMAT_VERSION,
                "toolId": TOOL_ID,
                **fixture,
            },
        )

        archive_restore = archive_and_restore(
            runner,
            root=root,
            target=target,
            source_container=source_container,
            source_database=source_database,
            image=immutable_image_id,
            run_id=run_id,
            token=token,
            source_pgdata=source_pgdata,
        )
        mailbox = prove_mailbox_contract(
            runner,
            root=root,
            image=immutable_image_id,
            run_id=run_id,
            token=token,
            archive_path=archive_restore["archivePath"],
            source_fence=archive_restore[
                "archiveManifest"
            ]["sourceFenceAfter"],
        )
        strategies = strategy_comparison(
            runner,
            root=root,
            target=target,
            database=source_database,
            expected_reference=fixture["referenceParity"],
        )
    except Exception as error:
        failure = error
    finally:
        initial_container_cleanup = cleanup_owned_container_passes(
            runner,
            run_id,
        )
        finalization_removed_containers.extend(
            initial_container_cleanup["removed"]
        )

        for directory, role in (
            (source_pgdata, "source-pgdata"),
            (runtime_root / "restore-pgdata", "restore-pgdata"),
        ):
            if not directory.exists():
                continue
            try:
                cleanup = cleanup_owned_directory(
                    runner,
                    immutable_image_id,
                    directory,
                    runtime_root,
                    root,
                    run_id,
                    token,
                    role,
                )
                if role == "source-pgdata":
                    source_cleanup = cleanup
            except Exception as error:
                finalization_errors.append(error)

        terminal_container_cleanup = cleanup_owned_container_passes(
            runner,
            run_id,
        )
        finalization_removed_containers.extend(
            terminal_container_cleanup["removed"]
        )

        for name in (
            "toc-pgdata",
            "prover-torn-pgdata",
            "prover-digest-pgdata",
            "prover-good-pgdata",
            "prover-resume-pgdata",
            "cleanup-source-pgdata-container-pgdata",
            "cleanup-restore-pgdata-container-pgdata",
        ):
            path = runtime_root / name
            if not path.exists():
                continue
            try:
                remove_empty_runtime_directory(runtime_root, path, name)
            except Exception as error:
                finalization_errors.append(error)

        try:
            require_empty_owned_inventory(terminal_container_cleanup)
        except Exception as error:
            finalization_errors.append(error)

        if runtime_root.exists():
            try:
                remaining = sorted(
                    entry.name for entry in runtime_root.iterdir()
                )
                if remaining:
                    raise DrillError(
                        "runtime directories remain after cleanup: "
                        + ", ".join(remaining)
                    )
                runtime_root.rmdir()
                migration.fsync_directory(runtime_root.parent)
            except Exception as error:
                finalization_errors.append(error)

        try:
            volumes_after = docker_volume_inventory(runner)
            try:
                volume_evidence = require_zero_volume_delta(
                    volumes_before,
                    volumes_after,
                )
            except DrillError:
                volume_evidence = volume_delta_evidence(
                    volumes_before,
                    volumes_after,
                )
                raise
        except Exception as error:
            finalization_errors.append(error)

        if failure is None and not finalization_errors:
            try:
                terminal_revalidation = revalidate_terminal_docker_and_image(
                    runner,
                    initial_docker=initial_docker_evidence,
                    initial_image=initial_image_evidence,
                    expected_context=args.expected_docker_context,
                    expected_daemon_id=args.expected_daemon_id,
                )
                docker_command_evidence = runner.docker_command_evidence()
                if docker_command_evidence.get("passed") is not True:
                    raise DrillError(
                        "Docker Engine command pinning evidence did not pass"
                    )
            except Exception as error:
                finalization_errors.append(error)

        if (failure is not None or finalization_errors) and root.exists():
            with contextlib.suppress(Exception):
                write_integrity_json(
                    root / "failure.json",
                    {
                        "formatVersion": FORMAT_VERSION,
                        "toolId": TOOL_ID,
                        "failedAtUtc": utc_now(),
                        "errorType": (
                            type(failure).__name__
                            if failure is not None
                            else type(finalization_errors[0]).__name__
                        ),
                        "error": (
                            str(failure)
                            if failure is not None
                            else str(finalization_errors[0])
                        ),
                        "finalizationErrors": [
                            str(error) for error in finalization_errors
                        ],
                        "ownedContainerCleanup": {
                            "initial": initial_container_cleanup,
                            "terminal": terminal_container_cleanup,
                        },
                        "dockerVolumes": volume_evidence,
                        "terminalRevalidation": terminal_revalidation,
                        "dockerCommands": docker_command_evidence,
                    },
                )

    if failure is not None:
        if finalization_errors:
            raise DrillError(
                f"drill failed ({failure}); finalization also failed: "
                + "; ".join(str(error) for error in finalization_errors)
            ) from failure
        raise failure
    if finalization_errors:
        raise DrillError(
            "drill finalization failed: "
            + "; ".join(str(error) for error in finalization_errors)
        )

    if source_pgdata.exists():
        raise DrillError("source PGDATA remains after drill cleanup")
    if terminal_revalidation is None:
        raise DrillError("terminal Docker/image revalidation is unavailable")
    artifact_bytes_before_seal = directory_size(root)
    report = {
        "formatVersion": FORMAT_VERSION,
        "toolId": TOOL_ID,
        "runId": run_id,
        "completedAtUtc": utc_now(),
        "docker": terminal_revalidation["docker"],
        "dockerCommands": docker_command_evidence,
        "dockerVolumes": volume_evidence,
        "postgresImage": terminal_revalidation["image"],
        "postgresMajor": POSTGRES_MAJOR,
        "source": source_evidence,
        "workRoot": path_evidence,
        "productionMutation": False,
        "productionComposeTouched": False,
        "alternateEightTbDriveUsed": False,
        "fixture": fixture,
        "archiveRestore": {
            "archive": archive_restore["archiveManifest"]["archive"],
            "restoreProof": archive_restore["restoreReport"],
        },
        "mailboxProver": mailbox,
        "dropStrategies": strategies,
        "cleanup": {
            "sourceContainerRemoved": (
                source_container
                not in (terminal_container_cleanup["finalInventory"] or [])
            ),
            "sourcePgdataRemoved": not source_pgdata.exists(),
            "sourceCleanupContainer": source_cleanup,
            "restoreContainerAndPgdataRemoved":
                archive_restore["restoreReport"]["cleanup"],
            "ownedContainerCleanup": {
                "initial": initial_container_cleanup,
                "terminal": terminal_container_cleanup,
            },
            "ownedContainersRemovedDuringFinalization": sorted(
                set(finalization_removed_containers)
            ),
            "ownedContainersRemaining": len(
                terminal_container_cleanup["finalInventory"] or []
            ),
            "runtimeDirectoryRemoved": not runtime_root.exists(),
            "dockerVolumeDeltaZero": volume_evidence["zeroDelta"],
        },
        "artifactBytesBeforeSeal": artifact_bytes_before_seal,
        "elapsedSeconds": round(time.monotonic() - started, 6),
        "decision": (
            "accepted isolated drill capability; production recurring "
            "retention remains unimplemented and unauthorized"
        ),
        "passed": True,
    }
    sealed = seal_artifacts(root, report)
    return {
        "status": "succeeded",
        "workRoot": str(root),
        "report": str(sealed["reportPath"]),
        "reportSha256": sha256_path(sealed["reportPath"]),
        "seal": str(sealed["sealPath"]),
        "sealSha256": sha256_path(sealed["sealPath"]),
    }


def build_parser():
    parser = argparse.ArgumentParser(
        description=(
            "Run the isolated PostgreSQL 17 generation-retention safety drill."
        )
    )
    parser.add_argument(
        "--work-root",
        required=True,
        help=(
            "New/empty run directory beneath the fixed 4 TB autonomous "
            "artifact root."
        ),
    )
    parser.add_argument(
        "--expected-device-id",
        required=True,
        help="Expected MAJ:MIN identity for the mounted 4 TB FST device.",
    )
    parser.add_argument(
        "--expected-device-uuid",
        required=True,
        help="Expected filesystem UUID for the mounted 4 TB FST device.",
    )
    parser.add_argument(
        "--expected-docker-context",
        required=True,
        help=(
            "Expected active local Docker context; its endpoint must still be "
            f"{DOCKER_ENDPOINT}."
        ),
    )
    parser.add_argument(
        "--expected-daemon-id",
        required=True,
        help="Expected Docker daemon ID from read-only docker info.",
    )
    parser.add_argument(
        "--instrument",
        choices=sorted(TARGET_BY_KEY),
        default="solo-guitar",
        help="Fixed allowlisted synthetic instrument shape.",
    )
    parser.add_argument("--image", default=POSTGRES_IMAGE)
    parser.add_argument("--run-id")
    parser.add_argument("--target-rows", type=int, default=40_000)
    parser.add_argument("--previous-rows", type=int, default=12_000)
    parser.add_argument("--current-rows", type=int, default=14_000)
    parser.add_argument("--working-rows", type=int, default=16_000)
    return parser


def main(argv=None):
    args = build_parser().parse_args(argv)
    result = run_drill(args)
    print(json.dumps(result, sort_keys=True))
    return 0


if __name__ == "__main__":
    try:
        sys.exit(main())
    except (
        DrillError,
        migration.MigrationError,
        subprocess.TimeoutExpired,
    ) as error:
        print(f"ERROR: {error}", file=sys.stderr)
        sys.exit(3)
