/**
 * Sync progress banner displayed on PlayerPage when backfill/history reconstruction is running.
 */
import { memo } from 'react';
import { useTranslation } from 'react-i18next';
import type { SyncPhase } from '../../hooks/useSyncStatus';
import { Gap } from '@festival/theme';
import s from '../../pages/PlayerPage.module.css';

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
      <div className={s.syncSpinner} />
      <div style={{ flex: 1 }}>
        <div className={s.syncTitle}>
          {phase === 'backfill' ? t('player.syncInProgress') : t('player.syncInProgress')}
        </div>
        <div className={s.syncSubtitle}>
          {phase === 'backfill'
            ? `Syncing ${displayName}'s scores…`
            : `Reconstructing ${displayName}'s score history across seasons…`}
        </div>
        {phase === 'backfill' && backfillProgress > 0 && (
          <div style={{ marginTop: Gap.md }}>
            <div className={s.syncProgressLabel}>
              <span>{t('player.syncingScores')}</span>
              <span>{(backfillProgress * 100).toFixed(1)}%</span>
            </div>
            <div className={s.syncProgressOuter}>
              <div className={s.syncProgressInner} style={{ width: `${Math.round(backfillProgress * 100)}%` }} />
            </div>
          </div>
        )}
        {phase === 'history' && (
          <>
            <div style={{ marginTop: Gap.md }}>
              <div className={s.syncProgressLabel}>
                <span>{t('player.syncingScores')}</span><span>100.0%</span>
              </div>
              <div className={s.syncProgressOuter}>
                <div className={s.syncProgressInner} style={{ width: '100%' }} />
              </div>
            </div>
            {historyProgress > 0 && (
              <div style={{ marginTop: Gap.sm }}>
                <div className={s.syncProgressLabel}>
                  <span>{t('player.buildingHistory')}</span>
                  <span>{(historyProgress * 100).toFixed(1)}%</span>
                </div>
                <div className={s.syncProgressOuter}>
                  <div className={s.syncProgressInner} style={{ width: `${Math.round(historyProgress * 100)}%` }} />
                </div>
              </div>
            )}
          </>
        )}
      </div>
    </div>
  );
});

export default SyncBanner;
