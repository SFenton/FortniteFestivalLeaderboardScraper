/* eslint-disable react/forbid-dom-props -- dynamic styles require inline style prop */
/**
 * Sync progress banner displayed on PlayerPage when backfill/history reconstruction is running.
 */
import { memo } from 'react';
import { useTranslation } from 'react-i18next';
import type { SyncPhase } from '../../hooks/data/useSyncStatus';
import ArcSpinner from '../common/ArcSpinner';
import s from './SyncBanner.module.css';

interface SyncBannerProps {
  displayName: string;
  phase: SyncPhase;
  backfillProgress: number;
  historyProgress: number;
}

const SyncBanner = memo(function SyncBanner({ displayName, phase, backfillProgress, historyProgress }: SyncBannerProps) {
  const { t } = useTranslation();

  return (
    <div className={s.syncBanner}>
      <div className={s.syncHeader}>
        <ArcSpinner size="sm" className={s.syncSpinner} />
        <span className={s.syncTitle}>
          {phase === 'backfill'
            ? `Syncing ${displayName}'s scores…`
            : `Reconstructing ${displayName}'s history…`}
        </span>
      </div>
        {phase === 'backfill' && backfillProgress > 0 && (
          <div>
            <div className={s.syncProgressLabel}>{t('player.syncingScores')}</div>
            <div className={s.syncProgressBar}>
              <div className={s.syncProgressInner} style={{ width: `${Math.round(backfillProgress * 100)}%` }} />
            </div>
          </div>
        )}
        {phase === 'history' && (
          <>
            <div>
              <div className={s.syncProgressLabel}>{t('player.syncingScores')}</div>
              <div className={s.syncProgressBar}>
                <div className={s.syncProgressInner} style={{ width: '100%' }} />
              </div>
            </div>
            {historyProgress > 0 && (
              <div>
                <div className={s.syncProgressLabel}>{t('player.buildingHistory')}</div>
                <div className={s.syncProgressBar}>
                  <div className={s.syncProgressInner} style={{ width: `${Math.round(historyProgress * 100)}%` }} />
                </div>
              </div>
            )}
          </>
        )}
    </div>
  );
});

export default SyncBanner;
