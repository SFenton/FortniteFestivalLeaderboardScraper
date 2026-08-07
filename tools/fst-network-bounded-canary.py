#!/usr/bin/env python3
import atexit
import argparse
import base64
import datetime
import hashlib
import json
import os
import pathlib
import secrets
import signal
import subprocess
import time
import urllib.parse
import urllib.request
from urllib.parse import urlparse

from fst_network_canary_logic import (
    VALID_CATEGORY,
    build_payload_control_workload,
    evaluate_gate,
    evaluate_payload_control_pairs,
    plan_distinct_alternates,
)

PROFILES = {
    "candidate-800-32-4": (800, 32, 4),
    "candidate-800-32-5": (800, 32, 5),
    "candidate-800-32-6": (800, 32, 6),
    "candidate-1600-64-8": (1600, 64, 8),
    "candidate-2000-80-10": (2000, 80, 10),
    "candidate-2880-128-16": (2880, 128, 16),
}
TOOLS_DIR = pathlib.Path(__file__).resolve().parent
DEFAULT_TOOLING_PROJECT = (
    TOOLS_DIR / "FstNetworkCanary" / "FstNetworkCanary.csproj"
)
DEFAULT_TOOLING = (
    TOOLS_DIR
    / "FstNetworkCanary"
    / "bin"
    / "Release"
    / "net9.0"
    / "FstNetworkCanary.dll"
)
DEFAULT_COMPOSE_DIR = pathlib.Path("/home/sfenton/Docker/FestivalServiceTracker")
DEFAULT_STORAGE_ROOT = pathlib.Path("/mnt/docker-storage")
DEFAULT_COORDINATION_SENTINEL = (
    DEFAULT_COMPOSE_DIR / ".fst-bounded-network-canary-active.json"
)
FULL_SCRAPE_PAGES = 592849
SCRAPE_1268_PURE_FETCH_SECONDS = 15427.543513


def parse_args():
    parser = argparse.ArgumentParser(
        description="Run one isolated, publication-disabled named network canary."
    )
    parser.add_argument("--network-profile", choices=PROFILES, required=True)
    parser.add_argument("--out-dir", type=pathlib.Path, required=True)
    parser.add_argument("--request-count", type=int, default=3000)
    parser.add_argument("--timeout-seconds", type=int, default=30)
    parser.add_argument("--max-recovery-rounds", type=int, default=3)
    parser.add_argument("--recovery-delay-seconds", type=float, default=0.5)
    parser.add_argument("--payload-control-scope-count", type=int, default=25)
    parser.add_argument(
        "--payload-control-max-start-skew-ms",
        type=float,
        default=250,
    )
    parser.add_argument(
        "--maximum-peak-memory-bytes",
        type=int,
        default=805306368,
    )
    parser.add_argument("--maximum-peak-pids", type=int, default=300)
    parser.add_argument("--prior-useful-rps", type=float, required=True)
    parser.add_argument("--minimum-improvement-percent", type=float, default=10.0)
    parser.add_argument(
        "--calibration-step",
        action="store_true",
        help="Treat this first matched profile as the bounded baseline.",
    )
    parser.add_argument("--compose-dir", type=pathlib.Path, default=DEFAULT_COMPOSE_DIR)
    parser.add_argument("--tooling-dll", type=pathlib.Path, default=DEFAULT_TOOLING)
    parser.add_argument(
        "--tooling-project",
        type=pathlib.Path,
        default=DEFAULT_TOOLING_PROJECT,
    )
    parser.add_argument("--storage-root", type=pathlib.Path, default=DEFAULT_STORAGE_ROOT)
    parser.add_argument(
        "--coordination-sentinel",
        type=pathlib.Path,
        default=DEFAULT_COORDINATION_SENTINEL,
    )
    return parser.parse_args()


def run(*args, input_bytes=None, check=True):
    return subprocess.run(
        [str(arg) for arg in args],
        input=input_bytes,
        check=check,
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
    )


class CoordinationSentinel:
    def __init__(self, path, owner_token):
        self.path = path
        self.owner_token = owner_token
        self.released = False

    @classmethod
    def acquire(cls, path, profile, evidence_dir):
        path = path.resolve()
        path.parent.mkdir(parents=True, exist_ok=True)
        owner_token = secrets.token_hex(16)
        payload = {
            "kind": "fst-bounded-network-canary",
            "profile": profile,
            "pid": os.getpid(),
            "startedAtUtc": datetime.datetime.now(
                datetime.timezone.utc
            ).isoformat(),
            "evidenceDir": str(evidence_dir),
            "ownerToken": owner_token,
        }
        try:
            descriptor = os.open(
                path,
                os.O_WRONLY | os.O_CREAT | os.O_EXCL,
                0o600,
            )
        except FileExistsError as error:
            existing = {}
            try:
                existing = json.loads(path.read_text())
            except (OSError, json.JSONDecodeError):
                pass
            details = {
                key: existing.get(key)
                for key in ("profile", "pid", "startedAtUtc", "evidenceDir")
                if existing.get(key) is not None
            }
            raise RuntimeError(
                f"bounded canary coordination sentinel already exists: "
                f"{path}; owner={details or 'unreadable'}"
            ) from error
        with os.fdopen(descriptor, "w") as stream:
            json.dump(payload, stream, indent=2)
            stream.write("\n")
            stream.flush()
            os.fsync(stream.fileno())
        os.chmod(path, 0o600)
        return cls(path, owner_token)

    def release(self):
        if self.released:
            return
        try:
            payload = json.loads(self.path.read_text())
            if payload.get("ownerToken") == self.owner_token:
                self.path.unlink(missing_ok=True)
        except (FileNotFoundError, json.JSONDecodeError):
            pass
        self.released = True


def worker_is_running():
    process = run(
        "docker",
        "inspect",
        "fstworker",
        "--format",
        "{{.State.Running}}",
        check=False,
    )
    if process.returncode != 0:
        raise RuntimeError(
            "cannot inspect fstworker before bounded canary: "
            + process.stderr.decode(errors="replace").strip()
        )
    value = process.stdout.decode().strip().lower()
    if value not in {"true", "false"}:
        raise RuntimeError(f"unexpected fstworker running state: {value!r}")
    return value == "true"


def install_termination_handler():
    def terminate(signum, _frame):
        raise SystemExit(128 + signum)

    signal.signal(signal.SIGTERM, terminate)


def ensure_tooling(args):
    tooling = args.tooling_dll.resolve()
    if tooling == DEFAULT_TOOLING.resolve():
        project = args.tooling_project.resolve()
        if not project.is_file():
            raise SystemExit(f"canary tooling project is missing: {project}")
        process = run(
            "dotnet",
            "build",
            project,
            "-c",
            "Release",
            "--nologo",
            check=False,
        )
        if process.returncode != 0:
            raise RuntimeError(
                "canary tooling build failed:\n"
                + process.stdout.decode(errors="replace")
                + process.stderr.decode(errors="replace")
            )
    if not tooling.is_file():
        raise SystemExit(f"canary tooling is missing: {tooling}")
    return tooling


def indexed(environment, prefix):
    values = []
    for key, value in environment.items():
        suffix = key.removeprefix(prefix)
        if key.startswith(prefix) and suffix.isdigit():
            values.append((int(suffix), str(value)))
    return [value for _, value in sorted(values)]


def refresh_access_token(environment, service):
    client_id = str(environment.get("EPIC_CLIENT_ID", ""))
    client_secret = str(environment.get("EPIC_CLIENT_SECRET", ""))
    if not client_id or not client_secret:
        raise RuntimeError("Epic client configuration is unavailable")

    data_source = next(
        mount["Source"]
        for mount in service["Mounts"]
        if mount["Destination"] == "/app/data"
    )
    credential_path = pathlib.Path(data_source) / "device-auth.json"
    credentials = json.loads(credential_path.read_text())
    form = urllib.parse.urlencode(
        {
            "grant_type": "refresh_token",
            "refresh_token": credentials["RefreshToken"],
            "token_type": "eg1",
        }
    ).encode()
    basic = base64.b64encode(f"{client_id}:{client_secret}".encode()).decode()
    request = urllib.request.Request(
        "https://account-public-service-prod.ol.epicgames.com/account/api/oauth/token",
        data=form,
        headers={
            "Authorization": f"Basic {basic}",
            "Content-Type": "application/x-www-form-urlencoded",
        },
        method="POST",
    )
    with urllib.request.urlopen(request, timeout=15) as response:
        token = json.loads(response.read())

    next_credentials = {
        "AccountId": token.get("account_id") or credentials["AccountId"],
        "DisplayName": token.get("displayName") or credentials.get("DisplayName", ""),
        "RefreshToken": token["refresh_token"],
        "SavedAt": datetime.datetime.now(datetime.timezone.utc).isoformat(),
    }
    stat = credential_path.stat()
    next_path = credential_path.with_name(".device-auth.bounded-canary-next.json")
    try:
        next_path.write_text(json.dumps(next_credentials, indent=2) + "\n")
        os.chmod(next_path, stat.st_mode & 0o777)
        try:
            os.chown(next_path, stat.st_uid, stat.st_gid)
        except PermissionError:
            pass
        with next_path.open("rb") as stream:
            os.fsync(stream.fileno())
        os.replace(next_path, credential_path)
    finally:
        next_path.unlink(missing_ok=True)
    return token["access_token"], next_credentials["AccountId"]


def build_workload(account_id):
    query = """
    WITH ranked AS (
        SELECT song_id, instrument,
               row_number() OVER (
                   PARTITION BY instrument
                   ORDER BY md5(song_id || ':' || instrument)
               ) AS rn
        FROM leaderboard_published_scope_source
        WHERE published_scrape_id = (
            SELECT published_scrape_id
            FROM scrape_publication_state
            WHERE id = TRUE
        )
          AND source_kind = 'snapshot'
          AND row_count > 0
    )
    SELECT song_id, instrument
    FROM ranked
    WHERE rn <= 25
    ORDER BY instrument, rn;
    """
    result = run(
        "docker",
        "exec",
        "fst-postgres",
        "psql",
        "-X",
        "-v",
        "ON_ERROR_STOP=1",
        "-U",
        "fst",
        "-d",
        "fstservice",
        "-AtF",
        "|",
        "-c",
        query,
    )
    rows = [
        line.split("|")
        for line in result.stdout.decode().splitlines()
        if line.strip()
    ]
    if len(rows) != 225:
        raise RuntimeError(f"expected 225 matched scopes, found {len(rows)}")
    user_agent = (
        "Fortnite/++Fortnite+Release-40.10-CL-52157884 "
        "Windows/10.0.26220.1.256.64bit"
    )
    workload = []
    for song_id, instrument in rows:
        url = (
            "https://events-public-service-live.ol.epicgames.com/api/v1/"
            f"leaderboards/FNFestival/alltime_{song_id}_{instrument}/alltime/"
            f"{account_id}?page=0&rank=0&appId=Fortnite&showLiveSessions=false"
        )
        workload.append(
            {
                "url": url,
                "songId": song_id,
                "instrument": instrument,
                "scopeHash": hashlib.sha256(
                    f"{song_id}|{instrument}|0".encode()
                ).hexdigest()[:16],
            }
        )
    return user_agent, workload


def main():
    args = parse_args()
    if (
        args.request_count <= 0
        or args.timeout_seconds <= 0
        or args.max_recovery_rounds < 0
        or args.recovery_delay_seconds < 0
        or args.payload_control_scope_count <= 0
        or args.payload_control_max_start_skew_ms <= 0
        or args.maximum_peak_memory_bytes <= 0
        or args.maximum_peak_pids <= 0
    ):
        raise SystemExit("canary counts, timeouts, and bounds must be positive")
    output_dir = args.out_dir.resolve()
    storage_root = args.storage_root.resolve()
    if storage_root not in output_dir.parents:
        raise SystemExit(f"output must stay under {storage_root}")
    if output_dir.exists() and any(output_dir.iterdir()):
        raise SystemExit(f"output directory is not empty: {output_dir}")
    output_dir.mkdir(parents=True, exist_ok=True)
    tooling_dll = ensure_tooling(args)
    if worker_is_running():
        raise SystemExit("fstworker must be stopped before a bounded canary")
    sentinel = CoordinationSentinel.acquire(
        args.coordination_sentinel,
        args.network_profile,
        output_dir,
    )
    atexit.register(sentinel.release)
    install_termination_handler()
    if worker_is_running():
        sentinel.release()
        raise SystemExit(
            "fstworker started during bounded-canary sentinel acquisition"
        )

    compose = json.loads(
        run(
            "docker",
            "compose",
            "-f",
            args.compose_dir / "docker-compose.yml",
            "-f",
            args.compose_dir / "docker-compose.pia-30.yml",
            "config",
            "--format",
            "json",
        ).stdout
    )
    environment = compose["services"]["fstworker"]["environment"]
    proxies = [
        urlparse(url).hostname
        for url in indexed(environment, "Scraper__ProxyUrls__")
    ]
    if len(proxies) != 25 or len(set(proxies)) != 25:
        raise SystemExit("effective proxy pool must contain 25 unique nodes")

    service = json.loads(run("docker", "inspect", "fstservice").stdout)[0]
    access_token, account_id = refresh_access_token(environment, service)
    user_agent, workload = build_workload(account_id)
    global_rps, per_exit_rps, per_exit_concurrency = PROFILES[
        args.network_profile
    ]
    stage_name = args.network_profile
    evidence_mount = output_dir.parent
    relative_output = output_dir.relative_to(evidence_mount)
    tooling_mount = tooling_dll.parent
    published_before = run(
        "docker",
        "exec",
        "fst-postgres",
        "psql",
        "-X",
        "-U",
        "fst",
        "-d",
        "fstservice",
        "-AtF",
        "|",
        "-c",
        (
            "SELECT published_scrape_id, public_reads_frozen, "
            "COALESCE((SELECT MAX(id) FROM scrape_log),0) "
            "FROM scrape_publication_state WHERE id=TRUE;"
        ),
    ).stdout.decode().strip()
    network = next(iter(service["NetworkSettings"]["Networks"]))
    image = service["Config"]["Image"]
    public_probe = workload[0]
    public_probe_url = (
        "http://festivalweb/api/leaderboard/"
        f"{urllib.parse.quote(public_probe['songId'], safe='')}/"
        f"{urllib.parse.quote(public_probe['instrument'], safe='')}"
        "?top=10"
    )

    def execute_stage(name, stage_workload, stage_proxies, subdirectory):
        stage_output = output_dir / subdirectory
        stage_output.mkdir(parents=True, exist_ok=True)
        stage = {
            "name": name,
            "routingMode": "fixed-round-robin",
            "perExitRps": per_exit_rps,
            "perExitConcurrency": per_exit_concurrency,
            "globalRps": global_rps,
            "requestCount": len(stage_workload),
            "timeoutSeconds": args.timeout_seconds,
            "cooldownSeconds": 0,
        }
        config = {
            "accessToken": access_token,
            "userAgent": user_agent,
            "proxies": stage_proxies,
            "workload": stage_workload,
            "stages": [stage],
            "outputDir": f"/evidence/{relative_output}/{subdirectory}",
            "scratchDir": f"/evidence/{relative_output}/{subdirectory}/scratch",
            "fullScrapePages": FULL_SCRAPE_PAGES,
            "priorNetworkSeconds": SCRAPE_1268_PURE_FETCH_SECONDS,
            "healthUrls": [
                "http://fstservice:8080/readyz",
                "http://festivalweb/",
                "http://festivalweb/api/service-info",
                public_probe_url,
            ],
        }
        container_name = f"fst-network-canary-{os.getpid()}-{subdirectory}"
        command = [
            "docker",
            "run",
            "--rm",
            "-i",
            "--name",
            container_name,
            "--network",
            network,
            "--cpus",
            "8",
            "--memory",
            "12g",
            "--pids-limit",
            "768",
            "--user",
            f"{os.getuid()}:{os.getgid()}",
            "-v",
            f"{evidence_mount}:/evidence",
            "-v",
            f"{tooling_mount}:/canary-tooling:ro",
            "-e",
            f"DOTNET_CLI_HOME=/evidence/{relative_output}/dotnet-home",
            "--entrypoint",
            "dotnet",
            image,
            f"/canary-tooling/{tooling_dll.name}",
        ]
        process = subprocess.Popen(
            [str(item) for item in command],
            stdin=subprocess.PIPE,
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
        )
        process.stdin.write(json.dumps(config).encode())
        process.stdin.close()
        worker_violation = None
        try:
            while process.poll() is None:
                try:
                    if worker_is_running():
                        worker_violation = {
                            "detectedAtUtc": datetime.datetime.now(
                                datetime.timezone.utc
                            ).isoformat(),
                            "stage": name,
                            "container": container_name,
                            "reason": "fstworker_started_during_bounded_canary",
                        }
                        run(
                            "docker",
                            "stop",
                            "-t",
                            "1",
                            container_name,
                            check=False,
                        )
                        break
                except RuntimeError as error:
                    worker_violation = {
                        "detectedAtUtc": datetime.datetime.now(
                            datetime.timezone.utc
                        ).isoformat(),
                        "stage": name,
                        "container": container_name,
                        "reason": "fstworker_monitor_failed",
                        "error": str(error),
                    }
                    run(
                        "docker",
                        "stop",
                        "-t",
                        "1",
                        container_name,
                        check=False,
                    )
                    break
                time.sleep(0.25)
        except BaseException:
            run(
                "docker",
                "stop",
                "-t",
                "1",
                container_name,
                check=False,
            )
            process.wait()
            raise
        returncode = process.wait()
        stdout = process.stdout.read()
        stderr = process.stderr.read()
        process.stdout.close()
        process.stderr.close()
        (stage_output / "canary.stdout").write_bytes(stdout)
        (stage_output / "canary.stderr").write_bytes(stderr)
        if worker_violation is not None:
            (stage_output / "worker-boundary-violation.json").write_text(
                json.dumps(worker_violation, indent=2) + "\n"
            )
            raise RuntimeError(
                f"worker boundary violation during {name}; see {stage_output}"
            )
        report_path = stage_output / f"{name}.json"
        if returncode != 0 or not report_path.is_file():
            raise RuntimeError(
                f"canary runner failed with exit {returncode}; "
                f"see {stage_output}"
            )
        return json.loads(report_path.read_text()), report_path

    started = datetime.datetime.now(datetime.timezone.utc)
    primary_workload = [
        workload[index % len(workload)] for index in range(args.request_count)
    ]
    report, report_path = execute_stage(
        stage_name,
        primary_workload,
        proxies,
        "primary",
    )
    aggregate = report["aggregate"]
    requests = aggregate["requests"]
    unresolved = []
    recovery_attempts = []
    for result in report["results"]:
        if result["category"] == VALID_CATEGORY:
            continue
        unresolved.append(
            {
                "originalIndex": result["index"],
                "workload": primary_workload[result["index"]],
                "attemptedProxies": [result["proxy"]],
            }
        )
        recovery_attempts.append(
            {
                "originalIndex": result["index"],
                "round": 0,
                "proxy": result["proxy"],
                "scopeHash": result["scopeHash"],
                "category": result["category"],
                "httpStatus": result["httpStatus"],
                "curlExit": result["curlExit"],
            }
        )

    recovery_reports = []
    recovery_report_paths = []
    recovery_delay_seconds = 0.0
    for recovery_round in range(1, args.max_recovery_rounds + 1):
        if not unresolved:
            break
        next_unresolved = []
        for batch_index in range(0, len(unresolved), len(proxies)):
            batch = unresolved[batch_index : batch_index + len(proxies)]
            recovery_proxies = plan_distinct_alternates(proxies, batch)
            subdirectory = (
                f"recovery-{recovery_round}-"
                f"{batch_index // len(proxies) + 1}"
            )
            recovery_report, recovery_report_path = execute_stage(
                f"{stage_name}-{subdirectory}",
                [item["workload"] for item in batch],
                recovery_proxies,
                subdirectory,
            )
            recovery_reports.append(recovery_report)
            recovery_report_paths.append(recovery_report_path)
            for item, result in zip(batch, recovery_report["results"]):
                item["attemptedProxies"].append(result["proxy"])
                recovery_attempts.append(
                    {
                        "originalIndex": item["originalIndex"],
                        "round": recovery_round,
                        "proxy": result["proxy"],
                        "scopeHash": result["scopeHash"],
                        "category": result["category"],
                        "httpStatus": result["httpStatus"],
                        "curlExit": result["curlExit"],
                    }
                )
                if result["category"] != VALID_CATEGORY:
                    next_unresolved.append(item)
        unresolved = next_unresolved
        if unresolved and recovery_round < args.max_recovery_rounds:
            time.sleep(args.recovery_delay_seconds)
            recovery_delay_seconds += args.recovery_delay_seconds

    payload_control_workload = build_payload_control_workload(
        workload,
        args.payload_control_scope_count,
    )
    payload_control_report, payload_control_report_path = execute_stage(
        f"{stage_name}-payload-control",
        payload_control_workload,
        proxies,
        "payload-control",
    )
    payload_control = evaluate_payload_control_pairs(
        payload_control_report["results"],
        args.payload_control_max_start_skew_ms,
    )

    recovery_aggregates = [
        recovery_report["aggregate"]
        for recovery_report in recovery_reports
    ]
    recovered_valid = len(
        {
            item["originalIndex"]
            for item in recovery_attempts
            if item["round"] > 0 and item["category"] == VALID_CATEGORY
        }
    )
    valid = aggregate["valid"] + recovered_valid
    recovery_wire_sends = sum(
        item["requests"] for item in recovery_aggregates
    )
    wire_sends = requests + recovery_wire_sends
    total_wall_seconds = (
        report["wallSeconds"]
        + sum(item["wallSeconds"] for item in recovery_reports)
        + recovery_delay_seconds
    )
    useful_rps = valid / total_wall_seconds if total_wall_seconds else 0
    retry_amplification = wire_sends / requests
    categories = dict(aggregate["categoryCounts"])
    for recovery_aggregate in recovery_aggregates:
        for category, count in recovery_aggregate["categoryCounts"].items():
            categories[category] = categories.get(category, 0) + count
    combined_429_503 = (
        categories.get("rate_limited_429", 0)
        + categories.get("http_503", 0)
    )
    combined_429_503_percent = 100 * combined_429_503 / wire_sends
    preflight_healthy = report["effectiveExits"]
    healthy_after = sum(
        1
        for item in aggregate["perProxy"].values()
        if item["valid"] > 0
        and item["validPercent"] >= 80
        and item["http429"] + item["http503"]
        <= max(1, item["requests"] // 20)
    )
    retained_percent = 100 * healthy_after / preflight_healthy

    minute_counts = {}
    for stage_report in [report, *recovery_reports]:
        for result in stage_report["results"]:
            started_at = result.get("startedAtUtc")
            if not started_at:
                continue
            minute = datetime.datetime.fromisoformat(started_at).replace(
                second=0,
                microsecond=0,
            )
            counts = minute_counts.setdefault(
                minute,
                {"requests": 0, "combined429And503": 0},
            )
            counts["requests"] += 1
            if result["category"] in {"rate_limited_429", "http_503"}:
                counts["combined429And503"] += 1
    minute_windows = []
    consecutive_bad = 0
    three_bad_windows = False
    for minute, counts in sorted(minute_counts.items()):
        percent = (
            100 * counts["combined429And503"] / counts["requests"]
        )
        above = percent > 10
        consecutive_bad = consecutive_bad + 1 if above else 0
        three_bad_windows = three_bad_windows or consecutive_bad >= 3
        minute_windows.append(
            {
                "minuteUtc": minute.isoformat(),
                **counts,
                "combinedPercent": percent,
                "above10Percent": above,
            }
        )

    publication_after = run(
        "docker",
        "exec",
        "fst-postgres",
        "psql",
        "-X",
        "-U",
        "fst",
        "-d",
        "fstservice",
        "-AtF",
        "|",
        "-c",
        (
            "SELECT published_scrape_id, public_reads_frozen, "
            "COALESCE((SELECT MAX(id) FROM scrape_log),0) "
            "FROM scrape_publication_state WHERE id=TRUE;"
        ),
    ).stdout.decode().strip()
    all_reports = [report, *recovery_reports, payload_control_report]
    peak_memory_bytes = max(
        item["resources"]["peakMemoryBytes"] for item in all_reports
    )
    peak_pids = max(item["resources"]["peakPids"] for item in all_reports)
    scratch_bytes_after = sum(
        item["resources"]["scratchBytesAfter"] for item in all_reports
    )
    health_failures = [
        item
        for stage_report in all_reports
        for item in stage_report["healthBefore"] + stage_report["healthAfter"]
        if item.get("status") != 200
    ]
    unrecovered = len(unresolved)
    improvement_percent, gate_reasons = evaluate_gate(
        useful_rps=useful_rps,
        prior_useful_rps=args.prior_useful_rps,
        minimum_improvement_percent=args.minimum_improvement_percent,
        unrecovered=unrecovered,
        retry_amplification=retry_amplification,
        combined_429_503_percent=combined_429_503_percent,
        three_bad_windows=three_bad_windows,
        retained_percent=retained_percent,
        shared_state_unchanged=published_before == publication_after,
        public_health_failures=health_failures,
        payload_control=payload_control,
        peak_memory_bytes=peak_memory_bytes,
        maximum_peak_memory_bytes=args.maximum_peak_memory_bytes,
        peak_pids=peak_pids,
        maximum_peak_pids=args.maximum_peak_pids,
        scratch_bytes_after=scratch_bytes_after,
        calibration_step=args.calibration_step,
    )
    if any(
        not result.get("startedAtUtc")
        for stage_report in [report, *recovery_reports]
        for result in stage_report["results"]
    ):
        gate_reasons.append("minute_window_evidence_incomplete")
    decision = {
        "profile": args.network_profile,
        "routingMode": "fixed-round-robin",
        "startedAtUtc": started.isoformat(),
        "finishedAtUtc": datetime.datetime.now(datetime.timezone.utc).isoformat(),
        "requestCount": requests,
        "wireSends": wire_sends,
        "evidenceWireSendsIncludingPayloadControls": (
            wire_sends + payload_control_report["aggregate"]["requests"]
        ),
        "validResponses": valid,
        "unrecoveredResponses": unrecovered,
        "priorUsefulPagesPerSecond": args.prior_useful_rps,
        "minimumUsefulPagesPerSecond": (
            args.prior_useful_rps
            * (1 + args.minimum_improvement_percent / 100)
        ),
        "primaryUsefulPagesPerSecond": aggregate["usefulPagesPerSecond"],
        "usefulPagesPerSecond": useful_rps,
        "improvementPercent": improvement_percent,
        "improvementGateApplicable": not args.calibration_step,
        "totalWallSeconds": total_wall_seconds,
        "retryAmplification": retry_amplification,
        "combined429And503": combined_429_503,
        "combined429And503Percent": combined_429_503_percent,
        "cdnBlocks": categories.get("cdn_non_json_403", 0),
        "preflightHealthyExits": preflight_healthy,
        "healthyExitsAfter": healthy_after,
        "retainedHealthyExitPercent": retained_percent,
        "peakMemoryBytes": peak_memory_bytes,
        "maximumPeakMemoryBytes": args.maximum_peak_memory_bytes,
        "peakPids": peak_pids,
        "maximumPeakPids": args.maximum_peak_pids,
        "scratchBytesAfter": scratch_bytes_after,
        "observedLiveScopeVariantCount": aggregate["multiVariantScopeCount"],
        "observedLiveScopeVariantsAreGating": False,
        "payloadControl": payload_control,
        "recoveryAttempted": bool(recovery_reports),
        "recoveryRounds": len(
            {item["round"] for item in recovery_attempts if item["round"] > 0}
        ),
        "recoveryRequestCount": recovery_wire_sends,
        "recoveryDelaySeconds": recovery_delay_seconds,
        "recoveryAttempts": recovery_attempts,
        "minuteWindows": minute_windows,
        "threeConsecutiveMinuteWindowsAbove10Percent": three_bad_windows,
        "publishedStateBefore": published_before,
        "publishedStateAfter": publication_after,
        "noSharedStateMutation": published_before == publication_after,
        "coordinationSentinel": str(sentinel.path),
        "toolingDll": str(tooling_dll),
        "toolingSha256": hashlib.sha256(tooling_dll.read_bytes()).hexdigest(),
        "gatePassed": not gate_reasons,
        "gateReasons": gate_reasons,
        "rawPrimaryReport": str(report_path),
        "rawRecoveryReports": [
            str(path) for path in recovery_report_paths
        ],
        "rawPayloadControlReport": str(payload_control_report_path),
    }
    (output_dir / "decision.json").write_text(json.dumps(decision, indent=2) + "\n")
    (output_dir / "workload-manifest.json").write_text(
        json.dumps(
            {
                "profile": args.network_profile,
                "routingMode": "fixed-round-robin",
                "scopeCount": len(workload),
                "requestCount": args.request_count,
                "payloadControlScopeCount": args.payload_control_scope_count,
                "payloadControlRequestCount": len(payload_control_workload),
                "maximumRecoveryRounds": args.max_recovery_rounds,
                "recoveryDelaySeconds": args.recovery_delay_seconds,
                "instruments": sorted({item["instrument"] for item in workload}),
                "scopeHashes": [item["scopeHash"] for item in workload],
                "payloadControlScopeHashes": [
                    payload_control_workload[index]["scopeHash"]
                    for index in range(0, len(payload_control_workload), 2)
                ],
            },
            indent=2,
        )
        + "\n"
    )
    sentinel.release()
    print(json.dumps(decision, indent=2))
    return 0 if decision["gatePassed"] else 2


if __name__ == "__main__":
    raise SystemExit(main())
