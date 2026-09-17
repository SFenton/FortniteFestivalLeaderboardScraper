#!/usr/bin/env python3

"""Disposable PostgreSQL 17 archive/proof drill on approved FST storage."""

import argparse
import importlib.util
import json
import os
import pathlib
import shutil
import subprocess
import sys
import time
from datetime import datetime, timezone


SCRIPT_DIR = pathlib.Path(__file__).resolve().parent
ARCHIVE_TOOL = SCRIPT_DIR / "postgres-snapshot-generation-archive.py"
EXTRA_VOLUME_DOCKERFILE = (
    SCRIPT_DIR
    / "testdata"
    / "postgres-snapshot-generation-archive-extra-volume.Dockerfile"
)
STORAGE_ROOT = pathlib.Path("/mnt/docker-storage")
DRILL_PARENT = (
    STORAGE_ROOT
    / "Docker/FestivalServiceTracker/fst-data/evidence/"
    "snapshot-generation-archives"
)
SOURCE_SCRATCH_PARENT = (
    STORAGE_ROOT
    / "Docker/FestivalServiceTracker/fst-data/replay/"
    "snapshot-generation-archive-drill"
)
IMAGE = "postgres:17"
INSTRUMENT = "Solo_Guitar"
ROOT_RELATION = "leaderboard_entries_snapshot_solo_guitar"
CHILD_RELATION = "leaderboard_entries_snapshot_solo_guitar_s100"
SNAPSHOT_ID = 100


class DrillError(RuntimeError):
    pass


def load_tool():
    spec = importlib.util.spec_from_file_location(
        "postgres_snapshot_generation_archive_drill_tool",
        ARCHIVE_TOOL,
    )
    module = importlib.util.module_from_spec(spec)
    sys.modules[spec.name] = module
    spec.loader.exec_module(module)
    return module


tool = load_tool()


def run(arguments, *, input_text=None, timeout=1800, check=True):
    completed = subprocess.run(
        [str(value) for value in arguments],
        input=input_text,
        text=True,
        capture_output=True,
        timeout=timeout,
    )
    if check and completed.returncode != 0:
        raise DrillError(
            f"command failed ({completed.returncode}): "
            + " ".join(str(value) for value in arguments)
            + "\n"
            + completed.stderr.strip()
        )
    return completed


def wait_ready(container):
    deadline = time.monotonic() + 120
    successes = 0
    while time.monotonic() < deadline:
        completed = run(
            [
                "docker",
                "exec",
                container,
                "pg_isready",
                "-U",
                "fst",
                "-d",
                "fstservice",
            ],
            timeout=15,
            check=False,
        )
        if completed.returncode == 0:
            successes += 1
            if successes >= 3:
                return
        else:
            successes = 0
        time.sleep(1)
    raise DrillError("synthetic PostgreSQL 17 source did not become ready")


def psql(container, sql):
    return run(
        [
            "docker",
            "exec",
            "-i",
            container,
            "psql",
            "-X",
            "-q",
            "-v",
            "ON_ERROR_STOP=1",
            "-U",
            "fst",
            "-d",
            "fstservice",
            "-At",
        ],
        input_text=sql,
    ).stdout.strip()


def quote(value):
    return "'" + str(value).replace("'", "''") + "'"


def seed_sql():
    return """
        CREATE TABLE scrape_log (
            id BIGINT PRIMARY KEY,
            started_at TIMESTAMPTZ NOT NULL,
            completed_at TIMESTAMPTZ,
            status TEXT NOT NULL,
            failed_at TIMESTAMPTZ);
        INSERT INTO scrape_log VALUES
            (100, now() - interval '2 days',
                  now() - interval '2 days', 'completed', NULL),
            (200, now() - interval '1 day',
                  now() - interval '1 day', 'completed', NULL);

        CREATE TABLE publication_generations (
            publication_id BIGINT PRIMARY KEY,
            scrape_id BIGINT NOT NULL,
            status TEXT NOT NULL);
        INSERT INTO publication_generations VALUES (20, 200, 'current');

        CREATE TABLE scrape_publication_state (
            id BOOLEAN PRIMARY KEY CHECK (id),
            published_scrape_id BIGINT,
            current_publication_id BIGINT,
            previous_publication_id BIGINT,
            working_publication_id BIGINT,
            public_reads_frozen BOOLEAN NOT NULL,
            publication_commit_intent_started_at TIMESTAMPTZ,
            publication_commit_intent_heartbeat_at TIMESTAMPTZ,
            publication_commit_intent_owner TEXT,
            max_score_mutation_gate_token TEXT,
            max_score_mutation_gate_publication_id BIGINT,
            max_score_mutation_gate_backend_pid INTEGER,
            max_score_mutation_gate_backend_start TIMESTAMPTZ,
            max_score_mutation_gate_acquired_at TIMESTAMPTZ,
            improvement_notifications_scrape_id BIGINT,
            improvement_notifications_status TEXT,
            improvement_notifications_completed_at TIMESTAMPTZ,
            improvement_notifications_projection_ready BOOLEAN NOT NULL,
            improvement_notifications_projection_scrape_id BIGINT);
        INSERT INTO scrape_publication_state VALUES (
            TRUE, 200, 20, NULL, NULL, FALSE,
            NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL,
            200, 'completed', now(), TRUE, 200);

        CREATE TABLE snapshot_generation_retention_cycles (
            cycle_id BIGINT PRIMARY KEY,
            trigger_scrape_id BIGINT NOT NULL,
            trigger_publication_id BIGINT NOT NULL,
            safe_point_kind TEXT NOT NULL,
            safe_point_at TIMESTAMPTZ NOT NULL,
            planner_version INTEGER NOT NULL,
            config_version INTEGER NOT NULL,
            report_only BOOLEAN NOT NULL,
            status TEXT NOT NULL,
            oracle_agreement BOOLEAN NOT NULL,
            candidate_identity_hash TEXT NOT NULL,
            observation_hash TEXT NOT NULL,
            planner_child_set JSONB NOT NULL,
            planner_live_set JSONB NOT NULL,
            planner_candidate_set JSONB NOT NULL,
            oracle_child_set JSONB NOT NULL,
            oracle_live_set JSONB NOT NULL,
            oracle_candidate_set JSONB NOT NULL,
            candidate_count INTEGER NOT NULL,
            protected_count INTEGER NOT NULL,
            blocked_count INTEGER NOT NULL,
            candidate_bytes BIGINT NOT NULL,
            global_blockers JSONB NOT NULL,
            anomalies JSONB NOT NULL,
            error_message TEXT,
            created_at TIMESTAMPTZ NOT NULL);

        CREATE TABLE snapshot_generation_retention_observations (
            observation_id BIGINT PRIMARY KEY,
            cycle_id BIGINT NOT NULL,
            report_only BOOLEAN NOT NULL,
            instrument TEXT NOT NULL,
            root_schema TEXT NOT NULL,
            root_relation TEXT NOT NULL,
            snapshot_parent_oid BIGINT NOT NULL,
            root_oid BIGINT NOT NULL,
            root_partition_key TEXT NOT NULL,
            root_partition_bound TEXT NOT NULL,
            root_tablespace_name TEXT NOT NULL,
            root_relation_options JSONB NOT NULL,
            root_index_configuration JSONB NOT NULL,
            child_schema TEXT NOT NULL,
            child_relation TEXT NOT NULL,
            snapshot_id BIGINT NOT NULL,
            child_oid BIGINT NOT NULL,
            child_relfilenode BIGINT NOT NULL,
            partition_bound TEXT NOT NULL,
            tablespace_name TEXT NOT NULL,
            relation_kind TEXT NOT NULL,
            persistence_kind TEXT NOT NULL,
            access_method TEXT NOT NULL,
            relation_options JSONB NOT NULL,
            index_configuration JSONB NOT NULL,
            stable_child_identity_hash TEXT NOT NULL,
            stable_config_schema_hash TEXT NOT NULL,
            row_estimate BIGINT NOT NULL,
            total_bytes BIGINT NOT NULL,
            observation_metrics_hash TEXT NOT NULL,
            planner_live BOOLEAN NOT NULL,
            oracle_live BOOLEAN NOT NULL,
            classification TEXT NOT NULL,
            root_reasons TEXT[] NOT NULL,
            blocker_codes TEXT[] NOT NULL,
            details JSONB NOT NULL,
            created_at TIMESTAMPTZ NOT NULL);

        CREATE TABLE snapshot_generation_retention_holds (
            instrument TEXT NOT NULL,
            snapshot_id BIGINT NOT NULL,
            released_at TIMESTAMPTZ);

        CREATE TABLE scrape_writer_failures (
            scrape_id BIGINT NOT NULL,
            instrument TEXT NOT NULL,
            replayed_at TIMESTAMPTZ);

        CREATE TABLE snapshot_generation_retention_evidence (
            evidence_id BIGINT PRIMARY KEY,
            cycle_id BIGINT NOT NULL,
            observation_id BIGINT,
            sequence INTEGER NOT NULL,
            phase TEXT NOT NULL,
            kind TEXT NOT NULL,
            payload JSONB NOT NULL,
            previous_hash TEXT,
            current_hash TEXT NOT NULL,
            created_at TIMESTAMPTZ NOT NULL);

        CREATE TABLE leaderboard_entries_snapshot (
            snapshot_id BIGINT NOT NULL,
            song_id TEXT NOT NULL,
            instrument TEXT NOT NULL,
            account_id TEXT NOT NULL,
            score INTEGER NOT NULL,
            accuracy INTEGER,
            is_full_combo BOOLEAN,
            stars INTEGER,
            season INTEGER,
            percentile REAL,
            rank INTEGER DEFAULT 0,
            source TEXT NOT NULL DEFAULT 'scrape',
            difficulty INTEGER DEFAULT -1,
            api_rank INTEGER,
            end_time TEXT,
            band_members_json JSONB,
            band_score INTEGER,
            base_score INTEGER,
            instrument_bonus INTEGER,
            overdrive_bonus INTEGER,
            instrument_combo TEXT,
            first_seen_at TIMESTAMPTZ NOT NULL,
            last_updated_at TIMESTAMPTZ NOT NULL,
            PRIMARY KEY (
                snapshot_id, song_id, instrument, account_id)
        ) PARTITION BY LIST (instrument);

        CREATE INDEX ix_les_snapshot_song_score
            ON leaderboard_entries_snapshot
            (snapshot_id, song_id, instrument, score DESC);

        CREATE TABLE leaderboard_entries_snapshot_solo_guitar
            PARTITION OF leaderboard_entries_snapshot
            FOR VALUES IN ('Solo_Guitar')
            PARTITION BY LIST (snapshot_id);

        CREATE TABLE leaderboard_entries_snapshot_solo_guitar_s100
            PARTITION OF leaderboard_entries_snapshot_solo_guitar
            FOR VALUES IN (100);

        INSERT INTO leaderboard_entries_snapshot VALUES
            (100, 'song-a', 'Solo_Guitar', 'account-a', 1000,
             100, TRUE, 5, 1, 0.5, 1, 'scrape', -1, 1, NULL, NULL,
             NULL, NULL, NULL, NULL, NULL, now(), now()),
            (100, 'song-a', 'Solo_Guitar', 'account-b', 900,
             99, FALSE, 4, 1, 0.4, 2, 'scrape', -1, 2, NULL, NULL,
             NULL, NULL, NULL, NULL, NULL, now(), now()),
            (100, 'song-b', 'Solo_Guitar', 'account-c', 800,
             98, FALSE, 4, 1, 0.3, 3, 'scrape', -1, 3, NULL, NULL,
             NULL, NULL, NULL, NULL, NULL, now(), now());
    """


def add_planner_evidence(container):
    catalogs = tool.capture_catalog(
        container,
        "fst",
        "fstservice",
        "public",
        (
            tool.ROOT_RELATION,
            ROOT_RELATION,
            CHILD_RELATION,
        ),
    )
    observation = tool.build_observation_snapshot(
        INSTRUMENT,
        SNAPSHOT_ID,
        catalogs,
    )
    child = catalogs[-1]
    db_observation = {
        "observation_id": 1,
        "cycle_id": 8,
        "report_only": True,
        "instrument": INSTRUMENT,
        "root_schema": "public",
        "root_relation": ROOT_RELATION,
        "snapshot_parent_oid": observation["snapshotParentOid"],
        "root_oid": observation["rootOid"],
        "root_partition_key": observation["rootPartitionKey"],
        "root_partition_bound": observation["rootPartitionBound"],
        "root_tablespace_name": observation["rootTablespaceName"],
        "root_relation_options": observation["rootRelationOptions"],
        "root_index_configuration": observation["rootIndexes"],
        "child_schema": "public",
        "child_relation": CHILD_RELATION,
        "snapshot_id": SNAPSHOT_ID,
        "child_oid": observation["childOid"],
        "child_relfilenode": observation["childRelfilenode"],
        "partition_bound": observation["partitionBound"],
        "tablespace_name": observation["tablespaceName"],
        "relation_kind": observation["relationKind"],
        "persistence_kind": observation["persistenceKind"],
        "access_method": observation["accessMethod"],
        "relation_options": observation["relationOptions"],
        "index_configuration": observation["indexes"],
        "stable_child_identity_hash":
            observation["stableChildIdentityHash"],
        "stable_config_schema_hash":
            observation["stableConfigSchemaHash"],
        "row_estimate": 3,
        "total_bytes": int(child["totalBytes"]),
        "planner_live": False,
        "oracle_live": False,
        "classification": "candidate",
        "root_reasons": [],
        "blocker_codes": [],
    }
    db_observation["observation_metrics_hash"] = tool.stable_hash(
        {
            "stableChildIdentityHash":
                db_observation["stable_child_identity_hash"],
            "rowEstimate": db_observation["row_estimate"],
            "totalBytes": db_observation["total_bytes"],
        }
    )
    physical_key = tool.observation_physical_key(db_observation)
    db_observation["details"] = {
        "childPhysicalKey": physical_key,
        "rootReasons": [],
        "blockers": [],
    }
    cycle = {
        "cycle_id": 8,
        "trigger_scrape_id": 200,
        "trigger_publication_id": 20,
        "safe_point_kind": "terminal_worker_post_publication",
        "planner_version": tool.PLANNER_VERSION,
        "config_version": tool.CONFIG_VERSION,
        "report_only": True,
        "status": "observed",
        "oracle_agreement": True,
        "candidate_count": 1,
        "protected_count": 0,
        "blocked_count": 0,
        "candidate_bytes": int(child["totalBytes"]),
        "global_blockers": [],
        "anomalies": [],
        "error_message": None,
    }
    def publication_validation(slot, publication_id, scrape_id, hash_value):
        comparison = {
            "slot": slot,
            "publicationId": publication_id,
            "scrapeId": scrape_id,
            "expectedRowCount": 12,
            "bindingRowCount": 12,
            "actualRowCount": 12,
            "bindingKeyHash": hash_value,
            "actualKeyHash": hash_value,
            "invalidRowCount": 0,
            "duplicateKeyCount": 0,
            "bindingIdentityValid": True,
            "isValid": True,
        }
        return {
            **comparison,
            "comparisonKey": tool.comparison_key_text(comparison),
        }

    publication_validations = [
        publication_validation("current", 20, 200, "a" * 64),
        publication_validation("previous", 19, 199, "b" * 64),
    ]

    def numeric_validation(instrument, snapshot_id, relation, index_key):
        comparison = {
            "instrument": instrument,
            "snapshotId": snapshot_id,
            "childRelation": relation,
            "indexKeys": [index_key],
            "expectedParentIndexCount": 1,
            "missingParentIndexCount": 0,
            "duplicateParentIndexCount": 0,
            "detachedIndexCount": 0,
            "invalidIndexCount": 0,
            "unreadyIndexCount": 0,
            "attributeMismatchIndexCount": 0,
            "isValid": True,
        }
        return {
            **comparison,
            "comparisonKey": tool.comparison_key_text(comparison),
        }

    def topology_validation(instrument, numeric):
        comparison = {
            "instrument": instrument,
            "topIndexKeys": [f'top|{instrument}|"primary"'],
            "rootIndexKeys": [f'root|{instrument}|"primary"'],
            "defaultIndexKeys": [f'default|{instrument}|"primary"'],
            "missingRequiredTopIndexNames": [],
            "invalidTopIndexCount": 0,
            "unreadyTopIndexCount": 0,
            "attachedTopIndexCount": 0,
            "missingRootIndexCount": 0,
            "duplicateRootIndexCount": 0,
            "detachedRootIndexCount": 0,
            "invalidRootIndexCount": 0,
            "unreadyRootIndexCount": 0,
            "missingDefaultIndexCount": 0,
            "duplicateDefaultIndexCount": 0,
            "detachedDefaultIndexCount": 0,
            "invalidDefaultIndexCount": 0,
            "unreadyDefaultIndexCount": 0,
            "numericChildIndexValidations": [numeric["comparisonKey"]],
            "isValid": True,
        }
        return {
            "instrument": instrument,
            "topIndexKeys": comparison["topIndexKeys"],
            "rootIndexKeys": comparison["rootIndexKeys"],
            "defaultIndexKeys": comparison["defaultIndexKeys"],
            "missingRequiredTopIndexNames": [],
            "invalidTopIndexCount": 0,
            "unreadyTopIndexCount": 0,
            "attachedTopIndexCount": 0,
            "missingRootIndexCount": 0,
            "duplicateRootIndexCount": 0,
            "detachedRootIndexCount": 0,
            "invalidRootIndexCount": 0,
            "unreadyRootIndexCount": 0,
            "missingDefaultIndexCount": 0,
            "duplicateDefaultIndexCount": 0,
            "detachedDefaultIndexCount": 0,
            "invalidDefaultIndexCount": 0,
            "unreadyDefaultIndexCount": 0,
            "numericChildIndexValidations": [numeric],
            "effectiveNumericChildIndexValidations": [numeric],
            "isValid": True,
            "comparisonKey": tool.comparison_key_text(comparison),
        }

    numeric_guitar = numeric_validation(
        INSTRUMENT,
        SNAPSHOT_ID,
        CHILD_RELATION,
        '700|1700|1800|"primary"',
    )
    numeric_bass = numeric_validation(
        "Solo_Bass",
        101,
        "leaderboard_entries_snapshot_solo_bass_s101",
        '710|1710|1810|"primary"',
    )
    topology_validations = [
        topology_validation(INSTRUMENT, numeric_guitar),
        topology_validation("Solo_Bass", numeric_bass),
    ]
    summary_validations = {
        "plannerPublicationSourceValidations": publication_validations,
        "oraclePublicationSourceValidations":
            list(reversed(publication_validations)),
        "plannerIndexTopologyValidations": topology_validations,
        "oracleIndexTopologyValidations":
            list(reversed(topology_validations)),
    }
    material = tool.authentic_cycle_material(
        cycle,
        [db_observation],
        summary_validations,
    )
    cycle["candidate_identity_hash"] = material["candidateIdentityHash"]
    cycle["observation_hash"] = material["observationHash"]
    for key, value in (
        ("planner_child_set", material["children"]),
        ("planner_live_set", material["live"]),
        ("planner_candidate_set", material["candidates"]),
        ("oracle_child_set", material["children"]),
        ("oracle_live_set", material["live"]),
        ("oracle_candidate_set", material["candidates"]),
    ):
        cycle[key] = value
    material = tool.authentic_cycle_material(
        cycle,
        [db_observation],
        summary_validations,
    )
    summary_evidence = {
        "observation_id": None,
        "sequence": 1,
        "phase": "observation",
        "kind": "summary",
        "payload": material["summaryPayload"],
        "previous_hash": None,
    }
    summary_evidence["current_hash"] = tool.evidence_hash(
        cycle["cycle_id"],
        summary_evidence,
    )
    evaluation = material["evaluations"][0]
    child_evidence = {
        "observation_id": 1,
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
        "previous_hash": summary_evidence["current_hash"],
    }
    child_evidence["current_hash"] = tool.evidence_hash(
        cycle["cycle_id"],
        child_evidence,
    )
    candidate_set = json.dumps(material["candidates"], separators=(",", ":"))
    insert = f"""
        INSERT INTO snapshot_generation_retention_cycles VALUES (
            8, 200, 20, 'terminal_worker_post_publication', now(),
            {cycle['planner_version']}, {cycle['config_version']},
            TRUE, 'observed', TRUE,
            {quote(cycle['candidate_identity_hash'])},
            {quote(cycle['observation_hash'])},
            {quote(candidate_set)}::jsonb,
            '[]'::jsonb,
            {quote(candidate_set)}::jsonb,
            {quote(candidate_set)}::jsonb,
            '[]'::jsonb,
            {quote(candidate_set)}::jsonb,
            1, 0, 0, {int(child['totalBytes'])},
            '[]'::jsonb, '[]'::jsonb, NULL, now());

        INSERT INTO snapshot_generation_retention_observations VALUES (
            1, 8, TRUE,
            {quote(INSTRUMENT)},
            'public',
            {quote(ROOT_RELATION)},
            {observation['snapshotParentOid']},
            {observation['rootOid']},
            {quote(observation['rootPartitionKey'])},
            {quote(observation['rootPartitionBound'])},
            {quote(observation['rootTablespaceName'])},
            {quote(json.dumps(observation['rootRelationOptions']))}::jsonb,
            {quote(json.dumps(observation['rootIndexes']))}::jsonb,
            'public',
            {quote(CHILD_RELATION)},
            {SNAPSHOT_ID},
            {observation['childOid']},
            {observation['childRelfilenode']},
            {quote(observation['partitionBound'])},
            {quote(observation['tablespaceName'])},
            {quote(observation['relationKind'])},
            {quote(observation['persistenceKind'])},
            {quote(observation['accessMethod'])},
            {quote(json.dumps(observation['relationOptions']))}::jsonb,
            {quote(json.dumps(observation['indexes']))}::jsonb,
            {quote(observation['stableChildIdentityHash'])},
            {quote(observation['stableConfigSchemaHash'])},
            3,
            {int(child['totalBytes'])},
            {quote(db_observation['observation_metrics_hash'])},
            FALSE, FALSE, 'candidate',
            ARRAY[]::TEXT[], ARRAY[]::TEXT[],
            {quote(json.dumps(db_observation['details']))}::jsonb, now());

        INSERT INTO snapshot_generation_retention_evidence VALUES
            (
                1, 8, NULL, 1, 'observation', 'summary',
                {quote(json.dumps(summary_evidence['payload']))}::jsonb,
                NULL,
                {quote(summary_evidence['current_hash'])},
                now()),
            (
                2, 8, 1, 2, 'observation', 'child',
                {quote(json.dumps(child_evidence['payload']))}::jsonb,
                {quote(child_evidence['previous_hash'])},
                {quote(child_evidence['current_hash'])},
                now());
    """
    psql(container, insert)
    return {
        "candidateIdentityHash": cycle["candidate_identity_hash"],
        "observationHash": cycle["observation_hash"],
        "evidenceFinalHash": child_evidence["current_hash"],
    }


def clean_owned_directory(path):
    path = pathlib.Path(path)
    approved = (
        DRILL_PARENT.resolve(),
        SOURCE_SCRATCH_PARENT.resolve(),
    )
    if (
        not path.is_absolute()
        or not any(tool.is_beneath(path.resolve(), root) for root in approved)
        or path.is_symlink()
    ):
        raise DrillError(f"refusing unsafe synthetic cleanup path: {path}")
    run(
        [
            "docker",
            "run",
            "--rm",
            "--network",
            "none",
            "--user",
            "0:0",
            "--mount",
            tool.docker_bind_mount(path, "/owned", readonly=False),
            IMAGE,
            "sh",
            "-c",
            (
                "find /owned -mindepth 1 -maxdepth 1 "
                "-exec rm -rf -- {} +; "
                f"chown {os.getuid()}:{os.getgid()} /owned; "
                "chmod 700 /owned"
            ),
        ],
        timeout=1800,
    )


def main(argv=None):
    parser = argparse.ArgumentParser()
    parser.add_argument("--keep-artifacts", action="store_true")
    args = parser.parse_args(argv)
    if not STORAGE_ROOT.is_dir() or STORAGE_ROOT.is_symlink():
        raise DrillError("approved FST storage root is unavailable")
    DRILL_PARENT.mkdir(parents=True, exist_ok=True)
    SOURCE_SCRATCH_PARENT.mkdir(parents=True, exist_ok=True)
    operation_lock_created = False
    if not tool.OPERATION_LOCK_PATH.exists():
        descriptor = os.open(
            tool.OPERATION_LOCK_PATH,
            os.O_WRONLY | os.O_CREAT | os.O_EXCL | os.O_CLOEXEC,
            0o600,
        )
        os.close(descriptor)
        operation_lock_created = True
    elif (
        tool.OPERATION_LOCK_PATH.is_symlink()
        or not tool.OPERATION_LOCK_PATH.is_file()
    ):
        raise DrillError("pre-provisioned operation lock is unsafe")
    run_id = (
        datetime.now(timezone.utc).strftime("%Y%m%dT%H%M%SZ")
        + f"-{os.getpid()}"
    )
    root = DRILL_PARENT / f"snapshot-generation-archive-synthetic-{run_id}"
    root.mkdir(mode=0o700)
    marker = root / ".synthetic-archive-drill.json"
    marker.write_text(
        json.dumps({"tool": str(ARCHIVE_TOOL), "runId": run_id}) + "\n",
        encoding="utf-8",
    )
    pgdata = SOURCE_SCRATCH_PARENT / f"source-pgdata-{run_id}"
    pgdata.mkdir(mode=0o700)
    package = root / "package"
    container = f"fst-snapshot-archive-source-{os.getpid()}"
    extra_volume_image = (
        f"fst-snapshot-archive-extra-volume:{os.getpid()}"
    )
    extra_volume_image_built = False
    try:
        run(["docker", "image", "inspect", IMAGE])
        run(
            [
                "docker",
                "run",
                "-d",
                "--name",
                container,
                "--network",
                "none",
                "--label",
                "fst.synthetic=snapshot-generation-archive",
                "-e",
                "POSTGRES_HOST_AUTH_METHOD=trust",
                "-e",
                "POSTGRES_USER=fst",
                "-e",
                "POSTGRES_DB=fstservice",
                "--mount",
                tool.docker_bind_mount(
                    pgdata,
                    "/var/lib/postgresql/data",
                    readonly=False,
                ),
                IMAGE,
            ]
        )
        wait_ready(container)
        psql(container, seed_sql())
        planner_evidence = add_planner_evidence(container)
        before_catalog = tool.capture_catalog(
            container,
            "fst",
            "fstservice",
            "public",
            (
                tool.ROOT_RELATION,
                ROOT_RELATION,
                CHILD_RELATION,
            ),
        )
        before_logical = tool.logical_catalog(before_catalog)
        before_fingerprint = tool.stream_fingerprint(
            container,
            "fst",
            "fstservice",
            "public",
            CHILD_RELATION,
            timeout_seconds=600,
        )
        rejected_package = root / "rejected-placeholder-package"
        psql(
            container,
            """
                UPDATE snapshot_generation_retention_cycles
                SET candidate_identity_hash = repeat('a', 64)
                WHERE cycle_id = 8;
            """,
        )
        rejected = run(
            [
                sys.executable,
                ARCHIVE_TOOL,
                "archive",
                "--output",
                rejected_package,
                "--source-container",
                container,
                "--pg-user",
                "fst",
                "--pg-database",
                "fstservice",
            ],
            timeout=7200,
            check=False,
        )
        if (
            rejected.returncode == 0
            or not (rejected_package / "rejected.json").is_file()
        ):
            raise DrillError("placeholder planner hash was not rejected")
        psql(
            container,
            f"""
                UPDATE snapshot_generation_retention_cycles
                SET candidate_identity_hash =
                    {quote(planner_evidence['candidateIdentityHash'])}
                WHERE cycle_id = 8;
            """,
        )
        archive_result = run(
            [
                sys.executable,
                ARCHIVE_TOOL,
                "archive",
                "--output",
                package,
                "--source-container",
                container,
                "--pg-user",
                "fst",
                "--pg-database",
                "fstservice",
            ],
            timeout=7200,
        )
        archive_manifest = json.loads(archive_result.stdout)
        if archive_manifest.get("status") != "accepted":
            raise DrillError("synthetic archive was not accepted")
        run(
            [
                "docker",
                "build",
                "--quiet",
                "-f",
                EXTRA_VOLUME_DOCKERFILE,
                "-t",
                extra_volume_image,
                EXTRA_VOLUME_DOCKERFILE.parent,
            ],
            timeout=600,
        )
        extra_volume_image_built = True
        extra_volume_proof_id = f"extra-volume-{os.getpid()}"
        extra_volume_result = run(
            [
                sys.executable,
                ARCHIVE_TOOL,
                "prove",
                "--package",
                package,
                "--proof-id",
                extra_volume_proof_id,
                "--postgres-image",
                extra_volume_image,
            ],
            timeout=7200,
            check=False,
        )
        extra_volume_proof = (
            package / "proofs" / extra_volume_proof_id
        )
        if (
            extra_volume_result.returncode == 0
            or not (extra_volume_proof / "proof-rejected.json").is_file()
        ):
            raise DrillError("image-declared extra volume was not rejected")
        extra_volume_cleanup = json.loads(
            (extra_volume_proof / "cleanup.json").read_text(
                encoding="utf-8"
            )
        )
        if (
            not extra_volume_cleanup["ownedVolumesRemoved"]
            or not extra_volume_cleanup["unexpectedVolumeNames"]
        ):
            raise DrillError("unexpected proof volume cleanup was not proven")
        for volume in extra_volume_cleanup["unexpectedVolumeNames"]:
            if run(
                ["docker", "volume", "inspect", volume],
                check=False,
            ).returncode == 0:
                raise DrillError("unexpected proof volume remains")
        broken_package = root / "broken-proof-package"
        shutil.copytree(package, broken_package)
        (broken_package / "archive.custom").write_bytes(
            b"synthetic-invalid-custom-archive\n"
        )
        broken_manifest = json.loads(
            (broken_package / "manifest.json").read_text(encoding="utf-8")
        )
        broken_manifest["archive"]["bytes"] = (
            broken_package / "archive.custom"
        ).stat().st_size
        broken_manifest["archive"]["sha256"] = tool.sha256_path(
            broken_package / "archive.custom"
        )
        tool.write_json(
            broken_package / "manifest.json",
            broken_manifest,
        )
        tool.write_checksums(broken_package, tool.PACKAGE_FILES)
        broken_proof_id = f"broken-{os.getpid()}"
        broken = run(
            [
                sys.executable,
                ARCHIVE_TOOL,
                "prove",
                "--package",
                broken_package,
                "--proof-id",
                broken_proof_id,
                "--postgres-image",
                IMAGE,
            ],
            timeout=7200,
            check=False,
        )
        broken_proof = (
            broken_package / "proofs" / broken_proof_id
        )
        if (
            broken.returncode == 0
            or not (broken_proof / "proof-rejected.json").is_file()
            or not (broken_proof / "cleanup.json").is_file()
        ):
            raise DrillError("failed restore did not retain rejection evidence")
        broken_cleanup = json.loads(
            (broken_proof / "cleanup.json").read_text(encoding="utf-8")
        )
        if not all(
            broken_cleanup[key]
            for key in (
                "containerRemoved",
                "containerAbsenceProven",
                "ownedVolumesRemoved",
                "scratchRemoved",
                "pgdataRemoved",
            )
        ):
            raise DrillError("failed restore did not clean isolated resources")
        proof_result = run(
            [
                sys.executable,
                ARCHIVE_TOOL,
                "prove",
                "--package",
                package,
                "--proof-id",
                f"synthetic-{os.getpid()}",
                "--postgres-image",
                IMAGE,
                "--keep-proof-outputs",
            ],
            timeout=7200,
        )
        proof_manifest = json.loads(proof_result.stdout)
        after_catalog = tool.capture_catalog(
            container,
            "fst",
            "fstservice",
            "public",
            (
                tool.ROOT_RELATION,
                ROOT_RELATION,
                CHILD_RELATION,
            ),
        )
        after_logical = tool.logical_catalog(after_catalog)
        after_fingerprint = tool.stream_fingerprint(
            container,
            "fst",
            "fstservice",
            "public",
            CHILD_RELATION,
            timeout_seconds=600,
        )
        if (
            before_fingerprint != after_fingerprint
            or before_logical != after_logical
        ):
            raise DrillError("synthetic source fingerprint or catalog changed")
        if proof_manifest.get("status") != "accepted":
            raise DrillError("synthetic restore proof was not accepted")
        if not all(
            proof_manifest["cleanup"][key]
            for key in (
                "containerRemoved",
                "containerAbsenceProven",
                "ownedVolumesRemoved",
                "scratchRemoved",
                "pgdataRemoved",
            )
        ):
            raise DrillError("synthetic proof cleanup is incomplete")
        print(
            json.dumps(
                {
                    "status": "accepted",
                    "archive": str(package),
                    "archiveSha256": archive_manifest["archive"]["sha256"],
                    "proofId": proof_manifest["proofId"],
                    "rowFingerprint": after_fingerprint,
                    "logicalCatalogSha256":
                        tool.stable_hash(after_logical),
                    "sourceUnchanged": True,
                    "placeholderHashRejected": True,
                    "failedRestoreCleanupProven": all(
                        broken_cleanup[key]
                        for key in (
                            "containerRemoved",
                            "containerAbsenceProven",
                            "ownedVolumesRemoved",
                            "scratchRemoved",
                            "pgdataRemoved",
                        )
                    ),
                    "extraVolumeRejected": True,
                    "extraVolumeNamesRemoved":
                        extra_volume_cleanup["unexpectedVolumeNames"],
                    "networkMode": proof_manifest["networkMode"],
                    "cleanup": proof_manifest["cleanup"],
                },
                sort_keys=True,
            )
        )
    finally:
        inspected = run(
            ["docker", "inspect", container],
            timeout=30,
            check=False,
        )
        if inspected.returncode == 0:
            details = json.loads(inspected.stdout)[0]
            labels = details.get("Config", {}).get("Labels") or {}
            if labels.get("fst.synthetic") != "snapshot-generation-archive":
                raise DrillError(
                    "refusing to remove an unowned synthetic source container"
                )
            for _ in range(3):
                run(
                    ["docker", "rm", "-f", container],
                    timeout=120,
                    check=False,
                )
                if run(
                    ["docker", "inspect", container],
                    check=False,
                ).returncode != 0:
                    break
                time.sleep(1)
            if run(
                ["docker", "inspect", container],
                check=False,
            ).returncode == 0:
                raise DrillError(
                    "synthetic source container absence was not proven"
                )
        if pgdata.exists():
            clean_owned_directory(pgdata)
            pgdata.rmdir()
        if extra_volume_image_built:
            run(
                ["docker", "image", "rm", "-f", extra_volume_image],
                timeout=120,
                check=False,
            )
        if operation_lock_created:
            tool.OPERATION_LOCK_PATH.unlink()
        if not args.keep_artifacts and root.exists():
            if not marker.is_file():
                raise DrillError("synthetic drill marker is missing")
            clean_owned_directory(root)
            root.rmdir()
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
