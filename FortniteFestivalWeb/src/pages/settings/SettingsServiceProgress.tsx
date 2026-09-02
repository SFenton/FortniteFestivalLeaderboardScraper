import { useEffect, useMemo, useRef, type CSSProperties, type ReactNode } from 'react';
import type { TFunction } from 'i18next';
import { useTranslation } from 'react-i18next';
import {
  SERVER_INSTRUMENT_KEYS,
  serverInstrumentLabel,
  type ServerInstrumentKey,
  type ServiceInfoResponse,
} from '@festival/core/api';
import { Colors, Layout, Radius, padding } from '@festival/theme';
import { FrostedCard } from '../../components/common/FrostedCard';
import ArcSpinner, { SpinnerSize } from '../../components/common/ArcSpinner';
import { modalStyles as modalCss } from '../../components/modals/modalStyles';
import {
  reduceServiceProgress,
  type ServiceBarProgress,
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

const cardStyle: CSSProperties = {
  borderRadius: Radius.md,
  padding: padding(Layout.paddingTop),
  '--settings-progress-muted': Colors.textSecondary,
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

function dynamicSubphaseLabel(
  t: TFunction,
  subphaseId: string,
): string | null {
  const normalized = subphaseId.trim();
  const lower = normalized.toLowerCase();
  const soloPrefix = 'cleanup_rank_history_';
  const bandPrefix = 'cleanup_band_rank_history_';
  let suffix: string | null = null;
  let translationKey: 'cleanupRankHistory' | 'cleanupBandRankHistory' | null = null;
  if (lower.startsWith(bandPrefix)) {
    suffix = normalized.slice(bandPrefix.length);
    translationKey = 'cleanupBandRankHistory';
  } else if (lower.startsWith(soloPrefix)) {
    suffix = normalized.slice(soloPrefix.length);
    translationKey = 'cleanupRankHistory';
  }
  if (!suffix || !translationKey) return null;

  const scope = SERVER_INSTRUMENT_KEYS.includes(suffix as ServerInstrumentKey)
    ? serverInstrumentLabel(suffix as ServerInstrumentKey)
    : fallbackLabel(suffix);
  return t(`settings.serviceInfo.subphasePatterns.${translationKey}`, { scope });
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
  return dynamicSubphaseLabel(t, subphaseId) ?? translatedStableLabel(
    t,
    'subphaseLabels',
    subphaseId,
    fallbackLabel(current.subOperation ?? subphaseId),
  );
}

function barUnitsText(
  t: TFunction,
  progress: ServiceBarProgress | null,
): string | null {
  if (progress?.unitsCompleted == null) return null;
  const unit = translatedStableLabel(
    t,
    'unitLabels',
    progress.unitsKind,
    progress.unitsKind ? fallbackLabel(progress.unitsKind) : null,
  ) ?? t('settings.serviceInfo.unitsGeneric');
  if (progress.unitsTotal != null) {
    return t('settings.serviceInfo.unitsCompletedOfTotal', {
      completed: progress.unitsCompleted.toLocaleString(),
      total: progress.unitsTotal.toLocaleString(),
      unit,
    });
  }
  return t('settings.serviceInfo.unitsCompleted', {
    completed: progress.unitsCompleted.toLocaleString(),
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
  const subphaseLabel = translatedSubphaseLabel?.trim().toLocaleLowerCase()
      === phaseLabel.trim().toLocaleLowerCase()
    ? null
    : translatedSubphaseLabel;
  const phaseTitle = subphaseLabel ? `${phaseLabel} · ${subphaseLabel}` : phaseLabel;
  const showPhaseRow = isUpdating || Boolean(display.phaseId || current.phase);
  const stateDescription = isUpdating
    ? phaseLabel
    : serviceStateText(t, serviceInfo, currentProcessState);
  const barProgress = display.barProgress;
  const barIsDeterminate = barProgress?.kind === 'exact'
    && barProgress.percent != null;
  const showProgressBar = isUpdating
    && barProgress?.kind !== 'not_applicable';
  const progressText = barIsDeterminate
    ? t('settings.serviceInfo.progressPercent', {
      percent: barProgress.percent?.toFixed(1),
    })
    : t('settings.serviceInfo.progressIndeterminate');
  const barUnits = barUnitsText(t, barProgress);
  const progressAriaText = [
    phaseLabel,
    subphaseLabel,
    barIsDeterminate
      ? progressText
      : t('settings.serviceInfo.progressUnknownTotal'),
    barUnits,
  ].filter(Boolean).join('. ');
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
              description={stateDescription}
              descriptionTestId="settings-service-info-row-update-sub-status"
              trailing={<ProcessStateDisplay state={currentProcessState} />}
            />

            {showPhaseRow ? (
              <ServiceInfoRow
                label={phaseTitle}
                testId="settings-service-info-row-update-step-position"
              >
                {showProgressBar ? (
                  <div className={styles.progressBlock}>
                    <div
                      className={`${styles.progressTrack} ${barIsDeterminate ? '' : styles.progressIndeterminate}`}
                      role="progressbar"
                      aria-label={t('settings.serviceInfo.phaseProgressAria')}
                      aria-valuemin={barIsDeterminate ? 0 : undefined}
                      aria-valuemax={barIsDeterminate ? 100 : undefined}
                      aria-valuenow={barIsDeterminate ? barProgress.percent ?? undefined : undefined}
                      aria-valuetext={progressAriaText}
                      data-testid="settings-service-phase-progress"
                      data-progress-kind={barIsDeterminate ? 'determinate' : 'indeterminate'}
                    >
                      {barIsDeterminate ? (
                        <div
                          className={styles.progressFill}
                          style={{ width: `${barProgress.percent}%` }}
                        />
                      ) : null}
                    </div>
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
