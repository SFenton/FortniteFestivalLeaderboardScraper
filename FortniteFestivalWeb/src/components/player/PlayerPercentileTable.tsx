/* eslint-disable react/forbid-dom-props -- dynamic styles require inline style prop */
import { memo } from 'react';
import PercentilePill from '../songs/metadata/PercentilePill';
import s from './PlayerPercentileTable.module.css';

export const PlayerPercentileHeader = memo(function PlayerPercentileHeader({
  percentileLabel,
  songsLabel,
}: {
  percentileLabel: string;
  songsLabel: string;
}) {
  return (
    <div className={s.pctRowHeader}>
      <span className={s.pctHeaderText}>{percentileLabel}</span>
      <span className={s.pctHeaderText} style={{ textAlign: 'right' }}>{songsLabel}</span>
    </div>
  );
});

export const PlayerPercentileRow = memo(function PlayerPercentileRow({
  pct,
  count,
  isLast,
  onClick,
}: {
  pct: number;
  count: number;
  isLast: boolean;
  onClick: () => void;
}) {
  return (
    <div
      className={s.pctRowItem}
      style={isLast ? { borderBottom: 'none' } : undefined}
      onClick={onClick}
    >
      <span>
        <PercentilePill
          display={`Top ${pct}%`}
        />
      </span>
      <span style={{ fontWeight: 600 }}>{count}</span>
    </div>
  );
});
