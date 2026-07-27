import assert from "node:assert/strict";
import { describe, it } from "node:test";
import {
  buildRecoverySql,
  evaluateNoProgressObservation,
  NO_PROGRESS_FAILURE_PHASE
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

  it("builds guarded recovery that preserves and unfreezes the prior publication", () => {
    const sql = buildRecoverySql({
      scrapeId: 1266,
      publishedScrapeId: 1236,
      failureMessage: "watchdog timeout",
      workerMessage: "worker stopped"
    });

    assert.match(sql, new RegExp(`failure_phase = '${NO_PROGRESS_FAILURE_PHASE}'`));
    assert.match(sql, /published_id <> 1236/);
    assert.match(sql, /published_scrape_id = 1266/);
    assert.match(sql, /candidate_mappings <> 0/);
    assert.match(sql, /active_worker_queries <> 0/);
    assert.match(sql, /pg_stat_progress_create_index/);
    assert.match(sql, /public_reads_frozen = FALSE/);
    assert.match(sql, /status = 'offline'/);
    assert.match(sql, /candidate_published_scope_rows/);
    assert.match(sql, /COMMIT;/);
  });
});
