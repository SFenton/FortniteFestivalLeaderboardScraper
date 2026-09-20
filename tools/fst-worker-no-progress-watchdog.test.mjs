import assert from "node:assert/strict";
import os from "node:os";
import { mkdtempSync, readFileSync, rmSync } from "node:fs";
import path from "node:path";
import { describe, it } from "node:test";
import {
  buildWorkerActivityPredicate,
  buildFailureIsolationCommand,
  CAPACITY_FAILURE_PHASE,
  evaluateNoProgressObservation,
  NO_PROGRESS_FAILURE_PHASE,
  parseFailureIsolationReadinessResult,
  parseDockerPercentage,
  selectFailureIsolationPhase,
  verifyFailureIsolationReadiness,
  WORKER_APPLICATION_NAMES
} from "./fst-worker-no-progress-watchdog.mjs";

function observation(overrides = {}) {
  return {
    observedAt: "2026-07-27T18:00:00Z",
    workerRunning: true,
    scrapeStatus: "running",
    scrapeStartedAt: "2026-07-27T01:00:00Z",
    publicReadsFrozen: true,
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
  it("keeps an observation row when no normalized phase attempt is running", () => {
    const source = readFileSync(
      new URL("./fst-worker-no-progress-watchdog.mjs", import.meta.url),
      "utf8"
    );

    assert.match(
      source,
      /LEFT JOIN normalized_phase normalized ON TRUE/
    );
  });

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

  it("continues observing a completed scrape while publication is frozen", () => {
    const decision = evaluateNoProgressObservation(
      observation({
        scrapeStatus: "completed",
        publicReadsFrozen: true,
        publicReadsFrozenReason: "publish",
        operation: {
          PhaseId: "publication.commit",
          StartedAtUtc: "2026-07-27T17:55:00Z",
          UpdatedAtUtc: "2026-07-27T17:59:30Z"
        },
        latestPhaseProgressAt: "2026-07-27T17:59:30Z"
      }),
      { idleSeconds: 2700 }
    );

    assert.equal(decision.decision, "healthy");
    assert.equal(decision.reason, "phase_progress_within_threshold");
    assert.equal(decision.idleForSeconds, 30);
  });

  it("never auto-recovers a completed scrape publication", () => {
    const decision = evaluateNoProgressObservation(
      observation({
        workerRunning: false,
        scrapeStatus: "completed",
        publicReadsFrozen: true,
        publicReadsFrozenReason: "publish"
      }),
      { recoverWorkerExit: true }
    );

    assert.equal(decision.decision, "terminal");
    assert.equal(
      decision.reason,
      "publication_recovery_requires_operator"
    );
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

  it("builds the guarded service command that isolates only the active candidate", () => {
    const command = buildFailureIsolationCommand({
      serviceContainer: "fstservice",
      scrapeId: 1266,
      publishedScrapeId: 1236,
      failurePhase: NO_PROGRESS_FAILURE_PHASE,
      failureMessage: "watchdog timeout",
    });

    assert.deepEqual(command, [
      "exec",
      "-i",
      "fstservice",
      "dotnet",
      "FSTService.dll",
      "--active-scrape-failure-isolation",
      "--active-scrape-failure-isolation-execute",
      "--active-scrape-id",
      "1266",
      "--published-scrape-id",
      "1236",
      "--active-scrape-failure-phase",
      NO_PROGRESS_FAILURE_PHASE,
      "--active-scrape-failure-message",
      "watchdog timeout"
    ]);
  });

  it("builds the service-owned readiness command before any worker stop", () => {
    const command = buildFailureIsolationCommand({
      serviceContainer: "fstservice",
      scrapeId: 1266,
      publishedScrapeId: 1236,
      execute: false
    });

    assert.deepEqual(command, [
      "exec",
      "-i",
      "fstservice",
      "dotnet",
      "FSTService.dll",
      "--active-scrape-failure-isolation",
      "--active-scrape-failure-isolation-check",
      "--active-scrape-id",
      "1266",
      "--published-scrape-id",
      "1236"
    ]);
  });

  it("rejects readiness payloads that refuse recovery", () => {
    assert.throws(
      () => parseFailureIsolationReadinessResult(
        JSON.stringify({
          CanExecute: false,
          BlockingReason: "worker still owns 1 active database query",
          ScrapeId: 1266,
          ExpectedPublishedScrapeId: 1236,
          PublishedScrapeId: 1236,
          CandidateStatus: "running",
          PublicReadsFrozen: true,
          FrozenScrapeId: 1236,
          FreezeReason: "post-process",
          WorkingPublicationId: 301,
          CandidatePublicationId: 301,
          CandidatePublicationStatus: "building",
          CandidatePublishedScopeRowCount: 0,
          ActiveWorkerQueryCount: 1,
          WaitingLockCount: 0,
          AdvisoryLockCount: 0,
          MaintenanceActivityPresent: false,
          RunningPhaseAttemptCount: 1,
          ForeignRunningPhaseAttemptCount: 0,
          WorkerStatus: "running",
          WorkerInstanceId: "worker-1266",
          WorkerUpdatedAtUtc: "2026-09-16T03:39:30Z",
          WorkerCurrentOperationPresent: true,
          FailedAcquisitionPhaseAttemptCount: 0,
          AcquisitionCheckpointPresent: false,
          PublicationMutationRequired: true,
          PublicationIsolationComplete: false,
          AcquisitionFailureMutationRequired: false
        }),
        {
          scrapeId: 1266,
          publishedScrapeId: 1236
        }
      ),
      /readiness refused recovery/
    );
  });

  it("rejects malformed readiness payloads", () => {
    assert.throws(
      () => parseFailureIsolationReadinessResult(
        "not-json",
        {
          scrapeId: 1266,
          publishedScrapeId: 1236
        }
      ),
      /malformed JSON/
    );
  });

  it("accepts the canonical published-baseline post-process freeze identity", () => {
    const readiness = parseFailureIsolationReadinessResult(
      JSON.stringify({
        CanExecute: true,
        BlockingReason: null,
        ScrapeId: 1266,
        ExpectedPublishedScrapeId: 1236,
        PublishedScrapeId: 1236,
        CandidateStatus: "running",
        PublicReadsFrozen: true,
        FrozenScrapeId: 1236,
        FreezeReason: "post-process",
        WorkingPublicationId: 301,
        CandidatePublicationId: 301,
        CandidatePublicationStatus: "building",
        CandidatePublishedScopeRowCount: 0,
        ActiveWorkerQueryCount: 0,
        WaitingLockCount: 0,
        AdvisoryLockCount: 0,
        MaintenanceActivityPresent: false,
        RunningPhaseAttemptCount: 1,
        ForeignRunningPhaseAttemptCount: 0,
        WorkerStatus: "running",
        WorkerInstanceId: "worker-1266",
        WorkerUpdatedAtUtc: "2026-09-16T03:39:30Z",
        WorkerCurrentOperationPresent: true,
        FailedAcquisitionPhaseAttemptCount: 0,
        AcquisitionCheckpointPresent: false,
        PublicationMutationRequired: true,
        PublicationIsolationComplete: false,
        AcquisitionFailureMutationRequired: false
      }),
      {
        scrapeId: 1266,
        publishedScrapeId: 1236
      }
    );

    assert.equal(readiness.FrozenScrapeId, 1236);
  });

  it("accepts terminalized publication state awaiting runtime convergence", () => {
    const readiness = parseFailureIsolationReadinessResult(
      JSON.stringify({
        CanExecute: true,
        BlockingReason: null,
        ScrapeId: 1266,
        ExpectedPublishedScrapeId: 1236,
        PublishedScrapeId: 1236,
        CandidateStatus: "failed",
        PublicReadsFrozen: false,
        FrozenScrapeId: null,
        FreezeReason: null,
        WorkingPublicationId: null,
        CandidatePublicationId: 301,
        CandidatePublicationStatus: "failed",
        CandidatePublishedScopeRowCount: 0,
        ActiveWorkerQueryCount: 0,
        WaitingLockCount: 0,
        AdvisoryLockCount: 0,
        MaintenanceActivityPresent: false,
        RunningPhaseAttemptCount: 1,
        ForeignRunningPhaseAttemptCount: 0,
        WorkerStatus: "running",
        WorkerInstanceId: "worker-1266",
        WorkerUpdatedAtUtc: "2026-09-16T03:39:30Z",
        WorkerCurrentOperationPresent: true,
        FailedAcquisitionPhaseAttemptCount: 0,
        AcquisitionCheckpointPresent: false,
        PublicationMutationRequired: false,
        PublicationIsolationComplete: true,
        AcquisitionFailureMutationRequired: false
      }),
      {
        scrapeId: 1266,
        publishedScrapeId: 1236
      }
    );

    assert.equal(readiness.PublicationIsolationComplete, true);
  });

  it("accepts an unfrozen uncheckpointed acquisition failure", () => {
    const readiness = parseFailureIsolationReadinessResult(
      JSON.stringify({
        CanExecute: true,
        BlockingReason: null,
        ScrapeId: 1400,
        ExpectedPublishedScrapeId: 1398,
        PublishedScrapeId: 1398,
        CandidateStatus: "running",
        PublicReadsFrozen: false,
        FrozenScrapeId: null,
        FreezeReason: null,
        WorkingPublicationId: 299,
        CandidatePublicationId: 299,
        CandidatePublicationStatus: "building",
        CandidatePublishedScopeRowCount: 0,
        ActiveWorkerQueryCount: 0,
        WaitingLockCount: 0,
        AdvisoryLockCount: 0,
        MaintenanceActivityPresent: false,
        RunningPhaseAttemptCount: 0,
        ForeignRunningPhaseAttemptCount: 0,
        WorkerStatus: "offline",
        WorkerInstanceId: "worker-1400",
        WorkerUpdatedAtUtc: "2026-09-16T21:32:23Z",
        WorkerCurrentOperationPresent: false,
        FailedAcquisitionPhaseAttemptCount: 1,
        AcquisitionCheckpointPresent: false,
        PublicationMutationRequired: false,
        PublicationIsolationComplete: false,
        AcquisitionFailureMutationRequired: true
      }),
      {
        scrapeId: 1400,
        publishedScrapeId: 1398
      }
    );

    assert.equal(
      readiness.AcquisitionFailureMutationRequired,
      true
    );
  });

  it("rejects a post-process freeze incorrectly attributed to the candidate", () => {
    assert.throws(
      () => parseFailureIsolationReadinessResult(
        JSON.stringify({
          CanExecute: true,
          BlockingReason: null,
          ScrapeId: 1266,
          ExpectedPublishedScrapeId: 1236,
          PublishedScrapeId: 1236,
          CandidateStatus: "running",
          PublicReadsFrozen: true,
          FrozenScrapeId: 1266,
          FreezeReason: "post-process",
          WorkingPublicationId: 301,
          CandidatePublicationId: 301,
          CandidatePublicationStatus: "building",
          CandidatePublishedScopeRowCount: 0,
          ActiveWorkerQueryCount: 0,
          WaitingLockCount: 0,
          AdvisoryLockCount: 0,
          MaintenanceActivityPresent: false,
          RunningPhaseAttemptCount: 1,
          ForeignRunningPhaseAttemptCount: 0,
          WorkerStatus: "running",
          WorkerInstanceId: "worker-1266",
          WorkerUpdatedAtUtc: "2026-09-16T03:39:30Z",
          WorkerCurrentOperationPresent: true,
          FailedAcquisitionPhaseAttemptCount: 0,
          AcquisitionCheckpointPresent: false,
          PublicationMutationRequired: true,
          PublicationIsolationComplete: false,
          AcquisitionFailureMutationRequired: false
        }),
        {
          scrapeId: 1266,
          publishedScrapeId: 1236
        }
      ),
      /frozen published scrape 1266, expected 1236/
    );
  });

  it("propagates readiness subprocess failures before any worker stop can run", () => {
    const evidenceDir = mkdtempSync(
      path.join(os.tmpdir(), "fst-watchdog-readiness-")
    );
    try {
      assert.throws(
        () => verifyFailureIsolationReadiness({
          composeDir: "/tmp/compose",
          serviceContainer: "fstservice",
          evidenceDir,
          scrapeId: 1266,
          publishedScrapeId: 1236,
          runCommand() {
            throw new Error("docker exec failed");
          }
        }),
        /docker exec failed/
      );
    } finally {
      rmSync(evidenceDir, { recursive: true, force: true });
    }
  });

  it("rejects readiness payloads whose identity mismatches the requested scrape", () => {
    assert.throws(
      () => parseFailureIsolationReadinessResult(
        JSON.stringify({
          CanExecute: true,
          BlockingReason: null,
          ScrapeId: 1267,
          ExpectedPublishedScrapeId: 1236,
          PublishedScrapeId: 1236,
          CandidateStatus: "running",
          PublicReadsFrozen: true,
          FrozenScrapeId: 1236,
          FreezeReason: "post-process",
          WorkingPublicationId: 301,
          CandidatePublicationId: 301,
          CandidatePublicationStatus: "building",
          CandidatePublishedScopeRowCount: 0,
          ActiveWorkerQueryCount: 0,
          WaitingLockCount: 0,
          AdvisoryLockCount: 0,
          MaintenanceActivityPresent: false,
          RunningPhaseAttemptCount: 1,
          ForeignRunningPhaseAttemptCount: 0,
          WorkerStatus: "running",
          WorkerInstanceId: "worker-1267",
          WorkerUpdatedAtUtc: "2026-09-16T03:39:30Z",
          WorkerCurrentOperationPresent: true,
          FailedAcquisitionPhaseAttemptCount: 0,
          AcquisitionCheckpointPresent: false,
          PublicationMutationRequired: true,
          PublicationIsolationComplete: false,
          AcquisitionFailureMutationRequired: false
        }),
        {
          scrapeId: 1266,
          publishedScrapeId: 1236
        }
      ),
      /expected 1266/
    );
  });

  it("maps resource-triggered watchdog recovery to the capacity isolation phase", () => {
    assert.equal(
      selectFailureIsolationPhase({
        decision: "timeout",
        reason: "worker_memory_threshold_exceeded"
      }),
      CAPACITY_FAILURE_PHASE
    );
    assert.equal(
      selectFailureIsolationPhase({
        decision: "timeout",
        reason: "worker_process_failed"
      }),
      CAPACITY_FAILURE_PHASE
    );
    assert.equal(
      selectFailureIsolationPhase({
        decision: "timeout",
        reason: "no_phase_progress"
      }),
      NO_PROGRESS_FAILURE_PHASE
    );
  });
});
