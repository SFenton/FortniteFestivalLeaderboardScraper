#!/usr/bin/env node

import { spawnSync } from "node:child_process";
import { mkdirSync, writeFileSync } from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";

export const NO_PROGRESS_FAILURE_PHASE = "post_process_no_progress_abandoned";
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
  if (observation.scrapeStatus !== "running") {
    return { decision: "terminal", reason: `scrape_${observation.scrapeStatus ?? "missing"}` };
  }
  if (observation.publicReadsFrozenReason !== "post-process") {
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

export function buildRecoverySql({
  scrapeId,
  publishedScrapeId,
  failureMessage,
  workerMessage,
  workerApplicationNames = WORKER_APPLICATION_NAMES,
  workerClientIp = ""
}) {
  const normalizedScrapeId = requirePositiveInteger(scrapeId, "scrapeId");
  const normalizedPublishedId = requirePositiveInteger(publishedScrapeId, "publishedScrapeId");
  const workerActivityPredicate = buildWorkerActivityPredicate({
    workerApplicationNames,
    workerClientIp
  });

  return `BEGIN;
SET LOCAL lock_timeout = '5s';
SET LOCAL statement_timeout = '30s';

DO $watchdog$
DECLARE
    recovery_at timestamptz := clock_timestamp();
    changed_rows integer;
    published_id bigint;
    candidate_status text;
    candidate_mappings bigint;
    active_worker_queries bigint;
    operation_started timestamptz;
    ended_text text;
BEGIN
    PERFORM 1 FROM scrape_publication_state WHERE id = TRUE FOR UPDATE;
    IF NOT FOUND THEN
        RAISE EXCEPTION 'scrape_publication_state singleton is missing';
    END IF;

    PERFORM 1 FROM scrape_log WHERE id = ${normalizedScrapeId} FOR UPDATE;
    IF NOT FOUND THEN
        RAISE EXCEPTION 'scrape ${normalizedScrapeId} does not exist';
    END IF;

    SELECT published_scrape_id
    INTO published_id
    FROM scrape_publication_state
    WHERE id = TRUE;
    SELECT status
    INTO candidate_status
    FROM scrape_log
    WHERE id = ${normalizedScrapeId};
    SELECT count(*)
    INTO candidate_mappings
    FROM leaderboard_published_scope_source
    WHERE published_scrape_id = ${normalizedScrapeId};
    SELECT count(*)
    INTO active_worker_queries
    FROM pg_stat_activity
    WHERE datname = current_database()
      AND pid <> pg_backend_pid()
      AND state <> 'idle'
      AND ${workerActivityPredicate};

    IF published_id <> ${normalizedPublishedId} THEN
        RAISE EXCEPTION 'expected published scrape ${normalizedPublishedId}, found %', published_id;
    END IF;
    IF candidate_status NOT IN ('running', 'failed') THEN
        RAISE EXCEPTION 'expected scrape ${normalizedScrapeId} running or failed after worker stop, found %', candidate_status;
    END IF;
    IF candidate_mappings <> 0 THEN
        RAISE EXCEPTION 'scrape ${normalizedScrapeId} owns % published-source rows', candidate_mappings;
    END IF;
    IF active_worker_queries <> 0 THEN
        RAISE EXCEPTION 'worker still owns % active database queries', active_worker_queries;
    END IF;
    IF EXISTS (SELECT 1 FROM pg_locks WHERE NOT granted) THEN
        RAISE EXCEPTION 'ungranted database locks remain';
    END IF;
    IF EXISTS (SELECT 1 FROM pg_locks WHERE locktype = 'advisory') THEN
        RAISE EXCEPTION 'advisory database locks remain';
    END IF;
    IF EXISTS (SELECT 1 FROM pg_stat_progress_vacuum)
       OR EXISTS (SELECT 1 FROM pg_stat_progress_create_index)
       OR EXISTS (SELECT 1 FROM pg_stat_progress_cluster)
       OR EXISTS (SELECT 1 FROM pg_stat_progress_analyze) THEN
        RAISE EXCEPTION 'database maintenance progress remains active';
    END IF;

    UPDATE scrape_log
    SET status = 'failed',
        failed_at = COALESCE(failed_at, recovery_at),
        failure_phase = CASE
            WHEN status = 'running' THEN '${NO_PROGRESS_FAILURE_PHASE}'
            ELSE failure_phase
        END,
        failure_message = CASE
            WHEN status = 'running' THEN ${quoteLiteral(failureMessage)}
            ELSE failure_message
        END
    WHERE id = ${normalizedScrapeId}
      AND status IN ('running', 'failed')
      AND NOT EXISTS (
          SELECT 1
          FROM scrape_publication_state
          WHERE id = TRUE
            AND published_scrape_id = ${normalizedScrapeId}
      );
    GET DIAGNOSTICS changed_rows = ROW_COUNT;
    IF changed_rows <> 1 THEN
        RAISE EXCEPTION 'expected to fail one scrape row, changed %', changed_rows;
    END IF;

    IF to_regclass('public.scrape_phase_attempts') IS NOT NULL THEN
        UPDATE scrape_phase_attempts
        SET status = 'interrupted',
            heartbeat_at = GREATEST(heartbeat_at, recovery_at),
            completed_at = recovery_at,
            warning_message = COALESCE(
                warning_message,
                ${quoteLiteral(failureMessage)})
        WHERE scrape_id = ${normalizedScrapeId}
          AND status = 'running';
    END IF;

    IF to_regclass('public.publication_generations') IS NOT NULL THEN
        UPDATE publication_generations
        SET status = 'failed',
            failed_at = COALESCE(failed_at, recovery_at),
            failure_phase = CASE
                WHEN status = 'failed' THEN failure_phase
                ELSE '${NO_PROGRESS_FAILURE_PHASE}'
            END,
            failure_message = CASE
                WHEN status = 'failed' THEN failure_message
                ELSE ${quoteLiteral(failureMessage)}
            END
        WHERE scrape_id = ${normalizedScrapeId}
          AND status NOT IN ('current', 'retained', 'retired');

        UPDATE scrape_publication_state publication
        SET working_publication_id = NULL,
            updated_at = recovery_at
        FROM publication_generations generation
        WHERE publication.id = TRUE
          AND generation.scrape_id = ${normalizedScrapeId}
          AND publication.working_publication_id = generation.publication_id;

        IF to_regclass('public.publication_api_response_cache_staging') IS NOT NULL THEN
            DELETE FROM publication_api_response_cache_staging staging
            USING publication_generations generation
            WHERE generation.scrape_id = ${normalizedScrapeId}
              AND staging.publication_id = generation.publication_id;
        END IF;
    END IF;

    UPDATE scrape_publication_state
    SET public_reads_frozen = FALSE,
        public_reads_frozen_at = NULL,
        public_reads_frozen_scrape_id = NULL,
        public_reads_frozen_reason = NULL,
        updated_at = recovery_at
    WHERE id = TRUE
      AND published_scrape_id = ${normalizedPublishedId};
    GET DIAGNOSTICS changed_rows = ROW_COUNT;
    IF changed_rows <> 1 THEN
        RAISE EXCEPTION 'expected to unfreeze one publication row, changed %', changed_rows;
    END IF;

    SELECT NULLIF(current_operation_json->>'StartedAtUtc', '')::timestamptz
    INTO operation_started
    FROM service_worker_status
    WHERE worker_key = 'scraper';
    ended_text := to_char(
        recovery_at AT TIME ZONE 'UTC',
        'YYYY-MM-DD"T"HH24:MI:SS.US"Z"');

    UPDATE service_worker_status
    SET status = 'offline',
        last_status_change_at = recovery_at,
        message = ${quoteLiteral(workerMessage)},
        last_operation_json = CASE
            WHEN current_operation_json IS NULL THEN last_operation_json
            ELSE current_operation_json || jsonb_build_object(
                'Status', 'failed',
                'Detail', ${quoteLiteral(failureMessage)},
                'UpdatedAtUtc', ended_text,
                'EndedAtUtc', ended_text,
                'ElapsedSeconds', CASE
                    WHEN operation_started IS NULL THEN current_operation_json->'ElapsedSeconds'
                    ELSE to_jsonb(EXTRACT(EPOCH FROM (recovery_at - operation_started)))
                END)
        END,
        current_operation_json = NULL,
        updated_at = recovery_at
    WHERE worker_key = 'scraper';
    GET DIAGNOSTICS changed_rows = ROW_COUNT;
    IF changed_rows <> 1 THEN
        RAISE EXCEPTION 'expected to reconcile one worker row, changed %', changed_rows;
    END IF;
END
$watchdog$;

COMMIT;

SELECT id, status, failed_at, failure_phase, failure_message
FROM scrape_log
WHERE id = ${normalizedScrapeId};
SELECT published_scrape_id, public_reads_frozen,
       public_reads_frozen_at, public_reads_frozen_scrape_id,
       public_reads_frozen_reason, updated_at
FROM scrape_publication_state
WHERE id = TRUE;
SELECT worker_key, status, last_heartbeat_at, last_status_change_at,
       message, current_operation_json, last_operation_json, updated_at
FROM service_worker_status
WHERE worker_key = 'scraper';
SELECT count(*) AS candidate_published_scope_rows
FROM leaderboard_published_scope_source
WHERE published_scrape_id = ${normalizedScrapeId};
`;
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
CROSS JOIN normalized_phase normalized
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
  const workerMessage =
    `Worker stopped by worker safety watchdog; scrape ${scrapeId} failed and published `
    + `scrape ${publishedScrapeId} was preserved.`;
  const recoverySql = buildRecoverySql({
    scrapeId,
    publishedScrapeId,
    failureMessage,
    workerMessage,
    workerClientIp: observation.workerClientIp ?? ""
  });
  writeFileSync(path.join(evidenceDir, "recovery.sql"), recoverySql);
  const recoveryOutput = run(
    "docker",
    [
      "exec",
      "-i",
      postgresContainer,
      "psql",
      "-X",
      "-v",
      "ON_ERROR_STOP=1",
      "-U",
      "fst",
      "-d",
      "fstservice",
      "-P",
      "pager=off"
    ],
    { input: recoverySql }
  );
  writeFileSync(path.join(evidenceDir, "recovery-output.txt"), recoveryOutput);
  return { failureMessage, workerMessage, stoppedStatus, queryDrain };
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
    `- \`${evidenceDir}/recovery.sql\` when recovery ran`,
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
