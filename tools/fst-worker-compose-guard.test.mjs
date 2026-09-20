import assert from "node:assert/strict";
import { execFile, spawn } from "node:child_process";
import { createHash } from "node:crypto";
import { once } from "node:events";
import {
  chmod,
  mkdir,
  mkdtemp,
  readFile,
  rm,
  writeFile
} from "node:fs/promises";
import os from "node:os";
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
const expectedWorkerImageId = "sha256:" + "a".repeat(64);
const expectedWorkerRevision = "1".repeat(40);
const bandMaintenanceProgressImage =
  "example.invalid/fstworker:band-maintenance-progress";
const bandMaintenanceProgressRevision =
  "3".repeat(40);
const wireSendTelemetryImage =
  "example.invalid/fstworker:wire-send-telemetry";
const wireSendTelemetryRevision =
  "4".repeat(40);
const immutableWorkerImage =
  "example.invalid/fstworker@sha256:" + "e".repeat(64);
const canonicalSoloInstruments = [
  "Solo_Guitar",
  "Solo_Bass",
  "Solo_Vocals",
  "Solo_Drums",
  "Solo_PeripheralGuitar",
  "Solo_PeripheralBass",
  "Solo_PeripheralVocals",
  "Solo_PeripheralCymbals",
  "Solo_PeripheralDrums"
];

function canonicalize(value) {
  if (Array.isArray(value)) {
    return value.map(canonicalize);
  }
  if (value && typeof value === "object") {
    return Object.fromEntries(
      Object.keys(value).sort()
        .map((key) => [key, canonicalize(value[key])])
    );
  }
  return value;
}

function workerConfigSha256(config) {
  const worker = structuredClone(config.services.fstworker);
  delete worker.image;
  return createHash("sha256")
    .update(JSON.stringify(canonicalize(worker)))
    .digest("hex");
}

function soloScopeFingerprint(pairs) {
  const hash = createHash("sha256");
  hash.update(Buffer.from("fst-solo-acquisition-scope\0v1\0", "utf8"));
  const ordered = [...new Set(
    pairs.map(([songId, instrument]) => `${instrument}\0${songId}`)
  )]
    .sort((left, right) => left.localeCompare(right, "en", {
      sensitivity: "case",
      numeric: false
    }))
    .map((value) => {
      const [instrument, songId] = value.split("\0");
      return [songId, instrument];
    });
  for (const [songId, instrument] of ordered) {
    for (const value of [instrument, songId]) {
      const bytes = Buffer.from(value, "utf8");
      const length = Buffer.alloc(4);
      length.writeInt32BE(bytes.length);
      hash.update(length);
      hash.update(bytes);
    }
  }
  return hash.digest("hex");
}

function buildPublicationCatalogJson(songIds, schemaVersion = 1) {
  const songs = songIds.map((songId) => ({
    track: { su: songId }
  }));
  return JSON.stringify(
    schemaVersion === 2
      ? { songs }
      : songs
  );
}

function buildActiveRecoveryResumeState({
  scrapeId = 1305,
  publishedScrapeId = 1304,
  songIds = ["song-a", "song-b"],
  status = "running",
  manifestCount = songIds.length * canonicalSoloInstruments.length,
  completeManifestCount = manifestCount,
  writerFailureCount = 0,
  criticalPhaseFailureCount = 0,
  acquisitionCompletedAtUtc = "2026-08-11T19:30:00Z",
  songsScraped = songIds.length,
  totalEntries = 40764011,
  totalRequests = 409088,
  totalBytes = 57563653024,
  epicReportedOver100Pages = true,
  expectedSoloScopeFingerprintVersion = 1,
  completeSoloPairs = null,
  expectedSoloScopeCount = null,
  expectedSoloScopeFingerprint = null,
  workingPublicationId = 9001,
  candidatePublicationId = 9001,
  candidatePublicationStatus = "building",
  startupShouldResumeDeferredPublication = false,
  improvementNotificationsScrapeId = null,
  improvementNotificationsStatus = null,
  publicationCatalogSchemaVersion = 1,
  publicationCatalogVersion = 77,
  publicationCatalogContentHash = "b".repeat(64),
  publicationCatalogJson = null
} = {}) {
  const pairs = completeSoloPairs ?? songIds.flatMap((songId) =>
    canonicalSoloInstruments.map((instrument) => [songId, instrument])
  );
  const checkpointScopeCount = expectedSoloScopeCount ?? pairs.length;
  const checkpointFingerprint =
    expectedSoloScopeFingerprint ?? soloScopeFingerprint(pairs);
  return {
    scrapeId,
    startedAtUtc: "2026-08-11T19:00:00Z",
    status,
    publishedScrapeId,
    workingPublicationId,
    publicReadsFrozenReason: "post-process",
    improvementNotificationsScrapeId,
    improvementNotificationsStatus,
    candidatePublicationId,
    candidatePublicationStatus,
    manifestCount,
    completeManifestCount,
    writerFailureCount,
    criticalPhaseFailureCount,
    acquisitionCompletedAtUtc,
    songsScraped,
    totalEntries,
    totalRequests,
    totalBytes,
    epicReportedOver100Pages,
    expectedSoloScopeCount: checkpointScopeCount,
    expectedSoloScopeFingerprintVersion,
    expectedSoloScopeFingerprint: checkpointFingerprint,
    publicationCatalogVersion,
    publicationCatalogSchemaVersion,
    publicationCatalogContentHash,
    publicationSongCount: songIds.length,
    publicationCatalogJson:
      publicationCatalogJson
      ?? buildPublicationCatalogJson(
        songIds,
        publicationCatalogSchemaVersion
      ),
    completeSoloPairs: pairs,
    startupShouldResumeDeferredPublication
  };
}

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
const runonceConfigPath = path.join(root, "runonce-compose.json");
const scenario = JSON.parse(readFileSync(scenarioPath, "utf8"));
const runtime = JSON.parse(readFileSync(runtimePath, "utf8"));
const args = process.argv.slice(2);
const defaultWorkerImageId = "sha256:" + "a".repeat(64);
const defaultWorkerRevision = "1".repeat(40);
let stdinConfig = null;

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

function currentComposeConfig(commandArgs) {
  function resolveComposeDefaults(value) {
    if (Array.isArray(value)) {
      return value.map(resolveComposeDefaults);
    }
    if (value && typeof value === "object") {
      return Object.fromEntries(
        Object.entries(value).map(([key, child]) => [
          key,
          resolveComposeDefaults(child)
        ])
      );
    }
    if (typeof value !== "string") {
      return value;
    }
    const match = value.match(
      /^\$\{BAND_CURRENT_PROJECTION_USE_BATCHED_MEMBER_STATS_AGGREGATION:-([^}]*)\}$/
    );
    if (!match) {
      return value;
    }
    return process.env.BAND_CURRENT_PROJECTION_USE_BATCHED_MEMBER_STATS_AGGREGATION
      || match[1];
  }

  let config;
  if (stdinConfig) {
    config = stdinConfig;
  } else if (commandArgs.some((value) => value.endsWith("/docker-compose.runonce.yml") || value === "docker-compose.runonce.yml")) {
    config = JSON.parse(readFileSync(runonceConfigPath, "utf8"));
  } else {
    config = JSON.parse(readFileSync(configPath, "utf8"));
  }
  return resolveComposeDefaults(config);
}

function startModeFromConfig(config) {
  const restart = String(
    config?.services?.fstworker?.restart ?? ""
  ).toLowerCase();
  if (restart === "no") {
    return "runonce";
  }
  const value = String(
    config?.services?.fstworker?.environment?.Scraper__RunOnce ?? ""
  ).toLowerCase();
  return value === "true" ? "runonce" : "continuous";
}

function assignWorkerIdentity(config) {
  runtime.workerContainerId =
    scenario.createdWorkerContainerId ?? "b".repeat(64);
  runtime.workerImage = config.services.fstworker.image;
  runtime.workerImageId =
    scenario.createdWorkerImageId
    ?? scenario.resolvedWorkerImageId
    ?? defaultWorkerImageId;
  runtime.workerRevision =
    scenario.createdWorkerRevision
    ?? scenario.resolvedWorkerRevision
    ?? defaultWorkerRevision;
  runtime.lastWorkerStartMode = startModeFromConfig(config);
  runtime.workerStartedOnce = true;
}

function containerState(name) {
  if (name === "fst-postgres" || name === "postgres") {
    return scenario.postgresState ?? "running|healthy";
  }
  if (name === "fstservice") {
    return scenario.serviceState ?? "running|healthy";
  }
  if (name === "fstworker" || name === runtime.workerContainerId) {
    return runtime.workerState;
  }
  if (/^pia-gluetun-\d+$/.test(name)) {
    return runtime.proxyStates[name] ?? "running|healthy";
  }
  return null;
}

function serviceInfo() {
  const mode = runtime.lastWorkerStartMode ?? "continuous";
  const afterWorkerStart = runtime.workerStarted || runtime.workerStartedOnce;
  const workerReady =
    afterWorkerStart
    && (
      mode === "runonce"
        ? scenario.runonceWorkerBecomesReady
        : scenario.continuousWorkerBecomesReady
    ) !== false
    && scenario.workerBecomesReady !== false;
  const statusPrefix = mode === "runonce"
    ? "runonce"
    : "continuous";
  return {
    currentUpdate: {
      status: afterWorkerStart
        ? scenario[statusPrefix + "PostStartCurrentUpdateStatus"]
          ?? scenario.postStartCurrentUpdateStatus
          ?? scenario.currentUpdateStatus
          ?? "idle"
        : scenario.currentUpdateStatus ?? "idle",
      scrapeId: scenario.currentScrapeId ?? 1305
    },
    publication: {
      publicReadsFrozen: afterWorkerStart
        ? scenario[statusPrefix + "PostStartPublicReadsFrozen"]
          ?? scenario.postStartPublicReadsFrozen
          ?? scenario.publicReadsFrozen
          ?? false
        : scenario.publicReadsFrozen ?? false,
      publishedScrapeId: afterWorkerStart
        ? scenario[statusPrefix + "PostStartPublishedScrapeId"]
          ?? scenario.postStartPublishedScrapeId
          ?? scenario.publishedScrapeId
          ?? 1304
        : scenario.publishedScrapeId ?? 1304,
      freezeReason: afterWorkerStart
        ? scenario[statusPrefix + "PostStartFreezeReason"]
          ?? scenario.postStartFreezeReason
          ?? scenario.freezeReason
          ?? "post-process"
        : scenario.freezeReason ?? "post-process"
    },
    workerStatus: workerReady
      ? {
          status: "online",
          instanceId: scenario[statusPrefix + "WorkerInstanceId"]
            ?? (mode === "runonce"
              ? "runonce-worker-instance"
              : "continuous-worker-instance"),
          lastHeartbeatAt: scenario[statusPrefix + "WorkerHeartbeatAt"]
            ?? (mode === "runonce"
              ? "2026-08-11T20:39:00Z"
              : "2026-08-11T20:40:00Z"),
          heartbeatAgeSeconds:
            scenario[statusPrefix + "WorkerHeartbeatAgeSeconds"]
            ?? 1,
          staleAfterSeconds:
            scenario[statusPrefix + "WorkerStaleAfterSeconds"]
            ?? 90
        }
      : {
          status: scenario.initialWorkerApiStatus ?? "offline",
          instanceId:
            scenario.initialWorkerApiInstanceId ?? "old-worker-instance",
          lastHeartbeatAt:
            scenario.initialWorkerHeartbeatAt ?? "2026-08-11T19:00:00Z",
          heartbeatAgeSeconds:
            scenario.initialWorkerHeartbeatAgeSeconds ?? 3600,
          staleAfterSeconds:
            scenario.initialWorkerStaleAfterSeconds ?? 90
        }
  };
}

function activeRecoveryDatabaseState() {
  if (
    runtime.workerStartedOnce
    && runtime.lastWorkerStartMode === "runonce"
    && scenario.postRunonceResumeState
  ) {
    return scenario.postRunonceResumeState;
  }
  return scenario.resumeState ?? null;
}

if (args[0] === "compose") {
  if (args.some((value, index) =>
    value === "-f" && args[index + 1] === "-"
  )) {
    const stdinText = readFileSync(0, "utf8");
    stdinConfig = stdinText ? JSON.parse(stdinText) : null;
  }
  if (args.includes("config")) {
    const config = currentComposeConfig(args);
    if (!workerProfileEnabled(args)) {
      delete config.services?.fstworker;
    }
    writeFileSync(1, JSON.stringify(config));
    process.exit(0);
  }

  const createIndex = args.indexOf("create");
  if (createIndex >= 0) {
    const services = args
      .slice(createIndex + 1)
      .filter((value) => !value.startsWith("-"));
    if (!services.includes("fstworker") || !workerProfileEnabled(args)) {
      process.stderr.write("invalid worker create\n");
      process.exit(96);
    }
    if (scenario.workerCreateFailsBeforeReplacement) {
      process.exit(1);
    }
    const config = currentComposeConfig(args);
    runtime.workerStarted = false;
    runtime.workerState = "created|none";
    runtime.workerExitCode = 0;
    runtime.workerStartedAt = "0001-01-01T00:00:00Z";
    assignWorkerIdentity(config);
    saveRuntime();
    process.exit(scenario.workerCreateFails ? 1 : 0);
  }

  const psIndex = args.indexOf("ps");
  if (psIndex >= 0) {
    if (scenario.composePsFails) {
      process.exit(1);
    }
    if (args.includes("fstworker") && runtime.workerContainerId) {
      process.stdout.write(runtime.workerContainerId);
    }
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
      if (args.includes("--no-start")) {
        if (scenario.workerCreateFailsBeforeReplacement) {
          process.exit(1);
        }
        const config = currentComposeConfig(args);
        runtime.workerStarted = false;
        runtime.workerState = "created|none";
        runtime.workerExitCode = 0;
        runtime.workerStartedAt = "0001-01-01T00:00:00Z";
        assignWorkerIdentity(config);
        saveRuntime();
        process.exit(scenario.workerCreateFails ? 1 : 0);
      }
      event("worker-start", ["fstworker"]);
      const config = currentComposeConfig(args);
      const mode = startModeFromConfig(config);
      runtime.workerStarted = true;
      runtime.workerState = "running|healthy";
      runtime.workerExitCode = mode === "runonce"
        ? scenario.runonceWorkerExitCode ?? scenario.workerExitCode ?? 0
        : scenario.continuousWorkerExitCode ?? scenario.workerExitCode ?? 0;
      runtime.workerStartedAt = "2026-08-11T20:39:00Z";
      assignWorkerIdentity(config);
      if (
        mode === "runonce"
        && (scenario.runonceWorkerExitsImmediately
          ?? scenario.workerExitsImmediately)
      ) {
        runtime.workerStarted = false;
        runtime.workerState = "exited|none";
      }
      saveRuntime();
      const startFails =
        (mode === "runonce"
          ? scenario.runonceWorkerStartFails
          : scenario.continuousWorkerStartFails)
        ?? scenario.workerStartFails;
      process.exit(startFails ? 1 : 0);
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

if (args[0] === "image" && args[1] === "inspect") {
  const imageId = scenario.resolvedWorkerImageId ?? defaultWorkerImageId;
  const revision =
    scenario.resolvedWorkerRevision ?? defaultWorkerRevision;
  if (args.includes("--format")) {
    process.stdout.write(imageId + "|" + revision);
  } else {
    process.stdout.write(JSON.stringify([{ Id: imageId }]));
  }
  process.exit(0);
}

if (args[0] === "info") {
  process.exit(scenario.dockerInfoFails ? 1 : 0);
}

if (args[0] === "inspect") {
  const name = args.at(-1);
  const state = containerState(name);
  if (state == null) {
    process.exit(1);
  }
  if (args.includes("--format")) {
    const format = args[args.indexOf("--format") + 1] ?? "";
    if (
      (name === "fstworker" || name === runtime.workerContainerId)
      && format.includes("{{.Id}}|{{.Image}}")
    ) {
      process.stdout.write([
        runtime.workerContainerId,
        runtime.workerImageId,
        runtime.workerImage,
        runtime.workerRevision,
        runtime.workerStarted ? "true" : "false",
        runtime.workerState.split("|", 1)[0],
        String(runtime.workerExitCode ?? 0),
        runtime.workerStartedAt ?? "0001-01-01T00:00:00Z"
      ].join("|"));
    } else if (
      (name === "fstworker" || name === runtime.workerContainerId)
      && format.includes("{{.Id}}")
    ) {
      process.stdout.write(runtime.workerContainerId ?? "");
    } else {
      process.stdout.write(state);
      if (
        (name === "fstworker" || name === runtime.workerContainerId)
        && format.includes(".State.Status")
        && scenario.runonceWorkerExitsAfterStateProbe
        && runtime.workerStarted
        && runtime.lastWorkerStartMode === "runonce"
      ) {
        runtime.workerStarted = false;
        runtime.workerState = "exited|none";
        saveRuntime();
      }
    }
  } else {
    process.stdout.write("{}");
  }
  process.exit(0);
}

if (args[0] === "start" && args.at(-1) === runtime.workerContainerId) {
  event("worker-start", ["fstworker"]);
  const mode = runtime.lastWorkerStartMode ?? startModeFromConfig(currentComposeConfig(args));
  runtime.lastWorkerStartMode = mode;
  runtime.workerStartedOnce = true;
  runtime.workerStartedAt = "2026-08-11T20:39:00Z";
  runtime.workerExitCode = mode === "runonce"
    ? scenario.runonceWorkerExitCode ?? scenario.workerExitCode ?? 0
    : scenario.continuousWorkerExitCode ?? scenario.workerExitCode ?? 0;
  if (
    mode === "runonce"
      ? scenario.runonceWorkerExitsImmediately ?? scenario.workerExitsImmediately
      : scenario.continuousWorkerExitsImmediately ?? scenario.workerExitsImmediately
  ) {
    runtime.workerStarted = false;
    runtime.workerState = "exited|none";
  } else {
    runtime.workerStarted = true;
    runtime.workerState = "running|healthy";
  }
  saveRuntime();
  if (scenario.workerStartDelayMs) {
    await new Promise((resolve) =>
      setTimeout(resolve, scenario.workerStartDelayMs)
    );
  }
  const startFails =
    (mode === "runonce"
      ? scenario.runonceWorkerStartFails
      : scenario.continuousWorkerStartFails)
    ?? scenario.workerStartFails;
  process.exit(startFails ? 1 : 0);
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
  if (
    (container === "fst-postgres" || container === "postgres")
    && commandArgs[0] === "psql"
    && joined.includes("fst_boot_acquisition_checkpoint_schema")
  ) {
    process.stdout.write(
      scenario.acquisitionCheckpointSchemaReady === false
        ? "missing"
        : "ready"
    );
    process.exit(0);
  }
  if (
    (container === "fst-postgres" || container === "postgres")
    && commandArgs[0] === "psql"
    && joined.includes("fst_boot_wire_send_telemetry_schema")
  ) {
    process.stdout.write(
      scenario.wireSendTelemetrySchemaState
        ?? (scenario.wireSendTelemetrySchemaReady === false
          ? "missing"
          : "ready")
    );
    process.exit(0);
  }
  if (
    (container === "fst-postgres" || container === "postgres")
    && commandArgs[0] === "psql"
    && joined.includes("fst_boot_active_recovery_state")
  ) {
    const userIndex = commandArgs.indexOf("-U");
    const databaseIndex = commandArgs.indexOf("-d");
    if (
      userIndex < 0
      || commandArgs[userIndex + 1] !== "fst"
      || databaseIndex < 0
      || commandArgs[databaseIndex + 1] !== "fstservice"
    ) {
      process.stderr.write(
        "active recovery query requires explicit PostgreSQL identity\n"
      );
      process.exit(98);
    }
    const state = activeRecoveryDatabaseState();
    if (state) {
      process.stdout.write(JSON.stringify(state));
    }
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

if (args[0] === "rm" && args.at(-1) === runtime.workerContainerId) {
  if (scenario.workerRemoveFails) {
    process.exit(1);
  }
  event("worker-remove", ["fstworker"]);
  runtime.workerStarted = false;
  runtime.workerState = "missing|none";
  runtime.workerContainerId = null;
  saveRuntime();
  process.exit(0);
}

if (
  args[0] === "stop"
  && (
    args.at(-1) === "fstworker"
    || args.at(-1) === runtime.workerContainerId
  )
) {
  event("worker-stop", ["fstworker"]);
  runtime.workerStarted = false;
  runtime.workerState = "exited|none";
  saveRuntime();
  process.exit(0);
}

process.stderr.write("unexpected docker invocation: " + args.join(" ") + "\n");
process.exit(97);
`;

const inheritedLockLauncherSource = String.raw`import fcntl
import os
import sys

guard, lock_path, *arguments = sys.argv[1:]
descriptor = os.open(lock_path, os.O_RDWR | os.O_CREAT, 0o600)
fcntl.flock(descriptor, fcntl.LOCK_EX | fcntl.LOCK_NB)
flags = fcntl.fcntl(descriptor, fcntl.F_GETFD)
fcntl.fcntl(descriptor, fcntl.F_SETFD, flags & ~fcntl.FD_CLOEXEC)
os.execve(
    guard,
    [guard, *arguments, "--inherited-worker-lock-fd", str(descriptor)],
    dict(os.environ),
)
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
    Scraper__EnableRegisteredPlayerBandDiscoveryRemainingWorkGrace: "false",
    Scraper__EnableRegisteredBandTargetedProcessingRemainingWorkGrace: "false",
    Scraper__RegisteredBandRemainingWorkGraceMaxDuration: "00:02:00",
    Scraper__RegisteredBandRemainingWorkGraceRecentProgressWindow: "00:01:30",
    Scraper__RegisteredBandRemainingWorkGraceMaxRemainingLookups: "3",
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

function buildAcquisitionCheckpointTerminalizationRunonceConfig({
  workerImage = "example.invalid/fstworker:test"
} = {}) {
  const config = buildComposeConfig({ runOnce: true, workerImage });
  Object.assign(config.services.fstworker.environment, {
    Scraper__EnabledPhases: "All",
    Scraper__RegisteredUserRefreshTimeout: "00:00:00",
    Scraper__EnableAutomaticPathGeneration: "false",
    Features__EnforcePublicationCriticalPhases: "true",
    Features__EnforceScopeCompletenessManifests: "true",
    Features__RequireSuccessfulScrapeWriters: "true",
    Features__UseLeaderboardScopeFingerprints: "true",
    Features__WritePublishedScopeSources: "true",
    Features__UseSnapshotOverlayWorkerReaders: "false",
    Features__UseStoredSoloProjectionRanksForFilteredReads: "false",
    Features__SkipUnchangedPhysicalLeaderboardSnapshots: "false",
    Features__WriteLogicalLeaderboardVersions: "false",
    DatabaseMaintenance__SnapshotRetentionRewriteEnabled: "false",
    ImprovementNotifications__Enabled: "true",
    ImprovementNotifications__Scope: "registered",
    ImprovementNotifications__IncludePlayers: "true",
    ImprovementNotifications__IncludeBands: "true",
    ImprovementNotifications__IncludeSongEvents: "true",
    ImprovementNotifications__IncludeRankings: "true",
    ImprovementNotifications__RefreshSoloProjection: "true",
    ImprovementNotifications__RefreshAllSoloScopesWhenNoImpactedScopes: "false",
    Scraper__RegisteredPlayerBandDiscoveryTimeout: "00:06:00",
    Scraper__RegisteredBandTargetedProcessingTimeout: "00:05:00",
    Scraper__EnableRegisteredPlayerBandDiscoveryRemainingWorkGrace: "false",
    Scraper__EnableRegisteredBandTargetedProcessingRemainingWorkGrace: "false",
    Scraper__RegisteredBandRemainingWorkGraceMaxDuration: "00:02:00",
    Scraper__RegisteredBandRemainingWorkGraceRecentProgressWindow: "00:01:30",
    Scraper__RegisteredBandRemainingWorkGraceMaxRemainingLookups: "3",
    Scraper__RegisteredPlayerBandDiscoveryMaxLookupsPerPass: "80",
    Scraper__RegisteredBandProcessingMaxLookupsPerPass: "80"
  });
  return config;
}

function buildBandMaintenanceProgressRunonceConfig({
  workerImage = "example.invalid/fstworker:test",
  useBatchedMemberStatsAggregation =
    "${BAND_CURRENT_PROJECTION_USE_BATCHED_MEMBER_STATS_AGGREGATION:-false}"
} = {}) {
  const config = buildAcquisitionCheckpointTerminalizationRunonceConfig({
    workerImage
  });
  config.services.fstworker.environment
    .Scraper__BandCurrentProjectionUseBatchedMemberStatsAggregation =
    useBatchedMemberStatsAggregation;
  return config;
}

function buildLeaderboardRivalsBatchRunonceConfig({
  accountBatchSize = "4",
  rivalsMaxDegreeOfParallelism = "2",
  initialCdnLearnedMaxDop = "360"
} = {}) {
  const config = buildComposeConfig({ runOnce: true });
  Object.assign(config.services.fstworker.environment, {
    Scraper__EnabledPhases: "All",
    Scraper__RegisteredUserRefreshTimeout: "00:00:00",
    Scraper__InitialCdnLearnedMaxDop:
      initialCdnLearnedMaxDop,
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
  rivalsMaxDegreeOfParallelism = "2",
  workerImage = "example.invalid/fstworker:test"
} = {}) {
  const config = buildComposeConfig({ runOnce: true, workerImage });
  Object.assign(config.services.fstworker.environment, {
    Scraper__ApiOnly: "false",
    Scraper__DisableScraperWorker: "false",
    Scraper__EnabledPhases: "SoloRankings",
    Scraper__QueryLead: "true",
    Scraper__QueryDrums: "true",
    Scraper__QueryVocals: "true",
    Scraper__QueryBass: "true",
    Scraper__QueryProLead: "true",
    Scraper__QueryProBass: "true",
    Scraper__QueryProVocals: "true",
    Scraper__QueryProCymbals: "true",
    Scraper__QueryProDrums: "true",
    Scraper__RegistrationSyncWorkerOnly: "false",
    Scraper__RegisteredUserRefreshTimeout: "00:00:00",
    Scraper__ResumeScrapeId: resumeScrapeId,
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
  runonceConfig = null,
  scenario = {}
} = {}) {
  const root = await mkdtemp(
    path.join(toolsDirectory, ".fst-worker-compose-guard-test-")
  );
  const binDirectory = path.join(root, "bin");
  const dockerPath = path.join(binDirectory, "docker");
  const lockPath = path.join(root, ".fst-worker-compose-guard.lock");
  const eventsPath = path.join(root, "events.log");
  const inheritedLockLauncherPath = path.join(
    root,
    "inherited-lock-launcher.py"
  );

  await mkdir(binDirectory);
  const effectiveRunonceConfig = runonceConfig
    ?? (
      String(config?.services?.fstworker?.restart ?? "").toLowerCase() === "no"
        ? config
        : buildScrapeResumeRunonceConfig({
            resumeScrapeId: String(scenario.currentScrapeId ?? 1305)
          })
    );
  await Promise.all([
    writeFile(path.join(root, "docker-compose.yml"), "services: {}\n"),
    writeFile(path.join(root, "docker-compose.pia-30.yml"), "services: {}\n"),
    writeFile(path.join(root, "docker-compose.runonce.yml"), "services: {}\n"),
    writeFile(path.join(root, "compose.json"), JSON.stringify(config)),
    writeFile(
      path.join(root, "runonce-compose.json"),
      JSON.stringify(effectiveRunonceConfig)
    ),
    writeFile(path.join(root, "scenario.json"), JSON.stringify(scenario)),
    writeFile(inheritedLockLauncherPath, inheritedLockLauncherSource),
    writeFile(
      path.join(root, "runtime.json"),
      JSON.stringify({
        workerStarted: false,
        workerStartedOnce: false,
        lastWorkerStartMode: "continuous",
        workerState: scenario.workerContainerState ?? "exited|none",
        workerContainerId:
          scenario.workerContainerId ?? "c".repeat(64),
        workerImage:
          config.services.fstworker.image,
        workerImageId:
          scenario.workerImageId
          ?? scenario.resolvedWorkerImageId
          ?? expectedWorkerImageId,
        workerRevision:
          scenario.workerRevision
          ?? scenario.resolvedWorkerRevision
          ?? expectedWorkerRevision,
        workerExitCode: scenario.workerExitCode ?? 0,
        workerStartedAt: "0001-01-01T00:00:00Z",
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
    async runWithInheritedLock(args, overrides = {}) {
      try {
        const result = await execFileAsync(
          "python3",
          [
            inheritedLockLauncherPath,
            guardPath,
            lockPath,
            ...args
          ],
          {
            cwd: repositoryRoot,
            env: { ...environment, ...overrides },
            encoding: "utf8",
            maxBuffer: 1024 * 1024
          }
        );
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

async function createActiveRecoveryHarness({
  scrapeId = 1305,
  publishedScrapeId = 1304,
  scenario = {},
  resumeState = null,
  postRunonceResumeState = null,
  config = null,
  runonceConfig = null
} = {}) {
  const workerImage =
    config?.services?.fstworker?.image
    ?? runonceConfig?.services?.fstworker?.image
    ?? immutableWorkerImage;
  const continuousConfig = config ?? buildComposeConfig({
    workerImage
  });
  const resumeConfig = runonceConfig ?? buildScrapeResumeRunonceConfig({
    resumeScrapeId: String(scrapeId),
    workerImage
  });

  return createHarness({
    config: continuousConfig,
    runonceConfig: resumeConfig,
    scenario: {
      currentUpdateStatus: "stalled",
      currentScrapeId: scrapeId,
      publicReadsFrozen: true,
      publishedScrapeId,
      initialWorkerApiStatus: "offline",
      resumeState: resumeState
        ?? buildActiveRecoveryResumeState({
          scrapeId,
          publishedScrapeId
        }),
      postRunonceResumeState: postRunonceResumeState
        ?? buildActiveRecoveryResumeState({
          scrapeId,
          publishedScrapeId: scrapeId,
          status: "completed"
        }),
      runonceWorkerExitsImmediately: true,
      runoncePostStartCurrentUpdateStatus: "idle",
      runoncePostStartPublicReadsFrozen: false,
      runoncePostStartPublishedScrapeId: scrapeId,
      ...scenario
    }
  });
}

describe("fstworker Compose startup recovery", () => {
  it("qualifies the telemetry schema gate to public bigint columns and constraints", async () => {
    const source = await readFile(guardPath, "utf8");
    const telemetryVerifier = source.match(
      /verify_wire_send_telemetry_schema\(\) \{[\s\S]*?\n\}/
    )?.[0];
    const acquisitionVerifier = source.match(
      /verify_acquisition_checkpoint_schema\(\) \{[\s\S]*?\n\}/
    )?.[0];
    assert.ok(telemetryVerifier);
    assert.ok(acquisitionVerifier);
    assert.match(telemetryVerifier, /table_schema = 'public'/);
    assert.match(telemetryVerifier, /data_type = 'bigint'/);
    assert.match(telemetryVerifier, /udt_name = 'int8'/);
    assert.match(telemetryVerifier, /'public\.scrape_log'::regclass/);
    assert.match(
      telemetryVerifier,
      /psql -X -A -t -q -U fst -d fstservice/
    );
    assert.match(
      acquisitionVerifier,
      /psql -X -A -t -q -U fst -d fstservice/
    );
    assert.doesNotMatch(acquisitionVerifier, /data_type = 'bigint'/);
    assert.doesNotMatch(acquisitionVerifier, /udt_name = 'int8'/);
  });

  it("creates the worker without starting it using supported Compose up flags", async () => {
    const source = await readFile(guardPath, "utf8");
    assert.match(
      source,
      /compose_snapshot true up --no-start --no-deps --force-recreate/
    );
    assert.doesNotMatch(
      source,
      /compose_snapshot true create --no-deps/
    );
  });

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

  it("uses Compose's real default interpolation for the Band aggregation switch", async () => {
    const compose = await readFile(
      path.join(repositoryRoot, "docker-compose.yml"),
      "utf8"
    );
    const mapping =
      "Scraper__BandCurrentProjectionUseBatchedMemberStatsAggregation=${BAND_CURRENT_PROJECTION_USE_BATCHED_MEMBER_STATS_AGGREGATION:-false}";
    assert.match(compose, new RegExp(mapping.replace(/[.*+?^${}()|[\]\\]/g, "\\$&")));

    const root = await mkdtemp(
      path.join(os.tmpdir(), "fst-worker-compose-default-test-")
    );
    const composePath = path.join(root, "compose.yml");
    try {
      await writeFile(
        composePath,
        [
          "services:",
          "  fstworker:",
          "    image: example.invalid/fstworker:test",
          "    environment:",
          `      - ${mapping}`,
          ""
        ].join("\n")
      );
      const environment = { ...process.env };
      delete environment.BAND_CURRENT_PROJECTION_USE_BATCHED_MEMBER_STATS_AGGREGATION;
      const result = await execFileAsync(
        "docker",
        ["compose", "-f", composePath, "config", "--format", "json"],
        { env: environment, encoding: "utf8" }
      );
      const resolved = JSON.parse(result.stdout);
      assert.equal(
        resolved.services.fstworker.environment
          .Scraper__BandCurrentProjectionUseBatchedMemberStatsAggregation,
        "false"
      );
    } finally {
      await rm(root, { recursive: true, force: true });
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

  it("removes a partially started worker when startup reports failure", async () => {
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
        "worker-remove|fstworker"
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

  it("starts the continuous worker from a terminal failed and unfrozen state", async () => {
    const harness = await createHarness({
      scenario: {
        currentUpdateStatus: "failed",
        postStartCurrentUpdateStatus: "failed",
        publicReadsFrozen: false
      }
    });
    try {
      const result = await harness.run();
      assert.equal(result.code, 0, result.stderr);
      assert.deepEqual(await harness.events(), ["worker-start|fstworker"]);
      assert.match(result.stdout, /update=failed reads=unfrozen/);
    } finally {
      await harness.cleanup();
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

  it("refuses worker recovery when the acquisition checkpoint schema is missing", async () => {
    const harness = await createHarness({
      scenario: {
        acquisitionCheckpointSchemaReady: false
      }
    });
    try {
      const result = await harness.run();
      assert.notEqual(result.code, 0);
      assert.deepEqual(await harness.events(), []);
      assert.match(
        result.stderr,
        /requires the acquisition checkpoint release schema/
      );
    } finally {
      await harness.cleanup();
    }
  });

  it("runs active scrape recovery through scrape-resume before restarting the continuous worker", async () => {
    const harness = await createActiveRecoveryHarness();
    try {
      const result = await harness.run(["--recover-start"]);
      assert.equal(result.code, 0, result.stderr);
      assert.deepEqual(await harness.events(), [
        "worker-start|fstworker",
        "worker-start|fstworker"
      ]);
      assert.match(
        result.stdout,
        /recovery=active-candidate scrape=1305 published=1304 mode=scrape-resume/
      );
      assert.match(result.stdout, /recovery=ok .*worker=online heartbeat=fresh/);
    } finally {
      await harness.cleanup();
    }
  });

  it("accepts the schema-v2 publication catalog envelope for active recovery", async () => {
    const harness = await createActiveRecoveryHarness({
      resumeState: buildActiveRecoveryResumeState({
        publicationCatalogSchemaVersion: 2
      })
    });
    try {
      const result = await harness.run(["--recover-start"]);
      assert.equal(result.code, 0, result.stderr);
      assert.deepEqual(await harness.events(), [
        "worker-start|fstworker",
        "worker-start|fstworker"
      ]);
    } finally {
      await harness.cleanup();
    }
  });

  it("observes a running scrape-resume worker until its terminal exit", async () => {
    const harness = await createActiveRecoveryHarness({
      scenario: {
        runonceWorkerExitsImmediately: false,
        runonceWorkerExitsAfterStateProbe: true
      }
    });
    try {
      const result = await harness.run(["--recover-start"]);
      assert.equal(result.code, 0, result.stderr);
      assert.deepEqual(await harness.events(), [
        "worker-start|fstworker",
        "worker-start|fstworker"
      ]);
      assert.doesNotMatch(
        result.stderr,
        /recovery requires fstworker to be stopped or absent/
      );
    } finally {
      await harness.cleanup();
    }
  });

  it("refuses active scrape recovery for a legacy null checkpoint before any mutation", async () => {
    const harness = await createActiveRecoveryHarness({
      resumeState: buildActiveRecoveryResumeState({
        acquisitionCompletedAtUtc: null,
        songsScraped: null,
        totalEntries: null,
        totalRequests: null,
        totalBytes: null
      })
    });
    try {
      const result = await harness.run(["--recover-start"]);
      assert.notEqual(result.code, 0);
      assert.deepEqual(await harness.events(), []);
      assert.match(result.stderr, /acquisition checkpoint is missing/);
    } finally {
      await harness.cleanup();
    }
  });

  it("fails active recovery closed for exact checkpoint and publication mismatches", async () => {
    const cases = [
      {
        name: "deferred publication path",
        resumeState: buildActiveRecoveryResumeState({
          startupShouldResumeDeferredPublication: true
        }),
        expected: /should resume through the existing deferred-publication startup path/
      },
      {
        name: "incomplete manifests",
        resumeState: buildActiveRecoveryResumeState({
          completeManifestCount: 17,
          manifestCount: 18
        }),
        expected: /manifests are incomplete/
      },
      {
        name: "writer failures",
        resumeState: buildActiveRecoveryResumeState({
          writerFailureCount: 1
        }),
        expected: /has writer failures/
      },
      {
        name: "critical failures",
        resumeState: buildActiveRecoveryResumeState({
          criticalPhaseFailureCount: 1
        }),
        expected: /has publication-critical failures/
      },
      {
        name: "wrong fingerprint version",
        resumeState: buildActiveRecoveryResumeState({
          expectedSoloScopeFingerprintVersion: 2
        }),
        expected: /fingerprint version is unsupported/
      },
      {
        name: "unsupported publication catalog schema",
        resumeState: buildActiveRecoveryResumeState({
          publicationCatalogSchemaVersion: 3
        }),
        expected: /publication song catalog schema is unsupported/
      },
      {
        name: "wrong scope count",
        resumeState: buildActiveRecoveryResumeState({
          expectedSoloScopeCount: 17
        }),
        expected: /scope count does not cover the exact catalog and canonical instruments/
      },
      {
        name: "reduced solo scope",
        resumeState: buildActiveRecoveryResumeState({
          completeSoloPairs: [["song-a", "Solo_Guitar"]]
        }),
        expected: /scope count does not cover the exact catalog and canonical instruments/
      },
      {
        name: "nonpositive persisted requests",
        resumeState: buildActiveRecoveryResumeState({
          totalRequests: 0
        }),
        expected: /totalRequests must be a positive persisted value/
      }
    ];

    for (const testCase of cases) {
      const harness = await createActiveRecoveryHarness({
        resumeState: testCase.resumeState
      });
      try {
        const result = await harness.run(["--recover-start"]);
        assert.notEqual(result.code, 0, testCase.name);
        assert.deepEqual(await harness.events(), [], testCase.name);
        assert.match(result.stderr, testCase.expected, testCase.name);
      } finally {
        await harness.cleanup();
      }
    }
  });

  it("refuses active recovery when the prior worker heartbeat is still fresh", async () => {
    const harness = await createActiveRecoveryHarness({
      scenario: {
        initialWorkerApiStatus: "online",
        initialWorkerHeartbeatAgeSeconds: 1,
        initialWorkerStaleAfterSeconds: 90
      }
    });
    try {
      const result = await harness.run(["--recover-start"]);
      assert.notEqual(result.code, 0);
      assert.deepEqual(await harness.events(), []);
      assert.match(result.stderr, /prior worker heartbeat to be stale or offline/);
    } finally {
      await harness.cleanup();
    }
  });

  it("fails active recovery before mutation when the run-once image or config drifts", async () => {
    const wrongImageHarness = await createActiveRecoveryHarness({
      config: buildComposeConfig({ workerImage: immutableWorkerImage }),
      runonceConfig: buildScrapeResumeRunonceConfig({
        resumeScrapeId: "1305",
        workerImage: "example.invalid/fstworker@sha256:" + "d".repeat(64)
      })
    });
    try {
      const result = await wrongImageHarness.run(["--recover-start"]);
      assert.notEqual(result.code, 0);
      assert.deepEqual(await wrongImageHarness.events(), []);
      assert.match(
        result.stderr,
        /recovery run-once worker image must match/
      );
    } finally {
      await wrongImageHarness.cleanup();
    }

    const mismatchedConfig = buildScrapeResumeRunonceConfig({
      resumeScrapeId: "1305",
      workerImage: immutableWorkerImage
    });
    mismatchedConfig.services.fstworker.environment.Features__WriteLogicalLeaderboardVersions =
      "true";
    const configHarness = await createActiveRecoveryHarness({
      runonceConfig: mismatchedConfig
    });
    try {
      const result = await configHarness.run(["--recover-start"]);
      assert.notEqual(result.code, 0);
      assert.deepEqual(await configHarness.events(), []);
      assert.match(
        result.stderr,
        /Features__WriteLogicalLeaderboardVersions=false|active recovery could not start the scrape-resume worker|configuration hash does not match/
      );
    } finally {
      await configHarness.cleanup();
    }
  });

  it("fails active recovery closed when the run-once worker exits without publication convergence", async () => {
    const harness = await createActiveRecoveryHarness({
      postRunonceResumeState: buildActiveRecoveryResumeState({
        status: "running"
      }),
      scenario: {
        runoncePostStartCurrentUpdateStatus: "stalled",
        runoncePostStartPublicReadsFrozen: true,
        runoncePostStartPublishedScrapeId: 1304
      }
    });
    try {
      const result = await harness.run(
        ["--recover-start"],
        { FST_WORKER_RECOVERY_TOTAL_DEADLINE_SECONDS: "3" }
      );
      assert.notEqual(result.code, 0);
      assert.deepEqual(await harness.events(), ["worker-start|fstworker"]);
      assert.match(
        result.stderr,
        /exceeded its total deadline before publication convergence/
      );
    } finally {
      await harness.cleanup();
    }
  });

  it("routes SIGTERM through the active recovery leave-running safeguard once work has begun", async () => {
    const harness = await createActiveRecoveryHarness({
      postRunonceResumeState: buildActiveRecoveryResumeState({
        status: "running"
      }),
      scenario: {
        runonceWorkerExitsImmediately: false,
        runonceWorkerBecomesReady: false,
        runoncePostStartCurrentUpdateStatus: "stalled",
        runoncePostStartPublicReadsFrozen: true,
        runoncePostStartPublishedScrapeId: 1304
      }
    });
    const child = harness.spawnGuard(
      ["--recover-start"],
      {
        FST_WORKER_RECOVERY_TOTAL_DEADLINE_SECONDS: "30"
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
      assert.deepEqual(await harness.events(), ["worker-start|fstworker"]);
      assert.match(stderr, /leaving the worker running/);
    } finally {
      if (child.exitCode == null && child.signalCode == null) {
        child.kill("SIGTERM");
        await once(child, "exit");
      }
      await harness.cleanup();
    }
  });

  it("starts the continuous worker directly when startup needs deferred publication recovery", async () => {
    const harness = await createHarness({
      config: buildComposeConfig({ workerImage: immutableWorkerImage }),
      scenario: {
        currentUpdateStatus: "idle",
        publicReadsFrozen: true,
        freezeReason: "publication-commit-deferred",
        publishedScrapeId: 1305
      }
    });
    try {
      const result = await harness.run([
        "--recover-start",
        "--expected-worker-image",
        immutableWorkerImage,
        "--expected-worker-image-id",
        expectedWorkerImageId,
        "--expected-worker-revision",
        expectedWorkerRevision,
        "--expected-worker-config-sha256",
        workerConfigSha256(buildComposeConfig({ workerImage: immutableWorkerImage }))
      ]);
      assert.equal(result.code, 0, result.stderr);
      assert.deepEqual(await harness.events(), ["worker-start|fstworker"]);
      assert.match(result.stdout, /reads=frozen deferred-publication=1305/);
    } finally {
      await harness.cleanup();
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

  it("binds a worker check to an exact image ID and revision", async () => {
    const config = buildComposeConfig();
    const harness = await createHarness({ config });
    try {
      const result = await harness.run([
        "--check",
        "--config-only",
        "--expected-worker-image",
        "example.invalid/fstworker:test",
        "--expected-worker-image-id",
        expectedWorkerImageId,
        "--expected-worker-revision",
        expectedWorkerRevision,
        "--expected-worker-config-sha256",
        workerConfigSha256(config)
      ]);
      assert.equal(result.code, 0, result.stderr);
      assert.deepEqual(await harness.events(), []);
    } finally {
      await harness.cleanup();
    }
  });

  it("rejects an image reference that resolves to another image ID", async () => {
    const harness = await createHarness();
    try {
      const result = await harness.run([
        "--check",
        "--config-only",
        "--expected-worker-image",
        "example.invalid/fstworker:test",
        "--expected-worker-image-id",
        "sha256:" + "d".repeat(64),
        "--expected-worker-revision",
        expectedWorkerRevision
      ]);
      assert.notEqual(result.code, 0);
      assert.deepEqual(await harness.events(), []);
      assert.match(result.stderr, /resolved to a different image ID/);
    } finally {
      await harness.cleanup();
    }
  });

  it("rejects a mismatched resolved worker configuration hash", async () => {
    const config = buildComposeConfig();
    const harness = await createHarness({ config });
    try {
      const result = await harness.run([
        "--check",
        "--config-only",
        "--expected-worker-image",
        "example.invalid/fstworker:test",
        "--expected-worker-config-sha256",
        "f".repeat(64)
      ]);
      assert.notEqual(result.code, 0);
      assert.deepEqual(await harness.events(), []);
      assert.match(result.stderr, /non-image configuration hash does not match/);
    } finally {
      await harness.cleanup();
    }
  });

  it("removes an unstarted worker whose image identity changed", async () => {
    const harness = await createHarness({
      scenario: {
        createdWorkerImageId: "sha256:" + "d".repeat(64)
      }
    });
    try {
      const result = await harness.run([
        "--recreate",
        "--expected-worker-image",
        "example.invalid/fstworker:test",
        "--expected-worker-image-id",
        expectedWorkerImageId,
        "--expected-worker-revision",
        expectedWorkerRevision
      ]);
      assert.notEqual(result.code, 0);
      assert.deepEqual(await harness.events(), ["worker-remove|fstworker"]);
      assert.match(result.stderr, /created worker image identity does not match/);
    } finally {
      await harness.cleanup();
    }
  });

  it("removes the created worker when Compose cannot return its ID", async () => {
    const harness = await createHarness({
      scenario: {
        composePsFails: true
      }
    });
    try {
      const result = await harness.run(["--recreate"]);
      assert.notEqual(result.code, 0);
      assert.deepEqual(await harness.events(), ["worker-remove|fstworker"]);
    } finally {
      await harness.cleanup();
    }
  });

  it("preserves the previous worker when create fails before replacement", async () => {
    const harness = await createHarness({
      scenario: {
        workerCreateFailsBeforeReplacement: true
      }
    });
    try {
      const result = await harness.run(["--recreate"]);
      assert.notEqual(result.code, 0);
      assert.deepEqual(await harness.events(), []);
      assert.match(result.stderr, /fstworker create failed/);
    } finally {
      await harness.cleanup();
    }
  });

  it("reports when unaccepted worker cleanup cannot be proven", async () => {
    const harness = await createHarness({
      scenario: {
        createdWorkerImageId: "sha256:" + "d".repeat(64),
        workerRemoveFails: true
      }
    });
    try {
      const result = await harness.run([
        "--recreate",
        "--expected-worker-image",
        "example.invalid/fstworker:test",
        "--expected-worker-image-id",
        expectedWorkerImageId,
        "--expected-worker-revision",
        expectedWorkerRevision
      ]);
      assert.notEqual(result.code, 0);
      assert.deepEqual(await harness.events(), []);
      assert.match(result.stderr, /unaccepted worker cleanup failed/);
    } finally {
      await harness.cleanup();
    }
  });

  it("preserves recovery cleanup remediation when containment fails", async () => {
    const harness = await createHarness({
      scenario: {
        composePsFails: true,
        workerRemoveFails: true
      }
    });
    try {
      const result = await harness.run(["--recover-start"]);
      assert.notEqual(result.code, 0);
      assert.deepEqual(await harness.events(), []);
      assert.match(
        result.stderr,
        /unaccepted worker cleanup failed for exact container [0-9a-f]{64}; stop and remove it before retry/
      );
    } finally {
      await harness.cleanup();
    }
  });

  it("removes an unaccepted direct worker when the guard is interrupted", async () => {
    const harness = await createHarness({
      scenario: {
        workerStartDelayMs: 1000
      }
    });
    const child = harness.spawnGuard(["--recreate"]);
    let stdout = "";
    let stderr = "";
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
      const [code] = await exitPromise;
      assert.equal(code, 143, stderr || stdout);
      assert.deepEqual(await harness.events(), [
        "worker-start|fstworker",
        "worker-remove|fstworker"
      ]);
    } finally {
      if (child.exitCode == null && child.signalCode == null) {
        child.kill("SIGTERM");
        await once(child, "exit");
      }
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

  it("retains a same-process inherited canonical lock through startup", async () => {
    const config = buildComposeConfig({
      workerImage: immutableWorkerImage
    });
    const harness = await createHarness({ config });
    try {
      const result = await harness.runWithInheritedLock([
        "--recreate",
        "--expected-worker-image",
        immutableWorkerImage,
        "--expected-worker-image-id",
        expectedWorkerImageId,
        "--expected-worker-revision",
        expectedWorkerRevision,
        "--expected-worker-config-sha256",
        workerConfigSha256(config)
      ]);
      assert.equal(result.code, 0, result.stderr);
      assert.deepEqual(await harness.events(), ["worker-start|fstworker"]);
    } finally {
      await harness.cleanup();
    }
  });

  it("rejects an inherited descriptor that does not own the canonical lock", async () => {
    const config = buildComposeConfig({
      workerImage: immutableWorkerImage
    });
    const harness = await createHarness({ config });
    try {
      const result = await harness.run([
        "--recreate",
        "--expected-worker-image",
        immutableWorkerImage,
        "--expected-worker-image-id",
        expectedWorkerImageId,
        "--expected-worker-revision",
        expectedWorkerRevision,
        "--expected-worker-config-sha256",
        workerConfigSha256(config),
        "--inherited-worker-lock-fd",
        "9"
      ]);
      assert.notEqual(result.code, 0);
      assert.deepEqual(await harness.events(), []);
      assert.match(
        result.stderr,
        /inherited worker lock descriptor or path is invalid|Bad file descriptor/
      );
    } finally {
      await harness.cleanup();
    }
  });

  it("rejects Compose and Docker routing environment overrides", async () => {
    const harness = await createHarness();
    try {
      for (const overrides of [
        { COMPOSE_PROJECT_NAME: "other-project" },
        { DOCKER_HOST: "tcp://127.0.0.1:2375" }
      ]) {
        const result = await harness.run(
          ["--check", "--config-only"],
          overrides
        );
        assert.equal(result.code, 64);
        assert.deepEqual(await harness.events(), []);
        assert.match(result.stderr, /routing environment overrides/);
      }
    } finally {
      await harness.cleanup();
    }
  });

  it("rejects a base Compose path outside the canonical directory", async () => {
    const harness = await createHarness();
    try {
      const result = await harness.run(
        ["--check", "--config-only"],
        { BASE_FILE: "../docker-compose.yml" }
      );
      assert.notEqual(result.code, 0);
      assert.deepEqual(await harness.events(), []);
      assert.match(result.stderr, /canonical base file must resolve/);
    } finally {
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

  it("rejects enabled registered lookup grace in the locked notification profile", async () => {
    const config = buildRunonceComposeConfig();
    config.services.fstworker.environment[
      "Scraper__EnableRegisteredBandTargetedProcessingRemainingWorkGrace"
    ] = "true";
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
      assert.match(
        result.stderr,
        /requires Scraper__EnableRegisteredBandTargetedProcessingRemainingWorkGrace=false/
      );
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

  it("accepts acquisition-checkpoint terminalization with the baseline all-phases contract", async () => {
    const harness = await createHarness({
      config: buildAcquisitionCheckpointTerminalizationRunonceConfig()
    });
    try {
      const result = await harness.run([
        "--check-runonce",
        "--config-only",
        "--throughput-profile",
        "candidate-800-32-4",
        "--data-profile",
        "acquisition-checkpoint-terminalization",
        "--expected-worker-image",
        "example.invalid/fstworker:test"
      ]);
      assert.equal(result.code, 0, result.stderr);
      assert.deepEqual(await harness.events(), []);
      assert.match(
        result.stdout,
        /data_profile=acquisition-checkpoint-terminalization/
      );
    } finally {
      await harness.cleanup();
    }
  });

  it("accepts acquisition-checkpoint terminalization when grace settings are absent and the exact image supplies code defaults", async () => {
    const config = buildAcquisitionCheckpointTerminalizationRunonceConfig({
      workerImage: "fstservice:checkpoint-terminalization-42bf8d9f"
    });
    const environment = config.services.fstworker.environment;
    for (const name of [
      "Scraper__EnableRegisteredPlayerBandDiscoveryRemainingWorkGrace",
      "Scraper__EnableRegisteredBandTargetedProcessingRemainingWorkGrace",
      "Scraper__RegisteredBandRemainingWorkGraceMaxDuration",
      "Scraper__RegisteredBandRemainingWorkGraceRecentProgressWindow",
      "Scraper__RegisteredBandRemainingWorkGraceMaxRemainingLookups"
    ]) {
      delete environment[name];
    }
    const harness = await createHarness({ config });
    try {
      const result = await harness.run([
        "--check-runonce",
        "--config-only",
        "--throughput-profile",
        "candidate-800-32-4",
        "--data-profile",
        "acquisition-checkpoint-terminalization",
        "--expected-worker-image",
        "fstservice:checkpoint-terminalization-42bf8d9f"
      ]);
      assert.equal(result.code, 0, result.stderr);
      assert.deepEqual(await harness.events(), []);
    } finally {
      await harness.cleanup();
    }
  });

  it("rejects acquisition-checkpoint terminalization when an explicit grace setting drifts", async () => {
    const config = buildAcquisitionCheckpointTerminalizationRunonceConfig({
      workerImage: "fstservice:checkpoint-terminalization-42bf8d9f"
    });
    config.services.fstworker.environment.Scraper__RegisteredBandRemainingWorkGraceMaxDuration =
      "00:01:00";
    const harness = await createHarness({ config });
    try {
      const result = await harness.run([
        "--check-runonce",
        "--config-only",
        "--throughput-profile",
        "candidate-800-32-4",
        "--data-profile",
        "acquisition-checkpoint-terminalization",
        "--expected-worker-image",
        "fstservice:checkpoint-terminalization-42bf8d9f"
      ]);
      assert.notEqual(result.code, 0);
      assert.deepEqual(await harness.events(), []);
      assert.match(
        result.stderr,
        /requires Scraper__RegisteredBandRemainingWorkGraceMaxDuration=00:02:00, found 00:01:00/
      );
    } finally {
      await harness.cleanup();
    }
  });

  it("rejects acquisition-checkpoint terminalization when snapshot overlay readers are enabled", async () => {
    const config = buildAcquisitionCheckpointTerminalizationRunonceConfig();
    config.services.fstworker.environment.Features__UseSnapshotOverlayWorkerReaders =
      "true";
    const harness = await createHarness({ config });
    try {
      const result = await harness.run([
        "--check-runonce",
        "--config-only",
        "--data-profile",
        "acquisition-checkpoint-terminalization",
        "--expected-worker-image",
        "example.invalid/fstworker:test"
      ]);
      assert.notEqual(result.code, 0);
      assert.deepEqual(await harness.events(), []);
      assert.match(
        result.stderr,
        /requires Features__UseSnapshotOverlayWorkerReaders=false/
      );
    } finally {
      await harness.cleanup();
    }
  });

  it("keeps acquisition-checkpoint terminalization run-once and image assertions fail-closed", async () => {
    const harness = await createHarness({
      config: buildAcquisitionCheckpointTerminalizationRunonceConfig()
    });
    try {
      const continuousResult = await harness.run([
        "--check",
        "--config-only",
        "--data-profile",
        "acquisition-checkpoint-terminalization",
        "--expected-worker-image",
        "example.invalid/fstworker:test"
      ]);
      assert.equal(continuousResult.code, 64);
      assert.match(
        continuousResult.stderr,
        /requires --check-runonce or --recreate-runonce/
      );

      const missingImageResult = await harness.run([
        "--check-runonce",
        "--config-only",
        "--data-profile",
        "acquisition-checkpoint-terminalization"
      ]);
      assert.equal(missingImageResult.code, 64);
      assert.match(
        missingImageResult.stderr,
        /--expected-worker-image is required with --data-profile/
      );
      assert.deepEqual(await harness.events(), []);
    } finally {
      await harness.cleanup();
    }
  });

  it("accepts band-maintenance progress with checkpoint terminalization defaults", async () => {
    const config = buildBandMaintenanceProgressRunonceConfig({
      workerImage: bandMaintenanceProgressImage
    });
    const harness = await createHarness({
      config,
      scenario: { resolvedWorkerRevision: bandMaintenanceProgressRevision }
    });
    try {
      const result = await harness.run([
        "--check-runonce",
        "--config-only",
        "--throughput-profile",
        "candidate-800-32-4",
        "--data-profile",
        "band-maintenance-progress",
        "--expected-worker-image",
        bandMaintenanceProgressImage,
        "--expected-worker-image-id",
        expectedWorkerImageId,
        "--expected-worker-revision",
        bandMaintenanceProgressRevision
      ]);
      assert.equal(result.code, 0, result.stderr);
      assert.deepEqual(await harness.events(), []);
      assert.match(result.stdout, /data_profile=band-maintenance-progress/);
    } finally {
      await harness.cleanup();
    }
  });

  it("rejects band-maintenance progress omission when the exact image revision is unknown", async () => {
    const config = buildBandMaintenanceProgressRunonceConfig({
      workerImage: bandMaintenanceProgressImage
    });
    const harness = await createHarness({ config });
    try {
      const result = await harness.run([
        "--check-runonce",
        "--config-only",
        "--throughput-profile",
        "candidate-800-32-4",
        "--data-profile",
        "band-maintenance-progress",
        "--expected-worker-image",
        bandMaintenanceProgressImage,
        "--expected-worker-image-id",
        expectedWorkerImageId
      ]);
      assert.notEqual(result.code, 0);
      assert.deepEqual(await harness.events(), []);
      assert.match(
        result.stderr,
        /requires exact expected worker image ID and revision/
      );
    } finally {
      await harness.cleanup();
    }
  });

  it("rejects band-maintenance progress with a wrong candidate image revision", async () => {
    const harness = await createHarness({
      config: buildBandMaintenanceProgressRunonceConfig({
        workerImage: bandMaintenanceProgressImage
      }),
      scenario: { resolvedWorkerRevision: "2".repeat(40) }
    });
    try {
      const result = await harness.run([
        "--check-runonce",
        "--config-only",
        "--throughput-profile",
        "candidate-800-32-4",
        "--data-profile",
        "band-maintenance-progress",
        "--expected-worker-image",
        bandMaintenanceProgressImage,
        "--expected-worker-image-id",
        expectedWorkerImageId,
        "--expected-worker-revision",
        bandMaintenanceProgressRevision
      ]);
      assert.notEqual(result.code, 0);
      assert.deepEqual(await harness.events(), []);
      assert.match(
        result.stderr,
        /expected worker image revision label does not match/
      );
    } finally {
      await harness.cleanup();
    }
  });

  it("rejects band-maintenance progress with a wrong candidate image ID", async () => {
    const harness = await createHarness({
      config: buildBandMaintenanceProgressRunonceConfig({
        workerImage: bandMaintenanceProgressImage
      }),
      scenario: {
        resolvedWorkerImageId: "sha256:" + "b".repeat(64),
        resolvedWorkerRevision: bandMaintenanceProgressRevision
      }
    });
    try {
      const result = await harness.run([
        "--check-runonce",
        "--config-only",
        "--throughput-profile",
        "candidate-800-32-4",
        "--data-profile",
        "band-maintenance-progress",
        "--expected-worker-image",
        bandMaintenanceProgressImage,
        "--expected-worker-image-id",
        expectedWorkerImageId,
        "--expected-worker-revision",
        bandMaintenanceProgressRevision
      ]);
      assert.notEqual(result.code, 0);
      assert.deepEqual(await harness.events(), []);
      assert.match(
        result.stderr,
        /expected worker image object is unavailable|expected worker image reference resolved to a different image ID/
      );
    } finally {
      await harness.cleanup();
    }
  });

  it("requires the Band configuration binding before recreate", async () => {
    const harness = await createHarness({
      config: buildBandMaintenanceProgressRunonceConfig({
        workerImage: bandMaintenanceProgressImage
      }),
      scenario: {
        resolvedWorkerRevision: bandMaintenanceProgressRevision
      }
    });
    try {
      const result = await harness.run([
        "--recreate-runonce",
        "--throughput-profile",
        "candidate-800-32-4",
        "--data-profile",
        "band-maintenance-progress",
        "--expected-worker-image",
        bandMaintenanceProgressImage,
        "--expected-worker-image-id",
        expectedWorkerImageId,
        "--expected-worker-revision",
        bandMaintenanceProgressRevision
      ]);
      assert.equal(result.code, 64);
      assert.match(
        result.stderr,
        /requires --expected-worker-config-sha256 for recreate/
      );
      assert.deepEqual(await harness.events(), []);
    } finally {
      await harness.cleanup();
    }
  });

  it("rejects band-maintenance progress omission with an unknown image binding", async () => {
    const config = buildBandMaintenanceProgressRunonceConfig({
      workerImage: "example.invalid/fstworker:unknown"
    });
    const harness = await createHarness({ config });
    try {
      const result = await harness.run([
        "--check-runonce",
        "--config-only",
        "--throughput-profile",
        "candidate-800-32-4",
        "--data-profile",
        "band-maintenance-progress",
        "--expected-worker-image",
        "example.invalid/fstworker:test",
        "--expected-worker-image-id",
        expectedWorkerImageId,
        "--expected-worker-revision",
        bandMaintenanceProgressRevision
      ]);
      assert.notEqual(result.code, 0);
      assert.deepEqual(await harness.events(), []);
      assert.match(
        result.stderr,
        /resolved fstworker image must match example.invalid\/fstworker:test/
      );
    } finally {
      await harness.cleanup();
    }
  });

  it("rejects every alternate throughput profile for band-maintenance progress", async () => {
    for (const throughputProfile of [
      "baseline-up-to-800-32-4",
      "candidate-1600-64-8",
      "candidate-1800-72-9",
      "candidate-2000-80-10",
      "candidate-2880-128-16"
    ]) {
      const harness = await createHarness({
        config: buildBandMaintenanceProgressRunonceConfig()
      });
      try {
        const result = await harness.run([
          "--check-runonce",
          "--config-only",
          "--throughput-profile",
          throughputProfile,
          "--data-profile",
          "band-maintenance-progress",
          "--expected-worker-image",
          "example.invalid/fstworker:test"
        ]);
        assert.equal(result.code, 64, throughputProfile);
        assert.match(
          result.stderr,
          /requires throughput profile candidate-800-32-4/
        );
        assert.deepEqual(await harness.events(), []);
      } finally {
        await harness.cleanup();
      }
    }
  });

  it("rejects band-maintenance progress when batched aggregation is enabled", async () => {
    const harness = await createHarness({
      config: buildBandMaintenanceProgressRunonceConfig({
        workerImage: bandMaintenanceProgressImage,
        useBatchedMemberStatsAggregation: "true"
      }),
      scenario: { resolvedWorkerRevision: bandMaintenanceProgressRevision }
    });
    try {
      const result = await harness.run([
        "--check-runonce",
        "--config-only",
        "--throughput-profile",
        "candidate-800-32-4",
        "--data-profile",
        "band-maintenance-progress",
        "--expected-worker-image",
        bandMaintenanceProgressImage,
        "--expected-worker-image-id",
        expectedWorkerImageId,
        "--expected-worker-revision",
        bandMaintenanceProgressRevision
      ]);
      assert.notEqual(result.code, 0);
      assert.deepEqual(await harness.events(), []);
      assert.match(
        result.stderr,
        /requires Scraper__BandCurrentProjectionUseBatchedMemberStatsAggregation=false/
      );
    } finally {
      await harness.cleanup();
    }
  });

  it("keeps band-maintenance progress run-once and image assertions fail-closed", async () => {
    const harness = await createHarness({
      config: buildBandMaintenanceProgressRunonceConfig()
    });
    try {
      const continuousResult = await harness.run([
        "--check",
        "--config-only",
        "--data-profile",
        "band-maintenance-progress",
        "--expected-worker-image",
        bandMaintenanceProgressImage
      ]);
      assert.equal(continuousResult.code, 64);
      assert.match(
        continuousResult.stderr,
        /requires --check-runonce or --recreate-runonce/
      );

      const missingImageResult = await harness.run([
        "--check-runonce",
        "--config-only",
        "--data-profile",
        "band-maintenance-progress"
      ]);
      assert.equal(missingImageResult.code, 64);
      assert.match(
        missingImageResult.stderr,
        /--expected-worker-image is required with --data-profile/
      );
      assert.deepEqual(await harness.events(), []);
    } finally {
      await harness.cleanup();
    }
  });

  it("accepts wire-send telemetry in config-only mode with the exact candidate contract", async () => {
    const config = buildBandMaintenanceProgressRunonceConfig({
      workerImage: wireSendTelemetryImage
    });
    const harness = await createHarness({
      config,
      scenario: { resolvedWorkerRevision: wireSendTelemetryRevision }
    });
    try {
      const result = await harness.run([
        "--check-runonce",
        "--config-only",
        "--throughput-profile",
        "candidate-800-32-4",
        "--data-profile",
        "wire-send-telemetry",
        "--expected-worker-image",
        wireSendTelemetryImage,
        "--expected-worker-image-id",
        expectedWorkerImageId,
        "--expected-worker-revision",
        wireSendTelemetryRevision
      ]);
      assert.equal(result.code, 0, result.stderr);
      assert.deepEqual(await harness.events(), []);
      assert.match(result.stdout, /data_profile=wire-send-telemetry/);
    } finally {
      await harness.cleanup();
    }
  });

  it("rejects wire-send telemetry action, network, identity, and recreate binding drift", async () => {
    const config = buildBandMaintenanceProgressRunonceConfig({
      workerImage: wireSendTelemetryImage
    });
    const configHash = workerConfigSha256(config);
    for (const args of [
      [
        "--check",
        "--config-only",
        "--throughput-profile",
        "candidate-800-32-4"
      ],
      [
        "--check-runonce",
        "--config-only",
        "--throughput-profile",
        "candidate-1600-64-8"
      ],
      [
        "--check-runonce",
        "--config-only",
        "--throughput-profile",
        "candidate-800-32-4",
        "--expected-worker-image-id",
        "sha256:" + "b".repeat(64)
      ],
      [
        "--recreate-runonce",
        "--throughput-profile",
        "candidate-800-32-4",
        "--expected-worker-config-sha256",
        "f".repeat(64)
      ]
    ]) {
      const harness = await createHarness({
        config,
        scenario: {
          resolvedWorkerRevision: wireSendTelemetryRevision,
          resolvedWorkerImageId:
            args.includes("--expected-worker-image-id")
              ? "sha256:" + "a".repeat(64)
              : undefined
        }
      });
      try {
        const result = await harness.run([
          ...args,
          "--data-profile",
          "wire-send-telemetry",
          "--expected-worker-image",
          wireSendTelemetryImage,
          ...(args.includes("--expected-worker-image-id")
            ? []
            : ["--expected-worker-image-id", expectedWorkerImageId]),
          "--expected-worker-revision",
          wireSendTelemetryRevision
        ]);
        assert.notEqual(result.code, 0);
        assert.deepEqual(await harness.events(), []);
      } finally {
        await harness.cleanup();
      }
    }
    assert.match(configHash, /^[0-9a-f]{64}$/);
  });

  it("rejects wire-send telemetry when batched aggregation is enabled", async () => {
    const config = buildBandMaintenanceProgressRunonceConfig({
      workerImage: wireSendTelemetryImage,
      useBatchedMemberStatsAggregation: "true"
    });
    const harness = await createHarness({
      config,
      scenario: { resolvedWorkerRevision: wireSendTelemetryRevision }
    });
    try {
      const result = await harness.run([
        "--check-runonce",
        "--config-only",
        "--throughput-profile",
        "candidate-800-32-4",
        "--data-profile",
        "wire-send-telemetry",
        "--expected-worker-image",
        wireSendTelemetryImage,
        "--expected-worker-image-id",
        expectedWorkerImageId,
        "--expected-worker-revision",
        wireSendTelemetryRevision
      ]);
      assert.notEqual(result.code, 0);
      assert.match(
        result.stderr,
        /requires Scraper__BandCurrentProjectionUseBatchedMemberStatsAggregation=false/
      );
      assert.deepEqual(await harness.events(), []);
    } finally {
      await harness.cleanup();
    }
  });

  it("requires the live wire-send telemetry schema before worker action", async () => {
    for (const schemaState of [
      "ready",
      "missing",
      "wrong-type",
      "wrong-schema-constraint"
    ]) {
      const config = buildBandMaintenanceProgressRunonceConfig({
        workerImage: wireSendTelemetryImage
      });
      const harness = await createHarness({
        config,
        scenario: {
          resolvedWorkerRevision: wireSendTelemetryRevision,
          wireSendTelemetrySchemaState: schemaState
        }
      });
      try {
        const result = await harness.run([
          "--check-runonce",
          "--throughput-profile",
          "candidate-800-32-4",
          "--data-profile",
          "wire-send-telemetry",
          "--expected-worker-image",
          wireSendTelemetryImage,
          "--expected-worker-image-id",
          expectedWorkerImageId,
          "--expected-worker-revision",
          wireSendTelemetryRevision
        ]);
        if (schemaState === "ready") {
          assert.equal(result.code, 0, result.stderr);
          assert.match(result.stdout, /schema=wire-send-telemetry-ready/);
        } else {
          assert.notEqual(result.code, 0);
          assert.match(result.stderr, /requires all six telemetry columns/);
        }
        assert.deepEqual(await harness.events(), []);
      } finally {
        await harness.cleanup();
      }
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

  it("rejects a changed learned CDN concurrency ceiling", async () => {
    const harness = await createHarness({
      config: buildLeaderboardRivalsBatchRunonceConfig({
        initialCdnLearnedMaxDop: "200"
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
        /Scraper__InitialCdnLearnedMaxDop=360/
      );
    } finally {
      await harness.cleanup();
    }
  });

  it("accepts a guarded scrape resume with persisted metrics and rivals cap", async () => {
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

  it("rejects scrape-resume on non-full-worker hosting modes before any mutation", async () => {
    const cases = [
      {
        name: "api-only",
        key: "Scraper__ApiOnly",
        value: "true",
        expected: /Scraper__ApiOnly=false/
      },
      {
        name: "frontend-only",
        key: "Scraper__DisableScraperWorker",
        value: "true",
        expected: /Scraper__DisableScraperWorker=false/
      },
      {
        name: "registration-sync-only",
        key: "Scraper__RegistrationSyncWorkerOnly",
        value: "true",
        expected: /Scraper__RegistrationSyncWorkerOnly=false/
      }
    ];

    for (const testCase of cases) {
      const runonceConfig = buildScrapeResumeRunonceConfig({
        workerImage: immutableWorkerImage
      });
      runonceConfig.services.fstworker.environment[testCase.key] =
        testCase.value;

      const genericHarness = await createHarness({
        config: runonceConfig
      });
      try {
        const result = await genericHarness.run([
          "--recreate-runonce",
          "--data-profile",
          "scrape-resume",
          "--expected-worker-image",
          immutableWorkerImage
        ]);
        assert.notEqual(result.code, 0, testCase.name);
        assert.deepEqual(await genericHarness.events(), [], testCase.name);
        assert.match(result.stderr, testCase.expected, testCase.name);
      } finally {
        await genericHarness.cleanup();
      }

      const activeHarness = await createActiveRecoveryHarness({
        config: buildComposeConfig({
          workerImage: immutableWorkerImage
        }),
        runonceConfig
      });
      try {
        const result = await activeHarness.run(["--recover-start"]);
        assert.notEqual(result.code, 0, testCase.name);
        assert.deepEqual(await activeHarness.events(), [], testCase.name);
        assert.match(result.stderr, testCase.expected, testCase.name);
      } finally {
        await activeHarness.cleanup();
      }
    }
  });

  it("rejects scrape-resume when any canonical solo query flag is disabled", async () => {
    const cases = [
      { key: "Scraper__QueryLead", instrument: "Lead" },
      { key: "Scraper__QueryDrums", instrument: "Drums" },
      { key: "Scraper__QueryVocals", instrument: "Vocals" },
      { key: "Scraper__QueryBass", instrument: "Bass" },
      { key: "Scraper__QueryProLead", instrument: "ProLead" },
      { key: "Scraper__QueryProBass", instrument: "ProBass" },
      { key: "Scraper__QueryProVocals", instrument: "ProVocals" },
      { key: "Scraper__QueryProCymbals", instrument: "ProCymbals" },
      { key: "Scraper__QueryProDrums", instrument: "ProDrums" }
    ];

    for (const testCase of cases) {
      const runonceConfig = buildScrapeResumeRunonceConfig({
        workerImage: immutableWorkerImage
      });
      runonceConfig.services.fstworker.environment[testCase.key] = "false";

      const genericHarness = await createHarness({
        config: runonceConfig
      });
      try {
        const result = await genericHarness.run([
          "--recreate-runonce",
          "--data-profile",
          "scrape-resume",
          "--expected-worker-image",
          immutableWorkerImage
        ]);
        assert.notEqual(result.code, 0, testCase.instrument);
        assert.deepEqual(await genericHarness.events(), [], testCase.instrument);
        assert.match(
          result.stderr,
          new RegExp(`${testCase.key}=true`),
          testCase.instrument
        );
      } finally {
        await genericHarness.cleanup();
      }

      const activeHarness = await createActiveRecoveryHarness({
        config: buildComposeConfig({
          workerImage: immutableWorkerImage
        }),
        runonceConfig
      });
      try {
        const result = await activeHarness.run(["--recover-start"]);
        assert.notEqual(result.code, 0, testCase.instrument);
        assert.deepEqual(await activeHarness.events(), [], testCase.instrument);
        assert.match(
          result.stderr,
          new RegExp(`${testCase.key}=true`),
          testCase.instrument
        );
      } finally {
        await activeHarness.cleanup();
      }
    }
  });

  it("rejects a scrape resume without a positive scrape id", async () => {
    const harness = await createHarness({
      config: buildScrapeResumeRunonceConfig({ resumeScrapeId: "0" })
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
      assert.match(result.stderr, /must be greater than zero/);
    } finally {
      await harness.cleanup();
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

  it("accepts a run-once worker that exits before post-start inspection", async () => {
    const harness = await createHarness({
      config: buildRunonceComposeConfig(),
      scenario: {
        workerExitsImmediately: true
      }
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

  it("rejects a run-once worker that immediately exits nonzero", async () => {
    const harness = await createHarness({
      config: buildRunonceComposeConfig(),
      scenario: {
        workerExitsImmediately: true,
        workerExitCode: 2
      }
    });
    try {
      const result = await harness.run([
        "--recreate-runonce",
        "--data-profile",
        "notification-db-only",
        "--expected-worker-image",
        "example.invalid/fstworker:test"
      ]);
      assert.notEqual(result.code, 0);
      assert.deepEqual(await harness.events(), [
        "worker-start|fstworker",
        "worker-remove|fstworker"
      ]);
      assert.match(result.stderr, /run-once worker start was not observed/);
    } finally {
      await harness.cleanup();
    }
  });

  it("rejects a run-once worker that retains the continuous restart policy", async () => {
    const config = buildRunonceComposeConfig();
    config.services.fstworker.restart = "on-failure:5";
    const harness = await createHarness({ runonceConfig: config });
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
