#!/usr/bin/env python3

import copy
import contextlib
import base64
import gzip
import importlib.util
import hashlib
import io
import json
import pathlib
import types
import unittest
from unittest import mock


SCRIPT = pathlib.Path(__file__).with_name(
    "postgres-snapshot-generation-restore.py"
)
SPEC = importlib.util.spec_from_file_location(
    "snapshot_generation_restore",
    SCRIPT,
)
assert SPEC is not None and SPEC.loader is not None
tool = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(tool)


class SnapshotGenerationRestoreTests(unittest.TestCase):
    def decode_sha_pinned_fixture(self, root, item):
        packed = base64.b64decode(
            "".join(
                (root / item["file"])
                .read_text(encoding="utf-8")
                .split()
            ),
            validate=True,
        )
        self.assertEqual(
            item["gzipSha256"],
            hashlib.sha256(packed).hexdigest(),
        )
        raw = gzip.decompress(packed)
        self.assertEqual(item["rawBytes"], len(raw))
        self.assertEqual(
            item["rawSha256"],
            hashlib.sha256(raw).hexdigest(),
        )
        return raw

    def load_live_q2_archive_fixture(self):
        root = (
            SCRIPT.parent
            / "testdata"
            / "snapshot-generation-live-q2-archive"
        )
        fixture = json.loads(
            (root / "fixture-manifest.json")
            .read_text(encoding="utf-8")
        )
        safety = fixture["safetyReview"]
        for key in (
            "credentialLikeKeys",
            "connectionStrings",
            "emailAddresses",
            "privateEndpoints",
            "accountIdKeys",
        ):
            self.assertEqual(0, safety[key])
        catalog = json.loads(
            self.decode_sha_pinned_fixture(
                root,
                fixture["catalog"],
            )
        )
        toc = self.decode_sha_pinned_fixture(
            root,
            fixture["toc"],
        ).decode("utf-8")
        return fixture, catalog, toc

    @staticmethod
    def canonical_drop_plan_bytes(
        *,
        ordered_names=None,
    ):
        members = {
            "activePlan": (
                b'{"archive":{"archiveSha256":"'
                + b"a" * 64
                + b'","childOid":300,'
                b'"childRelfilenode":300,'
                b'"instrument":"Solo_Guitar",'
                b'"packageManifestSha256":"'
                + b"b" * 64
                + b'","rowCount":1,'
                b'"rowFingerprintSha256":"'
                + b"j" * 64
                + b'","snapshotId":1314,'
                b'"totalBytes":4096}}'
            ),
            "activeSemantic": (
                b'{"logicalIndexShapeSha256":"'
                + b"i" * 64
                + b'","semanticCatalogSha256":"'
                + b"h" * 64
                + b'"}'
            ),
            "capacityReserveBytes": b"0",
            "explicitApprovalRequired": b"true",
            "freshProofManifestSha256": (
                b'"' + b"g" * 64 + b'"'
            ),
            "nested": (
                b'{"special":"\\u002B\\u0027",'
                b'"timestamp":"2026-08-31T14:00:00+00:00"}'
            ),
            "recoveryBundleManifestSha256": (
                b'"' + b"c" * 64 + b'"'
            ),
            "recoveryBundlePath": b'"/sealed/bundle"',
            "restoreImageIdSha256": (
                b'"' + b"d" * 64 + b'"'
            ),
            "restoreToolSha256": (
                b'"' + b"e" * 64 + b'"'
            ),
            "schemaVersion": b"1",
            "toolId": (
                b'"fst.snapshot-generation-drop-only.v1"'
            ),
        }
        unsigned_names = (
            list(ordered_names)
            if ordered_names is not None
            else sorted(members)
        )

        def encode(selected, values):
            return (
                b"{"
                + b",".join(
                    json.dumps(name).encode("ascii")
                    + b":"
                    + values[name]
                    for name in selected
                )
                + b"}"
            )

        unsigned = encode(unsigned_names, members)
        digest = hashlib.sha256(unsigned).hexdigest()
        operation_bytes = (
            b'{"planDigest":"'
            + digest.encode("ascii")
            + b'","toolId":"fst.snapshot-generation-drop-only.v1"}'
        )
        operation = hashlib.sha256(
            operation_bytes
        ).hexdigest()[:32]
        full_members = {
            **members,
            "dropOperationId":
                json.dumps(operation).encode("ascii"),
            "planDigest":
                json.dumps(digest).encode("ascii"),
        }
        if ordered_names is None:
            full_names = sorted(full_members)
        else:
            full_names = list(ordered_names)
            full_names.insert(1, "dropOperationId")
            full_names.insert(4, "planDigest")
        return (
            encode(full_names, full_members) + b"\n",
            digest,
            operation,
        )

    def validate_drop_plan_bytes(
        self,
        value,
        expected_digest=None,
        expected_operation=None,
    ):
        with mock.patch.object(
            pathlib.Path,
            "read_bytes",
            return_value=value,
        ):
            return tool.validate_drop_plan(
                pathlib.Path("unused"),
                expected_digest,
                expected_operation,
            )

    @staticmethod
    def canonical_drop_report_bytes(report):
        unsigned = dict(report)
        unsigned.pop("reportSha256", None)
        report_hash = hashlib.sha256(
            tool.ARCHIVE.dotnet_canonical_json_bytes(
                unsigned)
        ).hexdigest()
        sealed = {
            **unsigned,
            "reportSha256": report_hash,
        }
        return (
            tool.ARCHIVE.dotnet_canonical_json_bytes(
                sealed)
            + b"\n"
        )

    def test_drop_plan_uses_original_canonical_bytes(self):
        value, digest, operation = (
            self.canonical_drop_plan_bytes()
        )

        plan = self.validate_drop_plan_bytes(
            value,
            digest,
            operation,
        )

        self.assertEqual(digest, plan["planDigest"])
        self.assertEqual(
            operation,
            plan["dropOperationId"],
        )

    def test_committed_live_drop_fixture_uses_authoritative_bytes(self):
        root = SCRIPT.parent / "testdata" / (
            "snapshot-generation-live-drop")
        fixture = json.loads(
            (root / "fixture-manifest.json")
            .read_text(encoding="utf-8")
        )
        safety = fixture["safetyReview"]
        self.assertEqual(0, safety["credentialLikeKeys"])
        self.assertEqual(0, safety["connectionStrings"])
        self.assertEqual(0, safety["emailAddresses"])
        self.assertEqual(0, safety["privateEndpoints"])
        self.assertEqual(0, safety["accountIdKeys"])

        plan_bytes = self.decode_sha_pinned_fixture(
            root,
            fixture["plan"],
        )
        report_bytes = self.decode_sha_pinned_fixture(
            root,
            fixture["report"],
        )
        expected_digest = fixture["plan"]["planDigest"]
        expected_operation = fixture[
            "plan"]["dropOperationId"]
        with mock.patch.object(
            pathlib.Path,
            "read_bytes",
            side_effect=[
                plan_bytes,
                report_bytes,
            ],
        ):
            plan = tool.validate_drop_plan(
                pathlib.Path("drop-plan-v2.json"),
                expected_digest,
                expected_operation,
            )
            report = tool.validate_drop_report(
                pathlib.Path("drop-report-v2.json"),
                plan,
            )

        parsed = json.loads(plan_bytes)
        parsed.pop("planDigest")
        parsed.pop("dropOperationId")
        old_digest = hashlib.sha256(
            tool.ARCHIVE.dotnet_canonical_json_bytes(
                parsed)
        ).hexdigest()
        self.assertEqual(
            "2536d932d3c0009eb748354f08d221f6a87f9dc49b5529cdb8a932800baaad5a",
            old_digest,
        )
        self.assertNotEqual(expected_digest, old_digest)
        self.assertEqual(
            expected_operation,
            plan["dropOperationId"],
        )
        self.assertEqual(
            "committed",
            report["commitOutcome"],
        )

    def test_drop_plan_canonical_file_tampering_rejects(self):
        value, digest, operation = (
            self.canonical_drop_plan_bytes()
        )
        missing_digest = value.replace(
            (
                b',"planDigest":"'
                + digest.encode("ascii")
                + b'",'
            ),
            b",",
            1,
        )
        cases = {
            "nested edit": value.replace(
                b"Solo_Guitar",
                b"Solo_Drums",
            ),
            "duplicate identity": value.replace(
                b"{",
                (
                    b'{"dropOperationId":"'
                    + operation.encode("ascii")
                    + b'",'
                ),
                1,
            ),
            "whitespace": value[:1] + b" " + value[1:],
            "identity edit": value.replace(
                digest.encode("ascii"),
                (("0" if digest[0] != "0" else "1")
                 + digest[1:]).encode("ascii"),
                1,
            ),
            "missing identity": missing_digest,
            "invalid identity": value.replace(
                operation.encode("ascii"),
                b"z" * 32,
                1,
            ),
            "trailing data": value + b"{}",
            "non-object": b"[]",
            "malformed": b'{"schemaVersion":}',
        }
        for label, candidate in cases.items():
            with self.subTest(label=label):
                with self.assertRaises(tool.RestoreError):
                    self.validate_drop_plan_bytes(
                        candidate,
                        digest,
                        operation,
                    )

        reordered, reordered_digest, reordered_operation = (
            self.canonical_drop_plan_bytes(
                ordered_names=[
                    "toolId",
                    "activePlan",
                    "explicitApprovalRequired",
                    "nested",
                    "schemaVersion",
                ]
            )
        )
        with self.assertRaises(tool.RestoreError):
            self.validate_drop_plan_bytes(
                reordered,
                reordered_digest,
                reordered_operation,
            )

    def test_builds_restore_plan_from_committed_canonical_fixture(self):
        value, digest, operation = (
            self.canonical_drop_plan_bytes()
        )
        report_bytes = (
            self.canonical_drop_report_bytes(
                {
                    "schemaVersion": 1,
                    "toolId":
                        "fst.snapshot-generation-drop-only.v1",
                    "action": "drop",
                    "dropOperationId": operation,
                    "planDigest": digest,
                    "status": "dropped",
                    "commitOutcome": "committed",
                    "completedAtUtc":
                        "2026-08-31T14:00:00+00:00",
                    "actor": "operator+approved",
                    "reference": "approval's-reference",
                    "instrument": "Solo_Guitar",
                    "snapshotId": 1314,
                    "childOid": 300,
                    "childRelfilenode": 300,
                    "rowCount": 1,
                    "rowFingerprintSha256": "j" * 64,
                    "evidence": {"committed": True},
                }
            )
        )
        args = types.SimpleNamespace(
            drop_plan="drop-plan.json",
            drop_report="drop-report.json",
            output="/output/restore-plan.json",
            restore_list="/output/restore.list",
            expected_drop_plan_digest=digest,
            expected_drop_operation_id=operation,
            source_container="source",
            pg_user="fst",
            pg_database="fst",
            postgres_image="postgres:17",
        )
        child = {
            "totalBytes": 4096,
            "indexes": [
                {
                    "indexName": "archived_pk",
                    "isPrimary": True,
                },
                {
                    "indexName": "archived_score",
                    "isPrimary": False,
                },
            ],
        }
        selected = [
            "1; TABLE",
            "2; TABLE DATA",
            "3; CONSTRAINT",
            "4; INDEX",
        ]
        bundle = SCRIPT.parent

        def fake_sha256(path):
            path = pathlib.Path(path)
            if "archive" in path.name:
                return "f" * 64
            if (
                path == SCRIPT
                or path.name == "restore-tool.py"
            ):
                return "e" * 64
            return "9" * 64

        with (
            mock.patch.object(
                pathlib.Path,
                "read_bytes",
                side_effect=[
                    value,
                    report_bytes,
                ],
            ),
            mock.patch.object(
                pathlib.Path,
                "read_text",
                return_value="toc",
            ),
            mock.patch.object(
                tool,
                "validate_path",
                side_effect=lambda path, **_: pathlib.Path(path),
            ),
            mock.patch.object(
                tool,
                "sha256_path",
                side_effect=fake_sha256,
            ),
            mock.patch.object(
                tool,
                "validate_bundle",
                return_value=(bundle, {}),
            ),
            mock.patch.object(
                tool,
                "load_pinned_archive",
                return_value=(
                    {
                        "archive": {
                            "sha256": "a" * 64,
                            "bytes": 100,
                        },
                    },
                    {},
                    {"manifest.json": "b" * 64},
                ),
            ),
            mock.patch.object(
                tool,
                "validate_fresh_proof",
                return_value=(
                    pathlib.Path("/proof/manifest.json"),
                    {
                        "completedAtUtc":
                            "2026-08-31T14:00:00Z",
                    },
                ),
            ),
            mock.patch.object(
                tool,
                "child_catalog",
                return_value=child,
            ),
            mock.patch.object(
                tool,
                "select_restore_toc",
                return_value=selected,
            ),
            mock.patch.object(
                tool.ARCHIVE,
                "container_identity",
                return_value={
                    "pgdataMount": {
                        "source": str(bundle),
                    },
                },
            ),
            mock.patch.object(
                tool.shutil,
                "disk_usage",
                return_value=types.SimpleNamespace(
                    free=10 * 1024**3),
            ),
            mock.patch.object(
                tool,
                "inspect_restore_image",
                return_value=(
                    "sha256:" + "d" * 64,
                    "pg_restore (PostgreSQL) 17",
                ),
            ),
            mock.patch.object(
                tool,
                "database_drop_state",
                return_value={
                    "dropExists": True,
                    "restoreExists": False,
                    "originalExists": False,
                    "oldOidExists": False,
                    "holdActive": True,
                },
            ),
            mock.patch.object(
                tool,
                "require_database_identity",
                return_value={"database": "fst"},
            ),
            mock.patch.object(
                tool,
                "repository_identity",
                return_value={
                    "gitCommit": "1" * 40,
                    "toolPath":
                        "tools/postgres-snapshot-generation-restore.py",
                    "toolSha256": "e" * 64,
                },
            ),
            mock.patch.object(
                tool,
                "write_new_bytes",
            ) as write_bytes,
            mock.patch.object(
                tool,
                "write_new_json",
            ) as write_json,
        ):
            plan = tool.build_plan(args)

        self.assertEqual(operation, plan["dropOperationId"])
        self.assertEqual(digest, plan["dropPlanDigest"])
        self.assertEqual(
            selected[:2],
            plan["executedTocEntries"],
        )
        self.assertEqual(
            "pinned",
            plan["restoreToolAuthorization"]["mode"],
        )
        write_bytes.assert_called_once()
        write_json.assert_called_once()

    def test_builds_authorized_plan_from_live_q2_catalog_and_toc(self):
        drop_root = (
            SCRIPT.parent
            / "testdata"
            / "snapshot-generation-live-drop"
        )
        drop_fixture = json.loads(
            (drop_root / "fixture-manifest.json")
            .read_text(encoding="utf-8")
        )
        plan_bytes = self.decode_sha_pinned_fixture(
            drop_root,
            drop_fixture["plan"],
        )
        report_bytes = self.decode_sha_pinned_fixture(
            drop_root,
            drop_fixture["report"],
        )
        drop_plan = json.loads(plan_bytes)
        archive_fixture, catalog, toc = (
            self.load_live_q2_archive_fixture()
        )
        expected = archive_fixture["expected"]
        args = types.SimpleNamespace(
            drop_plan="drop-plan-v2.json",
            drop_report="drop-report-v2.json",
            output="/output/restore-plan.json",
            restore_list="/output/restore.list",
            expected_drop_plan_digest=
                drop_fixture["plan"]["planDigest"],
            expected_drop_operation_id=
                drop_fixture["plan"]["dropOperationId"],
            source_container="source",
            pg_user="fst",
            pg_database="fstservice",
            postgres_image="postgres:17",
            authorization_id="a" * 32,
            repair_package="/repair-package",
            expected_repair_package_manifest_sha256=
                "b" * 64,
        )
        bundle = SCRIPT.parent
        authorization = {
            "mode": "authorized",
            "authorizationId": args.authorization_id,
            "pinnedToolSha256":
                drop_plan["restoreToolSha256"],
            "executingToolSha256": "c" * 64,
        }
        archive_manifest = {
            "archive": {
                "sha256": drop_plan[
                    "activePlan"]["archive"]["archiveSha256"],
                "bytes": 100,
            },
            "target": {
                "childRelation":
                    expected["childRelation"],
            },
        }
        checksums = {
            "manifest.json": drop_plan[
                "activePlan"]["archive"][
                    "packageManifestSha256"],
        }

        with (
            mock.patch.object(
                pathlib.Path,
                "read_bytes",
                side_effect=[
                    plan_bytes,
                    report_bytes,
                ],
            ),
            mock.patch.object(
                pathlib.Path,
                "read_text",
                return_value=toc,
            ),
            mock.patch.object(
                tool,
                "validate_path",
                side_effect=lambda path, **_: pathlib.Path(path),
            ),
            mock.patch.object(
                tool,
                "sha256_path",
                return_value="d" * 64,
            ),
            mock.patch.object(
                tool,
                "validate_bundle",
                return_value=(bundle, {}),
            ),
            mock.patch.object(
                tool,
                "resolve_restore_tool_authorization",
                return_value=authorization,
            ) as resolve_authorization,
            mock.patch.object(
                tool,
                "load_pinned_archive",
                return_value=(
                    archive_manifest,
                    catalog,
                    checksums,
                ),
            ),
            mock.patch.object(
                tool,
                "validate_fresh_proof",
                return_value=(
                    pathlib.Path("/proof/manifest.json"),
                    {
                        "completedAtUtc":
                            "2026-08-31T20:00:00Z",
                    },
                ),
            ),
            mock.patch.object(
                tool.ARCHIVE,
                "container_identity",
                return_value={
                    "pgdataMount": {
                        "source": str(bundle),
                    },
                },
            ),
            mock.patch.object(
                tool.shutil,
                "disk_usage",
                return_value=types.SimpleNamespace(
                    free=10 * 1024**4),
            ),
            mock.patch.object(
                tool,
                "inspect_restore_image",
                return_value=(
                    "sha256:"
                    + drop_plan["restoreImageIdSha256"],
                    "pg_restore (PostgreSQL) 17",
                ),
            ),
            mock.patch.object(
                tool,
                "database_drop_state",
                return_value={
                    "dropExists": True,
                    "restoreExists": False,
                    "originalExists": False,
                    "oldOidExists": False,
                    "holdActive": True,
                },
            ),
            mock.patch.object(
                tool,
                "require_database_identity",
                return_value={"database": "fstservice"},
            ),
            mock.patch.object(
                tool,
                "repository_identity",
                return_value={
                    "gitCommit": "1" * 40,
                    "toolPath":
                        "tools/postgres-snapshot-generation-restore.py",
                    "toolSha256": "c" * 64,
                },
            ),
            mock.patch.object(
                tool,
                "write_new_bytes",
            ) as write_bytes,
            mock.patch.object(
                tool,
                "write_new_json",
            ) as write_json,
        ):
            plan = tool.build_plan(args)

        resolve_authorization.assert_called_once()
        self.assertEqual(
            args.authorization_id,
            resolve_authorization.call_args.kwargs[
                "authorization_id"],
        )
        self.assertEqual(
            args.repair_package,
            resolve_authorization.call_args.kwargs[
                "repair_package"],
        )
        self.assertEqual(
            "authorized",
            plan["restoreToolAuthorization"]["mode"],
        )
        self.assertEqual(
            expected["selectedTocEntries"],
            len(plan["selectedTocEntries"]),
        )
        self.assertEqual(
            expected["executedTocEntries"],
            len(plan["executedTocEntries"]),
        )
        self.assertEqual(
            expected["primaryIndexName"],
            plan["archivedIndexNames"]["pk"],
        )
        self.assertEqual(
            expected["scoreIndexName"],
            plan["archivedIndexNames"]["score"],
        )
        write_bytes.assert_called_once()
        write_json.assert_called_once()

    def test_selects_only_exact_child_toc_entries(self):
        child = {
            "name":
                "leaderboard_entries_snapshot_pro_cymbals_s1314",
            "indexes": [
                {
                    "indexName":
                        "leaderboard_entries_snapshot_pro_cymbals_s1314_pkey",
                    "isPrimary": True,
                    "isUnique": True,
                    "isValid": True,
                    "isReady": True,
                    "accessMethod": "btree",
                    "tablespaceName": "pg_default",
                    "columnNames": [
                        "snapshot_id",
                        "song_id",
                        "instrument",
                        "account_id",
                    ],
                    "definition": (
                        "CREATE UNIQUE INDEX ignored ON public.ignored "
                        "USING btree (snapshot_id, song_id, instrument, account_id)"
                    ),
                },
                {
                    "indexName":
                        "leaderboard_entries_snapshot_pro_cymbals_s1314_snapshot_id_song_id_instrument_score_idx",
                    "isPrimary": False,
                    "isUnique": False,
                    "isValid": True,
                    "isReady": True,
                    "accessMethod": "btree",
                    "tablespaceName": "pg_default",
                    "columnNames": [
                        "snapshot_id",
                        "song_id",
                        "instrument",
                        "score",
                    ],
                    "definition": (
                        "CREATE INDEX ignored ON public.ignored "
                        "USING btree (snapshot_id, song_id, instrument, score DESC)"
                    ),
                },
            ],
        }
        toc = "\n".join(
            [
                (
                    "820; 1259 16414 TABLE public "
                    "leaderboard_entries_snapshot_pro_cymbals_s1314 fst"
                ),
                (
                    "5466; 0 16414 TABLE DATA public "
                    "leaderboard_entries_snapshot_pro_cymbals_s1314 fst"
                ),
                (
                    "5311; 2606 16420 CONSTRAINT public "
                    "leaderboard_entries_snapshot_pro_cymbals_s1314 "
                    "leaderboard_entries_snapshot_pro_cymbals_s1314_pkey fst"
                ),
                (
                    "5312; 1259 16421 INDEX public "
                    "leaderboard_entries_snapshot_pro_cymbals_s1314_snapshot_id_song_id_instrument_score_idx fst"
                ),
                (
                    "6000; 0 0 TABLE ATTACH public "
                    "leaderboard_entries_snapshot_pro_cymbals_s1314 fst"
                ),
                (
                    "6001; 0 0 INDEX ATTACH public "
                    "leaderboard_entries_snapshot_pro_cymbals_s1314_pkey fst"
                ),
                "1; 1259 100 TABLE public leaderboard_entries_snapshot fst",
                (
                    "2; 1259 101 TABLE public "
                    "leaderboard_entries_snapshot_pro_cymbals fst"
                ),
                (
                    "3; 0 101 TABLE DATA public "
                    "leaderboard_entries_snapshot_pro_cymbals fst"
                ),
                (
                    "4; 2606 102 CONSTRAINT public "
                    "leaderboard_entries_snapshot_pro_cymbals "
                    "leaderboard_entries_snapshot_pro_cymbals_pkey fst"
                ),
                (
                    "5; 1259 103 INDEX public "
                    "leaderboard_entries_snapshot_pro_cymbals_"
                    "snapshot_id_song_id_instrument_score_idx fst"
                ),
            ]
        )

        selected = tool.select_restore_toc(toc, child)

        self.assertEqual(4, len(selected))
        self.assertEqual(
            ["820", "5466", "5311", "5312"],
            [line.split(";", 1)[0] for line in selected],
        )
        self.assertFalse(
            any("ATTACH" in line for line in selected)
        )
        parsed = [
            tool.parse_toc_entry(line)
            for line in selected
        ]
        self.assertTrue(
            all(
                entry["object"] == child["name"]
                or entry["object"]
                in {
                    child["indexes"][0]["indexName"],
                    child["indexes"][1]["indexName"],
                }
                for entry in parsed
            )
        )
        self.assertFalse(
            any(
                " TABLE public leaderboard_entries_snapshot "
                in line
                or (
                    "leaderboard_entries_snapshot_pro_cymbals "
                    in line
                    and child["name"] not in line
                )
                for line in selected
            )
        )

    def test_missing_or_duplicate_toc_entries_reject(self):
        child = {
            "name": "child",
            "indexes": [
                {"indexName": "child_pkey", "isPrimary": True},
                {"indexName": "child_score_idx", "isPrimary": False},
            ],
        }
        with self.assertRaises(tool.RestoreError):
            tool.select_restore_toc(
                "\n".join(
                    [
                        "1; 1 1 TABLE public child fst",
                        "2; 1 1 TABLE DATA public child fst",
                        "3; 1 1 CONSTRAINT public child child_pkey fst",
                    ]
                ),
                child,
            )

    def test_execution_list_contains_only_table_and_data(self):
        plan = {"restoreListPath": "unused"}
        with mock.patch.object(
            pathlib.Path,
            "read_text",
            return_value="1; TABLE\n2; TABLE DATA\n",
        ):
            self.assertEqual(
                "1; TABLE\n2; TABLE DATA\n",
                tool.restore_list_text(plan),
            )
        with mock.patch.object(
            pathlib.Path,
            "read_text",
            return_value=(
                "1; TABLE\n2; TABLE DATA\n"
                "3; CONSTRAINT\n4; INDEX\n"
            ),
        ):
            with self.assertRaises(tool.RestoreError):
                tool.restore_list_text(plan)

    def test_supported_index_specs_reject_extra_features(self):
        child = {
            "indexes": [
                {
                    "indexName": "pk",
                    "isPrimary": True,
                    "isUnique": True,
                    "isValid": True,
                    "isReady": True,
                    "accessMethod": "btree",
                    "tablespaceName": "pg_default",
                    "columnNames": [
                        "snapshot_id",
                        "song_id",
                        "instrument",
                        "account_id",
                    ],
                    "definition": (
                        "CREATE UNIQUE INDEX pk ON public.child "
                        "USING btree (snapshot_id, song_id, instrument, account_id)"
                    ),
                },
                {
                    "indexName": "score",
                    "isPrimary": False,
                    "isUnique": False,
                    "isValid": True,
                    "isReady": True,
                    "accessMethod": "btree",
                    "tablespaceName": "pg_default",
                    "columnNames": [
                        "snapshot_id",
                        "song_id",
                        "instrument",
                        "score",
                    ],
                    "definition": (
                        "CREATE INDEX score ON public.child "
                        "USING btree (snapshot_id, song_id, instrument, score DESC) "
                        "WHERE score > 0"
                    ),
                },
            ],
        }

        with self.assertRaises(tool.RestoreError):
            tool.validate_supported_index_specs(child)

        fixed = copy.deepcopy(child)
        fixed["indexes"][1]["definition"] = (
            "CREATE INDEX score ON public.child "
            "USING btree "
            "(snapshot_id, song_id, instrument, score DESC)"
        )
        fixed["indexes"][1].pop("predicate", None)
        fixed["indexes"][0].update(
            {
                "indNKeyAtts": 4,
                "indNAtts": 4,
                "keyAttnums": [1, 2, 3, 4],
                "opclassOids": [
                    3124,
                    3126,
                    3126,
                    3126,
                ],
                "collationOids": [0, 100, 100, 100],
                "indOptions": [0, 0, 0, 0],
            }
        )
        fixed["indexes"][1].update(
            {
                "indNKeyAtts": 4,
                "indNAtts": 4,
                "keyAttnums": [1, 2, 3, 5],
                "opclassOids": [
                    3124,
                    3126,
                    3126,
                    1978,
                ],
                "collationOids": [0, 100, 100, 0],
                "indOptions": [0, 0, 0, 3],
            }
        )
        tool.validate_supported_index_specs(fixed)

        drifted = copy.deepcopy(fixed)
        drifted["indexes"][1]["opclassOids"][-1] = 3124
        with self.assertRaises(tool.RestoreError):
            tool.validate_supported_index_specs(drifted)

    def test_live_q2_string_oid_catalog_is_supported(self):
        fixture, catalog, toc = (
            self.load_live_q2_archive_fixture()
        )
        expected = fixture["expected"]
        relations = {
            relation["name"]: relation
            for relation in catalog["physicalCatalog"]
        }
        child = relations[expected["childRelation"]]
        supported = tool.validate_supported_index_specs(
            child)
        self.assertEqual(
            expected["primaryIndexName"],
            supported["pk"]["indexName"],
        )
        self.assertEqual(
            expected["scoreIndexName"],
            supported["score"]["indexName"],
        )
        selected = tool.select_restore_toc(toc, child)
        self.assertEqual(
            expected["selectedTocEntries"],
            len(selected),
        )
        self.assertEqual(
            expected["logicalIndexShapeSha256"],
            tool.logical_index_shape_sha256(
                child,
                relations[expected["rootRelation"]],
                relations[expected["topRelation"]],
            ),
        )
        mixed = copy.deepcopy(relations)
        for relation_name in (
            expected["rootRelation"],
            expected["topRelation"],
        ):
            for index in mixed[relation_name]["indexes"]:
                index["opclassOids"] = [
                    int(value)
                    for value in index["opclassOids"]
                ]
                index["collationOids"] = [
                    int(value)
                    for value in index["collationOids"]
                ]
        self.assertEqual(
            expected["logicalIndexShapeSha256"],
            tool.logical_index_shape_sha256(
                mixed[expected["childRelation"]],
                mixed[expected["rootRelation"]],
                mixed[expected["topRelation"]],
            ),
        )
        drifted = copy.deepcopy(mixed)
        root_score = next(
            index
            for index in drifted[
                expected["rootRelation"]]["indexes"]
            if not index["isPrimary"]
        )
        root_score["opclassOids"][-1] = 3124
        with self.assertRaises(tool.RestoreError):
            tool.logical_index_shape_sha256(
                drifted[expected["childRelation"]],
                drifted[expected["rootRelation"]],
                drifted[expected["topRelation"]],
            )
        non_oid_drift = copy.deepcopy(mixed)
        root_primary = next(
            index
            for index in non_oid_drift[
                expected["rootRelation"]]["indexes"]
            if index["isPrimary"]
        )
        root_primary["indOptions"][0] = False
        with self.assertRaises(tool.RestoreError):
            tool.logical_index_shape_sha256(
                non_oid_drift[expected["childRelation"]],
                non_oid_drift[expected["rootRelation"]],
                non_oid_drift[expected["topRelation"]],
            )

    def test_postgres_oid_parser_is_strict(self):
        for value in (
            1,
            "1",
            3124,
            "3124",
            4294967295,
            "4294967295",
        ):
            with self.subTest(valid=value):
                self.assertEqual(
                    int(value),
                    tool.parse_postgres_oid(
                        value,
                        "oid",
                    ),
                )
        for value in (0, "0"):
            with self.subTest(valid_zero=value):
                self.assertEqual(
                    0,
                    tool.parse_postgres_oid(
                        value,
                        "oid",
                        allow_zero=True,
                    ),
                )
        invalid = (
            True,
            False,
            0,
            "0",
            -1,
            "-1",
            "+1",
            " 1",
            "1 ",
            "01",
            "00",
            "1.0",
            "1e3",
            1.0,
            1e3,
            4294967296,
            "4294967296",
            None,
            [],
            {},
        )
        for value in invalid:
            with self.subTest(invalid=value):
                with self.assertRaises(tool.RestoreError):
                    tool.parse_postgres_oid(
                        value,
                        "oid",
                    )
        for value in (
            True,
            False,
            -1,
            "-1",
            "+0",
            " 0",
            "00",
            "0.0",
            0.0,
            4294967296,
            "4294967296",
        ):
            with self.subTest(invalid_zero=value):
                with self.assertRaises(tool.RestoreError):
                    tool.parse_postgres_oid(
                        value,
                        "oid",
                        allow_zero=True,
                    )
        self.assertEqual(
            [3124, 3126, 0],
            tool.normalize_postgres_oid_array(
                [3124, "3126", "0"],
                "oids",
                allow_zero=True,
            ),
        )
        for value in (
            "3124",
            (3124,),
            [3124, "01"],
            [3124, True],
        ):
            with self.subTest(invalid_array=value):
                with self.assertRaises(tool.RestoreError):
                    tool.normalize_postgres_oid_array(
                        value,
                        "oids",
                        allow_zero=True,
                    )

    def test_supported_index_specs_reject_malformed_oid_strings(self):
        _, catalog, _ = self.load_live_q2_archive_fixture()
        child = next(
            relation
            for relation in catalog["physicalCatalog"]
            if relation["name"]
            == "leaderboard_entries_snapshot_pro_cymbals_s1314"
        )
        malformed = (
            ("opclassOids", 0, "03124"),
            ("opclassOids", 0, "+3124"),
            ("opclassOids", 0, " 3124"),
            ("opclassOids", 0, "3124 "),
            ("opclassOids", 0, "3124.0"),
            ("opclassOids", 0, "3e3"),
            ("opclassOids", 0, "4294967296"),
            ("opclassOids", 0, "0"),
            ("collationOids", 0, "00"),
            ("collationOids", 0, "+0"),
            ("collationOids", 0, " 0"),
            ("collationOids", 0, "0 "),
            ("collationOids", 0, "0.0"),
            ("collationOids", 0, "0e0"),
            ("collationOids", 0, "4294967296"),
        )
        for property_name, position, value in malformed:
            with self.subTest(
                property_name=property_name,
                value=value,
            ):
                candidate = copy.deepcopy(child)
                candidate["indexes"][0][
                    property_name][position] = value
                with self.assertRaises(tool.RestoreError):
                    tool.validate_supported_index_specs(
                        candidate)

    def test_non_oid_index_metadata_remains_number_only(self):
        _, catalog, _ = self.load_live_q2_archive_fixture()
        child = next(
            relation
            for relation in catalog["physicalCatalog"]
            if relation["name"]
            == "leaderboard_entries_snapshot_pro_cymbals_s1314"
        )
        mutations = {
            "indNKeyAtts": "4",
            "indNAtts": "4",
            "keyAttnums": ["1", 2, 3, 4],
            "indOptions": ["0", 0, 0, 0],
        }
        for property_name, value in mutations.items():
            with self.subTest(property_name=property_name):
                candidate = copy.deepcopy(child)
                primary = next(
                    index
                    for index in candidate["indexes"]
                    if index["isPrimary"]
                )
                primary[property_name] = value
                with self.assertRaises(tool.RestoreError):
                    tool.validate_supported_index_specs(
                        candidate)

    def test_parent_and_root_entries_cannot_satisfy_child_selection(self):
        child = {
            "name":
                "leaderboard_entries_snapshot_pro_cymbals_s1314",
            "indexes": [
                {
                    "indexName":
                        "leaderboard_entries_snapshot_pro_cymbals_s1314_pkey",
                    "isPrimary": True,
                },
                {
                    "indexName":
                        "leaderboard_entries_snapshot_pro_cymbals_s1314_score_idx",
                    "isPrimary": False,
                },
            ],
        }
        toc = "\n".join(
            [
                "1; 1259 1 TABLE public leaderboard_entries_snapshot fst",
                (
                    "2; 0 1 TABLE DATA public "
                    "leaderboard_entries_snapshot_pro_cymbals fst"
                ),
                (
                    "3; 2606 1 CONSTRAINT public "
                    "leaderboard_entries_snapshot_pro_cymbals "
                    "leaderboard_entries_snapshot_pro_cymbals_s1314_pkey fst"
                ),
                (
                    "4; 1259 1 INDEX public "
                    "leaderboard_entries_snapshot_pro_cymbals_s1314_score_idx fst"
                ),
            ]
        )

        with self.assertRaises(tool.RestoreError):
            tool.select_restore_toc(toc, child)

    def test_detached_catalog_ignores_only_temporary_restore_check(self):
        child = {
            "schema": "public",
            "name": "child",
            "relationKind": "r",
            "persistenceKind": "p",
            "owner": "fst",
            "tablespace": "pg_default",
            "accessMethod": "heap",
            "relationOptions": [],
            "partitionKey": "",
            "partitionBound": "FOR VALUES IN ('1005')",
            "parentSchema": "public",
            "parentRelation": "root",
            "columns": [],
            "constraints": [
                {
                    "name": "child_pkey",
                    "type": "p",
                    "definition": "PRIMARY KEY (id)",
                    "validated": True,
                },
                {
                    "name": "ck_sgr_1005_abcdef",
                    "type": "c",
                    "definition": "CHECK ((snapshot_id = 1005))",
                    "validated": True,
                },
            ],
            "indexes": [],
        }

        detached = tool.detached_logical_child(
            child,
            "ck_sgr_1005_abcdef",
        )

        self.assertEqual("", detached["partitionBound"])
        self.assertIsNone(detached["parentSchema"])
        self.assertEqual(
            ["child_pkey"],
            [
                constraint["name"]
                for constraint in detached["constraints"]
            ],
        )

    def test_cli_has_no_arbitrary_database_object_surface(self):
        parser = tool.build_parser()
        destinations = set()
        for action in parser._actions:
            destinations.add(action.dest)
        for action in parser._subparsers._group_actions:
            for child in action.choices.values():
                destinations.update(
                    item.dest for item in child._actions
                )

        for prohibited in (
            "relation",
            "schema",
            "table",
            "sql",
            "instrument",
            "snapshot_id",
            "force",
            "all",
        ):
            self.assertNotIn(prohibited, destinations)
        self.assertNotIn(
            "authorize-repair-tool",
            parser._subparsers._group_actions[0]
                .choices,
        )

    def test_restore_tool_authorization_resolver_is_exact(self):
        drop_plan = {
            "dropOperationId": "1" * 32,
            "planDigest": "2" * 64,
            "restoreToolSha256": "8" * 64,
            "recoveryBundleManifestSha256": "9" * 64,
        }
        bundle = pathlib.Path("/bundle")
        package = pathlib.Path("/repair")
        repair = {
            "dropOperationId":
                drop_plan["dropOperationId"],
            "dropPlanDigest": drop_plan["planDigest"],
            "originalBundleManifestSha256": "9" * 64,
            "pinnedRestoreToolSha256": "8" * 64,
            "validatorBaseToolSha256":
                tool.VALIDATOR_BASE_TOOL_SHA256,
            "authorizedRestoreToolSha256": "a" * 64,
            "authorizedArchiveHelperSha256": "b" * 64,
            "authorizerBinarySha256": "c" * 64,
            "repositoryCommit": "4" * 40,
            "repositoryTreeId": "5" * 40,
            "pinnedToBaseDiffSha256": "d" * 64,
            "baseToFinalDiffSha256": "e" * 64,
            "sourceManifestSha256": "f" * 64,
            "testEvidenceManifestSha256": "6" * 64,
        }
        authorization = {
            **repair,
            "repairPackageManifestSha256": "7" * 64,
            "evidenceSha256": "0" * 64,
            "canonicalEvidenceDbSha256": "3" * 64,
            "authorizedAt":
                "2026-08-29T14:00:00+00:00",
        }
        authorization_id = (
            tool.derive_restore_tool_authorization_id(
                authorization)
        )
        authorization["authorizationId"] = (
            authorization_id
        )

        def authorized_hash(path):
            path = pathlib.Path(path)
            if path == pathlib.Path(tool.__file__):
                return "a" * 64
            if path.name == "restore-tool.py":
                return (
                    "8" * 64
                    if path.parent == bundle
                    else "a" * 64
                )
            return "b" * 64

        args = types.SimpleNamespace(
            source_container="source",
            pg_user="fst",
            pg_database="fst",
        )
        with (
            mock.patch.object(
                tool,
                "sha256_path",
                side_effect=authorized_hash,
            ),
            mock.patch.object(
                tool,
                "validate_repair_package",
                return_value=(package, repair),
            ),
            mock.patch.object(
                tool,
                "read_restore_tool_authorization",
                return_value=authorization,
            ),
        ):
            resolved = (
                tool.resolve_restore_tool_authorization(
                    args,
                    drop_plan,
                    bundle,
                    authorization_id=authorization_id,
                    repair_package=package,
                    repair_package_manifest_sha256=
                        "7" * 64,
                )
            )
            with self.assertRaises(tool.RestoreError):
                tool.resolve_restore_tool_authorization(
                    args,
                    drop_plan,
                    bundle,
                )

        self.assertEqual("authorized", resolved["mode"])
        self.assertEqual(
            "a" * 64,
            resolved["executingToolSha256"],
        )
        self.assertEqual(
            authorization_id,
            resolved["authorizationId"],
        )

        def pinned_hash(path):
            path = pathlib.Path(path)
            if path.name == (
                "postgres-snapshot-generation-archive.py"
            ):
                return "b" * 64
            return "8" * 64

        with mock.patch.object(
            tool,
            "sha256_path",
            side_effect=pinned_hash,
        ):
            pinned = (
                tool.resolve_restore_tool_authorization(
                    args,
                    drop_plan,
                    bundle,
                )
            )
        self.assertEqual("pinned", pinned["mode"])
        self.assertIsNone(pinned["authorizationId"])

    def test_old_authorization_warns_without_expiring(self):
        args = types.SimpleNamespace(
            source_container="source",
            pg_user="fst",
            pg_database="fst",
        )
        drop_plan = {
            "dropOperationId": "1" * 32,
            "planDigest": "2" * 64,
        }
        authorization = {
            "authorizationId": "3" * 32,
            "authorizedAt":
                "2000-01-01T00:00:00+00:00",
        }
        warning = io.StringIO()
        with (
            mock.patch.object(
                tool.ARCHIVE,
                "psql_json",
                return_value=authorization,
            ),
            contextlib.redirect_stderr(warning),
        ):
            observed = (
                tool.read_restore_tool_authorization(
                    args,
                    drop_plan,
                    "3" * 32,
                )
            )

        self.assertEqual(authorization, observed)
        self.assertIn(
            "WARNING: restore-tool authorization",
            warning.getvalue(),
        )

    def test_restore_source_has_no_drop_surface(self):
        source = SCRIPT.read_text(encoding="utf-8").lower()
        self.assertNotIn("drop table", source)
        self.assertNotIn("drop database", source)
        self.assertNotIn("drop schema", source)
        self.assertNotIn("/var/run/docker.sock", source)
        self.assertGreaterEqual(
            source.count(
                "revalidate_restore_tool_authorization("),
            3,
        )
        self.assertIn(
            "sql = restore_tool_authorization_lookup_sql(",
            source,
        )
        self.assertNotIn(
            "snapshot_generation_restore_tool_authorizations\n"
            "                authorization",
            source,
        )

    def test_authorization_lookup_uses_safe_postgres_alias(self):
        sql = tool.restore_tool_authorization_lookup_sql(
            {
                "dropOperationId": "1" * 32,
                "planDigest": "2" * 64,
            },
            "3" * 32,
        )
        self.assertIn(
            "snapshot_generation_restore_tool_authorizations"
            "\n                auth_row",
            sql,
        )
        self.assertIn(
            "auth_row.authorization_id",
            sql,
        )
        self.assertNotIn("authorization.", sql)

    def test_actor_and_reference_validation_is_strict(self):
        self.assertEqual(
            "operator",
            tool.validate_actor("operator", "actor"),
        )
        for value in ("", " ", "bad\nreference", "x" * 513):
            with self.assertRaises(tool.RestoreError):
                tool.validate_actor(value, "actor")

    def test_confirm_report_is_accepted_and_tampering_rejects(self):
        target = {
            "instrument": "Solo_PeripheralCymbals",
            "snapshotId": 1314,
            "childOid": 319748510,
            "childRelfilenode": 319748510,
            "rowCount": 8627,
            "rowFingerprintSha256": "8" * 64,
        }
        plan = {
            "dropOperationId": "a" * 32,
            "planDigest": "b" * 64,
            "activePlan": {"archive": target},
        }
        report = {
            "schemaVersion": 1,
            "toolId": "fst.snapshot-generation-drop-only.v1",
            "action": "confirm",
            "dropOperationId": plan["dropOperationId"],
            "planDigest": plan["planDigest"],
            "status": "dropped",
            "commitOutcome": "confirmed",
            "completedAtUtc": "2026-08-30T12:00:00+00:00",
            "actor": "operator",
            "reference": "confirmation",
            **target,
            "evidence": {},
        }
        report_bytes = (
            self.canonical_drop_report_bytes(report)
        )

        with mock.patch.object(
            pathlib.Path,
            "read_bytes",
            return_value=report_bytes,
        ):
            self.assertEqual(
                {
                    **report,
                    "reportSha256": json.loads(
                        report_bytes
                    )["reportSha256"],
                },
                tool.validate_drop_report(
                    pathlib.Path("unused"),
                    plan,
                ),
            )
        tampered = report_bytes.replace(
            b'"rowCount":8627',
            b'"rowCount":8628',
        )
        with mock.patch.object(
            pathlib.Path,
            "read_bytes",
            return_value=tampered,
        ):
            with self.assertRaises(tool.RestoreError):
                tool.validate_drop_report(
                    pathlib.Path("unused"),
                    plan,
                )

    def test_restore_attestation_uses_semantics_not_raw_index_names(self):
        target = {
            "childRelation": "child",
            "rootRelation": "root",
            "childOid": 10,
            "rowCount": 1,
            "rowFingerprintSha256": "1" * 64,
            "logicalCatalogSha256": "2" * 64,
        }
        plan = {
            "restoreOperationId": "a" * 32,
            "recoveryBundlePath": "/unused",
            "semanticCatalogSha256": "3" * 64,
            "logicalIndexShapeSha256": "4" * 64,
            "target": target,
        }
        args = types.SimpleNamespace(
            attested_by="operator",
            baseline_route_manifest="baseline",
            candidate_route_manifest="candidate",
            source_container="container",
            pg_user="fst",
            pg_database="fst",
            timeout_seconds=30,
        )
        final_catalog = [
            {"name": "leaderboard_entries_snapshot"},
            {"name": "root"},
            {"name": "child", "indexName": "sgri_new"},
        ]
        source_catalog = {
            "physicalCatalog": [
                {"name": "child", "indexName": "archived_old"},
            ],
        }
        parity = {
            "publicationId": 1,
            "publishedScrapeId": 1,
            "baselineManifestSha256": "5" * 64,
            "candidateManifestSha256": "6" * 64,
        }
        with (
            mock.patch.object(
                tool,
                "validate_path",
                side_effect=lambda value, **_: pathlib.Path(value),
            ),
            mock.patch.object(
                tool,
                "validate_route_pair",
                return_value=parity,
            ),
            mock.patch.object(
                tool,
                "confirm_restore",
                return_value={"newOid": 11},
            ),
            mock.patch.object(
                tool.ARCHIVE,
                "capture_catalog",
                return_value=final_catalog,
            ),
            mock.patch.object(
                tool.ARCHIVE,
                "stream_fingerprint",
                return_value={
                    "rowCount": 1,
                    "sha256": "1" * 64,
                },
            ),
            mock.patch.object(
                tool.ARCHIVE,
                "logical_catalog",
                return_value=[{"raw": "renamed"}],
            ),
            mock.patch.object(
                tool.ARCHIVE,
                "stable_hash",
                return_value="7" * 64,
            ),
            mock.patch.object(
                tool,
                "load_pinned_archive",
                return_value=(
                    {"target": {"childRelation": "child"}},
                    source_catalog,
                    {},
                ),
            ),
            mock.patch.object(
                tool,
                "detached_base_relation",
                return_value={"base": "same"},
            ),
            mock.patch.object(
                tool,
                "logical_index_shape_sha256",
                return_value="4" * 64,
            ),
            mock.patch.object(
                tool,
                "psql_mutation",
            ),
        ):
            report = tool.attest_restore(args, plan)

        self.assertEqual("accepted", report["status"])
        evidence = report["databaseEvidence"]
        self.assertEqual(
            "7" * 64,
            evidence["logicalCatalogSha256"],
        )
        self.assertEqual(
            "2" * 64,
            evidence["archivedLogicalCatalogSha256"],
        )
        self.assertTrue(
            evidence["baseRelationSemanticMatch"]
        )


if __name__ == "__main__":
    unittest.main()
