import importlib.util
import json
import os
import pathlib
import shutil
import types
import unittest
from datetime import datetime, timezone


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


def plan_fixture():
    return {
        "planIdentity": {
            "exactRetainedRows": 6_691_993,
            "exactTotalRows": 300_000_000,
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

    def test_capacity_gate_preserves_emergency_floor(self):
        result = tool.calculate_capacity(
            plan_fixture(),
            profile_fixture(),
            66_000_000_000,
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
        )

        self.assertTrue(result["measured"])
        self.assertEqual(
            "synthetic-pg17-profile",
            result["profileId"],
        )
        self.assertTrue(result["allowed"])
        self.assertLess(result["requiredFreeBytes"], 500 * 1024**3)

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
            ("validate", "archive", "drill"),
            tool.DEPENDENCIES["drop"],
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
