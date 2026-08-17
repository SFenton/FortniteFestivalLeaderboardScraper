import copy
import contextlib
import importlib.util
import io
import json
import os
import pathlib
import shutil
import types
import unittest


TOOLS_DIR = pathlib.Path(__file__).resolve().parent
SCRIPT = TOOLS_DIR / "postgres-retire-ix-le-song-rank.py"
WORK_ROOT = (
    TOOLS_DIR
    / "testdata"
    / "postgres-retire-ix-le-song-rank"
    / f".work-{os.getpid()}"
)


def load_tool():
    spec = importlib.util.spec_from_file_location(
        "postgres_retire_ix_le_song_rank",
        SCRIPT,
    )
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


tool = load_tool()


def valid_probe():
    parent_oid = 1000
    indexes = []
    dependencies = []
    for index, spec in enumerate(tool.INDEX_SPECS):
        oid = parent_oid + index
        table_oid = 2000 + index
        parent_index_oid = None if index == 0 else parent_oid
        indexes.append(
            {
                "oid": oid,
                "name": spec["name"],
                "relkind": spec["relkind"],
                "persistence": "p",
                "owner": "fst",
                "tablespace": "pg_default",
                "reloptions": None,
                "tableOid": table_oid,
                "table": spec["table"],
                "tableRelkind": "p" if index == 0 else "r",
                "parentIndexOid": parent_index_oid,
                "definition": spec["definition"],
                "predicate": None,
                "expressions": None,
                "indkey": "1 2 10",
                "indclass": "3126 3126 1978",
                "indcollation": "100 100 0",
                "indoption": "0 0 0",
                "isUnique": False,
                "isPrimary": False,
                "isExclusion": False,
                "isImmediate": True,
                "isClustered": False,
                "isReplicaIdentity": False,
                "isValid": True,
                "isReady": True,
                "isLive": True,
                "bytes": 0 if index == 0 else index * 1000,
                "idxScan": 0,
                "lastIdxScan": None,
            }
        )
        for column in (1, 2, 10):
            dependencies.append(
                {
                    "class_name": "pg_class",
                    "object_oid": oid,
                    "object_sub_id": 0,
                    "ref_class_name": "pg_class",
                    "ref_object_oid": table_oid,
                    "ref_object_sub_id": column,
                    "dependency_type": "a",
                    "object_name": spec["name"],
                    "referenced_name": spec["table"],
                }
            )
        if index != 0:
            dependencies.extend(
                [
                    {
                        "class_name": "pg_class",
                        "object_oid": oid,
                        "object_sub_id": 0,
                        "ref_class_name": "pg_class",
                        "ref_object_oid": parent_oid,
                        "ref_object_sub_id": 0,
                        "dependency_type": "P",
                        "object_name": spec["name"],
                        "referenced_name": "ix_le_song_rank",
                    },
                    {
                        "class_name": "pg_class",
                        "object_oid": oid,
                        "object_sub_id": 0,
                        "ref_class_name": "pg_class",
                        "ref_object_oid": table_oid,
                        "ref_object_sub_id": 0,
                        "dependency_type": "S",
                        "object_name": spec["name"],
                        "referenced_name": spec["table"],
                    },
                ]
            )

    container_base = {
        "composeProject": tool.PRODUCTION_PROJECT,
        "composeWorkingDir": str(tool.PRODUCTION_COMPOSE_DIR),
        "composeFiles": (
            f"{tool.PRODUCTION_COMPOSE_DIR}/docker-compose.yml,"
            f"{tool.PRODUCTION_COMPOSE_DIR}/docker-compose.pia-30.yml"
        ),
        "state": "running",
        "running": True,
        "health": "healthy",
        "mounts": [],
    }
    postgres = {
        **container_base,
        "id": "postgres-container",
        "imageId": "sha256:postgres",
        "imageReference": "fst-postgres:17",
        "composeService": "postgres",
        "mounts": [
            {
                "type": "bind",
                "source": (
                    "/mnt/docker-storage/Docker/"
                    "FestivalServiceTracker/pg-data"
                ),
                "destination": "/var/lib/postgresql/data",
                "readWrite": True,
            }
        ],
    }
    worker = {
        **container_base,
        "id": "worker-container",
        "imageId": "sha256:service",
        "imageReference": "fstservice:test",
        "composeService": "fstworker",
        "state": "created",
        "running": False,
        "health": None,
    }
    service = {
        **container_base,
        "id": "service-container",
        "imageId": "sha256:service",
        "imageReference": "fstservice:test",
        "composeService": "fstservice",
    }
    web = {
        **container_base,
        "id": "web-container",
        "imageId": "sha256:web",
        "imageReference": "festivalweb:test",
        "composeService": "festivalweb",
    }
    return {
        "capturedAtUtc": "2026-08-17T00:00:00Z",
        "host": {
            "composeDir": str(tool.PRODUCTION_COMPOSE_DIR),
            "expectedComposeDir": str(tool.PRODUCTION_COMPOSE_DIR),
            "project": tool.PRODUCTION_PROJECT,
            "storageRoot": str(tool.PRODUCTION_STORAGE_ROOT),
            "filesystem": {
                "totalBytes": 4_000_000_000_000,
                "usedBytes": 3_940_000_000_000,
                "freeBytes": 60_000_000_000,
            },
            "containers": {
                tool.POSTGRES_CONTAINER: postgres,
                tool.WORKER_CONTAINER: worker,
                tool.SERVICE_CONTAINER: service,
                tool.WEB_CONTAINER: web,
            },
            "serviceReady": "Healthy",
            "serviceInfo": {
                "publishedScrapeId": 1302,
                "currentUpdateStatus": "idle",
                "publicReadsFrozen": False,
            },
        },
        "database": {
            "cluster": {
                "checkedAtUtc": "2026-08-17T00:00:00Z",
                "database": tool.DATABASE_NAME,
                "databaseOid": 5,
                "user": tool.DATABASE_USER,
                "serverVersion": "17.9",
                "serverVersionNum": 170009,
                "systemIdentifier": "7623196498058817570",
                "postmasterStartedAt": "2026-08-12T02:33:04Z",
                "databaseStatsReset": None,
            },
            "publication": {
                "currentPublicationId": 80,
                "previousPublicationId": 77,
                "workingPublicationId": None,
                "publishedScrapeId": 1302,
                "publicReadsFrozen": False,
                "frozenScrapeId": None,
                "freezeReason": None,
                "updatedAt": "2026-08-17T04:16:56Z",
            },
            "worker": {
                "status": "offline",
                "lastHeartbeatAt": "2026-08-16T11:48:49Z",
                "message": "Worker service stopped",
            },
            "runtime": {
                "waitingLocks": 0,
                "workerBackends": 0,
                "maintenanceBackends": 0,
                "runningScrapes": 0,
                "activePhaseAttempts": 0,
                "targetRelationLocks": 0,
                "targetWaitingLocks": 0,
                "matchingActivity": [],
            },
            "indexes": indexes,
            "constraints": [],
            "dependencies": dependencies,
            "tablePartitions": [
                {
                    "oid": 2000 + index,
                    "name": spec["table"],
                    "parent": "leaderboard_entries",
                }
                for index, spec in enumerate(tool.INDEX_SPECS)
                if index != 0
            ],
        },
    }


def absent_probe(source):
    probe = copy.deepcopy(source)
    probe["database"]["indexes"] = []
    probe["database"]["dependencies"] = []
    probe["database"]["constraints"] = []
    return probe


class RetirementToolTests(unittest.TestCase):
    def setUp(self):
        shutil.rmtree(WORK_ROOT, ignore_errors=True)
        WORK_ROOT.mkdir(parents=True)
        os.environ[tool.TEST_MODE_ENV] = "1"

    def tearDown(self):
        shutil.rmtree(WORK_ROOT, ignore_errors=True)
        os.environ.pop(tool.TEST_MODE_ENV, None)

    def prepare_check_package(self, probe=None):
        probe = probe or valid_probe()
        output = WORK_ROOT / "check"
        output.mkdir(mode=0o700)
        report = tool.check_mode(
            types.SimpleNamespace(),
            output,
            probe,
        )
        manifest_path = output / "manifest.json"
        observation_path = output / "zero-use-observation.json"
        rollback_path = output / "rollback.sql"
        args = types.SimpleNamespace(
            manifest=str(manifest_path),
            zero_use_observation=str(observation_path),
            rollback_file=str(rollback_path),
            expected_manifest_sha256=tool.sha256_path(manifest_path),
            expected_zero_use_sha256=tool.sha256_path(
                observation_path
            ),
            expected_rollback_sha256=tool.sha256_path(rollback_path),
            compose_dir=str(tool.PRODUCTION_COMPOSE_DIR),
        )
        return probe, output, report, args

    def test_valid_check_package_reports_exact_family_bytes(self):
        probe, output, report, _ = self.prepare_check_package()

        expected_bytes = sum(
            row["bytes"] for row in probe["database"]["indexes"]
        )
        manifest = json.loads(
            (output / "manifest.json").read_text()
        )

        self.assertEqual("validated", report["outcome"])
        self.assertEqual(expected_bytes, report["totalBytes"])
        self.assertEqual(10, manifest["family"]["indexCount"])
        self.assertEqual(9, manifest["family"]["childCount"])
        self.assertEqual(expected_bytes, manifest["family"]["totalBytes"])
        self.assertEqual(0, manifest["family"]["totalIdxScan"])
        self.assertEqual(
            48,
            manifest["family"]["catalogEvidence"][
                "dependencyCount"
            ],
        )
        self.assertEqual(
            9,
            manifest["family"]["catalogEvidence"][
                "tablePartitionCount"
            ],
        )

    def test_wrong_project_and_cluster_are_rejected(self):
        project = valid_probe()
        project["host"]["project"] = "wrong-project"
        with self.assertRaisesRegex(
            tool.GuardFailure,
            "project identity",
        ):
            tool.validate_runtime_guards(project)

        cluster = valid_probe()
        cluster["database"]["cluster"]["systemIdentifier"] = ""
        with self.assertRaisesRegex(
            tool.GuardFailure,
            "system identifier",
        ):
            tool.validate_runtime_guards(cluster)

        probe, output, _, _ = self.prepare_check_package()
        manifest = json.loads((output / "manifest.json").read_text())
        changed_cluster = copy.deepcopy(probe)
        changed_cluster["database"]["cluster"][
            "systemIdentifier"
        ] = "7623196498058817571"
        tool.validate_runtime_guards(changed_cluster)
        with self.assertRaisesRegex(
            tool.GuardFailure,
            "systemIdentifier",
        ):
            tool.validate_manifest_identity(
                changed_cluster,
                manifest,
            )

    def test_changed_definition_and_constraint_ownership_are_rejected(self):
        changed = valid_probe()
        changed["database"]["indexes"][1]["definition"] += " WHERE true"
        with self.assertRaisesRegex(
            tool.GuardFailure,
            "definition changed",
        ):
            tool.validate_present_family(changed)

        constrained = valid_probe()
        constrained["database"]["constraints"].append(
            {
                "constraint_oid": 1,
                "constraint_name": "unexpected",
                "index_oid": 1000,
            }
        )
        with self.assertRaisesRegex(
            tool.GuardFailure,
            "constraint",
        ):
            tool.validate_present_family(constrained)

    def test_active_query_lock_backend_and_worker_are_rejected(self):
        for field in (
            "waitingLocks",
            "workerBackends",
            "maintenanceBackends",
            "targetRelationLocks",
        ):
            probe = valid_probe()
            probe["database"]["runtime"][field] = 1
            with self.subTest(field=field), self.assertRaisesRegex(
                tool.GuardFailure,
                field,
            ):
                tool.validate_runtime_guards(probe)

        activity = valid_probe()
        activity["database"]["runtime"]["matchingActivity"] = [
            {"pid": 1, "query_md5": "hash"}
        ]
        with self.assertRaisesRegex(
            tool.GuardFailure,
            "active query",
        ):
            tool.validate_runtime_guards(activity)

        worker = valid_probe()
        state = worker["host"]["containers"][tool.WORKER_CONTAINER]
        state["state"] = "running"
        state["running"] = True
        with self.assertRaisesRegex(
            tool.GuardFailure,
            "fstworker must be offline",
        ):
            tool.validate_runtime_guards(worker)

    def test_drop_plan_uses_supported_parent_mechanics(self):
        _, output, _, _ = self.prepare_check_package()
        manifest = json.loads((output / "manifest.json").read_text())
        sql = tool.render_drop_sql(manifest)

        tool.validate_drop_sql(sql)
        self.assertIn(
            "DROP INDEX public.ix_le_song_rank;",
            sql,
        )
        self.assertNotIn("DROP INDEX CONCURRENTLY", sql.upper())
        self.assertNotIn("CASCADE", sql.upper())
        self.assertIn("SET LOCAL lock_timeout = '2s';", sql)
        self.assertIn("SET LOCAL statement_timeout = '30s';", sql)
        self.assertIn("pg_try_advisory_xact_lock_shared", sql)
        self.assertIn("target relation lock appeared", sql)
        self.assertIn("dependency ownership changed", sql)

        unsupported = sql.replace(
            "DROP INDEX public.ix_le_song_rank;",
            "DROP INDEX CONCURRENTLY public.ix_le_song_rank;",
        )
        with self.assertRaisesRegex(
            tool.GuardFailure,
            "partitioned parent",
        ):
            tool.validate_drop_sql(unsupported)

    def test_rollback_builds_leaves_concurrently_then_attaches(self):
        _, output, _, _ = self.prepare_check_package()
        rollback = (output / "rollback.sql").read_text()

        self.assertEqual(
            9,
            rollback.count("CREATE INDEX CONCURRENTLY"),
        )
        self.assertEqual(
            9,
            rollback.count(
                "ALTER INDEX public.ix_le_song_rank ATTACH PARTITION"
            ),
        )
        parent_at = rollback.index(
            "CREATE INDEX ix_le_song_rank ON ONLY"
        )
        first_leaf_at = rollback.index("CREATE INDEX CONCURRENTLY")
        first_attach_at = rollback.index(
            "ALTER INDEX public.ix_le_song_rank ATTACH PARTITION"
        )
        self.assertLess(parent_at, first_leaf_at)
        self.assertLess(first_leaf_at, first_attach_at)

    def test_manifest_drift_rejects_oid_and_zero_use_changes(self):
        probe, output, _, _ = self.prepare_check_package()
        manifest = json.loads((output / "manifest.json").read_text())

        changed_oid = copy.deepcopy(probe)
        old_oid = changed_oid["database"]["indexes"][1]["oid"]
        changed_oid["database"]["indexes"][1]["oid"] += 100
        for dependency in changed_oid["database"]["dependencies"]:
            if dependency["object_oid"] == old_oid:
                dependency["object_oid"] += 100
        with self.assertRaisesRegex(
            tool.GuardFailure,
            "manifest drift",
        ):
            tool.validate_present_family(
                changed_oid,
                expected_manifest=manifest,
            )

        changed_use = copy.deepcopy(probe)
        changed_use["database"]["indexes"][1]["idxScan"] = 1
        with self.assertRaisesRegex(
            tool.GuardFailure,
            "zero-use",
        ):
            tool.validate_present_family(
                changed_use,
                expected_manifest=manifest,
            )

        changed_bytes = copy.deepcopy(probe)
        changed_bytes["database"]["indexes"][1]["bytes"] += 8192
        with self.assertRaisesRegex(
            tool.GuardFailure,
            "manifest drift",
        ):
            tool.validate_present_family(
                changed_bytes,
                expected_manifest=manifest,
            )

    def test_transaction_timeout_reports_no_catalog_change(self):
        probe, _, _, args = self.prepare_check_package()
        output = WORK_ROOT / "execute-timeout"
        output.mkdir(mode=0o700)

        with self.assertRaisesRegex(
            tool.GuardFailure,
            "failed_no_catalog_change",
        ):
            tool.execute_mode(
                args,
                output,
                copy.deepcopy(probe),
                fixture_result={
                    "error": (
                        "canceling statement due to lock timeout"
                    ),
                    "probe": copy.deepcopy(probe),
                },
            )

        report = json.loads((output / "report.json").read_text())
        self.assertTrue(report["mutationAttempted"])
        self.assertEqual(
            "failed_no_catalog_change",
            report["outcome"],
        )
        self.assertIn("lock timeout", report["error"])

    def test_partial_failure_is_fail_closed_and_recoverable(self):
        probe, _, _, args = self.prepare_check_package()
        partial = copy.deepcopy(probe)
        partial["database"]["indexes"].pop()
        output = WORK_ROOT / "execute-partial"
        output.mkdir(mode=0o700)

        with self.assertRaisesRegex(
            tool.GuardFailure,
            "failed_partial_catalog",
        ):
            tool.execute_mode(
                args,
                output,
                copy.deepcopy(probe),
                fixture_result={
                    "error": "simulated post-command failure",
                    "probe": partial,
                },
            )

        report = json.loads((output / "report.json").read_text())
        self.assertEqual("failed_partial_catalog", report["outcome"])
        self.assertTrue((output / "rollback.sql").is_file())

    def test_already_absent_execute_is_idempotent(self):
        probe, _, _, args = self.prepare_check_package()
        output = WORK_ROOT / "execute-absent"
        output.mkdir(mode=0o700)

        report = tool.execute_mode(
            args,
            output,
            absent_probe(probe),
        )

        self.assertEqual("already_absent", report["outcome"])
        self.assertFalse(report["mutationAttempted"])
        self.assertEqual(0, report["catalogBytesRemoved"])
        self.assertFalse((output / "execute.sql").exists())

    def test_reviewed_observation_and_rollback_files_are_required(self):
        probe, output, _, args = self.prepare_check_package()
        manifest = json.loads((output / "manifest.json").read_text())
        rollback = tool.render_rollback_sql(
            {
                row["name"]: row
                for row in manifest["family"]["indexes"]
            }
        ).encode()

        pathlib.Path(args.zero_use_observation).write_text("{}\n")
        with self.assertRaisesRegex(
            tool.GuardFailure,
            "observation file",
        ):
            tool.compare_expected_digests(
                args,
                manifest,
                rollback,
                pathlib.Path(args.manifest).read_bytes(),
            )

        self.assertEqual("present", tool.family_state(probe))

    def test_zero_use_report_keeps_statistics_reset_caveat(self):
        probe = valid_probe()
        indexes, total_bytes = tool.validate_present_family(probe)
        observation = tool.build_zero_use_observation(
            probe,
            indexes,
            total_bytes,
        )

        self.assertIsNone(observation["databaseStatsReset"])
        self.assertIn("not proof of lifetime nonuse", observation["caveat"])
        self.assertEqual(0, observation["totalIdxScan"])

    def test_cli_has_no_arbitrary_index_target(self):
        with contextlib.redirect_stderr(io.StringIO()):
            with self.assertRaises(SystemExit):
                tool.parse_args(
                    [
                        "--check",
                        "--output",
                        str(WORK_ROOT / "out"),
                        "--index",
                        "other_index",
                    ]
                )

        with contextlib.redirect_stderr(io.StringIO()):
            with self.assertRaises(SystemExit):
                tool.parse_args(
                    [
                        "--execute",
                        "--output",
                        str(WORK_ROOT / "out"),
                        "--manifest",
                        "manifest.json",
                        "--zero-use-observation",
                        "zero-use-observation.json",
                        "--rollback-file",
                        "rollback.sql",
                        "--expected-manifest-sha256",
                        "a" * 64,
                        "--expected-zero-use-sha256",
                        "b" * 64,
                        "--expected-rollback-sha256",
                        "c" * 64,
                        "--fixture",
                        "fixture.json",
                    ]
                )

    def test_worker_start_guard_lock_is_nonblocking(self):
        lock_path = WORK_ROOT / "worker.lock"
        self.assertEqual(
            "available",
            tool.probe_worker_guard_lock(lock_path),
        )
        with tool.acquire_worker_guard_lock(lock_path):
            self.assertEqual(
                "externally_held",
                tool.probe_worker_guard_lock(lock_path),
            )
            with self.assertRaisesRegex(
                tool.GuardFailure,
                "already held",
            ):
                with tool.acquire_worker_guard_lock(lock_path):
                    self.fail("nested lock unexpectedly succeeded")


if __name__ == "__main__":
    unittest.main()
