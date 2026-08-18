import importlib.util
import json
import os
import pathlib
import shutil
import types
import unittest
from datetime import datetime, timezone
from unittest import mock


TOOLS_DIR = pathlib.Path(__file__).resolve().parent
SCRIPT = TOOLS_DIR / "postgres-pro-bass-snapshot-rewrite.py"
WORK_ROOT = (
    TOOLS_DIR
    / "testdata"
    / "postgres-pro-bass-snapshot-rewrite"
    / f".work-{os.getpid()}"
)


def load_tool():
    spec = importlib.util.spec_from_file_location(
        "postgres_pro_bass_snapshot_rewrite",
        SCRIPT,
    )
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


tool = load_tool()


class Completed:
    def __init__(self, stdout="", stderr="", returncode=0):
        self.stdout = stdout
        self.stderr = stderr
        self.returncode = returncode


class MountRunner:
    def __init__(
        self,
        source="/dev/test",
        filesystem="ext4",
        device_id="0:123",
        target="/",
    ):
        self.mount = (
            f"{source} {filesystem} {device_id} {target}\n"
        )

    def run(self, arguments, **_):
        if arguments[0] == "findmnt":
            return Completed(self.mount)
        raise AssertionError(f"unexpected command: {arguments}")


class CaptureRunner:
    def __init__(self, stdout="{}"):
        self.stdout = stdout
        self.calls = []

    def run(self, arguments, **kwargs):
        self.calls.append((arguments, kwargs))
        return Completed(self.stdout)


class CancellationRunner:
    def __init__(self):
        self.backend_counts = iter(["1", "1", "1", "1", "1", "0"])
        self.queries = []

    def run(self, arguments, **_):
        sql = arguments[-1]
        self.queries.append(sql)
        if "COUNT(*) FROM pg_stat_activity" in sql:
            return Completed(next(self.backend_counts))
        if "pg_cancel_backend" in sql or "pg_terminate_backend" in sql:
            return Completed("1")
        raise AssertionError(f"unexpected command: {arguments}")


def plan_fixture():
    return {
        "planIdentity": {
            "exactRetainedRows": 6_691_993,
        },
        "catalog": {
            "heapBytes": 100_000_000_000,
            "indexBytes": 50_000_000_000,
        },
    }


def profile_fixture():
    return {
        "formatVersion": tool.FORMAT_VERSION,
        "toolId": tool.TOOL_ID,
        "profileId": "synthetic-pg17-profile",
        "isolatedPg17DrillPassed": True,
        "promotionEligible": True,
        "scale": {
            "totalRows": 360_000,
            "retainedRows": 60_000,
        },
        "replacementHeapToSourceRetainedRatio": 1.2,
        "replacementIndexToSourceRetainedRatio": 1.2,
        "walToReplacementRatio": 1.0,
        "tempToReplacementRatio": 0.1,
        "failureReserveRatio": 1.0,
    }


class ProBassPilotToolTests(unittest.TestCase):
    def setUp(self):
        shutil.rmtree(WORK_ROOT, ignore_errors=True)
        WORK_ROOT.mkdir(parents=True, mode=0o700)

    def tearDown(self):
        shutil.rmtree(WORK_ROOT, ignore_errors=True)

    def test_target_is_exact_and_not_a_cli_argument(self):
        parser = tool.build_parser()
        option_destinations = {
            action.dest for action in parser._actions
        }

        self.assertEqual(
            "leaderboard_entries_snapshot_pro_bass",
            tool.TARGET_PARTITION,
        )
        self.assertEqual(
            "Solo_PeripheralBass",
            tool.TARGET_INSTRUMENT,
        )
        self.assertNotIn("table", option_destinations)
        self.assertNotIn("relation", option_destinations)
        self.assertNotIn("sql", option_destinations)

    def test_inventory_query_uses_loose_index_scan_without_row_hashing(self):
        sql = tool.inventory_query()

        self.assertNotIn("GROUPING SETS", sql)
        self.assertIn("WITH RECURSIVE snapshot_ids", sql)
        self.assertIn("MIN(next_snapshot.snapshot_id)", sql)
        self.assertNotIn("GROUP BY snapshot.snapshot_id", sql)
        self.assertNotIn("hashtextextended", sql)
        self.assertNotIn("COUNT(*)::bigint AS row_count", sql)
        self.assertIn(
            "enable_seqscan=off",
            tool.LOOSE_ID_PGOPTIONS,
        )

    def test_production_plan_requires_verified_archive_input(self):
        args = tool.build_parser().parse_args(
            [
                "plan",
                "--scratch-root",
                "/not-used-by-argument-validation",
                "--expected-device-id",
                "259:4",
                "--run-id",
                "production-plan-0001",
            ]
        )

        with self.assertRaisesRegex(
            tool.PilotError,
            "verified live archive input",
        ):
            tool.validate_args(args)

    def test_plan_query_options_bound_temp_and_parallelism(self):
        self.assertIn(
            "temp_file_limit=262144kB",
            tool.PLAN_QUERY_PGOPTIONS,
        )
        self.assertIn(
            "max_parallel_workers_per_gather=0",
            tool.PLAN_QUERY_PGOPTIONS,
        )
        self.assertIn("work_mem=64MB", tool.PLAN_QUERY_PGOPTIONS)

        runner = CaptureRunner()
        database = tool.Database(runner, "postgres", "fst", "fstservice")

        database.json(
            "SELECT '{}'::json",
            pgoptions=tool.PLAN_QUERY_PGOPTIONS,
        )

        arguments = runner.calls[0][0]
        options = arguments[arguments.index("-e") + 3]
        self.assertIn(tool.PLAN_QUERY_PGOPTIONS, options)

    def test_source_query_protects_only_named_publication_generations(self):
        sql = tool.protected_sources_query(1)

        self.assertIn("current_publication_id", sql)
        self.assertIn("previous_publication_id", sql)
        self.assertIn("working_publication_id", sql)
        self.assertIn("leaderboard_published_scope_source", sql)
        self.assertIn("publication_generations", sql)
        self.assertNotIn("SELECT DISTINCT source_snapshot_id\n"
                         "            FROM "
                         "leaderboard_published_scope_source",
                         sql)

    def test_classification_keeps_named_sources_and_allows_stale_maps(self):
        protected = [
            {
                "snapshotId": 700,
                "reasons": [
                    "current_publication_physical_source"
                ],
            },
            {
                "snapshotId": 699,
                "reasons": [
                    "previous_publication_physical_source"
                ],
            },
            {
                "snapshotId": 698,
                "reasons": [
                    "working_publication_physical_source"
                ],
            },
        ]
        inventory = [
            {
                "snapshotId": snapshot_id,
                "rowCount": 10,
                "scrapeLogPresent": True,
                "scrapeCompletedAt": "2026-08-01T00:00:00Z",
                "scrapeStatus": "completed",
                "namedSourceMapCount": (
                    1 if snapshot_id in (700, 699, 698) else 0
                ),
                "sourceMapCount": 1,
            }
            for snapshot_id in (700, 699, 698, 600)
        ]

        result = tool.classify_inventory(protected, inventory)

        self.assertEqual(
            [700, 699, 698],
            [row["snapshotId"] for row in result["keep"]],
        )
        self.assertEqual(
            [600],
            [
                row["snapshotId"]
                for row in result["archiveThenPurge"]
            ],
        )
        self.assertEqual([], result["blocked"])

    def test_classification_blocks_unowned_or_incomplete_snapshot(self):
        result = tool.classify_inventory(
            [],
            [
                {
                    "snapshotId": 600,
                    "rowCount": 1,
                    "scrapeLogPresent": False,
                    "scrapeCompletedAt": None,
                    "scrapeStatus": "running",
                    "namedSourceMapCount": 0,
                }
            ],
        )

        blocked = self.assertEqual(1, len(result["blocked"]))
        self.assertIsNone(blocked)
        self.assertIn(
            "missing scrape_log ownership",
            result["blocked"][0]["reasons"],
        )
        self.assertIn(
            "scrape is incomplete",
            result["blocked"][0]["reasons"],
        )

    def test_absent_rollback_snapshot_does_not_block_partition(self):
        result = tool.classify_inventory(
            [
                {
                    "snapshotId": 900,
                    "reasons": ["rollback_completed_snapshot"],
                }
            ],
            [
                {
                    "snapshotId": 700,
                    "rowCount": 1,
                    "scrapeLogPresent": True,
                    "scrapeCompletedAt": "2026-08-01T00:00:00Z",
                    "scrapeStatus": "completed",
                    "namedSourceMapCount": 0,
                }
            ],
        )

        self.assertEqual([900], result["missingProtectedSnapshotIds"])
        self.assertEqual([], result["missingRequiredSnapshotIds"])

    def test_absent_publication_source_blocks_partition(self):
        result = tool.classify_inventory(
            [
                {
                    "snapshotId": 700,
                    "reasons": [
                        "current_publication_physical_source"
                    ],
                }
            ],
            [],
        )

        self.assertEqual([700], result["missingRequiredSnapshotIds"])

    def test_scratch_validation_accepts_empty_matching_local_device(self):
        result = tool.validate_scratch_root(
            MountRunner(),
            WORK_ROOT,
            "0:123",
            test_mode=True,
            allow_unclaimed=True,
        )

        self.assertEqual("0:123", result["device"]["deviceId"])
        self.assertEqual(str(WORK_ROOT.resolve()), result["resolvedPath"])

    def test_scratch_validation_accepts_only_precreated_tablespace_mount(self):
        tablespace = WORK_ROOT / tool.TABLESPACE_DIR
        tablespace.mkdir(mode=0o700)

        result = tool.validate_scratch_root(
            MountRunner(),
            WORK_ROOT,
            "0:123",
            test_mode=True,
            allow_unclaimed=True,
        )
        marker = tool.claim_workspace(
            WORK_ROOT,
            result,
            "synthetic-run-0001",
            "2026-10-01T00:00:00Z",
            "a" * 40,
            "c" * 64,
            True,
        )

        self.assertEqual(
            "synthetic-run-0001",
            marker["runId"],
        )
        self.assertTrue(tablespace.is_dir())

    def test_scratch_validation_rejects_device_mismatch(self):
        with self.assertRaisesRegex(
            tool.PilotError,
            "device identity mismatch",
        ):
            tool.validate_scratch_root(
                MountRunner(),
                WORK_ROOT,
                "0:999",
                test_mode=True,
                allow_unclaimed=True,
            )

    def test_scratch_validation_rejects_remote_filesystem(self):
        with self.assertRaisesRegex(
            tool.PilotError,
            "local filesystem",
        ):
            tool.validate_scratch_root(
                MountRunner(filesystem="nfs4"),
                WORK_ROOT,
                "0:123",
                test_mode=True,
                allow_unclaimed=True,
            )

    def test_scratch_validation_rejects_foreign_nonempty_workspace(self):
        (WORK_ROOT / "foreign.txt").write_text(
            "not owned by the pilot",
            encoding="utf-8",
        )

        with self.assertRaisesRegex(
            tool.PilotError,
            "must be empty",
        ):
            tool.validate_scratch_root(
                MountRunner(),
                WORK_ROOT,
                "0:123",
                test_mode=True,
                allow_unclaimed=True,
            )

    def test_scratch_validation_rejects_symlink_escape(self):
        real = WORK_ROOT / "real"
        real.mkdir()
        linked = WORK_ROOT / "linked"
        linked.symlink_to(real, target_is_directory=True)

        with self.assertRaisesRegex(
            tool.PilotError,
            "symbolic link",
        ):
            tool.validate_scratch_root(
                MountRunner(),
                linked,
                "0:123",
                test_mode=True,
                allow_unclaimed=True,
            )

    def test_workspace_marker_records_temporary_only_archive_lifecycle(self):
        scratch = tool.validate_scratch_root(
            MountRunner(),
            WORK_ROOT,
            "0:123",
            test_mode=True,
            allow_unclaimed=True,
        )
        marker = tool.claim_workspace(
            WORK_ROOT,
            scratch,
            "synthetic-run-0001",
            "2026-10-01T00:00:00Z",
            "a" * 40,
            "c" * 64,
            True,
        )

        self.assertTrue(marker["temporaryOnly"])
        self.assertFalse(marker["acceptedDataMayRemainHere"])
        self.assertTrue(
            marker["archiveDeletionRequiresSeparateOperatorDecision"]
        )
        self.assertEqual(
            0o600,
            (WORK_ROOT / tool.WORKSPACE_MARKER).stat().st_mode
            & 0o777,
        )

    def test_claimed_workspace_rejects_foreign_entries(self):
        scratch = tool.validate_scratch_root(
            MountRunner(),
            WORK_ROOT,
            "0:123",
            test_mode=True,
            allow_unclaimed=True,
        )
        tool.claim_workspace(
            WORK_ROOT,
            scratch,
            "synthetic-run-0001",
            "2026-10-01T00:00:00Z",
            "a" * 40,
            "c" * 64,
            True,
        )
        (WORK_ROOT / "foreign.bin").write_bytes(b"x")

        with self.assertRaisesRegex(
            tool.PilotError,
            "foreign entries",
        ):
            tool.validate_scratch_root(
                MountRunner(),
                WORK_ROOT,
                "0:123",
                test_mode=True,
            )

    def test_claimed_workspace_rejects_owned_directory_symlink(self):
        scratch = tool.validate_scratch_root(
            MountRunner(),
            WORK_ROOT,
            "0:123",
            test_mode=True,
            allow_unclaimed=True,
        )
        tool.claim_workspace(
            WORK_ROOT,
            scratch,
            "synthetic-run-0001",
            "2026-10-01T00:00:00Z",
            "a" * 40,
            "c" * 64,
            True,
        )
        archive = WORK_ROOT / tool.ARCHIVE_DIR
        archive.rmdir()
        archive.symlink_to(
            WORK_ROOT / tool.RESTORE_DIR,
            target_is_directory=True,
        )

        with self.assertRaisesRegex(
            tool.PilotError,
            "not a real directory",
        ):
            tool.validate_scratch_root(
                MountRunner(),
                WORK_ROOT,
                "0:123",
                test_mode=True,
            )

    def test_workspace_marker_fences_commit_run_and_expiry(self):
        marker = {
            "runId": "synthetic-run-0001",
            "repositoryCommit": "a" * 40,
            "toolSourceSha256": "c" * 64,
            "expiresAtUtc": "2026-10-01T00:00:00Z",
        }

        tool.validate_workspace_marker(
            marker,
            "synthetic-run-0001",
            "a" * 40,
            "c" * 64,
            now=datetime(
                2026, 9, 1, tzinfo=timezone.utc
            ),
        )
        with self.assertRaisesRegex(
            tool.PilotError,
            "repository commit differs",
        ):
            tool.validate_workspace_marker(
                marker,
                "synthetic-run-0001",
                "b" * 40,
                "c" * 64,
                now=datetime(
                    2026, 9, 1, tzinfo=timezone.utc
                ),
            )
        with self.assertRaisesRegex(
            tool.PilotError,
            "tool source hash differs",
        ):
            tool.validate_workspace_marker(
                marker,
                "synthetic-run-0001",
                "a" * 40,
                "d" * 64,
                now=datetime(
                    2026, 9, 1, tzinfo=timezone.utc
                ),
            )
        with self.assertRaisesRegex(
            tool.PilotError,
            "has expired",
        ):
            tool.validate_workspace_marker(
                marker,
                "synthetic-run-0001",
                "a" * 40,
                "c" * 64,
                now=datetime(
                    2026, 10, 2, tzinfo=timezone.utc
                ),
            )

    def test_verified_archive_input_binds_source_restore_and_cleanup(self):
        archive = WORK_ROOT / "archive.custom"
        archive.write_bytes(b"verified archive")
        validation = WORK_ROOT / "validation.json"
        cleanup = WORK_ROOT / "cleanup.json"
        distribution = WORK_ROOT / "distribution.json"
        catalog_file = WORK_ROOT / "catalog.json"
        source = {
            "partitionOid": 100,
            "relfilenode": 101,
            "heapBytes": 1_000,
            "indexBytes": 2_000,
            "totalBytes": 3_000,
            "inserts": 10,
            "updates": 0,
            "deletes": 0,
        }
        catalog = {
            "parentDefinition": "LIST (instrument)",
            "partitionBound": (
                "FOR VALUES IN ('Solo_PeripheralBass')"
            ),
            "owner": "fst",
            "tablespace": "pg_default",
            "columns": [
                {
                    "ordinal": 1,
                    "name": "snapshot_id",
                    "type": "bigint",
                    "notNull": True,
                    "defaultExpression": None,
                }
            ],
            "constraints": [
                {
                    "name": "pro_bass_pkey",
                    "type": "p",
                    "definition": (
                        "PRIMARY KEY "
                        "(snapshot_id, song_id, instrument, account_id)"
                    ),
                }
            ],
            "indexes": [
                {
                    "name": "pro_bass_pkey",
                    "definition": "primary definition",
                    "isPrimary": True,
                    "isUnique": True,
                    "isValid": True,
                },
                {
                    "name": "pro_bass_score_idx",
                    "definition": "score definition",
                    "isPrimary": False,
                    "isUnique": False,
                    "isValid": True,
                },
            ],
            "heapBytes": 0,
            "indexBytes": 0,
            "totalBytes": 0,
        }
        catalog_file.write_bytes(tool.canonical_json_bytes(catalog))
        distribution.write_bytes(
            tool.canonical_json_bytes(
                {
                    "status": "succeeded",
                    "rowCount": 100,
                    "snapshotIds": [80, 90],
                    "distribution": [
                        {
                            "snapshotId": 80,
                            "rowCount": 40,
                            "contentHashXor": -10,
                            "contentHashSum": "-100",
                        },
                        {
                            "snapshotId": 90,
                            "rowCount": 60,
                            "contentHashXor": 20,
                            "contentHashSum": "200",
                        },
                    ],
                }
            )
        )
        validation.write_bytes(
            tool.canonical_json_bytes(
                {
                    "status": "succeeded",
                    "productionDatabaseMutated": False,
                    "sourceChangedDuringArchive": False,
                    "source": {
                        "oid": source["partitionOid"],
                        "relfilenode": source["relfilenode"],
                        "heapBytes": source["heapBytes"],
                        "indexBytes": source["indexBytes"],
                        "totalBytes": source["totalBytes"],
                        "inserts": source["inserts"],
                        "updates": source["updates"],
                        "deletes": source["deletes"],
                    },
                    "archive": {
                        "path": str(archive),
                        "bytes": archive.stat().st_size,
                        "sha256": tool.sha256_path(archive),
                        "checksumMatches": True,
                    },
                    "catalog": catalog,
                    "data": {
                        "rowCount": 100,
                        "snapshotIds": [80, 90],
                        "distributionPath": str(distribution),
                        "distributionSha256": tool.sha256_path(
                            distribution
                        ),
                    },
                }
            )
        )
        cleanup.write_bytes(
            tool.canonical_json_bytes(
                {
                    "status": "succeeded",
                    "containerRemoved": True,
                    "restorePgdataRemoved": True,
                    "archiveRetained": True,
                    "archivePath": str(archive),
                    "archiveSha256": tool.sha256_path(archive),
                    "validationPath": str(validation),
                    "validationSha256": tool.sha256_path(
                        validation
                    ),
                }
            )
        )
        value = {
            "formatVersion": tool.FORMAT_VERSION,
            "toolId": tool.TOOL_ID,
            "target": "public.leaderboard_entries_snapshot_pro_bass",
            "instrument": tool.TARGET_INSTRUMENT,
            "source": {
                **source,
                "database": "fstservice",
                "systemIdentifier": "system-1",
                "changedDuringArchive": False,
            },
            "archive": {
                "path": str(archive),
                "bytes": archive.stat().st_size,
                "sha256": tool.sha256_path(archive),
                "deviceSource": "/dev/test",
                "deviceId": "0:123",
            },
            "restore": {
                "status": "succeeded",
                "validationPath": str(validation),
                "validationSha256": tool.sha256_path(validation),
                "rowCount": 100,
                "snapshotIdCount": 2,
                "snapshotIdMin": 80,
                "snapshotIdMax": 90,
                "snapshotIds": [80, 90],
                "distributionPath": str(distribution),
                "distributionSha256": tool.sha256_path(
                    distribution
                ),
                "catalogPath": str(catalog_file),
                "catalogSha256": tool.sha256_path(catalog_file),
                "partitionBound": (
                    "FOR VALUES IN ('Solo_PeripheralBass')"
                ),
                "owner": "fst",
                "primaryConstraint": "pro_bass_pkey",
                "primaryConstraintDefinition": (
                    "PRIMARY KEY "
                    "(snapshot_id, song_id, instrument, account_id)"
                ),
                "primaryIndex": "pro_bass_pkey",
                "primaryIndexDefinition": "primary definition",
                "scoreIndex": "pro_bass_score_idx",
                "scoreIndexDefinition": "score definition",
            },
            "cleanup": {
                "status": "succeeded",
                "proofPath": str(cleanup),
                "proofSha256": tool.sha256_path(cleanup),
                "containerRemoved": True,
                "restorePgdataRemoved": True,
                "archiveRetained": True,
                "archivePath": str(archive),
                "archiveSha256": tool.sha256_path(archive),
                "validationPath": str(validation),
                "validationSha256": tool.sha256_path(
                    validation
                ),
            },
        }
        input_path = WORK_ROOT / "verified-input.json"
        input_path.write_bytes(tool.canonical_json_bytes(value))
        args = types.SimpleNamespace(
            verified_live_archive_input=str(input_path),
            expected_live_archive_input_sha256=tool.sha256_path(
                input_path
            ),
            test_mode=True,
        )
        check = {
            "identity": {
                "systemIdentifier": "system-1",
                "database": "fstservice",
            }
        }

        loaded, observed = tool.load_verified_archive_input(
            args,
            MountRunner(target="/workspace"),
            check,
            source,
            verify_archive_checksum=True,
        )

        self.assertEqual(value, loaded)
        self.assertEqual(tool.sha256_path(input_path), observed)

        original_distribution = distribution.read_bytes()
        content_tamper = json.loads(
            original_distribution.decode("utf-8")
        )
        content_tamper["distribution"][0]["contentHashXor"] += 1
        distribution.write_bytes(
            tool.canonical_json_bytes(content_tamper)
        )
        value["restore"]["distributionSha256"] = tool.sha256_path(
            distribution
        )
        input_path.write_bytes(tool.canonical_json_bytes(value))
        args.expected_live_archive_input_sha256 = tool.sha256_path(
            input_path
        )
        with self.assertRaisesRegex(
            tool.PilotError,
            "restore validation is inconsistent",
        ):
            tool.load_verified_archive_input(
                args,
                MountRunner(target="/workspace"),
                check,
                source,
                verify_archive_checksum=True,
            )

        distribution.write_bytes(original_distribution)
        value["restore"]["distributionSha256"] = tool.sha256_path(
            distribution
        )
        original_catalog = catalog_file.read_bytes()
        catalog_tamper = json.loads(
            original_catalog.decode("utf-8")
        )
        catalog_tamper["columns"][0]["type"] = "integer"
        catalog_file.write_bytes(
            tool.canonical_json_bytes(catalog_tamper)
        )
        value["restore"]["catalogSha256"] = tool.sha256_path(
            catalog_file
        )
        input_path.write_bytes(tool.canonical_json_bytes(value))
        args.expected_live_archive_input_sha256 = tool.sha256_path(
            input_path
        )
        with self.assertRaisesRegex(
            tool.PilotError,
            "restore validation is inconsistent",
        ):
            tool.load_verified_archive_input(
                args,
                MountRunner(target="/workspace"),
                check,
                source,
                verify_archive_checksum=True,
            )

        catalog_file.write_bytes(original_catalog)
        value["restore"]["catalogSha256"] = tool.sha256_path(
            catalog_file
        )
        tampered = json.loads(
            distribution.read_text(encoding="utf-8")
        )
        tampered["distribution"][1]["snapshotId"] = 80
        distribution.write_bytes(
            tool.canonical_json_bytes(tampered)
        )
        value["restore"]["distributionSha256"] = tool.sha256_path(
            distribution
        )
        input_path.write_bytes(tool.canonical_json_bytes(value))
        args.expected_live_archive_input_sha256 = tool.sha256_path(
            input_path
        )
        with self.assertRaisesRegex(
            tool.PilotError,
            "distribution is inconsistent",
        ):
            tool.load_verified_archive_input(
                args,
                MountRunner(target="/workspace"),
                check,
                source,
                verify_archive_checksum=True,
            )

    def test_exclusive_write_is_complete_and_leaves_no_temporary_file(self):
        path = WORK_ROOT / "evidence.json"
        payload = b'{"status":"succeeded"}\n'

        tool.write_bytes_exclusive(path, payload)

        self.assertEqual(payload, path.read_bytes())
        self.assertEqual(
            [],
            list(WORK_ROOT.glob(".evidence.json.tmp-*")),
        )

    def test_read_json_rejects_truncated_evidence(self):
        path = WORK_ROOT / "truncated.json"
        path.write_text('{"status":', encoding="utf-8")

        with self.assertRaisesRegex(
            tool.PilotError,
            "malformed",
        ):
            tool.read_json(path)

    def test_capacity_gate_preserves_emergency_floor(self):
        result = tool.calculate_capacity(
            plan_fixture(),
            profile_fixture(),
            66_000_000_000,
            300_000_000,
        )

        self.assertEqual(
            tool.EMERGENCY_FLOOR_BYTES,
            result["emergencyFloorBytes"],
        )
        self.assertGreater(
            result["requiredFreeBytes"],
            tool.EMERGENCY_FLOOR_BYTES,
        )
        self.assertFalse(result["allowed"])
        self.assertLess(result["marginBytes"], 0)

    def test_capacity_gate_uses_measured_candidate_profile_not_global_gate(
        self,
    ):
        result = tool.calculate_capacity(
            plan_fixture(),
            profile_fixture(),
            600_000_000_000,
            300_000_000,
        )

        self.assertTrue(result["measured"])
        self.assertEqual(
            "synthetic-pg17-profile",
            result["profileId"],
        )
        self.assertTrue(result["allowed"])
        self.assertLess(result["requiredFreeBytes"], 500 * 1024**3)

    def test_scratch_capacity_keeps_fst_above_emergency_floor(self):
        result = tool.calculate_scratch_capacity(
            plan_fixture(),
            profile_fixture(),
            66_000_000_000,
            5_000_000_000_000,
            300_000_000,
        )

        self.assertTrue(result["allowed"])
        self.assertGreater(
            result["fstRequiredFreeBytes"],
            tool.EMERGENCY_FLOOR_BYTES,
        )
        self.assertGreater(result["fstMarginBytes"], 0)
        self.assertGreater(result["scratchMarginBytes"], 0)
        self.assertEqual(
            "temporary_scratch_tablespace",
            result["mode"],
        )

    def test_repatriation_capacity_uses_measured_copy_and_wal(self):
        result = tool.calculate_repatriation_capacity(
            {
                "sizes": {
                    "totalBytes": 3_000_000_000,
                    "walBytes": 3_200_000_000,
                }
            },
            70_000_000_000,
        )

        self.assertTrue(result["allowed"])
        self.assertGreater(
            result["requiredFreeBytes"],
            tool.EMERGENCY_FLOOR_BYTES + 6_000_000_000,
        )

    def test_profile_requires_exact_checksum_and_passed_pg17_drill(self):
        profile_path = WORK_ROOT / "profile.json"
        profile_path.write_bytes(
            tool.canonical_json_bytes(profile_fixture())
        )
        expected = tool.sha256_path(profile_path)

        profile, observed = tool.verify_profile(
            profile_path,
            expected,
        )

        self.assertEqual(expected, observed)
        self.assertTrue(profile["isolatedPg17DrillPassed"])
        with self.assertRaisesRegex(
            tool.PilotError,
            "checksum differs",
        ):
            tool.verify_profile(profile_path, "0" * 64)

    def test_production_profile_rejects_seed_or_too_small_scale(self):
        profile = profile_fixture()
        profile["promotionEligible"] = False
        profile_path = WORK_ROOT / "seed.json"
        profile_path.write_bytes(tool.canonical_json_bytes(profile))

        with self.assertRaisesRegex(
            tool.PilotError,
            "not eligible",
        ):
            tool.verify_profile(
                profile_path,
                tool.sha256_path(profile_path),
            )

        profile["promotionEligible"] = True
        profile["scale"]["totalRows"] = 1_000
        profile_path.unlink()
        profile_path.write_bytes(tool.canonical_json_bytes(profile))
        with self.assertRaisesRegex(
            tool.PilotError,
            "scale is too small",
        ):
            tool.verify_profile(
                profile_path,
                tool.sha256_path(profile_path),
            )

    def test_fingerprint_covers_every_snapshot_column(self):
        sql = tool.fingerprint_sql(tool.TARGET_PARTITION)

        for column in tool.SNAPSHOT_COLUMNS:
            self.assertIn(f'row."{column}"', sql)
        self.assertIn("hashXor0", sql)
        self.assertIn("hashXor1", sql)
        self.assertIn("hashSum2", sql)

    def test_physical_relation_identity_ignores_rename_and_attachment(self):
        original = {
            "oid": 123,
            "relfilenode": 456,
            "heapBytes": 100,
            "indexBytes": 200,
            "totalBytes": 300,
            "inserts": 10,
            "updates": 0,
            "deletes": 0,
            "attached": True,
            "partitionBound": "FOR VALUES IN ('x')",
        }
        renamed = {
            **original,
            "attached": False,
            "partitionBound": None,
        }

        self.assertEqual(
            tool.physical_relation_identity(original),
            tool.physical_relation_identity(renamed),
        )
        renamed["updates"] = 1
        self.assertNotEqual(
            tool.physical_relation_identity(original),
            tool.physical_relation_identity(renamed),
        )

    def test_relation_state_maps_to_verified_source_fence(self):
        state = {
            "oid": 123,
            "relfilenode": 456,
            "heapBytes": 100,
            "indexBytes": 200,
            "totalBytes": 300,
            "inserts": 10,
            "updates": 1,
            "deletes": 2,
        }

        self.assertEqual(
            {
                "partitionOid": 123,
                "relfilenode": 456,
                "heapBytes": 100,
                "indexBytes": 200,
                "totalBytes": 300,
                "inserts": 10,
                "updates": 1,
                "deletes": 2,
            },
            tool.source_fence_from_relation_state(state),
        )

    def test_filesystem_monitor_retries_breach_handler_until_cleared(self):
        calls = []

        def handle(_free):
            calls.append(len(calls) + 1)
            return len(calls) >= 3

        monitor = tool.FilesystemMonitor(
            WORK_ROOT,
            minimum_allowed_bytes=10**30,
            on_breach=handle,
        )
        monitor._observe()
        monitor._observe()
        monitor._observe()

        self.assertTrue(monitor.breached)
        self.assertTrue(monitor.breach_handled)
        self.assertEqual(3, len(calls))

    def test_emergency_cancellation_escalates_until_no_backend_remains(self):
        runner = CancellationRunner()
        args = types.SimpleNamespace(
            scratch_root=str(WORK_ROOT),
            pg_container="postgres-test",
            pg_user="fst",
            pg_database="fstservice",
        )

        with mock.patch.object(tool.time, "sleep"):
            result = tool.cancel_pilot_backends(
                runner,
                args,
                60_000_000_000,
                61_000_000_000,
                "fst",
            )

        self.assertTrue(result)
        self.assertTrue(
            any("pg_cancel_backend" in sql for sql in runner.queries)
        )
        self.assertTrue(
            any("pg_terminate_backend" in sql for sql in runner.queries)
        )
        self.assertTrue(
            (
                WORK_ROOT
                / tool.REPORTS_DIR
                / "emergency-floor-breach.json"
            ).is_file()
        )

    def test_snapshot_id_predicate_is_exact_and_rejects_empty_input(self):
        predicate = tool.snapshot_id_predicate([1302, 1301])

        self.assertEqual(
            "snapshot_id = ANY(ARRAY[1302, 1301]::bigint[])",
            predicate,
        )
        with self.assertRaisesRegex(
            tool.PilotError,
            "cannot be empty",
        ):
            tool.snapshot_id_predicate([])

    def test_relation_names_are_deterministic_and_bounded(self):
        first = tool.replacement_name("synthetic-run-0001")
        second = tool.replacement_name("synthetic-run-0001")

        self.assertEqual(first, second)
        self.assertLessEqual(len(first), 63)
        self.assertRegex(first, r"^[a-z][a-z0-9_]+$")
        self.assertNotEqual(
            first,
            tool.replacement_name("synthetic-run-0002"),
        )
        for name in (
            tool.replacement_primary_name(
                "synthetic-run-0001"
            ),
            tool.replacement_score_name(
                "synthetic-run-0001"
            ),
            tool.replacement_instrument_check_name(
                "synthetic-run-0001"
            ),
            tool.home_name("synthetic-run-0001"),
            tool.home_primary_name("synthetic-run-0001"),
            tool.home_score_name("synthetic-run-0001"),
            tool.scratch_retired_name("synthetic-run-0001"),
            tool.tablespace_name("synthetic-run-0001"),
        ):
            self.assertLessEqual(len(name), 63)
            self.assertRegex(name, r"^[a-z][a-z0-9_]+$")

    def test_archive_command_contains_only_exact_parent_and_partition(self):
        args = types.SimpleNamespace(
            pg_container="fst-pro-bass-pilot-test-source",
            pg_user="fst",
            pg_database="fstservice",
        )

        command = tool.pg_dump_command(args)

        self.assertEqual(2, command.count("--table"))
        self.assertIn("public.leaderboard_entries_snapshot", command)
        self.assertIn(
            "public.leaderboard_entries_snapshot_pro_bass",
            command,
        )
        self.assertNotIn("--password", command)

    def test_api_parity_allows_dynamic_health_but_not_publication_drift(
        self,
    ):
        baseline = [
            {
                "route": "/api/service-info",
                "comparison": "health",
                "status": 200,
                "contentType": "application/json",
                "sha256": "old",
            },
            {
                "route": "/api/songs",
                "comparison": "exact",
                "status": 200,
                "contentType": "application/json",
                "etag": '"same"',
                "bytes": 10,
                "sha256": "same",
            },
        ]
        observed = [
            {
                **baseline[0],
                "sha256": "new-dynamic-body",
            },
            dict(baseline[1]),
        ]

        self.assertTrue(
            tool.compare_api_snapshots(baseline, observed)
        )
        observed[1]["sha256"] = "changed"
        self.assertFalse(
            tool.compare_api_snapshots(baseline, observed)
        )

    def test_success_and_failure_reports_are_typed_and_immutable(self):
        (WORK_ROOT / tool.REPORTS_DIR).mkdir()
        success = tool.write_stage_report(
            WORK_ROOT,
            "check",
            {"runId": "synthetic-run-0001", "value": 1},
        )
        repeated = tool.write_stage_report(
            WORK_ROOT,
            "check",
            {"runId": "synthetic-run-0001", "value": 2},
        )
        failure_path = tool.write_failure_report(
            WORK_ROOT,
            "plan",
            tool.PilotError("blocked"),
        )

        self.assertEqual("succeeded", success["status"])
        self.assertEqual(1, repeated["value"])
        failure = json.loads(
            failure_path.read_text(encoding="utf-8")
        )
        self.assertEqual("failed", failure["status"])
        self.assertEqual("plan", failure["stage"])

    def test_stage_dependencies_keep_archive_and_drill_before_swap_drop(self):
        self.assertEqual(
            ("build", "archive", "drill"),
            tool.DEPENDENCIES["swap"],
        )
        self.assertEqual(
            ("repatriate", "archive", "drill"),
            tool.DEPENDENCIES["drop"],
        )
        self.assertEqual(
            ("validate", "archive", "drill"),
            tool.DEPENDENCIES["repatriate"],
        )
        self.assertNotIn("drop", tool.DEPENDENCIES["rollback"])

    def test_source_contains_no_cascade_or_arbitrary_drop(self):
        source = SCRIPT.read_text(encoding="utf-8")

        self.assertNotIn("CASCADE", source)
        self.assertIn(
            "DROP TABLE {qualified(retired)}",
            source,
        )
        self.assertNotIn("DROP TABLE IF EXISTS", source)


if __name__ == "__main__":
    unittest.main()
