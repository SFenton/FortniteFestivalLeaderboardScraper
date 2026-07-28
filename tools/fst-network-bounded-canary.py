#!/usr/bin/env python3
import argparse
import base64
import datetime
import hashlib
import json
import os
import pathlib
import subprocess
import urllib.parse
import urllib.request
from urllib.parse import urlparse

PROFILES = {
    "candidate-800-32-4": (800, 32, 4),
    "candidate-1600-64-8": (1600, 64, 8),
    "candidate-2880-128-16": (2880, 128, 16),
}
DEFAULT_TOOLING = pathlib.Path(
    "/mnt/docker-storage/Docker/FestivalServiceTracker/fst-data/evidence/"
    "proxy-retune-disabled-writer-baseline-20260727T004228Z/canary/bin/"
    "ProxyCanary.dll"
)
DEFAULT_COMPOSE_DIR = pathlib.Path("/home/sfenton/Docker/FestivalServiceTracker")
DEFAULT_STORAGE_ROOT = pathlib.Path("/mnt/docker-storage")


def parse_args():
    parser = argparse.ArgumentParser(
        description="Run one isolated, publication-disabled named network canary."
    )
    parser.add_argument("--network-profile", choices=PROFILES, required=True)
    parser.add_argument("--out-dir", type=pathlib.Path, required=True)
    parser.add_argument("--request-count", type=int, default=3000)
    parser.add_argument("--timeout-seconds", type=int, default=30)
    parser.add_argument("--prior-useful-rps", type=float, required=True)
    parser.add_argument("--minimum-improvement-percent", type=float, default=10.0)
    parser.add_argument(
        "--calibration-step",
        action="store_true",
        help="Treat this first matched profile as the bounded baseline.",
    )
    parser.add_argument("--compose-dir", type=pathlib.Path, default=DEFAULT_COMPOSE_DIR)
    parser.add_argument("--tooling-dll", type=pathlib.Path, default=DEFAULT_TOOLING)
    parser.add_argument("--storage-root", type=pathlib.Path, default=DEFAULT_STORAGE_ROOT)
    return parser.parse_args()


def run(*args, input_bytes=None, check=True):
    return subprocess.run(
        [str(arg) for arg in args],
        input=input_bytes,
        check=check,
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
    )


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
                "instrument": instrument,
                "scopeHash": hashlib.sha256(
                    f"{song_id}|{instrument}|0".encode()
                ).hexdigest()[:16],
            }
        )
    return user_agent, workload


def main():
    args = parse_args()
    if args.request_count <= 0 or args.timeout_seconds <= 0:
        raise SystemExit("request count and timeout must be positive")
    output_dir = args.out_dir.resolve()
    storage_root = args.storage_root.resolve()
    if storage_root not in output_dir.parents:
        raise SystemExit(f"output must stay under {storage_root}")
    if output_dir.exists() and any(output_dir.iterdir()):
        raise SystemExit(f"output directory is not empty: {output_dir}")
    output_dir.mkdir(parents=True, exist_ok=True)
    if not args.tooling_dll.is_file():
        raise SystemExit(f"canary tooling is missing: {args.tooling_dll}")

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
    tooling_mount = args.tooling_dll.resolve().parent
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
    def execute_stage(name, stage_workload, stage_proxies, subdirectory):
        stage_output = output_dir / subdirectory
        stage_output.mkdir(parents=True, exist_ok=True)
        stage = {
            "name": name,
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
            "fullScrapePages": 592731,
            "priorNetworkSeconds": 18142.661,
            "healthUrls": [
                "http://fstservice:8080/readyz",
                "http://festivalweb/",
                "http://festivalweb/api/service-info",
            ],
        }
        command = [
            "docker",
            "run",
            "--rm",
            "-i",
            "--name",
            f"fst-network-canary-{os.getpid()}-{subdirectory}",
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
            "/canary-tooling/ProxyCanary.dll",
        ]
        process = run(*command, input_bytes=json.dumps(config).encode(), check=False)
        (stage_output / "canary.stdout").write_bytes(process.stdout)
        (stage_output / "canary.stderr").write_bytes(process.stderr)
        report_path = stage_output / f"{name}.json"
        if process.returncode != 0 or not report_path.is_file():
            raise RuntimeError(
                f"canary runner failed with exit {process.returncode}; "
                f"see {stage_output}"
            )
        return json.loads(report_path.read_text()), report_path

    started = datetime.datetime.now(datetime.timezone.utc)
    report, report_path = execute_stage(
        stage_name,
        [workload[index % len(workload)] for index in range(args.request_count)],
        proxies,
        "primary",
    )
    aggregate = report["aggregate"]
    requests = aggregate["requests"]
    failures = [
        result
        for result in report["results"]
        if result["category"] != "valid_epic_json"
    ]
    recovery_report = None
    recovery_report_path = None
    recovery_fingerprint_mismatches = 0
    if failures:
        failed_workload = [
            workload[result["index"] % len(workload)]
            for result in failures
        ]
        failed_proxies = {result["proxy"] for result in failures}
        recovery_proxies = [
            proxy for proxy in proxies if proxy not in failed_proxies
        ] or proxies
        recovery_report, recovery_report_path = execute_stage(
            f"{stage_name}-recovery",
            failed_workload,
            recovery_proxies,
            "recovery",
        )
        primary_fingerprints = {}
        for result in report["results"]:
            if result["category"] == "valid_epic_json":
                primary_fingerprints.setdefault(result["scopeHash"], set()).add(
                    result["entriesSha256"]
                )
        for result in recovery_report["results"]:
            expected = primary_fingerprints.get(result["scopeHash"], set())
            if (
                result["category"] == "valid_epic_json"
                and expected
                and result["entriesSha256"] not in expected
            ):
                recovery_fingerprint_mismatches += 1

    recovery_aggregate = (
        recovery_report["aggregate"] if recovery_report is not None else None
    )
    recovered_valid = (
        recovery_aggregate["valid"] if recovery_aggregate is not None else 0
    )
    valid = aggregate["valid"] + recovered_valid
    wire_sends = requests + (
        recovery_aggregate["requests"] if recovery_aggregate is not None else 0
    )
    total_wall_seconds = report["wallSeconds"] + (
        recovery_report["wallSeconds"] if recovery_report is not None else 0
    )
    useful_rps = valid / total_wall_seconds if total_wall_seconds else 0
    retry_amplification = wire_sends / requests
    categories = dict(aggregate["categoryCounts"])
    if recovery_aggregate is not None:
        for category, count in recovery_aggregate["categoryCounts"].items():
            categories[category] = categories.get(category, 0) + count
    combined_429_503 = (
        categories.get("rate_limited_429", 0) + categories.get("http_503", 0)
    )
    combined_429_503_percent = 100 * combined_429_503 / wire_sends
    preflight_healthy = report["effectiveExits"]
    healthy_after = sum(
        1
        for item in aggregate["perProxy"].values()
        if item["valid"] > 0
        and item["validPercent"] >= 80
        and item["http429"] + item["http503"] <= max(1, item["requests"] // 20)
    )
    retained_percent = 100 * healthy_after / preflight_healthy
    improvement_percent = 100 * (
        useful_rps - args.prior_useful_rps
    ) / args.prior_useful_rps
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
    fingerprint_variants = (
        aggregate["multiVariantScopeCount"] + recovery_fingerprint_mismatches
    )
    unrecovered = requests - valid
    correctness_failures = (
        unrecovered
        or fingerprint_variants
        or published_before != publication_after
    )
    gate_reasons = []
    if correctness_failures:
        gate_reasons.append("correctness_or_shared_state_difference")
    if (
        not args.calibration_step
        and improvement_percent < args.minimum_improvement_percent
    ):
        gate_reasons.append("useful_rps_improvement_below_10_percent")
    if retry_amplification > 1.50:
        gate_reasons.append("retry_amplification_above_1_50")
    if combined_429_503_percent > 5:
        gate_reasons.append("combined_429_503_above_5_percent")
    if total_wall_seconds >= 180:
        gate_reasons.append("three_consecutive_minute_window_evidence_unavailable")
    if retained_percent < 80:
        gate_reasons.append("healthy_exit_retention_below_80_percent")
    health_failures = [
        item
        for item in (
            report["healthBefore"]
            + report["healthAfter"]
            + (
                recovery_report["healthBefore"] + recovery_report["healthAfter"]
                if recovery_report is not None
                else []
            )
        )
        if item.get("status") != 200
    ]
    if health_failures:
        gate_reasons.append("public_health_failure")
    decision = {
        "profile": args.network_profile,
        "startedAtUtc": started.isoformat(),
        "finishedAtUtc": datetime.datetime.now(datetime.timezone.utc).isoformat(),
        "requestCount": requests,
        "wireSends": wire_sends,
        "validResponses": valid,
        "unrecoveredResponses": unrecovered,
        "priorUsefulPagesPerSecond": args.prior_useful_rps,
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
        "multiVariantScopeCount": fingerprint_variants,
        "recoveryAttempted": recovery_report is not None,
        "recoveryRequestCount": (
            recovery_aggregate["requests"]
            if recovery_aggregate is not None
            else 0
        ),
        "threeConsecutiveMinuteWindowsAbove10Percent": False,
        "publishedStateBefore": published_before,
        "publishedStateAfter": publication_after,
        "noSharedStateMutation": published_before == publication_after,
        "gatePassed": not gate_reasons,
        "gateReasons": gate_reasons,
        "rawPrimaryReport": str(report_path),
        "rawRecoveryReport": (
            str(recovery_report_path)
            if recovery_report_path is not None
            else None
        ),
    }
    (output_dir / "decision.json").write_text(json.dumps(decision, indent=2) + "\n")
    (output_dir / "workload-manifest.json").write_text(
        json.dumps(
            {
                "profile": args.network_profile,
                "scopeCount": len(workload),
                "requestCount": args.request_count,
                "instruments": sorted({item["instrument"] for item in workload}),
                "scopeHashes": [item["scopeHash"] for item in workload],
            },
            indent=2,
        )
        + "\n"
    )
    print(json.dumps(decision, indent=2))
    return 0 if decision["gatePassed"] else 2


if __name__ == "__main__":
    raise SystemExit(main())
