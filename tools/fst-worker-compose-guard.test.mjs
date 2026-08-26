import assert from "node:assert/strict";
import { execFile, spawn } from "node:child_process";
import { once } from "node:events";
import {
  chmod,
  mkdir,
  mkdtemp,
  readFile,
  rm,
  writeFile
} from "node:fs/promises";
import path from "node:path";
import { describe, it } from "node:test";
import { promisify } from "node:util";
import { fileURLToPath } from "node:url";

const execFileAsync = promisify(execFile);
const toolsDirectory = fileURLToPath(new URL("./", import.meta.url));
const repositoryRoot = fileURLToPath(new URL("../", import.meta.url));
const guardPath = fileURLToPath(
  new URL("fst-worker-compose-guard.sh", import.meta.url)
);

const sensitiveValues = [
  "203.0.113.77",
  "test-vpn-user-sensitive",
  "test-vpn-password-sensitive",
  "test-epic-secret-sensitive"
];

const fakeDockerSource = String.raw`#!/usr/bin/env node
import {
  appendFileSync,
  readFileSync,
  writeFileSync
} from "node:fs";
import path from "node:path";

const root = process.env.FST_GUARD_TEST_ROOT;
if (!root) {
  process.stderr.write("missing test root\n");
  process.exit(98);
}

const scenarioPath = path.join(root, "scenario.json");
const runtimePath = path.join(root, "runtime.json");
const eventPath = path.join(root, "events.log");
const configPath = path.join(root, "compose.json");
const scenario = JSON.parse(readFileSync(scenarioPath, "utf8"));
const runtime = JSON.parse(readFileSync(runtimePath, "utf8"));
const args = process.argv.slice(2);

function saveRuntime() {
  writeFileSync(runtimePath, JSON.stringify(runtime));
}

function event(name, values = []) {
  appendFileSync(eventPath, [name, ...values].join("|") + "\n");
}

function workerProfileEnabled(commandArgs) {
  return commandArgs.some((value, index) =>
    value === "--profile=worker"
    || (value === "--profile" && commandArgs[index + 1] === "worker")
  );
}

function containerState(name) {
  if (name === "fst-postgres" || name === "postgres") {
    return scenario.postgresState ?? "running|healthy";
  }
  if (name === "fstservice") {
    return scenario.serviceState ?? "running|healthy";
  }
  if (name === "fstworker") {
    return runtime.workerState;
  }
  if (/^pia-gluetun-\d+$/.test(name)) {
    return runtime.proxyStates[name] ?? "running|healthy";
  }
  return null;
}

function serviceInfo() {
  const afterWorkerStart = runtime.workerStarted;
  const workerReady =
    afterWorkerStart && scenario.workerBecomesReady !== false;
  return {
    currentUpdate: {
      status: afterWorkerStart
        ? scenario.postStartCurrentUpdateStatus
          ?? scenario.currentUpdateStatus
          ?? "idle"
        : scenario.currentUpdateStatus ?? "idle",
      scrapeId: scenario.currentScrapeId ?? 1305
    },
    publication: {
      publicReadsFrozen: afterWorkerStart
        ? scenario.postStartPublicReadsFrozen
          ?? scenario.publicReadsFrozen
          ?? false
        : scenario.publicReadsFrozen ?? false,
      publishedScrapeId: scenario.publishedScrapeId ?? 1304,
      freezeReason: scenario.freezeReason ?? "post-process"
    },
    workerStatus: workerReady
      ? {
          status: "online",
          instanceId: "new-worker-instance",
          lastHeartbeatAt: "2026-08-11T20:39:00Z",
          heartbeatAgeSeconds: 1,
          staleAfterSeconds: 90
        }
      : {
          status: scenario.initialWorkerApiStatus ?? "offline",
          instanceId: "old-worker-instance",
          lastHeartbeatAt: "2026-08-11T19:00:00Z",
          heartbeatAgeSeconds: 3600,
          staleAfterSeconds: 90
        }
  };
}

if (args[0] === "compose") {
  if (args.includes("config")) {
    const config = JSON.parse(readFileSync(configPath, "utf8"));
    if (!workerProfileEnabled(args)) {
      delete config.services?.fstworker;
    }
    writeFileSync(1, JSON.stringify(config));
    process.exit(0);
  }

  const upIndex = args.indexOf("up");
  if (upIndex >= 0) {
    const services = args
      .slice(upIndex + 1)
      .filter((value) => !value.startsWith("-"));
    if (services.includes("fstworker")) {
      if (!workerProfileEnabled(args)) {
        process.stderr.write("worker profile was not explicitly enabled\n");
        process.exit(96);
      }
      event("worker-start", ["fstworker"]);
      runtime.workerStarted = true;
      runtime.workerState = "running|healthy";
      saveRuntime();
      process.exit(scenario.workerStartFails ? 1 : 0);
    }
    if (workerProfileEnabled(args)) {
      process.stderr.write("proxy-only recreate enabled the worker profile\n");
      process.exit(95);
    }

    event("proxy-recreate", services);
    const persistent = new Set(scenario.persistentUnhealthy ?? []);
    for (const service of services) {
      if (!persistent.has(service)) {
        runtime.proxyStates[service] = "running|healthy";
      }
    }
    saveRuntime();
    process.exit(scenario.proxyRecreateFails ? 1 : 0);
  }
}

if (args[0] === "inspect") {
  const name = args.at(-1);
  const state = containerState(name);
  if (state == null) {
    process.exit(1);
  }
  if (args.includes("--format")) {
    process.stdout.write(state);
  } else {
    process.stdout.write("{}");
  }
  process.exit(0);
}

if (args[0] === "exec") {
  const container = args[1];
  const commandArgs = args.slice(2);
  const joined = commandArgs.join(" ");

  if (container === "fstservice" && joined.includes("/readyz")) {
    process.exit(scenario.serviceReady === false ? 1 : 0);
  }
  if (container === "fstservice" && joined.includes("/api/service-info")) {
    process.stdout.write(JSON.stringify(serviceInfo()));
    process.exit(0);
  }
  if (joined.includes("/v1/vpn/status")) {
    process.stdout.write('{"status":"running"}');
    process.exit(0);
  }
  if (joined.includes("https://api.ipify.org")) {
    const proxyIndex = commandArgs.indexOf("-x");
    if (proxyIndex < 0) {
      process.stdout.write("192.0.2.1");
      process.exit(0);
    }
    const proxy = commandArgs[proxyIndex + 1] ?? "";
    const match = proxy.match(/pia-gluetun-(\d+)/);
    if (!match) {
      process.exit(1);
    }
    process.stdout.write(
      scenario.duplicateEgress
        ? "198.51.100.1"
        : "198.51.100." + match[1]
    );
    process.exit(0);
  }
  if (/^pia-gluetun-\d+$/.test(container) && commandArgs[0] === "sh") {
    process.exit(0);
  }
}

if (args[0] === "stop" && args.at(-1) === "fstworker") {
  event("worker-stop", ["fstworker"]);
  runtime.workerStarted = false;
  runtime.workerState = "exited|none";
  saveRuntime();
  process.exit(0);
}

process.stderr.write("unexpected docker invocation: " + args.join(" ") + "\n");
process.exit(97);
`;

function buildComposeConfig({
  effectiveCount = 2,
  pinnedEffectiveIp = null,
  runOnce = false,
  restartPolicy = null,
  workerProfiles = ["worker"],
  workerImage = "example.invalid/fstworker:test"
} = {}) {
  const workerEnvironment = {
    Scraper__ExpectedProxyEndpointCount: String(effectiveCount),
    Scraper__CanonicalProxyServiceCount: "30",
    Scraper__MaxRequestsPerSecond: "800",
    Scraper__ProxyMaxRequestsPerSecondPerEndpoint: "32",
    Scraper__ProxyMaxConcurrentRequestsPerEndpoint: "4",
    Scraper__ProxyDisableConnectionReuse: "true",
    Scraper__ProxyUseCurlTransport: "true",
    Scraper__ProxyCurlTempDirectory: "/app/data/curl-transport",
    Scraper__InitialDop: "4",
    Scraper__DegreeOfParallelism: "64",
    Scraper__PageConcurrency: "10",
    EPIC_CLIENT_SECRET: sensitiveValues[3]
  };
  if (runOnce) {
    workerEnvironment.Scraper__RunOnce = "true";
  }
  const dependsOn = {
    postgres: { condition: "service_healthy" },
    fstservice: { condition: "service_healthy" }
  };
  const services = {
    postgres: {
      container_name: "fst-postgres"
    },
    fstservice: {
      container_name: "fstservice"
    },
    fstworker: {
      container_name: "fstworker",
      image: workerImage,
      restart: restartPolicy ?? (runOnce ? "no" : "on-failure:5"),
      profiles: workerProfiles,
      environment: workerEnvironment,
      depends_on: dependsOn
    }
  };

  for (let index = 1; index <= 30; index += 1) {
    const name = `pia-gluetun-${index}`;
    const environment = {
      VPN_SERVICE_PROVIDER: "private internet access",
      OPENVPN_USER: sensitiveValues[1],
      OPENVPN_PASSWORD: sensitiveValues[2]
    };
    if (index === 1 && pinnedEffectiveIp != null) {
      environment.OPENVPN_ENDPOINT_IP = pinnedEffectiveIp;
    }
    services[name] = {
      container_name: name,
      environment
    };
  }

  for (let index = 0; index < effectiveCount; index += 1) {
    const name = `pia-gluetun-${index + 1}`;
    workerEnvironment[`Scraper__ProxyUrls__${index}`] =
      `http://${name}:8888`;
    workerEnvironment[`Scraper__ControlUrls__${index}`] =
      `http://${name}:8000`;
    workerEnvironment[`Scraper__VpnProviders__${index}`] =
      "Private Internet Access";
    workerEnvironment[`Scraper__ContainerNames__${index}`] = name;
    dependsOn[name] = { condition: "service_healthy" };
  }

  return { services };
}

function buildRunonceComposeConfig() {
  const config = buildComposeConfig({ runOnce: true });
  Object.assign(config.services.fstworker.environment, {
    Scraper__EnabledPhases: "None",
    Scraper__RegisteredUserRefreshTimeout: "00:00:00",
    Scraper__RegisteredPlayerBandDiscoveryTimeout: "00:06:00",
    Scraper__RegisteredBandTargetedProcessingTimeout: "00:05:00",
    Scraper__RegisteredPlayerBandDiscoveryMaxLookupsPerPass: "80",
    Scraper__RegisteredBandProcessingMaxLookupsPerPass: "80",
    ImprovementNotifications__Enabled: "true",
    ImprovementNotifications__Scope: "registered",
    ImprovementNotifications__IncludePlayers: "true",
    ImprovementNotifications__IncludeBands: "true",
    ImprovementNotifications__IncludeSongEvents: "true",
    ImprovementNotifications__IncludeRankings: "true",
    ImprovementNotifications__RefreshSoloProjection: "true",
    ImprovementNotifications__RefreshAllSoloScopesWhenNoImpactedScopes: "false"
  });
  return config;
}

function buildPublicationCacheRunonceConfig({
  useLeaderboardScopeFingerprints = true
} = {}) {
  const config = buildComposeConfig({ runOnce: true });
  Object.assign(config.services.fstworker.environment, {
    Scraper__EnabledPhases: "All",
    Features__EnforcePublicationCriticalPhases: "true",
    Features__EnforceScopeCompletenessManifests: "true",
    Features__RequireSuccessfulScrapeWriters: "true",
    Features__UseLeaderboardScopeFingerprints:
      String(useLeaderboardScopeFingerprints),
    Features__WritePublishedScopeSources: "true",
    Features__UseStoredSoloProjectionRanksForFilteredReads: "false",
    Features__SkipUnchangedPhysicalLeaderboardSnapshots: "false"
  });
  return config;
}

function buildLeaderboardRivalsBatchRunonceConfig({
  accountBatchSize = "4",
  rivalsMaxDegreeOfParallelism = "2"
} = {}) {
  const config = buildComposeConfig({ runOnce: true });
  Object.assign(config.services.fstworker.environment, {
    Scraper__EnabledPhases: "All",
    Scraper__RegisteredUserRefreshTimeout: "00:00:00",
    Scraper__RivalsMaxDegreeOfParallelism:
      rivalsMaxDegreeOfParallelism,
    Scraper__LeaderboardRivalsMaxDegreeOfParallelism:
      accountBatchSize,
    Scraper__UsePublicationPathArtifacts: "true",
    Scraper__EnableScrapePassPathGeneration: "true",
    Scraper__EnableAutomaticPathGeneration: "false",
    Features__EnforcePublicationCriticalPhases: "true",
    Features__EnforceScopeCompletenessManifests: "true",
    Features__RequireSuccessfulScrapeWriters: "true",
    Features__UseLeaderboardScopeFingerprints: "true",
    Features__WritePublishedScopeSources: "true",
    Features__SkipUnchangedPhysicalLeaderboardSnapshots: "true",
    Features__UseStoredSoloProjectionRanksForFilteredReads: "false",
    Features__WriteLogicalLeaderboardVersions: "false",
    DatabaseMaintenance__SnapshotRetentionRewriteEnabled: "false",
    ImprovementNotifications__Enabled: "true",
    ImprovementNotifications__IncludePlayers: "true",
    ImprovementNotifications__IncludeBands: "true",
    ImprovementNotifications__IncludeSongEvents: "true",
    ImprovementNotifications__IncludeRankings: "true"
  });
  return config;
}

function buildScrapeResumeRunonceConfig({
  resumeScrapeId = "1305",
  resumeTotalBytes = "59082543837",
  rivalsMaxDegreeOfParallelism = "2"
} = {}) {
  const config = buildComposeConfig({ runOnce: true });
  Object.assign(config.services.fstworker.environment, {
    Scraper__EnabledPhases: "SoloRankings",
    Scraper__RegistrationSyncWorkerOnly: "false",
    Scraper__RegisteredUserRefreshTimeout: "00:00:00",
    Scraper__ResumeScrapeId: resumeScrapeId,
    Scraper__ResumeSongsScraped: "704",
    Scraper__ResumeTotalEntries: "40764011",
    Scraper__ResumeTotalRequests: "409088",
    Scraper__ResumeTotalBytes: resumeTotalBytes,
    Scraper__ResumeEpicReportedOver100Pages: "false",
    Scraper__RivalsMaxDegreeOfParallelism:
      rivalsMaxDegreeOfParallelism,
    Features__EnforcePublicationCriticalPhases: "true",
    Features__EnforceScopeCompletenessManifests: "true",
    Features__RequireSuccessfulScrapeWriters: "true",
    Features__UseLeaderboardScopeFingerprints: "true",
    Features__WritePublishedScopeSources: "true",
    Features__SkipUnchangedPhysicalLeaderboardSnapshots: "true",
    Features__UseStoredSoloProjectionRanksForFilteredReads: "false",
    Features__WriteLogicalLeaderboardVersions: "false",
    DatabaseMaintenance__SnapshotRetentionRewriteEnabled: "false"
  });
  return config;
}

async function createHarness({
  config = buildComposeConfig(),
  scenario = {}
} = {}) {
  const root = await mkdtemp(
    path.join(toolsDirectory, ".fst-worker-compose-guard-test-")
  );
  const binDirectory = path.join(root, "bin");
  const dockerPath = path.join(binDirectory, "docker");
  const lockPath = path.join(root, ".fst-worker-compose-guard.lock");
  const eventsPath = path.join(root, "events.log");

  await mkdir(binDirectory);
  await Promise.all([
    writeFile(path.join(root, "docker-compose.yml"), "services: {}\n"),
    writeFile(path.join(root, "docker-compose.pia-30.yml"), "services: {}\n"),
    writeFile(path.join(root, "docker-compose.runonce.yml"), "services: {}\n"),
    writeFile(path.join(root, "compose.json"), JSON.stringify(config)),
    writeFile(path.join(root, "scenario.json"), JSON.stringify(scenario)),
    writeFile(
      path.join(root, "runtime.json"),
      JSON.stringify({
        workerStarted: false,
        workerState: scenario.workerContainerState ?? "exited|none",
        proxyStates: scenario.proxyStates ?? {}
      })
    ),
    writeFile(dockerPath, fakeDockerSource)
  ]);
  await chmod(dockerPath, 0o755);

  const environment = {
    ...process.env,
    PATH: `${binDirectory}:${process.env.PATH}`,
    COMPOSE_DIR: root,
    FST_GUARD_TEST_ROOT: root,
    FST_WORKER_COMPOSE_GUARD_LOCK_PATH: "",
    FST_WORKER_RECOVERY_CORE_WAIT_SECONDS: "0",
    FST_WORKER_RECOVERY_INITIAL_WAIT_SECONDS: "0",
    FST_WORKER_RECOVERY_RECREATE_WAIT_SECONDS: "0",
    FST_WORKER_RECOVERY_WORKER_WAIT_SECONDS: "0",
    FST_WORKER_RECOVERY_TOTAL_DEADLINE_SECONDS: "30",
    FST_WORKER_RECOVERY_POLL_INTERVAL_SECONDS: "1",
    FST_WORKER_RECOVERY_MAX_PROXY_RECREATES: "3",
    FST_WORKER_RECOVERY_HEARTBEAT_FRESH_SECONDS: "30",
    FST_WORKER_RECOVERY_WORKER_STOP_TIMEOUT_SECONDS: "0"
  };

  return {
    root,
    dockerPath,
    lockPath,
    async run(args = ["--recover-start"], overrides = {}) {
      try {
        const result = await execFileAsync(guardPath, args, {
          cwd: repositoryRoot,
          env: { ...environment, ...overrides },
          encoding: "utf8",
          maxBuffer: 1024 * 1024
        });
        return {
          code: 0,
          stdout: result.stdout,
          stderr: result.stderr
        };
      } catch (error) {
        return {
          code: error.code,
          stdout: error.stdout ?? "",
          stderr: error.stderr ?? ""
        };
      }
    },
    spawnGuard(args = ["--recover-start"], overrides = {}) {
      return spawn(guardPath, args, {
        cwd: repositoryRoot,
        env: { ...environment, ...overrides },
        stdio: ["ignore", "pipe", "pipe"]
      });
    },
    async events() {
      try {
        const content = await readFile(eventsPath, "utf8");
        return content.trim().split("\n").filter(Boolean);
      } catch (error) {
        if (error.code === "ENOENT") {
          return [];
        }
        throw error;
      }
    },
    async cleanup() {
      await rm(root, { recursive: true, force: true });
    }
  };
}

async function waitFor(predicate, timeoutMilliseconds = 5000) {
  const deadline = Date.now() + timeoutMilliseconds;
  while (Date.now() < deadline) {
    if (await predicate()) {
      return;
    }
    await new Promise((resolve) => setTimeout(resolve, 20));
  }
  throw new Error("Timed out waiting for test condition.");
}

function composeServiceBlock(compose, serviceName) {
  const marker = `\n  ${serviceName}:\n`;
  const start = compose.indexOf(marker);
  assert.notEqual(start, -1, `missing ${serviceName} service`);
  const contentStart = start + marker.length;
  const nextServiceOffset = compose
    .slice(contentStart)
    .search(/\n  [a-zA-Z0-9][a-zA-Z0-9_-]*:\n/);
  return nextServiceOffset < 0
    ? compose.slice(start)
    : compose.slice(start, contentStart + nextServiceOffset);
}

describe("fstworker Compose startup recovery", () => {
  it("keeps bare repository template startup worker-free and crash-bounded", async () => {
    const [rootCompose, deployCompose] = await Promise.all([
      readFile(path.join(repositoryRoot, "docker-compose.yml"), "utf8"),
      readFile(path.join(repositoryRoot, "deploy/docker-compose.yml"), "utf8")
    ]);

    for (const compose of [rootCompose, deployCompose]) {
      const worker = composeServiceBlock(compose, "fstworker");
      assert.match(worker, /profiles:\s*\["worker"\]/);
      assert.match(worker, /restart:\s*"on-failure:5"/);
      for (const coreService of ["postgres", "fstservice", "festivalweb"]) {
        assert.match(
          composeServiceBlock(compose, coreService),
          /restart:\s*unless-stopped/
        );
      }
    }
  });

  it("resolves the profiled worker only when the profile is explicit", async () => {
    const harness = await createHarness();
    const dockerEnvironment = {
      ...process.env,
      FST_GUARD_TEST_ROOT: harness.root
    };
    try {
      const bare = await execFileAsync(
        harness.dockerPath,
        ["compose", "config", "--format", "json"],
        { env: dockerEnvironment, encoding: "utf8" }
      );
      assert.equal(JSON.parse(bare.stdout).services.fstworker, undefined);

      const profiled = await execFileAsync(
        harness.dockerPath,
        ["compose", "--profile", "worker", "config", "--format", "json"],
        { env: dockerEnvironment, encoding: "utf8" }
      );
      assert.ok(JSON.parse(profiled.stdout).services.fstworker);
    } finally {
      await harness.cleanup();
    }
  });

  it("starts the worker once without recreating healthy proxies", async () => {
    const harness = await createHarness();
    try {
      const result = await harness.run();
      assert.equal(result.code, 0, result.stderr);
      assert.deepEqual(await harness.events(), ["worker-start|fstworker"]);
      assert.equal(await readFile(harness.lockPath, "utf8"), "");
      assert.match(result.stdout, /recovery=ok .*recreated=0/);
      for (const value of sensitiveValues) {
        assert.doesNotMatch(result.stdout + result.stderr, new RegExp(value));
      }
    } finally {
      await harness.cleanup();
    }
  });

  it("recreates one unhealthy effective proxy once, then starts the worker", async () => {
    const harness = await createHarness({
      scenario: {
        proxyStates: {
          "pia-gluetun-1": "running|unhealthy"
        }
      }
    });
    try {
      const result = await harness.run();
      assert.equal(result.code, 0, result.stderr);
      assert.deepEqual(await harness.events(), [
        "proxy-recreate|pia-gluetun-1",
        "worker-start|fstworker"
      ]);
      assert.match(result.stdout, /recovery=ok .*recreated=1/);
    } finally {
      await harness.cleanup();
    }
  });

  it("fails closed when an effective proxy stays unhealthy", async () => {
    const harness = await createHarness({
      scenario: {
        proxyStates: {
          "pia-gluetun-1": "running|unhealthy"
        },
        persistentUnhealthy: ["pia-gluetun-1"]
      }
    });
    try {
      const result = await harness.run();
      assert.notEqual(result.code, 0);
      assert.deepEqual(await harness.events(), [
        "proxy-recreate|pia-gluetun-1"
      ]);
      assert.match(result.stderr, /did not become healthy/);
    } finally {
      await harness.cleanup();
    }
  });

  it("enforces the total deadline before proxy or worker mutation", async () => {
    const harness = await createHarness({
      scenario: {
        proxyStates: {
          "pia-gluetun-1": "running|unhealthy"
        }
      }
    });
    try {
      const result = await harness.run(
        ["--recover-start"],
        {
          FST_WORKER_RECOVERY_INITIAL_WAIT_SECONDS: "30",
          FST_WORKER_RECOVERY_TOTAL_DEADLINE_SECONDS: "1"
        }
      );
      assert.notEqual(result.code, 0);
      assert.deepEqual(await harness.events(), []);
      assert.match(result.stderr, /exceeded its total deadline/);
      assert.doesNotMatch(result.stderr, /did not become healthy/);
    } finally {
      await harness.cleanup();
    }
  });

  it("stops the worker when health and a fresh heartbeat do not converge", async () => {
    const harness = await createHarness({
      scenario: {
        workerBecomesReady: false
      }
    });
    try {
      const result = await harness.run();
      assert.notEqual(result.code, 0);
      assert.deepEqual(await harness.events(), [
        "worker-start|fstworker",
        "worker-stop|fstworker"
      ]);
      assert.match(result.stderr, /fresh heartbeat did not converge/);
    } finally {
      await harness.cleanup();
    }
  });

  it("leaves the worker running when work or a public-read freeze begins", async () => {
    const harness = await createHarness({
      scenario: {
        workerBecomesReady: false,
        postStartCurrentUpdateStatus: "updating",
        postStartPublicReadsFrozen: true
      }
    });
    try {
      const result = await harness.run();
      assert.notEqual(result.code, 0);
      assert.deepEqual(await harness.events(), ["worker-start|fstworker"]);
      assert.match(result.stderr, /leaving the worker running/);
      assert.match(
        result.stderr,
        /tools\/fst-worker-no-progress-watchdog\.mjs/
      );
      assert.match(result.stderr, /docs\/operations\/live-safety\.md/);
    } finally {
      await harness.cleanup();
    }
  });

  it("routes SIGTERM through one idle-state worker cleanup", async () => {
    const harness = await createHarness({
      scenario: {
        workerBecomesReady: false
      }
    });
    const child = harness.spawnGuard(
      ["--recover-start"],
      {
        FST_WORKER_RECOVERY_WORKER_WAIT_SECONDS: "30"
      }
    );
    let stdout = "";
    let stderr = "";
    child.stdout.setEncoding("utf8");
    child.stderr.setEncoding("utf8");
    child.stdout.on("data", (chunk) => {
      stdout += chunk;
    });
    child.stderr.on("data", (chunk) => {
      stderr += chunk;
    });

    try {
      await waitFor(async () =>
        (await harness.events()).includes("worker-start|fstworker")
      );
      const exitPromise = once(child, "exit");
      assert.equal(child.kill("SIGTERM"), true);
      const [code, signal] = await exitPromise;
      assert.equal(code, 143, stderr || stdout);
      assert.equal(signal, null);
      assert.deepEqual(await harness.events(), [
        "worker-start|fstworker",
        "worker-stop|fstworker"
      ]);
    } finally {
      if (child.exitCode == null && child.signalCode == null) {
        child.kill("SIGTERM");
        await once(child, "exit");
      }
      await harness.cleanup();
    }
  });

  it("stops a partially started worker when Compose reports failure", async () => {
    const harness = await createHarness({
      scenario: {
        workerStartFails: true
      }
    });
    try {
      const result = await harness.run();
      assert.notEqual(result.code, 0);
      assert.deepEqual(await harness.events(), [
        "worker-start|fstworker",
        "worker-stop|fstworker"
      ]);
      assert.match(result.stderr, /recreate\/start failed/);
    } finally {
      await harness.cleanup();
    }
  });

  it("ignores an unhealthy non-effective canonical proxy", async () => {
    const harness = await createHarness({
      scenario: {
        proxyStates: {
          "pia-gluetun-3": "running|unhealthy"
        }
      }
    });
    try {
      const result = await harness.run();
      assert.equal(result.code, 0, result.stderr);
      assert.deepEqual(await harness.events(), ["worker-start|fstworker"]);
    } finally {
      await harness.cleanup();
    }
  });

  it("does not start the worker when runtime egress qualification fails", async () => {
    const harness = await createHarness({
      scenario: {
        duplicateEgress: true
      }
    });
    try {
      const result = await harness.run();
      assert.notEqual(result.code, 0);
      assert.deepEqual(await harness.events(), []);
      assert.match(result.stderr, /duplicate egress detected/);
    } finally {
      await harness.cleanup();
    }
  });

  it("rejects recovery when unhealthy effective proxies exceed the cap", async () => {
    const harness = await createHarness({
      config: buildComposeConfig({ effectiveCount: 4 }),
      scenario: {
        proxyStates: {
          "pia-gluetun-1": "running|unhealthy",
          "pia-gluetun-2": "running|unhealthy",
          "pia-gluetun-3": "running|unhealthy",
          "pia-gluetun-4": "running|unhealthy"
        }
      }
    });
    try {
      const result = await harness.run();
      assert.notEqual(result.code, 0);
      assert.deepEqual(await harness.events(), []);
      assert.match(result.stderr, /exceeds the recovery cap/);
    } finally {
      await harness.cleanup();
    }
  });

  it("rejects running, updating, and frozen worker states before mutation", async () => {
    const cases = [
      {
        scenario: { workerContainerState: "running|healthy" },
        expected: /fstworker to be stopped/
      },
      {
        scenario: { currentUpdateStatus: "updating" },
        expected: /current update state to be idle/
      },
      {
        scenario: { publicReadsFrozen: true },
        expected: /public reads to be unfrozen/
      }
    ];

    for (const testCase of cases) {
      const harness = await createHarness({ scenario: testCase.scenario });
      try {
        const result = await harness.run();
        assert.notEqual(result.code, 0);
        assert.deepEqual(await harness.events(), []);
        assert.match(result.stderr, testCase.expected);
      } finally {
        await harness.cleanup();
      }
    }
  });

  it("requires healthy PostgreSQL and ready fstservice without restarting them", async () => {
    const cases = [
      { postgresState: "running|unhealthy" },
      { serviceReady: false }
    ];

    for (const scenario of cases) {
      const harness = await createHarness({ scenario });
      try {
        const result = await harness.run();
        assert.notEqual(result.code, 0);
        assert.deepEqual(await harness.events(), []);
        assert.match(result.stderr, /did not become healthy and ready/);
      } finally {
        await harness.cleanup();
      }
    }
  });

  it("rejects a static effective PIA IP pin without leaking values", async () => {
    const harness = await createHarness({
      config: buildComposeConfig({ pinnedEffectiveIp: sensitiveValues[0] })
    });
    try {
      const result = await harness.run();
      assert.notEqual(result.code, 0);
      assert.deepEqual(await harness.events(), []);
      assert.match(result.stderr, /must not set OPENVPN_ENDPOINT_IP/);
      for (const value of sensitiveValues) {
        assert.doesNotMatch(result.stdout + result.stderr, new RegExp(value));
      }
    } finally {
      await harness.cleanup();
    }
  });

  it("rejects a noncanonical service in the effective proxy arrays", async () => {
    const config = buildComposeConfig();
    const environment = config.services.fstworker.environment;
    environment.Scraper__ProxyUrls__0 = "http://fstservice:8888";
    environment.Scraper__ControlUrls__0 = "http://fstservice:8000";
    environment.Scraper__ContainerNames__0 = "fstservice";

    const harness = await createHarness({ config });
    try {
      const result = await harness.run();
      assert.notEqual(result.code, 0);
      assert.deepEqual(await harness.events(), []);
      assert.match(result.stderr, /must be canonical PIA services/);
    } finally {
      await harness.cleanup();
    }
  });

  it("rejects a continuous worker with the legacy unless-stopped policy", async () => {
    const harness = await createHarness({
      config: buildComposeConfig({ restartPolicy: "unless-stopped" })
    });
    try {
      const result = await harness.run(["--check", "--config-only"]);
      assert.notEqual(result.code, 0);
      assert.deepEqual(await harness.events(), []);
      assert.match(
        result.stderr,
        /continuous worker restart policy must resolve to on-failure:5/
      );
    } finally {
      await harness.cleanup();
    }
  });

  it("rejects a worker missing the guard-only Compose profile", async () => {
    const harness = await createHarness({
      config: buildComposeConfig({ workerProfiles: [] })
    });
    try {
      const result = await harness.run(["--check", "--config-only"]);
      assert.notEqual(result.code, 0);
      assert.deepEqual(await harness.events(), []);
      assert.match(result.stderr, /must include the worker Compose profile/);
    } finally {
      await harness.cleanup();
    }
  });

  it("enforces an expected image for a continuous worker", async () => {
    const harness = await createHarness();
    try {
      const result = await harness.run([
        "--check",
        "--config-only",
        "--expected-worker-image",
        "example.invalid/fstworker:test"
      ]);
      assert.equal(result.code, 0, result.stderr);
      assert.deepEqual(await harness.events(), []);
    } finally {
      await harness.cleanup();
    }
  });

  it("rejects a mismatched expected image without a data profile", async () => {
    const harness = await createHarness({
      config: buildComposeConfig({
        workerImage: "example.invalid/fstworker:unexpected"
      })
    });
    try {
      const result = await harness.run([
        "--check",
        "--config-only",
        "--expected-worker-image",
        "example.invalid/fstworker:test"
      ]);
      assert.notEqual(result.code, 0);
      assert.deepEqual(await harness.events(), []);
      assert.match(
        result.stderr,
        /resolved fstworker image must match .* found .*unexpected/
      );
    } finally {
      await harness.cleanup();
    }
  });

  it("shares one lock across every mutating worker action", async () => {
    const harness = await createHarness();
    const holder = spawn(
      "flock",
      [
        "-n",
        harness.lockPath,
        process.execPath,
        "-e",
        "process.stdout.write('locked\\n');setTimeout(() => {}, 1500)"
      ],
      {
        cwd: repositoryRoot,
        stdio: ["ignore", "pipe", "pipe"]
      }
    );
    await once(holder.stdout, "data");

    try {
      const [recovery, recreate, recreateRunonce, check] = await Promise.all([
        harness.run(["--recover-start"]),
        harness.run(["--recreate"]),
        harness.run([
          "--recreate-runonce",
          "--data-profile",
          "snapshot-reuse",
          "--expected-worker-image",
          "example.invalid/fstworker:test"
        ]),
        harness.run(["--check", "--config-only"])
      ]);
      for (const result of [recovery, recreate, recreateRunonce]) {
        assert.notEqual(result.code, 0);
        assert.match(result.stderr, /start\/recreate action is already running/);
      }
      assert.equal(check.code, 0, check.stderr);
      assert.deepEqual(await harness.events(), []);
    } finally {
      await once(holder, "exit");
      await harness.cleanup();
    }
  });

  it("honors an explicit shared lock-path override", async () => {
    const harness = await createHarness();
    const overrideLockPath = path.join(harness.root, "explicit-worker.lock");
    const holder = spawn(
      "flock",
      [
        "-n",
        overrideLockPath,
        process.execPath,
        "-e",
        "process.stdout.write('locked\\n');setTimeout(() => {}, 1000)"
      ],
      {
        cwd: repositoryRoot,
        stdio: ["ignore", "pipe", "pipe"]
      }
    );
    await once(holder.stdout, "data");

    try {
      const result = await harness.run(
        ["--recreate"],
        { FST_WORKER_COMPOSE_GUARD_LOCK_PATH: overrideLockPath }
      );
      assert.notEqual(result.code, 0);
      assert.match(result.stderr, /start\/recreate action is already running/);
      assert.deepEqual(await harness.events(), []);
    } finally {
      await once(holder, "exit");
      await harness.cleanup();
    }
  });

  it("rejects candidate profiles for continuous recovery", async () => {
    const harness = await createHarness();
    try {
      const result = await harness.run([
        "--recover-start",
        "--throughput-profile",
        "candidate-1600-64-8"
      ]);
      assert.equal(result.code, 64);
      assert.deepEqual(await harness.events(), []);
      assert.match(result.stderr, /require --recreate-runonce/);
    } finally {
      await harness.cleanup();
    }
  });

  it("rejects config-only, data-profile, and run-once recovery combinations", async () => {
    const argumentSets = [
      ["--recover-start", "--config-only"],
      [
        "--recover-start",
        "--data-profile",
        "snapshot-reuse",
        "--expected-worker-image",
        "example.invalid/fstworker:test"
      ],
      ["--check-runonce", "--recover-start"]
    ];

    for (const args of argumentSets) {
      const harness = await createHarness();
      try {
        const result = await harness.run(args);
        assert.equal(result.code, 64);
        assert.deepEqual(await harness.events(), []);
      } finally {
        await harness.cleanup();
      }
    }
  });

  it("accepts restart no for a valid run-once worker profile", async () => {
    const harness = await createHarness({
      config: buildRunonceComposeConfig()
    });
    try {
      const result = await harness.run([
        "--check-runonce",
        "--config-only",
        "--data-profile",
        "notification-db-only",
        "--expected-worker-image",
        "example.invalid/fstworker:test"
      ]);
      assert.equal(result.code, 0, result.stderr);
      assert.deepEqual(await harness.events(), []);
      assert.match(result.stdout, /run_once=true/);
    } finally {
      await harness.cleanup();
    }
  });

  it("accepts publication-cache generation with current scope fingerprints", async () => {
    const harness = await createHarness({
      config: buildPublicationCacheRunonceConfig()
    });
    try {
      const result = await harness.run([
        "--check-runonce",
        "--config-only",
        "--data-profile",
        "publication-cache-generation",
        "--expected-worker-image",
        "example.invalid/fstworker:test"
      ]);
      assert.equal(result.code, 0, result.stderr);
      assert.deepEqual(await harness.events(), []);
    } finally {
      await harness.cleanup();
    }
  });

  it("rejects publication-cache generation without current scope fingerprints", async () => {
    const harness = await createHarness({
      config: buildPublicationCacheRunonceConfig({
        useLeaderboardScopeFingerprints: false
      })
    });
    try {
      const result = await harness.run([
        "--check-runonce",
        "--config-only",
        "--data-profile",
        "publication-cache-generation",
        "--expected-worker-image",
        "example.invalid/fstworker:test"
      ]);
      assert.notEqual(result.code, 0);
      assert.deepEqual(await harness.events(), []);
      assert.match(
        result.stderr,
        /requires Features__UseLeaderboardScopeFingerprints=true/
      );
    } finally {
      await harness.cleanup();
    }
  });

  it("accepts the leaderboard-rivals batch run-once profile", async () => {
    const harness = await createHarness({
      config: buildLeaderboardRivalsBatchRunonceConfig()
    });
    try {
      const result = await harness.run([
        "--check-runonce",
        "--config-only",
        "--throughput-profile",
        "candidate-800-32-4",
        "--data-profile",
        "leaderboard-rivals-batch",
        "--expected-worker-image",
        "example.invalid/fstworker:test"
      ]);
      assert.equal(result.code, 0, result.stderr);
      assert.deepEqual(await harness.events(), []);
      assert.match(result.stdout, /throughput_profile=candidate-800-32-4/);
    } finally {
      await harness.cleanup();
    }
  });

  it("rejects an unapproved leaderboard-rivals account batch size", async () => {
    const harness = await createHarness({
      config: buildLeaderboardRivalsBatchRunonceConfig({
        accountBatchSize: "5"
      })
    });
    try {
      const result = await harness.run([
        "--check-runonce",
        "--config-only",
        "--throughput-profile",
        "candidate-800-32-4",
        "--data-profile",
        "leaderboard-rivals-batch",
        "--expected-worker-image",
        "example.invalid/fstworker:test"
      ]);
      assert.notEqual(result.code, 0);
      assert.deepEqual(await harness.events(), []);
      assert.match(
        result.stderr,
        /LeaderboardRivalsMaxDegreeOfParallelism=4/
      );
    } finally {
      await harness.cleanup();
    }
  });

  it("rejects a changed song-rivals account concurrency", async () => {
    const harness = await createHarness({
      config: buildLeaderboardRivalsBatchRunonceConfig({
        rivalsMaxDegreeOfParallelism: "4"
      })
    });
    try {
      const result = await harness.run([
        "--check-runonce",
        "--config-only",
        "--throughput-profile",
        "candidate-800-32-4",
        "--data-profile",
        "leaderboard-rivals-batch",
        "--expected-worker-image",
        "example.invalid/fstworker:test"
      ]);
      assert.notEqual(result.code, 0);
      assert.deepEqual(await harness.events(), []);
      assert.match(
        result.stderr,
        /Scraper__RivalsMaxDegreeOfParallelism=2/
      );
    } finally {
      await harness.cleanup();
    }
  });

  it("accepts a guarded scrape resume with exact metrics and rivals cap", async () => {
    const harness = await createHarness({
      config: buildScrapeResumeRunonceConfig()
    });
    try {
      const result = await harness.run([
        "--check-runonce",
        "--config-only",
        "--data-profile",
        "scrape-resume",
        "--expected-worker-image",
        "example.invalid/fstworker:test"
      ]);
      assert.equal(result.code, 0, result.stderr);
      assert.deepEqual(await harness.events(), []);
      assert.match(result.stdout, /data_profile=scrape-resume/);
    } finally {
      await harness.cleanup();
    }
  });

  it("rejects a scrape resume without positive persisted metrics", async () => {
    for (const config of [
      buildScrapeResumeRunonceConfig({ resumeScrapeId: "0" }),
      buildScrapeResumeRunonceConfig({ resumeTotalBytes: "0" })
    ]) {
      const harness = await createHarness({ config });
      try {
        const result = await harness.run([
          "--check-runonce",
          "--config-only",
          "--data-profile",
          "scrape-resume",
          "--expected-worker-image",
          "example.invalid/fstworker:test"
        ]);
        assert.notEqual(result.code, 0);
        assert.deepEqual(await harness.events(), []);
        assert.match(result.stderr, /must be greater than zero/);
      } finally {
        await harness.cleanup();
      }
    }
  });

  it("rejects a scrape resume with an unapproved rivals account cap", async () => {
    const harness = await createHarness({
      config: buildScrapeResumeRunonceConfig({
        rivalsMaxDegreeOfParallelism: "4"
      })
    });
    try {
      const result = await harness.run([
        "--check-runonce",
        "--config-only",
        "--data-profile",
        "scrape-resume",
        "--expected-worker-image",
        "example.invalid/fstworker:test"
      ]);
      assert.notEqual(result.code, 0);
      assert.deepEqual(await harness.events(), []);
      assert.match(
        result.stderr,
        /RivalsMaxDegreeOfParallelism=2/);
    } finally {
      await harness.cleanup();
    }
  });

  it("rejects scrape-resume profile outside run-once guard actions", async () => {
    for (const { action, configOnly } of [
      { action: "--check", configOnly: true },
      { action: "--recreate", configOnly: false }
    ]) {
      const harness = await createHarness({
        config: buildScrapeResumeRunonceConfig()
      });
      try {
        const result = await harness.run([
          action,
          ...(configOnly ? ["--config-only"] : []),
          "--data-profile",
          "scrape-resume",
          "--expected-worker-image",
          "example.invalid/fstworker:test"
        ]);
        assert.equal(result.code, 64);
        assert.deepEqual(await harness.events(), []);
        assert.match(
          result.stderr,
          /requires --check-runonce or --recreate-runonce/);
      } finally {
        await harness.cleanup();
      }
    }
  });

  it("starts a scrape resume only from the matching frozen candidate", async () => {
    const harness = await createHarness({
      config: buildScrapeResumeRunonceConfig(),
      scenario: {
        currentUpdateStatus: "stalled",
        currentScrapeId: 1305,
        publicReadsFrozen: true,
        publishedScrapeId: 1304
      }
    });
    try {
      const result = await harness.run([
        "--recreate-runonce",
        "--data-profile",
        "scrape-resume",
        "--expected-worker-image",
        "example.invalid/fstworker:test"
      ]);
      assert.equal(result.code, 0, result.stderr);
      assert.deepEqual(await harness.events(), [
        "worker-start|fstworker"
      ]);
      assert.match(
        result.stdout,
        /resume=preflight worker=stopped scrape=1305/);
    } finally {
      await harness.cleanup();
    }
  });

  it("rejects scrape resume when candidate identity or freeze is lost", async () => {
    const cases = [
      {
        currentUpdateStatus: "updating",
        currentScrapeId: 1306,
        publicReadsFrozen: true,
        expected: /does not match the configured scrape/
      },
      {
        currentUpdateStatus: "updating",
        currentScrapeId: 1305,
        publicReadsFrozen: false,
        expected: /requires public reads to remain frozen/
      },
      {
        currentUpdateStatus: "idle",
        currentScrapeId: 1305,
        publicReadsFrozen: true,
        expected: /current update state to be updating or stalled/
      },
      {
        currentUpdateStatus: "stalled",
        currentScrapeId: 1305,
        publicReadsFrozen: true,
        freezeReason: "max-score-maintenance:test",
        expected: /freeze reason post-process/
      }
    ];

    for (const scenario of cases) {
      const harness = await createHarness({
        config: buildScrapeResumeRunonceConfig(),
        scenario
      });
      try {
        const result = await harness.run([
          "--check-runonce",
          "--data-profile",
          "scrape-resume",
          "--expected-worker-image",
          "example.invalid/fstworker:test"
        ]);
        assert.notEqual(result.code, 0);
        assert.deepEqual(await harness.events(), []);
        assert.match(result.stderr, scenario.expected);
      } finally {
        await harness.cleanup();
      }
    }
  });

  it("explicitly starts a valid profiled run-once worker", async () => {
    const harness = await createHarness({
      config: buildRunonceComposeConfig()
    });
    try {
      const result = await harness.run([
        "--recreate-runonce",
        "--data-profile",
        "notification-db-only",
        "--expected-worker-image",
        "example.invalid/fstworker:test"
      ]);
      assert.equal(result.code, 0, result.stderr);
      assert.deepEqual(await harness.events(), ["worker-start|fstworker"]);
    } finally {
      await harness.cleanup();
    }
  });

  it("rejects a run-once worker that retains the continuous restart policy", async () => {
    const config = buildRunonceComposeConfig();
    config.services.fstworker.restart = "on-failure:5";
    const harness = await createHarness({ config });
    try {
      const result = await harness.run([
        "--check-runonce",
        "--config-only",
        "--data-profile",
        "notification-db-only",
        "--expected-worker-image",
        "example.invalid/fstworker:test"
      ]);
      assert.notEqual(result.code, 0);
      assert.deepEqual(await harness.events(), []);
      assert.match(result.stderr, /run-once worker restart policy must resolve to no/);
    } finally {
      await harness.cleanup();
    }
  });

  it("rejects a merged config that enables run-once mode", async () => {
    const harness = await createHarness({
      config: buildComposeConfig({ runOnce: true })
    });
    try {
      const result = await harness.run();
      assert.notEqual(result.code, 0);
      assert.deepEqual(await harness.events(), []);
      assert.match(result.stderr, /continuous guard actions require/);
    } finally {
      await harness.cleanup();
    }
  });

  it("rejects a nonpositive total recovery deadline", async () => {
    const harness = await createHarness();
    try {
      const result = await harness.run(
        ["--recover-start"],
        { FST_WORKER_RECOVERY_TOTAL_DEADLINE_SECONDS: "0" }
      );
      assert.equal(result.code, 64);
      assert.deepEqual(await harness.events(), []);
      assert.match(
        result.stderr,
        /FST_WORKER_RECOVERY_TOTAL_DEADLINE_SECONDS must be greater than zero/
      );
    } finally {
      await harness.cleanup();
    }
  });

  it("preserves config-only check behavior", async () => {
    const harness = await createHarness();
    try {
      const result = await harness.run(["--check", "--config-only"]);
      assert.equal(result.code, 0, result.stderr);
      assert.deepEqual(await harness.events(), []);
      assert.match(result.stdout, /compose_guard config=ok/);
    } finally {
      await harness.cleanup();
    }
  });
});
