#!/usr/bin/env python3
"""Capture the fixed pre/post snapshot-generation drop health window."""

from __future__ import annotations

import argparse
import hashlib
import importlib.util
import json
import os
import pathlib
import sys
import time
import urllib.request
from datetime import datetime, timezone


REQUIRED_ROOT = pathlib.Path(
    "/mnt/docker-storage/Docker/FestivalServiceTracker/fst-data/evidence"
)
SAMPLE_COUNT = 60
SAMPLE_INTERVAL_SECONDS = 30


class HealthError(RuntimeError):
    pass


def load_archive_module():
    path = pathlib.Path(__file__).with_name(
        "postgres-snapshot-generation-archive.py"
    )
    specification = importlib.util.spec_from_file_location(
        "fst_snapshot_generation_archive_health",
        path,
    )
    if specification is None or specification.loader is None:
        raise HealthError("archive helper module could not be loaded")
    module = importlib.util.module_from_spec(specification)
    specification.loader.exec_module(module)
    return module


ARCHIVE = load_archive_module()


def utc_now():
    return datetime.now(timezone.utc).replace(
        microsecond=0
    ).isoformat()


def read_url(url):
    request = urllib.request.Request(
        url,
        headers={"Accept": "application/json"},
    )
    with urllib.request.urlopen(request, timeout=15) as response:
        return response.status, response.read()


def read_json_url(url):
    status, body = read_url(url)
    if status != 200:
        raise HealthError(
            f"health request returned HTTP {status}: {url}"
        )
    return json.loads(body)


def database_sample(container, user, database):
    return ARCHIVE.psql_json(
        container,
        user,
        database,
        """
        SELECT json_build_object(
            'runningScrapeCount', (
                SELECT COUNT(*)::INTEGER
                FROM scrape_log
                WHERE status = 'running'),
            'lockWaiterCount', (
                SELECT COUNT(*)::INTEGER
                FROM pg_stat_activity activity
                WHERE activity.datname = current_database()
                  AND activity.wait_event_type = 'Lock'
                  AND activity.pid <> pg_backend_pid()),
            'publicReadsFrozen', state.public_reads_frozen,
            'currentPublicationId',
                state.current_publication_id::BIGINT,
            'publishedScrapeId',
                state.published_scrape_id::BIGINT,
            'workingPublicationId',
                state.working_publication_id::BIGINT,
            'publicationCommitIntentActive',
                state.publication_commit_intent_started_at
                    IS NOT NULL,
            'maxScoreMutationGateActive',
                state.max_score_mutation_gate_token IS NOT NULL,
            'notificationsComplete', (
                state.improvement_notifications_scrape_id =
                    state.published_scrape_id
                AND state.improvement_notifications_status =
                    'completed'
                AND state.improvement_notifications_completed_at
                    IS NOT NULL
                AND state.improvement_notifications_projection_ready
                AND state.improvement_notifications_projection_scrape_id =
                    state.published_scrape_id),
            'workerOffline', EXISTS (
                SELECT 1
                FROM service_worker_status worker
                WHERE worker.worker_key = 'scraper'
                  AND worker.status = 'offline'
                  AND worker.current_operation_json IS NULL))
        FROM scrape_publication_state state
        WHERE state.id = TRUE
        """,
    )


def capture(args):
    output = pathlib.Path(args.output).resolve(strict=False)
    root = REQUIRED_ROOT.resolve(strict=True)
    parent = output.parent.resolve(strict=True)
    if root not in parent.parents and parent != root:
        raise HealthError(
            f"health output must remain below {root}"
        )
    if output.exists() or output.is_symlink():
        raise HealthError(f"health output already exists: {output}")
    count = SAMPLE_COUNT
    interval = SAMPLE_INTERVAL_SECONDS
    started = datetime.now(timezone.utc).replace(
        microsecond=0)
    samples = []
    expected_publication = None
    expected_scrape = None
    for index in range(count):
        captured = utc_now()
        ready_status, _ = read_url(
            f"{args.api_base}/readyz")
        service = read_json_url(
            f"{args.api_base}/api/service-info"
        )
        database = database_sample(
            args.source_container,
            args.pg_user,
            args.pg_database,
        )
        publication_id = int(
            database.get("currentPublicationId") or 0
        )
        scrape_id = int(
            database.get("publishedScrapeId") or 0
        )
        if expected_publication is None:
            expected_publication = publication_id
            expected_scrape = scrape_id
        healthy = (
            publication_id == expected_publication
            and scrape_id == expected_scrape
            and publication_id > 0
            and scrape_id > 0
            and database.get("publicReadsFrozen") is False
            and database.get("workingPublicationId") is None
            and database.get("publicationCommitIntentActive") is False
            and database.get("maxScoreMutationGateActive") is False
            and database.get("notificationsComplete") is True
            and database.get("workerOffline") is True
            and int(database.get("runningScrapeCount", -1)) == 0
            and int(database.get("lockWaiterCount", -1)) == 0
            and ready_status == 200
            and service.get("currentUpdate", {}).get("status") == "idle"
            and int(service.get("publishedScrapeId") or 0)
                == scrape_id
        )
        if not healthy:
            raise HealthError(
                f"health sample {index + 1} failed"
            )
        samples.append(
            {
                "capturedAtUtc": captured,
                "publicationId": publication_id,
                "publishedScrapeId": scrape_id,
                "ready": True,
                "apiHealthy": True,
                "publicReadsFrozen": False,
                "runningScrapeCount": 0,
                "lockWaiterCount": 0,
            }
        )
        time.sleep(interval)
    completed = datetime.now(timezone.utc)
    evidence = {
        "schemaVersion": 1,
        "toolId": "fst.snapshot-generation-drop-health.v1",
        "startedAtUtc": started.isoformat(),
        "completedAtUtc": completed.replace(
            microsecond=0).isoformat(),
        "sampleIntervalSeconds": SAMPLE_INTERVAL_SECONDS,
        "successfulSampleCount": SAMPLE_COUNT,
        "publicationId": expected_publication,
        "publishedScrapeId": expected_scrape,
        "allHealthy": True,
        "samples": samples,
    }
    evidence["evidenceSha256"] = hashlib.sha256(
        ARCHIVE.dotnet_canonical_json_bytes(evidence)
    ).hexdigest()
    ARCHIVE.write_json(output, evidence)
    return evidence


def main(argv=None):
    parser = argparse.ArgumentParser()
    parser.add_argument("--output", required=True)
    parser.add_argument(
        "--api-base",
        default="http://127.0.0.1:3001",
    )
    parser.add_argument(
        "--source-container",
        default="fst-postgres",
    )
    parser.add_argument("--pg-user", default="fst")
    parser.add_argument("--pg-database", default="fstservice")
    args = parser.parse_args(argv)
    try:
        result = capture(args)
    except (
        HealthError,
        ARCHIVE.ArchiveError,
        OSError,
        ValueError,
    ) as error:
        print(f"ERROR: {error}", file=sys.stderr)
        return 1
    print(json.dumps(result, sort_keys=True))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
