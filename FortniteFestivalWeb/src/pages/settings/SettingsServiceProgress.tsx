import { useEffect, useMemo, useRef, type CSSProperties, type ReactNode } from 'react';
import type { TFunction } from 'i18next';
import { useTranslation } from 'react-i18next';
import type { ServiceInfoResponse } from '@festival/core/api';
import { Colors, Layout, Radius, padding } from '@festival/theme';
import { FrostedCard } from '../../components/common/FrostedCard';
import ArcSpinner, { SpinnerSize } from '../../components/common/ArcSpinner';
import { modalStyles as modalCss } from '../../components/modals/modalStyles';
import {
  reduceServiceProgress,
  type ServiceProgressDisplay,
  type ServiceProgressMemory,
} from './serviceProgress';
import styles from './SettingsServiceProgress.module.css';
import './settingsEnglish';

type ServiceProgressCardProps = {
  serviceInfo: ServiceInfoResponse | null;
  loadFailed: boolean;
};

type ProcessState = 'loading' | 'updating' | 'idle' | 'stopped';

type ServiceInfoRowProps = {
  label: ReactNode;
  description?: string;
  descriptionTestId?: string;
  trailing?: ReactNode;
  testId?: string;
  children?: ReactNode;
};

const SECONDS_PER_MINUTE = 60;
const SECONDS_PER_HOUR = 60 * SECONDS_PER_MINUTE;

const cardStyle: CSSProperties = {
  borderRadius: Radius.md,
  padding: padding(Layout.paddingTop),
  '--settings-progress-muted': Colors.textSecondary,
  '--settings-progress-warning': Colors.gold,
  '--settings-progress-track': Colors.surfaceMuted,
  '--settings-progress-fill': Colors.accentPurple,
} as CSSProperties;

const serviceRowStyle: CSSProperties = {
  ...modalCss.toggleRow,
  cursor: 'default',
  transition: 'none',
};

function formatDateTime(value: string | null | undefined, fallback: string): string {
  if (!value) return fallback;
  const parsed = new Date(value);
  return Number.isNaN(parsed.valueOf())
    ? fallback
    : parsed.toLocaleString(undefined, {
      year: 'numeric',
      month: 'short',
      day: 'numeric',
      hour: 'numeric',
      minute: '2-digit',
      timeZoneName: 'short',
    });
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

function processState(serviceInfo: ServiceInfoResponse): ProcessState {
  const workerStatus = serviceInfo.workerStatus?.status;
  if (!workerStatus || workerStatus === 'offline' || workerStatus === 'stale' || workerStatus === 'stopping') {
    return 'stopped';
  }
  return serviceInfo.currentUpdate.status === 'updating' ? 'updating' : 'idle';
}

function processStateText(t: TFunction, state: ProcessState): string {
  return t(`settings.serviceInfo.processStates.${state}`, {
    defaultValue: fallbackLabel(state),
  });
}

function serviceStateText(
  t: TFunction,
  serviceInfo: ServiceInfoResponse,
  state: ProcessState,
): string {
  const status = serviceInfo.currentUpdate.status;
  if (state === 'stopped' && status !== 'failed' && status !== 'stalled') {
    return t('settings.serviceInfo.serviceStates.unavailable');
  }
  return t(`settings.serviceInfo.serviceStates.${status}`, {
    defaultValue: fallbackLabel(status),
  });
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
  ) ?? t('settings.serviceInfo.progressWaiting');
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

function ServiceInfoRow({
  label,
  description,
  descriptionTestId,
  trailing,
  testId,
  children,
}: ServiceInfoRowProps) {
  return (
    <div style={serviceRowStyle} data-testid={testId}>
      <div style={modalCss.toggleContent}>
        <div style={modalCss.toggleLabel}>{label}</div>
        {description ? (
          <div style={modalCss.toggleDesc} data-testid={descriptionTestId}>
            {description}
          </div>
        ) : null}
        {children}
      </div>
      {trailing}
    </div>
  );
}

function ProcessStateDisplay({ state }: { state: ProcessState }) {
  const { t } = useTranslation(['translation', 'settings'], { nsMode: 'fallback' });
  const showSpinner = state === 'loading' || state === 'updating';
  return (
    <div className={styles.processState} data-testid="settings-service-info-row-update-status">
      <span>{processStateText(t, state)}</span>
      {showSpinner ? (
        <ArcSpinner className={styles.spinner} size={SpinnerSize.SM} />
      ) : null}
    </div>
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
    const state: ProcessState = loadFailed ? 'stopped' : 'loading';
    return (
      <div data-testid="settings-service-info-list">
        <FrostedCard className={styles.card} style={cardStyle}>
          <ServiceInfoRow
            label={t('settings.serviceInfo.serviceStateTitle')}
            description={fallback}
            descriptionTestId="settings-service-info-row-update-sub-status"
            trailing={<ProcessStateDisplay state={state} />}
          />
        </FrostedCard>
      </div>
    );
  }

  const display = reduction.display;
  const current = serviceInfo.currentUpdate;
  const isUpdating = current.status === 'updating';
  const currentProcessState = processState(serviceInfo);
  const phaseLabel = servicePhaseLabel(t, serviceInfo, display);
  const translatedSubphaseLabel = serviceSubphaseLabel(t, serviceInfo, display);
  const subphaseLabel = translatedSubphaseLabel?.trim() === phaseLabel.trim()
    ? null
    : translatedSubphaseLabel;
  const phaseTitle = subphaseLabel ? `${phaseLabel} · ${subphaseLabel}` : phaseLabel;
  const showPhaseRow = isUpdating || Boolean(display.phaseId || current.phase);
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
  const showProgressFacts = !display.isDeterminate
    || units != null
    || phaseState != null
    || display.overallPercent != null
    || etaText != null;
  const warnings = serviceInfo.lastCompletedUpdate?.bestEffortFailureCount ?? 0;
  const publishedAt = serviceInfo.lastCompletedUpdate?.publishedAt
    ?? serviceInfo.publication?.publishedAt;
  const publicationText = formatDateTime(
    publishedAt,
    t('settings.serviceInfo.publicationUnavailable'),
  );

  return (
    <div data-testid="settings-service-info-list">
      <FrostedCard className={styles.card} style={cardStyle}>
        <section aria-label={t('settings.serviceInfo.title')}>
          <div className={styles.liveSummary} aria-live="polite">
            <ServiceInfoRow
              label={t('settings.serviceInfo.serviceStateTitle')}
              description={serviceStateText(t, serviceInfo, currentProcessState)}
              descriptionTestId="settings-service-info-row-update-sub-status"
              trailing={<ProcessStateDisplay state={currentProcessState} />}
            >
              {warnings > 0 ? (
                <div className={styles.warning}>
                  {t('settings.serviceInfo.completedWithWarnings', {
                    count: warnings,
                  })}
                </div>
              ) : null}
            </ServiceInfoRow>

            {showPhaseRow ? (
              <ServiceInfoRow
                label={phaseTitle}
                testId="settings-service-info-row-update-step-position"
              >
                {isUpdating ? (
                  <div className={styles.progressBlock}>
                    <div className={styles.progressRail}>
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
                      {display.isDeterminate ? (
                        <strong
                          className={styles.progressValue}
                          data-testid="settings-service-info-row-update-phase-progress"
                        >
                          {progressText}
                        </strong>
                      ) : null}
                    </div>
                    {showProgressFacts ? (
                      <div className={styles.progressFacts} style={modalCss.toggleDesc}>
                        {!display.isDeterminate ? (
                          <span data-testid="settings-service-info-row-update-phase-progress">
                            {progressText}
                          </span>
                        ) : null}
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
                    ) : null}
                    {display.restarted ? (
                      <span className={styles.warning}>{t('settings.serviceInfo.progressRestarted')}</span>
                    ) : null}
                  </div>
                ) : null}
              </ServiceInfoRow>
            ) : null}
          </div>

          <ServiceInfoRow
            label={t('settings.serviceInfo.lastPublishedAt')}
            description={publicationText}
            testId="settings-service-info-row-last-published-at"
          />
        </section>
      </FrostedCard>
    </div>
  );
}
