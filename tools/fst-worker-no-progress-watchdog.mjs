#!/usr/bin/env node

import { spawnSync } from "node:child_process";
import { mkdirSync, writeFileSync } from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";

export const NO_PROGRESS_FAILURE_PHASE = "post_process_no_progress_abandoned";
export const WATCHDOG_RECOVERY_EXIT_CODE = 42;

const scriptPath = fileURLToPath(import.meta.url);
const repositoryRoot = path.resolve(path.dirname(scriptPath), "..");

export function evaluateNoProgressObservation(
  observation,
  {
    idleSeconds = 2700,
    maxPhaseSeconds = 0
  } = {}
) {
  if (!observation.workerRunning) {
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
  const progressCandidates = [
    operation.UpdatedAtUtc,
    operation.updatedAtUtc,
    operation.StartedAtUtc,
    operation.startedAtUtc,
    observation.latestPhaseProgressAt,
    observation.scrapeStartedAt
  ]
    .filter(Boolean)
    .map(value => parseTimestamp(value, "progress timestamp"));
  const latestProgressMs = Math.max(...progressCandidates);
  const idleForSeconds = Math.max(0, (nowMs - latestProgressMs) / 1000);

  const phaseStartedValue =
    operation.StartedAtUtc
    ?? operation.startedAtUtc
    ?? observation.scrapeStartedAt;
  const phaseElapsedSeconds = phaseStartedValue
    ? Math.max(0, (nowMs - parseTimestamp(phaseStartedValue, "phase start")) / 1000)
    : 0;
  const activeWorkerQueries = Number(observation.activeWorkerQueries ?? 0);

  if (activeWorkerQueries > 0) {
    return {
      decision: "defer_active_query",
      reason: "worker_database_activity_present",
      idleForSeconds,
      phaseElapsedSeconds,
      activeWorkerQueries
    };
  }

  if (maxPhaseSeconds > 0 && phaseElapsedSeconds >= maxPhaseSeconds) {
    return {
      decision: "timeout",
      reason: "max_phase_duration_exceeded",
      idleForSeconds,
      phaseElapsedSeconds,
      activeWorkerQueries
    };
  }

  if (idleForSeconds >= idleSeconds) {
    return {
      decision: "timeout",
      reason: "no_phase_progress",
      idleForSeconds,
      phaseElapsedSeconds,
      activeWorkerQueries
    };
  }

  return {
    decision: "healthy",
    reason: "phase_progress_within_threshold",
    idleForSeconds,
    phaseElapsedSeconds,
    activeWorkerQueries
  };
}

export function buildRecoverySql({
  scrapeId,
  publishedScrapeId,
  failureMessage,
  workerMessage,
  workerApplicationName = "fstworker-scraper",
  workerClientIp = ""
}) {
  const normalizedScrapeId = requirePositiveInteger(scrapeId, "scrapeId");
  const normalizedPublishedId = requirePositiveInteger(publishedScrapeId, "publishedScrapeId");
  const clientPredicate = workerClientIp
    ? ` OR client_addr = ${quoteLiteral(validateIp(workerClientIp))}::inet`
    : "";

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
      AND (application_name = ${quoteLiteral(workerApplicationName)}${clientPredicate});

    IF published_id <> ${normalizedPublishedId} THEN
        RAISE EXCEPTION 'expected published scrape ${normalizedPublishedId}, found %', published_id;
    END IF;
    IF candidate_status <> 'running' THEN
        RAISE EXCEPTION 'expected scrape ${normalizedScrapeId} running, found %', candidate_status;
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
        failure_phase = '${NO_PROGRESS_FAILURE_PHASE}',
        failure_message = ${quoteLiteral(failureMessage)}
    WHERE id = ${normalizedScrapeId}
      AND status = 'running'
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

function observe({ composeDir, postgresContainer, workerContainer }) {
  const workerStatus = run(
    "docker",
    ["inspect", "-f", "{{.State.Status}}", workerContainer]
  ).trim();
  let workerClientIp = "";
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
  }

  const clientPredicate = workerClientIp
    ? ` OR client_addr = ${quoteLiteral(workerClientIp)}::inet`
    : "";
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
)
SELECT json_build_object(
    'observedAt', clock_timestamp(),
    'workerRunning', ${workerStatus === "running" ? "TRUE" : "FALSE"},
    'workerContainerStatus', ${quoteLiteral(workerStatus)},
    'workerClientIp', ${quoteLiteral(workerClientIp)},
    'scrapeId', scrape.id,
    'scrapeStatus', scrape.status,
    'scrapeStartedAt', scrape.started_at,
    'publishedScrapeId', publication.published_scrape_id,
    'publicReadsFrozen', publication.public_reads_frozen,
    'publicReadsFrozenReason', publication.public_reads_frozen_reason,
    'workerLedgerStatus', worker.status,
    'lastHeartbeatAt', worker.last_heartbeat_at,
    'operation', worker.current_operation_json,
    'latestPhaseProgressAt', phase.progress_at,
    'latestPhase', phase.phase,
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

function captureRollback({ postgresContainer, evidenceDir, scrapeId }) {
  const sql = `
SELECT 'BEGIN;';
SELECT 'SET LOCAL lock_timeout = ''5s'';';
SELECT 'SET LOCAL statement_timeout = ''30s'';';
SELECT format(
    'UPDATE scrape_log SET status=%L, failed_at=%L::timestamptz, failure_phase=%L, failure_message=%L WHERE id=${scrapeId};',
    status, failed_at, failure_phase, failure_message)
FROM scrape_log WHERE id=${scrapeId};
SELECT format(
    'UPDATE scrape_publication_state SET published_scrape_id=%L, published_at=%L::timestamptz, updated_at=%L::timestamptz, public_reads_frozen=%L, public_reads_frozen_at=%L::timestamptz, public_reads_frozen_scrape_id=%L, public_reads_frozen_reason=%L, band_projection_generation=%L WHERE id=TRUE;',
    published_scrape_id, published_at, updated_at, public_reads_frozen,
    public_reads_frozen_at, public_reads_frozen_scrape_id,
    public_reads_frozen_reason, band_projection_generation)
FROM scrape_publication_state WHERE id=TRUE;
SELECT format(
    'UPDATE service_worker_status SET status=%L, last_status_change_at=%L::timestamptz, message=%L, current_operation_json=%L::jsonb, last_operation_json=%L::jsonb, updated_at=%L::timestamptz WHERE worker_key=''scraper'';',
    status, last_status_change_at, message, current_operation_json,
    last_operation_json, updated_at)
FROM service_worker_status WHERE worker_key='scraper';
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
  stopTimeoutSeconds
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

  captureRollback({ postgresContainer, evidenceDir, scrapeId });

  const failureMessage =
    `No-progress watchdog abandoned scrape ${scrapeId}: ${decision.reason}; `
    + `idle=${Math.round(decision.idleForSeconds)}s, `
    + `phaseElapsed=${Math.round(decision.phaseElapsedSeconds)}s. `
    + `The worker was stopped before recovery; no active worker query remained, `
    + `candidate published-source rows were zero, and published scrape `
    + `${publishedScrapeId} was preserved and unfrozen.`;
  const workerMessage =
    `Worker stopped by no-progress watchdog; scrape ${scrapeId} failed and published `
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
  return { failureMessage, workerMessage, stoppedStatus };
}

function renderReport({ evidenceDir, observation, decision, recovery }) {
  const reportPath = path.join(evidenceDir, "watchdog-report.md");
  const lines = [
    "## Phase 0 - Worker No-Progress Recovery",
    "",
    `- Scrape \`${observation.scrapeId}\` exceeded the configured progress gate.`,
    `- Decision: \`${decision.decision}\` (\`${decision.reason}\`).`,
    `- Published scrape \`${observation.publishedScrapeId}\` remained authoritative.`,
    "",
    "### Outcome",
    "",
    `- Idle without a phase heartbeat: \`${Math.round(decision.idleForSeconds ?? 0)} seconds\`.`,
    `- Active worker database queries at decision: \`${observation.activeWorkerQueries ?? 0}\`.`,
    `- Candidate published-source rows: \`${observation.candidatePublishedScopeRows ?? 0}\`.`,
    `- Recovery: ${recovery ? "worker stopped; scrape failed; prior publication unfrozen" : "dry-run only"}.`,
    "",
    "### Files/Artifacts",
    "",
    `- \`${evidenceDir}/observation.json\``,
    `- \`${evidenceDir}/decision.json\``,
    `- \`${evidenceDir}/recovery.sql\` when recovery ran`,
    `- \`${evidenceDir}/rollback-to-pre-watchdog-state.sql\` when recovery ran`,
    "",
    "### Validation",
    "",
    "- The recovery transaction guards the published pointer, zero candidate mappings, worker DB activity, locks, and affected row counts.",
    "- An active worker query defers timeout instead of terminating a legitimately progressing long PostgreSQL operation.",
    ""
  ];
  writeFileSync(reportPath, `${lines.join("\n")}\n`);
  return reportPath;
}

function sendReport({ reportPath, evidenceDir, send, fallbackEnvFile }) {
  const args = [
    path.join(repositoryRoot, "tools", "agent-report-email.mjs"),
    "--subject",
    "FST Autonomous Agent: Phase 0 - Worker No-Progress Recovery · Needs Attention",
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
  const pollSeconds = Number(args.values["poll-seconds"] ?? 60);
  const stopTimeoutSeconds = Number(args.values["stop-timeout-seconds"] ?? 30);

  while (true) {
    const observation = observe({
      composeDir,
      postgresContainer,
      workerContainer
    });
    const decision = evaluateNoProgressObservation(observation, {
      idleSeconds,
      maxPhaseSeconds
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
      const recovery = args.flags.has("dry-run")
        ? null
        : stopAndRecover({
          observation,
          decision,
          composeDir,
          postgresContainer,
          workerContainer,
          evidenceDir,
          stopTimeoutSeconds
        });
      const reportPath = renderReport({
        evidenceDir,
        observation,
        decision,
        recovery
      });
      sendReport({
        reportPath,
        evidenceDir,
        send: args.flags.has("send-report"),
        fallbackEnvFile: args.values["fallback-env-file"]
      });
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
