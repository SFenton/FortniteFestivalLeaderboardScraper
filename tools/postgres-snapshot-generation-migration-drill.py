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


def run_archive_mutation_drop_probe(
    scratch,
    run_id,
    container,
    mutation_phase,
):
    archive = (
        scratch
        / "archive"
        / f"{TARGET_KEY}-original.custom"
    )
    recovery = archive.with_name(
        "." + archive.name + ".drop-recovery"
    )
    expected_sha = sha256_path(archive)
    gate = scratch / "reports" / "archive-mutation-drop-gate"
    gate_paths = {
        phase: {
            suffix: gate.with_name(
                gate.name + f".{phase}.{suffix}"
            )
            for suffix in ("ready", "continue")
        }
        for phase in ("pre-ddl", "pre-commit", "commit-entry")
    }
    command = stage_command("drop", scratch, run_id, container)
    command.extend(["--test-final-drop-gate", str(gate)])
    drop = subprocess.Popen(
        command,
        text=True,
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
    )
    try:
        for phase in ("pre-ddl", "pre-commit"):
            ready = gate_paths[phase]["ready"]
            deadline = time.monotonic() + 120
            while time.monotonic() < deadline and not ready.is_file():
                if drop.poll() is not None:
                    stdout, stderr = drop.communicate()
                    raise DrillError(
                        "drop exited before the archive-mutation gate: "
                        + stderr
                        + stdout
                    )
                time.sleep(0.05)
            if not ready.is_file():
                raise DrillError(
                    "timed out waiting for final-drop decision gate "
                    + phase
                )
            if phase == mutation_phase:
                break
            gate_paths[phase]["continue"].write_text(
                "continue\n",
                encoding="ascii",
            )
        archive.unlink()
        archive.write_bytes(b"replacement archive")
        gate_paths[mutation_phase]["continue"].write_text(
            "continue\n",
            encoding="ascii",
        )
        stdout, stderr = drop.communicate(timeout=120)
        if drop.returncode == 0:
            raise DrillError(
                "archive mutation unexpectedly allowed final drop: "
                + stdout
            )
        if "archive" not in stderr.lower():
            raise DrillError(
                "archive mutation failure did not identify evidence: "
                + stderr
            )
        state = relation_summary(container)
        if (
            state["targetKind"] != "p"
            or not state["migrationArtifacts"]
            or not recovery.is_file()
            or os.path.samefile(archive, recovery)
            or sha256_path(recovery) != expected_sha
        ):
            raise DrillError(
                "archive mutation did not preserve rollback state: "
                f"{state}"
            )
        archive.unlink()
        shutil.copyfile(recovery, archive)
        archive.chmod(0o600)
        package = (
            scratch
            / "recovered-evidence"
            / "drop-recovery-package"
        )
        if package.exists():
            package.chmod(0o700)
            for child in package.iterdir():
                child.unlink()
            package.rmdir()
        for path in (
            recovery,
            (
                scratch
                / "archive"
                / f".{TARGET_KEY}-manifest.json.drop-recovery"
            ),
            scratch / "reports" / "drop.recovery.json",
            scratch / "reports" / ".drop.recovery.json.drop-recovery",
            *(
                scratch
                / "reports"
                / f".{stage}.json.drop-recovery"
                for stage in (
                    "check",
                    "plan",
                    "archive",
                    "restore",
                    "validate",
                )
            ),
        ):
            with contextlib.suppress(FileNotFoundError):
                path.unlink()
        for path in (
            scratch / "reports"
        ).glob(".drop-recovery-manifest-*.recovery"):
            path.unlink()
        for paths in gate_paths.values():
            for path in paths.values():
                with contextlib.suppress(FileNotFoundError):
                    path.unlink()
        return {
            "blocked": True,
            "mutationPhase": mutation_phase,
            "originalSha256": expected_sha,
            "independentRecoveryPath": str(recovery),
            "stateAfterFailure": state,
        }
    finally:
        if drop.poll() is None:
            drop.terminate()
            with contextlib.suppress(subprocess.TimeoutExpired):
                drop.communicate(timeout=10)


def run_child_catalog_mutation_probe(scratch, run_id, container):
    child = f"{TARGET_PARTITION}_s1302"
    constraint = "drill_unexpected_score_check"
    psql(
        container,
        (
            f"ALTER TABLE {child} ADD CONSTRAINT {constraint} "
            "CHECK (score < 0) NOT VALID"
        ),
        timeout=60,
    )
    try:
        completed = run(
            stage_command("drop", scratch, run_id, container),
            timeout=1800,
            check=False,
        )
        if completed.returncode == 0:
            raise DrillError(
                "child-local constraint unexpectedly allowed final drop"
            )
        if (
            "partition evidence changed" not in completed.stderr
            and "generation child" not in completed.stderr
        ):
            raise DrillError(
                "child catalog rejection was not attributed to a "
                "catalog guard: "
                + completed.stderr
            )
        state = relation_summary(container)
        if (
            state["targetKind"] != "p"
            or not state["migrationArtifacts"]
        ):
            raise DrillError(
                "child catalog mutation did not preserve rollback state: "
                f"{state}"
            )
        return {
            "blocked": True,
            "child": child,
            "constraint": constraint,
            "stateAfterFailure": state,
        }
    finally:
        psql(
            container,
            (
                f"ALTER TABLE {child} "
                f"DROP CONSTRAINT IF EXISTS {constraint}"
            ),
            timeout=60,
        )


def run_prevalidation_child_catalog_probe(scratch, run_id, container):
    child = f"{TARGET_PARTITION}_s1302"
    constraint = "drill_prevalidate_score_check"
    psql(
        container,
        (
            f"ALTER TABLE {child} ADD CONSTRAINT {constraint} "
            "CHECK (score < 0) NOT VALID"
        ),
        timeout=60,
    )
    try:
        completed = run(
            stage_command("validate", scratch, run_id, container),
            timeout=1800,
            check=False,
        )
        if completed.returncode == 0:
            raise DrillError(
                "pre-validation child constraint was accepted"
            )
        if "generation child" not in completed.stderr:
            raise DrillError(
                "pre-validation child rejection was not attributed to "
                "the independent child contract: "
                + completed.stderr
            )
        return {
            "blocked": True,
            "child": child,
            "constraint": constraint,
        }
    finally:
        psql(
            container,
            (
                f"ALTER TABLE {child} "
                f"DROP CONSTRAINT IF EXISTS {constraint}"
            ),
            timeout=60,
        )


def run_commit_to_report_recovery_probe(scratch, run_id, container):
    archive = (
        scratch
        / "archive"
        / f"{TARGET_KEY}-original.custom"
    )
    expected_sha = sha256_path(archive)
    gate = scratch / "reports" / "archive-write-lease-gate"
    gate_paths = {
        phase: {
            suffix: gate.with_name(
                gate.name + f".{phase}.{suffix}"
            )
            for suffix in ("ready", "continue")
        }
        for phase in (
            "pre-ddl",
            "pre-commit",
            "commit-entry",
            "post-commit",
        )
    }
    command = stage_command("drop", scratch, run_id, container)
    command.extend(["--test-final-drop-gate", str(gate)])
    drop = subprocess.Popen(
        command,
        text=True,
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
    )
    writer = None
    try:
        for phase in ("pre-ddl", "pre-commit", "commit-entry"):
            ready = gate_paths[phase]["ready"]
            deadline = time.monotonic() + 120
            while time.monotonic() < deadline and not ready.is_file():
                if drop.poll() is not None:
                    stdout, stderr = drop.communicate()
                    raise DrillError(
                        "drop exited before the write-lease gate: "
                        + stderr
                        + stdout
                    )
                time.sleep(0.05)
            if not ready.is_file():
                raise DrillError(
                    "timed out waiting for write-lease gate " + phase
                )
            if phase == "commit-entry":
                break
            gate_paths[phase]["continue"].write_text(
                "continue\n",
                encoding="ascii",
            )
        writer = subprocess.Popen(
            [
                sys.executable,
                "-c",
                (
                    "import os,sys;"
                    "p=sys.argv[1];"
                    "f=open(p,'r+b',buffering=0);"
                    "f.write(b'corrupted-after-commit');"
                    "os.fsync(f.fileno());"
                    "f.close()"
                ),
                str(archive),
            ],
            text=True,
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
        )
        time.sleep(1)
        if writer.poll() is not None:
            stdout, stderr = writer.communicate()
            raise DrillError(
                "kernel read lease did not block the archive writer: "
                + stderr
                + stdout
            )
        gate_paths["commit-entry"]["continue"].write_text(
            "continue\n",
            encoding="ascii",
        )
        post_commit_ready = gate_paths["post-commit"]["ready"]
        deadline = time.monotonic() + 120
        while (
            time.monotonic() < deadline
            and not post_commit_ready.is_file()
        ):
            if drop.poll() is not None:
                stdout, stderr = drop.communicate()
                raise DrillError(
                    "drop exited before post-commit kill gate: "
                    + stderr
                    + stdout
                )
            time.sleep(0.05)
        if not post_commit_ready.is_file():
            raise DrillError(
                "timed out waiting for post-commit kill gate"
            )
        drop.kill()
        drop.communicate(timeout=30)
        writer_stdout, writer_stderr = writer.communicate(timeout=60)
        if writer.returncode != 0:
            raise DrillError(
                "blocked archive writer did not resume: "
                + writer_stderr
                + writer_stdout
            )
        if (scratch / "reports" / "drop.json").exists():
            raise DrillError(
                "drop report existed before torn-commit recovery"
            )
        psql(
            container,
            (
                f"ALTER TABLE {TARGET_PARTITION} "
                "ALTER COLUMN accuracy SET DEFAULT 123"
            ),
            timeout=60,
        )
        mutated_recovery = run(
            stage_command("drop", scratch, run_id, container),
            timeout=1800,
            check=False,
        )
        if (
            mutated_recovery.returncode == 0
            or (
                "final partitioned catalog is unexpected"
                not in mutated_recovery.stderr
                and "generation child"
                not in mutated_recovery.stderr
            )
        ):
            raise DrillError(
                "finalized recovery accepted a mutated root catalog: "
                + mutated_recovery.stderr
            )
        psql(
            container,
            (
                f"ALTER TABLE {TARGET_PARTITION} "
                "ALTER COLUMN accuracy DROP DEFAULT"
            ),
            timeout=60,
        )
        recovered_result = run_stage(
            "drop",
            scratch,
            run_id,
            container,
        )
        report = json.loads(
            (scratch / "reports" / "drop.json").read_text(
                encoding="utf-8",
            )
        )
        recovery = pathlib.Path(report["archiveRetained"]["path"])
        if (
            not recovery.is_file()
            or os.path.samefile(archive, recovery)
            or sha256_path(recovery) != expected_sha
            or sha256_path(archive) == expected_sha
        ):
            raise DrillError(
                "leased writer did not preserve authoritative recovery"
            )
        for paths in gate_paths.values():
            for path in paths.values():
                with contextlib.suppress(FileNotFoundError):
                    path.unlink()
        return recovered_result, {
            "writerBlockedThroughCommit": True,
            "processKilledAfterCommit": True,
            "committedDropRecoveredFromIndependentCopy": True,
            "mutatedFinalCatalogRejected": True,
            "originalSha256Before": expected_sha,
            "originalSha256After": sha256_path(archive),
            "authoritativeRecovery": report["archiveRetained"],
        }
    finally:
        if drop.poll() is None:
            drop.terminate()
            with contextlib.suppress(subprocess.TimeoutExpired):
                drop.communicate(timeout=10)
        if writer is not None and writer.poll() is None:
            writer.terminate()
            with contextlib.suppress(subprocess.TimeoutExpired):
                writer.communicate(timeout=10)


def run_recovery_path_publication_probe(scratch, run_id, container):
    gate = scratch / "reports" / "recovery-path-publication-gate"
    phases = (
        "pre-ddl",
        "pre-commit",
        "commit-entry",
        "post-commit",
        "report-entry",
    )
    gate_paths = {
        phase: {
            suffix: gate.with_name(
                gate.name + f".{phase}.{suffix}"
            )
            for suffix in ("ready", "continue")
        }
        for phase in phases
    }
    command = stage_command("drop", scratch, run_id, container)
    command.extend(["--test-final-drop-gate", str(gate)])
    drop = subprocess.Popen(
        command,
        text=True,
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
    )
    try:
        for phase in phases:
            ready = gate_paths[phase]["ready"]
            deadline = time.monotonic() + 120
            while time.monotonic() < deadline and not ready.is_file():
                if drop.poll() is not None:
                    stdout, stderr = drop.communicate()
                    raise DrillError(
                        "drop exited before recovery publication gate: "
                        + stderr
                        + stdout
                    )
                time.sleep(0.05)
            if not ready.is_file():
                raise DrillError(
                    "timed out waiting for recovery publication gate "
                    + phase
                )
            if phase == "report-entry":
                break
            gate_paths[phase]["continue"].write_text(
                "continue\n",
                encoding="ascii",
            )
        recovery_evidence = json.loads(
            (
                scratch / "reports" / "drop.recovery.json"
            ).read_text(encoding="utf-8")
        )
        paths = [
            pathlib.Path(path)
            for path in recovery_evidence["copies"]["archive"]["paths"]
        ]
        anchor = next(
            path
            for path in paths
            if "drop-recovery-package" in str(path)
        )
        primary = next(path for path in paths if path != anchor)
        try:
            anchor.unlink()
        except PermissionError:
            anchor_unlink_blocked = True
        else:
            raise DrillError(
                "read-only recovery package allowed anchor unlink"
            )
        primary.unlink()
        original = (
            scratch
            / "archive"
            / f"{TARGET_KEY}-original.custom"
        )
        os.link(original, primary)
        gate_paths["report-entry"]["continue"].write_text(
            "continue\n",
            encoding="ascii",
        )
        stdout, stderr = drop.communicate(timeout=180)
        if drop.returncode != 0:
            raise DrillError(
                "recovery publication drop failed: " + stderr + stdout
            )
        report = json.loads(
            (scratch / "reports" / "drop.json").read_text(
                encoding="utf-8"
            )
        )
        authoritative = pathlib.Path(
            report["archiveRetained"]["path"]
        )
        if (
            not anchor_unlink_blocked
            or authoritative != primary
            or not authoritative.is_file()
            or sha256_path(authoritative)
            != report["archiveRetained"]["sha256"]
            or os.path.samefile(authoritative, original)
        ):
            raise DrillError(
                "durable recovery anchor was not authoritative"
            )
        for paths_by_phase in gate_paths.values():
            for path in paths_by_phase.values():
                with contextlib.suppress(FileNotFoundError):
                    path.unlink()
        return json.loads(stdout), {
            "anchorUnlinkBlocked": True,
            "primaryPathReplacedBySourceHardLink": True,
            "primaryPathRepairedFromAnchor": True,
            "authoritativePath": str(authoritative),
        }
    finally:
        if drop.poll() is None:
            drop.terminate()
            with contextlib.suppress(subprocess.TimeoutExpired):
                drop.communicate(timeout=10)


def run_package_rename_publication_probe(scratch, run_id, container):
    gate = scratch / "reports" / "package-rename-publication-gate"
    phases = (
        "pre-ddl",
        "pre-commit",
        "commit-entry",
        "post-commit",
        "report-entry",
    )
    gate_paths = {
        phase: {
            suffix: gate.with_name(
                gate.name + f".{phase}.{suffix}"
            )
            for suffix in ("ready", "continue")
        }
        for phase in phases
    }
    command = stage_command("drop", scratch, run_id, container)
    command.extend(["--test-final-drop-gate", str(gate)])
    drop = subprocess.Popen(
        command,
        text=True,
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
    )
    try:
        for phase in phases:
            ready = gate_paths[phase]["ready"]
            deadline = time.monotonic() + 120
            while time.monotonic() < deadline and not ready.is_file():
                if drop.poll() is not None:
                    stdout, stderr = drop.communicate()
                    raise DrillError(
                        "drop exited before package-rename gate: "
                        + stderr
                        + stdout
                    )
                time.sleep(0.05)
            if not ready.is_file():
                raise DrillError(
                    "timed out waiting for package-rename gate "
                    + phase
                )
            if phase == "report-entry":
                break
            gate_paths[phase]["continue"].write_text(
                "continue\n",
                encoding="ascii",
            )
        package = (
            scratch
            / "recovered-evidence"
            / "drop-recovery-package"
        )
        renamed = package.with_name(
            "drop-recovery-package-renamed"
        )
        package.rename(renamed)
        drop.kill()
        drop.communicate(timeout=30)
        if (scratch / "reports" / "drop.json").exists():
            raise DrillError(
                "drop report existed before package-rename recovery"
            )
        recovered_result = run_stage(
            "drop",
            scratch,
            run_id,
            container,
        )
        report = json.loads(
            (scratch / "reports" / "drop.json").read_text(
                encoding="utf-8"
            )
        )
        authoritative = pathlib.Path(
            report["archiveRetained"]["path"]
        )
        if (
            not authoritative.is_file()
            or "drop-recovery-package" in str(authoritative)
            or sha256_path(authoritative)
            != report["archiveRetained"]["sha256"]
        ):
            raise DrillError(
                "package rename stranded terminal recovery paths"
            )
        for paths_by_phase in gate_paths.values():
            for path in paths_by_phase.values():
                with contextlib.suppress(FileNotFoundError):
                    path.unlink()
        return recovered_result, {
            "packageRenamedBeforeReport": True,
            "processKilledAfterPackageRename": True,
            "renamedPackageRecoverySucceeded": True,
            "workingRecoveryPathRemainedAuthoritative": True,
            "authoritativePath": str(authoritative),
            "renamedPackage": str(renamed),
        }
    finally:
        if drop.poll() is None:
            drop.terminate()
            with contextlib.suppress(subprocess.TimeoutExpired):
                drop.communicate(timeout=10)


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
    archive_mutation_probes = []
    prevalidation_child_probe = None
    child_catalog_probe = None
    write_lease_probe = None
    recovery_publication_probe = None
    package_rename_probe = None
    drop_report = None
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
        if final_action == "drop":
            prevalidation_child_probe = (
                run_prevalidation_child_catalog_probe(
                    scratch,
                    run_id,
                    container,
                )
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
            run(
                ["docker", "kill", "--signal", "KILL", container],
                timeout=60,
            )
            run(["docker", "start", container], timeout=60)
            wait_ready(container)
            psql(container, "SELECT pg_stat_reset();", timeout=60)
            if final_action == "drop":
                child_catalog_probe = (
                    run_child_catalog_mutation_probe(
                        scratch,
                        run_id,
                        container,
                    )
                )
                for mutation_phase in ("pre-ddl", "pre-commit"):
                    archive_mutation_probes.append(
                        run_archive_mutation_drop_probe(
                            scratch,
                            run_id,
                            container,
                            mutation_phase,
                        )
                    )
                drop_result = run_stage(
                    "drop",
                    scratch,
                    run_id,
                    container,
                )
            elif final_action == "lease-drop":
                drop_result, write_lease_probe = (
                    run_commit_to_report_recovery_probe(
                        scratch,
                        run_id,
                        container,
                    )
                )
            elif final_action == "publish-drop":
                drop_result, recovery_publication_probe = (
                    run_recovery_path_publication_probe(
                        scratch,
                        run_id,
                        container,
                    )
                )
            elif final_action == "rename-drop":
                drop_result, package_rename_probe = (
                    run_package_rename_publication_probe(
                        scratch,
                        run_id,
                        container,
                    )
                )
            else:
                raise DrillError(
                    f"unknown final action: {final_action}"
                )
            stage_results.append(drop_result)
            drop_report = json.loads(
                (
                    scratch
                    / "reports"
                    / "drop.json"
                ).read_text(encoding="utf-8")
            )
            if (
                final_action == "drop"
                and not drop_report.get("fullArchiveReproofRequired")
            ):
                raise DrillError(
                    "drop did not require archive reproof after stats reset"
                )
            if final_action == "drop":
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
        retained_recovery = None
        if final_action in (
            "drop",
            "lease-drop",
            "publish-drop",
            "rename-drop",
        ):
            retained_recovery = drop_report["archiveRetained"][
                "recoveryCopies"
            ]
            recovery_sources = {
                "archive": archive,
                "manifest": (
                    scratch
                    / "archive"
                    / f"{TARGET_KEY}-manifest.json"
                ),
            }
            recovery_sources.update(
                {
                    f"{stage}Report": (
                        scratch / "reports" / f"{stage}.json"
                    )
                    for stage in (
                        "check",
                        "plan",
                        "archive",
                        "restore",
                        "validate",
                    )
                }
            )
            recovery_sources["dropRecoveryManifest"] = (
                scratch / "reports" / "drop.recovery.json"
            )
            for label, recovery in retained_recovery.items():
                recovery_path = pathlib.Path(recovery["path"])
                if (
                    not recovery_path.is_file()
                    or os.path.samefile(
                        recovery_sources[label],
                        recovery_path,
                    )
                    or recovery_path.stat().st_mode & 0o222
                    or sha256_path(recovery_path)
                        != recovery["sha256"]
                ):
                    raise DrillError(
                        "drop did not retain independent recovery copies"
                    )
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
            "archiveMutationProbes": archive_mutation_probes,
            "prevalidationChildCatalogProbe":
                prevalidation_child_probe,
            "childCatalogMutationProbe": child_catalog_probe,
            "writeLeaseProbe": write_lease_probe,
            "recoveryPublicationProbe":
                recovery_publication_probe,
            "packageRenameProbe": package_rename_probe,
            "retainedRecoveryCopies": retained_recovery,
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
            "Run rollback and four guarded final-drop lanes against isolated "
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
        run_lane(
            root,
            "lease-drop",
            final_action="lease-drop",
        ),
        run_lane(
            root,
            "publish-drop",
            final_action="publish-drop",
        ),
        run_lane(
            root,
            "rename-drop",
            final_action="rename-drop",
        ),
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
