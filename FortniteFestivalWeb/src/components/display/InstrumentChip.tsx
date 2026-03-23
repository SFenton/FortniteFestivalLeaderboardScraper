/**
 * Single instrument status chip — circle with fill/stroke based on score + FC status.
 * Shared between SongIconsDemo (first-run) and production SongRow.
 */
import { memo } from 'react';
import type { ServerInstrumentKey } from '@festival/core/api/serverTypes';
import { InstrumentIcon } from './InstrumentIcons';
import css from './InstrumentChip.module.css';

export interface InstrumentChipProps {
  instrument: ServerInstrumentKey;
  hasScore: boolean;
  isFC: boolean;
  size?: number;
}

export const InstrumentChip = memo(function InstrumentChip({ instrument, hasScore, isFC, size = 24 }: InstrumentChipProps) {
  const status = isFC ? 'fc' : hasScore ? 'scored' : 'none';
  return (
    <div className={css.chip} data-status={status}>
      <InstrumentIcon instrument={instrument} size={size} />
    </div>
  );
});
