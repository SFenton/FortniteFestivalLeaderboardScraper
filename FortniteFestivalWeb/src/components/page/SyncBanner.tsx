/* eslint-disable react/forbid-dom-props -- dynamic styles require inline style prop */
/**
 * Sync progress banner displayed on PlayerPage when backfill/history reconstruction is running.
 */
import { memo, useMemo, type CSSProperties } from 'react';
import { useTranslation } from 'react-i18next';
import { Colors, Font, Weight, Gap, Radius, Layout, Overflow, Display, Align, CssValue, TRANSITION_MS, frostedCard, flexColumn, flexRow, transition } from '@festival/theme';
import { CssProp } from '@festival/theme';
import type { SyncPhase } from '../../hooks/data/useSyncStatus';
import ArcSpinner, { SpinnerSize } from '../common/ArcSpinner';

interface SyncBannerProps {
  displayName: string;
  phase: SyncPhase;
  backfillProgress: number;
  historyProgress: number;
}

const SyncBanner = memo(function SyncBanner({ displayName, phase, backfillProgress, historyProgress }: SyncBannerProps) {
  const { t } = useTranslation();
  const s = useSyncBannerStyles();

  return (
    <div style={s.syncBanner}>
      <div style={s.syncHeader}>
        <ArcSpinner size={SpinnerSize.SM} style={s.spinnerIcon} />
        <span style={s.syncTitle}>
          {phase === 'backfill'
            ? t('player.syncingScores')
            : t('player.buildingHistory')}
        </span>
      </div>
        {phase === 'backfill' && backfillProgress > 0 && (
          <div>
            <div style={s.syncProgressLabel}>{t('player.syncingScores')}</div>
            <div style={s.syncProgressBar}>
              <div style={{ ...s.syncProgressInner, width: `${Math.round(backfillProgress * 100)}%` }} />
            </div>
          </div>
        )}
        {phase === 'history' && (
          <>
            <div>
              <div style={s.syncProgressLabel}>{t('player.syncingScores')}</div>
              <div style={s.syncProgressBar}>
                <div style={{ ...s.syncProgressInner, width: CssValue.full }} />
              </div>
            </div>
            {historyProgress > 0 && (
              <div>
                <div style={s.syncProgressLabel}>{t('player.buildingHistory')}</div>
                <div style={s.syncProgressBar}>
                  <div style={{ ...s.syncProgressInner, width: `${Math.round(historyProgress * 100)}%` }} />
                </div>
              </div>
            )}
          </>
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
      marginBottom: Gap.lg,
    } as CSSProperties,
    syncTitle: {
      fontSize: Font.lg,
      fontWeight: Weight.bold,
      color: Colors.textPrimary,
    } as CSSProperties,
    spinnerIcon: {
      flexShrink: 0,
    } as CSSProperties,
    syncProgressLabel: {
      fontSize: Font.md,
      color: Colors.textPrimary,
      marginBottom: Gap.sm,
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
  }), []);
}
