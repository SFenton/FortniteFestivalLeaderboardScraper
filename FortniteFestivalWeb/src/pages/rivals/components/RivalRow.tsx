import { memo } from 'react';
import { useTranslation } from 'react-i18next';
import type { RivalSummary } from '@festival/core/api/serverTypes';
import s from './RivalRow.module.css';

interface RivalRowProps {
  rival: RivalSummary;
  /** "above" = rival is ahead of you; "below" = you are ahead */
  direction: 'above' | 'below';
  onClick: () => void;
  style?: React.CSSProperties;
  onAnimationEnd?: (e: React.AnimationEvent<HTMLElement>) => void;
}

const RivalRow = memo(function RivalRow({ rival, direction, onClick, style, onAnimationEnd }: RivalRowProps) {
  const { t } = useTranslation();
  const name = rival.displayName ?? 'Unknown Player';

  const tintClass = direction === 'below' ? s.rowWinning : s.rowLosing;

  return (
    <div
      className={`${s.row} ${tintClass}`}
      onClick={onClick}
      role="button"
      tabIndex={0}
      onKeyDown={e => { if (e.key === 'Enter') onClick(); }}
      style={style}
      onAnimationEnd={onAnimationEnd}
    >
      <div className={s.content}>
        <span className={s.name}>{name}</span>
        <span className={s.shared}>{t('rivals.sharedSongs', { count: rival.sharedSongCount })}</span>
        <div className={s.pillRow}>
          <span className={s.pillAhead}>{rival.behindCount} {t('rivals.songsAhead', 'songs ahead')}</span>
          <span className={s.pillBehind}>{rival.aheadCount} {t('rivals.songsBehind', 'songs behind')}</span>
        </div>
      </div>
    </div>
  );
});

export default RivalRow;
