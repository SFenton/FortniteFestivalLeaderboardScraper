#!/usr/bin/env node

import { spawnSync } from "node:child_process";
import { mkdirSync, writeFileSync } from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";

export const NO_PROGRESS_FAILURE_PHASE = "post_process_no_progress_abandoned";
export const CAPACITY_FAILURE_PHASE = "capacity_watchdog_abandoned";
export const WATCHDOG_RECOVERY_EXIT_CODE = 42;
export const WORKER_APPLICATION_NAMES = [
  "fstworker-scraper",
  "fst-path-generation-admission"
];

const scriptPath = fileURLToPath(import.meta.url);
const repositoryRoot = path.resolve(path.dirname(scriptPath), "..");

export function evaluateNoProgressObservation(
  observation,
  {
    idleSeconds = 2700,
    maxPhaseSeconds = 0,
    recoverWorkerExit = false,
    maxWorkerMemoryPercent = 0,
    workerExitGraceSeconds = 120
  } = {}
) {
  if (!observation.workerRunning && !recoverWorkerExit) {
    return { decision: "inactive", reason: "worker_container_not_running" };
  }
  const publicationInProgress =
    observation.scrapeStatus === "completed"
    && observation.publicReadsFrozen === true
    && observation.publicReadsFrozenReason === "publish";
  if (publicationInProgress && recoverWorkerExit) {
    return {
      decision: "terminal",
      reason: "publication_recovery_requires_operator"
    };
  }
  if (observation.scrapeStatus !== "running" && !publicationInProgress) {
    return { decision: "terminal", reason: `scrape_${observation.scrapeStatus ?? "missing"}` };
  }
  if (
    observation.publicReadsFrozenReason !== "post-process"
    && observation.publicReadsFrozenReason !== "publish"
  ) {
    return { decision: "outside_post_process", reason: "publication_not_in_post_process" };
  }

  const nowMs = parseTimestamp(observation.observedAt, "observedAt");
  const operation = observation.operation ?? {};
  const normalizedAttempt = observation.normalizedPhaseAttempt ?? null;
  const subOperation =
    operation.SubOperation
    ?? operation.subOperation
    ?? "";
  const progressCandidates = normalizedAttempt
    ? [
        normalizedAttempt.lastProgressAt,
        normalizedAttempt.startedAt,
        observation.scrapeStartedAt
      ]
    : [
        operation.UpdatedAtUtc,
        operation.updatedAtUtc,
        operation.StartedAtUtc,
        operation.startedAtUtc,
        observation.latestPhaseProgressAt,
        observation.scrapeStartedAt
      ];
  if (
    (normalizedAttempt?.phaseId === "post.refresh_registered_users"
      || (!normalizedAttempt && subOperation === "RefreshRegisteredUsers"))
    && observation.registeredRefreshProgressAt
  ) {
    progressCandidates.push(observation.registeredRefreshProgressAt);
  }
  const parsedProgressCandidates = progressCandidates
    .filter(Boolean)
    .map(value => parseTimestamp(value, "progress timestamp"));
  const latestProgressMs = Math.max(...parsedProgressCandidates);
  const idleForSeconds = Math.max(0, (nowMs - latestProgressMs) / 1000);

  const phaseStartedValue =
    normalizedAttempt?.startedAt
    ?? operation.StartedAtUtc
    ?? operation.startedAtUtc
    ?? observation.scrapeStartedAt;
  const phaseElapsedSeconds = phaseStartedValue
    ? Math.max(0, (nowMs - parseTimestamp(phaseStartedValue, "phase start")) / 1000)
    : 0;
  const activeWorkerQueries = Number(observation.activeWorkerQueries ?? 0);
  const workerMemoryPercent = Number.isFinite(observation.workerMemoryPercent)
    ? Number(observation.workerMemoryPercent)
    : null;
  const configuredMemoryPercent =
    Number.isFinite(maxWorkerMemoryPercent) && maxWorkerMemoryPercent > 0
      ? Number(maxWorkerMemoryPercent)
      : null;
  const resourceFields = {
    workerMemoryPercent,
    maxWorkerMemoryPercent: configuredMemoryPercent
  };

  if (!observation.workerRunning) {
    const workerExitCode = Number(observation.workerExitCode ?? 0);
    const workerFinishedAtMs = Date.parse(observation.workerFinishedAt ?? "");
    const workerExitAgeSeconds = Number.isFinite(workerFinishedAtMs)
      ? Math.max(0, (nowMs - workerFinishedAtMs) / 1000)
      : null;
    const configuredExitGraceSeconds =
      Number.isFinite(workerExitGraceSeconds) && workerExitGraceSeconds > 0
        ? Number(workerExitGraceSeconds)
        : 0;
    if (
      !observation.workerOomKilled
      && workerExitCode === 0
      && workerExitAgeSeconds !== null
      && workerExitAgeSeconds < configuredExitGraceSeconds
    ) {
      return {
        decision: "healthy",
        reason: "worker_exit_grace_period",
        idleForSeconds,
        phaseElapsedSeconds,
        activeWorkerQueries,
        workerExitCode,
        workerExitAgeSeconds,
        workerExitGraceSeconds: configuredExitGraceSeconds,
        ...resourceFields
      };
    }
    return {
      decision: "timeout",
      reason: observation.workerOomKilled
        ? "worker_oom_killed"
        : workerExitCode === 0
          ? "worker_container_exited"
          : "worker_process_failed",
      idleForSeconds,
      phaseElapsedSeconds,
      activeWorkerQueries,
      workerExitCode,
      workerExitAgeSeconds,
      workerExitGraceSeconds: configuredExitGraceSeconds,
      ...resourceFields
    };
  }

  if (
    configuredMemoryPercent !== null
    && workerMemoryPercent !== null
    && workerMemoryPercent >= configuredMemoryPercent
  ) {
    return {
      decision: "timeout",
      reason: "worker_memory_threshold_exceeded",
      idleForSeconds,
      phaseElapsedSeconds,
      activeWorkerQueries,
      ...resourceFields
    };
  }

  if (activeWorkerQueries > 0) {
    return {
      decision: "defer_active_query",
      reason: "worker_database_activity_present",
      idleForSeconds,
      phaseElapsedSeconds,
      activeWorkerQueries,
      ...resourceFields
    };
  }

  if (maxPhaseSeconds > 0 && phaseElapsedSeconds >= maxPhaseSeconds) {
    return {
      decision: "timeout",
      reason: "max_phase_duration_exceeded",
      idleForSeconds,
      phaseElapsedSeconds,
      activeWorkerQueries,
      ...resourceFields
    };
  }

  if (idleForSeconds >= idleSeconds) {
    return {
      decision: "timeout",
      reason: "no_phase_progress",
      idleForSeconds,
      phaseElapsedSeconds,
      activeWorkerQueries,
      ...resourceFields
    };
  }

  return {
    decision: "healthy",
    reason: "phase_progress_within_threshold",
    idleForSeconds,
    phaseElapsedSeconds,
    activeWorkerQueries,
    ...resourceFields
  };
}

export function parseDockerPercentage(value) {
  const match = /^\s*(\d+(?:\.\d+)?)%\s*$/.exec(String(value));
  if (!match) {
    throw new Error(`Invalid Docker percentage: ${value}`);
  }
  const percent = Number(match[1]);
  if (!Number.isFinite(percent) || percent < 0 || percent > 100) {
    throw new Error(`Docker percentage is outside 0-100: ${value}`);
  }
  return percent;
}

export function selectFailureIsolationPhase(decision) {
  return decision.reason === "worker_memory_threshold_exceeded"
    || decision.reason === "worker_oom_killed"
    || decision.reason === "worker_process_failed"
    || decision.reason === "worker_container_exited"
    ? CAPACITY_FAILURE_PHASE
    : NO_PROGRESS_FAILURE_PHASE;
}

export function buildFailureIsolationCommand({
  serviceContainer = "fstservice",
  scrapeId,
  publishedScrapeId,
  failurePhase,
  failureMessage,
  execute = true
}) {
  const normalizedScrapeId = requirePositiveInteger(scrapeId, "scrapeId");
  const normalizedPublishedId = requirePositiveInteger(publishedScrapeId, "publishedScrapeId");
  if (execute) {
    if (
      failurePhase !== NO_PROGRESS_FAILURE_PHASE
      && failurePhase !== CAPACITY_FAILURE_PHASE
    ) {
      throw new Error(`Unsupported failure phase: ${failurePhase}`);
    }
    if (!String(failureMessage ?? "").trim()) {
      throw new Error("Failure message is required.");
    }
  }
  return [
    "exec",
    "-i",
    serviceContainer,
    "dotnet",
    "FSTService.dll",
    "--active-scrape-failure-isolation",
    ...(execute
      ? ["--active-scrape-failure-isolation-execute"]
      : ["--active-scrape-failure-isolation-check"]),
    "--active-scrape-id",
    String(normalizedScrapeId),
    "--published-scrape-id",
    String(normalizedPublishedId),
    ...(execute
      ? [
          "--active-scrape-failure-phase",
          failurePhase,
          "--active-scrape-failure-message",
          failureMessage
        ]
      : [])
  ];
}

export function parseFailureIsolationReadinessResult(
  output,
  { scrapeId, publishedScrapeId }
) {
  let parsed;
  try {
    parsed = JSON.parse(output);
  } catch (error) {
    throw new Error(
      `Active-scrape failure-isolation readiness returned malformed JSON: ${sanitizeError(error)}`
    );
  }
  if (!parsed || typeof parsed !== "object" || Array.isArray(parsed)) {
    throw new Error(
      "Active-scrape failure-isolation readiness returned a non-object payload."
    );
  }
  const expectedScrapeId = requirePositiveInteger(scrapeId, "scrapeId");
  const expectedPublishedId = requirePositiveInteger(
    publishedScrapeId,
    "publishedScrapeId"
  );
  const requireIntegerField = (fieldName) => {
    const value = Number(parsed[fieldName]);
    if (!Number.isSafeInteger(value) || value < 0) {
      throw new Error(
        `Active-scrape failure-isolation readiness field ${fieldName} is invalid.`
      );
    }
    return value;
  };
  const requireOptionalIntegerField = (fieldName) => {
    const value = parsed[fieldName];
    if (value === null) {
      return null;
    }
    const number = Number(value);
    if (!Number.isSafeInteger(number) || number <= 0) {
      throw new Error(
        `Active-scrape failure-isolation readiness field ${fieldName} is invalid.`
      );
    }
    return number;
  };
  const requireBooleanField = (fieldName) => {
    if (typeof parsed[fieldName] !== "boolean") {
      throw new Error(
        `Active-scrape failure-isolation readiness field ${fieldName} is invalid.`
      );
    }
    return parsed[fieldName];
  };
  if (requireIntegerField("ScrapeId") !== expectedScrapeId) {
    throw new Error(
      `Active-scrape failure-isolation readiness reported scrape ${parsed.ScrapeId}, expected ${expectedScrapeId}.`
    );
  }
  if (
    requireIntegerField("ExpectedPublishedScrapeId")
      !== expectedPublishedId
  ) {
    throw new Error(
      "Active-scrape failure-isolation readiness did not preserve the requested published scrape identity."
    );
  }
  if (
    requireOptionalIntegerField("PublishedScrapeId")
      !== expectedPublishedId
  ) {
    throw new Error(
      `Active-scrape failure-isolation readiness reported published scrape ${parsed.PublishedScrapeId}, expected ${expectedPublishedId}.`
    );
  }
  if (parsed.CandidateStatus !== "running" && parsed.CandidateStatus !== "failed") {
    throw new Error(
      `Active-scrape failure-isolation readiness reported unsupported candidate status ${parsed.CandidateStatus}.`
    );
  }
  if (!requireBooleanField("CanExecute")) {
    throw new Error(
      `Active-scrape failure-isolation readiness refused recovery: ${parsed.BlockingReason ?? "unknown blocker"}.`
    );
  }
  const publicationMutationRequired =
    requireBooleanField("PublicationMutationRequired");
  const publicationIsolationComplete =
    requireBooleanField("PublicationIsolationComplete");
  const acquisitionFailureMutationRequired =
    requireBooleanField("AcquisitionFailureMutationRequired");
  if (
    Number(publicationMutationRequired)
      + Number(publicationIsolationComplete)
      + Number(acquisitionFailureMutationRequired)
      !== 1
  ) {
    throw new Error(
      "Active-scrape failure-isolation readiness must report exactly one isolation state."
    );
  }
  const publicReadsFrozen =
    requireBooleanField("PublicReadsFrozen");
  const frozenScrapeId =
    requireOptionalIntegerField("FrozenScrapeId");
  const workingPublicationId =
    requireOptionalIntegerField("WorkingPublicationId");
  const candidatePublicationId =
    requireOptionalIntegerField("CandidatePublicationId");
  if (candidatePublicationId === null) {
    throw new Error(
      "Active-scrape failure-isolation readiness requires a candidate publication."
    );
  }
  if (
    typeof parsed.CandidatePublicationStatus !== "string"
    || !parsed.CandidatePublicationStatus.trim()
  ) {
    throw new Error(
      "Active-scrape failure-isolation readiness did not report candidate publication status."
    );
  }
  if (publicationMutationRequired) {
    if (!publicReadsFrozen) {
      throw new Error(
        "Active-scrape failure-isolation readiness lost the required public-read freeze."
      );
    }
    if (frozenScrapeId !== expectedPublishedId) {
      throw new Error(
        `Active-scrape failure-isolation readiness reported frozen published scrape ${parsed.FrozenScrapeId}, expected ${expectedPublishedId}.`
      );
    }
    if (parsed.FreezeReason !== "post-process") {
      throw new Error(
        `Active-scrape failure-isolation readiness reported freeze reason ${parsed.FreezeReason}, expected post-process.`
      );
    }
    if (workingPublicationId !== candidatePublicationId) {
      throw new Error(
        `Active-scrape failure-isolation readiness reported candidate publication ${candidatePublicationId} and working publication ${workingPublicationId}.`
      );
    }
  } else if (acquisitionFailureMutationRequired) {
    if (
      parsed.CandidateStatus !== "running"
      || publicReadsFrozen
      || frozenScrapeId !== null
      || parsed.FreezeReason !== null
      || workingPublicationId !== candidatePublicationId
      || parsed.CandidatePublicationStatus === "current"
      || parsed.CandidatePublicationStatus === "retained"
      || parsed.CandidatePublicationStatus === "retired"
      || requireIntegerField("RunningPhaseAttemptCount") !== 0
      || requireIntegerField("FailedAcquisitionPhaseAttemptCount") === 0
      || requireBooleanField("AcquisitionCheckpointPresent")
      || parsed.WorkerStatus !== "offline"
      || requireBooleanField("WorkerCurrentOperationPresent")
    ) {
      throw new Error(
        "Active-scrape failure-isolation readiness reported an invalid acquisition-failure state."
      );
    }
  } else if (
    parsed.CandidateStatus !== "failed"
    || publicReadsFrozen
    || frozenScrapeId !== null
    || parsed.FreezeReason !== null
    || workingPublicationId !== null
    || parsed.CandidatePublicationStatus !== "failed"
  ) {
    throw new Error(
      "Active-scrape failure-isolation readiness reported an invalid terminalized publication state."
    );
  }
  if (requireIntegerField("CandidatePublishedScopeRowCount") !== 0) {
    throw new Error(
      "Active-scrape failure-isolation readiness reported candidate published-scope rows."
    );
  }
  if (requireIntegerField("ActiveWorkerQueryCount") !== 0) {
    throw new Error(
      "Active-scrape failure-isolation readiness reported active worker queries."
    );
  }
  if (requireIntegerField("WaitingLockCount") !== 0) {
    throw new Error(
      "Active-scrape failure-isolation readiness reported waiting database locks."
    );
  }
  if (requireIntegerField("AdvisoryLockCount") !== 0) {
    throw new Error(
      "Active-scrape failure-isolation readiness reported advisory database locks."
    );
  }
  if (requireBooleanField("MaintenanceActivityPresent")) {
    throw new Error(
      "Active-scrape failure-isolation readiness reported active database maintenance."
    );
  }
  requireIntegerField("RunningPhaseAttemptCount");
  requireIntegerField("FailedAcquisitionPhaseAttemptCount");
  requireBooleanField("AcquisitionCheckpointPresent");
  if (requireIntegerField("ForeignRunningPhaseAttemptCount") !== 0) {
    throw new Error(
      "Active-scrape failure-isolation readiness reported phase attempts owned by another worker instance."
    );
  }
  if (
    typeof parsed.WorkerStatus !== "string"
    || !parsed.WorkerStatus.trim()
  ) {
    throw new Error(
      "Active-scrape failure-isolation readiness did not report persisted worker status."
    );
  }
  if (
    typeof parsed.WorkerInstanceId !== "string"
    || !parsed.WorkerInstanceId.trim()
  ) {
    throw new Error(
      "Active-scrape failure-isolation readiness did not report persisted worker identity."
    );
  }
  if (
    typeof parsed.WorkerUpdatedAtUtc !== "string"
    || !Number.isFinite(Date.parse(parsed.WorkerUpdatedAtUtc))
  ) {
    throw new Error(
      "Active-scrape failure-isolation readiness did not report valid worker freshness."
    );
  }
  requireBooleanField("WorkerCurrentOperationPresent");
  return parsed;
}

export function verifyFailureIsolationReadiness({
  composeDir,
  serviceContainer,
  evidenceDir,
  scrapeId,
  publishedScrapeId,
  runCommand = run,
  writeArtifact = writeFileSync
}) {
  const readinessCommand = buildFailureIsolationCommand({
    serviceContainer,
    scrapeId,
    publishedScrapeId,
    execute: false
  });
  writeArtifact(
    path.join(evidenceDir, "readiness-command.json"),
    `${JSON.stringify(readinessCommand, null, 2)}\n`
  );
  const readinessOutput = runCommand(
    "docker",
    readinessCommand,
    { cwd: composeDir }
  );
  writeArtifact(
    path.join(evidenceDir, "readiness-output.json"),
    readinessOutput
  );
  return parseFailureIsolationReadinessResult(
    readinessOutput,
    {
      scrapeId,
      publishedScrapeId
    }
  );
}

function parseArgs(argv) {
  const flags = new Set();
  const values = {};
  for (let index = 0; index < argv.length; index += 1) {
    const token = argv[index];
    if (!token.startsWith("--")) {
      continue;
    }
    const key = token.slice(2);
    const next = argv[index + 1];
    if (next && !next.startsWith("--")) {
      values[key] = next;
      index += 1;
    } else {
      flags.add(key);
    }
  }
  return { flags, values };
}

function run(command, args, { cwd, input } = {}) {
  const result = spawnSync(command, args, {
    cwd,
    input,
    encoding: "utf8",
    maxBuffer: 16 * 1024 * 1024
  });
  if (result.error) {
    throw result.error;
  }
  if (result.status !== 0) {
    throw new Error(
      `${command} ${args.join(" ")} failed (${result.status}): ${result.stderr || result.stdout}`
    );
  }
  return result.stdout;
}

function tryRun(command, args, { cwd, input } = {}) {
  const result = spawnSync(command, args, {
    cwd,
    input,
    encoding: "utf8",
    maxBuffer: 16 * 1024 * 1024
  });
  if (result.error || result.status !== 0) {
    return null;
  }
  return result.stdout;
}

function sleepMilliseconds(milliseconds) {
  Atomics.wait(
    new Int32Array(new SharedArrayBuffer(4)),
    0,
    0,
    milliseconds
  );
}

export function buildWorkerActivityPredicate({
  workerApplicationNames = WORKER_APPLICATION_NAMES,
  workerClientIp = ""
}) {
  if (
    !Array.isArray(workerApplicationNames)
    || workerApplicationNames.length === 0
    || workerApplicationNames.some(name => !String(name).trim())
  ) {
    throw new Error("At least one worker application name is required.");
  }
  const applicationNames = workerApplicationNames
    .map(name => quoteLiteral(String(name).trim()))
    .join(", ");
  const clientPredicate = workerClientIp
    ? ` OR client_addr = ${quoteLiteral(validateIp(workerClientIp))}::inet`
    : "";
  return `(application_name IN (${applicationNames})${clientPredicate})`;
}

function countActiveWorkerQueries({
  postgresContainer,
  workerApplicationNames,
  workerClientIp
}) {
  const predicate = buildWorkerActivityPredicate({
    workerApplicationNames,
    workerClientIp
  });
  const output = run(
    "docker",
    [
      "exec",
      "-i",
      postgresContainer,
      "psql",
      "-X",
      "-At",
      "-v",
      "ON_ERROR_STOP=1",
      "-U",
      "fst",
      "-d",
      "fstservice",
      "-c",
      `SELECT count(*) FROM pg_stat_activity
       WHERE datname = current_database()
         AND pid <> pg_backend_pid()
         AND state <> 'idle'
         AND ${predicate}`
    ]
  ).trim();
  const count = Number(output);
  if (!Number.isSafeInteger(count) || count < 0) {
    throw new Error(`Invalid active worker query count: ${output}`);
  }
  return count;
}

function waitForWorkerQueriesToDrain({
  postgresContainer,
  workerApplicationNames,
  workerClientIp,
  timeoutSeconds
}) {
  const deadline = Date.now() + (timeoutSeconds * 1000);
  let activeQueries = countActiveWorkerQueries({
    postgresContainer,
    workerApplicationNames,
    workerClientIp
  });
  while (activeQueries > 0 && Date.now() < deadline) {
    sleepMilliseconds(1000);
    activeQueries = countActiveWorkerQueries({
      postgresContainer,
      workerApplicationNames,
      workerClientIp
    });
  }
  return activeQueries;
}

function terminateWorkerQueries({
  postgresContainer,
  workerApplicationNames,
  workerClientIp
}) {
  const predicate = buildWorkerActivityPredicate({
    workerApplicationNames,
    workerClientIp
  });
  const output = run(
    "docker",
    [
      "exec",
      "-i",
      postgresContainer,
      "psql",
      "-X",
      "-At",
      "-v",
      "ON_ERROR_STOP=1",
      "-U",
      "fst",
      "-d",
      "fstservice",
      "-c",
      `SELECT count(*) FILTER (WHERE terminated)
       FROM (
         SELECT pg_terminate_backend(pid) AS terminated
         FROM pg_stat_activity
         WHERE datname = current_database()
           AND pid <> pg_backend_pid()
           AND ${predicate}
       ) targets`
    ]
  ).trim();
  const count = Number(output);
  if (!Number.isSafeInteger(count) || count < 0) {
    throw new Error(`Invalid terminated worker query count: ${output}`);
  }
  return count;
}

function drainStoppedWorkerQueries({
  postgresContainer,
  workerApplicationNames = WORKER_APPLICATION_NAMES,
  workerClientIp,
  queryDrainSeconds,
  evidenceDir
}) {
  const activeBeforeDrain = countActiveWorkerQueries({
    postgresContainer,
    workerApplicationNames,
    workerClientIp
  });
  const activeAfterGrace = waitForWorkerQueriesToDrain({
    postgresContainer,
    workerApplicationNames,
    workerClientIp,
    timeoutSeconds: queryDrainSeconds
  });
  let terminatedQueries = 0;
  let activeAfterTermination = activeAfterGrace;
  if (activeAfterGrace > 0) {
    terminatedQueries = terminateWorkerQueries({
      postgresContainer,
      workerApplicationNames,
      workerClientIp
    });
    activeAfterTermination = waitForWorkerQueriesToDrain({
      postgresContainer,
      workerApplicationNames,
      workerClientIp,
      timeoutSeconds: Math.max(5, Math.min(30, queryDrainSeconds || 15))
    });
  }
  const result = {
    activeBeforeDrain,
    activeAfterGrace,
    terminatedQueries,
    activeAfterTermination,
    queryDrainSeconds
  };
  writeFileSync(
    path.join(evidenceDir, "worker-query-drain.json"),
    `${JSON.stringify(result, null, 2)}\n`
  );
  if (activeAfterTermination !== 0) {
    throw new Error(
      `Worker query drain left ${activeAfterTermination} active backend(s).`
    );
  }
  return result;
}

function supportsNormalizedPhaseProgress({ postgresContainer }) {
  const output = run(
    "docker",
    [
      "exec",
      "-i",
      postgresContainer,
      "psql",
      "-X",
      "-At",
      "-v",
      "ON_ERROR_STOP=1",
      "-U",
      "fst",
      "-d",
      "fstservice",
      "-c",
      "SELECT to_regclass('public.scrape_phase_attempts') IS NOT NULL"
    ]
  ).trim();
  return output === "t";
}

function observe({
  composeDir,
  postgresContainer,
  workerContainer,
  normalizedProgressAvailable,
  sampleWorkerMemory
}) {
  const workerState = JSON.parse(run(
    "docker",
    ["inspect", "-f", "{{json .State}}", workerContainer]
  ).trim());
  const workerStatus = workerState.Status;
  const workerRestartPolicy = run(
    "docker",
    ["inspect", "-f", "{{.HostConfig.RestartPolicy.Name}}", workerContainer]
  ).trim();
  let workerClientIp = "";
  let workerMemoryPercent = null;
  let workerMemorySampleError = "";
  if (workerStatus === "running") {
    workerClientIp = run(
      "docker",
      [
        "inspect",
        "-f",
        "{{range .NetworkSettings.Networks}}{{.IPAddress}}{{end}}",
        workerContainer
      ]
    ).trim();
    if (workerClientIp) {
      validateIp(workerClientIp);
    }
    if (sampleWorkerMemory) {
      const memorySample = tryRun(
        "docker",
        ["stats", "--no-stream", "--format", "{{.MemPerc}}", workerContainer]
      );
      if (memorySample === null) {
        workerMemorySampleError = "docker_stats_failed";
      } else {
        try {
          workerMemoryPercent = parseDockerPercentage(memorySample.trim());
        } catch {
          workerMemorySampleError = "docker_stats_invalid_percentage";
        }
      }
    }
  }

  const clientPredicate = workerClientIp
    ? ` OR client_addr = ${quoteLiteral(workerClientIp)}::inet`
    : "";
  const normalizedPhaseCte = normalizedProgressAvailable
    ? `
, normalized_phase AS (
    SELECT phase_id, attempt, status, started_at, last_progress_at, heartbeat_at
    FROM scrape_phase_attempts
    WHERE scrape_id = (SELECT id FROM latest_scrape)
      AND status = 'running'
    ORDER BY last_progress_at DESC, phase_ordinal DESC, attempt DESC
    LIMIT 1
)`
    : `
, normalized_phase AS (
    SELECT NULL::text AS phase_id,
           NULL::integer AS attempt,
           NULL::text AS status,
           NULL::timestamptz AS started_at,
           NULL::timestamptz AS last_progress_at,
           NULL::timestamptz AS heartbeat_at
)`;
  const sql = `
WITH latest_scrape AS (
    SELECT id, status, started_at
    FROM scrape_log
    ORDER BY id DESC
    LIMIT 1
),
latest_phase AS (
    SELECT
        max(COALESCE(completed_at, started_at)) AS progress_at,
        (array_agg(phase ORDER BY COALESCE(completed_at, started_at) DESC))[1] AS phase
    FROM scrape_phase_outcomes
    WHERE scrape_id = (SELECT id FROM latest_scrape)
),
worker_activity AS (
    SELECT
        count(*) FILTER (WHERE state <> 'idle') AS active_queries,
        min(query_start) FILTER (WHERE state <> 'idle') AS oldest_query_started_at
    FROM pg_stat_activity
    WHERE datname = current_database()
      AND pid <> pg_backend_pid()
      AND (application_name = 'fstworker-scraper'${clientPredicate})
),
registered_refresh_progress AS (
    SELECT max(checked_at) AS progress_at
    FROM registered_user_refresh_scope_progress
    WHERE scrape_id = (SELECT id FROM latest_scrape)
)${normalizedPhaseCte}
SELECT json_build_object(
    'observedAt', clock_timestamp(),
    'workerRunning', ${workerStatus === "running" ? "TRUE" : "FALSE"},
    'workerContainerStatus', ${quoteLiteral(workerStatus)},
    'workerRestartPolicy', ${quoteLiteral(workerRestartPolicy)},
    'workerOomKilled', ${workerState.OOMKilled ? "TRUE" : "FALSE"},
    'workerExitCode', ${Number(workerState.ExitCode ?? 0)},
    'workerFinishedAt', ${quoteLiteral(workerState.FinishedAt ?? "")},
    'workerClientIp', ${quoteLiteral(workerClientIp)},
    'workerMemoryPercent', ${workerMemoryPercent ?? "NULL"},
    'workerMemorySampleError', ${quoteLiteral(workerMemorySampleError)},
    'scrapeId', scrape.id,
    'scrapeStatus', scrape.status,
    'scrapeStartedAt', scrape.started_at,
    'publishedScrapeId', publication.published_scrape_id,
    'publicReadsFrozen', publication.public_reads_frozen,
    'publicReadsFrozenReason', publication.public_reads_frozen_reason,
    'workerLedgerStatus', worker.status,
    'lastHeartbeatAt', worker.last_heartbeat_at,
    'operation', worker.current_operation_json,
    'normalizedPhaseAttempt', CASE
        WHEN normalized.phase_id IS NULL THEN NULL
        ELSE json_build_object(
            'phaseId', normalized.phase_id,
            'attempt', normalized.attempt,
            'status', normalized.status,
            'startedAt', normalized.started_at,
            'lastProgressAt', normalized.last_progress_at,
            'heartbeatAt', normalized.heartbeat_at)
    END,
    'latestPhaseProgressAt', phase.progress_at,
    'latestPhase', phase.phase,
    'registeredRefreshProgressAt', refresh.progress_at,
    'activeWorkerQueries', activity.active_queries,
    'oldestWorkerQueryStartedAt', activity.oldest_query_started_at,
    'candidatePublishedScopeRows', (
        SELECT count(*)
        FROM leaderboard_published_scope_source
        WHERE published_scrape_id = scrape.id
    )
)
FROM latest_scrape scrape
CROSS JOIN scrape_publication_state publication
LEFT JOIN service_worker_status worker ON worker.worker_key = 'scraper'
CROSS JOIN latest_phase phase
CROSS JOIN worker_activity activity
CROSS JOIN registered_refresh_progress refresh
LEFT JOIN normalized_phase normalized ON TRUE
WHERE publication.id = TRUE;
`;
  const output = run(
    "docker",
    [
      "exec",
      "-i",
      postgresContainer,
      "psql",
      "-X",
      "-At",
      "-v",
      "ON_ERROR_STOP=1",
      "-U",
      "fst",
      "-d",
      "fstservice"
    ],
    { cwd: composeDir, input: sql }
  ).trim();
  if (!output) {
    throw new Error("Watchdog observation query returned no row.");
  }
  return JSON.parse(output);
}

function captureRollback({
  postgresContainer,
  evidenceDir,
  scrapeId,
  normalizedProgressAvailable
}) {
  const phaseAttemptRollback = normalizedProgressAvailable
    ? `
SELECT format(
    'UPDATE scrape_phase_attempts SET status=%L, heartbeat_at=%L::timestamptz, completed_at=%L::timestamptz, warning_message=%L WHERE scrape_id=%L AND phase_id=%L AND attempt=%L;',
    status, heartbeat_at, completed_at, warning_message,
    scrape_id, phase_id, attempt)
FROM scrape_phase_attempts
WHERE scrape_id=${scrapeId};
`
    : "SELECT '-- scrape_phase_attempts did not exist before recovery.';";
  const sql = `
SELECT 'BEGIN;';
SELECT 'SET LOCAL lock_timeout = ''5s'';';
SELECT 'SET LOCAL statement_timeout = ''30s'';';
SELECT format(
    'UPDATE scrape_log SET status=%L, failed_at=%L::timestamptz, failure_phase=%L, failure_message=%L WHERE id=${scrapeId};',
    status, failed_at, failure_phase, failure_message)
FROM scrape_log WHERE id=${scrapeId};
SELECT format(
    'UPDATE scrape_publication_state SET published_scrape_id=%L, published_at=%L::timestamptz, updated_at=%L::timestamptz, public_reads_frozen=%L, public_reads_frozen_at=%L::timestamptz, public_reads_frozen_scrape_id=%L, public_reads_frozen_reason=%L, band_projection_generation=%L, current_publication_id=%L, previous_publication_id=%L, working_publication_id=%L WHERE id=TRUE;',
    published_scrape_id, published_at, updated_at, public_reads_frozen,
    public_reads_frozen_at, public_reads_frozen_scrape_id,
    public_reads_frozen_reason, band_projection_generation,
    current_publication_id, previous_publication_id, working_publication_id)
FROM scrape_publication_state WHERE id=TRUE;
SELECT format(
    'UPDATE publication_generations SET status=%L, failed_at=%L::timestamptz, failure_phase=%L, failure_message=%L WHERE scrape_id=${scrapeId};',
    status, failed_at, failure_phase, failure_message)
FROM publication_generations WHERE scrape_id=${scrapeId};
SELECT '-- publication_api_response_cache_staging is derived; rebuild candidate precompute after watchdog rollback.';
SELECT format(
    'UPDATE service_worker_status SET status=%L, last_status_change_at=%L::timestamptz, message=%L, current_operation_json=%L::jsonb, last_operation_json=%L::jsonb, updated_at=%L::timestamptz WHERE worker_key=''scraper'';',
    status, last_status_change_at, message, current_operation_json,
    last_operation_json, updated_at)
FROM service_worker_status WHERE worker_key='scraper';
${phaseAttemptRollback}
SELECT 'COMMIT;';
`;
  const rollback = run(
    "docker",
    [
      "exec",
      "-i",
      postgresContainer,
      "psql",
      "-X",
      "-At",
      "-v",
      "ON_ERROR_STOP=1",
      "-U",
      "fst",
      "-d",
      "fstservice"
    ],
    { input: sql }
  );
  writeFileSync(path.join(evidenceDir, "rollback-to-pre-watchdog-state.sql"), rollback);
}

function stopAndRecover({
  observation,
  decision,
  composeDir,
  postgresContainer,
  serviceContainer,
  workerContainer,
  evidenceDir,
  stopTimeoutSeconds,
  normalizedProgressAvailable,
  queryDrainSeconds
}) {
  const scrapeId = requirePositiveInteger(observation.scrapeId, "scrapeId");
  const publishedScrapeId = requirePositiveInteger(
    observation.publishedScrapeId,
    "publishedScrapeId"
  );
  if (Number(observation.candidatePublishedScopeRows ?? 0) !== 0) {
    throw new Error(
      `Refusing recovery: scrape ${scrapeId} owns published-source rows.`
    );
  }

  verifyFailureIsolationReadiness({
    composeDir,
    serviceContainer,
    evidenceDir,
    scrapeId,
    publishedScrapeId
  });

  run(
    "docker",
    ["compose", "stop", "-t", String(stopTimeoutSeconds), "fstworker"],
    { cwd: composeDir }
  );

  const stoppedStatus = run(
    "docker",
    ["inspect", "-f", "{{.State.Status}}", workerContainer]
  ).trim();
  if (stoppedStatus === "running") {
    throw new Error("fstworker is still running after the watchdog stop.");
  }

  const queryDrain = drainStoppedWorkerQueries({
    postgresContainer,
    workerClientIp: observation.workerClientIp ?? "",
    queryDrainSeconds,
    evidenceDir
  });

  captureRollback({
    postgresContainer,
    evidenceDir,
    scrapeId,
    normalizedProgressAvailable
  });

  const failureMessage =
    `Worker safety watchdog abandoned scrape ${scrapeId}: ${decision.reason}; `
    + `idle=${Math.round(decision.idleForSeconds ?? 0)}s, `
    + `phaseElapsed=${Math.round(decision.phaseElapsedSeconds ?? 0)}s`
    + (
      Number.isFinite(decision.workerMemoryPercent)
        ? `, workerMemory=${decision.workerMemoryPercent.toFixed(2)}%`
        : ""
    )
    + ". "
    + `The worker was stopped before recovery; no active worker query remained, `
    + `candidate published-source rows were zero, and published scrape `
    + `${publishedScrapeId} was preserved and unfrozen.`;
  const failurePhase = selectFailureIsolationPhase(decision);
  const recoveryCommand = buildFailureIsolationCommand({
    serviceContainer,
    scrapeId,
    publishedScrapeId,
    failurePhase,
    failureMessage
  });
  writeFileSync(
    path.join(evidenceDir, "recovery-command.json"),
    `${JSON.stringify(recoveryCommand, null, 2)}\n`
  );
  const recoveryOutput = run(
    "docker",
    recoveryCommand,
    { cwd: composeDir }
  );
  writeFileSync(path.join(evidenceDir, "recovery-output.json"), recoveryOutput);
  return { failureMessage, failurePhase, stoppedStatus, queryDrain };
}

function renderReport({
  evidenceDir,
  observation,
  decision,
  recovery,
  recoveryError
}) {
  const reportPath = path.join(evidenceDir, "watchdog-report.md");
  const lines = [
    "## Phase 0 - Worker Safety Watchdog Recovery",
    "",
    `- Scrape \`${observation.scrapeId}\` exceeded a configured worker safety gate.`,
    `- Decision: \`${decision.decision}\` (\`${decision.reason}\`).`,
    `- Published scrape \`${observation.publishedScrapeId}\` remained authoritative.`,
    "",
    "### Outcome",
    "",
    `- Idle without a phase heartbeat: \`${Math.round(decision.idleForSeconds ?? 0)} seconds\`.`,
    `- Worker memory at decision: \`${Number.isFinite(decision.workerMemoryPercent) ? `${decision.workerMemoryPercent.toFixed(2)}%` : "unavailable"}\`.`,
    `- Worker exit state: \`code=${observation.workerExitCode ?? "unknown"}, oom=${observation.workerOomKilled ?? false}\`.`,
    `- Active worker database queries at decision: \`${observation.activeWorkerQueries ?? 0}\`.`,
    `- Candidate published-source rows: \`${observation.candidatePublishedScopeRows ?? 0}\`.`,
    `- Recovery: ${
      recovery
        ? "worker stopped; scrape failed; prior publication unfrozen"
        : recoveryError
          ? "failed after the watchdog action; publication remains fail-closed"
          : "dry-run only"
    }.`,
    ...(recoveryError
      ? [`- Recovery error: \`${recoveryError}\`.`]
      : []),
    "",
    "### Files/Artifacts",
    "",
    `- \`${evidenceDir}/observation.json\``,
    `- \`${evidenceDir}/decision.json\``,
    `- \`${evidenceDir}/readiness-command.json\` when recovery ran`,
    `- \`${evidenceDir}/readiness-output.json\` when recovery ran`,
    `- \`${evidenceDir}/recovery-command.json\` when recovery ran`,
    `- \`${evidenceDir}/recovery-output.json\` when recovery ran`,
    `- \`${evidenceDir}/rollback-to-pre-watchdog-state.sql\` when recovery ran`,
    `- \`${evidenceDir}/worker-query-drain.json\` when worker shutdown ran`,
    `- \`${evidenceDir}/recovery-error.txt\` when recovery failed`,
    "",
    "### Validation",
    "",
    "- The recovery transaction guards the published pointer, zero candidate mappings, worker DB activity, locks, and affected row counts.",
    "- An active worker query defers ordinary progress timeouts; only an explicit emergency memory threshold may take precedence.",
    ""
  ];
  writeFileSync(reportPath, `${lines.join("\n")}\n`);
  return reportPath;
}

function sendReport({ reportPath, evidenceDir, send, fallbackEnvFile }) {
  const args = [
    path.join(repositoryRoot, "tools", "agent-report-email.mjs"),
    "--subject",
    "FST Autonomous Agent: Phase 0 - Worker Safety Recovery · Needs Attention",
    "--input-md",
    reportPath,
    "--outbox-dir",
    path.join(evidenceDir, "outbox")
  ];
  if (send) {
    args.push("--send");
  }
  if (fallbackEnvFile) {
    args.push("--fallback-env-file", fallbackEnvFile);
  }

  const result = spawnSync(process.execPath, args, {
    cwd: repositoryRoot,
    encoding: "utf8",
    maxBuffer: 4 * 1024 * 1024
  });
  writeFileSync(
    path.join(evidenceDir, "email-result.txt"),
    `${result.stdout ?? ""}${result.stderr ?? ""}`
  );
  if (result.status !== 0 && send) {
    return sendReport({
      reportPath,
      evidenceDir,
      send: false,
      fallbackEnvFile
    });
  }
  return result.status === 0;
}

function parseTimestamp(value, name) {
  const parsed = Date.parse(value);
  if (!Number.isFinite(parsed)) {
    throw new Error(`Invalid ${name}: ${value}`);
  }
  return parsed;
}

function sanitizeError(error) {
  return String(error instanceof Error ? error.message : error)
    .replaceAll(/\s+/g, " ")
    .slice(0, 500);
}

function requirePositiveInteger(value, name) {
  const number = Number(value);
  if (!Number.isSafeInteger(number) || number <= 0) {
    throw new Error(`${name} must be a positive integer.`);
  }
  return number;
}

function quoteLiteral(value) {
  return `'${String(value).replaceAll("'", "''")}'`;
}

function validateIp(value) {
  if (!/^\d{1,3}(?:\.\d{1,3}){3}$/.test(value)) {
    throw new Error(`Invalid worker client IP: ${value}`);
  }
  if (value.split(".").some(part => Number(part) > 255)) {
    throw new Error(`Invalid worker client IP: ${value}`);
  }
  return value;
}

function ensureEvidencePath(value) {
  const resolved = path.resolve(value);
  if (!resolved.startsWith("/mnt/docker-storage/")) {
    throw new Error("Watchdog evidence must remain on /mnt/docker-storage.");
  }
  mkdirSync(resolved, { recursive: true });
  return resolved;
}

async function main() {
  const args = parseArgs(process.argv.slice(2));
  if (args.flags.has("help")) {
    console.log(
      "Usage: node tools/fst-worker-no-progress-watchdog.mjs "
      + "--evidence-dir <FST-drive-path> [--monitor] [--dry-run] "
      + "[--idle-seconds 2700] [--max-phase-seconds 0] [--poll-seconds 60] "
      + "[--recover-worker-exit] [--worker-exit-grace-seconds 120] "
      + "[--max-worker-memory-percent 0] "
      + "[--worker-query-drain-seconds 60] "
      + "[--send-report] [--fallback-env-file <path>]"
    );
    return;
  }

  const composeDir =
    args.values["compose-dir"] ?? "/home/sfenton/Docker/FestivalServiceTracker";
  const postgresContainer = args.values["postgres-container"] ?? "fst-postgres";
  const serviceContainer = args.values["service-container"] ?? "fstservice";
  const workerContainer = args.values["worker-container"] ?? "fstworker";
  const evidenceDir = ensureEvidencePath(
    args.values["evidence-dir"]
    ?? (() => {
      throw new Error("Missing --evidence-dir.");
    })()
  );
  const idleSeconds = Number(args.values["idle-seconds"] ?? 2700);
  const maxPhaseSeconds = Number(args.values["max-phase-seconds"] ?? 0);
  const recoverWorkerExit = args.flags.has("recover-worker-exit");
  const workerExitGraceSeconds = Number(
    args.values["worker-exit-grace-seconds"] ?? 120
  );
  if (!Number.isFinite(workerExitGraceSeconds) || workerExitGraceSeconds < 0) {
    throw new Error("--worker-exit-grace-seconds must be zero or greater.");
  }
  const maxWorkerMemoryPercent = Number(
    args.values["max-worker-memory-percent"] ?? 0
  );
  if (
    !Number.isFinite(maxWorkerMemoryPercent)
    || maxWorkerMemoryPercent < 0
    || maxWorkerMemoryPercent > 100
  ) {
    throw new Error("--max-worker-memory-percent must be between 0 and 100.");
  }
  const workerQueryDrainSeconds = Number(
    args.values["worker-query-drain-seconds"] ?? 60
  );
  if (
    !Number.isFinite(workerQueryDrainSeconds)
    || workerQueryDrainSeconds < 0
    || workerQueryDrainSeconds > 300
  ) {
    throw new Error("--worker-query-drain-seconds must be between 0 and 300.");
  }
  const pollSeconds = Number(args.values["poll-seconds"] ?? 60);
  const stopTimeoutSeconds = Number(args.values["stop-timeout-seconds"] ?? 30);
  const normalizedProgressAvailable = supportsNormalizedPhaseProgress({
    postgresContainer
  });

  while (true) {
    const observation = observe({
      composeDir,
      postgresContainer,
      workerContainer,
      normalizedProgressAvailable,
      sampleWorkerMemory: maxWorkerMemoryPercent > 0
    });
    const resourceRecoveryEnabled =
      recoverWorkerExit || maxWorkerMemoryPercent > 0;
    if (
      resourceRecoveryEnabled
      && !args.flags.has("dry-run")
      && observation.workerRestartPolicy !== "no"
    ) {
      throw new Error(
        "Worker exit and memory recovery require restart policy 'no'."
      );
    }
    const decision = evaluateNoProgressObservation(observation, {
      idleSeconds,
      maxPhaseSeconds,
      recoverWorkerExit,
      maxWorkerMemoryPercent,
      workerExitGraceSeconds
    });
    writeFileSync(
      path.join(evidenceDir, "observation.json"),
      `${JSON.stringify(observation, null, 2)}\n`
    );
    writeFileSync(
      path.join(evidenceDir, "decision.json"),
      `${JSON.stringify(decision, null, 2)}\n`
    );
    console.log(JSON.stringify({ observation, decision }));

    if (decision.decision === "timeout") {
      let recovery = null;
      let recoveryError = null;
      if (!args.flags.has("dry-run")) {
        try {
          recovery = stopAndRecover({
            observation,
            decision,
            composeDir,
            postgresContainer,
            serviceContainer,
            workerContainer,
            evidenceDir,
            stopTimeoutSeconds,
            normalizedProgressAvailable,
            queryDrainSeconds: workerQueryDrainSeconds
          });
        } catch (error) {
          recoveryError = sanitizeError(error);
          writeFileSync(
            path.join(evidenceDir, "recovery-error.txt"),
            `${recoveryError}\n`
          );
        }
      }
      const reportPath = renderReport({
        evidenceDir,
        observation,
        decision,
        recovery,
        recoveryError
      });
      sendReport({
        reportPath,
        evidenceDir,
        send: args.flags.has("send-report"),
        fallbackEnvFile: args.values["fallback-env-file"]
      });
      if (recoveryError) {
        console.error(recoveryError);
        process.exitCode = 1;
        return;
      }
      process.exitCode = recovery ? WATCHDOG_RECOVERY_EXIT_CODE : 2;
      return;
    }

    if (!args.flags.has("monitor")
        || ["inactive", "terminal"].includes(decision.decision)) {
      return;
    }
    await new Promise(resolve => setTimeout(resolve, pollSeconds * 1000));
  }
}

if (process.argv[1] && path.resolve(process.argv[1]) === scriptPath) {
  main().catch(error => {
    console.error(error instanceof Error ? error.message : String(error));
    process.exitCode = 1;
  });
}
