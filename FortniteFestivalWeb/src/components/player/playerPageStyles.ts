import type { CSSProperties } from 'react';
import { MaxWidth, Layout, Gap, BoxSizing, CssValue, Overflow, Display } from '@festival/theme';
import { padding } from '@festival/theme';

export const playerPageStyles = {
  scrollArea: { overflowX: Overflow.hidden } as CSSProperties,
  container: {
    maxWidth: MaxWidth.card,
    margin: CssValue.marginCenter,
    padding: padding(Layout.paddingTop, Layout.paddingHorizontal),
    boxSizing: BoxSizing.borderBox,
    width: CssValue.full,
  } as CSSProperties,
  gridList: {
    display: Display.grid,
    gridTemplateColumns: 'repeat(2, minmax(0, 1fr))',
    gap: Gap.md,
    minWidth: 0,
    overflow: Overflow.hidden,
  } as CSSProperties,
  gridFullWidth: { gridColumn: '1 / -1' } as CSSProperties,
} as const;
