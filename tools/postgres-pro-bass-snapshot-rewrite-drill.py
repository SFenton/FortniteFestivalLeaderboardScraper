#!/usr/bin/env python3

"""Scaled PostgreSQL 17 drill for the pro-bass rewrite pilot."""

import argparse
import hashlib
import json
import os
import pathlib
import shutil
import stat
import subprocess
import sys
import time
from datetime import datetime, timezone


SCRIPT_DIR = pathlib.Path(__file__).resolve().parent
REPO_ROOT = SCRIPT_DIR.parent
PILOT = SCRIPT_DIR / "postgres-pro-bass-snapshot-rewrite.py"
TOOL_ID = "fst.pro-bass-snapshot-rewrite-pilot.v1"
TARGET = "leaderboard_entries_snapshot_pro_bass"
FORMAT_VERSION = 1


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
    with path.open("xb") as handle:
        handle.write(canonical_json_bytes(value))
        handle.flush()
        os.fsync(handle.fileno())
    path.chmod(0o600)


def wait_ready(container):
    deadline = time.monotonic() + 120
    consecutive_successes = 0
    while time.monotonic() < deadline:
        result = run(
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
                "SELECT 1",
            ],
            timeout=15,
            check=False,
        )
        if result.returncode == 0:
            logs = run(
                ["docker", "logs", container],
                timeout=15,
                check=False,
            )
            initialized = (
                "PostgreSQL init process complete"
                in (logs.stdout + logs.stderr)
            )
            consecutive_successes = (
                consecutive_successes + 1
                if initialized
                else 0
            )
            if consecutive_successes >= 3:
                return
        else:
            consecutive_successes = 0
        time.sleep(1)
    raise DrillError("synthetic PostgreSQL did not become ready")


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

        INSERT INTO scrape_log
            (id, started_at, completed_at, status)
        VALUES
            (70, now() - interval '30 days',
                 now() - interval '30 days', 'completed'),
            (80, now() - interval '20 days',
                 now() - interval '20 days', 'completed'),
            (90, now() - interval '10 days',
                 now() - interval '10 days', 'completed'),
            (98, now() - interval '3 days',
                 now() - interval '3 days', 'completed'),
            (99, now() - interval '2 days',
                 now() - interval '2 days', 'completed'),
            (100, now() - interval '1 day',
                  now() - interval '1 day', 'completed');

        CREATE TABLE publication_generations (
            publication_id bigint PRIMARY KEY,
            scrape_id bigint UNIQUE NOT NULL,
            status text NOT NULL);
        INSERT INTO publication_generations VALUES
            (1, 100, 'current'),
            (2, 99, 'retained'),
            (3, 98, 'retained');

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
            (true, 100, 1, 2, NULL, false, NULL, now());

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
            (100, 'current-song', 'Solo_PeripheralBass',
             'alltime', 'snapshot', 90, 90, {retained_rows},
             'current', 'current', {retained_rows}, 1,
             true, now(), now()),
            (99, 'previous-song', 'Solo_PeripheralBass',
             'alltime', 'snapshot', 80, 80, {retained_rows},
             'previous', 'previous', {retained_rows}, 1,
             true, now(), now()),
            (98, 'stale-song', 'Solo_PeripheralBass',
             'alltime', 'snapshot', 70, 70, {purge_rows},
             'stale', 'stale', {purge_rows}, 1,
             true, now(), now());

        CREATE TABLE leaderboard_snapshot_state (
            song_id text NOT NULL,
            instrument text NOT NULL,
            active_snapshot_id bigint,
            scrape_id bigint,
            is_finalized boolean NOT NULL,
            updated_at timestamptz NOT NULL,
            PRIMARY KEY (song_id, instrument));
        INSERT INTO leaderboard_snapshot_state VALUES
            ('current-song', 'Solo_PeripheralBass',
             90, 100, true, now());

        CREATE TABLE solo_current_projection_scope (
            song_id text NOT NULL,
            instrument text NOT NULL,
            projection_generation bigint NOT NULL,
            row_count bigint NOT NULL,
            source_snapshot_id bigint,
            status text NOT NULL,
            updated_at timestamptz NOT NULL,
            PRIMARY KEY (song_id, instrument));
        INSERT INTO solo_current_projection_scope VALUES
            ('current-song', 'Solo_PeripheralBass',
             1, {retained_rows}, 90, 'ready', now());

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

        CREATE TABLE leaderboard_entries_snapshot_pro_bass
            PARTITION OF leaderboard_entries_snapshot
            FOR VALUES IN ('Solo_PeripheralBass');

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
            (CASE input.snapshot_id
                WHEN 90 THEN 'current-song'
                WHEN 80 THEN 'previous-song'
                ELSE 'stale-song'
             END)
             || CASE
                    WHEN series = 1 THEN ''
                    ELSE '-' || ((series - 1) % 101)
                END,
            'Solo_PeripheralBass',
            'account-' || input.snapshot_id || '-' || series,
            1000000 - (series % 100000),
            900000 + (series % 100000),
            (series % 7) = 0,
            5,
            9,
            ((series % 10000)::real / 10000),
            series,
            'synthetic',
            3,
            series,
            NULL,
            CASE WHEN series % 20 = 0
                 THEN jsonb_build_array('a', 'b')
                 ELSE NULL END,
            NULL,
            NULL,
            NULL,
            NULL,
            NULL,
            now() - interval '1 day',
            now()
        FROM (
            VALUES
                (70::bigint, {purge_rows}),
                (80::bigint, {retained_rows}),
                (90::bigint, {retained_rows})
        ) input(snapshot_id, row_count)
        CROSS JOIN LATERAL generate_series(
            1, input.row_count) series;

        ANALYZE leaderboard_entries_snapshot_pro_bass;
        CHECKPOINT;
    """


def stage_command(
    stage,
    scratch,
    device_id,
    run_id,
    container,
    image,
    profile_path=None,
):
    command = [
        sys.executable,
        str(PILOT),
        stage,
        "--scratch-root",
        str(scratch),
        "--expected-device-id",
        device_id,
        "--run-id",
        run_id,
        "--test-mode",
        "--pg-container",
        container,
        "--restore-image",
        image,
        "--query-timeout-seconds",
        "3600",
        "--archive-timeout-seconds",
        "3600",
        "--build-timeout-seconds",
        "3600",
    ]
    if stage == "check":
        command.append("--claim-workspace")
    if stage in (
        "archive",
        "drill",
        "build",
        "swap",
        "drop",
        "rollback",
    ):
        command.append("--execute")
    if stage == "build":
        command.extend(
            [
                "--measured-profile",
                str(profile_path),
                "--expected-profile-sha256",
                sha256_path(profile_path),
            ]
        )
    result = run(command, timeout=7200)
    return json.loads(result.stdout)


def read_report(scratch, stage):
    return json.loads(
        (scratch / "reports" / f"{stage}.json").read_text(
            encoding="utf-8"
        )
    )


def simulate_missing_acknowledgement(
    stage,
    scratch,
    device,
    run_id,
    container,
    image,
    profile_path,
    evidence_root,
):
    report_path = scratch / "reports" / f"{stage}.json"
    first_report = read_report(scratch, stage)
    copy_path = evidence_root / f"{stage}-first-report.json"
    shutil.copy2(report_path, copy_path)
    report_path.unlink()
    stage_command(
        stage,
        scratch,
        device,
        run_id,
        container,
        image,
        profile_path,
    )
    resumed = read_report(scratch, stage)
    resume_fields = {
        "archive": "resumedExistingArchive",
        "build": "resumedExistingAtomicBuild",
        "swap": "resumedCommittedSwap",
        "rollback": "resumedCommittedRollback",
    }
    field = resume_fields[stage]
    if not resumed.get(field):
        raise DrillError(
            f"{stage} did not recognize its committed interruption state"
        )
    return {
        "stage": stage,
        "firstReport": str(copy_path),
        "firstReportSha256": sha256_path(copy_path),
        "resumedReport": str(report_path),
        "resumedReportSha256": sha256_path(report_path),
        "resumeField": field,
        "firstStatus": first_report["status"],
        "resumedStatus": resumed["status"],
    }


def seed_profile(path):
    value = {
        "formatVersion": FORMAT_VERSION,
        "toolId": TOOL_ID,
        "profileId": "synthetic-seed-only",
        "isolatedPg17DrillPassed": True,
        "replacementHeapToSourceRetainedRatio": 3.0,
        "replacementIndexToSourceRetainedRatio": 3.0,
        "walToReplacementRatio": 3.0,
        "tempToReplacementRatio": 1.0,
        "failureReserveRatio": 2.0,
        "promotionEligible": False,
        "purpose": (
            "Conservative bootstrap profile used only to run the "
            "isolated measurement; never valid for production."
        ),
    }
    write_json(path, value)


def cleanup_directory(image, directory):
    if not pathlib.Path(directory).exists():
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
            image,
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


def derive_profile(
    work_root,
    rollback_scratch,
    drop_scratch,
    purge_rows,
    retained_rows,
):
    plan = read_report(drop_scratch, "plan")
    build = read_report(drop_scratch, "build")
    drill = read_report(drop_scratch, "drill")
    drop = read_report(drop_scratch, "drop")
    total_rows = plan["planIdentity"]["exactTotalRows"]
    retained_exact = plan["planIdentity"]["exactRetainedRows"]
    retained_ratio = retained_exact / total_rows
    estimated_source_heap = (
        plan["catalog"]["heapBytes"] * retained_ratio
    )
    estimated_source_indexes = (
        plan["catalog"]["indexBytes"] * retained_ratio
    )
    replacement_heap = build["sizes"]["heapBytes"]
    replacement_indexes = build["sizes"]["indexBytes"]
    replacement_total = build["sizes"]["totalBytes"]
    profile = {
        "formatVersion": FORMAT_VERSION,
        "toolId": TOOL_ID,
        "profileId": (
            "scaled-synthetic-pg17-"
            + datetime.now(timezone.utc).strftime(
                "%Y%m%dT%H%M%SZ"
            )
        ),
        "createdAtUtc": datetime.now(timezone.utc).isoformat().replace(
            "+00:00", "Z"
        ),
        "isolatedPg17DrillPassed": True,
        "promotionEligible": True,
        "scale": {
            "purgeRows": purge_rows,
            "rowsPerRetainedSnapshot": retained_rows,
            "totalRows": total_rows,
            "retainedRows": retained_exact,
            "retainedRatio": retained_ratio,
        },
        "replacementHeapToSourceRetainedRatio": (
            replacement_heap / estimated_source_heap
        ),
        "replacementIndexToSourceRetainedRatio": (
            replacement_indexes / estimated_source_indexes
        ),
        "walToReplacementRatio": (
            build["sizes"]["walBytes"] / replacement_total
        ),
        "tempToReplacementRatio": (
            build["sizes"]["tempBytes"] / replacement_total
        ),
        "failureReserveRatio": 1.0,
        "observedPeakToReplacementRatio": (
            build["observedPeakFilesystemGrowthBytes"]
            / replacement_total
        ),
        "measured": {
            "sourceHeapBytes": plan["catalog"]["heapBytes"],
            "sourceIndexBytes": plan["catalog"]["indexBytes"],
            "sourceTotalBytes": plan["catalog"]["totalBytes"],
            "replacementHeapBytes": replacement_heap,
            "replacementIndexBytes": replacement_indexes,
            "replacementTotalBytes": replacement_total,
            "walBytes": build["sizes"]["walBytes"],
            "tempBytes": build["sizes"]["tempBytes"],
            "peakFilesystemGrowthBytes": build[
                "observedPeakFilesystemGrowthBytes"
            ],
            "archiveBytes": read_report(
                drop_scratch,
                "archive",
            )["archive"]["bytes"],
            "restorePeakBytes": drill["restorePeakBytes"],
            "dropRelationBytes": drop["droppedRelationBytes"],
            "filesystemBytesReturned": drop[
                "filesystemBytesReturned"
            ],
        },
        "proof": {
            "rollbackWorkspace": str(rollback_scratch),
            "rollbackReportSha256": sha256_path(
                rollback_scratch
                / "reports"
                / "rollback.json"
            ),
            "dropWorkspace": str(drop_scratch),
            "dropReportSha256": sha256_path(
                drop_scratch / "reports" / "drop.json"
            ),
            "archiveRestoreReportSha256": sha256_path(
                drop_scratch / "reports" / "drill.json"
            ),
        },
        "interpretation": {
            "scaledSyntheticOnly": True,
            "liveExecutionStillRequiresExactPlanAndCapacityGate": True,
            "archiveMustRemainUntilSeparateRetentionDecision": True,
        },
    }
    profile_path = work_root / "measured-profile.json"
    write_json(profile_path, profile)
    return profile_path, profile


def build_parser():
    parser = argparse.ArgumentParser()
    parser.add_argument("--work-root", required=True)
    parser.add_argument("--image", default="postgres:17")
    parser.add_argument("--purge-rows", type=int, default=300_000)
    parser.add_argument("--retained-rows", type=int, default=30_000)
    return parser


def main(argv=None):
    args = build_parser().parse_args(argv)
    work_root = pathlib.Path(args.work_root)
    if not work_root.is_absolute():
        raise DrillError("--work-root must be absolute")
    resolved_parent = work_root.parent.resolve(strict=True)
    if not str(resolved_parent).startswith(
        str((REPO_ROOT / "artifacts").resolve())
    ):
        raise DrillError(
            "isolated drill work must stay under repository artifacts/"
        )
    if work_root.exists() and any(work_root.iterdir()):
        raise DrillError("--work-root must be absent or empty")
    work_root.mkdir(parents=True, exist_ok=True, mode=0o700)
    device = run(
        [
            "findmnt",
            "-T",
            str(work_root),
            "-n",
            "-o",
            "MAJ:MIN",
        ],
        timeout=30,
    ).stdout.strip()
    container = (
        "fst-pro-bass-pilot-test-"
        + hashlib.sha256(
            str(work_root).encode("utf-8")
        ).hexdigest()[:12]
    )
    pgdata = work_root / "source-pgdata"
    pgdata.mkdir(mode=0o700)
    rollback_scratch = work_root / "rollback-path"
    drop_scratch = work_root / "drop-path"
    rollback_scratch.mkdir(mode=0o700)
    drop_scratch.mkdir(mode=0o700)
    seed_path = work_root / "seed-profile.json"
    seed_profile(seed_path)
    interruption_root = work_root / "interruption-recovery"
    interruption_root.mkdir(mode=0o700)
    run_id_rollback = "synthetic-rollback-0001"
    run_id_drop = "synthetic-drop-0001"
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
            f"{pgdata}:/var/lib/postgresql/data",
            args.image,
        ],
        timeout=120,
    )
    succeeded = False
    interruption_results = []
    try:
        wait_ready(container)
        psql(
            container,
            seed_sql(args.purge_rows, args.retained_rows),
            timeout=1800,
        )
        for stage in (
            "check",
            "plan",
            "archive",
            "drill",
            "build",
            "swap",
            "validate",
            "rollback",
        ):
            stage_command(
                stage,
                rollback_scratch,
                device,
                run_id_rollback,
                container,
                args.image,
                seed_path,
            )
            if stage in ("archive", "build", "swap", "rollback"):
                interruption_results.append(
                    simulate_missing_acknowledgement(
                        stage,
                        rollback_scratch,
                        device,
                        run_id_rollback,
                        container,
                        args.image,
                        seed_path,
                        interruption_root,
                    )
                )
        restored_rows = int(
            psql(
                container,
                f"SELECT COUNT(*) FROM {TARGET};",
            )
        )
        expected_rows = (
            args.purge_rows + 2 * args.retained_rows
        )
        if restored_rows != expected_rows:
            raise DrillError(
                "rollback did not restore the original row count"
            )
        failed_relation = (
            f"{TARGET}_failed_"
            + hashlib.sha256(
                run_id_rollback.encode("utf-8")
            ).hexdigest()[:12]
        )
        psql(
            container,
            f'DROP TABLE public."{failed_relation}"; CHECKPOINT;',
        )

        for stage in (
            "check",
            "plan",
            "archive",
            "drill",
            "build",
            "swap",
            "validate",
            "drop",
        ):
            stage_command(
                stage,
                drop_scratch,
                device,
                run_id_drop,
                container,
                args.image,
                seed_path,
            )
        final_rows = int(
            psql(
                container,
                f"SELECT COUNT(*) FROM {TARGET};",
            )
        )
        if final_rows != 2 * args.retained_rows:
            raise DrillError(
                "final-drop path retained the wrong row count"
            )
        profile_path, profile = derive_profile(
            work_root,
            rollback_scratch,
            drop_scratch,
            args.purge_rows,
            args.retained_rows,
        )
        summary = {
            "formatVersion": FORMAT_VERSION,
            "toolId": TOOL_ID,
            "status": "succeeded",
            "completedAtUtc": datetime.now(
                timezone.utc
            ).isoformat().replace("+00:00", "Z"),
            "containerImage": args.image,
            "deviceId": device,
            "rollbackPathPassed": True,
            "archiveRestorePassed": True,
            "finalDropPathPassed": True,
            "interruptionRecoveryPassed": True,
            "interruptionRecovery": interruption_results,
            "originalRows": expected_rows,
            "retainedRows": final_rows,
            "purgedRows": args.purge_rows,
            "measuredProfile": {
                "path": str(profile_path),
                "sha256": sha256_path(profile_path),
            },
            "measured": profile["measured"],
            "archiveRetention": {
                "rollbackArchive": str(
                    rollback_scratch / "archive"
                ),
                "dropArchive": str(
                    drop_scratch / "archive"
                ),
                "deletionDecision": "deferred",
            },
        }
        summary_path = work_root / "drill-summary.json"
        write_json(summary_path, summary)
        print(
            json.dumps(
                {
                    "status": "succeeded",
                    "summary": str(summary_path),
                    "summarySha256": sha256_path(summary_path),
                    "measuredProfile": str(profile_path),
                    "measuredProfileSha256": sha256_path(
                        profile_path
                    ),
                },
                sort_keys=True,
            )
        )
        succeeded = True
        return 0
    finally:
        run(
            ["docker", "rm", "-f", container],
            timeout=120,
            check=False,
        )
        cleanup_directory(args.image, pgdata)
        if pgdata.exists():
            pgdata.rmdir()
        cleanup = {
            "temporarySourceContainerRemoved": True,
            "temporarySourcePgdataRemoved": not pgdata.exists(),
            "archivesRetained": True,
            "drillSucceeded": succeeded,
            "recordedAtUtc": datetime.now(
                timezone.utc
            ).isoformat().replace("+00:00", "Z"),
        }
        cleanup_path = work_root / "cleanup-proof.json"
        if not cleanup_path.exists():
            write_json(cleanup_path, cleanup)


if __name__ == "__main__":
    try:
        sys.exit(main())
    except (DrillError, subprocess.TimeoutExpired) as error:
        print(f"ERROR: {error}", file=sys.stderr)
        sys.exit(3)
