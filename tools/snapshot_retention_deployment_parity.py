"""Read-only artifact classification for retention-schema source parity."""
import argparse
import hashlib
import json
from pathlib import Path
import re

PROOF_FIELDS = ("version", "statisticsSource", "backendScope", "schemaSqlSha256", "allowedRetentionRelations",
                "nonRetentionDml", "allowedRetentionChanges", "nonRetentionRelationIdentity")
IDENTITY_FIELDS = ("scope", "relationKinds", "excludedRetentionRelations", "beforeCount", "afterCount",
                   "beforeSha256", "afterSha256", "unchanged")
IDENTITY_RETENTION_EXCLUSIONS = [
    "public.snapshot_generation_retention_" + name
    for name in ("cycles", "deferrals", "evidence", "holds", "observations", "worker_configuration")
]
CUMULATIVE_FIELDS = ("n_tup_ins", "n_tup_upd", "n_tup_del")
REQUIRED_SECTIONS = {
    "rows", "scopeBinding", "topology", "nonRetentionMutationCounters", "databaseCounters",
    "retentionCycles", "reportSchemaConstraints", "workerConfigurationRelation", "sequences",
    "nonRetentionSchema",
}
REQUIRED_ROWS = {
    "scrape_log", "scrape_publication_state", "publication_generations", "publication_surface_bindings",
    "leaderboard_snapshot_state", "solo_current_projection_scope", "snapshot_generation_retention_holds",
    "scrape_writer_failures", "service_worker_status", "publication_path_artifacts", "publication_song_catalog",
    "snapshot_generation_retirement_control", "snapshot_generation_retirement_policy_epochs",
    "snapshot_generation_retirement_jobs", "snapshot_generation_retirement_events",
    "live_song_catalog", "songs", "api_response_cache", "publication_api_response_cache",
    "snapshot_generation_retention_worker_configuration", "all_publication_scope_sources",
}


def require_initializer_dml_proof(response):
    if response.get("outcome") != "schema_current" or response.get("scope") != "snapshot_generation_retention" \
            or response.get("transactionCommitted") is not True or response.get("hostedServicesStarted") is not False \
            or response.get("code") is not None or response.get("sqlState") is not None:
        raise ValueError("initializer_not_committed")
    proof = response.get("dmlProof")
    if not isinstance(proof, dict) or set(proof) != {*PROOF_FIELDS, "sha256"} \
            or type(proof["version"]) is not int or proof["version"] != 2 \
            or proof["statisticsSource"] != "pg_stat_xact_user_tables" \
            or proof["backendScope"] != "fresh_unpooled_single_transaction" \
            or proof["allowedRetentionRelations"] != [] or proof["allowedRetentionChanges"] != []:
        raise ValueError("transaction_dml_proof_contract_invalid")
    if not isinstance(proof["schemaSqlSha256"], str) or re.fullmatch(r"[0-9a-f]{64}", proof["schemaSqlSha256"]) is None:
        raise ValueError("schema_step_identity_invalid")
    totals = proof["nonRetentionDml"]
    if not isinstance(totals, dict) or set(totals) != {"inserted", "updated", "deleted"} \
            or any(type(totals[key]) is not int or totals[key] != 0 for key in totals):
        raise ValueError("transaction_non_retention_dml_not_zero")
    identity = proof["nonRetentionRelationIdentity"]
    if not isinstance(identity, dict) or set(identity) != set(IDENTITY_FIELDS) \
            or identity["scope"] != "non_system_non_temporary_user_tables" \
            or identity["relationKinds"] != ["f", "m", "p", "r"] \
            or identity["excludedRetentionRelations"] != IDENTITY_RETENTION_EXCLUSIONS \
            or any(type(identity[key]) is not int or identity[key] < 0 for key in ("beforeCount", "afterCount")) \
            or identity["beforeCount"] != identity["afterCount"] or identity["unchanged"] is not True \
            or any(not isinstance(identity[key], str) or re.fullmatch(r"[0-9a-f]{64}", identity[key]) is None
                   for key in ("beforeSha256", "afterSha256")) \
            or identity["beforeSha256"] != identity["afterSha256"]:
        raise ValueError("non_retention_relation_identity_not_proven")
    canonical = {key: proof[key] for key in PROOF_FIELDS}
    canonical["nonRetentionDml"] = {key: totals[key] for key in ("inserted", "updated", "deleted")}
    canonical["nonRetentionRelationIdentity"] = {key: identity[key] for key in IDENTITY_FIELDS}
    digest = hashlib.sha256(json.dumps(canonical, separators=(",", ":")).encode()).hexdigest()
    if proof["sha256"] != digest:
        raise ValueError("transaction_dml_proof_digest_mismatch")
    return proof


def classify_cumulative_counters(before, after):
    if not isinstance(before, list) or not isinstance(after, list):
        raise ValueError("cumulative_counter_artifact_invalid")
    return {
        "classification": "non_causal_cumulative_telemetry",
        "causalProof": False,
        "usedForAcceptance": False,
        "changed": before != after,
        "before": before,
        "after": after,
        "interpretation": "Asynchronously flushed/cached, database-wide counters; no transaction attribution.",
    }


def _records(snapshot):
    records = snapshot.get("records")
    if not isinstance(records, list):
        raise ValueError("source_artifact_records_missing")
    result = {}
    for record in records:
        if not isinstance(record, dict) or "section" not in record:
            raise ValueError("source_artifact_record_invalid")
        key = (record["section"], record.get("table"))
        if key in result:
            raise ValueError("source_artifact_duplicate_record")
        result[key] = record
    if {key[0] for key in result} != REQUIRED_SECTIONS \
            or {key[1] for key in result if key[0] == "rows"} != REQUIRED_ROWS:
        raise ValueError("source_artifact_coverage_mismatch")
    for (section, _), record in result.items():
        if section in ("rows", "nonRetentionSchema"):
            digest = record.get("sha256")
            if not isinstance(digest, str) or re.fullmatch(r"[0-9a-f]{64}", digest) is None:
                raise ValueError("source_artifact_digest_invalid")
        if section == "rows" and (type(record.get("count")) is not int or record["count"] < 0):
            raise ValueError("source_artifact_row_count_invalid")
    topology = result[("topology", None)].get("rows")
    if not isinstance(topology, list) or not topology:
        raise ValueError("source_topology_missing")
    ids = set()
    for row in topology:
        if not isinstance(row, dict) or any(type(row.get(key)) is not int or row[key] < 0
                                            for key in ("oid", "relfilenode", "bytes")) \
                or row["oid"] <= 0 or row["oid"] in ids:
            raise ValueError("source_topology_identity_invalid")
        ids.add(row["oid"])
    return result


def compare_deployment_snapshots(before, after, initializer):
    proof = require_initializer_dml_proof(initializer)
    first, last = _records(before), _records(after)
    counters = classify_cumulative_counters(
        first[("nonRetentionMutationCounters", None)]["rows"],
        last[("nonRetentionMutationCounters", None)]["rows"])
    changes, retention_ddl = [], []
    source_counter_telemetry = []
    for key in sorted(first.keys() | last.keys(), key=str):
        a, b = first.get(key), last.get(key)
        if key[0] in ("nonRetentionMutationCounters", "databaseCounters"):
            continue
        if key[0] in ("reportSchemaConstraints", "workerConfigurationRelation"):
            if a != b:
                retention_ddl.append({"section": key[0], "before": a, "after": b})
            continue
        if key[0] == "topology":
            for side, record in (("before", a), ("after", b)):
                source_counter_telemetry.append({
                    "side": side,
                    "rows": [{name: row[name] for name in ("oid", *CUMULATIVE_FIELDS) if name in row}
                             for row in record["rows"]],
                })
            a = a | {"rows": [{name: value for name, value in row.items() if name not in CUMULATIVE_FIELDS}
                              for row in a["rows"]]}
            b = b | {"rows": [{name: value for name, value in row.items() if name not in CUMULATIVE_FIELDS}
                              for row in b["rows"]]}
        if a != b:
            changes.append({"section": key[0], "table": key[1]})
    return {
        "scope": "retention_schema_source_parity",
        "sourceParityAccepted": not changes,
        "transactionDmlProof": proof,
        "actualSourceChanges": changes,
        "allowedRetentionSchemaChanges": retention_ddl,
        "cumulativeCounterTelemetry": counters,
        "sourceCumulativeCounterTelemetry": {
            "classification": "non_causal_cumulative_telemetry", "usedForAcceptance": False,
            "snapshots": source_counter_telemetry,
        },
        "resourceTelemetry": {
            "before": first[("databaseCounters", None)],
            "after": last[("databaseCounters", None)],
            "unexpectedLocksOrPressureStillReject": True,
        },
        "deploymentAuthorized": False,
        "remainingGates": "Exact binary/source pins, public-body parity, source completeness, ownership and bounded lock/resource admission remain required.",
    }


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--before", required=True)
    parser.add_argument("--after", required=True)
    parser.add_argument("--initializer-result", required=True)
    args = parser.parse_args()
    try:
        result = compare_deployment_snapshots(
            json.loads(Path(args.before).read_text()), json.loads(Path(args.after).read_text()),
            json.loads(Path(args.initializer_result).read_text()))
    except (ValueError, KeyError, TypeError) as error:
        result = {"scope": "retention_schema_source_parity", "sourceParityAccepted": False,
                  "code": str(error) if type(error) is ValueError else "artifact_shape_invalid",
                  "deploymentAuthorized": False}
    except OSError:
        result = {"scope": "retention_schema_source_parity", "sourceParityAccepted": False,
                  "code": "artifact_read_failed", "deploymentAuthorized": False}
    print(json.dumps(result, indent=2))
    return 0 if result["sourceParityAccepted"] else 2


if __name__ == "__main__":
    raise SystemExit(main())
