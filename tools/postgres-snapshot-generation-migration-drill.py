#!/usr/bin/env python3

"""Isolated PostgreSQL 17 lifecycle drill for snapshot partition migration."""

import argparse
import contextlib
import hashlib
import json
import os
import pathlib
import shutil
import subprocess
import sys
import time
from datetime import datetime, timezone


SCRIPT_DIR = pathlib.Path(__file__).resolve().parent
REPO_ROOT = SCRIPT_DIR.parent
MIGRATION = SCRIPT_DIR / "postgres-snapshot-generation-migration.py"
IMAGE = "postgres:17"
TARGET_KEY = "solo-guitar"
TARGET_PARTITION = "leaderboard_entries_snapshot_solo_guitar"
INSTRUMENT = "Solo_Guitar"
EXECUTE_STAGES = {
    "archive",
    "restore",
    "build",
    "swap",
    "drop",
    "rollback",
}


class DrillError(RuntimeError):
    pass


def run(arguments, *, input_text=None, timeout=3600, check=True):
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


def canonical_json_bytes(value):
    return (
        json.dumps(value, indent=2, sort_keys=True, ensure_ascii=True)
        + "\n"
    ).encode("utf-8")


def sha256_path(path):
    digest = hashlib.sha256()
    with pathlib.Path(path).open("rb") as handle:
        for chunk in iter(lambda: handle.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def write_json(path, value):
    path = pathlib.Path(path)
    path.parent.mkdir(parents=True, exist_ok=True, mode=0o700)
    temporary = path.with_name(
        f".{path.name}.partial-{os.getpid()}-{time.time_ns()}"
    )
    descriptor = os.open(
        temporary,
        os.O_WRONLY | os.O_CREAT | os.O_EXCL,
        0o600,
    )
    with os.fdopen(descriptor, "wb") as handle:
        handle.write(canonical_json_bytes(value))
        handle.flush()
        os.fsync(handle.fileno())
    os.replace(temporary, path)
    directory = os.open(
        path.parent,
        os.O_RDONLY | getattr(os, "O_DIRECTORY", 0),
    )
    try:
        os.fsync(directory)
    finally:
        os.close(directory)


def wait_ready(container):
    deadline = time.monotonic() + 120
    successes = 0
    while time.monotonic() < deadline:
        result = run(
            [
                "docker",
                "exec",
                container,
                "psql",
                "-X",
                "-U",
                "fst",
                "-d",
                "fstservice",
                "-At",
                "-c",
                "SELECT current_setting('server_version_num')",
            ],
            timeout=15,
            check=False,
        )
        if (
            result.returncode == 0
            and result.stdout.strip().isdigit()
            and int(result.stdout.strip()) // 10000 == 17
        ):
            successes += 1
            if successes >= 3:
                return
        else:
            successes = 0
        time.sleep(1)
    raise DrillError("isolated PostgreSQL 17 did not become ready")


def psql(container, sql, timeout=3600):
    return run(
        [
            "docker",
            "exec",
            container,
            "psql",
            "-X",
            "-v",
            "ON_ERROR_STOP=1",
            "-U",
            "fst",
            "-d",
            "fstservice",
            "-At",
            "-c",
            sql,
        ],
        timeout=timeout,
    ).stdout.strip()


def seed_sql(purge_rows, retained_rows):
    return f"""
        CREATE TABLE scrape_log (
            id bigint PRIMARY KEY,
            started_at timestamptz NOT NULL,
            completed_at timestamptz,
            status text NOT NULL);
        INSERT INTO scrape_log VALUES
            (1301, now() - interval '3 days',
                   now() - interval '3 days', 'completed'),
            (1302, now() - interval '2 days',
                   now() - interval '2 days', 'completed'),
            (1303, now() - interval '1 day',
                   now() - interval '1 day', 'completed');

        CREATE TABLE publication_generations (
            publication_id bigint PRIMARY KEY,
            scrape_id bigint UNIQUE NOT NULL,
            status text NOT NULL);
        INSERT INTO publication_generations VALUES
            (80, 1302, 'previous'),
            (89, 1303, 'current');

        CREATE TABLE scrape_publication_state (
            id boolean PRIMARY KEY CHECK (id),
            published_scrape_id bigint,
            current_publication_id bigint,
            previous_publication_id bigint,
            working_publication_id bigint,
            public_reads_frozen boolean NOT NULL,
            public_reads_frozen_reason text,
            updated_at timestamptz NOT NULL);
        INSERT INTO scrape_publication_state VALUES
            (true, 1303, 89, 80, NULL, false, NULL, now());

        CREATE TABLE service_worker_status (
            worker_key text PRIMARY KEY,
            status text NOT NULL,
            last_heartbeat_at timestamptz);
        INSERT INTO service_worker_status VALUES
            ('main', 'offline', now());

        CREATE TABLE scrape_phase_attempts (
            scrape_id bigint NOT NULL,
            phase_id text NOT NULL,
            attempt integer NOT NULL,
            status text NOT NULL,
            PRIMARY KEY (scrape_id, phase_id, attempt));

        CREATE TABLE leaderboard_snapshot_state (
            song_id text NOT NULL,
            instrument text NOT NULL,
            active_snapshot_id bigint,
            scrape_id bigint,
            is_finalized boolean NOT NULL DEFAULT false,
            updated_at timestamptz NOT NULL,
            PRIMARY KEY (song_id, instrument));
        INSERT INTO leaderboard_snapshot_state VALUES
            ('current-song', '{INSTRUMENT}', 1303, 1303, true, now());

        CREATE TABLE solo_current_projection_scope (
            song_id text NOT NULL,
            instrument text NOT NULL,
            projection_generation bigint NOT NULL DEFAULT 0,
            row_count bigint NOT NULL DEFAULT 0,
            source_snapshot_id bigint,
            source_kind text NOT NULL DEFAULT 'snapshot',
            status text NOT NULL DEFAULT 'ready',
            updated_at timestamptz NOT NULL,
            PRIMARY KEY (song_id, instrument));
        INSERT INTO solo_current_projection_scope VALUES
            ('current-song', '{INSTRUMENT}', 1303, {retained_rows},
             1303, 'snapshot', 'ready', now()),
            ('previous-song', '{INSTRUMENT}', 1303, {retained_rows},
             1302, 'snapshot', 'ready', now());

        CREATE TABLE leaderboard_published_scope_source (
            published_scrape_id bigint NOT NULL,
            song_id text NOT NULL,
            instrument text NOT NULL,
            scope_kind text NOT NULL,
            source_kind text NOT NULL,
            source_snapshot_id bigint,
            source_scrape_id bigint NOT NULL,
            row_count bigint NOT NULL,
            content_fingerprint text NOT NULL,
            coverage_fingerprint text NOT NULL,
            reported_total_entries bigint NOT NULL,
            reported_total_pages integer NOT NULL,
            is_complete boolean NOT NULL,
            created_at timestamptz NOT NULL,
            validated_at timestamptz NOT NULL,
            PRIMARY KEY (
                published_scrape_id, instrument, song_id, scope_kind));
        INSERT INTO leaderboard_published_scope_source VALUES
            (1303, 'current-song', '{INSTRUMENT}', 'alltime',
             'snapshot', 1303, 1303, {retained_rows},
             'current-content', 'current-coverage',
             {retained_rows}, 1, true, now(), now()),
            (1302, 'previous-song', '{INSTRUMENT}', 'alltime',
             'snapshot', 1302, 1302, {retained_rows},
             'previous-content', 'previous-coverage',
             {retained_rows}, 1, true, now(), now());

        CREATE TABLE leaderboard_entries_snapshot (
            snapshot_id bigint NOT NULL,
            song_id text NOT NULL,
            instrument text NOT NULL,
            account_id text NOT NULL,
            score integer NOT NULL,
            accuracy integer,
            is_full_combo boolean,
            stars integer,
            season integer,
            percentile real,
            rank integer DEFAULT 0,
            source text NOT NULL DEFAULT 'scrape',
            difficulty integer DEFAULT -1,
            api_rank integer,
            end_time text,
            band_members_json jsonb,
            band_score integer,
            base_score integer,
            instrument_bonus integer,
            overdrive_bonus integer,
            instrument_combo text,
            first_seen_at timestamptz NOT NULL,
            last_updated_at timestamptz NOT NULL,
            PRIMARY KEY (
                snapshot_id, song_id, instrument, account_id)
        ) PARTITION BY LIST (instrument);

        CREATE TABLE {TARGET_PARTITION}
            PARTITION OF leaderboard_entries_snapshot
            FOR VALUES IN ('{INSTRUMENT}');

        CREATE INDEX ix_les_snapshot_song_score
            ON leaderboard_entries_snapshot
            (snapshot_id, song_id, instrument, score DESC);

        INSERT INTO leaderboard_entries_snapshot (
            snapshot_id, song_id, instrument, account_id,
            score, accuracy, is_full_combo, stars, season,
            percentile, rank, source, difficulty, api_rank,
            end_time, band_members_json, band_score, base_score,
            instrument_bonus, overdrive_bonus, instrument_combo,
            first_seen_at, last_updated_at)
        SELECT
            input.snapshot_id,
            input.song_prefix ||
                CASE WHEN series = 1
                     THEN ''
                     ELSE '-' || series::text END,
            '{INSTRUMENT}',
            'account-' || input.snapshot_id || '-' || series,
            1000000 - series,
            950000,
            series % 7 = 0,
            5,
            9,
            0.5,
            series,
            'synthetic',
            3,
            series,
            NULL,
            CASE WHEN series % 20 = 0
                 THEN jsonb_build_array('a', 'b')
                 ELSE NULL END,
            NULL, NULL, NULL, NULL, NULL,
            now() - interval '1 day',
            now()
        FROM (
            VALUES
                (1301::bigint, 'stale-song', {purge_rows}),
                (1302::bigint, 'previous-song', {retained_rows}),
                (1303::bigint, 'current-song', {retained_rows})
        ) input(snapshot_id, song_prefix, row_count)
        CROSS JOIN LATERAL generate_series(
            1, input.row_count) series;

        ANALYZE {TARGET_PARTITION};
        SELECT pg_stat_force_next_flush();
        CHECKPOINT;
    """


def start_source(container, data_root):
    run(
        [
            "docker",
            "run",
            "--name",
            container,
            "--detach",
            "--network",
            "none",
            "-e",
            "POSTGRES_HOST_AUTH_METHOD=trust",
            "-e",
            "POSTGRES_USER=fst",
            "-e",
            "POSTGRES_DB=fstservice",
            "-v",
            f"{data_root}:/var/lib/postgresql/data",
            IMAGE,
        ],
        timeout=120,
    )
    wait_ready(container)


def cleanup_directory(directory):
    directory = pathlib.Path(directory)
    if not directory.exists():
        return
    run(
        [
            "docker",
            "run",
            "--rm",
            "--network",
            "none",
            "--user",
            "0:0",
            "-v",
            f"{directory}:/owned",
            IMAGE,
            "sh",
            "-c",
            (
                "find /owned -mindepth 1 -maxdepth 1 "
                "-exec rm -rf -- {} + && "
                f"chown {os.getuid()}:{os.getgid()} /owned && "
                "chmod 700 /owned"
            ),
        ],
        timeout=600,
    )


def device_id(path):
    output = run(
        [
            "findmnt",
            "-T",
            str(path),
            "-n",
            "-o",
            "MAJ:MIN",
        ],
        timeout=30,
    ).stdout.strip()
    if not output:
        raise DrillError("could not derive drill filesystem device ID")
    return output


def stage_command(stage, scratch, run_id, container):
    command = [
        sys.executable,
        str(MIGRATION),
        stage,
        "--instrument",
        TARGET_KEY,
        "--scratch-root",
        str(scratch),
        "--expected-device-id",
        device_id(scratch),
        "--run-id",
        run_id,
        "--test-mode",
        "--pg-container",
        container,
        "--pg-user",
        "fst",
        "--pg-database",
        "fstservice",
        "--restore-image",
        IMAGE,
        "--query-timeout-seconds",
        "600",
        "--archive-timeout-seconds",
        "1200",
        "--build-timeout-seconds",
        "1200",
        "--maximum-swap-seconds",
        "30",
    ]
    if stage == "check":
        command.append("--claim-workspace")
    if stage in EXECUTE_STAGES:
        command.append("--execute")
    return command


def run_stage(stage, scratch, run_id, container):
    completed = run(
        stage_command(stage, scratch, run_id, container),
        timeout=1800,
    )
    try:
        return json.loads(completed.stdout)
    except json.JSONDecodeError as error:
        raise DrillError(
            f"{stage} did not return JSON: {completed.stdout}"
        ) from error


def truncate_report(scratch, stage):
    path = pathlib.Path(scratch) / "reports" / f"{stage}.json"
    with path.open("wb") as handle:
        handle.flush()
        os.fsync(handle.fileno())
    directory = os.open(
        path.parent,
        os.O_RDONLY | getattr(os, "O_DIRECTORY", 0),
    )
    try:
        os.fsync(directory)
    finally:
        os.close(directory)


def relation_summary(container):
    summary = json.loads(
        psql(
            container,
            f"""
                SELECT json_build_object(
                    'targetKind', (
                        SELECT relkind
                        FROM pg_class
                        WHERE oid =
                            'public.{TARGET_PARTITION}'::regclass),
                    'targetPartitionKey', pg_get_partkeydef(
                        'public.{TARGET_PARTITION}'::regclass),
                    'snapshotIds', (
                        SELECT json_agg(DISTINCT snapshot_id
                                        ORDER BY snapshot_id)
                        FROM {TARGET_PARTITION}),
                    'defaultExists', to_regclass(
                        'public.{TARGET_PARTITION}_default')
                        IS NOT NULL,
                    'migrationArtifacts', (
                        SELECT COALESCE(
                            json_agg(relname ORDER BY relname),
                            '[]'::json)
                        FROM pg_class relation
                        JOIN pg_namespace namespace
                          ON namespace.oid = relation.relnamespace
                        WHERE namespace.nspname = 'public'
                          AND relation.relname LIKE
                                'sgm\\_%' ESCAPE '\\'))
            """,
        )
    )
    summary["defaultRows"] = (
        int(
            psql(
                container,
                f"SELECT COUNT(*) FROM {TARGET_PARTITION}_default",
            )
        )
        if summary.pop("defaultExists")
        else None
    )
    return summary


def run_lane(root, lane, *, final_action):
    lane_root = root / lane
    data_root = lane_root / "source-pgdata"
    scratch = lane_root / "scratch"
    data_root.mkdir(parents=True, mode=0o700)
    scratch.mkdir(mode=0o700)
    token = hashlib.sha256(
        f"{root}:{lane}".encode("utf-8")
    ).hexdigest()[:10]
    container = f"fst-snapshot-generation-test-{lane}-{token}"
    run_id = f"drill-{lane}-{token}"
    stage_results = []
    started = time.monotonic()
    try:
        start_source(container, data_root)
        psql(container, seed_sql(800, 300), timeout=600)
        for stage in ("check", "plan", "archive"):
            stage_results.append(
                run_stage(stage, scratch, run_id, container)
            )
        for stage in ("archive", "restore", "build", "swap"):
            if stage != "archive":
                stage_results.append(
                    run_stage(stage, scratch, run_id, container)
                )
            truncate_report(scratch, stage)
            stage_results.append(
                run_stage(stage, scratch, run_id, container)
            )
        for stage in ("archive", "restore", "build", "swap"):
            recovered = list(
                (scratch / "recovered-evidence").glob(
                    f"{stage}.recovery-*.json"
                )
            )
            if len(recovered) != 1:
                raise DrillError(
                    f"{stage} torn-report recovery evidence is missing"
                )
        stage_results.append(
            run_stage("validate", scratch, run_id, container)
        )
        if final_action == "rollback":
            run(
                ["docker", "kill", "--signal", "KILL", container],
                timeout=60,
            )
            run(["docker", "start", container], timeout=60)
            wait_ready(container)
            psql(container, "SELECT pg_stat_reset();", timeout=60)
            rollback_result = run_stage(
                "rollback",
                scratch,
                run_id,
                container,
            )
            stage_results.append(rollback_result)
            rollback_report = json.loads(
                (
                    scratch
                    / "reports"
                    / "rollback.json"
                ).read_text(encoding="utf-8")
            )
            if not rollback_report.get("fullArchiveReproofPerformed"):
                raise DrillError(
                    "rollback did not reprove archive after stats reset"
                )
            summary = relation_summary(container)
            if (
                summary["targetKind"] != "r"
                or summary["snapshotIds"] != [1301, 1302, 1303]
                or not summary["migrationArtifacts"]
            ):
                raise DrillError(
                    f"rollback lane state is unexpected: {summary}"
                )
        else:
            truncate_report(scratch, "validate")
            stage_results.append(
                run_stage("validate", scratch, run_id, container)
            )
            recovered_validate = list(
                (scratch / "recovered-evidence").glob(
                    "validate.recovery-*.json"
                )
            )
            if len(recovered_validate) != 1:
                raise DrillError(
                    "validate torn-report recovery evidence is missing"
                )
            stage_results.append(
                run_stage("drop", scratch, run_id, container)
            )
            truncate_report(scratch, "drop")
            stage_results.append(
                run_stage("drop", scratch, run_id, container)
            )
            summary = relation_summary(container)
            if (
                summary["targetKind"] != "p"
                or summary["targetPartitionKey"]
                    != "LIST (snapshot_id)"
                or summary["snapshotIds"] != [1302, 1303]
                or summary["defaultRows"] != 0
                or summary["migrationArtifacts"]
            ):
                raise DrillError(
                    f"drop lane state is unexpected: {summary}"
                )
        archive = (
            scratch
            / "archive"
            / f"{TARGET_KEY}-original.custom"
        )
        if not archive.is_file() or archive.stat().st_size <= 0:
            raise DrillError("lane archive was not retained")
        return {
            "lane": lane,
            "finalAction": final_action,
            "elapsedSeconds": round(time.monotonic() - started, 3),
            "container": container,
            "stageCount": len(stage_results),
            "finalRelation": summary,
            "archive": {
                "path": str(archive),
                "bytes": archive.stat().st_size,
                "sha256": sha256_path(archive),
            },
            "workspace": str(scratch),
            "tornEvidenceRecovered": True,
        }
    finally:
        run(
            ["docker", "rm", "-f", container],
            timeout=120,
            check=False,
        )
        cleanup_directory(data_root)
        with contextlib.suppress(OSError):
            data_root.rmdir()


def build_parser():
    parser = argparse.ArgumentParser(
        description=(
            "Run rollback and final-drop lanes against isolated "
            "network-none PostgreSQL 17."
        )
    )
    parser.add_argument(
        "--work-root",
        help=(
            "Empty/new drill artifact directory. Defaults below "
            "artifacts/snapshot-generation-migration-drills."
        ),
    )
    return parser


def main(argv=None):
    args = build_parser().parse_args(argv)
    if args.work_root:
        root = pathlib.Path(args.work_root).resolve()
    else:
        timestamp = datetime.now(timezone.utc).strftime(
            "%Y%m%dT%H%M%SZ"
        )
        root = (
            REPO_ROOT
            / "artifacts"
            / "snapshot-generation-migration-drills"
            / timestamp
        ).resolve()
    if pathlib.Path("/tmp") in (root, *root.parents):
        raise DrillError("drill work root must not use /tmp")
    if root.exists() and any(root.iterdir()):
        raise DrillError("drill work root must be empty")
    root.mkdir(parents=True, exist_ok=True, mode=0o700)
    started = time.monotonic()
    lanes = [
        run_lane(root, "rollback", final_action="rollback"),
        run_lane(root, "drop", final_action="drop"),
    ]
    summary = {
        "formatVersion": 1,
        "tool": "fst.snapshot-generation-partition-migration-drill.v1",
        "completedAtUtc": datetime.now(timezone.utc)
        .isoformat()
        .replace("+00:00", "Z"),
        "postgresImage": IMAGE,
        "postgresMajor": 17,
        "networkMode": "none",
        "target": f"public.{TARGET_PARTITION}",
        "instrument": INSTRUMENT,
        "lanes": lanes,
        "elapsedSeconds": round(time.monotonic() - started, 3),
        "passed": True,
    }
    summary_path = root / "drill-summary.json"
    write_json(summary_path, summary)
    print(
        json.dumps(
            {
                "status": "succeeded",
                "summary": str(summary_path),
                "summarySha256": sha256_path(summary_path),
            },
            sort_keys=True,
        )
    )
    return 0


if __name__ == "__main__":
    try:
        sys.exit(main())
    except (DrillError, subprocess.TimeoutExpired) as error:
        print(f"ERROR: {error}", file=sys.stderr)
        sys.exit(3)
