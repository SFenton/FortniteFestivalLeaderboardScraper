#!/usr/bin/env python3
"""Owned, network-none PostgreSQL 17 proof for the offline report entry point."""
import argparse
import datetime
import hashlib
import json
import os
import pathlib
import re
import resource
import shutil
import signal
import subprocess
import time
import uuid
import urllib.error
import urllib.request

from snapshot_retention_schema_proof import run_schema_repair_proof

ROOT = pathlib.Path(__file__).resolve().parents[1]
FST_DATA = pathlib.Path("/mnt/docker-storage/Docker/FestivalServiceTracker/fst-data")
IMAGE = "postgres:17"
LABEL = "fst.offline-retention-report.scope"


def utc():
    return datetime.datetime.now(datetime.timezone.utc).isoformat()


def write(name, value):
    pathlib.Path(name).write_text(json.dumps(value, indent=2) + "\n")


def sha(path):
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        while chunk := stream.read(1024 * 1024):
            digest.update(chunk)
    return digest.hexdigest()


def execute(arguments, *, environment=None, cwd=None, timeout=180, input_text=None):
    process = subprocess.Popen(arguments, env=environment, cwd=cwd, text=True,
                               stdin=subprocess.PIPE if input_text is not None else subprocess.DEVNULL,
                               stdout=subprocess.PIPE, stderr=subprocess.PIPE,
                               start_new_session=True)
    try:
        stdout, stderr = process.communicate(input=input_text, timeout=timeout)
    except BaseException:
        if process.poll() is None:
            os.killpg(process.pid, signal.SIGTERM)
            try:
                process.wait(timeout=15)
            except subprocess.TimeoutExpired:
                os.killpg(process.pid, signal.SIGKILL)
                process.wait(timeout=10)
        raise
    return subprocess.CompletedProcess(arguments, process.returncode, stdout, stderr)


def checked(arguments, **kwargs):
    result = execute(arguments, **kwargs)
    if result.returncode:
        stdout, stderr = result.stdout, result.stderr
        environment = kwargs.get("environment") or {}
        for key in ("ConnectionStrings__PostgreSQL", "FST_TEST_POSTGRES_CONNECTION_STRING",
                    "FST_SNAPSHOT_RETENTION_REPORT_CONNECTION_STRING"):
            secret = environment.get(key)
            if secret:
                stdout = stdout.replace(secret, "[redacted]")
                stderr = stderr.replace(secret, "[redacted]")
        write("owned-command-failure-" + str(time.time_ns()) + ".json", {
            "at": utc(), "executable": str(arguments[0]), "exitCode": result.returncode,
            "stdout": stdout, "stderr": stderr})
        raise RuntimeError("Owned drill command failed: " + str(arguments[0]))
    return result.stdout


def headroom():
    memory = {}
    for line in pathlib.Path("/proc/meminfo").read_text().splitlines():
        key, value = line.split(":", 1)
        memory[key] = int(value.strip().split()[0])
    available = memory["MemAvailable"] * 1024
    load = os.getloadavg()[0]
    free = os.statvfs(".").f_bavail * os.statvfs(".").f_frsize
    if available < 8 * 1024**3 or free < 32 * 1024**3 or load > (os.cpu_count() or 1) * 0.9:
        raise RuntimeError("Host headroom is unsafe for bounded disposable validation")
    return {"observedAt": utc(), "memoryAvailableBytes": available,
            "diskAvailableBytes": free, "load1": load, "logicalProcessors": os.cpu_count()}


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--work-root", required=True)
    parser.add_argument("--hold-for-tests", action="store_true")
    parser.add_argument("--run-focused-tests", action="store_true")
    parser.add_argument("--upgrade-from-880802ec", action="store_true")
    parser.add_argument("--schema-only-repair", action="store_true")
    parser.add_argument("--baseline-service")
    parser.add_argument("--baseline-service-sha256")
    args = parser.parse_args()
    if args.schema_only_repair and args.upgrade_from_880802ec:
        parser.error("Select either the schema repair proof or the legacy reporter upgrade proof")
    baseline_service = pathlib.Path(args.baseline_service).resolve() if args.baseline_service else None
    if baseline_service is not None:
        if not args.schema_only_repair or not baseline_service.is_relative_to(FST_DATA) \
                or baseline_service.is_symlink() or sha(baseline_service) != args.baseline_service_sha256:
            parser.error("A schema repair baseline requires an exact FST-drive service binary hash")
    elif args.baseline_service_sha256:
        parser.error("The baseline hash requires its explicit baseline service")
    os.chdir(ROOT)
    work = pathlib.Path(args.work_root).resolve()
    approved = ROOT / "artifacts/offline-retention-report-drills"
    if work.parent != approved or work.exists() or not work.name.replace("-", "").replace("_", "").isalnum():
        raise RuntimeError("The drill requires a new direct child of artifacts/offline-retention-report-drills")
    if os.stat(ROOT).st_dev != os.stat(FST_DATA).st_dev:
        raise RuntimeError("The worktree and drill must be on the FST drive")
    pathlib.Path("artifacts/offline-retention-report-drills").mkdir(parents=True, exist_ok=True)
    work.relative_to(ROOT).mkdir()
    os.chdir(work)
    write("host-headroom-before.json", headroom())
    for directory in ("postgres-data", "process-workspace", "runtime-bundle"):
        pathlib.Path(directory).mkdir()
    pathlib.Path("postgres-data").chmod(0o777)
    pathlib.Path("postgres-data/logs").mkdir()
    pathlib.Path("postgres-data/logs").chmod(0o777)
    scope = str(uuid.uuid4())
    key = scope.replace("-", "")[:12]
    socket = FST_DATA / ".s" / key
    os.chdir(FST_DATA)
    pathlib.Path(".s").mkdir(exist_ok=True)
    pathlib.Path(".s", key).mkdir()
    pathlib.Path(".s", key).chmod(0o777)
    os.chdir(work)
    if len(str(socket / ".s.PGSQL.5432").encode()) >= 108:
        raise RuntimeError("Owned PostgreSQL socket path is too long")

    environment = dict(os.environ)
    environment.update({
        "DOTNET_NOLOGO": "1", "DOTNET_CLI_TELEMETRY_OPTOUT": "1",
        "DOTNET_SKIP_FIRST_TIME_EXPERIENCE": "1", "DOTNET_PROCESSOR_COUNT": "2",
        "DOTNET_CLI_USE_MSBUILD_SERVER": "0", "MSBUILDDISABLENODEREUSE": "1",
        "TMPDIR": str(work / "process-workspace"),
        "DOTNET_BUNDLE_EXTRACT_BASE_DIR": str(work / "runtime-bundle"),
        "DOTNET_EnableDiagnostics": "0",
    })
    connection = f"Host={socket};Database=fst_offline_report_tests;Username=fst_test;Pooling=false"
    environment["FST_TEST_POSTGRES_CONNECTION_STRING"] = connection
    environment["FST_TEST_POSTGRES_SCOPE"] = scope
    container = None
    cleanup_complete = False
    result = {"startedAt": utc(), "scope": scope, "image": IMAGE,
              "productionTouched": False, "archiveInvoked": False,
              "destructiveSourceOperationInvoked": False}

    def owned_ids(role=None):
        arguments = ["docker", "ps", "-aq", "--filter", f"label={LABEL}={scope}"]
        if role:
            arguments += ["--filter", f"label=fst.offline-retention-report.role={role}"]
        return checked(arguments, timeout=30).split()

    def verify_owned(identity):
        obj = json.loads(checked(["docker", "inspect", identity], timeout=30))[0]
        if obj["Config"]["Labels"].get(LABEL) != scope or obj["HostConfig"]["NetworkMode"] != "none":
            raise RuntimeError("Disposable container identity differs from the owned scope")
        return obj

    def cleanup():
        nonlocal cleanup_complete
        for identity in owned_ids():
            verify_owned(identity)
            checked(["docker", "rm", "-f", "-v", identity], timeout=60)
        if owned_ids():
            raise RuntimeError("Owned container absence was not established")
        logs = []
        for path in sorted(pathlib.Path("postgres-data/logs").glob("*")):
            if path.is_file():
                if path.is_symlink() or os.stat(path).st_dev != os.stat(FST_DATA).st_dev:
                    raise RuntimeError("PostgreSQL log storage escaped the FST drive")
                logs.append({"path": str(work / path), "bytes": path.stat().st_size, "sha256": sha(path)})
        write("postgres-log-storage.json", {"logs": logs, "fstDevice": os.stat(FST_DATA).st_dev})
        checked([
            "docker", "run", "--rm", "--network", "none", "--read-only",
            "--log-driver", "none",
            "--cpus", "1", "--memory", "256m", "--memory-swap", "256m", "--pids-limit", "64",
            "--label", f"{LABEL}={scope}",
            "--label", "fst.offline-retention-report.role=cleanup",
            "--mount", f"type=bind,src={work / 'postgres-data'},dst=/var/lib/postgresql/data",
            "--mount", f"type=bind,src={socket},dst=/owned-socket",
            "--entrypoint", "/bin/sh", IMAGE, "-c",
            "find /var/lib/postgresql/data -mindepth 1 -delete && find /owned-socket -mindepth 1 -delete",
        ], timeout=120)
        if owned_ids():
            raise RuntimeError("Owned cleanup container remains")
        pathlib.Path("postgres-data").rmdir()
        os.chdir(FST_DATA)
        pathlib.Path(".s", key).rmdir()
        os.chdir(work)
        for name in ("process-workspace", "runtime-bundle"):
            path = work / name
            if path.exists():
                if path.is_symlink() or os.stat(path).st_dev != os.stat(FST_DATA).st_dev:
                    raise RuntimeError("Owned process scratch identity changed before cleanup")
                shutil.rmtree(path)
        volumes = checked(["docker", "volume", "ls", "-q", "--filter", f"label={LABEL}={scope}"],
                          timeout=30)
        if volumes.strip():
            raise RuntimeError("Owned volume absence was not established")
        proof = {
            "containersAbsent": not owned_ids(), "volumesAbsent": True,
            "pgdataAbsent": not (work / "postgres-data").exists(),
            "socketAbsent": not socket.exists(),
            "processScratchAbsent": not (work / "process-workspace").exists(),
            "runtimeScratchAbsent": not (work / "runtime-bundle").exists(),
            "retained": ["sealed evidence", "decoy test inputs", "worktree build/native-extraction caches"],
        }
        if any(value is not True for key, value in proof.items() if key != "retained"):
            raise RuntimeError("Owned fixture path absence was not established")
        write("owned-cleanup.json", proof)
        cleanup_complete = True

    def sql(query):
        output = checked(["docker", "exec", "-i", container, "psql", "-X", "-qAt",
                          "-v", "ON_ERROR_STOP=1", "-U", "fst_test",
                          "-d", "fst_offline_report_tests"], input_text=query, timeout=60)
        return output.strip()

    def call_tool(name, arguments, expected_exit=0, env=None):
        invocation = execute([str(ROOT / "tools/postgres-snapshot-generation-retention-report.sh"), *arguments],
                             environment=env or environment, cwd=ROOT, timeout=180)
        if connection in invocation.stdout or connection in invocation.stderr:
            raise RuntimeError("Connection output was suppressed")
        pathlib.Path(name + ".stdout").write_text(invocation.stdout)
        pathlib.Path(name + ".stderr").write_text(invocation.stderr)
        write(name + "-invocation.json", {"observedAt": utc(), "exitCode": invocation.returncode,
                                         "command": arguments[0] if arguments else None})
        if invocation.returncode != expected_exit:
            raise RuntimeError("Offline report command failed its expected outcome: " + name)
        return json.loads(invocation.stdout) if invocation.stdout.strip().startswith("{") else None

    def identity_arguments(identity):
        return [
            "observe-current",
            "--expected-repository-commit", identity["code"]["repositoryCommit"],
            "--expected-repository-tree", identity["code"]["repositoryTree"],
            "--expected-source-sha256", identity["code"]["sourceSha256"],
            "--expected-wrapper-sha256", identity["code"]["wrapperSha256"],
            "--expected-schema-sha256", identity["schemaSha256"],
            "--expected-database-identity-sha256", identity["databaseIdentitySha256"],
            "--expected-worker-configuration-sha256", identity["workerConfiguration"]["configurationSha256"],
        ]

    def interrupted(signum, _frame):
        raise RuntimeError("Owned drill interrupted by signal " + str(signum))

    signal.signal(signal.SIGTERM, interrupted)
    signal.signal(signal.SIGINT, interrupted)
    def validate():
        nonlocal container
        if execute(["docker", "image", "inspect", IMAGE], timeout=30).returncode:
            checked(["docker", "pull", IMAGE], timeout=300)
        container = checked([
            "docker", "run", "-d", "--name", "fst-offline-report-" + key,
            "--network", "none", "--read-only", "--cpus", "2", "--memory", "4g", "--memory-swap", "4g",
            "--log-driver", "none",
            "--pids-limit", "256", "--shm-size", "256m",
            "--label", f"{LABEL}={scope}",
            "--label", "fst.offline-retention-report.role=database",
            "--mount", f"type=bind,src={work / 'postgres-data'},dst=/var/lib/postgresql/data",
            "--mount", f"type=bind,src={socket},dst=/var/run/postgresql",
            "-e", "PGDATA=/var/lib/postgresql/data/pgdata",
            "-e", "POSTGRES_HOST_AUTH_METHOD=trust",
            "-e", "POSTGRES_USER=fst_test", "-e", "POSTGRES_DB=fst_offline_report_tests",
            "-e", "TMPDIR=/var/lib/postgresql/data",
            IMAGE, "-c", "max_connections=500", "-c", "shared_buffers=128MB",
            "-c", "max_parallel_workers=0", "-c", "max_parallel_workers_per_gather=0",
            "-c", "dynamic_shared_memory_type=mmap",
            "-c", "logging_collector=on",
            "-c", "log_directory=/var/lib/postgresql/data/logs",
            "-c", "log_filename=postgresql.log", "-c", "log_file_mode=0644",
            "-c", "fst.offline_report_test_scope=" + scope,
        ], timeout=120).strip()
        obj = verify_owned(container)
        expected_mounts = {
            "/var/lib/postgresql/data": str(work / "postgres-data"),
            "/var/run/postgresql": str(socket),
        }
        actual_mounts = obj.get("Mounts", [])
        if len(actual_mounts) != len(expected_mounts) or any(
            mount.get("Type") != "bind"
            or expected_mounts.get(mount.get("Destination")) != mount.get("Source")
            for mount in actual_mounts
        ):
            raise RuntimeError("Disposable PostgreSQL acquired an unexpected data mount")
        write("owned-mounts.json", actual_mounts)
        if obj["HostConfig"].get("LogConfig", {}).get("Type") != "none" or obj.get("LogPath"):
            raise RuntimeError("Docker logs are not disabled for the owned fixture")
        if obj["HostConfig"]["MemorySwap"] != obj["HostConfig"]["Memory"]:
            raise RuntimeError("The owned fixture must not acquire additional swap capacity")
        write("owned-storage.json", {
            "mounts": actual_mounts, "dockerLogging": obj["HostConfig"]["LogConfig"],
            "dockerLogPath": obj.get("LogPath"), "networkMode": obj["HostConfig"]["NetworkMode"],
            "portBindings": obj["HostConfig"].get("PortBindings"), "ryukUsed": False,
            "memoryBytes": obj["HostConfig"]["Memory"], "memoryAndSwapBytes": obj["HostConfig"]["MemorySwap"],
            "nanoCpus": obj["HostConfig"]["NanoCpus"], "pidsLimit": obj["HostConfig"]["PidsLimit"],
            "fstDevice": os.stat(FST_DATA).st_dev, "artifactDevice": os.stat(work).st_dev,
            "pgdataDevice": os.stat("postgres-data").st_dev, "socketDevice": os.stat(socket).st_dev})
        for _ in range(90):
            ready = execute(["docker", "exec", container, "pg_isready", "-h", "127.0.0.1", "-U", "fst_test",
                             "-d", "fst_offline_report_tests"], timeout=15)
            if ready.returncode == 0:
                break
            time.sleep(1)
        else:
            raise RuntimeError("Owned PostgreSQL did not become ready")
        if sql("SELECT current_setting('fst.offline_report_test_scope');") != scope:
            raise RuntimeError("Owned PostgreSQL server scope mismatch")
        write("runtime.json", {"scope": scope, "containerId": container, "imageId": obj["Image"],
                               "networkMode": "none", "socket": str(socket),
                               "driverPid": os.getpid(),
                               "database": "fst_offline_report_tests", "username": "fst_test"})
        print(json.dumps({"status": "ready", "workRoot": str(work), "scope": scope}), flush=True)
        if args.hold_for_tests:
            deadline = time.monotonic() + 7200
            while not pathlib.Path("continue.requested").exists():
                if pathlib.Path("stop.requested").exists():
                    raise RuntimeError("Validation stopped before the proof")
                if time.monotonic() >= deadline:
                    raise RuntimeError("Bounded development wait expired")
                time.sleep(5)
        write("host-headroom-before-build.json", headroom())
        build = execute([
            "dotnet", "publish",
            "FSTService/FSTService.csproj" if args.schema_only_repair else
            "tools/FstSnapshotGenerationRetentionReport/FstSnapshotGenerationRetentionReport.csproj",
            "-c", "Release", "--nologo", "-m:1", "-p:UseSharedCompilation=false", "-v:q",
        ], environment=environment, cwd=ROOT, timeout=600)
        pathlib.Path("publish.log").write_text(build.stdout + build.stderr)
        if build.returncode:
            raise RuntimeError("Single-file publish failed")
        if args.run_focused_tests:
            tests = execute([
                "dotnet", "test", "FSTService.Tests/FSTService.Tests.csproj", "-c", "Release",
                "--nologo", "-m:1", "-p:UseSharedCompilation=false", "-v:q",
                "--filter", "FullyQualifiedName~SnapshotGenerationRetentionPlannerTests|"
                "FullyQualifiedName~SnapshotGenerationRetentionSchemaTests|"
                "FullyQualifiedName~OfflineReportCommandTests",
            ], environment=environment, cwd=ROOT, timeout=1800)
            pathlib.Path("focused-tests.log").write_text(tests.stdout + tests.stderr)
            if tests.returncode:
                raise RuntimeError("Focused validation failed")

        seed_environment = dict(environment)
        seed_environment["ConnectionStrings__PostgreSQL"] = connection
        current_service = ROOT / "FSTService/bin/Release/net9.0/FSTService.dll"
        initial_service = ROOT / "artifacts/offline-retention-report-repair/base-880802ec/FSTService/bin/Release/net9.0/FSTService.dll" \
            if args.upgrade_from_880802ec else current_service
        initialize = execute([
            "dotnet", str(initial_service),
            "--initialize-schema-only",
        ], environment=seed_environment, cwd=work, timeout=180)
        write("fixture-initialization.json", {"observedAt": utc(), "exitCode": initialize.returncode,
                                             "baseline": "DatabaseInitializer.EnsureSchemaAsync"})
        if initialize.returncode:
            raise RuntimeError("Exact isolated baseline schema initialization failed")
        if args.schema_only_repair:
            seed = execute([
                "dotnet", "run", "--project", "tools/testdata/offline-retention-report-fixture/Fixture.csproj",
                "-c", "Release", "-p:UseSharedCompilation=false", "--", "seed-schema-repair",
            ], environment=environment, cwd=ROOT, timeout=180)
            pathlib.Path("schema-fixture-seed.stdout").write_text(seed.stdout)
            pathlib.Path("schema-fixture-seed.stderr").write_text(seed.stderr)
            if seed.returncode:
                raise RuntimeError("Live-like publication/path/catalog fixture seeding failed")

            def invoke_service(name, arguments, service=None, expected_exit=0, json_output=False):
                target = service or current_service
                started = time.monotonic()
                invocation = execute(["dotnet", str(target), *arguments],
                                     environment=seed_environment, cwd=work, timeout=180)
                if connection in invocation.stdout or connection in invocation.stderr:
                    raise RuntimeError("Service connection output was suppressed")
                pathlib.Path(name + ".stdout").write_text(invocation.stdout)
                pathlib.Path(name + ".stderr").write_text(invocation.stderr)
                write(name + "-invocation.json", {
                    "observedAt": utc(), "exitCode": invocation.returncode,
                    "elapsedSeconds": time.monotonic() - started,
                    "serviceBinary": str(target), "serviceSha256": sha(target),
                    "arguments": arguments})
                if invocation.returncode != expected_exit:
                    raise RuntimeError("Schema command failed its expected outcome: " + name)
                return json.loads(invocation.stdout) if json_output else invocation

            def serve_degraded(name, arguments):
                data_directory = work / (name + "-data")
                data_directory.mkdir()
                for child in ("home", "cache", "config", "data"):
                    (data_directory / child).mkdir()
                runtime_environment = dict(seed_environment)
                runtime_environment.update({
                    "HOME": str(data_directory / "home"),
                    "DOTNET_CLI_HOME": str(data_directory / "home"),
                    "XDG_CACHE_HOME": str(data_directory / "cache"),
                    "XDG_CONFIG_HOME": str(data_directory / "config"),
                    "XDG_DATA_HOME": str(data_directory / "data"),
                    "ASPNETCORE_URLS": "http://127.0.0.1:0",
                    "Api__ApiKey": uuid.uuid4().hex,
                    "Scraper__DataDirectory": str(data_directory),
                    "Scraper__DeviceAuthPath": str(data_directory / "device-auth.json"),
                    "Scraper__ApiOnly": "false",
                    "Scraper__RunOnce": "false",
                    "Scraper__BackfillOnly": "false",
                    "Scraper__DisableScraperWorker": "false",
                    "Scraper__RegistrationSyncWorkerOnly": "false",
                    "Scraper__UsePublicationPathArtifacts": "true",
                    "Scraper__EnableAutomaticPathGeneration": "false",
                    "Scraper__RolloutReadOnlyStartup": "false",
                    "Scraper__RolloutPostgresReadOnly": "false",
                    "DOTNET_USE_POLLING_FILE_WATCHER": "1",
                })
                stdout_path, stderr_path = work / (name + ".stdout"), work / (name + ".stderr")
                with stdout_path.open("w") as stdout, stderr_path.open("w") as stderr:
                    process = subprocess.Popen(
                        ["dotnet", str(current_service), *arguments], env=runtime_environment, cwd=work,
                        stdout=stdout, stderr=stderr, start_new_session=True,
                        preexec_fn=lambda: resource.setrlimit(resource.RLIMIT_CORE, (0, 0)))
                    started = utc()
                    validated = False
                    try:
                        address = None
                        deadline = time.monotonic() + 60
                        info = None
                        while time.monotonic() < deadline:
                            if process.poll() is not None:
                                raise RuntimeError("The ordinary degraded service exited before serving: " + name)
                            ports = re.findall(r"Now listening on: http://127\.0\.0\.1:([1-9][0-9]*)",
                                               stdout_path.read_text())
                            if ports:
                                address = "http://127.0.0.1:" + ports[-1]
                                try:
                                    with urllib.request.urlopen(address + "/api/service-info", timeout=2) as response:
                                        info = json.load(response)
                                    if (info.get("startup") or {}).get("readServingReady") is True:
                                        break
                                except (urllib.error.URLError, TimeoutError, ConnectionError):
                                    pass
                            time.sleep(0.25)
                        else:
                            raise RuntimeError("The ordinary degraded service did not become read-serving")
                        startup = info["startup"]
                        if startup["state"] != "degraded_read_only" or startup["mutationReady"] is not False \
                                or info["postgresDefaultTransactionReadOnly"] is not True \
                                or info["rolloutReadOnlyStartup"] is not False \
                                or info["readOnlyViolationDetected"] is not False:
                            raise RuntimeError("Ordinary startup did not select the explicit database-protected degraded state")
                        expected_suppressed = (
                            {"ImprovementNotificationStalenessMonitor", "PublicationChangeMonitorService",
                             "SongCatalogRefreshWorker"} if "--api-only" in arguments else
                            {"ImprovementNotificationStalenessMonitor", "DurablePhaseProgressBridgeService",
                             "WorkerStatusHeartbeatService", "ScraperWorker", "RegistrationBackfillWorker",
                             "BandRankHistoryWorker"})
                        suppressed = set(re.findall(
                            r"Hosted mutation/background service (\w+) suppressed", stdout_path.read_text()))
                        if suppressed != expected_suppressed:
                            raise RuntimeError("The ordinary degraded host did not suppress every expected background service")
                        requests = []
                        for method, path in (
                            ("GET", "/healthz"), ("GET", "/readyz"), ("GET", "/api/songs"),
                            ("POST", "/api/account/name-refresh"), ("GET", "/api/admin/epic-token"),
                            ("GET", "/api/player/fixture-selected/stats"),
                        ):
                            request = urllib.request.Request(
                                address + path, method=method,
                                headers={"X-FST-Selected-Player": "fixture-selected",
                                         "Content-Type": "application/json"},
                                data=b"{}" if method == "POST" else None)
                            try:
                                with urllib.request.urlopen(request, timeout=5) as response:
                                    status, body = response.status, response.read()
                            except urllib.error.HTTPError as response:
                                status, body = response.code, response.read()
                            requests.append({"method": method, "path": path, "status": status,
                                             "sha256": hashlib.sha256(body).hexdigest(),
                                             "body": body.decode()})
                        if [item["status"] for item in requests] != [200, 200, 200, 503, 503, 503]:
                            write(name + "-requests.json", requests)
                            raise RuntimeError("Degraded public-read/mutation HTTP behavior was incorrect")
                        readiness = json.loads(requests[1]["body"])
                        if readiness["status"] != "Healthy" or readiness["startup"] != startup \
                                or "degraded_read_only" not in readiness["checks"]["database"]["description"]:
                            raise RuntimeError("Read-serving health hid its sticky mode or used the global Degraded status")
                        if requests[2]["body"] != '[{"songId":"schema-repair-song","title":"persisted-read-proof"}]':
                            raise RuntimeError("Degraded GET did not preserve the exact persisted publication cache")
                        write(name + "-requests.json", requests)
                        with urllib.request.urlopen(address + "/api/service-info", timeout=5) as response:
                            final_info = json.load(response)
                        if final_info["readOnlyViolationDetected"] is not False:
                            raise RuntimeError("Degraded blocking was misclassified as a rollout violation")
                        write(name + "-state.json", {"startedAt": started, "pid": process.pid,
                            "arguments": arguments, "address": address, "serviceSha256": sha(current_service),
                            "initial": info, "afterRequests": final_info,
                            "suppressedHostedServices": sorted(suppressed)})
                        validated = True
                    finally:
                        if process.poll() is None:
                            os.killpg(process.pid, signal.SIGTERM)
                            try:
                                process.wait(timeout=20)
                            except subprocess.TimeoutExpired:
                                os.killpg(process.pid, signal.SIGKILL)
                                process.wait(timeout=10)
                        write(name + "-cleanup.json", {"pid": process.pid, "exitCode": process.returncode,
                            "processAbsent": not pathlib.Path(f"/proc/{process.pid}").exists()})
                        if data_directory.exists():
                            shutil.rmtree(data_directory)
                        if validated and (process.returncode != 0 or pathlib.Path(f"/proc/{process.pid}").exists()):
                            raise RuntimeError("Ordinary degraded service did not shut down cleanly")
                if sql("""
                    SELECT count(*) FROM pg_stat_activity WHERE datname=current_database()
                      AND application_name IN ('fstservice-api','fstworker-scraper',
                        'fst-startup-schema-selection','fst-publication-read-lock')
                    """) != "0":
                    raise RuntimeError("Degraded service database ownership remains after shutdown")

            result.update(run_schema_repair_proof(
                sql, invoke_service, write, baseline_service, serve_degraded=serve_degraded))
            return
        sql(SEED_SQL)
        if args.upgrade_from_880802ec:
            legacy_source = json.loads(sql(SOURCE_PARITY_SQL))
            legacy_constraints = json.loads(sql("""
                SELECT jsonb_agg(pg_get_constraintdef(oid) ORDER BY conname)::TEXT
                FROM pg_constraint WHERE conname IN (
                    'ck_snapshot_generation_retention_cycle_safe_point',
                    'ck_snapshot_generation_retention_deferral_safe_point');
                """))
            if any("operator_offline_post_publication" in value for value in legacy_constraints):
                raise RuntimeError("The requested 880802ec baseline was not the legacy contract")
            upgrade = execute(["dotnet", str(current_service), "--initialize-schema-only"],
                              environment=seed_environment, cwd=work, timeout=180)
            if upgrade.returncode or json.loads(sql(SOURCE_PARITY_SQL)) != legacy_source:
                raise RuntimeError("The exact baseline upgrade changed source rows or failed")
            upgraded = json.loads(sql("""
                SELECT jsonb_agg(pg_get_constraintdef(oid) ORDER BY conname)::TEXT
                FROM pg_constraint WHERE conname IN (
                    'ck_snapshot_generation_retention_cycle_safe_point',
                    'ck_snapshot_generation_retention_deferral_safe_point',
                    'ux_snapshot_generation_retention_cycle_trigger');
                """))
            write("baseline-upgrade.json", {"baseCommit": "880802ec7fa3fb9b20561596819766be1b11e66a",
                                            "legacyConstraints": legacy_constraints,
                                            "currentConstraints": upgraded, "sourceRowsUnchanged": True})
        configuration = execute([
            "dotnet", "run", "--project", "tools/testdata/offline-retention-report-fixture/Fixture.csproj",
            "-c", "Release", "-p:UseSharedCompilation=false", "--", "publish-worker-configuration",
        ], environment=environment, cwd=ROOT, timeout=180)
        write("fixture-configuration.json", {"observedAt": utc(), "exitCode": configuration.returncode,
                                            "publisher": "SnapshotGenerationRetentionWorkerConfigurationStore"})
        if configuration.returncode:
            raise RuntimeError("Genuine fixture worker configuration publication failed")
        binding = json.loads(sql(
            "SELECT to_jsonb(validation)::TEXT FROM publication_scope_source_binding_validation(9000) validation;"))
        write("fixture-publication-validation.json", binding)
        if binding["is_valid"] is not True:
            raise RuntimeError("The canonical publication binding fixture is not valid")
        binary = ROOT / "tools/FstSnapshotGenerationRetentionReport/bin/Release/net9.0/linux-x64/publish/FstSnapshotGenerationRetentionReport"
        environment["FST_SNAPSHOT_RETENTION_REPORT_CONNECTION_STRING"] = connection
        environment["FST_SNAPSHOT_RETENTION_REPORT_BINARY_SHA256"] = sha(binary)
        bad_pin = dict(environment)
        bad_pin["FST_SNAPSHOT_RETENTION_REPORT_BINARY_SHA256"] = "0" * 64
        call_tool("wrong-binary-pin", ["inspect"], expected_exit=1, env=bad_pin)
        for key_name in ("LD_PRELOAD", "LD_LIBRARY_PATH", "LD_AUDIT"):
            injected = dict(environment)
            injected[key_name] = str(work / "unapproved-loader")
            call_tool("loader-" + key_name.lower(), ["inspect"], expected_exit=64, env=injected)
        for forbidden in ("archive", "prove", "drop", "restore", "--sql", "--path", "--once"):
            response = call_tool("forbidden-" + forbidden.lstrip("-"), [forbidden], expected_exit=2)
            if response["code"] != "invalid_command_or_arguments":
                raise RuntimeError("A forbidden command reached another surface")
        identity = call_tool("inspect-before", ["inspect"])["runtimeIdentity"]
        pathlib.Path("decoy-path").mkdir()
        for utility in ("git", "sha256sum", "dirname", "stat", "cut", "mkdir"):
            decoy = pathlib.Path("decoy-path", utility)
            decoy.write_text("#!/bin/sh\nprintf decoy > '" + str(work / "decoy-executed") + "'\nexit 99\n")
            decoy.chmod(0o755)
        pathlib.Path("decoy-path/git").write_text(
            "#!/bin/sh\nprintf forged > '" + str(work / "decoy-executed")
            + "'\ncase \"$1\" in rev-parse) printf 'bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb\\n';; status) exit 0;; *) exit 99;; esac\n")
        decoy_environment = dict(environment)
        decoy_environment["PATH"] = str(work / "decoy-path")
        decoy_environment["DOTNET_ROOT"] = str(work / "unapproved-dotnet")
        clean = call_tool("sanitized-path-and-dotnet-root", ["inspect"], env=decoy_environment)
        if pathlib.Path("decoy-executed").exists() or clean["runtimeIdentity"] != identity:
            raise RuntimeError("Loader/path environment isolation failed")
        direct_environment = dict(decoy_environment)
        direct_environment["FST_SNAPSHOT_RETENTION_REPORT_BINARY_PATH"] = str(binary)
        direct = execute([str(binary), "inspect"], environment=direct_environment, cwd=ROOT, timeout=180)
        pathlib.Path("direct-binary-fake-git.stdout").write_text(direct.stdout)
        pathlib.Path("direct-binary-fake-git.stderr").write_text(direct.stderr)
        if direct.returncode or pathlib.Path("decoy-executed").exists() \
                or json.loads(direct.stdout)["runtimeIdentity"] != identity:
            raise RuntimeError("Direct binary repository identity was affected by fake git/PATH")
        write("direct-binary-fake-git.json", {
            "exitCode": direct.returncode, "fakeGitExecuted": False,
            "repositoryIdentityMatches": True, "gitPath": "/usr/bin/git",
            "subprocessPath": "/usr/bin:/bin"})
        if not identity["requiredSchemaAccepted"]:
            raise RuntimeError("The initialized baseline schema was not accepted")
        sql("UPDATE service_worker_status SET status='running';")
        refused = call_tool("online-worker-refusal", identity_arguments(identity), expected_exit=2)
        if refused["code"] != "scraper_not_offline" or sql("SELECT count(*) FROM snapshot_generation_retention_cycles;") != "0":
            raise RuntimeError("Online-worker admission did not fail closed")
        sql("UPDATE service_worker_status SET status='offline',last_status_change_at=now(),last_heartbeat_at=now(),updated_at=now();")
        sql(SOURCE_GUARDS_SQL)
        identity = call_tool("inspect-pinned", ["inspect"])["runtimeIdentity"]
        before = json.loads(sql(SOURCE_PARITY_SQL))
        write("source-before.json", before)
        observed = call_tool("observe-current", identity_arguments(identity))
        if not (observed["disposition"] == "Observed" and observed["status"] == "observed"
                and observed["safePointKind"] == "operator_offline_post_publication"
                and observed["scrapeId"] == 2000 and observed["publicationId"] == 9000
                and observed["plannerVersion"] == 3 and observed["configVersion"] == 1
                and observed["oracleAgreement"] is True and observed["candidateCount"] == 1
                and observed["blockedCount"] == 0 and observed["blockers"] == []):
            raise RuntimeError("A genuine accepted offline cycle was not produced")
        repeated = call_tool("observe-current-again", identity_arguments(identity))
        if repeated["disposition"] != "Existing" or repeated["cycleId"] != observed["cycleId"]:
            raise RuntimeError("The offline report was not idempotent")
        after = json.loads(sql(SOURCE_PARITY_SQL))
        write("source-after.json", after)
        if before != after or sql("SELECT count(*) FROM snapshot_generation_retention_cycles;") != "1":
            raise RuntimeError("Source parity or one-cycle persistence failed")
        result.update({"outcome": "passed", "cycleId": observed["cycleId"],
                       "sourceParity": True, "sourceMutation": False,
                       "realPlannerAndOracleInvoked": True, "oneCycleOnly": True,
                       "binarySha256": sha(binary),
                       "wrapperSha256": sha(ROOT / "tools/postgres-snapshot-generation-retention-report.sh")})
    try:
        validate()
    except Exception as error:
        result.update({"outcome": "failed", "error": str(error)})
    finally:
        signal.signal(signal.SIGTERM, signal.SIG_IGN)
        signal.signal(signal.SIGINT, signal.SIG_IGN)
        try:
            cleanup()
        except Exception as error:
            result.update({"outcome": "failed", "cleanupError": str(error)})
        result.update({"finishedAt": utc(), "ownedScratchAndContainersRemoved": cleanup_complete})
        write("run.json", result)
        files = sorted(path for path in pathlib.Path(".").rglob("*")
                       if path.is_file() and path.name != "SHA256SUMS"
                       and "postgres-data" not in path.parts)
        pathlib.Path("SHA256SUMS").write_text(
            "".join(sha(path) + "  " + path.as_posix() + "\n" for path in files))
        print(json.dumps(result), flush=True)
    return 0 if result.get("outcome") == "passed" and cleanup_complete else 1


SEED_SQL = """
SELECT ensure_leaderboard_snapshot_generation_partition('Solo_Guitar',1307);
INSERT INTO scrape_log(id,started_at,completed_at,status)
VALUES (1307,now()-interval '10 days',now()-interval '9 days','completed'),
       (2000,now()-interval '10 minutes',now()-interval '3 minutes','completed');
INSERT INTO publication_generations(publication_id,scrape_id,status,created_at,ready_at,published_at)
VALUES (9000,2000,'current',now()-interval '3 minutes',now()-interval '2 minutes',now()-interval '2 minutes');
UPDATE scrape_publication_state
SET published_scrape_id=2000,published_at=now()-interval '2 minutes',
    current_publication_id=9000,previous_publication_id=NULL,working_publication_id=NULL,
    public_reads_frozen=FALSE,public_reads_frozen_at=NULL,
    public_reads_frozen_scrape_id=NULL,public_reads_frozen_reason=NULL,
    publication_commit_intent_started_at=NULL,publication_commit_intent_heartbeat_at=NULL,
    publication_commit_intent_owner=NULL,improvement_notifications_status='disabled',
    improvement_notifications_scrape_id=NULL,improvement_notifications_completed_at=NULL,
    improvement_notifications_projection_ready=FALSE,
    improvement_notifications_projection_scrape_id=NULL,updated_at=now()
WHERE id=TRUE;
INSERT INTO leaderboard_published_scope_source(
    published_scrape_id,song_id,instrument,scope_kind,source_kind,
    source_scrape_id,row_count,content_fingerprint,coverage_fingerprint,
    reported_total_entries,reported_total_pages,is_complete,created_at,validated_at)
VALUES(2000,'baseline-empty','Solo_Guitar','alltime','empty',2000,0,
    'empty-content','empty-coverage',0,0,TRUE,now(),now());
UPDATE publication_generations
SET metadata=metadata || jsonb_build_object(
    'publicationPreparation',jsonb_build_object(
        'scrapeId',2000,'publicationId',9000,'expectedPublishedScopeCount',1))
WHERE publication_id=9000;
INSERT INTO publication_surface_bindings(
    publication_id,surface_name,binding_kind,binding_json,row_count,content_hash,status,built_at)
SELECT 9000,'solo_scope_sources','scrape_id',
    jsonb_build_object('publicationId',9000,'table','leaderboard_published_scope_source',
        'publishedScrapeId',2000,'keyHashVersion',1),
    actual_row_count,actual_key_hash,'ready',now()
FROM publication_scope_source_binding_validation(9000);
INSERT INTO service_worker_status(worker_key,status,mode,instance_id,started_at,
    last_status_change_at,last_heartbeat_at,current_operation_json,updated_at)
VALUES('scraper','offline','scraper','owned-offline-drill',now()-interval '1 hour',
    now()-interval '1 second',now()-interval '1 second',NULL,now()-interval '1 second');
INSERT INTO leaderboard_entries_snapshot(snapshot_id,song_id,instrument,account_id,score,first_seen_at,last_updated_at)
SELECT 1307,'fixture-song','Solo_Guitar','fixture-account-'||value,value,
    now()-interval '9 days',now()-interval '9 days' FROM generate_series(1,10) value;
"""

SOURCE_GUARDS_SQL = """
CREATE FUNCTION offline_drill_reject_source_mutation() RETURNS trigger LANGUAGE plpgsql
AS $body$ BEGIN RAISE EXCEPTION 'offline drill source mutation forbidden'; END $body$;
CREATE TRIGGER offline_drill_guard BEFORE INSERT OR UPDATE OR DELETE OR TRUNCATE
ON scrape_log FOR EACH STATEMENT EXECUTE FUNCTION offline_drill_reject_source_mutation();
CREATE TRIGGER offline_drill_guard BEFORE INSERT OR UPDATE OR DELETE OR TRUNCATE
ON scrape_publication_state FOR EACH STATEMENT EXECUTE FUNCTION offline_drill_reject_source_mutation();
CREATE TRIGGER offline_drill_guard BEFORE INSERT OR UPDATE OR DELETE OR TRUNCATE
ON publication_generations FOR EACH STATEMENT EXECUTE FUNCTION offline_drill_reject_source_mutation();
CREATE TRIGGER offline_drill_guard BEFORE INSERT OR UPDATE OR DELETE OR TRUNCATE
ON service_worker_status FOR EACH STATEMENT EXECUTE FUNCTION offline_drill_reject_source_mutation();
CREATE TRIGGER offline_drill_guard BEFORE INSERT OR UPDATE OR DELETE OR TRUNCATE
ON leaderboard_entries_snapshot FOR EACH STATEMENT EXECUTE FUNCTION offline_drill_reject_source_mutation();
CREATE TRIGGER offline_drill_guard BEFORE INSERT OR UPDATE OR DELETE OR TRUNCATE
ON leaderboard_entries_snapshot_solo_guitar_s1307 FOR EACH STATEMENT
EXECUTE FUNCTION offline_drill_reject_source_mutation();
"""

SOURCE_PARITY_SQL = """
SELECT jsonb_build_object(
    'scrapes',(SELECT jsonb_agg(to_jsonb(s) ORDER BY id) FROM scrape_log s),
    'publication',(SELECT to_jsonb(s) FROM scrape_publication_state s WHERE id),
    'generations',(SELECT jsonb_agg(to_jsonb(s) ORDER BY publication_id) FROM publication_generations s),
    'workers',(SELECT jsonb_agg(to_jsonb(s) ORDER BY worker_key) FROM service_worker_status s),
    'source',(SELECT jsonb_build_object('oid',oid,'relfilenode',relfilenode,
        'bytes',pg_total_relation_size(oid)) FROM pg_class
        WHERE oid='public.leaderboard_entries_snapshot_solo_guitar_s1307'::regclass),
    'rowCount',(SELECT count(*) FROM leaderboard_entries_snapshot_solo_guitar_s1307),
    'scoreSum',(SELECT sum(score) FROM leaderboard_entries_snapshot_solo_guitar_s1307)
)::TEXT;
"""


if __name__ == "__main__":
    raise SystemExit(main())
