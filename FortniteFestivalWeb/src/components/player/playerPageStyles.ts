import type { CSSProperties } from 'react';
import { Gap, Overflow, Display } from '@festival/theme';

export const playerPageStyles = {
  scrollArea: { overflowX: Overflow.hidden } as CSSProperties,
  gridList: {
    display: Display.grid,
    gridTemplateColumns: 'repeat(2, minmax(0, 1fr))',
    gap: Gap.md,
    minWidth: 0,
    overflow: Overflow.hidden,
  } as CSSProperties,
  gridFullWidth: { gridColumn: '1 / -1' } as CSSProperties,
} as const;
