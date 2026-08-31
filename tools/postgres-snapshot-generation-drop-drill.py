#!/usr/bin/env python3
"""Disposable PostgreSQL 17 archive/quarantine/drop/restore validation."""

from __future__ import annotations

import argparse
import contextlib
import hashlib
import json
import os
import pathlib
import subprocess
import sys
from datetime import datetime, timezone


TOOL_ID = "fst.snapshot-generation-drop-drill.v3"
REQUIRED_ROOT = pathlib.Path("/mnt/docker-storage")
COLLISION_REGRESSIONS = (
    "ReattachSurvivesFreedIndexNameReuse",
    "QuarantineSurvivesPrivateDestinationIndexCollisions",
    "ReattachRepairsPrePatchIndexNames",
    "QuarantineIndexNameCollisionRollsBackEarlierRename",
    "ReattachIndexRenamesDoNotStrongLockUnrelatedObjects",
    "AdvancedPlannerCycleRejectsDropWithoutResidue",
    "DroppedChildCanBeLogicallyRestoredWithNewIdentity",
    "RestoreToolAuthorizationIsExactImmutableAndIdempotent",
    "AuthorizedRestoreConsumesExactToolAuthorization",
    "ReplacementRestoreToolWithoutExactAuthorizationRejects",
    "EmptyRestoreIdentityUpgradeAllowsCommittedDrop",
    "PythonAcceptsCSharpCanonicalDropPlanBytes",
    "RepairPackageRequiresExactReadOnlyFileSet",
)


class DrillError(RuntimeError):
    pass


def utc_now():
    return datetime.now(timezone.utc).isoformat().replace("+00:00", "Z")


def run(arguments, *, cwd, timeout):
    completed = subprocess.run(
        [str(value) for value in arguments],
        cwd=cwd,
        capture_output=True,
        text=True,
        timeout=timeout,
    )
    if completed.returncode != 0:
        raise DrillError(
            f"command failed ({completed.returncode}): "
            + " ".join(str(value) for value in arguments)
            + "\n"
            + completed.stdout[-4000:]
            + completed.stderr[-4000:]
        )
    return completed


def sha256_path(path):
    digest = hashlib.sha256()
    with pathlib.Path(path).open("rb") as handle:
        for chunk in iter(lambda: handle.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def write_json(path, value):
    data = (
        json.dumps(
            value,
            sort_keys=True,
            separators=(",", ":"),
        )
        + "\n"
    ).encode()
    descriptor = os.open(
        path,
        os.O_WRONLY | os.O_CREAT | os.O_EXCL | os.O_CLOEXEC,
        0o600,
    )
    with os.fdopen(descriptor, "wb") as handle:
        handle.write(data)
        handle.flush()
        os.fsync(handle.fileno())


def main(argv=None):
    parser = argparse.ArgumentParser()
    parser.add_argument("--work-root", required=True)
    parser.add_argument("--image", default="postgres:17")
    args = parser.parse_args(argv)
    if args.image != "postgres:17":
        raise DrillError(
            "the composed archive proof currently requires postgres:17"
        )
    repository = pathlib.Path(__file__).resolve().parent.parent
    work_root = pathlib.Path(args.work_root).resolve(strict=False)
    required = REQUIRED_ROOT.resolve(strict=True)
    if required not in work_root.parents:
        raise DrillError(
            f"work root must be below {required}"
        )
    if work_root.exists():
        raise DrillError(
            f"work root already exists: {work_root}"
        )
    work_root.mkdir(parents=True, mode=0o700)
    started = utc_now()
    try:
        archive = run(
            [
                "python3",
                "tools/postgres-snapshot-generation-archive-drill.py",
            ],
            cwd=repository,
            timeout=7200,
        )
        tests = run(
            [
                "dotnet",
                "test",
                "FSTService.Tests/FSTService.Tests.csproj",
                "-c",
                "Release",
                "--filter",
                (
                    "FullyQualifiedName~"
                    "SnapshotGenerationDrop"
                    "|FullyQualifiedName~"
                    "SnapshotGenerationQuarantineSchemaTests"
                    "|FullyQualifiedName~"
                    "SnapshotGenerationPartitionTests"
                    "|FullyQualifiedName~"
                    "SnapshotGenerationRestoreAuthorizationTests"
                ),
            ],
            cwd=repository,
            timeout=1800,
        )
        restore_tests = run(
            [
                "python3",
                "tools/postgres-snapshot-generation-restore.test.py",
                "-q",
            ],
            cwd=repository,
            timeout=300,
        )
        test_sources = [
            repository
            / "FSTService.Tests"
            / "Unit"
            / "SnapshotGenerationQuarantineSchemaTests.cs",
            repository
            / "FSTService.Tests"
            / "Unit"
            / "SnapshotGenerationRestoreAuthorizationTests.cs",
        ]
        schema_test_text = "\n".join(
            source.read_text(encoding="utf-8")
            for source in test_sources
        )
        missing_regressions = [
            name
            for name in COLLISION_REGRESSIONS
            if f" {name}(" not in schema_test_text
        ]
        if missing_regressions:
            raise DrillError(
                "required collision regressions are absent: "
                + ", ".join(missing_regressions)
            )
        dependency_graph = (
            repository
            / "tools"
            / "FstSnapshotGenerationDrop"
            / "bin"
            / "Release"
            / "net9.0"
            / "FstSnapshotGenerationDrop.deps.json"
        )
        dependency_text = dependency_graph.read_text(
            encoding="utf-8"
        )
        if (
            "Docker.DotNet" in dependency_text
            or '"FSTService/' in dependency_text
        ):
            raise DrillError(
                "DROP deployment dependency graph is not isolated"
            )
        authorizer_dependency_graph = (
            repository
            / "tools"
            / "FstSnapshotGenerationRestoreAuthorization"
            / "bin"
            / "Release"
            / "net9.0"
            / "FstSnapshotGenerationRestoreAuthorization.deps.json"
        )
        authorizer_dependency_text = (
            authorizer_dependency_graph.read_text(
                encoding="utf-8")
        )
        if (
            "Docker.DotNet" in authorizer_dependency_text
            or '"FSTService/' in
                authorizer_dependency_text
        ):
            raise DrillError(
                "restore authorizer dependency graph is not isolated"
            )
        report = {
            "schemaVersion": 3,
            "toolId": TOOL_ID,
            "status": "accepted",
            "startedAtUtc": started,
            "completedAtUtc": utc_now(),
            "postgresImage": args.image,
            "archiveProof": {
                "storageRoot":
                    "/mnt/docker-storage/fst-snapshot-generation-archive-drills",
                "stdoutSha256": hashlib.sha256(
                    archive.stdout.encode()
                ).hexdigest(),
            },
            "dropRestoreTests": {
                "stdoutSha256": hashlib.sha256(
                    tests.stdout.encode()
                ).hexdigest(),
            },
            "restoreToolTests": {
                "stdoutSha256": hashlib.sha256(
                    (
                        restore_tests.stdout
                        + restore_tests.stderr
                    ).encode()
                ).hexdigest(),
            },
            "collisionSequence": {
                "status": "accepted",
                "regressions": list(
                    COLLISION_REGRESSIONS
                ),
                "testSourceSha256": hashlib.sha256(
                    "\n".join(
                        sha256_path(source)
                        for source in test_sources
                    ).encode()
                ).hexdigest(),
                "steps": [
                    "network-none archive and proof",
                    "Q1 quarantine and freed-name rotation collision",
                    "operation-scoped reattach repair",
                    "post-reattach cycle advancement gate",
                    "Q2 exact-child DROP RESTRICT",
                    "simulated 30-minute health evidence",
                    "tool-only repair package exact-set validation",
                    "immutable repair-tool authorization and confirmation",
                    "authorized restore plan resolution",
                    "TABLE and TABLE DATA logical restore",
                    "fixed-index restore with archived-name collision",
                    "authorized attach, attestation, and finalization",
                ],
            },
            "dropDependencyGraphSha256":
                sha256_path(dependency_graph),
            "authorizerDependencyGraphSha256":
                sha256_path(
                    authorizer_dependency_graph),
            "productionTouched": False,
        }
        report_path = work_root / "report.json"
        write_json(report_path, report)
        checksum_path = work_root / "SHA256SUMS"
        descriptor = os.open(
            checksum_path,
            os.O_WRONLY | os.O_CREAT | os.O_EXCL | os.O_CLOEXEC,
            0o600,
        )
        with os.fdopen(descriptor, "w", encoding="utf-8") as handle:
            handle.write(
                f"{sha256_path(report_path)}  report.json\n"
            )
            handle.flush()
            os.fsync(handle.fileno())
        print(json.dumps(report, sort_keys=True))
        return 0
    except Exception as error:
        rejection = {
            "schemaVersion": 3,
            "toolId": TOOL_ID,
            "status": "rejected",
            "startedAtUtc": started,
            "completedAtUtc": utc_now(),
            "reason": str(error),
            "productionTouched": False,
        }
        with contextlib.suppress(Exception):
            write_json(work_root / "rejected.json", rejection)
        print(f"ERROR: {error}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
