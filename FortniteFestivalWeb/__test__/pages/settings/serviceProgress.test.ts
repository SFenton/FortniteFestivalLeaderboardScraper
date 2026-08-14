import { describe, expect, it } from 'vitest';
import type { ServiceInfoResponse } from '@festival/core/api';
import {
  getTrustedEta,
  reduceServiceProgress,
  type ServiceProgressMemory,
} from '../../../src/pages/settings/serviceProgress';

function serviceInfo(
  currentUpdate: Partial<ServiceInfoResponse['currentUpdate']>,
): ServiceInfoResponse {
  return {
    contractVersion: 2,
    phasePlan: {
      version: 'fst.scrape-plan.v2',
      phases: [],
    },
    lastCompletedUpdate: null,
    currentUpdate: {
      status: 'updating',
      startedAt: '2026-08-13T17:00:00Z',
      phase: 'Scraping',
      subOperation: 'fetching_leaderboards',
      ...currentUpdate,
    },
    activeScrapeId: 1296,
    publishedScrapeId: 1295,
    publication: {
      publishedScrapeId: 1295,
      publishedAt: '2026-08-13T16:42:44Z',
      publicReadsFrozen: true,
      frozenAt: '2026-08-13T17:00:00Z',
      frozenScrapeId: 1295,
      freezeReason: 'scrape',
    },
    workerStatus: {
      workerKey: 'scraper',
      status: 'online',
    },
    nextScheduledUpdateAt: null,
  };
}

describe('service progress display reducer', () => {
  it('uses exact v2 phase progress only with a final denominator', () => {
    const final = reduceServiceProgress(null, serviceInfo({
      operationId: 'scrape.update',
      phaseId: 'scrape.leaderboards',
      phaseOrdinal: 100,
      phaseAttempt: 1,
      unitsKind: 'leaderboards',
      unitsCompleted: 25,
      unitsTotal: 100,
      unitsTotalFinal: true,
      phasePercent: 25,
      lastProgressAt: '2026-08-13T17:01:00Z',
    }));

    expect(final.display.phasePercent).toBe(25);
    expect(final.display.isDeterminate).toBe(true);

    const discovering = reduceServiceProgress(null, serviceInfo({
      operationId: 'scrape.update',
      phaseId: 'post.compute_rankings',
      phaseOrdinal: 310,
      phaseAttempt: 1,
      unitsCompleted: 5,
      unitsTotal: 10,
      unitsTotalFinal: false,
      phasePercent: 50,
      lastProgressAt: '2026-08-13T17:01:00Z',
    }));

    expect(discovering.display.phasePercent).toBeNull();
    expect(discovering.display.isDeterminate).toBe(false);
  });

  it('does not promote a legacy progressPercent into fake determinate progress', () => {
    const legacy = serviceInfo({
      contractVersion: undefined,
      phaseId: undefined,
      unitsTotalFinal: undefined,
      phasePercent: undefined,
      progressPercent: 75,
    });
    legacy.contractVersion = undefined;
    legacy.phasePlan = undefined;

    const result = reduceServiceProgress(null, legacy);

    expect(result.display.isV2).toBe(false);
    expect(result.display.phasePercent).toBeNull();
    expect(result.display.overallPercent).toBeNull();
  });

  it('prevents stale payloads from regressing the rendered phase and overall values', () => {
    const first = reduceServiceProgress(null, serviceInfo({
      operationId: 'scrape.update',
      phaseId: 'scrape.leaderboards',
      phaseOrdinal: 100,
      phaseAttempt: 1,
      phaseStatus: 'running',
      subphaseId: 'fetching_leaderboards',
      unitsKind: 'leaderboards',
      unitsCompleted: 60,
      unitsTotal: 100,
      unitsTotalFinal: true,
      phasePercent: 60,
      overallPercentKind: 'historical_phase_durations',
      overallPercent: 40,
      overallModelVersion: 'weights.v1',
      etaLowerSeconds: 60,
      etaUpperSeconds: 120,
      etaConfidence: 'medium',
      etaSampleCount: 6,
      lastProgressAt: '2026-08-13T17:02:00Z',
    }));
    const stale = reduceServiceProgress(first.memory, serviceInfo({
      operationId: 'scrape.update',
      phaseId: 'scrape.leaderboards',
      phaseOrdinal: 100,
      phaseAttempt: 1,
      phaseStatus: 'waiting',
      subphaseId: 'persisting_scores',
      unitsKind: 'leaderboards',
      unitsCompleted: 20,
      unitsTotal: 100,
      unitsTotalFinal: true,
      phasePercent: 20,
      overallPercentKind: 'historical_phase_durations',
      overallPercent: 10,
      overallModelVersion: 'weights.v1',
      etaLowerSeconds: 300,
      etaUpperSeconds: 600,
      etaConfidence: 'low',
      etaSampleCount: 6,
      lastProgressAt: '2026-08-13T17:01:00Z',
    }));

    expect(stale.display.phasePercent).toBe(60);
    expect(stale.display.overallPercent).toBe(40);
    expect(stale.display.phaseStatus).toBe('running');
    expect(stale.display.subphaseId).toBe('fetching_leaderboards');
    expect(stale.display.unitsCompleted).toBe(60);
    expect(stale.display.eta).toEqual({
      lowerSeconds: 60,
      upperSeconds: 120,
      confidence: 'medium',
      sampleCount: 6,
    });
    expect(stale.display.stalePayloadIgnored).toBe(true);
  });

  it('allows a genuine same-phase attempt restart to reset progress', () => {
    const first = reduceServiceProgress(null, serviceInfo({
      operationId: 'scrape.update',
      phaseId: 'publication.commit',
      phaseOrdinal: 900,
      phaseAttempt: 1,
      unitsTotalFinal: true,
      phasePercent: 80,
      overallPercentKind: 'historical_phase_durations',
      overallPercent: 95,
      lastProgressAt: '2026-08-13T17:02:00Z',
    }));
    const restarted = reduceServiceProgress(first.memory, serviceInfo({
      operationId: 'scrape.update',
      phaseId: 'publication.commit',
      phaseOrdinal: 900,
      phaseAttempt: 2,
      unitsTotalFinal: false,
      phasePercent: null,
      overallPercentKind: 'historical_phase_durations',
      overallPercent: 90,
      lastProgressAt: '2026-08-13T17:03:00Z',
    }));

    expect(restarted.display.restarted).toBe(true);
    expect(restarted.display.phasePercent).toBeNull();
    expect(restarted.display.overallPercent).toBe(90);
  });

  it('does not hide an attempt restart behind an older progress timestamp', () => {
    const first = reduceServiceProgress(null, serviceInfo({
      operationId: 'scrape.update',
      phaseId: 'post.compute_rankings',
      phaseOrdinal: 310,
      phaseAttempt: 1,
      unitsTotalFinal: true,
      phasePercent: 80,
      lastProgressAt: '2026-08-13T17:02:00Z',
    }));
    const restarted = reduceServiceProgress(first.memory, serviceInfo({
      operationId: 'scrape.update',
      phaseId: 'post.compute_rankings',
      phaseOrdinal: 310,
      phaseAttempt: 2,
      unitsTotalFinal: true,
      phasePercent: 5,
      lastProgressAt: '2026-08-13T17:01:59Z',
    }));

    expect(restarted.display.restarted).toBe(true);
    expect(restarted.display.stalePayloadIgnored).toBe(false);
    expect(restarted.display.phasePercent).toBe(5);
  });

  it('keeps overall progress monotonic across a normal phase transition', () => {
    const first = reduceServiceProgress(null, serviceInfo({
      operationId: 'scrape.update',
      phaseId: 'scrape.leaderboards',
      phaseOrdinal: 100,
      phaseAttempt: 1,
      unitsTotalFinal: true,
      phasePercent: 100,
      overallPercentKind: 'historical_phase_durations',
      overallPercent: 45,
      lastProgressAt: '2026-08-13T17:02:00Z',
    }));
    const next = reduceServiceProgress(first.memory, serviceInfo({
      operationId: 'scrape.update',
      phaseId: 'post.rank_recompute',
      phaseOrdinal: 200,
      phaseAttempt: 1,
      unitsTotalFinal: true,
      phasePercent: 10,
      overallPercentKind: 'historical_phase_durations',
      overallPercent: 42,
      lastProgressAt: '2026-08-13T17:03:00Z',
    }));

    expect(next.display.restarted).toBe(false);
    expect(next.display.phasePercent).toBe(10);
    expect(next.display.overallPercent).toBe(45);
  });

  it('suppresses incomplete or invalid ETA evidence', () => {
    expect(getTrustedEta(serviceInfo({
      etaLowerSeconds: 30,
      etaUpperSeconds: 60,
      etaConfidence: 'medium',
      etaSampleCount: 5,
    }))).toEqual({
      lowerSeconds: 30,
      upperSeconds: 60,
      confidence: 'medium',
      sampleCount: 5,
    });

    expect(getTrustedEta(serviceInfo({
      etaLowerSeconds: 60,
      etaUpperSeconds: 30,
      etaConfidence: 'medium',
      etaSampleCount: 5,
    }))).toBeNull();
    expect(getTrustedEta(serviceInfo({
      etaLowerSeconds: 30,
      etaUpperSeconds: 60,
      etaConfidence: null,
      etaSampleCount: 0,
    }))).toBeNull();
    expect(getTrustedEta(serviceInfo({
      etaLowerSeconds: 30,
      etaUpperSeconds: 60,
      etaConfidence: 'unsupported',
      etaSampleCount: 5,
    }))).toBeNull();
  });

  it('keeps the memory type serializable for hook state', () => {
    const result = reduceServiceProgress(null, serviceInfo({
      operationId: 'scrape.update',
      phaseId: 'scrape.leaderboards',
      phaseAttempt: 1,
      unitsTotalFinal: true,
      phasePercent: 25,
      lastProgressAt: '2026-08-13T17:01:00Z',
    }));
    const memory: ServiceProgressMemory = result.memory;

    expect(JSON.parse(JSON.stringify(memory)).display.phasePercent).toBe(25);
  });
});
