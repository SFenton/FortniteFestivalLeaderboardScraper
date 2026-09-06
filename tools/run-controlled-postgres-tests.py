#!/usr/bin/env python3
"""Run FST's ordinary TCP test fixture with owned FST-drive data and cleanup."""
import argparse
import datetime
import hashlib
import json
import os
import pathlib
import shutil
import signal
import subprocess
import time
import uuid
import xml.etree.ElementTree as ET

ROOT = pathlib.Path(__file__).resolve().parents[1]
FST = pathlib.Path("/mnt/docker-storage/Docker/FestivalServiceTracker/fst-data")
LABEL = "fst.controlled-test.scope"
IMAGE = "postgres:17-alpine"
EIGHT = [
    "FSTService.Tests.Integration.StoredRankRolloutHarnessIntegrationTests.Database_identity_rejects_same_named_database_on_a_different_cluster",
    "FSTService.Tests.Integration.StoredRankRolloutHarnessIntegrationTests.Manifest_and_row_harness_cover_the_complete_rollout_matrix",
    "FSTService.Tests.Integration.TierOneReplayIntegrationTests.MissingMarkerRefusesTargetBeforeImportOrOutput",
    "FSTService.Tests.Integration.TierOneReplayIntegrationTests.OptionParityProfilesPreserveOutputAndReduceMemberStatsPasses",
    "FSTService.Tests.Integration.TierOneReplayIntegrationTests.MarkerMismatchAndProductionControlTablesAreRejected",
    "FSTService.Tests.Integration.TierOneReplayIntegrationTests.StaleReplayObjectsRejectFreshImportAndLeaveFailureAttempt",
    "FSTService.Tests.Integration.TierOneReplayIntegrationTests.CancelledPhaseLeavesUnsealedFailedAttempt",
    "FSTService.Tests.Integration.TierOneReplayIntegrationTests.SameInputProducesExactProjectionParityInFreshDatabases",
]


def now():
    return datetime.datetime.now(datetime.timezone.utc).isoformat()


def command(args, **kwargs):
    return subprocess.run(args, capture_output=True, text=True, timeout=120, **kwargs)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--mode", choices=["comparison", "full", "focused"], required=True)
    parser.add_argument("--work-root", required=True)
    parser.add_argument("--filter", default="")
    args = parser.parse_args()
    os.chdir(ROOT)
    work = pathlib.Path(args.work_root).resolve()
    approved = ROOT / "artifacts/offline-retention-report-repair"
    if work.parent != approved or work.exists() or os.stat(ROOT).st_dev != os.stat(FST).st_dev:
        raise RuntimeError("Controlled validation requires a new direct child on the FST drive")
    pathlib.Path("artifacts/offline-retention-report-repair").mkdir(parents=True, exist_ok=True)
    work.relative_to(ROOT).mkdir()
    os.chdir(work)
    scope = str(uuid.uuid4())
    fixture = work / "pgdata"
    scratch = work / "process-workspace"
    base = ROOT / "artifacts/offline-retention-report-repair/base-880802ec"
    environment = dict(os.environ)
    for key in ("FST_TEST_POSTGRES_CONNECTION_STRING", "FST_TEST_POSTGRES_SCOPE"):
        environment.pop(key, None)
    environment.update({
        "FST_TEST_PGDATA_ROOT": str(fixture), "FST_TEST_RESOURCE_SCOPE": scope,
        "TESTCONTAINERS_RYUK_DISABLED": "true",
        "DOTNET_PROCESSOR_COUNT": "2", "DOTNET_CLI_USE_MSBUILD_SERVER": "0",
        "MSBUILDDISABLENODEREUSE": "1", "DOTNET_CLI_TELEMETRY_OPTOUT": "1",
        "DOTNET_NOLOGO": "1", "TMPDIR": str(scratch),
        "DOCKER_HOST": "unix:///var/run/docker.sock",
    })
    comparison_filter = "|".join("FullyQualifiedName=" + name for name in EIGHT)
    runs = [("base-1", base, comparison_filter), ("candidate-1", ROOT, comparison_filter),
            ("base-2", base, comparison_filter), ("candidate-2", ROOT, comparison_filter)] \
        if args.mode == "comparison" else [("candidate", ROOT, args.filter if args.mode == "focused" else "")]
    if args.mode == "comparison":
        helper = pathlib.Path("FSTService.Tests/Helpers/ControlledPostgresTestFixture.cs")
        if (base / helper).read_bytes() != (ROOT / helper).read_bytes():
            raise RuntimeError("Base and candidate resource overlays differ")
    manifest = {"startedAt": now(), "scope": scope, "mode": args.mode,
                "baseCommit": "880802ec7fa3fb9b20561596819766be1b11e66a",
                "ryukDisabled": True, "connectionOverrideUnset": True, "runs": [],
                "productionTouched": False, "allOwnedResourcesRemoved": False}
    manifest["cleanupAttempts"] = []
    manifest["fixtureEvidence"] = []
    invariant = {key: environment[key] for key in (
        "FST_TEST_PGDATA_ROOT", "FST_TEST_RESOURCE_SCOPE", "TESTCONTAINERS_RYUK_DISABLED",
        "DOTNET_PROCESSOR_COUNT", "TMPDIR")}
    manifest["environmentInvariantSha256"] = hashlib.sha256(
        json.dumps(invariant, sort_keys=True).encode()).hexdigest()
    observed = {}
    process = None

    def ids():
        result = command(["docker", "ps", "-aq", "--filter", f"label={LABEL}={scope}"])
        if result.returncode:
            raise RuntimeError("Owned container discovery failed")
        return result.stdout.split()

    def inspect(identity):
        result = command(["docker", "inspect", identity])
        if result.returncode:
            return
        obj = json.loads(result.stdout)[0]
        if obj["Config"]["Labels"].get(LABEL) != scope:
            raise RuntimeError("Test container ownership changed")
        if obj["Config"]["Labels"].get("fst.controlled-test.role") == "cleanup":
            return
        if not obj["Config"]["Image"].startswith("postgres:17"):
            raise RuntimeError("Controlled tests started a non-PostgreSQL17 resource")
        if obj["HostConfig"].get("LogConfig", {}).get("Type") != "none" or obj.get("LogPath"):
            raise RuntimeError("Docker logging could write outside the owned FST-drive scope")
        for mount in obj["Mounts"]:
            if mount["Type"] != "bind" or not pathlib.Path(mount["Source"]).is_relative_to(fixture):
                raise RuntimeError("A test acquired data outside the owned FST-drive root")
        for mappings in obj["HostConfig"].get("PortBindings", {}).values():
            if any(binding.get("HostIp") != "127.0.0.1" for binding in mappings):
                raise RuntimeError("A test port was exposed beyond loopback")
        actual_ports = obj.get("NetworkSettings", {}).get("Ports") or {}
        if obj["State"]["Running"]:
            bindings = actual_ports.get("5432/tcp") or []
            if len(bindings) != 1 or bindings[0].get("HostIp") != "127.0.0.1" \
                    or not 0 < int(bindings[0].get("HostPort") or "0") <= 65535:
                raise RuntimeError("Running fixture has no exact loopback dynamic port")
        observed[identity] = {
            "id": identity, "image": obj["Config"]["Image"], "imageId": obj["Image"],
            "mounts": obj["Mounts"], "portBindings": actual_ports,
            "requestedPortBindings": obj["HostConfig"].get("PortBindings", {}),
            "dockerLogging": obj["HostConfig"].get("LogConfig"), "dockerLogPath": obj.get("LogPath"),
            "role": obj["Config"]["Labels"].get("fst.controlled-test.role")}

    def capture_fixture_evidence():
        if not fixture.exists():
            return
        for instance in sorted(path for path in fixture.iterdir() if path.is_dir()):
            intent_path = instance / "fixture-intent.json"
            started_path = instance / "fixture-started.json"
            if not intent_path.exists():
                raise RuntimeError("An owned fixture directory has no creation intent")
            intent = json.loads(intent_path.read_text())
            started = json.loads(started_path.read_text()) if started_path.exists() else None
            if not started:
                raise RuntimeError("A test fixture lacks authoritative startup inventory")
            if intent["scope"] != scope or started["scope"] != scope:
                raise RuntimeError("Fixture inventory scope mismatch")
            if started["dockerLogging"] != "none" or started["dockerLogPath"] or not started["ryukDisabled"]:
                raise RuntimeError("Fixture logging/reaper inventory is unsafe")
            ports = started.get("portBindings", {}).get("5432/tcp") or []
            if len(ports) != 1 or ports[0].get("HostIp") != "127.0.0.1" \
                    or not 0 < int(ports[0].get("HostPort") or "0") <= 65535:
                raise RuntimeError("Startup inventory omitted the assigned loopback port")
            if not started.get("postgresIdentity", {}).get("systemIdentifier"):
                raise RuntimeError("Startup inventory omitted the PostgreSQL identity")
            logs = []
            log_root = pathlib.Path(intent["postgresLogDirectory"])
            for log in sorted(log_root.rglob("*")) if log_root.exists() else []:
                if log.is_file():
                    if log.is_symlink() or os.stat(log).st_dev != os.stat(FST).st_dev:
                        raise RuntimeError("PostgreSQL log is not an owned regular FST-drive file")
                    logs.append({"path": str(log), "bytes": log.stat().st_size,
                                 "sha256": hashlib.sha256(log.read_bytes()).hexdigest()})
            if not logs:
                raise RuntimeError("No FST-drive PostgreSQL log evidence was captured")
            entry = {"intent": intent, "started": started, "postgresLogs": logs,
                     "fstDevice": os.stat(FST).st_dev,
                     "dataDevice": os.stat(instance / "data").st_dev,
                     "socketDevice": os.stat(instance / "socket").st_dev}
            manifest["fixtureEvidence"].append(entry)

    def stop_process():
        if process is not None and process.poll() is None:
            os.killpg(process.pid, signal.SIGTERM)
            try:
                process.wait(timeout=20)
            except subprocess.TimeoutExpired:
                os.killpg(process.pid, signal.SIGKILL)
                process.wait(timeout=10)

    def cleanup():
        stop_process()
        evidence_error = None
        try:
            capture_fixture_evidence()
        except Exception as error:
            evidence_error = str(error)
        for identity in ids():
            inspect(identity)
            removed = command(["docker", "rm", "-f", "-v", identity])
            if removed.returncode:
                raise RuntimeError("Owned test container removal failed")
        if ids():
            raise RuntimeError("Owned test container absence was not established")
        if fixture.exists():
            for attempt in range(1, 4):
                started = time.monotonic()
                cleared = command([
                    "docker", "run", "--rm", "--network", "none", "--read-only",
                    "--cpus", "1", "--memory", "256m", "--pids-limit", "64",
                    "--label", f"{LABEL}={scope}", "--label", "fst.controlled-test.role=cleanup",
                    "--log-driver", "none",
                    "--mount", f"type=bind,src={fixture},dst=/var/lib/postgresql/data",
                    "--entrypoint", "/bin/sh", IMAGE,
                    "-c", "find /var/lib/postgresql/data -depth -mindepth 1 -delete"])
                manifest["cleanupAttempts"].append({
                    "attempt": attempt, "exitCode": cleared.returncode,
                    "elapsedSeconds": round(time.monotonic() - started, 3),
                    "stdout": cleared.stdout, "stderr": cleared.stderr})
                if cleared.returncode == 0:
                    break
                if ids():
                    raise RuntimeError("A cleanup workload remains; data cleanup cannot be retried")
                time.sleep(2)
            else:
                raise RuntimeError("Owned test data cleanup failed after bounded retries")
            pathlib.Path("pgdata").rmdir()
        if scratch.exists():
            shutil.rmtree("process-workspace")
        if ids():
            raise RuntimeError("Owned cleanup container remains")
        volumes = command(["docker", "volume", "ls", "-q", "--filter", f"label={LABEL}={scope}"])
        if volumes.returncode or volumes.stdout.strip():
            raise RuntimeError("Owned test volume absence was not established")
        if evidence_error:
            raise RuntimeError("Cleanup completed but fixture evidence is incomplete: " + evidence_error)

    def interrupted(signum, _frame):
        raise RuntimeError("Controlled validation interrupted: " + str(signum))

    signal.signal(signal.SIGTERM, interrupted)
    signal.signal(signal.SIGINT, interrupted)
    try:
        for name, source, selected in runs:
            memory = dict(line.split(":", 1) for line in pathlib.Path("/proc/meminfo").read_text().splitlines())
            if int(memory["MemAvailable"].split()[0]) < 8 * 1024**2 or os.getloadavg()[0] > (os.cpu_count() or 1) * .9:
                raise RuntimeError("Host headroom is unsafe for the next controlled run")
            pathlib.Path("pgdata").mkdir()
            pathlib.Path("process-workspace").mkdir()
            pathlib.Path(name).mkdir()
            args_test = ["dotnet", "test", "FSTService.Tests/FSTService.Tests.csproj", "-c", "Release",
                         "--nologo", "-m:1", "-p:UseSharedCompilation=false", "-v:q",
                         "--logger", "console;verbosity=normal",
                         "--logger", "trx;LogFileName=result.trx",
                         "--results-directory", str(work / name)]
            if selected:
                args_test += ["--filter", selected]
            started = time.monotonic()
            with pathlib.Path(name, "console.log").open("w") as output:
                process = subprocess.Popen(args_test, cwd=source, env=environment,
                                           stdout=output, stderr=subprocess.STDOUT, start_new_session=True)
                while process.poll() is None:
                    for identity in ids():
                        inspect(identity)
                    if time.monotonic() - started > 2700:
                        raise RuntimeError("Controlled test deadline exceeded")
                    time.sleep(1)
            root = ET.parse(work / name / "result.trx").getroot()
            counters = root.find(".//{*}Counters").attrib
            total, passed, failed = (int(counters[key]) for key in ("total", "passed", "failed"))
            if args.mode == "comparison" and total != 8:
                raise RuntimeError("The controlled comparison did not run exactly eight tests")
            manifest["runs"].append({"name": name, "source": str(source),
                                     "total": total, "passed": passed, "failed": failed,
                                     "exitCode": process.returncode,
                                     "elapsedSeconds": round(time.monotonic() - started, 3)})
            cleanup()
            print(json.dumps(manifest["runs"][-1]), flush=True)
        manifest["allOwnedResourcesRemoved"] = True
        manifest["outcome"] = "passed" if all(run["failed"] == 0 for run in manifest["runs"]) else "failed"
        if args.mode == "comparison":
            manifest["candidateNoWorseThanBase"] = all(
                manifest["runs"][index+1]["failed"] <= manifest["runs"][index]["failed"] for index in (0, 2))
    except Exception as error:
        manifest.update({"outcome": "failed", "error": str(error)})
    finally:
        signal.signal(signal.SIGTERM, signal.SIG_IGN)
        signal.signal(signal.SIGINT, signal.SIG_IGN)
        try:
            cleanup()
            manifest["allOwnedResourcesRemoved"] = True
        except Exception as error:
            manifest["cleanupError"] = str(error)
        manifest["finishedAt"] = now()
        manifest["containers"] = list(observed.values())
        manifest["artifactDevice"] = os.stat(work).st_dev
        manifest["fstDevice"] = os.stat(FST).st_dev
        pathlib.Path("run.json").write_text(json.dumps(manifest, indent=2) + "\n")
        checksums = []
        for path in sorted(pathlib.Path(".").rglob("*")):
            if path.is_file() and path.name != "SHA256SUMS" and not {"pgdata", "process-workspace"}.intersection(path.parts):
                checksums.append(hashlib.sha256(path.read_bytes()).hexdigest() + "  " + path.as_posix())
        pathlib.Path("SHA256SUMS").write_text("\n".join(checksums) + "\n")
        print(json.dumps({key: value for key, value in manifest.items() if key not in ("containers",)}), flush=True)
    return 0 if manifest.get("outcome") == "passed" and manifest["allOwnedResourcesRemoved"] else 1


if __name__ == "__main__":
    raise SystemExit(main())
