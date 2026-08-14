import { useEffect, useMemo, useRef, type CSSProperties } from 'react';
import type { TFunction } from 'i18next';
import { useTranslation } from 'react-i18next';
import type {
  BandSyncStatusResponse,
  ServiceInfoResponse,
  SyncStatusResponse,
} from '@festival/core/api';
import { Colors, Gap, Radius, padding } from '@festival/theme';
import type { SelectedProfile } from '../../hooks/data/useSelectedProfile';
import i18n from '../../i18n';
import { FrostedCard } from '../../components/common/FrostedCard';
import ArcSpinner, { SpinnerSize } from '../../components/common/ArcSpinner';
import {
  reduceServiceProgress,
  type ServiceProgressDisplay,
  type ServiceProgressMemory,
} from './serviceProgress';
import styles from './SettingsServiceProgress.module.css';
import serviceInfoEnglish from './serviceInfo.en.json';

i18n.addResourceBundle('en', 'translation', {
  settings: { serviceInfo: serviceInfoEnglish },
}, true, true);

type MetricRow = {
  id: string;
  label: string;
  value: string;
  spinner?: boolean;
};

type ServiceProgressCardProps = {
  serviceInfo: ServiceInfoResponse | null;
  loadFailed: boolean;
};

type SelectedProfileSyncCardProps = {
  profile: SelectedProfile;
  playerStatus: SyncStatusResponse | null;
  bandStatus: BandSyncStatusResponse | null;
  loadFailed: boolean;
};

const SECONDS_PER_MINUTE = 60;
const SECONDS_PER_HOUR = 60 * SECONDS_PER_MINUTE;

const cardStyle: CSSProperties = {
  borderRadius: Radius.md,
  padding: padding(Gap.lg),
  '--settings-progress-muted': Colors.textSecondary,
  '--settings-progress-warning': Colors.gold,
  '--settings-progress-track': Colors.surfaceMuted,
  '--settings-progress-fill': Colors.accentPurple,
  '--settings-progress-focus': Colors.accentBlue,
  '--settings-progress-border': Colors.glassBorder,
  '--settings-progress-group': Colors.glowHighlight,
} as CSSProperties;

function formatDateTime(value: string | null | undefined, fallback: string): string {
  if (!value) return fallback;
  const parsed = new Date(value);
  return Number.isNaN(parsed.valueOf()) ? fallback : parsed.toLocaleString();
}

function formatDuration(totalSeconds: number): string {
  const clamped = Math.max(0, Math.floor(totalSeconds));
  const hours = Math.floor(clamped / SECONDS_PER_HOUR);
  const minutes = Math.floor((clamped % SECONDS_PER_HOUR) / SECONDS_PER_MINUTE);
  const seconds = clamped % SECONDS_PER_MINUTE;
  if (hours > 0) return `${hours}h ${minutes}m`;
  if (minutes > 0) return `${minutes}m ${seconds}s`;
  return `${seconds}s`;
}

function fallbackLabel(id: string): string {
  return id
    .replace(/[._]/g, ' ')
    .replace(/([a-z0-9])([A-Z])/g, '$1 $2')
    .replace(/\b\w/g, letter => letter.toUpperCase())
    .trim();
}

function translatedStableLabel(
  t: TFunction,
  namespace: 'phaseLabels' | 'subphaseLabels' | 'unitLabels',
  id: string | null | undefined,
  fallback?: string | null,
): string | null {
  if (!id) return fallback ?? null;
  const key = id.replace(/[.-]/g, '_');
  return t(`settings.serviceInfo.${namespace}.${key}`, {
    defaultValue: fallback || fallbackLabel(id),
  });
}

function workerStatusText(t: TFunction, serviceInfo: ServiceInfoResponse): string {
  const status = serviceInfo.workerStatus?.status;
  return t(`settings.serviceInfo.workerStates.${status || 'unknown'}`, {
    defaultValue: status || t('settings.serviceInfo.workerStates.unknown'),
  });
}

function updateStatusText(t: TFunction, serviceInfo: ServiceInfoResponse): string {
  const status = serviceInfo.currentUpdate.status;
  return t(`settings.serviceInfo.updateStates.${status}`, {
    defaultValue: fallbackLabel(status),
  });
}

function healthMessage(t: TFunction, serviceInfo: ServiceInfoResponse): string {
  const status = serviceInfo.currentUpdate.status;
  if (status === 'failed') {
    return serviceInfo.publishedScrapeId
      ? t('settings.serviceInfo.healthFailedPublished', {
        scrapeId: serviceInfo.publishedScrapeId,
      })
      : t('settings.serviceInfo.healthFailedUnavailable');
  }
  if (status === 'stalled') {
    return serviceInfo.publishedScrapeId
      ? t('settings.serviceInfo.healthStalledPublished', {
        scrapeId: serviceInfo.publishedScrapeId,
      })
      : t('settings.serviceInfo.healthStalledUnavailable');
  }
  if (serviceInfo.workerStatus?.status === 'stale') {
    return serviceInfo.publishedScrapeId
      ? t('settings.serviceInfo.healthWorkerStalePublished', {
        scrapeId: serviceInfo.publishedScrapeId,
      })
      : t('settings.serviceInfo.healthWorkerStaleUnavailable');
  }
  if (status === 'updating') {
    return serviceInfo.publishedScrapeId
      ? t('settings.serviceInfo.healthUpdatingPublished', {
        scrapeId: serviceInfo.publishedScrapeId,
      })
      : t('settings.serviceInfo.healthUpdatingUnavailable');
  }
  return serviceInfo.publishedScrapeId
    ? t('settings.serviceInfo.healthIdlePublished', {
      scrapeId: serviceInfo.publishedScrapeId,
    })
    : t('settings.serviceInfo.healthIdleUnavailable');
}

function operationStatusText(t: TFunction, status: string | null | undefined): string {
  if (!status) return t('settings.serviceInfo.unavailable');
  return t(`settings.serviceInfo.operationStates.${status}`, {
    defaultValue: fallbackLabel(status),
  });
}

function phaseStatusText(t: TFunction, status: string | null | undefined): string | null {
  if (!status) return null;
  return t(`settings.serviceInfo.phaseStates.${status}`, {
    defaultValue: fallbackLabel(status),
  });
}

function MetricRows({ rows }: { rows: MetricRow[] }) {
  return (
    <div className={styles.metricList}>
      {rows.map(row => (
        <div
          key={row.id}
          className={styles.metricRow}
          data-testid={`settings-service-info-row-${row.id}`}
        >
          <span className={styles.metricLabel}>{row.label}</span>
          <div className={styles.metricValue}>
            <span className={styles.metricValueText}>{row.value}</span>
            {row.spinner ? <ArcSpinner className={styles.spinner} size={SpinnerSize.SM} /> : null}
          </div>
        </div>
      ))}
    </div>
  );
}

function servicePhaseLabel(
  t: TFunction,
  serviceInfo: ServiceInfoResponse,
  display: ServiceProgressDisplay,
): string {
  const current = serviceInfo.currentUpdate;
  const descriptor = serviceInfo.phasePlan?.phases.find(phase => phase.id === display.phaseId);
  return translatedStableLabel(
    t,
    'phaseLabels',
    display.phaseId,
    descriptor?.label ?? current.phase,
  ) ?? t('settings.serviceInfo.progressWaiting');
}

function serviceSubphaseLabel(
  t: TFunction,
  serviceInfo: ServiceInfoResponse,
  display: ServiceProgressDisplay,
): string | null {
  const current = serviceInfo.currentUpdate;
  return translatedStableLabel(
    t,
    'subphaseLabels',
    display.subphaseId ?? current.subOperation,
    current.subOperation,
  );
}

function unitsText(
  t: TFunction,
  display: ServiceProgressDisplay,
): string | null {
  if (display.unitsCompleted == null) return null;
  const unit = translatedStableLabel(
    t,
    'unitLabels',
    display.unitsKind,
    display.unitsKind,
  ) ?? t('settings.serviceInfo.unitsGeneric');
  if (display.unitsTotal != null) {
    return display.unitsTotalFinal
      ? t('settings.serviceInfo.unitsCompletedOfTotal', {
        completed: display.unitsCompleted.toLocaleString(),
        total: display.unitsTotal.toLocaleString(),
        unit,
      })
      : t('settings.serviceInfo.unitsCompletedDiscovering', {
        completed: display.unitsCompleted.toLocaleString(),
        total: display.unitsTotal.toLocaleString(),
        unit,
      });
  }
  return t('settings.serviceInfo.unitsCompleted', {
    completed: display.unitsCompleted.toLocaleString(),
    unit,
  });
}

function technicalRows(
  t: TFunction,
  serviceInfo: ServiceInfoResponse,
  fallback: string,
): MetricRow[] {
  const current = serviceInfo.currentUpdate;
  const worker = serviceInfo.workerStatus;
  const operation = worker?.currentOperation ?? worker?.lastOperation;
  return [
    {
      id: 'last-update-start',
      label: t('settings.serviceInfo.lastUpdateStart'),
      value: formatDateTime(serviceInfo.lastCompletedUpdate?.startedAt, fallback),
    },
    {
      id: 'last-update-complete',
      label: t('settings.serviceInfo.lastUpdateComplete'),
      value: formatDateTime(serviceInfo.lastCompletedUpdate?.completedAt, fallback),
    },
    {
      id: 'worker-activity',
      label: t('settings.serviceInfo.workerActivity'),
      value: operation?.operationLabel ?? t('settings.serviceInfo.workerActivityIdle'),
    },
    {
      id: 'worker-activity-start',
      label: t('settings.serviceInfo.workerActivityStart'),
      value: formatDateTime(operation?.startedAt, fallback),
    },
    {
      id: 'worker-activity-update',
      label: t('settings.serviceInfo.workerActivityUpdate'),
      value: formatDateTime(operation?.updatedAt, fallback),
    },
    {
      id: 'worker-activity-end',
      label: t('settings.serviceInfo.workerActivityEnd'),
      value: formatDateTime(operation?.endedAt, fallback),
    },
    {
      id: 'worker-heartbeat',
      label: t('settings.serviceInfo.workerHeartbeat'),
      value: formatDateTime(worker?.lastHeartbeatAt, fallback),
    },
    {
      id: 'worker-instance',
      label: t('settings.serviceInfo.workerInstance'),
      value: worker?.instanceId ?? fallback,
    },
    {
      id: 'operation-key',
      label: t('settings.serviceInfo.operationKey'),
      value: operation?.operationKey ?? fallback,
    },
    {
      id: 'operation-status',
      label: t('settings.serviceInfo.operationStatus'),
      value: operationStatusText(t, operation?.status),
    },
    {
      id: 'phase-plan-version',
      label: t('settings.serviceInfo.phasePlanVersion'),
      value: current.phasePlanVersion ?? serviceInfo.phasePlan?.version ?? fallback,
    },
    {
      id: 'phase-id',
      label: t('settings.serviceInfo.phaseId'),
      value: current.phaseId ?? fallback,
    },
    {
      id: 'phase-attempt',
      label: t('settings.serviceInfo.phaseAttempt'),
      value: current.phaseAttempt?.toLocaleString() ?? fallback,
    },
    {
      id: 'phase-status',
      label: t('settings.serviceInfo.phaseStatus'),
      value: operationStatusText(t, current.phaseStatus),
    },
    {
      id: 'operation-detail',
      label: t('settings.serviceInfo.operationDetail'),
      value: current.detail ?? operation?.detail ?? fallback,
    },
    {
      id: 'active-scrape-id',
      label: t('settings.serviceInfo.activeScrapeId'),
      value: serviceInfo.activeScrapeId?.toLocaleString() ?? fallback,
    },
    {
      id: 'published-scrape-id',
      label: t('settings.serviceInfo.publishedScrapeId'),
      value: serviceInfo.publishedScrapeId?.toLocaleString() ?? fallback,
    },
    {
      id: 'overall-model-version',
      label: t('settings.serviceInfo.overallModelVersion'),
      value: current.overallModelVersion ?? fallback,
    },
    {
      id: 'last-progress-at',
      label: t('settings.serviceInfo.lastProgressAt'),
      value: formatDateTime(current.lastProgressAt, fallback),
    },
    {
      id: 'heartbeat-at',
      label: t('settings.serviceInfo.heartbeatAt'),
      value: formatDateTime(current.heartbeatAt, fallback),
    },
    {
      id: 'service-instance',
      label: t('settings.serviceInfo.serviceInstance'),
      value: serviceInfo.serviceInstance?.nonce ?? fallback,
    },
  ];
}

export function SettingsServiceProgressCard({
  serviceInfo,
  loadFailed,
}: ServiceProgressCardProps) {
  const { t } = useTranslation();
  const progressMemory = useRef<ServiceProgressMemory | null>(null);
  const fallback = loadFailed
    ? t('common.failedToLoad')
    : serviceInfo
      ? t('settings.serviceInfo.unavailable')
      : t('common.loading');
  const reduction = useMemo(() => {
    if (!serviceInfo) return null;
    return reduceServiceProgress(progressMemory.current, serviceInfo);
  }, [serviceInfo]);
  useEffect(() => {
    if (reduction) progressMemory.current = reduction.memory;
  }, [reduction]);

  if (!serviceInfo || !reduction) {
    return (
      <FrostedCard className={styles.group} style={cardStyle}>
        <p className={styles.statusSupporting}>{fallback}</p>
      </FrostedCard>
    );
  }

  const display = reduction.display;
  const current = serviceInfo.currentUpdate;
  const isUpdating = current.status === 'updating';
  const phaseLabel = servicePhaseLabel(t, serviceInfo, display);
  const subphaseLabel = serviceSubphaseLabel(t, serviceInfo, display);
  const phaseState = phaseStatusText(t, display.phaseStatus);
  const units = unitsText(t, display);
  const eta = display.eta;
  const etaText = eta
    ? t('settings.serviceInfo.etaRange', {
      lower: formatDuration(eta.lowerSeconds),
      upper: formatDuration(eta.upperSeconds),
      confidence: t(`settings.serviceInfo.etaConfidence.${eta.confidence}`, {
        defaultValue: fallbackLabel(eta.confidence),
      }),
      samples: eta.sampleCount,
    })
    : null;
  const progressText = display.isDeterminate
    ? t('settings.serviceInfo.progressPercent', {
      percent: display.phasePercent?.toFixed(1),
    })
    : t('settings.serviceInfo.progressIndeterminate');
  const progressAriaText = [
    phaseLabel,
    subphaseLabel,
    display.isDeterminate ? progressText : t('settings.serviceInfo.progressUnknownTotal'),
    units,
  ].filter(Boolean).join('. ');
  const warnings = serviceInfo.lastCompletedUpdate?.bestEffortFailureCount ?? 0;

  const publishedAt = serviceInfo.lastCompletedUpdate?.publishedAt
    ?? serviceInfo.publication?.publishedAt;
  const publicationRows: MetricRow[] = [
    ...(publishedAt
      ? [{
      id: 'last-published-at',
      label: t('settings.serviceInfo.lastPublishedAt'),
      value: formatDateTime(publishedAt, fallback),
      }]
      : []),
    ...(current.status !== 'idle' && current.startedAt
      ? [{
        id: 'current-update-start',
        label: t('settings.serviceInfo.currentUpdateStart'),
        value: formatDateTime(current.startedAt, fallback),
      }]
      : []),
    ...(serviceInfo.nextScheduledUpdateAt
      ? [{
        id: 'next-scheduled-update',
        label: t('settings.serviceInfo.nextScheduledUpdate'),
        value: formatDateTime(serviceInfo.nextScheduledUpdateAt, fallback),
      }]
      : []),
  ];
  const healthRows: MetricRow[] = [
    {
      id: 'worker-status',
      label: t('settings.serviceInfo.workerStatus'),
      value: workerStatusText(t, serviceInfo),
    },
    {
      id: 'update-status',
      label: t('settings.serviceInfo.updateStatus'),
      value: updateStatusText(t, serviceInfo),
      spinner: isUpdating,
    },
    {
      id: 'update-sub-status',
      label: t('settings.serviceInfo.updateSubStatus'),
      value: healthMessage(t, serviceInfo),
      spinner: isUpdating,
    },
  ];

  return (
    <div data-testid="settings-service-info-list">
      <FrostedCard style={cardStyle}>
        <div className={styles.overview}>
          <section
            className={styles.group}
            aria-labelledby="settings-service-health-title"
            data-testid="settings-service-health"
          >
            <h3 id="settings-service-health-title" className={styles.groupTitle}>
              {t('settings.serviceInfo.healthTitle')}
            </h3>
            <div aria-live="polite">
              <MetricRows rows={healthRows} />
            </div>
            {warnings > 0 ? (
              <p className={styles.warning}>
                {t('settings.serviceInfo.completedWithWarnings', {
                  count: warnings,
                })}
              </p>
            ) : null}
          </section>

          <section
            className={styles.group}
            aria-labelledby="settings-service-progress-title"
            data-testid="settings-service-progress"
          >
            <h3 id="settings-service-progress-title" className={styles.groupTitle}>
              {t('settings.serviceInfo.progressTitle')}
            </h3>
            <div className={styles.progressBlock} aria-live="polite">
              <div
                className={styles.progressText}
                data-testid="settings-service-info-row-update-step-position"
              >
                <strong>{phaseLabel}</strong>
                {subphaseLabel ? ` · ${subphaseLabel}` : ''}
              </div>
              {phaseState ? (
                <span className={styles.statusSupporting}>
                  {t('settings.serviceInfo.phaseState', { state: phaseState })}
                </span>
              ) : null}
              {isUpdating ? (
                <div
                  className={`${styles.progressTrack} ${display.isDeterminate ? '' : styles.progressIndeterminate}`}
                  role="progressbar"
                  aria-label={t('settings.serviceInfo.phaseProgressAria')}
                  aria-valuemin={display.isDeterminate ? 0 : undefined}
                  aria-valuemax={display.isDeterminate ? 100 : undefined}
                  aria-valuenow={display.phasePercent ?? undefined}
                  aria-valuetext={progressAriaText}
                  data-testid="settings-service-phase-progress"
                  data-progress-kind={display.isDeterminate ? 'determinate' : 'indeterminate'}
                >
                  {display.isDeterminate ? (
                    <div
                      className={styles.progressFill}
                      style={{ width: `${display.phasePercent}%` }}
                    />
                  ) : null}
                </div>
              ) : null}
              <span
                className={styles.statusSupporting}
                data-testid="settings-service-info-row-update-phase-progress"
              >
                {isUpdating ? progressText : healthMessage(t, serviceInfo)}
              </span>
              {units ? <span className={styles.statusSupporting}>{units}</span> : null}
              {display.overallPercent != null ? (
                <span
                  className={styles.statusSupporting}
                  data-testid="settings-service-info-row-update-overall-progress"
                >
                  {t('settings.serviceInfo.overallEstimate', {
                    percent: display.overallPercent.toFixed(1),
                  })}
                </span>
              ) : null}
              {etaText ? (
                <span
                  className={styles.statusSupporting}
                  data-testid="settings-service-info-row-update-eta"
                >
                  {etaText}
                </span>
              ) : null}
              {display.restarted ? (
                <span className={styles.warning}>{t('settings.serviceInfo.progressRestarted')}</span>
              ) : null}
            </div>
          </section>

          <section
            className={styles.group}
            aria-labelledby="settings-service-publication-title"
            data-testid="settings-service-publication"
          >
            <h3 id="settings-service-publication-title" className={styles.groupTitle}>
              {t('settings.serviceInfo.publicationTitle')}
            </h3>
            {publicationRows.length > 0 ? (
              <MetricRows rows={publicationRows} />
            ) : (
              <p className={styles.statusSupporting}>
                {t('settings.serviceInfo.publicationUnavailable')}
              </p>
            )}
          </section>
        </div>

        <details className={styles.technicalDetails} data-testid="settings-service-technical-details">
          <summary className={styles.technicalSummary}>
            {t('settings.serviceInfo.technicalDetails')}
          </summary>
          <div className={styles.technicalRows}>
            <MetricRows rows={technicalRows(t, serviceInfo, t('settings.serviceInfo.notApplicable'))} />
          </div>
        </details>
      </FrostedCard>
    </div>
  );
}

function profileSyncStatus(
  t: TFunction,
  profile: SelectedProfile,
  playerStatus: SyncStatusResponse | null,
  bandStatus: BandSyncStatusResponse | null,
  fallback: string,
): string {
  if (profile.type === 'player') {
    if (!playerStatus) return fallback;
    const status = playerStatus?.rivals?.status
      ?? (playerStatus?.isTracked ? 'pending' : 'unknown');
    return t(`settings.serviceInfo.profileSyncStates.${status}`, {
      defaultValue: fallbackLabel(status),
    });
  }
  if (!bandStatus) return fallback;
  const status = bandStatus?.processing?.status
    ?? (bandStatus?.isTracked ? 'pending' : 'unknown');
  return t(`settings.serviceInfo.profileSyncStates.${status}`, {
    defaultValue: fallbackLabel(status),
  });
}

export function SelectedProfileSyncCard({
  profile,
  playerStatus,
  bandStatus,
  loadFailed,
}: SelectedProfileSyncCardProps) {
  const { t } = useTranslation();
  const fallback = loadFailed
    ? t('common.failedToLoad')
    : t('common.loading');
  const rows: MetricRow[] = profile.type === 'player'
    ? [
      {
        id: 'selected-player-id',
        label: t('settings.serviceInfo.selectedPlayerId'),
        value: profile.accountId,
      },
      {
        id: 'selected-player-rivals-status',
        label: t('settings.serviceInfo.selectedPlayerRivalsStatus'),
        value: profileSyncStatus(t, profile, playerStatus, bandStatus, fallback),
      },
    ]
    : [
      {
        id: 'selected-band-id',
        label: t('settings.serviceInfo.selectedBandId'),
        value: profile.bandId,
      },
      {
        id: 'selected-band-sync-status',
        label: t('settings.serviceInfo.selectedBandSyncStatus'),
        value: profileSyncStatus(t, profile, playerStatus, bandStatus, fallback),
      },
    ];

  return (
    <div data-testid="settings-selected-profile-sync">
      <FrostedCard className={styles.profileCard} style={cardStyle}>
        <MetricRows rows={rows} />
      </FrostedCard>
    </div>
  );
}
