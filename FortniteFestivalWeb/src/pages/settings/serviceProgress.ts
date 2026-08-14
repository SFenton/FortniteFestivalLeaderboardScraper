import type { ServiceInfoResponse } from '@festival/core/api';

export type TrustedServiceEta = {
  lowerSeconds: number;
  upperSeconds: number;
  confidence: string;
  sampleCount: number;
};

export type ServiceProgressDisplay = {
  isV2: boolean;
  isDeterminate: boolean;
  phasePercent: number | null;
  overallPercent: number | null;
  phaseId: string | null;
  subphaseId: string | null;
  phaseStatus: string | null;
  phaseAttempt: number | null;
  phaseOrdinal: number | null;
  unitsKind: string | null;
  unitsCompleted: number | null;
  unitsTotal: number | null;
  unitsTotalFinal: boolean;
  restarted: boolean;
  stalePayloadIgnored: boolean;
  eta: TrustedServiceEta | null;
};

export type ServiceProgressMemory = {
  operationIdentity: string | null;
  phaseId: string | null;
  phaseAttempt: number | null;
  phaseOrdinal: number | null;
  lastProgressTimestamp: number | null;
  display: ServiceProgressDisplay;
};

export type ServiceProgressReduction = {
  display: ServiceProgressDisplay;
  memory: ServiceProgressMemory;
};

function finiteNumber(value: unknown): number | null {
  return typeof value === 'number' && Number.isFinite(value) ? value : null;
}

function clampPercent(value: number | null): number | null {
  return value == null ? null : Math.max(0, Math.min(100, value));
}

function parseTimestamp(value: string | null | undefined): number | null {
  if (!value) return null;
  const timestamp = Date.parse(value);
  return Number.isFinite(timestamp) ? timestamp : null;
}

function operationIdentity(serviceInfo: ServiceInfoResponse): string | null {
  const current = serviceInfo.currentUpdate;
  const scrapeId = current.scrapeId ?? serviceInfo.activeScrapeId;
  if (scrapeId == null && !current.operationId) return null;
  return [
    scrapeId ?? 'none',
    current.operationId ?? 'legacy',
    current.phasePlanVersion ?? serviceInfo.phasePlan?.version ?? 'unversioned',
  ].join(':');
}

function emptyDisplay(isV2: boolean): ServiceProgressDisplay {
  return {
    isV2,
    isDeterminate: false,
    phasePercent: null,
    overallPercent: null,
    phaseId: null,
    subphaseId: null,
    phaseStatus: null,
    phaseAttempt: null,
    phaseOrdinal: null,
    unitsKind: null,
    unitsCompleted: null,
    unitsTotal: null,
    unitsTotalFinal: false,
    restarted: false,
    stalePayloadIgnored: false,
    eta: null,
  };
}

export function reduceServiceProgress(
  previous: ServiceProgressMemory | null,
  serviceInfo: ServiceInfoResponse,
): ServiceProgressReduction {
  const current = serviceInfo.currentUpdate;
  const isV2 = serviceInfo.contractVersion === 2
    || current.contractVersion === 2
    || typeof current.phaseId === 'string';
  const identity = operationIdentity(serviceInfo);

  if (current.status !== 'updating') {
    const display = emptyDisplay(isV2);
    return {
      display,
      memory: {
        operationIdentity: null,
        phaseId: null,
        phaseAttempt: null,
        phaseOrdinal: null,
        lastProgressTimestamp: null,
        display,
      },
    };
  }

  const timestamp = parseTimestamp(
    current.lastProgressAt
      ?? current.updatedAt
      ?? current.heartbeatAt,
  );
  const sameOperation = previous?.operationIdentity != null
    && previous.operationIdentity === identity;
  const phaseId = current.phaseId ?? null;
  const phaseAttempt = finiteNumber(current.phaseAttempt);
  const phaseOrdinal = finiteNumber(current.phaseOrdinal);
  const samePhase = sameOperation
    && previous?.phaseId != null
    && previous.phaseId === phaseId;
  const attemptChanged = samePhase
    && previous?.phaseAttempt != null
    && phaseAttempt != null
    && previous.phaseAttempt !== phaseAttempt;
  const ordinalRestart = sameOperation
    && previous?.phaseOrdinal != null
    && phaseOrdinal != null
    && phaseOrdinal < previous.phaseOrdinal;
  const restarted = Boolean(attemptChanged || ordinalRestart);
  const stalePayload = Boolean(
    sameOperation
    && !restarted
    && timestamp != null
    && previous?.lastProgressTimestamp != null
    && timestamp < previous.lastProgressTimestamp,
  );

  if (stalePayload && previous) {
    const display = {
      ...previous.display,
      stalePayloadIgnored: true,
      restarted: false,
    };
    return {
      display,
      memory: {
        ...previous,
        display,
      },
    };
  }

  const unitsTotalFinal = isV2 && current.unitsTotalFinal === true;
  const rawPhasePercent = unitsTotalFinal
    ? clampPercent(finiteNumber(current.phasePercent))
    : null;
  const previousPhasePercent = samePhase && !restarted
    ? previous?.display.phasePercent ?? null
    : null;
  const phasePercent = rawPhasePercent != null && previousPhasePercent != null
    ? Math.max(previousPhasePercent, rawPhasePercent)
    : rawPhasePercent;

  const rawOverallPercent = isV2
    && current.overallPercentKind
    && current.overallPercentKind !== 'indeterminate'
      ? clampPercent(finiteNumber(current.overallPercent))
      : null;
  const previousOverallPercent = sameOperation && !restarted
    ? previous?.display.overallPercent ?? null
    : null;
  const overallPercent = rawOverallPercent != null && previousOverallPercent != null
    ? Math.max(previousOverallPercent, rawOverallPercent)
    : rawOverallPercent;

  const display: ServiceProgressDisplay = {
    isV2,
    isDeterminate: phasePercent != null,
    phasePercent,
    overallPercent,
    phaseId,
    subphaseId: current.subphaseId ?? null,
    phaseStatus: current.phaseStatus ?? null,
    phaseAttempt,
    phaseOrdinal,
    unitsKind: current.unitsKind ?? null,
    unitsCompleted: finiteNumber(current.unitsCompleted),
    unitsTotal: finiteNumber(current.unitsTotal),
    unitsTotalFinal,
    restarted,
    stalePayloadIgnored: false,
    eta: getTrustedEta(serviceInfo),
  };

  return {
    display,
    memory: {
      operationIdentity: identity,
      phaseId,
      phaseAttempt,
      phaseOrdinal,
      lastProgressTimestamp: timestamp ?? previous?.lastProgressTimestamp ?? null,
      display,
    },
  };
}

export function getTrustedEta(
  serviceInfo: ServiceInfoResponse,
): TrustedServiceEta | null {
  const current = serviceInfo.currentUpdate;
  if (current.status !== 'updating') return null;

  const lowerSeconds = finiteNumber(current.etaLowerSeconds);
  const upperSeconds = finiteNumber(current.etaUpperSeconds);
  const sampleCount = finiteNumber(current.etaSampleCount);
  const confidence = current.etaConfidence;
  const trustedConfidence = confidence === 'low'
    || confidence === 'medium'
    || confidence === 'high';

  if (
    lowerSeconds == null
    || upperSeconds == null
    || lowerSeconds < 0
    || upperSeconds < lowerSeconds
    || sampleCount == null
    || sampleCount < 5
    || !trustedConfidence
  ) {
    return null;
  }

  return {
    lowerSeconds,
    upperSeconds,
    confidence,
    sampleCount,
  };
}
