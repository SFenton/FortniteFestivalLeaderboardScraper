import { memo, type CSSProperties } from 'react';
import css from './ScorePill.module.css';

interface ScorePillProps {
  score: number;
  /** Fixed or dynamic width (e.g. "78px", "6ch"). Omit for auto. */
  width?: string;
  /** Use bold weight (SongRow style) vs semi-bold (leaderboard style). */
  bold?: boolean;
  className?: string;
}

const ScorePill = memo(function ScorePill({ score, width, bold, className }: ScorePillProps) {
  const cls = bold
    ? (className ? `${css.bold} ${className}` : css.bold)
    : (className ? `${css.base} ${className}` : css.base);
  const style: CSSProperties | undefined = width ? { width } : undefined;
  return <span className={cls} style={style}>{score.toLocaleString()}</span>;
});

export default ScorePill;
