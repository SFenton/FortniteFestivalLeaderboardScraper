import copy
import hashlib
import json
from pathlib import Path
import subprocess
import sys
import unittest

import snapshot_retention_deployment_parity as tool


def response():
    proof = {
        "version": 2, "statisticsSource": "pg_stat_xact_user_tables",
        "backendScope": "fresh_unpooled_single_transaction", "schemaSqlSha256": "c" * 64,
        "allowedRetentionRelations": [],
        "nonRetentionDml": {"inserted": 0, "updated": 0, "deleted": 0}, "allowedRetentionChanges": [],
        "nonRetentionRelationIdentity": {
            "scope": "non_system_non_temporary_user_tables", "relationKinds": ["f", "m", "p", "r"],
            "excludedRetentionRelations": tool.IDENTITY_RETENTION_EXCLUSIONS,
            "beforeCount": 230, "afterCount": 230, "beforeSha256": "d" * 64, "afterSha256": "d" * 64,
            "unchanged": True,
        },
    }
    proof["sha256"] = hashlib.sha256(json.dumps(proof, separators=(",", ":")).encode()).hexdigest()
    return {"outcome": "schema_current", "scope": "snapshot_generation_retention",
            "transactionCommitted": True, "hostedServicesStarted": False, "dmlProof": proof}


def snapshot():
    records = [{"section": "rows", "table": name, "count": 1, "sha256": "a" * 64}
               for name in sorted(tool.REQUIRED_ROWS)]
    records += [
        {"section": "scopeBinding", "value": {"valid": True}},
        {"section": "topology", "rows": [{"oid": 1, "relfilenode": 2, "bytes": 4096,
                                         "n_tup_ins": 10, "n_tup_upd": 20, "n_tup_del": 0}]},
        {"section": "nonRetentionMutationCounters", "rows": [{"relname": "registered_users", "n_tup_upd": 20}]},
        {"section": "databaseCounters", "value": {"tempBytes": 0}},
        {"section": "retentionCycles", "rows": []},
        {"section": "reportSchemaConstraints", "rows": [{"definition": "canonical"}]},
        {"section": "workerConfigurationRelation", "present": True},
        {"section": "sequences", "value": {"scrapeNext": 100}},
        {"section": "nonRetentionSchema", "sha256": "b" * 64},
    ]
    return {"records": records}


def section(value, name):
    return next(item for item in value["records"] if item["section"] == name)


class CausalParityTests(unittest.TestCase):
    def test_valid_zero_proof_and_exact_source_accept(self):
        result = tool.compare_deployment_snapshots(snapshot(), snapshot(), response())
        self.assertTrue(result["sourceParityAccepted"])
        self.assertFalse(result["deploymentAuthorized"])

    def test_ambient_cumulative_delta_does_not_override_causal_proof(self):
        before = snapshot()
        after = copy.deepcopy(before)
        section(after, "nonRetentionMutationCounters")["rows"][0]["n_tup_upd"] += 8
        section(after, "topology")["rows"][0]["n_tup_upd"] += 228
        result = tool.compare_deployment_snapshots(before, after, response())
        self.assertTrue(result["sourceParityAccepted"])
        self.assertTrue(result["cumulativeCounterTelemetry"]["changed"])
        self.assertFalse(result["cumulativeCounterTelemetry"]["causalProof"])
        self.assertFalse(result["cumulativeCounterTelemetry"]["usedForAcceptance"])

    def test_actual_row_schema_topology_path_control_or_sequence_drift_rejects(self):
        for name in ("rows", "nonRetentionSchema", "topology", "scopeBinding", "sequences"):
            with self.subTest(section=name):
                before = snapshot()
                after = copy.deepcopy(before)
                section(after, name)["unexpected"] = True
                result = tool.compare_deployment_snapshots(before, after, response())
                self.assertFalse(result["sourceParityAccepted"])

    def test_nonzero_transaction_dml_refuses_even_with_equal_sources(self):
        result = response()
        result["dmlProof"]["nonRetentionDml"]["updated"] = 1
        with self.assertRaisesRegex(ValueError, "transaction_non_retention_dml_not_zero"):
            tool.compare_deployment_snapshots(snapshot(), snapshot(), result)

    def test_missing_uncommitted_or_tampered_proof_refuses(self):
        for field in ("missing", "uncommitted", "digest"):
            with self.subTest(field=field):
                result = response()
                if field == "missing":
                    del result["dmlProof"]
                elif field == "uncommitted":
                    result["transactionCommitted"] = False
                else:
                    result["dmlProof"]["sha256"] = "0" * 64
                with self.assertRaises(ValueError):
                    tool.compare_deployment_snapshots(snapshot(), snapshot(), result)

    def test_a_prefix_or_new_relation_cannot_widen_empty_allowlist(self):
        result = response()
        result["dmlProof"]["allowedRetentionRelations"] = ["public.snapshot_generation_retention_unreviewed"]
        with self.assertRaisesRegex(ValueError, "transaction_dml_proof_contract_invalid"):
            tool.compare_deployment_snapshots(snapshot(), snapshot(), result)

    def test_combined_identity_proof_is_required_and_cannot_hide_rewrites(self):
        for defect in ("legacy-version", "missing", "changed", "scope", "exclusions"):
            with self.subTest(defect=defect):
                result = response()
                identity = result["dmlProof"]["nonRetentionRelationIdentity"]
                if defect == "legacy-version":
                    result["dmlProof"]["version"] = 1
                elif defect == "missing":
                    del result["dmlProof"]["nonRetentionRelationIdentity"]
                elif defect == "changed":
                    identity["afterSha256"] = "e" * 64
                elif defect == "scope":
                    identity["relationKinds"] = ["r"]
                else:
                    identity["excludedRetentionRelations"] = ["public.snapshot_generation_retention_%"]
                with self.assertRaises(ValueError):
                    tool.compare_deployment_snapshots(snapshot(), snapshot(), result)

    def test_unknown_commit_never_passes_even_with_valid_combined_proof(self):
        result = response()
        result["outcome"] = "uncertain"
        result["code"] = "commit_acknowledgement_unknown"
        result["transactionCommitted"] = None
        result["possibleSchemaProof"] = {"combinedProofSha256": result["dmlProof"]["sha256"]}
        with self.assertRaisesRegex(ValueError, "initializer_not_committed"):
            tool.compare_deployment_snapshots(snapshot(), snapshot(), result)
        result["outcome"] = "schema_current"
        result["code"] = None
        with self.assertRaisesRegex(ValueError, "initializer_not_committed"):
            tool.compare_deployment_snapshots(snapshot(), snapshot(), result)

    def test_incomplete_or_duplicate_source_artifacts_refuse(self):
        for duplicate in (False, True):
            with self.subTest(duplicate=duplicate):
                before = snapshot()
                if duplicate:
                    before["records"].append(before["records"][0])
                else:
                    before["records"].pop()
                with self.assertRaises(ValueError):
                    tool.compare_deployment_snapshots(before, snapshot(), response())

    def test_expected_retention_ddl_is_visible_not_source_drift(self):
        before = snapshot()
        after = copy.deepcopy(before)
        section(after, "reportSchemaConstraints")["rows"] = [{"definition": "new canonical"}]
        result = tool.compare_deployment_snapshots(before, after, response())
        self.assertTrue(result["sourceParityAccepted"])
        self.assertEqual(1, len(result["allowedRetentionSchemaChanges"]))

    def test_equal_but_malformed_source_evidence_does_not_pass(self):
        for defect in ("digest", "count", "topology"):
            with self.subTest(defect=defect):
                value = snapshot()
                if defect == "digest":
                    section(value, "rows")["sha256"] = None
                elif defect == "count":
                    section(value, "rows")["count"] = -1
                else:
                    section(value, "topology")["rows"] = []
                with self.assertRaises(ValueError):
                    tool.compare_deployment_snapshots(value, value, response())

    def test_cli_missing_artifact_refuses_without_echoing_the_path(self):
        missing = Path(__file__).with_name("private-missing-artifact.json")
        result = subprocess.run(
            [sys.executable, "-B", str(Path(tool.__file__)), "--before", str(missing),
             "--after", str(missing), "--initializer-result", str(missing)],
            text=True, capture_output=True, timeout=10)
        self.assertEqual(2, result.returncode)
        self.assertEqual("artifact_read_failed", json.loads(result.stdout)["code"])
        self.assertNotIn("private-missing", result.stdout + result.stderr)
        self.assertEqual("", result.stderr)


if __name__ == "__main__":
    unittest.main()
