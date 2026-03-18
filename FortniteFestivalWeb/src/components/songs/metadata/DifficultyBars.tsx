import { memo } from 'react';

interface DifficultyBarsProps {
  /** Number of filled bars (1-7). Pass the raw difficulty number from the song model — it will be clamped and truncated. */
  level: number;
  /** When true, interpret `level` as a raw difficulty value (0-6 scale, add 1 for display). Default false. */
  raw?: boolean;
}

/**
 * SVG parallelogram difficulty bars matching the mobile DifficultyBars component.
 * Displays 7 bars with `level` of them filled.
 */
const DifficultyBars = memo(function DifficultyBars({ level, raw }: DifficultyBarsProps) {
  const display = raw
    ? Math.max(0, Math.min(6, Math.trunc(level))) + 1
    : Math.max(1, Math.min(7, level));
  const barW = 8;
  const barH = 20;
  const offset = Math.min(Math.max(Math.round(barW * 0.26), 1), Math.floor(barW * 0.45));
  return (
    <svg
      width={barW * 7 + 6}
      height={barH}
      style={{ display: 'inline-block', verticalAlign: 'middle' }}
      aria-label={`Difficulty ${display} of 7`}
    >
      {Array.from({ length: 7 }).map((_, i) => {
        const filled = i + 1 <= display;
        const x = i * (barW + 1);
        return (
          <polygon
            key={i}
            points={`${x + offset},0 ${x + barW},0 ${x + barW - offset},${barH} ${x},${barH}`}
            fill={filled ? '#FFFFFF' : '#666666'}
          />
        );
      })}
    </svg>
  );
});

export default DifficultyBars;
