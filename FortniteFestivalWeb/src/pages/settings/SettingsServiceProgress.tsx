import { useEffect, useMemo, useRef, type CSSProperties } from 'react';
import type { TFunction } from 'i18next';
import { useTranslation } from 'react-i18next';
import type { ServiceInfoResponse } from '@festival/core/api';
import { Colors, Gap, Radius, padding } from '@festival/theme';
import { FrostedCard } from '../../components/common/FrostedCard';
import ArcSpinner, { SpinnerSize } from '../../components/common/ArcSpinner';
import {
  reduceServiceProgress,
  type ServiceProgressDisplay,
  type ServiceProgressMemory,
} from './serviceProgress';
import styles from './SettingsServiceProgress.module.css';
import './settingsEnglish';

type TimingRow = {
  id: string;
  label: string;
  value: string;
};

type ServiceProgressCardProps = {
  serviceInfo: ServiceInfoResponse | null;
  loadFailed: boolean;
};

const SECONDS_PER_MINUTE = 60;
const SECONDS_PER_HOUR = 60 * SECONDS_PER_MINUTE;

const cardStyle: CSSProperties = {
  borderRadius: Radius.md,
  padding: padding(Gap.lg),
  '--settings-progress-muted': Colors.textSecondary,
  '--settings-progress-tertiary': Colors.textTertiary,
  '--settings-progress-warning': Colors.gold,
  '--settings-progress-track': Colors.surfaceMuted,
  '--settings-progress-fill': Colors.accentPurple,
  '--settings-progress-divider': Colors.borderSubtle,
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
  const hasPublication = serviceInfo.publishedScrapeId != null;
  if (status === 'failed') {
    return t(hasPublication
      ? 'settings.serviceInfo.healthFailedPublished'
      : 'settings.serviceInfo.healthFailedUnavailable');
  }
  if (status === 'stalled') {
    return t(hasPublication
      ? 'settings.serviceInfo.healthStalledPublished'
      : 'settings.serviceInfo.healthStalledUnavailable');
  }
  if (serviceInfo.workerStatus?.status === 'stale') {
    return t(hasPublication
      ? 'settings.serviceInfo.healthWorkerStalePublished'
      : 'settings.serviceInfo.healthWorkerStaleUnavailable');
  }
  if (status === 'updating') {
    return t(hasPublication
      ? 'settings.serviceInfo.healthUpdatingPublished'
      : 'settings.serviceInfo.healthUpdatingUnavailable');
  }
  return t(hasPublication
    ? 'settings.serviceInfo.healthIdlePublished'
    : 'settings.serviceInfo.healthIdleUnavailable');
}

function phaseStatusText(t: TFunction, status: string | null | undefined): string | null {
  if (!status || status === 'running') return null;
  return t(`settings.serviceInfo.phaseStates.${status}`, {
    defaultValue: fallbackLabel(status),
  });
}

function servicePhaseLabel(
  t: TFunction,
  serviceInfo: ServiceInfoResponse,
  display: ServiceProgressDisplay,
): string {
  const current = serviceInfo.currentUpdate;
  if (current.status === 'idle') {
    return t('settings.serviceInfo.waitingForNextUpdate');
  }
  const descriptor = serviceInfo.phasePlan?.phases.find(phase => phase.id === display.phaseId);
  return translatedStableLabel(
    t,
    'phaseLabels',
    display.phaseId,
    descriptor?.label ?? current.phase,
  ) ?? updateStatusText(t, serviceInfo);
}

function serviceSubphaseLabel(
  t: TFunction,
  serviceInfo: ServiceInfoResponse,
  display: ServiceProgressDisplay,
): string | null {
  const current = serviceInfo.currentUpdate;
  const subphaseId = display.subphaseId ?? current.subOperation;
  if (!subphaseId || subphaseId === current.status) return null;
  return translatedStableLabel(
    t,
    'subphaseLabels',
    subphaseId,
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

function TimingRows({ rows, label }: { rows: TimingRow[]; label: string }) {
  return (
    <dl className={styles.timingGrid} aria-label={label}>
      {rows.map(row => (
        <div
          key={row.id}
          className={styles.timingItem}
          data-testid={`settings-service-info-row-${row.id}`}
        >
          <dt className={styles.timingLabel}>{row.label}</dt>
          <dd className={styles.timingValue}>{row.value}</dd>
        </div>
      ))}
    </dl>
  );
}

export function SettingsServiceProgressCard({
  serviceInfo,
  loadFailed,
}: ServiceProgressCardProps) {
  const { t } = useTranslation(['translation', 'settings'], { nsMode: 'fallback' });
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
      <div data-testid="settings-service-info-list">
        <FrostedCard className={styles.card} style={cardStyle}>
          <p className={styles.statusSupporting}>{fallback}</p>
        </FrostedCard>
      </div>
    );
  }

  const display = reduction.display;
  const current = serviceInfo.currentUpdate;
  const isUpdating = current.status === 'updating';
  const phaseLabel = servicePhaseLabel(t, serviceInfo, display);
  const translatedSubphaseLabel = serviceSubphaseLabel(t, serviceInfo, display);
  const subphaseLabel = translatedSubphaseLabel?.trim() === phaseLabel.trim()
    ? null
    : translatedSubphaseLabel;
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
  const timingRows: TimingRow[] = [
    {
      id: 'last-published-at',
      label: t('settings.serviceInfo.lastPublishedAt'),
      value: formatDateTime(publishedAt, t('settings.serviceInfo.publicationUnavailable')),
    },
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

  return (
    <div data-testid="settings-service-info-list">
      <FrostedCard className={styles.card} style={cardStyle}>
        <section aria-label={t('settings.serviceInfo.title')}>
          <div className={styles.liveSummary} aria-live="polite">
            <div
              className={styles.statusLine}
              data-testid="settings-service-info-row-update-status"
            >
              {isUpdating ? <ArcSpinner className={styles.spinner} size={SpinnerSize.SM} /> : null}
              <span className={styles.statusValue}>
                {updateStatusText(t, serviceInfo)}
              </span>
              <span className={styles.statusSeparator} aria-hidden="true">·</span>
              <span
                className={styles.workerStatus}
                data-testid="settings-service-info-row-worker-status"
              >
                {t('settings.serviceInfo.workerSummary', {
                  status: workerStatusText(t, serviceInfo),
                })}
              </span>
            </div>

            <div
              className={styles.phaseBlock}
              data-testid="settings-service-info-row-update-step-position"
            >
              <span className={styles.phaseEyebrow}>
                {t('settings.serviceInfo.currentStep')}
              </span>
              <span className={styles.phaseTitle}>{phaseLabel}</span>
              {subphaseLabel ? (
                <span className={styles.phaseSubtitle}>{subphaseLabel}</span>
              ) : null}
            </div>

            {isUpdating ? (
              <div className={styles.progressBlock}>
                <div className={styles.progressHeader}>
                  <span className={styles.progressLabel}>
                    {t('settings.serviceInfo.phaseProgressLabel')}
                  </span>
                  <strong
                    className={styles.progressValue}
                    data-testid="settings-service-info-row-update-phase-progress"
                  >
                    {progressText}
                  </strong>
                </div>
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
                <div className={styles.progressFacts}>
                  {units ? <span>{units}</span> : null}
                  {phaseState ? (
                    <span>{t('settings.serviceInfo.phaseState', { state: phaseState })}</span>
                  ) : null}
                  {display.overallPercent != null ? (
                    <span data-testid="settings-service-info-row-update-overall-progress">
                      {t('settings.serviceInfo.overallEstimate', {
                        percent: display.overallPercent.toFixed(1),
                      })}
                    </span>
                  ) : null}
                  {etaText ? (
                    <span data-testid="settings-service-info-row-update-eta">
                      {etaText}
                    </span>
                  ) : null}
                </div>
                {display.restarted ? (
                  <span className={styles.warning}>{t('settings.serviceInfo.progressRestarted')}</span>
                ) : null}
              </div>
            ) : null}

            <p
              className={styles.statusSupporting}
              data-testid="settings-service-info-row-update-sub-status"
            >
              {healthMessage(t, serviceInfo)}
            </p>
            {warnings > 0 ? (
              <p className={styles.warning}>
                {t('settings.serviceInfo.completedWithWarnings', {
                  count: warnings,
                })}
              </p>
            ) : null}
          </div>
        </section>

        <TimingRows
          rows={timingRows}
          label={t('settings.serviceInfo.timingLabel')}
        />
      </FrostedCard>
    </div>
  );
}
