import contextlib
import copy
import importlib.util
import json
import os
import pathlib
import shutil
import subprocess
import sys
import unittest
from types import SimpleNamespace
from unittest import mock


TOOLS_DIR = pathlib.Path(__file__).resolve().parent
SCRIPT = TOOLS_DIR / "postgres-snapshot-generation-archive.py"
WRAPPER = TOOLS_DIR / "postgres-snapshot-generation-archive.sh"
CSHARP_FIXTURE = (
    TOOLS_DIR
    / "testdata"
    / "postgres-snapshot-generation-archive-csharp-fixture"
    / "Fixture.csproj"
)
WORK_ROOT = (
    TOOLS_DIR
    / "testdata"
    / "postgres-snapshot-generation-archive"
    / f".work-{os.getpid()}"
)


def load_tool():
    spec = importlib.util.spec_from_file_location(
        "postgres_snapshot_generation_archive",
        SCRIPT,
    )
    module = importlib.util.module_from_spec(spec)
    sys.modules[spec.name] = module
    spec.loader.exec_module(module)
    return module


tool = load_tool()


class Completed:
    def __init__(self, stdout=b"", stderr=b"", returncode=0):
        self.stdout = stdout
        self.stderr = stderr
        self.returncode = returncode


def cycle():
    return {
        "cycle_id": 8,
        "trigger_scrape_id": 1328,
        "trigger_publication_id": 145,
        "safe_point_kind": "terminal_worker_post_publication",
        "safe_point_at": "2026-08-29T20:00:00Z",
        "planner_version": tool.PLANNER_VERSION,
        "config_version": tool.CONFIG_VERSION,
        "report_only": True,
        "status": "observed",
        "oracle_agreement": True,
        "candidate_count": 1,
        "protected_count": 0,
        "blocked_count": 0,
        "candidate_bytes": 100,
        "global_blockers": [],
        "anomalies": [],
        "error_message": None,
    }


def target(instrument="Solo_Guitar", snapshot_id=100):
    definition = tool.INSTRUMENT_BY_NAME[instrument]
    value = {
        "observation_id": 1,
        "cycle_id": 8,
        "report_only": True,
        "instrument": instrument,
        "snapshot_id": snapshot_id,
        "root_schema": "public",
        "root_relation": definition["rootRelation"],
        "snapshot_parent_oid": 10,
        "root_oid": 20,
        "root_partition_key": "LIST (snapshot_id)",
        "root_partition_bound": f"FOR VALUES IN ('{instrument}')",
        "root_tablespace_name": "pg_default",
        "root_relation_options": [],
        "root_index_configuration": [],
        "child_schema": "public",
        "child_relation": f"{definition['rootRelation']}_s{snapshot_id}",
        "child_oid": 30,
        "child_relfilenode": 40,
        "partition_bound": f"FOR VALUES IN ('{snapshot_id}')",
        "tablespace_name": "pg_default",
        "relation_kind": "r",
        "persistence_kind": "p",
        "access_method": "heap",
        "relation_options": [],
        "index_configuration": [],
        "row_estimate": 1,
        "total_bytes": 100,
        "classification": "candidate",
        "planner_live": False,
        "oracle_live": False,
        "root_reasons": [],
        "blocker_codes": [],
    }
    value["details"] = {
        "childPhysicalKey": tool.observation_physical_key(value),
        "rootReasons": [],
        "blockers": [],
    }
    camel = {
        "instrument": value["instrument"],
        "rootSchema": value["root_schema"],
        "rootRelation": value["root_relation"],
        "snapshotParentOid": value["snapshot_parent_oid"],
        "rootOid": value["root_oid"],
        "rootPartitionKey": value["root_partition_key"],
        "rootPartitionBound": value["root_partition_bound"],
        "rootTablespaceName": value["root_tablespace_name"],
        "rootRelationOptions": value["root_relation_options"],
        "rootIndexes": value["root_index_configuration"],
        "childSchema": value["child_schema"],
        "childRelation": value["child_relation"],
        "snapshotId": value["snapshot_id"],
        "childOid": value["child_oid"],
        "childRelfilenode": value["child_relfilenode"],
        "partitionBound": value["partition_bound"],
        "tablespaceName": value["tablespace_name"],
        "relationKind": value["relation_kind"],
        "persistenceKind": value["persistence_kind"],
        "accessMethod": value["access_method"],
        "relationOptions": value["relation_options"],
        "indexes": value["index_configuration"],
    }
    value["stable_child_identity_hash"] = tool.stable_hash(
        tool.stable_identity_document(camel)
    )
    value["stable_config_schema_hash"] = tool.stable_hash(
        tool.stable_config_document(camel)
    )
    value["observation_metrics_hash"] = tool.stable_hash(
        {
            "stableChildIdentityHash":
                value["stable_child_identity_hash"],
            "rowEstimate": value["row_estimate"],
            "totalBytes": value["total_bytes"],
        }
    )
    return value


def refresh_authentic(value):
    material = tool.authentic_cycle_material(
        value["cycle"],
        value["observations"],
    )
    cycle_value = value["cycle"]
    cycle_value["candidate_identity_hash"] = material[
        "candidateIdentityHash"
    ]
    cycle_value["observation_hash"] = material["observationHash"]
    for key, source in (
        ("planner_child_set", "children"),
        ("planner_live_set", "live"),
        ("planner_candidate_set", "candidates"),
        ("oracle_child_set", "children"),
        ("oracle_live_set", "live"),
        ("oracle_candidate_set", "candidates"),
    ):
        cycle_value[key] = material[source]
    material = tool.authentic_cycle_material(
        cycle_value,
        value["observations"],
    )
    summary = {
        "evidence_id": 1,
        "cycle_id": cycle_value["cycle_id"],
        "observation_id": None,
        "sequence": 1,
        "phase": "observation",
        "kind": "summary",
        "payload": material["summaryPayload"],
        "previous_hash": None,
    }
    summary["current_hash"] = tool.evidence_hash(
        cycle_value["cycle_id"],
        summary,
    )
    evaluation = material["evaluations"][0]
    child = {
        "evidence_id": 2,
        "cycle_id": cycle_value["cycle_id"],
        "observation_id": evaluation["observationId"],
        "sequence": 2,
        "phase": "observation",
        "kind": "child",
        "payload": {
            key: evaluation[key]
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
        },
        "previous_hash": summary["current_hash"],
    }
    child["current_hash"] = tool.evidence_hash(
        cycle_value["cycle_id"],
        child,
    )
    value["evidence"] = [summary, child]
    value["target"] = value["observations"][0]
    return value


def preflight(instrument="Solo_Guitar", snapshot_id=100):
    observation = target(instrument, snapshot_id)
    value = {
        "cycle": cycle(),
        "candidateCountActual": 1,
        "target": observation,
        "observations": [observation],
        "evidence": [],
        "publicationState": {
            "published_scrape_id": 1328,
            "current_publication_id": 145,
            "working_publication_id": None,
            "public_reads_frozen": False,
            "publication_commit_intent_started_at": None,
            "publication_commit_intent_heartbeat_at": None,
            "publication_commit_intent_owner": None,
            "max_score_mutation_gate_token": None,
            "max_score_mutation_gate_publication_id": None,
            "max_score_mutation_gate_backend_pid": None,
            "max_score_mutation_gate_backend_start": None,
            "max_score_mutation_gate_acquired_at": None,
            "improvement_notifications_scrape_id": 1328,
            "improvement_notifications_status": "completed",
            "improvement_notifications_completed_at": "2026-08-29T20:00:00Z",
            "improvement_notifications_projection_ready": True,
            "improvement_notifications_projection_scrape_id": 1328,
        },
        "triggerScrape": {
            "id": 1328,
            "status": "completed",
            "completed_at": "2026-08-29T20:00:00Z",
            "failed_at": None,
        },
        "triggerPublication": {
            "publication_id": 145,
            "scrape_id": 1328,
            "status": "current",
        },
        "runningScrapes": [],
        "activeHoldCount": 0,
        "unreplayedWriterFailureCount": 0,
    }
    return refresh_authentic(value)


class SnapshotGenerationArchiveTests(unittest.TestCase):
    def setUp(self):
        shutil.rmtree(WORK_ROOT, ignore_errors=True)
        WORK_ROOT.mkdir(parents=True, mode=0o700)

    def tearDown(self):
        shutil.rmtree(WORK_ROOT, ignore_errors=True)
        shutil.rmtree(CSHARP_FIXTURE.parent / "bin", ignore_errors=True)
        shutil.rmtree(CSHARP_FIXTURE.parent / "obj", ignore_errors=True)

    def test_fixed_allowlist_is_exactly_nine_instruments(self):
        self.assertEqual(9, len(tool.INSTRUMENTS))
        self.assertEqual(
            {
                "Solo_Guitar",
                "Solo_Bass",
                "Solo_Vocals",
                "Solo_Drums",
                "Solo_PeripheralGuitar",
                "Solo_PeripheralBass",
                "Solo_PeripheralVocals",
                "Solo_PeripheralCymbals",
                "Solo_PeripheralDrums",
            },
            set(tool.INSTRUMENT_BY_NAME),
        )

    def test_dotnet_canonical_json_escaping_matches_planner_serializer(self):
        self.assertEqual(
            (
                b'{"x":"FOR VALUES IN (\\u0027Solo_Guitar\\u0027)'
                b'\\u002B\\u003C\\u003E\\u0026\\u0060\\u00E9'
                b' \\u0022quoted\\u0022"}'
            ),
            tool.dotnet_canonical_json_bytes(
                {"x": "FOR VALUES IN ('Solo_Guitar')+<>&`é \"quoted\""}
            ),
        )
        self.assertEqual(
            (
                b'{"x":"\\u0000\\b\\t\\n\\u000B\\f\\r\\u001F'
                b'\\\\/\\u007F\\uD83D\\uDE00"}'
            ),
            tool.dotnet_canonical_json_bytes(
                {"x": "\0\b\t\n\v\f\r\x1f\\/\x7f😀"}
            ),
        )

    def test_real_csharp_nonempty_evidence_fixture_validates(self):
        completed = subprocess.run(
            [
                "dotnet",
                "run",
                "--project",
                str(CSHARP_FIXTURE),
                "--configuration",
                "Release",
                "--nologo",
                "--verbosity",
                "quiet",
            ],
            capture_output=True,
            text=True,
            timeout=120,
            check=True,
        )
        value = json.loads(completed.stdout.strip().splitlines()[-1])
        tool.validate_preflight(value)
        summary = value["evidence"][0]["payload"]
        self.assertGreaterEqual(
            len(summary["plannerPublicationSourceValidations"]),
            2,
        )
        self.assertGreaterEqual(
            len(summary["plannerIndexTopologyValidations"]),
            2,
        )
        topology = summary["plannerIndexTopologyValidations"][0]
        self.assertIsInstance(topology, dict)
        self.assertGreaterEqual(
            len(topology["effectiveNumericChildIndexValidations"]),
            1,
        )
        self.assertIn(
            "\\u0022primary\\u0022",
            topology["comparisonKey"],
        )
        altered_key = copy.deepcopy(value)
        altered_key["evidence"][0]["payload"][
            "plannerPublicationSourceValidations"
        ][0]["comparisonKey"] = "{}"
        with self.assertRaisesRegex(tool.ArchiveError, "comparisonKey"):
            tool.validate_preflight(altered_key)
        altered_order = copy.deepcopy(value)
        altered_order["evidence"][0]["payload"][
            "plannerIndexTopologyValidations"
        ].reverse()
        with self.assertRaisesRegex(tool.ArchiveError, "evidence hash"):
            tool.validate_preflight(altered_order)
        altered_hash = copy.deepcopy(value)
        altered_hash["cycle"]["observation_hash"] = "f" * 64
        with self.assertRaisesRegex(tool.ArchiveError, "observation hash"):
            tool.validate_preflight(altered_hash)

    def test_storage_guard_rejects_outside_and_symlink_paths(self):
        approved = WORK_ROOT / "approved"
        approved.mkdir()
        outside = WORK_ROOT / "outside"
        outside.mkdir()
        with self.assertRaisesRegex(tool.ArchiveError, "below approved"):
            tool.validate_storage_path(
                outside,
                approved,
                must_exist=True,
            )
        real = approved / "real"
        real.mkdir()
        link = approved / "link"
        link.symlink_to(real, target_is_directory=True)
        with self.assertRaisesRegex(tool.ArchiveError, "symbolic link"):
            tool.validate_storage_path(
                link,
                approved,
                must_exist=True,
            )
        with self.assertRaisesRegex(tool.ArchiveError, "traversal"):
            tool.validate_storage_path(
                approved / "real" / ".." / "new",
                approved,
                must_exist=False,
            )

    def test_latest_candidate_sql_uses_newest_cycle_and_smallest_candidate(self):
        sql = tool.candidate_selection_sql()
        self.assertIn("ORDER BY created_at DESC, cycle_id DESC", sql)
        self.assertIn("observation.classification = 'candidate'", sql)
        self.assertIn("observation.planner_live = FALSE", sql)
        self.assertIn("observation.oracle_live = FALSE", sql)
        self.assertIn("observation.snapshot_id", sql)
        self.assertNotIn("WHERE observation.instrument =", sql)

        exact = tool.candidate_selection_sql("solo-bass", 99)
        self.assertIn("observation.instrument = 'Solo_Bass'", exact)
        self.assertIn("observation.snapshot_id = 99", exact)
        with self.assertRaisesRegex(tool.ArchiveError, "supplied together"):
            tool.candidate_selection_sql("solo-bass", None)

    def test_preflight_rejects_missing_blocked_and_mismatched_planner_state(self):
        with self.assertRaisesRegex(tool.ArchiveError, "missing"):
            tool.validate_preflight({})
        blocked = preflight()
        blocked["cycle"]["status"] = "blocked"
        blocked["cycle"]["blocked_count"] = 1
        blocked["cycle"]["candidate_count"] = 0
        blocked["cycle"]["candidate_bytes"] = 0
        blocked["observations"][0]["classification"] = "blocked"
        blocked["observations"][0]["details"]["blockers"] = [
            {"code": "synthetic", "detail": "blocked"}
        ]
        blocked["observations"][0]["blocker_codes"] = ["synthetic"]
        refresh_authentic(blocked)
        with self.assertRaises(tool.ArchiveError):
            tool.validate_preflight(blocked)
        mismatch = preflight()
        mismatch["publicationState"]["current_publication_id"] = 999
        with self.assertRaisesRegex(tool.ArchiveError, "differs"):
            tool.validate_preflight(mismatch)
        held = preflight()
        held["activeHoldCount"] = 1
        with self.assertRaisesRegex(tool.ArchiveError, "hold"):
            tool.validate_preflight(held)

    def test_solo_bass_1308_is_unconditionally_rejected(self):
        value = preflight("Solo_Bass", 1308)
        with self.assertRaisesRegex(tool.ArchiveError, "1308"):
            tool.validate_preflight(value)

    def test_arbitrary_cycle_and_evidence_hashes_are_rejected(self):
        value = preflight()
        value["cycle"]["candidate_identity_hash"] = "a" * 64
        with self.assertRaisesRegex(tool.ArchiveError, "candidate identity"):
            tool.validate_preflight(value)
        value = preflight()
        value["evidence"][1]["current_hash"] = "b" * 64
        with self.assertRaisesRegex(tool.ArchiveError, "evidence hash"):
            tool.validate_preflight(value)
        value = preflight()
        value["cycle"]["planner_version"] += 1
        with self.assertRaisesRegex(tool.ArchiveError, "planner version"):
            tool.validate_preflight(value)

    def test_planner_sets_summary_and_linkage_tamper_are_rejected(self):
        value = preflight()
        value["cycle"]["planner_candidate_set"] = []
        with self.assertRaisesRegex(tool.ArchiveError, "planner_candidate_set"):
            tool.validate_preflight(value)
        value = preflight()
        value["evidence"][0]["payload"]["candidateIdentityHash"] = "c" * 64
        with self.assertRaisesRegex(tool.ArchiveError, "summary payload"):
            tool.validate_preflight(value)
        value = preflight()
        value["evidence"][1]["previous_hash"] = "d" * 64
        with self.assertRaisesRegex(tool.ArchiveError, "linkage"):
            tool.validate_preflight(value)

    def test_exact_identity_drift_is_rejected(self):
        before = {
            "preflightSha256": "a",
            "catalogPhysicalSha256": "b",
            "logicalCatalogSha256": "c",
            "targetOid": 1,
            "targetRelfilenode": 2,
            "heapBytes": 3,
            "indexBytes": 4,
            "totalBytes": 7,
            "mutationCounters": {
                "inserts": 0,
                "updates": 0,
                "removals": 0,
            },
            "rowFingerprint": {"rowCount": 1, "sha256": "d"},
        }
        tool.compare_source_fences(before, dict(before))
        after = json.loads(json.dumps(before))
        after["targetRelfilenode"] = 3
        with self.assertRaisesRegex(tool.ArchiveError, "targetRelfilenode"):
            tool.compare_source_fences(before, after)

    def test_output_overlap_uses_path_ancestry_not_string_prefix(self):
        root = WORK_ROOT / "storage"
        source = root / "postgres"
        sibling = root / "postgres-archive"
        child = source / "child"
        child.mkdir(parents=True)
        sibling.mkdir()
        self.assertTrue(tool.paths_overlap(source, child))
        self.assertFalse(tool.paths_overlap(source, sibling))
        tool.reject_output_overlap(sibling, [source])
        with self.assertRaisesRegex(tool.ArchiveError, "overlaps"):
            tool.reject_output_overlap(child, [source])
        with self.assertRaisesRegex(tool.ArchiveError, "outside expected"):
            tool.posix_relative_to(
                "/var/lib/postgresql/data-other",
                "/var/lib/postgresql/data",
            )

    def test_mount_identity_detects_bind_alias_and_nested_device(self):
        pgdata = {
            "sourceDevice": "/dev/test",
            "deviceId": "8:1",
            "filesystemObjectPath": "/fst/pgdata",
        }
        alias = {
            "sourceDevice": "/dev/test",
            "deviceId": "8:1",
            "filesystemObjectPath": "/fst/pgdata/archive",
        }
        sibling = {
            "sourceDevice": "/dev/test",
            "deviceId": "8:1",
            "filesystemObjectPath": "/fst/pgdata-archive",
        }
        self.assertTrue(tool.mount_locations_overlap(pgdata, alias))
        self.assertFalse(tool.mount_locations_overlap(pgdata, sibling))
        reference = {
            "sourceDevice": "/dev/test",
            "deviceId": "8:1",
            "filesystemType": "ext4",
        }
        nested_device = {
            "sourceDevice": "/dev/other",
            "deviceId": "8:2",
            "filesystemType": "ext4",
        }
        with self.assertRaisesRegex(tool.ArchiveError, "mount identity"):
            tool.require_same_mount_device(
                reference,
                nested_device,
                "nested data_directory",
            )

    def test_mocked_findmnt_bind_alias_is_rejected(self):
        findmnt = {
            "filesystems": [
                {
                    "source": "/dev/test[/fst/pgdata]",
                    "fstype": "ext4",
                    "fsroot": "/fst/pgdata",
                    "maj:min": "8:1",
                    "target": "/archive-alias",
                }
            ]
        }
        with (
            mock.patch.object(
                tool,
                "run",
                return_value=Completed(stdout=json.dumps(findmnt).encode()),
            ),
            mock.patch.object(
                tool.shutil,
                "disk_usage",
                return_value=SimpleNamespace(total=10, used=1, free=9),
            ),
            mock.patch.object(tool.os, "stat") as stat_result,
        ):
            stat_result.return_value.st_dev = 99
            evidence = tool.mount_evidence("/archive-alias")
        self.assertEqual("/dev/test", evidence["sourceDevice"])
        alias_location = tool.mount_location(
            "/archive-alias/package",
            evidence,
        )
        self.assertEqual(
            "/fst/pgdata/package",
            alias_location["filesystemObjectPath"],
        )
        protected = {
            "path": "/real/pgdata",
            "location": {
                "sourceDevice": "/dev/test",
                "deviceId": "8:1",
                "filesystemObjectPath": "/fst/pgdata",
            },
        }
        with mock.patch.object(
            tool,
            "mount_evidence",
            return_value=evidence,
        ):
            with self.assertRaisesRegex(tool.ArchiveError, "overlaps"):
                tool.reject_output_overlap(
                    "/archive-alias/package",
                    [protected],
                )

    def test_nested_mount_boundary_is_rejected(self):
        root = WORK_ROOT / "nested-root"
        child = root / "child"
        child.mkdir(parents=True)
        entries = [
            {
                "source": "/dev/test",
                "filesystemType": "ext4",
                "fsRoot": "/",
                "deviceId": "8:1",
                "mountTarget": str(root),
            },
            {
                "source": "/dev/other",
                "filesystemType": "xfs",
                "fsRoot": "/",
                "deviceId": "8:2",
                "mountTarget": str(child),
            },
        ]
        with self.assertRaisesRegex(tool.ArchiveError, "nested mount"):
            tool.reject_nested_mounts(
                root,
                "test root",
                entries=entries,
            )

    def test_archive_discovers_protected_storage_before_reservation(self):
        events = []

        @contextlib.contextmanager
        def fake_lock(*_):
            events.append("lock")
            yield {"path": "lock", "device": 1}

        args = SimpleNamespace(
            output="/approved/package",
            instrument=None,
            snapshot_id=None,
        )
        with (
            mock.patch.object(
                tool,
                "validate_storage_path",
                return_value=pathlib.Path("/approved/package"),
            ) as validate,
            mock.patch.object(
                tool,
                "discover_source_storage",
                side_effect=lambda *_: (
                    events.append("discover")
                    or (None, None, None, [])
                ),
            ),
            mock.patch.object(
                tool,
                "mount_evidence",
                return_value={
                    "sourceDevice": "/dev/fst",
                    "filesystemType": "ext4",
                    "fsRoot": "/",
                    "deviceId": "8:1",
                    "mountTarget": "/approved",
                },
            ),
            mock.patch.object(tool, "reject_output_overlap"),
            mock.patch.object(tool, "operation_lock", fake_lock),
            mock.patch.object(
                tool,
                "_archive_locked",
                side_effect=lambda *_: events.append("archive"),
            ),
        ):
            tool.archive(args)
        self.assertEqual(["discover", "lock", "archive"], events)
        self.assertEqual(tool.ARCHIVE_ROOT, validate.call_args.args[1])

    def test_public_archive_alias_rejects_before_lock_or_output_write(self):
        archive_root = WORK_ROOT / "archive-root"
        archive_root.mkdir()
        package = archive_root / "package"
        args = SimpleNamespace(
            output=str(package),
            instrument=None,
            snapshot_id=None,
        )
        alias_mount = {
            "sourceDevice": "/dev/fst",
            "filesystemType": "ext4",
            "fsRoot": "/live/pgdata",
            "deviceId": "8:1",
            "mountTarget": str(archive_root),
        }
        protected = {
            "path": "/real/pgdata",
            "location": {
                "sourceDevice": "/dev/fst",
                "deviceId": "8:1",
                "filesystemObjectPath": "/live/pgdata",
            },
        }

        def unsafe_discovery(*_):
            tool.reject_output_overlap(
                package,
                [protected],
                output_mount=alias_mount,
            )

        with (
            mock.patch.object(tool, "require_commands"),
            mock.patch.object(tool, "ARCHIVE_ROOT", archive_root),
            mock.patch.object(
                tool,
                "validate_storage_path",
                return_value=package,
            ),
            mock.patch.object(
                tool,
                "discover_source_storage",
                side_effect=unsafe_discovery,
            ),
            mock.patch.object(
                tool,
                "operation_lock",
            ) as mocked_lock,
        ):
            with self.assertRaisesRegex(tool.ArchiveError, "overlaps"):
                tool.archive(args)
        mocked_lock.assert_not_called()
        self.assertEqual([], list(archive_root.iterdir()))

    def test_tablespaces_map_only_through_explicit_approved_mounts(self):
        storage = WORK_ROOT / "storage"
        pgdata = storage / "pgdata"
        custom = storage / "tablespaces" / "fast"
        pgdata.mkdir(parents=True)
        custom.mkdir(parents=True)
        identity = {
            "pgdataEnvironment": "/var/lib/postgresql/data",
            "mounts": [
                {
                    "destination": "/var/lib/postgresql/data",
                    "source": str(pgdata),
                    "type": "bind",
                    "readWrite": True,
                },
                {
                    "destination": "/fst-tablespaces",
                    "source": str(storage / "tablespaces"),
                    "type": "bind",
                    "readWrite": True,
                },
            ],
        }
        rows = [
            {"oid": 1, "name": "pg_default", "containerPath": ""},
            {
                "oid": 2,
                "name": "fast",
                "containerPath": "/fst-tablespaces/fast",
            },
        ]
        with mock.patch.object(tool, "psql_json", return_value=rows):
            result = tool.tablespace_inventory(
                "source",
                "fst",
                "fstservice",
                identity,
                storage,
            )
        self.assertEqual(str(pgdata.resolve()), result[0]["hostPath"])
        self.assertEqual(str(custom.resolve()), result[1]["hostPath"])
        rows[1]["containerPath"] = "/unmounted/fast"
        with mock.patch.object(tool, "psql_json", return_value=rows):
            with self.assertRaisesRegex(tool.ArchiveError, "explicit"):
                tool.tablespace_inventory(
                    "source",
                    "fst",
                    "fstservice",
                    identity,
                    storage,
                )

    def test_primary_key_and_fingerprint_are_exact(self):
        child = {
            "indexes": [
                {
                    "isPrimary": True,
                    "isUnique": True,
                    "accessMethod": "btree",
                    "columnNames": list(tool.PK_COLUMNS),
                }
            ]
        }
        tool.validate_primary_key(child)
        sql = tool.fingerprint_sql("public", "safe_child")
        self.assertIn("to_jsonb(row_value)::text", sql)
        self.assertIn(
            'ORDER BY "snapshot_id", "song_id", "instrument", "account_id"',
            sql,
        )
        child["indexes"][0]["columnNames"] = ["snapshot_id", "song_id"]
        with self.assertRaisesRegex(tool.ArchiveError, "shape"):
            tool.validate_primary_key(child)

    def test_fingerprint_command_uses_operation_specific_timeout(self):
        sql = tool.fingerprint_sql("public", "safe_child")
        command = tool.psql_command(
            "source",
            "fst",
            "fstservice",
            sql,
            statement_timeout_seconds=3570,
        )
        options = next(
            value for value in command if value.startswith("PGOPTIONS=")
        )
        self.assertIn("statement_timeout=3570s", options)
        self.assertNotIn("statement_timeout=120s", options)

    def test_archive_command_contains_only_observed_relation_allowlist(self):
        args = SimpleNamespace(
            source_container="synthetic-postgres",
            pg_user="fst",
            pg_database="fstservice",
        )
        observed = {
            "root_relation": "leaderboard_entries_snapshot_solo_guitar",
            "child_relation": "leaderboard_entries_snapshot_solo_guitar_s100",
            "child_schema": "public",
        }
        command = tool.archive_command(
            args,
            observed,
            "immutable-container-id",
        )
        tables = [
            command[index + 1]
            for index, value in enumerate(command)
            if value == "--table"
        ]
        self.assertEqual(
            [
                "public.leaderboard_entries_snapshot",
                "public.leaderboard_entries_snapshot_solo_guitar",
                "public.leaderboard_entries_snapshot_solo_guitar_s100",
            ],
            tables,
        )
        self.assertEqual(
            "immutable-container-id",
            command[command.index("pg_dump") - 1],
        )
        self.assertNotIn("synthetic-postgres", command)
        self.assertIn("--strict-names", command)
        self.assertIn("--lock-wait-timeout=5s", command)
        self.assertNotIn("--schema", command)

    def test_checksum_tamper_is_rejected(self):
        package = WORK_ROOT / "package"
        package.mkdir()
        for name in tool.PACKAGE_FILES:
            (package / name).write_bytes(name.encode())
        tool.write_checksums(package, tool.PACKAGE_FILES)
        tool.read_checksums(package)
        (package / "archive.custom").write_bytes(b"tampered")
        with self.assertRaisesRegex(tool.ArchiveError, "checksum failed"):
            tool.read_checksums(package)

    def test_restore_container_is_network_none_without_ports(self):
        command = tool.restore_container_command(
            "proof",
            "sha256:" + "1" * 64,
            "/approved/colon:path/proof/.scratch/pgdata",
            "/approved/colon:path/package",
            "proof-id",
            "f" * 64,
            "1g",
            "1",
        )
        self.assertEqual("none", command[command.index("--network") + 1])
        self.assertNotIn("-p", command)
        self.assertNotIn("--publish", command)
        mounts = [
            command[index + 1]
            for index, value in enumerate(command)
            if value == "--mount"
        ]
        self.assertIn(
            "type=bind,src=/approved/colon:path/package,dst=/package,readonly",
            mounts,
        )
        self.assertIn(
            (
                "type=bind,src=/approved/colon:path/proof/.scratch/pgdata,"
                "dst=/var/lib/postgresql/data"
            ),
            mounts,
        )
        self.assertIn("--memory", command)
        self.assertIn("--cpus", command)
        self.assertIn("data_directory=/var/lib/postgresql/data", command)
        with self.assertRaisesRegex(tool.ArchiveError, "delimiter"):
            tool.docker_bind_mount(
                "/approved/comma,path",
                "/package",
                readonly=True,
            )
        with self.assertRaisesRegex(tool.ArchiveError, "delimiter"):
            tool.docker_bind_mount(
                '/approved/quote"path',
                "/package",
                readonly=True,
            )

    def test_proof_container_rejects_unexpected_writable_mount(self):
        storage = WORK_ROOT / "storage"
        package = storage / "package"
        pgdata = storage / "proof" / "pgdata"
        extra = storage / "extra"
        package.mkdir(parents=True)
        pgdata.mkdir(parents=True)
        extra.mkdir()
        inspected = [
            {
                "Id": "container-id",
                "Image": "image-id",
                "Config": {
                    "Labels": {
                        "fst.tool": tool.TOOL_ID,
                        "fst.proof": "proof-id",
                        "fst.package": "f" * 64,
                    }
                },
                "HostConfig": {
                    "NetworkMode": "none",
                    "PortBindings": None,
                    "NanoCpus": 1_000_000_000,
                    "Memory": 1024,
                    "PidsLimit": 256,
                },
                "Mounts": [
                    {
                        "Destination": "/package",
                        "Source": str(package),
                        "RW": False,
                        "Type": "bind",
                    },
                    {
                        "Destination": "/var/lib/postgresql/data",
                        "Source": str(pgdata),
                        "RW": True,
                        "Type": "bind",
                    },
                    {
                        "Destination": "/unexpected",
                        "Source": str(extra),
                        "RW": True,
                        "Type": "bind",
                    },
                ],
            }
        ]
        with mock.patch.object(
            tool,
            "run",
            return_value=Completed(
                stdout=json.dumps(inspected).encode("utf-8")
            ),
        ):
            with self.assertRaisesRegex(tool.ArchiveError, "unexpected"):
                tool.inspect_proof_container(
                    "proof",
                    "proof-id",
                    "f" * 64,
                    package,
                    pgdata,
                    storage,
                )

    def test_bind_alias_proofs_parent_rejects_before_any_write(self):
        package = WORK_ROOT / "package"
        proofs = package / "proofs"
        proofs.mkdir(parents=True)
        storage_mount = {
            "sourceDevice": "/dev/fst",
            "filesystemType": "ext4",
            "fsRoot": "/",
            "deviceId": "8:1",
            "mountTarget": str(WORK_ROOT),
        }
        package_mount = {
            **storage_mount,
            "mountTarget": str(WORK_ROOT),
        }
        alias_mount = {
            "sourceDevice": "/dev/fst",
            "filesystemType": "ext4",
            "fsRoot": "/live/pgdata",
            "deviceId": "8:1",
            "mountTarget": str(proofs),
        }
        protected = {
            "path": "/real/pgdata",
            "location": {
                "sourceDevice": "/dev/fst",
                "filesystemType": "ext4",
                "fsRoot": "/",
                "deviceId": "8:1",
                "mountTarget": "/real",
                "filesystemObjectPath": "/live/pgdata",
            },
        }
        manifest = {
            "sourceIdentity": {
                "protectedHostLocations": [protected],
            }
        }
        args = SimpleNamespace(
            package=str(package),
            storage_root=str(WORK_ROOT),
            proof_id="alias-proof",
            proof_output=None,
        )
        with (
            mock.patch.object(tool, "require_commands"),
            mock.patch.object(tool, "ARCHIVE_ROOT", package),
            mock.patch.object(
                tool,
                "load_completed_package",
                return_value=(
                    package,
                    manifest,
                    {},
                    {"manifest.json": "f" * 64},
                ),
            ),
            mock.patch.object(
                tool,
                "mount_evidence",
                side_effect=[
                    storage_mount,
                    package_mount,
                    package_mount,
                    alias_mount,
                ],
            ),
            mock.patch.object(tool, "reject_nested_mounts"),
            mock.patch.object(
                tool,
                "operation_lock",
            ) as mocked_lock,
        ):
            with self.assertRaisesRegex(tool.ArchiveError, "overlaps"):
                tool.prove(args)
        mocked_lock.assert_not_called()
        self.assertEqual([], list(proofs.iterdir()))

    def test_scratch_cleanup_guard_requires_exact_owned_path(self):
        proof = WORK_ROOT / "proof"
        scratch = proof / ".scratch"
        pgdata = scratch / "pgdata"
        pgdata.mkdir(parents=True)
        proof_id = "proof-1"
        tool.write_json(
            scratch / "owner.json",
            {
                "toolId": tool.TOOL_ID,
                "proofId": proof_id,
                "scratch": str(scratch.resolve()),
            },
        )
        self.assertEqual(
            pgdata.resolve(),
            tool.validate_owned_scratch(
                scratch,
                proof,
                proof_id,
            ).resolve(),
        )
        with self.assertRaisesRegex(tool.ArchiveError, "outside"):
            tool.validate_owned_scratch(
                scratch,
                WORK_ROOT,
                proof_id,
            )

    def test_capacity_and_reservation_lock_are_fail_closed(self):
        self.assertEqual(
            2 * 1024**3,
            tool.required_capacity_bytes(1, 1),
        )
        self.assertEqual(
            7 * 1024**3,
            tool.required_capacity_bytes(3 * 1024**3, 0),
        )
        storage = WORK_ROOT / "storage"
        archive = storage / "archive"
        lock = storage / "operation.lock"
        archive.mkdir(parents=True)
        lock.write_bytes(b"")
        with (
            mock.patch.object(tool, "DEFAULT_STORAGE_ROOT", storage),
            mock.patch.object(tool, "ARCHIVE_ROOT", archive),
            mock.patch.object(tool, "OPERATION_LOCK_PATH", lock),
        ):
            expected = tool.mount_evidence(archive)
            with tool.operation_lock(expected, []):
                with self.assertRaisesRegex(tool.ArchiveError, "reservation"):
                    with tool.operation_lock(expected, []):
                        pass

    def test_operation_lock_rejects_remount_without_creating_files(self):
        storage = WORK_ROOT / "storage"
        archive = storage / "archive"
        lock = storage / "operation.lock"
        archive.mkdir(parents=True)
        lock.write_bytes(b"pre-provisioned")
        expected = {
            "sourceDevice": "/dev/fst",
            "filesystemType": "ext4",
            "fsRoot": "/safe",
            "deviceId": "8:1",
            "mountTarget": str(archive),
        }
        changed = {
            **expected,
            "fsRoot": "/live/pgdata",
        }
        lock_parent = {
            **expected,
            "mountTarget": str(storage),
        }
        with (
            mock.patch.object(tool, "DEFAULT_STORAGE_ROOT", storage),
            mock.patch.object(tool, "ARCHIVE_ROOT", archive),
            mock.patch.object(tool, "OPERATION_LOCK_PATH", lock),
            mock.patch.object(
                tool,
                "validate_storage_path",
                return_value=archive,
            ),
            mock.patch.object(
                tool,
                "mount_evidence",
                side_effect=[
                    expected,
                    lock_parent,
                    lock_parent,
                    changed,
                ],
            ),
            mock.patch.object(tool, "reject_nested_mounts"),
            mock.patch.object(tool, "reject_output_overlap"),
            mock.patch.object(tool, "require_same_mount_device"),
        ):
            with self.assertRaisesRegex(tool.ArchiveError, "remounted"):
                with tool.operation_lock(expected, []):
                    pass
        self.assertEqual([], list(archive.iterdir()))
        self.assertEqual(b"pre-provisioned", lock.read_bytes())

    def test_operation_lock_never_creates_missing_lock_file(self):
        storage = WORK_ROOT / "storage"
        archive = storage / "archive"
        lock = storage / "missing.lock"
        archive.mkdir(parents=True)
        expected = tool.mount_evidence(archive)
        with (
            mock.patch.object(tool, "DEFAULT_STORAGE_ROOT", storage),
            mock.patch.object(tool, "ARCHIVE_ROOT", archive),
            mock.patch.object(tool, "OPERATION_LOCK_PATH", lock),
        ):
            with self.assertRaisesRegex(tool.ArchiveError, "pre-provisioned"):
                with tool.operation_lock(expected, []):
                    pass
        self.assertFalse(lock.exists())

    def test_transient_container_removal_error_clears_after_absence(self):
        with (
            mock.patch.object(
                tool,
                "owned_proof_containers",
                side_effect=[["owned"], []],
            ),
            mock.patch.object(
                tool,
                "run",
                side_effect=tool.ArchiveError("transient daemon error"),
            ),
            mock.patch.object(
                tool,
                "owned_container_volumes",
                return_value=[],
            ),
            mock.patch.object(tool.time, "sleep"),
        ):
            result = tool.remove_owned_proof_containers(
                "proof",
                "f" * 64,
            )
        self.assertTrue(result["containerRemoved"])
        self.assertTrue(result["transientErrorsCleared"])

    def test_same_name_container_replacement_is_rejected(self):
        expected = {
            "containerId": "immutable-source-id",
            "imageId": "image-a",
        }
        replacement = {
            "containerId": "replacement-id",
            "imageId": "image-b",
        }
        with mock.patch.object(
            tool,
            "recapture_source_provenance",
            return_value=replacement,
        ):
            with self.assertRaisesRegex(tool.ArchiveError, "provenance"):
                tool.require_source_provenance(
                    SimpleNamespace(),
                    expected,
                )

    def test_owned_anonymous_volume_must_be_removed(self):
        with mock.patch.object(
            tool,
            "run",
            return_value=Completed(returncode=0),
        ):
            with self.assertRaisesRegex(tool.ArchiveError, "volumes remain"):
                tool.verify_owned_volumes_absent(["anonymous-volume"])

    def test_owned_container_cleanup_uses_rm_v_and_verifies_volume(self):
        commands = []

        def fake_run(arguments, **_):
            commands.append(arguments)
            if arguments[:3] == ["docker", "volume", "inspect"]:
                return Completed(
                    stderr=b"Error response from daemon: no such volume",
                    returncode=1,
                )
            return Completed()

        with (
            mock.patch.object(
                tool,
                "owned_proof_containers",
                side_effect=[["owned-container"], []],
            ),
            mock.patch.object(
                tool,
                "owned_container_volumes",
                return_value=["anonymous-volume"],
            ),
            mock.patch.object(tool, "run", side_effect=fake_run),
            mock.patch.object(tool.time, "sleep"),
        ):
            result = tool.remove_owned_proof_containers(
                "proof",
                "f" * 64,
            )
        remove = next(
            command
            for command in commands
            if command[:2] == ["docker", "rm"]
        )
        self.assertIn("-v", remove)
        self.assertEqual(["anonymous-volume"], result["unexpectedVolumeNames"])
        self.assertTrue(result["ownedVolumesRemoved"])

    def test_cli_and_source_sql_are_strictly_read_only(self):
        parser = tool.build_parser()
        destinations = set()
        for action in parser._actions:
            destinations.add(action.dest)
        for subparser in parser._subparsers._group_actions:
            for child in subparser.choices.values():
                destinations.update(action.dest for action in child._actions)
        self.assertNotIn("relation", destinations)
        self.assertNotIn("table", destinations)
        self.assertNotIn("sql", destinations)
        self.assertNotIn("storage_root", destinations)
        self.assertNotIn("cpus", destinations)
        self.assertNotIn("memory", destinations)
        source = SCRIPT.read_text(encoding="utf-8").lower()
        self.assertNotIn("postgres-snapshot-generation-migration", source)
        self.assertNotIn("postgres-pro-bass-snapshot-rewrite", source)
        tool.assert_read_only_sql("SELECT 1")
        tool.assert_read_only_sql(
            'COPY (SELECT * FROM "public"."safe") TO STDOUT'
        )
        for sql in (
            "UPDATE safe SET value = 1",
            "WITH changed AS (DELETE FROM safe RETURNING *) SELECT * FROM changed",
            "COPY safe FROM STDIN",
            "SELECT pg_terminate_backend(1)",
            "SELECT * INTO copied FROM safe",
            "SELECT 1; SELECT 2",
        ):
            with self.subTest(sql=sql):
                with self.assertRaises(tool.ArchiveError):
                    tool.assert_read_only_sql(sql)

    def test_wrapper_is_thin_and_executes_only_python_tool(self):
        source = WRAPPER.read_text(encoding="utf-8")
        self.assertIn("set -euo pipefail", source)
        self.assertIn("postgres-snapshot-generation-archive.py", source)
        self.assertNotIn("docker", source)
        self.assertNotIn("psql", source)


if __name__ == "__main__":
    unittest.main()
