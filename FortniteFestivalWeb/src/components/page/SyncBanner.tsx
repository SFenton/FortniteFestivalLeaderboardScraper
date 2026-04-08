/* eslint-disable react/forbid-dom-props -- dynamic styles require inline style prop */
/**
 * Sync progress banner displayed on PlayerPage when backfill/history/rivals sync is running.
 * Shows a unified progress bar, step indicator, numeric counts, and current song name.
 */
import { memo, useMemo, type CSSProperties } from 'react';
import { useTranslation } from 'react-i18next';
import { Colors, Font, Weight, Gap, Radius, Layout, Overflow, TRANSITION_MS, frostedCard, flexColumn, flexRow, transition } from '@festival/theme';
import { CssProp } from '@festival/theme';
import type { SyncPhase } from '../../hooks/data/useSyncStatus';
import ArcSpinner, { SpinnerSize } from '../common/ArcSpinner';

interface SyncBannerProps {
  phase: SyncPhase;
  backfillProgress: number;
  historyProgress: number;
  rivalsProgress: number;
  itemsCompleted: number;
  totalItems: number;
  entriesFound: number;
  currentSongName: string | null;
  seasonsQueried: number;
  rivalsFound: number;
  isThrottled: boolean;
  throttleStatusKey: string | null;
}

function getStepInfo(phase: SyncPhase): { step: number; totalSteps: number } {
  switch (phase) {
    case 'backfill': return { step: 1, totalSteps: 3 };
    case 'history': return { step: 2, totalSteps: 3 };
    case 'rivals': return { step: 3, totalSteps: 3 };
    case 'postscrape': return { step: 0, totalSteps: 0 };
    default: return { step: 0, totalSteps: 3 };
  }
}

function getUnifiedProgress(phase: SyncPhase, bf: number, hr: number, rv: number): number {
  switch (phase) {
    case 'backfill': return bf * (1 / 3);
    case 'history': return (1 / 3) + hr * (1 / 3);
    case 'rivals': return (2 / 3) + rv * (1 / 3);
    case 'complete': return 1;
    default: return 0;
  }
}

const SyncBanner = memo(function SyncBanner({
  phase, backfillProgress, historyProgress, rivalsProgress,
  itemsCompleted, totalItems, entriesFound, currentSongName,
  seasonsQueried, rivalsFound, isThrottled, throttleStatusKey,
}: SyncBannerProps) {
  const { t } = useTranslation();
  const s = useSyncBannerStyles();
  const { step, totalSteps } = getStepInfo(phase);
  const isPostScrape = phase === 'postscrape';
  const unified = isPostScrape
    ? (totalItems > 0 ? itemsCompleted / totalItems : 0)
    : getUnifiedProgress(phase, backfillProgress, historyProgress, rivalsProgress);
  const pct = Math.round(unified * 100);
  const isIndeterminate = (itemsCompleted === 0 && totalItems === 0) || isThrottled;

  return (
    <div style={s.syncBanner}>
      <div style={s.syncHeader}>
        <ArcSpinner size={SpinnerSize.SM} style={s.spinnerIcon} />
        <div style={s.syncHeaderText}>
          <span style={s.syncTitle}>
            {t(`player.syncStep_${phase}` as const)}
          </span>
          {step > 0 && (
            <span style={s.syncStep}>
              {t('player.syncStepOf', { step, totalSteps })}
            </span>
          )}
        </div>
      </div>

      {/* Unified progress bar */}
      <div style={s.syncProgressBar}>
        <div style={{
          ...s.syncProgressInner,
          ...(isThrottled ? s.syncProgressThrottled : isIndeterminate ? s.syncProgressIndeterminate : { width: `${pct}%` }),
        }} />
      </div>

      {/* Throttle warning */}
      {isThrottled && throttleStatusKey && (
        <div style={s.syncThrottleWarning}>
          {t(`player.${throttleStatusKey}` as const)}
        </div>
      )}

      {/* Counts row */}
      <div style={s.syncCounts}>
        {totalItems > 0 && (
          <span>{itemsCompleted.toLocaleString()} / {totalItems.toLocaleString()}</span>
        )}
        {phase === 'backfill' && entriesFound > 0 && (
          <span>{t('player.syncNewScores', { count: entriesFound })}</span>
        )}
        {phase === 'history' && (
          <>
            {seasonsQueried > 0 && <span>{t('player.syncSeasons', { count: seasonsQueried })}</span>}
            {entriesFound > 0 && <span>{t('player.syncEntriesFound', { count: entriesFound })}</span>}
          </>
        )}
        {phase === 'rivals' && rivalsFound > 0 && (
          <span>{t('player.syncRivalsFound', { count: rivalsFound })}</span>
        )}
        {phase === 'postscrape' && entriesFound > 0 && (
          <span>{t('player.syncNewScores', { count: entriesFound })}</span>
        )}
      </div>

      {/* Current song */}
      {currentSongName && (
        <div style={s.syncCurrentSong}>
          {currentSongName}
        </div>
      )}
    </div>
  );
});

export default SyncBanner;

function useSyncBannerStyles() {
  return useMemo(() => ({
    syncBanner: {
      ...frostedCard,
      ...flexColumn,
      gap: Gap.md,
      padding: Gap.xl,
      borderRadius: Radius.md,
      marginBottom: Gap.md,
    } as CSSProperties,
    syncHeader: {
      ...flexRow,
      gap: Gap.xl,
      alignItems: 'center',
    } as CSSProperties,
    syncHeaderText: {
      ...flexRow,
      gap: Gap.md,
      alignItems: 'baseline',
      flex: 1,
    } as CSSProperties,
    syncTitle: {
      fontSize: Font.lg,
      fontWeight: Weight.bold,
      color: Colors.textPrimary,
    } as CSSProperties,
    syncStep: {
      fontSize: Font.sm,
      color: Colors.textTertiary,
    } as CSSProperties,
    spinnerIcon: {
      flexShrink: 0,
    } as CSSProperties,
    syncProgressBar: {
      flex: 1,
      height: Layout.progressBarHeight,
      background: Colors.borderPrimary,
      borderRadius: Radius.progressBar,
      overflow: Overflow.hidden,
    } as CSSProperties,
    syncProgressInner: {
      height: '100%',
      background: Colors.accentBlue,
      borderRadius: Radius.progressBar,
      transition: transition(CssProp.width, TRANSITION_MS),
    } as CSSProperties,
    syncProgressIndeterminate: {
      width: '30%',
      animation: 'indeterminate-bar 1.5s ease-in-out infinite',
    } as CSSProperties,
    syncProgressThrottled: {
      width: '100%',
      background: Colors.statusAmber,
      opacity: 0.5,
      animation: 'indeterminate-bar 2s ease-in-out infinite',
    } as CSSProperties,
    syncCounts: {
      ...flexRow,
      gap: Gap.lg,
      fontSize: Font.sm,
      color: Colors.textSecondary,
      flexWrap: 'wrap' as const,
    } as CSSProperties,
    syncCurrentSong: {
      fontSize: Font.sm,
      color: Colors.textTertiary,
      fontStyle: 'italic' as const,
      overflow: 'hidden' as const,
      textOverflow: 'ellipsis',
      whiteSpace: 'nowrap' as const,
    } as CSSProperties,
    syncThrottleWarning: {
      fontSize: Font.sm,
      color: Colors.statusAmber,
      fontWeight: Weight.semibold,
    } as CSSProperties,
  }), []);
}
