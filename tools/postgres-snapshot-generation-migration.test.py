import contextlib
import importlib.util
import io
import json
import os
import pathlib
import shutil
import sys
import types
import unittest
from unittest import mock


TOOLS_DIR = pathlib.Path(__file__).resolve().parent
SCRIPT = TOOLS_DIR / "postgres-snapshot-generation-migration.py"
WORK_ROOT = (
    TOOLS_DIR
    / "testdata"
    / "postgres-snapshot-generation-migration"
    / f".work-{os.getpid()}"
)


def load_tool():
    spec = importlib.util.spec_from_file_location(
        "postgres_snapshot_generation_migration",
        SCRIPT,
    )
    module = importlib.util.module_from_spec(spec)
    sys.modules[spec.name] = module
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


def source_state():
    return {
        "oid": 100,
        "relfilenode": 200,
        "relationKind": "r",
        "heapBytes": 1_000_000,
        "indexBytes": 500_000,
        "totalBytes": 1_500_000,
        "treeBytes": 1_500_000,
        "estimatedRows": 1000,
        "inserts": 10,
        "updates": 0,
        "deletes": 0,
        "statisticsResetAt": None,
        "attached": True,
        "partitionBound": "FOR VALUES IN ('Solo_Guitar')",
        "partitionKey": None,
        "owner": "fst",
        "tablespace": "pg_default",
    }


def source_catalog():
    return {
        "relationKind": "r",
        "partitionKey": None,
        "partitionBound": "FOR VALUES IN ('Solo_Guitar')",
        "owner": "fst",
        "tablespace": "pg_default",
        "columns": [
            {
                "ordinal": index + 1,
                "name": name,
                "type": "text",
                "notNull": True,
                "defaultExpression": None,
            }
            for index, name in enumerate(tool.SNAPSHOT_COLUMNS)
        ],
        "constraints": [
            {
                "name": "leaderboard_entries_snapshot_solo_guitar_pkey",
                "type": "p",
                "definition": (
                    "PRIMARY KEY (snapshot_id, song_id, instrument, "
                    "account_id)"
                ),
                "validated": True,
            }
        ],
        "indexes": [
            {
                "name": "leaderboard_entries_snapshot_solo_guitar_pkey",
                "definition": (
                    "CREATE UNIQUE INDEX "
                    "leaderboard_entries_snapshot_solo_guitar_pkey "
                    "ON public.leaderboard_entries_snapshot_solo_guitar "
                    "USING btree (snapshot_id, song_id, instrument, "
                    "account_id)"
                ),
                "isPrimary": True,
                "isUnique": True,
                "isValid": True,
                "tablespace": "pg_default",
                "relationKind": "i",
                "parentIndex": "leaderboard_entries_snapshot_pkey",
            },
            {
                "name": "les_snapshot_solo_guitar_score_idx",
                "definition": (
                    "CREATE INDEX "
                    "leaderboard_entries_snapshot_solo_guitar_score "
                    "ON public.leaderboard_entries_snapshot_solo_guitar "
                    "USING btree "
                    "(snapshot_id, song_id, instrument, score DESC)"
                ),
                "isPrimary": False,
                "isUnique": False,
                "isValid": True,
                "tablespace": "pg_default",
                "relationKind": "i",
                "parentIndex": "ix_les_snapshot_song_score",
            },
        ],
        "heapBytes": 1_000_000,
        "indexBytes": 500_000,
        "totalBytes": 1_500_000,
    }


def plan_fixture():
    return {
        "planId": "plan-1",
        "sourceCatalog": source_catalog(),
        "sourceCatalogNames": tool.source_catalog_names(
            source_catalog()
        ),
        "sourceState": source_state(),
        "planIdentity": {
            "sourceFence": tool.source_fence_from_state(source_state()),
            "sourceRelationIdentity": tool.physical_relation_identity(
                source_state()
            ),
            "protectedSnapshotIds": [1302, 1303],
            "exactRetainedRows": 20,
            "retainedFingerprint": {"rowCount": 20},
            "retainedDistribution": [],
            "referenceParity": {
                "publication": {
                    "publishedScrapeId": 1303,
                    "currentPublicationId": 89,
                    "previousPublicationId": 80,
                    "workingPublicationId": None,
                    "publicReadsFrozen": False,
                }
            },
        },
    }


class SnapshotGenerationMigrationTests(unittest.TestCase):
    def setUp(self):
        shutil.rmtree(WORK_ROOT, ignore_errors=True)
        WORK_ROOT.mkdir(parents=True, mode=0o700)

    def tearDown(self):
        package = (
            WORK_ROOT
            / tool.RECOVERED_DIR
            / "drop-recovery-package"
        )
        if package.exists():
            package.chmod(0o700)
        shutil.rmtree(WORK_ROOT, ignore_errors=True)

    def test_fixed_allowlist_is_exactly_nine_database_partitions(self):
        self.assertEqual(
            [
                (
                    "solo-guitar",
                    "leaderboard_entries_snapshot_solo_guitar",
                    "Solo_Guitar",
                ),
                (
                    "solo-bass",
                    "leaderboard_entries_snapshot_solo_bass",
                    "Solo_Bass",
                ),
                (
                    "solo-drums",
                    "leaderboard_entries_snapshot_solo_drums",
                    "Solo_Drums",
                ),
                (
                    "solo-vocals",
                    "leaderboard_entries_snapshot_solo_vocals",
                    "Solo_Vocals",
                ),
                (
                    "pro-guitar",
                    "leaderboard_entries_snapshot_pro_guitar",
                    "Solo_PeripheralGuitar",
                ),
                (
                    "pro-bass",
                    "leaderboard_entries_snapshot_pro_bass",
                    "Solo_PeripheralBass",
                ),
                (
                    "pro-vocals",
                    "leaderboard_entries_snapshot_pro_vocals",
                    "Solo_PeripheralVocals",
                ),
                (
                    "pro-cymbals",
                    "leaderboard_entries_snapshot_pro_cymbals",
                    "Solo_PeripheralCymbals",
                ),
                (
                    "pro-drums",
                    "leaderboard_entries_snapshot_pro_drums",
                    "Solo_PeripheralDrums",
                ),
            ],
            [
                (target.key, target.partition, target.instrument)
                for target in tool.TARGETS
            ],
        )
        self.assertEqual(9, len(tool.TARGET_BY_KEY))

    def test_parser_accepts_only_fixed_instrument_key(self):
        parser = tool.build_parser()
        option_destinations = {
            action.dest for action in parser._actions
        }

        self.assertNotIn("table", option_destinations)
        self.assertNotIn("relation", option_destinations)
        self.assertNotIn("sql", option_destinations)
        with contextlib.redirect_stderr(io.StringIO()):
            with self.assertRaises(SystemExit):
                parser.parse_args(
                    [
                        "plan",
                        "--instrument",
                        "public.anything",
                        "--scratch-root",
                        "/work",
                        "--expected-device-id",
                        "1:1",
                        "--run-id",
                        "synthetic-run",
                    ]
                )

    def test_names_are_deterministic_safe_and_bounded(self):
        for target in tool.TARGETS:
            values = (
                tool.replacement_name("synthetic-run-0001", target),
                tool.retired_name("synthetic-run-0001", target),
                tool.failed_name("synthetic-run-0001", target),
                tool.replacement_primary_name(
                    "synthetic-run-0001", target
                ),
                tool.replacement_score_name(
                    "synthetic-run-0001", target
                ),
                tool.replacement_instrument_check_name(
                    "synthetic-run-0001", target
                ),
                tool.original_instrument_check_name(
                    "synthetic-run-0001", target
                ),
                tool.generation_child_name(target, 1303),
                tool.default_child_name(target),
                tool.failed_child_name(
                    "synthetic-run-0001", target, 1303
                ),
            )
            for value in values:
                self.assertLessEqual(len(value), 63)
                self.assertRegex(value, r"^[a-z][a-z0-9_]+$")
            self.assertEqual(
                values[0],
                tool.replacement_name("synthetic-run-0001", target),
            )

    def test_protected_query_uses_only_live_reference_owners(self):
        sql = tool.protected_sources_query(
            tool.TARGET_BY_KEY["solo-guitar"]
        )

        self.assertIn("leaderboard_snapshot_state", sql)
        self.assertIn("solo_current_projection_scope", sql)
        self.assertIn("current_publication_id", sql)
        self.assertIn("previous_publication_id", sql)
        self.assertIn("working_publication_id", sql)
        self.assertIn("leaderboard_published_scope_source", sql)
        self.assertNotIn("rollback", sql.lower())
        self.assertNotIn("ORDER BY id DESC", sql)

    def test_reference_parity_requires_dual_empty_scope_evidence(self):
        sql = tool.reference_parity_query(
            tool.TARGET_BY_KEY["solo-guitar"],
            "leaderboard_entries_snapshot_solo_guitar",
        )

        self.assertIn("current_sources AS", sql)
        self.assertIn("source.scope_kind = 'alltime'", sql)
        self.assertIn("source.source_kind = 'empty'", sql)
        self.assertIn("source.source_snapshot_id IS NULL", sql)
        self.assertIn("source.row_count = 0", sql)
        self.assertIn("source.is_complete = TRUE", sql)
        self.assertIn("scope.source_kind = 'snapshot'", sql)
        self.assertIn("scope.row_count = 0", sql)
        self.assertIn("scope.status = 'ready'", sql)
        self.assertIn("'currentEmptySourceFingerprint'", sql)
        self.assertIn("'invalidCurrentEmptySourceRows'", sql)

    def test_reference_parity_fails_closed_for_nonzero_missing_counts(self):
        base = {
            "missingNamedSourceRows": 0,
            "activeStateMissingRows": 0,
            "projectionMissingRows": 0,
            "invalidCurrentEmptySourceRows": 0,
        }

        self.assertIsNone(tool.assert_reference_parity(base))
        for key in base:
            with self.subTest(key=key):
                with self.assertRaisesRegex(
                    tool.MigrationError,
                    key,
                ):
                    tool.assert_reference_parity({**base, key: 1})

    def test_transactional_reference_guard_compares_complete_json(self):
        sql = tool.transactional_reference_parity_guard_sql(
            tool.TARGET_BY_KEY["solo-guitar"],
            "leaderboard_entries_snapshot_solo_guitar",
            {
                "missingNamedSourceRows": 0,
                "activeStateMissingRows": 0,
                "projectionMissingRows": 0,
                "invalidCurrentEmptySourceRows": 0,
                "currentEmptySourceCount": 2,
                "currentEmptySourceFingerprint": "empty-fingerprint",
            },
            "test_reference_guard",
        )

        self.assertIn("observed_reference", sql)
        self.assertIn("currentEmptySourceFingerprint", sql)
        self.assertIn("reference parity changed before test_reference_guard", sql)
        self.assertIn("IS DISTINCT FROM", sql)

    def test_derive_protected_ids_returns_exact_physical_set(self):
        result = {
            "unresolvedPublicationCount": 0,
            "invalidNamedSnapshotSourceCount": 0,
            "protected": [
                {
                    "snapshotId": 1303,
                    "reasons": [
                        "active_snapshot_state",
                        "current_publication_physical_source",
                    ],
                    "referenceCount": 10,
                },
                {
                    "snapshotId": 1302,
                    "reasons": [
                        "previous_publication_physical_source"
                    ],
                    "referenceCount": 5,
                },
            ],
        }

        self.assertEqual(
            [1302, 1303],
            tool.derive_protected_ids(result, [1301, 1302, 1303]),
        )

    def test_derive_protected_ids_fails_closed(self):
        base = {
            "unresolvedPublicationCount": 0,
            "invalidNamedSnapshotSourceCount": 0,
            "protected": [
                {
                    "snapshotId": 1303,
                    "reasons": ["active_snapshot_state"],
                    "referenceCount": 1,
                }
            ],
        }
        cases = [
            (
                {**base, "unresolvedPublicationCount": 1},
                [1303],
                "does not resolve",
            ),
            (
                {**base, "invalidNamedSnapshotSourceCount": 1},
                [1303],
                "invalid physical",
            ),
            (
                {**base, "protected": []},
                [1303],
                "no protected",
            ),
            (base, [1302], "absent"),
        ]
        for value, inventory, message in cases:
            with self.subTest(message=message):
                with self.assertRaisesRegex(
                    tool.MigrationError,
                    message,
                ):
                    tool.derive_protected_ids(value, inventory)

    def test_inventory_query_is_bounded_loose_index_walk(self):
        sql = tool.inventory_query(
            tool.TARGET_BY_KEY["solo-guitar"]
        )

        self.assertIn("WITH RECURSIVE snapshot_ids", sql)
        self.assertIn("MIN(next_snapshot.snapshot_id)", sql)
        self.assertNotIn("GROUP BY snapshot_id", sql)
        self.assertNotIn("hashtextextended", sql)
        self.assertIn("temp_file_limit=262144kB", tool.PLAN_QUERY_PGOPTIONS)
        self.assertIn("enable_seqscan=off", tool.LOOSE_ID_PGOPTIONS)

    def test_fingerprint_covers_every_snapshot_column(self):
        expression = tool.snapshot_row_expression()

        for column in tool.SNAPSHOT_COLUMNS:
            self.assertIn(f'"{column}"', expression)
        self.assertEqual(23, len(tool.SNAPSHOT_COLUMNS))

    def test_physical_identity_excludes_resettable_statistics_counters(self):
        before = source_state()
        after = {
            **before,
            "inserts": 0,
            "updates": 0,
            "deletes": 0,
        }

        self.assertEqual(
            tool.physical_relation_identity(before),
            tool.physical_relation_identity(after),
        )
        self.assertNotEqual(
            tool.relation_mutation_counters(before),
            tool.relation_mutation_counters(after),
        )
        self.assertEqual(10, tool.source_fence_from_state(before)["inserts"])
        self.assertIsNone(tool.relation_statistics_epoch(before))
        after["statisticsResetAt"] = "2026-08-18T00:00:00Z"
        self.assertNotEqual(
            tool.relation_statistics_epoch(before),
            tool.relation_statistics_epoch(after),
        )

    def test_archive_command_has_only_parent_and_selected_partition(self):
        args = types.SimpleNamespace(
            pg_container="fst-snapshot-generation-test-source",
            pg_user="fst",
            pg_database="fstservice",
        )
        target = tool.TARGET_BY_KEY["solo-guitar"]

        command = tool.pg_dump_command(args, target)

        self.assertEqual(2, command.count("--table"))
        self.assertIn(
            "public.leaderboard_entries_snapshot",
            command,
        )
        self.assertIn(
            "public.leaderboard_entries_snapshot_solo_guitar",
            command,
        )
        self.assertNotIn(
            "public.leaderboard_entries_snapshot_solo_bass",
            command,
        )
        self.assertNotIn("--clean", command)

    def test_final_drop_reproof_and_ddl_share_advisory_transaction(self):
        args = types.SimpleNamespace(
            run_id="synthetic-run-0001",
            query_timeout_seconds=600,
        )
        target = tool.TARGET_BY_KEY["solo-guitar"]
        plan = plan_fixture()
        restore = {
            "restoredFingerprint": {"rowCount": 1000},
            "restoredDistribution": [],
        }
        primary = tool.replacement_primary_name(args.run_id, target)
        score = tool.replacement_score_name(args.run_id, target)
        validation = {
            "candidateShape": {
                "indexes": [
                    {
                        "name": primary,
                        "definition":
                            f"CREATE UNIQUE INDEX {primary} ON ONLY public.x",
                    },
                    {
                        "name": score,
                        "definition":
                            f"CREATE INDEX {score} ON ONLY public.x",
                    },
                ]
            },
            "candidateCatalog": {
                "constraints": [
                    {"name": primary},
                    {
                        "name": tool.replacement_instrument_check_name(
                            args.run_id,
                            target,
                        )
                    },
                ],
                "indexes": [
                    {
                        "name": primary,
                        "definition":
                            f"CREATE UNIQUE INDEX {primary} ON ONLY public.x",
                    },
                    {
                        "name": score,
                        "definition":
                            f"CREATE INDEX {score} ON ONLY public.x",
                    },
                ],
            },
        }
        sql = tool.final_drop_sql(
            args,
            target,
            plan,
            restore,
            validation,
            tool.retired_name(args.run_id, target),
            plan["sourceCatalogNames"],
        )

        share_lock = sql.index("IN SHARE MODE")
        fingerprint = sql.index("hashtextextended")
        advisory = sql.index("pg_try_advisory_xact_lock")
        reference = sql.index(
            "reference parity changed before final_drop_reference_guard"
        )
        protected = sql.index("protected IDs changed")
        drop = sql.index("DROP TABLE")

        self.assertLess(share_lock, fingerprint)
        self.assertLess(fingerprint, advisory)
        self.assertLess(advisory, reference)
        self.assertLess(reference, protected)
        self.assertLess(advisory, protected)
        self.assertLess(protected, drop)
        self.assertIn("public_reads_frozen = FALSE", sql)

        reproof_sql, ddl_sql = tool.final_drop_transaction_sql(
            args,
            target,
            plan,
            restore,
            validation,
            tool.retired_name(args.run_id, target),
            plan["sourceCatalogNames"],
        )
        self.assertIn("BEGIN;", reproof_sql)
        self.assertNotIn("DROP TABLE", reproof_sql)
        self.assertIn("DROP TABLE", ddl_sql)
        self.assertNotIn("COMMIT;", ddl_sql)

    def test_archive_toc_rejects_other_instrument_data(self):
        target = tool.TARGET_BY_KEY["solo-guitar"]
        names = tool.source_catalog_names(source_catalog())
        valid = "\n".join(
            [
                "TABLE public leaderboard_entries_snapshot",
                "TABLE public leaderboard_entries_snapshot_solo_guitar",
                (
                    "TABLE DATA public "
                    "leaderboard_entries_snapshot_solo_guitar"
                ),
                names["primaryIndex"],
                names["scoreIndex"],
            ]
        )

        self.assertTrue(
            tool.verify_archive_toc(valid, target, names)
        )
        with self.assertRaisesRegex(
            tool.MigrationError,
            "another instrument",
        ):
            tool.verify_archive_toc(
                valid
                + "\nTABLE DATA public "
                "leaderboard_entries_snapshot_solo_bass",
                target,
                names,
            )

    def test_archive_manifest_binds_checksum_toc_and_source_fence(self):
        target = tool.TARGET_BY_KEY["solo-guitar"]
        archive_dir = WORK_ROOT / tool.ARCHIVE_DIR
        archive_dir.mkdir()
        archive = archive_dir / "solo-guitar-original.custom"
        archive.write_bytes(b"archive")
        toc = archive_dir / "solo-guitar-original.list"
        toc.write_text("toc", encoding="utf-8")
        fence = tool.source_fence_from_state(source_state())
        manifest = {
            "formatVersion": tool.FORMAT_VERSION,
            "toolId": tool.TOOL_ID,
            "targetKey": target.key,
            "target": f"public.{target.partition}",
            "instrument": target.instrument,
            "archive": {
                "path": str(archive),
                "bytes": archive.stat().st_size,
                "sha256": tool.sha256_path(archive),
            },
            "toc": {
                "path": str(toc),
                "sha256": tool.sha256_path(toc),
            },
            "source": {"before": fence, "after": dict(fence)},
            "sourceChangedDuringArchive": False,
        }
        tool.write_json_exclusive(
            tool.archive_manifest_path(WORK_ROOT, target),
            manifest,
        )

        self.assertEqual(
            manifest,
            tool.load_archive_manifest(WORK_ROOT, target),
        )
        archive.write_bytes(b"changed")
        with self.assertRaisesRegex(
            tool.MigrationError,
            "changed",
        ):
            tool.load_archive_manifest(WORK_ROOT, target)

    def test_archive_pin_detects_path_replacement_and_preserves_inode(self):
        archive_dir = WORK_ROOT / tool.ARCHIVE_DIR
        archive_dir.mkdir()
        archive = archive_dir / "solo-guitar-original.custom"
        archive.write_bytes(b"verified archive")
        manifest = archive_dir / "solo-guitar-manifest.json"
        manifest.write_bytes(b"verified manifest")
        chain = {
            "archivePath": str(archive),
            "archiveSha256": tool.sha256_path(archive),
            "manifestPath": str(manifest),
            "manifestSha256": tool.sha256_path(manifest),
        }
        recovery = archive.with_name(
            "." + archive.name + ".drop-recovery"
        )
        stale_partial = recovery.with_name(
            "." + recovery.name + ".partial-stale"
        )
        stale_partial.write_bytes(b"stale")
        pins = tool.pin_archive_evidence(chain)
        archive_recovery = recovery
        try:
            self.assertFalse(stale_partial.exists())
            self.assertFalse(
                os.path.samefile(archive, archive_recovery)
            )
            self.assertEqual(
                0,
                archive_recovery.stat().st_mode & 0o222,
            )
            archive.unlink()
            archive.write_bytes(b"replacement")
            with self.assertRaisesRegex(
                tool.MigrationError,
                "pinned evidence changed",
            ):
                pins[0].verify(checksum=False)
            self.assertEqual(
                b"verified archive",
                archive_recovery.read_bytes(),
            )
        finally:
            for pin in pins:
                pin.close()

    def test_capacity_gates_preserve_fst_floor_and_bound_scratch(self):
        archive = tool.calculate_archive_capacity(
            100 * 1024**3,
            500 * 1024**3,
        )
        build = tool.calculate_build_capacity(
            100 * 1024**3,
            1_000_000,
            20_000,
            100 * 1024**3,
        )

        self.assertTrue(archive["allowed"])
        self.assertGreater(
            archive["requiredFreeBytes"],
            2 * 100 * 1024**3,
        )
        self.assertGreaterEqual(
            build["requiredFreeBytes"],
            tool.EMERGENCY_FLOOR_BYTES,
        )
        self.assertEqual(
            (
                tool.EMERGENCY_FLOOR_BYTES
                + build["estimatedReplacementBytes"]
                + build["estimatedWalBytes"]
                + build["estimatedTempBytes"]
                + build["failureReserveBytes"]
            ),
            build["requiredFreeBytes"],
        )

    def test_capacity_rejects_impossible_retained_count(self):
        with self.assertRaisesRegex(
            tool.MigrationError,
            "exceed",
        ):
            tool.calculate_build_capacity(1000, 10, 11, 100000)

    def test_build_sql_creates_exact_children_default_and_indexes(self):
        target = tool.TARGET_BY_KEY["solo-guitar"]
        args = types.SimpleNamespace(
            run_id="synthetic-run-0001"
        )
        plan = plan_fixture()

        sql = tool.build_sql(args, target, plan)

        self.assertIn("PARTITION BY LIST (snapshot_id)", sql)
        self.assertIn("FOR VALUES IN (1302)", sql)
        self.assertIn("FOR VALUES IN (1303)", sql)
        self.assertIn("DEFAULT TABLESPACE pg_default", sql)
        self.assertIn("PRIMARY KEY", sql)
        self.assertIn(
            "(snapshot_id, song_id, instrument, score DESC)",
            sql,
        )
        self.assertIn("TABLESPACE pg_default", sql)
        self.assertNotIn("TABLESPACE fst_", sql)

    def test_partition_shape_requires_exact_children_and_parent_indexes(self):
        target = tool.TARGET_BY_KEY["solo-guitar"]
        shape = {
            "relationKind": "p",
            "partitionKey": "LIST (snapshot_id)",
            "partitionBound": target.bound,
            "rootTablespace": "pg_default",
            "children": [
                {
                    "name": tool.generation_child_name(target, 1302),
                    "relationKind": "r",
                    "partitionBound": "FOR VALUES IN (1302)",
                    "tablespace": "pg_default",
                },
                {
                    "name": tool.generation_child_name(target, 1303),
                    "relationKind": "r",
                    "partitionBound": "FOR VALUES IN ('1303')",
                    "tablespace": "pg_default",
                },
                {
                    "name": tool.default_child_name(target),
                    "relationKind": "r",
                    "partitionBound": "DEFAULT",
                    "tablespace": "pg_default",
                },
            ],
            "defaultRows": 0,
            "indexes": [
                {
                    "name": "candidate_pk",
                    "relationKind": "I",
                    "isPrimary": True,
                    "isUnique": True,
                    "isValid": True,
                    "tablespace": "pg_default",
                    "definition": (
                        "CREATE UNIQUE INDEX candidate_pk ON ONLY "
                        "public.candidate USING btree "
                        "(snapshot_id, song_id, instrument, account_id)"
                    ),
                    "parentIndex": "leaderboard_entries_snapshot_pkey",
                },
                {
                    "name": "candidate_score",
                    "relationKind": "I",
                    "isPrimary": False,
                    "isUnique": False,
                    "isValid": True,
                    "tablespace": "pg_default",
                    "definition": (
                        "CREATE INDEX candidate_score ON ONLY "
                        "public.candidate USING btree "
                        "(snapshot_id, song_id, instrument, score DESC)"
                    ),
                    "parentIndex": "ix_les_snapshot_song_score",
                },
            ],
            "nonDefaultTablespaces": 0,
        }

        self.assertTrue(
            tool.validate_partition_shape(
                shape,
                target,
                [1302, 1303],
            )
        )
        shape["defaultRows"] = 1
        with self.assertRaisesRegex(
            tool.MigrationError,
            "DEFAULT",
        ):
            tool.validate_partition_shape(
                shape,
                target,
                [1302, 1303],
            )

    def test_stage_dependencies_keep_restore_before_mutation_and_drop(self):
        self.assertEqual(
            ("plan", "restore"),
            tool.DEPENDENCIES["build"],
        )
        self.assertEqual(
            ("build", "archive", "restore"),
            tool.DEPENDENCIES["swap"],
        )
        self.assertEqual(
            ("validate", "archive", "restore"),
            tool.DEPENDENCIES["drop"],
        )
        self.assertEqual(
            ("archive", "restore"),
            tool.DEPENDENCIES["rollback"],
        )

    def test_physical_identity_ignores_statistics_counter_drift(self):
        before = source_state()
        after = {
            **before,
            "inserts": before["inserts"] + 10,
            "updates": before["updates"] + 3,
            "deletes": before["deletes"] + 1,
        }

        self.assertEqual(
            tool.physical_relation_identity(before),
            tool.physical_relation_identity(after),
        )
        self.assertNotEqual(
            tool.relation_mutation_counters(before),
            tool.relation_mutation_counters(after),
        )

    def test_success_reports_are_integrity_bound_and_immutable(self):
        (WORK_ROOT / tool.REPORTS_DIR).mkdir()
        report = tool.write_stage_report(
            WORK_ROOT,
            "check",
            {"runId": "synthetic-run", "value": 1},
        )

        self.assertEqual(
            report["integritySha256"],
            tool.report_integrity(report),
        )
        repeated = tool.write_stage_report(
            WORK_ROOT,
            "check",
            {"runId": "synthetic-run", "value": 2},
        )
        self.assertEqual(1, repeated["value"])
        path = tool.report_path(WORK_ROOT, "check")
        tampered = json.loads(path.read_text(encoding="utf-8"))
        tampered["value"] = 9
        path.write_text(json.dumps(tampered), encoding="utf-8")
        with self.assertRaisesRegex(
            tool.MigrationError,
            "integrity",
        ):
            tool.load_report(WORK_ROOT, "check")

    def test_swap_commit_evidence_is_integrity_bound_and_immutable(self):
        (WORK_ROOT / tool.REPORTS_DIR).mkdir()
        path = tool.swap_commit_path(WORK_ROOT)
        value = {
            "toolId": tool.TOOL_ID,
            "status": "committed",
            "runId": "synthetic-run-0001",
            "planId": "plan-1",
            "targetKey": "solo-guitar",
            "elapsedSeconds": 0.25,
            "durationKnown": True,
            "withinBound": True,
        }

        written = tool.write_integrity_evidence(path, value)
        loaded = tool.load_integrity_evidence(
            path,
            "swap committed",
        )

        self.assertEqual(written, loaded)
        self.assertEqual(
            written["integritySha256"],
            tool.report_integrity(written),
        )
        with self.assertRaisesRegex(
            tool.MigrationError,
            "immutable evidence differs",
        ):
            tool.write_integrity_evidence(
                path,
                {**value, "elapsedSeconds": 1.0},
            )

    def test_api_monitor_stop_timeout_is_a_failure(self):
        monitor = tool.PublicApiMonitor(
            "http://127.0.0.1:1",
            [],
        )
        thread = mock.Mock()
        thread.is_alive.return_value = True
        monitor._thread = thread

        self.assertFalse(monitor.stop(timeout_seconds=0))
        self.assertEqual(
            "api_monitor_thread_did_not_stop",
            monitor.failures[-1]["reason"],
        )

    def test_torn_report_is_preserved_then_reconciled(self):
        (WORK_ROOT / tool.REPORTS_DIR).mkdir()
        (WORK_ROOT / tool.RECOVERED_DIR).mkdir()
        path = tool.report_path(WORK_ROOT, "swap")
        path.write_bytes(b"")

        self.assertIsNone(
            tool.recover_torn_report(WORK_ROOT, "swap")
        )
        self.assertFalse(path.exists())
        recovered = list(
            (WORK_ROOT / tool.RECOVERED_DIR).glob(
                "swap.torn-*.json"
            )
        )
        evidence = list(
            (WORK_ROOT / tool.RECOVERED_DIR).glob(
                "swap.recovery-*.json"
            )
        )
        self.assertEqual(1, len(recovered))
        self.assertEqual(1, len(evidence))

    def test_valid_but_tampered_report_is_not_treated_as_torn(self):
        (WORK_ROOT / tool.REPORTS_DIR).mkdir()
        (WORK_ROOT / tool.RECOVERED_DIR).mkdir()
        path = tool.report_path(WORK_ROOT, "swap")
        path.write_text(
            json.dumps(
                {
                    "toolId": tool.TOOL_ID,
                    "stage": "swap",
                    "status": "succeeded",
                    "integritySha256": "wrong",
                }
            ),
            encoding="utf-8",
        )

        with self.assertRaisesRegex(
            tool.MigrationError,
            "integrity",
        ):
            tool.recover_torn_report(WORK_ROOT, "swap")
        self.assertTrue(path.exists())

    def test_scratch_guard_requires_matching_local_device(self):
        root = WORK_ROOT / "scratch"
        root.mkdir()

        value = tool.validate_scratch_root(
            MountRunner(),
            root,
            "0:123",
            test_mode=True,
            allow_unclaimed=True,
        )
        self.assertEqual("0:123", value["device"]["deviceId"])
        with self.assertRaisesRegex(
            tool.MigrationError,
            "mismatch",
        ):
            tool.validate_scratch_root(
                MountRunner(),
                root,
                "0:999",
                test_mode=True,
                allow_unclaimed=True,
            )

    def test_workspace_marker_binds_target_commit_source_and_archive_policy(
        self,
    ):
        root = WORK_ROOT / "workspace"
        root.mkdir()
        target = tool.TARGET_BY_KEY["solo-guitar"]
        scratch = {
            "requestedPath": str(root),
            "resolvedPath": str(root),
            "device": {"deviceId": "1:1"},
            "totalBytes": 1,
            "usedBytes": 0,
            "freeBytes": 1,
        }

        marker = tool.claim_workspace(
            root,
            scratch,
            "synthetic-run-0001",
            target,
            "2099-01-01T00:00:00Z",
            "commit",
            "source",
            True,
        )

        self.assertEqual(target.key, marker["targetKey"])
        self.assertEqual(9, marker["acceptedTargetCount"])
        self.assertTrue(
            marker["archiveDeletionRequiresSeparateOperatorDecision"]
        )
        self.assertFalse(marker["acceptedDatabaseDataMayRemainHere"])

    def test_api_parity_allows_dynamic_health_only(self):
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
            {**baseline[0], "sha256": "dynamic"},
            dict(baseline[1]),
        ]

        self.assertTrue(
            tool.compare_api_snapshots(baseline, observed)
        )
        observed[1]["sha256"] = "changed"
        self.assertFalse(
            tool.compare_api_snapshots(baseline, observed)
        )

    def test_source_has_no_cascade_arbitrary_sql_or_unsafe_drop(self):
        source = SCRIPT.read_text(encoding="utf-8")

        self.assertNotIn("CASCADE", source)
        self.assertNotIn("DROP TABLE IF EXISTS", source)
        self.assertNotIn('add_argument("--sql"', source)
        self.assertNotIn('add_argument("--relation"', source)
        self.assertNotIn('add_argument("--table"', source)
        self.assertIn(
            "DROP TABLE {qualified(retired)}",
            source,
        )


if __name__ == "__main__":
    unittest.main()
