import importlib.util
import json
import os
import pathlib
import re
import secrets
import shutil
import stat
import sys
import unittest
from unittest import mock


TOOLS_DIR = pathlib.Path(__file__).resolve().parent
SCRIPT = TOOLS_DIR / "postgres-snapshot-generation-retention-drill.py"
FIXED_TEST_ROOT_PARENT = (
    TOOLS_DIR
    / "testdata"
    / "postgres-snapshot-generation-retention"
)
TEST_ROOT_MARKER = ".fst-retention-drill-test-root.json"
TEST_ROOT_TOOL_ID = "fst.snapshot-generation-retention-tests.v1"
TEST_ROOT_NAME = re.compile(r"\.work-[0-9]+-[0-9a-f]{16}")


def load_tool():
    spec = importlib.util.spec_from_file_location(
        "postgres_snapshot_generation_retention_drill",
        SCRIPT,
    )
    module = importlib.util.module_from_spec(spec)
    sys.modules[spec.name] = module
    spec.loader.exec_module(module)
    return module


tool = load_tool()
TEST_IMAGE_ID = "sha256:" + ("1" * 64)


def validate_owned_test_root(path, token):
    path = pathlib.Path(path)
    if not path.is_absolute():
        raise RuntimeError("test root must be absolute")
    if not FIXED_TEST_ROOT_PARENT.is_dir():
        raise RuntimeError("fixed testdata parent is unavailable")
    tool.migration.validate_no_symlink_components(FIXED_TEST_ROOT_PARENT)
    parent = FIXED_TEST_ROOT_PARENT.resolve(strict=True)
    if path == parent or path == pathlib.Path("/"):
        raise RuntimeError("refusing broad test-root cleanup")
    if path.parent.resolve(strict=True) != parent:
        raise RuntimeError("test root is outside the fixed testdata parent")
    if not TEST_ROOT_NAME.fullmatch(path.name):
        raise RuntimeError("test root has an unexpected name")
    metadata = path.lstat()
    if stat.S_ISLNK(metadata.st_mode) or not stat.S_ISDIR(metadata.st_mode):
        raise RuntimeError("test root is not a real directory")
    tool.migration.validate_no_symlink_components(path)
    for entry in path.rglob("*"):
        if entry.is_symlink():
            raise RuntimeError("test root contains a symbolic link")
    marker_path = path / TEST_ROOT_MARKER
    try:
        marker_metadata = marker_path.lstat()
    except FileNotFoundError as error:
        raise RuntimeError("test-root ownership marker is missing") from error
    if (
        stat.S_ISLNK(marker_metadata.st_mode)
        or not stat.S_ISREG(marker_metadata.st_mode)
    ):
        raise RuntimeError("test-root ownership marker is not a regular file")
    marker = json.loads(marker_path.read_text())
    expected = {
        "toolId": TEST_ROOT_TOOL_ID,
        "token": token,
        "rootName": path.name,
    }
    if marker != expected:
        raise RuntimeError("test-root ownership marker mismatch")
    return path


def create_owned_test_root():
    FIXED_TEST_ROOT_PARENT.mkdir(parents=True, exist_ok=True, mode=0o700)
    tool.migration.validate_no_symlink_components(FIXED_TEST_ROOT_PARENT)
    token = secrets.token_hex(8)
    path = (
        FIXED_TEST_ROOT_PARENT
        / f".work-{os.getpid()}-{token}"
    ).resolve(strict=False)
    path.mkdir(mode=0o700)
    marker = {
        "toolId": TEST_ROOT_TOOL_ID,
        "token": token,
        "rootName": path.name,
    }
    marker_path = path / TEST_ROOT_MARKER
    with marker_path.open("x", encoding="utf-8") as handle:
        json.dump(marker, handle, sort_keys=True)
        handle.write("\n")
    return path, token


def cleanup_owned_test_root(path, token):
    path = validate_owned_test_root(path, token)
    for entry in sorted(path.rglob("*"), reverse=True):
        entry.chmod(0o700 if entry.is_dir() else 0o600)
    shutil.rmtree(path)
    return not path.exists()


class Completed:
    def __init__(self, stdout="", stderr="", returncode=0):
        self.stdout = stdout
        self.stderr = stderr
        self.returncode = returncode


class MountRunner:
    def __init__(
        self,
        *,
        mount_root,
        source="/dev/fst-test",
        filesystem="ext4",
        device_id="259:1",
        uuid="fixture-uuid",
        total_bytes=4_000_000_000_000,
        overrides=None,
    ):
        self.mount_root = pathlib.Path(mount_root).resolve()
        self.source = source
        self.filesystem = filesystem
        self.device_id = device_id
        self.uuid = uuid
        self.total_bytes = total_bytes
        self.overrides = {
            pathlib.Path(path).resolve(): value
            for path, value in (overrides or {}).items()
        }

    def run(self, arguments, **_):
        if arguments[0] != "findmnt":
            raise AssertionError(f"unexpected command: {arguments}")
        path = pathlib.Path(arguments[arguments.index("-T") + 1]).resolve()
        values = {
            "source": self.source,
            "filesystem": self.filesystem,
            "device_id": self.device_id,
            "target": str(self.mount_root),
            "uuid": self.uuid,
            "total_bytes": self.total_bytes,
        }
        values.update(self.overrides.get(path, {}))
        return Completed(
            f"{values['source']} {values['filesystem']} "
            f"{values['device_id']} {values['target']} "
            f"{values['uuid']} {values['total_bytes']}\n"
        )


class DockerIdentityRunner:
    def __init__(
        self,
        *,
        context="default",
        endpoint=tool.DOCKER_ENDPOINT,
        daemon_id="daemon-id",
        image_id=TEST_IMAGE_ID,
    ):
        self.context = context
        self.endpoint = endpoint
        self.daemon_id = daemon_id
        self.image_id = image_id
        self.present_image_ids = {image_id}
        self.calls = []

    def run(self, arguments, **_):
        arguments = [str(value) for value in arguments]
        self.calls.append(arguments)
        invocation = tool.parse_docker_invocation(arguments)
        command = invocation["command"]
        if invocation["isContext"] and command[:2] == ["context", "show"]:
            return Completed(self.context + "\n")
        if invocation["isContext"] and command[:2] == ["context", "inspect"]:
            return Completed(
                json.dumps(
                    [
                        {
                            "Endpoints": {
                                "docker": {
                                    "Host": self.endpoint,
                                }
                            }
                        }
                    ]
                )
            )
        if command[:1] == ["info"]:
            return Completed(json.dumps(self.daemon_id) + "\n")
        if command[:2] == ["image", "inspect"]:
            reference = command[2]
            if reference == tool.POSTGRES_IMAGE:
                image_id = self.image_id
            elif reference in self.present_image_ids:
                image_id = reference
            else:
                return Completed(stderr="not found", returncode=1)
            return Completed(
                json.dumps(
                    [
                        {
                            "Id": image_id,
                            "RepoDigests": [
                                "postgres@sha256:" + ("2" * 64)
                            ],
                        }
                    ]
                )
            )
        if command[:1] in (["create"], ["run"], ["exec"]):
            return Completed()
        raise AssertionError(f"unexpected command: {arguments}")


class ProverRunner:
    CONTAINER_ID = "a" * 64

    def __init__(self, *, fail_stage=None, prover_exit=0):
        self.fail_stage = fail_stage
        self.prover_exit = prover_exit
        self.calls = []
        self.exists = False
        self.name = None
        self.labels = {}
        self.mounts = []
        self.inspect_count = 0
        self.image_id = TEST_IMAGE_ID

    def _inspect_value(self):
        return {
            "Config": {
                "Labels": self.labels,
                "User": f"{os.getuid()}:{os.getgid()}",
            },
            "HostConfig": {
                "NetworkMode": "none",
                "ReadonlyRootfs": True,
            },
            "Image": self.image_id,
            "Mounts": self.mounts,
            "State": {
                "ExitCode": self.prover_exit,
            },
        }

    def _capture_container(self, arguments):
        self.name = arguments[arguments.index("--name") + 1]
        self.labels = {}
        for index, value in enumerate(arguments):
            if value == "--label":
                key, label_value = arguments[index + 1].split("=", 1)
                self.labels[key] = label_value
        self.mounts = []
        for index, value in enumerate(arguments):
            if value != "-v":
                continue
            source, destination, *options = arguments[index + 1].split(":")
            self.mounts.append(
                {
                    "Source": source,
                    "Destination": destination,
                    "Type": "bind",
                    "RW": "ro" not in options,
                }
            )
        image_ids = [
            value
            for value in arguments
            if tool.IMAGE_ID_PATTERN.fullmatch(value)
        ]
        if image_ids:
            self.image_id = image_ids[-1]
        self.exists = True

    def run(self, arguments, **_):
        arguments = [str(value) for value in arguments]
        self.calls.append(arguments)
        invocation = tool.parse_docker_invocation(arguments)
        command = invocation["command"]
        if command[:1] == ["inspect"]:
            if not self.exists:
                return Completed(stderr="not found", returncode=1)
            self.inspect_count += 1
            if (
                self.fail_stage == "inspect"
                and self.inspect_count == 1
            ):
                raise RuntimeError("simulated inspect failure")
            if (
                self.fail_stage == "post-inspect"
                and self.inspect_count == 2
            ):
                raise RuntimeError("simulated post-prover inspect failure")
            return Completed(json.dumps([self._inspect_value()]))
        if command[:1] in (["create"], ["run"]):
            self._capture_container(command)
            if self.fail_stage == "create":
                raise RuntimeError("simulated create failure")
            return Completed(self.CONTAINER_ID + "\n")
        if command[:2] == ["start", "-a"]:
            if self.fail_stage == "start":
                raise RuntimeError("simulated start failure")
            for mount in self.mounts:
                if mount["Destination"] != "/owned":
                    continue
                owned = pathlib.Path(mount["Source"])
                for entry in list(owned.iterdir()):
                    if entry.is_dir():
                        shutil.rmtree(entry)
                    else:
                        entry.unlink()
            return Completed(
                stdout='{"status":"simulated"}\n',
                returncode=self.prover_exit,
            )
        if command[:2] == ["rm", "-f"]:
            self.exists = False
            return Completed()
        raise AssertionError(f"unexpected command: {arguments}")


class MultiContainerRunner:
    def __init__(
        self,
        names,
        *,
        inspect_failures=None,
        remove_failures=None,
    ):
        self.containers = {
            name: {
                "Config": {
                    "Labels": tool.owned_labels("run", f"role-{name}"),
                }
            }
            for name in names
        }
        self.inspect_failures = dict(inspect_failures or {})
        self.remove_failures = dict(remove_failures or {})
        self.calls = []

    def _fail_once(self, failures, name):
        remaining = failures.get(name, 0)
        if remaining <= 0:
            return False
        failures[name] = remaining - 1
        return True

    def run(self, arguments, **_):
        arguments = [str(value) for value in arguments]
        self.calls.append(arguments)
        command = tool.parse_docker_invocation(arguments)["command"]
        if command[:2] == ["ps", "-a"]:
            return Completed("\n".join(sorted(self.containers)) + "\n")
        if command[:1] == ["inspect"]:
            name = command[1]
            if self._fail_once(self.inspect_failures, name):
                raise RuntimeError(f"inspect failed for {name}")
            if name not in self.containers:
                return Completed(stderr="not found", returncode=1)
            return Completed(json.dumps([self.containers[name]]))
        if command[:2] == ["rm", "-f"]:
            name = command[-1]
            if self._fail_once(self.remove_failures, name):
                raise RuntimeError(f"remove failed for {name}")
            self.containers.pop(name, None)
            return Completed()
        raise AssertionError(f"unexpected command: {arguments}")


def source_state():
    return {
        "oid": 100,
        "relfilenode": 200,
        "heapBytes": 1_000_000,
        "indexBytes": 500_000,
        "totalBytes": 1_500_000,
        "inserts": 10,
        "updates": 0,
        "deletes": 0,
    }


def reference_fixture():
    return {
        "publication": {
            "publishedScrapeId": tool.CURRENT_SNAPSHOT_ID,
            "currentPublicationId": 202,
            "previousPublicationId": 201,
            "workingPublicationId": 203,
            "publicReadsFrozen": True,
        },
        "missingNamedSourceRows": 0,
        "activeStateMissingRows": 0,
        "projectionMissingRows": 0,
        "invalidCurrentEmptySourceRows": 0,
        "namedSourceCount": 3,
        "namedSourceFingerprint": "named",
        "currentEmptySourceCount": 1,
        "currentEmptySourceFingerprint": "empty",
        "activeStateFingerprint": "active",
        "projectionFingerprint": "projection",
    }


def toc_catalogs(target):
    relations = tool.selected_archive_relations(target)
    return {
        relation: {
            "constraints": [
                {
                    "name": f"{relation}_pkey",
                }
            ],
            "indexes": [
                {
                    "name": f"{relation}_pkey",
                },
                {
                    "name": f"{relation}_score_idx",
                },
            ],
        }
        for relation in relations
    }


def valid_toc(target):
    relations = tool.selected_archive_relations(target)
    lines = []
    identifier = 100
    for relation in relations:
        lines.append(
            f"{identifier}; 1259 {identifier} TABLE public "
            f"{relation} fst"
        )
        identifier += 1
    target_child = tool.migration.generation_child_name(
        target,
        tool.TARGET_SNAPSHOT_ID,
    )
    default_child = tool.migration.default_child_name(target)
    lines.extend(
        [
            (
                f"{identifier}; 0 {identifier} TABLE DATA public "
                f"{target_child} fst"
            ),
            (
                f"{identifier + 1}; 0 {identifier + 1} TABLE DATA public "
                f"{default_child} fst"
            ),
            (
                f"{identifier + 2}; 2606 {identifier + 2} CONSTRAINT public "
                f"{target_child} {target_child}_pkey fst"
            ),
            (
                f"{identifier + 3}; 1259 {identifier + 3} INDEX public "
                f"{target_child}_score_idx fst"
            ),
        ]
    )
    return "\n".join(lines) + "\n"


class SnapshotGenerationRetentionDrillTests(unittest.TestCase):
    def setUp(self):
        self.work_root, self.work_token = create_owned_test_root()

    def tearDown(self):
        if self.work_root.exists():
            cleanup_owned_test_root(self.work_root, self.work_token)

    def test_reuses_exact_nine_target_allowlist(self):
        self.assertIs(tool.TARGETS, tool.migration.TARGETS)
        self.assertIs(tool.TARGET_BY_KEY, tool.migration.TARGET_BY_KEY)
        self.assertEqual(9, len(tool.TARGETS))
        self.assertEqual(
            {
                "solo-guitar",
                "solo-bass",
                "solo-drums",
                "solo-vocals",
                "pro-guitar",
                "pro-bass",
                "pro-vocals",
                "pro-cymbals",
                "pro-drums",
            },
            set(tool.TARGET_BY_KEY),
        )

    def test_parser_exposes_no_database_relation_or_sql_target(self):
        parser = tool.build_parser()
        destinations = {action.dest for action in parser._actions}
        self.assertNotIn("container", destinations)
        self.assertNotIn("database", destinations)
        self.assertNotIn("relation", destinations)
        self.assertNotIn("table", destinations)
        self.assertNotIn("sql", destinations)
        instrument = next(
            action
            for action in parser._actions
            if action.dest == "instrument"
        )
        self.assertEqual(sorted(tool.TARGET_BY_KEY), instrument.choices)
        required = {
            action.dest
            for action in parser._actions
            if action.required
        }
        self.assertTrue(
            {
                "work_root",
                "expected_device_id",
                "expected_device_uuid",
                "expected_docker_context",
                "expected_daemon_id",
            }.issubset(required)
        )

    def test_run_id_is_bounded_and_safe(self):
        self.assertEqual(
            "phase1-safe_run.1",
            tool.validate_run_id("phase1-safe_run.1"),
        )
        for value in ("", "-bad", "bad/value", "bad value", "x" * 129):
            with self.subTest(value=value):
                with self.assertRaises(tool.DrillError):
                    tool.validate_run_id(value)

    def test_path_and_device_fences_require_dedicated_four_tb_child(self):
        mount_root = self.work_root / "mount"
        allowed = mount_root / "artifacts"
        mount_root.mkdir()
        allowed.mkdir()
        child = allowed / "run"
        result = tool.validate_work_root(
            MountRunner(mount_root=mount_root),
            child,
            allowed_root=allowed,
            mount_root=mount_root,
            expected_device_id="259:1",
            expected_device_uuid="fixture-uuid",
        )
        self.assertEqual(str(child), result["resolvedPath"])
        self.assertEqual("259:1", result["device"]["deviceId"])
        self.assertEqual("fixture-uuid", result["device"]["uuid"])
        self.assertEqual(str(mount_root), result["device"]["mountTarget"])

        outside = self.work_root / "outside"
        with self.assertRaisesRegex(tool.DrillError, "direct child"):
            tool.validate_work_root(
                MountRunner(mount_root=mount_root),
                outside,
                allowed_root=allowed,
                mount_root=mount_root,
                expected_device_id="259:1",
                expected_device_uuid="fixture-uuid",
            )

    def test_mount_fence_rejects_fallback_and_identity_mismatches(self):
        cases = (
            (
                "absent mount fallback",
                {"overrides": {}, "expected_device_id": "259:1"},
                {"/": {"target": "/"}},
                "exact mountpoint",
            ),
            (
                "wrong device",
                {"overrides": {}, "expected_device_id": "999:9"},
                {},
                "device identity mismatch",
            ),
            (
                "wrong uuid",
                {"overrides": {}, "expected_device_uuid": "wrong"},
                {},
                "device UUID mismatch",
            ),
            (
                "wrong source",
                {"overrides": {}, "expected_device_id": "259:1"},
                {"allowed": {"source": "/dev/other"}},
                "exact 4 TB mount",
            ),
            (
                "wrong target",
                {"overrides": {}, "expected_device_id": "259:1"},
                {"/": {"target": "/wrong"}},
                "exact mountpoint",
            ),
            (
                "insufficient capacity",
                {
                    "overrides": {},
                    "expected_device_id": "259:1",
                    "total_bytes": tool.MIN_FST_CAPACITY_BYTES - 1,
                },
                {},
                "capacity",
            ),
            (
                "remote filesystem",
                {
                    "overrides": {},
                    "expected_device_id": "259:1",
                    "filesystem": "nfs4",
                },
                {},
                "local ext4",
            ),
        )
        for label, runner_values, path_overrides, message in cases:
            with self.subTest(label=label):
                mount_root = self.work_root / f"mount-{label.replace(' ', '-')}"
                allowed = mount_root / "artifacts"
                mount_root.mkdir()
                allowed.mkdir()
                overrides = {}
                for key, value in path_overrides.items():
                    path = mount_root if key == "/" else allowed
                    overrides[path] = value
                runner_values = dict(runner_values)
                expected_device_id = runner_values.pop(
                    "expected_device_id",
                    "259:1",
                )
                expected_device_uuid = runner_values.pop(
                    "expected_device_uuid",
                    "fixture-uuid",
                )
                runner_values["overrides"] = {
                    **runner_values.pop("overrides", {}),
                    **overrides,
                }
                with self.assertRaisesRegex(tool.DrillError, message):
                    tool.validate_work_root(
                        MountRunner(
                            mount_root=mount_root,
                            **runner_values,
                        ),
                        allowed / "run",
                        allowed_root=allowed,
                        mount_root=mount_root,
                        expected_device_id=expected_device_id,
                        expected_device_uuid=expected_device_uuid,
                    )

    def test_path_fence_rejects_symlink_component(self):
        mount_root = self.work_root / "mount"
        allowed = mount_root / "allowed"
        real = allowed / "real"
        mount_root.mkdir()
        allowed.mkdir()
        real.mkdir()
        linked = allowed / "linked"
        linked.symlink_to(real, target_is_directory=True)
        try:
            with self.assertRaisesRegex(
                tool.migration.MigrationError,
                "symbolic link",
            ):
                tool.validate_work_root(
                    MountRunner(mount_root=mount_root),
                    linked,
                    allowed_root=allowed,
                    mount_root=mount_root,
                    expected_device_id="259:1",
                    expected_device_uuid="fixture-uuid",
                )
        finally:
            linked.unlink()

    def test_docker_identity_fence_rejects_overrides_and_mismatches(self):
        socket_path = self.work_root / "docker.sock"
        socket_metadata = mock.Mock(st_mode=stat.S_IFSOCK | 0o660)
        with mock.patch.object(
            pathlib.Path,
            "lstat",
            return_value=socket_metadata,
        ):
            evidence = tool.validate_docker_identity(
                DockerIdentityRunner(),
                expected_context="default",
                expected_daemon_id="daemon-id",
                environ={},
                socket_path=socket_path,
            )
        self.assertTrue(evidence["passed"])
        self.assertEqual(tool.DOCKER_ENDPOINT, evidence["endpoint"])

        for variable in ("DOCKER_HOST", "DOCKER_CONTEXT"):
            with self.subTest(variable=variable):
                runner = DockerIdentityRunner()
                with self.assertRaisesRegex(
                    tool.DrillError,
                    "environment overrides",
                ):
                    tool.validate_docker_identity(
                        runner,
                        expected_context="default",
                        expected_daemon_id="daemon-id",
                        environ={variable: "forbidden"},
                        socket_path=socket_path,
                    )
                self.assertEqual([], runner.calls)

        with mock.patch.object(
            pathlib.Path,
            "lstat",
            return_value=socket_metadata,
        ):
            with self.assertRaisesRegex(tool.DrillError, "context mismatch"):
                tool.validate_docker_identity(
                    DockerIdentityRunner(context="other"),
                    expected_context="default",
                    expected_daemon_id="daemon-id",
                    environ={},
                    socket_path=socket_path,
                )
            with self.assertRaisesRegex(tool.DrillError, "endpoint"):
                tool.validate_docker_identity(
                    DockerIdentityRunner(endpoint="tcp://127.0.0.1:2375"),
                    expected_context="default",
                    expected_daemon_id="daemon-id",
                    environ={},
                    socket_path=socket_path,
                )
            with self.assertRaisesRegex(
                tool.DrillError,
                "daemon identity mismatch",
            ):
                tool.validate_docker_identity(
                    DockerIdentityRunner(daemon_id="other"),
                    expected_context="default",
                    expected_daemon_id="daemon-id",
                    environ={},
                    socket_path=socket_path,
                )

    def test_docker_identity_fence_rejects_missing_or_non_socket_path(self):
        missing = self.work_root / "missing.sock"
        with self.assertRaisesRegex(tool.DrillError, "socket is missing"):
            tool.validate_docker_identity(
                DockerIdentityRunner(),
                expected_context="default",
                expected_daemon_id="daemon-id",
                environ={},
                socket_path=missing,
            )

        regular = self.work_root / "not-a-socket"
        regular.write_text("not a socket")
        with self.assertRaisesRegex(tool.DrillError, "not a real Unix socket"):
            tool.validate_docker_identity(
                DockerIdentityRunner(),
                expected_context="default",
                expected_daemon_id="daemon-id",
                environ={},
                socket_path=regular,
            )

    def test_guarded_runner_blocks_docker_mutation_until_authorized(self):
        delegate = mock.Mock()
        delegate.run.return_value = Completed()
        runner = tool.DockerFenceRunner(delegate)
        with self.assertRaisesRegex(tool.DrillError, "before the daemon"):
            runner.run(["docker", "create", TEST_IMAGE_ID])
        with self.assertRaisesRegex(tool.DrillError, "before the daemon"):
            runner.run(["docker", "image", "inspect", tool.POSTGRES_IMAGE])
        runner.run(["docker", "info"])
        runner.authorize_docker_mutations(
            {"passed": True, "endpoint": tool.DOCKER_ENDPOINT}
        )
        runner.authorize_container_image(TEST_IMAGE_ID)
        runner.run(
            ["docker", "create", "--pull=never", TEST_IMAGE_ID]
        )
        self.assertEqual(2, delegate.run.call_count)
        for call in delegate.run.call_args_list:
            self.assertEqual(
                ["docker", "--host", tool.DOCKER_ENDPOINT],
                call.args[0][:3],
            )

    def test_every_engine_transport_is_endpoint_pinned(self):
        delegate = mock.Mock()
        delegate.run.return_value = Completed()
        delegate.run_to_file.return_value = None
        runner = tool.DockerFenceRunner(delegate)
        runner.authorize_docker_mutations(
            {"passed": True, "endpoint": tool.DOCKER_ENDPOINT}
        )
        runner.authorize_container_image(TEST_IMAGE_ID)

        runner.run(["docker", "exec", "container", "true"])
        runner.run_to_file(
            ["docker", "exec", "container", "cat", "/evidence"],
            self.work_root / "unused",
            timeout=30,
        )
        process = mock.Mock()
        with mock.patch.object(
            tool.subprocess,
            "Popen",
            return_value=process,
        ) as popen:
            self.assertIs(
                process,
                runner.popen(
                    ["docker", "exec", "container", "true"],
                    stdin=tool.subprocess.DEVNULL,
                ),
            )
        commands = [
            delegate.run.call_args.args[0],
            delegate.run_to_file.call_args.args[0],
            popen.call_args.args[0],
        ]
        for command in commands:
            self.assertEqual(
                ["docker", "--host", tool.DOCKER_ENDPOINT],
                command[:3],
            )
        self.assertEqual(
            1,
            runner.docker_command_evidence()["popenInvocationCount"],
        )

    def test_container_creation_requires_pull_never_and_immutable_id(self):
        delegate = mock.Mock()
        delegate.run.return_value = Completed()
        runner = tool.DockerFenceRunner(delegate)
        runner.authorize_docker_mutations(
            {"passed": True, "endpoint": tool.DOCKER_ENDPOINT}
        )
        runner.authorize_container_image(TEST_IMAGE_ID)
        with self.assertRaisesRegex(tool.DrillError, "--pull=never"):
            runner.run(["docker", "run", TEST_IMAGE_ID, "true"])
        with self.assertRaisesRegex(tool.DrillError, "immutable image ID"):
            runner.run(
                ["docker", "run", "--pull=never", tool.POSTGRES_IMAGE]
            )
        runner.run(
            ["docker", "run", "--pull=never", TEST_IMAGE_ID, "true"]
        )
        command = delegate.run.call_args.args[0]
        self.assertIn("--pull=never", command)
        self.assertIn(TEST_IMAGE_ID, command)
        self.assertNotIn(tool.POSTGRES_IMAGE, command)
        evidence = runner.docker_command_evidence()
        self.assertTrue(evidence["passed"])
        self.assertEqual(0, evidence["unpinnedEngineInvocationCount"])
        self.assertEqual(1, evidence["containerCreationCount"])
        self.assertEqual(1, evidence["pullNeverContainerCreationCount"])
        self.assertEqual(
            1,
            evidence["immutableImageContainerCreationCount"],
        )

    def test_image_tag_change_cannot_change_execution_and_fails_final_gate(self):
        delegate = DockerIdentityRunner()
        runner = tool.DockerFenceRunner(delegate)
        socket_metadata = mock.Mock(
            st_mode=stat.S_IFSOCK | 0o660,
            st_dev=77,
            st_ino=88,
        )
        with mock.patch.object(
            pathlib.Path,
            "lstat",
            return_value=socket_metadata,
        ):
            initial_docker = tool.validate_docker_identity(
                runner,
                expected_context="default",
                expected_daemon_id="daemon-id",
                environ={},
                socket_path=self.work_root / "docker.sock",
            )
            runner.authorize_docker_mutations(initial_docker)
            initial_image = tool.local_image_evidence(
                runner,
                tool.POSTGRES_IMAGE,
            )
            runner.authorize_container_image(initial_image["immutableId"])
            delegate.image_id = "sha256:" + ("3" * 64)
            delegate.present_image_ids.add(delegate.image_id)
            runner.run(
                [
                    "docker",
                    "run",
                    "--pull=never",
                    initial_image["immutableId"],
                    "true",
                ]
            )
            executed = tool.parse_docker_invocation(
                delegate.calls[-1]
            )["command"]
            self.assertIn(initial_image["immutableId"], executed)
            self.assertNotIn(tool.POSTGRES_IMAGE, executed)
            with self.assertRaisesRegex(
                tool.DrillError,
                "different immutable ID",
            ):
                tool.revalidate_terminal_docker_and_image(
                    runner,
                    initial_docker=initial_docker,
                    initial_image=initial_image,
                    expected_context="default",
                    expected_daemon_id="daemon-id",
                    environ={},
                    socket_path=self.work_root / "docker.sock",
                )

    def test_final_docker_identity_drift_fails_closed(self):
        for drift, message in (
            ("context", "context mismatch"),
            ("daemon", "daemon identity mismatch"),
            ("socket", "identity drifted"),
        ):
            with self.subTest(drift=drift):
                delegate = DockerIdentityRunner()
                runner = tool.DockerFenceRunner(delegate)
                initial_socket = mock.Mock(
                    st_mode=stat.S_IFSOCK | 0o660,
                    st_dev=77,
                    st_ino=88,
                )
                final_socket = (
                    mock.Mock(
                        st_mode=stat.S_IFSOCK | 0o660,
                        st_dev=77,
                        st_ino=99,
                    )
                    if drift == "socket"
                    else initial_socket
                )
                with mock.patch.object(
                    pathlib.Path,
                    "lstat",
                    side_effect=[initial_socket, final_socket],
                ):
                    initial_docker = tool.validate_docker_identity(
                        runner,
                        expected_context="default",
                        expected_daemon_id="daemon-id",
                        environ={},
                        socket_path=self.work_root / "docker.sock",
                    )
                    runner.authorize_docker_mutations(initial_docker)
                    initial_image = tool.local_image_evidence(
                        runner,
                        tool.POSTGRES_IMAGE,
                    )
                    runner.authorize_container_image(
                        initial_image["immutableId"]
                    )
                    if drift == "context":
                        delegate.context = "other"
                    elif drift == "daemon":
                        delegate.daemon_id = "other"
                    with self.assertRaisesRegex(tool.DrillError, message):
                        tool.revalidate_terminal_docker_and_image(
                            runner,
                            initial_docker=initial_docker,
                            initial_image=initial_image,
                            expected_context="default",
                            expected_daemon_id="daemon-id",
                            environ={},
                            socket_path=self.work_root / "docker.sock",
                        )

    def test_local_image_requires_sha256_image_id(self):
        runner = DockerIdentityRunner(image_id="not-an-image-id")
        with self.assertRaisesRegex(tool.DrillError, "immutable sha256"):
            tool.local_image_evidence(runner, tool.POSTGRES_IMAGE)

    def test_owned_test_root_cleanup_rejects_broad_and_unowned_paths(self):
        with self.assertRaisesRegex(RuntimeError, "broad"):
            cleanup_owned_test_root(
                FIXED_TEST_ROOT_PARENT,
                self.work_token,
            )
        with self.assertRaisesRegex(RuntimeError, "broad"):
            cleanup_owned_test_root(pathlib.Path("/"), self.work_token)
        with self.assertRaisesRegex(RuntimeError, "outside"):
            cleanup_owned_test_root(
                self.work_root / "nested",
                self.work_token,
            )

        missing_token = secrets.token_hex(8)
        missing = (
            FIXED_TEST_ROOT_PARENT
            / f".work-{os.getpid()}-{missing_token}"
        )
        missing.mkdir()
        try:
            with self.assertRaisesRegex(RuntimeError, "marker is missing"):
                cleanup_owned_test_root(missing, missing_token)
        finally:
            missing.rmdir()

        unexpected = FIXED_TEST_ROOT_PARENT / "unexpected-name"
        unexpected.mkdir()
        try:
            with self.assertRaisesRegex(RuntimeError, "unexpected name"):
                cleanup_owned_test_root(unexpected, self.work_token)
        finally:
            unexpected.rmdir()

        mismatched, token = create_owned_test_root()
        marker = mismatched / TEST_ROOT_MARKER
        marker.write_text(
            json.dumps(
                {
                    "toolId": TEST_ROOT_TOOL_ID,
                    "token": "wrong",
                    "rootName": mismatched.name,
                }
            )
        )
        try:
            with self.assertRaisesRegex(RuntimeError, "marker mismatch"):
                cleanup_owned_test_root(mismatched, token)
        finally:
            marker.write_text(
                json.dumps(
                    {
                        "toolId": TEST_ROOT_TOOL_ID,
                        "token": token,
                        "rootName": mismatched.name,
                    }
                )
                + "\n"
            )
            cleanup_owned_test_root(mismatched, token)

    def test_owned_test_root_cleanup_removes_only_marker_owned_child(self):
        owned, token = create_owned_test_root()
        (owned / "payload").mkdir()
        (owned / "payload" / "file").write_text("owned")
        self.assertTrue(cleanup_owned_test_root(owned, token))
        self.assertFalse(owned.exists())

        target, target_token = create_owned_test_root()
        link_token = secrets.token_hex(8)
        linked = (
            FIXED_TEST_ROOT_PARENT
            / f".work-{os.getpid()}-{link_token}"
        )
        linked.symlink_to(target, target_is_directory=True)
        try:
            with self.assertRaisesRegex(RuntimeError, "real directory"):
                cleanup_owned_test_root(linked, link_token)
        finally:
            linked.unlink()
            cleanup_owned_test_root(target, target_token)

    def test_candidate_selection_excludes_protected_and_default(self):
        self.assertEqual(
            [tool.TARGET_SNAPSHOT_ID],
            tool.candidate_snapshot_ids(
                tool.FIXTURE_SNAPSHOT_IDS,
                tool.PROTECTED_SNAPSHOT_IDS,
                default_rows=0,
            ),
        )
        with self.assertRaisesRegex(tool.DrillError, "default child"):
            tool.candidate_snapshot_ids(
                tool.FIXTURE_SNAPSHOT_IDS,
                tool.PROTECTED_SNAPSHOT_IDS,
                default_rows=1,
            )
        with self.assertRaisesRegex(tool.DrillError, "protected IDs"):
            tool.candidate_snapshot_ids(
                [tool.TARGET_SNAPSHOT_ID],
                tool.PROTECTED_SNAPSHOT_IDS,
                default_rows=0,
            )

    def test_archive_command_selects_only_parent_root_target_and_default(self):
        target = tool.TARGET_BY_KEY["solo-guitar"]
        command = tool.pg_dump_command("isolated-source", target)
        self.assertEqual(
            ["docker", "--host", tool.DOCKER_ENDPOINT],
            command[:3],
        )
        selected = [
            command[index + 1]
            for index, value in enumerate(command)
            if value == "--table"
        ]
        self.assertEqual(
            [
                f"public.{relation}"
                for relation in tool.selected_archive_relations(target)
            ],
            selected,
        )
        self.assertEqual(4, len(selected))
        self.assertNotIn("--clean", command)

    def test_measured_transaction_popen_arguments_are_endpoint_pinned(self):
        database = mock.Mock(
            container="source",
            user=tool.POSTGRES_USER,
            database=tool.POSTGRES_DATABASE,
        )
        command = tool.measurement_psql_arguments(
            database,
            "SELECT 1",
            "measurement",
        )
        self.assertEqual(
            ["docker", "--host", tool.DOCKER_ENDPOINT],
            command[:3],
        )

    def test_archive_toc_allowlist_rejects_contamination(self):
        target = tool.TARGET_BY_KEY["solo-guitar"]
        toc = valid_toc(target)
        self.assertTrue(
            tool.verify_archive_toc(
                toc,
                target,
                toc_catalogs(target),
            )
        )
        contaminated = (
            toc
            + "999; 0 999 TABLE DATA public "
            "leaderboard_entries_snapshot_solo_bass fst\n"
        )
        with self.assertRaisesRegex(
            tool.DrillError,
            "another instrument",
        ):
            tool.verify_archive_toc(
                contaminated,
                target,
                toc_catalogs(target),
            )

    def test_source_fence_detects_mutation_drift(self):
        before = tool.migration.source_fence_from_state(source_state())
        unchanged = tool.migration.source_fence_from_state(source_state())
        drifted_state = {**source_state(), "inserts": 11}
        drifted = tool.migration.source_fence_from_state(drifted_state)
        self.assertTrue(
            tool.migration.source_fence_matches(before, unchanged)
        )
        self.assertFalse(
            tool.migration.source_fence_matches(before, drifted)
        )

    def test_atomic_mailbox_publication_ignores_torn_request(self):
        requests = self.work_root / "mailbox" / "requests"
        requests.mkdir(parents=True)
        token = "request-token"
        final = requests / f"{token}.request.json"
        partial = requests / f".{final.name}.partial-torn"
        tool.write_torn_bytes(partial, b'{"complete":')
        self.assertEqual([], tool.complete_mailbox_requests(requests))

        value = {
            "toolId": tool.TOOL_ID,
            "requestToken": token,
            "complete": True,
        }
        replace_calls = []
        original_replace = os.replace

        def tracked_replace(source, destination):
            replace_calls.append(
                (
                    pathlib.Path(source).parent,
                    pathlib.Path(destination).parent,
                )
            )
            return original_replace(source, destination)

        with mock.patch.object(os, "replace", side_effect=tracked_replace):
            tool.atomic_publish_json(final, value)
        self.assertEqual([(requests, requests)], replace_calls)
        self.assertEqual([final], tool.complete_mailbox_requests(requests))
        self.assertEqual(value, json.loads(final.read_text()))
        self.assertFalse(
            any(
                path.name.startswith(f".{final.name}.partial-")
                and path != partial
                for path in requests.iterdir()
            )
        )

    def test_mailbox_digest_mismatch_fails_closed(self):
        archive = self.work_root / "archive.custom"
        archive.write_bytes(b"archive")
        token = "fenced-token"
        request = tool.mailbox_request(
            token,
            archive,
            "0" * 64,
            "1" * 64,
        )
        with self.assertRaisesRegex(tool.DrillError, "digest differs"):
            tool.validate_request_payload(request, archive, token)
        request["archiveSha256"] = tool.sha256_path(archive)
        self.assertTrue(
            tool.validate_request_payload(request, archive, token)
        )
        with self.assertRaisesRegex(tool.DrillError, "token fence"):
            tool.validate_request_payload(request, archive, "other-token")

    def test_destructive_sql_has_no_cascade_and_rechecks_references_first(self):
        target = tool.TARGET_BY_KEY["solo-guitar"]
        expected = reference_fixture()
        statements = {
            "direct": tool.direct_drop_sql(target, expected),
            "detach": tool.detach_sql(target, expected),
            "reattach": tool.reattach_sql(target, expected),
            "detachedDrop": tool.detached_drop_sql(target, expected),
        }
        for name, sql in statements.items():
            with self.subTest(name=name):
                self.assertNotIn("CASCADE", sql)
                self.assertLess(sql.index("BEGIN;"), sql.index("observed_reference"))
                if name == "direct":
                    ddl = sql.index("DROP TABLE")
                elif name in ("detach",):
                    ddl = sql.index("DETACH PARTITION")
                elif name == "reattach":
                    ddl = sql.index("ATTACH PARTITION")
                else:
                    ddl = sql.index("DROP TABLE")
                self.assertLess(sql.index("observed_reference"), ddl)
                self.assertLess(sql.index("default child changed"), ddl)

    def test_detach_and_reattach_sql_use_ordinary_bounded_path(self):
        target = tool.TARGET_BY_KEY["solo-guitar"]
        expected = reference_fixture()
        detach = tool.detach_sql(target, expected)
        reattach = tool.reattach_sql(target, expected)
        check = tool.matching_check_sql(
            tool.migration.generation_child_name(
                target,
                tool.TARGET_SNAPSHOT_ID,
            ),
            "fixture_snapshot_check",
        )
        self.assertIn("DETACH PARTITION", detach)
        self.assertNotIn("CONCURRENTLY", detach)
        self.assertIn("ATTACH PARTITION", reattach)
        self.assertIn(
            f"FOR VALUES IN ({tool.TARGET_SNAPSHOT_ID})",
            reattach,
        )
        self.assertIn("NOT VALID", check)
        self.assertIn("VALIDATE CONSTRAINT", check)
        self.assertIn(
            f"snapshot_id = {tool.TARGET_SNAPSHOT_ID}",
            check,
        )

    def test_prover_contract_has_asymmetric_atomic_no_socket_guards(self):
        source = tool.PROVER_SOURCE
        self.assertIn("rename($temporary, $path)", source)
        self.assertIn("$handle->sync", source)
        self.assertIn("sync_dir($directory)", source)
        self.assertIn("/var/run/docker.sock", source)
        self.assertIn("requestsMountWritable", source)
        self.assertIn("archiveMountWritable", source)
        self.assertIn("proofsMountWritable", source)
        self.assertIn("resumed-existing-proof", source)
        self.assertIn("archive-digest-mismatch", source)

    def test_owned_container_removal_includes_volume_cleanup(self):
        runner = ProverRunner()
        runner.exists = True
        runner.name = "owned"
        runner.labels = tool.owned_labels("run", "role")
        self.assertTrue(
            tool.remove_owned_container(runner, "owned", "run", "role")
        )
        self.assertIn(
            tool.docker_engine_command("rm", "-f", "-v", "owned"),
            runner.calls,
        )

    def test_cleanup_attempts_every_container_after_individual_failure(self):
        runner = MultiContainerRunner(
            ["first", "second", "third"],
            inspect_failures={"first": 1},
        )
        result = tool.cleanup_all_owned_containers(runner, "run")
        self.assertEqual(
            ["first", "second", "third"],
            result["discovered"],
        )
        self.assertEqual(["second", "third"], result["removed"])
        self.assertEqual(
            ["first"],
            [
                failure["container"]
                for failure in result["failures"]
            ],
        )
        self.assertEqual({"first"}, set(runner.containers))

    def test_repeated_cleanup_aggregates_errors_and_reaches_empty_inventory(self):
        runner = MultiContainerRunner(
            ["first", "second"],
            remove_failures={"first": 1},
        )
        evidence = tool.cleanup_owned_container_passes(
            runner,
            "run",
            max_passes=3,
        )
        self.assertTrue(evidence["inventoryVerified"])
        self.assertTrue(evidence["empty"])
        self.assertEqual([], evidence["finalInventory"])
        self.assertEqual(["first", "second"], evidence["removed"])
        self.assertEqual(
            ["first"],
            [
                failure["container"]
                for failure in evidence["failures"]
            ],
        )
        self.assertGreaterEqual(len(evidence["passes"]), 2)
        self.assertTrue(tool.require_empty_owned_inventory(evidence))

    def test_final_cleanup_inventory_enforcement_reports_all_failures(self):
        runner = MultiContainerRunner(
            ["first", "second", "third"],
            remove_failures={"first": 5, "second": 5},
        )
        evidence = tool.cleanup_owned_container_passes(
            runner,
            "run",
            max_passes=3,
        )
        self.assertEqual(["first", "second"], evidence["finalInventory"])
        failed_names = {
            failure["container"]
            for failure in evidence["failures"]
        }
        self.assertEqual({"first", "second"}, failed_names)
        self.assertNotIn("third", runner.containers)
        with self.assertRaisesRegex(
            tool.DrillError,
            "first, second.*first@.*second@",
        ):
            tool.require_empty_owned_inventory(evidence)

    def test_prover_uses_controlled_pgdata_bind_and_always_cleans(self):
        runtime_root = self.work_root / "runtime"
        runtime_root.mkdir()
        inputs = self.work_root / "inputs"
        proofs = self.work_root / "proofs"
        inputs.mkdir()
        proofs.mkdir()
        script_path = inputs / "prover.pl"
        request_path = inputs / "request.json"
        archive_path = inputs / "archive.custom"
        script_path.write_text("#!/usr/bin/perl\n")
        request_path.write_text("{}\n")
        archive_path.write_bytes(b"archive")

        for index, failure_stage in enumerate(
            ("create", "inspect", "start", "post-inspect")
        ):
            with self.subTest(failure_stage=failure_stage):
                role = f"prover-failure-{index}"
                runner = ProverRunner(fail_stage=failure_stage)
                with self.assertRaises(RuntimeError):
                    tool.run_prover_container(
                        runner,
                        image=TEST_IMAGE_ID,
                        runtime_root=runtime_root,
                        work_root=self.work_root,
                        run_id="run",
                        token="token",
                        role=role,
                        script_path=script_path,
                        request_path=request_path,
                        archive_path=archive_path,
                        proof_path=proofs / f"{role}.proof.json",
                        rejection_path=proofs / f"{role}.rejected.json",
                        expected_token="request-token",
                    )
                self.assertFalse(runner.exists)
                self.assertFalse(
                    (runtime_root / f"{role}-pgdata").exists()
                )
                self.assertTrue(
                    any(
                        tool.parse_docker_invocation(call)["command"][:3]
                        == ["rm", "-f", "-v"]
                        for call in runner.calls
                    )
                )

        runner = ProverRunner(prover_exit=7)
        result = tool.run_prover_container(
            runner,
            image=TEST_IMAGE_ID,
            runtime_root=runtime_root,
            work_root=self.work_root,
            run_id="run",
            token="token",
            role="prover-exit",
            script_path=script_path,
            request_path=request_path,
            archive_path=archive_path,
            proof_path=proofs / "exit.proof.json",
            rejection_path=proofs / "exit.rejected.json",
            expected_token="request-token",
        )
        self.assertEqual(7, result["exitCode"])
        self.assertTrue(result["removed"])
        self.assertTrue(result["pgdataRemoved"])
        self.assertEqual(
            "bind",
            result["isolation"]["pgdataBind"]["type"],
        )
        self.assertEqual(
            0,
            result["isolation"]["pgdataBind"]["anonymousVolumes"],
        )
        create = next(
            call
            for call in runner.calls
            if tool.parse_docker_invocation(call)["command"][:1]
            == ["create"]
        )
        pgdata_mount = (
            str(runtime_root / "prover-exit-pgdata")
            + ":/var/lib/postgresql/data"
        )
        self.assertIn(pgdata_mount, create)

    def test_pgdata_bind_rejects_anonymous_volume(self):
        pgdata = self.work_root / "runtime" / "pgdata"
        pgdata.mkdir(parents=True)
        inspected = {
            "Mounts": [
                {
                    "Destination": "/var/lib/postgresql/data",
                    "Source": "/docker/volumes/anonymous/_data",
                    "Type": "volume",
                    "RW": True,
                }
            ]
        }
        with self.assertRaisesRegex(tool.DrillError, "anonymous"):
            tool.validate_pgdata_bind(
                inspected,
                pgdata,
                self.work_root,
                "test",
            )

    def test_source_toc_and_cleanup_containers_bind_controlled_pgdata(self):
        runtime_root = self.work_root / "runtime"
        runtime_root.mkdir()

        source_pgdata = tool.create_empty_runtime_directory(
            runtime_root,
            "source-pgdata",
        )
        source_runner = ProverRunner()
        with mock.patch.object(tool, "wait_for_postgres"):
            inspected, source_bind = tool.start_postgres(
                source_runner,
                container="source",
                pgdata=source_pgdata,
                work_root=self.work_root,
                image=TEST_IMAGE_ID,
                run_id="run",
                role="source",
            )
        self.assertEqual("bind", source_bind["type"])
        self.assertFalse(
            any(
                mount["Type"] == "volume"
                for mount in inspected["Mounts"]
            )
        )
        tool.remove_owned_container(
            source_runner,
            "source",
            "run",
            "source",
        )
        tool.remove_empty_runtime_directory(
            runtime_root,
            source_pgdata,
            "source-pgdata",
        )

        archive_root = self.work_root / "archive"
        archive_root.mkdir()
        archive_path = archive_root / "archive.custom"
        archive_path.write_bytes(b"archive")
        toc_runner = ProverRunner()
        toc = tool.run_toc_container(
            toc_runner,
            image=TEST_IMAGE_ID,
            run_id="run",
            token="token",
            archive_root=archive_root,
            archive_path=archive_path,
            runtime_root=runtime_root,
            work_root=self.work_root,
        )
        self.assertEqual(
            "bind",
            toc["isolation"]["pgdataBind"]["type"],
        )
        self.assertTrue(toc["pgdataRemoved"])

        owned = runtime_root / "restore-pgdata"
        owned.mkdir()
        (owned / "root-owned-simulation").write_text("data")
        cleanup_runner = ProverRunner()
        original_iterdir = pathlib.Path.iterdir
        permission_probe_count = 0

        def permission_then_iterdir(path):
            nonlocal permission_probe_count
            if path == owned and permission_probe_count == 0:
                permission_probe_count += 1
                raise PermissionError("simulated container-owned PGDATA")
            return original_iterdir(path)

        with mock.patch.object(
            pathlib.Path,
            "iterdir",
            autospec=True,
            side_effect=permission_then_iterdir,
        ):
            cleanup = tool.cleanup_owned_directory(
                cleanup_runner,
                TEST_IMAGE_ID,
                owned,
                runtime_root,
                self.work_root,
                "run",
                "token",
                "restore-pgdata",
            )
        self.assertTrue(cleanup["targetRemoved"])
        self.assertTrue(cleanup["containerRemoved"])
        self.assertTrue(cleanup["containerPgdataRemoved"])
        self.assertEqual(
            "bind",
            cleanup["containerPgdata"]["type"],
        )

        mutation_commands = [
            call
            for runner in (source_runner, toc_runner, cleanup_runner)
            for call in runner.calls
            if tool.parse_docker_invocation(call)["command"][:1]
            in (["run"], ["create"])
        ]
        self.assertEqual(3, len(mutation_commands))
        for command in mutation_commands:
            invocation = tool.parse_docker_invocation(command)
            self.assertEqual(tool.DOCKER_ENDPOINT, invocation["host"])
            self.assertIn("--pull=never", invocation["command"])
            self.assertIn(TEST_IMAGE_ID, invocation["command"])
            self.assertNotIn(tool.POSTGRES_IMAGE, invocation["command"])
            mounts = [
                command[index + 1]
                for index, value in enumerate(command)
                if value == "-v"
            ]
            self.assertTrue(
                any(
                    mount.endswith(":/var/lib/postgresql/data")
                    for mount in mounts
                )
            )

    def test_zero_volume_delta_is_exact_and_fail_closed(self):
        evidence = tool.require_zero_volume_delta(
            ["named", "anonymous-a"],
            ["anonymous-a", "named"],
        )
        self.assertTrue(evidence["zeroDelta"])
        self.assertEqual([], evidence["added"])
        self.assertEqual([], evidence["removed"])
        with self.assertRaisesRegex(tool.DrillError, "added="):
            tool.require_zero_volume_delta(["named"], ["named", "new"])
        with self.assertRaisesRegex(tool.DrillError, "removed="):
            tool.require_zero_volume_delta(["named", "lost"], ["named"])

    def test_terminal_seal_is_last_and_tree_is_nonwritable(self):
        root = self.work_root / "successful-seal"
        root.mkdir()
        evidence = root / "evidence.json"
        tool.write_integrity_json(
            evidence,
            {
                "formatVersion": tool.FORMAT_VERSION,
                "toolId": tool.TOOL_ID,
                "status": "evidence",
            },
        )
        stages = []
        sealed = tool.seal_artifacts(
            root,
            {
                "formatVersion": tool.FORMAT_VERSION,
                "toolId": tool.TOOL_ID,
                "passed": True,
            },
            fault_injector=stages.append,
        )
        self.assertEqual(
            [
                "report-write",
                "checksums-write",
                "seal-write",
                "initial-verification",
                "chmod",
                "final-verification",
            ],
            stages,
        )
        self.assertTrue(sealed["verification"]["passed"])
        self.assertTrue(
            tool.verify_terminal_seal(
                root,
                require_nonwritable=True,
            )["passed"]
        )
        for path in [root, *root.rglob("*")]:
            self.assertEqual(0, stat.S_IMODE(path.lstat().st_mode) & 0o222)
        tool.restore_owner_permissions(root)

    def test_terminal_publication_faults_remove_success_and_write_failure(self):
        stages = (
            "report-write",
            "checksums-write",
            "seal-write",
            "chmod",
            "final-verification",
        )
        for stage in stages:
            with self.subTest(stage=stage):
                root = self.work_root / f"failed-seal-{stage}"
                root.mkdir()
                (root / "evidence.txt").write_text("evidence\n")

                def inject(observed):
                    if observed == stage:
                        raise OSError(f"simulated {stage} failure")

                with self.assertRaisesRegex(
                    tool.DrillError,
                    f"failed at {stage}",
                ):
                    tool.seal_artifacts(
                        root,
                        {
                            "formatVersion": tool.FORMAT_VERSION,
                            "toolId": tool.TOOL_ID,
                            "passed": True,
                        },
                        fault_injector=inject,
                    )
                for name in tool.SUCCESS_ARTIFACT_NAMES:
                    self.assertFalse((root / name).exists())
                    self.assertEqual(
                        [],
                        list(root.glob(f".{name}.partial-*")),
                    )
                failure_path = root / tool.SEAL_FAILURE_NAME
                failure = tool.read_json(failure_path)
                self.assertEqual(stage, failure["stage"])
                self.assertFalse(failure["passed"])
                self.assertFalse(failure["terminalSealPresent"])
                self.assertEqual(
                    failure["integritySha256"],
                    tool.migration.report_integrity(failure),
                )
                self.assertNotEqual(
                    0,
                    stat.S_IMODE(root.lstat().st_mode) & stat.S_IWUSR,
                )

    def test_terminal_seal_rejects_symlink_without_success_markers(self):
        root = self.work_root / "symlink-seal"
        root.mkdir()
        target = root / "target.txt"
        target.write_text("target\n")
        linked = root / "linked.txt"
        linked.symlink_to(target)
        try:
            with self.assertRaisesRegex(tool.DrillError, "prevalidation"):
                tool.seal_artifacts(
                    root,
                    {
                        "formatVersion": tool.FORMAT_VERSION,
                        "toolId": tool.TOOL_ID,
                        "passed": True,
                    },
                )
            for name in tool.SUCCESS_ARTIFACT_NAMES:
                self.assertFalse((root / name).exists())
            failure = tool.read_json(root / tool.SEAL_FAILURE_NAME)
            self.assertEqual("prevalidation", failure["stage"])
            self.assertEqual(
                failure["integritySha256"],
                tool.migration.report_integrity(failure),
            )
        finally:
            linked.unlink()

    def test_report_integrity_detects_tampering(self):
        report = tool.integrity_document(
            {
                "formatVersion": tool.FORMAT_VERSION,
                "toolId": tool.TOOL_ID,
                "passed": True,
            }
        )
        self.assertEqual(
            report["integritySha256"],
            tool.migration.report_integrity(report),
        )
        tampered = {**report, "passed": False}
        self.assertNotEqual(
            tampered["integritySha256"],
            tool.migration.report_integrity(tampered),
        )

    def test_prover_integrity_rejects_partial_or_tampered_proof(self):
        proof = {
            "status": "proved",
            "requestToken": "token",
            "requestSha256": "a" * 64,
            "archiveSha256": "b" * 64,
            "environment": {
                "networkInterfaces": ["lo"],
                "dockerSocketPresent": False,
                "requestsMountWritable": False,
                "archiveMountWritable": False,
                "proofsMountWritable": True,
            },
        }
        proof["integritySha256"] = tool.prover_integrity(proof)
        self.assertTrue(
            tool.validate_prover_document(
                proof,
                expected_status="proved",
                expected_token="token",
            )
        )
        proof["archiveSha256"] = "c" * 64
        with self.assertRaisesRegex(tool.DrillError, "integrity"):
            tool.validate_prover_document(
                proof,
                expected_status="proved",
                expected_token="token",
            )


if __name__ == "__main__":
    unittest.main()
