import { memo } from 'react';
import { useTranslation } from 'react-i18next';
import { IoArrowUp, IoArrowDown } from 'react-icons/io5';
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

function formatDelta(delta: number): string {
  const sign = delta >= 0 ? '+' : '';
  return `${sign}${delta.toFixed(1)}`;
}

const RivalRow = memo(function RivalRow({ rival, direction, onClick, style, onAnimationEnd }: RivalRowProps) {
  const { t } = useTranslation();
  const name = rival.displayName ?? 'Unknown Player';

  return (
    <div
      className={s.row}
      onClick={onClick}
      role="button"
      tabIndex={0}
      onKeyDown={e => { if (e.key === 'Enter') onClick(); }}
      style={style}
      onAnimationEnd={onAnimationEnd}
    >
      <span className={direction === 'above' ? s.dirAbove : s.dirBelow}>
        {direction === 'above' ? <IoArrowDown size={18} /> : <IoArrowUp size={18} />}
      </span>
      <div className={s.info}>
        <div className={s.name}>{name}</div>
        <div className={s.meta}>
          <span>{t('rivals.sharedSongs', { count: rival.sharedSongCount })}</span>
          <span>{t('rivals.ahead', { count: rival.aheadCount })}</span>
          <span>{t('rivals.behind', { count: rival.behindCount })}</span>
        </div>
      </div>
      <div className={rival.avgSignedDelta < 0 ? s.deltaPositive : s.deltaNegative}>
        {t('rivals.avgDelta', { delta: formatDelta(rival.avgSignedDelta) })}
      </div>
    </div>
  );
});

export default RivalRow;
