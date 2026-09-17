import assert from "node:assert/strict";
import { describe, it } from "node:test";
import {
  buildWorkerActivityPredicate,
  buildRecoverySql,
  evaluateNoProgressObservation,
  NO_PROGRESS_FAILURE_PHASE,
  parseDockerPercentage,
  WORKER_APPLICATION_NAMES
} from "./fst-worker-no-progress-watchdog.mjs";

function observation(overrides = {}) {
  return {
    observedAt: "2026-07-27T18:00:00Z",
    workerRunning: true,
    scrapeStatus: "running",
    scrapeStartedAt: "2026-07-27T01:00:00Z",
    publicReadsFrozenReason: "post-process",
    operation: {
      OperationKey: "scrape.post_process",
      StartedAtUtc: "2026-07-27T10:00:00Z",
      UpdatedAtUtc: "2026-07-27T17:00:00Z"
    },
    latestPhaseProgressAt: "2026-07-27T17:00:00Z",
    activeWorkerQueries: 0,
    ...overrides
  };
}

describe("FST worker no-progress watchdog", () => {
  it("times out a stale post-process operation with no database activity", () => {
    const decision = evaluateNoProgressObservation(observation(), {
      idleSeconds: 2700
    });

    assert.equal(decision.decision, "timeout");
    assert.equal(decision.reason, "no_phase_progress");
    assert.equal(decision.idleForSeconds, 3600);
  });

  it("defers a stale heartbeat while a worker-owned query remains active", () => {
    const decision = evaluateNoProgressObservation(
      observation({ activeWorkerQueries: 1 }),
      { idleSeconds: 2700 }
    );

    assert.equal(decision.decision, "defer_active_query");
    assert.equal(decision.reason, "worker_database_activity_present");
  });

  it("keeps worker-exit recovery disabled by default", () => {
    const decision = evaluateNoProgressObservation(
      observation({ workerRunning: false })
    );

    assert.equal(decision.decision, "inactive");
    assert.equal(decision.reason, "worker_container_not_running");
  });

  it("recovers an OOM-killed worker during frozen post-processing when enabled", () => {
    const decision = evaluateNoProgressObservation(
      observation({
        workerRunning: false,
        workerOomKilled: true,
        activeWorkerQueries: 0
      }),
      { recoverWorkerExit: true }
    );

    assert.equal(decision.decision, "timeout");
    assert.equal(decision.reason, "worker_oom_killed");
    assert.equal(decision.phaseElapsedSeconds, 28800);
  });

  it("allows a clean run-once exit to reach terminal state during the grace period", () => {
    const decision = evaluateNoProgressObservation(
      observation({
        workerRunning: false,
        workerExitCode: 0,
        workerFinishedAt: "2026-07-27T17:59:30Z"
      }),
      {
        recoverWorkerExit: true,
        workerExitGraceSeconds: 120
      }
    );

    assert.equal(decision.decision, "healthy");
    assert.equal(decision.reason, "worker_exit_grace_period");
    assert.equal(decision.workerExitAgeSeconds, 30);
  });

  it("recovers a clean exit that remains nonterminal after the grace period", () => {
    const decision = evaluateNoProgressObservation(
      observation({
        workerRunning: false,
        workerExitCode: 0,
        workerFinishedAt: "2026-07-27T17:55:00Z"
      }),
      {
        recoverWorkerExit: true,
        workerExitGraceSeconds: 120
      }
    );

    assert.equal(decision.decision, "timeout");
    assert.equal(decision.reason, "worker_container_exited");
    assert.equal(decision.workerExitAgeSeconds, 300);
  });

  it("recovers a nonzero worker exit without waiting for the clean-exit grace", () => {
    const decision = evaluateNoProgressObservation(
      observation({
        workerRunning: false,
        workerExitCode: 137,
        workerFinishedAt: "2026-07-27T17:59:59Z"
      }),
      {
        recoverWorkerExit: true,
        workerExitGraceSeconds: 120
      }
    );

    assert.equal(decision.decision, "timeout");
    assert.equal(decision.reason, "worker_process_failed");
    assert.equal(decision.workerExitCode, 137);
  });

  it("does not recover an exited worker after the scrape is terminal", () => {
    const decision = evaluateNoProgressObservation(
      observation({
        workerRunning: false,
        scrapeStatus: "completed"
      }),
      { recoverWorkerExit: true }
    );

    assert.equal(decision.decision, "terminal");
    assert.equal(decision.reason, "scrape_completed");
  });

  it("uses the memory safety gate even while worker queries remain active", () => {
    const decision = evaluateNoProgressObservation(
      observation({
        activeWorkerQueries: 4,
        workerMemoryPercent: 91.25
      }),
      { maxWorkerMemoryPercent: 90 }
    );

    assert.equal(decision.decision, "timeout");
    assert.equal(decision.reason, "worker_memory_threshold_exceeded");
    assert.equal(decision.workerMemoryPercent, 91.25);
    assert.equal(decision.maxWorkerMemoryPercent, 90);
  });

  it("includes worker resource recovery fields in the timeout decision", () => {
    const decision = evaluateNoProgressObservation(
      observation({
        activeWorkerQueries: 3,
        workerMemoryPercent: 92.5
      }),
      { maxWorkerMemoryPercent: 90 }
    );

    assert.deepEqual(
      {
        activeWorkerQueries: decision.activeWorkerQueries,
        workerMemoryPercent: decision.workerMemoryPercent,
        maxWorkerMemoryPercent: decision.maxWorkerMemoryPercent
      },
      {
        activeWorkerQueries: 3,
        workerMemoryPercent: 92.5,
        maxWorkerMemoryPercent: 90
      }
    );
  });

  it("leaves normal query deferral unchanged below the memory threshold", () => {
    const decision = evaluateNoProgressObservation(
      observation({
        activeWorkerQueries: 4,
        workerMemoryPercent: 65
      }),
      { maxWorkerMemoryPercent: 90 }
    );

    assert.equal(decision.decision, "defer_active_query");
    assert.equal(decision.reason, "worker_database_activity_present");
  });

  it("parses Docker percentage output", () => {
    assert.equal(parseDockerPercentage(" 64.73% \n"), 64.73);
    assert.throws(
      () => parseDockerPercentage("--"),
      /Invalid Docker percentage/
    );
    assert.throws(
      () => parseDockerPercentage("101%"),
      /outside 0-100/
    );
  });

  it("targets only the worker application or its captured container IP", () => {
    assert.equal(
      buildWorkerActivityPredicate({
        workerApplicationNames: WORKER_APPLICATION_NAMES,
        workerClientIp: "172.31.0.42"
      }),
      "(application_name IN ('fstworker-scraper', 'fst-path-generation-admission') OR client_addr = '172.31.0.42'::inet)"
    );
    assert.throws(
      () => buildWorkerActivityPredicate({
        workerApplicationNames: WORKER_APPLICATION_NAMES,
        workerClientIp: "not-an-ip"
      }),
      /Invalid worker client IP/
    );
  });

  it("accepts a recent explicit phase heartbeat", () => {
    const decision = evaluateNoProgressObservation(
      observation({
        operation: {
          OperationKey: "scrape.post_process",
          StartedAtUtc: "2026-07-27T10:00:00Z",
          UpdatedAtUtc: "2026-07-27T17:50:00Z"
        }
      }),
      { idleSeconds: 2700 }
    );

    assert.equal(decision.decision, "healthy");
    assert.equal(decision.idleForSeconds, 600);
  });

  it("prefers normalized progress over a newer heartbeat-only operation update", () => {
    const decision = evaluateNoProgressObservation(
      observation({
        operation: {
          OperationKey: "scrape.post_process",
          StartedAtUtc: "2026-07-27T10:00:00Z",
          UpdatedAtUtc: "2026-07-27T17:59:00Z"
        },
        normalizedPhaseAttempt: {
          phaseId: "post.band_maintenance",
          attempt: 1,
          status: "running",
          startedAt: "2026-07-27T10:00:00Z",
          lastProgressAt: "2026-07-27T17:00:00Z",
          heartbeatAt: "2026-07-27T17:59:00Z"
        }
      }),
      { idleSeconds: 2700 }
    );

    assert.equal(decision.decision, "timeout");
    assert.equal(decision.idleForSeconds, 3600);
  });

  it("accepts recent normalized last-progress time", () => {
    const decision = evaluateNoProgressObservation(
      observation({
        operation: {
          OperationKey: "scrape.post_process",
          StartedAtUtc: "2026-07-27T10:00:00Z",
          UpdatedAtUtc: "2026-07-27T17:00:00Z"
        },
        normalizedPhaseAttempt: {
          phaseId: "post.band_maintenance",
          attempt: 1,
          status: "running",
          startedAt: "2026-07-27T10:00:00Z",
          lastProgressAt: "2026-07-27T17:55:00Z",
          heartbeatAt: "2026-07-27T17:59:00Z"
        }
      }),
      { idleSeconds: 2700 }
    );

    assert.equal(decision.decision, "healthy");
    assert.equal(decision.idleForSeconds, 300);
  });

  it("accepts recent durable registered refresh scope progress", () => {
    const decision = evaluateNoProgressObservation(
      observation({
        operation: {
          OperationKey: "scrape.post_process",
          SubOperation: "RefreshRegisteredUsers",
          StartedAtUtc: "2026-07-27T10:00:00Z",
          UpdatedAtUtc: "2026-07-27T17:00:00Z"
        },
        registeredRefreshProgressAt: "2026-07-27T17:58:00Z"
      }),
      { idleSeconds: 2700 }
    );

    assert.equal(decision.decision, "healthy");
    assert.equal(decision.idleForSeconds, 120);
  });

  it("ignores registered refresh progress outside that sub-operation", () => {
    const decision = evaluateNoProgressObservation(
      observation({
        operation: {
          OperationKey: "scrape.post_process",
          SubOperation: "BandMaintenance",
          StartedAtUtc: "2026-07-27T10:00:00Z",
          UpdatedAtUtc: "2026-07-27T17:00:00Z"
        },
        registeredRefreshProgressAt: "2026-07-27T17:58:00Z"
      }),
      { idleSeconds: 2700 }
    );

    assert.equal(decision.decision, "timeout");
    assert.equal(decision.reason, "no_phase_progress");
  });

  it("builds guarded recovery that preserves and unfreezes the prior publication", () => {
    const sql = buildRecoverySql({
      scrapeId: 1266,
      publishedScrapeId: 1236,
      failureMessage: "watchdog timeout",
      workerMessage: "worker stopped"
    });

    assert.match(sql, new RegExp(NO_PROGRESS_FAILURE_PHASE));
    assert.match(sql, /published_id <> 1236/);
    assert.match(sql, /candidate_status NOT IN \('running', 'failed'\)/);
    assert.match(sql, /published_scrape_id = 1266/);
    assert.match(sql, /candidate_mappings <> 0/);
    assert.match(sql, /active_worker_queries <> 0/);
    assert.match(
      sql,
      /application_name IN \('fstworker-scraper', 'fst-path-generation-admission'\)/
    );
    assert.match(sql, /pg_stat_progress_create_index/);
    assert.match(sql, /public_reads_frozen = FALSE/);
    assert.match(sql, /UPDATE publication_generations/);
    assert.match(sql, /UPDATE scrape_phase_attempts/);
    assert.match(sql, /status = 'interrupted'/);
    assert.doesNotMatch(sql, /last_progress_at\s*=/);
    assert.match(sql, /working_publication_id = NULL/);
    assert.match(sql, /DELETE FROM publication_api_response_cache_staging/);
    assert.match(sql, /status = 'offline'/);
    assert.match(sql, /candidate_published_scope_rows/);
    assert.match(sql, /COMMIT;/);
  });
});
