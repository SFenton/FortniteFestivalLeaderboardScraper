/* eslint-disable react/forbid-dom-props -- useStyles pattern */
import { memo, useMemo } from 'react';
import type { ServerInstrumentKey } from '@festival/core/api/serverTypes';
import { InstrumentSize, Gap, Border, CssValue, Display, Align, Justify, BorderStyle } from '@festival/theme';
import { InstrumentIcon, getInstrumentStatusVisual } from './InstrumentIcons';

export interface InstrumentChipProps {
  instrument: ServerInstrumentKey;
  hasScore: boolean;
  isFC: boolean;
  size?: number;
}

export const InstrumentChip = memo(function InstrumentChip({ instrument, hasScore, isFC, size = 24 }: InstrumentChipProps) {
  const visual = getInstrumentStatusVisual(hasScore, isFC);
  const s = useStyles(visual.fill, visual.stroke);
  return (
    <div style={s.chip}>
      <InstrumentIcon instrument={instrument} size={size} />
    </div>
  );
});

function useStyles(fill: string, stroke: string) {
  return useMemo(() => ({
    chip: {
      width: InstrumentSize.chip,
      height: InstrumentSize.chip,
      borderRadius: CssValue.circle,
      borderWidth: Gap.xs,
      borderStyle: BorderStyle.solid,
      borderColor: stroke,
      backgroundColor: fill,
      display: Display.flex,
      alignItems: Align.center,
      justifyContent: Justify.center,
    },
  }), [fill, stroke]);
}
